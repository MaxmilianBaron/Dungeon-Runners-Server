using System;
using System.Collections.Generic;

namespace DungeonRunners.Gameplay
{
    public static class WorldObjectSpawner
    {
        private static readonly string[] BarrelTypes =
        {
            "terrain.misc.interactives.Breakiable_Barrel_01",
            "terrain.misc.interactives.Breakiable_Barrel_02",
            "terrain.misc.interactives.Breakiable_Barrel_03",
        };

        public static bool IsDestroyableObject(string gcType)
        {
            if (string.IsNullOrEmpty(gcType)) return false;
            if (gcType.StartsWith("world.objects.barrel", StringComparison.OrdinalIgnoreCase) ||
                gcType.StartsWith("world.objects.crate", StringComparison.OrdinalIgnoreCase))
                return true;
            for (int barrelTypeIndex = 0; barrelTypeIndex < BarrelTypes.Length; barrelTypeIndex++)
            {
                if (string.Equals(gcType, BarrelTypes[barrelTypeIndex], StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }

    public class ChestSpawnData
    {
        public string GCType;
        public string Label;
        public int PosFixedX;
        public int PosFixedY;
        public int PosFixedZ;
        public int HeadingFixed;
        public string ItemGenerator;
        public int ItemCount;
        public string ItemGenerator2;
        public int ItemCount2;
        public string ItemGenerator3;
        public int ItemCount3;
        public string ItemGenerator4;
        public int ItemCount4;
        public string ItemGenerator5;
        public int ItemCount5;

        public IEnumerable<(string Generator, int Count, int Slot)> GetChestGenerators()
        {
            var storedGenerators = new (string Generator, int Count)[]
            {
                (ItemGenerator, ItemCount),
                (ItemGenerator2, ItemCount2),
                (ItemGenerator3, ItemCount3),
                (ItemGenerator4, ItemCount4),
                (ItemGenerator5, ItemCount5)
            };
            foreach (var generator in WorldEntityLoot.GetNonCombatInteractiveItemGenerators(GCType, storedGenerators))
                yield return generator;
        }
    }
}
