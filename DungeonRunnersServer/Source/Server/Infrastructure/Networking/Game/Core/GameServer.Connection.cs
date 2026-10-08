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
        private Dictionary<int, RRConnection> _connections = new Dictionary<int, RRConnection>();

        private Dictionary<string, Dictionary<string, DateTime>> _debuffCooldowns
            = new Dictionary<string, Dictionary<string, DateTime>>(StringComparer.OrdinalIgnoreCase);

        public RRConnection GetConnectionByConnId(int connId)
            => _connections.TryGetValue(connId, out var conn) ? conn : null;

        private List<RRConnection> GetConnectionInsertionOrderSnapshot()
        {
            var connections = new List<RRConnection>(_connections.Count);
            for (int connId = 1; connId < _nextConnId; connId++)
            {
                if (_connections.TryGetValue(connId, out RRConnection conn))
                    connections.Add(conn);
            }
            return connections;
        }

        public List<RRConnection> GetInstancePeerConnections(RRConnection conn)
        {
            var peers = new List<RRConnection>();
            if (conn == null) return peers;
            foreach (var other in GetConnectionInsertionOrderSnapshot())
            {
                if (other == null || other == conn) continue;
                if (!other.IsConnected || !other.IsSpawned || !other.AllowFlush) continue;
                if (!IsSameMovementRuntime(other, conn)) continue;
                peers.Add(other);
            }
            return peers;
        }

        private void HandleDuelRequest(RRConnection conn, uint targetCharSqlId)
        {
            RRConnection target = FindConnectionByCharacterId((int)targetCharSqlId);

            if (target == null)
            {
                Debug.LogError($"[PVP-DUEL] Target CharSQLID {targetCharSqlId} not online");
                return;
            }

            IssueDuelChallenge(conn, target);
        }

        private RRConnection FindConnectionByCharacterId(int characterId)
        {
            foreach (RRConnection candidate in GetConnectionInsertionOrderSnapshot())
            {
                if (candidate == null || string.IsNullOrWhiteSpace(candidate.LoginName))
                    continue;
                if (_selectedCharacter.TryGetValue(candidate.LoginName, out var selectedCharacter)
                    && selectedCharacter != null
                    && selectedCharacter.Id == characterId)
                    return candidate;
            }
            return null;
        }

        public string IssueDuelChallenge(RRConnection challengerConn, RRConnection targetConn)
        {
            if (challengerConn == null || targetConn == null) return "Connection not found.";

            var challengerChar = _selectedCharacter.TryGetValue(challengerConn.LoginName, out var cs)
                ? GetActiveCharacter(challengerConn) : null;
            var targetChar = _selectedCharacter.TryGetValue(targetConn.LoginName, out var ts)
                ? GetActiveCharacter(targetConn) : null;
            if (challengerChar == null || targetChar == null)
            {
                Debug.LogError("[PVP-DUEL] Character lookup failed");
                return "Character lookup failed.";
            }

            uint challengerCharSqlId = (uint)challengerChar.id;
            uint targetCharSqlId     = (uint)targetChar.id;
            string err = _duelRuntime.TryChallenge(challengerConn.LoginName, challengerCharSqlId,
                targetConn.LoginName, targetCharSqlId,
                challengerChar.level, targetChar.level, _combatTick);
            if (err != null)
            {
                Debug.LogError($"[PVP-DUEL] Challenge rejected: {err}");
                return err;
            }

            byte[] targetPacket = PVPPackets.BuildDuelStatus(
                PVPPackets.DuelStatusType.Challenged, challengerCharSqlId, 0, 0);
            SendToClient(targetConn, targetPacket);
            Debug.LogError($"[PVP-DUEL] Sent Challenged to {targetConn.LoginName}");
            return null;
        }

        public bool AcceptDuel(RRConnection conn)
        {
            HandleDuelAccept(conn);
            return _duelRuntime.IsInDuel(conn.LoginName);
        }

        public bool DeclineDuel(RRConnection conn)
        {
            var hadDuel = _duelRuntime.IsInDuel(conn.LoginName);
            HandleDuelDecline(conn);
            return hadDuel;
        }

        public string GetDuelStatusFor(string loginName)
        {
            var d = _duelRuntime.GetDuel(loginName);
            if (d == null) return "No active duel.";
            string other = string.Equals(d.ChallengerLogin, loginName, StringComparison.OrdinalIgnoreCase)
                ? d.TargetLogin : d.ChallengerLogin;
            return $"{d.State} vs {other}";
        }

        private void HandleDuelAccept(RRConnection conn)
        {
            var duel = _duelRuntime.TryAccept(conn.LoginName, _combatTick);
            if (duel == null) { Debug.LogError($"[PVP-DUEL] No pending duel for {conn.LoginName}"); return; }

            var challenger = FindConnectionByLogin(duel.ChallengerLogin);
            var target = FindConnectionByLogin(duel.TargetLogin);

            if (challenger != null)
                SendToClient(challenger, PVPPackets.BuildDuelStatus(
                    PVPPackets.DuelStatusType.Accepted, duel.TargetCharSqlId, 0, 0));
            if (target != null)
                SendToClient(target, PVPPackets.BuildDuelStatus(
                    PVPPackets.DuelStatusType.Accepted, duel.ChallengerCharSqlId, 0, 0));

            _pendingDuelActivations.Add((
                checked((ulong)_combatTick + (ulong)Gameplay.DuelRuntime.DuelInfo.CountdownSec * SimulationClock.TicksPerSecond),
                duel));
            Debug.LogError($"[PVP-DUEL] Activation scheduled for {duel.ChallengerLogin} vs {duel.TargetLogin} in {Gameplay.DuelRuntime.DuelInfo.CountdownSec}s");
        }

        private void HandleDuelDecline(RRConnection conn)
        {
            var duel = _duelRuntime.TryDecline(conn.LoginName, _combatTick);
            if (duel == null) { Debug.LogError($"[PVP-DUEL] No pending duel for {conn.LoginName}"); return; }

            var challenger = FindConnectionByLogin(duel.ChallengerLogin);
            if (challenger != null)
                SendToClient(challenger, PVPPackets.BuildDuelStatus(
                    PVPPackets.DuelStatusType.Declined, duel.TargetCharSqlId, 0, 0));
        }

        private readonly List<(ulong dueTick, Gameplay.DuelRuntime.DuelInfo duel)> _pendingDuelActivations
            = new List<(ulong, Gameplay.DuelRuntime.DuelInfo)>();

        private class RemotePeerActionState
        {
            public bool ActionActive;
            public uint LastQueuedTargetActionTick;
            public bool AwaitingOwnerMovement;
            public bool BehaviorInitOwnsFirstMoverAdmission;
            public bool ClientControlEnabled;
            public bool FollowingClient;
            public bool FollowClientGrantQueued;
            public byte BehaviorGeneration;
            public bool CurrentActionInterruptLocked;
            public byte ActionOpcode;
            public byte ActionSessionId;
            public bool HasSourceUseTargetSession;
            public byte SourceUseTargetSessionId;
            public uint ActionTerminationTick;
            public bool PendingActionActive;
            public byte PendingActionOpcode;
            public byte PendingActionSessionId;
            public ushort PendingTargetEntityId;
        }

        private readonly Dictionary<string, RemotePeerActionState> _remotePeerActionStates
            = new Dictionary<string, RemotePeerActionState>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _remotePeerActionStateOrder = new List<string>();
        private int _moveSummaryOwnerMoves;
        private int _moveSummaryOwnerRecords;
        private int _moveSummaryOwnerMaxBatch;
        private int _moveSummaryPeerRelays;
        private int _moveSummaryPeerRecords;
        private int _moveSummaryPeerMaxBatch;
        private int _moveSummarySkippedRuntime;
        private int _moveSummarySkippedNoBehavior;
        private int _moveSummaryPostActionHandoffs;
        private int _moveSummaryTargetActionApproachMoves;
        private int _moveSummaryTargetActionApproachRecords;
        private int _moveSummarySkippedNotSpawned;
        private uint _moveSummaryMaxPostActionWaitTicks;
        private uint _lastMoveSummaryTick;

        private static string RemoteBehaviorSessionKey(RRConnection viewer, RRConnection source)
        {
            return $"{viewer.LoginName}\u001f{source.LoginName}";
        }

        private bool IsSameMovementRuntime(RRConnection viewer, RRConnection source)
        {
            if (viewer == null || source == null)
                return false;

            string viewerKey = GetInstanceZoneKey(viewer) ?? "";
            string sourceKey = GetInstanceZoneKey(source) ?? "";
            if (!string.IsNullOrWhiteSpace(viewerKey) && !string.IsNullOrWhiteSpace(sourceKey))
                return string.Equals(viewerKey, sourceKey, StringComparison.OrdinalIgnoreCase);

            return string.Equals(viewer.CurrentZoneGcType ?? "", source.CurrentZoneGcType ?? "", StringComparison.OrdinalIgnoreCase)
                && viewer.InstanceId == source.InstanceId;
        }

        private bool TryResolveRemoteBehaviorForViewer(RRConnection viewer, RRConnection source, out ushort remoteBehaviorId)
        {
            remoteBehaviorId = 0;
            if (viewer == null || source == null || string.IsNullOrWhiteSpace(viewer.LoginName) || string.IsNullOrWhiteSpace(source.LoginName))
                return false;
            return _remoteBehaviorIds.TryGetValue(viewer.LoginName, out var playerMap)
                && playerMap.TryGetValue(source.LoginName, out remoteBehaviorId)
                && remoteBehaviorId != 0;
        }

        private RemotePeerActionState GetRemotePeerActionState(RRConnection viewer, RRConnection source, bool create)
        {
            if (viewer == null || source == null || string.IsNullOrWhiteSpace(viewer.LoginName) || string.IsNullOrWhiteSpace(source.LoginName))
                return null;
            string key = RemoteBehaviorSessionKey(viewer, source);
            if (_remotePeerActionStates.TryGetValue(key, out var state))
                return state;
            if (!create)
                return null;
            state = new RemotePeerActionState();
            _remotePeerActionStates[key] = state;
            _remotePeerActionStateOrder.Add(key);
            return state;
        }

        private bool HasActivePeerAction(RRConnection viewer, RRConnection source)
        {
            return GetRemotePeerActionState(viewer, source, false)?.ActionActive == true;
        }

        private bool HasAnyActivePeerAction(RRConnection source)
        {
            if (source == null || string.IsNullOrWhiteSpace(source.LoginName))
                return false;
            foreach (RRConnection viewer in GetConnectionInsertionOrderSnapshot())
            {
                if (viewer == null || viewer == source)
                    continue;
                var state = GetRemotePeerActionState(viewer, source, false);
                if (state != null && (state.ActionActive || state.PendingActionActive || state.AwaitingOwnerMovement))
                    return true;
            }
            return false;
        }

        private static bool IsClientDrivenTargetApproachActive(RRConnection source)
        {
            return source?.UseTargetMovingActive == true && source.UseTargetMovingClientDriven;
        }

        private void RefreshRemoteActionRelayCache(RRConnection source)
        {
            if (source != null)
                source.RemoteActionRelayed = HasAnyActivePeerAction(source);
        }

        private static int CountPeerFollowClientRecordsAtActionAdmission(
            RRConnection source,
            IReadOnlyList<byte[]> messages,
            int actionMessageIndex,
            ushort remoteBehaviorId)
        {
            if (source == null)
                return 0;
            int records = source.UnitFollowClientMovementSamples.Count;
            if (messages == null || actionMessageIndex <= 0 || remoteBehaviorId == 0)
                return records;
            int messageLimit = Math.Min(actionMessageIndex, messages.Count);
            for (int messageIndex = 0; messageIndex < messageLimit; messageIndex++)
                records = checked(records + MessageQueue.CountUnitMoverRecords(messages[messageIndex], remoteBehaviorId));
            return records;
        }

        private int CountPendingPeerUnitFollowClientRecords(RRConnection viewer, RRConnection source)
        {
            if (viewer == null || source == null
                || !TryResolveRemoteBehaviorForViewer(viewer, source, out ushort remoteBehaviorId))
                return 0;
            return viewer.MessageQueue.CountPendingUnitMoverRecords(remoteBehaviorId);
        }

        private void MarkPeerActionStarted(
            RRConnection viewer,
            RRConnection source,
            ushort targetEntityId,
            byte actionOpcode = 0,
            byte actionSessionId = 0,
            int usePositionFollowClientRecords = 0,
            bool currentActionInterruptLocked = false)
        {
            var state = GetRemotePeerActionState(viewer, source, true);
            if (state == null)
                return;
            bool preserveUsePositionFollowClient = actionOpcode == 0x51 && usePositionFollowClientRecords > 0;
            state.BehaviorGeneration = unchecked((byte)(state.BehaviorGeneration + 1));
            ClearPeerPendingAction(state);
            state.FollowClientGrantQueued = false;
            if (state.ActionActive && state.ClientControlEnabled)
                MarkPeerFollowingClient(viewer, source, true);
            if (!preserveUsePositionFollowClient)
                MarkPeerFollowingClient(viewer, source, false);
            state.ActionActive = true;
            state.CurrentActionInterruptLocked = currentActionInterruptLocked;
            state.AwaitingOwnerMovement = true;
            state.ActionOpcode = actionOpcode;
            state.ActionSessionId = actionSessionId;
            state.HasSourceUseTargetSession = actionOpcode == 0x50;
            state.SourceUseTargetSessionId = actionOpcode == 0x50 ? actionSessionId : (byte)0;
            state.ActionTerminationTick = 0;
            state.LastQueuedTargetActionTick = _combatTick;
            RefreshRemoteActionRelayCache(source);
            if (actionOpcode == 0x51)
                Debug.LogError($"[PEER-USE-POSITION-FOLLOWCLIENT] viewer={viewer.LoginName} source={source.LoginName} queuedRecords={usePositionFollowClientRecords} followingClient={state.FollowingClient} moverSession={GetRemoteBehaviorSession(viewer, source)} result={(preserveUsePositionFollowClient ? "preserved" : "stopped")} sourceFunction=UsePosition::start@0x00546F80->UsePosition::States@0x00547360 UnitBehavior+0x14c");
        }

        private void MarkPeerActionStopped(RRConnection viewer, RRConnection source, bool preserveAwaitingOwnerMovement = false)
        {
            var state = GetRemotePeerActionState(viewer, source, false);
            if (state == null)
                return;
            state.BehaviorGeneration = unchecked((byte)(state.BehaviorGeneration + 1));
            state.ActionActive = false;
            state.CurrentActionInterruptLocked = false;
            if (state.ClientControlEnabled)
                MarkPeerFollowingClient(viewer, source, true);
            ClearPeerPendingAction(state);
            if (!preserveAwaitingOwnerMovement)
            {
                state.AwaitingOwnerMovement = false;
                state.FollowClientGrantQueued = false;
            }
            state.ActionOpcode = 0;
            state.ActionSessionId = 0;
            state.HasSourceUseTargetSession = false;
            state.SourceUseTargetSessionId = 0;
            state.ActionTerminationTick = 0;
            RefreshRemoteActionRelayCache(source);
        }

        private static void ClearPeerPendingAction(RemotePeerActionState state)
        {
            if (state == null)
                return;
            state.PendingActionActive = false;
            state.PendingActionOpcode = 0;
            state.PendingActionSessionId = 0;
            state.PendingTargetEntityId = 0;
        }

        private bool PromotePeerPendingAction(RRConnection viewer, RRConnection source, RemotePeerActionState state)
        {
            if (state?.PendingActionActive != true)
                return false;
            byte pendingActionOpcode = state.PendingActionOpcode;
            byte pendingActionSessionId = state.PendingActionSessionId;
            ushort pendingTargetEntityId = state.PendingTargetEntityId;
            int usePositionFollowClientRecords = pendingActionOpcode == 0x51 && source != null
                ? source.UnitFollowClientMovementSamples.Count
                : 0;
            bool preserveUsePositionFollowClient = pendingActionOpcode == 0x51 && usePositionFollowClientRecords > 0;
            if (state.ClientControlEnabled)
                MarkPeerFollowingClient(viewer, source, true);
            if (!preserveUsePositionFollowClient)
                MarkPeerFollowingClient(viewer, source, false);
            state.ActionActive = true;
            state.CurrentActionInterruptLocked = false;
            state.AwaitingOwnerMovement = true;
            state.ActionOpcode = pendingActionOpcode;
            state.ActionSessionId = pendingActionSessionId;
            byte sourceUseTargetSessionId = 0;
            state.HasSourceUseTargetSession = pendingActionOpcode == 0x50
                && TryResolvePromotedPeerUseTargetSourceSession(source, out sourceUseTargetSessionId);
            state.SourceUseTargetSessionId = state.HasSourceUseTargetSession
                ? sourceUseTargetSessionId
                : (byte)0;
            state.ActionTerminationTick = 0;
            ClearPeerPendingAction(state);
            if (source != null && ResolveCanonicalUnitFollowClientViewer(source) == viewer)
            {
                source.UnitFollowClientWriterStateInitialized = true;
                source.UnitFollowClientActionActive = true;
                source.UnitFollowClientFollowingClient = state.FollowingClient;
            }
            if (pendingActionOpcode == 0x51)
                Debug.LogError($"[PEER-USE-POSITION-FOLLOWCLIENT] viewer={viewer.LoginName} source={source.LoginName} queuedRecords={usePositionFollowClientRecords} followingClient={state.FollowingClient} moverSession={GetRemoteBehaviorSession(viewer, source)} session={pendingActionSessionId} target={pendingTargetEntityId} result={(preserveUsePositionFollowClient ? "preserved-pending-promotion" : "stopped-pending-promotion")} sourceFunction=Behavior::update@0x005154B0->Behavior::startAction@0x00515CF0->UsePosition::start@0x00546F80");
            return true;
        }

        private bool StopPeerUsePositionFollowingClientAtTermination(
            RRConnection viewer,
            RRConnection source,
            RemotePeerActionState state,
            uint simulationTick)
        {
            if (viewer == null
                || source == null
                || state?.ActionActive != true
                || state.ActionOpcode != 0x51
                || !state.FollowingClient)
                return false;
            byte moverSessionBefore = GetRemoteBehaviorSession(viewer, source);
            if (!MarkPeerFollowingClient(viewer, source, false))
                return false;
            if (ResolveCanonicalUnitFollowClientViewer(source) == viewer)
            {
                source.UnitFollowClientWriterStateInitialized = true;
                source.UnitFollowClientActionActive = true;
                source.UnitFollowClientFollowingClient = false;
            }
            Debug.LogError($"[PEER-USE-POSITION-FOLLOWCLIENT] viewer={viewer.LoginName} source={source.LoginName} followingClient=false moverSessionBefore={moverSessionBefore} moverSessionAfter={GetRemoteBehaviorSession(viewer, source)} session={state.ActionSessionId} tick={simulationTick} result=stopped-at-termination sourceFunction=UsePosition::States@0x00547360+0x396->UnitBehavior::StopMoving@0x005202D0");
            return true;
        }

        private static bool TryResolvePromotedPeerUseTargetSourceSession(RRConnection source, out byte sourceSessionId)
        {
            sourceSessionId = 0;
            if (source == null)
                return false;
            if (source.HasActiveUseTarget)
            {
                sourceSessionId = source.ActiveUseTargetSessionId;
                return true;
            }
            if (!source.HasPendingUseTargetAction)
                return false;
            sourceSessionId = source.PendingUseTargetSessionId;
            return true;
        }

        private void ArmPeerUsePositionTermination(RRConnection source, byte actionSessionId, uint terminationTick)
        {
            if (source == null || string.IsNullOrWhiteSpace(source.LoginName))
                return;
            foreach (RRConnection viewer in GetConnectionInsertionOrderSnapshot())
            {
                if (viewer == null || viewer == source || !IsSameMovementRuntime(viewer, source))
                    continue;
                var state = GetRemotePeerActionState(viewer, source, false);
                if (state?.ActionActive != true || state.ActionOpcode != 0x51 || state.ActionSessionId != actionSessionId)
                    continue;
                state.ActionTerminationTick = terminationTick;
            }
        }

        private void MarkPeerUsePositionTerminated(RRConnection source, byte actionSessionId, uint simulationTick)
        {
            if (source == null || string.IsNullOrWhiteSpace(source.LoginName))
                return;
            int terminated = 0;
            foreach (RRConnection viewer in GetConnectionInsertionOrderSnapshot())
            {
                if (viewer == null || viewer == source || !IsSameMovementRuntime(viewer, source))
                    continue;
                var state = GetRemotePeerActionState(viewer, source, false);
                if (state?.ActionActive != true
                    || state.ActionOpcode != 0x51
                    || state.ActionSessionId != actionSessionId
                    || state.ActionTerminationTick == 0
                    || simulationTick < state.ActionTerminationTick)
                    continue;
                byte moverSessionBeforeTermination = GetRemoteBehaviorSession(viewer, source);
                bool followingClientBeforeTermination = state.FollowingClient;
                StopPeerUsePositionFollowingClientAtTermination(viewer, source, state, simulationTick);
                if (!PromotePeerPendingAction(viewer, source, state))
                {
                    bool restoreFollowClientImmediately = state.ClientControlEnabled && !state.FollowingClient;
                    if (!TryResolveRemoteBehaviorForViewer(viewer, source, out ushort remoteBehaviorId)
                        || !QueuePeerActionStopForMovementHandoff(
                            viewer,
                            source,
                            remoteBehaviorId,
                            "MP-ACTION-STOP-use-position-terminate"))
                    {
                        state.ActionTerminationTick = 0;
                        state.AwaitingOwnerMovement = true;
                        state.FollowingClient = followingClientBeforeTermination;
                        SetRemoteBehaviorSession(viewer, source, moverSessionBeforeTermination);
                        RefreshRemoteActionRelayCache(source);
                        continue;
                    }
                    if (restoreFollowClientImmediately)
                    {
                        if (!QueueRemoteFollowClientGrant(
                            viewer,
                            source,
                            remoteBehaviorId,
                            "MP-CONTROL-use-position-terminate"))
                        {
                            state.ActionTerminationTick = 0;
                            state.AwaitingOwnerMovement = true;
                            RefreshRemoteActionRelayCache(source);
                            continue;
                        }
                    }
                }
                terminated++;
            }
            RefreshRemoteActionRelayCache(source);
            if (terminated > 0 && VerboseMovementEvidence)
                Debug.LogError($"[MP-ACTION-TERMINATE] source={source.LoginName} action=0x51 session={actionSessionId} peers={terminated} tick={simulationTick} sourceFunction=Behavior::terminate@0x00515CC0");
        }

        private void AdvancePeerUsePositionTerminations(uint simulationTick)
        {
            foreach (RRConnection source in GetConnectionInsertionOrderSnapshot())
            {
                if (source == null || string.IsNullOrWhiteSpace(source.LoginName))
                    continue;
                while (true)
                {
                    bool found = false;
                    byte actionSessionId = 0;
                    foreach (RRConnection viewer in GetConnectionInsertionOrderSnapshot())
                    {
                        if (viewer == null || viewer == source || !IsSameMovementRuntime(viewer, source))
                            continue;
                        RemotePeerActionState state = GetRemotePeerActionState(viewer, source, false);
                        if (state?.ActionActive != true
                            || state.ActionOpcode != 0x51
                            || state.ActionTerminationTick == 0
                            || simulationTick < state.ActionTerminationTick)
                            continue;
                        actionSessionId = state.ActionSessionId;
                        found = true;
                        break;
                    }
                    if (!found)
                        break;
                    MarkPeerUsePositionTerminated(source, actionSessionId, simulationTick);
                }
            }
        }

        private void MarkPeerUseTargetTerminated(RRConnection source, byte actionSessionId, uint simulationTick)
        {
            if (source == null || string.IsNullOrWhiteSpace(source.LoginName))
                return;
            int terminated = 0;
            foreach (RRConnection viewer in GetConnectionInsertionOrderSnapshot())
            {
                if (viewer == null || viewer == source || !IsSameMovementRuntime(viewer, source))
                    continue;
                var state = GetRemotePeerActionState(viewer, source, false);
                if (state?.ActionActive != true
                    || state.ActionOpcode != 0x50
                    || !state.HasSourceUseTargetSession
                    || state.SourceUseTargetSessionId != actionSessionId)
                    continue;
                if (!PromotePeerPendingAction(viewer, source, state))
                {
                    state.ActionActive = false;
                    state.CurrentActionInterruptLocked = false;
                    if (state.ClientControlEnabled)
                        MarkPeerFollowingClient(viewer, source, true);
                    state.AwaitingOwnerMovement = true;
                    state.ActionOpcode = 0;
                    state.ActionSessionId = 0;
                    state.HasSourceUseTargetSession = false;
                    state.SourceUseTargetSessionId = 0;
                    state.ActionTerminationTick = 0;
                }
                terminated++;
            }
            RefreshRemoteActionRelayCache(source);
            if (terminated > 0 && VerboseMovementEvidence)
                Debug.LogError($"[MP-ACTION-TERMINATE] source={source.LoginName} action=0x50 session={actionSessionId} peers={terminated} tick={simulationTick} sourceFunction=Behavior::terminate@0x00515CC0");
        }

        private void ArmPeerUseTargetTermination(RRConnection source, byte actionSessionId, uint terminationTick)
        {
            if (source == null || string.IsNullOrWhiteSpace(source.LoginName))
                return;
            foreach (RRConnection viewer in GetConnectionInsertionOrderSnapshot())
            {
                if (viewer == null || viewer == source || !IsSameMovementRuntime(viewer, source))
                    continue;
                RemotePeerActionState state = GetRemotePeerActionState(viewer, source, false);
                if (state?.ActionActive != true
                    || state.ActionOpcode != 0x50
                    || !state.HasSourceUseTargetSession
                    || state.SourceUseTargetSessionId != actionSessionId)
                    continue;
                state.ActionTerminationTick = terminationTick;
            }
        }

        private void AdvancePeerUseTargetTerminations(uint simulationTick)
        {
            foreach (RRConnection source in GetConnectionInsertionOrderSnapshot())
            {
                if (source == null || string.IsNullOrWhiteSpace(source.LoginName))
                    continue;
                while (true)
                {
                    bool found = false;
                    byte sourceActionSessionId = 0;
                    foreach (RRConnection viewer in GetConnectionInsertionOrderSnapshot())
                    {
                        if (viewer == null || viewer == source || !IsSameMovementRuntime(viewer, source))
                            continue;
                        RemotePeerActionState state = GetRemotePeerActionState(viewer, source, false);
                        if (state?.ActionActive != true
                            || state.ActionOpcode != 0x50
                            || !state.HasSourceUseTargetSession
                            || state.ActionTerminationTick == 0
                            || simulationTick < state.ActionTerminationTick)
                            continue;
                        sourceActionSessionId = state.SourceUseTargetSessionId;
                        found = true;
                        break;
                    }
                    if (!found)
                        break;
                    MarkPeerUseTargetTerminated(source, sourceActionSessionId, simulationTick);
                }
            }
        }

        private bool MarkPeerWarpToControlCycle(RRConnection viewer, RRConnection source)
        {
            var state = GetRemotePeerActionState(viewer, source, false);
            if (state == null)
                return false;
            if (state.ActionActive && state.ClientControlEnabled)
                MarkPeerFollowingClient(viewer, source, true);
            MarkPeerFollowingClient(viewer, source, false);
            state.BehaviorGeneration = unchecked((byte)(state.BehaviorGeneration + 1));
            state.ActionActive = false;
            state.CurrentActionInterruptLocked = false;
            state.AwaitingOwnerMovement = false;
            state.ActionOpcode = 0;
            state.ActionSessionId = 0;
            state.HasSourceUseTargetSession = false;
            state.SourceUseTargetSessionId = 0;
            state.ActionTerminationTick = 0;
            state.FollowClientGrantQueued = false;
            ClearPeerPendingAction(state);
            if (state.ClientControlEnabled)
                MarkPeerFollowingClient(viewer, source, true);
            RefreshRemoteActionRelayCache(source);
            return true;
        }

        private void MarkPeerOwnerMovementReflected(RRConnection viewer, RRConnection source)
        {
            var state = GetRemotePeerActionState(viewer, source, false);
            if (state == null)
                return;
            state.ActionActive = false;
            state.CurrentActionInterruptLocked = false;
            state.AwaitingOwnerMovement = false;
            state.ActionOpcode = 0;
            state.ActionSessionId = 0;
            state.HasSourceUseTargetSession = false;
            state.SourceUseTargetSessionId = 0;
            state.ActionTerminationTick = 0;
            state.FollowClientGrantQueued = false;
            ClearPeerPendingAction(state);
            RefreshRemoteActionRelayCache(source);
        }

        private void MarkPeerBehaviorInitialized(RRConnection viewer, RRConnection source)
        {
            var state = GetRemotePeerActionState(viewer, source, true);
            if (state != null)
            {
                state.BehaviorInitOwnsFirstMoverAdmission = true;
                state.ClientControlEnabled = true;
                state.FollowingClient = true;
                state.BehaviorGeneration = 0;
                state.ActionActive = false;
                state.CurrentActionInterruptLocked = false;
                state.AwaitingOwnerMovement = false;
                state.ActionOpcode = 0;
                state.ActionSessionId = 0;
                state.HasSourceUseTargetSession = false;
                state.SourceUseTargetSessionId = 0;
                state.ActionTerminationTick = 0;
                state.FollowClientGrantQueued = false;
                ClearPeerPendingAction(state);
            }
        }

        private bool MarkPeerClientControl(RRConnection viewer, RRConnection source, bool enabled)
        {
            var state = GetRemotePeerActionState(viewer, source, true);
            if (state == null || state.ClientControlEnabled == enabled)
                return false;
            state.ClientControlEnabled = enabled;
            if (!enabled)
                state.FollowClientGrantQueued = false;
            if (enabled && !state.ActionActive)
                MarkPeerFollowingClient(viewer, source, true);
            return true;
        }

        private bool MarkPeerFollowingClient(RRConnection viewer, RRConnection source, bool following)
        {
            var state = GetRemotePeerActionState(viewer, source, true);
            if (state == null)
                return false;
            if (state.FollowingClient == following)
                return false;
            if (!following)
                AdvanceRemoteBehaviorSession(viewer, source, state);
            state.FollowingClient = following;
            return true;
        }

        private void AdvanceRemoteBehaviorSession(
            RRConnection viewer,
            RRConnection source,
            RemotePeerActionState state = null)
        {
            SetRemoteBehaviorSession(viewer, source, unchecked((byte)(GetRemoteBehaviorSession(viewer, source) + 1)));
            state ??= GetRemotePeerActionState(viewer, source, true);
            if (state != null)
                state.BehaviorInitOwnsFirstMoverAdmission = false;
        }

        private void MarkPeerUsePositionStoppedFollowingClient(RRConnection source, uint simulationTick)
        {
            if (source == null)
                return;
            foreach (RRConnection viewer in GetConnectionInsertionOrderSnapshot())
            {
                if (viewer == null || viewer == source || !IsSameMovementRuntime(viewer, source))
                    continue;
                var state = GetRemotePeerActionState(viewer, source, false);
                if (state?.ActionActive != true
                    || state.ActionOpcode != 0x51
                    || state.ActionSessionId != source.UsePositionActionSessionId)
                    continue;
                byte moverSessionBefore = GetRemoteBehaviorSession(viewer, source);
                if (!MarkPeerFollowingClient(viewer, source, false))
                    continue;
                if (ResolveCanonicalUnitFollowClientViewer(source) == viewer)
                {
                    source.UnitFollowClientWriterStateInitialized = true;
                    source.UnitFollowClientActionActive = true;
                    source.UnitFollowClientFollowingClient = false;
                }
                Debug.LogError($"[PEER-USE-POSITION-FOLLOWCLIENT] viewer={viewer.LoginName} source={source.LoginName} queuedRecords=0 followingClient=false moverSessionBefore={moverSessionBefore} moverSessionAfter={GetRemoteBehaviorSession(viewer, source)} session={state.ActionSessionId} tick={simulationTick} result=stopped-by-client-control sourceFunction=ClientUnitBehavior::SuspendClientMovement@0x00518E10->UnitBehavior::StopFollowingClient@0x005203A0");
            }
            RefreshRemoteActionRelayCache(source);
        }

        private void MaybeLogMoveCadenceSummary()
        {
            if (unchecked(_combatTick - _lastMoveSummaryTick) < 150)
                return;
            if (_moveSummaryOwnerMoves == 0 && _moveSummaryPeerRelays == 0 && _moveSummaryPostActionHandoffs == 0)
                return;

            Debug.LogError($"[MOVE-CADENCE-SUMMARY] ownerMoves={_moveSummaryOwnerMoves} ownerRecords={_moveSummaryOwnerRecords} ownerMaxBatch={_moveSummaryOwnerMaxBatch} peerRelays={_moveSummaryPeerRelays} peerRecords={_moveSummaryPeerRecords} peerMaxBatch={_moveSummaryPeerMaxBatch} postActionHandoffs={_moveSummaryPostActionHandoffs} targetActionApproachMoves={_moveSummaryTargetActionApproachMoves} targetActionApproachRecords={_moveSummaryTargetActionApproachRecords} maxPostActionWaitTicks={_moveSummaryMaxPostActionWaitTicks} skippedNotSpawned={_moveSummarySkippedNotSpawned} skippedRuntime={_moveSummarySkippedRuntime} skippedNoBehavior={_moveSummarySkippedNoBehavior} policy=owner-request-group-ordered-throughput actionBarrier=preserve-predecessor-movers actionApproach=owner-0x65-never-swallowed serverMoverBudgetPerFlush={CLIENT_ENTITY_MOVEMENT_RECORD_BUDGET_PER_FLUSH}");
            _moveSummaryOwnerMoves = 0;
            _moveSummaryOwnerRecords = 0;
            _moveSummaryOwnerMaxBatch = 0;
            _moveSummaryPeerRelays = 0;
            _moveSummaryPeerRecords = 0;
            _moveSummaryPeerMaxBatch = 0;
            _moveSummaryPostActionHandoffs = 0;
            _moveSummaryTargetActionApproachMoves = 0;
            _moveSummaryTargetActionApproachRecords = 0;
            _moveSummarySkippedNotSpawned = 0;
            _moveSummarySkippedRuntime = 0;
            _moveSummarySkippedNoBehavior = 0;
            _moveSummaryMaxPostActionWaitTicks = 0;
            _lastMoveSummaryTick = _combatTick;
        }

        private byte GetRemoteBehaviorSession(RRConnection viewer, RRConnection source)
        {
            if (viewer == null || source == null || string.IsNullOrEmpty(viewer.LoginName) || string.IsNullOrEmpty(source.LoginName))
                return 0xFF;
            string key = RemoteBehaviorSessionKey(viewer, source);
            if (!_remoteSessionIds.TryGetValue(key, out byte sessionId))
            {
                sessionId = 0xFF;
                _remoteSessionIds[key] = sessionId;
                _remoteSessionOrder.Add(key);
            }
            return sessionId;
        }

        private void SetRemoteBehaviorSession(RRConnection viewer, RRConnection source, byte sessionId)
        {
            if (viewer == null || source == null || string.IsNullOrEmpty(viewer.LoginName) || string.IsNullOrEmpty(source.LoginName))
                return;
            string key = RemoteBehaviorSessionKey(viewer, source);
            if (!_remoteSessionIds.ContainsKey(key))
                _remoteSessionOrder.Add(key);
            _remoteSessionIds[key] = sessionId;
        }

        private static bool VerboseMovementEvidence => ServerDiagnostics.IsEnabled("verboseMovementLogging");

        private bool WriteRemotePlayerMovementSubupdate(RRConnection source, LEWriter writer, ushort remoteBehaviorId, byte relaySessionId, byte[] moveRecord)
        {
            writer.WriteByte(0x35);
            writer.WriteUInt16(remoteBehaviorId);
            writer.WriteByte(0x65);
            writer.WriteByte(relaySessionId);
            writer.WriteByte(0x01);
            writer.WriteBytes(moveRecord);
            return TryWriteRemoteAvatarEntitySynchInfo(source, writer, remoteBehaviorId, 0x65, "MP-MOVE");
        }

        private bool QueueRemotePlayerMovement(RRConnection source, RRConnection viewer, ushort remoteBehaviorId, byte sessionId, byte moveCount, byte[] moveData, string mode)
        {
            if (source == null || viewer == null || remoteBehaviorId == 0 || moveCount == 0 || moveData == null || moveData.Length != moveCount * UnitMoverUpdateRecordSize)
                return false;

            byte ownerSessionId = sessionId;
            var peerState = GetRemotePeerActionState(viewer, source, false);
            if (peerState == null)
                return false;
            byte relaySessionId = peerState.BehaviorInitOwnsFirstMoverAdmission
                ? ownerSessionId
                : GetRemoteBehaviorSession(viewer, source);
            if (peerState.BehaviorInitOwnsFirstMoverAdmission)
                SetRemoteBehaviorSession(viewer, source, relaySessionId);
            var subupdates = new List<byte[]>(moveCount);
            for (int offset = 0; offset < moveData.Length; offset += UnitMoverUpdateRecordSize)
            {
                var moveRecord = new byte[UnitMoverUpdateRecordSize];
                Buffer.BlockCopy(moveData, offset, moveRecord, 0, UnitMoverUpdateRecordSize);

                var remoteMoveMessage = new LEWriter();
                if (!WriteRemotePlayerMovementSubupdate(source, remoteMoveMessage, remoteBehaviorId, relaySessionId, moveRecord))
                    return false;
                subupdates.Add(remoteMoveMessage.ToArray());
            }

            viewer.MessageQueue.EnqueueNativeMoverBatch(subupdates, remoteBehaviorId, 0);
            peerState.BehaviorInitOwnsFirstMoverAdmission = false;

            _moveSummaryPeerRecords += moveCount;
            if (moveCount > _moveSummaryPeerMaxBatch)
                _moveSummaryPeerMaxBatch = moveCount;
            if (VerboseMovementEvidence)
            {
                DungeonRunners.Core.RuntimeEvidence.LogForPlayerPair(source.LoginName, viewer.LoginName, "[MP-MOVE]",
                    $"remoteBehavior={remoteBehaviorId} session=0x{relaySessionId:X2} ownerSession=0x{ownerSessionId:X2} clientControl={peerState.ClientControlEnabled} followingClient={peerState.FollowingClient} peerUpdates={subupdates.Count} peerRecords={moveCount} ownerRequestRecords={source.LastRawMoveCount} initialDelayFlushes=0 mode={mode} policy=owner-request-group-native-admission serverMoverBudgetPerFlush={CLIENT_ENTITY_MOVEMENT_RECORD_BUDGET_PER_FLUSH} hp={GetPlayerState(source.ConnId.ToString())?.EntitySynchInfoHP ?? 0} srcFixed=({source.PlayerPosFixedX},{source.PlayerPosFixedY})");
            }
            return true;
        }

        private void BroadcastPlayerMovement(RRConnection conn, byte sessionId, byte moveCount, byte[] rawMoveData, bool fromTargetBridge = false)
        {
            uint avatarId = GetPlayerAvatarId(conn.LoginName);
            if (avatarId == 0) return;

            if (moveCount == 0 || rawMoveData == null || rawMoveData.Length == 0) return;
            _moveSummaryOwnerMoves++;
            _moveSummaryOwnerRecords += moveCount;
            if (moveCount > _moveSummaryOwnerMaxBatch)
                _moveSummaryOwnerMaxBatch = moveCount;

            conn.LastRawMoveData = rawMoveData;
            conn.LastRawMoveCount = moveCount;

            byte relayMoveCount = moveCount;
            byte[] relayData = rawMoveData;
            if (!TryNormalizePeerUnitMoverUpdateData(relayMoveCount, relayData, out relayMoveCount, out relayData)) return;
            bool targetApproachRelay = IsClientDrivenTargetApproachActive(conn) || fromTargetBridge;

            foreach (RRConnection other in GetConnectionInsertionOrderSnapshot())
            {
                if (other == conn) continue;
                if (!other.IsSpawned)
                {
                    _moveSummarySkippedNotSpawned++;
                    continue;
                }
                if (_pendingRoomClientTokens.ContainsKey(other.ConnId))
                {
                    _moveSummarySkippedNotSpawned++;
                    continue;
                }
                if (!IsSameMovementRuntime(other, conn))
                {
                    _moveSummarySkippedRuntime++;
                    continue;
                }

                if (!TryResolveRemoteBehaviorForViewer(other, conn, out ushort remoteBehaviorId))
                {
                    _moveSummarySkippedNoBehavior++;
                    continue;
                }

                var peerActionState = GetRemotePeerActionState(other, conn, false);
                bool actionHandoff = peerActionState?.ActionActive == true || peerActionState?.AwaitingOwnerMovement == true;
                if (peerActionState?.ActionActive == true)
                {
                    if (QueuePeerActionStopForMovementHandoff(other, conn, remoteBehaviorId, "MP-ACTION-STOP-client-move-before-relay"))
                    {
                        _moveSummaryPostActionHandoffs++;
                    }
                    else
                        continue;
                }
                if (actionHandoff)
                {
                    if (peerActionState?.FollowClientGrantQueued != true)
                    {
                        if (!QueueRemoteFollowClientGrant(other, conn, remoteBehaviorId, "MP-CONTROL-post-action-handoff"))
                            continue;
                    }
                }

                string movementMode = actionHandoff ? "target-action-handoff" : targetApproachRelay ? "target-approach" : "owner-input";
                byte relaySessionId = actionHandoff
                    ? GetRemoteBehaviorSession(other, conn)
                    : sessionId;
                if (QueueRemotePlayerMovement(conn, other, remoteBehaviorId, relaySessionId, relayMoveCount, relayData, movementMode))
                {
                    _moveSummaryPeerRelays++;
                    if (actionHandoff)
                        MarkPeerOwnerMovementReflected(other, conn);
                }
            }
            MaybeLogMoveCadenceSummary();
        }

        private void ClearRemoteActionRelayState(RRConnection conn)
        {
            if (conn == null) return;
            if (!string.IsNullOrWhiteSpace(conn.LoginName))
            {
                string viewerPrefix = $"{conn.LoginName}\u001f";
                string sourceSuffix = $"\u001f{conn.LoginName}";
                for (int keyIndex = _remotePeerActionStateOrder.Count - 1; keyIndex >= 0; keyIndex--)
                {
                    string key = _remotePeerActionStateOrder[keyIndex];
                    if (key.StartsWith(viewerPrefix, StringComparison.OrdinalIgnoreCase)
                        || key.EndsWith(sourceSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        _remotePeerActionStates.Remove(key);
                        _remotePeerActionStateOrder.RemoveAt(keyIndex);
                    }
                }
            }
            conn.RemoteActionRelayed = false;
        }

        private void StopRemoteActionBeforeTargetTracker(RRConnection conn)
        {
            if (conn == null || !HasAnyActivePeerAction(conn)) return;
            BroadcastRemoteActionStop(conn, "MP-ACTION-STOP-target-click", includeFollowClientReset: false);
        }

        private bool WriteRemoteFollowClientControlUpdate(RRConnection viewer, RRConnection source, LEWriter writer, ushort remoteBehaviorId, bool followClient)
        {
            if (viewer == null || source == null || writer == null || remoteBehaviorId == 0) return false;
            writer.WriteByte(0x35);
            writer.WriteUInt16(remoteBehaviorId);
            writer.WriteByte(0x64);
            writer.WriteByte(followClient ? (byte)0x01 : (byte)0x00);
            return TryWriteRemoteAvatarEntitySynchInfo(source, writer, remoteBehaviorId, 0x64, "MP-CONTROL");
        }

        private bool QueueRemoteFollowClientReset(RRConnection viewer, RRConnection source, ushort remoteBehaviorId)
        {
            if (viewer == null || source == null || remoteBehaviorId == 0) return false;

            var message = new LEWriter();
            if (!WriteRemoteFollowClientControlUpdate(viewer, source, message, remoteBehaviorId, false)
                || !WriteRemoteFollowClientControlUpdate(viewer, source, message, remoteBehaviorId, true))
                return false;
            viewer.MessageQueue.Enqueue(message.ToArray());
            MarkPeerClientControl(viewer, source, false);
            MarkPeerClientControl(viewer, source, true);
            if (VerboseMovementEvidence)
                Debug.LogError($"[MP-CONTROL] FollowClient reset viewer={viewer.LoginName} source={source.LoginName} behavior={remoteBehaviorId} sourceFunction=UnitBehavior::FollowClient@0x5202F0");
            return true;
        }

        private bool QueueRemoteFollowClientGrant(RRConnection viewer, RRConnection source, ushort remoteBehaviorId, string tag, int delayFlushes = 0)
        {
            if (viewer == null || source == null || remoteBehaviorId == 0) return false;

            var message = new LEWriter();
            if (!WriteRemoteFollowClientControlUpdate(viewer, source, message, remoteBehaviorId, true))
                return false;
            if (delayFlushes > 0)
                viewer.MessageQueue.EnqueueAfterFlushes(message.ToArray(), delayFlushes);
            else
                viewer.MessageQueue.Enqueue(message.ToArray());
            MarkPeerClientControl(viewer, source, true);
            var state = GetRemotePeerActionState(viewer, source, true);
            if (state != null)
                state.FollowClientGrantQueued = true;
            if (VerboseMovementEvidence)
                Debug.LogError($"[{tag}] FollowClient grant viewer={viewer.LoginName} source={source.LoginName} behavior={remoteBehaviorId} sourceFunction=UnitBehavior::FollowClient@0x5202F0");
            return true;
        }

        private void RelayComponentUpdateToPeers(RRConnection source, byte subMessage, byte[] payload, string tag, bool startsPeerAction = false)
        {
            if (source == null) return;

            int relayedTo = 0;
            foreach (RRConnection other in GetConnectionInsertionOrderSnapshot())
            {
                if (other == source) continue;
                if (!other.IsSpawned) continue;
                if (other.CurrentZoneGcType != source.CurrentZoneGcType) continue;
                if (other.InstanceId != source.InstanceId) continue;

                if (!_remoteBehaviorIds.TryGetValue(other.LoginName, out var playerMap)) continue;
                if (!playerMap.TryGetValue(source.LoginName, out ushort remoteBehaviorId)) continue;

                var message = new LEWriter();
                message.WriteByte(0x35);
                message.WriteUInt16(remoteBehaviorId);
                message.WriteByte(subMessage);
                if (payload != null && payload.Length > 0)
                    message.WriteBytes(payload);
                if (!TryWriteRemoteAvatarEntitySynchInfo(source, message, remoteBehaviorId, subMessage, tag))
                    continue;
                other.MessageQueue.Enqueue(message.ToArray());
                if (startsPeerAction)
                    MarkPeerActionStarted(other, source, 0, currentActionInterruptLocked: true);
                relayedTo++;
            }
            if (VerbosePacketLogging)
                Debug.LogError($"[{tag}] src={source.LoginName} sub=0x{subMessage:X2} payload={payload?.Length ?? 0}b relayedToPeers={relayedTo}");
        }

        private void BroadcastRemoteActionStop(RRConnection source, string tag, bool includeFollowClientReset = true)
        {
            if (source == null) return;

            int relayedTo = 0;
            foreach (RRConnection other in GetConnectionInsertionOrderSnapshot())
            {
                if (other == source) continue;
                if (!other.IsSpawned) continue;
                if (!IsSameMovementRuntime(other, source)) continue;
                if (!TryResolveRemoteBehaviorForViewer(other, source, out ushort remoteBehaviorId)) continue;

                bool queued = false;
                var state = GetRemotePeerActionState(other, source, false);
                if (state?.ActionActive == true)
                {
                    var message = new LEWriter();
                    message.WriteByte(0x35);
                    message.WriteUInt16(remoteBehaviorId);
                    message.WriteByte(0x05);
                    if (!TryWriteRemoteAvatarEntitySynchInfo(source, message, remoteBehaviorId, 0x05, tag))
                        continue;
                    other.MessageQueue.Enqueue(message.ToArray());
                    MarkPeerActionStopped(other, source);
                    queued = true;
                }
                if (includeFollowClientReset)
                    queued |= QueueRemoteFollowClientReset(other, source, remoteBehaviorId);
                if (queued)
                    relayedTo++;
            }

            RefreshRemoteActionRelayCache(source);
            if (VerboseMovementEvidence)
                Debug.LogError($"[{tag}] src={source.LoginName} relayedToPeers={relayedTo} followReset={includeFollowClientReset}");
        }

        private bool QueuePeerActionStopForMovementHandoff(RRConnection viewer, RRConnection source, ushort remoteBehaviorId, string tag)
        {
            var state = GetRemotePeerActionState(viewer, source, false);
            if (state?.ActionActive != true)
                return false;

            var message = new LEWriter();
            message.WriteByte(0x35);
            message.WriteUInt16(remoteBehaviorId);
            message.WriteByte(0x05);
            if (!TryWriteRemoteAvatarEntitySynchInfo(source, message, remoteBehaviorId, 0x05, tag))
                return false;

            viewer.MessageQueue.Enqueue(message.ToArray());
            uint delayTicks = _combatTick >= state.LastQueuedTargetActionTick ? _combatTick - state.LastQueuedTargetActionTick : 0;
            if (delayTicks > _moveSummaryMaxPostActionWaitTicks)
                _moveSummaryMaxPostActionWaitTicks = delayTicks;
            MarkPeerActionStopped(viewer, source, preserveAwaitingOwnerMovement: true);
            return true;
        }

        private void BroadcastRemoteActionStopForMovementHandoff(RRConnection source, string tag)
        {
            if (source == null) return;

            int relayedTo = 0;
            foreach (var other in GetConnectionInsertionOrderSnapshot())
            {
                if (other == source) continue;
                if (!other.IsSpawned) continue;
                if (!IsSameMovementRuntime(other, source)) continue;
                if (!TryResolveRemoteBehaviorForViewer(other, source, out ushort remoteBehaviorId)) continue;
                if (QueuePeerActionStopForMovementHandoff(other, source, remoteBehaviorId, tag))
                    relayedTo++;
            }

            RefreshRemoteActionRelayCache(source);
            if (VerboseMovementEvidence)
                Debug.LogError($"[{tag}] src={source.LoginName} relayedToPeers={relayedTo} handoff=action-stop-only");
        }

        private RRConnection ResolveAvatarTargetOwnerForClient(RRConnection viewer, ushort targetEntityId, out bool behaviorTarget)
        {
            behaviorTarget = false;
            if (targetEntityId == 0) return null;

            RRConnection targetOwner = FindConnectionByAvatarEntityId(targetEntityId);
            if (targetOwner != null)
                return targetOwner;

            if (viewer == null || string.IsNullOrEmpty(viewer.LoginName))
                return null;

            if (_remoteAvatarIds.TryGetValue(viewer.LoginName, out var avatarMap))
            {
                foreach (RRConnection candidate in GetConnectionInsertionOrderSnapshot())
                {
                    if (candidate == null || string.IsNullOrEmpty(candidate.LoginName)) continue;
                    if (!avatarMap.TryGetValue(candidate.LoginName, out ushort avatarId) || avatarId != targetEntityId) continue;
                    return candidate;
                }
            }

            if (_remoteBehaviorIds.TryGetValue(viewer.LoginName, out var behaviorMap))
            {
                foreach (RRConnection candidate in GetConnectionInsertionOrderSnapshot())
                {
                    if (candidate == null || string.IsNullOrEmpty(candidate.LoginName)) continue;
                    if (!behaviorMap.TryGetValue(candidate.LoginName, out ushort behaviorId) || behaviorId != targetEntityId) continue;
                    behaviorTarget = true;
                    return candidate;
                }
            }

            return null;
        }

        private ushort ResolvePeerActionTargetId(RRConnection source, RRConnection recipient, ushort targetEntityId)
        {
            RRConnection targetOwner = ResolveAvatarTargetOwnerForClient(source, targetEntityId, out bool behaviorTarget);
            if (targetOwner == null)
                return targetEntityId;

            if (behaviorTarget)
            {
                if (recipient.ConnId == targetOwner.ConnId)
                    return targetOwner.UnitBehaviorId != 0 && targetOwner.UnitBehaviorId <= ushort.MaxValue ? (ushort)targetOwner.UnitBehaviorId : targetOwner.BehaviorComponentId;
                if (_remoteBehaviorIds.TryGetValue(recipient.LoginName, out var behaviorMap)
                    && behaviorMap.TryGetValue(targetOwner.LoginName, out ushort remoteBehaviorId))
                    return remoteBehaviorId;
                return targetEntityId;
            }

            if (targetOwner.Avatar == null)
                return targetEntityId;
            ushort mappedTargetId = ResolveRemoteAvatarEntityId(recipient, targetOwner, (uint)targetOwner.Avatar.Id);
            return mappedTargetId != 0 ? mappedTargetId : targetEntityId;
        }

        private bool RelayTargetActionToPeers(RRConnection source, byte responseId, byte actionType, byte sessionId, ushort targetEntityId, string tag)
        {
            if (source == null) return false;
            if (actionType != 0x06) return false;

            if (HasAnyActivePeerAction(source))
                BroadcastRemoteActionStop(source, $"{tag}-previous", includeFollowClientReset: false);
            int relayedTo = 0;
            foreach (RRConnection other in GetConnectionInsertionOrderSnapshot())
            {
                if (other == source) continue;
                if (!other.IsSpawned) continue;
                if (!IsSameMovementRuntime(other, source)) continue;

                if (!TryResolveRemoteBehaviorForViewer(other, source, out ushort remoteBehaviorId)) continue;

                ushort peerTargetId = ResolvePeerActionTargetId(source, other, targetEntityId);
                var message = new LEWriter();
                if (!WriteRemoteFollowClientControlUpdate(other, source, message, remoteBehaviorId, false))
                    continue;
                message.WriteByte(0x35);
                message.WriteUInt16(remoteBehaviorId);
                message.WriteByte(0x04);
                message.WriteByte(actionType);
                message.WriteByte(0x00);
                message.WriteUInt16(peerTargetId);
                if (!TryWriteRemoteAvatarEntitySynchInfo(source, message, remoteBehaviorId, 0x04, tag))
                    continue;
                other.MessageQueue.EnqueueAfterFlushes(message.ToArray(), 1);
                MarkPeerClientControl(other, source, false);
                MarkPeerActionStarted(other, source, targetEntityId, currentActionInterruptLocked: true);
                relayedTo++;
            }

            RefreshRemoteActionRelayCache(source);
            if (VerbosePacketLogging)
                Debug.LogError($"[{tag}] src={source.LoginName} action=0x{actionType:X2} target={targetEntityId} relayedToPeers={relayedTo} peerAdmissionFlushes=1 policy=activate-owner-peer-component-phase");
            return relayedTo > 0;
        }

        private bool RelaySelfCastActionToPeers(RRConnection source, byte spellSessionId, byte slotId, string tag)
        {
            if (source == null) return false;

            int relayedTo = 0;
            foreach (RRConnection other in GetConnectionInsertionOrderSnapshot())
            {
                if (other == source) continue;
                if (!other.IsSpawned) continue;
                if (!IsSameMovementRuntime(other, source)) continue;
                if (!TryResolveRemoteBehaviorForViewer(other, source, out ushort remoteBehaviorId)) continue;

                var message = new LEWriter();
                message.WriteByte(0x35);
                message.WriteUInt16(remoteBehaviorId);
                message.WriteByte(0x04);
                message.WriteByte(0x52);
                message.WriteByte(spellSessionId);
                message.WriteByte(slotId);
                if (!TryWriteRemoteAvatarEntitySynchInfo(source, message, remoteBehaviorId, 0x04, tag))
                    continue;
                other.MessageQueue.Enqueue(message.ToArray());
                MarkPeerActionStarted(other, source, 0, 0x52, slotId, currentActionInterruptLocked: true);
                relayedTo++;
            }

            RefreshRemoteActionRelayCache(source);
            if (VerbosePacketLogging)
                Debug.LogError($"[{tag}] src={source.LoginName} action=0x52 spellSession={spellSessionId} slot={slotId} relayedToPeers={relayedTo}");
            return relayedTo > 0;
        }

        private bool RelayUseTargetActionToPeers(RRConnection source, byte responseId, byte manipulatorId, byte useFlags, ushort targetEntityId, string tag)
        {
            if (source == null) return false;

            int relayedTo = 0;
            foreach (RRConnection other in GetConnectionInsertionOrderSnapshot())
            {
                if (other == source) continue;
                if (!other.IsSpawned) continue;
                if (!IsSameMovementRuntime(other, source)) continue;

                if (!TryResolveRemoteBehaviorForViewer(other, source, out ushort remoteBehaviorId)) continue;

                ushort peerTargetId = ResolvePeerActionTargetId(source, other, targetEntityId);

                var message = new LEWriter();
                message.WriteByte(0x35);
                message.WriteUInt16(remoteBehaviorId);
                message.WriteByte(0x04);
                message.WriteByte(0x50);
                message.WriteByte(manipulatorId);
                message.WriteByte(useFlags);
                message.WriteUInt16(peerTargetId);
                if (!TryWriteRemoteAvatarEntitySynchInfo(source, message, remoteBehaviorId, 0x04, tag))
                    continue;
                other.MessageQueue.Enqueue(message.ToArray());
                relayedTo++;
            }

            RefreshRemoteActionRelayCache(source);
            if (VerbosePacketLogging)
                Debug.LogError($"[{tag}] src={source.LoginName} action=0x50 target={targetEntityId} relayedToPeers={relayedTo} phase=queued-await-client-entity-writer sourceFunction=Behavior::processUpdate@0x00515620 opcode=0x04->Behavior::doInterruptLocal@0x00515290");
            return relayedTo > 0;
        }

        private bool RelayPendingUseTargetActionToPeers(RRConnection source, byte manipulatorId, byte useFlags, ushort targetEntityId, string tag)
        {
            if (source == null) return false;

            int relayedTo = 0;
            int skippedWithoutState = 0;
            foreach (RRConnection other in GetConnectionInsertionOrderSnapshot())
            {
                if (other == source) continue;
                if (!other.IsSpawned) continue;
                if (!IsSameMovementRuntime(other, source)) continue;
                if (!TryResolveRemoteBehaviorForViewer(other, source, out ushort remoteBehaviorId)) continue;

                var state = GetRemotePeerActionState(other, source, false);
                if (state == null)
                {
                    skippedWithoutState++;
                    continue;
                }

                ushort peerTargetId = ResolvePeerActionTargetId(source, other, targetEntityId);
                var message = new LEWriter();
                message.WriteByte(0x35);
                message.WriteUInt16(remoteBehaviorId);
                message.WriteByte(0x01);
                message.WriteByte(state.BehaviorGeneration);
                message.WriteByte(0x50);
                message.WriteByte(manipulatorId);
                message.WriteByte(useFlags);
                message.WriteUInt16(peerTargetId);
                if (!TryWriteRemoteAvatarEntitySynchInfo(source, message, remoteBehaviorId, 0x01, tag))
                    continue;
                other.MessageQueue.Enqueue(message.ToArray());
                relayedTo++;
            }

            RefreshRemoteActionRelayCache(source);
            if (VerbosePacketLogging || skippedWithoutState > 0)
                Debug.LogError($"[{tag}] src={source.LoginName} action=0x50 target={targetEntityId} relayedPendingToPeers={relayedTo} skippedWithoutState={skippedWithoutState} phase=queued-await-client-entity-writer sourceFunction=Behavior::processUpdate@0x00515620 opcode=0x01->Behavior::doActionLocal@0x00515130 slot=Behavior+0x78");
            return relayedTo > 0;
        }

        private static bool TryReadPeerPlayerActionMessage(
            byte[] message,
            out ushort remoteBehaviorId,
            out bool directInterrupt,
            out byte wireGeneration,
            out byte actionOpcode,
            out byte actionSessionId,
            out ushort peerTargetEntityId)
        {
            remoteBehaviorId = 0;
            directInterrupt = false;
            wireGeneration = 0;
            actionOpcode = 0;
            actionSessionId = 0;
            peerTargetEntityId = 0;
            if (message == null || message.Length < 6 || message[0] != 0x35)
                return false;
            remoteBehaviorId = (ushort)(message[1] | (message[2] << 8));
            if (message[3] == 0x04)
            {
                directInterrupt = true;
                actionOpcode = message[4];
                if (actionOpcode == 0x50)
                {
                    if (message.Length < 9)
                        return false;
                    actionSessionId = message[5];
                    peerTargetEntityId = (ushort)(message[7] | (message[8] << 8));
                    return true;
                }
                if (actionOpcode == 0x51)
                {
                    if (message.Length < 19)
                        return false;
                    actionSessionId = message[5];
                    return true;
                }
                return false;
            }
            if (message[3] != 0x01 || message.Length < 7)
                return false;
            wireGeneration = message[4];
            actionOpcode = message[5];
            if (actionOpcode == 0x50)
            {
                if (message.Length < 10)
                    return false;
                actionSessionId = message[6];
                peerTargetEntityId = (ushort)(message[8] | (message[9] << 8));
                return true;
            }
            if (actionOpcode == 0x51)
            {
                if (message.Length < 20)
                    return false;
                actionSessionId = message[6];
                return true;
            }
            return false;
        }

        private static bool TryReadPeerPlayerCancelMessage(
            byte[] message,
            out ushort remoteBehaviorId,
            out byte wireGeneration)
        {
            remoteBehaviorId = 0;
            wireGeneration = 0;
            if (message == null
                || message.Length < 5
                || message[0] != 0x35
                || message[3] != 0x03)
                return false;
            remoteBehaviorId = (ushort)(message[1] | (message[2] << 8));
            wireGeneration = message[4];
            return true;
        }

        private static bool HasCanonicalPeerWriterSubtype(
            IReadOnlyList<byte[]> messages,
            ushort remoteBehaviorId,
            byte wantedSubtype,
            out bool hasMover)
        {
            hasMover = false;
            if (messages == null || remoteBehaviorId == 0)
                return false;

            bool found = false;
            for (int messageIndex = 0; messageIndex < messages.Count; messageIndex++)
            {
                byte[] message = messages[messageIndex];
                int offset = 0;
                while (TryReadCanonicalUnitFollowClientUpdate(
                    message,
                    offset,
                    remoteBehaviorId,
                    out byte subtype,
                    out _,
                    out int nextOffset))
                {
                    found |= subtype == wantedSubtype;
                    hasMover |= subtype == 0x65;
                    offset = nextOffset;
                }
            }
            return found;
        }

        private void ReconcilePeerWriterStopAdmission(
            RRConnection viewer,
            RRConnection source,
            RemotePeerActionState state,
            bool awaitingOwnerMovement,
            bool followClientGrantQueued,
            bool followingClient,
            byte moverSession,
            bool hasMover)
        {
            if (viewer == null || source == null || state == null)
                return;

            state.ActionActive = false;
            state.CurrentActionInterruptLocked = false;
            state.AwaitingOwnerMovement = hasMover ? false : awaitingOwnerMovement;
            state.FollowClientGrantQueued = followClientGrantQueued || state.FollowClientGrantQueued;
            state.FollowingClient = state.FollowClientGrantQueued || followingClient;
            state.ActionOpcode = 0;
            state.ActionSessionId = 0;
            state.HasSourceUseTargetSession = false;
            state.SourceUseTargetSessionId = 0;
            state.ActionTerminationTick = 0;
            ClearPeerPendingAction(state);
            if (GetRemoteBehaviorSession(viewer, source) == moverSession)
                SetRemoteBehaviorSession(viewer, source, moverSession);
            RefreshRemoteActionRelayCache(source);
        }

        private RRConnection ResolvePeerPlayerActionSource(RRConnection viewer, ushort remoteBehaviorId)
        {
            foreach (RRConnection candidate in GetConnectionInsertionOrderSnapshot())
            {
                if (candidate == null || candidate == viewer || !IsSameMovementRuntime(viewer, candidate))
                    continue;
                if (TryResolveRemoteBehaviorForViewer(viewer, candidate, out ushort candidateBehaviorId)
                    && candidateBehaviorId == remoteBehaviorId)
                    return candidate;
            }
            return null;
        }

        private static byte[] RewritePeerPlayerActionAsPending(byte[] directMessage, byte behaviorGeneration)
        {
            var pendingMessage = new byte[directMessage.Length + 1];
            Buffer.BlockCopy(directMessage, 0, pendingMessage, 0, 3);
            pendingMessage[3] = 0x01;
            pendingMessage[4] = behaviorGeneration;
            Buffer.BlockCopy(directMessage, 4, pendingMessage, 5, directMessage.Length - 4);
            return pendingMessage;
        }

        private void RewritePeerPlayerActionMessages(RRConnection viewer, List<byte[]> messages, uint packetWriterTick)
        {
            if (viewer == null || messages == null || messages.Count == 0)
                return;
            var actionActiveByBehavior = new Dictionary<ushort, bool>();
            var behaviorGenerationByBehavior = new Dictionary<ushort, byte>();
            var interruptLockedByBehavior = new Dictionary<ushort, bool>();
            for (int messageIndex = 0; messageIndex < messages.Count; messageIndex++)
            {
                byte[] message = messages[messageIndex];
                bool playerAction = TryReadPeerPlayerActionMessage(
                    message,
                    out ushort remoteBehaviorId,
                    out bool directInterrupt,
                    out byte wireGeneration,
                    out byte actionOpcode,
                    out byte actionSessionId,
                    out _);
                bool cancelAction = !playerAction
                    && TryReadPeerPlayerCancelMessage(message, out remoteBehaviorId, out wireGeneration);
                if (!playerAction && !cancelAction)
                    continue;
                RRConnection source = ResolvePeerPlayerActionSource(viewer, remoteBehaviorId);
                if (source == null)
                    continue;
                if (!actionActiveByBehavior.TryGetValue(remoteBehaviorId, out bool actionActive))
                {
                    var state = GetRemotePeerActionState(viewer, source, false);
                    if (state == null)
                    {
                        Debug.LogError($"[PEER-ACTION-REWRITE] viewer={viewer.LoginName} source={source.LoginName} behavior={remoteBehaviorId} action=0x{actionOpcode:X2} session={actionSessionId} packetWriterTick={packetWriterTick} messageIndex={messageIndex} result=missing-mirror-state");
                        continue;
                    }
                    actionActive = state.ActionActive;
                    actionActiveByBehavior[remoteBehaviorId] = actionActive;
                    behaviorGenerationByBehavior[remoteBehaviorId] = state.BehaviorGeneration;
                    interruptLockedByBehavior[remoteBehaviorId] = state.CurrentActionInterruptLocked;
                }
                byte behaviorGeneration = behaviorGenerationByBehavior[remoteBehaviorId];
                bool interruptLocked = interruptLockedByBehavior[remoteBehaviorId];
                if (cancelAction)
                {
                    if (wireGeneration != behaviorGeneration)
                    {
                        byte[] rewrittenMessage = (byte[])message.Clone();
                        rewrittenMessage[4] = behaviorGeneration;
                        messages[messageIndex] = rewrittenMessage;
                        Debug.LogError($"[PEER-ACTION-REWRITE] viewer={viewer.LoginName} source={source.LoginName} behavior={remoteBehaviorId} action=cancel queuedGeneration={wireGeneration} writerGeneration={behaviorGeneration} packetWriterTick={packetWriterTick} messageIndex={messageIndex} result=generation-rebased sourceFunction=Behavior::processUpdate@0x00515620 opcode=0x03");
                    }
                    if (actionActive && !interruptLocked)
                    {
                        actionActive = false;
                        interruptLocked = false;
                    }
                    actionActiveByBehavior[remoteBehaviorId] = actionActive;
                    interruptLockedByBehavior[remoteBehaviorId] = interruptLocked;
                    continue;
                }
                if (directInterrupt)
                {
                    if (actionActive)
                    {
                        messages[messageIndex] = RewritePeerPlayerActionAsPending(message, behaviorGeneration);
                        Debug.LogError($"[PEER-ACTION-REWRITE] viewer={viewer.LoginName} source={source.LoginName} behavior={remoteBehaviorId} action=0x{actionOpcode:X2} session={actionSessionId} generation={behaviorGeneration} packetWriterTick={packetWriterTick} messageIndex={messageIndex} result=direct-to-pending sourceFunction=Behavior::doActionLocal@0x00515130");
                    }
                    else
                    {
                        actionActive = true;
                        interruptLocked = true;
                        behaviorGeneration = unchecked((byte)(behaviorGeneration + 1));
                    }
                }
                else
                {
                    if (wireGeneration != behaviorGeneration)
                    {
                        byte[] rewrittenMessage = (byte[])message.Clone();
                        rewrittenMessage[4] = behaviorGeneration;
                        messages[messageIndex] = rewrittenMessage;
                        Debug.LogError($"[PEER-ACTION-REWRITE] viewer={viewer.LoginName} source={source.LoginName} behavior={remoteBehaviorId} action=0x{actionOpcode:X2} session={actionSessionId} queuedGeneration={wireGeneration} writerGeneration={behaviorGeneration} packetWriterTick={packetWriterTick} messageIndex={messageIndex} result=generation-rebased sourceFunction=Behavior::processUpdate@0x00515620");
                    }
                    if (!actionActive)
                        interruptLocked = false;
                    actionActive = true;
                }
                actionActiveByBehavior[remoteBehaviorId] = actionActive;
                behaviorGenerationByBehavior[remoteBehaviorId] = behaviorGeneration;
                interruptLockedByBehavior[remoteBehaviorId] = interruptLocked;
            }
        }

        private void AdmitPeerPlayerActionResponses(RRConnection viewer, IReadOnlyList<byte[]> messages, uint packetWriterTick)
        {
            if (viewer == null || messages == null || messages.Count == 0)
                return;

            var writerStopAwaitingByBehavior = new Dictionary<ushort, bool>();
            var writerStopGrantByBehavior = new Dictionary<ushort, bool>();
            var writerStopFollowingByBehavior = new Dictionary<ushort, bool>();
            var writerStopSessionByBehavior = new Dictionary<ushort, byte>();
            var writerStopHasMoverByBehavior = new Dictionary<ushort, bool>();
            var writerStopBehaviorOrder = new List<ushort>();
            foreach (RRConnection candidate in GetConnectionInsertionOrderSnapshot())
            {
                if (candidate == null || candidate == viewer || !IsSameMovementRuntime(viewer, candidate))
                    continue;
                if (!TryResolveRemoteBehaviorForViewer(viewer, candidate, out ushort candidateBehaviorId))
                    continue;
                if (!HasCanonicalPeerWriterSubtype(
                    messages,
                    candidateBehaviorId,
                    0x05,
                    out bool hasMover))
                    continue;
                var candidateState = GetRemotePeerActionState(viewer, candidate, false);
                if (candidateState == null)
                    continue;
                if (!writerStopAwaitingByBehavior.ContainsKey(candidateBehaviorId))
                    writerStopBehaviorOrder.Add(candidateBehaviorId);
                writerStopAwaitingByBehavior[candidateBehaviorId] = candidateState.AwaitingOwnerMovement;
                writerStopGrantByBehavior[candidateBehaviorId] = candidateState.FollowClientGrantQueued;
                writerStopFollowingByBehavior[candidateBehaviorId] = candidateState.FollowingClient;
                writerStopSessionByBehavior[candidateBehaviorId] = GetRemoteBehaviorSession(viewer, candidate);
                writerStopHasMoverByBehavior[candidateBehaviorId] = hasMover;
            }

            for (int messageIndex = 0; messageIndex < messages.Count; messageIndex++)
            {
                byte[] message = messages[messageIndex];
                if (TryReadPeerPlayerCancelMessage(
                    message,
                    out ushort cancelBehaviorId,
                    out byte cancelGeneration))
                {
                    RRConnection cancelSource = ResolvePeerPlayerActionSource(viewer, cancelBehaviorId);
                    if (cancelSource == null)
                        continue;
                    var cancelState = GetRemotePeerActionState(viewer, cancelSource, false);
                    if (cancelState == null || cancelState.BehaviorGeneration != cancelGeneration)
                    {
                        Debug.LogError($"[PEER-ACTION-ADMISSION] viewer={viewer.LoginName} source={cancelSource.LoginName} behavior={cancelBehaviorId} action=cancel wireGeneration={cancelGeneration} mirrorGeneration={(cancelState == null ? -1 : cancelState.BehaviorGeneration)} packetWriterTick={packetWriterTick} messageIndex={messageIndex} result=rejected-generation sourceFunction=Behavior::processUpdate@0x00515620 opcode=0x03");
                        continue;
                    }
                    bool pendingCleared = cancelState.PendingActionActive;
                    bool currentStopped = cancelState.ActionActive && !cancelState.CurrentActionInterruptLocked;
                    ClearPeerPendingAction(cancelState);
                    if (currentStopped)
                    {
                        cancelState.ActionActive = false;
                        cancelState.CurrentActionInterruptLocked = false;
                        if (cancelState.ClientControlEnabled)
                            MarkPeerFollowingClient(viewer, cancelSource, true);
                        cancelState.AwaitingOwnerMovement = true;
                        cancelState.ActionOpcode = 0;
                        cancelState.ActionSessionId = 0;
                        cancelState.HasSourceUseTargetSession = false;
                        cancelState.SourceUseTargetSessionId = 0;
                        cancelState.ActionTerminationTick = 0;
                    }
                    cancelState.LastQueuedTargetActionTick = packetWriterTick;
                    RefreshRemoteActionRelayCache(cancelSource);
                    Debug.LogError($"[PEER-ACTION-ADMISSION] viewer={viewer.LoginName} source={cancelSource.LoginName} behavior={cancelBehaviorId} action=cancel generation={cancelGeneration} moverSession={GetRemoteBehaviorSession(viewer, cancelSource)} interruptLocked={cancelState.CurrentActionInterruptLocked} pendingCleared={pendingCleared} currentStopped={currentStopped} packetWriterTick={packetWriterTick} messageIndex={messageIndex} result=accepted sourceFunction=Behavior::processUpdate@0x00515620 opcode=0x03");
                    continue;
                }
                if (!TryReadPeerPlayerActionMessage(
                    message,
                    out ushort remoteBehaviorId,
                    out bool directInterrupt,
                    out byte wireGeneration,
                    out byte actionOpcode,
                    out byte actionSessionId,
                    out ushort peerTargetEntityId))
                    continue;
                RRConnection source = ResolvePeerPlayerActionSource(viewer, remoteBehaviorId);
                if (source == null)
                    continue;
                var state = GetRemotePeerActionState(viewer, source, false);
                ushort sourceTargetEntityId = actionOpcode == 0x50
                    ? ResolvePeerActionTargetId(viewer, source, peerTargetEntityId)
                    : (ushort)0;
                int usePositionFollowClientRecords = actionOpcode == 0x51
                    ? CountPeerFollowClientRecordsAtActionAdmission(source, messages, messageIndex, remoteBehaviorId)
                    : 0;

                if (directInterrupt)
                {
                    bool interruptedCurrent = state?.ActionActive == true;
                    byte generationBefore = state?.BehaviorGeneration ?? 0;
                    byte moverSessionBefore = GetRemoteBehaviorSession(viewer, source);
                    if (state?.ActionActive == true && state.CurrentActionInterruptLocked)
                    {
                        Debug.LogError($"[PEER-ACTION-ADMISSION] viewer={viewer.LoginName} source={source.LoginName} behavior={remoteBehaviorId} action=0x{actionOpcode:X2} session={actionSessionId} generation={generationBefore} moverSession={moverSessionBefore} packetWriterTick={packetWriterTick} messageIndex={messageIndex} result=rejected-current-interrupt-locked sourceFunction=Behavior::doInterruptLocal@0x00515290");
                        continue;
                    }
                    MarkPeerActionStarted(
                        viewer,
                        source,
                        sourceTargetEntityId,
                        actionOpcode,
                        actionSessionId,
                        usePositionFollowClientRecords,
                        true);
                    state = GetRemotePeerActionState(viewer, source, false);
                    if (state == null)
                        continue;
                    state.LastQueuedTargetActionTick = packetWriterTick;
                    byte moverSessionAfter = GetRemoteBehaviorSession(viewer, source);
                    Debug.LogError($"[PEER-ACTION-ADMISSION] viewer={viewer.LoginName} source={source.LoginName} behavior={remoteBehaviorId} action=0x{actionOpcode:X2} session={actionSessionId} generationBefore={generationBefore} generationAfter={state.BehaviorGeneration} moverSessionBefore={moverSessionBefore} moverSessionAfter={moverSessionAfter} clientControl={state.ClientControlEnabled} followingClient={state.FollowingClient} interruptLocked={state.CurrentActionInterruptLocked} interruptedCurrent={interruptedCurrent} queuedRecords={usePositionFollowClientRecords} peerTarget={peerTargetEntityId} sourceTarget={sourceTargetEntityId} packetWriterTick={packetWriterTick} messageIndex={messageIndex} result=accepted-direct-interrupt sourceFunction=ClientEntityManager::processMessage@0x005DA460->Behavior::processUpdate@0x00515620->Behavior::doInterruptLocal@0x00515290");
                    continue;
                }

                if (state == null || state.BehaviorGeneration != wireGeneration)
                {
                    Debug.LogError($"[PEER-ACTION-ADMISSION] viewer={viewer.LoginName} source={source.LoginName} behavior={remoteBehaviorId} action=0x{actionOpcode:X2} session={actionSessionId} wireGeneration={wireGeneration} mirrorGeneration={(state == null ? -1 : state.BehaviorGeneration)} peerTarget={peerTargetEntityId} packetWriterTick={packetWriterTick} messageIndex={messageIndex} result=rejected-generation sourceFunction=Behavior::processUpdate@0x00515620");
                    continue;
                }

                string admission;
                if (state.ActionActive)
                {
                    state.PendingActionActive = true;
                    state.PendingActionOpcode = actionOpcode;
                    state.PendingActionSessionId = actionSessionId;
                    state.PendingTargetEntityId = sourceTargetEntityId;
                    admission = "pending";
                }
                else
                {
                    bool preserveUsePositionFollowClient = actionOpcode == 0x51 && usePositionFollowClientRecords > 0;
                    if (!preserveUsePositionFollowClient)
                        MarkPeerFollowingClient(viewer, source, false);
                    state.ActionActive = true;
                    state.CurrentActionInterruptLocked = false;
                    state.AwaitingOwnerMovement = true;
                    state.ActionOpcode = actionOpcode;
                    state.ActionSessionId = actionSessionId;
                    state.HasSourceUseTargetSession = actionOpcode == 0x50;
                    state.SourceUseTargetSessionId = actionOpcode == 0x50 ? actionSessionId : (byte)0;
                    state.ActionTerminationTick = 0;
                    ClearPeerPendingAction(state);
                    admission = "current";
                }
                state.LastQueuedTargetActionTick = packetWriterTick;
                RefreshRemoteActionRelayCache(source);
                Debug.LogError($"[PEER-ACTION-ADMISSION] viewer={viewer.LoginName} source={source.LoginName} behavior={remoteBehaviorId} action=0x{actionOpcode:X2} session={actionSessionId} wireGeneration={wireGeneration} mirrorGeneration={state.BehaviorGeneration} moverSession={GetRemoteBehaviorSession(viewer, source)} clientControl={state.ClientControlEnabled} followingClient={state.FollowingClient} interruptLocked={state.CurrentActionInterruptLocked} queuedRecords={usePositionFollowClientRecords} peerTarget={peerTargetEntityId} sourceTarget={sourceTargetEntityId} packetWriterTick={packetWriterTick} messageIndex={messageIndex} result=accepted-{admission} sourceFunction=ClientEntityManager::processMessage@0x005DA460->Behavior::processUpdate@0x00515620->Behavior::doActionLocal@0x00515130");
            }

            foreach (ushort stopBehaviorId in writerStopBehaviorOrder)
            {
                RRConnection source = ResolvePeerPlayerActionSource(viewer, stopBehaviorId);
                if (source == null)
                    continue;
                var state = GetRemotePeerActionState(viewer, source, false);
                if (state == null)
                    continue;
                ReconcilePeerWriterStopAdmission(
                    viewer,
                    source,
                    state,
                    writerStopAwaitingByBehavior[stopBehaviorId],
                    writerStopGrantByBehavior[stopBehaviorId],
                    writerStopFollowingByBehavior[stopBehaviorId],
                    writerStopSessionByBehavior[stopBehaviorId],
                    writerStopHasMoverByBehavior[stopBehaviorId]);
                Debug.LogError($"[PEER-ACTION-ADMISSION] viewer={viewer.LoginName} source={source.LoginName} behavior={stopBehaviorId} action=stop packetWriterTick={packetWriterTick} finalActionActive={state.ActionActive} awaitingOwnerMovement={state.AwaitingOwnerMovement} clientControl={state.ClientControlEnabled} followingClient={state.FollowingClient} moverSession={GetRemoteBehaviorSession(viewer, source)} result=writer-ordered-stop sourceFunction=Behavior::processUpdate@0x00515620 opcode=0x05");
            }
        }

        private bool RelayCancelActionToPeers(RRConnection source, string tag)
        {
            if (source == null)
                return false;
            int relayedTo = 0;
            foreach (RRConnection other in GetConnectionInsertionOrderSnapshot())
            {
                if (other == null || other == source || !other.IsSpawned)
                    continue;
                if (!IsSameMovementRuntime(other, source))
                    continue;
                if (!TryResolveRemoteBehaviorForViewer(other, source, out ushort remoteBehaviorId))
                    continue;
                var state = GetRemotePeerActionState(other, source, false);
                if (state == null)
                    continue;
                var message = new LEWriter();
                message.WriteByte(0x35);
                message.WriteUInt16(remoteBehaviorId);
                message.WriteByte(0x03);
                message.WriteByte(state.BehaviorGeneration);
                if (!TryWriteRemoteAvatarEntitySynchInfo(source, message, remoteBehaviorId, 0x03, tag))
                    continue;
                other.MessageQueue.Enqueue(message.ToArray());
                relayedTo++;
            }
            if (VerboseMovementEvidence || relayedTo > 0)
                Debug.LogError($"[{tag}] source={source.LoginName} relayedToPeers={relayedTo} phase=queued-await-client-entity-writer sourceFunction=Behavior::processUpdate@0x00515620 opcode=0x03");
            return relayedTo > 0;
        }

        private bool RelayUsePositionActionToPeers(RRConnection source, byte actionSessionId, byte manipulatorId, int posX, int posY, int posZ, string tag)
        {
            if (source == null) return false;

            int relayedTo = 0;
            foreach (RRConnection other in GetConnectionInsertionOrderSnapshot())
            {
                if (other == source) continue;
                if (!other.IsSpawned) continue;
                if (!IsSameMovementRuntime(other, source)) continue;
                if (!TryResolveRemoteBehaviorForViewer(other, source, out ushort remoteBehaviorId)) continue;

                var message = new LEWriter();
                message.WriteByte(0x35);
                message.WriteUInt16(remoteBehaviorId);
                message.WriteByte(0x04);
                message.WriteByte(0x51);
                message.WriteByte(actionSessionId);
                message.WriteByte(manipulatorId);
                message.WriteUInt32(unchecked((uint)posX));
                message.WriteUInt32(unchecked((uint)posY));
                message.WriteUInt32(unchecked((uint)posZ));
                if (!TryWriteRemoteAvatarEntitySynchInfo(source, message, remoteBehaviorId, 0x04, tag))
                    continue;
                other.MessageQueue.Enqueue(message.ToArray());
                relayedTo++;
            }

            RefreshRemoteActionRelayCache(source);
            if (VerbosePacketLogging)
                Debug.LogError($"[{tag}] src={source.LoginName} action=0x51 session={actionSessionId} manipulator={manipulatorId} relayedToPeers={relayedTo} phase=queued-await-client-entity-writer sourceFunction=Behavior::processUpdate@0x00515620 opcode=0x04->Behavior::doInterruptLocal@0x00515290");
            return relayedTo > 0;
        }

        private bool QueueActiveTargetActionSnapshot(RRConnection viewer, RRConnection source, int delayFlushes = 0)
        {
            if (viewer == null || source == null || !TryResolveRemoteBehaviorForViewer(viewer, source, out ushort remoteBehaviorId))
                return false;

            var message = new LEWriter();
            if (!WriteRemoteFollowClientControlUpdate(viewer, source, message, remoteBehaviorId, false))
                return false;
            message.WriteByte(0x35);
            message.WriteUInt16(remoteBehaviorId);
            message.WriteByte(0x04);
            ushort targetEntityId;
            byte actionOpcode;
            byte actionSessionId;
            if (source.UseTargetMovingActive && source.UseTargetMovingActionMirrored && source.UseTargetMovingActivate && source.UseTargetMovingActivateTargetId != 0)
            {
                targetEntityId = source.UseTargetMovingActivateTargetId;
                actionOpcode = 0x06;
                actionSessionId = 0;
                message.WriteByte(0x06);
                message.WriteByte(0x00);
                message.WriteUInt16(ResolvePeerActionTargetId(source, viewer, targetEntityId));
            }
            else if (source.HasActiveUseTarget && source.ActiveUseTargetId != 0)
            {
                targetEntityId = source.ActiveUseTargetId;
                actionOpcode = 0x50;
                actionSessionId = source.ActiveUseTargetSessionId;
                message.WriteByte(0x50);
                message.WriteByte(source.ActiveUseTargetSessionId);
                message.WriteByte(source.ActiveUseTargetFlags);
                message.WriteUInt16(ResolvePeerActionTargetId(source, viewer, targetEntityId));
            }
            else
            {
                return false;
            }

            if (!TryWriteRemoteAvatarEntitySynchInfo(source, message, remoteBehaviorId, 0x04, "MP-ACTION-SNAPSHOT"))
                return false;
            if (delayFlushes > 0)
                viewer.MessageQueue.EnqueueAfterFlushes(message.ToArray(), delayFlushes);
            else
                viewer.MessageQueue.Enqueue(message.ToArray());
            MarkPeerClientControl(viewer, source, false);
            MarkPeerActionStarted(viewer, source, targetEntityId, actionOpcode, actionSessionId, currentActionInterruptLocked: true);
            Debug.LogError($"[MP-ACTION-SNAPSHOT] viewer={viewer.LoginName} source={source.LoginName} target={targetEntityId} behavior={remoteBehaviorId} delayFlushes={delayFlushes} sourceFunction=Behavior::writeInit@0x00515890");
            return true;
        }

        public void SendCompressedPublic(RRConnection conn, byte dest, byte messageType, byte[] data)
            => SendCompressedA(conn, dest, messageType, data);
    }
}
