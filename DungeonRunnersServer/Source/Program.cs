using System;
using System.IO;
using System.Reflection;
using System.Threading;
using DungeonRunners.Combat;
using DungeonRunners.Engine;
using DungeonRunners.Runtime;
using DungeonRunners.Core;

namespace DungeonRunners
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            bool timerPeriodRequested = false;
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    timeBeginPeriod(1);
                    timerPeriodRequested = true;
                }

                string dataPath = ResolveDataPath();
                string persistentPath = ResolvePersistentPath(dataPath);
                EngineRuntime.DataPath = dataPath;
                EngineRuntime.PersistentDataPath = persistentPath;

                if (!RuntimeEvidence.TryAcquireStartupOwnership())
                    return 2;

                string logPath = Path.Combine(persistentPath, "logs", "server.log");
                ServerLog.Init(logPath);
                RuntimeEvidence.SetFocusedLogFilter(!ServerDiagnostics.Enabled || ServerSettings.GetCfgBool("focusedDebugLog", true));

                Console.OutputEncoding = System.Text.Encoding.UTF8;

                ServerConfig config = BuildConfig();
                StartupLog.Begin(
                    config.serverVersion,
                    System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    persistentPath,
                    logPath);

                var rootGo = new GameObject("ServerHost");
                var serverHost = rootGo.AddComponent<ServerHost>();
                InjectPrivateField(serverHost, "serverConfig", config);
                Talkback.TalkbackServer.Instance.Start();

                bool quitRequested = false;
                Console.CancelKeyPress += (sender, eventArgs) => { eventArgs.Cancel = true; quitRequested = true; StartupLog.Shutdown("Ctrl+C"); };

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                double lastElapsedSeconds = 0.0;
                double fixedAccumulator = 0.0;
                const double targetFrameSeconds = 1.0 / SimulationClock.TicksPerSecond;

                while (!quitRequested && !EngineRuntime.QuitRequested)
                {
                    double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
                    double deltaSeconds = elapsedSeconds - lastElapsedSeconds;
                    lastElapsedSeconds = elapsedSeconds;
                    if (deltaSeconds > 0.25) deltaSeconds = 0.25;

                    EngineRuntime.Tick((float)deltaSeconds, (float)elapsedSeconds);

                    fixedAccumulator += deltaSeconds;
                    int fixedTickGuard = 0;
                    while (fixedAccumulator >= EngineRuntime.FixedDeltaTime && fixedTickGuard++ < 8)
                    {
                        EngineRuntime.FixedTick();
                        fixedAccumulator -= EngineRuntime.FixedDeltaTime;
                    }

                    double frameSpentSeconds = stopwatch.Elapsed.TotalSeconds - elapsedSeconds;
                    double sleepSeconds = targetFrameSeconds - frameSpentSeconds;
                    if (sleepSeconds > 0.0) Thread.Sleep((int)(sleepSeconds * 1000.0));
                }

                StartupLog.Shutdown("server loop ended");
            }
            catch (Exception ex)
            {
                EngineRuntime.SetExitCode(Math.Max(1, EngineRuntime.ExitCode));
                try { Debug.LogException(ex); }
                catch { try { Console.Error.WriteLine(ex); } catch { } }
                try { StartupLog.Shutdown("fatal exception"); } catch { }
            }
            finally
            {
                try { EngineRuntime.Shutdown(); }
                catch (Exception ex)
                {
                    EngineRuntime.SetExitCode(Math.Max(1, EngineRuntime.ExitCode));
                    try { Debug.LogException(ex); } catch { }
                }
                try { ServerLog.Shutdown(); } catch { }
                RuntimeEvidence.Stop();
                if (timerPeriodRequested)
                {
                    try { timeEndPeriod(1); } catch { }
                }
            }
            return EngineRuntime.ExitCode;
        }

        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint uPeriod);

        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint uPeriod);

        private static ServerConfig BuildConfig()
        {
            var config = ScriptableObject.CreateInstance<ServerConfig>();
            config.name = "ServerConfig";
            config.authServerIP = "0.0.0.0";
            config.authServerPort = 2110;
            config.gameServerIP = "127.0.0.1";
            config.gameServerPort = 2603;
            config.gameServerName = "Dungeon Runners Server";
            config.blowfishKey = "[;'.]94-31==-%&@!^+]";
            config.desKey = "TEST";
            config.maxPlayers = 100;
            config.serverVersion = "1.0.0";
            config.enableDebugLogging = ServerDiagnostics.Enabled;
            config.defaultWorldId = 1;
            return config;
        }

        private static void InjectPrivateField(object target, string fieldName, object value)
        {
            var fieldInfo = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            if (fieldInfo != null) fieldInfo.SetValue(target, value);
            else Debug.LogError($"[BOOT] field '{fieldName}' not found on {target.GetType().Name}");
        }

        private static string ResolveDataPath()
        {
            string serverRoot = Path.GetFullPath(AppContext.BaseDirectory);
            string databaseRoot = Path.Combine(serverRoot, "Database");
            if (!Directory.Exists(databaseRoot))
                throw new DirectoryNotFoundException(databaseRoot);
            return serverRoot;
        }

        private static string ResolvePersistentPath(string dataPath)
        {
            string persistentRoot = Path.GetFullPath(dataPath);
            if (!Directory.Exists(Path.Combine(persistentRoot, "Database")))
                throw new DirectoryNotFoundException("DungeonRunnersServer/Database");
            return persistentRoot;
        }
    }
}
