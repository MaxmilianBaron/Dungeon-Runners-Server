using System;

namespace DungeonRunners.Data
{
    internal readonly struct WorldEntityBounds
    {
        public readonly int MinX, MinY, MinZ, MaxX, MaxY, MaxZ;

        private WorldEntityBounds(int minX, int minY, int minZ, int maxX, int maxY, int maxZ)
        {
            MinX = minX;
            MinY = minY;
            MinZ = minZ;
            MaxX = maxX;
            MaxY = maxY;
            MaxZ = maxZ;
        }

        public int RadiusXYF32 => Math.Max(unchecked(MaxX - MinX), unchecked(MaxY - MinY)) / 2;

        public static bool TryResolve(GCNode worldEntity, out WorldEntityBounds bounds)
        {
            bounds = default;
            if (worldEntity == null)
                return false;
            GCNode archetype = ResolveObject(worldEntity);
            GCNode description = archetype?.GetDescription("WorldObjectDesc");
            if (description == null)
            {
                if (NativeAuthoredClasses.IsDerivedFrom(archetype?.NativeClassName, "ZonePortalObject"))
                    return false;
                bounds = new WorldEntityBounds(-1280, -1280, -1280, 1280, 1280, 1280);
                return true;
            }

            bool entityDescription = NativeAuthoredClasses.IsDerivedFrom(description.NativeClassName, "EntityObjectDesc");
            bool lavaDescription = NativeAuthoredClasses.IsDerivedFrom(description.NativeClassName, "LavaObjectDesc");
            int defaultMinX = entityDescription ? -640 : lavaDescription ? -5120 : 0;
            int defaultMinY = entityDescription || lavaDescription ? -5120 : 0;
            int defaultMaxX = entityDescription ? 640 : lavaDescription ? 5120 : 0;
            int defaultMaxY = entityDescription || lavaDescription ? 5120 : 0;
            int defaultMaxZ = entityDescription ? 8960 : lavaDescription ? 512 : 0;
            bounds = new WorldEntityBounds(
                description.GetFixed32("MinX", defaultMinX),
                description.GetFixed32("MinY", defaultMinY),
                description.GetFixed32("MinZ", 0),
                description.GetFixed32("MaxX", defaultMaxX),
                description.GetFixed32("MaxY", defaultMaxY),
                description.GetFixed32("MaxZ", defaultMaxZ));
            return true;
        }

        private static GCNode ResolveObject(GCNode worldEntity)
        {
            for (GCNode current = worldEntity; current != null; current = current.NativeBase)
                foreach (GCNode child in current.EnumerateChildrenInOrder())
                {
                    if ((current != worldEntity && !child.IsStatic)
                        || !NativeAuthoredClasses.IsDerivedFrom(child.NativeClassName, "GCObject")
                        || (!GCNode.ClassNamesEqual(child.GCClassName, "Object")
                            && !GCNode.ClassNamesEqual(child.GCClassPath, "Object")))
                        continue;
                    return NativeAuthoredClasses.IsDerivedFrom(child.NativeClassName, "EntityObject") ? child : null;
                }
            return null;
        }
    }
}
