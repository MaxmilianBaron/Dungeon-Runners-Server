using System;
using DungeonRunners.Engine;
using Mono.Data.Sqlite;

namespace DungeonRunners.Database
{
    public class PosseRecord
    {
        public uint Id;
        public string Name;
        public uint FounderCharacterId;
        public uint GoldPaid;
        public string FoundedAt;
        public string Motd;
        public string Description;
    }

    public static class PosseRepository
    {
        public static bool PosseNameExists(string name)
        {
            try
            {
                using (var connection = GameDatabase.GetConnection())
                {
                    object posseCount = GameDatabase.ExecuteScalar(connection,
                        "SELECT COUNT(*) FROM posses WHERE name = @n", ("@n", name));
                    return Convert.ToInt32(posseCount) > 0;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[POSSE-DB] operation=PosseNameExists state=failed message='{ex.Message}'");
                return false;
            }
        }

        public static PosseRecord CreatePosse(string name, uint founderCharacterId, uint goldPaid)
        {
            if (CharacterRepository.HasPendingFullSnapshot(founderCharacterId))
                return null;
            try
            {
                using (var connection = GameDatabase.GetConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    GameDatabase.ExecuteNonQuery(connection,
                        @"INSERT INTO posses (name, founder_character_id, gold_paid)
                          VALUES (@n, @fid, @g)",
                        ("@n", name), ("@fid", (int)founderCharacterId), ("@g", (int)goldPaid));

                    int posseId = Convert.ToInt32(GameDatabase.ExecuteScalar(connection, "SELECT last_insert_rowid()"));

                    GameDatabase.ExecuteNonQuery(connection,
                        @"UPDATE characters
                          SET posse_id = @pid, posse_rank_id = 10, gold = gold - @g
                          WHERE id = @cid AND posse_id = 0 AND gold >= @g",
                        ("@pid", posseId), ("@cid", (int)founderCharacterId), ("@g", (int)goldPaid));
                    int updated = Convert.ToInt32(GameDatabase.ExecuteScalar(connection, "SELECT changes()") ?? 0);
                    if (updated != 1)
                        throw new InvalidOperationException($"founder update rejected characterId={founderCharacterId}");

                    transaction.Commit();
                    Debug.LogError($"[POSSE-DB] Created posse '{name}' id={posseId} founder={founderCharacterId} gold={goldPaid}");
                    return new PosseRecord
                    {
                        Id = (uint)posseId,
                        Name = name,
                        FounderCharacterId = founderCharacterId,
                        GoldPaid = goldPaid,
                    };
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[POSSE-DB] operation=CreatePosse state=failed message='{ex.Message}'");
                return null;
            }
        }

        public static bool TryJoinPosse(uint characterId, uint posseId, int rank)
        {
            if (characterId == 0 || posseId == 0 || rank < 1 || rank > 10
                || CharacterRepository.HasPendingFullSnapshot(characterId))
                return false;
            try
            {
                using (var connection = GameDatabase.GetConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    int posseExists = Convert.ToInt32(GameDatabase.ExecuteScalar(connection,
                        "SELECT COUNT(*) FROM posses WHERE id = @pid", ("@pid", (int)posseId)) ?? 0);
                    if (posseExists != 1)
                        return false;
                    GameDatabase.ExecuteNonQuery(connection,
                        @"UPDATE characters
                          SET posse_id = @pid, posse_rank_id = @rank
                          WHERE id = @cid AND posse_id = 0",
                        ("@pid", (int)posseId), ("@rank", rank), ("@cid", (int)characterId));
                    int updated = Convert.ToInt32(GameDatabase.ExecuteScalar(connection, "SELECT changes()") ?? 0);
                    if (updated != 1)
                        return false;
                    transaction.Commit();
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[POSSE-DB] operation=JoinPosse state=failed message='{ex.Message}'");
                return false;
            }
        }

        public static bool TryTransferLeadership(uint posseId, uint oldLeaderId, uint newLeaderId)
        {
            if (posseId == 0 || oldLeaderId == 0 || newLeaderId == 0 || oldLeaderId == newLeaderId
                || CharacterRepository.HasPendingFullSnapshot(oldLeaderId)
                || CharacterRepository.HasPendingFullSnapshot(newLeaderId))
                return false;
            try
            {
                using (var connection = GameDatabase.GetConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    GameDatabase.ExecuteNonQuery(connection,
                        "UPDATE posses SET founder_character_id = @newLeader WHERE id = @posseId AND founder_character_id = @oldLeader",
                        ("@newLeader", (int)newLeaderId), ("@posseId", (int)posseId), ("@oldLeader", (int)oldLeaderId));
                    if (Convert.ToInt32(GameDatabase.ExecuteScalar(connection, "SELECT changes()") ?? 0) != 1)
                        return false;
                    GameDatabase.ExecuteNonQuery(connection,
                        "UPDATE characters SET posse_rank_id = 10 WHERE id = @characterId AND posse_id = @posseId",
                        ("@characterId", (int)newLeaderId), ("@posseId", (int)posseId));
                    if (Convert.ToInt32(GameDatabase.ExecuteScalar(connection, "SELECT changes()") ?? 0) != 1)
                        return false;
                    GameDatabase.ExecuteNonQuery(connection,
                        "UPDATE characters SET posse_rank_id = 9 WHERE id = @characterId AND posse_id = @posseId",
                        ("@characterId", (int)oldLeaderId), ("@posseId", (int)posseId));
                    if (Convert.ToInt32(GameDatabase.ExecuteScalar(connection, "SELECT changes()") ?? 0) != 1)
                        return false;
                    transaction.Commit();
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[POSSE-DB] operation=TransferLeadership state=failed message='{ex.Message}'");
                return false;
            }
        }

        public static bool TrySetCharacterRank(uint characterId, uint posseId, int rank)
        {
            if (characterId == 0 || posseId == 0 || rank < 1 || rank > 10
                || CharacterRepository.HasPendingFullSnapshot(characterId))
                return false;
            try
            {
                using (var connection = GameDatabase.GetConnection())
                {
                    GameDatabase.ExecuteNonQuery(connection,
                        "UPDATE characters SET posse_rank_id = @rank WHERE id = @characterId AND posse_id = @posseId",
                        ("@rank", rank), ("@characterId", (int)characterId), ("@posseId", (int)posseId));
                    return Convert.ToInt32(GameDatabase.ExecuteScalar(connection, "SELECT changes()") ?? 0) == 1;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[POSSE-DB] operation=SetCharacterRank state=failed message='{ex.Message}'");
                return false;
            }
        }

        public static PosseRecord GetPosse(uint posseId)
        {
            if (posseId == 0) return null;
            try
            {
                using (var connection = GameDatabase.GetConnection())
                using (var reader = GameDatabase.ExecuteReader(connection,
                    "SELECT id, name, founder_character_id, gold_paid, founded_at, COALESCE(motd,''), COALESCE(description,'') FROM posses WHERE id = @id",
                    ("@id", (int)posseId)))
                {
                    if (!reader.Read()) return null;
                    return new PosseRecord
                    {
                        Id = (uint)reader.GetInt32(0),
                        Name = reader.GetString(1),
                        FounderCharacterId = (uint)reader.GetInt32(2),
                        GoldPaid = (uint)reader.GetInt32(3),
                        FoundedAt = reader.IsDBNull(4) ? "" : reader.GetString(4),
                        Motd = reader.GetString(5),
                        Description = reader.GetString(6),
                    };
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[POSSE-DB] operation=GetPosse state=failed message='{ex.Message}'");
                return null;
            }
        }

        public static bool Rename(uint posseId, string newName)
        {
            if (posseId == 0 || string.IsNullOrEmpty(newName)) return false;
            try
            {
                using (var connection = GameDatabase.GetConnection())
                {
                    GameDatabase.ExecuteNonQuery(connection,
                        "UPDATE posses SET name = @n WHERE id = @id",
                        ("@id", (int)posseId), ("@n", newName));
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[POSSE-DB] operation=Rename state=failed message='{ex.Message}'");
                return false;
            }
        }

        public static bool SetMotd(uint posseId, string motd)
        {
            if (posseId == 0) return false;
            try
            {
                using (var connection = GameDatabase.GetConnection())
                {
                    GameDatabase.ExecuteNonQuery(connection,
                        "UPDATE posses SET motd = @m WHERE id = @id",
                        ("@id", (int)posseId), ("@m", motd ?? ""));
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[POSSE-DB] operation=SetMotd state=failed message='{ex.Message}'");
                return false;
            }
        }

        public static bool SetDescription(uint posseId, string desc)
        {
            if (posseId == 0) return false;
            try
            {
                using (var connection = GameDatabase.GetConnection())
                {
                    GameDatabase.ExecuteNonQuery(connection,
                        "UPDATE posses SET description = @d WHERE id = @id",
                        ("@id", (int)posseId), ("@d", desc ?? ""));
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[POSSE-DB] operation=SetDescription state=failed message='{ex.Message}'");
                return false;
            }
        }

        public static int MemberCount(uint posseId)
        {
            if (posseId == 0) return 0;
            try
            {
                using (var connection = GameDatabase.GetConnection())
                {
                    object memberCount = GameDatabase.ExecuteScalar(connection,
                        "SELECT COUNT(*) FROM characters WHERE posse_id = @pid", ("@pid", (int)posseId));
                    return Convert.ToInt32(memberCount);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[POSSE-DB] operation=MemberCount state=failed posseId={posseId} message='{ex.Message}'");
                return 0;
            }
        }

        public static System.Collections.Generic.List<string> MemberNames(uint posseId)
        {
            var result = new System.Collections.Generic.List<string>();
            if (posseId == 0) return result;
            try
            {
                using (var connection = GameDatabase.GetConnection())
                using (var reader = GameDatabase.ExecuteReader(connection,
                    "SELECT name FROM characters WHERE posse_id = @pid ORDER BY id",
                    ("@pid", (int)posseId)))
                {
                    while (reader.Read())
                        result.Add(reader.GetString(0));
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[POSSE-DB] operation=MemberNames state=failed message='{ex.Message}'");
            }
            return result;
        }

        public static bool ClearCharacterMembership(uint characterId, int cooldownExpiresUnix)
        {
            if (characterId == 0 || CharacterRepository.HasPendingFullSnapshot(characterId))
                return false;
            try
            {
                using (var connection = GameDatabase.GetConnection())
                {
                    GameDatabase.ExecuteNonQuery(connection,
                        "UPDATE characters SET posse_id = 0, posse_rank_id = 0, posse_join_cooldown = @cd WHERE id = @cid",
                        ("@cid", (int)characterId), ("@cd", cooldownExpiresUnix));
                    return Convert.ToInt32(GameDatabase.ExecuteScalar(connection, "SELECT changes()") ?? 0) == 1;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[POSSE-DB] operation=ClearCharacterMembership state=failed message='{ex.Message}'");
                return false;
            }
        }

        public static bool TryDisbandPosse(uint posseId, int cooldownExpiresUnix, out System.Collections.Generic.List<string> memberNames)
        {
            memberNames = new System.Collections.Generic.List<string>();
            if (posseId == 0) return false;
            try
            {
                using (var connection = GameDatabase.GetConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    using (var reader = GameDatabase.ExecuteReader(connection,
                        "SELECT id, name FROM characters WHERE posse_id = @pid ORDER BY id",
                        ("@pid", (int)posseId)))
                    {
                        while (reader.Read())
                        {
                            uint memberCharacterId = checked((uint)reader.GetInt32(0));
                            if (CharacterRepository.HasPendingFullSnapshot(memberCharacterId))
                            {
                                memberNames.Clear();
                                return false;
                            }
                            memberNames.Add(reader.GetString(1));
                        }
                    }
                    GameDatabase.ExecuteNonQuery(connection,
                        "UPDATE characters SET posse_id = 0, posse_rank_id = 0, posse_join_cooldown = @cd WHERE posse_id = @pid",
                        ("@pid", (int)posseId), ("@cd", cooldownExpiresUnix));
                    GameDatabase.ExecuteNonQuery(connection,
                        "DELETE FROM posses WHERE id = @pid", ("@pid", (int)posseId));
                    if (Convert.ToInt32(GameDatabase.ExecuteScalar(connection, "SELECT changes()") ?? 0) != 1)
                        throw new InvalidOperationException($"posse delete rejected posseId={posseId}");
                    transaction.Commit();
                    Debug.LogError($"[POSSE-DB] Disbanded posse id={posseId} (members affected={memberNames.Count})");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[POSSE-DB] operation=DisbandPosse state=failed message='{ex.Message}'");
                return false;
            }
        }
    }
}
