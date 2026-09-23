using System;
using System.IO;
using System.Linq;
using DungeonRunners.Core;
using DungeonRunners.Data;
using DungeonRunners.Gameplay;

namespace DungeonRunners.Utilities
{
    public static partial class PathMapBuilder
    {
        public static PathMap BuildAuthoredStaticWorld(string zoneName)
        {
            var world = AuthoredWorldLayout.FindWorld(zoneName);
            if (world == null || world.Generated) return null;
            GCNode waypoint = world.Entities.LastOrDefault(node => AuthoredWorldLayout.IsKindOf(node, "Waypoint"));
            if (waypoint == null || !AuthoredWorldLayout.TryPosition(waypoint, out int x, out int y, out int z))
                return null;
            var cells = new[] { new MazeGenerator.MazeCell { TileType = world.Root.CanonicalPath } };
            WorldCollision.Instance.PrepareProceduralInstance(zoneName, zoneName, cells);
            return Build(zoneName, cells, x, y, z, true);
        }

        private static void ComputeAuthoredWorldBoundsFixed(string worldPath,
            out int minX, out int maxX, out int minY, out int maxY)
        {
            minX = minY = int.MaxValue;
            maxX = maxY = int.MinValue;
            foreach (TilePlacement placement in TileLayoutLoader.LoadAuthored(worldPath).Placements)
            {
                GCNode description = GCDatabase.Instance.ResolveWithInheritance(placement.ExtendsPath)?.GetChild("Description");
                if (description == null || !description.HasProperty("MinX") || !description.HasProperty("MinY")
                    || !description.HasProperty("MaxX") || !description.HasProperty("MaxY")) continue;
                int heading = NativeZRotateTableIndex(placement.HeadingFixed);
                int cos = DungeonRunners.Combat.UnitMover.ZRotateCosFixed(heading);
                int sin = DungeonRunners.Combat.UnitMover.ZRotateSinFixed(heading);
                int lowX = description.GetFixed32("MinX", 0), highX = description.GetFixed32("MaxX", 0);
                int lowY = description.GetFixed32("MinY", 0), highY = description.GetFixed32("MaxY", 0);
                foreach (var corner in new[] { (lowX, lowY), (lowX, highY), (highX, lowY), (highX, highY) })
                {
                    RotateFixed(corner.Item1, corner.Item2, cos, sin, out int rotatedX, out int rotatedY);
                    int x = checked(placement.XFixed + rotatedX), y = checked(placement.YFixed + rotatedY);
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
            }
            if (minX == int.MaxValue) throw new InvalidDataException($"Authored world geometry has no bounds '{worldPath}'");
            minX = NativeSceneMinFixed(minX); minY = NativeSceneMinFixed(minY);
            maxX = NativeSceneMaxFixed(maxX); maxY = NativeSceneMaxFixed(maxY);
        }

        internal static int NativeSceneMinFixed(int value)
            => checked((((checked(value + 256) >> 8) / 640) - (value < 1 ? 1 : 0)) * 640 * 256);

        internal static int NativeSceneMaxFixed(int value)
            => checked((((checked(value - 256) >> 8) / 640) + (value < 1 ? 0 : 1)) * 640 * 256);
    }
}
