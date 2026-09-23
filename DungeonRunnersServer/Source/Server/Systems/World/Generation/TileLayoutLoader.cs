using System;
using System.Collections.Generic;
using System.IO;
using DungeonRunners.Data;

namespace DungeonRunners.Utilities
{
    public static class TileLayoutLoader
    {
        private static readonly object FileCacheLock = new object();
        private static readonly Dictionary<string, TileLayout> FileCache = new Dictionary<string, TileLayout>(StringComparer.OrdinalIgnoreCase);

        public static TileLayout LoadAuthored(string tileTypeName)
        {
            if (string.IsNullOrWhiteSpace(tileTypeName))
                throw new ArgumentException("Tile type is empty", nameof(tileTypeName));
            string key = "authored:" + tileTypeName.Trim();
            lock (FileCacheLock)
            {
                if (FileCache.TryGetValue(key, out TileLayout cached))
                    return cached;
            }
            if (!GCDatabase.Instance.IsLoaded)
                throw new InvalidOperationException("GCDatabase must be loaded before tile layout inheritance");
            GCNode root = GCDatabase.Instance.ResolveWithInheritance(tileTypeName.Trim());
            if (root == null)
                throw new InvalidDataException($"Authored tile not found: {tileTypeName}");
            var placements = new List<TilePlacement>();
            Collect(root, placements);
            var parsed = new TileLayout(key, root.Extends, placements);
            lock (FileCacheLock)
            {
                if (FileCache.TryGetValue(key, out TileLayout cached))
                    return cached;
                FileCache[key] = parsed;
                return parsed;
            }
        }

        public static TileLayout LoadFromText(string text, string sourceName = "")
        {
            GCNode root = GcParser.Parse(text, sourceName);
            if (root == null) throw new InvalidDataException("GcParser returned null");

            var placements = new List<TilePlacement>();
            Collect(root, placements);
            return new TileLayout(sourceName, root.Extends, placements);
        }

        private static void Collect(GCNode node, List<TilePlacement> output)
        {
            if (node == null) return;

            if (node.IsAnonymous
                && !string.IsNullOrEmpty(node.Extends)
                && node.HasProperty("Position"))
            {
                if (TryParseVector3Fixed(node.GetString("Position"), out int xFixed, out int yFixed, out int zFixed))
                {
                    int headingFixed = 0;
                    if (node.HasProperty("Heading"))
                        TryParseFixed8(node.GetString("Heading"), out headingFixed);
                    output.Add(new TilePlacement(node.Extends, xFixed, yFixed, zFixed, headingFixed, node));
                }
            }

            foreach (GCNode child in node.EnumerateChildrenInOrder())
                Collect(child, output);
        }

        private static bool TryParseVector3Fixed(string raw, out int xFixed, out int yFixed, out int zFixed)
        {
            xFixed = yFixed = zFixed = 0;
            if (string.IsNullOrEmpty(raw)) return false;

            string[] parts = raw.Split(',');
            if (parts.Length != 3) return false;

            return TryParseFixed8(parts[0], out xFixed)
                && TryParseFixed8(parts[1], out yFixed)
                && TryParseFixed8(parts[2], out zFixed);
        }

        private static bool TryParseFixed8(string raw, out int fixedValue)
        {
            return GCNode.TryParseFixed32(raw, out fixedValue);
        }

    }

    public sealed class TileLayout
    {
        public string SourcePath { get; }
        public string RootExtends { get; }
        public IReadOnlyList<TilePlacement> Placements { get; }

        public TileLayout(string sourcePath, string rootExtends, List<TilePlacement> placements)
        {
            SourcePath = sourcePath;
            RootExtends = rootExtends;
            Placements = placements;
        }
    }

    public readonly struct TilePlacement
    {
        public readonly string ExtendsPath;
        public readonly int XFixed;
        public readonly int YFixed;
        public readonly int ZFixed;
        public readonly int HeadingFixed;
        public readonly GCNode Definition;

        public TilePlacement(string extendsPath, int xFixed, int yFixed, int zFixed, int headingFixed, GCNode definition = null)
        {
            ExtendsPath = extendsPath;
            XFixed = xFixed;
            YFixed = yFixed;
            ZFixed = zFixed;
            HeadingFixed = headingFixed;
            Definition = definition;
        }

        public string LeafName
        {
            get
            {
                if (string.IsNullOrEmpty(ExtendsPath)) return "";
                int dot = ExtendsPath.LastIndexOf('.');
                return dot < 0 ? ExtendsPath : ExtendsPath.Substring(dot + 1);
            }
        }
    }
}
