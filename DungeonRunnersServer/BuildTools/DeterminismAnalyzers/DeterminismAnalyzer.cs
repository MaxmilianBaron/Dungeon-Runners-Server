using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DungeonRunners.DeterminismAnalyzers
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class DeterminismAnalyzer : DiagnosticAnalyzer
    {
        private sealed class AnalysisState
        {
            public readonly ImmutableHashSet<string> Baseline;
            public readonly bool IgnoreBaseline;
            public readonly ConcurrentDictionary<SyntaxTree, bool> SimTrees = new ConcurrentDictionary<SyntaxTree, bool>();
            public readonly ConcurrentDictionary<(SyntaxTree Tree, int SpanStart, int SpanLength, int RawKind), Lazy<ImmutableDictionary<string, ImmutableArray<int>>>> OccurrenceIndices = new ConcurrentDictionary<(SyntaxTree, int, int, int), Lazy<ImmutableDictionary<string, ImmutableArray<int>>>>();

            public AnalysisState(ImmutableHashSet<string> baseline, bool ignoreBaseline)
            {
                Baseline = baseline;
                IgnoreBaseline = ignoreBaseline;
            }
        }

        public const string FloatingPointId = "DR0001";
        public const string TranscendentalId = "DR0002";
        public const string RandomId = "DR0003";
        public const string WallClockId = "DR0004";
        public const string UnorderedIterationId = "DR0005";
        public const string RuntimeNondeterminismId = "DR0006";
        public const string FloatingIngressId = "DR0007";

        private static readonly DiagnosticDescriptor FloatingPoint = Rule(
            FloatingPointId,
            "Floating point in deterministic simulation",
            "'{0}' is not allowed in a validated/shared simulation path; use the native integer/fixed-point formula");

        private static readonly DiagnosticDescriptor Transcendental = Rule(
            TranscendentalId,
            "Non-deterministic math in deterministic simulation",
            "'{0}' is not allowed in a validated/shared simulation path without explicit native evidence");

        private static readonly DiagnosticDescriptor Random = Rule(
            RandomId,
            "Ambient random source in deterministic simulation",
            "'{0}' bypasses the ordered room RNG ledger; consume the injected room RNG at the native update point");

        private static readonly DiagnosticDescriptor WallClock = Rule(
            WallClockId,
            "Wall-clock scheduling in deterministic simulation",
            "'{0}' is not a simulation tick; use the common fixed SimulationClock");

        private static readonly DiagnosticDescriptor UnorderedIteration = Rule(
            UnorderedIterationId,
            "Unproven collection iteration order",
            "Iteration over '{0}' is not a native-proven deterministic entity/update order");

        private static readonly DiagnosticDescriptor RuntimeNondeterminism = Rule(
            RuntimeNondeterminismId,
            "Runtime identity or concurrency in deterministic simulation",
            "'{0}' depends on runtime identity, scheduling, or process-local state");

        private static readonly DiagnosticDescriptor FloatingIngress = Rule(
            FloatingIngressId,
            "Floating-point ingress into deterministic simulation",
            "'{0}' imports a floating-point value from outside the validated/shared simulation boundary");

        private static readonly string[] SimPathFragments =
        {
            "/Source/Server/Systems/Combat/",
            "/Source/Server/Systems/Movement/",
            "/Source/Server/Systems/Units/",
            "/Source/Server/Infrastructure/Networking/Game/",
            "/Source/Server/Infrastructure/Networking/Replication/",
            "/Source/Server/Systems/World/",
            "/Source/Server/Systems/Companions/",
            "/Source/Server/Systems/Skills/",
        };

        private static readonly string[] SimExactSuffixes =
        {
            "/Source/Server/Core/Determinism/MersenneTwister.cs",
            "/Source/Server/Core/Simulation/SimulationClock.cs",
            "/Source/Server/Systems/Entities/PlayerState.cs",
            "/Source/Server/Infrastructure/Networking/MessageQueue.cs",
            "/Source/Server/Infrastructure/Networking/RRConnection.cs",
            "/Source/Server/Systems/Items/ItemStatDatabase.cs",
            "/Source/Server/Systems/Items/GCObjectGeneratorTable.cs",
        };

        private static readonly HashSet<string> ForbiddenMathMembers = new HashSet<string>(StringComparer.Ordinal)
        {
            "System.Math.Round",
            "System.Math.Sqrt",
            "System.Math.Sin",
            "System.Math.Cos",
            "System.Math.Tan",
            "System.Math.Asin",
            "System.Math.Acos",
            "System.Math.Atan",
            "System.Math.Atan2",
            "System.Math.Pow",
            "System.Math.Exp",
            "System.Math.Log",
            "System.Math.Log10",
            "System.MathF.Round",
            "System.MathF.Sqrt",
            "System.MathF.Sin",
            "System.MathF.Cos",
            "System.MathF.Tan",
            "System.MathF.Asin",
            "System.MathF.Acos",
            "System.MathF.Atan",
            "System.MathF.Atan2",
            "System.MathF.Pow",
            "System.MathF.Exp",
            "System.MathF.Log",
            "System.MathF.Log10",
        };

        private static readonly HashSet<string> WallClockMembers = new HashSet<string>(StringComparer.Ordinal)
        {
            "System.DateTime.Now",
            "System.DateTime.UtcNow",
            "System.DateTimeOffset.Now",
            "System.DateTimeOffset.UtcNow",
            "System.Environment.TickCount",
            "System.Environment.TickCount64",
            "System.Threading.Thread.Sleep",
            "System.Threading.Tasks.Task.Delay",
            "UnityEngine.Time.time",
            "UnityEngine.Time.realtimeSinceStartup",
            "UnityEngine.MonoBehaviour.StartCoroutine",
        };

        private static readonly HashSet<string> RuntimeNondeterministicMembers = new HashSet<string>(StringComparer.Ordinal)
        {
            "System.Guid.NewGuid",
            "System.Object.GetHashCode",
            "System.String.GetHashCode",
            "System.HashCode.Combine",
            "System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode",
            "System.Threading.Tasks.Task.Run",
            "System.Threading.Tasks.Parallel.For",
            "System.Threading.Tasks.Parallel.ForEach",
            "System.Threading.Tasks.Parallel.Invoke",
            "System.Linq.ParallelEnumerable.AsParallel",
        };

        private static readonly HashSet<string> PotentialWallClockMemberNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Now",
            "UtcNow",
            "TickCount",
            "TickCount64",
            "Sleep",
            "Delay",
            "time",
            "realtimeSinceStartup",
            "StartCoroutine",
        };

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(
            FloatingPoint,
            Transcendental,
            Random,
            WallClock,
            UnorderedIteration,
            RuntimeNondeterminism,
            FloatingIngress);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(start =>
            {
                var state = new AnalysisState(
                    LoadBaseline(start.Options.AdditionalFiles),
                    GetGlobalBoolean(start.Options.AnalyzerConfigOptionsProvider, "build_property.DRDeterminismIgnoreBaseline"));

                start.RegisterSyntaxNodeAction(node => AnalyzePredefinedType(node, state), SyntaxKind.PredefinedType);
                start.RegisterSyntaxNodeAction(node => AnalyzeInvocation(node, state), SyntaxKind.InvocationExpression);
                start.RegisterSyntaxNodeAction(node => AnalyzeObjectCreation(node, state), SyntaxKind.ObjectCreationExpression);
                start.RegisterSyntaxNodeAction(node => AnalyzeMemberAccess(node, state), SyntaxKind.SimpleMemberAccessExpression);
                start.RegisterSyntaxNodeAction(node => AnalyzeForEach(node, state), SyntaxKind.ForEachStatement);
                start.RegisterSyntaxNodeAction(node => AnalyzeIdentifier(node, state), SyntaxKind.IdentifierName);
            });
        }

        private static void AnalyzePredefinedType(SyntaxNodeAnalysisContext context, AnalysisState state)
        {
            if (!IsSimNode(context.Node, state))
                return;
            var type = (PredefinedTypeSyntax)context.Node;
            if (!type.Keyword.IsKind(SyntaxKind.FloatKeyword) && !type.Keyword.IsKind(SyntaxKind.DoubleKeyword))
                return;
            Report(context, FloatingPoint, type.GetLocation(), type.Keyword.ValueText, state);
        }

        private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context, AnalysisState state)
        {
            if (!IsSimNode(context.Node, state))
                return;
            var invocation = (InvocationExpressionSyntax)context.Node;
            var symbol = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol as IMethodSymbol;
            if (symbol == null)
                return;
            string member = FullMemberName(symbol);
            if (ForbiddenMathMembers.Contains(member))
                Report(context, Transcendental, invocation.GetLocation(), member, state);
            if (WallClockMembers.Contains(member) || symbol.Name == "WaitForSeconds" || symbol.Name == "StartCoroutine")
                Report(context, WallClock, invocation.GetLocation(), member, state);
            if (IsSystemRandom(symbol.ContainingType))
                Report(context, Random, invocation.GetLocation(), member, state);
            if (RuntimeNondeterministicMembers.Contains(member))
                Report(context, RuntimeNondeterminism, invocation.GetLocation(), member, state);
            if (IsFloating(symbol.ReturnType) && !ForbiddenMathMembers.Contains(member) && !IsSimSymbol(symbol))
                Report(context, FloatingIngress, invocation.GetLocation(), member, state);
        }

        private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext context, AnalysisState state)
        {
            if (!IsSimNode(context.Node, state))
                return;
            var creation = (ObjectCreationExpressionSyntax)context.Node;
            var type = context.SemanticModel.GetTypeInfo(creation, context.CancellationToken).Type;
            if (IsSystemRandom(type))
                Report(context, Random, creation.GetLocation(), type!.ToDisplayString(), state);
            if (type?.Name == "WaitForSeconds")
                Report(context, WallClock, creation.GetLocation(), type.ToDisplayString(), state);
            if (type?.ToDisplayString() == "System.Threading.Thread" ||
                type?.ToDisplayString() == "System.Threading.Timer" ||
                type?.ToDisplayString() == "System.Timers.Timer")
                Report(context, RuntimeNondeterminism, creation.GetLocation(), type.ToDisplayString(), state);
        }

        private static void AnalyzeMemberAccess(SyntaxNodeAnalysisContext context, AnalysisState state)
        {
            if (!IsSimNode(context.Node, state))
                return;
            var access = (MemberAccessExpressionSyntax)context.Node;
            if (!PotentialWallClockMemberNames.Contains(access.Name.Identifier.ValueText))
                return;
            var symbol = context.SemanticModel.GetSymbolInfo(access, context.CancellationToken).Symbol;
            if (symbol == null)
                return;
            string member = symbol.ContainingType == null
                ? symbol.ToDisplayString()
                : symbol.ContainingType.ToDisplayString() + "." + symbol.Name;
            if (WallClockMembers.Contains(member))
                Report(context, WallClock, access.GetLocation(), member, state);
        }

        private static void AnalyzeForEach(SyntaxNodeAnalysisContext context, AnalysisState state)
        {
            if (!IsSimNode(context.Node, state))
                return;
            var statement = (ForEachStatementSyntax)context.Node;
            var type = context.SemanticModel.GetTypeInfo(statement.Expression, context.CancellationToken).Type;
            if (type != null && IsUnorderedExpression(statement.Expression, context.SemanticModel, context.CancellationToken, 0))
            {
                Report(context, UnorderedIteration, statement.Expression.GetLocation(), type.ToDisplayString(), state);
            }
        }

        private static void AnalyzeIdentifier(SyntaxNodeAnalysisContext context, AnalysisState state)
        {
            if (!IsSimNode(context.Node, state))
                return;
            var identifier = (IdentifierNameSyntax)context.Node;
            var symbol = context.SemanticModel.GetSymbolInfo(identifier, context.CancellationToken).Symbol;
            ITypeSymbol? type = symbol switch
            {
                IFieldSymbol field => field.Type,
                IPropertySymbol property => property.Type,
                _ => null,
            };
            if (type != null && IsFloating(type) && symbol != null && !IsSimSymbol(symbol))
                Report(context, FloatingIngress, identifier.GetLocation(), symbol.ToDisplayString(), state);
        }

        private static void Report(
            SyntaxNodeAnalysisContext context,
            DiagnosticDescriptor descriptor,
            Location location,
            string display,
            AnalysisState state)
        {
            string fingerprint = Fingerprint(context, descriptor.Id, location, state);
            if (!state.IgnoreBaseline && state.Baseline.Contains(fingerprint))
                return;
            ISymbol? symbol = context.SemanticModel.GetEnclosingSymbol(location.SourceSpan.Start, context.CancellationToken);
            string symbolIdentity = symbol?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? string.Empty;
            var properties = ImmutableDictionary<string, string?>.Empty
                .Add("Fingerprint", fingerprint)
                .Add("Symbol", symbolIdentity);
            context.ReportDiagnostic(Diagnostic.Create(descriptor, location, properties, display));
        }

        private static DiagnosticDescriptor Rule(string id, string title, string message) => new DiagnosticDescriptor(
            id,
            title,
            message,
            "Determinism",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Dungeon Runners validated/shared simulation must reproduce the native integer operation, update order, and RNG draw order exactly.");

        private static bool IsSimNode(SyntaxNode node, AnalysisState state)
        {
            return state.SimTrees.GetOrAdd(node.SyntaxTree, IsSimTree);
        }

        private static bool IsSimTree(SyntaxTree tree)
        {
            string path = (tree.FilePath ?? string.Empty).Replace('\\', '/');
            if (path.Length == 0)
                return false;
            var root = tree.GetRoot();
            string prefix = root.GetLeadingTrivia().ToFullString();
            if (prefix.IndexOf("DR-DETERMINISM: presentation-only", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            return IsSimPath(path);
        }

        private static bool IsSimPath(string path) =>
            SimPathFragments.Any(path.Contains) || SimExactSuffixes.Any(path.EndsWith);

        private static bool IsSimSymbol(ISymbol symbol) =>
            symbol.Locations.Any(location => location.IsInSource && IsSimPath((location.SourceTree?.FilePath ?? string.Empty).Replace('\\', '/')));

        private static bool IsFloating(ITypeSymbol type) =>
            type.SpecialType == SpecialType.System_Single || type.SpecialType == SpecialType.System_Double;

        private static bool IsUnorderedExpression(ExpressionSyntax expression, SemanticModel model, System.Threading.CancellationToken cancellationToken, int depth)
        {
            if (depth > 6)
                return false;
            var type = model.GetTypeInfo(expression, cancellationToken).Type as INamedTypeSymbol;
            if (type != null)
            {
                string definition = type.OriginalDefinition.ToDisplayString();
                if (definition == "System.Collections.Generic.Dictionary<TKey, TValue>" ||
                    definition == "System.Collections.Generic.HashSet<T>" ||
                    definition == "System.Collections.Concurrent.ConcurrentDictionary<TKey, TValue>" ||
                    definition == "System.Collections.Concurrent.ConcurrentBag<T>")
                    return true;
            }
            if (expression is MemberAccessExpressionSyntax access)
                return IsUnorderedExpression(access.Expression, model, cancellationToken, depth + 1);
            if (expression is InvocationExpressionSyntax invocation)
            {
                if (invocation.Expression is MemberAccessExpressionSyntax member &&
                    IsUnorderedExpression(member.Expression, model, cancellationToken, depth + 1))
                    return true;
                var first = invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression;
                return first != null && IsUnorderedExpression(first, model, cancellationToken, depth + 1);
            }
            return false;
        }

        private static bool IsSystemRandom(ITypeSymbol? type) =>
            type?.ToDisplayString() == "System.Random";

        private static string FullMemberName(IMethodSymbol symbol) =>
            symbol.ContainingType.ToDisplayString() + "." + symbol.Name;

        private static ImmutableHashSet<string> LoadBaseline(ImmutableArray<AdditionalText> files)
        {
            var builder = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files.Where(file => file.Path.EndsWith("determinism-baseline.txt", StringComparison.OrdinalIgnoreCase)))
            {
                var text = file.GetText();
                if (text == null)
                    continue;
                foreach (var line in text.Lines)
                {
                    string value = line.ToString().Trim();
                    if (value.Length == 64 && value.All(Uri.IsHexDigit))
                        builder.Add(value);
                }
            }
            return builder.ToImmutable();
        }

        private static bool GetGlobalBoolean(AnalyzerConfigOptionsProvider options, string key) =>
            options.GlobalOptions.TryGetValue(key, out string? value) &&
            bool.TryParse(value, out bool parsed) && parsed;

        private static string Fingerprint(SyntaxNodeAnalysisContext context, string ruleId, Location location, AnalysisState state)
        {
            var span = location.GetLineSpan();
            string path = span.Path.Replace('\\', '/');
            int marker = path.IndexOf("/DungeonRunnersServer/", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0)
                path = path.Substring(marker + 1);
            SyntaxNode node = context.Node;
            SyntaxNode container = node.AncestorsAndSelf().FirstOrDefault(candidate => candidate is MemberDeclarationSyntax) ?? node.SyntaxTree.GetRoot();
            string normalized = node.WithoutTrivia().ToFullString();
            ImmutableDictionary<string, ImmutableArray<int>> index = state.OccurrenceIndices.GetOrAdd(
                (container.SyntaxTree, container.SpanStart, container.Span.Length, node.RawKind),
                _ => new Lazy<ImmutableDictionary<string, ImmutableArray<int>>>(
                    () => BuildOccurrenceIndex(container, node.RawKind),
                    LazyThreadSafetyMode.ExecutionAndPublication)).Value;
            int occurrence = FindOccurrence(index[normalized], node.SpanStart);
            ISymbol? symbol = context.SemanticModel.GetEnclosingSymbol(node.SpanStart, context.CancellationToken);
            string identity = symbol?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? container.Kind().ToString();
            byte[] bytes = Encoding.UTF8.GetBytes(
                ruleId + "\n" + path + "\n" + identity + "\n" + node.Kind() + "\n" + normalized + "\n" + occurrence);
            using (SHA256 sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(bytes).Select(value => value.ToString("x2")));
        }

        private static ImmutableDictionary<string, ImmutableArray<int>> BuildOccurrenceIndex(SyntaxNode container, int rawKind)
        {
            var positions = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            foreach (SyntaxNode candidate in container.DescendantNodesAndSelf().Where(candidate => candidate.RawKind == rawKind))
            {
                string normalized = candidate.WithoutTrivia().ToFullString();
                if (!positions.TryGetValue(normalized, out List<int>? spans))
                {
                    spans = new List<int>();
                    positions.Add(normalized, spans);
                }
                spans.Add(candidate.SpanStart);
            }
            return positions.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.ToImmutableArray(), StringComparer.Ordinal);
        }

        private static int FindOccurrence(ImmutableArray<int> spans, int spanStart)
        {
            int low = 0;
            int high = spans.Length;
            while (low < high)
            {
                int middle = low + ((high - low) >> 1);
                if (spans[middle] <= spanStart)
                    low = middle + 1;
                else
                    high = middle;
            }
            return low - 1;
        }
    }
}
