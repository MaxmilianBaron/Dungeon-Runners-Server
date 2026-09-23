using System;
using System.Threading;

namespace DungeonRunners.Combat
{
    public static class SimulationClock
    {
        public const uint TicksPerSecond = 30u;

        private static long _simulationTick;

        public static uint SimulationTick => checked((uint)Interlocked.Read(ref _simulationTick));

        public static uint Advance()
        {
            long next = Interlocked.Increment(ref _simulationTick);
            if ((ulong)next > uint.MaxValue)
                throw new InvalidOperationException("SimulationTick exhausted uint range");
            return (uint)next;
        }

    }
}
