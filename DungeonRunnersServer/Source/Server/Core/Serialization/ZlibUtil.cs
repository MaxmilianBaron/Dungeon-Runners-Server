using System;
using System.IO;
using System.IO.Compression;
using DungeonRunners.Engine;

namespace DungeonRunners.Utilities
{
    public static class ZlibUtil
    {
        public static byte[] Deflate(byte[] data)
        {
            if (data == null || data.Length == 0)
                return Array.Empty<byte>();

            try
            {
                using (var output = new MemoryStream())
                {
                    output.WriteByte(0x78);
                    output.WriteByte(0x9C);

                    using (var deflate = new DeflateStream(output, CompressionMode.Compress, true))
                    {
                        deflate.Write(data, 0, data.Length);
                    }

                    uint adler = UtiHash.Adler32(data);
                    output.WriteByte((byte)((adler >> 24) & 0xFF));
                    output.WriteByte((byte)((adler >> 16) & 0xFF));
                    output.WriteByte((byte)((adler >> 8) & 0xFF));
                    output.WriteByte((byte)(adler & 0xFF));

                    return output.ToArray();
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ZLIB] action=deflate state=failed message='{ex.Message}'");
                throw;
            }
        }

        public static byte[] Inflate(byte[] data, uint expectedSize = 0)
        {
            if (data == null || data.Length < 6)
                return Array.Empty<byte>();

            try
            {
                using (var input = new MemoryStream(data))
                {
                    int cmf = input.ReadByte();
                    int flg = input.ReadByte();

                    if (cmf == -1 || flg == -1)
                        throw new InvalidDataException("Invalid zlib header");

                    if (((cmf * 256 + flg) % 31) != 0)
                        throw new InvalidDataException("Invalid zlib header checksum");

                    using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
                    {
                        using (var output = new MemoryStream(expectedSize > 0 ? (int)expectedSize : 4096))
                        {
                            deflate.CopyTo(output);
                            return output.ToArray();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ZLIB] action=inflate state=failed message='{ex.Message}'");
                throw;
            }
        }

        public static bool VerifyAdler32(byte[] data, uint checksum)
        {
            return UtiHash.Adler32(data) == checksum;
        }
    }
}
