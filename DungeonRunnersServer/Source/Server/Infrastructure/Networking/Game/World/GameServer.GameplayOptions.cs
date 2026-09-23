using System;
using DungeonRunners.Combat;
using DungeonRunners.Gameplay;
using DungeonRunners.Utilities;

namespace DungeonRunners.Networking
{
    public partial class GameServer
    {
        private byte ResolveSpawnDifficulty(Monster monster)
        {
            if (monster == null || IsPublicZone(monster.ZoneName))
                return 0;
            string instanceKey = RoomRuntime.NormalizeInstanceKey(monster.InstanceKey);
            foreach (RRConnection candidate in GetConnectionInsertionOrderSnapshot())
                if (candidate != null && candidate.IsConnected
                    && string.Equals(RoomRuntime.NormalizeInstanceKey(candidate.RuntimeInstanceKey), instanceKey, StringComparison.OrdinalIgnoreCase))
                    return (byte)GetDifficultyForConn(candidate);
            return 0;
        }

        private byte GetPersonalDifficultyForConn(RRConnection conn)
        {
            return GetActiveCharacter(conn)?.monsterDifficulty ?? 0;
        }

        private void EnsureGroupConnected(RRConnection conn)
        {
            if (conn.GroupConnectedSent)
                return;
            Group group = GroupDirectory.Instance.GetGroupForConn(conn.ConnId);
            SendToClient(conn, GroupPackets.BuildProcessConnected(
                GetCharSqlId(conn), GetPersonalDifficultyForConn(conn), group?.InviteMode ?? 0));
            conn.GroupConnectedSent = true;
        }

        public void SendSoloGroupState(RRConnection conn)
        {
            uint selfId = GetCharSqlId(conn);
            byte difficulty = GetPersonalDifficultyForConn(conn);
            EnsureGroupConnected(conn);
            SendToClient(conn, GroupPackets.BuildProcessUserChangedGroup(1, selfId, difficulty, 0, 0, 0, 1, new[] { BuildGroupMemberInfo(conn) }));
            SendJoinTalkbackGroup(conn, selfId);
        }

        private void HandleMinimumItemQualityRequest(RRConnection conn, ushort entityId, LEReader reader)
        {
            if (!PlayerOptionsPackets.TryReadMinimumItemQuality(reader, entityId, conn.Player?.Id ?? 0, out int quality))
                return;
            var character = GetActiveCharacter(conn)?.DeepClone();
            if (character == null)
                return;
            bool changed = character.minimumItemQuality != quality;
            character.minimumItemQuality = quality;
            if (changed && !TrySaveCharacterForConn(conn, character, "item-label-quality"))
                return;
            conn.Player.PlayerMinimumItemQuality = quality;
            conn.MessageQueue.Enqueue(PlayerOptionsPackets.BuildMinimumItemQualityUpdate(entityId, quality));
        }

        private void HandleMonsterDifficultyRequest(RRConnection conn, LEReader reader)
        {
            if (reader == null || reader.Remaining < 1)
                return;
            byte difficulty = reader.ReadByte();
            if (difficulty > 3)
                return;
            var character = GetActiveCharacter(conn)?.DeepClone();
            if (character == null)
                return;
            bool changed = character.monsterDifficulty != difficulty;
            character.monsterDifficulty = difficulty;
            if (changed && !TrySaveCharacterForConn(conn, character, "monster-difficulty"))
                return;
            if (conn.Player != null)
                conn.Player.PlayerMonsterDifficulty = difficulty;
            Group group = GroupDirectory.Instance.GetGroupForConn(conn.ConnId);
            if (group == null)
            {
                SendToClient(conn, GroupPackets.BuildMonsterDifficulty(difficulty, false));
                return;
            }
            if (!GroupDirectory.Instance.SetMonsterDifficulty(conn.ConnId, difficulty, out bool personalOnly))
                return;
            byte[] packet = GroupPackets.BuildMonsterDifficulty(difficulty, personalOnly);
            if (personalOnly)
                SendToClient(conn, packet);
            else
                foreach (GroupMember member in group.Members)
                {
                    if (!member.IsOnline)
                        continue;
                    RRConnection recipient = FindConnectionById(member.ConnId);
                    if (recipient != null)
                        SendToClient(recipient, packet);
                }
        }
    }
}
