using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DungeonRunners.Engine;
using DungeonRunners.Combat;
using DungeonRunners.Core;
using DungeonRunners.Data;
using DungeonRunners.Utilities;
using CombatRandom = DungeonRunners.Combat.MersenneTwister;

namespace DungeonRunners.Gameplay
{
    public static partial class DungeonMazeSpawner
    {
        public sealed class ProceduralDungeonSnapshot
        {
            public string ZoneName;
            public uint LayoutSeed;
            public uint RoomSeed;
            public byte GeneratorLevel = 1;
            public IReadOnlyList<string> QuestTypes = Array.Empty<string>();
            public int MazeWidth;
            public int MazeHeight;
            public int EntryGridX;
            public int EntryGridY;
            public int ExitGridX;
            public int ExitGridY;
            public int PlayerSpawnFixedX;
            public int PlayerSpawnFixedY;
            public int PlayerSpawnFixedZ;
            public int PlayerHeadingFixed;
            public int ExitPlayerSpawnFixedX;
            public int ExitPlayerSpawnFixedY;
            public int ExitPlayerSpawnFixedZ;
            public int ExitPlayerHeadingFixed;
            public int EntryPortalSpawnFixedX;
            public int EntryPortalSpawnFixedY;
            public int EntryPortalSpawnFixedZ;
            public int EntryPortalHeadingFixed;
            public int ExitPortalSpawnFixedX;
            public int ExitPortalSpawnFixedY;
            public int ExitPortalSpawnFixedZ;
            public int ExitPortalHeadingFixed;
            public int EntrySourceIndex = -1;
            public int ExitSourceIndex = -1;
            public string EntryTileType;
            public string ExitTileType;
            public string EntryLinkToZone;
            public string EntryLinkToSpawn;
            public string EntrySpawnName;
            public string EntryPortalGcType;
            public string ExitLinkToZone;
            public string ExitLinkToSpawn;
            public string ExitSpawnName;
            public string ExitPortalGcType;
            public string PlayerAnchorSource;
            public string ExitPlayerAnchorSource;
            public string EntryPortalAnchorSource;
            public string ExitPortalAnchorSource;
            public int PlayerAnchorLocalFixedX;
            public int PlayerAnchorLocalFixedY;
            public int PlayerAnchorLocalFixedZ;
            public int ExitPlayerAnchorLocalFixedX;
            public int ExitPlayerAnchorLocalFixedY;
            public int ExitPlayerAnchorLocalFixedZ;
            public int EntryPortalAnchorLocalFixedX;
            public int EntryPortalAnchorLocalFixedY;
            public int EntryPortalAnchorLocalFixedZ;
            public int ExitPortalAnchorLocalFixedX;
            public int ExitPortalAnchorLocalFixedY;
            public int ExitPortalAnchorLocalFixedZ;
            public bool PlayerAnchorWalkable;
            public bool ExitPlayerAnchorWalkable;
            public bool EntryPortalAnchorWalkable;
            public bool ExitPortalAnchorWalkable;
            public bool AnchorsResolved;
            public List<MazeGenerator.MazeCell> Cells = new();
            public List<MazeGenerator.MazeCell> WorldCells = new();
            public PathMap PathMap;
            public List<MazeGenerator.PlacedRoomNode> RoomNodes = new();
            public readonly List<NPCData> Npcs = new();
            public readonly List<WorldEntityData> WorldEntities = new();
            public readonly List<ZonePortalData> Portals = new();
            public readonly List<ZoneWaypointData> Waypoints = new();
            public readonly List<ZoneCheckpointData> Checkpoints = new();
            internal readonly List<(int X, int Y, int Radius)> ReservedSpaces = new();
            internal readonly List<(int X, int Y, int HalfX, int HalfY)> EncounterRegions = new();
            public List<DungeonSpawnData> Spawns = new();
            public List<EncounterObjectMirror> EncounterObjects = new();
        }

        public sealed class EncounterObjectMirror
        {
            public string ZoneName;
            public string GroupKey;
            public string Role;
            public string AuthoredPath;
            public int EntryId;
            public int ChoiceIndex;
            public int MarkerIndex;
            public int PackSlots;
            public int UnitRows;
            public int GridX = -1;
            public int GridY = -1;
            public string TileType;
            public int WorldOriginFixedX;
            public int WorldOriginFixedY;
            public string Source;
            public string ManifestSource;
            public int ChoiceChance;
        }


        private readonly struct EncounterMarker
        {
            public readonly int LocalFixedX;
            public readonly int LocalFixedY;
            public readonly int LocalFixedZ;
            public readonly int HeadingFixed;
            public readonly int SizeFixedX;
            public readonly int SizeFixedY;
            public readonly string Source;

            public EncounterMarker(int localFixedX, int localFixedY, int localFixedZ, int headingFixed = 0, int sizeFixedX = 0, int sizeFixedY = 0, string source = "authored")
            {
                LocalFixedX = localFixedX;
                LocalFixedY = localFixedY;
                LocalFixedZ = localFixedZ;
                HeadingFixed = headingFixed;
                SizeFixedX = sizeFixedX;
                SizeFixedY = sizeFixedY;
                Source = source;
            }
        }

        private readonly struct AuthoredAnchor
        {
            public readonly int LocalFixedX;
            public readonly int LocalFixedY;
            public readonly int LocalFixedZ;
            public readonly int HeadingFixed;
            public readonly string Source;

            public AuthoredAnchor(int localFixedX, int localFixedY, int localFixedZ, int headingFixed, string source)
            {
                LocalFixedX = localFixedX;
                LocalFixedY = localFixedY;
                LocalFixedZ = localFixedZ;
                HeadingFixed = headingFixed;
                Source = source;
            }
        }

        private struct SpawnUnit
        {
            public string GcType;
            public string SpawnGcTypeOverride;
            public int DifficultyF32;
            public int LevelOffset;
            public string AuthoredType => SpawnGcTypeOverride ?? GcType;
            public SpawnUnit(string gcType, int difficultyF32 = 0x100, string spawnGcTypeOverride = null, int levelOffset = 0)
            {
                GcType = gcType;
                DifficultyF32 = difficultyF32;
                SpawnGcTypeOverride = spawnGcTypeOverride;
                LevelOffset = levelOffset;
            }
        }

        private sealed class EncounterTableManifest
        {
            public readonly string AuthoredPath;
            public readonly int EntryId;
            private SpawnUnit[][] _pkgChoices;
            private int[] _pkgChoiceChances;
            private bool _pkgResolved;
            private string _pkgSource = "";
            private string _pkgDetail = "";

            public EncounterTableManifest(string authoredPath, int entryId)
            {
                AuthoredPath = authoredPath;
                EntryId = entryId;
            }

            public SpawnUnit[][] Choices
            {
                get
                {
                    EnsurePkgResolved();
                    return _pkgChoices;
                }
            }

            public int Length => Choices.Length;
            public SpawnUnit[] this[int index] => Choices[index];

            public string Source
            {
                get
                {
                    EnsurePkgResolved();
                    return _pkgSource;
                }
            }

            public string Detail
            {
                get
                {
                    EnsurePkgResolved();
                    return _pkgDetail;
                }
            }

            public int ChoiceChance(int index)
            {
                EnsurePkgResolved();
                return index >= 0 && index < _pkgChoiceChances.Length ? _pkgChoiceChances[index] : 1;
            }

            public void LogResolution()
            {
                EnsurePkgResolved();
            }

            private void EnsurePkgResolved()
            {
                lock (this)
                {
                    if (_pkgResolved) return;
                    if (!GCDatabase.Instance.IsLoaded)
                        throw new InvalidDataException($"Encounter graph is not loaded path='{AuthoredPath}' entry={EntryId}");
                    if (!TryResolveEncounterTableFromGc(this, out var choices, out var chances, out var source, out var detail))
                        throw new InvalidDataException($"Encounter table is incomplete path='{AuthoredPath}' entry={EntryId} detail='{detail}'");
                    _pkgChoices = choices;
                    _pkgChoiceChances = chances;
                    _pkgSource = source;
                    _pkgDetail = detail;
                    _pkgResolved = true;
                }
            }
        }


        private static bool TryResolveEncounterTableFromGc(
            EncounterTableManifest manifest,
            out SpawnUnit[][] choices,
            out int[] choiceChances,
            out string source,
            out string detail)
        {
            choices = null;
            choiceChances = null;
            source = "";
            detail = "";

            if (manifest == null)
            {
                detail = "manifest-null";
                return false;
            }

            var gc = GCDatabase.Instance;
            if (gc == null || !gc.IsLoaded)
            {
                detail = "GCDatabase-not-loaded";
                return false;
            }

            GCNode table = gc.ResolveWithInheritance(manifest.AuthoredPath);
            if (table == null)
            {
                detail = "table-not-found";
                return false;
            }

            if (table.AnonymousChildren == null || table.AnonymousChildren.Count == 0)
            {
                detail = "no-encounter-choices";
                return false;
            }

            var parsedChoices = new List<SpawnUnit[]>();
            var parsedChances = new List<int>();
            var unresolved = new List<string>();
            for (int choiceIndex = 0; choiceIndex < table.AnonymousChildren.Count; choiceIndex++)
            {
                GCNode encounter = AuthoredWorldLayout.Effective(table.AnonymousChildren[choiceIndex]);
                var units = new List<SpawnUnit>();
                if (encounter.AnonymousChildren != null)
                {
                    for (int unitIndex = 0; unitIndex < encounter.AnonymousChildren.Count; unitIndex++)
                    {
                        GCNode unitNode = AuthoredWorldLayout.Effective(encounter.AnonymousChildren[unitIndex]);
                        if (TryBuildSpawnUnitFromEncounterUnit(unitNode, out SpawnUnit unit, out string unitDetail))
                        {
                            units.Add(unit);
                        }
                        else
                        {
                            string type = unitNode?.GetString("Type", "") ?? "";
                            unresolved.Add($"choice={choiceIndex}:unit={unitIndex}:type='{type}':{unitDetail}");
                        }
                    }
                }

                parsedChoices.Add(units.ToArray());
                parsedChances.Add(encounter.GetInt("Chance", 1));
            }

            if (parsedChoices.Count == 0 || unresolved.Count > 0)
            {
                detail = unresolved.Count > 0 ? string.Join(";", unresolved) : "no-parsed-choices";
                return false;
            }

            choices = parsedChoices.ToArray();
            choiceChances = parsedChances.ToArray();
            source = "GCDatabase";
            detail = $"choices={choices.Length} sourceFile='{table.SourceFile ?? ""}'";
            return true;
        }

        private static bool TryBuildSpawnUnitFromEncounterUnit(GCNode unitNode, out SpawnUnit unit, out string detail)
        {
            unit = default;
            detail = "";
            if (unitNode == null)
            {
                detail = "unit-null";
                return false;
            }

            string typePath = unitNode.GetString("Type", "");
            if (string.IsNullOrWhiteSpace(typePath))
            {
                detail = "missing-Type";
                return false;
            }

            int difficultyF32 = unitNode.GetFixed32("Difficulty", 0x100);
            int levelOffset = unitNode.GetInt("LevelOffset", 0);
            if (!TryResolveSpawnBaseGcType(typePath.Trim(), out string gcType, out string spawnOverride, out detail))
                return false;

            unit = new SpawnUnit(gcType, difficultyF32, spawnOverride, levelOffset);
            return true;
        }

        private static bool TryResolveSpawnBaseGcType(string typePath, out string gcType, out string spawnOverride, out string detail)
        {
            gcType = null;
            spawnOverride = null;
            detail = "";
            if (string.IsNullOrWhiteSpace(typePath))
            {
                detail = "empty-type";
                return false;
            }

            if (AuthoredGameplayCatalog.FindCreature(typePath) != null)
            {
                gcType = typePath;
                spawnOverride = typePath;
                return true;
            }

            GCNode entityType = GCDatabase.Instance.Resolve(typePath);
            if (entityType != null && (AuthoredGameplayCatalog.IsNpcPlacement(entityType)
                || AuthoredWorldLayout.IsKindOf(entityType, "NonCombatInteractive")
                || AuthoredWorldLayout.IsKindOf(entityType, "base.Summonable")))
            {
                gcType = typePath;
                return true;
            }

            var gc = GCDatabase.Instance;
            GCNode raw = gc?.Resolve(typePath);
            string currentPath = typePath;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int depth = 0; depth < 16 && raw != null && !string.IsNullOrWhiteSpace(currentPath); depth++)
            {
                if (!visited.Add(currentPath))
                    break;

                if (AuthoredGameplayCatalog.FindCreature(currentPath) != null)
                {
                    gcType = currentPath;
                    spawnOverride = string.Equals(currentPath, typePath, StringComparison.OrdinalIgnoreCase) ? null : typePath;
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(raw.Extends) && AuthoredGameplayCatalog.FindCreature(raw.Extends) != null)
                {
                    gcType = raw.Extends;
                    spawnOverride = typePath;
                    return true;
                }

                currentPath = raw.Extends;
                raw = string.IsNullOrWhiteSpace(currentPath) ? null : gc?.Resolve(currentPath);
            }

            if (IsNonCreatureEncounterEntity(typePath))
            {
                gcType = typePath;
                detail = "non-creature encounter entity; spawn path preserved";
                return true;
            }

            detail = "base-creature-not-found";
            return false;
        }

        private static bool IsNonCreatureEncounterEntity(string typePath)
        {
            if (string.IsNullOrWhiteSpace(typePath))
                return false;
            return typePath.StartsWith("terrain.", StringComparison.OrdinalIgnoreCase) ||
                   typePath.StartsWith("misc.", StringComparison.OrdinalIgnoreCase) ||
                   typePath.IndexOf(".interactives.", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static void RunStartupManifestCheck()
        {
            _ = GetAuthoredLevelDefs();
        }

        private class RoomNodeDef
        {
            public string TileSet;
            public int? GridX;
            public int? GridY;
            public int Chance = 100;
            public EncounterTableManifest EncounterTable;
            public string LinkToSpawn;
            public string LinkToZone;
            public string SpawnName;
            public string PortalGcType;
            public string RequiredQuest;
            public string WaypointName;
            public bool ZoneStart;
            public GCNode AuthoredNode;
        }

        private class LevelDef
        {
            public GCNode AuthoredNode;
            public int EntrySourceIndex;
            public int ExitSourceIndex;
            public int MazeWidth;
            public int MazeHeight;
            public int MazeRandomness;
            public int MazeSparseness;
            public int MazeDeadEndRemovalChance;
            public int TileSize = MazeGenerator.TILE_SIZE;
            public string TileSetPrefix = "elmforest_tileset_";
            public RoomNodeDef[] RoomNodes;
            public EncounterTableManifest EncounterTable;
            public int EntryGridX;
            public int EntryGridY;
            public int ExitGridX;
            public int ExitGridY;
        }

        private static Dictionary<string, LevelDef> LevelDefs => GetAuthoredLevelDefs();

        private static int StableSpotSeed(string key)
        {
            if (string.IsNullOrEmpty(key))
                return 0;
            uint h = 2166136261u;
            for (int i = 0; i < key.Length; i++)
            {
                h ^= key[i];
                h *= 16777619u;
            }
            return (int)h;
        }

        private static int NormalizeHeadingFixed(int headingFixed)
        {
            int fullRotationFixed = 360 * UnitMover.Fixed;
            headingFixed %= fullRotationFixed;
            if (headingFixed < 0)
                headingFixed += fullRotationFixed;
            return headingFixed;
        }

        private static bool IsOpenSpotFixed(PathMap pathMap, int xFixed, int yFixed)
            => pathMap != null && pathMap.IsWalkableFixed(xFixed, yFixed);

        private static string NormalizeBaseZone(string zoneName)
        {
            if (string.IsNullOrEmpty(zoneName))
                return zoneName;
            int instIdx = zoneName.IndexOf("_inst", StringComparison.OrdinalIgnoreCase);
            return instIdx > 0 ? zoneName.Substring(0, instIdx) : zoneName;
        }

        private static int NextTableIndex(CombatRandom rng, EncounterTableManifest table, string phase, string owner)
        {
            int count = table?.Length ?? 0;
            if (count <= 0)
                return -1;

            string drawPhase = phase ?? "DungeonMazeSpawner::EncounterTableChoice";
            string drawOwner = owner ?? table.AuthoredPath;
            var groups = new SortedDictionary<int, List<int>>(Comparer<int>.Create((left, right) => right.CompareTo(left)));
            for (int choiceIndex = 0; choiceIndex < count; choiceIndex++)
            {
                int chance = table.ChoiceChance(choiceIndex);
                if (chance <= 0) continue;
                if (!groups.TryGetValue(chance, out var choices))
                {
                    choices = new List<int>();
                    groups[chance] = choices;
                }

                choices.Add(choiceIndex);
            }

            foreach (var group in groups)
            {
                int chance = group.Key;
                uint gate = RngLedger.Generate(
                    rng,
                    "layout",
                    $"{drawPhase}:chance",
                    0,
                    (uint)(chance - 1),
                    $"{drawOwner}:chance={chance}");
                if (gate != 0)
                    continue;

                var choices = group.Value;
                uint selected = RngLedger.Generate(
                    rng,
                    "layout",
                    $"{drawPhase}:select",
                    0,
                    (uint)(choices.Count - 1),
                    $"{drawOwner}:chance={chance}:candidates={choices.Count}");
                return choices[(int)selected];
            }

            return -1;
        }

        private static void AddSpawn(List<DungeonSpawnData> spawns, string zoneName, SpawnUnit unit,
            int posFixedX, int posFixedY, int posFixedZ, int headingFixed, string groupKey)
        {
            spawns.Add(new DungeonSpawnData
            {
                zoneName = zoneName,
                gcType = unit.GcType,
                spawnGcTypeOverride = unit.SpawnGcTypeOverride,
                PosFixedX = posFixedX,
                PosFixedY = posFixedY,
                PosFixedZ = posFixedZ,
                HeadingFixed = NormalizeHeadingFixed(headingFixed),
                encounterGroupKey = groupKey,
                encounterDifficultyF32 = unit.DifficultyF32,
                encounterLevelOffset = unit.LevelOffset
            });
        }

        public static bool IsStaticBossZone(string zoneName) => HasStaticEncounterData(zoneName);

        public static bool IsProceduralZone(string zoneName)
        {
            return LevelDefs.ContainsKey(NormalizeBaseZone(zoneName));
        }

        public static List<DungeonSpawnData> GenerateStaticBossSpawns(string zoneName, uint seed)
            => GenerateAuthoredStaticSpawns(zoneName, seed);

        public static bool TryGetMazeDimensions(string zoneName,
            out int width, out int height, out int entryX, out int entryY,
            out int randomness, out int sparseness, out int deadEndRemoval)
        {
            if (LevelDefs.TryGetValue(NormalizeBaseZone(zoneName), out var levelDef))
            {
                width = levelDef.MazeWidth;
                height = levelDef.MazeHeight;
                entryX = levelDef.EntryGridX;
                entryY = levelDef.EntryGridY;
                randomness = levelDef.MazeRandomness;
                sparseness = levelDef.MazeSparseness;
                deadEndRemoval = levelDef.MazeDeadEndRemovalChance;
                return true;
            }
            width = height = entryX = entryY = randomness = sparseness = deadEndRemoval = 0;
            return false;
        }

        public static bool TryResolveExploredBitCount(string zoneName, out ushort exploredBitCount)
        {
            exploredBitCount = 0;
            string baseZone = NormalizeBaseZone(zoneName);
            if (string.IsNullOrWhiteSpace(baseZone))
                return false;

            PackageTextDocument document = null;
            foreach (PackageTextDocument candidate in PackageCatalog.Instance.RuntimeTextDocuments)
            {
                if (candidate == null
                    || candidate.TypeCode != 15
                    || !string.Equals(candidate.Name, baseZone, StringComparison.OrdinalIgnoreCase))
                    continue;
                document = candidate;
                break;
            }
            if (document == null)
                return false;

            GCNode world = GCDatabase.Instance.ResolveWithInheritance(document.GcPath);
            if (world == null || !world.GetBool("Generated", false))
                return false;

            int mazeWidth = world.GetInt("MazeWidth", 0);
            int mazeHeight = world.GetInt("MazeHeight", 0);
            int tileSize = world.GetInt("TileSize", 0);
            if (mazeWidth <= 0 || mazeHeight <= 0 || tileSize <= 0)
                return false;

            long spanX = checked((long)mazeWidth * tileSize);
            long spanY = checked((long)mazeHeight * tileSize);
            long xCells = ((spanX + 256L) >> 7) + 1L;
            long yCells = ((spanY + 256L) >> 7) + 1L;
            long wordsPerRow = (xCells + 31L) / 32L;
            long count = checked(wordsPerRow * yCells);
            if (count <= 0 || count > ushort.MaxValue)
                return false;

            exploredBitCount = (ushort)count;
            Debug.LogError($"[MINIMAP] zone='{baseZone}' maze={mazeWidth}x{mazeHeight} tileSize={tileSize} cells=({xCells},{yCells}) wordsPerRow={wordsPerRow} exploredBitCount={exploredBitCount} source=authored-generated-world-bounds sourceFunction=MiniMapExplored::init@0x004BD980 MiniMapExplored::ReadExploredBits@0x004BDD50");
            return true;
        }

        public static bool TryResolveExploredBitCount(ProceduralDungeonSnapshot snapshot, out ushort exploredBitCount)
        {
            exploredBitCount = 0;
            if (snapshot?.Cells == null || snapshot.Cells.Count == 0)
                return false;

            int minX = 0;
            int minY = 0;
            int maxX = 0;
            int maxY = 0;
            bool hasMinimapObject = false;
            for (int cellIndex = 0; cellIndex < snapshot.Cells.Count; cellIndex++)
            {
                MazeGenerator.MazeCell cell = snapshot.Cells[cellIndex];
                if (cell == null || string.IsNullOrWhiteSpace(cell.TileType))
                    continue;
                TileLayout layout;
                try
                {
                    layout = TileLayoutLoader.LoadAuthored(cell.TileType);
                }
                catch (Exception)
                {
                    continue;
                }
                for (int placementIndex = 0; placementIndex < layout.Placements.Count; placementIndex++)
                {
                    TilePlacement placement = layout.Placements[placementIndex];
                    GCNode staticObject = GCDatabase.Instance.ResolveWithInheritance(placement.ExtendsPath);
                    GCNode description = staticObject?.GetChild("Description")
                        ?? staticObject?.GetChild("Object")?.GetChild("Description")
                        ?? staticObject;
                    if (description == null || string.IsNullOrWhiteSpace(description.GetString("MinimapTexture", "")))
                        continue;
                    int tileSize = Math.Max(description.GetInt("MinimapTileWidth", 0), description.GetInt("MinimapTileHeight", 0));
                    if (tileSize == 0)
                        tileSize = 40;
                    int centerX = checked(cell.WorldOriginFixedX + placement.XFixed) / 256;
                    int centerY = checked(cell.WorldOriginFixedY + placement.YFixed) / 256;
                    int halfSize = tileSize / 2;
                    int objectMinX = centerX - halfSize;
                    int objectMinY = centerY - halfSize;
                    int objectMaxX = centerX + halfSize;
                    int objectMaxY = centerY + halfSize;
                    minX = Math.Min(minX, objectMinX);
                    minY = Math.Min(minY, objectMinY);
                    maxX = Math.Max(maxX, objectMaxX);
                    maxY = Math.Max(maxY, objectMaxY);
                    hasMinimapObject = true;
                }
            }
            if (!hasMinimapObject)
                return false;

            int xCells = checked(((maxX + 128 - (minX - 128)) >> 7) + 1);
            int yCells = checked(((maxY + 128 - (minY - 128)) >> 7) + 1);
            int wordsPerRow = checked((xCells + 31) / 32);
            int count = checked(wordsPerRow * yCells);
            if (count <= 0 || count > ushort.MaxValue)
                return false;

            exploredBitCount = (ushort)count;
            Debug.LogError($"[MINIMAP] zone='{snapshot.ZoneName ?? ""}' bounds=({minX},{minY})-({maxX},{maxY}) padding=128 cells=({xCells},{yCells}) wordsPerRow={wordsPerRow} exploredBitCount={exploredBitCount} sourceFunction=MiniMap::init@0x004BD980 MiniMapExplored::ReadExploredBits@0x004C1600");
            return true;
        }

        private static string CellKey(MazeGenerator.MazeCell cell)
        {
            return $"{cell.GridX}:{cell.GridY}";
        }

        private static MazeGenerator.MazeCell FindCell(List<MazeGenerator.MazeCell> cells, int gridX, int gridY)
        {
            if (cells == null) return null;
            for (int cellIndex = 0; cellIndex < cells.Count; cellIndex++)
            {
                var cell = cells[cellIndex];
                if (cell.GridX == gridX && cell.GridY == gridY)
                    return cell;
            }
            return null;
        }


        private static MazeGenerator.PlacedRoomNode FindPlacedRoomNode(ProceduralDungeonSnapshot snapshot, int sourceIndex, params string[] tileSetFallbacks)
        {
            if (snapshot?.RoomNodes == null)
                return null;

            for (int roomNodeIndex = 0; roomNodeIndex < snapshot.RoomNodes.Count; roomNodeIndex++)
            {
                var placed = snapshot.RoomNodes[roomNodeIndex];
                if (placed != null && placed.SourceIndex == sourceIndex)
                    return placed;
            }

            if (tileSetFallbacks == null || tileSetFallbacks.Length == 0)
                return null;

            for (int roomNodeIndex = 0; roomNodeIndex < snapshot.RoomNodes.Count; roomNodeIndex++)
            {
                var placed = snapshot.RoomNodes[roomNodeIndex];
                if (placed == null || string.IsNullOrEmpty(placed.TileSet))
                    continue;

                for (int fallbackIndex = 0; fallbackIndex < tileSetFallbacks.Length; fallbackIndex++)
                {
                    if (placed.TileSet.StartsWith(tileSetFallbacks[fallbackIndex], StringComparison.OrdinalIgnoreCase))
                        return placed;
                }
            }

            return null;
        }

        private static RoomNodeDef GetRoomNodeDef(LevelDef level, int sourceIndex)
        {
            if (level?.RoomNodes == null || sourceIndex < 0 || sourceIndex >= level.RoomNodes.Length)
                return null;
            return level.RoomNodes[sourceIndex];
        }

        private static bool MatchesSpawnName(string requested, string spawnName, string linkToSpawn)
        {
            if (string.IsNullOrWhiteSpace(requested))
                return false;

            string normalized = requested.Trim();
            return (!string.IsNullOrEmpty(spawnName) && normalized.Equals(spawnName, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(linkToSpawn) && normalized.Equals(linkToSpawn, StringComparison.OrdinalIgnoreCase));
        }

        public static bool TryResolveSpawnPointFixed(
            ProceduralDungeonSnapshot snapshot,
            string spawnPoint,
            out int posFixedX,
            out int posFixedY,
            out int posFixedZ,
            out int headingFixed,
            out int sourceIndex,
            out string tileType,
            out int gridX,
            out int gridY,
            out int localFixedX,
            out int localFixedY,
            out int localFixedZ,
            out string source)
        {
            posFixedX = 0;
            posFixedY = 0;
            posFixedZ = 0;
            headingFixed = 0;
            sourceIndex = -1;
            tileType = "";
            gridX = 0;
            gridY = 0;
            localFixedX = 0;
            localFixedY = 0;
            localFixedZ = 0;
            source = "";

            if (snapshot == null || string.IsNullOrWhiteSpace(spawnPoint))
                return false;

            ZoneWaypointData authoredWaypoint = snapshot.Waypoints.Find(point => string.Equals(point.name, spawnPoint, StringComparison.OrdinalIgnoreCase));
            if (authoredWaypoint != null)
            {
                posFixedX = authoredWaypoint.PosFixedX; posFixedY = authoredWaypoint.PosFixedY; posFixedZ = authoredWaypoint.PosFixedZ;
                headingFixed = authoredWaypoint.HeadingFixed;
                source = "authored-room-waypoint";
                return true;
            }

            if (MatchesSpawnName(spawnPoint, snapshot.EntrySpawnName, snapshot.EntryLinkToSpawn))
            {
                posFixedX = snapshot.PlayerSpawnFixedX;
                posFixedY = snapshot.PlayerSpawnFixedY;
                posFixedZ = snapshot.PlayerSpawnFixedZ;
                headingFixed = snapshot.PlayerHeadingFixed;
                sourceIndex = snapshot.EntrySourceIndex;
                tileType = snapshot.EntryTileType;
                gridX = snapshot.EntryGridX;
                gridY = snapshot.EntryGridY;
                localFixedX = snapshot.PlayerAnchorLocalFixedX;
                localFixedY = snapshot.PlayerAnchorLocalFixedY;
                localFixedZ = snapshot.PlayerAnchorLocalFixedZ;
                source = snapshot.PlayerAnchorSource;
                return true;
            }

            if (MatchesSpawnName(spawnPoint, snapshot.ExitSpawnName, snapshot.ExitLinkToSpawn))
            {
                posFixedX = snapshot.ExitPlayerSpawnFixedX;
                posFixedY = snapshot.ExitPlayerSpawnFixedY;
                posFixedZ = snapshot.ExitPlayerSpawnFixedZ;
                headingFixed = snapshot.ExitPlayerHeadingFixed;
                sourceIndex = snapshot.ExitSourceIndex;
                tileType = snapshot.ExitTileType;
                gridX = snapshot.ExitGridX;
                gridY = snapshot.ExitGridY;
                localFixedX = snapshot.ExitPlayerAnchorLocalFixedX;
                localFixedY = snapshot.ExitPlayerAnchorLocalFixedY;
                localFixedZ = snapshot.ExitPlayerAnchorLocalFixedZ;
                source = snapshot.ExitPlayerAnchorSource;
                return true;
            }

            return false;
        }

        private static bool TryGetAuthoredDungeonAnchor(string tileType, bool playerSpawn, out AuthoredAnchor anchor)
        {
            anchor = default;
            foreach (GCNode entity in AuthoredWorldLayout.TileEntities(tileType))
            {
                bool matches = playerSpawn
                    ? AuthoredWorldLayout.IsKindOf(entity, "Waypoint") && (entity.GetString("Name", "").Equals("SpawnPoint", StringComparison.OrdinalIgnoreCase)
                        || entity.GetString("Name", "").Equals("Start", StringComparison.OrdinalIgnoreCase))
                    : AuthoredWorldLayout.IsKindOf(entity, "ZonePortal");
                if (matches && AuthoredWorldLayout.TryPosition(entity, out int x, out int y, out int z))
                {
                    anchor = new AuthoredAnchor(x, y, z, entity.GetFixed32("Heading", 0), $"PKG entry={entity.PackageEntryId} path={entity.CanonicalPath}");
                    return true;
                }
            }
            return false;
        }

        private static (int XFixed, int YFixed, int ZFixed) ResolveAuthoredAnchorFixed(PathMap pathMap, MazeGenerator.MazeCell cell, AuthoredAnchor anchor, out bool walkable)
        {
            walkable = false;
            if (cell == null)
                return (0, 0, 0);

            int worldFixedX = cell.WorldOriginFixedX + anchor.LocalFixedX;
            int worldFixedY = cell.WorldOriginFixedY + anchor.LocalFixedY;
            walkable = IsOpenSpotFixed(pathMap, worldFixedX, worldFixedY);
            return (worldFixedX, worldFixedY, anchor.LocalFixedZ);
        }

        private static bool ResolveSnapshotAnchors(ProceduralDungeonSnapshot snapshot, LevelDef level, PathMap pathMap, List<MazeGenerator.MazeCell> cells)
        {
            if (snapshot == null || level == null || cells == null || cells.Count == 0)
                return false;

            snapshot.MazeWidth = level.MazeWidth;
            snapshot.MazeHeight = level.MazeHeight;

            var entryNode = FindPlacedAnchorRoom(snapshot, level.EntrySourceIndex);
            var exitNode = FindPlacedAnchorRoom(snapshot, level.ExitSourceIndex);

            var entryCell = entryNode != null
                ? FindCell(cells, entryNode.GridX, entryNode.GridY)
                : FindCell(cells, level.EntryGridX, level.EntryGridY);
            var exitCell = exitNode != null
                ? FindCell(cells, exitNode.GridX, exitNode.GridY)
                : FindCell(cells, level.ExitGridX, level.ExitGridY);

            if (entryCell == null || exitCell == null)
            {
                Debug.LogError($"[DUNGEON-ANCHOR] zone={snapshot.ZoneName} entryCell={(entryCell != null)} exitCell={(exitCell != null)} reason=missing-authored-room-cell state=blocked");
                snapshot.AnchorsResolved = false;
                return false;
            }

            snapshot.EntrySourceIndex = entryNode?.SourceIndex ?? -1;
            snapshot.ExitSourceIndex = exitNode?.SourceIndex ?? -1;
            var entryDef = GetRoomNodeDef(level, snapshot.EntrySourceIndex);
            var exitDef = GetRoomNodeDef(level, snapshot.ExitSourceIndex);
            snapshot.EntryGridX = entryCell.GridX;
            snapshot.EntryGridY = entryCell.GridY;
            snapshot.ExitGridX = exitCell.GridX;
            snapshot.ExitGridY = exitCell.GridY;
            snapshot.EntryTileType = entryNode?.TileType ?? entryCell.TileType;
            snapshot.ExitTileType = exitNode?.TileType ?? exitCell.TileType;
            snapshot.EntryLinkToZone = entryDef?.LinkToZone;
            snapshot.EntryLinkToSpawn = entryDef?.LinkToSpawn;
            snapshot.EntrySpawnName = entryDef?.SpawnName;
            snapshot.EntryPortalGcType = FindTilePortalType(snapshot.EntryTileType);
            snapshot.ExitLinkToZone = exitDef?.LinkToZone;
            snapshot.ExitLinkToSpawn = exitDef?.LinkToSpawn;
            snapshot.ExitSpawnName = exitDef?.SpawnName;
            snapshot.ExitPortalGcType = FindTilePortalType(snapshot.ExitTileType);

            bool playerResolved = TryGetAuthoredDungeonAnchor(snapshot.EntryTileType, true, out var playerAnchor);
            bool exitPlayerResolved = TryGetAuthoredDungeonAnchor(snapshot.ExitTileType, true, out var exitPlayerAnchor);
            bool entryPortalResolved = TryGetAuthoredDungeonAnchor(snapshot.EntryTileType, false, out var entryPortalAnchor);
            bool exitPortalResolved = TryGetAuthoredDungeonAnchor(snapshot.ExitTileType, false, out var exitPortalAnchor);
            if (!playerResolved || !exitPlayerResolved)
            {
                Debug.LogError($"[DUNGEON-ANCHOR] zone={snapshot.ZoneName} entry='{snapshot.EntryTileType}' exit='{snapshot.ExitTileType}' player={playerResolved} exitPlayer={exitPlayerResolved} entryPortal={entryPortalResolved} exitPortal={exitPortalResolved} reason=missing-authored-anchor");
                snapshot.AnchorsResolved = false;
                return false;
            }

            snapshot.PlayerAnchorLocalFixedX = playerAnchor.LocalFixedX;
            snapshot.PlayerAnchorLocalFixedY = playerAnchor.LocalFixedY;
            snapshot.PlayerAnchorLocalFixedZ = playerAnchor.LocalFixedZ;
            snapshot.ExitPlayerAnchorLocalFixedX = exitPlayerAnchor.LocalFixedX;
            snapshot.ExitPlayerAnchorLocalFixedY = exitPlayerAnchor.LocalFixedY;
            snapshot.ExitPlayerAnchorLocalFixedZ = exitPlayerAnchor.LocalFixedZ;
            snapshot.EntryPortalAnchorLocalFixedX = entryPortalAnchor.LocalFixedX;
            snapshot.EntryPortalAnchorLocalFixedY = entryPortalAnchor.LocalFixedY;
            snapshot.EntryPortalAnchorLocalFixedZ = entryPortalAnchor.LocalFixedZ;
            snapshot.ExitPortalAnchorLocalFixedX = exitPortalAnchor.LocalFixedX;
            snapshot.ExitPortalAnchorLocalFixedY = exitPortalAnchor.LocalFixedY;
            snapshot.ExitPortalAnchorLocalFixedZ = exitPortalAnchor.LocalFixedZ;
            snapshot.PlayerAnchorSource = playerAnchor.Source;
            snapshot.ExitPlayerAnchorSource = exitPlayerAnchor.Source;
            snapshot.EntryPortalAnchorSource = entryPortalAnchor.Source;
            snapshot.ExitPortalAnchorSource = exitPortalAnchor.Source;

            var playerWorld = ResolveAuthoredAnchorFixed(pathMap, entryCell, playerAnchor, out bool playerWalkable);
            snapshot.PlayerSpawnFixedX = playerWorld.XFixed;
            snapshot.PlayerSpawnFixedY = playerWorld.YFixed;
            snapshot.PlayerSpawnFixedZ = playerWorld.ZFixed;
            snapshot.PlayerHeadingFixed = NormalizeHeadingFixed(playerAnchor.HeadingFixed);
            snapshot.PlayerAnchorWalkable = playerWalkable;

            var exitPlayerWorld = ResolveAuthoredAnchorFixed(pathMap, exitCell, exitPlayerAnchor, out bool exitPlayerWalkable);
            snapshot.ExitPlayerSpawnFixedX = exitPlayerWorld.XFixed;
            snapshot.ExitPlayerSpawnFixedY = exitPlayerWorld.YFixed;
            snapshot.ExitPlayerSpawnFixedZ = exitPlayerWorld.ZFixed;
            snapshot.ExitPlayerHeadingFixed = NormalizeHeadingFixed(exitPlayerAnchor.HeadingFixed);
            snapshot.ExitPlayerAnchorWalkable = exitPlayerWalkable;

            var entryPortalWorld = ResolveAuthoredAnchorFixed(pathMap, entryPortalResolved ? entryCell : null, entryPortalAnchor, out bool entryPortalWalkable);
            snapshot.EntryPortalSpawnFixedX = entryPortalWorld.XFixed;
            snapshot.EntryPortalSpawnFixedY = entryPortalWorld.YFixed;
            snapshot.EntryPortalSpawnFixedZ = entryPortalWorld.ZFixed;
            snapshot.EntryPortalHeadingFixed = NormalizeHeadingFixed(entryPortalAnchor.HeadingFixed);
            snapshot.EntryPortalAnchorWalkable = entryPortalWalkable;

            var exitPortalWorld = ResolveAuthoredAnchorFixed(pathMap, exitPortalResolved ? exitCell : null, exitPortalAnchor, out bool exitPortalWalkable);
            snapshot.ExitPortalSpawnFixedX = exitPortalWorld.XFixed;
            snapshot.ExitPortalSpawnFixedY = exitPortalWorld.YFixed;
            snapshot.ExitPortalSpawnFixedZ = exitPortalWorld.ZFixed;
            snapshot.ExitPortalHeadingFixed = NormalizeHeadingFixed(exitPortalAnchor.HeadingFixed);
            snapshot.ExitPortalAnchorWalkable = exitPortalWalkable;
            snapshot.AnchorsResolved = true;

            Debug.LogError($"[DUNGEON-TRANSFORM] role=player src={snapshot.EntrySourceIndex} tile='{snapshot.EntryTileType}' grid=({snapshot.EntryGridX},{snapshot.EntryGridY}) worldGridY={entryCell.WorldGridY} originFixed=({entryCell.WorldOriginFixedX},{entryCell.WorldOriginFixedY}) centerFixed=({entryCell.WorldCenterFixedX},{entryCell.WorldCenterFixedY}) localFixed=({snapshot.PlayerAnchorLocalFixedX},{snapshot.PlayerAnchorLocalFixedY},{snapshot.PlayerAnchorLocalFixedZ}) sentFixed=({snapshot.PlayerSpawnFixedX},{snapshot.PlayerSpawnFixedY},{snapshot.PlayerSpawnFixedZ}) headingFixed={snapshot.PlayerHeadingFixed} walkable={snapshot.PlayerAnchorWalkable} source='{snapshot.PlayerAnchorSource}'");
            Debug.LogError($"[DUNGEON-TRANSFORM] role=exit-player src={snapshot.ExitSourceIndex} tile='{snapshot.ExitTileType}' grid=({snapshot.ExitGridX},{snapshot.ExitGridY}) worldGridY={exitCell.WorldGridY} originFixed=({exitCell.WorldOriginFixedX},{exitCell.WorldOriginFixedY}) centerFixed=({exitCell.WorldCenterFixedX},{exitCell.WorldCenterFixedY}) localFixed=({snapshot.ExitPlayerAnchorLocalFixedX},{snapshot.ExitPlayerAnchorLocalFixedY},{snapshot.ExitPlayerAnchorLocalFixedZ}) sentFixed=({snapshot.ExitPlayerSpawnFixedX},{snapshot.ExitPlayerSpawnFixedY},{snapshot.ExitPlayerSpawnFixedZ}) headingFixed={snapshot.ExitPlayerHeadingFixed} walkable={snapshot.ExitPlayerAnchorWalkable} source='{snapshot.ExitPlayerAnchorSource}'");
            Debug.LogError($"[DUNGEON-TRANSFORM] role=entry-portal src={snapshot.EntrySourceIndex} tile='{snapshot.EntryTileType}' grid=({snapshot.EntryGridX},{snapshot.EntryGridY}) worldGridY={entryCell.WorldGridY} originFixed=({entryCell.WorldOriginFixedX},{entryCell.WorldOriginFixedY}) centerFixed=({entryCell.WorldCenterFixedX},{entryCell.WorldCenterFixedY}) localFixed=({snapshot.EntryPortalAnchorLocalFixedX},{snapshot.EntryPortalAnchorLocalFixedY},{snapshot.EntryPortalAnchorLocalFixedZ}) sentFixed=({snapshot.EntryPortalSpawnFixedX},{snapshot.EntryPortalSpawnFixedY},{snapshot.EntryPortalSpawnFixedZ}) headingFixed={snapshot.EntryPortalHeadingFixed} walkable={snapshot.EntryPortalAnchorWalkable} source='{snapshot.EntryPortalAnchorSource}'");
            Debug.LogError($"[DUNGEON-TRANSFORM] role=exit-portal src={snapshot.ExitSourceIndex} tile='{snapshot.ExitTileType}' grid=({snapshot.ExitGridX},{snapshot.ExitGridY}) worldGridY={exitCell.WorldGridY} originFixed=({exitCell.WorldOriginFixedX},{exitCell.WorldOriginFixedY}) centerFixed=({exitCell.WorldCenterFixedX},{exitCell.WorldCenterFixedY}) localFixed=({snapshot.ExitPortalAnchorLocalFixedX},{snapshot.ExitPortalAnchorLocalFixedY},{snapshot.ExitPortalAnchorLocalFixedZ}) sentFixed=({snapshot.ExitPortalSpawnFixedX},{snapshot.ExitPortalSpawnFixedY},{snapshot.ExitPortalSpawnFixedZ}) headingFixed={snapshot.ExitPortalHeadingFixed} walkable={snapshot.ExitPortalAnchorWalkable} source='{snapshot.ExitPortalAnchorSource}'");
            return true;
        }

        private static MazeGenerator.PlacedRoomNode FindPlacedAnchorRoom(ProceduralDungeonSnapshot snapshot, int preferredSourceIndex)
        {
            var preferred = FindPlacedRoomNode(snapshot, preferredSourceIndex);
            if (preferred != null && TryGetAuthoredDungeonAnchor(preferred.TileType, true, out _))
                return preferred;
            foreach (var node in snapshot.RoomNodes)
                if (TryGetAuthoredDungeonAnchor(node.TileType, true, out _)) return node;
            return preferred;
        }

        private static void ResolvePathMapBuildSeedFixed(
            MazeGenerator maze,
            List<MazeGenerator.MazeCell> cells,
            int entrySourceIndex,
            out int seedFixedX,
            out int seedFixedY,
            out int seedFixedZ,
            out string source)
        {
            MazeGenerator.MazeCell entryCell = null;
            if (maze?.PlacedRoomNodes != null)
            {
                foreach (var roomNode in maze.PlacedRoomNodes)
                {
                    if (roomNode.SourceIndex != entrySourceIndex)
                        continue;
                    entryCell = FindCell(cells, roomNode.GridX, roomNode.GridY);
                    if (entryCell != null)
                        break;
                }
            }

            entryCell ??= cells != null && cells.Count > 0 ? cells[0] : null;
            if (entryCell == null)
            {
                seedFixedX = 0;
                seedFixedY = 0;
                seedFixedZ = 0;
                source = "missing-cell";
                return;
            }

            seedFixedX = entryCell.WorldCenterFixedX;
            seedFixedY = entryCell.WorldCenterFixedY;
            seedFixedZ = 0;
            source = "entry-cell-center";
            if (TryGetAuthoredDungeonAnchor(entryCell.TileType, true, out var playerAnchor))
            {
                seedFixedX = entryCell.WorldOriginFixedX + playerAnchor.LocalFixedX;
                seedFixedY = entryCell.WorldOriginFixedY + playerAnchor.LocalFixedY;
                seedFixedZ = playerAnchor.LocalFixedZ;
                source = "authored-entry-player-anchor";
            }
        }

        public static List<DungeonSpawnData> GenerateSpawns(string zoneName, uint seed)
        {
            var snapshot = GenerateSnapshot(zoneName, seed);
            return snapshot?.Spawns;
        }

        public static ProceduralDungeonSnapshot GenerateSnapshot(string zoneName, uint seed, uint roomSeed = 0, string instanceKey = null, IReadOnlyList<string> questTypes = null, byte generatorLevel = 1)
        {
            string baseZone = NormalizeBaseZone(zoneName);
            string pathMapKey = string.IsNullOrWhiteSpace(instanceKey) ? baseZone : instanceKey;
            var snapshot = new ProceduralDungeonSnapshot
            {
                ZoneName = baseZone,
                LayoutSeed = seed,
                RoomSeed = roomSeed,
                QuestTypes = questTypes ?? Array.Empty<string>(),
                GeneratorLevel = generatorLevel
            };
            var spawns = snapshot.Spawns;

            if (!LevelDefs.TryGetValue(baseZone, out LevelDef level))
            {
                Debug.LogError($"[MAZE-SPAWNER] zone='{zoneName}' reason=no-level-definition");
                return snapshot;
            }

            Debug.LogError($"[MAZE-SPAWNER] begin zone={baseZone} size={level.MazeWidth}x{level.MazeHeight} seed=0x{seed:X8}");

            Debug.LogError($"[MAZE-SPAWNER] rng layoutSeed=0x{seed:X8} entityManagerOpcode0CSeed=0x{roomSeed:X8}");
            var rng = new CombatRandom(seed);
            RngLedger.LogSeed("layout", "DungeonMazeSpawner::layout-seed", seed, baseZone);

            var maze = new MazeGenerator(
                level.MazeWidth, level.MazeHeight, seed,
                level.MazeRandomness, level.MazeSparseness,
                level.MazeDeadEndRemovalChance,
                rng,
                level.TileSize
            );
            Debug.LogError($"[MAZE-SPAWNER] worldRoot=client-integer-half-grid tileSize={level.TileSize} pathMapCenterOverride=False");
            if (level.RoomNodes != null)
            {
                for (int nodeIndex = 0; nodeIndex < level.RoomNodes.Length; nodeIndex++)
                {
                    var node = level.RoomNodes[nodeIndex];
                    maze.AddRoomNode(node.TileSet, node.GridX, node.GridY, node.Chance, nodeIndex, string.IsNullOrWhiteSpace(node.RequiredQuest) || snapshot.QuestTypes.Contains(node.RequiredQuest, StringComparer.OrdinalIgnoreCase));
                }
            }
            var cells = maze.BuildWorld(level.TileSetPrefix);
            var worldCells = new List<MazeGenerator.MazeCell>(maze.WorldCells);

            Debug.LogError($"[MAZE-SPAWNER] cells={cells.Count} worldTiles={worldCells.Count} paddingTiles={maze.PaddingCellCount}");

            WorldCollision.Instance.PrepareProceduralInstance(baseZone, pathMapKey, worldCells);

            ResolvePathMapBuildSeedFixed(
                maze,
                cells,
                level.EntrySourceIndex,
                out int pathMapSeedFixedX,
                out int pathMapSeedFixedY,
                out int pathMapSeedFixedZ,
                out string pathMapSeedSource);
            Debug.LogError(
                $"[PATHMAP-SEED] zone={baseZone} instance='{pathMapKey}' fixed8=({pathMapSeedFixedX},{pathMapSeedFixedY},{pathMapSeedFixedZ}) source={pathMapSeedSource}");
            var mazePathMap = DungeonRunners.Utilities.PathMapBuilder.Build(
                pathMapKey,
                worldCells,
                pathMapSeedFixedX,
                pathMapSeedFixedY,
                pathMapSeedFixedZ);
            snapshot.PathMap = mazePathMap;
            if (mazePathMap == null)
                Debug.LogError($"[MAZE-SPAWNER] zone={baseZone} reason=pathmap-build-null cells={cells.Count}");

            snapshot.Cells = new List<MazeGenerator.MazeCell>(cells);
            snapshot.WorldCells = worldCells;
            snapshot.RoomNodes = new List<MazeGenerator.PlacedRoomNode>(maze.PlacedRoomNodes);
            ResolveSnapshotAnchors(snapshot, level, mazePathMap, cells);
            PopulateAuthoredSpawns(snapshot, level, rng);
            int totalRegular = snapshot.Spawns.Count;
            int totalLeaders = snapshot.EncounterObjects.Count;
            Debug.LogError($"[MAZE-SPAWNER] total={spawns.Count} regular={totalRegular} leaders={totalLeaders} zone={baseZone}");
            Debug.LogError($"[DUNGEON-SNAPSHOT] zone={baseZone} layoutSeed=0x{snapshot.LayoutSeed:X8} roomSeed=0x{snapshot.RoomSeed:X8} cells={snapshot.Cells.Count} roomNodes={snapshot.RoomNodes.Count} spawns={snapshot.Spawns.Count} entry=({snapshot.EntryGridX},{snapshot.EntryGridY}) entryTile='{snapshot.EntryTileType}' playerFixed=({snapshot.PlayerSpawnFixedX},{snapshot.PlayerSpawnFixedY},{snapshot.PlayerSpawnFixedZ}) entryPortalFixed=({snapshot.EntryPortalSpawnFixedX},{snapshot.EntryPortalSpawnFixedY},{snapshot.EntryPortalSpawnFixedZ}) exit=({snapshot.ExitGridX},{snapshot.ExitGridY}) exitTile='{snapshot.ExitTileType}' exitPortalFixed=({snapshot.ExitPortalSpawnFixedX},{snapshot.ExitPortalSpawnFixedY},{snapshot.ExitPortalSpawnFixedZ}) yTransform=worldGridY=gridY/BuildWorld");
            return snapshot;
        }
    }
}
