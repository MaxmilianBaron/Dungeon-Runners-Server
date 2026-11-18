using System;
using System.Collections.Generic;
using DungeonRunners.Data;

namespace DungeonRunners.Networking
{
    public partial class GameServer
    {
        private readonly struct PendingHeroStatInput
        {
            public readonly ushort AvatarId;
            public readonly string InstanceKey;
            public readonly byte SubType;
            public readonly byte StatType;
            public readonly byte NumPoints;
            public readonly int Strength;
            public readonly int Agility;
            public readonly int Endurance;
            public readonly int Intellect;

            public PendingHeroStatInput(RRConnection conn, ushort avatarId, byte subType, byte statType, byte numPoints, SavedCharacter saved)
            {
                AvatarId = avatarId;
                InstanceKey = ResolveConnectionInstanceKey(conn);
                SubType = subType;
                StatType = statType;
                NumPoints = numPoints;
                Strength = saved.statStrength;
                Agility = saved.statAgility;
                Endurance = saved.statEndurance;
                Intellect = saved.statIntellect;
            }
        }

        private readonly Dictionary<int, Queue<PendingHeroStatInput>> _queuedHeroStatInputs = new();
        private readonly Dictionary<int, Dictionary<(uint Tick, int Index), PendingHeroStatInput>> _pendingHeroStatInputs = new();

        private void QueueHeroStatInput(RRConnection conn, ushort avatarId, byte subType, byte statType, byte numPoints, SavedCharacter saved)
        {
            if (!_queuedHeroStatInputs.TryGetValue(conn.ConnId, out Queue<PendingHeroStatInput> queue))
            {
                queue = new Queue<PendingHeroStatInput>();
                _queuedHeroStatInputs.Add(conn.ConnId, queue);
            }
            queue.Enqueue(new PendingHeroStatInput(conn, avatarId, subType, statType, numPoints, saved));
        }

        private void MarkPendingHeroStatInputsFlushed(RRConnection conn, IReadOnlyList<byte[]> messages, IReadOnlyList<ClientEntityMessageRole> roles, uint tick)
        {
            if (conn == null || messages == null || roles == null || messages.Count != roles.Count)
                return;
            for (int index = 0; index < roles.Count; index++)
            {
                if (roles[index] != ClientEntityMessageRole.OwnerHeroStatUpdate)
                    continue;
                if (!_queuedHeroStatInputs.TryGetValue(conn.ConnId, out Queue<PendingHeroStatInput> queue) || !queue.TryPeek(out PendingHeroStatInput input))
                    throw new InvalidOperationException("Hero stat response has no pending allocation");
                byte[] message = messages[index];
                if (message == null || message.Length < 4 || message[0] != 0x03
                    || (ushort)(message[1] | message[2] << 8) != input.AvatarId || message[3] != input.SubType
                    || (input.SubType != 0x13 && (message.Length < 6 || message[4] != input.StatType || message[5] != input.NumPoints)))
                    throw new InvalidOperationException("Hero stat response differs from its pending allocation");
                queue.Dequeue();
                if (queue.Count == 0)
                    _queuedHeroStatInputs.Remove(conn.ConnId);
                if (!_pendingHeroStatInputs.TryGetValue(conn.ConnId, out var pending))
                {
                    pending = new Dictionary<(uint Tick, int Index), PendingHeroStatInput>();
                    _pendingHeroStatInputs.Add(conn.ConnId, pending);
                }
                pending.Add((tick, index), input);
            }
        }

        private void ApplyPendingHeroStatInput(int connId, uint tick, int index)
        {
            if (!_pendingHeroStatInputs.TryGetValue(connId, out var pending)
                || !pending.Remove((tick, index), out PendingHeroStatInput input))
                return;
            if (pending.Count == 0)
                _pendingHeroStatInputs.Remove(connId);
            if (!_connections.TryGetValue(connId, out RRConnection conn)
                || !IsPendingConnectionInstanceCurrent(conn, input.InstanceKey)
                || GetPlayerAvatarId(conn.LoginName) != input.AvatarId)
                return;
            PlayerState playerState = GetPlayerState(connId.ToString());
            if (playerState == null)
                return;
            playerState.StageAllocatedStats(input.Strength, input.Agility, input.Endurance, input.Intellect);
        }

        private void ClearPendingHeroStatInputs(RRConnection conn)
        {
            _queuedHeroStatInputs.Remove(conn.ConnId);
            _pendingHeroStatInputs.Remove(conn.ConnId);
        }
    }
}
