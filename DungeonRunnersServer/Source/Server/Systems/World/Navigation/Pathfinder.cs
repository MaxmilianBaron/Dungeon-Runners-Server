using System;
using System.Collections.Generic;

namespace DungeonRunners.Core
{
    public sealed class Pathfinder
    {

        private const bool ApplyDirectionGate = false;


        private readonly PathMap _pathMap;

        private PathNode _startPathMapNode;
        private PathNode _goalPathMapNode;
        private int _startFixedX;
        private int _startFixedY;
        private int _goalFixedX;
        private int _goalFixedY;

        private readonly SortedSet<Node> _openSet = new SortedSet<Node>(NodeComparer.Instance);
        private readonly Dictionary<PathNode, Node> _openByPathMapNode = new Dictionary<PathNode, Node>();
        private readonly Dictionary<PathNode, Node> _closedByPathMapNode = new Dictionary<PathNode, Node>();

        private Node _bestNode;
        private bool _done;
        private bool _directReach;
        private int _nodesExpanded;
        private long _nextNodeSequence;

        public bool IsDone => _done || _openSet.Count == 0;
        public bool DirectReach => _directReach;
        public bool ReachedGoal => _bestNode != null && _bestNode.PathMapNode == _goalPathMapNode;
        public int NodesExpanded => _nodesExpanded;
        public int StartFixedX => _startFixedX;
        public int StartFixedY => _startFixedY;
        public int GoalFixedX => _goalFixedX;
        public int GoalFixedY => _goalFixedY;

        public Pathfinder(PathMap pathMap)
        {
            _pathMap = pathMap ?? throw new ArgumentNullException(nameof(pathMap));
        }


        public void RequestPath(PathNode startPathMapNode, PathNode goalPathMapNode)
        {
            if (startPathMapNode == null) throw new ArgumentNullException(nameof(startPathMapNode));
            if (goalPathMapNode == null) throw new ArgumentNullException(nameof(goalPathMapNode));
            RequestPath(
                startPathMapNode.WorldFixedX,
                startPathMapNode.WorldFixedY,
                startPathMapNode,
                goalPathMapNode.WorldFixedX,
                goalPathMapNode.WorldFixedY,
                goalPathMapNode);
        }


        public void RequestPath(
            int startFixedX,
            int startFixedY,
            PathNode startPathMapNode,
            int goalFixedX,
            int goalFixedY,
            PathNode goalPathMapNode)
        {
            if (startPathMapNode == null) throw new ArgumentNullException(nameof(startPathMapNode));
            if (goalPathMapNode == null) throw new ArgumentNullException(nameof(goalPathMapNode));

            Reset();

            _startPathMapNode = startPathMapNode;
            _goalPathMapNode = goalPathMapNode;
            _startFixedX = startFixedX;
            _startFixedY = startFixedY;
            _goalFixedX = goalFixedX;
            _goalFixedY = goalFixedY;

            if (startPathMapNode != goalPathMapNode)
            {
                if (_pathMap.CanReachPointFixed(
                    startFixedX,
                    startFixedY,
                    goalFixedX,
                    goalFixedY))
                {
                    _directReach = true;
                    _done = true;
                    return;
                }
            }
            else
            {
                _directReach = true;
                _done = true;
                return;
            }

            var startNode = new Node
            {
                PathMapNode = startPathMapNode,
                G = 0,
                H = ManhattanHeuristic(startPathMapNode, goalFixedX, goalFixedY),
                Parent = null,
                Sequence = _nextNodeSequence++,
            };
            startNode.F = startNode.G + startNode.H;
            _openSet.Add(startNode);
            _openByPathMapNode[startPathMapNode] = startNode;
            _bestNode = startNode;
        }


        public int UpdateRequest(int maxSteps)
        {
            if (_done) return 0;

            int expanded = 0;
            while (expanded < maxSteps)
            {
                if (_openSet.Count == 0)
                {
                    _done = true;
                    break;
                }

                Node current = _openSet.Min;
                _openSet.Remove(current);
                _openByPathMapNode.Remove(current.PathMapNode);
                _closedByPathMapNode[current.PathMapNode] = current;

                if (_bestNode == null || current.H < _bestNode.H)
                    _bestNode = current;

                if (current.PathMapNode == _goalPathMapNode)
                {
                    _bestNode = current;
                    _done = true;
                    break;
                }

                ExpandNeighbors(current);
                expanded++;
            }

            _nodesExpanded += expanded;
            return expanded;
        }

        private void ExpandNeighbors(Node current)
        {
            byte connectionFlags = current.PathMapNode.ConnectionFlags;
            int cx = current.PathMapNode.GridX;
            int cy = current.PathMapNode.GridY;

            for (int dirIndex = 0; dirIndex < 8; dirIndex++)
            {
                if ((connectionFlags & (1 << dirIndex)) == 0) continue;

                var (dx, dy) = PathMap.Directions[dirIndex];
                PathNode neighbor = _pathMap.GetNodeAt(cx + dx, cy + dy);
                if (neighbor == null) continue;
                if (!neighbor.IsWalkable) continue;

                int stepCost = PathMap.DirectionCosts[dirIndex];
                int newG = current.G + stepCost;

                if (_closedByPathMapNode.TryGetValue(neighbor, out var closedNode) && closedNode.G <= newG)
                    continue;

                if (_openByPathMapNode.TryGetValue(neighbor, out var openNode) && openNode.G <= newG)
                    continue;

                if (openNode != null)
                {
                    _openSet.Remove(openNode);
                    _openByPathMapNode.Remove(neighbor);
                }
                if (closedNode != null)
                    _closedByPathMapNode.Remove(neighbor);

                int h = ManhattanHeuristic(neighbor, _goalFixedX, _goalFixedY);
                var newNode = new Node
                {
                    PathMapNode = neighbor,
                    G = newG,
                    H = h,
                    F = newG + h,
                    Parent = current,
                    Sequence = _nextNodeSequence++,
                };
                _openSet.Add(newNode);
                _openByPathMapNode[neighbor] = newNode;
            }
        }


        public bool GetPath(out List<PathNode> waypoints)
        {
            waypoints = new List<PathNode>();

            if (_directReach)
            {
                if (_startPathMapNode == null || _goalPathMapNode == null) return false;
                waypoints.Add(_startPathMapNode);
                waypoints.Add(_goalPathMapNode);
                return true;
            }

            Node tail = _bestNode;

            if (tail == null) return false;

            var reversed = new List<PathNode>();
            int prevDir = -1;
            Node node = tail;
            while (node != null)
            {
                if (node.Parent != null)
                {
                    int dir = PathMap.GetDirFromAToB(node.Parent.PathMapNode, node.PathMapNode);
                    if (prevDir == -1 || dir != prevDir)
                    {
                        reversed.Add(node.PathMapNode);
                    }
                    prevDir = dir;
                }
                else
                {
                    reversed.Add(node.PathMapNode);
                }
                node = node.Parent;
            }

            for (int reverseIndex = reversed.Count - 1; reverseIndex >= 0; reverseIndex--)
                waypoints.Add(reversed[reverseIndex]);

            return waypoints.Count > 0;
        }


        public bool GetPathFixed(out List<(int FixedX, int FixedY)> waypoints)
        {
            waypoints = new List<(int FixedX, int FixedY)>();
            if (_directReach)
            {
                if (_startPathMapNode == null || _goalPathMapNode == null)
                    return false;
                waypoints.Add((_startFixedX, _startFixedY));
                waypoints.Add((_goalFixedX, _goalFixedY));
                return true;
            }

            Node tail = _bestNode;
            if (tail == null)
                return false;

            var reversed = new List<(int FixedX, int FixedY)>();
            if (tail.PathMapNode == _goalPathMapNode)
                reversed.Add((_goalFixedX, _goalFixedY));

            int previousDirection = -1;
            Node node = tail;
            while (node != null)
            {
                bool addNode = node.Parent == null;
                if (node.Parent != null)
                {
                    int direction = PathMap.GetDirFromAToB(node.Parent.PathMapNode, node.PathMapNode);
                    addNode = previousDirection == -1 || direction != previousDirection;
                    previousDirection = direction;
                }

                if (addNode)
                    reversed.Add((node.PathMapNode.WorldFixedX, node.PathMapNode.WorldFixedY));
                node = node.Parent;
            }

            for (int reverseIndex = reversed.Count - 1; reverseIndex >= 0; reverseIndex--)
                waypoints.Add(reversed[reverseIndex]);
            return waypoints.Count > 0;
        }


        private static int ManhattanHeuristic(PathNode node, int goalFixedX, int goalFixedY)
        {
            int dx = node.WorldFixedX - goalFixedX;
            int dy = node.WorldFixedY - goalFixedY;
            if (dx < 0) dx = -dx;
            if (dy < 0) dy = -dy;
            return (dx + dy) >> 8;
        }

        private void Reset()
        {
            _openSet.Clear();
            _openByPathMapNode.Clear();
            _closedByPathMapNode.Clear();
            _bestNode = null;
            _done = false;
            _directReach = false;
            _nodesExpanded = 0;
            _startPathMapNode = null;
            _goalPathMapNode = null;
            _startFixedX = 0;
            _startFixedY = 0;
            _goalFixedX = 0;
            _goalFixedY = 0;
            _nextNodeSequence = 0;
        }

        private sealed class Node
        {
            public PathNode PathMapNode;
            public int G;
            public int H;
            public int F;
            public Node Parent;
            public long Sequence;
        }

        private sealed class NodeComparer : IComparer<Node>
        {
            public static readonly NodeComparer Instance = new NodeComparer();
            public int Compare(Node x, Node y)
            {
                if (ReferenceEquals(x, y)) return 0;
                int c = x.F.CompareTo(y.F);
                if (c != 0) return c;
                return x.Sequence.CompareTo(y.Sequence);
            }
        }
    }
}
