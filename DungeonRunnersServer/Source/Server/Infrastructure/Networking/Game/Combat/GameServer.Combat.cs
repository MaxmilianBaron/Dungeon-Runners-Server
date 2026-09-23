using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using DungeonRunners.Engine;
using DungeonRunners.Utilities;
using DungeonRunners.Data;
using DungeonRunners.Core;
using System.Text;
using System.Reflection;
using System.Runtime.CompilerServices;
using Org.BouncyCastle.Utilities;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;
using DungeonRunners.Gameplay;
using DungeonRunners.Database;
using DungeonRunners.Engine.Playables;
using System.Security.Cryptography;
using DungeonRunners.Combat;
using DungeonRunners.Networking.EntitySynchInfo;
using DungeonRunners.Infrastructure;

namespace DungeonRunners.Networking
{
    public partial class GameServer
    {private const int ClientUseTargetMirrorAdmissionTicks = 0;
        private static readonly Dictionary<string, (string modGcType, int durationSeconds)> _creatureDebuffMap
            = new Dictionary<string, (string, int)>(StringComparer.OrdinalIgnoreCase)
        {
            { "basicslow",                    ("skills.creature.BasicSlow.Modifier",                    15) },
            { "basicstun",                    ("skills.creature.BasicStun.Modifier",                     3) },
            { "creaturerend",                 ("skills.creature.CreatureRend.Modifier",                   5) },
            { "creaturehamstring",            ("skills.creature.CreatureHamstring.Modifier",              5) },
            { "creatureenfeeble",             ("skills.creature.CreatureEnfeeble.Modifier",              60) },
            { "creaturegoldstun",             ("skills.creature.CreatureGoldStun.Modifier",              15) },
            { "creaturedebuffdivine",         ("skills.creature.CreatureDebuffDivine.Modifier",          15) },
            { "creaturedebufffire",           ("skills.creature.CreatureDebuffFire.Modifier",            15) },
            { "creaturedebuffice",            ("skills.creature.CreatureDebuffIce.Modifier",             15) },
            { "creaturedebuffpoison",         ("skills.creature.CreatureDebuffPoison.Modifier",          15) },
            { "creaturedebuffshadow",         ("skills.creature.CreatureDebuffShadow.Modifier",          15) },
            { "widowerweb",                   ("skills.creature.WidowerWeb.Modifier",                    10) },
            { "widowerblackcloud",            ("skills.creature.WidowerBlackCloud.Modifier",             10) },
            { "agrockintimidate",             ("skills.creature.AgrockIntimidate.Modifier",              15) },
            { "abaddonflameprison",           ("skills.creature.AbaddonFlamePrison.Modifier",            10) },
            { "orokruntshotpoison",           ("skills.creature.OrokRuntShotPoison.Modifier",             3) },
            { "shadowqueenmortalstrike_fear", ("skills.creature.ShadowQueenMortalStrike_Fear.Modifier",  10) },
            { "bossmortalstrike_fear",        ("skills.creature.BossMortalStrike_Fear.Modifier",          6) },
            { "heckledebuff",                 ("skills.creature.HeckleDebuff.Modifier",                   5) },
            { "griefermultiboltsilence",      ("skills.creature.GrieferMultiBoltSilence.Modifier",        5) },
            { "relicstun",                    ("skills.creature.RelicStun.Modifier",                      1) },
            { "combatfearself",               ("skills.creature.CombatFearSelf.Modifier",                10) },
            { "combatfearfriendsaoe",         ("skills.creature.CombatFearFriendsAoE.Modifier",           8) },
        };

        private static readonly Dictionary<string, string> _weaponDebuffMap
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "basic",                          "creaturerend" },
            { "wargbase_grunt",                 "creaturerend" },
            { "wargbase_hero",                  "creaturerend" },
            { "wargbase_liger",                 "creaturerend" },
            { "wargbase_pinata_hero",           "creaturerend" },
            { "abba_labba_caster_grunt_base",   "creaturedebufffire" },
            { "abba_labba_caster_hero_base",    "creaturedebufffire" },
            { "boss_caster",                    "creaturedebufffire" },
            { "basicslow",                      "basicslow" },
            { "basicstun",                      "basicstun" },
            { "creaturerend",                   "creaturerend" },
            { "creaturehamstring",              "creaturehamstring" },
            { "creatureenfeeble",               "creatureenfeeble" },
            { "creaturegoldstun",               "creaturegoldstun" },
            { "creaturedebuffdivine",           "creaturedebuffdivine" },
            { "creaturedebufffire",             "creaturedebufffire" },
            { "creaturedebuffice",              "creaturedebuffice" },
            { "creaturedebuffpoison",           "creaturedebuffpoison" },
            { "creaturedebuffshadow",           "creaturedebuffshadow" },
            { "widowerweb",                     "widowerweb" },
            { "widowerblackcloud",              "widowerblackcloud" },
            { "agrockintimidate",               "agrockintimidate" },
            { "abaddonflameprison",             "abaddonflameprison" },
            { "orokruntshotpoison",             "orokruntshotpoison" },
            { "shadowqueenmortalstrike_fear",   "shadowqueenmortalstrike_fear" },
            { "bossmortalstrike_fear",          "bossmortalstrike_fear" },
            { "heckledebuff",                   "heckledebuff" },
            { "griefermultiboltsilence",        "griefermultiboltsilence" },
            { "relicstun",                      "relicstun" },
            { "combatfearself",                 "combatfearself" },
            { "combatfearfriendsaoe",           "combatfearfriendsaoe" },
        };

        private long _simulationClockLastMilliseconds;
        private long _pendingSimulationMilliseconds;
        private bool _simulationClockInitialized;
        private int _clientEntityUpdatePhase;
        private uint _clientEntityUpdateSerial;
        private uint _combatTick => SimulationClock.SimulationTick;
        private const int SIMULATION_TICKS_PER_SECOND = (int)SimulationClock.TicksPerSecond;
        private Dictionary<string, HashSet<byte>> _playerSpellSlots = new Dictionary<string, HashSet<byte>>();
        private readonly Dictionary<string, uint> _activeSkillBusyUntilTick = new Dictionary<string, uint>();
        private readonly List<string> _activeSkillBusyKeys = new List<string>();
        private readonly Dictionary<string, uint> _activeSkillCooldownUntilTick = new Dictionary<string, uint>();
        private Dictionary<string, Dictionary<uint, string>> _playerManipMap = new Dictionary<string, Dictionary<uint, string>>();
        private readonly Dictionary<string, List<uint>> _playerManipulatorOrder = new Dictionary<string, List<uint>>();
        private Dictionary<string, Dictionary<string, int>> _playerSkillLevels = new Dictionary<string, Dictionary<string, int>>();
        private Dictionary<string, Dictionary<string, uint>> _playerSkillSlots = new Dictionary<string, Dictionary<string, uint>>();

        private int SkillValuePerLevelF32 => GCDatabase.Instance.GetRequiredKnobFixed32("SkillValuePerLevel");
        private const int MAX_COMBAT_CATCH_UP_TICKS = 5;
        private const int MAX_SIMULATION_ELAPSED_MILLISECONDS = 0xA5;
        private const uint SIMULATION_TICK_INTERVAL_MILLISECONDS = 0x21;
        private const int CLIENT_ENTITY_MESSAGE_RATIO = 3;
        private const int CLIENT_DIRECT_HIT_VISIBILITY_MAX_AGE_TICKS = CLIENT_ENTITY_MESSAGE_RATIO * 2 + 1;

        private static int SkillDescFixedMultiply(int leftF32, int rightF32)
        {
            return unchecked((int)(((long)leftF32 * rightF32) >> 8));
        }

        private int SkillDescGetCostF32(Combat.SpellData spell, int nextLevel)
        {
            int levelOffsetF32 = unchecked((nextLevel << 8) - 0x100);
            int levelIncrementF32 = SkillDescFixedMultiply(levelOffsetF32, spell.RequiredLevelIncF32);
            int skillPowerLevelF32 = unchecked((spell.RequiredLevel << 8) + levelIncrementF32) & ~0xFF;
            int costF32 = SkillDescFixedMultiply(skillPowerLevelF32, SkillValuePerLevelF32);
            return SkillDescFixedMultiply(costF32, spell.GoldValueModF32);
        }

        private void ResetPlayerManipulatorMap(string connKey)
        {
            _playerManipMap[connKey] = new Dictionary<uint, string>();
            _playerManipulatorOrder[connKey] = new List<uint>();
        }

        private void EnsurePlayerManipulatorMap(string connKey)
        {
            if (!_playerManipMap.ContainsKey(connKey))
                _playerManipMap[connKey] = new Dictionary<uint, string>();
            if (!_playerManipulatorOrder.ContainsKey(connKey))
                _playerManipulatorOrder[connKey] = new List<uint>();
        }

        private void SetPlayerManipulator(string connKey, uint manipulatorId, string gcClass)
        {
            EnsurePlayerManipulatorMap(connKey);
            Dictionary<uint, string> map = _playerManipMap[connKey];
            if (!map.ContainsKey(manipulatorId))
                _playerManipulatorOrder[connKey].Add(manipulatorId);
            map[manipulatorId] = gcClass;
        }

        private bool RemovePlayerManipulator(string connKey, uint manipulatorId)
        {
            if (!_playerManipMap.TryGetValue(connKey, out Dictionary<uint, string> map) || !map.Remove(manipulatorId))
                return false;
            if (_playerManipulatorOrder.TryGetValue(connKey, out List<uint> order))
                order.Remove(manipulatorId);
            return true;
        }

        private uint? FindPlayerManipulatorSlot(string connKey, string gcClass, uint excludedSlot)
        {
            if (!_playerManipMap.TryGetValue(connKey, out Dictionary<uint, string> map)
                || !_playerManipulatorOrder.TryGetValue(connKey, out List<uint> order))
                return null;
            for (int index = 0; index < order.Count; index++)
            {
                uint manipulatorId = order[index];
                if (manipulatorId != excludedSlot
                    && map.TryGetValue(manipulatorId, out string candidate)
                    && string.Equals(candidate, gcClass, StringComparison.OrdinalIgnoreCase))
                    return manipulatorId;
            }
            return null;
        }

        private uint AllocatePlayerManipulatorId(string connKey)
        {
            EnsurePlayerManipulatorMap(connKey);
            Dictionary<uint, string> map = _playerManipMap[connKey];
            List<uint> order = _playerManipulatorOrder[connKey];
            uint nextManipulatorId = 100;
            for (int index = 0; index < order.Count; index++)
            {
                uint manipulatorId = order[index];
                if (map.ContainsKey(manipulatorId) && manipulatorId >= nextManipulatorId)
                    nextManipulatorId = manipulatorId == uint.MaxValue ? uint.MaxValue : manipulatorId + 1;
            }
            if (nextManipulatorId == uint.MaxValue && map.ContainsKey(nextManipulatorId))
                throw new InvalidOperationException($"player manipulator id exhausted for {connKey}");
            return nextManipulatorId;
        }

        private void GetValidationCutoff(out uint cutoffTick)
        {
            CombatRuntime.Instance.GetValidationCutoff(out cutoffTick);
        }

        private void SampleSimulationClock()
        {
            long frequency = System.Diagnostics.Stopwatch.Frequency;
            long rawTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            long timestamp = checked(
                (rawTimestamp / frequency) * 1000
                + ((rawTimestamp % frequency) * 1000 / frequency));
            if (!_simulationClockInitialized)
            {
                _simulationClockLastMilliseconds = timestamp;
                _simulationClockInitialized = true;
                return;
            }

            long elapsed = timestamp - _simulationClockLastMilliseconds;
            _simulationClockLastMilliseconds = timestamp;
            if (elapsed <= 0)
                return;
            if (elapsed > MAX_SIMULATION_ELAPSED_MILLISECONDS)
                elapsed = MAX_SIMULATION_ELAPSED_MILLISECONDS;
            _pendingSimulationMilliseconds = unchecked(_pendingSimulationMilliseconds + elapsed);
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
        private static bool VerboseMonsterDiag => ServerDiagnostics.IsEnabled("verboseMonsterDiagnostics");


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
                DrainLiveClientInputBeforeNativeServerTick();
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
                    PromotePendingReflectedAvatarMovementSamples();
                }
                DrainAvatarAggroSamples();
                PromotePendingPlayerUsePositionBehaviorActions(tickIndex);
                ApplyPendingClientActionInputBatches(tickIndex);
                ApplyPendingMonsterBehaviorInputs(tickIndex);
                AdvanceReflectedAvatarMovementSamples();
                TickCombatDeterministicSystems(tickIndex);
                UpdateQuestManagers();
                AdvancePeerUsePositionTerminations(tickIndex);
                AdvancePeerUseTargetTerminations(tickIndex);
                CombatRuntime.Instance.MarkEntityUpdateCompleted(tickIndex, "GameServer.Update");
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






        private void AdvanceAllAvatarHP()
        {
            foreach (RRConnection conn in GetConnectionInsertionOrderSnapshot())
            {
                if (conn == null || !conn.IsConnected || conn.Avatar == null)
                    continue;

                PlayerState state = GetPlayerState(conn.ConnId.ToString());
                if (_adminCommands != null && state != null
                    && _adminCommands.IsRegenActive(conn.ConnId))
                {
                    if (state.IsRegenComplete)
                        _adminCommands.ClearRegenFlag(conn.ConnId);
                }

                SendRemoteAvatarHPUpdates(conn);
                SendGroupMemberHealthManaIfChanged(conn);
            }
        }

        private static string ResolveConnectionInstanceKey(RRConnection conn)
        {
            if (conn == null)
                return null;
            if (!string.IsNullOrWhiteSpace(conn.RuntimeInstanceKey))
                return conn.RuntimeInstanceKey;
            if (IsPublicZone(conn.CurrentZoneName))
                return conn.CurrentZoneName;
            return $"{conn.CurrentZoneName}_inst{conn.InstanceId}";
        }

        private static bool IsPendingConnectionInstanceCurrent(RRConnection conn, string instanceKey)
        {
            return conn != null
                && conn.IsConnected
                && conn.IsSpawned
                && !string.IsNullOrWhiteSpace(instanceKey)
                && string.Equals(
                    RoomRuntime.NormalizeInstanceKey(ResolveConnectionInstanceKey(conn)),
                    RoomRuntime.NormalizeInstanceKey(instanceKey),
                    StringComparison.OrdinalIgnoreCase);
        }

        private bool IsPendingPlayerUseTargetInputCurrent(PendingPlayerUseTargetActionInput input, RRConnection conn)
        {
            if (!IsPendingPlayerUseTargetInputAdmissionCurrent(input, conn))
                return false;
            if (input.TargetPlayerConnId != 0)
            {
                return _connections.TryGetValue(input.TargetPlayerConnId, out RRConnection target)
                    && target != null
                    && target.IsConnected
                    && target.IsSpawned
                    && string.Equals(
                        RoomRuntime.NormalizeInstanceKey(ResolveConnectionInstanceKey(target)),
                        RoomRuntime.NormalizeInstanceKey(input.InstanceKey),
                        StringComparison.OrdinalIgnoreCase);
            }
            Combat.Monster monster = CombatRuntime.Instance.GetMonster(input.MonsterEntityId);
            return monster != null && CombatRuntime.Instance.MatchesInstance(monster, input.InstanceKey);
        }

        private bool IsPendingPlayerUseTargetInputAdmissionCurrent(PendingPlayerUseTargetActionInput input, RRConnection conn)
        {
            return IsPendingConnectionInstanceCurrent(conn, input.InstanceKey)
                && (input.AdmissionSequence <= 0 || conn.LastPlayerUseTargetActionSequence == input.AdmissionSequence);
        }

    }
}
