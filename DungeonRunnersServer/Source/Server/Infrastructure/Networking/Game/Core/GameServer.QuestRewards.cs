using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using DungeonRunners.Core;
using DungeonRunners.Data;
using DungeonRunners.Database;
using DungeonRunners.Engine;
using DungeonRunners.Gameplay;
using DungeonRunners.Utilities;

namespace DungeonRunners.Networking
{
    public partial class GameServer
    {
        private const string QUEST_XP_BONUS_MODIFIER_GC_TYPE = "quests.base.QuestXPBonus";
        private const uint QUEST_XP_BONUS_MODIFIER_ID = 2;

        private enum QuestRewardPlanEntryKind
        {
            Gold,
            Item,
            ExperienceModifier
        }

        private enum QuestRewardSnapshotCommitResult
        {
            Failed,
            Committed,
            AlreadyCommitted
        }

        private sealed class QuestRewardPlanEntry
        {
            public QuestRewardPlanEntryKind Kind;
            public uint Gold;
            public string GCType;
            public int Count;
            public int MaxStackSize;
            public LootDrop ItemTemplate;
            public bool SoulBound;
            public bool NoSell;
            public bool DropToWorld;
            public bool RequiresMembership;
        }

        private sealed class QuestRewardAddedSlot
        {
            public uint Slot;
            public byte X;
            public byte Y;
            public int Width;
            public int Height;
        }

        private sealed class QuestRewardItemMutation
        {
            public string GCType;
            public int Count;
            public readonly List<(uint Slot, int PreviousCount)> MergedSlots = new List<(uint, int)>();
            public readonly List<QuestRewardAddedSlot> AddedSlots = new List<QuestRewardAddedSlot>();
            public readonly List<byte[]> Packets = new List<byte[]>();
            public QuestProgressMutation QuestMutation;
        }

        private sealed class QuestRewardEmission
        {
            public byte[] Packet;
            public bool Compressed;
            public bool ExperienceModifier;
            public QuestRewardItemMutation ItemMutation;
            public QuestRewardWorldDropMutation WorldDropMutation;
        }

        private sealed class QuestRewardWorldDropMutation
        {
            public ushort EntityId;
            public DroppedItemInfo Info;
        }

        private sealed class QuestRewardMutation
        {
            public PlayerState PlayerState;
            public uint PreviousGold;
            public string InventoryKey;
            public bool HadInventory;
            public Dictionary<uint, (GCObject item, byte x, byte y)> PreviousInventory;
            public bool HadInventoryOrder;
            public List<uint> PreviousInventoryOrder;
            public bool HadStackCounts;
            public Dictionary<uint, int> PreviousStackCounts;
            public bool HadOccupiedSlots;
            public HashSet<int> PreviousOccupiedSlots;
            public bool HadSlotCounter;
            public uint PreviousSlotCounter;
            public string WorldActivationKey;
            public string WorldOutcomeJson;
            public readonly List<QuestRewardItemMutation> ItemMutations = new List<QuestRewardItemMutation>();
            public readonly List<QuestRewardWorldDropMutation> WorldDrops = new List<QuestRewardWorldDropMutation>();
            public readonly List<QuestRewardEmission> Emissions = new List<QuestRewardEmission>();
        }

        private bool TryBuildQuestRewardPlan(RRConnection conn, QuestData questData, int rewardChoiceIndex, out List<QuestRewardPlanEntry> plan)
        {
            plan = new List<QuestRewardPlanEntry>();
            if (conn == null || questData == null)
                return false;

            try
            {
                int questGoldPerLevelF32 = GCDatabase.Instance.GetRequiredKnobFixed32("QuestGoldPerLevel");
                PlayerState player = GetPlayerState(conn.ConnId.ToString());
                if (player == null)
                    return false;
                int questLevel = QuestRules.GetRewardLevel(player.Level, questData.minLevel, questData.maxLevel);
                int gold = QuestRules.GetCashReward(questLevel, questData.minLevel, questData.cashRewardF32, questGoldPerLevelF32);
                if (gold < 0)
                    return false;
                uint goldReward = (uint)gold;
                if (goldReward > 0)
                    plan.Add(new QuestRewardPlanEntry { Kind = QuestRewardPlanEntryKind.Gold, Gold = goldReward });

                if (questData.tokenReward < 0)
                    return false;
                if (questData.tokenReward > 0)
                {
                    plan.Add(new QuestRewardPlanEntry
                    {
                        Kind = QuestRewardPlanEntryKind.Item,
                        GCType = "QuestItemPAL.Token",
                        Count = questData.tokenReward,
                        MaxStackSize = 100
                    });
                }

                if (questData.grantXPBuff)
                    plan.Add(new QuestRewardPlanEntry { Kind = QuestRewardPlanEntryKind.ExperienceModifier });

                List<QuestRewardChoice> choices = questData.rewardChoices ?? new List<QuestRewardChoice>();
                if (questData.numRewardItems <= 0 || choices.Count == 0)
                    return questData.numRewardItems >= 0;
                if (rewardChoiceIndex < 0 || rewardChoiceIndex >= choices.Count)
                    return false;

                string generator = choices[rewardChoiceIndex]?.generator ?? string.Empty;
                if (string.IsNullOrWhiteSpace(generator))
                    return true;

                if (IsDirectAuthoredRewardItem(generator))
                {
                    plan.Add(new QuestRewardPlanEntry
                    {
                        Kind = QuestRewardPlanEntryKind.Item,
                        GCType = generator,
                        Count = questData.numRewardItems,
                        MaxStackSize = ItemStackRules.GetLimit(generator),
                        ItemTemplate = new LootDrop
                        {
                            GCType = generator,
                            ItemLevel = QuestRules.GetRewardItemLevel(questLevel,
                                GCDatabase.Instance.ResolveWithInheritance(generator)?.GetChild("Description")?.GetInt("LevelOverride", 0) ?? 0, false)
                        },
                        SoulBound = questData.rewardItemsSoulBound,
                        NoSell = questData.rewardItemsNoSell,
                        DropToWorld = questData.rewardItemsDropped,
                        RequiresMembership = questData.rewardItemsRequireMembership
                    });
                    return true;
                }

                bool isFree = IsPlayerFree(conn.LoginName);
                List<LootDrop> drops = GCObjectGeneratorTable.Instance.GenerateAuthoredGeneratorLoot(
                    generator,
                    questData.numRewardItems,
                    questLevel,
                    !isFree,
                    "quest-reward");
                if (drops.Count == 0)
                {
                    if (GCObjectGeneratorTable.Instance.CanResolveAuthoredGenerator(generator))
                        return true;
                    RuntimeEvidence.LogFallbackHit(
                        "gc-object-generator-table",
                        "missing-generator",
                        $"source=quest-reward quest={questData.id} generator={generator} count={questData.numRewardItems}",
                        32);
                    return false;
                }

                foreach (LootDrop drop in drops)
                {
                    if (drop == null)
                        continue;
                    if (drop.IsGold)
                    {
                        if (drop.GoldAmount <= 0)
                            continue;
                        plan.Add(new QuestRewardPlanEntry
                        {
                            Kind = QuestRewardPlanEntryKind.Gold,
                            Gold = checked((uint)drop.GoldAmount),
                            ItemTemplate = drop,
                            SoulBound = questData.rewardItemsSoulBound,
                            NoSell = questData.rewardItemsNoSell,
                            DropToWorld = questData.rewardItemsDropped,
                            RequiresMembership = questData.rewardItemsRequireMembership
                        });
                        continue;
                    }
                    if (drop.IsKingsCoin)
                    {
                        if (drop.KingsCoinCount <= 0)
                            continue;
                        plan.Add(new QuestRewardPlanEntry
                        {
                            Kind = QuestRewardPlanEntryKind.Item,
                            GCType = "QuestItemPAL.Token",
                            Count = drop.KingsCoinCount,
                            MaxStackSize = 100,
                            ItemTemplate = drop,
                            SoulBound = questData.rewardItemsSoulBound,
                            NoSell = questData.rewardItemsNoSell,
                            DropToWorld = questData.rewardItemsDropped,
                            RequiresMembership = questData.rewardItemsRequireMembership
                        });
                        continue;
                    }
                    if (!drop.IsItem || string.IsNullOrWhiteSpace(drop.GCType))
                        return false;
                    drop.ItemLevel = QuestRules.GetRewardItemLevel(questLevel, 0, true);
                    plan.Add(new QuestRewardPlanEntry
                    {
                        Kind = QuestRewardPlanEntryKind.Item,
                        GCType = drop.GCType,
                        Count = 1,
                        MaxStackSize = 1,
                        ItemTemplate = drop,
                        SoulBound = questData.rewardItemsSoulBound,
                        NoSell = questData.rewardItemsNoSell,
                        DropToWorld = questData.rewardItemsDropped,
                        RequiresMembership = questData.rewardItemsRequireMembership
                    });
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[QUEST-REWARDS] state=blocked phase=plan quest={questData.id ?? "<null>"} errorType={ex.GetType().Name} message='{ex.Message}'");
                plan.Clear();
                return false;
            }
        }

        private bool TryStageQuestRewardItem(RRConnection conn, QuestRewardPlanEntry entry, out QuestRewardItemMutation mutation)
        {
            mutation = null;
            if (conn == null || entry == null || entry.Kind != QuestRewardPlanEntryKind.Item
                || conn.UnitContainerId == 0 || string.IsNullOrWhiteSpace(entry.GCType)
                || entry.Count <= 0 || entry.MaxStackSize <= 0 || entry.MaxStackSize > byte.MaxValue)
                return false;

            string connId = conn.ConnId.ToString();
            var staged = new QuestRewardItemMutation { GCType = entry.GCType, Count = entry.Count };
            mutation = staged;
            int remaining = entry.Count;
            if (_playerInventoryItems.TryGetValue(connId, out Dictionary<uint, (GCObject item, byte x, byte y)> inventory))
            {
                IEnumerable<uint> orderedSlots = _playerInventoryOrder.TryGetValue(connId, out List<uint> inventoryOrder)
                    ? inventoryOrder.ToArray()
                    : inventory.Keys.OrderBy(slot => slot).ToArray();
                foreach (uint slot in orderedSlots)
                {
                    if (remaining <= 0)
                        break;
                    if (!inventory.TryGetValue(slot, out var inventoryEntry))
                        return false;
                    GCObject item = inventoryEntry.item;
                    if (item == null || !string.Equals(item.GCClass, entry.GCType, StringComparison.OrdinalIgnoreCase)
                        || !CanMergeItemState(item, entry.ItemTemplate, entry.SoulBound, entry.NoSell, entry.RequiresMembership))
                        continue;
                    int currentCount = Math.Max(1, GetStackCount(connId, slot));
                    if (currentCount >= entry.MaxStackSize)
                        continue;
                    int added = Math.Min(entry.MaxStackSize - currentCount, remaining);
                    int newCount = checked(currentCount + added);
                    remaining -= added;
                    staged.MergedSlots.Add((slot, currentCount));
                    SetStackCount(connId, slot, newCount);

                    var writer = new LEWriter();
                    writer.WriteByte(0x07);
                    writer.WriteByte(0x35);
                    writer.WriteUInt16(conn.UnitContainerId);
                    writer.WriteByte(0x22);
                    writer.WriteUInt32(slot);
                    writer.WriteByte(checked((byte)newCount));
                    WritePlayerEntitySynch(conn, writer);
                    writer.WriteByte(0x06);
                    staged.Packets.Add(writer.ToArray());
                }
            }

            GeneralItemData itemData = AuthoredGameplayCatalog.FindGeneralItem(entry.GCType);
            int itemWidth = itemData?.InventoryWidth ?? 1;
            int itemHeight = itemData?.InventoryHeight ?? 1;
            if (itemWidth <= 0 || itemHeight <= 0 || itemWidth > CONTAINER_WIDTH || itemHeight > ContainerHeight(0x0B))
                return false;

            while (remaining > 0)
            {
                var (freeX, freeY) = FindNextFreeInventorySlot(connId, itemWidth, itemHeight);
                if (freeX < 0 || freeY < 0)
                    return false;
                byte slotX = checked((byte)freeX);
                byte slotY = checked((byte)freeY);
                int stackSize = Math.Min(remaining, entry.MaxStackSize);
                remaining -= stackSize;
                uint slot = GetNextInventorySlot(connId);
                var item = new GCObject
                {
                    GCClass = entry.GCType,
                    DFCClass = ResolveAuthoredItemClass(entry.GCType),
                    StoredLevel = entry.ItemTemplate?.ItemLevel > 0
                        ? entry.ItemTemplate.ItemLevel
                        : Math.Max(1, ItemData.GetRequiredLevelFromGCClass(entry.GCType)),
                    PresetScaleMod = entry.ItemTemplate?.ScaleMod,
                    StoredRarity = entry.ItemTemplate == null ? -1 : (int)entry.ItemTemplate.Rarity,
                    HasGeneratedItemState = entry.RequiresMembership || entry.ItemTemplate?.HasGeneratedItemState == true,
                    RolledRequiresMembership = entry.ItemTemplate?.RolledRequiresMembership == true,
                    GeneratedRequiresMembership = entry.RequiresMembership || entry.ItemTemplate?.RequiresMembership == true,
                    GeneratedItemModifiers = entry.ItemTemplate?.ItemModifiers == null
                        ? new List<string>()
                        : new List<string>(entry.ItemTemplate.ItemModifiers),
                    SoulBound = entry.SoulBound,
                    NoSell = entry.NoSell,
                    SoulBoundCountdown = entry.SoulBound ? (ushort)0 : ushort.MaxValue
                };

                var writer = new LEWriter();
                writer.WriteByte(0x07);
                writer.WriteByte(0x35);
                writer.WriteUInt16(conn.UnitContainerId);
                writer.WriteByte(0x1E);
                writer.WriteByte(0x0B);
                item.WriteInitForInventory(writer, slotX, slotY, slot, item.StoredLevel, checked((byte)stackSize));
                WritePlayerEntitySynch(conn, writer);
                writer.WriteByte(0x06);
                staged.Packets.Add(writer.ToArray());

                TrackInventoryItem(connId, slot, item, slotX, slotY);
                OccupyInventorySlots(connId, slotX, slotY, itemWidth, itemHeight);
                SetStackCount(connId, slot, stackSize);
                staged.AddedSlots.Add(new QuestRewardAddedSlot
                {
                    Slot = slot,
                    X = slotX,
                    Y = slotY,
                    Width = itemWidth,
                    Height = itemHeight
                });
            }

            staged.QuestMutation = StageQuestItemAcquisition(conn, entry.GCType, entry.Count);
            return true;
        }

        private static byte[] BuildQuestRewardGoldPacket(GameServer server, RRConnection conn, uint amount)
        {
            if (server == null || conn == null || conn.UnitContainerId == 0 || amount == 0)
                return null;
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x35);
            writer.WriteUInt16(conn.UnitContainerId);
            writer.WriteByte(0x20);
            writer.WriteUInt32(amount);
            writer.WriteByte(0x00);
            writer.WriteUInt32(0x00000000);
            writer.WriteByte(0x01);
            server.WritePlayerEntitySynch(conn, writer);
            writer.WriteByte(0x06);
            return writer.ToArray();
        }

        private bool TryBuildQuestRewardWorldActivationKey(
            RRConnection conn,
            ActiveQuest completingQuest,
            QuestData questData,
            out string activationKey)
        {
            activationKey = "";
            if (conn == null || completingQuest == null || questData == null
                || !questData.rewardItemsDropped || !IsPublicZone(conn.CurrentZoneName))
                return false;
            uint characterId = GetCharSqlId(conn);
            if (characterId == 0 || completingQuest.AcceptedAt == default)
                return false;
            string questId = !string.IsNullOrWhiteSpace(completingQuest.QuestId)
                ? completingQuest.QuestId
                : questData.id;
            if (string.IsNullOrWhiteSpace(questId))
                return false;
            long acceptedTicks = completingQuest.AcceptedAt.ToUniversalTime().Ticks;
            activationKey = $"quest-reward:{characterId.ToString(CultureInfo.InvariantCulture)}:{questId.Trim().ToLowerInvariant()}:{acceptedTicks.ToString(CultureInfo.InvariantCulture)}";
            if (activationKey.Length > 2048)
            {
                activationKey = "";
                return false;
            }
            if (!CharacterRepository.TryIsWorldEntityActivationCommitted(activationKey, out bool alreadyCommitted)
                || alreadyCommitted)
            {
                activationKey = "";
                return false;
            }
            return true;
        }

        private bool TryStageQuestRewardWorldDrop(
            RRConnection conn,
            QuestRewardPlanEntry entry,
            QuestRewardMutation mutation)
        {
            if (conn == null || entry == null || mutation == null || string.IsNullOrEmpty(mutation.WorldActivationKey)
                || !entry.DropToWorld || (entry.Kind != QuestRewardPlanEntryKind.Item && entry.Kind != QuestRewardPlanEntryKind.Gold))
                return false;
            int dropCount = entry.Kind == QuestRewardPlanEntryKind.Gold ? 1 : entry.Count;
            if (dropCount <= 0 || mutation.WorldDrops.Count > byte.MaxValue - dropCount)
                return false;
            PlayerState playerState = GetPlayerState(conn.ConnId.ToString());
            if (playerState == null)
                return false;
            ResolveSpellActorPoint(conn, out int sourceFixedX, out int sourceFixedY, out int sourceFixedZ);
            int sourceHeadingFixed = ResolveSpellSourceHeadingFixed(conn);

            for (int dropIndex = 0; dropIndex < dropCount; dropIndex++)
            {
                string source = $"quest-reward:{mutation.WorldActivationKey}:{mutation.WorldDrops.Count}";
                ItemDropPlacement placement = ResolveItemDropPlacement(
                    conn,
                    conn.CurrentZoneName,
                    conn.InstanceId,
                    sourceFixedX,
                    sourceFixedY,
                    sourceFixedZ,
                    sourceHeadingFixed,
                    source);
                if (!placement.Success)
                    return false;
                int headingFixed = ConsumeItemAddToWorldHeading(source);

                GCObject item;
                uint goldAmount = 0;
                if (entry.Kind == QuestRewardPlanEntryKind.Gold)
                {
                    goldAmount = entry.Gold;
                    item = new GCObject
                    {
                        GCClass = "Currency",
                        DFCClass = "Currency",
                        StoredLevel = 1,
                        StoredRarity = -1,
                        HasGeneratedItemState = entry.RequiresMembership || entry.ItemTemplate?.HasGeneratedItemState == true,
                        RolledRequiresMembership = entry.ItemTemplate?.RolledRequiresMembership == true,
                        GeneratedRequiresMembership = entry.RequiresMembership || entry.ItemTemplate?.RequiresMembership == true,
                        GeneratedItemModifiers = new List<string>(),
                        SoulBound = entry.SoulBound,
                        NoSell = entry.NoSell,
                        SoulBoundCountdown = entry.SoulBound ? (ushort)0 : ushort.MaxValue
                    };
                }
                else
                {
                    bool kingsCoin = string.Equals(entry.GCType, "QuestItemPAL.Token", StringComparison.OrdinalIgnoreCase);
                    item = new GCObject
                    {
                        GCClass = entry.GCType,
                        DFCClass = ResolveAuthoredItemClass(entry.GCType),
                        StoredLevel = entry.ItemTemplate?.ItemLevel > 0
                            ? entry.ItemTemplate.ItemLevel
                            : kingsCoin
                                ? Math.Max(1, playerState.Level)
                                : Math.Max(1, ItemData.GetRequiredLevelFromGCClass(entry.GCType)),
                        PresetScaleMod = entry.ItemTemplate?.ScaleMod ?? (kingsCoin ? "ScaleModPAL.Binder.Mod1" : null),
                        StoredRarity = entry.ItemTemplate == null
                            ? kingsCoin ? (int)ItemRarity.Normal : -1
                            : (int)entry.ItemTemplate.Rarity,
                        HasGeneratedItemState = entry.RequiresMembership || entry.ItemTemplate?.HasGeneratedItemState == true,
                        RolledRequiresMembership = entry.ItemTemplate?.RolledRequiresMembership == true,
                        GeneratedRequiresMembership = entry.RequiresMembership || entry.ItemTemplate?.RequiresMembership == true,
                        GeneratedItemModifiers = entry.ItemTemplate?.ItemModifiers == null
                            ? new List<string>()
                            : new List<string>(entry.ItemTemplate.ItemModifiers),
                        SoulBound = entry.SoulBound,
                        NoSell = entry.NoSell,
                        SoulBoundCountdown = entry.SoulBound ? (ushort)0 : ushort.MaxValue
                    };
                }

                var info = new DroppedItemInfo
                {
                    Item = item,
                    DbId = 0,
                    Zone = conn.CurrentZoneName ?? "",
                    ZoneId = conn.CurrentZoneId,
                    InstanceId = conn.InstanceId,
                    RuntimeInstanceKey = conn.RuntimeInstanceKey ?? "",
                    PosFixedX = placement.FixedX,
                    PosFixedY = placement.FixedY,
                    PosFixedZ = placement.FixedZ,
                    HeadingFixed = headingFixed,
                    PlayerLevel = Math.Max(1, playerState.Level),
                    DroppedBy = conn.LoginName ?? "",
                    OwnerCharacterId = GetCharSqlId(conn),
                    OwnerName = ResolveCharacterName(conn),
                    Quantity = 1,
                    IsGoldDrop = goldAmount > 0,
                    GoldAmount = goldAmount
                };
                var worldDrop = new QuestRewardWorldDropMutation
                {
                    EntityId = 0,
                    Info = info
                };
                mutation.WorldDrops.Add(worldDrop);
                mutation.Emissions.Add(new QuestRewardEmission { WorldDropMutation = worldDrop });
            }
            return true;
        }

        private static string SerializeQuestRewardWorldOutcome(QuestRewardMutation mutation)
        {
            return JsonSerializer.Serialize(new
            {
                version = 1,
                type = "quest-reward",
                goldDelta = mutation.PlayerState == null ? 0u : mutation.PlayerState.Gold - mutation.PreviousGold,
                drops = mutation.WorldDrops.Select(drop => new
                {
                    gcClass = drop.Info.Item?.GCClass ?? "Currency",
                    goldAmount = drop.Info.GoldAmount,
                    quantity = drop.Info.Quantity,
                    posFixedX = drop.Info.PosFixedX,
                    posFixedY = drop.Info.PosFixedY,
                    posFixedZ = drop.Info.PosFixedZ,
                    headingFixed = drop.Info.HeadingFixed,
                    playerLevel = drop.Info.PlayerLevel
                }).ToArray()
            });
        }

        private bool TryStageQuestRewards(RRConnection conn, ActiveQuest completingQuest, QuestData questData, int rewardChoiceIndex, out QuestRewardMutation mutation)
        {
            mutation = null;
            string worldActivationKey = "";
            if (questData?.rewardItemsDropped == true
                && !TryBuildQuestRewardWorldActivationKey(conn, completingQuest, questData, out worldActivationKey))
                return false;
            if (!TryBuildQuestRewardPlan(conn, questData, rewardChoiceIndex, out List<QuestRewardPlanEntry> plan))
                return false;
            if (plan.Any(entry => entry.Kind == QuestRewardPlanEntryKind.ExperienceModifier) && conn.ModifiersId == 0)
                return false;
            PlayerState playerState = GetPlayerState(conn.ConnId.ToString());
            if (playerState == null)
                return false;
            string connId = conn.ConnId.ToString();
            string inventoryKey = InvKey(connId, 0x0B);
            var staged = new QuestRewardMutation
            {
                PlayerState = playerState,
                PreviousGold = playerState.Gold,
                InventoryKey = inventoryKey,
                WorldActivationKey = worldActivationKey,
                HadInventory = _playerInventoryItems.TryGetValue(inventoryKey, out Dictionary<uint, (GCObject item, byte x, byte y)> previousInventory),
                HadInventoryOrder = _playerInventoryOrder.TryGetValue(inventoryKey, out List<uint> previousInventoryOrder),
                HadStackCounts = _inventoryStackCounts.TryGetValue(inventoryKey, out Dictionary<uint, int> previousStackCounts),
                HadOccupiedSlots = _occupiedInventorySlots.TryGetValue(inventoryKey, out HashSet<int> previousOccupiedSlots),
                HadSlotCounter = _inventorySlotCounters.TryGetValue(connId, out uint previousSlotCounter)
            };
            staged.PreviousInventory = previousInventory == null
                ? null
                : new Dictionary<uint, (GCObject item, byte x, byte y)>(previousInventory);
            staged.PreviousInventoryOrder = previousInventoryOrder == null ? null : new List<uint>(previousInventoryOrder);
            staged.PreviousStackCounts = previousStackCounts == null ? null : new Dictionary<uint, int>(previousStackCounts);
            staged.PreviousOccupiedSlots = previousOccupiedSlots == null ? null : new HashSet<int>(previousOccupiedSlots);
            staged.PreviousSlotCounter = previousSlotCounter;

            try
            {
                foreach (QuestRewardPlanEntry entry in plan)
                {
                    if (entry.DropToWorld)
                    {
                        if (!TryStageQuestRewardWorldDrop(conn, entry, staged))
                        {
                            RollbackQuestRewards(conn, staged);
                            return false;
                        }
                        continue;
                    }
                    if (entry.Kind == QuestRewardPlanEntryKind.Gold)
                    {
                        if (entry.Gold > int.MaxValue || playerState.Gold > (uint)int.MaxValue - entry.Gold)
                        {
                            RollbackQuestRewards(conn, staged);
                            return false;
                        }
                        playerState.Gold += entry.Gold;
                        byte[] packet = BuildQuestRewardGoldPacket(this, conn, entry.Gold);
                        if (packet != null)
                            staged.Emissions.Add(new QuestRewardEmission { Packet = packet });
                        continue;
                    }
                    if (entry.Kind == QuestRewardPlanEntryKind.ExperienceModifier)
                    {
                        staged.Emissions.Add(new QuestRewardEmission { ExperienceModifier = true });
                        continue;
                    }
                    if (!TryStageQuestRewardItem(conn, entry, out QuestRewardItemMutation itemMutation))
                    {
                        if (itemMutation != null)
                            staged.ItemMutations.Add(itemMutation);
                        RollbackQuestRewards(conn, staged);
                        return false;
                    }
                    staged.ItemMutations.Add(itemMutation);
                    foreach (byte[] packet in itemMutation.Packets)
                        staged.Emissions.Add(new QuestRewardEmission { Packet = packet, Compressed = true });
                    staged.Emissions.Add(new QuestRewardEmission { ItemMutation = itemMutation });
                }
            }
            catch (Exception ex)
            {
                RollbackQuestRewards(conn, staged);
                Debug.LogError($"[QUEST-REWARDS] state=blocked phase=stage quest={questData.id ?? "<null>"} errorType={ex.GetType().Name} message='{ex.Message}'");
                return false;
            }

            if (staged.WorldDrops.Count > 0)
                staged.WorldOutcomeJson = SerializeQuestRewardWorldOutcome(staged);
            mutation = staged;
            return true;
        }

        private void RollbackQuestRewards(RRConnection conn, QuestRewardMutation mutation)
        {
            if (conn == null || mutation == null)
                return;
            string connId = conn.ConnId.ToString();
            for (int itemIndex = mutation.ItemMutations.Count - 1; itemIndex >= 0; itemIndex--)
            {
                QuestRewardItemMutation itemMutation = mutation.ItemMutations[itemIndex];
                RollbackQuestItemAcquisition(itemMutation.QuestMutation);
            }
            if (mutation.HadInventory)
                _playerInventoryItems[mutation.InventoryKey] = new Dictionary<uint, (GCObject item, byte x, byte y)>(mutation.PreviousInventory);
            else
                _playerInventoryItems.Remove(mutation.InventoryKey);
            if (mutation.HadInventoryOrder)
                _playerInventoryOrder[mutation.InventoryKey] = new List<uint>(mutation.PreviousInventoryOrder);
            else
                _playerInventoryOrder.Remove(mutation.InventoryKey);
            if (mutation.HadStackCounts)
                _inventoryStackCounts[mutation.InventoryKey] = new Dictionary<uint, int>(mutation.PreviousStackCounts);
            else
                _inventoryStackCounts.Remove(mutation.InventoryKey);
            if (mutation.HadOccupiedSlots)
                _occupiedInventorySlots[mutation.InventoryKey] = new HashSet<int>(mutation.PreviousOccupiedSlots);
            else
                _occupiedInventorySlots.Remove(mutation.InventoryKey);
            if (mutation.HadSlotCounter)
                _inventorySlotCounters[connId] = mutation.PreviousSlotCounter;
            else
                _inventorySlotCounters.Remove(connId);
            if (mutation.PlayerState != null)
                mutation.PlayerState.Gold = mutation.PreviousGold;
        }

        private QuestRewardSnapshotCommitResult TryCommitQuestRewardSnapshot(RRConnection conn, QuestRewardMutation mutation)
        {
            if (conn == null || mutation == null)
                return QuestRewardSnapshotCommitResult.Failed;
            bool persistExperienceModifier = mutation.Emissions.Any(emission => emission.ExperienceModifier);
            if (mutation.WorldDrops.Count == 0)
            {
                if (!persistExperienceModifier)
                    return SavePlayerQuests(conn)
                        ? QuestRewardSnapshotCommitResult.Committed
                        : QuestRewardSnapshotCommitResult.Failed;
                if (!TryCaptureFullCharacterSnapshot(conn, "quest-reward-modifier", out SavedCharacter modifierCharacter)
                    || !_selectedCharacter.TryGetValue(conn.LoginName, out GCObject modifierSelected)
                    || modifierSelected == null || modifierSelected.Id != modifierCharacter.id
                    || !CharacterRepository.TrySaveCharacterWithModifier(
                        modifierCharacter,
                        QUEST_XP_BONUS_MODIFIER_GC_TYPE,
                        QUEST_XP_BONUS_MODIFIER_ID,
                        0,
                        0,
                        0,
                        1,
                        "quest-reward-modifier"))
                    return QuestRewardSnapshotCommitResult.Failed;
                _activeCharacter[conn.LoginName] = modifierCharacter;
                return QuestRewardSnapshotCommitResult.Committed;
            }
            if (string.IsNullOrWhiteSpace(mutation.WorldActivationKey)
                || string.IsNullOrWhiteSpace(mutation.WorldOutcomeJson)
                || !TryCaptureFullCharacterSnapshot(conn, "quest-reward-world-drop", out SavedCharacter savedCharacter)
                || !_selectedCharacter.TryGetValue(conn.LoginName, out GCObject selected)
                || selected == null || selected.Id != savedCharacter.id)
                return QuestRewardSnapshotCommitResult.Failed;

            var writes = new List<CharacterRepository.WorldActivationDropWrite>(mutation.WorldDrops.Count);
            foreach (QuestRewardWorldDropMutation worldDrop in mutation.WorldDrops)
            {
                DroppedItemInfo info = worldDrop?.Info;
                if (info == null)
                    return QuestRewardSnapshotCommitResult.Failed;
                GCObject persistedItem = info.Item;
                if (persistedItem == null)
                    return QuestRewardSnapshotCommitResult.Failed;
                writes.Add(new CharacterRepository.WorldActivationDropWrite
                {
                    Zone = info.Zone,
                    ZoneId = info.ZoneId,
                    InstanceId = info.InstanceId,
                    Item = persistedItem,
                    PosFixedX = info.PosFixedX,
                    PosFixedY = info.PosFixedY,
                    PosFixedZ = info.PosFixedZ,
                    HeadingFixed = info.HeadingFixed,
                    PlayerLevel = info.PlayerLevel,
                    Quantity = info.Quantity,
                    GoldAmount = info.GoldAmount,
                    DroppedBy = info.DroppedBy,
                    OwnerCharacterId = info.OwnerCharacterId,
                    OwnerName = info.OwnerName,
                    QuestBindingKey = ""
                });
            }

            bool durableCommitted = false;
            try
            {
                if (!CharacterRepository.TrySaveCharacterCreatingWorldActivation(
                        savedCharacter,
                        mutation.WorldActivationKey,
                        conn.CurrentZoneName,
                        conn.CurrentZoneId,
                        conn.InstanceId,
                        0,
                        mutation.WorldOutcomeJson,
                        writes,
                        out List<long> droppedItemIds,
                        out bool alreadyCommitted,
                        "quest-reward-world-drop",
                        modifierGcType: persistExperienceModifier ? QUEST_XP_BONUS_MODIFIER_GC_TYPE : null,
                        modifierId: persistExperienceModifier ? QUEST_XP_BONUS_MODIFIER_ID : 0,
                        modifierSourceIsSelf: 1))
                    return QuestRewardSnapshotCommitResult.Failed;
                if (alreadyCommitted)
                    return QuestRewardSnapshotCommitResult.AlreadyCommitted;
                durableCommitted = true;
                if (droppedItemIds == null || droppedItemIds.Count != mutation.WorldDrops.Count)
                    throw new InvalidOperationException("quest reward drop identity count mismatch");
                for (int dropIndex = 0; dropIndex < droppedItemIds.Count; dropIndex++)
                {
                    if (droppedItemIds[dropIndex] <= 0)
                        throw new InvalidOperationException("quest reward drop identity is invalid");
                    mutation.WorldDrops[dropIndex].Info.DbId = droppedItemIds[dropIndex];
                    mutation.WorldDrops[dropIndex].EntityId = GetNextLootEntityId();
                }
                _activeCharacter[conn.LoginName] = savedCharacter;
                if (mutation.PlayerState != null)
                    mutation.PlayerState.Gold = savedCharacter.gold;
                return QuestRewardSnapshotCommitResult.Committed;
            }
            catch (Exception ex)
            {
                if (durableCommitted)
                {
                    QuarantineCommittedPersistenceSyncFailure(conn, "quest-reward-world-drop", ex);
                    return QuestRewardSnapshotCommitResult.Committed;
                }
                Debug.LogError($"[QUEST-REWARDS] state=blocked phase=world-drop-commit errorType={ex.GetType().Name} message='{ex.Message}'");
                return QuestRewardSnapshotCommitResult.Failed;
            }
        }

        private void CommitQuestRewardWorldDrop(RRConnection conn, QuestRewardWorldDropMutation mutation)
        {
            if (conn == null || mutation?.Info == null || mutation.Info.DbId <= 0 || mutation.EntityId == 0)
                throw new InvalidOperationException("quest reward world drop is not durably committed");
            StoreDroppedItemWithLifetime(mutation.EntityId, mutation.Info);
            _dbIdToEntityId[mutation.Info.DbId] = mutation.EntityId;
            if (mutation.Info.GoldAmount > 0)
                BroadcastGoldPileSpawnPacket(conn, mutation.EntityId, mutation.Info);
            else
                BroadcastDroppedItemSpawnPacket(conn, mutation.EntityId, mutation.Info);
        }

        private void CommitQuestRewards(RRConnection conn, QuestRewardMutation mutation)
        {
            if (conn == null || mutation == null)
                return;
            foreach (QuestRewardEmission emission in mutation.Emissions)
            {
                if (emission.WorldDropMutation != null)
                {
                    try
                    {
                        CommitQuestRewardWorldDrop(conn, emission.WorldDropMutation);
                    }
                    catch (Exception ex)
                    {
                        QuarantineCommittedPersistenceSyncFailure(conn, "quest-reward-world-drop-runtime", ex);
                        return;
                    }
                    continue;
                }
                if (emission.ExperienceModifier)
                {
                    if (!SendQuestXPBonusModifier(conn))
                        QuarantineCommittedPersistenceSyncFailure(conn, "quest-reward-modifier", new InvalidOperationException("quest reward modifier runtime sync failed"));
                    continue;
                }
                if (emission.ItemMutation != null)
                {
                    CommitQuestItemAcquisition(
                        conn,
                        emission.ItemMutation.QuestMutation,
                        emission.ItemMutation.GCType,
                        emission.ItemMutation.Count,
                        "quest-reward");
                    continue;
                }
                if (emission.Packet == null)
                    continue;
                if (emission.Compressed)
                    SendCompressedA(conn, 0x01, 0x0F, emission.Packet);
                else
                    SendToClient(conn, emission.Packet);
            }
        }
    }
}
