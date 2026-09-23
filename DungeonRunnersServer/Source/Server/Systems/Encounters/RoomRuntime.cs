using System;
using DungeonRunners.Engine;

namespace DungeonRunners.Combat
{
    public sealed class RoomRuntime
    {
        public const string DefaultInstanceKey = "global";

        public string InstanceKey { get; private set; }
        public uint Seed { get; private set; }
        public MersenneTwister RoomRng { get; private set; }
        public uint Tick { get; set; }
        public float Time { get; set; } = -1f;
        public string LastSeedSource { get; private set; }
        public byte? MonsterDifficulty { get; set; }

        public bool Initialized => RoomRng != null;
        public int RngCallsSinceReseed => RoomRng?.CallsSinceReseed ?? 0;

        public RoomRuntime(string instanceKey)
        {
            InstanceKey = NormalizeInstanceKey(instanceKey);
        }

        public static string NormalizeInstanceKey(string instanceKey)
        {
            return string.IsNullOrWhiteSpace(instanceKey) ? DefaultInstanceKey : instanceKey.Trim();
        }

        public void Initialize(uint seed, string source = null)
        {
            Seed = seed;
            RoomRng = new MersenneTwister(seed);
            LastSeedSource = source ?? "initialize";
            RngLedger.LogSeed("room", LastSeedSource, seed, InstanceKey, 0);
            Debug.LogError($"[ROOM-RNG] instance='{InstanceKey}' initialized seed=0x{seed:X8} source={LastSeedSource} rngPos=0");
        }

        public bool EnsureInitialized(uint seed, string source = null)
        {
            if (!Initialized)
            {
                Initialize(seed, source ?? "ensure");
                return true;
            }

            if (Seed != seed)
            {
                Debug.LogError($"[ROOM-RNG] instance='{InstanceKey}' preserved existing seed=0x{Seed:X8} requested=0x{seed:X8} rngPos={RngCallsSinceReseed} source={source ?? "ensure"}");
                return false;
            }

            return false;
        }

        public void Reseed(uint seed, string source = null)
        {
            uint previousSeed = Seed;
            int previousPos = RngCallsSinceReseed;
            Seed = seed;
            if (RoomRng == null)
                RoomRng = new MersenneTwister(seed);
            else
                RoomRng.Seed(seed);
            LastSeedSource = source ?? "reseed";
            RngLedger.LogSeed("room", LastSeedSource, seed, InstanceKey, RngCallsSinceReseed);
            Debug.LogError($"[RNG-SEED] instance='{InstanceKey}' reseed previous=0x{previousSeed:X8} seed=0x{seed:X8} previousPos={previousPos} rngPos={RngCallsSinceReseed} source={LastSeedSource}");
        }

        public void Advance(int count, string source)
        {
            if (!Initialized || count <= 0)
            {
                Debug.LogError($"[ROOM-RNG] instance='{InstanceKey}' advance skipped count={count} source={source ?? "unknown"} initialized={Initialized} seed=0x{Seed:X8}");
                return;
            }

            int before = RngCallsSinceReseed;
            for (int advanceIndex = 0; advanceIndex < count; advanceIndex++)
                RngLedger.Generate(RoomRng, "room", source ?? "advance", InstanceKey);
            Debug.LogError($"[ROOM-RNG] instance='{InstanceKey}' advanced {count} client calls source={source ?? "unknown"} seed=0x{Seed:X8} pos={before}->{RngCallsSinceReseed}");
        }

        public CombatContext CreateContext(string source = null)
        {
            return new CombatContext(this, source);
        }
    }

    public readonly struct CombatContext
    {
        public readonly RoomRuntime Runtime;
        public readonly string Source;

        public CombatContext(RoomRuntime runtime, string source = null)
        {
            Runtime = runtime;
            Source = source ?? "runtime";
        }

        public string InstanceKey => Runtime != null ? Runtime.InstanceKey : RoomRuntime.DefaultInstanceKey;
        public uint Seed => Runtime != null ? Runtime.Seed : 0u;
        public MersenneTwister RoomRng => Runtime != null ? Runtime.RoomRng : null;
        public int RngPos => Runtime != null ? Runtime.RngCallsSinceReseed : -1;
        public bool IsReady => Runtime != null && Runtime.Initialized;
    }
}
