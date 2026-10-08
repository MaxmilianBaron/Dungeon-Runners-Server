using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DungeonRunners.Engine;
using DungeonRunners.Combat;
using DungeonRunners.Core;

namespace DungeonRunners.Gameplay
{
    public partial class ZoneSpawner
    {
        private static ZoneSpawner _instance;
        public static ZoneSpawner Instance => _instance ??= new ZoneSpawner();

        private HashSet<string> _spawnedZones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, DungeonMazeSpawner.ProceduralDungeonSnapshot> _proceduralSnapshots
            = new Dictionary<string, DungeonMazeSpawner.ProceduralDungeonSnapshot>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _instancePathMapsBuilt = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _proceduralPreparationLock = new object();
        private readonly Dictionary<string, ProceduralSnapshotPreparation> _proceduralPreparations = new Dictionary<string, ProceduralSnapshotPreparation>(StringComparer.OrdinalIgnoreCase);

        private sealed class ProceduralSnapshotPreparation
        {
            public string ZoneName;
            public uint LayoutSeed;
            public Task<DungeonMazeSpawner.ProceduralDungeonSnapshot> Task;
            public bool Invalidated;
        }

        private const int EncounterScanRadiusFloorF32 = 500 * 0x100;
        private const int ENCOUNTER_SCAN_TICKS = 30;

        private readonly Dictionary<string, List<EncounterObject>> _encounterObjects
            = new Dictionary<string, List<EncounterObject>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _pendingZone
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public bool HasEncounterObjects(string instanceKey)
        {
            if (string.IsNullOrWhiteSpace(instanceKey))
                return false;
            return _encounterObjects.TryGetValue(RoomRuntime.NormalizeInstanceKey(instanceKey), out List<EncounterObject> encounters)
                && encounters.Count > 0;
        }

        private sealed class EncounterObject
        {
            public uint EntityId;
            public string GroupKey;
            public List<DungeonSpawnData> Defs = new List<DungeonSpawnData>();
            public int CenterFixedX;
            public int CenterFixedY;
            public int ScanRadiusF32;
            public int MinFixedX;
            public int MinFixedY;
            public int MaxFixedX;
            public int MaxFixedY;
            public int ScanTimer;
            public bool Spawned;
            public int ChoiceIndex = -1;
        }

        public List<Monster> SpawnZoneMobs(string zoneName)
        {
            var spawned = new List<Monster>();

            if (string.IsNullOrEmpty(zoneName))
                return spawned;

            if (_spawnedZones.Contains(zoneName))
            {
                Debug.LogError($"[ZONE-SPAWNER] zone='{zoneName}' reason=already-spawned");
                return spawned;
            }

            List<DungeonSpawnData> spawnDefs = GetSpawnData(zoneName);

            if (spawnDefs == null || spawnDefs.Count == 0)
            {
                Debug.LogError($"[ZONE-SPAWNER] zone='{zoneName}' reason=no-spawn-data");
                return spawned;
            }

            Debug.LogError($"[ZONE-SPAWNER] spawn zone='{zoneName}' count={spawnDefs.Count}");

            var pathMap = DungeonRunners.Core.PathMapCatalog.Instance.GetPathMap(zoneName);
            bool proceduralSpawns = DungeonMazeSpawner.IsProceduralZone(zoneName);
            if (!RegisterEncounterObjects(zoneName, zoneName, spawnDefs, false))
                return spawned;

            foreach (var spawnDef in spawnDefs)
            {
                var monster = CombatRuntime.Instance.SpawnMonsterFixed(
                    spawnDef.gcType,
                    spawnDef.PosFixedX,
                    spawnDef.PosFixedY,
                    spawnDef.PosFixedZ,
                    spawnDef.HeadingFixed,
                    zoneName,
                    spawnDef.encounterGroupKey,
                    spawnDef.encounterDifficultyF32 >= 0 ? spawnDef.encounterDifficultyF32 : 0x100,
                    spawnDef.spawnGcTypeOverride,
                    null,
                    spawnDef.encounterLevelOffset
                );

                if (monster != null)
                {
                    AssignEncounterObjectEntity(monster);
                    spawned.Add(monster);
                    Debug.LogError($"[ZONE-SPAWNER] spawned monster={monster.Name} gc='{spawnDef.gcType}' fixed=({spawnDef.PosFixedX},{spawnDef.PosFixedY},{spawnDef.PosFixedZ})");
                }
                else
                {
                    Debug.LogError($"[ZONE-SPAWNER] gc='{spawnDef.gcType}' reason=missing-creature");
                }
            }


            _spawnedZones.Add(zoneName);
            Debug.LogError($"[ZONE-SPAWNER] complete zone='{zoneName}' spawned={spawned.Count}/{spawnDefs.Count}");
            return spawned;
        }

        private List<DungeonSpawnData> GetSpawnData(string zoneName, uint? seed = null)
        {
            return GetNormalSpawnData(zoneName, seed);
        }

        private List<DungeonSpawnData> GetNormalSpawnData(string zoneName, uint? seed = null)
        {
            if (DungeonMazeSpawner.IsProceduralZone(zoneName))
            {
                if (!seed.HasValue || seed.Value == 0)
                {
                    Debug.LogError($"[ZONE-SPAWNER] zone='{zoneName}' mode=procedural reason=missing-layout-seed state=blocked");
                    return null;
                }
                uint spawnSeed = seed.Value;
                Debug.LogError($"[ZONE-SPAWNER] zone='{zoneName}' mode=procedural seed=0x{spawnSeed:X8}");
                return DungeonMazeSpawner.GenerateSpawns(zoneName, spawnSeed);
            }

            if (DungeonMazeSpawner.HasStaticEncounterData(zoneName))
            {
                if (!seed.HasValue || seed.Value == 0)
                {
                    Debug.LogError($"[ZONE-SPAWNER] zone='{zoneName}' mode=static-boss reason=missing-layout-seed state=blocked");
                    return null;
                }
                uint spawnSeed = seed.Value;
                Debug.LogError($"[ZONE-SPAWNER] zone='{zoneName}' mode=static-boss seed=0x{spawnSeed:X8}");
                return DungeonMazeSpawner.GenerateAuthoredStaticSpawns(zoneName, spawnSeed);
            }

            List<DungeonSpawnData> staticSpawns;
            if (AuthoredGameplayCatalog.DungeonSpawns.TryGetValue(zoneName, out staticSpawns) && staticSpawns.Count > 0)
            {
                Debug.LogError($"[ZONE-SPAWNER] zone='{zoneName}' mode=static count={staticSpawns.Count}");
                return staticSpawns;
            }

            return null;
        }

        public DungeonMazeSpawner.ProceduralDungeonSnapshot GetOrCreateProceduralSnapshot(
            string zoneName, string instanceKey, uint layoutSeed, uint roomSeed = 0)
        {
            if (string.IsNullOrEmpty(zoneName) || !DungeonMazeSpawner.IsProceduralZone(zoneName))
                return null;
            if (layoutSeed == 0)
            {
                Debug.LogError($"[DUNGEON-SNAPSHOT] instance='{instanceKey ?? ""}' zone={zoneName} reason=missing-layout-seed state=blocked");
                return null;
            }

            string key = string.IsNullOrEmpty(instanceKey) ? zoneName : instanceKey;
            uint clientLayoutSeed = layoutSeed;
            if (TryCommitProceduralSnapshotPreparation(zoneName, key, clientLayoutSeed, roomSeed, out var preparedSnapshot))
                return preparedSnapshot;
            if (_proceduralSnapshots.TryGetValue(key, out var snapshot))
            {
                bool layoutMismatch = snapshot.LayoutSeed != clientLayoutSeed;
                bool roomMismatch = roomSeed != 0 && snapshot.RoomSeed != 0 && snapshot.RoomSeed != roomSeed;
                if ((layoutMismatch || roomMismatch) && !_spawnedZones.Contains(key))
                {
                    Debug.LogError($"[DUNGEON-SNAPSHOT] rebuild instance='{key}' zone={zoneName} oldLayout=0x{snapshot.LayoutSeed:X8} newLayout=0x{clientLayoutSeed:X8} requestedLayout=0x{layoutSeed:X8} oldRoom=0x{snapshot.RoomSeed:X8} newRoom=0x{roomSeed:X8}");
                    snapshot = DungeonMazeSpawner.GenerateSnapshot(zoneName, clientLayoutSeed, roomSeed, key, GetLayoutQuests(key), GetLayoutGeneratorLevel(key));
                    if (snapshot == null || !snapshot.AnchorsResolved)
                    {
                        Debug.LogError($"[DUNGEON-SNAPSHOT] instance='{key}' zone={zoneName} reason=missing-authored-anchor state=blocked");
                        return null;
                    }
                    _proceduralSnapshots[key] = snapshot;
                    BuildAndRegisterInstancePathMap(key, snapshot, rebuild: true);
                }
                else
                {
                    if (snapshot.RoomSeed == 0 && roomSeed != 0)
                    {
                        snapshot.RoomSeed = roomSeed;
                        Debug.LogError($"[DUNGEON-SNAPSHOT] resolved room seed instance='{key}' zone={zoneName} roomSeed=0x{roomSeed:X8}");
                    }
                    if (layoutMismatch || roomMismatch)
                        Debug.LogError($"[DUNGEON-SNAPSHOT] keep spawned snapshot instance='{key}' zone={zoneName} layoutSeed=0x{snapshot.LayoutSeed:X8} requestedLayout=0x{layoutSeed:X8} clientLayout=0x{clientLayoutSeed:X8} roomSeed=0x{snapshot.RoomSeed:X8} requestedRoom=0x{roomSeed:X8}");
                }
                Debug.LogError($"[DUNGEON-SNAPSHOT] reuse instance='{key}' zone={zoneName} layoutSeed=0x{snapshot.LayoutSeed:X8} requestedLayout=0x{layoutSeed:X8} roomSeed=0x{snapshot.RoomSeed:X8} entry=({snapshot.EntryGridX},{snapshot.EntryGridY}) tile='{snapshot.EntryTileType}' playerFixed=({snapshot.PlayerSpawnFixedX},{snapshot.PlayerSpawnFixedY},{snapshot.PlayerSpawnFixedZ}) entryPortalFixed=({snapshot.EntryPortalSpawnFixedX},{snapshot.EntryPortalSpawnFixedY},{snapshot.EntryPortalSpawnFixedZ}) exit=({snapshot.ExitGridX},{snapshot.ExitGridY}) tile='{snapshot.ExitTileType}' exitPortalFixed=({snapshot.ExitPortalSpawnFixedX},{snapshot.ExitPortalSpawnFixedY},{snapshot.ExitPortalSpawnFixedZ}) spawns={snapshot.Spawns.Count} yTransform=worldGridY=gridY/BuildWorld");
                return snapshot;
            }

            snapshot = DungeonMazeSpawner.GenerateSnapshot(zoneName, clientLayoutSeed, roomSeed, key, GetLayoutQuests(key), GetLayoutGeneratorLevel(key));
            if (snapshot == null || !snapshot.AnchorsResolved)
            {
                Debug.LogError($"[DUNGEON-SNAPSHOT] instance='{key}' zone={zoneName} reason=missing-authored-anchor state=blocked");
                return null;
            }
            _proceduralSnapshots[key] = snapshot;
            BuildAndRegisterInstancePathMap(key, snapshot, rebuild: false);
            Debug.LogError($"[DUNGEON-SNAPSHOT] cache instance='{key}' zone={zoneName} layoutSeed=0x{clientLayoutSeed:X8} requestedLayout=0x{layoutSeed:X8} roomSeed=0x{roomSeed:X8} entry=({snapshot.EntryGridX},{snapshot.EntryGridY}) tile='{snapshot.EntryTileType}' playerFixed=({snapshot.PlayerSpawnFixedX},{snapshot.PlayerSpawnFixedY},{snapshot.PlayerSpawnFixedZ}) entryPortalFixed=({snapshot.EntryPortalSpawnFixedX},{snapshot.EntryPortalSpawnFixedY},{snapshot.EntryPortalSpawnFixedZ}) exit=({snapshot.ExitGridX},{snapshot.ExitGridY}) tile='{snapshot.ExitTileType}' exitPortalFixed=({snapshot.ExitPortalSpawnFixedX},{snapshot.ExitPortalSpawnFixedY},{snapshot.ExitPortalSpawnFixedZ}) cells={snapshot.Cells.Count} roomNodes={snapshot.RoomNodes.Count} spawns={snapshot.Spawns.Count} yTransform=worldGridY=gridY/BuildWorld");
            return snapshot;
        }

        public void BeginProceduralSnapshotPreparation(string zoneName, string instanceKey, uint layoutSeed)
        {
            if (string.IsNullOrEmpty(zoneName) || !DungeonMazeSpawner.IsProceduralZone(zoneName))
                return;
            if (layoutSeed == 0)
            {
                Debug.LogError($"[DUNGEON-PREP] instance='{instanceKey ?? ""}' zone={zoneName} reason=missing-layout-seed state=blocked");
                return;
            }
            string key = string.IsNullOrEmpty(instanceKey) ? zoneName : instanceKey;
            uint clientLayoutSeed = layoutSeed;
            lock (_proceduralPreparationLock)
            {
                if (_proceduralSnapshots.TryGetValue(key, out var existing) && existing.LayoutSeed == clientLayoutSeed)
                    return;
                if (_proceduralPreparations.ContainsKey(key))
                    return;
                _proceduralPreparations[key] = new ProceduralSnapshotPreparation
                {
                    ZoneName = zoneName,
                    LayoutSeed = clientLayoutSeed,
                    Task = Task.Run(() => DungeonMazeSpawner.GenerateSnapshot(zoneName, clientLayoutSeed, 0, key, GetLayoutQuests(key), GetLayoutGeneratorLevel(key)))
                };
            }
            Debug.LogError($"[DUNGEON-PREP] instance='{key}' zone={zoneName} layoutSeed=0x{clientLayoutSeed:X8} state=started");
        }

        public bool TryCompleteProceduralSnapshotPreparation(string zoneName, string instanceKey, uint layoutSeed)
        {
            if (string.IsNullOrEmpty(zoneName) || !DungeonMazeSpawner.IsProceduralZone(zoneName))
                return true;
            if (layoutSeed == 0)
            {
                Debug.LogError($"[DUNGEON-PREP] instance='{instanceKey ?? ""}' zone={zoneName} reason=missing-layout-seed state=blocked");
                return false;
            }
            string key = string.IsNullOrEmpty(instanceKey) ? zoneName : instanceKey;
            uint clientLayoutSeed = layoutSeed;
            if (TryCommitProceduralSnapshotPreparation(zoneName, key, clientLayoutSeed, 0, out _))
                return true;
            BeginProceduralSnapshotPreparation(zoneName, key, clientLayoutSeed);
            return TryCommitProceduralSnapshotPreparation(zoneName, key, clientLayoutSeed, 0, out _);
        }

        private bool TryCommitProceduralSnapshotPreparation(
            string zoneName,
            string key,
            uint layoutSeed,
            uint roomSeed,
            out DungeonMazeSpawner.ProceduralDungeonSnapshot snapshot)
        {
            snapshot = null;
            ProceduralSnapshotPreparation preparation;
            bool invalidated;
            lock (_proceduralPreparationLock)
            {
                if (_proceduralSnapshots.TryGetValue(key, out snapshot) && snapshot.LayoutSeed == layoutSeed)
                {
                    if (!snapshot.AnchorsResolved)
                    {
                        snapshot = null;
                        return false;
                    }
                    if (snapshot.RoomSeed == 0 && roomSeed != 0)
                        snapshot.RoomSeed = roomSeed;
                    return true;
                }
                if (!_proceduralPreparations.TryGetValue(key, out preparation)
                    || !preparation.Task.IsCompleted)
                    return false;
                invalidated = preparation.Invalidated || preparation.LayoutSeed != layoutSeed;
                _proceduralPreparations.Remove(key);
            }
            if (invalidated)
            {
                ClearPathCollisionState(key, "InvalidatedProceduralPreparation");
                Debug.LogError($"[DUNGEON-PREP] instance='{key}' zone={zoneName} layoutSeed=0x{layoutSeed:X8} state=discarded");
                return false;
            }
            if (preparation.Task.IsFaulted || preparation.Task.IsCanceled)
            {
                string detail = preparation.Task.Exception?.GetBaseException().Message ?? "canceled";
                Debug.LogError($"[DUNGEON-PREP] instance='{key}' zone={zoneName} layoutSeed=0x{layoutSeed:X8} state=failed message='{detail}'");
                return false;
            }
            snapshot = preparation.Task.GetAwaiter().GetResult();
            if (snapshot == null || !snapshot.AnchorsResolved)
            {
                Debug.LogError($"[DUNGEON-PREP] instance='{key}' zone={zoneName} layoutSeed=0x{layoutSeed:X8} reason=missing-authored-anchor state=blocked");
                return false;
            }
            if (snapshot.RoomSeed == 0 && roomSeed != 0)
                snapshot.RoomSeed = roomSeed;
            lock (_proceduralPreparationLock)
                _proceduralSnapshots[key] = snapshot;
            BuildAndRegisterInstancePathMap(key, snapshot, rebuild: false);
            Debug.LogError($"[DUNGEON-PREP] instance='{key}' zone={zoneName} layoutSeed=0x{layoutSeed:X8} state=committed nodes={snapshot.PathMap?.NodeCount ?? 0}");
            return true;
        }

        private void BuildAndRegisterInstancePathMap(
            string key, DungeonMazeSpawner.ProceduralDungeonSnapshot snapshot, bool rebuild)
        {
            if (string.IsNullOrEmpty(key) || snapshot == null || snapshot.Cells == null || snapshot.Cells.Count == 0)
                return;
            PathMap registeredPathMap = PathMapCatalog.Instance.GetPathMap(key);
            if (!rebuild && _instancePathMapsBuilt.Contains(key) && ReferenceEquals(registeredPathMap, snapshot.PathMap))
                return;
            bool replacesRegisteredPathMap = registeredPathMap != null && !ReferenceEquals(registeredPathMap, snapshot.PathMap);
            PathMap pathMap = snapshot.PathMap;
            bool canonicalPrepared = pathMap != null && string.Equals(pathMap.ZoneName, key, StringComparison.OrdinalIgnoreCase);
            if (!canonicalPrepared)
            {
                IReadOnlyList<MazeGenerator.MazeCell> worldCells = snapshot.WorldCells != null && snapshot.WorldCells.Count > 0
                    ? snapshot.WorldCells
                    : snapshot.Cells;
                WorldCollision.Instance.ClearInstance(key);
                WorldCollision.Instance.PrepareProceduralInstance(snapshot.ZoneName, key, worldCells);
                pathMap = DungeonRunners.Utilities.PathMapBuilder.Build(
                    key,
                    worldCells,
                    snapshot.PlayerSpawnFixedX,
                    snapshot.PlayerSpawnFixedY,
                    snapshot.PlayerSpawnFixedZ);
            }
            if (pathMap == null)
            {
                Debug.LogError($"[DUNGEON-SNAPSHOT] instance='{key}' zone={snapshot.ZoneName} reason=pathmap-build-null cells={snapshot.Cells.Count}");
                return;
            }
            snapshot.PathMap = pathMap;
            if (replacesRegisteredPathMap)
                CombatRuntime.Instance.RemoveMoveToPointPathManagersForInstance(key);
            PathMapCatalog.Instance.RegisterInstancePathMap(key, pathMap);
            PathMapGraphCapture.TryCapture(key, snapshot.LayoutSeed, snapshot.RoomSeed, pathMap);
            _instancePathMapsBuilt.Add(key);
            Debug.LogError($"[DUNGEON-SNAPSHOT] instance='{key}' layoutSeed=0x{snapshot.LayoutSeed:X8} pathmapLifecycle={(canonicalPrepared ? (replacesRegisteredPathMap ? "replace-generated" : "reuse-generated") : "fallback-rebuild")} nodes={pathMap.NodeCount} walkable={pathMap.WalkableCount}");
        }

        public bool TryGetProceduralSnapshot(string instanceKey, out DungeonMazeSpawner.ProceduralDungeonSnapshot snapshot)
        {
            snapshot = null;
            if (string.IsNullOrEmpty(instanceKey))
                return false;
            lock (_proceduralPreparationLock)
                return _proceduralSnapshots.TryGetValue(instanceKey, out snapshot);
        }

        private static bool MatchesBaseZoneInstanceKey(string zoneKey, string baseZoneName)
        {
            if (string.IsNullOrWhiteSpace(zoneKey) || string.IsNullOrWhiteSpace(baseZoneName))
                return false;
            return zoneKey.Equals(baseZoneName, StringComparison.OrdinalIgnoreCase) ||
                   zoneKey.StartsWith(baseZoneName + "_inst", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsProceduralInstanceKey(string zoneKey)
        {
            if (string.IsNullOrWhiteSpace(zoneKey))
                return false;
            int instIndex = zoneKey.IndexOf("_inst", StringComparison.OrdinalIgnoreCase);
            if (instIndex <= 0)
                return false;
            string baseZone = zoneKey.Substring(0, instIndex);
            return DungeonMazeSpawner.IsProceduralZone(baseZone);
        }

        private void AddMatchingResetKeys(IEnumerable<string> keys, string baseZoneName, HashSet<string> resetKeys)
        {
            if (keys == null || resetKeys == null)
                return;
            foreach (string key in keys)
            {
                if (MatchesBaseZoneInstanceKey(key, baseZoneName))
                    resetKeys.Add(key);
            }
        }

        private void ClearPathCollisionState(string zoneKey, string reason)
        {
            if (string.IsNullOrWhiteSpace(zoneKey))
                return;
            bool hadInstancePathMap = _instancePathMapsBuilt.Remove(zoneKey);
            CombatRuntime.Instance.RemoveMoveToPointPathManagersForInstance(zoneKey);
            if (hadInstancePathMap || IsProceduralInstanceKey(zoneKey))
            {
                PathMapCatalog.Instance.UnregisterInstancePathMap(zoneKey);
            }
            WorldCollision.Instance.ClearInstance(zoneKey);
            Debug.LogError($"[ZONE-SPAWNER-RESET] key='{zoneKey}' reason='{reason ?? "unknown"}' pathMapUnregistered={hadInstancePathMap || IsProceduralInstanceKey(zoneKey)} worldCollisionCleared=True");
        }

        private void InvalidateProceduralPreparation(string zoneKey)
        {
            if (string.IsNullOrWhiteSpace(zoneKey))
                return;
            lock (_proceduralPreparationLock)
            {
                if (_proceduralPreparations.TryGetValue(zoneKey, out var preparation))
                    preparation.Invalidated = true;
            }
        }

        public List<Monster> SpawnZoneMobsForInstance(string zoneName, string instanceKey, uint? seed = null, uint roomSeed = 0)
        {
            var spawned = new List<Monster>();

            if (string.IsNullOrEmpty(zoneName))
                return spawned;

            if (_spawnedZones.Contains(instanceKey))
            {
                Debug.LogError($"[ZONE-SPAWNER] instance='{instanceKey}' reason=already-spawned");
                return spawned;
            }

            List<DungeonSpawnData> spawnDefs;
            bool proceduralSpawns = DungeonMazeSpawner.IsProceduralZone(zoneName);
            if (proceduralSpawns)
            {
                if (!seed.HasValue || seed.Value == 0)
                {
                    Debug.LogError($"[ZONE-SPAWNER] instance='{instanceKey}' zone='{zoneName}' reason=missing-layout-seed state=blocked");
                    return spawned;
                }
                var snapshot = GetOrCreateProceduralSnapshot(zoneName, instanceKey, seed.Value, roomSeed);
                spawnDefs = snapshot?.Spawns;
            }
            else if (DungeonMazeSpawner.HasStaticEncounterData(zoneName))
            {
                spawnDefs = GetOrCreateStaticSnapshot(zoneName, instanceKey, seed.GetValueOrDefault())?.Spawns;
            }
            else
            {
                spawnDefs = GetSpawnData(zoneName, seed);
            }

            if (spawnDefs == null || spawnDefs.Count == 0)
            {
                Debug.LogError($"[ZONE-SPAWNER] zone='{zoneName}' reason=no-spawn-data");
                return spawned;
            }

            Debug.LogError($"[ZONE-SPAWNER] spawn instance='{instanceKey}' zone='{zoneName}' count={spawnDefs.Count}");

            if (proceduralSpawns)
            {
                if (!RegisterEncounterObjects(instanceKey, zoneName, spawnDefs, true))
                    return spawned;
                _spawnedZones.Add(instanceKey);
                int encounterCount = _encounterObjects.TryGetValue(instanceKey, out var pe) ? pe.Count : 0;
                Debug.LogError($"[ENCOUNTER-OBJECT] instance='{instanceKey}' zone={zoneName} deferred {spawnDefs.Count} spawns into {encounterCount} encounters scan={ENCOUNTER_SCAN_TICKS}t sourceFunction=EncounterObject::update+ScanForPlayer");
                return spawned;
            }

            var pathMap = DungeonRunners.Core.PathMapCatalog.Instance.GetPathMap(instanceKey)
                ?? DungeonRunners.Core.PathMapCatalog.Instance.GetPathMap(zoneName);
            if (!RegisterEncounterObjects(instanceKey, zoneName, spawnDefs, false))
                return spawned;

            foreach (var spawnDef in spawnDefs)
            {
                var monster = CombatRuntime.Instance.SpawnMonsterFixed(
                    spawnDef.gcType,
                    spawnDef.PosFixedX,
                    spawnDef.PosFixedY,
                    spawnDef.PosFixedZ,
                    spawnDef.HeadingFixed,
                    zoneName,
                    spawnDef.encounterGroupKey,
                    spawnDef.encounterDifficultyF32 >= 0 ? spawnDef.encounterDifficultyF32 : 0x100,
                    spawnDef.spawnGcTypeOverride,
                    instanceKey,
                    spawnDef.encounterLevelOffset
                );

                if (monster != null)
                {
                    AssignEncounterObjectEntity(monster);
                    spawned.Add(monster);
                    Debug.LogError($"[DUNGEON-SPAWN] instance='{instanceKey}' zone={zoneName} role={spawnDef.placementRole ?? ""} group={spawnDef.encounterGroupKey ?? ""} grid=({spawnDef.gridX},{spawnDef.gridY}) tile='{spawnDef.tileType ?? ""}' originFixed=({spawnDef.WorldOriginFixedX},{spawnDef.WorldOriginFixedY}) localFixed=({spawnDef.LocalFixedX},{spawnDef.LocalFixedY},{spawnDef.LocalFixedZ}) placeholder='{spawnDef.placeholderSource ?? ""}' marker={spawnDef.placeholderIndex} sizeFixed=({spawnDef.PlaceholderSizeFixedX},{spawnDef.PlaceholderSizeFixedY}) choice={spawnDef.encounterChoiceIndex} snapApplied={spawnDef.snapApplied} gc='{spawnDef.gcType}' spawnGc='{spawnDef.spawnGcTypeOverride ?? ""}' posFixed=({spawnDef.PosFixedX},{spawnDef.PosFixedY},{spawnDef.PosFixedZ}) headingFixed={spawnDef.HeadingFixed} difficultyF32={spawnDef.encounterDifficultyF32} levelOffset={spawnDef.encounterLevelOffset} zSource={(proceduralSpawns ? "snapshot" : "pathmap")} monster={monster.Name} level={monster.Level} tier={monster.Tier ?? ""} maxHP={monster.MaxHPWire}");
                }
            }


            _spawnedZones.Add(instanceKey);
            Debug.LogError($"[ZONE-SPAWNER] complete instance='{instanceKey}' spawned={spawned.Count}/{spawnDefs.Count}");
            return spawned;
        }

        private bool RegisterEncounterObjects(string instanceKey, string zoneName, List<DungeonSpawnData> spawnDefs, bool deferred)
        {
            instanceKey = RoomRuntime.NormalizeInstanceKey(instanceKey);
            var byGroup = new Dictionary<string, EncounterObject>(StringComparer.OrdinalIgnoreCase);
            var encounterOrder = new List<EncounterObject>();
            foreach (var spawnDef in spawnDefs)
            {
                string key = spawnDef.encounterGroupKey;
                if (string.IsNullOrWhiteSpace(key))
                {
                    if (!deferred)
                        continue;
                    key = $"solo:{spawnDef.PosFixedX},{spawnDef.PosFixedY}";
                }
                if (!byGroup.TryGetValue(key, out var encounter))
                {
                    encounter = new EncounterObject { GroupKey = key, ScanTimer = 0, Spawned = !deferred };
                    byGroup[key] = encounter;
                    encounterOrder.Add(encounter);
                }
                encounter.Defs.Add(spawnDef);
            }

            if (encounterOrder.Count == 0)
            {
                RemoveEncounterObjects(instanceKey);
                _pendingZone.Remove(instanceKey);
                return true;
            }

            var list = new List<EncounterObject>(encounterOrder.Count);
            for (int encounterIndex = 0; encounterIndex < encounterOrder.Count; encounterIndex++)
            {
                EncounterObject encounter = encounterOrder[encounterIndex];
                long sumFixedX = 0, sumFixedY = 0;
                int choiceIndex = -1;
                foreach (var spawnDef in encounter.Defs)
                {
                    sumFixedX += spawnDef.PosFixedX;
                    sumFixedY += spawnDef.PosFixedY;
                    if (choiceIndex < 0 && spawnDef.encounterChoiceIndex >= 0)
                        choiceIndex = spawnDef.encounterChoiceIndex;
                }
                encounter.CenterFixedX = (int)(sumFixedX / encounter.Defs.Count);
                encounter.CenterFixedY = (int)(sumFixedY / encounter.Defs.Count);
                encounter.ScanRadiusF32 = ResolveEncounterScanRadiusF32(encounter.Defs);
                ResolveEncounterBoundsFixed(encounter.Defs, encounter.CenterFixedX, encounter.CenterFixedY, encounter.ScanRadiusF32,
                    out encounter.MinFixedX, out encounter.MinFixedY, out encounter.MaxFixedX, out encounter.MaxFixedY);
                encounter.ChoiceIndex = choiceIndex;
                list.Add(encounter);
            }
            if (!CombatRuntime.Instance.TryAllocateEncounterObjectIds(list.Count, out uint[] encounterEntityIds))
            {
                Debug.LogError($"[ENCOUNTER-OBJECT] instance='{instanceKey}' zone='{zoneName}' state=blocked reason=combat-network-id-exhausted requested={list.Count} range=0x{CombatRuntime.CombatNetworkIdMin:X4}-0x{CombatRuntime.CombatNetworkIdMax:X4}");
                return false;
            }
            RemoveEncounterObjects(instanceKey);
            _pendingZone.Remove(instanceKey);
            for (int encounterIndex = 0; encounterIndex < list.Count; encounterIndex++)
            {
                EncounterObject encounter = list[encounterIndex];
                encounter.EntityId = encounterEntityIds[encounterIndex];
                CombatRuntime.Instance.RegisterEncounterObjectEntity(encounter.EntityId, instanceKey);
            }
            _pendingZone[instanceKey] = zoneName;
            _encounterObjects[instanceKey] = list;
            return true;
        }

        private void AssignEncounterObjectEntity(Monster monster)
        {
            if (monster == null || string.IsNullOrWhiteSpace(monster.EncounterGroupKey))
                return;
            if (!TryGetEncounterObjectEntityId(monster.InstanceKey, monster.EncounterGroupKey, out uint entityId))
                throw new InvalidOperationException($"Encounter root missing for monster {monster.EntityId} group '{monster.EncounterGroupKey}' instance '{monster.InstanceKey}'");
            monster.EncounterObjectEntityId = entityId;
        }

        public bool TryGetEncounterObjectEntityId(string instanceKey, string groupKey, out uint entityId)
        {
            entityId = 0;
            if (string.IsNullOrWhiteSpace(instanceKey) || string.IsNullOrWhiteSpace(groupKey))
                return false;
            if (!_encounterObjects.TryGetValue(RoomRuntime.NormalizeInstanceKey(instanceKey), out List<EncounterObject> encounters))
                return false;
            foreach (EncounterObject encounter in encounters)
            {
                if (!string.Equals(encounter.GroupKey, groupKey, StringComparison.OrdinalIgnoreCase))
                    continue;
                entityId = encounter.EntityId;
                return entityId != 0;
            }
            return false;
        }

        public bool TryGetDeferredEncounterObjectSpawnSnapshot(string instanceKey, uint entityId, out int posFixedX, out int posFixedY, out int posFixedZ, out int headingFixed)
        {
            posFixedX = 0;
            posFixedY = 0;
            posFixedZ = 0;
            headingFixed = 0;
            if (entityId == 0 || string.IsNullOrWhiteSpace(instanceKey))
                return false;
            if (!_encounterObjects.TryGetValue(RoomRuntime.NormalizeInstanceKey(instanceKey), out List<EncounterObject> encounters))
                return false;
            foreach (EncounterObject encounter in encounters)
            {
                if (encounter.EntityId != entityId || encounter.Spawned || encounter.Defs.Count == 0)
                    continue;
                DungeonSpawnData first = encounter.Defs[0];
                posFixedX = first.PosFixedX;
                posFixedY = first.PosFixedY;
                posFixedZ = first.PosFixedZ;
                headingFixed = first.HeadingFixed;
                return true;
            }
            return false;
        }

        private void RemoveEncounterObjects(string instanceKey)
        {
            if (string.IsNullOrWhiteSpace(instanceKey))
                return;
            instanceKey = RoomRuntime.NormalizeInstanceKey(instanceKey);
            if (!_encounterObjects.TryGetValue(instanceKey, out List<EncounterObject> encounters))
                return;
            foreach (EncounterObject encounter in encounters)
                CombatRuntime.Instance.UnregisterEncounterObjectEntity(encounter.EntityId);
            _encounterObjects.Remove(instanceKey);
        }

        private static int ResolveEncounterScanRadiusF32(List<DungeonSpawnData> defs)
        {
            int radiusF32 = 0;
            if (defs != null)
            {
                foreach (var def in defs)
                {
                    if (def == null)
                        continue;

                    radiusF32 = Math.Max(radiusF32, ResolveEncounterExtentXF32(def));
                    radiusF32 = Math.Max(radiusF32, ResolveEncounterExtentYF32(def));
                }
            }

            return Math.Max(EncounterScanRadiusFloorF32, radiusF32);
        }

        private static int ResolveEncounterExtentXF32(DungeonSpawnData def)
        {
            return def != null && def.PlaceholderSizeFixedX > 0
                ? def.PlaceholderSizeFixedX / 2
                : 0;
        }

        private static int ResolveEncounterExtentYF32(DungeonSpawnData def)
        {
            return def != null && def.PlaceholderSizeFixedY > 0
                ? def.PlaceholderSizeFixedY / 2
                : 0;
        }

        private static void ResolveEncounterBoundsFixed(List<DungeonSpawnData> defs, int centerFixedX, int centerFixedY, int scanRadiusF32,
            out int minFixedX, out int minFixedY, out int maxFixedX, out int maxFixedY)
        {
            minFixedX = int.MaxValue;
            minFixedY = int.MaxValue;
            maxFixedX = int.MinValue;
            maxFixedY = int.MinValue;

            if (defs != null)
            {
                foreach (var def in defs)
                {
                    if (def == null)
                        continue;

                    int triggerFixedX = def.WorldOriginFixedX + def.LocalFixedX;
                    int triggerFixedY = def.WorldOriginFixedY + def.LocalFixedY;
                    if (def.placeholderIndex < 0 && triggerFixedX == 0 && triggerFixedY == 0)
                    {
                        triggerFixedX = def.PosFixedX;
                        triggerFixedY = def.PosFixedY;
                    }

                    int halfF32X = ResolveEncounterExtentXF32(def);
                    int halfF32Y = ResolveEncounterExtentYF32(def);
                    minFixedX = Math.Min(minFixedX, triggerFixedX - halfF32X);
                    minFixedY = Math.Min(minFixedY, triggerFixedY - halfF32Y);
                    maxFixedX = Math.Max(maxFixedX, triggerFixedX + halfF32X);
                    maxFixedY = Math.Max(maxFixedY, triggerFixedY + halfF32Y);
                }
            }

            if (minFixedX == int.MaxValue)
            {
                int radiusF32 = scanRadiusF32 > 0 ? scanRadiusF32 : EncounterScanRadiusFloorF32;
                minFixedX = centerFixedX - radiusF32;
                minFixedY = centerFixedY - radiusF32;
                maxFixedX = centerFixedX + radiusF32;
                maxFixedY = centerFixedY + radiusF32;
            }
        }

        public List<Monster> UpdateEncounterObject(uint entityId, string instanceKey, List<(int x, int y)> playerPositions)
        {
            var spawnedMonsters = new List<Monster>();
            if (entityId == 0 || string.IsNullOrEmpty(instanceKey) || playerPositions == null || playerPositions.Count == 0)
                return spawnedMonsters;
            if (!_encounterObjects.TryGetValue(instanceKey, out var list) || list == null)
                return spawnedMonsters;
            string zoneName = _pendingZone.TryGetValue(instanceKey, out var pendingZoneName) ? pendingZoneName : instanceKey;

            EncounterObject encounter = null;
            foreach (EncounterObject candidate in list)
            {
                if (candidate.EntityId == entityId)
                {
                    encounter = candidate;
                    break;
                }
            }
            if (encounter == null || encounter.Spawned)
                return spawnedMonsters;
            if (encounter.ScanTimer > 0)
            {
                encounter.ScanTimer--;
                if (encounter.ScanTimer > 0)
                    return spawnedMonsters;
            }
            encounter.ScanTimer = ENCOUNTER_SCAN_TICKS;

            int scanRadiusF32 = encounter.ScanRadiusF32 > 0 ? encounter.ScanRadiusF32 : EncounterScanRadiusFloorF32;
            bool playerInRange = false;
            var scanPositionParts = new List<string>(playerPositions.Count);
            foreach (var playerPosition in playerPositions)
            {
                scanPositionParts.Add($"({playerPosition.x},{playerPosition.y})");
                int nearestFixedX = Math.Clamp(playerPosition.x, encounter.MinFixedX, encounter.MaxFixedX);
                int nearestFixedY = Math.Clamp(playerPosition.y, encounter.MinFixedY, encounter.MaxFixedY);
                long ddx = (long)playerPosition.x - nearestFixedX;
                long ddy = (long)playerPosition.y - nearestFixedY;
                if (ddx * ddx + ddy * ddy <= (long)scanRadiusF32 * scanRadiusF32)
                {
                    playerInRange = true;
                    break;
                }
            }
            Debug.LogError($"[ENCOUNTER-LAZY-SCAN] instance='{instanceKey}' entity={entityId} group='{encounter.GroupKey}' tick={CombatRuntime.Instance.CombatTick} players={string.Join(",", scanPositionParts)} bounds=({encounter.MinFixedX},{encounter.MinFixedY})->({encounter.MaxFixedX},{encounter.MaxFixedY}) radiusF32={scanRadiusF32} result={(playerInRange ? "spawn" : "defer")} nextScanTicks={ENCOUNTER_SCAN_TICKS} positionLane=ClientSimulationPosFixed sourceFunction=EncounterObject::ScanForPlayer@0x00563000");
            if (!playerInRange)
                return spawnedMonsters;

            encounter.Spawned = true;
            int spawnedCount = 0;
            foreach (var spawnDef in encounter.Defs)
            {
                int clearFixedX = spawnDef.PosFixedX, clearFixedY = spawnDef.PosFixedY;
                WorldCollisionHit spawnBlocker = null;
                SpawnClearanceResult clearanceResult = SpawnClearanceResult.ClearOriginal;
                if (spawnDef.placeholderIndex < 0)
                    clearanceResult = WorldCollision.Instance.ResolveSpawnClearanceFixed(
                    zoneName,
                    instanceKey,
                    spawnDef.PosFixedX,
                    spawnDef.PosFixedY,
                    spawnDef.PosFixedZ,
                    DungeonMazeSpawner.ResolveSpawnRadius(spawnDef.spawnGcTypeOverride ?? spawnDef.gcType),
                    out clearFixedX,
                    out clearFixedY,
                    out spawnBlocker);
                int spawnFixedX = clearanceResult == SpawnClearanceResult.Adjusted ? clearFixedX : spawnDef.PosFixedX;
                int spawnFixedY = clearanceResult == SpawnClearanceResult.Adjusted ? clearFixedY : spawnDef.PosFixedY;
                    Debug.LogError($"[ENCOUNTER-SPAWN-COBJ] instance='{instanceKey}' group='{encounter.GroupKey}' gc='{spawnDef.gcType}' result={clearanceResult} rawF32=({spawnDef.PosFixedX},{spawnDef.PosFixedY},{spawnDef.PosFixedZ}) selectedF32=({clearFixedX},{clearFixedY}) blocker='{spawnBlocker?.ObjectPath ?? "none"}' collision='{spawnBlocker?.CollisionObject ?? "none"}' hybrid={spawnBlocker?.Hybrid ?? false} hybridSource='{spawnBlocker?.HybridSource ?? ""}' tile='{spawnBlocker?.TileType ?? "none"}' grid=({spawnBlocker?.GridX ?? -1},{spawnBlocker?.GridY ?? -1}) sourceFunction=HybridCollisionObject::testCollision");
                var monster = CombatRuntime.Instance.SpawnMonsterFixed(
                    spawnDef.gcType,
                    spawnFixedX,
                    spawnFixedY,
                    spawnDef.PosFixedZ,
                    spawnDef.HeadingFixed,
                    zoneName,
                    encounter.GroupKey,
                    spawnDef.encounterDifficultyF32 >= 0 ? spawnDef.encounterDifficultyF32 : 0x100,
                    spawnDef.spawnGcTypeOverride,
                    instanceKey,
                    spawnDef.encounterLevelOffset);
                if (monster != null)
                {
                    monster.EncounterObjectEntityId = encounter.EntityId;
                    PathMap pathMap = PathMapCatalog.Instance.GetPathMap(instanceKey)
                        ?? PathMapCatalog.Instance.GetPathMap(zoneName);
                    int anchorFixedX = spawnDef.WorldOriginFixedX + spawnDef.LocalFixedX;
                    int anchorFixedY = spawnDef.WorldOriginFixedY + spawnDef.LocalFixedY;
                    PathReachability reachability = pathMap != null
                        ? pathMap.GetReachabilityFixed(anchorFixedX, anchorFixedY, monster.PosFixedX, monster.PosFixedY)
                        : PathReachability.CoverageMissing;
                    PathNode node = pathMap?.GetNodeAtWorldFixed(monster.PosFixedX, monster.PosFixedY);
                    bool finalBlocked = WorldCollision.Instance.TryGetPointBlockerFixed(
                        zoneName,
                        instanceKey,
                        monster.PosFixedX,
                        monster.PosFixedY,
                        monster.PosFixedZ,
                        Math.Max(0, monster.CollisionRadiusF32),
                        out WorldCollisionHit finalBlocker);
                    Debug.LogError($"[ENCOUNTER-SPAWN-SURFACE] instance='{instanceKey}' group='{encounter.GroupKey}' entity={monster.EntityId} authoredF32=({spawnDef.PosFixedX},{spawnDef.PosFixedY},{spawnDef.PosFixedZ}) finalF32=({monster.PosFixedX},{monster.PosFixedY},{monster.PosFixedZ}) anchorF32=({anchorFixedX},{anchorFixedY}) clearanceRadiusF32={DungeonMazeSpawner.ResolveSpawnRadius(spawnDef.spawnGcTypeOverride ?? spawnDef.gcType)} finalRadiusF32={monster.CollisionRadiusF32} node=({node?.GridX ?? -1},{node?.GridY ?? -1}) nodeZ={node?.HeightFixed ?? int.MinValue} flags=0x{(node?.ConnectionFlags ?? 0):X2} rayReachability={reachability} finalBlocked={finalBlocked} blocker='{finalBlocker?.ObjectPath ?? "none"}' collision='{finalBlocker?.CollisionObject ?? "none"}' sourceFunction=Spawn::readData+Unit::setPosition+PathMap::GetMapHeight");
                    spawnedMonsters.Add(monster);
                    spawnedCount++;
                }
            }
            Debug.LogError($"[ENCOUNTER-OBJECT] gen entity={encounter.EntityId} instance='{instanceKey}' group='{encounter.GroupKey}' centerF32=({encounter.CenterFixedX},{encounter.CenterFixedY}) bboxF32=({encounter.MinFixedX},{encounter.MinFixedY})-({encounter.MaxFixedX},{encounter.MaxFixedY}) choice={encounter.ChoiceIndex} spawned={spawnedCount} scanRadiusF32={encounter.ScanRadiusF32} sourceFunction=EncounterObject::update+GenerateObjectFromTable");
            return spawnedMonsters;
        }

        public bool HasSpawnsForZone(string zoneName)
        {
            return DungeonMazeSpawner.IsProceduralZone(zoneName) ||
                   DungeonMazeSpawner.HasStaticEncounterData(zoneName) ||
                   AuthoredGameplayCatalog.DungeonSpawns.ContainsKey(zoneName);
        }

        public bool IsZoneSpawned(string zoneName)
        {
            return _spawnedZones.Contains(zoneName);
        }

        public void ResetZone(string zoneName)
        {
            _spawnedZones.Remove(zoneName);
            lock (_proceduralPreparationLock)
                _proceduralSnapshots.Remove(zoneName);
            InvalidateProceduralPreparation(zoneName);
            RemoveEncounterObjects(zoneName);
            _pendingZone.Remove(zoneName);
            ClearAuthoredInstance(zoneName);
            ClearPathCollisionState(zoneName, "ResetZone");
        }

        public void ResetZoneSpawnState(string zoneName)
        {
            _spawnedZones.Remove(zoneName);
            RemoveEncounterObjects(zoneName);
            _pendingZone.Remove(zoneName);
        }

        public void ResetZoneAndInstances(string baseZoneName)
        {
            var resetKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(baseZoneName))
                resetKeys.Add(baseZoneName);
            AddMatchingResetKeys(_spawnedZones, baseZoneName, resetKeys);
            AddMatchingResetKeys(_proceduralSnapshots.Keys, baseZoneName, resetKeys);
            AddMatchingResetKeys(_staticSnapshots.Keys, baseZoneName, resetKeys);
            AddMatchingResetKeys(_layoutQuests.Keys, baseZoneName, resetKeys);
            AddMatchingResetKeys(_encounterObjects.Keys, baseZoneName, resetKeys);
            AddMatchingResetKeys(_pendingZone.Keys, baseZoneName, resetKeys);
            AddMatchingResetKeys(_instancePathMapsBuilt, baseZoneName, resetKeys);
            lock (_proceduralPreparationLock)
                AddMatchingResetKeys(_proceduralPreparations.Keys, baseZoneName, resetKeys);

            var toRemove = _spawnedZones
                .Where(z => z.Equals(baseZoneName, StringComparison.OrdinalIgnoreCase) ||
                            z.StartsWith(baseZoneName + "_inst", StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var zoneKey in toRemove)
            {
                _spawnedZones.Remove(zoneKey);
                lock (_proceduralPreparationLock)
                    _proceduralSnapshots.Remove(zoneKey);
            }
            lock (_proceduralPreparationLock)
            {
                var snapshotKeys = _proceduralSnapshots.Keys
                    .Where(z => z.Equals(baseZoneName, StringComparison.OrdinalIgnoreCase) ||
                                z.StartsWith(baseZoneName + "_inst", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                foreach (var zoneKey in snapshotKeys)
                    _proceduralSnapshots.Remove(zoneKey);
                _proceduralSnapshots.Remove(baseZoneName);
            }
            foreach (string zoneKey in resetKeys)
            {
                RemoveEncounterObjects(zoneKey);
                _pendingZone.Remove(zoneKey);
                InvalidateProceduralPreparation(zoneKey);
                ClearAuthoredInstance(zoneKey);
                ClearPathCollisionState(zoneKey, "ResetZoneAndInstances");
            }
            Debug.LogError($"[ZONE-SPAWNER] reset zone='{baseZoneName}' cleared={toRemove.Count}");
        }

        public void ResetAll()
        {
            var resetKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in _spawnedZones) resetKeys.Add(key);
            foreach (string key in _proceduralSnapshots.Keys) resetKeys.Add(key);
            foreach (string key in _staticSnapshots.Keys) resetKeys.Add(key);
            foreach (string key in _layoutQuests.Keys) resetKeys.Add(key);
            foreach (string key in _encounterObjects.Keys) resetKeys.Add(key);
            foreach (string key in _pendingZone.Keys) resetKeys.Add(key);
            foreach (string key in _instancePathMapsBuilt) resetKeys.Add(key);
            lock (_proceduralPreparationLock)
                foreach (string key in _proceduralPreparations.Keys) resetKeys.Add(key);

            _spawnedZones.Clear();
            lock (_proceduralPreparationLock)
            {
                _proceduralSnapshots.Clear();
                foreach (ProceduralSnapshotPreparation preparation in _proceduralPreparations.Values)
                    preparation.Invalidated = true;
            }
            foreach (string instanceKey in new List<string>(_encounterObjects.Keys))
                RemoveEncounterObjects(instanceKey);
            _pendingZone.Clear();
            foreach (string zoneKey in resetKeys)
            {
                ClearAuthoredInstance(zoneKey);
                ClearPathCollisionState(zoneKey, "ResetAll");
            }
            _instancePathMapsBuilt.Clear();
        }
    }
}
