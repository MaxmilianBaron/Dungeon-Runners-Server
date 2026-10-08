using System;
using System.Collections.Generic;
using DungeonRunners.Core;
using DungeonRunners.Gameplay;
using DungeonRunners.Engine;

namespace DungeonRunners.Utilities
{
    public static partial class PathMapBuilder
    {
        public const int NodeResolutionFixed = 0x0A00;
        public const int NativeNodeCenterOffsetFixed = 0x0500;

        public const int MazeTileSizeFixed = 0x19000;

        public const int HeightConnectThresholdFixed = 0x0A00;

        private const short NativeNoHeightUnits = -0x7fff;
        private const int NativeCollisionHalfExtentFixed = 0x0500;
        private const int NativeCollisionHeightFixed = 0x1400;

        private sealed class NativeCollisionPlacement
        {
            public HybridCollisionObject Collision;
            public int PositionFixedX;
            public int PositionFixedY;
            public int PositionFixedZ;
            public int CosFixed;
            public int SinFixed;
            public int HeadingFixed;
            public AuthoredEntityCollision EntityBounds;
        }

        private static int GetCellTileSizeFixed(MazeGenerator.MazeCell cell)
        {
            return cell != null && cell.TileSizeFixed > 0 ? cell.TileSizeFixed : MazeTileSizeFixed;
        }

        private static readonly (int a0, int b0, int a1, int b1)[] DiagonalCornerEdges =
        {
            default, (2, 4, 0, 6), default, (4, 6, 2, 0),
            default, (6, 0, 4, 2), default, (0, 2, 6, 4),
        };

        public static PathMap Build(string zoneName, IReadOnlyList<MazeGenerator.MazeCell> cells)
        {
            if (cells == null || cells.Count == 0)
            {
                Debug.LogError($"[PATHMAP-BUILD] zone='{zoneName}' skip=emptyCellList");
                return null;
            }

            return Build(zoneName, cells, cells[0].WorldCenterFixedX, cells[0].WorldCenterFixedY, 0);
        }

        public static PathMap Build(
            string zoneName,
            IReadOnlyList<MazeGenerator.MazeCell> cells,
            int seedFixedX,
            int seedFixedY,
            int seedFixedZ,
            bool staticWorld = false)
        {
            if (cells == null || cells.Count == 0)
            {
                Debug.LogError($"[PATHMAP-BUILD] zone='{zoneName}' skip=emptyCellList");
                return null;
            }

            ComputeWorldBoundsFixed(cells, out int minFixedX, out int maxFixedX, out int minFixedY, out int maxFixedY);
            if (staticWorld)
                ComputeAuthoredWorldBoundsFixed(cells[0].TileType, out minFixedX, out maxFixedX, out minFixedY, out maxFixedY);
            var pathMap = PathMap.CreateEmptyFixed(zoneName, minFixedX, maxFixedX, minFixedY, maxFixedY);

            int gridW = (int)CeilDiv((long)maxFixedX - minFixedX, NodeResolutionFixed) + 1;
            int gridH = (int)CeilDiv((long)maxFixedY - minFixedY, NodeResolutionFixed) + 1;
            var blocked = new bool[gridW, gridH];
            var heightGridFixed = new int[gridW, gridH];
            var heightKnown = new bool[gridW, gridH];
            var inMazeFootprint = new bool[gridW, gridH];
            if (staticWorld)
                for (int x = 0; x < gridW; x++)
                    for (int y = 0; y < gridH; y++) inMazeFootprint[x, y] = true;

            int tilesProcessed = 0;
            int tilesSkippedNoFile = 0;
            int placementsProcessed = 0;
            int placementsSkippedNoCobj = 0;
            int placementsSkippedMissingDeclaredCobj = 0;
            int blockedCellsTotal = 0;
            List<string> missingTileTypes = null;
            var nativeCollisionPlacements = new List<NativeCollisionPlacement>();
            var nativeCollisionObjects = new Dictionary<string, HybridCollisionObject>(StringComparer.OrdinalIgnoreCase);

            foreach (var cell in cells)
            {
                MarkMazeFootprintFixed(cell, inMazeFootprint, gridW, gridH, minFixedX, minFixedY);

                TileLayout layout;
                try { layout = TileLayoutLoader.LoadAuthored(cell.TileType); }
                catch (Exception e)
                {
                    tilesSkippedNoFile++;
                    (missingTileTypes ??= new List<string>()).Add(cell.TileType);
                    Debug.LogError($"[PATHMAP-BUILD] tileParse state=failed tile={cell.TileType} message='{e.Message}'");
                    continue;
                }

                tilesProcessed++;
                foreach (var placement in layout.Placements)
                {
                    if (AuthoredEntityCollision.TryResolve(placement.Definition, out var entityBounds))
                    {
                        int heading = NativeZRotateTableIndex(placement.HeadingFixed);
                        nativeCollisionPlacements.Add(new NativeCollisionPlacement
                        {
                            EntityBounds = entityBounds,
                            PositionFixedX = cell.WorldOriginFixedX + placement.XFixed,
                            PositionFixedY = cell.WorldOriginFixedY + placement.YFixed,
                            PositionFixedZ = placement.ZFixed,
                            HeadingFixed = placement.HeadingFixed,
                            CosFixed = DungeonRunners.Combat.UnitMover.ZRotateCosFixed(heading),
                            SinFixed = DungeonRunners.Combat.UnitMover.ZRotateSinFixed(heading)
                        });
                        placementsProcessed++;
                        continue;
                    }
                    if (!TryResolvePlacementCobj(placement.ExtendsPath, out string collisionObjectName, out CobjData cobj, out bool collisionDeclared))
                    {
                        placementsSkippedNoCobj++;
                        if (collisionDeclared)
                            placementsSkippedMissingDeclaredCobj++;
                        continue;
                    }

                    NativeCollisionPlacement nativeCollisionPlacement = null;
                    bool hasNativeCollision = !IsTerrainHeightSurfacePath(placement.ExtendsPath)
                        && TryCreateNativeCollisionPlacement(
                            collisionObjectName,
                            placement,
                            cell,
                            nativeCollisionObjects,
                            out nativeCollisionPlacement);

                    placementsProcessed++;
                    int blockedHere = ApplyCobjToBlockedGrid(
                        cobj, placement, cell,
                        !hasNativeCollision,
                        blocked, heightGridFixed, heightKnown,
                        gridW, gridH, minFixedX, minFixedY);
                    blockedCellsTotal += blockedHere;

                    if (hasNativeCollision)
                        nativeCollisionPlacements.Add(nativeCollisionPlacement);
                }
            }

            MaterializeWorldCollisionHeights(
                zoneName,
                minFixedX,
                minFixedY,
                seedFixedZ,
                gridW,
                gridH,
                inMazeFootprint,
                heightGridFixed,
                heightKnown);

            MaterializeNativeSeedHeight(
                seedFixedX,
                seedFixedY,
                seedFixedZ,
                minFixedX,
                minFixedY,
                gridW,
                gridH,
                inMazeFootprint,
                heightGridFixed,
                heightKnown);

            for (int placementIndex = 0; placementIndex < nativeCollisionPlacements.Count; placementIndex++)
            {
                blockedCellsTotal += ApplyNativeNodeCollision(
                    nativeCollisionPlacements[placementIndex],
                    blocked,
                    inMazeFootprint,
                    heightGridFixed,
                    heightKnown,
                    gridW,
                    gridH,
                    minFixedX,
                    minFixedY);
            }

            BuildNativeGraph(
                pathMap,
                seedFixedX,
                seedFixedY,
                minFixedX,
                minFixedY,
                gridW,
                gridH,
                heightKnown,
                blocked,
                inMazeFootprint,
                heightGridFixed,
                out int nativeConnectedCount,
                out int inaccessibleCount);

            if (ServerDiagnostics.IsEnabled("pathMapTracking"))
                Debug.LogError($"[PATHMAP-TRACK] phase=materialized zone='{zoneName ?? ""}' cells={cells.Count} seedFixed=({seedFixedX},{seedFixedY},{seedFixedZ}) boundsFixed=({minFixedX},{minFixedY})->({maxFixedX},{maxFixedY}) grid={gridW}x{gridH} nodes={pathMap.NodeCount} walkable={pathMap.WalkableCount} connected={nativeConnectedCount} inaccessible={inaccessibleCount} collisionPlacements={nativeCollisionPlacements.Count} blockedCells={blockedCellsTotal}");

            Debug.LogError(
                $"[PATHMAP-BUILD] zone='{zoneName}' cells={cells.Count} tiles={tilesProcessed}/{cells.Count} ({tilesSkippedNoFile} no-file) " +
                $"placements={placementsProcessed} ({placementsSkippedNoCobj} no-cobj, {placementsSkippedMissingDeclaredCobj} declared-missing) " +
                $"nodes={pathMap.NodeCount} (walkable={pathMap.WalkableCount} nonwalkable={inaccessibleCount} nativeConnected={nativeConnectedCount}) " +
                $"seedFixed=({seedFixedX},{seedFixedY}) " +
                $"boundsFixed=({minFixedX},{minFixedY})->({maxFixedX},{maxFixedY}) blockedCobjCells={blockedCellsTotal}");

            if (missingTileTypes != null && missingTileTypes.Count > 0)
                Debug.LogError($"[PATHMAP-BUILD] missing tile files for zone='{zoneName}': {string.Join(", ", missingTileTypes)}");

            return pathMap;
        }

        private static void ComputeWorldBoundsFixed(
            IReadOnlyList<MazeGenerator.MazeCell> cells,
            out int minFixedX, out int maxFixedX, out int minFixedY, out int maxFixedY)
        {
            minFixedX = int.MaxValue;
            minFixedY = int.MaxValue;
            maxFixedX = int.MinValue;
            maxFixedY = int.MinValue;
            foreach (var cell in cells)
            {
                int cellFixedX = cell.WorldOriginFixedX;
                int cellFixedY = cell.WorldOriginFixedY;
                if (cellFixedX < minFixedX) minFixedX = cellFixedX;
                if (cellFixedY < minFixedY) minFixedY = cellFixedY;
                int tileSizeFixed = GetCellTileSizeFixed(cell);
                int maxCellFixedX = cellFixedX + tileSizeFixed;
                int maxCellFixedY = cellFixedY + tileSizeFixed;
                if (maxCellFixedX > maxFixedX) maxFixedX = maxCellFixedX;
                if (maxCellFixedY > maxFixedY) maxFixedY = maxCellFixedY;
            }
        }

        private static void MarkMazeFootprintFixed(
            MazeGenerator.MazeCell cell, bool[,] inMaze,
            int gridW, int gridH, int minFixedX, int minFixedY)
        {
            int cellFixedX = cell.WorldOriginFixedX;
            int cellFixedY = cell.WorldOriginFixedY;
            int tileSizeFixed = GetCellTileSizeFixed(cell);
            int gx0 = FloorDiv((long)cellFixedX - minFixedX, NodeResolutionFixed);
            int gy0 = FloorDiv((long)cellFixedY - minFixedY, NodeResolutionFixed);
            int gx1 = (int)CeilDiv((long)cellFixedX + tileSizeFixed - minFixedX, NodeResolutionFixed);
            int gy1 = (int)CeilDiv((long)cellFixedY + tileSizeFixed - minFixedY, NodeResolutionFixed);
            for (int gx = Math.Max(0, gx0); gx < Math.Min(gridW, gx1); gx++)
            {
                for (int gy = Math.Max(0, gy0); gy < Math.Min(gridH, gy1); gy++)
                {
                    inMaze[gx, gy] = true;
                }
            }
        }

        private static int ApplyCobjToBlockedGrid(
            CobjData cobj, TilePlacement placement, MazeGenerator.MazeCell cell,
            bool applyBoundingBoxes,
            bool[,] blocked, int[,] heightGridFixed, bool[,] heightKnown,
            int gridW, int gridH, int minFixedX, int minFixedY)
        {
            int headingDegrees = NativeZRotateTableIndex(placement.HeadingFixed);
            int cosT = DungeonRunners.Combat.UnitMover.ZRotateCosFixed(headingDegrees);
            int sinT = DungeonRunners.Combat.UnitMover.ZRotateSinFixed(headingDegrees);
            int cellOriginFixedX = cell.WorldOriginFixedX;
            int cellOriginFixedY = cell.WorldOriginFixedY;

            int blockedHere = 0;
            if (cobj.Width1 > 0 && cobj.Height1 > 0)
            {
                int cs = cobj.CellSize1;
                for (int cy = 0; cy < cobj.Height1; cy++)
                {
                    for (int cx = 0; cx < cobj.Width1; cx++)
                    {
                        ushort h = cobj.Heightmap[cy * cobj.Width1 + cx];

                        int localFixedX = CobjCellCenterFixed(cobj.OriginX1, cx, cs);
                        int localFixedY = CobjCellCenterFixed(cobj.OriginY1, cy, cs);
                        RotateFixed(localFixedX, localFixedY, cosT, sinT, out int rotatedFixedX, out int rotatedFixedY);
                        int worldFixedX = cellOriginFixedX + placement.XFixed + rotatedFixedX;
                        int worldFixedY = cellOriginFixedY + placement.YFixed + rotatedFixedY;

                        int gxNode = NativeWorldToNodeIndex((long)worldFixedX - minFixedX);
                        int gyNode = NativeWorldToNodeIndex((long)worldFixedY - minFixedY);
                        if (gxNode < 0 || gxNode >= gridW || gyNode < 0 || gyNode >= gridH) continue;

                        short localHeightUnits = unchecked((short)h);
                        if (localHeightUnits == NativeNoHeightUnits)
                            continue;

                        int candidateHeightFixed = placement.ZFixed + (localHeightUnits << 8);
                        if (!heightKnown[gxNode, gyNode] || candidateHeightFixed > heightGridFixed[gxNode, gyNode])
                        {
                            heightGridFixed[gxNode, gyNode] = candidateHeightFixed;
                            heightKnown[gxNode, gyNode] = true;
                        }
                    }
                }
            }

            if (applyBoundingBoxes && cobj.Width2 > 0 && cobj.Height2 > 0 && cobj.Cells != null)
            {
                int cs2 = cobj.CellSize2;
                for (int cy = 0; cy < cobj.Height2; cy++)
                {
                    for (int cx = 0; cx < cobj.Width2; cx++)
                    {
                        var bboxCell = cobj.Cells[cy * cobj.Width2 + cx];
                        if (bboxCell == null || bboxCell.BBoxes.Length == 0) continue;
                        bool occupiesWalkBand = false;
                        foreach (var box in bboxCell.BBoxes)
                        {
                            if (cobj.OriginZ2 + box.ZLow <= 40 && cobj.OriginZ2 + box.ZHigh >= 5) { occupiesWalkBand = true; break; }
                        }
                        if (!occupiesWalkBand) continue;

                        int localFixedX = CobjCellCenterFixed(cobj.OriginX2, cx, cs2);
                        int localFixedY = CobjCellCenterFixed(cobj.OriginY2, cy, cs2);
                        RotateFixed(localFixedX, localFixedY, cosT, sinT, out int rotatedFixedX, out int rotatedFixedY);
                        int worldFixedX = cellOriginFixedX + placement.XFixed + rotatedFixedX;
                        int worldFixedY = cellOriginFixedY + placement.YFixed + rotatedFixedY;

                        int gxNode = NativeWorldToNodeIndex((long)worldFixedX - minFixedX);
                        int gyNode = NativeWorldToNodeIndex((long)worldFixedY - minFixedY);
                        if (gxNode < 0 || gxNode >= gridW || gyNode < 0 || gyNode >= gridH) continue;

                        if (!blocked[gxNode, gyNode])
                        {
                            blocked[gxNode, gyNode] = true;
                            blockedHere++;
                        }
                    }
                }
            }
            return blockedHere;
        }

        private static bool TryResolvePlacementCobj(string extendsPath, out string collisionObjectName, out CobjData cobj, out bool collisionDeclared)
        {
            collisionObjectName = null;
            cobj = null;
            collisionDeclared = false;
            if (string.IsNullOrWhiteSpace(extendsPath))
                return false;

            DungeonRunners.Data.GCNode node = DungeonRunners.Data.GCDatabase.Instance.ResolveWithInheritance(extendsPath);
            DungeonRunners.Data.GCNode description = node?.GetChild("Description");
            if (description == null || !description.HasProperty("CollisionObject"))
                return false;
            collisionDeclared = true;
            collisionObjectName = description.GetString("CollisionObject");
            if (string.IsNullOrWhiteSpace(collisionObjectName))
                return false;
            if (!DungeonRunners.Data.AuthoredSnapshotCatalog.Instance.TryLoadBinaryPayload(4, collisionObjectName, out byte[] bytes, out _))
                return false;
            cobj = CobjParser.Parse(bytes);
            return true;
        }

        private static bool TryCreateNativeCollisionPlacement(
            string collisionObjectName,
            TilePlacement placement,
            MazeGenerator.MazeCell cell,
            Dictionary<string, HybridCollisionObject> collisionObjects,
            out NativeCollisionPlacement nativePlacement)
        {
            nativePlacement = null;
            if (string.IsNullOrWhiteSpace(collisionObjectName))
                return false;

            if (!collisionObjects.TryGetValue(collisionObjectName, out HybridCollisionObject collision))
            {
                HybridCollisionObject.TryLoadFromAuthoredData(collisionObjectName, out collision, out _);
                collisionObjects[collisionObjectName] = collision;
            }
            if (collision == null || collision.RangeCount == 0 || collision.BlockCellSize <= 0
                || collision.BlockGridX <= 0 || collision.BlockGridY <= 0)
                return false;

            int headingDegrees = NativeZRotateTableIndex(placement.HeadingFixed);
            nativePlacement = new NativeCollisionPlacement
            {
                Collision = collision,
                PositionFixedX = cell.WorldOriginFixedX + placement.XFixed,
                PositionFixedY = cell.WorldOriginFixedY + placement.YFixed,
                PositionFixedZ = placement.ZFixed,
                CosFixed = DungeonRunners.Combat.UnitMover.ZRotateCosFixed(headingDegrees),
                SinFixed = DungeonRunners.Combat.UnitMover.ZRotateSinFixed(headingDegrees)
            };
            return true;
        }

        private static int ApplyNativeNodeCollision(
            NativeCollisionPlacement placement,
            bool[,] blocked,
            bool[,] inMazeFootprint,
            int[,] heightGridFixed,
            bool[,] heightKnown,
            int gridW,
            int gridH,
            int minFixedX,
            int minFixedY)
        {
            HybridCollisionObject collision = placement?.Collision;
            AuthoredEntityCollision entityBounds = placement?.EntityBounds;
            if (collision == null && entityBounds == null)
                return 0;

            int localMinFixedX = entityBounds != null ? entityBounds.MinX : collision.BlockOriginX << 8;
            int localMinFixedY = entityBounds != null ? entityBounds.MinY : collision.BlockOriginY << 8;
            int localMaxFixedX = entityBounds != null ? entityBounds.MaxX : (collision.BlockOriginX + collision.BlockGridX * collision.BlockCellSize) << 8;
            int localMaxFixedY = entityBounds != null ? entityBounds.MaxY : (collision.BlockOriginY + collision.BlockGridY * collision.BlockCellSize) << 8;

            RotateFixed(localMinFixedX, localMinFixedY, placement.CosFixed, placement.SinFixed, out int corner0X, out int corner0Y);
            RotateFixed(localMaxFixedX, localMinFixedY, placement.CosFixed, placement.SinFixed, out int corner1X, out int corner1Y);
            RotateFixed(localMinFixedX, localMaxFixedY, placement.CosFixed, placement.SinFixed, out int corner2X, out int corner2Y);
            RotateFixed(localMaxFixedX, localMaxFixedY, placement.CosFixed, placement.SinFixed, out int corner3X, out int corner3Y);

            int worldMinFixedX = placement.PositionFixedX + Math.Min(Math.Min(corner0X, corner1X), Math.Min(corner2X, corner3X));
            int worldMinFixedY = placement.PositionFixedY + Math.Min(Math.Min(corner0Y, corner1Y), Math.Min(corner2Y, corner3Y));
            int worldMaxFixedX = placement.PositionFixedX + Math.Max(Math.Max(corner0X, corner1X), Math.Max(corner2X, corner3X));
            int worldMaxFixedY = placement.PositionFixedY + Math.Max(Math.Max(corner0Y, corner1Y), Math.Max(corner2Y, corner3Y));
            int nodeCenterBaseFixedX = minFixedX + NativeNodeCenterOffsetFixed;
            int nodeCenterBaseFixedY = minFixedY + NativeNodeCenterOffsetFixed;
            int gx0 = Math.Max(0, (int)CeilDiv((long)worldMinFixedX - NativeCollisionHalfExtentFixed - nodeCenterBaseFixedX, NodeResolutionFixed));
            int gy0 = Math.Max(0, (int)CeilDiv((long)worldMinFixedY - NativeCollisionHalfExtentFixed - nodeCenterBaseFixedY, NodeResolutionFixed));
            int gx1 = Math.Min(gridW - 1, FloorDiv((long)worldMaxFixedX + NativeCollisionHalfExtentFixed - nodeCenterBaseFixedX, NodeResolutionFixed));
            int gy1 = Math.Min(gridH - 1, FloorDiv((long)worldMaxFixedY + NativeCollisionHalfExtentFixed - nodeCenterBaseFixedY, NodeResolutionFixed));
            if (gx0 > gx1 || gy0 > gy1)
                return 0;

            int count = 0;
            for (int gx = gx0; gx <= gx1; gx++)
            {
                for (int gy = gy0; gy <= gy1; gy++)
                {
                    if (blocked[gx, gy] || !inMazeFootprint[gx, gy] || !heightKnown[gx, gy])
                        continue;

                    int worldFixedX = nodeCenterBaseFixedX + gx * NodeResolutionFixed;
                    int worldFixedY = nodeCenterBaseFixedY + gy * NodeResolutionFixed;
                    int worldMinZ = heightGridFixed[gx, gy];
                    WorldToObjectLocalFixed(
                        placement,
                        worldFixedX - NativeCollisionHalfExtentFixed,
                        worldFixedY - NativeCollisionHalfExtentFixed,
                        worldMinZ,
                        out int local0X,
                        out int local0Y,
                        out int local0Z);
                    WorldToObjectLocalFixed(
                        placement,
                        worldFixedX + NativeCollisionHalfExtentFixed,
                        worldFixedY + NativeCollisionHalfExtentFixed,
                        worldMinZ + NativeCollisionHeightFixed,
                        out int local1X,
                        out int local1Y,
                        out int local1Z);

                    int queryMinX = Math.Min(local0X, local1X);
                    int queryMinY = Math.Min(local0Y, local1Y);
                    int queryMinZ = Math.Min(local0Z, local1Z);
                    int queryMaxX = Math.Max(local0X, local1X);
                    int queryMaxY = Math.Max(local0Y, local1Y);
                    int queryMaxZ = Math.Max(local0Z, local1Z);
                    bool hit = collision != null
                        ? collision.TestBoundingBoxFixed(queryMinX, queryMinY, queryMinZ, queryMaxX, queryMaxY, queryMaxZ)
                        : queryMinX <= entityBounds.MaxX && queryMaxX >= entityBounds.MinX
                            && queryMinY <= entityBounds.MaxY && queryMaxY >= entityBounds.MinY
                            && queryMinZ <= entityBounds.MaxZ && queryMaxZ >= entityBounds.MinZ;
                    if (!hit)
                        continue;

                    blocked[gx, gy] = true;
                    count++;
                }
            }
            return count;
        }

        private static void WorldToObjectLocalFixed(
            NativeCollisionPlacement placement,
            int worldFixedX,
            int worldFixedY,
            int worldFixedZ,
            out int localFixedX,
            out int localFixedY,
            out int localFixedZ)
        {
            int dx = worldFixedX - placement.PositionFixedX;
            int dy = worldFixedY - placement.PositionFixedY;
            if (placement.EntityBounds != null)
            {
                AuthoredEntityCollision.ToLocalFixed(dx, dy, placement.HeadingFixed, out localFixedX, out localFixedY);
                localFixedZ = worldFixedZ - placement.PositionFixedZ;
                return;
            }
            localFixedX = (int)(((long)placement.CosFixed * dx) >> 8)
                + (int)(((long)placement.SinFixed * dy) >> 8);
            localFixedY = (int)((-(long)placement.SinFixed * dx) >> 8)
                + (int)(((long)placement.CosFixed * dy) >> 8);
            localFixedZ = worldFixedZ - placement.PositionFixedZ;
        }

        private static bool IsTerrainHeightSurfacePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;
            string normalized = path.Replace('\\', '.').Replace('/', '.');
            return normalized.IndexOf(".floor.", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void MaterializeWorldCollisionHeights(
            string zoneName,
            int minFixedX,
            int minFixedY,
            int referenceFixedZ,
            int gridW,
            int gridH,
            bool[,] inMazeFootprint,
            int[,] heightGridFixed,
            bool[,] heightKnown)
        {
            for (int gx = 0; gx < gridW; gx++)
            {
                for (int gy = 0; gy < gridH; gy++)
                {
                    if (!inMazeFootprint[gx, gy])
                        continue;

                    int worldFixedX = minFixedX + gx * NodeResolutionFixed + NativeNodeCenterOffsetFixed;
                    int worldFixedY = minFixedY + gy * NodeResolutionFixed + NativeNodeCenterOffsetFixed;
                    int reference = heightKnown[gx, gy] ? heightGridFixed[gx, gy] : referenceFixedZ;
                    if (!WorldCollision.Instance.TryGetTerrainHeightFixed(
                            zoneName,
                            zoneName,
                            worldFixedX,
                            worldFixedY,
                            reference,
                            out int heightFixed,
                            out _))
                        continue;

                    heightGridFixed[gx, gy] = heightFixed;
                    heightKnown[gx, gy] = true;
                }
            }
        }

        private static void MaterializeNativeSeedHeight(
            int seedFixedX,
            int seedFixedY,
            int seedFixedZ,
            int minFixedX,
            int minFixedY,
            int gridW,
            int gridH,
            bool[,] inMazeFootprint,
            int[,] heightGridFixed,
            bool[,] heightKnown)
        {
            int seedGridX = NativeWorldToNodeIndex((long)seedFixedX - minFixedX);
            int seedGridY = NativeWorldToNodeIndex((long)seedFixedY - minFixedY);
            if (seedGridX < 0 || seedGridX >= gridW || seedGridY < 0 || seedGridY >= gridH)
                return;
            if (!inMazeFootprint[seedGridX, seedGridY] || heightKnown[seedGridX, seedGridY])
                return;
            heightGridFixed[seedGridX, seedGridY] = seedFixedZ;
            heightKnown[seedGridX, seedGridY] = true;
        }

        private static void BuildNativeGraph(
            PathMap pathMap,
            int seedFixedX,
            int seedFixedY,
            int minFixedX,
            int minFixedY,
            int gridW,
            int gridH,
            bool[,] heightKnown,
            bool[,] blocked,
            bool[,] inMazeFootprint,
            int[,] heightGridFixed,
            out int nativeConnectedCount,
            out int inaccessibleCount)
        {
            var nodes = new PathNode[gridW, gridH];
            var spaceParents = new byte[0xFD];
            byte spaceIdGeneration = 0;
            nativeConnectedCount = 0;
            inaccessibleCount = 0;
            int seedGridX = NativeWorldToNodeIndex((long)seedFixedX - minFixedX);
            int seedGridY = NativeWorldToNodeIndex((long)seedFixedY - minFixedY);
            PathNode seed = AddNativeNode(
                nodes,
                seedGridX,
                seedGridY,
                minFixedX,
                minFixedY,
                gridW,
                gridH,
                heightKnown,
                blocked,
                inMazeFootprint,
                heightGridFixed);
            if (seed == null)
            {
                Debug.LogError(
                    $"[PATHMAP-BUILD] seedValid=False fixed8=({seedFixedX},{seedFixedY}) grid=({seedGridX},{seedGridY})");
                pathMap.SetNativeGraphState(spaceIdGeneration, spaceParents, nativeConnectedCount);
                return;
            }

            if (seed.SolidFlag != 0xFE)
            {
                var queue = new Queue<PathNode>();
                queue.Enqueue(seed);
                while (queue.Count > 0)
                {
                    PathNode current = queue.Dequeue();
                    for (int direction = 0; direction < PathMap.Directions.Length; direction += 2)
                    {
                        VisitNativeCardinalDirection(
                            current,
                            direction,
                            nodes,
                            queue,
                            minFixedX,
                            minFixedY,
                            gridW,
                            gridH,
                            heightKnown,
                            blocked,
                            inMazeFootprint,
                            heightGridFixed,
                            ref spaceIdGeneration,
                            spaceParents,
                            ref nativeConnectedCount);
                    }
                }
            }

            for (int gridY = 0; gridY < gridH; gridY++)
            {
                for (int gridX = 0; gridX < gridW; gridX++)
                {
                    PathNode node = nodes[gridX, gridY];
                    if (node == null)
                        continue;
                    pathMap.SetNodeFixed(node, node.WorldFixedX, node.WorldFixedY, node.HeightFixed);
                }
            }

            pathMap.SetNativeGraphState(spaceIdGeneration, spaceParents, nativeConnectedCount);
            inaccessibleCount = pathMap.NodeCount - pathMap.WalkableCount;
        }

        private static PathNode AddNativeNode(
            PathNode[,] nodes,
            int gridX,
            int gridY,
            int minFixedX,
            int minFixedY,
            int gridW,
            int gridH,
            bool[,] heightKnown,
            bool[,] blocked,
            bool[,] inMazeFootprint,
            int[,] heightGridFixed)
        {
            if (gridX < 0 || gridX >= gridW || gridY < 0 || gridY >= gridH)
                return null;
            if (nodes[gridX, gridY] != null)
                throw new InvalidOperationException($"PathMap node ({gridX},{gridY}) was added twice");

            bool valid = inMazeFootprint[gridX, gridY]
                && heightKnown[gridX, gridY]
                && !blocked[gridX, gridY];
            var node = new PathNode
            {
                GridX = gridX,
                GridY = gridY,
                WorldFixedX = minFixedX + gridX * NodeResolutionFixed + NativeNodeCenterOffsetFixed,
                WorldFixedY = minFixedY + gridY * NodeResolutionFixed + NativeNodeCenterOffsetFixed,
                HeightFixed = heightKnown[gridX, gridY] ? heightGridFixed[gridX, gridY] : 0,
                ConnectionFlags = 0,
                SolidFlag = valid ? (byte)0xFF : (byte)0xFE,
                VisitedDirectionFlags = 0
            };
            nodes[gridX, gridY] = node;
            return node;
        }

        private static void VisitNativeCardinalDirection(
            PathNode current,
            int direction,
            PathNode[,] nodes,
            Queue<PathNode> queue,
            int minFixedX,
            int minFixedY,
            int gridW,
            int gridH,
            bool[,] heightKnown,
            bool[,] blocked,
            bool[,] inMazeFootprint,
            int[,] heightGridFixed,
            ref byte spaceIdGeneration,
            byte[] spaceParents,
            ref int nativeConnectedCount)
        {
            if ((current.VisitedDirectionFlags & (1 << direction)) != 0)
                return;

            var (deltaX, deltaY) = PathMap.Directions[direction];
            int targetGridX = current.GridX + deltaX;
            int targetGridY = current.GridY + deltaY;
            if (targetGridX < 0 || targetGridX >= gridW || targetGridY < 0 || targetGridY >= gridH)
                return;

            PathNode target = nodes[targetGridX, targetGridY];
            if (target == null)
            {
                target = AddNativeNode(
                    nodes,
                    targetGridX,
                    targetGridY,
                    minFixedX,
                    minFixedY,
                    gridW,
                    gridH,
                    heightKnown,
                    blocked,
                    inMazeFootprint,
                    heightGridFixed);
                if (target == null)
                {
                    current.VisitedDirectionFlags |= (byte)(1 << direction);
                    return;
                }
            }

            byte previousVisitedDirectionFlags = target.VisitedDirectionFlags;
            if (!TryConnectNativeNodes(current, target, direction, ref spaceIdGeneration, spaceParents))
                return;

            if (previousVisitedDirectionFlags == 0)
            {
                queue.Enqueue(target);
                nativeConnectedCount++;
            }

            VisitNativeDiagonalDirections(current, direction, nodes, ref spaceIdGeneration, spaceParents);
            VisitNativeDiagonalDirections(target, OppositeDirection(direction), nodes, ref spaceIdGeneration, spaceParents);
        }

        private static void VisitNativeDiagonalDirections(
            PathNode current,
            int cardinalDirection,
            PathNode[,] nodes,
            ref byte spaceIdGeneration,
            byte[] spaceParents)
        {
            int clockwise = cardinalDirection + 1;
            int counterClockwise = cardinalDirection == 0 ? 7 : cardinalDirection - 1;
            TryVisitNativeDiagonalDirection(current, clockwise, nodes, ref spaceIdGeneration, spaceParents);
            TryVisitNativeDiagonalDirection(current, counterClockwise, nodes, ref spaceIdGeneration, spaceParents);
        }

        private static void TryVisitNativeDiagonalDirection(
            PathNode current,
            int direction,
            PathNode[,] nodes,
            ref byte spaceIdGeneration,
            byte[] spaceParents)
        {
            if ((current.VisitedDirectionFlags & (1 << direction)) != 0)
                return;

            var (deltaX, deltaY) = PathMap.Directions[direction];
            int targetGridX = current.GridX + deltaX;
            int targetGridY = current.GridY + deltaY;
            if (targetGridX < 0 || targetGridX >= nodes.GetLength(0)
                || targetGridY < 0 || targetGridY >= nodes.GetLength(1))
                return;

            PathNode target = nodes[targetGridX, targetGridY];
            if (target == null || target.SolidFlag >= 0xFE)
                return;
            if (!CanCheckNativeDiagonal(current, target, direction))
                return;

            TryConnectNativeNodes(current, target, direction, ref spaceIdGeneration, spaceParents);
        }

        private static bool CanCheckNativeDiagonal(PathNode current, PathNode target, int direction)
        {
            var corner = DiagonalCornerEdges[direction];
            bool path0 = (current.VisitedDirectionFlags & (1 << corner.a0)) != 0
                && (target.VisitedDirectionFlags & (1 << corner.b0)) != 0;
            bool path1 = (current.VisitedDirectionFlags & (1 << corner.a1)) != 0
                && (target.VisitedDirectionFlags & (1 << corner.b1)) != 0;
            return path0 || path1;
        }

        private static bool TryConnectNativeNodes(
            PathNode current,
            PathNode target,
            int direction,
            ref byte spaceIdGeneration,
            byte[] spaceParents)
        {
            bool connected = false;
            if (target.SolidFlag != 0xFE && CheckNativeConnection(current, target, direction))
            {
                ConnectNativeNodes(current, target, direction, ref spaceIdGeneration, spaceParents);
                connected = true;
            }

            target.VisitedDirectionFlags |= (byte)(1 << OppositeDirection(direction));
            current.VisitedDirectionFlags |= (byte)(1 << direction);
            return connected;
        }

        private static bool CheckNativeConnection(PathNode current, PathNode target, int direction)
        {
            long heightDelta = (long)target.HeightFixed - current.HeightFixed;
            if (heightDelta < 0)
                heightDelta = -heightDelta;
            if (heightDelta > HeightConnectThresholdFixed)
                return false;
            if ((direction & 1) == 0)
                return true;

            var corner = DiagonalCornerEdges[direction];
            bool path0 = (current.ConnectionFlags & (1 << corner.a0)) != 0
                && (target.ConnectionFlags & (1 << corner.b0)) != 0;
            bool path1 = (current.ConnectionFlags & (1 << corner.a1)) != 0
                && (target.ConnectionFlags & (1 << corner.b1)) != 0;
            return path0 || path1;
        }

        private static void ConnectNativeNodes(
            PathNode current,
            PathNode target,
            int direction,
            ref byte spaceIdGeneration,
            byte[] spaceParents)
        {
            current.ConnectionFlags |= (byte)(1 << direction);
            target.ConnectionFlags |= (byte)(1 << OppositeDirection(direction));

            byte currentSpaceId = current.SolidFlag;
            if (currentSpaceId == 0xFF)
            {
                if (target.SolidFlag != 0xFF)
                {
                    current.SolidFlag = target.SolidFlag;
                    return;
                }

                spaceIdGeneration++;
                if (spaceIdGeneration > 0xFC)
                    throw new InvalidOperationException("PathMap space identifier generation exceeded 0xFC");
                spaceParents[spaceIdGeneration] = spaceIdGeneration;
                current.SolidFlag = spaceIdGeneration;
                currentSpaceId = spaceIdGeneration;
            }
            else if (target.SolidFlag != 0xFF)
            {
                byte targetSpaceId = target.SolidFlag;
                if (targetSpaceId < currentSpaceId)
                    spaceParents[currentSpaceId] = targetSpaceId;
                else if (targetSpaceId > currentSpaceId)
                    spaceParents[targetSpaceId] = currentSpaceId;
                return;
            }

            target.SolidFlag = currentSpaceId;
        }

        private static int OppositeDirection(int direction)
        {
            return (direction + 4) & 7;
        }

        private static int CobjCellCenterFixed(int origin, int index, int cellSize)
        {
            return (int)(((long)origin << 8) + (((long)2 * index + 1) * cellSize << 7));
        }

        private static void RotateFixed(int localFixedX, int localFixedY, int cosFixed, int sinFixed, out int rotatedFixedX, out int rotatedFixedY)
        {
            rotatedFixedX = (int)(((long)localFixedX * cosFixed) >> 8)
                - (int)(((long)localFixedY * sinFixed) >> 8);
            rotatedFixedY = (int)(((long)localFixedX * sinFixed) >> 8)
                + (int)(((long)localFixedY * cosFixed) >> 8);
        }

        private static int NativeWorldToNodeIndex(long relativeFixed)
        {
            return (int)(relativeFixed / NodeResolutionFixed);
        }

        private static int FloorDiv(long value, int divisor)
        {
            if (value >= 0)
                return (int)(value / divisor);
            return -(int)((-value + divisor - 1L) / divisor);
        }

        private static long CeilDiv(long value, int divisor)
        {
            return -FloorDiv(-value, divisor);
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
    }
}
