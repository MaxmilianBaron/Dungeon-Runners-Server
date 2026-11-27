using System;
using System.Text;

namespace DungeonRunners.Utilities
{
    public class ByteReader
    {
        private byte[] _buffer;
        private int _position;

        public int Position => _position;
        public int Length => _buffer.Length;
        public int Remaining => _buffer.Length - _position;

        public ByteReader(byte[] buffer)
        {
            _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
            _position = 0;
        }

        public byte ReadByte()
        {
            if (_position >= _buffer.Length)
                throw new InvalidOperationException("Cannot read beyond buffer length");
            return _buffer[_position++];
        }

        public byte[] ReadBytes(int count)
        {
            if (_position + count > _buffer.Length)
                throw new InvalidOperationException("Cannot read beyond buffer length");
            
            byte[] result = new byte[count];
            Array.Copy(_buffer, _position, result, 0, count);
            _position += count;
            return result;
        }

        public ushort ReadUInt16()
        {
            if (_position + 2 > _buffer.Length)
                throw new InvalidOperationException("Cannot read beyond buffer length");
            
            ushort value = (ushort)(_buffer[_position] | (_buffer[_position + 1] << 8));
            _position += 2;
            return value;
        }

        public short ReadInt16()
        {
            return (short)ReadUInt16();
        }

        public uint ReadUInt32()
        {
            if (_position + 4 > _buffer.Length)
                throw new InvalidOperationException("Cannot read beyond buffer length");
            
            uint value = (uint)(_buffer[_position] | 
                               (_buffer[_position + 1] << 8) | 
                               (_buffer[_position + 2] << 16) | 
                               (_buffer[_position + 3] << 24));
            _position += 4;
            return value;
        }

        public int ReadInt32()
        {
            return (int)ReadUInt32();
        }

        public ulong ReadUInt64()
        {
            if (_position + 8 > _buffer.Length)
                throw new InvalidOperationException("Cannot read beyond buffer length");
            
            ulong value = (ulong)_buffer[_position] |
                         ((ulong)_buffer[_position + 1] << 8) |
                         ((ulong)_buffer[_position + 2] << 16) |
                         ((ulong)_buffer[_position + 3] << 24) |
                         ((ulong)_buffer[_position + 4] << 32) |
                         ((ulong)_buffer[_position + 5] << 40) |
                         ((ulong)_buffer[_position + 6] << 48) |
                         ((ulong)_buffer[_position + 7] << 56);
            _position += 8;
            return value;
        }

        public long ReadInt64()
        {
            return (long)ReadUInt64();
        }

        public float ReadFloat()
        {
            byte[] bytes = ReadBytes(4);
            return BitConverter.ToSingle(bytes, 0);
        }

        public string ReadString()
        {
            ushort length = ReadUInt16();
            if (length == 0) return string.Empty;
            
            byte[] stringBytes = ReadBytes(length);
            return Encoding.UTF8.GetString(stringBytes);
        }

        public string ReadFixedString(int length)
        {
            byte[] stringBytes = ReadBytes(length);
            int nullIndex = Array.IndexOf(stringBytes, (byte)0);
            if (nullIndex >= 0)
            {
                return Encoding.UTF8.GetString(stringBytes, 0, nullIndex);
            }
            return Encoding.UTF8.GetString(stringBytes);
        }

        public void Skip(int count)
        {
            if (_position + count > _buffer.Length)
                throw new InvalidOperationException("Cannot skip beyond buffer length");
            _position += count;
        }

        public void Seek(int position)
        {
            if (position < 0 || position > _buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(position));
            _position = position;
        }
    }
}