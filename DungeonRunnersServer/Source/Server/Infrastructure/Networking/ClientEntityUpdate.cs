using System;
using DungeonRunners.Utilities;

namespace DungeonRunners.Networking
{
    public static class ClientEntityUpdate
    {
        private const byte EntityUpdate = 0x03;
        private const byte ComponentUpdate = 0x35;

        public static byte[] Entity(ushort entityId, byte updateId, Action<LEWriter> writePayload, Action<LEWriter> writeEntitySynchInfo)
        {
            return Serialize(EntityUpdate, entityId, updateId, writePayload, writeEntitySynchInfo);
        }

        public static byte[] Component(ushort componentId, byte updateId, Action<LEWriter> writePayload, Action<LEWriter> writeEntitySynchInfo)
        {
            return Serialize(ComponentUpdate, componentId, updateId, writePayload, writeEntitySynchInfo);
        }

        private static byte[] Serialize(byte messageType, ushort objectId, byte updateId, Action<LEWriter> writePayload, Action<LEWriter> writeEntitySynchInfo)
        {
            if (objectId == 0)
                throw new ArgumentOutOfRangeException(nameof(objectId));
            if (writePayload == null)
                throw new ArgumentNullException(nameof(writePayload));
            if (writeEntitySynchInfo == null)
                throw new ArgumentNullException(nameof(writeEntitySynchInfo));

            var writer = new LEWriter();
            writer.WriteByte(messageType);
            writer.WriteUInt16(objectId);
            writer.WriteByte(updateId);
            writePayload(writer);
            writeEntitySynchInfo(writer);
            return writer.ToArray();
        }
    }
}
