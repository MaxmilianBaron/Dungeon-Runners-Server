using System;
using DungeonRunners.Combat;

namespace DungeonRunners.Core
{
    public static partial class SimulationParityLedger
    {
        private static volatile bool _operationTracking;
        private static long _operationSequence;

        [ThreadStatic]
        private static OperationContext _operationContext;

        internal static bool OperationTracking => _active && _operationTracking && ServerDiagnostics.Enabled;

        internal static OperationContext EnterOperationPhase(string phase, string instance = null, uint? entity = null)
        {
            if (!OperationTracking)
                return default;
            OperationContext previous = _operationContext;
            _operationContext = new OperationContext(phase, instance ?? previous.Instance, entity ?? previous.Entity);
            return new OperationContext(previous.Phase, previous.Instance, previous.Entity, true);
        }

        internal static void RecordOperation(string family, string operation, object detail, uint? entity = null)
        {
            if (!OperationTracking)
                return;
            var context = _operationContext;
            lock (Sync)
            {
                if (!_accepting)
                    return;
                TryEnqueue(new
                {
                    Schema = "dr-server-simulation-parity-event-v1",
                    Kind = "server-operation",
                    OperationSequence = ++_operationSequence,
                    TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Tick = SimulationClock.SimulationTick,
                    context.Phase,
                    context.Instance,
                    Entity = entity ?? context.Entity,
                    Family = family,
                    Operation = operation,
                    Detail = detail
                });
            }
        }

        internal readonly struct OperationContext : IDisposable
        {
            internal readonly string Phase;
            internal readonly string Instance;
            internal readonly uint? Entity;
            private readonly bool _restore;

            internal OperationContext(string phase, string instance, uint? entity, bool restore = false)
            {
                Phase = phase;
                Instance = instance;
                Entity = entity;
                _restore = restore;
            }

            public void Dispose()
            {
                if (_restore)
                    _operationContext = new OperationContext(Phase, Instance, Entity);
            }
        }
    }
}
