namespace DungeonRunners.Networking.EntitySynchInfo
{
    public sealed class EntitySynchInfoOwnerState
    {
        public EntitySynchInfoOwnerKind OwnerKind;
        public uint OwnerEntityId;
        public uint ConnectionId;
        public string OwnerName;

        public uint LastOutboundHPWire;
        public string LastOutboundPacket;
        public byte LastOutboundFlags;
    }
}
