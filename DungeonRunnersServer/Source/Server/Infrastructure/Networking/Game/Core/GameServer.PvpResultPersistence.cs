using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using DungeonRunners.Core;
using DungeonRunners.Data;
using DungeonRunners.Database;
using DungeonRunners.Engine;

namespace DungeonRunners.Networking
{
    public partial class GameServer
    {
        private const int PendingPvpResultJournalVersion = 1;
        private const int MaxPendingPvpResultJournalEntries = 256;
        private const long MaxPendingPvpResultJournalBytes = 16L * 1024L * 1024L;
        private static readonly JsonSerializerOptions PendingPvpResultJournalJsonOptions = new JsonSerializerOptions
        {
            IncludeFields = true,
            PropertyNameCaseInsensitive = true
        };

        private sealed class PendingPvpResultJournal
        {
            public int Version;
            public List<PendingPvpResultJournalEntry> Entries = new List<PendingPvpResultJournalEntry>();
        }

        private sealed class PendingPvpResultJournalEntry
        {
            public string ResultId;
            public string MatchId;
            public int Archetype;
            public bool HasWinner;
            public List<PvpParticipantResult> Participants = new List<PvpParticipantResult>();
        }

        private readonly List<PvpMatchResultWrite> _pendingPvpResultJournal = new List<PvpMatchResultWrite>();

        private static string GetPendingPvpResultJournalPath()
        {
            return DataPaths.ServerPath("Data", "Persistence", "pending-pvp-match-results.json");
        }

        private bool TryPersistPvpResultDurably(PvpMatchResultWrite result)
        {
            if (!PvpResultRepository.IsValidMatchResult(result))
                return false;
            int existingIndex = _pendingPvpResultJournal.FindIndex(existing =>
                string.Equals(existing.ResultId, result.ResultId, StringComparison.Ordinal));
            if (existingIndex >= 0)
            {
                if (!PvpResultRepository.MatchPayloadEquals(_pendingPvpResultJournal[existingIndex], result))
                    return false;
            }
            else
            {
                if (_pendingPvpResultJournal.Count >= MaxPendingPvpResultJournalEntries)
                    return false;
                _pendingPvpResultJournal.Add(ClonePvpMatchResult(result));
                existingIndex = _pendingPvpResultJournal.Count - 1;
            }

            bool journalDurable = TryPersistPendingPvpResultJournal();
            bool databaseDurable = PvpResultRepository.TryCreateMatchResult(result, out _);
            if (databaseDurable)
                TryRemoveMaterializedPvpResultJournalEntry(result.ResultId);
            return journalDurable || databaseDurable;
        }

        private bool HasPendingPvpResultFence(uint characterId)
        {
            if (characterId == 0 || characterId > int.MaxValue)
                return true;
            return HasPendingPvpResultJournalFence(characterId)
                || PvpResultRepository.HasPendingResultForCharacter(characterId);
        }

        private bool HasPendingPvpResultJournalFence(uint characterId)
        {
            if (characterId == 0 || characterId > int.MaxValue)
                return true;
            return _pendingPvpResultJournal.Any(result =>
                result.Participants.Any(participant => participant.CharacterId == characterId));
        }

        private SavedCharacter ResolvePvpResultBaseline(string loginName, uint characterId)
        {
            for (int pendingIndex = 0; pendingIndex < _pendingDisconnectedCharacterSaves.Count; pendingIndex++)
            {
                PendingDisconnectedCharacterSave pending = _pendingDisconnectedCharacterSaves[pendingIndex];
                if (pending.CharacterId == characterId
                    && string.Equals(pending.LoginName, loginName, StringComparison.OrdinalIgnoreCase))
                    return pending.Snapshot.DeepClone();
            }
            if (!string.IsNullOrWhiteSpace(loginName)
                && _activeCharacter.TryGetValue(loginName, out SavedCharacter activeCharacter)
                && activeCharacter != null && activeCharacter.id == characterId)
                return activeCharacter.DeepClone();
            return CharacterRepository.GetCharacter(characterId);
        }

        private void RestorePendingPvpResultJournal()
        {
            string journalPath = GetPendingPvpResultJournalPath();
            string tempPath = DataPaths.RequireServerPath(journalPath + ".tmp");
            bool hasInterruptedWrite = File.Exists(tempPath);
            if (!hasInterruptedWrite && !File.Exists(journalPath))
                return;
            string recoveryPath = hasInterruptedWrite ? tempPath : journalPath;
            var fileInfo = new FileInfo(recoveryPath);
            if (fileInfo.Length <= 0 || fileInfo.Length > MaxPendingPvpResultJournalBytes)
                throw new InvalidDataException($"Pending PvP result journal size is invalid: {fileInfo.Length}");
            PendingPvpResultJournal journal;
            using (var stream = new FileStream(recoveryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                journal = JsonSerializer.Deserialize<PendingPvpResultJournal>(stream, PendingPvpResultJournalJsonOptions);
            if (journal == null || journal.Version != PendingPvpResultJournalVersion
                || journal.Entries == null || journal.Entries.Count > MaxPendingPvpResultJournalEntries)
                throw new InvalidDataException("Pending PvP result journal structure is invalid");
            var resultIds = new HashSet<string>(StringComparer.Ordinal);
            var restored = new List<PvpMatchResultWrite>();
            for (int entryIndex = 0; entryIndex < journal.Entries.Count; entryIndex++)
            {
                PendingPvpResultJournalEntry entry = journal.Entries[entryIndex];
                var result = new PvpMatchResultWrite
                {
                    ResultId = entry?.ResultId,
                    MatchId = entry?.MatchId,
                    Archetype = entry?.Archetype ?? -1,
                    HasWinner = entry?.HasWinner ?? false,
                    Participants = entry?.Participants
                };
                if (!PvpResultRepository.IsValidMatchResult(result) || !resultIds.Add(result.ResultId))
                    throw new InvalidDataException("Pending PvP result journal entry is invalid or duplicated");
                restored.Add(ClonePvpMatchResult(result));
            }
            if (hasInterruptedWrite)
                File.Move(tempPath, journalPath, true);
            _pendingPvpResultJournal.Clear();
            _pendingPvpResultJournal.AddRange(restored);
            DrainPendingPvpResultJournalToDatabase();
            if (_pendingPvpResultJournal.Count != 0)
                throw new InvalidDataException("Pending PvP result journal could not be materialized before startup");
            if (!TryPersistPendingPvpResultJournal())
                throw new InvalidDataException("Pending PvP result journal could not be finalized before startup");
        }

        private void DrainPendingPvpResultJournalToDatabase()
        {
            for (int journalIndex = 0; journalIndex < _pendingPvpResultJournal.Count;)
            {
                PvpMatchResultWrite result = _pendingPvpResultJournal[journalIndex];
                if (!PvpResultRepository.TryCreateMatchResult(result, out _))
                {
                    journalIndex++;
                    continue;
                }
                if (!TryRemoveMaterializedPvpResultJournalEntry(result.ResultId))
                {
                    journalIndex++;
                    continue;
                }
            }
        }

        private bool TryRemoveMaterializedPvpResultJournalEntry(string resultId)
        {
            int journalIndex = _pendingPvpResultJournal.FindIndex(result =>
                string.Equals(result.ResultId, resultId, StringComparison.Ordinal));
            if (journalIndex < 0)
                return true;
            PvpMatchResultWrite result = _pendingPvpResultJournal[journalIndex];
            _pendingPvpResultJournal.RemoveAt(journalIndex);
            if (TryPersistPendingPvpResultJournal())
                return true;
            _pendingPvpResultJournal.Insert(journalIndex, result);
            return false;
        }

        private bool TryPersistPendingPvpResultJournal()
        {
            try
            {
                var journal = new PendingPvpResultJournal
                {
                    Version = PendingPvpResultJournalVersion
                };
                for (int resultIndex = 0; resultIndex < _pendingPvpResultJournal.Count; resultIndex++)
                {
                    PvpMatchResultWrite result = _pendingPvpResultJournal[resultIndex];
                    journal.Entries.Add(new PendingPvpResultJournalEntry
                    {
                        ResultId = result.ResultId,
                        MatchId = result.MatchId,
                        Archetype = result.Archetype,
                        HasWinner = result.HasWinner,
                        Participants = result.Participants.Select(ClonePvpParticipantResult).ToList()
                    });
                }
                string json = JsonSerializer.Serialize(journal, PendingPvpResultJournalJsonOptions);
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                if (bytes.LongLength > MaxPendingPvpResultJournalBytes)
                    throw new InvalidDataException($"Pending PvP result journal exceeds {MaxPendingPvpResultJournalBytes} bytes");
                string journalPath = GetPendingPvpResultJournalPath();
                Directory.CreateDirectory(Path.GetDirectoryName(journalPath));
                string tempPath = DataPaths.RequireServerPath(journalPath + ".tmp");
                using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                File.Move(tempPath, journalPath, true);
                return true;
            }
            catch (Exception ex)
            {
                DungeonRunners.Runtime.EngineRuntime.SetExitCode(Math.Max(1, DungeonRunners.Runtime.EngineRuntime.ExitCode));
                Debug.LogError($"[PVP-MATCH] state=journal-write-failed errorType={ex.GetType().Name} message='{ex.Message}'");
                return false;
            }
        }

        private static PvpMatchResultWrite ClonePvpMatchResult(PvpMatchResultWrite result)
        {
            return new PvpMatchResultWrite
            {
                ResultId = result.ResultId,
                MatchId = result.MatchId,
                Archetype = result.Archetype,
                HasWinner = result.HasWinner,
                Participants = result.Participants.Select(ClonePvpParticipantResult).ToList()
            };
        }

        private static PvpParticipantResult ClonePvpParticipantResult(PvpParticipantResult participant)
        {
            return new PvpParticipantResult
            {
                ResultId = participant.ResultId,
                MatchId = participant.MatchId,
                Archetype = participant.Archetype,
                HasWinner = participant.HasWinner,
                ParticipantOrder = participant.ParticipantOrder,
                CharacterId = participant.CharacterId,
                LoginName = participant.LoginName,
                IsWinner = participant.IsWinner,
                IsRanked = participant.IsRanked,
                RatingBefore = participant.RatingBefore,
                RatingAfter = participant.RatingAfter,
                PersistedRatingBefore = participant.PersistedRatingBefore,
                ApplyRating = participant.ApplyRating,
                WinsBefore = participant.WinsBefore,
                WinsDelta = participant.WinsDelta,
                RewardGcClass = participant.RewardGcClass,
                RewardCount = participant.RewardCount,
                RewardWidth = participant.RewardWidth,
                RewardHeight = participant.RewardHeight,
                RewardRarity = participant.RewardRarity,
                ReturnZone = participant.ReturnZone,
                ReturnZoneId = participant.ReturnZoneId,
                ReturnPosFixedX = participant.ReturnPosFixedX,
                ReturnPosFixedY = participant.ReturnPosFixedY,
                ReturnPosFixedZ = participant.ReturnPosFixedZ,
                Applied = participant.Applied,
                ReturnCompleted = participant.ReturnCompleted,
                PendingGrantId = participant.PendingGrantId
            };
        }
    }
}
