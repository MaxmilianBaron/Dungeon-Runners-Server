using System;
using DungeonRunners.Utilities;

namespace DungeonRunners.Networking
{
    internal static class GCTypeCodec
    {
        internal static uint HashString(string value)
        {
            if (value == null)
                throw new ArgumentNullException(nameof(value));

            uint hash = 0x1505;
            foreach (char valueChar in value)
            {
                if (valueChar > 0x7F)
                    throw new ArgumentOutOfRangeException(nameof(value));
                uint foldedChar = valueChar >= 'A' && valueChar <= 'Z'
                    ? (uint)(valueChar + 0x20)
                    : valueChar;
                hash = unchecked(hash * 0x21 + foldedChar);
            }
            return hash == 0 ? 1u : hash;
        }

        internal static void WriteTypeId(LEWriter writer, string value)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException(nameof(value));

            uint typeId = HashString(value);
            if (typeId <= byte.MaxValue)
            {
                writer.WriteByte(0x01);
                writer.WriteByte((byte)typeId);
                return;
            }
            if (typeId <= ushort.MaxValue)
            {
                writer.WriteByte(0x02);
                writer.WriteUInt16((ushort)typeId);
                return;
            }
            writer.WriteByte(0x04);
            writer.WriteUInt32(typeId);
        }

        internal static uint ReadTypeId(LEReader reader)
        {
            if (reader == null)
                throw new ArgumentNullException(nameof(reader));

            byte tag = reader.ReadByte();
            return tag switch
            {
                0x00 => 0,
                0x01 => reader.ReadByte(),
                0x02 => reader.ReadUInt16(),
                0x04 => reader.ReadUInt32(),
                0xFF => HashString(reader.ReadCString()),
                _ => throw new InvalidOperationException($"Invalid GC type tag 0x{tag:X2}")
            };
        }
    }
}
