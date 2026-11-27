using System;
using System.Collections.Generic;
using System.IO;
using Mono.Data.Sqlite;
using DungeonRunners.Engine;

namespace DungeonRunners.Data
{
    public sealed class AuthoredSnapshotCatalog
    {
        private const int ExpectedSchemaVersion = 4;
        private const int ExpectedEntryCount = 30893;
        private const int ExpectedGcTextCount = 9159;
        private const int ExpectedTileTextCount = 1477;
        private const int ExpectedWorldTextCount = 584;
        private const int ExpectedZoneTextCount = 575;
        private const int ExpectedRuntimeTextCount = 11795;
        private const string ExpectedParserVersion = "direct-pki-v3-v1";
        private const string ExpectedPkiSha1 = "09b7cc6479dba96dbc8090f65a0f5720de605329";
        private const string ExpectedPkgSha1 = "6969271759ab427d47b3b05c9f8fcf4f6a283d14";
        private const string ExpectedLogicalSha256 = "665d153a6a11ad32541637d482f90ce77527c88dd08d1cc60f48bc73286534bd";

        private static AuthoredSnapshotCatalog _instance;
        public static AuthoredSnapshotCatalog Instance => _instance ??= new AuthoredSnapshotCatalog();

        private readonly List<PackageTextDocument> _gcDocuments = new List<PackageTextDocument>();
        private readonly List<PackageTextDocument> _dictionaryDocuments = new List<PackageTextDocument>();
        private readonly List<PackageTextDocument> _tileDocuments = new List<PackageTextDocument>();
        private readonly List<PackageTextDocument> _worldDocuments = new List<PackageTextDocument>();
        private readonly List<PackageTextDocument> _zoneDocuments = new List<PackageTextDocument>();
        private readonly Dictionary<string, string> _manifest = new Dictionary<string, string>(StringComparer.Ordinal);

        public bool IsLoaded { get; private set; }
        public string DatabasePath { get; private set; } = "";
        public IReadOnlyList<PackageTextDocument> GcDocuments => _gcDocuments;
        public IReadOnlyList<PackageTextDocument> DictionaryDocuments => _dictionaryDocuments;
        public IReadOnlyList<PackageTextDocument> TileDocuments => _tileDocuments;
        public IReadOnlyList<PackageTextDocument> WorldDocuments => _worldDocuments;
        public IReadOnlyList<PackageTextDocument> ZoneDocuments => _zoneDocuments;
        public int ManifestGcTextCount { get; private set; }
        public string LogicalSha256 { get; private set; } = "";

        public bool TryLoadBinaryPayload(int typeCode, string name, out byte[] payload, out int entryIndex)
        {
            payload = null;
            entryIndex = -1;
            if (!IsLoaded || string.IsNullOrWhiteSpace(name))
                return false;
            string leaf = Path.GetFileNameWithoutExtension(name.Trim());
            using var connection = OpenReadOnly();
            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT b.data,e.entry_index FROM entries e
                JOIN binary_payloads b ON b.entry_id=e.entry_id
                WHERE e.type_code=@typeCode AND e.name=@name COLLATE NOCASE
                ORDER BY e.entry_index LIMIT 1";
            command.Parameters.AddWithValue("@typeCode", typeCode);
            command.Parameters.AddWithValue("@name", leaf);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return false;
            payload = (byte[])reader.GetValue(0);
            entryIndex = reader.GetInt32(1);
            return true;
        }

        public int CountBinaryPayloads(int typeCode)
        {
            if (!IsLoaded)
                return 0;
            using var connection = OpenReadOnly();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM binary_payloads b JOIN entries e ON e.entry_id=b.entry_id WHERE e.type_code=@typeCode";
            command.Parameters.AddWithValue("@typeCode", typeCode);
            return checked(Convert.ToInt32(command.ExecuteScalar()));
        }

        public void Load()
        {
            IsLoaded = false;
            _gcDocuments.Clear();
            _dictionaryDocuments.Clear();
            _tileDocuments.Clear();
            _worldDocuments.Clear();
            _zoneDocuments.Clear();
            _manifest.Clear();
            DatabasePath = DungeonRunners.Core.DataPaths.DatabaseFile("authored.db");
            if (!File.Exists(DatabasePath))
                throw new InvalidDataException($"Authored snapshot is missing: {DatabasePath}");

            using var connection = OpenReadOnly();
            LoadManifest(connection);
            ValidateManifest(connection);
            LoadTextDocuments(connection, 13, _gcDocuments);
            LoadTextDocuments(connection, 12, _dictionaryDocuments);
            LoadTextDocuments(connection, 14, _tileDocuments);
            LoadTextDocuments(connection, 15, _worldDocuments);
            LoadTextDocuments(connection, 16, _zoneDocuments);
            int runtimeTextCount = _gcDocuments.Count + _tileDocuments.Count + _worldDocuments.Count + _zoneDocuments.Count;
            if (_gcDocuments.Count != ExpectedGcTextCount
                || _tileDocuments.Count != ExpectedTileTextCount
                || _worldDocuments.Count != ExpectedWorldTextCount
                || _zoneDocuments.Count != ExpectedZoneTextCount
                || runtimeTextCount != ExpectedRuntimeTextCount)
                throw new InvalidDataException($"Authored runtime text coverage mismatch gc={_gcDocuments.Count}/{ExpectedGcTextCount} tile={_tileDocuments.Count}/{ExpectedTileTextCount} world={_worldDocuments.Count}/{ExpectedWorldTextCount} zone={_zoneDocuments.Count}/{ExpectedZoneTextCount} total={runtimeTextCount}/{ExpectedRuntimeTextCount}");
            IsLoaded = true;
            Debug.LogError($"[AUTHORED-SNAPSHOT] loaded=True schema={ExpectedSchemaVersion} entries={ExpectedEntryCount} gcText={_gcDocuments.Count} tileText={_tileDocuments.Count} worldText={_worldDocuments.Count} zoneText={_zoneDocuments.Count} runtimeText={runtimeTextCount} dictionaryText={_dictionaryDocuments.Count} pkiSha1={ExpectedPkiSha1} pkgSha1={ExpectedPkgSha1} logicalSha256={LogicalSha256} path='{DatabasePath}'");
        }

        private SqliteConnection OpenReadOnly()
        {
            var connection = new SqliteConnection($"URI=file:{DatabasePath};FailIfMissing=True");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA query_only=ON";
            command.ExecuteNonQuery();
            return connection;
        }

        private void LoadManifest(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT key,value FROM manifest ORDER BY key";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                _manifest.Add(reader.GetString(0), reader.GetString(1));
        }

        private void ValidateManifest(SqliteConnection connection)
        {
            RequireManifest("schemaVersion", ExpectedSchemaVersion.ToString());
            RequireManifest("parserVersion", ExpectedParserVersion);
            RequireManifest("entryCount", ExpectedEntryCount.ToString());
            RequireManifest("pkiSha1", ExpectedPkiSha1);
            RequireManifest("pkgSha1", ExpectedPkgSha1);
            LogicalSha256 = RequireManifestValue("logicalSha256");
            if (!string.Equals(LogicalSha256, ExpectedLogicalSha256, StringComparison.Ordinal))
                throw new InvalidDataException($"Authored snapshot logical hash mismatch expected={ExpectedLogicalSha256} actual={LogicalSha256}");
            RequireManifest("runtimeTextEntryCount", ExpectedRuntimeTextCount.ToString());
            RequireManifest("dictionaryTextEntryCount", "1");
            RequireManifest("supplementalTextEntryCount", "7");
            RequireManifest("collisionEntryCount", "1452");
            RequireManifest("collisionParsedEntryCount", "1452");
            RequireManifest("collisionWalkCellCount", "325963");
            RequireManifest("collisionBlockBucketCount", "346701");
            RequireManifest("collisionBlockRangeCount", "166186");
            RequireManifest("collisionReferenceCount", "1638");
            RequireManifest("collisionDistinctReferenceCount", "1334");
            RequireManifest("collisionResolvedReferenceCount", "1631");
            RequireManifest("collisionMissingReferenceCount", "7");
            RequireManifest("collisionDistinctResolvedReferenceCount", "1327");
            RequireManifest("collisionMissingNames", "barrel_stack,barrelcommon_3_stack,ruins_tatters_1_small,shad_floatingrocks_1,turd_stack03,turd_stack04,wrestling_ring_coil");
            RequireManifest("serverRelevantTypes", "4,11,12,13,14,15,16,17,19");
            RequireManifest("simulationTypes", "4,12,13,14,15,16");
            RequireManifest("serverMetadataTypes", "11,17,19");
            RequireManifest("packageRuntimeCoverage", "complete");
            RequireManifest("packageSyncCoverage", "complete");
            ManifestGcTextCount = ScalarInt(connection, "SELECT COUNT(*) FROM text_payloads t JOIN entries e ON e.entry_id=t.entry_id WHERE e.type_code=13");
            int entries = ScalarInt(connection, "SELECT COUNT(*) FROM entries");
            int distinctOrdinals = ScalarInt(connection, "SELECT COUNT(DISTINCT entry_index) FROM entries");
            int binaryCobj = ScalarInt(connection, "SELECT COUNT(*) FROM binary_payloads b JOIN entries e ON e.entry_id=b.entry_id WHERE e.type_code=4");
            int parsedCobj = ScalarInt(connection, "SELECT COUNT(*) FROM package_collision_coverage");
            int parsedCobjBytes = ScalarInt(connection, "SELECT COALESCE(SUM(bytes_consumed),0) FROM package_collision_coverage");
            int binaryCobjBytes = ScalarInt(connection, "SELECT COALESCE(SUM(length(b.data)),0) FROM binary_payloads b JOIN entries e ON e.entry_id=b.entry_id WHERE e.type_code=4");
            int tileDescriptions = ScalarInt(connection, "SELECT COUNT(*) FROM text_payloads t JOIN entries e ON e.entry_id=t.entry_id WHERE e.type_code=14");
            int worldDescriptions = ScalarInt(connection, "SELECT COUNT(*) FROM text_payloads t JOIN entries e ON e.entry_id=t.entry_id WHERE e.type_code=15");
            int zoneDescriptions = ScalarInt(connection, "SELECT COUNT(*) FROM text_payloads t JOIN entries e ON e.entry_id=t.entry_id WHERE e.type_code=16");
            if (entries != ExpectedEntryCount || distinctOrdinals != ExpectedEntryCount)
                throw new InvalidDataException($"Authored PKI ordinal coverage mismatch entries={entries} distinctOrdinals={distinctOrdinals}");
            if (ManifestGcTextCount != ExpectedGcTextCount || binaryCobj != 1452 || parsedCobj != binaryCobj || parsedCobjBytes != binaryCobjBytes || tileDescriptions != 1477 || worldDescriptions != 584 || zoneDescriptions != 575)
                throw new InvalidDataException($"Authored dataset incomplete gcText={ManifestGcTextCount} cobj={binaryCobj} parsedCobj={parsedCobj} parsedBytes={parsedCobjBytes}/{binaryCobjBytes} tile={tileDescriptions} world={worldDescriptions} zone={zoneDescriptions}");
        }

        private void LoadTextDocuments(SqliteConnection connection, int typeCode, List<PackageTextDocument> destination)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT e.entry_id,e.entry_index,e.name,e.type_code,e.text_sha1,e.decoded_sha1,e.source_virtual_path,t.text_value
                FROM entries e JOIN text_payloads t ON t.entry_id=e.entry_id
                WHERE e.type_code=@typeCode ORDER BY e.entry_index";
            command.Parameters.AddWithValue("@typeCode", typeCode);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                destination.Add(new PackageTextDocument
                {
                    EntryId = reader.GetInt32(0),
                    PackageId = 1,
                    EntryIndex = reader.GetInt32(1),
                    Name = reader.GetString(2),
                    TypeCode = reader.GetInt32(3),
                    TextSha1 = reader.GetString(4),
                    DecodedSha1 = reader.GetString(5),
                    SourceVirtualPath = reader.GetString(6),
                    Text = reader.GetString(7)
                });
            }
        }

        private void RequireManifest(string key, string expected)
        {
            string actual = RequireManifestValue(key);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Authored manifest mismatch key={key} expected={expected} actual={actual}");
        }

        private string RequireManifestValue(string key)
        {
            if (!_manifest.TryGetValue(key, out string value) || string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException($"Authored manifest key is missing: {key}");
            return value;
        }

        private static int ScalarInt(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return checked(Convert.ToInt32(command.ExecuteScalar()));
        }
    }
}
