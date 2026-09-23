using System;
using System.Collections.Generic;
using System.Linq;
using DungeonRunners.Combat;
using DungeonRunners.Core;
using DungeonRunners.Data;
using DungeonRunners.Engine;

namespace DungeonRunners.Networking
{
    public sealed class BlingGnomeLockstepRuntime
    {
        private sealed class ConversionItemState
        {
            public ushort EntityId;
            public uint GoldAmount;
            public bool BonusEligible;
            public int Phase;
            public int BounceTimer;
        }

        private sealed class ItemAcquisition
        {
            public State Owner;
            public int Timer;
            public uint LastTick = uint.MaxValue;
        }

        private sealed class State
        {
            public RRConnection Owner;
            public uint EntityId;
            public ushort BehaviorId;
            public string InstanceKey;
            public bool Initialized;
            public bool FollowStarted;
            public bool SpawnAnimationActionActive;
            public byte SpawnAnimationPhase;
            public byte SpawnAnimationCounter;
            public bool UnSpawnAdmissionPending;
            public bool UnSpawnPacketFlushed;
            public uint UnSpawnWriterTick;
            public bool UnSpawnActionActive;
            public byte UnSpawnPhase;
            public byte UnSpawnCounter;
            public bool UnSpawnRemovalPending;
            public uint UnSpawnRemovalDueTick;
            public bool BehaviorAdmissionPending;
            public uint EntityUpdateAdmissionTick;
            public int FixedX;
            public int FixedY;
            public int FixedZ;
            public int HeadingFixed;
            public int BlingCounter;
            public int BlingState = -1;
            public int BlingPreviousState = -1;
            public int SearchDue;
            public int SearchRepeat;
            public ulong SearchMessageOrder;
            public int FidgetDue;
            public int FidgetRepeat;
            public ulong FidgetMessageOrder;
            public ulong NextOuterMessageOrder;
            public int FollowCounter;
            public int FollowState = -1;
            public int FollowPreviousState = -1;
            public int FollowDue;
            public int FollowRepeat;
            public uint FollowStartedTick;
            public uint FollowMoverAdmissionTick = uint.MaxValue;
            public bool FollowMoverTranslated;
            public bool FollowMoving;
            public bool FollowMoverMovingThisFrame;
            public int FollowMoverMode = 1;
            public int FollowSpeedMod = FollowBaseSpeedMod;
            public int FollowDirectionHeadingFixed;
            public int FollowTargetFixedX;
            public int FollowTargetFixedY;
            public int FollowTargetFixedZ;
            public int FollowPathRequestId = -1;
            public string FollowPathRequestInstanceKey;
            public PathMap FollowPathRequestMap;
            public uint FollowPathRequestTick;
            public uint FollowPathReadyTick;
            public readonly List<(int FixedX, int FixedY)> FollowPathFixed = new List<(int FixedX, int FixedY)>();
            public int FollowPathIndex;
            public uint FollowTargetSnapshotTick = uint.MaxValue;
            public int FollowTargetSnapshotFixedX;
            public int FollowTargetSnapshotFixedY;
            public int FollowTargetSnapshotFixedZ;
            public int FollowTargetSnapshotHeadingFixed;
            public bool FollowTargetSnapshotMovingThisFrame;
            public ushort AnimationId = 100;
            public int AnimationFramesRemaining;
            public byte AnimationState = 1;
            public bool RetrieveActive;
            public bool RetrievePending;
            public ushort RetrievePendingItemEntityId;
            public int RetrieveState = -1;
            public ushort RetrieveItemEntityId;
            public int RetrieveTargetFixedX;
            public int RetrieveTargetFixedY;
            public int RetrieveTargetFixedZ;
            public int RetrieveTimer;
            public bool RetrieveItemAcquired;
            public int RetrieveCounter;
            public int RetrievePreviousState = -1;
            public byte BehaviorSessionCounter;
            public int RetrievePathRequestId = -1;
            public string RetrievePathRequestInstanceKey;
            public PathMap RetrievePathRequestMap;
            public uint RetrievePathRequestTick;
            public uint RetrievePathReadyTick;
            public readonly List<(int FixedX, int FixedY)> RetrievePathFixed = new List<(int FixedX, int FixedY)>();
            public int RetrievePathIndex;
            public bool ConvertAdmissionPending;
            public bool ConvertRequested;
            public bool ConvertActive;
            public int ConvertState = -1;
            public int ConvertActionTimer;
            public int ConvertCounter;
            public int ConvertOriginalItemCount;
            public bool ConvertAnimationComplete;
            public bool ConvertItemsComplete;
            public uint ConvertStartedTick;
            public uint ConvertStateAdvanceDueTick;
            public uint ConvertAnimationDueTick;
            public uint ConvertAnimationCompletionTick;
            public uint ConvertItemsCompletionTick;
            public int ConvertCenterFixedX;
            public int ConvertCenterFixedY;
            public int ConvertRadiusFixed;
            public int ConvertRatioFixed;
            public readonly List<ushort> ItemAcquisitions = new List<ushort>();
            public readonly List<ConversionItemState> ConversionItems = new List<ConversionItemState>();
        }

        private const int Fixed8 = 0x100;
        private const int BlingSearchInitial = 30;
        private const int BlingSearchRepeat = 30;
        private const int BlingRetrieveSearchInitial = 15;
        private const int BlingRetrieveSearchRepeat = 15;
        private const int BlingFidgetBaseTicks = 2 * 30;
        private const int BlingFidgetVariableSeconds = 7;
        private const int FollowMessageRepeat = 10;
        internal const ushort FollowBaseSpeedMod = 125;
        private const int FollowMoverModeIdle = 1;
        private const int FollowMoverModePoint = 2;
        private const int FollowMoverModeDirection = 3;
        private const int FollowNearDistanceSqFixed = 0x38400;
        private const int FollowTooCloseDistanceSqFixed = 0x6400;
        private const int FollowSpeedF32 = 50 * Fixed8;
        private const int FollowTurnRateFixed = (720 << 8) / 30;
        private const int SearchRadiusFixed = 250 * Fixed8;
        private const int RetrieveStateDelayTicks = 9;
        private const int RetrieveFlipTicks = 7;
        private const int ConvertBounceTicks = 15;
        private const int ConvertFinalBounceTicks = 7;
        private const int ConvertAnimationTailTicks = 14;
        private const ushort SpawnAnimationId = 180;
        private const byte SpawnAnimationTicks = 24;

        private static readonly BlingGnomeLockstepRuntime _instance = new BlingGnomeLockstepRuntime();
        public static BlingGnomeLockstepRuntime Instance => _instance;

        private readonly Dictionary<uint, State> _states = new Dictionary<uint, State>();
        private readonly Dictionary<ushort, ItemAcquisition> _pendingItemAcquisitions = new Dictionary<ushort, ItemAcquisition>();
        private readonly Dictionary<ushort, uint> _retrieveItemOwners = new Dictionary<ushort, uint>();
        private readonly Dictionary<ushort, uint> _conversionItemOwners = new Dictionary<ushort, uint>();

        public bool TryGetSnapshot(uint entityId, out BlingGnomeLockstepSnapshot snapshot)
        {
            if (_states.TryGetValue(entityId, out State state))
            {
                ConversionItemState firstConversion = state.ConversionItems.Count == 0 ? null : state.ConversionItems[0];
                snapshot = new BlingGnomeLockstepSnapshot(
                    state.EntityId,
                    state.BehaviorId,
                    state.InstanceKey,
                    state.Initialized,
                    state.FollowStarted,
                    state.SpawnAnimationActionActive,
                    state.SpawnAnimationPhase,
                    state.SpawnAnimationCounter,
                    state.UnSpawnAdmissionPending,
                    state.UnSpawnPacketFlushed,
                    state.UnSpawnWriterTick,
                    state.UnSpawnActionActive,
                    state.UnSpawnPhase,
                    state.UnSpawnCounter,
                    state.UnSpawnRemovalPending,
                    state.UnSpawnRemovalDueTick,
                    state.FixedX,
                    state.FixedY,
                    state.FixedZ,
                    state.HeadingFixed,
                    state.BlingCounter,
                    state.BlingState,
                    state.BlingPreviousState,
                    state.SearchDue,
                    state.SearchRepeat,
                    state.SearchMessageOrder,
                    state.FidgetDue,
                    state.FidgetRepeat,
                    state.FidgetMessageOrder,
                    state.FollowCounter,
                    state.FollowState,
                    state.FollowPreviousState,
                    state.FollowDue,
                    state.FollowRepeat,
                    state.FollowMoving,
                    state.FollowMoverMovingThisFrame,
                    state.FollowMoverMode,
                    state.FollowSpeedMod,
                    state.FollowDirectionHeadingFixed,
                    state.FollowTargetFixedX,
                    state.FollowTargetFixedY,
                    state.FollowTargetFixedZ,
                    state.AnimationId,
                    state.AnimationFramesRemaining,
                    state.AnimationState,
                    state.RetrieveActive,
                    state.RetrievePending,
                    state.RetrievePendingItemEntityId,
                    state.RetrieveState,
                    state.RetrieveItemEntityId,
                    state.RetrieveTimer,
                    state.RetrieveTargetFixedX,
                    state.RetrieveTargetFixedY,
                    state.RetrieveTargetFixedZ,
                    state.RetrievePathRequestId,
                    state.RetrievePathRequestTick,
                    state.RetrievePathReadyTick,
                    state.RetrievePathFixed.Count,
                    state.RetrievePathIndex,
                    state.ConvertAdmissionPending,
                    state.ConvertRequested,
                    state.ConvertActive,
                    state.ConvertState,
                    state.ConvertActionTimer,
                    state.ConvertAnimationComplete,
                    state.ConvertItemsComplete,
                    state.ConvertStartedTick,
                    state.ConvertStateAdvanceDueTick,
                    state.ConvertAnimationDueTick,
                    state.ConvertAnimationCompletionTick,
                    state.ConvertItemsCompletionTick,
                    state.ConversionItems.Count,
                    firstConversion?.EntityId ?? 0,
                    firstConversion?.Phase ?? 0,
                    firstConversion?.BounceTimer ?? 0,
                    new BlingGnomeActionSnapshot
                    {
                        SessionCounter = state.BehaviorSessionCounter,
                        RetrieveCounter = state.RetrieveCounter,
                        RetrievePreviousState = state.RetrievePreviousState,
                        ConvertCounter = state.ConvertCounter,
                        ConvertOriginalItemCount = state.ConvertOriginalItemCount,
                        ConvertCenterFixedX = state.ConvertCenterFixedX,
                        ConvertCenterFixedY = state.ConvertCenterFixedY,
                        ConvertRadiusFixed = state.ConvertRadiusFixed,
                        ConvertRatioFixed = state.ConvertRatioFixed
                    });
                return true;
            }
            snapshot = default;
            return false;
        }

        public bool IsEntity(uint entityId)
        {
            return _states.ContainsKey(entityId);
        }

        public void Unregister(uint entityId)
        {
            if (!_states.Remove(entityId, out State state))
                return;
            ClearFollowPath(state);
            ClearRetrievePath(state);
            if (state.RetrieveItemEntityId != 0)
                _retrieveItemOwners.Remove(state.RetrieveItemEntityId);
            for (int index = 0; index < state.ConversionItems.Count; index++)
                _conversionItemOwners.Remove(state.ConversionItems[index].EntityId);
            for (int index = 0; index < state.ItemAcquisitions.Count; index++)
                _pendingItemAcquisitions.Remove(state.ItemAcquisitions[index]);
            state.ItemAcquisitions.Clear();
            CombatRuntime.Instance.UnregisterBlingGnomeEntity(entityId);
        }

        public bool StartConvertItemsToGold(GameServer server, RRConnection owner, uint entityId, int centerFixedX, int centerFixedY, int radiusFixed, int conversionRatioFixed)
        {
            if (server == null || !CanStartConvertItemsToGold(owner, entityId))
                return false;
            State state = _states[entityId];
            state.ConvertAdmissionPending = true;
            state.ConvertCenterFixedX = centerFixedX;
            state.ConvertCenterFixedY = centerFixedY;
            state.ConvertRadiusFixed = radiusFixed;
            state.ConvertRatioFixed = conversionRatioFixed;
            Debug.LogError($"[GNOME-CONVERT] entity={entityId} event=writer-admission-pending center=({centerFixedX},{centerFixedY}) radiusFixed={radiusFixed} ratioFixed8={conversionRatioFixed} sourceFunction=ConvertItemsToGold::readData@0x00524A70");
            return true;
        }

        public bool StartLocalConvertItemsToGold(GameServer server, RRConnection owner, uint entityId,
            int centerFixedX, int centerFixedY, int radiusFixed, int ratioFixed, uint simulationTick)
        {
            if (server == null || !CanStartConvertItemsToGold(owner, entityId))
                return false;
            State state = _states[entityId];
            state.ConvertRequested = true;
            state.ConvertCenterFixedX = centerFixedX;
            state.ConvertCenterFixedY = centerFixedY;
            state.ConvertRadiusFixed = radiusFixed;
            state.ConvertRatioFixed = ratioFixed;
            TickConversion(server, state, CombatRuntime.Instance.GetRoomRngForInstance(state.InstanceKey), simulationTick);
            return true;
        }

        public bool CanStartConvertItemsToGold(RRConnection owner, uint entityId)
        {
            return owner != null
                && _states.TryGetValue(entityId, out State state)
                && state.Owner == owner
                && state.Initialized
                && string.Equals(state.InstanceKey, RoomRuntime.NormalizeInstanceKey(owner.RuntimeInstanceKey), StringComparison.OrdinalIgnoreCase)
                && !state.ConvertAdmissionPending
                && !state.ConvertRequested
                && !state.ConvertActive
                && !state.SpawnAnimationActionActive
                && !state.UnSpawnAdmissionPending
                && !state.UnSpawnActionActive
                && !state.UnSpawnRemovalPending;
        }

        public bool RequestUnSpawn(uint entityId)
        {
            if (!_states.TryGetValue(entityId, out State state) || !state.Initialized || state.UnSpawnAdmissionPending || state.UnSpawnActionActive)
                return false;
            state.UnSpawnAdmissionPending = true;
            state.UnSpawnPacketFlushed = false;
            state.UnSpawnWriterTick = 0;
            state.UnSpawnPhase = 0;
            state.UnSpawnCounter = 0;
            return true;
        }

        public void MarkUnSpawnPacketFlushed(uint entityId, uint packetWriterTick)
        {
            if (!_states.TryGetValue(entityId, out State state) || !state.UnSpawnAdmissionPending)
                return;
            state.UnSpawnPacketFlushed = true;
            state.UnSpawnWriterTick = packetWriterTick;
        }

        public void RegisterSpawned(RRConnection conn, uint entityId, int fixedX, int fixedY, int fixedZ, int headingFixed, uint admissionTick)
        {
            if (conn == null || entityId == 0 || _states.ContainsKey(entityId))
                return;
            var state = new State
            {
                Owner = conn,
                EntityId = entityId,
                InstanceKey = RoomRuntime.NormalizeInstanceKey(conn.RuntimeInstanceKey),
                FixedX = fixedX,
                FixedY = fixedY,
                FixedZ = fixedZ,
                HeadingFixed = 0,
                EntityUpdateAdmissionTick = admissionTick
            };
            _states.Add(entityId, state);
            CombatRuntime.Instance.RegisterBlingGnomeEntity(entityId, admissionTick);
            Debug.LogError($"[RNG-BLING] register entity={entityId} owner={conn.LoginName} instance='{state.InstanceKey}' fixed=({state.FixedX},{state.FixedY},{state.FixedZ}) sourceFunction=ClientEntityManager::processComponentUpdate@0x005DB520");
        }

        public void OnBehaviorCreated(RRConnection conn, uint entityId, ushort behaviorId, uint admissionTick)
        {
            if (!_states.TryGetValue(entityId, out State state))
            {
                if (conn == null || !BlingGnomeRuntime.Instance.TryGetGnomeFixedSnapshot(conn.ConnId, out uint snapshotEntityId, out int fixedX, out int fixedY, out int fixedZ, out int headingFixed) || snapshotEntityId != entityId)
                    return;
                RegisterSpawned(conn, entityId, fixedX, fixedY, fixedZ, headingFixed, admissionTick);
                if (!_states.TryGetValue(entityId, out state))
                    return;
            }

            state.BehaviorId = behaviorId;
            state.EntityUpdateAdmissionTick = admissionTick;
            state.BehaviorAdmissionPending = !state.Initialized;
        }

        public void CommitEntityWriterAdmissions(uint simulationTick)
        {
            foreach (uint entityId in CombatRuntime.Instance.GetEntityOrderSnapshot())
            {
                if (!_states.TryGetValue(entityId, out State state))
                    continue;
                if (state.BehaviorAdmissionPending && simulationTick >= state.EntityUpdateAdmissionTick)
                {
                    MersenneTwister rng = CombatRuntime.Instance.GetRoomRngForInstance(state.InstanceKey);
                    if (rng == null)
                        throw new InvalidOperationException($"Bling root {entityId} has no room RNG at writer admission tick {simulationTick}");
                    state.Initialized = true;
                    state.SpawnAnimationActionActive = true;
                    state.SpawnAnimationPhase = 0;
                    state.SpawnAnimationCounter = 0;
                    state.BehaviorAdmissionPending = false;
                    Debug.LogError($"[RNG-BLING] entity={entityId} tick={simulationTick} event=spawn-animation-action-active animation={SpawnAnimationId} length={SpawnAnimationTicks} sourceFunction=SpawnAnimation::start@0x0052FA10->SpawnAnimation::update@0x0052FA30");
                }
                if (state.UnSpawnAdmissionPending
                    && state.UnSpawnPacketFlushed
                    && state.Initialized
                    && simulationTick >= state.UnSpawnWriterTick)
                {
                    BeginUnSpawn(state, simulationTick);
                }
                if (state.UnSpawnActionActive)
                    continue;
                if (!state.ConvertAdmissionPending || !state.Initialized)
                    continue;
                state.ConvertAdmissionPending = false;
                state.ConvertRequested = true;
                Debug.LogError($"[GNOME-CONVERT] entity={entityId} tick={simulationTick} event=writer-admitted center=({state.ConvertCenterFixedX},{state.ConvertCenterFixedY}) radiusFixed={state.ConvertRadiusFixed} ratioFixed8={state.ConvertRatioFixed} sourceFunction=ClientEntityManager::update@0x005DA300->Behavior::doInterruptLocal@0x00515290");
            }
        }

        public void Sync(GameServer server, IEnumerable<RRConnection> connections)
        {
            var live = new HashSet<uint>();
            foreach (RRConnection conn in connections ?? Enumerable.Empty<RRConnection>())
            {
                if (conn == null || !conn.IsConnected || !conn.IsSpawned || !conn.AllowFlush || !conn.TickUpdatesActive)
                    continue;
                if (!BlingGnomeRuntime.Instance.TryGetGnomeFixedSnapshot(conn.ConnId, out uint entityId, out _, out _, out _, out _))
                    continue;
                live.Add(entityId);
            }

            List<uint> entityOrder = CombatRuntime.Instance.GetEntityOrderSnapshot();
            for (int i = 0; i < entityOrder.Count; i++)
            {
                uint entityId = entityOrder[i];
                if (!_states.ContainsKey(entityId) || live.Contains(entityId))
                    continue;
                Unregister(entityId);
                Debug.LogError($"[RNG-BLING] unregister entity={entityId}");
            }
        }

        public void TickEntity(GameServer server, uint entityId, uint simulationTick)
        {
            if (!_states.TryGetValue(entityId, out State state) || state.Owner == null)
                return;
            if (!state.Owner.IsConnected || !state.Owner.IsSpawned || !state.Owner.AllowFlush || !state.Owner.TickUpdatesActive)
                return;
            if (!string.Equals(RoomRuntime.NormalizeInstanceKey(state.Owner.RuntimeInstanceKey), state.InstanceKey, StringComparison.OrdinalIgnoreCase))
                return;
            if (!BlingGnomeRuntime.Instance.TryResolveGnomeTarget(state.Owner, (ushort)entityId, out _, out ushort behaviorId, out bool behaviorCreated, out _))
                return;
            state.BehaviorId = behaviorId;
            if (!behaviorCreated)
                return;
            if (state.BehaviorAdmissionPending)
                return;
            if (simulationTick < state.EntityUpdateAdmissionTick)
                return;

            if (state.UnSpawnRemovalPending)
            {
                if (simulationTick >= state.UnSpawnRemovalDueTick)
                    BlingGnomeRuntime.Instance.CompleteUnSpawn(state.Owner, state.EntityId);
                return;
            }

            MersenneTwister rng = CombatRuntime.Instance.GetRoomRngForInstance(state.InstanceKey);
            if (state.UnSpawnActionActive)
            {
                bool complete = AdvanceUnSpawnLifecycle(state);
                if (complete)
                {
                    if (rng != null)
                    {
                        StartFollowAfterUnSpawn(state, rng, simulationTick);
                        state.FollowStarted = true;
                    }
                    state.UnSpawnActionActive = false;
                    state.UnSpawnRemovalPending = true;
                    state.UnSpawnRemovalDueTick = simulationTick + 4u;
                    return;
                }
                BlingGnomeRuntime.Instance.AdvanceGnomeUnit(state.EntityId);
                return;
            }

            if (rng == null)
            {
                Debug.LogError($"[RNG-BLING] blocked entity={entityId} reason=missing-room-rng instance='{state.InstanceKey}' tick={simulationTick}");
                BlingGnomeRuntime.Instance.AdvanceGnomeUnit(state.EntityId);
                return;
            }

            if (state.SpawnAnimationActionActive)
            {
                if (state.SpawnAnimationPhase == 1 && state.SpawnAnimationCounter == 1)
                    Initialize(state, rng, simulationTick);
                if (AdvanceSpawnAnimationLifecycle(state))
                {
                    state.BehaviorSessionCounter++;
                    StartFollow(state, rng, simulationTick, "SpawnAnimation::terminate->BlingGnomeBehavior::DoIdleAction-state5-enter");
                    state.FollowStarted = true;
                }
                else
                {
                    BlingGnomeRuntime.Instance.AdvanceGnomeUnit(state.EntityId);
                    return;
                }
            }
            AdvanceAnimation(state);
            if (state.ConvertRequested || state.ConvertActive)
                TickConversion(server, state, rng, simulationTick);
            if (state.RetrieveActive)
                TickRetrieve(server, state, simulationTick);
            else if (!state.ConvertRequested && !state.ConvertActive && state.FollowStartedTick != simulationTick)
                TickFollow(state, rng, simulationTick);
            state.BlingCounter++;
            bool searchDue = state.BlingCounter == state.SearchDue;
            bool fidgetDue = state.BlingCounter == state.FidgetDue;
            if (searchDue && fidgetDue)
            {
                if (state.SearchMessageOrder < state.FidgetMessageOrder)
                {
                    DispatchSearch(server, state, rng, simulationTick);
                    DispatchFidget(state, rng, simulationTick);
                }
                else
                {
                    DispatchFidget(state, rng, simulationTick);
                    DispatchSearch(server, state, rng, simulationTick);
                }
            }
            else if (searchDue)
            {
                DispatchSearch(server, state, rng, simulationTick);
            }
            else if (fidgetDue)
            {
                DispatchFidget(state, rng, simulationTick);
            }
            BlingGnomeRuntime.Instance.AdvanceGnomeUnit(state.EntityId);
        }

        public void CaptureFollowTargetStates(uint simulationTick)
        {
            List<uint> entityOrder = CombatRuntime.Instance.GetEntityOrderSnapshot();
            for (int index = 0; index < entityOrder.Count; index++)
            {
                if (!_states.TryGetValue(entityOrder[index], out State state))
                    continue;
                RRConnection owner = state.Owner;
                if (owner == null)
                    continue;
                state.FollowTargetSnapshotTick = simulationTick;
                state.FollowTargetSnapshotFixedX = OwnerFixedX(owner);
                state.FollowTargetSnapshotFixedY = OwnerFixedY(owner);
                state.FollowTargetSnapshotFixedZ = OwnerFixedZ(owner);
                state.FollowTargetSnapshotHeadingFixed = OwnerHeadingFixed(owner);
                state.FollowTargetSnapshotMovingThisFrame = OwnerMovingThisFrame(owner);
            }
        }

        public void CaptureFollowTargetStateForOwnerEntity(uint ownerEntityId, uint simulationTick)
        {
            List<uint> entityOrder = CombatRuntime.Instance.GetEntityOrderSnapshot();
            for (int index = 0; index < entityOrder.Count; index++)
            {
                if (!_states.TryGetValue(entityOrder[index], out State state))
                    continue;
                RRConnection owner = state.Owner;
                if (owner?.Avatar == null || (uint)owner.Avatar.Id != ownerEntityId)
                    continue;
                state.FollowTargetSnapshotTick = simulationTick;
                state.FollowTargetSnapshotFixedX = OwnerFixedX(owner);
                state.FollowTargetSnapshotFixedY = OwnerFixedY(owner);
                state.FollowTargetSnapshotFixedZ = OwnerFixedZ(owner);
                state.FollowTargetSnapshotHeadingFixed = OwnerHeadingFixed(owner);
                state.FollowTargetSnapshotMovingThisFrame = OwnerMovingThisFrame(owner);
            }
        }

        private static void BeginUnSpawn(State state, uint simulationTick)
        {
            state.UnSpawnAdmissionPending = false;
            state.UnSpawnPacketFlushed = false;
            state.UnSpawnActionActive = true;
            state.UnSpawnRemovalPending = false;
            state.UnSpawnRemovalDueTick = 0;
            state.UnSpawnPhase = 0;
            state.UnSpawnCounter = 0;
            ClearRetrievePath(state);
            if (state.RetrieveItemEntityId != 0)
                _instance._retrieveItemOwners.Remove(state.RetrieveItemEntityId);
            state.RetrieveActive = false;
            state.RetrievePending = false;
            state.RetrievePendingItemEntityId = 0;
            state.RetrieveState = -1;
            state.RetrieveItemEntityId = 0;
            for (int conversionIndex = 0; conversionIndex < state.ConversionItems.Count; conversionIndex++)
                _instance._conversionItemOwners.Remove(state.ConversionItems[conversionIndex].EntityId);
            state.ConversionItems.Clear();
            state.ConvertAdmissionPending = false;
            state.ConvertRequested = false;
            state.ConvertActive = false;
            state.ConvertState = -1;
            StopFollowMover(state);
            state.FollowStarted = false;
            state.FollowState = -1;
            AdvanceUnSpawnLifecycle(state);
            Debug.LogError($"[GNOME-UNSPAWN] entity={state.EntityId} event=writer-admitted tick={simulationTick} phase={state.UnSpawnPhase} counter={state.UnSpawnCounter} sourceFunction=ClientEntityManager::processMessage@0x005DA460->Behavior::doInterruptLocal@0x00515290->UnSpawn::update@0x00530B00");
        }

        private static bool AdvanceUnSpawnLifecycle(State state)
        {
            if (state.UnSpawnPhase == 0)
            {
                state.AnimationId = 100;
                state.AnimationState = 1;
                state.AnimationFramesRemaining = 0;
                state.UnSpawnCounter = 30;
                state.UnSpawnPhase = 1;
            }
            if (state.UnSpawnPhase == 1)
            {
                if (state.UnSpawnCounter != 0)
                    state.UnSpawnCounter--;
                if (state.UnSpawnCounter == 0)
                    state.UnSpawnPhase = 100;
            }
            return state.UnSpawnPhase == 100;
        }

        public void TickItemObject(GameServer server, uint entityId, uint simulationTick)
        {
            if (server == null || entityId == 0 || entityId > ushort.MaxValue)
                return;
            ushort itemEntityId = (ushort)entityId;
            if (_pendingItemAcquisitions.TryGetValue(itemEntityId, out ItemAcquisition acquisition))
            {
                State retrieveOwner = acquisition.Owner;
                if (!_states.TryGetValue(retrieveOwner.EntityId, out State registered)
                    || !ReferenceEquals(registered, retrieveOwner)
                    || !string.Equals(RoomRuntime.NormalizeInstanceKey(retrieveOwner.Owner.RuntimeInstanceKey), retrieveOwner.InstanceKey, StringComparison.OrdinalIgnoreCase)
                    || !server.TryGetBlingDroppedItem(itemEntityId, out GameServer.DroppedItemInfo item))
                {
                    _pendingItemAcquisitions.Remove(itemEntityId);
                    retrieveOwner.ItemAcquisitions.Remove(itemEntityId);
                }
                else if (acquisition.LastTick != simulationTick)
                {
                    acquisition.LastTick = simulationTick;
                    if (acquisition.Timer > 0)
                        acquisition.Timer--;
                    server.SetDroppedItemLifecycle(itemEntityId, 2, unchecked((ushort)acquisition.Timer), 0, simulationTick);
                    if (acquisition.Timer == 0)
                    {
                        bool pickedUp = server.BlingGnomeAcquireItemAsGold(retrieveOwner.Owner, itemEntityId, item.GoldAmount);
                        Debug.LogError($"[GNOME-RETRIEVE] entity={retrieveOwner.EntityId} item={itemEntityId} tick={simulationTick} state=4->complete acquired={pickedUp} gold={item.GoldAmount} sourceFunction=ItemObject::updateState@0x0058A410->ItemObject::AcquireItem@0x00589E70");
                        _pendingItemAcquisitions.Remove(itemEntityId);
                        retrieveOwner.ItemAcquisitions.Remove(itemEntityId);
                        _retrieveItemOwners.Remove(itemEntityId);
                        if (retrieveOwner.RetrieveItemEntityId == itemEntityId)
                            retrieveOwner.RetrieveItemAcquired = pickedUp;
                    }
                }
            }
            else
            {
                server.AdvanceDroppedItemLifecycle(itemEntityId, simulationTick);
            }

            if (!_conversionItemOwners.TryGetValue(itemEntityId, out uint conversionOwnerId)
                || !_states.TryGetValue(conversionOwnerId, out State conversionOwner)
                || !conversionOwner.ConvertActive)
                return;
            int conversionIndex = -1;
            for (int index = 0; index < conversionOwner.ConversionItems.Count; index++)
            {
                if (conversionOwner.ConversionItems[index].EntityId == itemEntityId)
                {
                    conversionIndex = index;
                    break;
                }
            }
            if (conversionIndex < 0)
            {
                _conversionItemOwners.Remove(itemEntityId);
                return;
            }
            ConversionItemState conversion = conversionOwner.ConversionItems[conversionIndex];
            if (conversion.BounceTimer > 0)
                conversion.BounceTimer--;
            server.SetDroppedItemLifecycle(
                conversion.EntityId,
                5,
                unchecked((ushort)conversion.BounceTimer),
                unchecked((byte)conversion.Phase),
                simulationTick);
            if (conversion.BounceTimer != 0)
                return;
            if (conversion.Phase < 3)
            {
                conversion.Phase++;
                conversion.BounceTimer = ConvertBounceTicks;
                server.SetDroppedItemLifecycle(conversion.EntityId, 5, ConvertBounceTicks, unchecked((byte)conversion.Phase), simulationTick);
                Debug.LogError($"[GNOME-CONVERT-ITEM] entity={conversionOwner.EntityId} item={conversion.EntityId} tick={simulationTick} phase={conversion.Phase} timer={ConvertBounceTicks} sourceFunction=ItemObject::updateState@0x0058A410->ItemObject::initState@0x0058A1E0");
                return;
            }
            if (conversion.Phase == 3)
            {
                conversion.Phase = 4;
                conversion.BounceTimer = ConvertFinalBounceTicks;
                server.SetDroppedItemLifecycle(conversion.EntityId, 5, ConvertFinalBounceTicks, 4, simulationTick);
                Debug.LogError($"[GNOME-CONVERT-ITEM] entity={conversionOwner.EntityId} item={conversion.EntityId} tick={simulationTick} phase=4 timer={ConvertFinalBounceTicks} sourceFunction=ItemObject::updateState@0x0058A410->ItemObject::initState@0x0058A1E0");
                return;
            }
            bool converted = server.BlingGnomeAcquireItemAsGold(conversionOwner.Owner, conversion.EntityId, conversion.GoldAmount, conversionOwner.ConvertRatioFixed);
            if (converted)
            {
                BlingGnomeRuntime.Instance.RecordConvertedItem(conversionOwner.Owner, conversionOwner.EntityId, conversion.EntityId, conversion.GoldAmount, conversion.BonusEligible);
            }
            _conversionItemOwners.Remove(conversion.EntityId);
            conversionOwner.ConversionItems.RemoveAt(conversionIndex);
            Debug.LogError($"[GNOME-CONVERT-ITEM] entity={conversionOwner.EntityId} item={conversion.EntityId} tick={simulationTick} phase=complete removed={converted} gold={conversion.GoldAmount} sourceFunction=ItemObject::updateState@0x0058A410");
        }

        private static bool AdvanceSpawnAnimationLifecycle(State state)
        {
            if (state.SpawnAnimationPhase == 0)
            {
                state.AnimationId = SpawnAnimationId;
                state.AnimationState = 0;
                state.AnimationFramesRemaining = SpawnAnimationTicks;
                state.SpawnAnimationPhase = 1;
                state.SpawnAnimationCounter = SpawnAnimationTicks;
            }
            if (state.SpawnAnimationPhase != 1)
                return false;
            if (state.SpawnAnimationCounter != 0)
                state.SpawnAnimationCounter--;
            if (state.AnimationFramesRemaining > 0)
                state.AnimationFramesRemaining--;
            if (state.SpawnAnimationCounter != 0)
                return false;
            state.SpawnAnimationPhase = 100;
            state.SpawnAnimationActionActive = false;
            state.AnimationId = 100;
            state.AnimationState = 1;
            state.AnimationFramesRemaining = 0;
            return true;
        }

        private static void AdvanceAnimation(State state)
        {
            if (state.AnimationState != 8)
                return;
            if (state.AnimationFramesRemaining > 0)
                state.AnimationFramesRemaining--;
            if (state.AnimationFramesRemaining != 0)
                return;
            if (state.FollowMoving && state.FollowMoverTranslated)
                SetMovementAnimation(state);
            else
                SetIdleAnimation(state);
        }

        private static void Initialize(State state, MersenneTwister rng, uint simulationTick)
        {
            int beforeIdle = rng.CallsSinceReseed;
            uint idleRaw = RngLedger.Generate(rng, "room", "BlingGnomeBehavior::States:state0-idle-delay", $"{state.InstanceKey}:entity={state.EntityId}");
            int fidgetSeconds = 2 + (int)(idleRaw % BlingFidgetVariableSeconds);
            state.BlingCounter = 0;
            state.BlingPreviousState = 0;
            state.BlingState = 5;
            state.SearchDue = BlingSearchInitial + 1;
            state.SearchRepeat = BlingSearchRepeat;
            state.SearchMessageOrder = ++state.NextOuterMessageOrder;
            state.FidgetRepeat = fidgetSeconds * 30;
            state.FidgetDue = state.FidgetRepeat + 1;
            state.FidgetMessageOrder = ++state.NextOuterMessageOrder;
            state.FollowCounter = 0;
            state.FollowPreviousState = -1;
            state.Initialized = true;
            Debug.LogError($"[RNG-BLING] entity={state.EntityId} tick={simulationTick} event=state0-idle stream=room streamInstance='{state.InstanceKey}' raw=0x{idleRaw:X8} pos={beforeIdle}->{rng.CallsSinceReseed} base=2 variable=7 seconds={fidgetSeconds} fidgetDue={state.FidgetDue} sourceFunction=BlingGnomeBehavior::States@0x00516B33");
        }

        private static void StartFollow(State state, MersenneTwister rng, uint simulationTick, string source)
        {
            state.FollowCounter = 0;
            int ownerX = OwnerFixedX(state);
            int ownerY = OwnerFixedY(state);
            int distanceSqFixed = DistanceSqFixed8(state.FixedX, state.FixedY, ownerX, ownerY);
            state.FollowPreviousState = -1;
            state.FollowState = -1;
            if (distanceSqFixed < FollowNearDistanceSqFixed && !OwnerMovingThisFrame(state))
                EnterIdle(state);
            else
                EnterMoving(state, ownerX, ownerY);
            int before = rng.CallsSinceReseed;
            uint raw = RngLedger.Generate(rng, "room", "BlingGnome:Follow::start", $"{state.InstanceKey}:entity={state.EntityId}");
            state.FollowDue = (int)(raw % 10u) + 1;
            state.FollowRepeat = FollowMessageRepeat;
            state.FollowStartedTick = simulationTick;
            Debug.LogError($"[RNG-BLING] entity={state.EntityId} tick={simulationTick} event=follow-start raw=0x{raw:X8} pos={before}->{rng.CallsSinceReseed} distanceSqFixed={distanceSqFixed} state={state.FollowState} due={state.FollowDue} repeat=10 source='{source}' sourceFunction=Follow::start@0x005270B5");
        }

        private static void StartFollowAfterUnSpawn(State state, MersenneTwister rng, uint simulationTick)
        {
            StartFollow(state, rng, simulationTick, "UnSpawn::terminate->BlingGnomeBehavior::DoIdleAction-state5-enter");
        }

        private static void TickFollow(State state, MersenneTwister rng, uint simulationTick)
        {
            state.FollowCounter++;
            if (state.FollowCounter == state.FollowDue)
            {
                state.FollowDue += state.FollowRepeat;
                DeliverFollowMessage(state, rng, simulationTick);
            }
            MoveFollow(state, simulationTick);
        }

        private static void DeliverFollowMessage(State state, MersenneTwister rng, uint simulationTick)
        {
            int ownerX = OwnerFixedX(state);
            int ownerY = OwnerFixedY(state);
            int distanceSqFixed = DistanceSqFixed8(state.FixedX, state.FixedY, ownerX, ownerY);
            if (SimulationParityLedger.Active)
            {
                RRConnection owner = state.Owner;
                CombatPlayer ownerPlayer = owner?.Avatar != null
                    ? CombatRuntime.Instance.GetPlayer((uint)owner.Avatar.Id)
                    : null;
                SimulationParityLedger.EnqueueBlingFollowDue(new SimulationParityBlingFollowDueRow
                {
                    Tick = simulationTick,
                    GnomeEntityId = state.EntityId,
                    OwnerConnId = owner?.ConnId ?? 0,
                    OwnerEntityId = owner?.Avatar?.Id,
                    FollowCounter = state.FollowCounter,
                    NextFollowDue = state.FollowDue,
                    FollowState = state.FollowState,
                    ReflectedAppliedOrdinal = owner?.ReflectedAvatarMovementAppliedOrdinal ?? 0,
                    ReflectedReceivedOrdinal = owner?.ReflectedAvatarMovementReceivedOrdinal ?? 0,
                    ReflectedX = owner?.ReflectedAvatarPosFixedX ?? 0,
                    ReflectedY = owner?.ReflectedAvatarPosFixedY ?? 0,
                    OwnerAckAppliedOrdinal = owner?.OwnerAckFollowClientAppliedOrdinal ?? 0,
                    OwnerAckReceivedOrdinal = owner?.OwnerAckFollowClientReceivedOrdinal ?? 0,
                    OwnerAckX = owner?.OwnerAckFollowClientPosFixedX ?? 0,
                    OwnerAckY = owner?.OwnerAckFollowClientPosFixedY ?? 0,
                    OwnerAckQueueCount = owner?.OwnerAckFollowClientMovementSamples.Count ?? 0,
                    ClientSimulationX = ownerPlayer?.ClientSimulationPosFixedX ?? 0,
                    ClientSimulationY = ownerPlayer?.ClientSimulationPosFixedY ?? 0,
                    PredictedX = ownerPlayer?.PredictedLocation2DFixedX ?? 0,
                    PredictedY = ownerPlayer?.PredictedLocation2DFixedY ?? 0,
                    PlayerInputX = owner?.PlayerPosFixedX ?? 0,
                    PlayerInputY = owner?.PlayerPosFixedY ?? 0,
                    LiveOwnerX = owner?.LivePlayerPosFixedX ?? 0,
                    LiveOwnerY = owner?.LivePlayerPosFixedY ?? 0,
                    OwnerX = ownerX,
                    OwnerY = ownerY,
                    OwnerMovingThisFrame = OwnerMovingThisFrame(state),
                    GnomeX = state.FixedX,
                    GnomeY = state.FixedY,
                    DistanceSqFixed = distanceSqFixed,
                    ReadyCount = owner?.ReadyReflectedAvatarMovementSamples.Count ?? 0,
                    HeldCount = owner?.HeldReflectedAvatarMovementSamples.Count ?? 0,
                    AdmittedCount = owner?.AdmittedReflectedAvatarMovementSamples.Count ?? 0,
                    PendingCount = owner?.PendingReflectedAvatarMovementSamples.Count ?? 0,
                    StreamAdmitted = owner?.ReflectedAvatarMovementStreamAdmitted ?? false,
                    TerminalRestartPending = owner?.ReflectedAvatarMovementTerminalRestartPending ?? false,
                    PostAdmissionBarrierPending = owner?.ReflectedAvatarMovementPostAdmissionBarrierPending ?? false,
                    IdleBatchBarrierPending = owner?.ReflectedAvatarMovementIdleBatchBarrierPending ?? false
                });
            }
            if (state.FollowState == 5)
            {
                if (distanceSqFixed < FollowTooCloseDistanceSqFixed)
                    EnterReposition(state, rng, simulationTick, ownerX, ownerY);
                else if (distanceSqFixed > FollowNearDistanceSqFixed)
                    EnterMoving(state, ownerX, ownerY);
            }
            else if (state.FollowState == 2)
            {
                if (distanceSqFixed < FollowNearDistanceSqFixed && !OwnerMovingThisFrame(state))
                    EnterIdle(state);
                else
                {
                    MatchSpeed(state, distanceSqFixed);
                    if (!TryMoveTowardOwner(state, ownerX, ownerY))
                        EnterFindLineOfSight(state, ownerX, ownerY, distanceSqFixed, simulationTick);
                }
            }
            else if (state.FollowState == 0x0F)
            {
                if (distanceSqFixed < FollowNearDistanceSqFixed && !OwnerMovingThisFrame(state))
                    EnterIdle(state);
                else
                {
                    MatchSpeed(state, distanceSqFixed);
                    if (TryMoveTowardOwner(state, ownerX, ownerY))
                    {
                        state.FollowPreviousState = state.FollowState;
                        state.FollowState = 2;
                    }
                    else if (!state.FollowMoving)
                    {
                        MoveToOwnerPoint(state, ownerX, ownerY, simulationTick);
                    }
                }
            }
            else if (state.FollowState == 8)
            {
                if (distanceSqFixed > FollowNearDistanceSqFixed)
                    EnterMoving(state, ownerX, ownerY);
                else if (!state.FollowMoving)
                    EnterIdle(state);
            }
        }

        private static void EnterIdle(State state)
        {
            state.FollowPreviousState = state.FollowState;
            state.FollowState = 5;
            state.FollowSpeedMod = FollowBaseSpeedMod;
            StopFollowMover(state);
        }

        private static void EnterMoving(State state, int ownerX, int ownerY)
        {
            state.FollowPreviousState = state.FollowState;
            state.FollowState = 2;
            MatchSpeed(state, DistanceSqFixed8(state.FixedX, state.FixedY, ownerX, ownerY));
            TryMoveTowardOwner(state, ownerX, ownerY);
        }

        private static void EnterFindLineOfSight(State state, int ownerX, int ownerY, int distanceSqFixed, uint simulationTick)
        {
            state.FollowPreviousState = state.FollowState;
            state.FollowState = 0x0F;
            MatchSpeed(state, distanceSqFixed);
            MoveToOwnerPoint(state, ownerX, ownerY, simulationTick);
        }

        private static void EnterReposition(State state, MersenneTwister rng, uint simulationTick, int ownerX, int ownerY)
        {
            int before = rng.CallsSinceReseed;
            uint headingRaw = RngLedger.Generate(rng, "room", "BlingGnome:Follow::PickRandomNearbyPoint-heading", $"{state.InstanceKey}:entity={state.EntityId}");
            int headingDegrees = (int)(headingRaw % 360u);
            PathMap pathMap = ResolvePathMap(state);
            uint radiusRaw = 0;
            int requestedDistanceFixed = 0;
            int actualDistanceFixed = 0;
            int nativeHeadingDegrees = 0;
            int sin = 0;
            int cos = 0;
            int radiusDraws = 0;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                radiusRaw = RngLedger.Generate(rng, "room", "BlingGnome:Follow::PickRandomNearbyPoint-radius", $"{state.InstanceKey}:entity={state.EntityId}:attempt={attempt}");
                radiusDraws++;
                requestedDistanceFixed = (int)(radiusRaw % 20u + 10u) * Fixed8;
                nativeHeadingDegrees = UnitMover.WrapDegrees(360 - headingDegrees);
                sin = UnitMover.ZRotateSinFixed(nativeHeadingDegrees);
                cos = UnitMover.ZRotateCosFixed(nativeHeadingDegrees);
                actualDistanceFixed = pathMap != null
                    ? pathMap.CastGroundRayDistanceFixed(ownerX, ownerY, sin, cos, requestedDistanceFixed)
                    : requestedDistanceFixed;
                if (actualDistanceFixed > 0xA00)
                    break;
                if (headingDegrees < 180)
                    headingDegrees += 180;
                else if (headingDegrees > 180)
                    headingDegrees -= 180;
            }
            int targetX = ownerX + (int)(((long)sin * actualDistanceFixed) >> 8);
            int targetY = ownerY + (int)(((long)cos * actualDistanceFixed) >> 8);
            state.FollowTargetFixedX = targetX;
            state.FollowTargetFixedY = targetY;
            state.FollowTargetFixedZ = state.FixedZ;
            state.FollowPreviousState = state.FollowState;
            state.FollowState = 8;
            state.FollowSpeedMod = FollowBaseSpeedMod;
            state.FollowMoving = true;
            state.FollowMoverMode = FollowMoverModePoint;
            state.FollowMoverAdmissionTick = simulationTick;
            state.FollowMoverTranslated = false;
            BeginFollowPath(state, simulationTick);
            Debug.LogError($"[RNG-BLING] entity={state.EntityId} tick={simulationTick} event=follow-reposition rawHeading=0x{headingRaw:X8} rawRadius=0x{radiusRaw:X8} pos={before}->{rng.CallsSinceReseed} heading={headingDegrees} requestedDistanceFixed={requestedDistanceFixed} actualDistanceFixed={actualDistanceFixed} radiusDraws={radiusDraws} target=({targetX},{targetY}) sourceFunction=Follow::PickRandomNearbyPoint@0x00527D30 draws={rng.CallsSinceReseed - before}");
        }

        private static void ClearFollowPath(State state)
        {
            if (state == null)
                return;
            if (state.FollowPathRequestId > 0 && state.FollowPathRequestMap != null)
            {
                CombatRuntime.Instance.CancelMoveToPointPathRequest(
                    state.FollowPathRequestInstanceKey,
                    state.FollowPathRequestMap,
                    state.FollowPathRequestId);
            }
            state.FollowPathRequestId = -1;
            state.FollowPathRequestInstanceKey = null;
            state.FollowPathRequestMap = null;
            state.FollowPathFixed.Clear();
            state.FollowPathIndex = 0;
        }

        private static void BeginFollowPath(State state, uint simulationTick)
        {
            ClearFollowPath(state);
            PathMap pathMap = ResolvePathMap(state);
            state.FollowPathRequestMap = pathMap;
            state.FollowPathRequestInstanceKey = state.InstanceKey;
            state.FollowPathRequestTick = simulationTick;
            state.FollowPathReadyTick = 0;
            PathNode startNode = pathMap?.GetClosestNodeFixed(state.FixedX, state.FixedY, state.FixedZ);
            int resolvedTargetFixedX = state.FixedX;
            int resolvedTargetFixedY = state.FixedY;
            PathNode goalNode = null;
            if (pathMap != null && startNode != null)
            {
                pathMap.FindValidDestPointFixed(
                    state.FixedX,
                    state.FixedY,
                    state.FollowTargetFixedX,
                    state.FollowTargetFixedY,
                    startNode,
                    0x6400,
                    out resolvedTargetFixedX,
                    out resolvedTargetFixedY,
                    out goalNode);
            }
            if (pathMap == null || startNode == null || goalNode == null)
            {
                state.FollowPathRequestId = -2;
                StopFollowMover(state);
                Debug.LogError($"[GNOME-FOLLOW-PATH] entity={state.EntityId} request=-2 start=({state.FixedX},{state.FixedY}) target=({state.FollowTargetFixedX},{state.FollowTargetFixedY}) tick={simulationTick} result=invalid-node sourceFunction=Follow::EnterReposition@0x00527B90->UnitMover::MoveToPoint@0x00535FB0");
                return;
            }
            var pathfinder = new Pathfinder(pathMap);
            pathfinder.RequestPath(
                state.FixedX,
                state.FixedY,
                startNode,
                resolvedTargetFixedX,
                resolvedTargetFixedY,
                goalNode);
            uint entityId = state.EntityId;
            string instanceKey = state.InstanceKey;
            int targetFixedX = state.FollowTargetFixedX;
            int targetFixedY = state.FollowTargetFixedY;
            int requestId = CombatRuntime.Instance.RequestMoveToPointPathSync(
                instanceKey,
                pathMap,
                pathfinder,
                "bling-gnome-follow",
                entityId,
                targetFixedX,
                targetFixedY,
                candidateRequestId => IsFollowPathRequestCurrent(candidateRequestId, entityId, instanceKey, pathMap, targetFixedX, targetFixedY),
                (candidateRequestId, completedPathfinder, completedTick) => InstallFollowPath(candidateRequestId, entityId, instanceKey, pathMap, targetFixedX, targetFixedY, completedPathfinder, completedTick));
            state.FollowPathRequestId = requestId;
            if (requestId <= 0)
                StopFollowMover(state);
            Debug.LogError($"[GNOME-FOLLOW-PATH] entity={state.EntityId} request={requestId} start=({state.FixedX},{state.FixedY}) target=({targetFixedX},{targetFixedY}) tick={simulationTick} result=queued sourceFunction=Follow::EnterReposition@0x00527B90->UnitMover::MoveToPoint@0x00535FB0");
        }

        private static bool IsFollowPathRequestCurrent(int requestId, uint entityId, string instanceKey, PathMap pathMap, int targetFixedX, int targetFixedY)
        {
            return _instance._states.TryGetValue(entityId, out State state)
                && state.FollowStarted
                && (state.FollowState == 8 || state.FollowState == 0x0F)
                && state.FollowMoverMode == FollowMoverModePoint
                && state.FollowPathRequestId == requestId
                && ReferenceEquals(state.FollowPathRequestMap, pathMap)
                && string.Equals(state.FollowPathRequestInstanceKey, instanceKey, StringComparison.OrdinalIgnoreCase)
                && state.FollowTargetFixedX == targetFixedX
                && state.FollowTargetFixedY == targetFixedY;
        }

        private static void InstallFollowPath(int requestId, uint entityId, string instanceKey, PathMap pathMap, int targetFixedX, int targetFixedY, Pathfinder pathfinder, uint simulationTick)
        {
            if (!IsFollowPathRequestCurrent(requestId, entityId, instanceKey, pathMap, targetFixedX, targetFixedY)
                || !_instance._states.TryGetValue(entityId, out State state))
                return;
            List<(int FixedX, int FixedY)> fixedPath = CombatRuntime.BuildUnitMoverPath(pathfinder, targetFixedX, targetFixedY);
            state.FollowPathRequestId = fixedPath.Count == 0 ? -2 : -1;
            state.FollowPathRequestInstanceKey = null;
            state.FollowPathRequestMap = null;
            state.FollowPathFixed.Clear();
            state.FollowPathFixed.AddRange(fixedPath);
            state.FollowPathIndex = 0;
            state.FollowPathReadyTick = simulationTick;
            if (fixedPath.Count == 0)
                StopFollowMover(state);
            string waypointLedger = string.Join(";", fixedPath.Select((point, index) => $"{index}:{point.FixedX},{point.FixedY}"));
            Debug.LogError($"[GNOME-FOLLOW-PATH] entity={entityId} request={requestId} tick={simulationTick} direct={pathfinder.DirectReach} reachedGoal={pathfinder.ReachedGoal} expanded={pathfinder.NodesExpanded} waypoints={fixedPath.Count} path='{waypointLedger}' result={(fixedPath.Count == 0 ? "no-path" : "ready")} sourceFunction=PathManager::UpdateRequests@0x004C3D40->UnitMover::OnPathRequestComplete@0x005369B0");
        }

        private static void MoveFollow(State state, uint simulationTick)
        {
            if (!state.FollowMoving || state.FollowMoverAdmissionTick == simulationTick)
                return;
            PathMap pathMap = ResolvePathMap(state);
            int followStepFixed = UnitMover.CacheSpeedPerFrame(FollowSpeedF32, state.FollowSpeedMod, out int followEffectiveSpeedF32);
            int followStopRadiusFixed = UnitMover.CacheSteeringArrivalDistance(followEffectiveSpeedF32, FollowTurnRateFixed * 30);
            if (state.FollowMoverMode == FollowMoverModeDirection)
            {
                int beforeFixedX = state.FixedX;
                int beforeFixedY = state.FixedY;
                int beforeFixedZ = state.FixedZ;
                bool previousMovingThisFrame = state.FollowMoverMovingThisFrame;
                UnitMover.StepInDirectionFixedHeading(
                    state.FixedX,
                    state.FixedY,
                    state.HeadingFixed,
                    state.FollowDirectionHeadingFixed,
                    followStepFixed,
                    FollowTurnRateFixed,
                    true,
                    previousMovingThisFrame,
                    out int nextX,
                    out int nextY,
                    out int nextHeading,
                    out bool nextMovingThisFrame);
                state.FollowMoverMovingThisFrame = nextMovingThisFrame;
                UnitMover.ResolveMovement(pathMap, state.FixedX, state.FixedY, state.FixedZ, nextX, nextY, out state.FixedX, out state.FixedY, out state.FixedZ);
                state.HeadingFixed = nextHeading;
                ApplyMovementAnimationAfterTranslation(state, beforeFixedX, beforeFixedY, beforeFixedZ);
            }
            else if (state.FollowMoverMode == FollowMoverModePoint)
            {
                if (state.FollowPathRequestId > 0)
                    return;
                if (state.FollowPathIndex >= state.FollowPathFixed.Count)
                {
                    StopFollowMover(state);
                    return;
                }
                while (state.FollowPathIndex < state.FollowPathFixed.Count)
                {
                    (int FixedX, int FixedY) waypoint = state.FollowPathFixed[state.FollowPathIndex];
                    if (waypoint.FixedX != state.FixedX || waypoint.FixedY != state.FixedY)
                        break;
                    state.FollowPathIndex++;
                }
                if (state.FollowPathIndex >= state.FollowPathFixed.Count)
                {
                    StopFollowMover(state);
                    return;
                }
                (int FixedX, int FixedY) target = state.FollowPathFixed[state.FollowPathIndex];
                int beforePathIndex = state.FollowPathIndex;
                int beforePathCount = state.FollowPathFixed.Count;
                bool lookaheadAdvanced = false;
                if (pathMap != null && state.FollowPathIndex + 1 < state.FollowPathFixed.Count)
                {
                    (int FixedX, int FixedY) nextWaypoint = state.FollowPathFixed[state.FollowPathIndex + 1];
                    if (!pathMap.CastGroundRayBlockedFixed(state.FixedX, state.FixedY, nextWaypoint.FixedX, nextWaypoint.FixedY))
                    {
                        state.FollowPathIndex++;
                        target = nextWaypoint;
                        lookaheadAdvanced = true;
                    }
                }
                int targetX = target.FixedX;
                int targetY = target.FixedY;
                int beforeFixedX = state.FixedX;
                int beforeFixedY = state.FixedY;
                int beforeFixedZ = state.FixedZ;
                long targetDeltaX = (long)targetX - state.FixedX;
                long targetDeltaY = (long)targetY - state.FixedY;
                long targetDistanceSquared = targetDeltaX * targetDeltaX + targetDeltaY * targetDeltaY;
                bool arrived = targetDistanceSquared <= (long)followStepFixed * followStepFixed
                    && targetDistanceSquared <= (long)followStopRadiusFixed * followStopRadiusFixed;
                int nextX;
                int nextY;
                int nextHeading;
                if (arrived)
                {
                    nextX = targetX;
                    nextY = targetY;
                    int desiredHeading = UnitMover.VectorToHeadingFixed((int)targetDeltaX, (int)targetDeltaY);
                    nextHeading = UnitMover.InterpolateHeading(state.HeadingFixed, desiredHeading, FollowTurnRateFixed);
                }
                else
                {
                    UnitMover.StepTowardFixedHeading(state.FixedX, state.FixedY, state.HeadingFixed, targetX, targetY, followStepFixed, FollowTurnRateFixed, out nextX, out nextY, out nextHeading, out _);
                }
                state.FollowMoverMovingThisFrame = nextX != beforeFixedX || nextY != beforeFixedY;
                UnitMover.ResolveMovement(pathMap, state.FixedX, state.FixedY, state.FixedZ, nextX, nextY, out state.FixedX, out state.FixedY, out state.FixedZ);
                state.HeadingFixed = nextHeading;
                ApplyMovementAnimationAfterTranslation(state, beforeFixedX, beforeFixedY, beforeFixedZ);
                if (arrived)
                {
                    state.FollowPathIndex++;
                    if (state.FollowPathIndex >= state.FollowPathFixed.Count)
                        StopFollowMover(state);
                }
                Debug.LogError($"[GNOME-MOVETOPOINT-STEP] entity={state.EntityId} tick={simulationTick} pathIndex={beforePathIndex}->{state.FollowPathIndex} pathCount={beforePathCount} stopRadius={followStopRadiusFixed} lookahead={lookaheadAdvanced} start=({beforeFixedX},{beforeFixedY},{beforeFixedZ}) target=({targetX},{targetY}) candidate=({nextX},{nextY}) result=({state.FixedX},{state.FixedY},{state.FixedZ}) arrived={arrived} moving={state.FollowMoving} sourceFunction=UnitMover::UpdateSteering@0x00536380->UnitMover::MoveUnit@0x005366C0");
            }
            else
            {
                StopFollowMover(state);
            }
            BlingGnomeRuntime.Instance.UpdateGnomeLockstepPose(state.Owner.ConnId, state.EntityId, state.FixedX, state.FixedY, state.FixedZ, state.HeadingFixed);
        }

        private static bool TryMoveTowardOwner(State state, int ownerX, int ownerY)
        {
            SetFollowTargetToOwner(state, ownerX, ownerY);
            int dx = ownerX - state.FixedX;
            int dy = ownerY - state.FixedY;
            int distanceSqFixed = DistanceSqFixed8(state.FixedX, state.FixedY, ownerX, ownerY);
            int distanceFixed = UnitMover.TableSquareRoot((uint)distanceSqFixed);
            if (distanceFixed <= 0)
            {
                state.FollowDirectionHeadingFixed = OwnerHeadingFixed(state);
                state.HeadingFixed = state.FollowDirectionHeadingFixed;
                StopFollowMover(state);
                return true;
            }
            int directionX = (int)(((long)dx << 8) / distanceFixed);
            int directionY = (int)(((long)dy << 8) / distanceFixed);
            PathMap pathMap = ResolvePathMap(state);
            int reachableDistanceFixed = pathMap != null
                ? pathMap.CastGroundRayDistanceFixed(state.FixedX, state.FixedY, directionX, directionY, distanceFixed)
                : distanceFixed;
            if (reachableDistanceFixed != distanceFixed)
                return false;
            state.FollowDirectionHeadingFixed = UnitMover.VectorToHeadingFixed(dx, dy);
            state.FollowMoverMode = FollowMoverModeDirection;
            state.FollowMoving = true;
            state.FollowMoverTranslated = false;
            return true;
        }

        private static void MoveToOwnerPoint(State state, int ownerX, int ownerY, uint simulationTick)
        {
            SetFollowTargetToOwner(state, ownerX, ownerY);
            state.FollowMoverMode = FollowMoverModePoint;
            state.FollowMoving = true;
            state.FollowMoverTranslated = false;
            BeginFollowPath(state, simulationTick);
        }

        private static void MatchSpeed(State state, int distanceSqFixed)
        {
            uint ownerEntityId = state.Owner?.Avatar?.Id ?? 0;
            CombatPlayer owner = CombatRuntime.Instance.GetPlayer(ownerEntityId);
            if (owner?.PlayerState == null)
                throw new InvalidOperationException($"Bling owner movement attributes missing entity={ownerEntityId}");
            int targetSpeedF32 = owner.PlayerState.SpeedF32;
            int targetSpeedModF32 = owner.OwnerAckFollowClientEffectiveSpeedModF32;
            int targetScaledSpeedF32 = checked((int)(((long)targetSpeedF32 * targetSpeedModF32) >> 8));
            int matchedSpeedModF32 = checked((int)(((long)targetScaledSpeedF32 << 8) / FollowSpeedF32));
            if (distanceSqFixed > 0x132400)
            {
                matchedSpeedModF32 = checked(matchedSpeedModF32 + 0x2800);
            }
            else if (distanceSqFixed > FollowNearDistanceSqFixed)
            {
                int distanceRatioF32 = checked((int)(((long)(distanceSqFixed - FollowNearDistanceSqFixed) << 8) / 0xFA000));
                int distanceBonusF32 = checked((int)(((long)distanceRatioF32 * 0x2800) >> 8));
                matchedSpeedModF32 = checked(matchedSpeedModF32 + distanceBonusF32);
            }
            state.FollowSpeedMod = matchedSpeedModF32 >> 8;
        }

        private static void StopFollowMover(State state)
        {
            ClearFollowPath(state);
            state.FollowMoving = false;
            state.FollowMoverMovingThisFrame = false;
            state.FollowMoverMode = FollowMoverModeIdle;
            state.FollowMoverTranslated = false;
            if (state.AnimationState != 8)
                SetIdleAnimation(state);
        }

        private static void SetMovementAnimation(State state)
        {
            if (state.AnimationState == 8)
                return;
            state.AnimationId = 107;
            state.AnimationFramesRemaining = 0;
            state.AnimationState = 2;
        }

        private static void SetIdleAnimation(State state)
        {
            state.AnimationId = 100;
            state.AnimationFramesRemaining = 0;
            state.AnimationState = 1;
        }

        private static void ApplyMovementAnimationAfterTranslation(State state, int beforeFixedX, int beforeFixedY, int beforeFixedZ)
        {
            if (state.FixedX == beforeFixedX && state.FixedY == beforeFixedY && state.FixedZ == beforeFixedZ)
                return;
            state.FollowMoverTranslated = true;
            SetMovementAnimation(state);
        }

        private static void SetFollowTargetToOwner(State state, int ownerX, int ownerY)
        {
            state.FollowTargetFixedX = ownerX;
            state.FollowTargetFixedY = ownerY;
            state.FollowTargetFixedZ = OwnerFixedZ(state);
        }

        private void StartRetrieve(State state, ushort entityId, GameServer.DroppedItemInfo item, uint simulationTick)
        {
            ClearRetrievePath(state);
            state.RetrieveActive = true;
            state.RetrieveState = 0;
            state.RetrievePreviousState = -1;
            state.RetrieveCounter = 0;
            state.RetrieveItemAcquired = false;
            state.RetrieveItemEntityId = entityId;
            state.RetrieveTargetFixedX = item.PosFixedX;
            state.RetrieveTargetFixedY = item.PosFixedY;
            state.RetrieveTargetFixedZ = item.PosFixedZ;
            state.RetrieveTimer = 0;
            state.FollowMoving = true;
            state.FollowMoverMode = FollowMoverModePoint;
            state.FollowTargetFixedX = item.PosFixedX;
            state.FollowTargetFixedY = item.PosFixedY;
            state.FollowTargetFixedZ = item.PosFixedZ;
            _instance._retrieveItemOwners[entityId] = state.EntityId;
            state.FollowMoverTranslated = false;
            BeginRetrievePath(state, simulationTick);
        }

        private void QueueRetrieve(State state, ushort entityId, uint simulationTick)
        {
            state.RetrievePending = true;
            state.RetrievePendingItemEntityId = entityId;
            Debug.LogError($"[GNOME-RETRIEVE] entity={state.EntityId} item={entityId} tick={simulationTick} event=pending slot=Behavior+0x78 sourceFunction=BlingGnomeBehavior::SearchForBling@0x00516DB0->Behavior::doActionLocal@0x00515130");
        }

        private void PromotePendingRetrieve(GameServer server, State state, uint simulationTick)
        {
            if (!state.RetrievePending)
                return;
            ushort entityId = state.RetrievePendingItemEntityId;
            state.RetrievePending = false;
            state.RetrievePendingItemEntityId = 0;
            if (!server.TryGetBlingDroppedItem(entityId, out GameServer.DroppedItemInfo item)
                || !CanAcquireRetrieveItem(state, item)
                || !item.IsGoldDrop
                || item.GoldAmount == 0)
            {
                Debug.LogError($"[GNOME-RETRIEVE] entity={state.EntityId} item={entityId} tick={simulationTick} event=pending-discarded reason=item-unavailable sourceFunction=Behavior::processUpdate@0x00515620");
                return;
            }
            StartRetrieve(state, entityId, item, simulationTick);
            Debug.LogError($"[GNOME-RETRIEVE] entity={state.EntityId} item={entityId} tick={simulationTick} event=pending-promoted slot=Behavior+0x70 sourceFunction=Behavior::processUpdate@0x00515620->Behavior::startAction@0x00515040");
        }

        private static void ClearPendingRetrieve(State state)
        {
            state.RetrievePending = false;
            state.RetrievePendingItemEntityId = 0;
        }

        private static void ClearRetrievePath(State state)
        {
            if (state == null)
                return;
            if (state.RetrievePathRequestId > 0 && state.RetrievePathRequestMap != null)
            {
                CombatRuntime.Instance.CancelMoveToPointPathRequest(
                    state.RetrievePathRequestInstanceKey,
                    state.RetrievePathRequestMap,
                    state.RetrievePathRequestId);
            }
            state.RetrievePathRequestId = -1;
            state.RetrievePathRequestInstanceKey = null;
            state.RetrievePathRequestMap = null;
            state.RetrievePathFixed.Clear();
            state.RetrievePathIndex = 0;
        }

        private void BeginRetrievePath(State state, uint simulationTick)
        {
            PathMap pathMap = ResolvePathMap(state);
            state.RetrievePathRequestMap = pathMap;
            state.RetrievePathRequestInstanceKey = state.InstanceKey;
            state.RetrievePathRequestTick = simulationTick;
            state.RetrievePathReadyTick = 0;
            PathNode startNode = pathMap?.GetClosestNodeFixed(state.FixedX, state.FixedY, state.FixedZ);
            int resolvedTargetFixedX = state.FixedX;
            int resolvedTargetFixedY = state.FixedY;
            PathNode goalNode = null;
            if (pathMap != null && startNode != null)
            {
                pathMap.FindValidDestPointFixed(
                    state.FixedX,
                    state.FixedY,
                    state.RetrieveTargetFixedX,
                    state.RetrieveTargetFixedY,
                    startNode,
                    0x6400,
                    out resolvedTargetFixedX,
                    out resolvedTargetFixedY,
                    out goalNode);
            }
            if (pathMap == null || startNode == null || goalNode == null)
            {
                state.RetrievePathRequestId = -2;
                StopFollowMover(state);
                Debug.LogError($"[GNOME-RETRIEVE-PATH] entity={state.EntityId} item={state.RetrieveItemEntityId} request=-2 start=({state.FixedX},{state.FixedY}) target=({state.RetrieveTargetFixedX},{state.RetrieveTargetFixedY}) tick={simulationTick} result=invalid-node sourceFunction=UnitMover::MoveToPoint@0x00535FB0->PathManager::RequestPathSync<UnitBehavior>@0x00519920");
                return;
            }

            var pathfinder = new Pathfinder(pathMap);
            pathfinder.RequestPath(
                state.FixedX,
                state.FixedY,
                startNode,
                resolvedTargetFixedX,
                resolvedTargetFixedY,
                goalNode);
            uint ownerEntityId = state.EntityId;
            ushort itemEntityId = state.RetrieveItemEntityId;
            string instanceKey = state.InstanceKey;
            int targetFixedX = state.RetrieveTargetFixedX;
            int targetFixedY = state.RetrieveTargetFixedY;
            int requestId = CombatRuntime.Instance.RequestMoveToPointPathSync(
                instanceKey,
                pathMap,
                pathfinder,
                "bling-gnome",
                ownerEntityId,
                targetFixedX,
                targetFixedY,
                candidateRequestId => IsRetrievePathRequestCurrent(candidateRequestId, ownerEntityId, itemEntityId, instanceKey, pathMap, targetFixedX, targetFixedY),
                (candidateRequestId, completedPathfinder, completedTick) => InstallRetrievePath(candidateRequestId, ownerEntityId, itemEntityId, instanceKey, pathMap, targetFixedX, targetFixedY, completedPathfinder, completedTick));
            state.RetrievePathRequestId = requestId;
            if (requestId <= 0)
                StopFollowMover(state);
            Debug.LogError($"[GNOME-RETRIEVE-PATH] entity={state.EntityId} item={state.RetrieveItemEntityId} request={requestId} start=({state.FixedX},{state.FixedY}) target=({targetFixedX},{targetFixedY}) tick={simulationTick} result=queued sourceFunction=RetrieveItem::States@0x0052C8E0->UnitMover::MoveToPoint@0x00535FB0->PathManager::RequestPathSync<UnitBehavior>@0x00519920");
        }

        private bool IsRetrievePathRequestCurrent(int requestId, uint entityId, ushort itemEntityId, string instanceKey, PathMap pathMap, int targetFixedX, int targetFixedY)
        {
            return _states.TryGetValue(entityId, out State state)
                && state.RetrieveActive
                && (state.RetrieveState == 0 || state.RetrieveState == 2)
                && state.RetrieveItemEntityId == itemEntityId
                && state.RetrievePathRequestId == requestId
                && ReferenceEquals(state.RetrievePathRequestMap, pathMap)
                && string.Equals(state.RetrievePathRequestInstanceKey, instanceKey, StringComparison.OrdinalIgnoreCase)
                && state.RetrieveTargetFixedX == targetFixedX
                && state.RetrieveTargetFixedY == targetFixedY;
        }

        private void InstallRetrievePath(int requestId, uint entityId, ushort itemEntityId, string instanceKey, PathMap pathMap, int targetFixedX, int targetFixedY, Pathfinder pathfinder, uint simulationTick)
        {
            if (!IsRetrievePathRequestCurrent(requestId, entityId, itemEntityId, instanceKey, pathMap, targetFixedX, targetFixedY)
                || !_states.TryGetValue(entityId, out State state))
                return;
            List<(int FixedX, int FixedY)> fixedPath = CombatRuntime.BuildUnitMoverPath(pathfinder, targetFixedX, targetFixedY);
            state.RetrievePathRequestId = fixedPath.Count == 0 ? -2 : -1;
            state.RetrievePathRequestInstanceKey = null;
            state.RetrievePathRequestMap = null;
            state.RetrievePathFixed.Clear();
            state.RetrievePathFixed.AddRange(fixedPath);
            state.RetrievePathIndex = 0;
            state.RetrievePathReadyTick = simulationTick;
            if (fixedPath.Count == 0)
                StopFollowMover(state);
            Debug.LogError($"[GNOME-RETRIEVE-PATH] entity={entityId} item={itemEntityId} request={requestId} tick={simulationTick} direct={pathfinder.DirectReach} reachedGoal={pathfinder.ReachedGoal} expanded={pathfinder.NodesExpanded} waypoints={fixedPath.Count} result={(fixedPath.Count == 0 ? "no-path" : "ready")} sourceFunction=PathManager::UpdateRequests@0x004C3D40->UnitMover::OnPathRequestComplete@0x005369B0");
        }

        private static void TickRetrieve(GameServer server, State state, uint simulationTick)
        {
            if (state.RetrieveState == 0)
            {
                state.RetrievePreviousState = 0;
                state.RetrieveState = 2;
                return;
            }
            if (state.RetrieveTimer > 0)
                state.RetrieveCounter++;
            bool itemAvailable = server.TryGetBlingDroppedItem(state.RetrieveItemEntityId, out GameServer.DroppedItemInfo item);
            if (state.RetrieveState == 4)
            {
                if (state.RetrieveTimer > 0)
                {
                    state.RetrieveTimer--;
                    if (state.RetrieveTimer == 0)
                    {
                        state.AnimationId = 100;
                        state.AnimationState = 1;
                        state.AnimationFramesRemaining = 0;
                    }
                }
                if (!itemAvailable && state.RetrieveTimer == 0)
                    FinishRetrieve(state, simulationTick, state.RetrieveItemAcquired, "item-and-animation-complete");
                return;
            }
            if (!itemAvailable
                || !CanAcquireRetrieveItem(state, item)
                || !item.IsGoldDrop
                || item.GoldAmount == 0)
            {
                FinishRetrieve(state, simulationTick, false, "item-unavailable");
                return;
            }

            if (state.RetrieveState == 2)
            {
                if (state.RetrievePathRequestId > 0)
                    return;
                if (!state.FollowMoving)
                {
                    BeginRetrieveAnimation(state, simulationTick);
                    return;
                }
                if (state.RetrievePathIndex < state.RetrievePathFixed.Count)
                {
                    (int FixedX, int FixedY) waypoint = state.RetrievePathFixed[state.RetrievePathIndex];
                    if (state.FixedX == waypoint.FixedX && state.FixedY == waypoint.FixedY)
                        state.RetrievePathIndex++;
                }
                if (state.RetrievePathIndex >= state.RetrievePathFixed.Count)
                {
                    StopFollowMover(state);
                    BeginRetrieveAnimation(state, simulationTick);
                    return;
                }
                (int FixedX, int FixedY) target = state.RetrievePathFixed[state.RetrievePathIndex];
                int stepFixed = UnitMover.CacheSpeedPerFrame(FollowSpeedF32, FollowBaseSpeedMod, out int effectiveSpeedF32);
                if (state.RetrievePathIndex < state.RetrievePathFixed.Count - 1)
                {
                    long waypointDeltaX = (long)target.FixedX - state.FixedX;
                    long waypointDeltaY = (long)target.FixedY - state.FixedY;
                    long distanceSqFixed = waypointDeltaX * waypointDeltaX + waypointDeltaY * waypointDeltaY;
                    long speedSqFixed = (long)stepFixed * stepFixed;
                    int steeringArrivalDistanceFixed = UnitMover.CacheSteeringArrivalDistance(effectiveSpeedF32, FollowTurnRateFixed * 30);
                    long steeringSqFixed = (long)steeringArrivalDistanceFixed * steeringArrivalDistanceFixed;
                    if (distanceSqFixed <= speedSqFixed && distanceSqFixed <= steeringSqFixed)
                    {
                        state.FixedX = target.FixedX;
                        state.FixedY = target.FixedY;
                        state.RetrievePathIndex++;
                        target = state.RetrievePathFixed[state.RetrievePathIndex];
                    }
                }
                UnitMover.StepTowardFixedHeading(
                    state.FixedX,
                    state.FixedY,
                    state.HeadingFixed,
                    target.FixedX,
                    target.FixedY,
                    stepFixed,
                    FollowTurnRateFixed,
                    out int nextX,
                    out int nextY,
                    out int nextHeading,
                    out bool arrived);
                int beforeFixedX = state.FixedX;
                int beforeFixedY = state.FixedY;
                int beforeFixedZ = state.FixedZ;
                UnitMover.ResolveMovement(ResolvePathMap(state), state.FixedX, state.FixedY, state.FixedZ, nextX, nextY, out state.FixedX, out state.FixedY, out state.FixedZ);
                state.HeadingFixed = nextHeading;
                ApplyMovementAnimationAfterTranslation(state, beforeFixedX, beforeFixedY, beforeFixedZ);
                BlingGnomeRuntime.Instance.UpdateGnomeLockstepPose(state.Owner.ConnId, state.EntityId, state.FixedX, state.FixedY, state.FixedZ, state.HeadingFixed);
                if (!arrived && (state.FixedX != target.FixedX || state.FixedY != target.FixedY))
                    return;
                state.RetrievePathIndex++;
                if (state.RetrievePathIndex >= state.RetrievePathFixed.Count)
                {
                    StopFollowMover(state);
                    BeginRetrieveAnimation(state, simulationTick);
                }
                return;
            }

            if (state.RetrieveTimer > 0)
                state.RetrieveTimer--;
            if (state.RetrieveTimer != 0)
                return;
            if (state.RetrieveState == 10)
            {
                state.RetrievePreviousState = 10;
                state.RetrieveState = 4;
                state.RetrieveTimer = RetrieveStateDelayTicks;
                state.RetrieveItemAcquired = false;
                if (_instance._pendingItemAcquisitions.TryGetValue(state.RetrieveItemEntityId, out ItemAcquisition previous))
                    previous.Owner.ItemAcquisitions.Remove(state.RetrieveItemEntityId);
                _instance._pendingItemAcquisitions[state.RetrieveItemEntityId] = new ItemAcquisition { Owner = state, Timer = RetrieveFlipTicks };
                state.ItemAcquisitions.Add(state.RetrieveItemEntityId);
                server.SetDroppedItemLifecycle(state.RetrieveItemEntityId, 2, RetrieveFlipTicks, 0, simulationTick);
                Debug.LogError($"[GNOME-RETRIEVE] entity={state.EntityId} item={state.RetrieveItemEntityId} tick={simulationTick} state=10->4 itemState=2 timer={RetrieveFlipTicks} sourceFunction=RetrieveItem::States@0x0052C8E0->ItemObject::FlipAndAcquire@0x0058B360->ItemObject::initState@0x0058A1E0");
                return;
            }
        }

        private static void BeginRetrieveAnimation(State state, uint simulationTick)
        {
            int animationFrames = ResolveAnimationFrameCount(110);
            state.AnimationId = 110;
            state.AnimationState = 8;
            state.AnimationFramesRemaining = animationFrames;
            state.RetrievePreviousState = 2;
            state.RetrieveState = 10;
            state.RetrieveTimer = RetrieveStateDelayTicks;
            Debug.LogError($"[GNOME-RETRIEVE] entity={state.EntityId} item={state.RetrieveItemEntityId} tick={simulationTick} state=2->10 animation=110 animationFrames={animationFrames} messageDelayTicks={RetrieveStateDelayTicks} sourceFunction=RetrieveItem::States@0x0052C8E0->UnitMover::isMoving@0x005357C0->Unit::playAnimation@0x0050AF30->StateMachine::SendMessageA@0x005F09F0");
        }

        private static void FinishRetrieve(State state, uint simulationTick, bool acquired, string reason)
        {
            ClearRetrievePath(state);
            _instance._retrieveItemOwners.Remove(state.RetrieveItemEntityId);
            state.RetrieveActive = false;
            state.RetrieveState = -1;
            state.RetrieveItemEntityId = 0;
            state.RetrieveTimer = 0;
            StopFollowMover(state);
            Debug.LogError($"[GNOME-RETRIEVE] entity={state.EntityId} tick={simulationTick} event=terminated acquired={acquired} reason={reason} nextSearch={state.SearchDue} sourceFunction=RetrieveItem::States@0x0052C8E0->Action::terminate@0x0052C1C0");
        }

        private static void InterruptRetrieveForConvert(State state, uint simulationTick)
        {
            ClearPendingRetrieve(state);
            if (!state.RetrieveActive)
                return;
            ushort itemEntityId = state.RetrieveItemEntityId;
            ClearRetrievePath(state);
            _instance._retrieveItemOwners.Remove(itemEntityId);
            state.RetrieveActive = false;
            state.RetrieveState = -1;
            state.RetrieveItemEntityId = 0;
            state.RetrieveTimer = 0;
            StopFollowMover(state);
            Debug.LogError($"[GNOME-RETRIEVE] entity={state.EntityId} item={itemEntityId} tick={simulationTick} event=interrupted-by-convert sourceFunction=Behavior::doInterruptLocal@0x00515290->RetrieveItem::stop@0x0052C760");
        }

        private static void TickConversion(GameServer server, State state, MersenneTwister rng, uint simulationTick)
        {
            if (state.ConvertRequested)
            {
                int animationFrames = ResolveAnimationFrameCount(140);
                state.ConvertRequested = false;
                InterruptRetrieveForConvert(state, simulationTick);
                state.BehaviorSessionCounter++;
                state.ConvertActive = true;
                state.ConvertState = 0;
                state.ConvertCounter = 0;
                state.ConvertOriginalItemCount = 0;
                state.ConvertStartedTick = simulationTick;
                state.ConvertStateAdvanceDueTick = simulationTick + (uint)Math.Max(1, animationFrames - ConvertAnimationTailTicks);
                state.ConvertAnimationDueTick = simulationTick + (uint)animationFrames + 2u;
                state.ConvertActionTimer = animationFrames + 2;
                state.ConvertAnimationComplete = false;
                state.ConvertItemsComplete = false;
                state.ConvertAnimationCompletionTick = 0;
                state.ConvertItemsCompletionTick = 0;
                state.ConversionItems.Clear();
                StopFollowMover(state);
                state.FollowState = -1;
                return;
            }
            if (state.ConvertState == 0)
            {
                int animationFrames = ResolveAnimationFrameCount(140);
                state.ConvertState = 3;
                state.ConvertStateAdvanceDueTick = simulationTick + 136u;
                state.ConvertAnimationDueTick = simulationTick + 152u;
                state.AnimationId = 140;
                state.AnimationState = 8;
                state.AnimationFramesRemaining = animationFrames;
                var candidates = server.GetBlingItemsNearFixed(
                    state.Owner,
                    state.FixedX,
                    state.FixedY,
                    state.FixedZ,
                    state.ConvertRadiusFixed);
                foreach (var candidate in candidates)
                {
                    if (!BlingGnomeRuntime.Instance.TryPrepareConversionItem(state.Owner, candidate.info, state.ConvertRatioFixed, out uint goldAmount))
                        continue;
                    state.ConversionItems.Add(new ConversionItemState
                    {
                        EntityId = candidate.entityId,
                        GoldAmount = goldAmount,
                        BonusEligible = !candidate.info.GeneratedByBlingGnome,
                        Phase = 1,
                        BounceTimer = ConvertBounceTicks
                    });
                    _instance._conversionItemOwners[candidate.entityId] = state.EntityId;
                    server.SetDroppedItemLifecycle(candidate.entityId, 5, ConvertBounceTicks, 1, simulationTick);
                    Debug.LogError($"[GNOME-CONVERT-ITEM] entity={state.EntityId} item={candidate.entityId} tick={simulationTick} state=3->5 phase=1 timer={ConvertBounceTicks} gold={goldAmount} sourceFunction=ConvertItemsToGold::States@0x00524530->ItemObject::BounceAndConvert@0x0058B310->ItemObject::initState@0x0058A1E0");
                }
                state.ConvertOriginalItemCount = state.ConversionItems.Count;
                Debug.LogError($"[GNOME-CONVERT] entity={state.EntityId} tick={simulationTick} state=3->10 candidates={candidates.Count} accepted={state.ConversionItems.Count} sourceFunction=ConvertItemsToGold::States@0x00524530");
                return;
            }
            if (state.ConvertState == 3)
                state.ConvertState = 10;
            state.ConvertCounter++;

            for (int index = 0; index < state.ConversionItems.Count;)
            {
                ConversionItemState conversion = state.ConversionItems[index];
                if (!server.TryGetBlingDroppedItem(conversion.EntityId, out GameServer.DroppedItemInfo item)
                    || !IsAllowedForOwner(state, item))
                {
                    _instance._conversionItemOwners.Remove(conversion.EntityId);
                    state.ConversionItems.RemoveAt(index);
                    Debug.LogError($"[GNOME-CONVERT-ITEM] entity={state.EntityId} item={conversion.EntityId} tick={simulationTick} event=removed-before-complete sourceFunction=ItemObject::updateState@0x0058A410");
                    continue;
                }
                index++;
            }

            if (!state.ConvertItemsComplete && state.ConvertCounter == 136)
                SignalConvertCompletion(server, state, rng, simulationTick, false);

            if (!state.ConvertAnimationComplete)
            {
                state.ConvertActionTimer = Math.Max(0, 152 - state.ConvertCounter);
                if (state.ConvertCounter == 152)
                {
                    state.AnimationId = 100;
                    state.AnimationState = 1;
                    state.AnimationFramesRemaining = 0;
                    SignalConvertCompletion(server, state, rng, simulationTick, true);
                }
            }
        }

        private static void SignalConvertCompletion(GameServer server, State state, MersenneTwister rng, uint simulationTick, bool animation)
        {
            if (animation)
            {
                if (state.ConvertAnimationComplete)
                    return;
                state.ConvertAnimationComplete = true;
                state.ConvertAnimationCompletionTick = simulationTick;
            }
            else
            {
                if (state.ConvertItemsComplete)
                    return;
                state.ConvertItemsComplete = true;
                state.ConvertItemsCompletionTick = simulationTick;
            }

            if (state.ConvertState == 10)
            {
                state.ConvertState = 4;
                Debug.LogError($"[GNOME-CONVERT] entity={state.EntityId} tick={simulationTick} state=10->4 completion={(animation ? "animation" : "items")} sourceFunction=ConvertItemsToGold::States@0x00524530");
                return;
            }
            if (state.ConvertState != 4 || !state.ConvertAnimationComplete || !state.ConvertItemsComplete)
                return;
            state.ConvertActive = false;
            state.ConvertState = -1;
            if (state.RetrievePending)
                _instance.PromotePendingRetrieve(server, state, simulationTick);
            else if (state.BlingState == 5)
                StartFollow(state, rng, simulationTick, "ConvertItemsToGold::terminate->BlingGnomeBehavior::States-state5-update");
            BlingGnomeRuntime.Instance.CompleteActiveConversion(state.Owner, state.EntityId);
            Debug.LogError($"[GNOME-CONVERT] entity={state.EntityId} tick={simulationTick} state=4->complete completion={(animation ? "animation" : "items")} sourceFunction=ConvertItemsToGold::States@0x00524530->Action::terminate@0x0052C1C0");
        }

        private static int ResolveAnimationFrameCount(int animationId)
        {
            GCNode animationList = GCDatabase.Instance?.ResolveWithInheritance("creatures.summon.blinggnome.base.BlingGnome_Animations");
            if (animationList == null || animationList.PackageEntryId != 20186)
                throw new InvalidOperationException("Missing canonical BlingGnome_Animations");
            IEnumerable<GCNode> rows = animationList.OrderedChildren.Count != 0
                ? animationList.OrderedChildren
                : animationList.AnonymousChildren;
            foreach (GCNode row in rows)
            {
                if (row.GetInt("AnimationID", -1) != animationId)
                    continue;
                int frames = row.GetInt("NumFrames", 0);
                if (frames <= 0)
                    break;
                return frames;
            }
            throw new InvalidOperationException($"Missing canonical Bling Gnome animation {animationId}");
        }

        private static void DispatchSearch(GameServer server, State state, MersenneTwister rng, uint simulationTick)
        {
            DeliverSearch(server, state, rng, simulationTick);
            state.SearchMessageOrder = ++state.NextOuterMessageOrder;
        }

        private static void DispatchFidget(State state, MersenneTwister rng, uint simulationTick)
        {
            DeliverFidget(state, rng, simulationTick);
            state.FidgetMessageOrder = ++state.NextOuterMessageOrder;
        }

        private static void DeliverSearch(GameServer server, State state, MersenneTwister rng, uint simulationTick)
        {
            if (state.BlingState == 5)
            {
                state.SearchDue += state.SearchRepeat;
                if (!TryFindSearchableGold(server, state, out ushort itemEntityId, out GameServer.DroppedItemInfo item))
                    return;
                state.BehaviorSessionCounter++;
                state.BlingState = 30;
                state.BlingPreviousState = 5;
                StopFollowMover(state);
                state.FollowState = -1;
                state.SearchRepeat = BlingRetrieveSearchRepeat;
                state.SearchDue = state.BlingCounter + BlingRetrieveSearchInitial + 1;
                if (state.ConvertRequested || state.ConvertActive)
                    _instance.QueueRetrieve(state, itemEntityId, simulationTick);
                else
                    _instance.StartRetrieve(state, itemEntityId, item, simulationTick);
                Debug.LogError($"[RNG-BLING] entity={state.EntityId} tick={simulationTick} event=search-found item={itemEntityId} state=5->30 nextSearch={state.SearchDue} sourceFunction=BlingGnomeBehavior::SearchForBling@0x00516DB0->Behavior::doActionLocal@0x00515130");
                return;
            }
            if (state.BlingState != 30)
                return;
            state.SearchDue += state.SearchRepeat;
            if (state.RetrieveActive || state.RetrievePending || state.ConvertRequested || state.ConvertActive)
                return;
            if (TryFindSearchableGold(server, state, out ushort nextItemEntityId, out GameServer.DroppedItemInfo nextItem))
            {
                _instance.StartRetrieve(state, nextItemEntityId, nextItem, simulationTick);
                Debug.LogError($"[RNG-BLING] entity={state.EntityId} tick={simulationTick} event=search-found item={nextItemEntityId} state=30 nextSearch={state.SearchDue} sourceFunction=BlingGnomeBehavior::States@0x00516A50->BlingGnomeBehavior::SearchForBling@0x00516DB0");
                return;
            }
            state.BlingPreviousState = state.BlingState;
            state.BlingState = 5;
            state.SearchRepeat = BlingSearchRepeat;
            state.SearchDue = state.BlingCounter + BlingSearchInitial + 1;
            state.BehaviorSessionCounter++;
            StartFollow(state, rng, simulationTick, "BlingGnomeBehavior::States-state30-to-state5");
            Debug.LogError($"[RNG-BLING] entity={state.EntityId} tick={simulationTick} event=search-empty state=30->5 nextSearch={state.SearchDue} sourceFunction=BlingGnomeBehavior::States@0x00516A50");
        }

        private static void DeliverFidget(State state, MersenneTwister rng, uint simulationTick)
        {
            state.FidgetDue += state.FidgetRepeat;
            byte animationState = GetAnimationState(state);
            if (state.BlingState != 5 || animationState != 1)
            {
                Debug.LogError($"[RNG-BLING] entity={state.EntityId} tick={simulationTick} event=fidget-skip state={state.BlingState} animationState={animationState} animation={state.AnimationId} animationFramesRemaining={state.AnimationFramesRemaining} moverMode={state.FollowMoverMode} moving={state.FollowMoving} nextDue={state.FidgetDue} sourceFunction=BlingGnomeBehavior::DoRandomFidget@0x005172A0->WorldEntity::getAnimationState@0x004D4500");
                return;
            }
            int before = rng.CallsSinceReseed;
            uint raw = RngLedger.Generate(rng, "room", "BlingGnome:Unit::ChooseRandomFidget", $"{state.InstanceKey}:entity={state.EntityId}");
            int animationId = 101 + (int)(raw % 4u);
            state.AnimationId = (ushort)animationId;
            state.AnimationFramesRemaining = ResolveAnimationFrameCount(animationId);
            state.AnimationState = 8;
            Debug.LogError($"[RNG-BLING] entity={state.EntityId} tick={simulationTick} event=fidget stream=room raw=0x{raw:X8} pos={before}->{rng.CallsSinceReseed} animation={animationId} animationFrames={state.AnimationFramesRemaining} nextDue={state.FidgetDue} sourceFunction=Unit::ChooseRandomFidget@0x0050B4F9 candidates=4");
        }

        private static byte GetAnimationState(State state)
        {
            return state.AnimationState;
        }

        private static bool TryFindSearchableGold(GameServer server, State state, out ushort entityId, out GameServer.DroppedItemInfo item)
        {
            entityId = 0;
            item = null;
            if (server == null || state.Owner == null)
                return false;
            PathMap pathMap = ResolvePathMap(state);
            if (pathMap == null)
                return false;
            var candidates = server.GetBlingItemsNearFixed(state.Owner, state.FixedX, state.FixedY, state.FixedZ, SearchRadiusFixed);
            foreach (var candidate in candidates)
            {
                GameServer.DroppedItemInfo candidateItem = candidate.info;
                if (candidateItem == null
                    || !candidateItem.IsGoldDrop
                    || candidateItem.GoldAmount == 0
                    || !CanSearchRetrieveItem(server, state, candidateItem))
                    continue;
                if (!pathMap.CanPathToFixed(state.FixedX, state.FixedY, candidateItem.PosFixedX, candidateItem.PosFixedY))
                    continue;
                entityId = candidate.entityId;
                item = candidateItem;
                return true;
            }
            return false;
        }

        private static bool IsAllowedForOwner(State state, GameServer.DroppedItemInfo item)
        {
            if (state?.Owner == null || item == null || item.OwnerCharacterId == 0)
                return false;
            return state.Owner.CharSqlId != 0 && item.OwnerCharacterId == state.Owner.CharSqlId;
        }

        private static bool CanAcquireRetrieveItem(State state, GameServer.DroppedItemInfo item)
        {
            if (state?.Owner == null || item == null)
                return false;
            return item.OwnerCharacterId == 0
                || (state.Owner.CharSqlId != 0 && item.OwnerCharacterId == state.Owner.CharSqlId);
        }

        private static bool CanSearchRetrieveItem(GameServer server, State state, GameServer.DroppedItemInfo item)
        {
            if (!CanAcquireRetrieveItem(state, item))
                return false;
            return item.OwnerCharacterId != 0
                || !server.HasOtherPlayerWithSameNativeGroupInWorld(state.Owner);
        }

        private static PathMap ResolvePathMap(State state)
        {
            PathMap pathMap = PathMapCatalog.Instance.GetPathMap(state.InstanceKey);
            return pathMap ?? PathMapCatalog.Instance.GetPathMap(state.Owner?.CurrentZoneName ?? "");
        }

        private static int DistanceSqFixed8(int ax, int ay, int bx, int by)
        {
            long dx = (long)ax - bx;
            long dy = (long)ay - by;
            long value = ((dx * dx) >> 8) + ((dy * dy) >> 8);
            return value > int.MaxValue ? int.MaxValue : (int)value;
        }

        private static int OwnerFixedX(RRConnection conn)
        {
            if (conn != null && conn.HasUnitFollowClientPosition)
                return conn.UnitFollowClientPosFixedX;
            if (conn != null && conn.HasOwnerAckFollowClientPosition)
                return conn.OwnerAckFollowClientPosFixedX;
            return conn != null && conn.HasReflectedAvatarPosition ? conn.ReflectedAvatarPosFixedX : conn?.PlayerPosFixedX ?? 0;
        }

        private static int OwnerFixedX(State state)
        {
            return state.FollowTargetSnapshotTick != uint.MaxValue
                ? state.FollowTargetSnapshotFixedX
                : OwnerFixedX(state.Owner);
        }

        private static int OwnerFixedY(RRConnection conn)
        {
            if (conn != null && conn.HasUnitFollowClientPosition)
                return conn.UnitFollowClientPosFixedY;
            if (conn != null && conn.HasOwnerAckFollowClientPosition)
                return conn.OwnerAckFollowClientPosFixedY;
            return conn != null && conn.HasReflectedAvatarPosition ? conn.ReflectedAvatarPosFixedY : conn?.PlayerPosFixedY ?? 0;
        }

        private static int OwnerFixedY(State state)
        {
            return state.FollowTargetSnapshotTick != uint.MaxValue
                ? state.FollowTargetSnapshotFixedY
                : OwnerFixedY(state.Owner);
        }

        private static int OwnerFixedZ(RRConnection conn)
        {
            if (conn != null && conn.HasUnitFollowClientPosition)
                return conn.UnitFollowClientPosFixedZ;
            if (conn != null && conn.HasOwnerAckFollowClientPosition)
                return conn.OwnerAckFollowClientPosFixedZ;
            return conn != null && conn.HasReflectedAvatarPosition ? conn.ReflectedAvatarPosFixedZ : conn?.PlayerPosFixedZ ?? 0;
        }

        private static int OwnerFixedZ(State state)
        {
            return state.FollowTargetSnapshotTick != uint.MaxValue
                ? state.FollowTargetSnapshotFixedZ
                : OwnerFixedZ(state.Owner);
        }

        private static int OwnerHeadingFixed(RRConnection conn)
        {
            if (conn != null && conn.HasUnitFollowClientPosition)
                return conn.UnitFollowClientHeadingFixed;
            if (conn != null && conn.HasOwnerAckFollowClientPosition)
                return conn.OwnerAckFollowClientHeadingFixed;
            return conn != null && conn.HasReflectedAvatarPosition ? conn.ReflectedAvatarHeadingFixed : conn?.PlayerHeadingFixed ?? 0;
        }

        private static int OwnerHeadingFixed(State state)
        {
            return state.FollowTargetSnapshotTick != uint.MaxValue
                ? state.FollowTargetSnapshotHeadingFixed
                : OwnerHeadingFixed(state.Owner);
        }

        private static bool OwnerMovingThisFrame(RRConnection conn)
        {
            if (conn != null && conn.HasUnitFollowClientPosition)
                return conn.UnitFollowClientMovingThisFrame;
            if (conn != null && conn.HasOwnerAckFollowClientPosition)
                return conn.OwnerAckFollowClientMovingThisFrame;
            return conn != null && conn.HasReflectedAvatarPosition && conn.ReflectedAvatarMovingThisFrame;
        }

        private static bool OwnerMovingThisFrame(State state)
        {
            return state.FollowTargetSnapshotTick != uint.MaxValue
                ? state.FollowTargetSnapshotMovingThisFrame
                : OwnerMovingThisFrame(state.Owner);
        }
    }

    public readonly struct BlingGnomeActionSnapshot
    {
        public byte SessionCounter { get; init; }
        public int RetrieveCounter { get; init; }
        public int RetrievePreviousState { get; init; }
        public int ConvertCounter { get; init; }
        public int ConvertOriginalItemCount { get; init; }
        public int ConvertCenterFixedX { get; init; }
        public int ConvertCenterFixedY { get; init; }
        public int ConvertRadiusFixed { get; init; }
        public int ConvertRatioFixed { get; init; }
    }

    public readonly struct BlingGnomeLockstepSnapshot
    {
        public uint EntityId { get; }
        public ushort BehaviorId { get; }
        public string InstanceKey { get; }
        public bool Initialized { get; }
        public bool FollowStarted { get; }
        public bool SpawnAnimationActionActive { get; }
        public byte SpawnAnimationPhase { get; }
        public byte SpawnAnimationCounter { get; }
        public bool UnSpawnAdmissionPending { get; }
        public bool UnSpawnPacketFlushed { get; }
        public uint UnSpawnWriterTick { get; }
        public bool UnSpawnActionActive { get; }
        public byte UnSpawnPhase { get; }
        public byte UnSpawnCounter { get; }
        public bool UnSpawnRemovalPending { get; }
        public uint UnSpawnRemovalDueTick { get; }
        public int FixedX { get; }
        public int FixedY { get; }
        public int FixedZ { get; }
        public int HeadingFixed { get; }
        public int BlingCounter { get; }
        public int BlingState { get; }
        public int BlingPreviousState { get; }
        public int SearchDue { get; }
        public int SearchRepeat { get; }
        public ulong SearchMessageOrder { get; }
        public int FidgetDue { get; }
        public int FidgetRepeat { get; }
        public ulong FidgetMessageOrder { get; }
        public int FollowCounter { get; }
        public int FollowState { get; }
        public int FollowPreviousState { get; }
        public int FollowDue { get; }
        public int FollowRepeat { get; }
        public bool FollowMoving { get; }
        public bool FollowMoverMovingThisFrame { get; }
        public int FollowMoverMode { get; }
        public int FollowSpeedMod { get; }
        public int FollowDirectionHeadingFixed { get; }
        public int FollowTargetFixedX { get; }
        public int FollowTargetFixedY { get; }
        public int FollowTargetFixedZ { get; }
        public ushort AnimationId { get; }
        public int AnimationFramesRemaining { get; }
        public byte AnimationState { get; }
        public bool RetrieveActive { get; }
        public bool RetrievePending { get; }
        public ushort RetrievePendingItemEntityId { get; }
        public int RetrieveState { get; }
        public ushort RetrieveItemEntityId { get; }
        public int RetrieveTimer { get; }
        public int RetrieveTargetFixedX { get; }
        public int RetrieveTargetFixedY { get; }
        public int RetrieveTargetFixedZ { get; }
        public int RetrievePathRequestId { get; }
        public uint RetrievePathRequestTick { get; }
        public uint RetrievePathReadyTick { get; }
        public int RetrievePathCount { get; }
        public int RetrievePathIndex { get; }
        public bool ConvertAdmissionPending { get; }
        public bool ConvertRequested { get; }
        public bool ConvertActive { get; }
        public int ConvertState { get; }
        public int ConvertActionTimer { get; }
        public bool ConvertAnimationComplete { get; }
        public bool ConvertItemsComplete { get; }
        public uint ConvertStartedTick { get; }
        public uint ConvertStateAdvanceDueTick { get; }
        public uint ConvertAnimationDueTick { get; }
        public uint ConvertAnimationCompletionTick { get; }
        public uint ConvertItemsCompletionTick { get; }
        public int ConversionItemCount { get; }
        public ushort FirstConversionItemEntityId { get; }
        public int FirstConversionItemPhase { get; }
        public int FirstConversionItemTimer { get; }
        public BlingGnomeActionSnapshot Actions { get; }

        public BlingGnomeLockstepSnapshot(
            uint entityId,
            ushort behaviorId,
            string instanceKey,
            bool initialized,
            bool followStarted,
            bool spawnAnimationActionActive,
            byte spawnAnimationPhase,
            byte spawnAnimationCounter,
            bool unSpawnAdmissionPending,
            bool unSpawnPacketFlushed,
            uint unSpawnWriterTick,
            bool unSpawnActionActive,
            byte unSpawnPhase,
            byte unSpawnCounter,
            bool unSpawnRemovalPending,
            uint unSpawnRemovalDueTick,
            int fixedX,
            int fixedY,
            int fixedZ,
            int headingFixed,
            int blingCounter,
            int blingState,
            int blingPreviousState,
            int searchDue,
            int searchRepeat,
            ulong searchMessageOrder,
            int fidgetDue,
            int fidgetRepeat,
            ulong fidgetMessageOrder,
            int followCounter,
            int followState,
            int followPreviousState,
            int followDue,
            int followRepeat,
            bool followMoving,
            bool followMoverMovingThisFrame,
            int followMoverMode,
            int followSpeedMod,
            int followDirectionHeadingFixed,
            int followTargetFixedX,
            int followTargetFixedY,
            int followTargetFixedZ,
            ushort animationId,
            int animationFramesRemaining,
            byte animationState,
            bool retrieveActive,
            bool retrievePending,
            ushort retrievePendingItemEntityId,
            int retrieveState,
            ushort retrieveItemEntityId,
            int retrieveTimer,
            int retrieveTargetFixedX,
            int retrieveTargetFixedY,
            int retrieveTargetFixedZ,
            int retrievePathRequestId,
            uint retrievePathRequestTick,
            uint retrievePathReadyTick,
            int retrievePathCount,
            int retrievePathIndex,
            bool convertAdmissionPending,
            bool convertRequested,
            bool convertActive,
            int convertState,
            int convertActionTimer,
            bool convertAnimationComplete,
            bool convertItemsComplete,
            uint convertStartedTick,
            uint convertStateAdvanceDueTick,
            uint convertAnimationDueTick,
            uint convertAnimationCompletionTick,
            uint convertItemsCompletionTick,
            int conversionItemCount,
            ushort firstConversionItemEntityId,
            int firstConversionItemPhase,
            int firstConversionItemTimer,
            BlingGnomeActionSnapshot actions = default)
        {
            EntityId = entityId;
            BehaviorId = behaviorId;
            InstanceKey = instanceKey;
            Initialized = initialized;
            FollowStarted = followStarted;
            SpawnAnimationActionActive = spawnAnimationActionActive;
            SpawnAnimationPhase = spawnAnimationPhase;
            SpawnAnimationCounter = spawnAnimationCounter;
            UnSpawnAdmissionPending = unSpawnAdmissionPending;
            UnSpawnPacketFlushed = unSpawnPacketFlushed;
            UnSpawnWriterTick = unSpawnWriterTick;
            UnSpawnActionActive = unSpawnActionActive;
            UnSpawnPhase = unSpawnPhase;
            UnSpawnCounter = unSpawnCounter;
            UnSpawnRemovalPending = unSpawnRemovalPending;
            UnSpawnRemovalDueTick = unSpawnRemovalDueTick;
            FixedX = fixedX;
            FixedY = fixedY;
            FixedZ = fixedZ;
            HeadingFixed = headingFixed;
            BlingCounter = blingCounter;
            BlingState = blingState;
            BlingPreviousState = blingPreviousState;
            SearchDue = searchDue;
            SearchRepeat = searchRepeat;
            SearchMessageOrder = searchMessageOrder;
            FidgetDue = fidgetDue;
            FidgetRepeat = fidgetRepeat;
            FidgetMessageOrder = fidgetMessageOrder;
            FollowCounter = followCounter;
            FollowState = followState;
            FollowPreviousState = followPreviousState;
            FollowDue = followDue;
            FollowRepeat = followRepeat;
            FollowMoving = followMoving;
            FollowMoverMovingThisFrame = followMoverMovingThisFrame;
            FollowMoverMode = followMoverMode;
            FollowSpeedMod = followSpeedMod;
            FollowDirectionHeadingFixed = followDirectionHeadingFixed;
            FollowTargetFixedX = followTargetFixedX;
            FollowTargetFixedY = followTargetFixedY;
            FollowTargetFixedZ = followTargetFixedZ;
            AnimationId = animationId;
            AnimationFramesRemaining = animationFramesRemaining;
            AnimationState = animationState;
            RetrieveActive = retrieveActive;
            RetrievePending = retrievePending;
            RetrievePendingItemEntityId = retrievePendingItemEntityId;
            RetrieveState = retrieveState;
            RetrieveItemEntityId = retrieveItemEntityId;
            RetrieveTimer = retrieveTimer;
            RetrieveTargetFixedX = retrieveTargetFixedX;
            RetrieveTargetFixedY = retrieveTargetFixedY;
            RetrieveTargetFixedZ = retrieveTargetFixedZ;
            RetrievePathRequestId = retrievePathRequestId;
            RetrievePathRequestTick = retrievePathRequestTick;
            RetrievePathReadyTick = retrievePathReadyTick;
            RetrievePathCount = retrievePathCount;
            RetrievePathIndex = retrievePathIndex;
            ConvertAdmissionPending = convertAdmissionPending;
            ConvertRequested = convertRequested;
            ConvertActive = convertActive;
            ConvertState = convertState;
            ConvertActionTimer = convertActionTimer;
            ConvertAnimationComplete = convertAnimationComplete;
            ConvertItemsComplete = convertItemsComplete;
            ConvertStartedTick = convertStartedTick;
            ConvertStateAdvanceDueTick = convertStateAdvanceDueTick;
            ConvertAnimationDueTick = convertAnimationDueTick;
            ConvertAnimationCompletionTick = convertAnimationCompletionTick;
            ConvertItemsCompletionTick = convertItemsCompletionTick;
            ConversionItemCount = conversionItemCount;
            FirstConversionItemEntityId = firstConversionItemEntityId;
            FirstConversionItemPhase = firstConversionItemPhase;
            FirstConversionItemTimer = firstConversionItemTimer;
            Actions = actions;
        }
    }
}
