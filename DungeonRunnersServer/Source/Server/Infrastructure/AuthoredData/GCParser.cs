using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DungeonRunners.Engine;

namespace DungeonRunners.Data
{

    public class GCNode
    {
        public string Name { get; set; }
        public string Extends { get; set; }
        public bool IsStatic { get; set; }
        public bool IsAnonymous { get; set; }

        public Dictionary<string, string> Properties { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public List<string> PropertyOrder { get; set; } = new List<string>();

        public Dictionary<string, GCNode> Children { get; set; } = new Dictionary<string, GCNode>(StringComparer.OrdinalIgnoreCase);
        public List<string> ChildOrder { get; set; } = new List<string>();
        public List<GCNode> OrderedChildren { get; set; } = new List<GCNode>();

        public List<GCNode> AnonymousChildren { get; set; } = new List<GCNode>();

        public string SourceFile { get; set; }
        public string CanonicalPath { get; set; }
        public int PackageEntryId { get; set; }
        public string NativeClassName { get; internal set; }
        private string _gcClassName;
        private string _gcClassPath;
        private bool? _ownsGCClass;
        public string GCClassName { get => _gcClassName ?? Name; internal set => _gcClassName = value; }
        public string GCClassPath { get => _gcClassPath ?? CanonicalPath ?? Name; internal set => _gcClassPath = value; }
        public bool OwnsGCClass { get => _ownsGCClass ?? !IsAnonymous; internal set => _ownsGCClass = value; }
        internal int NativeObjectFlags { get; set; } = 4;
        internal GCNode NativeBase { get; set; }
        internal bool InheritanceResolved { get; set; }

        public IEnumerable<GCNode> EnumerateChildrenInOrder()
        {
            var emittedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var emittedNodes = new HashSet<GCNode>();

            if (OrderedChildren != null)
            {
                foreach (GCNode orderedChild in OrderedChildren)
                {
                    if (orderedChild == null || !emittedNodes.Add(orderedChild))
                        continue;
                    string childName = orderedChild.Name ?? string.Empty;
                    if (!orderedChild.IsAnonymous && string.IsNullOrWhiteSpace(childName))
                        continue;
                    if (!orderedChild.IsAnonymous)
                        emittedNames.Add(childName);
                    yield return orderedChild;
                }
            }

            if (ChildOrder != null && Children != null)
            {
                foreach (string childName in ChildOrder)
                    if (!string.IsNullOrWhiteSpace(childName)
                        && emittedNames.Add(childName)
                        && Children.TryGetValue(childName, out GCNode child)
                        && child != null && emittedNodes.Add(child))
                        yield return child;
            }

            if (AnonymousChildren != null)
            {
                foreach (GCNode anonymousChild in AnonymousChildren)
                    if (anonymousChild != null && emittedNodes.Add(anonymousChild))
                        yield return anonymousChild;
            }
        }

        public void SetProperty(string propertyName, string value)
        {
            if (string.IsNullOrWhiteSpace(propertyName))
                return;
            if (!Properties.ContainsKey(propertyName))
                PropertyOrder.Add(propertyName);
            Properties[propertyName] = value;
        }

        public IEnumerable<KeyValuePair<string, string>> EnumeratePropertiesInOrder()
        {
            var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (PropertyOrder != null)
            {
                foreach (string propertyName in PropertyOrder)
                    if (!string.IsNullOrWhiteSpace(propertyName)
                        && emitted.Add(propertyName)
                        && Properties.TryGetValue(propertyName, out string value))
                        yield return new KeyValuePair<string, string>(propertyName, value);
            }
            foreach (KeyValuePair<string, string> propertyEntry in Properties)
                if (emitted.Add(propertyEntry.Key))
                    yield return propertyEntry;
        }

        public static GCNode MergeInherited(GCNode parent, GCNode child)
        {
            return MergeInherited(parent, child, null, child?.OwnsGCClass ?? false);
        }

        internal static GCNode MergeInherited(GCNode parent, GCNode child, Func<GCNode, GCNode, GCNode> resolveChild, bool ownsGCClass)
        {
            if (parent == null)
                return child;
            if (child == null)
                return parent;

            var merged = new GCNode
            {
                Name = child.Name,
                Extends = child.Extends ?? parent.Extends,
                IsStatic = (ownsGCClass && child.IsStatic) || parent.IsStatic,
                IsAnonymous = child.IsAnonymous,
                SourceFile = child.SourceFile,
                CanonicalPath = child.CanonicalPath,
                PackageEntryId = child.PackageEntryId,
                NativeClassName = child.NativeClassName ?? parent.NativeClassName,
                GCClassName = ownsGCClass ? child.GCClassName : parent.GCClassName,
                GCClassPath = ownsGCClass ? child.GCClassPath : parent.GCClassPath,
                OwnsGCClass = ownsGCClass,
                NativeObjectFlags = 4,
                NativeBase = parent
            };

            foreach (KeyValuePair<string, string> propertyEntry in parent.EnumeratePropertiesInOrder())
                merged.SetProperty(propertyEntry.Key, propertyEntry.Value);
            foreach (KeyValuePair<string, string> propertyEntry in child.EnumeratePropertiesInOrder())
                merged.SetProperty(propertyEntry.Key, propertyEntry.Value);

            bool childFailed = false;
            foreach (GCNode incomingChild in child.EnumerateChildrenInOrder())
            {
                GCNode existingChild = incomingChild.IsAnonymous
                    ? null : FindInheritedChild(parent, incomingChild.Name);
                GCNode effectiveChild = resolveChild == null
                    ? MergeInherited(existingChild, incomingChild)
                    : resolveChild(existingChild, incomingChild);
                if (effectiveChild == null)
                {
                    childFailed = true;
                    continue;
                }
                merged.OrderedChildren.Add(effectiveChild);
            }
            CopyInheritedChildren(parent, merged);
            merged.IndexChildren();
            return childFailed ? null : merged;
        }

        private static void CopyInheritedChildren(GCNode parent, GCNode target)
        {
            int insertionIndex = 0;
            bool namedTarget = target.OwnsGCClass || (target.NativeObjectFlags & 8) != 0;
            foreach (GCNode source in parent.EnumerateChildrenInOrder())
            {
                if (!namedTarget && source.IsStatic)
                    continue;
                bool namedSource = source.OwnsGCClass || (source.NativeObjectFlags & 8) != 0;
                GCNode existing = namedSource ? target.GetChildByGCClass(source.GCClassName) : null;
                if (existing != null && ((source.NativeObjectFlags & 8) != 0
                    || (target.NativeObjectFlags & 4) != 0 || (existing.NativeObjectFlags & 1) != 0))
                    continue;
                var copy = new GCNode
                {
                    Name = source.Name,
                    Extends = source.Extends,
                    IsStatic = source.IsStatic,
                    IsAnonymous = source.IsAnonymous,
                    SourceFile = source.SourceFile,
                    CanonicalPath = source.CanonicalPath,
                    PackageEntryId = source.PackageEntryId,
                    NativeClassName = source.NativeClassName,
                    GCClassName = source.GCClassName,
                    GCClassPath = source.GCClassPath,
                    OwnsGCClass = false,
                    NativeObjectFlags = (target.NativeObjectFlags & 4) | (namedTarget && namedSource ? 8 : 0),
                    NativeBase = source,
                    InheritanceResolved = source.InheritanceResolved
                };
                foreach (KeyValuePair<string, string> property in source.EnumeratePropertiesInOrder())
                    copy.SetProperty(property.Key, property.Value);
                CopyInheritedChildren(source, copy);
                copy.IndexChildren();
                target.OrderedChildren.Insert(insertionIndex++, copy);
                copy.NativeObjectFlags |= 1;
            }
        }

        private void IndexChildren()
        {
            foreach (GCNode child in OrderedChildren)
            {
                if (child.IsAnonymous)
                    AnonymousChildren.Add(child);
                else if (Children.TryAdd(child.Name, child))
                    ChildOrder.Add(child.Name);
            }
        }

        public GCNode GetChildByGCClass(string name, bool searchStaticBases = false)
        {
            if (name == null)
                return null;
            foreach (GCNode child in EnumerateChildrenInOrder())
                if (ClassNamesEqual(child.GCClassName, name) || ClassNamesEqual(child.GCClassPath, name))
                    return child;
            if (searchStaticBases)
                for (GCNode current = NativeBase; current != null; current = current.NativeBase)
                    foreach (GCNode child in current.EnumerateChildrenInOrder())
                        if (child.IsStatic && (ClassNamesEqual(child.GCClassName, name) || ClassNamesEqual(child.GCClassPath, name)))
                            return child;
            return null;
        }

        public GCNode GetChildByNativeClass(string nativeClass)
        {
            for (GCNode current = this; current != null; current = current.NativeBase)
                foreach (GCNode child in current.EnumerateChildrenInOrder())
                    if (NativeAuthoredClasses.IsDerivedFrom(child.NativeClassName, nativeClass))
                        return child;
            return null;
        }

        public GCNode GetDescription(string expectedNativeClass = "GCObjectDesc")
        {
            GCNode description = GetChildByNativeClass("GCObjectDesc");
            return NativeAuthoredClasses.IsDerivedFrom(description?.NativeClassName, expectedNativeClass) ? description : null;
        }

        internal static GCNode FindInheritedChild(GCNode parent, string name)
        {
            for (GCNode current = parent; current != null; current = current.NativeBase)
            {
                GCNode child = current.GetChildByGCClass(name);
                if (child != null)
                    return child;
            }
            return null;
        }

        internal static bool ClassNamesEqual(string first, string second)
        {
            if (first == null || second == null || first.Length != second.Length)
                return false;
            for (int index = 0; index < first.Length; index++)
            {
                char left = first[index], right = second[index];
                if (left >= 'A' && left <= 'Z') left = (char)(left + ('a' - 'A'));
                if (right >= 'A' && right <= 'Z') right = (char)(right + ('a' - 'A'));
                if (left != right)
                    return false;
            }
            return true;
        }


        public string GetString(string key, string fallback = "")
        {
            return Properties.TryGetValue(key, out string val) ? val : fallback;
        }

        public float GetFloat(string key, float fallback = 0f)
        {
            if (!Properties.TryGetValue(key, out string val))
                return fallback;
            AuthoredNumberParser.TryParsePrefix(val, out double result, out bool underflow);
            return result == 0 && underflow ? 0f : (float)result;
        }

        public int GetFixed32(string key, int fallbackF32 = 0)
        {
            if (!Properties.TryGetValue(key, out string val))
                return fallbackF32;
            TryParseFixed32Core(val, out int result);
            return result;
        }

        public int GetFixed32Ceiling(string key, int fallbackF32 = 0)
        {
            return GetFixed32(key, fallbackF32);
        }

        public static bool TryParseFixed32(string text, out int result)
        {
            return TryParseFixed32Core(text, out result);
        }

        private static bool TryParseFixed32Core(string text, out int result)
        {
            result = 0;
            if (!AuthoredNumberParser.TryParsePrefix(text, out double value, out _))
                return false;
            double scaled = value * 256d;
            int truncated = scaled >= int.MinValue && scaled < 2147483648d
                ? (int)scaled
                : int.MinValue;
            result = truncated / 256d < value ? unchecked(truncated + 1) : truncated;
            return true;
        }

        public int GetInt(string key, int fallback = 0)
        {
            if (!Properties.TryGetValue(key, out string val))
                return fallback;
            if (string.IsNullOrEmpty(val))
                return 0;
            int index = 0;
            while (index < val.Length && AuthoredNumberParser.IsWhitespace(val[index]))
                index++;
            bool negative = index < val.Length && val[index] == '-';
            if (index < val.Length && (val[index] == '+' || val[index] == '-'))
                index++;
            uint limit = negative ? 2147483648u : int.MaxValue;
            uint value = 0;
            while (index < val.Length && val[index] >= '0' && val[index] <= '9')
            {
                uint digit = (uint)(val[index] - '0');
                if (value > (limit - digit) / 10)
                    return negative ? int.MinValue : int.MaxValue;
                value = value * 10 + digit;
                index++;
            }
            return negative ? unchecked(-(int)value) : (int)value;
        }

        public bool GetBool(string key, bool fallback = false)
        {
            if (Properties.TryGetValue(key, out string val))
            {
                return string.Equals(val, "true", StringComparison.OrdinalIgnoreCase);
            }
            return fallback;
        }

        public bool HasProperty(string key) => Properties.ContainsKey(key);
        public bool HasChild(string name) => Children.ContainsKey(name);
        public GCNode GetChild(string name) => Children.TryGetValue(name, out GCNode c) ? c : null;

        public GCNode ResolvePath(string dottedPath)
        {
            if (string.IsNullOrEmpty(dottedPath)) return this;

            string[] parts = dottedPath.Split('.');
            GCNode current = this;

            foreach (string part in parts)
            {
                if (current.Children.TryGetValue(part, out GCNode child))
                {
                    current = child;
                }
                else
                {
                    return null;
                }
            }
            return current;
        }

        public string GetNestedProperty(string childName, string propName)
        {
            var child = GetChild(childName);
            if (child != null && child.Properties.TryGetValue(propName, out string val))
                return val;
            return null;
        }

        public float GetNestedFloat(string childName, string propName, float fallback = 0f)
        {
            return GetChild(childName)?.GetFloat(propName, fallback) ?? fallback;
        }

        public int GetNestedInt(string childName, string propName, int fallback = 0)
        {
            return GetChild(childName)?.GetInt(propName, fallback) ?? fallback;
        }

        public override string ToString()
        {
            return $"GCNode[{Name}] extends={Extends ?? "none"} props={Properties.Count} children={Children.Count}";
        }
    }

    public static class GcParser
    {
        public static GCNode ParseFile(string filePath)
        {
            string text = File.ReadAllText(filePath);
            string fileName = Path.GetFileNameWithoutExtension(filePath);
            return Parse(text, fileName);
        }

        public static GCNode Parse(string text, string sourceFile = "")
        {
            text = StripComments(text);
            int pos = 0;
            var node = ParseTopLevel(text, ref pos, sourceFile);
            if (node != null && string.Equals(node.Extends, "GameObject", StringComparison.Ordinal))
                node.Extends = "GCObject";
            return node;
        }


        private static string StripComments(string text)
        {
            var sb = new StringBuilder(text.Length);
            int i = 0;
            bool inString = false;

            while (i < text.Length)
            {
                if (text[i] == '"' && (i == 0 || text[i - 1] != '\\'))
                {
                    inString = !inString;
                    sb.Append(text[i]);
                    i++;
                    continue;
                }

                if (inString)
                {
                    sb.Append(text[i]);
                    i++;
                    continue;
                }

                if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n') i++;
                    continue;
                }

                if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/')) i++;
                    if (i + 1 < text.Length) i += 2;
                    continue;
                }

                sb.Append(text[i]);
                i++;
            }

            return sb.ToString();
        }


        private static GCNode ParseTopLevel(string text, ref int pos, string sourceFile)
        {
            SkipWhitespace(text, ref pos);

            string name = null;
            string extends_ = null;
            bool isStatic = false;

            if (TryReadWord(text, ref pos, out string firstWord))
            {
                if (firstWord.Equals("static", StringComparison.OrdinalIgnoreCase))
                {
                    isStatic = true;
                    SkipWhitespace(text, ref pos);
                    TryReadWord(text, ref pos, out name);
                }
                else
                {
                    name = firstWord;
                }
            }

            if (name == null) return null;

            SkipWhitespace(text, ref pos);

            int savedPos = pos;
            if (TryReadWord(text, ref pos, out string maybeExtends))
            {
                if (maybeExtends.Equals("extends", StringComparison.OrdinalIgnoreCase))
                {
                    SkipWhitespace(text, ref pos);
                    TryReadWord(text, ref pos, out extends_);
                }
                else
                {
                    pos = savedPos;
                }
            }

            SkipWhitespace(text, ref pos);

            var node = new GCNode
            {
                Name = name,
                Extends = extends_,
                IsStatic = isStatic,
                IsAnonymous = name == "*",
                SourceFile = sourceFile
            };

            if (pos < text.Length && text[pos] == '{')
            {
                pos++;
                ParseBlockBody(text, ref pos, node, sourceFile);
            }

            return node;
        }


        private static void ParseBlockBody(string text, ref int pos, GCNode parent, string sourceFile)
        {
            while (pos < text.Length)
            {
                SkipWhitespace(text, ref pos);
                if (pos >= text.Length) break;

                if (text[pos] == '}')
                {
                    pos++;
                    return;
                }

                bool isStatic = false;
                if (!TryReadWord(text, ref pos, out string word)) break;

                if (word.Equals("static", StringComparison.OrdinalIgnoreCase))
                {
                    isStatic = true;
                    SkipWhitespace(text, ref pos);
                    if (!TryReadWord(text, ref pos, out word)) break;
                }

                SkipWhitespace(text, ref pos);
                if (pos >= text.Length) break;

                char next = text[pos];

                if (next == '=')
                {
                    pos++;
                    SkipWhitespace(text, ref pos);
                    string value = ReadValue(text, ref pos);
                    parent.SetProperty(word, value);
                }
                else if (next == '{' || IsWord(text, pos, "extends"))
                {
                    string childExtends = null;

                    if (IsWord(text, pos, "extends"))
                    {
                        pos += 7;
                        SkipWhitespace(text, ref pos);
                        TryReadWord(text, ref pos, out childExtends);
                        SkipWhitespace(text, ref pos);
                    }

                    var child = new GCNode
                    {
                        Name = word,
                        Extends = childExtends,
                        IsStatic = isStatic,
                        IsAnonymous = word == "*",
                        SourceFile = sourceFile
                    };

                    if (pos < text.Length && text[pos] == '{')
                    {
                        pos++;
                        ParseBlockBody(text, ref pos, child, sourceFile);
                    }

                    parent.OrderedChildren.Add(child);
                    if (child.IsAnonymous)
                        parent.AnonymousChildren.Add(child);
                    else
                    {
                        if (parent.Children.TryAdd(word, child))
                            parent.ChildOrder.Add(word);
                    }
                }
                else
                {
                    SkipToNextStatement(text, ref pos);
                }
            }
        }


        private static string ReadValue(string text, ref int pos)
        {
            SkipWhitespace(text, ref pos);
            if (pos >= text.Length) return "";

            var sb = new StringBuilder();

            if (text[pos] == '"')
            {
                pos++;
                while (pos < text.Length && text[pos] != '"')
                {
                    if (text[pos] == '\\' && pos + 1 < text.Length)
                    {
                        sb.Append(text[pos + 1]);
                        pos += 2;
                    }
                    else
                    {
                        sb.Append(text[pos]);
                        pos++;
                    }
                }
                if (pos < text.Length) pos++;
            }
            else
            {
                while (pos < text.Length && text[pos] != ';' && text[pos] != '\n' && text[pos] != '\r' && text[pos] != '}')
                {
                    sb.Append(text[pos]);
                    pos++;
                }
            }

            SkipWhitespace(text, ref pos);
            if (pos < text.Length && text[pos] == ';') pos++;

            return sb.ToString().Trim();
        }


        private static void SkipWhitespace(string text, ref int pos)
        {
            while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
        }

        private static bool TryReadWord(string text, ref int pos, out string word)
        {
            SkipWhitespace(text, ref pos);
            if (pos >= text.Length)
            {
                word = null;
                return false;
            }

            int start = pos;
            while (pos < text.Length && (char.IsLetterOrDigit(text[pos]) || text[pos] == '_' || text[pos] == '.' || text[pos] == '/' || text[pos] == '\\' || text[pos] == '-' || text[pos] == '*' || text[pos] == ':'))
            {
                pos++;
            }

            if (pos == start)
            {
                word = null;
                return false;
            }

            word = text.Substring(start, pos - start);
            return true;
        }

        private static bool IsWord(string text, int pos, string word)
        {
            if (pos + word.Length > text.Length) return false;
            for (int wordIndex = 0; wordIndex < word.Length; wordIndex++)
            {
                if (char.ToLower(text[pos + wordIndex]) != char.ToLower(word[wordIndex])) return false;
            }
            if (pos + word.Length < text.Length && char.IsLetterOrDigit(text[pos + word.Length])) return false;
            return true;
        }

        private static void SkipToNextStatement(string text, ref int pos)
        {
            while (pos < text.Length && text[pos] != ';' && text[pos] != '}' && text[pos] != '{' && text[pos] != '\n')
                pos++;
            if (pos < text.Length && text[pos] == ';') pos++;
        }
    }
}
