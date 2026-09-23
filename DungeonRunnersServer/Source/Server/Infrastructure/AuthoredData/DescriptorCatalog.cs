using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DungeonRunners.Engine;

namespace DungeonRunners.Data
{
    public sealed class DescriptorCatalog
    {
        private static DescriptorCatalog _instance;
        public static DescriptorCatalog Instance => _instance ??= new DescriptorCatalog();

        public readonly List<DescriptorRecord> Records = new List<DescriptorRecord>();
        public readonly List<DescriptorRecord> EncounterTables = new List<DescriptorRecord>();
        public readonly List<DescriptorRecord> ZoneWorldDocs = new List<DescriptorRecord>();
        public readonly List<DescriptorRecord> ZoneDefinitions = new List<DescriptorRecord>();
        public readonly List<DescriptorRecord> TileDefinitions = new List<DescriptorRecord>();
        public readonly List<DescriptorRecord> StaticObjects = new List<DescriptorRecord>();

        private bool _built;
        private string _source = "unknown";

        public void BuildFromGCDatabase(GCDatabase db)
        {
            Records.Clear();
            EncounterTables.Clear();
            ZoneWorldDocs.Clear();
            ZoneDefinitions.Clear();
            TileDefinitions.Clear();
            StaticObjects.Clear();
            _built = false;

            if (db == null || !db.IsLoaded)
                return;

            PackageCatalog packageCatalog = PackageCatalog.Instance;
            if (!packageCatalog.IsLoaded)
                throw new InvalidDataException("Package catalog is not loaded");

            var seenEntries = new HashSet<int>();
            foreach (PackageTextDocument document in packageCatalog.RuntimeTextDocuments.OrderBy(document => document.EntryIndex))
            {
                if (document == null || !seenEntries.Add(document.EntryId))
                    throw new InvalidDataException($"Duplicate or missing authored document identity entryId={document?.EntryId ?? 0}");
                GCNode node = db.Resolve(document.GcPath);
                if (node == null)
                    throw new InvalidDataException($"Authored document is not registered entryId={document.EntryId} entryIndex={document.EntryIndex} path='{document.GcPath}'");
                if (node.PackageEntryId != document.EntryId)
                    throw new InvalidDataException($"Authored document identity mismatch entryId={document.EntryId} resolvedEntryId={node.PackageEntryId} path='{document.GcPath}'");

                var record = CreateRecord(document, node);
                Records.Add(record);
                if (record.Kind == DescriptorKind.EncounterTable)
                    EncounterTables.Add(record);
                else if (record.Kind == DescriptorKind.ZoneWorld)
                    ZoneWorldDocs.Add(record);
                else if (record.Kind == DescriptorKind.ZoneDefinition)
                    ZoneDefinitions.Add(record);
                else if (record.Kind == DescriptorKind.TileDefinition)
                    TileDefinitions.Add(record);
                else if (record.Kind == DescriptorKind.StaticObject)
                    StaticObjects.Add(record);
            }

            _built = true;
            _source = "PKI-PKG-AUTHORED-DB";
            Debug.LogError($"[CLIENT-DESCRIPTOR-CATALOG] records={Records.Count}/{packageCatalog.RuntimeTextDocumentCount} world={ZoneWorldDocs.Count}/{AuthoredSnapshotCatalog.Instance.WorldDocuments.Count} zones={ZoneDefinitions.Count}/{AuthoredSnapshotCatalog.Instance.ZoneDocuments.Count} tiles={TileDefinitions.Count}/{AuthoredSnapshotCatalog.Instance.TileDocuments.Count} encounterTables={EncounterTables.Count} staticObjects={StaticObjects.Count} source={_source} sourceFunction=GCClassRegistry+DFCClass");
            bool dewValleyBossZonePresent = ContainsZone("dungeon00_level03_boss");
            bool level03MasterPresent = ContainsRecord("level03_master_encounter");
            bool level04MasterPresent = ContainsRecord("level04_master_encounter");
            Debug.LogError($"[CLIENT-DESCRIPTOR-CATALOG] dewValleyBossZonePresent={dewValleyBossZonePresent} level03MasterPresent={level03MasterPresent} level04MasterPresent={level04MasterPresent} packageDocumentCoverage=complete pendingSpawnUnit=unresolved encounterDifficultyConsumer=unresolved");
        }

        public void RunStartupCheck()
        {
            if (!_built)
                BuildFromGCDatabase(GCDatabase.Instance);

            int wetLike = Records.Count(r => r.HasProperty("WorldEntityTable") || r.HasProperty("WorldEntityGenerator") || r.HasProperty("TableSelector"));
            int lootLike = Records.Count(r => r.AuthoredName.IndexOf("loot", StringComparison.OrdinalIgnoreCase) >= 0 || r.HasProperty("TreasureGenerator") || r.HasProperty("TreasureGenerator2"));
            int monsterLike = Records.Count(r => r.HasProperty("Behavior") || r.HasProperty("CreatureType") || r.HasProperty("CollisionRadius"));
            int expectedRecords = PackageCatalog.Instance.RuntimeTextDocumentCount;
            int expectedWorlds = AuthoredSnapshotCatalog.Instance.WorldDocuments.Count;
            int expectedZones = AuthoredSnapshotCatalog.Instance.ZoneDocuments.Count;
            int expectedTiles = AuthoredSnapshotCatalog.Instance.TileDocuments.Count;
            if (Records.Count != expectedRecords
                || Records.Select(record => record.PackageEntryId).Distinct().Count() != expectedRecords
                || ZoneWorldDocs.Count != expectedWorlds
                || ZoneDefinitions.Count != expectedZones
                || TileDefinitions.Count != expectedTiles)
                throw new InvalidDataException($"Authored descriptor coverage mismatch records={Records.Count}/{expectedRecords} world={ZoneWorldDocs.Count}/{expectedWorlds} zones={ZoneDefinitions.Count}/{expectedZones} tiles={TileDefinitions.Count}/{expectedTiles}");
            Debug.LogError($"[CLIENT-DESCRIPTOR-CATALOG] records={Records.Count}/{expectedRecords} world={ZoneWorldDocs.Count}/{expectedWorlds} zones={ZoneDefinitions.Count}/{expectedZones} tiles={TileDefinitions.Count}/{expectedTiles} encounterTables={EncounterTables.Count} wetLike={wetLike} lootLike={lootLike} monsterLike={monsterLike} status=complete source={_source}");
        }

        public bool ContainsZone(string name)
        {
            return ZoneWorldDocs.Any(r => string.Equals(r.AuthoredName, name, StringComparison.OrdinalIgnoreCase));
        }

        public bool ContainsRecord(string name)
        {
            return Records.Any(r => string.Equals(r.AuthoredName, name, StringComparison.OrdinalIgnoreCase));
        }

        private static DescriptorRecord CreateRecord(PackageTextDocument document, GCNode node)
        {
            var record = new DescriptorRecord
            {
                AuthoredName = document.Name,
                RegistryPath = document.GcPath,
                ExtendsPath = node.Extends ?? "",
                PackageEntryId = document.EntryId,
                PackageEntryIndex = document.EntryIndex,
                TypeCode = document.TypeCode,
                Kind = Classify(document, node)
            };

            foreach (var propertyEntry in node.Properties)
                record.Properties[propertyEntry.Key] = propertyEntry.Value;
            CollectProperties(node, record);
            return record;
        }

        private static void CollectProperties(GCNode node, DescriptorRecord record)
        {
            if (node == null)
                return;
            int anonymousChildIndex = 0;
            foreach (GCNode child in node.EnumerateChildrenInOrder())
            {
                string childPrefix = child.IsAnonymous
                    ? "*." + (anonymousChildIndex++).ToString(CultureInfo.InvariantCulture)
                    : child.Name;
                foreach (var propertyEntry in child.Properties)
                    AddProperty(record, $"{childPrefix}.{propertyEntry.Key}", propertyEntry.Value);
                CollectProperties(child, record);
            }
        }

        private static DescriptorKind Classify(PackageTextDocument document, GCNode node)
        {
            if (document.TypeCode == 15)
                return DescriptorKind.ZoneWorld;
            if (document.TypeCode == 16)
                return DescriptorKind.ZoneDefinition;
            if (document.TypeCode == 14)
                return DescriptorKind.TileDefinition;
            string name = document.Name ?? "";
            string extends = node.Extends ?? "";
            if (extends.IndexOf("EncounterTable", StringComparison.OrdinalIgnoreCase) >= 0)
                return DescriptorKind.EncounterTable;
            if (extends.IndexOf("StaticObject", StringComparison.OrdinalIgnoreCase) >= 0 || node.GetChild("Description")?.HasProperty("CollisionObject") == true)
                return DescriptorKind.StaticObject;
            if (node.HasProperty("Behavior") || node.GetChild("Description")?.HasProperty("CollisionRadius") == true)
                return DescriptorKind.Monster;
            if (name.IndexOf("loot", StringComparison.OrdinalIgnoreCase) >= 0)
                return DescriptorKind.Loot;
            return DescriptorKind.Other;
        }

        private static void AddProperty(DescriptorRecord record, string key, string value)
        {
            if (!record.Properties.ContainsKey(key))
                record.Properties[key] = value;
        }
    }

    public sealed class DescriptorRecord
    {
        public string AuthoredName;
        public string RegistryPath;
        public string ExtendsPath;
        public int PackageEntryId;
        public int PackageEntryIndex;
        public int TypeCode;
        public DescriptorKind Kind;
        public readonly Dictionary<string, string> Properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public bool HasProperty(string name)
        {
            return Properties.ContainsKey(name) || Properties.Keys.Any(k => k.EndsWith("." + name, StringComparison.OrdinalIgnoreCase));
        }
    }

    public enum DescriptorKind
    {
        Other,
        ZoneWorld,
        ZoneDefinition,
        TileDefinition,
        EncounterTable,
        Monster,
        StaticObject,
        Loot
    }
}
