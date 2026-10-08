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
            GCNode description = entity?.GetDescription("WorldEntityDesc");
            if (entity == null || !entity.GetBool("Blocking", false)
                || description == null || description.GetBool("DynamicBlocking", true))
                return false;
            if (!WorldEntityBounds.TryResolve(entity, out WorldEntityBounds geometry))
                return false;
            bounds = new AuthoredEntityCollision
            {
                MinX = geometry.MinX, MinY = geometry.MinY, MinZ = geometry.MinZ,
                MaxX = geometry.MaxX, MaxY = geometry.MaxY, MaxZ = geometry.MaxZ
            };
            return true;
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
