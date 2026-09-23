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
        private sealed class PendingProceduralZoneJoin
        {
            public RRConnection Connection;
            public byte[] Body;
            public uint ZoneId;
            public uint InstanceId;
        }

        private sealed class ResolvedRespawnDestination
        {
            public Zone Zone;
            public string SpawnPoint;
            public int PosFixedX;
            public int PosFixedY;
            public int PosFixedZ;
            public int HeadingFixed;
        }

        private readonly Dictionary<int, PendingProceduralZoneJoin> _pendingProceduralZoneJoins = new Dictionary<int, PendingProceduralZoneJoin>();

        private Dictionary<int, uint> _pendingZoneConnectSeeds = new Dictionary<int, uint>();
        private Dictionary<string, uint> _zoneInstanceLayoutSeeds = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, uint> _zoneInstanceRoomSeeds = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, uint> _soloDungeonInstanceIds = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, uint> _soloDungeonLastActiveTicks = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        private const uint SoloDungeonMemoryTicks = 18000u;
        private uint _nextSoloDungeonInstanceId = 0x80000000u;
        private HashSet<string> _loggedRuntimeZoneSeeds = new HashSet<string>();
        private Dictionary<uint, Zone> _zones = new Dictionary<uint, Zone>();
        private readonly List<Zone> _zoneOrder = new List<Zone>();
        private Dictionary<uint, List<ZoneNPC>> _zoneNPCs = new Dictionary<uint, List<ZoneNPC>>();
        private Dictionary<uint, List<ZonePortal>> _zonePortals = new Dictionary<uint, List<ZonePortal>>();
        private Dictionary<uint, List<ZoneCheckpoint>> _zoneCheckpoints = new Dictionary<uint, List<ZoneCheckpoint>>();
        private Dictionary<ushort, ZonePortal> _portalEntities = new Dictionary<ushort, ZonePortal>();
        private Dictionary<ushort, ZoneCheckpoint> _checkpointEntities = new Dictionary<ushort, ZoneCheckpoint>();
        private readonly HashSet<string> _activatedWorldEntities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _activatedWorldEntityOrder = new List<string>();
        private readonly HashSet<string> _reservedWorldEntityActivations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _reservedWorldEntityActivationOrder = new List<string>();
        private readonly HashSet<string> _outstandingQuestActivateDrops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _outstandingQuestActivateDropOrder = new List<string>();
        private readonly Dictionary<ushort, string> _questActivateDropBindings = new Dictionary<ushort, string>();
        private readonly List<ushort> _questActivateDropBindingOrder = new List<ushort>();
        private readonly Dictionary<int, Dictionary<int, ushort>> _worldEntityIdsByConn = new Dictionary<int, Dictionary<int, ushort>>();
        private readonly Dictionary<int, List<ushort>> _worldEntityRuntimeOrderByConn = new Dictionary<int, List<ushort>>();
        private readonly Dictionary<int, List<ushort>> _portalEntitiesByConn = new Dictionary<int, List<ushort>>();
        private readonly Dictionary<int, List<ushort>> _checkpointEntitiesByConn = new Dictionary<int, List<ushort>>();
        private Dictionary<int, List<ZoneNPC>> _adminShopNPCs = new Dictionary<int, List<ZoneNPC>>();
        private const uint ITEM_OBJECT_LIFETIME_TICKS = 27000;
        private const uint DROPPED_ITEM_DELETE_RETRY_TICKS = 150;
        private readonly Dictionary<ushort, uint> _droppedItemLifetimeTicks = new Dictionary<ushort, uint>();
        private readonly List<(ushort EntityId, DroppedItemInfo Info, uint SimulationTick)> _expiredDroppedItems = new List<(ushort, DroppedItemInfo, uint)>();
        private readonly HashSet<ushort> _pendingDroppedItemExpirations = new HashSet<ushort>();
        private static bool RespawnTracking => ServerDiagnostics.IsEnabled("respawnTracking");

        private static void SetAvatarUInt32Property(GCObject avatar, string propertyName, uint value)
        {
            if (avatar == null)
                throw new ArgumentNullException(nameof(avatar));
            foreach (var property in avatar.Properties)
            {
                if (property is UInt32Property uintProperty && string.Equals(uintProperty.Name, propertyName, StringComparison.Ordinal))
                {
                    uintProperty.Value = value;
                    return;
                }
            }
            throw new InvalidDataException($"Avatar property '{propertyName}' is missing from authored runtime object.");
        }

        private void ApplyAvatarRuntimeProperties(GCObject avatar, SavedCharacter savedChar, PlayerState playerState)
        {
            if (savedChar == null)
                throw new ArgumentNullException(nameof(savedChar));
            if (playerState == null)
                throw new ArgumentNullException(nameof(playerState));

            int totalAllocated = checked(Math.Max(0, savedChar.statStrength)
                + Math.Max(0, savedChar.statAgility)
                + Math.Max(0, savedChar.statEndurance)
                + Math.Max(0, savedChar.statIntellect));
            int maxTotalAttributePool = checked(Math.Max(0, playerState.Level - 1) * StatPointsPerLevel);
            int attributePoints = Math.Max(0, maxTotalAttributePool - totalAllocated);
            int respecTimer = 0;
            if (savedChar.lastRespecTime > 0)
            {
                int nowSeconds = checked((int)ServerRuntime.ReadUnixTimeSeconds());
                respecTimer = Math.Max(0, GetRespecCooldownSeconds() - (nowSeconds - savedChar.lastRespecTime));
            }

            SetAvatarUInt32Property(avatar, "TotalWorldTime", 0);
            SetAvatarUInt32Property(avatar, "LastKnownQueueLevel", 0);
            SetAvatarUInt32Property(avatar, "HasBlingGnome", 0);
            SetAvatarUInt32Property(avatar, "Level", checked((uint)Math.Max(1, playerState.Level)));
            SetAvatarUInt32Property(avatar, "HitPoints", playerState.CurrentHPWire);
            SetAvatarUInt32Property(avatar, "ManaPoints", playerState.CurrentManaWire);
            SetAvatarUInt32Property(avatar, "Experience", playerState.Experience);
            SetAvatarUInt32Property(avatar, "AttributePoints", checked((uint)attributePoints));
            SetAvatarUInt32Property(avatar, "ReSpecTimer", checked((uint)respecTimer));
            SetAvatarUInt32Property(avatar, "StrengthPoints", checked((uint)Math.Max(0, savedChar.statStrength)));
            SetAvatarUInt32Property(avatar, "AgilityPoints", checked((uint)Math.Max(0, savedChar.statAgility)));
            SetAvatarUInt32Property(avatar, "ToughnessPoints", checked((uint)Math.Max(0, savedChar.statEndurance)));
            SetAvatarUInt32Property(avatar, "PowerPoints", checked((uint)Math.Max(0, savedChar.statIntellect)));
            SetAvatarUInt32Property(avatar, "MaxTotalAttributePool", 0);
            SetAvatarUInt32Property(avatar, "PVPRating", savedChar.GetPvpRatingFixed32());
        }

        private static void WritePlayerPvpFields(LEWriter writer, SavedCharacter character)
        {
            writer.WriteUInt32(checked((uint)(character?.pvpWins ?? 0)));
            writer.WriteUInt32(character?.hasPvpRating == true ? 1u : 0u);
        }

        private static void WriteHeroPvpFields(LEWriter writer, SavedCharacter character)
        {
            writer.WriteUInt32(0);
            writer.WriteUInt32(character?.GetPvpRatingFixed32() ?? SavedCharacter.DefaultPvpRating * 256u);
        }

        private void StoreDroppedItemWithLifetime(ushort entityId, DroppedItemInfo info, bool freshDrop = true)
        {
            if (info == null)
                throw new ArgumentNullException(nameof(info));
            ActivateLootEntityId(entityId);
            info.ItemObjectState = freshDrop ? (byte)0 : (byte)6;
            info.ItemObjectStateCount = 0;
            info.ItemObjectStateTimer = freshDrop ? (ushort)0 : (ushort)27000;
            info.ItemObjectLifetimeTicks = ITEM_OBJECT_LIFETIME_TICKS;
            info.ItemObjectLastSimulationTick = _combatTick;
            StoreDroppedItem(entityId, info);
            _pendingDroppedItemExpirations.Remove(entityId);
            _droppedItemLifetimeTicks[entityId] = ITEM_OBJECT_LIFETIME_TICKS;
            CombatRuntime.Instance.RegisterItemObjectEntity(entityId, info?.RuntimeInstanceKey);
        }

        public void AdvanceDroppedItemLifecycle(ushort entityId, uint simulationTick)
        {
            if (!_droppedItems.TryGetValue(entityId, out DroppedItemInfo info) || info == null)
                return;
            if (info.ItemObjectLastSimulationTick == simulationTick)
                return;
            info.ItemObjectLastSimulationTick = simulationTick;
            if (info.ItemObjectState == 0)
            {
                info.ItemObjectState = 2;
                info.ItemObjectStateTimer = 15;
                return;
            }
            if (info.ItemObjectState != 2)
                return;
            if (info.ItemObjectStateTimer != 0)
                info.ItemObjectStateTimer--;
            if (info.ItemObjectStateTimer == 0)
            {
                info.ItemObjectState = 6;
                info.ItemObjectStateTimer = 27000;
            }
        }

        public bool SetDroppedItemLifecycle(ushort entityId, byte state, ushort timer, byte stateCount, uint simulationTick)
        {
            if (!_droppedItems.TryGetValue(entityId, out DroppedItemInfo info) || info == null)
                return false;
            info.ItemObjectState = state;
            info.ItemObjectStateTimer = timer;
            info.ItemObjectStateCount = stateCount;
            info.ItemObjectLastSimulationTick = simulationTick;
            return true;
        }

        private bool RemoveDroppedItemWithLifetime(ushort entityId, out DroppedItemInfo info)
        {
            bool removed = RemoveDroppedItem(entityId, out info);
            _pendingDroppedItemExpirations.Remove(entityId);
            _droppedItemLifetimeTicks.Remove(entityId);
            if (removed)
            {
                ReleaseQuestActivateDropBinding(entityId);
                RetireLootEntityId(entityId);
            }
            CombatRuntime.Instance.ScheduleItemObjectEntityRemoval(entityId);
            return removed;
        }

        private void TickDroppedItemLifetimeForEntity(ushort entityId, uint simulationTick)
        {
            if (!_droppedItems.TryGetValue(entityId, out DroppedItemInfo info))
            {
                _droppedItemLifetimeTicks.Remove(entityId);
                return;
            }

            if (!_droppedItemLifetimeTicks.TryGetValue(entityId, out uint remaining))
                remaining = ITEM_OBJECT_LIFETIME_TICKS;
            if (remaining > 0)
                remaining--;
            _droppedItemLifetimeTicks[entityId] = remaining;
            info.ItemObjectLifetimeTicks = remaining;
            if (remaining != 0)
                return;

            _pendingDroppedItemExpirations.Add(entityId);
            if (!_expiredDroppedItems.Any(expired => expired.EntityId == entityId))
                _expiredDroppedItems.Add((entityId, info, simulationTick));
        }

        private void FlushExpiredDroppedItems()
        {
            foreach (var expired in _expiredDroppedItems)
            {
                if (!_droppedItems.TryGetValue(expired.EntityId, out DroppedItemInfo current)
                    || !ReferenceEquals(current, expired.Info))
                    continue;

                if (expired.Info.DbId > 0)
                {
                    try
                    {
                        using var db = DungeonRunners.Database.GameDatabase.GetConnection();
                        DungeonRunners.Database.GameDatabase.ExecuteNonQuery(
                            db,
                            "DELETE FROM dropped_items WHERE id = @id",
                            ("@id", expired.Info.DbId));
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[ITEM-LIFETIME] state=db-delete-failed entity={expired.EntityId} dbId={expired.Info.DbId} tick={expired.SimulationTick} message='{ex.Message}'");
                        _droppedItemLifetimeTicks[expired.EntityId] = DROPPED_ITEM_DELETE_RETRY_TICKS;
                        expired.Info.ItemObjectLifetimeTicks = DROPPED_ITEM_DELETE_RETRY_TICKS;
                        continue;
                    }
                    _dbIdToEntityId.Remove(expired.Info.DbId);
                }

                if (!RemoveDroppedItemWithLifetime(expired.EntityId, out DroppedItemInfo removedInfo)
                    || !ReferenceEquals(removedInfo, expired.Info))
                    continue;
                List<RRConnection> recipients = GetConnectionInsertionOrderSnapshot();
                foreach (RRConnection other in recipients)
                {
                    if (other.IsSpawned && DroppedItemMatchesConnection(other, expired.Info))
                        SendDespawnEntity(other, expired.EntityId);
                }
                Debug.LogError($"[ITEM-LIFETIME] state=expired entity={expired.EntityId} zone='{expired.Info.Zone ?? ""}' instance={expired.Info.InstanceId:X8} runtime='{expired.Info.RuntimeInstanceKey ?? ""}' tick={expired.SimulationTick} lifetimeTicks={ITEM_OBJECT_LIFETIME_TICKS}");
            }
            _expiredDroppedItems.Clear();
        }

        private void SetZoneSpawnInvulnerability(RRConnection conn)
        {
            if (conn == null) return;
            conn.ZoneSpawnInvulnerabilityPending = false;
            conn.ZoneSpawnInvulnerabilityQueued = false;
            conn.ZoneSpawnInvulnerabilityDueTick = 0;
            conn.ZoneSpawnInvulnerabilityActive = true;
            ulong expires = (ulong)_combatTick + ZoneSpawnInvulnerabilityDuration;
            conn.ZoneSpawnInvulnerabilityExpiresTick = expires >= uint.MaxValue ? uint.MaxValue : (uint)expires;
            conn.ZoneSpawnInvulnerabilitySentTick = _combatTick;
            PlayerState state = GetPlayerState(conn.ConnId.ToString());
            if (state != null)
                state.IsZoneSpawnDamageImmune = true;
            Debug.LogError($"[ZONE-INVULN] Server immunity active for {conn.LoginName} tick={conn.ZoneSpawnInvulnerabilitySentTick}->{conn.ZoneSpawnInvulnerabilityExpiresTick}");
        }

        private void RefreshZoneSpawnInvulnerability(RRConnection conn)
        {
            if (conn == null || !conn.ZoneSpawnInvulnerabilityActive) return;
            if (conn.ZoneSpawnInvulnerabilityExpiresTick > 0 && _combatTick >= conn.ZoneSpawnInvulnerabilityExpiresTick)
                ClearZoneSpawnInvulnerability(conn, "EXPIRE");
        }

        private bool IsZoneSpawnInvulnerabilityActive(RRConnection conn)
        {
            if (conn == null) return false;
            RefreshZoneSpawnInvulnerability(conn);
            return conn.ZoneSpawnInvulnerabilityActive;
        }

        private void ClearZoneSpawnInvulnerability(RRConnection conn, string reason)
        {
            if (conn == null) return;
            PlayerState state = GetPlayerState(conn.ConnId.ToString());
            bool had = conn.ZoneSpawnInvulnerabilityPending || conn.ZoneSpawnInvulnerabilityActive || (state != null && state.IsZoneSpawnDamageImmune);
            if (!string.IsNullOrEmpty(conn.LoginName))
                UntrackModifier(conn.LoginName, "avatar.base.ZoneSpawnInvulnerabilityModifier");
            uint generation = unchecked(conn.ZoneSpawnInvulnerabilityGeneration + 1);
            conn.ZoneSpawnInvulnerabilityGeneration = generation == 0 ? 1u : generation;
            conn.ZoneSpawnInvulnerabilityPending = false;
            conn.ZoneSpawnInvulnerabilityQueued = false;
            conn.ZoneSpawnInvulnerabilityDueTick = 0;
            conn.ZoneSpawnInvulnerabilityActive = false;
            conn.ZoneSpawnInvulnerabilityExpiresTick = 0;
            conn.ZoneSpawnInvulnerabilitySentTick = 0;
            if (state != null)
                state.IsZoneSpawnDamageImmune = false;
            if (!had) return;
            Debug.LogError($"[ZONE-INVULN] Cleared server immunity for {conn.LoginName} reason={reason}");
        }

        private bool ShouldSendZoneSpawnInvulnerability(RRConnection conn)
        {
            if (conn == null) return false;

            string zoneName = conn.CurrentZoneName;
            if (string.IsNullOrWhiteSpace(zoneName) && conn.CurrentZoneId != 0 && _zones.TryGetValue(conn.CurrentZoneId, out Zone zone))
                zoneName = zone.name;

            return ZoneAllowsSpawnInvulnerability(zoneName);
        }

        private static bool ZoneAllowsSpawnInvulnerability(string zoneName)
        {
            zoneName = (zoneName ?? "").Trim().ToLowerInvariant();
            if (zoneName.Length == 0) return false;

            if (zoneName == "tutorial") return false;
            if (zoneName == "world.tutorial") return false;
            if (zoneName == "town") return false;
            if (zoneName == "world.town") return false;
            if (zoneName == "thehub") return false;
            if (zoneName == "world.thehub") return false;
            if (zoneName == "pvp_start") return false;
            if (zoneName == "pvp_hub") return false;
            if (zoneName.StartsWith("town")) return false;
            if (zoneName.StartsWith("world.town")) return false;
            if (zoneName.Contains("hub")) return false;

            if (zoneName == "amazon_dungeon") return true;
            if (zoneName.StartsWith("dungeon")) return true;
            if (zoneName.StartsWith("world.dungeon")) return true;
            if (zoneName.StartsWith("d0")) return true;
            if (zoneName.StartsWith("d1")) return true;
            if (zoneName.StartsWith("elite")) return true;
            if (zoneName.StartsWith("epic")) return true;
            if (zoneName.StartsWith("squeakeasy")) return true;
            if (zoneName.StartsWith("deathmatch")) return true;
            if (zoneName.StartsWith("pvpgroup")) return true;
            if (zoneName.StartsWith("pvpduel")) return true;

            return false;
        }

        private void AdminCompleteQuest(RRConnection conn, uint instanceId)
        {
            try
            {
                var completingQuest = QuestManager.Instance.GetQuestByInstanceId(conn.ConnId.ToString(), instanceId);
                if (completingQuest == null)
                {
                    Debug.LogError($"[ADMIN-COMPLETEQUEST] No active quest with instanceId={instanceId}");
                    return;
                }

                Debug.LogError($"[ADMIN-COMPLETEQUEST] Completing {completingQuest.QuestId} (instanceId={instanceId})");

                QuestData completedQuestData = AuthoredGameplayCatalog.Quests
                    .FirstOrDefault(q => q.id.Equals(completingQuest.QuestId, StringComparison.OrdinalIgnoreCase));

                int[] previousObjectiveProgress = completingQuest.Objectives?.Select(objective => objective.Current).ToArray() ?? Array.Empty<int>();
                if (completingQuest.Objectives != null)
                {
                    foreach (var objective in completingQuest.Objectives)
                    {
                        if (objective.Required > 0 && objective.Current < objective.Required)
                            objective.Current = objective.Required;
                    }
                }

                if (!TryCommitQuestTurnIn(conn, instanceId, completedQuestData?.repeatable != true || completedQuestData.minRepeatTimeSeconds != 0, completedQuestData, 0, out _))
                {
                    if (completingQuest.Objectives != null)
                    {
                        for (int index = 0; index < completingQuest.Objectives.Count && index < previousObjectiveProgress.Length; index++)
                            completingQuest.Objectives[index].Current = previousObjectiveProgress[index];
                    }
                    Debug.LogError($"[ADMIN-COMPLETEQUEST] state=failed phase=commit instanceId={instanceId}");
                    return;
                }
                Debug.LogError($"[ADMIN-COMPLETEQUEST] Done");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ADMIN-COMPLETEQUEST] error={ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private bool SavePlayerQuests(RRConnection conn)
        {
            return TrySaveFullCharacterSnapshot(conn, "quests");
        }

        public QuestProgressMutation StageQuestItemAcquisition(RRConnection conn, string itemGcType, int quantity = 1)
        {
            return QuestManager.Instance.StageItemPickup(conn, itemGcType, quantity);
        }

        public void CommitQuestItemAcquisition(RRConnection conn, QuestProgressMutation mutation, string itemGcType, int quantity, string source)
        {
            if (mutation == null)
                return;
            QuestManager.Instance.CommitProgress(conn, mutation);
            if (mutation.Updates.Count == 0)
                return;
            Debug.LogError($"[QUEST-ITEM] Item '{itemGcType}' quantity={quantity} updated {mutation.Updates.Count} quest objective(s)");
            if (QuestLootTracking)
                Debug.LogError($"[QUEST-LOOT-TRACK] source={source ?? "item-acquire"} characterId={GetCharSqlId(conn)} item='{itemGcType}' quantity={quantity} updates={mutation.Updates.Count} instance='{RoomRuntime.NormalizeInstanceKey(GetInstanceZoneKey(conn))}' dbCommit=true");
        }

        public void RollbackQuestItemAcquisition(QuestProgressMutation mutation)
        {
            QuestManager.Instance.RollbackProgress(mutation);
        }

        public bool NotifyQuestItemAcquired(RRConnection conn, string itemGcType, int quantity = 1)
        {
            if (conn == null || string.IsNullOrEmpty(itemGcType) || quantity <= 0)
                return false;
            QuestProgressMutation mutation = StageQuestItemAcquisition(conn, itemGcType, quantity);
            if (mutation.Updates.Count > 0 && !SavePlayerQuests(conn))
            {
                RollbackQuestItemAcquisition(mutation);
                if (QuestLootTracking)
                    Debug.LogError($"[QUEST-LOOT-TRACK] source=item-acquire characterId={GetCharSqlId(conn)} item='{itemGcType}' quantity={quantity} updates={mutation.Updates.Count} instance='{RoomRuntime.NormalizeInstanceKey(GetInstanceZoneKey(conn))}' dbCommit=false");
                return false;
            }
            CommitQuestItemAcquisition(conn, mutation, itemGcType, quantity, "item-acquire");
            return true;
        }

        private bool SavePlayerLevel(RRConnection conn, [CallerMemberName] string caller = null)
        {
            return TrySaveFullCharacterSnapshot(conn, $"level:{caller ?? "unknown"}");
        }

        public (GCObject item, byte x, byte y)? GetInventoryItemBySlot(string connId, uint slotIndex, byte containerId = 0x0B)
        {
            string key = InvKey(connId, containerId);
            if (_playerInventoryItems.ContainsKey(key) && _playerInventoryItems[key].ContainsKey(slotIndex))
            {
                return _playerInventoryItems[key][slotIndex];
            }
            Debug.LogError($"[INV-TRACK] No item at slot {slotIndex} in container 0x{containerId:X2}");
            return null;
        }

        public void RemoveInventoryItemBySlot(string connId, uint slotIndex, byte containerId = 0x0B)
        {
            string key = InvKey(connId, containerId);
            if (_playerInventoryItems.ContainsKey(key) && _playerInventoryItems[key].ContainsKey(slotIndex))
            {
                _playerInventoryItems[key].Remove(slotIndex);
                if (_playerInventoryOrder.ContainsKey(key)) _playerInventoryOrder[key].Remove(slotIndex);
                if (_inventoryStackCounts.ContainsKey(key)) _inventoryStackCounts[key].Remove(slotIndex);
                Debug.LogError($"[INV-TRACK] Removed item at slot {slotIndex} in container 0x{containerId:X2}");
            }
        }
        public Dictionary<uint, (GCObject item, byte x, byte y)> GetAllInventoryItems(string connId, byte containerId = 0x0B)
        {
            string key = InvKey(connId, containerId);
            if (_playerInventoryItems.ContainsKey(key))
                return _playerInventoryItems[key];
            return null;
        }

        public bool TryGetInventoryItemAt(string connId, byte x, byte y, byte containerId, out uint slotIndex,
            out (GCObject item, byte x, byte y) itemData)
        {
            slotIndex = 0;
            itemData = default;
            Dictionary<uint, (GCObject item, byte x, byte y)> items = GetAllInventoryItems(connId, containerId);
            string key = InvKey(connId, containerId);
            if (items == null || !_playerInventoryOrder.TryGetValue(key, out List<uint> order))
                return false;

            foreach (uint candidateSlot in order)
            {
                if (!items.TryGetValue(candidateSlot, out (GCObject item, byte x, byte y) entry))
                    continue;
                ItemData data = AuthoredGameplayCatalog.FindItem(entry.item.GCClass);
                int width = data?.inventoryWidth ?? 1;
                int height = data?.inventoryHeight ?? 1;
                if (x < entry.x || y < entry.y
                    || x >= entry.x + width || y >= entry.y + height)
                    continue;
                slotIndex = candidateSlot;
                itemData = entry;
                return true;
            }
            return false;
        }

        public byte? FindContainerForSlot(string connId, uint slotIndex)
        {
            if (_playerInventoryItems.ContainsKey(connId) && _playerInventoryItems[connId].ContainsKey(slotIndex))
                return 0x0B;
            byte[] bankIds = { 0x0C, 0x0E, 0x0F, 0x10, 0x11, 0x12, 0x13 };
            foreach (byte containerId in bankIds)
            {
                string key = InvKey(connId, containerId);
                if (_playerInventoryItems.ContainsKey(key) && _playerInventoryItems[key].ContainsKey(slotIndex))
                    return containerId;
            }
            return null;
        }

        public GCObject GetEquippedItem(string connId, uint slot)
        {
            Debug.LogError($"[GET-EQUIP] Looking for Player {connId} slot {slot}");

            if (!_playerEquippedItems.ContainsKey(connId))
            {
                Debug.LogError($"[GET-EQUIP]  Player {connId} NOT in _playerEquippedItems!");
                Debug.LogError($"[GET-EQUIP] Available players: {string.Join(", ", _playerEquippedItems.Keys)}");
                return null;
            }

            if (!_playerEquippedItems[connId].ContainsKey(slot))
            {
                Debug.LogError($"[GET-EQUIP]  Slot {slot} not found for player {connId}!");
                Debug.LogError($"[GET-EQUIP] Available slots: {string.Join(", ", _playerEquippedItems[connId].Keys)}");
                return null;
            }

            GCObject item = _playerEquippedItems[connId][slot];
            Debug.LogError($"[GET-EQUIP]  Found: {item.GCClass}");
            return item;
        }
        public void TrackManipulatorsId(string connId, ushort manipulatorsId)
        {
            _playerManipulatorsIds[connId] = manipulatorsId;
            Debug.LogError($"[MANIPULATORS-TRACK] Player {connId}: Manipulators ID = 0x{manipulatorsId:X4}");
        }

        public ushort GetManipulatorsComponentId(string connId)
        {
            if (_playerManipulatorsIds.ContainsKey(connId))
                return _playerManipulatorsIds[connId];
            Debug.LogError($"[MANIPULATORS]  No ID tracked for player {connId}!");
            return 0;
        }
        public ushort GetUnitContainerComponentId(string connId)
        {
            if (_playerUnitContainerComponentIds.TryGetValue(connId, out ushort componentId))
                return componentId;
            Debug.LogError($"[UNITCONTAINER]  No ID tracked for player {connId}!");
            return 0;
        }


        public bool TrackDroppedItem(ushort entityId, GCObject item, RRConnection conn, int quantity = 1, int? posFixedX = null, int? posFixedY = null, int? posFixedZ = null, int? playerLevelOverride = null, int? headingFixed = null, bool requirePublicPersistence = false, string questBindingKey = null, uint goldAmount = 0, bool generatedByBlingGnome = false)
        {
            uint ownerGroupId = string.IsNullOrWhiteSpace(questBindingKey)
                ? GroupDirectory.Instance.GetGroupForConn(conn.ConnId)?.GroupId ?? 0u
                : 0u;
            var info = new DroppedItemInfo
            {
                Item = item,
                DbId = 0,
                Zone = conn.CurrentZoneName ?? "",
                ZoneId = conn.CurrentZoneId,
                InstanceId = conn.InstanceId,
                RuntimeInstanceKey = conn.RuntimeInstanceKey ?? "",
                PosFixedX = posFixedX ?? conn.PlayerPosFixedX,
                PosFixedY = posFixedY ?? conn.PlayerPosFixedY,
                PosFixedZ = posFixedZ ?? conn.PlayerPosFixedZ,
                HeadingFixed = headingFixed ?? 0,
                PlayerLevel = playerLevelOverride ?? (GetPlayerState(conn.ConnId.ToString())?.Level ?? 1),
                DroppedBy = conn.LoginName ?? "",
                OwnerCharacterId = GetCharSqlId(conn),
                OwnerGroupId = ownerGroupId,
                OwnerName = ResolveCharacterName(conn),
                QuestBindingKey = questBindingKey ?? "",
                Quantity = quantity,
                IsGoldDrop = goldAmount > 0,
                GoldAmount = goldAmount,
                GeneratedByBlingGnome = generatedByBlingGnome
            };

            bool publicZone = IsPublicZone(info.Zone);
            bool persistenceCommitted = !publicZone;
            if (publicZone)
            {
                try
                {
                    using (var db = DungeonRunners.Database.GameDatabase.GetConnection())
                    {
                        SavedItemRuntimeState itemState = SavedItemRuntimeState.Capture(item);
                        DungeonRunners.Database.GameDatabase.ExecuteNonQuery(db,
                            @"INSERT INTO dropped_items (zone, zone_id, instance_id, gc_class, dfc_class, pos_x, pos_y, pos_z, pos_fixed_x, pos_fixed_y, pos_fixed_z, player_level, quantity, target_slot, preset_scale_mod, has_generated_item_state, rolled_requires_membership, generated_requires_membership, soul_bound, no_sell, soul_bound_countdown, generated_item_modifiers, dropped_by, rarity, stored_level, owner_character_id, owner_group_id, owner_name, quest_binding_key, heading_fixed, gold_amount, generated_by_bling_gnome)
                              VALUES (@zone, @zid, @iid, @gc, @nc, 0, 0, 0, @pfx, @pfy, @pfz, @pl, @qty, @ts, @psm, @hgs, @rrm, @grm, @sb, @ns, @sbc, @gim, @db, @rar, @slv, @ocid, @ogid, @oname, @qbk, @heading, @gold, @gbg)",
                            ("@zone", info.Zone),
                            ("@zid", unchecked((int)info.ZoneId)),
                            ("@iid", unchecked((int)info.InstanceId)),
                            ("@gc", item.GCClass),
                            ("@nc", item.DFCClass ?? "Armor"),
                            ("@pfx", info.PosFixedX),
                            ("@pfy", info.PosFixedY),
                            ("@pfz", info.PosFixedZ),
                            ("@pl", info.PlayerLevel),
                            ("@qty", Math.Clamp(info.Quantity, 1, byte.MaxValue)),
                            ("@ts", (int)(item.TargetSlot ?? 0xFFFFFFFF)),
                            ("@psm", itemState.presetScaleMod ?? ""),
                            ("@hgs", itemState.hasGeneratedItemState ? 1 : 0),
                            ("@rrm", itemState.rolledRequiresMembership ? 1 : 0),
                            ("@grm", itemState.generatedRequiresMembership ? 1 : 0),
                            ("@sb", itemState.soulBound ? 1 : 0),
                            ("@ns", itemState.noSell ? 1 : 0),
                            ("@sbc", itemState.soulBoundCountdown),
                            ("@gim", itemState.SerializeModifiers()),
                            ("@db", info.DroppedBy),
                            ("@rar", item.GetEffectiveRarity()),
                            ("@slv", item.StoredLevel),
                            ("@ocid", (int)info.OwnerCharacterId),
                            ("@ogid", (int)info.OwnerGroupId),
                            ("@oname", info.OwnerName ?? ""),
                            ("@qbk", info.QuestBindingKey ?? ""),
                            ("@heading", info.HeadingFixed),
                            ("@gold", checked((long)info.GoldAmount)),
                            ("@gbg", info.GeneratedByBlingGnome ? 1 : 0));

                        long dbId = Convert.ToInt64(DungeonRunners.Database.GameDatabase.ExecuteScalar(db, "SELECT last_insert_rowid()"));
                        info.DbId = dbId;
                        _dbIdToEntityId[dbId] = entityId;
                        persistenceCommitted = true;
                        Debug.LogError($"[DROP-TRACK] Saved to DB: dbId={dbId}, entityId={entityId}, GCClass={item.GCClass}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[DROP-TRACK]  DB save failed: {ex.Message}");
                    if (requirePublicPersistence)
                    {
                        CancelLootEntityIdReservation(entityId);
                        return false;
                    }
                }
            }
            else
                Debug.LogError($"[DROP-TRACK] scope=room entityId={entityId} runtime='{info.RuntimeInstanceKey}' persistence=disabled");

            try
            {
                StoreDroppedItemWithLifetime(entityId, info);
            }
            catch (Exception ex)
            {
                _dbIdToEntityId.Remove(info.DbId);
                if (info.DbId > 0)
                {
                    try
                    {
                        using var db = DungeonRunners.Database.GameDatabase.GetConnection();
                        DungeonRunners.Database.GameDatabase.ExecuteNonQuery(db, "DELETE FROM dropped_items WHERE id = @id", ("@id", info.DbId));
                    }
                    catch (Exception rollbackException)
                    {
                        Debug.LogError($"[DROP-TRACK] state=failed phase=world-store-rollback entity={entityId} dbId={info.DbId} message='{rollbackException.Message}'");
                    }
                }
                Debug.LogError($"[DROP-TRACK] state=failed phase=world-store entity={entityId} dbId={info.DbId} message='{ex.Message}'");
                CancelLootEntityIdReservation(entityId);
                return false;
            }
            Debug.LogError($"[DROP-TRACK] Stored dropped item: entityId={entityId}, GCClass={item.GCClass}, zone={info.Zone}, instance={info.InstanceId}");
            return persistenceCommitted;
        }

        public bool DropInventoryItem(RRConnection conn, ushort componentId, GCObject item, int playerLevel, int quantity)
        {
            if (conn == null || item == null)
                return false;

            ResolveSpellActorPoint(conn, out int actorFixedX, out int actorFixedY, out int actorFixedZ);
            int sourceHeadingFixed = ResolveSpellSourceHeadingFixed(conn);
            ItemDropPlacement placement = ResolveItemDropPlacement(
                conn,
                conn.CurrentZoneName,
                conn.InstanceId,
                actorFixedX,
                actorFixedY,
                actorFixedZ,
                sourceHeadingFixed,
                $"inventory-drop:{conn.LoginName}:{item.GCClass}");
            if (!placement.Success)
            {
                Debug.LogError($"[DROP] blocked item={item.GCClass} reason=missing-pathmap zone='{conn.CurrentZoneName}' instance={conn.InstanceId:X8}");
                return false;
            }

            int dropQuantity = Math.Max(1, quantity);
            if (dropQuantity > byte.MaxValue)
                return false;
            string connId = conn.ConnId.ToString();
            PlayerState playerState = GetPlayerState(connId);
            if (playerState == null || !ReferenceEquals(playerState.ActiveItem, item))
                return false;

            int itemHeadingFixed = ConsumeItemAddToWorldHeading($"inventory-drop:{conn.LoginName}:{item.GCClass}");
            ushort entityId = GetNextLootEntityId();
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
                HeadingFixed = itemHeadingFixed,
                PlayerLevel = Math.Max(1, playerLevel),
                DroppedBy = conn.LoginName ?? "",
                OwnerCharacterId = GetCharSqlId(conn),
                OwnerGroupId = GroupDirectory.Instance.GetGroupForConn(conn.ConnId)?.GroupId ?? 0u,
                OwnerName = ResolveCharacterName(conn),
                Quantity = dropQuantity
            };

            int previousCursorQuantity = GetStackCount(connId, 0xFFFFFFFF);
            playerState.ActiveItem = null;
            SetStackCount(connId, 0xFFFFFFFF, 0);
            bool publicDrop = IsPublicZone(info.Zone);
            if (publicDrop)
            {
                if (!TrySaveFullCharacterSnapshotCreatingDrop(conn, info, "item-drop-public", out long droppedItemId))
                {
                    playerState.ActiveItem = item;
                    SetStackCount(connId, 0xFFFFFFFF, previousCursorQuantity);
                    CancelLootEntityIdReservation(entityId);
                    return false;
                }
                info.DbId = droppedItemId;
                _dbIdToEntityId[droppedItemId] = entityId;
            }
            else if (!TrySaveFullCharacterSnapshot(conn, "item-drop-room"))
            {
                playerState.ActiveItem = item;
                SetStackCount(connId, 0xFFFFFFFF, previousCursorQuantity);
                CancelLootEntityIdReservation(entityId);
                return false;
            }

            try
            {
                StoreDroppedItemWithLifetime(entityId, info);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DROP] state=failed phase=world-store item={item.GCClass} entity={entityId} dbId={info.DbId} message='{ex.Message}'");
                if (publicDrop)
                {
                    _dbIdToEntityId.Remove(info.DbId);
                    playerState.ActiveItem = item;
                    SetStackCount(connId, 0xFFFFFFFF, previousCursorQuantity);
                    if (!TrySaveFullCharacterSnapshotConsumingDrop(conn, info.DbId, "item-drop-public-world-rollback"))
                    {
                        playerState.ActiveItem = null;
                        SetStackCount(connId, 0xFFFFFFFF, 0);
                        Debug.LogError($"[DROP] state=failed phase=world-store-compensation item={item.GCClass} entity={entityId} dbId={info.DbId}");
                    }
                }
                else
                {
                    playerState.ActiveItem = item;
                    SetStackCount(connId, 0xFFFFFFFF, previousCursorQuantity);
                    TrySaveFullCharacterSnapshot(conn, "item-drop-room-rollback");
                }
                CancelLootEntityIdReservation(entityId);
                return false;
            }

            var ownerBody = new LEWriter();
            ownerBody.WriteByte(0x35);
            ownerBody.WriteUInt16(componentId);
            ownerBody.WriteByte(0x29);
            WritePlayerEntitySynch(conn, ownerBody);
            WriteItemObjectEntityInit(ownerBody, entityId, info);
            item.WriteInitForDroppedItem(ownerBody, info.PlayerLevel, info.Quantity);
            SendToClient(conn, WrapClientEntityBody(ownerBody));

            byte[] peerPacket = BuildDroppedItemSpawnPacket(entityId, info);
            foreach (RRConnection other in GetConnectionInsertionOrderSnapshot())
            {
                if (other == conn || !other.IsSpawned || !DroppedItemMatchesConnection(other, info))
                    continue;
                SendToClient(other, peerPacket);
            }

            Debug.LogError($"[DROP] item={item.GCClass} entity={entityId} sourceFixed=({actorFixedX},{actorFixedY},{actorFixedZ}) posFixed=({info.PosFixedX},{info.PosFixedY},{info.PosFixedZ}) headingFixed={info.HeadingFixed} state={info.ItemObjectState} timer={info.ItemObjectStateTimer} lifetime={info.ItemObjectLifetimeTicks}");
            return true;
        }

        public bool SpawnBlingGnomeGeneratedItem(RRConnection conn, LootDrop drop, int sourceFixedX, int sourceFixedY, int sourceFixedZ, int sourceHeadingFixed)
        {
            if (conn == null || drop == null || !drop.IsItem || string.IsNullOrWhiteSpace(drop.GCType))
                return false;
            int playerLevel = Math.Max(1, GetPlayerState(conn.ConnId.ToString())?.Level ?? 1);
            var item = new GCObject
            {
                GCClass = drop.GCType,
                DFCClass = ResolveAuthoredItemClass(drop.GCType),
                PresetScaleMod = drop.ScaleMod,
                StoredRarity = (int)drop.Rarity,
                StoredLevel = drop.ItemLevel,
                HasGeneratedItemState = drop.HasGeneratedItemState,
                RolledRequiresMembership = drop.RolledRequiresMembership,
                GeneratedRequiresMembership = drop.RequiresMembership,
                GeneratedItemModifiers = new List<string>(drop.ItemModifiers)
            };
            string source = $"bling-gnome-item:{conn.LoginName}:{drop.GCType}";
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
            {
                Debug.LogError($"[GNOME-BONUS] blocked item={drop.GCType} reason=missing-pathmap zone='{conn.CurrentZoneName}' instance={conn.InstanceId:X8}");
                return false;
            }
            int itemHeadingFixed = ConsumeItemAddToWorldHeading(source);
            ushort entityId = GetNextLootEntityId();
            if (!TrackDroppedItem(
                entityId,
                item,
                conn,
                1,
                placement.FixedX,
                placement.FixedY,
                placement.FixedZ,
                playerLevel,
                itemHeadingFixed,
                requirePublicPersistence: true,
                generatedByBlingGnome: true))
                return false;
            if (!_droppedItems.TryGetValue(entityId, out DroppedItemInfo info))
                return false;
            BroadcastDroppedItemSpawnPacket(conn, entityId, info);
            Debug.LogError($"[GNOME-BONUS] entity={entityId} item={drop.GCType} level={drop.ItemLevel} rarity={drop.Rarity} sourceFixed=({sourceFixedX},{sourceFixedY},{sourceFixedZ}) posFixed=({info.PosFixedX},{info.PosFixedY},{info.PosFixedZ}) headingFixed={info.HeadingFixed}");
            return true;
        }

        public bool IsDroppedItem(ushort entityId)
        {
            return _droppedItems.ContainsKey(entityId);
        }

        private bool DroppedItemMatchesConnection(RRConnection conn, DroppedItemInfo info)
        {
            if (conn == null || info == null)
                return false;
            if (!string.Equals(info.Zone ?? "", conn.CurrentZoneName ?? "", StringComparison.OrdinalIgnoreCase)
                || info.InstanceId != conn.InstanceId)
                return false;
            string itemInstanceKey = RoomRuntime.NormalizeInstanceKey(info.RuntimeInstanceKey);
            string connectionInstanceKey = RoomRuntime.NormalizeInstanceKey(conn.RuntimeInstanceKey);
            return string.Equals(itemInstanceKey, connectionInstanceKey, StringComparison.OrdinalIgnoreCase);
        }

        private bool CanAcquireDroppedItem(RRConnection conn, ushort entityId, DroppedItemInfo info)
        {
            if (conn == null || info == null || _pendingDroppedItemExpirations.Contains(entityId))
                return false;
            if (info.OwnerGroupId != 0)
            {
                uint requesterGroupId = GroupDirectory.Instance.GetGroupForConn(conn.ConnId)?.GroupId ?? 0u;
                return requesterGroupId != 0 && requesterGroupId == info.OwnerGroupId;
            }
            if (info.OwnerCharacterId == 0)
                return true;
            uint characterId = GetCharSqlId(conn);
            return characterId != 0 && info.OwnerCharacterId == characterId;
        }

        private bool RollbackTrackedDroppedItem(ushort entityId, string source)
        {
            if (!_droppedItems.TryGetValue(entityId, out DroppedItemInfo info) || info == null)
                return true;
            if (info.DbId > 0)
            {
                try
                {
                    using var db = DungeonRunners.Database.GameDatabase.GetConnection();
                    DungeonRunners.Database.GameDatabase.ExecuteNonQuery(
                        db,
                        "DELETE FROM dropped_items WHERE id = @id",
                        ("@id", info.DbId));
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[DROP-TRACK] state=failed phase=durable-rollback source={source ?? "unknown"} entity={entityId} dbId={info.DbId} message='{ex.Message}'");
                    return false;
                }
                _dbIdToEntityId.Remove(info.DbId);
            }
            return RemoveDroppedItemWithLifetime(entityId, out _);
        }

        private int ClearDroppedItemsForInstance(string zone, uint instanceId, string reason)
        {
            var entityIds = new List<ushort>();
            for (int itemIndex = 0; itemIndex < _droppedItemOrder.Count; itemIndex++)
            {
                ushort entityId = _droppedItemOrder[itemIndex];
                if (!_droppedItems.TryGetValue(entityId, out DroppedItemInfo info))
                    continue;
                if (info == null
                    || !string.Equals(info.Zone ?? "", zone ?? "", StringComparison.OrdinalIgnoreCase)
                    || info.InstanceId != instanceId)
                    continue;
                entityIds.Add(entityId);
            }

            foreach (ushort entityId in entityIds)
            {
                DroppedItemInfo info = _droppedItems[entityId];
                if (info.DbId > 0)
                    _dbIdToEntityId.Remove(info.DbId);
                RemoveDroppedItemWithLifetime(entityId, out _);
            }

            int databaseRows = 0;
            try
            {
                using (var db = DungeonRunners.Database.GameDatabase.GetConnection())
                {
                    databaseRows = Convert.ToInt32(DungeonRunners.Database.GameDatabase.ExecuteScalar(
                        db,
                        "SELECT COUNT(*) FROM dropped_items WHERE zone = @zone AND instance_id = @iid",
                        ("@zone", zone ?? ""),
                        ("@iid", unchecked((int)instanceId))));
                    DungeonRunners.Database.GameDatabase.ExecuteNonQuery(
                        db,
                        "DELETE FROM dropped_items WHERE zone = @zone AND instance_id = @iid",
                        ("@zone", zone ?? ""),
                        ("@iid", unchecked((int)instanceId)));
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DROP-EPOCH] state=db-delete-failed zone='{zone ?? ""}' instance={instanceId:X8} message='{ex.Message}'");
            }

            Debug.LogError($"[DROP-EPOCH] zone='{zone ?? ""}' instance={instanceId:X8} memoryRemoved={entityIds.Count} databaseRemoved={databaseRows} reason={reason ?? "unknown"}");
            return entityIds.Count;
        }

        private void SendDroppedItemsForZone(RRConnection conn)
        {
            string zone = conn.CurrentZoneName ?? "";
            uint instanceId = conn.InstanceId;
            bool publicZone = IsPublicZone(zone);

            Debug.LogError($"[DROP-LOAD] Loading dropped items for zone={zone}, instance={instanceId}");

            if (publicZone)
            {
                if (!CleanupExpiredDroppedItems())
                {
                    Debug.LogError($"[DROP-LOAD] state=blocked phase=expiry-reconcile zone={zone} instance={instanceId:X8}");
                    return;
                }
                if (_expiredDroppedItems.Count > 0)
                    FlushExpiredDroppedItems();
            }

            int inMemoryCount = 0;
            for (int itemIndex = 0; itemIndex < _droppedItemOrder.Count; itemIndex++)
            {
                ushort entityId = _droppedItemOrder[itemIndex];
                if (!_droppedItems.TryGetValue(entityId, out DroppedItemInfo info))
                    continue;
                if (_pendingDroppedItemExpirations.Contains(entityId))
                    continue;
                if (!DroppedItemMatchesConnection(conn, info)) continue;

                if (info.GoldAmount > 0)
                    SendGoldPileSpawnPacket(conn, entityId, info);
                else
                    SendDroppedItemSpawnPacket(conn, entityId, info);
                inMemoryCount++;
            }
            Debug.LogError($"[DROP-LOAD] Re-sent {inMemoryCount} in-memory items");

            if (!publicZone)
            {
                Debug.LogError($"[DROP-LOAD] Zone load complete: {inMemoryCount} in-memory + 0 from DB scope=room");
                return;
            }

            int dbCount = 0;
            try
            {
                using (var db = DungeonRunners.Database.GameDatabase.GetConnection())
                using (var reader = DungeonRunners.Database.GameDatabase.ExecuteReader(db,
                    $"SELECT id, gc_class, dfc_class, pos_fixed_x, pos_fixed_y, pos_fixed_z, player_level, COALESCE(quantity, 1), target_slot, preset_scale_mod, COALESCE(has_generated_item_state, 0), COALESCE(rolled_requires_membership, 0), COALESCE(generated_requires_membership, 0), COALESCE(soul_bound, 0), COALESCE(no_sell, 0), COALESCE(soul_bound_countdown, 65535), COALESCE(generated_item_modifiers, '[]'), dropped_by, COALESCE(rarity, -1), COALESCE(stored_level, -1), COALESCE(owner_character_id, 0), COALESCE(owner_name, ''), COALESCE(quest_binding_key, ''), COALESCE(heading_fixed, 0), COALESCE(gold_amount, 0), COALESCE(generated_by_bling_gnome, 0), COALESCE(CAST(MAX(0, strftime('%s','now') - strftime('%s', dropped_at)) AS INTEGER), 0), COALESCE(owner_group_id, 0) FROM dropped_items WHERE zone = @zone AND instance_id = @iid AND dropped_at >= datetime('now', '-{DROPPED_ITEM_EXPIRE_MINUTES} minutes') ORDER BY id",
                    ("@zone", zone), ("@iid", unchecked((int)instanceId))))
                {
                    while (reader.Read())
                    {
                        long dbId = reader.GetInt64(0);

                        if (_dbIdToEntityId.ContainsKey(dbId)) continue;

                        string gcClass = reader.GetString(1);
                        string dfcClass = reader.GetString(2);
                        int posFixedX = reader.GetInt32(3);
                        int posFixedY = reader.GetInt32(4);
                        int posFixedZ = reader.GetInt32(5);
                        int playerLevel = reader.GetInt32(6);
                        int quantity = reader.GetInt32(7);
                        int targetSlotRaw = reader.GetInt32(8);
                        string presetScaleMod = reader.IsDBNull(9) ? "" : reader.GetString(9);
                        int hasGeneratedItemState = reader.GetInt32(10);
                        int rolledRequiresMembership = reader.GetInt32(11);
                        int generatedRequiresMembership = reader.GetInt32(12);
                        int soulBound = reader.GetInt32(13);
                        int noSell = reader.GetInt32(14);
                        int soulBoundCountdown = reader.GetInt32(15);
                        string generatedItemModifiers = reader.GetString(16);
                        string droppedBy = reader.IsDBNull(17) ? "" : reader.GetString(17);
                        int itemRarity = reader.GetInt32(18);
                        int itemStoredLevel = reader.GetInt32(19);
                        uint ownerCharacterId = unchecked((uint)reader.GetInt64(20));
                        string ownerName = reader.IsDBNull(21) ? "" : reader.GetString(21);
                        string questBindingKey = reader.IsDBNull(22) ? "" : reader.GetString(22);
                        int headingFixed = reader.GetInt32(23);
                        uint goldAmount = checked((uint)reader.GetInt32(24));
                        bool generatedByBlingGnome = reader.GetInt32(25) != 0;
                        int elapsedSeconds = Math.Max(0, reader.GetInt32(26));
                        uint ownerGroupId = unchecked((uint)Math.Max(0, reader.GetInt64(27)));

                        if (!SavedItemRuntimeState.TryRestore(presetScaleMod, hasGeneratedItemState, rolledRequiresMembership, generatedRequiresMembership, soulBound, noSell, soulBoundCountdown, generatedItemModifiers, out SavedItemRuntimeState itemState, out string stateReason))
                        {
                            Debug.LogError($"[DROP-LOAD] CORRUPT ITEM STATE dbId={dbId} gc={gcClass} reason={stateReason}");
                            continue;
                        }

                        var item = new GCObject
                        {
                            GCClass = gcClass,
                            DFCClass = dfcClass,
                            StoredRarity = itemRarity,
                            StoredLevel = itemStoredLevel
                        };
                        if (targetSlotRaw >= 0 && targetSlotRaw != -1 && (uint)targetSlotRaw != 0xFFFFFFFF)
                            item.TargetSlot = (uint)targetSlotRaw;
                        if (!string.IsNullOrEmpty(presetScaleMod))
                            item.PresetScaleMod = presetScaleMod;
                        itemState.ApplyTo(item);

                        ushort entityId = GetNextLootEntityId();
                        var info = new DroppedItemInfo
                        {
                            Item = item,
                            DbId = dbId,
                            Zone = zone,
                            ZoneId = conn.CurrentZoneId,
                            InstanceId = instanceId,
                            RuntimeInstanceKey = conn.RuntimeInstanceKey ?? "",
                            PosFixedX = posFixedX,
                            PosFixedY = posFixedY,
                            PosFixedZ = posFixedZ,
                            HeadingFixed = headingFixed,
                            PlayerLevel = playerLevel,
                            Quantity = quantity,
                            DroppedBy = droppedBy,
                            OwnerCharacterId = ownerCharacterId,
                            OwnerGroupId = ownerGroupId,
                            OwnerName = ownerName,
                            QuestBindingKey = questBindingKey,
                            IsQuestItem = !string.IsNullOrEmpty(questBindingKey),
                            IsGoldDrop = goldAmount > 0,
                            GoldAmount = goldAmount,
                            GeneratedByBlingGnome = generatedByBlingGnome
                        };

                        StoreDroppedItemWithLifetime(entityId, info, false);
                        long elapsedLifetimeTicks = (long)elapsedSeconds * SimulationClock.TicksPerSecond;
                        uint remainingLifetimeTicks = elapsedLifetimeTicks >= ITEM_OBJECT_LIFETIME_TICKS
                            ? 1u
                            : ITEM_OBJECT_LIFETIME_TICKS - checked((uint)elapsedLifetimeTicks);
                        info.ItemObjectLifetimeTicks = remainingLifetimeTicks;
                        info.ItemObjectStateTimer = checked((ushort)remainingLifetimeTicks);
                        _droppedItemLifetimeTicks[entityId] = remainingLifetimeTicks;
                        _dbIdToEntityId[dbId] = entityId;
                        if (!string.IsNullOrEmpty(questBindingKey))
                            RegisterQuestActivateDropBinding(entityId, questBindingKey);

                        if (goldAmount > 0)
                            SendGoldPileSpawnPacket(conn, entityId, info);
                        else
                            SendDroppedItemSpawnPacket(conn, entityId, info);
                        dbCount++;

                        Debug.LogError($"[DROP-LOAD] Loaded from DB: dbId={dbId}, entityId={entityId}, gc={gcClass}, posFixed=({posFixedX},{posFixedY},{posFixedZ})");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DROP-LOAD]  DB load failed: {ex.Message}");
            }

            Debug.LogError($"[DROP-LOAD]  Zone load complete: {inMemoryCount} in-memory + {dbCount} from DB");
        }

        private void SendDroppedItemSpawnPacket(RRConnection conn, ushort entityId, DroppedItemInfo info)
        {
            var body = new LEWriter();
            WriteItemObjectEntityInit(body, entityId, info);
            int beforeInit = body.Position;
            info.Item.WriteInitForDroppedItem(body, info.PlayerLevel, info.Quantity);
            int afterInit = body.Position;
            byte[] finalPacket = WrapClientEntityBody(body);

            if (info.Item != null)
            {
                byte[] bodyBytes = body.ToArray();
                int writeInitLen = afterInit - beforeInit;
                string writeInitHex = BitConverter.ToString(bodyBytes, beforeInit, writeInitLen).Replace("-", " ");
                string fullHex = BitConverter.ToString(finalPacket).Replace("-", " ");
                Debug.LogError($"[DROP-WRITEINIT] gc={info.Item.GCClass} writeInitBytes={writeInitLen} hex={writeInitHex}");
                Debug.LogError($"[DROP-WRITEINIT] full-packet ({finalPacket.Length}B) hex={fullHex}");
            }

            QueueClientEntityStream(conn, finalPacket);
        }

        private static void WriteItemObjectEntityInit(LEWriter body, ushort entityId, DroppedItemInfo info)
        {
            body.WriteByte(0x01);
            body.WriteUInt16(entityId);
            body.WriteByte(0xFF);
            body.WriteCString("itemobject");
            body.WriteByte(0x02);
            body.WriteUInt16(entityId);
            body.WriteUInt32(0x00000006);
            body.WriteInt32(info.PosFixedX);
            body.WriteInt32(info.PosFixedY);
            body.WriteInt32(info.PosFixedZ);
            body.WriteInt32(info.HeadingFixed);
            body.WriteByte(0xF7);
            body.WriteUInt16(0x0000);
            body.WriteByte(0x00);
            body.WriteUInt32(info.OwnerGroupId);
            body.WriteByte(info.ItemObjectState);
            body.WriteUInt16(info.ItemObjectStateTimer);
            body.WriteUInt32(info.OwnerCharacterId);
            body.WriteUInt32(0x00000000);
            body.WriteUInt32(info.ItemObjectLifetimeTicks);
            body.WriteByte(0x01);
            if (info.OwnerCharacterId != 0)
                body.WriteCString(info.OwnerName ?? "");
        }

        private static byte[] BuildDroppedItemSpawnPacket(ushort entityId, DroppedItemInfo info)
        {
            var body = new LEWriter();
            WriteItemObjectEntityInit(body, entityId, info);
            info.Item.WriteInitForDroppedItem(body, info.PlayerLevel, info.Quantity);
            return WrapClientEntityBody(body);
        }

        private static byte[] WrapClientEntityBody(LEWriter body)
        {
            var channel = new LEWriter();
            channel.WriteByte(0x07);
            channel.WriteBytes(body.ToArray());
            channel.WriteByte(0x06);
            return channel.ToArray();
        }

        private void BroadcastDroppedItemSpawnPacket(RRConnection conn, ushort entityId, DroppedItemInfo info)
        {
            SendDroppedItemSpawnPacket(conn, entityId, info);
            foreach (RRConnection other in GetConnectionInsertionOrderSnapshot())
            {
                if (other == conn || !other.IsSpawned) continue;
                if (other.CurrentZoneGcType != conn.CurrentZoneGcType || other.InstanceId != conn.InstanceId) continue;
                SendDroppedItemSpawnPacket(other, entityId, info);
            }
        }

        private void BroadcastGoldPileSpawnPacket(RRConnection conn, ushort entityId, DroppedItemInfo info)
        {
            SendGoldPileSpawnPacket(conn, entityId, info);
            foreach (var other in GetConnectionInsertionOrderSnapshot())
            {
                if (other == conn || !other.IsSpawned) continue;
                if (other.CurrentZoneGcType != conn.CurrentZoneGcType || other.InstanceId != conn.InstanceId) continue;
                SendGoldPileSpawnPacket(other, entityId, info);
            }
        }

        private void SendGoldPileSpawnPacket(RRConnection conn, ushort entityId, DroppedItemInfo info)
        {
            uint goldAmount = info.GoldAmount;
            GCObject currency = info.Item;

            var body = new LEWriter();
            WriteItemObjectEntityInit(body, entityId, info);
            body.WriteByte(0xFF);
            body.WriteCString("Currency");
            body.WriteUInt32(0);
            body.WriteByte(0x00);
            body.WriteByte(0x00);
            body.WriteByte(0x01);
            body.WriteByte(0x01);
            byte flags = 0;
            if (currency?.SoulBound == true)
                flags |= 0x01;
            if (currency?.NoSell == true)
                flags |= 0x02;
            if (currency != null && currency.SoulBoundCountdown != ushort.MaxValue)
                flags |= 0x04;
            if (currency?.GetRequiresMembership() == true)
                flags |= 0x08;
            body.WriteByte(flags);
            if ((flags & 0x04) != 0)
                body.WriteUInt16(currency.SoulBoundCountdown);
            body.WriteByte(0x00);
            body.WriteUInt32(goldAmount);

            QueueClientEntityStream(conn, WrapClientEntityBody(body));
        }


        private bool CleanupExpiredDroppedItems()
        {
            List<long> expiredDbIds = new List<long>();
            try
            {
                using (var db = DungeonRunners.Database.GameDatabase.GetConnection())
                {
                    using (var reader = DungeonRunners.Database.GameDatabase.ExecuteReader(db,
                        $"SELECT id FROM dropped_items WHERE dropped_at < datetime('now', '-{DROPPED_ITEM_EXPIRE_MINUTES} minutes')"))
                    {
                        while (reader.Read())
                            expiredDbIds.Add(reader.GetInt64(0));
                    }

                    int deleted = 0;
                    foreach (long dbId in expiredDbIds)
                    {
                        if (_dbIdToEntityId.TryGetValue(dbId, out ushort entityId)
                            && _droppedItems.TryGetValue(entityId, out DroppedItemInfo info)
                            && info != null
                            && info.DbId == dbId)
                        {
                            _droppedItemLifetimeTicks[entityId] = 0;
                            info.ItemObjectLifetimeTicks = 0;
                            _pendingDroppedItemExpirations.Add(entityId);
                            if (!_expiredDroppedItems.Any(expired => expired.EntityId == entityId))
                                _expiredDroppedItems.Add((entityId, info, SimulationClock.SimulationTick));
                            continue;
                        }
                        _dbIdToEntityId.Remove(dbId);
                        DungeonRunners.Database.GameDatabase.ExecuteNonQuery(db,
                            "DELETE FROM dropped_items WHERE id = @id",
                            ("@id", dbId));
                        deleted++;
                    }
                    if (deleted > 0)
                        Debug.LogError($"[DROP-CLEANUP] Deleted {deleted} inactive expired items from DB");
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DROP-CLEANUP]  DB cleanup failed: {ex.Message}");
                return false;
            }
        }













        private void LoadZones()
        {
            if (!AuthoredGameplayCatalog.IsLoaded)
                throw new InvalidOperationException("Authored gameplay catalog must be loaded before zones");
            _zones.Clear();
            _zoneOrder.Clear();
            foreach (AuthoredZoneData authored in AuthoredGameplayCatalog.Zones)
            {
                if (authored.Id == 0)
                    throw new InvalidDataException($"Authored zone has zero id: '{authored.Name}'");
                if (_zones.ContainsKey(authored.Id))
                    throw new InvalidDataException($"Duplicate authored zone id 0x{authored.Id:X8}: '{authored.Name}'");
                var zone = new Zone
                {
                    id = authored.Id,
                    name = authored.Name,
                    gcType = authored.GcType,
                    SpawnFixedX = authored.SpawnFixedX,
                    SpawnFixedY = authored.SpawnFixedY,
                    SpawnFixedZ = authored.SpawnFixedZ,
                    SpawnHeadingFixed = authored.SpawnHeadingFixed,
                    respawnZone = authored.RespawnZone,
                    respawnSpawnPoint = authored.RespawnSpawnPoint,
                    exploredBitCount = authored.ExploredBitCount
                };
                _zones.Add(zone.id, zone);
                _zoneOrder.Add(zone);
            }
            Debug.Log($"Loaded {_zones.Count} zones from authored gameplay catalog");
        }

        private ushort ResolveExploredBitCount(RRConnection conn, string source)
        {
            if (conn != null
                && TryGetProceduralDungeonSnapshot(conn, out DungeonMazeSpawner.ProceduralDungeonSnapshot snapshot)
                && DungeonMazeSpawner.TryResolveExploredBitCount(snapshot, out ushort proceduralBitCount))
            {
                Debug.LogError($"[MINIMAP] source={source ?? "unknown"} zoneId=0x{conn.CurrentZoneId:X8} zone='{conn.CurrentZoneName ?? ""}' exploredBitCount={proceduralBitCount} source=procedural-static-object-bounds sourceFunction=MiniMap::init@0x004BD980 MiniMapExplored::ReadExploredBits@0x004C1600");
                return proceduralBitCount;
            }

            if (conn != null && _zones.TryGetValue(conn.CurrentZoneId, out Zone zone) && zone.exploredBitCount > 0)
                return (ushort)zone.exploredBitCount;

            if (conn != null
                && DungeonMazeSpawner.TryResolveExploredBitCount(conn.CurrentZoneName, out ushort authoredGeneratedBitCount))
            {
                Debug.LogError($"[MINIMAP] source={source ?? "unknown"} zoneId=0x{conn.CurrentZoneId:X8} zone='{conn.CurrentZoneName ?? ""}' exploredBitCount={authoredGeneratedBitCount} source=authored-generated-world-bounds sourceFunction=MiniMapExplored::init@0x004BD980 MiniMapExplored::ReadExploredBits@0x004BDD50");
                return authoredGeneratedBitCount;
            }

            Debug.LogError($"[MINIMAP] source={source ?? "unknown"} zoneId=0x{(conn != null ? conn.CurrentZoneId : 0):X8} zone='{conn?.CurrentZoneName ?? ""}' exploredBitCount=0 reason=no-authored-zone-count sourceFunction=ZoneMessageReady");
            return 0;
        }

        private void StartServer()
        {
            try
            {
                _listener = new TcpListener(IPAddress.Any, ServerSettings.Get("gamePort", 2603));
                _listener.Start();
                _isRunning = true;

                string localIP = GetLocalIPAddress();
                int gamePort = ServerSettings.Get("gamePort", 2603);
                StartupLog.Service("Game", $"0.0.0.0:{gamePort} | local={localIP}:{gamePort}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to start game server: {ex.Message}");
                _isRunning = false;
                QueueConnection.OnQueueStreamRegistered -= OnQueueStreamReady;
                try
                {
                    _listener?.Stop();
                }
                catch (Exception stopException)
                {
                    Debug.LogError($"Failed to close game listener after startup failure: {stopException.Message}");
                }
                _listener = null;
                throw new InvalidOperationException("Game listener startup failed", ex);
            }
        }

        private void CompleteServerStartup()
        {
            ServerRuntime.RunRoutine(this, AcceptClientsCoroutine());
            ServerRuntime.RunRoutine(this, PollPendingItemGrants());
            QueueConnection.OnQueueStreamRegistered += OnQueueStreamReady;
            StartupLog.Complete(
                ServerSettings.Get("worldId", 1),
                ServerSettings.Get("maxPlayers", 100));
        }
        private Dictionary<string, string> _checkpointZoneMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static void ApplyDungeonPortalAuthority(DungeonMazeSpawner.ProceduralDungeonSnapshot snapshot, DungeonPortalRole role, ZonePortal portal)
        {
            if (snapshot == null || portal == null)
                return;

            string gcType = role == DungeonPortalRole.Entry ? snapshot.EntryPortalGcType : snapshot.ExitPortalGcType;
            string targetZone = role == DungeonPortalRole.Entry ? snapshot.EntryLinkToZone : snapshot.ExitLinkToZone;
            string spawnPoint = role == DungeonPortalRole.Entry ? snapshot.EntryLinkToSpawn : snapshot.ExitLinkToSpawn;

            if (!string.IsNullOrEmpty(gcType))
                portal.GCType = gcType;
            if (!string.IsNullOrEmpty(targetZone))
                portal.TargetZone = targetZone;
            if (!string.IsNullOrEmpty(spawnPoint))
                portal.SpawnPoint = spawnPoint;

        }

        private List<ZonePortal> ResolveZonePortalsForConnection(RRConnection conn, List<ZonePortal> portals)
        {
            if (TryGetAuthoredWorldSnapshot(conn, out var authoredSnapshot))
                return authoredSnapshot.Portals.Select(data => new ZonePortal
                {
                    Id = 0, GCType = data.gcType, Name = data.name, PosFixedX = data.PosFixedX, PosFixedY = data.PosFixedY,
                    PosFixedZ = data.PosFixedZ, HeadingFixed = data.HeadingFixed, TargetZone = data.targetZone, SpawnPoint = data.spawnPoint,
                    Width = data.width, Height = data.height, Color = data.color
                }).ToList();
            return portals;
        }

        private void SendZonePortals(RRConnection conn, uint zoneId)
        {
            _zonePortals.TryGetValue(zoneId, out var authoredPortals);
            var portals = ResolveZonePortalsForConnection(conn, authoredPortals);
            if (portals == null || portals.Count == 0)
            {
                Debug.LogError($"[SEND-ZONE-PORTALS] zone={zoneId} state=empty");
                return;
            }
            if (!TryAllocatePortalEntityIds(portals.Count, out ushort[] portalEntityIds))
            {
                Debug.LogError($"[SEND-ZONE-PORTALS] zone={zoneId} state=blocked reason=entity-id-exhausted requested={portals.Count} range=0x{PORTAL_ID_MIN:X4}-0x{PORTAL_ID_MAX:X4}");
                return;
            }

            if (VerbosePacketLogging) Debug.LogError($"[SEND-ZONE-PORTALS] count={portals.Count}");

            byte[] packetData;
            try
            {
                var writer = new LEWriter();
                writer.WriteByte(0x07);

                for (int portalIndex = 0; portalIndex < portals.Count; portalIndex++)
                {
                    ZonePortal portal = portals[portalIndex];
                    ushort portalId = portalEntityIds[portalIndex];
                    if (VerbosePacketLogging) Debug.LogError($"[PORTAL] name='{portal.Name}' id=0x{portalId:X4} posFixed=({portal.PosFixedX},{portal.PosFixedY},{portal.PosFixedZ}) target='{portal.TargetZone}'");
                    writer.WriteByte(0x01);
                    writer.WriteUInt16(portalId);
                    WriteGCType(writer, portal.GCType, preserveCase: true);

                    writer.WriteByte(0x02);
                    writer.WriteUInt16(portalId);

                    writer.WriteUInt32(0x06);


                    writer.WriteInt32(portal.PosFixedX);
                    writer.WriteInt32(portal.PosFixedY);
                    writer.WriteInt32(portal.PosFixedZ);
                    writer.WriteInt32(portal.HeadingFixed);
                    writer.WriteByte(0x00);

                    if (VerbosePacketLogging) Debug.LogError($"[PORTAL-WRITE] entityId=0x{portalId:X4} label='{portal.TargetZone}' spawnPoint='{portal.SpawnPoint}' width={portal.Width} height={portal.Height} color=0x{portal.Color:X8}");
                    writer.WriteCString(portal.SpawnPoint ?? "");
                    writer.WriteCString(portal.TargetZone ?? "");

                    writer.WriteUInt16((ushort)portal.Width);
                    writer.WriteUInt16((ushort)portal.Height);
                    writer.WriteUInt32(portal.Color);
                    if (VerbosePacketLogging) Debug.LogError($"[PORTAL-HEX] {BitConverter.ToString(writer.ToArray())}");
                    if (VerbosePacketLogging) Debug.LogError($"[PORTAL-WRITE] entityId=0x{portalId:X4} label='{portal.TargetZone}' spawnPoint='{portal.SpawnPoint}' width={portal.Width} height={portal.Height} color=0x{portal.Color:X8}");
                }

                writer.WriteByte(0x06);
                packetData = writer.ToArray();
            }
            catch
            {
                for (int portalIndex = 0; portalIndex < portalEntityIds.Length; portalIndex++)
                    ReleasePortalEntityId(portalEntityIds[portalIndex]);
                throw;
            }

            if (!_portalEntitiesByConn.TryGetValue(conn.ConnId, out var connPortalIds))
            {
                connPortalIds = new List<ushort>();
                _portalEntitiesByConn[conn.ConnId] = connPortalIds;
            }
            for (int portalIndex = 0; portalIndex < portals.Count; portalIndex++)
            {
                ZonePortal portal = portals[portalIndex];
                ushort portalId = portalEntityIds[portalIndex];
                _portalEntities[portalId] = portal;
                connPortalIds.Add(portalId);
                if (VerbosePacketLogging) Debug.LogError($"[PORTAL] registered id=0x{portalId:X4} target='{portal.TargetZone}'");
            }
            if (VerbosePacketLogging) Debug.LogError($"[SEND-ZONE-PORTALS] fullHexBytes={packetData.Length} hex={BitConverter.ToString(packetData).Replace("-", " ")}");
            if (VerbosePacketLogging) Debug.LogError("[SEND-ZONE-PORTALS] hexScope=portal-packet");
            if (VerbosePacketLogging) Debug.LogError($"[SEND-ZONE-PORTALS] length={packetData.Length}");
            if (VerbosePacketLogging) Debug.LogError($"[SEND-ZONE-PORTALS] hex={BitConverter.ToString(packetData)}");
            SendCompressedA(conn, 0x01, 0x0f, packetData);

            if (VerbosePacketLogging) Debug.LogError($"[SEND-ZONE-PORTALS] sent={portals.Count} bytes={packetData.Length}");
        }








        private void ClearConnZoneEntities(RRConnection conn)
        {
            if (conn == null) return;
            if (_portalEntitiesByConn.TryGetValue(conn.ConnId, out var portalIds))
            {
                foreach (var id in portalIds)
                {
                    _portalEntities.Remove(id);
                    ReleasePortalEntityId(id);
                }
                portalIds.Clear();
            }
            if (_checkpointEntitiesByConn.TryGetValue(conn.ConnId, out var checkpointIds))
            {
                foreach (var id in checkpointIds)
                    _checkpointEntities.Remove(id);
                checkpointIds.Clear();
            }
            if (_worldEntityIdsByConn.TryGetValue(conn.ConnId, out Dictionary<int, ushort> worldEntityIds))
            {
                if (_worldEntityRuntimeOrderByConn.TryGetValue(conn.ConnId, out List<ushort> worldEntityRuntimeOrder))
                {
                    foreach (ushort id in worldEntityRuntimeOrder)
                        WorldEntitySpawner.Instance.RemoveEntity(id);
                    worldEntityRuntimeOrder.Clear();
                }
                worldEntityIds.Clear();
            }
        }

        private void SendZoneCheckpoints(RRConnection conn, uint zoneId)
        {
            _zoneCheckpoints.TryGetValue(zoneId, out var checkpoints);
            if (TryGetAuthoredWorldSnapshot(conn, out var snapshot))
                checkpoints = snapshot.Checkpoints.Select(data => new ZoneCheckpoint
                {
                    GCType = data.entityGcType, CheckpointGCType = data.gcType, Name = data.name,
                    PosFixedX = data.PosFixedX, PosFixedY = data.PosFixedY, PosFixedZ = data.PosFixedZ, HeadingFixed = data.HeadingFixed
                }).ToList();
            if (checkpoints == null || checkpoints.Count == 0)
            {
                Debug.LogError($"[SEND-ZONE-CHECKPOINTS] zone={zoneId} state=empty");
                return;
            }

            if (VerbosePacketLogging) Debug.LogError($"[SEND-ZONE-CHECKPOINTS] count={checkpoints.Count}");

            var writer = new LEWriter();
            writer.WriteByte(0x07);

            foreach (var checkpoint in checkpoints)
            {
                ushort checkpointId = (ushort)AllocateGeneralEntityId();
                _checkpointEntities[checkpointId] = checkpoint;
                if (!_checkpointEntitiesByConn.TryGetValue(conn.ConnId, out var connCheckpointIds))
                {
                    connCheckpointIds = new List<ushort>();
                    _checkpointEntitiesByConn[conn.ConnId] = connCheckpointIds;
                }
                connCheckpointIds.Add(checkpointId);
                if (VerbosePacketLogging) Debug.LogError($"[CHECKPOINT] gcType='{checkpoint.GCType}' id=0x{checkpointId:X4} posFixed=({checkpoint.PosFixedX},{checkpoint.PosFixedY},{checkpoint.PosFixedZ})");

                writer.WriteByte(0x01);
                writer.WriteUInt16(checkpointId);
                WriteGCType(writer, checkpoint.GCType, preserveCase: true);

                writer.WriteByte(0x02);
                writer.WriteUInt16(checkpointId);

                writer.WriteUInt32(0x06);

                writer.WriteInt32(checkpoint.PosFixedX);
                writer.WriteInt32(checkpoint.PosFixedY);
                writer.WriteInt32(checkpoint.PosFixedZ);
                writer.WriteInt32(checkpoint.HeadingFixed);
                writer.WriteByte(0x00);
            }

            writer.WriteByte(0x06);

            byte[] packetData = writer.ToArray();
            SendCompressedA(conn, 0x01, 0x0f, packetData);

            if (VerbosePacketLogging) Debug.LogError($"[SEND-ZONE-CHECKPOINTS] sent={checkpoints.Count} bytes={packetData.Length}");
        }

        private void SendZoneChests(RRConnection conn, string zoneName)
        {
            if (string.IsNullOrEmpty(zoneName)) return;
            var chests = new List<ChestSpawnData>();
            if (chests.Count == 0) return;

            Debug.LogError($"[CHESTS] Spawning {chests.Count} treasure chests in {zoneName}");

            var writer = new LEWriter();
            writer.WriteByte(0x07);

            foreach (var chest in chests)
            {
                ushort chestId = (ushort)AllocateGeneralEntityId();
                ushort behaviorId = (ushort)AllocateGeneralEntityId();

                var chestPathMap = DungeonRunners.Core.PathMapCatalog.Instance.GetPathMap(zoneName);
                int chestFixedX = chest.PosFixedX;
                int chestFixedY = chest.PosFixedY;
                int chestFixedZ = chest.PosFixedZ;
                if (chestPathMap != null && chestPathMap.IsWalkableFixed(chestFixedX, chestFixedY))
                {
                    chest.PosFixedZ = ResolveZoneGroundHeightFixed(zoneName, null, chestPathMap, chestFixedX, chestFixedY, chestFixedZ);
                }

                _chestEntities[chestId] = chest;

                writer.WriteByte(0x01);
                writer.WriteUInt16(chestId);
                WriteGCType(writer, chest.GCType, preserveCase: true);

                writer.WriteByte(0x02);
                writer.WriteUInt16(chestId);

                writer.WriteUInt32(0x06);

                writer.WriteInt32(chest.PosFixedX);
                writer.WriteInt32(chest.PosFixedY);
                writer.WriteInt32(chest.PosFixedZ);
                writer.WriteInt32(chest.HeadingFixed);
                writer.WriteByte(0x00);


                writer.WriteByte(0x00);
                writer.WriteByte(0x00);
                writer.WriteUInt16(0);
                writer.WriteUInt16(0);

                WorldEntitySpawner.WriteNonCombatInteractiveReadInit(writer, chest.GCType);

                writer.WriteByte(0x32);
                writer.WriteUInt16(chestId);
                writer.WriteUInt16(behaviorId);
                WriteGCType(writer, "base.noncombatinteractive.behavior", preserveCase: true);
                writer.WriteByte(0x01);

                writer.WriteByte(0xFF);
                writer.WriteByte(0x00);
                writer.WriteByte(0x00);
                writer.WriteByte(0x01);

                writer.WriteByte(0x08);
                writer.WriteInt32(chest.HeadingFixed);
                writer.WriteInt32(chest.HeadingFixed);
                writer.WriteByte(0x00);

                writer.WriteByte(0xFF);
                writer.WriteByte(0x00);
                writer.WriteByte(0x00);

                Debug.LogError($"[CHEST] {chest.Label} ({chest.GCType}) id=0x{chestId:X4} beh=0x{behaviorId:X4} posFixed=({chest.PosFixedX},{chest.PosFixedY},{chest.PosFixedZ})");
            }

            writer.WriteByte(0x06);

            byte[] chestPacket = writer.ToArray();
            SendCompressedA(conn, 0x01, 0x0f, chestPacket);
            Debug.LogError($"[CHESTS]  Sent {chests.Count} chests ({chestPacket.Length} bytes)");
        }


        private static int ResolveZoneGroundHeightFixed(string zoneName, string instanceKey, DungeonRunners.Core.PathMap pathMap, int worldFixedX, int worldFixedY, int fallbackFixedZ)
        {
            if (pathMap != null && pathMap.TryGetHeightAtFixed(worldFixedX, worldFixedY, out int pathGroundFixedZ))
                return pathGroundFixedZ;

            if (DungeonRunners.Core.WorldCollision.Instance.TryGetTerrainHeightFixed(zoneName, instanceKey, worldFixedX, worldFixedY, fallbackFixedZ, out int worldGroundFixedZ, out _))
                return worldGroundFixedZ;

            return fallbackFixedZ;
        }


        private static WorldEntityData CloneWorldEntityData(WorldEntityData source)
        {
            if (source == null) return null;
            return new WorldEntityData
            {
                Id = source.Id,
                Zone = source.Zone,
                Name = source.Name,
                GCType = source.GCType,
                EntityType = source.EntityType,
                PosFixedX = source.PosFixedX,
                PosFixedY = source.PosFixedY,
                PosFixedZ = source.PosFixedZ,
                HeadingFixed = source.HeadingFixed,
                Flags = source.Flags,
                ItemGenerator = source.ItemGenerator,
                ItemCount = source.ItemCount,
                ItemGenerator2 = source.ItemGenerator2,
                ItemCount2 = source.ItemCount2,
                ItemGenerator3 = source.ItemGenerator3,
                ItemCount3 = source.ItemCount3,
                ItemGenerator4 = source.ItemGenerator4,
                ItemCount4 = source.ItemCount4,
                ItemGenerator5 = source.ItemGenerator5,
                ItemCount5 = source.ItemCount5,
                TargetZone = source.TargetZone,
                TargetSpawn = source.TargetSpawn,
                Label = source.Label,
                AllowMultiple = source.AllowMultiple
            };
        }



        private readonly Dictionary<string, List<ushort>> _pvpGateIdsByLogin =
            new Dictionary<string, List<ushort>>(StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<string> _pvpControllerReady =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, ulong> _pvpGateDropDue =
            new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);

        private readonly List<string> _pvpGateDropOrder = new List<string>();

        private readonly Dictionary<string, ushort> _pvpControllerIdByLogin =
            new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, List<(ulong dueTick, string msg)>> _pvpCombatReminders =
            new Dictionary<string, List<(ulong, string)>>(StringComparer.OrdinalIgnoreCase);

        private readonly List<string> _pvpCombatReminderOrder = new List<string>();

        private static void SetOrderedValue<T>(Dictionary<string, T> values, List<string> order, string key, T value)
        {
            if (!values.ContainsKey(key))
                order.Add(key);
            values[key] = value;
        }

        private static void RemoveOrderedValue<T>(Dictionary<string, T> values, List<string> order, string key)
        {
            if (!values.Remove(key))
                return;
            int index = order.FindIndex(candidate => candidate.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
                order.RemoveAt(index);
        }

        private void SendZoneWorldEntities(RRConnection conn)
        {
            if (WorldEntitySpawner.Instance == null) return;
            string zoneName = conn.CurrentZoneName;
            if (string.IsNullOrEmpty(zoneName)) return;

            if (conn.LoginName != null) _pvpGateIdsByLogin.Remove(conn.LoginName);

            var entities = TryGetAuthoredWorldSnapshot(conn, out var entitySnapshot)
                ? entitySnapshot.WorldEntities.Select(data => new SpawnedWorldEntity { Data = data }).ToList()
                : WorldEntitySpawner.Instance.GetEntitiesForZone(zoneName);
            if (entities.Count == 0) return;

            Debug.LogError($"[WORLD-ENTITIES] Spawning {entities.Count} world entities in {zoneName}");

            var writer = new LEWriter();
            writer.WriteByte(0x07);

            foreach (var ent in entities)
            {
                var data = ent.Data;
                if (IsWorldEntityActivationConsumed(conn, data))
                    continue;
                ushort entId = (ushort)AllocateGeneralEntityId();
                ushort behaviorId = (ushort)AllocateGeneralEntityId();
                ent.EntityId = entId;

                WorldEntitySpawner.WriteEntitySpawn(writer, entId, behaviorId, data);
                WorldEntitySpawner.Instance.TrackSpawnedEntity(entId, data);
                if (!_worldEntityIdsByConn.TryGetValue(conn.ConnId, out Dictionary<int, ushort> connWorldEntityIds))
                {
                    connWorldEntityIds = new Dictionary<int, ushort>();
                    _worldEntityIdsByConn.Add(conn.ConnId, connWorldEntityIds);
                }
                connWorldEntityIds[data.Id] = entId;
                if (!_worldEntityRuntimeOrderByConn.TryGetValue(conn.ConnId, out List<ushort> connWorldEntityRuntimeOrder))
                {
                    connWorldEntityRuntimeOrder = new List<ushort>();
                    _worldEntityRuntimeOrderByConn.Add(conn.ConnId, connWorldEntityRuntimeOrder);
                }
                connWorldEntityRuntimeOrder.Add(entId);

                if (data.IsGate && conn.LoginName != null)
                {
                    if (!_pvpGateIdsByLogin.TryGetValue(conn.LoginName, out var gids))
                        _pvpGateIdsByLogin[conn.LoginName] = gids = new List<ushort>();
                    gids.Add(entId);
                }



                Debug.LogError($"[WORLD-ENTITY] {data.EntityType}: {data.Label} ({data.GCType}) id=0x{entId:X4} posFixed=({data.PosFixedX},{data.PosFixedY},{data.PosFixedZ})");
            }

            writer.WriteByte(0x06);

            byte[] packet = writer.ToArray();
            SendCompressedA(conn, 0x01, 0x0f, packet);
            Debug.LogError($"[WORLD-ENTITIES]  Sent {entities.Count} entities ({packet.Length} bytes)");
        }


        private void HandleTeleporterActivation(RRConnection conn, ushort componentId,
            ushort targetEntityId, byte responseId, byte sessionId, WorldEntityData teleporter)
        {
            Debug.LogError($"[TELEPORTER] ");
            Debug.LogError($"[TELEPORTER] {teleporter.Label} -> {teleporter.TargetZone} ({teleporter.TargetSpawn})");
            conn.SessionID = sessionId;

            var teleporterActivationMessage = new LEWriter();
            teleporterActivationMessage.WriteByte(0x35);
            teleporterActivationMessage.WriteUInt16(componentId);
            teleporterActivationMessage.WriteByte(0x01);
            teleporterActivationMessage.WriteByte(responseId);
            teleporterActivationMessage.WriteByte(0x06);
            teleporterActivationMessage.WriteByte(sessionId);
            teleporterActivationMessage.WriteUInt16(targetEntityId);
            WritePlayerEntitySynch(conn, teleporterActivationMessage);
            conn.MessageQueue.Enqueue(teleporterActivationMessage.ToArray());
            HandleQuestWorldEntityActivation(conn, teleporter);

            if (!string.IsNullOrEmpty(teleporter.TargetZone))
            {
                int spawnFixedX = 0, spawnFixedY = 0, spawnFixedZ = 0;
                if (!string.IsNullOrEmpty(teleporter.TargetSpawn))
                {
                    var waypoints = AuthoredGameplayCatalog.GetWaypointsForZone(teleporter.TargetZone);
                    if (waypoints != null)
                    {
                        var waypoint = waypoints.FirstOrDefault(waypointData =>
                            waypointData.name.Equals(teleporter.TargetSpawn, StringComparison.OrdinalIgnoreCase));
                        if (waypoint != null)
                        {
                            spawnFixedX = waypoint.PosFixedX;
                            spawnFixedY = waypoint.PosFixedY;
                            spawnFixedZ = waypoint.PosFixedZ;
                            Debug.LogError($"[TELEPORTER] Found spawn '{teleporter.TargetSpawn}' fixed=({spawnFixedX},{spawnFixedY},{spawnFixedZ})");
                        }
                    }
                }

                conn.PendingSpawnFixedX = spawnFixedX;
                conn.PendingSpawnFixedY = spawnFixedY;
                conn.PendingSpawnFixedZ = spawnFixedZ;

                ChangeZone(conn, teleporter.TargetZone, teleporter.TargetSpawn ?? "");
                Debug.LogError($"[TELEPORTER] Player {conn.LoginName} teleported to {teleporter.TargetZone}");
            }
        }

        private void HandleEnterPvpZone(RRConnection conn)
        {
            Debug.LogError($"[PVP] {conn.LoginName} marked as in PVP hub area");
        }

        private void HandleRequestPvpMatch(RRConnection conn, byte[] data)
        {
            if (data == null || data.Length < 2)
            {
                Debug.LogError($"[PVP] requestPVPMatch payload too short ({data?.Length ?? 0} bytes)");
                return;
            }

            Gameplay.PVPMatchmaking.Archetype archetype;
            byte tag = data[0];
            if (tag == 0x04 || tag == 0x02 || tag == 0x01)
            {
                uint typeId;
                if (tag == 0x04 && data.Length >= 5) typeId = BitConverter.ToUInt32(data, 1);
                else if (tag == 0x02 && data.Length >= 3) typeId = BitConverter.ToUInt16(data, 1);
                else if (tag == 0x01 && data.Length >= 2) typeId = data[1];
                else { Debug.LogError($"[PVP] truncated TypeID payload (hex: {BitConverter.ToString(data)})"); return; }

                if (!Gameplay.PVPMatchmaking.TryParseArchetypeByTypeId(typeId, out archetype))
                {
                    Debug.LogError($"[PVP] Unknown match TypeID 0x{typeId:X8} (hex: {BitConverter.ToString(data)})");
                    return;
                }
                Debug.LogError($"[PVP] {conn.LoginName} requested archetype: {archetype} (TypeID 0x{typeId:X8})");
            }
            else
            {
                string archetypeGcType = TryReadGcTypeString(data);
                if (string.IsNullOrEmpty(archetypeGcType))
                {
                    Debug.LogError($"[PVP] Could not parse match archetype from payload (hex: {BitConverter.ToString(data)})");
                    return;
                }
                Debug.LogError($"[PVP] {conn.LoginName} requested archetype: '{archetypeGcType}'");
                if (!Gameplay.PVPMatchmaking.TryParseArchetype(archetypeGcType, out archetype))
                {
                    Debug.LogError($"[PVP] Unknown archetype '{archetypeGcType}'");
                    return;
                }
            }

            uint charSqlId = conn.CharSqlId;
            if (HasPendingPvpResultFence(charSqlId))
            {
                DrainPendingPvpMatchResults(characterId: charSqlId);
                if (HasPendingPvpResultFence(charSqlId))
                {
                    Debug.LogError($"[PVP] {conn.LoginName} could not be enqueued while a durable match result is pending");
                    return;
                }
            }
            int rating;
            try
            {
                var dbChar = GetActiveCharacter(conn);
                if (dbChar == null || dbChar.id != charSqlId || dbChar.pvpRating < 0 || dbChar.pvpRating > 3000)
                {
                    Debug.LogError($"[PVP] Could not load an exact valid PvP rating for {conn.LoginName}");
                    return;
                }
                rating = dbChar.pvpRating;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PVP] Could not load PvP rating: {ex.Message}");
                return;
            }
            var pvpGroup = Gameplay.GroupDirectory.Instance.GetGroupForConn(conn.ConnId);
            string teamKey = (pvpGroup != null && pvpGroup.Members.Count(m => m.IsOnline) > 1)
                ? "grp:" + pvpGroup.GroupId
                : "solo:" + conn.LoginName;
            bool ok = Gameplay.PVPMatchmaking.Instance.EnqueuePlayer(
                conn.LoginName, charSqlId, archetype, rating, teamKey, _combatTick);

            if (!ok)
            {
                Debug.LogError($"[PVP] {conn.LoginName} could not be enqueued");
                return;
            }

            uint pvpGroupId = Gameplay.GroupDirectory.Instance.GetGroupForConn(conn.ConnId)?.GroupId ?? 1u;
            SendToClient(conn, PVPPackets.BuildPVPMatchStatus(pvpGroupId, 0, 0, Gameplay.PVPMatchmaking.GetGcPath(archetype)));
            Debug.LogError($"[PVP] {conn.LoginName} now-in-queue status sent ({archetype}, group {pvpGroupId})");

            ProcessMatchmakingTick(forceRun: true);
        }

        private void HandleCancelPvpMatch(RRConnection conn)
        {
            bool removed = Gameplay.PVPMatchmaking.Instance.DequeuePlayer(conn.LoginName);
            Debug.LogError($"[PVP] cancel queue for {conn.LoginName}: {(removed ? "OK" : "was not queued")}");
            if (removed)
                SendToClient(conn, PVPPackets.BuildPVPMatchStatus(Gameplay.GroupDirectory.Instance.GetGroupForConn(conn.ConnId)?.GroupId ?? 1u, 0, 0, null));
        }

        private void HandleLeavePvp(RRConnection conn)
        {
            Gameplay.PVPMatchmaking.Instance.DequeuePlayer(conn.LoginName);
            var match = Gameplay.PVPMatchmaking.Instance.HandleDisconnect(conn.LoginName, _combatTick);
            if (match != null)
            {
                Debug.LogError($"[PVP] {conn.LoginName} forfeited match {match.MatchId}");
                FinalizeMatchResults(match);
            }
            SendToClient(conn, PVPPackets.BuildPVPMatchStatus(Gameplay.GroupDirectory.Instance.GetGroupForConn(conn.ConnId)?.GroupId ?? 1u, 0, 0, null));
            if (_zones.TryGetValue(conn.CurrentZoneId, out var z) && IsPvpZone(z.name))
                ChangeZone(conn, "pvp_start", "respawn");
        }

        public void ProcessMatchmakingTick(bool forceRun = false)
        {
            uint nowTick = _combatTick;
            if (!forceRun && _hasMatchmakingTick && unchecked(nowTick - _lastMatchmakingTick) < SimulationClock.TicksPerSecond)
                return;
            _lastMatchmakingTick = nowTick;
            _hasMatchmakingTick = true;

            if (_pendingDuelActivations.Count > 0)
            {
                for (int activationIndex = _pendingDuelActivations.Count - 1; activationIndex >= 0; activationIndex--)
                {
                    var (dueTick, duel) = _pendingDuelActivations[activationIndex];
                    if (dueTick <= nowTick)
                    {
                        _pendingDuelActivations.RemoveAt(activationIndex);
                        if (_duelRuntime.ActivateCombat(duel.ChallengerLogin, nowTick))
                            SendCombatStart(duel);
                    }
                }
            }

            foreach (var expiredDuel in _duelRuntime.CleanupExpired(nowTick))
            {
                var challenger = FindConnectionByLogin(expiredDuel.ChallengerLogin);
                var target = FindConnectionByLogin(expiredDuel.TargetLogin);
                if (challenger != null)
                    SendToClient(challenger, PVPPackets.BuildDuelStatus(PVPPackets.DuelStatusType.Cancelled, expiredDuel.TargetCharSqlId, 0, 0));
                if (target != null)
                    SendToClient(target, PVPPackets.BuildDuelStatus(PVPPackets.DuelStatusType.Cancelled, expiredDuel.ChallengerCharSqlId, 0, 0));
                Debug.LogError($"[PVP-DUEL] Challenge expired: {expiredDuel.ChallengerLogin} vs {expiredDuel.TargetLogin} tick={nowTick}");
            }

            DrainPendingPvpMatchResults();
            var (newMatches, endedMatches, enteringCombat) = Gameplay.PVPMatchmaking.Instance.Tick(nowTick);
            foreach (var match in newMatches)
                SpawnMatchParticipants(match);
            foreach (var match in enteringCombat)
                BeginPvpCombat(match);
            foreach (var match in endedMatches)
                FinalizeMatchResults(match);

            if (_pvpGateDropDue.Count > 0)
            {
                for (int gateIndex = 0; gateIndex < _pvpGateDropOrder.Count;)
                {
                    string login = _pvpGateDropOrder[gateIndex];
                    if (!_pvpGateDropDue.TryGetValue(login, out ulong dueTick))
                    {
                        _pvpGateDropOrder.RemoveAt(gateIndex);
                        continue;
                    }
                    if (nowTick < dueTick)
                    {
                        gateIndex++;
                        continue;
                    }
                    var gConn = GetConnectionInsertionOrderSnapshot().FirstOrDefault(c => c.IsConnected && c.IsSpawned
                        && c.LoginName?.Equals(login, StringComparison.OrdinalIgnoreCase) == true);
                    if (gConn != null)
                    {
                        DropPvpGatesFor(gConn);
                        SendPVPControllerTransition(gConn, 0x17);
                        ScheduleCombatTimeReminders(gConn);
                    }
                    RemoveOrderedValue(_pvpGateDropDue, _pvpGateDropOrder, login);
                }
            }

            if (_pvpCombatReminders.Count > 0)
            {
                for (int reminderIndex = 0; reminderIndex < _pvpCombatReminderOrder.Count;)
                {
                    string login = _pvpCombatReminderOrder[reminderIndex];
                    if (!_pvpCombatReminders.TryGetValue(login, out var list))
                    {
                        _pvpCombatReminderOrder.RemoveAt(reminderIndex);
                        continue;
                    }
                    while (list.Count > 0 && nowTick >= list[0].dueTick)
                    {
                        var rConn = GetConnectionInsertionOrderSnapshot().FirstOrDefault(c => c.IsConnected && c.IsSpawned
                            && c.LoginName?.Equals(login, StringComparison.OrdinalIgnoreCase) == true
                            && _zones.TryGetValue(c.CurrentZoneId, out var rz) && IsPvpZone(rz.name));
                        if (rConn != null) SendSystemMessage(rConn, list[0].msg);
                        list.RemoveAt(0);
                    }
                    if (list.Count == 0)
                        RemoveOrderedValue(_pvpCombatReminders, _pvpCombatReminderOrder, login);
                    else
                        reminderIndex++;
                }
            }

        }

        private void SpawnMatchParticipants(Gameplay.PVPMatchmaking.Match match)
        {
            if (!_zoneOrder.Any(z => z.name.Equals(match.ZoneName, StringComparison.OrdinalIgnoreCase)))
            {
                Debug.LogError($"[PVP-MATCH] Zone '{match.ZoneName}' not loaded - match {match.MatchId} cannot start");
                Gameplay.PVPMatchmaking.Instance.EndMatch(match.MatchId, "zone not loaded", _combatTick);
                return;
            }

            foreach (var login in match.ParticipantLogins)
            {
                var pConn = GetConnectionInsertionOrderSnapshot().FirstOrDefault(c =>
                    c.LoginName?.Equals(login, StringComparison.OrdinalIgnoreCase) == true);
                if (pConn == null) { Debug.LogError($"[PVP-MATCH] {login} not connected"); continue; }


                string entryPoint = match.FreeForAll
                    ? "ffa_start"
                    : match.IsRed(login) ? "red_team_start" : "blue_team_start";
                Debug.LogError($"[PVP-MATCH] Spawning {login} into {match.ZoneName}#{match.InstanceId} @ {entryPoint}");
                ChangeZone(pConn, match.ZoneName, entryPoint, (uint)match.InstanceId);
            }
            Gameplay.PVPMatchmaking.Instance.MarkMatchStarted(match.MatchId);
        }

        private void BeginPvpCombat(Gameplay.PVPMatchmaking.Match match)
        {
            Debug.LogError($"[PVP-MATCH] BeginPvpCombat {match.ZoneName}#{match.InstanceId} (combat phase begun)");
        }

        private void DropPvpGatesFor(RRConnection conn)
        {
            if (conn?.LoginName == null) return;
            if (!_pvpGateIdsByLogin.TryGetValue(conn.LoginName, out var gateIds) || gateIds.Count == 0)
            {
                Debug.LogError($"[PVP-GATES] no tracked gates for {conn.LoginName} (zone={conn.CurrentZoneName})");
                return;
            }
            foreach (var gid in gateIds)
            {
                var w = new LEWriter();
                w.WriteByte(0x03);
                w.WriteUInt16(gid);
                w.WriteByte(0x0A);
                w.WriteByte(0x00);
                conn.MessageQueue.Enqueue(w.ToArray());
            }
            Debug.LogError($"[PVP-GATES] dropped {gateIds.Count} gates for {conn.LoginName} (per-client, synced to countdown)");
        }

        private void SendPVPMatchControllerSpawn(RRConnection conn)
        {
            if (conn == null) return;
            var match = Gameplay.PVPMatchmaking.Instance.GetMatchForPlayer(conn.LoginName);
            ushort controllerId = (ushort)AllocateGeneralEntityId();
            var w = new LEWriter();
            w.WriteByte(0x07);
            w.WriteByte(0x01);
            w.WriteUInt16(controllerId);
            WriteGCType(w, "PVPMatchController", preserveCase: true);

            w.WriteByte(0x02);
            w.WriteUInt16(controllerId);
            w.WriteUInt32(0);
            w.WriteByte(0x00);
            w.WriteByte(0x00);
            if (match != null && match.Archetype == Gameplay.PVPMatchmaking.Archetype.GroupPracticeMatch)
            {
                w.WriteByte(0x01);
                WritePVPTeamScore(w, "pvp.FFATeam");
            }
            else
            {
                string teamList = (match != null && match.Archetype == Gameplay.PVPMatchmaking.Archetype.GroupDuelMatch)
                    ? "pvp.DuelTeamList" : "pvp.DefaultTeamList";
                w.WriteByte(0x02);
                WritePVPTeamScore(w, teamList + ".RedTeam");
                WritePVPTeamScore(w, teamList + ".BlueTeam");
            }

            w.WriteByte(0x06);
            SendToClient(conn, w.ToArray());
            if (conn.LoginName != null)
            {
                _pvpControllerReady.Add(conn.LoginName);
                _pvpControllerIdByLogin[conn.LoginName] = controllerId;
                int setupSec = match?.SetupTimeSec ?? 15;
                SetOrderedValue(_pvpGateDropDue, _pvpGateDropOrder, conn.LoginName, checked((ulong)_combatTick + (ulong)setupSec * SimulationClock.TicksPerSecond));
            }
            Debug.LogError($"[PVP-MATCH] Sent PVPMatchController entity {controllerId} (+team scores) to {conn.LoginName} (zone={conn.CurrentZoneName})");

            if (match != null && match.ParticipantLogins.All(l => _pvpControllerReady.Contains(l)))
                Gameplay.PVPMatchmaking.Instance.BeginSetupPhase(match.MatchId, _combatTick);
        }

        private void WritePVPTeamScore(LEWriter w, string teamGcPath)
        {
            for (int i = 0; i < 6; i++) w.WriteUInt32(0);
            w.WriteByte(0x00); w.WriteByte(0x00); w.WriteByte(0x00);
            w.WriteByte(0xFF); w.WriteCString(teamGcPath);
        }

        private void SendPVPControllerTransition(RRConnection conn, ushort nextState)
        {
            if (conn?.LoginName == null) return;
            if (!_pvpControllerIdByLogin.TryGetValue(conn.LoginName, out var controllerId)) return;
            var w = new LEWriter();
            w.WriteByte(0x07);
            w.WriteByte(0x02);
            w.WriteUInt16(controllerId);
            w.WriteUInt32(0);
            w.WriteByte(0x00);
            w.WriteByte(0x09);
            w.WriteUInt16(nextState);
            w.WriteByte(0x00);
            w.WriteByte(0x06);
            SendToClient(conn, w.ToArray());
            Debug.LogError($"[PVP-MATCH] Drove controller {controllerId} -> FSM next-state 0x{nextState:X2} for {conn.LoginName} (server-timed)");
        }

        private void ScheduleCombatTimeReminders(RRConnection conn)
        {
            if (conn?.LoginName == null) return;
            var match = Gameplay.PVPMatchmaking.Instance.GetMatchForPlayer(conn.LoginName);
            int combatSec = match?.CombatTimeSec ?? 300;
            ulong nowTick = _combatTick;
            var list = new List<(ulong, string)>();
            for (int elapsed = 60; elapsed < combatSec; elapsed += 60)
            {
                int remMin = (combatSec - elapsed) / 60;
                if (remMin < 1) continue;
                string msg = remMin == 1
                    ? "The battle will end in 1 minute!"
                    : $"The battle will end in {remMin} minutes!";
                list.Add((checked(nowTick + (ulong)elapsed * SimulationClock.TicksPerSecond), msg));
            }
            if (list.Count > 0) SetOrderedValue(_pvpCombatReminders, _pvpCombatReminderOrder, conn.LoginName, list);
        }

        private void FinalizeMatchResults(Gameplay.PVPMatchmaking.Match match)
        {
            if (match == null || string.IsNullOrEmpty(match.ResultId))
                return;
            if (TryDeferPvpMatchForPendingDisconnect(match))
                return;
            foreach (var login in match.ParticipantLogins)
                RemoveOrderedValue(_pvpCombatReminders, _pvpCombatReminderOrder, login);
            DrainPendingPvpMatchResults(resultId: match.ResultId);
        }

        private static string CreatePvpResultId()
        {
            Span<byte> resultBytes = stackalloc byte[16];
            RandomNumberGenerator.Fill(resultBytes);
            return Convert.ToHexString(resultBytes).ToLowerInvariant();
        }

        private bool PersistPvpMatchResultOutbox(Gameplay.PVPMatchmaking.Match match)
        {
            if (match == null)
                return false;
            if (string.IsNullOrEmpty(match.ResultId))
                match.ResultId = CreatePvpResultId();
            if (!Guid.TryParseExact(match.ResultId, "N", out _)
                || string.IsNullOrWhiteSpace(match.MatchId)
                || match.ParticipantLogins == null || match.ParticipantLogins.Count == 0
                || match.ParticipantLogins.Count > byte.MaxValue
                || match.ParticipantLogins.Distinct(StringComparer.OrdinalIgnoreCase).Count() != match.ParticipantLogins.Count)
                return false;
            if (!TryResolveRespawnDestination("pvp_start", "respawn", out ResolvedRespawnDestination returnDestination, out string returnFailure))
            {
                Debug.LogError($"[PVP-MATCH] state=outbox-blocked match={match.MatchId} reason={returnFailure}");
                return false;
            }

            bool isRanked = Gameplay.PVPMatchmaking.IsRanked(match.Archetype);
            string rewardGcClass = string.Empty;
            int rewardWidth = 0;
            int rewardHeight = 0;
            int rewardRarity = -1;
            if (match.HasWinner && isRanked)
            {
                rewardGcClass = "QuestItemPAL.Token";
                var rewardDimensions = Gameplay.MerchantRuntime.GetItemDimensions(rewardGcClass);
                rewardWidth = rewardDimensions.width;
                rewardHeight = rewardDimensions.height;
                rewardRarity = 1;
                if (rewardWidth <= 0 || rewardWidth > 10 || rewardHeight <= 0 || rewardHeight > 8)
                    return false;
            }

            var participants = new List<PvpParticipantResult>(match.ParticipantLogins.Count);
            for (int participantOrder = 0; participantOrder < match.ParticipantLogins.Count; participantOrder++)
            {
                string login = match.ParticipantLogins[participantOrder];
                if (!match.ParticipantCharacterIds.TryGetValue(login, out uint characterId)
                    || characterId == 0 || characterId > int.MaxValue)
                    return false;
                SavedCharacter baseline = ResolvePvpResultBaseline(login, characterId);
                if (baseline == null || baseline.id != characterId)
                    return false;
                bool isWinner = match.IsWinner(login);
                int preMatchRating = match.ParticipantRatings.TryGetValue(login, out var mr) ? mr : 1500;
                int newRating = preMatchRating;
                bool applyRating = match.HasWinner && isRanked;
                if (applyRating)
                {
                    var opponentRatings = match.GetOpponentLogins(login)
                        .Select(p => match.ParticipantRatings.TryGetValue(p, out var or) ? or : 1500)
                        .ToList();
                    int ratingDelta = Gameplay.PVPMatchmaking.EloDelta(preMatchRating, opponentRatings, isWinner ? 1.0 : 0.0);
                    newRating = System.Math.Max(0, System.Math.Min(3000, preMatchRating + ratingDelta));
                }
                int rewardCount = match.HasWinner && isRanked ? (isWinner ? 10 : 5) : 0;
                participants.Add(new PvpParticipantResult
                {
                    ResultId = match.ResultId,
                    MatchId = match.MatchId,
                    Archetype = (int)match.Archetype,
                    HasWinner = match.HasWinner,
                    ParticipantOrder = participantOrder,
                    CharacterId = characterId,
                    LoginName = login,
                    IsWinner = isWinner,
                    IsRanked = isRanked,
                    RatingBefore = preMatchRating,
                    RatingAfter = newRating,
                    PersistedRatingBefore = baseline.pvpRating,
                    ApplyRating = applyRating,
                    WinsBefore = baseline.pvpWins,
                    WinsDelta = isWinner ? 1 : 0,
                    RewardGcClass = rewardCount > 0 ? rewardGcClass : string.Empty,
                    RewardCount = rewardCount,
                    RewardWidth = rewardCount > 0 ? rewardWidth : 0,
                    RewardHeight = rewardCount > 0 ? rewardHeight : 0,
                    RewardRarity = rewardCount > 0 ? rewardRarity : -1,
                    ReturnZone = returnDestination.Zone.name,
                    ReturnZoneId = unchecked((int)returnDestination.Zone.id),
                    ReturnPosFixedX = returnDestination.PosFixedX,
                    ReturnPosFixedY = returnDestination.PosFixedY,
                    ReturnPosFixedZ = returnDestination.PosFixedZ
                });
            }
            return TryPersistPvpResultDurably(new PvpMatchResultWrite
            {
                ResultId = match.ResultId,
                MatchId = match.MatchId,
                Archetype = (int)match.Archetype,
                HasWinner = match.HasWinner,
                Participants = participants
            });
        }

        private bool DrainPendingPvpMatchResults(uint characterId = 0, string resultId = null)
        {
            DrainPendingPvpResultJournalToDatabase();
            if (!PvpResultRepository.TryGetPendingParticipantResults(out List<PvpParticipantResult> pendingResults, characterId, resultId))
                return false;
            bool allCompleted = true;
            for (int resultIndex = 0; resultIndex < pendingResults.Count; resultIndex++)
            {
                PvpParticipantResult result = pendingResults[resultIndex];
                RRConnection participantConnection = GetConnectionInsertionOrderSnapshot().FirstOrDefault(candidate =>
                    candidate != null && candidate.IsConnected
                    && candidate.LoginName?.Equals(result.LoginName, StringComparison.OrdinalIgnoreCase) == true
                    && candidate.CharSqlId == result.CharacterId);
                SavedCharacter activeCharacter = participantConnection != null ? GetActiveCharacter(participantConnection) : null;
                bool exactOnlineCharacter = participantConnection != null
                    && activeCharacter != null && activeCharacter.id == result.CharacterId;
                if (!result.Applied)
                {
                    if (CharacterRepository.HasPendingFullSnapshot(result.CharacterId))
                    {
                        allCompleted = false;
                        continue;
                    }
                    SavedCharacter workingCharacter;
                    if (exactOnlineCharacter)
                    {
                        workingCharacter = activeCharacter.DeepClone();
                    }
                    else
                    {
                        workingCharacter = CharacterRepository.GetCharacter(result.CharacterId);
                    }
                    if (workingCharacter == null
                        || workingCharacter.pvpWins != result.WinsBefore
                        || workingCharacter.pvpRating != result.PersistedRatingBefore)
                    {
                        allCompleted = false;
                        continue;
                    }
                    workingCharacter.pvpWins = checked(result.WinsBefore + result.WinsDelta);
                    if (result.ApplyRating)
                    {
                        workingCharacter.pvpRating = result.RatingAfter;
                        workingCharacter.hasPvpRating = true;
                    }
                    workingCharacter.currentZoneName = result.ReturnZone;
                    workingCharacter.zoneId = result.ReturnZoneId;
                    workingCharacter.positionFixedX = result.ReturnPosFixedX;
                    workingCharacter.positionFixedY = result.ReturnPosFixedY;
                    workingCharacter.positionFixedZ = result.ReturnPosFixedZ;
                    SavedCharacter commitCharacter = workingCharacter;
                    if (exactOnlineCharacter
                        && !TryCaptureCharacterSnapshot(participantConnection, "pvp-match-result", workingCharacter, activeCharacter, out commitCharacter))
                    {
                        allCompleted = false;
                        continue;
                    }
                    if (!CharacterRepository.TryApplyPvpParticipantResult(commitCharacter, result, out long pendingGrantId, out bool alreadyApplied, "pvp-match-result"))
                    {
                        allCompleted = false;
                        continue;
                    }
                    if (exactOnlineCharacter)
                    {
                        SavedCharacter committedCharacter = alreadyApplied
                            ? CharacterRepository.GetCharacter(result.CharacterId)
                            : commitCharacter;
                        if (committedCharacter == null || committedCharacter.id != result.CharacterId)
                        {
                            allCompleted = false;
                            continue;
                        }
                        _activeCharacter[result.LoginName] = committedCharacter;
                    }
                    result.Applied = true;
                    result.PendingGrantId = pendingGrantId;
                    Debug.LogError($"[PVP-MATCH] state=participant-committed resultId={result.ResultId} order={result.ParticipantOrder} characterId={result.CharacterId} pendingGrantId={pendingGrantId}");
                }

                if (result.ReturnCompleted)
                    continue;
                bool returned = !exactOnlineCharacter;
                if (exactOnlineCharacter)
                {
                    bool isStillInPvp = (_zones.TryGetValue(participantConnection.CurrentZoneId, out Zone currentZone)
                        && IsPvpZone(currentZone.name))
                        || IsPvpZone(participantConnection.CurrentZoneName);
                    if (!isStillInPvp)
                    {
                        returned = true;
                    }
                    else
                    {
                        if (!result.HasWinner)
                            SendPVPControllerTransition(participantConnection, 0x16);
                        returned = ReturnPvpParticipantAfterMatch(result.LoginName, result.CharacterId);
                    }
                }
                if (!returned || !PvpResultRepository.TryMarkReturnCompleted(result.ResultId, result.ParticipantOrder))
                    allCompleted = false;
            }
            return allCompleted;
        }

        private bool ReturnPvpParticipantAfterMatch(string login, uint characterId)
        {
            RRConnection conn = GetConnectionInsertionOrderSnapshot().FirstOrDefault(candidate =>
                candidate != null && candidate.IsConnected
                && candidate.CharSqlId == characterId
                && candidate.LoginName?.Equals(login, StringComparison.OrdinalIgnoreCase) == true);
            if (conn == null)
                return true;
            bool isInPvp = (_zones.TryGetValue(conn.CurrentZoneId, out Zone zone) && IsPvpZone(zone.name))
                || IsPvpZone(conn.CurrentZoneName);
            if (!isInPvp)
                return true;
            if (!conn.IsSpawned)
                return false;
            return ChangeZone(conn, "pvp_start", "respawn");
        }

        private static bool IsPvpZone(string zoneName)
        {
            if (string.IsNullOrEmpty(zoneName)) return false;
            return zoneName.StartsWith("PVPGroup", StringComparison.OrdinalIgnoreCase)
                || zoneName.StartsWith("DeathMatch", StringComparison.OrdinalIgnoreCase);
        }

        private static string TryReadGcTypeString(byte[] data)
        {
            if (data == null || data.Length < 2) return null;
            if (data[0] != 0xFF) return null;
            int end = 1;
            while (end < data.Length && data[end] != 0) end++;
            if (end <= 1) return null;
            return System.Text.Encoding.ASCII.GetString(data, 1, end - 1);
        }

        public void SendGroupConnectedToAll(Gameplay.Group group)
        {
            foreach (var member in group.Members)
            {
                var memberConnection = FindConnectionById(member.ConnId);
                var character = member.IsOnline && memberConnection != null ? GetActiveCharacter(memberConnection) : null;
                if (character != null)
                    member.PersonalMonsterDifficulty = character.monsterDifficulty;
                if (member.ConnId == group.LeaderConnId)
                    group.MonsterDifficulty = member.PersonalMonsterDifficulty;
            }
            var leaderConn = FindConnectionById(group.LeaderConnId);

            uint leaderGroupId = 0;
            if (leaderConn != null)
                leaderGroupId = leaderConn.CurrentZoneId;
            else
            {
                foreach (var member in group.Members)
                {
                    if (!member.IsOnline) continue;
                    var memberConnection = FindConnectionById(member.ConnId);
                    if (memberConnection != null) { leaderGroupId = memberConnection.CurrentZoneId; break; }
                }
            }

            var members = new System.Collections.Generic.List<GroupMemberInfo>();
            var leaderMember = group.Members.Find(member => member.ConnId == group.LeaderConnId);
            if (leaderMember != null)
            {
                var leaderConnection = FindConnectionById(leaderMember.ConnId);
                if (leaderConnection != null && leaderMember.IsOnline)
                    members.Add(BuildGroupMemberInfo(leaderConnection));
                else if (leaderMember.CharSqlId != 0)
                    members.Add(BuildGroupMemberInfoFromCache(leaderMember));
            }
            foreach (var member in group.Members)
            {
                if (member.ConnId == group.LeaderConnId) continue;
                var memberConnection = FindConnectionById(member.ConnId);
                if (memberConnection != null && member.IsOnline)
                    members.Add(BuildGroupMemberInfo(memberConnection));
                else if (member.CharSqlId != 0)
                    members.Add(BuildGroupMemberInfoFromCache(member));
            }

            foreach (var member in group.Members)
            {
                if (!member.IsOnline) continue;
                var memberConnection = FindConnectionById(member.ConnId);
                if (memberConnection != null)
                {
                    if (!memberConnection.GroupConnectedSent)
                    {
                        uint selfLeaderKey = GetCharSqlId(memberConnection);
                        byte[] connectedPacket = GroupPackets.BuildProcessConnected(
                            selfLeaderKey, member.PersonalMonsterDifficulty, group.InviteMode);
                        SendToClient(memberConnection, connectedPacket);
                        memberConnection.GroupConnectedSent = true;
                        Debug.LogError($"[GROUP] Sent 0x30 to {memberConnection.LoginName}: GC+0xB0=0x{selfLeaderKey:X8}");
                    }

                    byte isOpenGroup = (byte)(group.IsOpen ? 1 : 0);
                    uint leaderCharSqlId = 0;
                    var leaderKeyConnection = FindConnectionById(group.LeaderConnId);
                    if (leaderKeyConnection != null)
                        leaderCharSqlId = GetCharSqlId(leaderKeyConnection);
                    else
                        leaderCharSqlId = group.Members.FirstOrDefault(m => m.ConnId == group.LeaderConnId)?.CharSqlId ?? 0;
                    byte[] groupPacket = GroupPackets.BuildProcessUserChangedGroup(
                        group.GroupId, leaderCharSqlId, group.MonsterDifficulty, isOpenGroup,
                        0, 0,
                        (byte)members.Count, members.ToArray());
                    SendToClient(memberConnection, groupPacket);
                    Debug.LogError($"[GROUP] Sent 0x35 to {memberConnection.LoginName}: leaderCharSqlId=0x{leaderCharSqlId:X8}, {members.Count} members");
                    SendJoinTalkbackGroup(memberConnection, group);
                }
            }

        }

        private void SendGroupZoneStatesToAll(Gameplay.Group group)
        {
            if (group == null)
                return;
            var zoneStates = new List<(int ConnId, string LoginName, uint CharSqlId, string ZoneName, byte[] Packet)>();
            foreach (var groupMember in group.Members)
            {
                var groupMemberConnection = FindConnectionById(groupMember.ConnId);
                if (groupMemberConnection == null)
                    continue;
                uint memberCharSqlId = GetCharSqlId(groupMemberConnection);
                if (memberCharSqlId == 0)
                    continue;
                string zoneName = groupMemberConnection.CurrentZoneName ?? "";
                zoneStates.Add((groupMember.ConnId, groupMemberConnection.LoginName ?? "", memberCharSqlId, zoneName, GroupPackets.BuildUserChangedZone(memberCharSqlId, zoneName)));
            }
            foreach (var groupMember in group.Members)
            {
                var groupMemberConnection = FindConnectionById(groupMember.ConnId);
                if (groupMemberConnection == null)
                    continue;
                foreach (var zoneState in zoneStates)
                {
                    if (zoneState.ConnId == groupMember.ConnId)
                        continue;
                    SendToClient(groupMemberConnection, zoneState.Packet);
                    Debug.LogError($"[GROUP-ZONE] viewer={groupMemberConnection.LoginName} subject={zoneState.LoginName} char=0x{zoneState.CharSqlId:X8} zone='{zoneState.ZoneName}' opcode=0x4C");
                }
            }
        }

        private void SendGroupZoneStatesToViewer(RRConnection viewer)
        {
            if (viewer == null)
                return;
            var group = GroupDirectory.Instance.GetGroupForConn(viewer.ConnId);
            if (group == null)
                return;
            foreach (var groupMember in group.Members)
            {
                if (!groupMember.IsOnline || groupMember.ConnId == viewer.ConnId)
                    continue;
                var subject = FindConnectionById(groupMember.ConnId);
                if (subject == null)
                    continue;
                uint charSqlId = GetCharSqlId(subject);
                if (charSqlId == 0)
                    continue;
                string zoneName = subject.CurrentZoneName ?? "";
                SendToClient(viewer, GroupPackets.BuildUserChangedZone(charSqlId, zoneName));
                Debug.LogError($"[GROUP-ZONE] viewer={viewer.LoginName} subject={subject.LoginName} char=0x{charSqlId:X8} zone='{zoneName}' opcode=0x4C phase=room-commit");
            }
        }

        private void SendGroupMemberZoneState(RRConnection subject)
        {
            if (subject == null)
                return;
            var group = GroupDirectory.Instance.GetGroupForConn(subject.ConnId);
            if (group == null)
                return;
            uint charSqlId = GetCharSqlId(subject);
            if (charSqlId == 0)
                return;
            string zoneName = subject.CurrentZoneName ?? "";
            byte[] packet = GroupPackets.BuildUserChangedZone(charSqlId, zoneName);
            foreach (var member in group.Members)
            {
                if (!member.IsOnline || member.ConnId == subject.ConnId)
                    continue;
                var viewer = FindConnectionById(member.ConnId);
                if (viewer == null)
                    continue;
                SendToClient(viewer, packet);
                Debug.LogError($"[GROUP-ZONE] viewer={viewer.LoginName} subject={subject.LoginName} char=0x{charSqlId:X8} zone='{zoneName}' opcode=0x4C phase=zone-change");
            }
        }

        private void SendJoinTalkbackGroup(RRConnection conn, Gameplay.Group group)
        {
            SendJoinTalkbackGroup(conn, group?.GroupId ?? 0);
        }

        private void SendJoinTalkbackGroup(RRConnection conn, uint talkbackGroupId)
        {
            if (conn == null || talkbackGroupId == 0) return;
            if (conn.Client?.Client?.LocalEndPoint is not System.Net.IPEndPoint localEndPoint) return;
            byte[] address = localEndPoint.Address.MapToIPv4().GetAddressBytes();
            if (address.Length != 4) return;
            var writer = new LEWriter();
            writer.WriteByte(0x09);
            writer.WriteByte(0x50);
            writer.WriteUInt32(GetCharSqlId(conn));
            writer.WriteUInt32(GetCharSqlId(conn));
            writer.WriteByte(IsPlayerFree(conn.LoginName) ? (byte)0x00 : (byte)0x01);
            writer.WriteUInt32(talkbackGroupId);
            writer.WriteByte(address[0]);
            writer.WriteByte(address[1]);
            writer.WriteByte(address[2]);
            writer.WriteByte(address[3]);
            writer.WriteUInt32(DungeonRunners.Talkback.TalkbackServer.Port);
            SendToClient(conn, writer.ToArray());
            Debug.LogError($"[TALKBACK] JoinTalkbackGroup conn={conn.ConnId} ip={localEndPoint.Address} port={DungeonRunners.Talkback.TalkbackServer.Port} userId=0x{GetCharSqlId(conn):X8} groupId={talkbackGroupId}");
        }

        private void SendGroupHealthToAll(Gameplay.Group group)
        {
            foreach (var member in group.Members)
            {
                if (!member.IsOnline) continue;
                var memberConnection = FindConnectionById(member.ConnId);
                if (memberConnection == null) continue;
                uint charSqlId = GetCharSqlId(memberConnection);
                ResolveGroupMemberHealthMana(memberConnection, out byte hp15, out byte mp15);
                byte[] healthPacket = GroupPackets.BuildMemberHealthMana(charSqlId, hp15, mp15);
                _groupMemberHealthManaState[charSqlId] = PackGroupHealthMana(hp15, mp15);
                foreach (var target in group.Members)
                {
                    if (!target.IsOnline) continue;
                    var targetConnection = FindConnectionById(target.ConnId);
                    if (targetConnection != null) SendCompressedA(targetConnection, 0x01, 0x0F, healthPacket);
                }
            }
        }

        private void ResolveGroupMemberHealthMana(RRConnection conn, out byte hp15, out byte mp15)
        {
            hp15 = 15;
            mp15 = 15;
            var state = GetPlayerState(conn.ConnId.ToString());
            if (state == null) return;
            if (state.MaxHPWire > 0)
                hp15 = (byte)Math.Clamp((int)((long)state.CurrentHPWire * 15 / state.MaxHPWire), 0, 15);
            if (state.MaxManaWire > 0)
                mp15 = (byte)Math.Clamp((int)((long)state.CurrentManaWire * 15 / state.MaxManaWire), 0, 15);
        }

        private static byte PackGroupHealthMana(byte hp15, byte mp15)
        {
            return (byte)(((hp15 & 0xF) << 4) | (mp15 & 0xF));
        }

        private void SendGroupMemberHealthManaIfChanged(RRConnection conn)
        {
            if (conn == null || string.IsNullOrEmpty(conn.LoginName)) return;
            var group = GroupDirectory.Instance.GetGroupForConn(conn.ConnId);
            if (group == null || group.Members.Count <= 1) return;
            uint charSqlId = GetCharSqlId(conn);
            if (charSqlId == 0) return;
            ResolveGroupMemberHealthMana(conn, out byte hp15, out byte mp15);
            byte packed = PackGroupHealthMana(hp15, mp15);
            bool changed = !(_groupMemberHealthManaState.TryGetValue(charSqlId, out byte current) && current == packed);
            if (!changed)
                return;
            _groupMemberHealthManaState[charSqlId] = packed;
            byte[] healthPacket = GroupPackets.BuildMemberHealthMana(charSqlId, hp15, mp15);
            foreach (var member in group.Members)
            {
                if (!member.IsOnline) continue;
                var targetConnection = FindConnectionById(member.ConnId);
                if (targetConnection != null) SendCompressedA(targetConnection, 0x01, 0x0F, healthPacket);
            }
        }

        private void HandleGroupChannel(RRConnection conn, byte messageType, byte[] data)
        {
            Debug.LogError($"[CH9] type=0x{messageType:X2} len={data?.Length ?? 0} data={BitConverter.ToString(data ?? new byte[0])}");

            if (_adminCommands == null)
                _adminCommands = new AdminCommands();
            _adminCommands.SetServerCallbacks(
                (c, zone) => ChatChangeZone(c, zone),
                (c) => GetActiveCharacter(c)?.DeepClone(),
                (c, character, reason) => TrySaveCharacterForConn(c, character, reason),
                HandleAdminLevelUp,
                (login, gcType, modId) => _activeModifiers.AddOrReplace(login, new ActiveModifier
                {
                    GCType = gcType,
                    Id = modId,
                    Level = 0,
                    PowerLevel = 0,
                    Duration = 0,
                    SourceIsSelf = 0,
                    AddedSimulationTick = _combatTick
                }),
                (login, modId) => _activeModifiers.RemoveById(login, modId),
                AdminCompleteQuest,
                (c, w) => { WritePlayerEntitySynch(c, w); }
            );

            PlayerState playerState = GetPlayerState(conn.ConnId.ToString());

            if (IsPlayerAdmin(conn.LoginName) && _adminCommands.TryExecute(conn, messageType, data, playerState, SendSystemMessage, SendToClient))
            {
                var ack = new LEWriter();
                ack.WriteByte(9);
                ack.WriteByte(messageType);
                SendCompressedA(conn, 0x01, 0x0F, ack.ToArray());
                return;
            }

            switch (messageType)
            {
                case 0x00:
                    Debug.Log($"[GROUP] Connect ack from {conn.LoginName}");
                    return;
                case 0x16:
                case 0x12:
                case 0x20:
                case 0x21:
                case 0x22:
                case 0x14:
                case 0x15:
                case 0x17:
                case 0x24:
                case 0x26:
                case 0x27:
                case 0x28:
                case 0x29:
                case 0x2A:
                case 0x2B:
                case 0x2C:
                case 0x2D:
                case 0x2E:
                case 0x2F:
                    HandleGroupClientChannel(conn, messageType, data);
                    return;
            }

            Debug.Log($"Group channel message type: 0x{messageType:X2}");
            var groupAck = new LEWriter();
            groupAck.WriteByte(9);
            groupAck.WriteByte(messageType);
            SendCompressedA(conn, 0x01, 0x0F, groupAck.ToArray());
        }

        private bool ResolveZoneJoinMobState(RRConnection conn, bool zoneRuntimePrepared, Zone preparedSpawnZone, string preparedZoneName, string preparedInstanceKey, out Zone spawnZone, out string zoneName, out string instanceKey, out bool mobsAlreadyExist)
        {
            spawnZone = preparedSpawnZone;
            zoneName = null;
            instanceKey = null;
            mobsAlreadyExist = false;

            if (!((zoneRuntimePrepared && spawnZone != null) || _zones.TryGetValue(conn.CurrentZoneId, out spawnZone)))
                return false;

            zoneName = zoneRuntimePrepared ? preparedZoneName : spawnZone.name;
            instanceKey = zoneRuntimePrepared ? preparedInstanceKey : GetInstanceZoneKey(conn);
            string resolvedInstanceKey = instanceKey;
            Debug.LogError($"[ZONE-JOIN] Zone: '{zoneName}' instance: '{instanceKey}' (inst={conn.InstanceId})");

            mobsAlreadyExist = CombatRuntime.Instance.GetMonstersInZone(resolvedInstanceKey).Any();
            bool continuousPresence = GetConnectionInsertionOrderSnapshot().Any(o =>
                o != null && o != conn && o.IsSpawned &&
                string.Equals(GetInstanceZoneKey(o), resolvedInstanceKey, StringComparison.OrdinalIgnoreCase));
            if (!continuousPresence && !IsPublicZone(zoneName))
            {
                ClearDroppedItemsForInstance(zoneName, conn.InstanceId, "fresh-room-entry");
                ClearWorldEntityActivationStateForInstance(resolvedInstanceKey);
            }
            if (mobsAlreadyExist && !continuousPresence)
            {
                int reentryCleared = CombatRuntime.Instance.ClearInstanceMobs(resolvedInstanceKey);
                ZoneSpawner.Instance.ResetZoneSpawnState(resolvedInstanceKey);
                _monsterSpawnSentByConn.Remove(conn.ConnId);
                _encounterObjectSentByConn.Remove(conn.ConnId);
                Debug.LogError($"[ZONE-JOIN] RE-ENTRY solo (no continuous presence) in '{resolvedInstanceKey}', cleared {reentryCleared} stale monsters; respawning fresh");
                mobsAlreadyExist = false;
            }

            return true;
        }


        private void HandleZoneChannel(RRConnection conn, byte[] body)
        {
            Debug.LogError($" HandleZoneChannel: bodyLen={body?.Length ?? 0}, body={BitConverter.ToString(body ?? new byte[0])}");

            bool joinRequest = body == null || body.Length == 0 || body[0] == 0x06;
            if (joinRequest && conn != null && !conn.TickUpdatesActive && DungeonMazeSpawner.IsProceduralZone(conn.CurrentZoneName))
            {
                string instanceKey = GetInstanceZoneKey(conn);
                uint layoutSeed = ResolveZoneLayoutSeed(conn, conn.CurrentZoneName);
                if (!ZoneSpawner.Instance.TryCompleteProceduralSnapshotPreparation(conn.CurrentZoneName, instanceKey, layoutSeed))
                {
                    if (!_pendingProceduralZoneJoins.ContainsKey(conn.ConnId))
                    {
                        _pendingProceduralZoneJoins[conn.ConnId] = new PendingProceduralZoneJoin
                        {
                            Connection = conn,
                            Body = body == null ? null : body.ToArray(),
                            ZoneId = conn.CurrentZoneId,
                            InstanceId = conn.InstanceId
                        };
                        Debug.LogError($"[DUNGEON-PREP] instance='{instanceKey}' conn={conn.ConnId} state=join-deferred");
                    }
                    return;
                }
                _pendingProceduralZoneJoins.Remove(conn.ConnId);
            }

            if (body == null || body.Length == 0)
            {
                if (conn.TickUpdatesActive)
                {
                    Debug.LogError("[ZONE-JOIN] Player already spawned - ignoring duplicate zone join request");
                    return;
                }

                Debug.Log("CLIENT SENT EMPTY 13/6 PROGRESSION REQUEST - Starting zone progression!");

                Debug.LogError($"[ZONE-JOIN] zoneId={conn.CurrentZoneId} hex=0x{conn.CurrentZoneId:X8} source=HandleCharacterPlay");

                Debug.Log("STEP 1: Sending ZoneChannel + ZoneMessageReady (ID 1) - should trigger State 110");
                LEWriter zoneReadyWriter = new LEWriter();
                zoneReadyWriter.WriteByte(13);
                zoneReadyWriter.WriteByte(1);

                uint zoneReadyPlayerId = GetCharSqlId(conn);
                zoneReadyWriter.WriteUInt32(zoneReadyPlayerId);
                Debug.LogError($"[ZONE-READY] playerUserId=0x{zoneReadyPlayerId:X8} sourceFunction=ZoneClient::processReady@0x5FC250 field=ZoneClient+0xf4");

                ushort exploredBitCount = ResolveExploredBitCount(conn, "ZoneProgressionEmpty");

                zoneReadyWriter.WriteUInt16(exploredBitCount);
                for (int exploredBitIndex = 0; exploredBitIndex < exploredBitCount; exploredBitIndex++)
                {
                    zoneReadyWriter.WriteUInt32(0x00000000);
                }

                byte[] step1Data = zoneReadyWriter.ToArray();
                Debug.LogError($"[STEP1] ZoneMessageReady: {step1Data.Length} bytes");
                Debug.LogError($"[STEP1] Hex: {BitConverter.ToString(step1Data.Take(20).ToArray())}...");

                EnsureGroupConnected(conn);
                SendCompressedA(conn, 0x01, 0x0F, step1Data);
                Debug.Log("STEP 1 COMPLETE: Sent ZoneChannel + ZoneMessageReady (ID 1)");

                bool zoneRuntimePrepared = TryPrepareZoneJoinRoomRuntime(
                    conn,
                    "ZONE-JOIN pre-player",
                    out Zone preparedSpawnZone,
                    out string preparedZoneName,
                    out string preparedInstanceKey,
                    out uint preparedRoomSeed,
                    out uint preparedLayoutSeed);
                bool hasZoneJoinMobState = ResolveZoneJoinMobState(conn, zoneRuntimePrepared, preparedSpawnZone, preparedZoneName, preparedInstanceKey, out _, out string zoneJoinZoneName, out string zoneJoinInstanceKey, out bool zoneJoinMobsAlreadyExist);

                StagePendingRoomClientEpoch(conn);
                Debug.Log("[ZONE-JOIN] step=4 action=send-spawn-data before=connected-message");
                SendPlayerEntitySpawn(conn);
                if (!conn.IsSpawned)
                {
                    CancelPendingRoomClientEpoch(conn);
                    conn.Disconnect();
                    return;
                }
                SendZoneSpawnInvulnerability(conn);
                Debug.Log("[ZONE-JOIN] step=4 action=send-spawn-data state=complete");

                if (hasZoneJoinMobState)
                {
                    string zoneName = zoneJoinZoneName;
                    string instanceKey = zoneJoinInstanceKey;
                    bool mobsAlreadyExist = zoneJoinMobsAlreadyExist;

                    if (mobsAlreadyExist)
                    {
                        Debug.LogError($"[ZONE-JOIN] late joiner='{conn.LoginName}' instance='{instanceKey}' state=live-monster-snapshot-pending");
                    }
                    else
                    {
                        uint rngSeed = zoneRuntimePrepared ? preparedRoomSeed : ResolveRuntimeZoneSeed(conn, zoneName);
                        uint layoutSeed = zoneRuntimePrepared ? preparedLayoutSeed : ResolveZoneLayoutSeed(conn, zoneName);
                        Debug.LogError($"[ZONE-JOIN] EntityManager opcode 0x0C seed: 0x{rngSeed:X8} {FormatDungeonLayoutSeedForLog(zoneName, layoutSeed)} for instance '{instanceKey}'");

                        var spawned = ZoneSpawner.Instance.SpawnZoneMobsForInstance(zoneName, instanceKey, layoutSeed, rngSeed);

                        ApplyDifficultyToMonsters(conn, instanceKey);

                        foreach (var monster in CombatRuntime.Instance.GetMonstersInZone(instanceKey))
                        {
                            monster.AggroTriggered = false;
                            monster.State = MonsterState.Idle;
                            monster.TargetId = 0;
                            monster.RngSeed = rngSeed;

                            Debug.LogError($"[ZONE-JOIN] Materialized zone monster {monster.Name} for pending room epoch");
                        }
                    }
                }



                {
                    var zoneGroup = GroupDirectory.Instance.GetGroupForConn(conn.ConnId);
                    if (zoneGroup != null)
                    {
                        GroupDirectory.Instance.UpdateMemberZone(conn.ConnId, conn.CurrentZoneName ?? "");
                        SendGroupZoneStatesToAll(zoneGroup);
                        SendGroupHealthToAll(zoneGroup);
                        Debug.LogError($"[GROUP] Sent incremental group state for zone join: group={zoneGroup.GroupId}");
                    }
                    else
                    {
                        SendSoloGroupState(conn);
                    }
                }

                ResendAllModifiers(conn);

                return;
            }

            if (body.Length >= 1)
            {
                byte type = body[0];
                Debug.Log($"Zone channel message type: 0x{type:X2}");

                if (type == 0x06)
                {
                    if (conn.TickUpdatesActive)
                    {
                        Debug.LogError($"[ZONE-JOIN]  Got 0x06 but player already spawned (simulation registered) - IGNORING to prevent screen jump. body={BitConverter.ToString(body)}");
                        return;
                    }

                    conn.AllowFlush = false;
                    Debug.LogError($"[ZONE-JOIN]  Client sent ZoneJoin (0x06) - FULL SPAWN SEQUENCE");

                    bool isZoneTransition = (conn.Avatar != null && conn.Player != null);
                    Debug.LogError($"[ZONE-JOIN] isZoneTransition={isZoneTransition} (Avatar={(conn.Avatar != null ? conn.Avatar.Id.ToString() : "null")}, Player={(conn.Player != null ? conn.Player.Id.ToString() : "null")})");


                    Debug.LogError("[ZONE-JOIN] FIRST LOGIN - Sending full spawn sequence");

                    ushort exploredBitCount = ResolveExploredBitCount(conn, "ZoneJoin");

                    var readyWriter = new LEWriter();
                    readyWriter.WriteByte(13);
                    readyWriter.WriteByte(1);
                    readyWriter.WriteUInt32(GetCharSqlId(conn));
                    readyWriter.WriteUInt16(exploredBitCount);
                    for (int exploredBitIndex = 0; exploredBitIndex < exploredBitCount; exploredBitIndex++)
                    {
                        readyWriter.WriteUInt32(0x00000000);
                    }
                    EnsureGroupConnected(conn);
                    SendCompressedA(conn, 0x01, 0x0F, readyWriter.ToArray());

                    bool zoneRuntimePrepared = TryPrepareZoneJoinRoomRuntime(
                        conn,
                        "ZONE-JOIN pre-player",
                        out Zone preparedSpawnZone,
                        out string preparedZoneName,
                        out string preparedInstanceKey,
                        out uint preparedRoomSeed,
                        out uint preparedLayoutSeed);
                    bool hasZoneJoinMobState = ResolveZoneJoinMobState(conn, zoneRuntimePrepared, preparedSpawnZone, preparedZoneName, preparedInstanceKey, out _, out string zoneJoinZoneName, out string zoneJoinInstanceKey, out bool zoneJoinMobsAlreadyExist);

                    StagePendingRoomClientEpoch(conn);
                    SendPlayerEntitySpawn(conn);
                    if (!conn.IsSpawned)
                    {
                        CancelPendingRoomClientEpoch(conn);
                        conn.Disconnect();
                        return;
                    }
                    SendZoneSpawnInvulnerability(conn);

                    if (hasZoneJoinMobState)
                    {
                        string zoneName = zoneJoinZoneName;
                        string instanceKey = zoneJoinInstanceKey;
                        bool mobsAlreadyExist = zoneJoinMobsAlreadyExist;

                        if (mobsAlreadyExist)
                        {
                            Debug.LogError($"[ZONE-JOIN] late joiner='{conn.LoginName}' instance='{instanceKey}' state=live-monster-snapshot-pending");
                        }
                        else
                        {
                            uint rngSeed = zoneRuntimePrepared ? preparedRoomSeed : ResolveRuntimeZoneSeed(conn, zoneName);
                            uint layoutSeed = zoneRuntimePrepared ? preparedLayoutSeed : ResolveZoneLayoutSeed(conn, zoneName);
                            Debug.LogError($"[ZONE-JOIN] EntityManager opcode 0x0C seed: 0x{rngSeed:X8} {FormatDungeonLayoutSeedForLog(zoneName, layoutSeed)} for instance '{instanceKey}'");

                            var spawned = ZoneSpawner.Instance.SpawnZoneMobsForInstance(zoneName, instanceKey, layoutSeed, rngSeed);

                            ApplyDifficultyToMonsters(conn, instanceKey);

                            foreach (var monster in CombatRuntime.Instance.GetMonstersInZone(instanceKey))
                            {
                                monster.AggroTriggered = false;
                                monster.State = MonsterState.Idle;
                                monster.TargetId = 0;
                                monster.RngSeed = rngSeed;

                                Debug.LogError($"[ZONE-JOIN] Materialized zone monster {monster.Name} for pending room epoch");
                            }
                        }
                    }


                    {
                        var zoneGroup = GroupDirectory.Instance.GetGroupForConn(conn.ConnId);
                        if (zoneGroup != null)
                        {
                            GroupDirectory.Instance.UpdateMemberZone(conn.ConnId, conn.CurrentZoneName ?? "");
                            SendGroupZoneStatesToAll(zoneGroup);
                            Debug.LogError($"[GROUP] Sent incremental group state for zone-transition: group={zoneGroup.GroupId}");
                        }
                        else
                        {
                            SendSoloGroupState(conn);
                        }
                    }

                    ResendAllModifiers(conn);

                    return;
                }
                else if (type == 0x00)
                {
                    Debug.Log("ZoneJoin request (0x00) - acknowledging zone join");
                    LEWriter joinAck = new LEWriter();
                    joinAck.WriteByte(13);
                    joinAck.WriteByte(0x00);
                    SendCompressedA(conn, 0x01, 0x0F, joinAck.ToArray());
                }
                else if (type == 0x01)
                {
                    Debug.Log("ZoneEnter confirmation (0x01) - client successfully entered zone");
                }
                else if (type == 0x08)
                {
                    Debug.Log("IGNORING 13/8 message - do not acknowledge to prevent crashes");
                }
                else
                {
                    Debug.LogWarning($"Unknown zone channel message type: 0x{type:X2}");
                }
            }
        }

        private void SendCharacterList(RRConnection conn)
        {
            Debug.LogError($"[CHARLIST] *** ENTRY *** Sending character list for {conn.LoginName}");

            try
            {
                var savedCharacters = CharacterRepository.GetCharactersForAccount(conn.LoginName);
                if (savedCharacters == null)
                    throw new InvalidDataException($"Character list load failed for account '{conn.LoginName}'");
                var characters = new List<GCObject>();
                foreach (var savedCharacter in savedCharacters)
                {
                    characters.Add(new GCObject
                    {
                        Id = savedCharacter.id,
                        DFCClass = "Player",
                        GCClass = "Player",
                        Name = savedCharacter.name
                    });
                }
                _persistentCharacters[conn.LoginName] = characters;

                Debug.LogError($"[CHARLIST] Found {characters.Count} characters");

                uint savedNextEntityId = PeekGeneralEntityIdCursor();
                SetPreferredGeneralEntityId(10000);
                var sortedCharacters = characters.OrderBy(c => c.Id).ToList();
                if (sortedCharacters.Count > byte.MaxValue)
                    throw new InvalidOperationException($"Character list count exceeds wire width: {sortedCharacters.Count}");
                var writer = new LEWriter();
                writer.WriteByte(4);
                writer.WriteByte(3);
                writer.WriteByte((byte)sortedCharacters.Count);

                int slotIndex = 0;
                foreach (var character in sortedCharacters)
                {
                    writer.WriteUInt32(character.Id);

                    SavedCharacter savedChar = savedCharacters.FirstOrDefault(saved => saved.id == character.Id);
                    if (savedChar == null)
                        throw new InvalidOperationException($"Character list missing persisted character id={character.Id}");

                    GCObject playerObject = GCObjectFactory.NewPlayer(character.Name, (uint)character.Id, GroupDirectory.Instance.GetGroupForConn(conn.ConnId)?.GroupId ?? 0, savedChar);
                    playerObject.Id = character.Id;

                    var avatar = GCObjectFactory.LoadCharacterSelectionAvatar(savedChar);
                    avatar.Id = AllocateGeneralEntityId();

                    foreach (var child in avatar.Children)
                    {
                        child.Id = AllocateGeneralEntityId();
                        if (child.Children != null)
                        {
                            foreach (var grandchild in child.Children)
                            {
                                grandchild.Id = AllocateGeneralEntityId();
                            }
                        }
                    }

                    Debug.LogError($"[CHARLIST-IDS] Character: {character.Name} (slot {slotIndex})");
                    var equipmentComponent = avatar.Children.FirstOrDefault(c => c.DFCClass == "Equipment");
                    var manipulatorsComponent = avatar.Children.FirstOrDefault(c => c.DFCClass == "Manipulators");
                    if (equipmentComponent != null)
                    {
                        foreach (var equipmentChild in equipmentComponent.Children)
                        {
                            Debug.LogError($"[CHARLIST-IDS]   Equipment child: {equipmentChild.GCClass} ID={equipmentChild.Id}");
                        }
                    }
                    if (manipulatorsComponent != null)
                    {
                        foreach (var manipulatorChild in manipulatorsComponent.Children)
                        {
                            Debug.LogError($"[CHARLIST-IDS]   Manipulators child: {manipulatorChild.GCClass} ID={manipulatorChild.Id}");
                        }
                    }

                    playerObject.AddChild(avatar);

                    Debug.LogError($"[CHARLIST-DETAIL] slot={slotIndex} name={character.Name}");
                    Debug.LogError($"[CHARLIST-DETAIL] avatarChildren={avatar.Children.Count}");
                    foreach (var child in avatar.Children)
                    {
                        int grandchildCount = child.Children?.Count ?? 0;
                        Debug.LogError($"[CHARLIST-DETAIL] child={child.DFCClass} id={child.Id} children={grandchildCount}");
                    }

                    var procModifierObject = GCObjectFactory.NewProcModifier();
                    procModifierObject.Id = AllocateGeneralEntityId();
                    playerObject.AddChild(procModifierObject);

                    playerObject.WriteFullGCObject(writer);
                    slotIndex++;
                }

                byte[] currentData = writer.ToArray();
                Debug.LogError($"[CHARLIST] Sending TYPE 3 count={sortedCharacters.Count} bytes={currentData.Length}");
                SendCompressedA(conn, 0x01, 0x0f, currentData);
                _charListSent[conn.ConnId] = true;
                SetPreferredGeneralEntityId(Math.Max(savedNextEntityId, PeekGeneralEntityIdCursor()));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CHARLIST] state=failed message='{ex.Message}' stack='{ex.StackTrace}'");
                SendCharacterManagerError(conn, CharacterManagerErrorOperationUnavailable);
            }
        }


        private bool TryFindZoneByName(string zoneName, out Zone zone)
        {
            zone = null;
            if (string.IsNullOrWhiteSpace(zoneName))
                return false;
            zone = _zoneOrder.FirstOrDefault(z =>
                z != null && !string.IsNullOrEmpty(z.name) && z.name.Equals(zoneName.Trim(), StringComparison.OrdinalIgnoreCase));
            return zone != null;
        }

        private Zone ResolveCharacterPlayZone(RRConnection conn, uint charId, SavedCharacter savedChar, out string reason)
        {
            reason = "default";
            TryFindZoneByName("tutorial", out Zone tutorialZone);

            string savedZoneName = savedChar?.currentZoneName;
            if (!TryFindZoneByName(savedZoneName, out Zone savedZone))
            {
                reason = $"missing-saved-zone:{savedZoneName ?? ""}";
                return tutorialZone ?? _zoneOrder.FirstOrDefault();
            }

            if (IsPvpZone(savedZone.name) && TryFindZoneByName("pvp_start", out Zone pvpHub))
            {
                reason = "saved-pvp-arena-redirect:pvp_start";
                conn.PendingSpawnPoint = "respawn";
                return pvpHub;
            }

            if (IsPublicZone(savedZone.name))
            {
                reason = "saved-public-zone";
                return savedZone;
            }

            if (GroupDirectory.Instance.GetGroupForConn(conn.ConnId) != null)
            {
                reason = "saved-group-dungeon";
                return savedZone;
            }

            if (HasRememberedSoloDungeonInstance(charId, savedZone.name, out uint rememberedInstance, out bool expired))
            {
                reason = $"saved-solo-dungeon-memory:{rememberedInstance:X8}";
                return savedZone;
            }

            if (expired)
                reason = "saved-solo-dungeon-expired";
            else
                reason = "saved-solo-dungeon-missing";

            if (!string.IsNullOrEmpty(savedZone.respawnZone) && TryFindZoneByName(savedZone.respawnZone, out Zone respawnZone))
            {
                reason += $":redirect:{respawnZone.name}";
                return respawnZone;
            }

            reason += ":no-respawn-zone";
            return savedZone ?? tutorialZone;
        }

        private void HandleCharacterPlay(RRConnection conn, byte[] data)
        {
            Debug.Log($"HandleCharacterPlay for {conn.LoginName}");
            try
            {
                if (!_persistentCharacters.TryGetValue(conn.LoginName, out var characters) || characters.Count == 0)
                {
                    Debug.LogError($"No characters found for {conn.LoginName}");
                    SendCharacterManagerError(conn, CharacterManagerErrorOperationUnavailable);
                    return;
                }

                if (data == null || data.Length != 4)
                {
                    SendCharacterManagerError(conn, CharacterManagerErrorOperationUnavailable);
                    return;
                }

                var reader = new LEReader(data);
                uint selectedCharId = reader.ReadUInt32();
                Debug.LogError($"[CHAR-PLAY] selectedId={selectedCharId}");
                GCObject character = characters.Find(c => c.Id == selectedCharId);
                if (character == null)
                {
                    SendCharacterManagerError(conn, CharacterManagerErrorOperationUnavailable);
                    return;
                }

                if (HasPendingDisconnectedCharacterSaveForCharacter(character.Id)
                    && (!TryFlushPendingDisconnectedCharacterSaveForLogin(conn.LoginName)
                        || HasPendingDisconnectedCharacterSaveForCharacter(character.Id)))
                {
                    SendCharacterManagerError(conn, CharacterManagerErrorOperationUnavailable);
                    conn.Disconnect();
                    return;
                }

                if (HasPendingPvpResultFence(character.Id))
                {
                    DrainPendingPvpMatchResults(characterId: character.Id);
                    if (HasPendingPvpResultFence(character.Id))
                    {
                        SendCharacterManagerError(conn, CharacterManagerErrorOperationUnavailable);
                        conn.Disconnect();
                        return;
                    }
                }

                uint previousCharacterId = conn.CharSqlId;
                if (!TrySetCurrentCharacterSelection(conn.LoginName, character.Id, previousCharacterId))
                {
                    SendCharacterManagerError(conn, CharacterManagerErrorOperationUnavailable);
                    return;
                }
                if (previousCharacterId != 0 && previousCharacterId != character.Id)
                    InvalidateCharacterRuntimeState(conn, previousCharacterId);
                _selectedCharacter[conn.LoginName] = character;
                _activeCharacter.Remove(conn.LoginName);
                conn.CharSqlId = character.Id;
                Debug.Log($"Selected character: {character.Name} (ID={character.Id})");

                Debug.Log("Character selected for spawning - will create fresh entities during spawn");
                var ackWriter = new LEWriter();
                ackWriter.WriteByte(4);
                ackWriter.WriteByte(5);
                SendCompressedA(conn, 0x01, 0x0F, ackWriter.ToArray());
                Debug.Log("Sent 4/5 acknowledgment");
                var groupWriter = new LEWriter();
                groupWriter.WriteByte(9);
                groupWriter.WriteByte(0);
                SendCompressedA(conn, 0x01, 0x0F, groupWriter.ToArray());
                Debug.Log("Sent 9/0 group connected");

                {
                    var reconnectedGroup = GroupDirectory.Instance.ReconnectMember(conn.LoginName, conn.ConnId);
                    if (reconnectedGroup != null)
                    {
                        uint reconnCharId = GetCharSqlId(conn);
                        byte[] reconnectPacket = GroupPackets.BuildMemberReconnected(reconnectedGroup.GroupId, reconnCharId);
                        foreach (var reconnectedMember in reconnectedGroup.Members)
                        {
                            if (!reconnectedMember.IsOnline) continue;
                            if (reconnectedMember.ConnId == conn.ConnId) continue;
                            var reconnectedMemberConnection = FindConnectionById(reconnectedMember.ConnId);
                            if (reconnectedMemberConnection != null)
                                SendToClient(reconnectedMemberConnection, reconnectPacket);
                        }
                        foreach (var reconnectedMember in reconnectedGroup.Members)
                        {
                            var reconnectedMemberConnection = FindConnectionById(reconnectedMember.ConnId);
                            if (reconnectedMemberConnection != null) reconnectedMemberConnection.GroupConnectedSent = false;
                        }
                        conn.GroupConnectedSent = false;
                        SendGroupConnectedToAll(reconnectedGroup);
                        SendGroupHealthToAll(reconnectedGroup);
                        Debug.LogError($"[GROUP] Reconnected {conn.LoginName} to group {reconnectedGroup.GroupId}, sent 0x4A + full group state, reset all GroupConnectedSent.");
                    }
                }

                Debug.LogError("[ZONE-MSG] Building complete zone message (13/0)...");
                var zoneWriter = new LEWriter();
                zoneWriter.WriteByte(13);
                zoneWriter.WriteByte(0);


                var savedChar = CharacterRepository.GetCharacter(character.Id);
                var startZone = ResolveCharacterPlayZone(conn, character.Id, savedChar, out string zoneResolveReason);
                string zoneName = startZone?.name ?? "tutorial";
                zoneWriter.WriteCString(zoneName);
                uint zoneId = startZone?.id ?? 2781714545u;
                conn.CurrentZoneId = zoneId;
                conn.CurrentZoneName = zoneName;
                _monsterSpawnSentByConn.Remove(conn.ConnId);
                _encounterObjectSentByConn.Remove(conn.ConnId);
                GroupDirectory.Instance.UpdateMemberZone(conn.ConnId, zoneName);
                SendGroupMemberZoneState(conn);
                try { if (conn.CharSqlId != 0) PosseRuntime.Instance.NotifyMemberStateChange(conn.CharSqlId, this); }
                catch (Exception posseException) { Debug.LogError($"[POSSE] zone-notify failed: {posseException.Message}"); }
                Debug.LogError($"[ZONE-MSG] zoneId={zoneId} hex=0x{zoneId:X8}");
                conn.CurrentZoneGcType = ResolveZoneGcType(startZone);

                Debug.LogError($"[ZONE] CurrentZoneGcType set to: {conn.CurrentZoneGcType} reason={zoneResolveReason}");
                AssignInstanceId(conn);
                if (savedChar != null)
                    _activeCharacter[conn.LoginName] = savedChar;
                conn.PendingSavedPositionResume = savedChar != null
                    && IsPublicZone(zoneName)
                    && conn.InstanceId == 0
                    && string.Equals(savedChar.currentZoneName, zoneName, StringComparison.OrdinalIgnoreCase)
                    && (savedChar.positionFixedX != 0 || savedChar.positionFixedY != 0 || savedChar.positionFixedZ != 0)
                    && string.IsNullOrEmpty(conn.PendingSpawnPoint)
                    && conn.PendingSpawnFixedX == 0
                    && conn.PendingSpawnFixedY == 0
                    && conn.PendingSpawnFixedZ == 0
                    && !conn.PendingSpawnPreserveAuthoredPosition;

                uint zoneSeed = ResolveZoneConnectSeed(conn, zoneName);
                zoneWriter.WriteUInt32(zoneSeed);
                Debug.LogError($"[ZONE-MSG] Sending seed: 0x{zoneSeed:X8} zone={zoneName}");
                WriteDungeonQuestRooms(zoneWriter, conn, zoneName);
                zoneWriter.WriteUInt32(0x00);

                byte[] zoneData = zoneWriter.ToArray();
                Debug.LogError($"[ZONE-MSG] Complete zone message: {zoneData.Length} bytes");
                Debug.LogError($"[ZONE-MSG] Hex: {BitConverter.ToString(zoneData)}");
                SendCompressedA(conn, 0x01, 0x0F, zoneData);
                Debug.Log("Sent 13/0 zone info (COMPLETE FORMAT)");
                Debug.Log("Waiting for client to send zone progression request...");

                SocialRuntime.Instance.PlayerOnline(conn.LoginName, character.Name, conn, SendSocialViaAuth);
                SocialRuntime.Instance.SendLoginSocialInit(conn, character.Name, SendSocialViaAuth);

                PosseRuntime.Instance.SendConnectionNotification(conn, true, SendSocialViaAuth);
                SendPosseStateForCharacter(conn, character.Id, SendSocialViaAuth);
                try { PosseRuntime.Instance.NotifyMemberStateChange(character.Id, this); }
                catch (Exception ex2) { Debug.LogError($"[POSSE] login-notify (HandleCharacterPlay) failed: {ex2.Message}"); }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CHARACTER-PLAY] state=failed message='{ex.Message}' stack='{ex.StackTrace}'");
                SendCharacterManagerError(conn, CharacterManagerErrorOperationUnavailable);
            }
        }

        private void HandleEntityRequest(RRConnection conn, LEReader reader, byte[] data)
        {
            if (reader.Remaining < 3)
            {
                HandleClientRequestRespawn(conn);
                return;
            }

            ushort entityId = reader.ReadUInt16();
            byte requestType = reader.ReadByte();
            Debug.LogError($"[ENTITY-REQ] entity={entityId} type=0x{requestType:X2} remaining={reader.Remaining} from {conn.LoginName}");

            switch (requestType)
            {
                case 0x00:
                    HandleMinimumItemQualityRequest(conn, entityId, reader);
                    break;
                case 0x11:
                    HandleStatSpendRequest(conn, reader);
                    break;
                case 0x12:
                    HandleStatReturnRequest(conn, reader);
                    break;
                case 0x13:
                    HandleRespecRequest(conn);
                    break;
                default:
                    HandleClientRequestRespawn(conn);
                    break;
            }
        }

        private void HandleStatSpendRequest(RRConnection conn, LEReader reader)
        {
            if (reader.Remaining < 2) { Debug.LogError("[STAT-SPEND] Not enough data"); return; }

            byte statType = reader.ReadByte();
            byte numPoints = reader.ReadByte();
            Debug.LogError($"[STAT-SPEND] statType={statType} numPoints={numPoints} from {conn.LoginName}");

            if (statType > 3 || numPoints == 0) { Debug.LogError($"[STAT-SPEND] Invalid"); return; }

            if (!_selectedCharacter.TryGetValue(conn.LoginName, out var selChar)) return;
            var savedChar = GetActiveCharacter(conn)?.DeepClone();
            if (savedChar == null) return;

            int totalAllocated = savedChar.statStrength + savedChar.statAgility
                               + savedChar.statEndurance + savedChar.statIntellect;
            int pointsPerLevel = StatPointsPerLevel;
            int totalAvailable = (SavedCharacterLevel.ResolveRuntimeLevel(savedChar) - 1) * pointsPerLevel;
            int remaining = totalAvailable - totalAllocated;

            if (numPoints > remaining) { Debug.LogError($"[STAT-SPEND] Not enough: want={numPoints} have={remaining}"); return; }

            switch (statType)
            {
                case 0: savedChar.statStrength += numPoints; break;
                case 1: savedChar.statAgility += numPoints; break;
                case 2: savedChar.statEndurance += numPoints; break;
                case 3: savedChar.statIntellect += numPoints; break;
            }

            PlayerState playerState = GetPlayerState(conn.ConnId.ToString());
            playerState.ApplyAllocatedStats(savedChar.statStrength, savedChar.statAgility, savedChar.statEndurance, savedChar.statIntellect);
            RecalculateHotbarPassiveBonuses(conn, savedChar, sendModifiers: false);
            if (!TrySaveCharacterForConn(conn, savedChar, "stat-spend"))
            {
                switch (statType)
                {
                    case 0: savedChar.statStrength -= numPoints; break;
                    case 1: savedChar.statAgility -= numPoints; break;
                    case 2: savedChar.statEndurance -= numPoints; break;
                    case 3: savedChar.statIntellect -= numPoints; break;
                }
                playerState.ApplyAllocatedStats(savedChar.statStrength, savedChar.statAgility, savedChar.statEndurance, savedChar.statIntellect);
                RecalculateHotbarPassiveBonuses(conn, savedChar, sendModifiers: false);
                return;
            }
            Debug.LogError($"[STAT-SPEND] STR={savedChar.statStrength} AGI={savedChar.statAgility} END={savedChar.statEndurance} INT={savedChar.statIntellect} pts={remaining - numPoints}");
            SendHeroStatUpdate(conn, 0x11, numPoints, statType);
        }

        private void HandleStatReturnRequest(RRConnection conn, LEReader reader)
        {
            if (reader.Remaining < 2) { Debug.LogError("[STAT-RETURN] Not enough data"); return; }

            byte statType = reader.ReadByte();
            byte numPoints = reader.ReadByte();
            Debug.LogError($"[STAT-RETURN] statType={statType} numPoints={numPoints} from {conn.LoginName}");

            if (statType > 3 || numPoints == 0) return;
            if (!_selectedCharacter.TryGetValue(conn.LoginName, out var selChar)) return;
            var savedChar = GetActiveCharacter(conn)?.DeepClone();
            if (savedChar == null) return;

            int currentVal = 0;
            switch (statType) { case 0: currentVal = savedChar.statStrength; break; case 1: currentVal = savedChar.statAgility; break; case 2: currentVal = savedChar.statEndurance; break; case 3: currentVal = savedChar.statIntellect; break; }
            if (numPoints > currentVal) return;

            switch (statType)
            {
                case 0: savedChar.statStrength -= numPoints; break;
                case 1: savedChar.statAgility -= numPoints; break;
                case 2: savedChar.statEndurance -= numPoints; break;
                case 3: savedChar.statIntellect -= numPoints; break;
            }

            if (!TrySaveCharacterForConn(conn, savedChar, "stat-return"))
            {
                switch (statType)
                {
                    case 0: savedChar.statStrength += numPoints; break;
                    case 1: savedChar.statAgility += numPoints; break;
                    case 2: savedChar.statEndurance += numPoints; break;
                    case 3: savedChar.statIntellect += numPoints; break;
                }
                return;
            }
            Debug.LogError($"[STAT-RETURN] STR={savedChar.statStrength} AGI={savedChar.statAgility} END={savedChar.statEndurance} INT={savedChar.statIntellect}");
            PlayerState playerState = GetPlayerState(conn.ConnId.ToString());
            uint oldMaxWire = playerState.MaxHPWire;
            playerState.ApplyAllocatedStats(savedChar.statStrength, savedChar.statAgility, savedChar.statEndurance, savedChar.statIntellect);
            RecalculateHotbarPassiveBonuses(conn, savedChar, sendModifiers: false);
            if (playerState.MaxHPWire < oldMaxWire && playerState.CurrentHPWire > playerState.MaxHPWire)
                playerState.BeginPassiveMaxTransition(oldMaxWire);
            SendHeroStatUpdate(conn, 0x12, numPoints, statType);
        }

        private List<(int level, int fx32Value)> _respecCostCurveFx;

        private static int GetRespecCooldownSeconds()
        {
            var knobs = DungeonRunners.Data.GCDatabase.Instance.GlobalKnobs;
            if (knobs == null || !knobs.HasProperty("ReSpecTime"))
                throw new InvalidDataException("GlobalKnobs.ReSpecTime not loaded");
            return Math.Max(0, knobs.GetInt("ReSpecTime", 0));
        }

        private int EvaluateReSpecCostCurveFx32(int targetLevel)
        {
            if (_respecCostCurveFx == null)
            {
                _respecCostCurveFx = new List<(int, int)>();
                var tables = DungeonRunners.Data.GCDatabase.Instance.GetNode("Tables");
                var respecTable = tables?.GetChild("ReSpecCost");
                if (respecTable != null)
                {
                    foreach (var entry in respecTable.AnonymousChildren)
                    {
                        int level = entry.GetInt("Level", 0);
                        if (level > 0)
                        {
                            int fx32 = entry.GetFixed32Ceiling("Value", 0);
                            _respecCostCurveFx.Add((level, fx32));
                        }
                    }
                    _respecCostCurveFx.Sort((a, b) => a.level.CompareTo(b.level));
                    Debug.LogError($"[RESPEC-CURVE] Loaded {_respecCostCurveFx.Count} entries from Tables.ReSpecCost");
                    foreach (var e in _respecCostCurveFx)
                        Debug.LogError($"[RESPEC-CURVE]   L{e.level} = fx32({e.fx32Value})");
                }
                else
                {
                    RuntimeEvidence.LogFallbackHit("respec-cost", "missing-ReSpecCost", "sourceFunction=blocked compatibility=none", 1);
                    throw new InvalidDataException("Tables.ReSpecCost not loaded");
                }
            }

            if (_respecCostCurveFx.Count == 0)
            {
                RuntimeEvidence.LogFallbackHit("respec-cost", "empty-ReSpecCost", "sourceFunction=blocked compatibility=none", 1);
                throw new InvalidDataException("Tables.ReSpecCost has no entries");
            }

            if (targetLevel <= _respecCostCurveFx[0].level)
                return _respecCostCurveFx[0].fx32Value;

            if (targetLevel >= _respecCostCurveFx[_respecCostCurveFx.Count - 1].level)
                return _respecCostCurveFx[_respecCostCurveFx.Count - 1].fx32Value;

            for (int curveIndex = 1; curveIndex < _respecCostCurveFx.Count; curveIndex++)
            {
                if (targetLevel <= _respecCostCurveFx[curveIndex].level)
                {
                    int lowerLevel = _respecCostCurveFx[curveIndex - 1].level;
                    int upperLevel = _respecCostCurveFx[curveIndex].level;
                    int lowerValue = _respecCostCurveFx[curveIndex - 1].fx32Value;
                    int upperValue = _respecCostCurveFx[curveIndex].fx32Value;

                    int interpolationFixed = ((targetLevel - lowerLevel) * 65536) / (upperLevel - lowerLevel);
                    int delta = upperValue - lowerValue;
                    int interpolatedDelta = (int)(((long)delta * interpolationFixed) >> 16);
                    return lowerValue + interpolatedDelta;
                }
            }

            return _respecCostCurveFx[_respecCostCurveFx.Count - 1].fx32Value;
        }

        private void HandleRespecRequest(RRConnection conn)
        {
            Debug.LogError($"[RESPEC] from {conn.LoginName}");

            if (!_selectedCharacter.TryGetValue(conn.LoginName, out var selChar)) return;
            var savedChar = GetActiveCharacter(conn)?.DeepClone();
            if (savedChar == null) return;

            int nowUnix = checked((int)ServerRuntime.ReadUnixTimeSeconds());
            int cooldownSeconds = GetRespecCooldownSeconds();
            int elapsed = nowUnix - savedChar.lastRespecTime;
            if (savedChar.lastRespecTime > 0 && elapsed < cooldownSeconds)
            {
                int remaining = cooldownSeconds - elapsed;
                int mins = remaining / 60;
                int secs = remaining % 60;
                SendSystemMessage(conn, $"Respec on cooldown. {mins}m {secs}s remaining.");
                Debug.LogError($"[RESPEC] REJECTED: cooldown {remaining}s remaining");
                return;
            }

                int curveFixed32 = EvaluateReSpecCostCurveFx32(SavedCharacterLevel.ResolveRuntimeLevel(savedChar));
            uint goldCost = (uint)(((long)curveFixed32 * 1000) >> 8);
            Debug.LogError($"[RESPEC] Level={savedChar.level} fx32={curveFixed32} -> cost={goldCost} gold");

            if (savedChar.gold < goldCost)
            {
                SendSystemMessage(conn, $"Not enough gold to respec. Need {goldCost}, have {savedChar.gold}.");
                Debug.LogError($"[RESPEC] REJECTED: gold {savedChar.gold} < cost {goldCost}");
                return;
            }

            uint previousGold = savedChar.gold;
            int previousStrength = savedChar.statStrength;
            int previousAgility = savedChar.statAgility;
            int previousEndurance = savedChar.statEndurance;
            int previousIntellect = savedChar.statIntellect;
            int previousRespecTime = savedChar.lastRespecTime;
            int previousRespecCount = savedChar.respecCount;
            savedChar.gold -= goldCost;
            savedChar.statStrength = 0;
            savedChar.statAgility = 0;
            savedChar.statEndurance = 0;
            savedChar.statIntellect = 0;
            savedChar.lastRespecTime = nowUnix;
            savedChar.respecCount++;

            if (!TrySaveCharacterForConn(conn, savedChar, "respec"))
            {
                savedChar.gold = previousGold;
                savedChar.statStrength = previousStrength;
                savedChar.statAgility = previousAgility;
                savedChar.statEndurance = previousEndurance;
                savedChar.statIntellect = previousIntellect;
                savedChar.lastRespecTime = previousRespecTime;
                savedChar.respecCount = previousRespecCount;
                return;
            }
            Debug.LogError($"[RESPEC] Stats reset, gold {savedChar.gold + goldCost} -> {savedChar.gold} (cost {goldCost}), respec #{savedChar.respecCount}");
            PlayerState playerState = GetPlayerState(conn.ConnId.ToString());
            uint oldMaxWire = playerState.MaxHPWire;
            playerState.ApplyAllocatedStats(savedChar.statStrength, savedChar.statAgility, savedChar.statEndurance, savedChar.statIntellect);
            RecalculateHotbarPassiveBonuses(conn, savedChar, sendModifiers: false);
            if (playerState.MaxHPWire < oldMaxWire && playerState.CurrentHPWire > playerState.MaxHPWire)
                playerState.BeginPassiveMaxTransition(oldMaxWire);

            uint avatarId = GetPlayerAvatarId(conn.LoginName);
            if (avatarId == 0) return;

            if (conn.UnitContainerId != 0)
            {
                var goldWriter = new LEWriter();
                goldWriter.WriteByte(0x07);
                goldWriter.WriteByte(0x35);
                goldWriter.WriteUInt16(conn.UnitContainerId);
                goldWriter.WriteByte(0x20);
                goldWriter.WriteInt32(-(int)goldCost);
                goldWriter.WriteByte(0x00);
                goldWriter.WriteUInt32(0x00000000);
                goldWriter.WriteByte(0x01);
                WritePlayerEntitySynch(conn, goldWriter);
                goldWriter.WriteByte(0x06);
                byte[] goldPacket = goldWriter.ToArray();
                SendCompressedA(conn, 0x01, 0x0F, goldPacket);
                Debug.LogError($"[RESPEC-GOLD] Sent RemoveCurrency {goldCost} (merchant format)");
            }

            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x03);
            writer.WriteUInt16((ushort)avatarId);
            writer.WriteByte(0x13);
            writer.WriteByte(0x02);
            writer.WriteUInt32(0xFFFF00);
            writer.WriteByte(0x06);
            SendCompressedA(conn, 0x01, 0x0F, writer.ToArray());

            Debug.LogError($"[RESPEC] Sent respec packet, avatar={avatarId}");
        }

        private void SendHeroStatUpdate(RRConnection conn, byte subType, byte numPoints, byte statType)
        {
            uint avatarId = GetPlayerAvatarId(conn.LoginName);
            if (avatarId == 0) return;

            PlayerState playerState = GetPlayerState(conn.ConnId.ToString());
            uint capturedHPWire = playerState != null ? playerState.EntitySynchInfoHP : 0;
            uint capturedSimulationTick = _combatTick;
            conn.MessageQueue.EnqueueDeferred(() => BuildHeroStatUpdate(conn, (ushort)avatarId, subType, numPoints, statType, capturedHPWire, capturedSimulationTick));
            Debug.LogError($"[STAT-UPDATE] Queued 0x03 sub=0x{subType:X2} pts={numPoints} stat={statType} avatar={avatarId} hp={capturedHPWire} captureTick={capturedSimulationTick}");

            foreach (RRConnection viewer in GetConnectionInsertionOrderSnapshot())
            {
                if (viewer == null || viewer == conn || !viewer.IsSpawned || !viewer.AllowFlush)
                    continue;
                if (!IsSameMovementRuntime(viewer, conn))
                    continue;
                if (!_remoteAvatarIds.TryGetValue(viewer.LoginName, out Dictionary<string, ushort> avatarMap)
                    || !avatarMap.TryGetValue(conn.LoginName, out ushort remoteAvatarId)
                    || remoteAvatarId == 0)
                    continue;

                RRConnection capturedViewer = viewer;
                ushort capturedRemoteAvatarId = remoteAvatarId;
                capturedViewer.MessageQueue.EnqueueDeferred(() => BuildRemoteHeroStatUpdate(conn, capturedViewer, capturedRemoteAvatarId, subType, numPoints, statType, capturedHPWire, capturedSimulationTick, (ushort)avatarId));
                Debug.LogError($"[STAT-UPDATE] Queued remote 0x03 sub=0x{subType:X2} pts={numPoints} stat={statType} avatar={remoteAvatarId} hp={capturedHPWire} captureTick={capturedSimulationTick} source={conn.LoginName} viewer={viewer.LoginName}");
            }
        }

        private byte[] BuildHeroStatUpdate(RRConnection conn, ushort avatarId, byte subType, byte numPoints, byte statType, uint capturedHPWire, uint capturedSimulationTick)
        {
            if (conn == null || !conn.IsConnected || avatarId == 0)
                return Array.Empty<byte>();
            var writer = new LEWriter();
            writer.WriteByte(0x03);
            writer.WriteUInt16(avatarId);
            writer.WriteByte(subType);
            writer.WriteByte(statType);
            writer.WriteByte(numPoints);
            if (!TryWriteCapturedPlayerEntitySynchInfo(conn, writer, avatarId, capturedHPWire, "STAT-UPDATE"))
                return Array.Empty<byte>();
            Debug.LogError($"[STAT-UPDATE] Built 0x03 sub=0x{subType:X2} pts={numPoints} stat={statType} avatar={avatarId} hp={capturedHPWire} captureTick={capturedSimulationTick} tick={_combatTick}");
            return writer.ToArray();
        }

        private byte[] BuildRemoteHeroStatUpdate(RRConnection source, RRConnection viewer, ushort remoteAvatarId, byte subType, byte numPoints, byte statType, uint capturedHPWire, uint capturedSimulationTick, ushort sourceAvatarId)
        {
            if (source == null || viewer == null || !source.IsConnected || !viewer.IsConnected || remoteAvatarId == 0)
                return Array.Empty<byte>();
            var writer = new LEWriter();
            writer.WriteByte(0x03);
            writer.WriteUInt16(remoteAvatarId);
            writer.WriteByte(subType);
            writer.WriteByte(statType);
            writer.WriteByte(numPoints);
            if (!TryWriteCapturedPlayerEntitySynchInfo(source, writer, sourceAvatarId, capturedHPWire, "MP-STAT"))
                return Array.Empty<byte>();
            Debug.LogError($"[STAT-UPDATE] Built remote 0x03 sub=0x{subType:X2} pts={numPoints} stat={statType} avatar={remoteAvatarId} hp={capturedHPWire} captureTick={capturedSimulationTick} source={source.LoginName} viewer={viewer.LoginName} tick={_combatTick}");
            return writer.ToArray();
        }

        private void HandleClientRequestRespawn(RRConnection conn)
        {
            Debug.LogError($"[RESPAWN] HandleClientRequestRespawn for {conn?.LoginName ?? ""}");

            try
            {
                if (conn == null || !_playerStates.TryGetValue(conn.ConnId.ToString(), out var playerState) || playerState == null)
                {
                    Debug.LogError("[RESPAWN] state=blocked reason=missing-player-state");
                    return;
                }

                CombatPlayer combatPlayer = conn.Avatar != null && conn.Avatar.Id > 0
                    ? CombatRuntime.Instance.GetPlayer((uint)conn.Avatar.Id)
                    : null;
                if (playerState.CurrentHPWire != 0 || (combatPlayer != null && combatPlayer.IsAlive))
                {
                    Debug.LogError($"[RESPAWN] state=blocked reason=player-alive hp={playerState.CurrentHPWire} combatAlive={combatPlayer?.IsAlive}");
                    return;
                }

                if (_duelRuntime.IsInDuel(conn.LoginName))
                {
                    Debug.LogError("[RESPAWN] state=blocked reason=duel-does-not-allow-respawn");
                    return;
                }

                Gameplay.PVPMatchmaking.Match match = Gameplay.PVPMatchmaking.Instance.GetMatchForPlayer(conn.LoginName);
                if (match != null)
                {
                    if (match.Phase != Gameplay.PVPMatchmaking.Match.MatchPhase.Combat || !match.AllowRespawn)
                    {
                        Debug.LogError($"[RESPAWN] state=blocked reason=pvp-rules phase={match.Phase} allowRespawn={match.AllowRespawn}");
                        return;
                    }

                    string entryPoint = match.FreeForAll
                        ? "ffa_respawn"
                        : match.IsRed(conn.LoginName) ? "red_team_respawn" : "blue_team_respawn";
                    if (!TryResolveRespawnDestination(match.ZoneName, entryPoint, out ResolvedRespawnDestination pvpDestination, out string pvpFailure))
                    {
                        Debug.LogError($"[RESPAWN] state=blocked reason={pvpFailure} zone='{match.ZoneName}' spawnPoint='{entryPoint}'");
                        if (RespawnTracking)
                            Debug.LogError($"[RESPAWN-TRACK] phase=resolve result=blocked characterId={GetCharSqlId(conn)} mode=pvp sourceZone='{conn.CurrentZoneName}' sourceInstance='{RoomRuntime.NormalizeInstanceKey(GetInstanceZoneKey(conn))}' targetZone='{match.ZoneName}' spawnPoint='{entryPoint}' reason='{pvpFailure}' hp={playerState.CurrentHPWire} alive={combatPlayer?.IsAlive}");
                        return;
                    }
                    string pvpSourceZone = conn.CurrentZoneName ?? string.Empty;
                    string pvpSourceInstance = RoomRuntime.NormalizeInstanceKey(GetInstanceZoneKey(conn));
                    if (!ChangeZone(
                        conn,
                        pvpDestination.Zone.name,
                        pvpDestination.SpawnPoint,
                        (uint)match.InstanceId,
                        restoreFullHealth: true,
                        exactSpawnFixedX: pvpDestination.PosFixedX,
                        exactSpawnFixedY: pvpDestination.PosFixedY,
                        exactSpawnFixedZ: pvpDestination.PosFixedZ,
                        exactSpawnHeadingFixed: pvpDestination.HeadingFixed))
                    {
                        Debug.LogError($"[RESPAWN] state=blocked reason=persist-failed zone='{pvpDestination.Zone.name}' spawnPoint='{pvpDestination.SpawnPoint}'");
                        if (RespawnTracking)
                            Debug.LogError($"[RESPAWN-TRACK] phase=persist result=blocked characterId={GetCharSqlId(conn)} mode=pvp sourceZone='{pvpSourceZone}' sourceInstance='{pvpSourceInstance}' targetZone='{pvpDestination.Zone.name}' targetInstance={match.InstanceId:X8} spawnPoint='{pvpDestination.SpawnPoint}' reason=persist-failed hp={playerState.CurrentHPWire} alive={combatPlayer?.IsAlive}");
                        return;
                    }
                    Debug.LogError($"[RESPAWN] mode=pvp match={match.MatchId} zone={pvpDestination.Zone.name}#{match.InstanceId} entry={pvpDestination.SpawnPoint} fixed=({pvpDestination.PosFixedX},{pvpDestination.PosFixedY},{pvpDestination.PosFixedZ}) headingFixed={pvpDestination.HeadingFixed} hp={playerState.CurrentHPWire} mana={playerState.CurrentManaWire}");
                    if (RespawnTracking)
                        Debug.LogError($"[RESPAWN-TRACK] phase=commit characterId={GetCharSqlId(conn)} mode=pvp sourceZone='{pvpSourceZone}' sourceInstance='{pvpSourceInstance}' targetZone='{pvpDestination.Zone.name}' targetInstance={match.InstanceId:X8} spawnPoint='{pvpDestination.SpawnPoint}' fixed=({pvpDestination.PosFixedX},{pvpDestination.PosFixedY},{pvpDestination.PosFixedZ}) headingFixed={pvpDestination.HeadingFixed} pathMap=walkable hp={playerState.CurrentHPWire} alive={combatPlayer?.IsAlive}");
                    return;
                }

                string respawnZone = GetRespawnZone(conn.CurrentZoneGcType);
                if (string.IsNullOrWhiteSpace(respawnZone))
                {
                    Debug.LogError($"[RESPAWN] state=blocked reason=missing-authored-respawn-zone currentGcType='{conn.CurrentZoneGcType ?? ""}' currentZone='{conn.CurrentZoneName ?? ""}'");
                    if (RespawnTracking)
                        Debug.LogError($"[RESPAWN-TRACK] phase=resolve result=blocked characterId={GetCharSqlId(conn)} mode=pve sourceGc='{conn.CurrentZoneGcType ?? ""}' sourceZone='{conn.CurrentZoneName ?? ""}' sourceInstance='{RoomRuntime.NormalizeInstanceKey(GetInstanceZoneKey(conn))}' reason=missing-authored-respawn-zone hp={playerState.CurrentHPWire} alive={combatPlayer?.IsAlive}");
                    return;
                }

                Zone respawnTarget = _zoneOrder.FirstOrDefault(zone => zone.name.Equals(respawnZone, StringComparison.OrdinalIgnoreCase));
                string respawnSpawnPoint = respawnTarget?.respawnSpawnPoint;
                if (!TryResolveRespawnDestination(respawnZone, respawnSpawnPoint, out ResolvedRespawnDestination destination, out string failure))
                {
                    Debug.LogError($"[RESPAWN] state=blocked reason={failure} zone='{respawnZone}' spawnPoint='{respawnSpawnPoint ?? ""}'");
                    if (RespawnTracking)
                        Debug.LogError($"[RESPAWN-TRACK] phase=resolve result=blocked characterId={GetCharSqlId(conn)} mode=pve sourceGc='{conn.CurrentZoneGcType ?? ""}' sourceZone='{conn.CurrentZoneName ?? ""}' sourceInstance='{RoomRuntime.NormalizeInstanceKey(GetInstanceZoneKey(conn))}' targetZone='{respawnZone}' spawnPoint='{respawnSpawnPoint ?? ""}' reason='{failure}' hp={playerState.CurrentHPWire} alive={combatPlayer?.IsAlive}");
                    return;
                }

                string sourceGcType = conn.CurrentZoneGcType ?? string.Empty;
                string sourceZone = conn.CurrentZoneName ?? string.Empty;
                string sourceInstance = RoomRuntime.NormalizeInstanceKey(GetInstanceZoneKey(conn));
                if (!ChangeZone(
                    conn,
                    destination.Zone.name,
                    destination.SpawnPoint,
                    restoreFullHealth: true,
                    exactSpawnFixedX: destination.PosFixedX,
                    exactSpawnFixedY: destination.PosFixedY,
                    exactSpawnFixedZ: destination.PosFixedZ,
                    exactSpawnHeadingFixed: destination.HeadingFixed))
                {
                    Debug.LogError($"[RESPAWN] state=blocked reason=persist-failed zone='{destination.Zone.name}' spawnPoint='{destination.SpawnPoint}'");
                    if (RespawnTracking)
                        Debug.LogError($"[RESPAWN-TRACK] phase=persist result=blocked characterId={GetCharSqlId(conn)} mode=pve sourceGc='{sourceGcType}' sourceZone='{sourceZone}' sourceInstance='{sourceInstance}' targetZone='{destination.Zone.name}' spawnPoint='{destination.SpawnPoint}' reason=persist-failed hp={playerState.CurrentHPWire} alive={combatPlayer?.IsAlive}");
                    return;
                }
                Debug.LogError($"[RESPAWN] {sourceGcType} -> {destination.Zone.name} spawnPoint='{destination.SpawnPoint}' fixed=({destination.PosFixedX},{destination.PosFixedY},{destination.PosFixedZ}) headingFixed={destination.HeadingFixed} hp={playerState.CurrentHPWire} mana={playerState.CurrentManaWire}");
                if (RespawnTracking)
                    Debug.LogError($"[RESPAWN-TRACK] phase=commit characterId={GetCharSqlId(conn)} mode=pve sourceGc='{sourceGcType}' sourceZone='{sourceZone}' sourceInstance='{sourceInstance}' targetZone='{destination.Zone.name}' spawnPoint='{destination.SpawnPoint}' fixed=({destination.PosFixedX},{destination.PosFixedY},{destination.PosFixedZ}) headingFixed={destination.HeadingFixed} pathMap=walkable hp={playerState.CurrentHPWire} alive={combatPlayer?.IsAlive}");
            }
            catch (Exception ex)
            {
                if (conn != null)
                {
                    conn.PendingSpawnPreserveAuthoredPosition = false;
                    conn.PendingSpawnHeadingOverride = false;
                    conn.RespawnFullHPPending = false;
                    conn.PendingSpawnFixedX = 0;
                    conn.PendingSpawnFixedY = 0;
                    conn.PendingSpawnFixedZ = 0;
                }
                Debug.LogError($"[RESPAWN] state=failed message='{ex.Message}' stack='{ex.StackTrace}'");
            }
        }

        private bool TryResolveRespawnDestination(string zoneName, string spawnPoint, out ResolvedRespawnDestination destination, out string failure)
        {
            destination = null;
            failure = string.Empty;
            if (string.IsNullOrWhiteSpace(zoneName))
            {
                failure = "missing-authored-respawn-zone";
                return false;
            }
            Zone zone = _zoneOrder.FirstOrDefault(candidate => candidate.name.Equals(zoneName, StringComparison.OrdinalIgnoreCase));
            if (zone == null)
            {
                failure = "unresolved-respawn-zone";
                return false;
            }
            if (string.IsNullOrWhiteSpace(spawnPoint))
            {
                failure = "missing-authored-respawn-spawn-point";
                return false;
            }
            ZoneWaypointData waypoint = AuthoredGameplayCatalog.GetWaypointsForZone(zone.name).FirstOrDefault(candidate =>
                string.Equals(candidate.name, spawnPoint, StringComparison.OrdinalIgnoreCase));
            if (waypoint == null)
            {
                failure = "unresolved-respawn-spawn-point";
                return false;
            }
            PathMap pathMap = PathMapCatalog.Instance.GetPathMap(zone.name);
            if (pathMap == null)
            {
                failure = "missing-respawn-pathmap";
                return false;
            }
            if (!pathMap.IsWalkableFixed(waypoint.PosFixedX, waypoint.PosFixedY))
            {
                failure = "respawn-spawn-point-not-walkable";
                return false;
            }
            destination = new ResolvedRespawnDestination
            {
                Zone = zone,
                SpawnPoint = waypoint.name,
                PosFixedX = waypoint.PosFixedX,
                PosFixedY = waypoint.PosFixedY,
                PosFixedZ = waypoint.PosFixedZ,
                HeadingFixed = waypoint.HeadingFixed
            };
            return true;
        }

        private (int x, int y, int z)? GetRespawnPosition(string zoneName)
        {
            if (string.IsNullOrWhiteSpace(zoneName))
                return null;
            var zone = _zoneOrder.FirstOrDefault(z =>
                z.name.Equals(zoneName, StringComparison.OrdinalIgnoreCase));
            if (zone == null || string.IsNullOrWhiteSpace(zone.respawnSpawnPoint))
                return null;
            ZoneWaypointData waypoint = AuthoredGameplayCatalog.GetWaypointsForZone(zone.name).FirstOrDefault(candidate =>
                string.Equals(candidate.name, zone.respawnSpawnPoint, StringComparison.OrdinalIgnoreCase));
            return waypoint == null
                ? null
                : (waypoint.PosFixedX, waypoint.PosFixedY, waypoint.PosFixedZ);
        }
        private string GetRespawnZone(string currentZoneGcType)
        {
            string requested = currentZoneGcType?.Trim();
            var zone = _zoneOrder.FirstOrDefault(z =>
                !string.IsNullOrEmpty(requested) &&
                !string.IsNullOrEmpty(z.gcType) &&
                z.gcType.Equals(requested, StringComparison.OrdinalIgnoreCase));

            if (zone == null && !string.IsNullOrEmpty(requested))
            {
                zone = _zoneOrder.FirstOrDefault(z =>
                    !string.IsNullOrEmpty(z.name) &&
                    (z.name.Equals(requested, StringComparison.OrdinalIgnoreCase) ||
                     ("world." + z.name).Equals(requested, StringComparison.OrdinalIgnoreCase)));
            }

            if (zone != null && !string.IsNullOrEmpty(zone.respawnZone))
                return zone.respawnZone;

            return null;
        }





        private static void WriteGCType(LEWriter writer, string typeName, bool preserveCase = false)
        {
            bool verbose = VerbosePacketLogging;
            if (verbose)
                Debug.LogError($"[WRITE-GC-TYPE] phase=start type='{typeName}' preserveCase={preserveCase}");
            int positionBefore = writer.Position;
            if (verbose)
                Debug.LogError($"[WRITE-GC-TYPE] positionBefore={positionBefore}");

            string safeTypeName = preserveCase ? typeName : typeName.ToLower();

            if (verbose)
                Debug.LogError($"[WRITE-GC-TYPE] safeType='{safeTypeName}' original='{typeName}'");
            byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(safeTypeName);
            if (verbose)
                Debug.LogError($"[WRITE-GC-TYPE] stringBytes={nameBytes.Length}");
            writer.WriteByte(0xFF);
            if (verbose)
                Debug.LogError($"[WRITE-GC-TYPE] write=0xFF position={positionBefore}");
            writer.WriteCString(safeTypeName);
            if (verbose)
                Debug.LogError($"[WRITE-GC-TYPE] cstring=complete");
            int positionAfter = writer.Position;
            int bytesWritten = positionAfter - positionBefore;
            int expectedBytes = 1 + nameBytes.Length + 1;
            if (verbose)
            {
                Debug.LogError($"[WRITE-GC-TYPE] positionAfter={positionAfter}");
                Debug.LogError($"[WRITE-GC-TYPE] bytesWritten={bytesWritten} expectedBytes={expectedBytes}");
                if (bytesWritten != expectedBytes)
                {
                    Debug.LogError($"[WRITE-GC-TYPE] state=mismatch expectedBytes={expectedBytes} bytesWritten={bytesWritten}");
                }
                else
                {
                    Debug.LogError($"[WRITE-GC-TYPE] state=matched");
                }
                string hex = BitConverter.ToString(nameBytes).Replace("-", " ");
                Debug.LogError($"[WRITE-GC-TYPE] hex=FF {hex} 00");
            }
        }

        private static void WriteGCTypeTag22(LEWriter writer, string typeName, bool preserveCase = false)
        {
            string safeTypeName = preserveCase ? typeName : typeName.ToLower();

            byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(safeTypeName);
            writer.WriteByte(0x16);
            writer.WriteUInt16((ushort)nameBytes.Length);
            writer.WriteBytes(nameBytes);
            if (VerbosePacketLogging)
                Debug.LogError($"[WRITE-GC-TYPE-TAG22] type='{safeTypeName}' bytes={3 + nameBytes.Length} opcode=0x16");
        }






        public ushort ResolveRemotePlayerEntityId(RRConnection viewerConn, RRConnection ownerConn)
        {
            if (string.IsNullOrEmpty(viewerConn?.LoginName) || string.IsNullOrEmpty(ownerConn?.LoginName)) return 0;
            if (_remotePlayerIds.TryGetValue(viewerConn.LoginName, out var playerMap) && playerMap.TryGetValue(ownerConn.LoginName, out ushort remotePlayerId) && remotePlayerId != 0)
                return remotePlayerId;
            if (_remoteAvatarIds.TryGetValue(viewerConn.LoginName, out var avatarMap) && avatarMap.TryGetValue(ownerConn.LoginName, out ushort remoteAvatarId))
                return remoteAvatarId;
            return 0;
        }

        private void ProcessPreparedProceduralZoneJoins()
        {
            if (_pendingProceduralZoneJoins.Count == 0)
                return;
            foreach (RRConnection conn in GetConnectionInsertionOrderSnapshot())
            {
                if (conn == null || !_pendingProceduralZoneJoins.TryGetValue(conn.ConnId, out var pending))
                    continue;
                if (!object.ReferenceEquals(conn, pending.Connection) || !conn.IsConnected
                    || conn.CurrentZoneId != pending.ZoneId || conn.InstanceId != pending.InstanceId)
                {
                    _pendingProceduralZoneJoins.Remove(conn.ConnId);
                    continue;
                }
                string instanceKey = GetInstanceZoneKey(conn);
                uint layoutSeed = ResolveZoneLayoutSeed(conn, conn.CurrentZoneName);
                if (!ZoneSpawner.Instance.TryCompleteProceduralSnapshotPreparation(conn.CurrentZoneName, instanceKey, layoutSeed))
                    continue;
                _pendingProceduralZoneJoins.Remove(conn.ConnId);
                Debug.LogError($"[DUNGEON-PREP] instance='{instanceKey}' conn={conn.ConnId} state=join-resumed");
                HandleZoneChannel(conn, pending.Body);
            }
        }

        private void EnsureReplicaEntityIds(RRConnection sourceConn)
        {
            if (sourceConn == null || sourceConn.ReplicaBehaviorId != 0)
                return;
            sourceConn.ReplicaAvatarId = (ushort)AllocateGeneralEntityId();
            sourceConn.ReplicaBehaviorId = (ushort)AllocateGeneralEntityId();
            sourceConn.ReplicaSkillsId = (ushort)AllocateGeneralEntityId();
            sourceConn.ReplicaManipId = (ushort)AllocateGeneralEntityId();
            sourceConn.ReplicaModId = (ushort)AllocateGeneralEntityId();
            sourceConn.ReplicaPlayerId = (ushort)AllocateGeneralEntityId();
        }

        private static string RemoteAvatarResourceKey(string viewerLogin, string sourceLogin)
            => $"{viewerLogin}\u001f{sourceLogin}";

        private static void ResolveRemoteAvatarWorldPosition(RRConnection playerConn, out int posX, out int posY, out int posZ, out int heading)
        {
            if (playerConn != null && playerConn.HasLivePlayerPosition)
            {
                posX = playerConn.LivePlayerPosFixedX;
                posY = playerConn.LivePlayerPosFixedY;
                posZ = playerConn.LivePlayerPosFixedZ;
                heading = playerConn.LivePlayerHeadingFixed;
                return;
            }
            posX = playerConn.PlayerPosFixedX;
            posY = playerConn.PlayerPosFixedY;
            posZ = playerConn.PlayerPosFixedZ;
            heading = playerConn.PlayerHeadingFixed;
        }

        private static void WriteRemoteAvatarInitPayload(LEWriter writer, PlayerState remoteState, ushort remotePlayerId, uint hpWire, uint manaWire, SavedCharacter remoteChar, int posX, int posY, int posZ, int heading)
        {
            byte level = (byte)Math.Clamp(remoteState.Level, 1, byte.MaxValue);

            writer.WriteUInt32(0x06);
            writer.WriteInt32(posX);
            writer.WriteInt32(posY);
            writer.WriteInt32(posZ);
            writer.WriteInt32(heading);
            writer.WriteByte(0x01);
            writer.WriteUInt16(0);

            writer.WriteByte(0x07);
            writer.WriteByte(level);
            writer.WriteUInt16(0);
            writer.WriteUInt16(0);
            writer.WriteUInt16(remotePlayerId);
            writer.WriteUInt32(hpWire);
            writer.WriteUInt32(manaWire);

            writer.WriteUInt32(remoteState.Experience);
            writer.WriteUInt16((ushort)(remoteChar?.statStrength ?? 0));
            writer.WriteUInt16((ushort)(remoteChar?.statAgility ?? 0));
            writer.WriteUInt16((ushort)(remoteChar?.statEndurance ?? 0));
            writer.WriteUInt16((ushort)(remoteChar?.statIntellect ?? 0));
            writer.WriteUInt16(0);
            writer.WriteUInt16(0);
            WriteHeroPvpFields(writer, remoteChar);
            writer.WriteByte(remoteChar?.face ?? 0);
            writer.WriteByte(remoteChar?.hair ?? 0);
            writer.WriteByte(remoteChar?.hairColor ?? 0);
        }

        private bool WriteRemotePassiveModifierAdds(LEWriter writer, RRConnection sourceConn, ushort modifiersComponentId, List<PassiveManipulator> passives)
        {
            if (writer == null || sourceConn == null || modifiersComponentId == 0)
                return false;
            int written = 0;
            foreach (PassiveManipulator passive in passives ?? new List<PassiveManipulator>())
            {
                string modifierPath = PassiveAttributeModifiers.ResolveModifierPath(passive.Skill);
                if (string.IsNullOrWhiteSpace(modifierPath))
                    continue;
                writer.WriteByte(0x35);
                writer.WriteUInt16(modifiersComponentId);
                writer.WriteByte(0x00);
                GCTypeCodec.WriteTypeId(writer, modifierPath);
                writer.WriteUInt32(passive.ModifierId);
                writer.WriteByte(passive.Level);
                writer.WriteUInt32(0x00000000);
                writer.WriteUInt32(0x00000000);
                writer.WriteByte(0x01);
                if (!TryWriteRemoteAvatarEntitySynchInfo(sourceConn, writer, modifiersComponentId, 0x00, "MP-SPAWN-PASSIVE"))
                    return false;
                written++;
            }
            Debug.LogError($"[MULTIPLAYER-PASSIVE] source={sourceConn.LoginName} component={modifiersComponentId} count={written} phase=after-root-before-first-behavior-update sourceFunction=Modifiers::processAddModifier@0x00502280");
            return true;
        }

        private bool WriteRemoteZoneSpawnInvulnerabilityAdd(LEWriter writer, RRConnection sourceConn, ushort modifiersComponentId)
        {
            if (writer == null || sourceConn == null || modifiersComponentId == 0 || string.IsNullOrEmpty(sourceConn.LoginName))
                return false;
            ActiveModifier modifier = _activeModifiers.ListFor(sourceConn.LoginName, _combatTick)
                .FirstOrDefault(candidate => string.Equals(
                    candidate.GCType,
                    "avatar.base.ZoneSpawnInvulnerabilityModifier",
                    StringComparison.OrdinalIgnoreCase));
            if (modifier == null)
                return true;
            writer.WriteByte(0x35);
            writer.WriteUInt16(modifiersComponentId);
            writer.WriteByte(0x00);
            WriteGCType(writer, modifier.GCType, preserveCase: true);
            writer.WriteUInt32(modifier.Id);
            writer.WriteByte(modifier.Level);
            writer.WriteUInt32(modifier.PowerLevel);
            writer.WriteUInt32(modifier.Duration);
            writer.WriteByte(modifier.SourceIsSelf);
            if (!TryWriteRemoteAvatarEntitySynchInfo(sourceConn, writer, modifiersComponentId, 0x00, "MP-SPAWN-ZONE-INVULN"))
                return false;
            Debug.LogError($"[MULTIPLAYER-ZONE-INVULN] source={sourceConn.LoginName} component={modifiersComponentId} id={modifier.Id} duration={modifier.Duration} phase=after-root-before-first-behavior-update sourceFunction=Modifiers::processAddModifier@0x00502280");
            return true;
        }

        private void ClearRemoteAvatarResourceState(string loginName)
        {
            if (string.IsNullOrEmpty(loginName)) return;
            string prefix = loginName + "\u001f";
            string suffix = "\u001f" + loginName;
            var keys = _remoteAvatarResourceState.Keys
                .Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (string key in keys)
                _remoteAvatarResourceState.Remove(key);
        }

        private readonly Dictionary<string, uint> _remoteAvatarHPSent = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

        private void SendRemoteAvatarHPUpdates(RRConnection sourceConn)
        {
            if (sourceConn == null || !sourceConn.IsSpawned || string.IsNullOrEmpty(sourceConn.LoginName)) return;
            PlayerState state = GetPlayerState(sourceConn.ConnId.ToString());
            if (state == null) return;
            uint hpWire = state.EntitySynchInfoHP;
            foreach (var viewerConn in GetConnectionInsertionOrderSnapshot())
            {
                if (viewerConn == sourceConn || !viewerConn.IsSpawned || !viewerConn.AllowFlush) continue;
                if (viewerConn.InstanceId != sourceConn.InstanceId) continue;
                if (!string.Equals(viewerConn.CurrentZoneGcType, sourceConn.CurrentZoneGcType, StringComparison.OrdinalIgnoreCase)) continue;
                if (!_remoteBehaviorIds.TryGetValue(viewerConn.LoginName, out var behaviorMap)) continue;
                if (!behaviorMap.TryGetValue(sourceConn.LoginName, out ushort remoteBehaviorId) || remoteBehaviorId == 0) continue;
                string key = RemoteAvatarResourceKey(viewerConn.LoginName, sourceConn.LoginName);
                if (_remoteAvatarHPSent.TryGetValue(key, out uint lastHp) && lastHp == hpWire) continue;
                var writer = new LEWriter();
                writer.WriteByte(0x35);
                writer.WriteUInt16(remoteBehaviorId);
                writer.WriteByte(0x65);
                writer.WriteByte(0xFF);
                writer.WriteByte(0x00);
                if (!TryWriteRemoteAvatarEntitySynchInfo(sourceConn, writer, remoteBehaviorId, 0x65, "MP-HP"))
                    continue;
                viewerConn.MessageQueue.Enqueue(writer.ToArray());
                _remoteAvatarHPSent[key] = hpWire;
            }
        }

        private byte[] BuildOtherPlayerSpawnPacket(RRConnection playerConn, uint avatarEntityId, RRConnection viewerConn)
        {
            if (!TryResolvePlayerEntitySynchInfoHP(playerConn, "MP-SPAWN", out uint remoteHPWire))
                return null;

            PlayerState remoteState = GetPlayerState(playerConn.ConnId.ToString());
            if (remoteState == null)
                return null;
            uint remoteManaWire = Math.Min(remoteState.CurrentManaWire, remoteState.MaxManaWire);
            var writer = new LEWriter();
            ResolveRemoteAvatarWorldPosition(playerConn, out int posX, out int posY, out int posZ, out int heading);

            EnsureReplicaEntityIds(playerConn);
            avatarEntityId = playerConn.ReplicaAvatarId;
            ushort remoteBehaviorId = playerConn.ReplicaBehaviorId;
            ushort remoteSkillsId = playerConn.ReplicaSkillsId;
            ushort remoteManipId = playerConn.ReplicaManipId;
            ushort remoteModId = playerConn.ReplicaModId;
            ushort remotePlayerId = playerConn.ReplicaPlayerId;

            SavedCharacter remoteChar = GetActiveCharacter(playerConn);

            writer.WriteByte(0x07);

            writer.WriteByte(0x01);
            writer.WriteUInt16((ushort)avatarEntityId);
            WriteGCType(writer, playerConn.AvatarGcType, preserveCase: true);

            writer.WriteByte(0x01);
            writer.WriteUInt16(remotePlayerId);
            WriteGCType(writer, "player");

            writer.WriteByte(0x02);
            writer.WriteUInt16(remotePlayerId);
            writer.WriteCString(remoteChar?.name ?? playerConn.LoginName);
            writer.WriteUInt32(0x00);
            writer.WriteUInt32(GroupDirectory.Instance.GetGroupForConn(playerConn.ConnId)?.GroupId ?? 0);
            byte remoteMembership;
            if (IsPlayerAdmin(playerConn.LoginName))
                remoteMembership = 0x00;
            else if (IsPlayerFree(playerConn.LoginName))
                remoteMembership = 0x02;
            else
                remoteMembership = 0x01;
            writer.WriteByte(remoteMembership);
            writer.WriteUInt32(GetCharSqlId(playerConn));
            WritePlayerPvpFields(writer, remoteChar);
            var remotePvpMatch = Gameplay.PVPMatchmaking.Instance.GetMatchForPlayer(playerConn.LoginName);
            if (remotePvpMatch != null)
            {
                string remoteTeamPath = Gameplay.PVPMatchmaking.TeamGcPath(remotePvpMatch.Archetype, remotePvpMatch.IsRed(playerConn.LoginName));
                writer.WriteByte(0xFF); writer.WriteCString(remoteTeamPath);
            }
            else
            {
                writer.WriteByte(0x00);
            }
            writer.WriteCString(remoteChar?.posseName ?? "");
            writer.WriteInt32(remoteChar?.minimumItemQuality ?? 1);

            writer.WriteByte(0x32);
            writer.WriteUInt16((ushort)avatarEntityId);
            writer.WriteUInt16(remoteSkillsId);
            WriteGCType(writer, "skills", preserveCase: false);
            writer.WriteByte(0x01);
            writer.WriteByte(0xFF); writer.WriteByte(0xFF);
            writer.WriteByte(0xFF); writer.WriteByte(0xFF);
            writer.WriteByte(0x00);
            writer.WriteByte(0x01);
            string profession = (playerConn.ClassName?.ToLower()) switch
            {
                "mage" or "warlock" => "skills.professions.Warlock",
                "ranger" => "skills.professions.Ranger",
                _ => "skills.professions.Warrior"
            };
            WriteGCType(writer, profession, preserveCase: true);

            writer.WriteByte(0x32);
            writer.WriteUInt16((ushort)avatarEntityId);
            writer.WriteUInt16(remoteManipId);
            WriteGCType(writer, "manipulators", preserveCase: false);
            writer.WriteByte(0x01);

            var skillItems = new List<GCObject>();
            var equipmentItems = new List<GCObject>();
            var remotePassives = (remoteChar != null && !IsPvpZone(playerConn.CurrentZoneName))
                ? CollectPassiveManipulators(remoteChar)
                : new List<PassiveManipulator>();
            string playerConnectionKey = playerConn.ConnId.ToString();
            if (playerConn.Avatar != null)
            {
                var manipulatorsComponent = playerConn.Avatar.Children?.FirstOrDefault(c =>
                    c.DFCClass == "Manipulators" || c.GCClass == "Manipulators");
                if (manipulatorsComponent?.Children != null)
                {
                    foreach (var child in manipulatorsComponent.Children)
                    {
                        bool isEquipment = (child.DFCClass == "Armor" || child.DFCClass == "Item" ||
                                       child.DFCClass == "MeleeWeapon" || child.DFCClass == "RangedWeapon");
                        if (isEquipment && !string.IsNullOrEmpty(child.GCClass))
                        {
                            var itemData = AuthoredGameplayCatalog.FindItem(child.GCClass);
                            var generalItem = itemData == null ? AuthoredGameplayCatalog.FindGeneralItem(child.GCClass) : null;
                            if (itemData != null || generalItem != null)
                            {
                                equipmentItems.Add(child);
                            }
                            else
                            {
                                string merchantLookupKey = child.GCClass.ToLowerInvariant();
                                if (merchantLookupKey.StartsWith("items.pal.")) merchantLookupKey = merchantLookupKey.Substring(10);
                                if (DungeonRunners.Gameplay.MerchantRuntime.HasAuthoredMerchantModSlots(merchantLookupKey))
                                    equipmentItems.Add(child);
                            }
                        }
                        else if (!isEquipment && !string.IsNullOrEmpty(child.GCClass) &&
                                 !child.GCClass.StartsWith("skills.professions.", StringComparison.OrdinalIgnoreCase) &&
                                 !IsPassiveSkill(child.GCClass))
                        {
                            skillItems.Add(child);
                        }
                    }
                }
            }

            writer.WriteByte((byte)(skillItems.Count + remotePassives.Count + equipmentItems.Count));

            uint remoteSlotCounter = 200;
            foreach (var skill in skillItems)
            {
                uint slotId = remoteSlotCounter++;
                uint? trackedSlotId = FindPlayerManipulatorSlot(playerConnectionKey, skill.GCClass, uint.MaxValue);
                if (trackedSlotId.HasValue)
                    slotId = trackedSlotId.Value;
                byte remoteSkillLevel = (byte)(remoteChar?.GetSkillLevel(skill.GCClass) ?? 1);
                WriteActiveSkillManipulatorChild(writer, skill.GCClass, slotId, remoteSkillLevel);
            }

            foreach (var passive in remotePassives)
                WritePassiveManipulatorChild(writer, passive);

            foreach (var item in equipmentItems)
            {
                uint equipmentSlot = item.TargetSlot ?? item.GetEquipmentSlotFromGCClass();
                int equipmentLevel = item.StoredLevel >= 0 ? item.StoredLevel : Math.Max(1, item.GetItemRequiredLevel());
                WriteEquipmentManipulatorChild(writer, item, equipmentSlot, equipmentLevel);
            }
            Debug.LogError($"[MULTIPLAYER-MANIP] {skillItems.Count} skills + {remotePassives.Count} passives + {equipmentItems.Count} equipment for {playerConn.LoginName}");

            writer.WriteByte(0x32);
            writer.WriteUInt16((ushort)avatarEntityId);
            writer.WriteUInt16(remoteModId);
            WriteGCType(writer, "modifiers", preserveCase: false);
            writer.WriteByte(0x01);
            WritePassiveModifiersComponent(writer, null, (ushort)avatarEntityId);

            writer.WriteByte(0x02);
            writer.WriteUInt16((ushort)avatarEntityId);
            WriteRemoteAvatarInitPayload(writer, remoteState, remotePlayerId, remoteHPWire, remoteManaWire, remoteChar, posX, posY, posZ, heading);

            writer.WriteByte(0x32);
            writer.WriteUInt16((ushort)avatarEntityId);
            writer.WriteUInt16(remoteBehaviorId);
            WriteGCType(writer, "avatar.base.UnitBehavior");
            writer.WriteByte(0x01);

            writer.WriteByte(0xFF);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);

            writer.WriteByte(0xFF);
            writer.WriteByte(0x00);
            writer.WriteInt32(heading);
            writer.WriteInt32(heading);

            writer.WriteByte(0x00);

            writer.WriteByte(0xFF);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);

            if (!WriteRemotePassiveModifierAdds(writer, playerConn, remoteModId, remotePassives))
                return null;
            if (!WriteRemoteZoneSpawnInvulnerabilityAdd(writer, playerConn, remoteModId))
                return null;

            writer.WriteByte(0x35);
            writer.WriteUInt16(remoteBehaviorId);
            writer.WriteByte(0x04);
            writer.WriteByte(0x04);
            writer.WriteByte(0xFF);
            writer.WriteInt32(posX);
            writer.WriteInt32(posY);
            writer.WriteInt32(posZ);
            writer.WriteUInt16((ushort)avatarEntityId);
            GetValidationCutoff(out uint spawnCutoffTick);
            if (!TryWriteResolvedEntitySynchInfo(writer, remoteBehaviorId, 0x04, EntitySynchInfoContext.PlayerActionResponse, "MP-SPAWN-ACTION", EntitySynchInfoDecision.HP(EntitySynchInfoOwner.Avatar, remoteHPWire, $"MP-SPAWN-ACTION source={playerConn.LoginName}", remoteBehaviorId, remoteBehaviorId, 0x04, $"remote-spawn-action; validationCutoffTick={spawnCutoffTick}", spawnCutoffTick)))
                return null;

            writer.WriteByte(0x06);

            if (!_remoteBehaviorIds.ContainsKey(viewerConn.LoginName))
                _remoteBehaviorIds[viewerConn.LoginName] = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
            _remoteBehaviorIds[viewerConn.LoginName][playerConn.LoginName] = remoteBehaviorId;
            SetRemoteBehaviorSession(viewerConn, playerConn, 0xFF);
            MarkPeerBehaviorInitialized(viewerConn, playerConn);
            if (!_remoteAvatarIds.ContainsKey(viewerConn.LoginName))
                _remoteAvatarIds[viewerConn.LoginName] = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
            _remoteAvatarIds[viewerConn.LoginName][playerConn.LoginName] = (ushort)avatarEntityId;
            if (!_remotePlayerIds.ContainsKey(viewerConn.LoginName))
                _remotePlayerIds[viewerConn.LoginName] = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
            _remotePlayerIds[viewerConn.LoginName][playerConn.LoginName] = remotePlayerId;
            _remoteAvatarResourceState[RemoteAvatarResourceKey(viewerConn.LoginName, playerConn.LoginName)] = ((ushort)avatarEntityId, remoteHPWire, remoteManaWire);
            Debug.LogError($"[MULTIPLAYER] Built avatar spawn for {playerConn.LoginName}: {writer.Position} bytes (avatar.base.UnitBehavior)");
            return writer.ToArray();
        }

        private void BroadcastEntityRemove(RRConnection leavingConn, string zoneGcType, bool queueForViewers = false)
        {
            Debug.LogError($"[MULTIPLAYER] Player {leavingConn.LoginName} leaving zone {zoneGcType}");

            foreach (RRConnection other in GetConnectionInsertionOrderSnapshot())
            {
                if (other == leavingConn) continue;
                if (!other.IsSpawned) continue;
                if (_remoteAvatarIds.TryGetValue(other.LoginName, out var avatarMap) &&
                    avatarMap.TryGetValue(leavingConn.LoginName, out ushort avatarEntityId))
                {
                    if (_remoteBehaviorIds.TryGetValue(other.LoginName, out var preRemoveBehaviorMap) &&
                        preRemoveBehaviorMap.TryGetValue(leavingConn.LoginName, out ushort remoteBehaviorId) &&
                        remoteBehaviorId != 0)
                    {
                        int purged = other.MessageQueue.RemoveComponentUpdates(remoteBehaviorId);
                        if (purged > 0)
                            Debug.LogError($"[MULTIPLAYER] Purged {purged} queued updates for remote behavior 0x{remoteBehaviorId:X4} before despawn owner={leavingConn.LoginName} viewer={other.LoginName}");
                    }

                    var writer = new LEWriter();
                    writer.WriteByte(0x07);
                    writer.WriteByte(0x05);
                    writer.WriteUInt16(avatarEntityId);
                    if (_remotePlayerIds.TryGetValue(other.LoginName, out var playerIdMap) &&
                        playerIdMap.TryGetValue(leavingConn.LoginName, out ushort playerEntityId))
                    {
                        writer.WriteByte(0x05);
                        writer.WriteUInt16(playerEntityId);
                        playerIdMap.Remove(leavingConn.LoginName);
                    }
                    writer.WriteByte(0x06);
                    if (queueForViewers)
                        other.MessageQueue.Enqueue(writer.ToArray());
                    else
                        SendToClient(other, writer.ToArray());
                    Debug.LogError($"[MULTIPLAYER] {(queueForViewers ? "Queued" : "Sent")} entity despawn (0x05) for avatar {avatarEntityId} to {other.LoginName}");
                    _remoteAvatarResourceState.Remove(RemoteAvatarResourceKey(other.LoginName, leavingConn.LoginName));
                    avatarMap.Remove(leavingConn.LoginName);
                }

                if (_remoteBehaviorIds.TryGetValue(other.LoginName, out var behaviorMap))
                    behaviorMap.Remove(leavingConn.LoginName);
            }

            if (leavingConn.IsConnected && _remoteAvatarIds.TryGetValue(leavingConn.LoginName, out var leaverViewMap) && leaverViewMap.Count > 0)
            {
                _remotePlayerIds.TryGetValue(leavingConn.LoginName, out var leaverPlayerMap);
                foreach (RRConnection viewedOwner in GetConnectionInsertionOrderSnapshot())
                {
                    if (viewedOwner == null
                        || string.IsNullOrEmpty(viewedOwner.LoginName)
                        || !leaverViewMap.TryGetValue(viewedOwner.LoginName, out ushort viewedAvatarId))
                        continue;
                    if (_remoteBehaviorIds.TryGetValue(leavingConn.LoginName, out var leaverBehaviorMap) &&
                        leaverBehaviorMap.TryGetValue(viewedOwner.LoginName, out ushort viewedBehaviorId) &&
                        viewedBehaviorId != 0)
                    {
                        int purged = leavingConn.MessageQueue.RemoveComponentUpdates(viewedBehaviorId);
                        if (purged > 0)
                            Debug.LogError($"[MULTIPLAYER] Purged {purged} queued updates for viewed behavior 0x{viewedBehaviorId:X4} before despawn viewer={leavingConn.LoginName} owner={viewedOwner.LoginName}");
                    }

                    var writer = new LEWriter();
                    writer.WriteByte(0x07);
                    writer.WriteByte(0x05);
                    writer.WriteUInt16(viewedAvatarId);
                    if (leaverPlayerMap != null && leaverPlayerMap.TryGetValue(viewedOwner.LoginName, out ushort viewedPlayerId))
                    {
                        writer.WriteByte(0x05);
                        writer.WriteUInt16(viewedPlayerId);
                    }
                    writer.WriteByte(0x06);
                    SendToClient(leavingConn, writer.ToArray());
                    Debug.LogError($"[MULTIPLAYER] Sent entity despawn (0x05) for avatar {viewedAvatarId} to leaver {leavingConn.LoginName} (owner {viewedOwner.LoginName})");
                    _remoteAvatarResourceState.Remove(RemoteAvatarResourceKey(leavingConn.LoginName, viewedOwner.LoginName));
                }
            }

            _remoteBehaviorIds.Remove(leavingConn.LoginName);
            _remoteAvatarIds.Remove(leavingConn.LoginName);
            _remotePlayerIds.Remove(leavingConn.LoginName);
            _pvpControllerReady.Remove(leavingConn.LoginName);
            _pvpControllerIdByLogin.Remove(leavingConn.LoginName);
            RemoveOrderedValue(_pvpGateDropDue, _pvpGateDropOrder, leavingConn.LoginName);
            RemoveOrderedValue(_pvpCombatReminders, _pvpCombatReminderOrder, leavingConn.LoginName);
            leavingConn.ReplicaAvatarId = 0;
            leavingConn.ReplicaBehaviorId = 0;
            leavingConn.ReplicaPlayerId = 0;
            leavingConn.ReplicaSkillsId = 0;
            leavingConn.ReplicaManipId = 0;
            leavingConn.ReplicaModId = 0;
            ClearRemoteAvatarResourceState(leavingConn.LoginName);

            string sessionPrefix = leavingConn.LoginName + "\u001f";
            string sessionSuffix = "\u001f" + leavingConn.LoginName;
            for (int keyIndex = _remoteSessionOrder.Count - 1; keyIndex >= 0; keyIndex--)
            {
                string key = _remoteSessionOrder[keyIndex];
                if (key.StartsWith(sessionPrefix, StringComparison.OrdinalIgnoreCase) || key.EndsWith(sessionSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    _remoteSessionIds.Remove(key);
                    _remoteSessionOrder.RemoveAt(keyIndex);
                }
            }
            leavingConn.LastRawMoveData = null;
            leavingConn.LastRawMoveCount = 0;
            leavingConn.LivePlayerMovingThisFrame = false;
            ResetReflectedAvatarPosition(leavingConn);
        }

        private Dictionary<string, byte> _remoteSessionIds = new Dictionary<string, byte>();
        private readonly List<string> _remoteSessionOrder = new List<string>();

        private string ResolveCharacterName(RRConnection conn)
        {
            string name = conn?.LoginName ?? "Unknown";
            if (conn?.LoginName != null && _selectedCharacter.TryGetValue(conn.LoginName, out var ch))
                name = ch?.Name ?? name;
            return name;
        }

        private static byte[] BuildChatMessagePacket(byte dispChannel, string senderName, string message)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x06);
            writer.WriteByte(0x00);
            writer.WriteByte(dispChannel);
            if (dispChannel == 0x02 || dispChannel == 0x03 || dispChannel == 0x04 || dispChannel == 0x05
                || dispChannel == 0x0B || dispChannel == 0x0C || dispChannel == 0x10)
                writer.WriteByte(0x00);
            if (dispChannel != 0x0D)
                writer.WriteCString(senderName ?? "");
            writer.WriteCString(message ?? "");
            return writer.ToArray();
        }

        private void SendChatMessage(RRConnection conn, byte dispChannel, string senderName, string message)
        {
            SendCompressedA(conn, 0x01, 0x0F, BuildChatMessagePacket(dispChannel, senderName, message));
        }

        private void BroadcastChatMessage(byte dispChannel, string senderName, string message, string zoneName)
        {
            byte[] chatPacket = BuildChatMessagePacket(dispChannel, senderName, message);
            foreach (var other in GetConnectionInsertionOrderSnapshot())
            {
                if (!other.IsSpawned) continue;
                if (zoneName != null && !string.Equals(other.CurrentZoneName, zoneName, StringComparison.OrdinalIgnoreCase)) continue;
                SendCompressedA(other, 0x01, 0x0F, chatPacket);
            }
        }

        private RRConnection FindConnectionByCharacterName(string characterName)
        {
            if (string.IsNullOrWhiteSpace(characterName)) return null;
            foreach (var other in GetConnectionInsertionOrderSnapshot())
            {
                if (string.IsNullOrEmpty(other?.LoginName)) continue;
                if (_selectedCharacter.TryGetValue(other.LoginName, out var ch) && ch?.Name != null
                    && string.Equals(ch.Name, characterName, StringComparison.OrdinalIgnoreCase))
                    return other;
            }
            return null;
        }

        private void HandleChatMessage(RRConnection conn, byte chatChannel, string text)
        {
            string senderName = ResolveCharacterName(conn);
            switch (chatChannel)
            {
                case 1:
                    BroadcastChatMessage(0x02, senderName, text, null);
                    break;
                case 2:
                    BroadcastChatMessage(0x03, senderName, text, conn.CurrentZoneName);
                    break;
                case 3:
                {
                    var group = GroupDirectory.Instance.GetGroupForConn(conn.ConnId);
                    if (group == null)
                    {
                        SendChatMessage(conn, 0x0A, senderName, text);
                        break;
                    }
                    foreach (var member in group.Members)
                    {
                        if (!member.IsOnline) continue;
                        if (_connections.TryGetValue(member.ConnId, out var memberConn) && memberConn.IsSpawned)
                            SendChatMessage(memberConn, 0x04, senderName, text);
                    }
                    break;
                }
                case 4:
                {
                    int split = text.IndexOf(' ');
                    string targetName = split > 0 ? text.Substring(0, split) : text;
                    string message = split > 0 ? text.Substring(split + 1).Trim() : "";
                    RRConnection target = FindConnectionByCharacterName(targetName);
                    if (target == null || !target.IsSpawned || message.Length == 0)
                    {
                        SendChatMessage(conn, 0x0A, targetName, message.Length == 0 ? text : message);
                        break;
                    }
                    SendChatMessage(target, 0x05, senderName, message);
                    SendChatMessage(conn, 0x06, ResolveCharacterName(target), message);
                    break;
                }
                case 5:
                    BroadcastChatMessage(0x0B, senderName, text, null);
                    break;
                case 6:
                    BroadcastChatMessage(0x0C, senderName, text, null);
                    break;
                case 7:
                    BroadcastChatMessage(0x10, senderName, text, null);
                    break;
                default:
                    Debug.LogError($"[CHAT-CH6] unhandled chatChannel={chatChannel} sender={senderName} sourceFunction=ChatClient::sendChatMessage@0x5FFCA0");
                    break;
            }
        }

        private void BroadcastPlayerDeath(RRConnection deadConn)
        {
            Debug.LogError($"[MP-DEATH] from {deadConn.LoginName} zone='{deadConn.CurrentZoneGcType}'");
            foreach (RRConnection other in GetConnectionInsertionOrderSnapshot())
            {
                if (other == deadConn) continue;
                if (!other.IsSpawned) continue;
                if (other.CurrentZoneGcType != deadConn.CurrentZoneGcType) continue;
                if (other.InstanceId != deadConn.InstanceId) continue;

                if (!_remoteBehaviorIds.TryGetValue(other.LoginName, out var behaviorMap)) continue;
                if (!behaviorMap.TryGetValue(deadConn.LoginName, out ushort remoteBehaviorId)) continue;

                var remoteDeathMessage = new LEWriter();
                remoteDeathMessage.WriteByte(0x07);
                remoteDeathMessage.WriteByte(0x36);
                remoteDeathMessage.WriteUInt16(remoteBehaviorId);
                GetValidationCutoff(out uint deathCutoffTick);
                if (!TryWriteResolvedEntitySynchInfo(remoteDeathMessage, remoteBehaviorId, 0x00, EntitySynchInfoContext.PlayerActionResponse, "MP-DEATH", EntitySynchInfoDecision.HP(EntitySynchInfoOwner.Avatar, 0, $"MP-DEATH source={deadConn.LoginName}", remoteBehaviorId, remoteBehaviorId, 0x00, $"remote-death; validationCutoffTick={deathCutoffTick}", deathCutoffTick)))
                    continue;
                remoteDeathMessage.WriteByte(0x06);
                SendToClient(other, remoteDeathMessage.ToArray());
                Debug.LogError($"[MULTIPLAYER-DEATH] Sent EntitySynchInfo HP=0 to {other.LoginName} for {deadConn.LoginName}");
            }
        }

        private bool TryPrepareProceduralDungeonSnapshot(RRConnection conn, out DungeonMazeSpawner.ProceduralDungeonSnapshot snapshot)
        {
            snapshot = null;
            if (conn == null || string.IsNullOrEmpty(conn.CurrentZoneName))
                return false;
            if (!DungeonMazeSpawner.IsProceduralZone(conn.CurrentZoneName))
                return false;

            string zoneName = conn.CurrentZoneName;
            string instanceKey = GetInstanceZoneKey(conn);
            uint layoutSeed = ResolveZoneLayoutSeed(conn, zoneName);
            uint roomSeed = ResolveRuntimeZoneSeed(conn, zoneName);
            snapshot = ZoneSpawner.Instance.GetOrCreateProceduralSnapshot(zoneName, instanceKey, layoutSeed, roomSeed);
            return snapshot != null;
        }

        private bool TryGetProceduralDungeonSnapshot(RRConnection conn, out DungeonMazeSpawner.ProceduralDungeonSnapshot snapshot)
        {
            snapshot = null;
            if (conn == null || string.IsNullOrEmpty(conn.CurrentZoneName))
                return false;
            if (!DungeonMazeSpawner.IsProceduralZone(conn.CurrentZoneName))
                return false;

            string instanceKey = GetInstanceZoneKey(conn);
            if (ZoneSpawner.Instance.TryGetProceduralSnapshot(instanceKey, out snapshot))
                return true;

            return TryPrepareProceduralDungeonSnapshot(conn, out snapshot);
        }

        private (int x, int y, int z) ResolvePlayerSpawnPositionFixed(RRConnection conn)
        {
            int spawnFixedX;
            int spawnFixedY;
            int spawnFixedZ;
            bool proceduralSnapshotSpawn = false;
            bool persistedPositionSpawn = false;
            bool preserveAuthoredPosition = conn.PendingSpawnPreserveAuthoredPosition;
            conn.PendingSpawnPreserveAuthoredPosition = false;
            bool resumePersistedPosition = conn.PendingSavedPositionResume;
            conn.PendingSavedPositionResume = false;

            if (resumePersistedPosition)
            {
                SavedCharacter persistedCharacter = GetActiveCharacter(conn);
                PathMap resumePathMap = DungeonRunners.Core.PathMapCatalog.Instance.GetPathMap(conn.CurrentZoneName ?? "");
                if (persistedCharacter != null
                    && IsPublicZone(conn.CurrentZoneName)
                    && conn.InstanceId == 0
                    && string.Equals(persistedCharacter.currentZoneName, conn.CurrentZoneName, StringComparison.OrdinalIgnoreCase)
                    && resumePathMap != null
                    && resumePathMap.IsWalkableFixed(persistedCharacter.positionFixedX, persistedCharacter.positionFixedY))
                {
                    spawnFixedX = persistedCharacter.positionFixedX;
                    spawnFixedY = persistedCharacter.positionFixedY;
                    spawnFixedZ = persistedCharacter.positionFixedZ;
                    persistedPositionSpawn = true;
                    Debug.LogError($"[SPAWN] Using persisted public-zone position characterId={persistedCharacter.id} zone='{conn.CurrentZoneName}' instance={conn.InstanceId} fixed=({spawnFixedX},{spawnFixedY},{spawnFixedZ}) pathMap=walkable");
                }
                else
                {
                    resumePersistedPosition = false;
                    Debug.LogError($"[SPAWN] Persisted position rejected characterId={persistedCharacter?.id ?? 0} zone='{conn.CurrentZoneName ?? ""}' instance={conn.InstanceId} pathMap={(resumePathMap == null ? "missing" : "not-walkable")}");
                    spawnFixedX = 0;
                    spawnFixedY = 0;
                    spawnFixedZ = 0;
                }
            }
            else
            {
                spawnFixedX = 0;
                spawnFixedY = 0;
                spawnFixedZ = 0;
            }

            if (!persistedPositionSpawn && TryPrepareProceduralDungeonSnapshot(conn, out var dungeonSnapshot))
            {
                proceduralSnapshotSpawn = true;
                string pendingSpawnPoint = conn.PendingSpawnPoint ?? "";
                if (DungeonMazeSpawner.TryResolveSpawnPointFixed(
                    dungeonSnapshot,
                    pendingSpawnPoint,
                    out spawnFixedX,
                    out spawnFixedY,
                    out spawnFixedZ,
                    out int resolvedHeadingFixed,
                    out int resolvedSourceIndex,
                    out string resolvedTileType,
                    out int resolvedGridX,
                    out int resolvedGridY,
                    out int resolvedLocalFixedX,
                    out int resolvedLocalFixedY,
                    out int resolvedLocalFixedZ,
                    out string resolvedSource))
                {
                    conn.PlayerHeadingFixed = resolvedHeadingFixed;
                    Debug.LogError($"[SPAWN] Procedural snapshot named spawnPoint='{pendingSpawnPoint}' zone={dungeonSnapshot.ZoneName} layoutSeed=0x{dungeonSnapshot.LayoutSeed:X8} roomSeed=0x{dungeonSnapshot.RoomSeed:X8} playerFixed=({spawnFixedX},{spawnFixedY},{spawnFixedZ}) headingFixed={conn.PlayerHeadingFixed} src={resolvedSourceIndex} tile='{resolvedTileType}' grid=({resolvedGridX},{resolvedGridY}) localFixed=({resolvedLocalFixedX},{resolvedLocalFixedY},{resolvedLocalFixedZ}) source='{resolvedSource}'");
                }
                else
                {
                    spawnFixedX = dungeonSnapshot.PlayerSpawnFixedX;
                    spawnFixedY = dungeonSnapshot.PlayerSpawnFixedY;
                    spawnFixedZ = dungeonSnapshot.PlayerSpawnFixedZ;
                    conn.PlayerHeadingFixed = dungeonSnapshot.PlayerHeadingFixed;
                    Debug.LogError($"[SPAWN] Procedural snapshot default entry spawnPoint='{pendingSpawnPoint}' zone={dungeonSnapshot.ZoneName} layoutSeed=0x{dungeonSnapshot.LayoutSeed:X8} roomSeed=0x{dungeonSnapshot.RoomSeed:X8} playerFixed=({spawnFixedX},{spawnFixedY},{spawnFixedZ}) headingFixed={conn.PlayerHeadingFixed} entry=({dungeonSnapshot.EntryGridX},{dungeonSnapshot.EntryGridY}) src={dungeonSnapshot.EntrySourceIndex} tile='{dungeonSnapshot.EntryTileType}' walkable={dungeonSnapshot.PlayerAnchorWalkable}/{dungeonSnapshot.EntryPortalAnchorWalkable} yTransform=worldGridY=gridY/BuildWorld source='{dungeonSnapshot.PlayerAnchorSource}'");
                }
                conn.PendingSpawnFixedX = 0;
                conn.PendingSpawnFixedY = 0;
                conn.PendingSpawnFixedZ = 0;
                conn.PendingSpawnPoint = "";
            }
            else if (!persistedPositionSpawn && (conn.PendingSpawnFixedX != 0 || conn.PendingSpawnFixedY != 0 || conn.PendingSpawnFixedZ != 0))
            {
                spawnFixedX = conn.PendingSpawnFixedX;
                spawnFixedY = conn.PendingSpawnFixedY;
                spawnFixedZ = conn.PendingSpawnFixedZ;
                conn.PendingSpawnFixedX = 0;
                conn.PendingSpawnFixedY = 0;
                conn.PendingSpawnFixedZ = 0;
                Debug.LogError($"[SPAWN] Using CHECKPOINT positionFixed=({spawnFixedX},{spawnFixedY},{spawnFixedZ})");
            }
            else if (!persistedPositionSpawn && !string.IsNullOrEmpty(conn.PendingSpawnPoint)
                     && AuthoredGameplayCatalog.GetWaypointsForZone(conn.CurrentZoneName ?? "")
                            .FirstOrDefault(w => w.name != null && w.name.Equals(conn.PendingSpawnPoint, StringComparison.OrdinalIgnoreCase)) is { } namedWp)
            {
                spawnFixedX = namedWp.PosFixedX;
                spawnFixedY = namedWp.PosFixedY;
                spawnFixedZ = namedWp.PosFixedZ;
                conn.PlayerHeadingFixed = namedWp.HeadingFixed;
                Debug.LogError($"[SPAWN] Named waypoint '{namedWp.name}' in {conn.CurrentZoneName}: fixed=({spawnFixedX},{spawnFixedY},{spawnFixedZ}) headingFixed={conn.PlayerHeadingFixed}");
                conn.PendingSpawnPoint = "";
            }
            else if (!persistedPositionSpawn && _zones.TryGetValue(conn.CurrentZoneId, out Zone currentZone) && (currentZone.SpawnFixedX != 0 || currentZone.SpawnFixedY != 0))
            {
                spawnFixedX = currentZone.SpawnFixedX;
                spawnFixedY = currentZone.SpawnFixedY;
                spawnFixedZ = currentZone.SpawnFixedZ;
                Debug.LogError($"[SPAWN] Zone '{currentZone.name}' spawnFixed=({spawnFixedX},{spawnFixedY},{spawnFixedZ})");
            }
            else if (!persistedPositionSpawn)
            {
                throw new InvalidDataException($"Zone '{conn.CurrentZoneName ?? ""}' ({conn.CurrentZoneId:X8}) has no authored spawn point");
            }

            string pmZone = conn.CurrentZoneName ?? "";
            var pathMap = DungeonRunners.Core.PathMapCatalog.Instance.GetPathMap(pmZone);
            if (pathMap != null && !proceduralSnapshotSpawn && !preserveAuthoredPosition && !persistedPositionSpawn)
            {
                if (pathMap.IsWalkableFixed(spawnFixedX, spawnFixedY))
                {
                    int terrainFixedZ = ResolveZoneGroundHeightFixed(pmZone, null, pathMap, spawnFixedX, spawnFixedY, spawnFixedZ);
                    Debug.LogError($"[SPAWN] PathMap height correction fixedZ {spawnFixedZ}->{terrainFixedZ}");
                    spawnFixedZ = terrainFixedZ;
                }
                else
                {
                    Debug.LogError($"[SPAWN] PathMap says fixed=({spawnFixedX},{spawnFixedY}) is not walkable; retaining fixedZ={spawnFixedZ}");
                }
            }
            else if (pathMap != null && preserveAuthoredPosition)
            {
                if (!pathMap.IsWalkableFixed(spawnFixedX, spawnFixedY))
                    throw new InvalidDataException($"Authored respawn point in zone '{pmZone}' is not walkable at fixed ({spawnFixedX},{spawnFixedY})");
                if (RespawnTracking)
                    Debug.LogError($"[RESPAWN-TRACK] phase=spawn-resolve characterId={GetCharSqlId(conn)} targetZone='{pmZone}' spawnPoint=authored fixed=({spawnFixedX},{spawnFixedY},{spawnFixedZ}) headingFixed={conn.PlayerHeadingFixed} pathMap=walkable preserveExact=true");
            }
            else if (preserveAuthoredPosition && !proceduralSnapshotSpawn)
            {
                throw new InvalidDataException($"Authored respawn point in zone '{pmZone}' has no pathmap");
            }
            else if (proceduralSnapshotSpawn)
            {
                Debug.LogError($"[SPAWN] Procedural snapshot raw anchor kept: no PathMap height correction fixed=({spawnFixedX},{spawnFixedY},{spawnFixedZ})");
            }

            int playersInZone = 0;
            foreach (var other in GetConnectionInsertionOrderSnapshot())
            {
                if (other == conn) continue;
                if (!other.IsSpawned) continue;
                if (other.CurrentZoneGcType == conn.CurrentZoneGcType && other.InstanceId == conn.InstanceId)
                    playersInZone++;
            }
            if (playersInZone > 0 && !proceduralSnapshotSpawn && !persistedPositionSpawn)
            {
                int angleDegrees = (playersInZone * 72) % 360;
                spawnFixedX += UnitMover.ZRotateCosFixed(angleDegrees) * 5;
                spawnFixedY += UnitMover.ZRotateSinFixed(angleDegrees) * 5;
                Debug.LogError($"[SPAWN] Offset for player #{playersInZone + 1}: fixed=({spawnFixedX},{spawnFixedY})");
            }
            else if (playersInZone > 0)
            {
                Debug.LogError($"[SPAWN] Procedural snapshot keeps authored spawn with {playersInZone} existing players in instance");
            }

            return (spawnFixedX, spawnFixedY, spawnFixedZ);
        }

        private void QueueRemoteClientControl(RRConnection viewer, RRConnection source, int delayFlushes = 0)
        {
            if (viewer == null || source == null || string.IsNullOrEmpty(viewer.LoginName)) return;
            if (!_remoteBehaviorIds.TryGetValue(viewer.LoginName, out var behaviorMap)
                || !behaviorMap.TryGetValue(source.LoginName, out ushort remoteBehaviorId)
                || remoteBehaviorId == 0) return;
            var writer = new LEWriter();
            writer.WriteByte(0x35);
            writer.WriteUInt16(remoteBehaviorId);
            writer.WriteByte(0x64);
            writer.WriteByte(0x01);
            if (!TryWriteRemoteAvatarEntitySynchInfo(source, writer, remoteBehaviorId, 0x64, "MP-CONTROL")) return;
            if (delayFlushes > 0)
                viewer.MessageQueue.EnqueueAfterFlushes(writer.ToArray(), delayFlushes);
            else
                QueueClientEntityStream(viewer, writer.ToArray());
            Debug.LogError($"[MP-CONTROL] FollowClient grant queued viewer={viewer.LoginName} source={source.LoginName} behavior={remoteBehaviorId} delayFlushes={delayFlushes} sourceFunction=UnitBehavior::FollowClient@0x5202F0");
        }

        private void QueueInitialClientControl(RRConnection conn, ushort behaviorId)
        {
            if (conn == null || !conn.IsConnected) return;
            ushort componentId = behaviorId;
            if (componentId == 0) return;

            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x35);
            writer.WriteUInt16(componentId);
            writer.WriteByte(0x64);
            writer.WriteByte(0x01);
            WritePlayerEntitySynch(conn, writer);
            writer.WriteByte(0x06);
            QueueClientEntityStream(conn, writer.ToArray());
            conn.OwnerUnitBehaviorMoverMode = UnitMover.FollowClientMode;
            SyncReflectedAvatarCombatPosition(conn);
            Debug.LogError($"[FOLLOW-INIT] Queued client control for UnitBehavior={componentId}");
        }

        private static bool ValidatePersistedInventoryForAdmission(SavedCharacter savedChar, out string failure)
        {
            failure = "";
            if (!AuthoredGameplayCatalog.IsLoaded)
            {
                failure = "authored-catalog-not-loaded";
                return false;
            }
            if (savedChar?.inventory == null)
                return true;

            var occupiedByContainer = new Dictionary<byte, bool[,]>();
            var itemOrderKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (SavedInventoryItem item in savedChar.inventory)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.gcClass)
                    || !IsPersistedInventoryContainer(item.containerId)
                    || item.count <= 0 || item.count > byte.MaxValue
                    || item.buyPrice > int.MaxValue
                    || item.rarity < -1 || item.rarity > 5
                    || (item.storedLevel != -1 && (item.storedLevel < 1 || item.storedLevel > 120))
                    || item.itemOrder < 0
                    || !itemOrderKeys.Add($"{item.containerId}:{item.itemOrder}"))
                {
                    failure = "invalid-row";
                    return false;
                }

                var dimensions = MerchantRuntime.GetItemDimensions(item.gcClass);
                int containerHeight = ContainerHeight(item.containerId);
                if (dimensions.width <= 0 || dimensions.height <= 0
                    || item.x + dimensions.width > 10 || item.y + dimensions.height > containerHeight)
                {
                    failure = $"invalid-footprint:{item.gcClass}";
                    return false;
                }
                if (!occupiedByContainer.TryGetValue(item.containerId, out bool[,] occupied))
                {
                    occupied = new bool[10, containerHeight];
                    occupiedByContainer[item.containerId] = occupied;
                }
                for (int offsetX = 0; offsetX < dimensions.width; offsetX++)
                {
                    for (int offsetY = 0; offsetY < dimensions.height; offsetY++)
                    {
                        if (occupied[item.x + offsetX, item.y + offsetY])
                        {
                            failure = $"overlapping-footprint:{item.gcClass}";
                            return false;
                        }
                    }
                }
                for (int offsetX = 0; offsetX < dimensions.width; offsetX++)
                    for (int offsetY = 0; offsetY < dimensions.height; offsetY++)
                        occupied[item.x + offsetX, item.y + offsetY] = true;
            }
            return true;
        }

        private static bool ValidatePersistedSkillsForAdmission(SavedCharacter savedChar, out string failure)
        {
            failure = "";
            if (!AuthoredGameplayCatalog.IsLoaded)
            {
                failure = "authored-catalog-not-loaded";
                return false;
            }
            if (savedChar?.skills == null)
                return true;

            var levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (savedChar.skillLevels != null)
            {
                foreach (SkillLevelEntry entry in savedChar.skillLevels)
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.skill) || entry.level < 1
                        || !levels.TryAdd(entry.skill, entry.level))
                    {
                        failure = "invalid-level-row";
                        return false;
                    }
                }
            }

            var skills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string skillGcClass in savedChar.skills)
            {
                if (string.IsNullOrWhiteSpace(skillGcClass) || !skills.Add(skillGcClass))
                {
                    failure = "invalid-skill-row";
                    return false;
                }
                Combat.SpellData spell = Combat.SpellDatabase.GetSpell(skillGcClass);
                if (spell == null)
                {
                    failure = $"unknown-skill:{skillGcClass}";
                    return false;
                }
                int level = levels.TryGetValue(skillGcClass, out int persistedLevel) ? persistedLevel : 1;
                int authoredMaximum = spell.MaxSkillLevel > 0 ? spell.MaxSkillLevel : 1;
                if (level > authoredMaximum)
                {
                    failure = $"level-above-authored-maximum:{skillGcClass}:{level}:{authoredMaximum}";
                    return false;
                }
            }

            if (savedChar.skillLevels != null)
            {
                for (int skillLevelIndex = 0; skillLevelIndex < savedChar.skillLevels.Count; skillLevelIndex++)
                {
                    string leveledSkill = savedChar.skillLevels[skillLevelIndex].skill;
                    if (!skills.Contains(leveledSkill))
                    {
                        failure = $"orphan-skill-level:{leveledSkill}";
                        return false;
                    }
                }
            }
            return true;
        }

        private void SendPlayerEntitySpawn(RRConnection conn)
        {
            Debug.LogError("[SEND-PLAYER-ENTITY-SPAWN] state=start");
            Debug.LogError($" SendPlayerEntitySpawn CALLED! Stack trace:");
            Debug.LogError(Environment.StackTrace);

            try
            {
                if (conn.LoginName != null && _playerIsFree.ContainsKey(conn.LoginName))
                {
                    try
                    {
                        using (var banDb = Database.GameDatabase.GetConnection())
                        {
                            object banResult = Database.GameDatabase.ExecuteScalar(banDb,
                                "SELECT is_banned FROM accounts WHERE username = @name",
                                ("@name", conn.LoginName));
                            if (banResult != null && System.Convert.ToInt32(banResult) != 0)
                            {
                                SendSystemMessage(conn, "Your account has been banned. Contact an administrator.");
                                Debug.LogError($"[ACCOUNT] Banned user {conn.LoginName} tried to enter game");
                                ServerRuntime.RunRoutine(this, DelayedBanDisconnect(conn));
                                return;
                            }
                        }
                    }
                    catch { }
                }

                if (!_selectedCharacter.TryGetValue(conn.LoginName, out var character))
                {
                    Debug.LogError($"No selected character found for {conn.LoginName}");
                    return;
                }

                SavedCharacter savedChar = CharacterRepository.GetCharacter(character.Id);
                if (savedChar == null)
                {
                    Debug.LogError($"[SPAWN] state=blocked reason=missing-persisted-character characterId={character.Id} user='{conn.LoginName}'");
                    return;
                }
                if (string.IsNullOrWhiteSpace(savedChar.className) || ClassConfig.GetClassDefinition(savedChar.className) == null)
                {
                    Debug.LogError($"[SPAWN] state=blocked reason=invalid-persisted-class characterId={savedChar.id} class='{savedChar.className ?? ""}' user='{conn.LoginName}'");
                    return;
                }
                if (!ValidatePersistedInventoryForAdmission(savedChar, out string inventoryFailure))
                {
                    Debug.LogError($"[SPAWN] state=blocked reason=invalid-persisted-inventory characterId={savedChar.id} detail={inventoryFailure}");
                    return;
                }
                if (!ValidatePersistedSkillsForAdmission(savedChar, out string skillFailure))
                {
                    Debug.LogError($"[SPAWN] state=blocked reason=invalid-persisted-skills characterId={savedChar.id} detail={skillFailure}");
                    return;
                }
                _activeCharacter[conn.LoginName] = savedChar;
                Debug.LogError($"[OP4]  Loaded character {savedChar.name} ({savedChar.className}) from JSON");
                string spawnConnIdKey = conn.ConnId.ToString();

                {
                    var pvpMatchForSpawn = Gameplay.PVPMatchmaking.Instance.GetMatchForPlayer(conn.LoginName);
                    if (pvpMatchForSpawn != null && _zones.TryGetValue(conn.CurrentZoneId, out var pvpSpawnZone) && IsPvpZone(pvpSpawnZone.name))
                        SendPVPMatchControllerSpawn(conn);
                }
                Debug.LogError($"[SPAWN] Initializing PlayerState with key='{spawnConnIdKey}' for conn.ConnId={conn.ConnId}");
                PlayerState playerState = GetPlayerState(spawnConnIdKey);
                bool isZoneTransition = (conn.Avatar != null && conn.Player != null);
                if (!isZoneTransition)
                    playerState.Gold = savedChar.gold;

                if (playerState.Experience > 0 || playerState.Level > 1)
                {
                    var currentChar = savedChar?.DeepClone();
                    byte currentPersistedLevel = SavedCharacterLevel.ResolvePersistedLevel(playerState.Level);
                    if (currentChar != null && (currentPersistedLevel > currentChar.level || playerState.Experience > currentChar.experience))
                    {
                        byte previousLevel = currentChar.level;
                        uint previousExperience = currentChar.experience;
                        currentChar.level = currentPersistedLevel;
                        currentChar.experience = playerState.Experience;
                        if (!TrySaveCharacterForConn(conn, currentChar, "zone-pre-spawn-progress"))
                        {
                            currentChar.level = previousLevel;
                            currentChar.experience = previousExperience;
                            return;
                        }
                        savedChar = currentChar;
                        Debug.LogError($"[ZONE] Saved progress before zone transition: persistedLevel={currentChar.level} clientLevel={playerState.Level} xp={currentChar.experience}");
                    }
                }

                bool isFreshPlayerState = (playerState.Experience == 0 && playerState.Level <= 1);
                bool useFullHPBaseline = isZoneTransition && (conn.FullHPBaselineOnNextSpawn || conn.RespawnFullHPPending);
                bool includeSavedCharacterHP = isZoneTransition || (savedChar != null && savedChar.currentHP > 0);
                PlayerHPPreserve spawnHPPreserve = CapturePlayerHPPreserve(conn, playerState, savedChar, isZoneTransition ? "zone-preinit" : "spawn-preinit", includeSavedCharacterHP);
                bool preservedHPFromLiveState = spawnHPPreserve.FromLiveState;
                uint preservedLiveHPWire = preservedHPFromLiveState ? spawnHPPreserve.HPWire : 0;
                uint preservedSavedHPWire = savedChar.currentHP;
                uint hpToKeepWire = spawnHPPreserve.HasHP ? spawnHPPreserve.HPWire : 0;
                bool preservedHPKnown = spawnHPPreserve.HasHP;
                string hpToKeepSource = spawnHPPreserve.HasHP ? spawnHPPreserve.Source : "none";
                uint manaToKeepWire = isZoneTransition && playerState.HasClientMana ? playerState.CurrentManaWire : savedChar.currentMana;
                Debug.LogError($"[SPAWN-HP-PRESERVE] zoneTransition={isZoneTransition} baseline={useFullHPBaseline} includeSaved={includeSavedCharacterHP} source={hpToKeepSource} preserved={hpToKeepWire} live={preservedLiveHPWire} saved={preservedSavedHPWire}");

                int runtimeLevel = SavedCharacterLevel.ResolveRuntimeLevel(savedChar);
                Debug.LogError($"[SPAWN-LEVEL] persistedLevel={savedChar.level} runtimeLevel={runtimeLevel} xp={savedChar.experience} source=SavedCharacterLevel");
                playerState.InitializeStats(savedChar.className, runtimeLevel, preserveAttributeModifiers: isZoneTransition);
                playerState.AvatarGcType = savedChar.avatarClass ?? "";
                playerState.ApplyAllocatedStats(savedChar.statStrength, savedChar.statAgility, savedChar.statEndurance, savedChar.statIntellect);
                if (isFreshPlayerState)
                {
                    playerState.Experience = savedChar.experience;
                    Debug.LogError($"[SPAWN-XP] Fresh login: loaded XP={savedChar.experience} persistedLv={savedChar.level} clientLv={playerState.Level} from DB for {conn.LoginName}");
                }
                else
                {
                    Debug.LogError($"[SPAWN-XP] Zone transition: keeping live XP={playerState.Experience} Lv={playerState.Level} for {conn.LoginName}");
                }
                playerState.RefreshRegenFactors("spawn-authored-hero-desc");


                InitializePlayerSkillLevels(conn, savedChar);

                if (!string.IsNullOrEmpty(savedChar.tpZone))
                {
                    conn.HasSavedTownPortal = true;
                    conn.TownPortalZoneName = savedChar.tpZone;
                    conn.TownPortalZoneId = unchecked((uint)savedChar.tpZoneId);
                    conn.TownPortalTargetZone = savedChar.tpTargetZone;
                    conn.TownPortalPosFixedX = savedChar.tpPosFixedX;
                    conn.TownPortalPosFixedY = savedChar.tpPosFixedY;
                    conn.TownPortalPosFixedZ = savedChar.tpPosFixedZ;
                    Debug.LogError($"[SPAWN] Loaded saved town portal: {savedChar.tpZone}");
                }

                if (savedChar.equipment != null && !string.IsNullOrEmpty(savedChar.equipment.weapon))
                {
                    var weaponData = AuthoredGameplayCatalog.FindItem(savedChar.equipment.weapon);
                    int weaponLevel = 1;
                    int weaponStoredLevel = -1;
                    if (savedChar.equipment.slotLevel != null && savedChar.equipment.slotLevel.TryGetValue("weapon", out int savedWeaponLevel) && savedWeaponLevel >= 0)
                    {
                        weaponLevel = savedWeaponLevel;
                        weaponStoredLevel = savedWeaponLevel;
                    }
                    else
                        weaponLevel = DungeonRunners.Gameplay.RPGSettings.GetItemLevel(savedChar.equipment.weapon);
                    var weaponNode = GCDatabase.Instance.ResolveWithInheritance(savedChar.equipment.weapon);
                    GCDatabase.WeaponStatsFixed weaponStats = default;
                    if (weaponNode == null)
                        RuntimeEvidence.LogFallbackHit("damage-weapon-desc", "missing-gc-node", $"source=spawn weapon={savedChar.equipment.weapon} sourceFunction=Weapon::ComputeAttributes-return", 64);
                    else
                        weaponStats = GCDatabase.Instance.GetWeaponStatsFixed(savedChar.equipment.weapon);
                    int authoredWeaponDamageF32 = Math.Max(0, weaponStats.DamageF32);
                    int authoredWeaponVolatilityF32 = weaponStats.VolatilityF32 > 0 ? Math.Min(0xF4, weaponStats.VolatilityF32) : 0x40;
                    string resolvedWeaponClass = !string.IsNullOrEmpty(weaponStats.WeaponClass) ? weaponStats.WeaponClass : weaponData != null && !string.IsNullOrEmpty(weaponData.weaponClass) ? weaponData.weaponClass : "";
                    string resolvedDamageType = !string.IsNullOrEmpty(weaponStats.DamageType) ? weaponStats.DamageType : "";
                    if (authoredWeaponDamageF32 > 0 &&
                        TryResolveWeaponDescIds("spawn", savedChar.equipment.weapon, resolvedWeaponClass, resolvedDamageType, out int clientWeaponClassId, out int clientDamageTypeId))
                    {
                        playerState.WeaponDamageF32 = authoredWeaponDamageF32;
                        playerState.WeaponDamageVolatilityF32 = authoredWeaponVolatilityF32;
                        playerState.WeaponLevel = Math.Max(1, weaponLevel);
                        playerState.WeaponClass = resolvedWeaponClass;
                        playerState.WeaponDamageType = resolvedDamageType;
                        playerState.WeaponCategory = !string.IsNullOrEmpty(weaponStats.WeaponCategory) ? weaponStats.WeaponCategory : "";
                        playerState.WeaponStatsResolved = true;
                        playerState.WeaponClassId = clientWeaponClassId;
                        playerState.DamageTypeId = clientDamageTypeId;
                        var spawnedWeaponItem = new GCObject { GCClass = savedChar.equipment.weapon, StoredLevel = weaponStoredLevel };
                        if (savedChar.equipment.slotItemState != null &&
                            savedChar.equipment.slotItemState.TryGetValue("weapon", out SavedItemRuntimeState spawnedWeaponState) && spawnedWeaponState != null)
                            spawnedWeaponState.ApplyTo(spawnedWeaponItem);
                        DamageResolver.ApplyWeaponRuntimeBaseDamage(playerState, spawnedWeaponItem, playerState.Level, "spawn");
                        playerState.WeaponRange = weaponStats.RangeF32 > 0 ? weaponStats.RangeRoundedUnits : weaponData != null && weaponData.range > 0 ? weaponData.range : 0;
                        playerState.WeaponRangeF32 = weaponStats.RangeF32 > 0 ? weaponStats.RangeF32 : playerState.WeaponRange * 0x100;
                        playerState.WeaponInitUseRangeF32 = weaponStats.InitUseRangeF32;
                        playerState.WeaponClientSyncToleranceF32 = weaponStats.ClientSyncToleranceF32;
                        playerState.WeaponCooldownF32 = Math.Max(0, weaponStats.CooldownF32);
                        playerState.WeaponSpeedF32 = weaponStats.WeaponSpeedF32 > 0 ? weaponStats.WeaponSpeedF32 : 100 * 0x100;
                        playerState.WeaponUsesProjectile = weaponStats.UseProjectile;
                        playerState.WeaponShotType = weaponStats.ShotType;
                        playerState.WeaponProjectileSpeedF32 = Math.Max(0, weaponStats.ProjectileSpeedF32);
                        playerState.WeaponProjectileSizeF32 = Math.Max(0, weaponStats.ProjectileSizeF32);
                        playerState.WeaponBurstCount = Math.Max(1, weaponStats.BurstCount);
                        playerState.WeaponStunMod = Math.Max(0, weaponStats.StunMod);
                        Debug.LogError($"[SPAWN] Weapon '{savedChar.equipment.weapon}' damageF32={playerState.WeaponDamageF32} volatilityF32={playerState.WeaponDamageVolatilityF32} level={playerState.WeaponLevel} clientDamageLevel={playerState.WeaponDamageLevel} clientBaseDamage={playerState.WeaponBaseDamage} clientBaseSource={playerState.WeaponBaseDamageSource} class={playerState.WeaponClass}/{playerState.WeaponClassId} damageType={playerState.WeaponDamageType}/{playerState.DamageTypeId} category={playerState.WeaponCategory} range={playerState.WeaponRange} cooldownF32={playerState.WeaponCooldownF32} speedF32={playerState.WeaponSpeedF32} useProjectile={playerState.WeaponUsesProjectile} projectileSpeedF32={playerState.WeaponProjectileSpeedF32} projectileSizeF32={playerState.WeaponProjectileSizeF32} burst={playerState.WeaponBurstCount} from authored data");
                    }
                    else
                    {
                        playerState.WeaponDamageF32 = 0;
                        playerState.WeaponDamageVolatilityF32 = 0;
                        playerState.WeaponLevel = 0;
                        playerState.WeaponClass = "";
                        playerState.WeaponDamageType = "";
                        playerState.WeaponCategory = "";
                        playerState.WeaponStatsResolved = false;
                        playerState.WeaponClassId = 0;
                        playerState.DamageTypeId = -1;
                        playerState.WeaponDamageLevel = 0;
                        playerState.WeaponBaseDamage = 0;
                        playerState.WeaponBaseDamageTracksPlayerLevel = false;
                        playerState.WeaponBaseDamageSource = "spawn:unresolved";
                        playerState.WeaponRange = 0;
                        playerState.WeaponRangeF32 = 0;
                        playerState.WeaponInitUseRangeF32 = 64000;
                        playerState.WeaponClientSyncToleranceF32 = 0;
                        playerState.WeaponCooldownF32 = 0;
                        playerState.WeaponSpeedF32 = 0;
                        playerState.WeaponUsesProjectile = false;
                        playerState.WeaponShotType = 0;
                        playerState.WeaponProjectileSpeedF32 = 0;
                        playerState.WeaponProjectileSizeF32 = 0;
                        playerState.WeaponBurstCount = 1;
                        playerState.WeaponStunMod = 100;
                        RuntimeEvidence.LogFallbackHit("damage-level", "spawn-weapon-unresolved", $"weapon={savedChar.equipment.weapon}", 32);
                        Debug.LogError($"[SPAWN] Weapon '{savedChar.equipment.weapon}' unresolved; leaving weapon damage fields blocked until equipment data resolves");
                    }
                }

                Debug.LogError($"[SPAWN] playerState key='{spawnConnIdKey}' level={playerState.Level} xp={playerState.Experience} weaponDamageF32={playerState.WeaponDamageF32} weaponVolatilityF32={playerState.WeaponDamageVolatilityF32} weaponLevel={playerState.WeaponLevel} baseDamage={playerState.WeaponBaseDamage} baseSource={playerState.WeaponBaseDamageSource} op12HP={playerState.Op12HP} entitySynchInfoHP={playerState.EntitySynchInfoHP}");

                PlayerQuestState previousQuests = isZoneTransition ? QuestManager.Instance.GetPlayerState(conn.ConnId.ToString()) : null;
                var activeQuests = previousQuests?.ActiveQuests ?? ConvertToActiveQuests(savedChar.activeQuests);

                if (previousQuests == null)
                    foreach (var loadedQ in activeQuests)
                        loadedQ.InstanceId = conn.NextQuestInstanceId++;

                var completedQuests = previousQuests?.CompletedQuests ?? savedChar.completedQuests ?? new List<string>();
                var unlockedCheckpoints = savedChar.unlockedCheckpoints ?? new List<string>();
                foreach (string initialCheckpoint in AuthoredGameplayCatalog.StartingCheckpointIds)
                {
                    if (!unlockedCheckpoints.Contains(initialCheckpoint, StringComparer.OrdinalIgnoreCase))
                        unlockedCheckpoints.Add(initialCheckpoint);
                }
                QuestManager.Instance.InitializePlayer(conn.ConnId.ToString(), activeQuests, completedQuests, unlockedCheckpoints,
                    playerState.Level, conn.AvatarGcType, previousQuests?.CompletedAtUnixSeconds ?? savedChar.questCompletionTimes,
                    objective => CountSavedQuestItems(savedChar, objective), isZoneTransition);
                InitializeQuestWorldObjectives(conn);
                Debug.LogError($"[SPAWN] component=QuestManager persistedLevel={savedChar.level} clientLevel={playerState.Level} active={activeQuests.Count} completed={completedQuests.Count}");

                GCObject avatar;
                GCObject player;







                Debug.LogError("[SPAWN] state=first-login action=create-entities");

                if (isZoneTransition)
                {
                    Debug.LogError("[ZONE-TRANSITION] action=reassign-ids source=reuse-objects");
                    avatar = conn.Avatar;
                    player = conn.Player;

                    if (_playerComponentTypes.ContainsKey(spawnConnIdKey))
                        _playerComponentTypes[spawnConnIdKey].Clear();
                    _playerUnitContainerComponentIds.Remove(spawnConnIdKey);
                    if (_playerManipulatorsIds.ContainsKey(spawnConnIdKey))
                        _playerManipulatorsIds.Remove(spawnConnIdKey);
                    if (_playerEquippedItems.ContainsKey(spawnConnIdKey))
                        _playerEquippedItems[spawnConnIdKey].Clear();

                    SetPreferredGeneralEntityId(checked((uint)(conn.ConnId * 500 + 10)));

                    avatar.Id = AllocateGeneralEntityId();
                    player.Id = AllocateGeneralEntityId();

                    var freshChar = savedChar;

                    var freshEquipItems = new List<GCObject>();
                    if (freshChar?.equipment != null)
                    {
                        string[] eqSlots = { freshChar.equipment.weapon, freshChar.equipment.armor,
                            freshChar.equipment.helmet, freshChar.equipment.gloves, freshChar.equipment.boots,
                            freshChar.equipment.shoulders, freshChar.equipment.shield,
                            freshChar.equipment.ring1, freshChar.equipment.ring2, freshChar.equipment.amulet };
                        string[] eqSlotNames = { "weapon", "armor", "helmet", "gloves", "boots",
                            "shoulders", "shield", "ring1", "ring2", "amulet" };
                        for (int eqIdx = 0; eqIdx < eqSlots.Length; eqIdx++)
                        {
                            string equipmentGcClass = eqSlots[eqIdx];
                            if (string.IsNullOrEmpty(equipmentGcClass)) continue;
                            string fixedGc = GCObject.GetPacketGCClassFor(equipmentGcClass);
                            string gcLower = fixedGc.ToLower();
                            string dfcClass = "Armor";
                            if (gcLower.Contains("ring") || gcLower.Contains("amulet"))
                                dfcClass = "Item";
                            else if (gcLower.Contains("sword") || gcLower.Contains("axe") || gcLower.Contains("mace") || gcLower.Contains("dagger") || gcLower.Contains("hammer") || gcLower.Contains("staff") || gcLower.Contains("spear") || gcLower.Contains("pick") || gcLower.Contains("club") || gcLower.Contains("katana") || gcLower.Contains("polearm"))
                                dfcClass = "MeleeWeapon";
                            else if (gcLower.Contains("bow") || gcLower.Contains("gun") || gcLower.Contains("crossbow") || gcLower.Contains("cannon"))
                                dfcClass = "RangedWeapon";
                            var freshEquipItem = new GCObject
                            {
                                GCClass = fixedGc,
                                DFCClass = dfcClass,
                                StoredRarity = (freshChar.equipment.slotRarity != null &&
                                    freshChar.equipment.slotRarity.TryGetValue(eqSlotNames[eqIdx], out int eqRar)) ? eqRar : -1,
                                StoredLevel = (freshChar.equipment.slotLevel != null &&
                                    freshChar.equipment.slotLevel.TryGetValue(eqSlotNames[eqIdx], out int eqLvl)) ? eqLvl : -1,
                                TargetSlot = (eqSlotNames[eqIdx] == "shield" && (dfcClass == "MeleeWeapon" || dfcClass == "RangedWeapon")) ? (uint?)11 : null
                            };
                            if (freshChar.equipment.slotItemState != null &&
                                freshChar.equipment.slotItemState.TryGetValue(eqSlotNames[eqIdx], out SavedItemRuntimeState freshItemState) &&
                                freshItemState != null)
                                freshItemState.ApplyTo(freshEquipItem);
                            freshEquipItems.Add(freshEquipItem);
                        }
                        Debug.LogError($"[ZONE-EQUIP] items={freshEquipItems.Count} source=db");
                    }

                    GCObject ztManipulators = avatar.Children.FirstOrDefault(c => c.GCClass == "Manipulators");
                    if (ztManipulators != null)
                    {
                        int removedCount = ztManipulators.Children.RemoveAll(c =>
                            c.DFCClass == "Armor" || c.DFCClass == "MeleeWeapon" ||
                            c.DFCClass == "RangedWeapon" || c.DFCClass == "Item");
                        Debug.LogError($"[ZONE-MANIP] removed={removedCount} remainingSkills={ztManipulators.Children.Count}");

                        foreach (var eqItem in freshEquipItems)
                            ztManipulators.Children.Add(eqItem);

                        ztManipulators.Id = AllocateGeneralEntityId();
                        foreach (var child in ztManipulators.Children)
                            child.Id = AllocateGeneralEntityId();
                        conn.ManipulatorsComponentId = (ushort)ztManipulators.Id;
                        TrackManipulatorsId(spawnConnIdKey, (ushort)ztManipulators.Id);
                    }
                    GCObject ztEquipment = avatar.Children.FirstOrDefault(c => c.GCClass == "avatar.base.Equipment");
                    if (ztEquipment != null)
                    {
                        ztEquipment.Id = AllocateGeneralEntityId();
                        ztEquipment.Children.Clear();
                        foreach (var eqItem in freshEquipItems)
                            ztEquipment.Children.Add(eqItem);
                        TrackComponent(spawnConnIdKey, (ushort)ztEquipment.Id, "Equipment");
                    }
                    GCObject zoneTransitionQuestManager = player.Children.FirstOrDefault(c => c.GCClass == "QuestManager");
                    if (zoneTransitionQuestManager != null)
                    {
                        zoneTransitionQuestManager.Id = AllocateGeneralEntityId();
                        conn.QuestManagerId = (ushort)zoneTransitionQuestManager.Id;
                    }
                    GCObject zoneTransitionDialogManager = player.Children.FirstOrDefault(c => c.GCClass == "DialogManager");
                    if (zoneTransitionDialogManager != null)
                    {
                        zoneTransitionDialogManager.Id = AllocateGeneralEntityId();
                        conn.DialogManagerId = (ushort)zoneTransitionDialogManager.Id;
                    }
                    GCObject zoneTransitionUnitContainer = avatar.Children.FirstOrDefault(c => c.GCClass == "UnitContainer");
                    if (zoneTransitionUnitContainer != null)
                    {
                        zoneTransitionUnitContainer.Id = AllocateGeneralEntityId();
                        foreach (var child in zoneTransitionUnitContainer.Children)
                            child.Id = AllocateGeneralEntityId();
                        conn.UnitContainerId = (ushort)zoneTransitionUnitContainer.Id;
                        TrackComponent(spawnConnIdKey, (ushort)zoneTransitionUnitContainer.Id, "UnitContainer");
                    }
                    GCObject ztMod = avatar.Children.FirstOrDefault(c => c.GCClass == "Modifiers");
                    if (ztMod != null)
                    {
                        ztMod.Id = AllocateGeneralEntityId();
                        conn.ModifiersId = (ushort)ztMod.Id;
                        conn.SentPassiveModifierIds.Clear();
                    }
                    GCObject ztSkills = avatar.Children.FirstOrDefault(c => c.GCClass == "avatar.base.skills");
                    if (ztSkills != null) ztSkills.Id = AllocateGeneralEntityId();
                    GCObject ztUB = avatar.Children.FirstOrDefault(c => c.GCClass == "avatar.base.UnitBehavior");
                    if (ztUB != null) ztUB.Id = AllocateGeneralEntityId();

                    Debug.LogError($"[ZONE-TRANSITION] avatarId={avatar.Id} playerId={player.Id} nextId={PeekGeneralEntityIdCursor()}");

                    if (_playerAvatarEntityId.TryGetValue(spawnConnIdKey, out uint previousAvatarIdZone) && previousAvatarIdZone != avatar.Id)
                    {
                        ClearPvpConnectionRuntime(conn, "zone-transition-avatar-change", true);
                        Combat.WeaponUseRuntime.Instance.ClearConnection(spawnConnIdKey);
                        _unitContainer?.ClearConnectionRuntime(conn, "zone-transition-avatar-change", true);
                        playerState.InitializeEntityEpochRuntime(true);
                        CombatRuntime.Instance.UnregisterPlayer(previousAvatarIdZone, preserveAttributeModifiers: true);
                        Debug.LogError($"[COMBAT-LIFECYCLE] avatar id changed conn={spawnConnIdKey} old={previousAvatarIdZone} new={avatar.Id}");
                    }
                    _playerAvatarEntityId[spawnConnIdKey] = avatar.Id;

                    CalculateEquipmentBonuses(conn.ConnId.ToString(), avatar);
                    if (useFullHPBaseline)
                    {
                        ApplyFullHPBaseline(conn, playerState, spawnHPPreserve, "zone");
                    }
                    else if (ApplyPlayerHPPreserve(conn, playerState, spawnHPPreserve, "zone", false))
                    {
                        Debug.LogError($"[ZONE-HP-PRESERVE] source={hpToKeepSource} preserved={hpToKeepWire} applied={playerState.CurrentHPWire}/{playerState.MaxHPWire} baseline={useFullHPBaseline} live={preservedHPFromLiveState}");
                        Debug.LogError($"[ZONE-HP-REGEN] damage regen cooldown initialized ticks=0 hp={playerState.CurrentHPWire}/{playerState.MaxHPWire}");
                    }
                    else
                    {
                        Debug.LogError($"[ZONE-HP-FULL] action=restore source={hpToKeepSource} preserved={hpToKeepWire} baseline={useFullHPBaseline}");
                        playerState.RestoreToFull();
                    }
                    if (!useFullHPBaseline && manaToKeepWire > 0)
                        playerState.SetCurrentMana(manaToKeepWire, "ZONE-HP-PRESERVE", false);
                    RecordPlayerHPKnown(conn, "ZONE-HP-INIT", playerState.CurrentHPWire);

                    ApplyAvatarRuntimeProperties(avatar, savedChar, playerState);
                    Debug.LogError($"[ZONE] HP={playerState.CurrentHPWire / 256}/{playerState.MaxHPWire / 256} Mana={playerState.CurrentManaWire / 256}/{playerState.MaxManaWire / 256}");

                    playerState.LogFullState("ZONE-TRANSITION");
                }
                else
                {
                    Debug.LogError("[SPAWN] state=first-login action=create-entities");

                    SetPreferredGeneralEntityId(checked((uint)(conn.ConnId * 500 + 10)));

                    avatar = GCObjectFactory.LoadAvatar(savedChar);
                    player = GCObjectFactory.NewPlayer(character.Name, (uint)character.Id, GroupDirectory.Instance.GetGroupForConn(conn.ConnId)?.GroupId ?? 0, savedChar);

                    CalculateEquipmentBonuses(conn.ConnId.ToString(), avatar);
                    playerState.LogFullState("AFTER-AVATAR-LOAD");

                    conn.Avatar = avatar;
                    conn.Player = player;

                    Debug.LogError("[SPAWN-ID] scope=entity state=start");

                    avatar.Id = AllocateGeneralEntityId();
                    Debug.LogError($"[OP1] avatarId={avatar.Id}");

                    player.Id = AllocateGeneralEntityId();
                    Debug.LogError($"[OP2] playerId={player.Id}");

                    Debug.LogError("[SPAWN-ID] scope=avatar-components state=start");

                    GCObject manipulatorsObject = avatar.Children.FirstOrDefault(c => c.GCClass == "Manipulators");
                    if (manipulatorsObject != null)
                    {
                        manipulatorsObject.Id = AllocateGeneralEntityId();
                        Debug.LogError($"[OP4] manipulatorsId={manipulatorsObject.Id}");
                        foreach (var child in manipulatorsObject.Children)
                        {
                            child.Id = AllocateGeneralEntityId();
                            Debug.LogError($"[OP4] childGcClass='{child.GCClass}' id={child.Id}");
                        }
                    }

                    GCObject equipmentObject = avatar.Children.FirstOrDefault(c => c.GCClass == "avatar.base.Equipment");
                    if (equipmentObject != null)
                    {
                        equipmentObject.Id = AllocateGeneralEntityId();
                        Debug.LogError($"[OP5] equipmentId={equipmentObject.Id}");
                        foreach (var child in equipmentObject.Children)
                        {
                            child.Id = AllocateGeneralEntityId();
                            Debug.LogError($"[OP5] childGcClass='{child.GCClass}' id={child.Id}");
                        }
                    }

                    Debug.LogError("[SPAWN-ID] scope=player-components state=start");

                    GCObject questManagerObject = player.Children.FirstOrDefault(c => c.GCClass == "QuestManager");
                    if (questManagerObject == null)
                    {
                        Debug.LogError("[SPAWN-ID] component=QuestManager state=missing action=create");
                        questManagerObject = new GCObject
                        {
                            GCClass = "QuestManager",
                            DFCClass = "QuestManager",
                            Name = "QuestManager"
                        };
                        player.AddChild(questManagerObject);
                    }
                    questManagerObject.Id = AllocateGeneralEntityId();
                    Debug.LogError($"[OP6] questManagerId={questManagerObject.Id}");

                    GCObject dialogManagerObject = player.Children.FirstOrDefault(c => c.GCClass == "DialogManager");
                    if (dialogManagerObject == null)
                    {
                        Debug.LogError("[SPAWN-ID] component=DialogManager state=missing action=create");
                        dialogManagerObject = new GCObject
                        {
                            GCClass = "DialogManager",
                            DFCClass = "DialogManager",
                            Name = "DialogManager"
                        };
                        player.AddChild(dialogManagerObject);
                    }
                    dialogManagerObject.Id = AllocateGeneralEntityId();
                    Debug.LogError($"[OP7] dialogManagerId={dialogManagerObject.Id}");

                    GCObject unitContainerObject = avatar.Children.FirstOrDefault(c => c.GCClass == "UnitContainer");
                    if (unitContainerObject != null)
                    {
                        unitContainerObject.Id = AllocateGeneralEntityId();
                        Debug.LogError($"[OP8] unitContainerId={unitContainerObject.Id}");
                        foreach (var child in unitContainerObject.Children)
                        {
                            child.Id = AllocateGeneralEntityId();
                            Debug.LogError($"[OP8] inventoryGcClass='{child.GCClass}' id={child.Id}");
                        }
                    }

                    GCObject modifiersObject = avatar.Children.FirstOrDefault(c => c.GCClass == "Modifiers");
                    if (modifiersObject != null)
                    {
                        modifiersObject.Id = AllocateGeneralEntityId();
                        conn.ModifiersId = (ushort)modifiersObject.Id;
                        conn.SentPassiveModifierIds.Clear();
                        Debug.LogError($"[OP9] modifiersId={modifiersObject.Id}");

                    }

                    GCObject skillsObject = avatar.Children.FirstOrDefault(c => c.GCClass == "avatar.base.skills");
                    if (skillsObject == null)
                    {
                        Debug.LogError("[SPAWN-ID] component=Skills state=missing action=create");
                        skillsObject = new GCObject
                        {
                            GCClass = "avatar.base.skills",
                            DFCClass = "Skills",
                            Name = "Skills"
                        };
                        avatar.AddChild(skillsObject);
                    }
                    skillsObject.Id = AllocateGeneralEntityId();
                    Debug.LogError($"[OP10] skillsId={skillsObject.Id}");

                    GCObject unitBehaviorObject = avatar.Children.FirstOrDefault(c => c.GCClass == "avatar.base.UnitBehavior");
                    if (unitBehaviorObject != null)
                    {
                        unitBehaviorObject.Id = AllocateGeneralEntityId();
                        Debug.LogError($"[OP11] unitBehaviorId={unitBehaviorObject.Id}");
                    }

                    Debug.LogError($"[SPAWN-ID] state=complete nextId={PeekGeneralEntityIdCursor()}");
                }

                Debug.LogError($"[SPAWN-ID] avatarId=0x{avatar.Id:X4} playerId=0x{player.Id:X4}");

                if (_playerAvatarEntityId.TryGetValue(spawnConnIdKey, out uint previousAvatarIdNormal) && previousAvatarIdNormal != avatar.Id)
                {
                    ClearPvpConnectionRuntime(conn, "spawn-avatar-change", true);
                    Combat.WeaponUseRuntime.Instance.ClearConnection(spawnConnIdKey);
                    _unitContainer?.ClearConnectionRuntime(conn, "spawn-avatar-change", false);
                    playerState.InitializeEntityEpochRuntime(false);
                    CombatRuntime.Instance.UnregisterPlayer(previousAvatarIdNormal, preserveAttributeModifiers: false);
                    Debug.LogError($"[COMBAT-LIFECYCLE] avatar id changed conn={spawnConnIdKey} old={previousAvatarIdNormal} new={avatar.Id}");
                }
                _playerAvatarEntityId[spawnConnIdKey] = avatar.Id;

                if (useFullHPBaseline)
                {
                    ApplyFullHPBaseline(conn, playerState, spawnHPPreserve, isZoneTransition ? "zone-final" : "spawn");
                }
                else if (ApplyPlayerHPPreserve(conn, playerState, spawnHPPreserve, isZoneTransition ? "zone-final" : "spawn", false))
                {
                    Debug.LogError($"[SPAWN-HP-PRESERVE] source={hpToKeepSource} preserved={hpToKeepWire} applied={playerState.CurrentHPWire}/{playerState.MaxHPWire} baseline={useFullHPBaseline} live={preservedHPFromLiveState}");
                    Debug.LogError($"[SPAWN-HP-REGEN] damage regen cooldown initialized ticks=0 hp={playerState.CurrentHPWire}/{playerState.MaxHPWire}");
                }
                else
                {
                    Debug.LogError($"[SPAWN-HP-FULL] Restoring full HP source={hpToKeepSource} preserved={hpToKeepWire} baseline={useFullHPBaseline}");
                    playerState.RestoreToFull();
                }

                if (!useFullHPBaseline && manaToKeepWire > 0)
                    playerState.SetCurrentMana(manaToKeepWire, "SPAWN-HP-PRESERVE", false);

                RecordPlayerHPKnown(conn, "SPAWN-HP-INIT", playerState.CurrentHPWire);

                if (!isZoneTransition)
                {
                    ApplyAvatarRuntimeProperties(avatar, savedChar, playerState);
                    Debug.LogError($"[SPAWN] Full HP: {playerState.MaxHPWire / 256} Mana: {playerState.MaxManaWire / 256} (savedHP={savedChar.currentHP / 256} savedMana={savedChar.currentMana / 256})");
                }

                playerState.AvatarHP = playerState.CurrentHPWire;
                if (useFullHPBaseline)
                {
                    Debug.LogError($"[ZONE-HP-BASELINE] completed flag source={hpToKeepSource} preservedKnown={preservedHPKnown} hp={playerState.CurrentHPWire}/{playerState.MaxHPWire}");
                    conn.FullHPBaselineOnNextSpawn = false;
                    conn.RespawnFullHPPending = false;
                }
                Debug.LogError($"[SPAWN] Server HP set: {playerState.CurrentHPWire / 256} / {playerState.MaxHPWire / 256} Mana: {playerState.CurrentManaWire / 256} / {playerState.MaxManaWire / 256}");

                var writer = new LEWriter();

                writer.WriteByte(0x07);
                Debug.LogError(" BeginStream (ClientEntityChannel 0x07)");
                Debug.LogError($"[OP1-DETAIL] avatar.GCClass='{avatar.GCClass}'");
                Debug.LogError($"[OP1-DETAIL] savedChar.avatarClass='{savedChar.avatarClass}'");
                Debug.LogError($"[OP1-DETAIL] savedChar.className='{savedChar.className}'");
                Debug.LogError("[OP1] action=create-avatar state=start");
                int beforeOp = writer.Position;

                writer.WriteByte(0x01);
                writer.WriteUInt16((ushort)avatar.Id);
                WriteGCType(writer, avatar.GCClass, preserveCase: true);
                int opBytes = (int)(writer.Position - beforeOp);
                Debug.LogError($" Create Avatar ID={avatar.Id:X4} Class={avatar.GCClass.ToLower()} ({opBytes} bytes)");
                Debug.LogError($" Create Avatar ID={avatar.Id:X4} Class={avatar.GCClass.ToLower()} ({opBytes} bytes)");
                Debug.LogError($"[CUMULATIVE-OP1] Total after Op1: {writer.Position} bytes");
                Debug.LogError("[OP2] action=create-player state=start");
                beforeOp = writer.Position;

                writer.WriteByte(0x01);
                writer.WriteUInt16((ushort)player.Id);
                WriteGCType(writer, player.GCClass.ToLower());

                opBytes = (int)(writer.Position - beforeOp);
                Debug.LogError($" Create Player ID={player.Id:X4} Class={player.GCClass.ToLower()} ({opBytes} bytes)");
                Debug.LogError($" Create Player ID={player.Id:X4} Class={player.GCClass.ToLower()} ({opBytes} bytes)");
                Debug.LogError($"[CUMULATIVE-OP2] Total after Op2: {writer.Position} bytes");
                Debug.LogError("[OP3] action=init-player state=start");
                beforeOp = writer.Position;

                writer.WriteByte(0x02);
                writer.WriteUInt16((ushort)player.Id);

                writer.WriteCString(player.Name);

                writer.WriteUInt32(0x00);
                writer.WriteUInt32(GroupDirectory.Instance.GetGroupForConn(conn.ConnId)?.GroupId ?? 0);

                byte membershipByte;
                if (IsPlayerAdmin(conn.LoginName))
                    membershipByte = 0x00;
                else if (IsPlayerFree(conn.LoginName))
                    membershipByte = 0x02;
                else
                    membershipByte = 0x01;
                writer.WriteByte(membershipByte);

                uint op3UserId = GetCharSqlId(conn);
                writer.WriteUInt32(op3UserId);
                Debug.LogError($"[OP3-USERID] charSqlId=0x{op3UserId:X8}");

                var _pvpChar = GetActiveCharacter(conn);
                WritePlayerPvpFields(writer, _pvpChar);

                var selfPvpMatch = Gameplay.PVPMatchmaking.Instance.GetMatchForPlayer(conn.LoginName);
                if (selfPvpMatch != null)
                {
                    string selfTeamPath = Gameplay.PVPMatchmaking.TeamGcPath(selfPvpMatch.Archetype, selfPvpMatch.IsRed(conn.LoginName));
                    writer.WriteByte(0xFF); writer.WriteCString(selfTeamPath);
                }
                else
                {
                    writer.WriteByte(0x00);
                }

                string op3PosseName = savedChar.posseName ?? "";
                writer.WriteCString(op3PosseName);

                writer.WriteInt32(savedChar.minimumItemQuality);

                opBytes = (int)(writer.Position - beforeOp);
                Debug.LogError($"[OP3] state=complete bytes={opBytes} pos={writer.Position}");
                Debug.LogError("[OP4] component=Manipulators state=start");


                beforeOp = writer.Position;

                var manipulators = avatar.Children.FirstOrDefault(c => c.GCClass == "Manipulators");
                if (manipulators == null)
                {
                    Debug.LogError(" Manipulators not found in avatar children!");
                    return;
                }

                Debug.LogError($"[OP4] manipulatorsId={manipulators.Id}");
                Debug.LogError($"[OP4-START] Total children in Manipulators: {manipulators.Children?.Count ?? 0}");
                TrackManipulatorsId(conn.ConnId.ToString(), (ushort)manipulators.Id);
                conn.ManipulatorsComponentId = (ushort)manipulators.Id;
                writer.WriteByte(0x32);
                writer.WriteUInt16((ushort)avatar.Id);
                writer.WriteUInt16((ushort)manipulators.Id);
                WriteGCType(writer, "Manipulators");
                writer.WriteByte(0x01);
                Debug.LogError($"[OP4-HEADER] Component header written");

                int validChildCount = 0;
                var validChildren = new List<GCObject>();

                if (manipulators.Children != null)
                {
                    foreach (var child in manipulators.Children)
                    {
                        bool isEquipment = (child.DFCClass == "Armor" || child.DFCClass == "Item" ||
                   child.DFCClass == "MeleeWeapon" ||
                   child.DFCClass == "RangedWeapon");

                        if (isEquipment)
                        {
                            ItemData itemData = AuthoredGameplayCatalog.FindItem(child.GCClass);
                            if (itemData != null)
                            {
                                validChildren.Add(child);
                                validChildCount++;
                                Debug.LogError($"[OP4-PRECHECK]  Equipment '{child.GCClass}' is VALID (in ItemDatabase)");
                            }
                            else
                            {
                                string lk4 = child.GCClass.ToLowerInvariant();
                                if (lk4.StartsWith("items.pal.")) lk4 = lk4.Substring(10);
                                if (DungeonRunners.Gameplay.MerchantRuntime.HasAuthoredMerchantModSlots(lk4))
                                {
                                    validChildren.Add(child);
                                    validChildCount++;
                                    Debug.LogError($"[OP4-PRECHECK]  Equipment '{child.GCClass}' is VALID (in authored slot catalog)");
                                }
                                else
                                {
                                    var generalItem = AuthoredGameplayCatalog.FindGeneralItem(child.GCClass);
                                    if (generalItem != null)
                                    {
                                        validChildren.Add(child);
                                        validChildCount++;
                                        Debug.LogError($"[OP4-PRECHECK]  Equipment '{child.GCClass}' is VALID (in GeneralItemDatabase)");
                                    }
                                    else
                                    {
                                        Debug.LogError($"[OP4-PRECHECK]  Equipment '{child.GCClass}' NOT in any database - WILL SKIP");
                                    }
                                }
                            }
                        }
                        else if (child.GCClass != null && child.GCClass.StartsWith("skills.professions.", StringComparison.OrdinalIgnoreCase))
                        {
                            Debug.LogError($"[OP4-PRECHECK]  Profession '{child.GCClass}' is NOT an ActiveSkill - WILL SKIP");
                        }
                        else
                        {
                            validChildren.Add(child);
                            validChildCount++;
                            Debug.LogError($"[OP4-PRECHECK]  Skill '{child.GCClass}' is VALID");
                        }
                    }
                }

                List<PassiveManipulator> passiveManipulators = CollectPassiveManipulators(savedChar);
                int passiveCount = passiveManipulators.Count;
                byte childCount = (byte)(validChildCount + passiveCount);
                writer.WriteByte(childCount);
                Debug.LogError($"[OP4-COUNT] Writing {childCount} VALID children (skills + equipment + {passiveCount} passives)");

                if (validChildren.Count > 0 || passiveCount > 0)
                {
                    var startingSlotMap = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
                    string playerClass = savedChar.className.ToLower();
                    if (playerClass.Contains("fight") || playerClass.Contains("warrior"))
                    {
                        startingSlotMap["skills.generic.Stomp"] = 100;
                        startingSlotMap["skills.generic.Butcher"] = 105;
                    }
                    else if (playerClass.Contains("ranger"))
                    {
                        startingSlotMap["skills.generic.PoisonBlastRadius"] = 100;
                        startingSlotMap["skills.generic.PoisonShot"] = 105;
                    }
                    else
                    {
                        startingSlotMap["skills.generic.ShadowLightning"] = 100;
                        startingSlotMap["skills.generic.FireBolt"] = 105;
                    }
                    Debug.LogError($"[OP4-SLOTS] Class={playerClass}, starting slots: {string.Join(", ", startingSlotMap.Select(slotEntry => $"{slotEntry.Key}->{slotEntry.Value}"))}");

                    if (savedChar.hotbarSlots != null)
                        foreach (var hotbarSlot in savedChar.hotbarSlots)
                        {
                            startingSlotMap[hotbarSlot.skill] = hotbarSlot.slot;
                            Debug.LogError($"[OP4-HOTBAR] Loaded saved slot: {hotbarSlot.skill} -> {hotbarSlot.slot}");
                        }

                    uint nonSlotIdCounter = 200;
                    byte writeIndex = 0;
                    string connKey = conn.ConnId.ToString();
                    _playerSpellSlots[connKey] = new HashSet<byte>();
                    ResetPlayerManipulatorMap(connKey);

                    Debug.LogError($"");
                    Debug.LogError($"[OP4] ");
                    Debug.LogError($"[OP4]  FIRST PASS: WRITING ALL SKILLS");
                    Debug.LogError($"[OP4] ");

                    foreach (var child in validChildren)
                    {
                        bool isEquipment = (child.DFCClass == "Armor" || child.DFCClass == "Item" ||
                    child.DFCClass == "MeleeWeapon" ||
                    child.DFCClass == "RangedWeapon");

                        if (!isEquipment)
                        {
                            uint skillId;
                            if (startingSlotMap.TryGetValue(child.GCClass, out uint slotId))
                            {
                                skillId = slotId;
                                Debug.LogError($"[OP4-SKILL] {child.GCClass} -> STARTING SLOT {skillId}");
                            }
                            else
                            {
                                skillId = nonSlotIdCounter++;
                                Debug.LogError($"[OP4-SKILL] {child.GCClass} -> UNASSIGNED (id={skillId}, no hotbar slot)");
                            }

                            _playerSpellSlots[connKey].Add(writeIndex);
                            SetPlayerManipulator(connKey, skillId, child.GCClass);
                            Debug.LogError($"[OP4-SKILL] Slot {writeIndex} = SPELL: {child.GCClass} -> manipId={skillId}");

                            Debug.LogError($"");
                            Debug.LogError($"[OP4-SKILL] ");
                            Debug.LogError($"[OP4-SKILL] Writing skill: {child.GCClass}");

                            int childStart = writer.Position;

                            byte skillLevel = (byte)savedChar.GetSkillLevel(child.GCClass);
                            WriteActiveSkillManipulatorChild(writer, child.GCClass, skillId, skillLevel);

                            int childBytes = writer.Position - childStart;
                            Debug.LogError($"[OP4-SKILL] gcClass={child.GCClass} level={skillLevel} bytes={childBytes} id=0x{skillId:X8}");
                            writeIndex++;
                        }
                    }

                    foreach (var passive in passiveManipulators)
                    {
                        SetPlayerManipulator(connKey, passive.Slot, passive.Skill);
                        WritePassiveManipulatorChild(writer, passive);
                        writeIndex++;
                        Debug.LogError($"[OP4-PASSIVE] {passive.Skill} slot={passive.Slot} level={passive.Level} modifierId=0x{passive.ModifierId:X8} sourceFunction=PassiveSkill::readInit@0x53D0E0");
                    }

                    Debug.LogError($"");
                    Debug.LogError($"[OP4] ");
                    Debug.LogError($"[OP4]  SECOND PASS: WRITING EQUIPMENT FULL WRITEINIT DATA!");
                    Debug.LogError($"[OP4] ");

                    foreach (var child in validChildren)
                    {
                        bool isEquipment = (child.DFCClass == "Armor" || child.DFCClass == "Item" ||
                    child.DFCClass == "MeleeWeapon" ||
                    child.DFCClass == "RangedWeapon");

                        if (isEquipment)
                        {
                            Debug.LogError($"[OP4-EQUIP] Slot {writeIndex} = EQUIP: {child.GCClass}");
                            Debug.LogError($"");
                            Debug.LogError($"[OP4-EQUIP] ");
                            Debug.LogError($"[OP4-EQUIP] Writing FULL WriteInit for: {child.GCClass}");

                            int childStart = writer.Position;

                            uint equipmentSlot = child.TargetSlot ?? child.GetEquipmentSlotFromGCClass();
                            int equipmentLevel = child.StoredLevel >= 0 ? child.StoredLevel : Math.Max(1, child.GetItemRequiredLevel());
                            WriteEquipmentManipulatorChild(writer, child, equipmentSlot, equipmentLevel);

                            int childBytes = writer.Position - childStart;
                            Debug.LogError($"[OP4-EQUIP]  WriteInit complete: {childBytes} bytes");
                            if (VerbosePacketLogging) Debug.LogError($"[OP4-EQUIP] Hex: {BitConverter.ToString(writer.ToArray(), childStart, childBytes)}");
                            writeIndex++;
                        }
                    }
                }
                else
                {
                    Debug.LogError($"[OP4-WARNING] No valid children to write!");
                }

                opBytes = (int)(writer.Position - beforeOp);
                Debug.LogError($"[OP4-COMPLETE] Manipulators component: {opBytes} bytes");
                if (_playerSpellSlots.ContainsKey(conn.ConnId.ToString()))
                    Debug.LogError($"[OP4-SPELLMAP] Spell slots for {conn.LoginName}: [{string.Join(", ", _playerSpellSlots[conn.ConnId.ToString()])}]");
                string manipulatorLogKey = conn.ConnId.ToString();
                if (_playerManipMap.TryGetValue(manipulatorLogKey, out Dictionary<uint, string> manipulatorLogMap)
                    && _playerManipulatorOrder.TryGetValue(manipulatorLogKey, out List<uint> manipulatorLogOrder))
                {
                    for (int manipulatorLogIndex = 0; manipulatorLogIndex < manipulatorLogOrder.Count; manipulatorLogIndex++)
                    {
                        uint manipulatorId = manipulatorLogOrder[manipulatorLogIndex];
                        if (manipulatorLogMap.TryGetValue(manipulatorId, out string manipulatorGcClass))
                            Debug.LogError($"[OP4-MANIPMAP] manipId={manipulatorId} -> {manipulatorGcClass}");
                    }
                }

                if (VerbosePacketLogging)
                {
                    Debug.LogError($"");
                    Debug.LogError("[OP4-HEX]");
                    Debug.LogError("[OP4-HEX] operation=4 complete");
                    StringBuilder op4Hex = new StringBuilder();
                    for (int rowOffset = beforeOp; rowOffset < writer.Position; rowOffset += 16)
                    {
                        op4Hex.Append($"{rowOffset:X8}  ");
                        for (int columnOffset = 0; columnOffset < 16; columnOffset++)
                        {
                            if (rowOffset + columnOffset < writer.Position)
                                op4Hex.Append($"{writer.ToArray()[rowOffset + columnOffset]:X2} ");
                            else
                                op4Hex.Append("   ");
                        }
                        op4Hex.Append(" |");
                        for (int columnOffset = 0; columnOffset < 16 && rowOffset + columnOffset < writer.Position; columnOffset++)
                        {
                            byte byteValue = writer.ToArray()[rowOffset + columnOffset];
                            op4Hex.Append((byteValue >= 32 && byteValue < 127) ? (char)byteValue : '.');
                        }
                        op4Hex.AppendLine("|");
                    }
                    Debug.LogError(op4Hex.ToString());
                    Debug.LogError("[OP4-HEX]");
                }
                Debug.LogError("[OP4] state=complete");
                Debug.LogError($"[OP4-COMPLETE] Manipulators component: {opBytes} bytes");
                Debug.LogError($"[CUMULATIVE-OP4] Total after Op4: {writer.Position} bytes");


                Debug.LogError("[OP5] component=Equipment state=start");
                beforeOp = writer.Position;

                var equipment = avatar.Children.FirstOrDefault(c => c.GCClass == "avatar.base.Equipment");

                if (equipment == null)
                {
                    Debug.LogError(" Equipment not found in avatar children!");
                    return;
                }

                Debug.LogError($"[OP5] equipmentId={equipment.Id} children={equipment.Children?.Count ?? 0}");
                TrackComponent(conn.ConnId.ToString(), (ushort)equipment.Id, "Equipment");

                int op5HeaderStart = writer.Position;
                writer.WriteByte(0x32);
                writer.WriteUInt16((ushort)avatar.Id);
                writer.WriteUInt16((ushort)equipment.Id);
                WriteGCType(writer, "avatar.base.Equipment");
                writer.WriteByte(0x01);
                int op5HeaderBytes = writer.Position - op5HeaderStart;
                Debug.LogError($"[OP5-HEADER] Component header written ({op5HeaderBytes} bytes)");

                var equippedItems = equipment.Children?.Where(c =>
      c.DFCClass == "Armor" ||
      c.DFCClass == "MeleeWeapon" ||
      c.DFCClass == "RangedWeapon" ||
      c.DFCClass == "Item"
  ).ToList() ?? new List<GCObject>();

                var validItems = new List<GCObject>();
                foreach (var item in equippedItems)
                {
                    ItemData itemData = AuthoredGameplayCatalog.FindItem(item.GCClass);
                    if (itemData != null)
                    {
                        validItems.Add(item);
                        Debug.LogError($"[OP5-VALID] Item '{item.GCClass}' found in ItemDatabase - will write");
                    }
                    else
                    {
                        string lk = item.GCClass.ToLowerInvariant();
                        if (lk.StartsWith("items.pal.")) lk = lk.Substring(10);
                        if (DungeonRunners.Gameplay.MerchantRuntime.HasAuthoredMerchantModSlots(lk))
                        {
                            validItems.Add(item);
                            Debug.LogError($"[OP5-VALID] Item '{item.GCClass}' found in authored slot catalog - will write");
                        }
                        else
                        {
                            var generalItem = AuthoredGameplayCatalog.FindGeneralItem(item.GCClass);
                            if (generalItem != null)
                            {
                                validItems.Add(item);
                                Debug.LogError($"[OP5-VALID] Item '{item.GCClass}' found in GeneralItemDatabase (ring/amulet) - will write");
                            }
                            else
                            {
                                Debug.LogError($"[OP5-SKIP] Item '{item.GCClass}' NOT in any database - SKIPPING!");
                            }
                        }
                    }
                }

                byte itemCount = (byte)validItems.Count;

                int op5CountBytePos = writer.Position;
                writer.WriteByte(itemCount);
                Debug.LogError($"[OP5-COUNT] Writing {itemCount} VALID equipped items at position {op5CountBytePos}");

                foreach (var item in validItems)
                {
                    uint equipSlot = item.TargetSlot ?? item.GetEquipmentSlotFromGCClass();
                    item.TargetSlot = equipSlot;
                    TrackEquippedItem(conn.ConnId.ToString(), equipSlot, item);

                    int itemStart = writer.Position;
                    int itemLevel = item.StoredLevel >= 0
                        ? item.StoredLevel
                        : Math.Max(1, item.GetItemRequiredLevel());
                    item.WriteItemData(writer, equipSlot, 0, 0, 1, itemLevel);
                    int itemBytes = writer.Position - itemStart;
                    Debug.LogError($"[OP5-ITEM] gcClass='{item.GCClass}' slot={equipSlot} level={itemLevel} bytes={itemBytes} state=complete");
                    if (VerbosePacketLogging)
                        Debug.LogError($"[OP5-ITEM] hex={BitConverter.ToString(writer.ToArray(), itemStart, itemBytes)}");
                }

                opBytes = (int)(writer.Position - beforeOp);
                Debug.LogError($"[OP5-COMPLETE] component=Equipment bytes={opBytes} equippedItems={itemCount} state=complete");

                if (VerbosePacketLogging)
                {
                    Debug.LogError($"");
                    Debug.LogError("[OP5-HEX]");
                    Debug.LogError("[OP5-HEX] operation=5 complete");
                    StringBuilder op5Hex = new StringBuilder();
                    for (int rowOffset = beforeOp; rowOffset < writer.Position; rowOffset += 16)
                    {
                        op5Hex.Append($"{rowOffset:X8}  ");
                        for (int columnOffset = 0; columnOffset < 16; columnOffset++)
                        {
                            if (rowOffset + columnOffset < writer.Position)
                                op5Hex.Append($"{writer.ToArray()[rowOffset + columnOffset]:X2} ");
                            else
                                op5Hex.Append("   ");
                        }
                        op5Hex.Append(" |");
                        for (int columnOffset = 0; columnOffset < 16 && rowOffset + columnOffset < writer.Position; columnOffset++)
                        {
                            byte byteValue = writer.ToArray()[rowOffset + columnOffset];
                            op5Hex.Append((byteValue >= 32 && byteValue < 127) ? (char)byteValue : '.');
                        }
                        op5Hex.AppendLine("|");
                    }
                    Debug.LogError(op5Hex.ToString());
                    Debug.LogError("[OP5-HEX]");
                }
                Debug.LogError("[OP5] state=complete");
                Debug.LogError($"[OP5-COMPLETE] component=Equipment bytes={opBytes} equippedItems={itemCount} state=complete");
                Debug.LogError($"[CUMULATIVE-OP5] Total after Op5: {writer.Position} bytes");
                Debug.LogError("[OP6] component=QuestManager state=start");
                beforeOp = writer.Position;

                var questManager = player.Children.FirstOrDefault(c => c.GCClass == "QuestManager");
                if (questManager == null)
                {
                    Debug.LogError("[OP6] component=QuestManager state=missing action=create");
                    questManager = new GCObject
                    {
                        GCClass = "QuestManager",
                        DFCClass = "QuestManager",
                        Name = "QuestManager",
                        Id = AllocateGeneralEntityId()
                    };
                    player.AddChild(questManager);
                    Debug.LogError("[OP6] component=QuestManager state=created");
                }

                conn.QuestManagerId = (ushort)questManager.Id;
                Debug.LogError($"[OP6] questManagerId={questManager.Id}");

                QuestManager.Instance.WriteQuestManagerComponent(
                    writer,
                    conn.ConnId.ToString(),
                    (ushort)player.Id,
                    (ushort)questManager.Id,
                    (w, gcType, preserveCase) => WriteGCType(w, gcType, preserveCase),
                    conn
                );

                opBytes = (int)(writer.Position - beforeOp);
                Debug.LogError($"[OP6] component=QuestManager bytes={opBytes} state=complete");

                if (VerbosePacketLogging)
                {
                    Debug.LogError("[OP6-HEX]");
                    StringBuilder opHex = new StringBuilder();
                    for (int rowOffset = beforeOp; rowOffset < writer.Position; rowOffset += 16)
                    {
                        opHex.Append($"{rowOffset:X8}  ");
                        for (int columnOffset = 0; columnOffset < 16; columnOffset++)
                        {
                            if (rowOffset + columnOffset < writer.Position)
                                opHex.Append($"{writer.ToArray()[rowOffset + columnOffset]:X2} ");
                            else
                                opHex.Append("   ");
                        }
                        opHex.Append(" |");
                        for (int columnOffset = 0; columnOffset < 16 && rowOffset + columnOffset < writer.Position; columnOffset++)
                        {
                            byte byteValue = writer.ToArray()[rowOffset + columnOffset];
                            opHex.Append((byteValue >= 32 && byteValue < 127) ? (char)byteValue : '.');
                        }
                        opHex.AppendLine("|");
                    }
                    Debug.LogError(opHex.ToString());
                    Debug.LogError("[OP6-HEX]");
                }
                Debug.LogError($"[CUMULATIVE-OP6] Total after Op6: {writer.Position} bytes");


                Debug.LogError("[OP7] component=DialogManager state=start");
                beforeOp = writer.Position;

                var dialogManager = player.Children.FirstOrDefault(c => c.GCClass == "DialogManager");
                if (dialogManager == null)
                {
                    Debug.LogError("[OP7] component=DialogManager state=missing action=create");
                    dialogManager = new GCObject();
                    dialogManager.GCClass = "DialogManager";
                    dialogManager.DFCClass = "DialogManager";
                    dialogManager.Name = "DialogManager";
                    dialogManager.Id = AllocateGeneralEntityId();
                    player.AddChild(dialogManager);
                    Debug.LogError("[OP7] component=DialogManager state=created");
                }

                Debug.LogError($"[OP7] dialogManagerId={dialogManager.Id}");

                writer.WriteByte(0x32);
                writer.WriteUInt16((ushort)player.Id);
                writer.WriteUInt16((ushort)dialogManager.Id);
                WriteGCType(writer, "DialogManager");
                writer.WriteByte(0x01);

                opBytes = (int)(writer.Position - beforeOp);
                Debug.LogError($"[OP7] component=DialogManager bytes={opBytes} data=none state=complete");



                conn.DialogManagerId = (ushort)dialogManager.Id;
                Debug.LogError($"[OP7] component=DialogManager trackedId={dialogManager.Id}");

                opBytes = (int)(writer.Position - beforeOp);
                Debug.LogError($"[OP7] component=DialogManager bytes={opBytes} data=none state=complete");


                if (VerbosePacketLogging)
                {
                    Debug.LogError("[OP7-HEX]");
                    Debug.LogError("[OP7-HEX] state=complete");
                    StringBuilder opHex7 = new StringBuilder();
                    for (int rowOffset = beforeOp; rowOffset < writer.Position; rowOffset += 16)
                    {
                        opHex7.Append($"{rowOffset:X8}  ");
                        for (int columnOffset = 0; columnOffset < 16; columnOffset++)
                        {
                            if (rowOffset + columnOffset < writer.Position)
                                opHex7.Append($"{writer.ToArray()[rowOffset + columnOffset]:X2} ");
                            else
                                opHex7.Append("   ");
                        }
                        opHex7.Append(" |");
                        for (int columnOffset = 0; columnOffset < 16 && rowOffset + columnOffset < writer.Position; columnOffset++)
                        {
                            byte byteValue = writer.ToArray()[rowOffset + columnOffset];
                            opHex7.Append((byteValue >= 32 && byteValue < 127) ? (char)byteValue : '.');
                        }
                        opHex7.AppendLine("|");
                    }
                    Debug.LogError(opHex7.ToString());
                    Debug.LogError("[OP7-HEX]");
                }
                Debug.LogError("[OP7] component=DialogManager data=none state=complete");
                Debug.LogError($"[CUMULATIVE-OP7] Total after Op7: {writer.Position} bytes");


                Debug.LogError("[OP8] component=UnitContainer state=start");
                beforeOp = writer.Position;

                var unitContainer = avatar.Children.FirstOrDefault(c => c.GCClass == "UnitContainer");
                if (unitContainer == null)
                {
                    Debug.LogError("[OP8] component=UnitContainer state=missing");
                    return;
                }
                Debug.LogError($"[OP8] unitContainerId={unitContainer.Id}");
                TrackComponent(conn.ConnId.ToString(), (ushort)unitContainer.Id, "UnitContainer");
                conn.UnitContainerId = (ushort)unitContainer.Id;
                writer.WriteByte(0x32);
                writer.WriteUInt16((ushort)avatar.Id);
                writer.WriteUInt16((ushort)unitContainer.Id);
                WriteGCType(writer, "UnitContainer");
                writer.WriteByte(0x01);

                writer.WriteUInt32(0);
                writer.WriteUInt32(savedChar.gold);

                Debug.LogError($"[UNITCONTAINER] gold={savedChar.gold}");

                var mainInventory = unitContainer.Children.FirstOrDefault(c => c.GCClass == "avatar.base.Inventory");
                var bankPage1 = unitContainer.Children.FirstOrDefault(c => c.GCClass == "avatar.base.Bank");
                var bankPage2 = unitContainer.Children.FirstOrDefault(c => c.GCClass == "avatar.base.Bank2");
                var bankPage3 = unitContainer.Children.FirstOrDefault(c => c.GCClass == "avatar.base.Bank3");
                var bankPage4 = unitContainer.Children.FirstOrDefault(c => c.GCClass == "avatar.base.Bank4");
                var bankPage5 = unitContainer.Children.FirstOrDefault(c => c.GCClass == "avatar.base.Bank5");
                var bankPage6 = unitContainer.Children.FirstOrDefault(c => c.GCClass == "avatar.base.Bank6");
                var bankPage7 = unitContainer.Children.FirstOrDefault(c => c.GCClass == "avatar.base.Bank7");
                var tradeInventory = unitContainer.Children.FirstOrDefault(c => c.GCClass == "avatar.base.TradeInventory");

                if (mainInventory == null || bankPage1 == null || tradeInventory == null
                    || bankPage2 == null || bankPage3 == null || bankPage4 == null
                    || bankPage5 == null || bankPage6 == null || bankPage7 == null)
                {
                    Debug.LogError(" Missing required inventories!");
                    return;
                }

                var inventoriesToWrite = new[] {
            (inventory: mainInventory, id: (byte)0x0B, name: "Inventory"),
            (inventory: bankPage1, id: (byte)0x0C, name: "Bank"),
            (inventory: tradeInventory, id: (byte)0x0D, name: "TradeInventory"),
            (inventory: bankPage2, id: (byte)0x0E, name: "Bank2"),
            (inventory: bankPage3, id: (byte)0x0F, name: "Bank3"),
            (inventory: bankPage4, id: (byte)0x10, name: "Bank4"),
            (inventory: bankPage5, id: (byte)0x11, name: "Bank5"),
            (inventory: bankPage6, id: (byte)0x12, name: "Bank6"),
            (inventory: bankPage7, id: (byte)0x13, name: "Bank7")
        };

                writer.WriteByte((byte)inventoriesToWrite.Length);

                {
                    string clearConnId = conn.ConnId.ToString();
                    byte[] allContainers = { 0x0B, 0x0C, 0x0E, 0x0F, 0x10, 0x11, 0x12, 0x13 };
                    foreach (byte containerId in allContainers)
                    {
                        string key = InvKey(clearConnId, containerId);
                        if (_playerInventoryItems.ContainsKey(key))
                            _playerInventoryItems[key].Clear();
                        if (_playerInventoryOrder.ContainsKey(key))
                            _playerInventoryOrder[key].Clear();
                        if (_inventoryStackCounts.ContainsKey(key))
                            _inventoryStackCounts[key].Clear();
                        if (_occupiedInventorySlots.ContainsKey(key))
                            _occupiedInventorySlots[key].Clear();
                    }
                    if (_inventorySlotCounters.ContainsKey(clearConnId))
                        _inventorySlotCounters.Remove(clearConnId);

                    if (savedChar.inventory != null)
                    {
                        foreach (var bpItem in savedChar.inventory)
                        {
                            if (bpItem.buyPrice > 0)
                                DungeonRunners.Gameplay.MerchantRuntime.SetBuyPrice(clearConnId, bpItem.gcClass, bpItem.buyPrice);
                        }
                    }
                }

                uint globalItemIndex = 1;

                foreach (var inv in inventoriesToWrite)
                {
                    Debug.LogError($"     Writing {inv.name}");
                    WriteGCType(writer, inv.inventory.GCClass);
                    writer.WriteByte(inv.id);
                    writer.WriteByte(0x01);

                    bool isPersistedContainer = (inv.id == 0x0B || inv.id == 0x0C || (inv.id >= 0x0E && inv.id <= 0x13));
                    var containerItems = (isPersistedContainer && savedChar.inventory != null)
                        ? savedChar.inventory.FindAll(i => i.containerId == inv.id)
                        : new List<SavedInventoryItem>();

                    if (containerItems.Count > 0)
                    {
                        string clearConnId = conn.ConnId.ToString();
                        writer.WriteByte((byte)containerItems.Count);
                        Debug.LogError($"[UNITCONTAINER] gcType={inv.inventory.GCClass} id=0x{inv.id:X2} items={containerItems.Count}");

                        foreach (var item in containerItems)
                        {
                            uint itemIndex = globalItemIndex++;
                            string gcTypeToSend = item.gcClass.ToLowerInvariant();
                            var gcObj = new GCObject
                            {
                                GCClass = item.gcClass,
                                DFCClass = ResolveAuthoredItemClass(item.gcClass),
                                StoredRarity = item.rarity,
                                StoredLevel = item.storedLevel
                            };
                            (item.itemState ?? new SavedItemRuntimeState()).ApplyTo(gcObj);
                            int itemLevel = item.storedLevel >= 0
                                ? item.storedLevel
                                : Math.Max(1, ItemData.GetRequiredLevelFromGCClass(item.gcClass));
                            byte quantity = (byte)item.count;
                            int invItemStart = writer.Position;
                            gcObj.WriteItemData(writer, itemIndex, item.x, item.y, quantity, itemLevel);
                            int invItemLength = writer.Position - invItemStart;
                            if (VerbosePacketLogging)
                                Debug.LogError($"[INV-RESTORE] item={itemIndex} gc={item.gcClass} level={itemLevel} quantity={quantity} bytes={invItemLength} hex={BitConverter.ToString(writer.ToArray(), invItemStart, invItemLength)}");

                            TrackInventoryItem(conn.ConnId.ToString(), itemIndex, gcObj, item.x, item.y, inv.id);
                            var itemDims = DungeonRunners.Gameplay.MerchantRuntime.GetItemDimensions(item.gcClass);
                            int iw = itemDims.width, ih = itemDims.height;
                            OccupyInventorySlots(conn.ConnId.ToString(), item.x, item.y, iw, ih, inv.id);
                            SetStackCount(conn.ConnId.ToString(), itemIndex, item.count, inv.id);

                            Debug.LogError($"        -> Item {itemIndex} (container 0x{inv.id:X2}): {gcTypeToSend} at ({item.x},{item.y})");
                        }
                    }
                    else
                    {
                        writer.WriteByte(0x00);
                        Debug.LogError($"[UNITCONTAINER] gcType={inv.inventory.GCClass} id=0x{inv.id:X2} items=0");
                    }
                }

                {
                    string slotCounterKey = conn.ConnId.ToString();
                    uint floor = globalItemIndex > 100 ? globalItemIndex : 100;
                    _inventorySlotCounters[slotCounterKey] = floor;
                }

                writer.WriteByte(0x00);
                Debug.LogError($"     UnitContainer final byte: 0x00");

                opBytes = (int)(writer.Position - beforeOp);
                Debug.LogError($"[OP8] component=UnitContainer bytes={opBytes} state=complete");

                if (VerbosePacketLogging)
                {
                    Debug.LogError("[OP7-HEX]");
                    Debug.LogError("[OP7-HEX] state=complete");
                    StringBuilder opHex8 = new StringBuilder();
                    for (int rowOffset = beforeOp; rowOffset < writer.Position; rowOffset += 16)
                    {
                        opHex8.Append($"{rowOffset:X8}  ");
                        for (int columnOffset = 0; columnOffset < 16; columnOffset++)
                        {
                            if (rowOffset + columnOffset < writer.Position)
                                opHex8.Append($"{writer.ToArray()[rowOffset + columnOffset]:X2} ");
                            else
                                opHex8.Append("   ");
                        }
                        opHex8.Append(" |");
                        for (int columnOffset = 0; columnOffset < 16 && rowOffset + columnOffset < writer.Position; columnOffset++)
                        {
                            byte byteValue = writer.ToArray()[rowOffset + columnOffset];
                            opHex8.Append((byteValue >= 32 && byteValue < 127) ? (char)byteValue : '.');
                        }
                        opHex8.AppendLine("|");
                    }
                    Debug.LogError(opHex8.ToString());
                    Debug.LogError("[OP7-HEX]");
                }
                Debug.LogError("[OP8] component=UnitContainer state=complete");
                Debug.LogError($"[CUMULATIVE-OP8] Total after Op8: {writer.Position} bytes");

                Debug.LogError("[OP9] component=Modifiers state=start");
                beforeOp = writer.Position;

                var modifiers = avatar.Children.FirstOrDefault(c => c.GCClass == "Modifiers");
                if (modifiers == null)
                {
                    Debug.LogError(" Modifiers not found in avatar children!");
                    return;
                }

                Debug.LogError($"[OP9] modifiersId={modifiers.Id}");

                writer.WriteByte(0x32);
                writer.WriteUInt16((ushort)avatar.Id);
                writer.WriteUInt16((ushort)modifiers.Id);
                WriteGCType(writer, "Modifiers");
                writer.WriteByte(0x01);

                WritePassiveModifiersComponent(writer, null, (ushort)avatar.Id);

                opBytes = (int)(writer.Position - beforeOp);
                Debug.LogError($"[OP9] component=Modifiers passives=0 bytes={opBytes} state=complete");

                if (VerbosePacketLogging)
                {
                    Debug.LogError("[OP7-HEX]");
                    Debug.LogError("[OP7-HEX] state=complete");
                    StringBuilder opHex9 = new StringBuilder();
                    for (int rowOffset = beforeOp; rowOffset < writer.Position; rowOffset += 16)
                    {
                        opHex9.Append($"{rowOffset:X8}  ");
                        for (int columnOffset = 0; columnOffset < 16; columnOffset++)
                        {
                            if (rowOffset + columnOffset < writer.Position)
                                opHex9.Append($"{writer.ToArray()[rowOffset + columnOffset]:X2} ");
                            else
                                opHex9.Append("   ");
                        }
                        opHex9.Append(" |");
                        for (int columnOffset = 0; columnOffset < 16 && rowOffset + columnOffset < writer.Position; columnOffset++)
                        {
                            byte byteValue = writer.ToArray()[rowOffset + columnOffset];
                            opHex9.Append((byteValue >= 32 && byteValue < 127) ? (char)byteValue : '.');
                        }
                        opHex9.AppendLine("|");
                    }
                    Debug.LogError(opHex9.ToString());
                    Debug.LogError("[OP7-HEX]");
                }
                Debug.LogError("[OP9] component=Modifiers state=complete");
                Debug.LogError($"[CUMULATIVE-OP9] Total after Op9: {writer.Position} bytes");


                Debug.LogError("[OP10] component=Skills state=start");
                beforeOp = writer.Position;

                var skills = avatar.Children.FirstOrDefault(c => c.GCClass == "avatar.base.skills");
                if (skills == null)
                {
                    Debug.LogError("[OP10] component=Skills state=missing action=create");
                    skills = new GCObject();
                    skills.GCClass = "avatar.base.skills";
                    skills.DFCClass = "Skills";
                    skills.Name = "Skills";
                    skills.Id = AllocateGeneralEntityId();
                    avatar.AddChild(skills);
                    Debug.LogError("[OP10] component=Skills state=created");
                }

                Debug.LogError($"[OP10] skillsId={skills.Id}");

                _playerSkillsComponentId[spawnConnIdKey] = (ushort)skills.Id;
                _playerSkillSlots[spawnConnIdKey] = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

                int headerStart = writer.Position;
                writer.WriteByte(0x32);
                writer.WriteUInt16((ushort)avatar.Id);
                writer.WriteUInt16((ushort)skills.Id);
                WriteGCType(writer, "avatar.base.skills");
                writer.WriteByte(0x01);
                int headerEnd = writer.Position;
                Debug.LogError($"[OP10-HEADER] Component header: {headerEnd - headerStart} bytes (expected 26)");
                Debug.LogError($"[OP10-HEADER] Component header written");

                writer.WriteUInt32(0xFFFFFFFF);
                Debug.LogError($"[OP10-INIT] Wrote 0xFFFFFFFF");

                var manipulatorSkills = manipulators?.Children?.Where(c =>
                    c.DFCClass == "ActiveSkill"
                    && !(c.GCClass ?? "").StartsWith("skills.professions.", StringComparison.OrdinalIgnoreCase)).ToList()
                    ?? new List<GCObject>();

                var passiveSkills = new List<GCObject>();
                if (savedChar.skills != null)
                {
                    foreach (var skillId in savedChar.skills)
                    {
                        if (skillId.ToLower().Contains("passive") || skillId.ToLower().Contains("trait"))
                        {
                            passiveSkills.Add(new GCObject
                            {
                                DFCClass = "PassiveSkill",
                                GCClass = skillId,
                                Name = null
                            });
                            Debug.LogError($"[OP10-PASSIVE] Including passive: {skillId}");
                        }
                    }
                }

                int totalSkillCount = manipulatorSkills.Count + passiveSkills.Count;
                Debug.LogError($"[OP10-COUNT] Active skills: {manipulatorSkills.Count}, Passive skills: {passiveSkills.Count}, Total: {totalSkillCount}");
                Debug.LogError($"[OP10-COUNT] AuthoredGameplayCatalog.Skills.Count = {AuthoredGameplayCatalog.Skills.Count} (NOT USING!)");
                writer.WriteByte((byte)totalSkillCount);
                Debug.LogError($"[OP10-COUNT] Writing {totalSkillCount} skills (active + passive)");


                foreach (var skillObj in manipulatorSkills)
                {
                    int skillStart = writer.Position;
                    uint skillEntityId = 0;

                    WriteGCType(writer, skillObj.GCClass);
                    writer.WriteUInt32(skillEntityId);

                    int savedSkillLevel = savedChar.GetSkillLevel(skillObj.GCClass);
                    if (savedSkillLevel < 1) savedSkillLevel = 1;
                    writer.WriteByte((byte)savedSkillLevel);

                    _playerSkillSlots[spawnConnIdKey][skillObj.GCClass] = skillEntityId;
                    int dotIdx = skillObj.GCClass.LastIndexOf('.');
                    if (dotIdx >= 0)
                        _playerSkillSlots[spawnConnIdKey][skillObj.GCClass.Substring(dotIdx + 1)] = skillEntityId;

                    int skillBytes = writer.Position - skillStart;
                    Debug.LogError($"[OP10-SKILL] '{skillObj.GCClass}' entityId={skillEntityId} level={savedSkillLevel} ({skillBytes} bytes)");
                }
                foreach (var skillObj in passiveSkills)
                {
                    int skillStart = writer.Position;
                    uint skillEntityId = 0;

                    WriteGCType(writer, skillObj.GCClass);
                    writer.WriteUInt32(skillEntityId);

                    int savedSkillLevel = savedChar.GetSkillLevel(skillObj.GCClass);
                    if (savedSkillLevel < 1) savedSkillLevel = 1;
                    writer.WriteByte((byte)savedSkillLevel);

                    _playerSkillSlots[spawnConnIdKey][skillObj.GCClass] = skillEntityId;
                    int dotIdx = skillObj.GCClass.LastIndexOf('.');
                    if (dotIdx >= 0)
                        _playerSkillSlots[spawnConnIdKey][skillObj.GCClass.Substring(dotIdx + 1)] = skillEntityId;

                    int skillBytes = writer.Position - skillStart;
                    Debug.LogError($"[OP10-PASSIVE] '{skillObj.GCClass}' entityId={skillEntityId} level={savedSkillLevel} ({skillBytes} bytes)");
                }
                _playerNextSkillEntityId[spawnConnIdKey] = PeekGeneralEntityIdCursor();
                Debug.LogError($"[OP10-TRACK] Next skill entity ID for trainer: {PeekGeneralEntityIdCursor()}");
                Debug.LogError("[OP10-DETAIL]");
                Debug.LogError($"[OP10-DETAIL] positionAfterLoop={writer.Position}");
                Debug.LogError("[OP10-DETAIL] lastBytes=30");
                if (VerbosePacketLogging && writer.Position >= 30)
                {
                    StringBuilder hexAfterLoop = new StringBuilder();
                    for (int byteOffset = writer.Position - 30; byteOffset < writer.Position; byteOffset++)
                    {
                        hexAfterLoop.Append($"{writer.ToArray()[byteOffset]:X2} ");
                    }
                    Debug.LogError(hexAfterLoop.ToString());
                }
                Debug.LogError("[OP10-DETAIL]");

                Debug.LogError($"[OP10-PROFESSION] Writing SkillProfession...");
                int professionStart = writer.Position;

                writer.WriteByte(0x01);
                Debug.LogError($"[OP10-DETAIL] afterWriteByte=0x01 position={writer.Position}");
                if (VerbosePacketLogging) Debug.LogError($"[OP10-DETAIL] bytes={BitConverter.ToString(writer.ToArray(), professionStart, writer.Position - professionStart)}");

                int beforeGCType = writer.Position;
                string profession = savedChar.className?.ToLower() switch
                {
                    "mage" or "warlock" => "skills.professions.Warlock",
                    "ranger" => "skills.professions.Ranger",
                    _ => "skills.professions.Warrior"
                };
                Debug.LogError($"[OP10-PROFESSION] savedChar.className = '{savedChar.className}', sending profession = '{profession}'");
                WriteGCType(writer, profession);
                int afterGCType = writer.Position;
                Debug.LogError($"[OP10-PROFESSION] {profession} written");

                opBytes = (int)(writer.Position - beforeOp);
                Debug.LogError($"[OP10-COMPLETE] Skills: {opBytes} bytes");


                Debug.LogError($"[OP10-DETAIL] afterWriteGCType position={writer.Position}");
                if (VerbosePacketLogging) Debug.LogError($"[OP10-DETAIL] gcTypeBytes={BitConverter.ToString(writer.ToArray(), beforeGCType, afterGCType - beforeGCType)}");

                if (VerbosePacketLogging) Debug.LogError($"[OP10-DETAIL] professionBytes={BitConverter.ToString(writer.ToArray(), professionStart, writer.Position - professionStart)}");

                Debug.LogError($"[OP10-PROFESSION] {profession} written");
                Debug.LogError($"[OP10-COMPLETE] Skills: {opBytes} bytes");
                Debug.LogError($"[CUMULATIVE-OP10] Total after Op10: {writer.Position} bytes");




                var resolvedSpawn = ResolvePlayerSpawnPositionFixed(conn);
                bool preserveAuthoredSpawnHeading = conn.PendingSpawnHeadingOverride;
                conn.PendingSpawnHeadingOverride = false;
                int spawnFixedX = resolvedSpawn.x;
                int spawnFixedY = resolvedSpawn.y;
                int spawnFixedZ = resolvedSpawn.z;
                conn.PlayerPosFixedX = spawnFixedX;
                conn.PlayerPosFixedY = spawnFixedY;
                conn.PlayerPosFixedZ = spawnFixedZ;
                conn.LivePlayerPosFixedX = spawnFixedX;
                conn.LivePlayerPosFixedY = spawnFixedY;
                conn.LivePlayerPosFixedZ = spawnFixedZ;
                conn.LivePlayerHeadingFixed = conn.PlayerHeadingFixed;
                conn.AvatarAggroSampleQueue.Clear();
                conn.AggroSamplePosFixedX = conn.PlayerPosFixedX;
                conn.AggroSamplePosFixedY = conn.PlayerPosFixedY;
                Debug.LogError($"[SPAWN-TRACK] Primed authoritative player position before OP12/combat registration posFixed=({spawnFixedX},{spawnFixedY},{spawnFixedZ}) headingFixed={conn.PlayerHeadingFixed} zone={conn.CurrentZoneName} instance={conn.InstanceId}");
                if (!preserveAuthoredSpawnHeading && !DungeonMazeSpawner.IsProceduralZone(conn.CurrentZoneName) && _zones.TryGetValue(conn.CurrentZoneId, out Zone preOp12Zone) && preOp12Zone.SpawnHeadingFixed != 0)
                    conn.PlayerHeadingFixed = preOp12Zone.SpawnHeadingFixed;
                conn.LivePlayerHeadingFixed = conn.PlayerHeadingFixed;
                conn.MovementGeneration = 0;
                SetReflectedAvatarPosition(conn, conn.PlayerPosFixedX, conn.PlayerPosFixedY, conn.PlayerPosFixedZ, conn.PlayerHeadingFixed, true);
                Debug.LogError("[OP12] action=init-avatar state=start");
                beforeOp = writer.Position;

                writer.WriteByte(0x02);
                writer.WriteUInt16((ushort)avatar.Id);
                Debug.LogError($"[OP12] opcode=0x02 avatarId={avatar.Id}");

                Debug.LogError($"[OP12] Before WorldEntity.WriteInit: position {writer.Position}");
                writer.WriteUInt32(0x04);
                Debug.LogError($"[OP12] WorldEntityFlags: 0x04");

                writer.WriteInt32(spawnFixedX);
                Debug.LogError($"[OP12] After posX: position {writer.Position}");
                writer.WriteInt32(spawnFixedY);
                Debug.LogError($"[OP12] After posY: position {writer.Position}");
                writer.WriteInt32(spawnFixedZ);
                Debug.LogError($"[OP12] After posZ: position {writer.Position}");
                writer.WriteInt32(conn.PlayerHeadingFixed);
                Debug.LogError($"[OP12] After headingFixed ({conn.PlayerHeadingFixed}): position {writer.Position}");
                writer.WriteByte(0x01);
                Debug.LogError($"[OP12] After initFlags: position {writer.Position}");

                writer.WriteUInt16(0);
                Debug.LogError($"[OP12] After Unk1Case (0, was avatar.Id): position {writer.Position}");

                Debug.LogError($"[OP12] Before Unit.WriteInit: position {writer.Position}");
                writer.WriteByte(0x07);
                Debug.LogError($"[OP12] After unitFlags: position {writer.Position}");

                byte unitLevel = (byte)Math.Max(1, Math.Min(255, playerState.Level));
                writer.WriteByte(unitLevel);
                Debug.LogError($"[OP12] After level={unitLevel} persistedLevel={savedChar.level}: position {writer.Position}");

                writer.WriteUInt16(0);
                Debug.LogError($"[OP12] After unk1: position {writer.Position}");
                writer.WriteUInt16(0);
                Debug.LogError($"[OP12] After unk2: position {writer.Position}");

                writer.WriteUInt16((ushort)player.Id);
                Debug.LogError($"[OP12] ownerId={player.Id} previousConnId={conn.ConnId}");


                uint avatarEntitySynchInfoHPValue = GetEntitySynchInfoHPValue(conn);
                uint avatarManaValue = playerState.CurrentManaWire;
                writer.WriteUInt32(avatarEntitySynchInfoHPValue);
                writer.WriteUInt32(avatarManaValue);
                Debug.LogError($"[OP12] HP/Mana init: hp={avatarEntitySynchInfoHPValue} mana={avatarManaValue}");

                Debug.LogError($"[OP12] Before Hero.WriteInit: position {writer.Position}");
                writer.WriteUInt32(playerState.Experience);
                Debug.LogError($"[OP12] After Hero XP({playerState.Experience}): position {writer.Position}");
                Debug.LogError($"[OP12] After Hero uint32(0): position {writer.Position}");

                int _pointsPerLevel = StatPointsPerLevel;
                int _totalAllocated = savedChar.statStrength + savedChar.statAgility
                                    + savedChar.statEndurance + savedChar.statIntellect;
                int _totalAvailable = (playerState.Level - 1) * _pointsPerLevel;
                int _statPtsRemaining = System.Math.Max(0, _totalAvailable - _totalAllocated);
                writer.WriteUInt16((ushort)savedChar.statStrength);
                writer.WriteUInt16((ushort)savedChar.statAgility);
                writer.WriteUInt16((ushort)savedChar.statEndurance);
                writer.WriteUInt16((ushort)savedChar.statIntellect);
                writer.WriteUInt16((ushort)_statPtsRemaining);

                int respecCooldownRemaining = 0;
                if (savedChar.lastRespecTime > 0)
                {
                    int nowSec = checked((int)ServerRuntime.ReadUnixTimeSeconds());
                    int elapsedSec = nowSec - savedChar.lastRespecTime;
                    respecCooldownRemaining = System.Math.Max(0, GetRespecCooldownSeconds() - elapsedSec);
                }
                writer.WriteUInt16((ushort)respecCooldownRemaining);
                Debug.LogError($"[OP12] Stats: STR={savedChar.statStrength} AGI={savedChar.statAgility} END={savedChar.statEndurance} INT={savedChar.statIntellect} pts={_statPtsRemaining} respecTimer={respecCooldownRemaining}s");

                WriteHeroPvpFields(writer, savedChar);

                Debug.LogError($"[OP12] Before Avatar.WriteInit: position {writer.Position}");
                writer.WriteByte(savedChar.face);
                writer.WriteByte(savedChar.hair);
                writer.WriteByte(savedChar.hairColor);
                Debug.LogError($"[OP12] Avatar appearance: Face={savedChar.face}, Hair={savedChar.hair}, HairColor={savedChar.hairColor}");
                Debug.LogError($"[OP12] After Avatar.WriteInit (3 bytes): position {writer.Position}");



                opBytes = (int)(writer.Position - beforeOp);
                Debug.LogError($"[OP12-COMPLETE] Init Avatar: {opBytes} bytes");
                Debug.LogError($"[CUMULATIVE-OP12] Total after Op12: {writer.Position} bytes");
                Debug.LogError(" Operation 12 complete - SPAWN PACKET ENDS HERE!");
                Debug.LogError($"[CUMULATIVE-OP12] Total after Op12: {writer.Position} bytes");
                Debug.LogError(" Operation 12 complete - SPAWN PACKET ENDS HERE!");

                Debug.LogError(" Operation 12 complete - SPAWN PACKET ENDS HERE!");

                Debug.LogError("[OP11] component=UnitBehavior state=start");
                beforeOp = writer.Position;

                var unitBehavior = avatar.Children.FirstOrDefault(c => c.GCClass == "avatar.base.UnitBehavior");

                if (unitBehavior == null)
                {
                    Debug.LogError("[OP11] UnitBehavior missing in avatar children");
                    Debug.LogError(" LoadAvatar() should have created it!");
                    return;
                }

                Debug.LogError($"[OP11] unitBehaviorId={unitBehavior.Id} state=assigned");

                writer.WriteByte(0x32);
                writer.WriteUInt16((ushort)avatar.Id);
                writer.WriteUInt16((ushort)unitBehavior.Id);
                WriteGCType(writer, "avatar.base.UnitBehavior");
                writer.WriteByte(0x01);

                Debug.LogError($"[OP11-HEADER] Component header complete - Entity {avatar.Id}, Component {unitBehavior.Id}");

                int writeInitStart = writer.Position;

                writer.WriteByte(0xFF);
                Debug.LogError($"[OP11-BYTE] Offset {writer.Position - 1}: 0xFF (Behavior marker)");

                writer.WriteByte(0x00);
                Debug.LogError($"[OP11-BYTE] Offset {writer.Position - 1}: 0x00 (Action1 null)");

                writer.WriteByte(0x00);
                Debug.LogError($"[OP11-BYTE] Offset {writer.Position - 1}: 0x00 (Action2 null)");

                writer.WriteByte(0xFF);
                Debug.LogError($"[OP11-BYTE] Offset {writer.Position - 1}: 0xFF (Generation counter 0x7d)");
                byte unitMoverFlags = 0x08;
                writer.WriteByte(unitMoverFlags);
                Debug.LogError($"[OP11-BYTE] Offset {writer.Position - 1}: 0x{unitMoverFlags:X2} (UnitMoverFlags)");

                writer.WriteInt32(conn.PlayerHeadingFixed);
                Debug.LogError($"[OP11-UINT32] Offset {writer.Position - 4}: UnitMover headingFixed = {conn.PlayerHeadingFixed}");

                writer.WriteInt32(conn.PlayerHeadingFixed);
                Debug.LogError($"[OP11-UINT32] Offset {writer.Position - 4}: UnitMover headingFixed2 = {conn.PlayerHeadingFixed}");

                byte waypointFlags = 0x00;
                writer.WriteByte(waypointFlags);
                Debug.LogError($"[OP11-BYTE] Offset {writer.Position - 1}: 0x{waypointFlags:X2} (WaypointFlags)");

                writer.WriteByte(0xFF);
                Debug.LogError($"[OP11-BYTE] Offset {writer.Position - 1}: 0xFF (SessionID)");

                writer.WriteByte(0x00);
                Debug.LogError($"[OP11-BYTE] Offset {writer.Position - 1}: 0x00 (UnitBehaviorUnk1)");

                writer.WriteByte(0x00);
                Debug.LogError($"[OP11-BYTE] Offset {writer.Position - 1}: 0x00 (UnitBehaviorUnk2)");
                opBytes = (int)(writer.Position - beforeOp);
                int writeInitBytes = writer.Position - writeInitStart;

                Debug.LogError($"[OP11-COMPLETE] WriteInit: {writeInitBytes} bytes");
                Debug.LogError($"[OP11-COMPLETE] Total operation: {opBytes} bytes");
                Debug.LogError("[OP11] state=complete");
                Debug.LogError($"[SPAWN-SERIALIZE] packetBytesBeforeEndStream={writer.Position}");

                Debug.LogError($"[SPAWN-TRACK] ");
                Debug.LogError($"[SPAWN-TRACK] ABOUT TO WRITE 0x06 (EndStream pre-connected)");
                Debug.LogError($"[SPAWN-TRACK] conn.UpdateNumber BEFORE 0x06: {conn.UpdateNumber}");
                Debug.LogError($"[SPAWN-TRACK] isZoneTransition: {isZoneTransition}");

                writer.WriteByte(0x06);
                byte[] spawnData = writer.ToArray();
                if (VerbosePacketLogging)
                {
                    Debug.LogError($"[PLAYER-SPAWN-HEX] Size: {spawnData.Length} hex: {BitConverter.ToString(spawnData)}");
                    Debug.LogError("[FULL-SPAWN-PACKET]");
                    StringBuilder fullHex = new StringBuilder();
                    for (int rowOffset = 0; rowOffset < spawnData.Length; rowOffset += 16)
                    {
                        fullHex.Append($"{rowOffset:X8}  ");
                        for (int columnOffset = 0; columnOffset < 16; columnOffset++)
                        {
                            if (rowOffset + columnOffset < spawnData.Length)
                                fullHex.Append($"{spawnData[rowOffset + columnOffset]:X2} ");
                            else
                                fullHex.Append("   ");
                        }
                        fullHex.Append(" |");
                        for (int columnOffset = 0; columnOffset < 16 && rowOffset + columnOffset < spawnData.Length; columnOffset++)
                        {
                            byte byteValue = spawnData[rowOffset + columnOffset];
                            fullHex.Append((byteValue >= 32 && byteValue < 127) ? (char)byteValue : '.');
                        }
                        fullHex.AppendLine("|");
                    }
                    Debug.LogError(fullHex.ToString());
                }
                Debug.LogError($"[PACKET-1] bytes={spawnData.Length}");
                Debug.LogError($"[PACKET-1] lastByte=0x{spawnData[spawnData.Length - 1]:X2} expected=0x06");
                Debug.LogError("[PACKET-1] action=send-spawn-packet");
                SendCompressedA(conn, 0x01, 0x0F, spawnData);
                Debug.LogError("[PACKET-1] spawn packet sent");





                if (savedChar.hotbarSlots != null)
                {
                    string passiveConnectionKey = conn.ConnId.ToString();
                    EnsurePlayerManipulatorMap(passiveConnectionKey);
                    RecalculateHotbarPassiveBonuses(conn, savedChar);
                }

                SendZoneNPCs(conn, conn.CurrentZoneId);
                SendZonePortals(conn, conn.CurrentZoneId);
                SpawnReturnTownPortal(conn);
                SendZoneCheckpoints(conn, conn.CurrentZoneId);
                SendZoneChests(conn, conn.CurrentZoneName);
                SendZoneWorldEntities(conn);
                SendDroppedItemsForZone(conn);

                bool hasBlingSkill = false;
                if (savedChar.hotbarSlots != null)
                    hasBlingSkill = savedChar.hotbarSlots.Any(hotbarSlot => hotbarSlot.skill.IndexOf("BlingGnome", StringComparison.OrdinalIgnoreCase) >= 0
                                                                         || hotbarSlot.skill.IndexOf("SummonBling", StringComparison.OrdinalIgnoreCase) >= 0);
                if (hasBlingSkill)
                {
                    BlingGnomeRuntime.Instance.SetServer(this);
                    BlingGnomeRuntime.Instance.CleanupForZoneTransition(conn.ConnId);
                    if (!BlingGnomeRuntime.Instance.HasGnome(conn.ConnId))
                    {
                        Debug.LogError($"[ZONE-IN] BlingGnome skill found - scheduling tick-owned spawn for {conn.LoginName}");
                        BlingGnomeRuntime.Instance.SpawnGnome(conn,
                            (targetConnection, dest, messageType, data) => SendCompressedA(targetConnection, dest, messageType, data),
                            (targetConnection, message) => SendSystemMessage(targetConnection, message));
                    }
                }

                Debug.LogError($"[ZONE-IN] Sending quest update for zone: {conn.CurrentZoneGcType}");
                if (QuestManager.Instance != null)
                    QuestManager.Instance.SendAvailableQuestUpdateForZone(conn);

                _spawnedAvatarIds[conn.LoginName] = avatar.Id;

                conn.AvatarGcType = avatar.GCClass;
                conn.ClassName = savedChar.className;
                conn.PlayerLevel = playerState.Level;
                ushort currentBehaviorComponentId = (ushort)(unitBehavior?.Id ?? 0);
                conn.BehaviorComponentId = currentBehaviorComponentId;
                conn.UnitBehaviorId = currentBehaviorComponentId;
                conn.SkillsComponentId = (ushort)(skills?.Id ?? 0);
                conn.ManipulatorsComponentId = (ushort)(manipulators?.Id ?? 0);
                var modifiersComponent = avatar.Children?.FirstOrDefault(c => c.GCClass == "Modifiers");
                conn.ModifiersComponentId = (ushort)(modifiersComponent?.Id ?? 0);
                conn.IsSpawned = true;
                conn.LastOutboundHPWire = 0;
                conn.LastOutboundHPSource = null;
                Debug.LogError($"[MULTIPLAYER] Saved spawn data for {conn.LoginName}: avatar={avatar.Id} behavior={conn.BehaviorComponentId}");
                var combatPlayerState = GetPlayerState(conn.ConnId.ToString());
                if (combatPlayerState != null)
                {
                    string combatInstanceKey = GetInstanceZoneKey(conn);
                    CombatPlayer registeredCombatPlayer = CombatRuntime.Instance.RegisterPlayer(avatar.Id, conn.LoginName, combatPlayerState, conn.PlayerPosFixedX, conn.PlayerPosFixedY, conn.PlayerPosFixedZ, combatInstanceKey);
                    if (registeredCombatPlayer == null)
                    {
                        QuarantineCommittedPersistenceSyncFailure(conn, "combat-player-register", new InvalidOperationException("combat player instance identity is missing"));
                        return;
                    }
                    Debug.LogError($"[COMBAT] registerPlayer name='{conn.LoginName}' instance={combatInstanceKey}");
                    CombatRuntime.Instance.RestoreFriendlySummons(registeredCombatPlayer);

                }



                if (unitBehavior != null)
                {
                    Debug.LogError("");
                    Debug.LogError("");
                    Debug.LogError("[PACKET-2] build spawn action + FollowClient");
                    Debug.LogError("");

                    var followWriter = new LEWriter();
                    if (!preserveAuthoredSpawnHeading && !DungeonMazeSpawner.IsProceduralZone(conn.CurrentZoneName) && _zones.TryGetValue(conn.CurrentZoneId, out Zone spawnHeadingZone) && spawnHeadingZone.SpawnHeadingFixed != 0)
                    {
                        conn.PlayerHeadingFixed = spawnHeadingZone.SpawnHeadingFixed;
                        Debug.LogError($"[HEADING] Set headingFixed={spawnHeadingZone.SpawnHeadingFixed} from zone '{spawnHeadingZone.name}' id={conn.CurrentZoneId}");
                    }

                    followWriter.WriteByte(0x07);
                    Debug.LogError($"[PACKET-2] BeginStream: 0x07");

                    int spawnStart = followWriter.Position;
                    followWriter.WriteByte(0x35);
                    followWriter.WriteUInt16((ushort)unitBehavior.Id);
                    followWriter.WriteByte(0x04);
                    followWriter.WriteByte(0x04);
                    followWriter.WriteByte(0xFF);
                    followWriter.WriteInt32(spawnFixedX);
                    followWriter.WriteInt32(spawnFixedY);
                    followWriter.WriteInt32(spawnFixedZ);
                    followWriter.WriteUInt16((ushort)avatar.Id);

                    int spawnSynchPos = followWriter.Position;
                    Debug.LogError($"[SPAWN-ENTITY-SYNCH] Position before 0x02: {spawnSynchPos}");


                    WritePlayerEntitySynchNoCombatFlush(conn, followWriter);

                    Debug.LogError($"[SPAWN-ENTITY-SYNCH] SPAWN ACTION total bytes: {followWriter.Position - spawnStart}");

                    Debug.LogError($"[PACKET-2] SPAWN ACTION: UnitBehavior={unitBehavior.Id:X4}, PosFixed=({spawnFixedX},{spawnFixedY},{spawnFixedZ})");

                    followWriter.WriteByte(0x06);
                    Debug.LogError($"[PACKET-2] EndStream: 0x06 at position {followWriter.Position - 1}");

                    byte[] followData = followWriter.ToArray();

                    if (VerbosePacketLogging)
                    {
                        Debug.LogError($"[PACKET-2-COMPLETE] ");
                        Debug.LogError($"[PACKET-2-COMPLETE] Total packet size: {followData.Length} bytes");
                        Debug.LogError($"[PACKET-2-COMPLETE] Full hex dump:");
                        StringBuilder hexDump = new StringBuilder();
                        for (int byteOffset = 0; byteOffset < followData.Length; byteOffset++)
                        {
                            hexDump.Append($"{followData[byteOffset]:X2} ");
                            if ((byteOffset + 1) % 16 == 0)
                            {
                                hexDump.Append("\n[PACKET-2-COMPLETE]                      ");
                            }
                        }
                        Debug.LogError($"[PACKET-2-COMPLETE]                      {hexDump}");
                        Debug.LogError($"[PACKET-2-COMPLETE] ");
                    }

                    Debug.LogError("[PACKET-2] send begin");
                    SendCompressedA(conn, 0x01, 0x0F, followData);
                    Debug.LogError("[PACKET-2] send complete");
                    Debug.LogError($"[SPAWN-TRACK] After Packet 2, conn.UpdateNumber: {conn.UpdateNumber}");

                    Debug.LogError("[PACKET-3] type=UnitMoverUpdate opcode=0x65");

                    var moveWriter = new LEWriter();
                    moveWriter.WriteByte(0x07);
                    Debug.LogError($"[PACKET-3] BeginStream (0x07)");

                    moveWriter.WriteByte(0x35);
                    moveWriter.WriteUInt16((ushort)unitBehavior.Id);
                    moveWriter.WriteByte(0x65);
                    moveWriter.WriteByte(0x00);
                    moveWriter.WriteByte(0x01);
                    moveWriter.WriteByte(0x03);
                    moveWriter.WriteInt32(conn.PlayerHeadingFixed);
                    moveWriter.WriteInt32(spawnFixedX);
                    moveWriter.WriteInt32(spawnFixedY);

                    int pkt3SynchPos = moveWriter.Position;
                    WritePlayerEntitySynchNoCombatFlush(conn, moveWriter);


                    moveWriter.WriteByte(0x06);

                    byte[] moveData = moveWriter.ToArray();
                    Debug.LogError($"[PACKET-3] Total size: {moveData.Length} bytes");
                    if (VerbosePacketLogging) Debug.LogError($"[PACKET-3] Full hex: {BitConverter.ToString(moveData)}");
                    Debug.LogError("[PACKET-3] send begin");
                    SendCompressedA(conn, 0x01, 0x0F, moveData);
                    Debug.LogError("[PACKET-3] send complete");
                    Debug.LogError($"[SPAWN-TRACK] After Packet 3, conn.UpdateNumber: {conn.UpdateNumber}");
                    QueueInitialClientControl(conn, (ushort)unitBehavior.Id);




                    Debug.LogError("[WELCOME] state=start");
                    var welcomeWriter = new LEWriter();
                    welcomeWriter.WriteByte(0x06);
                    welcomeWriter.WriteByte(0x00);
                    welcomeWriter.WriteByte(0x0d);
                    string rawWelcome = ServerSettings.GetString("welcomeMessage", "Welcome to Dungeon Runners!");
                    string welcomeColor = ServerSettings.GetString("welcomeColor", "");
                    string welcomeMessage = WrapChatColor(rawWelcome, welcomeColor, "") + "\n";
                    foreach (char c in welcomeMessage)
                    {
                        welcomeWriter.WriteByte((byte)c);
                    }
                    welcomeWriter.WriteByte(0x00);
                    SendCompressedA(conn, 0x01, 0x0F, welcomeWriter.ToArray());
                    Debug.LogError("[WELCOME] Sent welcome message after packets 1-3");

                    string motd = ServerSettings.GetString("motd", "");
                    if (!string.IsNullOrEmpty(motd))
                    {
                        string motdColor = ServerSettings.GetString("motdColor", "");
                        var motdWriter = new LEWriter();
                        motdWriter.WriteByte(0x06);
                        motdWriter.WriteByte(0x00);
                        motdWriter.WriteByte(0x0d);
                        string motdMessage = WrapChatColor(motd, motdColor, "") + "\n";
                        foreach (char c in motdMessage)
                            motdWriter.WriteByte((byte)c);
                        motdWriter.WriteByte(0x00);
                        SendCompressedA(conn, 0x01, 0x0F, motdWriter.ToArray());
                        Debug.LogError($"[MOTD] Sent: {motd}");
                    }

                    conn.AllowFlush = false;
                    Debug.LogError("[SPAWN] player materialized; room epoch pending");

                    if (IsPlayerFree(conn.LoginName) && conn.ModifiersId != 0
                        && (!_freePlayerModifierComponentIds.TryGetValue(conn.LoginName, out ushort freePlayerModifierComponentId)
                            || freePlayerModifierComponentId != conn.ModifiersId))
                    {
                        if (SendFreePlayerModifier(conn))
                            _freePlayerModifierComponentIds[conn.LoginName] = conn.ModifiersId;
                    }

                    if (!RestorePersistedQuestXPBonusModifier(conn))
                    {
                        QuarantineCommittedPersistenceSyncFailure(conn, "quest-xp-modifier-restore", new InvalidOperationException("quest XP modifier durable restore failed"));
                        return;
                    }

                    if (_unitContainer != null && !_unitContainer.RestorePersistedConsumableModifiers(conn))
                    {
                        QuarantineCommittedPersistenceSyncFailure(conn, "consumable-modifier-restore", new InvalidOperationException("consumable modifier durable restore failed"));
                        return;
                    }

                    try
                    {
                        _unitContainer?.RebindAndReplayConnectionRuntime(conn);
                        CombatRuntime.Instance.ReplayPlayerAttributeModifiers(avatar.Id, "zone-spawn");
                    }
                    catch (Exception ex)
                    {
                        QuarantineCommittedPersistenceSyncFailure(conn, "attribute-modifier-zone-replay", ex);
                        return;
                    }

                    conn.PlayerPosFixedX = spawnFixedX;
                    conn.PlayerPosFixedY = spawnFixedY;
                    conn.PlayerPosFixedZ = spawnFixedZ;
                    conn.HasLivePlayerPosition = true;
                    conn.LivePlayerPosFixedX = spawnFixedX;
                    conn.LivePlayerPosFixedY = spawnFixedY;
                    conn.LivePlayerPosFixedZ = spawnFixedZ;
                    conn.LivePlayerHeadingFixed = conn.PlayerHeadingFixed;
                    conn.LivePlayerMovingThisFrame = false;
                    SetReflectedAvatarPosition(conn, conn.PlayerPosFixedX, conn.PlayerPosFixedY, conn.PlayerPosFixedZ, conn.PlayerHeadingFixed, true);
                    conn.SessionID = 0xFF;
                    conn.MovementGeneration = 0;

                    conn.TickUpdatesActive = false;

                    try { SavePlayerLevel(conn); }
                    catch (Exception ex) { Debug.LogError($"[SPAWN] Login save failed: {ex.Message}"); }
                }
                else
                {
                    Debug.LogError("[SPAWN] UnitBehavior missing; cannot send spawn/followclient");
                }

                Debug.LogError($"");
                Debug.LogError("");
                Debug.LogError("[SPAWN] SendPlayerEntitySpawn complete");

                Debug.LogError($"[MULTIPLAYER] avatar and shared-entity snapshot staged for room epoch login='{conn.LoginName}'");
                Debug.LogError($"   PACKET 1: Spawn ({spawnData.Length} bytes) ending pre-connected with 0x06");
                Debug.LogError($"   PACKET 2: Spawn Action + FollowClient ending with 0x06");
                Debug.LogError($"   PACKET 3: UnitMoverUpdate (0x65)");
                Debug.LogError($"   WELCOME: Sent after all packets");
                Debug.LogError("");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SEND-PLAYER-ENTITY-SPAWN] state=failed message='{ex.Message}'");
                Debug.LogError($"[SEND-PLAYER-ENTITY-SPAWN] stack='{ex.StackTrace}'");
            }
        }





        private string HexDump(byte[] bytes, int offset, int length)
        {
            var sb = new System.Text.StringBuilder();
            int bytesPerLine = 16;

            for (int rowOffset = 0; rowOffset < length; rowOffset += bytesPerLine)
            {
                sb.Append($"{(offset + rowOffset):X8}  ");

                for (int columnOffset = 0; columnOffset < bytesPerLine; columnOffset++)
                {
                    if (rowOffset + columnOffset < length)
                    {
                        sb.Append($"{bytes[offset + rowOffset + columnOffset]:X2} ");
                    }
                    else
                    {
                        sb.Append("   ");
                    }

                    if (columnOffset == 7) sb.Append(" ");
                }

                sb.Append(" |");

                for (int columnOffset = 0; columnOffset < bytesPerLine && rowOffset + columnOffset < length; columnOffset++)
                {
                    byte byteValue = bytes[offset + rowOffset + columnOffset];
                    sb.Append(byteValue >= 32 && byteValue < 127 ? (char)byteValue : '.');
                }

                sb.Append("|");

                if (rowOffset + bytesPerLine < length)
                {
                    sb.AppendLine();
                }
            }

            return sb.ToString();
        }



        private void ReassignEntityIDs(GCObject player)
        {
            player.Id = AllocateGeneralEntityId();

            foreach (var child in player.Children)
            {
                child.Id = AllocateGeneralEntityId();

                if (child.Children != null)
                {
                    foreach (var grandchild in child.Children)
                    {
                        grandchild.Id = AllocateGeneralEntityId();
                    }
                }
            }
        }

        private uint GetClientId24(int connId)
        {
            return _peerId24.TryGetValue(connId, out var id) ? id : 0u;
        }

        private void SendSocialViaAuth(RRConnection conn, byte dest, byte messageType, byte[] innerData)
        {
            SendCompressedA(conn, dest, messageType, innerData);
        }

        private void SendSocialViaQueue(RRConnection conn, byte dest, byte messageType, byte[] innerData)
        {
            SendCompressedA(conn, dest, messageType, innerData);
        }

        private void OnQueueStreamReady(string username)
        {
            foreach (var conn in GetConnectionInsertionOrderSnapshot())
            {
                if (conn.IsConnected && conn.LoginName != null &&
                    conn.LoginName.Equals(username, StringComparison.OrdinalIgnoreCase))
                {
                    if (_selectedCharacter.TryGetValue(conn.LoginName, out var selectedCharacter) && selectedCharacter.Name != null)
                    {
                        Debug.LogError($"[QUEUE-CONNECTION] Queue ready for {username} - resending social init");
                        SocialRuntime.Instance.SendLoginSocialInit(conn, selectedCharacter.Name, SendSocialViaAuth);
                        PosseRuntime.Instance.SendConnectionNotification(conn, true, SendSocialViaAuth);
                        SendPosseStateForCharacter(conn, selectedCharacter.Id, SendSocialViaAuth);
                        try { PosseRuntime.Instance.NotifyMemberStateChange(selectedCharacter.Id, this); }
                        catch (Exception ex2) { Debug.LogError($"[POSSE] login-notify (OnQueueStreamReady) failed: {ex2.Message}"); }
                    }
                    break;
                }
            }
        }

        private void SendCompressedE(RRConnection conn, byte[] innerData)
        {
            try
            {
                if (conn == null || innerData == null || innerData.Length == 0)
                {
                    Debug.LogError("[SEND-COMPRESSEDE] dropped empty packet");
                    return;
                }
                byte[] compressed = ZlibUtil.Deflate(innerData);
                int compressedLen = compressed.Length + 12;

                var writer = new LEWriter();
                writer.WriteByte(0x0E);
                writer.WriteUInt24((int)MSG_DEST);
                writer.WriteUInt24(compressedLen);
                writer.WriteByte(0x00);
                writer.WriteUInt24((int)MSG_SOURCE);
                writer.WriteByte(0x01);
                writer.WriteByte(0x00);
                writer.WriteByte(0x01);
                writer.WriteByte(0x00);
                writer.WriteByte(0x00);
                writer.WriteUInt32((uint)innerData.Length);
                writer.WriteBytes(compressed);

                byte[] data = writer.ToArray();
                lock (conn.SendLock)
                {
                    conn.Stream.Write(data, 0, data.Length);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SEND-COMPRESSED-E] state=failed message='{ex.Message}'");
            }
        }



        private void SendCompressedA(RRConnection conn, byte dest, byte messageType, byte[] innerData)
        {
            SendCompressedA(conn, dest, messageType, innerData, InferSendCompressedAEntitySynchInfoContext(conn, innerData), "SEND-COMPRESSEDA");
        }

        private EntitySynchInfoContext InferSendCompressedAEntitySynchInfoContext(RRConnection conn, byte[] innerData)
        {
            if (innerData == null || innerData.Length < 2) return EntitySynchInfoContext.PlayerActionResponse;
            if (innerData[0] == 0x07 && innerData[1] == 0x0D) return EntitySynchInfoContext.WorldInterval;
            if (innerData[0] == 0x07 && (innerData[1] == 0x01 || innerData[1] == 0x02 || innerData[1] == 0x32)) return EntitySynchInfoContext.EntityInitPrimer;
            for (int byteOffset = 0; byteOffset + 3 < innerData.Length; byteOffset++)
            {
                if (innerData[byteOffset] != 0x35) continue;
                ushort componentId = (ushort)(innerData[byteOffset + 1] | (innerData[byteOffset + 2] << 8));
                byte subtype = innerData[byteOffset + 3];
                if (subtype == 0x64)
                    return IsAvatarOrAvatarComponentId(conn, componentId) ? EntitySynchInfoContext.ControlAck : EntitySynchInfoContext.MonsterAction;
                if (subtype == 0x65)
                    return IsAvatarOrAvatarComponentId(conn, componentId) ? EntitySynchInfoContext.MoverAck : EntitySynchInfoContext.MonsterMove;
                if (subtype == 0x04) return EntitySynchInfoContext.MonsterAction;
                if (subtype == 0x01) return EntitySynchInfoContext.PlayerActionResponse;
            }
            return EntitySynchInfoContext.PlayerActionResponse;
        }

        private bool SendCompressedA(RRConnection conn, byte dest, byte messageType, byte[] innerData, EntitySynchInfoContext entitySynchInfoContext, string packetName)
        {
            if (IsPendingRoomQueueableClientEntityStream(conn, dest, messageType, innerData))
                return QueueClientEntityStream(conn, innerData);
            if (IsLiveQueueableClientEntityStream(conn, dest, messageType, innerData))
                return QueueClientEntityStream(conn, innerData);
            return SendCompressedAImmediate(conn, dest, messageType, innerData, entitySynchInfoContext, packetName);
        }

        private bool SendCompressedAImmediate(RRConnection conn, byte dest, byte messageType, byte[] innerData, EntitySynchInfoContext entitySynchInfoContext, string packetName)
        {
            try
            {
                if (conn == null || innerData == null || innerData.Length == 0)
                {
                    Debug.LogError($"[SEND-COMPRESSEDA] dropped empty packet dest=0x{dest:X2} type=0x{messageType:X2}");
                    return false;
                }
                byte[] compressed = ZlibUtil.Deflate(innerData);
                uint peer = GetClientId24(conn.ConnId);
                var writer = new LEWriter();
                writer.WriteByte(0x0A);
                writer.WriteUInt24((int)(peer & 0xFFFFFFu));
                writer.WriteUInt32((uint)(compressed.Length + 7));
                writer.WriteByte(dest);
                writer.WriteByte(messageType);
                writer.WriteByte(0x00);
                writer.WriteUInt32((uint)innerData.Length);
                writer.WriteBytes(compressed);
                byte[] data = writer.ToArray();
                lock (conn.SendLock)
                {
                    conn.Stream.Write(data, 0, data.Length);
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SEND-COMPRESSED-A] state=failed message='{ex.Message}'");
                return false;
            }
        }

        private static bool IsLiveQueueableClientEntityStream(RRConnection conn, byte dest, byte messageType, byte[] innerData)
        {
            if (conn == null
                || !conn.IsConnected
                || !conn.IsSpawned
                || !conn.AllowFlush
                || dest != 0x01
                || messageType != 0x0F
                || innerData == null
                || innerData.Length < 2
                || innerData[innerData.Length - 1] != 0x06)
                return false;
            return innerData[0] == 0x07 || IsClientEntityManagerStreamOpcode(innerData[0]);
        }

        private bool IsPendingRoomQueueableClientEntityStream(RRConnection conn, byte dest, byte messageType, byte[] innerData)
        {
            if (conn == null
                || !conn.IsConnected
                || !_pendingRoomClientTokens.ContainsKey(conn.ConnId)
                || dest != 0x01
                || messageType != 0x0F
                || innerData == null
                || innerData.Length < 2
                || innerData[innerData.Length - 1] != 0x06)
                return false;
            return innerData[0] == 0x07 || IsClientEntityManagerStreamOpcode(innerData[0]);
        }

        private static bool IsClientEntityManagerStreamOpcode(byte opcode)
        {
            return opcode == 0x01
                || opcode == 0x02
                || opcode == 0x03
                || opcode == 0x05
                || opcode == 0x08
                || opcode == 0x1E
                || opcode == 0x1F
                || opcode == 0x20
                || opcode == 0x32
                || opcode == 0x33
                || opcode == 0x35
                || opcode == 0x36;
        }

        private static byte[] StripClientEntityStreamEnvelope(byte[] data)
        {
            if (data == null || data.Length == 0)
                return Array.Empty<byte>();
            int start = data[0] == 0x07 ? 1 : 0;
            int end = data.Length;
            if (end > start && data[end - 1] == 0x06)
                end--;
            int length = end - start;
            if (length <= 0)
                return Array.Empty<byte>();
            if (start == 0 && length == data.Length)
                return data;
            var inner = new byte[length];
            Array.Copy(data, start, inner, 0, length);
            return inner;
        }

        public void SendToClient(RRConnection conn, byte[] data)
        {
            SendCompressedA(conn, 0x01, 0x0F, data);
        }


        [System.Serializable]
        public class Zone
        {
            public uint id;
            public string name;
            public string gcType;
            public int SpawnFixedX;
            public int SpawnFixedY;
            public int SpawnFixedZ;
            public int SpawnHeadingFixed;
            public string respawnZone;
            public string respawnSpawnPoint;
            public int exploredBitCount;
            public uint Id => id;
            public string Name => name;
        }

        [System.Serializable]
        public class ZoneList
        {
            public List<Zone> zones;

        }
        private List<ActiveQuest> ConvertToActiveQuests(List<SavedQuest> saved)
        {
            if (saved == null) return new List<ActiveQuest>();
            return saved.Select(sq => new ActiveQuest
            {
                QuestId = sq.questId,
                QuestGiverId = sq.questGiverId,
                AcceptedAt = DateTime.TryParse(sq.acceptedAt, out var dt) ? dt : ServerRuntime.ReadUtc(),
                Objectives = sq.objectives?.Select(o => new QuestProgress
                {
                    ObjectiveName = o.objectiveName,
                    Type = o.type,
                    Target = o.target,
                    Label = o.label,
                    Required = o.required,
                    Current = o.current
                }).ToList() ?? new List<QuestProgress>()
            }).ToList();
        }



        public Dictionary<int, RRConnection> GetConnections() => _connections;

        public List<string> GetZoneNames()
        {
            return _zones.Values.Select(z => z.name).OrderBy(n => n).ToList();
        }

        public SavedCharacter GetSavedCharacterForConn(RRConnection conn)
        {
            return GetActiveCharacter(conn);
        }

        public enum SkillGrantRuntimeResult
        {
            AlreadyKnown,
            CommitFailed,
            Committed
        }

        public SkillGrantRuntimeResult GrantSkillRuntime(RRConnection conn, string skillGcClass, bool deferMaterialization = false)
        {
            if (conn == null || string.IsNullOrEmpty(skillGcClass)) return SkillGrantRuntimeResult.CommitFailed;

            var savedChar = GetSavedCharacterForConn(conn)?.DeepClone();
            if (savedChar == null)
            {
                Debug.LogError($"[GRANT-SKILL] No saved character for {conn.LoginName}");
                return SkillGrantRuntimeResult.CommitFailed;
            }

            string connKey = conn.ConnId.ToString();

            bool alreadyHas = false;
            if (_playerSkillLevels.TryGetValue(connKey, out var existingLevels))
                alreadyHas = existingLevels.ContainsKey(skillGcClass);
            if (!alreadyHas && savedChar.skills != null)
                alreadyHas = savedChar.skills.Any(s => string.Equals(s, skillGcClass, StringComparison.OrdinalIgnoreCase));

            if (alreadyHas)
            {
                Debug.LogError($"[GRANT-SKILL] {conn.LoginName} already has {skillGcClass}");
                return SkillGrantRuntimeResult.AlreadyKnown;
            }

            if (savedChar.skills == null)
                savedChar.skills = new List<string>();
            savedChar.skills.Add(skillGcClass);
            savedChar.SetSkillLevel(skillGcClass, 1);
            if (!TrySaveCharacterForConn(conn, savedChar, "grant-skill"))
            {
                savedChar.skills.RemoveAll(skill => string.Equals(skill, skillGcClass, StringComparison.OrdinalIgnoreCase));
                savedChar.skillLevels.RemoveAll(entry => string.Equals(entry.skill, skillGcClass, StringComparison.OrdinalIgnoreCase));
                return SkillGrantRuntimeResult.CommitFailed;
            }

            if (deferMaterialization)
                return SkillGrantRuntimeResult.Committed;
            MaterializeCommittedSkillGrantRuntime(conn, skillGcClass);
            return SkillGrantRuntimeResult.Committed;
        }

        public bool MaterializeCommittedSkillGrantRuntime(RRConnection conn, string skillGcClass)
        {
            if (conn == null || string.IsNullOrWhiteSpace(skillGcClass))
                return false;
            try
            {
            string connKey = conn.ConnId.ToString();
            if (!_playerSkillLevels.ContainsKey(connKey))
                _playerSkillLevels[connKey] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _playerSkillLevels[connKey][skillGcClass] = 1;
            int lastDot = skillGcClass.LastIndexOf('.');
            if (lastDot >= 0)
                _playerSkillLevels[connKey][skillGcClass.Substring(lastDot + 1)] = 1;

            ushort skillsCid = 0;
            _playerSkillsComponentId.TryGetValue(connKey, out skillsCid);

            Debug.LogError("[GRANT-SKILL-DETAIL]");
            Debug.LogError($"[GRANT-SKILL-DETAIL] connKey='{connKey}' login='{conn.LoginName}'");
            Debug.LogError($"[GRANT-SKILL-DETAIL] skillsCid=0x{skillsCid:X4} ({skillsCid})");
            Debug.LogError($"[GRANT-SKILL-DETAIL] unitContainerId=0x{conn.UnitContainerId:X4}");
            Debug.LogError($"[GRANT-SKILL-DETAIL] avatarId={conn.Avatar?.Id ?? 0}");
            Debug.LogError($"[GRANT-SKILL-DETAIL] skillGcClass='{skillGcClass}' len={skillGcClass.Length}");
            Debug.LogError($"[GRANT-SKILL-DETAIL] skillGcClassBytes={BitConverter.ToString(System.Text.Encoding.ASCII.GetBytes(skillGcClass))}");
            if (conn.Avatar?.Children != null)
            {
                var skillsNode = conn.Avatar.Children.FirstOrDefault(c => c.GCClass == "avatar.base.skills");
                Debug.LogError($"[GRANT-SKILL-DETAIL] avatar.base.skills id={skillsNode?.Id ?? 0} skillsCid={skillsCid}");
            }

            if (skillsCid != 0)
            {
                uint currentGold = 0;
                try
                {
                    var savedCharacter = GetSavedCharacterForConn(conn);
                    if (savedCharacter != null) currentGold = savedCharacter.gold;
                }
                catch { }

                var grantSkillMessage = new LEWriter();
                grantSkillMessage.WriteByte(0x07);

                grantSkillMessage.WriteByte(0x35);
                grantSkillMessage.WriteUInt16(skillsCid);
                grantSkillMessage.WriteByte(0x33);
                grantSkillMessage.WriteUInt32(currentGold);
                if (!WritePlayerEntitySynch(conn, grantSkillMessage))
                    throw new InvalidOperationException("Skill grant gold synchronization failed");

                grantSkillMessage.WriteByte(0x35);
                grantSkillMessage.WriteUInt16(skillsCid);
                grantSkillMessage.WriteByte(0x32);
                grantSkillMessage.WriteByte(0xFF);
                grantSkillMessage.WriteCString(skillGcClass);
                grantSkillMessage.WriteByte(0x01);
                if (!WritePlayerEntitySynch(conn, grantSkillMessage))
                    throw new InvalidOperationException("Skill grant component synchronization failed");

                grantSkillMessage.WriteByte(0x06);

                byte[] packet = grantSkillMessage.ToArray();
                Debug.LogError($"[GRANT-SKILL-HEX] ({packet.Length}b) {BitConverter.ToString(packet)}");
                Debug.LogError($"[GRANT-SKILL-ANNOT] 07 | 35 cid=0x{skillsCid:X4} 33 gold={currentGold} 00 | 35 cid=0x{skillsCid:X4} 32 FF \"{skillGcClass}\"+00 level=1 00 | 06");

                SendCompressedE(conn, packet);
                Debug.LogError($"[GRANT-SKILL] Sent combined 0x33+0x32 (trainer-shape) for '{skillGcClass}'");
            }
            else
            {
                Debug.LogError($"[GRANT-SKILL]  skillsCid=0 - Skills component ID not captured. Skill saved to DB but won't appear until zone.");
            }

            bool isPassive = skillGcClass.ToLower().Contains("passive") || skillGcClass.ToLower().Contains("trait");
            if (!isPassive)
            {
                var manip = conn.Avatar?.Children?.FirstOrDefault(c => c.GCClass == "Manipulators");
                if (manip != null)
                {
                    var newSkill = new GCObject
                    {
                        GCClass = skillGcClass,
                        DFCClass = "ActiveSkill",
                        Name = skillGcClass,
                        Id = AllocateGeneralEntityId()
                    };
                    manip.AddChild(newSkill);
                    if (_playerSkillSlots.TryGetValue(connKey, out var slotMap))
                        slotMap[skillGcClass] = (uint)newSkill.Id;
                    if (_playerManipMap.ContainsKey(connKey))
                        SetPlayerManipulator(connKey, AllocatePlayerManipulatorId(connKey), skillGcClass);
                    Debug.LogError($"[GRANT-SKILL] Added '{skillGcClass}' to Manipulators, eid={newSkill.Id}");
                }
            }

            Debug.LogError($"[GRANT-SKILL]  Granted '{skillGcClass}' to {conn.LoginName}");
            return true;
            }
            catch (Exception ex)
            {
                QuarantineCommittedPersistenceSyncFailure(conn, "grant-skill-runtime", ex);
                return false;
            }
        }

        public RRConnection FindConnectionByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            List<RRConnection> connections = GetConnectionInsertionOrderSnapshot();
            foreach (RRConnection conn in connections)
            {
                if (conn.IsConnected && conn.LoginName != null
                    && _selectedCharacter.TryGetValue(conn.LoginName, out GCObject selectedCharacter)
                    && selectedCharacter.Name != null
                    && selectedCharacter.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    if (conn != null) return conn;
            }
            return connections.FirstOrDefault(c =>
                c.IsConnected && !string.IsNullOrEmpty(c.LoginName) &&
                c.LoginName.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        public RRConnection FindConnectionById(int connId)
        {
            _connections.TryGetValue(connId, out var conn);
            return conn;
        }

        public void SetPlayerMembershipPublic(string loginName, bool isFree) { SetPlayerMembership(loginName, isFree); }
        public bool IsPlayerFreePublic(string loginName) { return IsPlayerFree(loginName); }
        public Dictionary<string, bool> GetAllMembershipsPublic()
        {
            var result = new Dictionary<string, bool>(System.StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var db = Database.GameDatabase.GetConnection())
                using (var reader = Database.GameDatabase.ExecuteReader(db, "SELECT username, is_member FROM accounts ORDER BY id"))
                {
                    while (reader.Read())
                        result[reader.GetString(0)] = reader.GetInt32(1) == 0;
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[MEMBER] getAll state=failed message='{ex.Message}'");
            }
            return result;
        }

        public uint GetCharSqlIdPublic(RRConnection conn) => GetCharSqlId(conn);
    }
}
