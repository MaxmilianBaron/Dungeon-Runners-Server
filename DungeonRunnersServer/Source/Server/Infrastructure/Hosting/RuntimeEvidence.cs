using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using DungeonRunners.Engine;

namespace DungeonRunners.Core
{
    public static class RuntimeEvidence
    {
        private static readonly object LogLock = new object();
        private static bool _started;
        private static bool _artifactsReset;
        private static bool _logBindingStarted;
        private static bool _focusedFilterInstalled;
        private static volatile bool _focusedModeEnabled;
        private static ILogHandler _originalLogHandler;
        private static Mutex _singleInstanceMutex;
        private static bool _ownsSingleInstanceMutex;
        private static bool _shouldAbortStartup;
        private static readonly Dictionary<string, int> FallbackHitCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public static bool ShouldAbortStartup => _shouldAbortStartup;
        public static bool TrackingEnabled => ServerDiagnostics.IsEnabled("runtimeEvidenceTracking");

        public static void EnsureStarted()
        {
            if (_started) return;
            _started = true;

            if (!TryAcquireStartupOwnership())
            {
                Application.quitting += Stop;
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
                return;
            }

            Application.quitting += Stop;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            ApplyConfiguredDiagnostics();
        }

        public static bool TryAcquireStartupOwnership()
        {
            if (_ownsSingleInstanceMutex)
                return true;
            if (_shouldAbortStartup)
                return false;
            if (!TryAcquireSingleInstanceMutex())
            {
                _shouldAbortStartup = true;
                IsOtherRuntimeProcessActive(out int activePid);
                WriteDuplicateLaunchNotice("mutex", activePid);
                return false;
            }

            if (IsOtherRuntimeProcessActive(out int otherRuntimePid))
            {
                _shouldAbortStartup = true;
                WriteDuplicateLaunchNotice("process", otherRuntimePid);
                ReleaseSingleInstanceMutex();
                return false;
            }

            return true;
        }

        public static void ApplyConfiguredDiagnostics()
        {
            bool masterEnabled = ServerDiagnostics.Enabled;
            bool focusedEnabled = ServerSettings.GetCfgBool("focusedDebugLog", true);
            SetFocusedLogFilter(!masterEnabled || focusedEnabled);

            if (!_started || _shouldAbortStartup)
                return;

            bool runtimeEvidenceEnabled = TrackingEnabled;
            bool parityEnabled = ServerDiagnostics.IsEnabled("simulationParityTracking");

            if (!parityEnabled)
                SimulationParityLedger.Stop();

            if (!masterEnabled)
                return;

            if ((runtimeEvidenceEnabled || parityEnabled) && !_artifactsReset)
            {
                ResetRunArtifacts();
                _artifactsReset = true;
            }

            if (parityEnabled)
                SimulationParityLedger.EnsureStarted();

            if (runtimeEvidenceEnabled && !_logBindingStarted)
            {
                _logBindingStarted = true;
                StartLogMirror();
            }
        }

        public static void Stop()
        {
            lock (LogLock)
            {
                if (_focusedFilterInstalled)
                {
                    DungeonRunners.Engine.Debug.logger.logHandler = _originalLogHandler;
                    _originalLogHandler = null;
                    _focusedFilterInstalled = false;
                }

                ReleaseSingleInstanceMutex();
            }
            SimulationParityLedger.Stop();
        }

        private static void OnProcessExit(object sender, EventArgs e)
        {
            Stop();
        }

        public static void SetFocusedLogFilter(bool enabled)
        {
            lock (LogLock)
            {
                _focusedModeEnabled = enabled;
                if (_focusedFilterInstalled)
                    return;
                _originalLogHandler = DungeonRunners.Engine.Debug.logger.logHandler;
                DungeonRunners.Engine.Debug.logger.logHandler = new FocusedLogHandler(_originalLogHandler);
                _focusedFilterInstalled = true;
            }
        }

        private static void StartLogMirror()
        {
            try
            {
                int pid = Process.GetCurrentProcess().Id;
                DungeonRunners.Engine.Debug.LogError("[RUNTIME-EVIDENCE] pid=" + pid + " log=" + ResolveServerLogPath());
                LogBuildBinding("startup");
            }
            catch (Exception ex)
            {
                DungeonRunners.Engine.Debug.LogError("[RUNTIME-EVIDENCE] Server log binding failed: " + ex.Message);
            }
        }

        public static void LogBuildBinding(string source)
        {
            if (!TrackingEnabled)
                return;
            string marker = string.IsNullOrWhiteSpace(source) ? "[BUILD-BINDING] " : "[BUILD-BINDING] source=" + source + " ";
            DungeonRunners.Engine.Debug.LogError(marker + ResolveBuildBinding());
        }

        public static int LogFallbackHit(string area, string key, string detail = null, int throttleEvery = 64)
        {
            if (!ServerDiagnostics.IsEnabled("fallbackHitTracking"))
                return 0;
            area = NormalizeLogToken(area, "unknown");
            key = NormalizeLogToken(key, "unknown");
            string counterKey = area + "|" + key;
            int count;
            lock (FallbackHitCounts)
            {
                FallbackHitCounts.TryGetValue(counterKey, out count);
                count++;
                FallbackHitCounts[counterKey] = count;
            }

            bool shouldLog = count <= 3 || (throttleEvery > 0 && count % throttleEvery == 0);
            if (shouldLog)
            {
                string suffix = string.IsNullOrWhiteSpace(detail) ? "" : " " + detail.Trim();
                DungeonRunners.Engine.Debug.LogError($"[FALLBACK-HIT] area={area} key={key} count={count}{suffix}");
            }
            return count;
        }

        public static void LogForPlayer(string player, string marker, string body)
        {
            if (!TrackingEnabled)
                return;
            string token = NormalizeLogToken(player, "unknown");
            DungeonRunners.Engine.Debug.LogError($"{marker} player={token} {body}");
        }

        public static void LogForPlayerPair(string src, string dst, string marker, string body)
        {
            if (!TrackingEnabled)
                return;
            string srcToken = NormalizeLogToken(src, "unknown");
            string dstToken = NormalizeLogToken(dst, "unknown");
            string line = $"{marker} src={srcToken} dst={dstToken} {body}";
            DungeonRunners.Engine.Debug.LogError(line);
        }

        private static string NormalizeLogToken(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
                return fallback;
            var sb = new StringBuilder(value.Length);
            foreach (char ch in value.Trim())
            {
                if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.' || ch == ':')
                    sb.Append(ch);
                else
                    sb.Append('_');
            }
            return sb.Length > 0 ? sb.ToString() : fallback;
        }

        private static bool TryAcquireSingleInstanceMutex()
        {
            try
            {
                _singleInstanceMutex = new Mutex(false, "DungeonRunnersServerEngineRuntime");
                _ownsSingleInstanceMutex = _singleInstanceMutex.WaitOne(0, false);
                if (!_ownsSingleInstanceMutex)
                {
                    _singleInstanceMutex.Dispose();
                    _singleInstanceMutex = null;
                    return false;
                }
                return true;
            }
            catch (AbandonedMutexException)
            {
                _ownsSingleInstanceMutex = true;
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[BOOT] Startup ownership validation failed: " + ex.Message);
                return false;
            }
        }

        private static void ReleaseSingleInstanceMutex()
        {
            if (_singleInstanceMutex == null)
                return;
            try
            {
                if (_ownsSingleInstanceMutex)
                    _singleInstanceMutex.ReleaseMutex();
            }
            catch
            {
            }
            try
            {
                _singleInstanceMutex.Dispose();
            }
            catch
            {
            }
            _singleInstanceMutex = null;
            _ownsSingleInstanceMutex = false;
        }

        private static string ServerLocalTimestamp()
        {
            return DateTimeOffset.Now.ToString("HH:mm:ss");
        }

        private static void WriteDuplicateLaunchNotice(string reason, int otherPid)
        {
            string owner = otherPid > 0 ? " PID " + otherPid : "";
            Console.Error.WriteLine(ServerLocalTimestamp() + " [BOOT] Server already running" + owner + "; duplicate launch stopped (" + reason + ").");
        }

        private static bool IsOtherRuntimeProcessActive(out int otherPid)
        {
            otherPid = 0;
            try
            {
                using (var current = Process.GetCurrentProcess())
                {
                    string currentPath = "";
                    try
                    {
                        currentPath = current.MainModule?.FileName ?? "";
                    }
                    catch
                    {
                    }

                    string processName = !string.IsNullOrWhiteSpace(currentPath)
                        ? Path.GetFileNameWithoutExtension(currentPath)
                        : current.ProcessName;

                    foreach (var proc in Process.GetProcessesByName(processName))
                    {
                        try
                        {
                            if (proc.Id == current.Id)
                                continue;

                            string otherPath = "";
                            try
                            {
                                otherPath = proc.MainModule?.FileName ?? "";
                            }
                            catch
                            {
                            }

                            if (string.IsNullOrWhiteSpace(currentPath) ||
                                string.Equals(otherPath, currentPath, StringComparison.OrdinalIgnoreCase))
                            {
                                otherPid = proc.Id;
                                return true;
                            }
                        }
                        finally
                        {
                            proc.Dispose();
                        }
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static string ResolveBuildBinding()
        {
            try
            {
                string buildDir = ResolveBuildDir();
                string buildInfoPath = Path.Combine(buildDir, "build_info.txt");
                if (TryResolveExecutableBuildBinding(out string executableBinding))
                    return executableBinding;
                if (File.Exists(buildInfoPath))
                    return File.ReadAllText(buildInfoPath).Replace("\r", " ").Replace("\n", " ").Trim();
                return "Runtime=dotnet BuildInfoMissing=" + buildInfoPath;
            }
            catch (Exception ex)
            {
                return "Runtime=dotnet BuildInfoError=" + ex.Message;
            }
        }

        private static void ResetRunArtifacts()
        {
            try
            {
                string logDirectory = Path.GetDirectoryName(ResolveServerLogPath());
                if (string.IsNullOrWhiteSpace(logDirectory))
                    return;
                Directory.CreateDirectory(logDirectory);
                DeleteFiles(logDirectory, "player_*.log");
                DeleteFiles(logDirectory, "simulation-parity-server-*.jsonl");
            }
            catch (Exception ex)
            {
                DungeonRunners.Engine.Debug.LogError("[RUNTIME-EVIDENCE] resetFailed message=" + ex.Message);
            }
        }

        private static void DeleteFiles(string directory, string pattern)
        {
            foreach (string path in Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly))
                File.Delete(path);
        }

        private static bool TryResolveExecutableBuildBinding(out string binding)
        {
            binding = "";
            try
            {
                using (Process current = Process.GetCurrentProcess())
                {
                    string executablePath = current.MainModule?.FileName ?? "";
                    if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
                        return false;
                    executablePath = DataPaths.RequireServerPath(executablePath);
                    string hash;
                    using (var algorithm = System.Security.Cryptography.SHA256.Create())
                    using (var stream = File.OpenRead(executablePath))
                        hash = BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", "");
                    string builtUtc = File.GetLastWriteTimeUtc(executablePath).ToString("O");
                    binding = "Runtime=dotnet BuildTag=sha256:" + hash + " Executable=" + executablePath + " BuiltUtc=" + builtUtc;
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static string ResolveBuildDir()
        {
            return DataPaths.ServerRoot;
        }

        private static bool ShouldMirrorLog(string condition, LogType type)
        {
            if (type == LogType.Exception || type == LogType.Assert)
                return true;

            if (string.IsNullOrWhiteSpace(condition))
                return false;

            string line = condition.TrimStart();
            if (IsImportantLog(line))
                return true;

            if (!ServerDiagnostics.Enabled)
                return false;

            if (ServerDiagnostics.IsEnabled("verboseEvidenceLogging"))
                return true;

            if (IsExplicitlyEnabledLog(line))
                return true;

            if (IsTrackerLog(line))
                return ServerDiagnostics.IsEnabled("trackerLogging");

            if (!_focusedModeEnabled)
                return true;

            if (IsFocusedLog(line))
                return true;

            string[] noisyPrefixes =
            {
                "[UDP]",
                "[NEW-PKT]",
                "[OP",
                "[SIZE-CHECK]",
                "[CUMULATIVE-",
                "[PLAYER-SPAWN-HEX]",
                "[MONSTER-SPAWN-HEX]",
                "[DETAIL-0x00]",
                "[PLAYER-STATE]",
                "[PACKET-",
                "[NPC-",
                "[PORTAL",
                "[SEND-ZONE-NPCS]",
                "[SEND-ZONE-PORTALS]",
                "[SEND-ZONE-CHECKPOINTS]",
                "[CHECKPOINT]",
                "[QUEST-",
                "[GNOME-",
                "[GC-OBJECT]",
                "[CHARLIST",
                "[ACTION-READ]",
                "[COMPONENT]",
                "[COMPONENT-ROUTE]",
                "[UNIT-CONTAINER]",
                "[GROUP]",
                "[ROOM-RNG]",
                "[SPAWN",
                "[DROP-LOAD]",
                "[PASSIVE-",
                "[CLASS-PASSIVE]",
                "[KNOBS]",
                "[MEMBER]",
                "[COMBAT] registerPlayer",
                "[ZONE-SPAWNER]",
                "[FOLLOW",
                "[MOTD]",
                "[WELCOME]",
                "[MERCHANT-WRITE-ITEM]",
                "[EQUIP-WRITEINIT]",
                "[UDP-RAW]",
                "[UDP-FOLLOW]",
                "[UDP-SKILLS]",
                "[ENTITY-STREAM]",
                "[ENTITY-CH]",
                "[TICK] Starting",
                "[TICK] Using",
                "0000",
                "[OP",
                "[PACKET"
            };

            foreach (string prefix in noisyPrefixes)
            {
                if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return false;
        }

        private static bool IsImportantLog(string line)
        {
            return line.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("CRITICAL", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("FATAL", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf(" failed", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf(" failure", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf(" error", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf(" invalid", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf(" corrupt", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf(" denied", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf(" unavailable", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf(" not found", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf(" missing", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf(" cannot", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf(" unable", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf(" timeout", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("=failed", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("-ERROR]", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("-FAILURE]", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("-REJECT]", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("Invalid ComponentID", StringComparison.OrdinalIgnoreCase) >= 0
                || line.StartsWith("[RUNTIME-EVIDENCE]", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("[BUILD-BINDING]", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("[FALLBACK-HIT]", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("[AUTHORED-COVERAGE]", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("[DR-LOG]", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("[BOOT]", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("[SERVER]", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("[CONFIG]", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("[SERVER]", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsExplicitlyEnabledLog(string line)
        {
            return ServerDiagnostics.IsEnabled("wirePacketTracking")
                    && line.StartsWith("[MC]", StringComparison.OrdinalIgnoreCase)
                || ServerDiagnostics.IsEnabled("simulationParityTracking")
                    && line.StartsWith("[SIM-PARITY]", StringComparison.OrdinalIgnoreCase)
                || ServerDiagnostics.IsEnabled("desyncForensicsTracking")
                    && line.StartsWith("[DESYNC-FORENSICS]", StringComparison.OrdinalIgnoreCase)
                || ServerDiagnostics.IsEnabled("databaseTracking")
                    && line.StartsWith("[DB-TRACK]", StringComparison.OrdinalIgnoreCase)
                || ServerDiagnostics.IsEnabled("pathMapTracking")
                    && line.StartsWith("[PATHMAP-TRACK]", StringComparison.OrdinalIgnoreCase)
                || ServerDiagnostics.IsEnabled("collisionTracking")
                    && line.StartsWith("[COLLISION-TRACK]", StringComparison.OrdinalIgnoreCase)
                || ServerDiagnostics.IsEnabled("fsmTracking")
                    && line.StartsWith("[FSM-TRACK]", StringComparison.OrdinalIgnoreCase)
                || ServerDiagnostics.IsEnabled("skillEffectTracking")
                    && line.StartsWith("[SKILL-EFFECT-TRACK]", StringComparison.OrdinalIgnoreCase)
                || ServerDiagnostics.IsEnabled("questLootTracking")
                    && line.StartsWith("[QUEST-LOOT-TRACK]", StringComparison.OrdinalIgnoreCase)
                || ServerDiagnostics.IsEnabled("respawnTracking")
                    && line.StartsWith("[RESPAWN-TRACK]", StringComparison.OrdinalIgnoreCase)
                || ServerDiagnostics.IsEnabled("encounterTracking")
                    && line.StartsWith("[ENCOUNTER-TRACK]", StringComparison.OrdinalIgnoreCase)
                || ServerDiagnostics.IsEnabled("verbosePacketLogging")
                    && StartsWithAny(line, "[UDP", "[NEW-PKT]", "[PACKET", "[OP")
                || ServerDiagnostics.IsEnabled("verboseSynchLogging")
                    && StartsWithAny(line, "[ENTITY-SYNCH", "[SYNCH", "[SEND-UPDATE]", "[MOVE-ENTITY-SYNCH")
                || ServerDiagnostics.IsEnabled("verboseMerchantItemLogging")
                    && line.StartsWith("[MERCHANT", StringComparison.OrdinalIgnoreCase)
                || ServerDiagnostics.IsEnabled("verboseMerchantItemLogging")
                    && StartsWithAny(line, "[LOOT-AUTHORED]", "[LOOT-MEMBERSHIP]", "[MERCHANT-GENERATE]", "[MERCHANT-MYTHIC]")
                || ServerDiagnostics.IsEnabled("verboseRngLogging")
                    && StartsWithAny(line, "[RNG", "[ROOM-RNG]", "[LAYOUT-SEED]", "[GC-OBJECT-GENERATOR-TABLE]", "[LOOT-GENERATOR]")
                || ServerDiagnostics.IsEnabled("verboseGnomeLogging")
                    && StartsWithAny(line, "[GNOME", "[BLING", "[GOLD")
                || ServerDiagnostics.IsEnabled("verboseMovementLogging")
                    && StartsWithAny(line, "[MP-", "[MOVE", "[LOCAL-MOVE", "[FOLLOW", "[OWNER-", "[PEER-")
                || ServerDiagnostics.IsEnabled("verboseProjectileLogging")
                    && StartsWithAny(line, "[PROJ", "[PROJECTILE", "[RANGED-PROJECTILE")
                || ServerDiagnostics.IsEnabled("verboseWanderLogging")
                    && line.StartsWith("[WANDER", StringComparison.OrdinalIgnoreCase)
                || ServerDiagnostics.IsEnabled("verboseCombatClockLogging")
                    && StartsWithAny(line, "[CLIENT-COMBAT-CLOCK]", "[COMBAT-TICK]")
                || ServerDiagnostics.IsEnabled("verboseMonsterDiagnostics")
                    && StartsWithAny(line, "[MON-", "[MONSTER-", "[NPC-");
        }

        private static bool IsTrackerLog(string line)
        {
            return line.IndexOf("-TRACK]", StringComparison.OrdinalIgnoreCase) >= 0
                || line.StartsWith("[TRACK", StringComparison.OrdinalIgnoreCase)
                || line.IndexOf("-AUDIT]", StringComparison.OrdinalIgnoreCase) >= 0
                || line.StartsWith("[RNG-LEDGER]", StringComparison.OrdinalIgnoreCase)
                || line.IndexOf("-DIAGNOSTIC]", StringComparison.OrdinalIgnoreCase) >= 0
                || line.StartsWith("[MC]", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("[SIM-PARITY]", StringComparison.OrdinalIgnoreCase);
        }

        private static bool StartsWithAny(string line, params string[] prefixes)
        {
            foreach (string prefix in prefixes)
            {
                if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool IsFocusedLog(string line)
        {
            string[] allowedPrefixes =
            {
                "[COMBAT]",
                "[COMBAT",
                "[ATTACK]",
                "[ACTION",
                "[NPC]",
                "[DAMAGE]",
                "[GET-NEAREST]",
                "[MON-DAMAGE]",
                "[MON-DAMAGE-ENTITY-SYNCH-INFO]",
                "[MON-ATTACK]",
                "[MON-STATE]",
                "[MONSTER-DAMAGE]",
                "[MON-CONTACT]",
                "[MON-HP-TRUTH]",
                "[MON-REGEN]",
                "[MON-MANA-REGEN]",
                "[MON-SKILL]",
                "[MON-SKILL-CD]",
                "[MON-MOVE]",
                "[MON-MOVE-SEND]",
                "[MON-PRE-SUFFIX-COMBAT]",
                "[MON-HP-PRIMER]",
                "[DESYNC-FORENSICS]",
                "[ENCOUNTER-MANIFEST]",
                "[ENCOUNTER-MANIFEST-CHECK]",
                "[ENCOUNTER-OBJECT]",
                "[DUNGEON-SNAPSHOT]",
                "[DUNGEON-SPAWN]",
                "[DUNGEON-TRANSFORM]",
                "[DUNGEON-PORTAL]",
                "[PROJECTILE-HIT]",
                "[PRE-SUFFIX-DUE-DRAIN]",
                "[RANGED-PROJECTILE]",
                "[RANGED-PROJECTILE-DUE]",
                "[PROJECTILE-ENTITY]",
                "[PLAYER-HP-TRUTH]",
                "[PLAYER-HP-SUFFIX]",
                "[PLAYER-HP-CLIENT",
                "[PLAYER-DAMAGE]",
                "[PLAYER-REGEN]",
                "[PLAYER-REGEN-COOLDOWN]",
                "[HP-PRESERVE]",
                "[SPAWN-HP-FRESH]",
                "[SPAWN-HP-PRESERVE]",
                "[SPAWN-HP-FULL]",
                "[SPAWN-HP-REGEN]",
                "[SPAWN-PKT]",
                "[SPAWN-XP]",
                "[ZONE-HP-PRESERVE]",
                "[ZONE-HP-FULL]",
                "[ZONE-HP-REGEN]",
                "[ZONE-HP-BASELINE]",
                "[PLAYERSTATE]",
                "[ALLOC-STATS]",
                "[HP-FINAL]",
                "[PLAYER-HIT-DETAIL]",
                "[SPAWN]",
                "[SPAWN-ENTITY-SYNCH]",
                "[SPAWN-TRACK]",
                "[PACKET-2]",
                "[PACKET-3]",
                "[OP12]",
                "[RNG-AUDIT]",
                "[RNG-COMBAT]",
                "[RNG-LEDGER]",
                "[RNG-SEED]",
                "[RUNTIME-SEED]",
                "[CLIENT-VALIDATION-CUTOFF]",
                "[LAYOUT-SEED]",
                "[UDP-RNG]",
                "[SPELL",
                "[LOCAL-MOVE-ACK]",
                "[SEND-COMPRESSEDA]",
                "[ENTITY-SYNCH-INFO",
                "[TAKEDAMAGE]",
                "[MANA]",
                "[MANA-0x52]",
                "[USETARGET-",
                "[RANGED-USE-START]",
                "[PROJECTILE-COLLISION]",
                "[WEAPON-USE]",
                "[LEVEL-UP",
                "[SERVER-AGGRO]",
                "[SERVER-SHOUT]",
                "[AGGRO]",
                "[AGGRO-OBSERVE]",
                "[MON-BEHAVIOR-RNG]",
                "[ROOM-RNG]",
                "[WANDER-RNG]",
                "[WANDER-AUDIT]",
                "[WANDER-SIM]",
                "[MON-WANDER-POS]",
                "[MON-CLIENT-POS]",
                "[MAZE-SPAWNER]",
                "[ZONE-SPAWNER]",
                "[BEHAVIOR]",
                "[DAMAGE-CLIENT-SLOTS]",
                "[CLIENT-DAMAGE-CONTRACT]",
                "[DAMAGE-LEVEL]",
                "[DAMAGE-CLASS-MOD]",
                "[ATTACK-TIMING]",
                "[LOOT-FALLBACK]",
                "[LOOT-RNG]",
                "[RNG-INSTANCE]",
                "[ITEM-SERIALIZE]",
                "[ENTITY-SYNCH-INFO-VALUE]",
                "[ENTITY-SYNCH-INFO]",
                "[SEND-UPDATE]",
                "[MOVE-ENTITY-SYNCH-INFO]",
                "[ACTION-0x50-ENTITY-SYNCH-INFO]",
                "[ACTION-ENTITY-SYNCH-INFO]",
                "[HP-VERIFY]",
                "[SYNCH",
                "[REGEN]",
                "[COMBAT-TICK]",
                "[UDP-COMBAT",
                "[ZONE-INVULN]",
                "[ZONE-TRACK]",
                "[ZONE-IN]",
                "[CHESTS]",
                "[WORLD-ENTITIES]",
                "[INSTANCE]",
                "[MERCHANT-DETAIL]",
                "[LOC]",
                "[POSSE]",
                "[PVP]",
                "[PVP-DUEL]",
                "[PVP-MATCH]",
                "[DUELARENA]",
                "[GROUP-CH0B]",
                "[GROUP]",
                "[ADMIN]",
                "[ACCOUNT]",
                "[ITEM-STAT-DB]",
                "[MERCHANT-WRITE-ITEM]",
                "[MERCHANT-WRITE-INVENTORY]",
                "[MERCHANT-PRE-WRITE]",
                "[MERCHANT-BUY]",
                "[MERCHANT-SELL]",
                "[MERCHANT-STACK]",
                "[MERCHANT-DIMS]",
                "[MERCHANT-MYTHIC]",
                "[ITEM-WIRE-MODS]",
                "[INVENTORY-WRITEINIT]",
                "[QUEST-AVAILABLE]",
                "[QUEST-ACCEPT]",
                "[QUEST-TURNIN]",
                "[QUEST-REWARDS]",
                "[QUEST-ITEM-REMOVE]",
                "[QUEST-QUERY]",
                "[QUEST-PROGRESS]",
                "[QUEST-ADD]",
                "[QUEST-COMPLETE]",
                "[QUEST-REMOVE]",
                "[DROP-RING]",
                "[DROP-AMULET]",
                "[DROP-WRITEINIT]",
                "[UNIT-CONTAINER]",
                "[INV-TRACK]",
                "[INV-SLOT]",
                "[INV-VALIDATOR]",
                "[INV-RESTORE]",
                "[EQUIP]",
                "[EQUIP-TRACK]",
                "[EQUIPMENT-INIT]",
                "[GIVE-STACKED]",
                "[GIVE-ON-ACCEPT-ITEM]",
                "[STATE]",
                "[SAVE]",
                "[MERCHANT-RUNTIME]",
                "[MERCHANT-REFRESH]",
                "[REFRESH]"
            };

            foreach (string prefix in allowedPrefixes)
            {
                if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            if (line.StartsWith("[ZONE-JOIN]", StringComparison.OrdinalIgnoreCase))
            {
                return line.IndexOf("Zone:", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("ZoneJoin", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("first-login", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("late-joiner", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("Room RNG seed", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("already spawned", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("transition", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("zoneId=", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            if (line.StartsWith("[ZONE]", StringComparison.OrdinalIgnoreCase))
            {
                return line.IndexOf("ZONE TRANSITION", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("CHECKPOINT TELEPORT", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("Sent DISCONNECT", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("Sent CONNECT", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("CurrentZone", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("Stopped tick coroutine", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("Cleared message queue", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            return false;
        }

        private sealed class FocusedLogHandler : ILogHandler
        {
            private readonly ILogHandler _inner;

            public FocusedLogHandler(ILogHandler inner)
            {
                _inner = inner;
            }

            public void LogException(Exception exception, DungeonRunners.Engine.Object context)
            {
                _inner?.LogException(exception, context);
            }

            public void LogFormat(LogType logType, DungeonRunners.Engine.Object context, string format, params object[] args)
            {
                string condition = format;
                if (args != null && args.Length > 0)
                {
                    try
                    {
                        condition = string.Format(format, args);
                    }
                    catch
                    {
                        condition = format;
                    }
                }

                if (ShouldMirrorLog(condition, logType))
                    _inner?.LogFormat(logType, context, format, args);
            }
        }

        private static string ResolveServerLogPath()
        {
            return Path.Combine(DataPaths.ServerRoot, "logs", "server.log");
        }

    }
}
