using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DungeonRunners.Engine;

namespace DungeonRunners.Data
{
    public class GCDatabase
    {
        private const int ExpectedRuntimeTextDocumentCount = 11795;
        private static GCDatabase _instance;
        public static GCDatabase Instance => _instance ??= new GCDatabase();

        private Dictionary<string, GCNode> _nodes = new Dictionary<string, GCNode>(StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, GCNode> _pathRegistry = new Dictionary<string, GCNode>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _canonicalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _canonicalRootAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _ambiguousAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _reportedAmbiguousAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private ConcurrentDictionary<string, GCNode> _resolvedCache = new ConcurrentDictionary<string, GCNode>(StringComparer.OrdinalIgnoreCase);
        private ConcurrentDictionary<string, List<(int levelF32, int valueF32)>> _curveCache = new ConcurrentDictionary<string, List<(int, int)>>(StringComparer.OrdinalIgnoreCase);

        public GCNode GlobalKnobs => GetNode("GlobalKnobs");
        public GCNode Tables => GetNode("Tables");

        public bool IsLoaded { get; private set; }
        public int FileCount { get; private set; }
        public int FlatFileCount { get; private set; }
        public int PackageFileCount { get; private set; }
        public int NodeCount => _pathRegistry.Count;
        public IEnumerable<string> RegisteredPaths => _pathRegistry.Keys.ToArray();


        public void Load()
        {
            IsLoaded = false;
            _nodes.Clear();
            _pathRegistry.Clear();
            _canonicalPaths.Clear();
            _canonicalRootAliases.Clear();
            _ambiguousAliases.Clear();
            _reportedAmbiguousAliases.Clear();
            _resolvedCache.Clear();
            _curveCache.Clear();

            FlatFileCount = 0;
            PackageFileCount = 0;
            FileCount = FlatFileCount;
            int parseErrors = 0;

            var packageCatalog = PackageCatalog.Instance;
            if (!packageCatalog.IsLoaded)
                packageCatalog.LoadFromAssets();
            if (packageCatalog.IsLoaded)
            {
                foreach (var doc in packageCatalog.RuntimeTextDocuments
                    .OrderBy(doc => NormalizeCanonicalPath(doc?.GcPath).Count(ch => ch == '.'))
                    .ThenBy(doc => doc?.EntryIndex ?? int.MaxValue)
                    .ThenBy(doc => doc?.EntryId ?? int.MaxValue))
                {
                    if (doc == null || string.IsNullOrWhiteSpace(doc.Text))
                        continue;
                    try
                    {
                        GCNode node = GcParser.Parse(doc.Text, doc.Stem);
                        if (node == null || string.IsNullOrEmpty(node.Name))
                            continue;
                        RegisterParsedNode(node, ResolveRegistryName(node, doc.Stem), doc.GcPath, doc.EntryId, true);
                        PackageFileCount++;
                    }
                    catch (Exception ex)
                    {
                        parseErrors++;
                        if (parseErrors <= 10)
                            Debug.LogError($"[GC-DATABASE] packageParseError doc='{doc.Name}' message='{ex.Message}'");
                    }
                }
            }

            FileCount = FlatFileCount + PackageFileCount;

            if (PackageFileCount == 0)
                throw new InvalidDataException("No GC documents found in authored.db");
            if (PackageFileCount != ExpectedRuntimeTextDocumentCount)
                throw new InvalidDataException($"Runtime authored text coverage mismatch expected={ExpectedRuntimeTextDocumentCount} actual={PackageFileCount}");

            Debug.LogError($"[GC-DATABASE] loadSummary phase=start");
            Debug.LogError($"[GC-DATABASE] files={FileCount} flat={FlatFileCount} packageGc={PackageFileCount} paths={_pathRegistry.Count}");
            if (parseErrors > 0)
            {
                Debug.LogError($"[GC-DATABASE] parseErrors={parseErrors}");
                throw new InvalidDataException($"GC-DATABASE parseErrors={parseErrors}");
            }

            if (GlobalKnobs == null)
                throw new InvalidDataException("GlobalKnobs.gc not found");
            else
                Debug.LogError($"[GC-DATABASE] globalKnobs weaponDamagePerLevel={GlobalKnobs.GetFloat("WeaponDamagePerLevel")} meleeDamagePerStrength={GlobalKnobs.GetFloat("MeleeDamagePerStrength")}");

            ValidateRequiredGlobalKnobs();

            if (Tables == null)
                throw new InvalidDataException("Tables.gc not found");
            string[] requiredCurveTables =
            {
                "Experience",
                "MonsterHealth",
                "MonsterDamage",
                "MonsterAttackRating",
                "MonsterDefenseRating",
                "ReSpecCost"
            };
            foreach (string tableName in requiredCurveTables)
            {
                var table = Tables.GetChild(tableName);
                if (table == null || table.AnonymousChildren.Count == 0)
                    throw new InvalidDataException($"Tables.{tableName} not found");
            }

            ValidateCanonicalRuntimeEntries();

            IsLoaded = true;
            Debug.LogError($"[GC-DATABASE] loadSummary phase=end");
        }

        private void ValidateCanonicalRuntimeEntries()
        {
            GCNode avatarBehaviorExact = Resolve("avatar.base.UnitBehavior");
            GCNode avatarBehavior = ResolveWithInheritance("avatar.base.UnitBehavior");
            GCNode avatarDescription = avatarBehavior?.GetChild("Description") ?? avatarBehavior;
            if (avatarBehaviorExact == null
                || avatarBehaviorExact.PackageEntryId != 19218
                || avatarDescription == null
                || avatarDescription.GetInt("CollisionBand", int.MinValue) != 5
                || avatarDescription.GetInt("CollisionPriority", int.MinValue) != 500)
                throw new InvalidDataException("avatar.base.UnitBehavior canonical entry 19218 invalid");

            GCNode meleeExact = Resolve("creatures.base.weapons.melee");
            GCNode melee = ResolveWithInheritance("creatures.base.weapons.melee");
            GCNode meleeDescription = melee?.GetChild("Description") ?? melee;
            if (meleeExact == null
                || meleeExact.PackageEntryId != 19434
                || meleeDescription == null
                || meleeDescription.GetFixed32("Range", int.MinValue) != 8 * 0x100)
                throw new InvalidDataException("creatures.base.weapons.melee canonical entry 19434 invalid");

            Debug.LogError("[GC-DATABASE] canonicalRuntime avatarUnitBehavior=19218 collisionBand=5 collisionPriority=500 creatureMelee=19434 rangeF32=2048");
        }

        private void ValidateRequiredGlobalKnobs()
        {
            string[] required =
            {
                "MaxLevel",
                "RangedDamagePerAgility",
                "ItemLevelDeltaNormal",
                "ItemLevelDeltaSuperior",
                "ItemLevelDeltaMagical",
                "ItemLevelDeltaRare",
                "ItemLevelDeltaUnique",
                "ItemLevelDeltaMythic",
                "ItemGoldValuePerLevel",
                "ItemBuyValueModifier",
                "ItemSellValueModifier",
                "ItemPriceModifierNormal",
                "ItemPriceModifierSuperior",
                "ItemPriceModifierMagical",
                "ItemPriceModifierRare",
                "ItemPriceModifierUnique",
                "ItemPriceModifierMythic",
                "MemberGoldMod",
                "FreePlayerExperienceMult",
                "ExperienceMod",
                "HeroHealthPerLevel",
                "HeroHealthRegen",
                "HeroPowerRegen",
                "BaseSkillPowerCost",
                "SkillPowerCostPerLevel",
                "MonsterHealthRegen",
                "MonsterPowerRegen",
                "SkillValuePerLevel",
                "ItemDefenseRatingPerLevel",
                "MonsterAttackSpeed",
                "MonsterStunMod",
                "MonsterStunResist",
                "WeaponDamagePerLevel",
                "DPSModifier",
                "MeleeDamagePerStrength",
                "HeroCriticalChance",
                "AttackRatingPerAgility",
                "SkillDamagePerIntellect",
                "SkillDamagePerLevel",
                "DefenseRatingPerStrength",
                "MonsterCriticalChance",
                "QuestGoldPerLevel",
                "ItemChanceRequiresMembershipRare",
                "ItemChanceRequiresMembershipUnique",
                "ItemChanceRequiresMembershipMythic"
            };
            foreach (string name in required)
                GetRequiredKnobFixed32(name);
        }

        private static string ResolveRegistryName(GCNode node, string fallbackName)
        {
            string name = node != null ? node.Name : "";
            return string.Equals(name, "*", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(name)
                ? fallbackName
                : name;
        }

        private void RegisterParsedNode(GCNode node, string registryName, string sourcePath, int packageEntryId, bool packageBacked)
        {
            if (node == null || string.IsNullOrWhiteSpace(registryName))
                return;

            string canonicalPath = NormalizeCanonicalPath(string.IsNullOrWhiteSpace(sourcePath) ? registryName : sourcePath);
            if (string.IsNullOrWhiteSpace(canonicalPath))
                canonicalPath = NormalizeCanonicalPath(registryName);
            if (!packageBacked)
            {
                string flatCanonicalPath = "flat." + canonicalPath;
                StampCanonicalIdentity(node, flatCanonicalPath, 0);
                RegisterCanonicalPath(flatCanonicalPath, node);
                string flatRegistryAlias = NormalizeCanonicalPath(registryName);
                string flatNodeAlias = string.Equals(node.Name, "*", StringComparison.Ordinal)
                    ? flatRegistryAlias
                    : NormalizeCanonicalPath(node.Name);
                RegisterUniqueAlias(flatRegistryAlias, node);
                RegisterUniqueAlias(flatNodeAlias, node);
                RegisterChildren(flatCanonicalPath, node, false);
                return;
            }
            StampCanonicalIdentity(node, canonicalPath, packageEntryId);
            RegisterCanonicalPath(canonicalPath, node);
            RegisterChildren(canonicalPath, node, true);

            string registryAlias = NormalizeCanonicalPath(registryName);
            string nodeAlias = string.Equals(node.Name, "*", StringComparison.Ordinal)
                ? registryAlias
                : NormalizeCanonicalPath(node.Name);
            bool canonicalRoot = packageBacked && canonicalPath.IndexOf('.') < 0;
            if (canonicalRoot)
            {
                RegisterCanonicalRootAlias(registryAlias, node);
                RegisterCanonicalRootAlias(nodeAlias, node);
            }
            else
            {
                RegisterUniqueAlias(registryAlias, node);
                RegisterUniqueAlias(nodeAlias, node);
            }
        }

        private static string NormalizeCanonicalPath(string path)
        {
            string normalized = (path ?? "").Trim().Replace('\\', '.').Replace('/', '.');
            if (normalized.EndsWith(".gc", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(0, normalized.Length - 3);
            while (normalized.Contains("..", StringComparison.Ordinal))
                normalized = normalized.Replace("..", ".", StringComparison.Ordinal);
            return normalized.Trim('.');
        }

        private static void StampCanonicalIdentity(GCNode node, string canonicalPath, int packageEntryId)
        {
            if (node == null)
                return;
            node.CanonicalPath = canonicalPath;
            node.PackageEntryId = packageEntryId;
            var orderedChildren = node.OrderedChildren != null && node.OrderedChildren.Count > 0
                ? node.OrderedChildren
                : node.Children.Values.ToList();
            var totals = orderedChildren
                .Where(child => child != null)
                .GroupBy(child => child.Name ?? "", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            var equivalentDuplicates = orderedChildren
                .Where(child => child != null)
                .GroupBy(child => child.Name ?? "", StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .ToDictionary(
                    group => group.Key,
                    group => group.Skip(1).All(child => DefinitionsEquivalent(group.First(), child)),
                    StringComparer.OrdinalIgnoreCase);
            var occurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var stamped = new HashSet<GCNode>();
            foreach (GCNode child in orderedChildren)
            {
                if (child == null)
                    continue;
                string childName = child.Name ?? "";
                occurrences.TryGetValue(childName, out int occurrence);
                occurrences[childName] = occurrence + 1;
                string childPath = totals[childName] > 1 && !equivalentDuplicates[childName]
                    ? canonicalPath + "." + childName + "[" + occurrence.ToString(CultureInfo.InvariantCulture) + "]"
                    : canonicalPath + "." + childName;
                StampCanonicalIdentity(child, childPath, packageEntryId);
                stamped.Add(child);
            }
            foreach (GCNode child in node.Children.Values)
                if (child != null && stamped.Add(child))
                    StampCanonicalIdentity(child, canonicalPath + "." + (child.Name ?? ""), packageEntryId);
            for (int childIndex = 0; childIndex < node.AnonymousChildren.Count; childIndex++)
                StampCanonicalIdentity(node.AnonymousChildren[childIndex], canonicalPath + ".*[" + childIndex.ToString(CultureInfo.InvariantCulture) + "]", packageEntryId);
        }

        private static bool DefinitionsEquivalent(GCNode left, GCNode right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null ||
                !string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(left.Extends, right.Extends, StringComparison.OrdinalIgnoreCase) ||
                left.IsStatic != right.IsStatic ||
                left.IsAnonymous != right.IsAnonymous ||
                left.Properties.Count != right.Properties.Count)
                return false;

            foreach (var property in left.Properties)
                if (!right.Properties.TryGetValue(property.Key, out string value) ||
                    !string.Equals(property.Value, value, StringComparison.Ordinal))
                    return false;

            var leftChildren = left.OrderedChildren ?? new List<GCNode>();
            var rightChildren = right.OrderedChildren ?? new List<GCNode>();
            if (leftChildren.Count != rightChildren.Count)
                return false;
            for (int index = 0; index < leftChildren.Count; index++)
                if (!DefinitionsEquivalent(leftChildren[index], rightChildren[index]))
                    return false;
            return true;
        }

        private void RegisterCanonicalPath(string key, GCNode node)
        {
            string normalized = NormalizeCanonicalPath(key);
            if (string.IsNullOrWhiteSpace(normalized))
                return;
            if (_pathRegistry.TryGetValue(normalized, out GCNode existing)
                && existing != node
                && (existing.PackageEntryId != node.PackageEntryId
                    || !string.Equals(existing.CanonicalPath, node.CanonicalPath, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"GC canonical path collision path='{normalized}' existingEntry={existing.PackageEntryId} incomingEntry={node.PackageEntryId}");
            _canonicalPaths.Add(normalized);
            _ambiguousAliases.Remove(normalized);
            _pathRegistry[normalized] = node;
            _nodes[normalized] = node;
        }

        private void RegisterCanonicalRootAlias(string key, GCNode node)
        {
            string normalized = NormalizeCanonicalPath(key);
            if (string.IsNullOrWhiteSpace(normalized))
                return;
            _canonicalRootAliases.Add(normalized);
            _ambiguousAliases.Remove(normalized);
            _pathRegistry[normalized] = node;
            _nodes[normalized] = node;
        }

        private void RegisterUniqueAlias(string key, GCNode node)
        {
            string normalized = NormalizeCanonicalPath(key);
            if (string.IsNullOrWhiteSpace(normalized)
                || _canonicalPaths.Contains(normalized)
                || _canonicalRootAliases.Contains(normalized)
                || _ambiguousAliases.Contains(normalized))
                return;
            if (!_pathRegistry.TryGetValue(normalized, out GCNode existing))
            {
                _pathRegistry[normalized] = node;
                _nodes[normalized] = node;
                return;
            }
            if (ReferenceEquals(existing, node)
                || (existing.PackageEntryId == node.PackageEntryId
                    && string.Equals(existing.CanonicalPath, node.CanonicalPath, StringComparison.OrdinalIgnoreCase)))
                return;
            _pathRegistry.Remove(normalized);
            _nodes.Remove(normalized);
            _ambiguousAliases.Add(normalized);
        }

        private void RegisterChildren(string parentPath, GCNode parent, bool canonical)
        {
            var orderedChildren = parent.OrderedChildren != null && parent.OrderedChildren.Count > 0
                ? parent.OrderedChildren
                : parent.Children.Values.ToList();
            var registered = new HashSet<GCNode>();
            foreach (GCNode child in orderedChildren)
            {
                if (child == null || !registered.Add(child))
                    continue;
                string childPath = string.IsNullOrWhiteSpace(child.CanonicalPath)
                    ? parentPath + "." + (child.Name ?? "")
                    : child.CanonicalPath;
                if (canonical)
                    RegisterCanonicalPath(childPath, child);
                else
                    RegisterUniqueAlias(childPath, child);
                RegisterUniqueAlias(parentPath + "." + (child.Name ?? ""), child);
                RegisterChildren(childPath, child, canonical);
            }
            foreach (GCNode child in parent.Children.Values)
                if (child != null && registered.Add(child))
                {
                    string childPath = string.IsNullOrWhiteSpace(child.CanonicalPath)
                        ? parentPath + "." + (child.Name ?? "")
                        : child.CanonicalPath;
                    if (canonical)
                        RegisterCanonicalPath(childPath, child);
                    else
                        RegisterUniqueAlias(childPath, child);
                    RegisterUniqueAlias(parentPath + "." + (child.Name ?? ""), child);
                    RegisterChildren(childPath, child, canonical);
                }
        }


        public GCNode GetNode(string nameOrPath)
        {
            string normalized = NormalizeCanonicalPath(nameOrPath);
            if (_pathRegistry.TryGetValue(normalized, out GCNode node))
                return node;
            return null;
        }

        public GCNode Resolve(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            string normalized = NormalizeCanonicalPath(path);
            if ((_canonicalPaths.Contains(normalized) || _canonicalRootAliases.Contains(normalized))
                && _pathRegistry.TryGetValue(normalized, out GCNode canonical))
                return canonical;
            if (_ambiguousAliases.Contains(normalized))
            {
                if (_reportedAmbiguousAliases.Add(normalized))
                    Debug.LogError($"[GC-DATABASE] resolveAmbiguous path='{path}' normalized='{normalized}' result=missing");
                return null;
            }

            if (_pathRegistry.TryGetValue(normalized, out GCNode exact))
                return exact;

            return null;
        }

        public List<string> GetInheritanceChainPaths(string path, int maxDepth = 32)
        {
            var chain = new List<string>();
            if (string.IsNullOrWhiteSpace(path))
                return chain;

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string currentPath = path.Trim();
            for (int depth = 0; depth < maxDepth && !string.IsNullOrWhiteSpace(currentPath); depth++)
            {
                if (!visited.Add(currentPath))
                    break;
                GCNode node = ResolveExact(currentPath);
                if (node == null)
                    break;
                chain.Add(currentPath);
                currentPath = node.Extends;
            }
            return chain;
        }

        private GCNode ResolveExact(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            string normalized = NormalizeCanonicalPath(path);
            if ((_canonicalPaths.Contains(normalized) || _canonicalRootAliases.Contains(normalized))
                && _pathRegistry.TryGetValue(normalized, out GCNode canonical))
                return canonical;
            if (_ambiguousAliases.Contains(normalized))
                return null;
            if (_pathRegistry.TryGetValue(normalized, out GCNode exact))
                return exact;
            return null;
        }

        public GCNode ResolveWithInheritance(string path)
        {
            string normalized = NormalizeCanonicalPath(path);
            if (_resolvedCache.TryGetValue(normalized, out GCNode cached))
                return cached;

            GCNode node = Resolve(normalized);
            if (node == null) return null;

            GCNode resolved = ResolveWithInheritance(node);
            _resolvedCache[normalized] = resolved;
            return resolved;
        }

        public GCNode ResolveLastDeclaredDuplicateWithInheritance(string path)
        {
            string normalized = NormalizeCanonicalPath(path);
            if (string.IsNullOrWhiteSpace(normalized))
                return null;
            if (!_ambiguousAliases.Contains(normalized))
                return ResolveWithInheritance(normalized);

            string prefix = normalized + "[";
            int selectedIndex = -1;
            GCNode selected = null;
            foreach (var entry in _pathRegistry)
            {
                string candidate = entry.Key;
                if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    || !candidate.EndsWith("]", StringComparison.Ordinal))
                    continue;
                string indexText = candidate.Substring(prefix.Length, candidate.Length - prefix.Length - 1);
                if (!int.TryParse(indexText, NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                    || index <= selectedIndex)
                    continue;
                selectedIndex = index;
                selected = entry.Value;
            }
            return ResolveWithInheritance(selected);
        }

        public GCNode ResolveWithInheritance(GCNode node)
        {
            if (node == null)
                return null;
            return FlattenInheritance(node, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        private GCNode FlattenInheritance(GCNode node, HashSet<string> visited)
        {
            string key = node.Name + "|" + (node.Extends ?? "");
            if (visited.Contains(key)) return node;
            visited.Add(key);

            if (string.IsNullOrEmpty(node.Extends))
                return node;

            GCNode parent = Resolve(node.Extends);
            if (parent == null)
                return node;

            GCNode resolvedParent = FlattenInheritance(parent, visited);

            return GCNode.MergeInherited(resolvedParent, node);
        }

        private GCNode MergeNodes(GCNode parent, GCNode child)
        {
            return GCNode.MergeInherited(parent, child);
        }


        public int GetRequiredKnobFixed32(string name)
        {
            GCNode knobs = GlobalKnobs ?? throw new InvalidDataException("GlobalKnobs.gc not loaded");
            if (!knobs.HasProperty(name))
                throw new InvalidDataException($"GlobalKnobs.{name} not found");
            string raw = knobs.GetString(name, "");
            if (!GCNode.TryParseFixed32(raw, out int value))
                throw new InvalidDataException($"GlobalKnobs.{name} is not a valid fixed32 value: '{raw}'");
            return value;
        }

        public int GetRequiredKnobInt(string name)
        {
            int fixed32 = GetRequiredKnobFixed32(name);
            return fixed32 >> 8;
        }

        public int RequireCurveValueFixed32(string tableName, int targetLevel)
        {
            var curve = GetCurveFixed32(tableName);
            if (curve.Count == 0)
                throw new InvalidDataException($"Tables.{tableName} not found");
            return InterpolateCurveValueFixed32(curve, targetLevel);
        }

        private static int InterpolateCurveValueFixed32(List<(int levelF32, int valueF32)> curve, int targetLevel)
        {
            int levelF32 = ToFixed32(Math.Max(0, targetLevel));
            if (levelF32 <= curve[0].levelF32) return curve[0].valueF32;

            int last = curve.Count - 1;
            if (levelF32 >= curve[last].levelF32) return curve[last].valueF32;

            for (int curveIndex = 0; curveIndex < last; curveIndex++)
            {
                var lo = curve[curveIndex];
                var hi = curve[curveIndex + 1];
                if (levelF32 > hi.levelF32) continue;
                if (hi.levelF32 == lo.levelF32) return hi.valueF32;

                long ratioQ16 = ((long)(levelF32 - lo.levelF32) * 0x10000L) / (hi.levelF32 - lo.levelF32);
                long delta = (long)(hi.valueF32 - lo.valueF32) * ratioQ16;
                return lo.valueF32 + (int)(delta / 0x10000L);
            }

            return curve[last].valueF32;
        }

        private List<(int levelF32, int valueF32)> GetCurveFixed32(string tableName)
        {
            if (_curveCache.TryGetValue(tableName, out var cached)) return cached;

            var curve = new List<(int levelF32, int valueF32)>();
            var table = Tables?.GetChild(tableName);
            if (table?.AnonymousChildren != null)
            {
                foreach (var entry in table.AnonymousChildren)
                {
                    int levelF32 = entry.GetFixed32("Level", 0);
                    int valueF32 = entry.GetFixed32("Value", 0);
                    if (entry.HasProperty("Level") && levelF32 >= 0)
                        curve.Add((levelF32, valueF32));
                }
            }
            curve.Sort((a, b) => a.levelF32.CompareTo(b.levelF32));
            _curveCache[tableName] = curve;
            return curve;
        }

        private static int ToFixed32(float value)
        {
            return (int)decimal.Round((decimal)value * 256m, 0, MidpointRounding.ToEven);
        }

        public static int AuthoredFloatToFixed32(float value)
        {
            return ToFixed32(value);
        }

        public static int AuthoredFloatToFixed32Ceiling(float value)
        {
            return (int)decimal.Ceiling((decimal)value * 256m);
        }

        public static float Fixed32ToFloat(int valueF32)
        {
            return valueF32 / 256f;
        }

        public static int RoundFixed32ToInt(int valueF32)
        {
            if (valueF32 <= 0) return 0;
            int integer = valueF32 >> 8;
            int fraction = valueF32 & 0xFF;
            if (fraction > 0x80) return integer + 1;
            if (fraction < 0x80) return integer;
            return (integer & 1) == 0 ? integer : integer + 1;
        }

        public struct WeaponStatsFixed
        {
            public int DamageF32;
            public int VolatilityF32;
            public int RangeF32;
            public int InitUseRangeF32;
            public int ClientSyncToleranceF32;
            public int CooldownF32;
            public string WeaponClass;
            public int WeaponSpeedF32;
            public string DamageType;
            public string WeaponCategory;
            public bool UseProjectile;
            public int ShotType;
            public int ProjectileSpeedF32;
            public int ProjectileSizeF32;
            public int BurstCount;
            public int StunMod;

            public int RangeRoundedUnits => GCDatabase.RoundFixed32ToInt(RangeF32);
        }

        public WeaponStatsFixed GetWeaponStatsFixed(string weaponGCPath)
        {
            var node = ResolveWithInheritance(weaponGCPath);
            if (node == null)
                throw new InvalidDataException($"Weapon descriptor '{weaponGCPath ?? "<null>"}' is not present in authored GC data.");

            var desc = node.GetChild("Description");
            if (desc == null) desc = node;
            return new WeaponStatsFixed
            {
                DamageF32 = desc.GetFixed32Ceiling("Damage", 0x100),
                VolatilityF32 = desc.GetFixed32Ceiling("DamageVolatility", 0x40),
                RangeF32 = desc.GetFixed32("Range", 0),
                InitUseRangeF32 = desc.GetFixed32("InitUseRange", 64000),
                ClientSyncToleranceF32 = desc.GetFixed32("ClientSyncTolerance", 0),
                CooldownF32 = desc.GetFixed32("CoolDown", 0),
                WeaponClass = desc.GetString("WeaponClass", "HTH"),
                WeaponSpeedF32 = desc.GetFixed32("WeaponSpeed", 100 << 8),
                DamageType = desc.GetString("DamageType", "CRUSHING"),
                WeaponCategory = desc.GetString("WeaponCategory", ""),
                UseProjectile = desc.GetBool("UseProjectile", false),
                ShotType = desc.GetInt("ShotType", 0),
                ProjectileSpeedF32 = desc.GetFixed32("ProjectileSpeed", 180 << 8),
                ProjectileSizeF32 = desc.GetFixed32("ProjectileSize", 10 << 8),
                BurstCount = Math.Max(1, desc.GetInt("BurstCount", 1)),
                StunMod = desc.GetInt("StunMod", 100)
            };
        }

        public float GetArmorDefenseRating(string armorGCPath)
        {
            return Fixed32ToFloat(GetArmorDefenseRatingF32(armorGCPath));
        }

        public int GetArmorDefenseRatingF32(string armorGCPath)
        {
            var node = ResolveWithInheritance(armorGCPath);
            if (node == null) return 0;

            var desc = node.GetChild("Description");
            if (desc == null) desc = node;
            return desc.GetFixed32("DefenseRating", 0);
        }


        public GCNode GetCreatureStats(string creatureGCPath)
        {
            var node = ResolveWithInheritance(creatureGCPath);
            if (node == null) return null;

            return node.GetChild("Description") ?? node;
        }

        public GCNode GetCreatureWeaponStats(string creatureGCPath)
        {
            var node = ResolveWithInheritance(creatureGCPath);
            if (node == null) return null;

            var manip = node.GetChild("Manipulators");
            if (manip == null) return null;

            var weapon = manip.GetChild("PrimaryWeapon");
            if (weapon == null) return null;

            return weapon.GetChild("Description") ?? weapon;
        }


        public void DumpNode(string path, int maxDepth = 3)
        {
            var node = Resolve(path);
            if (node == null)
            {
                Debug.LogError($"[GC-DATABASE] dumpNode path='{path}' state=missing");
                return;
            }
            DumpNodeRecursive(node, 0, maxDepth);
        }

        private void DumpNodeRecursive(GCNode node, int depth, int maxDepth)
        {
            if (depth > maxDepth) return;
            string indent = new string(' ', depth * 2);

            Debug.LogError($"{indent}[{node.Name}] extends={node.Extends ?? "none"} src={node.SourceFile}");
            foreach (var propertyEntry in node.Properties)
                Debug.LogError($"{indent}  {propertyEntry.Key} = {propertyEntry.Value}");
            foreach (var childEntry in node.Children)
                DumpNodeRecursive(childEntry.Value, depth + 1, maxDepth);
            foreach (var anon in node.AnonymousChildren)
                DumpNodeRecursive(anon, depth + 1, maxDepth);
        }
    }
}
