using DungeonRunners.Utilities;

namespace DungeonRunners.Networking.EntitySynchInfo
{
    public readonly struct EntitySynchInfoPayload
    {
        public readonly byte Flags;
        public readonly uint HPWire;

        public bool HasHP => (Flags & 0x02) != 0;

        public EntitySynchInfoPayload(byte flags, uint hpWire)
        {
            Flags = flags;
            HPWire = hpWire;
        }

        public static EntitySynchInfoPayload Empty => new EntitySynchInfoPayload(0x00, 0);

        public static EntitySynchInfoPayload FromHP(uint hpWire)
        {
            return new EntitySynchInfoPayload(0x02, hpWire);
        }

        public void Write(LEWriter writer)
        {
            writer.WriteByte(Flags);
            if ((Flags & 0x02) != 0)
                writer.WriteUInt32(HPWire);
        }

        public override string ToString()
        {
            return HasHP ? $"flags=0x{Flags:X2} hpWire={HPWire}" : $"flags=0x{Flags:X2}";
        }
    }

    public readonly struct ResolvedEntitySynchInfo
    {
        public readonly EntitySynchInfoPayload Payload;
        public readonly uint OwnerEntityId;
        public readonly uint ComponentId;
        public readonly byte Subtype;
        public readonly uint ValidationCutoffTick;
        public readonly string Reason;
        public readonly string Provenance;
        public readonly string RuntimeInstanceKey;
        public readonly uint SchedulerTick;
        public readonly bool SubEntityPhase;
        public readonly int RngPos;
        public readonly string HpMutationSource;

        public byte Flags => Payload.Flags;
        public uint HPWire => Payload.HPWire;
        public bool HasHP => Payload.HasHP;

        public ResolvedEntitySynchInfo(EntitySynchInfoPayload payload, uint ownerEntityId, uint componentId, byte subtype, string reason, string provenance, uint validationCutoffTick = 0, string runtimeInstanceKey = null, uint schedulerTick = 0, bool subEntityPhase = false, int rngPos = -1, string hpMutationSource = null)
        {
            Payload = payload;
            OwnerEntityId = ownerEntityId;
            ComponentId = componentId;
            Subtype = subtype;
            ValidationCutoffTick = validationCutoffTick;
            Reason = reason ?? "resolved";
            Provenance = provenance ?? "runtime";
            RuntimeInstanceKey = runtimeInstanceKey ?? string.Empty;
            SchedulerTick = schedulerTick;
            SubEntityPhase = subEntityPhase;
            RngPos = rngPos;
            HpMutationSource = hpMutationSource ?? string.Empty;
        }

        public static ResolvedEntitySynchInfo FromHP(uint ownerEntityId, uint componentId, byte subtype, uint hpWire, string reason, string provenance)
        {
            return new ResolvedEntitySynchInfo(EntitySynchInfoPayload.FromHP(hpWire), ownerEntityId, componentId, subtype, reason, provenance);
        }

        public override string ToString()
        {
            return $"{Payload} owner={OwnerEntityId} component={ComponentId} sub=0x{Subtype:X2} cutoffTick={ValidationCutoffTick} runtime='{RuntimeInstanceKey}' schedulerTick={SchedulerTick} subentity={SubEntityPhase} rngPos={RngPos} hpMutation='{HpMutationSource}' reason={Reason} provenance={Provenance}";
        }
    }
}
