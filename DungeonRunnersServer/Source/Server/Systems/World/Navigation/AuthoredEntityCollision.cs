using System.IO;
using DungeonRunners.Data;

namespace DungeonRunners.Core
{
    internal sealed class AuthoredEntityCollision
    {
        public int MinX, MinY, MinZ, MaxX, MaxY, MaxZ;

        public static bool TryResolve(GCNode placement, out AuthoredEntityCollision bounds)
        {
            bounds = null;
            GCNode entity = placement == null ? null : GCDatabase.Instance.ResolveWithInheritance(placement);
            GCNode description = entity?.GetChild("Description");
            if (entity == null || !entity.GetBool("Blocking", false)
                || description == null || description.GetBool("DynamicBlocking", true))
                return false;
            GCNode objectDescription = entity.GetChild("Object")?.GetChild("Description");
            if (objectDescription == null)
                return false;
            var result = new AuthoredEntityCollision();
            if (!Read(objectDescription, "MinX", out result.MinX)
                || !Read(objectDescription, "MinY", out result.MinY)
                || !Read(objectDescription, "MinZ", out result.MinZ)
                || !Read(objectDescription, "MaxX", out result.MaxX)
                || !Read(objectDescription, "MaxY", out result.MaxY)
                || !Read(objectDescription, "MaxZ", out result.MaxZ)
                || result.MinX > result.MaxX || result.MinY > result.MaxY || result.MinZ > result.MaxZ)
                throw new InvalidDataException($"Blocking entity has invalid authored bounds: {placement.CanonicalPath ?? placement.Extends}");
            bounds = result;
            return true;
        }

        private static bool Read(GCNode node, string key, out int value)
        {
            value = 0;
            return node.HasProperty(key) && GCNode.TryParseFixed32(node.GetString(key), out value);
        }

        public static void ToLocalFixed(int dx, int dy, int headingFixed, out int x, out int y)
        {
            if (headingFixed == 0)
            {
                x = dx;
                y = dy;
                return;
            }
            int angle = (unchecked(-headingFixed) >> 8) % 360;
            if (angle < 0) angle += 360;
            int cos = DungeonRunners.Combat.UnitMover.ZRotateCosFixed(angle);
            int sin = DungeonRunners.Combat.UnitMover.ZRotateSinFixed(angle);
            x = unchecked((dx * cos >> 8) - (dy * sin >> 8));
            y = unchecked((dy * cos >> 8) + (dx * sin >> 8));
        }
    }
}
