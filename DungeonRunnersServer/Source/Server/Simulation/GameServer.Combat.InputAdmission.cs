using System;
using System.Collections.Generic;

namespace DungeonRunners.Networking
{
    public partial class GameServer
    {
        private readonly struct PendingClientActionInputBatch
        {
            public readonly RRConnection Connection;
            public readonly IReadOnlyList<byte[]> Messages;
            public readonly uint PacketWriterTick;
            public readonly uint ClientEntityUpdateSerial;
            public readonly uint AvatarId;
            public readonly string InstanceKey;
            public readonly List<(int MessageIndex, ushort ComponentId, byte Generation)> BehaviorCancels;
            public readonly List<(int MessageIndex, ushort ComponentId, byte[] Message)> OwnerMovement;
            public readonly List<(int MessageIndex, PendingGroupGotoWarpInput Input)> OwnerWarps;
            public readonly int OwnerActivateMessageIndex;
            public readonly ushort OwnerActivateTarget;

            public int ConnId => Connection.ConnId;
            public int MessageCount => Messages.Count;

            public PendingClientActionInputBatch(
                RRConnection conn, IReadOnlyList<byte[]> messages, uint packetWriterTick, uint clientEntityUpdateSerial,
                List<(int MessageIndex, ushort ComponentId, byte Generation)> behaviorCancels,
                List<(int MessageIndex, ushort ComponentId, byte[] Message)> ownerMovement,
                List<(int MessageIndex, PendingGroupGotoWarpInput Input)> ownerWarps,
                int ownerActivateMessageIndex, ushort ownerActivateTarget)
            {
                Connection = conn;
                Messages = messages;
                PacketWriterTick = packetWriterTick;
                ClientEntityUpdateSerial = clientEntityUpdateSerial;
                AvatarId = conn.Avatar?.Id ?? 0;
                InstanceKey = behaviorCancels != null || ownerMovement != null || ownerWarps != null || ownerActivateMessageIndex >= 0
                    ? ResolveConnectionInstanceKey(conn)
                    : null;
                BehaviorCancels = behaviorCancels;
                OwnerMovement = ownerMovement;
                OwnerWarps = ownerWarps;
                OwnerActivateMessageIndex = ownerActivateMessageIndex;
                OwnerActivateTarget = ownerActivateTarget;
            }
        }

        private readonly Queue<PendingClientActionInputBatch> _pendingClientActionInputBatches = new Queue<PendingClientActionInputBatch>();
        private PendingClientActionInputBatch? _currentClientActionInputBatch;
        private int _currentClientActionMessageIndex;
        private readonly Dictionary<(int ConnId, uint Tick, int Index), uint> _pendingSummonInputMessages = new();
        private readonly HashSet<uint> _scheduledSummonInputEntities = new();

        private void MarkPendingSummonInitializationsFlushed(RRConnection conn, IReadOnlyList<byte[]> messages, uint tick)
        {
            for (int index = 0; index < messages.Count; index++)
            {
                byte[] message = messages[index];
                if (message == null || message.Length < 3 || message[0] != 0x08)
                    continue;
                uint entityId = (uint)(message[1] | message[2] << 8);
                if (Combat.CombatRuntime.Instance.IsSummonInitializationPending(entityId) && _scheduledSummonInputEntities.Add(entityId))
                    _pendingSummonInputMessages.Add((conn.ConnId, tick, index), entityId);
            }
        }

        private void QueuePendingClientActionInputBatch(RRConnection conn, IReadOnlyList<byte[]> messages, uint packetWriterTick, uint clientEntityUpdateSerial)
        {
            if (conn == null || messages == null || messages.Count == 0)
                return;

            List<(int MessageIndex, ushort ComponentId, byte Generation)> behaviorCancels = null;
            List<(int MessageIndex, ushort ComponentId, byte[] Message)> ownerMovement = null;
            List<(int MessageIndex, PendingGroupGotoWarpInput Input)> ownerWarps = null;
            int ownerActivateMessageIndex = -1;
            ushort ownerActivateTarget = conn.UseTargetMovingActive && conn.UseTargetMovingActivate && ResolveCanonicalUnitFollowClientViewer(conn) == null
                ? conn.UseTargetMovingActivateTargetId
                : (ushort)0;
            for (int index = 0; index < messages.Count; index++)
            {
                byte[] message = messages[index];
                if (message == null || message.Length < 5 || message[0] != 0x35)
                    continue;
                ushort componentId = (ushort)(message[1] | (message[2] << 8));
                if (componentId == 0 || componentId != conn.UnitBehaviorId)
                    continue;
                if (message[3] == 0x03)
                {
                    behaviorCancels ??= new List<(int, ushort, byte)>();
                    behaviorCancels.Add((index, componentId, message[4]));
                }
                else if (message[3] == 0x65 && MessageQueue.CountUnitMoverRecords(message, componentId) > 0)
                {
                    ownerMovement ??= new List<(int, ushort, byte[])>();
                    ownerMovement.Add((index, componentId, message));
                }
                else if (message[3] == 0x04 && message[4] == 0x11
                    && _groupGotoWarpMessages.TryGetValue(message, out PendingGroupGotoWarpInput input))
                {
                    ownerWarps ??= new List<(int, PendingGroupGotoWarpInput)>();
                    ownerWarps.Add((index, input));
                    _groupGotoWarpMessages.Remove(message);
                }
                else if (ownerActivateTarget != 0 && ownerActivateMessageIndex < 0
                    && IsPlayerActivateActionResponseMessage(message, componentId, ownerActivateTarget))
                {
                    ownerActivateMessageIndex = index;
                }
            }
            _pendingClientActionInputBatches.Enqueue(new PendingClientActionInputBatch(
                conn, messages, packetWriterTick, clientEntityUpdateSerial, behaviorCancels, ownerMovement,
                ownerWarps, ownerActivateMessageIndex, ownerActivateTarget));
        }

        private bool IsPendingOwnerInputCurrent(PendingClientActionInputBatch batch)
        {
            RRConnection conn = batch.Connection;
            return _connections.TryGetValue(batch.ConnId, out RRConnection current)
                && ReferenceEquals(conn, current)
                && conn.Avatar?.Id == batch.AvatarId
                && IsPendingConnectionInstanceCurrent(conn, batch.InstanceKey);
        }

        private void ApplyPendingSelfCastBehaviorCancel(PendingClientActionInputBatch batch, ushort componentId, byte generation)
        {
            if (_pendingSelfCastBehaviorActions.TryGetValue(batch.ConnId, out PendingSelfCastAction pending)
                && pending.ComponentId == componentId
                && pending.ActionResponseId == generation
                && pending.PlayerEntityId == batch.AvatarId
                && ReferenceEquals(pending.Conn, batch.Connection)
                && _connections.TryGetValue(batch.ConnId, out RRConnection conn)
                && ReferenceEquals(conn, batch.Connection)
                && conn.UnitBehaviorId == componentId
                && IsPendingConnectionInstanceCurrent(conn, batch.InstanceKey)
                && IsSelfCastActionCurrent(pending))
            {
                DiscardPendingSelfCastBehaviorAction(conn);
            }
        }

        private bool IsPendingClientActionMessageSelected(int connId, uint packetWriterTick, int messageIndex)
        {
            if (!_currentClientActionInputBatch.HasValue)
                return true;
            PendingClientActionInputBatch batch = _currentClientActionInputBatch.Value;
            return batch.ConnId == connId
                && batch.PacketWriterTick == packetWriterTick
                && _currentClientActionMessageIndex == messageIndex;
        }

        private void ApplyPendingClientActionInputBatches(uint simulationTick)
        {
            if (_currentClientActionInputBatch.HasValue)
                throw new InvalidOperationException("Client action input admission cannot be reentered");
            while (_pendingClientActionInputBatches.TryPeek(out PendingClientActionInputBatch batch)
                && batch.PacketWriterTick <= simulationTick)
            {
                _pendingClientActionInputBatches.Dequeue();
                _currentClientActionInputBatch = batch;
                try
                {
                    int cancelIndex = 0;
                    int movementIndex = 0;
                    int warpIndex = 0;
                    for (_currentClientActionMessageIndex = 0; _currentClientActionMessageIndex < batch.MessageCount; _currentClientActionMessageIndex++)
                    {
                        if (_pendingSummonInputMessages.Remove((batch.ConnId, batch.PacketWriterTick, _currentClientActionMessageIndex), out uint summonEntityId))
                        {
                            _scheduledSummonInputEntities.Remove(summonEntityId);
                            Combat.CombatRuntime.Instance.AdmitSummonInitialization(summonEntityId, simulationTick);
                        }
                        ApplyPendingHeroStatInput(batch.ConnId, batch.PacketWriterTick, _currentClientActionMessageIndex);
                        byte[] message = batch.Messages[_currentClientActionMessageIndex];
                        _unitContainer?.ApplyPendingConsumableModifierAdmission(batch.Connection, message, simulationTick, _currentClientActionMessageIndex);
                        ApplyPendingHotbarPassiveAdmission(batch.Connection, message, simulationTick, _currentClientActionMessageIndex);
                        _equipment?.ApplyPendingAdmission(batch.Connection, message);
                        ApplyPendingPlayerUseTargetActionInputs(simulationTick);
                        ApplyPendingPlayerUsePositionActionInputs(simulationTick);
                        ApplyPendingSelfCastActionInputs(simulationTick);
                        ApplyPendingOwnerClientControlInputs(simulationTick);
                        ApplyPendingPlayerCancelActionInputs(simulationTick);
                        if (batch.OwnerWarps != null && warpIndex < batch.OwnerWarps.Count
                            && batch.OwnerWarps[warpIndex].MessageIndex == _currentClientActionMessageIndex)
                            AdmitOwnerGroupGotoWarp(batch, batch.OwnerWarps[warpIndex++].Input, simulationTick);
                        if (batch.BehaviorCancels != null && cancelIndex < batch.BehaviorCancels.Count
                            && batch.BehaviorCancels[cancelIndex].MessageIndex == _currentClientActionMessageIndex)
                        {
                            var cancel = batch.BehaviorCancels[cancelIndex++];
                            ApplyPendingSelfCastBehaviorCancel(batch, cancel.ComponentId, cancel.Generation);
                        }
                        if (batch.OwnerMovement != null && movementIndex < batch.OwnerMovement.Count
                            && batch.OwnerMovement[movementIndex].MessageIndex == _currentClientActionMessageIndex)
                        {
                            var movement = batch.OwnerMovement[movementIndex++];
                            RRConnection conn = batch.Connection;
                            if (conn.UnitBehaviorId == movement.ComponentId && IsPendingOwnerInputCurrent(batch))
                            {
                                AdmitOwnerAckFollowClientRecords(conn, movement.Message, simulationTick, batch.ClientEntityUpdateSerial);
                            }
                        }
                        if (batch.OwnerActivateMessageIndex == _currentClientActionMessageIndex
                            && IsPendingOwnerInputCurrent(batch))
                        {
                            RRConnection conn = batch.Connection;
                            if (conn.UseTargetMovingActive && conn.UseTargetMovingActivate
                                && conn.UseTargetMovingActivateTargetId == batch.OwnerActivateTarget)
                            {
                                AdmitPlayerActivateAction(conn, batch.PacketWriterTick, _currentClientActionMessageIndex, conn.UnitFollowClientReceivedOrdinal);
                            }
                        }
                    }
                }
                finally
                {
                    _currentClientActionInputBatch = null;
                    _currentClientActionMessageIndex = 0;
                }
            }
        }
    }
}
