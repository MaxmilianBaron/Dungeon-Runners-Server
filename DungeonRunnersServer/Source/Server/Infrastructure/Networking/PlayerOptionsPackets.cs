using System;
using DungeonRunners.Networking.EntitySynchInfo;
using DungeonRunners.Utilities;

namespace DungeonRunners.Networking
{
    public static class PlayerOptionsPackets
    {
        public static bool TryReadMinimumItemQuality(LEReader reader, ushort entityId, uint ownerPlayerId, out int quality)
        {
            quality = 0;
            if (reader == null || reader.Remaining < 4)
                return false;
            quality = reader.ReadInt32();
            return entityId != 0 && ownerPlayerId == entityId && quality >= 1 && quality <= 7;
        }

        public static byte[] BuildMinimumItemQualityUpdate(ushort playerId, int quality)
        {
            if (playerId == 0 || quality < 1 || quality > 7)
                throw new ArgumentOutOfRangeException();
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x03);
            writer.WriteUInt16(playerId);
            writer.WriteByte(0x04);
            writer.WriteInt32(quality);
            EntitySynchInfoPayload.Empty.Write(writer);
            writer.WriteByte(0x06);
            return writer.ToArray();
        }
    }
}
