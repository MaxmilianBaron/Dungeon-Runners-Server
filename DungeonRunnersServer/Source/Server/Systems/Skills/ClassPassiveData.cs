using System;

namespace DungeonRunners.Data
{
    public static class ClassPassiveData
    {
        public const int BASE_ENDURANCE = 10;
        public const int BASE_INTELLECT = 10;
        public const int HERO_HEALTH_PER_LEVEL = 16;
        public const int HEALTH_PER_ENDURANCE = 25;
        public const int POWER_PER_INTELLECT = 17;
        public const int POWER_PER_LEVEL = 5;
        private static uint ClampWire(long wire)
        {
            if (wire <= 0) return 0;
            if (wire >= uint.MaxValue) return uint.MaxValue;
            return (uint)wire;
        }

        public static uint CalculateHPWire(int level, int endurance, int healthPerEnduranceModPercent)
        {
            int clientLevel = Math.Max(1, level);
            int clientEndurance = Math.Max(1, endurance);
            int percent = Math.Max(0, 100 + healthPerEnduranceModPercent);
            int percentFixed = (int)(((long)percent * 0x10000L) / 0x6400L);
            long hpPerEnduranceFixed = ((long)HEALTH_PER_ENDURANCE * 256L * percentFixed) >> 8;
            long enduranceHP = (((long)clientEndurance << 8) * hpPerEnduranceFixed) >> 16;
            long levelHP = (long)clientLevel * HERO_HEALTH_PER_LEVEL;
            return ClampWire((enduranceHP + levelHP) * 256L);
        }

        public static uint CalculateManaWire(int level, int intellect, int manaPerIntellectModPercent)
        {
            int clientLevel = Math.Max(1, level);
            int clientIntellect = Math.Max(1, intellect);
            int percent = Math.Max(0, 100 + manaPerIntellectModPercent);
            int percentFixed = (int)(((long)percent * 0x10000L) / 0x6400L);
            long manaPerIntellectFixed = ((long)POWER_PER_INTELLECT * 256L * percentFixed) >> 8;
            long intellectMana = (((long)clientIntellect << 8) * manaPerIntellectFixed) >> 16;
            long levelMana = (long)clientLevel * POWER_PER_LEVEL;
            return ClampWire((intellectMana + levelMana) * 256L);
        }

    }
}
