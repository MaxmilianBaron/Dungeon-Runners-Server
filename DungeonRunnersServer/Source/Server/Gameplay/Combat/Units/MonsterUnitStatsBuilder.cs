using DungeonRunners.Data;

namespace DungeonRunners.Combat
{
    public static class MonsterUnitStatsBuilder
    {
        public static int ComputeBaseDamageMod(int authoredDamageModF32, bool isAlive)
        {
            int delta = unchecked(authoredDamageModF32 - 0x100);
            if (!isAlive || delta == 0x100)
                return 0;
            return unchecked((int)(((long)delta * 0x6400) >> 8)) >> 8;
        }

        public static int ComputeBaseRating(int authoredRatingF32, bool isAlive, byte level, bool useHenchmanCurves, bool attack)
        {
            if (!isAlive)
                return 0;
            string curve = useHenchmanCurves
                ? (attack ? "HenchmanAttackRating" : "HenchmanDefenseRating")
                : (attack ? "MonsterAttackRating" : "MonsterDefenseRating");
            int tableF32 = GCDatabase.Instance.RequireCurveValueFixed32(curve, level);
            return unchecked((ushort)((int)(((long)authoredRatingF32 * tableF32) >> 8) >> 8));
        }

        public static int ComputeBaseCriticalChanceF32(int authoredCritChanceF32, bool isAlive)
        {
            if (!isAlive)
                return 0;
            long authoredFixed = authoredCritChanceF32;
            long globalScalar = GCDatabase.Instance.GetRequiredKnobFixed32("MonsterCriticalChance");
            return unchecked((ushort)((int)((authoredFixed * globalScalar) >> 8) >> 8));
        }
    }
}
