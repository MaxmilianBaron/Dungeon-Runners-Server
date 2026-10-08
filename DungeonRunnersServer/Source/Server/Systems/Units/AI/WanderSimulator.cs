using System;
using System.Collections.Generic;
using DungeonRunners.Data;
using DungeonRunners.Core;
using DungeonRunners.Engine;

namespace DungeonRunners.Combat
{
    public class WanderSimulator
    {
        private static WanderSimulator _instance;
        public static WanderSimulator Instance => _instance ??= new WanderSimulator();
        private static bool VerboseWanderLogs => ServerDiagnostics.IsEnabled("verboseWanderLogging");

        private List<WanderState> _entities = new List<WanderState>();
        private List<uint> _tickOrder = new List<uint>();

        public int EntityCount => _entities.Count;

        private static void LogVerboseWander(string message)
        {
            if (VerboseWanderLogs)
                Debug.LogError(message);
        }

        private static void LogWanderClientPos(WanderState ws)
        {
            if (ws.Monster == null)
                return;
            if (!VerboseWanderLogs)
                return;
            int fx = ws.FixedX;
            int fy = ws.FixedY;
            if (ws.HasLoggedClientPos && fx == ws.LastLoggedFixedX && fy == ws.LastLoggedFixedY)
                return;
            ws.HasLoggedClientPos = true;
            ws.LastLoggedFixedX = fx;
            ws.LastLoggedFixedY = fy;
            Debug.LogError($"[MON-WANDER-POS] entity={ws.EntityId} fixed8=({fx},{fy}) hex=(0x{(uint)fx:X8},0x{(uint)fy:X8}) state={ws.State} targetFixed8=({ws.TargetFixedX},{ws.TargetFixedY}) sourceFunction=UnitMover::Update");
        }

        private static int ResolveWanderGroundHeightFixed(Monster monster, PathMap pathMap, int worldFixedX, int worldFixedY, int fallbackFixedZ)
        {
            if (pathMap != null && pathMap.TryGetHeightAtFixed(worldFixedX, worldFixedY, out int pathGroundFixedZ))
                return pathGroundFixedZ;

            if (monster != null &&
                WorldCollision.Instance.TryGetTerrainHeightFixed(
                    monster.ZoneName,
                    monster.InstanceKey,
                    worldFixedX,
                    worldFixedY,
                    fallbackFixedZ,
                    out int worldGroundFixedZ,
                    out _))
                return worldGroundFixedZ;

            return fallbackFixedZ;
        }

        private static void ResolveWanderMovementFixed(
            WanderState ws,
            PathMap pathMap,
            int curFixedX,
            int curFixedY,
            int curFixedZ,
            int candFixedX,
            int candFixedY,
            out int outFixedX,
            out int outFixedY,
            out int outFixedZ)
        {
            UnitMover.ResolveMovement(pathMap, curFixedX, curFixedY, curFixedZ, candFixedX, candFixedY, out outFixedX, out outFixedY, out outFixedZ);
        }

        public void RegisterEntity(uint entityId, bool canWander = true)
        {
            UnregisterEntity(entityId);
            var state = new WanderState
            {
                EntityId = entityId,
                State = 0,
                Timer = 0,
                CanWander = canWander
            };
            _entities.Add(state);
            _tickOrder.Add(entityId);
            Debug.LogError($"[WANDER-SIM] Registered entity {entityId} (total: {_entities.Count})");
        }

        public void RegisterMonster(Monster monster, bool canWander = true, bool initialUpdateCompleted = false)
        {
            if (monster == null) return;
            WanderState previousState = null;
            for (int entityIndex = 0; entityIndex < _entities.Count; entityIndex++)
            {
                if (_entities[entityIndex].EntityId != monster.EntityId)
                    continue;
                previousState = _entities[entityIndex];
                break;
            }
            int moverHeadingFixed = previousState != null && previousState.HeadingInit
                ? previousState.HeadingFixed
                : 0;
            _entities.RemoveAll(e => e.EntityId == monster.EntityId);
            _tickOrder.Remove(monster.EntityId);
            var state = new WanderState
            {
                EntityId = monster.EntityId,
                Monster = monster,
                State = initialUpdateCompleted ? (byte)3 : (byte)0,
                Timer = 0,
                CanWander = canWander,
                DefaultFixedX = monster.SpawnPosFixedX,
                DefaultFixedY = monster.SpawnPosFixedY,
                FixedX = monster.PosFixedX,
                FixedY = monster.PosFixedY,
                FixedZ = monster.PosFixedZ,
                FixedInit = true,
                FixedZInit = true,
                TargetFixedX = monster.PosFixedX,
                TargetFixedY = monster.PosFixedY,
                HeadingFixed = moverHeadingFixed,
                HeadingInit = true
            };
            _entities.Add(state);
            _tickOrder.Add(monster.EntityId);
            Debug.LogError($"[WANDER-SIM] Registered monster {monster.EntityId} walkF32={monster.WalkSpeedF32} rangeF32={monster.WanderRangeF32} canWander={canWander} initialUpdateCompleted={initialUpdateCompleted} total={_entities.Count}");
            LogWanderClientPos(state);
        }

        public void UnregisterEntity(uint entityId)
        {
            _entities.RemoveAll(e => e.EntityId == entityId);
            _tickOrder.Remove(entityId);
        }

        public bool IsRegisteredEntity(uint entityId)
        {
            for (int entityIndex = 0; entityIndex < _entities.Count; entityIndex++)
                if (_entities[entityIndex].EntityId == entityId)
                    return true;
            return false;
        }

        public bool IsInitialTimerInitialized(uint entityId)
        {
            for (int entityIndex = 0; entityIndex < _entities.Count; entityIndex++)
                if (_entities[entityIndex].EntityId == entityId)
                    return _entities[entityIndex].InitialTimerInitialized;
            return false;
        }

        public void TickAll(MersenneTwister roomRng)
        {
            for (int entityIndex = 0; entityIndex < _entities.Count; entityIndex++)
            {
                TickEntity(_entities[entityIndex], roomRng, ResolveUnitOwnedRng(_entities[entityIndex], roomRng));
            }
        }

        public void TickEntity(uint entityId, MersenneTwister roomRng, MersenneTwister unitOwnedRng = null)
        {
            for (int entityIndex = 0; entityIndex < _entities.Count; entityIndex++)
            {
                if (_entities[entityIndex].EntityId != entityId)
                    continue;
                TickEntity(_entities[entityIndex], roomRng, unitOwnedRng ?? ResolveUnitOwnedRng(_entities[entityIndex], roomRng));
                return;
            }
        }

        public bool TryGetClientVisiblePositionFixed(uint entityId, out int posFixedX, out int posFixedY)
        {
            for (int entityIndex = 0; entityIndex < _entities.Count; entityIndex++)
            {
                var state = _entities[entityIndex];
                if (state.EntityId != entityId)
                    continue;

                posFixedX = state.FixedX;
                posFixedY = state.FixedY;
                return true;
            }

            posFixedX = 0;
            posFixedY = 0;
            return false;
        }

        public bool TryGetSnapshot(uint entityId, out WanderStateSnapshot snapshot)
        {
            for (int entityIndex = 0; entityIndex < _entities.Count; entityIndex++)
            {
                WanderState state = _entities[entityIndex];
                if (state.EntityId != entityId)
                    continue;

                snapshot = new WanderStateSnapshot(state);
                return true;
            }

            snapshot = default;
            return false;
        }

        public bool TryConsumeMoveCommand(uint entityId, out int targetFixedX, out int targetFixedY, out int headingWire)
        {
            for (int entityIndex = 0; entityIndex < _entities.Count; entityIndex++)
            {
                var state = _entities[entityIndex];
                if (state.EntityId != entityId || state.LastConsumedMoveCommand == state.MoveCommand)
                    continue;
                state.LastConsumedMoveCommand = state.MoveCommand;
                targetFixedX = state.TargetFixedX;
                targetFixedY = state.TargetFixedY;
                headingWire = state.TargetHeadingWire;
                return true;
            }

            targetFixedX = 0;
            targetFixedY = 0;
            headingWire = 0;
            return false;
        }

        private void TickEntity(WanderState ws, MersenneTwister roomRng, MersenneTwister unitOwnedRng)
        {
            ws.MovingThisFrame = false;
            if (ws.Monster != null
                && (!ws.Monster.IsAlive
                    || (ws.Monster.State != MonsterState.Idle && !ws.Monster.WanderActionTerminationPending)))
                return;

            switch (ws.State)
            {
                case 0:
                    ws.State = 3;
                    break;

                case 1:
                    int rngBeforeTarget = roomRng?.CallsSinceReseed ?? -1;
                    uint rawX = RngLedger.Generate(roomRng, "room", "Wander::target-x", $"{ws.Monster?.Name ?? "monster"}#{ws.EntityId}");
                    uint rawY = RngLedger.Generate(roomRng, "room", "Wander::target-y", $"{ws.Monster?.Name ?? "monster"}#{ws.EntityId}");
                    LogVerboseWander($"[WANDER-RNG] entity={ws.EntityId} state=1 target rawX=0x{rawX:X8} rawY=0x{rawY:X8} rng={rngBeforeTarget}->{roomRng?.CallsSinceReseed ?? -1} stream=room");
                    if (ws.Monster != null && ws.Monster.WanderRangeF32 > 0)
                    {
                        int range = Math.Max(1, GCDatabase.RoundFixed32ToInt(ws.Monster.WanderRangeF32));
                        uint span = (uint)Math.Max(1, range * 2);
                        int offsetX = (int)(rawX % span) - range;
                        int offsetY = (int)(rawY % span) - range;
                        int baseFixedX = ws.CanWander ? ws.DefaultFixedX : ws.FixedX;
                        int baseFixedY = ws.CanWander ? ws.DefaultFixedY : ws.FixedY;
                        int tgtFixedX = baseFixedX + offsetX * UnitMover.Fixed;
                        int tgtFixedY = baseFixedY + offsetY * UnitMover.Fixed;
                        ws.TargetAttempt++;
                        Debug.LogError($"[WANDER-TGT] entity={ws.EntityId} st={ws.State} cw={(ws.CanWander ? 1 : 0)} anchorFixed8=({baseFixedX},{baseFixedY}) currentFixed8=({ws.FixedX},{ws.FixedY}) rawX=0x{rawX:X8} rawY=0x{rawY:X8} targetFixed8=({tgtFixedX},{tgtFixedY})");
                        bool pathValid = true;
                        if (ws.CanWander && !string.IsNullOrWhiteSpace(ws.Monster.ZoneName))
                        {
                            var pathMap = ResolveWanderPathMap(ws);
                            PathReachability reachability = PathReachability.Reachable;
                            if (pathMap != null)
                            {
                                reachability = pathMap.GetReachabilityFixed(ws.FixedX, ws.FixedY, tgtFixedX, tgtFixedY);
                                pathValid = reachability == PathReachability.Reachable;
                            }
                            if (!pathValid)
                            {
                                LogVerboseWander($"[WANDER-AUDIT] entity={ws.EntityId} state=1 attempt={ws.TargetAttempt} canWander={ws.CanWander} anchorFixed8=({baseFixedX},{baseFixedY}) currentFixed8=({ws.FixedX},{ws.FixedY}) rawX=0x{rawX:X8} rawY=0x{rawY:X8} targetFixed8=({tgtFixedX},{tgtFixedY}) reachability={reachability} pathValid=False accepted=False rng={rngBeforeTarget}->{roomRng?.CallsSinceReseed ?? -1} stream=room");
                                return;
                            }
                        }
                        LogVerboseWander($"[WANDER-AUDIT] entity={ws.EntityId} state=1 attempt={ws.TargetAttempt} canWander={ws.CanWander} anchorFixed8=({baseFixedX},{baseFixedY}) currentFixed8=({ws.FixedX},{ws.FixedY}) rawX=0x{rawX:X8} rawY=0x{rawY:X8} targetFixed8=({tgtFixedX},{tgtFixedY}) pathValid={pathValid} accepted=True rng={rngBeforeTarget}->{roomRng?.CallsSinceReseed ?? -1} stream=room");
                        ws.HasTarget = true;
                        int headingFixed = UnitMover.VectorToHeadingFixed(tgtFixedX - ws.FixedX, tgtFixedY - ws.FixedY);
                        ws.TargetFixedX = tgtFixedX;
                        ws.TargetFixedY = tgtFixedY;
                        ws.TargetHeadingWire = (UnitMover.FullCircleFixed - headingFixed) % UnitMover.FullCircleFixed;
                        ws.MoveCommand++;
                    }
                    else
                    {
                        ws.HasTarget = false;
                    }
                    ws.State = 2;
                    ws.TargetAttempt = 0;
                    break;

                case 2:
                    if (ws.Monster != null && ws.HasTarget)
                    {
                        if (!ws.FixedInit)
                        {
                            ws.FixedX = ws.Monster.PosFixedX;
                            ws.FixedY = ws.Monster.PosFixedY;
                            ws.FixedZ = ws.Monster.PosFixedZ;
                            ws.FixedInit = true;
                            ws.FixedZInit = true;
                        }
                        int tgtX = ws.TargetFixedX;
                        int tgtY = ws.TargetFixedY;
                        int speedFixed = ws.Monster.MoveSpeedF32 > 0
                            ? ws.Monster.MoveSpeedF32
                            : ws.Monster.WalkSpeedF32 > 0 ? ws.Monster.WalkSpeedF32 : 0;
                        int stepFixed = UnitMover.CacheSpeedPerFrame(speedFixed, ws.Monster.SpeedMod + ws.Monster.GetActiveAttributeModifierValue("SPEEDMOD"), out _);
                        int turnRate = UnitMover.TurnRatePerTickFixed(ws.Monster.TurnRateDegrees);
                        int nextX, nextY, nextHeading;
                        bool arrived;
                        var wpm = PathMapCatalog.Instance.GetPathMap(!string.IsNullOrWhiteSpace(ws.Monster.InstanceKey) ? ws.Monster.InstanceKey : ws.Monster.ZoneName);
                        int currentFixedZ = ws.FixedZInit ? ws.FixedZ : ws.Monster.PosFixedZ;
                        UnitMover.StepTowardFixedHeading(ws.FixedX, ws.FixedY, ws.HeadingFixed, tgtX, tgtY, stepFixed, turnRate, out nextX, out nextY, out nextHeading, out arrived);
                        ResolveWanderMovementFixed(ws, wpm, ws.FixedX, ws.FixedY, currentFixedZ, nextX, nextY, out nextX, out nextY, out int nextZ);
                        ws.MovingThisFrame = nextX != ws.FixedX || nextY != ws.FixedY;
                        if (wpm == null || !wpm.TryGetHeightAtFixed(nextX, nextY, out _))
                            nextZ = ResolveWanderGroundHeightFixed(ws.Monster, wpm, nextX, nextY, nextZ);
                        ws.FixedX = nextX;
                        ws.FixedY = nextY;
                        ws.FixedZ = nextZ;
                        ws.FixedZInit = true;
                        if (!arrived)
                            ws.HeadingFixed = nextHeading;
                        if (ws.Monster != null)
                            ws.Monster.PosFixedZ = ws.FixedZ;
                        if (arrived)
                        {
                            ws.HasTarget = false;
                            Debug.LogError($"[WANDER-PHASE] entity={ws.EntityId} event=mover-arrived tick={SimulationClock.SimulationTick} state={ws.State} timer={ws.Timer} fixed8=({ws.FixedX},{ws.FixedY},{ws.FixedZ}) targetFixed8=({ws.TargetFixedX},{ws.TargetFixedY})");
                            LogVerboseWander($"[WANDER-MOVE] entity={ws.EntityId} arrivedFixed8=({ws.FixedX},{ws.FixedY}) targetFixed8=({ws.TargetFixedX},{ws.TargetFixedY}) sourceFunction=UnitMover::Update");
                        }
                        LogWanderClientPos(ws);
                    }
                    else
                    {
                        ws.ArriveTicks++;
                        if (ws.ArriveTicks >= 1)
                        {
                            ws.State = 3;
                            ws.ArriveTicks = 0;
                            Debug.LogError($"[WANDER-PHASE] entity={ws.EntityId} event=arrival-observed tick={SimulationClock.SimulationTick} state={ws.State} timer={ws.Timer} fixed8=({ws.FixedX},{ws.FixedY},{ws.FixedZ}) targetFixed8=({ws.TargetFixedX},{ws.TargetFixedY})");
                        }
                    }
                    break;

                case 3:
                    {
                        int rngBeforeTimer = roomRng?.CallsSinceReseed ?? -1;
                        uint raw = RngLedger.Generate(roomRng, "room", "Wander::timer", $"{ws.Monster?.Name ?? "monster"}#{ws.EntityId}");
                        ws.InitialTimerInitialized = true;
                        uint decision = raw % 150;
                        ushort timer = (ushort)(decision + 90);

                        if (ws.CanWander)
                        {
                            timer = (ushort)(timer * 3);
                        }

                        ws.Timer = timer;
                        ws.State = 4;
                        Debug.LogError($"[WANDER-PHASE] entity={ws.EntityId} event=timer-start tick={SimulationClock.SimulationTick} state={ws.State} raw=0x{raw:X8} timer={ws.Timer} rng={rngBeforeTimer}->{roomRng?.CallsSinceReseed ?? -1}");
                        LogVerboseWander($"[WANDER-RNG] entity={ws.EntityId} state=3 timer raw=0x{raw:X8} roll={decision} timer={timer} canWander={ws.CanWander} rng={rngBeforeTimer}->{roomRng?.CallsSinceReseed ?? -1} stream=room");
                    }
                    break;

                case 4:
                    if (ws.Timer > 0)
                        ws.Timer--;
                    if (ws.Timer > 0)
                        return;

                    if (!ws.CanWander)
                    {
                        ws.State = 1;
                        return;
                    }

                    {
                        int rngBeforeMoveCheck = roomRng?.CallsSinceReseed ?? -1;
                        uint raw = RngLedger.Generate(roomRng, "room", "Wander::move-check", $"{ws.Monster?.Name ?? "monster"}#{ws.EntityId}");
                        uint roll = raw % 100;
                        Debug.LogError($"[WANDER-PHASE] entity={ws.EntityId} event=move-check tick={SimulationClock.SimulationTick} state={ws.State} raw=0x{raw:X8} roll={roll} timer={ws.Timer} rng={rngBeforeMoveCheck}->{roomRng?.CallsSinceReseed ?? -1}");
                        LogVerboseWander($"[WANDER-RNG] entity={ws.EntityId} state=4 move-check raw=0x{raw:X8} roll={roll} rng={rngBeforeMoveCheck}->{roomRng?.CallsSinceReseed ?? -1} stream=room");

                        if (roll < 30)
                        {
                            ws.State = 1;
                        }
                        else
                        {
                            ws.Timer = 450;
                        }
                    }
                    break;

                default:
                    ws.State = 3;
                    break;
            }
        }

        private static MersenneTwister ResolveUnitOwnedRng(WanderState ws, MersenneTwister roomRng)
        {
            if (ws?.Monster?.Rng != null)
                return ws.Monster.Rng;
            if (ws?.Monster != null)
                LogVerboseWander($"[RNG-LEDGER] stream=unitOwnedCombat phase=Wander::resolve-unit-rng owner='{ws.Monster.Name}#{ws.Monster.EntityId}' alias=room clientObject=EntityManager+0x44 reason=client-unit-manager-alias sourceFunction=Wander::update");
            return roomRng;
        }

        public void Clear()
        {
            _entities.Clear();
            _tickOrder.Clear();
        }

        public string DumpState()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[WANDER-SIM] {_entities.Count} entities:");
            int[] stateCounts = new int[5];
            foreach (var e in _entities)
            {
                if (e.State >= 0 && e.State <= 4) stateCounts[e.State]++;
            }
            sb.AppendLine($"  State0={stateCounts[0]} State1={stateCounts[1]} State2={stateCounts[2]} State3={stateCounts[3]} State4={stateCounts[4]}");
            return sb.ToString();
        }

        public string DescribeSchedule(int soonTicks = 30)
        {
            int state1Pending = 0;
            int movingState2 = 0;
            int state3Waiting = 0;
            int state4Due = 0;
            int state4Soon = 0;
            foreach (var e in _entities)
            {
                switch (e.State)
                {
                    case 1:
                        state1Pending++;
                        break;
                    case 2:
                        movingState2++;
                        break;
                    case 3:
                        state3Waiting++;
                        break;
                    case 4:
                        if (e.Timer == 0) state4Due++;
                        else if (e.Timer <= soonTicks) state4Soon++;
                        break;
                }
            }
            return $"state3Waiting={state3Waiting} state4Due={state4Due} state4Soon={state4Soon} state1Pending={state1Pending} movingState2={movingState2}";
        }

        private PathMap ResolveWanderPathMap(WanderState ws)
        {
            if (ws.PathMapResolved) return ws.CachedPathMap;
            ws.PathMapResolved = true;
            if (ws.Monster == null) return null;
            string key = !string.IsNullOrWhiteSpace(ws.Monster.InstanceKey) ? ws.Monster.InstanceKey : ws.Monster.ZoneName;
            if (string.IsNullOrWhiteSpace(key)) return null;
            ws.CachedPathMap = PathMapCatalog.Instance.GetPathMap(key);
            return ws.CachedPathMap;
        }
    }

    public readonly struct WanderStateSnapshot
    {
        public uint EntityId { get; }
        public byte State { get; }
        public ushort Timer { get; }
        public bool InitialTimerInitialized { get; }
        public bool CanWander { get; }
        public int ArriveTicks { get; }
        public bool HasTarget { get; }
        public int TargetAttempt { get; }
        public int IdleSubTick { get; }
        public int FixedX { get; }
        public int FixedY { get; }
        public int FixedZ { get; }
        public int DefaultFixedX { get; }
        public int DefaultFixedY { get; }
        public bool FixedInit { get; }
        public bool FixedZInit { get; }
        public int HeadingFixed { get; }
        public bool HeadingInit { get; }
        public int MoveCommand { get; }
        public int LastConsumedMoveCommand { get; }
        public int TargetFixedX { get; }
        public int TargetFixedY { get; }
        public int TargetHeadingWire { get; }
        public bool MovingThisFrame { get; }

        internal WanderStateSnapshot(WanderState state)
        {
            EntityId = state.EntityId;
            State = state.State;
            Timer = state.Timer;
            InitialTimerInitialized = state.InitialTimerInitialized;
            CanWander = state.CanWander;
            ArriveTicks = state.ArriveTicks;
            HasTarget = state.HasTarget;
            TargetAttempt = state.TargetAttempt;
            IdleSubTick = state.IdleSubTick;
            FixedX = state.FixedX;
            FixedY = state.FixedY;
            FixedZ = state.FixedZ;
            DefaultFixedX = state.DefaultFixedX;
            DefaultFixedY = state.DefaultFixedY;
            FixedInit = state.FixedInit;
            FixedZInit = state.FixedZInit;
            HeadingFixed = state.HeadingFixed;
            HeadingInit = state.HeadingInit;
            MoveCommand = state.MoveCommand;
            LastConsumedMoveCommand = state.LastConsumedMoveCommand;
            TargetFixedX = state.TargetFixedX;
            TargetFixedY = state.TargetFixedY;
            TargetHeadingWire = state.TargetHeadingWire;
            MovingThisFrame = state.MovingThisFrame;
        }
    }

    public class WanderState
    {
        public uint EntityId;
        public byte State;
        public ushort Timer;
        public bool InitialTimerInitialized;
        public bool CanWander;
        public int ArriveTicks;
        public Monster Monster;
        public bool HasTarget;
        public int TargetAttempt;
        public int IdleSubTick;
        public int FixedX;
        public int FixedY;
        public int FixedZ;
        public int DefaultFixedX;
        public int DefaultFixedY;
        public bool FixedInit;
        public bool FixedZInit;
        public int HeadingFixed;
        public bool HeadingInit;
        public PathMap CachedPathMap;
        public bool PathMapResolved;
        public int LastLoggedFixedX = int.MinValue;
        public int LastLoggedFixedY = int.MinValue;
        public bool HasLoggedClientPos;
        public int MoveCommand;
        public int LastConsumedMoveCommand;
        public int TargetFixedX;
        public int TargetFixedY;
        public int TargetHeadingWire;
        public bool MovingThisFrame;
    }
}
