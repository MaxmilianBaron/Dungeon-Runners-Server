using DungeonRunners.Utilities;

namespace DungeonRunners.Networking
{
    public static class TradePackets
    {
        public static byte[] BuildTradeRequested(uint requesterCharId, string requesterName, uint targetCharId, string targetName)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x0A);
            writer.WriteByte(0x00);
            writer.WriteUInt32(requesterCharId);
            writer.WriteCString(requesterName);
            writer.WriteUInt32(targetCharId);
            writer.WriteCString(targetName);
            return writer.ToArray();
        }

        public static byte[] BuildTradeRequestFailed(byte resultCode)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x0A);
            writer.WriteByte(0x01);
            writer.WriteByte(resultCode);
            writer.WriteByte(0x00);
            return writer.ToArray();
        }

        public static byte[] BuildTradeCancelled(byte resultCode, uint actorCharId)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x0A);
            writer.WriteByte(0x03);
            writer.WriteByte(resultCode);
            writer.WriteUInt32(actorCharId);
            writer.WriteUInt32(actorCharId);
            return writer.ToArray();
        }

        public static byte[] BuildTradeAccepted(uint charId, bool accepted)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x0A);
            writer.WriteByte(0x05);
            writer.WriteUInt32(charId);
            writer.WriteByte((byte)(accepted ? 1 : 0));
            return writer.ToArray();
        }

        public static byte[] BuildTradeInitiated(uint partnerCharId, string partnerName)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x0A);
            writer.WriteByte(0x0A);
            writer.WriteByte(0x00);
            writer.WriteUInt32(partnerCharId);
            writer.WriteCString(partnerName);
            writer.WriteByte(0xFF);
            writer.WriteCString("avatar.base.tradeinventory");
            writer.WriteByte(0x0C);
            writer.WriteByte(0x01);
            writer.WriteByte(0x00);
            return writer.ToArray();
        }

        public static byte[] BuildTradeComplete(uint charId)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x0A);
            writer.WriteByte(0x0B);
            writer.WriteUInt32(charId);
            return writer.ToArray();
        }

        public static byte[] BuildItemRemovedFromTrade(uint itemId)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x0A);
            writer.WriteByte(0x08);
            writer.WriteUInt32(itemId);
            return writer.ToArray();
        }
    }
}
