using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DungeonRunners.Engine;
using Mono.Data.Sqlite;

namespace DungeonRunners.Core
{
    public enum PathReachability
    {
        CoverageMissing,
        Reachable,
        Blocked
    }

    public class PathNode
    {
        public int GridX;
        public int GridY;
        public int WorldFixedX;
        public int WorldFixedY;
        public int HeightFixed;
        public byte ConnectionFlags;
        public byte SolidFlag;
        public byte VisitedDirectionFlags;
        public int ConnectedSpaceId = -1;

        public bool IsWalkable => SolidFlag < 0xFE;
    }

    public class PathMap
    {
        public string ZoneName { get; private set; }
        public int WorldOffsetFixedX => _worldOffsetFixedX;
        public int WorldOffsetFixedY => _worldOffsetFixedY;

        private const int TILE_SIZE_FIXED = 0xA00;

        private Dictionary<(int, int), PathNode> _nodeGrid = new Dictionary<(int, int), PathNode>();
        private int _walkableCount;

        private int _worldOffsetFixedX, _worldOffsetFixedY;
        private int _minWorldFixedX, _maxWorldFixedX;
        private int _minWorldFixedY, _maxWorldFixedY;
        private bool _connectedSpacesDirty = true;
        private bool _hasNativeGraphState;
        private byte _nativeSpaceIdGeneration;
        private byte[] _nativeSpaceParents = new byte[0xFD];
        private int _nativeConnectedNodeCount;
        public int MinWorldFixedX => _minWorldFixedX;
        public int MaxWorldFixedX => _maxWorldFixedX;
        public int MinWorldFixedY => _minWorldFixedY;
        public int MaxWorldFixedY => _maxWorldFixedY;
        public int NodeCount => _nodeGrid.Count;
        public int GridWidth => ComputeGridDimension(_minWorldFixedX, _maxWorldFixedX);
        public int GridHeight => ComputeGridDimension(_minWorldFixedY, _maxWorldFixedY);
        internal bool HasNativeGraphState => _hasNativeGraphState;
        internal byte NativeSpaceIdGeneration => _nativeSpaceIdGeneration;
        internal int NativeConnectedNodeCount => _nativeConnectedNodeCount;

        public int WalkableCount => _walkableCount;

        public static PathMap CreateEmptyFixed(string zoneName, int minFixedX, int maxFixedX, int minFixedY, int maxFixedY)
        {
            var pathMap = new PathMap
            {
                ZoneName = zoneName,
                _worldOffsetFixedX = minFixedX,
                _worldOffsetFixedY = minFixedY,
                _minWorldFixedX = minFixedX,
                _maxWorldFixedX = maxFixedX,
                _minWorldFixedY = minFixedY,
                _maxWorldFixedY = maxFixedY,
            };
            return pathMap;
        }

        public void SetNodeFixed(PathNode node, int worldFixedX, int worldFixedY, int heightFixed)
        {
            if (node == null) return;
            node.WorldFixedX = worldFixedX;
            node.WorldFixedY = worldFixedY;
            node.HeightFixed = heightFixed;
            if (_nodeGrid.TryGetValue((node.GridX, node.GridY), out PathNode existingNode) && existingNode.IsWalkable)
                _walkableCount--;
            _nodeGrid[(node.GridX, node.GridY)] = node;
            if (node.IsWalkable)
                _walkableCount++;
            _hasNativeGraphState = false;
            _connectedSpacesDirty = true;
        }

        internal void SetNativeGraphState(byte spaceIdGeneration, byte[] spaceParents, int connectedNodeCount)
        {
            if (spaceParents == null || spaceParents.Length != 0xFD)
                throw new ArgumentException("Native space parent table must contain 253 bytes", nameof(spaceParents));
            if (spaceIdGeneration > 0xFC)
                throw new ArgumentOutOfRangeException(nameof(spaceIdGeneration));
            if (connectedNodeCount < 0 || connectedNodeCount > _nodeGrid.Count)
                throw new ArgumentOutOfRangeException(nameof(connectedNodeCount));

            _nativeSpaceIdGeneration = spaceIdGeneration;
            _nativeSpaceParents = (byte[])spaceParents.Clone();
            _nativeConnectedNodeCount = connectedNodeCount;
            _hasNativeGraphState = true;
            _connectedSpacesDirty = true;
        }

        internal byte[] CopyNativeSpaceParents()
        {
            return (byte[])_nativeSpaceParents.Clone();
        }

        public static readonly (int dx, int dy)[] Directions =
        {
            (0, 1), (1, 1), (1, 0), (1, -1),
            (0, -1), (-1, -1), (-1, 0), (-1, 1),
        };

        public static readonly int[] DirectionCosts = { 10, 14, 10, 14, 10, 14, 10, 14 };

        public static int GetDirFromAToB(PathNode a, PathNode b)
        {
            if (a == null || b == null) return -1;
            int dxRaw = b.GridX - a.GridX;
            int dyRaw = b.GridY - a.GridY;
            int dx = dxRaw < 0 ? -1 : (dxRaw > 0 ? 1 : 0);
            int dy = dyRaw < 0 ? -1 : (dyRaw > 0 ? 1 : 0);
            for (int directionIndex = 0; directionIndex < Directions.Length; directionIndex++)
            {
                if (Directions[directionIndex].dx == dx && Directions[directionIndex].dy == dy)
                    return directionIndex;
            }
            return -1;
        }

        public static PathMap LoadFromSQLite(SqliteConnection connection, string zoneName)
        {
            var pathMap = new PathMap();
            using (var zoneReader = DungeonRunners.Database.GameDatabase.ExecuteReader(connection,
                "SELECT zone_name, world_offset_x, world_offset_y, world_min_x, world_max_x, world_min_y, world_max_y FROM cache.pathmap_zones WHERE zone_name = @z", ("@z", zoneName)))
            {
                if (!zoneReader.Read())
                    throw new InvalidOperationException($"Pathmap zone is missing: {zoneName}");
                pathMap.ZoneName = zoneReader.GetString(0);
                int worldOffsetFixedX = GetSqliteFixed8(zoneReader, "world_offset_x");
                int worldOffsetFixedY = GetSqliteFixed8(zoneReader, "world_offset_y");
                int minWorldFixedX = GetSqliteFixed8(zoneReader, "world_min_x");
                int maxWorldFixedX = GetSqliteFixed8(zoneReader, "world_max_x");
                int minWorldFixedY = GetSqliteFixed8(zoneReader, "world_min_y");
                int maxWorldFixedY = GetSqliteFixed8(zoneReader, "world_max_y");
                pathMap.SetFixedBounds(worldOffsetFixedX, worldOffsetFixedY, minWorldFixedX, maxWorldFixedX, minWorldFixedY, maxWorldFixedY);
            }

            using (var nodeReader = DungeonRunners.Database.GameDatabase.ExecuteReader(connection,
                "SELECT gx, gy, wx, wy, h, c, s FROM cache.pathmap_nodes WHERE zone_name = @z ORDER BY id", ("@z", zoneName)))
            {
                while (nodeReader.Read())
                {
                    int worldFixedX = DungeonRunners.Database.GameDatabase.GetFixed8(nodeReader, "wx");
                    int worldFixedY = DungeonRunners.Database.GameDatabase.GetFixed8(nodeReader, "wy");
                    int heightFixed = DungeonRunners.Database.GameDatabase.GetFixed8(nodeReader, "h");
                    var node = new PathNode
                    {
                        GridX = nodeReader.GetInt32(0),
                        GridY = nodeReader.GetInt32(1),
                        ConnectionFlags = (byte)nodeReader.GetInt32(5),
                        SolidFlag = (byte)nodeReader.GetInt32(6)
                    };
                    pathMap.SetNodeFixed(node, worldFixedX, worldFixedY, heightFixed);
                }
            }
            DungeonRunners.Engine.Debug.Log($"[PATHMAP] loaded zone='{zoneName}' source=cache.db nodes={pathMap._nodeGrid.Count}");
            return pathMap;
        }

        public (int gridX, int gridY) WorldToGridFixed(int worldFixedX, int worldFixedY)
        {
            int gridX = NativeWorldToGridDiv(worldFixedX, _worldOffsetFixedX);
            int gridY = NativeWorldToGridDiv(worldFixedY, _worldOffsetFixedY);
            return (gridX, gridY);
        }

        public PathNode GetNodeAt(int gridX, int gridY)
        {
            _nodeGrid.TryGetValue((gridX, gridY), out var node);
            return node;
        }

        public PathNode GetNodeAtWorldFixed(int worldFixedX, int worldFixedY)
        {
            var (gridX, gridY) = WorldToGridFixed(worldFixedX, worldFixedY);
            if (!IsGridCoordinateInBounds(gridX, gridY))
                return null;

            PathNode node = GetWalkableNodeAtGrid(gridX, gridY);
            if (node != null)
                return node;

            bool xBoundary = false;
            if ((worldFixedX & 0xFF) == 0 && ((worldFixedX >> 8) % 10) == 0)
            {
                gridX--;
                xBoundary = true;
                node = GetWalkableNodeAtGrid(gridX, gridY);
                if (node != null)
                    return node;
            }

            if ((worldFixedY & 0xFF) != 0 || ((worldFixedY >> 8) % 10) != 0)
                return null;

            gridY--;
            node = GetWalkableNodeAtGrid(gridX, gridY);
            if (node != null)
                return node;
            if (!xBoundary)
                return null;

            gridX++;
            return GetWalkableNodeAtGrid(gridX, gridY);
        }

        public PathNode GetClosestNodeFixed(int worldFixedX, int worldFixedY, int heightFixed)
        {
            PathNode node = GetNodeAtWorldFixed(worldFixedX, worldFixedY);
            if (node != null)
                return node;

            var (gridX, gridY) = WorldToGridFixed(worldFixedX, worldFixedY);
            int centerFixedX = _worldOffsetFixedX + gridX * TILE_SIZE_FIXED + CellHalfFixed;
            int centerFixedY = _worldOffsetFixedY + gridY * TILE_SIZE_FIXED + CellHalfFixed;
            Span<int> searchOrder = stackalloc int[8];
            BuildNativeSearchOrderFixed(worldFixedX - centerFixedX, worldFixedY - centerFixedY, searchOrder);
            for (int orderIndex = 0; orderIndex < searchOrder.Length; orderIndex++)
            {
                int direction = searchOrder[orderIndex];
                var (deltaX, deltaY) = Directions[direction];
                PathNode candidate = GetWalkableNodeAtGrid(gridX + deltaX, gridY + deltaY);
                if (candidate == null)
                    continue;
                if (NativeAbs(candidate.HeightFixed - heightFixed) > TILE_SIZE_FIXED)
                    continue;
                return candidate;
            }

            return null;
        }

        public bool FindValidDestPointFixed(
            int startFixedX,
            int startFixedY,
            int targetFixedX,
            int targetFixedY,
            PathNode startNode,
            int maxDistanceFixed,
            out int validDestFixedX,
            out int validDestFixedY,
            out PathNode validDestNode)
        {
            validDestFixedX = startFixedX;
            validDestFixedY = startFixedY;
            validDestNode = null;
            if (startNode == null)
                return false;

            int deltaFixedX = startFixedX - targetFixedX;
            int deltaFixedY = startFixedY - targetFixedY;
            int lengthFixed = DungeonRunners.Combat.UnitMover.TableSquareRoot(
                NativeVectorLengthSquaredFixed(deltaFixedX, deltaFixedY));
            if (lengthFixed == 0)
            {
                validDestNode = startNode;
                return true;
            }

            int directionFixedX = FixedDivide(deltaFixedX, lengthFixed);
            int directionFixedY = FixedDivide(deltaFixedY, lengthFixed);
            int travelFixed = lengthFixed < maxDistanceFixed ? lengthFixed : maxDistanceFixed;
            bool found = FindFirstConnectedPointInDirFixed(
                targetFixedX,
                targetFixedY,
                directionFixedX,
                directionFixedY,
                travelFixed,
                startNode,
                out int foundFixedX,
                out int foundFixedY,
                out validDestNode);
            if (found)
            {
                validDestFixedX = foundFixedX;
                validDestFixedY = foundFixedY;
            }
            return found;
        }

        public int GetHeightAtFixed(int worldFixedX, int worldFixedY, int defaultHeightFixed)
        {
            return TryGetHeightAtFixed(worldFixedX, worldFixedY, out int heightFixed)
                ? heightFixed
                : defaultHeightFixed;
        }

        public bool TryGetHeightAtFixed(int worldFixedX, int worldFixedY, out int heightFixed)
        {
            if (worldFixedX < _minWorldFixedX || worldFixedX > _maxWorldFixedX ||
                worldFixedY < _minWorldFixedY || worldFixedY > _maxWorldFixedY)
            {
                heightFixed = 0;
                return false;
            }

            var node = GetNodeAtWorldFixed(worldFixedX, worldFixedY);
            if (node == null)
            {
                heightFixed = 0;
                return false;
            }

            return TryGetNativeConnectedMapHeightFixed(node, worldFixedX, worldFixedY, out heightFixed);
        }

        private bool TryGetNativeConnectedMapHeightFixed(PathNode node, int worldFixedX, int worldFixedY, out int heightFixed)
        {
            heightFixed = 0;
            if (node == null)
                return false;

            int localFixedX = worldFixedX - node.WorldFixedX;
            int localFixedY = worldFixedY - node.WorldFixedY;
            int xSign = NativeSign(localFixedX);
            int ySign = NativeSign(localFixedY);
            int baseHeight = node.HeightFixed;

            if (xSign == 0)
            {
                int axisHeight = baseHeight;
                if (ySign != 0 && !TryResolveConnectedNeighborHeightFixed(node, 0, ySign, out axisHeight))
                    return false;

                heightFixed = baseHeight + FixedDivideRawFixed(FixedMultiply(localFixedY, axisHeight - baseHeight), TILE_SIZE_FIXED);
                return true;
            }

            int xHeight = baseHeight;
            if (!TryResolveConnectedNeighborHeightFixed(node, xSign, 0, out xHeight))
                return false;

            if (ySign == 0)
            {
                heightFixed = baseHeight + FixedDivideRawFixed(FixedMultiply(localFixedX, xHeight - baseHeight), TILE_SIZE_FIXED);
                return true;
            }

            int yHeight = baseHeight;
            if (!TryResolveConnectedNeighborHeightFixed(node, 0, ySign, out yHeight))
                return false;

            int diagonalHeight = baseHeight;
            if (!TryResolveConnectedNeighborHeightFixed(node, xSign, ySign, out diagonalHeight))
                return false;

            int absX = NativeAbs(localFixedX);
            int absY = NativeAbs(localFixedY);
            int invX = TILE_SIZE_FIXED - absX;
            int invY = TILE_SIZE_FIXED - absY;

            int diagonalArea = FixedMultiply(absX, absY);
            int yArea = FixedMultiply(invX, absY);
            int xArea = FixedMultiply(absX, invY);
            int baseArea = FixedMultiply(invX, invY);
            int weighted =
                FixedMultiply(diagonalArea, diagonalHeight) +
                FixedMultiply(yArea, yHeight) +
                FixedMultiply(xArea, xHeight) +
                FixedMultiply(baseArea, baseHeight);

            heightFixed = FixedDivideRawFixed(weighted, TILE_SIZE_FIXED * TILE_SIZE_FIXED / FixedScale);
            return true;
        }

        private bool TryResolveConnectedNeighborHeightFixed(PathNode node, int dx, int dy, out int heightFixed)
        {
            heightFixed = node != null ? node.HeightFixed : 0;
            if (node == null)
                return false;

            int direction = DirectionIndexFromDelta(dx, dy);
            if (direction < 0)
                return true;
            if ((node.ConnectionFlags & (1 << direction)) == 0)
                return true;

            PathNode neighbor = GetNodeAt(node.GridX + dx, node.GridY + dy);
            if (neighbor == null)
                return false;

            heightFixed = neighbor.HeightFixed;
            return true;
        }

        private static int DirectionIndexFromDelta(int dx, int dy)
        {
            for (int directionIndex = 0; directionIndex < Directions.Length; directionIndex++)
            {
                if (Directions[directionIndex].dx == dx && Directions[directionIndex].dy == dy)
                    return directionIndex;
            }

            return -1;
        }

        public bool IsWalkableFixed(int worldFixedX, int worldFixedY)
        {
            if (worldFixedX < _minWorldFixedX || worldFixedX > _maxWorldFixedX ||
                worldFixedY < _minWorldFixedY || worldFixedY > _maxWorldFixedY)
                return false;
            var node = GetNodeAtWorldFixed(worldFixedX, worldFixedY);
            return node?.IsWalkable == true;
        }

        public PathReachability GetReachabilityFixed(int startFixedX, int startFixedY, int endFixedX, int endFixedY)
        {
            if (!TryCanReachPointFixed(startFixedX, startFixedY, endFixedX, endFixedY, out bool canReach))
                return PathReachability.CoverageMissing;
            return canReach ? PathReachability.Reachable : PathReachability.Blocked;
        }

        public bool CanReachPointFixed(int startFixedX, int startFixedY, int endFixedX, int endFixedY)
        {
            if (!TryBuildNativeRayDirectionFixed(
                    endFixedX - startFixedX,
                    endFixedY - startFixedY,
                    out int directionFixedX,
                    out int directionFixedY,
                    out int requestedDistanceFixed))
                return requestedDistanceFixed <= 0;

            int castDistanceFixed = CastGroundRayDistanceFixed(
                startFixedX,
                startFixedY,
                directionFixedX,
                directionFixedY,
                requestedDistanceFixed);
            return castDistanceFixed == requestedDistanceFixed;
        }

        public bool CanPathToFixed(int startFixedX, int startFixedY, int endFixedX, int endFixedY)
        {
            PathNode startNode = GetWalkableNodeAtWorldFixed(startFixedX, startFixedY);
            PathNode endNode = GetWalkableNodeAtWorldFixed(endFixedX, endFixedY);
            if (startNode == null || endNode == null)
                return false;
            if (startNode.GridX == endNode.GridX && startNode.GridY == endNode.GridY)
                return true;

            EnsureConnectedSpacesFixed();
            return startNode.ConnectedSpaceId >= 0 && startNode.ConnectedSpaceId == endNode.ConnectedSpaceId;
        }

        public bool TryCanReachPointFixed(int startFixedX, int startFixedY, int endFixedX, int endFixedY, out bool canReach)
        {
            canReach = false;
            if (startFixedX == endFixedX && startFixedY == endFixedY)
            {
                canReach = true;
                return true;
            }

            bool hasRayDirection = TryBuildNativeRayDirectionFixed(
                    endFixedX - startFixedX,
                    endFixedY - startFixedY,
                    out int dirXFixed,
                    out int dirYFixed,
                    out int totalDistFixed);
            if (!hasRayDirection)
            {
                if (totalDistFixed <= 0)
                {
                    canReach = true;
                    return true;
                }
                return false;
            }

            if (!TryGetClosestWalkableNodeAtWorldFixed(
                    startFixedX,
                    startFixedY,
                    0,
                    false,
                    out PathNode startNode,
                    out _,
                    out _))
                return false;

            canReach = CanReachPointFromNodeDirectionFixed(
                startNode,
                startFixedX,
                startFixedY,
                dirXFixed,
                dirYFixed,
                totalDistFixed);
            return true;
        }

        private PathNode GetWalkableNodeAtWorldFixed(int worldFixedX, int worldFixedY)
        {
            return GetNodeAtWorldFixed(worldFixedX, worldFixedY);
        }

        private PathNode GetWalkableNodeAtGrid(int gridX, int gridY)
        {
            if (!IsGridCoordinateInBounds(gridX, gridY))
                return null;
            PathNode node = GetNodeAt(gridX, gridY);
            return node != null && node.IsWalkable ? node : null;
        }

        private bool IsGridCoordinateInBounds(int gridX, int gridY)
        {
            return gridX >= 0 && gridX < NativeGridDimension(_maxWorldFixedX - _worldOffsetFixedX)
                && gridY >= 0 && gridY < NativeGridDimension(_maxWorldFixedY - _worldOffsetFixedY);
        }

        private static int NativeGridDimension(int spanFixed)
        {
            if (spanFixed < 0)
                return 0;
            return (int)(((long)spanFixed + TILE_SIZE_FIXED - 1) / TILE_SIZE_FIXED) + 1;
        }

        private void EnsureConnectedSpacesFixed()
        {
            if (!_connectedSpacesDirty)
                return;

            if (_hasNativeGraphState)
            {
                int gridWidth = NativeGridDimension(_maxWorldFixedX - _worldOffsetFixedX);
                int gridHeight = NativeGridDimension(_maxWorldFixedY - _worldOffsetFixedY);
                for (int gridY = 0; gridY < gridHeight; gridY++)
                {
                    for (int gridX = 0; gridX < gridWidth; gridX++)
                    {
                        if (_nodeGrid.TryGetValue((gridX, gridY), out PathNode node))
                            node.ConnectedSpaceId = ResolveNativeSpaceId(node.SolidFlag);
                    }
                }
                _connectedSpacesDirty = false;
                return;
            }

            var nodes = new List<PathNode>(_nodeGrid.Values);
            nodes.Sort((a, b) =>
            {
                int c = a.GridY.CompareTo(b.GridY);
                if (c != 0) return c;
                return a.GridX.CompareTo(b.GridX);
            });

            foreach (var node in nodes)
                node.ConnectedSpaceId = -1;

            int nextSpaceId = 0;
            var queue = new Queue<PathNode>();
            foreach (var root in nodes)
            {
                if (root == null || !root.IsWalkable || root.ConnectedSpaceId >= 0)
                    continue;

                root.ConnectedSpaceId = nextSpaceId;
                queue.Enqueue(root);
                while (queue.Count > 0)
                {
                    PathNode current = queue.Dequeue();
                    byte connectionFlags = current.ConnectionFlags;
                    for (int directionIndex = 0; directionIndex < Directions.Length; directionIndex++)
                    {
                        if ((connectionFlags & (1 << directionIndex)) == 0)
                            continue;

                        var (dx, dy) = Directions[directionIndex];
                        PathNode next = GetNodeAt(current.GridX + dx, current.GridY + dy);
                        if (next == null || !next.IsWalkable || next.ConnectedSpaceId >= 0)
                            continue;

                        next.ConnectedSpaceId = nextSpaceId;
                        queue.Enqueue(next);
                    }
                }
                nextSpaceId++;
            }

            _connectedSpacesDirty = false;
        }

        private int ResolveNativeSpaceId(byte spaceId)
        {
            if (spaceId == 0 || spaceId > 0xFC)
                return -1;

            byte current = spaceId;
            for (int depth = 0; depth < _nativeSpaceParents.Length; depth++)
            {
                byte parent = _nativeSpaceParents[current];
                if (parent == current)
                    return current;
                if (parent == 0 || parent > 0xFC)
                    return -1;
                current = parent;
            }
            return -1;
        }

        public bool FindFirstValidPointInDirFixed(
            int originFixedX,
            int originFixedY,
            int directionFixedX,
            int directionFixedY,
            int distanceFixed,
            out int validFixedX,
            out int validFixedY)
        {
            return FindFirstConnectedPointInDirFixed(
                originFixedX,
                originFixedY,
                directionFixedX,
                directionFixedY,
                distanceFixed,
                null,
                out validFixedX,
                out validFixedY,
                out _);
        }

        private bool FindFirstConnectedPointInDirFixed(
            int originFixedX,
            int originFixedY,
            int directionFixedX,
            int directionFixedY,
            int distanceFixed,
            PathNode requiredNode,
            out int connectedFixedX,
            out int connectedFixedY,
            out PathNode connectedNode)
        {
            connectedFixedX = originFixedX;
            connectedFixedY = originFixedY;
            connectedNode = null;
            int requiredSpaceId = -1;
            if (requiredNode != null)
            {
                EnsureConnectedSpacesFixed();
                requiredSpaceId = requiredNode.ConnectedSpaceId;
            }
            var (gridX, gridY) = WorldToGridFixed(originFixedX, originFixedY);
            if (distanceFixed == 0)
            {
                connectedNode = GetWalkableNodeAtGrid(gridX, gridY);
                return connectedNode != null
                    && (requiredNode == null
                        || (requiredSpaceId >= 0 && connectedNode.ConnectedSpaceId == requiredSpaceId));
            }

            int centerFixedX = _worldOffsetFixedX + gridX * TILE_SIZE_FIXED + CellHalfFixed;
            int centerFixedY = _worldOffsetFixedY + gridY * TILE_SIZE_FIXED + CellHalfFixed;
            int travelledFixed = 0;
            while (true)
            {
                connectedNode = GetWalkableNodeAtGrid(gridX, gridY);
                if (connectedNode != null
                    && (requiredNode == null
                        || (requiredSpaceId >= 0 && connectedNode.ConnectedSpaceId == requiredSpaceId)))
                {
                    connectedFixedX = originFixedX + FixedMultiply(directionFixedX, travelledFixed);
                    connectedFixedY = originFixedY + FixedMultiply(directionFixedY, travelledFixed);
                    ClampPointToNodeFixed(connectedNode, ref connectedFixedX, ref connectedFixedY);
                    return true;
                }

                if (!CollideRayWithNodeFixed(
                        centerFixedX,
                        centerFixedY,
                        originFixedX,
                        originFixedY,
                        directionFixedX,
                        directionFixedY,
                        out int exitDistanceFixed,
                        out int exitEdge)
                    || exitEdge < 0)
                    return false;
                if (distanceFixed <= exitDistanceFixed)
                    return false;

                var (deltaX, deltaY) = Directions[exitEdge];
                gridX += deltaX;
                gridY += deltaY;
                centerFixedX += deltaX * TILE_SIZE_FIXED;
                centerFixedY += deltaY * TILE_SIZE_FIXED;
                travelledFixed = exitDistanceFixed;
            }
        }

        public bool CastGroundRayBlockedFixed(int startFixedX, int startFixedY, int endFixedX, int endFixedY)
        {
            if (!TryGetClosestWalkableNodeAtWorldFixed(
                    startFixedX,
                    startFixedY,
                    0,
                    false,
                    out PathNode node,
                    out int clampedStartFixedX,
                    out int clampedStartFixedY))
                return true;

            return CastGroundRayBlockedFromNodeFixed(node, clampedStartFixedX, clampedStartFixedY, endFixedX, endFixedY);
        }

        public bool CastGroundRayFixed(
            int startFixedX,
            int startFixedY,
            int startFixedZ,
            int targetFixedX,
            int targetFixedY,
            out int resolvedFixedX,
            out int resolvedFixedY,
            out int resolvedFixedZ)
        {
            resolvedFixedX = startFixedX;
            resolvedFixedY = startFixedY;
            resolvedFixedZ = startFixedZ;
            if (!TryGetClosestWalkableNodeAtWorldFixed(
                    startFixedX,
                    startFixedY,
                    startFixedZ,
                    true,
                    out _,
                    out _,
                    out _))
                return false;

            bool collided = CastGroundRayHitHeightFixed(
                startFixedX,
                startFixedY,
                targetFixedX,
                targetFixedY,
                out resolvedFixedX,
                out resolvedFixedY,
                out resolvedFixedZ,
                out _,
                out _);
            if (TryGetHeightAtFixed(resolvedFixedX, resolvedFixedY, out int mapHeightFixed))
                resolvedFixedZ = mapHeightFixed;
            return collided;
        }

        public string DescribeGroundRayFixed(int startFixedX, int startFixedY, int endFixedX, int endFixedY)
        {
            if (!TryGetClosestWalkableNodeAtWorldFixed(
                    startFixedX,
                    startFixedY,
                    0,
                    false,
                    out PathNode node,
                    out int clampedStartFixedX,
                    out int clampedStartFixedY))
                return "startNode=none blocked=True";

            var trace = new StringBuilder();
            trace.Append("startNode=(").Append(node.GridX).Append(',').Append(node.GridY)
                .Append(") startCenter=(").Append(node.WorldFixedX).Append(',').Append(node.WorldFixedY)
                .Append(") startFlags=0x").Append(node.ConnectionFlags.ToString("X2", CultureInfo.InvariantCulture))
                .Append(" clamped=(").Append(clampedStartFixedX).Append(',').Append(clampedStartFixedY).Append(')');
            if (!TryBuildNativeRayDirectionFixed(
                    endFixedX - clampedStartFixedX,
                    endFixedY - clampedStartFixedY,
                    out int dirX,
                    out int dirY,
                    out int totalDistFixed))
                return trace.Append(" total=").Append(totalDistFixed).Append(" blocked=False").ToString();

            trace.Append(" dir=(").Append(dirX).Append(',').Append(dirY).Append(") total=").Append(totalDistFixed);
            int maxIterations = Math.Max(_nodeGrid.Count, 1);
            for (int iteration = 0; iteration < maxIterations; iteration++)
            {
                if (!CollideRayWithNodeFixed(
                        node.WorldFixedX,
                        node.WorldFixedY,
                        clampedStartFixedX,
                        clampedStartFixedY,
                        dirX,
                        dirY,
                        out int exitDistanceFixed,
                        out int exitEdge)
                    || exitEdge < 0)
                    return trace.Append(" step=").Append(iteration).Append(":no-exit blocked=False").ToString();

                bool finishes = exitDistanceFixed >= totalDistFixed;
                bool connected = (node.ConnectionFlags & (1 << exitEdge)) != 0;
                trace.Append(" step=").Append(iteration).Append(":node=(").Append(node.GridX).Append(',').Append(node.GridY)
                    .Append(") flags=0x").Append(node.ConnectionFlags.ToString("X2", CultureInfo.InvariantCulture))
                    .Append(" exit=").Append(exitEdge).Append('@').Append(exitDistanceFixed)
                    .Append(" connected=").Append(connected);
                if (finishes)
                    return trace.Append(" blocked=False").ToString();
                if (!connected)
                    return trace.Append(" blocked=True").ToString();

                var (deltaX, deltaY) = Directions[exitEdge];
                PathNode next = GetNodeAt(node.GridX + deltaX, node.GridY + deltaY);
                if (next == null || !next.IsWalkable)
                    return trace.Append(" next=").Append(next == null ? "none" : $"({next.GridX},{next.GridY})/0x{next.SolidFlag:X2}")
                        .Append(" blocked=True").ToString();
                node = next;
            }
            return trace.Append(" blocked=True reason=iteration-limit").ToString();
        }

        public int CastGroundRayDistanceFixed(int startFixedX, int startFixedY, int directionFixedX, int directionFixedY, int requestedDistanceFixed)
        {
            if (requestedDistanceFixed <= 0)
                return 0;
            if (directionFixedX == 0 && directionFixedY == 0)
                return requestedDistanceFixed;
            if (!TryGetClosestWalkableNodeAtWorldFixed(
                    startFixedX,
                    startFixedY,
                    0,
                    false,
                    out PathNode node,
                    out int clampedStartFixedX,
                    out int clampedStartFixedY))
                return 0;

            int actualDistanceFixed = 0;
            int maxIterations = Math.Max(_nodeGrid.Count, 1);
            for (int iteration = 0; iteration < maxIterations; iteration++)
            {
                if (!CollideRayWithNodeFixed(
                        node.WorldFixedX,
                        node.WorldFixedY,
                        clampedStartFixedX,
                        clampedStartFixedY,
                        directionFixedX,
                        directionFixedY,
                        out int exitDistanceFixed,
                        out int exitEdge)
                    || exitEdge < 0)
                    return actualDistanceFixed;
                if (requestedDistanceFixed <= exitDistanceFixed)
                    return requestedDistanceFixed;

                actualDistanceFixed = exitDistanceFixed;
                if ((node.ConnectionFlags & (1 << exitEdge)) == 0)
                    return actualDistanceFixed;
                var (deltaX, deltaY) = Directions[exitEdge];
                PathNode next = GetNodeAt(node.GridX + deltaX, node.GridY + deltaY);
                if (next == null || !next.IsWalkable)
                    return actualDistanceFixed;
                node = next;
            }
            return actualDistanceFixed;
        }

        private bool CastGroundRayBlockedFromNodeFixed(PathNode node, int startFixedX, int startFixedY, int endFixedX, int endFixedY)
        {
            if (node == null) return true;
            if (!TryBuildNativeRayDirectionFixed(endFixedX - startFixedX, endFixedY - startFixedY, out int dirX, out int dirY, out int totalDistFixed))
                return false;

            int maxIterations = Math.Max(_nodeGrid.Count, 1);
            for (int iter = 0; iter < maxIterations; iter++)
            {
                int nx = node.WorldFixedX;
                int ny = node.WorldFixedY;
                if (!CollideRayWithNodeFixed(nx, ny, startFixedX, startFixedY, dirX, dirY,
                        out int exitT, out int exitEdge) || exitEdge < 0)
                    return false;
                if (exitT >= totalDistFixed) return false;
                if ((node.ConnectionFlags & (1 << exitEdge)) == 0) return true;
                var (ddx, ddy) = Directions[exitEdge];
                var next = GetNodeAt(node.GridX + ddx, node.GridY + ddy);
                if (next == null || !next.IsWalkable) return true;
                node = next;
            }
            return true;
        }

        private bool CanReachPointFromNodeDirectionFixed(PathNode node, int startFixedX, int startFixedY, int dirX, int dirY, int totalDistFixed)
        {
            if (node == null) return false;
            int maxIterations = Math.Max(_nodeGrid.Count, 1);
            for (int iter = 0; iter < maxIterations; iter++)
            {
                int nx = node.WorldFixedX;
                int ny = node.WorldFixedY;
                if (!CollideRayWithNodeFixed(nx, ny, startFixedX, startFixedY, dirX, dirY,
                        out int exitT, out int exitEdge) || exitEdge < 0)
                    return false;
                if (exitT >= totalDistFixed) return true;
                if ((node.ConnectionFlags & (1 << exitEdge)) == 0) return false;
                var (ddx, ddy) = Directions[exitEdge];
                var next = GetNodeAt(node.GridX + ddx, node.GridY + ddy);
                if (next == null || !next.IsWalkable) return false;
                node = next;
            }
            return false;
        }

        public bool CastGroundRaySlideFixed(int startFixedX, int startFixedY, int startFixedZ, int targetFixedX, int targetFixedY, out int slidFixedX, out int slidFixedY, out int slidFixedZ)
        {
            if (!TryGetClosestWalkableNodeAtWorldFixed(
                    startFixedX,
                    startFixedY,
                    startFixedZ,
                    true,
                    out _,
                    out int clampedStartFixedX,
                    out int clampedStartFixedY))
            {
                slidFixedX = startFixedX;
                slidFixedY = startFixedY;
                slidFixedZ = startFixedZ;
                return false;
            }

            if (!CastGroundRayHitHeightFixed(clampedStartFixedX, clampedStartFixedY, targetFixedX, targetFixedY, out int hitFixedX, out int hitFixedY, out int hitFixedZ, out int exitEdge, out int remainingFixed))
            {
                slidFixedX = hitFixedX; slidFixedY = hitFixedY; slidFixedZ = hitFixedZ;
                return false;
            }
            int dx = targetFixedX - clampedStartFixedX;
            int dy = targetFixedY - clampedStartFixedY;
            if (!TryBuildNativeRayDirectionFixed(dx, dy, out int dirX, out int dirY, out int _) ||
                !TryResolveNativeSlideDirectionFixed(exitEdge, dirX, dirY, out int slideUnitX, out int slideUnitY))
            {
                slidFixedX = hitFixedX; slidFixedY = hitFixedY; slidFixedZ = hitFixedZ;
                return true;
            }
            int slideDx = (int)(((long)remainingFixed * slideUnitX) >> 8);
            int slideDy = (int)(((long)remainingFixed * slideUnitY) >> 8);
            int slideEndFixedX = hitFixedX + slideDx;
            int slideEndFixedY = hitFixedY + slideDy;
            CastGroundRayHitHeightFixed(hitFixedX, hitFixedY, slideEndFixedX, slideEndFixedY, out int fx, out int fy, out int fz, out int _, out int _);
            slidFixedX = fx; slidFixedY = fy; slidFixedZ = fz;
            return true;
        }

        public bool CastGroundRaySlideFixed(int startFixedX, int startFixedY, int targetFixedX, int targetFixedY, out int slidFixedX, out int slidFixedY)
        {
            int startFixedZ = ResolveGroundRayNodeHeightFixed(startFixedX, startFixedY, 0);
            return CastGroundRaySlideFixed(startFixedX, startFixedY, startFixedZ, targetFixedX, targetFixedY, out slidFixedX, out slidFixedY, out int _);
        }

        private int ResolveGroundRayNodeHeightFixed(int worldFixedX, int worldFixedY, int fallbackFixedZ)
        {
            var node = GetWalkableNodeAtWorldFixed(worldFixedX, worldFixedY);
            return node != null ? node.HeightFixed : fallbackFixedZ;
        }

        private bool TryGetClosestWalkableNodeAtWorldFixed(
            int worldFixedX,
            int worldFixedY,
            int heightFixed,
            bool useHeightGate,
            out PathNode node,
            out int clampedFixedX,
            out int clampedFixedY)
        {
            node = GetWalkableNodeAtWorldFixed(worldFixedX, worldFixedY);
            clampedFixedX = worldFixedX;
            clampedFixedY = worldFixedY;
            if (node != null)
            {
                ClampPointToNodeFixed(node, ref clampedFixedX, ref clampedFixedY);
                return true;
            }

            var (gridX, gridY) = WorldToGridFixed(worldFixedX, worldFixedY);
            int centerFixedX = _worldOffsetFixedX + (gridX * TILE_SIZE_FIXED) + CellHalfFixed;
            int centerFixedY = _worldOffsetFixedY + (gridY * TILE_SIZE_FIXED) + CellHalfFixed;
            Span<int> searchOrder = stackalloc int[8];
            BuildNativeSearchOrderFixed(worldFixedX - centerFixedX, worldFixedY - centerFixedY, searchOrder);
            for (int orderIndex = 0; orderIndex < searchOrder.Length; orderIndex++)
            {
                int direction = searchOrder[orderIndex];
                var (dx, dy) = Directions[direction];
                PathNode candidate = GetNodeAt(gridX + dx, gridY + dy);
                if (candidate == null || !candidate.IsWalkable)
                    continue;
                if (useHeightGate && NativeAbs(candidate.HeightFixed - heightFixed) >= 0xA01)
                    continue;

                node = candidate;
                clampedFixedX = worldFixedX;
                clampedFixedY = worldFixedY;
                ClampPointToNodeFixed(node, ref clampedFixedX, ref clampedFixedY);
                return true;
            }
            return false;
        }

        private static void ClampPointToNodeFixed(PathNode node, ref int fixedX, ref int fixedY)
        {
            if (node == null)
                return;

            int minX = node.WorldFixedX - CellHalfFixed;
            int maxX = node.WorldFixedX + CellHalfFixed - 1;
            if (fixedX < minX)
                fixedX = minX;
            else if (fixedX > maxX)
                fixedX = maxX;

            int minY = node.WorldFixedY - CellHalfFixed;
            int maxY = node.WorldFixedY + CellHalfFixed - 1;
            if (fixedY < minY)
                fixedY = minY;
            else if (fixedY > maxY)
                fixedY = maxY;
        }

        private static void BuildNativeSearchOrderFixed(int localFixedX, int localFixedY, Span<int> searchOrder)
        {
            Span<long> distances = stackalloc long[8];
            searchOrder[0] = 0;
            searchOrder[1] = 1;
            searchOrder[2] = 2;
            searchOrder[3] = 3;
            searchOrder[4] = 4;
            searchOrder[5] = 5;
            searchOrder[6] = 6;
            searchOrder[7] = 7;

            long north = NativeSquareF32(CellHalfFixed - localFixedY);
            long east = NativeSquareF32(CellHalfFixed - localFixedX);
            long south = NativeSquareF32(localFixedY + CellHalfFixed);
            long west = NativeSquareF32(localFixedX + CellHalfFixed);
            distances[0] = north;
            distances[1] = north + east;
            distances[2] = east;
            distances[3] = east + south;
            distances[4] = south;
            distances[5] = south + west;
            distances[6] = west;
            distances[7] = west + north;

            for (int i = 1; i < searchOrder.Length; i++)
            {
                int direction = searchOrder[i];
                long distance = distances[direction];
                int j = i - 1;
                while (j >= 0 && distances[searchOrder[j]] > distance)
                {
                    searchOrder[j + 1] = searchOrder[j];
                    j--;
                }
                searchOrder[j + 1] = direction;
            }
        }

        private static long NativeSquareF32(int valueFixed)
        {
            return ((long)valueFixed * valueFixed) >> 8;
        }

        private static int NativeAbs(int value)
        {
            return value < 0 ? -value : value;
        }

        private static int NativeSign(int value)
        {
            if (value > 0)
                return 1;
            if (value < 0)
                return -1;
            return 0;
        }

        private static bool TryResolveNativeSlideDirectionFixed(int exitEdge, int dxFixed, int dyFixed, out int slideUnitX, out int slideUnitY)
        {
            slideUnitX = 0;
            slideUnitY = 0;
            long dx = dxFixed;
            long dy = dyFixed;

            switch (exitEdge)
            {
                case 0:
                case 4:
                    if (dx > 0) slideUnitX = FixedScale;
                    else if (dx < 0) slideUnitX = -FixedScale;
                    break;
                case 1:
                    if (dx > dy) slideUnitX = FixedScale;
                    else if (dx < dy) slideUnitY = FixedScale;
                    break;
                case 2:
                case 6:
                    if (dy > 0) slideUnitY = FixedScale;
                    else if (dy < 0) slideUnitY = -FixedScale;
                    break;
                case 3:
                    long southEastCompare = dx + dy;
                    if (southEastCompare > 0) slideUnitX = FixedScale;
                    else if (southEastCompare < 0) slideUnitY = -FixedScale;
                    break;
                case 5:
                    if (dy > dx) slideUnitX = -FixedScale;
                    else if (dy < dx) slideUnitY = -FixedScale;
                    break;
                case 7:
                    long northWestCompare = dy + dx;
                    if (northWestCompare < 0) slideUnitX = -FixedScale;
                    else if (northWestCompare > 0) slideUnitY = FixedScale;
                    break;
            }

            return slideUnitX != 0 || slideUnitY != 0;
        }

        private bool CastGroundRayHitFixed(int startFixedX, int startFixedY, int endFixedX, int endFixedY,
            out int hitFixedX, out int hitFixedY, out int hitEdge, out int remainingFixed)
        {
            hitFixedX = endFixedX; hitFixedY = endFixedY; hitEdge = -1; remainingFixed = 0;
            if (!TryGetClosestWalkableNodeAtWorldFixed(
                    startFixedX,
                    startFixedY,
                    0,
                    false,
                    out PathNode node,
                    out int sx,
                    out int sy))
                return false;
            int ex = endFixedX;
            int ey = endFixedY;
            if (!TryBuildNativeRayDirectionFixed(ex - sx, ey - sy, out int dirX, out int dirY, out int totalDistFixed))
                return false;
            int maxIterations = Math.Max(_nodeGrid.Count, 1);
            for (int iter = 0; iter < maxIterations; iter++)
            {
                int nx = node.WorldFixedX;
                int ny = node.WorldFixedY;
                if (!CollideRayWithNodeFixed(nx, ny, sx, sy, dirX, dirY,
                        out int exitT, out int exitEdge) || exitEdge < 0)
                {
                    hitEdge = -1;
                    remainingFixed = 0;
                    return false;
                }
                if (exitT >= totalDistFixed) return false;
                bool blocked = (node.ConnectionFlags & (1 << exitEdge)) == 0;
                if (!blocked)
                {
                    var (ddx, ddy) = Directions[exitEdge];
                    var next = GetNodeAt(node.GridX + ddx, node.GridY + ddy);
                    if (next == null || !next.IsWalkable) blocked = true;
                    else { node = next; continue; }
                }
                hitFixedX = sx + FixedMultiply(dirX, exitT);
                hitFixedY = sy + FixedMultiply(dirY, exitT);
                hitEdge = exitEdge;
                remainingFixed = totalDistFixed - exitT;
                return true;
            }

            hitFixedX = sx;
            hitFixedY = sy;
            hitEdge = -1;
            remainingFixed = totalDistFixed;
            return true;
        }

        private bool CastGroundRayHitHeightFixed(int startFixedX, int startFixedY, int endFixedX, int endFixedY,
            out int hitFixedX, out int hitFixedY, out int hitHeightFixed, out int hitEdge, out int remainingFixed)
        {
            int startHeightFixed = ResolveGroundRayNodeHeightFixed(startFixedX, startFixedY, 0);
            hitFixedX = endFixedX; hitFixedY = endFixedY; hitHeightFixed = ResolveGroundRayNodeHeightFixed(endFixedX, endFixedY, startHeightFixed); hitEdge = -1; remainingFixed = 0;
            if (!TryGetClosestWalkableNodeAtWorldFixed(
                    startFixedX,
                    startFixedY,
                    startHeightFixed,
                    true,
                    out PathNode node,
                    out int sx,
                    out int sy))
                return false;
            int ex = endFixedX;
            int ey = endFixedY;
            if (!TryBuildNativeRayDirectionFixed(ex - sx, ey - sy, out int dirX, out int dirY, out int totalDistFixed))
            {
                hitFixedX = startFixedX;
                hitFixedY = startFixedY;
                hitHeightFixed = startHeightFixed;
                return false;
            }
            int maxIterations = Math.Max(_nodeGrid.Count, 1);
            for (int iter = 0; iter < maxIterations; iter++)
            {
                int nx = node.WorldFixedX;
                int ny = node.WorldFixedY;
                if (!CollideRayWithNodeFixed(nx, ny, sx, sy, dirX, dirY,
                        out int exitT, out int exitEdge) || exitEdge < 0)
                {
                    hitEdge = -1;
                    remainingFixed = 0;
                    return false;
                }
                if (exitT >= totalDistFixed)
                {
                    hitFixedX = sx + FixedMultiply(dirX, totalDistFixed);
                    hitFixedY = sy + FixedMultiply(dirY, totalDistFixed);
                    hitHeightFixed = node.HeightFixed;
                    return false;
                }
                bool blocked = (node.ConnectionFlags & (1 << exitEdge)) == 0;
                if (!blocked)
                {
                    var (ddx, ddy) = Directions[exitEdge];
                    var next = GetNodeAt(node.GridX + ddx, node.GridY + ddy);
                    if (next == null || !next.IsWalkable) blocked = true;
                    else { node = next; continue; }
                }
                hitFixedX = sx + FixedMultiply(dirX, exitT);
                hitFixedY = sy + FixedMultiply(dirY, exitT);
                hitHeightFixed = node.HeightFixed;
                hitEdge = exitEdge;
                remainingFixed = totalDistFixed - exitT;
                return true;
            }

            hitFixedX = sx;
            hitFixedY = sy;
            hitHeightFixed = node.HeightFixed;
            hitEdge = -1;
            remainingFixed = totalDistFixed;
            return true;
        }

        private const int CellHalfFixed = 0x500;
        private const int FixedScale = 0x100;

        public static bool TryBuildNativeRayDirectionFixed(int dxFixed, int dyFixed, out int dirXFixed, out int dirYFixed, out int lengthFixed)
        {
            dirXFixed = 0;
            dirYFixed = 0;
            lengthFixed = DungeonRunners.Combat.UnitMover.TableSquareRoot(NativeVectorLengthSquaredFixed(dxFixed, dyFixed));
            if (lengthFixed <= 0)
                return false;

            dirXFixed = FixedDivide(dxFixed, lengthFixed);
            dirYFixed = FixedDivide(dyFixed, lengthFixed);
            return dirXFixed != 0 || dirYFixed != 0;
        }

        private static uint NativeVectorLengthSquaredFixed(int dxFixed, int dyFixed)
        {
            return unchecked((uint)((((long)dxFixed * dxFixed) >> 8) + (((long)dyFixed * dyFixed) >> 8)));
        }

        private static int FixedDivide(int valueFixed, int divisorFixed)
        {
            return (int)(((long)valueFixed << 8) / divisorFixed);
        }

        private static int FixedDivideRawFixed(int valueFixed, int divisorFixed)
        {
            if (divisorFixed == 0)
                return 0;
            return (int)(((long)valueFixed << 8) / divisorFixed);
        }

        private static int FixedMultiply(int valueFixed, int factorFixed)
        {
            return (int)(((long)valueFixed * factorFixed) >> 8);
        }

        private static bool CollideRayWithNodeFixed(int nxFixed, int nyFixed, int sxFixed, int syFixed, int dirXFixed, int dirYFixed,
            out int exitDistFixed, out int exitEdge)
        {
            int tNear = -0x80000000;
            exitDistFixed = 0x7fffffff;
            exitEdge = -1;
            int entryEdge = -1;

            if (dirXFixed == 0)
            {
                if (sxFixed < nxFixed - CellHalfFixed) return false;
                if (nxFixed + CellHalfFixed < sxFixed) return false;
            }
            else
            {
                int t1 = (int)((((long)(nxFixed - sxFixed - CellHalfFixed)) << 8) / dirXFixed);
                int t2 = (int)((((long)(nxFixed - sxFixed + CellHalfFixed)) << 8) / dirXFixed);
                int near = t1, far = t2, nearEdge = 6, farEdge = 2;
                if (t2 < t1) { near = t2; far = t1; nearEdge = 2; farEdge = 6; }
                tNear = near;
                exitDistFixed = far;
                entryEdge = nearEdge;
                exitEdge = farEdge;
            }

            if (dirYFixed == 0)
            {
                if (syFixed < nyFixed - CellHalfFixed) return false;
                if (nyFixed + CellHalfFixed < syFixed) return false;
            }
            else
            {
                int t1 = (int)((((long)(nyFixed - syFixed - CellHalfFixed)) << 8) / dirYFixed);
                int t2 = (int)((((long)(nyFixed - syFixed + CellHalfFixed)) << 8) / dirYFixed);
                int near = t1, far = t2, nearEdge = 4, farEdge = 0;
                if (t2 < t1) { near = t2; far = t1; nearEdge = 0; farEdge = 4; }
                if (tNear < near) { tNear = near; entryEdge = nearEdge; }
                else if (near == tNear)
                {
                    if (nearEdge == 0 && entryEdge == 6) entryEdge = 7;
                    else entryEdge = (entryEdge + nearEdge) / 2;
                }
                if (far < exitDistFixed) { exitDistFixed = far; exitEdge = farEdge; }
                else if (far == exitDistFixed)
                {
                    if (farEdge == 0 && exitEdge == 6) exitEdge = 7;
                    else exitEdge = (exitEdge + farEdge) / 2;
                }
            }

            return exitDistFixed >= 0 && tNear <= exitDistFixed;
        }

        private void SetFixedBounds(int worldOffsetFixedX, int worldOffsetFixedY, int minWorldFixedX, int maxWorldFixedX, int minWorldFixedY, int maxWorldFixedY)
        {
            _worldOffsetFixedX = worldOffsetFixedX;
            _worldOffsetFixedY = worldOffsetFixedY;
            _minWorldFixedX = minWorldFixedX;
            _maxWorldFixedX = maxWorldFixedX;
            _minWorldFixedY = minWorldFixedY;
            _maxWorldFixedY = maxWorldFixedY;
        }

        private static int GetSqliteFixed8(SqliteDataReader reader, string column)
        {
            return DungeonRunners.Database.GameDatabase.GetFixed8(reader, column);
        }

        private static int NativeWorldToGridDiv(int worldFixed, int worldOffsetFixed)
        {
            return ((worldFixed >> 8) - (worldOffsetFixed >> 8)) / 10;
        }

        private static int ComputeGridDimension(int minimumFixed, int maximumFixed)
        {
            long extent = (long)maximumFixed - minimumFixed;
            if (extent < 0)
                return 0;
            long dimension = (extent + TILE_SIZE_FIXED - 1) / TILE_SIZE_FIXED + 1;
            return dimension <= int.MaxValue ? (int)dimension : 0;
        }
    }
}
