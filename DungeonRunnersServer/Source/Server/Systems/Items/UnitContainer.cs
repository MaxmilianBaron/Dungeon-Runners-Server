using DungeonRunners.Utilities;
using System.Collections.Generic;
using DungeonRunners.Engine;
using DungeonRunners.Data;
using DungeonRunners.Combat;
using DungeonRunners.Database;
using System;
using System.Linq;

namespace DungeonRunners.Networking
{
    public class UnitContainer
    {
        private readonly GameServer _server;

        public UnitContainer(GameServer server)
        {
            _server = server;
        }

        private readonly object _consumableModifierSync = new object();
        private readonly List<PendingConsumableModifierApply> _pendingConsumableModifierApplies = new List<PendingConsumableModifierApply>();
        private readonly HashSet<string> _reservedConsumableModifierKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ActiveConsumableModifier> _activeConsumableModifiers = new Dictionary<string, ActiveConsumableModifier>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<PlayerState> _subscribedModifierStates = new HashSet<PlayerState>();
        private const uint FirstConsumableModifierId = 0x10000000;
        private const uint LastConsumableModifierId = 0x1FFFFFFF;
        private uint _nextConsumableModifierId = FirstConsumableModifierId;
        private ulong _nextConsumableModifierSequence;

        private sealed class PendingConsumableModifierApply
        {
            public ulong Sequence;
            public uint ReceiveTick;
            public RRConnection Connection;
            public PlayerState PlayerState;
            public string ConnectionId;
            public uint AvatarEntityId;
            public string RuntimeInstanceKey;
            public ushort ModifiersComponentId;
            public uint ModifierId;
            public string ModifierKey;
            public string ModifierFamily;
            public string GCType;
            public int HitPointRegenBonus;
            public int ManaPointRegenBonus;
            public int StrengthBonus;
            public int EnduranceBonus;
            public int IntellectBonus;
            public int StunResistBonus;
            public ushort DurationTicks;
            public bool RemoveOnDeath;
            public uint PowerLevel;
            public string StackRule;
            public byte[] ResponseMessage;
        }

        private sealed class ActiveConsumableModifier
        {
            public PendingConsumableModifierApply Runtime;
        }

        public void ProcessRequest(RRConnection conn, LEReader reader, ushort componentId, byte subMessage)
        {
            Debug.LogError($"[UNIT-CONTAINER] update component=0x{componentId:X4} sub=0x{subMessage:X2}");
            switch (subMessage)
            {
                case 0x21:
                    ProcessSetLocal(conn, reader, componentId);
                    break;
                case 0x22:
                    ProcessClearLocal(conn, reader, componentId);
                    break;
                case 0x23:
                    ProcessDropItem(conn, reader, componentId);
                    break;
                case 0x25:
                    ProcessUseItem(conn, reader, componentId);
                    break;
                case 0x26:
                    ProcessUseItemPosition(conn, reader, componentId);
                    break;
                case 0x27:
                    ProcessUseItemTarget(conn, reader, componentId);
                    break;
                case 0x28:
                    ProcessGetItemLocal(conn, reader, componentId);
                    break;
                case 0x29:
                    ProcessPutItemXYLocal(conn, reader, componentId);
                    break;
                default:
                    Debug.LogError($"[UNIT-CONTAINER] sub=0x{subMessage:X2} reason=unhandled");
                    break;
            }
        }

        private void ProcessUseItem(RRConnection conn, LEReader reader, ushort componentId)
        {
            if (!TryReadUseItemRequest(reader, out uint clientItemId, out uint requestedTypeId, out string requestedPacketGcClass))
            {
                Debug.LogError("[USE-ITEM] reason=invalid-item-payload");
                return;
            }
            Debug.LogError($"[USE-ITEM] clientItemId={clientItemId} typeId=0x{requestedTypeId:X8} packetGcType={requestedPacketGcClass ?? ""}");

            string connId = conn.ConnId.ToString();
            PlayerState playerState = _server.GetPlayerState(connId);

            uint actualSlot = clientItemId;
            var itemData = _server.GetInventoryItemBySlot(connId, clientItemId);

            if (itemData == null)
            {
                Debug.LogError($"[USE-ITEM] clientItemId={clientItemId} reason=missing");
                return;
            }

            GCObject item = itemData.Value.item;
            byte itemX = itemData.Value.x;
            byte itemY = itemData.Value.y;
            string gcLower = item.GCClass.ToLower();

            uint storedPacketTypeId = GCTypeCodec.HashString(GCObject.GetPacketGCClassFor(item.GCClass));
            uint storedItemTypeId = GCTypeCodec.HashString(item.GCClass);
            if (requestedTypeId != storedPacketTypeId && requestedTypeId != storedItemTypeId)
            {
                Debug.LogError($"[USE-ITEM] clientItemId={clientItemId} requested=0x{requestedTypeId:X8} storedPacket=0x{storedPacketTypeId:X8} storedItem=0x{storedItemTypeId:X8} reason=item-type-mismatch");
                return;
            }

            Debug.LogError($"[USE-ITEM] item={item.GCClass} pos=({itemX},{itemY}) slot={actualSlot}");

            if (gcLower.Contains("townportal") || gcLower.Contains("permawaypointscroll"))
            {
                var zoneDef = GCDatabase.Instance?.Resolve(conn.CurrentZoneName ?? "");
                if (zoneDef != null && zoneDef.GetBool("IsTown"))
                {
                    Debug.LogError($"[USE-ITEM] item={item.GCClass} zone={conn.CurrentZoneName} reason=UsableInTown=false sourceFunction=ActiveItem::validateUse@0x00579190");
                    return;
                }
                string respawnZone = zoneDef?.GetString("RespawnZone", "town") ?? "town";
                if (!TryConsumeInventoryItem(connId, actualSlot, item, itemX, itemY, out int portalStack, out int remaining, out int portalWidth, out int portalHeight))
                    return;
                if (!_server.SpawnTownPortalWithRemoval(conn, respawnZone.ToLowerInvariant(), componentId, clientItemId,
                    playerState, item, gcLower, itemX, itemY, remaining))
                {
                    RollbackConsumedInventoryItem(connId, actualSlot, item, itemX, itemY, portalStack, portalWidth, portalHeight);
                }
                return;
            }

            if (gcLower.Contains("skillbook"))
            {
                if (!TryResolveSkillBookSkill(item, out string skillToLearn))
                {
                    Debug.LogError($"[SKILLBOOK] item={item.GCClass} result=rejected reason=unsupported-book consumed=False");
                    return;
                }
                SavedCharacter savedCharacter = _server.GetSavedCharacterForConn(conn);
                bool alreadyKnown = savedCharacter?.skills != null
                    && savedCharacter.skills.Any(skill => string.Equals(skill, skillToLearn, StringComparison.OrdinalIgnoreCase));
                if (alreadyKnown)
                {
                    _server.SendSystemMessage(conn, "[Skill Book] You already know this skill.");
                    Debug.LogError($"[SKILLBOOK] item={item.GCClass} skill={skillToLearn} result=already-known consumed=False");
                    return;
                }
                if (!TryConsumeInventoryItem(connId, actualSlot, item, itemX, itemY, out int bookStack, out int remainingBooks, out int bookWidth, out int bookHeight))
                    return;
                GameServer.SkillGrantRuntimeResult grantResult = _server.GrantSkillRuntime(conn, skillToLearn, deferMaterialization: true);
                if (grantResult != GameServer.SkillGrantRuntimeResult.Committed)
                {
                    RollbackConsumedInventoryItem(connId, actualSlot, item, itemX, itemY, bookStack, bookWidth, bookHeight);
                    _server.SendSystemMessage(conn, grantResult == GameServer.SkillGrantRuntimeResult.AlreadyKnown
                        ? "[Skill Book] You already know this skill."
                        : "[Skill Book] The skill could not be saved; the book was not consumed.");
                    return;
                }
                try
                {
                    var skillBookWriter = new LEWriter();
                    skillBookWriter.WriteByte(0x07);
                    skillBookWriter.WriteByte(0x35);
                    skillBookWriter.WriteUInt16(componentId);
                    skillBookWriter.WriteByte(0x1F);
                    skillBookWriter.WriteUInt32(clientItemId);
                    if (remainingBooks > 0)
                    {
                        skillBookWriter.WriteByte(0x35);
                        skillBookWriter.WriteUInt16(componentId);
                        skillBookWriter.WriteByte(0x1E);
                        skillBookWriter.WriteByte(0x0B);
                        int skillBookLevel = item.StoredLevel >= 0 ? item.StoredLevel : Math.Max(1, item.GetItemRequiredLevel());
                        item.WriteItemData(skillBookWriter, clientItemId, itemX, itemY, (byte)Math.Min(remainingBooks, byte.MaxValue), skillBookLevel);
                    }
                    if (!_server.WritePlayerEntitySynch(conn, skillBookWriter))
                        throw new InvalidOperationException("Skill book inventory synchronization failed");
                    skillBookWriter.WriteByte(0x06);
                    _server.SendToClient(conn, skillBookWriter.ToArray());
                    if (!_server.MaterializeCommittedSkillGrantRuntime(conn, skillToLearn))
                        return;
                    string shortName = skillToLearn.Substring(skillToLearn.LastIndexOf('.') + 1);
                    _server.SendSystemMessage(conn, $"[Skill Book] You learned {shortName}!");
                    Debug.LogError($"[SKILLBOOK] item={item.GCClass} skill={skillToLearn} result=committed remaining={remainingBooks}");
                }
                catch (Exception ex)
                {
                    _server.QuarantineCommittedPersistenceSyncFailurePublic(conn, "skillbook-materialization", ex);
                }
                return;
            }

            bool potionModifier = TryResolvePotionModifier(item, out string resolvedPotionModifierType, out string resolvedPotionModifierFamily, out int hitPointRegenBonus, out int manaPointRegenBonus);
            bool dragonJuiceModifier = !potionModifier && gcLower.Contains("dragonjuice");
            bool intellectModifier = !potionModifier && !dragonJuiceModifier && gcLower.Contains("intbuff");
            if (!potionModifier && !dragonJuiceModifier && !intellectModifier)
            {
                Debug.LogError($"[USE-ITEM] item={item.GCClass} reason=unsupported-usable-item consumed=False");
                return;
            }
            string modifierType;
            string modifierFamily;
            int strengthBonus = 0;
            int enduranceBonus = 0;
            int intellectBonus = 0;
            int stunResistBonus = 0;
            ushort durationTicks;
            bool removeOnDeath;
            string stackRule;
            if (potionModifier)
            {
                modifierType = resolvedPotionModifierType;
                modifierFamily = resolvedPotionModifierFamily;
                durationTicks = 151;
                removeOnDeath = false;
                stackRule = null;
            }
            else if (dragonJuiceModifier)
            {
                bool large = gcLower.Contains("_lg");
                modifierType = large ? "PotionPAL.DragonJuice_Lg.Modifier" : "PotionPAL.DragonJuice_Sm.Modifier";
                modifierFamily = "dragonjuice";
                strengthBonus = large ? 2 : 1;
                enduranceBonus = -1;
                durationTicks = large ? (ushort)3601 : (ushort)1801;
                removeOnDeath = true;
                stackRule = "UNIQUEBYTYPE";
            }
            else
            {
                bool large = gcLower.Contains("_lg");
                modifierType = large ? "PotionPAL.IntBuff_Lg.Modifier" : "PotionPAL.IntBuff_Sm.Modifier";
                modifierFamily = "intbuff";
                intellectBonus = large ? 1 : 0;
                durationTicks = large ? (ushort)5401 : (ushort)1801;
                removeOnDeath = true;
                stackRule = "UNIQUEBYTYPE";
            }
            if (playerState == null || conn.Avatar == null || conn.ModifiersId == 0
                || !TryReserveConsumableModifierId(out uint consumableModifierId))
            {
                Debug.LogError($"[CONSUMABLE-MODIFIER] queue reason=modifier-id-unavailable conn={conn.ConnId} avatar={conn.Avatar?.Id ?? 0} modifiers={conn.ModifiersId} type={item.GCClass}");
                return;
            }
            string modifierKey = BuildConsumableModifierKey(connId, modifierFamily, consumableModifierId, stackRule);
            if (!playerState.ShouldAcceptAttributeModifier(modifierKey, modifierType, stackRule, 0x100, durationTicks))
            {
                Debug.LogError($"[CONSUMABLE-MODIFIER] reject-before-commit entity={conn.Avatar.Id} modifier={consumableModifierId} type={modifierType} stack={stackRule ?? ""}");
                return;
            }
            if (!TryReserveConsumableModifierAdmission(modifierKey))
            {
                Debug.LogError($"[CONSUMABLE-MODIFIER] reject-before-commit entity={conn.Avatar.Id} modifier={consumableModifierId} type={modifierType} stack={stackRule ?? ""} reason=pending-family");
                return;
            }
            IReadOnlyList<string> replaceGcTypes = dragonJuiceModifier
                ? new[] { "PotionPAL.DragonJuice_Sm.Modifier", "PotionPAL.DragonJuice_Lg.Modifier" }
                : intellectModifier
                    ? new[] { "PotionPAL.IntBuff_Sm.Modifier", "PotionPAL.IntBuff_Lg.Modifier" }
                    : null;
            if (!TryConsumeInventoryItem(connId, actualSlot, item, itemX, itemY, out int stackCount, out int remainingCount, out int itemWidth, out int itemHeight))
            {
                ReleaseConsumableModifierAdmission(modifierKey);
                return;
            }
            if (!_server.SavePlayerInventoryWithModifierPublic(
                conn,
                modifierType,
                consumableModifierId,
                1,
                0x100,
                durationTicks,
                1,
                !string.IsNullOrWhiteSpace(stackRule),
                replaceGcTypes))
            {
                RollbackConsumedInventoryItem(connId, actualSlot, item, itemX, itemY, stackCount, itemWidth, itemHeight);
                ReleaseConsumableModifierAdmission(modifierKey);
                Debug.LogError($"[USE-ITEM] item={item.GCClass} reason=inventory-modifier-commit-failed consumed=False");
                return;
            }
            bool consumableQueued = false;
            try
            {
                QueueUseItemInventoryUpdate(conn, componentId, clientItemId, remainingCount);
                QueueActiveItemUseUpdate(conn, item, clientItemId, itemX, itemY, stackCount);
                QueuePeerActiveItemUseUpdates(conn, item, clientItemId, itemX, itemY, stackCount);
                consumableQueued = QueueConsumableModifier(conn, playerState, consumableModifierId, modifierType, modifierFamily, hitPointRegenBonus, manaPointRegenBonus, strengthBonus, enduranceBonus, intellectBonus, stunResistBonus, durationTicks, removeOnDeath, stackRule);
                if (!consumableQueued)
                    throw new InvalidOperationException("Committed consumable modifier could not be queued");
                Debug.LogError("[USE-ITEM] consumed=True durable=True");
            }
            catch (Exception ex)
            {
                if (!consumableQueued)
                    ReleaseConsumableModifierAdmission(modifierKey);
                _server.QuarantineCommittedPersistenceSyncFailurePublic(conn, "consumable-use-materialization", ex);
            }
        }

        private static bool TryReadUseItemRequest(LEReader reader, out uint itemSlotId, out uint typeId, out string packetGcClass)
        {
            itemSlotId = 0;
            typeId = 0;
            packetGcClass = null;
            if (reader == null || reader.Remaining < 1)
                return false;

            byte typeTag = reader.ReadByte();
            switch (typeTag)
            {
                case 0x01:
                    if (reader.Remaining < 1)
                        return false;
                    typeId = reader.ReadByte();
                    break;
                case 0x02:
                    if (reader.Remaining < 2)
                        return false;
                    typeId = reader.ReadUInt16();
                    break;
                case 0x04:
                    if (reader.Remaining < 4)
                        return false;
                    typeId = reader.ReadUInt32();
                    break;
                case 0xFF:
                    try
                    {
                        packetGcClass = reader.ReadCString().ToLowerInvariant();
                        if (string.IsNullOrWhiteSpace(packetGcClass))
                            return false;
                        typeId = GCTypeCodec.HashString(packetGcClass);
                    }
                    catch (InvalidOperationException)
                    {
                        return false;
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        return false;
                    }
                    break;
                default:
                    return false;
            }

            if (typeId == 0 || reader.Remaining < 8)
                return false;
            itemSlotId = reader.ReadUInt32();
            reader.Skip(4);
            return true;
        }

        private bool TryConsumeInventoryItem(string connectionId, uint slot, GCObject item, byte itemX, byte itemY, out int previousStackCount, out int remainingCount, out int itemWidth, out int itemHeight)
        {
            previousStackCount = _server.GetStackCount(connectionId, slot);
            remainingCount = 0;
            ItemData itemData = AuthoredGameplayCatalog.FindItem(item.GCClass);
            itemWidth = itemData?.inventoryWidth ?? 0;
            itemHeight = itemData?.inventoryHeight ?? 0;
            if (itemData == null || previousStackCount <= 0 || itemWidth <= 0 || itemWidth > 10 || itemHeight <= 0 || itemHeight > 8)
                return false;
            if (previousStackCount > 1)
            {
                remainingCount = previousStackCount - 1;
                _server.SetStackCount(connectionId, slot, remainingCount);
                return true;
            }
            _server.FreeInventorySlots(connectionId, itemX, itemY, itemWidth, itemHeight);
            _server.RemoveInventoryItemBySlot(connectionId, slot);
            return true;
        }

        private void RollbackConsumedInventoryItem(string connectionId, uint slot, GCObject item, byte itemX, byte itemY, int previousStackCount, int itemWidth, int itemHeight)
        {
            if (previousStackCount > 1)
            {
                _server.SetStackCount(connectionId, slot, previousStackCount);
                return;
            }
            _server.TrackInventoryItem(connectionId, slot, item, itemX, itemY);
            _server.OccupyInventorySlots(connectionId, itemX, itemY, itemWidth, itemHeight);
            _server.SetStackCount(connectionId, slot, previousStackCount);
        }

        private static bool TryResolvePotionModifier(GCObject item, out string modifierType, out string modifierFamily, out int hitPointRegenBonus, out int manaPointRegenBonus)
        {
            modifierType = null;
            modifierFamily = null;
            hitPointRegenBonus = 0;
            manaPointRegenBonus = 0;
            string packetGcType = GCObject.GetPacketGCClassFor(item?.GCClass);
            switch (packetGcType.ToLowerInvariant())
            {
                case "potionpal.healthpotion_noob":
                    modifierType = "PotionPAL.HealthPotion_Noob.Modifier";
                    modifierFamily = "health-potion";
                    hitPointRegenBonus = 9;
                    return true;
                case "potionpal.manapotion_noob":
                    modifierType = "PotionPAL.ManaPotion_Noob.Modifier";
                    modifierFamily = "mana-potion";
                    manaPointRegenBonus = 9;
                    return true;
                case "items.consumables.consumable_minorhealthpotion":
                    modifierType = "items.consumables.Consumable_MinorHealthPotion.Modifier";
                    modifierFamily = "health-potion";
                    hitPointRegenBonus = 8;
                    return true;
                case "items.consumables.consumable_minormanapotion":
                    modifierType = "items.consumables.Consumable_MinorManaPotion.Modifier";
                    modifierFamily = "mana-potion";
                    manaPointRegenBonus = 8;
                    return true;
                case "items.consumables.consumable_majorhealthpotion":
                    modifierType = "items.consumables.Consumable_MajorHealthPotion.Modifier";
                    modifierFamily = "health-potion";
                    hitPointRegenBonus = 10;
                    return true;
                case "items.consumables.consumable_majormanapotion":
                    modifierType = "items.consumables.Consumable_MajorManaPotion.Modifier";
                    modifierFamily = "mana-potion";
                    manaPointRegenBonus = 10;
                    return true;
                case "potionpal.healthpotion_itempack":
                    modifierType = "PotionPAL.HealthPotion_ItemPack.Modifier";
                    modifierFamily = "health-potion";
                    hitPointRegenBonus = 10;
                    return true;
                case "potionpal.manapotion_itempack":
                    modifierType = "PotionPAL.ManaPotion_ItemPack.Modifier";
                    modifierFamily = "mana-potion";
                    manaPointRegenBonus = 10;
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryResolveSkillBookSkill(GCObject item, out string skillToLearn)
        {
            skillToLearn = null;
            if (item == null || string.IsNullOrWhiteSpace(item.GCClass) || GCDatabase.Instance == null)
                return false;
            string packetGcType = GCObject.GetPacketGCClassFor(item.GCClass);
            GCNode book = GCDatabase.Instance.ResolveWithInheritance(packetGcType)
                ?? GCDatabase.Instance.ResolveWithInheritance(item.GCClass);
            GCNode effect = GCDatabase.Instance.ResolveWithInheritance(book?.GetChild("Effect"))
                ?? GCDatabase.Instance.ResolveWithInheritance(packetGcType + ".Effect")
                ?? GCDatabase.Instance.ResolveWithInheritance(item.GCClass + ".Effect");
            string authoredSkill = effect?.GetString("SkillToLearn", string.Empty)?.Trim();
            if (string.IsNullOrWhiteSpace(authoredSkill)
                || GCDatabase.Instance.ResolveWithInheritance(authoredSkill) == null)
                return false;
            skillToLearn = authoredSkill;
            return true;
        }

        private void QueueUseItemInventoryUpdate(RRConnection conn, ushort componentId, uint clientItemId, int quantity)
        {
            conn.MessageQueue.EnqueueDeferred(() =>
            {
                if (conn == null || !conn.IsConnected || componentId == 0)
                    return Array.Empty<byte>();
                return ClientEntityUpdate.Component(
                    componentId,
                    quantity > 0 ? (byte)0x22 : (byte)0x1F,
                    writer =>
                    {
                        writer.WriteUInt32(clientItemId);
                        if (quantity > 0)
                            writer.WriteByte((byte)Math.Min(byte.MaxValue, quantity));
                    },
                    writer => _server.WritePlayerEntitySynch(conn, writer));
            }, componentId: componentId);
        }

        private void QueueActiveItemUseUpdate(RRConnection conn, GCObject item, uint clientItemId, byte itemX, byte itemY, int quantity)
        {
            ushort avatarEntityId = (ushort)Math.Max(0, conn?.Avatar?.Id ?? 0);
            string packetGcType = item?.GetPacketGCClass() ?? string.Empty;
            int itemLevel = Math.Clamp(item?.StoredLevel >= 0 ? item.StoredLevel : 1, 0, byte.MaxValue);
            byte itemQuantity = (byte)Math.Clamp(quantity, 0, byte.MaxValue);
            conn?.MessageQueue.EnqueueDeferred(() =>
            {
                if (conn == null || !conn.IsConnected || avatarEntityId == 0 || string.IsNullOrWhiteSpace(packetGcType))
                    return Array.Empty<byte>();
                return ClientEntityUpdate.Entity(
                    avatarEntityId,
                    0x08,
                    writer =>
                    {
                        item.WriteItemData(writer, clientItemId, itemX, itemY, itemQuantity, itemLevel);
                    },
                    writer => _server.WritePlayerEntitySynch(conn, writer));
            }, componentId: avatarEntityId);
        }

        private void QueuePeerActiveItemUseUpdates(RRConnection owner, GCObject item, uint clientItemId, byte itemX, byte itemY, int quantity)
        {
            string packetGcType = item?.GetPacketGCClass() ?? string.Empty;
            int itemLevel = Math.Clamp(item?.StoredLevel >= 0 ? item.StoredLevel : 1, 0, byte.MaxValue);
            byte itemQuantity = (byte)Math.Clamp(quantity, 0, byte.MaxValue);
            if (owner == null || string.IsNullOrWhiteSpace(packetGcType))
                return;
            foreach (RRConnection viewer in _server.GetInstancePeerConnections(owner))
            {
                ushort remoteAvatarEntityId = _server.ResolveRemoteAvatarEntityIdForViewer(viewer, owner);
                if (remoteAvatarEntityId == 0)
                    continue;
                viewer.MessageQueue.EnqueueDeferred(() =>
                {
                    if (!viewer.IsConnected || !owner.IsConnected)
                        return Array.Empty<byte>();
                    return ClientEntityUpdate.Entity(
                        remoteAvatarEntityId,
                        0x08,
                        writer =>
                        {
                            item.WriteItemData(writer, clientItemId, itemX, itemY, itemQuantity, itemLevel);
                        },
                        writer => _server.WritePlayerEntitySynch(owner, writer));
                }, componentId: remoteAvatarEntityId);
            }
        }

        private void ProcessUseItemPosition(RRConnection conn, LEReader reader, ushort componentId)
        {
            Debug.LogError($"[USE-ITEM-POS] sub=0x26 remaining={reader.Remaining}");
            if (reader.Remaining >= 4)
            {
                ProcessUseItem(conn, reader, componentId);
            }
            else
            {
                Debug.LogError("[USE-ITEM-POS] reason=short-read");
            }
        }

        private void ProcessUseItemTarget(RRConnection conn, LEReader reader, ushort componentId)
        {
            Debug.LogError($"[USE-ITEM-TARGET] sub=0x27 remaining={reader.Remaining}");
            if (reader.Remaining > 0)
            {
                byte[] rawData = reader.PeekRemaining();
                Debug.LogError($"[USE-ITEM-TARGET] rawBytes={rawData.Length} hex={BitConverter.ToString(rawData)}");
            }
        }

        private void ProcessSetLocal(RRConnection conn, LEReader reader, ushort componentId)
        {
            Debug.LogError($"[UNIT-CONTAINER] query sub=0x21 remaining={reader.Remaining}");
            var bytes = new List<byte>();
            while (reader.Remaining > 0)
                bytes.Add(reader.ReadByte());
            Debug.LogError($"[UNIT-CONTAINER] queryData sub=0x21 hex={BitConverter.ToString(bytes.ToArray())}");
        }

        private void ProcessClearLocal(RRConnection conn, LEReader reader, ushort componentId)
        {
            Debug.LogError($"[UNIT-CONTAINER] position sub=0x22 remaining={reader.Remaining}");
            var bytes = new List<byte>();
            while (reader.Remaining > 0)
                bytes.Add(reader.ReadByte());
            Debug.LogError($"[UNIT-CONTAINER] positionData sub=0x22 hex={BitConverter.ToString(bytes.ToArray())}");
        }

        private void ProcessPutItemXYLocal(RRConnection conn, LEReader reader, ushort componentId)
        {
            byte inventoryID = reader.ReadByte();
            byte x = reader.ReadByte();
            byte y = reader.ReadByte();
            Debug.LogError($"[UNIT-CONTAINER] place container=0x{inventoryID:X2} pos=({x},{y})");

            PlayerState playerState = _server.GetPlayerState(conn.ConnId.ToString());
            GCObject item = playerState.ActiveItem;

            if (item == null)
            {
                Debug.LogError("[UNIT-CONTAINER] place reason=no-active-item");
                return;
            }

            ItemData itemData = AuthoredGameplayCatalog.FindItem(item.GCClass);
            int itemWidth = itemData?.inventoryWidth ?? 1;
            int itemHeight = itemData?.inventoryHeight ?? 1;

            if (!_server.IsInventoryPlacementInBounds(x, y, itemWidth, itemHeight, inventoryID))
                return;
            if (_server.IsInventorySlotOccupied(conn.ConnId.ToString(), x, y, itemWidth, itemHeight, inventoryID))
            {
                uint? collision = null;
                for (int row = y; row < y + itemHeight; row++)
                    for (int col = x; col < x + itemWidth; col++)
                        if (_server.TryGetInventoryItemAt(conn.ConnId.ToString(), (byte)col, (byte)row, inventoryID, out uint slot, out _))
                        {
                            if (collision.HasValue && collision.Value != slot)
                                return;
                            collision = slot;
                        }
                if (TryMergeActiveItemIntoOccupiedSlot(conn, componentId, inventoryID, x, y, item))
                    return;
                if (TrySwapActiveItemIntoOccupiedSlot(conn, componentId, inventoryID, x, y, item, itemWidth, itemHeight))
                    return;
                Debug.LogError($"[UNIT-CONTAINER] place size={itemWidth}x{itemHeight} pos=({x},{y}) container=0x{inventoryID:X2} reason=occupied");
                return;
            }

            uint trackingSlot = _server.GetNextInventorySlot(conn.ConnId.ToString());

            var writer = new LEWriter();
            writer.WriteByte(0x07);

            writer.WriteByte(0x35);
            writer.WriteUInt16(componentId);
            writer.WriteByte(0x29);
            _server.WritePlayerEntitySynch(conn, writer);

            writer.WriteByte(0x35);
            writer.WriteUInt16(componentId);
            writer.WriteByte(0x1E);
            writer.WriteByte(inventoryID);

            string connId = conn.ConnId.ToString();
            int stackCount = _server.GetStackCount(connId, 0xFFFFFFFF);
            if (stackCount <= 0)
                stackCount = 1;
            int itemLevel = item.StoredLevel >= 0 ? item.StoredLevel : Math.Max(1, item.GetItemRequiredLevel());
            item.WriteInitForInventory(writer, x, y, trackingSlot, itemLevel, (byte)Math.Min(stackCount, byte.MaxValue));
            _server.WritePlayerEntitySynch(conn, writer);

            writer.WriteByte(0x06);

            _server.OccupyInventorySlots(conn.ConnId.ToString(), x, y, itemWidth, itemHeight, inventoryID);
            _server.TrackInventoryItem(conn.ConnId.ToString(), trackingSlot, item, x, y, inventoryID);
            _server.SetStackCount(connId, trackingSlot, stackCount, inventoryID);
            _server.SetStackCount(connId, 0xFFFFFFFF, 0);
            playerState.ActiveItem = null;
            if (!_server.SavePlayerInventoryPublic(conn))
            {
                _server.RemoveInventoryItemBySlot(connId, trackingSlot, inventoryID);
                _server.FreeInventorySlots(connId, x, y, itemWidth, itemHeight, inventoryID);
                playerState.ActiveItem = item;
                _server.SetStackCount(connId, 0xFFFFFFFF, stackCount);
                Debug.LogError($"[UNIT-CONTAINER] place state=failed phase=save item={item.GCClass} container=0x{inventoryID:X2}");
                return;
            }
            _server.SendToClient(conn, writer.ToArray());
            Debug.LogError($"[UNIT-CONTAINER] placed item={item.GCClass} pos=({x},{y}) container=0x{inventoryID:X2}");
        }

        private bool TryMergeActiveItemIntoOccupiedSlot(RRConnection conn, ushort componentId, byte inventoryID,
            byte x, byte y, GCObject activeItem)
        {
            string connId = conn.ConnId.ToString();
            if (!_server.TryGetInventoryItemAt(connId, x, y, inventoryID, out uint destinationSlot,
                out (GCObject item, byte x, byte y) destinationData))
                return false;
            int sourceCount = _server.GetStackCount(connId, 0xFFFFFFFF);
            int destinationCount = _server.GetStackCount(connId, destinationSlot, inventoryID);
            if (!ItemStackRules.CanMerge(destinationData.item, destinationCount, activeItem, sourceCount))
                return false;

            int mergedCount = destinationCount + sourceCount;
            PlayerState playerState = _server.GetPlayerState(connId);
            _server.SetStackCount(connId, destinationSlot, mergedCount, inventoryID);
            _server.SetStackCount(connId, 0xFFFFFFFF, 0);
            playerState.ActiveItem = null;
            if (!_server.SavePlayerInventoryPublic(conn))
            {
                _server.SetStackCount(connId, destinationSlot, destinationCount, inventoryID);
                _server.SetStackCount(connId, 0xFFFFFFFF, sourceCount);
                playerState.ActiveItem = activeItem;
                return true;
            }

            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x35);
            writer.WriteUInt16(componentId);
            writer.WriteByte(0x29);
            _server.WritePlayerEntitySynch(conn, writer);
            writer.WriteByte(0x35);
            writer.WriteUInt16(componentId);
            writer.WriteByte(0x22);
            writer.WriteUInt32(destinationSlot);
            writer.WriteByte((byte)mergedCount);
            _server.WritePlayerEntitySynch(conn, writer);
            writer.WriteByte(0x06);
            _server.SendToClient(conn, writer.ToArray());
            return true;
        }

        private bool TrySwapActiveItemIntoOccupiedSlot(RRConnection conn, ushort componentId, byte inventoryID,
            byte x, byte y, GCObject activeItem, int activeWidth, int activeHeight)
        {
            string connId = conn.ConnId.ToString();
            if (!_server.TryGetInventoryItemAt(connId, x, y, inventoryID, out uint destinationSlot,
                out (GCObject item, byte x, byte y) destinationData))
                return false;

            int activeStackCount = _server.GetStackCount(connId, 0xFFFFFFFF);
            if (activeStackCount <= 0)
                activeStackCount = 1;
            int destinationStackCount = _server.GetStackCount(connId, destinationSlot, inventoryID);
            if (destinationStackCount <= 0)
                destinationStackCount = 1;
            ItemData destinationItemData = AuthoredGameplayCatalog.FindItem(destinationData.item.GCClass);
            int destinationWidth = destinationItemData?.inventoryWidth ?? 1;
            int destinationHeight = destinationItemData?.inventoryHeight ?? 1;
            var removedDestination = _server.GetAndRemoveInventoryItem(connId, destinationSlot, inventoryID);
            if (!removedDestination.HasValue)
                return false;

            _server.SetStackCount(connId, destinationSlot, 0, inventoryID);
            uint activeSlot = _server.GetNextInventorySlot(connId);
            _server.OccupyInventorySlots(connId, x, y, activeWidth, activeHeight, inventoryID);
            _server.TrackInventoryItem(connId, activeSlot, activeItem, x, y, inventoryID);
            _server.SetStackCount(connId, activeSlot, activeStackCount, inventoryID);
            _server.SetStackCount(connId, 0xFFFFFFFF, destinationStackCount);
            PlayerState playerState = _server.GetPlayerState(connId);
            playerState.ActiveItem = destinationData.item;

            if (!_server.SavePlayerInventoryPublic(conn))
            {
                _server.RemoveInventoryItemBySlot(connId, activeSlot, inventoryID);
                _server.FreeInventorySlots(connId, x, y, activeWidth, activeHeight, inventoryID);
                _server.TrackInventoryItem(connId, destinationSlot, destinationData.item, destinationData.x, destinationData.y, inventoryID);
                _server.OccupyInventorySlots(connId, destinationData.x, destinationData.y, destinationWidth, destinationHeight, inventoryID);
                _server.SetStackCount(connId, destinationSlot, destinationStackCount, inventoryID);
                _server.SetStackCount(connId, 0xFFFFFFFF, activeStackCount);
                playerState.ActiveItem = activeItem;
                Debug.LogError($"[UNIT-CONTAINER] swap state=failed phase=save destinationSlot={destinationSlot} container=0x{inventoryID:X2}");
                return false;
            }

            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x35);
            writer.WriteUInt16(componentId);
            writer.WriteByte(0x1F);
            writer.WriteUInt32(destinationSlot);
            _server.WritePlayerEntitySynch(conn, writer);

            writer.WriteByte(0x35);
            writer.WriteUInt16(componentId);
            writer.WriteByte(0x1E);
            writer.WriteByte(inventoryID);
            int activeLevel = activeItem.StoredLevel >= 0 ? activeItem.StoredLevel : Math.Max(1, activeItem.GetItemRequiredLevel());
            activeItem.WriteInitForInventory(writer, x, y, activeSlot, activeLevel, (byte)Math.Min(activeStackCount, byte.MaxValue));
            _server.WritePlayerEntitySynch(conn, writer);

            writer.WriteByte(0x35);
            writer.WriteUInt16(componentId);
            writer.WriteByte(0x28);
            int destinationLevel = destinationData.item.StoredLevel >= 0 ? destinationData.item.StoredLevel : Math.Max(1, destinationData.item.GetItemRequiredLevel());
            destinationData.item.WriteItemData(writer, 0, 0, 0, (byte)Math.Min(destinationStackCount, byte.MaxValue), destinationLevel);
            _server.WritePlayerEntitySynch(conn, writer);
            writer.WriteByte(0x06);
            _server.SendToClient(conn, writer.ToArray());
            Debug.LogError($"[UNIT-CONTAINER] swapped active={activeItem.GCClass} destination={destinationData.item.GCClass} pos=({x},{y}) container=0x{inventoryID:X2} activeSlot={activeSlot} cursorSlot={destinationSlot}");
            return true;
        }

        private void ProcessGetItemLocal(RRConnection conn, LEReader reader, ushort componentId)
        {
            uint index = reader.ReadUInt32();
            Debug.LogError($"[UNIT-CONTAINER] pickup slot={index}");
            string connId = conn.ConnId.ToString();
            PlayerState playerState = _server.GetPlayerState(connId);

            if (playerState.ActiveItem != null)
            {
                Debug.LogError($"[UNIT-CONTAINER] pickup activeItem={playerState.ActiveItem.GCClass} reason=cursor-occupied");
                return;
            }

            byte sourceContainer = _server.FindContainerForSlot(connId, index) ?? (byte)0x0B;
            Debug.LogError($"[UNIT-CONTAINER] pickup sourceContainer=0x{sourceContainer:X2}");

            int storedStackCount = _server.GetStackCount(connId, index, sourceContainer);
            if (storedStackCount <= 0)
                storedStackCount = 1;
            var itemData = _server.GetAndRemoveInventoryItem(connId, index, sourceContainer);
            if (itemData == null)
            {
                Debug.LogError($"[UNIT-CONTAINER] pickup slot={index} container=0x{sourceContainer:X2} reason=missing");
                return;
            }

            GCObject item = itemData.Value.item;
            byte storedX = itemData.Value.x;
            byte storedY = itemData.Value.y;
            int cursorQuantity = storedStackCount;
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x35);
            writer.WriteUInt16(componentId);
            writer.WriteByte(0x1F);
            writer.WriteUInt32(index);
            _server.WritePlayerEntitySynch(conn, writer);
            writer.WriteByte(0x35);
            writer.WriteUInt16(componentId);
            writer.WriteByte(0x28);
            int cursorLevel = item.StoredLevel >= 0 ? item.StoredLevel : Math.Max(1, item.GetItemRequiredLevel());
            item.WriteItemData(writer, 0, 0, 0, (byte)Math.Min(cursorQuantity, byte.MaxValue), cursorLevel);
            _server.WritePlayerEntitySynch(conn, writer);
            writer.WriteByte(0x06);
            playerState.ActiveItem = item;
            _server.SetStackCount(connId, 0xFFFFFFFF, cursorQuantity);
            if (!_server.SavePlayerInventoryPublic(conn))
            {
                playerState.ActiveItem = null;
                _server.SetStackCount(connId, 0xFFFFFFFF, 0);
                _server.TrackInventoryItem(connId, index, item, storedX, storedY, sourceContainer);
                ItemData restoredItemData = AuthoredGameplayCatalog.FindItem(item.GCClass);
                int restoredWidth = restoredItemData?.inventoryWidth ?? 1;
                int restoredHeight = restoredItemData?.inventoryHeight ?? 1;
                _server.OccupyInventorySlots(connId, storedX, storedY, restoredWidth, restoredHeight, sourceContainer);
                _server.SetStackCount(connId, index, storedStackCount, sourceContainer);
                Debug.LogError($"[UNIT-CONTAINER] pickup state=failed phase=save item={item.GCClass} slot={index} container=0x{sourceContainer:X2}");
                return;
            }
            _server.SendToClient(conn, writer.ToArray());
            Debug.LogError($"[UNIT-CONTAINER] cursor item={item.GCClass} count={cursorQuantity}");
        }

        private bool TryReserveConsumableModifierId(out uint modifierId)
        {
            lock (_consumableModifierSync)
            {
                modifierId = 0;
                if (_nextConsumableModifierId < FirstConsumableModifierId || _nextConsumableModifierId > LastConsumableModifierId)
                    return false;
                modifierId = _nextConsumableModifierId++;
                return true;
            }
        }

        private static string BuildConsumableModifierKey(string connectionId, string modifierFamily, uint modifierId, string stackRule)
        {
            return string.IsNullOrWhiteSpace(stackRule)
                ? $"consumable:{connectionId}:{modifierFamily}:{modifierId}"
                : $"consumable:{connectionId}:{modifierFamily}";
        }

        private bool TryReserveConsumableModifierAdmission(string modifierKey)
        {
            if (string.IsNullOrWhiteSpace(modifierKey))
                return false;
            lock (_consumableModifierSync)
            {
                if (_reservedConsumableModifierKeys.Contains(modifierKey)
                    || _pendingConsumableModifierApplies.Any(pending => string.Equals(pending.ModifierKey, modifierKey, StringComparison.OrdinalIgnoreCase)))
                    return false;
                return _reservedConsumableModifierKeys.Add(modifierKey);
            }
        }

        private void ReleaseConsumableModifierAdmission(string modifierKey)
        {
            if (string.IsNullOrWhiteSpace(modifierKey))
                return;
            lock (_consumableModifierSync)
                _reservedConsumableModifierKeys.Remove(modifierKey);
        }

        private bool QueueConsumableModifier(RRConnection conn, PlayerState playerState, uint modifierId, string gcType, string modifierFamily, int hitPointRegenBonus, int manaPointRegenBonus, int strengthBonus, int enduranceBonus, int intellectBonus, int stunResistBonus, ushort durationTicks, bool removeOnDeath, string stackRule)
        {
            if (conn == null || playerState == null || conn.Avatar == null || conn.ModifiersId == 0 || modifierId == 0)
            {
                Debug.LogError($"[CONSUMABLE-MODIFIER] queue reason=missing-runtime conn={conn?.ConnId ?? 0} avatar={conn?.Avatar?.Id ?? 0} modifiers={conn?.ModifiersId ?? 0} type={gcType ?? ""}");
                return false;
            }

            uint avatarEntityId = (uint)Math.Max(0, conn.Avatar.Id);
            string runtimeInstanceKey = RoomRuntime.NormalizeInstanceKey(conn.RuntimeInstanceKey);
            uint receiveTick = SimulationClock.SimulationTick;
            ulong sequence;
            string modifierKey = BuildConsumableModifierKey(conn.ConnId.ToString(), modifierFamily, modifierId, stackRule);
            lock (_consumableModifierSync)
            {
                if (!_reservedConsumableModifierKeys.Remove(modifierKey))
                    return false;
                sequence = ++_nextConsumableModifierSequence;
                _pendingConsumableModifierApplies.Add(new PendingConsumableModifierApply
                {
                    Sequence = sequence,
                    ReceiveTick = receiveTick,
                    Connection = conn,
                    PlayerState = playerState,
                    ConnectionId = conn.ConnId.ToString(),
                    AvatarEntityId = avatarEntityId,
                    RuntimeInstanceKey = runtimeInstanceKey,
                    ModifiersComponentId = conn.ModifiersId,
                    ModifierId = modifierId,
                    ModifierKey = modifierKey,
                    ModifierFamily = modifierFamily,
                    GCType = gcType,
                    HitPointRegenBonus = hitPointRegenBonus,
                    ManaPointRegenBonus = manaPointRegenBonus,
                    StrengthBonus = strengthBonus,
                    EnduranceBonus = enduranceBonus,
                    IntellectBonus = intellectBonus,
                    StunResistBonus = stunResistBonus,
                    DurationTicks = durationTicks,
                    RemoveOnDeath = removeOnDeath,
                    PowerLevel = 0x100,
                    StackRule = stackRule
                });
            }
            conn.MessageQueue.EnqueueDeferred(() => BuildConsumableModifierAddResponse(conn, sequence), componentId: conn.ModifiersId);
            Debug.LogError($"[CONSUMABLE-MODIFIER] queued seq={sequence} receiveTick={receiveTick} entity={avatarEntityId} runtime='{runtimeInstanceKey}' component={conn.ModifiersId} modifier={modifierId} type={gcType} durationTicks={durationTicks}");
            return true;
        }

        private byte[] BuildConsumableModifierAddResponse(RRConnection conn, ulong sequence)
        {
            lock (_consumableModifierSync)
            {
                int pendingIndex = _pendingConsumableModifierApplies.FindIndex(candidate => candidate.Sequence == sequence);
                if (pendingIndex < 0 || conn == null || !conn.IsConnected)
                    return Array.Empty<byte>();
                PendingConsumableModifierApply pending = _pendingConsumableModifierApplies[pendingIndex];
                if (!ReferenceEquals(pending.Connection, conn) || !IsConsumableModifierRuntimeCurrent(pending, pending.AvatarEntityId))
                {
                    _pendingConsumableModifierApplies.RemoveAt(pendingIndex);
                    return Array.Empty<byte>();
                }
                byte[] response = ClientEntityUpdate.Component(
                    pending.ModifiersComponentId,
                    0x00,
                    writer =>
                    {
                        writer.WriteByte(0xFF);
                        writer.WriteCString(pending.GCType);
                        writer.WriteUInt32(pending.ModifierId);
                        writer.WriteByte(0x01);
                        writer.WriteUInt32(pending.PowerLevel);
                        writer.WriteUInt32(pending.DurationTicks);
                        writer.WriteByte(0x01);
                    },
                    writer => _server.WritePlayerEntitySynch(conn, writer));
                pending.ResponseMessage = response;
                return response;
            }
        }

        public void ApplyPendingConsumableModifierAdmissions(RRConnection conn, IReadOnlyList<byte[]> messages, uint simulationApplyTick)
        {
            if (conn == null || messages == null || messages.Count == 0)
                return;
            var admissions = new List<(PendingConsumableModifierApply Pending, int MessageIndex)>();
            lock (_consumableModifierSync)
            {
                for (int messageIndex = 0; messageIndex < messages.Count; messageIndex++)
                {
                    byte[] message = messages[messageIndex];
                    for (int pendingIndex = 0; pendingIndex < _pendingConsumableModifierApplies.Count; pendingIndex++)
                    {
                        PendingConsumableModifierApply pending = _pendingConsumableModifierApplies[pendingIndex];
                        if (!ReferenceEquals(pending.Connection, conn)
                            || pending.ResponseMessage == null
                            || message == null
                            || !message.SequenceEqual(pending.ResponseMessage))
                            continue;
                        admissions.Add((pending, messageIndex));
                        _pendingConsumableModifierApplies.RemoveAt(pendingIndex);
                        break;
                    }
                }
            }
            foreach ((PendingConsumableModifierApply pending, int messageIndex) in admissions)
            {
                if (!IsConsumableModifierRuntimeCurrent(pending, pending.AvatarEntityId))
                {
                    Debug.LogError($"[CONSUMABLE-MODIFIER] drop seq={pending.Sequence} receiveTick={pending.ReceiveTick} applyTick={simulationApplyTick} entity={pending.AvatarEntityId} runtime='{pending.RuntimeInstanceKey}' component={pending.ModifiersComponentId} modifier={pending.ModifierId} reason=stale-epoch");
                    continue;
                }
                if (!ApplyConsumableModifier(pending, simulationApplyTick))
                {
                    _server.QuarantineCommittedPersistenceSyncFailurePublic(conn, "consumable-modifier-admission", new InvalidOperationException("Durable consumable modifier admission failed"));
                    continue;
                }
                Debug.LogError($"[CONSUMABLE-MODIFIER-ADMISSION] seq={pending.Sequence} receiveTick={pending.ReceiveTick} applyTick={simulationApplyTick} entity={pending.AvatarEntityId} messageIndex={messageIndex} phase=client-entity-flush");
            }
        }

        private bool IsConsumableModifierRuntimeCurrent(PendingConsumableModifierApply pending, uint playerEntityId)
        {
            if (pending?.Connection == null || pending.PlayerState == null || !pending.Connection.IsConnected || pending.Connection.Avatar == null)
                return false;
            if (pending.AvatarEntityId != playerEntityId || (uint)Math.Max(0, pending.Connection.Avatar.Id) != pending.AvatarEntityId)
                return false;
            if (!string.Equals(pending.Connection.ConnId.ToString(), pending.ConnectionId, StringComparison.Ordinal))
                return false;
            if (!string.Equals(RoomRuntime.NormalizeInstanceKey(pending.Connection.RuntimeInstanceKey), pending.RuntimeInstanceKey, StringComparison.OrdinalIgnoreCase))
                return false;
            if (pending.Connection.ModifiersId != pending.ModifiersComponentId)
                return false;
            CombatPlayer player = CombatRuntime.Instance.GetPlayer(playerEntityId);
            return player != null
                && ReferenceEquals(player.PlayerState, pending.PlayerState)
                && string.Equals(RoomRuntime.NormalizeInstanceKey(player.InstanceKey), pending.RuntimeInstanceKey, StringComparison.OrdinalIgnoreCase);
        }

        private bool ApplyConsumableModifier(PendingConsumableModifierApply pending, uint simulationTick)
        {
            if (!pending.PlayerState.ShouldAcceptAttributeModifier(pending.ModifierKey, pending.GCType, pending.StackRule, pending.PowerLevel, pending.DurationTicks))
            {
                Debug.LogError($"[CONSUMABLE-MODIFIER] reject seq={pending.Sequence} receiveTick={pending.ReceiveTick} applyTick={simulationTick} entity={pending.AvatarEntityId} modifier={pending.ModifierId} type={pending.GCType} stack={pending.StackRule ?? ""}");
                return false;
            }

            ActiveConsumableModifier replaced = null;
            lock (_consumableModifierSync)
            {
                if (_activeConsumableModifiers.TryGetValue(pending.ModifierKey, out replaced))
                    _activeConsumableModifiers.Remove(pending.ModifierKey);
            }
            if (replaced != null && IsConsumableModifierRuntimeCurrent(replaced.Runtime, replaced.Runtime.AvatarEntityId))
                SendModifierRemove(replaced.Runtime);

            bool applied = pending.PlayerState.ApplyAttributeModifier(
                pending.GCType,
                pending.HitPointRegenBonus,
                pending.ManaPointRegenBonus,
                pending.StrengthBonus,
                pending.EnduranceBonus,
                pending.IntellectBonus,
                pending.StunResistBonus,
                pending.DurationTicks,
                pending.RemoveOnDeath,
                "ActiveItem::doItemEffect@0x00579F60",
                pending.ModifierKey,
                pending.AvatarEntityId,
                pending.GCType,
                pending.GCType,
                pending.PowerLevel,
                pending.StackRule,
                level: 1,
                sourceIsSelf: 1);
            if (!applied)
                return false;

            bool subscribe;
            lock (_consumableModifierSync)
            {
                _activeConsumableModifiers[pending.ModifierKey] = new ActiveConsumableModifier { Runtime = pending };
                subscribe = _subscribedModifierStates.Add(pending.PlayerState);
            }
            if (subscribe)
                pending.PlayerState.OnAttributeModifierRemoved += HandleAttributeModifierRemoved;

            Debug.LogError($"[CONSUMABLE-MODIFIER] apply seq={pending.Sequence} receiveTick={pending.ReceiveTick} applyTick={simulationTick} entity={pending.AvatarEntityId} runtime='{pending.RuntimeInstanceKey}' component={pending.ModifiersComponentId} modifier={pending.ModifierId} type={pending.GCType} durationTicks={pending.DurationTicks}");
            return true;
        }

        private void HandleAttributeModifierRemoved(PlayerState playerState, PlayerState.AttributeModifierRemoval removal)
        {
            if (playerState == null || removal == null || string.IsNullOrWhiteSpace(removal.ModifierKey))
                return;

            ActiveConsumableModifier active = null;
            bool unsubscribe = false;
            lock (_consumableModifierSync)
            {
                if (_activeConsumableModifiers.TryGetValue(removal.ModifierKey, out ActiveConsumableModifier candidate)
                    && ReferenceEquals(candidate.Runtime.PlayerState, playerState))
                    active = candidate;
            }

            if (active != null)
            {
                SavedCharacter savedCharacter = _server.GetSavedCharacterForConn(active.Runtime.Connection);
                if (savedCharacter == null || !CharacterRepository.TryDeleteCharacterModifier(savedCharacter.id, active.Runtime.ModifierId, "consumable-modifier-runtime-remove"))
                {
                    _server.QuarantineCommittedPersistenceSyncFailurePublic(active.Runtime.Connection, "consumable-modifier-remove", new InvalidOperationException("Consumable modifier durable removal failed"));
                    return;
                }
                lock (_consumableModifierSync)
                {
                    if (_activeConsumableModifiers.TryGetValue(removal.ModifierKey, out ActiveConsumableModifier current)
                        && ReferenceEquals(current, active))
                        _activeConsumableModifiers.Remove(removal.ModifierKey);
                    if (!_activeConsumableModifiers.Values.Any(candidate => ReferenceEquals(candidate.Runtime.PlayerState, playerState)))
                        unsubscribe = _subscribedModifierStates.Remove(playerState);
                }
            }
            if (active != null && IsConsumableModifierRuntimeCurrent(active.Runtime, active.Runtime.AvatarEntityId))
                SendModifierRemove(active.Runtime);
            if (unsubscribe)
                playerState.OnAttributeModifierRemoved -= HandleAttributeModifierRemoved;
        }

        private void SendModifierRemove(PendingConsumableModifierApply pending)
        {
            lock (pending.Connection.SendLock)
            {
                byte[] update = ClientEntityUpdate.Component(
                    pending.ModifiersComponentId,
                    0x01,
                    writer => writer.WriteUInt32(pending.ModifierId),
                    writer => _server.WritePlayerEntitySynch(pending.Connection, writer));
                var stream = new LEWriter();
                stream.WriteByte(0x07);
                stream.WriteBytes(update);
                stream.WriteByte(0x06);
                _server.SendToClient(pending.Connection, stream.ToArray());
            }
            Debug.LogError($"[CONSUMABLE-MODIFIER] remove entity={pending.AvatarEntityId} runtime='{pending.RuntimeInstanceKey}' component={pending.ModifiersComponentId} modifier={pending.ModifierId} type={pending.GCType}");
        }

        public bool RestorePersistedConsumableModifiers(RRConnection conn)
        {
            if (conn?.Avatar == null || conn.ModifiersId == 0)
                return false;
            SavedCharacter savedCharacter = _server.GetSavedCharacterForConn(conn);
            PlayerState playerState = _server.GetPlayerState(conn.ConnId.ToString());
            if (savedCharacter == null || playerState == null
                || !CharacterRepository.TryGetCharacterModifiersInRange(savedCharacter.id, FirstConsumableModifierId, LastConsumableModifierId, out List<CharacterRepository.PersistedCharacterModifier> persisted))
                return false;
            string connectionId = conn.ConnId.ToString();
            string runtimeInstanceKey = RoomRuntime.NormalizeInstanceKey(conn.RuntimeInstanceKey);
            var hydration = new List<PendingConsumableModifierApply>();
            var uniqueModifierKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int modifierIndex = 0; modifierIndex < persisted.Count; modifierIndex++)
            {
                CharacterRepository.PersistedCharacterModifier row = persisted[modifierIndex];
                lock (_consumableModifierSync)
                {
                    if (_activeConsumableModifiers.Values.Any(active => active?.Runtime != null
                        && string.Equals(active.Runtime.ConnectionId, connectionId, StringComparison.Ordinal)
                        && active.Runtime.ModifierId == row.ModifierId))
                        continue;
                }
                if (!TryResolvePersistedConsumableModifier(
                    row.GcType,
                    out string modifierFamily,
                    out int hitPointRegenBonus,
                    out int manaPointRegenBonus,
                    out int strengthBonus,
                    out int enduranceBonus,
                    out int intellectBonus,
                    out int stunResistBonus,
                    out bool removeOnDeath,
                    out string stackRule)
                    || row.DurationRemaining == 0 || row.DurationRemaining > ushort.MaxValue)
                    return false;
                var pending = new PendingConsumableModifierApply
                {
                    ReceiveTick = SimulationClock.SimulationTick,
                    Connection = conn,
                    PlayerState = playerState,
                    ConnectionId = connectionId,
                    AvatarEntityId = (uint)conn.Avatar.Id,
                    RuntimeInstanceKey = runtimeInstanceKey,
                    ModifiersComponentId = conn.ModifiersId,
                    ModifierId = row.ModifierId,
                    ModifierFamily = modifierFamily,
                    GCType = row.GcType,
                    HitPointRegenBonus = hitPointRegenBonus,
                    ManaPointRegenBonus = manaPointRegenBonus,
                    StrengthBonus = strengthBonus,
                    EnduranceBonus = enduranceBonus,
                    IntellectBonus = intellectBonus,
                    StunResistBonus = stunResistBonus,
                    DurationTicks = (ushort)row.DurationRemaining,
                    RemoveOnDeath = removeOnDeath,
                    PowerLevel = row.PowerLevel,
                    StackRule = stackRule
                };
                lock (_consumableModifierSync)
                {
                    pending.Sequence = ++_nextConsumableModifierSequence;
                    pending.ModifierKey = BuildConsumableModifierKey(connectionId, modifierFamily, row.ModifierId, stackRule);
                    if (row.ModifierId >= _nextConsumableModifierId)
                        _nextConsumableModifierId = row.ModifierId == LastConsumableModifierId ? LastConsumableModifierId + 1u : row.ModifierId + 1u;
                }
                if (!string.IsNullOrWhiteSpace(stackRule) && !uniqueModifierKeys.Add(pending.ModifierKey))
                    return false;
                hydration.Add(pending);
            }
            for (int hydrationIndex = 0; hydrationIndex < hydration.Count; hydrationIndex++)
            {
                PendingConsumableModifierApply pending = hydration[hydrationIndex];
                if (!ApplyConsumableModifier(pending, SimulationClock.SimulationTick))
                    return false;
            }
            return true;
        }

        private static bool TryResolvePersistedConsumableModifier(
            string gcType,
            out string modifierFamily,
            out int hitPointRegenBonus,
            out int manaPointRegenBonus,
            out int strengthBonus,
            out int enduranceBonus,
            out int intellectBonus,
            out int stunResistBonus,
            out bool removeOnDeath,
            out string stackRule)
        {
            modifierFamily = null;
            hitPointRegenBonus = 0;
            manaPointRegenBonus = 0;
            strengthBonus = 0;
            enduranceBonus = 0;
            intellectBonus = 0;
            stunResistBonus = 0;
            removeOnDeath = false;
            stackRule = null;
            switch ((gcType ?? string.Empty).ToLowerInvariant())
            {
                case "potionpal.healthpotion_noob.modifier":
                    modifierFamily = "health-potion";
                    hitPointRegenBonus = 9;
                    return true;
                case "potionpal.manapotion_noob.modifier":
                    modifierFamily = "mana-potion";
                    manaPointRegenBonus = 9;
                    return true;
                case "items.consumables.consumable_minorhealthpotion.modifier":
                    modifierFamily = "health-potion";
                    hitPointRegenBonus = 8;
                    return true;
                case "items.consumables.consumable_minormanapotion.modifier":
                    modifierFamily = "mana-potion";
                    manaPointRegenBonus = 8;
                    return true;
                case "items.consumables.consumable_majorhealthpotion.modifier":
                case "potionpal.healthpotion_itempack.modifier":
                    modifierFamily = "health-potion";
                    hitPointRegenBonus = 10;
                    return true;
                case "items.consumables.consumable_majormanapotion.modifier":
                case "potionpal.manapotion_itempack.modifier":
                    modifierFamily = "mana-potion";
                    manaPointRegenBonus = 10;
                    return true;
                case "potionpal.dragonjuice_sm.modifier":
                    modifierFamily = "dragonjuice";
                    strengthBonus = 1;
                    enduranceBonus = -1;
                    removeOnDeath = true;
                    stackRule = "UNIQUEBYTYPE";
                    return true;
                case "potionpal.dragonjuice_lg.modifier":
                    modifierFamily = "dragonjuice";
                    strengthBonus = 2;
                    enduranceBonus = -1;
                    removeOnDeath = true;
                    stackRule = "UNIQUEBYTYPE";
                    return true;
                case "potionpal.intbuff_sm.modifier":
                    modifierFamily = "intbuff";
                    removeOnDeath = true;
                    stackRule = "UNIQUEBYTYPE";
                    return true;
                case "potionpal.intbuff_lg.modifier":
                    modifierFamily = "intbuff";
                    intellectBonus = 1;
                    removeOnDeath = true;
                    stackRule = "UNIQUEBYTYPE";
                    return true;
                default:
                    return false;
            }
        }

        public int RebindAndReplayConnectionRuntime(RRConnection conn)
        {
            if (conn?.Avatar == null || conn.ModifiersId == 0)
                return 0;
            string connectionId = conn.ConnId.ToString();
            PlayerState playerState = _server.GetPlayerState(connectionId);
            if (playerState == null)
                return 0;
            string runtimeInstanceKey = RoomRuntime.NormalizeInstanceKey(conn.RuntimeInstanceKey);
            var replay = new List<PendingConsumableModifierApply>();
            List<PlayerState.AttributeModifierSnapshot> snapshots = playerState.GetAttributeModifierSnapshots();
            lock (_consumableModifierSync)
            {
                for (int snapshotIndex = 0; snapshotIndex < snapshots.Count; snapshotIndex++)
                {
                    PlayerState.AttributeModifierSnapshot snapshot = snapshots[snapshotIndex];
                    string modifierKey = snapshot.ModifierKey ?? snapshot.ModifierType;
                    if (!_activeConsumableModifiers.TryGetValue(modifierKey, out ActiveConsumableModifier active)
                        || active?.Runtime == null
                        || !string.Equals(active.Runtime.ConnectionId, connectionId, StringComparison.Ordinal))
                        continue;
                    PendingConsumableModifierApply pending = active.Runtime;
                    pending.Connection = conn;
                    pending.PlayerState = playerState;
                    pending.AvatarEntityId = (uint)conn.Avatar.Id;
                    pending.RuntimeInstanceKey = runtimeInstanceKey;
                    pending.ModifiersComponentId = conn.ModifiersId;
                    pending.DurationTicks = snapshot.RemainingTicks;
                    replay.Add(pending);
                }
            }
            for (int replayIndex = 0; replayIndex < replay.Count; replayIndex++)
                SendModifierAdd(replay[replayIndex]);
            Debug.LogError($"[CONSUMABLE-MODIFIER] replay conn={conn.ConnId} count={replay.Count} runtime='{runtimeInstanceKey}' component={conn.ModifiersId}");
            return replay.Count;
        }

        private void SendModifierAdd(PendingConsumableModifierApply pending)
        {
            lock (pending.Connection.SendLock)
            {
                byte[] update = ClientEntityUpdate.Component(
                    pending.ModifiersComponentId,
                    0x00,
                    writer =>
                    {
                        writer.WriteByte(0xFF);
                        writer.WriteCString(pending.GCType);
                        writer.WriteUInt32(pending.ModifierId);
                        writer.WriteByte(0x01);
                        writer.WriteUInt32(pending.PowerLevel);
                        writer.WriteUInt32(pending.DurationTicks);
                        writer.WriteByte(0x01);
                    },
                    writer => _server.WritePlayerEntitySynch(pending.Connection, writer));
                var stream = new LEWriter();
                stream.WriteByte(0x07);
                stream.WriteBytes(update);
                stream.WriteByte(0x06);
                _server.SendToClient(pending.Connection, stream.ToArray());
            }
        }

        public IReadOnlyList<uint> GetRemoveOnDeathModifierIds(RRConnection conn)
        {
            if (conn == null)
                return Array.Empty<uint>();
            string connectionId = conn.ConnId.ToString();
            lock (_consumableModifierSync)
            {
                return _activeConsumableModifiers.Values
                    .Where(active => active?.Runtime != null
                        && active.Runtime.RemoveOnDeath
                        && string.Equals(active.Runtime.ConnectionId, connectionId, StringComparison.Ordinal))
                    .Select(active => active.Runtime.ModifierId)
                    .Concat(_pendingConsumableModifierApplies
                        .Where(pending => pending != null
                            && pending.RemoveOnDeath
                            && string.Equals(pending.ConnectionId, connectionId, StringComparison.Ordinal))
                        .Select(pending => pending.ModifierId))
                    .Distinct()
                    .ToList();
            }
        }

        public void CommitRemoveOnDeathModifierRuntime(RRConnection conn, IReadOnlyCollection<uint> modifierIds)
        {
            if (conn == null || modifierIds == null || modifierIds.Count == 0)
                return;
            string connectionId = conn.ConnId.ToString();
            var ids = new HashSet<uint>(modifierIds);
            lock (_consumableModifierSync)
            {
                _pendingConsumableModifierApplies.RemoveAll(pending => pending != null
                    && pending.RemoveOnDeath
                    && string.Equals(pending.ConnectionId, connectionId, StringComparison.Ordinal)
                    && ids.Contains(pending.ModifierId));
            }
        }

        public bool TryCaptureConnectionRuntimeDurations(RRConnection conn, out Dictionary<uint, uint> durations)
        {
            durations = new Dictionary<uint, uint>();
            if (conn == null)
                return false;
            string connectionId = conn.ConnId.ToString();
            lock (_consumableModifierSync)
                if (!_activeConsumableModifiers.Values.Any(active => active?.Runtime != null
                    && string.Equals(active.Runtime.ConnectionId, connectionId, StringComparison.Ordinal)))
                    return true;
            PlayerState playerState = _server.GetPlayerState(conn.ConnId.ToString());
            if (playerState == null)
                return false;
            Dictionary<string, PlayerState.AttributeModifierSnapshot> snapshots;
            try
            {
                snapshots = playerState.GetAttributeModifierSnapshots()
                    .Where(snapshot => snapshot != null && !string.IsNullOrWhiteSpace(snapshot.ModifierKey))
                    .ToDictionary(snapshot => snapshot.ModifierKey, snapshot => snapshot, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
            lock (_consumableModifierSync)
            {
                foreach (ActiveConsumableModifier active in _activeConsumableModifiers.Values)
                {
                    PendingConsumableModifierApply runtime = active?.Runtime;
                    if (runtime == null
                        || !string.Equals(runtime.ConnectionId, connectionId, StringComparison.Ordinal))
                        continue;
                    if (!snapshots.TryGetValue(runtime.ModifierKey, out PlayerState.AttributeModifierSnapshot snapshot))
                        return false;
                    if (snapshot.RemainingTicks == 0)
                        continue;
                    durations[runtime.ModifierId] = snapshot.RemainingTicks;
                }
            }
            return true;
        }

        public bool TryPersistConnectionRuntime(RRConnection conn, string source)
        {
            if (!TryCaptureConnectionRuntimeDurations(conn, out Dictionary<uint, uint> durations))
                return false;
            if (durations.Count == 0)
                return true;
            SavedCharacter savedCharacter = _server.GetSavedCharacterForConn(conn);
            if (savedCharacter == null)
                return false;
            return CharacterRepository.TryUpdateCharacterModifierDurations(savedCharacter.id, durations, source ?? "consumable-modifier-checkpoint");
        }

        public void ClearConnectionRuntime(RRConnection conn, string source, bool preserveAttributeModifiers = false)
        {
            if (conn == null)
                return;
            string connectionId = conn.ConnId.ToString();
            if (preserveAttributeModifiers)
            {
                lock (_consumableModifierSync)
                    _pendingConsumableModifierApplies.RemoveAll(pending => string.Equals(pending.ConnectionId, connectionId, StringComparison.Ordinal));
                Debug.LogError($"[CONSUMABLE-MODIFIER] preserve conn={conn.ConnId} source={source ?? "unknown"}");
                return;
            }
            var states = new HashSet<PlayerState>();
            var unsubscribed = new List<PlayerState>();
            PlayerState currentState = _server.GetPlayerState(connectionId);
            if (currentState != null)
                states.Add(currentState);
            lock (_consumableModifierSync)
            {
                _pendingConsumableModifierApplies.RemoveAll(pending => string.Equals(pending.ConnectionId, connectionId, StringComparison.Ordinal));
                foreach (KeyValuePair<string, ActiveConsumableModifier> entry in _activeConsumableModifiers.ToArray())
                {
                    if (!string.Equals(entry.Value.Runtime.ConnectionId, connectionId, StringComparison.Ordinal))
                        continue;
                    states.Add(entry.Value.Runtime.PlayerState);
                    _activeConsumableModifiers.Remove(entry.Key);
                }
                foreach (PlayerState state in states)
                {
                    if (_activeConsumableModifiers.Values.Any(candidate => ReferenceEquals(candidate.Runtime.PlayerState, state)))
                        continue;
                    if (_subscribedModifierStates.Remove(state))
                        unsubscribed.Add(state);
                }
            }

            foreach (PlayerState state in unsubscribed)
                state.OnAttributeModifierRemoved -= HandleAttributeModifierRemoved;
            foreach (PlayerState state in states)
                state.ClearEntityEpochAttributeModifiers();
            Debug.LogError($"[CONSUMABLE-MODIFIER] purge conn={conn.ConnId} states={states.Count} source={source ?? "unknown"}");
        }

        private void ProcessDropItem(RRConnection conn, LEReader reader, ushort componentId)
        {
            string connId = conn.ConnId.ToString();
            PlayerState playerState = _server.GetPlayerState(connId);
            GCObject item = playerState?.ActiveItem;

            if (item == null)
            {
                Debug.LogError("[DROP] reason=no-active-item");
                return;
            }

            int cursorQty = _server.GetStackCount(connId, 0xFFFFFFFF);
            if (cursorQty <= 0) cursorQty = 1;
            if (!_server.DropInventoryItem(conn, componentId, item, playerState.Level, cursorQty))
                return;
            _server.SetStackCount(connId, 0xFFFFFFFF, 0);
            playerState.ActiveItem = null;
        }
    }
}
