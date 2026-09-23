using System;
using DungeonRunners.Core;
using DungeonRunners.Engine;

namespace DungeonRunners.Combat.Behavior
{
    public interface IMonsterBehaviorContext
    {
        uint RoomDraw(string site);
        bool DispatchUpdateTargets();
        bool DispatchThreatNear();
        MonsterBehavior2.ActionSlot DispatchUpdateSkills();
        MonsterBehavior2.ActionSlot ResolveAttackAction();
        uint MonsterEntityId { get; }
        uint SimulationTick { get; }
        uint TargetEntityId { get; }
        uint AssistSourceEntityId { get; }
        bool IsPrimaryTargetValid(uint targetEntityId);
        bool IsAssistSourceValid(uint sourceEntityId);
        bool DoAssistAction(uint sourceEntityId, out uint targetEntityId);
        bool IsFollowTargetValid(uint targetEntityId);
        bool OwnerActionValid { get; }
        bool HasTarget { get; }
        void ClearTargets();
        int DistanceToTargetFixed { get; }
        int DistanceToTargetSquaredFixed { get; }
        bool TargetMovingThisFrame { get; }
        bool TargetInMeleeReach { get; }
        bool TargetIsPlayer { get; }
        bool HasAttackDistanceRange { get; }
        int ResolveAttackDistance(uint raw);
        bool HasRetreatDistanceRange { get; }
        int ResolveRetreatDistance(uint raw);
        int ScoreCurrentAttackLocation();
        void PrepareSearchAttackTargetActionUse();
        void FaceSearchTarget();
        int ScoreAttackLocation(int headingFixed, int distanceFixed, out int fixedX, out int fixedY);
        int ScoreRetreatLocation(int headingFixed, int distanceFixed, out int fixedX, out int fixedY);
        bool SearchCurrentLocationCollides { get; }
        void IncrementSearchCollisionCount();
        void DecrementSearchCollisionCount();
        void BeginAttackSearchMove(int fixedX, int fixedY);
        void StopAttackSearchMove();
        void UpdateAttackSearchMoveForTarget();
        bool AttackSearchMoveComplete { get; }
        bool AttackTargetActionQueued { get; }
        bool AttackTargetActionActive { get; }
        bool AttackRuntimePending { get; }
        bool IsCurrentActionBusy(MonsterBehavior2.ActionSlot action);
        bool DamageReactionActionActiveAtTickEntry { get; }
        bool DamageReactionActionActiveNow { get; }
        bool UsesWanderIdleAction { get; }
        bool PrepareIdleFollow() => false;
        void UpdateWanderAction();
        bool ConsumeWanderActionTermination();
        bool ValidateAttackTarget2Action();
        bool DoAttackAction();
        bool DoUseAction();
        bool DoUseTargetAction();
        bool StartFleeAction();
        void UpdateFleeAction();
        bool FleeActionActive { get; }
        void StopFleeAction();
        void StopAttackTarget2Action();
        void ClearUseTargetAction();
        void StartQueuedAttackTargetAction();
        void UpdateFollowSpeedMod();
        void ResetFollowSpeedMod();
        void StopMoving();
        bool TryMoveFollowTowardTarget();
        void BeginFollowFindLineOfSightMove();
        void StopFollowFindLineOfSightMove();
        bool FollowFindLineOfSightMoving { get; }
        int ResolveFollowRepositionPointFixed(int headingFixed, int distanceFixed, out int fixedX, out int fixedY);
        void BeginFollowRepositionMove(int fixedX, int fixedY);
        void StopFollowRepositionMove();
        void QueueFollowReachedNearStop();
        bool RetreatRequired => false;
        bool ReturnMoveActive => false;
        bool ValidateReturnMove() => false;
        bool StartReturnMove() => false;
        void SetRetreatModifier(bool active) { }
        bool ShouldDespawn => false;
        bool CanFinishReturn => false;
        void SetEncounterReturning(bool returning) { }
        void FinishEncounterReturn() { }
    }

    public sealed class MonsterBehavior2
    {
        public enum ActionSlot : byte
        {
            None,
            Spawn,
            Follow,
            SearchForAttack,
            AttackTarget2,
            Use,
            UseTarget,
            Wander,
            DamageReaction,
            Flee,
            Kill,
            MoveTo
        }

        private const int MsgUpdateMorale = 3;
        private const int MsgUpdateTargets = 2;
        private const int MsgUpdateSkills = 4;
        private const int MsgSkillCond = 5;
        private const int MsgThreatNear = 9;
        private const int MsgThreatAlt = 10;
        private const int MsgActionTerminated = 8;
        private const int MsgSearchCurrentScore = 2;
        private const int MsgScanTick = 6;

        private const int StateInit = 0;
        private const int StateIdle = 5;
        private const int StateAttack = 6;
        private const int StateRecover = 0xb;
        private const int StateAcquire = 0xc;
        private const int StateRetreat = 9;
        private const int StateReturn = 0x14;
        private const int StateRemoved = 4;
        private const int StateFollow = 0x1d;

        private const int FollowSubChase = 2;
        private const int FollowSubNear = 5;
        private const int FollowSubReposition = 8;
        private const int FollowSubFindLineOfSight = 0xf;

        private const int ScanSubScan = 0x10;
        private const int ScanSubRetreat = 0x11;
        private const int ScanSubAttackMove = 0x12;
        private const int ScanSubRetreatMove = 0x13;

        private const int RepositionDistSquaredFixed = 0x6400;
        private const int NearDistSquaredFixed = 0x38400;

        public readonly StateMachine Sm = new StateMachine();
        private readonly StateMachine _followSm = new StateMachine();
        private readonly StateMachine _searchSm = new StateMachine();
        public int TopState;
        public int CombatTimer;
        public bool SwingDrawsEnabled;

        private int _followSub;
        private bool _followMovementActive;
        private bool _followActive;
        private uint _followTargetEntityId;
        private uint _followStartSimulationTick = uint.MaxValue;
        private int _scanSub;
        private int _searchPendingState = -1;
        private int _scanCountdown;
        private bool _maneuverActive;
        private bool _maneuverPending;
        private bool _maneuverStartPending;
        private bool _wanderActionPending;
        private int _scanMoveGapTicks;
        private int _scanHeadingFixed;
        private int _scanBestScore;
        private int _scanBestFixedX;
        private int _scanBestFixedY;
        private bool _attackTarget2Pending;
        private bool _attackTarget2State5Pending;
        private bool _attackTarget2ObservedAttack;
        private bool _useTargetStarted;
        private bool _useTargetObservedAttack;
        private int _attackUseIndex;
        private int _prevUseIndex;
        private ActionSlot _currentAction;
        private ActionSlot _alternateAction;
        private ActionSlot _pendingAction;
        private bool _currentActionInterruptLocked;
        private byte _currentDamageReactionActionId;
        private byte _alternateDamageReactionActionId;
        private byte _pendingDamageReactionActionId;
        private uint _primaryTargetEntityId;
        private uint _alternateTargetEntityId;
        private bool _primaryTargetObserved;
        private bool _alternateTargetObserved;

        private const byte SpawnPhaseStart = 0;
        private const byte SpawnPhaseFadeOut = 1;
        private const byte SpawnPhaseFadeIn = 2;
        private const byte SpawnPhaseComplete = 100;

        private byte _spawnPhase;
        private byte _spawnCounter;
        private byte _actionGeneration;

        public byte SpawnPhase => _spawnPhase;
        public byte SpawnCounter => _spawnCounter;
        public byte ActionGeneration => _actionGeneration;
        public bool SpawnActive => _spawnPhase != SpawnPhaseComplete;
        public bool SpawnCompletedThisTick { get; private set; }
        public bool WanderActionPending => _wanderActionPending;
        public bool SearchForAttackResetScanHeadingPending => _maneuverActive && _maneuverStartPending;
        public bool SearchForAttackScanForAttackLocationPending =>
            _maneuverActive
            && !_maneuverStartPending
            && _scanSub == ScanSubScan
            && _searchSm.GetMessageETA(MsgScanTick) == 1;
        public bool SearchForAttackScanForRetreatLocationPending =>
            _maneuverActive
            && !_maneuverStartPending
            && _scanSub == ScanSubRetreat
            && _searchSm.GetMessageETA(MsgScanTick) == 1;
        public bool HasCurrentAction => _currentAction != ActionSlot.None;
        public ActionSlot CurrentAction => _currentAction;
        public ActionSlot AlternateAction => _alternateAction;
        public ActionSlot PendingAction => _pendingAction;
        public bool CurrentActionInterruptLocked => _currentActionInterruptLocked;
        public uint PrimaryTargetEntityId => _primaryTargetEntityId;
        public uint AlternateTargetEntityId => _alternateTargetEntityId;
        public bool PrimaryTargetObserved => _primaryTargetObserved;
        public bool AlternateTargetObserved => _alternateTargetObserved;

        public MonsterBehavior2Snapshot GetSnapshot()
        {
            return new MonsterBehavior2Snapshot(
                TopState,
                CombatTimer,
                Sm.GetSnapshot(),
                _followSm.GetSnapshot(),
                _followActive ? _followSub : -1,
                _followActive,
                _followTargetEntityId,
                _scanSub,
                _scanCountdown,
                _maneuverActive,
                _scanMoveGapTicks,
                _attackUseIndex,
                _prevUseIndex,
                _spawnPhase,
                _spawnCounter,
                _actionGeneration,
                SpawnActive,
                _wanderActionPending,
                _maneuverPending,
                _attackTarget2Pending,
                _currentAction == ActionSlot.AttackTarget2,
                _currentAction,
                _alternateAction,
                _pendingAction,
                _currentActionInterruptLocked,
                _currentDamageReactionActionId,
                _alternateDamageReactionActionId,
                _pendingDamageReactionActionId,
                _primaryTargetEntityId,
                _alternateTargetEntityId,
                _primaryTargetObserved,
                _alternateTargetObserved);
        }

        public void EnterSimulation(IMonsterBehaviorContext context)
        {
            Sm.Reset();
            _followSm.Reset();
            _searchSm.Reset();
            TopState = StateInit;
            CombatTimer = 0;
            _followActive = false;
            _followTargetEntityId = 0;
            _followStartSimulationTick = uint.MaxValue;
            _maneuverActive = false;
            _maneuverPending = false;
            _maneuverStartPending = false;
            _wanderActionPending = false;
            _scanSub = 0;
            _searchPendingState = -1;
            _scanCountdown = 0;
            _scanHeadingFixed = 0;
            _scanBestScore = 0;
            _scanBestFixedX = 0;
            _scanBestFixedY = 0;
            _attackTarget2Pending = false;
            _attackTarget2State5Pending = false;
            _useTargetStarted = false;
            _useTargetObservedAttack = false;
            _currentAction = ActionSlot.None;
            _alternateAction = ActionSlot.None;
            _pendingAction = ActionSlot.None;
            _currentActionInterruptLocked = false;
            _currentDamageReactionActionId = 0;
            _alternateDamageReactionActionId = 0;
            _pendingDamageReactionActionId = 0;
            _primaryTargetEntityId = 0;
            _alternateTargetEntityId = 0;
            _primaryTargetObserved = false;
            _alternateTargetObserved = false;
            _followSub = 0;
            _followMovementActive = false;
            _prevUseIndex = 0;
            _scanMoveGapTicks = 0;
            _spawnPhase = SpawnPhaseStart;
            _spawnCounter = 0;
            _actionGeneration = 0;
            SpawnCompletedThisTick = false;
            DoInterruptLocal(context, ActionSlot.Spawn);
            States(context, StateInit, EventEnter, 0);
            AdvanceSpawnAction();
        }

        public void ResetAfterRessurect(IMonsterBehaviorContext context)
        {
            Sm.Reset();
            _followSm.Reset();
            _searchSm.Reset();
            TopState = StateInit;
            CombatTimer = 0;
            _followActive = false;
            _followTargetEntityId = 0;
            _followStartSimulationTick = uint.MaxValue;
            _maneuverActive = false;
            _maneuverPending = false;
            _maneuverStartPending = false;
            _wanderActionPending = false;
            _scanSub = 0;
            _searchPendingState = -1;
            _scanCountdown = 0;
            _scanHeadingFixed = 0;
            _scanBestScore = 0;
            _scanBestFixedX = 0;
            _scanBestFixedY = 0;
            _attackTarget2Pending = false;
            _attackTarget2State5Pending = false;
            _attackTarget2ObservedAttack = false;
            _useTargetStarted = false;
            _useTargetObservedAttack = false;
            _currentAction = ActionSlot.None;
            _alternateAction = ActionSlot.None;
            _pendingAction = ActionSlot.None;
            _currentActionInterruptLocked = false;
            _currentDamageReactionActionId = 0;
            _alternateDamageReactionActionId = 0;
            _pendingDamageReactionActionId = 0;
            _primaryTargetEntityId = 0;
            _alternateTargetEntityId = 0;
            _primaryTargetObserved = false;
            _alternateTargetObserved = false;
            _followSub = 0;
            _followMovementActive = false;
            _prevUseIndex = 0;
            _scanMoveGapTicks = 0;
            _spawnPhase = SpawnPhaseComplete;
            _spawnCounter = 0;
            _actionGeneration = 0;
            SpawnCompletedThisTick = false;
            States(context, StateInit, EventEnter, 0);
        }

        public const int EventUpdate = 0;
        public const int EventEnter = 1;
        public const int EventExit = 2;
        public const int EventMessage = 3;

        public bool TryPromotePendingWanderAction()
        {
            if (SpawnActive || !_wanderActionPending || _currentAction != ActionSlot.Wander)
                return false;
            _wanderActionPending = false;
            return true;
        }

        public void UpdateActions(IMonsterBehaviorContext context)
        {
            SpawnCompletedThisTick = false;
            ActionSlot currentAtTickEntry = ResolveCurrentAction();
            bool attackTarget2UseStartedThisTick = false;
            if (currentAtTickEntry == ActionSlot.None)
            {
                ReleaseAlternateAction();
                PromotePendingAction(context);
            }
            else
                UpdateCurrentAction(context, currentAtTickEntry, out attackTarget2UseStartedThisTick);
            if (attackTarget2UseStartedThisTick && context.AttackTargetActionQueued)
                context.StartQueuedAttackTargetAction();
        }

        public void UpdateStateMachine(IMonsterBehaviorContext context)
        {
            if (CombatTimer > 0) CombatTimer--;
            if (!context.OwnerActionValid)
                return;
            ObserveTargetWatchers(context);
            Sm.DeliverMessages((id, param) => OnMessage(context, id));
            States(context, TopState, EventUpdate, 0);
        }

        private void ObserveTargetWatchers(IMonsterBehaviorContext context)
        {
            bool primaryPresent = HasPrimaryTarget(context);
            if (primaryPresent != _primaryTargetObserved)
            {
                Sm.SendMessageA(MsgThreatNear, 0, 0);
                _primaryTargetObserved = primaryPresent;
            }
            bool alternatePresent = HasAlternateTarget(context);
            if (alternatePresent != _alternateTargetObserved)
            {
                Sm.SendMessageA(MsgThreatAlt, 0, 0);
                _alternateTargetObserved = alternatePresent;
            }
        }

        private bool HasPrimaryTarget(IMonsterBehaviorContext context)
        {
            return _primaryTargetEntityId != 0
                && context.IsPrimaryTargetValid(_primaryTargetEntityId);
        }

        private bool HasAlternateTarget(IMonsterBehaviorContext context)
        {
            return _alternateTargetEntityId != 0
                && context.IsAssistSourceValid(_alternateTargetEntityId);
        }

        private void ClearTargetWatchers(IMonsterBehaviorContext context)
        {
            _primaryTargetEntityId = 0;
            _alternateTargetEntityId = 0;
            context.ClearTargets();
        }

        private ActionSlot ResolveCurrentAction()
        {
            return _currentAction;
        }

        private bool HasCurrentActionNow()
        {
            return ResolveCurrentAction() != ActionSlot.None;
        }

        private void UpdateCurrentAction(IMonsterBehaviorContext context, ActionSlot action, out bool attackTarget2UseStarted)
        {
            attackTarget2UseStarted = false;
            if (action == ActionSlot.MoveTo)
            {
                if (!context.ReturnMoveActive)
                    TerminateCurrentAction(context);
                return;
            }
            if (action == ActionSlot.Spawn)
            {
                AdvanceSpawnAction();
                if (!SpawnActive)
                    TerminateCurrentAction(context);
                return;
            }
            if (action == ActionSlot.Wander)
            {
                context.UpdateWanderAction();
                return;
            }
            if (action == ActionSlot.Follow)
            {
                _followSm.DeliverMessages((id, param) =>
                {
                    if (ValidateFollowAction(context))
                        OnFollowMessage(context, id);
                });
                ValidateFollowAction(context);
                return;
            }
            if (action == ActionSlot.SearchForAttack)
            {
                if (_maneuverStartPending)
                    StartManeuver(context);
                else
                    _searchSm.DeliverMessages((id, param) => OnSearchMessage(context, id));
                if (_currentAction != ActionSlot.SearchForAttack)
                    return;
                if (_scanSub == ScanSubAttackMove && context.AttackSearchMoveComplete)
                {
                    _searchPendingState = ScanSubScan;
                }
                else if (_scanSub == ScanSubRetreatMove && context.AttackSearchMoveComplete)
                {
                    _searchPendingState = ScanSubScan;
                }
                ApplyPendingSearchState(context);
                return;
            }
            if (action == ActionSlot.AttackTarget2)
            {
                UpdateAttackTarget2Action(context, out attackTarget2UseStarted);
                return;
            }
            if (action == ActionSlot.UseTarget)
            {
                UpdateUseTargetAction(context);
                return;
            }
            if (action == ActionSlot.Use)
            {
                UpdateUseAction(context);
                return;
            }
            if (action == ActionSlot.Flee)
            {
                context.UpdateFleeAction();
                if (!context.FleeActionActive)
                    TerminateCurrentAction(context);
                return;
            }
            if (action == ActionSlot.DamageReaction
                && context.DamageReactionActionActiveAtTickEntry
                && !context.DamageReactionActionActiveNow)
                TerminateCurrentAction(context);
        }

        private void UpdateAttackTarget2Action(IMonsterBehaviorContext context, out bool useStarted)
        {
            useStarted = false;
            if (_attackTarget2State5Pending)
            {
                _attackTarget2State5Pending = false;
                if (context.DoAttackAction())
                {
                    _attackTarget2ObservedAttack = context.AttackRuntimePending;
                    useStarted = true;
                    Debug.LogError($"[MON-ATTACKTARGET2] entity={context.MonsterEntityId} state=5 event=use tick={context.SimulationTick} sourceFunction=AttackTarget2::States@0x00523DA0");
                    return;
                }
                TerminateCurrentAction(context);
                Debug.LogError($"[MON-ATTACKTARGET2] entity={context.MonsterEntityId} state=5 event=terminate tick={context.SimulationTick} sourceFunction=AttackTarget2::States@0x00523DA0->Action::terminate@0x0052C1C0");
                return;
            }
            if (_attackTarget2ObservedAttack && context.AttackRuntimePending)
            {
                if (context.AttackTargetActionQueued)
                    context.StartQueuedAttackTargetAction();
                return;
            }
            if (_attackTarget2ObservedAttack)
                TerminateCurrentAction(context);
        }

        private void UpdateUseTargetAction(IMonsterBehaviorContext context)
        {
            if (!_useTargetObservedAttack)
            {
                _useTargetStarted = context.DoUseTargetAction();
                if (!_useTargetStarted)
                {
                    TerminateCurrentAction(context);
                    return;
                }
                _useTargetObservedAttack = context.AttackRuntimePending;
                if (_useTargetObservedAttack && context.AttackTargetActionQueued)
                    context.StartQueuedAttackTargetAction();
                return;
            }
            if (context.AttackRuntimePending)
            {
                _useTargetObservedAttack = true;
                if (context.AttackTargetActionQueued)
                    context.StartQueuedAttackTargetAction();
                return;
            }
            if (_useTargetObservedAttack)
                TerminateCurrentAction(context);
        }

        private void UpdateUseAction(IMonsterBehaviorContext context)
        {
            if (!_useTargetStarted)
            {
                _useTargetStarted = context.DoUseAction();
                if (!_useTargetStarted)
                    TerminateCurrentAction(context);
                return;
            }
            if (!context.IsCurrentActionBusy(ActionSlot.Use))
                TerminateCurrentAction(context);
        }

        private void PromotePendingAction(IMonsterBehaviorContext context)
        {
            if (HasCurrentActionNow())
                return;
            ActionSlot pending = _pendingAction;
            if (pending == ActionSlot.None)
                return;
            byte pendingDamageReactionActionId = _pendingDamageReactionActionId;
            ClearPendingAction(pending == ActionSlot.Wander);
            StartCurrentAction(context, pending, pendingDamageReactionActionId, false);
        }

        private void AdvanceSpawnAction()
        {
            if (_spawnPhase == SpawnPhaseStart)
            {
                _spawnPhase = SpawnPhaseFadeOut;
                _spawnCounter = 10;
            }
            if (_spawnPhase == SpawnPhaseFadeOut)
            {
                if (_spawnCounter != 0)
                    _spawnCounter--;
                if (_spawnCounter == 0)
                {
                    _spawnCounter = 15;
                    _spawnPhase = SpawnPhaseFadeIn;
                }
            }
            if (_spawnPhase == SpawnPhaseFadeIn)
            {
                if (_spawnCounter != 0)
                    _spawnCounter--;
                if (_spawnCounter == 0)
                {
                    _spawnPhase = SpawnPhaseComplete;
                    SpawnCompletedThisTick = true;
                }
            }
        }

        private void OnFollowMessage(IMonsterBehaviorContext context, int id)
        {
            if (id != 0x0f) return;
            if (_followSub == FollowSubChase)
            {
                if (context.DistanceToTargetSquaredFixed <= NearDistSquaredFixed && !context.TargetMovingThisFrame)
                {
                    _followSub = FollowSubNear;
                    _followMovementActive = false;
                    context.StopFollowRepositionMove();
                    context.ResetFollowSpeedMod();
                    context.StopMoving();
                    context.QueueFollowReachedNearStop();
                    Debug.LogError($"[MON-FOLLOW-STATE] entity={context.MonsterEntityId} tick={context.SimulationTick} state=2->5 distanceSqFixed={context.DistanceToTargetSquaredFixed} targetMovingThisFrame={context.TargetMovingThisFrame} sourceFunction=Follow::UpdateMoving@0x005277E0->UnitMover::IsMovingThisFrame@0x00536210");
                    return;
                }
                context.UpdateFollowSpeedMod();
                _followMovementActive = context.TryMoveFollowTowardTarget();
                if (!_followMovementActive)
                {
                    _followSub = FollowSubFindLineOfSight;
                    context.BeginFollowFindLineOfSightMove();
                }
                return;
            }
            if (_followSub == FollowSubNear)
            {
                if (context.DistanceToTargetSquaredFixed < RepositionDistSquaredFixed)
                {
                    _followSub = FollowSubReposition;
                    _followMovementActive = false;
                    context.ResetFollowSpeedMod();
                    PickRandomNearbyPoint(context);
                    Debug.LogError($"[MON-FOLLOW-STATE] entity={context.MonsterEntityId} tick={context.SimulationTick} state=5->8 distanceSqFixed={context.DistanceToTargetSquaredFixed} targetMovingThisFrame={context.TargetMovingThisFrame} sourceFunction=Follow::UpdateIdle@0x00527650->Follow::States@0x005273B0");
                    return;
                }
                if (context.DistanceToTargetSquaredFixed > NearDistSquaredFixed)
                {
                    _followSub = FollowSubChase;
                    context.StopFollowRepositionMove();
                    context.UpdateFollowSpeedMod();
                    _followMovementActive = context.TryMoveFollowTowardTarget();
                    Debug.LogError($"[MON-FOLLOW-STATE] entity={context.MonsterEntityId} tick={context.SimulationTick} state=5->2 distanceSqFixed={context.DistanceToTargetSquaredFixed} sourceFunction=Follow::UpdateIdle@0x00527650->Follow::EnterMoving@0x00527700");
                }
                return;
            }
            if (_followSub == FollowSubReposition)
            {
                if (context.DistanceToTargetSquaredFixed > NearDistSquaredFixed)
                {
                    _followSub = FollowSubChase;
                    context.StopFollowRepositionMove();
                    context.UpdateFollowSpeedMod();
                    _followMovementActive = context.TryMoveFollowTowardTarget();
                    Debug.LogError($"[MON-FOLLOW-STATE] entity={context.MonsterEntityId} tick={context.SimulationTick} state=8->2 distanceSqFixed={context.DistanceToTargetSquaredFixed} targetMovingThisFrame={context.TargetMovingThisFrame} sourceFunction=Follow::UpdateReposition@0x00527B90->Follow::EnterMoving@0x00527700");
                    return;
                }
                if (!context.TargetMovingThisFrame)
                {
                    _followSub = FollowSubNear;
                    _followMovementActive = false;
                    context.StopFollowRepositionMove();
                    context.ResetFollowSpeedMod();
                    context.StopMoving();
                    Debug.LogError($"[MON-FOLLOW-STATE] entity={context.MonsterEntityId} tick={context.SimulationTick} state=8->5 distanceSqFixed={context.DistanceToTargetSquaredFixed} targetMovingThisFrame={context.TargetMovingThisFrame} sourceFunction=Follow::UpdateReposition@0x00527B90->Follow::EnterIdle@0x005275D0");
                }
                return;
            }
            if (_followSub == FollowSubFindLineOfSight)
            {
                if (context.DistanceToTargetSquaredFixed <= NearDistSquaredFixed && !context.TargetMovingThisFrame)
                {
                    _followSub = FollowSubNear;
                    _followMovementActive = false;
                    context.StopFollowFindLineOfSightMove();
                    context.ResetFollowSpeedMod();
                    context.StopMoving();
                    return;
                }
                context.UpdateFollowSpeedMod();
                if (context.TryMoveFollowTowardTarget())
                {
                    _followSub = FollowSubChase;
                    _followMovementActive = true;
                    context.StopFollowFindLineOfSightMove();
                    return;
                }
                _followMovementActive = false;
                if (!context.FollowFindLineOfSightMoving)
                    context.BeginFollowFindLineOfSightMove();
            }
        }

        private bool ValidateFollowAction(IMonsterBehaviorContext context)
        {
            if (_currentAction != ActionSlot.Follow)
                return false;
            if (_followTargetEntityId != 0
                && context.IsFollowTargetValid(_followTargetEntityId))
                return true;
            TerminateCurrentAction(context);
            return false;
        }

        private void OnMessage(IMonsterBehaviorContext context, int id)
        {
            if (ServerDiagnostics.IsEnabled("fsmTracking"))
                Debug.LogError($"[FSM-TRACK] machine=monster-top event=message entity={context.MonsterEntityId} tick={context.SimulationTick} state={TopState} message={id} target={context.TargetEntityId} assist={context.AssistSourceEntityId} action={_currentAction} pending={_pendingAction}");
            if (TopState == StateRetreat && (id == 9 || id == 7 || id == 2 || id == 3 || id == 14 || id == 4 || id == 12 || id == 5))
                return;
            if (TopState == StateReturn && (id == 7 || id == 14 || id == 9 || id == 10))
            {
                States(context, TopState, EventMessage, id);
                return;
            }
            if (id == MsgUpdateMorale)
            {
                if (context.RetreatRequired)
                    SetState(context, StateRetreat);
                return;
            }
            if (id == 7)
            {
                if (context.ShouldDespawn)
                    Sm.SendMessageA(14, 0, 0);
                return;
            }
            if (id == 14)
            {
                SetState(context, StateReturn);
                return;
            }
            if (id == MsgUpdateTargets)
            {
                if (context.DispatchUpdateTargets())
                {
                    _primaryTargetEntityId = context.TargetEntityId;
                    Sm.SendMessageA(MsgThreatNear, 0, 0);
                }
                return;
            }
            if (id == MsgUpdateSkills)
            {
                ActionSlot action = context.DispatchUpdateSkills();
                if (action != ActionSlot.None)
                    DoActionLocal(context, action);
                return;
            }
            States(context, TopState, EventMessage, id);
        }

        private void OnSearchMessage(IMonsterBehaviorContext context, int id)
        {
            if (ServerDiagnostics.IsEnabled("fsmTracking"))
                Debug.LogError($"[FSM-TRACK] machine=monster-search event=message entity={context.MonsterEntityId} tick={context.SimulationTick} state={_scanSub} message={id} target={context.TargetEntityId} action={_currentAction}");
            if (id == MsgSearchCurrentScore)
            {
                OnSearchCurrentScore(context);
            }
            else if (id == MsgScanTick)
            {
                if (_scanSub == ScanSubScan)
                    OnAttackScanTick(context);
                else if (_scanSub == ScanSubRetreat)
                    OnRetreatScanTick(context);
            }
            ApplyPendingSearchState(context);
        }

        private void ApplyPendingSearchState(IMonsterBehaviorContext context)
        {
            if (!_maneuverActive || _searchPendingState < 0)
                return;
            int next = _searchPendingState;
            _searchPendingState = -1;
            if (_scanSub == ScanSubScan || _scanSub == ScanSubRetreat)
                _searchSm.CancelMessage(MsgScanTick);
            if (next == ScanSubRetreat)
                EnterSearchRetreatScan(context);
            else if (next == ScanSubScan)
                EnterSearchAttackScan(context);
        }

        private void SetState(IMonsterBehaviorContext context, int next)
        {
            if (TopState == next) return;
            int previous = TopState;
            States(context, TopState, EventExit, 0);
            TopState = next;
            States(context, next, EventEnter, 0);
            if (ServerDiagnostics.IsEnabled("fsmTracking"))
                Debug.LogError($"[FSM-TRACK] machine=monster-top event=transition entity={context.MonsterEntityId} tick={context.SimulationTick} previous={previous} requested={next} applied={TopState} target={context.TargetEntityId} assist={context.AssistSourceEntityId} action={_currentAction} alternate={_alternateAction} pending={_pendingAction}");
        }

        private void States(IMonsterBehaviorContext context, int state, int ev, int msgId)
        {
            switch (state)
            {
                case StateInit:
                    if (ev == EventEnter)
                    {
                        Sm.SendMessageA(MsgUpdateMorale, 30, 30);
                        Sm.SendMessageA(MsgUpdateTargets, 45, 45);
                        Sm.SendMessageA(MsgUpdateSkills, 30, 30);
                        TopState = StateIdle;
                        States(context, StateIdle, EventEnter, 0);
                    }
                    return;

                case StateIdle:
                    if (ev == EventEnter)
                    {
                        ClearTargetWatchers(context);
                        TerminateAllActionsLocal(context, false);
                        DoIdleAction(context);
                        return;
                    }
                    if (ev == EventExit)
                    {
                        TerminateAllActionsLocal(context, false);
                        return;
                    }
                    if (ev == EventUpdate)
                    {
                        if (!HasCurrentActionNow())
                            DoIdleAction(context);
                        return;
                    }
                    if (ev == EventMessage)
                    {
                        if (msgId == MsgThreatNear && HasPrimaryTarget(context))
                        {
                            if (context.DispatchThreatNear())
                                SetState(context, StateAttack);
                        }
                        else if (msgId == MsgThreatAlt && HasAlternateTarget(context))
                            SetState(context, StateAcquire);
                        else if (msgId == 5)
                            DoManeuverAction(context);
                    }
                    return;

                case StateAttack:
                    if (ev == EventEnter)
                    {
                        CombatTimer = 300;
                        if (HasPrimaryTarget(context))
                        {
                            bool wanderActionTerminated = context.ConsumeWanderActionTermination();
                            if (wanderActionTerminated && _currentAction == ActionSlot.Wander)
                                RetireCurrentAction(context, false);
                            bool actionStartedCurrent = !HasCurrentActionNow();
                            ActionSlot attackAction = context.ResolveAttackAction();
                            bool actionAccepted = DoActionLocal(context, attackAction);
                            Debug.LogError($"[MON-ATTACK-ACTION] entity={context.MonsterEntityId} event=admission tick={context.SimulationTick} accepted={actionAccepted} current={actionStartedCurrent} active={context.AttackTargetActionActive} sourceFunction=MonsterBehavior2::DoAttackAction@0x0051D900");
                            if (wanderActionTerminated)
                                Sm.SendMessageA(MsgActionTerminated, 0, 0);
                        }
                    }
                    else if (ev == EventMessage)
                    {
                        if (msgId == MsgThreatNear)
                        {
                            if (HasPrimaryTarget(context))
                            {
                                TerminateAllActionsLocal(context, false);
                                SetState(context, StateRecover);
                            }
                            else
                                SetState(context, StateIdle);
                        }
                        else if (msgId == 8)
                            SetState(context, StateRecover);
                    }
                    return;

                case StateRecover:
                    if (ev == EventEnter)
                    {
                        DoManeuverAction(context);
                    }
                    else if (ev == EventMessage)
                    {
                        if (msgId == 8)
                            SetState(context, StateAttack);
                        else if (msgId == MsgThreatNear)
                        {
                            if (HasPrimaryTarget(context))
                            {
                                TerminateAllActionsLocal(context, false);
                                SetState(context, StateAttack);
                            }
                            else
                                SetState(context, StateIdle);
                        }
                    }
                    return;

                case StateAcquire:
                    if (ev == EventEnter)
                    {
                        if (context.DoAssistAction(_alternateTargetEntityId, out uint targetEntityId))
                        {
                            _primaryTargetEntityId = targetEntityId;
                            SetState(context, StateAttack);
                        }
                        else
                        {
                            _primaryTargetEntityId = 0;
                            _alternateTargetEntityId = 0;
                            SetState(context, StateIdle);
                        }
                    }
                    return;

                case StateRetreat:
                    if (ev == EventEnter)
                    {
                        ClearTargetWatchers(context);
                        TerminateAllActionsLocal(context, false);
                        context.SetRetreatModifier(true);
                    }
                    else if (ev == EventExit)
                        context.SetRetreatModifier(false);
                    else if (ev == EventUpdate && !HasCurrentActionNow())
                    {
                        if (context.RetreatRequired)
                            DoActionLocal(context, ActionSlot.MoveTo);
                        else
                            SetState(context, StateIdle);
                    }
                    return;

                case StateReturn:
                    if (ev == EventEnter)
                    {
                        DoActionLocal(context, ActionSlot.MoveTo);
                        context.SetEncounterReturning(true);
                    }
                    else if (ev == EventExit)
                        context.SetEncounterReturning(false);
                    else if (ev == EventMessage)
                    {
                        if (msgId == 7)
                        {
                            if (!context.ShouldDespawn)
                                SetState(context, StateIdle);
                            else
                            {
                                Sm.SendMessageA(14, 0, 0);
                                if (context.CanFinishReturn)
                                {
                                    context.FinishEncounterReturn();
                                    SetState(context, StateRemoved);
                                }
                            }
                        }
                        else if (msgId == MsgThreatNear && HasPrimaryTarget(context))
                            SetState(context, StateAttack);
                        else if (msgId == MsgThreatAlt && HasAlternateTarget(context))
                            SetState(context, StateAcquire);
                    }
                    return;

                case StateFollow:
                case StateRemoved:
                    return;
            }
        }

        private void StartFollow(IMonsterBehaviorContext context)
        {
            context.StopFollowRepositionMove();
            context.StopFollowFindLineOfSightMove();
            _followActive = true;
            _followTargetEntityId = context.TargetEntityId;
            _followStartSimulationTick = context.SimulationTick;
            bool startNear = context.DistanceToTargetSquaredFixed < NearDistSquaredFixed && !context.TargetMovingThisFrame;
            _followSub = startNear ? FollowSubNear : FollowSubChase;
            _followMovementActive = false;
            if (startNear)
            {
                context.ResetFollowSpeedMod();
                context.StopMoving();
            }
            else
            {
                context.UpdateFollowSpeedMod();
                _followMovementActive = context.TryMoveFollowTowardTarget();
            }
            uint raw = context.RoomDraw("Follow::start");
            _followSm.Reset();
            _followSm.SendMessageA(0x0f, (int)(raw % 10u), 10);
        }

        public bool BeginFollow(IMonsterBehaviorContext context)
        {
            return DoInterruptLocal(context, ActionSlot.Follow);
        }

        public bool BeginSpawn(IMonsterBehaviorContext context)
        {
            _currentActionInterruptLocked = false;
            return DoInterruptLocal(context, ActionSlot.Spawn);
        }

        public bool BeginDamageReaction(IMonsterBehaviorContext context, byte actionId)
        {
            return DoInterruptLocal(context, ActionSlot.DamageReaction, actionId);
        }

        public bool BeginFlee(IMonsterBehaviorContext context)
        {
            return DoInterruptLocal(context, ActionSlot.Flee);
        }

        public bool ProcessUseTargetWeaponInterruptInput(IMonsterBehaviorContext context)
        {
            if (!ValidateAction(context, ActionSlot.UseTarget))
            {
                OnDoActionFailed();
                return false;
            }
            if (_currentAction != ActionSlot.UseTarget)
                return DoInterruptLocal(context, ActionSlot.UseTarget);
            if (_currentActionInterruptLocked)
            {
                OnDoActionFailed();
                return false;
            }
            _actionGeneration = unchecked((byte)(_actionGeneration + 1));
            ActionSlot retiredPendingAction = _pendingAction;
            ClearPendingAction(false);
            if (retiredPendingAction != ActionSlot.None)
                OnDoActionFailed();
            ReleaseAlternateAction();
            _alternateAction = _currentAction;
            _alternateDamageReactionActionId = _currentDamageReactionActionId;
            _currentAction = ActionSlot.UseTarget;
            _currentActionInterruptLocked = true;
            _currentDamageReactionActionId = 0;
            _useTargetStarted = context.DoUseTargetAction();
            _useTargetObservedAttack = _useTargetStarted && context.AttackRuntimePending;
            Debug.LogError($"[MON-ACTION-SLOT] entity={context.MonsterEntityId} event=use-target-weapon-interrupt tick={context.SimulationTick} current={_currentAction} alternate={_alternateAction} pending={_pendingAction} generation={_actionGeneration} interruptLocked={_currentActionInterruptLocked} sourceFunction=Behavior::doInterruptLocal@0x00515290->UseTarget::start@0x00547F20");
            return _useTargetStarted;
        }

        public bool ProcessAttackTarget2WeaponInterruptInput(IMonsterBehaviorContext context)
        {
            if (!ValidateAction(context, ActionSlot.AttackTarget2)
                || _currentAction != ActionSlot.AttackTarget2
                || !context.AttackRuntimePending)
            {
                OnDoActionFailed();
                return false;
            }
            if (_currentActionInterruptLocked)
            {
                OnDoActionFailed();
                return false;
            }
            _actionGeneration = unchecked((byte)(_actionGeneration + 1));
            ActionSlot retiredPendingAction = _pendingAction;
            ClearPendingAction(false);
            if (retiredPendingAction != ActionSlot.None)
                OnDoActionFailed();
            _currentActionInterruptLocked = true;
            Debug.LogError($"[MON-ACTION-SLOT] entity={context.MonsterEntityId} event=attack-target2-weapon-interrupt tick={context.SimulationTick} current={_currentAction} alternate={_alternateAction} pending={_pendingAction} generation={_actionGeneration} interruptLocked={_currentActionInterruptLocked} sourceFunction=Behavior::doInterruptLocal@0x00515290->Behavior::startAction@0x00515CF0->AttackTarget2::start@0x00524510");
            return true;
        }

        public void NotifyThreatNear(IMonsterBehaviorContext context)
        {
            if (_primaryTargetEntityId == 0 || !context.IsPrimaryTargetValid(_primaryTargetEntityId))
            {
                _primaryTargetEntityId = context.TargetEntityId;
                Sm.SendMessageA(MsgThreatNear, 0, 0);
            }
        }

        public void NotifyThreatAlternate(IMonsterBehaviorContext context)
        {
            if (_alternateTargetEntityId == 0 || !context.IsAssistSourceValid(_alternateTargetEntityId))
                _alternateTargetEntityId = context.AssistSourceEntityId;
        }

        public void ProcessTerminateAllInput(IMonsterBehaviorContext context)
        {
            TerminateAllActionsLocal(context, true);
        }

        public void TerminateAllActionsForDeath(IMonsterBehaviorContext context)
        {
            TerminateAllActionsLocal(context, true);
        }

        public bool BeginStockUnitLifespanKill(IMonsterBehaviorContext context)
        {
            TerminateAllActionsLocal(context, true);
            return DoInterruptLocal(context, ActionSlot.Kill);
        }

        public void CompleteStockUnitLifespanKill(IMonsterBehaviorContext context)
        {
            if (_currentAction == ActionSlot.Kill)
                TerminateCurrentAction(context);
        }

        public void OnDoActionFailed()
        {
            Sm.SendMessageA(MsgActionTerminated, 0, 0);
        }

        public bool FollowActive => _followActive;
        public uint FollowTargetEntityId => _followActive ? _followTargetEntityId : 0;
        public uint FollowStartSimulationTick => _followStartSimulationTick;
        public bool FollowMovementActive => _followMovementActive;
        public bool FollowMovesTowardTarget => _followActive && _followMovementActive && _followSub == FollowSubChase;
        public bool AttackSearchActive => _maneuverActive;
        public bool AttackTarget2Active => _currentAction == ActionSlot.AttackTarget2;
        public bool UseTargetActive => _currentAction == ActionSlot.UseTarget;
        public bool IsIdleState => TopState == StateIdle;

        public bool QueueReceivedActionPreservingCurrent(IMonsterBehaviorContext context, ActionSlot action)
        {
            if (!ValidateAction(context, action))
            {
                OnDoActionFailed();
                return false;
            }
            if (_currentAction == ActionSlot.None)
                return false;
            ActionSlot retiredPendingAction = _pendingAction;
            ClearPendingAction(false);
            if (retiredPendingAction != ActionSlot.None)
                OnDoActionFailed();
            QueuePendingAction(action);
            Debug.LogError($"[MON-ACTION-SLOT] entity={context.MonsterEntityId} event=pending-preserve-current tick={context.SimulationTick} current={_currentAction} pending={_pendingAction} alternate={_alternateAction} generation={_actionGeneration} sourceFunction=Behavior::processUpdate@0x00515620->Behavior::doActionLocal@0x00515130");
            return true;
        }

        private void DoManeuverAction(IMonsterBehaviorContext context)
        {
            DoActionLocal(context, ActionSlot.SearchForAttack);
        }

        private void DoIdleAction(IMonsterBehaviorContext context)
        {
            if (context.PrepareIdleFollow())
                DoActionLocal(context, ActionSlot.Follow);
            else if (context.UsesWanderIdleAction)
                DoActionLocal(context, ActionSlot.Wander);
        }

        private void StartPendingManeuver(IMonsterBehaviorContext context)
        {
            _maneuverPending = false;
            _maneuverActive = true;
            _maneuverStartPending = true;
            Debug.LogError($"[MON-SEARCH-ACTION] entity={context.MonsterEntityId} event=promoted tick={context.SimulationTick} sourceFunction=Behavior::update@0x005154B0->Behavior::startAction@0x00515CF0");
        }

        private void StartManeuver(IMonsterBehaviorContext context)
        {
            _maneuverStartPending = false;
            _searchSm.Reset();
            _searchPendingState = -1;
            _searchSm.SendMessageA(MsgSearchCurrentScore, 0, 15);
            _searchSm.DeliverMessages((id, param) => OnSearchMessage(context, id));
            if (_currentAction != ActionSlot.SearchForAttack)
                return;
            EnterSearchAttackScan(context);
        }

        private void EnterSearchAttackScan(IMonsterBehaviorContext context)
        {
            _scanSub = ScanSubScan;
            _scanMoveGapTicks = 0;
            context.FaceSearchTarget();
            int currentScore = context.ScoreCurrentAttackLocation();
            if (currentScore > 0)
            {
                context.PrepareSearchAttackTargetActionUse();
                context.StopAttackSearchMove();
                Debug.LogError($"[MON-SEARCH-ACTION] entity={context.MonsterEntityId} event=current-score-accepted tick={context.SimulationTick} score={currentScore} sourceFunction=SearchForAttack::ScoreAttackLocation@0x0052D700");
                TerminateCurrentAction(context);
                return;
            }
            ResetSearchScan(context);
        }

        private void EnterSearchRetreatScan(IMonsterBehaviorContext context)
        {
            _scanSub = ScanSubRetreat;
            context.FaceSearchTarget();
            if (!context.SearchCurrentLocationCollides)
            {
                uint gateRaw = context.RoomDraw("SearchForAttack::States:retreat-gate");
                if (gateRaw % 0x65u > 0x18u)
                {
                    _searchPendingState = ScanSubScan;
                    return;
                }
            }
            ResetSearchScan(context);
        }

        private void ResetSearchScan(IMonsterBehaviorContext context)
        {
            _scanCountdown = 3;
            _scanBestScore = 0;
            _scanBestFixedX = 0;
            _scanBestFixedY = 0;
            _scanMoveGapTicks = 0;
            uint raw = context.RoomDraw("SearchForAttack::ResetScanHeading");
            _scanHeadingFixed = (int)(raw % 360u) << 8;
            Debug.LogError($"[MON-SEARCH-ACTION] entity={context.MonsterEntityId} event=reset tick={context.SimulationTick} raw=0x{raw:X8} heading={_scanHeadingFixed} scanState={_scanSub} sourceFunction=SearchForAttack::ResetScanHeading@0x0052DF40");
            _searchSm.SendMessageA(MsgScanTick, 0, 5);
        }

        private void OnSearchCurrentScore(IMonsterBehaviorContext context)
        {
            if (!_maneuverActive)
                return;
            int currentScore = context.ScoreCurrentAttackLocation();
            if (currentScore > 0)
            {
                context.PrepareSearchAttackTargetActionUse();
                context.StopAttackSearchMove();
                TerminateCurrentAction(context);
                return;
            }
            if (_scanSub == ScanSubAttackMove && !context.AttackSearchMoveComplete && context.TargetMovingThisFrame)
                context.UpdateAttackSearchMoveForTarget();
        }

        private void AdvanceScanHeading()
        {
            _scanHeadingFixed += 120 << 8;
            if (_scanHeadingFixed > 360 << 8)
                _scanHeadingFixed -= 360 << 8;
        }

        private void OnAttackScanTick(IMonsterBehaviorContext context)
        {
            if (!_maneuverActive || _scanSub != ScanSubScan) return;
            if (_scanCountdown == 0)
            {
                if (_scanBestScore > 0)
                {
                    _searchSm.CancelMessage(MsgScanTick);
                    context.DecrementSearchCollisionCount();
                    _scanSub = ScanSubAttackMove;
                    _scanMoveGapTicks = 0;
                    context.BeginAttackSearchMove(_scanBestFixedX, _scanBestFixedY);
                    return;
                }
                context.IncrementSearchCollisionCount();
                _searchPendingState = ScanSubRetreat;
                return;
            }

            AdvanceScanHeading();
            int distanceFixed = context.ResolveAttackDistance(
                context.HasAttackDistanceRange
                    ? context.RoomDraw("SearchForAttack::GetRandomAttackDistance")
                    : 0u);
            int score = context.ScoreAttackLocation(_scanHeadingFixed, distanceFixed, out int fixedX, out int fixedY);
            if (score > _scanBestScore)
            {
                _scanBestScore = score;
                _scanBestFixedX = fixedX;
                _scanBestFixedY = fixedY;
            }
            else if (score == _scanBestScore)
            {
                uint tieRaw = context.RoomDraw("SearchForAttack::ScanForAttackLocation::tie");
                if (tieRaw % 100u > 48u)
                {
                    _scanBestFixedX = fixedX;
                    _scanBestFixedY = fixedY;
                }
            }
            _scanCountdown--;
            if (_scanCountdown == 0)
                _scanMoveGapTicks = 5;
        }

        private void OnRetreatScanTick(IMonsterBehaviorContext context)
        {
            if (!_maneuverActive || _scanSub != ScanSubRetreat) return;
            if (_scanCountdown == 0)
            {
                if (_scanBestScore > 0)
                {
                    _searchSm.CancelMessage(MsgScanTick);
                    context.DecrementSearchCollisionCount();
                    _scanSub = ScanSubRetreatMove;
                    _scanMoveGapTicks = 0;
                    context.BeginAttackSearchMove(_scanBestFixedX, _scanBestFixedY);
                    return;
                }
                context.IncrementSearchCollisionCount();
                _searchPendingState = ScanSubScan;
                return;
            }

            AdvanceScanHeading();
            int distanceFixed = context.ResolveRetreatDistance(
                context.HasRetreatDistanceRange
                    ? context.RoomDraw("SearchForAttack::GetRandomRetreatDistance")
                    : 0u);
            int score = context.ScoreRetreatLocation(_scanHeadingFixed, distanceFixed, out int fixedX, out int fixedY);
            if (score > _scanBestScore)
            {
                _scanBestScore = score;
                _scanBestFixedX = fixedX;
                _scanBestFixedY = fixedY;
            }
            else if (score == _scanBestScore)
            {
                uint tieRaw = context.RoomDraw("SearchForAttack::ScanForRetreatLocation::tie");
                if (tieRaw % 100u > 48u)
                {
                    _scanBestFixedX = fixedX;
                    _scanBestFixedY = fixedY;
                }
            }
            _scanCountdown--;
            if (_scanCountdown == 0)
                _scanMoveGapTicks = 5;
        }

        private void PickRandomNearbyPoint(IMonsterBehaviorContext context)
        {
            uint headingRaw = context.RoomDraw("Follow::PickRandomNearbyPoint-heading");
            int headingDeg = (int)(headingRaw % 0x168u);
            uint radiusRaw = 0;
            int distanceFixed = 0;
            int reachFixed = 0;
            int targetFixedX = 0;
            int targetFixedY = 0;
            int iter = 0;
            int radiusDraws = 0;
            do
            {
                radiusRaw = context.RoomDraw("Follow::PickRandomNearbyPoint-radius");
                radiusDraws++;
                distanceFixed = (int)((radiusRaw % 0x14u + 10u) * 0x100u);
                reachFixed = context.ResolveFollowRepositionPointFixed(headingDeg << 8, distanceFixed, out targetFixedX, out targetFixedY);
                if (reachFixed > 0xa00) break;
                if (headingDeg < 0xb4) headingDeg += 0xb4;
                else if (headingDeg > 0xb4) headingDeg -= 0xb4;
                iter++;
            } while (iter < 2);
            context.BeginFollowRepositionMove(targetFixedX, targetFixedY);
            Debug.LogError($"[MON-FOLLOW-REPOSITION] entity={context.MonsterEntityId} tick={context.SimulationTick} headingRaw=0x{headingRaw:X8} radiusRaw=0x{radiusRaw:X8} heading={headingDeg} requestedDistanceFixed={distanceFixed} actualDistanceFixed={reachFixed} radiusDraws={radiusDraws} targetFixed8=({targetFixedX},{targetFixedY}) sourceFunction=Follow::PickRandomNearbyPoint@0x00527D30");
        }

        private void MeleeWeaponUse(IMonsterBehaviorContext context)
        {
            if (!SwingDrawsEnabled) return;
            uint useRaw = context.RoomDraw("MeleeWeapon::use");
            _attackUseIndex = (int)(((useRaw & 1u) + (uint)_prevUseIndex + 1u) % 3u);
            _prevUseIndex = _attackUseIndex;

            context.RoomDraw("Weapon::hitRoll");
            context.RoomDraw("Weapon::blockGate");
            context.RoomDraw("Weapon::damageRoll");
        }

        private bool DoActionLocal(IMonsterBehaviorContext context, ActionSlot action)
        {
            if (!ValidateAction(context, action))
            {
                OnDoActionFailed();
                Debug.LogError($"[MON-ACTION-SLOT] entity={context.MonsterEntityId} event=action-rejected tick={context.SimulationTick} incoming={action} current={_currentAction} pending={_pendingAction} alternate={_alternateAction} generation={_actionGeneration} sourceFunction=Behavior::doActionLocal@0x00515130->MonsterBehavior2::onDoActionFailed@0x00516860");
                return false;
            }
            ActionSlot retiredPendingAction = _pendingAction;
            ClearPendingAction(false);
            if (retiredPendingAction != ActionSlot.None)
            {
                OnDoActionFailed();
                Debug.LogError($"[MON-ACTION-SLOT] entity={context.MonsterEntityId} event=pending-terminated tick={context.SimulationTick} retired={retiredPendingAction} incoming={action} current={_currentAction} generation={_actionGeneration} sourceFunction=Behavior::doActionLocal@0x00515130->Action::terminate@0x0052C1C0->MonsterBehavior2::onIdle@0x0051B4A0");
            }
            if (_currentAction == ActionSlot.None)
            {
                StartCurrentAction(context, action, 0, false);
                return true;
            }
            QueuePendingAction(action);
            Debug.LogError($"[MON-ACTION-SLOT] entity={context.MonsterEntityId} event=pending tick={context.SimulationTick} current={_currentAction} pending={_pendingAction} alternate={_alternateAction} sourceFunction=Behavior::doActionLocal@0x00515130");
            bool currentBusy = context.IsCurrentActionBusy(_currentAction);
            Debug.LogError($"[MON-ACTION-BUSY] entity={context.MonsterEntityId} tick={context.SimulationTick} current={_currentAction} busy={currentBusy} interruptLocked={_currentActionInterruptLocked} pending={_pendingAction} sourceFunction=Behavior::doActionLocal@0x00515130->Action::isBusy@vtable+0xD8");
            if (!_currentActionInterruptLocked && !currentBusy)
                ShutdownCurrentAction(context);
            return true;
        }

        private void StartCurrentAction(IMonsterBehaviorContext context, ActionSlot action, byte damageReactionActionId, bool interruptLocked)
        {
            _currentAction = action;
            _currentActionInterruptLocked = interruptLocked;
            _currentDamageReactionActionId = action == ActionSlot.DamageReaction ? damageReactionActionId : (byte)0;
            if (action == ActionSlot.MoveTo)
            {
                if (!context.StartReturnMove())
                    TerminateCurrentAction(context);
                return;
            }
            if (action == ActionSlot.Spawn)
            {
                _spawnPhase = SpawnPhaseStart;
                _spawnCounter = 0;
                SpawnCompletedThisTick = false;
                return;
            }
            if (action == ActionSlot.Wander)
                _wanderActionPending = true;
            if (action == ActionSlot.SearchForAttack)
            {
                StartPendingManeuver(context);
                return;
            }
            if (action == ActionSlot.Follow)
            {
                StartFollow(context);
                return;
            }
            if (action == ActionSlot.AttackTarget2)
            {
                _attackTarget2State5Pending = true;
                _attackTarget2ObservedAttack = false;
                Debug.LogError($"[MON-ATTACKTARGET2] entity={context.MonsterEntityId} state=0 event=current tick={context.SimulationTick} sourceFunction=Behavior::startAction@0x00515CF0->AttackTarget2::start@0x00524510");
                return;
            }
            if (action == ActionSlot.UseTarget)
            {
                _useTargetStarted = false;
                _useTargetObservedAttack = false;
                StartUseTargetAction(context);
                return;
            }
            if (action == ActionSlot.Use)
            {
                _useTargetStarted = context.DoUseAction();
                _useTargetObservedAttack = false;
                if (!_useTargetStarted)
                    TerminateCurrentAction(context);
                return;
            }
            if (action == ActionSlot.Flee && !context.StartFleeAction())
            {
                TerminateCurrentAction(context);
            }
        }

        private void StartUseTargetAction(IMonsterBehaviorContext context)
        {
            _useTargetStarted = context.DoUseTargetAction();
            _useTargetObservedAttack = _useTargetStarted && context.AttackRuntimePending;
            if (!_useTargetStarted)
                TerminateCurrentAction(context);
        }

        private void QueuePendingAction(ActionSlot action, byte damageReactionActionId = 0)
        {
            _pendingAction = action;
            _pendingDamageReactionActionId = action == ActionSlot.DamageReaction ? damageReactionActionId : (byte)0;
            _wanderActionPending = action == ActionSlot.Wander;
            _maneuverPending = action == ActionSlot.SearchForAttack;
            _attackTarget2Pending = action == ActionSlot.AttackTarget2;
        }

        private void ClearPendingAction(bool preserveWanderRegistration)
        {
            if (!preserveWanderRegistration)
                _wanderActionPending = false;
            _maneuverPending = false;
            _attackTarget2Pending = false;
            _pendingAction = ActionSlot.None;
            _pendingDamageReactionActionId = 0;
        }

        private void ReleaseAlternateAction()
        {
            _alternateAction = ActionSlot.None;
            _alternateDamageReactionActionId = 0;
        }

        private void TerminateCurrentAction(IMonsterBehaviorContext context)
        {
            RetireCurrentAction(context, true);
        }

        private void RetireCurrentAction(IMonsterBehaviorContext context, bool notifyIdle)
        {
            ActionSlot retired = _currentAction;
            if (retired == ActionSlot.None)
                return;
            byte retiredDamageReactionActionId = _currentDamageReactionActionId;
            ClearCurrentActionState(context, retired);
            ReleaseAlternateAction();
            _alternateAction = retired;
            _alternateDamageReactionActionId = retiredDamageReactionActionId;
            _currentAction = ActionSlot.None;
            _currentActionInterruptLocked = false;
            _currentDamageReactionActionId = 0;
            if (notifyIdle && _pendingAction == ActionSlot.None)
                Sm.SendMessageA(MsgActionTerminated, 0, 0);
        }

        private void ClearCurrentActionState(IMonsterBehaviorContext context, ActionSlot action)
        {
            if (action == ActionSlot.Follow)
            {
                _followActive = false;
                _followTargetEntityId = 0;
                _followStartSimulationTick = uint.MaxValue;
                _followMovementActive = false;
                _followSub = 0;
                _followSm.Reset();
                context?.StopFollowRepositionMove();
                context?.StopFollowFindLineOfSightMove();
                context?.ResetFollowSpeedMod();
                return;
            }
            if (action == ActionSlot.SearchForAttack)
            {
                _maneuverActive = false;
                _maneuverStartPending = false;
                _scanSub = 0;
                _searchPendingState = -1;
                _scanCountdown = 0;
                _scanMoveGapTicks = 0;
                _searchSm.Reset();
                context?.StopAttackSearchMove();
                return;
            }
            if (action == ActionSlot.AttackTarget2)
            {
                context?.StopAttackTarget2Action();
                _attackTarget2State5Pending = false;
                _attackTarget2ObservedAttack = false;
                return;
            }
            if (action == ActionSlot.Use || action == ActionSlot.UseTarget)
            {
                _useTargetStarted = false;
                _useTargetObservedAttack = false;
                context?.ClearUseTargetAction();
                return;
            }
            if (action == ActionSlot.Flee)
                context?.StopFleeAction();
        }

        private bool DoInterruptLocal(IMonsterBehaviorContext context, ActionSlot action, byte damageReactionActionId = 0)
        {
            if (!ValidateAction(context, action))
            {
                OnDoActionFailed();
                Debug.LogError($"[MON-ACTION-SLOT] entity={context.MonsterEntityId} event=interrupt-action-rejected tick={context.SimulationTick} incoming={action} current={_currentAction} pending={_pendingAction} alternate={_alternateAction} generation={_actionGeneration} sourceFunction=Behavior::doInterruptLocal@0x00515290->MonsterBehavior2::onDoActionFailed@0x00516860");
                return false;
            }
            if (_currentAction != ActionSlot.None && _currentActionInterruptLocked)
            {
                OnDoActionFailed();
                Debug.LogError($"[MON-ACTION-SLOT] entity={context.MonsterEntityId} event=interrupt-rejected tick={context.SimulationTick} current={_currentAction} incoming={action} pending={_pendingAction} alternate={_alternateAction} generation={_actionGeneration} sourceFunction=Behavior::doInterruptLocal@0x00515290->MonsterBehavior2::onDoActionFailed@0x00516860");
                return false;
            }
            _actionGeneration = unchecked((byte)(_actionGeneration + 1));
            ActionSlot retiredPendingAction = _pendingAction;
            ClearPendingAction(false);
            if (retiredPendingAction != ActionSlot.None)
                OnDoActionFailed();
            RetireCurrentAction(context, false);
            StartCurrentAction(context, action, damageReactionActionId, true);
            return true;
        }

        private static bool ValidateAction(IMonsterBehaviorContext context, ActionSlot action)
        {
            if (context == null || !context.OwnerActionValid)
                return false;
            if (action == ActionSlot.AttackTarget2)
                return context.HasTarget && context.ValidateAttackTarget2Action();
            if (action == ActionSlot.MoveTo)
                return context.ValidateReturnMove();
            if (action == ActionSlot.SearchForAttack || action == ActionSlot.UseTarget || action == ActionSlot.Follow)
                return context.HasTarget;
            return true;
        }

        private void ShutdownCurrentAction(IMonsterBehaviorContext context)
        {
            if (_currentAction == ActionSlot.None || _currentAction == ActionSlot.Spawn)
                return;
            if (_currentAction == ActionSlot.DamageReaction
                && (_currentDamageReactionActionId == 0x0A || _currentDamageReactionActionId == 0x0B))
                return;
            TerminateCurrentAction(context);
        }

        private void ClearAllActionsLocal(IMonsterBehaviorContext context)
        {
            _actionGeneration = unchecked((byte)(_actionGeneration + 1));
            ClearPendingAction(false);
            if (_currentAction != ActionSlot.None && !_currentActionInterruptLocked)
                ShutdownCurrentAction(context);
        }

        private void TerminateAllActionsLocal(IMonsterBehaviorContext context, bool force)
        {
            _actionGeneration = unchecked((byte)(_actionGeneration + 1));
            if (_currentAction != ActionSlot.None && _currentActionInterruptLocked && !force)
                return;
            ClearPendingAction(false);
            if (force && _currentAction != ActionSlot.None)
            {
                TerminateCurrentAction(context);
                return;
            }
            ShutdownCurrentAction(context);
        }
    }

    public readonly struct MonsterBehavior2Snapshot
    {
        public int TopState { get; }
        public int CombatTimer { get; }
        public StateMachineSnapshot StateMachine { get; }
        public StateMachineSnapshot FollowStateMachine { get; }
        public int FollowState { get; }
        public bool FollowActive { get; }
        public uint FollowTargetEntityId { get; }
        public int ScanState { get; }
        public int ScanCountdown { get; }
        public bool ManeuverActive { get; }
        public int ScanMoveGapTicks { get; }
        public int AttackUseIndex { get; }
        public int PreviousUseIndex { get; }
        public byte SpawnPhase { get; }
        public byte SpawnCounter { get; }
        public byte ActionGeneration { get; }
        public bool SpawnActive { get; }
        public bool WanderActionPending { get; }
        public bool ManeuverPending { get; }
        public bool AttackTarget2Pending { get; }
        public bool AttackTarget2Active { get; }
        public MonsterBehavior2.ActionSlot CurrentAction { get; }
        public MonsterBehavior2.ActionSlot AlternateAction { get; }
        public MonsterBehavior2.ActionSlot PendingAction { get; }
        public bool CurrentActionInterruptLocked { get; }
        public byte CurrentDamageReactionActionId { get; }
        public byte AlternateDamageReactionActionId { get; }
        public byte PendingDamageReactionActionId { get; }
        public uint PrimaryTargetEntityId { get; }
        public uint AlternateTargetEntityId { get; }
        public bool PrimaryTargetObserved { get; }
        public bool AlternateTargetObserved { get; }

        public MonsterBehavior2Snapshot(
            int topState,
            int combatTimer,
            StateMachineSnapshot stateMachine,
            StateMachineSnapshot followStateMachine,
            int followState,
            bool followActive,
            uint followTargetEntityId,
            int scanState,
            int scanCountdown,
            bool maneuverActive,
            int scanMoveGapTicks,
            int attackUseIndex,
            int previousUseIndex,
            byte spawnPhase,
            byte spawnCounter,
            byte actionGeneration,
            bool spawnActive,
            bool wanderActionPending,
            bool maneuverPending,
            bool attackTarget2Pending,
            bool attackTarget2Active,
            MonsterBehavior2.ActionSlot currentAction,
            MonsterBehavior2.ActionSlot alternateAction,
            MonsterBehavior2.ActionSlot pendingAction,
            bool currentActionInterruptLocked,
            byte currentDamageReactionActionId,
            byte alternateDamageReactionActionId,
            byte pendingDamageReactionActionId,
            uint primaryTargetEntityId,
            uint alternateTargetEntityId,
            bool primaryTargetObserved,
            bool alternateTargetObserved)
        {
            TopState = topState;
            CombatTimer = combatTimer;
            StateMachine = stateMachine;
            FollowStateMachine = followStateMachine;
            FollowState = followState;
            FollowActive = followActive;
            FollowTargetEntityId = followTargetEntityId;
            ScanState = scanState;
            ScanCountdown = scanCountdown;
            ManeuverActive = maneuverActive;
            ScanMoveGapTicks = scanMoveGapTicks;
            AttackUseIndex = attackUseIndex;
            PreviousUseIndex = previousUseIndex;
            SpawnPhase = spawnPhase;
            SpawnCounter = spawnCounter;
            ActionGeneration = actionGeneration;
            SpawnActive = spawnActive;
            WanderActionPending = wanderActionPending;
            ManeuverPending = maneuverPending;
            AttackTarget2Pending = attackTarget2Pending;
            AttackTarget2Active = attackTarget2Active;
            CurrentAction = currentAction;
            AlternateAction = alternateAction;
            PendingAction = pendingAction;
            CurrentActionInterruptLocked = currentActionInterruptLocked;
            CurrentDamageReactionActionId = currentDamageReactionActionId;
            AlternateDamageReactionActionId = alternateDamageReactionActionId;
            PendingDamageReactionActionId = pendingDamageReactionActionId;
            PrimaryTargetEntityId = primaryTargetEntityId;
            AlternateTargetEntityId = alternateTargetEntityId;
            PrimaryTargetObserved = primaryTargetObserved;
            AlternateTargetObserved = alternateTargetObserved;
        }
    }
}
