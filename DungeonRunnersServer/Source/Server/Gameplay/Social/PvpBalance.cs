using DungeonRunners.Data;
using System;
using System.IO;

namespace DungeonRunners.Gameplay
{
    public static class PvpBalance
    {
        public const int DamageRemapLevel = 101;
        public const int MaxHealthReferenceLevel = 100;

        public static uint RemapDamageWire(uint damageWire, int damageModPercent, byte damageKind, int sourceLevel)
        {
            int damage = (int)(damageWire >> 8);
            if (damage <= 0)
                return 0;

            long scaledDamage = ((long)damage * Math.Max(0, damageModPercent)) / 100L;
            if (damageKind >= 1 && damageKind <= 3)
            {
                string curveName = damageKind == 3 ? "MaxSkillDamage" : "MaxWeaponDamage";
                int sourceCurveF32 = GCDatabase.Instance.RequireCurveValueFixed32(curveName, sourceLevel);
                int targetCurveF32 = GCDatabase.Instance.RequireCurveValueFixed32(curveName, DamageRemapLevel);
                if (sourceCurveF32 <= 0 || targetCurveF32 <= 0)
                    throw new InvalidDataException($"Tables.{curveName} contains a non-positive PvP remap value");

                long scaledDamageF32 = scaledDamage << 8;
                long ratioF32 = (scaledDamageF32 << 8) / sourceCurveF32;
                long remappedF32 = ((ratioF32 + 1) * targetCurveF32) >> 8;
                scaledDamage = remappedF32 >> 8;
            }

            if (scaledDamage <= 0)
                return 0;
            if (scaledDamage >= 0xFFFFFFL)
                return uint.MaxValue & 0xFFFFFF00u;
            return (uint)scaledDamage << 8;
        }

        public static int RemapDefenseRating(int defenseRating, int attackerLevel, int defenderLevel)
        {
            if (defenseRating <= 0)
                return 0;

            int averageCurveF32 = GCDatabase.Instance.RequireCurveValueFixed32("AveragePlayerDefenseRating", defenderLevel);
            int pvpCurveF32 = GCDatabase.Instance.RequireCurveValueFixed32("PvPDefenseRating", attackerLevel);
            if (averageCurveF32 <= 0 || pvpCurveF32 <= 0)
                throw new InvalidDataException("PvP defense tables contain a non-positive remap value");

            long ratioF32 = ((long)defenseRating << 16) / averageCurveF32;
            long remappedF32 = ((long)pvpCurveF32 * ratioF32) >> 8;
            long remappedRating = remappedF32 >> 8;
            if (remappedRating <= 0)
                return 0;
            return remappedRating >= int.MaxValue ? int.MaxValue : (int)remappedRating;
        }

        public static uint RemapMaxHealthWire(uint maxHealthWire, int playerLevel)
        {
            int maxHealth = (int)(maxHealthWire >> 8);
            if (maxHealth <= 0)
                return 0;

            int levelCurveF32 = GCDatabase.Instance.RequireCurveValueFixed32("MaxHealth", playerLevel);
            int referenceCurveF32 = GCDatabase.Instance.RequireCurveValueFixed32("MaxHealth", MaxHealthReferenceLevel);
            if (levelCurveF32 <= 0 || referenceCurveF32 <= 0)
                throw new InvalidDataException("Tables.MaxHealth contains a non-positive PvP remap value");

            long maxHealthF32 = (long)maxHealth << 8;
            long ratioF32 = (maxHealthF32 << 8) / levelCurveF32;
            long remappedF32 = ((ratioF32 + 1) * referenceCurveF32) >> 8;
            remappedF32 = (remappedF32 * 0x200L) >> 8;
            long remappedHealth = remappedF32 >> 8;
            if (remappedHealth <= 0)
                return 0;
            if (remappedHealth >= 0xFFFFFFL)
                return uint.MaxValue & 0xFFFFFF00u;
            return (uint)remappedHealth << 8;
        }
    }
}
