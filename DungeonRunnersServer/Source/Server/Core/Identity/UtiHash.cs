namespace DungeonRunners.Utilities
{
    public static class UtiHash
    {
        public static uint Adler32(byte[] data)
        {
            const uint MOD_ADLER = 65521;
            uint a = 1;
            uint b = 0;

            if (data != null)
            {
                foreach (byte c in data)
                {
                    a = (a + c) % MOD_ADLER;
                    b = (b + a) % MOD_ADLER;
                }
            }

            return (b << 16) | a;
        }
    }
}
