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

namespace DungeonRunners.Networking
{
    public partial class GameServer
    {
        private readonly object _generalEntityIdGate = new object();
        private int _nextGeneralEntityId = GENERAL_ENTITY_ID_MIN;

        private uint AllocateGeneralEntityId()
        {
            lock (_generalEntityIdGate)
            {
                int range = GENERAL_ENTITY_ID_MAX - GENERAL_ENTITY_ID_MIN + 1;
                int candidate = _nextGeneralEntityId;
                if (candidate < GENERAL_ENTITY_ID_MIN || candidate > GENERAL_ENTITY_ID_MAX)
                    candidate = GENERAL_ENTITY_ID_MIN;
                for (int attempt = 0; attempt < range; attempt++)
                {
                    ushort wireId = checked((ushort)candidate);
                    if (NetworkEntityIdRegistry.TryReserve(NetworkEntityIdDomain.General, wireId))
                    {
                        _nextGeneralEntityId = candidate < GENERAL_ENTITY_ID_MAX ? candidate + 1 : GENERAL_ENTITY_ID_MIN;
                        return wireId;
                    }
                    candidate = candidate < GENERAL_ENTITY_ID_MAX ? candidate + 1 : GENERAL_ENTITY_ID_MIN;
                }
            }
            throw new InvalidOperationException("General entity id range is exhausted");
        }

        private void SetPreferredGeneralEntityId(uint preferredEntityId)
        {
            lock (_generalEntityIdGate)
            {
                _nextGeneralEntityId = preferredEntityId >= GENERAL_ENTITY_ID_MIN && preferredEntityId <= GENERAL_ENTITY_ID_MAX
                    ? checked((int)preferredEntityId)
                    : GENERAL_ENTITY_ID_MIN;
            }
        }

        private uint PeekGeneralEntityIdCursor()
        {
            lock (_generalEntityIdGate)
                return checked((uint)_nextGeneralEntityId);
        }

        private const byte LOOT_ID_PENDING = 1;
        private const byte LOOT_ID_ACTIVE = 2;
        private const byte LOOT_ID_RETIRED = 3;
        private readonly Dictionary<ushort, byte> _lootEntityIdStates = new Dictionary<ushort, byte>();
        private int _nextLootEntityId = LOOT_ID_MIN;
        public ushort GetNextLootEntityId()
        {
            int range = LOOT_ID_MAX - LOOT_ID_MIN + 1;
            lock (_droppedItems)
            {
                int candidate = _nextLootEntityId;
                if (candidate < LOOT_ID_MIN || candidate > LOOT_ID_MAX)
                    candidate = LOOT_ID_MIN;
                for (int attempt = 0; attempt < range; attempt++)
                {
                    ushort wireId = (ushort)candidate;
                    if (_lootEntityIdStates.TryGetValue(wireId, out byte state)
                        && state == LOOT_ID_RETIRED
                        && !_droppedItems.ContainsKey(wireId)
                        && !CombatRuntime.Instance.IsItemObjectEntity(wireId))
                    {
                        NetworkEntityIdRegistry.Release(NetworkEntityIdDomain.Loot, wireId);
                        _lootEntityIdStates.Remove(wireId);
                    }
                    if (!_lootEntityIdStates.ContainsKey(wireId)
                        && !_droppedItems.ContainsKey(wireId)
                        && NetworkEntityIdRegistry.TryReserve(NetworkEntityIdDomain.Loot, wireId))
                    {
                        _lootEntityIdStates.Add(wireId, LOOT_ID_PENDING);
                        _nextLootEntityId = candidate < LOOT_ID_MAX ? candidate + 1 : LOOT_ID_MIN;
                        return wireId;
                    }
                    candidate = candidate < LOOT_ID_MAX ? candidate + 1 : LOOT_ID_MIN;
                }
            }
            throw new InvalidOperationException("Loot entity id range is exhausted");
        }

        private void ActivateLootEntityId(ushort entityId)
        {
            lock (_droppedItems)
            {
                if (entityId < LOOT_ID_MIN || entityId > LOOT_ID_MAX
                    || !_lootEntityIdStates.TryGetValue(entityId, out byte state)
                    || state != LOOT_ID_PENDING
                    || _droppedItems.ContainsKey(entityId))
                    throw new InvalidOperationException($"Loot entity id 0x{entityId:X4} is not an exclusive pending reservation");
                _lootEntityIdStates[entityId] = LOOT_ID_ACTIVE;
            }
        }

        private void RetireLootEntityId(ushort entityId)
        {
            lock (_droppedItems)
            {
                if (_lootEntityIdStates.TryGetValue(entityId, out byte state) && state == LOOT_ID_ACTIVE)
                    _lootEntityIdStates[entityId] = LOOT_ID_RETIRED;
            }
        }

        private void CancelLootEntityIdReservation(ushort entityId)
        {
            lock (_droppedItems)
            {
                if (_lootEntityIdStates.TryGetValue(entityId, out byte state) && state == LOOT_ID_PENDING)
                {
                    _lootEntityIdStates.Remove(entityId);
                    NetworkEntityIdRegistry.Release(NetworkEntityIdDomain.Loot, entityId);
                }
            }
        }


        public List<(ushort entityId, DroppedItemInfo info)> GetDroppedItemsNearFixed(string zone, uint instanceId, int fixedX, int fixedY, int fixedZ, int radiusFixed)
        {
            var result = new List<(ushort, DroppedItemInfo)>();
            long radiusSq = ((long)radiusFixed * radiusFixed) >> 8;
            lock (_droppedItems)
            {
                foreach (ushort entityId in _droppedItemOrder)
                {
                    if (!_droppedItems.TryGetValue(entityId, out DroppedItemInfo info))
                        continue;
                    if (info.Zone != zone || info.InstanceId != instanceId) continue;
                    long dx = (long)info.PosFixedX - fixedX;
                    long dy = (long)info.PosFixedY - fixedY;
                    long dz = (long)info.PosFixedZ - fixedZ;
                    long distanceSq = ((dx * dx) >> 8) + ((dy * dy) >> 8) + ((dz * dz) >> 8);
                    if (distanceSq <= radiusSq)
                        result.Add((entityId, info));
                }
            }
            return result;
        }

        public List<(ushort entityId, DroppedItemInfo info)> GetBlingItemsNearFixed(RRConnection owner,
            int fixedX, int fixedY, int fixedZ, int radiusFixed)
        {
            var result = new List<(ushort entityId, DroppedItemInfo info)>();
            if (owner == null || radiusFixed < 0)
                return result;
            int minX = (int)(((long)fixedX - radiusFixed) >> 15);
            int maxX = (int)(((long)fixedX + radiusFixed) >> 15);
            int minY = (int)(((long)fixedY - radiusFixed) >> 15);
            int maxY = (int)(((long)fixedY + radiusFixed) >> 15);
            long radiusSquared = ((long)radiusFixed * radiusFixed) >> 8;
            var cells = new Dictionary<(int X, int Y), List<(ushort entityId, DroppedItemInfo info)>>();
            lock (_droppedItems)
            {
                for (int index = _droppedItemOrder.Count - 1; index >= 0; index--)
                {
                    ushort entityId = _droppedItemOrder[index];
                    if (!_droppedItems.TryGetValue(entityId, out DroppedItemInfo item)
                        || !DroppedItemMatchesConnection(owner, item))
                        continue;
                    int x = item.PosFixedX >> 15;
                    int y = item.PosFixedY >> 15;
                    if (x < minX || x > maxX || y < minY || y > maxY)
                        continue;
                    long dx = (long)item.PosFixedX - fixedX;
                    long dy = (long)item.PosFixedY - fixedY;
                    long dz = (long)item.PosFixedZ - fixedZ;
                    long distanceSquared = ((dx * dx) >> 8) + ((dy * dy) >> 8) + ((dz * dz) >> 8);
                    if (distanceSquared > radiusSquared)
                        continue;
                    if (!cells.TryGetValue((x, y), out var cell))
                    {
                        cell = new List<(ushort entityId, DroppedItemInfo info)>();
                        cells.Add((x, y), cell);
                    }
                    cell.Add((entityId, item));
                }
            }
            for (int x = minX; x <= maxX; x++)
                for (int y = minY; y <= maxY; y++)
                    if (cells.TryGetValue((x, y), out var cell))
                        result.AddRange(cell);
            return result;
        }

        public bool TryGetBlingDroppedItem(ushort entityId, out DroppedItemInfo info)
        {
            lock (_droppedItems)
                return _droppedItems.TryGetValue(entityId, out info);
        }

        private readonly HashSet<ushort> _droppedGoldAcquisitions = new HashSet<ushort>();

        private bool TryAcquireDroppedGoldTransaction(RRConnection conn, ushort entityId, uint goldAmount, string source, out DroppedItemInfo info, out uint updatedGold, int conversionRatioFixed = 0)
        {
            info = null;
            updatedGold = 0;
            if (conn == null || goldAmount == 0 || goldAmount > int.MaxValue)
                return false;
            lock (_droppedItems)
            {
                if (!_droppedItems.TryGetValue(entityId, out info)
                    || info == null
                    || !DroppedItemMatchesConnection(conn, info)
                    || !CanAcquireDroppedItem(conn, entityId, info))
                    return false;
                if (conversionRatioFixed > 0)
                {
                    if (!BlingGnomeRuntime.Instance.TryPrepareConversionItem(conn, info, conversionRatioFixed, out uint expectedGold)
                        || expectedGold != goldAmount)
                        return false;
                }
                else if (!info.IsGoldDrop || info.GoldAmount != goldAmount)
                    return false;
                if (!_droppedGoldAcquisitions.Add(entityId))
                    return false;
            }
            SavedCharacter activeCharacter = GetActiveCharacter(conn);
            if (activeCharacter == null || activeCharacter.id == 0)
            {
                lock (_droppedItems) _droppedGoldAcquisitions.Remove(entityId);
                return false;
            }
            if (CharacterRepository.HasPendingFullSnapshot(activeCharacter.id))
            {
                Debug.LogError($"[DROP-GOLD] transaction blocked source={source ?? "unknown"} characterId={activeCharacter.id} entity=0x{entityId:X4} reason=pending-full-snapshot");
                lock (_droppedItems) _droppedGoldAcquisitions.Remove(entityId);
                return false;
            }
            try
            {
                using (var db = DungeonRunners.Database.GameDatabase.GetConnection())
                using (var transaction = db.BeginTransaction())
                {
                    if (info.DbId > 0)
                    {
                        using (var deleteDrop = db.CreateCommand())
                        {
                            deleteDrop.Transaction = transaction;
                            deleteDrop.CommandText = "DELETE FROM dropped_items WHERE id = @id";
                            deleteDrop.Parameters.AddWithValue("@id", info.DbId);
                            if (deleteDrop.ExecuteNonQuery() != 1)
                                throw new InvalidDataException($"Persistent drop {info.DbId} was not deleted exactly once");
                        }
                    }
                    using (var updateGold = db.CreateCommand())
                    {
                        updateGold.Transaction = transaction;
                        updateGold.CommandText = "UPDATE characters SET gold = gold + @amount WHERE id = @id AND gold >= 0 AND gold <= @maxCurrent";
                        updateGold.Parameters.AddWithValue("@amount", (int)goldAmount);
                        updateGold.Parameters.AddWithValue("@id", (int)activeCharacter.id);
                        updateGold.Parameters.AddWithValue("@maxCurrent", int.MaxValue - (int)goldAmount);
                        if (updateGold.ExecuteNonQuery() != 1)
                            throw new InvalidDataException($"Character {activeCharacter.id} gold increment was not applied exactly once");
                    }
                    using (var readGold = db.CreateCommand())
                    {
                        readGold.Transaction = transaction;
                        readGold.CommandText = "SELECT gold FROM characters WHERE id = @id";
                        readGold.Parameters.AddWithValue("@id", (int)activeCharacter.id);
                        object persistedGold = readGold.ExecuteScalar();
                        if (persistedGold == null || persistedGold == DBNull.Value)
                            throw new InvalidDataException($"Character {activeCharacter.id} gold is missing after increment");
                        long persistedGoldValue = Convert.ToInt64(persistedGold);
                        if (persistedGoldValue < 0 || persistedGoldValue > int.MaxValue)
                            throw new InvalidDataException($"Character {activeCharacter.id} gold is outside the persistent range after increment: {persistedGoldValue}");
                        updatedGold = (uint)persistedGoldValue;
                    }
                    transaction.Commit();
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DROP-GOLD] transaction failed source={source ?? "unknown"} characterId={activeCharacter.id} entity=0x{entityId:X4} dbId={info.DbId} amount={goldAmount} message='{ex.Message}'");
                lock (_droppedItems) _droppedGoldAcquisitions.Remove(entityId);
                return false;
            }

            activeCharacter.gold = updatedGold;
            if (_playerStates.TryGetValue(conn.ConnId.ToString(), out PlayerState state))
                state.Gold = updatedGold;
            if (info.DbId > 0)
                _dbIdToEntityId.Remove(info.DbId);
            bool removedFromWorld = RemoveDroppedItemWithLifetime(entityId, out DroppedItemInfo removedItem);
            lock (_droppedItems) _droppedGoldAcquisitions.Remove(entityId);
            if (!removedFromWorld || !ReferenceEquals(removedItem, info))
                Debug.LogError($"[DROP-GOLD] world removal mismatch source={source ?? "unknown"} characterId={activeCharacter.id} entity=0x{entityId:X4} removed={removedFromWorld}");
            return true;
        }

        public bool BlingGnomeAcquireItemAsGold(RRConnection conn, ushort entityId, uint goldAmount, int conversionRatioFixed = 0)
        {
            if (!TryAcquireDroppedGoldTransaction(conn, entityId, goldAmount, "bling-gnome", out DroppedItemInfo info, out uint updatedGold, conversionRatioFixed))
                return false;

            bool sentDespawn = false;
            foreach (var other in GetConnectionInsertionOrderSnapshot())
            {
                if (other == null || !other.IsSpawned) continue;
                if (!DroppedItemMatchesConnection(other, info)) continue;
                SendDespawnEntity(other, entityId);
                sentDespawn = true;
            }

            if (!sentDespawn && conn != null)
                SendDespawnEntity(conn, entityId);

            if (conn.UnitContainerId != 0)
            {
                var goldPacket = new LEWriter();
                goldPacket.WriteByte(0x07);
                goldPacket.WriteByte(0x35);
                goldPacket.WriteUInt16(conn.UnitContainerId);
                goldPacket.WriteByte(0x20);
                goldPacket.WriteUInt32(goldAmount);
                goldPacket.WriteByte(0x00);
                goldPacket.WriteUInt32(0x00000000);
                goldPacket.WriteByte(0x01);
                if (TryWriteEntitySynchForComponent(conn, goldPacket, conn.UnitContainerId, 0x20, EntitySynchInfoContext.PlayerActionResponse, "BLING-GOLD"))
                {
                    goldPacket.WriteByte(0x06);
                    SendToClient(conn, goldPacket.ToArray());
                }
            }
            Debug.LogError($"[BLING-GOLD] transaction committed characterId={GetCharSqlId(conn)} entity=0x{entityId:X4} dbId={info.DbId} amount={goldAmount} total={updatedGold}");
            return true;
        }

        public void BlingGnomeSpawnGoldPile(RRConnection conn, int posFixedX, int posFixedY, int posFixedZ, uint goldAmount)
        {
            ushort entityId = GetNextLootEntityId();
            int headingFixed = ConsumeItemAddToWorldHeading($"bling-gold:{conn?.LoginName}:{entityId}");
            var goldInfo = new DroppedItemInfo
            {
                Item = null,
                DbId = 0,
                Zone = conn.CurrentZoneName ?? "",
                ZoneId = conn.CurrentZoneId,
                InstanceId = conn.InstanceId,
                RuntimeInstanceKey = conn.RuntimeInstanceKey ?? "",
                PosFixedX = posFixedX,
                PosFixedY = posFixedY,
                PosFixedZ = posFixedZ,
                HeadingFixed = headingFixed,
                PlayerLevel = 1,
                DroppedBy = conn.LoginName ?? "",
                OwnerCharacterId = GetCharSqlId(conn),
                OwnerName = ResolveCharacterName(conn),
                IsGoldDrop = true,
                GoldAmount = goldAmount
            };
            StoreDroppedItemWithLifetime(entityId, goldInfo);
            SendGoldPileSpawnPacket(conn, entityId, goldInfo);
            Debug.LogError($"[BLING-GOLD] Created gold pile 0x{entityId:X4} worth {goldAmount}g at fixed ({posFixedX},{posFixedY},{posFixedZ})");
        }

        public uint GetItemGoldValue(DroppedItemInfo item)
        {
            if (item == null)
                throw new ArgumentNullException(nameof(item));
            if (item.IsGoldDrop)
            {
                if (item.GoldAmount == 0)
                    throw new InvalidDataException("Bling Gnome gold drop has no currency value");
                return item.GoldAmount;
            }
            if (item.Item == null)
                throw new InvalidDataException("Bling Gnome conversion item has no item payload");
            return RPGSettings.CalculateNativeItemValue(item.Item, item.PlayerLevel, false);
        }

        private Dictionary<string, uint> _spawnedAvatarIds = new Dictionary<string, uint>();
        private Dictionary<string, Dictionary<ushort, string>> _playerComponentTypes = new Dictionary<string, Dictionary<ushort, string>>();
        private readonly Dictionary<string, ushort> _playerUnitContainerComponentIds = new Dictionary<string, ushort>();
        private const uint RETURN_TOWN_PORTAL_LIFETIME_TICKS = 150u;
        private readonly List<(ushort EntityId, string ZoneGcType, uint InstanceId, uint DueTick)> _pendingReturnTownPortalDespawns = new List<(ushort, string, uint, uint)>();
        private readonly object _portalEntityIdGate = new object();
        private readonly HashSet<ushort> _reservedPortalEntityIds = new HashSet<ushort>();
        private int _nextPortalEntityId = PORTAL_ID_MIN;

        private Dictionary<string, ushort> _playerSkillsComponentId = new Dictionary<string, ushort>();
        private Dictionary<string, uint> _playerAvatarEntityId = new Dictionary<string, uint>();
        private Dictionary<string, uint> _playerNextSkillEntityId = new Dictionary<string, uint>();
        private Dictionary<long, ushort> _dbIdToEntityId = new Dictionary<long, ushort>();

        public ushort GetNextEntityId()
        {
            return checked((ushort)AllocateGeneralEntityId());
        }

        private bool TryAllocatePortalEntityId(out ushort entityId)
        {
            entityId = 0;
            if (!TryAllocatePortalEntityIds(1, out ushort[] entityIds))
                return false;
            entityId = entityIds[0];
            return true;
        }

        private bool TryAllocatePortalEntityIds(int count, out ushort[] entityIds)
        {
            entityIds = Array.Empty<ushort>();
            int range = PORTAL_ID_MAX - PORTAL_ID_MIN + 1;
            if (count <= 0 || count > range)
                return false;
            lock (_portalEntityIdGate)
            {
                if (count > range - _reservedPortalEntityIds.Count)
                    return false;
                int candidate = _nextPortalEntityId;
                if (candidate < PORTAL_ID_MIN || candidate > PORTAL_ID_MAX)
                    candidate = PORTAL_ID_MIN;
                var selected = new List<ushort>(count);
                for (int attempt = 0; attempt < range && selected.Count < count; attempt++)
                {
                    ushort wireId = (ushort)candidate;
                    if (!_reservedPortalEntityIds.Contains(wireId)
                        && !_portalEntities.ContainsKey(wireId)
                        && !NetworkEntityIdRegistry.IsReserved(wireId))
                        selected.Add(wireId);
                    candidate = candidate < PORTAL_ID_MAX ? candidate + 1 : PORTAL_ID_MIN;
                }
                if (selected.Count != count)
                    return false;
                if (!NetworkEntityIdRegistry.TryReserveBatch(NetworkEntityIdDomain.Portal, selected))
                    return false;
                foreach (ushort wireId in selected)
                    _reservedPortalEntityIds.Add(wireId);
                _nextPortalEntityId = candidate;
                entityIds = selected.ToArray();
                return true;
            }
        }

        private void ReleasePortalEntityId(ushort entityId)
        {
            lock (_portalEntityIdGate)
            {
                if (_reservedPortalEntityIds.Remove(entityId))
                    NetworkEntityIdRegistry.Release(NetworkEntityIdDomain.Portal, entityId);
            }
        }
        public uint GetPlayerAvatarId(string loginName)
        {
            if (_spawnedAvatarIds.TryGetValue(loginName, out uint avatarId))
            {
                return avatarId;
            }
            return 0;
        }

        public GCObject GetSelectedCharacter(string loginName)
        {
            if (!string.IsNullOrEmpty(loginName) && _selectedCharacter.TryGetValue(loginName, out var ch))
                return ch;
            return null;
        }

        public IEnumerable<RRConnection> AllConnectedConnections()
        {
            foreach (var conn in GetConnectionInsertionOrderSnapshot())
            {
                if (!conn.IsConnected || string.IsNullOrEmpty(conn.LoginName)) continue;
                yield return conn;
            }
        }

        public bool HasOtherPlayerWithSameNativeGroupInWorld(RRConnection owner)
        {
            if (owner == null || !owner.IsConnected || !owner.IsSpawned)
                return false;
            uint ownerGroupId = GroupDirectory.Instance.GetGroupForConn(owner.ConnId)?.GroupId ?? 0;
            string ownerInstanceKey = RoomRuntime.NormalizeInstanceKey(owner.RuntimeInstanceKey);
            foreach (RRConnection other in GetConnectionInsertionOrderSnapshot())
            {
                if (other == null
                    || ReferenceEquals(other, owner)
                    || !other.IsConnected
                    || !other.IsSpawned
                    || !string.Equals(RoomRuntime.NormalizeInstanceKey(other.RuntimeInstanceKey), ownerInstanceKey, StringComparison.OrdinalIgnoreCase))
                    continue;
                uint otherGroupId = GroupDirectory.Instance.GetGroupForConn(other.ConnId)?.GroupId ?? 0;
                if (otherGroupId == ownerGroupId)
                    return true;
            }
            return false;
        }

        public IEnumerable<RRConnection> GetConnectedMemberConnsForPosse(uint posseId)
        {
            if (posseId == 0) yield break;
            foreach (var conn in GetConnectionInsertionOrderSnapshot())
            {
                if (!conn.IsConnected || string.IsNullOrEmpty(conn.LoginName)) continue;
                var savedCharacter = GetActiveCharacter(conn);
                if (savedCharacter != null && savedCharacter.posseId == posseId) yield return conn;
            }
        }

        private void SendPosseStateForCharacter(RRConnection conn, uint characterId,
            Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            try
            {
                var savedChar = GetActiveCharacter(conn);
                if (savedChar == null || savedChar.id != characterId)
                    savedChar = CharacterRepository.GetCharacter(characterId);
                if (savedChar != null && savedChar.posseId != 0)
                {
                    var posse = PosseRepository.GetPosse(savedChar.posseId);
                    if (posse != null)
                    {
                        var memberNames = PosseRepository.MemberNames(posse.Id);
                        var members = new List<(uint, string, bool)>(memberNames.Count);
                        using (var dbConn = GameDatabase.GetConnection())
                        using (var reader = GameDatabase.ExecuteReader(dbConn,
                            "SELECT id, name FROM characters WHERE posse_id = @pid ORDER BY id",
                            ("@pid", (int)posse.Id)))
                        {
                            while (reader.Read())
                            {
                                uint memberCharacterId = (uint)reader.GetInt32(0);
                                string characterName = reader.GetString(1);
                                members.Add((memberCharacterId, characterName, memberCharacterId == posse.FounderCharacterId));
                            }
                        }
                        Debug.LogError($"[POSSE-LOGIN] Restoring posse '{posse.Name}' id={posse.Id} ({members.Count} members) for {savedChar.name}");
                        PosseRuntime.Instance.SendCachedPosseFull(conn, characterId, posse, members, sendCompressed, this);
                        return;
                    }
                    Debug.LogError($"[POSSE-LOGIN] character id={characterId} has posse_id={savedChar.posseId} but row missing  falling back to no-posse state");
                }
                Debug.LogError($"[POSSE-LOGIN] character id={characterId} has no posse  skipping cache push so Tad button reads 'eligible'");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[POSSE-LOGIN] sendPosseStateForCharacter state=failed message='{ex.Message}'");
            }
        }


        private void OnMonsterSpawned(Monster monster)
        {
            ApplyServerReconstructionScaling(monster);
            _monsterBehaviorIds[monster.EntityId] = monster.BehaviorId;
            Debug.LogError($"[COMBAT] monster='{monster.Name}' spawnedId={monster.EntityId} behaviorId={monster.BehaviorId}");
            WirePacketTally.RegisterMonster(
                (ushort)monster.EntityId, (ushort)monster.BehaviorId,
                (ushort)monster.SkillsId, (ushort)monster.ManipulatorsId,
                (ushort)monster.ModifiersId);
            if (monster.Summoner != null)
                foreach (RRConnection recipient in GetConnectionInsertionOrderSnapshot())
                    if (recipient.IsConnected && recipient.IsSpawned && CombatRuntime.Instance.MatchesInstance(monster, recipient.RuntimeInstanceKey))
                        SendMonsterToClient(recipient, monster, true);
        }

        private Dictionary<uint, int> _monsterOwnerConnId = new Dictionary<uint, int>();

        private void OnMonsterDespawned(Monster monster)
        {
            if (monster == null) return;
            uint entityId = monster.EntityId;
            _scheduledSummonInputEntities.Remove(entityId);
            var summonInputs = _pendingSummonInputMessages.Where(entry => entry.Value == entityId).Select(entry => entry.Key).ToArray();
            for (int index = 0; index < summonInputs.Length; index++)
                _pendingSummonInputMessages.Remove(summonInputs[index]);
            _monsterBehaviorIds.Remove(entityId);
            _pendingMonsterBehaviorUpdates.RemoveAll(update => update.EntityId == entityId);
            var packet = CombatPackets.BuildMonsterDespawnPacket(entityId);
            foreach (RRConnection zoneConn in GetConnectionInsertionOrderSnapshot())
            {
                if (zoneConn == null || !zoneConn.IsConnected || !zoneConn.IsSpawned) continue;
                if (!string.Equals(zoneConn.RuntimeInstanceKey, monster.InstanceKey, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (IsUseTargetingMonster(zoneConn, monster))
                    zoneConn.ActiveUseTargetRemoved = true;
                if (!_monsterSpawnSentByConn.TryGetValue(zoneConn.ConnId, out HashSet<uint> sentMonsters) || !sentMonsters.Remove(entityId))
                    continue;
                SendCompressedA(zoneConn, 0x01, 0x0f, packet);
            }
            _monsterOwnerConnId.Remove(entityId);
            _pendingMonsterDeathExperience.Remove(entityId);
            _finalizedMonsterKills.Remove(entityId);
        }

        private bool TryClaimStockUnitDeathExperience(
            Monster monster,
            out (Monster monster, int experienceF32, uint sourceLevel, string source) death)
        {
            death = default;
            if (monster == null || !monster.DeathLifecycleActive || !monster.OnDeadDispatched
                || monster.IsAlive || CombatRuntime.Instance.PeekMonsterCurrentHPWire(monster) != 0
                || monster.DeathExperienceDispatched)
                return false;

            _pendingMonsterDeathExperience.Remove(monster.EntityId, out var pending);
            string source = ReferenceEquals(pending.monster, monster) ? pending.source : "StockUnit::onDead";
            death = (monster, ResolveStockUnitOnDeadExperienceF32(monster), monster.Level, source);
            monster.DeathExperienceDispatched = true;
            return death.experienceF32 > 0;
        }

        private void OnStockUnitDead(Monster monster)
        {
            if (!TryClaimStockUnitDeathExperience(monster, out var pending))
                return;

            List<uint> entityOrder = CombatRuntime.Instance.GetEntityOrderSnapshot();
            var admittedRecipients = new List<(CombatPlayer Player, RRConnection Conn, int DistanceSquaredF32)>();
            for (int entityIndex = entityOrder.Count - 1; entityIndex >= 0; entityIndex--)
            {
                uint playerEntityId = entityOrder[entityIndex];
                CombatPlayer combatPlayer = CombatRuntime.Instance.GetPlayer(playerEntityId);
                if (combatPlayer == null || combatPlayer.PlayerState == null)
                    continue;

                RRConnection conn = FindConnectionByAvatarEntityId(playerEntityId);
                if (!TryAdmitHeroOnAddExperience(conn, combatPlayer, monster, pending.sourceLevel, out int distanceSquaredF32, out string rejectReason))
                {
                    Debug.LogError($"[XP-ONDEAD] player={conn?.LoginName ?? combatPlayer.Name ?? "unknown"} monster={monster.Name}#{monster.EntityId} experienceF32={pending.experienceF32} effectiveXP=0 distanceSquaredF32={distanceSquaredF32} result={rejectReason} source={pending.source}");
                    continue;
                }

                admittedRecipients.Add((combatPlayer, conn, distanceSquaredF32));
            }

            int recipients = 0;
            foreach (var admitted in admittedRecipients)
            {
                CombatPlayer combatPlayer = admitted.Player;
                RRConnection conn = admitted.Conn;
                int distanceSquaredF32 = admitted.DistanceSquaredF32;

                PlayerState playerState = combatPlayer.PlayerState;
                int experienceF32 = ResolveGroupExperienceF32(pending.experienceF32, conn, admittedRecipients, out uint groupId, out uint groupLevelTotal, out uint groupPlayerLevel);
                uint effectiveXP = ResolveHeroOnAddExperience(conn, monster, playerState, experienceF32, pending.sourceLevel);
                if (effectiveXP == 0)
                {
                    Debug.LogError($"[XP-ONDEAD] player={conn.LoginName} monster={monster.Name}#{monster.EntityId} experienceF32={experienceF32} effectiveXP=0 distanceSquaredF32={distanceSquaredF32} groupId={groupId} groupLevelTotal={groupLevelTotal} groupPlayerLevel={groupPlayerLevel} result=zero source={pending.source}");
                    continue;
                }

                int oldLevel = playerState.Level;
                uint oldHPWire = playerState.CurrentHPWire;
                uint oldMaxHPWire = playerState.MaxHPWire;
                bool leveled;
                lock (conn.SendLock)
                {
                    leveled = playerState.AddExperience(effectiveXP, _ =>
                        RecalculateHotbarPassiveBonuses(conn, GetActiveCharacter(conn), sendModifiers: false));
                }
                if (!SavePlayerLevel(conn))
                {
                    QuarantineCommittedPersistenceSyncFailure(conn, "kill-experience-save", new InvalidOperationException("kill experience could not be persisted"));
                    continue;
                }
                lock (conn.SendLock)
                {
                    CommitPlayerHPTruth(conn, playerState, leveled ? "LEVEL-UP-ONDEAD" : "KILL-XP-ONDEAD", playerState.CurrentHPWire, false, false);
                }
                uint nextThreshold = PlayerState.GetClientThreshold(playerState.Level + 1);
                Debug.LogError($"[XP-LEDGER] phase=StockUnit::onDead player={conn.LoginName} gained={effectiveXP} total={playerState.Experience} level={oldLevel}->{playerState.Level} nextThreshold={nextThreshold} experienceF32={experienceF32} maxHP={playerState.MaxHPWire / 256} curHP={playerState.CurrentHPWire / 256} leveled={leveled} groupId={groupId} groupLevelTotal={groupLevelTotal} groupPlayerLevel={groupPlayerLevel}");
                Debug.LogError($"[KILL-XP] phase=StockUnit::onDead monster={monster.Name} experienceF32={experienceF32} effectiveXP={effectiveXP} sourceLevel={pending.sourceLevel} level={oldLevel}->{playerState.Level}{(leveled ? " LEVELUP" : "")} HP={oldHPWire}->{playerState.CurrentHPWire}/{playerState.MaxHPWire} maxHP={oldMaxHPWire}->{playerState.MaxHPWire} distanceSquaredF32={distanceSquaredF32} groupId={groupId} groupLevelTotal={groupLevelTotal} groupPlayerLevel={groupPlayerLevel} source={pending.source}");
                recipients++;
            }

            Debug.LogError($"[XP-ONDEAD] monster={monster.Name}#{monster.EntityId} experienceF32={pending.experienceF32} recipients={recipients} source={pending.source}");
        }

        private int ResolveGroupExperienceF32(
            int experienceF32,
            RRConnection recipientConn,
            IReadOnlyList<(CombatPlayer Player, RRConnection Conn, int DistanceSquaredF32)> admittedRecipients,
            out uint groupId,
            out uint groupLevelTotal,
            out uint groupPlayerLevel)
        {
            groupId = 0;
            groupLevelTotal = 0;
            groupPlayerLevel = 0;
            if (experienceF32 <= 0 || recipientConn == null)
                return experienceF32;

            Group group = GroupDirectory.Instance.GetGroupForConn(recipientConn.ConnId);
            if (group == null)
                return experienceF32;

            groupId = group.GroupId;
            foreach (var admitted in admittedRecipients)
            {
                if (admitted.Conn == null || admitted.Player == null || admitted.Player.PlayerState == null)
                    continue;
                Group candidateGroup = GroupDirectory.Instance.GetGroupForConn(admitted.Conn.ConnId);
                if (candidateGroup == null || candidateGroup.GroupId != group.GroupId)
                    continue;

                uint playerLevel = checked((uint)Math.Max(1, admitted.Player.PlayerState.Level));
                groupLevelTotal = checked(groupLevelTotal + playerLevel);
                if (admitted.Conn.ConnId == recipientConn.ConnId)
                    groupPlayerLevel = playerLevel;
            }

            if (groupLevelTotal <= groupPlayerLevel || groupPlayerLevel == 0)
                return experienceF32;

            return checked((int)(((long)experienceF32 * groupPlayerLevel) / groupLevelTotal));
        }

        private void OnStockUnitRespawned(Monster monster)
        {
            if (monster != null)
                _finalizedMonsterKills.Remove(monster.EntityId);
        }

        private bool TryAdmitHeroOnAddExperience(RRConnection conn, CombatPlayer player, Monster source, uint sourceLevel, out int distanceSquaredF32, out string rejectReason)
        {
            distanceSquaredF32 = 0;
            rejectReason = "invalid";
            if (conn == null || !conn.IsConnected || !conn.IsSpawned || conn.Avatar == null || player == null || player.PlayerState == null)
                return false;
            if (IsZoneSpawnInvulnerabilityActive(conn))
            {
                rejectReason = "experience-immunity";
                return false;
            }
            if (!string.Equals(RoomRuntime.NormalizeInstanceKey(player.InstanceKey), RoomRuntime.NormalizeInstanceKey(source.InstanceKey), StringComparison.OrdinalIgnoreCase))
            {
                rejectReason = "world";
                return false;
            }
            if (!player.IsAlive)
            {
                rejectReason = "dead";
                return false;
            }
            int playerLevel = player.PlayerState.Level;
            if (playerLevel >= GCDatabase.Instance.GetRequiredKnobInt("MaxLevel"))
            {
                rejectReason = "max-level";
                return false;
            }
            distanceSquaredF32 = WorldEntityDistanceToSquaredF32(
                source.PosFixedX,
                source.PosFixedY,
                source.PosFixedZ,
                player.PosFixedX,
                player.PosFixedY,
                player.PosFixedZ);
            if (distanceSquaredF32 >= 0x049DA400)
            {
                rejectReason = "distance";
                return false;
            }
            if (unchecked((int)sourceLevel) <= playerLevel - 5)
            {
                rejectReason = "source-level";
                return false;
            }
            rejectReason = "admitted";
            return true;
        }

        private static int WorldEntityDistanceToSquaredF32(int sourceX, int sourceY, int sourceZ, int targetX, int targetY, int targetZ)
        {
            int dx = unchecked(sourceX - targetX);
            int dy = unchecked(sourceY - targetY);
            int dz = unchecked(sourceZ - targetZ);
            int xSquaredF32 = Fixed32Multiply(dx, dx);
            int ySquaredF32 = Fixed32Multiply(dy, dy);
            int zSquaredF32 = Fixed32Multiply(dz, dz);
            return unchecked(unchecked(xSquaredF32 + ySquaredF32) + zSquaredF32);
        }

        private List<RRConnection> GetMonsterInstanceRecipients(uint entityId)
        {
            var recipients = new List<RRConnection>();
            if (!_monsterOwnerConnId.TryGetValue(entityId, out int ownerConnId)) return recipients;
            if (!_connections.TryGetValue(ownerConnId, out var ownerConn) || ownerConn == null) return recipients;
            foreach (var conn in GetConnectionInsertionOrderSnapshot())
            {
                if (conn == null || !conn.IsConnected) continue;
                if (conn.InstanceId != ownerConn.InstanceId) continue;
                if (!string.Equals(conn.CurrentZoneGcType, ownerConn.CurrentZoneGcType, StringComparison.OrdinalIgnoreCase)) continue;
                recipients.Add(conn);
            }
            return recipients;
        }

        internal List<RRConnection> GetInstanceRecipients(RRConnection origin)
        {
            var recipients = new List<RRConnection>();
            if (origin == null) return recipients;
            foreach (var conn in GetConnectionInsertionOrderSnapshot())
            {
                if (conn == null || !conn.IsConnected || !conn.IsSpawned || !conn.AllowFlush) continue;
                if (conn.InstanceId != origin.InstanceId) continue;
                if (!string.Equals(conn.CurrentZoneGcType, origin.CurrentZoneGcType, StringComparison.OrdinalIgnoreCase)) continue;
                recipients.Add(conn);
            }
            return recipients;
        }

        private bool TryPrimeMonsterHPBeforeSynch(RRConnection conn, Monster monster, uint hpWire, string source)
        {
            if (conn == null || monster == null) return false;
            string hpState = CombatRuntime.Instance != null ? CombatRuntime.Instance.DescribeMonsterHPState(monster) : "state=<missing>";
            if (VerboseMonsterDiag) Debug.LogError($"[MON-HP-PRIMER] suffix current {monster.Name}#{monster.EntityId} hpWire={hpWire}/{monster.MaxHPWire} source={source} {hpState}");
            return true;
        }

        private EntitySynchInfoDecision ResolveMonsterRuntimeHPDecision(Monster monster, string packetName, string reason)
        {
            GetValidationCutoff(out uint validationCutoffTick);
            CombatRuntime.Instance.TryResolveMonsterEntitySynchInfoHP(monster, EntitySynchInfoContext.Unknown, packetName, out uint hpWire, out string hpReason);
            EntitySynchInfoAuthority.Instance.RecordMonsterOutboundHP(monster, hpWire, $"{packetName} {hpReason} {reason}");
            Debug.LogError($"[ENTITY-SYNCH-INFO-RECOVER] packet={packetName} owner=Monster entity={monster.EntityId} hp={hpWire} reason={reason} hpReason='{hpReason}'");
            return EntitySynchInfoDecision.HP(EntitySynchInfoOwner.Monster, hpWire, $"{packetName} {hpReason} {reason}", monster.EntityId, monster.BehaviorId, 0x04, $"{hpReason}; validationCutoffTick={validationCutoffTick}", validationCutoffTick, hpMutationSource: hpReason);
        }

        private void OnMonsterAttackStarted(Monster monster, CombatTarget target, byte sessionId)
        {
            if (monster == null || target == null) return;


            RRConnection targetConn = null;
            foreach (var conn in GetConnectionInsertionOrderSnapshot())
            {
                if (conn == null || !conn.IsConnected || conn.Avatar == null) continue;
                if (conn.Avatar.Id != target.EntityId) continue;
                targetConn = conn;
                break;
            }

            if (targetConn == null && target.Monster != null)
                targetConn = FindMonsterSimulationConnection(monster);
            if (targetConn == null)
            {
                CombatRuntime.Instance.CancelMonsterPendingAttack(monster, "MON-ATTACK-no-target-connection");
                return;
            }

            if (!monster.IsAlive || CombatRuntime.Instance.PeekMonsterCurrentHPWire(monster) == 0)
            {
                CombatRuntime.Instance.CancelMonsterPendingAttack(monster, "MON-ATTACK-flush-dead");
                return;
            }
            byte useFlags = ResolveMonsterPrimaryManipulatorId(monster);
            bool useTargetAction = ShouldUseMonsterUseTargetAction(monster);
            uint combatTick = CombatRuntime.Instance.CombatTick;
            bool hasRecentDirectPlayerHit = WeaponUseRuntime.Instance.TryGetRecentDirectPlayerWeaponHit(
                target.EntityId,
                monster.EntityId,
                combatTick,
                (uint)CLIENT_DIRECT_HIT_VISIBILITY_MAX_AGE_TICKS,
                out uint directPlayerWeaponHitTick,
                out uint directPlayerWeaponHitPreviousHPWire,
                out uint directPlayerWeaponHitNewHPWire);
            bool directPlayerWeaponHitAtCombatTick = WeaponUseRuntime.Instance.WasDirectPlayerWeaponHitAppliedAtTick(target.EntityId, monster.EntityId, combatTick);
            bool followsDirectPlayerHit = (monster.LocalAttackActionUsePending
                    && monster.LocalAttackActionUseTargetId == target.EntityId)
                || directPlayerWeaponHitAtCombatTick
                || hasRecentDirectPlayerHit;
            uint proximityAttackInputApplyTick = monster.ProximityAttackInputApplyTick;
            bool proximityAttackInput = !followsDirectPlayerHit
                && proximityAttackInputApplyTick == unchecked(combatTick + 1);
            monster.ProximityAttackInputApplyTick = 0;
            if (!monster.FollowClientVisible)
                SupersedePendingMonsterFollowWithAttack(monster, target);
            monster.AttackClientVisible = true;
            QueuePendingMonsterBehaviorUpdate(new PendingMonsterBehaviorUpdate
            {
                Kind = monster.FollowClientVisible ? PendingMonsterBehaviorKind.AttackStop : PendingMonsterBehaviorKind.Attack,
                EntityId = monster.EntityId,
                BehaviorId = monster.BehaviorId,
                ConnId = targetConn.ConnId,
                TargetEntityId = target.EntityId,
                UseFlags = useFlags,
                SessionId = sessionId,
                UseTargetAction = useTargetAction,
                AdmissionTick = proximityAttackInput
                    ? unchecked(combatTick + 2)
                    : unchecked(combatTick + 1),
                SimulationApplyTick = followsDirectPlayerHit
                    ? unchecked(combatTick + 1)
                    : proximityAttackInput
                        ? proximityAttackInputApplyTick
                        : 0u,
                ProximityAttackInput = proximityAttackInput,
                DirectPlayerWeaponHitTick = hasRecentDirectPlayerHit ? directPlayerWeaponHitTick : 0u,
                DirectPlayerWeaponHitPreviousHPWire = hasRecentDirectPlayerHit ? directPlayerWeaponHitPreviousHPWire : 0u,
                DirectPlayerWeaponHitNewHPWire = hasRecentDirectPlayerHit ? directPlayerWeaponHitNewHPWire : 0u,
                QueuedTick = _combatTick
            }, "MON-ATTACK");
            uint localApplyTick = followsDirectPlayerHit
                ? unchecked(combatTick + 1)
                : proximityAttackInput
                    ? proximityAttackInputApplyTick
                    : 0u;
            Debug.LogError($"[MON-ATTACK-QUEUE] client {(useTargetAction ? "UseTarget" : "AttackTarget2")} {monster.Name}->{target.Name} behavior={monster.BehaviorId} session={sessionId} flags={useFlags} target={target.EntityId} localApplyTick={localApplyTick} proximityInput={proximityAttackInput}");
        }

        private void OnMonsterFollowReachedNearStop(Monster monster, CombatTarget target)
        {
            if (monster == null || target == null || !monster.IsAlive || !target.IsAlive)
                return;
            RRConnection targetConn = target.Player != null ? FindConnectionByAvatarEntityId(target.EntityId) : FindMonsterSimulationConnection(monster);
            if (targetConn == null || !targetConn.IsConnected)
                return;
            uint combatTick = CombatRuntime.Instance.CombatTick;
            uint localApplyTick = CombatRuntime.Instance.NextClientEntityUpdateTick;
            bool hasRecentDirectPlayerHit = WeaponUseRuntime.Instance.TryGetRecentDirectPlayerWeaponHit(
                target.EntityId,
                monster.EntityId,
                combatTick,
                (uint)CLIENT_DIRECT_HIT_VISIBILITY_MAX_AGE_TICKS,
                out uint directPlayerWeaponHitTick,
                out uint directPlayerWeaponHitPreviousHPWire,
                out uint directPlayerWeaponHitNewHPWire);
            QueuePendingMonsterBehaviorUpdate(new PendingMonsterBehaviorUpdate
            {
                Kind = PendingMonsterBehaviorKind.AttackStop,
                EntityId = monster.EntityId,
                BehaviorId = monster.BehaviorId,
                ConnId = targetConn.ConnId,
                TargetEntityId = target.EntityId,
                SessionId = monster.AttackSessionId,
                AdmissionTick = unchecked(combatTick + 2),
                SimulationApplyTick = localApplyTick,
                DirectPlayerWeaponHitTick = hasRecentDirectPlayerHit ? directPlayerWeaponHitTick : 0u,
                DirectPlayerWeaponHitPreviousHPWire = hasRecentDirectPlayerHit ? directPlayerWeaponHitPreviousHPWire : 0u,
                DirectPlayerWeaponHitNewHPWire = hasRecentDirectPlayerHit ? directPlayerWeaponHitNewHPWire : 0u,
                QueuedTick = _combatTick
            }, "MON-ATTACK-STOP-FOLLOW-NEAR");
            Debug.LogError($"[MON-ATTACK-STOP-QUEUE] entity={monster.EntityId} target={target.EntityId} tick={combatTick} localApplyTick={localApplyTick} packetWriterTick={CombatRuntime.Instance.NextClientEntityUpdateTick} phase=follow-near sourceFunction=Follow::UpdateMoving@0x005277E0->Behavior::terminateAllActionsLocal@0x00515430");
        }

        private void SupersedePendingMonsterFollowWithAttack(Monster monster, CombatTarget target)
        {
            bool removed = false;
            for (int updateIndex = _pendingMonsterBehaviorUpdates.Count - 1; updateIndex >= 0; updateIndex--)
            {
                PendingMonsterBehaviorUpdate pending = _pendingMonsterBehaviorUpdates[updateIndex];
                if (pending.Kind != PendingMonsterBehaviorKind.Follow
                    || pending.EntityId != monster.EntityId
                    || pending.TargetEntityId != target.EntityId
                    || pending.PacketSent
                    || pending.SimulationApplied)
                    continue;
                _pendingMonsterBehaviorUpdates.RemoveAt(updateIndex);
                removed = true;
            }
            if (!removed)
                return;
            monster.FollowInputPending = false;
            monster.FollowInputTargetId = 0;
            monster.FollowInputApplyTick = 0;
            monster.FollowInputDirectPlayerWeaponHitTick = 0;
            monster.FollowInputPlayerDamageTick = 0;
            Debug.LogError($"[MON-FOLLOW-SUPERSEDE] entity={monster.EntityId} target={target.EntityId} tick={CombatRuntime.Instance.CombatTick} replacement=AttackTarget2 sourceFunction=Behavior::doActionLocal@0x00515130");
        }

        private void OnMonsterAttackResolved(Monster monster, CombatTarget target, bool damaged, uint hpWire)
        {
            if (target.Player != null)
                HandlePlayerDamageResolved(monster, target.Player, damaged, hpWire, damaged ? "MON-ATTACK-RESOLVE-HIT" : "MON-ATTACK-RESOLVE-NO-DAMAGE");
            else
                HandleMonsterTargetDamageResolved(monster, target.Monster, damaged, hpWire);
        }

        private void OnPlayerDamageResolved(Monster monster, CombatPlayer target, bool damaged, uint hpWire, string source)
        {
            HandlePlayerDamageResolved(monster, target, damaged, hpWire, string.IsNullOrWhiteSpace(source) ? (damaged ? "PLAYER-DAMAGE-RESOLVE-HIT" : "PLAYER-DAMAGE-RESOLVE-NO-DAMAGE") : source);
        }

        private void OnPlayerStunActionResolved(Monster monster, CombatPlayer target, CombatRuntime.PlayerStunActionResolved action)
        {
            if (target == null || action == null)
                return;
            RRConnection targetConn = null;
            foreach (var conn in GetConnectionInsertionOrderSnapshot())
            {
                if (conn == null || !conn.IsConnected || conn.Avatar == null) continue;
                if (conn.Avatar.Id != target.EntityId) continue;
                targetConn = conn;
                break;
            }
            if (targetConn == null)
            {
                Debug.LogError($"[PLAYER-STUN-ACTION] not-sent reason=missing-target-connection player={target.Name}#{target.EntityId} action={action.ActionClassName} actionId=0x{action.ActionClassId:X2}");
                return;
            }
            ushort componentId = targetConn.UnitBehaviorId != 0 ? (ushort)targetConn.UnitBehaviorId : targetConn.BehaviorComponentId;
            if (componentId == 0)
            {
                Debug.LogError($"[PLAYER-STUN-ACTION] not-sent reason=missing-behavior-component player={target.Name}#{target.EntityId} action={action.ActionClassName} actionId=0x{action.ActionClassId:X2}");
                return;
            }
            if (!ResolveEntitySynchInfoForComponent(targetConn, componentId, 0x04, EntitySynchInfoContext.PlayerActionResponse, target.EntityId, "PLAYER-STUN-ACTION", out EntitySynchInfoDecision decision))
            {
                Debug.LogError($"[PLAYER-STUN-ACTION] not-sent reason=entity-synch-info-decision player={target.Name}#{target.EntityId} action={action.ActionClassName} actionId=0x{action.ActionClassId:X2} decision={decision.Reason ?? "none"}");
                return;
            }
            ResolvedEntitySynchInfo entitySynchInfo = decision.ToResolved(target.EntityId, componentId, 0x04, "PLAYER-STUN-ACTION sourceFunction=KnockBack::writeData@0x0052A320");
            byte[] packet = CombatPackets.BuildPlayerStunActionPacket(componentId, action.ActionClassId, action.HeadingWire, action.StrengthWire, entitySynchInfo);
            bool sent = QueueClientEntityStream(targetConn, packet);
            Debug.LogError($"[PLAYER-STUN-ACTION] sent={sent} monster={monster?.Name ?? "monster"}#{monster?.EntityId ?? 0} player={target.Name}#{target.EntityId} component=0x{componentId:X4} action={action.ActionClassName} actionId=0x{action.ActionClassId:X2} heading={action.HeadingWire} strengthWire={action.StrengthWire} authoredStrength={action.AuthoredStrength} usesKnockDownAction={action.UsesKnockDownAction} hp={entitySynchInfo.HPWire} chanceRaw=0x{action.ChanceRaw:X8} chanceRoll={action.ChanceRoll} stunResistWire={action.StunResistWire} stunRaw=0x{action.StunRaw:X8} stunRoll={action.StunRoll} source={action.Source ?? "unknown"} sourceFunction=Behavior::processUpdate@0x00515620 KnockBack::writeData@0x0052A320");
        }

        private void OnPlayerModifierNetworkEvent(Monster monster, CombatTarget target, CombatRuntime.PlayerModifierNetworkEvent mod)
        {
            if (target?.Monster != null)
            {
                SendMonsterTargetModifier(target.Monster, mod);
                return;
            }
            if (target == null || mod == null)
                return;
            RRConnection targetConn = null;
            foreach (var conn in GetConnectionInsertionOrderSnapshot())
            {
                if (conn == null || !conn.IsConnected || conn.Avatar == null) continue;
                if (conn.Avatar.Id != target.EntityId) continue;
                targetConn = conn;
                break;
            }
            if (targetConn == null)
            {
                Debug.LogError($"[PLAYER-MODIFIER-PACKET] not-sent reason=missing-target-connection player={target.Name}#{target.EntityId} add={mod.Add} gc={mod.GCType ?? ""} id={mod.ModifierId}");
                return;
            }
            ushort componentId = targetConn.ModifiersId != 0 ? targetConn.ModifiersId : targetConn.ModifiersComponentId;
            if (componentId == 0)
            {
                Debug.LogError($"[PLAYER-MODIFIER-PACKET] not-sent reason=missing-modifiers-component player={target.Name}#{target.EntityId} add={mod.Add} gc={mod.GCType ?? ""} id={mod.ModifierId}");
                return;
            }
            byte subtype = mod.Add ? (byte)0x00 : (byte)0x01;
            string packetName = mod.Add ? "PLAYER-MODIFIER-ADD" : "PLAYER-MODIFIER-REMOVE";
            if (!ResolveEntitySynchInfoForComponent(targetConn, componentId, subtype, EntitySynchInfoContext.PlayerActionResponse, target.EntityId, packetName, out EntitySynchInfoDecision decision))
            {
                Debug.LogError($"[PLAYER-MODIFIER-PACKET] not-sent reason=entity-synch-info-decision player={target.Name}#{target.EntityId} add={mod.Add} gc={mod.GCType ?? ""} id={mod.ModifierId} decision={decision.Reason ?? "none"}");
                return;
            }
            ResolvedEntitySynchInfo entitySynchInfo = decision.ToResolved(target.EntityId, componentId, subtype, $"{packetName} sourceFunction={mod.SourceFunction ?? "Modifiers::processUpdate"}");
            byte[] packet = mod.Add
                ? CombatPackets.BuildPlayerModifierAddPacket(componentId, mod.GCType, mod.ModifierId, mod.Level, mod.PowerLevel, mod.DurationTicks, mod.SourceIsSelf, entitySynchInfo)
                : CombatPackets.BuildPlayerModifierRemovePacket(componentId, mod.ModifierId, entitySynchInfo);
            bool sent = QueueClientEntityStream(targetConn, packet);
            string lifecycle = mod.Lifecycle != null && mod.Lifecycle.HasClientLocalLifecycle
                ? $"visual={mod.Lifecycle.Visual ?? ""} initSound={mod.Lifecycle.InitSound ?? ""} initEffect={mod.Lifecycle.InitEffect ?? ""} removeEffect={mod.Lifecycle.RemoveEffect ?? ""} overlay={mod.Lifecycle.OverlayIcon ?? ""} overlayDuration={mod.Lifecycle.OverlayDuration}"
                : "none";
            Debug.LogError($"[PLAYER-MODIFIER-PACKET] sent={sent} packet={packetName} monster={monster?.Name ?? "monster"}#{monster?.EntityId ?? 0} player={target.Name}#{target.EntityId} component=0x{componentId:X4} gc={mod.GCType ?? ""} id={mod.ModifierId} level={mod.Level} power={mod.PowerLevel} duration={mod.DurationTicks} sourceIsSelf={mod.SourceIsSelf} replace={mod.Replace} hp={entitySynchInfo.HPWire} skill={mod.SkillPath ?? ""} effect={mod.EffectPath ?? ""} lifecycle={lifecycle} source={mod.Source ?? "unknown"} sourceFunction={mod.SourceFunction ?? "Modifiers::processUpdate"}");
        }

        private void OnPlayerAttributeModifierRemoved(CombatPlayer target, PlayerState.AttributeModifierRemoval removal)
        {
            if (target == null || removal == null || string.IsNullOrWhiteSpace(removal.ModifierType))
                return;
            foreach (RRConnection conn in GetConnectionInsertionOrderSnapshot())
            {
                if (conn == null || conn.Avatar == null || conn.Avatar.Id != target.EntityId || string.IsNullOrWhiteSpace(conn.LoginName))
                    continue;
                if (string.Equals(removal.ModifierType, QUEST_XP_BONUS_MODIFIER_GC_TYPE, StringComparison.OrdinalIgnoreCase))
                {
                    uint characterId = GetCharSqlId(conn);
                    if (characterId == 0 || !CharacterRepository.TryDeleteCharacterModifier(characterId, QUEST_XP_BONUS_MODIFIER_GC_TYPE, "modifier-runtime-remove"))
                    {
                        QuarantineCommittedPersistenceSyncFailure(conn, "quest-xp-modifier-remove", new InvalidOperationException("quest XP modifier durable removal failed"));
                        return;
                    }
                }
                UntrackModifier(conn.LoginName, removal.ModifierType);
                Debug.LogError($"[ACTIVE-MODIFIERS] lifecycle-remove player={target.Name}#{target.EntityId} modifier={removal.ModifierType} source={removal.Source ?? "unknown"} sourceFunction={removal.SourceFunction ?? "Modifiers::processRemoveModifier@0x00502390"}");
                return;
            }
        }

        private bool OnPlayerAttributeModifiersRemovingForDeath(CombatPlayer target, string source)
        {
            if (target?.PlayerState == null)
                return false;
            foreach (RRConnection conn in GetConnectionInsertionOrderSnapshot())
            {
                if (conn == null || conn.Avatar == null || conn.Avatar.Id != target.EntityId)
                    continue;
                var modifierIds = _unitContainer?.GetRemoveOnDeathModifierIds(conn)?.ToList() ?? new List<uint>();
                if (target.PlayerState.HasRemoveOnDeathAttributeModifier(QUEST_XP_BONUS_MODIFIER_GC_TYPE))
                    modifierIds.Add(QUEST_XP_BONUS_MODIFIER_ID);
                modifierIds = modifierIds.Distinct().ToList();
                if (modifierIds.Count == 0)
                    return true;
                uint characterId = GetCharSqlId(conn);
                if (characterId != 0
                    && CharacterRepository.TryDeleteCharacterModifiers(characterId, modifierIds, source ?? "modifier-death"))
                {
                    _unitContainer?.CommitRemoveOnDeathModifierRuntime(conn, modifierIds);
                    return true;
                }
                QuarantineCommittedPersistenceSyncFailure(conn, "modifier-death-remove", new InvalidOperationException("remove-on-death modifier durable removal failed"));
                return false;
            }
            return false;
        }

        private bool RestorePersistedQuestXPBonusModifier(RRConnection conn)
        {
            if (conn == null)
                return false;
            uint characterId = GetCharSqlId(conn);
            if (characterId == 0
                || !CharacterRepository.TryHasCharacterModifier(characterId, QUEST_XP_BONUS_MODIFIER_GC_TYPE, out bool exists))
                return false;
            if (!exists)
                return true;
            if (!TryMaterializeQuestXPBonusModifier(conn, out _))
                return false;
            RecordModifierSent(conn.LoginName, QUEST_XP_BONUS_MODIFIER_GC_TYPE, QUEST_XP_BONUS_MODIFIER_ID,
                level: 0, powerLevel: 0, duration: 0, sourceIsSelf: 1);
            return true;
        }

        private void HandlePlayerDamageResolved(Monster monster, CombatPlayer target, bool damaged, uint hpWire, string source)
        {
            if (target == null) return;
            RRConnection targetConn = null;
            foreach (var conn in GetConnectionInsertionOrderSnapshot())
            {
                if (conn == null || !conn.IsConnected || conn.Avatar == null) continue;
                if (conn.Avatar.Id != target.EntityId) continue;
                targetConn = conn;
                break;
            }

            if (targetConn == null) return;
            uint entitySynchInfoHP = hpWire;
            bool attackClientVisible = monster != null && monster.AttackClientVisible;
            bool clientContact = monster != null && monster.AttackContactOnly;
            bool attackPending = monster != null && monster.AttackPending;
            bool hitResolved = monster != null && monster.AttackHitResolved;
            Debug.LogError($"[PLAYER-HP-TRUTH] RESOLVE monster={monster?.Name ?? "unknown"}#{monster?.EntityId ?? 0} player={target.Name} damaged={damaged} serverHP={entitySynchInfoHP} clientVisible={attackClientVisible} clientContact={clientContact} pending={attackPending} hitResolved={hitResolved} source={source ?? "unknown"}");
            PlayerState state = GetPlayerState(targetConn.ConnId.ToString());
            if (damaged)
            {
                if (state != null)
                    CommitPlayerHPTruth(targetConn, state, source, entitySynchInfoHP, false, false);
                else
                    RecordPlayerHPKnown(targetConn, source, entitySynchInfoHP);
                if (entitySynchInfoHP == 0 || hpWire == 0)
                    HandleLocalPlayerDeathFromMonster(targetConn, monster, target, entitySynchInfoHP);
                else
                    Debug.LogError($"[PLAYER-HP-TRUTH] DAMAGE source={source} player={target.Name} hp={entitySynchInfoHP / 256f:F2}");
            }
            else
            {
                Debug.LogError($"[PLAYER-HP-TRUTH] NO-DAMAGE source={source} player={target.Name} keepEntitySynchInfoHP={(state != null ? state.EntitySynchInfoHP / 256f : entitySynchInfoHP / 256f):F2}");
            }
        }

        private void HandleLocalPlayerDeathFromMonster(RRConnection conn, Monster monster, CombatPlayer target, uint hpWire)
        {
            if (conn == null) return;
            if (BlingGnomeRuntime.Instance.HasGnome(conn.ConnId))
                BlingGnomeRuntime.Instance.DespawnGnome(conn, (c, d, t, b) => SendCompressedA(c, d, t, b), playDeathAnim: false);
            ClearUseTarget(conn);
            Combat.WeaponUseRuntime.Instance.ClearConnection(conn.ConnId.ToString());
            if (conn.Avatar != null)
                CombatRuntime.Instance.SetPlayerActiveClientAttack((uint)conn.Avatar.Id, false);

            ushort componentId = conn.UnitBehaviorId != 0 ? (ushort)conn.UnitBehaviorId : conn.BehaviorComponentId;
            if (componentId == 0)
            {
                Debug.LogError($"[PLAYER-DEATH] Missing player behavior component player={conn.LoginName ?? conn.ConnId.ToString()} hp={hpWire}");
                return;
            }

            var playerDeathControlMessage = new LEWriter();
            playerDeathControlMessage.WriteByte(0x07);
            if (!WriteClientControlUpdate(conn, playerDeathControlMessage, componentId, false, "PLAYER-DEATH-CONTROL", hpWire))
            {
                Debug.LogError($"[PLAYER-DEATH] Dropped local control release player={conn.LoginName ?? conn.ConnId.ToString()} component=0x{componentId:X4} hp={hpWire}");
                return;
            }
            playerDeathControlMessage.WriteByte(0x06);

            QueueClientEntityStream(conn, playerDeathControlMessage.ToArray());
            conn.OwnerUnitBehaviorMoverMode = UnitMover.StoppedMode;
            SyncReflectedAvatarCombatPosition(conn);
            BroadcastPlayerDeath(conn);
            Debug.LogError($"[PLAYER-DEATH] Sent local control release player={target?.Name ?? conn.LoginName ?? conn.ConnId.ToString()} monster={monster?.Name ?? "unknown"}#{monster?.EntityId ?? 0} component=0x{componentId:X4} hp={hpWire}");
        }

        private static byte ResolveMonsterPrimaryManipulatorId(Monster monster)
        {
            if (monster == null || !monster.UsePrimaryActiveSkillThisAttack)
                return 10;
            if (monster.PrimaryActiveSkillId != 0)
                return monster.PrimaryActiveSkillId;
            if (monster.Manipulators == null)
                return 10;

            foreach (var manipulatorEntry in monster.ManipulatorOrder)
            {
                ManipulatorData manipulator = manipulatorEntry.Value;
                if (!IsPrimaryActiveSkillManipulator(manipulator))
                    continue;
                if (TryGetManipulatorByte(manipulator, "ID", out byte id))
                    return id;
            }

            return 10;
        }

        private static bool ShouldUseMonsterUseTargetAction(Monster monster)
        {
            if (monster == null)
                return false;
            if (!monster.UsePrimaryActiveSkillThisAttack)
                return false;
            if (!string.IsNullOrWhiteSpace(monster.PrimaryActiveSkillPath))
                return true;
            if (monster.Manipulators == null)
                return false;

            foreach (var manipulatorEntry in monster.ManipulatorOrder)
            {
                ManipulatorData manipulator = manipulatorEntry.Value;
                if (IsPrimaryActiveSkillManipulator(manipulator))
                    return true;
            }

            return false;
        }

        private static bool IsPrimaryActiveSkillManipulator(ManipulatorData manipulator)
        {
            if (manipulator == null || string.IsNullOrWhiteSpace(manipulator.gcType))
                return false;

            if (!IsActiveSkillManipulatorPath(manipulator.gcType))
                return false;

            if (TryGetManipulatorBool(manipulator, "IsPrimaryAttack", out bool primaryFromManipulator))
                return primaryFromManipulator;

            var node = GCDatabase.Instance?.ResolveWithInheritance(manipulator.gcType);
            var desc = node?.GetChild("Description") ?? node;
            return desc != null && desc.GetBool("IsPrimaryAttack", false);
        }

        private static bool IsActiveSkillManipulatorPath(string gcType)
        {
            var gc = GCDatabase.Instance;
            string current = gcType;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (!string.IsNullOrWhiteSpace(current) && visited.Add(current))
            {
                if (current.Equals("ActiveSkill", StringComparison.OrdinalIgnoreCase) ||
                    current.EndsWith(".ActiveSkill", StringComparison.OrdinalIgnoreCase))
                    return true;

                var node = gc?.Resolve(current);
                current = node?.Extends;
            }

            return false;
        }

        private static bool TryGetManipulatorByte(ManipulatorData manipulator, string property, out byte value)
        {
            value = 0;
            if (manipulator?.properties == null || !manipulator.properties.TryGetValue(property, out string text))
                return false;
            return byte.TryParse(text, out value);
        }

        private static bool TryGetManipulatorBool(ManipulatorData manipulator, string property, out bool value)
        {
            value = false;
            if (manipulator?.properties == null || !manipulator.properties.TryGetValue(property, out string text))
                return false;
            if (bool.TryParse(text, out value))
                return true;
            if (text == "1")
            {
                value = true;
                return true;
            }
            if (text == "0")
            {
                value = false;
                return true;
            }
            return false;
        }

        private void QueuePendingMonsterBehaviorUpdate(PendingMonsterBehaviorUpdate update, string packetName)
        {
            bool followBroadcast = update.Kind == PendingMonsterBehaviorKind.Follow;
            if (update.EntityId == 0 || update.BehaviorId == 0 || (update.ConnId == 0 && !followBroadcast))
                return;
            Monster updateMonster = CombatRuntime.Instance.GetMonster(update.EntityId);
            if (string.IsNullOrWhiteSpace(update.InstanceKey))
                update.InstanceKey = RoomRuntime.NormalizeInstanceKey(updateMonster?.InstanceKey);
            if (updateMonster == null || !CombatRuntime.Instance.MatchesInstance(updateMonster, update.InstanceKey))
                return;
            uint queueKey = update.Kind switch
            {
                PendingMonsterBehaviorKind.Follow => update.EntityId | 0x40000000u,
                PendingMonsterBehaviorKind.AttackStop => update.EntityId | 0x20000000u,
                PendingMonsterBehaviorKind.Attack => update.EntityId | 0x20000000u,
                _ => update.EntityId
            };
            int replaceIndex = -1;
            for (int updateIndex = 0; updateIndex < _pendingMonsterBehaviorUpdates.Count; updateIndex++)
            {
                PendingMonsterBehaviorUpdate pending = _pendingMonsterBehaviorUpdates[updateIndex];
                uint pendingKey = pending.Kind switch
                {
                    PendingMonsterBehaviorKind.Follow => pending.EntityId | 0x40000000u,
                    PendingMonsterBehaviorKind.AttackStop => pending.EntityId | 0x20000000u,
                    PendingMonsterBehaviorKind.Attack => pending.EntityId | 0x20000000u,
                    _ => pending.EntityId
                };
                if (pendingKey == queueKey)
                {
                    if (!string.Equals(pending.InstanceKey, update.InstanceKey, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (pending.PacketSent || pending.SimulationApplied)
                    {
                        if (pending.Kind == update.Kind
                            && pending.TargetEntityId == update.TargetEntityId
                            && pending.SessionId == update.SessionId)
                            return;
                        continue;
                    }
                    replaceIndex = updateIndex;
                    break;
                }
            }
            if (replaceIndex >= 0)
                _pendingMonsterBehaviorUpdates[replaceIndex] = update;
            else if (update.Kind == PendingMonsterBehaviorKind.Attack
                && update.SimulationApplyTick != 0)
            {
                int insertIndex = _pendingMonsterBehaviorUpdates.FindIndex(pending =>
                    pending.AdmissionTick > update.AdmissionTick);
                if (insertIndex >= 0)
                    _pendingMonsterBehaviorUpdates.Insert(insertIndex, update);
                else
                    _pendingMonsterBehaviorUpdates.Add(update);
            }
            else
                _pendingMonsterBehaviorUpdates.Add(update);
            if (VerboseMonsterDiag) Debug.LogError($"[MON-BEHAVIOR-QUEUE] packet={packetName} entity={update.EntityId} behavior={update.BehaviorId} kind={update.Kind} instance='{update.InstanceKey}' conn={update.ConnId} target={update.TargetEntityId} queuedTick={update.QueuedTick}");
        }

        private bool IsPendingMonsterBehaviorInputCurrent(PendingMonsterBehaviorUpdate update)
        {
            Monster monster = CombatRuntime.Instance.GetMonster(update.EntityId);
            if (monster == null
                || string.IsNullOrWhiteSpace(update.InstanceKey)
                || !CombatRuntime.Instance.MatchesInstance(monster, update.InstanceKey))
                return false;
            if (update.TargetEntityId != 0)
            {
                CombatRuntime.Instance.TryGetCombatTarget(update.TargetEntityId, out CombatTarget target);
                if (target == null
                    || !string.Equals(
                        RoomRuntime.NormalizeInstanceKey(target.InstanceKey),
                        RoomRuntime.NormalizeInstanceKey(update.InstanceKey),
                        StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            if (update.ConnId == 0)
                return true;
            return _connections.TryGetValue(update.ConnId, out RRConnection conn)
                && IsPendingConnectionInstanceCurrent(conn, update.InstanceKey);
        }

        private void ApplyPendingMonsterBehaviorInputs(uint simulationTick)
        {
            var releaseOrder = new List<uint>();
            var releaseSeen = new HashSet<uint>();
            int updateIndex = 0;
            while (updateIndex < _pendingMonsterBehaviorUpdates.Count)
            {
                PendingMonsterBehaviorUpdate update = _pendingMonsterBehaviorUpdates[updateIndex];
                if (!IsPendingMonsterBehaviorInputCurrent(update))
                {
                    _pendingMonsterBehaviorUpdates.RemoveAt(updateIndex);
                    continue;
                }
                if (update.SimulationApplied)
                {
                    if (update.PacketSent && update.WireSimulationApplyTick != 0)
                    {
                        if (update.Kind == PendingMonsterBehaviorKind.Attack
                            && simulationTick >= update.WireSimulationApplyTick)
                        {
                            Monster wireMonster = CombatRuntime.Instance.GetMonster(update.EntityId);
                            CombatRuntime.Instance.TryGetCombatTarget(update.TargetEntityId, out CombatTarget wireTarget);
                            bool wireCommitted = CombatRuntime.Instance.CommitMonsterAttackActionWireInput(
                                wireMonster,
                                wireTarget,
                                update.SessionId,
                                simulationTick);
                            if (wireCommitted)
                            {
                                if (releaseSeen.Add(update.EntityId))
                                    releaseOrder.Add(update.EntityId);
                                Debug.LogError($"[MON-ATTACK-WIRE-SIM] committed {wireMonster.Name}#{wireMonster.EntityId} target={update.TargetEntityId} session={update.SessionId} simulationTick={simulationTick} packetWriterTick={update.WireSimulationApplyTick} phase=before-entities sourceFunction=ClientEntityManager::update@0x005D9E30->ClientEntityManager::processMessage@0x005DA460->Behavior::processUpdate@0x00515620");
                            }
                        }
                        _pendingMonsterBehaviorUpdates.RemoveAt(updateIndex);
                        continue;
                    }
                    updateIndex++;
                    continue;
                }
                if (update.SimulationApplyTick == 0
                    || (update.Kind == PendingMonsterBehaviorKind.Follow && !update.PacketSent)
                    || (update.Kind == PendingMonsterBehaviorKind.AttackStop && !update.PacketSent)
                    || simulationTick < update.SimulationApplyTick)
                {
                    updateIndex++;
                    continue;
                }

                Monster monster = CombatRuntime.Instance.GetMonster(update.EntityId);
                bool committed;
                if (update.Kind == PendingMonsterBehaviorKind.Follow)
                {
                    CombatRuntime.Instance.TryGetCombatTarget(update.TargetEntityId, out CombatTarget target);
                    committed = CombatRuntime.Instance.CommitMonsterFollowBehaviorInput(monster, target, simulationTick);
                    if (committed)
                    {
                        string packetWriterTick = update.WireSimulationApplyTick == 0
                            ? "pending"
                            : update.WireSimulationApplyTick.ToString();
                        Debug.LogError($"[MON-FOLLOW-SIM] committed {monster.Name}#{monster.EntityId} target={update.TargetEntityId} session={update.SessionId} playerDamageTick={update.PlayerDamageTick} simulationTick={simulationTick} packetWriterTick={packetWriterTick} wireOrder={update.WireOrder} wireGeneration={update.BehaviorGenerationAtWire} wireCurrent={update.BehaviorCurrentAtWire} wirePending={update.BehaviorPendingAtWire} wireInterruptLocked={update.BehaviorInterruptLockedAtWire} phase=before-entities sourceFunction=ClientEntityManager::update@0x005D9E30->ClientEntityManager::processMessage@0x005DA460->Behavior::processUpdate@0x00515620->Follow::start@0x00526F70");
                    }
                }
                else if (update.Kind == PendingMonsterBehaviorKind.AttackStop)
                {
                    committed = CombatRuntime.Instance.CommitMonsterAttackStopBehaviorInput(monster, update.SessionId, simulationTick);
                    if (committed)
                        Debug.LogError($"[MON-ATTACK-STOP-SIM] committed {monster.Name}#{monster.EntityId} session={update.SessionId} simulationTick={simulationTick} packetWriterTick={update.WireSimulationApplyTick} wireOrder={update.WireOrder} wireGeneration={update.BehaviorGenerationAtWire} wireCurrent={update.BehaviorCurrentAtWire} wirePending={update.BehaviorPendingAtWire} wireInterruptLocked={update.BehaviorInterruptLockedAtWire} phase=before-entities sourceFunction=ClientEntityManager::update@0x005D9E30->ClientEntityManager::processMessage@0x005DA460->Behavior::processUpdate@0x00515620");
                }
                else
                {
                    CombatRuntime.Instance.TryGetCombatTarget(update.TargetEntityId, out CombatTarget target);
                    committed = update.PacketSent
                        && update.WireSimulationApplyTick != 0
                        && simulationTick >= update.WireSimulationApplyTick
                        ? CombatRuntime.Instance.CommitMonsterAttackActionWireInput(monster, target, update.SessionId, simulationTick)
                        : CombatRuntime.Instance.CommitMonsterAttackActionInput(monster, target, update.SessionId, simulationTick, update.ProximityAttackInput, update.UseTargetAction);
                    if (committed)
                        Debug.LogError($"[MON-ATTACK-SIM] committed {monster.Name}#{monster.EntityId} target={update.TargetEntityId} session={update.SessionId} simulationTick={simulationTick} packetWriterTick={update.WireSimulationApplyTick} wireOrder={update.WireOrder} wireGeneration={update.BehaviorGenerationAtWire} wireCurrent={update.BehaviorCurrentAtWire} wirePending={update.BehaviorPendingAtWire} wireInterruptLocked={update.BehaviorInterruptLockedAtWire} phase=before-entities sourceFunction=ClientEntityManager::update@0x005D9E30->ClientEntityManager::processMessage@0x005DA460->Behavior::processUpdate@0x00515620");
                }
                DungeonRunners.Combat.Behavior.MonsterBehavior2 appliedBehavior = monster?.Behavior;
                update.BehaviorGenerationAfterApply = appliedBehavior?.ActionGeneration ?? 0;
                update.BehaviorCurrentAfterApply = appliedBehavior?.CurrentAction ?? DungeonRunners.Combat.Behavior.MonsterBehavior2.ActionSlot.None;
                update.BehaviorAlternateAfterApply = appliedBehavior?.AlternateAction ?? DungeonRunners.Combat.Behavior.MonsterBehavior2.ActionSlot.None;
                update.BehaviorPendingAfterApply = appliedBehavior?.PendingAction ?? DungeonRunners.Combat.Behavior.MonsterBehavior2.ActionSlot.None;
                update.BehaviorInterruptLockedAfterApply = appliedBehavior?.CurrentActionInterruptLocked ?? false;
                Debug.LogError($"[MON-BEHAVIOR-ADMISSION] committed={committed} kind={update.Kind} entity={update.EntityId} target={update.TargetEntityId} session={update.SessionId} packetWriterTick={update.WireSimulationApplyTick} messageOrder={update.WireMessageOrder} simulationApplyTick={simulationTick} phase=before-entities beforeGeneration={update.BehaviorGenerationAtWire} beforeCurrent={update.BehaviorCurrentAtWire} beforeAlternate={update.BehaviorAlternateAtWire} beforePending={update.BehaviorPendingAtWire} beforeInterruptLocked={update.BehaviorInterruptLockedAtWire} afterGeneration={update.BehaviorGenerationAfterApply} afterCurrent={update.BehaviorCurrentAfterApply} afterAlternate={update.BehaviorAlternateAfterApply} afterPending={update.BehaviorPendingAfterApply} afterInterruptLocked={update.BehaviorInterruptLockedAfterApply} sourceFunction=ClientEntityManager::processMessage@0x005DA460->Behavior::processUpdate@0x00515620");
                if (committed && releaseSeen.Add(update.EntityId))
                    releaseOrder.Add(update.EntityId);
                if (!committed || update.PacketSent)
                {
                    _pendingMonsterBehaviorUpdates.RemoveAt(updateIndex);
                    continue;
                }
                update.SimulationApplied = true;
                _pendingMonsterBehaviorUpdates[updateIndex] = update;
                updateIndex++;
            }
            foreach (uint monsterEntityId in releaseOrder)
                CombatRuntime.Instance.ReleaseDeferredMonsterTargetScanMessage(monsterEntityId, simulationTick);
        }

        private static bool IsPendingMonsterBehaviorMessage(byte[] message, PendingMonsterBehaviorUpdate update)
        {
            if (message == null
                || message.Length < 4
                || message[0] != 0x35
                || (ushort)(message[1] | (message[2] << 8)) != (ushort)update.BehaviorId)
                return false;
            if (update.Kind == PendingMonsterBehaviorKind.AttackStop)
                return message[3] == 0x05;
            if (message[3] != 0x04 || message.Length < 5)
                return false;
            if (update.Kind == PendingMonsterBehaviorKind.Follow)
                return message[4] == 0x16;
            return update.Kind == PendingMonsterBehaviorKind.Attack
                && (message[4] == 0x50 || message[4] == 0xF0);
        }

        private void MarkPendingMonsterBehaviorMessagesFlushed(
            RRConnection conn,
            IReadOnlyList<byte[]> messages,
            uint packetWriterTick)
        {
            if (conn == null || messages == null || messages.Count == 0)
                return;
            for (int messageIndex = 0; messageIndex < messages.Count; messageIndex++)
            {
                for (int updateIndex = 0; updateIndex < _pendingMonsterBehaviorUpdates.Count; updateIndex++)
                {
                    PendingMonsterBehaviorUpdate update = _pendingMonsterBehaviorUpdates[updateIndex];
                    if (!update.PacketSent
                        || update.WireSimulationApplyTick != 0
                        || update.WireMessageOrder != 0)
                        continue;
                    RRConnection ownerConn = update.ConnId != 0
                        ? _connections.GetValueOrDefault(update.ConnId)
                        : FindConnectionByAvatarEntityId(update.TargetEntityId);
                    if (ownerConn == null
                        || ownerConn.ConnId != conn.ConnId
                        || !IsPendingMonsterBehaviorMessage(messages[messageIndex], update))
                        continue;
                    update.WireConnId = conn.ConnId;
                    update.WireMessageOrder = checked((uint)messageIndex + 1u);
                    _pendingMonsterBehaviorUpdates[updateIndex] = update;
                    Debug.LogError($"[MON-BEHAVIOR-WIRE-ORDER] kind={update.Kind} entity={update.EntityId} target={update.TargetEntityId} session={update.SessionId} conn={conn.ConnId} packetWriterTick={packetWriterTick} messageIndex={messageIndex}");
                    break;
                }
            }
        }

        private void MarkPendingMonsterBehaviorPacketsFlushed(uint packetWriterTick)
        {
            uint wireOrder = 0;
            var behaviorBarriers = new Dictionary<uint, uint>();
            for (int updateIndex = 0; updateIndex < _pendingMonsterBehaviorUpdates.Count; updateIndex++)
            {
                PendingMonsterBehaviorUpdate update = _pendingMonsterBehaviorUpdates[updateIndex];
                if ((update.Kind != PendingMonsterBehaviorKind.Follow
                        && update.Kind != PendingMonsterBehaviorKind.AttackStop
                        && update.Kind != PendingMonsterBehaviorKind.Attack)
                    || !update.PacketSent
                    || update.WireSimulationApplyTick != 0)
                    continue;
                wireOrder++;
                uint simulationApplyTick = packetWriterTick;
                if (behaviorBarriers.TryGetValue(update.EntityId, out uint behaviorBarrier)
                    && simulationApplyTick < behaviorBarrier)
                    simulationApplyTick = behaviorBarrier;
                if (update.SimulationApplied && update.SimulationApplyTick != 0)
                    simulationApplyTick = update.SimulationApplyTick;
                else if (update.Kind != PendingMonsterBehaviorKind.Follow
                    && update.Kind != PendingMonsterBehaviorKind.Attack
                    && update.SimulationApplyTick != 0
                    && simulationApplyTick < update.SimulationApplyTick)
                    simulationApplyTick = update.SimulationApplyTick;
                if (!update.SimulationApplied && simulationApplyTick > packetWriterTick)
                    behaviorBarriers[update.EntityId] = simulationApplyTick;
                update.WireSimulationApplyTick = packetWriterTick;
                Monster monster = CombatRuntime.Instance.GetMonster(update.EntityId);
                DungeonRunners.Combat.Behavior.MonsterBehavior2 behavior = monster?.Behavior;
                update.SimulationApplyTick = simulationApplyTick;
                update.WireOrder = update.WireMessageOrder != 0 ? update.WireMessageOrder : wireOrder;
                update.BehaviorGenerationAtWire = behavior?.ActionGeneration ?? 0;
                update.BehaviorCurrentAtWire = behavior?.CurrentAction ?? DungeonRunners.Combat.Behavior.MonsterBehavior2.ActionSlot.None;
                update.BehaviorAlternateAtWire = behavior?.AlternateAction ?? DungeonRunners.Combat.Behavior.MonsterBehavior2.ActionSlot.None;
                update.BehaviorPendingAtWire = behavior?.PendingAction ?? DungeonRunners.Combat.Behavior.MonsterBehavior2.ActionSlot.None;
                update.BehaviorInterruptLockedAtWire = behavior?.CurrentActionInterruptLocked ?? false;
                _pendingMonsterBehaviorUpdates[updateIndex] = update;
                if (update.Kind == PendingMonsterBehaviorKind.Follow
                    && monster != null
                    && monster.FollowInputPending
                    && monster.FollowInputTargetId == update.TargetEntityId
                    && (monster.FollowInputApplyTick == uint.MaxValue
                        || (!update.SimulationApplied && monster.FollowInputApplyTick != update.SimulationApplyTick)))
                    monster.FollowInputApplyTick = update.SimulationApplyTick;
                Debug.LogError($"[MON-BEHAVIOR-WIRE] flushed order={update.WireOrder} pendingOrder={wireOrder} wireConn={update.WireConnId} wireMessageOrder={update.WireMessageOrder} kind={update.Kind} entity={update.EntityId} target={update.TargetEntityId} session={update.SessionId} playerDamageTick={update.PlayerDamageTick} packetWriterTick={packetWriterTick} simulationApplyTick={update.SimulationApplyTick} wireSimulationApplyTick={packetWriterTick} generation={update.BehaviorGenerationAtWire} current={update.BehaviorCurrentAtWire} alternate={update.BehaviorAlternateAtWire} pending={update.BehaviorPendingAtWire} interruptLocked={update.BehaviorInterruptLockedAtWire} phase=before-entities sourceFunction=ServerEntityManager::update@0x005DEB90->ServerEntityManager::updateClients@0x005DF010");
            }
        }

        private void FlushPendingMonsterBehaviorUpdates(uint writerTick)
        {
            if (_pendingMonsterBehaviorUpdates.Count == 0)
                return;

            var pending = new List<PendingMonsterBehaviorUpdate>(_pendingMonsterBehaviorUpdates.Count);
            pending.AddRange(_pendingMonsterBehaviorUpdates);
            _pendingMonsterBehaviorUpdates.Clear();
            foreach (var update in pending)
            {
                if (!IsPendingMonsterBehaviorInputCurrent(update))
                    continue;
                switch (update.Kind)
                {
                    case PendingMonsterBehaviorKind.AttackStop:
                        FlushPendingMonsterAttackStopUpdate(update, writerTick);
                        break;
                    case PendingMonsterBehaviorKind.Attack:
                        FlushPendingMonsterAttackUpdate(update, writerTick);
                        break;
                    case PendingMonsterBehaviorKind.Follow:
                        FlushPendingMonsterFollowUpdate(update, writerTick);
                        break;
                }
            }
        }

        private ushort ResolveRemoteAvatarEntityId(RRConnection recipient, RRConnection targetOwnerConn, uint canonicalTargetId)
        {
            if (recipient != null && CombatRuntime.Instance.GetMonster(canonicalTargetId) != null)
                return checked((ushort)canonicalTargetId);
            if (recipient == null || targetOwnerConn == null) return 0;
            if (recipient.ConnId == targetOwnerConn.ConnId) return (ushort)canonicalTargetId;
            if (_remoteAvatarIds.TryGetValue(recipient.LoginName, out var avatarMap)
                && avatarMap.TryGetValue(targetOwnerConn.LoginName, out ushort remoteAvatarTargetId))
                return remoteAvatarTargetId;
            EnsureReplicaEntityIds(targetOwnerConn);
            return targetOwnerConn.ReplicaAvatarId;
        }

        public ushort ResolveRemoteAvatarEntityIdForViewer(RRConnection recipient, RRConnection targetOwnerConn)
        {
            if (recipient == null || targetOwnerConn == null)
                return 0;
            uint canonicalTargetId = GetPlayerAvatarId(targetOwnerConn.LoginName);
            if (canonicalTargetId == 0 && targetOwnerConn.Avatar != null && targetOwnerConn.Avatar.Id > 0)
                canonicalTargetId = (uint)targetOwnerConn.Avatar.Id;
            if (canonicalTargetId == 0 || canonicalTargetId > ushort.MaxValue)
                return 0;
            return ResolveRemoteAvatarEntityId(recipient, targetOwnerConn, canonicalTargetId);
        }

        private void FlushPendingMonsterFollowUpdate(PendingMonsterBehaviorUpdate update, uint writerTick)
        {
            Monster monster = CombatRuntime.Instance.GetMonster(update.EntityId);
            if (monster == null || !monster.IsAlive || CombatRuntime.Instance.PeekMonsterCurrentHPWire(monster) == 0)
                return;
            if (!update.PacketSent && writerTick < update.AdmissionTick)
            {
                uint directHitLocalApplyTick = update.DirectPlayerWeaponHitTick != 0
                    ? unchecked(update.DirectPlayerWeaponHitTick + (uint)(CLIENT_ENTITY_MESSAGE_RATIO + 1))
                    : 0;
                if (update.DirectPlayerWeaponHitTick != 0
                    && writerTick >= update.DirectPlayerWeaponHitTick
                    && writerTick <= directHitLocalApplyTick
                    && update.SimulationApplyTick == 0
                    && monster.FollowInputPending
                    && monster.FollowInputTargetId == update.TargetEntityId
                    && monster.FollowInputApplyTick == uint.MaxValue
                    && WeaponUseRuntime.Instance.WasDirectPlayerWeaponHitAppliedAtTick(update.TargetEntityId, update.DirectPlayerWeaponHitTick))
                {
                    uint localApplyTick = directHitLocalApplyTick;
                    update.SimulationApplyTick = localApplyTick;
                    monster.FollowInputApplyTick = localApplyTick;
                    Debug.LogError($"[MON-FOLLOW-SIM] scheduled {monster.Name}#{monster.EntityId} target={update.TargetEntityId} directHitTick={update.DirectPlayerWeaponHitTick} writerTick={writerTick} localApplyTick={localApplyTick} packetAdmissionTick={update.AdmissionTick} sourceFunction=ServerEntityManager::update@0x005DEB90->ClientEntityManager::update@0x005D9E30");
                }
                QueuePendingMonsterBehaviorUpdate(update, "MON-FOLLOW-DEFER");
                Debug.LogError($"[MON-FOLLOW-PACKET] deferred {monster.Name}#{monster.EntityId} target={update.TargetEntityId} writerTick={writerTick} admissionTick={update.AdmissionTick} sourceFunction=Follow::writeData");
                return;
            }
            if (!update.PacketSent
                && update.DirectPlayerWeaponHitTick != 0
                && writerTick == update.AdmissionTick)
            {
                if (WeaponUseRuntime.Instance.WasDirectPlayerWeaponHitAppliedAtTick(update.TargetEntityId, update.DirectPlayerWeaponHitTick))
                {
                    QueuePendingMonsterBehaviorUpdate(update, "MON-FOLLOW-DEFER-DIRECT-HIT-PHASE");
                    Debug.LogError($"[MON-FOLLOW-PACKET] deferred {monster.Name}#{monster.EntityId} target={update.TargetEntityId} writerTick={writerTick} directHitTick={update.DirectPlayerWeaponHitTick} admissionTick={update.AdmissionTick} sourceFunction=ServerEntityManager::update@0x005DEB90->ClientEntityManager::update@0x005D9E30");
                    return;
                }
            }
            RRConnection targetOwnerConn = FindConnectionByAvatarEntityId(update.TargetEntityId);
            if (!update.PacketSent)
            {
                bool sent = false;
                foreach (var recipient in GetMonsterInstanceRecipients(update.EntityId))
                {
                    if (recipient == null || !recipient.IsConnected) continue;
                    if (!EnsureMonsterSpawnQueuedForRecipient(recipient, monster, "MON-FOLLOW")) continue;
                    ushort remoteAvatarTargetId = ResolveRemoteAvatarEntityId(recipient, targetOwnerConn, update.TargetEntityId);
                    if (remoteAvatarTargetId == 0) continue;
                    EntitySynchInfoDecision decision = ResolveMonsterBehaviorWriterHPDecision(recipient, monster, (ushort)update.BehaviorId, 0x04, EntitySynchInfoContext.MonsterAction, "MON-FOLLOW", writerTick);
                    if (decision.HPWire == 0) continue;
                    if ((decision.Flags & 0x02) != 0 && !TryPrimeMonsterHPBeforeSynch(recipient, monster, decision.HPWire, "MON-FOLLOW"))
                        continue;
                    var packet = CombatPackets.BuildMonsterFollowPacket(update.BehaviorId, remoteAvatarTargetId, decision.ToResolved(monster.EntityId, update.BehaviorId, 0x04, "MON-FOLLOW client-writer"));
                    recipient.MessageQueue.Enqueue(packet);
                    sent = true;
                    if (VerboseMonsterDiag) Debug.LogError($"[MON-FOLLOW-FLUSH] {monster.Name}#{monster.EntityId} behavior={update.BehaviorId} target={update.TargetEntityId} remoteTarget={remoteAvatarTargetId} hp={decision.HPWire} conn={recipient.ConnId} sourceFunction=Follow::readData@0x5227A0");
                }
                if (!sent)
                {
                    QueuePendingMonsterBehaviorUpdate(update, "MON-FOLLOW-WAIT-RECIPIENT");
                    return;
                }
                update.PacketSent = true;
                monster.FollowClientVisible = true;
                Debug.LogError($"[MON-FOLLOW-PACKET] queued {monster.Name}#{monster.EntityId} target={update.TargetEntityId} writerTick={writerTick} simulationApplyTick=awaiting-wire-flush sourceFunction=Follow::writeData");
            }
            QueuePendingMonsterBehaviorUpdate(update, "MON-FOLLOW-SIM");
        }

        private void FlushPendingMonsterAttackStopUpdate(PendingMonsterBehaviorUpdate update, uint writerTick)
        {
            if (!_connections.TryGetValue(update.ConnId, out var targetConn) || targetConn == null || !targetConn.IsConnected)
                return;

            Monster monster = CombatRuntime.Instance.GetMonster(update.EntityId);
            CombatRuntime.Instance.TryGetCombatTarget(update.TargetEntityId, out CombatTarget target);
            if (monster == null || target == null)
                return;
            if (!monster.FollowClientVisible && !update.SimulationApplied)
            {
                update.Kind = PendingMonsterBehaviorKind.Attack;
                QueuePendingMonsterBehaviorUpdate(update, "MON-ATTACK");
                return;
            }
            if (!monster.IsAlive || CombatRuntime.Instance.PeekMonsterCurrentHPWire(monster) == 0)
            {
                monster.AttackClientVisible = false;
                return;
            }

            uint validationTick = unchecked(writerTick + 1);
            if (!update.PacketSent
                && TryGetPendingPlayerSkillDamageTick(update.TargetEntityId, update.EntityId, out uint pendingSkillDamageTick)
                && pendingSkillDamageTick == writerTick)
            {
                QueuePendingMonsterBehaviorUpdate(update, "MON-ATTACK-STOP-DEFER-SKILL-DAMAGE");
                Debug.LogError($"[MON-ATTACK-STOP-PACKET] deferred {monster.Name}#{monster.EntityId} target={update.TargetEntityId} writerTick={writerTick} pendingSkillDamageTick={pendingSkillDamageTick} sourceFunction=ServerEntityManager::updateClients@0x005DF010-before-ServerEntityManager::updateEntities@0x005E12F0");
                return;
            }
            if (!update.PacketSent
                && WeaponUseRuntime.Instance.TryGetPendingPlayerWeaponHitTick(update.TargetEntityId, update.EntityId, out uint pendingHitTick)
                && pendingHitTick >= validationTick)
            {
                QueuePendingMonsterBehaviorUpdate(update, "MON-ATTACK-STOP-DEFER-HIT");
                Debug.LogError($"[MON-ATTACK-STOP-PACKET] deferred {monster.Name}#{monster.EntityId} target={update.TargetEntityId} writerTick={writerTick} pendingHitTick={pendingHitTick} validationTick={validationTick} sourceFunction=Weapon::update-before-Behavior::processUpdate");
                return;
            }

            uint currentHPWire = CombatRuntime.Instance.PeekMonsterCurrentHPWire(monster);
            uint directHitLocalApplyTick = update.SimulationApplyTick != 0
                ? update.SimulationApplyTick
                : update.DirectPlayerWeaponHitTick != 0
                    ? unchecked(update.DirectPlayerWeaponHitTick + (uint)(CLIENT_ENTITY_MESSAGE_RATIO + 1))
                    : 0u;
            bool usePreLocalDamageHP = !update.SimulationApplied
                && update.DirectPlayerWeaponHitTick != 0
                && update.DirectPlayerWeaponHitPreviousHPWire > update.DirectPlayerWeaponHitNewHPWire
                && writerTick >= update.DirectPlayerWeaponHitTick
                && writerTick < directHitLocalApplyTick
                && currentHPWire == update.DirectPlayerWeaponHitNewHPWire;
            bool ownerUsesPreLocalDamageHP = false;
            if (!update.PacketSent)
            {
                bool sent = false;
                foreach (var recipient in GetMonsterInstanceRecipients(update.EntityId))
                {
                    if (recipient == null || !recipient.IsConnected) continue;
                    bool recipientMonsterSpawnWasSent = _monsterSpawnSentByConn.TryGetValue(recipient.ConnId, out HashSet<uint> recipientSpawnedMonsters)
                        && recipientSpawnedMonsters.Contains(monster.EntityId);
                    if (!EnsureMonsterSpawnQueuedForRecipient(recipient, monster, "MON-ATTACK-STOP")) continue;
                    bool recipientUsesPreLocalDamageHP = usePreLocalDamageHP
                        && recipient.ConnId == targetConn.ConnId
                        && recipientMonsterSpawnWasSent;
                    if (recipientUsesPreLocalDamageHP)
                        ownerUsesPreLocalDamageHP = true;
                    uint? forcedHPWire = recipientUsesPreLocalDamageHP
                        ? update.DirectPlayerWeaponHitPreviousHPWire
                        : (uint?)null;
                    EntitySynchInfoDecision decision = ResolveMonsterBehaviorWriterHPDecision(recipient, monster, (ushort)update.BehaviorId, 0x05, EntitySynchInfoContext.MonsterAction, "MON-ATTACK-STOP", writerTick, forcedHPWire);
                    if (decision.HPWire == 0) continue;
                    if ((decision.Flags & 0x02) != 0 && !TryPrimeMonsterHPBeforeSynch(recipient, monster, decision.HPWire, "MON-ATTACK-STOP"))
                        continue;
                    recipient.MessageQueue.Enqueue(CombatPackets.BuildMonsterActionStopPacket(
                        update.BehaviorId,
                        decision.ToResolved(monster.EntityId, update.BehaviorId, 0x05, "MON-ATTACK-STOP client-writer")));
                    sent = true;
                }
                if (!sent)
                {
                    QueuePendingMonsterBehaviorUpdate(update, "MON-ATTACK-STOP-WAIT-RECIPIENT");
                    return;
                }
                update.PacketSent = true;
                Debug.LogError($"[MON-ATTACK-STOP-PACKET] queued {monster.Name}->{target.Name} behavior={update.BehaviorId} session={update.SessionId} writerTick={writerTick} hpSource={(ownerUsesPreLocalDamageHP ? "client-prelocal-damage" : "simulation-current-hp")} simulationApplyTick=awaiting-wire-flush");
            }
            QueuePendingMonsterBehaviorUpdate(update, "MON-ATTACK-STOP-SIM");
        }

        private void FlushPendingMonsterAttackUpdate(PendingMonsterBehaviorUpdate update, uint writerTick)
        {
            if (!_connections.TryGetValue(update.ConnId, out var targetConn) || targetConn == null || !targetConn.IsConnected)
                return;

            Monster monster = CombatRuntime.Instance.GetMonster(update.EntityId);
            CombatRuntime.Instance.TryGetCombatTarget(update.TargetEntityId, out CombatTarget target);
            if (monster == null || target == null)
                return;
            if (monster.FollowClientVisible)
            {
                update.Kind = PendingMonsterBehaviorKind.AttackStop;
                FlushPendingMonsterAttackStopUpdate(update, writerTick);
                return;
            }
            if (writerTick < update.AdmissionTick)
            {
                QueuePendingMonsterBehaviorUpdate(update, "MON-ATTACK");
                Debug.LogError($"[MON-ATTACK-FLUSH-DEFER] {monster.Name}->{target.Name} behavior={update.BehaviorId} session={update.SessionId} admissionTick={update.AdmissionTick} writerTick={writerTick}");
                return;
            }
            if (update.SimulationApplyTick != 0 && !update.SimulationApplied)
            {
                QueuePendingMonsterBehaviorUpdate(update, "MON-ATTACK-DEFER-LOCAL-HIT");
                Debug.LogError($"[MON-ATTACK-PACKET] deferred {monster.Name}#{monster.EntityId} target={update.TargetEntityId} writerTick={writerTick} localApplyTick={update.SimulationApplyTick} sourceFunction=Weapon::update-before-Behavior::processUpdate");
                return;
            }
            if (update.SimulationApplyTick != 0
                && update.SimulationApplied
                && writerTick == update.SimulationApplyTick)
            {
                QueuePendingMonsterBehaviorUpdate(update, "MON-ATTACK-DEFER-DIRECT-HIT-PHASE");
                Debug.LogError($"[MON-ATTACK-PACKET] deferred {monster.Name}#{monster.EntityId} target={update.TargetEntityId} writerTick={writerTick} localApplyTick={update.SimulationApplyTick} sourceFunction=ServerEntityManager::update@0x005DEB90->ClientEntityManager::update@0x005D9E30");
                return;
            }
            if (!update.PacketSent
                && TryGetPendingPlayerSkillDamageTick(update.TargetEntityId, update.EntityId, out uint pendingSkillDamageTick)
                && pendingSkillDamageTick == writerTick)
            {
                QueuePendingMonsterBehaviorUpdate(update, "MON-ATTACK-DEFER-SKILL-DAMAGE");
                Debug.LogError($"[MON-ATTACK-PACKET] deferred {monster.Name}#{monster.EntityId} target={update.TargetEntityId} writerTick={writerTick} pendingSkillDamageTick={pendingSkillDamageTick} sourceFunction=ServerEntityManager::updateClients@0x005DF010-before-ServerEntityManager::updateEntities@0x005E12F0");
                return;
            }
            if (!update.PacketSent
                && WeaponUseRuntime.Instance.TryGetPendingPlayerWeaponHitTick(update.TargetEntityId, update.EntityId, out uint pendingHitTick)
                && pendingHitTick == writerTick)
            {
                QueuePendingMonsterBehaviorUpdate(update, "MON-ATTACK-DEFER-HIT");
                if (ServerDiagnostics.IsEnabled("desyncForensicsTracking"))
                    Debug.LogError($"[MON-ATTACK-PACKET] deferred {monster.Name}#{monster.EntityId} target={update.TargetEntityId} writerTick={writerTick} pendingHitTick={pendingHitTick} sourceFunction=Weapon::update@0x00591980-before-Behavior::processUpdate@0x00515620");
                CombatRuntime.Instance.TraceDesyncForensics(monster, "packet-defer", "MON-ATTACK-DEFER-HIT", target, null, null, null, writerTick, null);
                return;
            }
            bool targetMonsterSpawnWasSent = _monsterSpawnSentByConn.TryGetValue(targetConn.ConnId, out HashSet<uint> targetSpawnedMonsters)
                && targetSpawnedMonsters.Contains(monster.EntityId);
            if (!EnsureMonsterSpawnQueuedForRecipient(targetConn, monster, "MON-ATTACK"))
                return;
            if (!monster.IsAlive || CombatRuntime.Instance.PeekMonsterCurrentHPWire(monster) == 0)
            {
                monster.AttackClientVisible = false;
                Debug.LogError($"[MON-ATTACK-FLUSH-SKIP] dead {monster.Name}#{monster.EntityId} behavior={update.BehaviorId} session={update.SessionId} queuedTick={update.QueuedTick} writerTick={writerTick}");
                return;
            }

            uint currentHPWire = CombatRuntime.Instance.PeekMonsterCurrentHPWire(monster);
            uint directHitLocalApplyTick = update.DirectPlayerWeaponHitTick != 0
                ? unchecked(update.DirectPlayerWeaponHitTick + (uint)(CLIENT_ENTITY_MESSAGE_RATIO + 1))
                : 0u;
            bool usePreLocalDamageHP = update.DirectPlayerWeaponHitTick != 0
                && update.DirectPlayerWeaponHitPreviousHPWire > update.DirectPlayerWeaponHitNewHPWire
                && writerTick >= update.DirectPlayerWeaponHitTick
                && writerTick < directHitLocalApplyTick
                && currentHPWire == update.DirectPlayerWeaponHitNewHPWire;
            bool ownerUsesPreLocalDamageHP = usePreLocalDamageHP && targetMonsterSpawnWasSent;
            uint? ownerForcedHPWire = ownerUsesPreLocalDamageHP
                ? update.DirectPlayerWeaponHitPreviousHPWire
                : (uint?)null;
            EntitySynchInfoDecision decision = ResolveMonsterBehaviorWriterHPDecision(targetConn, monster, (ushort)update.BehaviorId, 0x04, EntitySynchInfoContext.MonsterAction, "MON-ATTACK", writerTick, ownerForcedHPWire);
            if (!monster.IsAlive || decision.HPWire == 0 || CombatRuntime.Instance.PeekMonsterCurrentHPWire(monster) == 0)
            {
                monster.AttackClientVisible = false;
                Debug.LogError($"[MON-ATTACK-FLUSH-SKIP] client-writer-dead {monster.Name}#{monster.EntityId} behavior={update.BehaviorId} session={update.SessionId} hp={decision.HPWire} queuedTick={update.QueuedTick} writerTick={writerTick}");
                return;
            }
            if ((decision.Flags & 0x02) != 0 && !TryPrimeMonsterHPBeforeSynch(targetConn, monster, decision.HPWire, "MON-ATTACK"))
            {
                string hpState = CombatRuntime.Instance.DescribeMonsterHPState(monster);
                Debug.LogError($"[MON-ATTACK] primer unresolved; continuing with client-writer HP {monster.Name}->{target.Name} behavior={update.BehaviorId} session={update.SessionId} hp={decision.HPWire} hpState={hpState}");
            }
            bool ownerActionAlreadyAdmitted = IsOwnerMonsterAttackAlreadyAdmitted(update, targetConn, monster);
            if (!ownerActionAlreadyAdmitted)
            {
                byte[] packet = CombatPackets.BuildMonsterAttackPacket(
                    monster.EntityId,
                    update.BehaviorId,
                    (ushort)update.TargetEntityId,
                    update.UseFlags,
                    decision.ToResolved(monster.EntityId, update.BehaviorId, 0x04, "MON-ATTACK client-writer"),
                    update.UseTargetAction,
                    false);
                targetConn.MessageQueue.Enqueue(packet);
            }
            foreach (var recipient in GetMonsterInstanceRecipients(update.EntityId))
            {
                if (recipient == null || recipient.ConnId == targetConn.ConnId || !recipient.IsConnected) continue;
                if (!EnsureMonsterSpawnQueuedForRecipient(recipient, monster, "MON-ATTACK")) continue;
                ushort remoteAvatarTargetId = ResolveRemoteAvatarEntityId(recipient, targetConn, update.TargetEntityId);
                if (remoteAvatarTargetId == 0) continue;
                EntitySynchInfoDecision recipientDecision = usePreLocalDamageHP
                    ? ResolveMonsterBehaviorWriterHPDecision(recipient, monster, (ushort)update.BehaviorId, 0x04, EntitySynchInfoContext.MonsterAction, "MON-ATTACK", writerTick)
                    : decision;
                if (recipientDecision.HPWire == 0)
                    continue;
                if ((recipientDecision.Flags & 0x02) != 0)
                    TryPrimeMonsterHPBeforeSynch(recipient, monster, recipientDecision.HPWire, "MON-ATTACK");
                byte[] recipientPacket = CombatPackets.BuildMonsterAttackPacket(
                    monster.EntityId,
                    update.BehaviorId,
                    remoteAvatarTargetId,
                    update.UseFlags,
                    recipientDecision.ToResolved(monster.EntityId, update.BehaviorId, 0x04, "MON-ATTACK client-writer"),
                    update.UseTargetAction,
                    false);
                recipient.MessageQueue.Enqueue(recipientPacket);
            }
            if (ownerActionAlreadyAdmitted)
            {
                monster.AttackClientVisible = true;
                LogPlayerHPVisibleEvent(targetConn, $"MonsterAttackStarted {monster.Name}#{monster.EntityId}");
                Debug.LogError($"[MON-ATTACK-FLUSH] client AttackTarget2 {monster.Name}->{target.Name} behavior={update.BehaviorId} session={update.SessionId} flags={update.UseFlags} target={update.TargetEntityId} hp={decision.HPWire} hpSource={(ownerUsesPreLocalDamageHP ? "client-prelocal-damage" : "simulation-current-hp")} ownerAction=already-admitted queuedTick={update.QueuedTick} writerTick={writerTick} localApplyTick={update.SimulationApplyTick} queue={targetConn.MessageQueue.Count}");
                return;
            }
            if (!update.SimulationApplied && update.SimulationApplyTick == 0)
                update.SimulationApplyTick = writerTick;
            update.PacketSent = true;
            QueuePendingMonsterBehaviorUpdate(update, "MON-ATTACK-SIM");
            monster.AttackClientVisible = true;
            LogPlayerHPVisibleEvent(targetConn, $"MonsterAttackStarted {monster.Name}#{monster.EntityId}");
            Debug.LogError($"[MON-ATTACK-FLUSH] client {(update.UseTargetAction ? "UseTarget" : "AttackTarget2")} {monster.Name}->{target.Name} behavior={update.BehaviorId} session={update.SessionId} flags={update.UseFlags} target={update.TargetEntityId} hp={decision.HPWire} hpSource={(ownerUsesPreLocalDamageHP ? "client-prelocal-damage" : "simulation-current-hp")} ownerAction=wire queuedTick={update.QueuedTick} writerTick={writerTick} localApplyTick={update.SimulationApplyTick} queue={targetConn.MessageQueue.Count}");
        }

        private static bool IsOwnerMonsterAttackAlreadyAdmitted(PendingMonsterBehaviorUpdate update, RRConnection targetConn, Monster monster)
        {
            return update.SimulationApplied
                && !update.UseTargetAction
                && !update.ProximityAttackInput
                && update.DirectPlayerWeaponHitTick == 0
                && targetConn != null
                && targetConn.Avatar != null
                && targetConn.Avatar.Id == update.TargetEntityId
                && monster?.Behavior?.AttackTarget2Active == true;
        }

        private bool EnsureMonsterSpawnQueuedForRecipient(RRConnection recipient, Monster monster, string source)
        {
            if (recipient == null || monster == null || !recipient.IsConnected || !recipient.IsSpawned)
                return false;
            if (_monsterSpawnSentByConn.TryGetValue(recipient.ConnId, out HashSet<uint> sentMonsters)
                && sentMonsters.Contains(monster.EntityId))
                return true;

            SendMonsterToClient(recipient, monster, true);
            bool queued = _monsterSpawnSentByConn.TryGetValue(recipient.ConnId, out sentMonsters)
                && sentMonsters.Contains(monster.EntityId);
            Debug.LogError($"[MON-LIFECYCLE] source={source} entity={monster.EntityId} behavior={monster.BehaviorId} conn={recipient.ConnId} action={(queued ? "spawn-before-update" : "block-update")} sourceFunction=ClientEntityManager::processComponentUpdate@0x005DB520");
            return queued;
        }

        private EntitySynchInfoDecision ResolveMonsterBehaviorWriterHPDecision(RRConnection conn, Monster monster, ushort componentId, byte subtype, EntitySynchInfoContext context, string packetName, uint writerTick, uint? forcedHPWire = null)
        {
            EntitySynchInfoAuthority.Instance.RegisterMonster(monster);
            uint beforeHP = CombatRuntime.Instance.PeekMonsterCurrentHPWire(monster);

            string runtimeInstanceKey = !string.IsNullOrWhiteSpace(monster.InstanceKey) ? monster.InstanceKey : monster.ZoneName;
            CombatRuntime.EntitySynchInfoVisibilityCutoff hpCutoff = CombatRuntime.Instance.GetEntitySynchInfoValidationCutoff(context, $"{packetName} client-writer");
            int rngPos = CombatRuntime.Instance.GetRoomRngPosForInstance(runtimeInstanceKey);
            CombatRuntime.Instance.TryResolveMonsterEntitySynchInfoHP(monster, context, packetName, hpCutoff, out uint resolvedHPWire, out string resolvedHPReason);
            uint hpWire = resolvedHPWire;
            string hpReason = resolvedHPReason;
            if (forcedHPWire.HasValue)
            {
                hpWire = Math.Min(forcedHPWire.Value, monster.MaxHPWire);
                hpReason = $"{resolvedHPReason}; client-prelocal-damage; authoritativeHP={resolvedHPWire}";
            }
            EntitySynchInfoAuthority.Instance.RecordMonsterOutboundHP(monster, hpWire, $"{packetName} {hpReason}");
            if (conn != null)
                conn.EntitySchedulerMirror.ObserveSuffixCutoff(runtimeInstanceKey, hpCutoff.Tick, hpCutoff.IncludeSubEntityEffects, hpCutoff.Phase, packetName);
            CombatRuntime.Instance.TraceDesyncForensics(monster, "entity-synch-outbound", packetName, CombatRuntime.Instance.GetPlayer(monster.TargetId), beforeHP, hpWire, null, writerTick, hpCutoff.Tick);
            string provenance = $"{hpReason}; beforeHP={beforeHP}; writerTick={writerTick}; visibleCutoffTick={hpCutoff.Tick}; cutoffPhase={hpCutoff.Phase}; includeSubEntity={hpCutoff.IncludeSubEntityEffects}; cutoffReason={hpCutoff.Reason}; lastEntity={hpCutoff.LastEntityTick}; lastSubEntity={hpCutoff.LastSubEntityTick}";
            return EntitySynchInfoDecision.HP(EntitySynchInfoOwner.Monster, hpWire, $"{packetName} {hpReason}", monster.EntityId, componentId, subtype, provenance, hpCutoff.Tick, runtimeInstanceKey, conn?.EntitySchedulerMirror.SchedulerTick ?? writerTick, hpCutoff.IncludeSubEntityEffects, rngPos, hpReason);
        }

        private sealed class EncounterObjectTickSnapshot
        {
            public readonly List<RRConnection> Connections = new List<RRConnection>();
        }

        private Dictionary<string, EncounterObjectTickSnapshot> BuildEncounterObjectTickSnapshots()
        {
            var snapshots = new Dictionary<string, EncounterObjectTickSnapshot>(StringComparer.OrdinalIgnoreCase);
            foreach (RRConnection conn in GetConnectionInsertionOrderSnapshot())
            {
                if (conn == null || !conn.IsConnected || !conn.IsSpawned || !conn.AllowFlush || !conn.TickUpdatesActive || conn.Avatar == null)
                    continue;
                string instanceKey = RoomRuntime.NormalizeInstanceKey(conn.RuntimeInstanceKey);
                if (string.IsNullOrEmpty(instanceKey) || !ZoneSpawner.Instance.HasEncounterObjects(instanceKey))
                    continue;
                if (!snapshots.TryGetValue(instanceKey, out EncounterObjectTickSnapshot snapshot))
                {
                    snapshot = new EncounterObjectTickSnapshot();
                    snapshots[instanceKey] = snapshot;
                }
                snapshot.Connections.Add(conn);
            }
            return snapshots;
        }

        private void TickEncounterObjectForEntity(uint entityId, uint simulationTick, Dictionary<string, EncounterObjectTickSnapshot> snapshots)
        {
            if (snapshots == null
                || !CombatRuntime.Instance.TryGetEncounterObjectInstanceKey(entityId, out string instanceKey)
                || !snapshots.TryGetValue(instanceKey, out EncounterObjectTickSnapshot snapshot))
                return;

            var positions = new List<(int x, int y)>(snapshot.Connections.Count);
            foreach (RRConnection instanceConnection in snapshot.Connections)
            {
                CombatPlayer player = CombatRuntime.Instance.GetPlayer((uint)instanceConnection.Avatar.Id);
                if (player != null)
                    positions.Add((player.ClientSimulationPosFixedX, player.ClientSimulationPosFixedY));
                else
                    Debug.LogError($"[ENCOUNTER-OBJECT] entity={entityId} conn={instanceConnection.ConnId} state=missing-combat-player position=excluded");
            }
            List<Monster> newly = ZoneSpawner.Instance.UpdateEncounterObject(entityId, instanceKey, positions);
            if (newly == null || newly.Count == 0)
                return;

            foreach (Monster monster in newly)
            {
                monster.AggroTriggered = false;
                monster.State = MonsterState.Idle;
                monster.TargetId = 0;
                foreach (RRConnection instanceConnection in snapshot.Connections)
                    SendMonsterToClient(instanceConnection, monster, true);
            }
            Debug.LogError($"[ENCOUNTER-OBJECT] tick={simulationTick} entity={entityId} replicated={newly.Count} conns={snapshot.Connections.Count} instance='{instanceKey}' sourceFunction=EncounterObject::update+SendMonsterToClient");
        }

        private readonly Dictionary<int, HashSet<uint>> _encounterObjectSentByConn = new Dictionary<int, HashSet<uint>>();
        private readonly Dictionary<int, HashSet<uint>> _monsterSpawnSentByConn = new Dictionary<int, HashSet<uint>>();

        private void ReassignMonsterOwnership(RRConnection conn)
        {
            var owned = _monsterOwnerConnId.Where(e => e.Value == conn.ConnId).Select(e => e.Key).ToList();
            foreach (uint entityId in owned)
            {
                Monster monster = CombatRuntime.Instance.GetMonster(entityId);
                RRConnection next = null;
                if (monster != null)
                {
                    foreach (var other in GetConnectionInsertionOrderSnapshot())
                    {
                        if (other == null || other == conn || !other.IsSpawned || !other.IsConnected) continue;
                        if (!string.Equals(other.RuntimeInstanceKey, monster.InstanceKey, StringComparison.OrdinalIgnoreCase)) continue;
                        next = other;
                        break;
                    }
                }
                if (next != null)
                    _monsterOwnerConnId[entityId] = next.ConnId;
                else
                    _monsterOwnerConnId.Remove(entityId);
            }
        }

        private bool EnsureEncounterObject(RRConnection conn, Monster monster, bool queueForTickFlush)
        {
            if (string.IsNullOrWhiteSpace(monster.EncounterGroupKey))
                return true;
            if (!ZoneSpawner.Instance.TryGetEncounterObjectEntityId(monster.InstanceKey, monster.EncounterGroupKey, out uint encId))
            {
                Debug.LogError($"[ENCOUNTER-OBJECT-SPAWN] group='{monster.EncounterGroupKey}' instance='{monster.InstanceKey}' monster={monster.EntityId} state=missing-root");
                return false;
            }
            monster.EncounterObjectEntityId = encId;
            if (!_encounterObjectSentByConn.TryGetValue(conn.ConnId, out var sent))
            {
                sent = new HashSet<uint>();
                _encounterObjectSentByConn[conn.ConnId] = sent;
            }
            if (sent.Contains(encId))
                return true;
            byte[] encounterObjectPacket = CombatPackets.BuildEncounterObjectSpawnPacket(encId, monster.PosFixedX, monster.PosFixedY, monster.PosFixedZ, monster.HeadingFixed);
            if (queueForTickFlush)
            {
                if (!QueueClientEntityStream(conn, encounterObjectPacket))
                    return false;
            }
            else
                SendToClient(conn, encounterObjectPacket);
            sent.Add(encId);
            Debug.LogError($"[ENCOUNTER-OBJECT-SPAWN] encId={encId} group='{monster.EncounterGroupKey}' instance='{monster.InstanceKey}' posFixed8=({monster.PosFixedX},{monster.PosFixedY}) conn={conn.ConnId} sourceFunction=EncounterObject::writeInit@0x562B40");
            return true;
        }

        private bool QueueDeferredEncounterObjectRootSnapshot(RRConnection conn, uint encounterObjectId, string instanceKey)
        {
            if (conn == null || encounterObjectId == 0 || !conn.IsConnected || !conn.IsSpawned)
                return false;
            if (!_encounterObjectSentByConn.TryGetValue(conn.ConnId, out HashSet<uint> sent))
            {
                sent = new HashSet<uint>();
                _encounterObjectSentByConn[conn.ConnId] = sent;
            }
            if (sent.Contains(encounterObjectId))
                return true;
            if (!ZoneSpawner.Instance.TryGetDeferredEncounterObjectSpawnSnapshot(
                instanceKey,
                encounterObjectId,
                out int posFixedX,
                out int posFixedY,
                out int posFixedZ,
                out int headingFixed))
                return false;
            byte[] packet = CombatPackets.BuildEncounterObjectSpawnPacket(encounterObjectId, posFixedX, posFixedY, posFixedZ, headingFixed);
            if (!QueueClientEntityStream(conn, packet))
                return false;
            sent.Add(encounterObjectId);
            Debug.LogError($"[ENCOUNTER-OBJECT-SPAWN] encId={encounterObjectId} instance='{instanceKey}' posFixed=({posFixedX},{posFixedY},{posFixedZ}) headingFixed={headingFixed} conn={conn.ConnId} phase=room-writer sourceFunction=ServerEntityManager::writeInitMessages@0x005DF6F0->EncounterObject::writeInit@0x00562B40");
            return true;
        }

        public void SendMonsterToClient(RRConnection conn, Monster monster)
        {
            SendMonsterToClient(conn, monster, true);
        }

        private void SendMonsterToClient(RRConnection conn, Monster monster, bool queueForTickFlush)
        {
            if (!_monsterSpawnSentByConn.TryGetValue(conn.ConnId, out var sentMonsters))
            {
                sentMonsters = new HashSet<uint>();
                _monsterSpawnSentByConn[conn.ConnId] = sentMonsters;
            }
            if (sentMonsters.Contains(monster.EntityId))
            {
                Debug.LogError($"[SPAWN-DEDUP] entity={monster.EntityId} name='{monster.Name}' conn={conn.ConnId} state=already-sent");
                return;
            }
            ushort targetId = 0;
            ushort summonOwnerId = ResolveCombatSummonOwnerForViewer(conn, monster);
            if (monster.Summoner != null && summonOwnerId == 0)
                return;
            if (!EnsureEncounterObject(conn, monster, queueForTickFlush))
                return;
            if (!ResolveEntitySynchInfoForComponent(conn, (ushort)monster.BehaviorId, 0x04, EntitySynchInfoContext.EntityInitPrimer, monster.EntityId, "SPAWN-PKT", out EntitySynchInfoDecision spawnDecision)
                || (spawnDecision.Flags & 0x02) == 0)
            {
                spawnDecision = ResolveMonsterRuntimeHPDecision(monster, "SPAWN-PKT", spawnDecision.Reason);
            }
            byte[] packet = CombatPackets.BuildMonsterSpawnPacket(
                monster,
                monster.BehaviorId,
                monster.SkillsId,
                monster.ManipulatorsId,
                monster.ModifiersId,
                targetId,
                summonOwnerId,
                spawnDecision.ToResolved(monster.EntityId, monster.BehaviorId, 0x04, "SPAWN-PKT")
            );
            if (queueForTickFlush)
            {
                if (!QueueClientEntityStream(conn, packet))
                    return;
            }
            else
                SendToClient(conn, packet);
            sentMonsters.Add(monster.EntityId);
            if (!_monsterOwnerConnId.ContainsKey(monster.EntityId))
                _monsterOwnerConnId[monster.EntityId] = conn.ConnId;
            if (conn.Avatar != null)
                CombatRuntime.Instance.ResetMonsterClientVisiblePositionFixed(monster, (uint)conn.Avatar.Id, monster.PosFixedX, monster.PosFixedY, "SPAWN-PKT-COMMIT");
            CombatRuntime.Instance.RecordMonsterOutboundHP(monster, spawnDecision.HPWire, "SPAWN-PKT");

            Debug.LogError($"[SPAWN] Monster {monster.Name} spawned with RNG seed 0x{monster.RngSeed:X8}");

        }





        private void HandleMonsterAggro(Monster monster, CombatTarget player)
        {
            if (monster == null || player == null) return;
            uint admissionTick = unchecked(CombatRuntime.Instance.CombatTick + 2);
            uint directPlayerWeaponHitTick = monster.FollowInputDirectPlayerWeaponHitTick;
            uint playerDamageTick = monster.FollowInputPlayerDamageTick;
            uint simulationApplyTick = monster.FollowInputApplyTick == uint.MaxValue
                ? 0u
                : monster.FollowInputApplyTick;
            QueuePendingMonsterBehaviorUpdate(new PendingMonsterBehaviorUpdate
            {
                Kind = PendingMonsterBehaviorKind.Follow,
                EntityId = monster.EntityId,
                BehaviorId = monster.BehaviorId,
                TargetEntityId = player.EntityId,
                AdmissionTick = admissionTick,
                SimulationApplyTick = simulationApplyTick,
                DirectPlayerWeaponHitTick = directPlayerWeaponHitTick,
                PlayerDamageTick = playerDamageTick,
                QueuedTick = _combatTick
            }, "MON-FOLLOW");
            Debug.LogError($"[SERVER-AGGRO] follow-broadcast {monster.Name}#{monster.EntityId} -> {player.Name}#{player.EntityId} behavior={monster.BehaviorId}");
        }
        void Start()
        {
            try
            {
                StartGameRuntime();
            }
            catch (Exception ex)
            {
                StartupLog.Failure("Game runtime", ex.Message);
                Debug.LogError($"[SERVER-STARTUP] state=fatal component=GameServer message='{ex.Message}'");
                Application.Quit();
                throw;
            }
        }

        private void StartGameRuntime()
        {

            CombatRuntime.Instance.SetNextClientEntityUpdateTick(checked(_combatTick + (uint)(CLIENT_ENTITY_MESSAGE_RATIO + 1)));

            RandomStreams.EnsureGlobalStaticSeededFromTime64("GameServer.Start");
            Database.GameDatabase.Initialize();
            Database.CharacterRepository.HasPendingPvpJournalFence = HasPendingPvpResultJournalFence;
            Database.CharacterRepository.HasAnyPendingPvpJournalFence = () => _pendingPvpResultJournal.Count != 0;
            RestorePendingPvpResultJournal();
            RestorePendingDisconnectedCharacterSpool();
            Gameplay.PVPMatchmaking.Instance.PersistEndedMatch = PersistPvpMatchResultOutbox;
            DrainPendingPvpMatchResults();
            StartupLog.Ready("Databases", "runtime.db | authored.db | cache.db");
            ServerSettings.Load();
            StartServer();

            Debug.LogError("[MEMBER] Using accounts.is_member column (1=member, 0=free)");
            DungeonRunners.Networking.DRLog.InitFromConfig();
            bool packageCatalogLoaded = DungeonRunners.Data.PackageCatalog.Instance.LoadFromAssets();
            if (!packageCatalogLoaded
                || DungeonRunners.Data.PackageCatalog.Instance.GcTextDocumentCount != DungeonRunners.Data.PackageCatalog.Instance.ManifestGcTextDocs)
                throw new InvalidDataException("Database authored package GC catalog is incomplete");
            DungeonRunners.Data.GCDatabase.Instance.Load();
            DungeonRunners.Data.DescriptorCatalog.Instance.BuildFromGCDatabase(DungeonRunners.Data.GCDatabase.Instance);
            DungeonRunners.Data.DescriptorCatalog.Instance.RunStartupCheck();
            WorldCollision.Instance.RunStartupCheck();

            DungeonRunners.Data.ItemStatDatabase.Instance.Load();
            AuthoredGameplayCatalog.LoadAll();
            Database.GameDatabase.ValidateDerivedCaches();
            Combat.SpellDatabase.Initialize();
            DungeonMazeSpawner.RunStartupManifestCheck();
            GCObjectGeneratorTable.Instance.Initialize();
            WorldEntitySpawner.Instance.Initialize();

            ClassConfig.Load();
            FallbackAudit.RunStartupCoverage();


            LoadZones();
            _unitContainer = new UnitContainer(this);
            _equipment = new Equipment(this);
            CombatRuntime.Instance.OnMonsterSpawned += OnMonsterSpawned;
            CombatRuntime.Instance.ResolveMonsterDifficulty = ResolveSpawnDifficulty;
            CombatRuntime.Instance.OnPlayerHitEvent += OnPlayerFriendlySummonHit;
            CombatRuntime.Instance.OnMonsterDespawned += OnMonsterDespawned;
            CombatRuntime.Instance.OnStockUnitDead += OnStockUnitDead;
            CombatRuntime.Instance.OnStockUnitRespawned += OnStockUnitRespawned;
            CombatRuntime.Instance.OnMonsterAttackStarted += OnMonsterAttackStarted;
            CombatRuntime.Instance.OnMonsterFollowReachedNearStop += OnMonsterFollowReachedNearStop;
            CombatRuntime.Instance.OnMonsterAttackResolved += OnMonsterAttackResolved;
            CombatRuntime.Instance.OnPlayerDamageResolved += OnPlayerDamageResolved;
            CombatRuntime.Instance.OnPlayerStunActionResolved += OnPlayerStunActionResolved;
            CombatRuntime.Instance.OnPlayerModifierNetworkEvent += OnPlayerModifierNetworkEvent;
            CombatRuntime.Instance.OnPlayerAttributeModifierRemoved += OnPlayerAttributeModifierRemoved;
            CombatRuntime.Instance.OnPlayerAttributeModifiersRemovingForDeath += OnPlayerAttributeModifiersRemovingForDeath;
            CombatRuntime.Instance.OnMonsterAggro += HandleMonsterAggro;
            DungeonRunners.Talkback.TalkbackServer.Instance.ResolveMemberFlag = userId =>
            {
                foreach (var memberConn in GetConnectionInsertionOrderSnapshot())
                {
                    if (memberConn == null || string.IsNullOrEmpty(memberConn.LoginName)) continue;
                    if (GetCharSqlId(memberConn) != (uint)userId) continue;
                    return !IsPlayerFree(memberConn.LoginName);
                }
                return true;
            };
            StartupLog.Ready("Game data", $"zones={_zones.Count} creatures={AuthoredGameplayCatalog.Creatures.Count} skills={AuthoredGameplayCatalog.Skills.Count}");
            InitializeTownNPCs();
            InitializeZonePortals();
            InitializeZoneCheckpoints();
            QuestManager.Instance.SetSendCallback(SendCompressedA);
            QuestManager.Instance.SetEntitySynchCallback(WritePlayerEntitySynch);
            QuestManager.Instance.SetPlayerContextCallback(GetPlayerState);
            QuestManager.Instance.SetZoneQuestGiversCallback(conn =>
                TryGetZoneNpcsForConnection(conn, out List<ZoneNPC> npcs)
                    ? npcs.Select(npc => npc.GCClass)
                    : Enumerable.Empty<string>());
            QuestManager.Instance.SetItemObjectiveCallbacks(CountQuestItems, GetQuestItemRequiredQuantity);

            CompleteServerStartup();
        }

        void OnDestroy()
        {
            if (CombatRuntime.Instance.ResolveMonsterDifficulty == ResolveSpawnDifficulty)
                CombatRuntime.Instance.ResolveMonsterDifficulty = null;
            CombatRuntime.Instance.OnMonsterAttackStarted -= OnMonsterAttackStarted;
            CombatRuntime.Instance.OnMonsterFollowReachedNearStop -= OnMonsterFollowReachedNearStop;
            CombatRuntime.Instance.OnMonsterAttackResolved -= OnMonsterAttackResolved;
            CombatRuntime.Instance.OnPlayerHitEvent -= OnPlayerFriendlySummonHit;
            CombatRuntime.Instance.OnStockUnitDead -= OnStockUnitDead;
            CombatRuntime.Instance.OnStockUnitRespawned -= OnStockUnitRespawned;
            CombatRuntime.Instance.OnPlayerDamageResolved -= OnPlayerDamageResolved;
            CombatRuntime.Instance.OnPlayerStunActionResolved -= OnPlayerStunActionResolved;
            CombatRuntime.Instance.OnPlayerModifierNetworkEvent -= OnPlayerModifierNetworkEvent;
            CombatRuntime.Instance.OnPlayerAttributeModifierRemoved -= OnPlayerAttributeModifierRemoved;
            CombatRuntime.Instance.OnPlayerAttributeModifiersRemovingForDeath -= OnPlayerAttributeModifiersRemovingForDeath;
            CombatRuntime.Instance.OnMonsterAggro -= HandleMonsterAggro;
            StopServer();
        }

        private bool IsPortal(ushort entityId, out ZonePortal portal)
        {
            bool found = _portalEntities.TryGetValue(entityId, out portal);
            Debug.LogError($"[IS-PORTAL] entityId=0x{entityId:X4} found={found} count={_portalEntities.Count}");
            if (!found)
                Debug.LogError($"[IS-PORTAL] knownIds={string.Join(",", _portalEntities.Keys.Select(k => $"0x{k:X4}"))}");
            return found;
        }

        private bool IsCheckpoint(ushort entityId, out ZoneCheckpoint checkpoint)
        {
            return _checkpointEntities.TryGetValue(entityId, out checkpoint);
        }

        private void HandlePortalActivation(RRConnection conn, ushort componentId, ushort targetEntityId, byte responseId, byte sessionId, ZonePortal portal)
        {
            Debug.LogError($"[PORTAL] Activating portal to {portal.TargetZone} @ {portal.SpawnPoint}");
            conn.SessionID = sessionId;
            conn.PendingPortalTransition = true;
            conn.PendingPortalTransitionReflected = false;
            conn.PendingPortalTransitionComponentId = componentId;
            conn.PendingPortalTransitionTargetEntityId = targetEntityId;
            conn.PendingPortalTransitionResponseId = responseId;
            conn.PendingPortalTransitionSessionId = sessionId;
            conn.PendingPortalTransitionTargetZone = portal.TargetZone ?? "";
            conn.PendingPortalTransitionSpawnPoint = portal.SpawnPoint ?? "";
            conn.PortalClientEntityEpochClosing = true;
            conn.PendingPortalTransitionResponseWriterSerial = 0;
            int purgedClientEntityMessages = conn.MessageQueue.Count;
            conn.MessageQueue.Clear();
            Debug.LogError($"[PORTAL-EPOCH] conn={conn.ConnId} component={componentId} purged={purgedClientEntityMessages} state=closing sourceFunction=ZoneClient::EnterLoading@0x005FC510->ClientEntityManager::~ClientEntityManager");
            QueuePortalActivationResponse(conn, componentId, targetEntityId, responseId, sessionId);

            if (portal.Name == "TownPortal")
            {
                SendDespawnEntity(conn, targetEntityId);
                _portalEntities.Remove(targetEntityId);
                ReleasePortalEntityId(targetEntityId);
                if (portal.TargetZone.Equals(conn.TownPortalZoneName, StringComparison.OrdinalIgnoreCase))
                {
                    conn.HasSavedTownPortal = false;
                    ClearTownPortalFromDB(conn);
                    Debug.LogError($"[PORTAL] Return portal used, cleared saved state");
                }
                Debug.LogError($"[PORTAL] Despawned town portal 0x{targetEntityId:X4}");
            }

            if (!string.IsNullOrEmpty(portal.SpawnPoint))
            {
                var waypoints = AuthoredGameplayCatalog.GetWaypointsForZone(portal.TargetZone);
                var waypoint = waypoints.FirstOrDefault(waypointData => waypointData.name.Equals(portal.SpawnPoint, StringComparison.OrdinalIgnoreCase));
                if (waypoint != null)
                {
                    conn.PendingSpawnFixedX = waypoint.PosFixedX;
                    conn.PendingSpawnFixedY = waypoint.PosFixedY;
                    conn.PendingSpawnFixedZ = waypoint.PosFixedZ;
                    Debug.LogError($"[PORTAL] Using waypoint '{portal.SpawnPoint}' fixed=({waypoint.PosFixedX}, {waypoint.PosFixedY}, {waypoint.PosFixedZ})");
                }
                else
                {
                    Debug.LogError($"[PORTAL]  Waypoint '{portal.SpawnPoint}' not found for zone '{portal.TargetZone}'");
                }
            }

            conn.ZonePortalSource = conn.CurrentZoneName;
            Debug.LogError($"[PORTAL] Transition scheduled after owner activation response target={portal.TargetZone} spawn={portal.SpawnPoint}");
        }

        private void SendMonsterLiveSnapshotToClient(RRConnection conn, Monster monster)
        {
            if (conn == null || monster == null || !conn.IsConnected || !conn.IsSpawned)
                return;
            if (!_monsterSpawnSentByConn.TryGetValue(conn.ConnId, out HashSet<uint> sentMonsters))
            {
                sentMonsters = new HashSet<uint>();
                _monsterSpawnSentByConn[conn.ConnId] = sentMonsters;
            }
            if (sentMonsters.Contains(monster.EntityId))
                return;

            if (!EnsureEncounterObject(conn, monster, true))
                return;
            if (!ResolveEntitySynchInfoForComponent(conn, (ushort)monster.BehaviorId, 0x04, EntitySynchInfoContext.EntityInitPrimer, monster.EntityId, "MON-LIVE-SNAPSHOT", out EntitySynchInfoDecision decision)
                || (decision.Flags & 0x02) == 0)
                decision = ResolveMonsterRuntimeHPDecision(monster, "MON-LIVE-SNAPSHOT", decision.Reason);

            MonsterLiveSnapshot snapshot = CaptureMonsterLiveSnapshot(conn, monster);
            ushort summonOwnerId = ResolveCombatSummonOwnerForViewer(conn, monster);
            if (monster.Summoner != null && summonOwnerId == 0)
                return;
            byte[] packet = CombatPackets.BuildMonsterLiveSnapshotPacket(
                monster,
                monster.BehaviorId,
                monster.SkillsId,
                monster.ManipulatorsId,
                monster.ModifiersId,
                snapshot,
                decision.ToResolved(monster.EntityId, monster.BehaviorId, 0x04, "MON-LIVE-SNAPSHOT"), summonOwnerId);
            if (!QueueClientEntityStream(conn, packet))
                return;

            sentMonsters.Add(monster.EntityId);
            if (!_monsterOwnerConnId.ContainsKey(monster.EntityId))
                _monsterOwnerConnId[monster.EntityId] = conn.ConnId;
            if (conn.Avatar != null)
                CombatRuntime.Instance.CommitMonsterClientVisibleSnapshotFixed(
                    monster,
                    (uint)conn.Avatar.Id,
                    snapshot.FixedX,
                    snapshot.FixedY,
                    snapshot.FixedZ,
                    snapshot.TargetFixedX,
                    snapshot.TargetFixedY,
                    snapshot.HeadingFixed,
                    snapshot.Moving);
            CombatRuntime.Instance.RecordMonsterOutboundHP(monster, decision.HPWire, "MON-LIVE-SNAPSHOT");
            Debug.LogError($"[MON-LIVE-SNAPSHOT] entity={monster.EntityId} conn={conn.ConnId} action={snapshot.ActionKind} pending={snapshot.PendingActionKind} spawn={snapshot.SpawnPhase}/{snapshot.SpawnCounter} generation={snapshot.ActionGeneration} fixed=({snapshot.FixedX},{snapshot.FixedY},{snapshot.FixedZ}) target=({snapshot.TargetFixedX},{snapshot.TargetFixedY}) moving={snapshot.Moving}");
        }

        private MonsterLiveSnapshot CaptureMonsterLiveSnapshot(RRConnection viewer, Monster monster)
        {
            uint viewerEntityId = viewer?.Avatar != null ? (uint)viewer.Avatar.Id : 0u;
            CombatRuntime.Instance.TryPeekMonsterClientVisibleMoverFixed(
                monster,
                viewerEntityId,
                out int fixedX,
                out int fixedY,
                out int fixedZ,
                out int targetFixedX,
                out int targetFixedY,
                out int headingFixed,
                out bool moving);

            WanderStateSnapshot wander = default;
            bool hasWander = !monster.AggroTriggered && WanderSimulator.Instance.TryGetSnapshot(monster.EntityId, out wander);
            RRConnection primaryOwner = FindConnectionByAvatarEntityId(monster.TargetId);
            ushort primaryTarget = ResolveRemoteAvatarEntityId(viewer, primaryOwner, monster.TargetId);
            RRConnection secondaryOwner = FindConnectionByAvatarEntityId(monster.AlertSourceEntityId);
            ushort secondaryTarget = ResolveRemoteAvatarEntityId(viewer, secondaryOwner, monster.AlertSourceEntityId);
            MonsterLiveActionKind actionKind = hasWander
                ? MonsterLiveActionKind.Wander
                : primaryTarget != 0 ? MonsterLiveActionKind.Follow : MonsterLiveActionKind.None;
            MonsterLiveActionKind pendingActionKind = MonsterLiveActionKind.None;

            DungeonRunners.Combat.Behavior.StateMachineSnapshot topStateMachine = null;
            ushort topCurrent = 5;
            ushort followCurrent = moving ? (ushort)2 : (ushort)5;
            DungeonRunners.Combat.Behavior.StateMachineSnapshot followStateMachine = null;
            int combatTimer = 0;
            byte actionGeneration = 0;
            byte spawnPhase = 100;
            byte spawnCounter = 0;
            if (monster.Behavior != null)
            {
                DungeonRunners.Combat.Behavior.MonsterBehavior2Snapshot behavior = monster.Behavior.GetSnapshot();
                topCurrent = (ushort)Math.Max(0, Math.Min(ushort.MaxValue, behavior.TopState));
                combatTimer = behavior.CombatTimer;
                actionGeneration = behavior.ActionGeneration;
                spawnPhase = behavior.SpawnPhase;
                spawnCounter = behavior.SpawnCounter;
                if (behavior.CurrentAction == DungeonRunners.Combat.Behavior.MonsterBehavior2.ActionSlot.Spawn)
                    actionKind = MonsterLiveActionKind.Spawn;
                else if (behavior.CurrentAction == DungeonRunners.Combat.Behavior.MonsterBehavior2.ActionSlot.Wander && hasWander)
                    actionKind = MonsterLiveActionKind.Wander;
                else if (behavior.CurrentAction == DungeonRunners.Combat.Behavior.MonsterBehavior2.ActionSlot.Follow)
                    actionKind = MonsterLiveActionKind.Follow;
                else if (behavior.CurrentAction == DungeonRunners.Combat.Behavior.MonsterBehavior2.ActionSlot.MoveTo)
                {
                    actionKind = MonsterLiveActionKind.MoveTo;
                    targetFixedX = monster.ReturnMoveTargetFixedX;
                    targetFixedY = monster.ReturnMoveTargetFixedY;
                }
                else if (behavior.CurrentAction == DungeonRunners.Combat.Behavior.MonsterBehavior2.ActionSlot.None)
                    actionKind = MonsterLiveActionKind.None;
                if (behavior.PendingAction == DungeonRunners.Combat.Behavior.MonsterBehavior2.ActionSlot.Wander)
                    pendingActionKind = MonsterLiveActionKind.Wander;
                else if (behavior.PendingAction == DungeonRunners.Combat.Behavior.MonsterBehavior2.ActionSlot.MoveTo)
                    pendingActionKind = MonsterLiveActionKind.MoveTo;
                topStateMachine = behavior.StateMachine;
                if (behavior.FollowActive)
                {
                    if (behavior.FollowState >= 0)
                        followCurrent = (ushort)Math.Min(ushort.MaxValue, behavior.FollowState);
                    followStateMachine = behavior.FollowStateMachine;
                }
            }

            return new MonsterLiveSnapshot(
                fixedX,
                fixedY,
                fixedZ,
                headingFixed,
                targetFixedX,
                targetFixedY,
                moving,
                monster.WorldEntityAnimationState,
                monster.WorldEntityAnimationId,
                monster.WorldEntityAnimationPlayTime,
                monster.WorldEntityAnimationSpeed,
                actionKind,
                pendingActionKind,
                spawnPhase,
                spawnCounter,
                wander,
                primaryTarget,
                primaryTarget,
                secondaryTarget,
                (ushort)Math.Max(0, Math.Min(ushort.MaxValue, combatTimer)),
                actionGeneration,
                0,
                topCurrent,
                topCurrent,
                topStateMachine,
                0xFFFF,
                followCurrent,
                followCurrent,
                followStateMachine);
        }

        private void QueuePortalActivationResponse(RRConnection conn, ushort componentId, ushort targetEntityId, byte responseId, byte sessionId)
        {
            var portalActivationMessage = new LEWriter();
            portalActivationMessage.WriteByte(0x35);
            portalActivationMessage.WriteUInt16(componentId);
            portalActivationMessage.WriteByte(0x01);
            portalActivationMessage.WriteByte(responseId);
            portalActivationMessage.WriteByte(0x06);
            portalActivationMessage.WriteByte(sessionId);
            portalActivationMessage.WriteUInt16(targetEntityId);

            WritePlayerEntitySynch(conn, portalActivationMessage);
            byte[] response = portalActivationMessage.ToArray();
            conn.MessageQueue.Enqueue(response);

            Debug.LogError($"[PORTAL]  Queued portal activation response");
        }

        private void ProcessReflectedPortalTransitions()
        {
            foreach (RRConnection conn in GetConnectionInsertionOrderSnapshot())
            {
                if (!conn.IsConnected
                    || !conn.PendingPortalTransition
                    || !conn.PendingPortalTransitionReflected
                    || conn.PendingPortalTransitionResponseWriterSerial == 0
                    || unchecked((int)(_clientEntityUpdateSerial - conn.PendingPortalTransitionResponseWriterSerial)) <= 0)
                    continue;

                string targetZone = conn.PendingPortalTransitionTargetZone;
                string spawnPoint = conn.PendingPortalTransitionSpawnPoint;
                Debug.LogError($"[PORTAL] Transition executing after owner activation response target={targetZone} spawn={spawnPoint} responseSerial={conn.PendingPortalTransitionResponseWriterSerial} currentSerial={_clientEntityUpdateSerial}");
                ChangeZone(conn, targetZone, spawnPoint, queueEntityRemoval: true);
            }
        }

        private static void ClearPendingPortalTransition(RRConnection conn)
        {
            if (conn == null)
                return;
            conn.PendingPortalTransition = false;
            conn.PendingPortalTransitionReflected = false;
            conn.PendingPortalTransitionComponentId = 0;
            conn.PendingPortalTransitionTargetEntityId = 0;
            conn.PendingPortalTransitionResponseId = 0;
            conn.PendingPortalTransitionSessionId = 0;
            conn.PendingPortalTransitionTargetZone = "";
            conn.PendingPortalTransitionSpawnPoint = "";
            conn.PortalClientEntityEpochClosing = false;
            conn.PendingPortalTransitionResponseWriterSerial = 0;
        }

        private static void MarkPendingPortalTransitionResponseFlushed(RRConnection conn, IReadOnlyList<byte[]> messages, uint packetWriterTick, uint clientEntityUpdateSerial)
        {
            if (conn == null || !conn.PendingPortalTransition || conn.PendingPortalTransitionReflected || messages == null)
                return;
            ushort componentId = conn.PendingPortalTransitionComponentId;
            ushort targetEntityId = conn.PendingPortalTransitionTargetEntityId;
            byte responseId = conn.PendingPortalTransitionResponseId;
            byte sessionId = conn.PendingPortalTransitionSessionId;
            foreach (byte[] message in messages)
            {
                if (message == null || message.Length < 9)
                    continue;
                ushort candidateComponentId = (ushort)(message[1] | (message[2] << 8));
                ushort candidateTargetEntityId = (ushort)(message[7] | (message[8] << 8));
                if (message[0] != 0x35
                    || candidateComponentId != componentId
                    || message[3] != 0x01
                    || message[4] != responseId
                    || message[5] != 0x06
                    || message[6] != sessionId
                    || candidateTargetEntityId != targetEntityId)
                    continue;
                conn.PendingPortalTransitionReflected = true;
                conn.PendingPortalTransitionResponseWriterSerial = clientEntityUpdateSerial;
                Debug.LogError($"[PORTAL-FLUSH] conn={conn.ConnId} component={componentId} target={targetEntityId} response={responseId} session={sessionId} writerTick={packetWriterTick} serial={clientEntityUpdateSerial} state=sent");
                return;
            }
        }

        private void HandleCheckpointActivation(RRConnection conn, ushort componentId, ushort targetEntityId, byte responseId, byte sessionId, ZoneCheckpoint checkpoint, bool includeActivateResponse = true)
        {
            Debug.LogError($"[CHECKPOINT] Player activated checkpoint: {checkpoint.CheckpointGCType}");
            string cpClass = checkpoint.CheckpointGCType;
            var knownCp = AuthoredGameplayCatalog.Checkpoints.FirstOrDefault(c =>
                c.id.Equals(cpClass, StringComparison.OrdinalIgnoreCase));
            bool unlocked = false;
            if (knownCp != null)
            {
                string connId = conn.ConnId.ToString();
                var playerState = QuestManager.Instance.GetPlayerState(connId);
                if (playerState != null && QuestManager.Instance.UnlockCheckpoint(connId, cpClass))
                {
                    unlocked = true;
                    if (!SavePlayerQuests(conn))
                    {
                        playerState.UnlockedCheckpoints.Remove(cpClass);
                        Debug.LogError($"[CHECKPOINT] unlock={cpClass} state=rollback reason=save-failed");
                        return;
                    }
                    Debug.LogError($"[CHECKPOINT] Unlocked obelisk: {cpClass} zone='{knownCp.zone}' durable=True");
                }
            }
            try
            {
                conn.SessionID = sessionId;
                if (includeActivateResponse)
                {
                    var checkpointActivationMessage = new LEWriter();
                    checkpointActivationMessage.WriteByte(0x35);
                    checkpointActivationMessage.WriteUInt16(componentId);
                    checkpointActivationMessage.WriteByte(0x01);
                    checkpointActivationMessage.WriteByte(responseId);
                    checkpointActivationMessage.WriteByte(0x06);
                    checkpointActivationMessage.WriteByte(sessionId);
                    checkpointActivationMessage.WriteUInt16(targetEntityId);
                    if (!WritePlayerEntitySynch(conn, checkpointActivationMessage))
                        throw new InvalidOperationException("Checkpoint activation synchronization failed");
                    conn.MessageQueue.Enqueue(checkpointActivationMessage.ToArray());
                }
                Debug.LogError($"[CHECKPOINT] Queued activation response unlocked={unlocked}");
            }
            catch (Exception ex)
            {
                if (unlocked)
                    QuarantineCommittedPersistenceSyncFailure(conn, "checkpoint-materialization", ex);
                else
                    Debug.LogError($"[CHECKPOINT] state=failed phase=materialization errorType={ex.GetType().Name} message='{ex.Message}'");
            }
        }

        private bool IsChest(ushort entityId, out ChestSpawnData chest)
        {
            return _chestEntities.TryGetValue(entityId, out chest);
        }

        private void HandleChestActivation(RRConnection conn, ushort componentId,
            ushort targetEntityId, byte responseId, byte sessionId, ChestSpawnData chest)
        {
            Debug.LogError($"[CHEST] ");
            Debug.LogError($"[CHEST] Opening: {chest.Label} gc={chest.GCType}");
            conn.SessionID = sessionId;

            var chestActivationMessage = new LEWriter();
            chestActivationMessage.WriteByte(0x35);
            chestActivationMessage.WriteUInt16(componentId);
            chestActivationMessage.WriteByte(0x01);
            chestActivationMessage.WriteByte(responseId);
            chestActivationMessage.WriteByte(0x06);
            chestActivationMessage.WriteByte(sessionId);
            chestActivationMessage.WriteUInt16(targetEntityId);
            WritePlayerEntitySynch(conn, chestActivationMessage);
            conn.MessageQueue.Enqueue(chestActivationMessage.ToArray());

            var nonCombatInteractiveMessage = new LEWriter();
            nonCombatInteractiveMessage.WriteByte(0x03);
            nonCombatInteractiveMessage.WriteUInt16(targetEntityId);
            nonCombatInteractiveMessage.WriteByte(0x0A);
            nonCombatInteractiveMessage.WriteUInt32(0x00000001);
            WriteNonCombatInteractiveEntitySynchInfo(nonCombatInteractiveMessage, chest.GCType);
            conn.MessageQueue.Enqueue(nonCombatInteractiveMessage.ToArray());

            PlayerState playerState = GetPlayerState(conn.ConnId.ToString());
            int pLevel = playerState?.Level ?? 1;

            var drops = new List<LootDrop>();
            foreach (var (generator, count, slot) in chest.GetChestGenerators())
            {
                var slotDrops = GCObjectGeneratorTable.Instance.GenerateChestLoot(generator, count, pLevel, !IsPlayerFree(conn.LoginName), conn.AvatarGcType);
                drops.AddRange(slotDrops);
                Debug.LogError($"[CHEST] slot={slot} generator={generator} count={count} drops={slotDrops.Count} sourceFunction=NonCombatInteractiveDesc-ItemGenerator1-5");
            }

            UpgradePotionsForMembers(drops, conn);

            foreach (var drop in drops)
            {
                if (drop.IsGold)
                {
                    Debug.LogError($"[CHEST] +{drop.GoldAmount} gold");
                    if (drop.GoldAmount > 0)
                    {
                        bool goldCommitted = GiveGold(conn, (uint)drop.GoldAmount, $"nci-chest:{chest.GCType}");
                        if (QuestLootTracking)
                            Debug.LogError($"[QUEST-LOOT-TRACK] source=nci-chest-gold characterId={GetCharSqlId(conn)} entity={targetEntityId} gc='{chest.GCType}' quantity={drop.GoldAmount} dbCommit={goldCommitted} instance='{RoomRuntime.NormalizeInstanceKey(GetInstanceZoneKey(conn))}'");
                    }
                }
                else
                {
                    if (string.IsNullOrEmpty(drop.GCType)) { Debug.LogError("[CHEST]  Skipping null GCType"); continue; }
                    string _detectedChest = ResolveAuthoredItemClass(drop.GCType);

                    var item = new GCObject
                    {
                        GCClass = drop.GCType,
                        DFCClass = _detectedChest,
                        PresetScaleMod = drop.ScaleMod,
                        StoredRarity = (int)drop.Rarity,
                        StoredLevel = drop.ItemLevel,
                        HasGeneratedItemState = drop.HasGeneratedItemState,
                        RolledRequiresMembership = drop.RolledRequiresMembership,
                        GeneratedRequiresMembership = drop.RequiresMembership,
                        GeneratedItemModifiers = new List<string>(drop.ItemModifiers)
                    };

                    string itemOwner = $"nci-chest:{chest.Label}:{drop.GCType}";
                    var chestPlacement = ResolveItemDropPlacement(
                        conn,
                        conn.CurrentZoneName,
                        conn.InstanceId,
                        chest.PosFixedX,
                        chest.PosFixedY,
                        chest.PosFixedZ,
                        chest.HeadingFixed,
                        itemOwner);
                    if (!chestPlacement.Success)
                    {
                        Debug.LogError($"[CHEST] blocked item={drop.GCType} reason=missing-pathmap zone='{conn.CurrentZoneName}' instance={conn.InstanceId:X8}");
                        continue;
                    }

                    int itemHeadingFixed = ConsumeItemAddToWorldHeading(itemOwner);
                    ushort lootId = GetNextLootEntityId();
                    TrackDroppedItem(lootId, item, conn, 1, chestPlacement.FixedX, chestPlacement.FixedY, chestPlacement.FixedZ, pLevel, itemHeadingFixed);

                    SendDroppedItemSpawnPacket(conn, lootId, _droppedItems[lootId]);
                    Debug.LogError($"[CHEST]  {drop.Label} ({drop.Rarity}) at fixed ({chestPlacement.FixedX},{chestPlacement.FixedY},{chestPlacement.FixedZ})");
                }
            }

            _chestEntities.Remove(targetEntityId);

            Debug.LogError($"[CHEST] {chest.Label}: {drops.Count} drops, chest opened");
            Debug.LogError($"[CHEST] ");
        }

        private void HandleCheckpointUse(RRConnection conn, ushort componentId, byte responseId, byte sessionID, LEReader reader)
        {
            Debug.LogError($"[CHECKPOINT-USE] ");
            Debug.LogError($"[CHECKPOINT-USE] Player selected checkpoint destination!");

            try
            {
                string checkpointGcType = reader.ReadCString();
                Debug.LogError($"[CHECKPOINT-USE] Target checkpoint gcType: '{checkpointGcType}'");

                var checkpointData = AuthoredGameplayCatalog.GetCheckpointByGcType(checkpointGcType);

                if (checkpointData == null)
                {
                    Debug.LogError($"[CHECKPOINT-USE]  Checkpoint '{checkpointGcType}' not found in database!");
                    return;
                }

                string checkpointKey = checkpointData.gcType ?? checkpointGcType;
                if (checkpointKey.EndsWith("Entity", StringComparison.OrdinalIgnoreCase))
                    checkpointKey = checkpointKey.Substring(0, checkpointKey.Length - 6);
                PlayerQuestState questState = QuestManager.Instance.GetPlayerState(conn.ConnId.ToString());
                if (questState == null || !questState.UnlockedCheckpoints.Any(checkpoint =>
                    string.Equals(checkpoint, checkpointKey, StringComparison.OrdinalIgnoreCase)))
                {
                    Debug.LogError($"[CHECKPOINT-USE] checkpoint='{checkpointGcType}' state=rejected reason=locked");
                    return;
                }

                Debug.LogError($"[CHECKPOINT-USE] Found checkpoint: zone='{checkpointData.zone}', fixed=({checkpointData.PosFixedX}, {checkpointData.PosFixedY}, {checkpointData.PosFixedZ})");

                Action sendCheckpointUseResponse = () =>
                {
                    var checkpointUseMessage = new LEWriter();
                    checkpointUseMessage.WriteByte(0x35);
                    checkpointUseMessage.WriteUInt16(componentId);
                    checkpointUseMessage.WriteByte(0x01);
                    checkpointUseMessage.WriteByte(responseId);
                    checkpointUseMessage.WriteByte(0x52);
                    checkpointUseMessage.WriteByte(sessionID);
                    checkpointUseMessage.WriteByte(0x00);
                    if (!WritePlayerEntitySynch(conn, checkpointUseMessage))
                        throw new InvalidOperationException("checkpoint-use entity synch failed after durable transition commit");
                    SendToClient(conn, WrapClientEntityBody(checkpointUseMessage));
                };

                Debug.LogError($"[CHECKPOINT-USE]  Staged use response, teleporting to {checkpointData.zone}");

                ChangeZoneToPosition(
                    conn,
                    checkpointData.zone,
                    checkpointData.PosFixedX,
                    checkpointData.PosFixedY,
                    checkpointData.PosFixedZ,
                    sendCheckpointUseResponse);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CHECKPOINT-USE] state=failed message='{ex.Message}' stack='{ex.StackTrace}'");
            }
        }

        private static bool ShouldUseFullHPZoneBaseline(string fromZone, string toZone)
        {
            if (!DungeonMazeSpawner.IsProceduralZone(toZone))
                return false;
            if (DungeonMazeSpawner.IsProceduralZone(fromZone))
                return false;
            return true;
        }

        private static string ResolveZoneGcType(Zone zone)
        {
            if (zone != null && !string.IsNullOrEmpty(zone.gcType))
                return zone.gcType;
            string zoneName = zone?.name ?? "";
            if (zoneName.IndexOf("tutorial", StringComparison.OrdinalIgnoreCase) >= 0)
                return "world.tutorial";
            if (zoneName.IndexOf("town", StringComparison.OrdinalIgnoreCase) >= 0)
                return "world.town";
            if (!string.IsNullOrEmpty(zoneName))
                return "world." + zoneName;
            return "world.tutorial";
        }

        private bool ChangeZoneToPosition(
            RRConnection conn,
            string targetZone,
            int spawnFixedX,
            int spawnFixedY,
            int spawnFixedZ,
            Action afterPersistBeforeTransition = null)
        {
            bool durableCommitted = false;
            try
            {
                var zone = _zoneOrder.FirstOrDefault(z => z.name.Equals(targetZone, StringComparison.OrdinalIgnoreCase));
                if (zone == null)
                {
                    Debug.LogError($"[ZONE] target='{targetZone}' state=notFound");
                    return false;
                }

                PathMap targetPathMap = PathMapCatalog.Instance.GetPathMap(zone.name);
                if (targetPathMap == null || !targetPathMap.IsWalkableFixed(spawnFixedX, spawnFixedY))
                {
                    Debug.LogError($"[ZONE] state=blocked phase=plan target={zone.name} reason={(targetPathMap == null ? "missing-pathmap" : "position-not-walkable")}");
                    return false;
                }
                if (!TrySaveZoneTransitionSnapshot(conn, zone, spawnFixedX, spawnFixedY, spawnFixedZ, false, "zone-position"))
                {
                    Debug.LogError($"[ZONE] state=failed phase=persist target={zone.name}");
                    return false;
                }
                durableCommitted = true;
                afterPersistBeforeTransition?.Invoke();

            if (conn.IsSpawned)
                BroadcastEntityRemove(conn, conn.CurrentZoneGcType);

            conn.IsSpawned = false;
            conn.HasLivePlayerPosition = false;
            conn.LivePlayerMovingThisFrame = false;
            ResetReflectedAvatarPosition(conn);

            Debug.LogError($"[ZONE] ");
            Debug.LogError($"[ZONE] CHECKPOINT TELEPORT: {targetZone} @ fixed ({spawnFixedX},{spawnFixedY},{spawnFixedZ})");
            Debug.LogError($"[ZONE] ");
            BlingGnomeRuntime.Instance.SetServer(this);
            BlingGnomeRuntime.Instance.CleanupForZoneTransition(conn.ConnId);
                CancelPendingCombatForConnection(conn, preserveAttributeModifiers: true);
                ClearPvpConnectionRuntime(conn, "zone-position-transition", true);
                Combat.WeaponUseRuntime.Instance.ClearConnection(conn.ConnId.ToString());
                if (conn.Avatar != null && conn.Avatar.Id > 0)
                {
                    CombatRuntime.Instance.RemoveExternalPlayerAttributeModifiers((uint)conn.Avatar.Id, "zone-position-transition");
                    CombatRuntime.Instance.UnregisterPlayer((uint)conn.Avatar.Id, preserveAttributeModifiers: true);
                }

            conn.TickUpdatesActive = false;

            conn.AllowFlush = false;
            ClearRemoteActionRelayState(conn);
            ClearPendingPortalActivation(conn);
            ClearPendingPortalTransition(conn);
            conn.MessageQueue.Clear();
            ClearPendingCheckpointActivation(conn);

            conn.FullHPBaselineOnNextSpawn = ShouldUseFullHPZoneBaseline(conn.CurrentZoneName, zone.name);
            if (conn.FullHPBaselineOnNextSpawn)
                Debug.LogError($"[ZONE-HP-BASELINE] full HP baseline queued: {conn.CurrentZoneName} -> {zone.name}");
            conn.CurrentZoneId = zone.id;
            conn.CurrentZoneName = zone.name;
            _monsterSpawnSentByConn.Remove(conn.ConnId);
            _encounterObjectSentByConn.Remove(conn.ConnId);
            GroupDirectory.Instance.UpdateMemberZone(conn.ConnId, zone.name);
            SendGroupMemberZoneState(conn);
            conn.CurrentZoneGcType = ResolveZoneGcType(zone);

            Debug.LogError($"[ZONE] CurrentZoneGcType set to: {conn.CurrentZoneGcType}");
            Debug.LogError($"[ZONE] CurrentZoneName set to: {conn.CurrentZoneName}");
            AssignInstanceId(conn);

            if (_selectedCharacter.TryGetValue(conn.LoginName, out var zoneChangeChar1))
            {
                SocialRuntime.Instance.NotifyFriendsZoneChange(conn.LoginName, zoneChangeChar1.Name, zone.name, SendSocialViaAuth);
                SocialRuntime.Instance.PushWhoListToAll(SendSocialViaAuth);
            }

            conn.PendingSpawnFixedX = spawnFixedX;
            conn.PendingSpawnFixedY = spawnFixedY;
            conn.PendingSpawnFixedZ = spawnFixedZ;

            ClearConnZoneEntities(conn);
            _encounterObjectSentByConn.Remove(conn.ConnId);
            _monsterSpawnSentByConn.Remove(conn.ConnId);

            var disconnectWriter = new LEWriter();
            disconnectWriter.WriteByte(0x0D);
            disconnectWriter.WriteByte(0x02);
            disconnectWriter.WriteCString("zoneleave");
            SendCompressedA(conn, 0x01, 0x0F, disconnectWriter.ToArray());
            Debug.LogError("[ZONE]  Sent DISCONNECT");

            var writer = new LEWriter();
            writer.WriteByte(0x0D);
            writer.WriteByte(0x00);
            writer.WriteCString(zone.name);
            uint zoneSeed = ResolveZoneConnectSeed(conn, zone.name);
            writer.WriteUInt32(zoneSeed);
            WriteDungeonQuestRooms(writer, conn, zone.name);
            writer.WriteUInt32(0x00);

            SendCompressedA(conn, 0x01, 0x0F, writer.ToArray());
            Debug.LogError($"[ZONE]  Sent CONNECT seed=0x{zoneSeed:X8} - waiting for client 0x06 response");
                return true;
            }
            catch (Exception ex)
            {
                if (durableCommitted)
                {
                    QuarantineCommittedPersistenceSyncFailure(conn, "zone-position-runtime", ex);
                    return true;
                }
                Debug.LogError($"[ZONE] state=failed phase=plan-exception target={targetZone ?? ""} errorType={ex.GetType().Name} message='{ex.Message}'");
                return false;
            }
        }




        private void SendActivationResponse(RRConnection conn, ushort componentId, ushort targetEntityId, byte responseId, byte sessionId)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x35);
            writer.WriteUInt16(componentId);
            writer.WriteByte(0x2E);

            writer.WriteByte(0x02);
            writer.WriteByte(0x06);
            writer.WriteByte(responseId);
            writer.WriteByte(sessionId);

            writer.WriteUInt16(targetEntityId);

            writer.WriteByte(0x06);

            SendCompressedA(conn, 0x01, 0x0f, writer.ToArray());
            Debug.LogError($"[ACTIVATE] Sent activation response");
        }

        public void ChatChangeZone(RRConnection conn, string zoneName)
        {
            Debug.LogError($"[CHAT-ZONE] @z command: {zoneName}");
            ChangeZone(conn, zoneName, "");
        }

        public bool SpawnTownPortalWithRemoval(RRConnection conn, string targetZone,
      ushort componentId, uint itemSlotId, PlayerState playerState,
      GCObject item, string gcLower, byte itemX, byte itemY, int remainingCount)
        {
            if (!TryAllocatePortalEntityId(out ushort portalEntityId))
            {
                Debug.LogError($"[TOWN-PORTAL] state=blocked reason=entity-id-exhausted range=0x{PORTAL_ID_MIN:X4}-0x{PORTAL_ID_MAX:X4}");
                return false;
            }

            int headingDegrees = ((conn.PlayerHeadingFixed + 0x80) >> 8) % 360;
            if (headingDegrees < 0) headingDegrees += 360;
            int fx = conn.PlayerPosFixedX + UnitMover.ZRotateSinFixed(headingDegrees) * 10;
            int fy = conn.PlayerPosFixedY + UnitMover.ZRotateCosFixed(headingDegrees) * 10;
            int fz = conn.PlayerPosFixedZ;
            Debug.LogError($"[TOWN-PORTAL] Player fixed=({conn.PlayerPosFixedX},{conn.PlayerPosFixedY}) headingFixed={conn.PlayerHeadingFixed} portalFixed=({fx},{fy})");

            _portalEntities[portalEntityId] = new ZonePortal
            {
                Id = portalEntityId,
                GCType = "items.townportal.TownPortalBlue",
                Name = "TownPortal",
                PosFixedX = fx,
                PosFixedY = fy,
                PosFixedZ = fz,
                TargetZone = targetZone,
                SpawnPoint = "",
                Width = 3,
                Height = 3,
                Color = 0x0000FFFF
            };

            conn.HasSavedTownPortal = true;
            conn.TownPortalZoneName = conn.CurrentZoneName;
            conn.TownPortalZoneId = conn.CurrentZoneId;
            conn.TownPortalTargetZone = targetZone;
            conn.TownPortalPosFixedX = fx;
            conn.TownPortalPosFixedY = fy;
            conn.TownPortalPosFixedZ = fz;

            if (!_selectedCharacter.ContainsKey(conn.LoginName))
            {
                _portalEntities.Remove(portalEntityId);
                ReleasePortalEntityId(portalEntityId);
                ClearTownPortalCreationState(conn);
                return false;
            }
            if (_selectedCharacter.ContainsKey(conn.LoginName))
            {
                var saveChar = GetActiveCharacter(conn)?.DeepClone();
                if (saveChar == null)
                {
                    _portalEntities.Remove(portalEntityId);
                    ReleasePortalEntityId(portalEntityId);
                    ClearTownPortalCreationState(conn);
                    return false;
                }
                if (saveChar != null)
                {
                    saveChar.tpZone = conn.TownPortalZoneName;
                    saveChar.tpZoneId = unchecked((int)conn.TownPortalZoneId);
                    saveChar.tpTargetZone = conn.TownPortalTargetZone;
                    saveChar.tpPosFixedX = conn.TownPortalPosFixedX;
                    saveChar.tpPosFixedY = conn.TownPortalPosFixedY;
                    saveChar.tpPosFixedZ = conn.TownPortalPosFixedZ;
                    if (!TrySaveCharacterForConn(conn, saveChar, "town-portal-create"))
                    {
                        _portalEntities.Remove(portalEntityId);
                        ReleasePortalEntityId(portalEntityId);
                        ClearTownPortalCreationState(conn);
                        return false;
                    }
                    Debug.LogError("[TOWN-PORTAL] Saved to DB");
                }
            }

            try
            {
            var removeWriter = new LEWriter();
            removeWriter.WriteByte(0x07);
            removeWriter.WriteByte(0x35);
            removeWriter.WriteUInt16(componentId);
            removeWriter.WriteByte(0x1F);
            removeWriter.WriteUInt32(itemSlotId);
            if (!WritePlayerEntitySynch(conn, removeWriter))
                throw new InvalidOperationException("Town portal item removal synchronization failed");

            if (remainingCount > 0)
            {
                removeWriter.WriteByte(0x35);
                removeWriter.WriteUInt16(componentId);
                removeWriter.WriteByte(0x1E);
                removeWriter.WriteByte(0x0B);
                int remainingLevel = item.StoredLevel >= 0 ? item.StoredLevel : Math.Max(1, item.GetItemRequiredLevel());
                item.WriteItemData(removeWriter, itemSlotId, itemX, itemY, (byte)Math.Min(remainingCount, byte.MaxValue), remainingLevel);
                if (!WritePlayerEntitySynch(conn, removeWriter))
                    throw new InvalidOperationException("Town portal stack synchronization failed");
            }

            removeWriter.WriteByte(0x06);
            SendToClient(conn, removeWriter.ToArray());

            var questManagerWriter = new LEWriter();
            questManagerWriter.WriteByte(0x07);
            questManagerWriter.WriteByte(0x35);
            questManagerWriter.WriteUInt16(conn.QuestManagerId);
            questManagerWriter.WriteByte(0x0A);
            questManagerWriter.WriteByte(0x01);
            questManagerWriter.WriteUInt32(conn.CurrentZoneId);
            questManagerWriter.WriteCString(conn.CurrentZoneName);
            questManagerWriter.WriteCString("");
            if (!WritePlayerEntitySynch(conn, questManagerWriter))
                throw new InvalidOperationException("Town portal quest-manager synchronization failed");
            questManagerWriter.WriteByte(0x06);
            SendToClient(conn, questManagerWriter.ToArray());

            var spawnWriter = new LEWriter();
            spawnWriter.WriteByte(0x07);
            spawnWriter.WriteByte(0x01);
            spawnWriter.WriteUInt16(portalEntityId);
            spawnWriter.WriteByte(0xFF);
            spawnWriter.WriteCString("items.townportal.TownPortalBlue");

            spawnWriter.WriteByte(0x02);
            spawnWriter.WriteUInt16(portalEntityId);
            spawnWriter.WriteUInt32(0x06);
            spawnWriter.WriteInt32(fx);
            spawnWriter.WriteInt32(fy);
            spawnWriter.WriteInt32(fz);
            spawnWriter.WriteInt32(0);
            spawnWriter.WriteByte(0x01);
            spawnWriter.WriteUInt16((ushort)(conn.Avatar?.Id ?? 0));

            spawnWriter.WriteCString(targetZone);
            spawnWriter.WriteCString("");
            spawnWriter.WriteByte(0x02);
            spawnWriter.WriteUInt32(0x00);
            spawnWriter.WriteUInt32(conn.CurrentZoneId);

            spawnWriter.WriteByte(0x06);

            Debug.LogError($"[TOWN-PORTAL] Spawning TownPortalBlue 0x{portalEntityId:X4} at fixed ({fx},{fy}) {targetZone} zoneGUID={conn.CurrentZoneId}");
            Debug.LogError($"[TOWN-PORTAL] Saved portal: zone={conn.TownPortalZoneName} posFixed=({fx},{fy})");
            SendCompressedA(conn, 0x01, 0x0f, spawnWriter.ToArray());

            var otherWriter = new LEWriter();
            otherWriter.WriteByte(0x07);
            otherWriter.WriteByte(0x01);
            otherWriter.WriteUInt16(portalEntityId);
            otherWriter.WriteByte(0xFF);
            otherWriter.WriteCString("items.townportal.TownPortalBlue");
            otherWriter.WriteByte(0x02);
            otherWriter.WriteUInt16(portalEntityId);
            otherWriter.WriteUInt32(0x04);
            otherWriter.WriteInt32(fx);
            otherWriter.WriteInt32(fy);
            otherWriter.WriteInt32(fz);
            otherWriter.WriteInt32(0);
            otherWriter.WriteByte(0x01);
            otherWriter.WriteUInt16((ushort)(conn.Avatar?.Id ?? 0));
            otherWriter.WriteCString(targetZone);
            otherWriter.WriteCString("");
            otherWriter.WriteByte(0x02);
            otherWriter.WriteUInt32(0x00);
            otherWriter.WriteUInt32(conn.CurrentZoneId);
            otherWriter.WriteByte(0x06);

            byte[] otherPortalPacket = otherWriter.ToArray();
            foreach (var other in GetConnectionInsertionOrderSnapshot())
            {
                if (other == conn) continue;
                if (!other.IsSpawned) continue;
                if (other.CurrentZoneGcType != conn.CurrentZoneGcType) continue;
                if (other.InstanceId != conn.InstanceId) continue;
                SendCompressedA(other, 0x01, 0x0f, otherPortalPacket);
            }
            Debug.LogError($"[TOWN-PORTAL] Broadcast portal to other players in zone");
            return true;
            }
            catch (Exception ex)
            {
                QuarantineCommittedPersistenceSyncFailure(conn, "town-portal-materialization", ex);
                return true;
            }
        }

        private static void ClearTownPortalCreationState(RRConnection conn)
        {
            if (conn == null)
                return;
            conn.HasSavedTownPortal = false;
            conn.TownPortalZoneName = "";
            conn.TownPortalZoneId = 0;
            conn.TownPortalTargetZone = "";
            conn.TownPortalPosFixedX = 0;
            conn.TownPortalPosFixedY = 0;
            conn.TownPortalPosFixedZ = 0;
        }

        private void SpawnReturnTownPortal(RRConnection conn)
        {
            if (!conn.HasSavedTownPortal) return;
            if (!conn.CurrentZoneName.Equals(conn.TownPortalZoneName, StringComparison.OrdinalIgnoreCase)) return;

            if (!TryAllocatePortalEntityId(out ushort portalEntityId))
            {
                Debug.LogError($"[TOWN-PORTAL] state=blocked reason=return-entity-id-exhausted range=0x{PORTAL_ID_MIN:X4}-0x{PORTAL_ID_MAX:X4}");
                return;
            }
            int fx = conn.TownPortalPosFixedX;
            int fy = conn.TownPortalPosFixedY;
            int fz = conn.TownPortalPosFixedZ;


            var spawnWriter = new LEWriter();
            spawnWriter.WriteByte(0x07);
            spawnWriter.WriteByte(0x01);
            spawnWriter.WriteUInt16(portalEntityId);
            spawnWriter.WriteByte(0xFF);
            spawnWriter.WriteCString("items.townportal.TownPortalBlue");
            spawnWriter.WriteByte(0x02);
            spawnWriter.WriteUInt16(portalEntityId);
            spawnWriter.WriteUInt32(0x04);
            spawnWriter.WriteInt32(fx);
            spawnWriter.WriteInt32(fy);
            spawnWriter.WriteInt32(fz);
            spawnWriter.WriteInt32(0);
            spawnWriter.WriteByte(0x01);
            spawnWriter.WriteUInt16((ushort)(conn.Avatar?.Id ?? 0));
            spawnWriter.WriteCString(conn.TownPortalTargetZone);
            spawnWriter.WriteCString("");
            spawnWriter.WriteByte(0x01);
            spawnWriter.WriteUInt32(0x00);
            spawnWriter.WriteUInt32(conn.TownPortalZoneId);
            spawnWriter.WriteByte(0x06);

            SendCompressedA(conn, 0x01, 0x0f, spawnWriter.ToArray());
            conn.HasSavedTownPortal = false;
            ClearTownPortalFromDB(conn);
            Debug.LogError($"[TOWN-PORTAL] Re-spawned return portal at fixed ({conn.TownPortalPosFixedX},{conn.TownPortalPosFixedY})");

            byte[] returnPortalPacket = spawnWriter.ToArray();
            foreach (var other in GetConnectionInsertionOrderSnapshot())
            {
                if (other == conn) continue;
                if (!other.IsSpawned) continue;
                if (other.CurrentZoneGcType != conn.CurrentZoneGcType) continue;
                if (other.InstanceId != conn.InstanceId) continue;
                SendCompressedA(other, 0x01, 0x0f, returnPortalPacket);
            }

            _pendingReturnTownPortalDespawns.Add((
                portalEntityId,
                conn.CurrentZoneGcType,
                conn.InstanceId,
                checked(_combatTick + RETURN_TOWN_PORTAL_LIFETIME_TICKS)));
        }

        private void ProcessPendingReturnTownPortalDespawns(uint tickIndex)
        {
            for (int pendingIndex = 0; pendingIndex < _pendingReturnTownPortalDespawns.Count;)
            {
                var pending = _pendingReturnTownPortalDespawns[pendingIndex];
                if (!HasReachedSimulationTick(tickIndex, pending.DueTick))
                {
                    pendingIndex++;
                    continue;
                }

                int queued = 0;
                foreach (RRConnection recipient in GetConnectionInsertionOrderSnapshot())
                {
                    if (recipient == null || !recipient.IsConnected || !recipient.IsSpawned) continue;
                    if (!string.Equals(recipient.CurrentZoneGcType, pending.ZoneGcType, StringComparison.Ordinal)) continue;
                    if (recipient.InstanceId != pending.InstanceId) continue;
                    var writer = new LEWriter();
                    writer.WriteByte(0x07);
                    writer.WriteByte(0x05);
                    writer.WriteUInt16(pending.EntityId);
                    writer.WriteByte(0x06);
                    if (QueueClientEntityStream(recipient, writer.ToArray()))
                        queued++;
                }

                _pendingReturnTownPortalDespawns.RemoveAt(pendingIndex);
                ReleasePortalEntityId(pending.EntityId);
                Debug.LogError($"[TOWN-PORTAL] Queued return portal despawn entity=0x{pending.EntityId:X4} tick={tickIndex} recipients={queued}");
            }
        }

        private void ClearTownPortalFromDB(RRConnection conn)
        {
            if (conn.LoginName == null || !_selectedCharacter.ContainsKey(conn.LoginName)) return;
            var ch = GetActiveCharacter(conn)?.DeepClone();
            if (ch == null) return;
            ch.tpZone = "";
            ch.tpZoneId = 0;
            ch.tpTargetZone = "";
            ch.tpPosFixedX = 0;
            ch.tpPosFixedY = 0;
            ch.tpPosFixedZ = 0;
            if (TrySaveCharacterForConn(conn, ch, "town-portal-clear"))
                Debug.LogError("[TOWN-PORTAL] Cleared from DB");
        }


        private bool ChangeZone(RRConnection conn, string targetZone, string spawnPoint, uint? forcedInstanceId = null, bool queueEntityRemoval = false, bool restoreFullHealth = false, int? exactSpawnFixedX = null, int? exactSpawnFixedY = null, int? exactSpawnFixedZ = null, int? exactSpawnHeadingFixed = null)
        {
            bool durableCommitted = false;
            try
            {
                if (!TryFindZoneByName(targetZone, out Zone zone))
                {
                    Debug.LogError($"[ZONE] target='{targetZone}' state=notFound");
                    return false;
                }

            bool hasAnyExactCoordinate = exactSpawnFixedX.HasValue || exactSpawnFixedY.HasValue || exactSpawnFixedZ.HasValue;
            bool hasExactPosition = exactSpawnFixedX.HasValue && exactSpawnFixedY.HasValue && exactSpawnFixedZ.HasValue;
            if (hasAnyExactCoordinate != hasExactPosition || restoreFullHealth && !hasExactPosition)
            {
                Debug.LogError($"[ZONE] state=blocked phase=plan target={zone.name} reason=incomplete-exact-spawn");
                return false;
            }

            int persistSpawnFixedX = exactSpawnFixedX ?? zone.SpawnFixedX;
            int persistSpawnFixedY = exactSpawnFixedY ?? zone.SpawnFixedY;
            int persistSpawnFixedZ = exactSpawnFixedZ ?? zone.SpawnFixedZ;
            if (!hasExactPosition
                && !string.IsNullOrEmpty(spawnPoint)
                && AuthoredGameplayCatalog.GetWaypointsForZone(zone.name)
                    .FirstOrDefault(waypoint => waypoint.name != null && waypoint.name.Equals(spawnPoint, StringComparison.OrdinalIgnoreCase)) is { } persistedWaypoint)
            {
                persistSpawnFixedX = persistedWaypoint.PosFixedX;
                persistSpawnFixedY = persistedWaypoint.PosFixedY;
                persistSpawnFixedZ = persistedWaypoint.PosFixedZ;
            }
            if (hasExactPosition)
            {
                PathMap targetPathMap = PathMapCatalog.Instance.GetPathMap(zone.name);
                if (targetPathMap == null || !targetPathMap.IsWalkableFixed(persistSpawnFixedX, persistSpawnFixedY))
                {
                    Debug.LogError($"[ZONE] state=blocked phase=plan target={zone.name} reason={(targetPathMap == null ? "missing-pathmap" : "position-not-walkable")}");
                    return false;
                }
            }
            PlayerState respawnPlayerState = null;
            CombatPlayer respawnCombatPlayer = null;
            if (restoreFullHealth)
            {
                respawnPlayerState = GetPlayerState(conn.ConnId.ToString());
                if (respawnPlayerState == null)
                {
                    Debug.LogError($"[ZONE] state=blocked phase=plan target={zone.name} reason=missing-player-state");
                    return false;
                }
                respawnCombatPlayer = conn.Avatar != null && conn.Avatar.Id > 0
                    ? CombatRuntime.Instance.GetPlayer((uint)conn.Avatar.Id)
                    : null;
                if (respawnCombatPlayer == null
                    || !CombatRuntime.Instance.TryCommitPlayerAttributeModifierDeathEvent(respawnCombatPlayer, "respawn-transition"))
                {
                    Debug.LogError($"[ZONE] state=blocked phase=respawn-modifier-persist target={zone.name}");
                    return false;
                }
            }
                if (!TrySaveZoneTransitionSnapshot(conn, zone, persistSpawnFixedX, persistSpawnFixedY, persistSpawnFixedZ, restoreFullHealth, restoreFullHealth ? "respawn-zone" : "zone"))
                {
                    Debug.LogError($"[ZONE] state=failed phase=persist target={zone.name}");
                    return false;
                }
                durableCommitted = true;
            if (restoreFullHealth)
            {
                respawnPlayerState.RestoreToFull(false);
                if (respawnCombatPlayer != null)
                    respawnCombatPlayer.IsAlive = true;
                conn.RespawnFullHPPending = true;
                conn.PendingSpawnPreserveAuthoredPosition = true;
                conn.PendingSpawnHeadingOverride = true;
                conn.PendingSpawnFixedX = persistSpawnFixedX;
                conn.PendingSpawnFixedY = persistSpawnFixedY;
                conn.PendingSpawnFixedZ = persistSpawnFixedZ;
                if (exactSpawnHeadingFixed.HasValue)
                    conn.PlayerHeadingFixed = exactSpawnHeadingFixed.Value;
            }

            BlingGnomeRuntime.Instance.SetServer(this);
            BlingGnomeRuntime.Instance.CleanupForZoneTransition(conn.ConnId);
                CancelPendingCombatForConnection(conn, preserveAttributeModifiers: true);
                ClearPvpConnectionRuntime(conn, restoreFullHealth ? "respawn-transition" : "zone-transition", true);
                Combat.WeaponUseRuntime.Instance.ClearConnection(conn.ConnId.ToString());
                if (conn.Avatar != null && conn.Avatar.Id > 0)
                {
                    CombatRuntime.Instance.RemoveExternalPlayerAttributeModifiers((uint)conn.Avatar.Id, restoreFullHealth ? "respawn-transition" : "zone-transition");
                    CombatRuntime.Instance.UnregisterPlayer((uint)conn.Avatar.Id, preserveAttributeModifiers: true);
                }

            if (_adminShopNPCs.ContainsKey(conn.ConnId))
            {
                if (_adminShopNPCs.TryGetValue(conn.ConnId, out var shopNpcs))
                {
                    foreach (var npc in shopNpcs)
                    {
                        if (TryGetZoneNpcsForConnection(conn, out var zoneList))
                            zoneList.Remove(npc);
                    }
                    _adminShopNPCs.Remove(conn.ConnId);
                }
            }

            if (conn.IsSpawned)
                BroadcastEntityRemove(conn, conn.CurrentZoneGcType, queueEntityRemoval);

            conn.IsSpawned = false;
            conn.HasLivePlayerPosition = false;
            conn.LivePlayerMovingThisFrame = false;
            ResetReflectedAvatarPosition(conn);

            Debug.LogError($"[ZONE-TRACK] ");
            Debug.LogError($"[ZONE-TRACK] CHANGEZONE START");
            Debug.LogError($"[ZONE-TRACK] conn.UpdateNumber BEFORE: {conn.UpdateNumber}");
            Debug.LogError($"[ZONE-TRACK] Target zone: {targetZone}");
            Debug.LogError($"[ZONE-TRACK] ");
            Debug.LogError($"[ZONE] ");
            Debug.LogError($"[ZONE] ZONE TRANSITION: {targetZone} @ {spawnPoint}");
            Debug.LogError($"[ZONE] ");

            conn.TickUpdatesActive = false;

            conn.AllowFlush = false;

            ClearRemoteActionRelayState(conn);
            ClearPendingPortalActivation(conn);
            ClearPendingPortalTransition(conn);
            conn.MessageQueue.Clear();
            ClearPendingCheckpointActivation(conn);
            Debug.LogError("[ZONE]  Cleared message queue");

            conn.PendingSpawnPoint = spawnPoint ?? "";
            Debug.LogError($"[ZONE] PendingSpawnPoint set to '{conn.PendingSpawnPoint}' for target zone {zone.name}");
            conn.FullHPBaselineOnNextSpawn = ShouldUseFullHPZoneBaseline(conn.CurrentZoneName, zone.name);
            if (conn.FullHPBaselineOnNextSpawn)
                Debug.LogError($"[ZONE-HP-BASELINE] full HP baseline queued: {conn.CurrentZoneName} -> {zone.name}");
            conn.CurrentZoneId = zone.id;
            conn.CurrentZoneName = zone.name;
            _monsterSpawnSentByConn.Remove(conn.ConnId);
            _encounterObjectSentByConn.Remove(conn.ConnId);
            GroupDirectory.Instance.UpdateMemberZone(conn.ConnId, zone.name);
            SendGroupMemberZoneState(conn);
            conn.CurrentZoneGcType = ResolveZoneGcType(zone);

            Debug.LogError($"[ZONE] CurrentZoneGcType set to: {conn.CurrentZoneGcType}");
            Debug.LogError($"[ZONE] CurrentZoneName set to: {conn.CurrentZoneName}");
            if (forcedInstanceId.HasValue)
            {
                conn.InstanceId = forcedInstanceId.Value;
                StampRuntimeInstanceKey(conn, "pvp-match");
                Debug.LogError($"[INSTANCE] {conn.LoginName} -> PVP MATCH '{conn.CurrentZoneName}' (forced instance {conn.InstanceId:X8})");
            }
            else
            {
                AssignInstanceId(conn);
            }

            if (_selectedCharacter.TryGetValue(conn.LoginName, out var zoneChangeChar2))
            {
                SocialRuntime.Instance.NotifyFriendsZoneChange(conn.LoginName, zoneChangeChar2.Name, zone.name, SendSocialViaAuth);
                SocialRuntime.Instance.PushWhoListToAll(SendSocialViaAuth);
            }

            ClearConnZoneEntities(conn);

            Debug.LogError("[ZONE]  Keeping conn.Avatar and conn.Player for entity reuse");

            var disconnectWriter = new LEWriter();
            disconnectWriter.WriteByte(0x0D);
            disconnectWriter.WriteByte(0x02);
            disconnectWriter.WriteCString("zoneleave");
            SendCompressedA(conn, 0x01, 0x0F, disconnectWriter.ToArray());
            Debug.LogError("[ZONE]  Sent DISCONNECT");

            var writer = new LEWriter();
            writer.WriteByte(0x0D);
            writer.WriteByte(0x00);
            writer.WriteCString(zone.name);

            uint zoneSeed = ResolveZoneConnectSeed(conn, zone.name);
            writer.WriteUInt32(zoneSeed);
            Debug.LogError($"[ZONE] Sending seed: 0x{zoneSeed:X8} for zone {zone.name}");

            WriteDungeonQuestRooms(writer, conn, zone.name);
            writer.WriteUInt32(0x00);

            SendCompressedA(conn, 0x01, 0x0F, writer.ToArray());
            Debug.LogError($"[ZONE]  Sent CONNECT seed=0x{zoneSeed:X8} - waiting for client 0x06 response");

            if (DungeonMazeSpawner.IsProceduralZone(zone.name))
                ZoneSpawner.Instance.BeginProceduralSnapshotPreparation(zone.name, GetInstanceZoneKey(conn), zoneSeed);

                return true;
            }
            catch (Exception ex)
            {
                if (durableCommitted)
                {
                    QuarantineCommittedPersistenceSyncFailure(conn, restoreFullHealth ? "respawn-zone-runtime" : "zone-runtime", ex);
                    return true;
                }
                Debug.LogError($"[ZONE] state=failed phase=plan-exception target={targetZone ?? ""} errorType={ex.GetType().Name} message='{ex.Message}'");
                return false;
            }
        }





        [System.Serializable]
        public class ZoneNPC
        {
            public uint Id;
            public uint UnitBehaviorId;
            public string GCClass;
            public string Name;
            public int PosFixedX;
            public int PosFixedY;
            public int PosFixedZ;
            public int HeadingFixed;
            public bool HasFixedPosition;
            public uint HitPointsWire;
            public bool IsMerchant;
            public uint MerchantId;
            public bool IsAdminMerchant;
            public bool IsTrainer;
            public uint TrainerId;
            public bool IsBank;
            public uint BankComponentId;
            public bool IsPosseMagnate;
            public uint PosseOptionComponentId;
            public List<string> TrainerSkills;
        }

        private static List<string> GetTrainerSkillList(string npcGcClass)
        {
            if (npcGcClass.IndexOf("TrainerFighter", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new List<string>
                {
                    "skills.generic.1HMeleeSpeedBuff",
                    "skills.generic.2HMeleeSpeedBuff",
                    "skills.generic.CleaveUpgradeProcPassive",
                    "skills.generic.FearResistModPassive",
                    "skills.generic.Butcher",
                    "skills.generic.Cleave",
                    "skills.generic.ShadowRage",
                    "skills.generic.DivineDamageBuff",
                    "skills.generic.DivineResistBuff",
                    "skills.generic.DivineResistPassive",
                    "skills.generic.FearMeleeAttack",
                    "skills.generic.FighterClassPassive",
                    "skills.generic.HealSelf",
                    "skills.generic.Charge",
                    "skills.generic.BlockKnockdownProcPassive",
                    "skills.generic.MinMoveSpeedBuff",
                    "skills.generic.MeleeAttackRatingModPassive",
                    "skills.generic.FireMeleeSummon",
                    "skills.generic.AggroIncreaseModBuff",
                    "skills.generic.MeleeDamageReflectionBuff",
                    "skills.generic.Stomp",
                    "skills.generic.DivineMeleeAttack",
                    "skills.generic.StunResistBuff",
                    "skills.generic.SlowDeBuff",
                    "skills.generic.MeleeAttackSpeedModPassive",
                };
            }
            else if (npcGcClass.IndexOf("TrainerMage", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new List<string>
                {
                    "skills.generic.ManaShield",
                    "skills.generic.MageClassPassive",
                    "skills.Generic.SummonSnowman",
                    "skills.generic.IceDamageBuff",
                    "skills.generic.IceTargetedBurst",
                    "skills.generic.SummonerClassPassive",
                    "skills.generic.FireCone",
                    "skills.generic.ShadowDamageBuff",
                    "skills.generic.FireBolt",
                    "skills.generic.FireResistBuff",
                    "skills.generic.FireResistPassive",
                    "skills.generic.DivineIntervention",
                    "skills.generic.IceBolt",
                    "skills.generic.IceResistBuff",
                    "skills.generic.IceMultiBolt",
                    "skills.generic.IceResistPassive",
                    "skills.generic.ShadowLightningUpgradeProcPassive",
                    "skills.generic.MagicDamageModPassive",
                    "skills.generic.SnowManIceDamageProcAuraModBuff",
                    "skills.generic.IceTargetedBurstUpgradeProcPassive",
                    "skills.generic.SnowmanFreezeAura",
                    "skills.generic.FireDamageBuff",
                    "skills.generic.DivineRay",
                    "skills.generic.FireRing",
                    "skills.generic.ShadowBolt",
                    "skills.generic.ShadowLightning",
                    "skills.generic.ShadowResistBuff",
                    "skills.generic.ShadowResistPassive",
                    "skills.generic.ShadowLightningKnockdown",
                    "skills.generic.ShadowTendrils",
                    "skills.generic.SnowmanHealthModAuraBuff",
                    "skills.generic.ManaSelf",
                    "skills.generic.Teleport",
                };
            }
            else if (npcGcClass.IndexOf("TrainerRanger", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new List<string>
                {
                    "skills.generic.Blight",
                    "skills.generic.InfectiousPoisonUpgradeProcPassive",
                    "skills.generic.RangerClassPassive",
                    "skills.generic.FireCurseShot",
                    "skills.generic.MonsterBaitHealthModPassive",
                    "skills.generic.FireShot",
                    "skills.generic.FearShot",
                    "skills.generic.PoisonBlastRadius",
                    "skills.generic.PoisonDamageBuff",
                    "skills.generic.SummonMonsterBait",
                    "skills.generic.PoisonTrail",
                    "skills.generic.NoxiousShot",
                    "skills.generic.PoisonResistBuff",
                    "skills.generic.PoisonResistPassive",
                    "skills.generic.PoisonShot",
                    "skills.generic.PenetrateKnockdownShot",
                    "skills.generic.RangedSpeedBuff",
                    "skills.generic.IceShot",
                    "skills.generic.Sprint",
                    "skills.generic.RangeAttackSpeedModPassive",
                };
            }
            return new List<string>();
        }


        private void InitializeTownNPCs()
        {
            Debug.LogError("");
            Debug.LogError("[INIT-TOWN-NPCS] phase=start zone=town");
            Debug.LogError("");

            var townZone = _zoneOrder.FirstOrDefault(z => z.name.ToLower() == "town");
            if (townZone == null)
            {
                Debug.LogError("[INIT-TOWN-NPCS] zone=town state=missing");
                return;
            }

            uint zoneId = townZone.id;
            _zoneNPCs[zoneId] = new List<ZoneNPC>();

            if (AuthoredGameplayCatalog.TownNPCs == null || AuthoredGameplayCatalog.TownNPCs.Count == 0)
            {
                Debug.LogError("[INIT-TOWN-NPCS] zone=town state=empty");
                return;
            }

            foreach (var npcData in AuthoredGameplayCatalog.TownNPCs)
            {
                bool isMerchant = MerchantRuntime.IsMerchant(npcData.gcType);
                bool isTrainer = npcData.gcType.IndexOf("Trainer", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isBank = npcData.gcType.EndsWith(".Bank", StringComparison.OrdinalIgnoreCase);
                bool isPosseMagnate = npcData.gcType.EndsWith(".PosseMagnate", StringComparison.OrdinalIgnoreCase);
                var npc = new ZoneNPC
                {
                    GCClass = npcData.gcType,
                    Name = npcData.name,
                    PosFixedX = npcData.PosFixedX,
                    PosFixedY = npcData.PosFixedY,
                    PosFixedZ = npcData.PosFixedZ,
                    HeadingFixed = npcData.HeadingFixed,
                    HasFixedPosition = true,
                    Id = AllocateGeneralEntityId(),
                    UnitBehaviorId = AllocateGeneralEntityId(),
                    IsMerchant = isMerchant,
                    MerchantId = isMerchant ? AllocateGeneralEntityId() : 0,
                    IsTrainer = isTrainer,
                    TrainerId = isTrainer ? AllocateGeneralEntityId() : 0,
                    TrainerSkills = isTrainer ? GetTrainerSkillList(npcData.gcType) : null,
                    IsBank = isBank,
                    BankComponentId = isBank ? AllocateGeneralEntityId() : 0,
                    IsPosseMagnate = isPosseMagnate,
                    PosseOptionComponentId = isPosseMagnate ? AllocateGeneralEntityId() : 0
                };

                _zoneNPCs[zoneId].Add(npc);
                string tags = (isMerchant ? " merchant=true" : "") + (isTrainer ? $" trainerId={npc.TrainerId}" : "") + (isBank ? $" bankId={npc.BankComponentId}" : "") + (isPosseMagnate ? $" posseId={npc.PosseOptionComponentId}" : "");
                Debug.LogError($"[INIT-TOWN-NPCS] create name='{npc.Name}' id={npc.Id}{tags}");
            }
            var tutorialZone = _zoneOrder.FirstOrDefault(z => z.name.ToLower() == "tutorial");
            if (tutorialZone != null)
            {
                uint tutorialZoneId = tutorialZone.id;
                _zoneNPCs[tutorialZoneId] = new List<ZoneNPC>();

                if (AuthoredGameplayCatalog.TutorialNPCs != null && AuthoredGameplayCatalog.TutorialNPCs.Count > 0)
                {
                    foreach (var npcData in AuthoredGameplayCatalog.TutorialNPCs)
                    {
                        bool isMerchant = MerchantRuntime.IsMerchant(npcData.gcType);
                        bool isTrainer = npcData.gcType.IndexOf("Trainer", StringComparison.OrdinalIgnoreCase) >= 0;
                        bool isBank = npcData.gcType.EndsWith(".Bank", StringComparison.OrdinalIgnoreCase);
                        bool isPosseMagnate = npcData.gcType.EndsWith(".PosseMagnate", StringComparison.OrdinalIgnoreCase);
                        var npc = new ZoneNPC
                        {
                            GCClass = npcData.gcType,
                            Name = npcData.name,
                            PosFixedX = npcData.PosFixedX,
                            PosFixedY = npcData.PosFixedY,
                            PosFixedZ = npcData.PosFixedZ,
                            HeadingFixed = npcData.HeadingFixed,
                            HasFixedPosition = true,
                            Id = AllocateGeneralEntityId(),
                            UnitBehaviorId = AllocateGeneralEntityId(),
                            IsMerchant = isMerchant,
                            MerchantId = isMerchant ? AllocateGeneralEntityId() : 0,
                            IsTrainer = isTrainer,
                            TrainerId = isTrainer ? AllocateGeneralEntityId() : 0,
                            TrainerSkills = isTrainer ? GetTrainerSkillList(npcData.gcType) : null,
                            IsBank = isBank,
                            BankComponentId = isBank ? AllocateGeneralEntityId() : 0,
                            IsPosseMagnate = isPosseMagnate,
                            PosseOptionComponentId = isPosseMagnate ? AllocateGeneralEntityId() : 0
                        };

                        _zoneNPCs[tutorialZoneId].Add(npc);
                        string tags = (isMerchant ? " merchant=true" : "") + (isTrainer ? $" trainerId={npc.TrainerId}" : "") + (isBank ? $" bankId={npc.BankComponentId}" : "") + (isPosseMagnate ? $" posseId={npc.PosseOptionComponentId}" : "");
                        Debug.LogError($"[INIT-TUTORIAL-NPCS] create name='{npc.Name}' id={npc.Id}{tags}");
                    }
                }


                Debug.LogError($"[INIT-TOWN-NPCS] zone=town count={_zoneNPCs[zoneId].Count}");
                Debug.LogError("");
            }

            var pvpZone = _zoneOrder.FirstOrDefault(z => z.name.Equals("pvp_start", StringComparison.OrdinalIgnoreCase));
            if (pvpZone != null && AuthoredGameplayCatalog.PvpNPCs != null && AuthoredGameplayCatalog.PvpNPCs.Count > 0)
            {
                uint pvpZoneId = pvpZone.id;
                _zoneNPCs[pvpZoneId] = new List<ZoneNPC>();

                foreach (var npcData in AuthoredGameplayCatalog.PvpNPCs)
                {
                    bool isMerchant = MerchantRuntime.IsMerchant(npcData.gcType);
                    bool isTrainer = npcData.gcType.IndexOf("Trainer", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool isBank = npcData.gcType.EndsWith(".Bank", StringComparison.OrdinalIgnoreCase);
                    bool isPosseMagnate = npcData.gcType.EndsWith(".PosseMagnate", StringComparison.OrdinalIgnoreCase);
                    bool isPvpNpc = npcData.gcType.IndexOf("L33tenant", StringComparison.OrdinalIgnoreCase) >= 0
                                 || npcData.name.IndexOf("L33tenant", StringComparison.OrdinalIgnoreCase) >= 0;
                    var npc = new ZoneNPC
                    {
                        GCClass = npcData.gcType,
                        Name = npcData.name,
                        PosFixedX = npcData.PosFixedX,
                        PosFixedY = npcData.PosFixedY,
                        PosFixedZ = npcData.PosFixedZ,
                        HeadingFixed = npcData.HeadingFixed,
                        HasFixedPosition = true,
                        Id = AllocateGeneralEntityId(),
                        UnitBehaviorId = AllocateGeneralEntityId(),
                        IsMerchant = isMerchant,
                        MerchantId = isMerchant ? AllocateGeneralEntityId() : 0,
                        IsTrainer = isTrainer,
                        TrainerId = isTrainer ? AllocateGeneralEntityId() : 0,
                        TrainerSkills = isTrainer ? GetTrainerSkillList(npcData.gcType) : null,
                        IsBank = isBank,
                        BankComponentId = isBank ? AllocateGeneralEntityId() : 0,
                        IsPosseMagnate = isPosseMagnate,
                        PosseOptionComponentId = isPosseMagnate ? AllocateGeneralEntityId() : 0
                    };

                    _zoneNPCs[pvpZoneId].Add(npc);
                    Debug.LogError($"[INIT-PVP-NPCS] create name='{npc.Name}' id={npc.Id} pvpQueue={isPvpNpc}");
                }
                Debug.LogError($"[INIT-PVP-NPCS] zone=pvp_start count={_zoneNPCs[pvpZoneId].Count}");
            }
            InitializeAuthoredZoneNPCs();
        }

        private void InitializeAuthoredZoneNPCs()
        {
            foreach (Zone zone in _zoneOrder)
            {
                if (_zoneNPCs.ContainsKey(zone.id)
                    || !AuthoredGameplayCatalog.NPCsByZone.TryGetValue(zone.name, out List<NPCData> definitions))
                    continue;
                var npcs = new List<ZoneNPC>();
                foreach (NPCData definition in definitions)
                {
                    bool merchant = AuthoredGameplayCatalog.Merchants.Any(entry => string.Equals(entry.npcGcType, definition.gcType, StringComparison.OrdinalIgnoreCase));
                    bool trainer = definition.gcType.IndexOf("Trainer", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool bank = definition.gcType.EndsWith(".Bank", StringComparison.OrdinalIgnoreCase);
                    bool posseMagnate = definition.gcType.EndsWith(".PosseMagnate", StringComparison.OrdinalIgnoreCase);
                    npcs.Add(new ZoneNPC
                    {
                        GCClass = definition.gcType,
                        Name = definition.name,
                        PosFixedX = definition.PosFixedX,
                        PosFixedY = definition.PosFixedY,
                        PosFixedZ = definition.PosFixedZ,
                        HeadingFixed = definition.HeadingFixed,
                        HasFixedPosition = true,
                        Id = AllocateGeneralEntityId(),
                        UnitBehaviorId = AllocateGeneralEntityId(),
                        IsMerchant = merchant,
                        MerchantId = merchant ? AllocateGeneralEntityId() : 0,
                        IsTrainer = trainer,
                        TrainerId = trainer ? AllocateGeneralEntityId() : 0,
                        TrainerSkills = trainer ? GetTrainerSkillList(definition.gcType) : null,
                        IsBank = bank,
                        BankComponentId = bank ? AllocateGeneralEntityId() : 0,
                        IsPosseMagnate = posseMagnate,
                        PosseOptionComponentId = posseMagnate ? AllocateGeneralEntityId() : 0
                    });
                }
                _zoneNPCs.Add(zone.id, npcs);
            }
        }

        private void SendZoneNPCs(RRConnection conn, uint zoneId)
        {
            if (VerbosePacketLogging) Debug.LogError("");
            if (VerbosePacketLogging) Debug.LogError($"[SEND-ZONE-NPCS] phase=start zone={zoneId}");
            if (VerbosePacketLogging) Debug.LogError("");

            if (!TryGetZoneNpcsForConnection(conn, out var npcs) || npcs.Count == 0)
            {
                Debug.LogError($"[SEND-ZONE-NPCS] zone={zoneId} state=empty");
                return;
            }

            if (VerbosePacketLogging) Debug.LogError($"[SEND-ZONE-NPCS] zone={zoneId} count={npcs.Count}");
            if (VerbosePacketLogging) Debug.LogError($"[SEND-ZONE-NPCS] batch=single");

            var writer = new LEWriter();
            writer.WriteByte(0x07);
            if (VerbosePacketLogging) Debug.LogError($"[SEND-ZONE-NPCS] stream=begin opcode=0x07");

            int npcCounter = 0;
            foreach (var npc in npcs)
            {
                int npcStartPos = writer.Position;
                npcCounter++;
                if (VerbosePacketLogging) Debug.LogError("");
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] ");
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}]  PROCESSING NPC #{npcCounter} OF {npcs.Count}");
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] ");
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}]  Name:     {npc.Name}");
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}]  GCClass:  {npc.GCClass}");
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}]  PositionFixed: ({npc.PosFixedX},{npc.PosFixedY},{npc.PosFixedZ})");
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}]  HeadingFixed: {npc.HeadingFixed}");
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] ");

                ushort npcId = (ushort)npc.Id;
                ushort behaviorId = (ushort)npc.UnitBehaviorId;
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}]  Tracking position for entityId=0x{npcId:X4}");
                ushort skillsId = (ushort)AllocateGeneralEntityId();
                ushort manipulatorsId = (ushort)AllocateGeneralEntityId();
                ushort modifiersId = (ushort)AllocateGeneralEntityId();

                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] ids state=start");
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] npcId=0x{npcId:X4} value={npcId}");
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] behaviorId=0x{behaviorId:X4} value={behaviorId}");
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] skillsId=0x{skillsId:X4} value={skillsId}");
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] manipulatorsId=0x{manipulatorsId:X4} value={manipulatorsId}");
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] modifiersId=0x{modifiersId:X4} value={modifiersId}");
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] ");

                string behaviorGCType = "npc.base.behavior";

                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] gcTypes state=start");
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] entityGcType='{npc.GCClass}' preserveCase=true");
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] behaviorGcType='{behaviorGCType}' preserveCase=false");
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] ");

                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] op=1 action=create-npc-entity opcode=0x01");
                writer.WriteByte(0x01);
                writer.WriteUInt16(npcId);
                string entityGcType = npc.GCClass;
                if (entityGcType.Contains("AdminWeaponVendor")) entityGcType = "world.town.npc.VendorWeapon1";
                else if (entityGcType.Contains("AdminArmorVendor")) entityGcType = "world.town.npc.VendorWeapon2";
                else if (entityGcType.Contains("AdminMiscVendor")) entityGcType = "world.town.npc.VendorWeapon3";
                uint npcHPWire = ResolveAuthoredUnitMaxHealthWire(entityGcType);
                WriteGCType(writer, entityGcType, preserveCase: true);

                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}]  OPERATION 2: CREATE BEHAVIOR COMPONENT (0x32)");
                writer.WriteByte(0x32);
                writer.WriteUInt16(npcId);
                writer.WriteUInt16(behaviorId);
                WriteGCType(writer, behaviorGCType, preserveCase: false);
                writer.WriteByte(0x01);

                writer.WriteByte(0xFF);
                writer.WriteByte(0x00);
                writer.WriteByte(0x00);
                writer.WriteByte(0x01);

                writer.WriteByte(0x85);
                writer.WriteByte(0x00);
                writer.WriteUInt32(0x00000000);
                writer.WriteUInt32(0x00000000);
                writer.WriteUInt32(0x00000000);
                writer.WriteUInt32(0x00000000);
                writer.WriteUInt32(0x00000000);

                writer.WriteByte(0x00);

                writer.WriteByte(0xFF);
                writer.WriteByte(0x00);
                writer.WriteByte(0x00);

                writer.WriteByte(0x00);
                writer.WriteByte(0x00);
                writer.WriteUInt32(0x00000000);
                writer.WriteUInt32(0x00000000);

                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}]  OPERATION 3: CREATE SKILLS COMPONENT (0x32)");
                writer.WriteByte(0x32);
                writer.WriteUInt16(npcId);
                writer.WriteUInt16(skillsId);
                WriteGCType(writer, "skills", preserveCase: false);
                writer.WriteByte(0x01);
                writer.WriteByte(0xFF);
                writer.WriteByte(0xFF);
                writer.WriteByte(0xFF);
                writer.WriteByte(0xFF);
                writer.WriteByte(0x00);
                writer.WriteByte(0x01);
                WriteGCType(writer, "skills.professions.Warrior", preserveCase: true);

                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}]  OPERATION 4: CREATE MANIPULATORS COMPONENT (0x32)");
                writer.WriteByte(0x32);
                writer.WriteUInt16(npcId);
                writer.WriteUInt16(manipulatorsId);
                WriteGCType(writer, "manipulators", preserveCase: false);
                writer.WriteByte(0x01);
                writer.WriteByte(0x00);

                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}]  OPERATION 5: CREATE MODIFIERS COMPONENT (0x32)");
                writer.WriteByte(0x32);
                writer.WriteUInt16(npcId);
                writer.WriteUInt16(modifiersId);
                WriteGCType(writer, "modifiers", preserveCase: false);
                writer.WriteByte(0x01);
                writer.WriteUInt32(0x00000000);
                writer.WriteByte(0x00);
                writer.WriteUInt32(0x00000000);

                if (npc.IsMerchant)
                {
                    int merchantStart = writer.Position;
                    if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}]  CREATING MERCHANT COMPONENT (startPos={merchantStart})");
                    MerchantRuntime.WriteMerchantComponent(writer, npc.GCClass, npcId, (ushort)npc.MerchantId);
                    if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}]  MERCHANT DONE (endPos={writer.Position}, bytes={writer.Position - merchantStart})");
                }

                if (npc.IsTrainer)
                {
                    if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}]  CREATING SKILLTRAINER COMPONENT (trainerId={npc.TrainerId})");
                    writer.WriteByte(0x32);
                    writer.WriteUInt16(npcId);
                    writer.WriteUInt16((ushort)npc.TrainerId);
                    int lastDot = npc.GCClass.LastIndexOf('.');
                    string gcPrefix = npc.GCClass.Substring(0, lastDot);
                    string npcName = npc.GCClass.Substring(lastDot + 1);
                    string skillTrainerGcType = gcPrefix + ".base." + npcName + "Base.SkillTrainer";
                    if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}]  SkillTrainer GCType: {skillTrainerGcType}");
                    WriteGCType(writer, skillTrainerGcType, preserveCase: true);
                    writer.WriteByte(0x00);
                    if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}]  SKILLTRAINER DONE (trainerId=0x{npc.TrainerId:X4})");
                }

                if (npc.IsBank)
                {
                    Debug.LogError($"[NPC-{npcCounter}]  CREATING BANK COMPONENT (bankId={npc.BankComponentId})");
                    writer.WriteByte(0x32);
                    writer.WriteUInt16(npcId);
                    writer.WriteUInt16((ushort)npc.BankComponentId);
                    WriteGCType(writer, "banker", preserveCase: false);
                    writer.WriteByte(0x00);
                    Debug.LogError($"[NPC-{npcCounter}]  BANK DONE (bankId=0x{npc.BankComponentId:X4})");
                }

                if (npc.IsPosseMagnate)
                {
                    Debug.LogError($"[POSSE] CREATING POSSE COMPONENT (cid={npc.PosseOptionComponentId}) for {npc.GCClass}");
                    writer.WriteByte(0x32);
                    writer.WriteUInt16(npcId);
                    writer.WriteUInt16((ushort)npc.PosseOptionComponentId);
                    WriteGCType(writer, "PosseRegistry", preserveCase: false);
                    writer.WriteByte(0x00);
                    Debug.LogError($"[POSSE] POSSE COMPONENT DONE (cid=0x{npc.PosseOptionComponentId:X4})");
                }

                var pvpAccess = GetPvpAccessPoint(npc.GCClass);
                if (pvpAccess != null)
                {
                    ushort pvpComponentId = (ushort)AllocateGeneralEntityId();
                    Debug.LogError($"[PVP-ACCESS] CREATING PVPACCESSPOINT COMPONENT (cid=0x{pvpComponentId:X4}) gc='{pvpAccess.Value.subPath}' match='{pvpAccess.Value.matchType}' for {npc.Name}");
                    writer.WriteByte(0x32);
                    writer.WriteUInt16(npcId);
                    writer.WriteUInt16(pvpComponentId);
                    WriteGCType(writer, pvpAccess.Value.subPath, preserveCase: true);
                    writer.WriteByte(0x00);
                }





                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}]  OPERATION 6: INIT NPC ENTITY (0x02)");
                writer.WriteByte(0x02);
                writer.WriteUInt16(npcId);
                writer.WriteUInt32(0x06);

                npc.HitPointsWire = npcHPWire;

                writer.WriteInt32(npc.PosFixedX);
                writer.WriteInt32(npc.PosFixedY);
                writer.WriteInt32(npc.PosFixedZ);
                writer.WriteInt32(npc.HeadingFixed);
                writer.WriteByte(0x00);
                writer.WriteByte(0x01);

                for (int zeroIndex = 0; zeroIndex < 8; zeroIndex++)
                    writer.WriteUInt32(0x00000000);

                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}]  OPERATION 7: WARP TO POSITION (0x35)");
                writer.WriteByte(0x35);
                writer.WriteUInt16(behaviorId);
                writer.WriteByte(0x04);
                writer.WriteByte(0x11);
                writer.WriteByte(0x00);

                writer.WriteInt32(npc.PosFixedX);
                writer.WriteInt32(npc.PosFixedY);
                writer.WriteInt32(npc.PosFixedZ);

                writer.WriteByte(0x02);
                writer.WriteUInt32(npcHPWire);
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}]  NPC OPERATIONS WRITTEN TO BATCH (startPos={npcStartPos}, endPos={writer.Position}, bytes={writer.Position - npcStartPos}, isMerchant={npc.IsMerchant}, isTrainer={npc.IsTrainer})");
            }

            writer.WriteByte(0x06);
            if (VerbosePacketLogging) Debug.LogError($"[SEND-ZONE-NPCS] stream=end opcode=0x06");

            byte[] packetData = writer.ToArray();
            int totalPacketSize = packetData.Length;
            if (VerbosePacketLogging) Debug.LogError($"[SEND-ZONE-NPCS] batchBytes={totalPacketSize} npcs={npcs.Count}");
            if (VerbosePacketLogging) Debug.LogError($"[SEND-ZONE-NPCS] hexFirst200={BitConverter.ToString(packetData, 0, Math.Min(200, packetData.Length))}");

            if (VerbosePacketLogging) Debug.LogError("[SEND-ZONE-NPCS] sendPhase=begin");
            SendCompressedA(conn, 0x01, 0x0f, packetData);
            MarkSimulationParityNpcsKnown(conn, zoneId, npcs);
            if (VerbosePacketLogging) Debug.LogError("[SEND-ZONE-NPCS] sendPhase=complete");

            if (VerbosePacketLogging) Debug.LogError("");
            if (VerbosePacketLogging) Debug.LogError("");
            if (VerbosePacketLogging) Debug.LogError($"[SEND-ZONE-NPCS] sent={npcs.Count} batch=single");
            if (VerbosePacketLogging) Debug.LogError("");
        }

        private readonly Dictionary<string, (string subPath, string matchType)?> _pvpAccessPointCache =
            new Dictionary<string, (string subPath, string matchType)?>(StringComparer.OrdinalIgnoreCase);

        private struct PendingGroupGotoWarpInput
        {
            public ulong Sequence;
            public int ConnId;
            public string InstanceKey;
            public ushort ComponentId;
            public uint TargetCharSqlId;
            public string TargetLogin;
            public int FixedX;
            public int FixedY;
            public int FixedZ;
            public uint ReceivedTick;
            public bool PacketSent;
            public uint PacketWriterTick;
            public uint SimulationApplyTick;
            public int WireMessageIndex;
        }

        private readonly List<PendingGroupGotoWarpInput> _pendingGroupGotoWarpInputs = new List<PendingGroupGotoWarpInput>();
        private ulong _nextGroupGotoWarpSequence;

        private (string subPath, string matchType)? GetPvpAccessPoint(string gcType)
        {
            if (string.IsNullOrEmpty(gcType)) return null;
            if (_pvpAccessPointCache.TryGetValue(gcType, out var cached)) return cached;

            (string subPath, string matchType)? result = null;
            try
            {
                var node = GCDatabase.Instance?.ResolveWithInheritance(gcType);
                if (node != null)
                {
                    foreach (var child in node.EnumerateChildrenInOrder())
                    {
                        if (child != null && !string.IsNullOrEmpty(child.Extends)
                            && child.Extends.IndexOf("PVPAccessPoint", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            result = (gcType + "." + child.Name, child.GetString("MatchType"));
                            break;
                        }
                    }
                }
            }
            catch (Exception ex) { Debug.LogError($"[PVP-ACCESS] resolve failed for '{gcType}': {ex.Message}"); }

            _pvpAccessPointCache[gcType] = result;
            return result;
        }

        private bool QueueGroupGotoWarp(RRConnection source, RRConnection target, uint targetCharSqlId, int fixedX, int fixedY, int fixedZ)
        {
            if (source == null
                || target == null
                || !source.IsConnected
                || !target.IsConnected
                || !source.IsSpawned
                || !target.IsSpawned
                || !source.TickUpdatesActive
                || !target.TickUpdatesActive
                || !source.AllowFlush
                || !target.AllowFlush
                || !IsSameMovementRuntime(source, target))
                return false;

            ushort componentId = ResolveClientControlComponentId(source, 0);
            if (componentId == 0)
                return false;

            var peerWarps = new List<(RRConnection Viewer, ushort BehaviorId)>();
            foreach (RRConnection viewer in GetConnectionInsertionOrderSnapshot())
            {
                if (viewer == null || viewer == source || !viewer.IsConnected || !viewer.IsSpawned || !viewer.AllowFlush)
                    continue;
                if (!IsSameMovementRuntime(viewer, source))
                    continue;
                if (!TryResolveRemoteBehaviorForViewer(viewer, source, out ushort remoteBehaviorId))
                    continue;
                if (GetRemotePeerActionState(viewer, source, false) == null)
                    return false;
                peerWarps.Add((viewer, remoteBehaviorId));
            }

            bool removeZoneSpawnInvulnerability = IsZoneSpawnInvulnerabilityActive(source);
            ushort remoteModifiersId = source.ReplicaModId;
            if (removeZoneSpawnInvulnerability && peerWarps.Count != 0 && remoteModifiersId == 0)
                return false;

            string instanceKey = RoomRuntime.NormalizeInstanceKey(ResolveConnectionInstanceKey(source));
            ulong sequence = ++_nextGroupGotoWarpSequence;
            if (sequence == 0)
                sequence = ++_nextGroupGotoWarpSequence;
            _pendingGroupGotoWarpInputs.Add(new PendingGroupGotoWarpInput
            {
                Sequence = sequence,
                ConnId = source.ConnId,
                InstanceKey = instanceKey,
                ComponentId = componentId,
                TargetCharSqlId = targetCharSqlId,
                TargetLogin = target.LoginName ?? string.Empty,
                FixedX = fixedX,
                FixedY = fixedY,
                FixedZ = fixedZ,
                ReceivedTick = _combatTick,
                WireMessageIndex = -1
            });

            source.MessageQueue.EnqueueDeferred(
                () => BuildGroupGotoWarpUpdate(source, null, componentId, instanceKey, fixedX, fixedY, fixedZ),
                componentId: componentId);

            foreach (var peerWarp in peerWarps)
            {
                RRConnection capturedViewer = peerWarp.Viewer;
                ushort capturedBehaviorId = peerWarp.BehaviorId;
                if (!MarkPeerWarpToControlCycle(capturedViewer, source))
                    throw new InvalidOperationException("Group Go To peer control state disappeared after preflight");
                if (removeZoneSpawnInvulnerability)
                {
                    capturedViewer.MessageQueue.EnqueueDeferred(
                        () => BuildRemoteZoneSpawnInvulnerabilityRemoveUpdate(source, capturedViewer, remoteModifiersId, instanceKey),
                        componentId: remoteModifiersId);
                }
                capturedViewer.MessageQueue.EnqueueDeferred(
                    () => BuildGroupGotoWarpUpdate(source, capturedViewer, capturedBehaviorId, instanceKey, fixedX, fixedY, fixedZ),
                    componentId: capturedBehaviorId);
            }

            Debug.LogError($"[GROUP-GOTO-WARP] state=queued seq={sequence} source={source.LoginName} target={target.LoginName} targetCharSqlId={targetCharSqlId} component={componentId} instance='{instanceKey}' posFixed=({fixedX},{fixedY},{fixedZ}) receiveTick={_combatTick} peers={peerWarps.Count} sourceFunction=GroupClient::gotoMember@0x005F78E0->WarpTo::readInit@0x00531F00");
            return true;
        }

        private byte[] BuildRemoteZoneSpawnInvulnerabilityRemoveUpdate(RRConnection source, RRConnection viewer, ushort componentId, string instanceKey)
        {
            if (source == null
                || viewer == null
                || !source.IsConnected
                || !source.IsSpawned
                || !source.AllowFlush
                || !viewer.IsConnected
                || !viewer.IsSpawned
                || !viewer.AllowFlush
                || componentId == 0
                || source.ReplicaModId != componentId
                || !IsSameMovementRuntime(viewer, source)
                || !string.Equals(RoomRuntime.NormalizeInstanceKey(ResolveConnectionInstanceKey(source)), instanceKey, StringComparison.OrdinalIgnoreCase))
                return Array.Empty<byte>();

            var writer = new LEWriter();
            writer.WriteByte(0x35);
            writer.WriteUInt16(componentId);
            writer.WriteByte(0x01);
            writer.WriteUInt32(ZoneSpawnInvulnerabilityModifierId);
            return TryWriteRemoteAvatarEntitySynchInfo(source, writer, componentId, 0x01, "GROUP-GOTO-ZONE-INVULN-REMOVE")
                ? writer.ToArray()
                : Array.Empty<byte>();
        }

        private byte[] BuildGroupGotoWarpUpdate(RRConnection source, RRConnection viewer, ushort componentId, string instanceKey, int fixedX, int fixedY, int fixedZ)
        {
            if (source == null
                || !source.IsConnected
                || !source.IsSpawned
                || !source.AllowFlush
                || componentId == 0
                || !string.Equals(RoomRuntime.NormalizeInstanceKey(ResolveConnectionInstanceKey(source)), instanceKey, StringComparison.OrdinalIgnoreCase))
                return Array.Empty<byte>();
            if (viewer != null)
            {
                if (!viewer.IsConnected
                    || !viewer.IsSpawned
                    || !viewer.AllowFlush
                    || !IsSameMovementRuntime(viewer, source)
                    || !TryResolveRemoteBehaviorForViewer(viewer, source, out ushort currentBehaviorId)
                    || currentBehaviorId != componentId)
                    return Array.Empty<byte>();
            }
            else if (ResolveClientControlComponentId(source, 0) != componentId)
            {
                return Array.Empty<byte>();
            }

            var writer = new LEWriter();
            writer.WriteByte(0x35);
            writer.WriteUInt16(componentId);
            writer.WriteByte(0x04);
            writer.WriteByte(0x11);
            writer.WriteByte(0x00);
            writer.WriteInt32(fixedX);
            writer.WriteInt32(fixedY);
            writer.WriteInt32(fixedZ);
            bool suffixWritten = viewer == null
                ? TryWriteEntitySynchForComponent(source, writer, componentId, 0x04, EntitySynchInfoContext.PlayerActionResponse, "GROUP-GOTO-WARP-OWNER")
                : TryWriteRemoteAvatarEntitySynchInfo(source, writer, componentId, 0x04, "GROUP-GOTO-WARP-PEER");
            return suffixWritten ? writer.ToArray() : Array.Empty<byte>();
        }

        private static bool IsGroupGotoWarpUpdate(byte[] message, PendingGroupGotoWarpInput input)
        {
            return message != null
                && message.Length >= 18
                && message[0] == 0x35
                && (ushort)(message[1] | (message[2] << 8)) == input.ComponentId
                && message[3] == 0x04
                && message[4] == 0x11
                && message[5] == 0x00
                && ReadQueuedActionUInt32(message, 6) == unchecked((uint)input.FixedX)
                && ReadQueuedActionUInt32(message, 10) == unchecked((uint)input.FixedY)
                && ReadQueuedActionUInt32(message, 14) == unchecked((uint)input.FixedZ);
        }

        private void MarkPendingGroupGotoWarpPacketsFlushed(RRConnection conn, IReadOnlyList<byte[]> messages, uint packetWriterTick, uint simulationApplyTick)
        {
            if (conn == null || messages == null || messages.Count == 0 || _pendingGroupGotoWarpInputs.Count == 0)
                return;

            for (int messageIndex = 0; messageIndex < messages.Count; messageIndex++)
            {
                byte[] message = messages[messageIndex];
                for (int inputIndex = 0; inputIndex < _pendingGroupGotoWarpInputs.Count; inputIndex++)
                {
                    PendingGroupGotoWarpInput input = _pendingGroupGotoWarpInputs[inputIndex];
                    if (input.ConnId != conn.ConnId || input.PacketSent || !IsGroupGotoWarpUpdate(message, input))
                        continue;
                    input.PacketSent = true;
                    input.PacketWriterTick = packetWriterTick;
                    input.SimulationApplyTick = simulationApplyTick;
                    input.WireMessageIndex = messageIndex;
                    _pendingGroupGotoWarpInputs[inputIndex] = input;
                    Debug.LogError($"[GROUP-GOTO-WARP] state=packet-sent seq={input.Sequence} source={conn.LoginName} target={input.TargetLogin} targetCharSqlId={input.TargetCharSqlId} component={input.ComponentId} packetWriterTick={packetWriterTick} simulationApplyTick={simulationApplyTick} messageIndex={messageIndex}");
                    break;
                }
            }
        }

        private bool IsGroupGotoWarpInputCurrent(PendingGroupGotoWarpInput input, RRConnection conn)
        {
            return conn != null
                && conn.IsConnected
                && conn.IsSpawned
                && conn.TickUpdatesActive
                && conn.AllowFlush
                && ResolveClientControlComponentId(conn, 0) == input.ComponentId
                && string.Equals(RoomRuntime.NormalizeInstanceKey(ResolveConnectionInstanceKey(conn)), input.InstanceKey, StringComparison.OrdinalIgnoreCase);
        }

        private void CompleteGroupGotoRemoteActionState(RRConnection source)
        {
            if (source == null || string.IsNullOrWhiteSpace(source.LoginName))
                return;
            foreach (RRConnection viewer in GetConnectionInsertionOrderSnapshot())
            {
                if (viewer == null || viewer == source || string.IsNullOrWhiteSpace(viewer.LoginName))
                    continue;
                string key = RemoteBehaviorSessionKey(viewer, source);
                if (!_remotePeerActionStates.TryGetValue(key, out RemotePeerActionState state))
                    continue;
                state.ActionActive = false;
                state.CurrentActionInterruptLocked = false;
                state.AwaitingOwnerMovement = false;
                state.ActionOpcode = 0;
                state.ActionSessionId = 0;
                state.HasSourceUseTargetSession = false;
                state.SourceUseTargetSessionId = 0;
                state.ActionTerminationTick = 0;
                ClearPeerPendingAction(state);
            }
            source.RemoteActionRelayed = false;
        }

        private void ApplyGroupGotoWarp(RRConnection source, PendingGroupGotoWarpInput input, uint simulationTick)
        {
            ClearZoneSpawnInvulnerability(source, "GROUP-GOTO");
            CompleteGroupGotoRemoteActionState(source);
            CancelUseTargetMoving(source, "group-goto-warp", false);
            ClearUseTarget(source);
            DiscardPendingUseTargetBehaviorAction(source, "GroupGoto-replaces-pending-action");
            DiscardPendingPlayerUsePositionWeaponBehaviorAction(source, "GroupGoto-replaces-pending-action");
            DiscardPendingPlayerSpellBehaviorAction(source, 0, "GroupGoto-replaces-pending-action");
            Combat.WeaponUseRuntime.Instance.ClearConnection(source.ConnId.ToString());
            if (source.Avatar != null)
                CombatRuntime.Instance.SetPlayerActiveClientAttack((uint)source.Avatar.Id, false);
            source.UsePositionActionMirrored = false;
            source.UsePositionActionStoppedFollowClient = false;
            source.UsePositionActionComponentId = 0;
            source.UsePositionManipulatorId = 0;
            source.UsePositionActionSessionId = 0;
            source.UsePositionActionTargetFixedX = 0;
            source.UsePositionActionTargetFixedY = 0;
            source.UsePositionActionTargetFixedZ = 0;
            source.UsePositionActionApplyTick = 0;
            source.UsePositionActionBusyUntilTick = 0;
            source.UsePositionWeaponAction = false;
            source.UsePositionActionAdmissionFollowClientRecords = 0;

            int headingFixed = source.PlayerHeadingFixed;
            source.PlayerPosFixedX = input.FixedX;
            source.PlayerPosFixedY = input.FixedY;
            source.PlayerPosFixedZ = input.FixedZ;
            source.HasLivePlayerPosition = true;
            source.LivePlayerPosFixedX = input.FixedX;
            source.LivePlayerPosFixedY = input.FixedY;
            source.LivePlayerPosFixedZ = input.FixedZ;
            source.LivePlayerHeadingFixed = headingFixed;
            source.LivePlayerMovingThisFrame = false;
            source.AvatarAggroSampleQueue.Clear();
            source.AggroSamplePosFixedX = input.FixedX;
            source.AggroSamplePosFixedY = input.FixedY;
            source.LastRawMoveCount = 0;
            source.LastRawMoveData = null;
            SetReflectedAvatarPosition(source, input.FixedX, input.FixedY, input.FixedZ, headingFixed, true);
            source.MessageQueue.ResetNativeMoverAdmission(input.ComponentId);

            foreach (RRConnection viewer in GetConnectionInsertionOrderSnapshot())
            {
                if (viewer == null || viewer == source || !IsSameMovementRuntime(viewer, source))
                    continue;
                if (TryResolveRemoteBehaviorForViewer(viewer, source, out ushort remoteBehaviorId))
                    viewer.MessageQueue.ResetNativeMoverAdmission(remoteBehaviorId);
            }

            if (source.Avatar != null)
                CombatRuntime.Instance.UpdatePlayerPositionFixed((uint)source.Avatar.Id, input.FixedX, input.FixedY, input.FixedZ);
            SyncReflectedAvatarCombatPosition(source);
            Debug.LogError($"[GROUP-GOTO-WARP] state=applied seq={input.Sequence} source={source.LoginName} target={input.TargetLogin} targetCharSqlId={input.TargetCharSqlId} component={input.ComponentId} instance='{input.InstanceKey}' posFixed=({input.FixedX},{input.FixedY},{input.FixedZ}) headingFixed={headingFixed} packetWriterTick={input.PacketWriterTick} simulationTick={simulationTick} sourceFunction=WarpTo::update@0x00531CD0->Unit::setPosition@0x00508DF0");
        }

        private bool ApplyPendingGroupGotoWarpBehaviorChild(uint playerEntityId, uint simulationTick)
        {
            RRConnection source = FindConnectionByAvatarEntityId(playerEntityId);
            if (source == null || _pendingGroupGotoWarpInputs.Count == 0)
                return false;

            for (int inputIndex = _pendingGroupGotoWarpInputs.Count - 1; inputIndex >= 0; inputIndex--)
            {
                PendingGroupGotoWarpInput input = _pendingGroupGotoWarpInputs[inputIndex];
                if (input.ConnId != source.ConnId)
                    continue;
                if (!IsGroupGotoWarpInputCurrent(input, source))
                {
                    _pendingGroupGotoWarpInputs.RemoveAt(inputIndex);
                    Debug.LogError($"[GROUP-GOTO-WARP] state=discarded seq={input.Sequence} source={source.LoginName} reason=stale-runtime");
                    continue;
                }
            }

            int selectedIndex = -1;
            for (int inputIndex = 0; inputIndex < _pendingGroupGotoWarpInputs.Count; inputIndex++)
            {
                PendingGroupGotoWarpInput input = _pendingGroupGotoWarpInputs[inputIndex];
                if (input.ConnId != source.ConnId)
                    continue;
                if (!input.PacketSent || input.SimulationApplyTick == 0 || simulationTick < input.SimulationApplyTick)
                    continue;
                if (selectedIndex < 0)
                {
                    selectedIndex = inputIndex;
                    continue;
                }
                PendingGroupGotoWarpInput selected = _pendingGroupGotoWarpInputs[selectedIndex];
                if (input.PacketWriterTick > selected.PacketWriterTick
                    || input.PacketWriterTick == selected.PacketWriterTick && input.WireMessageIndex > selected.WireMessageIndex
                    || input.PacketWriterTick == selected.PacketWriterTick && input.WireMessageIndex == selected.WireMessageIndex && input.Sequence > selected.Sequence)
                    selectedIndex = inputIndex;
            }
            if (selectedIndex < 0)
                return false;

            PendingGroupGotoWarpInput selectedInput = _pendingGroupGotoWarpInputs[selectedIndex];
            _pendingGroupGotoWarpInputs.RemoveAll(input => input.ConnId == source.ConnId
                && input.PacketSent
                && input.SimulationApplyTick != 0
                && input.SimulationApplyTick <= simulationTick);
            ApplyGroupGotoWarp(source, selectedInput, simulationTick);
            return true;
        }










        private void HandleGroupClientChannel(RRConnection conn, byte messageType, byte[] data)
        {
            string hex = data != null ? BitConverter.ToString(data, 0, Math.Min(data.Length, 60)) : "null";
            Debug.LogError($"[GROUP-CH0B]  RECEIVED from {conn.LoginName}: type=0x{messageType:X2} len={data?.Length ?? 0} hex={hex}");

            var reader = (data != null && data.Length > 0) ? new LEReader(data) : null;

            try
            {
                switch (messageType)
                {

                    case 0x16:
                        {
                            if (reader == null) { Debug.LogError("[GROUP-CH0B] 0x16 no data"); break; }
                            string targetName = reader.ReadCString();
                            Debug.LogError($"[GROUP-CH0B] INVITE BY NAME '{targetName}' from {conn.LoginName}");
                            var target = FindConnectionByName(targetName);
                            if (target == null)
                            {
                                Debug.LogError($"[GROUP-CH0B] Target '{targetName}' not found online");
                                break;
                            }

                            if (!GroupDirectory.Instance.IsInGroup(conn.ConnId))
                            {
                                GroupDirectory.Instance.CreateGroup(conn.ConnId, conn.LoginName, conn.LoginName);
                                var newGroup = GroupDirectory.Instance.GetGroupForConn(conn.ConnId);
                                if (newGroup != null)
                                    SendGroupConnectedToAll(newGroup);
                            }

                            if (GroupDirectory.Instance.InvitePlayer(conn.ConnId, target.ConnId))
                            {
                                var group = GroupDirectory.Instance.GetGroupForConn(conn.ConnId);
                                uint inviterCharId = 0;
                                string inviterName = conn.LoginName;
                                if (_selectedCharacter.TryGetValue(conn.LoginName, out var inviterChar))
                                {
                                    inviterCharId = (uint)inviterChar.Id;
                                    inviterName = inviterChar.Name ?? inviterName;
                                }
                                byte[] invitePacket = GroupPackets.BuildProcessInvitation(
                                    inviterCharId, group.GroupId, inviterName, 0x00);
                                SendToClient(target, invitePacket);
                                Debug.LogError($"[GROUP] Sent processInvitation(0x32) to {target.LoginName}: inviteId=0x{inviterCharId:X8} groupId={group.GroupId} name='{inviterName}'");

                                SendSystemMessage(conn, $"Invite sent to {targetName}.");
                            }
                            break;
                        }

                    case 0x12:
                        {
                            if (reader == null) { Debug.LogError("[GROUP-CH0B] 0x12 no data"); break; }
                            uint targetId = reader.ReadUInt32();
                            Debug.LogError($"[GROUP-CH0B] INVITE BY ID 0x{targetId:X8} from {conn.LoginName}");
                            RRConnection targetById = null;
                            foreach (var candidate in GetConnectionInsertionOrderSnapshot())
                            {
                                if (candidate == null || candidate == conn || !candidate.IsConnected) continue;
                                if (GetCharSqlId(candidate) == targetId) { targetById = candidate; break; }
                            }
                            if (targetById == null)
                            {
                                Debug.LogError($"[GROUP-CH0B] Target charId 0x{targetId:X8} not found online");
                                break;
                            }

                            if (!GroupDirectory.Instance.IsInGroup(conn.ConnId))
                            {
                                GroupDirectory.Instance.CreateGroup(conn.ConnId, conn.LoginName, conn.LoginName);
                                var newGroup = GroupDirectory.Instance.GetGroupForConn(conn.ConnId);
                                if (newGroup != null)
                                    SendGroupConnectedToAll(newGroup);
                            }

                            if (GroupDirectory.Instance.InvitePlayer(conn.ConnId, targetById.ConnId))
                            {
                                var group = GroupDirectory.Instance.GetGroupForConn(conn.ConnId);
                                uint inviterCharId = 0;
                                string inviterName = conn.LoginName;
                                if (_selectedCharacter.TryGetValue(conn.LoginName, out var inviterChar))
                                {
                                    inviterCharId = (uint)inviterChar.Id;
                                    inviterName = inviterChar.Name ?? inviterName;
                                }
                                byte[] invitePacket = GroupPackets.BuildProcessInvitation(
                                    inviterCharId, group.GroupId, inviterName, 0x00);
                                SendToClient(targetById, invitePacket);
                                Debug.LogError($"[GROUP] Sent processInvitation(0x32) to {targetById.LoginName}: inviteId=0x{inviterCharId:X8} groupId={group.GroupId} name='{inviterName}'");
                                SendSystemMessage(conn, $"Invite sent to {targetById.LoginName}.");
                            }
                            break;
                        }

                    case 0x20:
                        {
                            uint inviteId = reader?.ReadUInt32() ?? 0;
                            Debug.LogError($"[GROUP-CH0B] ACCEPT invite (inviteId=0x{inviteId:X8}) from {conn.LoginName}");
                            var group = GroupDirectory.Instance.AcceptInvite(conn.ConnId, conn.LoginName, conn.LoginName);
                            if (group != null)
                            {
                                SendGroupConnectedToAll(group);
                                SendGroupHealthToAll(group);
                                if (group.IsOpen)
                                    SocialRuntime.Instance.PushWhoListToAll(SendSocialViaAuth);
                                Debug.LogError($"[GROUP] Group {group.GroupId} formed with {group.Members.Count} members");
                            }
                            break;
                        }

                    case 0x21:
                        {
                            uint inviteId = reader?.ReadUInt32() ?? 0;
                            Debug.LogError($"[GROUP-CH0B] DECLINE invite (inviteId=0x{inviteId:X8}) from {conn.LoginName}");
                            GroupDirectory.Instance.DeclineInvite(conn.ConnId);
                            break;
                        }

                    case 0x22:
                        {
                            Debug.LogError($"[GROUP-CH0B] LEAVE group from {conn.LoginName}");
                            var leaveGroup = GroupDirectory.Instance.GetGroupForConn(conn.ConnId);
                            bool wasLeader = (leaveGroup != null && leaveGroup.LeaderConnId == conn.ConnId);
                            SendGroupRemoveUser(conn);
                            GroupDirectory.Instance.LeaveGroup(conn.ConnId);
                            conn.GroupConnectedSent = false;

                            SendSoloGroupState(conn);

                            if (leaveGroup != null && leaveGroup.Members.Count > 0)
                            {
                                if (leaveGroup.Members.Count == 1)
                                {
                                    var lastConn = FindConnectionById(leaveGroup.Members[0].ConnId);
                                    if (lastConn != null)
                                    {
                                        SendSoloGroupState(lastConn);
                                        lastConn.GroupConnectedSent = false;
                                    }
                                    GroupDirectory.Instance.LeaveGroup(leaveGroup.Members[0].ConnId);
                                }
                                else
                                {
                                    if (wasLeader)
                                    {
                                        foreach (var remainingMember in leaveGroup.Members)
                                        {
                                            var remainingMemberConnection = FindConnectionById(remainingMember.ConnId);
                                            if (remainingMemberConnection != null) remainingMemberConnection.GroupConnectedSent = false;
                                        }
                                    }
                                    SendGroupConnectedToAll(leaveGroup);
                                }
                            }
                            SocialRuntime.Instance.PushWhoListToAll(SendSocialViaAuth);
                            break;
                        }

                    case 0x14:
                        {
                            if (reader == null) break;
                            uint kickId = reader.ReadUInt32();
                            Debug.LogError($"[GROUP-CH0B] KICK member 0x{kickId:X8} from {conn.LoginName}");
                            var group = GroupDirectory.Instance.GetGroupForConn(conn.ConnId);
                            if (group == null || group.LeaderConnId != conn.ConnId) break;
                            RRConnection kickTarget = FindGroupMemberByCharSqlId(group, kickId);
                            if (kickTarget == null || kickTarget.ConnId == conn.ConnId) break;
                            byte[] kickPacket = GroupPackets.BuildProcessRemoveUser(group.GroupId, kickId);
                            foreach (var member in group.Members)
                            {
                                var memberConnection = FindConnectionById(member.ConnId);
                                if (memberConnection != null) SendToClient(memberConnection, kickPacket);
                            }
                            GroupDirectory.Instance.LeaveGroup(kickTarget.ConnId);
                            kickTarget.GroupConnectedSent = false;
                            SendSoloGroupState(kickTarget);
                            if (group.Members.Count == 1)
                            {
                                var lastConn = FindConnectionById(group.Members[0].ConnId);
                                if (lastConn != null)
                                {
                                    SendSoloGroupState(lastConn);
                                    lastConn.GroupConnectedSent = false;
                                }
                                GroupDirectory.Instance.LeaveGroup(group.Members[0].ConnId);
                            }
                            else
                            {
                                SendGroupConnectedToAll(group);
                            }
                            SocialRuntime.Instance.PushWhoListToAll(SendSocialViaAuth);
                            Debug.LogError($"[GROUP] Kicked charSqlId=0x{kickId:X8} from group {group.GroupId}");
                            break;
                        }

                    case 0x15:
                        {
                            if (reader == null) break;
                            uint newLeaderId = reader.ReadUInt32();
                            Debug.LogError($"[GROUP-CH0B] SET LEADER 0x{newLeaderId:X8} from {conn.LoginName}");
                            var group = GroupDirectory.Instance.GetGroupForConn(conn.ConnId);
                            if (group == null || group.LeaderConnId != conn.ConnId) break;
                            RRConnection newLeaderConn = FindGroupMemberByCharSqlId(group, newLeaderId);
                            if (newLeaderConn == null) break;
                            int newLeaderConnId = newLeaderConn.ConnId;
                            GroupDirectory.Instance.SetLeader(conn.ConnId, newLeaderConnId);
                            byte[] setLeaderPacket = GroupPackets.BuildProcessSetLeader(group.GroupId, newLeaderId, group.MonsterDifficulty);
                            foreach (var member in group.Members)
                            {
                                var memberConnection = FindConnectionById(member.ConnId);
                                if (memberConnection != null)
                                    SendToClient(memberConnection, setLeaderPacket);
                            }
                            SocialRuntime.Instance.PushWhoListToAll(SendSocialViaAuth);
                            Debug.LogError($"[GROUP] processSetLeader group={group.GroupId} newLeader=0x{newLeaderId:X8}");
                            break;
                        }

                    case 0x17:
                        HandleMonsterDifficultyRequest(conn, reader);
                        break;

                    case 0x24:
                        {
                            if (reader == null) break;
                            byte flag = reader.ReadByte();
                            Debug.LogError($"[GROUP-CH0B] SET OPEN GROUP flag={flag} from {conn.LoginName}");
                            var group = GroupDirectory.Instance.GetGroupForConn(conn.ConnId);
                            if (group != null)
                            {
                                group.IsOpen = (flag != 0);
                                SendGroupConnectedToAll(group);
                                SocialRuntime.Instance.PushWhoListToAll(SendSocialViaAuth);
                            }
                            break;
                        }

                    case 0x26:
                        {
                            Debug.LogError($"[GROUP-CH0B] RESET INSTANCES from {conn.LoginName}");
                            var resetGroup = GroupDirectory.Instance.GetGroupForConn(conn.ConnId);
                            if (resetGroup != null && resetGroup.LeaderConnId != conn.ConnId)
                            {
                                SendSystemMessage(conn, "Only the party leader can reset dungeon instances.");
                                break;
                            }
                            GroupDirectory.Instance.ResetInstances(conn.ConnId);
                            int soloReset = ResetSoloDungeonInstances(conn);
                            SendSystemMessage(conn, soloReset > 0
                                ? "Dungeon instances reset. Re-enter to get a new layout."
                                : "Dungeon instances reset.");
                            break;
                        }

                    case 0x28:
                        {
                            if (reader == null) break;
                            byte mode = reader.ReadByte();
                            Debug.LogError($"[GROUP-CH0B] SET INVITE MODE {mode} from {conn.LoginName}");
                            var group = GroupDirectory.Instance.GetGroupForConn(conn.ConnId);
                            if (group != null)
                                group.InviteMode = mode;
                            SendToClient(conn, GroupPackets.BuildChangedInviteMode(mode));
                            break;
                        }

                    case 0x27:
                        {
                            if (reader == null) break;
                            uint gotoId = reader.ReadUInt32();
                            Debug.LogError($"[GROUP-CH0B] GOTO member 0x{gotoId:X8} from {conn.LoginName}");
                            var group = GroupDirectory.Instance.GetGroupForConn(conn.ConnId);
                            if (group == null) break;
                            RRConnection gotoTarget = FindGroupMemberByCharSqlId(group, gotoId);
                            if (gotoTarget == null
                                || gotoTarget.ConnId == conn.ConnId
                                || !gotoTarget.IsConnected
                                || !gotoTarget.IsSpawned
                                || !gotoTarget.TickUpdatesActive
                                || !gotoTarget.AllowFlush)
                            {
                                Debug.LogError($"[GROUP-GOTO] state=rejected source={conn.LoginName} targetCharSqlId={gotoId} reason=target-unavailable");
                                break;
                            }
                            string targetZone = gotoTarget.CurrentZoneName ?? "tutorial";
                            int gotoFixedX = gotoTarget.HasLivePlayerPosition ? gotoTarget.LivePlayerPosFixedX : gotoTarget.PlayerPosFixedX;
                            int gotoFixedY = gotoTarget.HasLivePlayerPosition ? gotoTarget.LivePlayerPosFixedY : gotoTarget.PlayerPosFixedY;
                            int gotoFixedZ = gotoTarget.HasLivePlayerPosition ? gotoTarget.LivePlayerPosFixedZ : gotoTarget.PlayerPosFixedZ;
                            if (IsSameMovementRuntime(conn, gotoTarget))
                            {
                                bool queued = QueueGroupGotoWarp(conn, gotoTarget, gotoId, gotoFixedX, gotoFixedY, gotoFixedZ);
                                Debug.LogError($"[GROUP-GOTO] state={(queued ? "warp-queued" : "rejected")} source={conn.LoginName} target={gotoTarget.LoginName} targetCharSqlId={gotoId} mode=same-runtime posFixed=({gotoFixedX},{gotoFixedY},{gotoFixedZ})");
                                break;
                            }
                            Debug.LogError($"[GROUP-GOTO] state=zone-transition source={conn.LoginName} target={gotoTarget.LoginName} targetCharSqlId={gotoId} zone='{targetZone}' posFixed=({gotoFixedX},{gotoFixedY},{gotoFixedZ})");
                            ChangeZoneToPosition(conn, targetZone, gotoFixedX, gotoFixedY, gotoFixedZ);
                            break;
                        }

                    case 0x29:
                        Debug.LogError($"[PVP] {conn.LoginName} entering PVP hub zone");
                        HandleEnterPvpZone(conn);
                        break;

                    case 0x2A:
                        Debug.LogError($"[PVP] {conn.LoginName} requesting PVP match");
                        HandleRequestPvpMatch(conn, data);
                        break;

                    case 0x2B:
                        Debug.LogError($"[PVP] {conn.LoginName} cancelling PVP queue");
                        HandleCancelPvpMatch(conn);
                        break;

                    case 0x2C:
                        Debug.LogError($"[PVP] {conn.LoginName} leaving PVP system");
                        HandleLeavePvp(conn);
                        break;

                    case 0x2D:
                        {
                            if (reader == null || data.Length < 4) { Debug.LogError("[PVP] 0x2D no data"); break; }
                            uint targetCharSqlId = reader.ReadUInt32();
                            Debug.LogError($"[PVP-DUEL] {conn.LoginName} requests duel with CharSQLID {targetCharSqlId}");
                            HandleDuelRequest(conn, targetCharSqlId);
                            break;
                        }

                    case 0x2E:
                        {
                            Debug.LogError($"[PVP-DUEL] {conn.LoginName} accepts duel");
                            HandleDuelAccept(conn);
                            break;
                        }

                    case 0x2F:
                        {
                            Debug.LogError($"[PVP-DUEL] {conn.LoginName} declines duel");
                            HandleDuelDecline(conn);
                            break;
                        }

                    default:
                        Debug.LogError($"[GROUP-CH0B] unhandled type=0x{messageType:X2}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GROUP-CH0B] type=0x{messageType:X2} state=failed message='{ex.Message}' stack='{ex.StackTrace}'");
            }
        }

        private readonly Gameplay.DuelRuntime _duelRuntime = new Gameplay.DuelRuntime();
    }
}
