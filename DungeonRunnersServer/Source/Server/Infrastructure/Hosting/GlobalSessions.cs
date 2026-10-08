using System.Collections.Generic;
using DungeonRunners.Engine;

namespace DungeonRunners.Core
{
    public static class GlobalSessions
    {
        private static readonly Dictionary<uint, string> _sessions = new Dictionary<uint, string>();
        private static readonly object _lock = new object();

        public static void Store(uint token, string username)
        {
            lock (_lock)
            {
                _sessions[token] = username;
                Debug.Log($"[GLOBAL-SESSIONS] action=store token=0x{token:X8} user='{username}'");
            }
        }

        public static bool TryConsume(uint token, out string username)
        {
            lock (_lock)
            {
                if (_sessions.TryGetValue(token, out username))
                {
                    _sessions.Remove(token);
                    Debug.Log($"[GLOBAL-SESSIONS] action=consume token=0x{token:X8} user='{username}'");
                    return true;
                }
                
                Debug.LogWarning($"[GLOBAL-SESSIONS] action=consume token=0x{token:X8} state=missing");
                username = null;
                return false;
            }
        }

        public static bool Exists(uint token)
        {
            lock (_lock)
            {
                return _sessions.ContainsKey(token);
            }
        }

        public static void Clear()
        {
            lock (_lock)
            {
                int count = _sessions.Count;
                _sessions.Clear();
                Debug.Log($"[GLOBAL-SESSIONS] action=clear count={count}");
            }
        }

        public static int Count
        {
            get
            {
                lock (_lock)
                {
                    return _sessions.Count;
                }
            }
        }
    }
}
