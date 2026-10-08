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
        private static void ResolveAuthoritativePlayerPositionFixed(RRConnection conn, out int fixedX, out int fixedY, out int fixedZ)
        {
            CombatPlayer player = conn?.Avatar != null && conn.Avatar.Id > 0
                ? CombatRuntime.Instance.GetPlayer((uint)conn.Avatar.Id)
                : null;
            if (player != null)
            {
                fixedX = player.ClientSimulationPosFixedX;
                fixedY = player.ClientSimulationPosFixedY;
                fixedZ = player.ClientSimulationPosFixedZ;
                return;
            }
            fixedX = conn != null && conn.HasReflectedAvatarPosition ? conn.ReflectedAvatarPosFixedX : conn?.PlayerPosFixedX ?? 0;
            fixedY = conn != null && conn.HasReflectedAvatarPosition ? conn.ReflectedAvatarPosFixedY : conn?.PlayerPosFixedY ?? 0;
            fixedZ = conn != null && conn.HasReflectedAvatarPosition ? conn.ReflectedAvatarPosFixedZ : conn?.PlayerPosFixedZ ?? 0;
        }

        private void DrainAvatarAggroSamples()
        {
            foreach (var conn in GetConnectionInsertionOrderSnapshot())
            {
                if (conn == null) continue;
                uint avatarId = GetPlayerAvatarId(conn.LoginName);
                ResolveAuthoritativePlayerPositionFixed(conn, out _, out _, out int posFixedZ);
                if (conn.AvatarAggroSampleQueue.Count > 0)
                {
                    int advance = conn.AvatarAggroSampleQueue.Count;
                    for (int i = 0; i < advance; i++)
                    {
                        var (x, y) = conn.AvatarAggroSampleQueue.Dequeue();
                        conn.AggroSamplePosFixedX = x;
                        conn.AggroSamplePosFixedY = y;
                        if (avatarId != 0)
                            CombatRuntime.Instance.TraverseAvatarMovementSampleFixed(avatarId, x, y, posFixedZ);
                    }
                }
                else if (avatarId != 0)
                    CombatRuntime.Instance.UpdatePlayerPositionFixed(avatarId, conn.AggroSamplePosFixedX, conn.AggroSamplePosFixedY, posFixedZ);
            }
        }

        private void AdvanceReflectedAvatarMovementSamples()
        {
            foreach (var conn in GetConnectionInsertionOrderSnapshot())
            {
                if (conn == null)
                    continue;
                conn.ReflectedAvatarMovingThisFrame = false;
                SyncReflectedAvatarCombatPosition(conn);
                if (conn.ReflectedAvatarMovementIdleBatchBarrierPending)
                {
                    conn.ReflectedAvatarMovementIdleBatchBarrierPending = false;
                    continue;
                }
                if (conn.ReadyReflectedAvatarMovementSamples.Count == 0)
                    continue;

                var sample = conn.ReadyReflectedAvatarMovementSamples.Peek();
                if (conn.ReflectedAvatarMovementPostAdmissionBarrierPending)
                {
                    conn.ReflectedAvatarMovementPostAdmissionBarrierPending = false;
                    if ((sample.MoveType & 0x01) == 0 && !sample.RequiresAdmission)
                        continue;
                }
                PlayerState state = GetPlayerState(conn.ConnId.ToString());
                int stepFixed = UnitMover.CacheSpeedPerFrame(state.SpeedF32, state.SpeedMod, out _);
                int currentX = conn.HasReflectedAvatarPosition ? conn.ReflectedAvatarPosFixedX : conn.PlayerPosFixedX;
                int currentY = conn.HasReflectedAvatarPosition ? conn.ReflectedAvatarPosFixedY : conn.PlayerPosFixedY;
                int currentZ = conn.HasReflectedAvatarPosition ? conn.ReflectedAvatarPosFixedZ : conn.PlayerPosFixedZ;
                int currentHeading = conn.HasReflectedAvatarPosition ? conn.ReflectedAvatarHeadingFixed : conn.PlayerHeadingFixed;
                int nextHeading = UnitMover.InterpolateHeading(currentHeading, sample.Heading, USE_TARGET_AVATAR_TURN_RATE_FIXED);
                bool facingReached = nextHeading == sample.Heading;
                ResolveFollowClientCandidateFixed(currentX, currentY, sample.X, sample.Y, stepFixed, out int candidateX, out int candidateY, out long lengthSqFixedLong);

                PathMap pathMap = ResolveUseTargetMovingPathMap(conn);
                bool collided = false;
                int nextX;
                int nextY;
                int nextZ;
                if (lengthSqFixedLong == 0)
                {
                    nextX = sample.X;
                    nextY = sample.Y;
                    nextZ = ResolveConnectionGroundHeightFixed(conn, pathMap, sample.X, sample.Y, currentZ);
                }
                else if (pathMap != null)
                    collided = pathMap.CastGroundRaySlideFixed(currentX, currentY, currentZ, candidateX, candidateY, out nextX, out nextY, out nextZ);
                else
                {
                    nextX = candidateX;
                    nextY = candidateY;
                    nextZ = ResolveConnectionGroundHeightFixed(conn, null, candidateX, candidateY, currentZ);
                }

                bool terminalRecord = (sample.MoveType & 0x01) != 0;
                if (!terminalRecord)
                    conn.ReflectedAvatarUnitMoverMode = UnitMover.FollowClientMode;
                SetReflectedAvatarPosition(conn, nextX, nextY, nextZ, nextHeading, false);
                conn.ReflectedAvatarMovingThisFrame = nextX != currentX || nextY != currentY;
                SyncReflectedAvatarCombatPosition(conn);
                conn.AvatarAggroSampleQueue.Enqueue((nextX, nextY));
                CheckPendingPortalActivation(conn);
                conn.ReflectedAvatarMovementAppliedOrdinal = sample.Ordinal;
                long remainingX = (long)sample.X - nextX;
                long remainingY = (long)sample.Y - nextY;
                bool positionReached = ((remainingX * remainingX) >> 8) + ((remainingY * remainingY) >> 8) == 0;
                if (!collided && facingReached)
                {
                    conn.ReadyReflectedAvatarMovementSamples.Dequeue();
                    conn.ReflectedAvatarUnitMoverMode = terminalRecord
                        ? UnitMover.StoppedMode
                        : UnitMover.FollowClientMode;
                    if (terminalRecord)
                        conn.ReflectedAvatarMovingThisFrame = false;
                    SyncReflectedAvatarCombatPosition(conn);
                    if (sample.RequiresAdmission)
                        conn.ReflectedAvatarMovementPostAdmissionBarrierPending = true;
                }
            }
        }

        private void SyncReflectedAvatarCombatPosition(RRConnection conn)
        {
            if (conn == null)
                return;
            uint avatarId = GetPlayerAvatarId(conn.LoginName);
            if (avatarId == 0)
                return;
            bool actionMoverOwnsUnitMover = ActionMirroredMoveToPointOwnsUnitMover(conn)
                && conn.HasOwnerAckFollowClientPosition;
            int fixedX = actionMoverOwnsUnitMover
                ? conn.OwnerAckFollowClientPosFixedX
                : conn.HasReflectedAvatarPosition ? conn.ReflectedAvatarPosFixedX : conn.PlayerPosFixedX;
            int fixedY = actionMoverOwnsUnitMover
                ? conn.OwnerAckFollowClientPosFixedY
                : conn.HasReflectedAvatarPosition ? conn.ReflectedAvatarPosFixedY : conn.PlayerPosFixedY;
            int fixedZ = actionMoverOwnsUnitMover
                ? conn.OwnerAckFollowClientPosFixedZ
                : conn.HasReflectedAvatarPosition ? conn.ReflectedAvatarPosFixedZ : conn.PlayerPosFixedZ;
            CombatRuntime.Instance.UpdatePlayerClientSimulationPositionFixed(avatarId, fixedX, fixedY, fixedZ);
            CombatRuntime.Instance.UpdatePlayerClientSimulationMoverMode(
                avatarId,
                actionMoverOwnsUnitMover ? UnitMover.MoveToPointMode : conn.ReflectedAvatarUnitMoverMode);
            CombatRuntime.Instance.UpdatePlayerClientSimulationMovingThisFrame(
                avatarId,
                actionMoverOwnsUnitMover ? conn.OwnerAckFollowClientMovingThisFrame : conn.ReflectedAvatarMovingThisFrame);
        }

        private void PromotePendingReflectedAvatarMovementSamples()
        {
            foreach (var conn in GetConnectionInsertionOrderSnapshot())
            {
                if (conn == null)
                    continue;
                while (conn.AdmittedReflectedAvatarMovementSamples.Count > 0)
                    conn.ReadyReflectedAvatarMovementSamples.Enqueue(conn.AdmittedReflectedAvatarMovementSamples.Dequeue());
                while (conn.HeldReflectedAvatarMovementSamples.Count > 0)
                    conn.AdmittedReflectedAvatarMovementSamples.Enqueue(conn.HeldReflectedAvatarMovementSamples.Dequeue());
                bool admissionBarrier = conn.AdmittedReflectedAvatarMovementSamples.Count > 0;
                bool initialStreamBarrier = false;
                while (conn.PendingReflectedAvatarMovementSamples.Count > 0)
                {
                    var sample = conn.PendingReflectedAvatarMovementSamples.Dequeue();
                    if (sample.RequiresAdmission)
                    {
                        admissionBarrier = true;
                        initialStreamBarrier = !sample.TerminalRestartAdmission;
                    }
                    if (admissionBarrier)
                    {
                        if (initialStreamBarrier)
                            conn.HeldReflectedAvatarMovementSamples.Enqueue(sample);
                        else
                            conn.AdmittedReflectedAvatarMovementSamples.Enqueue(sample);
                    }
                    else
                        conn.ReadyReflectedAvatarMovementSamples.Enqueue(sample);
                }
            }
        }

        private static void SetReflectedAvatarPosition(RRConnection conn, int fixedX, int fixedY, int fixedZ, int headingFixed, bool clearQueuedSamples)
        {
            if (conn == null)
                return;
            if (clearQueuedSamples)
            {
                conn.PendingReflectedAvatarMovementSamples.Clear();
                conn.HeldReflectedAvatarMovementSamples.Clear();
                conn.AdmittedReflectedAvatarMovementSamples.Clear();
                conn.ReadyReflectedAvatarMovementSamples.Clear();
                conn.ReflectedAvatarMovementStreamAdmitted = false;
                conn.ReflectedAvatarMovementTerminalRestartPending = false;
                conn.ReflectedAvatarMovementPostAdmissionBarrierPending = false;
                conn.ReflectedAvatarMovementIdleBatchBarrierPending = false;
                conn.ReflectedAvatarMovementReceivedOrdinal = 0;
                conn.ReflectedAvatarMovementAppliedOrdinal = 0;
                conn.ReflectedAvatarUnitMoverMode = UnitMover.StoppedMode;
                conn.OwnerUnitBehaviorMoverMode = UnitMover.StoppedMode;
                SetOwnerAckFollowClientPosition(conn, fixedX, fixedY, fixedZ, headingFixed, true);
                SetUnitFollowClientPosition(conn, fixedX, fixedY, fixedZ, headingFixed, true);
            }
            else if (!conn.HasOwnerAckFollowClientPosition)
                SetOwnerAckFollowClientPosition(conn, fixedX, fixedY, fixedZ, headingFixed, false);
            if (!conn.HasUnitFollowClientPosition)
                SetUnitFollowClientPosition(conn, fixedX, fixedY, fixedZ, headingFixed, false);
            conn.HasReflectedAvatarPosition = true;
            conn.ReflectedAvatarPosFixedX = fixedX;
            conn.ReflectedAvatarPosFixedY = fixedY;
            conn.ReflectedAvatarPosFixedZ = fixedZ;
            conn.ReflectedAvatarHeadingFixed = headingFixed;
        }

        private static void SetOwnerAckFollowClientPosition(RRConnection conn, int fixedX, int fixedY, int fixedZ, int headingFixed, bool clearQueuedSamples)
        {
            if (conn == null)
                return;
            if (clearQueuedSamples)
            {
                conn.OwnerAckFollowClientMovementSamples.Clear();
                conn.OwnerAckFollowClientReceivedOrdinal = 0;
                conn.OwnerAckFollowClientAppliedOrdinal = 0;
                conn.OwnerAckFollowClientMovingThisFrame = false;
            }
            conn.HasOwnerAckFollowClientPosition = true;
            conn.OwnerAckFollowClientPosFixedX = fixedX;
            conn.OwnerAckFollowClientPosFixedY = fixedY;
            conn.OwnerAckFollowClientPosFixedZ = fixedZ;
            conn.OwnerAckFollowClientHeadingFixed = headingFixed;
            conn.OwnerAckFollowClientMovingThisFrame = false;
            SyncOwnerAckFollowClientCurrentState(conn);
        }

        private static void SyncUnitBehaviorPredictedLocation2D(RRConnection conn)
        {
            if (conn?.Avatar == null || conn.Avatar.Id <= 0)
                return;
            int fixedX;
            int fixedY;
            if (conn.UnitFollowClientMovementSamples.Count > 0)
            {
                var sample = conn.UnitFollowClientMovementSamples.Last();
                fixedX = sample.X;
                fixedY = sample.Y;
            }
            else if (conn.HasUnitFollowClientPosition)
            {
                fixedX = conn.UnitFollowClientPosFixedX;
                fixedY = conn.UnitFollowClientPosFixedY;
            }
            else
            {
                fixedX = conn.PlayerPosFixedX;
                fixedY = conn.PlayerPosFixedY;
            }
            CombatRuntime.Instance.UpdatePlayerPredictedLocation2DFixed((uint)conn.Avatar.Id, fixedX, fixedY);
        }

        private static byte ResolveOwnerAckFollowClientMoverMode(RRConnection conn)
        {
            if (conn == null)
                return UnitMover.StoppedMode;
            if (ActionMirroredMoveToPointOwnsUnitMover(conn))
                return UnitMover.MoveToPointMode;
            if (conn.UsePositionActionMirrored && conn.UsePositionActionStoppedFollowClient)
                return UnitMover.StoppedMode;
            if (conn.UseTargetMovingActionMirrored && conn.UseTargetMovingUsing)
                return UnitMover.StoppedMode;
            return conn.ReflectedAvatarUnitMoverMode;
        }

        private static void SyncOwnerAckFollowClientCurrentState(RRConnection conn)
        {
            if (conn?.Avatar == null || conn.Avatar.Id <= 0)
                return;
            int fixedX = conn.HasOwnerAckFollowClientPosition
                ? conn.OwnerAckFollowClientPosFixedX
                : conn.PlayerPosFixedX;
            int fixedY = conn.HasOwnerAckFollowClientPosition
                ? conn.OwnerAckFollowClientPosFixedY
                : conn.PlayerPosFixedY;
            int fixedZ = conn.HasOwnerAckFollowClientPosition
                ? conn.OwnerAckFollowClientPosFixedZ
                : conn.PlayerPosFixedZ;
            bool movingThisFrame = conn.HasOwnerAckFollowClientPosition
                && conn.OwnerAckFollowClientMovingThisFrame;
            CombatRuntime.Instance.UpdatePlayerOwnerAckFollowClientStateFixed(
                (uint)conn.Avatar.Id,
                fixedX,
                fixedY,
                fixedZ,
                movingThisFrame,
                ResolveOwnerAckFollowClientMoverMode(conn));
        }

        private static void SetUnitFollowClientPosition(RRConnection conn, int fixedX, int fixedY, int fixedZ, int headingFixed, bool clearQueuedSamples)
        {
            if (conn == null)
                return;
            if (clearQueuedSamples)
            {
                conn.UnitFollowClientMovementSamples.Clear();
                conn.UnitFollowClientReceivedOrdinal = 0;
                conn.UnitFollowClientAppliedOrdinal = 0;
                conn.UnitFollowClientMovingThisFrame = false;
                conn.UnitFollowClientMoverMode = UnitMover.StoppedMode;
            }
            conn.HasUnitFollowClientPosition = true;
            conn.UnitFollowClientPosFixedX = fixedX;
            conn.UnitFollowClientPosFixedY = fixedY;
            conn.UnitFollowClientPosFixedZ = fixedZ;
            conn.UnitFollowClientHeadingFixed = headingFixed;
            SyncUnitBehaviorPredictedLocation2D(conn);
            SyncUnitFollowClientCurrentState(conn);
        }

        private static void SyncUnitFollowClientCurrentState(RRConnection conn)
        {
            if (conn?.Avatar == null || conn.Avatar.Id <= 0)
                return;
            int fixedX = conn.HasUnitFollowClientPosition ? conn.UnitFollowClientPosFixedX : conn.PlayerPosFixedX;
            int fixedY = conn.HasUnitFollowClientPosition ? conn.UnitFollowClientPosFixedY : conn.PlayerPosFixedY;
            int fixedZ = conn.HasUnitFollowClientPosition ? conn.UnitFollowClientPosFixedZ : conn.PlayerPosFixedZ;
            CombatRuntime.Instance.UpdatePlayerUnitFollowClientStateFixed(
                (uint)conn.Avatar.Id,
                fixedX,
                fixedY,
                fixedZ,
                conn.HasUnitFollowClientPosition && conn.UnitFollowClientMovingThisFrame);
        }

        private static void ResetReflectedAvatarPosition(RRConnection conn)
        {
            if (conn == null)
                return;
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
            conn.PendingReflectedAvatarMovementSamples.Clear();
            conn.HeldReflectedAvatarMovementSamples.Clear();
            conn.AdmittedReflectedAvatarMovementSamples.Clear();
            conn.ReadyReflectedAvatarMovementSamples.Clear();
            conn.ReflectedAvatarMovementStreamAdmitted = false;
            conn.ReflectedAvatarMovementTerminalRestartPending = false;
            conn.ReflectedAvatarMovementPostAdmissionBarrierPending = false;
            conn.ReflectedAvatarMovementIdleBatchBarrierPending = false;
            conn.ReflectedAvatarMovementReceivedOrdinal = 0;
            conn.ReflectedAvatarMovementAppliedOrdinal = 0;
            conn.OwnerAckFollowClientMovementSamples.Clear();
            conn.OwnerAckFollowClientReceivedOrdinal = 0;
            conn.OwnerAckFollowClientAppliedOrdinal = 0;
            conn.HasOwnerAckFollowClientPosition = false;
            conn.OwnerAckFollowClientMovingThisFrame = false;
            conn.UnitFollowClientMovementSamples.Clear();
            conn.UnitFollowClientReceivedOrdinal = 0;
            conn.UnitFollowClientAppliedOrdinal = 0;
            conn.HasUnitFollowClientPosition = false;
            conn.UnitFollowClientMovingThisFrame = false;
            conn.UnitFollowClientMoverMode = UnitMover.StoppedMode;
            conn.UnitFollowClientWriterStateInitialized = false;
            conn.UnitFollowClientClientControlEnabled = true;
            conn.UnitFollowClientFollowingClient = true;
            conn.UnitFollowClientActionActive = false;
            conn.UseTargetMovingUnitFollowClientWaitActive = false;
            conn.UseTargetMovingUnitFollowClientWaitThroughOrdinal = 0;
            conn.UseTargetMovingInputAdmitted = true;
            SyncUnitBehaviorPredictedLocation2D(conn);
            SyncOwnerAckFollowClientCurrentState(conn);
            SyncUnitFollowClientCurrentState(conn);
            conn.HasReflectedAvatarPosition = false;
            conn.ReflectedAvatarUnitMoverMode = UnitMover.StoppedMode;
            conn.OwnerUnitBehaviorMoverMode = UnitMover.StoppedMode;
            conn.ReflectedAvatarMovingThisFrame = false;
        }

        private void HandleClientMove(RRConnection conn, LEReader reader, ushort componentId)
        {
            if (conn == null
                || !conn.IsConnected
                || !conn.IsSpawned
                || !conn.TickUpdatesActive
                || !conn.AllowFlush
                || conn.UnitBehaviorId == 0
                || componentId != conn.UnitBehaviorId)
            {
                Debug.LogError($"[MOVE] state=discarded component={componentId} current={conn?.UnitBehaviorId ?? 0} spawned={conn?.IsSpawned ?? false} ticks={conn?.TickUpdatesActive ?? false} flush={conn?.AllowFlush ?? false} sourceFunction=ClientEntityManager::processMessage@0x005DA460 roomEpoch=inactive-or-mismatched");
                return;
            }
            try
            {
                byte sessionId = reader.ReadByte();
                byte moveCount = reader.ReadByte();
                conn.MovementGeneration = sessionId;

                int lastFixedX = conn.PlayerPosFixedX;
                int lastFixedY = conn.PlayerPosFixedY;
                int lastHeadingFixed = conn.PlayerHeadingFixed;
                byte lastMoveType = 0x01;
                int previousFixedX = conn.PlayerPosFixedX;
                int previousFixedY = conn.PlayerPosFixedY;
                int rawStartPos = reader.Position;
                var ownerFollowSamples = new List<(byte MoveType, int Heading, int X, int Y)>(moveCount);
                if (moveCount > 0 && !conn.HasReflectedAvatarPosition)
                    SetReflectedAvatarPosition(conn, conn.PlayerPosFixedX, conn.PlayerPosFixedY, conn.PlayerPosFixedZ, conn.PlayerHeadingFixed, false);
                bool reflectedStreamAdmitted = conn.ReflectedAvatarMovementStreamAdmitted;
                bool terminalRestartPending = conn.ReflectedAvatarMovementTerminalRestartPending;
                bool reflectedQueuesEmpty = conn.PendingReflectedAvatarMovementSamples.Count == 0
                    && conn.HeldReflectedAvatarMovementSamples.Count == 0
                    && conn.AdmittedReflectedAvatarMovementSamples.Count == 0
                    && conn.ReadyReflectedAvatarMovementSamples.Count == 0;
                if (reflectedStreamAdmitted && reflectedQueuesEmpty && moveCount > 0)
                    conn.ReflectedAvatarMovementIdleBatchBarrierPending = true;
                for (int moveIndex = 0; moveIndex < moveCount; moveIndex++)
                {
                    byte moveType = reader.ReadByte();
                    int heading = reader.ReadInt32();
                    int posX = reader.ReadInt32();
                    int posY = reader.ReadInt32();

                    lastFixedX = posX;
                    lastFixedY = posY;
                    lastHeadingFixed = heading;
                    lastMoveType = moveType;
                    ownerFollowSamples.Add((moveType, heading, posX, posY));
                    bool terminalRecord = (moveType & 0x01) != 0;
                    bool requiresAdmission = !reflectedStreamAdmitted && !terminalRecord;
                    bool terminalRestartAdmission = requiresAdmission && terminalRestartPending;
                    ulong ordinal = ++conn.ReflectedAvatarMovementReceivedOrdinal;
                    conn.PendingReflectedAvatarMovementSamples.Enqueue((moveType, WrapHeadingFixed(heading), posX, posY, requiresAdmission, terminalRestartAdmission, ordinal));
                    reflectedStreamAdmitted = !terminalRecord;
                    terminalRestartPending = terminalRecord;
                }
                if (moveCount > 0)
                {
                    conn.ReflectedAvatarMovementStreamAdmitted = reflectedStreamAdmitted;
                    conn.ReflectedAvatarMovementTerminalRestartPending = terminalRestartPending;
                }
                int rawEndPos = reader.Position;
                TryConsumeClientEntitySynchInfoSuffix(conn, reader, "MOVE-ENTITY-SYNCH-INFO");
                if (moveCount > 0)
                {
                    bool useTargetMovingActive = conn.UseTargetMovingActive;
                    bool positionChanged = !conn.HasLivePlayerPosition || lastFixedX != previousFixedX || lastFixedY != previousFixedY;
                    if (conn.UseTargetMovingActive)
                    {
                        if (TryResolveUseTargetMovingTargetFixedReadOnly(conn, out int moveTargetFixedX, out int moveTargetFixedY, out string lostReason))
                        {
                            long beforeSq = DistanceSqFixed(previousFixedX, previousFixedY, moveTargetFixedX, moveTargetFixedY);
                            long afterSq = DistanceSqFixed(lastFixedX, lastFixedY, moveTargetFixedX, moveTargetFixedY);
                            if (!conn.UseTargetMovingActionMirrored
                                && ShouldEndClientDrivenTargetApproachForOwnerMove(positionChanged, beforeSq, afterSq))
                                CancelUseTargetMoving(conn, "client-move");
                        }
                        else
                        {
                            CancelUseTargetMoving(conn, lostReason ?? "target-lost");
                        }
                    }
                    if (positionChanged && IsZoneSpawnInvulnerabilityActive(conn))
                        ClearZoneSpawnInvulnerability(conn, "MOVE");
                    conn.PlayerPosFixedX = lastFixedX;
                    conn.PlayerPosFixedY = lastFixedY;
                    PathMap ownerMovePathMap = ResolveUseTargetMovingPathMap(conn);
                    int lastFixedZ = ResolveConnectionGroundHeightFixed(conn, ownerMovePathMap, lastFixedX, lastFixedY, conn.PlayerPosFixedZ);
                    conn.PlayerPosFixedZ = lastFixedZ;
                    conn.PlayerHeadingFixed = lastHeadingFixed;
                    conn.HasLivePlayerPosition = true;
                    conn.LivePlayerPosFixedX = lastFixedX;
                    conn.LivePlayerPosFixedY = lastFixedY;
                    conn.LivePlayerPosFixedZ = lastFixedZ;
                    conn.LivePlayerHeadingFixed = lastHeadingFixed;
                    conn.LivePlayerMovingThisFrame = (lastMoveType & 0x01) == 0;
                    if (conn.UseTargetMovingActive && !conn.UseTargetMovingActionMirrored)
                        SyncUseTargetMovingFixedState(conn);

                    CheckPendingPortalActivation(conn);
                    if (!conn.AllowFlush)
                        return;
                    if (useTargetMovingActive && conn.UseTargetMovingActive && !conn.UseTargetMovingActionMirrored)
                        conn.UseTargetMovingClientDriven = true;
                }
                byte[] rawMoveData = reader.GetRawBytes(rawStartPos, rawEndPos - rawStartPos);
                byte peerMoveCount = moveCount;
                byte[] peerMoveData = rawMoveData;

                conn.SessionID = sessionId;

                SendLocalPlayerMovementAck(conn, sessionId, moveCount, rawMoveData);
                BroadcastPlayerMovement(conn, sessionId, peerMoveCount, peerMoveData);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MOVE] state=failed message='{ex.Message}'");
            }
        }

        private const int UnitMoverUpdateRecordSize = 13;

        private static int Fixed32Multiply(int left, int right)
        {
            return unchecked((int)(((long)left * right) >> 8));
        }

        private static int Fixed32Divide(int numerator, int denominator)
        {
            if (denominator == 0) return 0;
            return unchecked((int)(((long)numerator << 8) / denominator));
        }

        private int ResolveStockUnitOnDeadExperienceF32(Combat.Monster monster)
        {
            if (monster == null) return 0;
            if (monster.FactionID == 0 || monster.FactionID == 2 || monster.FactionID == 1000 || !monster.UnitDescIsAlive)
                return 0;
            if (monster.StockUnitLifespanF32 != -0x100 && monster.StockUnitLifespanF32 != 0
                && monster.StockUnitLifespanTicksRemaining == 0)
                return 0;
            int difficultyF32 = monster.DifficultyF32;
            int experienceValueMultF32 = monster.ExperienceValueMultF32;
            int experienceF32 = Fixed32Multiply(difficultyF32, experienceValueMultF32);
            if (experienceF32 <= 0) return 0;
            int experienceModF32 = GCDatabase.Instance.GetRequiredKnobFixed32("ExperienceMod");
            return Fixed32Multiply(experienceF32, experienceModF32);
        }

        private uint ResolveHeroOnAddExperience(RRConnection conn, Combat.Monster monster, PlayerState playerState, int experienceF32, uint sourceLevel)
        {
            if (monster == null || playerState == null || experienceF32 <= 0) return 0;
            int playerLevel = playerState.Level;
            int stockUnitLevel = unchecked((int)sourceLevel);
            if (playerLevel <= 0 || stockUnitLevel <= playerLevel - 5)
                return 0;

            int effectiveLevel = Math.Min(stockUnitLevel, playerLevel);
            int ratioF32 = Fixed32Divide(unchecked(effectiveLevel << 8), unchecked(playerLevel << 8));
            int appliedF32 = Fixed32Multiply(experienceF32, ratioF32);

            bool isFree = conn != null && !string.IsNullOrEmpty(conn.LoginName) && IsPlayerFree(conn.LoginName);
            if (isFree)
            {
                int freeMultF32 = GCDatabase.Instance.GetRequiredKnobFixed32("FreePlayerExperienceMult");
                appliedF32 = Fixed32Multiply(appliedF32, freeMultF32);
            }

            int experienceMod = checked(playerState.ExperienceModPercent + playerState.GetActiveAttributeModifierValue("EXPMOD"));
            int clientExperience = unchecked(appliedF32 >> 8);
            int scaledExperience = unchecked(clientExperience * experienceMod);
            uint result = scaledExperience > 0 ? unchecked((uint)scaledExperience) : 0u;
            Debug.LogError($"[XP-CLIENT] monster={monster.Name}#{monster.EntityId} experienceF32={experienceF32} difficultyF32={monster.DifficultyF32} experienceValueMultF32={monster.ExperienceValueMultF32} sourceLevel={stockUnitLevel} playerLevel={playerLevel} effectiveLevel={effectiveLevel} ratioF32={ratioF32} expMod={experienceMod} free={isFree} appliedF32={appliedF32} clientExperience={clientExperience} effectiveXP={result}");
            return result;
        }

        private void SendLocalPlayerMovementAck(RRConnection conn, byte sessionId, byte moveCount, byte[] rawMoveData)
        {
            if (conn == null || !conn.IsConnected || conn.UnitBehaviorId == 0) return;
            if (!TryNormalizeUnitMoverUpdateData(moveCount, rawMoveData, out byte safeMoveCount, out byte[] safeMoveData)) return;
            ushort unitBehaviorId = (ushort)conn.UnitBehaviorId;
            byte[] admittedMoveData = (byte[])safeMoveData.Clone();
            conn.MessageQueue.EnqueueDeferred(() => BuildLocalPlayerMovementAck(conn, unitBehaviorId, sessionId, safeMoveCount, admittedMoveData), componentId: unitBehaviorId);
        }

        private byte[] BuildLocalPlayerMovementAck(RRConnection conn, ushort unitBehaviorId, byte sessionId, byte moveCount, byte[] moveData)
        {
            if (conn == null || !conn.IsConnected || unitBehaviorId == 0)
                return null;
            var writer = new LEWriter();
            writer.WriteByte(0x35);
            writer.WriteUInt16(unitBehaviorId);
            writer.WriteByte(0x65);
            writer.WriteByte(sessionId);
            writer.WriteByte(moveCount);
            writer.WriteBytes(moveData);
            if (!TryWriteEntitySynchForComponent(conn, writer, unitBehaviorId, 0x65, EntitySynchInfoContext.MoverAck, "WorldMoverAck"))
                return null;
            return writer.ToArray();
        }

        private static bool TryNormalizeUnitMoverUpdateData(byte moveCount, byte[] rawMoveData, out byte safeMoveCount, out byte[] safeMoveData)
        {
            const int recordSize = 13;
            safeMoveCount = 0;
            safeMoveData = Array.Empty<byte>();
            if (moveCount == 0 || rawMoveData == null || rawMoveData.Length == 0) return false;
            int availableCount = Math.Min(moveCount, rawMoveData.Length / recordSize);
            if (availableCount <= 0) return false;
            int safeBytes = availableCount * recordSize;
            safeMoveCount = (byte)availableCount;
            if (safeBytes == rawMoveData.Length)
            {
                safeMoveData = rawMoveData;
                return true;
            }
            safeMoveData = new byte[safeBytes];
            Buffer.BlockCopy(rawMoveData, 0, safeMoveData, 0, safeBytes);
            return true;
        }

        private static bool TryNormalizePeerUnitMoverUpdateData(byte moveCount, byte[] rawMoveData, out byte safeMoveCount, out byte[] safeMoveData)
        {
            if (!TryNormalizeUnitMoverUpdateData(moveCount, rawMoveData, out safeMoveCount, out safeMoveData))
                return false;
            safeMoveData = (byte[])safeMoveData.Clone();
            for (int index = 0; index < safeMoveCount; index++)
            {
                int offset = index * UnitMoverUpdateRecordSize + 1;
                int heading = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(safeMoveData.AsSpan(offset, sizeof(int)));
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(safeMoveData.AsSpan(offset, sizeof(int)), WrapHeadingFixed(heading));
            }
            return true;
        }

        private static bool TryGetLastUnitMoverUpdateRecord(byte moveCount, byte[] rawMoveData, out byte[] record)
        {
            record = Array.Empty<byte>();
            if (!TryNormalizeUnitMoverUpdateData(moveCount, rawMoveData, out byte safeMoveCount, out byte[] safeMoveData)) return false;
            int offset = (safeMoveCount - 1) * UnitMoverUpdateRecordSize;
            if (offset < 0 || offset + UnitMoverUpdateRecordSize > safeMoveData.Length) return false;
            record = new byte[UnitMoverUpdateRecordSize];
            Buffer.BlockCopy(safeMoveData, offset, record, 0, UnitMoverUpdateRecordSize);
            return true;
        }

    }
}
