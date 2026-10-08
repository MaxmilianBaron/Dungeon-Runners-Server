using System;
using System.Collections.Generic;
using System.Linq;

namespace DungeonRunners.Gameplay
{
    public partial class ZoneSpawner
    {
        private readonly Dictionary<string, byte> _layoutGeneratorLevels = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IReadOnlyList<string>> _layoutQuests = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DungeonMazeSpawner.ProceduralDungeonSnapshot> _staticSnapshots = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<string> FreezeLayoutQuests(string instanceKey, IEnumerable<string> quests, byte generatorLevel = 1)
        {
            lock (_proceduralPreparationLock)
            {
                if (_layoutQuests.TryGetValue(instanceKey, out var existing)) return existing;
                var selected = quests.Where(quest => !string.IsNullOrWhiteSpace(quest)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (selected.Length > byte.MaxValue) throw new InvalidOperationException("World quest list exceeds the native byte count");
                var frozen = Array.AsReadOnly(selected);
                _layoutQuests.Add(instanceKey, frozen);
                _layoutGeneratorLevels[instanceKey] = generatorLevel;
                return frozen;
            }
        }

        public IReadOnlyList<string> GetLayoutQuests(string instanceKey)
        {
            lock (_proceduralPreparationLock)
                return _layoutQuests.TryGetValue(instanceKey, out var quests) ? quests : Array.Empty<string>();
        }

        public byte GetLayoutGeneratorLevel(string instanceKey)
        {
            lock (_proceduralPreparationLock) return _layoutGeneratorLevels.TryGetValue(instanceKey, out byte level) ? level : (byte)1;
        }

        public DungeonMazeSpawner.ProceduralDungeonSnapshot GetOrCreateStaticSnapshot(string zoneName, string instanceKey, uint seed)
        {
            if (!DungeonMazeSpawner.HasStaticEncounterData(zoneName)) return null;
            if (seed == 0) throw new InvalidOperationException("Authored static encounters require the admitted instance seed");
            lock (_proceduralPreparationLock)
            {
                if (_staticSnapshots.TryGetValue(instanceKey, out var existing))
                {
                    if (existing.LayoutSeed != seed) throw new InvalidOperationException("Authored static encounter instance seed changed without a reset");
                    return existing;
                }
                var snapshot = DungeonMazeSpawner.GenerateStaticSnapshot(zoneName, seed, instanceKey, GetLayoutGeneratorLevel(instanceKey));
                _staticSnapshots.Add(instanceKey, snapshot);
                return snapshot;
            }
        }

        private void ClearAuthoredInstance(string instanceKey)
        {
            lock (_proceduralPreparationLock)
            {
                _layoutQuests.Remove(instanceKey);
                _layoutGeneratorLevels.Remove(instanceKey);
                _staticSnapshots.Remove(instanceKey);
            }
        }
    }
}
