using System;
using System.Collections.Generic;

namespace DungeonRunners.Networking
{
    public partial class GameServer
    {
        private readonly struct PendingClientActionInputBatch
        {
            public readonly int ConnId;
            public readonly int MessageCount;
            public readonly uint PacketWriterTick;

            public PendingClientActionInputBatch(int connId, int messageCount, uint packetWriterTick)
            {
                ConnId = connId;
                MessageCount = messageCount;
                PacketWriterTick = packetWriterTick;
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

        private void QueuePendingClientActionInputBatch(RRConnection conn, int messageCount, uint packetWriterTick)
        {
            if (conn == null || messageCount <= 0)
                return;
            _pendingClientActionInputBatches.Enqueue(new PendingClientActionInputBatch(conn.ConnId, messageCount, packetWriterTick));
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
                    for (_currentClientActionMessageIndex = 0; _currentClientActionMessageIndex < batch.MessageCount; _currentClientActionMessageIndex++)
                    {
                        if (_pendingSummonInputMessages.Remove((batch.ConnId, batch.PacketWriterTick, _currentClientActionMessageIndex), out uint summonEntityId))
                        {
                            _scheduledSummonInputEntities.Remove(summonEntityId);
                            Combat.CombatRuntime.Instance.AdmitSummonInitialization(summonEntityId, simulationTick);
                        }
                        ApplyPendingPlayerUseTargetActionInputs(simulationTick);
                        ApplyPendingPlayerUsePositionActionInputs(simulationTick);
                        ApplyPendingSelfCastActionInputs(simulationTick);
                        ApplyPendingOwnerClientControlInputs(simulationTick);
                        ApplyPendingPlayerCancelActionInputs(simulationTick);
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
