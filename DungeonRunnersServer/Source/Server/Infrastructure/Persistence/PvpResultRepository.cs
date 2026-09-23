using System;
using System.Collections.Generic;
using System.IO;
using DungeonRunners.Data;
using DungeonRunners.Engine;
using Mono.Data.Sqlite;

namespace DungeonRunners.Database
{
    public sealed class PvpParticipantResult
    {
        public string ResultId;
        public string MatchId;
        public int Archetype;
        public bool HasWinner;
        public int ParticipantOrder;
        public uint CharacterId;
        public string LoginName;
        public bool IsWinner;
        public bool IsRanked;
        public int RatingBefore;
        public int RatingAfter;
        public int PersistedRatingBefore;
        public bool ApplyRating;
        public int WinsBefore;
        public int WinsDelta;
        public string RewardGcClass;
        public int RewardCount;
        public int RewardWidth;
        public int RewardHeight;
        public int RewardRarity;
        public string ReturnZone;
        public int ReturnZoneId;
        public int ReturnPosFixedX;
        public int ReturnPosFixedY;
        public int ReturnPosFixedZ;
        public bool Applied;
        public bool ReturnCompleted;
        public long PendingGrantId;
    }

    public sealed class PvpMatchResultWrite
    {
        public string ResultId;
        public string MatchId;
        public int Archetype;
        public bool HasWinner;
        public IReadOnlyList<PvpParticipantResult> Participants;
    }

    public static class PvpResultRepository
    {
        public static bool TryCreateMatchResult(PvpMatchResultWrite result, out bool alreadyCreated)
        {
            alreadyCreated = false;
            if (!IsValidMatchResult(result))
                return false;
            try
            {
                using var connection = GameDatabase.GetConnection();
                using var transaction = connection.BeginTransaction();
                GameDatabase.ExecuteNonQuery(
                    connection,
                    @"INSERT OR IGNORE INTO pvp_match_results
                      (result_id, match_id, archetype, has_winner, participant_count)
                      VALUES (@resultId, @matchId, @archetype, @hasWinner, @participantCount)",
                    ("@resultId", result.ResultId),
                    ("@matchId", result.MatchId),
                    ("@archetype", result.Archetype),
                    ("@hasWinner", result.HasWinner ? 1 : 0),
                    ("@participantCount", result.Participants.Count));
                int insertedHeader = Convert.ToInt32(GameDatabase.ExecuteScalar(connection, "SELECT changes()") ?? 0);
                if (insertedHeader == 0)
                {
                    if (!ExistingResultMatches(connection, result))
                        throw new InvalidDataException($"PvP result identity collision resultId={result.ResultId}");
                    transaction.Rollback();
                    alreadyCreated = true;
                    return true;
                }
                if (insertedHeader != 1)
                    throw new InvalidDataException($"PvP result header was not inserted exactly once resultId={result.ResultId}");

                for (int participantIndex = 0; participantIndex < result.Participants.Count; participantIndex++)
                {
                    PvpParticipantResult participant = result.Participants[participantIndex];
                    int matchingCharacter = Convert.ToInt32(GameDatabase.ExecuteScalar(
                        connection,
                        @"SELECT COUNT(*) FROM characters c
                          JOIN accounts a ON a.id=c.account_id
                          WHERE c.id=@characterId AND a.username=@login COLLATE NOCASE",
                        ("@characterId", (int)participant.CharacterId),
                        ("@login", participant.LoginName)) ?? 0);
                    if (matchingCharacter != 1)
                        throw new InvalidDataException($"PvP participant identity mismatch resultId={result.ResultId} order={participant.ParticipantOrder}");
                    GameDatabase.ExecuteNonQuery(
                        connection,
                        @"INSERT INTO pvp_participant_results
                          (result_id, participant_order, character_id, login_name, is_winner, is_ranked,
                           rating_before, rating_after, persisted_rating_before, apply_rating,
                           wins_before, wins_delta, reward_gc_class, reward_count, reward_width,
                           reward_height, reward_rarity, return_zone, return_zone_id,
                           return_pos_fixed_x, return_pos_fixed_y, return_pos_fixed_z)
                          VALUES
                          (@resultId, @participantOrder, @characterId, @login, @isWinner, @isRanked,
                           @ratingBefore, @ratingAfter, @persistedRatingBefore, @applyRating,
                           @winsBefore, @winsDelta, @rewardGcClass, @rewardCount, @rewardWidth,
                           @rewardHeight, @rewardRarity, @returnZone, @returnZoneId,
                           @returnPosFixedX, @returnPosFixedY, @returnPosFixedZ)",
                        ("@resultId", result.ResultId),
                        ("@participantOrder", participant.ParticipantOrder),
                        ("@characterId", (int)participant.CharacterId),
                        ("@login", participant.LoginName),
                        ("@isWinner", participant.IsWinner ? 1 : 0),
                        ("@isRanked", participant.IsRanked ? 1 : 0),
                        ("@ratingBefore", participant.RatingBefore),
                        ("@ratingAfter", participant.RatingAfter),
                        ("@persistedRatingBefore", participant.PersistedRatingBefore),
                        ("@applyRating", participant.ApplyRating ? 1 : 0),
                        ("@winsBefore", participant.WinsBefore),
                        ("@winsDelta", participant.WinsDelta),
                        ("@rewardGcClass", participant.RewardGcClass),
                        ("@rewardCount", participant.RewardCount),
                        ("@rewardWidth", participant.RewardWidth),
                        ("@rewardHeight", participant.RewardHeight),
                        ("@rewardRarity", participant.RewardRarity),
                        ("@returnZone", participant.ReturnZone),
                        ("@returnZoneId", participant.ReturnZoneId),
                        ("@returnPosFixedX", participant.ReturnPosFixedX),
                        ("@returnPosFixedY", participant.ReturnPosFixedY),
                        ("@returnPosFixedZ", participant.ReturnPosFixedZ));
                }
                transaction.Commit();
                Debug.LogError($"[PVP-MATCH] state=outbox-committed resultId={result.ResultId} match={result.MatchId} participants={result.Participants.Count}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PVP-MATCH] state=outbox-failed resultId={result?.ResultId ?? "unknown"} message='{ex.Message}'");
                return false;
            }
        }

        public static bool TryGetPendingParticipantResults(out List<PvpParticipantResult> results, uint characterId = 0, string resultId = null)
        {
            results = new List<PvpParticipantResult>();
            if (characterId > int.MaxValue || (!string.IsNullOrEmpty(resultId) && !IsResultId(resultId)))
                return false;
            try
            {
                using var connection = GameDatabase.GetConnection();
                string filter = characterId != 0
                    ? " AND p.character_id=@characterId"
                    : !string.IsNullOrEmpty(resultId)
                        ? " AND p.result_id=@resultId"
                        : string.Empty;
                using var command = connection.CreateCommand();
                command.CommandText = @"SELECT
                    p.result_id, m.match_id, m.archetype, m.has_winner, p.participant_order,
                    p.character_id, p.login_name, p.is_winner, p.is_ranked,
                    p.rating_before, p.rating_after, p.persisted_rating_before, p.apply_rating,
                    p.wins_before, p.wins_delta, p.reward_gc_class, p.reward_count,
                    p.reward_width, p.reward_height, p.reward_rarity, p.return_zone,
                    p.return_zone_id, p.return_pos_fixed_x, p.return_pos_fixed_y, p.return_pos_fixed_z,
                    CASE WHEN p.applied_at IS NULL THEN 0 ELSE 1 END,
                    CASE WHEN p.return_completed_at IS NULL THEN 0 ELSE 1 END,
                    p.pending_grant_id
                    FROM pvp_participant_results p
                    JOIN pvp_match_results m ON m.result_id=p.result_id
                    WHERE (p.applied_at IS NULL OR p.return_completed_at IS NULL)" + filter +
                    " ORDER BY m.created_at, p.result_id, p.participant_order";
                if (characterId != 0)
                    command.Parameters.AddWithValue("@characterId", (int)characterId);
                else if (!string.IsNullOrEmpty(resultId))
                    command.Parameters.AddWithValue("@resultId", resultId);
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                    results.Add(ReadParticipant(reader));
                return true;
            }
            catch (Exception ex)
            {
                results.Clear();
                Debug.LogError($"[PVP-MATCH] state=outbox-load-failed characterId={characterId} resultId={resultId ?? "all"} message='{ex.Message}'");
                return false;
            }
        }

        public static bool HasPendingResultForCharacter(uint characterId)
        {
            if (characterId == 0 || characterId > int.MaxValue)
                return true;
            try
            {
                using var connection = GameDatabase.GetConnection();
                int pending = Convert.ToInt32(GameDatabase.ExecuteScalar(
                    connection,
                    @"SELECT COUNT(*) FROM pvp_participant_results
                      WHERE character_id=@characterId
                        AND (applied_at IS NULL OR return_completed_at IS NULL)",
                    ("@characterId", (int)characterId)) ?? 0);
                return pending != 0;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PVP-MATCH] state=outbox-fence-failed characterId={characterId} message='{ex.Message}'");
                return true;
            }
        }

        internal static bool CanPersistCharacterSnapshot(SavedCharacter character)
        {
            if (character == null || character.id == 0 || character.id > int.MaxValue)
                return false;
            try
            {
                using var connection = GameDatabase.GetConnection();
                using var command = connection.CreateCommand();
                command.CommandText = @"SELECT applied_at, return_zone, return_zone_id,
                    return_pos_fixed_x, return_pos_fixed_y, return_pos_fixed_z
                    FROM pvp_participant_results
                    WHERE character_id=@characterId
                      AND (applied_at IS NULL OR return_completed_at IS NULL)
                    ORDER BY result_id, participant_order";
                command.Parameters.AddWithValue("@characterId", (int)character.id);
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.IsDBNull(0)
                        || !string.Equals(reader.GetString(1), character.currentZoneName, StringComparison.OrdinalIgnoreCase)
                        || reader.GetInt32(2) != character.zoneId
                        || reader.GetInt32(3) != character.positionFixedX
                        || reader.GetInt32(4) != character.positionFixedY
                        || reader.GetInt32(5) != character.positionFixedZ)
                        return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PVP-MATCH] state=snapshot-fence-failed characterId={character.id} message='{ex.Message}'");
                return false;
            }
        }

        internal static bool TryNormalizeDisconnectSnapshot(SavedCharacter character, out SavedCharacter normalized)
        {
            normalized = null;
            if (character == null || character.id == 0 || character.id > int.MaxValue)
                return false;
            try
            {
                normalized = character.DeepClone();
                using var connection = GameDatabase.GetConnection();
                using var command = connection.CreateCommand();
                command.CommandText = @"SELECT return_zone, return_zone_id,
                    return_pos_fixed_x, return_pos_fixed_y, return_pos_fixed_z
                    FROM pvp_participant_results
                    WHERE character_id=@characterId AND applied_at IS NOT NULL
                      AND return_completed_at IS NULL
                    ORDER BY result_id, participant_order";
                command.Parameters.AddWithValue("@characterId", (int)character.id);
                using SqliteDataReader reader = command.ExecuteReader();
                bool foundReturnIntent = false;
                string returnZone = null;
                int returnZoneId = 0;
                int returnPosFixedX = 0;
                int returnPosFixedY = 0;
                int returnPosFixedZ = 0;
                while (reader.Read())
                {
                    if (!foundReturnIntent)
                    {
                        returnZone = reader.GetString(0);
                        returnZoneId = reader.GetInt32(1);
                        returnPosFixedX = reader.GetInt32(2);
                        returnPosFixedY = reader.GetInt32(3);
                        returnPosFixedZ = reader.GetInt32(4);
                        foundReturnIntent = true;
                    }
                    else if (!string.Equals(returnZone, reader.GetString(0), StringComparison.OrdinalIgnoreCase)
                        || returnZoneId != reader.GetInt32(1)
                        || returnPosFixedX != reader.GetInt32(2)
                        || returnPosFixedY != reader.GetInt32(3)
                        || returnPosFixedZ != reader.GetInt32(4))
                    {
                        normalized = null;
                        return false;
                    }
                }
                if (foundReturnIntent)
                {
                    normalized.currentZoneName = returnZone;
                    normalized.zoneId = returnZoneId;
                    normalized.positionFixedX = returnPosFixedX;
                    normalized.positionFixedY = returnPosFixedY;
                    normalized.positionFixedZ = returnPosFixedZ;
                }
                return true;
            }
            catch (Exception ex)
            {
                normalized = null;
                Debug.LogError($"[PVP-MATCH] state=disconnect-return-normalize-failed characterId={character.id} message='{ex.Message}'");
                return false;
            }
        }

        public static bool TryMarkReturnCompleted(string resultId, int participantOrder)
        {
            if (!IsResultId(resultId) || participantOrder < 0 || participantOrder > byte.MaxValue)
                return false;
            try
            {
                using var connection = GameDatabase.GetConnection();
                using var transaction = connection.BeginTransaction();
                GameDatabase.ExecuteNonQuery(
                    connection,
                    @"UPDATE pvp_participant_results
                      SET return_completed_at=COALESCE(return_completed_at, datetime('now'))
                      WHERE result_id=@resultId AND participant_order=@participantOrder
                        AND applied_at IS NOT NULL",
                    ("@resultId", resultId),
                    ("@participantOrder", participantOrder));
                int changed = Convert.ToInt32(GameDatabase.ExecuteScalar(connection, "SELECT changes()") ?? 0);
                if (changed != 1)
                {
                    int alreadyCompleted = Convert.ToInt32(GameDatabase.ExecuteScalar(
                        connection,
                        @"SELECT COUNT(*) FROM pvp_participant_results
                          WHERE result_id=@resultId AND participant_order=@participantOrder
                            AND applied_at IS NOT NULL AND return_completed_at IS NOT NULL",
                        ("@resultId", resultId),
                        ("@participantOrder", participantOrder)) ?? 0);
                    if (alreadyCompleted != 1)
                        return false;
                }
                transaction.Commit();
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PVP-MATCH] state=return-mark-failed resultId={resultId ?? "unknown"} order={participantOrder} message='{ex.Message}'");
                return false;
            }
        }

        internal static bool TryGetParticipant(SqliteConnection connection, string resultId, int participantOrder, out PvpParticipantResult participant)
        {
            participant = null;
            if (connection == null || !IsResultId(resultId) || participantOrder < 0 || participantOrder > byte.MaxValue)
                return false;
            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT
                p.result_id, m.match_id, m.archetype, m.has_winner, p.participant_order,
                p.character_id, p.login_name, p.is_winner, p.is_ranked,
                p.rating_before, p.rating_after, p.persisted_rating_before, p.apply_rating,
                p.wins_before, p.wins_delta, p.reward_gc_class, p.reward_count,
                p.reward_width, p.reward_height, p.reward_rarity, p.return_zone,
                p.return_zone_id, p.return_pos_fixed_x, p.return_pos_fixed_y, p.return_pos_fixed_z,
                CASE WHEN p.applied_at IS NULL THEN 0 ELSE 1 END,
                CASE WHEN p.return_completed_at IS NULL THEN 0 ELSE 1 END,
                p.pending_grant_id
                FROM pvp_participant_results p
                JOIN pvp_match_results m ON m.result_id=p.result_id
                WHERE p.result_id=@resultId AND p.participant_order=@participantOrder";
            command.Parameters.AddWithValue("@resultId", resultId);
            command.Parameters.AddWithValue("@participantOrder", participantOrder);
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read())
                return false;
            participant = ReadParticipant(reader);
            return !reader.Read();
        }

        internal static bool IsValidParticipant(PvpParticipantResult participant)
        {
            if (participant == null || !IsResultId(participant.ResultId)
                || string.IsNullOrWhiteSpace(participant.MatchId) || participant.MatchId.Length > 128
                || participant.Archetype < 0 || participant.Archetype > 3
                || participant.ParticipantOrder < 0 || participant.ParticipantOrder > byte.MaxValue
                || participant.CharacterId == 0 || participant.CharacterId > int.MaxValue
                || string.IsNullOrWhiteSpace(participant.LoginName) || participant.LoginName.Length > 256
                || participant.RatingBefore < 0 || participant.RatingBefore > 3000
                || participant.RatingAfter < 0 || participant.RatingAfter > 3000
                || participant.PersistedRatingBefore < 0 || participant.PersistedRatingBefore > 3000
                || participant.WinsBefore < 0 || participant.WinsDelta < 0 || participant.WinsDelta > 1
                || participant.WinsBefore > int.MaxValue - participant.WinsDelta
                || string.IsNullOrWhiteSpace(participant.ReturnZone) || participant.ReturnZone.Length > 1024)
                return false;
            if (participant.IsWinner != (participant.WinsDelta == 1))
                return false;
            if (participant.ApplyRating && (!participant.IsRanked || !participant.HasWinner))
                return false;
            if (!participant.ApplyRating && participant.RatingAfter != participant.RatingBefore)
                return false;
            if (participant.RewardCount == 0)
                return string.IsNullOrEmpty(participant.RewardGcClass)
                    && participant.RewardWidth == 0 && participant.RewardHeight == 0
                    && participant.RewardRarity == -1;
            return participant.HasWinner && participant.IsRanked
                && !string.IsNullOrWhiteSpace(participant.RewardGcClass)
                && participant.RewardGcClass.Length <= 1024
                && participant.RewardCount > 0 && participant.RewardCount <= byte.MaxValue
                && participant.RewardWidth > 0 && participant.RewardWidth <= 10
                && participant.RewardHeight > 0 && participant.RewardHeight <= 8
                && participant.RewardRarity >= -1 && participant.RewardRarity <= 5;
        }

        internal static bool IsValidMatchResult(PvpMatchResultWrite result)
        {
            if (result == null || !IsResultId(result.ResultId)
                || string.IsNullOrWhiteSpace(result.MatchId) || result.MatchId.Length > 128
                || result.Archetype < 0 || result.Archetype > 3
                || result.Participants == null || result.Participants.Count == 0
                || result.Participants.Count > byte.MaxValue)
                return false;
            var characterIds = new HashSet<uint>();
            var logins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int winnerCount = 0;
            bool? ranked = null;
            for (int participantIndex = 0; participantIndex < result.Participants.Count; participantIndex++)
            {
                PvpParticipantResult participant = result.Participants[participantIndex];
                if (!IsValidParticipant(participant)
                    || participant.ParticipantOrder != participantIndex
                    || !string.Equals(participant.ResultId, result.ResultId, StringComparison.Ordinal)
                    || !string.Equals(participant.MatchId, result.MatchId, StringComparison.Ordinal)
                    || participant.Archetype != result.Archetype
                    || participant.HasWinner != result.HasWinner
                    || !characterIds.Add(participant.CharacterId)
                    || !logins.Add(participant.LoginName))
                    return false;
                ranked ??= participant.IsRanked;
                if (ranked.Value != participant.IsRanked)
                    return false;
                if (participant.IsWinner)
                    winnerCount++;
            }
            return result.HasWinner ? winnerCount > 0 : winnerCount == 0;
        }

        internal static bool MatchPayloadEquals(PvpMatchResultWrite left, PvpMatchResultWrite right)
        {
            if (left == null || right == null
                || left.ResultId != right.ResultId
                || left.MatchId != right.MatchId
                || left.Archetype != right.Archetype
                || left.HasWinner != right.HasWinner
                || left.Participants == null || right.Participants == null
                || left.Participants.Count != right.Participants.Count)
                return false;
            for (int participantIndex = 0; participantIndex < left.Participants.Count; participantIndex++)
                if (!ParticipantPayloadEquals(left.Participants[participantIndex], right.Participants[participantIndex]))
                    return false;
            return true;
        }

        private static bool ExistingResultMatches(SqliteConnection connection, PvpMatchResultWrite result)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT match_id, archetype, has_winner, participant_count
                    FROM pvp_match_results WHERE result_id=@resultId";
                command.Parameters.AddWithValue("@resultId", result.ResultId);
                using SqliteDataReader reader = command.ExecuteReader();
                if (!reader.Read()
                    || !string.Equals(reader.GetString(0), result.MatchId, StringComparison.Ordinal)
                    || reader.GetInt32(1) != result.Archetype
                    || (reader.GetInt32(2) != 0) != result.HasWinner
                    || reader.GetInt32(3) != result.Participants.Count
                    || reader.Read())
                    return false;
            }
            var existing = new List<PvpParticipantResult>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT
                    p.result_id, m.match_id, m.archetype, m.has_winner, p.participant_order,
                    p.character_id, p.login_name, p.is_winner, p.is_ranked,
                    p.rating_before, p.rating_after, p.persisted_rating_before, p.apply_rating,
                    p.wins_before, p.wins_delta, p.reward_gc_class, p.reward_count,
                    p.reward_width, p.reward_height, p.reward_rarity, p.return_zone,
                    p.return_zone_id, p.return_pos_fixed_x, p.return_pos_fixed_y, p.return_pos_fixed_z,
                    CASE WHEN p.applied_at IS NULL THEN 0 ELSE 1 END,
                    CASE WHEN p.return_completed_at IS NULL THEN 0 ELSE 1 END,
                    p.pending_grant_id
                    FROM pvp_participant_results p
                    JOIN pvp_match_results m ON m.result_id=p.result_id
                    WHERE p.result_id=@resultId ORDER BY p.participant_order";
                command.Parameters.AddWithValue("@resultId", result.ResultId);
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                    existing.Add(ReadParticipant(reader));
            }
            if (existing.Count != result.Participants.Count)
                return false;
            for (int participantIndex = 0; participantIndex < existing.Count; participantIndex++)
                if (!ParticipantPayloadEquals(existing[participantIndex], result.Participants[participantIndex]))
                    return false;
            return true;
        }

        private static PvpParticipantResult ReadParticipant(SqliteDataReader reader)
        {
            return new PvpParticipantResult
            {
                ResultId = reader.GetString(0),
                MatchId = reader.GetString(1),
                Archetype = reader.GetInt32(2),
                HasWinner = reader.GetInt32(3) != 0,
                ParticipantOrder = reader.GetInt32(4),
                CharacterId = checked((uint)reader.GetInt32(5)),
                LoginName = reader.GetString(6),
                IsWinner = reader.GetInt32(7) != 0,
                IsRanked = reader.GetInt32(8) != 0,
                RatingBefore = reader.GetInt32(9),
                RatingAfter = reader.GetInt32(10),
                PersistedRatingBefore = reader.GetInt32(11),
                ApplyRating = reader.GetInt32(12) != 0,
                WinsBefore = reader.GetInt32(13),
                WinsDelta = reader.GetInt32(14),
                RewardGcClass = reader.GetString(15),
                RewardCount = reader.GetInt32(16),
                RewardWidth = reader.GetInt32(17),
                RewardHeight = reader.GetInt32(18),
                RewardRarity = reader.GetInt32(19),
                ReturnZone = reader.GetString(20),
                ReturnZoneId = reader.GetInt32(21),
                ReturnPosFixedX = reader.GetInt32(22),
                ReturnPosFixedY = reader.GetInt32(23),
                ReturnPosFixedZ = reader.GetInt32(24),
                Applied = reader.GetInt32(25) != 0,
                ReturnCompleted = reader.GetInt32(26) != 0,
                PendingGrantId = reader.GetInt64(27)
            };
        }

        internal static bool ParticipantPayloadEquals(PvpParticipantResult left, PvpParticipantResult right)
        {
            return left.ResultId == right.ResultId
                && left.MatchId == right.MatchId
                && left.Archetype == right.Archetype
                && left.HasWinner == right.HasWinner
                && left.ParticipantOrder == right.ParticipantOrder
                && left.CharacterId == right.CharacterId
                && string.Equals(left.LoginName, right.LoginName, StringComparison.OrdinalIgnoreCase)
                && left.IsWinner == right.IsWinner
                && left.IsRanked == right.IsRanked
                && left.RatingBefore == right.RatingBefore
                && left.RatingAfter == right.RatingAfter
                && left.PersistedRatingBefore == right.PersistedRatingBefore
                && left.ApplyRating == right.ApplyRating
                && left.WinsBefore == right.WinsBefore
                && left.WinsDelta == right.WinsDelta
                && left.RewardGcClass == right.RewardGcClass
                && left.RewardCount == right.RewardCount
                && left.RewardWidth == right.RewardWidth
                && left.RewardHeight == right.RewardHeight
                && left.RewardRarity == right.RewardRarity
                && left.ReturnZone == right.ReturnZone
                && left.ReturnZoneId == right.ReturnZoneId
                && left.ReturnPosFixedX == right.ReturnPosFixedX
                && left.ReturnPosFixedY == right.ReturnPosFixedY
                && left.ReturnPosFixedZ == right.ReturnPosFixedZ;
        }

        private static bool IsResultId(string resultId)
        {
            return !string.IsNullOrWhiteSpace(resultId)
                && resultId.Length == 32
                && string.Equals(resultId, resultId.ToLowerInvariant(), StringComparison.Ordinal)
                && Guid.TryParseExact(resultId, "N", out _);
        }
    }
}
