using System;
using System.Text;

namespace DungeonRunners.Utilities
{
    public class LEReader
    {
        private readonly byte[] _data;
        private int _position;

        public LEReader(byte[] data)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
            _position = 0;
        }

        public int Position => _position;
        public int Length => _data.Length;
        public int Remaining => _data.Length - _position;
        public bool HasData => _position < _data.Length;

        public byte ReadByte()
        {
            if (_position >= _data.Length)
                throw new InvalidOperationException("Attempted to read beyond end of data");
            return _data[_position++];
        }

        public byte[] ReadBytes(int count)
        {
            if (_position + count > _data.Length)
                throw new InvalidOperationException($"Attempted to read {count} bytes but only {Remaining} remaining");
            byte[] result = new byte[count];
            Array.Copy(_data, _position, result, 0, count);
            _position += count;
            return result;
        }

        public ushort ReadUInt16()
        {
            if (_position + 2 > _data.Length)
                throw new InvalidOperationException("Not enough data to read UInt16");
            ushort value = (ushort)(_data[_position] | (_data[_position + 1] << 8));
            _position += 2;
            return value;
        }

        public uint ReadUInt24()
        {
            if (_position + 3 > _data.Length)
                throw new InvalidOperationException("Not enough data to read UInt24");
            uint value = (uint)(_data[_position] | (_data[_position + 1] << 8) | (_data[_position + 2] << 16));
            _position += 3;
            return value;
        }

        public byte[] PeekRemaining()
        {
            byte[] remaining = new byte[_data.Length - _position];
            Array.Copy(_data, _position, remaining, 0, remaining.Length);
            return remaining;
        }

        public byte[] GetRawBytes(int startPos, int length)
        {
            byte[] result = new byte[length];
            Array.Copy(_data, startPos, result, 0, length);
            return result;
        }

        public uint ReadUInt32()
        {
            if (_position + 4 > _data.Length)
                throw new InvalidOperationException("Not enough data to read UInt32");
            uint value = (uint)(_data[_position] | (_data[_position + 1] << 8) |
                               (_data[_position + 2] << 16) | (_data[_position + 3] << 24));
            _position += 4;
            return value;
        }

        public int ReadInt32()
        {
            return (int)ReadUInt32();
        }

        public ulong ReadUInt64()
        {
            if (_position + 8 > _data.Length)
                throw new InvalidOperationException("Not enough data to read UInt64");
            ulong value = (ulong)_data[_position] |
                         ((ulong)_data[_position + 1] << 8) |
                         ((ulong)_data[_position + 2] << 16) |
                         ((ulong)_data[_position + 3] << 24) |
                         ((ulong)_data[_position + 4] << 32) |
                         ((ulong)_data[_position + 5] << 40) |
                         ((ulong)_data[_position + 6] << 48) |
                         ((ulong)_data[_position + 7] << 56);
            _position += 8;
            return value;
        }

        public float ReadFloat()
        {
            if (_position + 4 > _data.Length)
                throw new InvalidOperationException("Not enough data to read Float");
            byte[] bytes = new byte[4];
            Array.Copy(_data, _position, bytes, 0, 4);
            if (!BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            _position += 4;
            return BitConverter.ToSingle(bytes, 0);
        }

        public string ReadString()
        {
            ushort length = ReadUInt16();
            if (length == 0) return string.Empty;
            if (_position + length > _data.Length)
                throw new InvalidOperationException($"String length {length} exceeds remaining data");
            string result = Encoding.UTF8.GetString(_data, _position, length);
            _position += length;
            return result;
        }

        public string ReadCString()
        {
            int start = _position;
            while (_position < _data.Length && _data[_position] != 0)
                _position++;
            if (_position >= _data.Length)
                throw new InvalidOperationException("C-string not null-terminated");
            int length = _position - start;
            string result = length > 0 ? Encoding.UTF8.GetString(_data, start, length) : string.Empty;
            _position++;
            return result;
        }

        public void Skip(int count)
        {
            if (_position + count > _data.Length)
                throw new InvalidOperationException($"Cannot skip {count} bytes, only {Remaining} remaining");
            _position += count;
        }

        public void Seek(int position)
        {
            if (position < 0 || position > _data.Length)
                throw new ArgumentOutOfRangeException(nameof(position));
            _position = position;
        }
    }
}