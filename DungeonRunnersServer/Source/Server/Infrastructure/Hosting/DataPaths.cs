using System.IO;
using DungeonRunners.Engine;

namespace DungeonRunners.Core
{
    public static class DataPaths
    {
        public static string ServerRoot => Path.GetFullPath(Application.dataPath);
        public static string DatabaseDir => ServerPath("Database");

        public static string DatabaseFile(string filename)
        {
            if (string.IsNullOrWhiteSpace(filename) || !string.Equals(Path.GetFileName(filename), filename, System.StringComparison.Ordinal))
                throw new InvalidDataException("Database filename must be a leaf name");
            return ServerPath("Database", filename);
        }

        public static string ServerPath(params string[] segments)
        {
            string path = ServerRoot;
            if (segments != null)
                for (int index = 0; index < segments.Length; index++)
                    path = Path.Combine(path, segments[index] ?? "");
            return RequireServerPath(path);
        }

        public static string RequireServerPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidDataException("Server path is empty");
            string root = Path.GetFullPath(ServerRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullPath = Path.GetFullPath(path);
            string prefix = root + Path.DirectorySeparatorChar;
            if (!string.Equals(fullPath, root, System.StringComparison.OrdinalIgnoreCase)
                && !fullPath.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Server path escapes runtime root: {fullPath}");
            RequireNoReparsePoints(root, fullPath);
            return fullPath;
        }

        private static void RequireNoReparsePoints(string root, string fullPath)
        {
            string relative = Path.GetRelativePath(root, fullPath);
            if (relative == ".")
                return;
            string current = root;
            string[] components = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            for (int index = 0; index < components.Length; index++)
            {
                if (string.IsNullOrWhiteSpace(components[index]))
                    continue;
                current = Path.Combine(current, components[index]);
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(current);
                }
                catch (FileNotFoundException)
                {
                    break;
                }
                catch (DirectoryNotFoundException)
                {
                    break;
                }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"Server path contains a reparse point: {current}");
            }
        }
    }
}
