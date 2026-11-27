using System;
using System.Collections.Generic;
using DungeonRunners.Core;
using DungeonRunners.Engine;

namespace DungeonRunners.Combat
{
    public partial class CombatRuntime
    {
        private readonly Dictionary<string, RoomRuntime> _roomRuntimes = new Dictionary<string, RoomRuntime>(StringComparer.OrdinalIgnoreCase);
        private string _currentRoomRuntimeKey = RoomRuntime.DefaultInstanceKey;

        private RoomRuntime CurrentRoomRuntime => GetRoomRuntime(_currentRoomRuntimeKey);

        public MersenneTwister RoomRng => CurrentRoomRuntime.RoomRng;

        public uint RoomSeed => CurrentRoomRuntime.Seed;

        public bool IsRoomRngReady => CurrentRoomRuntime.Initialized;

        public int RoomRngCallsSinceReseed => CurrentRoomRuntime.RngCallsSinceReseed;

        public MersenneTwister RoomRandom => CurrentRoomRuntime.RoomRng;
        public uint RandomSeed => CurrentRoomRuntime.Seed;

        public string CurrentRoomRuntimeKey => _currentRoomRuntimeKey;

        public RoomRuntime GetRoomRuntime(string instanceKey)
        {
            string key = RoomRuntime.NormalizeInstanceKey(instanceKey);
            if (!_roomRuntimes.TryGetValue(key, out var runtime))
            {
                runtime = new RoomRuntime(key);
                _roomRuntimes[key] = runtime;
            }
            return runtime;
        }

        public bool TryGetRoomRuntime(string instanceKey, out RoomRuntime runtime)
        {
            string key = RoomRuntime.NormalizeInstanceKey(instanceKey);
            return _roomRuntimes.TryGetValue(key, out runtime);
        }

        public bool TryGetInitializedRoomRuntime(string instanceKey, out RoomRuntime runtime)
        {
            runtime = null;
            if (string.IsNullOrWhiteSpace(instanceKey))
            {
                RuntimeEvidence.LogFallbackHit("rng-instance", "missing-instance-key", "source=TryGetInitializedRoomRuntime", 64);
                Debug.LogError("[RNG-INSTANCE] source=TryGetInitializedRoomRuntime reason=missing-instance-key rng=null");
                return false;
            }

            string key = RoomRuntime.NormalizeInstanceKey(instanceKey);
            if (string.Equals(key, RoomRuntime.DefaultInstanceKey, StringComparison.OrdinalIgnoreCase))
            {
                RuntimeEvidence.LogFallbackHit("rng-instance", "default-instance-key", $"source=TryGetInitializedRoomRuntime instance='{key}'", 64);
                Debug.LogError($"[RNG-INSTANCE] source=TryGetInitializedRoomRuntime reason=default-instance-key instance='{key}' rng=null");
                return false;
            }

            if (!_roomRuntimes.TryGetValue(key, out runtime) || runtime == null || !runtime.Initialized)
            {
                RuntimeEvidence.LogFallbackHit("rng-instance", "uninitialized-instance", $"source=TryGetInitializedRoomRuntime instance='{key}'", 64);
                Debug.LogError($"[RNG-INSTANCE] source=TryGetInitializedRoomRuntime reason=uninitialized-instance instance='{key}' rng=null");
                runtime = null;
                return false;
            }

            return true;
        }

        public RoomRuntime RequireInitializedRoomRuntime(string instanceKey, string source)
        {
            if (TryGetInitializedRoomRuntime(instanceKey, out var runtime))
                return runtime;

            Debug.LogError($"[RNG-INSTANCE] source={source ?? "unknown"} reason=required-runtime-unavailable instance='{instanceKey ?? "<null>"}'");
            return null;
        }

        public void SetCurrentRoomRuntime(string instanceKey, string source = null)
        {
            _currentRoomRuntimeKey = RoomRuntime.NormalizeInstanceKey(instanceKey);
            GetRoomRuntime(_currentRoomRuntimeKey);
            Debug.LogError($"[ROOM-RUNTIME] current='{_currentRoomRuntimeKey}' source={source ?? "unknown"}");
        }

        private string ResolveMonsterRuntimeKey(Monster monster)
        {
            if (!string.IsNullOrWhiteSpace(monster?.InstanceKey))
                return monster.InstanceKey;
            RuntimeEvidence.LogFallbackHit(
                "rng-instance",
                "monster-missing-instance",
                $"monster={monster?.Name ?? "<null>"}#{monster?.EntityId ?? 0}",
                64);
            return null;
        }

        public RoomRuntime GetRoomRuntimeForMonster(Monster monster)
        {
            return RequireInitializedRoomRuntime(ResolveMonsterRuntimeKey(monster), "monster-runtime");
        }

        public CombatContext GetCombatContextForMonster(Monster monster, string source = null)
        {
            RoomRuntime runtime = GetRoomRuntimeForMonster(monster);
            return runtime != null ? runtime.CreateContext(source) : new CombatContext(null, source);
        }

        public MersenneTwister GetRoomRngForMonster(Monster monster)
        {
            return GetRoomRuntimeForMonster(monster)?.RoomRng;
        }

        public MersenneTwister GetRoomRngForInstance(string instanceKey)
        {
            return RequireInitializedRoomRuntime(instanceKey, "instance-rng")?.RoomRng;
        }
    }
}
