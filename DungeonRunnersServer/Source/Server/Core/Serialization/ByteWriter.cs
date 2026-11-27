
using System;
using System.Collections.Generic;
using System.Text;

namespace DungeonRunners.Utilities
{
    public class ByteWriter
    {
        private List<byte> _buffer;

        public int Length => _buffer.Count;

        public ByteWriter()
        {
            _buffer = new List<byte>();
        }

        public ByteWriter(int capacity)
        {
            _buffer = new List<byte>(capacity);
        }

        public void WriteByte(byte value)
        {
            _buffer.Add(value);
        }

        public void WriteBytes(byte[] bytes)
        {
            if (bytes == null) return;
            _buffer.AddRange(bytes);
        }

        public void WriteUInt16(ushort value)
        {
            _buffer.Add((byte)(value & 0xFF));
            _buffer.Add((byte)((value >> 8) & 0xFF));
        }

        public void WriteInt16(short value)
        {
            WriteUInt16((ushort)value);
        }

        public void WriteUInt24(uint value)
        {
            _buffer.Add((byte)(value & 0xFF));
            _buffer.Add((byte)((value >> 8) & 0xFF));
            _buffer.Add((byte)((value >> 16) & 0xFF));
        }

        public void WriteUInt32(uint value)
        {
            _buffer.Add((byte)(value & 0xFF));
            _buffer.Add((byte)((value >> 8) & 0xFF));
            _buffer.Add((byte)((value >> 16) & 0xFF));
            _buffer.Add((byte)((value >> 24) & 0xFF));
        }

        public void WriteInt32(int value)
        {
            WriteUInt32((uint)value);
        }

        public void WriteUInt64(ulong value)
        {
            _buffer.Add((byte)(value & 0xFF));
            _buffer.Add((byte)((value >> 8) & 0xFF));
            _buffer.Add((byte)((value >> 16) & 0xFF));
            _buffer.Add((byte)((value >> 24) & 0xFF));
            _buffer.Add((byte)((value >> 32) & 0xFF));
            _buffer.Add((byte)((value >> 40) & 0xFF));
            _buffer.Add((byte)((value >> 48) & 0xFF));
            _buffer.Add((byte)((value >> 56) & 0xFF));
        }

        public void WriteInt64(long value)
        {
            WriteUInt64((ulong)value);
        }

        public void WriteFloat(float value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            WriteBytes(bytes);
        }

        public void WriteString(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                WriteUInt16(0);
                return;
            }

            byte[] stringBytes = Encoding.UTF8.GetBytes(value);
            WriteUInt16((ushort)stringBytes.Length);
            WriteBytes(stringBytes);
        }

        public void WriteFixedString(string value, int length)
        {
            byte[] stringBytes = new byte[length];
            if (!string.IsNullOrEmpty(value))
            {
                byte[] valueBytes = Encoding.UTF8.GetBytes(value);
                int copyLength = Math.Min(valueBytes.Length, length);
                Array.Copy(valueBytes, stringBytes, copyLength);
            }
            WriteBytes(stringBytes);
        }

        public byte[] ToArray()
        {
            return _buffer.ToArray();
        }

        public void WriteBool(bool value)
        {
            _buffer.Add(value ? (byte)1 : (byte)0);
        }

        public void Clear()
        {
            _buffer.Clear();
        }
    }
}
