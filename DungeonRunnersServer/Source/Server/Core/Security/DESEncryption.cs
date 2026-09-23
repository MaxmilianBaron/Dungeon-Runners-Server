using System;
using System.Security.Cryptography;

namespace DungeonRunners.Utilities
{
    public sealed class DESEncryption : IDisposable
    {
        private readonly DES _des;
        private readonly byte[] _key;
        private readonly byte[] _iv;

        public DESEncryption(string key)
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key));

            _key = new byte[8];
            byte[] keyBytes = System.Text.Encoding.ASCII.GetBytes(key);
            Array.Copy(keyBytes, _key, Math.Min(keyBytes.Length, 8));

            _iv = new byte[8];

            _des = DES.Create();
            _des.Mode = CipherMode.ECB;
            _des.Padding = PaddingMode.Zeros;
        }

        public byte[] Encrypt(byte[] data)
        {
            if (data == null || data.Length == 0)
                return data;

            using (var encryptor = _des.CreateEncryptor(_key, _iv))
            {
                return encryptor.TransformFinalBlock(data, 0, data.Length);
            }
        }

        public byte[] Decrypt(byte[] data)
        {
            if (data == null || data.Length == 0)
                return data;

            using (var decryptor = _des.CreateDecryptor(_key, _iv))
            {
                return decryptor.TransformFinalBlock(data, 0, data.Length);
            }
        }

        public void Dispose()
        {
            _des.Dispose();
        }
    }
}
