using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using DungeonRunners.Engine;
using DungeonRunners.Data;
using DungeonRunners.Combat;
using DungeonRunners.Core;

namespace DungeonRunners.Gameplay
{

    public class GCObjectGeneratorTable
    {
        private static GCObjectGeneratorTable _instance;
        public static GCObjectGeneratorTable Instance => _instance ??= new GCObjectGeneratorTable();

        private bool _initialized;
        private static bool VerboseGeneratorLogging => ServerDiagnostics.IsEnabled("verboseRngLogging");
        private static bool VerboseItemLogging => ServerDiagnostics.IsEnabled("verboseMerchantItemLogging");

        private Dictionary<string, TreasureGenerator> _itemGenerators;
        private Dictionary<string, CurrencyGenerator> _currencyGenerators;

        private sealed class AuthoredGeneratorCandidate
        {
            public GCNode Entry;
            public int Order;
            public int Chance;
            public int MinLevel;
            public int MaxLevel;
            public string FilterUnitType;
        }

        private sealed class AuthoredGeneratorGroups
        {
            public IGrouping<int, AuthoredGeneratorCandidate>[] Groups;
        }

        private static readonly ConditionalWeakTable<GCNode, AuthoredGeneratorGroups> AuthoredGroups = new ConditionalWeakTable<GCNode, AuthoredGeneratorGroups>();

        private static IGrouping<int, AuthoredGeneratorCandidate>[] GetAuthoredGeneratorGroups(GCNode node)
        {
            return AuthoredGroups.GetValue(node, BuildAuthoredGeneratorGroups).Groups;
        }

        private static AuthoredGeneratorGroups BuildAuthoredGeneratorGroups(GCNode node)
        {
            return new AuthoredGeneratorGroups
            {
                Groups = EnumerateOrderedChildren(node)
                    .Select(ResolveChildWithInheritance)
                    .Where(entry => entry != null)
                    .Select((entry, order) => new AuthoredGeneratorCandidate
                    {
                        Entry = entry,
                        Order = order,
                        Chance = entry.GetInt("Chance", 1),
                        MinLevel = entry.GetInt("MinLevel", 1),
                        MaxLevel = entry.GetInt("MaxLevel", 100),
                        FilterUnitType = entry.GetString("FilterUnitType", "")
                    })
                    .Where(candidate => candidate.Chance > 0)
                    .GroupBy(candidate => candidate.Chance)
                    .OrderByDescending(group => group.Key)
                    .ToArray()
            };
        }

        private static readonly string[] PackageBackedItemGeneratorNames =
        {
            "DefaultIG",
            "ChampionIG",
            "HeroIG",
            "TreasureChestIG",
            "TreasureChestSmallIG",
            "TreasureChestBossIG",
            "TreasureChestMediumIG",
            "TreasureChestLargeIG"
        };

        private static readonly string[] PackageBackedCurrencyGeneratorNames =
        {
            "DefaultGG",
            "ChampionGG",
            "HeroGG",
            "LegendGG",
            "TreasureChestSmallGG"
        };

        private int RollLootRandom(int maxExclusive, string phase, string owner)
        {
            return RollLootRandom(maxExclusive, phase, owner, out _, out _);
        }

        private int RollLootRandom(int maxExclusive, string phase, string owner, out int rawIndex, out uint raw)
        {
            rawIndex = RandomStreams.GlobalStaticCalls;
            raw = 0;
            if (maxExclusive <= 0)
                return 0;
            raw = RandomStreams.GenerateGlobalStatic(
                phase ?? "GCObjectGeneratorTable::GenerateObjectFromTable",
                owner ?? "GCObjectGeneratorTable");
            int value = (int)(raw % (uint)maxExclusive);
            if (VerboseGeneratorLogging)
            {
                Debug.LogError($"[GC-OBJECT-GENERATOR-TABLE] stream=globalStatic seed=0x{RandomStreams.GlobalStaticSeed:X8} rawIndex={rawIndex} phase={phase ?? "draw"} raw=0x{raw:X8} value={value} max={maxExclusive} owner='{owner ?? "unknown"}' sourceFunction=GCObjectGeneratorTable<Item>::GenerateObjectFromTable@0x0059DC30 RandomItemGenerator::GenerateObjectFromTable@0x005A02E0");
            }
            return value;
        }

        private int RollLootRandom(int minInclusive, int maxExclusive, string phase, string owner)
        {
            if (maxExclusive <= minInclusive)
                return minInclusive;
            int rawIndex = RandomStreams.GlobalStaticCalls;
            uint raw = RandomStreams.GenerateGlobalStatic(
                phase ?? "GCObjectGeneratorTable::GenerateObjectFromTable.range",
                owner ?? "GCObjectGeneratorTable");
            int span = maxExclusive - minInclusive;
            int value = minInclusive + (int)(raw % (uint)span);
            if (VerboseGeneratorLogging)
            {
                Debug.LogError($"[GC-OBJECT-GENERATOR-TABLE] stream=globalStatic seed=0x{RandomStreams.GlobalStaticSeed:X8} rawIndex={rawIndex} phase={phase ?? "draw"} raw=0x{raw:X8} value={value} range=[{minInclusive},{maxExclusive}) owner='{owner ?? "unknown"}' sourceFunction=GCObjectGeneratorTable<Item>::GenerateObjectFromTable@0x0059DC30 Random::generate@0x0044B1F0");
            }
            return value;
        }

        public void Initialize()
        {
            if (_initialized) return;

            BuildItemGenerators();
            BuildCurrencyGenerators();
            LoadClientExcludedItems();

            _initialized = true;
            Debug.LogError($"[GC-OBJECT-GENERATOR-TABLE] loaded itemGenerators={_itemGenerators.Count} currencyGenerators={_currencyGenerators.Count} clientExcluded={_clientExcludedItems.Count}");
        }

        private static HashSet<string> _clientExcludedItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static readonly Regex _newClassRegex = new Regex(
            @"^[\t ]*([A-Za-z0-9][A-Za-z0-9_-]*)\s+extends\s+\S+\s*//.*NEW!",
            RegexOptions.Compiled);

        private static readonly Regex _renamedClassRegex = new Regex(
            @"^[\t ]*([A-Za-z0-9][A-Za-z0-9_-]*)\s+extends\s+\S+\s*//\s*was\s",
            RegexOptions.Compiled);

        private void LoadClientExcludedItems()
        {
            _clientExcludedItems.Clear();

            int filesScanned = 0;
            try
            {
                var packageCatalog = PackageCatalog.Instance;
                if (!packageCatalog.IsLoaded)
                    packageCatalog.LoadFromAssets();
                if (!packageCatalog.IsLoaded)
                    throw new InvalidDataException("authored.db GC catalog is unavailable");
                foreach (var document in packageCatalog.EnumerateGcTextDocuments("*.gc"))
                {
                    if (document == null || string.IsNullOrWhiteSpace(document.Text))
                        continue;
                    string ns = document.Stem.ToLowerInvariant();
                    filesScanned++;
                    using var reader = new StringReader(document.Text);
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        var newClassMatch = _newClassRegex.Match(line);
                        if (newClassMatch.Success)
                        {
                            string className = newClassMatch.Groups[1].Value.ToLowerInvariant();
                            _clientExcludedItems.Add($"{ns}.{className}");
                            continue;
                        }
                        var renamedClassMatch = _renamedClassRegex.Match(line);
                        if (renamedClassMatch.Success)
                        {
                            string className = renamedClassMatch.Groups[1].Value.ToLowerInvariant();
                            _clientExcludedItems.Add($"{ns}.{className}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GC-OBJECT-GENERATOR-TABLE] clientExcludedItems error={ex.Message}");
            }

            Debug.LogError($"[GC-OBJECT-GENERATOR-TABLE] gcFiles={filesScanned} clientExcluded={_clientExcludedItems.Count}");
        }


        private void BuildItemGenerators()
        {
            _itemGenerators = new Dictionary<string, TreasureGenerator>(StringComparer.OrdinalIgnoreCase);
            if (GCDatabase.Instance == null || !GCDatabase.Instance.IsLoaded)
                throw new InvalidDataException("GCDatabase must be loaded before package-backed loot generators");

            var missing = new List<string>();
            foreach (string generatorName in PackageBackedItemGeneratorNames)
            {
                if (TryBuildAuthoredTreasureGenerator(generatorName, out var generator))
                {
                    _itemGenerators[generatorName] = generator;
                    continue;
                }
                missing.Add(generatorName);
            }

            if (missing.Count > 0)
                throw new InvalidDataException($"Package-backed item generators missing: {string.Join(",", missing)}");

            Debug.LogError($"[GC-OBJECT-GENERATOR-TABLE] kind=item source=GC-DATABASE+PACKAGE-CATALOG packageBacked={PackageBackedItemGeneratorNames.Length} total={_itemGenerators.Count}");
        }


        private void BuildCurrencyGenerators()
        {
            _currencyGenerators = new Dictionary<string, CurrencyGenerator>(StringComparer.OrdinalIgnoreCase);

            var missing = new List<string>();
            foreach (string generatorName in PackageBackedCurrencyGeneratorNames)
            {
                if (TryBuildAuthoredCurrencyGenerator(generatorName, out var generator))
                {
                    _currencyGenerators[generatorName] = generator;
                    continue;
                }
                missing.Add(generatorName);
            }

            if (missing.Count > 0)
                throw new InvalidDataException($"Package-backed gold generators missing: {string.Join(",", missing)}");

            Debug.LogError($"[GC-OBJECT-GENERATOR-TABLE] kind=currency source=GC-DATABASE+PACKAGE-CATALOG packageBacked={PackageBackedCurrencyGeneratorNames.Length} total={_currencyGenerators.Count}");
        }

        private bool TryBuildAuthoredTreasureGenerator(string generatorName, out TreasureGenerator generator)
        {
            generator = null;
            GCNode node = ResolvePackageGeneratorNode(generatorName);
            if (node == null)
                return false;

            var entries = new List<(TreasureEntry entry, int order)>();
            int order = 0;
            int skipped = 0;
            foreach (GCNode child in EnumerateOrderedChildren(node))
            {
                GCNode entryNode = ResolveChildWithInheritance(child);
                if (!TryResolveItemRarity(entryNode, out var rarity))
                {
                    skipped++;
                    continue;
                }

                int chance = entryNode.GetInt("Chance", 1);
                int minLevel = entryNode.GetInt("MinLevel", 1);
                int maxLevel = entryNode.GetInt("MaxLevel", 100);
                string linkedGenerator = entryNode.GetString("LinkedGenerator", "");
                string filterUnitType = entryNode.GetString("FilterUnitType", "");
                entries.Add((new TreasureEntry(rarity, chance, minLevel, maxLevel, linkedGenerator, filterUnitType, entryNode.CanonicalPath, entryNode.PackageEntryId), order++));
            }

            if (entries.Count == 0)
                return false;

            generator = new TreasureGenerator(entries
                .OrderByDescending(x => x.entry.Chance)
                .ThenBy(x => x.order)
                .Select(x => x.entry)
                .ToArray(), node.GetString("FilterUnitType", ""), node.CanonicalPath, node.PackageEntryId);

            Debug.LogError($"[GC-OBJECT-GENERATOR-TABLE] kind=item generator={generatorName} entries={generator.Entries.Length} skippedNonItem={skipped} source=package-backed");
            return true;
        }

        private bool TryBuildAuthoredCurrencyGenerator(string generatorName, out CurrencyGenerator generator)
        {
            generator = null;
            GCNode node = ResolvePackageGeneratorNode(generatorName);
            if (node == null)
                return false;

            var entries = new List<(CurrencyEntry entry, int order)>();
            int order = 0;
            int skipped = 0;
            foreach (GCNode child in EnumerateOrderedChildren(node))
            {
                GCNode entryNode = ResolveChildWithInheritance(child);
                if (!entryNode.HasProperty("GoldValue"))
                {
                    skipped++;
                    continue;
                }

                entries.Add((new CurrencyEntry(
                    entryNode.GetInt("Chance", 1),
                    entryNode.GetFixed32("GoldValue", 0),
                    entryNode.GetFixed32("Volatility", 0),
                    entryNode.GetInt("MinLevel", 1),
                    entryNode.GetInt("MaxLevel", 100),
                    entryNode.GetString("FilterUnitType", ""),
                    entryNode.CanonicalPath,
                    entryNode.PackageEntryId), order++));
            }

            if (entries.Count == 0)
                return false;

            generator = new CurrencyGenerator(entries
                .OrderByDescending(x => x.entry.Chance)
                .ThenBy(x => x.order)
                .Select(x => x.entry)
                .ToArray(), node.GetString("FilterUnitType", ""), node.CanonicalPath, node.PackageEntryId);

            Debug.LogError($"[GC-OBJECT-GENERATOR-TABLE] kind=currency generator={generatorName} entries={generator.Entries.Length} skippedNonCurrency={skipped} source=package-backed");
            return true;
        }

        private static GCNode ResolvePackageGeneratorNode(string generatorName)
        {
            if (string.IsNullOrWhiteSpace(generatorName))
                return null;
            return GCDatabase.Instance?.ResolveWithInheritance(generatorName);
        }

        private static IEnumerable<GCNode> EnumerateOrderedChildren(GCNode node)
        {
            if (node == null)
                yield break;
            foreach (GCNode child in node.EnumerateChildrenInOrder())
                yield return child;
        }

        private static GCNode ResolveChildWithInheritance(GCNode child)
        {
            if (child == null)
                return null;
            if (string.IsNullOrWhiteSpace(child.Extends))
                return child;

            GCNode parent = GCDatabase.Instance?.ResolveWithInheritance(child.Extends);
            if (parent == null)
                return child;
            return GCNode.MergeInherited(parent, child);
        }

        private static bool TryResolveItemRarity(GCNode entry, out ItemRarity rarity)
        {
            rarity = ItemRarity.Normal;
            string token = ((entry?.Name ?? "") + " " + (entry?.Extends ?? "")).ToLowerInvariant();
            if (token.Contains("mythic"))
            {
                rarity = ItemRarity.Mythic;
                return true;
            }
            if (token.Contains("unique"))
            {
                rarity = ItemRarity.Unique;
                return true;
            }
            if (token.Contains("rare"))
            {
                rarity = ItemRarity.Rare;
                return true;
            }
            if (token.Contains("magic"))
            {
                rarity = ItemRarity.Magical;
                return true;
            }
            if (token.Contains("superior"))
            {
                rarity = ItemRarity.Superior;
                return true;
            }
            if (token.Contains("normal"))
            {
                rarity = ItemRarity.Normal;
                return true;
            }
            return false;
        }

        private CreatureTreasureData ResolveAuthoredCreatureTreasure(Monster monster)
        {
            if (monster == null || GCDatabase.Instance == null)
                return null;

            var paths = new List<string>();
            if (!string.IsNullOrWhiteSpace(monster.SpawnGCType)) paths.Add(monster.SpawnGCType);
            if (!string.IsNullOrWhiteSpace(monster.GCType)) paths.Add(monster.GCType);
            if (monster.AuthoredArchetypeAncestry != null)
                paths.AddRange(monster.AuthoredArchetypeAncestry);

            foreach (string path in paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                GCNode node = GCDatabase.Instance.ResolveWithInheritance(path);
                if (node == null)
                    continue;

                var data = new CreatureTreasureData();
                for (int slot = 1; slot <= 10; slot++)
                {
                    string genKey = slot == 1 ? "TreasureGenerator" : $"TreasureGenerator{slot}";
                    string countKey = slot == 1 ? "TreasureCount" : $"TreasureCount{slot}";
                    string generator = GetEffectiveString(node, genKey);
                    int count = GetEffectiveInt(node, countKey, string.IsNullOrWhiteSpace(generator) ? 0 : 1);
                    if (!string.IsNullOrWhiteSpace(generator) && count > 0)
                        data.Generators.Add((generator, count));
                }

                if (data.Generators.Count > 0)
                {
                    if (VerboseItemLogging)
                    {
                        Debug.LogError($"[LOOT-AUTHORED] source=PKG creature={monster.Name}#{monster.EntityId} path='{path}' generators={string.Join(",", data.Generators.Select(g => $"{g.gen}x{g.count}"))}");
                    }
                    return data;
                }
            }

            return null;
        }


        public List<LootDrop> GenerateMobLoot(Monster monster, int playerLevel, bool isMember = false, string unitGcType = "avatar.base.Avatar")
        {
            return GenerateMobLoot(
                monster,
                new[]
                {
                    new LootGenerationProfile
                    {
                        PlayerLevel = playerLevel,
                        IsMember = isMember,
                        UnitGcType = unitGcType
                    }
                },
                0,
                out _);
        }

        public List<LootDrop> GenerateMobLoot(Monster monster, IReadOnlyList<LootGenerationProfile> profiles, int startRecipientIndex, out int recipientAssignments)
        {
            if (!_initialized) Initialize();
            var drops = new List<LootDrop>();
            recipientAssignments = 0;

            var treasure = ResolveAuthoredCreatureTreasure(monster);
            if (treasure == null)
            {
                Debug.LogError($"[AUTHORED-COVERAGE] area=loot reason=missing-treasure monster={monster.Name} gc={monster.GCType} spawnGc={monster.SpawnGCType ?? ""} tier={monster.Tier}");
                return drops;
            }

            string source = $"mob:{monster.Name}#{monster.EntityId}:deathTick={CombatRuntime.Instance.CombatTick}:roomEpoch={monster.InstanceKey ?? ""}";
            RollTreasureData(treasure, drops, source, $"source=mob monster={monster.Name} gc={monster.GCType}", profiles, startRecipientIndex, ref recipientAssignments, monster.LootFactorF32);

            if (drops.Count > 0)
                Debug.LogError($"[GC-OBJECT-GENERATOR-TABLE] monster={monster.Name} tier={monster.Tier} gold={drops.Count(d => d.IsGold)} items={drops.Count(d => d.IsItem)} kingsCoin={drops.Count(d => d.IsKingsCoin)}");
            return drops;
        }

        public List<LootDrop> GenerateDestroyableLoot(Monster monster, int playerLevel, bool isMember = false, string unitGcType = "avatar.base.Avatar")
        {
            return GenerateDestroyableLoot(
                monster,
                new[]
                {
                    new LootGenerationProfile
                    {
                        PlayerLevel = playerLevel,
                        IsMember = isMember,
                        UnitGcType = unitGcType
                    }
                },
                0,
                out _);
        }

        public List<LootDrop> GenerateDestroyableLoot(Monster monster, IReadOnlyList<LootGenerationProfile> profiles, int startRecipientIndex, out int recipientAssignments)
        {
            if (!_initialized) Initialize();
            var drops = new List<LootDrop>();
            recipientAssignments = 0;

            var treasure = ResolveAuthoredCreatureTreasure(monster);
            if (treasure != null)
            {
                string source = $"destroyable:{monster.Name}#{monster.EntityId}:deathTick={CombatRuntime.Instance.CombatTick}:roomEpoch={monster.InstanceKey ?? ""}";
                RollTreasureData(treasure, drops, source, $"source=destroyable monster={monster.Name} gc={monster.GCType}", profiles, startRecipientIndex, ref recipientAssignments);
                if (VerboseItemLogging)
                {
                    Debug.LogError($"[LOOT-AUTHORED] source=destroyable monster={monster.Name}#{monster.EntityId} gc={monster.GCType} drops={drops.Count}");
                }
                return drops;
            }

            Debug.LogError($"[AUTHORED-COVERAGE] area=loot reason=missing-destroyable-treasure monster={monster?.Name ?? "<null>"} gc={monster?.GCType ?? "<null>"}");
            return drops;
        }

        private void RollTreasureData(CreatureTreasureData treasure, List<LootDrop> drops, string source, string fallbackContext, IReadOnlyList<LootGenerationProfile> profiles, int startRecipientIndex, ref int recipientAssignments, ushort lootFactorF32 = 0x100)
        {
            foreach (var (generatorType, rollCount) in treasure.Generators)
            {
                int scaledRollCount = MonsterDifficultySettings.ScaleLootRollCount(rollCount, lootFactorF32,
                    () => RollLootRandom(256, "difficulty-loot-fraction", source));
                for (int rollIndex = 0; rollIndex < scaledRollCount; rollIndex++)
                {
                    int recipientOrdinal = startRecipientIndex + recipientAssignments;
                    LootGenerationProfile profile = ResolveLootGenerationProfile(generatorType, profiles, recipientOrdinal);
                    recipientAssignments++;
                    int playerLevel = Math.Clamp(profile?.PlayerLevel ?? 1, 1, 110);
                    bool isMember = profile?.IsMember ?? false;
                    string unitGcType = profile?.UnitGcType ?? "avatar.base.Avatar";
                    if (_currencyGenerators.TryGetValue(generatorType, out var currencyGen))
                    {
                        int gold = RollCurrency(currencyGen, playerLevel, isMember, unitGcType, source);
                        if (gold > 0)
                        {
                            LootDrop drop = LootDrop.Gold(gold);
                            AssignLootRecipient(drop, ResolveDropRecipient(drop, profiles, profile, recipientOrdinal));
                            drops.Add(drop);
                        }
                    }
                    else if (_itemGenerators.TryGetValue(generatorType, out var itemGen))
                    {
                        var item = RollItem(itemGen, playerLevel, isMember, unitGcType, null, source);
                        if (item != null)
                        {
                            AssignLootRecipient(item, ResolveDropRecipient(item, profiles, profile, recipientOrdinal));
                            drops.Add(item);
                        }
                    }
                    else if (TryRollAuthoredGenerator(generatorType, playerLevel, isMember, out var authoredDrops, source, null, unitGcType))
                    {
                        foreach (LootDrop authoredDrop in authoredDrops)
                            AssignLootRecipient(authoredDrop, ResolveDropRecipient(authoredDrop, profiles, profile, recipientOrdinal));
                        drops.AddRange(authoredDrops);
                    }
                    else
                    {
                        RuntimeEvidence.LogFallbackHit(
                            "gc-object-generator-table",
                            "missing-generator",
                            $"{fallbackContext} generator={generatorType}",
                            32);
                        Debug.LogError($"[LOOT-FALLBACK] reason=missing-generator {fallbackContext} generator={generatorType}");
                    }
                }
            }
        }

        private LootGenerationProfile ResolveLootGenerationProfile(string generatorType, IReadOnlyList<LootGenerationProfile> profiles, int recipientOrdinal)
        {
            if (profiles == null || profiles.Count == 0)
                return new LootGenerationProfile { PlayerLevel = 1, UnitGcType = "avatar.base.Avatar" };

            string filterUnitType = string.Empty;
            if (_itemGenerators.TryGetValue(generatorType, out TreasureGenerator itemGenerator))
                filterUnitType = itemGenerator.FilterUnitType;
            else if (_currencyGenerators.TryGetValue(generatorType, out CurrencyGenerator currencyGenerator))
                filterUnitType = currencyGenerator.FilterUnitType;
            else
                filterUnitType = GCDatabase.Instance?.ResolveWithInheritance(generatorType)?.GetString("FilterUnitType", string.Empty) ?? string.Empty;

            int startIndex = recipientOrdinal % profiles.Count;
            if (startIndex < 0)
                startIndex += profiles.Count;
            for (int offset = 0; offset < profiles.Count; offset++)
            {
                LootGenerationProfile candidate = profiles[(startIndex + offset) % profiles.Count];
                if (candidate == null)
                    continue;
                if (MatchesFilterUnitType(filterUnitType, candidate.UnitGcType))
                    return candidate;
            }

            return profiles[startIndex] ?? new LootGenerationProfile { PlayerLevel = 1, UnitGcType = "avatar.base.Avatar" };
        }

        private static LootGenerationProfile ResolveDropRecipient(LootDrop drop, IReadOnlyList<LootGenerationProfile> profiles, LootGenerationProfile fallback, int recipientOrdinal)
        {
            string affinity = ResolveExplicitItemClassAffinity(drop?.GCType);
            if (string.IsNullOrEmpty(affinity) || profiles == null || profiles.Count == 0)
                return fallback;

            int startIndex = recipientOrdinal % profiles.Count;
            if (startIndex < 0)
                startIndex += profiles.Count;
            for (int offset = 0; offset < profiles.Count; offset++)
            {
                LootGenerationProfile candidate = profiles[(startIndex + offset) % profiles.Count];
                if (MatchesExplicitItemClassAffinity(candidate, affinity))
                    return candidate;
            }
            return fallback;
        }

        private static string ResolveExplicitItemClassAffinity(string gcType)
        {
            if (string.IsNullOrWhiteSpace(gcType))
                return string.Empty;
            string identity = gcType.ToLowerInvariant();
            if (identity.Contains("fighter"))
                return "fighter";
            if (identity.Contains("mage"))
                return "mage";
            if (identity.Contains("ranger"))
                return "ranger";
            return string.Empty;
        }

        private static bool MatchesExplicitItemClassAffinity(LootGenerationProfile profile, string affinity)
        {
            if (profile == null || string.IsNullOrWhiteSpace(affinity))
                return false;
            string identity = $"{profile.ClassName ?? string.Empty} {profile.UnitGcType ?? string.Empty}";
            return identity.IndexOf(affinity, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void AssignLootRecipient(LootDrop drop, LootGenerationProfile profile)
        {
            if (drop == null || profile == null)
                return;
            drop.RecipientCharacterId = profile.CharacterId;
            drop.RecipientPlayerLevel = Math.Clamp(profile.PlayerLevel, 1, 110);
        }

        public List<LootDrop> GenerateChestLoot(string generatorName, int itemCount, int playerLevel, bool isMember = false, string unitGcType = "avatar.base.Avatar")
        {
            if (!_initialized) Initialize();
            var drops = new List<LootDrop>();
            if (itemCount < 0)
                throw new ArgumentOutOfRangeException(nameof(itemCount));
            if (itemCount == 0)
                return drops;

            if (_itemGenerators.TryGetValue(generatorName, out var itemGenerator))
                for (int itemIndex = 0; itemIndex < itemCount; itemIndex++)
                {
                    var item = RollItem(itemGenerator, playerLevel, isMember, unitGcType, null, "chest");
                    if (item != null) drops.Add(item);
                }
            else if (TryRollAuthoredGenerator(generatorName, playerLevel, isMember, out var authoredDrops, "chest", null, unitGcType))
            {
                drops.AddRange(authoredDrops.Take(itemCount));
            }
            else
            {
                RuntimeEvidence.LogFallbackHit(
                    "gc-object-generator-table",
                    "missing-chest-generator",
                    $"generator={generatorName ?? "<null>"} itemCount={itemCount} playerLevel={playerLevel}",
                    32);
                Debug.LogError($"[LOOT-FALLBACK] reason=missing-chest-generator generator={generatorName ?? "<null>"} itemCount={itemCount} playerLevel={playerLevel}");
            }

            return drops;
        }

        public List<LootDrop> GenerateAuthoredGeneratorLoot(string generatorName, int itemCount, int playerLevel, bool isMember = false, string source = null, string unitGcType = "avatar.base.Avatar")
        {
            if (!_initialized) Initialize();
            var drops = new List<LootDrop>();
            if (itemCount < 0)
                throw new ArgumentOutOfRangeException(nameof(itemCount));
            if (itemCount == 0)
                return drops;
            int rollCount = itemCount;
            for (int rollIndex = 0; rollIndex < rollCount; rollIndex++)
            {
                if (_itemGenerators.TryGetValue(generatorName, out var itemGen))
                {
                    var item = RollItem(itemGen, playerLevel, isMember, unitGcType, null, source ?? "authored-generator");
                    if (item != null) drops.Add(item);
                    continue;
                }
                if (_currencyGenerators.TryGetValue(generatorName, out var currencyGen))
                {
                    int gold = RollCurrency(currencyGen, playerLevel, isMember, unitGcType, source ?? "authored-generator");
                    if (gold > 0) drops.Add(LootDrop.Gold(gold));
                    continue;
                }
                if (TryRollAuthoredGenerator(generatorName, playerLevel, isMember, out var authoredDrops, source ?? "authored-generator", null, unitGcType))
                    drops.AddRange(authoredDrops);
            }
            return drops;
        }

        public bool CanResolveAuthoredGenerator(string generatorName)
        {
            if (!_initialized) Initialize();
            if (string.IsNullOrWhiteSpace(generatorName))
                return false;
            if (_itemGenerators.ContainsKey(generatorName) || _currencyGenerators.ContainsKey(generatorName))
                return true;
            return GCDatabase.Instance?.ResolveWithInheritance(generatorName) != null;
        }

        private bool TryRollAuthoredGenerator(string generatorName, int playerLevel, bool isMember, out List<LootDrop> drops, string source, ItemRarity? forcedRarity = null, string unitGcType = "avatar.base.Avatar")
        {
            drops = new List<LootDrop>();
            if (string.IsNullOrWhiteSpace(generatorName))
                return false;
            var node = GCDatabase.Instance?.ResolveWithInheritance(generatorName);
            if (node == null)
                return false;

            RollAuthoredGeneratorNode(node, generatorName, playerLevel, isMember, drops, new HashSet<string>(StringComparer.OrdinalIgnoreCase), source, forcedRarity, unitGcType);
            return true;
        }

        private void RollAuthoredGeneratorNode(GCNode node, string generatorName, int playerLevel, bool isMember, List<LootDrop> drops, HashSet<string> stack, string source, ItemRarity? forcedRarity, string unitGcType)
        {
            string stackKey = "item:" + (generatorName ?? node?.Name ?? "anonymous");
            if (node == null || !stack.Add(stackKey))
                return;

            try
            {
                if (!MatchesFilterUnitType(node.GetString("FilterUnitType", ""), unitGcType))
                    return;

                if (ProcessAuthoredGeneratorEntry(node, generatorName, playerLevel, isMember, drops, stack, source, forcedRarity, unitGcType))
                    return;

                var chanceGroups = GetAuthoredGeneratorGroups(node);

                foreach (var group in chanceGroups)
                {
                    string canonicalPath = string.IsNullOrWhiteSpace(node.CanonicalPath) ? generatorName : node.CanonicalPath;
                    string generatorIdentity = $"{canonicalPath}#{node.PackageEntryId}";
                    int chanceRoll = RollLootRandom(group.Key, "authored-generator-chance", $"{source ?? "unknown"}:{generatorIdentity}:chance={group.Key}", out int chanceRawIndex, out uint chanceRaw);
                    if (chanceRoll != 0)
                    {
                        if (VerboseGeneratorLogging)
                        {
                            Debug.LogError($"[LOOT-GENERATOR] source={source ?? "unknown"} canonical='{canonicalPath}' entry={node.PackageEntryId} chance={group.Key} chanceRawIndex={chanceRawIndex} chanceRaw=0x{chanceRaw:X8} chanceRoll={chanceRoll} result=skip");
                        }
                        continue;
                    }

                    var candidates = group
                        .Where(candidate => playerLevel >= candidate.MinLevel && playerLevel <= candidate.MaxLevel)
                        .Where(candidate => MatchesFilterUnitType(candidate.FilterUnitType, unitGcType))
                        .OrderBy(candidate => candidate.Order)
                        .ToList();
                    if (candidates.Count == 0)
                    {
                        if (VerboseGeneratorLogging)
                        {
                            Debug.LogError($"[LOOT-GENERATOR] source={source ?? "unknown"} canonical='{canonicalPath}' entry={node.PackageEntryId} chance={group.Key} chanceRawIndex={chanceRawIndex} chanceRaw=0x{chanceRaw:X8} chanceRoll={chanceRoll} candidates=0 result=filtered");
                        }
                        continue;
                    }

                    int selectedIndex = RollLootRandom(candidates.Count, "authored-generator-select", $"{source ?? "unknown"}:{generatorIdentity}:candidates={candidates.Count}", out int selectionRawIndex, out uint selectionRaw);
                    var selected = candidates[selectedIndex];
                    if (VerboseGeneratorLogging)
                    {
                        string candidateLedger = string.Join(",", candidates.Select(candidate => $"{candidate.Order}:{candidate.Entry.CanonicalPath ?? candidate.Entry.Name}#{candidate.Entry.PackageEntryId}"));
                        Debug.LogError($"[LOOT-GENERATOR] source={source ?? "unknown"} canonical='{canonicalPath}' entry={node.PackageEntryId} chance={group.Key} chanceRawIndex={chanceRawIndex} chanceRaw=0x{chanceRaw:X8} chanceRoll={chanceRoll} selectionRawIndex={selectionRawIndex} selectionRaw=0x{selectionRaw:X8} selectedIndex={selectedIndex} selected='{selected.Entry.CanonicalPath ?? selected.Entry.Name}#{selected.Entry.PackageEntryId}' candidates='{candidateLedger}' playerLevel={playerLevel} unit='{unitGcType}'");
                    }
                    int before = drops.Count;
                    bool handled = ProcessAuthoredGeneratorEntry(selected.Entry, generatorName, playerLevel, isMember, drops, stack, source, forcedRarity, unitGcType);
                    if (!handled)
                    {
                        string childName = string.IsNullOrWhiteSpace(selected.Entry.Name)
                            ? $"{generatorName}:anonymous:{selected.Order}"
                            : $"{generatorName}.{selected.Entry.Name}";
                        RollAuthoredGeneratorNode(selected.Entry, childName, playerLevel, isMember, drops, stack, source, forcedRarity, unitGcType);
                    }
                    if (drops.Count > before)
                        return;
                }
            }
            finally
            {
                stack.Remove(stackKey);
            }
        }

        private bool ProcessAuthoredGeneratorEntry(GCNode entry, string generatorName, int playerLevel, bool isMember, List<LootDrop> drops, HashSet<string> stack, string source, ItemRarity? forcedRarity, string unitGcType)
        {
            if (entry == null)
                return false;

            if (IsGeneratorKind(entry, "RandomItemGenerator"))
            {
                LootDrop generated = GenerateRandomItem(entry, generatorName, playerLevel, isMember, stack, source, forcedRarity, unitGcType);
                if (generated != null)
                    drops.Add(generated);
                return true;
            }

            string linked = entry.GetString("LinkedGenerator", "");
            if (!string.IsNullOrWhiteSpace(linked))
            {
                if (_itemGenerators.TryGetValue(linked, out var itemGen))
                {
                    var item = RollItem(itemGen, playerLevel, isMember, unitGcType, forcedRarity, source);
                    if (item != null) drops.Add(item);
                }
                else if (_currencyGenerators.TryGetValue(linked, out var currencyGen))
                {
                    int gold = RollCurrency(currencyGen, playerLevel, isMember, unitGcType, source);
                    if (gold > 0) drops.Add(LootDrop.Gold(gold));
                }
                else
                {
                    var linkedNode = GCDatabase.Instance.ResolveWithInheritance(linked);
                    RollAuthoredGeneratorNode(linkedNode, linked, playerLevel, isMember, drops, stack, source, forcedRarity, unitGcType);
                }
                return true;
            }

            string itemGenerator = entry.GetString("ItemGenerator", "");
            if (!string.IsNullOrWhiteSpace(itemGenerator))
            {
                int before = drops.Count;
                if (_itemGenerators.TryGetValue(itemGenerator, out var itemGen))
                {
                    LootDrop item = RollItem(itemGen, playerLevel, isMember, unitGcType, forcedRarity, source);
                    if (item != null)
                        drops.Add(item);
                }
                else
                {
                    GCNode itemGeneratorNode = GCDatabase.Instance?.ResolveWithInheritance(itemGenerator);
                    RollAuthoredGeneratorNode(itemGeneratorNode, itemGenerator, playerLevel, isMember, drops, stack, source, forcedRarity, unitGcType);
                }
                if (VerboseItemLogging)
                {
                    Debug.LogError($"[LOOT-AUTHORED] source={source ?? "unknown"} generator={generatorName} itemGenerator={itemGenerator} produced={drops.Count - before}");
                }
                return true;
            }

            string itemPath = entry.GetString("Item", "");
            if (!string.IsNullOrWhiteSpace(itemPath))
            {
                LootDrop item = GenerateSingleItem(entry, itemPath, playerLevel, source, forcedRarity);
                if (item != null)
                    drops.Add(item);
                return true;
            }

            if (entry.HasProperty("GoldValue"))
            {
                var goldEntry = new CurrencyEntry(
                    entry.GetInt("Chance", 1),
                    entry.GetFixed32("GoldValue", 0),
                    entry.GetFixed32("Volatility", 0),
                    entry.GetInt("MinLevel", 1),
                    entry.GetInt("MaxLevel", 100),
                    entry.GetString("FilterUnitType", ""),
                    entry.CanonicalPath,
                    entry.PackageEntryId);
                int gold = RollCurrencyAmount(goldEntry, playerLevel, isMember, source);
                if (gold > 0) drops.Add(LootDrop.Gold(gold));
                if (VerboseItemLogging)
                {
                    Debug.LogError($"[LOOT-AUTHORED] source={source ?? "unknown"} generator={generatorName} gold={gold}");
                }
                return true;
            }

            return false;
        }

        private LootDrop GenerateRandomItem(GCNode generator, string generatorName, int playerLevel, bool isMember, HashSet<string> stack, string source, ItemRarity? forcedRarity, string unitGcType)
        {
            string itemPath = generator.GetString("Item", "");
            LootDrop item = string.IsNullOrWhiteSpace(itemPath)
                ? null
                : GenerateSingleItem(generator, itemPath, playerLevel, source, forcedRarity, false);

            if (item == null)
            {
                string itemGenerator = generator.GetString("ItemGenerator", "");
                if (!string.IsNullOrWhiteSpace(itemGenerator))
                {
                    if (_itemGenerators.TryGetValue(itemGenerator, out var itemGen))
                    {
                        item = RollItem(itemGen, playerLevel, isMember, unitGcType, forcedRarity, source);
                    }
                    else
                    {
                        GCNode itemGeneratorNode = GCDatabase.Instance?.ResolveWithInheritance(itemGenerator);
                        var generated = new List<LootDrop>();
                        RollAuthoredGeneratorNode(itemGeneratorNode, itemGenerator, playerLevel, isMember, generated, stack, source, forcedRarity, unitGcType);
                        item = generated.FirstOrDefault(drop => drop != null && drop.IsItem);
                    }
                }
            }

            if (item == null)
                return null;

            for (int modifierSlot = 1; modifierSlot <= 10; modifierSlot++)
            {
                string modifierGenerator = generator.GetString($"ItemModGenerator{modifierSlot}", "");
                if (string.IsNullOrWhiteSpace(modifierGenerator))
                    continue;
                string modifier = RollItemModifierGenerator(modifierGenerator, playerLevel, unitGcType, stack, source);
                if (!string.IsNullOrWhiteSpace(modifier))
                    item.ItemModifiers.Add(modifier);
            }

            if (forcedRarity.HasValue)
                item.Rarity = forcedRarity.Value;
            else if (TryResolveItemRarity(generator, out var generatorRarity))
                item.Rarity = generatorRarity;
            item.ScaleMod = item.ItemModifiers.FirstOrDefault();
            RollRequiresMembership(item, $"{source ?? "unknown"}:{generatorName}:random-final");
            if (VerboseItemLogging)
            {
                GCNode canonicalItem = GCDatabase.Instance?.Resolve(item.GCType);
                Debug.LogError($"[LOOT-AUTHORED] source={source ?? "unknown"} generator={generatorName} canonicalGenerator='{generator.CanonicalPath ?? generatorName}' generatorEntry={generator.PackageEntryId} itemGenerator={generator.GetString("ItemGenerator", "")} item='{item.GCType}' itemCanonical='{canonicalItem?.CanonicalPath ?? ""}' itemEntry={canonicalItem?.PackageEntryId ?? 0} produced=1 modGenerators=10 modifiers={item.ItemModifiers.Count} membership={item.RequiresMembership}");
            }
            return item;
        }

        private LootDrop GenerateSingleItem(GCNode generator, string itemPath, int playerLevel, string source, ItemRarity? forcedRarity, bool rollMembership = true)
        {
            if (string.IsNullOrWhiteSpace(itemPath))
                return null;

            ItemRarity rarity;
            if (forcedRarity.HasValue)
                rarity = forcedRarity.Value;
            else if (!TryResolveItemRarity(generator, out rarity))
                rarity = RPGSettings.ResolveItemRarity(itemPath);

            GCNode itemNode = GCDatabase.Instance?.ResolveWithInheritance(itemPath);
            int itemLevel = GetEffectiveInt(itemNode, "Level", 0);
            if (itemLevel == 0)
                itemLevel = playerLevel + RollLootRandom(8, "single-item-level", $"{source ?? "unknown"}:{itemPath}");

            LootDrop item = LootDrop.Item(itemPath, GetItemLabel(itemPath), rarity, null, itemLevel);
            item.HasGeneratedItemState = true;
            if (rollMembership)
                RollRequiresMembership(item, $"{source ?? "unknown"}:{itemPath}:single");
            if (VerboseItemLogging)
            {
                Debug.LogError($"[LOOT-AUTHORED] source={source ?? "unknown"} item={itemPath} level={itemLevel}");
            }
            return item;
        }

        private string RollItemModifierGenerator(string generatorName, int playerLevel, string unitGcType, HashSet<string> stack, string source)
        {
            if (string.IsNullOrWhiteSpace(generatorName))
                return null;

            GCNode node = GCDatabase.Instance?.ResolveWithInheritance(generatorName);
            string stackKey = "modifier:" + generatorName;
            if (node == null || !stack.Add(stackKey))
                return null;

            try
            {
                if (!MatchesFilterUnitType(node.GetString("FilterUnitType", ""), unitGcType))
                    return null;

                string directModifier = node.GetString("ItemModifier", "");
                if (!string.IsNullOrWhiteSpace(directModifier))
                    return directModifier;

                string linked = node.GetString("LinkedGenerator", "");
                if (!string.IsNullOrWhiteSpace(linked))
                    return RollItemModifierGenerator(linked, playerLevel, unitGcType, stack, source);

                var chanceGroups = GetAuthoredGeneratorGroups(node);

                foreach (var group in chanceGroups)
                {
                    if (RollLootRandom(group.Key, "item-modifier-generator-chance", $"{source ?? "unknown"}:{generatorName}:chance={group.Key}") != 0)
                        continue;

                    var candidates = group
                        .Where(candidate => playerLevel >= candidate.MinLevel && playerLevel <= candidate.MaxLevel)
                        .Where(candidate => MatchesFilterUnitType(candidate.FilterUnitType, unitGcType))
                        .OrderBy(candidate => candidate.Order)
                        .ToList();
                    if (candidates.Count == 0)
                        continue;

                    GCNode selected = candidates[RollLootRandom(candidates.Count, "item-modifier-generator-select", $"{source ?? "unknown"}:{generatorName}:candidates={candidates.Count}")].Entry;
                    string modifier = selected.GetString("ItemModifier", "");
                    if (!string.IsNullOrWhiteSpace(modifier))
                        return modifier;

                    string selectedLink = selected.GetString("LinkedGenerator", "");
                    if (string.IsNullOrWhiteSpace(selectedLink) && !string.IsNullOrWhiteSpace(selected.Name))
                        selectedLink = $"{generatorName}.{selected.Name}";
                    modifier = RollItemModifierGenerator(selectedLink, playerLevel, unitGcType, stack, source);
                    if (!string.IsNullOrWhiteSpace(modifier))
                        return modifier;
                }

                return null;
            }
            finally
            {
                stack.Remove(stackKey);
            }
        }

        private void RollRequiresMembership(LootDrop item, string owner)
        {
            if (item == null || !item.IsItem)
                return;

            string knob = item.Rarity switch
            {
                ItemRarity.Rare => "ItemChanceRequiresMembershipRare",
                ItemRarity.Unique => "ItemChanceRequiresMembershipUnique",
                ItemRarity.Mythic => "ItemChanceRequiresMembershipMythic",
                _ => null
            };
            int threshold = knob == null
                ? 0
                : Math.Max(0, GCDatabase.Instance.GetRequiredKnobFixed32(knob) >> 8);
            int roll = RollLootRandom(100, "item-roll-requires-membership", owner);
            item.RolledRequiresMembership = roll < threshold;

            GCNode description = GCObject.ResolveItemDescription(item.GCType);
            if (description?.GetBool("ForceRequiresMembership", false) == true)
                item.RequiresMembership = true;
            else if (description?.GetBool("ForceNotRequiresMembership", false) == true)
                item.RequiresMembership = false;
            else
                item.RequiresMembership = item.RolledRequiresMembership;
            item.HasGeneratedItemState = true;
            if (VerboseItemLogging)
            {
                Debug.LogError($"[LOOT-MEMBERSHIP] item={item.GCType} rarity={item.Rarity} roll={roll} threshold={threshold} rolled={item.RolledRequiresMembership} effective={item.RequiresMembership}");
            }
        }

        private static bool IsGeneratorKind(GCNode node, string generatorKind)
        {
            GCNode current = node;
            for (int depth = 0; current != null && depth < 64; depth++)
            {
                if (IdentityMatches(current.Name, generatorKind) || IdentityMatches(current.Extends, generatorKind))
                    return true;
                current = string.IsNullOrWhiteSpace(current.Extends) ? null : GCDatabase.Instance?.Resolve(current.Extends);
            }
            return false;
        }

        private static bool MatchesFilterUnitType(string filterUnitType, string unitGcType)
        {
            if (string.IsNullOrWhiteSpace(filterUnitType))
                return true;
            if (string.IsNullOrWhiteSpace(unitGcType))
                return false;

            GCNode filterNode = GCDatabase.Instance?.Resolve(filterUnitType);
            string currentPath = unitGcType;
            for (int depth = 0; !string.IsNullOrWhiteSpace(currentPath) && depth < 64; depth++)
            {
                if (string.Equals(currentPath, filterUnitType, StringComparison.OrdinalIgnoreCase))
                    return true;
                GCNode current = GCDatabase.Instance?.Resolve(currentPath);
                if (current == null)
                    return false;
                if (filterNode != null && ReferenceEquals(current, filterNode))
                    return true;
                currentPath = current.Extends;
            }
            return false;
        }

        private static bool IdentityMatches(string identity, string expected)
        {
            if (string.IsNullOrWhiteSpace(identity) || string.IsNullOrWhiteSpace(expected))
                return false;
            if (string.Equals(identity, expected, StringComparison.OrdinalIgnoreCase))
                return true;
            int separator = identity.LastIndexOf('.');
            return separator >= 0 && string.Equals(identity.Substring(separator + 1), expected, StringComparison.OrdinalIgnoreCase);
        }

        private LootDrop RollItem(TreasureGenerator gen, int playerLevel, bool isMember = false, string unitGcType = "avatar.base.Avatar", ItemRarity? forcedRarity = null, string source = null)
        {
            if (gen == null || !MatchesFilterUnitType(gen.FilterUnitType, unitGcType))
                return null;

            var chanceGroups = gen.Entries
                .Select((entry, order) => new
                {
                    Entry = entry,
                    Order = order,
                    Chance = entry.Chance
                })
                .Where(x => x.Chance > 0)
                .GroupBy(x => x.Chance)
                .OrderByDescending(g => g.Key);

            foreach (var group in chanceGroups)
            {
                string generatorIdentity = $"{gen.CanonicalPath ?? "unknown"}#{gen.PackageEntryId}";
                int chanceRoll = RollLootRandom(group.Key, "item-generator-chance", $"{source ?? "unknown"}:{generatorIdentity}:chance={group.Key}", out int chanceRawIndex, out uint chanceRaw);
                if (chanceRoll != 0)
                {
                    if (VerboseGeneratorLogging)
                    {
                        Debug.LogError($"[LOOT-GENERATOR] source={source ?? "unknown"} canonical='{gen.CanonicalPath ?? ""}' entry={gen.PackageEntryId} chance={group.Key} chanceRawIndex={chanceRawIndex} chanceRaw=0x{chanceRaw:X8} chanceRoll={chanceRoll} result=skip");
                    }
                    continue;
                }

                var candidates = group
                    .Where(candidate => playerLevel >= candidate.Entry.MinLevel && playerLevel <= candidate.Entry.MaxLevel)
                    .Where(candidate => MatchesFilterUnitType(candidate.Entry.FilterUnitType, unitGcType))
                    .OrderBy(candidate => candidate.Order)
                    .ToList();
                if (candidates.Count == 0)
                {
                    if (VerboseGeneratorLogging)
                    {
                        Debug.LogError($"[LOOT-GENERATOR] source={source ?? "unknown"} canonical='{gen.CanonicalPath ?? ""}' entry={gen.PackageEntryId} chance={group.Key} chanceRawIndex={chanceRawIndex} chanceRaw=0x{chanceRaw:X8} chanceRoll={chanceRoll} candidates=0 result=filtered");
                    }
                    continue;
                }

                int selectedIndex = RollLootRandom(candidates.Count, "item-generator-select", $"{source ?? "unknown"}:{generatorIdentity}:candidates={candidates.Count}", out int selectionRawIndex, out uint selectionRaw);
                TreasureEntry selected = candidates[selectedIndex].Entry;
                if (VerboseGeneratorLogging)
                {
                    string candidateLedger = string.Join(",", candidates.Select(candidate => $"{candidate.Order}:{candidate.Entry.CanonicalPath ?? candidate.Entry.LinkedGenerator}#{candidate.Entry.PackageEntryId}"));
                    Debug.LogError($"[LOOT-GENERATOR] source={source ?? "unknown"} canonical='{gen.CanonicalPath ?? ""}' entry={gen.PackageEntryId} chance={group.Key} chanceRawIndex={chanceRawIndex} chanceRaw=0x{chanceRaw:X8} chanceRoll={chanceRoll} selectionRawIndex={selectionRawIndex} selectionRaw=0x{selectionRaw:X8} selectedIndex={selectedIndex} selected='{selected.CanonicalPath ?? selected.LinkedGenerator}#{selected.PackageEntryId}' linked='{selected.LinkedGenerator ?? ""}' candidates='{candidateLedger}' playerLevel={playerLevel} unit='{unitGcType}'");
                }
                if (!string.IsNullOrWhiteSpace(selected.LinkedGenerator) &&
                    TryRollAuthoredGenerator(selected.LinkedGenerator, playerLevel, isMember, out var linkedDrops, source ?? "item-generator", forcedRarity ?? selected.Rarity, unitGcType))
                {
                    LootDrop linkedItem = linkedDrops.FirstOrDefault(drop => drop != null && drop.IsItem);
                    if (linkedItem != null)
                        return linkedItem;
                    continue;
                }
            }
            return null;
        }

        private int RollCurrency(CurrencyGenerator gen, int playerLevel, bool isMember = false, string unitGcType = "avatar.base.Avatar", string source = null)
        {
            if (gen == null || !MatchesFilterUnitType(gen.FilterUnitType, unitGcType))
                return 0;

            var chanceGroups = gen.Entries
                .Select((entry, order) => new
                {
                    Entry = entry,
                    Order = order,
                    Chance = entry.Chance
                })
                .Where(x => x.Chance > 0)
                .GroupBy(x => x.Chance)
                .OrderByDescending(g => g.Key);

            foreach (var group in chanceGroups)
            {
                string generatorIdentity = $"{gen.CanonicalPath ?? "unknown"}#{gen.PackageEntryId}";
                int chanceRoll = RollLootRandom(group.Key, "gold-generator-chance", $"{source ?? "unknown"}:{generatorIdentity}:chance={group.Key}", out int chanceRawIndex, out uint chanceRaw);
                if (chanceRoll != 0)
                {
                    if (VerboseGeneratorLogging)
                    {
                        Debug.LogError($"[LOOT-GENERATOR] source={source ?? "unknown"} canonical='{gen.CanonicalPath ?? ""}' entry={gen.PackageEntryId} chance={group.Key} chanceRawIndex={chanceRawIndex} chanceRaw=0x{chanceRaw:X8} chanceRoll={chanceRoll} kind=gold result=skip");
                    }
                    continue;
                }

                var candidates = group
                    .Where(candidate => playerLevel >= candidate.Entry.MinLevel && playerLevel <= candidate.Entry.MaxLevel)
                    .Where(candidate => MatchesFilterUnitType(candidate.Entry.FilterUnitType, unitGcType))
                    .OrderBy(candidate => candidate.Order)
                    .ToList();
                if (candidates.Count == 0)
                    continue;

                int selectedIndex = RollLootRandom(candidates.Count, "gold-generator-select", $"{source ?? "unknown"}:{generatorIdentity}:candidates={candidates.Count}", out int selectionRawIndex, out uint selectionRaw);
                CurrencyEntry selected = candidates[selectedIndex].Entry;
                if (VerboseGeneratorLogging)
                {
                    string candidateLedger = string.Join(",", candidates.Select(candidate => $"{candidate.Order}:{candidate.Entry.CanonicalPath ?? "gold"}#{candidate.Entry.PackageEntryId}"));
                    Debug.LogError($"[LOOT-GENERATOR] source={source ?? "unknown"} canonical='{gen.CanonicalPath ?? ""}' entry={gen.PackageEntryId} chance={group.Key} chanceRawIndex={chanceRawIndex} chanceRaw=0x{chanceRaw:X8} chanceRoll={chanceRoll} selectionRawIndex={selectionRawIndex} selectionRaw=0x{selectionRaw:X8} selectedIndex={selectedIndex} selected='{selected.CanonicalPath ?? "gold"}#{selected.PackageEntryId}' candidates='{candidateLedger}' playerLevel={playerLevel} unit='{unitGcType}' kind=gold");
                }
                return RollCurrencyAmount(selected, playerLevel, isMember, source);
            }
            return 0;
        }

        private int RollCurrencyAmount(CurrencyEntry entry, int playerLevel, bool isMember, string source = null)
        {
            int itemGoldPerLevelF32 = GCDatabase.Instance != null
                ? GCDatabase.Instance.GetRequiredKnobFixed32("ItemGoldValuePerLevel")
                : 50 * 0x100;
            int memberGoldF32 = isMember && GCDatabase.Instance != null
                ? GCDatabase.Instance.GetRequiredKnobFixed32("MemberGoldMod")
                : 0x100;
            int levelF32 = Math.Max(1, playerLevel) << 8;
            int goldValueF32 = entry.GoldValueF32;
            int volatilityF32 = entry.VolatilityF32;

            int baseF32 = DamageResolver.FixedMul(
                DamageResolver.FixedMul(
                    DamageResolver.FixedMul(itemGoldPerLevelF32, memberGoldF32),
                    levelF32),
                goldValueF32);
            int spreadF32 = DamageResolver.FixedMul(baseF32, volatilityF32);
            int minF32 = Math.Max(0x100, baseF32 - spreadF32);
            int minGold = Math.Max(1, minF32 >> 8);
            int maxGold = (baseF32 + spreadF32) >> 8;
            if (maxGold <= minGold)
            {
                if (VerboseGeneratorLogging)
                {
                    Debug.LogError($"[LOOT-GENERATOR] source={source ?? "unknown"} selected='{entry.CanonicalPath ?? "gold"}#{entry.PackageEntryId}' phase=gold-amount min={minGold} max={maxGold} result={minGold} draw=none");
                }
                return minGold;
            }

            return minGold + RollLootRandom(maxGold - minGold, "gold-amount", $"{source ?? "unknown"}:{entry.CanonicalPath ?? "gold"}#{entry.PackageEntryId}:min={minGold}:max={maxGold}");
        }


        private static bool IsPollutedNonItemClass(string gcType)
        {
            if (string.IsNullOrEmpty(gcType)) return true;
            if (gcType.IndexOf(".manipulators.", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (gcType.StartsWith("avatar.", StringComparison.OrdinalIgnoreCase))
                return true;
            if (gcType.StartsWith("testpal.", StringComparison.OrdinalIgnoreCase))
                return true;
            if (gcType.EndsWith(".visual", StringComparison.OrdinalIgnoreCase))
                return true;

            if (gcType.IndexOf(".effect.", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (gcType.EndsWith(".effect", StringComparison.OrdinalIgnoreCase))
                return true;
            if (gcType.IndexOf(".modifier.", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (gcType.EndsWith(".modifier", StringComparison.OrdinalIgnoreCase))
                return true;
            int lastDot = gcType.LastIndexOf('.');
            if (lastDot >= 0)
            {
                string suffix = gcType.Substring(lastDot + 1);
                if (suffix.Length >= 4 && suffix.StartsWith("mod", StringComparison.OrdinalIgnoreCase)
                    && char.IsDigit(suffix[3]))
                    return true;
            }
            if (gcType.IndexOf(".description.", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (gcType.EndsWith(".description", StringComparison.OrdinalIgnoreCase))
                return true;
            if (gcType.EndsWith(".spellsoundeffect", StringComparison.OrdinalIgnoreCase))
                return true;
            if (gcType.EndsWith(".sound", StringComparison.OrdinalIgnoreCase))
                return true;

            if (gcType.IndexOf('.') < 0)
                return true;

            if (_clientExcludedItems.Contains(gcType))
                return true;
            return false;
        }

        private string PickRandomItem(ItemRarity rarity, int playerLevel)
        {
            var pool = new List<string>();

            int maxItemLevel = playerLevel + 10;

            if (AuthoredGameplayCatalog.AllWeapons != null)
                foreach (var weaponData in AuthoredGameplayCatalog.AllWeapons)
                {
                    if (IsPollutedNonItemClass(weaponData.gcType)) continue;
                    if (!MatchesRarity(weaponData.gcType, rarity)) continue;
                    int itemLevel = RPGSettings.GetItemLevel(weaponData.gcType);
                    if (itemLevel > maxItemLevel) continue;
                    pool.Add(weaponData.gcType);
                }

            if (AuthoredGameplayCatalog.AllArmor != null)
                foreach (var armorData in AuthoredGameplayCatalog.AllArmor)
                {
                    if (IsPollutedNonItemClass(armorData.gcType)) continue;
                    if (!MatchesRarity(armorData.gcType, rarity)) continue;
                    int itemLevel = RPGSettings.GetItemLevel(armorData.gcType);
                    if (itemLevel > maxItemLevel) continue;
                    pool.Add(armorData.gcType);
                }

            if (rarity >= ItemRarity.Rare)
            {
                AddMatchingItemsLevelFiltered(pool, AuthoredGameplayCatalog.Rings, rarity, maxItemLevel);
                AddMatchingItemsLevelFiltered(pool, AuthoredGameplayCatalog.Amulets, rarity, maxItemLevel);
            }

            if (pool.Count == 0 && AuthoredGameplayCatalog.ItemDatabase != null)
            {
                Debug.LogError($"[AUTHORED-COVERAGE] area=loot reason=empty-client-pool rarity={rarity} playerLevel={playerLevel} maxItemLevel={maxItemLevel} itemDatabaseFallbackSuppressed=True");
            }

            if (pool.Count == 0)
            {
                Debug.LogError($"[AUTHORED-COVERAGE] area=loot reason=empty-client-pool-suppressed rarity={rarity} playerLevel={playerLevel} maxItemLevel={maxItemLevel}");
            }

            return pool.Count > 0 ? pool[RollLootRandom(pool.Count, "item-pool-select", $"rarity={rarity}:level={playerLevel}:count={pool.Count}")] : null;
        }

        private void AddMatchingItemsLevelFiltered(List<string> pool, List<GeneralItemData> items, ItemRarity rarity, int maxItemLevel)
        {
            if (items == null) return;
            foreach (var item in items)
            {
                if (!MatchesRarity(item.gcType, rarity)) continue;
                int itemLv = RPGSettings.GetItemLevel(item.gcType);
                if (itemLv > maxItemLevel) continue;
                pool.Add(item.gcType);
            }
        }

        private bool MatchesRarity(string gcType, ItemRarity target)
        {
            if (string.IsNullOrEmpty(gcType)) return false;
            if (target == ItemRarity.Mythic) return RPGSettings.IsMythicPALItem(gcType);
            if (RPGSettings.IsMythicPALItem(gcType)) return false;
            return RPGSettings.ResolveItemRarity(gcType) == target;
        }

        private string GetItemLabel(string gcType)
        {
            if (AuthoredGameplayCatalog.ItemDatabase?.TryGetValue(gcType, out var item) == true)
                return item.name ?? gcType;
            if (AuthoredGameplayCatalog.GeneralItemDatabase?.TryGetValue(gcType, out var gi) == true)
                return gi.Label ?? gcType;
            return gcType;
        }

        private string GetEffectiveString(GCNode node, string key)
        {
            if (node == null || string.IsNullOrWhiteSpace(key))
                return "";
            GCNode desc = node.GetChild("Description");
            if (desc != null && desc.HasProperty(key))
                return desc.GetString(key, "");
            return node.GetString(key, "");
        }

        private int GetEffectiveInt(GCNode node, string key, int fallback)
        {
            if (node == null || string.IsNullOrWhiteSpace(key))
                return fallback;
            GCNode desc = node.GetChild("Description");
            if (desc != null && desc.HasProperty(key))
                return desc.GetInt(key, fallback);
            return node.GetInt(key, fallback);
        }

        public LootDrop RollKingsCoin(string tier)
        {
            int pct;
            switch ((tier ?? "").ToUpperInvariant())
            {
                case "HERO":
                case "BOSS":
                case "DUNGEON_BOSS":
                    pct = ServerSettings.Get("kingsCoinPctHero", 10);
                    break;
                case "CHAMPION":
                    pct = ServerSettings.Get("kingsCoinPctChampion", 5);
                    break;
                case "VETERAN":
                case "WARMONGER":
                    pct = ServerSettings.Get("kingsCoinPctVeteran", 2);
                    break;
                default:
                    pct = ServerSettings.Get("kingsCoinPctGrunt", 1);
                    break;
            }
            if (pct <= 0) return null;
            if (RollLootRandom(100, "kings-coin-chance", tier) >= pct) return null;

            int count = ServerSettings.Get("kingsCoinDropAmount", 1);
            if (count < 1) count = 1;
            Debug.LogError($"[LOOT-KC] tier={tier} pct={pct} count={count}");
            return LootDrop.KingsCoin(count);
        }
    }


    public class LootDrop
    {
        public bool IsGold;
        public int GoldAmount;
        public bool IsKingsCoin;
        public int KingsCoinCount;
        public uint RecipientCharacterId;
        public int RecipientPlayerLevel = 1;
        public bool IsItem => !IsGold && !IsKingsCoin;
        public string GCType;
        public string Label;
        public ItemRarity Rarity;
        public string ScaleMod;
        public int ItemLevel;
        public bool HasGeneratedItemState;
        public bool RolledRequiresMembership;
        public bool RequiresMembership;
        public List<string> ItemModifiers = new List<string>();

        public static LootDrop Gold(int amount) =>
            new LootDrop { IsGold = true, GoldAmount = amount };

        public static LootDrop Item(string gcType, string label, ItemRarity rarity, string scaleMod, int level) =>
            new LootDrop { GCType = gcType, Label = label, Rarity = rarity, ScaleMod = scaleMod, ItemLevel = level };

        public static LootDrop KingsCoin(int count) =>
            new LootDrop { IsKingsCoin = true, KingsCoinCount = count };
    }

    public sealed class LootGenerationProfile
    {
        public uint CharacterId;
        public int PlayerLevel = 1;
        public bool IsMember;
        public string UnitGcType = "avatar.base.Avatar";
        public string ClassName = string.Empty;
    }

    public class TreasureEntry
    {
        public ItemRarity Rarity;
        public int Chance;
        public int MinLevel;
        public int MaxLevel;
        public string LinkedGenerator;
        public string FilterUnitType;
        public string CanonicalPath;
        public int PackageEntryId;
        public TreasureEntry(ItemRarity r, int c, int min, int max, string linkedGenerator, string filterUnitType, string canonicalPath, int packageEntryId)
        { Rarity = r; Chance = c; MinLevel = min; MaxLevel = max; LinkedGenerator = linkedGenerator; FilterUnitType = filterUnitType; CanonicalPath = canonicalPath; PackageEntryId = packageEntryId; }
    }

    public class TreasureGenerator
    {
        public TreasureEntry[] Entries;
        public string FilterUnitType;
        public string CanonicalPath;
        public int PackageEntryId;
        public TreasureGenerator(TreasureEntry[] entries, string filterUnitType, string canonicalPath, int packageEntryId) { Entries = entries; FilterUnitType = filterUnitType; CanonicalPath = canonicalPath; PackageEntryId = packageEntryId; }
    }

    public class CurrencyEntry
    {
        public int Chance;
        public int GoldValueF32;
        public int VolatilityF32;
        public int MinLevel;
        public int MaxLevel;
        public string FilterUnitType;
        public string CanonicalPath;
        public int PackageEntryId;
        public CurrencyEntry(int c, int gvF32, int vF32, int min, int max, string filterUnitType, string canonicalPath = null, int packageEntryId = 0)
        { Chance = c; GoldValueF32 = gvF32; VolatilityF32 = vF32; MinLevel = min; MaxLevel = max; FilterUnitType = filterUnitType; CanonicalPath = canonicalPath; PackageEntryId = packageEntryId; }
    }

    public class CurrencyGenerator
    {
        public CurrencyEntry[] Entries;
        public string FilterUnitType;
        public string CanonicalPath;
        public int PackageEntryId;
        public CurrencyGenerator(CurrencyEntry[] entries, string filterUnitType, string canonicalPath, int packageEntryId) { Entries = entries; FilterUnitType = filterUnitType; CanonicalPath = canonicalPath; PackageEntryId = packageEntryId; }
    }

    public class CreatureTreasureData
    {
        public List<(string gen, int count)> Generators = new();
    }
}
