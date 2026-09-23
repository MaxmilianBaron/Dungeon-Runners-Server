using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using DungeonRunners.Combat;

namespace DungeonRunners.Core
{
    public sealed class SimulationParityPosition
    {
        public int? X { get; set; }
        public int? Y { get; set; }
        public int? Z { get; set; }
        public int? Heading { get; set; }
    }

    public sealed class SimulationParityStance
    {
        public int? State { get; set; }
        public int? Clip { get; set; }
        public int? Duration { get; set; }
        public uint? Speed { get; set; }
        public string Model { get; set; }
    }

    public sealed class SimulationParityResources
    {
        public uint? MaxHp { get; set; }
        public uint? MaxMana { get; set; }
        public uint? Hp { get; set; }
        public uint? Mana { get; set; }
        public int? HpCooldown { get; set; }
        public int? ManaCooldown { get; set; }
    }

    public sealed class SimulationParityActionState
    {
        public string Kind { get; set; }
        public string VtableRva { get; set; }
        public int? Phase { get; set; }
        public bool? Moving { get; set; }
        public int? TargetX { get; set; }
        public int? TargetY { get; set; }
        public uint? TargetEntityId { get; set; }
    }

    public sealed class SimulationParityStateMachine
    {
        public int? Clock { get; set; }
        public int? PreviousState { get; set; }
        public int? CurrentState { get; set; }
        public int? NextState { get; set; }
        public int? MessageCount { get; set; }
    }

    public sealed class SimulationParityMoverState
    {
        public int? Speed { get; set; }
        public int? SpeedPerFrame { get; set; }
        public int? TurnRatePerFrame { get; set; }
        public int? CurrentHeading { get; set; }
        public bool? MovingThisFrame { get; set; }
        public int? Mode { get; set; }
        public int? DesiredHeading { get; set; }
        public int? RequestedHeading { get; set; }
        public int? AngularVelocity { get; set; }
        public int? TargetX { get; set; }
        public int? TargetY { get; set; }
    }

    public sealed class SimulationParityBehaviorState
    {
        public int? Generation { get; set; }
        public bool? InterruptLocked { get; set; }
        public SimulationParityActionState Primary { get; set; }
        public SimulationParityActionState Alternate { get; set; }
        public SimulationParityActionState Pending { get; set; }
        public SimulationParityStateMachine TopStateMachine { get; set; }
        public SimulationParityStateMachine FollowStateMachine { get; set; }
        public SimulationParityMoverState Mover { get; set; }
    }

    public sealed class SimulationParityUnit
    {
        public uint? EntityId { get; set; }
        public uint ServerEntityId { get; set; }
        public string StableKey { get; set; }
        public string Kind { get; set; }
        public string Name { get; set; }
        public string GcType { get; set; }
        public string InstanceKey { get; set; }
        public bool KnownToViewer { get; set; }
        public string PositionModel { get; set; }
        public SimulationParityPosition Position { get; set; }
        public SimulationParityStance Stance { get; set; }
        public SimulationParityResources Resources { get; set; }
        public byte? EntityFlags { get; set; }
        public uint? UnitFlags { get; set; }
        public int? SilenceAttribute { get; set; }
        public byte? UnitState31B { get; set; }
        public int? ActivityGate { get; set; }
        public int? StockState { get; set; }
        public SimulationParityBehaviorState BehaviorState { get; set; }
        public object ServerState { get; set; }
    }

    public sealed class SimulationParityServerView
    {
        public string Viewer { get; set; }
        public int ConnectionId { get; set; }
        public uint ZoneId { get; set; }
        public string Zone { get; set; }
        public string InstanceKey { get; set; }
        public uint RoomSeed { get; set; }
        public uint ClientEntityUpdateSerial { get; set; }
        public uint SimulationTick { get; set; }
        public List<SimulationParityUnit> Units { get; set; }
    }

    internal sealed class SimulationParityServerFrame
    {
        public string Schema { get; set; }
        public string Kind { get; set; }
        public long Sequence { get; set; }
        public long TimestampMs { get; set; }
        public uint Tick { get; set; }
        public List<SimulationParityServerView> Views { get; set; }
    }

    public sealed class SimulationParityTickInput
    {
        public int ConnectionId { get; set; }
        public uint EntityId { get; set; }
        public string InstanceKey { get; set; }
        public uint ClientEntityUpdateSerial { get; set; }
        public ulong ReflectedReceivedOrdinal { get; set; }
        public ulong ReflectedAppliedOrdinal { get; set; }
        public int ReflectedReadyCount { get; set; }
        public ulong? ReflectedFrontOrdinal { get; set; }
        public byte? ReflectedFrontMoveType { get; set; }
        public int? ReflectedFrontX { get; set; }
        public int? ReflectedFrontY { get; set; }
        public int ReflectedX { get; set; }
        public int ReflectedY { get; set; }
        public ulong OwnerAckReceivedOrdinal { get; set; }
        public ulong OwnerAckAppliedOrdinal { get; set; }
        public int OwnerAckQueueCount { get; set; }
        public ulong? OwnerAckFrontOrdinal { get; set; }
        public uint? OwnerAckFrontApplyTick { get; set; }
        public byte? OwnerAckFrontMoveType { get; set; }
        public int? OwnerAckFrontX { get; set; }
        public int? OwnerAckFrontY { get; set; }
        public int OwnerAckX { get; set; }
        public int OwnerAckY { get; set; }
        public ulong UnitFollowReceivedOrdinal { get; set; }
        public ulong UnitFollowAppliedOrdinal { get; set; }
        public int UnitFollowQueueCount { get; set; }
        public ulong? UnitFollowFrontOrdinal { get; set; }
        public uint? UnitFollowFrontApplyTick { get; set; }
        public byte? UnitFollowFrontMoveType { get; set; }
        public int? UnitFollowFrontX { get; set; }
        public int? UnitFollowFrontY { get; set; }
        public int UnitFollowX { get; set; }
        public int UnitFollowY { get; set; }
        public bool HasActiveUseTarget { get; set; }
        public ushort ActiveUseTargetId { get; set; }
        public byte ActiveUseTargetFlags { get; set; }
        public byte ActiveUseTargetSessionId { get; set; }
        public uint ActiveUseTargetEvaluationTick { get; set; }
        public bool HasPendingUseTarget { get; set; }
        public uint PendingUseTargetReceivedTick { get; set; }
        public bool UsePositionActive { get; set; }
        public uint UsePositionApplyTick { get; set; }
        public uint UsePositionBusyUntilTick { get; set; }
    }

    public sealed class SimulationParityTickEntity
    {
        public int RootOrdinal { get; set; }
        public uint EntityId { get; set; }
        public string Kind { get; set; }
        public string InstanceKey { get; set; }
        public string ChildOrder { get; set; }
        public int? X { get; set; }
        public int? Y { get; set; }
        public int? Z { get; set; }
        public int? Heading { get; set; }
        public uint? Hp { get; set; }
        public uint? Mana { get; set; }
        public int? HpCooldown { get; set; }
        public int? ManaCooldown { get; set; }
        public uint? TargetEntityId { get; set; }
        public string Action { get; set; }
        public string AlternateAction { get; set; }
        public string PendingAction { get; set; }
        public int? ActionGeneration { get; set; }
        public int? TopState { get; set; }
        public int? StateClock { get; set; }
        public int? FollowState { get; set; }
        public int? NextAttackTick { get; set; }
        public int? AttackStartTick { get; set; }
        public int? AttackEndTick { get; set; }
        public int? SkillCooldownTicks { get; set; }
        public int? MoverMode { get; set; }
        public bool? MovingThisFrame { get; set; }
        public bool? Alive { get; set; }
        public bool? Aggro { get; set; }
        public bool? AttackPending { get; set; }
    }

    public sealed class SimulationParityTickRng
    {
        public string InstanceKey { get; set; }
        public uint Seed { get; set; }
        public int Position { get; set; }
    }

    internal sealed class SimulationParityServerTickDigest
    {
        public string Schema { get; set; }
        public string Kind { get; set; }
        public long Sequence { get; set; }
        public long TimestampMs { get; set; }
        public uint Tick { get; set; }
        public List<uint> RootOrder { get; set; }
        public List<string> SubEntityOrder { get; set; }
        public List<SimulationParityTickInput> Inputs { get; set; }
        public List<SimulationParityTickEntity> Entities { get; set; }
        public List<SimulationParityTickRng> Rng { get; set; }
        public string InputHash { get; set; }
        public string StateHash { get; set; }
    }

    internal sealed class SimulationParityRngRow
    {
        public string Schema { get; set; }
        public string Kind { get; set; }
        public long Sequence { get; set; }
        public long TimestampMs { get; set; }
        public uint Tick { get; set; }
        public bool IsSeed { get; set; }
        public string Stream { get; set; }
        public string Phase { get; set; }
        public string Consumer { get; set; }
        public uint Seed { get; set; }
        public int Before { get; set; }
        public int After { get; set; }
        public uint Raw { get; set; }
        public string Owner { get; set; }
        public uint? EntityId { get; set; }
        public uint? AttackerEntityId { get; set; }
        public uint? DefenderEntityId { get; set; }
        public uint? Value { get; set; }
    }

    internal sealed class SimulationParityBlingFollowDueRow
    {
        public string Schema { get; set; }
        public string Kind { get; set; }
        public long Sequence { get; set; }
        public long TimestampMs { get; set; }
        public uint Tick { get; set; }
        public uint GnomeEntityId { get; set; }
        public int OwnerConnId { get; set; }
        public uint? OwnerEntityId { get; set; }
        public int FollowCounter { get; set; }
        public int NextFollowDue { get; set; }
        public int FollowState { get; set; }
        public ulong ReflectedAppliedOrdinal { get; set; }
        public ulong ReflectedReceivedOrdinal { get; set; }
        public int ReflectedX { get; set; }
        public int ReflectedY { get; set; }
        public ulong OwnerAckAppliedOrdinal { get; set; }
        public ulong OwnerAckReceivedOrdinal { get; set; }
        public int OwnerAckX { get; set; }
        public int OwnerAckY { get; set; }
        public int OwnerAckQueueCount { get; set; }
        public int ClientSimulationX { get; set; }
        public int ClientSimulationY { get; set; }
        public int PredictedX { get; set; }
        public int PredictedY { get; set; }
        public int PlayerInputX { get; set; }
        public int PlayerInputY { get; set; }
        public int LiveOwnerX { get; set; }
        public int LiveOwnerY { get; set; }
        public int OwnerX { get; set; }
        public int OwnerY { get; set; }
        public bool OwnerMovingThisFrame { get; set; }
        public int GnomeX { get; set; }
        public int GnomeY { get; set; }
        public int DistanceSqFixed { get; set; }
        public int ReadyCount { get; set; }
        public int HeldCount { get; set; }
        public int AdmittedCount { get; set; }
        public int PendingCount { get; set; }
        public bool StreamAdmitted { get; set; }
        public bool TerminalRestartPending { get; set; }
        public bool PostAdmissionBarrierPending { get; set; }
        public bool IdleBatchBarrierPending { get; set; }
    }

    internal sealed class SimulationParityCaptureMarker
    {
        public string Schema { get; set; }
        public string Kind { get; set; }
        public long TimestampMs { get; set; }
        public int ProcessId { get; set; }
        public string Output { get; set; }
        public long Dropped { get; set; }
        public string Error { get; set; }
    }

    public static class SimulationParityLedger
    {
        private const int MaxCaptureSeconds = 900;
        private const uint FrameSampleTicks = 6;
        private static readonly object Sync = new object();
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };
        private static BlockingCollection<object> _queue;
        private static Thread _writerThread;
        private static bool _checked;
        private static volatile bool _active;
        private static volatile bool _accepting;
        private static uint? _firstFrameTick;
        private static long _captureDeadlineTimestamp;
        private static long _sequence;
        private static long _dropped;
        private static string _outputPath;
        private static string _writeError;

        public static bool Active => _active;
        public static string OutputPath => _outputPath;

        public static bool ShouldCaptureTick(uint tick)
        {
            if (!_active && tick % FrameSampleTicks == 0)
                EnsureStarted();
            if (!_active)
                return false;
            if (tick % FrameSampleTicks == 0 && ConsumeStopRequest())
            {
                RequestStop();
                return false;
            }
            if (Stopwatch.GetTimestamp() >= _captureDeadlineTimestamp)
            {
                RequestStop();
                return false;
            }
            return true;
        }

        public static bool ShouldCaptureFrame(uint tick)
        {
            return ShouldCaptureTick(tick)
                && (!_firstFrameTick.HasValue || unchecked(tick - _firstFrameTick.Value) % FrameSampleTicks == 0);
        }

        public static void EnsureStarted()
        {
            if (!ServerDiagnostics.IsEnabled("simulationParityTracking"))
                return;
            lock (Sync)
            {
                if (_active || _accepting)
                    return;
                if (_writerThread != null && _writerThread.IsAlive)
                    return;
                _checked = true;
                string logDirectory = DataPaths.ServerPath("logs");
                string requestPath = Path.Combine(logDirectory, "simulation-parity.request");
                string stopPath = Path.Combine(logDirectory, "simulation-parity.stop");
                if (!File.Exists(requestPath))
                    return;
                try
                {
                    Directory.CreateDirectory(logDirectory);
                    File.Delete(requestPath);
                    File.Delete(stopPath);
                    _firstFrameTick = null;
                    Interlocked.Exchange(ref _sequence, 0);
                    Interlocked.Exchange(ref _dropped, 0);
                    _outputPath = null;
                    _writeError = null;
                    _queue = null;
                    _writerThread = null;
                    int processId = Process.GetCurrentProcess().Id;
                    string timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
                    _outputPath = Path.Combine(logDirectory, $"simulation-parity-server-{timestamp}-pid{processId}.jsonl");
                    _queue = new BlockingCollection<object>();
                    _accepting = true;
                    _captureDeadlineTimestamp = Stopwatch.GetTimestamp() + MaxCaptureSeconds * Stopwatch.Frequency;
                    _active = true;
                    _writerThread = new Thread(WriterLoop)
                    {
                        IsBackground = true,
                        Name = "SimulationParityLedger"
                    };
                    _writerThread.Start();
                    TryEnqueue(new SimulationParityCaptureMarker
                    {
                        Schema = "dr-server-simulation-parity-capture-v1",
                        Kind = "capture-start",
                        TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        ProcessId = processId,
                        Output = _outputPath,
                        Dropped = 0
                    });
                    RngLedger.EntryObserved += OnRngEntry;
                    AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
                    DungeonRunners.Engine.Debug.LogError($"[SIM-PARITY] state=active output='{_outputPath}' maxSeconds={MaxCaptureSeconds}");
                }
                catch (Exception ex)
                {
                    _active = false;
                    _accepting = false;
                    _writeError = ex.Message;
                    DungeonRunners.Engine.Debug.LogError($"[SIM-PARITY] state=failed message='{ex.Message}'");
                }
            }
        }

        public static void EnqueueFrame(uint tick, List<SimulationParityServerView> views)
        {
            if (!_active || views == null || views.Count == 0)
                return;
            if (!_firstFrameTick.HasValue)
                _firstFrameTick = tick;
            if (!ShouldCaptureFrame(tick))
                return;
            TryEnqueue(new SimulationParityServerFrame
            {
                Schema = "dr-server-simulation-parity-event-v1",
                Kind = "server-view-frame",
                Sequence = Interlocked.Increment(ref _sequence),
                TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Tick = tick,
                Views = views
            });
        }

        public static void EnqueueTickDigest(
            uint tick,
            List<uint> rootOrder,
            List<string> subEntityOrder,
            List<SimulationParityTickInput> inputs,
            List<SimulationParityTickEntity> entities,
            List<SimulationParityTickRng> rng,
            string inputHash,
            string stateHash)
        {
            if (!_active)
                return;
            if (!_firstFrameTick.HasValue)
                _firstFrameTick = tick;
            TryEnqueue(new SimulationParityServerTickDigest
            {
                Schema = "dr-server-simulation-parity-event-v1",
                Kind = "server-tick-digest",
                Sequence = Interlocked.Increment(ref _sequence),
                TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Tick = tick,
                RootOrder = rootOrder,
                SubEntityOrder = subEntityOrder,
                Inputs = inputs,
                Entities = entities,
                Rng = rng,
                InputHash = inputHash,
                StateHash = stateHash
            });
        }

        internal static void EnqueueBlingFollowDue(SimulationParityBlingFollowDueRow row)
        {
            if (!_active || row == null)
                return;
            row.Schema = "dr-server-simulation-parity-event-v1";
            row.Kind = "bling-follow-due";
            row.Sequence = Interlocked.Increment(ref _sequence);
            row.TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            TryEnqueue(row);
        }

        public static void Stop()
        {
            Thread writer;
            lock (Sync)
            {
                if (!_checked)
                    return;
                RngLedger.EntryObserved -= OnRngEntry;
                AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
                if (_accepting)
                {
                    _accepting = false;
                    try
                    {
                        _queue?.CompleteAdding();
                    }
                    catch
                    {
                    }
                }
                _active = false;
                writer = _writerThread;
            }
            if (writer != null && writer.IsAlive && writer != Thread.CurrentThread)
                writer.Join(3000);
        }

        private static void RequestStop()
        {
            lock (Sync)
            {
                if (!_accepting)
                    return;
                RngLedger.EntryObserved -= OnRngEntry;
                AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
                _accepting = false;
                _active = false;
                try
                {
                    _queue?.CompleteAdding();
                }
                catch
                {
                }
            }
        }

        private static bool ConsumeStopRequest()
        {
            string stopPath = Path.Combine(DataPaths.ServerPath("logs"), "simulation-parity.stop");
            if (!File.Exists(stopPath))
                return false;
            try
            {
                File.Delete(stopPath);
            }
            catch
            {
            }
            return true;
        }

        private static SimulationParityCaptureMarker CreateStopMarker()
        {
            return new SimulationParityCaptureMarker
            {
                Schema = "dr-server-simulation-parity-capture-v1",
                Kind = "capture-stop",
                TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ProcessId = Process.GetCurrentProcess().Id,
                Output = _outputPath,
                Dropped = Interlocked.Read(ref _dropped),
                Error = _writeError
            };
        }

        private static void OnProcessExit(object sender, EventArgs args)
        {
            Stop();
        }

        private static void OnRngEntry(RngLedger.Entry entry)
        {
            if (!_active || !_firstFrameTick.HasValue || entry == null)
                return;
            TryEnqueue(new SimulationParityRngRow
            {
                Schema = "dr-server-simulation-parity-event-v1",
                Kind = entry.IsSeed ? "rng-seed" : "rng-draw",
                Sequence = Interlocked.Increment(ref _sequence),
                TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Tick = SimulationClock.SimulationTick,
                IsSeed = entry.IsSeed,
                Stream = entry.Stream,
                Phase = entry.Phase,
                Consumer = entry.Phase,
                Seed = entry.Seed,
                Before = entry.Before,
                After = entry.After,
                Raw = entry.Raw,
                Owner = entry.Owner,
                EntityId = entry.EntityId,
                AttackerEntityId = entry.AttackerEntityId,
                DefenderEntityId = entry.DefenderEntityId,
                Value = entry.Value
            });
        }

        private static void TryEnqueue(object row)
        {
            if (!_accepting || row == null || _queue == null)
                return;
            if (!_queue.TryAdd(row))
                Interlocked.Increment(ref _dropped);
        }

        private static void WriterLoop()
        {
            try
            {
                using var stream = new FileStream(_outputPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
                using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), 1 << 20)
                {
                    AutoFlush = false
                };
                var flushClock = Stopwatch.StartNew();
                foreach (object row in _queue.GetConsumingEnumerable())
                {
                    writer.WriteLine(JsonSerializer.Serialize(row, row.GetType(), JsonOptions));
                    if (flushClock.ElapsedMilliseconds >= 250)
                    {
                        writer.Flush();
                        flushClock.Restart();
                    }
                }
                SimulationParityCaptureMarker stop = CreateStopMarker();
                writer.WriteLine(JsonSerializer.Serialize(stop, stop.GetType(), JsonOptions));
                writer.Flush();
            }
            catch (Exception ex)
            {
                _writeError = ex.Message;
                _active = false;
                _accepting = false;
            }
        }
    }
}
