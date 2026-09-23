using System;
using DungeonRunners.Engine;
using DungeonRunners.Utilities;

namespace DungeonRunners.Networking
{
    public static class PVPPackets
    {

        public enum DuelStatusType : byte
        {
            None = 0,
            Challenged = 1,
            Accepted = 2,
            InProgress = 3,
            Won = 4,
            Lost = 5,
            Declined = 6,
            Cancelled = 7,
        }

        public static byte[] BuildDuelStatus(DuelStatusType status,
            uint opponentCharSqlId, uint opponentEntityHandle, int ratingChange)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x09);
            writer.WriteByte(0x4F);
            writer.WriteUInt32((uint)status);
            writer.WriteUInt32(opponentCharSqlId);
            writer.WriteUInt32(opponentEntityHandle);
            writer.WriteInt32(ratingChange);
            return writer.ToArray();
        }

        public static byte[] BuildPVPStatusChanged(byte pvpState, uint matchId)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x09);
            writer.WriteByte(0x4E);
            writer.WriteUInt32((uint)pvpState);
            writer.WriteUInt32(matchId);
            return writer.ToArray();
        }

        public static byte[] BuildPVPMatchStatus(uint groupId, uint a, uint b, string matchGcPath)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x09);
            writer.WriteByte(0x4E);
            writer.WriteUInt32(groupId);
            writer.WriteUInt32(a);
            writer.WriteUInt32(b);
            if (!string.IsNullOrEmpty(matchGcPath)) { writer.WriteByte(0xFF); writer.WriteCString(matchGcPath); }
            else { writer.WriteByte(0x00); }
            writer.WriteByte(0x00);
            return writer.ToArray();
        }


    }
}
