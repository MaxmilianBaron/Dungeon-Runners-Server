using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DungeonRunners.Engine;
using Mono.Data.Sqlite;

namespace DungeonRunners.Database
{
    public static class GameDatabase
    {
        private const int RuntimeSchemaVersion = 4;
        private const int AuthoredSchemaVersion = 4;
        private static string _runtimeDbPath;
        private static string _authoredDbPath;
        private static string _cacheDbPath;
        private static string _connectionString;
        private static readonly object InitializationGate = new object();
        private static bool _initialized = false;
        private static readonly string[] RuntimeTables =
        {
            "accounts", "character_checkpoints", "character_equipment", "character_inventory",
            "character_modifiers", "character_quests", "character_skills", "characters",
            "completed_quests", "dropped_items", "pending_admin_actions", "pending_item_grants",
            "posses", "quest_objectives", "schema_migrations", "server_settings",
            "social_friends_v2", "social_ignores_v2", "world_entity_activations",
            "pvp_match_results", "pvp_participant_results"
        };
        private static readonly string[] AuthoredTables =
        {
            "authored_documents", "binary_payloads", "entries", "gc_extends_references",
            "gc_nodes", "gc_properties", "manifest", "package_collision_coverage",
            "package_collision_references", "package_type_coverage", "text_payloads", "type_counts"
        };
        private static readonly string[] CacheTables =
        {
            "item_resolved_mods", "item_stat_metadata", "item_wire_mods", "pathmap_nodes", "pathmap_zones"
        };

        public static string DbPath => _runtimeDbPath;
        public static string RuntimeDbPath => _runtimeDbPath;
        public static string AuthoredDbPath => _authoredDbPath;
        public static string CacheDbPath => _cacheDbPath;

        public static void Initialize()
        {
            if (_initialized) return;
            lock (InitializationGate)
            {
                if (_initialized) return;
                _runtimeDbPath = DungeonRunners.Core.DataPaths.DatabaseFile("runtime.db");
                _authoredDbPath = DungeonRunners.Core.DataPaths.DatabaseFile("authored.db");
                _cacheDbPath = DungeonRunners.Core.DataPaths.DatabaseFile("cache.db");
                string dir = Path.GetDirectoryName(_runtimeDbPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                RequireDatabase(_authoredDbPath, "authored");
                RequireDatabase(_cacheDbPath, "cache");
                var connectionStringBuilder = new SqliteConnectionStringBuilder
                {
                    Uri = "file:" + _runtimeDbPath,
                    Version = 3,
                    JournalMode = SQLiteJournalModeEnum.Delete,
                    SyncMode = SynchronizationModes.Full,
                    DefaultTimeout = 5,
                    Pooling = false
                };
                _connectionString = connectionStringBuilder.ConnectionString;
                ConfigureRuntimeDatabase();
                Debug.LogError($"[DB] runtime='{_runtimeDbPath}' authored='{_authoredDbPath}' cache='{_cacheDbPath}'");
                CreatePlayerTables();
                using (var connection = OpenConnection())
                {
                    ValidateTopology(connection);
                    ExecuteNonQuery(connection, "PRAGMA optimize");
                }
                _initialized = true;
                Debug.LogError("[DB] SQLite initialized stores=runtime,authored,cache");
            }
        }

        public static SqliteConnection GetConnection()
        {
            Initialize();
            return OpenConnection();
        }

        private static SqliteConnection OpenConnection()
        {
            if (string.IsNullOrWhiteSpace(_connectionString))
                throw new InvalidOperationException("Database connection string is unavailable");
            var connection = new SqliteConnection(_connectionString);
            try
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "PRAGMA foreign_keys=ON";
                    command.ExecuteNonQuery();
                    command.CommandText = "PRAGMA synchronous=FULL";
                    command.ExecuteNonQuery();
                    command.CommandText = "PRAGMA busy_timeout=5000";
                    command.ExecuteNonQuery();
                    command.CommandText = "ATTACH DATABASE @authoredPath AS authored";
                    command.Parameters.AddWithValue("@authoredPath", _authoredDbPath);
                    command.ExecuteNonQuery();
                    command.Parameters.Clear();
                    command.CommandText = "ATTACH DATABASE @cachePath AS cache";
                    command.Parameters.AddWithValue("@cachePath", _cacheDbPath);
                    command.ExecuteNonQuery();
                }
                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        private static void RequireDatabase(string path, string role)
        {
            if (!File.Exists(path))
                throw new InvalidDataException($"Required {role} database is missing: {path}");
        }

        private static void ConfigureRuntimeDatabase()
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            string journalMode = Convert.ToString(ExecuteScalar(connection, "PRAGMA journal_mode=DELETE"));
            if (!string.Equals(journalMode, "delete", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Runtime database rollback journal unavailable result={journalMode}");
            ExecuteNonQuery(connection, "PRAGMA synchronous=FULL");
            ExecuteNonQuery(connection, "PRAGMA busy_timeout=5000");
        }

        private static void ValidateTopology(SqliteConnection connection)
        {
            ValidateIntegrity(connection, "main");
            ValidateIntegrity(connection, "authored");
            ValidateIntegrity(connection, "cache");
            ValidateTableSet(connection, "main", RuntimeTables);
            ValidateTableSet(connection, "authored", AuthoredTables);
            ValidateTableSet(connection, "cache", CacheTables);
            ValidateRequiredColumns(connection, "main", "accounts", "id", "username", "password_hash", "salt", "is_member", "is_banned", "is_admin", "current_character_id");
            ValidateRequiredColumns(connection, "main", "characters", "id", "account_id", "name", "class_name", "current_zone", "zone_id", "position_fixed_x", "position_fixed_y", "position_fixed_z", "current_hp", "current_mana", "max_hp", "max_mana", "has_pvp_rating", "monster_difficulty", "minimum_item_quality");
            ValidateRequiredColumns(connection, "main", "character_inventory", "id", "character_id", "gc_class", "container_id", "item_order", "generated_item_modifiers", "soul_bound", "no_sell", "soul_bound_countdown");
            ValidateRequiredColumns(connection, "main", "character_equipment", "id", "character_id", "slot", "gc_class", "generated_item_modifiers", "soul_bound", "no_sell", "soul_bound_countdown");
            ValidateRequiredColumns(connection, "main", "dropped_items", "id", "gc_class", "quantity", "gold_amount", "generated_by_bling_gnome", "heading_fixed", "quest_binding_key", "activation_key", "generated_item_modifiers", "soul_bound", "no_sell", "soul_bound_countdown", "owner_group_id");
            ValidateRequiredColumns(connection, "main", "world_entity_activations", "activation_key", "character_id", "zone", "zone_id", "instance_id", "entity_id", "outcome_json", "activated_at");
            ValidateRequiredColumns(connection, "main", "pvp_match_results", "result_id", "match_id", "archetype", "has_winner", "participant_count", "created_at");
            ValidateRequiredColumns(connection, "main", "pvp_participant_results", "result_id", "participant_order", "character_id", "login_name", "is_winner", "is_ranked", "rating_before", "rating_after", "persisted_rating_before", "apply_rating", "wins_before", "wins_delta", "reward_gc_class", "reward_count", "reward_width", "reward_height", "reward_rarity", "return_zone", "return_zone_id", "return_pos_fixed_x", "return_pos_fixed_y", "return_pos_fixed_z", "applied_at", "return_completed_at", "pending_grant_id");
            ValidateRequiredColumns(connection, "authored", "entries", "entry_id", "entry_index", "type_code", "type_ordinal", "name", "package_crc", "stored_size", "package_offset", "decoded_size", "flags", "native_words", "decoded_sha1", "source_virtual_path");
            ValidateRequiredColumns(connection, "authored", "text_payloads", "entry_id", "text_value");
            ValidateRequiredColumns(connection, "authored", "binary_payloads", "entry_id", "data");
            ValidateRequiredColumns(connection, "authored", "authored_documents", "entry_id", "type_code", "document_kind", "canonical_path", "root_name", "root_extends", "node_count", "property_count");
            ValidateRequiredColumns(connection, "authored", "gc_nodes", "node_id", "entry_id", "parent_node_id", "node_ordinal", "child_ordinal", "canonical_path", "name", "extends_path", "is_static", "is_anonymous");
            ValidateRequiredColumns(connection, "authored", "gc_properties", "node_id", "property_ordinal", "name", "value");
            ValidateRequiredColumns(connection, "authored", "gc_extends_references", "node_id", "extends_path", "resolution_kind", "resolved_node_id");
            ValidateRequiredColumns(connection, "authored", "package_type_coverage", "type_code", "entry_count", "server_role", "payload_mode");
            ValidateRequiredColumns(connection, "authored", "package_collision_coverage", "entry_id", "body_offset", "bytes_consumed", "walk_grid_x", "walk_grid_y", "block_grid_x", "block_grid_y", "block_grid_z", "block_bucket_count", "block_range_count");
            ValidateRequiredColumns(connection, "authored", "package_collision_references", "node_id", "property_ordinal", "collision_object", "normalized_name", "resolved_entry_id", "resolution_kind");
            ValidateRequiredColumns(connection, "cache", "pathmap_zones", "zone_name", "walkable_nodes");
            ValidateRequiredColumns(connection, "cache", "pathmap_nodes", "zone_name");
            string parserVersion = Convert.ToString(ExecuteScalar(connection, "SELECT value FROM authored.manifest WHERE key='parserVersion'"));
            string pkiSha1 = Convert.ToString(ExecuteScalar(connection, "SELECT value FROM authored.manifest WHERE key='pkiSha1'"));
            string pkgSha1 = Convert.ToString(ExecuteScalar(connection, "SELECT value FROM authored.manifest WHERE key='pkgSha1'"));
            string logicalSha256 = Convert.ToString(ExecuteScalar(connection, "SELECT value FROM authored.manifest WHERE key='logicalSha256'"));
            string runtimeTextEntryCount = Convert.ToString(ExecuteScalar(connection, "SELECT value FROM authored.manifest WHERE key='runtimeTextEntryCount'"));
            string dictionaryTextEntryCount = Convert.ToString(ExecuteScalar(connection, "SELECT value FROM authored.manifest WHERE key='dictionaryTextEntryCount'"));
            string supplementalTextEntryCount = Convert.ToString(ExecuteScalar(connection, "SELECT value FROM authored.manifest WHERE key='supplementalTextEntryCount'"));
            string collisionEntryCount = Convert.ToString(ExecuteScalar(connection, "SELECT value FROM authored.manifest WHERE key='collisionEntryCount'"));
            string collisionParsedEntryCount = Convert.ToString(ExecuteScalar(connection, "SELECT value FROM authored.manifest WHERE key='collisionParsedEntryCount'"));
            string collisionWalkCellCount = Convert.ToString(ExecuteScalar(connection, "SELECT value FROM authored.manifest WHERE key='collisionWalkCellCount'"));
            string collisionBlockBucketCount = Convert.ToString(ExecuteScalar(connection, "SELECT value FROM authored.manifest WHERE key='collisionBlockBucketCount'"));
            string collisionBlockRangeCount = Convert.ToString(ExecuteScalar(connection, "SELECT value FROM authored.manifest WHERE key='collisionBlockRangeCount'"));
            string collisionReferenceCount = Convert.ToString(ExecuteScalar(connection, "SELECT value FROM authored.manifest WHERE key='collisionReferenceCount'"));
            string collisionDistinctReferenceCount = Convert.ToString(ExecuteScalar(connection, "SELECT value FROM authored.manifest WHERE key='collisionDistinctReferenceCount'"));
            string collisionResolvedReferenceCount = Convert.ToString(ExecuteScalar(connection, "SELECT value FROM authored.manifest WHERE key='collisionResolvedReferenceCount'"));
            string collisionMissingReferenceCount = Convert.ToString(ExecuteScalar(connection, "SELECT value FROM authored.manifest WHERE key='collisionMissingReferenceCount'"));
            string collisionDistinctResolvedReferenceCount = Convert.ToString(ExecuteScalar(connection, "SELECT value FROM authored.manifest WHERE key='collisionDistinctResolvedReferenceCount'"));
            string collisionMissingNames = Convert.ToString(ExecuteScalar(connection, "SELECT value FROM authored.manifest WHERE key='collisionMissingNames'"));
            string serverRelevantTypes = Convert.ToString(ExecuteScalar(connection, "SELECT value FROM authored.manifest WHERE key='serverRelevantTypes'"));
            string simulationTypes = Convert.ToString(ExecuteScalar(connection, "SELECT value FROM authored.manifest WHERE key='simulationTypes'"));
            string serverMetadataTypes = Convert.ToString(ExecuteScalar(connection, "SELECT value FROM authored.manifest WHERE key='serverMetadataTypes'"));
            string textTypes = Convert.ToString(ExecuteScalar(connection, "SELECT value FROM authored.manifest WHERE key='textTypes'"));
            string binaryTypes = Convert.ToString(ExecuteScalar(connection, "SELECT value FROM authored.manifest WHERE key='binaryTypes'"));
            string packageRuntimeCoverage = Convert.ToString(ExecuteScalar(connection, "SELECT value FROM authored.manifest WHERE key='packageRuntimeCoverage'"));
            string packageSyncCoverage = Convert.ToString(ExecuteScalar(connection, "SELECT value FROM authored.manifest WHERE key='packageSyncCoverage'"));
            int authoredDocumentCount = Convert.ToInt32(ExecuteScalar(connection, "SELECT value FROM authored.manifest WHERE key='authoredDocumentCount'"));
            int authoredNodeCount = Convert.ToInt32(ExecuteScalar(connection, "SELECT value FROM authored.manifest WHERE key='authoredNodeCount'"));
            int authoredPropertyCount = Convert.ToInt32(ExecuteScalar(connection, "SELECT value FROM authored.manifest WHERE key='authoredPropertyCount'"));
            int authoredExtendsCount = Convert.ToInt32(ExecuteScalar(connection, "SELECT value FROM authored.manifest WHERE key='authoredExtendsCount'"));
            int authoredMissingExtendsCount = Convert.ToInt32(ExecuteScalar(connection, "SELECT value FROM authored.manifest WHERE key='authoredMissingExtendsCount'"));
            int authoredAmbiguousExtendsCount = Convert.ToInt32(ExecuteScalar(connection, "SELECT value FROM authored.manifest WHERE key='authoredAmbiguousExtendsCount'"));
            int entryCount = Convert.ToInt32(ExecuteScalar(connection, "SELECT COUNT(*) FROM authored.entries"));
            int distinctOrdinals = Convert.ToInt32(ExecuteScalar(connection, "SELECT COUNT(DISTINCT entry_index) FROM authored.entries"));
            int firstOrdinal = Convert.ToInt32(ExecuteScalar(connection, "SELECT MIN(entry_index) FROM authored.entries"));
            int lastOrdinal = Convert.ToInt32(ExecuteScalar(connection, "SELECT MAX(entry_index) FROM authored.entries"));
            int textPayloads = Convert.ToInt32(ExecuteScalar(connection, "SELECT COUNT(*) FROM authored.text_payloads"));
            int binaryPayloads = Convert.ToInt32(ExecuteScalar(connection, "SELECT COUNT(*) FROM authored.binary_payloads"));
            int typeCountRows = Convert.ToInt32(ExecuteScalar(connection, "SELECT COUNT(*) FROM authored.type_counts"));
            int declaredEntries = Convert.ToInt32(ExecuteScalar(connection, "SELECT COALESCE(SUM(entry_count),0) FROM authored.type_counts"));
            if (!string.Equals(parserVersion, "direct-pki-v3-v1", StringComparison.Ordinal)
                || !string.Equals(pkiSha1, "09b7cc6479dba96dbc8090f65a0f5720de605329", StringComparison.Ordinal)
                || !string.Equals(pkgSha1, "6969271759ab427d47b3b05c9f8fcf4f6a283d14", StringComparison.Ordinal)
                || !string.Equals(logicalSha256, "665d153a6a11ad32541637d482f90ce77527c88dd08d1cc60f48bc73286534bd", StringComparison.Ordinal)
                || !string.Equals(runtimeTextEntryCount, "11795", StringComparison.Ordinal)
                || !string.Equals(dictionaryTextEntryCount, "1", StringComparison.Ordinal)
                || !string.Equals(supplementalTextEntryCount, "7", StringComparison.Ordinal)
                || !string.Equals(collisionEntryCount, "1452", StringComparison.Ordinal)
                || !string.Equals(collisionParsedEntryCount, "1452", StringComparison.Ordinal)
                || !string.Equals(collisionWalkCellCount, "325963", StringComparison.Ordinal)
                || !string.Equals(collisionBlockBucketCount, "346701", StringComparison.Ordinal)
                || !string.Equals(collisionBlockRangeCount, "166186", StringComparison.Ordinal)
                || !string.Equals(collisionReferenceCount, "1638", StringComparison.Ordinal)
                || !string.Equals(collisionDistinctReferenceCount, "1334", StringComparison.Ordinal)
                || !string.Equals(collisionResolvedReferenceCount, "1631", StringComparison.Ordinal)
                || !string.Equals(collisionMissingReferenceCount, "7", StringComparison.Ordinal)
                || !string.Equals(collisionDistinctResolvedReferenceCount, "1327", StringComparison.Ordinal)
                || !string.Equals(collisionMissingNames, "barrel_stack,barrelcommon_3_stack,ruins_tatters_1_small,shad_floatingrocks_1,turd_stack03,turd_stack04,wrestling_ring_coil", StringComparison.Ordinal)
                || !string.Equals(serverRelevantTypes, "4,11,12,13,14,15,16,17,19", StringComparison.Ordinal)
                || !string.Equals(simulationTypes, "4,12,13,14,15,16", StringComparison.Ordinal)
                || !string.Equals(serverMetadataTypes, "11,17,19", StringComparison.Ordinal)
                || !string.Equals(textTypes, "11,12,13,14,15,16,17,19", StringComparison.Ordinal)
                || !string.Equals(binaryTypes, "4", StringComparison.Ordinal)
                || !string.Equals(packageRuntimeCoverage, "complete", StringComparison.Ordinal)
                || !string.Equals(packageSyncCoverage, "complete", StringComparison.Ordinal)
                || entryCount != 30893
                || distinctOrdinals != entryCount
                || firstOrdinal != 0
                || lastOrdinal != entryCount - 1
                || typeCountRows != 21
                || declaredEntries != entryCount
                || textPayloads != 11803
                || binaryPayloads != 1452)
                throw new InvalidDataException($"Authored package snapshot is incomplete parser={parserVersion} pkiSha1={pkiSha1} pkgSha1={pkgSha1} logicalSha256={logicalSha256} entries={entryCount}/{declaredEntries} distinctOrdinals={distinctOrdinals} range={firstOrdinal}..{lastOrdinal} types={typeCountRows} textPayloads={textPayloads} binaryPayloads={binaryPayloads} runtimeText={runtimeTextEntryCount} dictionaryText={dictionaryTextEntryCount} supplementalText={supplementalTextEntryCount} collision={collisionEntryCount}/{collisionParsedEntryCount} collisionCells={collisionWalkCellCount}/{collisionBlockBucketCount}/{collisionBlockRangeCount} serverRelevantTypes={serverRelevantTypes} simulationTypes={simulationTypes} serverMetadataTypes={serverMetadataTypes} textTypes={textTypes} binaryTypes={binaryTypes} runtimeCoverage={packageRuntimeCoverage} syncCoverage={packageSyncCoverage}");
            int coverageRows = Convert.ToInt32(ExecuteScalar(connection, "SELECT COUNT(*) FROM authored.package_type_coverage"));
            int coverageEntries = Convert.ToInt32(ExecuteScalar(connection, "SELECT COALESCE(SUM(entry_count),0) FROM authored.package_type_coverage"));
            int coverageCountMismatches = Convert.ToInt32(ExecuteScalar(connection, "SELECT COUNT(*) FROM authored.package_type_coverage c JOIN authored.type_counts t ON t.type_code=c.type_code WHERE c.entry_count<>t.entry_count"));
            int payloadCoverageMismatches = Convert.ToInt32(ExecuteScalar(connection, "SELECT COUNT(*) FROM authored.package_type_coverage c WHERE (c.payload_mode='text' AND c.entry_count<>(SELECT COUNT(*) FROM authored.entries e JOIN authored.text_payloads p ON p.entry_id=e.entry_id WHERE e.type_code=c.type_code)) OR (c.payload_mode='binary' AND c.entry_count<>(SELECT COUNT(*) FROM authored.entries e JOIN authored.binary_payloads p ON p.entry_id=e.entry_id WHERE e.type_code=c.type_code)) OR (c.payload_mode='index' AND EXISTS(SELECT 1 FROM authored.entries e LEFT JOIN authored.text_payloads t ON t.entry_id=e.entry_id LEFT JOIN authored.binary_payloads b ON b.entry_id=e.entry_id WHERE e.type_code=c.type_code AND (t.entry_id IS NOT NULL OR b.entry_id IS NOT NULL)))"));
            if (coverageRows != 21 || coverageEntries != entryCount || coverageCountMismatches != 0 || payloadCoverageMismatches != 0)
                throw new InvalidDataException($"Authored package type coverage is invalid rows={coverageRows}/21 entries={coverageEntries}/{entryCount} countMismatches={coverageCountMismatches} payloadMismatches={payloadCoverageMismatches}");
            int collisionCoverageRows = Convert.ToInt32(ExecuteScalar(connection, "SELECT COUNT(*) FROM authored.package_collision_coverage"));
            long collisionCoverageBytes = Convert.ToInt64(ExecuteScalar(connection, "SELECT COALESCE(SUM(bytes_consumed),0) FROM authored.package_collision_coverage"));
            long collisionPayloadBytes = Convert.ToInt64(ExecuteScalar(connection, "SELECT COALESCE(SUM(length(data)),0) FROM authored.binary_payloads"));
            long collisionWalkCells = Convert.ToInt64(ExecuteScalar(connection, "SELECT COALESCE(SUM(walk_grid_x*walk_grid_y),0) FROM authored.package_collision_coverage"));
            long collisionBlockBuckets = Convert.ToInt64(ExecuteScalar(connection, "SELECT COALESCE(SUM(block_bucket_count),0) FROM authored.package_collision_coverage"));
            long collisionBlockRanges = Convert.ToInt64(ExecuteScalar(connection, "SELECT COALESCE(SUM(block_range_count),0) FROM authored.package_collision_coverage"));
            int collisionOrphans = Convert.ToInt32(ExecuteScalar(connection, "SELECT COUNT(*) FROM authored.package_collision_coverage c LEFT JOIN authored.entries e ON e.entry_id=c.entry_id WHERE e.entry_id IS NULL OR e.type_code<>4"));
            if (collisionCoverageRows != 1452 || collisionCoverageBytes != collisionPayloadBytes || collisionWalkCells != 325963L || collisionBlockBuckets != 346701L || collisionBlockRanges != 166186L || collisionOrphans != 0)
                throw new InvalidDataException($"Authored collision coverage is invalid rows={collisionCoverageRows}/1452 bytes={collisionCoverageBytes}/{collisionPayloadBytes} walkCells={collisionWalkCells}/325963 blockBuckets={collisionBlockBuckets}/346701 blockRanges={collisionBlockRanges}/166186 orphans={collisionOrphans}");
            int collisionReferences = Convert.ToInt32(ExecuteScalar(connection, "SELECT COUNT(*) FROM authored.package_collision_references"));
            int collisionDistinctReferences = Convert.ToInt32(ExecuteScalar(connection, "SELECT COUNT(DISTINCT normalized_name) FROM authored.package_collision_references"));
            int collisionResolvedReferences = Convert.ToInt32(ExecuteScalar(connection, "SELECT COUNT(*) FROM authored.package_collision_references WHERE resolution_kind='resolved' AND resolved_entry_id IS NOT NULL"));
            int collisionMissingReferences = Convert.ToInt32(ExecuteScalar(connection, "SELECT COUNT(*) FROM authored.package_collision_references WHERE resolution_kind='package-missing' AND resolved_entry_id IS NULL"));
            int collisionInvalidReferences = Convert.ToInt32(ExecuteScalar(connection, "SELECT COUNT(*) FROM authored.package_collision_references WHERE (resolution_kind='resolved')<>(resolved_entry_id IS NOT NULL) OR resolution_kind NOT IN ('resolved','package-missing')"));
            if (collisionReferences != 1638 || collisionDistinctReferences != 1334 || collisionResolvedReferences != 1631 || collisionMissingReferences != 7 || collisionInvalidReferences != 0)
                throw new InvalidDataException($"Authored collision references are invalid total={collisionReferences}/1638 distinct={collisionDistinctReferences}/1334 resolved={collisionResolvedReferences}/1631 packageMissing={collisionMissingReferences}/7 invalid={collisionInvalidReferences}");
            int materializedDocuments = Convert.ToInt32(ExecuteScalar(connection, "SELECT COUNT(*) FROM authored.authored_documents"));
            int materializedNodes = Convert.ToInt32(ExecuteScalar(connection, "SELECT COUNT(*) FROM authored.gc_nodes"));
            int materializedProperties = Convert.ToInt32(ExecuteScalar(connection, "SELECT COUNT(*) FROM authored.gc_properties"));
            int materializedExtends = Convert.ToInt32(ExecuteScalar(connection, "SELECT COUNT(*) FROM authored.gc_extends_references"));
            int declaredDocumentNodes = Convert.ToInt32(ExecuteScalar(connection, "SELECT COALESCE(SUM(node_count),0) FROM authored.authored_documents"));
            int declaredDocumentProperties = Convert.ToInt32(ExecuteScalar(connection, "SELECT COALESCE(SUM(property_count),0) FROM authored.authored_documents"));
            int uncoveredRuntimeDocuments = Convert.ToInt32(ExecuteScalar(connection, "SELECT COUNT(*) FROM authored.entries e LEFT JOIN authored.authored_documents d ON d.entry_id=e.entry_id WHERE e.type_code IN (13,14,15,16) AND d.entry_id IS NULL"));
            int unexpectedMaterializedDocuments = Convert.ToInt32(ExecuteScalar(connection, "SELECT COUNT(*) FROM authored.authored_documents d JOIN authored.entries e ON e.entry_id=d.entry_id WHERE e.type_code NOT IN (13,14,15,16)"));
            int unresolvedExtends = Convert.ToInt32(ExecuteScalar(connection, "SELECT COUNT(*) FROM authored.gc_extends_references WHERE resolution_kind IN ('missing','ambiguous')"));
            if (authoredDocumentCount != 11795
                || materializedDocuments != authoredDocumentCount
                || authoredNodeCount != materializedNodes
                || authoredPropertyCount != materializedProperties
                || authoredExtendsCount != materializedExtends
                || declaredDocumentNodes != materializedNodes
                || declaredDocumentProperties != materializedProperties
                || authoredMissingExtendsCount != 0
                || authoredAmbiguousExtendsCount != 0
                || uncoveredRuntimeDocuments != 0
                || unexpectedMaterializedDocuments != 0
                || unresolvedExtends != 0)
                throw new InvalidDataException($"Authored GC graph is incomplete documents={materializedDocuments}/{authoredDocumentCount} nodes={materializedNodes}/{authoredNodeCount}/{declaredDocumentNodes} properties={materializedProperties}/{authoredPropertyCount}/{declaredDocumentProperties} extends={materializedExtends}/{authoredExtendsCount} missing={authoredMissingExtendsCount} ambiguous={authoredAmbiguousExtendsCount} uncoveredRuntimeDocuments={uncoveredRuntimeDocuments} unexpectedDocuments={unexpectedMaterializedDocuments} unresolved={unresolvedExtends}");
            int runtimeForeignKeyFailures = Convert.ToInt32(ExecuteScalar(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check"));
            int authoredForeignKeyFailures = Convert.ToInt32(ExecuteScalar(connection, "SELECT COUNT(*) FROM authored.pragma_foreign_key_check"));
            int cacheForeignKeyFailures = Convert.ToInt32(ExecuteScalar(connection, "SELECT COUNT(*) FROM cache.pragma_foreign_key_check"));
            if (runtimeForeignKeyFailures != 0 || authoredForeignKeyFailures != 0 || cacheForeignKeyFailures != 0)
                throw new InvalidDataException($"Database foreign key violations runtime={runtimeForeignKeyFailures} authored={authoredForeignKeyFailures} cache={cacheForeignKeyFailures}");
            int runtimeSchemaVersion = Convert.ToInt32(ExecuteScalar(connection, "PRAGMA main.user_version"));
            int authoredSchemaVersion = Convert.ToInt32(ExecuteScalar(connection, "PRAGMA authored.user_version"));
            int cacheSchemaVersion = Convert.ToInt32(ExecuteScalar(connection, "PRAGMA cache.user_version"));
            if (runtimeSchemaVersion != RuntimeSchemaVersion || authoredSchemaVersion != AuthoredSchemaVersion || cacheSchemaVersion != 1)
                throw new InvalidDataException($"Database schema versions invalid runtime={runtimeSchemaVersion}/{RuntimeSchemaVersion} authored={authoredSchemaVersion}/{AuthoredSchemaVersion} cache={cacheSchemaVersion}/1");
            string runtimeJournal = Convert.ToString(ExecuteScalar(connection, "PRAGMA main.journal_mode"));
            string authoredJournal = Convert.ToString(ExecuteScalar(connection, "PRAGMA authored.journal_mode"));
            string cacheJournal = Convert.ToString(ExecuteScalar(connection, "PRAGMA cache.journal_mode"));
            if (!string.Equals(runtimeJournal, "delete", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(authoredJournal, "delete", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(cacheJournal, "wal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Database journal modes invalid runtime={runtimeJournal} authored={authoredJournal} cache={cacheJournal}");
            ValidateRuntimeReferences(connection);
        }

        public static void ValidateDerivedCaches()
        {
            using var connection = GetConnection();
            int cacheItemMetadata = Convert.ToInt32(ExecuteScalar(connection, "SELECT COUNT(*) FROM cache.sqlite_master WHERE type='table' AND name='item_stat_metadata'"));
            int cacheResolvedModTable = Convert.ToInt32(ExecuteScalar(connection, "SELECT COUNT(*) FROM cache.sqlite_master WHERE type='table' AND name='item_resolved_mods'"));
            int cacheWireModTable = Convert.ToInt32(ExecuteScalar(connection, "SELECT COUNT(*) FROM cache.sqlite_master WHERE type='table' AND name='item_wire_mods'"));
            if (cacheItemMetadata != 1 || cacheResolvedModTable != 1 || cacheWireModTable != 1)
                throw new InvalidDataException($"Derived item cache topology is incomplete metadata={cacheItemMetadata} resolvedMods={cacheResolvedModTable} wireMods={cacheWireModTable}");
            int cacheItemSchemaVersion = Convert.ToInt32(ExecuteScalar(connection, "SELECT value FROM cache.item_stat_metadata WHERE key='schema_version'"));
            int cacheResolvedMods = Convert.ToInt32(ExecuteScalar(connection, "SELECT COUNT(*) FROM cache.item_resolved_mods"));
            int cacheWireModRows = Convert.ToInt32(ExecuteScalar(connection, "SELECT COUNT(*) FROM cache.item_wire_mods"));
            if (cacheItemSchemaVersion != 3 || cacheResolvedMods == 0 || cacheWireModRows == 0)
                throw new InvalidDataException($"Derived item cache is incomplete schema={cacheItemSchemaVersion} resolvedMods={cacheResolvedMods} wireMods={cacheWireModRows}");
            int cachePathMapNodes = Convert.ToInt32(ExecuteScalar(connection, "SELECT COUNT(*) FROM cache.pathmap_nodes"));
            int cachePathMapDeclaredNodes = Convert.ToInt32(ExecuteScalar(connection, "SELECT COALESCE(SUM(walkable_nodes),0) FROM cache.pathmap_zones"));
            if (cachePathMapNodes == 0 || cachePathMapNodes != cachePathMapDeclaredNodes)
                throw new InvalidDataException($"Derived pathmap cache is incomplete nodes={cachePathMapNodes} declaredNodes={cachePathMapDeclaredNodes}");
        }

        private static void ValidateTableSet(SqliteConnection connection, string schema, IEnumerable<string> expectedTables)
        {
            var expected = new HashSet<string>(expectedTables, StringComparer.OrdinalIgnoreCase);
            var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"SELECT name FROM {QuoteIdentifier(schema)}.sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    actual.Add(Convert.ToString(reader.GetValue(0)));
            }
            var missing = new List<string>();
            foreach (string table in expected)
                if (!actual.Contains(table))
                    missing.Add(table);
            var unexpected = new List<string>();
            foreach (string table in actual)
                if (!expected.Contains(table))
                    unexpected.Add(table);
            if (missing.Count != 0 || unexpected.Count != 0)
                throw new InvalidDataException($"Database role table set invalid schema={schema} missing={string.Join(",", missing)} unexpected={string.Join(",", unexpected)}");
        }

        private static void ValidateRequiredColumns(SqliteConnection connection, string schema, string table, params string[] requiredColumns)
        {
            var missing = new HashSet<string>(requiredColumns, StringComparer.OrdinalIgnoreCase);
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"PRAGMA {QuoteIdentifier(schema)}.table_info({QuoteIdentifier(table)})";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    missing.Remove(Convert.ToString(reader["name"]));
            }
            if (missing.Count != 0)
                throw new InvalidDataException($"Database table columns incomplete schema={schema} table={table} missing={string.Join(",", missing)}");
        }

        private static void ValidateRuntimeReferences(SqliteConnection connection)
        {
            int invalidCurrentCharacters = Convert.ToInt32(ExecuteScalar(connection,
                "SELECT COUNT(*) FROM accounts a LEFT JOIN characters c ON c.id=a.current_character_id WHERE a.current_character_id IS NULL OR (a.current_character_id<>0 AND (c.id IS NULL OR c.account_id<>a.id))"));
            int orphanModifiers = Convert.ToInt32(ExecuteScalar(connection,
                "SELECT COUNT(*) FROM character_modifiers m LEFT JOIN characters c ON c.id=m.character_id WHERE c.id IS NULL"));
            int invalidModifierRows = Convert.ToInt32(ExecuteScalar(connection,
                "SELECT COUNT(*) FROM character_modifiers WHERE COALESCE(trim(gc_type),'')='' OR length(gc_type)>1024 OR modifier_id IS NULL OR modifier_id<1 OR modifier_id>536870911 OR modifier_id BETWEEN 61440 AND 65535 OR level IS NULL OR level<0 OR level>255 OR power_level IS NULL OR power_level<0 OR power_level>2147483647 OR duration_remaining IS NULL OR duration_remaining<0 OR duration_remaining>2147483647 OR source_is_self IS NULL OR source_is_self NOT IN (0,1)"));
            int orphanPendingGrants = Convert.ToInt32(ExecuteScalar(connection,
                "SELECT COUNT(*) FROM pending_item_grants g LEFT JOIN characters c ON c.id=g.character_id WHERE c.id IS NULL"));
            int orphanPendingAdminActions = Convert.ToInt32(ExecuteScalar(connection,
                "SELECT COUNT(*) FROM pending_admin_actions a LEFT JOIN characters c ON c.id=a.character_id WHERE c.id IS NULL"));
            int orphanPosseLinks = Convert.ToInt32(ExecuteScalar(connection,
                "SELECT COUNT(*) FROM characters c LEFT JOIN posses p ON p.id=c.posse_id WHERE c.posse_id IS NULL OR (c.posse_id<>0 AND p.id IS NULL)"));
            int orphanDropOwners = Convert.ToInt32(ExecuteScalar(connection,
                "SELECT COUNT(*) FROM dropped_items d LEFT JOIN characters c ON c.id=d.owner_character_id WHERE d.owner_character_id IS NULL OR (d.owner_character_id<>0 AND c.id IS NULL)"));
            int orphanWorldActivations = Convert.ToInt32(ExecuteScalar(connection,
                "SELECT COUNT(*) FROM world_entity_activations a LEFT JOIN characters c ON c.id=a.character_id WHERE c.id IS NULL"));
            int orphanWorldActivationDrops = Convert.ToInt32(ExecuteScalar(connection,
                "SELECT COUNT(*) FROM dropped_items d LEFT JOIN world_entity_activations a ON a.activation_key=d.activation_key WHERE COALESCE(trim(d.activation_key),'')<>'' AND a.activation_key IS NULL"));
            int orphanPvpParticipants = Convert.ToInt32(ExecuteScalar(connection,
                @"SELECT COUNT(*) FROM pvp_participant_results p
                  LEFT JOIN pvp_match_results m ON m.result_id=p.result_id
                  LEFT JOIN characters c ON c.id=p.character_id
                  LEFT JOIN accounts a ON a.id=c.account_id AND a.username=p.login_name COLLATE NOCASE
                  WHERE m.result_id IS NULL OR c.id IS NULL OR a.id IS NULL"));
            int invalidPvpHeaders = Convert.ToInt32(ExecuteScalar(connection,
                "SELECT COUNT(*) FROM pvp_match_results WHERE result_id IS NULL OR length(result_id)<>32 OR lower(result_id)<>result_id OR result_id GLOB '*[^0-9a-f]*' OR COALESCE(trim(match_id),'')='' OR length(match_id)>128 OR archetype IS NULL OR archetype<0 OR archetype>3 OR has_winner IS NULL OR has_winner NOT IN (0,1) OR participant_count IS NULL OR participant_count<1 OR participant_count>255"));
            int invalidPvpParticipants = Convert.ToInt32(ExecuteScalar(connection,
                @"SELECT COUNT(*) FROM pvp_participant_results p JOIN pvp_match_results m ON m.result_id=p.result_id
                  WHERE p.participant_order IS NULL OR p.participant_order<0 OR p.participant_order>=m.participant_count
                     OR p.character_id IS NULL OR p.character_id<=0 OR p.character_id>2147483647
                     OR COALESCE(trim(p.login_name),'')='' OR length(p.login_name)>256
                     OR p.is_winner IS NULL OR p.is_winner NOT IN (0,1)
                     OR p.is_ranked IS NULL OR p.is_ranked NOT IN (0,1)
                     OR p.rating_before IS NULL OR p.rating_before<0 OR p.rating_before>3000
                     OR p.rating_after IS NULL OR p.rating_after<0 OR p.rating_after>3000
                     OR p.persisted_rating_before IS NULL OR p.persisted_rating_before<0 OR p.persisted_rating_before>3000
                     OR p.apply_rating IS NULL OR p.apply_rating NOT IN (0,1)
                     OR (p.apply_rating=1 AND (p.is_ranked=0 OR m.has_winner=0))
                     OR (p.apply_rating=0 AND p.rating_after<>p.rating_before)
                     OR p.wins_before IS NULL OR p.wins_before<0 OR p.wins_before>2147483647
                     OR p.wins_delta IS NULL OR p.wins_delta NOT IN (0,1) OR p.wins_before>2147483647-p.wins_delta
                     OR p.is_winner<>p.wins_delta
                     OR p.reward_gc_class IS NULL OR p.reward_count IS NULL OR p.reward_width IS NULL OR p.reward_height IS NULL OR p.reward_rarity IS NULL
                     OR (p.reward_count=0 AND (p.reward_gc_class<>'' OR p.reward_width<>0 OR p.reward_height<>0 OR p.reward_rarity<>-1))
                     OR (p.reward_count<>0 AND (m.has_winner=0 OR p.is_ranked=0 OR COALESCE(trim(p.reward_gc_class),'')='' OR length(p.reward_gc_class)>1024 OR p.reward_count<1 OR p.reward_count>255 OR p.reward_width<1 OR p.reward_width>10 OR p.reward_height<1 OR p.reward_height>8 OR p.reward_rarity< -1 OR p.reward_rarity>5))
                     OR COALESCE(trim(p.return_zone),'')='' OR length(p.return_zone)>1024 OR p.return_zone_id IS NULL
                     OR p.return_pos_fixed_x IS NULL OR p.return_pos_fixed_y IS NULL OR p.return_pos_fixed_z IS NULL
                     OR (p.return_completed_at IS NOT NULL AND p.applied_at IS NULL)
                     OR p.pending_grant_id IS NULL OR p.pending_grant_id<0
                     OR (p.applied_at IS NOT NULL AND p.reward_count>0 AND p.pending_grant_id<=0)
                     OR (p.reward_count=0 AND p.pending_grant_id<>0)"));
            int invalidPvpCardinality = Convert.ToInt32(ExecuteScalar(connection,
                @"SELECT COUNT(*) FROM pvp_match_results m LEFT JOIN (
                    SELECT result_id, COUNT(*) AS row_count, COUNT(DISTINCT participant_order) AS order_count,
                           COUNT(DISTINCT character_id) AS character_count, COUNT(DISTINCT lower(login_name)) AS login_count,
                           MIN(participant_order) AS min_order, MAX(participant_order) AS max_order,
                           SUM(is_winner) AS winner_count, COUNT(DISTINCT is_ranked) AS ranked_modes
                    FROM pvp_participant_results GROUP BY result_id
                  ) p ON p.result_id=m.result_id
                  WHERE COALESCE(p.row_count,0)<>m.participant_count OR p.order_count<>m.participant_count
                     OR p.character_count<>m.participant_count OR p.login_count<>m.participant_count
                     OR p.min_order<>0 OR p.max_order<>m.participant_count-1 OR p.ranked_modes<>1
                     OR (m.has_winner=0 AND p.winner_count<>0) OR (m.has_winner=1 AND p.winner_count<1)"));
            int invalidFixedPositions = Convert.ToInt32(ExecuteScalar(connection,
                "SELECT COUNT(*) FROM characters WHERE position_fixed_x IS NULL OR position_fixed_y IS NULL OR position_fixed_z IS NULL OR tp_pos_fixed_x IS NULL OR tp_pos_fixed_y IS NULL OR tp_pos_fixed_z IS NULL"));
            int invalidAccountIdentity = Convert.ToInt32(ExecuteScalar(connection,
                "SELECT COUNT(*) FROM accounts WHERE COALESCE(trim(username),'')=''"));
            int invalidCharacterIdentity = Convert.ToInt32(ExecuteScalar(connection,
                "SELECT COUNT(*) FROM characters WHERE COALESCE(trim(name),'')='' OR COALESCE(trim(class_name),'')='' OR COALESCE(trim(current_zone),'')=''"));
            int invalidCharacterNumeric = Convert.ToInt32(ExecuteScalar(connection,
                "SELECT COUNT(*) FROM characters WHERE level IS NULL OR level<0 OR level>99 OR experience IS NULL OR experience<0 OR experience>2147483647 OR gold IS NULL OR gold<0 OR gold>2147483647 OR current_hp IS NULL OR current_hp<0 OR current_hp>2147483647 OR current_mana IS NULL OR current_mana<0 OR current_mana>2147483647 OR max_hp IS NULL OR max_hp<0 OR max_hp>2147483647 OR max_mana IS NULL OR max_mana<0 OR max_mana>2147483647 OR zone_id IS NULL OR zone_id< -2147483648 OR zone_id>2147483647 OR tp_zone_id IS NULL OR tp_zone_id< -2147483648 OR tp_zone_id>2147483647 OR posse_id IS NULL OR posse_id<0 OR posse_id>2147483647 OR stat_strength IS NULL OR stat_strength<0 OR stat_strength>65535 OR stat_agility IS NULL OR stat_agility<0 OR stat_agility>65535 OR stat_intellect IS NULL OR stat_intellect<0 OR stat_intellect>65535 OR stat_endurance IS NULL OR stat_endurance<0 OR stat_endurance>65535 OR last_respec_time IS NULL OR last_respec_time<0 OR last_respec_time>2147483647 OR respec_count IS NULL OR respec_count<0 OR respec_count>2147483647 OR pvp_wins IS NULL OR pvp_wins<0 OR pvp_wins>2147483647 OR pvp_rating IS NULL OR pvp_rating<0 OR pvp_rating>3000 OR posse_join_cooldown IS NULL OR posse_join_cooldown<0 OR posse_join_cooldown>2147483647 OR posse_rank_id IS NULL OR posse_rank_id<0 OR posse_rank_id>10"));
            invalidCharacterNumeric += Convert.ToInt32(ExecuteScalar(connection,
                "SELECT COUNT(*) FROM characters WHERE has_pvp_rating IS NULL OR has_pvp_rating NOT IN (0,1)"));
            int invalidInventoryRows = Convert.ToInt32(ExecuteScalar(connection,
                "SELECT COUNT(*) FROM character_inventory WHERE COALESCE(trim(gc_class),'')='' OR slot_x IS NULL OR slot_x<0 OR slot_x>9 OR slot_y IS NULL OR slot_y<0 OR (container_id=11 AND slot_y>7) OR (container_id IN (12,14,15,16,17,18,19) AND slot_y>13) OR container_id IS NULL OR container_id NOT IN (11,12,14,15,16,17,18,19) OR count IS NULL OR count<=0 OR count>255 OR buy_price IS NULL OR buy_price<0 OR buy_price>2147483647 OR rarity IS NULL OR rarity< -1 OR rarity>5 OR stored_level IS NULL OR (stored_level<>-1 AND (stored_level<1 OR stored_level>120)) OR item_order IS NULL OR item_order<0"));
            int duplicateInventoryOrder = Convert.ToInt32(ExecuteScalar(connection,
                "SELECT COUNT(*) FROM (SELECT character_id, container_id, item_order FROM character_inventory GROUP BY character_id, container_id, item_order HAVING COUNT(*)>1)"));
            int invalidEquipmentRows = Convert.ToInt32(ExecuteScalar(connection,
                "SELECT COUNT(*) FROM character_equipment WHERE COALESCE(slot,'') NOT IN ('weapon','armor','helmet','gloves','boots','shoulders','shield','ring1','ring2','amulet') OR COALESCE(trim(gc_class),'')='' OR rarity IS NULL OR rarity< -1 OR rarity>5 OR stored_level IS NULL OR (stored_level<>-1 AND (stored_level<1 OR stored_level>120))"));
            int invalidPendingGrantRows = Convert.ToInt32(ExecuteScalar(connection,
                "SELECT COUNT(*) FROM pending_item_grants WHERE COALESCE(trim(gc_class),'')='' OR count IS NULL OR count<=0 OR count>255 OR width IS NULL OR width<=0 OR width>10 OR height IS NULL OR height<=0 OR height>8 OR rarity IS NULL OR rarity< -1 OR rarity>5"));
            int invalidSkillRows = Convert.ToInt32(ExecuteScalar(connection,
                "SELECT COUNT(*) FROM character_skills WHERE COALESCE(trim(skill_gc_class),'')='' OR level IS NULL OR level<1 OR level>255 OR hotbar_slot IS NULL OR hotbar_slot< -1 OR hotbar_slot>4095"));
            int duplicateSkillRows = Convert.ToInt32(ExecuteScalar(connection,
                "SELECT COUNT(*) FROM (SELECT character_id, lower(skill_gc_class) FROM character_skills GROUP BY character_id, lower(skill_gc_class) HAVING COUNT(*)>1)"));
            int duplicateHotbarRows = Convert.ToInt32(ExecuteScalar(connection,
                "SELECT COUNT(*) FROM (SELECT character_id, hotbar_slot FROM character_skills WHERE hotbar_slot>=0 GROUP BY character_id, hotbar_slot HAVING COUNT(*)>1)"));
            int invalidDropRows = Convert.ToInt32(ExecuteScalar(connection,
                "SELECT COUNT(*) FROM dropped_items WHERE COALESCE(trim(zone),'')='' OR COALESCE(trim(gc_class),'')='' OR quantity IS NULL OR quantity<=0 OR quantity>255 OR gold_amount IS NULL OR gold_amount<0 OR gold_amount>2147483647 OR (gold_amount>0 AND (lower(gc_class)<>'currency' OR quantity<>1)) OR generated_by_bling_gnome IS NULL OR generated_by_bling_gnome NOT IN (0,1) OR heading_fixed IS NULL OR quest_binding_key IS NULL OR activation_key IS NULL OR pos_fixed_x IS NULL OR pos_fixed_y IS NULL OR pos_fixed_z IS NULL"));
            int invalidWorldActivations = Convert.ToInt32(ExecuteScalar(connection,
                "SELECT COUNT(*) FROM world_entity_activations WHERE COALESCE(trim(activation_key),'')='' OR COALESCE(trim(zone),'')='' OR character_id IS NULL OR character_id<=0 OR zone_id IS NULL OR instance_id IS NULL OR entity_id IS NULL OR entity_id<0 OR COALESCE(trim(outcome_json),'')=''"));
            int invalidItemWireState = Convert.ToInt32(ExecuteScalar(connection,
                "SELECT (SELECT COUNT(*) FROM character_inventory WHERE soul_bound IS NULL OR soul_bound NOT IN (0,1) OR no_sell IS NULL OR no_sell NOT IN (0,1) OR soul_bound_countdown IS NULL OR soul_bound_countdown<0 OR soul_bound_countdown>65535) + (SELECT COUNT(*) FROM character_equipment WHERE soul_bound IS NULL OR soul_bound NOT IN (0,1) OR no_sell IS NULL OR no_sell NOT IN (0,1) OR soul_bound_countdown IS NULL OR soul_bound_countdown<0 OR soul_bound_countdown>65535) + (SELECT COUNT(*) FROM dropped_items WHERE soul_bound IS NULL OR soul_bound NOT IN (0,1) OR no_sell IS NULL OR no_sell NOT IN (0,1) OR soul_bound_countdown IS NULL OR soul_bound_countdown<0 OR soul_bound_countdown>65535)"));
            if (invalidCurrentCharacters != 0
                || orphanModifiers != 0
                || invalidModifierRows != 0
                || orphanPendingGrants != 0
                || orphanPendingAdminActions != 0
                || orphanPosseLinks != 0
                || orphanDropOwners != 0
                || orphanWorldActivations != 0
                || orphanWorldActivationDrops != 0
                || orphanPvpParticipants != 0
                || invalidPvpHeaders != 0
                || invalidPvpParticipants != 0
                || invalidPvpCardinality != 0
                || invalidFixedPositions != 0
                || invalidAccountIdentity != 0
                || invalidCharacterIdentity != 0
                || invalidCharacterNumeric != 0
                || invalidInventoryRows != 0
                || duplicateInventoryOrder != 0
                || invalidEquipmentRows != 0
                || invalidPendingGrantRows != 0
                || invalidSkillRows != 0
                || duplicateSkillRows != 0
                || duplicateHotbarRows != 0
                || invalidDropRows != 0
                || invalidWorldActivations != 0
                || invalidItemWireState != 0)
                throw new InvalidDataException($"Runtime database references invalid currentCharacters={invalidCurrentCharacters} modifiers={orphanModifiers} modifierRows={invalidModifierRows} pendingGrants={orphanPendingGrants} pendingAdminActions={orphanPendingAdminActions} posseLinks={orphanPosseLinks} dropOwners={orphanDropOwners} worldActivations={orphanWorldActivations} worldActivationDrops={orphanWorldActivationDrops} pvpOrphans={orphanPvpParticipants} pvpHeaders={invalidPvpHeaders} pvpParticipants={invalidPvpParticipants} pvpCardinality={invalidPvpCardinality} fixedPositions={invalidFixedPositions} accountIdentity={invalidAccountIdentity} characterIdentity={invalidCharacterIdentity} characterNumeric={invalidCharacterNumeric} inventoryRows={invalidInventoryRows} duplicateInventoryOrder={duplicateInventoryOrder} equipmentRows={invalidEquipmentRows} grantRows={invalidPendingGrantRows} skillRows={invalidSkillRows} duplicateSkills={duplicateSkillRows} duplicateHotbar={duplicateHotbarRows} dropRows={invalidDropRows} activationRows={invalidWorldActivations} itemWireState={invalidItemWireState}");
            ValidateJsonArrayColumn(connection, "character_inventory", "id", "generated_item_modifiers");
            ValidateJsonArrayColumn(connection, "character_equipment", "id", "generated_item_modifiers");
            ValidateJsonArrayColumn(connection, "dropped_items", "id", "generated_item_modifiers");
            ValidateJsonObjectColumn(connection, "world_entity_activations", "activation_key", "outcome_json");
            ValidateNonEmptyTextColumns(connection, "character_equipment", "slot");
            ValidateNonEmptyTextColumns(connection, "character_skills", "skill_gc_class");
            ValidateNonEmptyTextColumns(connection, "character_checkpoints", "checkpoint_id");
            ValidateNonEmptyTextColumns(connection, "character_quests", "quest_id", "status");
            ValidateNonEmptyTextColumns(connection, "completed_quests", "quest_id");
            ValidateNonEmptyTextColumns(connection, "quest_objectives", "quest_id", "objective_name");
            ValidateNonEmptyTextColumns(connection, "pending_admin_actions", "action_type");
            ValidateNonEmptyTextColumns(connection, "pvp_match_results", "result_id", "match_id");
            ValidateNonEmptyTextColumns(connection, "pvp_participant_results", "result_id", "login_name", "return_zone");
            ValidateNonEmptyTextColumns(connection, "posses", "name");
            ValidateNonEmptyTextColumns(connection, "server_settings", "key");
            ValidateNonEmptyTextColumns(connection, "social_friends_v2", "character_name", "friend_name");
            ValidateNonEmptyTextColumns(connection, "social_ignores_v2", "character_name", "ignore_name");
        }

        private static void ValidateJsonArrayColumn(SqliteConnection connection, string table, string identityColumn, string jsonColumn)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {QuoteIdentifier(identityColumn)},{QuoteIdentifier(jsonColumn)} FROM {QuoteIdentifier(table)} ORDER BY {QuoteIdentifier(identityColumn)}";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                long identity = Convert.ToInt64(reader.GetValue(0));
                string json = Convert.ToString(reader.GetValue(1));
                try
                {
                    using JsonDocument document = JsonDocument.Parse(json);
                    if (document.RootElement.ValueKind != JsonValueKind.Array)
                        throw new InvalidDataException($"Runtime JSON payload is not an array table={table} row={identity} column={jsonColumn}");
                }
                catch (JsonException ex)
                {
                    throw new InvalidDataException($"Runtime JSON payload is invalid table={table} row={identity} column={jsonColumn}", ex);
                }
            }
        }

        private static void ValidateJsonObjectColumn(SqliteConnection connection, string table, string identityColumn, string jsonColumn)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {QuoteIdentifier(identityColumn)},{QuoteIdentifier(jsonColumn)} FROM {QuoteIdentifier(table)} ORDER BY {QuoteIdentifier(identityColumn)}";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                string identity = Convert.ToString(reader.GetValue(0));
                string json = Convert.ToString(reader.GetValue(1));
                try
                {
                    using JsonDocument document = JsonDocument.Parse(json);
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                        throw new InvalidDataException($"Runtime JSON payload is not an object table={table} row={identity} column={jsonColumn}");
                }
                catch (JsonException ex)
                {
                    throw new InvalidDataException($"Runtime JSON payload is invalid table={table} row={identity} column={jsonColumn}", ex);
                }
            }
        }

        private static void ValidateNonEmptyTextColumns(SqliteConnection connection, string table, params string[] columns)
        {
            var invalidPredicates = new List<string>(columns.Length);
            for (int columnIndex = 0; columnIndex < columns.Length; columnIndex++)
                invalidPredicates.Add($"COALESCE(trim({QuoteIdentifier(columns[columnIndex])}),'')=''");
            int invalidRows = Convert.ToInt32(ExecuteScalar(connection,
                $"SELECT COUNT(*) FROM {QuoteIdentifier(table)} WHERE {string.Join(" OR ", invalidPredicates)}"));
            if (invalidRows != 0)
                throw new InvalidDataException($"Runtime text identity is invalid table={table} columns={string.Join(",", columns)} rows={invalidRows}");
        }

        private static void ValidateIntegrity(SqliteConnection connection, string schema)
        {
            string result = Convert.ToString(ExecuteScalar(connection, $"PRAGMA {schema}.integrity_check"));
            if (!string.Equals(result, "ok", StringComparison.Ordinal))
                throw new InvalidDataException($"Database integrity check failed schema={schema} result={result}");
        }

        private static void CreatePlayerTables()
        {
            using (var connection = OpenConnection())
            {
                ExecuteNonQuery(connection, @"CREATE TABLE IF NOT EXISTS accounts (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, username TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    password_hash TEXT NOT NULL, salt TEXT NOT NULL, email TEXT DEFAULT '',
                    is_member INTEGER DEFAULT 1, is_banned INTEGER DEFAULT 0, is_admin INTEGER DEFAULT 0,
                    created_at TEXT DEFAULT (datetime('now')), last_login TEXT DEFAULT (datetime('now')))");
                ApplyDefaultMembershipMigration(connection);
                ExecuteNonQuery(connection, @"CREATE TABLE IF NOT EXISTS characters (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, account_id INTEGER NOT NULL,
                    name TEXT NOT NULL UNIQUE COLLATE NOCASE, class_name TEXT NOT NULL DEFAULT 'Fighter',
                    avatar_class TEXT DEFAULT '', level INTEGER DEFAULT 1, experience INTEGER DEFAULT 0,
                    gold INTEGER DEFAULT 100, skin INTEGER DEFAULT 0, face INTEGER DEFAULT 0,
                    face_feature INTEGER DEFAULT 0, hair INTEGER DEFAULT 0, hair_color INTEGER DEFAULT 0,
                    current_zone TEXT DEFAULT 'tutorial', zone_id INTEGER DEFAULT 0,
                    position_x REAL DEFAULT 0, position_y REAL DEFAULT 0, position_z REAL DEFAULT 0,
                    position_fixed_x INTEGER DEFAULT 0, position_fixed_y INTEGER DEFAULT 0, position_fixed_z INTEGER DEFAULT 0,
                    current_hp INTEGER DEFAULT 0, current_mana INTEGER DEFAULT 0,
                    created_at TEXT DEFAULT (datetime('now')),
                    FOREIGN KEY (account_id) REFERENCES accounts(id))");
                ExecuteNonQuery(connection, @"CREATE TABLE IF NOT EXISTS character_equipment (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, character_id INTEGER NOT NULL,
                    slot TEXT NOT NULL, gc_class TEXT NOT NULL DEFAULT '', rarity INTEGER DEFAULT -1,
                    stored_level INTEGER DEFAULT -1, preset_scale_mod TEXT DEFAULT '',
                    has_generated_item_state INTEGER DEFAULT 0,
                    rolled_requires_membership INTEGER DEFAULT 0,
                    generated_requires_membership INTEGER DEFAULT 0,
                    soul_bound INTEGER DEFAULT 0,
                    no_sell INTEGER DEFAULT 0,
                    soul_bound_countdown INTEGER DEFAULT 65535,
                    generated_item_modifiers TEXT DEFAULT '[]',
                    FOREIGN KEY (character_id) REFERENCES characters(id) ON DELETE CASCADE,
                    UNIQUE(character_id, slot))");
                ExecuteNonQuery(connection, @"CREATE TABLE IF NOT EXISTS character_inventory (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, character_id INTEGER NOT NULL,
                    gc_class TEXT NOT NULL, slot_x INTEGER DEFAULT 0, slot_y INTEGER DEFAULT 0,
                    count INTEGER DEFAULT 1, buy_price INTEGER DEFAULT 0, rarity INTEGER DEFAULT -1,
                    stored_level INTEGER DEFAULT -1, container_id INTEGER DEFAULT 11, item_order INTEGER DEFAULT 0,
                    preset_scale_mod TEXT DEFAULT '', has_generated_item_state INTEGER DEFAULT 0,
                    rolled_requires_membership INTEGER DEFAULT 0,
                    generated_requires_membership INTEGER DEFAULT 0,
                    soul_bound INTEGER DEFAULT 0,
                    no_sell INTEGER DEFAULT 0,
                    soul_bound_countdown INTEGER DEFAULT 65535,
                    generated_item_modifiers TEXT DEFAULT '[]',
                    FOREIGN KEY (character_id) REFERENCES characters(id) ON DELETE CASCADE)");
                ExecuteNonQuery(connection, @"CREATE TABLE IF NOT EXISTS character_skills (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, character_id INTEGER NOT NULL,
                    skill_gc_class TEXT NOT NULL, level INTEGER DEFAULT 1, hotbar_slot INTEGER DEFAULT -1,
                    FOREIGN KEY (character_id) REFERENCES characters(id) ON DELETE CASCADE,
                    UNIQUE(character_id, skill_gc_class))");
                ExecuteNonQuery(connection, @"CREATE TABLE IF NOT EXISTS character_quests (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, character_id INTEGER NOT NULL,
                    quest_id TEXT NOT NULL, quest_giver_id TEXT DEFAULT '',
                    accepted_at TEXT DEFAULT (datetime('now')), status TEXT DEFAULT 'active',
                    FOREIGN KEY (character_id) REFERENCES characters(id) ON DELETE CASCADE,
                    UNIQUE(character_id, quest_id))");
                ExecuteNonQuery(connection, @"CREATE TABLE IF NOT EXISTS quest_objectives (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, character_id INTEGER NOT NULL,
                    quest_id TEXT NOT NULL, objective_name TEXT NOT NULL,
                    type TEXT DEFAULT '', target TEXT DEFAULT '', label TEXT DEFAULT '',
                    required INTEGER DEFAULT 0, current INTEGER DEFAULT 0,
                    FOREIGN KEY (character_id) REFERENCES characters(id) ON DELETE CASCADE)");
                ExecuteNonQuery(connection, @"CREATE TABLE IF NOT EXISTS completed_quests (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, character_id INTEGER NOT NULL,
                    quest_id TEXT NOT NULL, completed_at TEXT DEFAULT (datetime('now')),
                    FOREIGN KEY (character_id) REFERENCES characters(id) ON DELETE CASCADE,
                    UNIQUE(character_id, quest_id))");
                ExecuteNonQuery(connection, @"CREATE TABLE IF NOT EXISTS character_checkpoints (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, character_id INTEGER NOT NULL,
                    checkpoint_id TEXT NOT NULL,
                    FOREIGN KEY (character_id) REFERENCES characters(id) ON DELETE CASCADE,
                    UNIQUE(character_id, checkpoint_id))");
                ExecuteNonQuery(connection, @"CREATE TABLE IF NOT EXISTS character_modifiers (
                    character_id INTEGER NOT NULL,
                    gc_type TEXT NOT NULL,
                    modifier_id INTEGER NOT NULL,
                    level INTEGER DEFAULT 0,
                    power_level INTEGER DEFAULT 0,
                    duration_remaining INTEGER DEFAULT 0,
                    source_is_self INTEGER DEFAULT 1,
                    created_at TEXT DEFAULT CURRENT_TIMESTAMP,
                    FOREIGN KEY (character_id) REFERENCES characters(id) ON DELETE CASCADE,
                    PRIMARY KEY (character_id, modifier_id))");
                ExecuteNonQuery(connection, "PRAGMA foreign_keys=ON");

                ExecuteNonQuery(connection, @"CREATE TABLE IF NOT EXISTS dropped_items (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    zone TEXT NOT NULL,
                    zone_id INTEGER NOT NULL,
                    instance_id INTEGER DEFAULT 0,
                    gc_class TEXT NOT NULL,
                    dfc_class TEXT DEFAULT 'Armor',
                    pos_x REAL NOT NULL,
                    pos_y REAL NOT NULL,
                    pos_z REAL NOT NULL,
                    pos_fixed_x INTEGER NOT NULL,
                    pos_fixed_y INTEGER NOT NULL,
                    pos_fixed_z INTEGER NOT NULL,
                    heading_fixed INTEGER NOT NULL DEFAULT 0,
                    player_level INTEGER DEFAULT 1,
                    quantity INTEGER DEFAULT 1,
                    gold_amount INTEGER NOT NULL DEFAULT 0,
                    generated_by_bling_gnome INTEGER NOT NULL DEFAULT 0,
                    target_slot INTEGER DEFAULT -1,
                    preset_scale_mod TEXT DEFAULT '',
                    has_generated_item_state INTEGER DEFAULT 0,
                    rolled_requires_membership INTEGER DEFAULT 0,
                    generated_requires_membership INTEGER DEFAULT 0,
                    soul_bound INTEGER DEFAULT 0,
                    no_sell INTEGER DEFAULT 0,
                    soul_bound_countdown INTEGER DEFAULT 65535,
                    generated_item_modifiers TEXT DEFAULT '[]',
                    quest_binding_key TEXT NOT NULL DEFAULT '',
                    activation_key TEXT NOT NULL DEFAULT '',
                    dropped_at TEXT DEFAULT (datetime('now')),
                    dropped_by TEXT DEFAULT '',
                    owner_character_id INTEGER DEFAULT 0,
                    owner_group_id INTEGER DEFAULT 0,
                    owner_name TEXT DEFAULT '')");

                ExecuteNonQuery(connection, @"CREATE TABLE IF NOT EXISTS world_entity_activations (
                    activation_key TEXT PRIMARY KEY,
                    character_id INTEGER NOT NULL,
                    zone TEXT NOT NULL,
                    zone_id INTEGER NOT NULL,
                    instance_id INTEGER NOT NULL,
                    entity_id INTEGER NOT NULL,
                    outcome_json TEXT NOT NULL,
                    activated_at TEXT NOT NULL DEFAULT (datetime('now')),
                    FOREIGN KEY (character_id) REFERENCES characters(id) ON DELETE CASCADE)");

                EnsureColumn(connection, "characters", "tp_zone", "TEXT DEFAULT ''");
                EnsureColumn(connection, "characters", "tp_zone_id", "INTEGER DEFAULT 0");
                EnsureColumn(connection, "characters", "tp_target_zone", "TEXT DEFAULT ''");
                EnsureColumn(connection, "characters", "tp_pos_x", "REAL DEFAULT 0");
                EnsureColumn(connection, "characters", "tp_pos_y", "REAL DEFAULT 0");
                EnsureColumn(connection, "characters", "tp_pos_z", "REAL DEFAULT 0");
                EnsureColumn(connection, "characters", "tp_pos_fixed_x", "INTEGER DEFAULT 0");
                EnsureColumn(connection, "characters", "tp_pos_fixed_y", "INTEGER DEFAULT 0");
                EnsureColumn(connection, "characters", "tp_pos_fixed_z", "INTEGER DEFAULT 0");
                EnsureColumn(connection, "characters", "position_fixed_x", "INTEGER DEFAULT 0");
                EnsureColumn(connection, "characters", "position_fixed_y", "INTEGER DEFAULT 0");
                EnsureColumn(connection, "characters", "position_fixed_z", "INTEGER DEFAULT 0");
                BackfillFixed8Triplet(connection, "characters", "position_x", "position_y", "position_z", "position_fixed_x", "position_fixed_y", "position_fixed_z");
                BackfillFixed8Triplet(connection, "characters", "tp_pos_x", "tp_pos_y", "tp_pos_z", "tp_pos_fixed_x", "tp_pos_fixed_y", "tp_pos_fixed_z");

                EnsureColumn(connection, "character_inventory", "rarity", "INTEGER DEFAULT -1");
                EnsureColumn(connection, "character_equipment", "rarity", "INTEGER DEFAULT -1");
                EnsureColumn(connection, "dropped_items", "rarity", "INTEGER DEFAULT -1");
                EnsureColumn(connection, "character_inventory", "stored_level", "INTEGER DEFAULT -1");
                EnsureColumn(connection, "character_inventory", "buy_price", "INTEGER DEFAULT 0");
                EnsureColumn(connection, "character_inventory", "container_id", "INTEGER DEFAULT 11");
                EnsureColumn(connection, "character_inventory", "item_order", "INTEGER DEFAULT 0");
                EnsureColumn(connection, "character_equipment", "stored_level", "INTEGER DEFAULT -1");
                EnsureColumn(connection, "dropped_items", "stored_level", "INTEGER DEFAULT -1");
                EnsureColumn(connection, "dropped_items", "quantity", "INTEGER DEFAULT 1");
                EnsureColumn(connection, "dropped_items", "gold_amount", "INTEGER NOT NULL DEFAULT 0");
                EnsureColumn(connection, "dropped_items", "generated_by_bling_gnome", "INTEGER NOT NULL DEFAULT 0");
                EnsureColumn(connection, "dropped_items", "dfc_class", "TEXT DEFAULT 'Armor'");
                EnsureColumn(connection, "dropped_items", "owner_character_id", "INTEGER DEFAULT 0");
                EnsureColumn(connection, "dropped_items", "owner_group_id", "INTEGER DEFAULT 0");
                EnsureColumn(connection, "dropped_items", "owner_name", "TEXT DEFAULT ''");
                EnsureColumn(connection, "dropped_items", "quest_binding_key", "TEXT NOT NULL DEFAULT ''");
                EnsureColumn(connection, "dropped_items", "activation_key", "TEXT NOT NULL DEFAULT ''");
                EnsureColumn(connection, "dropped_items", "heading_fixed", "INTEGER NOT NULL DEFAULT 0");
                EnsureColumn(connection, "dropped_items", "pos_fixed_x", "INTEGER");
                EnsureColumn(connection, "dropped_items", "pos_fixed_y", "INTEGER");
                EnsureColumn(connection, "dropped_items", "pos_fixed_z", "INTEGER");
                BackfillFixed8Triplet(connection, "dropped_items", "pos_x", "pos_y", "pos_z", "pos_fixed_x", "pos_fixed_y", "pos_fixed_z", true);
                EnsureGeneratedItemColumns(connection, "character_inventory");
                EnsureGeneratedItemColumns(connection, "character_equipment");
                EnsureGeneratedItemColumns(connection, "dropped_items");
                EnsureItemWireStateColumns(connection, "character_inventory");
                EnsureItemWireStateColumns(connection, "character_equipment");
                EnsureItemWireStateColumns(connection, "dropped_items");
                EnsureColumn(connection, "characters", "max_hp", "INTEGER DEFAULT 0");
                EnsureColumn(connection, "characters", "max_mana", "INTEGER DEFAULT 0");
                EnsureColumn(connection, "characters", "stat_strength", "INTEGER DEFAULT 0");
                EnsureColumn(connection, "characters", "stat_agility", "INTEGER DEFAULT 0");
                EnsureColumn(connection, "characters", "stat_intellect", "INTEGER DEFAULT 0");
                EnsureColumn(connection, "characters", "stat_endurance", "INTEGER DEFAULT 0");
                EnsureColumn(connection, "characters", "last_respec_time", "INTEGER DEFAULT 0");
                EnsureColumn(connection, "characters", "respec_count", "INTEGER DEFAULT 0");
                EnsureColumn(connection, "characters", "pvp_wins", "INTEGER DEFAULT 0");
                EnsureColumn(connection, "characters", "pvp_rating", "INTEGER DEFAULT 1500");
                EnsureColumn(connection, "characters", "has_pvp_rating", "INTEGER NOT NULL DEFAULT 0 CHECK(has_pvp_rating IN (0,1))");
                EnsureColumn(connection, "characters", "monster_difficulty", "INTEGER NOT NULL DEFAULT 0 CHECK(monster_difficulty BETWEEN 0 AND 3)");
                EnsureColumn(connection, "characters", "minimum_item_quality", "INTEGER NOT NULL DEFAULT 1 CHECK(minimum_item_quality BETWEEN 1 AND 7)");
                EnsureColumn(connection, "accounts", "current_character_id", "INTEGER DEFAULT 0");

                ExecuteNonQuery(connection, @"CREATE TABLE IF NOT EXISTS pending_item_grants (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    character_id INTEGER NOT NULL,
                    gc_class TEXT NOT NULL,
                    count INTEGER DEFAULT 1,
                    width INTEGER DEFAULT 1,
                    height INTEGER DEFAULT 1,
                    rarity INTEGER DEFAULT 1,
                    created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                    FOREIGN KEY (character_id) REFERENCES characters(id) ON DELETE CASCADE
                )");

                ExecuteNonQuery(connection, @"CREATE TABLE IF NOT EXISTS pvp_match_results (
                    result_id TEXT PRIMARY KEY,
                    match_id TEXT NOT NULL,
                    archetype INTEGER NOT NULL,
                    has_winner INTEGER NOT NULL,
                    participant_count INTEGER NOT NULL,
                    created_at TEXT NOT NULL DEFAULT (datetime('now'))
                )");

                ExecuteNonQuery(connection, @"CREATE TABLE IF NOT EXISTS pvp_participant_results (
                    result_id TEXT NOT NULL,
                    participant_order INTEGER NOT NULL,
                    character_id INTEGER NOT NULL,
                    login_name TEXT NOT NULL COLLATE NOCASE,
                    is_winner INTEGER NOT NULL,
                    is_ranked INTEGER NOT NULL,
                    rating_before INTEGER NOT NULL,
                    rating_after INTEGER NOT NULL,
                    persisted_rating_before INTEGER NOT NULL,
                    apply_rating INTEGER NOT NULL,
                    wins_before INTEGER NOT NULL,
                    wins_delta INTEGER NOT NULL,
                    reward_gc_class TEXT NOT NULL DEFAULT '',
                    reward_count INTEGER NOT NULL DEFAULT 0,
                    reward_width INTEGER NOT NULL DEFAULT 0,
                    reward_height INTEGER NOT NULL DEFAULT 0,
                    reward_rarity INTEGER NOT NULL DEFAULT -1,
                    return_zone TEXT NOT NULL,
                    return_zone_id INTEGER NOT NULL,
                    return_pos_fixed_x INTEGER NOT NULL,
                    return_pos_fixed_y INTEGER NOT NULL,
                    return_pos_fixed_z INTEGER NOT NULL,
                    applied_at TEXT,
                    return_completed_at TEXT,
                    pending_grant_id INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (result_id, character_id),
                    UNIQUE (result_id, participant_order),
                    UNIQUE (result_id, login_name),
                    FOREIGN KEY (result_id) REFERENCES pvp_match_results(result_id) ON DELETE CASCADE,
                    FOREIGN KEY (character_id) REFERENCES characters(id) ON DELETE CASCADE
                )");

                ExecuteNonQuery(connection, @"CREATE TABLE IF NOT EXISTS pending_admin_actions (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    character_id INTEGER NOT NULL,
                    action_type TEXT NOT NULL,
                    value INTEGER DEFAULT 0,
                    created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                    FOREIGN KEY (character_id) REFERENCES characters(id) ON DELETE CASCADE
                )");

                ExecuteNonQuery(connection, @"CREATE TABLE IF NOT EXISTS server_settings (
                    key TEXT PRIMARY KEY NOT NULL,
                    value TEXT NOT NULL,
                    updated_at TEXT DEFAULT CURRENT_TIMESTAMP
                )");

                ExecuteNonQuery(connection, @"CREATE TABLE IF NOT EXISTS social_friends_v2 (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    character_name TEXT NOT NULL COLLATE NOCASE,
                    friend_name TEXT NOT NULL COLLATE NOCASE,
                    UNIQUE(character_name, friend_name)
                )");

                ExecuteNonQuery(connection, @"CREATE TABLE IF NOT EXISTS social_ignores_v2 (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    character_name TEXT NOT NULL COLLATE NOCASE,
                    ignore_name TEXT NOT NULL COLLATE NOCASE,
                    UNIQUE(character_name, ignore_name)
                )");

                ExecuteNonQuery(connection, @"CREATE TABLE IF NOT EXISTS posses (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    founder_character_id INTEGER NOT NULL,
                    founded_at TEXT DEFAULT (datetime('now')),
                    gold_paid INTEGER DEFAULT 0,
                    FOREIGN KEY (founder_character_id) REFERENCES characters(id))");
                EnsureColumn(connection, "characters", "posse_id", "INTEGER DEFAULT 0");
                EnsureColumn(connection, "characters", "posse_join_cooldown", "INTEGER DEFAULT 0");
                EnsureColumn(connection, "characters", "posse_rank_id", "INTEGER DEFAULT 1");
                EnsureColumn(connection, "posses", "motd", "TEXT DEFAULT ''");
                EnsureColumn(connection, "posses", "description", "TEXT DEFAULT ''");

                ApplyEmptyEquipmentSlotMigration(connection);
                ApplyInventoryOrderMigration(connection);
                ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_character_inventory_cid ON character_inventory(character_id)");
                ExecuteNonQuery(connection, "CREATE UNIQUE INDEX IF NOT EXISTS ux_character_inventory_container_order ON character_inventory(character_id, container_id, item_order)");
                ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_quest_objectives_cid_qid ON quest_objectives(character_id, quest_id)");
                ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_dropped_items_zone_inst ON dropped_items(zone, instance_id)");
                ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_dropped_items_dropped_at ON dropped_items(dropped_at)");
                ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_dropped_items_activation ON dropped_items(activation_key) WHERE activation_key<>''");
                ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_world_entity_activations_character ON world_entity_activations(character_id)");
                ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_characters_account ON characters(account_id)");
                ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_characters_posse ON characters(posse_id)");
                ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_pending_item_grants_cid ON pending_item_grants(character_id)");
                ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_pending_admin_actions_cid ON pending_admin_actions(character_id)");
                ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_pvp_participant_results_pending ON pvp_participant_results(character_id, applied_at, return_completed_at)");
                ExecuteNonQuery(connection, @"UPDATE characters SET has_pvp_rating=1
                    WHERE has_pvp_rating=0 AND EXISTS (
                        SELECT 1 FROM pvp_participant_results p
                        WHERE p.character_id=characters.id AND p.apply_rating=1 AND p.applied_at IS NOT NULL)");
                ExecuteNonQuery(connection, "INSERT OR IGNORE INTO schema_migrations(name) VALUES('runtime-schema-v4')");
                ExecuteNonQuery(connection, $"PRAGMA user_version={RuntimeSchemaVersion}");
            }
        }

        private static void ApplyDefaultMembershipMigration(SqliteConnection connection)
        {
            ExecuteNonQuery(connection, @"CREATE TABLE IF NOT EXISTS schema_migrations (
                name TEXT PRIMARY KEY,
                applied_at TEXT DEFAULT (datetime('now')))");

            using (var transaction = connection.BeginTransaction())
            {
                using (var check = connection.CreateCommand())
                {
                    check.Transaction = transaction;
                    check.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE name = @name";
                    check.Parameters.AddWithValue("@name", "accounts-default-membership-v1");
                    if (Convert.ToInt32(check.ExecuteScalar()) != 0)
                    {
                        transaction.Commit();
                        return;
                    }
                }

                using (var update = connection.CreateCommand())
                {
                    update.Transaction = transaction;
                    update.CommandText = "UPDATE accounts SET is_member = 1";
                    update.ExecuteNonQuery();
                }

                using (var mark = connection.CreateCommand())
                {
                    mark.Transaction = transaction;
                    mark.CommandText = "INSERT INTO schema_migrations (name) VALUES (@name)";
                    mark.Parameters.AddWithValue("@name", "accounts-default-membership-v1");
                    mark.ExecuteNonQuery();
                }

                transaction.Commit();
            }
        }

        private static void ApplyInventoryOrderMigration(SqliteConnection connection)
        {
            using (var transaction = connection.BeginTransaction())
            {
                using (var check = connection.CreateCommand())
                {
                    check.Transaction = transaction;
                    check.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE name=@name";
                    check.Parameters.AddWithValue("@name", "character-inventory-order-v1");
                    if (Convert.ToInt32(check.ExecuteScalar()) != 0)
                    {
                        transaction.Commit();
                        return;
                    }
                }

                var rows = new List<(long Id, int CharacterId, int ContainerId)>();
                using (var select = connection.CreateCommand())
                {
                    select.Transaction = transaction;
                    select.CommandText = "SELECT id, character_id, container_id FROM character_inventory ORDER BY character_id, container_id, item_order, id";
                    using var reader = select.ExecuteReader();
                    while (reader.Read())
                        rows.Add((reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2)));
                }

                var nextOrder = new Dictionary<string, int>(StringComparer.Ordinal);
                using (var update = connection.CreateCommand())
                {
                    update.Transaction = transaction;
                    update.CommandText = "UPDATE character_inventory SET item_order=@itemOrder WHERE id=@id";
                    var orderParameter = update.CreateParameter();
                    orderParameter.ParameterName = "@itemOrder";
                    orderParameter.DbType = System.Data.DbType.Int32;
                    update.Parameters.Add(orderParameter);
                    var idParameter = update.CreateParameter();
                    idParameter.ParameterName = "@id";
                    idParameter.DbType = System.Data.DbType.Int64;
                    update.Parameters.Add(idParameter);
                    foreach (var row in rows)
                    {
                        string key = $"{row.CharacterId}:{row.ContainerId}";
                        int itemOrder = nextOrder.TryGetValue(key, out int currentOrder) ? currentOrder : 0;
                        orderParameter.Value = itemOrder;
                        idParameter.Value = row.Id;
                        if (update.ExecuteNonQuery() != 1)
                            throw new InvalidDataException($"Inventory order migration did not update row id={row.Id}");
                        nextOrder[key] = checked(itemOrder + 1);
                    }
                }

                using (var mark = connection.CreateCommand())
                {
                    mark.Transaction = transaction;
                    mark.CommandText = "INSERT INTO schema_migrations(name) VALUES(@name)";
                    mark.Parameters.AddWithValue("@name", "character-inventory-order-v1");
                    if (mark.ExecuteNonQuery() != 1)
                        throw new InvalidDataException("Inventory order migration was not recorded exactly once");
                }
                transaction.Commit();
            }
        }

        private static void ApplyEmptyEquipmentSlotMigration(SqliteConnection connection)
        {
            using (var transaction = connection.BeginTransaction())
            {
                using (var check = connection.CreateCommand())
                {
                    check.Transaction = transaction;
                    check.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE name=@name";
                    check.Parameters.AddWithValue("@name", "character-equipment-empty-slots-v1");
                    if (Convert.ToInt32(check.ExecuteScalar()) != 0)
                    {
                        transaction.Commit();
                        return;
                    }
                }

                using (var delete = connection.CreateCommand())
                {
                    delete.Transaction = transaction;
                    delete.CommandText = "DELETE FROM character_equipment WHERE COALESCE(trim(gc_class),'')='' AND COALESCE(rarity,-1)=-1 AND COALESCE(stored_level,-1)=-1 AND COALESCE(preset_scale_mod,'')='' AND COALESCE(has_generated_item_state,0)=0 AND COALESCE(rolled_requires_membership,0)=0 AND COALESCE(generated_requires_membership,0)=0 AND COALESCE(soul_bound,0)=0 AND COALESCE(no_sell,0)=0 AND COALESCE(soul_bound_countdown,65535)=65535 AND COALESCE(generated_item_modifiers,'[]')='[]'";
                    delete.ExecuteNonQuery();
                }

                using (var mark = connection.CreateCommand())
                {
                    mark.Transaction = transaction;
                    mark.CommandText = "INSERT INTO schema_migrations(name) VALUES(@name)";
                    mark.Parameters.AddWithValue("@name", "character-equipment-empty-slots-v1");
                    if (mark.ExecuteNonQuery() != 1)
                        throw new InvalidDataException("Empty equipment slot migration was not recorded exactly once");
                }
                transaction.Commit();
            }
        }

        private static void EnsureGeneratedItemColumns(SqliteConnection connection, string table)
        {
            EnsureColumn(connection, table, "preset_scale_mod", "TEXT DEFAULT ''");
            EnsureColumn(connection, table, "has_generated_item_state", "INTEGER DEFAULT 0");
            EnsureColumn(connection, table, "rolled_requires_membership", "INTEGER DEFAULT 0");
            EnsureColumn(connection, table, "generated_requires_membership", "INTEGER DEFAULT 0");
            EnsureColumn(connection, table, "generated_item_modifiers", "TEXT DEFAULT '[]'");
        }

        private static void EnsureItemWireStateColumns(SqliteConnection connection, string table)
        {
            EnsureColumn(connection, table, "soul_bound", "INTEGER DEFAULT 0");
            EnsureColumn(connection, table, "no_sell", "INTEGER DEFAULT 0");
            EnsureColumn(connection, table, "soul_bound_countdown", "INTEGER DEFAULT 65535");
        }

        private static void BackfillFixed8Triplet(
            SqliteConnection connection,
            string table,
            string realX,
            string realY,
            string realZ,
            string fixedX,
            string fixedY,
            string fixedZ,
            bool nullOnly = false)
        {
            string predicate = nullOnly
                ? $"{QuoteIdentifier(fixedX)} IS NULL OR {QuoteIdentifier(fixedY)} IS NULL OR {QuoteIdentifier(fixedZ)} IS NULL"
                : $"({QuoteIdentifier(fixedX)} = 0 AND {QuoteIdentifier(realX)} != 0) OR ({QuoteIdentifier(fixedY)} = 0 AND {QuoteIdentifier(realY)} != 0) OR ({QuoteIdentifier(fixedZ)} = 0 AND {QuoteIdentifier(realZ)} != 0)";
            var rows = new List<(long Id, int X, int Y, int Z)>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"SELECT id,{QuoteIdentifier(realX)},{QuoteIdentifier(realY)},{QuoteIdentifier(realZ)} FROM {QuoteIdentifier(table)} WHERE {predicate} ORDER BY id";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add((
                        reader.GetInt64(0),
                        ParseFixed8DatabaseValue(reader.GetValue(1), table, realX),
                        ParseFixed8DatabaseValue(reader.GetValue(2), table, realY),
                        ParseFixed8DatabaseValue(reader.GetValue(3), table, realZ)));
                }
            }
            foreach (var row in rows)
            {
                ExecuteNonQuery(connection,
                    $"UPDATE {QuoteIdentifier(table)} SET {QuoteIdentifier(fixedX)}=@x,{QuoteIdentifier(fixedY)}=@y,{QuoteIdentifier(fixedZ)}=@z WHERE id=@id",
                    ("@x", row.X), ("@y", row.Y), ("@z", row.Z), ("@id", row.Id));
            }
        }

        private static int ParseFixed8DatabaseValue(object value, string table, string column)
        {
            if (value == null || value == DBNull.Value)
                return 0;
            string text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
            if (!DungeonRunners.Data.GCNode.TryParseFixed32(text, out int fixedValue))
                throw new InvalidDataException($"Invalid fixed8 migration value table={table} column={column} value='{text}'");
            return fixedValue;
        }

        private static void EnsureColumn(SqliteConnection connection, string table, string column, string declaration)
        {
            if (ColumnExists(connection, table, column))
                return;
            ExecuteNonQuery(connection, $"ALTER TABLE {QuoteIdentifier(table)} ADD COLUMN {QuoteIdentifier(column)} {declaration}");
            if (!ColumnExists(connection, table, column))
                throw new InvalidDataException($"Database migration did not create {table}.{column}");
        }

        private static bool ColumnExists(SqliteConnection connection, string table, string column)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA main.table_info({QuoteIdentifier(table)})";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(Convert.ToString(reader["name"]), column, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static string QuoteIdentifier(string value)
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        public static void ExecuteNonQuery(SqliteConnection conn, string sql, params (string name, object value)[] parameters)
        {
            using (var command = conn.CreateCommand())
            {
                command.CommandText = sql;
                foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.name, parameter.value);
                command.ExecuteNonQuery();
            }
        }
        public static object ExecuteScalar(SqliteConnection conn, string sql, params (string name, object value)[] parameters)
        {
            using (var command = conn.CreateCommand())
            {
                command.CommandText = sql;
                foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.name, parameter.value);
                return command.ExecuteScalar();
            }
        }
        public static SqliteDataReader ExecuteReader(SqliteConnection conn, string sql, params (string name, object value)[] parameters)
        {
            var command = conn.CreateCommand();
            command.CommandText = sql;
            foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.name, parameter.value);
            return command.ExecuteReader();
        }
        public static int GetInt(SqliteDataReader reader, string column, int fallback = 0)
        {
            int ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? fallback : Convert.ToInt32(reader.GetValue(ordinal));
        }
        public static string GetString(SqliteDataReader reader, string column, string fallback = "")
        {
            int ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? fallback : Convert.ToString(reader.GetValue(ordinal));
        }
        public static float GetFloat(SqliteDataReader reader, string column, float fallback = 0f)
        {
            int ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? fallback : Convert.ToSingle(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);
        }
        public static int GetFixed32(SqliteDataReader reader, string column, int fallbackF32 = 0)
        {
            int ordinal = reader.GetOrdinal(column);
            if (reader.IsDBNull(ordinal)) return fallbackF32;
            string text = Convert.ToString(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);
            if (!DungeonRunners.Data.GCNode.TryParseFixed32(text, out int result))
                throw new InvalidDataException($"Invalid fixed32 value column={column} value='{text}'");
            return result;
        }
        public static int GetFixed8(SqliteDataReader reader, string column, int fallbackFixed = 0)
        {
            int ordinal = reader.GetOrdinal(column);
            if (reader.IsDBNull(ordinal)) return fallbackFixed;
            string text = Convert.ToString(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);
            if (!DungeonRunners.Data.GCNode.TryParseFixed32(text, out int result))
                throw new InvalidDataException($"Invalid fixed8 value column={column} value='{text}'");
            return result;
        }
        public static uint GetUInt(SqliteDataReader reader, string column, uint fallback = 0)
        {
            int ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? fallback : Convert.ToUInt32(reader.GetValue(ordinal));
        }
    }
}
