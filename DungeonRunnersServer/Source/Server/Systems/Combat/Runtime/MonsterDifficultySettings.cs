using System;

namespace DungeonRunners.Combat
{
    public static class MonsterDifficultySettings
    {
        private static readonly ushort[] Health = { 0x100, 0x180, 0x200, 0x280 };
        private static readonly ushort[] Damage = { 0x100, 0x135, 0x168, 0x1cf };
        private static readonly ushort[] Loot = { 0x100, 0x180, 0x200, 0x280 };

        public static void ApplyInitial(Monster monster, byte difficulty)
        {
            if (monster == null || difficulty > 3)
                throw new ArgumentOutOfRangeException();
            monster.ServerReconstructionDifficulty = difficulty;
            monster.HealthFactorF32 = Health[difficulty];
            monster.DamageFactorF32 = Damage[difficulty];
            monster.LootFactorF32 = Loot[difficulty];
            monster.MaxHPWire = ScaleHealthWire(monster.MaxHPWire, monster.HealthFactorF32);
            monster.CurrentHPWire = monster.MaxHPWire;
        }

        public static uint ScaleHealthWire(uint authoredHealthWire, ushort factorF32)
        {
            int scaled = unchecked((int)(((long)unchecked((int)authoredHealthWire) * factorF32) >> 8));
            return unchecked((uint)((scaled >> 8) << 8));
        }

        public static int ScaleWeaponDamageMod(int percent, ushort factorF32)
        {
            int fixedPercent = Math.Max(0, unchecked(percent << 8));
            return (ushort)(unchecked((uint)(fixedPercent * factorF32)) >> 16);
        }

        public static int ScaleLootRollCount(int count, ushort factorF32, Func<int> fractionalRoll)
        {
            if (count < 0 || factorF32 < 0x100 || factorF32 > 0x280)
                throw new ArgumentOutOfRangeException();
            long scaled = (long)count * factorF32;
            int rolls = checked((int)(scaled >> 8));
            int remainder = (int)(scaled & 255);
            if (remainder != 0 && fractionalRoll() < remainder)
                rolls = checked(rolls + 1);
            return rolls;
        }
    }
}
