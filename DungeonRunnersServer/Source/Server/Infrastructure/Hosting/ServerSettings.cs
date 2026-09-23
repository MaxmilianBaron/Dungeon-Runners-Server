using System;
using System.Collections.Generic;
using System.IO;
using DungeonRunners.Engine;
using DungeonRunners.Networking;

namespace DungeonRunners.Core
{
    public static class ServerSettings
    {
        private static volatile Dictionary<string, string> _cfgValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, string> _dbValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static volatile bool _cfgLoaded = false;
        private static bool _loaded = false;
        private static string _cfgPath;
        private static readonly HashSet<string> _runtimeMutableKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "authIP",
            "authPort",
            "gameIP",
            "gamePort",
            "queuePort",
            "serverName",
            "maxPlayers",
            "startZone",
            "welcomeMessage",
            "welcomeColor",
            "motd",
            "motdColor",
            "announceColor",
            "announceEffect"
        };

        public static bool IsRuntimeMutableKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;
            return _runtimeMutableKeys.Contains(key);
        }

        public static void Load()
        {
            _loaded = true;
            _dbValues.Clear();
            LoadCfgSettings("Loaded");
            LoadFromDatabase();
            QueueConnection.MaxPlayers = Get("maxPlayers", 50);
            LogAllSettings();
        }

        public static void Reload()
        {
            LoadCfgSettings("Reloaded");
            LoadFromDatabase();
            QueueConnection.MaxPlayers = Get("maxPlayers", 50);
            Debug.LogError($"[CONFIG] Reloaded. MaxPlayers={QueueConnection.MaxPlayers}");
        }


        private static void EnsureLoaded()
        {
            if (!_loaded) Load();
        }

        private static void EnsureCfgLoaded()
        {
            if (!_cfgLoaded)
                LoadCfgSettings(null);
        }

        public static string GetString(string key, string defaultValue = "")
        {
            EnsureLoaded();
            if (_dbValues.TryGetValue(key, out string dbVal)) return dbVal;
            if (_cfgValues.TryGetValue(key, out string cfgVal)) return cfgVal;
            return defaultValue;
        }

        public static int Get(string key, int defaultValue)
        {
            string settingValue = GetString(key, null);
            if (settingValue != null && int.TryParse(settingValue, out int result)) return result;
            return defaultValue;
        }

        public static float GetFloat(string key, float defaultValue)
        {
            string settingValue = GetString(key, null);
            if (settingValue != null && float.TryParse(settingValue,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float result))
                return result;
            return defaultValue;
        }

        public static bool GetBool(string key, bool defaultValue)
        {
            string settingValue = GetString(key, null);
            if (settingValue == null) return defaultValue;
            settingValue = settingValue.ToLower().Trim();
            if (settingValue == "true" || settingValue == "1" || settingValue == "yes") return true;
            if (settingValue == "false" || settingValue == "0" || settingValue == "no") return false;
            return defaultValue;
        }

        public static string GetCfgString(string key, string defaultValue = "")
        {
            EnsureCfgLoaded();
            return _cfgValues.TryGetValue(key, out string value) ? value : defaultValue;
        }

        public static bool GetCfgBool(string key, bool defaultValue)
        {
            string settingValue = GetCfgString(key, null);
            if (settingValue == null)
                return defaultValue;
            settingValue = settingValue.Trim();
            if (settingValue == "1")
                return true;
            if (settingValue == "0")
                return false;
            throw new InvalidDataException($"Configuration switch '{key}' must be 0 or 1");
        }


        public static bool Set(string key, string value)
        {
            if (!IsRuntimeMutableKey(key))
            {
                Debug.LogError($"[CONFIG] Blocked client-authoritative DB override '{key}'");
                return false;
            }
            _dbValues[key] = value;
            SaveToDatabase(key, value);
            Debug.LogError($"[CONFIG] Set '{key}' = '{value}' (saved to DB)");

            if (key.Equals("maxPlayers", StringComparison.OrdinalIgnoreCase))
                QueueConnection.MaxPlayers = Get("maxPlayers", 50);
            return true;
        }


        public static void Remove(string key)
        {
            _dbValues.Remove(key);
            RemoveFromDatabase(key);
            Debug.LogError($"[CONFIG] Removed DB override for '{key}'");

            if (key.Equals("maxPlayers", StringComparison.OrdinalIgnoreCase))
                QueueConnection.MaxPlayers = Get("maxPlayers", 50);
        }


        public static Dictionary<string, (string value, string source)> GetAll()
        {
            EnsureLoaded();
            var result = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
            foreach (var configEntry in _cfgValues)
                result[configEntry.Key] = (configEntry.Value, "cfg");
            foreach (var databaseEntry in _dbValues)
                result[databaseEntry.Key] = (databaseEntry.Value, "db");
            return result;
        }


        private static void LoadCfgFile(string path)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string[] lines = File.ReadAllLines(path);
                foreach (string rawLine in lines)
                {
                    string line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line)) continue;
                    if (line.StartsWith("#")) continue;

                    int eqIdx = line.IndexOf('=');
                    if (eqIdx <= 0) continue;

                    string key = line.Substring(0, eqIdx).Trim();
                    string value = line.Substring(eqIdx + 1).Trim();

                    for (int ci = 1; ci < value.Length; ci++)
                    {
                        if (value[ci] == '#' && char.IsWhiteSpace(value[ci - 1]))
                        {
                            value = value.Substring(0, ci).Trim();
                            break;
                        }
                    }

                    if (!string.IsNullOrEmpty(key))
                        values[key] = value;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CONFIG] read path='{path}' state=failed message='{ex.Message}'");
            }
            ValidateDiagnosticSwitches(values);
            _cfgValues = values;
        }

        private static void ValidateDiagnosticSwitches(Dictionary<string, string> values)
        {
            foreach (string key in ServerDiagnostics.SwitchKeys)
            {
                if (!values.TryGetValue(key, out string value))
                    continue;
                value = value.Trim();
                if (value != "0" && value != "1")
                    throw new InvalidDataException($"Configuration switch '{key}' must be 0 or 1");
            }
        }

        private static string FindCfgPath()
        {
            string path = DataPaths.ServerPath("server.cfg");
            return File.Exists(path) ? path : null;
        }

        private static void LoadCfgSettings(string action)
        {
            _cfgPath = FindCfgPath();
            if (_cfgPath != null)
            {
                LoadCfgFile(_cfgPath);
                if (!string.IsNullOrWhiteSpace(action))
                    Debug.LogError($"[CONFIG] {action} {_cfgValues.Count} settings from {_cfgPath}");
            }
            else if (!string.IsNullOrWhiteSpace(action))
            {
                _cfgValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                Debug.LogError("[CONFIG] server.cfg not found - using defaults");
            }
            else
            {
                _cfgValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            _cfgLoaded = true;
        }


        private static void LoadFromDatabase()
        {
            try
            {
                using (var connection = Database.GameDatabase.GetConnection())
                {
                    Database.GameDatabase.ExecuteNonQuery(connection,
                        @"CREATE TABLE IF NOT EXISTS server_settings (
                            key TEXT PRIMARY KEY NOT NULL,
                            value TEXT NOT NULL,
                            updated_at TEXT DEFAULT CURRENT_TIMESTAMP
                        )");

                    using (var reader = Database.GameDatabase.ExecuteReader(connection,
                        "SELECT key, value FROM server_settings ORDER BY key"))
                    {
                        int count = 0;
                        int skipped = 0;
                        while (reader.Read())
                        {
                            string key = reader.GetString(0);
                            string value = reader.GetString(1);
                            if (!IsRuntimeMutableKey(key))
                            {
                                skipped++;
                                continue;
                            }
                            _dbValues[key] = value;
                            count++;
                        }
                        if (count > 0)
                            Debug.LogError($"[CONFIG] Loaded {count} DB overrides");
                        if (skipped > 0)
                            Debug.LogError($"[CONFIG] Ignored {skipped} client-authoritative DB overrides");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CONFIG] DB load error (non-fatal): {ex.Message}");
            }
        }

        private static void SaveToDatabase(string key, string value)
        {
            try
            {
                using (var connection = Database.GameDatabase.GetConnection())
                {
                    Database.GameDatabase.ExecuteNonQuery(connection,
                        "INSERT OR REPLACE INTO server_settings (key, value, updated_at) VALUES (@key, @value, datetime('now'))",
                        ("@key", key), ("@value", value));
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CONFIG] dbSave state=failed message='{ex.Message}'");
            }
        }

        private static void RemoveFromDatabase(string key)
        {
            try
            {
                using (var connection = Database.GameDatabase.GetConnection())
                {
                    Database.GameDatabase.ExecuteNonQuery(connection,
                        "DELETE FROM server_settings WHERE key = @key",
                        ("@key", key));
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CONFIG] dbRemove state=failed message='{ex.Message}'");
            }
        }

        private static void LogAllSettings()
        {
            Debug.LogError("[CONFIG] settings");
            Debug.LogError("[CONFIG] scope=server-settings");
            Debug.LogError($"[CONFIG] serverName={GetString("serverName", "Dungeon Runners")}");
            Debug.LogError($"[CONFIG] maxPlayers={Get("maxPlayers", 50)}");
            Debug.LogError($"[CONFIG] startZone={GetString("startZone", "tutorial")}");
            Debug.LogError($"[CONFIG] welcomeMessage={GetString("welcomeMessage", "Welcome!")}");
            int dbCount = _dbValues.Count;
            if (dbCount > 0)
            {
                Debug.LogError($"[CONFIG] dbOverrides={dbCount}");
                foreach (var databaseEntry in _dbValues)
                    Debug.LogError($"[CONFIG] dbOverride {databaseEntry.Key}={databaseEntry.Value}");
            }
            Debug.LogError("[CONFIG] settings");
        }
    }
}
