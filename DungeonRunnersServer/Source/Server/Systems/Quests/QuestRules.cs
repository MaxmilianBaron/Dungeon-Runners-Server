using System;
using System.Linq;
using DungeonRunners.Engine;
using DungeonRunners.Data;

namespace DungeonRunners.Gameplay
{
    public static class QuestRules
    {
        public static int GetRewardLevel(int playerLevel, int minimum, int maximum)
        {
            return playerLevel < minimum ? minimum : playerLevel > maximum ? maximum : playerLevel;
        }

        public static int GetCashReward(int rewardLevel, int minimum, int cashMultiplierFixed8, int goldPerLevelFixed8)
        {
            int levelFixed8 = unchecked(Math.Min(rewardLevel, minimum) << 8);
            int scaled = unchecked((int)(((long)cashMultiplierFixed8 * levelFixed8) >> 8));
            int result = unchecked((int)(((long)scaled * goldPerLevelFixed8) >> 8));
            return result >> 8;
        }

        public static int GetRewardItemLevel(int rewardLevel, int levelOverride, bool generated)
        {
            return generated ? unchecked((byte)(rewardLevel + 5))
                : unchecked((byte)(levelOverride != 0 ? levelOverride : rewardLevel));
        }

        public static int ScaleTokenRequirement(int required, int multiplierFixed8)
        {
            int scaled = checked(required * multiplierFixed8);
            if ((scaled & 0xFF) >= 0x7F)
                scaled = checked(scaled + 0x100);
            return scaled >> 8;
        }

        public static bool RepeatWaitElapsed(long now, long acceptedAt, int seconds)
        {
            return now >= acceptedAt && now - acceptedAt >= seconds;
        }

        public static bool MatchesTargetZone(string currentZone, string targetZone)
        {
            return string.IsNullOrEmpty(targetZone) || string.Equals(currentZone, targetZone, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsWithinObjectiveRange(int x, int y, int z, int targetX, int targetY, int targetZ, int range)
        {
            long dx = (long)x - targetX;
            long dy = (long)y - targetY;
            long dz = (long)z - targetZ;
            long radius = (long)range * 256;
            if (range <= 0 || range > 2896 || Math.Abs(dx) >= radius || Math.Abs(dy) >= radius || Math.Abs(dz) >= radius)
                return false;
            long squared = (dx * dx >> 8) + (dy * dy >> 8) + (dz * dz >> 8);
            return squared < (radius * radius >> 8);
        }

        public static bool IsGcType(string actual, string expected)
        {
            return !string.IsNullOrEmpty(actual) && !string.IsNullOrEmpty(expected)
                && (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
                    || GCDatabase.Instance.GetInheritanceChainPaths(actual).Contains(expected, StringComparer.OrdinalIgnoreCase));
        }

        public static bool IsWithinNpcRange(int x, int y, int z, int targetX, int targetY, int targetZ, bool turnIn)
        {
            long dx = (long)x - targetX;
            long dy = (long)y - targetY;
            long dz = turnIn ? (long)z - targetZ : 0;
            int limit = (turnIn ? 120 : 40) * 256;
            if (Math.Abs(dx) > limit || Math.Abs(dy) > limit || Math.Abs(dz) > limit)
                return false;
            long distance = (dx * dx >> 8) + (dy * dy >> 8) + (dz * dz >> 8);
            return turnIn ? distance < 0x384000L : distance <= 0x64000L;
        }
    }
}
