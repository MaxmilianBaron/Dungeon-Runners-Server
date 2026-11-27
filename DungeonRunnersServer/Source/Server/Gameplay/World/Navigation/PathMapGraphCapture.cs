using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DungeonRunners.Combat;
using DungeonRunners.Engine;

namespace DungeonRunners.Core
{
    internal sealed class PathMapGraphCaptureRequest
    {
        public string Schema { get; set; }
        public string CaptureId { get; set; }
        public string Instance { get; set; }
        public uint? LayoutSeed { get; set; }
    }

    internal sealed class PathMapGraphConnection
    {
        public int Direction { get; set; }
        public int DeltaX { get; set; }
        public int DeltaY { get; set; }
        public int TargetLocalGridX { get; set; }
        public int TargetLocalGridY { get; set; }
        public int TargetWorldGridX { get; set; }
        public int TargetWorldGridY { get; set; }
        public string TargetState { get; set; }
        public bool Reciprocal { get; set; }
    }

    internal static class PathMapGraphCapture
    {
        private const string RequestFileName = "pathmap-parity.request";
        private const string OutputFileName = "pathmap-parity-server.jsonl";
        private const string TemporaryFileName = "pathmap-parity-server.jsonl.tmp";
        private const string Schema = "dr-pathmap-graph-v2";
        private const int MaxRequestBytes = 4096;
        private static readonly object Gate = new object();
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        public static void TryCapture(string instance, uint layoutSeed, uint roomSeed, PathMap pathMap)
        {
            if (pathMap == null || string.IsNullOrWhiteSpace(instance))
                return;

            string logDirectory = DataPaths.ServerPath("logs");
            string requestPath = Path.Combine(logDirectory, RequestFileName);
            if (!File.Exists(requestPath))
                return;

            lock (Gate)
            {
                if (!TryClaimRequest(requestPath, instance, layoutSeed, out PathMapGraphCaptureRequest request))
                    return;

                string outputPath = Path.Combine(logDirectory, OutputFileName);
                string temporaryPath = Path.Combine(logDirectory, TemporaryFileName);
                DeleteIfPresent(outputPath);
                DeleteIfPresent(temporaryPath);
                WriteGraph(
                    request.CaptureId,
                    instance,
                    layoutSeed,
                    roomSeed,
                    pathMap,
                    outputPath,
                    temporaryPath);
            }

        }

        private static bool TryClaimRequest(
            string requestPath,
            string instance,
            uint layoutSeed,
            out PathMapGraphCaptureRequest request)
        {
            request = null;
            try
            {
                long requestLength = new FileInfo(requestPath).Length;
                if (requestLength <= 0 || requestLength > MaxRequestBytes)
                {
                    File.Delete(requestPath);
                    Debug.LogError($"[PATHMAP-GRAPH] instance='{instance}' state=request-rejected bytes={requestLength}");
                    return false;
                }
                string json = File.ReadAllText(requestPath);
                request = JsonSerializer.Deserialize<PathMapGraphCaptureRequest>(json, JsonOptions);
                if (request == null || !string.Equals(request.Schema, "dr-server-pathmap-graph-request-v1", StringComparison.Ordinal))
                    return false;
                if (!Guid.TryParseExact(request.CaptureId, "D", out _))
                    return false;
                if (request.Instance != null && request.Instance.Length > 256)
                    return false;
                if (!string.IsNullOrWhiteSpace(request.Instance)
                    && !string.Equals(request.Instance, instance, StringComparison.OrdinalIgnoreCase))
                    return false;
                if (request.LayoutSeed.HasValue && request.LayoutSeed.Value != layoutSeed)
                    return false;
                File.Delete(requestPath);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PATHMAP-GRAPH] instance='{instance}' state=request-error message='{ex.Message}'");
                return false;
            }
        }

        private static void WriteGraph(
            string captureId,
            string instance,
            uint layoutSeed,
            uint roomSeed,
            PathMap pathMap,
            string outputPath,
            string temporaryPath)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
                int width = pathMap.GridWidth;
                int height = pathMap.GridHeight;
                if (width <= 0 || height <= 0 || (long)width * height > 262144)
                    throw new InvalidDataException($"Invalid path map dimensions {width}x{height}");
                if (!pathMap.HasNativeGraphState)
                    throw new InvalidDataException("Native path map graph state is unavailable");

                int originGridX = pathMap.WorldOffsetFixedX / 0x0A00;
                int originGridY = pathMap.WorldOffsetFixedY / 0x0A00;
                byte[] spaceParents = pathMap.CopyNativeSpaceParents();
                int nodeCount = 0;
                int connectedNodeCount = 0;
                int edgeCount = 0;
                int asymmetricEdgeCount = 0;
                uint captureTick = SimulationClock.SimulationTick;
                using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
                {
                    WriteLine(writer, new
                    {
                        schema = Schema,
                        kind = "header",
                        role = "server",
                        captureId,
                        captureTick,
                        instance,
                        layoutSeed,
                        roomSeed,
                        originGridX,
                        originGridY,
                        width,
                        height,
                        cellCount = (long)width * height,
                        nodeCountHint = pathMap.NodeCount,
                        connectedNodeCountHint = pathMap.NativeConnectedNodeCount,
                        spaceIdGeneration = pathMap.NativeSpaceIdGeneration,
                        spaceParents,
                        minWorldFixedX = pathMap.MinWorldFixedX,
                        minWorldFixedY = pathMap.MinWorldFixedY,
                        maxWorldFixedX = pathMap.MaxWorldFixedX,
                        maxWorldFixedY = pathMap.MaxWorldFixedY,
                        directionTable = DirectionTable()
                    });

                    for (int localGridY = 0; localGridY < height; localGridY++)
                    {
                        for (int localGridX = 0; localGridX < width; localGridX++)
                        {
                            int ordinal = localGridY * width + localGridX;
                            int worldGridX = originGridX + localGridX;
                            int worldGridY = originGridY + localGridY;
                            PathNode node = pathMap.GetNodeAt(localGridX, localGridY);
                            if (node == null)
                            {
                                WriteLine(writer, new
                                {
                                    schema = Schema,
                                    kind = "cell",
                                    role = "server",
                                    ordinal,
                                    localGridX,
                                    localGridY,
                                    worldGridX,
                                    worldGridY,
                                    state = "missing-node"
                                });
                                continue;
                            }

                            nodeCount++;
                            if (node.SolidFlag < 0xFE)
                                connectedNodeCount++;
                            List<PathMapGraphConnection> connections = BuildConnections(
                                pathMap,
                                node,
                                originGridX,
                                originGridY,
                                ref edgeCount,
                                ref asymmetricEdgeCount);
                            WriteLine(writer, new
                            {
                                schema = Schema,
                                kind = "cell",
                                role = "server",
                                ordinal,
                                localGridX,
                                localGridY,
                                worldGridX,
                                worldGridY,
                                state = "node",
                                storedGridX = node.GridX,
                                storedGridY = node.GridY,
                                worldFixedX = node.WorldFixedX,
                                worldFixedY = node.WorldFixedY,
                                heightFixed = node.HeightFixed,
                                connectionFlags = node.ConnectionFlags,
                                spaceOrValidity = node.SolidFlag,
                                visitedDirectionFlags = node.VisitedDirectionFlags,
                                connections
                            });
                        }
                    }

                    WriteLine(writer, new
                    {
                        schema = Schema,
                        kind = "complete",
                        role = "server",
                        captureId,
                        nodeCount,
                        connectedNodeCount,
                        missingNodeCount = (long)width * height - nodeCount,
                        edgeCount,
                        asymmetricEdgeCount,
                        completedTick = SimulationClock.SimulationTick
                    });
                }

                File.Move(temporaryPath, outputPath, true);
                Debug.LogError($"[PATHMAP-GRAPH] instance='{instance}' layoutSeed=0x{layoutSeed:X8} nodes={nodeCount} edges={edgeCount} asymmetric={asymmetricEdgeCount} state=complete");
            }
            catch (Exception ex)
            {
                DeleteIfPresent(temporaryPath);
                Debug.LogError($"[PATHMAP-GRAPH] instance='{instance}' layoutSeed=0x{layoutSeed:X8} state=failed message='{ex.Message}'");
            }
        }

        private static List<PathMapGraphConnection> BuildConnections(
            PathMap pathMap,
            PathNode node,
            int originGridX,
            int originGridY,
            ref int edgeCount,
            ref int asymmetricEdgeCount)
        {
            var connections = new List<PathMapGraphConnection>(8);
            for (int direction = 0; direction < PathMap.Directions.Length; direction++)
            {
                if ((node.ConnectionFlags & (1 << direction)) == 0)
                    continue;
                var (deltaX, deltaY) = PathMap.Directions[direction];
                int targetGridX = node.GridX + deltaX;
                int targetGridY = node.GridY + deltaY;
                PathNode target = pathMap.GetNodeAt(targetGridX, targetGridY);
                int opposite = (direction + 4) & 7;
                bool reciprocal = target != null && (target.ConnectionFlags & (1 << opposite)) != 0;
                edgeCount++;
                if (!reciprocal)
                    asymmetricEdgeCount++;
                connections.Add(new PathMapGraphConnection
                {
                    Direction = direction,
                    DeltaX = deltaX,
                    DeltaY = deltaY,
                    TargetLocalGridX = targetGridX,
                    TargetLocalGridY = targetGridY,
                    TargetWorldGridX = originGridX + targetGridX,
                    TargetWorldGridY = originGridY + targetGridY,
                    TargetState = target == null ? "missing-node" : "node",
                    Reciprocal = reciprocal
                });
            }
            return connections;
        }

        private static object[] DirectionTable()
        {
            var rows = new object[PathMap.Directions.Length];
            for (int direction = 0; direction < PathMap.Directions.Length; direction++)
            {
                var (deltaX, deltaY) = PathMap.Directions[direction];
                rows[direction] = new
                {
                    direction,
                    deltaX,
                    deltaY,
                    opposite = (direction + 4) & 7
                };
            }
            return rows;
        }

        private static void WriteLine(StreamWriter writer, object value)
        {
            writer.WriteLine(JsonSerializer.Serialize(value, value.GetType(), JsonOptions));
        }

        private static void DeleteIfPresent(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }
    }
}
