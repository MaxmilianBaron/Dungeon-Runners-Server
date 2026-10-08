using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Mono.Data.Sqlite;
using DungeonRunners.Engine;
using DungeonRunners.Utilities;

namespace DungeonRunners.Data
{
    public class ItemStatDatabase
    {
        private static ItemStatDatabase _instance;
        public static ItemStatDatabase Instance => _instance ??= new ItemStatDatabase();

        public bool IsLoaded { get; private set; }

        public static bool PathBEnabled = true;


        internal const int SchemaVersion = 5;

        struct PoolFormula
        {
            public int PoolTag;
            public string Name;
            public (int LevelF32, int ValueF32)[] Points;

            public int EvalFixed32(int level)
            {
                if (Points == null || Points.Length == 0) return 0;
                int levelF32 = checked(Math.Max(0, level) * 0x100);
                if (levelF32 <= Points[0].LevelF32) return Points[0].ValueF32;
                var last = Points[Points.Length - 1];
                if (levelF32 >= last.LevelF32) return last.ValueF32;
                for (int pointIndex = 0; pointIndex < Points.Length - 1; pointIndex++)
                {
                    var lowerPoint = Points[pointIndex];
                    var upperPoint = Points[pointIndex + 1];
                    if (levelF32 > upperPoint.LevelF32) continue;
                    if (upperPoint.LevelF32 == lowerPoint.LevelF32) return upperPoint.ValueF32;
                    return Fixed32Math.InterpolateCurveValue(lowerPoint.LevelF32, lowerPoint.ValueF32,
                        upperPoint.LevelF32, upperPoint.ValueF32, levelF32);
                }
                return last.ValueF32;
            }
        }

        struct ResolvedMod
        {
            public int ModSlot;
            public string Attribute;
            public string Pool;
            public int PoolTag;
            public int ValueF32;
            public int ValueIncrementF32;
            public bool OverrideTable;
            public NativeCurveTable Curve;
        }

        private Dictionary<int, PoolFormula> _pools = new();
        private Dictionary<string, Dictionary<int, int>> _poolMappings = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, List<ResolvedMod>> _itemMods = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, List<(int Slot, string ModRef)>> _itemWireMods = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, int> _itemReadDataSlotCounts = new(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _itemReadDataSlotMisses = new(StringComparer.OrdinalIgnoreCase);

        private struct GcTextSource
        {
            public string FileName;
            public string Stem;
            public string Text;
            public string SourcePath;
        }

        private IEnumerable<GcTextSource> EnumerateGcSources(string searchPattern)
        {
            var packageCatalog = PackageCatalog.Instance;
            if (!packageCatalog.IsLoaded)
                packageCatalog.LoadFromAssets();
            if (!packageCatalog.IsLoaded)
                yield break;

            foreach (var doc in packageCatalog.EnumerateGcTextDocuments(searchPattern))
            {
                if (doc == null || string.IsNullOrWhiteSpace(doc.FileName))
                    continue;
                yield return new GcTextSource
                {
                    FileName = doc.FileName,
                    Stem = doc.Stem,
                    Text = doc.Text,
                    SourcePath = doc.Name
                };
            }
        }

        private bool TryReadGcSource(string fileName, out GcTextSource source)
        {
            source = default;
            var packageCatalog = PackageCatalog.Instance;
            if (!packageCatalog.IsLoaded)
                packageCatalog.LoadFromAssets();
            if (packageCatalog.TryGetGcText(fileName, out var doc))
            {
                source = new GcTextSource
                {
                    FileName = doc.FileName,
                    Stem = doc.Stem,
                    Text = doc.Text,
                    SourcePath = doc.Name
                };
                return true;
            }
            return false;
        }


        public void Load()
        {
            IsLoaded = false;
            try
            {
                if (!PackageCatalog.Instance.IsLoaded)
                    PackageCatalog.Instance.LoadFromAssets();
                if (!PackageCatalog.Instance.IsLoaded)
                    throw new InvalidDataException("authored.db GC catalog is unavailable");

                using var conn = Database.GameDatabase.GetConnection();
                CreateTables(conn);
                LoadPoolTablesAndMappings();

                using (var command = conn.CreateCommand())
                {
                    command.CommandText = "SELECT COUNT(*) FROM cache.item_resolved_mods";
                    long count = (long)command.ExecuteScalar();
                    command.CommandText = "SELECT COUNT(*) FROM cache.item_wire_mods";
                    long wireCount = (long)command.ExecuteScalar();

                    bool needsRepopulate = count == 0 || wireCount == 0;
                    if (!needsRepopulate)
                    {
                        command.CommandText = "SELECT COUNT(*) FROM cache.item_resolved_mods WHERE full_gc_key='2haxemythicpal.2haxemythic101'";
                        long baselineItemCount = (long)command.ExecuteScalar();
                        needsRepopulate = baselineItemCount == 0;
                    }
                    if (!needsRepopulate)
                    {
                        command.CommandText = "SELECT COUNT(*) FROM cache.item_wire_mods WHERE full_gc_key LIKE 'items.pal.%' LIMIT 1";
                        long staleCount = (long)command.ExecuteScalar();
                        needsRepopulate = staleCount > 0;
                    }
                    if (needsRepopulate)
                    {
                        Debug.LogError($"[ITEM-STAT-DB] populate source=gc-files existingRows={count} mode=full-rebuild");
                        using (var deleteCommand = conn.CreateCommand())
                        {
                            deleteCommand.CommandText = "DELETE FROM cache.item_resolved_mods; DELETE FROM cache.item_wire_mods;";
                            deleteCommand.ExecuteNonQuery();
                        }
                        PopulateFromGCFiles(conn);
                    }
                    else
                    {
                        Debug.LogError($"[ITEM-STAT-DB] populate=False modEntries={count}");
                    }
                }

                LoadResolvedMods(conn);
                LoadWireMods(conn);
                if (_itemMods.Count == 0 || _itemWireMods.Count == 0)
                    throw new InvalidDataException($"Item stat cache is incomplete resolvedItems={_itemMods.Count} wireItems={_itemWireMods.Count}");
                ValidateCanonicalRuntimeCoverage();

                IsLoaded = true;

                Debug.LogError($"[ITEM-STAT-DB] loaded pools={_pools.Count} mappings={_poolMappings.Sum(entry => entry.Value.Count)} items={_itemMods.Count} mods={_itemMods.Values.Sum(v => v.Count)} wireItems={_itemWireMods.Count}");

                foreach (var itemKey in new[] {
                    "2haxemythicpal.2haxemythic101",
                    "magebodypal.rare001",
                    "magebodypal.unique001",
                    "platepal.plateuniquearmor1",
                    "magebodypal.normal001:magic",
                    "magebodypal.normal001:superior"
                })
                {
                    if (_itemWireMods.TryGetValue(itemKey, out var mods))
                        Debug.LogError($"[ITEM-STAT-DB] item={itemKey} wireMods={mods.Count} refs=[{string.Join(", ", mods.Select(p => p.ModRef))}]");
                    else
                        Debug.LogError($"[ITEM-STAT-DB] item={itemKey} wireMods=missing");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ITEM-STAT-DB] load error={ex.Message}\n{ex.StackTrace}");
                throw;
            }
        }


        private void PopulateFromGCFiles(SqliteConnection conn)
        {
            int itemCount = 0, modCount = 0;
            using var transaction = conn.BeginTransaction();

            foreach (var source in EnumerateGcSources("*MythicPAL.gc"))
            {
                string fileName = source.Stem;
                if (fileName.Contains("Mod")) continue;

                var items = ParseItemFile(source.Text, fileName, false);
                foreach (var (itemName, mods) in items)
                {
                    string fullKey = $"{fileName}.{itemName}".ToLowerInvariant();
                    var resolved = ResolveMods(mods);
                    InsertWireMods(conn, fullKey, mods);
                    if (resolved.Count > 0)
                    {
                        InsertResolvedMods(conn, fullKey, resolved);
                        modCount += resolved.Count;
                        itemCount++;
                    }
                }
            }

            foreach (var source in EnumerateGcSources("*PAL.gc"))
            {
                string fileName = source.Stem;
                if (fileName.Contains("Mod") || fileName.Contains("Enhancement") ||
                    fileName.Contains("Attribute") || fileName.Contains("Mythic") ||
                    fileName.Contains("Pool") || fileName.Contains("Weapon") && fileName.EndsWith("PAL")) continue;
                string peek = source.Text;
                if (!peek.Contains("MythicPreBuilt")) continue;

                var items = ParseItemFile(source.Text, fileName, true);
                foreach (var (itemName, mods) in items)
                {
                    string fullKey = $"{fileName}.{itemName}".ToLowerInvariant();
                    var resolved = ResolveMods(mods);
                    InsertWireMods(conn, fullKey, mods);
                    if (resolved.Count > 0)
                    {
                        InsertResolvedMods(conn, fullKey, resolved);
                        modCount += resolved.Count;
                        itemCount++;
                    }
                }
            }

            transaction.Commit();
            Debug.LogError($"[ITEM-STAT-DB] staticItems={itemCount} resolvedMods={modCount} generatedRows=0 source=exact-item-modifiers");
        }


        private List<(string Name, List<(int Slot, string Ref)>)> ParseItemFile(string sourceText, string fileName, bool prebuiltOnly)
        {
            var result = new List<(string, List<(int, string)>)>();
            string content = sourceText.Replace("\r", "");
            string[] lines = content.Split('\n');

            string currentItem = null;
            var currentMods = new List<(int, string)>();

            foreach (string rawLine in lines)
            {
                string t = rawLine.Trim();
                if (t.StartsWith("//") || t.StartsWith("/*")) continue;

                var itemMatch = Regex.Match(t, @"^(\w+)\s+extends\s+\S+");
                if (itemMatch.Success)
                {
                    string name = itemMatch.Groups[1].Value;
                    bool isPrebuilt = name.StartsWith("MythicPreBuilt", StringComparison.OrdinalIgnoreCase);
                    bool isNamedMythic = !isPrebuilt && !name.StartsWith("Mod") && !name.StartsWith("Description") &&
                        !name.StartsWith("One") && !name.StartsWith("Two") && !name.StartsWith("Three") &&
                        !name.StartsWith("Four") && !name.StartsWith("Five") && !name.StartsWith("static") &&
                        !name.StartsWith("Base") && name != fileName;

                    bool shouldParse = prebuiltOnly ? isPrebuilt : isNamedMythic;
                    if (shouldParse)
                    {
                        if (currentItem != null && currentMods.Count > 0)
                            result.Add((currentItem, new List<(int, string)>(currentMods)));
                        currentItem = name;
                        currentMods.Clear();
                    }
                }

                if (currentItem != null)
                {
                    var modMatch = Regex.Match(t, @"^Mod(\d+)\s+extends\s+(\S+)");
                    if (modMatch.Success)
                    {
                        int slot = int.Parse(modMatch.Groups[1].Value);
                        string extendsRef = modMatch.Groups[2].Value.TrimEnd('{', ' ');
                        if (!extendsRef.Contains("ItemModifier"))
                            currentMods.Add((slot, extendsRef));
                    }
                }
            }

            if (currentItem != null && currentMods.Count > 0)
                result.Add((currentItem, currentMods));

            return result;
        }


        private List<ResolvedMod> ResolveMods(List<(int Slot, string Ref)> mods)
        {
            var result = new List<ResolvedMod>();
            foreach (var (slot, rawRef) in mods)
            {
                GCNode modifier = ResolveModifierNode(rawRef);
                if (modifier == null)
                {
                    Debug.LogWarning($"[ITEM-STAT-DB] reason=modifier-unresolved modifier={rawRef}");
                    continue;
                }
                foreach (GCNode attribute in EnumerateAttributeNodes(modifier))
                {
                    string attributeName = NativeAttributeIdentity.ResolveName(attribute);
                    result.Add(ResolveAttribute(attribute, attributeName, slot));
                }
            }
            return result;
        }

        private ResolvedMod ResolveAttribute(GCNode attribute, string attributeName, int slot)
        {
            int poolTag = attribute.GetInt("PoolTag", -1);
            GCNode[] children = attribute.EnumerateChildrenInOrder().ToArray();
            return new ResolvedMod
            {
                ModSlot = slot,
                Attribute = attributeName,
                Pool = _pools.TryGetValue(poolTag, out PoolFormula pool) ? pool.Name : string.Empty,
                PoolTag = poolTag,
                ValueF32 = attribute.GetFixed32("Value", 0),
                ValueIncrementF32 = attribute.GetFixed32("ValueInc", 0),
                OverrideTable = attribute.GetBool("OverrideTable", false),
                Curve = children.Length == 0 ? null : new NativeCurveTable(children
                    .Where(child => NativeAuthoredClasses.IsDerivedFrom(child.NativeClassName, "CurveTableEntry"))
                    .Select(child => (child.GetFixed32("Level", 0), child.GetFixed32("Value", 0))))
            };
        }

        private static GCNode ResolveModifierNode(string rawRef)
        {
            if (string.IsNullOrWhiteSpace(rawRef)) return null;
            var gc = GCDatabase.Instance;
            string normalized = rawRef.Trim().Replace('\\', '.').Replace('/', '.');
            var candidates = new List<string> { normalized };
            if (normalized.StartsWith("items.modpal.", StringComparison.OrdinalIgnoreCase))
                candidates.Add(normalized.Substring("items.modpal.".Length));
            else
                candidates.Add("items.modpal." + normalized);
            if (normalized.StartsWith("items.pal.", StringComparison.OrdinalIgnoreCase))
                candidates.Add(normalized.Substring("items.pal.".Length));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string candidate in candidates)
            {
                if (!seen.Add(candidate)) continue;
                GCNode node = gc.ResolveWithInheritance(candidate);
                if (node != null) return node;
            }
            return null;
        }

        private static IEnumerable<GCNode> EnumerateAttributeNodes(GCNode root)
        {
            if (root == null || !NativeAuthoredClasses.IsDerivedFrom(root.NativeClassName, "ItemAttributeModifier"))
                yield break;
            GCNode description = root.GetDescription("ItemAttributeModifierDesc")
                ?? throw new InvalidDataException($"ItemAttributeModifier invalid description modifier={root.CanonicalPath}");
            foreach (GCNode child in description.EnumerateChildrenInOrder())
                if (NativeAuthoredClasses.IsDerivedFrom(child.NativeClassName, "Attribute"))
                    yield return child;
        }

        private static IEnumerable<GCNode> EnumerateNodeChildren(GCNode node)
        {
            if (node == null)
                yield break;
            foreach (GCNode child in node.EnumerateChildrenInOrder())
                yield return child;
        }

        private void LoadPoolTablesAndMappings()
        {
            var gc = GCDatabase.Instance;
            GCNode poolRoot = gc.ResolveWithInheritance("PoolTables") ?? throw new InvalidDataException("PoolTables missing");
            var pools = new Dictionary<int, PoolFormula>();
            foreach (GCNode rawPool in EnumerateNodeChildren(poolRoot))
            {
                GCNode pool = gc.ResolveWithInheritance(rawPool);
                if (pool == null || !pool.HasProperty("PoolTag")) continue;
                int poolTag = pool.GetInt("PoolTag", -1);
                if (poolTag < 0 || pools.ContainsKey(poolTag))
                    throw new InvalidDataException($"PoolTables invalid pool={rawPool.Name} tag={poolTag}");
                var points = new List<(int LevelF32, int ValueF32)>();
                foreach (GCNode rawPoint in pool.AnonymousChildren)
                {
                    GCNode point = gc.ResolveWithInheritance(rawPoint);
                    if (point == null || !point.HasProperty("Level") || !point.HasProperty("Value")) continue;
                    int levelF32 = point.GetFixed32("Level", -1);
                    int valueF32 = point.GetFixed32("Value", int.MinValue);
                    if (levelF32 < 0 || valueF32 == int.MinValue)
                        throw new InvalidDataException($"PoolTables invalid point pool={rawPool.Name}");
                    if (points.Count > 0 && levelF32 <= points[points.Count - 1].LevelF32)
                        throw new InvalidDataException($"PoolTables unordered point pool={rawPool.Name} levelF32={levelF32}");
                    points.Add((levelF32, valueF32));
                }
                if (points.Count == 0)
                    throw new InvalidDataException($"PoolTables empty pool={rawPool.Name}");
                pools[poolTag] = new PoolFormula
                {
                    PoolTag = poolTag,
                    Name = rawPool.Name,
                    Points = points.ToArray()
                };
            }
            if (pools.Count != 16)
                throw new InvalidDataException($"PoolTables count={pools.Count} expected=16");

            GCNode mappingRoot = gc.ResolveWithInheritance("PoolTableMapping") ?? throw new InvalidDataException("PoolTableMapping missing");
            var mappings = new Dictionary<string, Dictionary<int, int>>(StringComparer.OrdinalIgnoreCase);
            foreach (string itemType in new[] { "Weapon", "Shield", "Armor", "Glove", "Boot", "Helmet", "Amulet", "Ring", "Shoulder" })
            {
                GCNode typeNode = mappingRoot.GetChild(itemType) ?? throw new InvalidDataException($"PoolTableMapping.{itemType} missing");
                var values = new Dictionary<int, int>();
                foreach (GCNode rawMapping in typeNode.AnonymousChildren)
                {
                    GCNode mapping = gc.ResolveWithInheritance(rawMapping);
                    if (mapping == null || !mapping.HasProperty("PoolTag")) continue;
                    int poolTag = mapping.GetInt("PoolTag", -1);
                    if (!pools.ContainsKey(poolTag) || values.ContainsKey(poolTag))
                        throw new InvalidDataException($"PoolTableMapping invalid type={itemType} poolTag={poolTag}");
                    values[poolTag] = mapping.GetFixed32("Value", 0);
                }
                if (values.Count != pools.Count)
                    throw new InvalidDataException($"PoolTableMapping count type={itemType} count={values.Count} expected={pools.Count}");
                mappings[itemType] = values;
            }
            _pools = pools;
            _poolMappings = mappings;
        }

        private void ValidateCanonicalRuntimeCoverage()
        {
            var vector = ResolveMods(new List<(int Slot, string Ref)> { (1, "items.modpal.MageModPAL.Rare.Mod28") });
            if (vector.Count != 2
                || !vector.Any(mod => mod.Attribute == "CAST_SPEED_MOD" && mod.ValueF32 == 0x80)
                || !vector.Any(mod => mod.Attribute == "SPEEDMOD" && mod.ValueF32 == 0x80))
                throw new InvalidDataException("MageModPAL.Rare.Mod28 canonical attributes invalid");
            if (!_poolMappings.TryGetValue("Shield", out var shield)
                || !shield.TryGetValue(14, out int blockF32)
                || blockF32 != 0x100)
                throw new InvalidDataException("PoolTableMapping.Shield.BlockModPool invalid");
            Debug.LogError("[ITEM-STAT-DB] canonicalRuntime pools=16 mappings=144 castSpeedMod=True shieldBlockF32=256");
        }


        private void CreateTables(SqliteConnection conn)
        {
            using var command = conn.CreateCommand();
            command.CommandText = "CREATE TABLE IF NOT EXISTS cache.item_stat_metadata (key TEXT PRIMARY KEY, value INTEGER NOT NULL)";
            command.ExecuteNonQuery();
            command.CommandText = "SELECT value FROM cache.item_stat_metadata WHERE key='schema_version'";
            object versionValue = command.ExecuteScalar();
            int currentVersion = versionValue == null || versionValue == DBNull.Value ? 0 : Convert.ToInt32(versionValue);
            if (currentVersion != SchemaVersion)
            {
                command.CommandText = "DROP TABLE IF EXISTS cache.item_resolved_mods; DROP TABLE IF EXISTS cache.item_wire_mods;";
                command.ExecuteNonQuery();
            }
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS cache.item_resolved_mods (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    full_gc_key TEXT NOT NULL,
                    mod_slot INTEGER NOT NULL,
                    attribute TEXT NOT NULL,
                    pool_name TEXT NOT NULL,
                    pool_tag INTEGER NOT NULL,
                    value_f32 INTEGER NOT NULL,
                    value_increment_f32 INTEGER NOT NULL,
                    override_table INTEGER NOT NULL,
                    UNIQUE(full_gc_key, mod_slot, attribute, pool_tag));
                CREATE INDEX IF NOT EXISTS cache.idx_item_mods_key ON item_resolved_mods(full_gc_key);
                CREATE TABLE IF NOT EXISTS cache.item_wire_mods (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    full_gc_key TEXT NOT NULL,
                    mod_slot INTEGER NOT NULL,
                    mod_ref TEXT NOT NULL,
                    UNIQUE(full_gc_key, mod_slot));
                CREATE INDEX IF NOT EXISTS cache.idx_item_wire_mods_key ON item_wire_mods(full_gc_key);";
            command.ExecuteNonQuery();
            command.CommandText = "INSERT OR REPLACE INTO cache.item_stat_metadata (key,value) VALUES('schema_version',@version)";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("@version", SchemaVersion);
            command.ExecuteNonQuery();
        }

        private void InsertResolvedMods(SqliteConnection conn, string fullKey, List<ResolvedMod> mods)
        {
            using var command = conn.CreateCommand();
            command.CommandText = "INSERT OR IGNORE INTO cache.item_resolved_mods (full_gc_key,mod_slot,attribute,pool_name,pool_tag,value_f32,value_increment_f32,override_table) VALUES(@k,@s,@a,@p,@pt,@v,@vi,@o)";
            var keyParameter = command.Parameters.Add("@k", System.Data.DbType.String);
            var slotParameter = command.Parameters.Add("@s", System.Data.DbType.Int32);
            var attributeParameter = command.Parameters.Add("@a", System.Data.DbType.String);
            var poolParameter = command.Parameters.Add("@p", System.Data.DbType.String);
            var poolTagParameter = command.Parameters.Add("@pt", System.Data.DbType.Int32);
            var valueParameter = command.Parameters.Add("@v", System.Data.DbType.Int32);
            var incrementParameter = command.Parameters.Add("@vi", System.Data.DbType.Int32);
            var overrideParameter = command.Parameters.Add("@o", System.Data.DbType.Int32);

            keyParameter.Value = fullKey;
            foreach (var mod in mods)
            {
                slotParameter.Value = mod.ModSlot;
                attributeParameter.Value = mod.Attribute;
                poolParameter.Value = mod.Pool;
                poolTagParameter.Value = mod.PoolTag;
                valueParameter.Value = mod.ValueF32;
                incrementParameter.Value = mod.ValueIncrementF32;
                overrideParameter.Value = mod.OverrideTable ? 1 : 0;
                command.ExecuteNonQuery();
            }
        }

        private void InsertWireMods(SqliteConnection conn, string fullKey, List<(int Slot, string ModRef)> wireMods)
        {
            using var command = conn.CreateCommand();
            command.CommandText = "INSERT OR REPLACE INTO cache.item_wire_mods (full_gc_key,mod_slot,mod_ref) VALUES(@k,@s,@r)";
            var keyParameter = command.Parameters.Add("@k", System.Data.DbType.String);
            var slotParameter = command.Parameters.Add("@s", System.Data.DbType.Int32);
            var modRefParameter = command.Parameters.Add("@r", System.Data.DbType.String);
            keyParameter.Value = fullKey;
            foreach (var (slot, modRef) in wireMods)
            {
                slotParameter.Value = slot;
                modRefParameter.Value = modRef;
                command.ExecuteNonQuery();
            }
        }

        private void LoadResolvedMods(SqliteConnection conn)
        {
            _itemMods.Clear();
            using var command = conn.CreateCommand();
            command.CommandText = "SELECT full_gc_key,mod_slot,attribute,pool_name,pool_tag,value_f32,value_increment_f32,override_table FROM cache.item_resolved_mods ORDER BY full_gc_key, mod_slot, id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                string key = reader.GetString(0);
                if (!_itemMods.TryGetValue(key, out var list)) { list = new List<ResolvedMod>(); _itemMods[key] = list; }
                list.Add(new ResolvedMod
                {
                    ModSlot = reader.GetInt32(1),
                    Attribute = reader.GetString(2),
                    Pool = reader.GetString(3),
                    PoolTag = reader.GetInt32(4),
                    ValueF32 = reader.GetInt32(5),
                    ValueIncrementF32 = reader.GetInt32(6),
                    OverrideTable = reader.GetInt32(7) != 0
                });
            }
        }

        private void LoadWireMods(SqliteConnection conn)
        {
            _itemWireMods.Clear();
            using var command = conn.CreateCommand();
            command.CommandText = "SELECT full_gc_key,mod_slot,mod_ref FROM cache.item_wire_mods ORDER BY full_gc_key, mod_slot, id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                string key = reader.GetString(0);
                if (!_itemWireMods.TryGetValue(key, out var list))
                {
                    list = new List<(int, string)>();
                    _itemWireMods[key] = list;
                }
                list.Add((reader.GetInt32(1), reader.GetString(2)));
            }
        }

        public uint GetGCClassHash(string className)
        {
            if (!IsLoaded || string.IsNullOrWhiteSpace(className)) return 0;
            GCNode node = ResolveModifierNode(className);
            if (node == null || string.IsNullOrWhiteSpace(node.GCClassPath)) return 0;
            return ComputeDJB2(node.GCClassPath);
        }

        private static uint ComputeDJB2(string s)
        {
            uint hash = 0x1505;
            foreach (char character in s)
            {
                if (character > 0x7F) return 0;
                uint value = character is >= 'A' and <= 'Z' ? (uint)(character + 0x20) : character;
                hash = unchecked(hash * 0x21 + value);
            }
            return hash == 0 ? 1u : hash;
        }

        public bool TryGetItemReadDataSlotCount(string gcClass, out int slotCount)
        {
            slotCount = 0;
            if (string.IsNullOrWhiteSpace(gcClass))
                return false;

            string key = NormalizeGCClass(gcClass);
            if (_itemReadDataSlotCounts.TryGetValue(key, out slotCount))
                return true;
            if (_itemReadDataSlotMisses.Contains(key))
                return false;

            GCNode node = ResolveItemReadDataNode(key, out string resolvedKey);
            if (node == null)
            {
                _itemReadDataSlotMisses.Add(key);
                return false;
            }

            int childSlots = CountItemReadDataModifierChildren(node);
            slotCount = Math.Max(1, 1 + childSlots);
            _itemReadDataSlotCounts[key] = slotCount;
            if (!string.IsNullOrWhiteSpace(resolvedKey))
                _itemReadDataSlotCounts[resolvedKey] = slotCount;
            return true;
        }

        private GCNode ResolveItemReadDataNode(string key, out string resolvedKey)
        {
            resolvedKey = key;
            var gc = GCDatabase.Instance;
            if (gc == null || !gc.IsLoaded)
                return null;

            foreach (string candidate in GetItemReadDataCandidates(key))
            {
                var node = gc.ResolveWithInheritance(candidate);
                if (node != null)
                {
                    resolvedKey = NormalizeGCClass(candidate);
                    return node;
                }
            }
            return null;
        }

        private IEnumerable<string> GetItemReadDataCandidates(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                yield break;

            string normalized = key.Replace('\\', '.').Replace('/', '.').Trim();
            yield return normalized;

            if (normalized.StartsWith("items.pal.", StringComparison.OrdinalIgnoreCase))
            {
                yield return normalized.Substring("items.pal.".Length);
            }
            else
            {
                yield return "items.pal." + normalized;
            }
        }

        private int CountItemReadDataModifierChildren(GCNode node)
        {
            int count = 0;
            foreach (GCNode child in node.EnumerateChildrenInOrder())
                if (IsItemReadDataModifierChild(child))
                    count++;

            return count;
        }

        private bool IsItemReadDataModifierChild(GCNode node)
        {
            if (node == null || string.Equals(node.Name, "Description", StringComparison.OrdinalIgnoreCase))
                return false;
            return ExtendsItemModifier(node.Extends, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        private bool ExtendsItemModifier(string typeName, HashSet<string> visited)
        {
            if (IsItemModifierBase(typeName))
                return true;
            if (string.IsNullOrWhiteSpace(typeName))
                return false;

            string key = typeName.Trim().TrimEnd('{', ';');
            if (!visited.Add(key))
                return false;

            var gc = GCDatabase.Instance;
            if (gc == null || !gc.IsLoaded)
                return false;

            GCNode parent = gc.Resolve(key);
            if (parent == null && key.StartsWith("items.modpal.", StringComparison.OrdinalIgnoreCase))
                parent = gc.Resolve(key.Substring("items.modpal.".Length));
            if (parent == null && key.StartsWith("items.pal.", StringComparison.OrdinalIgnoreCase))
                parent = gc.Resolve(key.Substring("items.pal.".Length));
            if (parent == null)
                return false;

            if (IsItemModifierBase(parent.Name) || IsItemModifierBase(parent.Extends))
                return true;
            return ExtendsItemModifier(parent.Extends, visited);
        }

        private static bool IsItemModifierBase(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
                return false;
            string leaf = typeName.Trim();
            int dot = leaf.LastIndexOf('.');
            if (dot >= 0)
                leaf = leaf.Substring(dot + 1);
            return leaf.Equals("ItemModifier", StringComparison.OrdinalIgnoreCase) ||
                   leaf.Equals("ItemAttributeModifier", StringComparison.OrdinalIgnoreCase);
        }


        public List<(int Slot, string ModRef)> GetEffectiveItemWireMods(GCObject item)
        {
            if (!IsLoaded || item == null) return new List<(int, string)>();
            var modifiers = new List<(int Slot, string ModRef)>();
            GCNode itemNode = ResolveItemReadDataNode(item.GCClass, out _);
            if (itemNode != null)
            {
                foreach (GCNode child in itemNode.EnumerateChildrenInOrder())
                {
                    if (IsItemReadDataModifierChild(child) && IsNumberedItemModifierSlot(child))
                        modifiers.Add((modifiers.Count + 1, child.CanonicalPath));
                }
            }
            if (item.HasGeneratedItemState)
            {
                foreach (string modifier in item.GeneratedItemModifiers)
                {
                    if (!string.IsNullOrWhiteSpace(modifier))
                        modifiers.Add((modifiers.Count + 1, modifier));
                }
                return modifiers;
            }
            if (modifiers.Count > 0) return modifiers;
            if (item.GetEffectiveRarity() > 0 && !string.IsNullOrWhiteSpace(item.PresetScaleMod))
                modifiers.Add((1, item.PresetScaleMod));
            return modifiers;
        }

        public List<(int Slot, string ModRef)> GetDynamicItemWireMods(GCObject item)
        {
            if (!IsLoaded || item == null) return new List<(int, string)>();
            if (item.HasGeneratedItemState)
                return item.GeneratedItemModifiers
                    .Where(modifier => !string.IsNullOrWhiteSpace(modifier))
                    .Select((modifier, index) => (index + 1, modifier))
                    .ToList();
            if (GetItemWireMods(item.GCClass).Count > 0)
                return new List<(int, string)>();
            if (item.GetEffectiveRarity() > 0 && !string.IsNullOrWhiteSpace(item.PresetScaleMod))
                return new List<(int, string)> { (1, item.PresetScaleMod) };
            return new List<(int, string)>();
        }

        public GCNode ResolveItemModifierDescription(string modifierRef)
        {
            if (!IsLoaded)
                throw new InvalidDataException("Item stat database is not loaded");
            GCNode modifier = ResolveModifierNode(modifierRef);
            if (modifier == null)
                throw new InvalidDataException($"Unresolved item modifier '{modifierRef}'");
            GCNode description = modifier.GetDescription();
            if (description == null)
                return null;
            return GCDatabase.Instance.ResolveWithInheritance(description)
                ?? throw new InvalidDataException($"Unresolved item modifier Description '{modifierRef}'");
        }

        public IEnumerable<GCNode> GetItemModifierDescriptions(GCObject item)
        {
            if (!IsLoaded || item == null)
                yield break;
            GCNode itemNode = ResolveItemReadDataNode(item.GCClass, out _);
            if (itemNode != null)
            {
                foreach (GCNode child in itemNode.EnumerateChildrenInOrder())
                {
                    if (!IsItemReadDataModifierChild(child))
                        continue;
                    GCNode modifier = GCDatabase.Instance.ResolveWithInheritance(child);
                    GCNode description = modifier?.GetDescription();
                    if (description != null)
                        yield return GCDatabase.Instance.ResolveWithInheritance(description);
                }
            }
            foreach ((int _, string modifierRef) in GetDynamicItemWireMods(item))
            {
                GCNode description = ResolveItemModifierDescription(modifierRef);
                if (description != null)
                    yield return description;
            }
        }

        public bool TryResolveItemModifierCoverage(GCObject item, out int authoredCount, out int resolvedCount, out string reason)
        {
            authoredCount = 0;
            resolvedCount = 0;
            reason = "none";
            if (!IsLoaded || item == null)
            {
                reason = "missing-item";
                return false;
            }
            var wireMods = GetEffectiveItemWireMods(item);
            authoredCount = wireMods.Count;
            foreach (var (_, modRef) in wireMods)
                if (ResolveModifierNode(modRef) != null && GetGCClassHash(modRef) != 0)
                    resolvedCount++;
            if (resolvedCount != authoredCount)
            {
                reason = resolvedCount == 0 ? "unresolved-wire-mods" : "partial-wire-mods";
                return false;
            }
            reason = item.HasGeneratedItemState ? "generated-item-mods" : authoredCount > 0 ? "authored-item-mods" : "intrinsic-only";
            return true;
        }

        public Dictionary<string, int> GetItemStatsAtItemLevel(GCObject item, int itemLevel)
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            if (!IsLoaded || item == null || string.IsNullOrWhiteSpace(item.GCClass)) return result;
            itemLevel = itemLevel < 1 ? 1 : itemLevel;
            var mods = ResolveMods(GetEffectiveItemWireMods(item));
            mods.AddRange(ResolveIntrinsicItemMods(item));
            if (mods.Count == 0) return result;

            GCNode description = mods.Any(mod => !mod.OverrideTable) ? GCObject.ResolveItemDescription(item.GCClass) : null;
            string itemType = ResolvePoolTableItemType(description);
            bool twoHanded = IsTwoHandedWeapon(description);

            foreach (var mod in mods)
            {
                int bonus = ComputeAttributeValue(mod, itemLevel, itemType, twoHanded);
                if (result.TryGetValue(mod.Attribute, out int current))
                    result[mod.Attribute] = unchecked(current + bonus);
                else
                    result[mod.Attribute] = bonus;
            }
            return result;
        }

        private List<ResolvedMod> ResolveIntrinsicItemMods(GCObject item)
        {
            var result = new List<ResolvedMod>();
            GCNode itemNode = GCDatabase.Instance.ResolveWithInheritance(item.GCClass)
                ?? GCDatabase.Instance.ResolveWithInheritance(GCObject.GetPacketGCClassFor(item.GCClass));
            if (itemNode == null) return result;
            int intrinsicSlot = 0;
            foreach (GCNode rawModifier in itemNode.EnumerateChildrenInOrder())
            {
                if (!IsItemReadDataModifierChild(rawModifier) || IsNumberedItemModifierSlot(rawModifier))
                    continue;
                GCNode modifier = GCDatabase.Instance.ResolveWithInheritance(rawModifier);
                if (modifier == null) continue;
                foreach (GCNode attribute in EnumerateAttributeNodes(modifier))
                {
                    string attributeName = NativeAttributeIdentity.ResolveName(attribute);
                    result.Add(ResolveAttribute(attribute, attributeName, intrinsicSlot));
                }
                intrinsicSlot--;
            }
            return result;
        }

        private static bool IsNumberedItemModifierSlot(GCNode node)
        {
            string name = node?.Name;
            if (string.IsNullOrEmpty(name) || name.Length <= 3 || !name.StartsWith("Mod", StringComparison.OrdinalIgnoreCase))
                return false;
            for (int index = 3; index < name.Length; index++)
                if (name[index] < '0' || name[index] > '9')
                    return false;
            return true;
        }

        private int ComputeAttributeValue(ResolvedMod mod, int itemLevel, string itemType, bool twoHanded)
        {
            int levelF32 = (Math.Max(1, itemLevel) & 0xFF) << 8;
            int attributeValue;
            if (!mod.OverrideTable && mod.Curve != null)
            {
                int curveValueF32 = mod.Curve.EvalFixed32(levelF32);
                if ((curveValueF32 & 0xFF) >= 0x7F)
                    curveValueF32 = unchecked(curveValueF32 + 0x100);
                attributeValue = curveValueF32 >> 8;
            }
            else
            {
                if (levelF32 >= 0x100) levelF32 -= 0x100;
                attributeValue = unchecked(mod.ValueF32 + FixedMultiplyF32(levelF32, mod.ValueIncrementF32)) >> 8;
            }

            int resultF32 = unchecked((short)attributeValue) << 8;
            if (mod.OverrideTable || itemType == null) return resultF32 >> 8;
            int mappingF32 = ResolveMappingValue(itemType, mod.PoolTag);
            if (twoHanded) mappingF32 = unchecked(mappingF32 + ResolveMappingValue("Shield", mod.PoolTag));
            resultF32 = FixedMultiplyF32(resultF32, mod.ValueF32);
            resultF32 = FixedMultiplyF32(resultF32, mappingF32);
            if (resultF32 < 0x100) resultF32 = 0x100;
            return resultF32 >> 8;
        }

        private static int FixedMultiplyF32(int leftF32, int rightF32)
        {
            return unchecked((int)(((long)leftF32 * rightF32) >> 8));
        }

        private int ResolveMappingValue(string itemType, int poolTag)
        {
            return _poolMappings.TryGetValue(itemType, out var mapping) && mapping.TryGetValue(poolTag, out int value) ? value : 0;
        }

        private static string ResolvePoolTableItemType(GCNode description)
        {
            if (description == null) return null;
            byte slot = unchecked((byte)description.GetInt("SlotType", 0));
            return slot switch
            {
                10 => "Weapon",
                11 => "Shield",
                6 => "Armor",
                2 => "Glove",
                7 => "Boot",
                5 => "Helmet",
                1 => "Amulet",
                3 or 4 => "Ring",
                8 => "Shoulder",
                0 or 9 => string.Empty,
                _ => throw new InvalidDataException($"ItemAttributeModifier invalid SlotType={slot}")
            };
        }

        private static bool IsTwoHandedWeapon(GCNode description)
        {
            if (description == null || unchecked((byte)description.GetInt("SlotType", 0)) != 10
                || !NativeAuthoredClasses.IsDerivedFrom(description.NativeClassName, "WeaponDesc")) return false;
            int weaponClass = description.GetString("WeaponClass", "HTH") switch
            {
                "HTH" => 1,
                "1HMELEE" => 5,
                "2HMELEE" => 6,
                "1HRANGED" => 9,
                "2HRANGED" => 3,
                "POLEARM" => 8,
                "2HCANNON" => 13,
                _ => description.GetInt("WeaponClass", 1)
            };
            return unchecked((byte)weaponClass) is 3 or 6 or 8 or 13;
        }

        public List<(int Slot, string ModRef)> GetItemWireMods(string gcClass)
        {
            if (!IsLoaded || string.IsNullOrEmpty(gcClass)) return new List<(int, string)>();
            string key = NormalizeGCClass(gcClass);
            if (_itemWireMods.TryGetValue(key, out var list))
                return new List<(int, string)>(list);
            return new List<(int, string)>();
        }

        private string NormalizeGCClass(string gcClass)
        {
            string lower = gcClass.ToLowerInvariant();
            if (lower.StartsWith("items.pal."))
                lower = lower.Substring("items.pal.".Length);
            return lower;
        }

        public static string ExtractPattern(string gcClass)
        {
            if (string.IsNullOrEmpty(gcClass)) return "";
            string[] parts = gcClass.Split('.');
            return parts.Length > 0 ? parts[parts.Length - 1] : gcClass;
        }
    }
}
