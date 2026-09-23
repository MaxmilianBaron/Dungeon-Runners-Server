using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DungeonRunners.Engine;

namespace DungeonRunners.Data
{
    public sealed class PackageCatalog
    {
        private static PackageCatalog _instance;
        public static PackageCatalog Instance => _instance ??= new PackageCatalog();

        private readonly List<PackageTextDocument> _gcDocuments = new List<PackageTextDocument>();
        private readonly Dictionary<string, PackageTextDocument> _gcByName = new Dictionary<string, PackageTextDocument>(StringComparer.OrdinalIgnoreCase);
        private readonly List<PackageTextDocument> _dictionaryDocuments = new List<PackageTextDocument>();
        private readonly Dictionary<string, PackageTextDocument> _dictionaryByName = new Dictionary<string, PackageTextDocument>(StringComparer.OrdinalIgnoreCase);
        private readonly List<PackageTextDocument> _runtimeDocuments = new List<PackageTextDocument>();

        public bool IsLoaded { get; private set; }
        public string RootPath { get; private set; } = "";
        public string SourceManifest { get; private set; } = "";
        public int ManifestGcTextDocs { get; private set; }
        public int GcTextDocumentCount => _gcDocuments.Count;
        public int RuntimeTextDocumentCount => _runtimeDocuments.Count;
        public IEnumerable<PackageTextDocument> GcTextDocuments => _gcDocuments;
        public IEnumerable<PackageTextDocument> RuntimeTextDocuments => _runtimeDocuments;

        public bool LoadFromAssets()
        {
            _gcDocuments.Clear();
            _gcByName.Clear();
            _dictionaryDocuments.Clear();
            _dictionaryByName.Clear();
            _runtimeDocuments.Clear();
            IsLoaded = false;
            SourceManifest = "";
            ManifestGcTextDocs = 0;

            AuthoredSnapshotCatalog.Instance.Load();
            RootPath = AuthoredSnapshotCatalog.Instance.DatabasePath;
            SourceManifest = AuthoredSnapshotCatalog.Instance.LogicalSha256;
            ManifestGcTextDocs = AuthoredSnapshotCatalog.Instance.ManifestGcTextCount;
            foreach (PackageTextDocument document in AuthoredSnapshotCatalog.Instance.GcDocuments)
            {
                PrepareDocument(document);
                _gcDocuments.Add(document);
                _runtimeDocuments.Add(document);
                Register(document, _gcByName);
            }
            AddRuntimeDocuments(AuthoredSnapshotCatalog.Instance.TileDocuments);
            AddRuntimeDocuments(AuthoredSnapshotCatalog.Instance.WorldDocuments);
            AddRuntimeDocuments(AuthoredSnapshotCatalog.Instance.ZoneDocuments);
            foreach (PackageTextDocument document in AuthoredSnapshotCatalog.Instance.DictionaryDocuments)
            {
                PrepareDocument(document);
                _dictionaryDocuments.Add(document);
                Register(document, _dictionaryByName);
            }
            IsLoaded = _gcDocuments.Count > 0;
            Debug.LogError($"[CLIENT-PACKAGE-CATALOG] source=AuthoredSnapshot loaded={IsLoaded} gcTextDocs={_gcDocuments.Count}/{ManifestGcTextDocs} runtimeTextDocs={_runtimeDocuments.Count} dictionaryTextDocs={_dictionaryDocuments.Count} path='{RootPath}' runtimePkgDependency=false");
            return IsLoaded;
        }

        private void AddRuntimeDocuments(IEnumerable<PackageTextDocument> documents)
        {
            foreach (PackageTextDocument document in documents)
            {
                PrepareDocument(document);
                _runtimeDocuments.Add(document);
                Register(document, _gcByName);
            }
        }

        private static void PrepareDocument(PackageTextDocument document)
        {
            string prefix = document.TypeCode switch
            {
                14 => "tile.",
                15 => "world.",
                16 => "zone.",
                _ => ""
            };
            document.GcPath = prefix + NormalizeGcPath(document.Name);
            document.FileName = ResolveFileName(document);
            document.Stem = Path.GetFileNameWithoutExtension(document.FileName);
        }

        public IEnumerable<PackageTextDocument> EnumerateGcTextDocuments(string searchPattern = "*.gc")
        {
            string pattern = string.IsNullOrWhiteSpace(searchPattern) ? "*.gc" : searchPattern.Trim();
            foreach (var doc in _gcDocuments)
            {
                if (WildcardMatch(doc.FileName, pattern) ||
                    WildcardMatch(doc.Name, pattern) ||
                    WildcardMatch(doc.SourceVirtualPath, pattern) ||
                    WildcardMatch(doc.GcPath, pattern))
                    yield return doc;
            }
        }

        public bool TryGetGcText(string nameOrPath, out PackageTextDocument document)
        {
            document = null;
            if (string.IsNullOrWhiteSpace(nameOrPath))
                return false;
            return _gcByName.TryGetValue(nameOrPath.Trim(), out document);
        }

        public bool TryGetDictionaryText(string nameOrPath, out PackageTextDocument document)
        {
            document = null;
            if (string.IsNullOrWhiteSpace(nameOrPath))
                return false;
            return _dictionaryByName.TryGetValue(nameOrPath.Trim(), out document);
        }

        private void LoadGcTextDocuments(string path)
        {
            LoadTextDocuments(path, _gcDocuments, _gcByName);
        }

        private void LoadDictionaryTextDocuments(string path)
        {
            LoadTextDocuments(path, _dictionaryDocuments, _dictionaryByName);
        }

        private static void LoadTextDocuments(string path, List<PackageTextDocument> documents, Dictionary<string, PackageTextDocument> byName)
        {
            foreach (string line in ReadJsonLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                var document = new PackageTextDocument
                {
                    EntryId = GetInt(line, "entryId"),
                    PackageId = GetInt(line, "packageId"),
                    EntryIndex = GetInt(line, "entryIndex"),
                    Name = GetString(line, "name"),
                    TypeCode = GetInt(line, "typeCode"),
                    TextSha1 = GetString(line, "textSha1"),
                    DecodedSha1 = GetString(line, "decodedSha1"),
                    SourceVirtualPath = GetString(line, "sourceVirtualPath"),
                    Text = GetString(line, "text")
                };
                if (document.EntryId == 0 && string.IsNullOrWhiteSpace(document.Name))
                    continue;
                document.GcPath = NormalizeGcPath(document.Name);
                document.FileName = ResolveFileName(document);
                document.Stem = Path.GetFileNameWithoutExtension(document.FileName);
                documents.Add(document);
                Register(document, byName);
            }
        }

        private static void Register(PackageTextDocument document, Dictionary<string, PackageTextDocument> byName)
        {
            AddName(document.FileName, document, byName);
            AddName(document.Stem, document, byName);
            AddName(document.Name, document, byName);
            AddName(document.Name.Replace('/', '\\'), document, byName);
            AddName(document.Name.Replace('\\', '/'), document, byName);
            AddName(document.SourceVirtualPath, document, byName);
            AddName(document.GcPath, document, byName);
            if (!string.IsNullOrWhiteSpace(document.GcPath))
                AddName(document.GcPath + ".gc", document, byName);
        }

        private static void AddName(string name, PackageTextDocument document, Dictionary<string, PackageTextDocument> byName)
        {
            if (string.IsNullOrWhiteSpace(name))
                return;
            string key = name.Trim();
            if (!byName.ContainsKey(key))
                byName[key] = document;
        }

        private static IEnumerable<string> ReadJsonLines(string path)
        {
            if (File.Exists(path))
            {
                foreach (string line in File.ReadLines(path))
                    yield return line;
                yield break;
            }

            string partsDir = path + ".parts";
            if (!Directory.Exists(partsDir))
                yield break;

            string[] parts = Directory.GetFiles(partsDir, "*.jsonl");
            Array.Sort(parts, StringComparer.OrdinalIgnoreCase);
            foreach (string part in parts)
            {
                foreach (string line in File.ReadLines(part))
                    yield return line;
            }
        }

        private static string ResolveFileName(PackageTextDocument document)
        {
            string source = document.SourceVirtualPath;
            string leaf = "";
            if (!string.IsNullOrWhiteSpace(source))
                leaf = Path.GetFileName(source.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar));

            if (string.IsNullOrWhiteSpace(leaf))
            {
                leaf = (document.Name ?? "").Replace('/', '\\');
                int slash = leaf.LastIndexOf('\\');
                if (slash >= 0)
                    leaf = leaf.Substring(slash + 1);
            }
            if (string.IsNullOrWhiteSpace(leaf))
                leaf = document.EntryId.ToString();
            if (!leaf.EndsWith(".gc", StringComparison.OrdinalIgnoreCase))
                leaf += ".gc";
            return leaf;
        }

        private static string NormalizeGcPath(string value)
        {
            string path = (value ?? "").Replace('\\', '.').Replace('/', '.').Trim();
            if (path.EndsWith(".gc", StringComparison.OrdinalIgnoreCase))
                path = path.Substring(0, path.Length - 3);
            return path;
        }

        private static int GetInt(string json, string key)
        {
            string marker = "\"" + key + "\":";
            int start = json.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
                return 0;
            int pos = start + marker.Length;
            while (pos < json.Length && char.IsWhiteSpace(json[pos]))
                pos++;
            int sign = 1;
            if (pos < json.Length && json[pos] == '-')
            {
                sign = -1;
                pos++;
            }
            int value = 0;
            bool any = false;
            while (pos < json.Length && char.IsDigit(json[pos]))
            {
                any = true;
                value = (value * 10) + (json[pos] - '0');
                pos++;
            }
            return any ? value * sign : 0;
        }

        private static string GetString(string json, string key)
        {
            string marker = "\"" + key + "\":\"";
            int start = json.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
                return "";
            int pos = start + marker.Length;
            var sb = new StringBuilder();
            while (pos < json.Length)
            {
                char c = json[pos++];
                if (c == '"')
                    break;
                if (c != '\\' || pos >= json.Length)
                {
                    sb.Append(c);
                    continue;
                }

                char esc = json[pos++];
                switch (esc)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (pos + 4 <= json.Length && int.TryParse(json.Substring(pos, 4), System.Globalization.NumberStyles.HexNumber, null, out int code))
                        {
                            sb.Append((char)code);
                            pos += 4;
                        }
                        break;
                    default:
                        sb.Append(esc);
                        break;
                }
            }
            return sb.ToString();
        }

        private static bool WildcardMatch(string text, string pattern)
        {
            text = text ?? "";
            pattern = string.IsNullOrWhiteSpace(pattern) ? "*" : pattern;
            int ti = 0, pi = 0, star = -1, mark = 0;
            while (ti < text.Length)
            {
                if (pi < pattern.Length &&
                    (pattern[pi] == '?' || char.ToLowerInvariant(pattern[pi]) == char.ToLowerInvariant(text[ti])))
                {
                    ti++;
                    pi++;
                }
                else if (pi < pattern.Length && pattern[pi] == '*')
                {
                    star = pi++;
                    mark = ti;
                }
                else if (star >= 0)
                {
                    pi = star + 1;
                    ti = ++mark;
                }
                else
                {
                    return false;
                }
            }
            while (pi < pattern.Length && pattern[pi] == '*')
                pi++;
            return pi == pattern.Length;
        }
    }

    public sealed class PackageTextDocument
    {
        public int EntryId;
        public int PackageId;
        public int EntryIndex;
        public string Name;
        public int TypeCode;
        public string TextSha1;
        public string DecodedSha1;
        public string SourceVirtualPath;
        public string Text;
        public string FileName;
        public string Stem;
        public string GcPath;
    }
}
