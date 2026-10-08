using System;
using System.Collections.Generic;
using DungeonRunners.Combat;
using DungeonRunners.Core;
using DungeonRunners.Engine;
using DungeonRunners.Utilities;

namespace DungeonRunners.Networking
{
    public partial class GameServer
    {
        private sealed class PendingRoomClient
        {
            public RRConnection Connection;
            public int ConnectionId;
            public string LoginName;
            public string InstanceKey;
            public uint ZoneId;
            public uint InstanceId;
            public uint Token;
        }

        private readonly List<PendingRoomClient> _pendingRoomClients = new List<PendingRoomClient>();
        private readonly Dictionary<int, uint> _pendingRoomClientTokens = new Dictionary<int, uint>();
        private uint _nextPendingRoomClientToken;

        private void StagePendingRoomClientEpoch(RRConnection conn)
        {
            if (conn == null || !conn.IsConnected)
                return;

            CancelPendingRoomClientEpoch(conn);
            uint token = ++_nextPendingRoomClientToken;
            if (token == 0)
                token = ++_nextPendingRoomClientToken;
            string instanceKey = RoomRuntime.NormalizeInstanceKey(GetInstanceZoneKey(conn));
            conn.AllowFlush = false;
            conn.TickUpdatesActive = false;
            _pendingRoomClientTokens[conn.ConnId] = token;
            _pendingRoomClients.Add(new PendingRoomClient
            {
                Connection = conn,
                ConnectionId = conn.ConnId,
                LoginName = conn.LoginName ?? string.Empty,
                InstanceKey = instanceKey,
                ZoneId = conn.CurrentZoneId,
                InstanceId = conn.InstanceId,
                Token = token
            });
            Debug.LogError($"[ROOM-EPOCH] instance='{instanceKey}' conn={conn.ConnId} login='{conn.LoginName ?? string.Empty}' state=pending token={token}");
        }

        private void CancelPendingRoomClientEpoch(RRConnection conn)
        {
            if (conn == null)
                return;
            _pendingRoomClientTokens.Remove(conn.ConnId);
            _pendingRoomClients.RemoveAll(pending => pending.ConnectionId == conn.ConnId);
        }

        private bool IsPendingRoomClientCurrent(PendingRoomClient pending)
        {
            if (pending == null || pending.Connection == null)
                return false;
            RRConnection conn = pending.Connection;
            if (!_connections.TryGetValue(pending.ConnectionId, out RRConnection current) || !object.ReferenceEquals(current, conn))
                return false;
            if (!_pendingRoomClientTokens.TryGetValue(pending.ConnectionId, out uint token) || token != pending.Token)
                return false;
            if (!conn.IsConnected || !conn.IsSpawned || conn.CurrentZoneId != pending.ZoneId || conn.InstanceId != pending.InstanceId)
                return false;
            if (!string.Equals(conn.LoginName ?? string.Empty, pending.LoginName, StringComparison.OrdinalIgnoreCase))
                return false;
            return string.Equals(RoomRuntime.NormalizeInstanceKey(GetInstanceZoneKey(conn)), pending.InstanceKey, StringComparison.OrdinalIgnoreCase);
        }

        private void ProcessPendingRoomClientEpochs(uint simulationTick)
        {
            if (_pendingRoomClients.Count == 0)
                return;

            var roomOrder = new List<string>();
            var rooms = new Dictionary<string, List<PendingRoomClient>>(StringComparer.OrdinalIgnoreCase);
            foreach (PendingRoomClient pending in new List<PendingRoomClient>(_pendingRoomClients))
            {
                if (!IsPendingRoomClientCurrent(pending))
                {
                    _pendingRoomClientTokens.Remove(pending?.ConnectionId ?? 0);
                    _pendingRoomClients.Remove(pending);
                    if (pending?.Connection != null && pending.Connection.IsConnected)
                        pending.Connection.Disconnect();
                    continue;
                }
                if (!rooms.TryGetValue(pending.InstanceKey, out List<PendingRoomClient> room))
                {
                    room = new List<PendingRoomClient>();
                    rooms.Add(pending.InstanceKey, room);
                    roomOrder.Add(pending.InstanceKey);
                }
                room.Add(pending);
            }

            foreach (string instanceKey in roomOrder)
            {
                List<PendingRoomClient> pendingRoom = rooms[instanceKey];
                try
                {
                    ProcessPendingRoomClientEpoch(instanceKey, pendingRoom, simulationTick);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[ROOM-EPOCH] instance='{instanceKey}' state=failed message='{ex.Message}' stack='{ex.StackTrace}'");
                    foreach (PendingRoomClient pending in pendingRoom)
                    {
                        _pendingRoomClientTokens.Remove(pending.ConnectionId);
                        _pendingRoomClients.Remove(pending);
                        if (pending.Connection != null && pending.Connection.IsConnected)
                            pending.Connection.Disconnect();
                    }
                }
            }
        }

        private void ProcessPendingRoomClientEpoch(string instanceKey, List<PendingRoomClient> pendingRoom, uint simulationTick)
        {
            var pendingConnections = new HashSet<RRConnection>();
            foreach (PendingRoomClient pending in pendingRoom)
                pendingConnections.Add(pending.Connection);

            var participants = new List<RRConnection>();
            var pendingParticipants = new List<RRConnection>();
            var activeParticipants = new List<RRConnection>();
            bool hadActiveParticipant = false;
            foreach (RRConnection conn in GetConnectionInsertionOrderSnapshot())
            {
                if (conn == null || !conn.IsConnected || !conn.IsSpawned)
                    continue;
                if (!string.Equals(RoomRuntime.NormalizeInstanceKey(GetInstanceZoneKey(conn)), instanceKey, StringComparison.OrdinalIgnoreCase))
                    continue;
                bool pending = pendingConnections.Contains(conn);
                if (!pending && (!conn.AllowFlush || !conn.TickUpdatesActive))
                    continue;
                participants.Add(conn);
                if (pending)
                    pendingParticipants.Add(conn);
                else
                {
                    hadActiveParticipant = true;
                    activeParticipants.Add(conn);
                }
            }

            var failedParticipants = new HashSet<RRConnection>();
            int successfulPendingCount = 0;
            foreach (PendingRoomClient pending in pendingRoom)
            {
                if (SendPendingRoomSnapshot(pending.Connection, simulationTick))
                {
                    successfulPendingCount++;
                    continue;
                }
                failedParticipants.Add(pending.Connection);
                pending.Connection.Disconnect();
            }

            IReadOnlyList<uint> committedEntityIds = BlingGnomeRuntime.Instance.CommitPendingSpawns(this, simulationTick, instanceKey, replicateFreshLifecycle: false);
            var sameEpochEntityIds = new HashSet<uint>(committedEntityIds);
            QueueRoomEntitySnapshots(pendingParticipants, sameEpochEntityIds);
            QueueRoomEntitySnapshots(activeParticipants, sameEpochEntityIds, includeMonsters: false);
            foreach (RRConnection participant in participants)
            {
                for (int entityIndex = 0; entityIndex < committedEntityIds.Count; entityIndex++)
                    BlingGnomeRuntime.Instance.SendGnomeToConnection(participant, committedEntityIds[entityIndex], freshLifecycle: true);
            }

            uint previousSeed = CombatRuntime.Instance.GetRoomSeedForInstance(instanceKey);
            int previousRngPosition = CombatRuntime.Instance.GetRoomRngPosForInstance(instanceKey);
            uint seed = GenerateDungeonLayoutSeed();
            CombatRuntime.Instance.ReseedRoomRng(instanceKey, seed, "pending-room-client-epoch");
            List<RRConnection> commonParticipants = participants;
            if (hadActiveParticipant)
            {
                if (previousSeed == 0)
                    throw new InvalidOperationException($"Active room '{instanceKey}' has no committed seed");
            }
            else
            {
                QueueRoomEntitySnapshots(participants);
            }
            foreach (RRConnection participant in participants)
            {
                if (!failedParticipants.Contains(participant) && participant.IsConnected)
                    _zoneInstanceRoomSeeds[GetDungeonLayoutSeedKey(participant, participant.CurrentZoneName)] = seed;
            }

            var commonPackets = new List<(RRConnection Connection, byte[] Packet)>();
            foreach (RRConnection participant in commonParticipants)
            {
                if (failedParticipants.Contains(participant) || !participant.IsConnected)
                    continue;
                List<byte[]> commonMessages = participant.MessageQueue.DequeueAll(CLIENT_ENTITY_MOVEMENT_RECORD_BUDGET_PER_FLUSH);
                commonPackets.Add((participant, BuildRoomEpochCommonPacket(seed, commonMessages)));
            }

            bool resetTransients = !hadActiveParticipant;
            if (resetTransients)
                CombatRuntime.Instance.ApplyRoomEpochResetTransients(instanceKey, ResetRoomEpochPlayerMover);

            foreach ((RRConnection participant, byte[] packet) in commonPackets)
            {
                if (!SendCompressedAImmediate(participant, 0x01, 0x0F, packet, InferSendCompressedAEntitySynchInfoContext(participant, packet), "ROOM-EPOCH-COMMON"))
                {
                    failedParticipants.Add(participant);
                    participant.Disconnect();
                    continue;
                }
                participant.LastClientEntityUpdateSerial = _clientEntityUpdateSerial;
            }

            foreach (PendingRoomClient pending in pendingRoom)
            {
                RRConnection conn = pending.Connection;
                if (!failedParticipants.Contains(conn) && conn.IsConnected)
                {
                    conn.AllowFlush = true;
                    conn.TickUpdatesActive = true;
                    SendGroupZoneStatesToViewer(conn);
                }
                _pendingRoomClientTokens.Remove(pending.ConnectionId);
                _pendingRoomClients.Remove(pending);
            }
            Debug.LogError($"[ROOM-EPOCH] instance='{instanceKey}' state=committed tick={simulationTick} previousSeed=0x{previousSeed:X8} previousRngPos={previousRngPosition} seed=0x{seed:X8} rngPos={CombatRuntime.Instance.GetRoomRngPosForInstance(instanceKey)} pending={pendingRoom.Count} successfulPending={successfulPendingCount} participants={participants.Count} failed={failedParticipants.Count} hadActive={hadActiveParticipant} sameEpochEntities={sameEpochEntityIds.Count} reseed=True reset={resetTransients} pendingOrder=interval-before-common commonOrder=0C-update-init-remove-14 sourceFunction=ServerEntityManager::updateClients@0x005DF010");
        }

        private bool ResetRoomEpochPlayerMover(uint playerEntityId)
        {
            RRConnection conn = FindConnectionByAvatarEntityId(playerEntityId);
            if (conn == null
                || !conn.IsConnected
                || !conn.IsSpawned
                || !conn.UseTargetMovingActive
                || !conn.UseTargetMovingMoverActive)
                return false;
            PathMap pathMap = ResolveUseTargetMovingPathMap(conn);
            int targetFixedX = conn.UseTargetMovingPathTargetFixedX;
            int targetFixedY = conn.UseTargetMovingPathTargetFixedY;
            bool reissued = BuildUseTargetMovingPath(conn, pathMap, targetFixedX, targetFixedY);
            Debug.LogError($"[USE-TARGET-MOVING-RESET] conn={conn.ConnId} player={playerEntityId} start=({conn.UseTargetMovingFixedX},{conn.UseTargetMovingFixedY}) target=({targetFixedX},{targetFixedY}) request={conn.UseTargetMovingPathRequestId} reissued={reissued} sourceFunction=UnitMover::ResetToInit@0x00535880");
            return reissued;
        }

        private static byte[] BuildRoomEpochCommonPacket(uint seed, IReadOnlyList<byte[]> commonMessages)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x0C);
            writer.WriteUInt32(seed);
            if (commonMessages != null)
            {
                for (int messageIndex = 0; messageIndex < commonMessages.Count; messageIndex++)
                    writer.WriteBytes(commonMessages[messageIndex]);
            }
            writer.WriteByte(0x14);
            writer.WriteByte(0x06);
            return writer.ToArray();
        }

        private void QueueRoomEntitySnapshots(List<RRConnection> participants, HashSet<uint> excludedEntityIds = null, bool includeMonsters = true, HashSet<uint> includedBlingEntityIds = null)
        {
            foreach (RRConnection viewer in participants)
            {
                foreach (uint entityId in CombatRuntime.Instance.GetEntityOrderSnapshot())
                {
                    if (excludedEntityIds != null && excludedEntityIds.Contains(entityId))
                        continue;
                    if (!CombatRuntime.Instance.IsPlayerEntity(entityId))
                        continue;
                    RRConnection source = FindConnectionByAvatarEntityId(entityId);
                    if (source == null || viewer == source || string.Equals(viewer.LoginName, source.LoginName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!source.IsConnected || !source.IsSpawned
                        || !string.Equals(RoomRuntime.NormalizeInstanceKey(source.RuntimeInstanceKey), RoomRuntime.NormalizeInstanceKey(viewer.RuntimeInstanceKey), StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (_remoteBehaviorIds.TryGetValue(viewer.LoginName, out Dictionary<string, ushort> behaviorMap)
                        && behaviorMap.ContainsKey(source.LoginName))
                        continue;
                    byte[] spawn = BuildOtherPlayerSpawnPacket(source, entityId, viewer);
                    if (spawn == null)
                        throw new InvalidOperationException($"Failed to build remote avatar source='{source.LoginName}' viewer='{viewer.LoginName}'");
                    QueueClientEntityStream(viewer, spawn);
                    QueueRemoteClientControl(viewer, source);
                    QueueActiveTargetActionSnapshot(viewer, source, 2);
                }
                foreach (uint entityId in CombatRuntime.Instance.GetEntityOrderSnapshot())
                {
                    if (excludedEntityIds != null && excludedEntityIds.Contains(entityId))
                        continue;
                    if (CombatRuntime.Instance.IsPlayerEntity(entityId))
                        continue;
                    if (CombatRuntime.Instance.IsMonsterEntity(entityId))
                    {
                        if (!includeMonsters)
                            continue;
                        Monster monster = CombatRuntime.Instance.GetMonster(entityId);
                        if (monster != null
                            && string.Equals(RoomRuntime.NormalizeInstanceKey(monster.InstanceKey), RoomRuntime.NormalizeInstanceKey(viewer.RuntimeInstanceKey), StringComparison.OrdinalIgnoreCase))
                            SendMonsterLiveSnapshotToClient(viewer, monster);
                        continue;
                    }
                    if (CombatRuntime.Instance.TryGetEncounterObjectInstanceKey(entityId, out string encounterInstanceKey))
                    {
                        if (string.Equals(
                            RoomRuntime.NormalizeInstanceKey(encounterInstanceKey),
                            RoomRuntime.NormalizeInstanceKey(viewer.RuntimeInstanceKey),
                            StringComparison.OrdinalIgnoreCase))
                            QueueDeferredEncounterObjectRootSnapshot(viewer, entityId, encounterInstanceKey);
                        continue;
                    }
                    if (BlingGnomeLockstepRuntime.Instance.IsEntity(entityId))
                    {
                        if (includedBlingEntityIds != null && !includedBlingEntityIds.Contains(entityId))
                            continue;
                        BlingGnomeRuntime.Instance.SendGnomeToConnection(viewer, entityId);
                        continue;
                    }
                }
            }
        }

        private bool SendPendingRoomSnapshot(RRConnection conn, uint simulationTick)
        {
            List<byte[]> messages = conn.MessageQueue.DequeueAll(int.MaxValue);
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x0D);
            writer.WriteUInt32(simulationTick);
            writer.WriteInt32(0x21);
            writer.WriteInt32(CLIENT_ENTITY_MESSAGE_RATIO);
            writer.WriteInt32(1);
            writer.WriteUInt16(100);
            writer.WriteUInt16(20);
            foreach (byte[] message in messages)
                writer.WriteBytes(message);
            writer.WriteByte(0x46);
            byte[] packet = writer.ToArray();
            Debug.LogError($"[INTERVAL] tick={simulationTick} tickInterval=0x21 messageRatio={CLIENT_ENTITY_MESSAGE_RATIO} queuedSubmessages={messages.Count} block=ROOM-EPOCH-INITIAL-SNAPSHOT sourceFunction=ServerEntityManager::initNewClient@0x005DF320->writeIntervals@0x005E0E70");
            if (!SendCompressedA(conn, 0x01, 0x0F, packet, InferSendCompressedAEntitySynchInfoContext(conn, packet), "ROOM-EPOCH-INITIAL-SNAPSHOT"))
                return false;
            return true;
        }

    }
}
