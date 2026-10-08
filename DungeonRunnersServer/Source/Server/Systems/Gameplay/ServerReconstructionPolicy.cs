using System;

namespace DungeonRunners.Gameplay
{
    public readonly struct ServerEncounterScale
    {
        public ServerEncounterScale(
            int partySize,
            int difficulty,
            int healthPercent,
            int damagePercent,
            int experiencePercent,
            int goldPercent,
            int itemLevelBonus)
        {
            PartySize = partySize;
            Difficulty = difficulty;
            HealthPercent = healthPercent;
            DamagePercent = damagePercent;
            ExperiencePercent = experiencePercent;
            GoldPercent = goldPercent;
            ItemLevelBonus = itemLevelBonus;
        }

        public int PartySize { get; }
        public int Difficulty { get; }
        public int HealthPercent { get; }
        public int DamagePercent { get; }
        public int ExperiencePercent { get; }
        public int GoldPercent { get; }
        public int ItemLevelBonus { get; }
    }

    public static class ServerReconstructionPolicy
    {
        private static readonly int[] PartyHealthPercent = { 100, 100, 140, 175, 205, 230 };
        private static readonly int[] PartyDamagePercent = { 100, 100, 108, 116, 124, 132 };
        private static readonly int[] DifficultyHealthPercent = { 100, 120, 145, 175 };
        private static readonly int[] DifficultyDamagePercent = { 100, 110, 122, 136 };
        private static readonly int[] DifficultyRewardPercent = { 100, 110, 125, 145 };
        private static readonly int[] DifficultyItemLevelBonus = { 0, 1, 2, 3 };

        public static byte ResolveTierLevelOffset(string tier)
        {
            if (string.IsNullOrWhiteSpace(tier))
                return 1;
            return tier.Trim().ToUpperInvariant() switch
            {
                "FODDER" => 0,
                "RECRUIT" => 1,
                "MINION" => 1,
                "VETERAN" => 2,
                "SPECIAL" => 3,
                "CHAMPION" => 4,
                "UNIQUE" => 5,
                "HERO" => 6,
                "WARMONGER" => 8,
                "BOSS" => 8,
                "UNIQUE_BOSS" => 9,
                "DUNGEON_BOSS" => 10,
                _ => 1
            };
        }

        public static int ResolveTierHealthModifierF32(string tier)
        {
            if (string.IsNullOrWhiteSpace(tier))
                return 0x100;
            return tier.Trim().ToUpperInvariant() switch
            {
                "FODDER" => 0x080,
                "RECRUIT" => 0x100,
                "MINION" => 0x100,
                "VETERAN" => 0x200,
                "WARMONGER" => 0x280,
                "SPECIAL" => 0x300,
                "CHAMPION" => 0x400,
                "UNIQUE" => 0x500,
                "HERO" => 0x700,
                "BOSS" => 0x800,
                "UNIQUE_BOSS" => 0x900,
                "DUNGEON_BOSS" => 0xA00,
                _ => 0x100
            };
        }

        public static ServerEncounterScale ResolveEncounterScale(int partySize, int difficulty)
        {
            int safePartySize = Math.Clamp(partySize, 1, 5);
            int safeDifficulty = Math.Clamp(difficulty, 0, 3);
            int healthPercent = MultiplyPercent(
                PartyHealthPercent[safePartySize],
                DifficultyHealthPercent[safeDifficulty]);
            int damagePercent = MultiplyPercent(
                PartyDamagePercent[safePartySize],
                DifficultyDamagePercent[safeDifficulty]);
            int rewardPercent = DifficultyRewardPercent[safeDifficulty];
            return new ServerEncounterScale(
                safePartySize,
                safeDifficulty,
                healthPercent,
                damagePercent,
                rewardPercent,
                rewardPercent,
                DifficultyItemLevelBonus[safeDifficulty]);
        }

        public static uint ScaleWireValue(uint value, int percent)
        {
            if (value == 0)
                return 0;
            long scaled = ((long)value * Math.Max(0, percent) + 50L) / 100L;
            return scaled >= uint.MaxValue ? uint.MaxValue : Math.Max(1u, (uint)scaled);
        }

        public static int ScaleNonNegative(int value, int percent)
        {
            if (value <= 0 || percent <= 0)
                return 0;
            long scaled = ((long)value * percent + 50L) / 100L;
            return scaled >= int.MaxValue ? int.MaxValue : (int)scaled;
        }

        public static int ScaleItemLevel(int itemLevel, int bonus)
        {
            return Math.Clamp(itemLevel + Math.Max(0, bonus), 1, 110);
        }

        private static int MultiplyPercent(int left, int right)
        {
            return checked((int)(((long)left * right + 50L) / 100L));
        }
    }
}
