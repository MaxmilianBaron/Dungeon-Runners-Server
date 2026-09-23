using System;
using System.Collections.Generic;
using DungeonRunners.Infrastructure;
using DungeonRunners.Core;

namespace DungeonRunners.Combat
{
    public class MersenneTwister
    {
        private const int N = 624;
        private const int M = 397;
        private const uint MATRIX_A = 0x9908B0DF;
        private const uint UPPER_MASK = 0x80000000;
        private const uint LOWER_MASK = 0x7FFFFFFF;

        private const uint TEMPERING_MASK_B = 0xFF3A58AD;
        private const uint TEMPERING_MASK_C = 0xFFFFDF8C;

        private uint[] _mt = new uint[N];
        private int _mti = N + 1;

        public uint LastSeed { get; private set; }
        public int CallsSinceReseed { get; private set; }
        public uint LastGeneratedValue { get; private set; }

        public MersenneTwister()
        {
        }

        public MersenneTwister(uint seed)
        {
            Seed(seed);
        }

        public MersenneTwister Copy()
        {
            var copy = new MersenneTwister();
            Array.Copy(_mt, copy._mt, N);
            copy._mti = _mti;
            copy.LastSeed = LastSeed;
            copy.CallsSinceReseed = CallsSinceReseed;
            copy.LastGeneratedValue = LastGeneratedValue;
            return copy;
        }

        public void RestoreFrom(MersenneTwister snapshot)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));
            Array.Copy(snapshot._mt, _mt, N);
            _mti = snapshot._mti;
            LastSeed = snapshot.LastSeed;
            CallsSinceReseed = snapshot.CallsSinceReseed;
            LastGeneratedValue = snapshot.LastGeneratedValue;
        }

        public void Seed(uint seed)
        {
            LastSeed = seed;
            CallsSinceReseed = 0;
            _mt[0] = seed;
            for (int stateIndex = 1; stateIndex < N; stateIndex++)
            {
                _mt[stateIndex] = (uint)(0x6c078965 * (_mt[stateIndex - 1] ^ (_mt[stateIndex - 1] >> 30)) + stateIndex);
            }
            _mti = N;
        }

        public uint Generate()
        {
            uint y;
            uint[] mag01 = { 0, MATRIX_A };

            if (_mti >= N)
            {
                int kk;

                if (_mti == N + 1)
                {
                    Seed(0x1105);
                }

                for (kk = 0; kk < N - M; kk++)
                {
                    y = (_mt[kk] & UPPER_MASK) | (_mt[kk + 1] & LOWER_MASK);
                    _mt[kk] = _mt[kk + M] ^ (y >> 1) ^ mag01[y & 1];
                }

                for (; kk < N - 1; kk++)
                {
                    y = (_mt[kk] & UPPER_MASK) | (_mt[kk + 1] & LOWER_MASK);
                    _mt[kk] = _mt[kk + (M - N)] ^ (y >> 1) ^ mag01[y & 1];
                }

                y = (_mt[N - 1] & UPPER_MASK) | (_mt[0] & LOWER_MASK);
                _mt[N - 1] = _mt[M - 1] ^ (y >> 1) ^ mag01[y & 1];

                _mti = 0;
            }

            y = _mt[_mti++];

            y ^= (y >> 11);
            y ^= (y & TEMPERING_MASK_B) << 7;
            y ^= (y & TEMPERING_MASK_C) << 15;
            y ^= (y >> 18);

            CallsSinceReseed++;
            LastGeneratedValue = y;
            return y;
        }

        public uint Generate(uint min, uint max)
        {
            uint range = max - min + 1;
            if (range == 0)
                return min;
            return (Generate() % range) + min;
        }

        public int GenerateInt(int min, int max)
        {
            if (max < min)
                return min;
            uint range = (uint)(max - min + 1);
            return (int)((Generate() % range) + (uint)min);
        }
    }

    public static class RngLedger
    {
        private static bool VerboseRngLedgerDrawLogs => ServerDiagnostics.IsEnabled("verboseRngLogging");

        public sealed class Entry
        {
            public bool IsSeed;
            public string Stream;
            public string Phase;
            public uint Seed;
            public int Before;
            public int After;
            public uint Raw;
            public string Owner;
            public uint? EntityId;
            public uint? AttackerEntityId;
            public uint? DefenderEntityId;
            public uint? Value;
        }

        private static readonly List<Entry> _capture = new List<Entry>();

        public static event Action<Entry> EntryObserved;

        public static bool CaptureEnabled { get; set; }

        public static void ClearCapture()
        {
            lock (_capture)
                _capture.Clear();
        }

        public static List<Entry> SnapshotCapture()
        {
            lock (_capture)
            {
                var snapshot = new List<Entry>(_capture.Count);
                foreach (var entry in _capture)
                    snapshot.Add(Clone(entry));
                return snapshot;
            }
        }

        private static Entry Clone(Entry entry)
        {
            return new Entry
            {
                IsSeed = entry.IsSeed,
                Stream = entry.Stream,
                Phase = entry.Phase,
                Seed = entry.Seed,
                Before = entry.Before,
                After = entry.After,
                Raw = entry.Raw,
                Owner = entry.Owner,
                EntityId = entry.EntityId,
                AttackerEntityId = entry.AttackerEntityId,
                DefenderEntityId = entry.DefenderEntityId,
                Value = entry.Value
            };
        }

        private static void Capture(Entry entry)
        {
            try
            {
                EntryObserved?.Invoke(entry);
            }
            catch
            {
            }
            if (!CaptureEnabled || entry == null)
                return;

            lock (_capture)
                _capture.Add(Clone(entry));
        }

        public static uint Generate(MersenneTwister rng, string stream, string phase, string owner = null, uint? entityId = null, uint? attackerEntityId = null, uint? defenderEntityId = null)
        {
            if (rng == null)
                return 0;

            int before = rng.CallsSinceReseed;
            uint raw = rng.Generate();
            LogDraw(stream, phase, rng.LastSeed, before, rng.CallsSinceReseed, raw, owner, null, entityId, attackerEntityId, defenderEntityId);
            return raw;
        }

        public static uint Generate(MersenneTwister rng, string stream, string phase, uint min, uint max, string owner = null, uint? entityId = null, uint? attackerEntityId = null, uint? defenderEntityId = null)
        {
            if (rng == null)
                return min;

            int before = rng.CallsSinceReseed;
            uint raw = rng.Generate();
            uint range = max - min + 1;
            uint value = range == 0 ? min : (raw % range) + min;
            LogDraw(stream, $"{phase ?? "draw"}[{min}..{max}]", rng.LastSeed, before, rng.CallsSinceReseed, raw, owner, value, entityId, attackerEntityId, defenderEntityId);
            return value;
        }

        public static void LogSeed(string stream, string phase, uint seed, string owner = null, int position = 0)
        {
            if (CaptureEnabled || EntryObserved != null)
            {
                Capture(new Entry
                {
                    IsSeed = true,
                    Stream = stream ?? "unknown",
                    Phase = phase ?? "seed",
                    Seed = seed,
                    Before = position,
                    After = position,
                    Owner = owner ?? string.Empty
                });
            }
            if (VerboseRngLedgerDrawLogs)
                DungeonRunners.Engine.Debug.LogError($"[RNG-LEDGER] stream={stream ?? "unknown"} phase={phase ?? "seed"} seed=0x{seed:X8} pos={position} owner='{owner ?? ""}'");
        }

        public static void LogDraw(string stream, string phase, uint seed, int before, int after, uint raw, string owner = null, uint? value = null, uint? entityId = null, uint? attackerEntityId = null, uint? defenderEntityId = null)
        {
            if (CaptureEnabled || EntryObserved != null)
            {
                Capture(new Entry
                {
                    IsSeed = false,
                    Stream = stream ?? "unknown",
                    Phase = phase ?? "draw",
                    Seed = seed,
                    Before = before,
                    After = after,
                    Raw = raw,
                    Owner = owner ?? string.Empty,
                    EntityId = entityId,
                    AttackerEntityId = attackerEntityId,
                    DefenderEntityId = defenderEntityId,
                    Value = value
                });
            }
            if (VerboseRngLedgerDrawLogs)
            {
                string valueText = value.HasValue ? $" value={value.Value}" : "";
                DungeonRunners.Engine.Debug.LogError($"[RNG-LEDGER] stream={stream ?? "unknown"} phase={phase ?? "draw"} seed=0x{seed:X8} pos={before}->{after} raw=0x{raw:X8}{valueText} owner='{owner ?? ""}'");
            }
        }

        public static void LogSystemRandom(string phase, int value, string owner = null)
        {
            if (VerboseRngLedgerDrawLogs)
                DungeonRunners.Engine.Debug.LogError($"[RNG-LEDGER] stream=serverNon/System.Random phase={phase ?? "draw"} value={value} owner='{owner ?? ""}' sourceFunction=non-client-audit");
        }

        public static void LogRollback(string stream, string phase, uint seed, int before, int after, string owner = null)
        {
            if (before == after)
                return;
            if (CaptureEnabled || EntryObserved != null)
            {
                Capture(new Entry
                {
                    IsSeed = false,
                    Stream = stream ?? "unknown",
                    Phase = phase ?? "transaction-rollback",
                    Seed = seed,
                    Before = before,
                    After = after,
                    Owner = owner ?? string.Empty
                });
            }
            if (VerboseRngLedgerDrawLogs)
                DungeonRunners.Engine.Debug.LogError($"[RNG-LEDGER] stream={stream ?? "unknown"} phase={phase ?? "transaction-rollback"} seed=0x{seed:X8} pos={before}->{after} owner='{owner ?? ""}'");
        }
    }

    public static class RandomStreams
    {
        private static readonly MersenneTwister _globalStaticRng = new MersenneTwister();
        private static readonly object _globalStaticGate = new object();
        private static bool _globalStaticSeeded;

        public sealed class GlobalStaticTransaction : IDisposable
        {
            private MersenneTwister _snapshot;
            private readonly string _source;
            private bool _completed;

            internal GlobalStaticTransaction(MersenneTwister snapshot, string source)
            {
                _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
                _source = source ?? "globalStatic:transaction";
            }

            public void Commit()
            {
                Complete(true);
            }

            public void Dispose()
            {
                Complete(false);
            }

            private void Complete(bool commit)
            {
                if (_completed)
                    return;
                _completed = true;
                MersenneTwister snapshot = _snapshot;
                _snapshot = null;
                try
                {
                    if (!commit)
                    {
                        int before = _globalStaticRng.CallsSinceReseed;
                        _globalStaticRng.RestoreFrom(snapshot);
                        RngLedger.LogRollback("globalStatic", "transaction-rollback", snapshot.LastSeed, before, snapshot.CallsSinceReseed, _source);
                    }
                }
                finally
                {
                    System.Threading.Monitor.Exit(_globalStaticGate);
                }
            }
        }

        public static GlobalStaticTransaction BeginGlobalStaticTransaction(string source = null)
        {
            System.Threading.Monitor.Enter(_globalStaticGate);
            try
            {
                EnsureGlobalStaticSeededFromTime64(source ?? "globalStatic:transaction");
                return new GlobalStaticTransaction(_globalStaticRng.Copy(), source);
            }
            catch
            {
                System.Threading.Monitor.Exit(_globalStaticGate);
                throw;
            }
        }

        public static void SeedGlobalStatic(uint seed, string source = null)
        {
            lock (_globalStaticGate)
            {
                _globalStaticRng.Seed(seed);
                _globalStaticSeeded = true;
                RngLedger.LogSeed("globalStatic", source ?? "_WinMain@16:time64", seed, "0x00932FF8", 0);
                DungeonRunners.Engine.Debug.LogError($"[RNG-SEED] stream=globalStatic clientObject=0x00932FF8 seed=0x{seed:X8} source={source ?? "_WinMain@16:time64"} rngPos=0");
            }
        }

        public static void EnsureGlobalStaticSeededFromTime64(string source = null)
        {
            lock (_globalStaticGate)
            {
                if (_globalStaticSeeded)
                    return;
                long seconds = ServerRuntime.ReadUnixTimeSeconds();
                SeedGlobalStatic(unchecked((uint)seconds), source ?? "_WinMain@16:time64");
            }
        }

        public static uint GenerateGlobalStatic(string phase = null, string owner = null)
        {
            lock (_globalStaticGate)
            {
                EnsureGlobalStaticSeededFromTime64("globalStatic:first-use");
                return RngLedger.Generate(_globalStaticRng, "globalStatic", phase ?? "Random::generator", owner);
            }
        }

        public static uint GenerateGlobalStaticRangeInclusive(uint min, uint max, string phase = null, string owner = null)
        {
            lock (_globalStaticGate)
            {
                EnsureGlobalStaticSeededFromTime64("globalStatic:first-use");
                return RngLedger.Generate(_globalStaticRng, "globalStatic", phase ?? "Random::generate(min,max)", min, max, owner);
            }
        }

        public static uint GenerateGlobalSound(string phase = null, string owner = null)
        {
            return GenerateGlobalStatic(phase ?? "Weapon::playAttackSound", owner);
        }

        public static int GlobalStaticCalls
        {
            get
            {
                lock (_globalStaticGate)
                    return _globalStaticRng.CallsSinceReseed;
            }
        }

        public static uint GlobalStaticSeed
        {
            get
            {
                lock (_globalStaticGate)
                    return _globalStaticRng.LastSeed;
            }
        }
        public static int GlobalSoundCalls => GlobalStaticCalls;
        public static uint GlobalSoundSeed => GlobalStaticSeed;
    }
}
