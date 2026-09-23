using System;
using System.Collections.Generic;
using DungeonRunners.Engine;
using DungeonRunners.Data;
using DungeonRunners.Networking;
using DungeonRunners.Core;

namespace DungeonRunners.Combat
{
    public class WeaponUseRuntime
    {
        private static WeaponUseRuntime _instance;
        public static WeaponUseRuntime Instance => _instance ??= new WeaponUseRuntime();

        private Dictionary<string, WeaponUseState> _activeCycles = new Dictionary<string, WeaponUseState>();
        private readonly Dictionary<uint, string> _cycleKeyByPlayerEntity = new Dictionary<uint, string>();
        private readonly Dictionary<string, uint> _playerEntityByCycleKey = new Dictionary<string, uint>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _nextUseReadyTickByConnection = new Dictionary<string, int>(StringComparer.Ordinal);
        private Queue<CompletedAttack> _completedAttacks = new Queue<CompletedAttack>();
        private readonly List<PendingProjectileHit> _activeProjectiles = new List<PendingProjectileHit>();
        private long _nextProjectileSequence;

        private const int CLIENT_CONTACT_RANGE_EPSILON_F32 = 0x10;
        private const int CLIENT_PROJECTILE_BROAD_SCAN_PADDING_FIXED = 30 * UnitMover.Fixed;
        private const int PROJECTILE_HEIGHT_TOLERANCE_FIXED = 0x0A00;
        private const int MAX_PENDING_REPEAT_USES = 1;
        private const int CLIENT_USE_TARGET_STATE2_DELAY_TICKS = 6;
        private static bool VerboseProjectileDiagnostics => ServerDiagnostics.IsEnabled("verboseProjectileLogging");
        private int NowTick => (int)CombatRuntime.Instance.CombatTick;
        public int PendingProjectileEventCount => _activeProjectiles.Count;

        public static int ProjectileFlightTicksFixed32(int distanceF32, int speedF32)
        {
            long rangeFixed16 = (long)Math.Max(0, distanceF32) << 8;
            return ProjectileFlightTicksFixed16(rangeFixed16, speedF32);
        }

        public static int ProjectileFlightTicksFixed16(long rangeFixed16, int speedF32)
        {
            int speedByte = Math.Max(1, Math.Min(255, GCDatabase.RoundFixed32ToInt(Math.Max(0, speedF32))));
            long stepDenominator = ((long)speedByte << 16) / 7680L;
            if (stepDenominator < 1L) stepDenominator = 1L;
            if (rangeFixed16 < 0L) rangeFixed16 = 0L;
            long flight = rangeFixed16 / stepDenominator;
            return Math.Max(1, (int)(flight >> 8));
        }

        public static int ProjectileImpactDelayTicksFixed32(int distanceF32, int speedF32)
        {
            return ProjectileFlightTicksFixed32(distanceF32, speedF32);
        }

        public static int ProjectileStepDistanceF32(int speedF32)
        {
            int safeSpeedF32 = Math.Max(0x100, speedF32);
            return Math.Max(1, safeSpeedF32 / 30);
        }

        public static int ProjectileRadiusFromAuthoredSizeF32(int projectileSizeF32)
        {
            return Math.Max(0, projectileSizeF32);
        }

        public static int ProjectileCollisionRadiusF32(int targetCollisionRadiusF32, int projectileSizeF32)
        {
            return Math.Max(0, targetCollisionRadiusF32) + ProjectileRadiusFromAuthoredSizeF32(projectileSizeF32);
        }

        public static bool TestProjectilePointUnitCollisionFixed(
            int pointFixedX,
            int pointFixedY,
            int centerFixedX,
            int centerFixedY,
            int radiusFixed,
            out long distSqFixed)
        {
            distSqFixed = long.MaxValue;
            radiusFixed = Math.Max(0, radiusFixed);
            if (radiusFixed <= 0)
                return false;
            long dxFixed = (long)pointFixedX - centerFixedX;
            long dyFixed = (long)pointFixedY - centerFixedY;
            distSqFixed = NativeUnitTestCollisionDistanceSqFixed8(dxFixed, dyFixed);
            long nativeRadiusSqFixed8 = NativeUnitTestCollisionRadiusSqFixed8(radiusFixed);
            return NativeUnitTestCollisionSqFixed8(distSqFixed, nativeRadiusSqFixed8);
        }

        private static long NativeUnitTestCollisionDistanceSqFixed8(long dxFixed, long dyFixed)
        {
            return ((dxFixed * dxFixed) >> 8) + ((dyFixed * dyFixed) >> 8);
        }

        private static long NativeUnitTestCollisionRadiusSqFixed8(int radiusFixed)
        {
            if (radiusFixed <= 0)
                return 0;
            return ((long)radiusFixed * radiusFixed) >> 8;
        }

        private static bool NativeUnitTestCollisionSqFixed8(long distanceSqFixed8, long radiusSqFixed8)
        {
            if (radiusSqFixed8 <= 0)
                return false;
            if (distanceSqFixed8 < 0)
                distanceSqFixed8 = 0;
            return distanceSqFixed8 <= radiusSqFixed8;
        }

        public static int ProjectileInitialDistanceF32(int ownerRadiusF32)
        {
            return Math.Max(0, ownerRadiusF32);
        }

        public static int ProjectileLifetimeTicksFixed32(int rangeF32, int speedF32)
        {
            int safeSpeedF32 = Math.Max(0x100, speedF32);
            long ticks = ((long)Math.Max(0, rangeF32) * 30L) / safeSpeedF32;
            return Math.Max(1, (int)ticks);
        }

        public bool TryResolvePlayerWeaponProjectileSourceFixed(
            RRConnection conn,
            PlayerState state,
            int actorFixedX,
            int actorFixedY,
            int actorHeadingFixed,
            out int sourceFixedX,
            out int sourceFixedY,
            out int sourceOffsetFixedX,
            out int sourceOffsetFixedY,
            out int sourceOffsetFixedZ)
        {
            sourceFixedX = actorFixedX;
            sourceFixedY = actorFixedY;
            sourceOffsetFixedX = 0;
            sourceOffsetFixedY = 0;
            sourceOffsetFixedZ = 0;
            if (conn == null || state == null)
                return false;

            var cycle = new WeaponUseState
            {
                Connection = conn,
                PlayerState = state
            };
            ResolveAttackFrames(cycle);
            sourceOffsetFixedX = cycle.AttackSourceOffsetFixedX;
            sourceOffsetFixedY = cycle.AttackSourceOffsetFixedY;
            sourceOffsetFixedZ = cycle.AttackSourceOffsetFixedZ;
            int headingDegrees = UnitMover.WrapDegrees(WrapHeadingFixed(actorHeadingFixed) >> 8);
            int sin = UnitMover.ZRotateSinFixed(headingDegrees);
            int cos = UnitMover.ZRotateCosFixed(headingDegrees);
            sourceFixedX = actorFixedX
                - (int)(((long)cos * sourceOffsetFixedX) >> 8)
                - (int)(((long)sin * sourceOffsetFixedY) >> 8);
            sourceFixedY = actorFixedY
                - (int)(((long)sin * sourceOffsetFixedX) >> 8)
                + (int)(((long)cos * sourceOffsetFixedY) >> 8);
            return cycle.AttackSourceOffsetResolved;
        }

        private static int FixedMultiply8(int leftFixed, int rightFixed)
        {
            return (int)(((long)leftFixed * rightFixed) >> 8);
        }

        private static WeaponDamageInput CreatePlayerWeaponDamageInput(MersenneTwister rng, PlayerState state, Monster monster, string source, uint? attackerEntityId)
        {
            int attackerLevel = Math.Max(0, state?.Level ?? 1);
            int defenderLevel = Math.Max(0, monster?.Level ?? attackerLevel);
            return new WeaponDamageInput
            {
                Rng = rng,
                Source = source,
                AttackerEntityId = attackerEntityId,
                DefenderEntityId = monster?.EntityId,
                AttackerLevel = attackerLevel,
                DefenderLevel = defenderLevel,
                AttackRating = DamageResolver.ResolveAvatarAttackRating(state),
                DefenseRating = DamageResolver.ResolveMonsterDefenseRating(monster),
                BlockChance = 0,
                DamageLevel = DamageResolver.ResolveWeaponDamageLevel(state),
                DamageBonus = DamageResolver.ResolveWeaponDamageBonus(state),
                DamageMod = DamageResolver.ResolveDamageMod(state),
                WeaponClassId = DamageResolver.ResolveWeaponClassId(state),
                DamageTypeId = DamageResolver.ResolveDamageTypeId(state),
                WeaponDamageF32 = DamageResolver.GetWeaponBaseDamageF32(state),
                WeaponVolatilityF32 = DamageResolver.GetWeaponVolatilityF32(state),
                CritThreshold = DamageResolver.ResolveCriticalThreshold(state, monster),
                CritDamagePercent = DamageResolver.ResolveCriticalDamagePercent(state),
                AttackerState = state,
                IncludeWeaponDamageAdds = true
            };
        }

        private static MersenneTwister ResolveRoomRng(WeaponUseState cycle)
        {
            if (!string.IsNullOrWhiteSpace(cycle?.InstanceKey))
                return CombatRuntime.Instance.GetRoomRngForInstance(cycle.InstanceKey);
            RuntimeEvidence.LogFallbackHit("rng-instance", "weapon-use-missing-owner", "source=WeaponUseRuntime.ResolveRoomRng", 64);
            Debug.LogError("[RNG-INSTANCE] source=WeaponUseRuntime.ResolveRoomRng reason=missing-cycle-owner rng=null");
            return null;
        }

        private static string ResolveCycleInstanceKey(WeaponUseState cycle, Monster monster = null, RRConnection conn = null, string source = null)
        {
            if (!string.IsNullOrWhiteSpace(cycle?.InstanceKey))
                return cycle.InstanceKey;
            if (!string.IsNullOrWhiteSpace(monster?.InstanceKey))
                return monster.InstanceKey;
            if (!string.IsNullOrWhiteSpace(cycle?.Monster?.InstanceKey))
                return cycle.Monster.InstanceKey;
            RRConnection ownerConn = conn ?? cycle?.Connection;
            if (!string.IsNullOrWhiteSpace(ownerConn?.RuntimeInstanceKey))
                return ownerConn.RuntimeInstanceKey;

            RuntimeEvidence.LogFallbackHit(
                "rng-instance",
                "cycle-missing-instance",
                $"source={source ?? "unknown"} connZone='{ownerConn?.CurrentZoneName ?? ""}' connInstance={ownerConn?.InstanceId ?? 0u}",
                64);
            Debug.LogError($"[RNG-INSTANCE] source={source ?? "unknown"} reason=cycle-missing-instance rng=null connZone='{ownerConn?.CurrentZoneName ?? ""}' connInstance={ownerConn?.InstanceId ?? 0u}");
            return null;
        }

        private static uint ResolveProjectileViewerEntityId(RRConnection conn)
        {
            return conn?.Avatar != null ? (uint)conn.Avatar.Id : 0u;
        }

        private static int WrapHeadingFixed(int headingFixed)
        {
            headingFixed %= UnitMover.FullCircleFixed;
            if (headingFixed < 0)
                headingFixed += UnitMover.FullCircleFixed;
            return headingFixed;
        }

        private static int ResolveProjectileSourceHeadingFixed(RRConnection conn)
        {
            if (conn == null)
                return 0;
            int headingFixed = conn.HasOwnerAckFollowClientPosition
                ? conn.OwnerAckFollowClientHeadingFixed
                : conn.HasUnitFollowClientPosition
                    ? conn.UnitFollowClientHeadingFixed
                : conn.PlayerHeadingFixed;
            return WrapHeadingFixed(headingFixed);
        }

        private static string ResolvePathMapKey(string instanceKey, string zoneName)
        {
            if (!string.IsNullOrWhiteSpace(instanceKey))
                return instanceKey;
            return zoneName;
        }

        private static WeaponDamageInput CloneWeaponDamageInput(WeaponDamageInput input, MersenneTwister rng, string source)
        {
            if (input == null) return null;
            return new WeaponDamageInput
            {
                Rng = rng,
                Source = source,
                AttackerEntityId = input.AttackerEntityId,
                DefenderEntityId = input.DefenderEntityId,
                AttackerLevel = input.AttackerLevel,
                DefenderLevel = input.DefenderLevel,
                AttackRating = input.AttackRating,
                DefenseRating = input.DefenseRating,
                BlockChance = input.BlockChance,
                DamageLevel = input.DamageLevel,
                DamageBonus = input.DamageBonus,
                DamageMod = input.DamageMod,
                WeaponClassId = input.WeaponClassId,
                DamageTypeId = input.DamageTypeId,
                WeaponDamageF32 = input.WeaponDamageF32,
                WeaponVolatilityF32 = input.WeaponVolatilityF32,
                CritThreshold = input.CritThreshold,
                CritDamagePercent = input.CritDamagePercent,
                AttackerState = input.AttackerState,
                IncludeWeaponDamageAdds = input.IncludeWeaponDamageAdds
            };
        }

        public void RegisterAttack(string connKey, ushort targetId, Monster monster,
            PlayerState playerState, RRConnection conn, bool canStartNow = true, int distanceF32 = 0, int allowedRangeF32 = 0, bool requiresScheduledInitUse = false, int inputAdmissionTick = 0)
        {
            bool reflectedContactHint = canStartNow;
            if (requiresScheduledInitUse)
                canStartNow = false;
            bool clientProjectileRequest = DamageResolver.IsProjectileWeapon(playerState);
            if (clientProjectileRequest && canStartNow &&
                (conn == null || !conn.HasActiveUseTarget || conn.ActiveUseTargetId != targetId || !conn.ActiveUseTargetInitUsePassed))
            {
                canStartNow = false;
                Debug.LogError($"[WEAPON-USE] {connKey} ranged UseTarget awaiting client init-use target={targetId} distF32={distanceF32} rangeF32={allowedRangeF32}");
            }

            if (!_activeCycles.TryGetValue(connKey, out var cycle))
            {
                cycle = new WeaponUseState();
                _activeCycles[connKey] = cycle;
            }
            bool targetSwitchFromActiveCycle = cycle.IsActive
                && !cycle.UsePositionAction
                && cycle.Monster != null
                && (cycle.TargetId != targetId || cycle.Monster.EntityId != (monster?.EntityId ?? 0));
            int previousCycleReadyTick = targetSwitchFromActiveCycle ? cycle.NextUseTick : 0;
            BindPlayerCycle(connKey, conn);
            cycle.UsePositionAction = false;
            cycle.UsePositionManipulatorId = 0;
            cycle.UsePositionTargetFixedX = 0;
            cycle.UsePositionTargetFixedY = 0;
            cycle.UsePositionTargetFixedZ = 0;

            bool sameMonster = cycle.Monster != null && monster != null && cycle.Monster.EntityId == monster.EntityId;
            bool sameTarget = cycle.TargetId == targetId && sameMonster;
            if ((cycle.IsActive || cycle.AwaitingContact) && sameTarget)
            {
                cycle.Monster = monster;
                cycle.PlayerState = playerState;
                cycle.Connection = conn;
                cycle.InstanceKey = ResolveCycleInstanceKey(cycle, monster, conn, "RegisterAttack-update");
                CaptureUseTargetState(cycle, conn, distanceF32, allowedRangeF32);
                ApplyUseCooldownLedger(connKey, cycle, NowTick);
                if (requiresScheduledInitUse)
                {
                    if (cycle.IsActive)
                        QueueRepeatUse(connKey, cycle, monster);
                    else if (cycle.AwaitingContact)
                    {
                        cycle.ServerApproachOnly = true;
                        if (reflectedContactHint)
                            QueueRepeatUse(connKey, cycle, monster);
                    }
                    Debug.LogError($"[WEAPON-USE] {connKey} reflected UseTarget deferred to scheduled 3D init-use target={targetId} active={cycle.IsActive} awaiting={cycle.AwaitingContact} contactHint={reflectedContactHint}");
                    return;
                }
                if (cycle.IsActive && canStartNow)
                {
                    if (IsRangedCycle(cycle))
                    {
                        QueueRepeatUse(connKey, cycle, monster);
                        return;
                    }
                    QueueRepeatUse(connKey, cycle, monster);
                }
                if (canStartNow && cycle.AwaitingContact)
                {
                    cycle.ServerApproachOnly = false;
                    int nowTick = NowTick;
                    if (!IsUseReady(cycle, nowTick) || !IsUseTargetUseAllowed(cycle, nowTick))
                    {
                        if (IsRangedCycle(cycle))
                        {
                            QueueRepeatUse(connKey, cycle, monster);
                            Debug.LogError($"[WEAPON-USE] {connKey} redundant ranged UseTarget while awaiting contact on {monster.Name} nextInTicks={cycle.NextUseTick - nowTick} sourceFunction=UseTarget::IsRedundant pendingRepeat={cycle.PendingRepeatUses}");
                        }
                        else
                        {
                            QueueRepeatUse(connKey, cycle, monster);
                        }
                        cycle.IsActive = false;
                        cycle.AwaitingContact = true;
                        cycle.LastSimulationTick = nowTick;
                        Debug.LogError($"[WEAPON-USE] {connKey} -> cooldown hold on {monster.Name} nextInTicks={cycle.NextUseTick - nowTick}");
                        return;
                    }
                    BeginCycle(connKey, cycle, monster, targetId, nowTick);
                    Debug.LogError($"[WEAPON-USE] {connKey} -> CONTACT cycle on {monster.Name} distF32={distanceF32} rangeF32={allowedRangeF32}");
                }
                else
                {
                    string mode = cycle.AwaitingContact ? "approach" : "continuation swing";
                    Debug.LogError($"[WEAPON-USE] {connKey} -> {mode} on {monster.Name} pendingRepeat={cycle.PendingRepeatUses}");
                }
                return;
            }

            cycle.TargetId = targetId;
            cycle.Monster = monster;
            cycle.PlayerState = playerState;
            cycle.Connection = conn;
            cycle.InstanceKey = ResolveCycleInstanceKey(cycle, monster, conn, "RegisterAttack-new");
            cycle.TickCounter = 0;
            ResetSwingRngState(cycle);
            cycle.SwingCount = 0;
            cycle.PendingRepeatUses = 0;
            cycle.RepeatUsePendingAtRelease = false;
            CaptureUseTargetState(cycle, conn, distanceF32, allowedRangeF32);
            cycle.ServerApproachOnly = !canStartNow;
            cycle.ContactHoldLogged = false;
            int registrationTick = NowTick;
            cycle.LastSimulationTick = requiresScheduledInitUse ? registrationTick - 1 : registrationTick;
            cycle.InputAdmissionTick = requiresScheduledInitUse ? inputAdmissionTick : registrationTick;
            cycle.CycleStartTick = 0;
            cycle.NextUseTick = 0;
            ApplyUseCooldownLedger(connKey, cycle, registrationTick);
            cycle.UseTargetUseNotBeforeTick = ResolveTargetSwitchUseNotBeforeTick(previousCycleReadyTick, registrationTick);

            if (canStartNow && IsUseReady(cycle, registrationTick) && IsUseTargetUseAllowed(cycle, registrationTick))
            {
                BeginCycle(connKey, cycle, monster, targetId, registrationTick);
                Debug.LogError($"[WEAPON-USE] {connKey} -> NEW cycle on {monster.Name} (ID:{targetId})");
            }
            else
            {
                cycle.IsActive = false;
                cycle.AwaitingContact = true;
                string mode = canStartNow ? "cooldown hold" : "APPROACH intent";
                Debug.LogError($"[WEAPON-USE] {connKey} -> {mode} on {monster.Name} (ID:{targetId}) distF32={distanceF32} rangeF32={allowedRangeF32} readyTick={cycle.NextUseTick}");
            }
        }

        public bool RegisterUsePosition(
            string connKey,
            PlayerState playerState,
            RRConnection conn,
            ushort componentId,
            byte sessionId,
            byte manipulatorId,
            int targetFixedX,
            int targetFixedY,
            int targetFixedZ,
            uint simulationTick)
        {
            if (string.IsNullOrEmpty(connKey)
                || conn == null
                || playerState == null
                || (manipulatorId != 0x0A && manipulatorId != 0x0B))
                return false;
            if (!_activeCycles.TryGetValue(connKey, out WeaponUseState cycle))
            {
                cycle = new WeaponUseState();
                _activeCycles[connKey] = cycle;
            }
            BindPlayerCycle(connKey, conn);
            cycle.TargetId = 0;
            cycle.Monster = null;
            cycle.PlayerState = playerState;
            cycle.Connection = conn;
            cycle.InstanceKey = null;
            cycle.InstanceKey = ResolveCycleInstanceKey(cycle, null, conn, "RegisterUsePosition");
            cycle.TickCounter = 0;
            cycle.SwingCount = 0;
            cycle.PendingRepeatUses = 0;
            cycle.RepeatUsePendingAtRelease = false;
            cycle.AwaitingContact = false;
            cycle.ServerApproachOnly = false;
            cycle.UseTargetUseNotBeforeTick = 0;
            cycle.ContactHoldLogged = false;
            cycle.InitUsePassed = true;
            cycle.InitUseRangeF32 = CombatRuntime.Instance.ResolvePlayerWeaponTargetClearRangeF32(playerState, null);
            cycle.InitUseDistanceF32 = 0;
            cycle.InitUseToleranceF32 = 0;
            cycle.UseTargetComponentId = componentId;
            cycle.UseTargetSessionId = sessionId;
            cycle.UseTargetFlags = manipulatorId;
            cycle.InputAdmissionTick = unchecked((int)simulationTick);
            cycle.CycleStartTick = 0;
            cycle.LastSimulationTick = unchecked((int)simulationTick);
            cycle.UsePositionAction = true;
            cycle.UsePositionManipulatorId = manipulatorId;
            cycle.UsePositionTargetFixedX = targetFixedX;
            cycle.UsePositionTargetFixedY = targetFixedY;
            cycle.UsePositionTargetFixedZ = targetFixedZ;
            ResetSwingRngState(cycle);
            ApplyUseCooldownLedger(connKey, cycle, unchecked((int)simulationTick));
            BeginCycle(connKey, cycle, null, 0, unchecked((int)simulationTick));
            Debug.LogError($"[WEAPON-USE-POSITION] conn={connKey} manipulatorId={manipulatorId} targetFixed=({targetFixedX},{targetFixedY},{targetFixedZ}) tick={simulationTick} animation={cycle.AttackAnimationId} cycleTicks={GetCycleTicks(cycle)} sourceFunction=UsePosition::States@0x00547360->Weapon::use(int,int,int)");
            return cycle.IsActive;
        }

        public bool IsUsePositionBusy(uint playerEntityId)
        {
            return TryGetPlayerCycle(playerEntityId, out _, out WeaponUseState cycle)
                && cycle.UsePositionAction
                && cycle.IsActive;
        }

        public bool TryGetUsePositionNaturalTerminationTick(uint playerEntityId, out uint terminationTick)
        {
            terminationTick = 0;
            if (!TryGetPlayerCycle(playerEntityId, out _, out WeaponUseState cycle)
                || cycle == null
                || !cycle.UsePositionAction
                || !cycle.IsActive
                || cycle.CycleStartTick < 0)
                return false;
            long dueTick = (long)cycle.CycleStartTick + GetCycleTicks(cycle);
            if (dueTick < 0 || dueTick > uint.MaxValue)
                return false;
            terminationTick = (uint)dueTick;
            return true;
        }

        private void ClearUsePositionAction(string connKey, string source, bool cancelled)
        {
            if (string.IsNullOrEmpty(connKey)
                || !_activeCycles.TryGetValue(connKey, out WeaponUseState cycle)
                || cycle == null
                || !cycle.UsePositionAction)
                return;
            RememberUseCooldown(connKey, cycle.NextUseTick);
            RemovePlayerCycleBinding(connKey, cycle);
            _activeCycles.Remove(connKey);
            Debug.LogError($"[WEAPON-USE-POSITION] conn={connKey} state={(cancelled ? "cancelled" : "complete")} source={source ?? "unknown"} readyTick={cycle.NextUseTick} sourceFunction=Weapon::isBusy");
        }

        public void CancelUsePositionAction(string connKey, string source)
        {
            if (string.IsNullOrEmpty(connKey)
                || !_activeCycles.TryGetValue(connKey, out WeaponUseState cycle)
                || cycle == null
                || !cycle.UsePositionAction)
                return;
            if (cycle.IsActive)
            {
                cycle.PendingRepeatUses = 0;
                cycle.RepeatUsePendingAtRelease = false;
                Debug.LogError($"[WEAPON-USE-POSITION] conn={connKey} state=behavior-cancelled weaponPreserved=True source={source ?? "unknown"} readyTick={cycle.NextUseTick} sourceFunction=Behavior::processUpdate@0x00515620 opcode=0x03");
                return;
            }
            ClearUsePositionAction(connKey, source, true);
        }

        public void CompleteUsePositionAction(string connKey, string source)
        {
            ClearUsePositionAction(connKey, source, false);
        }

        private static void CaptureUseTargetState(WeaponUseState cycle, RRConnection conn, int distanceF32, int allowedRangeF32)
        {
            if (cycle == null) return;
            cycle.InitUsePassed = conn != null && conn.ActiveUseTargetInitUsePassed;
            cycle.InitUseRangeF32 = conn != null && conn.ActiveUseTargetInitUseRangeF32 > 0 ? conn.ActiveUseTargetInitUseRangeF32 : allowedRangeF32;
            cycle.InitUseDistanceF32 = conn != null && conn.ActiveUseTargetInitUseDistanceF32 > 0 ? conn.ActiveUseTargetInitUseDistanceF32 : distanceF32;
            cycle.InitUseToleranceF32 = conn != null ? conn.ActiveUseTargetClientToleranceF32 : 0;
            cycle.UseTargetComponentId = conn != null ? conn.ActiveUseTargetComponentId : (ushort)0;
            cycle.UseTargetSessionId = conn != null ? conn.ActiveUseTargetSessionId : (byte)0;
            cycle.UseTargetFlags = conn != null ? conn.ActiveUseTargetFlags : (byte)0;
        }

        private void QueueRepeatUse(string connKey, WeaponUseState cycle, Monster monster)
        {
            if (cycle == null || monster == null || !monster.IsAlive) return;
            int before = cycle.PendingRepeatUses;
            cycle.PendingRepeatUses = cycle.PendingRepeatUses >= MAX_PENDING_REPEAT_USES ? MAX_PENDING_REPEAT_USES : cycle.PendingRepeatUses + 1;
            bool projectile = IsProjectileRangedCycle(cycle);
            if (cycle.PendingRepeatUses != before)
                Debug.LogError($"[WEAPON-USE] {connKey} queued repeat UseTarget on {monster.Name} pending={cycle.PendingRepeatUses} projectile={projectile} sourceFunction=UseTarget::IsRedundant clientPendingSlot=Behavior+0x78 coalesced=next-client-use");
            else
                Debug.LogError($"[WEAPON-USE] {connKey} repeat UseTarget coalesced on {monster.Name} pending={cycle.PendingRepeatUses} projectile={projectile} sourceFunction=UseTarget::IsRedundant clientPendingSlot=Behavior+0x78 coalesced=existing-pending-use");
        }

        public Monster GetActiveTarget(string playerKey)
        {
            if (_activeCycles.TryGetValue(playerKey, out var cycle) && (cycle.IsActive || cycle.AwaitingContact))
                return cycle.Monster;
            return null;
        }

        public int GetUseTargetSwitchAdmissionDelayTicks(string connKey, ushort targetId)
        {
            if (string.IsNullOrEmpty(connKey)
                || !_activeCycles.TryGetValue(connKey, out WeaponUseState cycle)
                || cycle == null
                || !cycle.IsActive
                || cycle.UsePositionAction
                || cycle.TargetId == targetId)
                return 0;
            int nowTick = NowTick;
            return cycle.NextUseTick > nowTick ? cycle.NextUseTick - nowTick : 0;
        }

        public bool HasActivePlayerWeaponAction(uint playerEntityId)
        {
            return TryGetPlayerCycle(playerEntityId, out _, out WeaponUseState cycle)
                && cycle.IsActive;
        }

        public bool ConsumePlayerWeaponRepeatAtRelease(uint playerEntityId, uint targetEntityId)
        {
            if (!TryGetPlayerCycle(playerEntityId, out _, out WeaponUseState cycle)
                || !cycle.RepeatUsePendingAtRelease
                || !cycle.AwaitingContact
                || cycle.TargetId != targetEntityId)
                return false;
            cycle.RepeatUsePendingAtRelease = false;
            return true;
        }

        public bool TryGetPendingPlayerWeaponHitTick(uint playerEntityId, uint targetEntityId, out uint hitTick)
        {
            hitTick = 0;
            if (playerEntityId == 0
                || targetEntityId == 0
                || !TryGetPlayerCycle(playerEntityId, out _, out WeaponUseState cycle)
                || !cycle.IsActive
                || cycle.HitFired
                || cycle.CycleStartTick <= 0
                || cycle.Monster == null
                || cycle.Monster.EntityId != targetEntityId
                || IsProjectileRangedCycle(cycle))
                return false;

            long candidateTick = (long)cycle.CycleStartTick + GetHitEventTick(cycle);
            if (candidateTick < 0 || candidateTick > uint.MaxValue)
                return false;
            hitTick = (uint)candidateTick;
            return true;
        }

        public bool WasDirectPlayerWeaponHitAppliedAtTick(uint playerEntityId, uint targetEntityId, uint simulationTick)
        {
            return playerEntityId != 0
                && targetEntityId != 0
                && simulationTick != 0
                && TryGetPlayerCycle(playerEntityId, out _, out WeaponUseState cycle)
                && cycle.Monster != null
                && cycle.Monster.EntityId == targetEntityId
                && cycle.LastDirectHitSimulationTick == simulationTick;
        }

        public bool WasDirectPlayerWeaponHitAppliedAtTick(uint playerEntityId, uint simulationTick)
        {
            return playerEntityId != 0
                && simulationTick != 0
                && TryGetPlayerCycle(playerEntityId, out _, out WeaponUseState cycle)
                && cycle.LastDirectHitSimulationTick == simulationTick;
        }

        public bool TryGetRecentDirectPlayerWeaponHit(uint playerEntityId, uint targetEntityId, uint simulationTick, uint maxAgeTicks, out uint hitTick, out uint previousHPWire, out uint newHPWire)
        {
            hitTick = 0;
            previousHPWire = 0;
            newHPWire = 0;
            if (playerEntityId == 0
                || targetEntityId == 0
                || simulationTick == 0
                || !TryGetPlayerCycle(playerEntityId, out _, out WeaponUseState cycle)
                || cycle.Monster == null
                || cycle.Monster.EntityId != targetEntityId
                || IsProjectileRangedCycle(cycle)
                || cycle.LastDirectHitSimulationTick == 0
                || cycle.LastDirectHitSimulationTick > simulationTick
                || simulationTick - cycle.LastDirectHitSimulationTick > maxAgeTicks)
                return false;
            hitTick = cycle.LastDirectHitSimulationTick;
            previousHPWire = cycle.LastDirectHitPreviousHPWire;
            newHPWire = cycle.LastDirectHitNewHPWire;
            return previousHPWire > newHPWire;
        }

        public void TickPlayerWeaponManipulatorChild(uint playerEntityId, uint simulationTick)
        {
            if (!TryGetPlayerCycle(playerEntityId, out string connKey, out WeaponUseState cycle))
                return;
            if (!cycle.IsActive)
                return;
            AdvanceCycleToTick(connKey, cycle, ResolveRoomRng(cycle), (int)simulationTick);
        }

        public void ProcessPlayerUseTargetInput(uint playerEntityId, uint simulationTick)
        {
            if (!TryGetPlayerCycle(playerEntityId, out _, out WeaponUseState cycle))
                return;
            if (cycle.AwaitingContact && IsRangedCycle(cycle))
                TryBeginPlayerWeaponEntity(playerEntityId, simulationTick);
            if (!cycle.IsActive
                || cycle.CycleStartTick != unchecked((int)simulationTick)
                || cycle.TickCounter != 0)
                return;
            cycle.LastSimulationTick = unchecked((int)simulationTick - 1);
            Debug.LogError($"[WEAPON-INPUT-ADMISSION] player={playerEntityId} tick={simulationTick} cycleStart={cycle.CycleStartTick} childEligible=True sourceFunction=Behavior::processUpdate@0x00515620->UseTarget::States@0x00548370->Weapon::use");
        }

        public bool BeginPlayerMeleeUseTargetState(uint playerEntityId, uint targetEntityId, uint simulationTick)
        {
            if (!TryGetPlayerCycle(playerEntityId, out string connKey, out WeaponUseState cycle)
                || cycle.IsActive
                || !cycle.AwaitingContact
                || IsRangedCycle(cycle)
                || cycle.Monster == null
                || cycle.Monster.EntityId != targetEntityId
                || cycle.TargetId != targetEntityId
                || cycle.Connection == null
                || !cycle.Connection.HasActiveUseTarget
                || cycle.Connection.ActiveUseTargetId != targetEntityId
                || cycle.Connection.ActiveUseTargetInitUseEvaluationTick != simulationTick
                || cycle.Connection.ActiveUseTargetInitUseEvaluationTargetId != targetEntityId
                || !cycle.Connection.ActiveUseTargetInitUsePassed
                || !IsUseTargetUseAllowed(cycle, unchecked((int)simulationTick))
                || !IsUseReady(cycle, (int)simulationTick))
                return false;
            BeginCycle(connKey, cycle, cycle.Monster, cycle.TargetId, (int)simulationTick);
            Debug.LogError($"[WEAPON-USE] {connKey} -> CONTACT cycle from UseTarget::States state=0x18 target={targetEntityId} inputAdmissionTick={cycle.InputAdmissionTick} cycleStartTick={simulationTick} sourceFunction=UseTarget::States@0x00548370->MeleeWeapon::use@0x005917D0");
            return true;
        }

        public bool TryBeginDeferredPlayerMeleeUseTargetState(uint playerEntityId, uint targetEntityId, uint simulationTick)
        {
            if (!TryGetPlayerCycle(playerEntityId, out string connKey, out WeaponUseState cycle)
                || cycle.IsActive
                || !cycle.AwaitingContact
                || cycle.UseTargetUseNotBeforeTick <= 0
                || unchecked((int)simulationTick) < cycle.UseTargetUseNotBeforeTick
                || IsRangedCycle(cycle)
                || cycle.Monster == null
                || cycle.Monster.EntityId != targetEntityId
                || cycle.TargetId != targetEntityId
                || cycle.Connection == null
                || !cycle.Connection.HasActiveUseTarget
                || cycle.Connection.ActiveUseTargetId != targetEntityId
                || cycle.Connection.ActiveUseTargetInitUseEvaluationTargetId != targetEntityId
                || !cycle.Connection.ActiveUseTargetInitUsePassed)
                return false;
            int tick = unchecked((int)simulationTick);
            if (!IsUseReady(cycle, tick))
                return false;
            cycle.ServerApproachOnly = false;
            if (!HasPlayerMeleeContact(cycle, tick, out _, out _))
                return false;
            cycle.UseTargetUseNotBeforeTick = 0;
            BeginCycle(connKey, cycle, cycle.Monster, cycle.TargetId, tick);
            if (!cycle.IsActive)
                return false;
            Debug.LogError($"[WEAPON-USE] {connKey} -> CONTACT cycle from delayed UseTarget::States state=0x18 target={targetEntityId} inputAdmissionTick={cycle.InputAdmissionTick} cycleStartTick={simulationTick} sourceFunction=UseTarget::States@0x00548370->MeleeWeapon::use@0x005917D0");
            return true;
        }

        public void TryBeginPlayerWeaponEntity(uint playerEntityId, uint simulationTick)
        {
            if (!TryGetPlayerCycle(playerEntityId, out string connKey, out WeaponUseState cycle)
                || !cycle.AwaitingContact
                || !IsRangedCycle(cycle))
                return;
            AdvanceCycleToTick(connKey, cycle, ResolveRoomRng(cycle), (int)simulationTick);
        }

        private bool TryGetPlayerCycle(uint playerEntityId, out string connKey, out WeaponUseState cycle)
        {
            cycle = null;
            if (!_cycleKeyByPlayerEntity.TryGetValue(playerEntityId, out connKey))
                return false;
            return _activeCycles.TryGetValue(connKey, out cycle) && cycle != null;
        }

        private void BindPlayerCycle(string connKey, RRConnection conn)
        {
            if (string.IsNullOrEmpty(connKey) || conn?.Avatar == null)
                return;
            uint playerEntityId = (uint)conn.Avatar.Id;
            if (_playerEntityByCycleKey.TryGetValue(connKey, out uint previousEntityId) && previousEntityId != playerEntityId)
            {
                if (_cycleKeyByPlayerEntity.TryGetValue(previousEntityId, out string previousKey) && string.Equals(previousKey, connKey, StringComparison.Ordinal))
                    _cycleKeyByPlayerEntity.Remove(previousEntityId);
            }
            _playerEntityByCycleKey[connKey] = playerEntityId;
            _cycleKeyByPlayerEntity[playerEntityId] = connKey;
        }

        private void RemovePlayerCycleBinding(string connKey, WeaponUseState cycle)
        {
            uint playerEntityId = 0;
            if (!string.IsNullOrEmpty(connKey) && _playerEntityByCycleKey.TryGetValue(connKey, out uint mappedEntityId))
                playerEntityId = mappedEntityId;
            else if (cycle?.Connection?.Avatar != null)
                playerEntityId = (uint)cycle.Connection.Avatar.Id;
            if (playerEntityId != 0 && _cycleKeyByPlayerEntity.TryGetValue(playerEntityId, out string mappedKey) && string.Equals(mappedKey, connKey, StringComparison.Ordinal))
                _cycleKeyByPlayerEntity.Remove(playerEntityId);
            if (!string.IsNullOrEmpty(connKey))
                _playerEntityByCycleKey.Remove(connKey);
        }

        private void AdvanceCycleToTick(string connKey, WeaponUseState cycle, MersenneTwister rng, int simulationTick)
        {
            if (cycle == null) return;
            if (!cycle.IsActive && !cycle.AwaitingContact) return;
            if (cycle.LastSimulationTick >= simulationTick) return;
            cycle.LastSimulationTick = simulationTick;
            TickCycle(connKey, cycle, rng, simulationTick);
        }

        public void TickProjectileSubEntity(long sequence, string instanceKey, uint simulationTick, string source = null)
        {
            Predicate<PendingProjectileHit> onlySequence = pending => pending != null && pending.Sequence == sequence;
            UpdateActiveProjectileSubEntities(null, (int)simulationTick, source ?? "Projectile::update", onlySequence, onlySequence);
            if (!_activeProjectiles.Exists(pending => pending != null && pending.Sequence == sequence))
                CombatRuntime.Instance.RemoveClientSubEntity(ClientSubEntityKind.PlayerWeaponProjectile, sequence, instanceKey);
        }

        private void RemoveProjectileRuntime(long sequence)
        {
            _activeProjectiles.RemoveAll(pending => pending != null && pending.Sequence == sequence);
        }

        private int GetSpeedField(WeaponUseState cycle)
        {
            return DamageResolver.ResolveWeaponSpeedField(cycle?.PlayerState);
        }

    private int GetTickPosition(WeaponUseState cycle, int defaultPosition)
    {
            if (defaultPosition <= 0)
                return 0;
            return Math.Max(1, (defaultPosition * 100) / GetSpeedField(cycle));
        }

    private int GetCycleTickCount(WeaponUseState cycle, int defaultPosition)
    {
            if (defaultPosition <= 0)
                return 0;
            return Math.Max(1, (defaultPosition * 100) / GetSpeedField(cycle));
        }

    private int GetCycleTicks(WeaponUseState cycle)
    {
            if (cycle == null || !cycle.AttackTimingResolved)
                return 0;
            int ticks = GetCycleTickCount(cycle, cycle.AttackTotalFrames);
            return IsRangedCycle(cycle) ? ticks * GetRangedBurstCount(cycle) : ticks;
        }

    private int GetSingleCycleTicks(WeaponUseState cycle)
    {
            return cycle == null || !cycle.AttackTimingResolved
                ? 0
                : GetCycleTickCount(cycle, cycle.AttackTotalFrames);
        }

        private int GetRangedBurstCount(WeaponUseState cycle)
        {
            int count = cycle?.PlayerState != null ? cycle.PlayerState.WeaponBurstCount : 1;
            return Math.Max(1, count);
        }

    private int GetHitTick(WeaponUseState cycle)
    {
            return cycle == null || !cycle.AttackTimingResolved
                ? 0
                : GetTickPosition(cycle, cycle.AttackHitFrame);
        }

    private int GetSoundTick(WeaponUseState cycle)
    {
            return cycle == null || !cycle.AttackTimingResolved
                ? 0
                : GetTickPosition(cycle, cycle.AttackSoundFrame);
        }

        private int GetHitEventTick(WeaponUseState cycle)
        {
            return GetHitTick(cycle);
        }

        private int GetSoundEventTick(WeaponUseState cycle)
        {
            return GetSoundTick(cycle);
        }

        private int GetRangedHitEventTick(WeaponUseState cycle, int burstIndex)
        {
            return burstIndex * GetSingleCycleTicks(cycle) + GetHitTick(cycle) + 1;
        }

        private int GetRangedSoundEventTick(WeaponUseState cycle, int burstIndex)
        {
            return burstIndex * GetSingleCycleTicks(cycle) + GetSoundTick(cycle) + 1;
        }

        private int GetCooldownTicks(WeaponUseState cycle)
        {
            return DamageResolver.ResolveBasicAttackCooldownTicks(cycle?.PlayerState);
        }

        private bool IsUseReady(WeaponUseState cycle, int simulationTick)
        {
            return cycle == null || cycle.NextUseTick <= 0 || simulationTick >= cycle.NextUseTick;
        }

        private static bool IsUseTargetUseAllowed(WeaponUseState cycle, int simulationTick)
        {
            return cycle == null || cycle.UseTargetUseNotBeforeTick <= 0 || simulationTick >= cycle.UseTargetUseNotBeforeTick;
        }

        private static int ResolveTargetSwitchUseNotBeforeTick(int previousCycleReadyTick, int registrationTick)
        {
            if (previousCycleReadyTick <= registrationTick)
                return 0;
            long notBeforeTick = (long)previousCycleReadyTick + CLIENT_USE_TARGET_STATE2_DELAY_TICKS;
            return notBeforeTick >= int.MaxValue ? int.MaxValue : (int)notBeforeTick;
        }

        public bool ValidateUse(string connKey, uint simulationTick, out int readyTick)
        {
            readyTick = 0;
            if (string.IsNullOrEmpty(connKey) || !_activeCycles.TryGetValue(connKey, out WeaponUseState cycle) || cycle == null)
                return false;
            int tick = unchecked((int)simulationTick);
            ApplyUseCooldownLedger(connKey, cycle, tick);
            readyTick = cycle.NextUseTick;
            return IsUseReady(cycle, tick);
        }

        private void ApplyUseCooldownLedger(string connKey, WeaponUseState cycle, int simulationTick)
        {
            if (string.IsNullOrEmpty(connKey) || cycle == null)
                return;
            if (!_nextUseReadyTickByConnection.TryGetValue(connKey, out int readyTick))
                return;
            if (readyTick <= simulationTick)
            {
                _nextUseReadyTickByConnection.Remove(connKey);
                return;
            }
            if (readyTick > cycle.NextUseTick)
                cycle.NextUseTick = readyTick;
        }

        private void RememberUseCooldown(string connKey, int readyTick)
        {
            if (string.IsNullOrEmpty(connKey) || readyTick <= 0)
                return;
            if (!_nextUseReadyTickByConnection.TryGetValue(connKey, out int current) || readyTick > current)
                _nextUseReadyTickByConnection[connKey] = readyTick;
        }

        private void BeginCycle(string connKey, WeaponUseState cycle, Monster monster, ushort targetId)
        {
            BeginCycle(connKey, cycle, monster, targetId, NowTick);
        }

        private void BeginCycle(string connKey, WeaponUseState cycle, Monster monster, ushort targetId, int simulationTick)
        {
            cycle.IsActive = true;
            cycle.TargetDiedDuringCycle = false;
            cycle.AwaitingContact = false;
            cycle.ServerApproachOnly = false;
            cycle.ContactHoldLogged = false;
            cycle.RepeatUsePendingAtRelease = false;
            cycle.TargetId = targetId;
            cycle.Monster = monster;
            cycle.InstanceKey = ResolveCycleInstanceKey(cycle, monster, cycle.Connection, "BeginCycle");
            cycle.UseTargetUseNotBeforeTick = 0;
            cycle.TickCounter = 0;
            cycle.LastDirectHitSimulationTick = 0;
            cycle.LastDirectHitPreviousHPWire = 0;
            cycle.LastDirectHitNewHPWire = 0;
            ResetSwingRngState(cycle);
            cycle.LastSimulationTick = simulationTick;
            cycle.CycleStartTick = simulationTick;
            if (cycle.Connection != null && cycle.Connection.HasActiveUseTarget && cycle.Connection.ActiveUseTargetId == targetId)
                cycle.Connection.ActiveUseTargetStartedWeaponUse = true;
            ConsumeUseRng(connKey, cycle, ResolveRoomRng(cycle));
            ResolveAttackFrames(cycle);
            if (!cycle.AttackTimingResolved)
            {
                cycle.IsActive = false;
                cycle.AwaitingContact = false;
                cycle.NextUseTick = 0;
                Debug.LogError($"[WEAPON-USE] {connKey} blocked reason=attack-timing-unresolved animation={cycle.AttackAnimationId} sourceFunction=Weapon::computeAttackTicks@0x00598E50");
                return;
            }
            SnapshotProjectileTargetFixed(cycle);
            int cooldownTicks = GetCooldownTicks(cycle);
            cycle.NextUseTick = simulationTick + cooldownTicks;
            RememberUseCooldown(connKey, cycle.NextUseTick);
            if (IsProjectileRangedCycle(cycle))
            {
                cycle.InitUsePassed = true;
                if (!cycle.UsePositionAction
                    && cycle.Connection != null
                    && cycle.Connection.HasActiveUseTarget
                    && cycle.Connection.ActiveUseTargetId == targetId)
                {
                    cycle.Connection.ActiveUseTargetStartedWeaponUse = true;
                    cycle.Connection.ActiveUseTargetInitUsePassed = true;
                }
                Debug.LogError($"[RANGED-USE-START] conn={connKey} target={monster?.EntityId ?? 0} component={cycle.UseTargetComponentId} session={cycle.UseTargetSessionId} initUsePassed={cycle.InitUsePassed} initUseRangeF32={cycle.InitUseRangeF32} initUseDistF32={cycle.InitUseDistanceF32} toleranceF32={cycle.InitUseToleranceF32} tick={simulationTick} rngAdvanced=False");
            }
            Debug.LogError($"[WEAPON-USE-RATE] {connKey} anim={cycle.AttackAnimationId} frames total={cycle.AttackTotalFrames} hit={cycle.AttackHitFrame} sound={cycle.AttackSoundFrame} speed={GetSpeedField(cycle)} speedPctF32={DamageResolver.ResolveWeaponAttackSpeedPctF32(cycle.PlayerState)} animationTicks={GetCycleTicks(cycle)} useCooldownTicks={cooldownTicks} readyTick={cycle.NextUseTick} class={cycle.PlayerState?.WeaponClass ?? "unknown"} category={cycle.PlayerState?.WeaponCategory ?? "unknown"} useProjectile={cycle.PlayerState?.WeaponUsesProjectile ?? false} burst={cycle.PlayerState?.WeaponBurstCount ?? 1} sourceFunction=Weapon::use+0x86/RangedWeapon::update+0x8e");
        }

        private static void SnapshotProjectileTargetFixed(WeaponUseState cycle)
        {
            if (cycle == null)
                return;
            cycle.ProjectileTargetSnapshotResolved = false;
            cycle.ProjectileTargetFixedX = 0;
            cycle.ProjectileTargetFixedY = 0;
            if (!IsProjectileRangedCycle(cycle))
                return;

            if (cycle.UsePositionAction)
            {
                cycle.ProjectileTargetFixedX = cycle.UsePositionTargetFixedX;
                cycle.ProjectileTargetFixedY = cycle.UsePositionTargetFixedY;
                cycle.ProjectileTargetSnapshotResolved = true;
                return;
            }
            if (cycle.Monster == null)
                return;

            int targetFixedX = cycle.Monster.PosFixedX;
            int targetFixedY = cycle.Monster.PosFixedY;
            uint viewerEntityId = ResolveProjectileViewerEntityId(cycle.Connection);
            CombatRuntime.Instance.TryGetMonsterClientVisiblePositionFixed(
                cycle.Monster,
                viewerEntityId,
                out targetFixedX,
                out targetFixedY);
            targetFixedX &= ~0xFF;
            targetFixedY &= ~0xFF;
            cycle.ProjectileTargetFixedX = targetFixedX;
            cycle.ProjectileTargetFixedY = targetFixedY;
            cycle.ProjectileTargetSnapshotResolved = true;
        }

        private void ResetSwingRngState(WeaponUseState cycle)
        {
            if (cycle == null) return;
            cycle.ProcFired = false;
            cycle.HitFired = false;
            cycle.AttackSoundFired = false;
            cycle.RangedHitEventsFired = 0;
            cycle.RangedSoundEventsFired = 0;
            cycle.UseRngConsumed = false;
            cycle.UseRaw = 0;
            cycle.AttackSoundSelectRaw = 0;
            cycle.AttackSoundGateRaw = 0;
            cycle.AttackSoundRepeatRaw = 0;
            cycle.ImpactSoundRaw = 0;
        }

    private void ResolveAttackFrames(WeaponUseState cycle)
    {
            if (cycle == null)
                return;

            cycle.AttackTimingResolved = false;
            cycle.AttackTotalFrames = 0;
            cycle.AttackHitFrame = 0;
            cycle.AttackSoundFrame = 0;
            int animationSelector = ResolveAttackAnimationSelector(cycle);
            cycle.AttackAnimationId = ResolveAttackAnimationId(cycle, animationSelector);
            cycle.AttackSourceOffsetFixedX = 0;
            cycle.AttackSourceOffsetFixedY = 0;
            cycle.AttackSourceOffsetFixedZ = 12 * UnitMover.Fixed;
            cycle.AttackSourceOffsetResolved = false;

            if (SpellDatabase.TryResolvePlayerWeaponAnimation(
                animationSelector,
                cycle.PlayerState,
                out int animationId,
                out int frames,
                out int hitFrame,
                out int soundFrame,
                out int sourceOffsetFixedX,
                out int sourceOffsetFixedY,
                out int sourceOffsetFixedZ))
            {
                cycle.AttackAnimationId = animationId;
                cycle.AttackTotalFrames = frames;
                cycle.AttackHitFrame = hitFrame;
                cycle.AttackSoundFrame = soundFrame;
                cycle.AttackSourceOffsetFixedX = sourceOffsetFixedX;
                cycle.AttackSourceOffsetFixedY = sourceOffsetFixedY;
                cycle.AttackSourceOffsetFixedZ = sourceOffsetFixedZ;
                cycle.AttackSourceOffsetResolved = true;
                cycle.AttackTimingResolved = true;
            }
        }

        private static int ResolveAttackAnimationSelector(WeaponUseState cycle)
        {
            if (IsRangedCycle(cycle))
                return cycle?.PlayerState?.WeaponShotType != 0 ? 15 : 10;
            int selector = 10 + (cycle?.AttackAnimationIndex ?? 0);
            if (cycle?.PlayerState?.WeaponEquipmentSlot == 11)
                selector += 3;
            return selector;
        }

        private static int ResolveAttackAnimationId(WeaponUseState cycle, int selector)
        {
            int weaponClassId = DamageResolver.ResolveWeaponClassId(cycle?.PlayerState);
            return weaponClassId > 0 ? weaponClassId * 100 + selector : selector;
        }

        private static bool IsRangedCycle(WeaponUseState cycle)
        {
            return cycle?.PlayerState != null && DamageResolver.IsRangedWeapon(cycle.PlayerState);
        }

        private static bool IsProjectileRangedCycle(WeaponUseState cycle)
        {
            int projectileSpeedF32 = cycle?.PlayerState?.WeaponProjectileSpeedF32 ?? 0;
            int projectileSizeF32 = cycle?.PlayerState?.WeaponProjectileSizeF32 ?? 0;

            return IsRangedCycle(cycle) &&
                DamageResolver.IsProjectileWeapon(cycle.PlayerState) &&
                projectileSpeedF32 > 0 &&
                projectileSizeF32 > 0;
        }

        private void ConsumeUseRng(string connKey, WeaponUseState cycle, MersenneTwister rng)
        {
            if (cycle == null || rng == null || cycle.UseRngConsumed) return;
            if (cycle.PlayerState != null && DamageResolver.IsRangedWeapon(cycle.PlayerState))
            {
                cycle.UseRaw = 0;
                cycle.UseRngConsumed = true;
                Debug.LogError($"[RNG-COMBAT] {connKey} RangedWeapon::use no room RNG class={cycle.PlayerState.WeaponClass} rngPos={rng.CallsSinceReseed}");
                return;
            }
            Debug.LogError($"[RNG-AUDIT] before-player-use seed=0x{rng.LastSeed:X8} rngPos={rng.CallsSinceReseed} {WanderSimulator.Instance.DescribeSchedule()}");
            uint? playerEntityId = cycle.Connection?.Avatar != null ? (uint)cycle.Connection.Avatar.Id : null;
            cycle.UseRaw = RngLedger.Generate(rng, "room", "player-melee:MeleeWeapon::use", connKey, playerEntityId);
            cycle.UseRngConsumed = true;
            uint previousAnim = cycle.AttackAnimationIndex;
            cycle.AttackAnimationIndex = (byte)(((cycle.UseRaw & 1u) + previousAnim + 1u) % 3u);
            Debug.LogError($"[RNG-COMBAT] {connKey} MeleeWeapon::use useRaw=0x{cycle.UseRaw:X8} animBit={cycle.UseRaw & 1u} anim={previousAnim}->{cycle.AttackAnimationIndex} rngPos={rng.CallsSinceReseed}");
        }

        private Monster ResolveUsePositionMeleeTarget(WeaponUseState cycle)
        {
            if (cycle == null || cycle.Connection == null || cycle.PlayerState == null)
                return null;
            string instanceKey = ResolveCycleInstanceKey(cycle, null, cycle.Connection, "MeleeWeapon::doHit(position)");
            string zoneName = cycle.Connection.CurrentZoneName;
            const int scanRangeF32 = 60 * UnitMover.Fixed;
            int hitRangeF32 = CombatRuntime.Instance.ResolvePlayerWeaponTargetClearRangeF32(cycle.PlayerState, null);
            long hitRangeSqFixed8 = ((long)Math.Max(0, hitRangeF32) * Math.Max(0, hitRangeF32)) >> 8;
            uint viewerEntityId = ResolveProjectileViewerEntityId(cycle.Connection);
            string pathMapKey = ResolvePathMapKey(instanceKey, zoneName);
            PathMap pathMap = !string.IsNullOrWhiteSpace(pathMapKey)
                ? PathMapCatalog.Instance.GetPathMap(pathMapKey)
                : null;
            List<Monster> candidates = CombatRuntime.Instance.GetProjectileHittableMonstersInNativeDistanceOrder(
                cycle.UsePositionTargetFixedX,
                cycle.UsePositionTargetFixedY,
                cycle.UsePositionTargetFixedZ,
                instanceKey,
                zoneName,
                scanRangeF32,
                playerEntityId: viewerEntityId,
                useClientVisiblePosition: true);
            foreach (Monster candidate in candidates)
            {
                if (!CombatRuntime.Instance.TryGetMonsterClientVisiblePositionFixed(
                    candidate,
                    viewerEntityId,
                    out int candidateFixedX,
                    out int candidateFixedY,
                    out int candidateFixedZ))
                    continue;
                long distanceSqFixed8 = CombatRuntime.ProjectileFinderDistanceSqFixed8(
                    candidateFixedX,
                    candidateFixedY,
                    candidateFixedZ,
                    cycle.UsePositionTargetFixedX,
                    cycle.UsePositionTargetFixedY,
                    cycle.UsePositionTargetFixedZ);
                if (distanceSqFixed8 > hitRangeSqFixed8)
                    continue;
                if (pathMap == null
                    || pathMap.GetReachabilityFixed(
                        cycle.UsePositionTargetFixedX,
                        cycle.UsePositionTargetFixedY,
                        candidateFixedX,
                        candidateFixedY) != PathReachability.Reachable)
                    continue;
                CombatRuntime.Instance.ApplyMonsterWanderClientVisiblePosition(candidate, "MeleeWeapon::doHit(position)");
                Debug.LogError($"[WEAPON-USE-POSITION-HIT] target={candidate.Name}#{candidate.EntityId} aimFixed=({cycle.UsePositionTargetFixedX},{cycle.UsePositionTargetFixedY},{cycle.UsePositionTargetFixedZ}) targetFixed=({candidateFixedX},{candidateFixedY},{candidateFixedZ}) distanceSqFixed8={distanceSqFixed8} rangeF32={hitRangeF32} candidates={candidates.Count} sourceFunction=MeleeWeapon::doHit(void)@0x00591B70->UnitFinder2::findEnemies@0x00510F50");
                return candidate;
            }
            Debug.LogError($"[WEAPON-USE-POSITION-HIT] target=none aimFixed=({cycle.UsePositionTargetFixedX},{cycle.UsePositionTargetFixedY},{cycle.UsePositionTargetFixedZ}) rangeF32={hitRangeF32} candidates={candidates.Count} pathMap={(pathMap != null)} sourceFunction=MeleeWeapon::doHit(void)@0x00591B70->UnitFinder2::findEnemies@0x00510F50");
            return null;
        }

        private void TickCycle(string connKey, WeaponUseState cycle, MersenneTwister rng, int simulationTick)
        {
            if (cycle == null) return;
            if (cycle.Monster == null && !cycle.UsePositionAction)
            {
                cycle.IsActive = false;
                cycle.AwaitingContact = false;
                return;
            }

            if (cycle.AwaitingContact)
            {
                if (cycle.Connection != null && !cycle.Connection.HasActiveUseTarget)
                {
                    Debug.LogError($"[WEAPON-USE] {connKey} stop awaiting contact on {cycle.Monster.Name} target={cycle.TargetId} source=no-active-UseTarget sourceFunction=UseTarget::UpdateMoving owner=Behavior::doActionLocal");
                    cycle.IsActive = false;
                    cycle.AwaitingContact = false;
                    cycle.ServerApproachOnly = false;
                    cycle.PendingRepeatUses = 0;
                    cycle.RepeatUsePendingAtRelease = false;
                    return;
                }
                if (cycle.Connection != null && cycle.Connection.HasActiveUseTarget && cycle.Connection.ActiveUseTargetId != cycle.TargetId)
                {
                    cycle.IsActive = false;
                    cycle.AwaitingContact = false;
                    return;
                }
                if (!HasPlayerMeleeContact(cycle, simulationTick, out int distanceFixed, out int rangeFixed))
                {
                    return;
                }
                if (!IsUseReady(cycle, simulationTick))
                {
                    return;
                }
                bool wasServerApproachOnly = cycle.ServerApproachOnly;
                if (wasServerApproachOnly)
                {
                    uint avatarId = cycle.Connection?.Avatar != null ? (uint)cycle.Connection.Avatar.Id : 0u;
                    if (avatarId != 0)
                        CombatRuntime.Instance.SetPlayerActiveClientAttack(avatarId, true, cycle.Monster.EntityId);
                }
                BeginCycle(connKey, cycle, cycle.Monster, cycle.TargetId, simulationTick);
                string contactMode = wasServerApproachOnly ? "CONTACT cycle from approach" : "CONTACT cycle";
                Debug.LogError($"[WEAPON-USE] {connKey} -> {contactMode} on {cycle.Monster.Name} distFixed={distanceFixed} rangeFixed={rangeFixed} inputAdmissionTick={cycle.InputAdmissionTick} cycleStartTick={simulationTick}");
                return;
            }

            if (!cycle.IsActive) return;

            cycle.TickCounter++;

                bool fireSound = false;
                if (IsRangedCycle(cycle))
                {
                    int burstCount = GetRangedBurstCount(cycle);
                    if (cycle.RangedSoundEventsFired < burstCount && cycle.TickCounter >= GetRangedSoundEventTick(cycle, cycle.RangedSoundEventsFired))
                    {
                        cycle.RangedSoundEventsFired++;
                        cycle.ProcFired = cycle.RangedSoundEventsFired >= burstCount;
                        fireSound = true;
                    }
                }
                else if (cycle.TickCounter >= GetSoundEventTick(cycle) && !cycle.ProcFired)
                {
                    cycle.ProcFired = true;
                    fireSound = true;
                }

                if (fireSound)
                {
                    cycle.AttackSoundFired = true;
                    cycle.AttackSoundGateRaw = RandomStreams.GenerateGlobalSound("player-weapon:sound", connKey);
                    cycle.AttackSoundSelectRaw = 0;
                    cycle.AttackSoundRepeatRaw = (cycle.AttackSoundGateRaw & 3u) == 0 ? cycle.AttackSoundGateRaw : 0;
                    string roomRngPos = rng != null ? rng.CallsSinceReseed.ToString() : "n/a";
                    Debug.LogError($"[WEAPON-USE] {connKey} SOUND tick={cycle.TickCounter} clientGlobalSoundRng=True soundGate=0x{cycle.AttackSoundGateRaw:X8} repeat={(cycle.AttackSoundRepeatRaw != 0)} globalSoundRngPos={RandomStreams.GlobalSoundCalls} roomRngPos={roomRngPos}");
                }

                bool fireHit = false;
                if (IsRangedCycle(cycle))
                {
                    int burstCount = GetRangedBurstCount(cycle);
                    if (cycle.RangedHitEventsFired < burstCount && cycle.TickCounter >= GetRangedHitEventTick(cycle, cycle.RangedHitEventsFired))
                    {
                        cycle.RangedHitEventsFired++;
                        cycle.HitFired = cycle.RangedHitEventsFired >= burstCount;
                        fireHit = true;
                    }
                }
                else if (cycle.TickCounter >= GetHitEventTick(cycle) && !cycle.HitFired)
                {
                    cycle.HitFired = true;
                    fireHit = true;
                }

                if (fireHit)
                {
                    cycle.SwingCount++;

                    ConsumeUseRng(connKey, cycle, rng);
                    uint useRaw = cycle.UseRaw;

                    if (IsProjectileRangedCycle(cycle))
                    {
                        QueueProjectileHit(connKey, cycle, simulationTick);
                    }
                    else
                    {
                        if (cycle.UsePositionAction)
                            cycle.Monster = ResolveUsePositionMeleeTarget(cycle);
                        if (cycle.Monster == null)
                        {
                            Debug.LogError($"[WEAPON-USE-POSITION] conn={connKey} result=no-hit swing={cycle.SwingCount} tick={simulationTick} rngPos={rng?.CallsSinceReseed ?? 0} sourceFunction=MeleeWeapon::doHit(void)@0x00591B70");
                        }
                        else
                        {
                    WeaponDamageInput damageInput = CreatePlayerWeaponDamageInput(rng, cycle.PlayerState, cycle.Monster, "WeaponUseState", cycle.Connection?.Avatar != null ? (uint)cycle.Connection.Avatar.Id : null);
                    DamageResolver.LogDamageSlots(cycle.PlayerState, damageInput, cycle.Monster, "WeaponUseState");
                    WeaponDamageResult damageResult = DamageResolver.ResolveWeaponDamage(damageInput);
                    uint hitRaw = damageResult.HitRaw;
                    int hitRoll = damageResult.HitRoll;
                    uint blockRaw = damageResult.BlockRaw;
                    int blockRoll = damageResult.BlockRoll;
                    int attackRating = damageResult.AttackRating;
                    int defenseRating = damageResult.DefenseRating;
                    int attackerLevel = damageResult.AttackerLevel;
                    int hitDefenderLevel = damageResult.DefenderLevel;
                    int hitChanceF32 = damageResult.HitThreshold;
                    bool isHit = damageResult.IsHit;
                    bool isBlocked = damageResult.IsBlocked;

                    Debug.LogError($"[RNG-COMBAT] swing#{cycle.SwingCount} seed=0x{rng.LastSeed:X8} rngPos={rng.CallsSinceReseed} useRaw=0x{useRaw:X8} soundSelect=0x{cycle.AttackSoundSelectRaw:X8} soundGate=0x{cycle.AttackSoundGateRaw:X8} soundRepeat=0x{cycle.AttackSoundRepeatRaw:X8} hitRaw=0x{hitRaw:X8} blockRaw=0x{blockRaw:X8} hitRoll={hitRoll} threshold={hitChanceF32} ar={attackRating} dr={defenseRating} levels={attackerLevel}->{hitDefenderLevel} isHit={isHit}");

                    int damage = 0;
                    uint damageRaw = 0;
                    uint damageWire = 0;
                    int weaponDmg = 0;
                    int volatility = 0;
                    int levelDamageBonus = 0;
                    int damageBonus = 0;
                    int damageMod = 0;
                    int minDmg = 0;
                    int maxDmg = 0;
                    int critThreshold = 0;
                    int critPercent = 0;
                    string weaponClass = cycle.PlayerState?.WeaponClass ?? "unknown";
                    string statSource = DamageResolver.ResolveWeaponStatSource(cycle.PlayerState);
                    bool isCritical = false;
                    uint oldHPWire = CombatRuntime.Instance.PeekMonsterCurrentHPWire(cycle.Monster);
                    uint newHPWire = oldHPWire;
                    bool applied = false;
                    bool killed = false;
                    int actualDamage = 0;
                    int appliedDamage = 0;
                    uint damageProcRaw = 0;
                    uint effectRaw = 0;
                    uint impactSoundRaw = 0;
                    weaponDmg = damageInput.WeaponDamageF32;
                    volatility = damageInput.WeaponVolatilityF32;
                    levelDamageBonus = damageInput.DamageLevel;
                    damageBonus = damageInput.DamageBonus;
                    damageMod = damageInput.DamageMod;
                    minDmg = damageResult.MinDamageF32;
                    maxDmg = damageResult.MaxDamageF32;
                    critThreshold = damageInput.CritThreshold;
                    critPercent = damageInput.CritDamagePercent;
                    isCritical = damageResult.IsCritical;
                    if (isHit && !isBlocked)
                    {
                        damageRaw = damageResult.DamageRaw;
                        damage = damageResult.DamageF32;

                        Debug.LogError($"[RNG-COMBAT] dmgRaw=0x{damageRaw:X8} weaponClass={weaponClass} statSource={statSource} dmg={damage / 256} range=[{minDmg / 256},{maxDmg / 256}] rangeWire=[{minDmg},{maxDmg}] rolledWire={damage} bonus={damageBonus} damageMod={damageMod} crit={isCritical} critThreshold={critThreshold} critPct={critPercent} str={cycle.PlayerState.Strength} agi={cycle.PlayerState.Agility} level={cycle.PlayerState.Level} weaponLevel={cycle.PlayerState.WeaponLevel} levelBonus={levelDamageBonus} clientBaseDamage={cycle.PlayerState.WeaponBaseDamage} clientBaseSource={cycle.PlayerState.WeaponBaseDamageSource} weaponF32={weaponDmg} volF32={volatility} roomRng={rng.CallsSinceReseed}");

                        damageWire = damageResult.DamageWire;
                        uint totalDamageWire = damageResult.TotalDamageWire != 0 ? damageResult.TotalDamageWire : damageWire;
                        int addCount = damageResult.DamageAdds != null ? damageResult.DamageAdds.Count : 0;
                        actualDamage = (int)((totalDamageWire + 255) / 256);
                        uint playerEntityId = cycle.Connection?.Avatar != null ? (uint)cycle.Connection.Avatar.Id : 0u;

                        int clientHitTick = IsRangedCycle(cycle)
                            ? GetRangedHitEventTick(cycle, Math.Max(0, cycle.RangedHitEventsFired - 1))
                            : GetHitEventTick(cycle);
                        int absoluteHitTick = cycle.CycleStartTick > 0
                            ? cycle.CycleStartTick + clientHitTick
                            : simulationTick;
                        applied = CombatRuntime.Instance.ApplyPlayerWeaponDamageToMonsterWire(cycle.Monster, damageResult, $"WeaponUseState-HIT swing={cycle.SwingCount}", out oldHPWire, out newHPWire, out killed, out effectRaw, rng, "player-weapon", (uint)Math.Max(0, absoluteHitTick), playerEntityId);
                        if (applied)
                        {
                            cycle.LastDirectHitSimulationTick = (uint)Math.Max(0, simulationTick);
                            cycle.LastDirectHitPreviousHPWire = oldHPWire;
                            cycle.LastDirectHitNewHPWire = newHPWire;
                            CombatRuntime.Instance.NotifyMonsterDamagedByPlayer(cycle.Monster, playerEntityId, "damage");
                        }
                        cycle.ImpactSoundRaw = impactSoundRaw;
                        appliedDamage = (int)((oldHPWire > newHPWire ? oldHPWire - newHPWire : 0) + 255) / 256;

                        Debug.LogError($"[CLIENT-DAMAGE-CONTRACT] source=WeaponUseState swing={cycle.SwingCount} target={cycle.Monster.Name}#{cycle.Monster.EntityId} damageLevel={levelDamageBonus} weaponClassId={damageInput.WeaponClassId} damageTypeId={damageInput.DamageTypeId} bonus={damageBonus} mod={damageMod} rangeWire=[{minDmg},{maxDmg}] rawRoll=0x{damageRaw:X8} preQueryWire={damageWire} addCount={addCount} totalRawWire={totalDamageWire} hp={oldHPWire}->{newHPWire}/{cycle.Monster.MaxHPWire} result={(isCritical ? "CRIT" : "HIT")} rngAfter={rng.CallsSinceReseed}");
                        Debug.LogError($"[WEAPON-USE] {connKey} {(isCritical ? "CRIT" : "HIT")}: {actualDamage} dmgWire={damageWire} totalWire={totalDamageWire} addCount={addCount} applied={applied} appliedDamage={appliedDamage} on {cycle.Monster.Name} HP={oldHPWire}->{newHPWire} proc=0x{damageProcRaw:X8} impact=0x{impactSoundRaw:X8} effect=0x{effectRaw:X8} rngPos={rng.CallsSinceReseed} [swing #{cycle.SwingCount}]");
                        if (killed && cycle.Monster != null)
                        {
                            _completedAttacks.Enqueue(new CompletedAttack
                            {
                                ConnKey = connKey,
                                Connection = cycle.Connection,
                                Monster = cycle.Monster,
                                DamageDealt = appliedDamage,
                                Killed = true
                            });
                            cycle.TargetDiedDuringCycle = true;
                        }
                    }
                    else
                    {
                        string resultType = !isHit ? "MISS" : "BLOCK";
                        CombatRuntime.Instance.LogMonsterClientVisibleSwingNoDamage(cycle.Monster, $"WeaponUseState-{resultType} swing={cycle.SwingCount}");
                        Debug.LogError($"[WEAPON-USE] {connKey} {resultType}: hitRoll={hitRoll} blockRoll={blockRoll} on {cycle.Monster.Name} [swing #{cycle.SwingCount}]");
                    }
                    string combatResult = isHit ? (isBlocked ? "BLOCK" : (isCritical ? "CRIT" : "HIT")) : "MISS";
                    Debug.LogError($"[PLAYER-HIT-DETAIL] player={connKey} target={cycle.Monster.EntityId}/{cycle.Monster.BehaviorId} seed=0x{rng.LastSeed:X8} swing={cycle.SwingCount} tick={cycle.TickCounter} rngAfter={rng.CallsSinceReseed} useRaw=0x{useRaw:X8} soundGate=0x{cycle.AttackSoundGateRaw:X8} soundSelect=0x{cycle.AttackSoundSelectRaw:X8} soundRepeat=0x{cycle.AttackSoundRepeatRaw:X8} hitRaw=0x{hitRaw:X8} blockRaw=0x{blockRaw:X8} dmgRaw=0x{damageRaw:X8} effectRaw=0x{effectRaw:X8} impact=0x{impactSoundRaw:X8} hitRoll={hitRoll} threshold={hitChanceF32} critThreshold={critThreshold} critPct={critPercent} blockRoll={blockRoll} ar={attackRating} dr={defenseRating} levels={attackerLevel}->{hitDefenderLevel} result={combatResult} weaponClass={weaponClass} weaponClassId={damageInput.WeaponClassId} damageTypeId={damageInput.DamageTypeId} statSource={statSource} weaponDamageF32={weaponDmg} volF32={volatility} weaponLevel={cycle.PlayerState.WeaponLevel} levelBonus={levelDamageBonus} clientBaseDamage={cycle.PlayerState.WeaponBaseDamage} clientBaseSource={cycle.PlayerState.WeaponBaseDamageSource} bonus={damageBonus} mod={damageMod} rangeWire=[{minDmg},{maxDmg}] rolledWire={damageWire} totalWire={damageResult.TotalDamageWire} addCount={(damageResult.DamageAdds != null ? damageResult.DamageAdds.Count : 0)} applied={applied} hp={oldHPWire}->{newHPWire} exact=weapon-stat-source");
                    Debug.LogError($"[COMBAT-EVENT] actor=player player={connKey} actorId={(cycle.Connection?.Avatar != null ? cycle.Connection.Avatar.Id : 0)} target=monster targetId={cycle.Monster.EntityId} behaviorId={cycle.Monster.BehaviorId} result={combatResult} damageWire={damageWire} totalWire={damageResult.TotalDamageWire} addCount={(damageResult.DamageAdds != null ? damageResult.DamageAdds.Count : 0)} appliedDamage={appliedDamage} hp={oldHPWire}->{newHPWire} hitRaw=0x{hitRaw:X8} hitRoll={hitRoll} threshold={hitChanceF32} blockRaw=0x{blockRaw:X8} blockRoll={blockRoll} blockChance=0 damageRaw=0x{damageRaw:X8} effectRaw=0x{effectRaw:X8} resist=0 critThreshold={critThreshold} critPct={critPercent} rngAfter={rng.CallsSinceReseed}");
                        }
                    }
                }

                if (cycle.TickCounter >= GetCycleTicks(cycle))
                {
                    bool heldActionAlive = cycle.PendingRepeatUses > 0
                        && cycle.Connection != null
                        && cycle.Connection.HasActiveUseTarget
                        && cycle.Connection.ActiveUseTargetId == cycle.TargetId
                        && cycle.Monster != null
                        && cycle.Monster.IsAlive;
                    cycle.RepeatUsePendingAtRelease = heldActionAlive;
                    bool consumedActiveUseTarget = false;
                    cycle.TickCounter = 0;
                    ResetSwingRngState(cycle);
                    cycle.CycleStartTick = 0;
                    cycle.IsActive = false;
                    cycle.TargetDiedDuringCycle = false;
                    cycle.ServerApproachOnly = false;
                    cycle.ContactHoldLogged = false;
                    if (cycle.Connection != null && cycle.Connection.ActiveUseTargetId == cycle.TargetId)
                        cycle.Connection.ActiveUseTargetStartedWeaponUse = false;
                    if (cycle.PendingRepeatUses > 0)
                        cycle.PendingRepeatUses--;
                    if (heldActionAlive)
                    {
                        cycle.AwaitingContact = true;
                        cycle.ServerApproachOnly = true;
                        EnqueueControlRelease(cycle.Connection);
                        Debug.LogError($"[WEAPON-USE] {connKey} -> REPEAT deferred to next scheduled 3D init-use tick on {cycle.Monster.Name} pending={cycle.PendingRepeatUses}");
                        return;
                    }
                    cycle.AwaitingContact = false;
                    bool hasPendingProjectile = IsProjectileRangedCycle(cycle) && HasPendingProjectileForCycle(cycle);
                    if (hasPendingProjectile)
                        Debug.LogError($"[WEAPON-USE] {connKey} -> STOP animation on {cycle.Monster?.Name ?? "monster"} pendingProjectile=True actionClear=weapon-stop sourceFunction=RangedWeapon::update");
                    if (cycle.Connection != null && cycle.Connection.HasActiveUseTarget && cycle.Connection.ActiveUseTargetId == cycle.TargetId)
                    {
                        EnqueueControlRelease(cycle.Connection);
                        consumedActiveUseTarget = true;
                    }
                    Debug.LogError($"[WEAPON-USE] {connKey} -> STOP cycle on {cycle.Monster?.Name ?? "monster"} consumedUseTarget={consumedActiveUseTarget} pendingProjectile={hasPendingProjectile} sourceFunction=RangedWeapon::update timer==0");
                }
        }

        public bool IsWeaponBusyOnKilledTarget(RRConnection connection, Monster monster)
        {
            if (connection == null || monster == null)
                return false;
            string connKey = connection.ConnId.ToString();
            return _activeCycles.TryGetValue(connKey, out WeaponUseState cycle) &&
                cycle != null &&
                cycle.IsActive &&
                cycle.Monster != null &&
                cycle.Monster.EntityId == monster.EntityId;
        }


        private void QueueProjectileHit(string connKey, WeaponUseState cycle, int fireTick)
        {
            if (cycle == null || cycle.Connection == null || cycle.PlayerState == null)
                return;

            Monster requestedMonster = cycle.Monster;
            if (requestedMonster == null && !cycle.UsePositionAction)
                return;

            int playerFixedX = cycle.Connection.HasOwnerAckFollowClientPosition
                ? cycle.Connection.OwnerAckFollowClientPosFixedX
                : cycle.Connection.HasUnitFollowClientPosition
                    ? cycle.Connection.UnitFollowClientPosFixedX
                : cycle.Connection.HasReflectedAvatarPosition
                    ? cycle.Connection.ReflectedAvatarPosFixedX
                : cycle.Connection.HasLivePlayerPosition
                    ? cycle.Connection.LivePlayerPosFixedX
                    : cycle.Connection.PlayerPosFixedX;
            int playerFixedY = cycle.Connection.HasOwnerAckFollowClientPosition
                ? cycle.Connection.OwnerAckFollowClientPosFixedY
                : cycle.Connection.HasUnitFollowClientPosition
                    ? cycle.Connection.UnitFollowClientPosFixedY
                : cycle.Connection.HasReflectedAvatarPosition
                    ? cycle.Connection.ReflectedAvatarPosFixedY
                : cycle.Connection.HasLivePlayerPosition
                    ? cycle.Connection.LivePlayerPosFixedY
                    : cycle.Connection.PlayerPosFixedY;
            ResolveProjectileSourcePositionFixed(cycle, playerFixedX, playerFixedY, out int sourceFixedX, out int sourceFixedY);
            uint projectileViewerEntityId = ResolveProjectileViewerEntityId(cycle.Connection);
            int targetFixedX = cycle.ProjectileTargetSnapshotResolved
                ? cycle.ProjectileTargetFixedX
                : requestedMonster?.PosFixedX ?? cycle.UsePositionTargetFixedX;
            int targetFixedY = cycle.ProjectileTargetSnapshotResolved
                ? cycle.ProjectileTargetFixedY
                : requestedMonster?.PosFixedY ?? cycle.UsePositionTargetFixedY;
            bool targetFromUseSnapshot = cycle.ProjectileTargetSnapshotResolved;
            if (!targetFromUseSnapshot
                && requestedMonster != null
                && CombatRuntime.Instance.TryGetMonsterClientVisiblePositionFixed(requestedMonster, projectileViewerEntityId, out int visibleTargetFixedX, out int visibleTargetFixedY))
            {
                targetFixedX = visibleTargetFixedX;
                targetFixedY = visibleTargetFixedY;
            }
            bool hasDirection = PathMap.TryBuildNativeRayDirectionFixed(
                targetFixedX - sourceFixedX,
                targetFixedY - sourceFixedY,
                out int directionFixedX,
                out int directionFixedY,
                out int pathDistanceF32);
            if (pathDistanceF32 <= 0)
                pathDistanceF32 = 1;
            int speedF32 = cycle.PlayerState.WeaponProjectileSpeedF32 > 0
                ? cycle.PlayerState.WeaponProjectileSpeedF32
                : 0x100;
            speedF32 = Math.Max(0x100, speedF32);
            long pathDistanceF16 = (long)pathDistanceF32 << 8;
            int flightTicks = ProjectileFlightTicksFixed16(pathDistanceF16, speedF32);
            int impactDelayTicks = ProjectileFlightTicksFixed16(pathDistanceF16, speedF32);
            int dueTick = fireTick + impactDelayTicks;
            int delayTicks = Math.Max(0, dueTick - fireTick);
            int projectileSizeF32 = cycle.PlayerState.WeaponProjectileSizeF32 > 0
                ? cycle.PlayerState.WeaponProjectileSizeF32
                : 0;
            projectileSizeF32 = Math.Max(0, projectileSizeF32);
            int projectileReachF32 = CombatRuntime.Instance.ResolvePlayerRangedProjectileRangeF32(cycle.PlayerState, requestedMonster);
            int maxDistanceF32 = projectileReachF32 > 0
                ? projectileReachF32
                : (cycle.PlayerState.WeaponRangeF32 > 0 ? cycle.PlayerState.WeaponRangeF32 : pathDistanceF32);
            maxDistanceF32 = Math.Max(1, maxDistanceF32);
            int maxLifetimeTicks = ProjectileLifetimeTicksFixed32(maxDistanceF32, speedF32);
            int stepDistanceF32 = ProjectileStepDistanceF32(speedF32);
            int ownerRadiusF32 = CombatRuntime.Instance.ResolveAvatarUnitBehaviorRadiusF32();
            int initialDistanceF32 = ProjectileInitialDistanceF32(ownerRadiusF32);
            int startFixedX = sourceFixedX + FixedMultiply8(directionFixedX, initialDistanceF32);
            int startFixedY = sourceFixedY + FixedMultiply8(directionFixedY, initialDistanceF32);
            int velocityFixedX = hasDirection ? FixedMultiply8(directionFixedX, stepDistanceF32) : 0;
            int velocityFixedY = hasDirection ? FixedMultiply8(directionFixedY, stepDistanceF32) : 0;
            string instanceKey = ResolveCycleInstanceKey(cycle, requestedMonster, cycle.Connection, "QueueProjectileHit");
            if (string.IsNullOrWhiteSpace(instanceKey))
            {
                RuntimeEvidence.LogFallbackHit(
                    "rng-instance",
                    "projectile-missing-instance-stamp",
                    $"target={requestedMonster?.EntityId ?? 0} conn={connKey}",
                    64);
                Debug.LogError($"[RNG-INSTANCE] source=QueueProjectileHit reason=missing-instance target={requestedMonster?.EntityId ?? 0} conn={connKey}");
                return;
            }
            string zoneName = !string.IsNullOrWhiteSpace(requestedMonster?.ZoneName)
                ? requestedMonster.ZoneName
                : cycle.Connection.CurrentZoneName;
            int baseFixedZ = ResolveProjectileBaseZFixed(cycle.Connection, requestedMonster);
            int sourceOffsetFixedZ = cycle.AttackSourceOffsetFixedZ;
            int startGroundFallbackFixedZ = baseFixedZ;
            bool startGroundResolved = ResolveProjectileGroundZFixed(
                zoneName,
                instanceKey,
                startFixedX,
                startFixedY,
                startGroundFallbackFixedZ,
                out int startGroundFixedZ,
                out string startGroundSource);
            int startFixedZ = baseFixedZ + sourceOffsetFixedZ;
            int groundOffsetFixedZ = startFixedZ - (startGroundResolved ? startGroundFixedZ : startGroundFallbackFixedZ);
            var pending = new PendingProjectileHit
            {
                Sequence = ++_nextProjectileSequence,
                ConnKey = connKey,
                Connection = cycle.Connection,
                PlayerState = cycle.PlayerState,
                Monster = requestedMonster,
                InstanceKey = instanceKey,
                RequestedTargetId = requestedMonster?.EntityId ?? 0,
                TargetId = requestedMonster?.EntityId ?? 0,
                BehaviorId = requestedMonster?.BehaviorId ?? 0,
                UsePositionAction = cycle.UsePositionAction,
                Swing = cycle.SwingCount,
                Tick = cycle.TickCounter,
                FireTick = fireTick,
                FlightTicks = flightTicks,
                ImpactDelayTicks = impactDelayTicks,
                DueTick = dueTick,
                HitDistanceF32 = pathDistanceF32,
                UseRaw = cycle.UseRaw,
                AttackSoundSelectRaw = cycle.AttackSoundSelectRaw,
                AttackSoundGateRaw = cycle.AttackSoundGateRaw,
                AttackSoundRepeatRaw = cycle.AttackSoundRepeatRaw,
                DamageInput = requestedMonster != null
                    ? CreatePlayerWeaponDamageInput(CombatRuntime.Instance.GetRoomRngForInstance(instanceKey), cycle.PlayerState, requestedMonster, "RangedProjectileSnapshot", cycle.Connection?.Avatar != null ? (uint)cycle.Connection.Avatar.Id : null)
                    : null,
                StartFixedX = startFixedX,
                StartFixedY = startFixedY,
                StartFixedZ = startFixedZ,
                CurrentFixedX = startFixedX,
                CurrentFixedY = startFixedY,
                CurrentFixedZ = startFixedZ,
                DirectionFixedX = directionFixedX,
                DirectionFixedY = directionFixedY,
                VelocityFixedX = velocityFixedX,
                VelocityFixedY = velocityFixedY,
                VelocityFixedZ = 0,
                GroundOffsetFixedZ = groundOffsetFixedZ,
                GroundOffsetResolved = startGroundResolved,
                GroundSource = startGroundSource,
                SourceOffsetFixedX = cycle.AttackSourceOffsetFixedX,
                SourceOffsetFixedY = cycle.AttackSourceOffsetFixedY,
                SourceOffsetFixedZ = cycle.AttackSourceOffsetFixedZ,
                SourceOffsetResolved = cycle.AttackSourceOffsetResolved,
                TargetFixedX = targetFixedX,
                TargetFixedY = targetFixedY,
                WorldBlocked = false,
                ProjectileSpeedF32 = speedF32,
                ProjectileSizeF32 = projectileSizeF32,
                StepDistanceF32 = stepDistanceF32,
                InitialDistanceF32 = initialDistanceF32,
                CurrentDistanceF32 = initialDistanceF32,
                MaxDistanceF32 = maxDistanceF32,
                PathDistanceF16 = pathDistanceF16,
                PathDistanceF32 = pathDistanceF32,
                MaxLifetimeTicks = maxLifetimeTicks,
                LastUpdateTick = fireTick,
                UpdatesCompleted = 0,
                InitUsePassed = cycle.InitUsePassed,
                InitUseRangeF32 = cycle.InitUseRangeF32,
                InitUseDistanceF32 = cycle.InitUseDistanceF32,
                InitUseToleranceF32 = cycle.InitUseToleranceF32,
                ImpactResolved = false
            };
            _activeProjectiles.Add(pending);
            CombatRuntime.Instance.RegisterClientSubEntity(ClientSubEntityKind.PlayerWeaponProjectile, pending.Sequence, pending.InstanceKey, RemoveProjectileRuntime);
            if (IsConnectionUseTargetForProjectile(pending))
            {
                cycle.Connection.ActiveUseTargetLastProjectileSeq = pending.Sequence;
                cycle.Connection.ActiveUseTargetVisibleHit = false;
            }
            Debug.LogError($"[RANGED-PROJECTILE] {connKey} create-subentity target={requestedMonster?.Name ?? "position"}#{requestedMonster?.EntityId ?? 0} requested={requestedMonster?.Name ?? "position"}#{requestedMonster?.EntityId ?? 0} seq={pending.Sequence} swing={cycle.SwingCount} hitFrameTick={cycle.TickCounter} fireTick={fireTick} firstUpdateTick={fireTick + 1} flightTicks={flightTicks} impactDelayTicks={impactDelayTicks} dueTick={dueTick} sourceFixed=({sourceFixedX},{sourceFixedY},{startFixedZ}) startFixed=({startFixedX},{startFixedY},{startFixedZ}) directionFixed=({directionFixedX},{directionFixedY},0) velocityFixed=({velocityFixedX},{velocityFixedY},0) groundFixedZ={(startGroundResolved ? startGroundFixedZ : startGroundFallbackFixedZ)} groundOffsetFixedZ={groundOffsetFixedZ} groundResolved={startGroundResolved} groundSource='{startGroundSource ?? "fallback"}' sourceOffsetFixed=({pending.SourceOffsetFixedX},{pending.SourceOffsetFixedY},{pending.SourceOffsetFixedZ}) sourceOffsetResolved={pending.SourceOffsetResolved} headingFixed={ResolveProjectileSourceHeadingFixed(cycle.Connection)} hitDistanceF32={pathDistanceF32} pathDistanceF32={pathDistanceF32} targetFixed=({targetFixedX},{targetFixedY}) targetSource={(targetFromUseSnapshot ? "weapon-use-snapshot" : "monster-runtime")} speedF32={speedF32} stepDistanceF32={stepDistanceF32} initialOwnerRadiusF32={initialDistanceF32} sizeF32={projectileSizeF32} maxDistanceF32={maxDistanceF32} maxLife={maxLifetimeTicks} delayTicks={delayTicks} initUsePassed={pending.InitUsePassed} initUseRangeF32={pending.InitUseRangeF32} initUseDistanceF32={pending.InitUseDistanceF32} useProjectile=True collision=subentity-point sourceFunction=RangedWeapon::update+Projectile::init");
        }

        private static void ResolveProjectileSourcePositionFixed(WeaponUseState cycle, int playerFixedX, int playerFixedY, out int startFixedX, out int startFixedY)
        {
            startFixedX = playerFixedX;
            startFixedY = playerFixedY;
            if (cycle == null)
                return;

            int sourceOffsetFixedX = cycle.AttackSourceOffsetFixedX;
            int sourceOffsetFixedY = cycle.AttackSourceOffsetFixedY;
            if (!cycle.AttackSourceOffsetResolved && sourceOffsetFixedX == 0 && sourceOffsetFixedY == 0)
                return;

            int headingFixed = ResolveProjectileSourceHeadingFixed(cycle.Connection);
            int headingDegrees = UnitMover.WrapDegrees(headingFixed >> 8);
            int sin = UnitMover.ZRotateSinFixed(headingDegrees);
            int cos = UnitMover.ZRotateCosFixed(headingDegrees);
            startFixedX = playerFixedX
                - (int)(((long)cos * sourceOffsetFixedX) >> 8)
                - (int)(((long)sin * sourceOffsetFixedY) >> 8);
            startFixedY = playerFixedY
                - (int)(((long)sin * sourceOffsetFixedX) >> 8)
                + (int)(((long)cos * sourceOffsetFixedY) >> 8);
        }

        private DueDrainSummary UpdateActiveProjectileSubEntities(MersenneTwister rng, int nowTick, string source, Predicate<PendingProjectileHit> countPredicate, Predicate<PendingProjectileHit> processPredicate)
        {
            if (VerboseProjectileDiagnostics && nowTick % 30 == 0)
                Debug.LogError($"[PROJ-SWEEP-ENTRY] source={source ?? "?"} nowTick={nowTick} count={_activeProjectiles.Count} rngParam={(rng != null)}");
            int matched = 0;
            var summary = new DueDrainSummary
            {
                PendingBefore = _activeProjectiles.Count
            };

            if (_activeProjectiles.Count == 0)
            {
                summary.PendingAfter = 0;
                return summary;
            }

            if (rng == null)
            {
                bool anyRuntimeRng = false;
                for (int r = 0; r < _activeProjectiles.Count; r++)
                {
                    var pending = _activeProjectiles[r];
                    if (pending != null &&
                        !string.IsNullOrWhiteSpace(pending.InstanceKey) &&
                        CombatRuntime.Instance.GetRoomRngForInstance(pending.InstanceKey) != null)
                    {
                        anyRuntimeRng = true;
                        break;
                    }
                }
                if (!anyRuntimeRng)
                {
                    summary.PendingAfter = _activeProjectiles.Count;
                    summary.Stopped = true;
                    var next = _activeProjectiles[0];
                    summary.NextDueTick = Math.Max(0, next.LastUpdateTick + 1);
                    Debug.LogError($"[RANGED-PROJECTILE] active projectile update missing room RNG source={source ?? "unknown"} pending={summary.PendingBefore}");
                    return summary;
                }
            }

            for (int projectileIndex = 0; projectileIndex < _activeProjectiles.Count;)
            {
                PendingProjectileHit pending = _activeProjectiles[projectileIndex];
                if (pending == null)
                {
                    _activeProjectiles.RemoveAt(projectileIndex);
                    continue;
                }

                if (processPredicate != null && !processPredicate(pending))
                {
                    projectileIndex++;
                    continue;
                }

                MersenneTwister pendingRng = !string.IsNullOrWhiteSpace(pending.InstanceKey)
                    ? CombatRuntime.Instance.GetRoomRngForInstance(pending.InstanceKey)
                    : null;
                if (nowTick % 30 == 0)
                    Debug.LogError($"[PROJ-DETAIL] seq={pending.Sequence} nowTick={nowTick} lastUpdate={pending.LastUpdateTick} updates={pending.UpdatesCompleted}/{pending.MaxLifetimeTicks} currentF32={pending.CurrentDistanceF32} maxF32={pending.MaxDistanceF32} due={pending.DueTick} impactResolved={pending.ImpactResolved} skipGate={(nowTick <= pending.LastUpdateTick)} rng={(pendingRng != null)} instance={pending.InstanceKey ?? "<none>"}");
                if (pendingRng == null)
                {
                    RuntimeEvidence.LogFallbackHit(
                        "rng-instance",
                        "projectile-missing-instance-rng",
                        $"seq={pending.Sequence} target={pending.TargetId} instance={pending.InstanceKey ?? "<none>"}",
                        64);
                    Debug.LogError($"[RNG-INSTANCE] source=RangedProjectile reason=missing-instance-rng seq={pending.Sequence} target={pending.TargetId} instance={pending.InstanceKey ?? "<none>"}");
                    projectileIndex++;
                    continue;
                }

                if (pending.ImpactResolved)
                {
                    if (nowTick >= pending.DueTick)
                    {
                        if (countPredicate == null || countPredicate(pending))
                            matched++;
                        ResolveProjectileDamage(pending, pendingRng);
                        _activeProjectiles.RemoveAt(projectileIndex);
                        summary.Drained++;
                    }
                    else
                    {
                        projectileIndex++;
                    }
                    continue;
                }

                if (nowTick <= pending.LastUpdateTick)
                {
                    projectileIndex++;
                    continue;
                }

                bool removed = false;
                int updateLimitTick = Math.Min(nowTick, pending.LastUpdateTick + 1);
                for (int updateTick = pending.LastUpdateTick + 1; updateTick <= updateLimitTick; updateTick++)
                {
                    if (pending.UpdatesCompleted >= pending.MaxLifetimeTicks)
                    {
                        Debug.LogError($"[RANGED-PROJECTILE] {pending.ConnKey} subentity expired no-hit requested={pending.Monster?.Name ?? "monster"}#{pending.TargetId} seq={pending.Sequence} swing={pending.Swing} currentF32={pending.CurrentDistanceF32} maxF32={pending.MaxDistanceF32} updates={pending.UpdatesCompleted}/{pending.MaxLifetimeTicks} source={source ?? "unknown"} sourceFunction=Projectile::update lifetime-zero-before-unit-check");
                        if (pending.Monster != null)
                            CombatRuntime.Instance.LogMonsterClientVisibleSwingNoDamage(pending.Monster, $"RangedProjectile-expired-no-hit swing={pending.Swing}");
                        _activeProjectiles.RemoveAt(projectileIndex);
                        removed = true;
                        break;
                    }

                    int beforeDistanceF32 = Math.Max(0, pending.CurrentDistanceF32);
                    int stepDistanceF32 = pending.StepDistanceF32 > 0
                        ? pending.StepDistanceF32
                        : ProjectileStepDistanceF32(Math.Max(0x100, pending.ProjectileSpeedF32));
                    int maxDistanceF32 = pending.MaxDistanceF32 > 0
                        ? pending.MaxDistanceF32
                        : 1;
                    int afterDistanceF32 = Math.Min(maxDistanceF32, beforeDistanceF32 + Math.Max(1, stepDistanceF32));
                    pending.UpdatesCompleted++;
                    pending.LastUpdateTick = updateTick;

                    if (TryResolveProjectileTargetAlongSegment(
                        pending,
                        beforeDistanceF32,
                        afterDistanceF32,
                        out int nextFixedX,
                        out int nextFixedY,
                        out int nextFixedZ,
                        out Monster impactMonster,
                        out int impactDistanceF32,
                        out bool impactWorldBlocked))
                    {
                        if (impactMonster == null)
                        {
                            pending.HitDistanceF32 = impactDistanceF32;
                            pending.CurrentDistanceF32 = impactDistanceF32;
                            pending.DueTick = updateTick;
                            pending.WorldBlocked = true;
                            pending.ImpactResolved = true;
                            if (countPredicate == null || countPredicate(pending))
                                matched++;
                            if (pending.Monster != null)
                                CombatRuntime.Instance.LogMonsterClientVisibleSwingNoDamage(pending.Monster, $"RangedProjectile-world-collision swing={pending.Swing}");
                            Debug.LogError($"[RANGED-PROJECTILE] {pending.ConnKey} NO-DAMAGE world-collision requested={pending.Monster?.Name ?? "monster"}#{pending.TargetId} seq={pending.Sequence} swing={pending.Swing} hitDistF32={impactDistanceF32} tick={updateTick} source={source ?? "unknown"} sourceFunction=ProjectileChecker::testFirstTime");
                            _activeProjectiles.RemoveAt(projectileIndex);
                            summary.Drained++;
                            removed = true;
                            break;
                        }

                        pending.Monster = impactMonster;
                        pending.TargetId = impactMonster.EntityId;
                        pending.BehaviorId = impactMonster.BehaviorId;
                        pending.CurrentFixedX = nextFixedX;
                        pending.CurrentFixedY = nextFixedY;
                        pending.CurrentFixedZ = nextFixedZ;
                        pending.HitDistanceF32 = impactDistanceF32;
                        pending.CurrentDistanceF32 = impactDistanceF32;
                        pending.ImpactDelayTicks = Math.Max(0, updateTick - pending.FireTick);
                        pending.FlightTicks = pending.ImpactDelayTicks;
                        pending.DueTick = updateTick;
                        pending.WorldBlocked = impactWorldBlocked;
                        pending.ImpactResolved = true;
                        if (nowTick >= pending.DueTick)
                        {
                            if (countPredicate == null || countPredicate(pending))
                                matched++;
                            ResolveProjectileDamage(pending, pendingRng);
                            _activeProjectiles.RemoveAt(projectileIndex);
                            summary.Drained++;
                            removed = true;
                        }
                        break;
                    }

                    pending.CurrentFixedX = nextFixedX;
                    pending.CurrentFixedY = nextFixedY;
                    pending.CurrentFixedZ = nextFixedZ;
                    pending.CurrentDistanceF32 = afterDistanceF32;
                    if (pending.CurrentDistanceF32 >= maxDistanceF32 || pending.UpdatesCompleted >= pending.MaxLifetimeTicks)
                    {
                        Debug.LogError($"[RANGED-PROJECTILE] {pending.ConnKey} subentity expired no-hit requested={pending.Monster?.Name ?? "monster"}#{pending.TargetId} seq={pending.Sequence} swing={pending.Swing} currentF32={pending.CurrentDistanceF32} maxF32={pending.MaxDistanceF32} updates={pending.UpdatesCompleted}/{pending.MaxLifetimeTicks} tick={updateTick} source={source ?? "unknown"} sourceFunction=Projectile::update range-end");
                        if (pending.Monster != null)
                            CombatRuntime.Instance.LogMonsterClientVisibleSwingNoDamage(pending.Monster, $"RangedProjectile-expired-no-hit swing={pending.Swing}");
                        _activeProjectiles.RemoveAt(projectileIndex);
                        removed = true;
                        break;
                    }
                }

                if (!removed)
                    projectileIndex++;
            }
            summary.MatchingDrained = matched;
            summary.PendingAfter = _activeProjectiles.Count;
            if (_activeProjectiles.Count > 0)
            {
                var next = _activeProjectiles[0];
                summary.NextDueTick = Math.Max(0, next.LastUpdateTick + 1);
            }

            if (summary.Drained > 0 || summary.Stopped)
            {
                Debug.LogError($"[RANGED-PROJECTILE-DUE] source={source ?? "unknown"} nowTick={nowTick} drained={summary.Drained} matching={summary.MatchingDrained} pending={summary.PendingBefore}->{summary.PendingAfter} stopped={summary.Stopped} nextDueTick={summary.NextDueTick} runtime=subentity-swept");
            }

            return summary;
        }

        private bool TryResolveProjectileTargetAlongSegment(
            PendingProjectileHit pending,
            int segmentStartF32,
            int segmentEndF32,
            out int nextFixedX,
            out int nextFixedY,
            out int nextFixedZ,
            out Monster hitMonster,
            out int hitDistanceF32,
            out bool worldBlocked)
        {
            nextFixedX = pending?.CurrentFixedX ?? 0;
            nextFixedY = pending?.CurrentFixedY ?? 0;
            nextFixedZ = pending?.CurrentFixedZ ?? 0;
            hitMonster = null;
            hitDistanceF32 = Math.Max(0, segmentEndF32);
            worldBlocked = pending != null && pending.WorldBlocked;
            if (pending == null || pending.PlayerState == null)
                return false;

            int segmentStartDistanceFixed = Math.Max(0, segmentStartF32);
            int segmentEndDistanceFixed = Math.Max(segmentStartDistanceFixed, segmentEndF32);
            int projectileSizeF32 = pending.ProjectileSizeF32 > 0
                ? pending.ProjectileSizeF32
                : 0;
            int projectileRadiusFixed = ProjectileRadiusFromAuthoredSizeF32(projectileSizeF32);
            string zoneName = !string.IsNullOrWhiteSpace(pending.Monster?.ZoneName)
                ? pending.Monster.ZoneName
                : pending.Connection?.CurrentZoneName;
            string instanceKey = pending.InstanceKey;
            if (string.IsNullOrWhiteSpace(instanceKey))
            {
                RuntimeEvidence.LogFallbackHit("rng-instance", "projectile-segment-missing-instance", $"seq={pending.Sequence} target={pending.TargetId}", 64);
                return false;
            }

            int segmentStartFixedX = pending.CurrentFixedX;
            int segmentStartFixedY = pending.CurrentFixedY;
            int segmentStartFixedZ = pending.CurrentFixedZ;
            nextFixedX = unchecked(segmentStartFixedX + pending.VelocityFixedX);
            nextFixedY = unchecked(segmentStartFixedY + pending.VelocityFixedY);
            int candidateFixedZ = unchecked(segmentStartFixedZ + pending.VelocityFixedZ);
            nextFixedZ = candidateFixedZ;
            if (ResolveProjectileGroundZFixed(
                zoneName,
                instanceKey,
                nextFixedX,
                nextFixedY,
                segmentStartFixedZ,
                out int groundFixedZ,
                out string groundSource))
            {
                int groundedFixedZ = groundFixedZ + pending.GroundOffsetFixedZ;
                int heightDeltaFixed = groundedFixedZ - segmentStartFixedZ;
                if (heightDeltaFixed > PROJECTILE_HEIGHT_TOLERANCE_FIXED)
                {
                    worldBlocked = true;
                    Debug.LogError($"[PROJECTILE-PATHMAP-HEIGHT] conn={pending.ConnKey} seq={pending.Sequence} target={pending.TargetId}/{pending.BehaviorId} zone='{zoneName ?? ""}' instance='{instanceKey ?? ""}' fromFixed=({segmentStartFixedX},{segmentStartFixedY},{segmentStartFixedZ}) candidateFixed=({nextFixedX},{nextFixedY},{groundedFixedZ}) deltaFixedZ={heightDeltaFixed} groundSource='{groundSource ?? "unknown"}' result=impact sourceFunction=Projectile::update");
                    return true;
                }
                nextFixedZ = heightDeltaFixed < -PROJECTILE_HEIGHT_TOLERANCE_FIXED
                    ? segmentStartFixedZ
                    : groundedFixedZ;
            }
            else
            {
                nextFixedZ = segmentStartFixedZ;
            }

            WorldCollisionHit worldHit = null;
            int worldHitAlongFixed = int.MaxValue;
            if (WorldCollision.Instance.TryGetPointBlockerFixed(
                zoneName,
                instanceKey,
                nextFixedX,
                nextFixedY,
                nextFixedZ,
                UnitMover.Fixed,
                out worldHit))
                worldHitAlongFixed = segmentEndDistanceFixed;

            Monster best = null;
            int bestAlongFixed = int.MaxValue;
            bool bestBlocked = worldBlocked;
            int scanRangeFixed = ProjectileScanRangeFixed(segmentEndDistanceFixed, pending.HitDistanceF32, projectileRadiusFixed);
            uint projectileViewerEntityId = ResolveProjectileViewerEntityId(pending.Connection);
            var nativeOrderedCandidates = CombatRuntime.Instance.GetProjectileHittableMonstersInNativeDistanceOrder(
                nextFixedX,
                nextFixedY,
                nextFixedZ,
                instanceKey,
                zoneName,
                scanRangeFixed,
                playerEntityId: projectileViewerEntityId,
                useClientVisiblePosition: true);
            foreach (var candidate in nativeOrderedCandidates)
            {
                if (!CombatRuntime.Instance.IsProjectileHittableMonster(candidate, instanceKey, zoneName))
                    continue;

                if (!CombatRuntime.Instance.TryGetMonsterClientVisiblePositionFixed(candidate, projectileViewerEntityId, out int candidateFixedX, out int candidateFixedY))
                    continue;
                int candidateRadiusF32 = CombatRuntime.ResolveProjectileUnitCollisionRadiusF32(candidate);
                int hitRadiusFixed = ProjectileCollisionRadiusF32(candidateRadiusF32, projectileSizeF32);
                if (!TestProjectilePointUnitCollisionFixed(
                    nextFixedX,
                    nextFixedY,
                    candidateFixedX,
                    candidateFixedY,
                    hitRadiusFixed,
                    out _))
                    continue;
                best = candidate;
                bestAlongFixed = segmentEndDistanceFixed;
                bestBlocked = false;
                break;
            }

            if (best == null)
            {
                if (worldHit != null)
                {
                    hitDistanceF32 = segmentEndDistanceFixed;
                    worldBlocked = true;
                    Debug.LogError($"[PROJECTILE-WORLD-COLLISION] conn={pending.ConnKey} seq={pending.Sequence} target={pending.TargetId}/{pending.BehaviorId} zone='{zoneName ?? ""}' instance='{instanceKey ?? ""}' tile='{worldHit.TileType}' grid=({worldHit.GridX},{worldHit.GridY}) object='{worldHit.ObjectPath}' collision='{worldHit.CollisionObject}' hybrid={worldHit.Hybrid} hybridSource='{worldHit.HybridSource ?? ""}' localFixed=({worldHit.LocalFixedX},{worldHit.LocalFixedY},{worldHit.LocalFixedZ}) worldFixed=({worldHit.WorldFixedX},{worldHit.WorldFixedY},{worldHit.WorldFixedZ}) pointFixed=({nextFixedX},{nextFixedY},{nextFixedZ}) radiusFixed={UnitMover.Fixed} hitDistF32={Math.Max(0, worldHitAlongFixed)} result=impact sourceFunction=ProjectileChecker::testFirstTime@0x0059A490->WorldCollisionObject::testCollision@0x004EAB00");
                    return true;
                }
                bool startHeightResolved = ResolveProjectileGroundZFixed(
                    zoneName,
                    instanceKey,
                    segmentStartFixedX,
                    segmentStartFixedY,
                    segmentStartFixedZ,
                    out int startTerrainFixedZ,
                    out string startTerrainSource);
                bool endHeightResolved = ResolveProjectileGroundZFixed(
                    zoneName,
                    instanceKey,
                    nextFixedX,
                    nextFixedY,
                    nextFixedZ,
                    out int endTerrainFixedZ,
                    out string endTerrainSource);
                bool heightSectorCrossed = startHeightResolved
                    && endHeightResolved
                    && (segmentStartFixedZ <= startTerrainFixedZ) != (nextFixedZ <= endTerrainFixedZ);
                Debug.LogError($"[PROJECTILE-SEGMENT] conn={pending.ConnKey} seq={pending.Sequence} tick={pending.LastUpdateTick} fromFixed=({segmentStartFixedX},{segmentStartFixedY},{segmentStartFixedZ}) toFixed=({nextFixedX},{nextFixedY},{nextFixedZ}) terrainFixedZ=({startTerrainFixedZ},{endTerrainFixedZ}) terrainResolved=({startHeightResolved},{endHeightResolved}) terrainSource=('{startTerrainSource ?? ""}','{endTerrainSource ?? ""}') worldCandidate={(worldHit != null)} unitCandidates={nativeOrderedCandidates.Count} heightSectorCrossed={heightSectorCrossed} classification={(heightSectorCrossed ? "world-height" : "none")} sourceFunction=ProjectileChecker::testFirstTime@0x0059A490");
                if (heightSectorCrossed)
                {
                    worldBlocked = true;
                    return true;
                }
                return false;
            }

            if (worldHit != null && worldHitAlongFixed <= bestAlongFixed)
            {
                Debug.LogError($"[PROJECTILE-WORLD-COLLISION] conn={pending.ConnKey} seq={pending.Sequence} target={pending.TargetId}/{pending.BehaviorId} zone='{zoneName ?? ""}' instance='{instanceKey ?? ""}' tile='{worldHit.TileType}' grid=({worldHit.GridX},{worldHit.GridY}) object='{worldHit.ObjectPath}' collision='{worldHit.CollisionObject}' hybrid={worldHit.Hybrid} hybridSource='{worldHit.HybridSource ?? ""}' localFixed=({worldHit.LocalFixedX},{worldHit.LocalFixedY},{worldHit.LocalFixedZ}) worldFixed=({worldHit.WorldFixedX},{worldHit.WorldFixedY},{worldHit.WorldFixedZ}) segmentF32={segmentStartDistanceFixed}->{segmentEndDistanceFixed} hitDistF32={Math.Max(0, worldHitAlongFixed)} targetHitDistF32={Math.Max(0, bestAlongFixed)} result=ignored-unit-first sourceFunction=ProjectileChecker::testFirstTime->UnitFinder2::findHittableUnits");
            }

            CombatRuntime.Instance.ApplyMonsterWanderClientVisiblePosition(best, "ProjectileChecker-subentity-hit");
            pending.DamagePositionResolved = true;
            pending.DamagePositionFixedX = segmentStartFixedX;
            pending.DamagePositionFixedY = segmentStartFixedY;
            pending.DamagePositionFixedZ = segmentStartFixedZ;
            hitMonster = best;
            hitDistanceF32 = Math.Max(0, bestAlongFixed);
            worldBlocked = bestBlocked;
            int bestRadiusF32 = CombatRuntime.ResolveProjectileUnitCollisionRadiusF32(best);
            int hitRadiusF32 = ProjectileCollisionRadiusF32(bestRadiusF32, projectileSizeF32);
            Debug.LogError($"[RANGED-PROJECTILE] {pending.ConnKey} subentity impact seq={pending.Sequence} requested={pending.RequestedTargetId} target={best.Name}#{best.EntityId} swing={pending.Swing} segmentF32={segmentStartDistanceFixed}->{segmentEndDistanceFixed} pointFixed=({nextFixedX},{nextFixedY},{nextFixedZ}) hitDistF32={Math.Max(0, bestAlongFixed)} radiusF32={hitRadiusF32} projectileRadiusF32={projectileRadiusFixed} worldBlocked={worldBlocked}");
            Debug.LogError($"[PROJECTILE-COLLISION] conn={pending.ConnKey} seq={pending.Sequence} createTick={pending.FireTick} firstUpdateTick={pending.FireTick + 1} hitTick={pending.LastUpdateTick} segmentF32={segmentStartDistanceFixed}->{segmentEndDistanceFixed} pointFixed=({nextFixedX},{nextFixedY},{nextFixedZ}) radiusF32={hitRadiusF32} projectileRadiusF32={projectileRadiusFixed} target={best.EntityId}/{best.BehaviorId} initUsePassed={pending.InitUsePassed}");
            return true;
        }

        private void ResolveProjectileDamage(PendingProjectileHit pending, MersenneTwister rng)
        {
            if (pending == null || pending.Monster == null || pending.PlayerState == null)
                return;

            bool sameTarget;
            if (!pending.ImpactResolved)
            {
                if (!TryResolveProjectileTargetAtImpact(pending, out Monster impactMonster, out int impactDistanceF32, out bool impactWorldBlocked))
                {
                    Debug.LogError($"[RANGED-PROJECTILE] {pending.ConnKey} impact no-hit requested={pending.Monster?.Name ?? "monster"}#{pending.TargetId} swing={pending.Swing} pathFixed=({pending.StartFixedX},{pending.StartFixedY})->({pending.TargetFixedX},{pending.TargetFixedY}) hitDistF32={pending.HitDistanceF32} dueTick={pending.DueTick}");
                    CombatRuntime.Instance.LogMonsterClientVisibleSwingNoDamage(pending.Monster, $"RangedProjectile-impact-no-hit swing={pending.Swing}");
                    return;
                }

                sameTarget = pending.RequestedTargetId == 0 || impactMonster.EntityId == pending.RequestedTargetId;
                pending.Monster = impactMonster;
                pending.TargetId = impactMonster.EntityId;
                pending.BehaviorId = impactMonster.BehaviorId;
                pending.HitDistanceF32 = impactDistanceF32;
                pending.WorldBlocked = impactWorldBlocked;
            }
            else
            {
                sameTarget = pending.RequestedTargetId == 0 || pending.Monster.EntityId == pending.RequestedTargetId;
            }

            bool activeUseTargetForProjectile = IsConnectionUseTargetForProjectile(pending);
            if (activeUseTargetForProjectile)
            {
                pending.Connection.ActiveUseTargetVisibleHit = true;
                pending.Connection.ActiveUseTargetLastProjectileSeq = pending.Sequence;
                pending.Connection.ActiveUseTargetLastImpactTick = pending.DueTick;
            }
            else if (pending.Connection != null)
            {
                Debug.LogError($"[PROJECTILE-COLLISION] conn={pending.ConnKey} seq={pending.Sequence} activeUseTarget={pending.Connection.ActiveUseTargetId} requested={pending.RequestedTargetId} target={pending.TargetId} telemetry=inactive-use-target-damage-allowed sourceFunction=Projectile::doImpact");
            }

            WeaponDamageInput damageInput = sameTarget
                ? CloneWeaponDamageInput(pending.DamageInput, rng, "RangedProjectile")
                : null;
            if (damageInput == null)
                damageInput = CreatePlayerWeaponDamageInput(rng, pending.PlayerState, pending.Monster, "RangedProjectile", pending.Connection?.Avatar != null ? (uint)pending.Connection.Avatar.Id : null);
            DamageResolver.LogDamageSlots(pending.PlayerState, damageInput, pending.Monster, "RangedProjectile");
            WeaponDamageResult damageResult = DamageResolver.ResolveWeaponDamage(damageInput);
            uint hitRaw = damageResult.HitRaw;
            int hitRoll = damageResult.HitRoll;
            uint blockRaw = damageResult.BlockRaw;
            int blockRoll = damageResult.BlockRoll;
            int attackRating = damageResult.AttackRating;
            int defenseRating = damageResult.DefenseRating;
            int attackerLevel = damageResult.AttackerLevel;
            int hitDefenderLevel = damageResult.DefenderLevel;
            int hitChanceF32 = damageResult.HitThreshold;
            bool isHit = damageResult.IsHit;
            bool isBlocked = damageResult.IsBlocked;

            Debug.LogError($"[RNG-COMBAT] projectile swing#{pending.Swing} seed=0x{rng.LastSeed:X8} rngPos={rng.CallsSinceReseed} useRaw=0x{pending.UseRaw:X8} soundSelect=0x{pending.AttackSoundSelectRaw:X8} soundGate=0x{pending.AttackSoundGateRaw:X8} soundRepeat=0x{pending.AttackSoundRepeatRaw:X8} hitRaw=0x{hitRaw:X8} blockRaw=0x{blockRaw:X8} hitRoll={hitRoll} threshold={hitChanceF32} ar={attackRating} dr={defenseRating} levels={attackerLevel}->{hitDefenderLevel} isHit={isHit}");

            int damage = 0;
            uint damageRaw = 0;
            uint damageWire = 0;
            int weaponDmg = 0;
            int volatility = 0;
            int levelDamageBonus = 0;
            int damageBonus = 0;
            int damageMod = 0;
            int minDmg = 0;
            int maxDmg = 0;
            int critThreshold = 0;
            int critPercent = 0;
            string weaponClass = pending.PlayerState.WeaponClass ?? "unknown";
            string statSource = DamageResolver.ResolveWeaponStatSource(pending.PlayerState);
            bool isCritical = false;
            uint oldHPWire = CombatRuntime.Instance.PeekMonsterCurrentHPWire(pending.Monster);
            uint newHPWire = oldHPWire;
            bool applied = false;
            bool killed = false;
            int actualDamage = 0;
            int appliedDamage = 0;
            uint effectRaw = 0;
            uint impactSoundRaw = 0;
            weaponDmg = damageInput.WeaponDamageF32;
            volatility = damageInput.WeaponVolatilityF32;
            levelDamageBonus = damageInput.DamageLevel;
            damageBonus = damageInput.DamageBonus;
            damageMod = damageInput.DamageMod;
            minDmg = damageResult.MinDamageF32;
            maxDmg = damageResult.MaxDamageF32;
            critThreshold = damageInput.CritThreshold;
            critPercent = damageInput.CritDamagePercent;
            isCritical = damageResult.IsCritical;

            if (isHit && !isBlocked)
            {
                damageRaw = damageResult.DamageRaw;
                damage = damageResult.DamageF32;

                Debug.LogError($"[RNG-COMBAT] projectile dmgRaw=0x{damageRaw:X8} weaponClass={weaponClass} statSource={statSource} dmg={damage / 256} range=[{minDmg / 256},{maxDmg / 256}] rangeWire=[{minDmg},{maxDmg}] rolledWire={damage} bonus={damageBonus} damageMod={damageMod} crit={isCritical} critThreshold={critThreshold} critPct={critPercent} str={pending.PlayerState.Strength} agi={pending.PlayerState.Agility} level={pending.PlayerState.Level} weaponLevel={pending.PlayerState.WeaponLevel} levelBonus={levelDamageBonus} clientBaseDamage={pending.PlayerState.WeaponBaseDamage} clientBaseSource={pending.PlayerState.WeaponBaseDamageSource} weaponF32={weaponDmg} volF32={volatility} roomRng={rng.CallsSinceReseed}");

                damageWire = damageResult.DamageWire;
                uint totalDamageWire = damageResult.TotalDamageWire != 0 ? damageResult.TotalDamageWire : damageWire;
                int addCount = damageResult.DamageAdds != null ? damageResult.DamageAdds.Count : 0;
                actualDamage = (int)((totalDamageWire + 255) / 256);
                uint playerEntityId = pending.Connection?.Avatar != null ? (uint)pending.Connection.Avatar.Id : 0u;

                uint dueTick = pending.DueTick > 0 ? (uint)pending.DueTick : 0u;
                applied = CombatRuntime.Instance.ApplyPlayerWeaponDamageToMonsterWire(
                    pending.Monster,
                    damageResult,
                    $"RangedProjectile-HIT swing={pending.Swing}",
                    out oldHPWire,
                    out newHPWire,
                    out killed,
                    out effectRaw,
                    rng,
                    "player-projectile",
                    dueTick,
                    playerEntityId,
                    hasDamagePosition: pending.DamagePositionResolved,
                    damagePositionFixedX: pending.DamagePositionFixedX,
                    damagePositionFixedY: pending.DamagePositionFixedY);
                if (applied)
                    CombatRuntime.Instance.NotifyMonsterDamagedByPlayer(pending.Monster, playerEntityId, "projectile-damage");
                appliedDamage = (int)((oldHPWire > newHPWire ? oldHPWire - newHPWire : 0) + 255) / 256;

                Debug.LogError($"[CLIENT-DAMAGE-CONTRACT] source=RangedProjectile swing={pending.Swing} target={pending.Monster.Name}#{pending.Monster.EntityId} damageLevel={levelDamageBonus} weaponClassId={damageInput.WeaponClassId} damageTypeId={damageInput.DamageTypeId} bonus={damageBonus} mod={damageMod} rangeWire=[{minDmg},{maxDmg}] rawRoll=0x{damageRaw:X8} preQueryWire={damageWire} addCount={addCount} totalRawWire={totalDamageWire} hp={oldHPWire}->{newHPWire}/{pending.Monster.MaxHPWire} result={(isCritical ? "CRIT" : "HIT")} rngAfter={rng.CallsSinceReseed}");
                Debug.LogError($"[RANGED-PROJECTILE] {pending.ConnKey} {(isCritical ? "CRIT" : "HIT")}: {actualDamage} dmgWire={damageWire} totalWire={totalDamageWire} addCount={addCount} applied={applied} appliedDamage={appliedDamage} requested={pending.RequestedTargetId} target={pending.Monster.Name}#{pending.Monster.EntityId} HP={oldHPWire}->{newHPWire} impact=0x{impactSoundRaw:X8} effect=0x{effectRaw:X8} rngPos={rng.CallsSinceReseed} swing={pending.Swing} hitDistF32={pending.HitDistanceF32} flightTicks={pending.FlightTicks} dueTick={pending.DueTick} delayTicks={Math.Max(0, pending.DueTick - pending.FireTick)} worldBlocked={pending.WorldBlocked}");
                if (killed)
                {
                    _completedAttacks.Enqueue(new CompletedAttack
                    {
                        ConnKey = pending.ConnKey,
                        Connection = pending.Connection,
                        Monster = pending.Monster,
                        DamageDealt = appliedDamage,
                        Killed = true
                    });
                }
            }
            else
            {
                string resultType = !isHit ? "MISS" : "BLOCK";
                CombatRuntime.Instance.LogMonsterClientVisibleSwingNoDamage(pending.Monster, $"RangedProjectile-{resultType} swing={pending.Swing}");
                Debug.LogError($"[RANGED-PROJECTILE] {pending.ConnKey} {resultType}: hitRoll={hitRoll} blockRoll={blockRoll} target={pending.Monster.Name}#{pending.Monster.EntityId} swing={pending.Swing}");
            }

            string combatResult = isHit ? (isBlocked ? "BLOCK" : (isCritical ? "CRIT" : "HIT")) : "MISS";
            Debug.LogError($"[PLAYER-HIT-DETAIL] player={pending.ConnKey} target={pending.Monster.EntityId}/{pending.BehaviorId} seed=0x{rng.LastSeed:X8} projectile=True swing={pending.Swing} tick={pending.Tick} fireTick={pending.FireTick} flightTicks={pending.FlightTicks} impactDelayTicks={pending.ImpactDelayTicks} dueTick={pending.DueTick} rngAfter={rng.CallsSinceReseed} useRaw=0x{pending.UseRaw:X8} soundGate=0x{pending.AttackSoundGateRaw:X8} soundSelect=0x{pending.AttackSoundSelectRaw:X8} soundRepeat=0x{pending.AttackSoundRepeatRaw:X8} hitRaw=0x{hitRaw:X8} blockRaw=0x{blockRaw:X8} dmgRaw=0x{damageRaw:X8} effectRaw=0x{effectRaw:X8} impact=0x{impactSoundRaw:X8} hitRoll={hitRoll} threshold={hitChanceF32} critThreshold={critThreshold} critPct={critPercent} blockRoll={blockRoll} ar={attackRating} dr={defenseRating} levels={attackerLevel}->{hitDefenderLevel} result={combatResult} weaponClass={weaponClass} weaponClassId={damageInput.WeaponClassId} damageTypeId={damageInput.DamageTypeId} statSource={statSource} weaponDamageF32={weaponDmg} volF32={volatility} weaponLevel={pending.PlayerState.WeaponLevel} levelBonus={levelDamageBonus} clientBaseDamage={pending.PlayerState.WeaponBaseDamage} clientBaseSource={pending.PlayerState.WeaponBaseDamageSource} bonus={damageBonus} mod={damageMod} rangeWire=[{minDmg},{maxDmg}] rolledWire={damageWire} totalWire={damageResult.TotalDamageWire} addCount={(damageResult.DamageAdds != null ? damageResult.DamageAdds.Count : 0)} applied={applied} hp={oldHPWire}->{newHPWire} exact=projectile-weapon-stat-source");
            Debug.LogError($"[COMBAT-EVENT] actor=player player={pending.ConnKey} actorId={(pending.Connection?.Avatar != null ? pending.Connection.Avatar.Id : 0)} target=monster targetId={pending.Monster.EntityId} behaviorId={pending.BehaviorId} result={combatResult} projectile=True damageWire={damageWire} totalWire={damageResult.TotalDamageWire} addCount={(damageResult.DamageAdds != null ? damageResult.DamageAdds.Count : 0)} appliedDamage={appliedDamage} hp={oldHPWire}->{newHPWire} hitRaw=0x{hitRaw:X8} hitRoll={hitRoll} threshold={hitChanceF32} blockRaw=0x{blockRaw:X8} blockRoll={blockRoll} blockChance=0 damageRaw=0x{damageRaw:X8} effectRaw=0x{effectRaw:X8} resist=0 critThreshold={critThreshold} critPct={critPercent} rngAfter={rng.CallsSinceReseed}");
        }

        private bool TryResolveProjectileTargetAtImpact(PendingProjectileHit pending, out Monster hitMonster, out int hitDistanceF32, out bool worldBlocked)
        {
            hitMonster = null;
            hitDistanceF32 = pending != null ? Math.Max(0, pending.HitDistanceF32) : 0;
            worldBlocked = pending != null && pending.WorldBlocked;
            if (pending == null || pending.PlayerState == null || pending.Monster == null)
                return false;

            string zoneName = !string.IsNullOrWhiteSpace(pending.Monster.ZoneName)
                ? pending.Monster.ZoneName
                : pending.Connection?.CurrentZoneName;
            string instanceKey = pending.InstanceKey;
            if (string.IsNullOrWhiteSpace(instanceKey))
            {
                RuntimeEvidence.LogFallbackHit("rng-instance", "projectile-impact-missing-instance", $"seq={pending.Sequence} target={pending.TargetId}", 64);
                return false;
            }
            int impactFixedX = pending.CurrentFixedX;
            int impactFixedY = pending.CurrentFixedY;
            int impactFixedZ = pending.CurrentFixedZ;
            int impactDistanceFixed = Math.Max(0, pending.CurrentDistanceF32);
            int projectileSizeF32 = pending.ProjectileSizeF32 > 0
                ? pending.ProjectileSizeF32
                : pending.PlayerState.WeaponProjectileSizeF32 > 0
                    ? pending.PlayerState.WeaponProjectileSizeF32
                    : 0;
            int projectileRadiusFixed = ProjectileRadiusFromAuthoredSizeF32(projectileSizeF32);
            Monster best = null;
            int bestAlongFixed = int.MaxValue;
            long bestDistSqFixed = long.MaxValue;
            bool bestBlocked = worldBlocked;

            int scanRangeFixed = ProjectileScanRangeFixed(impactDistanceFixed, pending.HitDistanceF32, projectileRadiusFixed);
            uint projectileViewerEntityId = ResolveProjectileViewerEntityId(pending.Connection);
            var nativeOrderedCandidates = CombatRuntime.Instance.GetProjectileHittableMonstersInNativeDistanceOrder(
                impactFixedX,
                impactFixedY,
                impactFixedZ,
                instanceKey,
                zoneName,
                scanRangeFixed,
                playerEntityId: projectileViewerEntityId,
                useClientVisiblePosition: true);
            foreach (var candidate in nativeOrderedCandidates)
            {
                if (!CombatRuntime.Instance.IsProjectileHittableMonster(candidate, instanceKey, zoneName))
                    continue;

                if (!CombatRuntime.Instance.TryGetMonsterClientVisiblePositionFixed(candidate, projectileViewerEntityId, out int candidateFixedX, out int candidateFixedY))
                    continue;
                int candidateRadiusF32 = CombatRuntime.ResolveProjectileUnitCollisionRadiusF32(candidate);
                int hitRadiusFixed = ProjectileCollisionRadiusF32(candidateRadiusF32, projectileSizeF32);
                if (!TestProjectilePointUnitCollisionFixed(
                    impactFixedX,
                    impactFixedY,
                    candidateFixedX,
                    candidateFixedY,
                    hitRadiusFixed,
                    out long distSqFixed))
                    continue;

                if (CombatRuntime.IsProjectileHitBetterFixed(candidate, impactDistanceFixed, distSqFixed, best, bestAlongFixed, bestDistSqFixed))
                {
                    best = candidate;
                    bestAlongFixed = impactDistanceFixed;
                    bestDistSqFixed = distSqFixed;
                    bestBlocked = false;
                }
            }

            if (best == null)
                return false;

            hitMonster = best;
            hitDistanceF32 = Math.Max(0, bestAlongFixed);
            worldBlocked = bestBlocked;
            return true;
        }

        private static bool ResolveProjectileGroundZFixed(string zoneName, string instanceKey, int fixedX, int fixedY, int fallbackFixedZ, out int groundFixedZ, out string source)
        {
            groundFixedZ = fallbackFixedZ;
            source = null;
            string pathMapKey = ResolvePathMapKey(instanceKey, zoneName);
            PathMap pathMap = !string.IsNullOrWhiteSpace(pathMapKey) ? PathMapCatalog.Instance.GetPathMap(pathMapKey) : null;
            if (pathMap != null)
            {
                if (pathMap.TryGetHeightAtFixed(fixedX, fixedY, out int pathGroundFixedZ))
                {
                    groundFixedZ = pathGroundFixedZ;
                    source = $"PathMap:{pathMapKey}";
                    return true;
                }
            }
            if (WorldCollision.Instance.TryGetTerrainHeightWithinToleranceFixed(zoneName, instanceKey, fixedX, fixedY, fallbackFixedZ, PROJECTILE_HEIGHT_TOLERANCE_FIXED, out int groundZFixed, out source))
            {
                groundFixedZ = groundZFixed;
                source = $"WorldCollisionHeight:{source}";
                return true;
            }
            return false;
        }

        private static int ProjectileScanRangeFixed(int primaryDistanceFixed, int secondaryDistanceFixed, int projectileRadiusFixed)
        {
            long primary = (long)Math.Max(0, primaryDistanceFixed) + Math.Max(0, projectileRadiusFixed) + CLIENT_PROJECTILE_BROAD_SCAN_PADDING_FIXED;
            long secondary = (long)Math.Max(0, secondaryDistanceFixed) + Math.Max(0, projectileRadiusFixed) + CLIENT_PROJECTILE_BROAD_SCAN_PADDING_FIXED;
            long scan = Math.Max(primary, secondary);
            return scan > int.MaxValue ? int.MaxValue : (int)scan;
        }

        private static int ResolveProjectileBaseZFixed(RRConnection conn, Monster monster)
        {
            if (conn != null)
            {
                int ownerAckFixedZ = conn.OwnerAckFollowClientPosFixedZ;
                if (conn.HasOwnerAckFollowClientPosition && ownerAckFixedZ != 0)
                    return ownerAckFixedZ;
                int unitFollowFixedZ = conn.UnitFollowClientPosFixedZ;
                if (conn.HasUnitFollowClientPosition && unitFollowFixedZ != 0)
                    return unitFollowFixedZ;
                int reflectedFixedZ = conn.ReflectedAvatarPosFixedZ;
                if (conn.HasReflectedAvatarPosition && reflectedFixedZ != 0)
                    return reflectedFixedZ;
                int liveFixedZ = conn.LivePlayerPosFixedZ;
                if (conn.HasLivePlayerPosition && liveFixedZ != 0)
                    return liveFixedZ;
                int playerFixedZ = conn.PlayerPosFixedZ;
                if (playerFixedZ != 0)
                    return playerFixedZ;
            }
            if (monster != null)
            {
                int monsterFixedZ = monster.PosFixedZ;
                if (monsterFixedZ != 0)
                    return monsterFixedZ;
            }
            return 10 * UnitMover.Fixed;
        }

        private static bool IsConnectionUseTargetForProjectile(PendingProjectileHit pending)
        {
            if (pending == null || pending.Connection == null || !pending.Connection.HasActiveUseTarget)
                return false;

            uint activeTarget = pending.Connection.ActiveUseTargetId;
            uint requestedTarget = pending.RequestedTargetId != 0 ? pending.RequestedTargetId : pending.TargetId;
            return activeTarget == requestedTarget || activeTarget == pending.TargetId;
        }

        private bool ClearConsumedUseTarget(WeaponUseState cycle)
        {
            if (cycle == null || cycle.Connection == null) return false;
            if (!cycle.Connection.HasActiveUseTarget || cycle.Connection.ActiveUseTargetId != cycle.TargetId) return false;

            ClearConnectionUseTargetFields(cycle.Connection);

            uint avatarId = cycle.Connection.Avatar != null ? (uint)cycle.Connection.Avatar.Id : 0u;
            if (avatarId != 0)
                CombatRuntime.Instance.SetPlayerActiveClientAttack(avatarId, false);
            return true;
        }

        private bool HasPendingProjectileForCycle(WeaponUseState cycle)
        {
            if (cycle == null || _activeProjectiles.Count == 0)
                return false;

            for (int projectileIndex = 0; projectileIndex < _activeProjectiles.Count; projectileIndex++)
            {
                var pending = _activeProjectiles[projectileIndex];
                if (pending == null) continue;
                if (cycle.Connection != null && pending.Connection != null && !ReferenceEquals(cycle.Connection, pending.Connection))
                    continue;
                uint requestedTarget = pending.RequestedTargetId != 0 ? pending.RequestedTargetId : pending.TargetId;
                if (requestedTarget == cycle.TargetId || pending.TargetId == cycle.TargetId)
                    return true;
            }
            return false;
        }

        private static void ClearConnectionUseTargetFields(RRConnection conn)
        {
            if (conn == null) return;
            conn.HasActiveUseTarget = false;
            conn.ActiveUseTargetId = 0;
            conn.ActiveUseTargetRemoved = false;
            conn.ActiveUseTargetFlags = 0;
            conn.ActiveUseTargetComponentId = 0;
            conn.ActiveUseTargetSessionId = 0;
            conn.ActiveUseTargetResponseId = 0;
            conn.ActiveUseTargetActionFailureQueued = false;
            conn.ActiveUseTargetInitUsePassed = false;
            conn.ActiveUseTargetInitUseEvaluationTick = uint.MaxValue;
            conn.ActiveUseTargetInitUseEvaluationTargetId = 0;
            conn.ActiveUseTargetStartedWeaponUse = false;
            conn.ActiveUseTargetVisibleHit = false;
            conn.ActiveUseTargetInitUseRangeF32 = 0;
            conn.ActiveUseTargetInitUseDistanceF32 = 0;
            conn.ActiveUseTargetClientToleranceF32 = 0;
            conn.ActiveUseTargetLastProjectileSeq = 0;
            conn.ActiveUseTargetLastImpactTick = -1;
        }

        private bool HasPlayerMeleeContact(WeaponUseState cycle, int simulationTick, out int distanceFixed, out int rangeFixed)
        {
            distanceFixed = int.MaxValue;
            rangeFixed = 0;
            if (cycle == null || cycle.Connection == null || cycle.Monster == null) return false;
            if (!cycle.Connection.HasActiveUseTarget || cycle.Connection.ActiveUseTargetId != cycle.TargetId)
                return false;
            if (cycle.ServerApproachOnly)
            {
                int latchedDistanceFixed = cycle.Connection.ActiveUseTargetInitUseDistanceF32;
                int latchedRangeFixed = cycle.Connection.ActiveUseTargetInitUseRangeF32;
                distanceFixed = latchedDistanceFixed;
                rangeFixed = latchedRangeFixed;
                bool sameTick = simulationTick >= 0 && cycle.Connection.ActiveUseTargetInitUseEvaluationTick == (uint)simulationTick;
                bool sameTarget = cycle.Connection.ActiveUseTargetInitUseEvaluationTargetId == cycle.TargetId;
                bool passed = sameTick && sameTarget && cycle.Connection.ActiveUseTargetInitUsePassed;
                cycle.InitUsePassed = passed;
                cycle.InitUseRangeF32 = latchedRangeFixed;
                cycle.InitUseDistanceF32 = latchedDistanceFixed;
                cycle.InitUseToleranceF32 = cycle.Connection.ActiveUseTargetClientToleranceF32;
                return passed;
            }
            uint viewerEntityId = ResolveProjectileViewerEntityId(cycle.Connection);
            int monsterFixedX = cycle.Monster.PosFixedX;
            int monsterFixedY = cycle.Monster.PosFixedY;
            int monsterFixedZ = cycle.Monster.PosFixedZ;
            CombatRuntime.Instance.TryPeekMonsterClientVisiblePositionFixed(cycle.Monster, viewerEntityId, out monsterFixedX, out monsterFixedY, out monsterFixedZ);
            if (IsProjectileRangedCycle(cycle))
            {
                if (!cycle.Connection.HasActiveUseTarget || cycle.Connection.ActiveUseTargetId != cycle.TargetId)
                    return false;

                bool wasPassed = cycle.InitUsePassed;
                int toleranceFixed = CombatRuntime.Instance.ResolveUseTargetClientSyncToleranceF32(cycle.PlayerState);
                const string source = "ManipulatorDesc+0x6c init-use reach reader=UseTarget::CheckInitUse@0x00548980";
                int initUseRangeFixed = CombatRuntime.Instance.ResolveUseTargetInitUseRangeF32(cycle.PlayerState, cycle.Monster);
                int actorFixedX = cycle.Connection.HasLivePlayerPosition ? cycle.Connection.LivePlayerPosFixedX : cycle.Connection.PlayerPosFixedX;
                int actorFixedY = cycle.Connection.HasLivePlayerPosition ? cycle.Connection.LivePlayerPosFixedY : cycle.Connection.PlayerPosFixedY;
                int actorFixedZ = cycle.Connection.HasLivePlayerPosition ? cycle.Connection.LivePlayerPosFixedZ : cycle.Connection.PlayerPosFixedZ;
                bool initUsePassed = CombatRuntime.Instance.EvaluateUseTargetInitUseFixed3D(
                    actorFixedX,
                    actorFixedY,
                    actorFixedZ,
                    monsterFixedX,
                    monsterFixedY,
                    monsterFixedZ,
                    initUseRangeFixed,
                    toleranceFixed,
                    out int evaluatedDistanceFixed,
                    out long distanceSqFixed8,
                    out long thresholdSqFixed8);
                distanceFixed = evaluatedDistanceFixed;

                if (cycle.Connection.HasActiveUseTarget && cycle.Connection.ActiveUseTargetId == cycle.TargetId)
                {
                    cycle.Connection.ActiveUseTargetInitUsePassed = initUsePassed;
                    cycle.Connection.ActiveUseTargetInitUseRangeF32 = initUseRangeFixed;
                    cycle.Connection.ActiveUseTargetInitUseDistanceF32 = evaluatedDistanceFixed;
                    cycle.Connection.ActiveUseTargetClientToleranceF32 = toleranceFixed;
                }
                cycle.InitUsePassed = initUsePassed;
                cycle.InitUseRangeF32 = initUseRangeFixed;
                cycle.InitUseDistanceF32 = evaluatedDistanceFixed;
                cycle.InitUseToleranceF32 = toleranceFixed;
                bool clearShot = initUsePassed;
                if (initUsePassed && !wasPassed)
                    Debug.LogError($"[USETARGET-INIT] target={cycle.Monster.EntityId} behavior={cycle.Monster.BehaviorId} component={cycle.UseTargetComponentId} session={cycle.UseTargetSessionId} distF32={evaluatedDistanceFixed} distSqFixed8={distanceSqFixed8} initUseRangeF32={initUseRangeFixed} toleranceF32={toleranceFixed} thresholdSqFixed8={thresholdSqFixed8} source={source} weaponRangeF32={(cycle.PlayerState != null && cycle.PlayerState.WeaponRangeF32 > 0 ? cycle.PlayerState.WeaponRangeF32 : 0x100)} clearShot={clearShot} result={(clearShot ? "use" : "moving")} rngBefore=-1 rngAfter=-1 sourceFunction=UseTarget::CheckInitUse+Unit::vtbl0xe8");
                rangeFixed = initUseRangeFixed;
                return clearShot;
            }

            int meleeActorFixedX = cycle.Connection.HasLivePlayerPosition ? cycle.Connection.LivePlayerPosFixedX : cycle.Connection.PlayerPosFixedX;
            int meleeActorFixedY = cycle.Connection.HasLivePlayerPosition ? cycle.Connection.LivePlayerPosFixedY : cycle.Connection.PlayerPosFixedY;
            int meleeActorFixedZ = cycle.Connection.HasLivePlayerPosition ? cycle.Connection.LivePlayerPosFixedZ : cycle.Connection.PlayerPosFixedZ;
            int meleeRangeFixed = CombatRuntime.Instance.ResolvePlayerMeleeRangeF32(cycle.PlayerState, cycle.Monster);
            const int meleeToleranceFixed = CLIENT_CONTACT_RANGE_EPSILON_F32;
            bool meleeContact = CombatRuntime.Instance.EvaluateUseTargetInitUseFixed3D(
                meleeActorFixedX,
                meleeActorFixedY,
                meleeActorFixedZ,
                monsterFixedX,
                monsterFixedY,
                monsterFixedZ,
                meleeRangeFixed,
                meleeToleranceFixed,
                out int meleeDistanceFixed,
                out _,
                out _);
            distanceFixed = meleeDistanceFixed;

            cycle.Connection.ActiveUseTargetInitUsePassed = meleeContact;
            cycle.Connection.ActiveUseTargetInitUseRangeF32 = meleeRangeFixed;
            cycle.Connection.ActiveUseTargetInitUseDistanceF32 = meleeDistanceFixed;
            cycle.Connection.ActiveUseTargetClientToleranceF32 = meleeToleranceFixed;
            cycle.InitUsePassed = meleeContact;
            cycle.InitUseRangeF32 = meleeRangeFixed;
            cycle.InitUseDistanceF32 = meleeDistanceFixed;
            cycle.InitUseToleranceF32 = meleeToleranceFixed;
            rangeFixed = meleeRangeFixed;
            return meleeContact;
        }

        public CompletedAttack DequeueKill()
        {
            return _completedAttacks.Count > 0 ? _completedAttacks.Dequeue() : null;
        }

        public bool HasPendingKills => _completedAttacks.Count > 0;

        private readonly Queue<RRConnection> _pendingControlReleases = new Queue<RRConnection>();
        public bool HasPendingControlReleases => _pendingControlReleases.Count > 0;
        public RRConnection DequeueControlRelease() => _pendingControlReleases.Count > 0 ? _pendingControlReleases.Dequeue() : null;

        private void EnqueueControlRelease(RRConnection conn)
        {
            if (conn == null || !conn.HasActiveUseTarget) return;
            if (!_pendingControlReleases.Contains(conn))
                _pendingControlReleases.Enqueue(conn);
        }

        public void ClearConnection(string connKey)
        {
            if (!string.IsNullOrEmpty(connKey) && _activeCycles.TryGetValue(connKey, out var cycle))
            {
                RememberUseCooldown(connKey, cycle.NextUseTick);
                RemovePlayerCycleBinding(connKey, cycle);
            }
            _activeCycles.Remove(connKey);
        }

        public void CancelConnectionUseTargetIntent(string connKey, string source = null)
        {
            if (string.IsNullOrEmpty(connKey))
                return;
            int preservedReadyTick = 0;
            bool removedCycle = false;
            bool preservedActiveCycle = false;
            if (_activeCycles.TryGetValue(connKey, out var cycle))
            {
                preservedReadyTick = cycle.NextUseTick;
                RememberUseCooldown(connKey, preservedReadyTick);
                if (cycle.IsActive)
                {
                    cycle.PendingRepeatUses = 0;
                    cycle.RepeatUsePendingAtRelease = false;
                    preservedActiveCycle = true;
                }
                else
                {
                    RemovePlayerCycleBinding(connKey, cycle);
                    removedCycle = _activeCycles.Remove(connKey);
                }
            }
            int preservedProjectiles = 0;
            for (int projectileIndex = 0; projectileIndex < _activeProjectiles.Count; projectileIndex++)
            {
                if (_activeProjectiles[projectileIndex] != null && string.Equals(_activeProjectiles[projectileIndex].ConnKey, connKey, StringComparison.Ordinal))
                    preservedProjectiles++;
            }
            int nowTick = NowTick;
            int preservedNextInTicks = preservedReadyTick > nowTick ? preservedReadyTick - nowTick : 0;
            Debug.LogError($"[WEAPON-USE] {connKey} cancel-use-target-intent source={source ?? "unknown"} removedCycle={removedCycle} preservedActiveCycle={preservedActiveCycle} preservedProjectiles={preservedProjectiles} preservedReadyTick={preservedReadyTick} preservedNextInTicks={preservedNextInTicks} sourceFunction=MeleeWeapon::update@0x00591980");
        }

        public void Clear()
        {
            foreach (var pending in _activeProjectiles)
                if (pending != null)
                    CombatRuntime.Instance.RemoveClientSubEntity(ClientSubEntityKind.PlayerWeaponProjectile, pending.Sequence, pending.InstanceKey);
            _activeCycles.Clear();
            _cycleKeyByPlayerEntity.Clear();
            _playerEntityByCycleKey.Clear();
            _nextUseReadyTickByConnection.Clear();
            _completedAttacks.Clear();
            _activeProjectiles.Clear();
            _nextProjectileSequence = 0;
        }
    }

    public class WeaponUseState
    {
        public bool IsActive;
        public bool TargetDiedDuringCycle;
        public ushort TargetId;
        public Monster Monster;
        public PlayerState PlayerState;
        public RRConnection Connection;
        public string InstanceKey;
        public int TickCounter;
        public int CycleStartTick;
        public bool ProcFired;
        public bool HitFired;
        public uint LastDirectHitSimulationTick;
        public uint LastDirectHitPreviousHPWire;
        public uint LastDirectHitNewHPWire;
        public bool AttackSoundFired;
        public int RangedHitEventsFired;
        public int RangedSoundEventsFired;
        public bool UseRngConsumed;
        public uint UseRaw;
        public uint AttackSoundSelectRaw;
        public uint AttackSoundGateRaw;
        public uint AttackSoundRepeatRaw;
        public uint ImpactSoundRaw;
        public byte AttackAnimationIndex;
        public int SwingCount;
        public int PendingRepeatUses;
        public bool RepeatUsePendingAtRelease;
        public bool AwaitingContact;
        public bool ServerApproachOnly;
        public int UseTargetUseNotBeforeTick;
        public bool ContactHoldLogged;
        public bool InitUsePassed;
        public int InitUseRangeF32;
        public int InitUseDistanceF32;
        public int InitUseToleranceF32;
        public ushort UseTargetComponentId;
        public byte UseTargetSessionId;
        public byte UseTargetFlags;
        public bool UsePositionAction;
        public byte UsePositionManipulatorId;
        public int UsePositionTargetFixedX;
        public int UsePositionTargetFixedY;
        public int UsePositionTargetFixedZ;
        public int LastSimulationTick;
        public int InputAdmissionTick;
        public int NextUseTick;
        public int AttackTotalFrames;
        public int AttackHitFrame;
        public int AttackSoundFrame;
        public bool AttackTimingResolved;
        public int AttackAnimationId;
        public bool AttackSourceOffsetResolved;
        public int AttackSourceOffsetFixedX;
        public int AttackSourceOffsetFixedY;
        public int AttackSourceOffsetFixedZ;
        public bool ProjectileTargetSnapshotResolved;
        public int ProjectileTargetFixedX;
        public int ProjectileTargetFixedY;
    }

    public class CompletedAttack
    {
        public string ConnKey;
        public RRConnection Connection;
        public Monster Monster;
        public bool Killed;
        public int DamageDealt;
    }

    public class PendingProjectileHit
    {
        public long Sequence;
        public string ConnKey;
        public RRConnection Connection;
        public PlayerState PlayerState;
        public Monster Monster;
        public string InstanceKey;
        public uint RequestedTargetId;
        public uint TargetId;
        public uint BehaviorId;
        public int Swing;
        public int Tick;
        public int FireTick;
        public int FlightTicks;
        public int ImpactDelayTicks;
        public int DueTick;
        public int HitDistanceF32;
        public long PathDistanceF16;
        public int PathDistanceF32;
        public bool WorldBlocked;
        public uint UseRaw;
        public uint AttackSoundSelectRaw;
        public uint AttackSoundGateRaw;
        public uint AttackSoundRepeatRaw;
        public WeaponDamageInput DamageInput;
        public bool DamagePositionResolved;
        public int DamagePositionFixedX;
        public int DamagePositionFixedY;
        public int DamagePositionFixedZ;
        public int StartFixedX;
        public int StartFixedY;
        public int StartFixedZ;
        public int CurrentFixedX;
        public int CurrentFixedY;
        public int CurrentFixedZ;
        public int DirectionFixedX;
        public int DirectionFixedY;
        public int VelocityFixedX;
        public int VelocityFixedY;
        public int VelocityFixedZ;
        public int GroundOffsetFixedZ;
        public bool GroundOffsetResolved;
        public string GroundSource;
        public bool SourceOffsetResolved;
        public int SourceOffsetFixedX;
        public int SourceOffsetFixedY;
        public int SourceOffsetFixedZ;
        public int TargetFixedX;
        public int TargetFixedY;
        public int ProjectileSpeedF32;
        public int ProjectileSizeF32;
        public int StepDistanceF32;
        public int InitialDistanceF32;
        public int CurrentDistanceF32;
        public int MaxDistanceF32;
        public int MaxLifetimeTicks;
        public int LastUpdateTick;
        public int UpdatesCompleted;
        public bool InitUsePassed;
        public int InitUseRangeF32;
        public int InitUseDistanceF32;
        public int InitUseToleranceF32;
        public bool ImpactResolved;
        public bool UsePositionAction;
    }

    public class DueDrainSummary
    {
        public int PendingBefore;
        public int PendingAfter;
        public int Drained;
        public int MatchingDrained;
        public bool Stopped;
        public int NextDueTick = -1;
    }

}
