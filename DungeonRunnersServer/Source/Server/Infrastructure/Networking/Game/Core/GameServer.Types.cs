using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using DungeonRunners.Engine;
using DungeonRunners.Utilities;
using DungeonRunners.Data;
using DungeonRunners.Core;
using System.Text;
using System.Reflection;
using System.Runtime.CompilerServices;
using Org.BouncyCastle.Utilities;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;
using DungeonRunners.Gameplay;
using DungeonRunners.Database;
using DungeonRunners.Engine.Playables;
using System.Security.Cryptography;
using DungeonRunners.Combat;
using DungeonRunners.Networking.EntitySynchInfo;
using DungeonRunners.Infrastructure;

namespace DungeonRunners.Networking
{
    public partial class GameServer
    {

        private enum EntitySynchInfoOwner
        {
            Unknown,
            NonUnit,
            Avatar,
            Monster
        }

        private struct EntitySynchInfoDecision
        {
            public bool Allow;
            public byte Flags;
            public uint HPWire;
            public EntitySynchInfoOwner Owner;
            public string Reason;
            public uint OwnerEntityId;
            public uint ComponentId;
            public byte Subtype;
            public uint ValidationCutoffTick;
            public string Provenance;
            public string RuntimeInstanceKey;
            public uint SchedulerTick;
            public bool SubEntityPhase;
            public int RngPos;
            public string HpMutationSource;

            public static EntitySynchInfoDecision Empty(EntitySynchInfoOwner owner, string reason)
            {
                return new EntitySynchInfoDecision { Allow = true, Flags = 0x00, HPWire = 0, Owner = owner, Reason = reason };
            }

            public static EntitySynchInfoDecision HP(EntitySynchInfoOwner owner, uint hpWire, string reason, uint ownerEntityId = 0, uint componentId = 0, byte subtype = 0, string provenance = null, uint validationCutoffTick = 0, string runtimeInstanceKey = null, uint schedulerTick = 0, bool subEntityPhase = false, int rngPos = -1, string hpMutationSource = null)
            {
                return new EntitySynchInfoDecision { Allow = true, Flags = 0x02, HPWire = hpWire, Owner = owner, Reason = reason, OwnerEntityId = ownerEntityId, ComponentId = componentId, Subtype = subtype, Provenance = provenance, ValidationCutoffTick = validationCutoffTick, RuntimeInstanceKey = runtimeInstanceKey, SchedulerTick = schedulerTick, SubEntityPhase = subEntityPhase, RngPos = rngPos, HpMutationSource = hpMutationSource };
            }

            public static EntitySynchInfoDecision Block(EntitySynchInfoOwner owner, string reason)
            {
                return new EntitySynchInfoDecision { Allow = false, Flags = 0x00, HPWire = 0, Owner = owner, Reason = reason };
            }

            public ResolvedEntitySynchInfo ToResolved(uint fallbackOwnerEntityId, uint fallbackComponentId, byte fallbackSubtype, string fallbackProvenance)
            {
                uint ownerEntityId = OwnerEntityId != 0 ? OwnerEntityId : fallbackOwnerEntityId;
                uint componentId = ComponentId != 0 ? ComponentId : fallbackComponentId;
                byte resolvedSubtype = Subtype != 0 ? Subtype : fallbackSubtype;
                string provenance = !string.IsNullOrWhiteSpace(Provenance) ? Provenance : fallbackProvenance;
                return new ResolvedEntitySynchInfo(new EntitySynchInfoPayload(Flags, HPWire), ownerEntityId, componentId, resolvedSubtype, Reason, provenance, ValidationCutoffTick, RuntimeInstanceKey, SchedulerTick, SubEntityPhase, RngPos, HpMutationSource);
            }
        }

        private enum PendingMonsterBehaviorKind
        {
            AttackStop,
            Attack,
            Follow
        }

        private struct PendingMonsterBehaviorUpdate
        {
            public PendingMonsterBehaviorKind Kind;
            public uint EntityId;
            public uint BehaviorId;
            public string InstanceKey;
            public int ConnId;
            public uint TargetEntityId;
            public byte UseFlags;
            public byte SessionId;
            public bool UseTargetAction;
            public uint AdmissionTick;
            public bool PacketSent;
            public bool SimulationApplied;
            public uint SimulationApplyTick;
            public uint WireSimulationApplyTick;
            public uint WireOrder;
            public int WireConnId;
            public uint WireMessageOrder;
            public byte BehaviorGenerationAtWire;
            public DungeonRunners.Combat.Behavior.MonsterBehavior2.ActionSlot BehaviorCurrentAtWire;
            public DungeonRunners.Combat.Behavior.MonsterBehavior2.ActionSlot BehaviorAlternateAtWire;
            public DungeonRunners.Combat.Behavior.MonsterBehavior2.ActionSlot BehaviorPendingAtWire;
            public bool BehaviorInterruptLockedAtWire;
            public byte BehaviorGenerationAfterApply;
            public DungeonRunners.Combat.Behavior.MonsterBehavior2.ActionSlot BehaviorCurrentAfterApply;
            public DungeonRunners.Combat.Behavior.MonsterBehavior2.ActionSlot BehaviorAlternateAfterApply;
            public DungeonRunners.Combat.Behavior.MonsterBehavior2.ActionSlot BehaviorPendingAfterApply;
            public bool BehaviorInterruptLockedAfterApply;
            public uint DirectPlayerWeaponHitTick;
            public uint DirectPlayerWeaponHitPreviousHPWire;
            public uint DirectPlayerWeaponHitNewHPWire;
            public uint PlayerDamageTick;
            public bool ProximityAttackInput;
            public uint QueuedTick;
        }

        private struct PendingPlayerUseTargetActionInput
        {
            public int ConnId;
            public string InstanceKey;
            public ushort ComponentId;
            public byte ResponseId;
            public byte ManipulatorId;
            public byte UseFlags;
            public ushort TargetId;
            public uint MonsterEntityId;
            public ushort GnomeEntityId;
            public int TargetPlayerConnId;
            public uint ReceivedTick;
            public long AdmissionSequence;
            public bool ResponseQueued;
            public bool PacketSent;
            public uint PacketWriterTick;
            public uint SimulationApplyTick;
            public int WireMessageIndex;
            public ulong UnitFollowClientWaitThroughOrdinal;
        }

        private struct PendingPlayerCancelActionInput
        {
            public int ConnId;
            public string InstanceKey;
            public ushort ComponentId;
            public byte SessionId;
            public uint ReceivedTick;
            public bool PacketSent;
            public uint PacketWriterTick;
            public uint SimulationApplyTick;
            public int MessageIndex;
        }

        private struct PendingPlayerUsePositionWeaponActionInput
        {
            public int ConnId;
            public string InstanceKey;
            public ushort ComponentId;
            public byte ResponseId;
            public byte SessionId;
            public byte ManipulatorId;
            public int TargetFixedX;
            public int TargetFixedY;
            public int TargetFixedZ;
            public uint ReceivedTick;
            public bool PacketSent;
            public uint PacketWriterTick;
            public uint SimulationApplyTick;
            public int WireMessageIndex;
            public long AdmissionSequence;
            public int FollowClientRecordsAtAdmission;
        }

        private struct PendingPlayerUsePositionBehaviorAction
        {
            public int ConnId;
            public string InstanceKey;
            public ushort ComponentId;
            public byte SessionId;
            public byte ManipulatorId;
            public int TargetFixedX;
            public int TargetFixedY;
            public int TargetFixedZ;
            public uint ReceivedTick;
            public uint PacketWriterTick;
            public int WireMessageIndex;
            public long SourceSequence;
            public int FollowClientRecordsAtAdmission;
            public bool WeaponAction;
        }

        private struct PendingPlayerUsePositionAdmission
        {
            public long Sequence;
            public bool WeaponAction;
            public PendingPlayerUsePositionWeaponActionInput WeaponInput;
            public PendingSpell SpellInput;
        }

        private struct PendingOwnerClientControlInput
        {
            public int ConnId;
            public ushort ComponentId;
            public uint PacketWriterTick;
            public uint SimulationApplyTick;
            public int WireMessageIndex;
        }

        private struct PendingHotbarPassiveAdmission
        {
            public long Sequence;
            public int ConnId;
            public string InstanceKey;
            public uint AvatarEntityId;
            public byte[] ResponseMessage;
            public List<PassiveManipulator> Passives;
            public uint ReceivedTick;
        }

        private TcpListener _listener;

        private static readonly Dictionary<uint, string> _skillHashToGcClass = new Dictionary<uint, string>
        {
            { 0xA6CCC405u, "skills.generic.1HMeleeSpeedBuff" },
            { 0xB301E9E6u, "skills.generic.2HMeleeSpeedBuff" },
            { 0xE70E75EDu, "skills.generic.AggroIncreaseModBuff" },
            { 0x5E5B060Au, "skills.generic.Blight" },
            { 0xCCA86938u, "skills.generic.BlockKnockdownProcPassive" },
            { 0x3F7F0F7Du, "skills.generic.Butcher" },
            { 0x6063983Au, "skills.generic.Charge" },
            { 0x60ADE560u, "skills.generic.Cleave" },
            { 0x393590D7u, "skills.generic.CleaveUpgradeProcPassive" },
            { 0x449319F1u, "skills.generic.DivineDamageBuff" },
            { 0xA86DE2F4u, "skills.generic.DivineIntervention" },
            { 0x31E9D9CFu, "skills.generic.DivineMeleeAttack" },
            { 0x99C3D77Bu, "skills.generic.DivineRay" },
            { 0x5D83082Cu, "skills.generic.DivineResistBuff" },
            { 0x1F1A7C64u, "skills.generic.DivineResistPassive" },
            { 0xAC80BBEEu, "skills.generic.FearMeleeAttack" },
            { 0xC4A217A3u, "skills.generic.FearResistModPassive" },
            { 0xE588072Cu, "skills.generic.FearShot" },
            { 0x997C6A0Au, "skills.generic.FighterClassPassive" },
            { 0x40243947u, "skills.generic.FireBolt" },
            { 0x4024C5DBu, "skills.generic.FireCone" },
            { 0x63425376u, "skills.generic.FireCurseShot" },
            { 0xB2A6D958u, "skills.generic.FireDamageBuff" },
            { 0x8C80D7DDu, "skills.generic.FireMeleeSummon" },
            { 0xCB96C793u, "skills.generic.FireResistBuff" },
            { 0xA142566Bu, "skills.generic.FireResistPassive" },
            { 0x402CE606u, "skills.generic.FireRing" },
            { 0x402D6E54u, "skills.generic.FireShot" },
            { 0xBD9F10B4u, "skills.generic.HealSelf" },
            { 0x2F4A0032u, "skills.generic.IceBolt" },
            { 0xDE3EE483u, "skills.generic.IceDamageBuff" },
            { 0x54B3785Du, "skills.generic.IceMultiBolt" },
            { 0xF72ED2BEu, "skills.generic.IceResistBuff" },
            { 0x4BFA15B6u, "skills.generic.IceResistPassive" },
            { 0x2F53353Fu, "skills.generic.IceShot" },
            { 0xF1B72961u, "skills.generic.IceTargetedBurst" },
            { 0x56823B18u, "skills.generic.IceTargetedBurstUpgradeProcPassive" },
            { 0x327BF3B8u, "skills.generic.InfectiousPoisonUpgradeProcPassive" },
            { 0x90E6BBFBu, "skills.generic.MageClassPassive" },
            { 0xF3EBCFABu, "skills.generic.MagicDamageModPassive" },
            { 0x448B48B7u, "skills.generic.ManaSelf" },
            { 0x94B500E6u, "skills.generic.ManaShield" },
            { 0x7502AD10u, "skills.generic.MeleeAttackRatingModPassive" },
            { 0x8E2E23BCu, "skills.generic.MeleeAttackSpeedModPassive" },
            { 0x9B9699A5u, "skills.generic.MeleeDamageReflectionBuff" },
            { 0x4AC3B99Fu, "skills.generic.MinMoveSpeedBuff" },
            { 0x82EEC8C9u, "skills.generic.MonsterBaitHealthModPassive" },
            { 0xC3578423u, "skills.generic.NoxiousShot" },
            { 0x45E3A604u, "skills.generic.PenetrateKnockdownShot" },
            { 0x5FAE1AA6u, "skills.generic.PoisonBlastRadius" },
            { 0xA366A18Au, "skills.generic.PoisonDamageBuff" },
            { 0xBC568FC5u, "skills.generic.PoisonResistBuff" },
            { 0xBB68895Du, "skills.generic.PoisonResistPassive" },
            { 0xC30F7906u, "skills.generic.PoisonShot" },
            { 0x2515F184u, "skills.generic.PoisonTrail" },
            { 0xF7B882A1u, "skills.generic.RangeAttackSpeedModPassive" },
            { 0x83AC0575u, "skills.generic.RangedSpeedBuff" },
            { 0x582F9DC0u, "skills.generic.RangerClassPassive" },
            { 0xBBA98687u, "skills.generic.ShadowBolt" },
            { 0x80C11698u, "skills.generic.ShadowDamageBuff" },
            { 0xAA5741BAu, "skills.generic.ShadowLightning" },
            { 0x5E4290C8u, "skills.generic.ShadowLightningKnockdown" },
            { 0xECA5DFB1u, "skills.generic.ShadowLightningUpgradeProcPassive" },
            { 0xBBB21055u, "skills.generic.ShadowRage" },
            { 0x99B104D3u, "skills.generic.ShadowResistBuff" },
            { 0x12B28BABu, "skills.generic.ShadowResistPassive" },
            { 0x892B23BBu, "skills.generic.ShadowTendrils" },
            { 0x14D911E1u, "skills.generic.SlowDeBuff" },
            { 0x3AA3DD1Du, "skills.generic.SnowmanFreezeAura" },
            { 0x756AB8F5u, "skills.generic.SnowmanHealthModAuraBuff" },
            { 0xF7D5A663u, "skills.generic.SnowManIceDamageProcAuraModBuff" },
            { 0x86501370u, "skills.generic.Sprint" },
            { 0x2ADDC8C3u, "skills.generic.Stomp" },
            { 0x7A0B94B7u, "skills.generic.StunResistBuff" },
            { 0xDFF88E97u, "skills.generic.SummonerClassPassive" },
            { 0x1F638F77u, "skills.generic.SummonMonsterBait" },
            { 0xDD957B31u, "skills.generic.SummonBlingGnome" },
            { 0x7E1353D2u, "skills.Generic.SummonSnowman" },
            { 0xE9DDA4DFu, "skills.generic.Teleport" },
        };
        public class DroppedItemInfo
        {
            public GCObject Item;
            public long DbId;
            public string Zone;
            public uint ZoneId;
            public uint InstanceId;
            public string RuntimeInstanceKey;
            public int PosFixedX { get; set; }
            public int PosFixedY { get; set; }
            public int PosFixedZ { get; set; }
            public int HeadingFixed;
            public byte ItemObjectStateCount;
            public byte ItemObjectState;
            public ushort ItemObjectStateTimer;
            public uint ItemObjectLifetimeTicks;
            public uint ItemObjectLastSimulationTick;
            public int PlayerLevel;
            public string DroppedBy;
            public uint OwnerCharacterId;
            public uint OwnerGroupId;
            public string OwnerName;
            public string QuestBindingKey;
            public int Quantity = 1;
            public bool IsQuestItem;
            public bool IsGoldDrop;
            public uint GoldAmount;
            public bool GeneratedByBlingGnome;
        }
        private Dictionary<ushort, DroppedItemInfo> _droppedItems = new Dictionary<ushort, DroppedItemInfo>();
        private readonly List<ushort> _droppedItemOrder = new List<ushort>();

        private void StoreDroppedItem(ushort entityId, DroppedItemInfo info)
        {
            lock (_droppedItems)
            {
                if (!_droppedItems.ContainsKey(entityId))
                {
                    _droppedItemOrder.Remove(entityId);
                    _droppedItemOrder.Add(entityId);
                }
                _droppedItems[entityId] = info;
            }
        }

        private bool RemoveDroppedItem(ushort entityId, out DroppedItemInfo info)
        {
            lock (_droppedItems)
            {
                if (!_droppedItems.TryGetValue(entityId, out info))
                    return false;
                _droppedItems.Remove(entityId);
                _droppedItemOrder.Remove(entityId);
                return true;
            }
        }

        private sealed class QuestAcceptItemCommit
        {
            public QuestRewardMutation Rewards;
        }

        private bool TryCommitOnAcceptItem(RRConnection conn, QuestData quest, out QuestAcceptItemCommit commit)
        {
            commit = null;
            if (string.IsNullOrEmpty(quest?.onAcceptItem))
                return false;
            var itemDefinition = new QuestData
            {
                id = quest.id,
                minLevel = quest.minLevel,
                maxLevel = quest.maxLevel,
                numRewardItems = 1,
                rewardItemsSoulBound = quest.onAcceptItemsSoulBound,
                rewardItemsNoSell = quest.onAcceptItemsNoSell,
                rewardChoices = new List<QuestRewardChoice> { new QuestRewardChoice { generator = quest.onAcceptItem } }
            };
            if (!TryStageQuestRewards(conn, null, itemDefinition, 0, out QuestRewardMutation mutation))
                return false;
            bool durableCommitted = false;
            try
            {
                if (!SavePlayerQuests(conn))
                    return false;
                durableCommitted = true;
                commit = new QuestAcceptItemCommit { Rewards = mutation };
                return true;
            }
            finally
            {
                if (!durableCommitted)
                    RollbackQuestRewards(conn, mutation);
            }
        }

        private void MaterializeCommittedOnAcceptItem(RRConnection conn, QuestAcceptItemCommit commit)
        {
            if (conn == null || commit?.Rewards == null)
                throw new InvalidOperationException("Quest accept item commit is incomplete");
            CommitQuestRewards(conn, commit.Rewards);
        }

        private bool GiveStackedItem(RRConnection conn, string gcType, int totalCount, int maxStackSize = 100, LootDrop itemTemplate = null, bool soulBound = false, bool noSell = false)
        {
            if (totalCount <= 0 || string.IsNullOrEmpty(gcType) || maxStackSize <= 0 || maxStackSize > byte.MaxValue)
                return false;
            if (conn.UnitContainerId == 0)
            {
                Debug.LogError($"[GIVE-STACKED] No UnitContainerId - cannot give {gcType}");
                return false;
            }

            string connId = conn.ConnId.ToString();
            int remaining = totalCount;
            var packets = new List<byte[]>();
            var mergedSlots = new List<(uint Slot, int PreviousCount)>();
            var addedSlots = new List<(uint Slot, byte X, byte Y, int Width, int Height)>();

            void RollbackInventory()
            {
                for (int addedIndex = addedSlots.Count - 1; addedIndex >= 0; addedIndex--)
                {
                    var added = addedSlots[addedIndex];
                    RemoveInventoryItemBySlot(connId, added.Slot);
                    FreeInventorySlots(connId, added.X, added.Y, added.Width, added.Height);
                }
                for (int mergedIndex = mergedSlots.Count - 1; mergedIndex >= 0; mergedIndex--)
                {
                    var merged = mergedSlots[mergedIndex];
                    SetStackCount(connId, merged.Slot, merged.PreviousCount);
                }
            }

            Debug.LogError($"[GIVE-STACKED] {gcType} x{totalCount} (max stack {maxStackSize})");

            if (_playerInventoryItems.ContainsKey(connId))
            {
                var slots = new List<uint>(_playerInventoryItems[connId].Keys);
                foreach (var slotId in slots)
                {
                    if (remaining <= 0) break;
                    var entry = _playerInventoryItems[connId][slotId];
                    if (entry.item == null) continue;
                    if (!string.Equals(entry.item.GCClass, gcType, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!CanMergeItemState(entry.item, itemTemplate, soulBound, noSell))
                        continue;

                    int currentCount = GetStackCount(connId, slotId);
                    if (currentCount >= maxStackSize) continue;

                    int canAdd = System.Math.Min(maxStackSize - currentCount, remaining);
                    int newCount = currentCount + canAdd;
                    remaining -= canAdd;

                    var qWriter = new LEWriter();
                    qWriter.WriteByte(0x07);
                    qWriter.WriteByte(0x35);
                    qWriter.WriteUInt16(conn.UnitContainerId);
                    qWriter.WriteByte(0x22);
                    qWriter.WriteUInt32(slotId);
                    qWriter.WriteByte((byte)(newCount > 255 ? 255 : newCount));
                    WritePlayerEntitySynch(conn, qWriter);
                    qWriter.WriteByte(0x06);
                    packets.Add(qWriter.ToArray());

                    mergedSlots.Add((slotId, currentCount));
                    SetStackCount(connId, slotId, newCount);
                    Debug.LogError($"[GIVE-STACKED] Topped up slot {slotId} {currentCount}->{newCount} (+{canAdd})");
                }
            }

            var itemData = AuthoredGameplayCatalog.FindItem(gcType);
            int itemWidth = itemData?.inventoryWidth ?? 1;
            int itemHeight = itemData?.inventoryHeight ?? 1;

            while (remaining > 0)
            {
                var (sx, sy) = FindNextFreeInventorySlot(connId, itemWidth, itemHeight);
                if (sx < 0)
                {
                    Debug.LogError($"[GIVE-STACKED]  Inventory full! {remaining}x {gcType} not given");
                    RollbackInventory();
                    return false;
                }
                byte slotX = (byte)sx, slotY = (byte)sy;

                int stackSize = System.Math.Min(remaining, maxStackSize);
                remaining -= stackSize;
                uint slot = GetNextInventorySlot(connId);

                var writer = new LEWriter();
                writer.WriteByte(0x07);
                writer.WriteByte(0x35);
                writer.WriteUInt16(conn.UnitContainerId);
                writer.WriteByte(0x1E);
                writer.WriteByte(0x0B);
                var newItem = new GCObject
                {
                    GCClass = gcType,
                    DFCClass = ResolveAuthoredItemClass(gcType),
                    StoredLevel = itemTemplate?.ItemLevel > 0 ? itemTemplate.ItemLevel : Math.Max(1, ItemData.GetRequiredLevelFromGCClass(gcType)),
                    PresetScaleMod = itemTemplate?.ScaleMod,
                    StoredRarity = itemTemplate == null ? -1 : (int)itemTemplate.Rarity,
                    HasGeneratedItemState = itemTemplate?.HasGeneratedItemState == true,
                    RolledRequiresMembership = itemTemplate?.RolledRequiresMembership == true,
                    GeneratedRequiresMembership = itemTemplate?.RequiresMembership == true,
                    GeneratedItemModifiers = itemTemplate?.ItemModifiers == null ? new List<string>() : new List<string>(itemTemplate.ItemModifiers),
                    SoulBound = soulBound,
                    NoSell = noSell,
                    SoulBoundCountdown = soulBound ? (ushort)0 : ushort.MaxValue
                };
                newItem.WriteInitForInventory(writer, slotX, slotY, slot, newItem.StoredLevel, (byte)Math.Min(stackSize, byte.MaxValue));
                WritePlayerEntitySynch(conn, writer);
                writer.WriteByte(0x06);
                packets.Add(writer.ToArray());

                TrackInventoryItem(connId, slot, newItem, slotX, slotY);
                OccupyInventorySlots(connId, slotX, slotY, itemWidth, itemHeight);
                SetStackCount(connId, slot, stackSize);
                addedSlots.Add((slot, slotX, slotY, itemWidth, itemHeight));

                Debug.LogError($"[GIVE-STACKED] New stack: slot {slot} at ({slotX},{slotY}) x{stackSize}");
            }

            QuestProgressMutation questMutation = StageQuestItemAcquisition(conn, gcType, totalCount);
            if (!SavePlayerInventory(conn))
            {
                RollbackQuestItemAcquisition(questMutation);
                RollbackInventory();
                Debug.LogError($"[GIVE-STACKED] state=failed phase=commit item={gcType} quantity={totalCount}");
                return false;
            }
            for (int packetIndex = 0; packetIndex < packets.Count; packetIndex++)
                SendCompressedA(conn, 0x01, 0x0F, packets[packetIndex]);
            CommitQuestItemAcquisition(conn, questMutation, gcType, totalCount, "stacked-item-grant");
            Debug.LogError($"[GIVE-STACKED]  Done. {totalCount}/{totalCount} {gcType} placed");
            return true;
        }

        private static bool CanMergeItemState(GCObject item, LootDrop itemTemplate, bool soulBound, bool noSell, bool requiresMembership = false)
        {
            ushort expectedSoulBoundCountdown = soulBound ? (ushort)0 : ushort.MaxValue;
            if (item == null || item.SoulBound != soulBound || item.NoSell != noSell || item.SoulBoundCountdown != expectedSoulBoundCountdown)
                return false;
            bool expectedHasGeneratedItemState = requiresMembership || itemTemplate?.HasGeneratedItemState == true;
            bool expectedRolledRequiresMembership = itemTemplate?.RolledRequiresMembership == true;
            bool expectedGeneratedRequiresMembership = requiresMembership || itemTemplate?.RequiresMembership == true;
            if (item.HasGeneratedItemState != expectedHasGeneratedItemState
                || item.RolledRequiresMembership != expectedRolledRequiresMembership
                || item.GeneratedRequiresMembership != expectedGeneratedRequiresMembership)
                return false;
            if (itemTemplate == null)
                return string.IsNullOrEmpty(item.PresetScaleMod) && (item.GeneratedItemModifiers?.Count ?? 0) == 0;
            IReadOnlyList<string> existingModifiers = item.GeneratedItemModifiers ?? new List<string>();
            IReadOnlyList<string> templateModifiers = itemTemplate.ItemModifiers ?? new List<string>();
            return string.Equals(item.PresetScaleMod ?? "", itemTemplate.ScaleMod ?? "", StringComparison.OrdinalIgnoreCase)
                && existingModifiers.SequenceEqual(templateModifiers, StringComparer.OrdinalIgnoreCase);
        }

        private void UpgradePotionsForMembers(List<LootDrop> drops, RRConnection conn)
        {
            string who = conn?.LoginName ?? "<null>";
            if (drops == null) { Debug.LogError($"[LOOT-MEMBER] {who}: drops==null, skipping"); return; }
            if (conn == null) { Debug.LogError($"[LOOT-MEMBER] <null conn>: drops.Count={drops.Count}, skipping"); return; }

            bool isFree = IsPlayerFree(conn.LoginName);
            bool isAdmin = IsPlayerAdmin(conn.LoginName);
            Debug.LogError($"[LOOT-MEMBER] called for {who} isFree={isFree} isAdmin={isAdmin} drops.Count={drops.Count}");

            foreach (var drop in drops)
            {
                if (drop == null) { Debug.LogError("[LOOT-MEMBER]   drop=null"); continue; }
                if (drop.IsGold) { Debug.LogError($"[LOOT-MEMBER]   gold +{drop.GoldAmount}"); continue; }
                Debug.LogError($"[LOOT-MEMBER]   item gcType='{drop.GCType ?? "<null>"}' label='{drop.Label ?? "<null>"}'");
            }

            if (isFree) { Debug.LogError($"[LOOT-MEMBER] {who} is free, no upgrades"); return; }

            int swapped = 0;
            foreach (var drop in drops)
            {
                if (drop == null || drop.IsGold || string.IsNullOrEmpty(drop.GCType)) continue;
                string gcType = drop.GCType;
                string gcTypeLower = gcType.ToLowerInvariant();
                string originalGcType = gcType;

                if (gcTypeLower.Contains("minorhealthpotion"))
                {
                    drop.GCType = System.Text.RegularExpressions.Regex.Replace(
                        gcType, "minorhealthpotion", "MajorHealthPotion",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    drop.Label = "Major Health Potion";
                    Debug.LogError($"[LOOT-MEMBER]   SWAP A-H: '{originalGcType}' -> '{drop.GCType}'");
                    swapped++;
                    continue;
                }
                if (gcTypeLower.Contains("minormanapotion"))
                {
                    drop.GCType = System.Text.RegularExpressions.Regex.Replace(
                        gcType, "minormanapotion", "MajorManaPotion",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    drop.Label = "Major Mana Potion";
                    Debug.LogError($"[LOOT-MEMBER]   SWAP A-M: '{originalGcType}' -> '{drop.GCType}'");
                    swapped++;
                    continue;
                }

                if (gcTypeLower.Contains("healthpotion_sm"))
                {
                    drop.GCType = System.Text.RegularExpressions.Regex.Replace(
                        gcType, "healthpotion_sm", "HealthPotion_Lg",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    drop.Label = "Major Health Potion";
                    Debug.LogError($"[LOOT-MEMBER]   SWAP B-H: '{originalGcType}' -> '{drop.GCType}'");
                    swapped++;
                    continue;
                }
                if (gcTypeLower.Contains("manapotion_sm"))
                {
                    drop.GCType = System.Text.RegularExpressions.Regex.Replace(
                        gcType, "manapotion_sm", "ManaPotion_Lg",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    drop.Label = "Major Mana Potion";
                    Debug.LogError($"[LOOT-MEMBER]   SWAP B-M: '{originalGcType}' -> '{drop.GCType}'");
                    swapped++;
                    continue;
                }

                if (gcTypeLower.Contains("potion"))
                {
                    Debug.LogError($"[LOOT-MEMBER]   POTION NOT MATCHED: '{gcType}' (need a swap rule for this)");
                }
            }
            Debug.LogError($"[LOOT-MEMBER] {who}: {swapped} swap(s) total");
        }

        private bool CompleteGotoObjective(RRConnection conn, ActiveQuest quest, QuestProgress objective)
        {
            int previousCurrent = objective.Current;
            objective.Current = objective.Required;
            if (!SavePlayerQuests(conn))
            {
                objective.Current = previousCurrent;
                Debug.LogError($"[GOTO] state=failed phase=persist quest={quest.QuestId} objective='{objective.Label}'");
                return false;
            }
            QuestManager.Instance.SendProgressPacket(conn, quest.InstanceId, quest);
            return true;
        }


        private static bool IsDirectAuthoredRewardItem(string gcType)
        {
            if (string.IsNullOrWhiteSpace(gcType))
                return false;
            if (AuthoredGameplayCatalog.FindItem(gcType) != null)
                return true;
            if (GCDatabase.Instance == null || !GCDatabase.Instance.IsLoaded)
                return false;

            var node = GCDatabase.Instance.ResolveWithInheritance(gcType);
            if (node == null)
                return false;
            string extends = node.Extends ?? string.Empty;
            if (extends.IndexOf("Generator", StringComparison.OrdinalIgnoreCase) >= 0 ||
                gcType.IndexOf("Generator", StringComparison.OrdinalIgnoreCase) >= 0 ||
                gcType.EndsWith("IG", StringComparison.OrdinalIgnoreCase))
                return false;

            return AuthoredExtendsClass(gcType, "Item") ||
                   AuthoredExtendsClass(gcType, "ActiveItem") ||
                   AuthoredExtendsClass(gcType, "MeleeWeapon") ||
                   AuthoredExtendsClass(gcType, "RangedWeapon") ||
                   AuthoredExtendsClass(gcType, "Armor");
        }

        private bool TryMaterializeQuestXPBonusModifier(RRConnection conn, out bool added)
        {
            added = false;
            if (conn == null)
                return false;
            PlayerState playerState = GetPlayerState(conn.ConnId.ToString());
            if (playerState == null)
                return false;
            const string modifierType = QUEST_XP_BONUS_MODIFIER_GC_TYPE;
            const string stackRule = "UNIQUEBYTYPE";
            if (!playerState.ShouldAcceptAttributeModifier(modifierType, modifierType, stackRule, 0, 0))
                return true;
            var attributes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["EXPMOD"] = 15
            };
            added = playerState.ApplyAuthoredAttributeModifier(
                    modifierType,
                    attributes,
                    0,
                    true,
                    "quest-reward",
                    modifierType,
                    0,
                    modifierType,
                    "EXPMOD",
                    0,
                    stackRule,
                    level: 0,
                    sourceIsSelf: 1);
            return added;
        }

        public bool SendQuestXPBonusModifier(RRConnection conn)
        {
            if (conn == null || conn.ModifiersId == 0)
            {
                Debug.LogError("[QUEST-XP-BUFF] Cannot send QuestXPBonus - ModifiersId not set");
                return false;
            }
            if (!TryMaterializeQuestXPBonusModifier(conn, out bool added))
                return false;
            if (!added)
                return true;

            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x35);
            writer.WriteUInt16((ushort)conn.ModifiersId);
            writer.WriteByte(0x00);
            WriteGCType(writer, QUEST_XP_BONUS_MODIFIER_GC_TYPE, preserveCase: true);
            writer.WriteUInt32(QUEST_XP_BONUS_MODIFIER_ID);
            writer.WriteByte(0);
            writer.WriteUInt32(0);
            writer.WriteUInt32(0x00000000);
            writer.WriteByte(0x01);
            WritePlayerEntitySynch(conn, writer);
            writer.WriteByte(0x06);

            SendCompressedA(conn, 0x01, 0x0F, writer.ToArray());
            Debug.LogError($"[QUEST-XP-BUFF]  Sent QuestXPBonus modifier (+15% EXPMOD)");

            RecordModifierSent(conn.LoginName, QUEST_XP_BONUS_MODIFIER_GC_TYPE, QUEST_XP_BONUS_MODIFIER_ID,
                level: 0, powerLevel: 0, duration: 0, sourceIsSelf: 1);
            Debug.LogError($"[QUEST-XP-BUFF]  Tracked for zone-change persistence");
            return true;
        }

        public void SendZoneSpawnInvulnerability(RRConnection conn)
        {
            if (!ShouldSendZoneSpawnInvulnerability(conn))
            {
                ClearZoneSpawnInvulnerability(conn, "ZONE-SKIP");
                Debug.LogError($"[ZONE-INVULN] Skipped ZoneSpawnInvulnerability for {conn?.LoginName ?? "<null>"} (zone={conn?.CurrentZoneName ?? ""})");
                return;
            }

            if (conn.ModifiersId == 0)
            {
                Debug.LogError("[ZONE-INVULN] Cannot send ZoneSpawnInvulnerability - ModifiersId not set");
                ClearZoneSpawnInvulnerability(conn, "MISSING-MODIFIERS");
                return;
            }

            ClearZoneSpawnInvulnerability(conn, "ZONE-SPAWN-REARM");
            conn.ZoneSpawnInvulnerabilityPending = true;
            conn.ZoneSpawnInvulnerabilityQueued = false;
            ulong dueTick = (ulong)_combatTick + ZoneSpawnInvulnerabilitySpawnDelayTicks;
            conn.ZoneSpawnInvulnerabilityDueTick = dueTick >= uint.MaxValue ? uint.MaxValue : (uint)dueTick;
            Debug.LogError($"[ZONE-INVULN] Scheduled ZoneSpawnInvulnerability for {conn.LoginName} tick={_combatTick}->{conn.ZoneSpawnInvulnerabilityDueTick} zone={conn.CurrentZoneName}");
        }

        private void ProcessPendingZoneSpawnInvulnerabilities(uint simulationTick)
        {
            foreach (RRConnection conn in GetConnectionInsertionOrderSnapshot())
            {
                if (conn == null || !conn.ZoneSpawnInvulnerabilityPending || conn.ZoneSpawnInvulnerabilityQueued || simulationTick < conn.ZoneSpawnInvulnerabilityDueTick)
                    continue;
                if (!conn.IsConnected || !conn.IsSpawned || !conn.AllowFlush || conn.ModifiersId == 0 || !ShouldSendZoneSpawnInvulnerability(conn))
                {
                    ClearZoneSpawnInvulnerability(conn, "PENDING-INVALID");
                    continue;
                }
                conn.ZoneSpawnInvulnerabilityQueued = true;
                uint generation = conn.ZoneSpawnInvulnerabilityGeneration;
                ushort componentId = conn.ModifiersId;
                conn.MessageQueue.EnqueueDeferred(
                    () => BuildZoneSpawnInvulnerabilityUpdate(conn, componentId, generation),
                    componentId: componentId);
                int peerCount = 0;
                ushort remoteModifiersId = conn.ReplicaModId;
                if (remoteModifiersId != 0 && !string.IsNullOrEmpty(conn.LoginName))
                {
                    foreach (RRConnection viewer in GetConnectionInsertionOrderSnapshot())
                    {
                        if (viewer == null || viewer == conn || !viewer.IsConnected || !viewer.IsSpawned || !viewer.AllowFlush || !IsSameMovementRuntime(viewer, conn))
                            continue;
                        if (!_remoteAvatarIds.TryGetValue(viewer.LoginName, out Dictionary<string, ushort> avatarMap)
                            || !avatarMap.TryGetValue(conn.LoginName, out ushort remoteAvatarId)
                            || remoteAvatarId == 0)
                            continue;
                        RRConnection capturedViewer = viewer;
                        capturedViewer.MessageQueue.EnqueueDeferred(
                            () => BuildRemoteZoneSpawnInvulnerabilityAddUpdate(conn, capturedViewer, remoteModifiersId, generation),
                            componentId: remoteModifiersId);
                        peerCount++;
                    }
                }
                Debug.LogError($"[ZONE-INVULN] Queued ZoneSpawnInvulnerability for {conn.LoginName} tick={simulationTick} component={componentId} peers={peerCount}");
            }
        }

        private byte[] BuildZoneSpawnInvulnerabilityUpdate(RRConnection conn, ushort componentId, uint generation)
        {
            if (conn == null
                || conn.ZoneSpawnInvulnerabilityGeneration != generation
                || !conn.ZoneSpawnInvulnerabilityPending
                || !conn.ZoneSpawnInvulnerabilityQueued)
                return Array.Empty<byte>();
            if (!conn.IsConnected || !conn.IsSpawned || !conn.AllowFlush || componentId == 0 || conn.ModifiersId != componentId || !ShouldSendZoneSpawnInvulnerability(conn))
            {
                ClearZoneSpawnInvulnerability(conn, "WRITER-INVALID");
                return Array.Empty<byte>();
            }

            var writer = new LEWriter();
            writer.WriteByte(0x35);
            writer.WriteUInt16(componentId);
            writer.WriteByte(0x00);
            WriteGCType(writer, "avatar.base.ZoneSpawnInvulnerabilityModifier", preserveCase: true);
            writer.WriteUInt32(ZoneSpawnInvulnerabilityModifierId);
            writer.WriteByte(0);
            writer.WriteUInt32(0);
            writer.WriteUInt32(ZoneSpawnInvulnerabilityDuration);
            writer.WriteByte(0x01);
            WritePlayerEntitySynch(conn, writer);
            SetZoneSpawnInvulnerability(conn);
            RecordModifierSent(conn.LoginName, "avatar.base.ZoneSpawnInvulnerabilityModifier", ZoneSpawnInvulnerabilityModifierId,
                level: 0, powerLevel: 0, duration: ZoneSpawnInvulnerabilityDuration, sourceIsSelf: 1);
            Debug.LogError($"[ZONE-INVULN] Sent ZoneSpawnInvulnerability for {conn.LoginName} tick={_combatTick} zone={conn.CurrentZoneName}");
            return writer.ToArray();
        }

        private byte[] BuildRemoteZoneSpawnInvulnerabilityAddUpdate(RRConnection source, RRConnection viewer, ushort componentId, uint generation)
        {
            if (source == null
                || viewer == null
                || source.ZoneSpawnInvulnerabilityGeneration != generation
                || (!source.ZoneSpawnInvulnerabilityPending && !source.ZoneSpawnInvulnerabilityActive)
                || !source.IsConnected
                || !source.IsSpawned
                || !source.AllowFlush
                || !viewer.IsConnected
                || !viewer.IsSpawned
                || !viewer.AllowFlush
                || componentId == 0
                || source.ReplicaModId != componentId
                || !IsSameMovementRuntime(viewer, source)
                || !_remoteAvatarIds.TryGetValue(viewer.LoginName, out Dictionary<string, ushort> avatarMap)
                || !avatarMap.TryGetValue(source.LoginName, out ushort remoteAvatarId)
                || remoteAvatarId == 0)
                return Array.Empty<byte>();

            var writer = new LEWriter();
            writer.WriteByte(0x35);
            writer.WriteUInt16(componentId);
            writer.WriteByte(0x00);
            WriteGCType(writer, "avatar.base.ZoneSpawnInvulnerabilityModifier", preserveCase: true);
            writer.WriteUInt32(ZoneSpawnInvulnerabilityModifierId);
            writer.WriteByte(0);
            writer.WriteUInt32(0);
            writer.WriteUInt32(ZoneSpawnInvulnerabilityDuration);
            writer.WriteByte(0x01);
            return TryWriteRemoteAvatarEntitySynchInfo(source, writer, componentId, 0x00, "MP-ZONE-INVULN-ADD")
                ? writer.ToArray()
                : Array.Empty<byte>();
        }

        private const uint ZoneSpawnInvulnerabilityDuration = 1800;
        private const uint ZoneSpawnInvulnerabilitySpawnDelayTicks = 23;
        private const uint ZoneSpawnInvulnerabilityModifierId = 3;

        private struct PendingSpell
        {
            public long Sequence;
            public long SubEntityOrder;
            public RRConnection Conn;
            public PlayerState State;
            public Combat.Monster Monster;
            public Combat.SpellData Spell;
            public byte ManipId;
            public byte UseFlags;
            public ushort ComponentId;
            public int StartFixedX;
            public int StartFixedY;
            public int AimFixedX;
            public int AimFixedY;
            public int CurrentFixedX;
            public int CurrentFixedY;
            public int CurrentFixedZ;
            public int DirectionFixedX;
            public int DirectionFixedY;
            public int VelocityFixedX;
            public int VelocityFixedY;
            public int VelocityFixedZ;
            public int GroundOffsetFixedZ;
            public string InstanceKey;
            public int DueTick;
            public int ProjectileHitDistanceF32;
            public int ProjectileDelayTicks;
            public bool QueuedWithoutInitialTarget;
            public bool ProjectileRuntimeInitialized;
            public bool ProjectileSubEntityRegistered;
            public bool AwaitingActionResponsePacket;
            public byte ActionResponseId;
            public byte ActionResponseSessionId;
            public int ActionAimFixedZ;
            public uint ActionReceiveTick;
            public uint ActionResponseWriterTick;
            public uint ActionResponseSimulationApplyTick;
            public int ActionResponseWireMessageIndex;
            public long ActionAdmissionSequence;
            public bool ActionAdmissionApplied;
            public int FollowClientRecordsAtAdmission;
            public uint SkillUseTick;
            public bool SkillUseCommitted;
            public bool SkillUseCommitAttempted;
            public bool StartsAfterSkillsChild;
            public int FireTick;
            public int LastUpdateTick;
            public int UpdatesCompleted;
            public int MaxLifetimeTicks;
            public int ProjectileSpeedF32;
            public int ProjectileSizeF32;
            public int ProjectileOffsetF32;
            public int StepDistanceF32;
            public int InitialDistanceF32;
            public int CurrentDistanceF32;
            public int MaxDistanceF32;
            public bool ProjectileGeometryInitialized;
            public bool WeaponProjectileRuntime;
        }

        private sealed class PendingChainSpell
        {
            public long Sequence;
            public RRConnection Conn;
            public PlayerState State;
            public Combat.SpellData Spell;
            public Combat.Monster Center;
            public Combat.MersenneTwister Rng;
            public HashSet<uint> Hit;
            public string InstanceKey;
            public string ZoneName;
            public uint ViewerEntityId;
            public int DueTick;
            public int ActiveBranchExpiryTick;
            public int RemainingChains;
            public int LastFixedX;
            public int LastFixedY;
            public int LastFixedZ;
            public int SkillLevel;
            public bool WaitingForExpiry;
        }

        private struct SpellProjectileGeometry
        {
            public int BurstHeadingFixed;
            public int BurstDirectionFixedX;
            public int BurstDirectionFixedY;
            public int BurstSourceRawFixedX;
            public int BurstSourceRawFixedY;
            public int BurstSourceFixedX;
            public int BurstSourceFixedY;
            public int BurstTargetFixedX;
            public int BurstTargetFixedY;
            public int ProjectileSourceRawFixedX;
            public int ProjectileSourceRawFixedY;
            public int ProjectileSourceFixedX;
            public int ProjectileSourceFixedY;
            public int ProjectileDirectionFixedX;
            public int ProjectileDirectionFixedY;
            public int ProjectileVelocityFixedX;
            public int ProjectileVelocityFixedY;
            public int BurstRayDistanceFixed;
            public int ProjectileRayDistanceFixed;
        }

        private bool TryGetPendingPlayerSkillDamageTick(uint playerEntityId, uint targetEntityId, out uint damageTick)
        {
            damageTick = 0;
            if (playerEntityId == 0 || targetEntityId == 0 || _pendingSpells.IsEmpty)
                return false;

            PendingSpell[] pendingSpells = _pendingSpells.ToArray();
            bool found = false;
            for (int pendingIndex = 0; pendingIndex < pendingSpells.Length; pendingIndex++)
            {
                PendingSpell pending = pendingSpells[pendingIndex];
                uint pendingPlayerId = pending.Conn?.Avatar != null ? (uint)pending.Conn.Avatar.Id : 0;
                if (pendingPlayerId != playerEntityId
                    || pending.Monster == null
                    || pending.Monster.EntityId != targetEntityId
                    || pending.Spell == null
                    || !pending.Spell.HasAnyDamage
                    || pending.ProjectileRuntimeInitialized
                    || pending.AwaitingActionResponsePacket
                    || !pending.SkillUseCommitted)
                    continue;

                uint candidateTick = pending.DueTick > 0 ? (uint)pending.DueTick : _combatTick;
                if (!found || candidateTick < damageTick)
                {
                    damageTick = candidateTick;
                    found = true;
                }
            }
            return found;
        }

        private struct SpellWeaponDamageEffectResult
        {
            public bool Attempted;
            public bool Landed;
            public bool Applied;
            public bool Died;
            public uint OldHPWire;
            public uint NewHPWire;
            public uint DamageWire;
            public uint HitRaw;
            public uint BlockRaw;
            public uint DamageRaw;
            public int HitRoll;
            public int BlockRoll;
            public int HitThreshold;
            public int AttackRating;
            public int DefenseRating;
            public int DamageMod;
            public int SkillDamageModRaw;
            public int ARMod;
            public int MinDamageWire;
            public int MaxDamageWire;
            public bool IsCritical;
            public bool ReferencedModifierHandled;
            public string ResultName;
        }

        private RRConnection FindConnectionByAvatarEntityId(uint entityId)
        {
            if (entityId == 0) return null;
            foreach (var conn in GetConnectionInsertionOrderSnapshot())
            {
                if (conn?.Avatar == null) continue;
                if ((uint)conn.Avatar.Id == entityId)
                    return conn;
            }
            return null;
        }

        private void DrainPendingModifierKills()
        {
            while (CombatRuntime.Instance.HasPendingModifierKills)
            {
                var kill = CombatRuntime.Instance.DequeuePendingModifierKill();
                if (kill == null)
                    break;

                var monster = CombatRuntime.Instance.GetMonster(kill.TargetEntityId);
                CombatPlayer rewardOwner = CombatRuntime.Instance.ResolveCombatRewardOwner(kill.SourceEntityId, monster?.InstanceKey);
                var conn = rewardOwner == null ? null : FindConnectionByAvatarEntityId(rewardOwner.EntityId);
                Debug.LogError($"[POISON-SHOT-MOD-KILL] drain target={kill.TargetEntityId} sourceEntity={kill.SourceEntityId} damageTick={kill.DamageTick} conn={(conn != null ? conn.ConnId.ToString() : "null")} source={kill.Source ?? "modifier-tick"}");
                if (monster == null)
                {
                    Debug.LogError($"[POISON-SHOT-MOD-KILL] missing monster target={kill.TargetEntityId}");
                    continue;
                }

                CombatRuntime.Instance.CancelMonsterPendingAttack(monster, "SPELL-MOD-tick-kill");
                try
                {
                    bool finalized = TryFinalizeMonsterKill(conn, monster, "SPELL-MOD-tick");
                    Debug.LogError($"[POISON-SHOT-MOD-KILL] finalize result={finalized} monster={monster.Name} eid={monster.EntityId}");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[KILL-ERROR] SPELL-MOD-tick finalize failed for {monster.Name}: {ex.Message}\n{ex.StackTrace}");
                    if (IsUseTargetingMonster(conn, monster))
                        ClearUseTargetAndReleaseControl(conn, "SPELL-MOD-tick-error");
                }

            }
        }

        private void DrainWeaponUseStateKills(string source)
        {
            while (Combat.WeaponUseRuntime.Instance.HasPendingKills)
            {
                var kill = Combat.WeaponUseRuntime.Instance.DequeueKill();
                if (kill == null || !kill.Killed)
                    continue;

                Debug.LogError($"[WEAPON-USE]  KILL: {kill.Monster?.Name ?? "monster"} killed by {kill.ConnKey} ({kill.DamageDealt} final dmg) source={source ?? "unknown"}");
                if (kill.Monster != null)
                    CombatRuntime.Instance.CancelMonsterPendingAttack(kill.Monster, "WeaponUseState-kill");
                try
                {
                    bool finalized = TryFinalizeMonsterKill(kill.Connection, kill.Monster, source ?? "WeaponUseState-tick");
                    Debug.LogError($"[WEAPON-USE] finalize result={finalized} monster={kill.Monster?.Name} eid={kill.Monster?.EntityId} source={source ?? "unknown"}");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[KILL-ERROR] WeaponUseState finalize failed for {kill.Monster?.Name}: {ex.Message}\n{ex.StackTrace}");
                    if (IsUseTargetingMonster(kill.Connection, kill.Monster))
                        ClearUseTargetAndReleaseControl(kill.Connection, "WeaponUseState-error");
                }
            }
        }

        private void DrainPendingWeaponControlReleases(string source)
        {
            while (Combat.WeaponUseRuntime.Instance.HasPendingControlReleases)
            {
                RRConnection releaseConn = Combat.WeaponUseRuntime.Instance.DequeueControlRelease();
                if (releaseConn != null)
                {
                    releaseConn.ActiveUseTargetWeaponReleasePending = true;
                    Debug.LogError($"[PLAYER-ACTION-RELEASE-PENDING] action=UseTarget conn={releaseConn.ConnId} target={releaseConn.ActiveUseTargetId} tick={_combatTick} source={source} sourceFunction=RangedWeapon::update->Behavior::update@0x005154B0");
                }
            }
        }

        private HashSet<string> GetActivePlayerInstanceKeys(uint simulationTick, out HashSet<uint> activePlayerEntityIds)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            activePlayerEntityIds = new HashSet<uint>();
            foreach (RRConnection conn in GetConnectionInsertionOrderSnapshot())
            {
                if (conn == null || !conn.IsConnected || !conn.IsSpawned || !conn.AllowFlush || !conn.TickUpdatesActive || conn.Avatar == null)
                    continue;
                string key = RoomRuntime.NormalizeInstanceKey(conn.RuntimeInstanceKey);
                if (!string.IsNullOrEmpty(key))
                {
                    keys.Add(key);
                    activePlayerEntityIds.Add((uint)conn.Avatar.Id);
                }
            }
            return keys;
        }

        private void TickCombatDeterministicSystems(uint simulationTick)
        {
            HashSet<string> activeInstanceKeys = GetActivePlayerInstanceKeys(simulationTick, out HashSet<uint> activePlayerEntityIds);
            Dictionary<string, EncounterObjectTickSnapshot> encounterObjectTickSnapshots = BuildEncounterObjectTickSnapshots();
            CombatRuntime.Instance.CaptureMonsterFollowTargetStates();
            BlingGnomeRuntime.Instance.CommitPendingSpawns(this, simulationTick);
            BlingGnomeLockstepRuntime.Instance.Sync(this, GetConnectionInsertionOrderSnapshot());
            BlingGnomeLockstepRuntime.Instance.CaptureFollowTargetStates(simulationTick);
            foreach (ClientSubEntityEntry subEntity in CombatRuntime.Instance.GetClientSubEntityOrderSnapshot())
            {
                if (!activeInstanceKeys.Contains(subEntity.InstanceKey))
                    continue;
                switch (subEntity.Kind)
                {
                    case ClientSubEntityKind.PlayerWeaponProjectile:
                        Combat.WeaponUseRuntime.Instance.TickProjectileSubEntity(subEntity.LocalId, subEntity.InstanceKey, simulationTick, "ClientEntityManager.updateEntities-subentities");
                        DrainWeaponUseStateKills("PlayerWeaponProjectile-subentity");
                        break;
                    case ClientSubEntityKind.PlayerSpellProjectile:
                        ProcessPendingSpellSubEntity(subEntity.LocalId, subEntity.InstanceKey, simulationTick);
                        break;
                    case ClientSubEntityKind.MonsterProjectile:
                        CombatRuntime.Instance.TickMonsterProjectileSubEntity(subEntity.LocalId, subEntity.InstanceKey, simulationTick);
                        break;
                    case ClientSubEntityKind.PlayerSpellChainProjectile:
                        ProcessPendingChainSpellSubEntity(subEntity.LocalId, subEntity.InstanceKey, simulationTick);
                        break;
                    case ClientSubEntityKind.MonsterSpellChainProjectile:
                        CombatRuntime.Instance.TickMonsterChainSubEntity(subEntity.LocalId, subEntity.InstanceKey, simulationTick);
                        break;
                    case ClientSubEntityKind.PlayerPvpWeaponProjectile:
                        TickPvpWeaponProjectile(subEntity.LocalId, subEntity.InstanceKey, simulationTick);
                        break;
                }
            }
            CombatRuntime.Instance.MarkSubEntityUpdateCompleted(simulationTick, "GameServer.Update-subentities");

            foreach (uint entityId in CombatRuntime.Instance.GetEntityUpdateOrderSnapshot(simulationTick))
            {
                if (CombatRuntime.Instance.IsMonsterEntity(entityId))
                {
                    var monster = CombatRuntime.Instance.GetMonster(entityId);
                    if (monster == null)
                        continue;
                    if (!string.IsNullOrEmpty(monster.InstanceKey) && !activeInstanceKeys.Contains(monster.InstanceKey))
                        continue;
                    if (!CombatRuntime.Instance.IsMonsterEntityUpdateAdmitted(monster, simulationTick))
                        continue;
                    CombatRuntime.Instance.TickMonsterBehaviorForEntity(entityId);
                    CombatRuntime.Instance.AdvanceMonsterActiveSkillsForEntity(entityId);
                    CombatRuntime.Instance.AdvanceMonsterWeaponManipulatorChild(entityId);
                    CombatRuntime.Instance.AdvanceMonsterModifiersChild(entityId, simulationTick);
                    DrainPendingModifierKills();
                    CombatRuntime.Instance.AdvanceMonsterUnitUpdateForEntity(entityId);
                    CombatRuntime.Instance.AdvanceMonsterDeathLifecycleForEntity(entityId);
                    CombatRuntime.Instance.AdvanceMonsterStockUnitLifespanForEntity(entityId);
                    monster = CombatRuntime.Instance.GetMonster(entityId);
                    if (monster == null)
                        continue;
                }
                else if (CombatRuntime.Instance.IsPlayerEntity(entityId))
                {
                    var player = CombatRuntime.Instance.GetPlayer(entityId);
                    if (player == null || !activePlayerEntityIds.Contains(entityId))
                        continue;
                    RRConnection playerConnection = FindConnectionByAvatarEntityId(entityId);
                    if (string.IsNullOrEmpty(player.InstanceKey) || !activeInstanceKeys.Contains(player.InstanceKey))
                        continue;
                    Combat.WeaponUseRuntime.Instance.TickPlayerWeaponManipulatorChild(entityId, simulationTick);
                    TickPvpWeaponUse(entityId, simulationTick);
                    DrainWeaponUseStateKills("PlayerWeaponManipulator-child");
                    DrainPendingWeaponControlReleases("PlayerWeaponManipulator-child-release");
                    CombatRuntime.Instance.AdvancePlayerAnchoredMonsterAurasForEntity(entityId);
                    CombatRuntime.Instance.AdvancePlayerDamageModifierRuntime(entityId, simulationTick, "ClientEntityManager.updateEntities-player-modifiers");
                    player.PlayerState?.AdvanceModifiersChild("ClientEntityManager.updateEntities-player-modifiers");
                    ProcessPendingSpellProjectilesForPlayerEntity(entityId, simulationTick);
                    ProcessPendingSpellsForPlayerEntity(entityId, simulationTick);
                    ProcessPendingSelfSpellEffects(entityId, simulationTick);
                    ProcessPendingBlingGnomeSkillEffects(entityId, simulationTick);
                    ProcessPendingAoECasts(entityId, simulationTick);
                    AdvanceFriendlySummonCast(entityId, simulationTick);
                    UpdatePlayerUseTargetBehaviorChild(entityId, simulationTick);
                    CombatRuntime.Instance.UpdatePlayerUnitMoverTouchingMonsters(entityId, simulationTick);
                    TickUseTargetMoving(entityId, simulationTick);
                    UpdateUsePositionActionFacing(entityId, simulationTick);
                    Combat.WeaponUseRuntime.Instance.TryBeginPlayerWeaponEntity(entityId, simulationTick);
                    AdvanceOwnerAckFollowClientQueue(entityId, simulationTick);
                    AdvanceUnitFollowClientQueue(entityId, simulationTick);
                    BlingGnomeLockstepRuntime.Instance.CaptureFollowTargetStateForOwnerEntity(entityId, simulationTick);
                    CombatRuntime.Instance.CaptureMonsterFollowTargetStateForEntity(
                        entityId,
                        playerConnection != null
                            && playerConnection.UsePositionActionMirrored
                            && !playerConnection.UsePositionActionStoppedFollowClient,
                        playerConnection?.UnitFollowClientMoverMode);
                    player.PlayerState?.AdvanceEntitySynchInfoHPToTick(simulationTick, "ClientEntityManager.updateEntities-player-unit-update");
                }
                else if (BlingGnomeLockstepRuntime.Instance.IsEntity(entityId))
                {
                    BlingGnomeLockstepRuntime.Instance.TickEntity(this, entityId, simulationTick);
                }
                else if (CombatRuntime.Instance.TryGetEncounterObjectInstanceKey(entityId, out string encounterObjectInstanceKey)
                    && activeInstanceKeys.Contains(encounterObjectInstanceKey))
                {
                    TickEncounterObjectForEntity(entityId, simulationTick, encounterObjectTickSnapshots);
                }
                else if (CombatRuntime.Instance.TryGetItemObjectInstanceKey(entityId, out string itemObjectInstanceKey)
                    && activeInstanceKeys.Contains(itemObjectInstanceKey))
                {
                    BlingGnomeLockstepRuntime.Instance.TickItemObject(this, entityId, simulationTick);
                    TickDroppedItemLifetimeForEntity((ushort)entityId, simulationTick);
                }
            }

            CombatRuntime.Instance.UpdateMaintenance();
            CaptureSimulationParityFrame(simulationTick);
        }

        private bool CommitActiveSkillUse(
            RRConnection conn,
            PlayerState state,
            Combat.SpellData spell,
            ushort componentId,
            byte actionId,
            int skillLevel,
            int simulationTick,
            bool startsAfterSkillsChild)
        {
            if (conn == null || state == null || spell == null)
                return false;
            if (!Combat.SpellDatabase.TryValidatePlayerResourceCommitRuntime(spell, out string resourceRuntimeReason))
            {
                SkillEffectTracker.RecordPlayerGraph(conn.Avatar != null ? (uint)conn.Avatar.Id : 0u, 0, spell.SkillId, spell.TargetType, spell.OrderedEffects?.Count ?? 0, "rejected", resourceRuntimeReason, unchecked((uint)simulationTick));
                Debug.LogError($"[ACTIVE-SKILL-USE] tick={simulationTick} skill={spell.DisplayName ?? spell.SkillId ?? "UNKNOWN"} state=blocked reason={resourceRuntimeReason ?? "unsupported-player-runtime"} resourceCommit=False sourceFunction=ActiveSkill::validateUse@0x00538710");
                return false;
            }

            bool initUseCooldownStarted = spell.InitUseResetsCooldown
                && conn.ActiveUseTargetInitUseCooldownStarted
                && conn.ActiveUseTargetInitUseCooldownComponentId == componentId
                && conn.ActiveUseTargetInitUseCooldownActionId == actionId;

            uint manaCostWire = Combat.SpellDatabase.GetManaCost(spell, skillLevel, 0);
            uint oldManaWire = state.CurrentManaWire;
            if (!initUseCooldownStarted && IsActiveSkillCooldown(conn, spell, actionId, out uint remainingCooldownTicks))
            {
                Debug.LogError($"[ACTIVE-SKILL-USE] tick={simulationTick} skill={spell.DisplayName ?? spell.SkillId ?? "UNKNOWN"} state=blocked reason=cooldown remainingTicks={remainingCooldownTicks} sourceFunction=ActiveSkill::validateUse@0x00538710");
                return false;
            }
            if (oldManaWire < manaCostWire)
            {
                Debug.LogError($"[ACTIVE-SKILL-USE] tick={simulationTick} skill={spell.DisplayName ?? spell.SkillId ?? "UNKNOWN"} state=blocked reason=mana mana={oldManaWire} cost={manaCostWire} sourceFunction=ActiveSkill::validateUse@0x00538710->ActiveSkill::getManaCost@0x00539D70");
                return false;
            }
            uint newManaWire = oldManaWire - manaCostWire;
            if (!_selectedCharacter.TryGetValue(conn.LoginName, out GCObject selectedCharacter)
                || selectedCharacter == null
                || !_activeCharacter.TryGetValue(conn.LoginName, out SavedCharacter activeCharacter)
                || activeCharacter == null
                || selectedCharacter.Id != activeCharacter.id
                || !CharacterRepository.TryUpdateCharacterCurrentMana(activeCharacter.id, newManaWire, "active-skill-use"))
            {
                Debug.LogError($"[ACTIVE-SKILL-USE] tick={simulationTick} skill={spell.DisplayName ?? spell.SkillId ?? "UNKNOWN"} state=blocked reason=mana-persistence resourceCommit=False");
                return false;
            }
            try
            {
                state.SetCurrentMana(newManaWire, $"ActiveSkill::use:{spell.DisplayName}");
                activeCharacter.currentMana = newManaWire;
                StartActiveSkillBusy(conn, componentId, actionId, spell, state, startsAfterSkillsChild);
                BeginFriendlySummonCast(conn, state, spell, skillLevel, componentId, actionId);
                ushort cooldownTicks = initUseCooldownStarted
                    ? Combat.SpellDatabase.ResolveCooldownTicks(spell, skillLevel, state)
                    : StartActiveSkillCooldown(conn, componentId, actionId, spell, state, skillLevel, startsAfterSkillsChild);
                conn.ActiveUseTargetInitUseCooldownStarted = false;
                conn.ActiveUseTargetInitUseCooldownComponentId = 0;
                conn.ActiveUseTargetInitUseCooldownActionId = 0;
                Debug.LogError($"[ACTIVE-SKILL-USE] tick={simulationTick} skill={spell.DisplayName ?? spell.SkillId ?? "UNKNOWN"} level={skillLevel} mana={oldManaWire}->{state.CurrentManaWire} cost={manaCostWire} cooldownTicks={cooldownTicks} cooldownStartedAt={(initUseCooldownStarted ? "initUse" : "use")} speed={Combat.SpellDatabase.ResolveActiveSkillSpeed(spell, state)} sourceFunction=ActiveSkill::use@0x00538DD0->ActiveSkill::getManaCost@0x00539D70->SkillDesc::GetCooldown@0x0053F560");
                return true;
            }
            catch (Exception ex)
            {
                QuarantineCommittedPersistenceSyncFailure(conn, "active-skill-use", ex);
                return false;
            }
        }

        private bool CommitPendingActiveSkillUse(ref PendingSpell pending, int simulationTick)
        {
            if (pending.SkillUseCommitted)
                return true;
            if (pending.AwaitingActionResponsePacket
                || pending.SkillUseTick == 0
                || simulationTick < pending.SkillUseTick)
                return false;
            if (pending.SkillUseCommitAttempted)
                return false;
            pending.SkillUseCommitAttempted = true;
            if (pending.Conn == null || pending.State == null || pending.Spell == null)
                return false;

            int skillLevel = GetPlayerSkillLevel(pending.Conn, pending.Spell);
            pending.SkillUseCommitted = CommitActiveSkillUse(
                pending.Conn,
                pending.State,
                pending.Spell,
                pending.ComponentId,
                pending.UseFlags,
                skillLevel,
                simulationTick,
                pending.StartsAfterSkillsChild);
            return pending.SkillUseCommitted;
        }

        private void ProcessPendingSpellProjectilesForPlayerEntity(uint playerEntityId, uint simulationTick)
        {
            int nowTick = (int)simulationTick;
            int pendingCount = _pendingSpells.Count;
            for (int pendingIndex = 0; pendingIndex < pendingCount; pendingIndex++)
            {
                if (!_pendingSpells.TryDequeue(out var pending))
                    break;

                uint pendingPlayerId = pending.Conn?.Avatar != null ? (uint)pending.Conn.Avatar.Id : 0;
                if (playerEntityId != 0 && pendingPlayerId != playerEntityId)
                {
                    _pendingSpells.Enqueue(pending);
                    continue;
                }
                if (pending.Spell == null)
                {
                    if (pending.ProjectileSubEntityRegistered)
                        CombatRuntime.Instance.RemoveClientSubEntity(ClientSubEntityKind.PlayerSpellProjectile, pending.Sequence, pending.InstanceKey);
                    Debug.LogError($"[SPELL-PENDING-DROP] conn={pending.Conn?.ConnId ?? 0} manip={pending.ManipId} sequence={pending.Sequence} reason=null-spell source=ProcessPendingSpellProjectilesForPlayerEntity");
                    continue;
                }

                CommitPendingActiveSkillUse(ref pending, nowTick);
                if (pending.SkillUseCommitAttempted && !pending.SkillUseCommitted)
                {
                    if (pending.ProjectileSubEntityRegistered)
                        CombatRuntime.Instance.RemoveClientSubEntity(ClientSubEntityKind.PlayerSpellProjectile, pending.Sequence, pending.InstanceKey);
                    Debug.LogError($"[SPELL-PENDING-DROP] conn={pending.Conn?.ConnId ?? 0} manip={pending.ManipId} sequence={pending.Sequence} reason=resource-use-rejected source=ProcessPendingSpellProjectilesForPlayerEntity");
                    continue;
                }
                if (!pending.ProjectileRuntimeInitialized
                    || pending.ProjectileSubEntityRegistered
                    || pending.AwaitingActionResponsePacket
                    || !pending.SkillUseCommitted
                    || pending.ActionResponseSimulationApplyTick == 0 && pending.QueuedWithoutInitialTarget
                    || nowTick < pending.FireTick)
                {
                    _pendingSpells.Enqueue(pending);
                    continue;
                }

                int actorFixedX;
                int actorFixedY;
                int actorFixedZ;
                int effectHeadingFixed;
                if (pending.WeaponProjectileRuntime)
                {
                    ResolvePlayerWeaponProjectileActorPoint(pending.Conn, out actorFixedX, out actorFixedY, out actorFixedZ);
                    effectHeadingFixed = ResolvePlayerWeaponProjectileSourceHeadingFixed(pending.Conn);
                }
                else
                {
                    ResolveSpellActorPoint(pending.Conn, out actorFixedX, out actorFixedY, out actorFixedZ);
                    effectHeadingFixed = ResolveSpellSourceHeadingFixed(pending.Conn);
                }
                int sourceFixedX;
                int sourceFixedY;
                int sourceFixedZ;
                int headingFixed = effectHeadingFixed;
                bool sourceOffsetResolved;
                int sourceOffsetFixedX;
                int sourceOffsetFixedY;
                int sourceOffsetFixedZ;
                int actionAimFixedX = pending.AimFixedX;
                int actionAimFixedY = pending.AimFixedY;
                int effectAimFixedX = CanonicalizeUsePositionEffectCoordinateFixed(actionAimFixedX);
                int effectAimFixedY = CanonicalizeUsePositionEffectCoordinateFixed(actionAimFixedY);
                SpellProjectileGeometry geometry = default;
                bool geometryResolved;
                if (pending.WeaponProjectileRuntime)
                {
                    sourceOffsetResolved = Combat.WeaponUseRuntime.Instance.TryResolvePlayerWeaponProjectileSourceFixed(
                        pending.Conn,
                        pending.State,
                        actorFixedX,
                        actorFixedY,
                        effectHeadingFixed,
                        out sourceFixedX,
                        out sourceFixedY,
                        out sourceOffsetFixedX,
                        out sourceOffsetFixedY,
                        out sourceOffsetFixedZ);
                    sourceFixedZ = actorFixedZ + sourceOffsetFixedZ;
                    if (pending.Monster != null)
                        CombatRuntime.Instance.TryGetMonsterClientVisiblePositionFixed(
                            pending.Monster,
                            pendingPlayerId,
                            out actionAimFixedX,
                            out actionAimFixedY);
                    effectAimFixedX = actionAimFixedX;
                    effectAimFixedY = actionAimFixedY;
                    int directionFixedX;
                    int directionFixedY;
                    bool directionResolved;
                    int skillLevel = GetPlayerSkillLevel(pending.Conn, pending.Spell);
                    if (pending.Spell.UsesWeaponArcHitContext(skillLevel))
                    {
                        HeadingVectorFixed(effectHeadingFixed, out directionFixedX, out directionFixedY);
                        directionResolved = directionFixedX != 0 || directionFixedY != 0;
                    }
                    else
                    {
                        directionResolved = PathMap.TryBuildNativeRayDirectionFixed(
                            effectAimFixedX - sourceFixedX,
                            effectAimFixedY - sourceFixedY,
                            out directionFixedX,
                            out directionFixedY,
                            out _);
                    }
                    pending.StartFixedX = sourceFixedX + MultiplySpellFixed8(directionFixedX, pending.InitialDistanceF32);
                    pending.StartFixedY = sourceFixedY + MultiplySpellFixed8(directionFixedY, pending.InitialDistanceF32);
                    pending.AimFixedX = effectAimFixedX;
                    pending.AimFixedY = effectAimFixedY;
                    pending.CurrentFixedX = pending.StartFixedX;
                    pending.CurrentFixedY = pending.StartFixedY;
                    pending.CurrentFixedZ = sourceFixedZ;
                    pending.DirectionFixedX = directionFixedX;
                    pending.DirectionFixedY = directionFixedY;
                    pending.VelocityFixedX = directionResolved ? MultiplySpellFixed8(directionFixedX, pending.StepDistanceF32) : 0;
                    pending.VelocityFixedY = directionResolved ? MultiplySpellFixed8(directionFixedY, pending.StepDistanceF32) : 0;
                    pending.VelocityFixedZ = 0;
                    PathMap weaponProjectilePathMap = ResolveUseTargetMovingPathMap(pending.Conn);
                    pending.GroundOffsetFixedZ = weaponProjectilePathMap != null
                        && weaponProjectilePathMap.TryGetHeightAtFixed(pending.CurrentFixedX, pending.CurrentFixedY, out int weaponProjectileGroundFixedZ)
                            ? pending.CurrentFixedZ - weaponProjectileGroundFixedZ
                            : sourceOffsetFixedZ;
                    pending.ProjectileGeometryInitialized = directionResolved;
                    geometryResolved = directionResolved;
                }
                else
                {
                    ResolveSpellSourcePoint(
                        actorFixedX,
                        actorFixedY,
                        actorFixedZ,
                        effectHeadingFixed,
                        pending.Spell,
                        pending.State,
                        out sourceFixedX,
                        out sourceFixedY,
                        out sourceFixedZ,
                        out headingFixed,
                        out sourceOffsetResolved,
                        out sourceOffsetFixedX,
                        out sourceOffsetFixedY,
                        out sourceOffsetFixedZ);
                    geometryResolved = TryResolveSpellProjectileGeometryFixed(
                        pending.Conn,
                        pending.InstanceKey,
                        pending.Spell,
                        actorFixedX,
                        actorFixedY,
                        sourceFixedX,
                        sourceFixedY,
                        effectAimFixedX,
                        effectAimFixedY,
                        pending.StepDistanceF32,
                        out geometry);
                }
                if (geometryResolved)
                {
                    if (!pending.WeaponProjectileRuntime)
                    {
                        pending.StartFixedX = geometry.ProjectileSourceFixedX;
                        pending.StartFixedY = geometry.ProjectileSourceFixedY;
                        pending.AimFixedX = geometry.BurstTargetFixedX;
                        pending.AimFixedY = geometry.BurstTargetFixedY;
                        pending.CurrentFixedX = geometry.ProjectileSourceFixedX;
                        pending.CurrentFixedY = geometry.ProjectileSourceFixedY;
                        pending.CurrentFixedZ = sourceFixedZ;
                        pending.DirectionFixedX = geometry.ProjectileDirectionFixedX;
                        pending.DirectionFixedY = geometry.ProjectileDirectionFixedY;
                        pending.VelocityFixedX = geometry.ProjectileVelocityFixedX;
                        pending.VelocityFixedY = geometry.ProjectileVelocityFixedY;
                        pending.VelocityFixedZ = 0;
                        PathMap projectilePathMap = ResolveUseTargetMovingPathMap(pending.Conn);
                        pending.GroundOffsetFixedZ = projectilePathMap != null
                            && projectilePathMap.TryGetHeightAtFixed(pending.CurrentFixedX, pending.CurrentFixedY, out int projectileGroundFixedZ)
                                ? pending.CurrentFixedZ - projectileGroundFixedZ
                                : 12 * UnitMover.Fixed;
                        pending.ProjectileGeometryInitialized = true;
                    }
                }
                else
                {
                    pending.StartFixedX = sourceFixedX;
                    pending.StartFixedY = sourceFixedY;
                }
                pending.SubEntityOrder = CombatRuntime.Instance.RegisterClientSubEntity(ClientSubEntityKind.PlayerSpellProjectile, pending.Sequence, pending.InstanceKey, RemovePendingSpellRuntime);
                pending.ProjectileSubEntityRegistered = true;
                pending.LastUpdateTick = nowTick;
                Debug.LogError($"[SPELL-PROJECTILE] subentity init spell={pending.Spell?.DisplayName ?? pending.Spell?.SkillId ?? "spell"} seq={pending.Sequence} receivedTick={pending.ActionReceiveTick} packetWriterTick={pending.ActionResponseWriterTick} simulationApplyTick={pending.ActionResponseSimulationApplyTick} fireTick={pending.FireTick} firstUpdateTick={nowTick + 1} tick={nowTick} collisionActorFixed=({actorFixedX},{actorFixedY},{actorFixedZ}) effectSourceFixed=({sourceFixedX},{sourceFixedY},{sourceFixedZ}) actionAimFixed=({actionAimFixedX},{actionAimFixedY},{pending.ActionAimFixedZ}) effectAimFixed=({effectAimFixedX},{effectAimFixedY},{pending.ActionAimFixedZ}) burstHeadingFixed={geometry.BurstHeadingFixed} burstDirectionFixed=({geometry.BurstDirectionFixedX},{geometry.BurstDirectionFixedY}) burstSourceRawFixed=({geometry.BurstSourceRawFixedX},{geometry.BurstSourceRawFixedY}) burstSourceFixed=({geometry.BurstSourceFixedX},{geometry.BurstSourceFixedY}) burstTargetFixed=({geometry.BurstTargetFixedX},{geometry.BurstTargetFixedY}) projectileSourceRawFixed=({geometry.ProjectileSourceRawFixedX},{geometry.ProjectileSourceRawFixedY}) projectileSourceFixed=({pending.StartFixedX},{pending.StartFixedY},{sourceFixedZ}) projectileDirectionFixed=({pending.DirectionFixedX},{pending.DirectionFixedY}) projectileVelocityFixed=({pending.VelocityFixedX},{pending.VelocityFixedY}) burstRayDistanceFixed={geometry.BurstRayDistanceFixed} projectileRayDistanceFixed={geometry.ProjectileRayDistanceFixed} projectileOffsetFixed={pending.ProjectileOffsetF32} burstOffsetFixed={pending.Spell?.BurstOffsetF32 ?? 0} burstDistanceFixed={pending.Spell?.BurstDistanceF32 ?? 0} geometryResolved={geometryResolved} headingFixed={headingFixed} sourceOffsetFixed=({sourceOffsetFixedX},{sourceOffsetFixedY},{sourceOffsetFixedZ}) sourceOffsetResolved={sourceOffsetResolved} avatar={pending.State?.AvatarGcType ?? ""} phase=player-pre-follow-client sourceFunction=ActiveSkill::update@0x005392F0->ActiveSkill::doSkillEffect@0x00539630->SpellBurstEffect::doBurst@0x0054A4F0->SpellProjectile::init@0x00554CF0->EntityManager::addSubEntity");
                _pendingSpells.Enqueue(pending);
            }
        }

        private static int CanonicalizeUsePositionEffectCoordinateFixed(int value)
        {
            return (value >> 8) << 8;
        }

        private static bool TryResolveSpellProjectileGeometryFixed(
            RRConnection conn,
            string instanceKey,
            Combat.SpellData spell,
            int actorFixedX,
            int actorFixedY,
            int sourceFixedX,
            int sourceFixedY,
            int aimFixedX,
            int aimFixedY,
            int stepDistanceF32,
            out SpellProjectileGeometry geometry)
        {
            geometry = default;
            if (conn == null
                || spell == null
                || !spell.HasBurstEffect
                || spell.BurstCountMinF32 != UnitMover.Fixed
                || spell.BurstCountMaxF32 != UnitMover.Fixed)
                return false;

            geometry.BurstHeadingFixed = UnitMover.VectorToHeadingFixed(aimFixedX - sourceFixedX, aimFixedY - sourceFixedY);
            HeadingVectorFixed(unchecked((uint)geometry.BurstHeadingFixed), out int headingVectorFixedX, out int headingVectorFixedY);
            if (!PathMap.TryBuildNativeRayDirectionFixed(
                    headingVectorFixedX,
                    headingVectorFixedY,
                    out geometry.BurstDirectionFixedX,
                    out geometry.BurstDirectionFixedY,
                    out _))
                return false;

            geometry.BurstSourceRawFixedX = sourceFixedX + MultiplySpellFixed8(geometry.BurstDirectionFixedX, spell.BurstOffsetF32);
            geometry.BurstSourceRawFixedY = sourceFixedY + MultiplySpellFixed8(geometry.BurstDirectionFixedY, spell.BurstOffsetF32);
            if (!TryProjectSpellGroundRayEndpointFixed(
                    conn,
                    instanceKey,
                    actorFixedX,
                    actorFixedY,
                    geometry.BurstSourceRawFixedX,
                    geometry.BurstSourceRawFixedY,
                    out geometry.BurstSourceFixedX,
                    out geometry.BurstSourceFixedY,
                    out geometry.BurstRayDistanceFixed))
                return false;

            geometry.BurstTargetFixedX = aimFixedX + MultiplySpellFixed8(geometry.BurstDirectionFixedX, spell.BurstDistanceF32);
            geometry.BurstTargetFixedY = aimFixedY + MultiplySpellFixed8(geometry.BurstDirectionFixedY, spell.BurstDistanceF32);
            if (!PathMap.TryBuildNativeRayDirectionFixed(
                    geometry.BurstTargetFixedX - geometry.BurstSourceFixedX,
                    geometry.BurstTargetFixedY - geometry.BurstSourceFixedY,
                    out geometry.ProjectileDirectionFixedX,
                    out geometry.ProjectileDirectionFixedY,
                    out _))
                return false;

            geometry.ProjectileSourceRawFixedX = geometry.BurstSourceFixedX + MultiplySpellFixed8(geometry.ProjectileDirectionFixedX, spell.ProjectileOffsetF32);
            geometry.ProjectileSourceRawFixedY = geometry.BurstSourceFixedY + MultiplySpellFixed8(geometry.ProjectileDirectionFixedY, spell.ProjectileOffsetF32);
            if (!TryProjectSpellGroundRayEndpointFixed(
                    conn,
                    instanceKey,
                    actorFixedX,
                    actorFixedY,
                    geometry.ProjectileSourceRawFixedX,
                    geometry.ProjectileSourceRawFixedY,
                    out geometry.ProjectileSourceFixedX,
                    out geometry.ProjectileSourceFixedY,
                    out geometry.ProjectileRayDistanceFixed))
                return false;

            geometry.ProjectileVelocityFixedX = MultiplySpellFixed8(geometry.ProjectileDirectionFixedX, stepDistanceF32);
            geometry.ProjectileVelocityFixedY = MultiplySpellFixed8(geometry.ProjectileDirectionFixedY, stepDistanceF32);
            return geometry.ProjectileVelocityFixedX != 0 || geometry.ProjectileVelocityFixedY != 0;
        }

        private static bool TryProjectSpellGroundRayEndpointFixed(
            RRConnection conn,
            string instanceKey,
            int actorFixedX,
            int actorFixedY,
            int rawEndFixedX,
            int rawEndFixedY,
            out int projectedFixedX,
            out int projectedFixedY,
            out int requestedDistanceFixed)
        {
            projectedFixedX = rawEndFixedX;
            projectedFixedY = rawEndFixedY;
            requestedDistanceFixed = 0;
            PathMap pathMap = !string.IsNullOrWhiteSpace(instanceKey)
                ? PathMapCatalog.Instance.GetPathMap(instanceKey)
                : null;
            if (pathMap == null && !string.IsNullOrWhiteSpace(conn?.CurrentZoneName))
                pathMap = PathMapCatalog.Instance.GetPathMap(conn.CurrentZoneName);
            if (pathMap == null
                || !PathMap.TryBuildNativeRayDirectionFixed(
                    rawEndFixedX - actorFixedX,
                    rawEndFixedY - actorFixedY,
                    out int directionFixedX,
                    out int directionFixedY,
                    out requestedDistanceFixed))
                return false;

            int actualDistanceFixed = pathMap.CastGroundRayDistanceFixed(
                actorFixedX,
                actorFixedY,
                directionFixedX,
                directionFixedY,
                requestedDistanceFixed);
            if (actualDistanceFixed <= 0)
                return false;

            projectedFixedX = actorFixedX + MultiplySpellFixed8(directionFixedX, actualDistanceFixed);
            projectedFixedY = actorFixedY + MultiplySpellFixed8(directionFixedY, actualDistanceFixed);
            return true;
        }

        private static int MultiplySpellFixed8(int valueFixed, int factorFixed)
        {
            return (int)(((long)valueFixed * factorFixed) >> 8);
        }

        private void ProcessPendingSpellsForPlayerEntity(uint playerEntityId, uint simulationTick)
        {
            int nowTick = (int)simulationTick;
            int pendingCount = _pendingSpells.Count;
            for (int pendingIndex = 0; pendingIndex < pendingCount; pendingIndex++)
            {
                if (!_pendingSpells.TryDequeue(out var pending))
                    break;

                uint pendingPlayerId = pending.Conn?.Avatar != null ? (uint)pending.Conn.Avatar.Id : 0;
                if (playerEntityId != 0 && pendingPlayerId != playerEntityId)
                {
                    _pendingSpells.Enqueue(pending);
                    continue;
                }
                if (pending.Spell == null)
                {
                    Debug.LogError($"[SPELL-PENDING-DROP] conn={pending.Conn?.ConnId ?? 0} manip={pending.ManipId} sequence={pending.Sequence} reason=null-spell source=ProcessPendingSpellsForPlayerEntity");
                    continue;
                }

                CommitPendingActiveSkillUse(ref pending, nowTick);
                if (pending.SkillUseCommitAttempted && !pending.SkillUseCommitted)
                {
                    if (pending.ProjectileSubEntityRegistered)
                        CombatRuntime.Instance.RemoveClientSubEntity(ClientSubEntityKind.PlayerSpellProjectile, pending.Sequence, pending.InstanceKey);
                    Debug.LogError($"[SPELL-PENDING-DROP] conn={pending.Conn?.ConnId ?? 0} manip={pending.ManipId} sequence={pending.Sequence} reason=resource-use-rejected source=ProcessPendingSpellsForPlayerEntity");
                    continue;
                }
                if (pending.ProjectileRuntimeInitialized)
                {
                    _pendingSpells.Enqueue(pending);
                    continue;
                }

                if (pending.AwaitingActionResponsePacket)
                {
                    _pendingSpells.Enqueue(pending);
                    continue;
                }

                if (!pending.SkillUseCommitted)
                {
                    _pendingSpells.Enqueue(pending);
                    continue;
                }

                if (!IsPendingSpellDue(pending, nowTick))
                {
                    _pendingSpells.Enqueue(pending);
                    continue;
                }

                if (pending.Spell.HasTeleportEffect)
                {
                    ApplySpellTeleportEffect(pending, simulationTick);
                    continue;
                }

                Combat.Monster target = ResolvePendingSpellTarget(ref pending, nowTick, "ProcessPendingSpells");
                if (target != null && target.IsAlive)
                    HandleSpellAttack(pending.Conn, pending.State, target, pending.ManipId, pending.UseFlags, null, false, pending.SkillUseCommitted);
                else if (pending.QueuedWithoutInitialTarget)
                    Debug.LogError($"[SPELL-PROJECTILE] no-hit spell={pending.Spell?.DisplayName ?? pending.Spell?.SkillId ?? "UNKNOWN"} manip={pending.ManipId} aimFixed=({pending.AimFixedX},{pending.AimFixedY}) dueTick={pending.DueTick} nowTick={nowTick} source=ProcessPendingSpells");
            }
        }

        private void ProcessPendingSpellSubEntity(long sequence, string instanceKey, uint simulationTick)
        {
            int nowTick = (int)simulationTick;
            int pendingCount = _pendingSpells.Count;
            bool found = false;
            bool remains = false;
            for (int pendingIndex = 0; pendingIndex < pendingCount; pendingIndex++)
            {
                if (!_pendingSpells.TryDequeue(out var pending)) break;
                if (pending.Sequence != sequence
                    || !pending.ProjectileRuntimeInitialized
                    || !pending.ProjectileSubEntityRegistered)
                {
                    _pendingSpells.Enqueue(pending);
                    continue;
                }

                found = true;
                if (!UpdatePendingSpellProjectile(ref pending, nowTick, "ClientEntityManager.updateEntities-subentity"))
                {
                    _pendingSpells.Enqueue(pending);
                    remains = true;
                }
            }

            if (!found || !remains)
                CombatRuntime.Instance.RemoveClientSubEntity(ClientSubEntityKind.PlayerSpellProjectile, sequence, instanceKey);
        }

        private void RemovePendingSpellRuntime(long sequence)
        {
            int pendingCount = _pendingSpells.Count;
            for (int pendingIndex = 0; pendingIndex < pendingCount; pendingIndex++)
            {
                if (!_pendingSpells.TryDequeue(out PendingSpell pending))
                    break;
                if (pending.Sequence == sequence)
                    continue;
                _pendingSpells.Enqueue(pending);
            }
        }

        private void ApplySpellTeleportEffect(PendingSpell pending, uint simulationTick)
        {
            RRConnection conn = pending.Conn;
            if (conn == null
                || conn.Avatar == null
                || pending.State == null
                || !IsPendingConnectionInstanceCurrent(conn, pending.InstanceKey)
                || !_playerStates.TryGetValue(conn.ConnId.ToString(), out PlayerState currentState)
                || !ReferenceEquals(currentState, pending.State))
                return;

            PathMap pathMap = ResolveUseTargetMovingPathMap(conn);
            if (!TryResolveSpellTeleportGroundHeightFixed(
                    conn,
                    pathMap,
                    pending.AimFixedX,
                    pending.AimFixedY,
                    out int targetFixedZ))
            {
                Debug.LogError($"[SPELL-TELEPORT] state=no-ground player={conn.LoginName} spell={pending.Spell.SkillId} targetFixed=({pending.AimFixedX},{pending.AimFixedY},{pending.ActionAimFixedZ}) tick={simulationTick} sourceFunction=SpellTeleportEffect::doEffect@0x0055C5B0 WorldCollisionManager::getHeight@0x004E9940");
                return;
            }

            int targetFixedX = pending.AimFixedX;
            int targetFixedY = pending.AimFixedY;
            int headingFixed = conn.PlayerHeadingFixed;
            conn.PlayerPosFixedX = targetFixedX;
            conn.PlayerPosFixedY = targetFixedY;
            conn.PlayerPosFixedZ = targetFixedZ;
            conn.HasLivePlayerPosition = true;
            conn.LivePlayerPosFixedX = targetFixedX;
            conn.LivePlayerPosFixedY = targetFixedY;
            conn.LivePlayerPosFixedZ = targetFixedZ;
            conn.LivePlayerHeadingFixed = headingFixed;
            conn.LivePlayerMovingThisFrame = false;
            conn.UsePositionActionStoppedFollowClient = true;
            conn.ReflectedAvatarMovingThisFrame = false;
            conn.OwnerAckFollowClientMovingThisFrame = false;
            conn.UnitFollowClientMovingThisFrame = false;
            conn.AvatarAggroSampleQueue.Clear();
            conn.AggroSamplePosFixedX = targetFixedX;
            conn.AggroSamplePosFixedY = targetFixedY;
            conn.LastRawMoveCount = 0;
            conn.LastRawMoveData = null;
            CombatRuntime.Instance.UpdatePlayerPositionFixed((uint)conn.Avatar.Id, targetFixedX, targetFixedY, targetFixedZ);
            StopFollowingClientAtCurrentPosition(
                conn,
                targetFixedX,
                targetFixedY,
                targetFixedZ,
                headingFixed,
                "SpellTeleportEffect::doEffect@0x0055C5B0->Unit::setPosition@0x00508DF0");
            MarkPeerUsePositionStoppedFollowingClient(conn, simulationTick);
            SyncReflectedAvatarCombatPosition(conn);
            Debug.LogError($"[SPELL-TELEPORT] state=applied player={conn.LoginName} entity={conn.Avatar.Id} spell={pending.Spell.SkillId} targetFixed=({targetFixedX},{targetFixedY},{targetFixedZ}) headingFixed={headingFixed} tick={simulationTick} sourceFunction=ActiveSkill::doSkillEffect@0x00539630 SpellSnapToGroundEffect::doEffect@0x0055AE40 SpellTeleportEffect::doEffect@0x0055C5B0 Unit::setPosition@0x00508DF0 UnitBehavior::OnSetPosition@0x005201A0");
        }

        private void ProcessPendingChainSpellSubEntity(long sequence, string instanceKey, uint simulationTick)
        {
            int pendingCount = _pendingChainSpells.Count;
            bool remains = false;
            bool found = false;
            for (int pendingIndex = 0; pendingIndex < pendingCount; pendingIndex++)
            {
                if (!_pendingChainSpells.TryDequeue(out PendingChainSpell pending))
                    break;
                if (pending == null)
                    continue;
                if (pending.Sequence != sequence)
                {
                    _pendingChainSpells.Enqueue(pending);
                    continue;
                }

                found = true;
                if (simulationTick < pending.DueTick)
                {
                    _pendingChainSpells.Enqueue(pending);
                    remains = true;
                    continue;
                }

                if (pending.WaitingForExpiry)
                    continue;

                if (!TryResolveNextChainSpellTarget(
                        pending,
                        out Combat.Monster target,
                        out int targetFixedX,
                        out int targetFixedY,
                        out int targetFixedZ))
                {
                    if (simulationTick < pending.ActiveBranchExpiryTick)
                    {
                        pending.WaitingForExpiry = true;
                        pending.DueTick = pending.ActiveBranchExpiryTick;
                        _pendingChainSpells.Enqueue(pending);
                        remains = true;
                    }
                    continue;
                }

                ApplySpellDamageToMonster(
                    pending.Conn,
                    pending.State,
                    pending.Spell,
                    target,
                    pending.Rng,
                    pending.SkillLevel,
                    true,
                    "SPELL-CHAIN");

                pending.Hit.Add(target.EntityId);
                pending.Center = target;
                pending.LastFixedX = targetFixedX;
                pending.LastFixedY = targetFixedY;
                pending.LastFixedZ = targetFixedZ;
                pending.RemainingChains--;
                pending.ActiveBranchExpiryTick = unchecked((int)simulationTick) + Math.Max(1, pending.Spell.ChainLifespanTicks);
                if (pending.RemainingChains > 0)
                {
                    pending.DueTick = unchecked((int)simulationTick) + Math.Max(1, pending.Spell.ChainDelayTicks);
                    _pendingChainSpells.Enqueue(pending);
                    remains = true;
                    Debug.LogError($"[SPELL-CHAIN] state=advance seq={pending.Sequence} center={target.EntityId} dueTick={pending.DueTick} expiryTick={pending.ActiveBranchExpiryTick} remaining={pending.RemainingChains} sourceFunction=SpellChainProjectile::update@0x0054C7E0");
                }
                else
                {
                    pending.WaitingForExpiry = true;
                    pending.DueTick = pending.ActiveBranchExpiryTick;
                    _pendingChainSpells.Enqueue(pending);
                    remains = true;
                }
            }

            if (!found || !remains)
                CombatRuntime.Instance.RemoveClientSubEntity(ClientSubEntityKind.PlayerSpellChainProjectile, sequence, instanceKey);
        }

        private void RemovePendingChainSpellRuntime(long sequence)
        {
            int pendingCount = _pendingChainSpells.Count;
            for (int pendingIndex = 0; pendingIndex < pendingCount; pendingIndex++)
            {
                if (!_pendingChainSpells.TryDequeue(out PendingChainSpell pending))
                    break;
                if (pending == null || pending.Sequence == sequence)
                    continue;
                _pendingChainSpells.Enqueue(pending);
            }
        }

        private void CancelPendingCombatForConnection(RRConnection conn, bool preserveAttributeModifiers = false)
        {
            if (conn == null) return;
            TryClearUseTargetAndReleaseControl(conn, "combat-lifecycle-cancel");
            ClearActiveSkillBusyRuntimeForConnection(conn);
            uint avatarEntityId = conn.Avatar != null
                ? (uint)Math.Max(0, conn.Avatar.Id)
                : _playerAvatarEntityId.TryGetValue(conn.ConnId.ToString(), out uint mappedAvatarEntityId)
                    ? mappedAvatarEntityId
                    : 0u;
            _unitContainer?.ClearConnectionRuntime(conn, "CancelPendingCombatForConnection", preserveAttributeModifiers);
            _equipment?.ClearPendingAdmissions(conn);
            ClearPendingUseTargetBehaviorAction(conn);
            _pendingGroupGotoWarpInputs.RemoveAll(input => input.ConnId == conn.ConnId);
            _pendingPlayerUseTargetActionInputs.RemoveAll(input => input.ConnId == conn.ConnId);
            _pendingPlayerUsePositionWeaponActionInputs.RemoveAll(input => input.ConnId == conn.ConnId);
            _pendingPlayerUsePositionBehaviorActions.RemoveAll(input => input.ConnId == conn.ConnId);
            _pendingOwnerClientControlInputs.RemoveAll(input => input.ConnId == conn.ConnId);
            _pendingPlayerCancelActionInputs.RemoveAll(input => input.ConnId == conn.ConnId);
            lock (_pendingHotbarPassiveAdmissionLock)
                _pendingHotbarPassiveAdmissions.RemoveAll(admission => admission.ConnId == conn.ConnId);
            _pendingMonsterBehaviorUpdates.RemoveAll(update =>
                update.ConnId == conn.ConnId
                || (avatarEntityId != 0 && update.TargetEntityId == avatarEntityId));
            conn.UsePositionActionMirrored = false;
            conn.UsePositionActionStoppedFollowClient = false;
            conn.UsePositionActionComponentId = 0;
            conn.UsePositionManipulatorId = 0;
            conn.UsePositionActionSessionId = 0;
            conn.UsePositionActionTargetFixedX = 0;
            conn.UsePositionActionTargetFixedY = 0;
            conn.UsePositionActionTargetFixedZ = 0;
            conn.UsePositionActionApplyTick = 0;
            conn.UsePositionActionBusyUntilTick = 0;
            conn.UsePositionWeaponAction = false;
            conn.UsePositionActionAdmissionFollowClientRecords = 0;
            int pendingCount = _pendingSpells.Count;
            for (int pendingIndex = 0; pendingIndex < pendingCount; pendingIndex++)
            {
                if (!_pendingSpells.TryDequeue(out PendingSpell pending))
                    break;
                if (pending.Conn?.ConnId == conn.ConnId)
                {
                    if (pending.ProjectileSubEntityRegistered)
                        CombatRuntime.Instance.RemoveClientSubEntity(ClientSubEntityKind.PlayerSpellProjectile, pending.Sequence, pending.InstanceKey);
                    continue;
                }
                _pendingSpells.Enqueue(pending);
            }

            int pendingChainCount = _pendingChainSpells.Count;
            for (int pendingIndex = 0; pendingIndex < pendingChainCount; pendingIndex++)
            {
                if (!_pendingChainSpells.TryDequeue(out PendingChainSpell pending))
                    break;
                if (pending == null)
                    continue;
                if (pending.Conn?.ConnId == conn.ConnId)
                {
                    CombatRuntime.Instance.RemoveClientSubEntity(ClientSubEntityKind.PlayerSpellChainProjectile, pending.Sequence, pending.InstanceKey);
                    continue;
                }
                _pendingChainSpells.Enqueue(pending);
            }

            lock (_pendingAoECastLock)
                _pendingAoECasts.RemoveAll(pending => pending.Conn?.ConnId == conn.ConnId);
            lock (_pendingSelfCastActionLock)
                _pendingSelfCastActions.RemoveAll(pending => pending.Conn?.ConnId == conn.ConnId);
            DiscardPendingSelfCastBehaviorAction(conn);
            lock (_pendingSelfSpellEffectLock)
                _pendingSelfSpellEffects.RemoveAll(pending => pending.Conn?.ConnId == conn.ConnId);
        }

        private PendingSpell CreatePendingSpellProjectileFixed(
            RRConnection conn,
            PlayerState state,
            Combat.Monster monster,
            Combat.SpellData spell,
            byte manipId,
            byte useFlags,
            ushort componentId,
            int startFixedX,
            int startFixedY,
            int aimFixedX,
            int aimFixedY,
            bool queuedWithoutInitialTarget,
            int hitDistanceHintF32,
            bool awaitActionResponsePacket,
            bool startsAfterSkillsChild,
            uint skillUseTick,
            byte actionResponseId,
            byte actionResponseSessionId,
            int actionAimFixedZ)
        {
            int aimDxFixed = aimFixedX - startFixedX;
            int aimDyFixed = aimFixedY - startFixedY;
            int aimDistanceF32 = UnitMover.IntSqrt((long)aimDxFixed * aimDxFixed + (long)aimDyFixed * aimDyFixed);
            if (aimDistanceF32 <= 0)
                aimDistanceF32 = Math.Max(1, hitDistanceHintF32);
            bool weaponProjectileRuntime = spell != null
                && spell.HasImmediateWeaponDamageEffect
                && state != null
                && state.WeaponUsesProjectile
                && state.WeaponProjectileSpeedF32 > 0
                && state.WeaponProjectileSizeF32 > 0;
            int speedF32 = weaponProjectileRuntime
                ? state.WeaponProjectileSpeedF32
                : spell != null ? spell.ProjectileSpeedF32 : 0;
            if (speedF32 > 0 && speedF32 < 0x100)
                speedF32 = 0x100;
            int sizeF32 = weaponProjectileRuntime
                ? Math.Max(0, state.WeaponProjectileSizeF32)
                : spell != null ? Math.Max(0, spell.ProjectileSizeF32) : 0;
            int maxDistanceF32 = weaponProjectileRuntime
                ? CombatRuntime.Instance.ResolvePlayerRangedProjectileRangeF32(state, monster)
                : spell != null && sizeF32 > 0 && speedF32 > 0
                    ? ResolveProjectileMaxDistanceF32(spell, aimDistanceF32)
                : Math.Max(0, hitDistanceHintF32);
            if (maxDistanceF32 <= 0)
                maxDistanceF32 = Math.Max(1, aimDistanceF32);

            bool projectileRuntimeInitialized = spell != null && sizeF32 > 0 && speedF32 > 0;
            int fireTick = awaitActionResponsePacket
                ? 0
                : startsAfterSkillsChild
                    ? ResolveActiveSkillEffectTickAfterSkillsChild(skillUseTick, spell, state)
                    : ResolveActiveSkillEffectTickBeforeSkillsChild(skillUseTick, spell, state);
            int maxLifetimeTicks = spell != null && sizeF32 > 0 && speedF32 > 0
                ? Combat.WeaponUseRuntime.ProjectileFlightTicksFixed32(maxDistanceF32, speedF32)
                : 1;
            int stepDistanceF32 = spell != null && sizeF32 > 0 && speedF32 > 0
                ? Combat.WeaponUseRuntime.ProjectileStepDistanceF32(speedF32)
                : 0;
            int offsetF32 = weaponProjectileRuntime
                ? Combat.WeaponUseRuntime.ProjectileInitialDistanceF32(CombatRuntime.Instance.ResolveAvatarUnitBehaviorRadiusF32())
                : spell != null ? Math.Max(0, spell.ProjectileOffsetF32) : 0;
            int initialDistanceF32 = spell != null && sizeF32 > 0 && speedF32 > 0
                ? Math.Min(maxDistanceF32, offsetF32)
                : 0;
            hitDistanceHintF32 = Math.Max(0, hitDistanceHintF32);
            int resolvedHitDistanceF32 = hitDistanceHintF32 > 0
                ? Math.Min(hitDistanceHintF32, maxDistanceF32)
                : maxDistanceF32;
            int projectileDelayTicks = spell != null && sizeF32 > 0 && speedF32 > 0
                ? Combat.WeaponUseRuntime.ProjectileImpactDelayTicksFixed32(resolvedHitDistanceF32, speedF32)
                : ResolveProjectileImpactDelayTicksFixed32(spell, state, resolvedHitDistanceF32);
            int dueTick = awaitActionResponsePacket ? 0 : fireTick + projectileDelayTicks;

            var pending = new PendingSpell
            {
                Sequence = ++_nextPendingSpellProjectileSequence,
                Conn = conn,
                State = state,
                Monster = monster,
                Spell = spell,
                ManipId = manipId,
                UseFlags = useFlags,
                ComponentId = componentId,
                StartFixedX = startFixedX,
                StartFixedY = startFixedY,
                AimFixedX = aimFixedX,
                AimFixedY = aimFixedY,
                InstanceKey = conn != null ? GetInstanceZoneKey(conn) : null,
                DueTick = dueTick,
                ProjectileHitDistanceF32 = resolvedHitDistanceF32,
                ProjectileDelayTicks = projectileDelayTicks,
                QueuedWithoutInitialTarget = queuedWithoutInitialTarget,
                ProjectileRuntimeInitialized = projectileRuntimeInitialized,
                ProjectileSubEntityRegistered = projectileRuntimeInitialized && !awaitActionResponsePacket && !weaponProjectileRuntime,
                AwaitingActionResponsePacket = projectileRuntimeInitialized && awaitActionResponsePacket,
                ActionResponseId = actionResponseId,
                ActionResponseSessionId = actionResponseSessionId,
                ActionAimFixedZ = actionAimFixedZ,
                ActionReceiveTick = _combatTick,
                SkillUseTick = skillUseTick,
                StartsAfterSkillsChild = startsAfterSkillsChild,
                FireTick = fireTick,
                LastUpdateTick = fireTick,
                UpdatesCompleted = 0,
                MaxLifetimeTicks = maxLifetimeTicks,
                ProjectileSpeedF32 = speedF32,
                ProjectileSizeF32 = sizeF32,
                ProjectileOffsetF32 = offsetF32,
                StepDistanceF32 = stepDistanceF32,
                InitialDistanceF32 = initialDistanceF32,
                CurrentDistanceF32 = initialDistanceF32,
                MaxDistanceF32 = maxDistanceF32,
                WeaponProjectileRuntime = weaponProjectileRuntime
            };
            if (pending.ProjectileSubEntityRegistered)
                pending.SubEntityOrder = CombatRuntime.Instance.RegisterClientSubEntity(ClientSubEntityKind.PlayerSpellProjectile, pending.Sequence, pending.InstanceKey, RemovePendingSpellRuntime);
            return pending;
        }

        private bool UpdatePendingSpellProjectile(ref PendingSpell pending, int nowTick, string source)
        {
            Combat.SpellData spell = pending.Spell;
            if (spell == null)
            {
                Combat.SpellDatabase.Initialize();
                spell = ResolveSpellFromManip(pending.Conn, pending.ManipId);
                pending.Spell = spell;
            }

            if (spell == null || pending.ProjectileSpeedF32 <= 0 || pending.ProjectileSizeF32 <= 0)
                return IsPendingSpellDue(pending, nowTick);

            if (nowTick <= pending.LastUpdateTick)
                return false;

            int updateLimitTick = Math.Min(nowTick, pending.LastUpdateTick + 1);
            for (int updateTick = pending.LastUpdateTick + 1; updateTick <= updateLimitTick; updateTick++)
            {
                if (pending.UpdatesCompleted >= pending.MaxLifetimeTicks)
                {
                    Debug.LogError($"[SPELL-PROJECTILE] expired no-hit spell={spell.DisplayName ?? spell.SkillId ?? "spell"} seq={pending.Sequence} manip={pending.ManipId} aimFixed=({pending.AimFixedX},{pending.AimFixedY}) currentF32={pending.CurrentDistanceF32}/{pending.MaxDistanceF32} updates={pending.UpdatesCompleted}/{pending.MaxLifetimeTicks} tick={updateTick} source={source ?? "unknown"} sourceFunction=Projectile::update lifetime-zero-before-unit-check");
                    return true;
                }

                int beforeDistanceF32 = Math.Max(0, pending.CurrentDistanceF32);
                int stepDistanceF32 = pending.StepDistanceF32 > 0
                    ? pending.StepDistanceF32
                    : Combat.WeaponUseRuntime.ProjectileStepDistanceF32(pending.ProjectileSpeedF32);
                int maxDistanceF32 = pending.MaxDistanceF32 > 0
                    ? pending.MaxDistanceF32
                    : 1;
                int afterDistanceF32 = Math.Min(maxDistanceF32, beforeDistanceF32 + Math.Max(1, stepDistanceF32));
                pending.UpdatesCompleted++;
                pending.LastUpdateTick = updateTick;

                if (TryResolvePendingSpellTargetAlongSegment(ref pending, beforeDistanceF32, afterDistanceF32, out Combat.Monster target, out int hitDistanceF32, out bool worldBlocked))
                {
                    if (worldBlocked && target == null)
                    {
                        pending.ProjectileHitDistanceF32 = Math.Max(0, hitDistanceF32);
                        pending.ProjectileDelayTicks = Math.Max(0, updateTick - pending.FireTick);
                        pending.DueTick = updateTick;
                        pending.CurrentDistanceF32 = pending.ProjectileHitDistanceF32;
                        Debug.LogError($"[SPELL-PROJECTILE] world impact spell={spell.DisplayName ?? spell.SkillId ?? "spell"} seq={pending.Sequence} segmentF32={beforeDistanceF32}->{afterDistanceF32} hitDistF32={hitDistanceF32} tick={updateTick} updates={pending.UpdatesCompleted}/{pending.MaxLifetimeTicks} source={source ?? "unknown"} sourceFunction=SpellProjectile::update@0x00555790->ProjectileChecker::testFirstTime@0x0059A490");
                        return true;
                    }
                    pending.Monster = target;
                    pending.ProjectileHitDistanceF32 = Math.Max(0, hitDistanceF32);
                    pending.ProjectileDelayTicks = Math.Max(0, updateTick - pending.FireTick);
                    pending.DueTick = updateTick;
                    pending.CurrentDistanceF32 = pending.ProjectileHitDistanceF32;
                    int targetRadiusF32 = CombatRuntime.ResolveProjectileUnitCollisionRadiusF32(target);
                    int hitRadiusF32 = WeaponUseRuntime.ProjectileCollisionRadiusF32(targetRadiusF32, pending.ProjectileSizeF32);
                    int projectileRadiusF32 = WeaponUseRuntime.ProjectileRadiusFromAuthoredSizeF32(pending.ProjectileSizeF32);
                    Debug.LogError($"[SPELL-PROJECTILE] subentity impact spell={spell.DisplayName ?? spell.SkillId ?? "spell"} seq={pending.Sequence} target={target.Name}#{target.EntityId} segmentF32={beforeDistanceF32}->{afterDistanceF32} hitDistF32={hitDistanceF32} tick={updateTick} updates={pending.UpdatesCompleted}/{pending.MaxLifetimeTicks} radiusF32={hitRadiusF32} projectileRadiusF32={projectileRadiusF32} worldBlocked={worldBlocked} source={source ?? "unknown"}");
                    if (target.IsAlive && CombatRuntime.Instance.PeekMonsterCurrentHPWire(target) != 0)
                        HandleSpellAttack(pending.Conn, pending.State, target, pending.ManipId, pending.UseFlags, "SpellProjectile", false, pending.SkillUseCommitted);
                    return true;
                }

                pending.CurrentDistanceF32 = afterDistanceF32;
                if (pending.CurrentDistanceF32 >= maxDistanceF32 || pending.UpdatesCompleted >= pending.MaxLifetimeTicks)
                {
                    Debug.LogError($"[SPELL-PROJECTILE] expired no-hit spell={spell.DisplayName ?? spell.SkillId ?? "spell"} seq={pending.Sequence} manip={pending.ManipId} aimFixed=({pending.AimFixedX},{pending.AimFixedY}) currentF32={pending.CurrentDistanceF32}/{pending.MaxDistanceF32} updates={pending.UpdatesCompleted}/{pending.MaxLifetimeTicks} tick={updateTick} source={source ?? "unknown"} sourceFunction=Projectile::update range-end");
                    return true;
                }
            }

            return false;
        }

        private bool TryResolvePendingSpellTargetAlongSegment(ref PendingSpell pending, int segmentStartF32, int segmentEndF32, out Combat.Monster hitMonster, out int hitDistanceF32, out bool worldBlocked)
        {
            hitMonster = null;
            hitDistanceF32 = Math.Max(0, segmentEndF32);
            worldBlocked = false;
            Combat.SpellData spell = pending.Spell;
            if (spell == null || pending.Conn == null)
                return false;

            int startFixedX = pending.StartFixedX;
            int startFixedY = pending.StartFixedY;
            int aimFixedX = pending.AimFixedX;
            int aimFixedY = pending.AimFixedY;
            int aimDxFixed = aimFixedX - startFixedX;
            int aimDyFixed = aimFixedY - startFixedY;
            int aimDistanceFixed = UnitMover.IntSqrt(((long)aimDxFixed * aimDxFixed) + ((long)aimDyFixed * aimDyFixed));
            if (!pending.ProjectileGeometryInitialized && aimDistanceFixed <= 0)
                return false;

            string zoneName = !string.IsNullOrWhiteSpace(pending.Monster?.ZoneName)
                ? pending.Monster.ZoneName
                : pending.Conn.CurrentZoneName;
            string instanceKey = !string.IsNullOrWhiteSpace(pending.InstanceKey)
                ? pending.InstanceKey
                : (pending.Conn != null ? GetInstanceZoneKey(pending.Conn) : null);
            int projectileSizeF32 = pending.ProjectileSizeF32 > 0
                ? pending.ProjectileSizeF32
                : 0;
            int projectileRadiusFixed = WeaponUseRuntime.ProjectileRadiusFromAuthoredSizeF32(projectileSizeF32);
            int segmentStartFixed = Math.Max(0, segmentStartF32);
            int segmentEndFixed = Math.Max(segmentStartFixed, segmentEndF32);
            if (segmentEndFixed <= segmentStartFixed)
                return false;

            int segmentStartFixedX = pending.ProjectileGeometryInitialized ? pending.CurrentFixedX : startFixedX + (int)(((long)aimDxFixed * segmentStartFixed) / aimDistanceFixed);
            int segmentStartFixedY = pending.ProjectileGeometryInitialized ? pending.CurrentFixedY : startFixedY + (int)(((long)aimDyFixed * segmentStartFixed) / aimDistanceFixed);
            int segmentStartFixedZ = pending.ProjectileGeometryInitialized
                ? pending.CurrentFixedZ
                : pending.Conn.HasUnitFollowClientPosition
                    ? pending.Conn.UnitFollowClientPosFixedZ + 12 * UnitMover.Fixed
                    : pending.Conn.PlayerPosFixedZ + 12 * UnitMover.Fixed;
            int segmentEndFixedX;
            int segmentEndFixedY;
            int segmentEndFixedZ = segmentStartFixedZ;
            if (pending.ProjectileGeometryInitialized)
            {
                segmentEndFixedX = pending.CurrentFixedX + pending.VelocityFixedX;
                segmentEndFixedY = pending.CurrentFixedY + pending.VelocityFixedY;
                segmentEndFixedZ = pending.CurrentFixedZ + pending.VelocityFixedZ;
            }
            else
            {
                segmentEndFixedX = startFixedX + (int)(((long)aimDxFixed * segmentEndFixed) / aimDistanceFixed);
                segmentEndFixedY = startFixedY + (int)(((long)aimDyFixed * segmentEndFixed) / aimDistanceFixed);
            }
            PathMap projectilePathMap = ResolveUseTargetMovingPathMap(pending.Conn);
            int startTerrainFixedZ = segmentStartFixedZ;
            int endTerrainFixedZ = segmentEndFixedZ;
            bool startHeightResolved = projectilePathMap != null
                && projectilePathMap.TryGetHeightAtFixed(segmentStartFixedX, segmentStartFixedY, out startTerrainFixedZ);
            bool endHeightResolved = projectilePathMap != null
                && projectilePathMap.TryGetHeightAtFixed(segmentEndFixedX, segmentEndFixedY, out endTerrainFixedZ);
            if (endHeightResolved)
                segmentEndFixedZ = endTerrainFixedZ + pending.GroundOffsetFixedZ;
            if (pending.ProjectileGeometryInitialized)
                Debug.LogError($"[SPELL-PROJECTILE] subentity update spell={pending.Spell?.DisplayName ?? pending.Spell?.SkillId ?? "spell"} seq={pending.Sequence} update={pending.UpdatesCompleted} positionFixed=({segmentEndFixedX},{segmentEndFixedY},{segmentEndFixedZ}) velocityFixed=({pending.VelocityFixedX},{pending.VelocityFixedY},{pending.VelocityFixedZ}) terrainFixedZ=({(startHeightResolved ? startTerrainFixedZ : segmentStartFixedZ)},{(endHeightResolved ? endTerrainFixedZ : segmentEndFixedZ)}) distanceFixed={segmentEndFixed}");
            int maxDistanceF32 = pending.MaxDistanceF32 > 0
                ? pending.MaxDistanceF32
                : 1;
            long scanRangeLong = Math.Max(
                (long)segmentEndFixed + projectileRadiusFixed + (20L * UnitMover.Fixed),
                (long)maxDistanceF32 + projectileRadiusFixed + (20L * UnitMover.Fixed));
            int scanRangeFixed = scanRangeLong > int.MaxValue ? int.MaxValue : (int)Math.Max(0, scanRangeLong);

            Combat.Monster best = null;
            int bestAlongFixed = int.MaxValue;
            long bestDistSqFixed = long.MaxValue;
            bool bestBlocked = false;
            uint projectileViewerEntityId = pending.Conn?.Avatar != null ? (uint)pending.Conn.Avatar.Id : 0u;
            var nativeOrderedCandidates = CombatRuntime.Instance.GetProjectileHittableMonstersInNativeDistanceOrder(
                startFixedX,
                startFixedY,
                pending.Conn.PlayerPosFixedZ,
                instanceKey,
                zoneName,
                scanRangeFixed,
                playerEntityId: projectileViewerEntityId,
                useClientVisiblePosition: true);
            foreach (var candidate in nativeOrderedCandidates)
            {
                if (!CombatRuntime.Instance.IsProjectileHittableMonster(candidate, instanceKey, zoneName))
                    continue;

                if (!CombatRuntime.Instance.TryGetMonsterClientVisiblePositionFixed(candidate, projectileViewerEntityId, out int candidateFixedX, out int candidateFixedY))
                    continue;

                int candidateRadiusF32 = CombatRuntime.ResolveProjectileUnitCollisionRadiusF32(candidate);
                int hitRadiusFixed = WeaponUseRuntime.ProjectileCollisionRadiusF32(candidateRadiusF32, projectileSizeF32);
                if (!WeaponUseRuntime.TestProjectilePointUnitCollisionFixed(
                    segmentEndFixedX,
                    segmentEndFixedY,
                    candidateFixedX,
                    candidateFixedY,
                    hitRadiusFixed,
                    out long distSqFixed))
                    continue;

                int impactAlongFixed = segmentEndFixed;
                if (CombatRuntime.IsProjectileHitBetterFixed(candidate, impactAlongFixed, distSqFixed, best, bestAlongFixed, bestDistSqFixed))
                {
                    best = candidate;
                    bestAlongFixed = impactAlongFixed;
                    bestDistSqFixed = distSqFixed;
                    bestBlocked = false;
                }
            }

            if (best == null)
            {
                bool worldHitResolved = WorldCollision.Instance.TryGetPointBlockerFixed(
                    zoneName,
                    instanceKey,
                    segmentEndFixedX,
                    segmentEndFixedY,
                    segmentEndFixedZ,
                    UnitMover.Fixed,
                    out WorldCollisionHit worldHit);
                if (worldHitResolved)
                {
                    pending.CurrentFixedX = segmentEndFixedX;
                    pending.CurrentFixedY = segmentEndFixedY;
                    pending.CurrentFixedZ = segmentEndFixedZ;
                    hitDistanceF32 = segmentEndFixed;
                    worldBlocked = true;
                    Debug.LogError($"[SPELL-PROJECTILE-WORLD] seq={pending.Sequence} zone='{zoneName ?? ""}' instance='{instanceKey ?? ""}' object='{worldHit.ObjectPath}' collision='{worldHit.CollisionObject}' hybrid={worldHit.Hybrid} hybridSource='{worldHit.HybridSource ?? ""}' pointFixed=({segmentEndFixedX},{segmentEndFixedY},{segmentEndFixedZ}) radiusFixed={UnitMover.Fixed} hitDistanceFixed={hitDistanceF32} result=impact sourceFunction=ProjectileChecker::testFirstTime@0x0059A490->WorldCollisionObject::testCollision@0x004EAB00");
                    return true;
                }
                bool heightSectorCrossed = startHeightResolved
                    && endHeightResolved
                    && (segmentStartFixedZ <= startTerrainFixedZ) != (segmentEndFixedZ <= endTerrainFixedZ);
                pending.CurrentFixedX = segmentEndFixedX;
                pending.CurrentFixedY = segmentEndFixedY;
                pending.CurrentFixedZ = segmentEndFixedZ;
                if (heightSectorCrossed)
                {
                    hitDistanceF32 = segmentEndFixed;
                    worldBlocked = true;
                    Debug.LogError($"[SPELL-PROJECTILE-HEIGHT] seq={pending.Sequence} segmentFixed=({segmentStartFixedX},{segmentStartFixedY},{segmentStartFixedZ})->({segmentEndFixedX},{segmentEndFixedY},{segmentEndFixedZ}) terrainFixedZ=({startTerrainFixedZ},{endTerrainFixedZ}) hitDistanceFixed={hitDistanceF32} sourceFunction=ProjectileChecker::testFirstTime@0x0059A490");
                    return true;
                }
                return false;
            }

            pending.CurrentFixedX = segmentEndFixedX;
            pending.CurrentFixedY = segmentEndFixedY;
            pending.CurrentFixedZ = segmentEndFixedZ;
            CombatRuntime.Instance.ApplyMonsterWanderClientVisiblePosition(best, "SpellProjectileChecker-subentity-hit");
            hitMonster = best;
            hitDistanceF32 = Math.Max(0, bestAlongFixed);
            worldBlocked = bestBlocked;
            return true;
        }

        private Combat.Monster ResolvePendingSpellTarget(ref PendingSpell pending, int nowTick, string source)
        {
            string instanceKey = !string.IsNullOrWhiteSpace(pending.InstanceKey)
                ? pending.InstanceKey
                : (pending.Conn != null ? GetInstanceZoneKey(pending.Conn) : null);
            if (pending.Monster != null &&
                pending.Monster.IsAlive &&
                CombatRuntime.Instance.PeekMonsterCurrentHPWire(pending.Monster) != 0 &&
                CombatRuntime.Instance.MatchesInstance(pending.Monster, instanceKey))
                return pending.Monster;

            Combat.SpellData spell = pending.Spell;
            if (spell == null)
            {
                Combat.SpellDatabase.Initialize();
                spell = ResolveSpellFromManip(pending.Conn, pending.ManipId);
                pending.Spell = spell;
            }

            if (spell == null || spell.ProjectileSizeF32 <= 0)
                return null;
            if (pending.QueuedWithoutInitialTarget || pending.ProjectileRuntimeInitialized)
                return null;

            int startFixedX = pending.StartFixedX;
            int startFixedY = pending.StartFixedY;
            if (pending.Conn != null && startFixedX == 0 && startFixedY == 0)
            {
                startFixedX = pending.Conn.PlayerPosFixedX;
                startFixedY = pending.Conn.PlayerPosFixedY;
            }

            Combat.Monster target = FindFirstProjectileMonsterHitFromStartFixed(
                pending.Conn,
                spell,
                startFixedX,
                startFixedY,
                pending.AimFixedX,
                pending.AimFixedY,
                out int hitDistanceF32);
            if (target == null)
                return null;

            pending.Monster = target;
            pending.ProjectileHitDistanceF32 = Math.Max(0, hitDistanceF32);
            pending.ProjectileDelayTicks = ResolveProjectileImpactDelayTicksFixed32(spell, pending.State, pending.ProjectileHitDistanceF32);
            Debug.LogError($"[SPELL-PROJECTILE] resolved spell={spell.DisplayName ?? spell.SkillId ?? "spell"} target={target.Name}#{target.EntityId} hitDistF32={hitDistanceF32} dueTick={pending.DueTick} nowTick={nowTick} source={source ?? "unknown"} queuedWithoutInitialTarget={pending.QueuedWithoutInitialTarget}");
            return target;
        }

        private bool IsPendingSpellDue(PendingSpell pending, int nowTick)
        {
            return pending.DueTick <= 0 || nowTick >= pending.DueTick;
        }

        private Dictionary<uint, (Combat.Monster monster, int experienceF32, uint sourceLevel, string source)> _pendingMonsterDeathExperience
            = new Dictionary<uint, (Combat.Monster, int, uint, string)>();
    }
}
