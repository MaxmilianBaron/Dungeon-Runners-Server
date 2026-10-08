using System.Collections.Generic;
using DungeonRunners.Engine;
using DungeonRunners.Gameplay;

namespace DungeonRunners.Core
{
    public class PathMapCatalog
    {
        private static PathMapCatalog _instance;
        public static PathMapCatalog Instance => _instance ??= new PathMapCatalog();

        private Dictionary<string, PathMap> _pathMaps = new Dictionary<string, PathMap>();
        private HashSet<string> _proceduralInstancePathMapMissLogged = new HashSet<string>();
        private bool _loaded = false;
        private readonly object _authoredStaticLock = new object();
        private readonly Dictionary<string, PathMap> _authoredStaticMaps = new Dictionary<string, PathMap>();

        public void LoadAllPathMaps()
        {
            if (_loaded) return;

            using (var connection = DungeonRunners.Database.GameDatabase.GetConnection())
            {
                var zones = new List<string>();
                using (var reader = DungeonRunners.Database.GameDatabase.ExecuteReader(connection,
                    "SELECT zone_name FROM cache.pathmap_zones ORDER BY id"))
                {
                    while (reader.Read())
                        zones.Add(reader.GetString(0));
                }

                foreach (var zone in zones)
                {
                    var pathMap = PathMap.LoadFromSQLite(connection, zone);
                    string key = zone.ToLowerInvariant();
                    _pathMaps.Add(key, pathMap);
                    Debug.Log($"[PATHMAP-CATALOG] loaded zone='{key}' source=cache.db");
                }
            }

            _loaded = true;
            Debug.Log($"[PATHMAP-CATALOG] loaded count={_pathMaps.Count}");
        }

        public void RegisterInstancePathMap(string zoneName, PathMap pathMap)
        {
            if (string.IsNullOrWhiteSpace(zoneName) || pathMap == null) return;
            string key = zoneName.ToLowerInvariant();
            _pathMaps[key] = pathMap;
            _proceduralInstancePathMapMissLogged.Remove(key);
            Debug.Log($"[PATHMAP-CATALOG] register instance='{key}' nodes={pathMap.NodeCount}");
            Debug.LogError($"[PATHMAP-VERIFY] zone='{key}' nodes={pathMap.NodeCount} walkable={pathMap.WalkableCount} boundsFixed=({pathMap.MinWorldFixedX},{pathMap.MinWorldFixedY})->({pathMap.MaxWorldFixedX},{pathMap.MaxWorldFixedY})");
        }

        public void UnregisterInstancePathMap(string zoneName)
        {
            if (string.IsNullOrWhiteSpace(zoneName)) return;
            string key = zoneName.ToLowerInvariant();
            _proceduralInstancePathMapMissLogged.Remove(key);
            if (_pathMaps.Remove(key))
                Debug.Log($"[PATHMAP-CATALOG] unregister instance='{key}'");
        }

        public PathMap GetPathMap(string zoneName)
        {
            if (!_loaded) LoadAllPathMaps();
            if (string.IsNullOrWhiteSpace(zoneName)) return null;
            string key = zoneName.ToLowerInvariant();
            if (_pathMaps.TryGetValue(key, out var pathMap)) return pathMap;
            int instIndex = key.IndexOf("_inst", System.StringComparison.OrdinalIgnoreCase);
            if (instIndex > 0)
            {
                string baseKey = key.Substring(0, instIndex);
                if (DungeonMazeSpawner.IsProceduralZone(baseKey))
                {
                    if (_proceduralInstancePathMapMissLogged.Add(key))
                        Debug.LogError($"[PATHMAP-CATALOG] instance='{zoneName}' base='{baseKey}' reason=missing-instance-pathmap");
                    return null;
                }
                if (_pathMaps.TryGetValue(baseKey, out pathMap)) return pathMap;
            }
            string staticKey = instIndex > 0 ? key.Substring(0, instIndex) : key;
            var world = AuthoredWorldLayout.FindWorld(staticKey);
            if (world == null || world.Generated) return null;
            lock (_authoredStaticLock)
            {
                if (_authoredStaticMaps.TryGetValue(staticKey, out pathMap)) return pathMap;
                pathMap = DungeonRunners.Utilities.PathMapBuilder.BuildAuthoredStaticWorld(staticKey);
                _authoredStaticMaps.Add(staticKey, pathMap);
                return pathMap;
            }
        }

        public int GetHeightFixed(string zoneName, int worldFixedX, int worldFixedY, int defaultHeightFixed)
        {
            return TryGetHeightFixed(zoneName, worldFixedX, worldFixedY, out int heightFixed)
                ? heightFixed
                : defaultHeightFixed;
        }

        public bool TryGetHeightFixed(string zoneName, int worldFixedX, int worldFixedY, out int heightFixed)
        {
            heightFixed = 0;
            var pathMap = GetPathMap(zoneName);
            if (pathMap == null) return false;
            return pathMap.TryGetHeightAtFixed(worldFixedX, worldFixedY, out heightFixed);
        }

        public bool IsWalkableFixed(string zoneName, int worldFixedX, int worldFixedY)
        {
            return TryIsWalkableFixed(zoneName, worldFixedX, worldFixedY, out bool isWalkable) && isWalkable;
        }

        public bool TryIsWalkableFixed(string zoneName, int worldFixedX, int worldFixedY, out bool isWalkable)
        {
            isWalkable = false;
            var pathMap = GetPathMap(zoneName);
            if (pathMap == null)
                return false;
            isWalkable = pathMap.IsWalkableFixed(worldFixedX, worldFixedY);
            return true;
        }
    }
}
