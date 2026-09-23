using System;
using System.Collections.Generic;

namespace DungeonRunners.Core
{
    public enum NetworkEntityIdDomain
    {
        General,
        BlingGnome,
        Combat,
        Loot,
        Portal
    }

    public static class NetworkEntityIdRegistry
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<ushort, NetworkEntityIdDomain> Reservations = new Dictionary<ushort, NetworkEntityIdDomain>();

        public static bool TryReserve(NetworkEntityIdDomain domain, ushort entityId)
        {
            return TryReserveBatch(domain, new[] { entityId });
        }

        public static bool TryReserveBatch(NetworkEntityIdDomain domain, IReadOnlyList<ushort> entityIds)
        {
            if (entityIds == null || entityIds.Count == 0)
                return false;
            lock (Gate)
            {
                var unique = new HashSet<ushort>();
                for (int index = 0; index < entityIds.Count; index++)
                {
                    ushort entityId = entityIds[index];
                    if (entityId == 0 || !unique.Add(entityId) || Reservations.ContainsKey(entityId))
                        return false;
                }
                for (int index = 0; index < entityIds.Count; index++)
                    Reservations.Add(entityIds[index], domain);
                return true;
            }
        }

        public static bool IsReserved(ushort entityId)
        {
            lock (Gate)
                return Reservations.ContainsKey(entityId);
        }

        public static void Release(NetworkEntityIdDomain domain, ushort entityId)
        {
            lock (Gate)
            {
                if (Reservations.TryGetValue(entityId, out NetworkEntityIdDomain current) && current == domain)
                    Reservations.Remove(entityId);
            }
        }

        public static void ReleaseBatch(NetworkEntityIdDomain domain, IReadOnlyList<ushort> entityIds)
        {
            if (entityIds == null)
                return;
            lock (Gate)
            {
                for (int index = 0; index < entityIds.Count; index++)
                {
                    ushort entityId = entityIds[index];
                    if (Reservations.TryGetValue(entityId, out NetworkEntityIdDomain current) && current == domain)
                        Reservations.Remove(entityId);
                }
            }
        }
    }
}
