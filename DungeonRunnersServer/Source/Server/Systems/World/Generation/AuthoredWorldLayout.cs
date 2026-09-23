using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DungeonRunners.Data;

namespace DungeonRunners.Gameplay
{
    public static class AuthoredWorldLayout
    {
        public sealed class WorldDefinition
        {
            public string Name;
            public GCNode Root;
            public readonly List<GCNode> Rooms = new();
            public readonly List<GCNode> Entities = new();
            public bool Generated => Root.GetBool("Generated", false);
        }

        public sealed class TileGroup
        {
            public string TileSet;
            public byte Exits;
            public string North = "", South = "", East = "", West = "";
            public string EdgeKey;
            public int Width => Math.Max(1, North.Length > 0 ? North.Length : South.Length);
            public int Height => Math.Max(1, East.Length > 0 ? East.Length : West.Length);
            public readonly List<string> Variants = new();
            public string First => Variants[0];

            public byte CellExits(int x, int y)
            {
                byte exits = 0;
                if (y == 0 && North.Length > Width - 1 - x && North[Width - 1 - x] != '0') exits |= 1;
                if (y == Height - 1 && South.Length > Width - 1 - x && South[Width - 1 - x] != '0') exits |= 2;
                if (x == 0 && East.Length > y && East[y] != '0') exits |= 8;
                if (x == Width - 1 && West.Length > y && West[y] != '0') exits |= 4;
                return exits;
            }

            public string ChooseVariant(uint raw)
            {
                int draw = (int)(raw % checked((uint)Variants.Count * 100u));
                for (int i = 0; i < Variants.Count; i++)
                    if (draw <= checked((i + 1) * 100))
                        return Variants[i];
                throw new InvalidDataException("Tile variant interval is invalid");
            }
        }

        private static readonly object Sync = new();
        private static bool Loaded;
        private static readonly Dictionary<string, WorldDefinition> Worlds = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, List<TileGroup>> Sets = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, TileGroup> GroupsByVariant = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, GCNode> Tiles = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Regex TileName = new(@"^([a-z_]+)((?:[0-9]+[nesw])+)(?:_[a-z0-9]+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static IReadOnlyCollection<WorldDefinition> Definitions
        {
            get { EnsureLoaded(); return Worlds.Values; }
        }

        public static void EnsureLoaded()
        {
            lock (Sync)
            {
                if (Loaded) return;
                if (!GCDatabase.Instance.IsLoaded || !PackageCatalog.Instance.IsLoaded)
                    throw new InvalidOperationException("Authored graph must be loaded before world layouts");
                Worlds.Clear(); Sets.Clear(); GroupsByVariant.Clear(); Tiles.Clear();
                foreach (PackageTextDocument document in PackageCatalog.Instance.RuntimeTextDocuments)
                {
                    if (document.TypeCode != 14 && document.TypeCode != 15) continue;
                    string scope = document.TypeCode == 14 ? "tile." : "world.";
                    GCNode root = GCDatabase.Instance.ResolveWithInheritance(scope + document.Name);
                    if (root == null) throw new InvalidDataException($"Authored world root missing entry={document.EntryId} name='{document.Name}'");
                    if (document.TypeCode == 14)
                    {
                        Tiles.Add(document.Name, root);
                        Match match = TileName.Match(document.Name);
                        if (!match.Success) throw new InvalidDataException($"Unsupported authored tile name '{document.Name}'");
                        string set = match.Groups[1].Value;
                        var parsed = new TileGroup { TileSet = set };
                        foreach (Match edge in Regex.Matches(match.Groups[2].Value, @"([0-9]+)([nesw])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                        {
                            string digits = edge.Groups[1].Value;
                            char direction = char.ToLowerInvariant(edge.Groups[2].Value[0]);
                            switch (direction)
                            {
                                case 'n': parsed.North = digits; if (digits.Any(c => c != '0')) parsed.Exits |= 1; break;
                                case 's': parsed.South = digits; if (digits.Any(c => c != '0')) parsed.Exits |= 2; break;
                                case 'e': parsed.East = digits; if (digits.Any(c => c != '0')) parsed.Exits |= 8; break;
                                case 'w': parsed.West = digits; if (digits.Any(c => c != '0')) parsed.Exits |= 4; break;
                            }
                        }
                        parsed.EdgeKey = $"{parsed.North}|{parsed.East}|{parsed.South}|{parsed.West}";
                        if (!Sets.TryGetValue(set, out List<TileGroup> groups)) Sets.Add(set, groups = new());
                        TileGroup group = groups.FirstOrDefault(candidate => candidate.EdgeKey == parsed.EdgeKey);
                        if (group == null) { group = parsed; groups.Add(group); }
                        group.Variants.Add(scope + document.Name);
                        GroupsByVariant.Add(document.Name, group);
                    }
                    else
                    {
                        var world = new WorldDefinition { Name = document.Name, Root = root };
                        foreach (GCNode child in root.EnumerateChildrenInOrder())
                            if (IsKindOf(child, "RoomNode") || IsKindOf(child, "LinkRoomNode")) world.Rooms.Add(Effective(child));
                        GCNode entities = root.GetChild("Entities");
                        if (entities != null)
                            foreach (GCNode child in entities.EnumerateChildrenInOrder())
                                if (!string.IsNullOrWhiteSpace(child.Extends)) world.Entities.Add(Effective(child));
                        Worlds.Add(document.Name, world);
                    }
                }
                Loaded = true;
            }
        }

        public static byte ParseExits(string name)
        {
            byte exits = 0;
            if (name.Contains("1n", StringComparison.OrdinalIgnoreCase)) exits |= 1;
            if (name.Contains("1s", StringComparison.OrdinalIgnoreCase)) exits |= 2;
            if (name.Contains("1e", StringComparison.OrdinalIgnoreCase)) exits |= 8;
            if (name.Contains("1w", StringComparison.OrdinalIgnoreCase)) exits |= 4;
            return exits;
        }

        public static WorldDefinition FindWorld(string zone)
        {
            EnsureLoaded();
            return zone != null && Worlds.TryGetValue(zone, out var world) ? world : null;
        }

        public static GCNode FindTile(string name)
        {
            EnsureLoaded();
            if (name?.StartsWith("tile.", StringComparison.OrdinalIgnoreCase) == true) name = name.Substring(5);
            return name != null && Tiles.TryGetValue(name, out var tile) ? tile : null;
        }

        public static List<string> RoomTiles(string set)
        {
            EnsureLoaded();
            return Sets.TryGetValue(set, out var groups)
                ? groups.Where(group => group.Exits != 0).Select(group => group.First).ToList()
                : new List<string>();
        }

        public static TileGroup FindGroup(string set, byte exits)
        {
            EnsureLoaded();
            return Sets.TryGetValue(set, out var groups) ? groups.FirstOrDefault(group => group.Exits == exits) : null;
        }

        public static TileGroup GroupForVariant(string name)
        {
            EnsureLoaded();
            if (name?.StartsWith("tile.", StringComparison.OrdinalIgnoreCase) == true) name = name.Substring(5);
            return GroupsByVariant.TryGetValue(name, out var group) ? group : null;
        }

        public static GCNode Effective(GCNode node) => GCDatabase.Instance.ResolveWithInheritance(node);

        public static bool IsKindOf(GCNode node, string type)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (node != null)
            {
                if (string.Equals(node.CanonicalPath, type, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(node.Extends, type, StringComparison.OrdinalIgnoreCase)) return true;
                if (string.IsNullOrWhiteSpace(node.Extends) || !seen.Add(node.Extends)) return false;
                node = GCDatabase.Instance.Resolve(node.Extends);
            }
            return false;
        }

        public static bool TryPosition(GCNode node, out int x, out int y, out int z)
        {
            x = y = z = 0;
            string[] parts = node?.GetString("Position", "").Split(',');
            return parts?.Length == 3 && GCNode.TryParseFixed32(parts[0], out x)
                && GCNode.TryParseFixed32(parts[1], out y) && GCNode.TryParseFixed32(parts[2], out z);
        }

        public static List<GCNode> TileEntities(string tile)
        {
            GCNode entities = FindTile(tile)?.GetChild("Entities");
            var result = new List<GCNode>();
            if (entities != null)
                foreach (GCNode node in entities.EnumerateChildrenInOrder())
                    if (!string.IsNullOrWhiteSpace(node.Extends)) result.Add(Effective(node));
            return result;
        }
    }
}
