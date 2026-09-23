using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using DungeonRunners.Data;
using DungeonRunners.Engine;
using DungeonRunners.Gameplay;

public static class AuthoredGameplayCatalog
{
    private sealed class OrderedRoot
    {
        public PackageTextDocument Document;
        public GCNode Node;
    }

    public static readonly Dictionary<string, List<DungeonSpawnData>> DungeonSpawns = new(StringComparer.OrdinalIgnoreCase);
    public static readonly List<ItemData> AllArmor = new();
    public static readonly List<ItemData> AllWeapons = new();
    public static readonly List<GeneralItemData> Items = new();
    public static readonly Dictionary<string, ItemData> ItemDatabase = new(StringComparer.OrdinalIgnoreCase);
    public static readonly List<SkillData> Skills = new();
    public static readonly List<QuestData> Quests = new();
    public static readonly Dictionary<uint, QuestData> QuestsByHash = new();
    public static readonly Dictionary<string, List<QuestData>> QuestOffersByNpc = new(StringComparer.OrdinalIgnoreCase);
    public static readonly List<CheckpointData> Checkpoints = new();
    public static readonly List<NPCData> TownNPCs = new();
    public static readonly List<NPCData> TutorialNPCs = new();
    public static readonly List<NPCData> PvpNPCs = new();
    public static readonly Dictionary<string, List<NPCData>> NPCsByZone = new(StringComparer.OrdinalIgnoreCase);
    public static readonly List<CreatureData> Creatures = new();
    public static readonly List<SummonedUnitData> SummonedUnits = new();
    public static readonly Dictionary<string, CreatureData> CreatureDatabase = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, CreatureData> ReferencedCreatures = new(StringComparer.OrdinalIgnoreCase);
    public static readonly Dictionary<string, SummonedUnitData> SummonedUnitDatabase = new(StringComparer.OrdinalIgnoreCase);
    public static readonly List<ZonePortalData> ZonePortals = new();
    public static readonly List<ZoneWaypointData> ZoneWaypoints = new();
    public static readonly Dictionary<string, List<ZonePortalData>> PortalsByZone = new(StringComparer.OrdinalIgnoreCase);
    public static readonly Dictionary<string, List<ZoneWaypointData>> WaypointsByZone = new(StringComparer.OrdinalIgnoreCase);
    public static readonly Dictionary<string, List<ZoneWaypointData>> QuestLocationsByZone = new(StringComparer.OrdinalIgnoreCase);
    public static readonly Dictionary<string, List<ZoneWaypointData>> QuestLocationsByTile = new(StringComparer.OrdinalIgnoreCase);
    public static readonly List<ZoneCheckpointData> ZoneCheckpoints = new();
    public static readonly Dictionary<string, List<ZoneCheckpointData>> CheckpointsByZone = new(StringComparer.OrdinalIgnoreCase);
    public static readonly Dictionary<string, CheckpointData> CheckpointDatabase = new(StringComparer.OrdinalIgnoreCase);
    public static readonly Dictionary<string, List<ItemData>> Helmets = new(StringComparer.OrdinalIgnoreCase);
    public static readonly Dictionary<string, List<ItemData>> Armors = new(StringComparer.OrdinalIgnoreCase);
    public static readonly Dictionary<string, List<ItemData>> Gloves = new(StringComparer.OrdinalIgnoreCase);
    public static readonly Dictionary<string, List<ItemData>> Boots = new(StringComparer.OrdinalIgnoreCase);
    public static readonly Dictionary<string, List<ItemData>> MeleeWeapons = new(StringComparer.OrdinalIgnoreCase);
    public static readonly Dictionary<string, List<ItemData>> RangedWeapons = new(StringComparer.OrdinalIgnoreCase);
    public static readonly List<GeneralItemData> QuestItems = new();
    public static readonly List<GeneralItemData> Relics = new();
    public static readonly List<GeneralItemData> Rings = new();
    public static readonly List<GeneralItemData> Amulets = new();
    public static readonly List<GeneralItemData> Potions = new();
    public static readonly List<GeneralItemData> Skillbooks = new();
    public static readonly List<GeneralItemData> Keys = new();
    public static readonly List<GeneralItemData> Scrolls = new();
    public static readonly List<GeneralItemData> DungeonItems = new();
    public static readonly List<GeneralItemData> Vouchers = new();
    public static readonly List<GeneralItemData> ItemPacks = new();
    public static readonly List<GeneralItemData> Consumables = new();
    public static readonly Dictionary<string, GeneralItemData> GeneralItemDatabase = new(StringComparer.OrdinalIgnoreCase);
    public static readonly List<MerchantData> Merchants = new();
    public static readonly Dictionary<string, MerchantData> MerchantsByNpc = new(StringComparer.OrdinalIgnoreCase);
    public static readonly Dictionary<string, List<QuestKillDropEntry>> QuestKillDropsByMonster = new(StringComparer.OrdinalIgnoreCase);
    public static readonly Dictionary<string, List<QuestActivateDropEntry>> QuestActivateDropsByQuest = new(StringComparer.OrdinalIgnoreCase);
    public static readonly List<AuthoredZoneData> Zones = new();
    public static readonly Dictionary<string, AuthoredZoneData> ZonesByName = new(StringComparer.OrdinalIgnoreCase);
    public static readonly Dictionary<string, ClassDefinition> ClassDefinitions = new(StringComparer.OrdinalIgnoreCase);
    public static readonly List<string> StartingCheckpointIds = new();
    public static readonly Dictionary<string, List<WorldEntityData>> WorldEntitiesByZone = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsLoaded { get; private set; }

    public static void LoadAll()
    {
        IsLoaded = false;
        if (!PackageCatalog.Instance.IsLoaded)
            throw new InvalidDataException("PKI/PKG package catalog is not loaded");
        if (!GCDatabase.Instance.IsLoaded)
            throw new InvalidDataException("PKI/PKG gameplay graph is not loaded");

        Clear();
        List<OrderedRoot> roots = GetOrderedRoots();
        LoadZones(roots);
        LoadClassDefinitions(roots);
        LoadCheckpointDefinitions(roots);
        LoadCreaturesAndSummonedUnits(roots);
        LoadItems(roots);
        LoadSkills(roots);
        LoadQuests(roots);
        LoadMerchants(roots);
        LoadReferencedItems();
        LoadWorldPlacements(roots);
        LoadQuestTileLocations(roots);
        LinkAuthoredNpcQuestGivers(roots);
        FinalizeZoneSpawns();
        BuildEquipmentMappings();
        ValidateCoverage(roots.Count);
        IsLoaded = true;
        Debug.LogError($"[AUTHORED-GAMEPLAY] roots={roots.Count} zones={Zones.Count} creatures={Creatures.Count} summonedUnits={SummonedUnits.Count} items={GeneralItemDatabase.Count} equipment={ItemDatabase.Count} skills={Skills.Count} quests={Quests.Count} checkpoints={Checkpoints.Count} merchants={Merchants.Count} npcs={NPCsByZone.Sum(entry => entry.Value.Count)} worldEntities={WorldEntitiesByZone.Sum(entry => entry.Value.Count)} source=pki-pkg-graph");
    }

    public static GeneralItemData FindGeneralItem(string gcType)
    {
        return TryLookup(GeneralItemDatabase, gcType, out GeneralItemData item) ? item : null;
    }

    public static ItemData FindItem(string gcType)
    {
        return TryLookup(ItemDatabase, gcType, out ItemData item) ? item : null;
    }

    public static CreatureData FindCreature(string gcType)
    {
        if (TryLookup(CreatureDatabase, gcType, out CreatureData creature)) return creature;
        if (string.IsNullOrWhiteSpace(gcType) || !GCDatabase.Instance.IsLoaded) return null;
        if (ReferencedCreatures.TryGetValue(gcType, out creature)) return creature;
        GCNode node = GCDatabase.Instance.Resolve(gcType);
        if (node == null || IsNpc(node) || !AuthoredWorldLayout.IsKindOf(node, "StockUnit")
            || AuthoredWorldLayout.IsKindOf(node, "base.Summonable")) return null;
        return ReferencedCreatures.GetOrAdd(gcType, _ => BuildCreatureData(node.CanonicalPath, Effective(node)));
    }

    public static SummonedUnitData FindSummonedUnit(string gcType)
    {
        return TryLookup(SummonedUnitDatabase, gcType, out SummonedUnitData summonedUnit) ? summonedUnit : null;
    }

    public static List<CreatureData> GetCreaturesByFaction(string faction)
    {
        return Creatures.Where(creature => string.Equals(creature.faction, faction, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public static List<CreatureData> GetCreaturesByTier(string tier)
    {
        return Creatures.Where(creature => string.Equals(creature.creatureDifficulty, tier, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public static List<CreatureData> GetCreaturesByElement(string element)
    {
        return Creatures.Where(creature => string.Equals(creature.element, element, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public static uint ComputeDJB2Hash(string value)
    {
        uint hash = 5381;
        foreach (char character in (value ?? string.Empty).ToLowerInvariant())
            hash = unchecked(hash * 33 + character);
        return hash == 0 ? 1u : hash;
    }

    public static List<ZoneCheckpointData> GetCheckpointsForZone(string zoneName)
    {
        return !string.IsNullOrWhiteSpace(zoneName) && CheckpointsByZone.TryGetValue(zoneName, out List<ZoneCheckpointData> checkpoints)
            ? checkpoints
            : new List<ZoneCheckpointData>();
    }

    public static ZoneCheckpointData GetCheckpointByGcType(string gcType)
    {
        if (string.IsNullOrWhiteSpace(gcType))
            return null;
        foreach (List<ZoneCheckpointData> checkpoints in CheckpointsByZone.Values)
            foreach (ZoneCheckpointData checkpoint in checkpoints)
                if (string.Equals(checkpoint.gcType, gcType, StringComparison.OrdinalIgnoreCase))
                    return checkpoint;
        return null;
    }

    public static CheckpointData FindCheckpoint(string checkpointId)
    {
        return !string.IsNullOrWhiteSpace(checkpointId) && CheckpointDatabase.TryGetValue(checkpointId, out CheckpointData checkpoint)
            ? checkpoint
            : null;
    }

    public static List<ZonePortalData> GetPortalsForZone(string zoneName)
    {
        return !string.IsNullOrWhiteSpace(zoneName) && PortalsByZone.TryGetValue(zoneName, out List<ZonePortalData> portals)
            ? portals
            : new List<ZonePortalData>();
    }

    public static List<ZoneWaypointData> GetWaypointsForZone(string zoneName)
    {
        return !string.IsNullOrWhiteSpace(zoneName) && WaypointsByZone.TryGetValue(zoneName, out List<ZoneWaypointData> waypoints)
            ? waypoints
            : new List<ZoneWaypointData>();
    }

    private static void Clear()
    {
        DungeonSpawns.Clear();
        AllArmor.Clear();
        AllWeapons.Clear();
        Items.Clear();
        ItemDatabase.Clear();
        Skills.Clear();
        Quests.Clear();
        QuestsByHash.Clear();
        QuestOffersByNpc.Clear();
        Checkpoints.Clear();
        TownNPCs.Clear();
        TutorialNPCs.Clear();
        PvpNPCs.Clear();
        NPCsByZone.Clear();
        Creatures.Clear();
        SummonedUnits.Clear();
        CreatureDatabase.Clear();
        ReferencedCreatures.Clear();
        SummonedUnitDatabase.Clear();
        ZonePortals.Clear();
        ZoneWaypoints.Clear();
        PortalsByZone.Clear();
        WaypointsByZone.Clear();
        QuestLocationsByZone.Clear();
        QuestLocationsByTile.Clear();
        ZoneCheckpoints.Clear();
        CheckpointsByZone.Clear();
        CheckpointDatabase.Clear();
        Helmets.Clear();
        Armors.Clear();
        Gloves.Clear();
        Boots.Clear();
        MeleeWeapons.Clear();
        RangedWeapons.Clear();
        QuestItems.Clear();
        Relics.Clear();
        Rings.Clear();
        Amulets.Clear();
        Potions.Clear();
        Skillbooks.Clear();
        Keys.Clear();
        Scrolls.Clear();
        DungeonItems.Clear();
        Vouchers.Clear();
        ItemPacks.Clear();
        Consumables.Clear();
        GeneralItemDatabase.Clear();
        Merchants.Clear();
        MerchantsByNpc.Clear();
        QuestKillDropsByMonster.Clear();
        QuestActivateDropsByQuest.Clear();
        Zones.Clear();
        ZonesByName.Clear();
        ClassDefinitions.Clear();
        StartingCheckpointIds.Clear();
        WorldEntitiesByZone.Clear();
    }

    private static List<OrderedRoot> GetOrderedRoots()
    {
        var roots = new List<OrderedRoot>();
        var seenEntries = new HashSet<int>();
        foreach (PackageTextDocument document in PackageCatalog.Instance.RuntimeTextDocuments.OrderBy(document => document.EntryIndex))
        {
            if (document == null || !seenEntries.Add(document.EntryId))
                continue;
            GCNode node = GCDatabase.Instance.Resolve(document.GcPath);
            if (node == null)
                throw new InvalidDataException($"Authored root is not registered entry={document.EntryId} ordinal={document.EntryIndex} path='{document.GcPath}'");
            roots.Add(new OrderedRoot { Document = document, Node = node });
        }
        return roots;
    }

    private static void LoadZones(IEnumerable<OrderedRoot> roots)
    {
        foreach (OrderedRoot root in roots.Where(root => root.Document.TypeCode == 16))
        {
            GCNode effective = Effective(root.Node);
            string name = effective.GetString("Name", root.Document.Name).Trim();
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidDataException($"Zone name is missing at '{root.Node.CanonicalPath}'");
            if (ZonesByName.ContainsKey(name))
                throw new InvalidDataException($"Duplicate authored zone name '{name}'");
            var zone = new AuthoredZoneData
            {
                Id = StableZoneId(name),
                Name = name,
                GcType = root.Node.CanonicalPath,
                Label = effective.GetString("Label", name),
                Private = effective.GetBool("Private", false),
                RespawnZone = effective.GetString("RespawnZone", "Town"),
                RespawnSpawnPoint = effective.GetString("RespawnSpawnPoint", "Start"),
                ExploredBitCount = effective.GetInt("ExploredBitCount", 0)
            };
            Zones.Add(zone);
            ZonesByName.Add(zone.Name, zone);
        }
    }

    private static void LoadClassDefinitions(IEnumerable<OrderedRoot> roots)
    {
        foreach (OrderedRoot root in roots.Where(root => string.Equals(root.Node.Extends, "avatar.base.Avatar", StringComparison.OrdinalIgnoreCase)))
        {
            string classPath = root.Node.CanonicalPath;
            GCNode classNode = Effective(root.Node);
            GCNode description = RequireChild(classNode, "Description", classPath);
            string displayName = RequireString(description, "Name", classPath + ".Description");
            string skillsPath = RequireString(description, "StartingSkillsList", classPath + ".Description");
            string equipmentPath = RequireString(description, "StartingEquipmentList", classPath + ".Description");
            string inventoryPath = RequireString(description, "StartingInventoryList", classPath + ".Description");
            string checkpointPath = RequireString(description, "StartingCheckpointList", classPath + ".Description");
            List<string> startingCheckpoints = ReadStartingCheckpoints(checkpointPath);
            var definition = new ClassDefinition
            {
                displayName = displayName,
                description = description.GetString("Description", string.Empty),
                startingEquipment = ReadStartingEquipment(equipmentPath),
                startingSkills = ReadStartingSkills(skillsPath),
                startingInventory = ReadStartingInventory(inventoryPath)
            };
            if (definition.startingSkills.Count == 0 || definition.startingInventory.Count == 0 || definition.startingEquipment.slotLevel.Count == 0)
                throw new InvalidDataException($"Class authored lists are empty for '{displayName}'");
            if (startingCheckpoints.Count == 0)
                throw new InvalidDataException($"Class authored starting checkpoints are empty for '{displayName}'");
            if (StartingCheckpointIds.Count == 0)
                StartingCheckpointIds.AddRange(startingCheckpoints);
            else if (!StartingCheckpointIds.SequenceEqual(startingCheckpoints, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException($"Class authored starting checkpoints differ for '{displayName}'");
            ClassDefinitions.Add(displayName, definition);
        }
    }

    private static StartingEquipment ReadStartingEquipment(string path)
    {
        GCNode node = RequireRaw(path);
        var equipment = new StartingEquipment();
        foreach (GCNode child in node.EnumerateChildrenInOrder())
        {
            if (string.IsNullOrWhiteSpace(child.Extends))
                continue;
            int id = RequireInt(child, "ID", child.CanonicalPath);
            int level = RequireInt(child, "Level", child.CanonicalPath);
            string slot = id switch
            {
                10 => "weapon",
                6 => "armor",
                2 => "gloves",
                7 => "boots",
                _ => throw new InvalidDataException($"Unknown starting equipment slot id={id} path='{child.CanonicalPath}'")
            };
            switch (slot)
            {
                case "weapon": equipment.weapon = child.Extends; break;
                case "armor": equipment.armor = child.Extends; break;
                case "gloves": equipment.gloves = child.Extends; break;
                case "boots": equipment.boots = child.Extends; break;
            }
            equipment.slotLevel.Add(slot, level);
        }
        return equipment;
    }

    private static List<string> ReadStartingSkills(string path)
    {
        return RequireRaw(path).EnumerateChildrenInOrder()
            .Where(child => !string.IsNullOrWhiteSpace(child.Extends))
            .Select(child => child.Extends)
            .Where(skill => !skill.StartsWith("skills.professions.", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static List<string> ReadStartingCheckpoints(string path)
    {
        return RequireRaw(path).EnumerateChildrenInOrder()
            .Where(child => !string.IsNullOrWhiteSpace(child.Extends))
            .Select(child => child.Extends)
            .ToList();
    }

    private static List<StartingInventoryItem> ReadStartingInventory(string path)
    {
        var result = new List<StartingInventoryItem>();
        foreach (GCNode child in RequireRaw(path).EnumerateChildrenInOrder())
        {
            if (string.IsNullOrWhiteSpace(child.Extends))
                continue;
            int x = RequireInt(child, "InventoryX", child.CanonicalPath);
            int y = RequireInt(child, "InventoryY", child.CanonicalPath);
            int quantity = child.HasProperty("Quantity") ? RequireInt(child, "Quantity", child.CanonicalPath) : 1;
            if ((uint)x > byte.MaxValue || (uint)y > byte.MaxValue || quantity < 1)
                throw new InvalidDataException($"Invalid starting inventory placement path='{child.CanonicalPath}' x={x} y={y} quantity={quantity}");
            result.Add(new StartingInventoryItem { gcClass = child.Extends, x = (byte)x, y = (byte)y, count = quantity });
        }
        return result;
    }

    private static void LoadCheckpointDefinitions(IEnumerable<OrderedRoot> roots)
    {
        foreach (OrderedRoot root in roots)
        {
            if (!InheritanceContains(root.Node, "checkpoint") || InheritanceContains(root.Node, "checkpointentity"))
                continue;
            GCNode effective = Effective(root.Node);
            GCNode description = effective.GetChild("Description") ?? effective;
            if (!description.HasProperty("Zone") || !description.HasProperty("SpawnPoint"))
                continue;
            string id = root.Node.CanonicalPath;
            var checkpoint = new CheckpointData
            {
                id = id,
                name = Leaf(id),
                description = description.GetString("Description", string.Empty),
                zone = RequireString(description, "Zone", id + ".Description"),
                mapId = description.GetString("MapID", string.Empty),
                spawnPoint = RequireString(description, "SpawnPoint", id + ".Description"),
                position = new PositionData(),
                rotation = new PositionData(),
                order = description.GetInt("Order", 0),
                isActive = description.GetBool("IsActive", true),
                levelRequirement = description.GetInt("LevelRequirement", 1),
                unlockQuest = description.GetString("UnlockQuest", string.Empty),
                image = description.GetString("Image", string.Empty)
            };
            Checkpoints.Add(checkpoint);
            CheckpointDatabase.Add(id, checkpoint);
        }
    }

    private static void LoadCreaturesAndSummonedUnits(IEnumerable<OrderedRoot> roots)
    {
        foreach (OrderedRoot root in roots)
        {
            GCNode effective = Effective(root.Node);
            GCNode description = effective.GetChild("Description") ?? effective;
            if (IsSummonedUnit(root.Node))
            {
                var summonedUnit = new SummonedUnitData
                {
                    unit = BuildCreatureData(root.Node.CanonicalPath, effective),
                    gcType = root.Node.CanonicalPath,
                    behaviourType = ResolveBehaviorType(effective),
                    name = description.GetString("Label", description.GetString("Name", Leaf(root.Node.CanonicalPath))),
                    maxHealthFixed32 = description.GetFixed32("MaxHealth", 0),
                    maxManaFixed32 = description.GetFixed32("MaxMana", 0),
                    lifespanFixed32 = description.GetFixed32("Lifespan", 0),
                    lifespanIncrementFixed32 = description.GetFixed32("LifespanInc", 0),
                    useSummonerAsSource = description.GetBool("UseSummonerAsSource", false),
                    element = description.GetString("CreatureElement", string.Empty),
                    description = description.GetString("Description", string.Empty)
                };
                Register(SummonedUnitDatabase, summonedUnit.gcType, summonedUnit);
                SummonedUnits.Add(summonedUnit);
                continue;
            }
            if (!IsCreature(root.Node, description))
                continue;
            var creature = BuildCreatureData(root.Node.CanonicalPath, effective);
            Register(CreatureDatabase, creature.gcType, creature);
            Creatures.Add(creature);
        }
    }

    private static CreatureData BuildCreatureData(string gcType, GCNode effective)
    {
        GCNode description = effective.GetChild("Description") ?? effective;
        return new CreatureData
        {
            gcType = gcType,
            behaviourType = ResolveBehaviorType(effective),
            name = description.GetString("Label", description.GetString("Name", Leaf(gcType))),
            faction = description.GetString("Faction", description.GetString("FactionID", string.Empty)),
            creatureType = description.GetString("CreatureFamily", string.Empty),
            element = description.GetString("CreatureElement", string.Empty),
            creatureDifficulty = description.GetString("CreatureDifficulty", string.Empty),
            speed = description.GetString("Speed", string.Empty),
            walkSpeed = description.GetString("WalkSpeed", string.Empty),
            attackRange = description.GetString("AttackRange", string.Empty),
            hitPoints = description.GetInt("HitPoints", 0),
            manaPoints = description.GetInt("ManaPoints", 0),
            baseDamage = description.GetInt("BaseDamage", 0),
            attackRating = description.GetString("AttackRating", string.Empty),
            defenseRating = description.GetString("DefenseRating", string.Empty),
            damageMod = description.GetString("DamageMod", string.Empty),
            criticalChance = description.GetString("CriticalChance", string.Empty),
            divineResist = description.GetString("DivineResist", string.Empty),
            fireResist = description.GetString("FireResist", string.Empty),
            iceResist = description.GetString("IceResist", string.Empty),
            poisonResist = description.GetString("PoisonResist", string.Empty),
            shadowResist = description.GetString("ShadowResist", string.Empty),
            manipulators = ReadManipulators(effective)
        };
    }

    private static Dictionary<string, ManipulatorData> ReadManipulators(GCNode effective)
    {
        var result = new Dictionary<string, ManipulatorData>(StringComparer.OrdinalIgnoreCase);
        GCNode manipulators = effective.GetChild("Manipulators");
        if (manipulators == null)
            return result;
        foreach (GCNode slot in manipulators.EnumerateChildrenInOrder())
        {
            if (slot == null || string.IsNullOrWhiteSpace(slot.Name) || string.IsNullOrWhiteSpace(slot.Extends))
                continue;
            GCNode resolved = Effective(slot);
            GCNode description = resolved.GetChild("Description") ?? resolved;
            var data = new ManipulatorData { gcType = slot.Extends };
            foreach (KeyValuePair<string, string> property in description.EnumeratePropertiesInOrder())
                data.properties[property.Key] = property.Value;
            result[slot.Name] = data;
        }
        return result;
    }

    private static void LoadItems(IEnumerable<OrderedRoot> roots)
    {
        foreach (OrderedRoot root in roots)
        {
            foreach (GCNode node in EnumerateTree(root.Node))
            {
                if (node.IsAnonymous || string.IsNullOrWhiteSpace(node.Extends) || !IsItemNamespace(node.CanonicalPath))
                    continue;
                GCNode effective = Effective(node);
                GCNode description = effective.GetChild("Description");
                if (!HasAuthoredItemDimensions(description))
                    continue;
                RegisterItem(node, description, true);
            }
        }
    }

    private static void LoadReferencedItems()
    {
        foreach (ClassDefinition definition in ClassDefinitions.Values)
        {
            EnsureReferencedItem(definition.startingEquipment.weapon, "class starting weapon");
            EnsureReferencedItem(definition.startingEquipment.armor, "class starting armor");
            EnsureReferencedItem(definition.startingEquipment.gloves, "class starting gloves");
            EnsureReferencedItem(definition.startingEquipment.boots, "class starting boots");
            foreach (StartingInventoryItem item in definition.startingInventory)
                EnsureReferencedItem(item?.gcClass, "class starting inventory");
        }

        foreach (QuestData quest in Quests)
        {
            EnsureItemIfDirectReference(quest.onAcceptItem, $"quest on-accept quest='{quest.id}'");
            foreach (QuestRewardChoice choice in quest.rewardChoices ?? new List<QuestRewardChoice>())
                EnsureItemIfDirectReference(choice?.generator, $"quest reward quest='{quest.id}'");
            foreach (QuestObjective objective in quest.objectives ?? new List<QuestObjective>())
                if (objective.type == "item")
                    foreach (string target in objective.targets)
                        EnsureReferencedItem(target, $"quest objective quest='{quest.id}' objective='{objective.name}'");
        }

        foreach (List<QuestKillDropEntry> entries in QuestKillDropsByMonster.Values)
            foreach (QuestKillDropEntry entry in entries)
                EnsureReferencedItem(entry.ItemGcType, $"quest kill drop path='{entry.SourcePath}'");

        foreach (MerchantData merchant in Merchants)
            foreach (MerchantInventoryData inventory in merchant.inventories)
                foreach (MerchantItemData item in inventory.items)
                    EnsureReferencedItem(item.gcType, $"merchant npc='{merchant.npcGcType}' inventory='{inventory.gcType}'");
    }

    private static void EnsureItemIfDirectReference(string path, string source)
    {
        if (string.IsNullOrWhiteSpace(path) || FindGeneralItem(path) != null)
            return;
        GCNode node = GCDatabase.Instance.Resolve(path);
        if (node == null)
            return;
        GCNode description = Effective(node).GetChild("Description");
        if (IsNativeItemDescription(description))
            RegisterItem(node, description, false);
    }

    private static void EnsureReferencedItem(string path, string source)
    {
        if (string.IsNullOrWhiteSpace(path) || FindGeneralItem(path) != null)
            return;
        GCNode node = GCDatabase.Instance.Resolve(path)
            ?? throw new InvalidDataException($"Referenced item is unresolved source={source} item='{path}'");
        GCNode description = Effective(node).GetChild("Description");
        if (!IsNativeItemDescription(description))
            throw new InvalidDataException($"Referenced object is not an item source={source} item='{path}'");
        RegisterItem(node, description, false);
    }

    private static void RegisterItem(GCNode node, GCNode description, bool includeInAuthoredPools)
    {
        string gcType = node.CanonicalPath;
        int inventoryWidth = description.HasProperty("InventoryWidth")
            ? RequireInt(description, "InventoryWidth", gcType + ".Description")
            : 1;
        int inventoryHeight = description.HasProperty("InventoryHeight")
            ? RequireInt(description, "InventoryHeight", gcType + ".Description")
            : 1;
        if (inventoryWidth < 1 || inventoryWidth > byte.MaxValue || inventoryHeight < 1 || inventoryHeight > byte.MaxValue)
            throw new InvalidDataException($"Invalid authored item dimensions path='{gcType}' width={inventoryWidth} height={inventoryHeight}");
        int modCount = Enumerable.Range(1, 10).Count(index => !string.IsNullOrWhiteSpace(description.GetString("ItemModGenerator" + index.ToString(CultureInfo.InvariantCulture), string.Empty)));
        var general = new GeneralItemData
        {
            gcType = gcType,
            baseType = node.Extends,
            Label = description.GetString("Label", Leaf(gcType)),
            InventoryIcon = description.GetString("InventoryIcon", string.Empty),
            GroundObject = description.GetString("GroundObject", string.Empty),
            Stackable = description.GetBool("Stackable", false),
            InventoryWidth = inventoryWidth,
            InventoryHeight = inventoryHeight,
            DropLevel = description.GetInt("DropLevel", 0),
            LevelReq = description.GetInt("LevelReq", description.GetInt("RequiredLevel", 0)),
            modCount = modCount,
            GoldValueF32 = description.GetFixed32("GoldValue", 0),
            GcGoldValueF32 = description.GetFixed32("GCGoldValue", description.GetFixed32("GcGoldValue", 0))
        };
        Register(GeneralItemDatabase, gcType, general);
        if (includeInAuthoredPools)
        {
            Items.Add(general);
            AddGeneralItemCategory(node, general);
        }

        string weaponClass = description.GetString("WeaponClass", string.Empty);
        string slotType = description.GetString("SlotType", string.Empty);
        bool equipable = description.GetBool("Equipable", !string.IsNullOrWhiteSpace(weaponClass) || !string.IsNullOrWhiteSpace(slotType));
        if (!equipable && string.IsNullOrWhiteSpace(weaponClass) && string.IsNullOrWhiteSpace(slotType))
            return;
        int rangeFixed = description.GetFixed32("Range", 0);
        var item = new ItemData
        {
            gcType = gcType,
            name = general.Label,
            description = description.GetString("Description", string.Empty),
            GoldValueF32 = general.GoldValueF32,
            GcGoldValueF32 = general.GcGoldValueF32,
            range = rangeFixed / 256,
            slotType = slotType,
            weaponClass = weaponClass,
            inventoryWidth = general.InventoryWidth,
            inventoryHeight = general.InventoryHeight,
            inventoryIcon = general.InventoryIcon,
            groundObject = general.GroundObject,
            equipable = equipable,
            modCount = modCount,
            requiredLevel = Math.Max(1, general.LevelReq > 0 ? general.LevelReq : general.DropLevel)
        };
        Register(ItemDatabase, gcType, item);
        if (!includeInAuthoredPools)
            return;
        if (!string.IsNullOrWhiteSpace(weaponClass))
            AllWeapons.Add(item);
        else
            AllArmor.Add(item);
    }

    private static void LoadSkills(IEnumerable<OrderedRoot> roots)
    {
        foreach (OrderedRoot root in roots)
        {
            string path = root.Node.CanonicalPath ?? string.Empty;
            if (!path.StartsWith("skills.", StringComparison.OrdinalIgnoreCase))
                continue;
            GCNode effective = Effective(root.Node);
            GCNode description = effective.GetChild("Description") ?? effective;
            if (!description.HasProperty("SkillID") && !description.HasProperty("Label") && !InheritanceContains(root.Node, "skill"))
                continue;
            Skills.Add(new SkillData
            {
                id = path,
                name = description.GetString("Label", Leaf(path)),
                description = description.GetString("Description", string.Empty),
                level = description.GetInt("Level", 1),
                experience = description.GetInt("Experience", 0),
                maxLevel = description.GetInt("MaxLevel", 1),
                attributes = new SkillAttributes
                {
                    strength = description.GetInt("Strength", 0),
                    dexterity = description.GetInt("Dexterity", 0),
                    vitality = description.GetInt("Vitality", 0),
                    intelligence = description.GetInt("Intelligence", 0),
                    wisdom = description.GetInt("Wisdom", 0),
                    spirit = description.GetInt("Spirit", 0),
                    perception = description.GetInt("Perception", 0),
                    agility = description.GetInt("Agility", 0)
                }
            });
        }
    }

    private static void LoadQuests(IEnumerable<OrderedRoot> roots)
    {
        foreach (OrderedRoot root in roots)
        {
            string path = root.Node.CanonicalPath ?? string.Empty;
            bool isConcreteQuest = path.StartsWith("world.", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("quests.base.HelperNoobosaur.", StringComparison.OrdinalIgnoreCase);
            if (!isConcreteQuest || !InheritanceContains(root.Node, "quests.base.quest"))
                continue;
            GCNode effective = Effective(root.Node);
            GCNode description = effective.GetChild("Description") ?? effective;
            int minLevel = description.GetInt("MinLevel", 1);
            var quest = new QuestData
            {
                id = path,
                name = description.GetString("Label", Leaf(path)),
                summary = description.GetString("Summary", string.Empty),
                description = description.GetString("Description", string.Empty),
                level = description.GetInt("Level", minLevel),
                minLevel = minLevel,
                maxLevel = description.GetInt("MaxLevel", 100),
                npcs = ReadNumberedStrings(description, "NPC", 5, true),
                onAcceptItem = description.GetString("OnAcceptItemGenerator", string.Empty),
                baseClass = root.Node.Extends ?? string.Empty,
                tokenReward = description.GetInt("TokenReward", 0),
                cashRewardF32 = description.GetFixed32("CashReward", 256),
                grantXPBuff = description.GetBool("GrantXPBuff", false),
                repeatable = description.GetBool("Repeatable", false),
                requiredQuests = ReadNumberedStrings(description, "RequiredQuest", 2, false),
                followupQuest = description.GetString("FollowupQuest", string.Empty),
                uiZoneInfo = description.GetString("UIZoneInfo", string.Empty),
                numRewardItems = description.GetInt("NumRewardItems", 1),
                rewardChoices = ReadQuestRewardChoices(description),
                rewardItemsSoulBound = description.GetBool("RewardItemsAreSoulBound", false),
                rewardItemsNoSell = description.GetBool("RewardItemsAreNoSell", false),
                rewardItemsDropped = description.GetBool("RewardItemsAreDropped", false),
                rewardItemsRequireMembership = description.GetBool("RewardItemsRequireMembership", false),
                onAcceptItemsSoulBound = description.GetBool("OnAcceptItemsAreSoulBound", false),
                onAcceptItemsNoSell = description.GetBool("OnAcceptItemsAreNoSell", false),
                minRepeatTimeSeconds = description.GetInt("MinRepeatTimeSeconds", 0),
                addOnAccept = description.GetBool("AddOnAccept", true),
                autoAcceptOnQuery = description.GetBool("AutoAcceptOnQuery", false),
                permanentAbandon = description.GetBool("PermanentAbandon", false),
                temporary = description.GetBool("Temporary", false),
                requiredClass = description.GetString("RequiredClass", string.Empty),
                modToAddOnComplete = description.GetString("ModToAddOnComplete", string.Empty),
                rewardText = description.GetString("RewardText", string.Empty),
                abandonOnZone = description.GetString("AbandonOnZone", string.Empty),
                objectives = ReadQuestObjectives(effective),
                rewards = ReadQuestRewards(description)
            };
            quest.hash = ComputeDJB2Hash(quest.id);
            if (QuestsByHash.TryGetValue(quest.hash, out QuestData existing))
                throw new InvalidDataException($"Quest hash collision hash=0x{quest.hash:X8} first='{existing.id}' second='{quest.id}'");
            Quests.Add(quest);
            QuestsByHash.Add(quest.hash, quest);
            ReadQuestTriggers(effective, quest.id);
        }
    }

    private static List<QuestObjective> ReadQuestObjectives(GCNode questNode)
    {
        var objectives = new List<QuestObjective>();
        foreach (GCNode child in questNode.EnumerateChildrenInOrder())
        {
            string type = ObjectiveType(child);
            if (type == null)
                continue;
            GCNode effective = Effective(child);
            var targets = new List<string>();
            foreach (KeyValuePair<string, string> property in effective.EnumeratePropertiesInOrder())
                if (IsObjectiveTargetProperty(property.Key) && !string.IsNullOrWhiteSpace(property.Value))
                    targets.Add(property.Value);
            int count = type switch
            {
                "kill" => effective.GetInt("RequiredKills", 20),
                "item" => effective.GetInt("RequiredQuantity", 20),
                "activate" => effective.GetInt("RequiredQuantity", 1),
                _ => 1
            };
            objectives.Add(new QuestObjective
            {
                sourcePath = child.CanonicalPath,
                name = effective.GetString("Name", string.Empty),
                type = type,
                target = targets.FirstOrDefault() ?? string.Empty,
                targets = targets,
                count = count,
                removeOnFinalize = effective.GetBool("RemoveOnFinalize", false),
                itemLevel = checked((byte)effective.GetInt("ItemLevel", 255)),
                targetZone = effective.GetString("TargetZoneName", string.Empty),
                targetEntity = effective.GetString("TargetEntityName", string.Empty),
                range = effective.GetInt("Range", 0),
                label = effective.GetString("Label", string.Empty)
            });
        }
        return objectives;
    }

    private static QuestRewards ReadQuestRewards(GCNode description)
    {
        return new QuestRewards
        {
            experience = description.GetInt("ExperienceReward", 0)
        };
    }

    private static List<string> ReadNumberedStrings(GCNode node, string property, int count, bool unsuffixedFirst)
    {
        var values = new List<string>();
        for (int index = 1; index <= count; index++)
        {
            string propertyName = index == 1 && unsuffixedFirst ? property : property + index.ToString(CultureInfo.InvariantCulture);
            string value = node.GetString(propertyName, string.Empty);
            if (!string.IsNullOrWhiteSpace(value))
                values.Add(value);
        }
        return values;
    }

    private static List<QuestRewardChoice> ReadQuestRewardChoices(GCNode description)
    {
        var choices = new List<QuestRewardChoice>(4);
        for (int index = 1; index <= 4; index++)
        {
            string suffix = index == 1 ? string.Empty : index.ToString(CultureInfo.InvariantCulture);
            choices.Add(new QuestRewardChoice
            {
                generator = description.GetString("RewardItemGenerator" + suffix, string.Empty),
                description = description.GetString("RewardItemDescription" + suffix, string.Empty),
                icon = description.GetString("RewardIconGenerator" + suffix, string.Empty)
            });
        }
        return choices;
    }

    private static void ReadQuestTriggers(GCNode root, string questId)
    {
        ReadQuestTriggerNode(root, questId, -1, string.Empty);
        int objectiveIndex = 0;
        foreach (GCNode child in root.EnumerateChildrenInOrder())
        {
            string objectiveType = ObjectiveType(child);
            if (objectiveType == null)
                ReadQuestTriggerTree(Effective(child), questId, -1, string.Empty);
            else
            {
                ReadQuestTriggerTree(Effective(child), questId, objectiveIndex, child.CanonicalPath);
                objectiveIndex++;
            }
        }
    }

    private static void ReadQuestTriggerTree(GCNode node, string questId, int objectiveIndex, string objectiveSourcePath)
    {
        ReadQuestTriggerNode(node, questId, objectiveIndex, objectiveSourcePath);
        foreach (GCNode child in node.EnumerateChildrenInOrder())
            ReadQuestTriggerTree(Effective(child), questId, objectiveIndex, objectiveSourcePath);
    }

    private static void ReadQuestTriggerNode(GCNode node, string questId, int objectiveIndex, string objectiveSourcePath)
    {
        if (InheritanceContains(node, "killdroptrigger"))
        {
            GCNode effective = Effective(node);
            string monster = effective.GetString("MonsterType", string.Empty);
            string item = effective.GetString("ItemType", effective.GetString("Item", string.Empty));
            string treasureGenerator = effective.GetString("TreasureGenerator", string.Empty);
            if (objectiveIndex < 0
                || string.IsNullOrWhiteSpace(monster)
                || string.IsNullOrWhiteSpace(item) == string.IsNullOrWhiteSpace(treasureGenerator))
                throw new InvalidDataException($"Incomplete quest kill drop path='{node.CanonicalPath}' objectiveIndex={objectiveIndex}");
            if (!string.IsNullOrWhiteSpace(treasureGenerator) && GCDatabase.Instance.ResolveWithInheritance(treasureGenerator) == null)
                throw new InvalidDataException($"Quest kill drop generator is missing path='{node.CanonicalPath}' generator='{treasureGenerator}'");
            int chance = effective.GetInt("Chance", 100);
            int chanceCount = effective.GetInt("ChanceCount", 1);
            int minLevel = effective.GetInt("MinLevel", 1);
            int maxLevel = effective.GetInt("MaxLevel", 200);
            if (chance < 0 || chance > 100 || chanceCount < 1 || minLevel < 1 || maxLevel < minLevel)
                throw new InvalidDataException($"Invalid quest kill drop path='{node.CanonicalPath}' chance={chance} chanceCount={chanceCount} levelRange={minLevel}-{maxLevel}");
            if (!QuestKillDropsByMonster.TryGetValue(monster, out List<QuestKillDropEntry> entries))
            {
                entries = new List<QuestKillDropEntry>();
                QuestKillDropsByMonster.Add(monster, entries);
            }
            entries.Add(new QuestKillDropEntry
            {
                SourcePath = node.CanonicalPath,
                QuestId = questId,
                ObjectiveSourcePath = objectiveSourcePath,
                ObjectiveIndex = objectiveIndex,
                ItemGcType = item,
                TreasureGenerator = treasureGenerator,
                Chance = chance,
                ChanceCount = chanceCount,
                MinLevel = minLevel,
                MaxLevel = maxLevel
            });
            return;
        }

        if (!InheritanceContains(node, "activatedroptrigger"))
            return;
        GCNode activateEffective = Effective(node);
        string entityGcType = activateEffective.GetString("EntityType", string.Empty);
        string itemGcType = activateEffective.GetString("Item", activateEffective.GetString("ItemType", string.Empty));
        int activateChance = activateEffective.GetInt("Chance", 100);
        bool removeEntity = activateEffective.GetBool("RemoveEntity", false);
        if (objectiveIndex < 0 || string.IsNullOrWhiteSpace(entityGcType) || string.IsNullOrWhiteSpace(itemGcType))
            throw new InvalidDataException($"Incomplete quest activate drop path='{node.CanonicalPath}' objectiveIndex={objectiveIndex}");
        if (activateChance < 0 || activateChance > 100)
            throw new InvalidDataException($"Invalid quest activate drop path='{node.CanonicalPath}' chance={activateChance}");
        if (GCDatabase.Instance.ResolveWithInheritance(entityGcType) == null)
            throw new InvalidDataException($"Quest activate drop entity type is missing path='{node.CanonicalPath}' entity='{entityGcType}'");
        if (GCDatabase.Instance.ResolveWithInheritance(itemGcType) == null)
            throw new InvalidDataException($"Quest activate drop item is missing path='{node.CanonicalPath}' item='{itemGcType}'");
        if (!QuestActivateDropsByQuest.TryGetValue(questId, out List<QuestActivateDropEntry> activateEntries))
        {
            activateEntries = new List<QuestActivateDropEntry>();
            QuestActivateDropsByQuest.Add(questId, activateEntries);
        }
        activateEntries.Add(new QuestActivateDropEntry
        {
            SourcePath = node.CanonicalPath,
            QuestId = questId,
            ObjectiveSourcePath = objectiveSourcePath,
            ObjectiveIndex = objectiveIndex,
            EntityGcType = entityGcType,
            ItemGcType = itemGcType,
            Chance = activateChance,
            RemoveEntity = removeEntity
        });
    }

    private static void LoadMerchants(IEnumerable<OrderedRoot> roots)
    {
        foreach (OrderedRoot root in roots)
        {
            GCNode merchantNode = root.Node.GetChild("Merchant");
            if (merchantNode == null || !InheritanceContains(merchantNode, "merchant"))
                continue;
            GCNode effectiveMerchant = Effective(merchantNode);
            var merchant = new MerchantData
            {
                npcGcType = root.Node.CanonicalPath,
                merchantGcType = merchantNode.CanonicalPath,
                inventories = new List<MerchantInventoryData>()
            };
            int authoredOrdinal = 0;
            foreach (GCNode inventoryNode in effectiveMerchant.EnumerateChildrenInOrder())
            {
                if (!InheritanceContains(inventoryNode, "merchantinventory"))
                    continue;
                GCNode effectiveInventory = Effective(inventoryNode);
                GCNode description = effectiveInventory.GetChild("Description") ?? effectiveInventory;
                var inventory = new MerchantInventoryData
                {
                    name = inventoryNode.Name,
                    gcType = inventoryNode.CanonicalPath,
                    authoredOrdinal = authoredOrdinal,
                    id = RequireInt(effectiveInventory, "ID", inventoryNode.CanonicalPath),
                    staticContents = effectiveInventory.GetBool("StaticContents", false),
                    autoGenerateItems = effectiveInventory.GetBool("AutoGenerateItems", true),
                    itemGenerator = effectiveInventory.GetString("ItemGenerator", string.Empty),
                    minItemLevel = effectiveInventory.GetInt("MinItemLevel", 1),
                    maxItemLevel = effectiveInventory.GetInt("MaxItemLevel", 100),
                    label = description.GetString("Label", inventoryNode.Name),
                    width = description.GetInt("Width", 0),
                    height = description.GetInt("Height", 0),
                    items = new List<MerchantItemData>()
                };
                foreach (GCNode itemNode in effectiveInventory.EnumerateChildrenInOrder())
                {
                    if (!itemNode.IsAnonymous || string.IsNullOrWhiteSpace(itemNode.Extends))
                        continue;
                    inventory.items.Add(new MerchantItemData
                    {
                        gcType = itemNode.Extends,
                        inventoryX = RequireInt(itemNode, "InventoryX", itemNode.CanonicalPath),
                        inventoryY = RequireInt(itemNode, "InventoryY", itemNode.CanonicalPath),
                        id = RequireInt(itemNode, "ID", itemNode.CanonicalPath),
                        quantity = itemNode.HasProperty("Quantity") ? RequireInt(itemNode, "Quantity", itemNode.CanonicalPath) : 1
                    });
                }
                merchant.inventories.Add(inventory);
                authoredOrdinal++;
            }
            Merchants.Add(merchant);
            MerchantsByNpc.Add(merchant.npcGcType, merchant);
        }
    }

    private static void LoadWorldPlacements(IEnumerable<OrderedRoot> roots)
    {
        int waypointId = 0;
        int portalId = 0;
        int checkpointId = 0;
        int worldEntityId = 0;
        foreach (OrderedRoot root in roots.Where(root => root.Document.TypeCode == 15))
        {
            string zone = root.Document.Name;
            GCNode entities = Effective(root.Node).GetChild("Entities");
            if (entities == null)
                continue;
            foreach (GCNode placement in entities.EnumerateChildrenInOrder())
            {
                if (placement == null || string.IsNullOrWhiteSpace(placement.Extends))
                    continue;
                if (!TryReadPosition(Effective(placement), out int x, out int y, out int z))
                    throw new InvalidDataException($"World placement position is missing path='{placement.CanonicalPath}' type='{placement.Extends}'");
                int heading = Effective(placement).GetFixed32("Heading", 0);
                string name = Effective(placement).GetString("Name", Leaf(placement.Extends));
                if (!QuestLocationsByZone.TryGetValue(zone, out List<ZoneWaypointData> locations))
                {
                    locations = new List<ZoneWaypointData>();
                    QuestLocationsByZone.Add(zone, locations);
                }
                locations.Add(new ZoneWaypointData { zone = zone, name = name, PosFixedX = x, PosFixedY = y, PosFixedZ = z });
                if (InheritanceContains(placement, "waypoint"))
                {
                    AddWaypoint(new ZoneWaypointData { id = ++waypointId, zone = zone, name = name, PosFixedX = x, PosFixedY = y, PosFixedZ = z, HeadingFixed = heading });
                    continue;
                }
                if (InheritanceContains(placement, "zoneportal"))
                {
                    GCNode effective = Effective(placement);
                    AddPortal(new ZonePortalData
                    {
                        id = ++portalId,
                        zone = zone,
                        name = name,
                        gcType = placement.Extends,
                        PosFixedX = x,
                        PosFixedY = y,
                        PosFixedZ = z,
                        HeadingFixed = heading,
                        width = effective.GetInt("Width", 40),
                        height = effective.GetInt("Height", 40),
                        targetZone = RequireString(effective, "Zone", placement.CanonicalPath),
                        spawnPoint = RequireString(effective, "SpawnPoint", placement.CanonicalPath),
                        color = ParseUInt(effective.GetString("Color", "0"))
                    });
                    continue;
                }
                if (InheritanceContains(placement, "checkpointentity"))
                {
                    GCNode description = Effective(placement).GetChild("Description") ?? Effective(placement);
                    string checkpointGcType = RequireString(description, "Checkpoint", placement.CanonicalPath);
                    var zoneCheckpoint = new ZoneCheckpointData
                    {
                        id = ++checkpointId,
                        zone = zone,
                        name = name,
                        gcType = checkpointGcType,
                        entityGcType = placement.Extends,
                        PosFixedX = x,
                        PosFixedY = y,
                        PosFixedZ = z,
                        HeadingFixed = heading
                    };
                    AddCheckpoint(zoneCheckpoint);
                    if (CheckpointDatabase.TryGetValue(checkpointGcType, out CheckpointData checkpoint))
                    {
                        checkpoint.position = new PositionData { x = x / 256f, y = y / 256f, z = z / 256f };
                        checkpoint.zone = zone;
                    }
                    continue;
                }
                GCNode placementDescription = Effective(placement).GetChild("Description") ?? Effective(placement);
                if (IsNpc(placement))
                {
                    AddNpc(zone, new NPCData
                    {
                        name = name,
                        gcType = placement.Extends,
                        PosFixedX = x,
                        PosFixedY = y,
                        PosFixedZ = z,
                        HeadingFixed = heading,
                        hitPoints = placement.GetInt("HitPoints", placementDescription.GetInt("HitPoints", 0)),
                        manaPoints = placement.GetInt("ManaPoints", placementDescription.GetInt("ManaPoints", 0))
                    });
                    continue;
                }
                if (IsCreature(placement, placementDescription))
                {
                    if (!DungeonSpawns.TryGetValue(zone, out List<DungeonSpawnData> spawns))
                    {
                        spawns = new List<DungeonSpawnData>();
                        DungeonSpawns.Add(zone, spawns);
                    }
                    spawns.Add(new DungeonSpawnData
                    {
                        zoneName = zone,
                        gcType = placement.Extends,
                        PosFixedX = x,
                        PosFixedY = y,
                        PosFixedZ = z,
                        HeadingFixed = heading,
                        placementRole = "authored-world-entity"
                    });
                    continue;
                }
                string entityType = ClassifyWorldEntity(placement, placementDescription);
                if (entityType == null)
                    continue;
                AddWorldEntity(zone, BuildWorldEntity(++worldEntityId, zone, name, placement, placementDescription, entityType, x, y, z, heading));
            }
        }
    }

    private static void LoadQuestTileLocations(IEnumerable<OrderedRoot> roots)
    {
        foreach (OrderedRoot root in roots.Where(root => root.Document.TypeCode == 14))
        {
            GCNode entities = Effective(root.Node).GetChild("Entities");
            if (entities == null)
                continue;
            var locations = new List<ZoneWaypointData>();
            foreach (GCNode placement in entities.EnumerateChildrenInOrder())
            {
                GCNode effective = Effective(placement);
                string name = effective.GetString("Name", string.Empty);
                if (string.IsNullOrEmpty(name) || !TryReadPosition(effective, out int x, out int y, out int z))
                    continue;
                locations.Add(new ZoneWaypointData { name = name, PosFixedX = x, PosFixedY = y, PosFixedZ = z });
            }
            QuestLocationsByTile.Add(root.Document.Name, locations);
        }
    }

    private static void LinkAuthoredNpcQuestGivers(IEnumerable<OrderedRoot> roots)
    {
        foreach (OrderedRoot root in roots)
        {
            string npcType = root.Node.CanonicalPath;
            GCNode definition = Effective(root.Node);
            GCNode description = definition?.GetChild("Description");
            if (description == null)
                continue;
            GCNode questGiver = description.EnumerateChildrenInOrder().FirstOrDefault(node => InheritanceContains(node, "QuestGiver"));
            if (questGiver == null)
                continue;
            var offeredQuests = new List<QuestData>();
            foreach (GCNode availableQuest in Effective(questGiver).EnumerateChildrenInOrder().Where(node => InheritanceContains(node, "AvailableQuest")))
            {
                string questId = Effective(availableQuest).GetString("Quest", string.Empty);
                if (string.IsNullOrWhiteSpace(questId))
                    continue;
                QuestData quest = Quests.FirstOrDefault(candidate => candidate.id.Equals(questId, StringComparison.OrdinalIgnoreCase));
                if (quest == null)
                    throw new InvalidDataException($"NPC available quest is unresolved npc='{npcType}' quest='{questId}'");
                offeredQuests.Add(quest);
                if (!quest.offeringNpcs.Contains(npcType, StringComparer.OrdinalIgnoreCase))
                    quest.offeringNpcs.Add(npcType);
            }
            QuestOffersByNpc.Add(npcType, offeredQuests);
        }
    }

    public static bool IsNpcPlacement(GCNode node) => IsNpc(node);

    public static WorldEntityData CreateWorldEntityPlacement(int id, string zone, string name, GCNode placement, int x, int y, int z, int heading)
    {
        GCNode effective = Effective(placement);
        GCNode description = effective.GetChild("Description") ?? effective;
        string entityType = ClassifyWorldEntity(effective, description);
        return entityType == null ? null : BuildWorldEntity(id, zone, name, effective, description, entityType, x, y, z, heading);
    }

    private static WorldEntityData BuildWorldEntity(int id, string zone, string name, GCNode placement, GCNode description, string entityType, int x, int y, int z, int heading)
    {
        var data = new WorldEntityData
        {
            Id = id,
            Zone = zone,
            Name = name,
            GCType = placement.Extends,
            EntityType = entityType,
            PosFixedX = x,
            PosFixedY = y,
            PosFixedZ = z,
            HeadingFixed = heading,
            Flags = ZonesByName.TryGetValue(zone, out AuthoredZoneData zoneData) && zoneData.Private ? 7u : 6u,
            TargetZone = description.GetString("TeleportZone", string.Empty),
            TargetSpawn = description.GetString("TeleportPoint", string.Empty),
            Label = description.GetString("Label", name),
            AllowMultiple = description.GetBool("AllowMultipleActivations", false)
        };
        for (int slot = 1; slot <= 5; slot++)
        {
            string suffix = slot == 1 ? string.Empty : slot.ToString(CultureInfo.InvariantCulture);
            string generator = description.GetString("ItemGenerator" + suffix, description.GetString("ItemGenerator" + slot.ToString(CultureInfo.InvariantCulture), string.Empty));
            int count = description.GetInt("ItemCount" + suffix, description.GetInt("ItemCount" + slot.ToString(CultureInfo.InvariantCulture), 0));
            switch (slot)
            {
                case 1: data.ItemGenerator = generator; data.ItemCount = count; break;
                case 2: data.ItemGenerator2 = generator; data.ItemCount2 = count; break;
                case 3: data.ItemGenerator3 = generator; data.ItemCount3 = count; break;
                case 4: data.ItemGenerator4 = generator; data.ItemCount4 = count; break;
                case 5: data.ItemGenerator5 = generator; data.ItemCount5 = count; break;
            }
        }
        return data;
    }

    private static void FinalizeZoneSpawns()
    {
        foreach (AuthoredZoneData zone in Zones)
        {
            if (!WaypointsByZone.TryGetValue(zone.Name, out List<ZoneWaypointData> waypoints))
                continue;
            string requested = zone.RespawnSpawnPoint;
            ZoneWaypointData spawn = waypoints.FirstOrDefault(waypoint => string.Equals(waypoint.name, requested, StringComparison.OrdinalIgnoreCase));
            if (spawn == null)
                continue;
            zone.SpawnFixedX = spawn.PosFixedX;
            zone.SpawnFixedY = spawn.PosFixedY;
            zone.SpawnFixedZ = spawn.PosFixedZ;
            zone.SpawnHeadingFixed = spawn.HeadingFixed;
        }
    }

    private static void BuildEquipmentMappings()
    {
        foreach (ItemData item in AllArmor)
        {
            Dictionary<string, List<ItemData>> target = item.slotType switch
            {
                "1" => Helmets,
                "6" => Armors,
                "3" => Gloves,
                "4" => Boots,
                _ => null
            };
            if (target != null)
                AddCategoryItem(target, ItemFamily(item.gcType), item);
        }
        foreach (ItemData item in AllWeapons)
        {
            Dictionary<string, List<ItemData>> target = item.weaponClass.Contains("RANGED", StringComparison.OrdinalIgnoreCase)
                ? RangedWeapons
                : MeleeWeapons;
            AddCategoryItem(target, ItemFamily(item.gcType), item);
        }
    }

    private static void ValidateCoverage(int rootCount)
    {
        if (rootCount != PackageCatalog.Instance.RuntimeTextDocumentCount)
            throw new InvalidDataException($"Authored root coverage mismatch roots={rootCount} documents={PackageCatalog.Instance.RuntimeTextDocumentCount}");
        if (Zones.Count != 575)
            throw new InvalidDataException($"Authored zone coverage mismatch expected=575 actual={Zones.Count}");
        if (ClassDefinitions.Count != 3)
            throw new InvalidDataException($"Authored class coverage mismatch expected=3 actual={ClassDefinitions.Count}");
        if (StartingCheckpointIds.Count != 3)
            throw new InvalidDataException($"Authored starting checkpoint coverage mismatch expected=3 actual={StartingCheckpointIds.Count}");
        if (SummonedUnits.Count != 3)
            throw new InvalidDataException($"Authored summoned-unit coverage mismatch expected=3 actual={SummonedUnits.Count}");
        if (Creatures.Count != 510)
            throw new InvalidDataException($"Authored creature coverage mismatch expected=510 actual={Creatures.Count}");
        if (Items.Count != 3837)
            throw new InvalidDataException($"Authored item coverage mismatch expected=3837 actual={Items.Count}");
        int equipmentCount = AllWeapons.Count + AllArmor.Count;
        if (equipmentCount != 3312)
            throw new InvalidDataException($"Authored equipment coverage mismatch expected=3312 actual={equipmentCount}");
        int questKillDropCount = QuestKillDropsByMonster.Values.Sum(entries => entries.Count);
        if (questKillDropCount != 1625)
            throw new InvalidDataException($"Authored quest kill drop coverage mismatch expected=1625 actual={questKillDropCount}");
        int questActivateDropCount = QuestActivateDropsByQuest.Values.Sum(entries => entries.Count);
        if (questActivateDropCount != 217)
            throw new InvalidDataException($"Authored quest activate drop coverage mismatch expected=217 actual={questActivateDropCount}");
        if (Quests.Count != 1292)
            throw new InvalidDataException($"Authored quest coverage mismatch expected=1292 actual={Quests.Count}");
        if (Merchants.Count != 217)
            throw new InvalidDataException($"Authored merchant coverage mismatch expected=217 actual={Merchants.Count}");
        ValidateClassReferences();
        ValidateQuestReferences();
        ValidateMerchantReferences();
        ValidateCheckpointReferences();
        foreach (string requiredZone in new[] { "town", "tutorial", "dungeon00_level01" })
            if (!ZonesByName.ContainsKey(requiredZone))
                throw new InvalidDataException($"Required authored zone is missing: {requiredZone}");
    }

    private static void ValidateClassReferences()
    {
        var skills = new HashSet<string>(Skills.Select(skill => skill.id), StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, ClassDefinition> entry in ClassDefinitions)
        {
            ClassDefinition definition = entry.Value;
            foreach (string itemPath in new[]
            {
                definition.startingEquipment.weapon,
                definition.startingEquipment.armor,
                definition.startingEquipment.gloves,
                definition.startingEquipment.boots
            })
            {
                if (string.IsNullOrWhiteSpace(itemPath) || FindGeneralItem(itemPath) == null)
                    throw new InvalidDataException($"Class starting equipment is unresolved class='{entry.Key}' item='{itemPath}'");
            }
            foreach (StartingInventoryItem item in definition.startingInventory)
                if (item == null || string.IsNullOrWhiteSpace(item.gcClass) || FindGeneralItem(item.gcClass) == null)
                    throw new InvalidDataException($"Class starting inventory is unresolved class='{entry.Key}' item='{item?.gcClass}'");
            foreach (string skill in definition.startingSkills)
                if (string.IsNullOrWhiteSpace(skill) || !skills.Contains(skill))
                    throw new InvalidDataException($"Class starting skill is unresolved class='{entry.Key}' skill='{skill}'");
        }
    }

    private static void ValidateQuestReferences()
    {
        var quests = new HashSet<string>(Quests.Select(quest => quest.id), StringComparer.OrdinalIgnoreCase);
        foreach (QuestData quest in Quests)
        {
            if (quest.npcs.Count == 0)
                throw new InvalidDataException($"Quest has no authored NPC quest='{quest.id}'");
            foreach (string npc in quest.npcs)
                if (GCDatabase.Instance.ResolveWithInheritance(npc) == null)
                    throw new InvalidDataException($"Quest NPC is unresolved quest='{quest.id}' npc='{npc}'");
            foreach (string requiredQuest in quest.requiredQuests)
                if (!quests.Contains(requiredQuest))
                    throw new InvalidDataException($"Required quest is unresolved quest='{quest.id}' required='{requiredQuest}'");
            if (!string.IsNullOrWhiteSpace(quest.followupQuest) && !quests.Contains(quest.followupQuest))
                throw new InvalidDataException($"Followup quest is unresolved quest='{quest.id}' followup='{quest.followupQuest}'");
            if (!string.IsNullOrWhiteSpace(quest.onAcceptItem)
                && FindGeneralItem(quest.onAcceptItem) == null
                && GCDatabase.Instance.ResolveWithInheritance(quest.onAcceptItem) == null)
                throw new InvalidDataException($"On-accept item generator is unresolved quest='{quest.id}' generator='{quest.onAcceptItem}'");
            if (quest.rewardChoices == null || quest.rewardChoices.Count != 4)
                throw new InvalidDataException($"Quest reward slot shape is invalid quest='{quest.id}' slots={quest.rewardChoices?.Count ?? 0}");
            foreach (QuestRewardChoice choice in quest.rewardChoices)
                if (!string.IsNullOrWhiteSpace(choice.generator)
                    && FindGeneralItem(choice.generator) == null
                    && GCDatabase.Instance.ResolveWithInheritance(choice.generator) == null)
                    throw new InvalidDataException($"Quest reward generator is unresolved quest='{quest.id}' generator='{choice.generator}'");
            foreach (QuestObjective objective in quest.objectives ?? new List<QuestObjective>())
            {
                if (objective.count < 1)
                    throw new InvalidDataException($"Quest objective count is invalid quest='{quest.id}' objective='{objective.name}' count={objective.count}");
                if (objective.type == "kill" || objective.type == "activate")
                    foreach (string target in objective.targets)
                        if (GCDatabase.Instance.ResolveWithInheritance(target) == null)
                            throw new InvalidDataException($"Quest objective target is unresolved quest='{quest.id}' objective='{objective.name}' target='{target}'");
                if (objective.type == "item")
                    foreach (string target in objective.targets)
                        if (FindGeneralItem(target) == null)
                            throw new InvalidDataException($"Quest item objective target is unresolved quest='{quest.id}' objective='{objective.name}' target='{target}'");
            }
        }
    }

    private static void ValidateMerchantReferences()
    {
        foreach (MerchantData merchant in Merchants)
        {
            if (GCDatabase.Instance.ResolveWithInheritance(merchant.npcGcType) == null)
                throw new InvalidDataException($"Merchant NPC is unresolved npc='{merchant.npcGcType}'");
            foreach (MerchantInventoryData inventory in merchant.inventories)
            {
                if (!inventory.staticContents && !inventory.autoGenerateItems)
                    throw new InvalidDataException($"Merchant inventory has no authored content mode npc='{merchant.npcGcType}' inventory='{inventory.gcType}'");
                if (inventory.autoGenerateItems
                    && (string.IsNullOrWhiteSpace(inventory.itemGenerator)
                        || GCDatabase.Instance.ResolveWithInheritance(inventory.itemGenerator) == null))
                    throw new InvalidDataException($"Merchant generator is unresolved npc='{merchant.npcGcType}' inventory='{inventory.gcType}' generator='{inventory.itemGenerator}'");
                if (inventory.minItemLevel < 0 || inventory.maxItemLevel < 0
                    || inventory.maxItemLevel > 0 && inventory.minItemLevel > inventory.maxItemLevel)
                    throw new InvalidDataException($"Merchant level range is invalid npc='{merchant.npcGcType}' inventory='{inventory.gcType}' range={inventory.minItemLevel}-{inventory.maxItemLevel}");
                var itemIds = new HashSet<int>();
                foreach (MerchantItemData item in inventory.items)
                {
                    if (!itemIds.Add(item.id))
                        throw new InvalidDataException($"Merchant item ID is duplicated npc='{merchant.npcGcType}' inventory='{inventory.gcType}' id={item.id}");
                    if (item.inventoryX < 0 || item.inventoryY < 0 || item.quantity < 1)
                        throw new InvalidDataException($"Merchant item placement is invalid npc='{merchant.npcGcType}' inventory='{inventory.gcType}' item='{item.gcType}'");
                    if (FindGeneralItem(item.gcType) == null)
                        throw new InvalidDataException($"Merchant item is unresolved npc='{merchant.npcGcType}' inventory='{inventory.gcType}' item='{item.gcType}'");
                }
            }
        }
    }

    private static void ValidateCheckpointReferences()
    {
        foreach (ZoneCheckpointData checkpoint in ZoneCheckpoints)
        {
            GCNode entity = GCDatabase.Instance.Resolve(checkpoint.entityGcType);
            if (entity == null || !InheritanceContains(entity, "checkpointentity"))
                throw new InvalidDataException($"Checkpoint entity is unresolved or invalid zone='{checkpoint.zone}' entity='{checkpoint.entityGcType}'");
            GCNode definition = GCDatabase.Instance.Resolve(checkpoint.gcType);
            if (definition == null || !InheritanceContains(definition, "checkpoint"))
                throw new InvalidDataException($"Checkpoint definition is unresolved or invalid zone='{checkpoint.zone}' checkpoint='{checkpoint.gcType}'");
        }
    }

    private static void AddGeneralItemCategory(GCNode node, GeneralItemData item)
    {
        string lineage = string.Join("|", GCDatabase.Instance.GetInheritanceChainPaths(node.CanonicalPath)).ToLowerInvariant();
        string path = (node.CanonicalPath ?? string.Empty).ToLowerInvariant();
        string evidence = path + "|" + lineage;
        if (evidence.Contains("questitem")) QuestItems.Add(item);
        if (evidence.Contains("relic")) Relics.Add(item);
        if (evidence.Contains("ring")) Rings.Add(item);
        if (evidence.Contains("amulet")) Amulets.Add(item);
        if (evidence.Contains("potion")) Potions.Add(item);
        if (evidence.Contains("skillbook")) Skillbooks.Add(item);
        if (evidence.Contains("key")) Keys.Add(item);
        if (evidence.Contains("scroll")) Scrolls.Add(item);
        if (evidence.Contains("dungeonitem")) DungeonItems.Add(item);
        if (evidence.Contains("voucher")) Vouchers.Add(item);
        if (evidence.Contains("itempack")) ItemPacks.Add(item);
        if (evidence.Contains("consumable") || evidence.Contains("potion")) Consumables.Add(item);
    }

    private static void AddWaypoint(ZoneWaypointData waypoint)
    {
        ZoneWaypoints.Add(waypoint);
        if (!WaypointsByZone.TryGetValue(waypoint.zone, out List<ZoneWaypointData> list))
        {
            list = new List<ZoneWaypointData>();
            WaypointsByZone.Add(waypoint.zone, list);
        }
        list.Add(waypoint);
    }

    private static void AddPortal(ZonePortalData portal)
    {
        ZonePortals.Add(portal);
        if (!PortalsByZone.TryGetValue(portal.zone, out List<ZonePortalData> list))
        {
            list = new List<ZonePortalData>();
            PortalsByZone.Add(portal.zone, list);
        }
        list.Add(portal);
    }

    private static void AddCheckpoint(ZoneCheckpointData checkpoint)
    {
        ZoneCheckpoints.Add(checkpoint);
        if (!CheckpointsByZone.TryGetValue(checkpoint.zone, out List<ZoneCheckpointData> list))
        {
            list = new List<ZoneCheckpointData>();
            CheckpointsByZone.Add(checkpoint.zone, list);
        }
        list.Add(checkpoint);
    }

    private static void AddNpc(string zone, NPCData npc)
    {
        if (!NPCsByZone.TryGetValue(zone, out List<NPCData> list))
        {
            list = new List<NPCData>();
            NPCsByZone.Add(zone, list);
        }
        list.Add(npc);
        if (string.Equals(zone, "town", StringComparison.OrdinalIgnoreCase)) TownNPCs.Add(npc);
        if (string.Equals(zone, "tutorial", StringComparison.OrdinalIgnoreCase)) TutorialNPCs.Add(npc);
        if (string.Equals(zone, "pvp_start", StringComparison.OrdinalIgnoreCase)) PvpNPCs.Add(npc);
    }

    private static void AddWorldEntity(string zone, WorldEntityData entity)
    {
        if (!WorldEntitiesByZone.TryGetValue(zone, out List<WorldEntityData> list))
        {
            list = new List<WorldEntityData>();
            WorldEntitiesByZone.Add(zone, list);
        }
        list.Add(entity);
    }

    private static void AddCategoryItem(Dictionary<string, List<ItemData>> categories, string category, ItemData item)
    {
        if (!categories.TryGetValue(category, out List<ItemData> list))
        {
            list = new List<ItemData>();
            categories.Add(category, list);
        }
        list.Add(item);
    }

    private static bool TryLookup<T>(Dictionary<string, T> database, string gcType, out T value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(gcType))
            return false;
        if (database.TryGetValue(gcType, out value))
            return true;
        string packetPath = GCObject.GetPacketGCClassFor(gcType);
        return !string.Equals(packetPath, gcType, StringComparison.OrdinalIgnoreCase) && database.TryGetValue(packetPath, out value);
    }

    private static void Register<T>(Dictionary<string, T> database, string path, T value)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidDataException("Authored catalog key is empty");
        if (!database.TryAdd(path, value))
            throw new InvalidDataException($"Duplicate authored catalog key '{path}'");
        string packetPath = GCObject.GetPacketGCClassFor(path);
        if (!string.Equals(packetPath, path, StringComparison.OrdinalIgnoreCase) && !database.ContainsKey(packetPath))
            database.Add(packetPath, value);
    }

    private static GCNode Effective(GCNode node)
    {
        return GCDatabase.Instance.ResolveWithInheritance(node) ?? throw new InvalidDataException($"Unable to resolve authored inheritance path='{node?.CanonicalPath}'");
    }

    private static GCNode RequireRaw(string path)
    {
        return GCDatabase.Instance.Resolve(path) ?? throw new InvalidDataException($"Authored GC path is missing: {path}");
    }

    private static GCNode RequireResolved(string path)
    {
        return GCDatabase.Instance.ResolveWithInheritance(path) ?? throw new InvalidDataException($"Authored GC path is missing: {path}");
    }

    private static GCNode RequireChild(GCNode node, string childName, string path)
    {
        return node.GetChild(childName) ?? throw new InvalidDataException($"Authored child is missing path='{path}' child='{childName}'");
    }

    private static string RequireString(GCNode node, string property, string path)
    {
        string value = node.GetString(property, string.Empty);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"Authored property is missing path='{path}' property='{property}'");
        return value;
    }

    private static int RequireInt(GCNode node, string property, string path)
    {
        if (!node.HasProperty(property))
            throw new InvalidDataException($"Authored property is missing path='{path}' property='{property}'");
        string raw = node.GetString(property, string.Empty);
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            throw new InvalidDataException($"Authored property is not an integer path='{path}' property='{property}' value='{raw}'");
        return value;
    }

    private static bool TryReadPosition(GCNode node, out int x, out int y, out int z)
    {
        x = 0;
        y = 0;
        z = 0;
        string raw = node.GetString("Position", string.Empty);
        string[] parts = raw.Split(',');
        return parts.Length == 3
            && GCNode.TryParseFixed32(parts[0], out x)
            && GCNode.TryParseFixed32(parts[1], out y)
            && GCNode.TryParseFixed32(parts[2], out z);
    }

    private static bool InheritanceContains(GCNode node, string token)
    {
        if (node == null || string.IsNullOrWhiteSpace(token))
            return false;
        if ((node.CanonicalPath ?? string.Empty).Contains(token, StringComparison.OrdinalIgnoreCase)
            || (node.Extends ?? string.Empty).Contains(token, StringComparison.OrdinalIgnoreCase))
            return true;
        string current = node.Extends;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (!string.IsNullOrWhiteSpace(current) && visited.Add(current))
        {
            if (current.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
            GCNode parent = GCDatabase.Instance.Resolve(current);
            current = parent?.Extends;
        }
        return false;
    }

    private static bool IsCreature(GCNode node, GCNode description)
    {
        return !IsNpc(node)
            && (InheritanceContains(node, "creatures.") || (node.CanonicalPath ?? string.Empty).StartsWith("creatures.", StringComparison.OrdinalIgnoreCase))
            && (description.HasProperty("CreatureDifficulty") || description.HasProperty("CreatureFamily"));
    }

    private static bool IsSummonedUnit(GCNode node)
    {
        return (node.CanonicalPath ?? string.Empty).StartsWith("creatures.summon.", StringComparison.OrdinalIgnoreCase)
            && InheritanceContains(node, "base.Summonable");
    }

    private static bool IsNpc(GCNode node)
    {
        string path = node?.Extends ?? node?.CanonicalPath ?? string.Empty;
        return path.Contains(".npc.", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("npc.", StringComparison.OrdinalIgnoreCase)
            || InheritanceContains(node, "npc.avatar")
            || InheritanceContains(node, "npc.guard")
            || InheritanceContains(node, "npc.oldman");
    }

    private static bool IsItemNamespace(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        string lower = path.ToLowerInvariant();
        return lower.StartsWith("items.")
            || lower.Contains("pal.")
            || lower.StartsWith("questitem")
            || lower.StartsWith("potionpal.")
            || lower.StartsWith("potionpal2.");
    }

    private static bool HasAuthoredItemDimensions(GCNode description)
    {
        return description != null
            && description.HasProperty("InventoryWidth")
            && description.HasProperty("InventoryHeight");
    }

    private static bool IsNativeItemDescription(GCNode description)
    {
        return description != null
            && (InheritanceContains(description, "ItemDesc")
                || InheritanceContains(description, "WeaponDesc")
                || InheritanceContains(description, "ArmorDesc"));
    }

    private static string ResolveBehaviorType(GCNode effective)
    {
        GCNode behavior = effective.GetChild("Behavior");
        return behavior?.Extends ?? behavior?.CanonicalPath ?? string.Empty;
    }

    private static string ObjectiveType(GCNode node)
    {
        string path = node?.Extends ?? string.Empty;
        if (path.Contains("KillObjective", StringComparison.OrdinalIgnoreCase)) return "kill";
        if (path.Contains("ItemObjective", StringComparison.OrdinalIgnoreCase)) return "item";
        if (path.Contains("GoToObjective", StringComparison.OrdinalIgnoreCase) || path.Contains("GotoObjective", StringComparison.OrdinalIgnoreCase)) return "goto";
        if (path.Contains("ActivateObjective", StringComparison.OrdinalIgnoreCase)) return "activate";
        return null;
    }

    private static bool IsObjectiveTargetProperty(string name)
    {
        return name.StartsWith("MonsterType", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ItemType", StringComparison.OrdinalIgnoreCase)
            || name.Equals("TargetZoneName", StringComparison.OrdinalIgnoreCase)
            || name.Equals("TargetEntityName", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("EntityType", StringComparison.OrdinalIgnoreCase)
            || name.Equals("NPC", StringComparison.OrdinalIgnoreCase);
    }

    private static string ClassifyWorldEntity(GCNode placement, GCNode description)
    {
        string evidence = string.Join("|", GCDatabase.Instance.GetInheritanceChainPaths(placement.Extends)).ToLowerInvariant();
        if (!evidence.Contains("noncombatinteractive") && !evidence.Contains("interactives."))
            return null;
        if (evidence.Contains("chest") || description.EnumeratePropertiesInOrder().Any(property => property.Key.StartsWith("ItemGenerator", StringComparison.OrdinalIgnoreCase))) return "chest";
        if (evidence.Contains("teleport") || description.HasProperty("TeleportZone")) return "teleporter";
        if (evidence.Contains("shrine") || description.HasProperty("SpellEffect")) return "shrine";
        if (evidence.Contains("gate") || evidence.Contains("door")) return "gate";
        if (evidence.Contains("npc")) return "npc";
        return null;
    }

    private static IEnumerable<GCNode> EnumerateTree(GCNode root)
    {
        yield return root;
        foreach (GCNode child in root.EnumerateChildrenInOrder())
            foreach (GCNode descendant in EnumerateTree(child))
                yield return descendant;
    }

    private static string ItemFamily(string gcType)
    {
        string family = (gcType ?? string.Empty).Split('.')[0].ToLowerInvariant();
        if (family.EndsWith("pal", StringComparison.OrdinalIgnoreCase))
            family = family.Substring(0, family.Length - 3);
        while (family.Length > 0 && char.IsDigit(family[family.Length - 1]))
            family = family.Substring(0, family.Length - 1);
        return string.IsNullOrWhiteSpace(family) ? "unknown" : family;
    }

    private static string Leaf(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;
        int dot = path.LastIndexOf('.');
        return dot >= 0 ? path.Substring(dot + 1) : path;
    }

    private static uint ParseUInt(string value)
    {
        string text = (value ?? string.Empty).Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hex) ? hex : 0;
        return uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint number) ? number : 0;
    }

    private static uint StableZoneId(string name)
    {
        byte[] digest = MD5.HashData(Encoding.UTF8.GetBytes(name.ToLowerInvariant()));
        return BitConverter.ToUInt32(digest, 0);
    }
}
