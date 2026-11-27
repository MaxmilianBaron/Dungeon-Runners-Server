using System;
using System.IO;
using System.Linq;

namespace DungeonRunners.Gameplay
{
    public static partial class DungeonMazeSpawner
    {
        private static SpawnUnit[] ResolveDewValleyEncounterUnits(ProceduralDungeonSnapshot snapshot,
            EncounterTableManifest table, SpawnUnit[] authored, string groupKey, out string role)
        {
            role = null;
            if (!string.Equals(snapshot.ZoneName, "dungeon00_level01", StringComparison.OrdinalIgnoreCase))
                return authored;

            const string pup = "world.dungeon00.mob.melee01.rank1";
            const string wolf = "world.dungeon00.mob.melee02.rank1";
            const string ratling = "world.dungeon00.mob.melee03.rank1";
            bool IsType(SpawnUnit unit, string type) => string.Equals(unit.AuthoredType, type, StringComparison.OrdinalIgnoreCase);
            uint seed = unchecked((uint)StableSpotSeed(groupKey)) ^ snapshot.LayoutSeed;
            int count = ResolveFallbackGroupCount(seed);

            if (string.Equals(table.AuthoredPath, "world.dungeon00.enc.level01_encounter", StringComparison.OrdinalIgnoreCase))
            {
                if (authored.Length < 1 || authored.Length > 2 || authored.Any(unit => !IsType(unit, pup) && !IsType(unit, ratling)))
                    throw new InvalidDataException("Unexpected Dew Valley normal encounter composition");
                bool hasPup = authored.Any(unit => IsType(unit, pup));
                bool hasRatling = authored.Any(unit => IsType(unit, ratling));
                role = hasPup && hasRatling ? "dew-valley-mixed" : hasPup ? "dew-valley-pups" : "dew-valley-ratlings";
                var units = new SpawnUnit[count];
                for (int i = 0; i < units.Length; i++) units[i] = authored[i % authored.Length];
                return units;
            }

            if (string.Equals(table.AuthoredPath, "world.dungeon00.enc.level01_leader_encounter", StringComparison.OrdinalIgnoreCase))
            {
                if (authored.Length != 2 || authored.Count(unit => IsType(unit, wolf)) != 1 || authored.Count(unit => IsType(unit, pup)) != 1)
                    throw new InvalidDataException("Unexpected Dew Valley wolf encounter composition");
                role = "dew-valley-wolf-pack";
                var units = new SpawnUnit[count + 1];
                int index = 0;
                foreach (SpawnUnit unit in authored)
                    for (int repeat = 0; repeat < (IsType(unit, wolf) ? 1 : count); repeat++) units[index++] = unit;
                return units;
            }

            return authored;
        }
    }
}
