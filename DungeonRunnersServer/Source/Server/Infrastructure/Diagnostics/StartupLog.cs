using System;
using DungeonRunners.Engine;
using DungeonRunners.Runtime;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace DungeonRunners.Core
{
    public static class StartupLog
    {
        private static readonly Stopwatch Timer = new Stopwatch();
        private const string Rule = "------------------------------------------------------------";

        public static void Begin(string version, string runtime, string rootPath, string logPath)
        {
            Timer.Restart();
            Debug.Log($"[BOOT] {Rule}");
            Debug.Log("[BOOT] Dungeon Runners Server");
            Debug.Log($"[BOOT] Version      {version}");
            Debug.Log($"[BOOT] Runtime      {runtime}");
            Debug.Log($"[BOOT] Root         {rootPath}");
            Debug.Log($"[BOOT] Log          {logPath}");
            Debug.Log($"[BOOT] {Rule}");
        }

        public static void Ready(string label, string details)
        {
            Debug.Log($"[BOOT] {label,-12} OK  {details}");
        }

        public static void Service(string name, string endpoint)
        {
            Debug.Log($"[BOOT] Service      OK  {name,-14} {endpoint}");
        }

        public static void Warning(string label, string details)
        {
            Debug.LogWarning($"[BOOT] {label,-12} WARN  {details}");
        }

        public static void Failure(string label, string details)
        {
            Debug.LogError($"[BOOT] {label,-12} FAIL  {details}");
        }

        public static void Complete(int worldId, int maxPlayers)
        {
            Debug.Log($"[BOOT] {Rule}");
            Debug.Log($"[BOOT] READY        World {worldId} | capacity={maxPlayers} | startup={Timer.Elapsed.TotalSeconds:F2}s");
            Debug.Log($"[BOOT] {Rule}");
            ServerLog.CompleteStartup();
        }

        public static void Shutdown(string reason)
        {
            Debug.Log($"[BOOT] SHUTDOWN     {reason}");
        }
    }
}
