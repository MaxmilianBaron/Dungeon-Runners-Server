using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using DungeonRunners.Core;
using DungeonRunners.Data;
using DungeonRunners.Database;
using DungeonRunners.Engine;
using DungeonRunners.Gameplay;

namespace DungeonRunners.Networking
{
    public partial class GameServer
    {
        private const int PendingDisconnectedCharacterSpoolVersion = 1;
        private const int MaxPendingDisconnectedCharacterSpoolEntries = 256;
        private const long MaxPendingDisconnectedCharacterSpoolBytes = 16L * 1024L * 1024L;
        private static readonly JsonSerializerOptions PendingDisconnectedCharacterSpoolJsonOptions = new JsonSerializerOptions
        {
            IncludeFields = true,
            PropertyNameCaseInsensitive = true
        };

        private sealed class PendingDisconnectedCharacterSpool
        {
            public int Version;
            public List<PendingDisconnectedCharacterSpoolEntry> Entries = new List<PendingDisconnectedCharacterSpoolEntry>();
            public Dictionary<string, uint> FailedCaptureFences = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class PendingDisconnectedCharacterSpoolEntry
        {
            public string LoginName;
            public int ConnectionId;
            public SavedCharacter Snapshot;
            public Dictionary<uint, uint> ModifierDurations = new Dictionary<uint, uint>();
            public int Attempts;
            public bool DatabaseCommitted;
        }

        private sealed class PendingDisconnectedCharacterSave
        {
            public string LoginName { get; }
            public int ConnectionId { get; }
            public uint CharacterId { get; }
            public SavedCharacter Snapshot { get; }
            public IReadOnlyDictionary<uint, uint> ModifierDurations { get; }
            public int Attempts { get; set; }
            public bool SpoolDurable { get; set; }
            public bool DatabaseCommitted { get; set; }

            public PendingDisconnectedCharacterSave(
                string loginName,
                int connectionId,
                SavedCharacter snapshot,
                IReadOnlyDictionary<uint, uint> modifierDurations)
            {
                LoginName = loginName;
                ConnectionId = connectionId;
                CharacterId = snapshot.id;
                Snapshot = snapshot.DeepClone();
                ModifierDurations = new ReadOnlyDictionary<uint, uint>(new Dictionary<uint, uint>(modifierDurations));
            }
        }

        private sealed class PendingDisconnectCaptureFailure
        {
            public string LoginName { get; }
            public uint CharacterId { get; }
            public bool SpoolDurable { get; set; }

            public PendingDisconnectCaptureFailure(string loginName, uint characterId)
            {
                LoginName = loginName;
                CharacterId = characterId;
            }
        }

        private readonly List<PendingDisconnectedCharacterSave> _pendingDisconnectedCharacterSaves = new List<PendingDisconnectedCharacterSave>();
        private readonly Dictionary<string, PendingDisconnectCaptureFailure> _failedDisconnectCaptureFences = new Dictionary<string, PendingDisconnectCaptureFailure>(StringComparer.OrdinalIgnoreCase);
        private readonly List<PVPMatchmaking.Match> _deferredDisconnectPvpMatches = new List<PVPMatchmaking.Match>();

        private static string GetPendingDisconnectedCharacterSpoolPath()
        {
            return DataPaths.ServerPath("Data", "Persistence", "pending-disconnected-characters.json");
        }

        private bool TryPersistPendingDisconnectedCharacterSpool()
        {
            try
            {
                var spool = new PendingDisconnectedCharacterSpool
                {
                    Version = PendingDisconnectedCharacterSpoolVersion
                };
                var orderedFailureLogins = new List<string>(_failedDisconnectCaptureFences.Keys);
                orderedFailureLogins.Sort(StringComparer.OrdinalIgnoreCase);
                for (int failureIndex = 0; failureIndex < orderedFailureLogins.Count; failureIndex++)
                {
                    string failureLogin = orderedFailureLogins[failureIndex];
                    spool.FailedCaptureFences.Add(failureLogin, _failedDisconnectCaptureFences[failureLogin].CharacterId);
                }
                for (int index = 0; index < _pendingDisconnectedCharacterSaves.Count; index++)
                {
                    PendingDisconnectedCharacterSave pending = _pendingDisconnectedCharacterSaves[index];
                    var durations = new Dictionary<uint, uint>();
                    var orderedModifierIds = new List<uint>(pending.ModifierDurations.Keys);
                    orderedModifierIds.Sort();
                    for (int modifierIndex = 0; modifierIndex < orderedModifierIds.Count; modifierIndex++)
                    {
                        uint modifierId = orderedModifierIds[modifierIndex];
                        durations.Add(modifierId, pending.ModifierDurations[modifierId]);
                    }
                    spool.Entries.Add(new PendingDisconnectedCharacterSpoolEntry
                    {
                        LoginName = pending.LoginName,
                        ConnectionId = pending.ConnectionId,
                        Snapshot = pending.Snapshot.DeepClone(),
                        ModifierDurations = durations,
                        Attempts = pending.Attempts,
                        DatabaseCommitted = pending.DatabaseCommitted
                    });
                }
                string json = JsonSerializer.Serialize(spool, PendingDisconnectedCharacterSpoolJsonOptions);
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                if (bytes.LongLength > MaxPendingDisconnectedCharacterSpoolBytes)
                    throw new InvalidDataException($"Pending disconnect spool exceeds {MaxPendingDisconnectedCharacterSpoolBytes} bytes");
                string spoolPath = GetPendingDisconnectedCharacterSpoolPath();
                string spoolDirectory = Path.GetDirectoryName(spoolPath);
                Directory.CreateDirectory(spoolDirectory);
                string tempPath = DataPaths.RequireServerPath(spoolPath + ".tmp");
                using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                File.Move(tempPath, spoolPath, true);
                for (int index = 0; index < _pendingDisconnectedCharacterSaves.Count; index++)
                    _pendingDisconnectedCharacterSaves[index].SpoolDurable = true;
                for (int failureIndex = 0; failureIndex < orderedFailureLogins.Count; failureIndex++)
                {
                    PendingDisconnectCaptureFailure failure = _failedDisconnectCaptureFences[orderedFailureLogins[failureIndex]];
                    failure.SpoolDurable = true;
                }
                return true;
            }
            catch (Exception ex)
            {
                DungeonRunners.Runtime.EngineRuntime.SetExitCode(Math.Max(1, DungeonRunners.Runtime.EngineRuntime.ExitCode));
                Debug.LogError($"[SAVE] state=failed phase=disconnect-spool-write errorType={ex.GetType().Name} message='{ex.Message}'");
                return false;
            }
        }

        private void RestorePendingDisconnectedCharacterSpool()
        {
            string spoolPath = GetPendingDisconnectedCharacterSpoolPath();
            string tempPath = DataPaths.RequireServerPath(spoolPath + ".tmp");
            bool hasInterruptedWrite = File.Exists(tempPath);
            if (!hasInterruptedWrite && !File.Exists(spoolPath))
                return;
            string recoveryPath = hasInterruptedWrite ? tempPath : spoolPath;
            var fileInfo = new FileInfo(recoveryPath);
            if (fileInfo.Length <= 0 || fileInfo.Length > MaxPendingDisconnectedCharacterSpoolBytes)
                throw new InvalidDataException($"Pending disconnect spool size is invalid: {fileInfo.Length}");
            PendingDisconnectedCharacterSpool spool;
            using (var stream = new FileStream(recoveryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                spool = JsonSerializer.Deserialize<PendingDisconnectedCharacterSpool>(stream, PendingDisconnectedCharacterSpoolJsonOptions);
            if (spool == null || spool.Version != PendingDisconnectedCharacterSpoolVersion
                || spool.Entries == null || spool.FailedCaptureFences == null
                || spool.Entries.Count > MaxPendingDisconnectedCharacterSpoolEntries
                || spool.FailedCaptureFences.Count > MaxPendingDisconnectedCharacterSpoolEntries)
                throw new InvalidDataException("Pending disconnect spool structure is invalid");

            var logins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var characterIds = new HashSet<uint>();
            var restoredFailures = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            var restoredPending = new List<PendingDisconnectedCharacterSave>();
            var orderedSpoolFailureLogins = new List<string>(spool.FailedCaptureFences.Keys);
            orderedSpoolFailureLogins.Sort(StringComparer.OrdinalIgnoreCase);
            for (int failureIndex = 0; failureIndex < orderedSpoolFailureLogins.Count; failureIndex++)
            {
                string failureLogin = orderedSpoolFailureLogins[failureIndex];
                uint failureCharacterId = spool.FailedCaptureFences[failureLogin];
                if (string.IsNullOrWhiteSpace(failureLogin) || failureLogin.Length > 256
                    || failureCharacterId > int.MaxValue || !logins.Add(failureLogin)
                    || (failureCharacterId != 0 && !characterIds.Add(failureCharacterId)))
                    throw new InvalidDataException("Pending disconnect capture fence is invalid or duplicated");
                restoredFailures.Add(failureLogin, failureCharacterId);
            }
            for (int index = 0; index < spool.Entries.Count; index++)
            {
                PendingDisconnectedCharacterSpoolEntry entry = spool.Entries[index];
                if (entry == null || string.IsNullOrWhiteSpace(entry.LoginName) || entry.LoginName.Length > 256
                    || entry.Snapshot == null || entry.Snapshot.id == 0 || entry.Snapshot.id > int.MaxValue
                    || entry.ModifierDurations == null || entry.Attempts < 0
                    || !CharacterRepository.IsValidDisconnectSnapshotPayload(entry.Snapshot, entry.LoginName, entry.ModifierDurations)
                    || !logins.Add(entry.LoginName) || !characterIds.Add(entry.Snapshot.id))
                    throw new InvalidDataException("Pending disconnect snapshot is invalid or duplicated");
                var pending = new PendingDisconnectedCharacterSave(
                    entry.LoginName,
                    entry.ConnectionId,
                    entry.Snapshot,
                    entry.ModifierDurations)
                {
                    Attempts = entry.Attempts,
                    SpoolDurable = true,
                    DatabaseCommitted = entry.DatabaseCommitted
                };
                restoredPending.Add(pending);
            }
            if (hasInterruptedWrite)
                File.Move(tempPath, spoolPath, true);
            var registeredCharacterIds = new List<uint>();
            try
            {
                var orderedCharacterIds = new List<uint>(characterIds);
                orderedCharacterIds.Sort();
                for (int characterIndex = 0; characterIndex < orderedCharacterIds.Count; characterIndex++)
                {
                    uint characterId = orderedCharacterIds[characterIndex];
                    if (!CharacterRepository.TryRegisterPendingFullSnapshot(characterId))
                        throw new InvalidDataException($"Pending disconnect character fence is already registered characterId={characterId}");
                    registeredCharacterIds.Add(characterId);
                }
            }
            catch
            {
                for (int index = 0; index < registeredCharacterIds.Count; index++)
                    CharacterRepository.ReleasePendingFullSnapshot(registeredCharacterIds[index]);
                throw;
            }
            var orderedRestoredFailureLogins = new List<string>(restoredFailures.Keys);
            orderedRestoredFailureLogins.Sort(StringComparer.OrdinalIgnoreCase);
            for (int failureIndex = 0; failureIndex < orderedRestoredFailureLogins.Count; failureIndex++)
            {
                string failureLogin = orderedRestoredFailureLogins[failureIndex];
                uint failureCharacterId = restoredFailures[failureLogin];
                _failedDisconnectCaptureFences.Add(
                    failureLogin,
                    new PendingDisconnectCaptureFailure(failureLogin, failureCharacterId) { SpoolDurable = true });
                Debug.LogError($"[SAVE] state=quarantined phase=disconnect-capture-restore login={failureLogin} characterId={failureCharacterId}");
            }
            _pendingDisconnectedCharacterSaves.AddRange(restoredPending);
            DrainPendingDisconnectedCharacterSaves();
            if (_pendingDisconnectedCharacterSaves.Count != 0)
                throw new InvalidDataException("Pending disconnect spool could not be reconciled before startup");
            if (!TryPersistPendingDisconnectedCharacterSpool())
                throw new InvalidDataException("Pending disconnect spool could not be finalized before startup");
        }

        private bool TryFinalizePendingDisconnectedCharacterSave(int pendingIndex, PendingDisconnectedCharacterSave pendingSave)
        {
            if (pendingIndex < 0 || pendingIndex >= _pendingDisconnectedCharacterSaves.Count
                || !ReferenceEquals(_pendingDisconnectedCharacterSaves[pendingIndex], pendingSave))
                return false;
            _pendingDisconnectedCharacterSaves.RemoveAt(pendingIndex);
            if (!TryPersistPendingDisconnectedCharacterSpool())
            {
                _pendingDisconnectedCharacterSaves.Insert(pendingIndex, pendingSave);
                return false;
            }
            CharacterRepository.ReleasePendingFullSnapshot(pendingSave.CharacterId);
            return true;
        }

        private bool TryCaptureDisconnectCharacterSnapshot(RRConnection conn, out PendingDisconnectedCharacterSave pendingSave)
        {
            pendingSave = null;
            if (conn == null || string.IsNullOrWhiteSpace(conn.LoginName)
                || !_activeCharacter.TryGetValue(conn.LoginName, out SavedCharacter activeCharacter)
                || activeCharacter == null || activeCharacter.id == 0)
                return false;
            var modifierDurations = new Dictionary<uint, uint>();
            if (_unitContainer != null
                && !_unitContainer.TryCaptureConnectionRuntimeDurations(conn, out modifierDurations))
                return false;
            if (!TryCaptureCharacterSnapshot(
                    conn,
                    "disconnect-capture",
                    activeCharacter,
                    activeCharacter,
                    out SavedCharacter savedCharacter))
                return false;
            ClearDisconnectTransientCharacterState(savedCharacter);
            pendingSave = new PendingDisconnectedCharacterSave(
                conn.LoginName,
                conn.ConnId,
                savedCharacter,
                modifierDurations);
            return true;
        }

        private bool EnqueuePendingDisconnectedCharacterSave(PendingDisconnectedCharacterSave pendingSave)
        {
            if (pendingSave == null
                || _pendingDisconnectedCharacterSaves.Count >= MaxPendingDisconnectedCharacterSpoolEntries
                || HasPendingDisconnectedCharacterSaveForCharacter(pendingSave.CharacterId)
                || HasPendingDisconnectedCharacterSaveForLogin(pendingSave.LoginName)
                || !CharacterRepository.TryRegisterPendingFullSnapshot(pendingSave.CharacterId))
                return false;
            _pendingDisconnectedCharacterSaves.Add(pendingSave);
            if (!TryPersistPendingDisconnectedCharacterSpool())
                Debug.LogError($"[SAVE] state=pending phase=disconnect-spool-ownership characterId={pendingSave.CharacterId} conn={pendingSave.ConnectionId}");
            return true;
        }

        private bool HasPendingDisconnectedCharacterSaveForCharacter(uint characterId)
        {
            if (characterId == 0)
                return false;
            if (CharacterRepository.HasPendingFullSnapshot(characterId))
                return true;
            for (int index = 0; index < _pendingDisconnectedCharacterSaves.Count; index++)
                if (_pendingDisconnectedCharacterSaves[index].CharacterId == characterId)
                    return true;
            var orderedFailureLogins = new List<string>(_failedDisconnectCaptureFences.Keys);
            orderedFailureLogins.Sort(StringComparer.OrdinalIgnoreCase);
            for (int failureIndex = 0; failureIndex < orderedFailureLogins.Count; failureIndex++)
                if (_failedDisconnectCaptureFences[orderedFailureLogins[failureIndex]].CharacterId == characterId)
                    return true;
            return false;
        }

        private bool HasFailedDisconnectCaptureFenceForLogin(string loginName)
        {
            return !string.IsNullOrWhiteSpace(loginName)
                && _failedDisconnectCaptureFences.ContainsKey(loginName);
        }

        private bool HasPendingDisconnectedCharacterSaveForLogin(string loginName)
        {
            if (string.IsNullOrWhiteSpace(loginName))
                return false;
            if (_failedDisconnectCaptureFences.ContainsKey(loginName))
                return true;
            for (int index = 0; index < _pendingDisconnectedCharacterSaves.Count; index++)
                if (string.Equals(_pendingDisconnectedCharacterSaves[index].LoginName, loginName, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private bool IsPendingDisconnectedCharacterSaveCurrent(PendingDisconnectedCharacterSave pendingSave)
        {
            if (pendingSave == null)
                return false;
            for (int index = 0; index < _pendingDisconnectedCharacterSaves.Count; index++)
                if (ReferenceEquals(_pendingDisconnectedCharacterSaves[index], pendingSave))
                    return true;
            return false;
        }

        private bool HasConflictingCharacterWriter(PendingDisconnectedCharacterSave pendingSave)
        {
            if (pendingSave == null)
                return true;
            foreach (RRConnection candidate in GetConnectionInsertionOrderSnapshot())
            {
                if (candidate == null || !candidate.LoginAdmitted || candidate.ConnId == pendingSave.ConnectionId)
                    continue;
                if (candidate.CharSqlId == pendingSave.CharacterId)
                    return true;
            }
            return false;
        }

        private bool TryCommitPendingDisconnectedCharacterSave(PendingDisconnectedCharacterSave pendingSave)
        {
            if (!IsPendingDisconnectedCharacterSaveCurrent(pendingSave)
                || HasConflictingCharacterWriter(pendingSave))
                return false;
            pendingSave.Attempts++;
            bool committed = CharacterRepository.TrySaveDisconnectSnapshot(
                pendingSave.Snapshot.DeepClone(),
                pendingSave.LoginName,
                pendingSave.ModifierDurations,
                "disconnect-retry");
            pendingSave.DatabaseCommitted |= committed;
            Debug.LogError($"[SAVE] state={(committed ? "committed" : "pending")} phase=disconnect-retry characterId={pendingSave.CharacterId} conn={pendingSave.ConnectionId} attempts={pendingSave.Attempts}");
            return committed;
        }

        private bool IsPendingDisconnectedCharacterSaveDurable(PendingDisconnectedCharacterSave pendingSave)
        {
            return pendingSave != null
                && (pendingSave.SpoolDurable || pendingSave.DatabaseCommitted || !IsPendingDisconnectedCharacterSaveCurrent(pendingSave));
        }

        private void DrainPendingDisconnectedCharacterSaves()
        {
            for (int pendingIndex = 0; pendingIndex < _pendingDisconnectedCharacterSaves.Count;)
            {
                PendingDisconnectedCharacterSave pendingSave = _pendingDisconnectedCharacterSaves[pendingIndex];
                if (!TryCommitPendingDisconnectedCharacterSave(pendingSave))
                {
                    pendingIndex++;
                    continue;
                }
                if (!TryFinalizePendingDisconnectedCharacterSave(pendingIndex, pendingSave))
                {
                    pendingIndex++;
                    continue;
                }
            }
            DrainDeferredDisconnectPvpMatches();
        }

        private PendingDisconnectCaptureFailure MarkDisconnectCaptureFailure(RRConnection conn)
        {
            if (conn == null || string.IsNullOrWhiteSpace(conn.LoginName))
                return null;
            uint characterId = conn.CharSqlId;
            if (characterId == 0 && _activeCharacter.TryGetValue(conn.LoginName, out SavedCharacter activeCharacter))
                characterId = activeCharacter?.id ?? 0;
            if (_failedDisconnectCaptureFences.TryGetValue(conn.LoginName, out PendingDisconnectCaptureFailure existingFailure))
                return existingFailure;
            if (characterId != 0
                && !CharacterRepository.TryRegisterPendingFullSnapshot(characterId)
                && !CharacterRepository.HasPendingFullSnapshot(characterId))
            {
                Debug.LogError($"[SAVE] state=failed phase=disconnect-capture-character-fence login={conn.LoginName} characterId={characterId}");
                characterId = 0;
            }
            var failure = new PendingDisconnectCaptureFailure(conn.LoginName, characterId);
            _failedDisconnectCaptureFences.Add(conn.LoginName, failure);
            if (!TryPersistPendingDisconnectedCharacterSpool())
                Debug.LogError($"[SAVE] state=pending phase=disconnect-capture-spool login={conn.LoginName} characterId={characterId}");
            DungeonRunners.Runtime.EngineRuntime.SetExitCode(Math.Max(1, DungeonRunners.Runtime.EngineRuntime.ExitCode));
            Debug.LogError($"[SAVE] state=failed phase=disconnect-capture-fenced conn={conn.ConnId} characterId={characterId}");
            return failure;
        }

        private bool IsFailedDisconnectCaptureFenceDurable(PendingDisconnectCaptureFailure failure)
        {
            return failure != null
                && failure.SpoolDurable
                && _failedDisconnectCaptureFences.TryGetValue(failure.LoginName, out PendingDisconnectCaptureFailure current)
                && ReferenceEquals(current, failure);
        }

        private bool HasUndurableDisconnectCaptureFailure()
        {
            var orderedFailureLogins = new List<string>(_failedDisconnectCaptureFences.Keys);
            orderedFailureLogins.Sort(StringComparer.OrdinalIgnoreCase);
            for (int failureIndex = 0; failureIndex < orderedFailureLogins.Count; failureIndex++)
                if (!IsFailedDisconnectCaptureFenceDurable(_failedDisconnectCaptureFences[orderedFailureLogins[failureIndex]]))
                    return true;
            return false;
        }

        private bool TryFlushPendingDisconnectedCharacterSaveForLogin(string loginName)
        {
            if (string.IsNullOrWhiteSpace(loginName) || _failedDisconnectCaptureFences.ContainsKey(loginName))
                return false;
            for (int pendingIndex = 0; pendingIndex < _pendingDisconnectedCharacterSaves.Count; pendingIndex++)
            {
                PendingDisconnectedCharacterSave pendingSave = _pendingDisconnectedCharacterSaves[pendingIndex];
                if (!string.Equals(pendingSave.LoginName, loginName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!TryCommitPendingDisconnectedCharacterSave(pendingSave))
                    return false;
                if (!TryFinalizePendingDisconnectedCharacterSave(pendingIndex, pendingSave))
                    return false;
                DrainDeferredDisconnectPvpMatches();
                return true;
            }
            return true;
        }

        private bool TrySetCurrentCharacterSelection(string loginName, uint characterId, uint previousCharacterId)
        {
            if (string.IsNullOrWhiteSpace(loginName) || characterId == 0 || characterId > int.MaxValue
                || previousCharacterId > int.MaxValue)
                return false;
            try
            {
                using var connection = GameDatabase.GetConnection();
                using var transaction = connection.BeginTransaction();
                object currentValue = GameDatabase.ExecuteScalar(
                    connection,
                    "SELECT current_character_id FROM accounts WHERE username=@login COLLATE NOCASE",
                    ("@login", loginName));
                if (currentValue == null || currentValue == DBNull.Value)
                    return false;
                long currentCharacterId = Convert.ToInt64(currentValue);
                if (currentCharacterId != 0 && currentCharacterId != characterId && currentCharacterId != previousCharacterId)
                    return false;
                if (currentCharacterId != characterId)
                {
                    GameDatabase.ExecuteNonQuery(
                        connection,
                        "UPDATE accounts SET current_character_id=@cid WHERE username=@login COLLATE NOCASE AND current_character_id=@expected",
                        ("@cid", (int)characterId),
                        ("@login", loginName),
                        ("@expected", checked((int)currentCharacterId)));
                    int affected = Convert.ToInt32(GameDatabase.ExecuteScalar(connection, "SELECT changes()") ?? 0);
                    if (affected != 1)
                        return false;
                }
                transaction.Commit();
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SAVE] state=failed phase=current-character-set login={loginName} characterId={characterId} errorType={ex.GetType().Name} message='{ex.Message}'");
                return false;
            }
        }

        private bool TryDeferPvpMatchForPendingDisconnect(PVPMatchmaking.Match match)
        {
            if (match == null)
                return false;
            bool blocked = false;
            for (int participantIndex = 0; participantIndex < match.ParticipantLogins.Count; participantIndex++)
                if (match.ParticipantCharacterIds.TryGetValue(match.ParticipantLogins[participantIndex], out uint participantCharacterId)
                    && HasPendingDisconnectedCharacterSaveForCharacter(participantCharacterId))
                {
                    blocked = true;
                    break;
                }
            if (!blocked)
                return false;
            for (int index = 0; index < _deferredDisconnectPvpMatches.Count; index++)
                if (ReferenceEquals(_deferredDisconnectPvpMatches[index], match)
                    || string.Equals(_deferredDisconnectPvpMatches[index].MatchId, match.MatchId, StringComparison.Ordinal))
                    return true;
            _deferredDisconnectPvpMatches.Add(match);
            Debug.LogError($"[PVP-MATCH] match={match.MatchId ?? "unknown"} state=deferred reason=pending-disconnect-save");
            return true;
        }

        private void DrainDeferredDisconnectPvpMatches()
        {
            for (int matchIndex = 0; matchIndex < _deferredDisconnectPvpMatches.Count;)
            {
                PVPMatchmaking.Match match = _deferredDisconnectPvpMatches[matchIndex];
                bool blocked = false;
                for (int participantIndex = 0; participantIndex < match.ParticipantLogins.Count; participantIndex++)
                    if (match.ParticipantCharacterIds.TryGetValue(match.ParticipantLogins[participantIndex], out uint participantCharacterId)
                        && HasPendingDisconnectedCharacterSaveForCharacter(participantCharacterId))
                    {
                        blocked = true;
                        break;
                    }
                if (blocked)
                {
                    matchIndex++;
                    continue;
                }
                _deferredDisconnectPvpMatches.RemoveAt(matchIndex);
                FinalizeMatchResults(match);
            }
        }
    }
}
