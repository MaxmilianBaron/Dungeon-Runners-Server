using DungeonRunners.Engine;

namespace DungeonRunners.Networking
{
    public sealed class ClientEntitySchedulerMirror
    {
        public const uint TickIntervalMs = 0x21;
        public const uint Cadence = 0x0B;

        public string RuntimeInstanceKey { get; private set; } = "global";
        public uint SchedulerTick { get; private set; }
        public uint UpdateNumber { get; private set; }
        public uint CadenceCounter { get; private set; }
        public bool SubEntityPhase { get; private set; }
        public string LastPhase { get; private set; } = "unknown";
        public uint LastCutoffTick { get; private set; }

        public void Reset(string instanceKey, string source = null)
        {
            RuntimeInstanceKey = string.IsNullOrWhiteSpace(instanceKey) ? "global" : instanceKey;
            SchedulerTick = 0;
            UpdateNumber = 0;
            CadenceCounter = 0;
            SubEntityPhase = false;
            LastPhase = "reset";
            LastCutoffTick = 0;
            Debug.LogError($"[CLIENT-ENTITY-SCHED] reset instance='{RuntimeInstanceKey}' source={source ?? "unknown"} interval=0x{TickIntervalMs:X} cadence=0x{Cadence:X}");
        }

        public void ObserveSuffixCutoff(string instanceKey, uint cutoffTick, bool subEntityPhase, string phase, string source = null)
        {
            if (!string.IsNullOrWhiteSpace(instanceKey) && !string.Equals(RuntimeInstanceKey, instanceKey, System.StringComparison.OrdinalIgnoreCase))
                RuntimeInstanceKey = instanceKey;

            LastCutoffTick = cutoffTick;
            SchedulerTick = cutoffTick;
            SubEntityPhase = subEntityPhase;
            LastPhase = string.IsNullOrWhiteSpace(phase) ? (subEntityPhase ? "subentity" : "entity") : phase;
            UpdateNumber++;
            CadenceCounter = Cadence == 0 ? 0 : (CadenceCounter + 1u) % Cadence;
            Debug.LogError($"[CLIENT-ENTITY-SCHED] instance='{RuntimeInstanceKey}' update={UpdateNumber} cadence={CadenceCounter}/0x{Cadence:X} tick={SchedulerTick} cutoff={cutoffTick} subentity={SubEntityPhase} phase={LastPhase} source={source ?? "unknown"}");
        }
    }
}
