using DungeonRunners.Utilities;
using DungeonRunners.Engine;
using DungeonRunners.Data;
using DungeonRunners.Gameplay;
using System;
using System.Collections.Generic;

namespace DungeonRunners.Networking
{
    public class Equipment
    {
        private GameServer _server;
        private readonly List<PendingEquipmentAdmission> _pendingAdmissions = new List<PendingEquipmentAdmission>();

        private sealed class PendingEquipmentAdmission
        {
            public RRConnection Connection;
            public GCObject Avatar;
            public PlayerState PlayerState;
            public string RuntimeInstanceKey;
            public Dictionary<uint, GCObject> Items;
            public List<byte[]> Updates;
            public byte[] ResponseMessage;
        }

        public Equipment(GameServer server)
        {
            _server = server;
        }

        public void ProcessRequest(RRConnection conn, LEReader reader, ushort componentId, byte subMessage)
        {
            Debug.LogError($"[EQUIPMENT] update component=0x{componentId:X4} sub=0x{subMessage:X2}");

            switch (subMessage)
            {
                case 0x21:
                    Debug.LogError($"[EQUIPMENT] query sub=0x21 remaining={reader.Remaining}");
                    List<byte> bytes21 = new List<byte>();
                    while (reader.Remaining > 0)
                    {
                        byte b = reader.ReadByte();
                        bytes21.Add(b);
                    }
                    if (bytes21.Count > 0)
                        Debug.LogError($"[EQUIPMENT] queryData sub=0x21 hex={BitConverter.ToString(bytes21.ToArray())}");
                    break;

                case 0x22:
                    Debug.LogError($"[EQUIPMENT] position sub=0x22 remaining={reader.Remaining}");
                    List<byte> bytes22 = new List<byte>();
                    while (reader.Remaining > 0)
                    {
                        byte b = reader.ReadByte();
                        bytes22.Add(b);
                    }
                    if (bytes22.Count > 0)
                        Debug.LogError($"[EQUIPMENT] positionData sub=0x22 hex={BitConverter.ToString(bytes22.ToArray())}");
                    break;

                case 0x28:
                    ProcessEquipItem(conn, reader, componentId);
                    break;

                case 0x29:
                    ProcessUnEquipItem(conn, reader, componentId);
                    break;

                default:
                    Debug.LogError($"[EQUIPMENT] sub=0x{subMessage:X2} reason=unhandled");
                    break;
            }
        }

        private void ProcessUnEquipItem(RRConnection conn, LEReader reader, ushort componentId)
        {
            uint slot = reader.ReadUInt32();
            Debug.LogError($"[EQUIPMENT] unequip slot=0x{slot:X2}");
            PlayerState playerState = _server.GetPlayerState(conn.ConnId.ToString());
            if (playerState == null || playerState.ActiveItem != null)
            {
                Debug.LogError($"[EQUIPMENT] unequip slot={slot} reason={(playerState == null ? "missing-player-state" : "cursor-occupied")}");
                return;
            }
            GCObject equippedItem = GetEquippedItemAtSlot(conn, slot);
            if (equippedItem == null)
            {
                Debug.LogError($"[EQUIPMENT] unequip slot={slot} reason=empty");
                return;
            }
            Debug.LogError($"[EQUIPMENT] unequip item={equippedItem.GCClass}");
            Debug.LogError($"[EQUIPMENT] entitySynchInfoHP={playerState.EntitySynchInfoHP}");
            ushort unitContainerComponentId = GetUnitContainerComponentId(conn);
            ushort manipulatorsComponentId = GetManipulatorsComponentId(conn);
            Debug.LogError($"[EQUIPMENT] equipmentId=0x{componentId:X4}");
            Debug.LogError($"[EQUIPMENT] unitContainerId=0x{unitContainerComponentId:X4}");
            Debug.LogError($"[EQUIPMENT] manipulatorsId=0x{manipulatorsComponentId:X4}");
            uint? previousTargetSlot = equippedItem.TargetSlot;
            _server.RemoveEquippedItem(conn.ConnId.ToString(), slot);
            playerState.ActiveItem = equippedItem;
            _server.SetStackCount(conn.ConnId.ToString(), 0xFFFFFFFF, 1);
            if (!_server.SavePlayerInventoryPublic(conn))
            {
                playerState.ActiveItem = null;
                _server.SetStackCount(conn.ConnId.ToString(), 0xFFFFFFFF, 0);
                equippedItem.TargetSlot = previousTargetSlot;
                _server.TrackEquippedItem(conn.ConnId.ToString(), slot, equippedItem);
                Debug.LogError($"[EQUIPMENT] unequip slot={slot} state=rollback reason=save-failed");
                return;
            }
            try
            {
                var updates = new List<byte[]>();
                var writer = new LEWriter();
                writer.WriteByte(0x35);
                writer.WriteUInt16(componentId);
                writer.WriteByte(0x29);
                writer.WriteUInt32(slot);
                updates.Add(writer.ToArray());
                writer = new LEWriter();
                writer.WriteByte(0x35);
                writer.WriteUInt16(unitContainerComponentId);
                writer.WriteByte(0x28);
                int cursorLevel = equippedItem.StoredLevel >= 0 ? equippedItem.StoredLevel : Math.Max(1, equippedItem.GetItemRequiredLevel());
                equippedItem.WriteItemData(writer, 0, 0, 0, 1, cursorLevel);
                updates.Add(writer.ToArray());
                writer = new LEWriter();
                writer.WriteByte(0x35);
                writer.WriteUInt16(manipulatorsComponentId);
                writer.WriteByte(0x01);
                writer.WriteUInt32(slot);
                updates.Add(writer.ToArray());
                QueueEquipmentResponse(conn, componentId, updates);
                Debug.LogError($"[EQUIPMENT] unequipped slot={slot} activeItem={equippedItem.GCClass} durable=True");
            }
            catch (Exception ex)
            {
                _server.QuarantineCommittedPersistenceSyncFailurePublic(conn, "equipment-unequip-materialization", ex);
            }
        }

        private void ProcessEquipItem(RRConnection conn, LEReader reader, ushort componentId)
        {
            uint slot = reader.ReadUInt32();
            Debug.LogError($"[EQUIPMENT] equip slot=0x{slot:X2}");
            PlayerState playerState = _server.GetPlayerState(conn.ConnId.ToString());
            if (playerState == null)
            {
                Debug.LogError("[EQUIPMENT] equip reason=missing-player-state");
                return;
            }
            GCObject item = playerState.ActiveItem;
            if (item == null)
            {
                Debug.LogError("[EQUIPMENT] equip reason=no-active-item");
                return;
            }
            if (item.GCClass.StartsWith("QuestItemPAL", StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogError($"[EQUIPMENT] equip item={item.GCClass} reason=quest-item");
                return;
            }
            uint? previousTargetSlot = item.TargetSlot;
            item.TargetSlot = null;
            uint correctSlot = item.GetEquipmentSlotFromGCClass();
            if (slot != correctSlot)
            {
                if (correctSlot == 10 && slot == 11 && !IsTwoHandedWeapon(item))
                {
                    Debug.LogError($"[EQUIPMENT] equip item={item.GCClass} slot=11 reason=dual-wield");
                }
                else if ((correctSlot == 3 || correctSlot == 4) && (slot == 3 || slot == 4))
                {
                    Debug.LogError($"[EQUIPMENT] equip item={item.GCClass} slot={slot} reason=ring-slot");
                }
                else
                {
                    Debug.LogError($"[EQUIPMENT] equip item={item.GCClass} slot={slot} correctSlot={correctSlot} reason=slot-mismatch");
                    item.TargetSlot = previousTargetSlot;
                    return;
                }
            }
            Debug.LogError($"[EQUIPMENT] equip item={item.GCClass} slot={slot} slotValid=True");

            {
                int itemLevel = item.StoredLevel >= 0 ? item.StoredLevel : RPGSettings.GetItemLevel(item.GCClass);
                int requiredLevel = Math.Max(0, Math.Min(100, itemLevel - 5));

                if (playerState.Level < requiredLevel)
                {
                    Debug.LogError($"[EQUIPMENT] equip item={item.GCClass} requiredLevel={requiredLevel} playerLevel={playerState.Level} storedLevel={item.StoredLevel} reason=level");
                    item.TargetSlot = previousTargetSlot;
                    return;
                }
                Debug.LogError($"[EQUIPMENT] levelCheck playerLevel={playerState.Level} requiredLevel={requiredLevel} storedLevel={item.StoredLevel}");

                if (RequiresMembership(item) && _server.IsPlayerFreePublic(conn.LoginName))
                {
                    Debug.LogError($"[EQUIPMENT] equip item={item.GCClass} reason=membership");
                    item.TargetSlot = previousTargetSlot;
                    return;
                }
            }

            {
                bool is2HWeapon = IsTwoHandedWeapon(item);

                if (is2HWeapon && slot == 10)
                {
                    GCObject equippedOffhand = GetEquippedItemAtSlot(conn, 11);
                    if (equippedOffhand != null)
                    {
                        Debug.LogError($"[EQUIPMENT] equip item={item.GCClass} offhand={equippedOffhand.GCClass} reason=two-hand-offhand");
                        item.TargetSlot = previousTargetSlot;
                        return;
                    }
                }
                else if (slot == 11)
                {
                    GCObject equippedWeapon = GetEquippedItemAtSlot(conn, 10);
                    if (equippedWeapon != null && IsTwoHandedWeapon(equippedWeapon))
                    {
                        Debug.LogError($"[EQUIPMENT] equip item={item.GCClass} weapon={equippedWeapon.GCClass} reason=offhand-two-hand");
                        item.TargetSlot = previousTargetSlot;
                        return;
                    }
                }
            }

            uint itemCorrectSlot = item.GetEquipmentSlotFromGCClass();
            if (itemCorrectSlot == 10 && slot == 11)
            {
                item.TargetSlot = 11;
                Debug.LogError($"[EQUIPMENT] targetSlot=11 item={item.GCClass}");
            }
            else if (itemCorrectSlot == 10 && slot == 10)
            {
                item.TargetSlot = null;
                Debug.LogError($"[EQUIPMENT] targetSlot=primary item={item.GCClass}");
            }

            GCObject existingItem = GetEquippedItemAtSlot(conn, slot);
            Debug.LogError($"[EQUIPMENT] equip item={item.GCClass}");

            if (existingItem != null)
            {
                Debug.LogError($"[EQUIPMENT] equip slot={slot} existing={existingItem.GCClass} swap=True");
            }
            int previousCursorCount = _server.GetStackCount(conn.ConnId.ToString(), 0xFFFFFFFF);
            _server.TrackEquippedItem(conn.ConnId.ToString(), slot, item);
            if (existingItem != null)
            {
                playerState.ActiveItem = existingItem;
                _server.SetStackCount(conn.ConnId.ToString(), 0xFFFFFFFF, 1);
            }
            else
            {
                playerState.ActiveItem = null;
                _server.SetStackCount(conn.ConnId.ToString(), 0xFFFFFFFF, 0);
            }
            if (!_server.SavePlayerInventoryPublic(conn))
            {
                _server.RemoveEquippedItem(conn.ConnId.ToString(), slot);
                if (existingItem != null)
                    _server.TrackEquippedItem(conn.ConnId.ToString(), slot, existingItem);
                item.TargetSlot = previousTargetSlot;
                playerState.ActiveItem = item;
                _server.SetStackCount(conn.ConnId.ToString(), 0xFFFFFFFF, previousCursorCount);
                Debug.LogError($"[EQUIPMENT] equip slot={slot} state=rollback reason=save-failed");
                return;
            }
            try
            {
                ushort unitContainerComponentId = GetUnitContainerComponentId(conn);
                ushort manipulatorsComponentId = GetManipulatorsComponentId(conn);
                int equippedItemLevel = item.StoredLevel >= 0 ? item.StoredLevel : Math.Max(1, item.GetItemRequiredLevel());
                var updates = new List<byte[]>();
                var writer = new LEWriter();
                if (existingItem != null)
                {
                    writer.WriteByte(0x35);
                    writer.WriteUInt16(componentId);
                    writer.WriteByte(0x29);
                    writer.WriteUInt32(slot);
                    updates.Add(writer.ToArray());
                    writer = new LEWriter();
                    writer.WriteByte(0x35);
                    writer.WriteUInt16(manipulatorsComponentId);
                    writer.WriteByte(0x01);
                    writer.WriteUInt32(slot);
                    updates.Add(writer.ToArray());
                    writer = new LEWriter();
                }
                writer.WriteByte(0x35);
                writer.WriteUInt16(componentId);
                writer.WriteByte(0x28);
                item.WriteItemData(writer, slot, 0, 0, 1, equippedItemLevel);
                updates.Add(writer.ToArray());
                writer = new LEWriter();
                writer.WriteByte(0x35);
                writer.WriteUInt16(unitContainerComponentId);
                if (existingItem != null)
                {
                    writer.WriteByte(0x28);
                    int existingItemLevel = existingItem.StoredLevel >= 0 ? existingItem.StoredLevel : Math.Max(1, existingItem.GetItemRequiredLevel());
                    existingItem.WriteItemData(writer, 0, 0, 0, 1, existingItemLevel);
                }
                else
                {
                    writer.WriteByte(0x29);
                }
                updates.Add(writer.ToArray());
                writer = new LEWriter();
                writer.WriteByte(0x35);
                writer.WriteUInt16(manipulatorsComponentId);
                writer.WriteByte(0x00);
                item.WriteItemData(writer, slot, 0, 0, 1, equippedItemLevel);
                updates.Add(writer.ToArray());
                QueueEquipmentResponse(conn, componentId, updates);
                if (existingItem != null)
                    Debug.LogError($"[EQUIPMENT] equipped={item.GCClass} activeItem={existingItem.GCClass} swap=True durable=True");
                else
                    Debug.LogError($"[EQUIPMENT] equipped={item.GCClass} slot={slot} durable=True");
            }
            catch (Exception ex)
            {
                _server.QuarantineCommittedPersistenceSyncFailurePublic(conn, "equipment-equip-materialization", ex);
            }
        }

        private bool IsAdmissionCurrent(PendingEquipmentAdmission admission)
        {
            RRConnection conn = admission.Connection;
            return conn != null
                && conn.IsConnected
                && !conn.PortalClientEntityEpochClosing
                && admission.Avatar != null
                && ReferenceEquals(conn.Avatar, admission.Avatar)
                && _server.IsCurrentPlayerState(conn.ConnId.ToString(), admission.PlayerState)
                && string.Equals(conn.RuntimeInstanceKey, admission.RuntimeInstanceKey, StringComparison.OrdinalIgnoreCase);
        }

        private void QueueEquipmentResponse(RRConnection conn, ushort componentId, List<byte[]> updates)
        {
            var admission = new PendingEquipmentAdmission
            {
                Connection = conn,
                Avatar = conn.Avatar,
                PlayerState = _server.GetPlayerState(conn.ConnId.ToString()),
                RuntimeInstanceKey = conn.RuntimeInstanceKey,
                Items = _server.GetAllEquippedItems(conn.ConnId.ToString()),
                Updates = updates
            };
            lock (_pendingAdmissions)
            {
                _pendingAdmissions.RemoveAll(pending => !IsAdmissionCurrent(pending));
                _pendingAdmissions.Add(admission);
            }
            conn.MessageQueue.EnqueueDeferred(() => BuildEquipmentResponse(admission), componentId: componentId);
        }

        private byte[] BuildEquipmentResponse(PendingEquipmentAdmission admission)
        {
            lock (_pendingAdmissions)
            {
                if (!_pendingAdmissions.Contains(admission) || !IsAdmissionCurrent(admission))
                {
                    _pendingAdmissions.Remove(admission);
                    return Array.Empty<byte>();
                }
                try
                {
                    var writer = new LEWriter();
                    foreach (byte[] update in admission.Updates)
                    {
                        writer.WriteBytes(update);
                        WriteEquipmentEntitySynch(writer, admission.Connection);
                    }
                    admission.ResponseMessage = writer.ToArray();
                    return admission.ResponseMessage;
                }
                catch (Exception ex)
                {
                    _pendingAdmissions.Remove(admission);
                    _server.QuarantineCommittedPersistenceSyncFailurePublic(admission.Connection, "equipment-response", ex);
                    return Array.Empty<byte>();
                }
            }
        }

        public void ApplyPendingAdmissions(RRConnection conn, IReadOnlyList<byte[]> messages)
        {
            if (conn == null || messages == null || messages.Count == 0)
                return;
            lock (_pendingAdmissions)
            {
                _pendingAdmissions.RemoveAll(pending => !IsAdmissionCurrent(pending));
                foreach (byte[] message in messages)
                {
                    int index = _pendingAdmissions.FindIndex(pending =>
                        ReferenceEquals(pending.Connection, conn)
                        && pending.ResponseMessage != null
                        && ReferenceEquals(pending.ResponseMessage, message));
                    if (index < 0)
                        continue;
                    PendingEquipmentAdmission admission = _pendingAdmissions[index];
                    _pendingAdmissions.RemoveAt(index);
                    try
                    {
                        _server.CalculateEquipmentBonuses(conn.ConnId.ToString(), conn.Avatar, admission.Items);
                    }
                    catch (Exception ex)
                    {
                        _server.QuarantineCommittedPersistenceSyncFailurePublic(conn, "equipment-admission", ex);
                        return;
                    }
                }
            }
        }

        public void ClearPendingAdmissions(RRConnection conn)
        {
            lock (_pendingAdmissions)
                _pendingAdmissions.RemoveAll(pending => ReferenceEquals(pending.Connection, conn));
        }

        private static bool IsTwoHandedWeapon(GCObject item)
        {
            if (item == null) return false;
            if (GCObject.TryResolveWeaponClass(item.GCClass, out string weaponClass))
                return IsTwoHandedWeaponClass(weaponClass);
            string gcLow = item.GCClass?.ToLowerInvariant() ?? string.Empty;
            return gcLow.Contains("2h");
        }

        private static bool IsTwoHandedWeaponClass(string weaponClass)
        {
            string value = weaponClass?.Trim().ToUpperInvariant() ?? string.Empty;
            return value == "2HRANGED"
                || value == "2HMELEE"
                || value == "2HMACE"
                || value == "2HSWORD"
                || value == "2HAXE"
                || value == "2HCANNON"
                || value == "2HCROSSBOW"
                || value == "2HBOW"
                || value == "2HGUN"
                || value == "POLEARM";
        }

        private static bool RequiresMembership(GCObject item)
        {
            return item?.GetRequiresMembership() == true;
        }

        private void WriteEquipmentEntitySynch(LEWriter writer, RRConnection conn)
        {
            if (!_server.WritePlayerEntitySynch(conn, writer))
                throw new InvalidOperationException("Equipment entity synchronization failed");
        }

        private GCObject GetEquippedItemAtSlot(RRConnection conn, uint slot)
        {
            return _server.GetEquippedItem(conn.ConnId.ToString(), slot);
        }

        private ushort GetUnitContainerComponentId(RRConnection conn)
        {
            return _server.GetUnitContainerComponentId(conn.ConnId.ToString());
        }

        private ushort GetManipulatorsComponentId(RRConnection conn)
        {
            return _server.GetManipulatorsComponentId(conn.ConnId.ToString());
        }
    }
}
