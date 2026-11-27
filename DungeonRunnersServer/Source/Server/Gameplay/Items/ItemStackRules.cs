using System;
using DungeonRunners.Data;

namespace DungeonRunners.Networking
{
    public static class ItemStackRules
    {
        public static int GetLimit(string gcType)
        {
            GCNode description = GCObject.ResolveItemDescription(GCObject.GetPacketGCClassFor(gcType));
            if (description == null || !description.GetBool("Stackable", false))
                return 1;
            int maximum = description.GetInt("MaxStackSize", 5);
            return maximum >= 1 && maximum <= byte.MaxValue ? maximum : 1;
        }

        public static bool CanMerge(GCObject destination, int destinationCount, GCObject incoming, int incomingCount)
        {
            if (destination == null || incoming == null || destinationCount < 1 || incomingCount < 1
                || destinationCount > byte.MaxValue || incomingCount > byte.MaxValue)
                return false;
            int limit = GetLimit(incoming.GCClass);
            if (incomingCount > limit - destinationCount)
                return false;
            string destinationType = GCObject.GetPacketGCClassFor(destination.GCClass);
            string incomingType = GCObject.GetPacketGCClassFor(incoming.GCClass);
            if (string.IsNullOrWhiteSpace(destinationType) || string.IsNullOrWhiteSpace(incomingType))
                return false;
            foreach (string ancestor in GCDatabase.Instance.GetInheritanceChainPaths(incomingType))
                if (string.Equals(ancestor, destinationType, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        public static bool CanMergeAutomatically(GCObject destination, int destinationCount, GCObject incoming, int incomingCount)
        {
            return destination != null && incoming != null
                && string.Equals(GCObject.GetPacketGCClassFor(destination.GCClass), GCObject.GetPacketGCClassFor(incoming.GCClass), StringComparison.OrdinalIgnoreCase)
                && CanMerge(destination, destinationCount, incoming, incomingCount);
        }
    }
}
