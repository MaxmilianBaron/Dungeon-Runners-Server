using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using DungeonRunners.Data;
using DungeonRunners.Engine;

namespace DungeonRunners.Core
{
    public enum SpawnClearanceResult : byte
    {
        CoverageMissing,
        ClearOriginal,
        Adjusted,
        BlockedUnresolved
    }

    public sealed class WorldCollisionHit
    {
        public bool Blocked;
        public string ZoneName;
        public string InstanceKey;
        public string TileType;
        public int GridX;
        public int GridY;
        public string ObjectPath;
        public string CollisionObject;
        public bool Hybrid;
        public string HybridSource;
        public int WorldFixedX;
        public int WorldFixedY;
        public int WorldFixedZ;
        public int LocalFixedX;
        public int LocalFixedY;
        public int LocalFixedZ;
        public int DistanceFixed;

    }

    public sealed class WorldCollision
    {
        private sealed class CollisionBox
        {
            public string ZoneName;
            public string InstanceKey;
            public string TileType;
            public int GridX;
            public int GridY;
            public int TileOriginFixedX;
            public int TileOriginFixedY;
            public string ObjectPath;
            public string CollisionObject;
            public int PositionFixedX;
            public int PositionFixedY;
            public int PositionFixedZ;
            public int HeadingFixed32;
            public int HeadingDegrees;
            public int HeadingCosFixed;
            public int HeadingSinFixed;
            public bool HeightOnly;
            public bool EntityBounds;
            public int MinFixedX;
            public int MinFixedY;
            public int MinFixedZ;
            public int MaxFixedX;
            public int MaxFixedY;
            public int MaxFixedZ;
            public HybridCollisionObject Hybrid;
            public string HybridSource;
            public int SourceOrder;
            public int WorldMinFixedX;
            public int WorldMinFixedY;
            public int WorldMinFixedZ;
            public int WorldMaxFixedX;
            public int WorldMaxFixedY;
            public int WorldMaxFixedZ;
            public int NativeMinCellX;
            public int NativeMinCellY;
            public int NativeMinCellZ;
            public int NativeMaxCellX;
            public int NativeMaxCellY;
            public int NativeMaxCellZ;
            public readonly List<NativeCollisionCellKey> NativeCellKeys = new List<NativeCollisionCellKey>();
        }

        private sealed class CollisionCache
        {
            public string ZoneName;
            public string InstanceKey;
            public readonly List<CollisionBox> Boxes = new List<CollisionBox>();
            public readonly List<CollisionBox> HeightSurfaces = new List<CollisionBox>();
            public readonly Dictionary<NativeCollisionCellKey, List<CollisionBox>> NativeHeightCells = new Dictionary<NativeCollisionCellKey, List<CollisionBox>>();
            public readonly Dictionary<(int X, int Y), (int MinZ, int MaxZ)> NativeHeightColumnRanges = new Dictionary<(int X, int Y), (int MinZ, int MaxZ)>();
            public readonly Dictionary<NativeCollisionCellKey, List<CollisionBox>> NativeCells = new Dictionary<NativeCollisionCellKey, List<CollisionBox>>();
            public readonly List<CollisionBox> NativeUnbucketedBoxes = new List<CollisionBox>();
            public int NextBoxSourceOrder;
            public bool HasNativeCellExtents;
            public int NativeMinCellX;
            public int NativeMinCellY;
            public int NativeMinCellZ;
            public int NativeMaxCellX;
            public int NativeMaxCellY;
            public int NativeMaxCellZ;
        }

        private struct NativeCollisionCellKey : IEquatable<NativeCollisionCellKey>
        {
            public readonly int X;
            public readonly int Y;
            public readonly int Z;

            public NativeCollisionCellKey(int x, int y, int z)
            {
                X = x;
                Y = y;
                Z = z;
            }

            public bool Equals(NativeCollisionCellKey other)
            {
                return X == other.X && Y == other.Y && Z == other.Z;
            }

            public override bool Equals(object obj)
            {
                return obj is NativeCollisionCellKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = (hash * 31) + X;
                    hash = (hash * 31) + Y;
                    hash = (hash * 31) + Z;
                    return hash;
                }
            }
        }

        private sealed class StaticBounds
        {
            public string CollisionObject;
            public bool HeightOnly;
            public int MinFixedX;
            public int MinFixedY;
            public int MinFixedZ;
            public int MaxFixedX;
            public int MaxFixedY;
            public int MaxFixedZ;
            public HybridCollisionObject Hybrid;
            public string HybridSource;
        }

        private static WorldCollision _instance;
        public static WorldCollision Instance => _instance ??= new WorldCollision();

        private readonly Dictionary<string, CollisionCache> _instanceCaches = new Dictionary<string, CollisionCache>(StringComparer.OrdinalIgnoreCase);
        private readonly object _instanceCacheLock = new object();
        private readonly ConcurrentDictionary<string, GCNode> _authoredTextCache = new ConcurrentDictionary<string, GCNode>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, StaticBounds> _staticBoundsCache = new ConcurrentDictionary<string, StaticBounds>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _missingStaticBounds = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _loggedMissingDocs = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _loggedMissingBounds = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private int _startupCheckLogged;
        private const int NativeNoHeightFixed = -25600000;
        private const int SpawnPushOutStepFixed = 8 * 0x100;
        private const int SpawnPushOutMaxFixed = 200 * 0x100;
        private const int SpawnPushOutAngleStep = 30;
        private const int NativeCollisionCellSizeXYFixed = 0x50 * 0x100;
        private const int NativeCollisionCellSizeZFixed = 0x500 * 0x100;
        private const int NativeCollisionSectorCellsXY = 8;
        private const int NativeCollisionSectorSizeXYFixed = NativeCollisionCellSizeXYFixed * NativeCollisionSectorCellsXY;
        private const int NativeCollisionBoundaryInsetFixed = 0x100;

        public void ClearInstance(string instanceKey)
        {
            if (string.IsNullOrWhiteSpace(instanceKey))
                return;
            lock (_instanceCacheLock)
                _instanceCaches.Remove(instanceKey);
        }

        public void PrepareProceduralInstance(
            string zoneName,
            string instanceKey,
            IReadOnlyList<DungeonRunners.Gameplay.MazeGenerator.MazeCell> cells)
        {
            string key = !string.IsNullOrWhiteSpace(instanceKey) ? instanceKey : zoneName;
            if (string.IsNullOrWhiteSpace(key))
                return;

            CollisionCache cache = BuildProceduralCollisionCache(zoneName, key, cells);
            StoreCollisionCache(key, zoneName, cache, "prepared-before-pathmap");
        }

        public bool TrySegmentHitFixed(
            string zoneName,
            string instanceKey,
            int startFixedX,
            int startFixedY,
            int startFixedZ,
            int endFixedX,
            int endFixedY,
            int endFixedZ,
            int radiusFixed,
            out WorldCollisionHit hit)
        {
            hit = null;
            var cache = GetOrBuildCache(zoneName, instanceKey);
            bool blocked = TrySegmentHitFixed(cache, zoneName, startFixedX, startFixedY, startFixedZ, endFixedX, endFixedY, endFixedZ, radiusFixed, false, out hit);
            if (DungeonRunners.Core.ServerDiagnostics.IsEnabled("collisionTracking"))
                Debug.LogError($"[COLLISION-TRACK] query=segment zone='{zoneName ?? ""}' instance='{instanceKey ?? ""}' startFixed=({startFixedX},{startFixedY},{startFixedZ}) endFixed=({endFixedX},{endFixedY},{endFixedZ}) radiusFixed={radiusFixed} candidates={cache?.Boxes.Count ?? 0} result={(blocked ? "blocked" : "clear")} object='{hit?.ObjectPath ?? ""}' collision='{hit?.CollisionObject ?? ""}' hybrid={hit?.Hybrid ?? false} hybridSource='{hit?.HybridSource ?? ""}' hitFixed=({hit?.WorldFixedX ?? 0},{hit?.WorldFixedY ?? 0},{hit?.WorldFixedZ ?? 0})");
            return blocked;
        }

        private bool TrySegmentHitFixed(
            CollisionCache cache,
            string zoneName,
            int startFixedX,
            int startFixedY,
            int startFixedZ,
            int endFixedX,
            int endFixedY,
            int endFixedZ,
            int radiusFixed,
            bool requireHybrid,
            out WorldCollisionHit hit)
        {
            hit = null;
            if (cache == null || cache.Boxes.Count == 0)
                return false;
            List<CollisionBox> queryOrder = BuildNativeCollisionQueryOrder(
                cache,
                startFixedX,
                startFixedY,
                startFixedZ,
                endFixedX,
                endFixedY,
                endFixedZ,
                radiusFixed);
            for (int boxIndex = 0; boxIndex < queryOrder.Count; boxIndex++)
            {
                CollisionBox box = queryOrder[boxIndex];
                if (requireHybrid && box.Hybrid == null)
                    continue;
                int t;
                bool intersects;
                if (box.Hybrid != null)
                {
                    intersects = HybridSegmentIntersectsFixed(
                        box,
                        startFixedX,
                        startFixedY,
                        startFixedZ,
                        endFixedX,
                        endFixedY,
                        endFixedZ,
                        radiusFixed,
                        out t);
                }
                else
                {
                    intersects = AabbSegmentIntersectsFixed(
                        box,
                        startFixedX,
                        startFixedY,
                        startFixedZ,
                        endFixedX,
                        endFixedY,
                        endFixedZ,
                        radiusFixed,
                        out t);
                }
                if (!intersects)
                    continue;

                int hitX = LerpFixed(startFixedX, endFixedX, t);
                int hitY = LerpFixed(startFixedY, endFixedY, t);
                int hitZ = LerpFixed(startFixedZ, endFixedZ, t);
                hit = CreateHitFixed(box, hitX, hitY, hitZ, DistanceFixed(startFixedX, startFixedY, startFixedZ, hitX, hitY, hitZ));
                return true;
            }

            return false;
        }

        public bool IsPointBlockedFixed(string zoneName, string instanceKey, int worldFixedX, int worldFixedY, int worldFixedZ, int radiusFixed)
        {
            var cache = GetOrBuildCache(zoneName, instanceKey);
            if (cache == null || cache.Boxes.Count == 0)
                return false;
            return TryGetPointBlockerFixed(cache, worldFixedX, worldFixedY, worldFixedZ, radiusFixed, out _);
        }

        public bool TryGetPointBlockerFixed(
            string zoneName,
            string instanceKey,
            int worldFixedX,
            int worldFixedY,
            int worldFixedZ,
            int radiusFixed,
            out WorldCollisionHit hit)
        {
            hit = null;
            var cache = GetOrBuildCache(zoneName, instanceKey);
            if (cache == null || cache.Boxes.Count == 0)
            {
                if (DungeonRunners.Core.ServerDiagnostics.IsEnabled("collisionTracking"))
                    Debug.LogError($"[COLLISION-TRACK] query=point zone='{zoneName ?? ""}' instance='{instanceKey ?? ""}' pointFixed=({worldFixedX},{worldFixedY},{worldFixedZ}) radiusFixed={radiusFixed} result=coverage-missing");
                return false;
            }
            if (!TryGetPointBlockerFixed(cache, worldFixedX, worldFixedY, worldFixedZ, radiusFixed, out CollisionBox blocker))
                return false;
            hit = CreateHitFixed(blocker, worldFixedX, worldFixedY, worldFixedZ, 0);
            if (DungeonRunners.Core.ServerDiagnostics.IsEnabled("collisionTracking"))
                Debug.LogError($"[COLLISION-TRACK] query=point zone='{zoneName ?? ""}' instance='{instanceKey ?? ""}' pointFixed=({worldFixedX},{worldFixedY},{worldFixedZ}) radiusFixed={radiusFixed} result=blocked object='{hit.ObjectPath ?? ""}' collision='{hit.CollisionObject ?? ""}' hybrid={hit.Hybrid} hybridSource='{hit.HybridSource ?? ""}'");
            return true;
        }

        private bool TryGetPointBlockerFixed(CollisionCache cache, int worldFixedX, int worldFixedY, int worldFixedZ, int radiusFixed, out CollisionBox blocker)
        {
            blocker = null;
            radiusFixed = Math.Max(0, radiusFixed);
            List<CollisionBox> queryOrder = BuildNativeCollisionQueryOrder(
                cache,
                worldFixedX,
                worldFixedY,
                worldFixedZ,
                worldFixedX,
                worldFixedY,
                worldFixedZ,
                radiusFixed);
            for (int boxIndex = 0; boxIndex < queryOrder.Count; boxIndex++)
            {
                CollisionBox box = queryOrder[boxIndex];
                WorldToObjectLocalFixed(box, worldFixedX, worldFixedY, worldFixedZ, out int localX, out int localY, out int localZ);
                bool blocked = box.Hybrid != null
                    ? box.Hybrid.TestBoundingBoxFixed(localX, localY, localZ, radiusFixed)
                    : TestLocalAabbFixed(box, localX, localY, localZ, radiusFixed);
                if (blocked)
                {
                    blocker = box;
                    return true;
                }
            }
            return false;
        }

        public SpawnClearanceResult ResolveSpawnClearanceFixed(
            string zoneName,
            string instanceKey,
            int worldFixedX,
            int worldFixedY,
            int worldFixedZ,
            int radiusFixed,
            out int clearFixedX,
            out int clearFixedY,
            out WorldCollisionHit blocker)
        {
            clearFixedX = worldFixedX;
            clearFixedY = worldFixedY;
            blocker = null;
            var cache = GetOrBuildCache(zoneName, instanceKey);
            if (cache == null || cache.Boxes.Count == 0)
                return SpawnClearanceResult.CoverageMissing;
            if (!TryGetPointBlockerFixed(cache, worldFixedX, worldFixedY, worldFixedZ, radiusFixed, out CollisionBox originalBlocker))
                return SpawnClearanceResult.ClearOriginal;

            blocker = CreateHitFixed(originalBlocker, worldFixedX, worldFixedY, worldFixedZ, 0);

            for (int ringFixed = SpawnPushOutStepFixed; ringFixed <= SpawnPushOutMaxFixed; ringFixed += SpawnPushOutStepFixed)
            {
                for (int degrees = 0; degrees < 360; degrees += SpawnPushOutAngleStep)
                {
                    int cos = DungeonRunners.Combat.UnitMover.ZRotateCosFixed(degrees);
                    int sin = DungeonRunners.Combat.UnitMover.ZRotateSinFixed(degrees);
                    int candidateX = worldFixedX + (int)(((long)ringFixed * cos) >> 8);
                    int candidateY = worldFixedY + (int)(((long)ringFixed * sin) >> 8);
                    if (!TryGetPointBlockerFixed(cache, candidateX, candidateY, worldFixedZ, radiusFixed, out _))
                    {
                        clearFixedX = candidateX;
                        clearFixedY = candidateY;
                        return SpawnClearanceResult.Adjusted;
                    }
                }
            }
            return SpawnClearanceResult.BlockedUnresolved;
        }

        public bool TryGetTerrainHeightFixed(string zoneName, string instanceKey, int worldFixedX, int worldFixedY, int referenceFixedZ, out int heightFixed, out string source)
        {
            heightFixed = referenceFixedZ;
            source = null;
            var cache = GetOrBuildCache(zoneName, instanceKey);
            if (cache == null || cache.HeightSurfaces.Count == 0)
                return false;

            if (!TryGetTerrainHeightDirectFixed(cache, worldFixedX, worldFixedY, out CollisionBox best, out int bestHeight))
                return false;

            heightFixed = bestHeight;
            source = $"{best.ObjectPath}@{best.TileType}[{best.GridX},{best.GridY}]";
            return true;
        }

        private bool TryGetTerrainHeightDirectFixed(CollisionCache cache, int worldFixedX, int worldFixedY, out CollisionBox best, out int bestHeight)
        {
            best = null;
            bestHeight = NativeNoHeightFixed;
            if (cache == null || cache.HeightSurfaces.Count == 0)
                return false;

            List<CollisionBox> queryOrder = BuildNativeHeightSurfaceQueryOrder(cache, worldFixedX, worldFixedY);
            for (int surfaceIndex = 0; surfaceIndex < queryOrder.Count; surfaceIndex++)
            {
                CollisionBox surface = queryOrder[surfaceIndex];
                if (!TryGetSurfaceHeightFixed(surface, worldFixedX, worldFixedY, out int candidateHeight))
                    continue;

                if (best == null || candidateHeight > bestHeight)
                {
                    best = surface;
                    bestHeight = candidateHeight;
                }
            }

            return best != null;
        }

        public bool TryGetTerrainHeightHighestFixed(string zoneName, string instanceKey, int worldFixedX, int worldFixedY, out int heightFixed, out string source)
        {
            heightFixed = NativeNoHeightFixed;
            source = null;
            var cache = GetOrBuildCache(zoneName, instanceKey);
            if (cache == null || cache.HeightSurfaces.Count == 0)
                return false;

            if (!TryGetTerrainHeightDirectFixed(cache, worldFixedX, worldFixedY, out CollisionBox best, out int bestHeight))
                return false;

            heightFixed = bestHeight;
            source = $"{best.ObjectPath}@{best.TileType}[{best.GridX},{best.GridY}]";
            return true;
        }

        public bool TryGetTerrainHeightWithinToleranceFixed(string zoneName, string instanceKey, int worldFixedX, int worldFixedY, int referenceFixedZ, int toleranceFixed, out int heightFixed, out string source)
        {
            heightFixed = referenceFixedZ;
            if (!TryGetTerrainHeightFixed(zoneName, instanceKey, worldFixedX, worldFixedY, referenceFixedZ, out int candidateHeight, out source))
                return false;

            long delta = (long)candidateHeight - referenceFixedZ;
            if (delta < 0)
                delta = -delta;
            if (delta > Math.Max(0, toleranceFixed))
                return false;

            heightFixed = candidateHeight;
            return true;
        }

        public void RunStartupCheck()
        {
            if (System.Threading.Interlocked.Exchange(ref _startupCheckLogged, 1) != 0)
                return;

            bool wall = TryResolveStaticBounds("terrain.elmforest.walls.elmforest_4_straight_2", out StaticBounds wallBounds);
            bool rock = TryResolveStaticBounds("terrain.elmforest.decor.rocks.elmforest_rock_13", out StaticBounds rockBounds);
            bool tree = TryResolveStaticBounds("terrain.elmforest.decor.trees.elmforest_tree_13", out StaticBounds treeBounds);
            bool wallPointHit = wallBounds?.Hybrid != null &&
                wallBounds.Hybrid.TestBoundingBoxFixed(ToFixed8(-12), ToFixed8(-12), ToFixed8(2), ToFixed8(-12), ToFixed8(-12), ToFixed8(2));
            bool wallPointClear = wallBounds?.Hybrid != null &&
                !wallBounds.Hybrid.TestBoundingBoxFixed(ToFixed8(-12), ToFixed8(-12), ToFixed8(7), ToFixed8(-12), ToFixed8(-12), ToFixed8(7));
            bool treePointHit = treeBounds?.Hybrid != null &&
                treeBounds.Hybrid.TestBoundingBoxFixed(ToFixed8(1), ToFixed8(-9), ToFixed8(20), ToFixed8(1), ToFixed8(-9), ToFixed8(20));
            bool treePointClear = treeBounds?.Hybrid != null &&
                !treeBounds.Hybrid.TestBoundingBoxFixed(ToFixed8(1), ToFixed8(-9), ToFixed8(50), ToFixed8(1), ToFixed8(-9), ToFixed8(50));
            int bundledCobjCount = CountBundledCobjAssets();
            bool aabbSegmentBlocked = TrySegmentHitInTileFixed(
                "dungeon00_level01", "check", "elmforest_tileset_1e1w_a",
                ToFixed8(0), ToFixed8(800), 2, 4,
                ToFixed8(84), 246170, ToFixed8(10),
                ToFixed8(230), ToFixed8(970), ToFixed8(10),
                ToFixed8(10),
                out WorldCollisionHit blockedHit);
            bool clear = !TrySegmentHitInTileFixed(
                "dungeon00_level01", "check", "elmforest_tileset_1e1w_a",
                ToFixed8(0), ToFixed8(800), 2, 4,
                ToFixed8(240), ToFixed8(960), ToFixed8(10),
                ToFixed8(260), ToFixed8(970), ToFixed8(10),
                ToFixed8(10),
                out WorldCollisionHit clearHit);

            Debug.LogError($"[WORLD-COLLISION] tile=elmforest_tileset_1e1w_a wallBounds={wall} rockBounds={rock} treeBounds={tree} wall=({DescribeBounds(wallBounds)}) rock=({DescribeBounds(rockBounds)}) tree=({DescribeBounds(treeBounds)}) hybridWall={wallBounds?.Hybrid != null} hybridRock={rockBounds?.Hybrid != null} hybridTree={treeBounds?.Hybrid != null} bodyOffsetWall=0x{(wallBounds?.Hybrid?.BodyOffset ?? 0):X} authoredCobj={bundledCobjCount} pointWallHit={wallPointHit} pointWallClear={wallPointClear} pointTreeHit={treePointHit} pointTreeClear={treePointClear} segmentBlocked={aabbSegmentBlocked} segmentObj={blockedHit?.ObjectPath ?? "none"} segmentCollision={blockedHit?.CollisionObject ?? "none"} clear={clear} clearObj={clearHit?.ObjectPath ?? "none"} runtimeProjectileBlockers=hybrid-cobj source=authored.db:type4 sourceFunction=HybridCollisionObject::readObject+WorldCollisionObject::testCollision");
        }

        private static int CountBundledCobjAssets()
        {
            return DungeonRunners.Data.AuthoredSnapshotCatalog.Instance.CountBinaryPayloads(4);
        }

        private CollisionCache GetOrBuildCache(string zoneName, string instanceKey)
        {
            string key = !string.IsNullOrWhiteSpace(instanceKey) ? instanceKey : zoneName;
            if (string.IsNullOrWhiteSpace(key))
                return null;
            lock (_instanceCacheLock)
                if (_instanceCaches.TryGetValue(key, out CollisionCache cached))
                    return cached;

            CollisionCache cache;
            if (DungeonRunners.Gameplay.ZoneSpawner.Instance.TryGetProceduralSnapshot(key, out var snapshot) && snapshot?.Cells != null)
            {
                IReadOnlyList<DungeonRunners.Gameplay.MazeGenerator.MazeCell> worldCells = snapshot.WorldCells != null && snapshot.WorldCells.Count > 0
                    ? snapshot.WorldCells
                    : snapshot.Cells;
                cache = BuildProceduralCollisionCache(snapshot.ZoneName ?? zoneName, key, worldCells);
            }
            else
            {
                cache = new CollisionCache
                {
                    ZoneName = zoneName,
                    InstanceKey = key
                };
                string baseZone = zoneName;
                int suffix = baseZone?.IndexOf("_inst", StringComparison.OrdinalIgnoreCase) ?? -1;
                if (suffix > 0) baseZone = baseZone.Substring(0, suffix);
                var world = DungeonRunners.Gameplay.AuthoredWorldLayout.FindWorld(baseZone);
                if (world != null && !world.Generated)
                    AddTileCollision(cache, baseZone, key, world.Root.CanonicalPath, 0, 0, 0, 0);
            }

            StoreCollisionCache(key, zoneName, cache, "lazy");
            return cache;
        }

        private CollisionCache BuildProceduralCollisionCache(
            string zoneName,
            string instanceKey,
            IReadOnlyList<DungeonRunners.Gameplay.MazeGenerator.MazeCell> cells)
        {
            var cache = new CollisionCache
            {
                ZoneName = zoneName,
                InstanceKey = instanceKey
            };
            if (cells == null)
                return cache;

            for (int cellIndex = 0; cellIndex < cells.Count; cellIndex++)
            {
                DungeonRunners.Gameplay.MazeGenerator.MazeCell cell = cells[cellIndex];
                if (cell == null)
                    continue;
                AddTileCollision(cache, zoneName, instanceKey, cell.TileType, cell.WorldOriginFixedX, cell.WorldOriginFixedY, cell.GridX, cell.GridY);
            }
            return cache;
        }

        private void StoreCollisionCache(string key, string zoneName, CollisionCache cache, string lifecycle)
        {
            lock (_instanceCacheLock)
                _instanceCaches[key] = cache;
            int hybridCount = 0;
            for (int boxIndex = 0; boxIndex < cache.Boxes.Count; boxIndex++)
                if (cache.Boxes[boxIndex].Hybrid != null)
                    hybridCount++;
            if (DungeonRunners.Core.ServerDiagnostics.IsEnabled("collisionTracking"))
                Debug.LogError($"[COLLISION-TRACK] query=cache zone='{zoneName ?? ""}' instance='{key ?? ""}' lifecycle='{lifecycle ?? "unknown"}' blockers={cache.Boxes.Count} hybrid={hybridCount} heightSurfaces={cache.HeightSurfaces.Count}");
            Debug.LogError($"[WORLD-COLLISION] cache instance='{key}' zone='{zoneName ?? ""}' staticBlockers={cache.Boxes.Count} hybridBlockers={hybridCount} heightSurfaces={cache.HeightSurfaces.Count} projectileBlockers=hybrid-cobj lifecycle={lifecycle ?? "unknown"} source=ZoneSpawner+authored.db");
        }

        private bool TrySegmentHitInTileFixed(
            string zoneName,
            string instanceKey,
            string tileType,
            int tileOriginFixedX,
            int tileOriginFixedY,
            int gridX,
            int gridY,
            int startFixedX,
            int startFixedY,
            int startFixedZ,
            int endFixedX,
            int endFixedY,
            int endFixedZ,
            int radiusFixed,
            out WorldCollisionHit hit)
        {
            var cache = new CollisionCache { ZoneName = zoneName, InstanceKey = instanceKey };
            AddTileCollision(cache, zoneName, instanceKey, tileType, tileOriginFixedX, tileOriginFixedY, gridX, gridY);
            return TrySegmentHitFixed(cache, zoneName, startFixedX, startFixedY, startFixedZ, endFixedX, endFixedY, endFixedZ, radiusFixed, false, out hit);
        }

        private void AddTileCollision(CollisionCache cache, string zoneName, string instanceKey, string tileType, int tileOriginFixedX, int tileOriginFixedY, int gridX, int gridY)
        {
            if (cache == null || string.IsNullOrWhiteSpace(tileType))
                return;

            GCNode tile = ResolveAuthoredNode(tileType);
            if (tile == null)
            {
                LogMissingDoc(tileType, "tile");
                return;
            }

            GCNode map = tile.GetChild("Map");
            var placements = new List<GCNode>();
            if (map != null)
                CollectAnonymousPlacements(map, placements);
            GCNode entities = tile.GetChild("Entities");
            if (entities != null)
                CollectAnonymousPlacements(entities, placements);
            for (int placementIndex = 0; placementIndex < placements.Count; placementIndex++)
            {
                GCNode placement = placements[placementIndex];
                if (string.IsNullOrWhiteSpace(placement.Extends) || !placement.HasProperty("Position"))
                    continue;
                if (!TryParseVector3Fixed(placement.GetString("Position"), out int localFixedX, out int localFixedY, out int localFixedZ))
                    continue;
                StaticBounds bounds;
                if (AuthoredEntityCollision.TryResolve(placement, out var entityBounds))
                    bounds = new StaticBounds
                    {
                        CollisionObject = "",
                        MinFixedX = entityBounds.MinX, MinFixedY = entityBounds.MinY, MinFixedZ = entityBounds.MinZ,
                        MaxFixedX = entityBounds.MaxX, MaxFixedY = entityBounds.MaxY, MaxFixedZ = entityBounds.MaxZ
                    };
                else if (!TryResolveStaticBounds(placement.Extends, out bounds))
                    continue;

                bool authoredHeightOnly = bounds.HeightOnly || IsTerrainHeightSurfacePath(placement.Extends);
                bool hybridHeightCapable = bounds.Hybrid?.HasHeightSurface == true;
                bool addHeightSurface = authoredHeightOnly || hybridHeightCapable;
                int worldFixedX = tileOriginFixedX + localFixedX;
                int worldFixedY = tileOriginFixedY + localFixedY;
                int worldFixedZ = localFixedZ;
                int headingFixed = GetFixed8Property(placement, "Heading", 0);
                int headingDegrees = NativeZRotateTableIndex(headingFixed);
                var box = new CollisionBox
                {
                    ZoneName = zoneName,
                    InstanceKey = instanceKey,
                    TileType = tileType,
                    GridX = gridX,
                    GridY = gridY,
                    TileOriginFixedX = tileOriginFixedX,
                    TileOriginFixedY = tileOriginFixedY,
                    ObjectPath = placement.Extends,
                    CollisionObject = bounds.CollisionObject,
                    PositionFixedX = worldFixedX,
                    PositionFixedY = worldFixedY,
                    PositionFixedZ = worldFixedZ,
                    HeadingFixed32 = headingFixed,
                    HeadingDegrees = headingDegrees,
                    HeadingCosFixed = DungeonRunners.Combat.UnitMover.ZRotateCosFixed(headingDegrees),
                    HeadingSinFixed = DungeonRunners.Combat.UnitMover.ZRotateSinFixed(headingDegrees),
                    HeightOnly = authoredHeightOnly,
                    EntityBounds = entityBounds != null,
                    MinFixedX = bounds.MinFixedX,
                    MinFixedY = bounds.MinFixedY,
                    MinFixedZ = bounds.MinFixedZ,
                    MaxFixedX = bounds.MaxFixedX,
                    MaxFixedY = bounds.MaxFixedY,
                    MaxFixedZ = bounds.MaxFixedZ,
                    Hybrid = bounds.Hybrid,
                    HybridSource = bounds.HybridSource,
                    SourceOrder = cache.NextBoxSourceOrder++
                };
                MaterializeWorldBoundsAndNativeCells(box);
                if (addHeightSurface)
                {
                    cache.HeightSurfaces.Add(box);
                    RegisterNativeHeightSurface(cache, box);
                }
                if (!authoredHeightOnly)
                {
                    RegisterNativeCollisionBox(cache, box);
                    cache.Boxes.Add(box);
                }
            }
        }

        private static List<CollisionBox> BuildNativeCollisionQueryOrder(
            CollisionCache cache,
            int startFixedX,
            int startFixedY,
            int startFixedZ,
            int endFixedX,
            int endFixedY,
            int endFixedZ,
            int radiusFixed)
        {
            var ordered = new List<CollisionBox>(cache?.Boxes.Count ?? 0);
            if (cache == null || cache.Boxes.Count == 0)
                return ordered;

            radiusFixed = Math.Max(0, radiusFixed);
            int queryMinFixedX = ExpandNativeCollisionQueryMinFixed(Math.Min(startFixedX, endFixedX), radiusFixed);
            int queryMinFixedY = ExpandNativeCollisionQueryMinFixed(Math.Min(startFixedY, endFixedY), radiusFixed);
            int queryMinFixedZ = ExpandNativeCollisionQueryMinFixed(Math.Min(startFixedZ, endFixedZ), radiusFixed);
            int queryMaxFixedX = ExpandNativeCollisionQueryMaxFixed(Math.Max(startFixedX, endFixedX), radiusFixed);
            int queryMaxFixedY = ExpandNativeCollisionQueryMaxFixed(Math.Max(startFixedY, endFixedY), radiusFixed);
            int queryMaxFixedZ = ExpandNativeCollisionQueryMaxFixed(Math.Max(startFixedZ, endFixedZ), radiusFixed);
            var markedSourceOrders = new HashSet<int>();

            if (cache.HasNativeCellExtents)
            {
                List<NativeCollisionCellKey> queryCells = BuildNativeCollisionAllocatedQueryCells(
                    cache,
                    queryMinFixedX,
                    queryMinFixedY,
                    queryMinFixedZ,
                    queryMaxFixedX,
                    queryMaxFixedY,
                    queryMaxFixedZ);
                for (int cellIndex = 0; cellIndex < queryCells.Count; cellIndex++)
                {
                    NativeCollisionCellKey key = queryCells[cellIndex];
                    if (!cache.NativeCells.TryGetValue(key, out List<CollisionBox> cellBoxes))
                        continue;

                    for (int cellBoxIndex = 0; cellBoxIndex < cellBoxes.Count; cellBoxIndex++)
                    {
                        CollisionBox box = cellBoxes[cellBoxIndex];
                        if (markedSourceOrders.Contains(box.SourceOrder))
                            continue;

                        markedSourceOrders.Add(box.SourceOrder);
                        ordered.Add(box);
                    }
                }
            }

            AppendUnbucketedNativeCollisionBoxes(
                cache,
                ordered,
                markedSourceOrders,
                queryMinFixedX,
                queryMinFixedY,
                queryMinFixedZ,
                queryMaxFixedX,
                queryMaxFixedY,
                queryMaxFixedZ);
            return ordered;
        }

        private static List<CollisionBox> BuildNativeHeightSurfaceQueryOrder(CollisionCache cache, int worldFixedX, int worldFixedY)
        {
            var ordered = new List<CollisionBox>();
            if (cache == null || cache.HeightSurfaces.Count == 0)
                return ordered;

            int pointCellX = NativeCollisionPointGlobalCellForFixed(worldFixedX, 0x280, 0x50, NativeCollisionSectorCellsXY);
            int pointCellY = NativeCollisionPointGlobalCellForFixed(worldFixedY, 0x280, 0x50, NativeCollisionSectorCellsXY);
            if (!TryGetNativeHeightSurfaceZRange(cache, pointCellX, pointCellY, out int minCellZ, out int maxCellZ))
                return ordered;

            var markedSourceOrders = new HashSet<int>();
            for (int cellZ = minCellZ; cellZ <= maxCellZ; cellZ++)
            {
                var key = new NativeCollisionCellKey(pointCellX, pointCellY, cellZ);
                if (!cache.NativeHeightCells.TryGetValue(key, out List<CollisionBox> surfaces)) continue;
                for (int surfaceIndex = 0; surfaceIndex < surfaces.Count; surfaceIndex++)
                {
                    CollisionBox surface = surfaces[surfaceIndex];
                    if (surface == null || markedSourceOrders.Contains(surface.SourceOrder))
                        continue;
                    markedSourceOrders.Add(surface.SourceOrder);
                    ordered.Add(surface);
                }
            }
            return ordered;
        }

        private static bool TryGetNativeHeightSurfaceZRange(CollisionCache cache, int pointCellX, int pointCellY, out int minCellZ, out int maxCellZ)
        {
            minCellZ = int.MaxValue;
            maxCellZ = int.MinValue;
            if (cache == null)
                return false;

            if (!cache.NativeHeightColumnRanges.TryGetValue((pointCellX, pointCellY), out var range)) return false;
            minCellZ = range.MinZ;
            maxCellZ = range.MaxZ;

            return minCellZ <= maxCellZ;
        }

        private static void RegisterNativeHeightSurface(CollisionCache cache, CollisionBox surface)
        {
            foreach (NativeCollisionCellKey key in surface.NativeCellKeys)
            {
                if (!cache.NativeHeightCells.TryGetValue(key, out List<CollisionBox> surfaces))
                    cache.NativeHeightCells.Add(key, surfaces = new List<CollisionBox>());
                surfaces.Add(surface);
                var column = (key.X, key.Y);
                if (cache.NativeHeightColumnRanges.TryGetValue(column, out var range))
                    cache.NativeHeightColumnRanges[column] = (Math.Min(range.MinZ, key.Z), Math.Max(range.MaxZ, key.Z));
                else cache.NativeHeightColumnRanges.Add(column, (key.Z, key.Z));
            }
        }

        private static bool NativeCellKeysContain(List<NativeCollisionCellKey> keys, NativeCollisionCellKey needle)
        {
            if (keys == null)
                return false;
            for (int i = 0; i < keys.Count; i++)
            {
                if (keys[i].Equals(needle))
                    return true;
            }
            return false;
        }

        private static List<NativeCollisionCellKey> BuildNativeCollisionAllocatedQueryCells(
            CollisionCache cache,
            int queryMinFixedX,
            int queryMinFixedY,
            int queryMinFixedZ,
            int queryMaxFixedX,
            int queryMaxFixedY,
            int queryMaxFixedZ)
        {
            var cells = new List<NativeCollisionCellKey>();
            if (cache == null)
                return cells;

            EnumerateNativeCollisionCellsForBounds(
                queryMinFixedX,
                queryMinFixedY,
                queryMinFixedZ,
                queryMaxFixedX,
                queryMaxFixedY,
                queryMaxFixedZ,
                key =>
                {
                    if (!IsNativeCellInsideCacheExtents(cache, key))
                        return;
                    if (cache.NativeCells.ContainsKey(key))
                        cells.Add(key);
                });
            return cells;
        }

        private static void RegisterNativeCollisionBox(CollisionCache cache, CollisionBox box)
        {
            if (cache == null)
                return;
            if (!HasValidNativeCellRange(box))
            {
                if (box != null)
                    cache.NativeUnbucketedBoxes.Add(box);
                return;
            }

            RegisterNativeCellExtents(cache, box);
            for (int cellIndex = 0; cellIndex < box.NativeCellKeys.Count; cellIndex++)
            {
                NativeCollisionCellKey key = box.NativeCellKeys[cellIndex];
                if (!cache.NativeCells.TryGetValue(key, out List<CollisionBox> cellBoxes))
                {
                    cellBoxes = new List<CollisionBox>();
                    cache.NativeCells[key] = cellBoxes;
                }
                cellBoxes.Add(box);
            }
        }

        private static bool HasValidNativeCellRange(CollisionBox box)
        {
            return box != null &&
                   box.NativeCellKeys.Count > 0 &&
                   box.NativeMinCellX <= box.NativeMaxCellX &&
                   box.NativeMinCellY <= box.NativeMaxCellY &&
                   box.NativeMinCellZ <= box.NativeMaxCellZ;
        }

        private static void RegisterNativeCellExtents(CollisionCache cache, CollisionBox box)
        {
            if (cache == null || !HasValidNativeCellRange(box))
                return;

            if (!cache.HasNativeCellExtents)
            {
                cache.NativeMinCellX = box.NativeMinCellX;
                cache.NativeMinCellY = box.NativeMinCellY;
                cache.NativeMinCellZ = box.NativeMinCellZ;
                cache.NativeMaxCellX = box.NativeMaxCellX;
                cache.NativeMaxCellY = box.NativeMaxCellY;
                cache.NativeMaxCellZ = box.NativeMaxCellZ;
                cache.HasNativeCellExtents = true;
                return;
            }

            cache.NativeMinCellX = Math.Min(cache.NativeMinCellX, box.NativeMinCellX);
            cache.NativeMinCellY = Math.Min(cache.NativeMinCellY, box.NativeMinCellY);
            cache.NativeMinCellZ = Math.Min(cache.NativeMinCellZ, box.NativeMinCellZ);
            cache.NativeMaxCellX = Math.Max(cache.NativeMaxCellX, box.NativeMaxCellX);
            cache.NativeMaxCellY = Math.Max(cache.NativeMaxCellY, box.NativeMaxCellY);
            cache.NativeMaxCellZ = Math.Max(cache.NativeMaxCellZ, box.NativeMaxCellZ);
        }

        private static int ExpandNativeCollisionQueryMinFixed(int minFixed, int radiusFixed)
        {
            return (int)ClampLong((long)minFixed - radiusFixed, int.MinValue, int.MaxValue);
        }

        private static int ExpandNativeCollisionQueryMaxFixed(int maxFixed, int radiusFixed)
        {
            return (int)ClampLong((long)maxFixed + radiusFixed, int.MinValue, int.MaxValue);
        }

        private static int NativeCollisionMinSectorForBoundsFixed(int minFixed, int sectorWorldUnits)
        {
            int shiftedWorldUnits = AddNativeCollisionBoundaryInset(minFixed) >> 8;
            int sector = shiftedWorldUnits / sectorWorldUnits;
            return minFixed < 1 ? sector - 1 : sector;
        }

        private static int NativeCollisionMaxSectorExclusiveForBoundsFixed(int maxFixed, int sectorWorldUnits)
        {
            int shiftedWorldUnits = SubtractNativeCollisionBoundaryInset(maxFixed) >> 8;
            int sector = shiftedWorldUnits / sectorWorldUnits;
            return maxFixed < 1 ? sector : sector + 1;
        }

        private static int NativeCollisionLocalMinCellForFixed(int localMinFixed, int cellWorldUnits, int maxExclusive)
        {
            int cell = (localMinFixed >> 8) / cellWorldUnits;
            return ClampInt(cell, 0, maxExclusive);
        }

        private static int NativeCollisionLocalMaxCellExclusiveForFixed(int localMaxFixed, int cellWorldUnits, int maxExclusive)
        {
            int cell = ((localMaxFixed >> 8) / cellWorldUnits) + 1;
            return ClampInt(cell, 0, maxExclusive);
        }

        private static int ClampInt(int value, int min, int max)
        {
            if (value < min)
                return min;
            if (value > max)
                return max;
            return value;
        }

        private static int NativeCollisionSectorOriginFixed(int sector, int sectorSizeFixed)
        {
            return (int)ClampLong((long)sector * sectorSizeFixed, int.MinValue, int.MaxValue);
        }

        private static int NativeCollisionPointGlobalCellForFixed(int valueFixed, int sectorWorldUnits, int localCellWorldUnits, int localCellCount)
        {
            int worldUnits = valueFixed >> 8;
            int sector = (int)FloorDivLong(worldUnits, sectorWorldUnits);
            int localUnits = worldUnits - (sector * sectorWorldUnits);
            int localCell = localUnits / localCellWorldUnits;
            if (localCell >= localCellCount)
                localCell = localCellCount - 1;
            if (localCell < 0)
                localCell = 0;
            return (sector * localCellCount) + localCell;
        }

        private static int SubtractNativeCollisionSectorOrigin(int valueFixed, int sectorOriginFixed)
        {
            return (int)ClampLong((long)valueFixed - sectorOriginFixed, int.MinValue, int.MaxValue);
        }

        private static bool IsNativeCellInsideCacheExtents(CollisionCache cache, NativeCollisionCellKey key)
        {
            if (cache == null || !cache.HasNativeCellExtents)
                return false;
            return key.X >= cache.NativeMinCellX && key.X <= cache.NativeMaxCellX &&
                   key.Y >= cache.NativeMinCellY && key.Y <= cache.NativeMaxCellY &&
                   key.Z >= cache.NativeMinCellZ && key.Z <= cache.NativeMaxCellZ;
        }

        private static void EnumerateNativeCollisionCellsForBounds(
            int minFixedX,
            int minFixedY,
            int minFixedZ,
            int maxFixedX,
            int maxFixedY,
            int maxFixedZ,
            Action<NativeCollisionCellKey> visit)
        {
            if (visit == null)
                return;

            int minSectorX = NativeCollisionMinSectorForBoundsFixed(minFixedX, 0x280);
            int minSectorY = NativeCollisionMinSectorForBoundsFixed(minFixedY, 0x280);
            int minSectorZ = NativeCollisionMinSectorForBoundsFixed(minFixedZ, 0x500);
            int maxSectorX = NativeCollisionMaxSectorExclusiveForBoundsFixed(maxFixedX, 0x280);
            int maxSectorY = NativeCollisionMaxSectorExclusiveForBoundsFixed(maxFixedY, 0x280);
            int maxSectorZ = NativeCollisionMaxSectorExclusiveForBoundsFixed(maxFixedZ, 0x500);

            for (int sectorZ = minSectorZ; sectorZ < maxSectorZ; sectorZ++)
            for (int sectorY = minSectorY; sectorY < maxSectorY; sectorY++)
            for (int sectorX = minSectorX; sectorX < maxSectorX; sectorX++)
            {
                int sectorOriginX = NativeCollisionSectorOriginFixed(sectorX, NativeCollisionSectorSizeXYFixed);
                int sectorOriginY = NativeCollisionSectorOriginFixed(sectorY, NativeCollisionSectorSizeXYFixed);
                int sectorOriginZ = NativeCollisionSectorOriginFixed(sectorZ, NativeCollisionCellSizeZFixed);
                int localMinFixedX = SubtractNativeCollisionSectorOrigin(minFixedX, sectorOriginX);
                int localMaxFixedX = SubtractNativeCollisionSectorOrigin(maxFixedX, sectorOriginX);
                int localMinFixedY = SubtractNativeCollisionSectorOrigin(minFixedY, sectorOriginY);
                int localMaxFixedY = SubtractNativeCollisionSectorOrigin(maxFixedY, sectorOriginY);
                int localMinFixedZ = SubtractNativeCollisionSectorOrigin(minFixedZ, sectorOriginZ);
                int localMaxFixedZ = SubtractNativeCollisionSectorOrigin(maxFixedZ, sectorOriginZ);
                int localMinCellX = NativeCollisionLocalMinCellForFixed(localMinFixedX, 0x50, NativeCollisionSectorCellsXY);
                int localMaxCellX = NativeCollisionLocalMaxCellExclusiveForFixed(localMaxFixedX, 0x50, NativeCollisionSectorCellsXY);
                int localMinCellY = NativeCollisionLocalMinCellForFixed(localMinFixedY, 0x50, NativeCollisionSectorCellsXY);
                int localMaxCellY = NativeCollisionLocalMaxCellExclusiveForFixed(localMaxFixedY, 0x50, NativeCollisionSectorCellsXY);
                int localMinCellZ = NativeCollisionLocalMinCellForFixed(localMinFixedZ, 0x500, 1);
                int localMaxCellZ = NativeCollisionLocalMaxCellExclusiveForFixed(localMaxFixedZ, 0x500, 1);

                for (int localZ = localMinCellZ; localZ < localMaxCellZ; localZ++)
                for (int localY = localMinCellY; localY < localMaxCellY; localY++)
                for (int localX = localMinCellX; localX < localMaxCellX; localX++)
                {
                        visit(new NativeCollisionCellKey(
                            (sectorX * NativeCollisionSectorCellsXY) + localX,
                            (sectorY * NativeCollisionSectorCellsXY) + localY,
                            sectorZ + localZ));
                }
            }
        }

        private static int AddNativeCollisionBoundaryInset(int valueFixed)
        {
            return (int)ClampLong((long)valueFixed + NativeCollisionBoundaryInsetFixed, int.MinValue, int.MaxValue);
        }

        private static int SubtractNativeCollisionBoundaryInset(int valueFixed)
        {
            return (int)ClampLong((long)valueFixed - NativeCollisionBoundaryInsetFixed, int.MinValue, int.MaxValue);
        }

        private static void MaterializeWorldBoundsAndNativeCells(CollisionBox box)
        {
            RotateObjectCornerToWorldFixed(box, box.MinFixedX, box.MinFixedY, out int x0, out int y0);
            RotateObjectCornerToWorldFixed(box, box.MaxFixedX, box.MinFixedY, out int x1, out int y1);
            RotateObjectCornerToWorldFixed(box, box.MinFixedX, box.MaxFixedY, out int x2, out int y2);
            RotateObjectCornerToWorldFixed(box, box.MaxFixedX, box.MaxFixedY, out int x3, out int y3);

            box.WorldMinFixedX = Math.Min(Math.Min(x0, x1), Math.Min(x2, x3));
            box.WorldMinFixedY = Math.Min(Math.Min(y0, y1), Math.Min(y2, y3));
            box.WorldMinFixedZ = box.PositionFixedZ + Math.Min(box.MinFixedZ, box.MaxFixedZ);
            box.WorldMaxFixedX = Math.Max(Math.Max(x0, x1), Math.Max(x2, x3));
            box.WorldMaxFixedY = Math.Max(Math.Max(y0, y1), Math.Max(y2, y3));
            box.WorldMaxFixedZ = box.PositionFixedZ + Math.Max(box.MinFixedZ, box.MaxFixedZ);

            box.NativeCellKeys.Clear();
            EnumerateNativeCollisionCellsForBounds(
                box.WorldMinFixedX,
                box.WorldMinFixedY,
                box.WorldMinFixedZ,
                box.WorldMaxFixedX,
                box.WorldMaxFixedY,
                box.WorldMaxFixedZ,
                key => box.NativeCellKeys.Add(key));

            if (box.NativeCellKeys.Count == 0)
            {
                box.NativeMinCellX = 1;
                box.NativeMinCellY = 1;
                box.NativeMinCellZ = 1;
                box.NativeMaxCellX = 0;
                box.NativeMaxCellY = 0;
                box.NativeMaxCellZ = 0;
                return;
            }

            NativeCollisionCellKey first = box.NativeCellKeys[0];
            box.NativeMinCellX = first.X;
            box.NativeMaxCellX = first.X;
            box.NativeMinCellY = first.Y;
            box.NativeMaxCellY = first.Y;
            box.NativeMinCellZ = first.Z;
            box.NativeMaxCellZ = first.Z;
            for (int cellIndex = 1; cellIndex < box.NativeCellKeys.Count; cellIndex++)
            {
                NativeCollisionCellKey key = box.NativeCellKeys[cellIndex];
                box.NativeMinCellX = Math.Min(box.NativeMinCellX, key.X);
                box.NativeMinCellY = Math.Min(box.NativeMinCellY, key.Y);
                box.NativeMinCellZ = Math.Min(box.NativeMinCellZ, key.Z);
                box.NativeMaxCellX = Math.Max(box.NativeMaxCellX, key.X);
                box.NativeMaxCellY = Math.Max(box.NativeMaxCellY, key.Y);
                box.NativeMaxCellZ = Math.Max(box.NativeMaxCellZ, key.Z);
            }
        }

        private static void AppendUnbucketedNativeCollisionBoxes(
            CollisionCache cache,
            List<CollisionBox> ordered,
            HashSet<int> markedSourceOrders,
            int queryMinFixedX,
            int queryMinFixedY,
            int queryMinFixedZ,
            int queryMaxFixedX,
            int queryMaxFixedY,
            int queryMaxFixedZ)
        {
            if (cache == null || ordered == null || markedSourceOrders == null || cache.NativeUnbucketedBoxes.Count == 0)
                return;

            for (int boxIndex = 0; boxIndex < cache.NativeUnbucketedBoxes.Count; boxIndex++)
            {
                CollisionBox box = cache.NativeUnbucketedBoxes[boxIndex];
                if (box == null || markedSourceOrders.Contains(box.SourceOrder))
                    continue;
                if (!FixedBoundsOverlap(
                        queryMinFixedX,
                        queryMinFixedY,
                        queryMinFixedZ,
                        queryMaxFixedX,
                        queryMaxFixedY,
                        queryMaxFixedZ,
                        box.WorldMinFixedX,
                        box.WorldMinFixedY,
                        box.WorldMinFixedZ,
                        box.WorldMaxFixedX,
                        box.WorldMaxFixedY,
                        box.WorldMaxFixedZ))
                    continue;

                markedSourceOrders.Add(box.SourceOrder);
                ordered.Add(box);
            }
        }

        private static bool FixedBoundsOverlap(
            int aMinX,
            int aMinY,
            int aMinZ,
            int aMaxX,
            int aMaxY,
            int aMaxZ,
            int bMinX,
            int bMinY,
            int bMinZ,
            int bMaxX,
            int bMaxY,
            int bMaxZ)
        {
            return aMinX <= bMaxX && aMaxX >= bMinX &&
                   aMinY <= bMaxY && aMaxY >= bMinY &&
                   aMinZ <= bMaxZ && aMaxZ >= bMinZ;
        }

        private static void RotateObjectCornerToWorldFixed(CollisionBox box, int localFixedX, int localFixedY, out int worldFixedX, out int worldFixedY)
        {
            int cos = box.HeadingCosFixed;
            int sin = box.HeadingSinFixed;
            int dx = (int)(((long)cos * localFixedX) >> 8)
                - (int)(((long)sin * localFixedY) >> 8);
            int dy = (int)(((long)sin * localFixedX) >> 8)
                + (int)(((long)cos * localFixedY) >> 8);
            worldFixedX = box.PositionFixedX + dx;
            worldFixedY = box.PositionFixedY + dy;
        }

        private void CollectAnonymousPlacements(GCNode node, List<GCNode> placements)
        {
            if (node == null)
                return;

            if (node.IsAnonymous && node.HasProperty("Position") && !string.IsNullOrWhiteSpace(node.Extends))
                placements.Add(node);

            foreach (GCNode child in node.EnumerateChildrenInOrder())
                CollectAnonymousPlacements(child, placements);
        }

        private bool TryResolveStaticBounds(string path, out StaticBounds bounds)
        {
            bounds = null;
            if (string.IsNullOrWhiteSpace(path))
                return false;
            if (_staticBoundsCache.TryGetValue(path, out bounds))
                return true;
            if (_missingStaticBounds.ContainsKey(path))
                return false;

            GCNode node = GCDatabase.Instance.ResolveWithInheritance(path) ?? ResolveAuthoredNode(path);
            GCNode desc = node?.GetChild("Description");
            if (desc == null || !desc.HasProperty("CollisionObject"))
            {
                _missingStaticBounds.TryAdd(path, 0);
                return false;
            }

            string collisionObject = desc.GetString("CollisionObject");
            if (string.IsNullOrWhiteSpace(collisionObject))
            {
                _missingStaticBounds.TryAdd(path, 0);
                return false;
            }

            if (!TryGetFixed8Property(desc, "MinX", out int minFixedX) ||
                !TryGetFixed8Property(desc, "MinY", out int minFixedY) ||
                !TryGetFixed8Property(desc, "MinZ", out int minFixedZ) ||
                !TryGetFixed8Property(desc, "MaxX", out int maxFixedX) ||
                !TryGetFixed8Property(desc, "MaxY", out int maxFixedY) ||
                !TryGetFixed8Property(desc, "MaxZ", out int maxFixedZ))
            {
                _missingStaticBounds.TryAdd(path, 0);
                LogMissingBounds(path);
                return false;
            }

            if (!HybridCollisionObject.TryLoadFromAuthoredData(collisionObject, out var hybrid, out string hybridSource))
            {
                _missingStaticBounds.TryAdd(path, 0);
                return false;
            }

            bounds = new StaticBounds
            {
                CollisionObject = collisionObject,
                HeightOnly = IsTerrainHeightSurfacePath(path),
                MinFixedX = minFixedX,
                MinFixedY = minFixedY,
                MinFixedZ = minFixedZ,
                MaxFixedX = maxFixedX,
                MaxFixedY = maxFixedY,
                MaxFixedZ = maxFixedZ,
                Hybrid = hybrid,
                HybridSource = hybridSource
            };
            Debug.LogError($"[COBJ] loaded name='{collisionObject}' source='{hybridSource}' bodyOffset=0x{hybrid.BodyOffset:X} walk={hybrid.WalkGridX}x{hybrid.WalkGridY}@{hybrid.WalkCellSize} block={hybrid.BlockGridX}x{hybrid.BlockGridY}x{hybrid.BlockGridZ}@{hybrid.BlockCellSize} buckets={hybrid.NonEmptyBuckets} ranges={hybrid.RangeCount} sourceFunction=HybridCollisionObject::readObject");
            _staticBoundsCache.TryAdd(path, bounds);
            return true;
        }

        private static bool IsTerrainHeightSurfacePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;
            string normalized = path.Replace('\\', '.').Replace('/', '.');
            return normalized.IndexOf(".floor.", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private GCNode ResolveAuthoredNode(string nameOrPath)
        {
            if (string.IsNullOrWhiteSpace(nameOrPath))
                return null;
            if (_authoredTextCache.TryGetValue(nameOrPath, out GCNode cached))
                return cached;

            GCNode node = GCDatabase.Instance.ResolveWithInheritance(nameOrPath);
            if (node != null)
                _authoredTextCache.TryAdd(nameOrPath, node);
            return node;
        }

        private bool HybridSegmentIntersectsFixed(
            CollisionBox box,
            int startFixedX,
            int startFixedY,
            int startFixedZ,
            int endFixedX,
            int endFixedY,
            int endFixedZ,
            int radiusFixed,
            out int tHitFixed)
        {
            tHitFixed = 0;
            if (box?.Hybrid == null)
                return false;

            WorldToObjectLocalFixed(box, startFixedX, startFixedY, startFixedZ, out int localStartX, out int localStartY, out int localStartZ);
            WorldToObjectLocalFixed(box, endFixedX, endFixedY, endFixedZ, out int localEndX, out int localEndY, out int localEndZ);
            return box.Hybrid.TestSegmentFixed(localStartX, localStartY, localStartZ, localEndX, localEndY, localEndZ, radiusFixed, out tHitFixed);
        }

        private bool AabbSegmentIntersectsFixed(
            CollisionBox box,
            int startFixedX,
            int startFixedY,
            int startFixedZ,
            int endFixedX,
            int endFixedY,
            int endFixedZ,
            int radiusFixed,
            out int tHitFixed)
        {
            tHitFixed = 0;
            if (box == null)
                return false;

            WorldToObjectLocalFixed(box, startFixedX, startFixedY, startFixedZ, out int localStartX, out int localStartY, out int localStartZ);
            WorldToObjectLocalFixed(box, endFixedX, endFixedY, endFixedZ, out int localEndX, out int localEndY, out int localEndZ);
            NormalizeAabbFixedBounds(
                box,
                out int minX,
                out int minY,
                out int minZ,
                out int maxX,
                out int maxY,
                out int maxZ);
            return SegmentIntersectsLocalAabbFixed(
                localStartX,
                localStartY,
                localStartZ,
                localEndX,
                localEndY,
                localEndZ,
                radiusFixed,
                minX,
                minY,
                minZ,
                maxX,
                maxY,
                maxZ,
                out tHitFixed);
        }

        private static bool TestLocalAabbFixed(CollisionBox box, int localX, int localY, int localZ, int radiusFixed)
        {
            if (box == null)
                return false;
            NormalizeAabbFixedBounds(
                box,
                out int minX,
                out int minY,
                out int minZ,
                out int maxX,
                out int maxY,
                out int maxZ);
            return localX + radiusFixed >= minX &&
                   localX - radiusFixed <= maxX &&
                   localY + radiusFixed >= minY &&
                   localY - radiusFixed <= maxY &&
                   localZ + radiusFixed >= minZ &&
                   localZ - radiusFixed <= maxZ;
        }

        private static bool SegmentIntersectsLocalAabbFixed(
            int startX,
            int startY,
            int startZ,
            int endX,
            int endY,
            int endZ,
            int radiusFixed,
            int minX,
            int minY,
            int minZ,
            int maxX,
            int maxY,
            int maxZ,
            out int entryT)
        {
            entryT = 0;
            int boxMinX = startX - radiusFixed;
            int boxMinY = startY - radiusFixed;
            int boxMinZ = startZ - radiusFixed;
            int boxMaxX = startX + radiusFixed;
            int boxMaxY = startY + radiusFixed;
            int boxMaxZ = startZ + radiusFixed;
            if (AabbBoundsOverlapFixed(boxMinX, boxMinY, boxMinZ, boxMaxX, boxMaxY, boxMaxZ, minX, minY, minZ, maxX, maxY, maxZ))
                return true;

            int dirX = endX - startX;
            int dirY = endY - startY;
            int dirZ = endZ - startZ;
            int sampleCount = NativeBoundingBoxSweepSampleCount(dirX, dirY, dirZ);
            int denominatorFixed = (sampleCount + 1) << 8;
            int stepX = (int)(((long)dirX << 8) / denominatorFixed);
            int stepY = (int)(((long)dirY << 8) / denominatorFixed);
            int stepZ = (int)(((long)dirZ << 8) / denominatorFixed);

            boxMinX += stepX;
            boxMinY += stepY;
            boxMinZ += stepZ;
            boxMaxX += stepX;
            boxMaxY += stepY;
            boxMaxZ += stepZ;

            int sampleIndex = 1;
            while (sampleIndex < sampleCount)
            {
                if (AabbBoundsOverlapFixed(boxMinX, boxMinY, boxMinZ, boxMaxX, boxMaxY, boxMaxZ, minX, minY, minZ, maxX, maxY, maxZ))
                {
                    entryT = NativeBoundingBoxSweepT(sampleIndex, sampleCount);
                    return true;
                }
                boxMinX += stepX;
                boxMinY += stepY;
                boxMinZ += stepZ;
                boxMaxX += stepX;
                boxMaxY += stepY;
                boxMaxZ += stepZ;
                sampleIndex++;
            }

            if (!AabbBoundsOverlapFixed(boxMinX, boxMinY, boxMinZ, boxMaxX, boxMaxY, boxMaxZ, minX, minY, minZ, maxX, maxY, maxZ))
                return false;
            entryT = NativeBoundingBoxSweepT(sampleIndex, sampleCount);
            return true;
        }

        private static bool AabbBoundsOverlapFixed(
            int leftMinX,
            int leftMinY,
            int leftMinZ,
            int leftMaxX,
            int leftMaxY,
            int leftMaxZ,
            int rightMinX,
            int rightMinY,
            int rightMinZ,
            int rightMaxX,
            int rightMaxY,
            int rightMaxZ)
        {
            return leftMinX <= rightMaxX &&
                   rightMinX <= leftMaxX &&
                   leftMinY <= rightMaxY &&
                   rightMinY <= leftMaxY &&
                   leftMinZ <= rightMaxZ &&
                   rightMinZ <= leftMaxZ;
        }

        private static int NativeBoundingBoxSweepSampleCount(int dx, int dy, int dz)
        {
            int maxAxis = NativeAbsInt32(dx);
            int absY = NativeAbsInt32(dy);
            if (absY > maxAxis) maxAxis = absY;
            int absZ = NativeAbsInt32(dz);
            if (absZ > maxAxis) maxAxis = absZ;
            return (maxAxis >> 8) / 3;
        }

        private static int NativeBoundingBoxSweepT(int sampleIndex, int sampleCount)
        {
            if (sampleIndex <= 0)
                return 0;
            int denominator = sampleCount + 1;
            int tFixed = denominator <= 0 ? 0x100 : (sampleIndex << 8) / denominator;
            return ClampInt(tFixed, 0, 0x100);
        }

        private static int NativeAbsInt32(int value)
        {
            if (value == int.MinValue)
                return int.MaxValue;
            return value < 0 ? -value : value;
        }

        private bool TryGetSurfaceHeightFixed(CollisionBox box, int worldFixedX, int worldFixedY, out int heightFixed)
        {
            heightFixed = 0;
            if (box == null)
                return false;
            if (box.Hybrid == null)
                return TryGetAabbSurfaceHeightFixed(box, worldFixedX, worldFixedY, out heightFixed);

            int boxFixedZ = box.PositionFixedZ;
            WorldToObjectLocalFixed(box, worldFixedX, worldFixedY, boxFixedZ, out int localXFixed8, out int localYFixed8, out _);
            if (!box.Hybrid.GetHeight(localXFixed8, localYFixed8, out int localHeightFixed8))
                return false;

            heightFixed = boxFixedZ + localHeightFixed8;
            return true;
        }

        private bool TryGetAabbSurfaceHeightFixed(CollisionBox box, int worldFixedX, int worldFixedY, out int heightFixed)
        {
            heightFixed = 0;
            if (box == null)
                return false;

            WorldToObjectLocalFixed(box, worldFixedX, worldFixedY, box.PositionFixedZ, out int localXFixed8, out int localYFixed8, out _);
            NormalizeAabbFixedBounds(
                box,
                out int minX,
                out int minY,
                out _,
                out int maxX,
                out int maxY,
                out _);
            if (localXFixed8 < minX || localXFixed8 > maxX ||
                localYFixed8 < minY || localYFixed8 > maxY)
                return false;

            heightFixed = box.PositionFixedZ + Math.Max(box.MinFixedZ, box.MaxFixedZ);
            return true;
        }

        private static void NormalizeAabbFixedBounds(
            CollisionBox box,
            out int minX,
            out int minY,
            out int minZ,
            out int maxX,
            out int maxY,
            out int maxZ)
        {
            minX = Math.Min(box.MinFixedX, box.MaxFixedX);
            minY = Math.Min(box.MinFixedY, box.MaxFixedY);
            minZ = Math.Min(box.MinFixedZ, box.MaxFixedZ);
            maxX = Math.Max(box.MinFixedX, box.MaxFixedX);
            maxY = Math.Max(box.MinFixedY, box.MaxFixedY);
            maxZ = Math.Max(box.MinFixedZ, box.MaxFixedZ);
        }

        private WorldCollisionHit CreateHitFixed(CollisionBox box, int worldFixedX, int worldFixedY, int worldFixedZ, int distanceFixed)
        {
            WorldToObjectLocalFixed(box, worldFixedX, worldFixedY, worldFixedZ, out int localFixedX, out int localFixedY, out int localFixedZ);
            return new WorldCollisionHit
            {
                Blocked = true,
                ZoneName = box.ZoneName,
                InstanceKey = box.InstanceKey,
                TileType = box.TileType,
                GridX = box.GridX,
                GridY = box.GridY,
                ObjectPath = box.ObjectPath,
                CollisionObject = box.CollisionObject,
                Hybrid = box.Hybrid != null,
                HybridSource = box.HybridSource ?? (box.Hybrid != null ? "hybrid-cobj" : null),
                WorldFixedX = worldFixedX,
                WorldFixedY = worldFixedY,
                WorldFixedZ = worldFixedZ,
                LocalFixedX = localFixedX,
                LocalFixedY = localFixedY,
                LocalFixedZ = localFixedZ,
                DistanceFixed = distanceFixed
            };
        }

        public static int ToFixed8(int value)
        {
            return checked(value * 256);
        }

        private static string FormatFixed8(int value)
        {
            long magnitude = value < 0 ? -(long)value : value;
            long tenths = (magnitude * 10 + 128) / 256;
            return $"{(value < 0 ? "-" : "")}{tenths / 10}.{tenths % 10}";
        }

        private static int LerpFixed(int start, int end, int tFixed)
        {
            return start + (int)(((long)(end - start) * tFixed) >> 8);
        }

        private static int DistanceFixed(int startX, int startY, int startZ, int endX, int endY, int endZ)
        {
            long dx = (long)endX - startX;
            long dy = (long)endY - startY;
            long dz = (long)endZ - startZ;
            return DungeonRunners.Combat.UnitMover.IntSqrt((dx * dx) + (dy * dy) + (dz * dz));
        }

        private static long FloorDivLong(long value, long divisor)
        {
            if (divisor < 0)
            {
                value = -value;
                divisor = -divisor;
            }
            long quotient = value / divisor;
            long remainder = value % divisor;
            if (remainder != 0 && value < 0)
                quotient--;
            return quotient;
        }

        private static long ClampLong(long value, long min, long max)
        {
            if (value < min)
                return min;
            if (value > max)
                return max;
            return value;
        }

        private void WorldToObjectLocalFixed(CollisionBox box, int worldFixedX, int worldFixedY, int worldFixedZ, out int localFixedX, out int localFixedY, out int localFixedZ)
        {
            int dx = worldFixedX - box.PositionFixedX;
            int dy = worldFixedY - box.PositionFixedY;
            localFixedZ = worldFixedZ - box.PositionFixedZ;
            if (box.EntityBounds)
            {
                AuthoredEntityCollision.ToLocalFixed(dx, dy, box.HeadingFixed32, out localFixedX, out localFixedY);
                return;
            }
            int cos = box.HeadingCosFixed;
            int sin = box.HeadingSinFixed;
            localFixedX = (int)(((long)cos * dx) >> 8)
                + (int)(((long)sin * dy) >> 8);
            localFixedY = (int)((-(long)sin * dx) >> 8)
                + (int)(((long)cos * dy) >> 8);
        }

        private static bool TryParseVector3Fixed(string value, out int xFixed, out int yFixed, out int zFixed)
        {
            xFixed = 0;
            yFixed = 0;
            zFixed = 0;
            if (string.IsNullOrWhiteSpace(value))
                return false;
            string[] parts = value.Split(',');
            if (parts.Length < 2)
                return false;

            if (!TryParseFixed8(parts[0], out xFixed) || !TryParseFixed8(parts[1], out yFixed))
                return false;
            if (parts.Length >= 3)
                TryParseFixed8(parts[2], out zFixed);
            return true;
        }

        private static int GetFixed8Property(GCNode node, string key, int fallbackFixed)
        {
            if (node?.Properties != null &&
                node.Properties.TryGetValue(key, out string raw) &&
                TryParseFixed8(raw, out int valueFixed))
            {
                return valueFixed;
            }
            return fallbackFixed;
        }

        private static bool TryGetFixed8Property(GCNode node, string key, out int fixedValue)
        {
            fixedValue = 0;
            return node?.Properties != null &&
                   node.Properties.TryGetValue(key, out string raw) &&
                   TryParseFixed8(raw, out fixedValue);
        }

        private static bool TryParseFixed8(string value, out int fixedValue)
        {
            return GCNode.TryParseFixed32(value, out fixedValue);
        }

        private static int NativeZRotateTableIndex(int headingFixed32)
        {
            int index = headingFixed32 >> 8;
            if (index < 0)
                index = index + 0x168 + (((-index) - 1) / 0x168) * 0x168;
            if (index > 0x167)
                index %= 0x168;
            return index;
        }

        private void LogMissingDoc(string path, string kind)
        {
            string key = kind + ":" + path;
            if (_loggedMissingDocs.TryAdd(key, 0))
                Debug.LogError($"[WORLD-COLLISION] kind={kind} path='{path}' source=GC-DATABASE reason=missing-authored");
        }

        private void LogMissingBounds(string path)
        {
            if (_loggedMissingBounds.TryAdd(path, 0))
                Debug.LogError($"[WORLD-COLLISION] path='{path}' reason=missing-static-object-bounds");
        }

        private static string DescribeBounds(StaticBounds bounds)
        {
            if (bounds == null)
                return "missing";
            return $"{bounds.CollisionObject} [{FormatFixed8(bounds.MinFixedX)},{FormatFixed8(bounds.MinFixedY)},{FormatFixed8(bounds.MinFixedZ)}]-[{FormatFixed8(bounds.MaxFixedX)},{FormatFixed8(bounds.MaxFixedY)},{FormatFixed8(bounds.MaxFixedZ)}]";
        }
    }
}
