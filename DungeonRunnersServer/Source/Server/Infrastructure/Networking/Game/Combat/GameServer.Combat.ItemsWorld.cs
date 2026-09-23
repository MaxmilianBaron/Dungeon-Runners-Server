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

        private readonly HashSet<ushort> _droppedItemAcquisitions = new HashSet<ushort>();

        private bool TryReserveDroppedItemAcquisition(ushort entityId, DroppedItemInfo expected)
        {
            lock (_droppedItems)
            {
                return expected != null
                    && _droppedItems.TryGetValue(entityId, out DroppedItemInfo current)
                    && ReferenceEquals(current, expected)
                    && _droppedItemAcquisitions.Add(entityId);
            }
        }

        private void ReleaseDroppedItemAcquisition(ushort entityId)
        {
            lock (_droppedItems)
                _droppedItemAcquisitions.Remove(entityId);
        }

        private void CompleteDroppedItemAcquisition(ushort entityId, DroppedItemInfo expected, string source)
        {
            bool removed;
            DroppedItemInfo removedInfo;
            lock (_droppedItems)
            {
                removed = _droppedItems.TryGetValue(entityId, out DroppedItemInfo current)
                    && ReferenceEquals(current, expected)
                    && RemoveDroppedItemWithLifetime(entityId, out removedInfo);
                if (removed)
                    _droppedItemAcquisitions.Remove(entityId);
            }
            if (expected?.DbId > 0)
                _dbIdToEntityId.Remove(expected.DbId);
            if (!removed)
                Debug.LogError($"[PICKUP] state=committed phase=world-remove-mismatch source={source ?? "unknown"} entity=0x{entityId:X4} dbId={expected?.DbId ?? 0}");
        }

        private void HandleItemPickup(RRConnection conn, ushort componentId, ushort targetEntityID, byte responseId, byte sessionID)
        {
            if (_droppedItems.TryGetValue(targetEntityID, out var goldCheck) && goldCheck.GoldAmount > 0)
            {
                HandleItemRightClickPickup(conn, componentId, targetEntityID, responseId, sessionID);
                return;
            }

            Debug.LogError("");
            Debug.LogError("                              ITEM PICKUP START                                ");
            Debug.LogError("");

            bool isQuestPickup = false;
            bool isGoldPickup = false;
            uint goldPickupAmount = 0;
            if (!_droppedItems.TryGetValue(targetEntityID, out var preInfo)
                || !DroppedItemMatchesConnection(conn, preInfo))
            {
                Debug.LogError($"[PICKUP] state=rejected target=0x{targetEntityID:X4} reason=missing-or-instance");
                return;
            }
            if (!CanAcquireDroppedItem(conn, targetEntityID, preInfo))
            {
                Debug.LogError($"[PICKUP] state=rejected target=0x{targetEntityID:X4} reason=owner owner={preInfo.OwnerCharacterId} requester={GetCharSqlId(conn)}");
                return;
            }
            isQuestPickup = preInfo.IsQuestItem;
            isGoldPickup = preInfo.IsGoldDrop;
            goldPickupAmount = preInfo.GoldAmount;
            if (isGoldPickup && goldPickupAmount > 0)
            {
                if (!TryAcquireDroppedGoldTransaction(conn, targetEntityID, goldPickupAmount, "gold-pickup", out _, out _))
                {
                    Debug.LogError($"[PICKUP] state=failed phase=gold-transaction target=0x{targetEntityID:X4}");
                    return;
                }
                var goldWriter = new LEWriter();
                goldWriter.WriteByte(0x07);
                goldWriter.WriteByte(0x05);
                goldWriter.WriteUInt16(targetEntityID);
                if (conn.UnitContainerId != 0)
                {
                    goldWriter.WriteByte(0x35);
                    goldWriter.WriteUInt16(conn.UnitContainerId);
                    goldWriter.WriteByte(0x20);
                    goldWriter.WriteUInt32(goldPickupAmount);
                    goldWriter.WriteByte(0x00);
                    goldWriter.WriteUInt32(0x00000000);
                    goldWriter.WriteByte(0x01);
                    WritePlayerEntitySynch(conn, goldWriter);
                }
                goldWriter.WriteByte(0x06);
                SendToClient(conn, goldWriter.ToArray());
                foreach (var other in GetConnectionInsertionOrderSnapshot())
                {
                    if (other == conn || !other.IsSpawned || !DroppedItemMatchesConnection(other, preInfo)) continue;
                    SendDespawnEntity(other, targetEntityID);
                }
                return;
            }
            if (!TryReserveDroppedItemAcquisition(targetEntityID, preInfo))
            {
                Debug.LogError($"[PICKUP] state=rejected target=0x{targetEntityID:X4} reason=acquisition-in-progress");
                return;
            }
            int droppedQty = Math.Max(1, preInfo.Quantity);
            GCObject item = preInfo.Item;
            if (item == null)
            {
                ReleaseDroppedItemAcquisition(targetEntityID);
                Debug.LogError($"[PICKUP] state=rejected target=0x{targetEntityID:X4} reason=missing-item");
                return;
            }

            Debug.LogError($"[PICKUP] Found item: {item.GCClass} isQuestItem={isQuestPickup} isGold={isGoldPickup}");

            string connId = conn.ConnId.ToString();
            PlayerState playerState = GetPlayerState(connId);
            if (playerState == null || playerState.ActiveItem != null)
            {
                ReleaseDroppedItemAcquisition(targetEntityID);
                Debug.LogError($"[PICKUP] state=rejected target=0x{targetEntityID:X4} reason=cursor-occupied");
                return;
            }
            ushort unitContainerId = GetUnitContainerComponentId(connId);

            playerState.ActiveItem = item;
            SetStackCount(connId, 0xFFFFFFFF, droppedQty);
            QuestProgressMutation questMutation = StageQuestItemAcquisition(conn, item.GCClass, droppedQty);
            if (!TrySaveFullCharacterSnapshotConsumingDrop(conn, preInfo.DbId, "item-pickup-cursor"))
            {
                RollbackQuestItemAcquisition(questMutation);
                playerState.ActiveItem = null;
                SetStackCount(connId, 0xFFFFFFFF, 0);
                ReleaseDroppedItemAcquisition(targetEntityID);
                Debug.LogError($"[PICKUP] state=failed phase=commit target=0x{targetEntityID:X4} dbId={preInfo.DbId}");
                return;
            }
            CompleteDroppedItemAcquisition(targetEntityID, preInfo, "item-pickup-cursor");

            Debug.LogError($"[PICKUP] unitBehaviorId=0x{componentId:X4}");
            Debug.LogError($"[PICKUP] unitContainerId=0x{unitContainerId:X4}");

            var writer = new LEWriter();
            writer.WriteByte(0x07);

            writer.WriteByte(0x35);
            writer.WriteUInt16(componentId);
            writer.WriteByte(0x01);
            writer.WriteByte(responseId);
            writer.WriteByte(0x06);
            writer.WriteByte(sessionID);
            writer.WriteUInt16(targetEntityID);
            WritePlayerEntitySynch(conn, writer);
            Debug.LogError($"[PICKUP]  Wrote Activate response");

            writer.WriteByte(0x05);
            writer.WriteUInt16(targetEntityID);
            Debug.LogError($"[PICKUP]  Wrote Remove entity {targetEntityID}");

            writer.WriteByte(0x35);
            writer.WriteUInt16(unitContainerId);
            writer.WriteByte(0x28);
            int pickupLevel = item.StoredLevel >= 0 ? item.StoredLevel : Math.Max(1, item.GetItemRequiredLevel());
            item.WriteItemData(writer, 0, 0, 0, (byte)Math.Clamp(droppedQty, 1, byte.MaxValue), pickupLevel);
            WritePlayerEntitySynch(conn, writer);

            writer.WriteByte(0x06);

            byte[] packet = writer.ToArray();
            Debug.LogError($"[PICKUP] packetBytes={packet.Length}");
            Debug.LogError($"[PICKUP] hex={BitConverter.ToString(packet)}");

            SendToClient(conn, packet);

            foreach (var other in GetConnectionInsertionOrderSnapshot())
            {
                if (other == conn) continue;
                if (!other.IsSpawned) continue;
                if (other.CurrentZoneGcType != conn.CurrentZoneGcType) continue;
                if (other.InstanceId != conn.InstanceId) continue;
                SendDespawnEntity(other, targetEntityID);
            }

            var pickedUpWeapon = AuthoredGameplayCatalog.FindItem(item.GCClass);
            var pickedUpWeaponNode = GCDatabase.Instance.ResolveWithInheritance(item.GCClass);
            GCDatabase.WeaponStatsFixed pickedUpWeaponStats = default;
            if (pickedUpWeaponNode == null)
                RuntimeEvidence.LogFallbackHit("damage-weapon-desc", "missing-gc-node", $"source=pickup weapon={item.GCClass} sourceFunction=Weapon::ComputeAttributes-return", 64);
            else
                pickedUpWeaponStats = GCDatabase.Instance.GetWeaponStatsFixed(item.GCClass);
            int pickedUpAuthoredDamageF32 = pickedUpWeaponStats.DamageF32 > 0 ? pickedUpWeaponStats.DamageF32 : 0;
            int pickedUpAuthoredVolatilityF32 = pickedUpWeaponStats.VolatilityF32 > 0
                ? Math.Min(0xF4, pickedUpWeaponStats.VolatilityF32)
                : 0x40;
            string pickedUpWeaponClass = !string.IsNullOrEmpty(pickedUpWeaponStats.WeaponClass) ? pickedUpWeaponStats.WeaponClass : pickedUpWeapon != null && !string.IsNullOrEmpty(pickedUpWeapon.weaponClass) ? pickedUpWeapon.weaponClass : "";
            string pickedUpDamageType = !string.IsNullOrEmpty(pickedUpWeaponStats.DamageType) ? pickedUpWeaponStats.DamageType : "";
            if (pickedUpAuthoredDamageF32 > 0 &&
                TryResolveWeaponDescIds("pickup", item.GCClass, pickedUpWeaponClass, pickedUpDamageType, out int pickedUpWeaponClassId, out int pickedUpDamageTypeId))
            {
                playerState.WeaponDamageF32 = pickedUpAuthoredDamageF32;
                playerState.WeaponDamageVolatilityF32 = pickedUpAuthoredVolatilityF32;
                playerState.WeaponLevel = Math.Max(1, item.StoredLevel >= 0 ? item.StoredLevel : DungeonRunners.Gameplay.RPGSettings.GetItemLevel(item.GCClass));
                playerState.WeaponClass = pickedUpWeaponClass;
                playerState.WeaponDamageType = pickedUpDamageType;
                playerState.WeaponCategory = !string.IsNullOrEmpty(pickedUpWeaponStats.WeaponCategory) ? pickedUpWeaponStats.WeaponCategory : "";
                playerState.WeaponStatsResolved = true;
                playerState.WeaponClassId = pickedUpWeaponClassId;
                playerState.DamageTypeId = pickedUpDamageTypeId;
                DamageResolver.ApplyWeaponRuntimeBaseDamage(playerState, item, playerState.Level, "pickup");
                playerState.WeaponRange = pickedUpWeaponStats.RangeF32 > 0 ? pickedUpWeaponStats.RangeRoundedUnits : pickedUpWeapon != null && pickedUpWeapon.range > 0 ? pickedUpWeapon.range : 0;
                playerState.WeaponRangeF32 = pickedUpWeaponStats.RangeF32 > 0 ? pickedUpWeaponStats.RangeF32 : playerState.WeaponRange * 0x100;
                playerState.WeaponInitUseRangeF32 = pickedUpWeaponStats.InitUseRangeF32;
                playerState.WeaponClientSyncToleranceF32 = pickedUpWeaponStats.ClientSyncToleranceF32;
                playerState.WeaponCooldownF32 = Math.Max(0, pickedUpWeaponStats.CooldownF32);
                playerState.WeaponSpeedF32 = pickedUpWeaponStats.WeaponSpeedF32 > 0 ? pickedUpWeaponStats.WeaponSpeedF32 : 100 * 0x100;
                playerState.WeaponUsesProjectile = pickedUpWeaponStats.UseProjectile;
                playerState.WeaponShotType = pickedUpWeaponStats.ShotType;
                playerState.WeaponProjectileSpeedF32 = pickedUpWeaponStats.ProjectileSpeedF32;
                playerState.WeaponProjectileSizeF32 = pickedUpWeaponStats.ProjectileSizeF32;
                playerState.WeaponBurstCount = Math.Max(1, pickedUpWeaponStats.BurstCount);
                playerState.WeaponEquipmentSlot = item.TargetSlot ?? item.GetEquipmentSlotFromGCClass();
                playerState.WeaponStunMod = Math.Max(0, pickedUpWeaponStats.StunMod);
                Debug.LogError($"[PICKUP] Weapon damageF32={playerState.WeaponDamageF32} volatilityF32={playerState.WeaponDamageVolatilityF32} level={playerState.WeaponLevel} clientDamageLevel={playerState.WeaponDamageLevel} clientBaseDamage={playerState.WeaponBaseDamage} clientBaseSource={playerState.WeaponBaseDamageSource} class={playerState.WeaponClass}/{playerState.WeaponClassId} damageType={playerState.WeaponDamageType}/{playerState.DamageTypeId} category={playerState.WeaponCategory} range={playerState.WeaponRange} cooldownF32={playerState.WeaponCooldownF32} speedF32={playerState.WeaponSpeedF32} useProjectile={playerState.WeaponUsesProjectile} projectileSpeedF32={playerState.WeaponProjectileSpeedF32} projectileSizeF32={playerState.WeaponProjectileSizeF32} burst={playerState.WeaponBurstCount} from '{item.GCClass}'");
            }

            Debug.LogError($"[PICKUP]  Item picked up and now in hand!");

            CommitQuestItemAcquisition(conn, questMutation, item.GCClass, droppedQty, "item-pickup-cursor");

            Debug.LogError("");
            Debug.LogError("                              ITEM PICKUP COMPLETE                             ");
            Debug.LogError("");
        }

        private void HandleItemRightClickPickup(RRConnection conn, ushort componentId, ushort targetEntityID, byte responseId, byte sessionID, bool includeActivateResponse = true)
        {
            Debug.LogError($"[PICKUP-RC] Right-click target=0x{targetEntityID:X4}");
            if (!_droppedItems.TryGetValue(targetEntityID, out DroppedItemInfo pickupInfo))
            {
                Debug.LogError($"[PICKUP-RC] state=rejected target=0x{targetEntityID:X4} reason=missing");
                return;
            }
            if (!DroppedItemMatchesConnection(conn, pickupInfo))
            {
                Debug.LogError($"[PICKUP-RC] state=rejected target=0x{targetEntityID:X4} reason=instance");
                return;
            }
            if (!IsWithinActivationRangeFixed(
                    conn,
                    pickupInfo.PosFixedX,
                    pickupInfo.PosFixedY,
                    ACTIVATE_ITEM_TARGET_RADIUS_FIXED,
                    out long pickupDistanceSqFixed,
                    out int pickupRangeFixed))
            {
                Debug.LogError($"[PICKUP-RC] state=rejected target=0x{targetEntityID:X4} reason=range distanceSq={pickupDistanceSqFixed} range={pickupRangeFixed}");
                return;
            }
            if (!CanAcquireDroppedItem(conn, targetEntityID, pickupInfo))
            {
                Debug.LogError($"[PICKUP-RC] state=rejected target=0x{targetEntityID:X4} reason=owner owner={pickupInfo.OwnerCharacterId} requester={GetCharSqlId(conn)}");
                return;
            }

            if (pickupInfo.GoldAmount > 0)
            {
                uint goldAmount = pickupInfo.GoldAmount;
                if (!TryAcquireDroppedGoldTransaction(conn, targetEntityID, goldAmount, "gold-pickup-right-click", out _, out _))
                {
                    Debug.LogError($"[GOLD-PICKUP] state=failed phase=gold-transaction entity=0x{targetEntityID:X4}");
                    return;
                }
                Debug.LogError($"[GOLD-PICKUP] +{goldAmount} gold (entity 0x{targetEntityID:X4})");

                string connId2 = conn.ConnId.ToString();
                ushort unitContainerId2 = GetUnitContainerComponentId(connId2);
                var gw = new LEWriter();
                gw.WriteByte(0x07);
                if (includeActivateResponse)
                {
                    gw.WriteByte(0x35);
                    gw.WriteUInt16(componentId);
                    gw.WriteByte(0x01);
                    gw.WriteByte(responseId);
                    gw.WriteByte(0x06); gw.WriteByte(sessionID); gw.WriteUInt16(targetEntityID); WritePlayerEntitySynch(conn, gw);
                }
                gw.WriteByte(0x05);
                gw.WriteUInt16(targetEntityID);
                if (unitContainerId2 != 0)
                {
                    gw.WriteByte(0x35);
                    gw.WriteUInt16(unitContainerId2);
                    gw.WriteByte(0x20);
                    gw.WriteUInt32(goldAmount);
                    gw.WriteByte(0x00);
                    gw.WriteUInt32(0x00000000);
                    gw.WriteByte(0x01);
                    WritePlayerEntitySynch(conn, gw);
                }
                gw.WriteByte(0x06);
                SendToClient(conn, gw.ToArray());
                foreach (var other in GetConnectionInsertionOrderSnapshot())
                {
                    if (other == conn || !other.IsSpawned || !DroppedItemMatchesConnection(other, pickupInfo)) continue;
                    SendDespawnEntity(other, targetEntityID);
                }
                return;
            }

            if (!TryReserveDroppedItemAcquisition(targetEntityID, pickupInfo))
            {
                Debug.LogError($"[PICKUP-RC] state=rejected target=0x{targetEntityID:X4} reason=acquisition-in-progress");
                return;
            }
            int droppedQty = Math.Max(1, pickupInfo.Quantity);
            GCObject item = pickupInfo.Item;
            if (item == null)
            {
                ReleaseDroppedItemAcquisition(targetEntityID);
                Debug.LogError($"[PICKUP-RC] state=rejected target=0x{targetEntityID:X4} reason=missing-item");
                return;
            }

            string connId = conn.ConnId.ToString();
            ushort unitContainerId = GetUnitContainerComponentId(connId);

            int maxStack = ItemStackRules.GetLimit(item.GCClass);
            if (maxStack > 1)
            {
                if (_playerInventoryItems.ContainsKey(connId))
                {
                    var inventoryEntries = GetOrderedInventoryItems(connId).ToArray();
                    for (int inventoryIndex = 0; inventoryIndex < inventoryEntries.Length; inventoryIndex++)
                    {
                        var inventoryEntry = inventoryEntries[inventoryIndex];
                        uint existingSlot = inventoryEntry.Key;
                        var entry = inventoryEntry.Value;
                        int currentCount = GetStackCount(connId, existingSlot);
                        if (!ItemStackRules.CanMergeAutomatically(entry.item, currentCount, item, droppedQty)) continue;

                        int newCount = currentCount + droppedQty;
                        Debug.LogError($"[PICKUP-RC] STACK MERGE: {item.GCClass} -> slot {existingSlot} {currentCount}->{newCount} (max {maxStack})");

                        SetStackCount(connId, existingSlot, newCount);
                        QuestProgressMutation questMutation = StageQuestItemAcquisition(conn, item.GCClass, droppedQty);
                        if (!TrySaveFullCharacterSnapshotConsumingDrop(conn, pickupInfo.DbId, "item-pickup-stack-merge"))
                        {
                            RollbackQuestItemAcquisition(questMutation);
                            SetStackCount(connId, existingSlot, currentCount);
                            ReleaseDroppedItemAcquisition(targetEntityID);
                            Debug.LogError($"[PICKUP-RC] state=failed phase=commit target=0x{targetEntityID:X4} slot={existingSlot} dbId={pickupInfo.DbId}");
                            return;
                        }
                        CompleteDroppedItemAcquisition(targetEntityID, pickupInfo, "item-pickup-stack-merge");

                        var mWriter = new LEWriter();
                        mWriter.WriteByte(0x07);

                        if (includeActivateResponse)
                        {
                            mWriter.WriteByte(0x35);
                            mWriter.WriteUInt16(componentId);
                            mWriter.WriteByte(0x01);
                            mWriter.WriteByte(responseId);
                            mWriter.WriteByte(0x06); mWriter.WriteByte(sessionID); mWriter.WriteUInt16(targetEntityID); WritePlayerEntitySynch(conn, mWriter);
                        }

                        mWriter.WriteByte(0x05);
                        mWriter.WriteUInt16(targetEntityID);

                        mWriter.WriteByte(0x35);
                        mWriter.WriteUInt16(unitContainerId);
                        mWriter.WriteByte(0x22);
                        mWriter.WriteUInt32(existingSlot);
                        mWriter.WriteByte((byte)(newCount > 255 ? 255 : newCount));
                        WritePlayerEntitySynch(conn, mWriter);

                        mWriter.WriteByte(0x06);

                        SendToClient(conn, mWriter.ToArray());

                        foreach (var other in GetConnectionInsertionOrderSnapshot())
                        {
                            if (other == conn) continue;
                            if (!other.IsSpawned) continue;
                            if (other.CurrentZoneGcType != conn.CurrentZoneGcType) continue;
                            if (other.InstanceId != conn.InstanceId) continue;
                            SendDespawnEntity(other, targetEntityID);
                        }

                        CommitQuestItemAcquisition(conn, questMutation, item.GCClass, droppedQty, "item-pickup-stack-merge");
                        return;
                    }
                }
                Debug.LogError($"[PICKUP-RC] No mergeable stack for {item.GCClass} (max {maxStack}) - creating new stack");
            }

            ItemData itemData = AuthoredGameplayCatalog.FindItem(item.GCClass);
            int itemWidth = itemData?.inventoryWidth ?? 1;
            int itemHeight = itemData?.inventoryHeight ?? 1;

            var (slotX, slotY) = FindNextFreeInventorySlot(connId, itemWidth, itemHeight);
            if (slotX < 0 || slotY < 0)
            {
                Debug.LogError($"[PICKUP-RC] Inventory full - leaving item on ground");
                ReleaseDroppedItemAcquisition(targetEntityID);
                SendSystemMessage(conn, "Your inventory is full!");
                var fullWriter = new LEWriter();
                fullWriter.WriteByte(0x07);
                if (includeActivateResponse)
                {
                    fullWriter.WriteByte(0x35);
                    fullWriter.WriteUInt16(componentId);
                    fullWriter.WriteByte(0x01);
                    fullWriter.WriteByte(responseId);
                    fullWriter.WriteByte(0x06); fullWriter.WriteByte(sessionID); fullWriter.WriteUInt16(targetEntityID); WritePlayerEntitySynch(conn, fullWriter);
                }
                fullWriter.WriteByte(0x06);
                SendToClient(conn, fullWriter.ToArray());
                return;
            }

            uint trackingSlot = GetNextInventorySlot(connId);
            Debug.LogError($"[PICKUP-RC] {item.GCClass} -> slot ({slotX},{slotY}) trackingSlot={trackingSlot}");

            OccupyInventorySlots(connId, (byte)slotX, (byte)slotY, itemWidth, itemHeight);
            TrackInventoryItem(connId, trackingSlot, item, (byte)slotX, (byte)slotY);
            SetStackCount(connId, trackingSlot, droppedQty);
            QuestProgressMutation inventoryQuestMutation = StageQuestItemAcquisition(conn, item.GCClass, droppedQty);
            if (!TrySaveFullCharacterSnapshotConsumingDrop(conn, pickupInfo.DbId, "item-pickup-inventory"))
            {
                RollbackQuestItemAcquisition(inventoryQuestMutation);
                RemoveInventoryItemBySlot(connId, trackingSlot);
                FreeInventorySlots(connId, (byte)slotX, (byte)slotY, itemWidth, itemHeight);
                ReleaseDroppedItemAcquisition(targetEntityID);
                Debug.LogError($"[PICKUP-RC] state=failed phase=commit target=0x{targetEntityID:X4} slot={trackingSlot} dbId={pickupInfo.DbId}");
                return;
            }
            CompleteDroppedItemAcquisition(targetEntityID, pickupInfo, "item-pickup-inventory");

            var writer = new LEWriter();
            writer.WriteByte(0x07);

            if (includeActivateResponse)
            {
                writer.WriteByte(0x35);
                writer.WriteUInt16(componentId);
                writer.WriteByte(0x01);
                writer.WriteByte(responseId);
                writer.WriteByte(0x06); writer.WriteByte(sessionID); writer.WriteUInt16(targetEntityID); WritePlayerEntitySynch(conn, writer);
            }

            writer.WriteByte(0x05);
            writer.WriteUInt16(targetEntityID);

            writer.WriteByte(0x35);
            writer.WriteUInt16(unitContainerId);
            writer.WriteByte(0x29);
            WritePlayerEntitySynch(conn, writer);

            writer.WriteByte(0x35);
            writer.WriteUInt16(unitContainerId);
            writer.WriteByte(0x1E);
            writer.WriteByte(0x0B);

            string gcCheck = item.GCClass.ToLower();
            int itemLevel = item.StoredLevel >= 0 ? item.StoredLevel : Math.Max(1, item.GetItemRequiredLevel());
            item.WriteInitForInventory(writer, (byte)slotX, (byte)slotY, trackingSlot, itemLevel, (byte)Math.Clamp(droppedQty, 1, byte.MaxValue));
            WritePlayerEntitySynch(conn, writer);

            writer.WriteByte(0x06);

            byte[] packet = writer.ToArray();
            Debug.LogError($"[PICKUP-RC] packetBytes={packet.Length}");
            if (gcCheck.Contains("skillbook") || gcCheck.Contains("voucher"))
            {
                Debug.LogError($"[PICKUP-RC] item={item.GCClass} gcPacket={GCObject.GetPacketGCClassFor(gcCheck)} trackingSlot={trackingSlot} slot=({slotX},{slotY}) qty={droppedQty}");
                Debug.LogError($"[PICKUP-RC] hex={BitConverter.ToString(packet)}");
            }
            SendToClient(conn, packet);

            foreach (var other in GetConnectionInsertionOrderSnapshot())
            {
                if (other == conn) continue;
                if (!other.IsSpawned) continue;
                if (other.CurrentZoneGcType != conn.CurrentZoneGcType) continue;
                if (other.InstanceId != conn.InstanceId) continue;
                SendDespawnEntity(other, targetEntityID);
            }

            CommitQuestItemAcquisition(conn, inventoryQuestMutation, item.GCClass, droppedQty, "item-pickup-inventory");

            Debug.LogError($"[PICKUP-RC]  {item.GCClass} placed in inventory at ({slotX},{slotY})");
        }







        private void HandleClientControlResponse(RRConnection conn, LEReader reader, ushort componentId)
        {
            try
            {
                Debug.Log($"[CLIENT-CONTROL]  CLIENT RESPONDED TO OUR 0x64 MESSAGE!");
                Debug.Log($"[CLIENT-CONTROL] ComponentId: {componentId:X4}, Remaining bytes: {reader.Remaining}");

                while (reader.Remaining > 0)
                {
                    byte b = reader.ReadByte();
                    Debug.Log($"[CLIENT-CONTROL] Read byte: 0x{b:X2}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CLIENT-CONTROL] state=failed message='{ex.Message}'");
            }
        }

        private void HandleCancelAction(RRConnection conn, LEReader reader, ushort componentId)
        {
            try
            {
                byte sessionId = reader.ReadByte();
                Debug.LogError($"[CANCEL-ACTION] Client wants to cancel, sessionId=0x{sessionId:X2}");
                string atkKey = conn.ConnId.ToString();
                var oldTarget = Combat.WeaponUseRuntime.Instance.GetActiveTarget(atkKey);
                if (oldTarget != null)
                {
                    Debug.LogError($"[CANCEL-ACTION] Clearing target {oldTarget.Name} (UseTargets={oldTarget.UseTargetCount})");
                }
                _pendingPlayerCancelActionInputs.Add(new PendingPlayerCancelActionInput
                {
                    ConnId = conn.ConnId,
                    InstanceKey = RoomRuntime.NormalizeInstanceKey(ResolveConnectionInstanceKey(conn)),
                    ComponentId = componentId,
                    SessionId = sessionId,
                    ReceivedTick = _combatTick
                });
                conn.MessageQueue.EnqueueDeferred(
                    () => BuildCancelActionResponse(conn, componentId, sessionId),
                    retryOnEmpty: true,
                    componentId: componentId);
                RelayCancelActionToPeers(conn, "PEER-CANCEL-ACTION");
                Debug.LogError($"[CANCEL-ACTION] Queued cancel response component=0x{componentId:X4} sessionId=0x{sessionId:X2} receivedTick={_combatTick} phase=await-client-entity-writer");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CANCEL-ACTION] state=failed message='{ex.Message}'");
            }
        }

        private byte[] BuildCancelActionResponse(RRConnection conn, ushort componentId, byte sessionId)
        {
            if (conn == null || !conn.IsConnected || componentId == 0)
                return Array.Empty<byte>();
            var cancelActionMessage = new LEWriter();
            cancelActionMessage.WriteByte(0x35);
            cancelActionMessage.WriteUInt16(componentId);
            cancelActionMessage.WriteByte(0x03);
            cancelActionMessage.WriteByte(sessionId);
            if (!TryWriteEntitySynchForComponent(conn, cancelActionMessage, componentId, 0x03, EntitySynchInfoContext.ControlAck, "CANCEL-ACTION"))
                return Array.Empty<byte>();
            return cancelActionMessage.ToArray();
        }


        private void HandleActionType06(RRConnection conn, LEReader reader, ushort componentId)
        {
            try
            {
                Debug.Log($"[ACTION-06] Client sent 0x06 submessage, remaining bytes: {reader.Remaining}");
                while (reader.Remaining > 0)
                {
                    byte b = reader.ReadByte();
                    Debug.Log($"[ACTION-06] Read byte: 0x{b:X2}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ACTION-06] state=failed message='{ex.Message}'");
            }
        }


        public class ZonePortal
        {
            public uint Id;
            public string GCType;
            public string Name;
            public int PosFixedX;
            public int PosFixedY;
            public int PosFixedZ;
            public int HeadingFixed;
            public int Width;
            public int Height;
            public string TargetZone;
            public string SpawnPoint;
            public uint Color;
        }

        public class ZoneCheckpoint
        {
            public uint Id;
            public string GCType;
            public string CheckpointGCType;
            public string Name;
            public int PosFixedX;
            public int PosFixedY;
            public int PosFixedZ;
            public int HeadingFixed;
        }





        private void InitializeZonePortals()
        {
            Debug.LogError("[INIT-PORTALS] phase=start source=pki-pkg-graph");

            foreach (var zone in _zoneOrder)
            {
                string zoneName = zone.name.ToLower();
                var portalData = AuthoredGameplayCatalog.GetPortalsForZone(zoneName);

                if (portalData == null || portalData.Count == 0)
                    continue;

                _zonePortals[zone.id] = new List<ZonePortal>();

                foreach (var data in portalData)
                {
                    var portal = new ZonePortal
                    {
                        Id = 0,
                        GCType = data.gcType,
                        Name = data.name,
                        PosFixedX = data.PosFixedX,
                        PosFixedY = data.PosFixedY,
                        PosFixedZ = data.PosFixedZ,
                        HeadingFixed = data.HeadingFixed,
                        Width = data.width,
                        Height = data.height,
                        TargetZone = data.targetZone,
                        SpawnPoint = data.spawnPoint,
                        Color = data.color
                    };

                    _zonePortals[zone.id].Add(portal);
                    Debug.LogError($"[INIT-PORTALS] zone={zone.name} name='{portal.Name}' id={portal.Id} target='{portal.TargetZone}'");
                }
            }

            int totalPortals = _zonePortals.Values.Sum(list => list.Count);
            Debug.LogError($"[INIT-PORTALS] total={totalPortals}");
        }

        private void InitializeZoneCheckpoints()
        {
            Debug.LogError("[INIT-CHECKPOINTS] phase=start source=pki-pkg-graph");

            foreach (var zone in _zoneOrder)
            {
                string zoneName = zone.name.ToLower();
                var checkpointData = AuthoredGameplayCatalog.GetCheckpointsForZone(zoneName);

                if (checkpointData == null || checkpointData.Count == 0)
                    continue;

                _zoneCheckpoints[zone.id] = new List<ZoneCheckpoint>();

                foreach (var data in checkpointData)
                {
                    var checkpoint = new ZoneCheckpoint
                    {
                        Id = 0,
                        GCType = data.entityGcType,
                        CheckpointGCType = data.gcType,
                        Name = data.name,
                        PosFixedX = data.PosFixedX,
                        PosFixedY = data.PosFixedY,
                        PosFixedZ = data.PosFixedZ,
                        HeadingFixed = data.HeadingFixed
                    };

                    _zoneCheckpoints[zone.id].Add(checkpoint);
                    Debug.LogError($"[INIT-CHECKPOINTS] zone={zone.name} gcType='{checkpoint.GCType}'");
                }
            }

            int checkpointCount = 0;
            foreach (Zone zone in _zoneOrder)
            {
                if (!_zoneCheckpoints.TryGetValue(zone.id, out List<ZoneCheckpoint> zoneCheckpoints))
                    continue;
                checkpointCount += zoneCheckpoints.Count;
                foreach (ZoneCheckpoint checkpoint in zoneCheckpoints)
                {
                    _checkpointZoneMap[checkpoint.CheckpointGCType] = zone.name;
                }
            }
            Debug.LogError($"[INIT-CHECKPOINTS] total={checkpointCount}");
            Debug.LogError($"[INIT-CHECKPOINTS] checkpointZoneMap={_checkpointZoneMap.Count}");
        }

        private enum DungeonPortalRole
        {
            Entry,
            Exit
        }

        private static bool ContainsIgnoreCase(string value, string needle)
        {
            return !string.IsNullOrEmpty(value)
                && value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int GetDungeon00LevelOrdinal(string zoneName)
        {
            if (string.IsNullOrWhiteSpace(zoneName))
                return -1;
            if (zoneName.IndexOf("boss", StringComparison.OrdinalIgnoreCase) >= 0)
                return 4;

            int nameIndex = zoneName.IndexOf("dungeon00_level", StringComparison.OrdinalIgnoreCase);
            if (nameIndex < 0)
                return -1;

            nameIndex += "dungeon00_level".Length;
            int value = 0;
            int digits = 0;
            while (nameIndex < zoneName.Length && char.IsDigit(zoneName[nameIndex]))
            {
                value = value * 10 + (zoneName[nameIndex] - '0');
                nameIndex++;
                digits++;
            }

            return digits > 0 ? value : -1;
        }

        private static DungeonPortalRole ResolveDungeonPortalRole(string currentZoneName, string gcType, string name, string targetZone, string spawnPoint)
        {
            int currentLevel = GetDungeon00LevelOrdinal(currentZoneName);
            int targetLevel = GetDungeon00LevelOrdinal(targetZone);
            if (targetLevel > 0 && currentLevel > 0 && targetLevel > currentLevel)
                return DungeonPortalRole.Exit;
            if (ContainsIgnoreCase(targetZone, "boss") && currentLevel > 0)
                return DungeonPortalRole.Exit;
            if (ContainsIgnoreCase(gcType, "oneway")
                || ContainsIgnoreCase(name, "to_level")
                || ContainsIgnoreCase(name, "to_boss"))
                return DungeonPortalRole.Exit;

            if (ContainsIgnoreCase(gcType, "hub")
                || ContainsIgnoreCase(name, "to_tutorial")
                || ContainsIgnoreCase(targetZone, "tutorial")
                || ContainsIgnoreCase(targetZone, "town"))
                return DungeonPortalRole.Entry;

            if (targetLevel > 0 && currentLevel > 0 && targetLevel <= currentLevel)
                return DungeonPortalRole.Entry;

            return DungeonPortalRole.Exit;
        }

        private static void ResolveDungeonPortalAnchor(
            DungeonMazeSpawner.ProceduralDungeonSnapshot snapshot,
            DungeonPortalRole role,
            out int positionFixedX,
            out int positionFixedY,
            out int positionFixedZ,
            out int headingFixed,
            out int sourceIndex,
            out string tileType,
            out int gridX,
            out int gridY,
            out int localFixedX,
            out int localFixedY,
            out int localFixedZ,
            out string source)
        {
            if (role == DungeonPortalRole.Entry)
            {
                positionFixedX = snapshot.EntryPortalSpawnFixedX;
                positionFixedY = snapshot.EntryPortalSpawnFixedY;
                positionFixedZ = snapshot.EntryPortalSpawnFixedZ;
                headingFixed = snapshot.EntryPortalHeadingFixed;
                sourceIndex = snapshot.EntrySourceIndex;
                tileType = snapshot.EntryTileType;
                gridX = snapshot.EntryGridX;
                gridY = snapshot.EntryGridY;
                localFixedX = snapshot.EntryPortalAnchorLocalFixedX;
                localFixedY = snapshot.EntryPortalAnchorLocalFixedY;
                localFixedZ = snapshot.EntryPortalAnchorLocalFixedZ;
                source = snapshot.EntryPortalAnchorSource;
                return;
            }

            positionFixedX = snapshot.ExitPortalSpawnFixedX;
            positionFixedY = snapshot.ExitPortalSpawnFixedY;
            positionFixedZ = snapshot.ExitPortalSpawnFixedZ;
            headingFixed = snapshot.ExitPortalHeadingFixed;
            sourceIndex = snapshot.ExitSourceIndex;
            tileType = snapshot.ExitTileType;
            gridX = snapshot.ExitGridX;
            gridY = snapshot.ExitGridY;
            localFixedX = snapshot.ExitPortalAnchorLocalFixedX;
            localFixedY = snapshot.ExitPortalAnchorLocalFixedY;
            localFixedZ = snapshot.ExitPortalAnchorLocalFixedZ;
            source = snapshot.ExitPortalAnchorSource;
        }

        private void SendCombatStart(Gameplay.DuelRuntime.DuelInfo duel)
        {
            var challenger = FindConnectionByLogin(duel.ChallengerLogin);
            var target     = FindConnectionByLogin(duel.TargetLogin);
            if (challenger != null)
            {
                SendToClient(challenger, PVPPackets.BuildDuelStatus(
                    PVPPackets.DuelStatusType.InProgress, duel.TargetCharSqlId, 0, 0));
                SendToClient(challenger, PVPPackets.BuildPVPStatusChanged(pvpState: 1, matchId: 0));
            }
            if (target != null)
            {
                SendToClient(target, PVPPackets.BuildDuelStatus(
                    PVPPackets.DuelStatusType.InProgress, duel.ChallengerCharSqlId, 0, 0));
                SendToClient(target, PVPPackets.BuildPVPStatusChanged(pvpState: 1, matchId: 0));
            }
            Debug.LogError($"[PVP-DUEL] Combat start sent: {duel.ChallengerLogin} vs {duel.TargetLogin}");
        }

        private void SendDuelEndPackets(string winnerLogin, string loserLogin, Gameplay.DuelRuntime.DuelInfo duel)
        {
            uint winnerCharSqlId = string.Equals(duel.ChallengerLogin, winnerLogin, StringComparison.OrdinalIgnoreCase)
                ? duel.ChallengerCharSqlId : duel.TargetCharSqlId;
            uint loserCharSqlId  = string.Equals(duel.ChallengerLogin, loserLogin,  StringComparison.OrdinalIgnoreCase)
                ? duel.ChallengerCharSqlId : duel.TargetCharSqlId;

            var winnerConn = FindConnectionByLogin(winnerLogin);
            var loserConn  = FindConnectionByLogin(loserLogin);

            if (winnerConn != null)
            {
                SendToClient(winnerConn, PVPPackets.BuildDuelStatus(
                    PVPPackets.DuelStatusType.Won, loserCharSqlId, 0, 0));
                SendToClient(winnerConn, PVPPackets.BuildPVPStatusChanged(pvpState: 0, matchId: 0));
            }
            if (loserConn != null)
            {
                SendToClient(loserConn, PVPPackets.BuildDuelStatus(
                    PVPPackets.DuelStatusType.Lost, winnerCharSqlId, 0, 0));
                SendToClient(loserConn, PVPPackets.BuildPVPStatusChanged(pvpState: 0, matchId: 0));
            }
            Debug.LogError($"[PVP-DUEL] End packets sent: winner={winnerLogin} loser={loserLogin}");
        }

        private RRConnection FindConnectionByLogin(string loginName)
        {
            return FindConnectionByName(loginName);
        }

        private void QueueDroppedItemActivateResponse(RRConnection conn, ushort componentId, ushort targetEntityId, byte responseId, byte sessionId)
        {
            conn.MessageQueue.EnqueueDeferred(
                () => BuildDroppedItemActivateResponse(conn, componentId, targetEntityId, responseId, sessionId),
                retryOnEmpty: true,
                componentId: componentId);
        }

        private byte[] BuildDroppedItemActivateResponse(RRConnection conn, ushort componentId, ushort targetEntityId, byte responseId, byte sessionId)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x35);
            writer.WriteUInt16(componentId);
            writer.WriteByte(0x01);
            writer.WriteByte(responseId);
            writer.WriteByte(0x06);
            writer.WriteByte(sessionId);
            writer.WriteUInt16(targetEntityId);
            if (!WritePlayerEntitySynch(conn, writer))
                return Array.Empty<byte>();
            return writer.ToArray();
        }

        private uint _lastMatchmakingTick;
        private bool _hasMatchmakingTick;

    }
}
