namespace DungeonRunners.Combat
{
    public static class UnitFactionRules
    {
        public static bool IsEnemyFaction(
            int sourceFaction,
            int targetFaction,
            bool sourceAlwaysFriendly = false,
            bool targetAlwaysFriendly = false,
            bool playerFactionsAreEnemies = false)
        {
            if (sourceFaction == 1000 || targetFaction == 1000 || sourceAlwaysFriendly || targetAlwaysFriendly)
                return false;
            if (targetFaction == 100)
                return sourceFaction == 20;
            if (targetFaction != 0 && targetFaction != 2)
                return sourceFaction != targetFaction;
            if (sourceFaction != 0 && sourceFaction != 2)
                return true;
            return playerFactionsAreEnemies;
        }
    }
}
