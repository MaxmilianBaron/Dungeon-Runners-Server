using System;
using DungeonRunners.Engine;
using DungeonRunners.Utilities;

namespace DungeonRunners.Networking
{
    public static class GroupPackets
    {
        public static byte[] BuildProcessConnected(uint selfCharSqlId,
            byte difficulty, byte inviteMode)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x09);
            writer.WriteByte(0x30);
            writer.WriteUInt32(selfCharSqlId);
            writer.WriteByte(inviteMode);
            writer.WriteByte(difficulty);
            return writer.ToArray();
        }

        public static byte[] BuildProcessUserChangedGroup(uint groupId, uint leaderCharSqlId,
            byte difficulty, byte isOpenGroup, uint selfEntityId1, uint selfEntityId2,
            byte memberCount, GroupMemberInfo[] members = null)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x09);
            writer.WriteByte(0x35);
            writer.WriteUInt32(groupId);
            writer.WriteUInt32(leaderCharSqlId);
            writer.WriteByte(difficulty);
            writer.WriteByte(isOpenGroup);
            writer.WriteUInt32(selfEntityId1);
            writer.WriteUInt32(selfEntityId2);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            writer.WriteByte(memberCount);
            if (members != null)
            {
                foreach (var m in members)
                    WriteMember(writer, m);
            }
            return writer.ToArray();
        }

        public static byte[] BuildProcessInvitation(uint inviteId, uint groupId, string inviterName, byte flags)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x09);
            writer.WriteByte(0x32);
            writer.WriteUInt32(inviteId);
            writer.WriteUInt32(groupId);
            writer.WriteCString(inviterName);
            writer.WriteByte(flags);
            return writer.ToArray();
        }

        public static byte[] BuildProcessUnvitation(uint inviteId)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x09);
            writer.WriteByte(0x33);
            writer.WriteUInt32(inviteId);
            return writer.ToArray();
        }

        public static byte[] BuildInviteResult(byte subType, string playerName)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x09);
            writer.WriteByte(0x34);
            writer.WriteByte(subType);
            writer.WriteCString(playerName);
            return writer.ToArray();
        }

        public static byte[] BuildProcessAddUser(uint groupId, GroupMemberInfo member)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x09);
            writer.WriteByte(0x42);
            writer.WriteUInt32(groupId);
            WriteMember(writer, member);
            return writer.ToArray();
        }

        public static byte[] BuildProcessRemoveUser(uint groupId, uint charId)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x09);
            writer.WriteByte(0x43);
            writer.WriteUInt32(groupId);
            writer.WriteUInt32(charId);
            return writer.ToArray();
        }

        public static byte[] BuildProcessSetLeader(uint groupId, uint newLeaderCharId, byte difficulty)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x09);
            writer.WriteByte(0x44);
            writer.WriteUInt32(groupId);
            writer.WriteUInt32(newLeaderCharId);
            writer.WriteByte(difficulty);
            return writer.ToArray();
        }

        public static byte[] BuildChangedInviteMode(byte mode)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x09);
            writer.WriteByte(0x45);
            writer.WriteByte(mode);
            return writer.ToArray();
        }

        public static byte[] BuildMemberHealthMana(uint charId, byte hpFraction15, byte mpFraction15)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x09);
            writer.WriteByte(0x4B);
            writer.WriteUInt32(charId);
            byte packed = (byte)(((hpFraction15 & 0xF) << 4) | (mpFraction15 & 0xF));
            writer.WriteByte(packed);
            return writer.ToArray();
        }

        public static byte[] BuildUserChangedZone(uint charSQLID, string zoneName)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x09);
            writer.WriteByte(0x4C);
            writer.WriteUInt32(charSQLID);
            writer.WriteCString(zoneName);
            return writer.ToArray();
        }

        public static byte[] BuildMemberDisconnected(uint groupId, uint charId)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x09);
            writer.WriteByte(0x49);
            writer.WriteUInt32(groupId);
            writer.WriteUInt32(charId);
            return writer.ToArray();
        }

        public static byte[] BuildMemberReconnected(uint groupId, uint charId)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x09);
            writer.WriteByte(0x4A);
            writer.WriteUInt32(groupId);
            writer.WriteUInt32(charId);
            return writer.ToArray();
        }

        public static byte[] BuildMonsterDifficulty(byte difficulty, bool personalOnly)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x09);
            writer.WriteByte(0x55);
            writer.WriteByte(difficulty);
            writer.WriteByte((byte)(personalOnly ? 1 : 0));
            return writer.ToArray();
        }

        private static void WriteMember(LEWriter writer, GroupMemberInfo m)
        {
            writer.WriteUInt32(m.CharSQLID);
            writer.WriteCString(m.Name);
            writer.WriteUInt32(m.AvatarEntityId);
            writer.WriteByte((byte)(m.IsOnline ? 1 : 0));
        }
    }

    public class GroupMemberInfo
    {
        public uint CharSQLID;
        public string Name;
        public uint AvatarEntityId;
        public bool IsOnline;
    }
}
