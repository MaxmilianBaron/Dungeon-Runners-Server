using DungeonRunners.Data;

namespace DungeonRunners.Combat
{
    public static class MonsterUnitStatsBuilder
    {
        public static int ComputeBaseCriticalChanceF32(int authoredCritChanceF32)
        {
            if (authoredCritChanceF32 <= 0) return 0;
            long authoredFixed = authoredCritChanceF32;
            long globalScalar = GCDatabase.Instance.GetRequiredKnobInt("MonsterCriticalChance");
            return (int)((authoredFixed * globalScalar) >> 16);
        }
    }
}
