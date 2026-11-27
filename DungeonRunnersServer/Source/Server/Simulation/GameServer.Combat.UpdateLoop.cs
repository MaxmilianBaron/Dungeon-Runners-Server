using System;
using DungeonRunners.Combat;
using DungeonRunners.Core;
using DungeonRunners.Engine;
using DungeonRunners.Gameplay;
using DungeonRunners.Infrastructure;

namespace DungeonRunners.Networking
{
    public partial class GameServer
    {
        private long _simulationClockLastMilliseconds;
        private long _pendingSimulationMilliseconds;
        private bool _simulationClockInitialized;
        private int _clientEntityUpdatePhase;
        private uint _clientEntityUpdateSerial;
        private uint _combatTick => SimulationClock.SimulationTick;
        private const int SIMULATION_TICKS_PER_SECOND = (int)SimulationClock.TicksPerSecond;
        private const int MAX_COMBAT_CATCH_UP_TICKS = 5;
        private const int MAX_SIMULATION_ELAPSED_MILLISECONDS = 0xA5;
        private const uint SIMULATION_TICK_INTERVAL_MILLISECONDS = 0x21;
        private const int CLIENT_ENTITY_MESSAGE_RATIO = 3;

        private void SampleSimulationClock()
        {
            long frequency = System.Diagnostics.Stopwatch.Frequency;
            long rawTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            long timestamp = checked(
                (rawTimestamp / frequency) * 1000
                + ((rawTimestamp % frequency) * 1000 / frequency));
            AccumulateSimulationMilliseconds(unchecked((uint)timestamp));
        }

        private void AccumulateSimulationMilliseconds(uint timestamp)
        {
            if (!_simulationClockInitialized)
            {
                _simulationClockLastMilliseconds = timestamp;
                _simulationClockInitialized = true;
                return;
            }

            uint elapsed = unchecked(timestamp - (uint)_simulationClockLastMilliseconds);
            if (elapsed > MAX_SIMULATION_ELAPSED_MILLISECONDS)
                elapsed = MAX_SIMULATION_ELAPSED_MILLISECONDS;
            uint pending = unchecked((uint)_pendingSimulationMilliseconds + elapsed);
            if (pending < SIMULATION_TICK_INTERVAL_MILLISECONDS)
                return;
            _simulationClockLastMilliseconds = timestamp;
            _pendingSimulationMilliseconds = pending;
        }

        private bool AdvanceSimulationClock(out uint tickIndex)
        {
            tickIndex = _combatTick;
            if (_pendingSimulationMilliseconds < SIMULATION_TICK_INTERVAL_MILLISECONDS)
                return false;

            _pendingSimulationMilliseconds -= SIMULATION_TICK_INTERVAL_MILLISECONDS;
            tickIndex = SimulationClock.Advance();
            if (VerboseCombatClock) Debug.LogError($"[CLIENT-COMBAT-CLOCK] tick={tickIndex} source=Update");
            return true;
        }

        private bool AdvanceClientEntityUpdatePhase()
        {
            if (_clientEntityUpdatePhase != CLIENT_ENTITY_MESSAGE_RATIO)
            {
                _clientEntityUpdatePhase++;
                return false;
            }

            _clientEntityUpdatePhase = 0;
            _clientEntityUpdateSerial = unchecked(_clientEntityUpdateSerial + 1);
            if (_clientEntityUpdateSerial == 0)
                _clientEntityUpdateSerial = 1;
            return true;
        }

        private static bool VerboseCombatClock => ServerDiagnostics.IsEnabled("verboseCombatClockLogging");


        void Update()
        {
            ProcessPreparedProceduralZoneJoins();
            DrainLiveClientInputBeforeNativeServerTick();
            SampleSimulationClock();

            int combatTicks = 0;
            uint lastTickIndex = 0;
            while (combatTicks < MAX_COMBAT_CATCH_UP_TICKS
                && _pendingSimulationMilliseconds >= SIMULATION_TICK_INTERVAL_MILLISECONDS)
            {
                if (!AdvanceSimulationClock(out uint tickIndex))
                    break;
                bool clientEntityUpdatePhase = AdvanceClientEntityUpdatePhase();
                uint ticksUntilNextClientEntityUpdate = _clientEntityUpdatePhase == 0
                    ? (uint)(CLIENT_ENTITY_MESSAGE_RATIO + 1)
                    : checked((uint)(CLIENT_ENTITY_MESSAGE_RATIO - _clientEntityUpdatePhase + 1));
                CombatRuntime.Instance.SetNextClientEntityUpdateTick(checked(tickIndex + ticksUntilNextClientEntityUpdate));
                ProcessPendingZoneSpawnInvulnerabilities(tickIndex);
                FlushPendingPlayerUseTargetActionInputs(tickIndex);
                if (clientEntityUpdatePhase)
                {
                    FlushPendingMonsterBehaviorUpdates(tickIndex);
                    ProcessPendingRoomClientEpochs(tickIndex);
                    ProcessPendingReturnTownPortalDespawns(tickIndex);
                    FlushAllQueues(
                        heartbeatIfEmpty: true,
                        nativeMoverRecordBudgetPerComponent: CLIENT_ENTITY_MOVEMENT_RECORD_BUDGET_PER_FLUSH,
                        clientEntityUpdateSerial: _clientEntityUpdateSerial);
                    BlingGnomeRuntime.Instance.CommitPendingSpawns(this, tickIndex);
                    BlingGnomeLockstepRuntime.Instance.CommitEntityWriterAdmissions(tickIndex);
                    CombatRuntime.Instance.CommitEntityWriterRemovals(tickIndex);
                    MarkPendingMonsterBehaviorPacketsFlushed(tickIndex);
                }
                using (SimulationParityLedger.EnterOperationPhase("input-admission"))
                {
                    DrainAvatarAggroSamples();
                    PromotePendingPlayerUsePositionBehaviorActions(tickIndex);
                    ApplyPendingClientActionInputBatches(tickIndex);
                    if (clientEntityUpdatePhase)
                        PromotePendingReflectedAvatarMovementSamples();
                    ApplyPendingMonsterBehaviorInputs(tickIndex);
                    AdvanceReflectedAvatarMovementSamples();
                }
                TickCombatDeterministicSystems(tickIndex);
                UpdateQuestManagers();
                AdvancePeerUsePositionTerminations(tickIndex);
                AdvancePeerUseTargetTerminations(tickIndex);
                CombatRuntime.Instance.MarkEntityUpdateCompleted(tickIndex, "GameServer.Update");
                using (SimulationParityLedger.EnterOperationPhase("path-requests"))
                    CombatRuntime.Instance.UpdateMoveToPointPathRequests(tickIndex);
                lastTickIndex = tickIndex;
                combatTicks++;
            }
            if (_expiredDroppedItems.Count > 0)
                FlushExpiredDroppedItems();
            if (combatTicks > 0)
            {
                AdvanceAllAvatarHP();
                ProcessReflectedPortalTransitions();
                WirePacketTally.Report();
                if (VerboseCombatClock) Debug.LogError($"[CLIENT-COMBAT-CLOCK] completed ticks={combatTicks} lastTick={lastTickIndex} pendingMilliseconds={_pendingSimulationMilliseconds}");
                FlushPendingClientControlResets();

                try { ProcessMatchmakingTick(); }
                catch (Exception ex) { Debug.LogError($"[PVP-TICK] {ex.Message}"); }

                ProcessSimulationMaintenance(lastTickIndex);
            }
        }

        private static bool HasReachedSimulationTick(uint currentTick, uint dueTick)
        {
            return unchecked((int)(currentTick - dueTick)) >= 0;
        }

        private static uint AdvanceMaintenanceDueTick(uint dueTick, uint intervalTicks, uint currentTick)
        {
            do
            {
                dueTick = checked(dueTick + intervalTicks);
            }
            while (HasReachedSimulationTick(currentTick, dueTick));

            return dueTick;
        }

        private void ProcessSimulationMaintenance(uint tickIndex)
        {
            if (!_maintenanceScheduleInitialized)
            {
                _nextMerchantRefreshTick = checked(tickIndex + MERCHANT_REFRESH_INTERVAL_TICKS);
                _nextDroppedItemCleanupTick = checked(tickIndex + DROPPED_ITEM_CLEANUP_INTERVAL_TICKS);
                _nextGroupHealthBroadcastTick = checked(tickIndex + GROUP_HEALTH_BROADCAST_INTERVAL_TICKS);
                _nextAutoSaveTick = checked(tickIndex + AUTO_SAVE_INTERVAL_TICKS);
                _maintenanceScheduleInitialized = true;
                return;
            }

            if (HasReachedSimulationTick(tickIndex, _nextMerchantRefreshTick))
            {
                _nextMerchantRefreshTick = AdvanceMaintenanceDueTick(_nextMerchantRefreshTick, MERCHANT_REFRESH_INTERVAL_TICKS, tickIndex);
                MerchantRuntime.ProcessRefreshes(_zoneOrder, _zoneNPCs, GetConnectionInsertionOrderSnapshot(), SendCompressedA, tickIndex);
            }

            if (HasReachedSimulationTick(tickIndex, _nextDroppedItemCleanupTick))
            {
                _nextDroppedItemCleanupTick = AdvanceMaintenanceDueTick(_nextDroppedItemCleanupTick, DROPPED_ITEM_CLEANUP_INTERVAL_TICKS, tickIndex);
                CleanupExpiredDroppedItems();
            }

            if (HasReachedSimulationTick(tickIndex, _nextGroupHealthBroadcastTick))
            {
                _nextGroupHealthBroadcastTick = AdvanceMaintenanceDueTick(_nextGroupHealthBroadcastTick, GROUP_HEALTH_BROADCAST_INTERVAL_TICKS, tickIndex);
                foreach (var broadcastGroup in GroupDirectory.Instance.AllGroups())
                {
                    if (broadcastGroup.Members.Count > 1)
                        SendGroupHealthToAll(broadcastGroup);
                }
            }

            if (HasReachedSimulationTick(tickIndex, _nextAutoSaveTick))
            {
                _nextAutoSaveTick = AdvanceMaintenanceDueTick(_nextAutoSaveTick, AUTO_SAVE_INTERVAL_TICKS, tickIndex);
                if (_pendingAutoSaveConnectionIndex >= _pendingAutoSaveConnections.Count)
                {
                    _pendingAutoSaveConnections.Clear();
                    _pendingAutoSaveConnections.AddRange(GetConnectionInsertionOrderSnapshot());
                    _pendingAutoSaveConnectionIndex = 0;
                }
            }

            if (_pendingAutoSaveConnectionIndex < _pendingAutoSaveConnections.Count)
            {
                RRConnection conn = _pendingAutoSaveConnections[_pendingAutoSaveConnectionIndex++];
                if (conn != null && conn.IsConnected && !string.IsNullOrEmpty(conn.LoginName))
                    SavePlayerLevel(conn);
                if (_pendingAutoSaveConnectionIndex >= _pendingAutoSaveConnections.Count)
                {
                    _pendingAutoSaveConnections.Clear();
                    _pendingAutoSaveConnectionIndex = 0;
                }
            }
        }
    }
}
