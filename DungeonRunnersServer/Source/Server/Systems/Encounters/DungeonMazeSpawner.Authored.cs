using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DungeonRunners.Combat;
using DungeonRunners.Core;
using DungeonRunners.Data;
using DungeonRunners.Utilities;
using CombatRandom = DungeonRunners.Combat.MersenneTwister;

namespace DungeonRunners.Gameplay
{
    public static partial class DungeonMazeSpawner
    {
        private static readonly object DefinitionsLock = new();
        private static Dictionary<string, LevelDef> AuthoredLevels;
        private static readonly Dictionary<string, EncounterTableManifest> AuthoredTables = new(StringComparer.OrdinalIgnoreCase);

        private static EncounterTableManifest GetEncounterTable(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            lock (DefinitionsLock)
            {
                if (AuthoredTables.TryGetValue(path, out var table)) return table;
                GCNode root = GCDatabase.Instance.Resolve(path);
                if (root == null || !AuthoredWorldLayout.IsKindOf(root, "EncounterTable"))
                    throw new InvalidDataException($"Authored encounter table is unresolved '{path}'");
                table = new EncounterTableManifest(path, root.PackageEntryId);
                AuthoredTables.Add(path, table);
                return table;
            }
        }

        private static Dictionary<string, LevelDef> GetAuthoredLevelDefs()
        {
            lock (DefinitionsLock)
            {
                if (AuthoredLevels != null) return AuthoredLevels;
                var levels = new Dictionary<string, LevelDef>(StringComparer.OrdinalIgnoreCase);
                foreach (var world in AuthoredWorldLayout.Definitions)
                {
                    if (!world.Generated) continue;
                    GCNode root = world.Root;
                    var rooms = new List<RoomNodeDef>();
                    foreach (GCNode node in world.Rooms)
                    {
                        int x = node.GetInt("GridX", -1), y = node.GetInt("GridY", -1);
                        rooms.Add(new RoomNodeDef
                        {
                            AuthoredNode = node,
                            TileSet = node.GetString("TileSet", ""),
                            GridX = x < 0 ? null : x,
                            GridY = y < 0 ? null : y,
                            Chance = node.GetInt("Chance", 100),
                            RequiredQuest = node.GetString("RequiredQuest", ""),
                            WaypointName = node.GetString("WaypointName", ""),
                            ZoneStart = node.GetBool("ZoneStart", false),
                            EncounterTable = GetEncounterTable(node.GetString("EncounterType", "")),
                            LinkToZone = node.GetString("LinkToZone", ""),
                            LinkToSpawn = node.GetString("LinkToSpawn", ""),
                            SpawnName = node.GetString("SpawnName", "")
                        });
                    }
                    int entry = rooms.FindIndex(node => node.ZoneStart);
                    if (entry < 0) entry = rooms.FindIndex(node => !string.IsNullOrEmpty(node.SpawnName));
                    if (entry < 0) entry = 0;
                    int exit = rooms.FindIndex(node => !node.ZoneStart && !string.IsNullOrEmpty(node.LinkToZone));
                    if (exit < 0) exit = entry;
                    var level = new LevelDef
                    {
                        AuthoredNode = root,
                        MazeWidth = root.GetInt("MazeWidth", 5), MazeHeight = root.GetInt("MazeHeight", 5),
                        MazeRandomness = root.GetInt("MazeRandomness", 90), MazeSparseness = root.GetInt("MazeSparseness", 5),
                        MazeDeadEndRemovalChance = root.GetInt("MazeDeadEndRemovalChance", 10),
                        TileSize = root.GetInt("TileSize", 400), TileSetPrefix = root.GetString("TileSet", ""),
                        EncounterTable = GetEncounterTable(root.GetString("EncounterTable", "")),
                        RoomNodes = rooms.ToArray(), EntrySourceIndex = entry, ExitSourceIndex = exit,
                        EntryGridX = rooms.Count > entry ? rooms[entry].GridX ?? -1 : -1,
                        EntryGridY = rooms.Count > entry ? rooms[entry].GridY ?? -1 : -1,
                        ExitGridX = rooms.Count > exit ? rooms[exit].GridX ?? -1 : -1,
                        ExitGridY = rooms.Count > exit ? rooms[exit].GridY ?? -1 : -1
                    };
                    if (level.MazeWidth <= 0 || level.MazeWidth > 64 || level.MazeHeight <= 0 || level.MazeHeight > 64 || level.TileSize <= 0)
                        throw new InvalidDataException($"Authored maze dimensions invalid zone='{world.Name}'");
                    levels.Add(world.Name, level);
                }
                AuthoredLevels = levels;
                return levels;
            }
        }

        private static string FindTilePortalType(string tile)
        {
            return AuthoredWorldLayout.TileEntities(tile).FirstOrDefault(node => AuthoredWorldLayout.IsKindOf(node, "ZonePortal"))?.Extends;
        }

        public static int ResolveFallbackGroupCount(uint seed) => 2 + (int)(seed % 3);

        private static bool IsNormalEncounterUnit(SpawnUnit unit)
        {
            string tier = AuthoredGameplayCatalog.FindCreature(unit.GcType)?.creatureDifficulty?.ToUpperInvariant();
            if (tier is not ("FODDER" or "RECRUIT" or "VETERAN" or "WARMONGER" or "MINION")) return false;
            GCNode description = GCDatabase.Instance.ResolveWithInheritance(unit.AuthoredType)?.GetChild("Description");
            if (description == null) return false;
            int difficulty = description.GetFixed32("Difficulty", 0x100);
            return difficulty > 0 && difficulty < 0x300;
        }

        public static bool HasStaticEncounterData(string zone)
        {
            var world = AuthoredWorldLayout.FindWorld(NormalizeBaseZone(zone));
            return world != null && !world.Generated && world.Entities.Any(node =>
                AuthoredWorldLayout.IsKindOf(node, "EncounterObject") || AuthoredWorldLayout.IsKindOf(node, "RoomPlaceholderEntity"));
        }

        public static List<DungeonSpawnData> GenerateAuthoredStaticSpawns(string zone, uint seed)
            => GenerateStaticSnapshot(zone, seed)?.Spawns;

        public static ProceduralDungeonSnapshot GenerateStaticSnapshot(string zone, uint seed, string instanceKey = null, byte generatorLevel = 1)
        {
            zone = NormalizeBaseZone(zone);
            var world = AuthoredWorldLayout.FindWorld(zone);
            var snapshot = new ProceduralDungeonSnapshot { ZoneName = zone, LayoutSeed = seed, GeneratorLevel = generatorLevel, PathMap = PathMapCatalog.Instance.GetPathMap(zone) };
            if (world == null) return snapshot;
            ReservePlacements(snapshot, world.Entities, 0, 0);
            PopulatePlacements(snapshot, world.Root, null, null, world.Entities, new CombatRandom(seed));
            return snapshot;
        }

        private static void PopulateAuthoredSpawns(ProceduralDungeonSnapshot snapshot, LevelDef level, CombatRandom rng)
        {
            foreach (MazeGenerator.MazeCell tile in snapshot.WorldCells)
                if (!tile.IsPadding) ReservePlacements(snapshot, AuthoredWorldLayout.TileEntities(tile.TileType), tile.WorldOriginFixedX, tile.WorldOriginFixedY);
            var world = AuthoredWorldLayout.FindWorld(snapshot.ZoneName);
            if (world != null) ReservePlacements(snapshot, world.Entities, 0, 0);
            foreach (MazeGenerator.MazeCell cell in snapshot.WorldCells)
            {
                if (cell.IsPadding) continue;
                var placed = snapshot.RoomNodes.FirstOrDefault(node => node.GridX == cell.GridX && node.GridY == cell.GridY);
                RoomNodeDef room = placed != null ? GetRoomNodeDef(level, placed.SourceIndex) : null;
                List<GCNode> placements = AuthoredWorldLayout.TileEntities(cell.TileType);
                PopulatePlacements(snapshot, level.AuthoredNode, room, cell, placements, rng);
                if (room == null && !placements.Any(node => AuthoredWorldLayout.IsKindOf(node, "EncounterObject")))
                    AddFallbackEncounter(snapshot, level.EncounterTable, cell, rng);
            }
            if (world != null && world.Entities.Count > 0)
                PopulatePlacements(snapshot, level.AuthoredNode, null, null, world.Entities, rng);
        }

        private static void AddFallbackEncounter(ProceduralDungeonSnapshot snapshot, EncounterTableManifest table, MazeGenerator.MazeCell cell, CombatRandom rng)
        {
            if (table == null) return;
            int choice = NextTableIndex(rng, table, "DungeonMazeSpawner::fallback-choice", cell.TileType);
            if (choice < 0) return;
            SpawnUnit[] pool = table[choice];
            if (pool.Length == 0 || pool.Any(unit => !IsNormalEncounterUnit(unit))) return;
            string key = $"{snapshot.ZoneName}:cell:{cell.GridX}:{cell.GridY}:fallback";
            uint localSeed = unchecked((uint)StableSpotSeed(key)) ^ snapshot.LayoutSeed;
            var units = new SpawnUnit[ResolveFallbackGroupCount(localSeed)];
            for (int i = 0; i < units.Length; i++) units[i] = pool[i % pool.Length];
            int size = Math.Min(cell.TileSizeFixed / 2, 150 * 256);
            var marker = new EncounterMarker(cell.WorldCenterFixedX, cell.WorldCenterFixedY, 0, 0, size, size, "local fallback:" + cell.TileType);
            AddAuthoredEncounter(snapshot, table, marker, key, cell, cell.TileSizeFixed / 2, cell.TileSizeFixed / 2, 0, rng, units, choice);
        }

        private static void ReservePlacements(ProceduralDungeonSnapshot snapshot, List<GCNode> entities, int ox, int oy)
        {
            foreach (GCNode entity in entities)
            {
                if (!AuthoredWorldLayout.TryPosition(entity, out int x, out int y, out _)) continue;
                if (AuthoredWorldLayout.IsKindOf(entity, "EncounterObject"))
                {
                    snapshot.EncounterRegions.Add((ox + x, oy + y, entity.GetFixed32("SizeX", 50 * 256) / 2, entity.GetFixed32("SizeY", 50 * 256) / 2));
                    continue;
                }
                bool portal = AuthoredWorldLayout.IsKindOf(entity, "ZonePortal");
                bool entry = AuthoredWorldLayout.IsKindOf(entity, "Waypoint") && (entity.GetString("Name", "").Equals("SpawnPoint", StringComparison.OrdinalIgnoreCase)
                    || entity.GetString("Name", "").Equals("Start", StringComparison.OrdinalIgnoreCase));
                int radius = portal || entry ? 20 * 256 : ResolveSpawnRadius(entity.Extends);
                snapshot.ReservedSpaces.Add((ox + x, oy + y, radius));
            }
        }

        private static void PopulatePlacements(ProceduralDungeonSnapshot snapshot, GCNode world, RoomNodeDef room,
            MazeGenerator.MazeCell cell, List<GCNode> entities, CombatRandom rng)
        {
            int ox = cell?.WorldOriginFixedX ?? 0, oy = cell?.WorldOriginFixedY ?? 0;
            int firstEncounter = entities.FindIndex(node => AuthoredWorldLayout.IsKindOf(node, "EncounterObject"));
            string groupRoot = cell == null ? $"{snapshot.ZoneName}:world" : $"{snapshot.ZoneName}:cell:{cell.GridX}:{cell.GridY}";
            for (int i = 0; i < entities.Count; i++)
            {
                GCNode entity = entities[i];
                if (!AuthoredWorldLayout.TryPosition(entity, out int x, out int y, out int z))
                    throw new InvalidDataException($"Authored entity position missing '{entity.CanonicalPath}'");
                string group = $"{groupRoot}:entity:{i}";
                if (AuthoredWorldLayout.IsKindOf(entity, "EncounterObject"))
                {
                    string tablePath = i == firstEncounter && room?.EncounterTable != null
                        ? room.EncounterTable.AuthoredPath : entity.GetString("EncounterTable", world.GetString("EncounterTable", ""));
                    EncounterTableManifest table = GetEncounterTable(tablePath);
                    var marker = new EncounterMarker(checked(x + ox), checked(y + oy), z, entity.GetFixed32("Heading", 0),
                        entity.GetFixed32("SizeX", 50 * 256), entity.GetFixed32("SizeY", 50 * 256), entity.CanonicalPath);
                    AddAuthoredEncounter(snapshot, table, marker, group, cell, x, y, i, rng);
                    continue;
                }
                if (AuthoredWorldLayout.IsKindOf(entity, "EncounterPlaceholderEntity") || AuthoredWorldLayout.IsKindOf(entity, "RoomPlaceholderEntity"))
                {
                    int selector = entity.GetInt("TableSelector", 1);
                    string suffix = selector == 1 ? "" : selector.ToString(CultureInfo.InvariantCulture);
                    string tablePath = entity.GetString("WorldEntityTable", "");
                    if (string.IsNullOrWhiteSpace(tablePath))
                        tablePath = room?.AuthoredNode.GetString("EncounterWorldEntityTable" + suffix, "");
                    if (string.IsNullOrWhiteSpace(tablePath))
                        tablePath = world.GetString("WorldEntityTable" + suffix, "");
                    string generatedType = GenerateWorldEntityType(tablePath, rng, new HashSet<string>(StringComparer.OrdinalIgnoreCase), snapshot.GeneratorLevel);
                    if (string.IsNullOrWhiteSpace(generatedType)) continue;
                    GCNode replacement = AuthoredWorldLayout.Effective(new GCNode { Extends = generatedType, CanonicalPath = entity.CanonicalPath, PackageEntryId = entity.PackageEntryId });
                    AddAuthoredEntity(snapshot, replacement, checked(ox + x), checked(oy + y), z, entity.GetFixed32("Heading", 0), entity.GetString("Name", ""), group, room);
                    continue;
                }
                AddAuthoredEntity(snapshot, entity, checked(ox + x), checked(oy + y), z, entity.GetFixed32("Heading", 0), entity.GetString("Name", ""), group, room);
            }
            if (!string.IsNullOrWhiteSpace(room?.WaypointName) && cell != null)
                snapshot.Waypoints.Add(new ZoneWaypointData { zone = snapshot.ZoneName, name = room.WaypointName,
                    PosFixedX = cell.WorldCenterFixedX, PosFixedY = cell.WorldCenterFixedY, PosFixedZ = 0 });
        }

        private static string GenerateWorldEntityType(string path, CombatRandom rng, HashSet<string> visited, byte level)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            if (!visited.Add(path)) throw new InvalidDataException($"World entity generator cycle '{path}'");
            try
            {
                GCNode table = GCDatabase.Instance.ResolveWithInheritance(path);
                if (table == null) throw new InvalidDataException($"World entity generator missing '{path}'");
                if (AuthoredWorldLayout.IsKindOf(table, "SingleWorldEntityGenerator")) return table.GetString("WorldEntity", "");
                if (AuthoredWorldLayout.IsKindOf(table, "WorldEntityGeneratorLink"))
                    return GenerateWorldEntityType(table.GetString("LinkedGenerator", ""), rng, visited, level);
                var groups = table.EnumerateChildrenInOrder().Where(node => node.IsAnonymous).Select(AuthoredWorldLayout.Effective)
                    .Where(node => node.GetInt("Chance", 1) > 0).GroupBy(node => node.GetInt("Chance", 1)).OrderByDescending(group => group.Key);
                foreach (var group in groups)
                {
                    if (rng.Generate() % (uint)group.Key != 0) continue;
                    GCNode[] candidates = group.Where(node => level >= node.GetInt("MinLevel", 0) && level <= node.GetInt("MaxLevel", 255)).ToArray();
                    if (candidates.Length == 0) continue;
                    GCNode selected = candidates[rng.Generate() % (uint)candidates.Length];
                    string result = selected.HasProperty("LinkedGenerator")
                        ? GenerateWorldEntityType(selected.GetString("LinkedGenerator", ""), rng, visited, level)
                        : selected.GetString("WorldEntity", "");
                    if (!string.IsNullOrWhiteSpace(result)) return result;
                }
                return null;
            }
            finally { visited.Remove(path); }
        }

        private static void AddAuthoredEntity(ProceduralDungeonSnapshot snapshot, GCNode entity, int x, int y, int z, int heading,
            string name, string group, RoomNodeDef room)
        {
            if (AuthoredWorldLayout.IsKindOf(entity, "Waypoint"))
            {
                if (name.Equals("SpawnPoint", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(room?.SpawnName)) name = room.SpawnName;
                snapshot.Waypoints.Add(new ZoneWaypointData { zone = snapshot.ZoneName, name = name, PosFixedX = x, PosFixedY = y, PosFixedZ = z, HeadingFixed = heading });
                return;
            }
            if (AuthoredWorldLayout.IsKindOf(entity, "ZonePortal"))
            {
                string target = room?.LinkToZone;
                if (string.IsNullOrWhiteSpace(target)) target = entity.GetString("Zone", "");
                string point = room?.LinkToSpawn;
                if (string.IsNullOrWhiteSpace(point)) point = entity.GetString("SpawnPoint", "");
                if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(point)) return;
                string color = entity.GetString("Color", "0");
                uint colorValue = color.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? uint.Parse(color.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                    : uint.Parse(color, CultureInfo.InvariantCulture);
                snapshot.Portals.Add(new ZonePortalData { zone = snapshot.ZoneName, gcType = entity.Extends, name = name,
                    PosFixedX = x, PosFixedY = y, PosFixedZ = z, HeadingFixed = heading, targetZone = target, spawnPoint = point,
                    width = entity.GetInt("Width", 40), height = entity.GetInt("Height", 40), color = colorValue });
                return;
            }
            GCNode description = entity.GetChild("Description") ?? entity;
            if (AuthoredWorldLayout.IsKindOf(entity, "CheckpointEntity"))
            {
                string checkpointType = description.GetString("Checkpoint", "");
                if (!AuthoredGameplayCatalog.CheckpointDatabase.ContainsKey(checkpointType))
                    throw new InvalidDataException($"Checkpoint definition missing '{checkpointType}'");
                snapshot.Checkpoints.Add(new ZoneCheckpointData { zone = snapshot.ZoneName, name = name, gcType = checkpointType,
                    entityGcType = entity.Extends, PosFixedX = x, PosFixedY = y, PosFixedZ = z, HeadingFixed = heading });
                return;
            }
            if (AuthoredGameplayCatalog.IsNpcPlacement(entity))
            {
                snapshot.Npcs.Add(new NPCData { gcType = entity.Extends, name = name, PosFixedX = x, PosFixedY = y, PosFixedZ = z, HeadingFixed = heading,
                    hitPoints = entity.GetInt("HitPoints", description.GetInt("HitPoints", 0)), manaPoints = entity.GetInt("ManaPoints", description.GetInt("ManaPoints", 0)) });
                return;
            }
            if (TryResolveSpawnBaseGcType(entity.Extends, out string gcType, out string spawnOverride, out _)
                && AuthoredGameplayCatalog.FindCreature(gcType) != null)
            {
                AddSpawn(snapshot.Spawns, snapshot.ZoneName, new SpawnUnit(gcType, 256, spawnOverride), x, y, z, heading, group);
                snapshot.Spawns[^1].placementRole = "authored-world-entity";
                return;
            }
            WorldEntityData data = AuthoredGameplayCatalog.CreateWorldEntityPlacement(snapshot.WorldEntities.Count + 1, snapshot.ZoneName, name, entity, x, y, z, heading);
            if (data != null) snapshot.WorldEntities.Add(data);
        }

        private static void AddAuthoredEncounter(ProceduralDungeonSnapshot snapshot, EncounterTableManifest table, EncounterMarker marker,
            string groupKey, MazeGenerator.MazeCell cell, int localX, int localY, int markerIndex, CombatRandom rng, SpawnUnit[] fallbackUnits = null, int fallbackChoice = -1)
        {
            if (table == null) return;
            int choice = fallbackUnits == null ? NextTableIndex(rng, table, "DungeonMazeSpawner::authored-encounter-choice", groupKey) : fallbackChoice;
            if (choice < 0) return;
            SpawnUnit[] units = fallbackUnits ?? table[choice];
            if (units.Length == 0) return;
            string populationRole = null;
            if (fallbackUnits == null)
                units = ResolveDewValleyEncounterUnits(snapshot, table, units, groupKey, out populationRole);
            bool normalMinimum = false;
            if (populationRole == null && fallbackUnits == null && units.Length == 1)
            {
                normalMinimum = IsNormalEncounterUnit(units[0]);
                if (normalMinimum) units = new[] { units[0], units[0] };
            }
            var spots = ChooseMarkerSpots(snapshot, marker, units, groupKey);
            if (spots.Count != units.Length)
                throw new InvalidDataException($"Encounter marker has insufficient walkable space zone='{snapshot.ZoneName}' path='{marker.Source}' units={units.Length} spots={spots.Count}");
            string role = fallbackUnits == null ? "authored-encounter" : "local-fallback";
            if (normalMinimum) role = "authored-normal-minimum";
            if (populationRole != null) role = populationRole;
            snapshot.EncounterObjects.Add(new EncounterObjectMirror { ZoneName = snapshot.ZoneName, GroupKey = groupKey, Role = role,
                AuthoredPath = table.AuthoredPath, EntryId = table.EntryId, ChoiceIndex = choice, MarkerIndex = markerIndex,
                PackSlots = units.Length, UnitRows = units.Length, GridX = cell?.GridX ?? -1, GridY = cell?.GridY ?? -1,
                TileType = cell?.TileType, WorldOriginFixedX = cell?.WorldOriginFixedX ?? 0, WorldOriginFixedY = cell?.WorldOriginFixedY ?? 0,
                Source = marker.Source, ManifestSource = table.Source, ChoiceChance = table.ChoiceChance(choice) });
            for (int i = 0; i < units.Length; i++)
            {
                PathNode spot = spots[i];
                if (AuthoredGameplayCatalog.FindCreature(units[i].GcType) == null)
                {
                    GCNode entity = AuthoredWorldLayout.Effective(new GCNode { Extends = units[i].AuthoredType, CanonicalPath = marker.Source });
                    AddAuthoredEntity(snapshot, entity, spot.WorldFixedX, spot.WorldFixedY, spot.HeightFixed, marker.HeadingFixed, "", groupKey, null);
                    continue;
                }
                AddSpawn(snapshot.Spawns, snapshot.ZoneName, units[i], spot.WorldFixedX, spot.WorldFixedY, spot.HeightFixed, marker.HeadingFixed, groupKey);
                DungeonSpawnData spawn = snapshot.Spawns[^1];
                spawn.placementRole = role; spawn.placeholderSource = marker.Source; spawn.placeholderIndex = markerIndex;
                spawn.PlaceholderSizeFixedX = marker.SizeFixedX; spawn.PlaceholderSizeFixedY = marker.SizeFixedY;
                spawn.gridX = cell?.GridX ?? -1; spawn.gridY = cell?.GridY ?? -1; spawn.tileType = cell?.TileType;
                spawn.WorldOriginFixedX = cell?.WorldOriginFixedX ?? 0; spawn.WorldOriginFixedY = cell?.WorldOriginFixedY ?? 0;
                spawn.LocalFixedX = localX; spawn.LocalFixedY = localY; spawn.LocalFixedZ = marker.LocalFixedZ;
                spawn.encounterChoiceIndex = choice;
            }
        }

        private static List<PathNode> ChooseMarkerSpots(ProceduralDungeonSnapshot snapshot, EncounterMarker marker, SpawnUnit[] units, string key)
        {
            var result = new List<PathNode>();
            PathMap map = snapshot.PathMap;
            if (map == null) return result;
            int halfX = Math.Max(0, marker.SizeFixedX / 2), halfY = Math.Max(0, marker.SizeFixedY / 2);
            int minX = marker.LocalFixedX - halfX, maxX = marker.LocalFixedX + halfX, minY = marker.LocalFixedY - halfY, maxY = marker.LocalFixedY + halfY;
            var min = map.WorldToGridFixed(minX, minY); var max = map.WorldToGridFixed(maxX, maxY);
            var candidates = new List<PathNode>();
            for (int y = min.gridY; y <= max.gridY; y++)
                for (int x = min.gridX; x <= max.gridX; x++)
                {
                    PathNode node = map.GetNodeAt(x, y);
                    if (node == null || !node.IsWalkable || node.WorldFixedX < minX || node.WorldFixedX > maxX || node.WorldFixedY < minY || node.WorldFixedY > maxY) continue;
                    candidates.Add(node);
                }
            var seen = new HashSet<(int, int)>(candidates.Select(node => (node.WorldFixedX, node.WorldFixedY)));
            void AddCandidate(int x, int y)
            {
                if (!seen.Add((x, y))) return;
                PathNode node = map.GetNodeAtWorldFixed(x, y);
                if (node?.IsWalkable != true) return;
                candidates.Add(new PathNode { GridX = node.GridX, GridY = node.GridY,
                    WorldFixedX = x, WorldFixedY = y, HeightFixed = map.GetHeightAtFixed(x, y, marker.LocalFixedZ), ConnectedSpaceId = node.ConnectedSpaceId });
            }
            AddCandidate(marker.LocalFixedX, marker.LocalFixedY);
            int placementStep = units.Length > 16 ? 256 : 5 * 256;
            for (int y = minY; y <= maxY; y += placementStep)
                for (int x = minX; x <= maxX; x += placementStep) AddCandidate(x, y);
            candidates.Sort((a, b) =>
            {
                long ax = (long)a.WorldFixedX - marker.LocalFixedX, ay = (long)a.WorldFixedY - marker.LocalFixedY;
                long bx = (long)b.WorldFixedX - marker.LocalFixedX, by = (long)b.WorldFixedY - marker.LocalFixedY;
                int distance = (ax * ax + ay * ay).CompareTo(bx * bx + by * by);
                if (distance != 0 && units.Length <= 16) return distance;
                int row = a.WorldFixedY.CompareTo(b.WorldFixedY); return row != 0 ? row : a.WorldFixedX.CompareTo(b.WorldFixedX);
            });
            if (candidates.Count > 0)
            {
                int connectedSpace = candidates[0].ConnectedSpaceId;
                candidates.RemoveAll(node => node.ConnectedSpaceId != connectedSpace);
            }
            int groupGap = units.Length is >= 2 and <= 5 && units.All(IsNormalEncounterUnit) ? 5 * 256 : 0;
            if (groupGap > 0)
            {
                uint placementSeed = unchecked((uint)StableSpotSeed(key)) ^ snapshot.LayoutSeed;
                long PlacementScore(PathNode node)
                {
                    long dx = (long)node.WorldFixedX - marker.LocalFixedX, dy = (long)node.WorldFixedY - marker.LocalFixedY;
                    uint variation = unchecked((placementSeed ^ (uint)node.WorldFixedX) * 16777619u ^ (uint)node.WorldFixedY);
                    variation ^= variation >> 16;
                    variation = unchecked(variation * 2246822519u);
                    variation ^= variation >> 13;
                    return dx * dx + dy * dy + variation % (uint)(groupGap * groupGap);
                }
                candidates.Sort((a, b) =>
                {
                    int score = PlacementScore(a).CompareTo(PlacementScore(b));
                    if (score != 0) return score;
                    int row = a.WorldFixedY.CompareTo(b.WorldFixedY);
                    return row != 0 ? row : a.WorldFixedX.CompareTo(b.WorldFixedX);
                });
            }
            int[] placementOrder = Enumerable.Range(0, units.Length).ToArray();
            if (units.Length > 16)
                placementOrder = placementOrder.OrderByDescending(index => ResolveSpawnRadius(units[index].AuthoredType)).ThenBy(index => index).ToArray();
            for (int i = 0; i < units.Length; i++)
            {
                int radius = ResolveSpawnRadius(units[placementOrder[i]].AuthoredType);
                foreach (PathNode candidate in candidates)
                {
                    bool occupied = false;
                    foreach (var region in snapshot.EncounterRegions)
                    {
                        if ((long)region.HalfX * region.HalfY >= (long)halfX * halfY) continue;
                        long dx = Math.Max(0L, Math.Abs((long)candidate.WorldFixedX - region.X) - region.HalfX);
                        long dy = Math.Max(0L, Math.Abs((long)candidate.WorldFixedY - region.Y) - region.HalfY);
                        if (dx * dx + dy * dy <= (long)radius * radius) { occupied = true; break; }
                    }
                    for (int j = 0; j < result.Count && !occupied; j++)
                        occupied = TooClose(candidate, result[j].WorldFixedX, result[j].WorldFixedY, radius + ResolveSpawnRadius(units[placementOrder[j]].AuthoredType) + groupGap);
                    foreach (DungeonSpawnData existing in snapshot.Spawns)
                        if (TooClose(candidate, existing.PosFixedX, existing.PosFixedY, radius + ResolveSpawnRadius(existing.spawnGcTypeOverride ?? existing.gcType))) { occupied = true; break; }
                    foreach (var reserved in snapshot.ReservedSpaces)
                        if (TooClose(candidate, reserved.X, reserved.Y, radius + reserved.Radius)) { occupied = true; break; }
                    if (occupied) continue;
                    if (WorldCollision.Instance.TryGetPointBlockerFixed(snapshot.ZoneName, snapshot.PathMap.ZoneName, candidate.WorldFixedX, candidate.WorldFixedY,
                        candidate.HeightFixed, radius, out _)) continue;
                    result.Add(candidate); break;
                }
                if (result.Count != i + 1) return result;
            }
            var authoredOrder = new PathNode[units.Length];
            for (int i = 0; i < result.Count; i++) authoredOrder[placementOrder[i]] = result[i];
            return authoredOrder.ToList();
        }

        private static bool TooClose(PathNode node, int x, int y, int radius)
        {
            long dx = (long)node.WorldFixedX - x, dy = (long)node.WorldFixedY - y;
            return dx * dx + dy * dy <= (long)radius * radius;
        }

        public static int ResolveSpawnRadius(string gcType)
        {
            GCNode node = GCDatabase.Instance.ResolveWithInheritance(gcType);
            return Math.Max(0, (node?.GetChild("Description") ?? node)?.GetFixed32("CollisionRadius", 5 * 256) ?? 5 * 256);
        }
    }
}
