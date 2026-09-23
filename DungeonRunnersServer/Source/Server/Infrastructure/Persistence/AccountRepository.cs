using System;
using DungeonRunners.Engine;

namespace DungeonRunners.Database
{
    public static class AccountRepository
    {
        public static uint CreateLocalAccount(string username)
        {
            Debug.LogError($"[DB-AUTH] CreateLocalAccount called: username='{username}'");

            if (string.IsNullOrWhiteSpace(username))
            {
                Debug.LogError("[DB-AUTH] REJECTED: username is empty");
                return 0;
            }

            try
            {
                Debug.LogError($"[DB-AUTH] DB Path: {GameDatabase.DbPath}");
                using (var connection = GameDatabase.GetConnection())
                {
                    Debug.LogError($"[DB-AUTH] Got connection, inserting account...");
                    GameDatabase.ExecuteNonQuery(connection,
                        "INSERT INTO accounts (username, password_hash, salt, is_member) VALUES (@u, '', '', 1)",
                        ("@u", username));

                    Debug.LogError($"[DB-AUTH] INSERT done, getting ID...");
                    object accountIdValue = GameDatabase.ExecuteScalar(connection, "SELECT last_insert_rowid()");
                    uint accountId = Convert.ToUInt32(accountIdValue);
                    Debug.LogError($"[DB-AUTH] Created account '{username}' (ID: {accountId})");
                    return accountId;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DB-AUTH] CreateLocalAccount FAILED: {ex.GetType().Name}: {ex.Message}");
                Debug.LogError($"[DB-AUTH] Stack: {ex.StackTrace}");

                if (ex.Message.Contains("UNIQUE") || ex.Message.Contains("unique"))
                {
                    Debug.LogError($"[DB-AUTH] Username '{username}' already exists");
                    return GetAccountId(username);
                }

                return 0;
            }
        }

        public static bool UsernameExists(string username)
        {
            try
            {
                using (var connection = GameDatabase.GetConnection())
                {
                    object result = GameDatabase.ExecuteScalar(connection,
                        "SELECT COUNT(*) FROM accounts WHERE username = @u",
                        ("@u", username));
                    return Convert.ToInt32(result) > 0;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DB-AUTH] operation=UsernameExists state=failed username='{username ?? string.Empty}' message='{ex.Message}'");
                return false;
            }
        }

        public static uint GetAccountId(string username)
        {
            try
            {
                using (var connection = GameDatabase.GetConnection())
                {
                    object result = GameDatabase.ExecuteScalar(connection,
                        "SELECT id FROM accounts WHERE username = @u",
                        ("@u", username));
                    return result != null ? Convert.ToUInt32(result) : 0;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DB-AUTH] operation=GetAccountId state=failed username='{username ?? string.Empty}' message='{ex.Message}'");
                return 0;
            }
        }

        public static void SetBanned(uint accountId, bool banned)
        {
            try
            {
                using (var connection = GameDatabase.GetConnection())
                {
                    GameDatabase.ExecuteNonQuery(connection,
                        "UPDATE accounts SET is_banned = @b WHERE id = @id",
                        ("@b", banned ? 1 : 0), ("@id", (int)accountId));
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DB-AUTH] operation=Ban state=failed message='{ex.Message}'");
            }
        }
    }
}
