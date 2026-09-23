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
    {        private void HandlePlayerAttackMonster(RRConnection conn, ushort componentId, byte responseId,
     byte manipulatorId, byte useFlags, ushort targetId, Combat.Monster monster, int mirrorAdmissionTicks = ClientUseTargetMirrorAdmissionTicks, ulong unitFollowClientWaitThroughOverride = ulong.MaxValue, int inputAdmissionTick = 0, bool behaviorChildStart = false)
        {
            Debug.LogError($"[ATTACK] action=useTarget monster='{monster.Name}' targetId={targetId} sessionCtr={manipulatorId} slotId={useFlags} responseId={responseId}");
            bool isSkillAction = useFlags >= 100;
            string attackConnKey = conn?.ConnId.ToString() ?? string.Empty;
            int effectiveMirrorAdmissionTicks = mirrorAdmissionTicks;
            if (!isSkillAction && conn != null)
            {
                effectiveMirrorAdmissionTicks = Math.Max(
                    effectiveMirrorAdmissionTicks,
                    Combat.WeaponUseRuntime.Instance.GetUseTargetSwitchAdmissionDelayTicks(attackConnKey, targetId));
            }
            bool inAttackRange = true;
            int attackDistanceF32 = 0;
            int attackRangeF32 = 0;
            int clientAttackRangeF32 = 0;
            int clientInitUseRangeF32 = 0;
            int clientInitUseDistanceF32 = 0;
            int initUseToleranceF32 = 0;
            bool clientInitUsePassed = false;
            bool clientMeleeContact = false;
            bool clientRangedBasic = false;
            bool clientRangedProjectileBasic = false;
            bool canStartWeaponUseState = false;
            int activeUseTargetRangeF32 = 0;
            int activeUseTargetDistanceF32 = 0;
            int activeUseTargetToleranceF32 = 0;
            int weaponUseDistanceF32 = 0;
            int weaponUseRangeF32 = 0;
            PlayerState state = null;
            Combat.SpellData actionSpell = null;
            if (monster.IsAlive)
                state = GetPlayerState(conn.ConnId.ToString());
            bool zoneInvulnerabilityBlocking = IsZoneSpawnInvulnerabilityActive(conn);
            if (zoneInvulnerabilityBlocking)
            {
                ClearZoneSpawnInvulnerability(conn, $"ACTION-0x50 flags={useFlags} target={targetId}");
                zoneInvulnerabilityBlocking = false;
            }
            if (ShouldQueuePendingUseTargetBehaviorAction(conn, useFlags, monster))
            {
                QueuePendingUseTargetBehaviorAction(conn, componentId, responseId, manipulatorId, useFlags, targetId, monster);
                return;
            }
            if (monster.IsAlive && !zoneInvulnerabilityBlocking)
            {
                clientRangedBasic = !isSkillAction && state != null && Combat.DamageResolver.IsRangedWeapon(state);
                clientRangedProjectileBasic = clientRangedBasic && Combat.DamageResolver.IsProjectileWeapon(state);
                if (!isSkillAction)
                {
                    inAttackRange = clientRangedBasic
                        ? IsRangedBasicTargetWithinServerRange(conn, state, monster, out attackDistanceF32, out attackRangeF32)
                        : IsMeleeTargetWithinServerRange(conn, state, monster, out attackDistanceF32, out attackRangeF32);
                }
                else
                {
                    actionSpell = ResolveActionSpell(conn, state, useFlags);
                    bool withinServerRange = IsSkillTargetWithinServerRange(conn, actionSpell, monster, out attackDistanceF32, out attackRangeF32);
                    inAttackRange = withinServerRange;
                    if (!withinServerRange)
                        Debug.LogError($"[SPELL] action=targetIsClear state=outsideRange spell={actionSpell?.DisplayName ?? "UNKNOWN"} target={monster.Name} distF32={attackDistanceF32} rangeF32={attackRangeF32} sourceFunction=ActiveSkill::targetIsClear@0x00538A40 result=no-use");
                }
                if (!isSkillAction)
                {
                    clientInitUseRangeF32 = Combat.CombatRuntime.Instance.ResolveUseTargetInitUseRangeF32(state, monster);
                    initUseToleranceF32 = Combat.CombatRuntime.Instance.ResolveUseTargetClientSyncToleranceF32(state);
                    uint initViewerId = conn.Avatar != null ? (uint)conn.Avatar.Id : 0u;
                    Combat.CombatRuntime.Instance.TryPeekMonsterClientVisiblePositionFixed(monster, initViewerId, out int initTargetFixedX, out int initTargetFixedY, out int initTargetFixedZ);
                    ResolveAuthoritativePlayerPositionFixed(conn, out int initActorFixedX, out int initActorFixedY, out int initActorFixedZ);
                    clientInitUsePassed = Combat.CombatRuntime.Instance.EvaluateUseTargetInitUseFixed3D(
                        initActorFixedX, initActorFixedY, initActorFixedZ, initTargetFixedX, initTargetFixedY, initTargetFixedZ,
                        clientInitUseRangeF32, initUseToleranceF32, out clientInitUseDistanceF32,
                        out _, out _);
                    clientAttackRangeF32 = attackRangeF32;
                    clientMeleeContact = inAttackRange;
                }
                if (!isSkillAction && state != null)
                {
                    int playerFixedX = conn != null ? conn.PlayerPosFixedX : 0;
                    int playerFixedY = conn != null ? conn.PlayerPosFixedY : 0;
                    string lane = clientRangedBasic ? "ranged" : "melee";
                    int projectileReachF32 = 0;
                    if (clientRangedProjectileBasic)
                    {
                        int projectileSizeF32 = Math.Max(0, state.WeaponProjectileSizeF32);
                        int projectileSpeedF32 = Math.Max(0, state.WeaponProjectileSpeedF32);
                        projectileReachF32 = WeaponUseRuntime.ProjectileRadiusFromAuthoredSizeF32(projectileSizeF32)
                            + (projectileSpeedF32 > 0 ? WeaponUseRuntime.ProjectileStepDistanceF32(projectileSpeedF32) : 0);
                    }
                    uint logViewerId = conn.Avatar != null ? (uint)conn.Avatar.Id : 0u;
                    CombatRuntime.Instance.TryPeekMonsterClientVisiblePositionFixed(monster, logViewerId, out int logTargetFixedX, out int logTargetFixedY, out _);
                    Debug.LogError($"[ATTACK-RANGE] lane={lane} weaponClass={state.WeaponClass} weaponRange={state.WeaponRange} weaponSpeedF32={state.WeaponSpeedF32} useProjectile={state.WeaponUsesProjectile} projectileSpeedF32={state.WeaponProjectileSpeedF32} projectileSizeF32={state.WeaponProjectileSizeF32} projectileReachF32={projectileReachF32} playerFixed=({playerFixedX},{playerFixedY}) monsterFixed=({logTargetFixedX},{logTargetFixedY}) distF32={attackDistanceF32} rangeF32={attackRangeF32} clientRangeF32={clientAttackRangeF32} initUseRangeF32={clientInitUseRangeF32} initUse={clientInitUsePassed} inRange={inAttackRange} clientContact={clientMeleeContact} flags={useFlags} target={targetId}");
                }
                if (!isSkillAction)
                {
                    canStartWeaponUseState = inAttackRange;
                    weaponUseDistanceF32 = attackDistanceF32;
                    weaponUseRangeF32 = attackRangeF32;
                    activeUseTargetRangeF32 = attackRangeF32;
                    activeUseTargetDistanceF32 = attackDistanceF32;
                    activeUseTargetToleranceF32 = 0;
                    ActivateUseTarget(conn, targetId, useFlags, componentId, manipulatorId, responseId: responseId);
                    if (conn != null)
                    {
                        conn.ActiveUseTargetInitUsePassed = false;
                        conn.ActiveUseTargetInitUseRangeF32 = activeUseTargetRangeF32;
                        conn.ActiveUseTargetInitUseDistanceF32 = activeUseTargetDistanceF32;
                        conn.ActiveUseTargetClientToleranceF32 = activeUseTargetToleranceF32;
                    }
                }
                if (conn != null && monster.IsAlive)
                {
                    if (isSkillAction)
                    {
                        if (!inAttackRange)
                        {
                            uint movementViewerId = conn.Avatar != null ? (uint)conn.Avatar.Id : 0u;
                            Combat.CombatRuntime.Instance.TryPeekMonsterClientVisiblePositionFixed(monster, movementViewerId, out int targetFixedX, out int targetFixedY, out _);
                         StartUseTargetMoving(conn, targetFixedX, targetFixedY, entityId: (ushort)monster.EntityId, mirrorWithAction: true, admissionTicks: mirrorAdmissionTicks, includeQueuedUnitFollowClientRecords: false, unitFollowClientWaitThroughOverride: unitFollowClientWaitThroughOverride);
                            Debug.LogError($"[ATTACK] Target action starts from current owner mover pose target=0x{monster.EntityId:X4} flags={useFlags} targetFixed=({targetFixedX},{targetFixedY}) nativeClientApproach=False sourceFunction=UseTarget::doClientApproach@0x0044B680");
                        }
                        else
                            CancelUseTargetMoving(conn, "use-ready");
                    }
                    else
                    {
                        uint movementViewerId = conn.Avatar != null ? (uint)conn.Avatar.Id : 0u;
                        Combat.CombatRuntime.Instance.TryPeekMonsterClientVisiblePositionFixed(monster, movementViewerId, out int targetFixedX, out int targetFixedY, out _);
                         StartUseTargetMoving(conn, targetFixedX, targetFixedY, entityId: (ushort)monster.EntityId, mirrorWithAction: true, admissionTicks: effectiveMirrorAdmissionTicks, includeQueuedUnitFollowClientRecords: false, unitFollowClientWaitThroughOverride: unitFollowClientWaitThroughOverride);
                        Debug.LogError($"[ATTACK] Basic target action deferred to scheduled targetIsClear and current owner mover pose target=0x{monster.EntityId:X4} flags={useFlags} targetFixed=({targetFixedX},{targetFixedY}) nativeClientApproach=False sourceFunction=UseTarget::States@0x00548370");
                    }
                }
                if (state != null)
                {
                     string atkKey = attackConnKey;
                    var oldTarget = Combat.WeaponUseRuntime.Instance.GetActiveTarget(atkKey);
                    bool targetSwitch = oldTarget != null && oldTarget.EntityId != monster.EntityId;
                    if (targetSwitch)
                    {
                        Debug.LogError($"[ATTACK] Target switch from {oldTarget.Name} to {monster.Name}");
                    }
                    monster.UseTargetCount++;
                    if (isSkillAction)
                    {
                        if (inAttackRange)
                        {
                            ActivateUseTarget(conn, targetId, useFlags, componentId, manipulatorId, responseId: responseId);
                            conn.ActiveUseTargetInitUsePassed = true;
                            conn.ActiveUseTargetInitUseRangeF32 = attackRangeF32;
                            conn.ActiveUseTargetInitUseDistanceF32 = attackDistanceF32;
                            conn.ActiveUseTargetClientToleranceF32 = Math.Max(0, actionSpell?.ClientSyncToleranceF32 ?? 0);
                            QueueTargetSkillUse(conn, state, monster, actionSpell, useFlags, useFlags, componentId, "UseTarget", behaviorChildStart);
                        }
                        else
                        {
                            ActivateUseTarget(conn, targetId, useFlags, componentId, manipulatorId, responseId: responseId);
                            if (conn != null)
                            {
                                conn.ActiveUseTargetInitUsePassed = false;
                                conn.ActiveUseTargetInitUseRangeF32 = attackRangeF32;
                                conn.ActiveUseTargetInitUseDistanceF32 = attackDistanceF32;
                                conn.ActiveUseTargetClientToleranceF32 = Math.Max(0, actionSpell?.ClientSyncToleranceF32 ?? 0);
                            }
                            Debug.LogError($"[SPELL] Target outside range: {actionSpell?.DisplayName ?? "UNKNOWN"} on {monster.Name} distF32={attackDistanceF32} rangeF32={attackRangeF32} action=defer-use-target");
                        }
                    }
                    else
                    {
                        string weaponLane = clientRangedBasic ? "RangedWeapon" : "Melee";
                        if (clientRangedProjectileBasic)
                        {
                            Combat.WeaponUseRuntime.Instance.RegisterAttack(atkKey, targetId, monster, state, conn, canStartWeaponUseState, weaponUseDistanceF32, weaponUseRangeF32, requiresScheduledInitUse: true, inputAdmissionTick: inputAdmissionTick);
                            Debug.LogError($"[ATTACK] {weaponLane} UseTarget armed: slotId={useFlags} on {monster.Name} sessionCtr={manipulatorId} distF32={attackDistanceF32} initUseRangeF32={clientInitUseRangeF32} weaponRange={state.WeaponRange} contactRangeF32={weaponUseRangeF32} actionRangeF32={attackRangeF32} currentInitUseWouldPass={clientInitUsePassed} inInitRange={inAttackRange} processedByUseTargetTick={canStartWeaponUseState} rngAdvanced=False hpMutated=False");
                        }
                        else
                        {
                            Debug.LogError($"[ATTACK] {weaponLane}: slotId={useFlags} on {monster.Name} sessionCtr={manipulatorId} distF32={attackDistanceF32} rangeF32={attackRangeF32} clientRangeF32={clientAttackRangeF32} canStart={canStartWeaponUseState}");
                            Combat.WeaponUseRuntime.Instance.RegisterAttack(atkKey, targetId, monster, state, conn, canStartWeaponUseState, weaponUseDistanceF32, weaponUseRangeF32, requiresScheduledInitUse: true, inputAdmissionTick: inputAdmissionTick);
                        }
                    }
                }
                else
                {
                    Debug.LogError($"[COMBAT] no PlayerState for conn={conn.ConnId}; skipping damage");
                }
            }

            if (!zoneInvulnerabilityBlocking && conn?.Avatar != null && monster.IsAlive)
                Combat.CombatRuntime.Instance.SetPlayerActiveClientAttack((uint)conn.Avatar.Id, true, monster.EntityId);
            Debug.LogError($"[ATTACK] action=applyUseTarget alive={monster.IsAlive} playerHp={state?.EntitySynchInfoHP ?? 0} targetHp={CombatRuntime.Instance.PeekMonsterCurrentHPWire(monster)}/{monster.MaxHPWire}");
            if (!monster.IsAlive && IsUseTargetingMonster(conn, monster))
                ClearUseTargetAndReleaseControl(conn, "ATTACK-target-dead", componentId);
        }

        private bool TryQueueActiveSkillUseTarget(RRConnection conn, string source)
        {
            if (conn == null || !conn.HasActiveUseTarget || conn.ActiveUseTargetFlags < 100)
                return false;
            if (conn.ActiveUseTargetPlayerConnId != 0)
                return TryBeginPvpTargetSkill(conn, _combatTick, true);
            if (IsBlingGnomeSkillTargetAvailable(conn, conn.ActiveUseTargetFlags, conn.ActiveUseTargetId))
                return TryBeginBlingGnomeSkill(conn, _combatTick, true);
            var monster = CombatRuntime.Instance.GetMonster(conn.ActiveUseTargetId)
                       ?? CombatRuntime.Instance.GetMonsterByComponent(conn.ActiveUseTargetId);
            if (monster == null || !monster.IsAlive || CombatRuntime.Instance.PeekMonsterCurrentHPWire(monster) == 0)
                return false;
            var state = GetPlayerState(conn.ConnId.ToString());
            if (state == null)
                return false;
            var spell = ResolveActionSpell(conn, state, conn.ActiveUseTargetFlags);
            if (spell == null)
                return false;
            monster.UseTargetCount++;
            return QueueTargetSkillUse(conn, state, monster, spell, conn.ActiveUseTargetFlags, conn.ActiveUseTargetFlags, conn.ActiveUseTargetComponentId, source ?? "UseTarget", true);
        }

        private bool QueueTargetSkillUse(RRConnection conn, PlayerState state, Combat.Monster monster, Combat.SpellData actionSpell, byte manipulatorId, byte useFlags, ushort componentId, string source, bool behaviorChildStart = false)
        {
            if (conn == null || state == null || monster == null || actionSpell == null)
                return false;
            uint sourceEntityId = conn.Avatar != null ? (uint)conn.Avatar.Id : 0u;
            if (!Combat.SpellDatabase.TryValidatePlayerPveTargetRuntime(actionSpell, out string targetRuntimeReason))
            {
                SkillEffectTracker.RecordPlayerGraph(sourceEntityId, monster.EntityId, actionSpell.SkillId, actionSpell.TargetType, actionSpell.OrderedEffects?.Count ?? 0, "rejected", targetRuntimeReason, _combatTick);
                Debug.LogError($"[SKILL-USE] action=target result=rejected manipId={manipulatorId} gc={actionSpell.SkillId} target={monster.EntityId} reason={targetRuntimeReason ?? "unsupported-player-pve-target-effect"} sourceFunction=ActiveSkill::validateUse@0x00538710");
                return false;
            }
            SkillEffectTracker.RecordPlayerGraph(sourceEntityId, monster.EntityId, actionSpell.SkillId, actionSpell.TargetType, actionSpell.OrderedEffects.Count, "accepted", null, _combatTick);
            uint skillUseTick = _combatTick;
            bool skillUseCommitted = false;
            if (behaviorChildStart)
            {
                int skillLevel = GetPlayerSkillLevel(conn, actionSpell);
                skillUseCommitted = CommitActiveSkillUse(conn, state, actionSpell, componentId, useFlags, skillLevel, unchecked((int)skillUseTick), true);
                if (!skillUseCommitted)
                    return false;
            }
            uint viewerId = conn.Avatar != null ? (uint)conn.Avatar.Id : 0u;
            CombatRuntime.Instance.TryGetMonsterClientVisiblePositionFixed(
                monster,
                viewerId,
                out int aimFixedX,
                out int aimFixedY);
            ResolveAuthoritativePlayerPositionFixed(conn, out int sourceFixedX, out int sourceFixedY, out _);
            int projectileHitDistanceF32 = UnitMover.IntSqrt(
                (long)(aimFixedX - sourceFixedX) * (aimFixedX - sourceFixedX)
                + (long)(aimFixedY - sourceFixedY) * (aimFixedY - sourceFixedY));
            bool weaponProjectileRuntime = actionSpell.HasImmediateWeaponDamageEffect
                && state.WeaponUsesProjectile
                && state.WeaponProjectileSizeF32 > 0
                && state.WeaponProjectileSpeedF32 > 0;
            if (actionSpell.ProjectileSizeF32 > 0 && actionSpell.ProjectileSpeedF32 > 0 || weaponProjectileRuntime)
            {
                var pending = CreatePendingSpellProjectileFixed(
                    conn,
                    state,
                    monster,
                    actionSpell,
                    manipulatorId,
                    useFlags,
                    componentId,
                    sourceFixedX,
                    sourceFixedY,
                    aimFixedX,
                    aimFixedY,
                    false,
                    projectileHitDistanceF32,
                    false,
                    behaviorChildStart,
                    skillUseTick,
                    0,
                    0,
                    0);
                pending.SkillUseCommitted = skillUseCommitted;
                _pendingSpells.Enqueue(pending);
                Debug.LogError($"[SPELL] UseTarget projectile runtime: slotId={useFlags} on {monster.Name} fireTick={pending.FireTick} firstUpdateTick={pending.FireTick + 1} delayTicks={pending.ProjectileDelayTicks} hitHintF32={pending.ProjectileHitDistanceF32} speedF32={pending.ProjectileSpeedF32} stepF32={pending.StepDistanceF32} initPreStepF32={pending.InitialDistanceF32} maxDistF32={pending.MaxDistanceF32} seq={pending.Sequence} source={source ?? "UseTarget"}");
                return true;
            }

            int projectileDelayTicks = ResolveProjectileImpactDelayTicksFixed32(actionSpell, state, projectileHitDistanceF32);
            int fireTick = behaviorChildStart
                ? ResolveActiveSkillEffectTickAfterSkillsChild(skillUseTick, actionSpell, state)
                : ResolveActiveSkillEffectTickBeforeSkillsChild(skillUseTick, actionSpell, state);
            int dueTick = fireTick + projectileDelayTicks;
            _pendingSpells.Enqueue(new PendingSpell { Sequence = ++_nextPendingSpellProjectileSequence, Conn = conn, State = state, Monster = monster, Spell = actionSpell, ManipId = manipulatorId, UseFlags = useFlags, ComponentId = componentId, StartFixedX = sourceFixedX, StartFixedY = sourceFixedY, AimFixedX = aimFixedX, AimFixedY = aimFixedY, InstanceKey = GetInstanceZoneKey(conn), SkillUseTick = skillUseTick, StartsAfterSkillsChild = behaviorChildStart, FireTick = fireTick, DueTick = dueTick, ProjectileHitDistanceF32 = projectileHitDistanceF32, ProjectileDelayTicks = projectileDelayTicks, SkillUseCommitted = skillUseCommitted });
            Debug.LogError($"[SPELL] UseTarget spell: slotId={useFlags} on {monster.Name} queued skillUseTick={skillUseTick} fireTick={fireTick} delayTicks={projectileDelayTicks} hitDistF32={projectileHitDistanceF32} speedF32={actionSpell.ProjectileSpeedF32} dueTick={dueTick} source={source ?? "UseTarget"}");
            return true;
        }

        private static string GetActiveSkillBusyKey(RRConnection conn, ushort componentId, byte actionId)
        {
            int connId = conn != null ? conn.ConnId : 0;
            return $"{connId}:{componentId}:{actionId}";
        }

        private static string GetActiveSkillCooldownKey(RRConnection conn, Combat.SpellData spell, byte actionId)
        {
            int connId = conn != null ? conn.ConnId : 0;
            string skillIdentity = !string.IsNullOrWhiteSpace(spell?.SkillId)
                ? spell.SkillId.Trim().Replace('\\', '.').Replace('/', '.').ToUpperInvariant()
                : !string.IsNullOrWhiteSpace(spell?.ShortName)
                    ? spell.ShortName.Trim().ToUpperInvariant()
                    : $"ACTION-{actionId}";
            return $"{connId}:skill:{skillIdentity}";
        }

        private void ClearActiveSkillBusyRuntimeForConnection(RRConnection conn)
        {
            if (conn == null)
                return;
            if (conn.Avatar != null)
                _friendlySummonCasts.Remove((uint)conn.Avatar.Id);
            string keyPrefix = conn.ConnId + ":";
            for (int keyIndex = _activeSkillBusyKeys.Count - 1; keyIndex >= 0; keyIndex--)
            {
                string key = _activeSkillBusyKeys[keyIndex];
                if (!key.StartsWith(keyPrefix, StringComparison.Ordinal))
                    continue;
                _activeSkillBusyUntilTick.Remove(key);
                _activeSkillBusyKeys.RemoveAt(keyIndex);
            }
        }

        private void ClearActiveSkillCooldownRuntimeForConnection(RRConnection conn)
        {
            if (conn == null)
                return;
            string keyPrefix = conn.ConnId + ":";
            List<string> cooldownKeys = _activeSkillCooldownUntilTick.Keys
                .Where(key => key.StartsWith(keyPrefix, StringComparison.Ordinal))
                .ToList();
            for (int keyIndex = 0; keyIndex < cooldownKeys.Count; keyIndex++)
                _activeSkillCooldownUntilTick.Remove(cooldownKeys[keyIndex]);
        }

        private static int ResolveActiveSkillBusyTicks(Combat.SpellData spell, PlayerState state)
        {
            if (spell == null) return 0;
            int repeatCount = Math.Clamp(spell.RepeatCount, 0, byte.MaxValue);
            if (repeatCount <= 0) return 0;
            bool resolved = Combat.SpellDatabase.TryResolvePlayerAnimationTiming(spell.AnimationId, state, out int frames, out _);
            int animationFrames = resolved && frames > 0
                ? frames
                : spell.AnimationLengthFrames > 0 ? spell.AnimationLengthFrames : 30;
            ushort busyTicks = unchecked((ushort)(repeatCount * unchecked((ushort)animationFrames)));
            int speed = Combat.SpellDatabase.ResolveActiveSkillSpeed(spell, state);
            if (speed != 100)
                busyTicks = unchecked((ushort)(((uint)busyTicks * 100u) / (uint)speed));
            return busyTicks;
        }

        private bool IsActiveSkillBusy(RRConnection conn, ushort componentId, byte actionId, Combat.SpellData spell, out uint remainingTicks)
        {
            remainingTicks = 0;
            if (spell == null) return false;
            if (!TryGetActiveSkillBusyUntilTick(conn, componentId, actionId, out uint busyUntilTick))
                return false;
            uint remainingBeforeSkillsChild = busyUntilTick - _combatTick;
            remainingTicks = remainingBeforeSkillsChild - 1u;
            return remainingTicks != 0;
        }

        private bool TryGetActiveSkillBusyUntilTick(RRConnection conn, ushort componentId, byte actionId, out uint busyUntilTick)
        {
            string key = GetActiveSkillBusyKey(conn, componentId, actionId);
            if (!_activeSkillBusyUntilTick.TryGetValue(key, out busyUntilTick))
                return false;
            if (_combatTick < busyUntilTick)
                return true;
            _activeSkillBusyUntilTick.Remove(key);
            _activeSkillBusyKeys.Remove(key);
            busyUntilTick = 0;
            return false;
        }

        private bool HasActiveSkillBehaviorSlot(RRConnection conn)
        {
            if (conn == null)
                return false;
            string keyPrefix = conn.ConnId + ":";
            for (int index = 0; index < _activeSkillBusyKeys.Count;)
            {
                string key = _activeSkillBusyKeys[index];
                if (!key.StartsWith(keyPrefix, StringComparison.Ordinal))
                {
                    index++;
                    continue;
                }
                if (!_activeSkillBusyUntilTick.TryGetValue(key, out uint busyUntilTick)
                    || _combatTick >= busyUntilTick)
                {
                    _activeSkillBusyUntilTick.Remove(key);
                    _activeSkillBusyKeys.RemoveAt(index);
                    continue;
                }
                return true;
            }
            return false;
        }

        private bool IsActiveSkillCooldown(RRConnection conn, Combat.SpellData spell, byte actionId, out uint remainingTicks)
        {
            remainingTicks = 0;
            string key = GetActiveSkillCooldownKey(conn, spell, actionId);
            if (!_activeSkillCooldownUntilTick.TryGetValue(key, out uint cooldownUntilTick))
                return false;
            if (_combatTick < cooldownUntilTick)
            {
                remainingTicks = cooldownUntilTick - _combatTick;
                return true;
            }
            _activeSkillCooldownUntilTick.Remove(key);
            return false;
        }

        private void StartActiveSkillBusy(RRConnection conn, ushort componentId, byte actionId, Combat.SpellData spell, PlayerState state, bool startsAfterSkillsChild)
        {
            int busyTicks = ResolveActiveSkillBusyTicks(spell, state);
            if (busyTicks <= 0) return;
            ulong due = (ulong)_combatTick + (uint)busyTicks + (startsAfterSkillsChild ? 1u : 0u);
            string key = GetActiveSkillBusyKey(conn, componentId, actionId);
            if (!_activeSkillBusyUntilTick.ContainsKey(key))
                _activeSkillBusyKeys.Add(key);
            _activeSkillBusyUntilTick[key] = due >= uint.MaxValue ? uint.MaxValue : (uint)due;
        }

        private ushort StartActiveSkillCooldown(RRConnection conn, ushort componentId, byte actionId, Combat.SpellData spell, PlayerState state, int skillLevel, bool startsAfterSkillsChild)
        {
            ushort cooldownTicks = Combat.SpellDatabase.ResolveCooldownTicks(spell, skillLevel, state);
            if (cooldownTicks == 0)
                return 0;
            ulong due = (ulong)_combatTick + cooldownTicks + (startsAfterSkillsChild ? 1u : 0u);
            _activeSkillCooldownUntilTick[GetActiveSkillCooldownKey(conn, spell, actionId)] = due >= uint.MaxValue ? uint.MaxValue : (uint)due;
            return cooldownTicks;
        }

        private static void ActivateUseTarget(RRConnection conn, ushort targetId, byte useFlags, ushort componentId = 0, byte sessionId = 0, int targetPlayerConnId = 0, byte responseId = 0)
        {
            if (conn == null) return;
            bool sameBasicTarget = conn.HasActiveUseTarget
                && conn.ActiveUseTargetId == targetId
                && IsBasicWeaponManipulatorId(conn.ActiveUseTargetFlags)
                && IsBasicWeaponManipulatorId(useFlags);
            bool visibleHit = sameBasicTarget && conn.ActiveUseTargetVisibleHit;
            int initUseRangeF32 = sameBasicTarget ? conn.ActiveUseTargetInitUseRangeF32 : 0;
            int initUseDistanceF32 = sameBasicTarget ? conn.ActiveUseTargetInitUseDistanceF32 : 0;
            int toleranceF32 = sameBasicTarget ? conn.ActiveUseTargetClientToleranceF32 : 0;
            long lastProjectileSeq = sameBasicTarget ? conn.ActiveUseTargetLastProjectileSeq : 0;
            int lastImpactTick = sameBasicTarget ? conn.ActiveUseTargetLastImpactTick : -1;

            conn.HasActiveUseTarget = true;
            conn.ActiveUseTargetId = targetId;
            conn.ActiveUseTargetPlayerConnId = targetPlayerConnId;
            conn.ActiveUseTargetRemoved = false;
            conn.ActiveUseTargetFlags = useFlags;
            conn.ActiveUseTargetComponentId = componentId;
            conn.ActiveUseTargetSessionId = sessionId;
            conn.ActiveUseTargetResponseId = responseId;
            conn.ActiveUseTargetActionFailureQueued = false;
            conn.ActiveUseTargetActionSequence = conn.LastPlayerUseTargetActionSequence;
            conn.ActiveUseTargetInitUseEvaluationTick = uint.MaxValue;
            conn.ActiveUseTargetInitUseEvaluationTargetId = 0;
            conn.ActiveUseTargetStartedWeaponUse = false;
            conn.ActiveUseTargetInitUsePassed = false;
            conn.ActiveUseTargetVisibleHit = visibleHit;
            conn.ActiveUseTargetInitUseRangeF32 = initUseRangeF32;
            conn.ActiveUseTargetInitUseDistanceF32 = initUseDistanceF32;
            conn.ActiveUseTargetClientToleranceF32 = toleranceF32;
            conn.ActiveUseTargetLastProjectileSeq = lastProjectileSeq;
            conn.ActiveUseTargetLastImpactTick = lastImpactTick;
            conn.ActiveUseTargetWeaponReleasePending = false;
            conn.ActiveUseTargetInitUseCooldownStarted = false;
            conn.ActiveUseTargetInitUseCooldownComponentId = 0;
            conn.ActiveUseTargetInitUseCooldownActionId = 0;
        }

        private static void ClearUseTarget(RRConnection conn)
        {
            if (conn == null) return;
            conn.HasActiveUseTarget = false;
            conn.ActiveUseTargetId = 0;
            conn.ActiveUseTargetPlayerConnId = 0;
            conn.ActiveUseTargetRemoved = false;
            conn.ActiveUseTargetFlags = 0;
            conn.ActiveUseTargetComponentId = 0;
            conn.ActiveUseTargetSessionId = 0;
            conn.ActiveUseTargetResponseId = 0;
            conn.ActiveUseTargetActionFailureQueued = false;
            conn.ActiveUseTargetActionSequence = 0;
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
            conn.ActiveUseTargetWeaponReleasePending = false;
            conn.ActiveUseTargetInitUseCooldownStarted = false;
            conn.ActiveUseTargetInitUseCooldownComponentId = 0;
            conn.ActiveUseTargetInitUseCooldownActionId = 0;
        }

        private static ushort ResolveClientControlComponentId(RRConnection conn, ushort componentId)
        {
            if (componentId != 0) return componentId;
            if (conn == null) return 0;
            if (conn.UnitBehaviorId != 0 && conn.UnitBehaviorId <= ushort.MaxValue)
                return (ushort)conn.UnitBehaviorId;
            return conn.BehaviorComponentId;
        }

        private bool HasUseTargetActionResponseQueuedAheadOfControlReset(RRConnection conn)
        {
            if (conn == null)
                return false;
            for (int i = 0; i < _pendingPlayerUseTargetActionInputs.Count; i++)
            {
                PendingPlayerUseTargetActionInput input = _pendingPlayerUseTargetActionInputs[i];
                if (input.ConnId == conn.ConnId
                    && input.ResponseQueued
                    && !input.PacketSent
                    && IsPendingPlayerUseTargetInputCurrent(input, conn))
                    return true;
            }
            return false;
        }

        private bool HasPendingUseTargetActionResponseForTarget(RRConnection conn, uint targetEntityId, ushort componentId, byte responseId)
        {
            if (conn == null || targetEntityId == 0 || componentId == 0)
                return false;
            for (int i = 0; i < _pendingPlayerUseTargetActionInputs.Count; i++)
            {
                PendingPlayerUseTargetActionInput input = _pendingPlayerUseTargetActionInputs[i];
                if (input.ConnId == conn.ConnId
                    && input.ResponseQueued
                    && !input.PacketSent
                    && input.ComponentId == componentId
                    && input.ResponseId == responseId
                    && input.TargetPlayerConnId == 0
                    && input.MonsterEntityId == targetEntityId
                    && IsPendingPlayerUseTargetInputCurrent(input, conn))
                    return true;
            }
            return false;
        }

        private bool HasPendingPlayerActionResponseAheadOfControlReset(RRConnection conn)
        {
            if (conn == null)
                return false;
            if (HasUseTargetActionResponseQueuedAheadOfControlReset(conn))
                return true;
            for (int i = 0; i < _pendingPlayerUsePositionWeaponActionInputs.Count; i++)
            {
                PendingPlayerUsePositionWeaponActionInput input = _pendingPlayerUsePositionWeaponActionInputs[i];
                if (input.ConnId == conn.ConnId)
                    return true;
            }
            foreach (PendingSpell pending in _pendingSpells.ToArray())
            {
                if (pending.Conn?.ConnId == conn.ConnId
                    && (pending.AwaitingActionResponsePacket
                        || !pending.ActionAdmissionApplied && pending.ActionAdmissionSequence > 0))
                    return true;
            }
            for (int actionIndex = 0; actionIndex < _pendingPlayerUsePositionBehaviorActions.Count; actionIndex++)
            {
                if (_pendingPlayerUsePositionBehaviorActions[actionIndex].ConnId == conn.ConnId)
                    return true;
            }
            lock (_pendingAoECastLock)
            {
                for (int i = 0; i < _pendingAoECasts.Count; i++)
                {
                    PendingAoECast pending = _pendingAoECasts[i];
                    if (pending.Conn?.ConnId == conn.ConnId && pending.AwaitingActionResponsePacket)
                        return true;
                }
            }
            lock (_pendingSelfCastActionLock)
            {
                for (int i = 0; i < _pendingSelfCastActions.Count; i++)
                {
                    PendingSelfCastAction pending = _pendingSelfCastActions[i];
                    if (pending.Conn?.ConnId == conn.ConnId && pending.AwaitingActionResponsePacket)
                        return true;
                }
            }
            return false;
        }

        private void RemarryClientToLogicalMovement(RRConnection conn, string source)
        {
            if (conn == null)
                return;
            int fixedX = conn.HasOwnerAckFollowClientPosition
                ? conn.OwnerAckFollowClientPosFixedX
                : conn.HasReflectedAvatarPosition ? conn.ReflectedAvatarPosFixedX : conn.PlayerPosFixedX;
            int fixedY = conn.HasOwnerAckFollowClientPosition
                ? conn.OwnerAckFollowClientPosFixedY
                : conn.HasReflectedAvatarPosition ? conn.ReflectedAvatarPosFixedY : conn.PlayerPosFixedY;
            int fixedZ = conn.HasOwnerAckFollowClientPosition
                ? conn.OwnerAckFollowClientPosFixedZ
                : conn.HasReflectedAvatarPosition ? conn.ReflectedAvatarPosFixedZ : conn.PlayerPosFixedZ;
            int headingFixed = conn.HasOwnerAckFollowClientPosition
                ? conn.OwnerAckFollowClientHeadingFixed
                : conn.HasReflectedAvatarPosition ? conn.ReflectedAvatarHeadingFixed : conn.PlayerHeadingFixed;
            SetReflectedAvatarPosition(conn, fixedX, fixedY, fixedZ, headingFixed, false);
            conn.ReflectedAvatarUnitMoverMode = UnitMover.FollowClientMode;
            conn.ReflectedAvatarMovingThisFrame = false;
            SetOwnerAckFollowClientPosition(conn, fixedX, fixedY, fixedZ, headingFixed, false);
            conn.OwnerAckFollowClientMovingThisFrame = false;
            SetUnitFollowClientPosition(conn, fixedX, fixedY, fixedZ, headingFixed, false);
            conn.UnitFollowClientMovingThisFrame = false;
            conn.UnitFollowClientActionActive = false;
            conn.UnitFollowClientFollowingClient = conn.UnitFollowClientClientControlEnabled;
            conn.OwnerUnitBehaviorMoverMode = UnitMover.FollowClientMode;
            if (conn.UnitFollowClientClientControlEnabled)
                conn.UnitFollowClientMoverMode = UnitMover.FollowClientMode;
            SyncOwnerAckFollowClientCurrentState(conn);
            SyncUnitFollowClientCurrentState(conn);
            SyncReflectedAvatarCombatPosition(conn);
            Debug.LogError($"[OWNER-ACK-FOLLOWCLIENT] state=remarry conn={conn.ConnId} tick={_combatTick} positionFixed=({fixedX},{fixedY},{fixedZ}) headingFixed={headingFixed} actionActive={conn.UnitFollowClientActionActive} clientControl={conn.UnitFollowClientClientControlEnabled} followingClient={conn.UnitFollowClientFollowingClient} source={source} sourceFunction=UnitBehavior::onActionStopped@0x005205E0->UnitBehavior::RemarryClientToLogicalMovement@0x00520890->UnitBehavior::FollowClient@0x005202F0");
        }

        private void ClearUseTargetAndReleaseControl(RRConnection conn, string source = "unknown", ushort componentId = 0, bool sendClientControlReset = true, bool requireActiveUseTargetForReset = false, bool preserveActiveWeaponChild = false)
        {
            if (conn == null) return;
            bool hadUseTarget = conn.HasActiveUseTarget;
            bool hadActionMirroredMovement = ActionMirroredMoveToPointOwnsUnitMover(conn);
            bool pendingUseTarget = conn.HasPendingUseTargetAction;
            bool queuedUseTargetResponse = HasUseTargetActionResponseQueuedAheadOfControlReset(conn);
            ushort targetId = conn.ActiveUseTargetId;
            ushort controlComponentId = ResolveClientControlComponentId(conn, componentId);
            CancelUseTargetMoving(conn, source);
            if (!preserveActiveWeaponChild)
                ClearPvpUseState(conn, source, preserveProjectiles: true);
            ClearUseTarget(conn);
            if ((hadUseTarget || hadActionMirroredMovement) && conn.IsConnected && conn.IsSpawned)
                RemarryClientToLogicalMovement(conn, source);
            if (preserveActiveWeaponChild)
                Combat.WeaponUseRuntime.Instance.CancelConnectionUseTargetIntent(conn.ConnId.ToString(), source);
            else
                Combat.WeaponUseRuntime.Instance.ClearConnection(conn.ConnId.ToString());
            if (conn.Avatar != null)
            {
                Combat.CombatRuntime.Instance.SetPlayerActiveClientAttack((uint)conn.Avatar.Id, false);
                if (!preserveActiveWeaponChild)
                    _friendlySummonCasts.Remove((uint)conn.Avatar.Id);
            }

            uint playerEntityId = conn.Avatar != null && conn.Avatar.Id > 0
                ? (uint)conn.Avatar.Id
                : 0u;
            bool currentPlayerBehaviorAction = HasCurrentPlayerBehaviorAction(conn, playerEntityId);
            bool pendingPlayerActionResponse = HasPendingPlayerActionResponseAheadOfControlReset(conn);

            if (sendClientControlReset && !pendingUseTarget
                && !queuedUseTargetResponse
                && !currentPlayerBehaviorAction
                && !pendingPlayerActionResponse
                && (!requireActiveUseTargetForReset || hadUseTarget)
                && controlComponentId != 0
                && conn.IsConnected)
            {
                Debug.LogError($"[CONTROL] Release UseTarget source={source} target={targetId} hadUseTarget={hadUseTarget} componentId=0x{controlComponentId:X4}");
                SendClientControlReset(conn, controlComponentId);
            }
            else
            {
                Debug.LogError($"[CONTROL] Release UseTarget source={source} target={targetId} hadUseTarget={hadUseTarget} pendingUseTarget={pendingUseTarget} queuedUseTargetResponse={queuedUseTargetResponse} currentPlayerBehaviorAction={currentPlayerBehaviorAction} pendingPlayerActionResponse={pendingPlayerActionResponse} no-reset componentId=0x{controlComponentId:X4} connected={conn.IsConnected}");
            }
        }

        private void TryClearUseTargetAndReleaseControl(RRConnection conn, string source)
        {
            if (conn == null) return;
            try
            {
                ClearUseTargetAndReleaseControl(conn, source);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CONTROL-ERROR] {source}: {ex.Message}\n{ex.StackTrace}");
                ClearUseTarget(conn);
            }
        }

        private void StopPlayerUseTargetActionSlot(RRConnection conn, string source, bool preserveActiveWeaponChild = false)
        {
            if (conn == null)
                return;
            if (conn.HasActiveUseTarget)
            {
                byte actionSessionId = conn.ActiveUseTargetSessionId;
                ClearUseTargetAndReleaseControl(conn, source, preserveActiveWeaponChild: preserveActiveWeaponChild);
                MarkPeerUseTargetTerminated(conn, actionSessionId, _combatTick);
                conn.PlayerBehaviorActionStoppedTick = _combatTick;
            }
            else
                CancelUseTargetMoving(conn, source);
        }

        private void AdvancePlayerUseTargetActionSlot(uint playerEntityId, uint simulationTick)
        {
            RRConnection conn = FindConnectionByAvatarEntityId(playerEntityId);
            if (conn == null || !conn.HasActiveUseTarget || conn.UseTargetMovingActivate)
                return;
            if (conn.ActiveUseTargetFlags == 10 || conn.ActiveUseTargetFlags == 11)
            {
                Combat.Monster activeWeaponTarget = CombatRuntime.Instance.GetMonster(conn.ActiveUseTargetId)
                    ?? CombatRuntime.Instance.GetMonsterByComponent(conn.ActiveUseTargetId);
                if (Combat.WeaponUseRuntime.Instance.IsWeaponBusyOnKilledTarget(conn, activeWeaponTarget))
                    return;
            }
            if ((conn.HasPendingUseTargetAction && !conn.PendingUseTargetIsRedundant)
                || HasPendingSelfCastBehaviorAction(conn))
            {
                if (conn.ActiveUseTargetFlags >= 100)
                {
                    PlayerState activeState = GetPlayerState(conn.ConnId.ToString());
                    Combat.SpellData activeSpell = ResolveActionSpell(conn, activeState, conn.ActiveUseTargetFlags);
                    if (activeSpell != null && IsActiveSkillBusy(conn, conn.ActiveUseTargetComponentId, conn.ActiveUseTargetFlags, activeSpell, out uint remainingTicks))
                    {
                        Debug.LogError($"[CONTROL] UseTarget slot target={conn.ActiveUseTargetId} currentManipulator={conn.ActiveUseTargetFlags} pendingTarget={conn.PendingUseTargetActionId} pendingManipulator={conn.PendingUseTargetFlags} tick={simulationTick} state=pending-shutdown-busy remainingTicks={remainingTicks} sourceFunction=Behavior::doActionLocal@0x00515130->Action::shutdown@0x005140A0->UseTarget::States@0x00548370->ActiveSkill::isBusy@0x005394F0");
                        return;
                    }
                }
                Debug.LogError($"[CONTROL] UseTarget slot target={conn.ActiveUseTargetId} currentManipulator={conn.ActiveUseTargetFlags} pendingTarget={conn.PendingUseTargetActionId} pendingManipulator={conn.PendingUseTargetFlags} tick={simulationTick} state=pending-nonredundant sourceFunction=Behavior::doActionLocal@0x00515130->UseTarget::IsRedundant@0x005482E0->Use::shutdown@vtable+0xB4");
                StopPlayerUseTargetActionSlot(conn, "Behavior::doActionLocal-pending-nonredundant", preserveActiveWeaponChild: true);
                return;
            }
            if (conn.ActiveUseTargetPlayerConnId != 0)
            {
                if (conn.UseTargetMovingActive
                    && conn.UseTargetMovingActionMirrored
                    && !conn.UseTargetMovingActivate
                    && !conn.UseTargetMovingUsing)
                    return;
                if (conn.ActiveUseTargetFlags >= 100)
                {
                    PlayerState pvpSkillState = GetPlayerState(conn.ConnId.ToString());
                    Combat.SpellData pvpSpell = ResolveActionSpell(conn, pvpSkillState, conn.ActiveUseTargetFlags);
                    if (pvpSpell != null && IsActiveSkillBusy(conn, conn.ActiveUseTargetComponentId, conn.ActiveUseTargetFlags, pvpSpell, out _))
                        return;
                    StopPlayerUseTargetActionSlot(conn, "PVP-UseTarget-skill-complete");
                    return;
                }
                if (IsPvpWeaponUseBusy(playerEntityId))
                    return;
                StopPlayerUseTargetActionSlot(conn, "PVP-UseTarget-weapon-complete");
                return;
            }
            if (conn.ActiveUseTargetFlags >= 100)
            {
                if (conn.UseTargetMovingActive
                    && conn.UseTargetMovingActionMirrored
                    && !conn.UseTargetMovingActivate
                    && !conn.UseTargetMovingUsing)
                    return;
                PlayerState state = GetPlayerState(conn.ConnId.ToString());
                Combat.SpellData spell = ResolveActionSpell(conn, state, conn.ActiveUseTargetFlags);
                if (spell != null && IsActiveSkillBusy(conn, conn.ActiveUseTargetComponentId, conn.ActiveUseTargetFlags, spell, out _))
                    return;
                Debug.LogError($"[CONTROL] UseTarget slot target={conn.ActiveUseTargetId} tick={simulationTick} state=skill-complete sourceFunction=UseTarget::States@0x00548370->ActiveSkill::isBusy@0x005394F0");
                StopPlayerUseTargetActionSlot(conn, "UseTarget::States-skill-complete");
                return;
            }
            if (conn.ActiveUseTargetWeaponReleasePending)
            {
                bool repeatWeaponUse = Combat.WeaponUseRuntime.Instance.ConsumePlayerWeaponRepeatAtRelease(playerEntityId, conn.ActiveUseTargetId);
                Combat.Monster repeatTarget = repeatWeaponUse
                    ? CombatRuntime.Instance.GetMonster(conn.ActiveUseTargetId)
                    : null;
                PlayerState repeatState = repeatWeaponUse ? GetPlayerState(conn.ConnId.ToString()) : null;
                if (repeatTarget != null
                    && repeatState != null
                    && repeatTarget.IsAlive
                    && CombatRuntime.Instance.PeekMonsterCurrentHPWire(repeatTarget) != 0
                    && IsMeleeTargetWithinServerRange(conn, repeatState, repeatTarget, out int repeatDistanceF32, out int repeatRangeF32))
                {
                    conn.ActiveUseTargetInitUseEvaluationTick = simulationTick;
                    conn.ActiveUseTargetInitUseEvaluationTargetId = conn.ActiveUseTargetId;
                    conn.ActiveUseTargetInitUsePassed = true;
                    conn.ActiveUseTargetInitUseRangeF32 = repeatRangeF32;
                    conn.ActiveUseTargetInitUseDistanceF32 = repeatDistanceF32;
                    conn.ActiveUseTargetClientToleranceF32 = 0;
                    if (Combat.WeaponUseRuntime.Instance.BeginPlayerMeleeUseTargetState(playerEntityId, conn.ActiveUseTargetId, simulationTick))
                    {
                        conn.ActiveUseTargetWeaponReleasePending = false;
                        Debug.LogError($"[CONTROL] UseTarget slot target={conn.ActiveUseTargetId} tick={simulationTick} state=weapon-repeat sourceFunction=UseTarget::States@0x00548370->MeleeWeapon::use@0x00591760");
                        return;
                    }
                }
                Debug.LogError($"[CONTROL] UseTarget slot target={conn.ActiveUseTargetId} tick={simulationTick} state=weapon-complete sourceFunction=UseTarget::States@0x00548370 state=0x18->4");
                StopPlayerUseTargetActionSlot(conn, "UseTarget::States-weapon-complete");
                return;
            }
            if (conn.ActiveUseTargetRemoved)
            {
                Debug.LogError($"[CONTROL] UseTarget slot target={conn.ActiveUseTargetId} tick={simulationTick} state=removed sourceFunction=Behavior::processRemoved@0x00515BC0");
                StopPlayerUseTargetActionSlot(conn, "UseTarget::processRemoved");
                return;
            }
            var monster = CombatRuntime.Instance.GetMonster(conn.ActiveUseTargetId)
                       ?? CombatRuntime.Instance.GetMonsterByComponent(conn.ActiveUseTargetId);
            if (monster == null || (monster.IsAlive && CombatRuntime.Instance.PeekMonsterCurrentHPWire(monster) != 0))
                return;
            if (Combat.WeaponUseRuntime.Instance.IsWeaponBusyOnKilledTarget(conn, monster))
            {
                Debug.LogError($"[CONTROL] UseTarget slot target={conn.ActiveUseTargetId} tick={simulationTick} state=dead weaponBusy=True sourceFunction=MeleeWeapon::isBusy@0x00591B00");
                return;
            }
            Debug.LogError($"[CONTROL] UseTarget slot target={conn.ActiveUseTargetId} tick={simulationTick} state=dead weaponBusy=False sourceFunction=UseTarget::States@0x00548370");
            StopPlayerUseTargetActionSlot(conn, "UseTarget::States-target-dead");
        }

        private void UpdatePlayerUseTargetBehaviorChild(uint playerEntityId, uint simulationTick)
        {
            if (ApplyPendingGroupGotoWarpBehaviorChild(playerEntityId, simulationTick))
                return;
            RRConnection conn = FindConnectionByAvatarEntityId(playerEntityId);
            bool hadCurrentAction = conn != null && conn.HasActiveUseTarget;
            if (hadCurrentAction)
            {
                AdvancePlayerUseTargetActionSlot(playerEntityId, simulationTick);
                return;
            }
            if (HasPendingSelfCastBehaviorAction(conn))
            {
                ProcessPendingSelfCastActions(playerEntityId, simulationTick);
                return;
            }
            PromotePendingPlayerUseTargetBehaviorAction(playerEntityId, simulationTick);
        }

        private bool IsMeleeTargetWithinServerRange(RRConnection conn, PlayerState state, Combat.Monster monster, out int distanceF32, out int allowedRangeF32)
        {
            distanceF32 = 0;
            allowedRangeF32 = 0;
            if (conn == null || monster == null) return false;
            allowedRangeF32 = CombatRuntime.Instance.ResolvePlayerWeaponTargetClearRangeF32(state, monster);
            uint viewerId = conn.Avatar != null ? (uint)conn.Avatar.Id : 0;
            CombatRuntime.Instance.TryPeekMonsterClientVisiblePositionFixed(monster, viewerId, out int targetFixedX, out int targetFixedY, out _);
            ResolveAuthoritativePlayerPositionFixed(conn, out int actorFixedX, out int actorFixedY, out _);
            bool inRange = CombatRuntime.Instance.EvaluateUseTargetInitUseFixed(
                actorFixedX,
                actorFixedY,
                targetFixedX,
                targetFixedY,
                allowedRangeF32,
                0,
                out distanceF32,
                out _,
                out _);
            PathMap pathMap = ResolveUseTargetMovingPathMap(conn);
            return inRange && pathMap != null && pathMap.CanReachPointFixed(actorFixedX, actorFixedY, targetFixedX, targetFixedY);
        }

        private bool IsRangedProjectileTargetWithinServerRange(RRConnection conn, PlayerState state, Combat.Monster monster, out int distanceF32, out int allowedRangeF32)
        {
            distanceF32 = 0;
            allowedRangeF32 = 0;
            if (conn == null || monster == null) return false;
            allowedRangeF32 = CombatRuntime.Instance.ResolvePlayerWeaponTargetClearRangeF32(state, monster);
            uint viewerId = conn.Avatar != null ? (uint)conn.Avatar.Id : 0;
            CombatRuntime.Instance.TryPeekMonsterClientVisiblePositionFixed(monster, viewerId, out int targetFixedX, out int targetFixedY, out _);
            ResolveAuthoritativePlayerPositionFixed(conn, out int actorFixedX, out int actorFixedY, out _);
            bool inRange = CombatRuntime.Instance.EvaluateUseTargetInitUseFixed(
                actorFixedX,
                actorFixedY,
                targetFixedX,
                targetFixedY,
                allowedRangeF32,
                0,
                out distanceF32,
                out _,
                out _);
            PathMap pathMap = ResolveUseTargetMovingPathMap(conn);
            return inRange && pathMap != null && pathMap.CanReachPointFixed(actorFixedX, actorFixedY, targetFixedX, targetFixedY);
        }

        private bool IsRangedBasicTargetWithinServerRange(RRConnection conn, PlayerState state, Combat.Monster monster, out int distanceF32, out int allowedRangeF32)
        {
            return IsRangedProjectileTargetWithinServerRange(conn, state, monster, out distanceF32, out allowedRangeF32);
        }

        private Combat.SpellData ResolveActionSpell(RRConnection conn, PlayerState state, byte manipulatorId)
        {
            Combat.SpellDatabase.Initialize();
            var spell = ResolveSpellFromManip(conn, manipulatorId);
            if (spell == null)
                Debug.LogError($"[SPELL] action=resolveActionSpell manipId={manipulatorId} state=missing");
            return spell;
        }

        private bool IsSkillTargetWithinServerRange(RRConnection conn, Combat.SpellData spell, Combat.Monster monster, out int distanceF32, out int allowedRangeF32)
        {
            distanceF32 = 0;
            allowedRangeF32 = 0;
            if (conn == null || monster == null || spell == null) return false;
            allowedRangeF32 = CombatRuntime.Instance.ResolvePlayerSkillTargetClearRangeF32(spell, monster);
            uint viewerId = conn.Avatar != null ? (uint)conn.Avatar.Id : 0;
            if (!CombatRuntime.Instance.TryPeekMonsterClientVisiblePositionFixed(monster, viewerId, out int targetFixedX, out int targetFixedY, out int targetFixedZ))
                return false;
            PathMap pathMap = ResolveUseTargetMovingPathMap(conn);
            ResolveUnitBehaviorPredictedPositionFixed(conn, pathMap, out int actorFixedX, out int actorFixedY, out int actorFixedZ);
            CombatRuntime.Instance.ResolvePlayerActiveSkillTargetPositionsFixed(
                actorFixedX, actorFixedY, actorFixedZ,
                monster, targetFixedX, targetFixedY, targetFixedZ,
                out actorFixedX, out actorFixedY, out actorFixedZ,
                out targetFixedX, out targetFixedY, out targetFixedZ);
            CombatRuntime.Instance.EvaluateUseTargetInitUseFixed3D(
                actorFixedX,
                actorFixedY,
                actorFixedZ,
                targetFixedX,
                targetFixedY,
                targetFixedZ,
                allowedRangeF32,
                0,
                out distanceF32,
                out long distanceSqFixed8,
                out long thresholdSqFixed8);
            return thresholdSqFixed8 > 0
                && distanceSqFixed8 < thresholdSqFixed8
                && !WorldCollision.Instance.TrySegmentHitFixed(
                    conn.CurrentZoneName, conn.RuntimeInstanceKey,
                    actorFixedX, actorFixedY, actorFixedZ,
                    targetFixedX, targetFixedY, targetFixedZ,
                    UnitMover.Fixed, out _);
        }

        private static void ResolveUnitBehaviorPredictedPositionFixed(RRConnection conn, PathMap pathMap, out int fixedX, out int fixedY, out int fixedZ)
        {
            if (conn.HasUnitFollowClientPosition)
            {
                fixedX = conn.UnitFollowClientPosFixedX;
                fixedY = conn.UnitFollowClientPosFixedY;
                fixedZ = conn.UnitFollowClientPosFixedZ;
            }
            else if (conn.HasOwnerAckFollowClientPosition)
            {
                fixedX = conn.OwnerAckFollowClientPosFixedX;
                fixedY = conn.OwnerAckFollowClientPosFixedY;
                fixedZ = conn.OwnerAckFollowClientPosFixedZ;
            }
            else
                ResolveAuthoritativePlayerPositionFixed(conn, out fixedX, out fixedY, out fixedZ);
            if (pathMap != null && conn.UnitFollowClientMovementSamples.Count > 0)
            {
                var sample = conn.UnitFollowClientMovementSamples.Last();
                fixedX = sample.X;
                fixedY = sample.Y;
                fixedZ = pathMap.GetHeightAtFixed(fixedX, fixedY, fixedZ);
            }
        }

        private bool IsSpellPositionWithinServerRangeFixed(RRConnection conn, Combat.SpellData spell, int posFixedX, int posFixedY, int posFixedZ, out int distanceF32, out int allowedRangeF32)
        {
            distanceF32 = 0;
            allowedRangeF32 = 0;
            if (conn == null || spell == null || spell.Range <= 0) return false;
            allowedRangeF32 = Math.Max(0x100, checked(spell.Range * 0x100));
            ResolveAuthoritativePlayerPositionFixed(conn, out int actorFixedX, out int actorFixedY, out int actorFixedZ);
            long dx = (long)posFixedX - actorFixedX;
            long dy = (long)posFixedY - actorFixedY;
            long dz = (long)posFixedZ - actorFixedZ;
            long rawDistanceSquared = dx * dx + dy * dy + dz * dz;
            distanceF32 = UnitMover.IntSqrt(rawDistanceSquared);
            long distanceSquaredF32 = rawDistanceSquared >> 8;
            long rangeSquaredF32 = ((long)allowedRangeF32 * allowedRangeF32) >> 8;
            return distanceSquaredF32 <= rangeSquaredF32;
        }

        private Combat.Monster ResolvePositionSpellTargetFixed(RRConnection conn, Combat.SpellData spell, int posFixedX, int posFixedY, out int projectileHitDistanceF32)
        {
            projectileHitDistanceF32 = 0;
            ResolveAuthoritativePlayerPositionFixed(conn, out int sourceFixedX, out int sourceFixedY, out _);
            if (spell != null && spell.ProjectileSizeF32 > 0)
                return FindFirstProjectileMonsterHitFromStartFixed(
                    conn,
                    spell,
                    sourceFixedX,
                    sourceFixedY,
                    posFixedX,
                    posFixedY,
                    out projectileHitDistanceF32);

            return Combat.CombatRuntime.Instance.GetNearestMonsterFixed(posFixedX, posFixedY, 50 * UnitMover.Fixed, GetInstanceZoneKey(conn));
        }

        private static int ResolveProjectileImpactDelayTicksFixed32(Combat.SpellData spell, PlayerState state, int projectileHitDistanceF32)
        {
            int speedF32 = spell != null && spell.ProjectileSizeF32 > 0 && spell.ProjectileSpeedF32 > 0
                ? spell.ProjectileSpeedF32
                : state != null && state.WeaponUsesProjectile ? state.WeaponProjectileSpeedF32 : 0;
            return speedF32 > 0
                ? Combat.WeaponUseRuntime.ProjectileImpactDelayTicksFixed32(Math.Max(0, projectileHitDistanceF32), speedF32)
                : 0;
        }

        private int ResolveProjectileMaxDistanceF32(Combat.SpellData spell, int aimDistanceF32)
        {
            aimDistanceF32 = Math.Max(0, aimDistanceF32);
            if (spell == null || spell.ProjectileSpeedF32 <= 0)
                return aimDistanceF32;

            int speedF32 = spell.ProjectileSpeedF32;
            if (speedF32 <= 0)
                return aimDistanceF32;

            if (spell.ProjectileLifespanF32 > 0)
            {
                int lifespanF32 = spell.ProjectileLifespanF32;
                long distanceF32 = ((long)speedF32 * lifespanF32) / (SIMULATION_TICKS_PER_SECOND * 0x100L);
                if (distanceF32 <= 0) return 0;
                return distanceF32 > int.MaxValue ? int.MaxValue : (int)distanceF32;
            }

            int rangeDistanceF32 = spell.Range > 0
                ? Math.Max(0, spell.Range * 0x100 + (14 * 0x100))
                : 0;
            return Math.Max(aimDistanceF32, rangeDistanceF32);
        }

        private PathMap ResolveProjectilePathMap(RRConnection conn, Combat.Monster monster)
        {
            string zoneName = monster?.ZoneName;
            if (string.IsNullOrWhiteSpace(zoneName))
                zoneName = conn?.CurrentZoneName;
            string pathMapKey = !string.IsNullOrWhiteSpace(monster?.InstanceKey)
                ? monster.InstanceKey
                : (conn != null ? GetInstanceZoneKey(conn) : null);
            if (string.IsNullOrWhiteSpace(pathMapKey))
                pathMapKey = zoneName;
            return !string.IsNullOrWhiteSpace(pathMapKey) ? PathMapCatalog.Instance.GetPathMap(pathMapKey) : null;
        }

        private void LogProjectilePathMapForUnitFirst(PathMap pathMap, Combat.SpellData spell, int startFixedX, int startFixedY, int pathFixedX, int pathFixedY, int pathDistanceFixed, int impactDistanceFixed, Combat.Monster monster, bool predictedMove)
        {
            if (pathMap == null)
                return;
            if (pathDistanceFixed <= 0)
                return;

            int clampedDistanceFixed = Math.Clamp(impactDistanceFixed, 0, pathDistanceFixed);
            int impactFixedX = startFixedX + (int)(((long)pathFixedX * clampedDistanceFixed) / pathDistanceFixed);
            int impactFixedY = startFixedY + (int)(((long)pathFixedY * clampedDistanceFixed) / pathDistanceFixed);
            PathReachability reachability = pathMap.GetReachabilityFixed(startFixedX, startFixedY, impactFixedX, impactFixedY);
            if (reachability == PathReachability.Reachable)
                return;

            string spellName = spell?.DisplayName ?? spell?.SkillId ?? "spell";
            string targetName = monster != null ? $"{monster.Name}#{monster.EntityId}" : "<none>";
            Debug.LogError($"[PROJECTILE-PATHMAP] {spellName} pathFixed=({startFixedX},{startFixedY})->impactFixed=({impactFixedX},{impactFixedY}) reachability={reachability} target={targetName} predictedMove={predictedMove} clientUnitFirst=True result=log-only sourceFunction=ProjectileChecker::testFirstTime->WorldCollision");
        }

        private static bool TryResolveProjectileSegmentUnitHitFixed(
            int startFixedX,
            int startFixedY,
            int endFixedX,
            int endFixedY,
            int maxDistanceFixed,
            int centerFixedX,
            int centerFixedY,
            int radiusFixed,
            out int impactDistanceFixed,
            out long distSqFixed)
        {
            impactDistanceFixed = 0;
            distSqFixed = long.MaxValue;
            maxDistanceFixed = Math.Max(0, maxDistanceFixed);
            radiusFixed = Math.Max(0, radiusFixed);
            if (maxDistanceFixed <= 0 || radiusFixed <= 0)
                return false;

            int pathDx = endFixedX - startFixedX;
            int pathDy = endFixedY - startFixedY;
            long pathLenSq = ((long)pathDx * pathDx) + ((long)pathDy * pathDy);
            int pathDistanceFixed = UnitMover.IntSqrt(pathLenSq);
            if (pathDistanceFixed <= 0)
                return false;

            int projectedDistanceFixed = ProjectPointDistanceAlongSegmentFixed(
                startFixedX,
                startFixedY,
                pathDx,
                pathDy,
                pathDistanceFixed,
                centerFixedX,
                centerFixedY);
            if (projectedDistanceFixed + radiusFixed < 0 || projectedDistanceFixed - radiusFixed > maxDistanceFixed)
                return false;

            int closestDistanceFixed = ClampFixed(projectedDistanceFixed, 0, maxDistanceFixed);
            int closestFixedX = startFixedX + (int)(((long)pathDx * closestDistanceFixed) / pathDistanceFixed);
            int closestFixedY = startFixedY + (int)(((long)pathDy * closestDistanceFixed) / pathDistanceFixed);
            long missDx = (long)centerFixedX - closestFixedX;
            long missDy = (long)centerFixedY - closestFixedY;
            long rawDistSqFixed = (missDx * missDx) + (missDy * missDy);
            distSqFixed = NativeUnitTestCollisionDistanceSqFixed8(missDx, missDy);
            long radiusSqFixed = (long)radiusFixed * radiusFixed;
            long nativeRadiusSqFixed8 = NativeUnitTestCollisionRadiusSqFixed8(radiusFixed);
            if (!NativeUnitTestCollisionSqFixed8(distSqFixed, nativeRadiusSqFixed8))
                return false;

            int entryOffsetFixed = UnitMover.IntSqrt(Math.Max(0, radiusSqFixed - rawDistSqFixed));
            impactDistanceFixed = ClampFixed(projectedDistanceFixed - entryOffsetFixed, 0, maxDistanceFixed);
            return true;
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

        private static int ProjectPointDistanceAlongSegmentFixed(
            int startFixedX,
            int startFixedY,
            int pathDx,
            int pathDy,
            int pathDistanceFixed,
            int pointFixedX,
            int pointFixedY)
        {
            if (pathDistanceFixed <= 0)
                return 0;
            long relX = (long)pointFixedX - startFixedX;
            long relY = (long)pointFixedY - startFixedY;
            long dot = (relX * pathDx) + (relY * pathDy);
            long projected = dot / pathDistanceFixed;
            if (projected > int.MaxValue) return int.MaxValue;
            if (projected < int.MinValue) return int.MinValue;
            return (int)projected;
        }

        private static int ClampFixed(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static int RoundedSqrtFixed8(long value)
        {
            if (value <= 0) return 0;
            int floor = UnitMover.IntSqrt(value);
            long floorSq = (long)floor * floor;
            int next = floor == int.MaxValue ? floor : floor + 1;
            long nextSq = (long)next * next;
            long lowerDelta = value - floorSq;
            long upperDelta = nextSq - value;
            if (upperDelta < lowerDelta)
                return next;
            if (upperDelta == lowerDelta && (floor & 1) != 0)
                return next;
            return floor;
        }

        private Combat.Monster FindFirstProjectileMonsterHitFromStartFixed(RRConnection conn, Combat.SpellData spell, int startFixedX, int startFixedY, int aimFixedX, int aimFixedY, out int projectileHitDistanceF32)
        {
            projectileHitDistanceF32 = 0;
            if (conn == null || spell == null || spell.ProjectileSizeF32 <= 0)
                return null;

            int aimDxFixed = aimFixedX - startFixedX;
            int aimDyFixed = aimFixedY - startFixedY;
            long aimLenSqFixed = ((long)aimDxFixed * aimDxFixed) + ((long)aimDyFixed * aimDyFixed);
            int pathLenF32 = RoundedSqrtFixed8(aimLenSqFixed);
            if (pathLenF32 <= 0)
                return null;
            int projectileMaxDistanceF32 = ResolveProjectileMaxDistanceF32(spell, pathLenF32);
            if (projectileMaxDistanceF32 <= 0)
                return null;
            int projectilePathFixedX = (int)(((long)aimDxFixed * projectileMaxDistanceF32) / pathLenF32);
            int projectilePathFixedY = (int)(((long)aimDyFixed * projectileMaxDistanceF32) / pathLenF32);
            int projectileEndFixedX = startFixedX + projectilePathFixedX;
            int projectileEndFixedY = startFixedY + projectilePathFixedY;
            int projectilePathDistanceFixed = UnitMover.IntSqrt(((long)projectilePathFixedX * projectilePathFixedX) + ((long)projectilePathFixedY * projectilePathFixedY));
            if (projectilePathDistanceFixed <= 0)
                return null;

            Combat.Monster best = null;
            int bestImpactDistanceFixed = int.MaxValue;
            long bestDistSqFixed = long.MaxValue;
            bool bestPredictedMove = false;
            int bestHitTick = 0;
            string instanceKey = GetInstanceZoneKey(conn);
            int projectileSizeF32 = spell.ProjectileSizeF32;
            int projectileRadiusF32 = WeaponUseRuntime.ProjectileRadiusFromAuthoredSizeF32(projectileSizeF32);
            int scanCenterFixedZ = conn.PlayerPosFixedZ;
            uint projectileViewerEntityId = conn.Avatar != null ? (uint)conn.Avatar.Id : 0u;
            var nativeOrderedCandidates = Combat.CombatRuntime.Instance.GetProjectileHittableMonstersInNativeDistanceOrder(
                startFixedX,
                startFixedY,
                scanCenterFixedZ,
                instanceKey,
                conn.CurrentZoneName,
                mode: Combat.ProjectileUnitFinderMode.FirstTimeHittableUnits,
                playerEntityId: projectileViewerEntityId,
                useClientVisiblePosition: true);

            foreach (var monster in nativeOrderedCandidates)
            {
                if (!Combat.CombatRuntime.Instance.IsProjectileHittableMonster(monster, instanceKey, conn.CurrentZoneName, Combat.ProjectileUnitFinderMode.FirstTimeHittableUnits))
                    continue;

                PathMap projectilePathMap = ResolveProjectilePathMap(conn, monster);
                if (!Combat.CombatRuntime.Instance.TryGetMonsterClientVisiblePositionFixed(monster, projectileViewerEntityId, out int visibleMonsterFixedX, out int visibleMonsterFixedY))
                    continue;
                int monsterRadiusF32 = Combat.CombatRuntime.ResolveProjectileUnitCollisionRadiusF32(monster);
                int hitRadiusFixed = WeaponUseRuntime.ProjectileCollisionRadiusF32(monsterRadiusF32, projectileSizeF32);
                if (TryResolveProjectileSegmentUnitHitFixed(
                    startFixedX,
                    startFixedY,
                    projectileEndFixedX,
                    projectileEndFixedY,
                    projectileMaxDistanceF32,
                    visibleMonsterFixedX,
                    visibleMonsterFixedY,
                    hitRadiusFixed,
                    out int impactDistanceFixed,
                    out long distSqFixed))
                {
                    LogProjectilePathMapForUnitFirst(projectilePathMap, spell, startFixedX, startFixedY, projectilePathFixedX, projectilePathFixedY, projectilePathDistanceFixed, impactDistanceFixed, monster, false);
                    if (Combat.CombatRuntime.IsProjectileHitBetterFixed(monster, impactDistanceFixed, distSqFixed, best, bestImpactDistanceFixed, bestDistSqFixed))
                    {
                        best = monster;
                        bestImpactDistanceFixed = impactDistanceFixed;
                        bestDistSqFixed = distSqFixed;
                        bestPredictedMove = false;
                        bestHitTick = 0;
                    }
                }

                if (TryResolveMovingProjectileMonsterHit(
                    conn,
                    monster,
                    spell,
                    startFixedX,
                    startFixedY,
                    projectilePathFixedX,
                    projectilePathFixedY,
                    projectilePathDistanceFixed,
                    projectileMaxDistanceF32,
                    hitRadiusFixed,
                    out int movingImpactDistanceFixed,
                    out long movingDistSqFixed,
                    out int movingHitTick))
                {
                    LogProjectilePathMapForUnitFirst(projectilePathMap, spell, startFixedX, startFixedY, projectilePathFixedX, projectilePathFixedY, projectilePathDistanceFixed, movingImpactDistanceFixed, monster, true);
                    if (Combat.CombatRuntime.IsProjectileHitBetterFixed(monster, movingImpactDistanceFixed, movingDistSqFixed, best, bestImpactDistanceFixed, bestDistSqFixed))
                    {
                        best = monster;
                        bestImpactDistanceFixed = movingImpactDistanceFixed;
                        bestDistSqFixed = movingDistSqFixed;
                        bestPredictedMove = true;
                        bestHitTick = movingHitTick;
                    }
                }
            }

            if (best == null)
            {
                foreach (var monster in nativeOrderedCandidates)
                {
                    if (!Combat.CombatRuntime.Instance.IsProjectileHittableMonster(monster, instanceKey, conn.CurrentZoneName))
                        continue;

                    if (!Combat.CombatRuntime.Instance.TryGetMonsterClientVisiblePositionFixed(monster, projectileViewerEntityId, out int visibleFixedX, out int visibleFixedY))
                        continue;
                    int monsterRadiusF32 = Combat.CombatRuntime.ResolveProjectileUnitCollisionRadiusF32(monster);
                    int hitRadiusFixed = WeaponUseRuntime.ProjectileCollisionRadiusF32(monsterRadiusF32, projectileSizeF32);
                    long endpointDx = (long)visibleFixedX - aimFixedX;
                    long endpointDy = (long)visibleFixedY - aimFixedY;
                    long endpointRawDistSqFixed = (endpointDx * endpointDx) + (endpointDy * endpointDy);
                    long endpointDistSqFixed = NativeUnitTestCollisionDistanceSqFixed8(endpointDx, endpointDy);
                    long hitRadiusSqFixed = NativeUnitTestCollisionRadiusSqFixed8(hitRadiusFixed);
                    if (!NativeUnitTestCollisionSqFixed8(endpointDistSqFixed, hitRadiusSqFixed))
                        continue;

                    int projectedDistanceFixed = ProjectPointDistanceAlongSegmentFixed(
                        startFixedX,
                        startFixedY,
                        projectilePathFixedX,
                        projectilePathFixedY,
                        projectilePathDistanceFixed,
                        visibleFixedX,
                        visibleFixedY);
                    if (projectedDistanceFixed < 0 || projectedDistanceFixed > projectileMaxDistanceF32 + hitRadiusFixed)
                        continue;

                    int endpointImpactDistanceFixed = ClampFixed(projectedDistanceFixed, 0, projectileMaxDistanceF32);
                    if (Combat.CombatRuntime.IsProjectileHitBetterFixed(monster, endpointImpactDistanceFixed, endpointDistSqFixed, best, bestImpactDistanceFixed, bestDistSqFixed))
                    {
                        best = monster;
                        bestImpactDistanceFixed = endpointImpactDistanceFixed;
                        bestDistSqFixed = endpointDistSqFixed;
                        bestPredictedMove = false;
                        bestHitTick = 0;
                        int endpointDistanceFixed = UnitMover.IntSqrt(endpointRawDistSqFixed);
                        Debug.LogError($"[PROJECTILE-HIT] {spell.DisplayName ?? spell.SkillId ?? "spell"} endpoint unit-first fallback aimFixed=({aimFixedX},{aimFixedY}) hit={monster.Name}#{monster.EntityId} distF32={endpointDistanceFixed} radiusF32={hitRadiusFixed} projectedF32={projectedDistanceFixed}");
                    }
                }
            }

            if (best != null)
            {
                Combat.CombatRuntime.Instance.ApplyMonsterWanderClientVisiblePosition(best, "ProjectileChecker-hit");
                projectileHitDistanceF32 = Math.Max(0, bestImpactDistanceFixed);
                string predictedMove = bestPredictedMove ? $" predictedMove=True hitTick={bestHitTick}" : string.Empty;
                string pastAim = bestImpactDistanceFixed > pathLenF32 ? " pastAim=True" : string.Empty;
                int bestRadiusF32 = Combat.CombatRuntime.ResolveProjectileUnitCollisionRadiusF32(best);
                int bestHitRadiusF32 = WeaponUseRuntime.ProjectileCollisionRadiusF32(bestRadiusF32, projectileSizeF32);
                int delayTicks = ResolveProjectileImpactDelayTicksFixed32(spell, null, bestImpactDistanceFixed);
                Debug.LogError($"[PROJECTILE-HIT] {spell.DisplayName ?? spell.SkillId ?? "spell"} pathFixed=({startFixedX},{startFixedY})->aimFixed=({aimFixedX},{aimFixedY}) hit={best.Name}#{best.EntityId} hitDistF32={bestImpactDistanceFixed} aimDistF32={pathLenF32} maxDistF32={projectileMaxDistanceF32} delayTicks={delayTicks} distSqF32={bestDistSqFixed} radiusF32={bestHitRadiusF32} projectileRadiusF32={projectileRadiusF32}{pastAim}{predictedMove}");
                Combat.CombatRuntime.Instance.TryGetMonsterClientVisiblePositionFixed(best, 0, out int bestCvFixedX, out int bestCvFixedY);
                Debug.LogError($"[PROJ-HIT-CV] ent={best.EntityId} cvFixed=({bestCvFixedX},{bestCvFixedY}) aggro={best.AggroTriggered} wanderActive={best.ClientVisibleMoveActive} hpWire={Combat.CombatRuntime.Instance.PeekMonsterCurrentHPWire(best)} hitDistF32={bestImpactDistanceFixed} tick={_combatTick}");
            }
            else
            {
                Debug.LogError($"[PROJECTILE-HIT] {spell.DisplayName ?? spell.SkillId ?? "spell"} pathFixed=({startFixedX},{startFixedY})->aimFixed=({aimFixedX},{aimFixedY}) no unit hit sizeF32={spell.ProjectileSizeF32} aimDistF32={pathLenF32} maxDistF32={projectileMaxDistanceF32}");
            }

            return best;
        }

        private bool TryResolveMovingProjectileMonsterHit(
            RRConnection conn,
            Combat.Monster monster,
            Combat.SpellData spell,
            int startFixedX,
            int startFixedY,
            int projectilePathFixedX,
            int projectilePathFixedY,
            int projectilePathDistanceFixed,
            int projectileMaxDistanceF32,
            int hitRadiusFixed,
            out int impactDistanceFixed,
            out long distSqFixed,
            out int hitTick)
        {
            impactDistanceFixed = 0;
            distSqFixed = long.MaxValue;
            hitTick = 0;

            if (conn == null || conn.Avatar == null || monster == null || spell == null)
                return false;
            int projectileSpeedF32 = spell.ProjectileSpeedF32;
            if (projectileSpeedF32 > 0 && projectileSpeedF32 < 0x100)
                projectileSpeedF32 = 0x100;
            if (projectileSpeedF32 <= 0 || projectilePathDistanceFixed <= 0 || projectileMaxDistanceF32 <= 0 || hitRadiusFixed <= 0)
                return false;

            uint playerEntityId = (uint)conn.Avatar.Id;
            if (!monster.AggroTriggered || monster.TargetId != playerEntityId || monster.AttackPending)
                return false;

            int targetFixedX = conn.PlayerPosFixedX;
            int targetFixedY = conn.PlayerPosFixedY;
            Combat.CombatRuntime.Instance.TryGetMonsterClientVisiblePositionFixed(
                monster,
                playerEntityId,
                out int monsterStartFixedX,
                out int monsterStartFixedY);
            int moveFixedX = targetFixedX - monsterStartFixedX;
            int moveFixedY = targetFixedY - monsterStartFixedY;
            int moveDistanceFixed = UnitMover.IntSqrt(((long)moveFixedX * moveFixedX) + ((long)moveFixedY * moveFixedY));
            if (moveDistanceFixed <= 0)
                return false;

            int stopRangeFixed = Combat.CombatRuntime.Instance.GetMonsterEffectiveAttackRangeF32(monster);
            int moveLimitFixed = Math.Max(0, moveDistanceFixed - stopRangeFixed);
            if (moveLimitFixed <= 0)
                return false;

            int monsterSpeedF32 = Combat.CombatRuntime.Instance.GetMonsterMovementSpeedFixed(monster);
            if (monsterSpeedF32 <= 0)
                return false;

            long hitRadiusSqFixed = NativeUnitTestCollisionRadiusSqFixed8(hitRadiusFixed);
            int samples = Combat.WeaponUseRuntime.ProjectileFlightTicksFixed32(projectileMaxDistanceF32, projectileSpeedF32);
            int projectileStepF32 = Combat.WeaponUseRuntime.ProjectileStepDistanceF32(projectileSpeedF32);
            int monsterStepF32 = Math.Max(1, monsterSpeedF32 / 30);

            for (int sample = 1; sample <= samples; sample++)
            {
                int projectileDistanceFixed = (int)Math.Min((long)projectileMaxDistanceF32, (long)sample * projectileStepF32);
                int monsterDistanceFixed = (int)Math.Min((long)moveLimitFixed, (long)sample * monsterStepF32);
                int projectileFixedX = startFixedX + (int)(((long)projectilePathFixedX * projectileDistanceFixed) / projectilePathDistanceFixed);
                int projectileFixedY = startFixedY + (int)(((long)projectilePathFixedY * projectileDistanceFixed) / projectilePathDistanceFixed);
                int monsterFixedX = monsterStartFixedX + (int)(((long)moveFixedX * monsterDistanceFixed) / moveDistanceFixed);
                int monsterFixedY = monsterStartFixedY + (int)(((long)moveFixedY * monsterDistanceFixed) / moveDistanceFixed);
                long dxFixed = (long)monsterFixedX - projectileFixedX;
                long dyFixed = (long)monsterFixedY - projectileFixedY;
                long sampleDistSqFixed = NativeUnitTestCollisionDistanceSqFixed8(dxFixed, dyFixed);
                if (NativeUnitTestCollisionSqFixed8(sampleDistSqFixed, hitRadiusSqFixed))
                {
                    impactDistanceFixed = projectileDistanceFixed;
                    distSqFixed = sampleDistSqFixed;
                    hitTick = sample;
                    return true;
                }
            }

            return false;
        }

        private Combat.SpellData ResolveSpellFromManip(RRConnection conn, byte manipulatorId)
        {
            Combat.SpellDatabase.Initialize();
            string connKey = conn.ConnId.ToString();
            if (_playerManipMap.TryGetValue(connKey, out var map))
            {
                if (map.TryGetValue(manipulatorId, out string gcClass))
                {
                    var spell = Combat.SpellDatabase.GetSpell(gcClass);
                    if (spell != null)
                    {
                        Debug.LogError($"[MANIP-RESOLVE] manipId={manipulatorId} -> {gcClass} -> {spell.DisplayName}");
                        return spell;
                    }
                    string shortName = gcClass;
                    int lastDot = gcClass.LastIndexOf('.');
                    if (lastDot >= 0) shortName = gcClass.Substring(lastDot + 1);
                    spell = Combat.SpellDatabase.GetSpell(shortName);
                    if (spell != null)
                    {
                        Debug.LogError($"[MANIP-RESOLVE] manipId={manipulatorId} -> {gcClass} -> short={shortName} -> {spell.DisplayName}");
                        return spell;
                    }
                    Debug.LogError($"[MANIP-RESOLVE] manipId={manipulatorId} -> {gcClass} but NOT in SpellDatabase!");
                }
                else
                {
                    Debug.LogError($"[MANIP-RESOLVE] manipId={manipulatorId} NOT in manipMap (keys: {string.Join(",", map.Keys)})");
                }
            }
            else
            {
                Debug.LogError($"[MANIP-RESOLVE] No manipMap for connection {connKey}");
            }
            return null;
        }

        private void HandleSkillTrainRequest(RRConnection conn, LEReader reader, ushort componentId, byte subMessage, ZoneNPC trainerNpc)
        {
            Debug.LogError($"[TRAINER] ");
            Debug.LogError($"[TRAINER]  SKILL TRAIN REQUEST from {conn.LoginName}");
            Debug.LogError($"[TRAINER]   NPC: {trainerNpc.Name} ({trainerNpc.GCClass}) cid=0x{componentId:X4}");

            if (reader.Remaining < 9)
            {
                Debug.LogError($"[TRAINER]  Not enough data: {reader.Remaining} bytes (need 9)");
                if (reader.Remaining > 0) reader.ReadBytes(reader.Remaining);
                return;
            }

            uint playerEntityId = reader.ReadUInt32();
            byte entityRefType = reader.ReadByte();
            uint skillHash = reader.ReadUInt32();

            Debug.LogError($"[TRAINER] playerEntityId=0x{playerEntityId:X} refType=0x{entityRefType:X2} skillHash=0x{skillHash:X8}");

            if (reader.Remaining > 0)
            {
                byte[] trailing = reader.ReadBytes(reader.Remaining);
                Debug.LogError($"[TRAINER]   trailing entitySynchInfo: {BitConverter.ToString(trailing)}");
            }

            if (!_skillHashToGcClass.TryGetValue(skillHash, out string skillGcClass))
            {
                Debug.LogError($"[TRAINER]  Unknown skill hash 0x{skillHash:X8} - not in DJB2 table");
                return;
            }
            Debug.LogError($"[TRAINER]   Resolved: 0x{skillHash:X8} -> {skillGcClass}");

            if (!_selectedCharacter.ContainsKey(conn.LoginName))
            {
                Debug.LogError($"[TRAINER]  No selected character for {conn.LoginName}");
                return;
            }
            var savedChar = GetActiveCharacter(conn)?.DeepClone();
            if (savedChar == null)
            {
                Debug.LogError($"[TRAINER]  SavedCharacter not found for {conn.LoginName}");
                return;
            }

            int currentLevel = savedChar.GetSkillLevel(skillGcClass);
            string connKey = conn.ConnId.ToString();
            bool playerHasSkill = false;
            if (_playerSkillLevels.TryGetValue(connKey, out var existingLevels))
                playerHasSkill = existingLevels.ContainsKey(skillGcClass);
            if (!playerHasSkill && savedChar.skills != null)
                playerHasSkill = savedChar.skills.Contains(skillGcClass);

            int nextLevel = playerHasSkill ? currentLevel + 1 : 1;

            Combat.SpellDatabase.Initialize();
            Combat.SpellData trainSpell = Combat.SpellDatabase.GetSpell(skillGcClass);
            if (trainSpell == null)
            {
                Debug.LogError($"[TRAINER] authored skill missing gcClass={skillGcClass}");
                return;
            }
            if (IsPassiveSkill(skillGcClass)
                && !PassiveAttributeModifiers.TryValidateRuntime(skillGcClass, out string passiveRuntimeReason))
            {
                SkillEffectTracker.RecordPlayerGraph(conn.Avatar != null ? (uint)conn.Avatar.Id : 0u, 0, skillGcClass, "PASSIVE", 0, "rejected", passiveRuntimeReason, _combatTick);
                SendSystemMessage(conn, $"Skill nelze vytrénovat: {passiveRuntimeReason ?? "unsupported-passive-runtime"}");
                Debug.LogError($"[TRAINER] skill={skillGcClass} result=rejected reason={passiveRuntimeReason ?? "unsupported-passive-runtime"} goldMutation=False persistenceMutation=False");
                return;
            }

            int goldValueModF32 = trainSpell.GoldValueModF32;
            int requiredLevel = trainSpell.RequiredLevel;
            int maxSkillLevel = trainSpell.MaxSkillLevel;
            if (goldValueModF32 <= 0 || requiredLevel <= 0 || maxSkillLevel <= 0)
            {
                Debug.LogError($"[TRAINER] authored train data missing gcClass={skillGcClass} goldValueModF32={goldValueModF32} requiredLevel={requiredLevel} maxSkillLevel={maxSkillLevel}");
                return;
            }

            if (nextLevel > maxSkillLevel)
            {
                Debug.LogError($"[TRAINER]  Already at max level {currentLevel}/{maxSkillLevel} for {skillGcClass}");
                return;
            }

            int goldCost = SkillDescGetCostF32(trainSpell, nextLevel) >> 8;
            if (goldCost < 1) goldCost = 1;

            Debug.LogError($"[TRAINER]   Level: {currentLevel} -> {nextLevel} (max {maxSkillLevel})");
            Debug.LogError($"[TRAINER]   Gold cost: {goldCost} | Player gold: {savedChar.gold}");

            if (savedChar.gold < (uint)goldCost)
            {
                Debug.LogError($"[TRAINER]  Not enough gold: have {savedChar.gold}, need {goldCost}");
                return;
            }

            savedChar.gold -= (uint)goldCost;
            Debug.LogError($"[TRAINER]    Gold: {savedChar.gold + goldCost} -> {savedChar.gold}");

            savedChar.SetSkillLevel(skillGcClass, nextLevel);
            if (!playerHasSkill)
            {
                if (savedChar.skills == null)
                    savedChar.skills = new List<string>();
                if (!savedChar.skills.Contains(skillGcClass))
                    savedChar.skills.Add(skillGcClass);
                Debug.LogError($"[TRAINER]    Learned NEW skill: {skillGcClass}");
            }

            if (!TrySaveCharacterForConn(conn, savedChar, "trainer-skill"))
            {
                savedChar.gold += (uint)goldCost;
                if (playerHasSkill)
                    savedChar.SetSkillLevel(skillGcClass, currentLevel);
                else
                {
                    savedChar.skills?.RemoveAll(skill => string.Equals(skill, skillGcClass, StringComparison.OrdinalIgnoreCase));
                    savedChar.skillLevels?.RemoveAll(entry => string.Equals(entry.skill, skillGcClass, StringComparison.OrdinalIgnoreCase));
                }
                Debug.LogError("[TRAINER] state=failed phase=save");
                return;
            }

            if (!_playerSkillLevels.ContainsKey(connKey))
                _playerSkillLevels[connKey] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _playerSkillLevels[connKey][skillGcClass] = nextLevel;
            int lastDot = skillGcClass.LastIndexOf('.');
            if (lastDot >= 0)
                _playerSkillLevels[connKey][skillGcClass.Substring(lastDot + 1)] = nextLevel;
            Debug.LogError("[TRAINER] Character saved");


            ushort skillsCid = 0;
            _playerSkillsComponentId.TryGetValue(connKey, out skillsCid);
            Debug.LogError($"[TRAINER-RESPONSE] connKey={connKey} skillsCid=0x{skillsCid:X4}");

            if (skillsCid != 0)
            {

                uint skillEntityId = 0;
                bool hasSlot = false;
                if (_playerSkillSlots.TryGetValue(connKey, out var slots))
                {
                    hasSlot = slots.TryGetValue(skillGcClass, out skillEntityId);
                    if (!hasSlot)
                    {
                        string sn = skillGcClass;
                        int dot = sn.LastIndexOf('.');
                        if (dot >= 0) sn = sn.Substring(dot + 1);
                        hasSlot = slots.TryGetValue(sn, out skillEntityId);
                    }
                    Debug.LogError($"[TRAINER-RESPONSE] Slot lookup: '{skillGcClass}' -> entityId={skillEntityId} found={hasSlot}");
                    Debug.LogError($"[TRAINER-RESPONSE] All slots: {string.Join(", ", slots.Select(kv => $"{kv.Key}={kv.Value}"))}");
                }
                else
                {
                    Debug.LogError($"[TRAINER-RESPONSE]  No slot map for connKey={connKey}");
                }

                {
                    var combined = new LEWriter();
                    combined.WriteByte(0x07);

                    combined.WriteByte(0x35);
                    combined.WriteUInt16(skillsCid);
                    combined.WriteByte(0x33);
                    combined.WriteUInt32(savedChar.gold);
                    WritePlayerEntitySynch(conn, combined);

                    combined.WriteByte(0x35);
                    combined.WriteUInt16(skillsCid);
                    combined.WriteByte(0x32);
                    combined.WriteByte(0xFF);
                    combined.WriteCString(skillGcClass);
                    combined.WriteByte((byte)nextLevel);
                    WritePlayerEntitySynch(conn, combined);

                    combined.WriteByte(0x06);

                    byte[] combinedPacket = combined.ToArray();
                    Debug.LogError($"[TRAINER-COMBINED] hex ({combinedPacket.Length}b): {BitConverter.ToString(combinedPacket)}");
                    SendCompressedE(conn, combinedPacket);
                    Debug.LogError($"[TRAINER-COMBINED]  Sent gold={savedChar.gold} + level={nextLevel} for '{skillGcClass}'");
                }

                if (conn.UnitContainerId != 0)
                {
                    var goldWriter = new LEWriter();
                    goldWriter.WriteByte(0x07);
                    goldWriter.WriteByte(0x35);
                    goldWriter.WriteUInt16(conn.UnitContainerId);
                    goldWriter.WriteByte(0x20);
                    goldWriter.WriteInt32(-goldCost);
                    goldWriter.WriteByte(0x00);
                    goldWriter.WriteUInt32(0x00000000);
                    goldWriter.WriteByte(0x01);
                    WritePlayerEntitySynch(conn, goldWriter);
                    goldWriter.WriteByte(0x06);
                    byte[] goldPacket = goldWriter.ToArray();
                    Debug.LogError($"[TRAINER-GOLD]  RemoveCurrency {goldCost} via UnitContainer 0x{conn.UnitContainerId:X4}");
                    SendCompressedA(conn, 0x01, 0x0F, goldPacket);
                }
                else
                {
                    Debug.LogError($"[TRAINER-GOLD]  UnitContainerId=0, can't send gold update");
                }
            }
            else
            {
                Debug.LogError($"[TRAINER-RESPONSE]  SkillsComponentId=0 for {connKey} - rezone needed");
            }

            Debug.LogError($"[TRAINER]  TRAINED {skillGcClass} to Lv{nextLevel} for {conn.LoginName}");
            Debug.LogError($"[TRAINER] ");

            if (!playerHasSkill)
            {
                bool isPassive = skillGcClass.ToLower().Contains("passive") || skillGcClass.ToLower().Contains("trait");
                if (!isPassive)
                {
                    var manip = conn.Avatar?.Children?.FirstOrDefault(c => c.GCClass == "Manipulators");
                    if (manip != null)
                    {
                        var newSkill = new GCObject
                        {
                            GCClass = skillGcClass,
                            DFCClass = "ActiveSkill",
                            Name = skillGcClass,
                            Id = AllocateGeneralEntityId()
                        };
                        manip.AddChild(newSkill);
                        if (_playerSkillSlots.TryGetValue(connKey, out var slotMap))
                            slotMap[skillGcClass] = (uint)newSkill.Id;
                        if (_playerManipMap.ContainsKey(connKey))
                            SetPlayerManipulator(connKey, AllocatePlayerManipulatorId(connKey), skillGcClass);
                        Debug.LogError($"[TRAINER] Added '{skillGcClass}' to Manipulators, eid={newSkill.Id}");
                    }
                }
            }

            string shortName = skillGcClass;
            int dotIdx = skillGcClass.LastIndexOf('.');
            if (dotIdx >= 0) shortName = skillGcClass.Substring(dotIdx + 1);
            SendSystemMessage(conn, playerHasSkill
                ? $"{shortName} -> Rank {nextLevel}! ({savedChar.gold} gold)"
                : $"Learned {shortName}! ({savedChar.gold} gold)");
        }

        private int GetPlayerSkillLevel(RRConnection conn, Combat.SpellData spell)
        {
            string connKey = conn.ConnId.ToString();
            if (_playerSkillLevels.TryGetValue(connKey, out var levels))
            {
                if (levels.TryGetValue(spell.ShortName, out int lvl)) return lvl;
                if (!string.IsNullOrEmpty(spell.SkillId) && levels.TryGetValue(spell.SkillId, out lvl)) return lvl;
            }
            return 1;
        }

        private void InitializePlayerSkillLevels(RRConnection conn, SavedCharacter savedChar)
        {
            string connKey = conn.ConnId.ToString();
            var levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            if (savedChar.skills != null)
            {
                foreach (var skillGc in savedChar.skills)
                {
                    int savedLevel = savedChar.GetSkillLevel(skillGc);

                    levels[skillGc] = savedLevel;
                    int lastDot = skillGc.LastIndexOf('.');
                    if (lastDot >= 0)
                        levels[skillGc.Substring(lastDot + 1)] = savedLevel;
                }
            }
            _playerSkillLevels[connKey] = levels;
            Debug.LogError($"[SKILL-LEVELS] Initialized {levels.Count} skill entries for {conn.LoginName}");
            if (savedChar.skills != null)
                for (int skillIndex = 0; skillIndex < savedChar.skills.Count; skillIndex++)
                {
                    string skillGc = savedChar.skills[skillIndex];
                    Debug.LogError($"[SKILL-LEVELS]   {skillGc} = Lv{savedChar.GetSkillLevel(skillGc)}");
                }
        }

        private void HandleSpellAttack(RRConnection conn, PlayerState state, Combat.Monster monster, byte manipulatorId, byte useFlags, string sourceTag = null, bool monsterOnAttackedAlreadyAdmitted = false, bool activeSkillUseCommitted = false)
        {
            Combat.SpellDatabase.Initialize();
            var spell = ResolveSpellFromManip(conn, manipulatorId);
            if (spell == null)
            {
                Debug.LogError($"[SPELL] No spell found for manipId={manipulatorId} class={state.ClassName}");
                return;
            }
            if (!Combat.SpellDatabase.TryValidatePlayerPveTargetRuntime(spell, out string targetRuntimeReason))
            {
                SkillEffectTracker.RecordPlayerGraph(conn?.Avatar != null ? (uint)conn.Avatar.Id : 0u, monster?.EntityId ?? 0u, spell.SkillId, spell.TargetType, spell.OrderedEffects?.Count ?? 0, "rejected", targetRuntimeReason, _combatTick);
                Debug.LogError($"[SKILL-USE] action=effect state=blocked reason={targetRuntimeReason ?? "unsupported-player-pve-target-effect"} manipId={manipulatorId} spell={spell.SkillId ?? "UNKNOWN"} sourceFunction=ActiveSkill::doSkillEffect@0x00539630");
                return;
            }
            if (!activeSkillUseCommitted)
            {
                Debug.LogError($"[SKILL-USE] action=effect state=blocked reason=use-not-committed manipId={manipulatorId} spell={spell.DisplayName ?? spell.SkillId ?? "UNKNOWN"} sourceFunction=ActiveSkill::use@0x00538F00->ActiveSkill::update@0x005392F0");
                return;
            }
            int skillLevel = GetPlayerSkillLevel(conn, spell);
            int triggerFrames = ResolveActiveSkillTriggerFrames(spell, state);
            int animationFrames = Combat.SpellDatabase.TryResolvePlayerAnimationTiming(spell.AnimationId, state, out int targetFrames, out _) && targetFrames > 0
                ? targetFrames
                : spell.AnimationLengthFrames;
            Debug.LogError($"[SKILL-USE] action=target manipId={manipulatorId} gc={spell.SkillId} spell={spell.DisplayName} level={skillLevel} anim={spell.AnimationId} frames={animationFrames} trigger={triggerFrames} target={monster?.EntityId ?? 0} flags=0x{useFlags:X2} tick={_combatTick}");
            Debug.LogError($"[SPELL] {spell.DisplayName} (Lv{skillLevel}) cast by {conn.LoginName} on {monster.Name} (flags={useFlags} manip={manipulatorId}) AoE={spell.IsAoE}");

            var rng = CombatRuntime.Instance.GetRoomRngForMonster(monster);
            ApplySpellDamageToMonster(conn, state, spell, monster, rng, skillLevel, false, sourceTag, monsterOnAttackedAlreadyAdmitted);
            if (spell.IsChainSpell && monster != null)
                HandleChainSpell(conn, state, monster, spell, rng, skillLevel);
        }

        private void ApplySpellDamageToMonster(RRConnection conn, PlayerState state,
            Combat.SpellData spell, Combat.Monster target, Combat.MersenneTwister rng,
            int skillLevel, bool isAoETarget, string sourceTag = null,
            bool monsterOnAttackedAlreadyAdmitted = false, (int X, int Y)? effectSourcePosition = null)
        {
            if (spell == null || target == null) return;
            string tag = !string.IsNullOrEmpty(sourceTag) ? sourceTag : (isAoETarget ? "SPELL-AOE" : "SPELL");
            if (rng == null)
            {
                Debug.LogError($"[{tag}] {spell.DisplayName} -> {target.Name}: room RNG unavailable, damage not applied");
                LogMonsterOnAttackedBlocked(conn, target, tag, "missing-rng-no-Damage::apply");
                return;
            }

            SpellWeaponDamageEffectResult weaponEffect = default;
            if (spell.HasImmediateWeaponDamageEffect)
            {
                weaponEffect = ApplySpellWeaponDamageEffect(conn, state, spell, target, rng, skillLevel, tag);
                if (!weaponEffect.Landed)
                {
                    LogMonsterOnAttackedBlocked(conn, target, tag, "weapon-effect-not-landed");
                    return;
                }
                if (!weaponEffect.Applied)
                {
                    LogMonsterOnAttackedBlocked(conn, target, tag, "weapon-effect-not-applied");
                    return;
                }

                bool lethalWeaponDamage = weaponEffect.Died || weaponEffect.NewHPWire == 0 || CombatRuntime.Instance.PeekMonsterCurrentHPWire(target) == 0;
                if (lethalWeaponDamage)
                {
                    try
                    {
                        Debug.LogError($"[{tag}-KILL] Finalizing target={target.EntityId} hp={weaponEffect.OldHPWire}->{weaponEffect.NewHPWire} source=SpellWeaponDamageEffect");
                        bool finalized = TryFinalizeMonsterKill(conn, target, $"{tag}-weapon-kill");
                        if (state != null)
                            CommitPlayerHPTruth(conn, state, $"{tag}-WEAPON-KILL-AFTER-FINALIZE", state.CurrentHPWire, false, false);
                        Debug.LogError($"[{tag}-KILL] Finalize result target={target.EntityId} finalized={finalized} playerLevel={(state != null ? state.Level : 0)} playerHP={(state != null ? state.EntitySynchInfoHP : 0)}");
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[{tag}-KILL] SpellWeaponDamageEffect finalize failed target={target.EntityId}: {ex}");
                    }
                    CombatRuntime.Instance.CancelMonsterPendingAttack(target, $"{tag}-weapon-kill");
                    if (IsUseTargetingMonster(conn, target))
                        ClearUseTargetAndReleaseControl(conn, $"{tag}-weapon-kill", sendClientControlReset: true, requireActiveUseTargetForReset: true);
                    return;
                }
            }

            if (spell.HasProjectileModifierDamage && !weaponEffect.ReferencedModifierHandled)
            {
                uint sourceEntityId = conn?.Avatar != null ? (uint)conn.Avatar.Id : 0;
                var modifierResult = CombatRuntime.Instance.ApplyProjectileModifierFromSpell(
                    target,
                    sourceEntityId,
                    state,
                    spell,
                    rng,
                    skillLevel,
                    tag);

                if (!modifierResult.AppliedModifier)
                {
                    Debug.LogError($"[{tag}] {spell.DisplayName} -> {target.Name}: projectile modifier not applied reason={modifierResult.Reason} hp={CombatRuntime.Instance.PeekMonsterCurrentHPWire(target)} rngAfter={rng.CallsSinceReseed}");
                    LogMonsterOnAttackedBlocked(conn, target, tag, $"modifier-not-applied:{modifierResult.Reason}");
                    return;
                }

                Debug.LogError($"[POISON-SHOT-CHAIN] spell={spell.SkillId} projectileEffect={spell.ProjectileEffectId} modifier={spell.ProjectileModifierId} modifierEffect={spell.ProjectileModifierEffectId} ticks={modifierResult.TicksApplied} hp={modifierResult.OldHPWire}->{modifierResult.NewHPWire} rngAfterImpact={rng.CallsSinceReseed} status=modifier-attached-first-tick-deferred");
                LogMonsterOnAttackedBlocked(conn, target, tag, "modifier-attached-no-Damage::apply");
                return;
            }

            if (spell.HasImmediateWeaponDamageEffect && (!spell.HasDirectDamageEffect || spell.FriendlySummon?.IsHitProc == true))
            {
                return;
            }

            int attackerLevel = Math.Max(1, state != null ? state.Level : 1);
            int spellDamageTypeId = Combat.DamageResolver.ResolveDamageTypeId(spell.DamageType);
            bool spellEffectVulnerable;
            if (!CombatRuntime.Instance.ResolveSpellDamageResistance(
                    target,
                    spellDamageTypeId,
                    attackerLevel,
                    tag,
                    out spellEffectVulnerable))
            {
                Debug.LogError($"[COMBAT-EVENT] actor=player-spell actorId={(conn?.Avatar != null ? conn.Avatar.Id : 0)} target=monster targetId={target.EntityId} result=RESISTED damageWire=0 hp={CombatRuntime.Instance.PeekMonsterCurrentHPWire(target)}->{CombatRuntime.Instance.PeekMonsterCurrentHPWire(target)} spell={spell.DisplayName} rngAfter={rng.CallsSinceReseed} marker={tag}");
                return;
            }

            if (spell.HasSpellKnockDownEffect)
            {
                int strength = spell.ResolveSpellKnockDownStrength(skillLevel);
                CombatRuntime.Instance.ApplySpellKnockDownEffectToMonster(
                    rng,
                    target,
                    attackerLevel,
                    strength,
                    spell.SpellKnockDownChanceF32,
                    tag,
                    conn?.Avatar != null ? (uint)conn.Avatar.Id : 0,
                    effectSourcePosition: effectSourcePosition);
            }

            var result = Combat.DamageResolver.ProcessSpellAttack(
                rng,
                state.Level,
                state.ClientSpellIntellect,
                state.ClientSpellAgility,
                state.ClientSpellStrength,
                state.WeaponDamageF32,
                state.WeaponDamageVolatilityF32,
                spell,
                target,
                skillLevel,
                isAoETarget,
                Combat.DamageResolver.ResolveCriticalDamagePercent(state),
                state,
                spellEffectVulnerable ? -1 : Combat.DamageResolver.ResolveSpellCriticalThreshold(state, target, spell),
                spellEffectVulnerable);

            string resultName = result.Type.ToString().ToUpperInvariant();
            if (result.Type == Combat.AttackResultType.Miss || result.DamageF32 <= 0)
            {
                Debug.LogError($"[COMBAT-EVENT] actor=player-spell actorId={(conn?.Avatar != null ? conn.Avatar.Id : 0)} target=monster targetId={target.EntityId} result={resultName} damageWire=0 hp={CombatRuntime.Instance.PeekMonsterCurrentHPWire(target)}->{CombatRuntime.Instance.PeekMonsterCurrentHPWire(target)} spell={spell.DisplayName} rngAfter={rng.CallsSinceReseed} marker={tag}");
                return;
            }

            bool applied = CombatRuntime.Instance.ApplyPlayerDamageToMonsterWire(
                target,
                (uint)result.DamageF32,
                tag,
                out uint oldHPWire,
                out uint newHPWire,
                out bool died,
                clientDamageTick: _combatTick,
                damageTypeId: result.DamageTypeId,
                rawDamageWire: (uint)result.DamageF32,
                attackerLevel: attackerLevel,
                damageKind: 3,
                spellEffectResistResolved: true,
                spellEffectVulnerable: spellEffectVulnerable,
                sourceEntityId: conn?.Avatar != null ? (uint)conn.Avatar.Id : 0u);

            if (!applied)
            {
                Debug.LogError($"[{tag}] {spell.DisplayName} -> {target.Name}: damage not applied alive={target.IsAlive} hp={CombatRuntime.Instance.PeekMonsterCurrentHPWire(target)}");
                return;
            }
            uint appliedDamageWire = oldHPWire > newHPWire ? oldHPWire - newHPWire : 0u;
            int damageStunMod = ResolveSpellDamageStunMod(spell, state);
            uint damageSourceEntityId = conn?.Avatar != null ? (uint)conn.Avatar.Id : 0u;
            uint effectRaw = CombatRuntime.Instance.ConsumeSpellDamageReactionRng(
                rng,
                "player-spell",
                target,
                oldHPWire,
                newHPWire,
                appliedDamageWire,
                damageStunMod,
                attackerLevel,
                damageSourceEntityId,
                tag);
            CombatRuntime.Instance.DispatchPlayerDamageEvents(damageSourceEntityId, target, appliedDamageWire, spell.EffectId, tag);
            if (state != null && oldHPWire > newHPWire)
                state.ApplyOnDamageCallback(oldHPWire - newHPWire, tag);

            Debug.LogError($"[COMBAT-EVENT] actor=player-spell actorId={(conn?.Avatar != null ? conn.Avatar.Id : 0)} target=monster targetId={target.EntityId} result={resultName} damageWire={result.DamageF32} hp={oldHPWire}->{newHPWire} range=[{result.MinDamageF32},{result.MaxDamageF32}] damageRaw=0x{result.DamageRaw:X8} effectRaw=0x{effectRaw:X8} spell={spell.DisplayName} rngAfter={rng.CallsSinceReseed} marker={tag}");
            if (!monsterOnAttackedAlreadyAdmitted)
                NotifyMonsterDamagedByConnection(conn, target, tag);
            else
                CombatRuntime.Instance.NotifyDeferredMonsterOnAttackedThreat(target, conn?.Avatar != null ? (uint)conn.Avatar.Id : 0u, tag);

            bool lethalSpellDamage = died || newHPWire == 0 || CombatRuntime.Instance.PeekMonsterCurrentHPWire(target) == 0;
            if (lethalSpellDamage)
            {
                if (!died)
                    Debug.LogError($"[{tag}-KILL] Lethal HP reached without died flag target={target.EntityId} hp={oldHPWire}->{newHPWire}");
                try
                {
                    Debug.LogError($"[{tag}-KILL] Finalizing target={target.EntityId} hp={oldHPWire}->{newHPWire}");
                    bool finalized = TryFinalizeMonsterKill(conn, target, $"{tag}-kill");
                    if (state != null)
                        CommitPlayerHPTruth(conn, state, $"{tag}-KILL-AFTER-FINALIZE", state.CurrentHPWire, false, false);
                    Debug.LogError($"[{tag}-KILL] Finalize result target={target.EntityId} finalized={finalized} playerLevel={(state != null ? state.Level : 0)} playerHP={(state != null ? state.EntitySynchInfoHP : 0)}");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[{tag}-KILL] Finalize failed target={target.EntityId}: {ex}");
                }
                CombatRuntime.Instance.CancelMonsterPendingAttack(target, $"{tag}-kill");
                if (IsUseTargetingMonster(conn, target))
                    ClearUseTargetAndReleaseControl(conn, $"{tag}-kill", sendClientControlReset: true, requireActiveUseTargetForReset: true);
                return;
            }
        }

        private bool ApplySpellWeaponDamageChildEffects(
            RRConnection conn,
            PlayerState state,
            Combat.SpellData spell,
            Combat.Monster target,
            Combat.MersenneTwister rng,
            int skillLevel,
            string tag)
        {
            if (spell == null || target == null || !target.IsAlive)
                return false;

            bool referencedModifierHandled = false;
            if (spell.FriendlySummon?.WeaponImpact != null)
            {
                ApplySpellDamageToMonster(conn, state, spell.FriendlySummon.WeaponImpact, target, rng, skillLevel, false, tag);
                return true;
            }
            if (spell.HasDeferredProjectileModifierDamage
                && spell.ModifierEffects != null
                && spell.ModifierEffects.Count == 1
                && spell.ModifierEffects[0] != null
                && spell.ModifierEffects[0].EffectOrder == -1)
            {
                var modifierResult = CombatRuntime.Instance.ApplyProjectileModifierFromSpell(
                    target,
                    conn?.Avatar != null ? (uint)conn.Avatar.Id : 0u,
                    state,
                    spell,
                    rng,
                    skillLevel,
                    $"{tag}-SpellWeaponDamageEffect");
                referencedModifierHandled = true;
                Debug.LogError($"[SPELL-WEAPON-MODIFIER] spell={spell.SkillId} target={target.EntityId} modifier={spell.ProjectileModifierId} applied={modifierResult.AppliedModifier} reason={modifierResult.Reason ?? "none"} rngAfter={rng.CallsSinceReseed} sourceFunction=Weapon::applyDamage@0x00597E50->SpellModEffect::doEffect@0x00554460");
            }

            if (!spell.HasSpellKnockDownEffect)
                return referencedModifierHandled;

            int strength = spell.ResolveSpellKnockDownStrength(skillLevel);
            CombatRuntime.Instance.ApplySpellKnockDownEffectToMonster(
                rng,
                target,
                Math.Max(1, state != null ? state.Level : 1),
                strength,
                spell.SpellKnockDownChanceF32,
                $"{tag}-SpellWeaponDamageEffect",
                conn?.Avatar != null ? (uint)conn.Avatar.Id : 0,
                useWeaponImpactPosition: true);
            return referencedModifierHandled;
        }

        private SpellWeaponDamageEffectResult ApplySpellWeaponDamageEffect(
            RRConnection conn,
            PlayerState state,
            Combat.SpellData spell,
            Combat.Monster target,
            Combat.MersenneTwister rng,
            int skillLevel,
            string tag)
        {
            var result = new SpellWeaponDamageEffectResult
            {
                Attempted = true,
                OldHPWire = target != null ? CombatRuntime.Instance.PeekMonsterCurrentHPWire(target) : 0,
                NewHPWire = target != null ? CombatRuntime.Instance.PeekMonsterCurrentHPWire(target) : 0,
                ResultName = "MISS"
            };

            if (state == null || spell == null || target == null || rng == null)
            {
                Debug.LogError($"[SPELL-WEAPON-DAMAGE] skipped spell={spell?.DisplayName ?? "unknown"} target={target?.EntityId ?? 0} state={(state != null)} rng={(rng != null)} tag={tag ?? "SPELL"}");
                return result;
            }

            int arMod = ResolveSpellEffectPercent(spell.ARModMin, spell.ARModMax, spell.ARModInc, skillLevel, 100);
            int damageModRaw = ResolveSpellEffectRawMod(spell.WeaponEffectDamageModMin, spell.WeaponEffectDamageModMax, spell.WeaponEffectDamageModInc, skillLevel);
            int baseAttackRating = DamageResolver.ResolveAvatarAttackRating(state);
            long attackRatingScaled = (long)baseAttackRating * Math.Max(0, 100 + arMod);
            int attackRating = (int)Math.Clamp(attackRatingScaled / 100L, 0L, 0xFFFFL);
            int baseDamageMod = DamageResolver.ResolveDamageMod(state);
            int damageMod = DamageResolver.ResolveDamageMod(state, damageModRaw);
            int attackerLevel = Math.Max(0, state.Level);
            int defenderLevel = Math.Max(0, (int)target.Level);

            var damageInput = new WeaponDamageInput
            {
                Rng = rng,
                Source = $"{tag ?? "SPELL"}-SpellWeaponDamageEffect",
                AttackerEntityId = conn?.Avatar != null ? (uint)conn.Avatar.Id : null,
                DefenderEntityId = target.EntityId,
                AttackerLevel = attackerLevel,
                DefenderLevel = defenderLevel,
                AttackRating = attackRating,
                DefenseRating = DamageResolver.ResolveMonsterDefenseRating(target),
                BlockChance = 0,
                DamageLevel = DamageResolver.ResolveWeaponDamageLevel(state),
                DamageBonus = DamageResolver.ResolveWeaponDamageBonus(state),
                DamageMod = damageMod,
                WeaponClassId = DamageResolver.ResolveWeaponClassId(state),
                DamageTypeId = DamageResolver.ResolveDamageTypeId(state),
                WeaponDamageF32 = DamageResolver.GetWeaponBaseDamageF32(state),
                WeaponVolatilityF32 = DamageResolver.GetWeaponVolatilityF32(state),
                CritThreshold = DamageResolver.ResolveCriticalThreshold(state, target),
                CritDamagePercent = DamageResolver.ResolveCriticalDamagePercent(state),
                AttackerState = state,
                IncludeWeaponDamageAdds = true
            };

            DamageResolver.LogDamageSlots(state, damageInput, target, damageInput.Source);
            WeaponDamageResult damageResult = DamageResolver.ResolveWeaponDamage(damageInput);
            result.HitRaw = damageResult.HitRaw;
            result.BlockRaw = damageResult.BlockRaw;
            result.DamageRaw = damageResult.DamageRaw;
            result.HitRoll = damageResult.HitRoll;
            result.BlockRoll = damageResult.BlockRoll;
            result.HitThreshold = damageResult.HitThreshold;
            result.AttackRating = damageResult.AttackRating;
            result.DefenseRating = damageResult.DefenseRating;
            result.DamageMod = damageMod;
            result.SkillDamageModRaw = damageModRaw;
            result.ARMod = arMod;
            result.MinDamageWire = damageResult.MinDamageF32;
            result.MaxDamageWire = damageResult.MaxDamageF32;
            result.DamageWire = damageResult.DamageWire;
            result.IsCritical = damageResult.IsCritical;
            result.ResultName = damageResult.ResultName;

            bool landed = damageResult.IsHit && !damageResult.IsBlocked && damageResult.DamageWire > 0;
            if (!landed)
            {
                Debug.LogError($"[SPELL-WEAPON-DAMAGE] spell={spell.DisplayName} result={damageResult.ResultName} target={target.Name}#{target.EntityId} hp={result.OldHPWire}->{result.NewHPWire} arMod={arMod} ar={baseAttackRating}->{attackRating} dr={damageResult.DefenseRating} hitRaw=0x{damageResult.HitRaw:X8} hitRoll={damageResult.HitRoll} threshold={damageResult.HitThreshold} blockRaw=0x{damageResult.BlockRaw:X8} blockRoll={damageResult.BlockRoll} rngAfter={rng.CallsSinceReseed} tag={tag ?? "SPELL"}");
                Debug.LogError($"[COMBAT-EVENT] actor=player-spell-weapon actorId={(conn?.Avatar != null ? conn.Avatar.Id : 0)} target=monster targetId={target.EntityId} result={damageResult.ResultName} damageWire=0 hp={result.OldHPWire}->{result.NewHPWire} spell={spell.DisplayName} arMod={arMod} damageModRaw={damageModRaw} rngAfter={rng.CallsSinceReseed} marker={tag ?? "SPELL"}");
                return result;
            }

            result.ReferencedModifierHandled = ApplySpellWeaponDamageChildEffects(conn, state, spell, target, rng, skillLevel, tag);

            if (!target.IsAlive || CombatRuntime.Instance.PeekMonsterCurrentHPWire(target) == 0)
            {
                result.Landed = true;
                result.Applied = true;
                result.Died = true;
                result.NewHPWire = 0;
                return result;
            }

            bool applied = CombatRuntime.Instance.ApplyPlayerWeaponDamageToMonsterWire(
                target,
                damageResult,
                $"{tag}-SpellWeaponDamageEffect",
                out uint oldHPWire,
                out uint newHPWire,
                out bool died,
                out uint effectRaw,
                rng,
                "player-spell-weapon",
                _combatTick,
                conn?.Avatar != null ? (uint)conn.Avatar.Id : 0u,
                damageKind: 3);
            if (applied)
                NotifyMonsterDamagedByConnection(conn, target, $"{tag}-weapon");

            result.Landed = true;
            result.Applied = applied;
            result.Died = died;
            result.OldHPWire = oldHPWire;
            result.NewHPWire = newHPWire;

            string resultName = damageResult.IsCritical ? "CRIT" : "HIT";
            Debug.LogError($"[SPELL-WEAPON-DAMAGE] spell={spell.DisplayName} result={resultName} target={target.Name}#{target.EntityId} damageWire={damageResult.DamageWire} totalWire={damageResult.TotalDamageWire} addCount={(damageResult.DamageAdds != null ? damageResult.DamageAdds.Count : 0)} hp={oldHPWire}->{newHPWire} applied={applied} died={died} arMod={arMod} ar={baseAttackRating}->{attackRating} dr={damageResult.DefenseRating} damageMod={baseDamageMod}->{damageMod} damageModRaw={damageModRaw} range=[{damageResult.MinDamageF32},{damageResult.MaxDamageF32}] hitRaw=0x{damageResult.HitRaw:X8} blockRaw=0x{damageResult.BlockRaw:X8} dmgRaw=0x{damageResult.DamageRaw:X8} crit={damageResult.IsCritical} effectRaw=0x{effectRaw:X8} rngAfter={rng.CallsSinceReseed} tag={tag ?? "SPELL"}");
            Debug.LogError($"[COMBAT-EVENT] actor=player-spell-weapon actorId={(conn?.Avatar != null ? conn.Avatar.Id : 0)} target=monster targetId={target.EntityId} result={resultName} damageWire={damageResult.DamageWire} totalWire={damageResult.TotalDamageWire} addCount={(damageResult.DamageAdds != null ? damageResult.DamageAdds.Count : 0)} hp={oldHPWire}->{newHPWire} range=[{damageResult.MinDamageF32},{damageResult.MaxDamageF32}] hitRaw=0x{damageResult.HitRaw:X8} blockRaw=0x{damageResult.BlockRaw:X8} damageRaw=0x{damageResult.DamageRaw:X8} effectRaw=0x{effectRaw:X8} arMod={arMod} damageModRaw={damageModRaw} spell={spell.DisplayName} rngAfter={rng.CallsSinceReseed} marker={tag ?? "SPELL"}");
            return result;
        }

        private static int ResolveSpellEffectRawMod(int min, int max, int inc, int skillLevel)
        {
            int raw = min + (Math.Max(1, skillLevel) * inc);
            if (max > 0 && raw > max)
                raw = max;
            return raw;
        }

        private static int ResolveSpellEffectPercent(int min, int max, int inc, int skillLevel, int fallback)
        {
            int raw = ResolveSpellEffectRawMod(min, max, inc, skillLevel);
            return raw > 0 ? raw : fallback;
        }

        private static int ResolveSpellDamageStunMod(Combat.SpellData spell, PlayerState state)
        {
            int effectStunMod = unchecked((ushort)(spell?.DamageStunMod ?? 50));
            int unitStunMod = state?.StunMod ?? 100;
            int product = unchecked(effectStunMod * unitStunMod);
            return unchecked((ushort)(product / 100));
        }

        private void NotifyMonsterDamagedByConnection(RRConnection conn, Combat.Monster monster, string reason)
        {
            if (conn?.Avatar == null || monster == null) return;
            Combat.CombatRuntime.Instance.NotifyMonsterOnAttackedAdmission(monster, (uint)conn.Avatar.Id, reason);
        }

        private void LogMonsterOnAttackedBlocked(RRConnection conn, Combat.Monster monster, string source, string reason)
        {
            if (monster == null) return;
            uint playerId = conn?.Avatar != null ? (uint)conn.Avatar.Id : 0u;
            Debug.LogError($"[MON-ONATTACKED-NO-ADMISSION] monster={monster.Name}#{monster.EntityId} player={playerId} source={source ?? "unknown"} reason={reason ?? "unknown"} sourceFunction=Damage::apply@0x004F6580->MonsterBehavior2::onAttacked@0x0051B550");
        }

        private void HandleChainSpell(RRConnection conn, PlayerState state, Combat.Monster source,
    Combat.SpellData spell, Combat.MersenneTwister rng, int skillLevel = 1)
        {
            if (conn == null || source == null || spell == null || rng == null || spell.NumChains <= 1)
                return;
            string instanceKey = !string.IsNullOrWhiteSpace(source?.InstanceKey)
                ? source.InstanceKey
                : GetInstanceZoneKey(conn);
            string zoneName = !string.IsNullOrWhiteSpace(source?.ZoneName)
                ? source.ZoneName
                : conn?.CurrentZoneName;
            uint chainViewerEntityId = conn?.Avatar != null ? (uint)conn.Avatar.Id : 0u;
            var hit = new System.Collections.Generic.HashSet<uint> { source.EntityId };
            Combat.CombatRuntime.Instance.TryGetMonsterClientVisiblePositionFixed(
                source,
                chainViewerEntityId,
                out int lastFixedX,
                out int lastFixedY,
                out int lastFixedZ);
            var pending = new PendingChainSpell
            {
                Sequence = ++_nextPendingChainSpellSequence,
                Conn = conn,
                State = state,
                Spell = spell,
                Rng = rng,
                Hit = hit,
                Center = source,
                InstanceKey = instanceKey,
                ZoneName = zoneName,
                ViewerEntityId = chainViewerEntityId,
                DueTick = unchecked((int)_combatTick) + Math.Max(1, spell.ChainDelayTicks),
                ActiveBranchExpiryTick = unchecked((int)_combatTick) + Math.Max(1, spell.ChainLifespanTicks),
                RemainingChains = spell.NumChains - 1,
                LastFixedX = lastFixedX,
                LastFixedY = lastFixedY,
                LastFixedZ = lastFixedZ,
                SkillLevel = skillLevel
            };
            _pendingChainSpells.Enqueue(pending);
            CombatRuntime.Instance.RegisterClientSubEntity(
                ClientSubEntityKind.PlayerSpellChainProjectile,
                pending.Sequence,
                pending.InstanceKey,
                RemovePendingChainSpellRuntime);
            Debug.LogError($"[SPELL-CHAIN] state=init seq={pending.Sequence} source={source.EntityId} dueTick={pending.DueTick} expiryTick={pending.ActiveBranchExpiryTick} delay={spell.ChainDelayTicks} lifespan={spell.ChainLifespanTicks} remaining={pending.RemainingChains} sourceFunction=SpellChainEffect::doEffect@0x0054AC70->SpellChainProjectile::init@0x0054BD80");
        }

        private bool TryResolveNextChainSpellTarget(
            PendingChainSpell pending,
            out Combat.Monster next,
            out int nextFixedX,
            out int nextFixedY,
            out int nextFixedZ)
        {
            next = null;
            nextFixedX = pending?.LastFixedX ?? 0;
            nextFixedY = pending?.LastFixedY ?? 0;
            nextFixedZ = pending?.LastFixedZ ?? 0;
            if (pending == null || pending.Spell == null || pending.Hit == null)
                return false;
            int chainRangeFixed = WorldCollision.ToFixed8(pending.Spell.ChainRange);
            int centerFixedX = pending.LastFixedX;
            int centerFixedY = pending.LastFixedY;
            int centerFixedZ = pending.LastFixedZ;
            if (pending.Center != null)
                Combat.CombatRuntime.Instance.TryGetMonsterClientVisiblePositionFixed(
                    pending.Center,
                    pending.ViewerEntityId,
                    out centerFixedX,
                    out centerFixedY,
                    out centerFixedZ);
            List<Combat.Monster> candidates = Combat.CombatRuntime.Instance.GetProjectileHittableMonstersInNativeDistanceOrder(
                centerFixedX,
                centerFixedY,
                centerFixedZ,
                pending.InstanceKey,
                pending.ZoneName,
                chainRangeFixed,
                Combat.ProjectileUnitFinderMode.RepeatEnemies,
                pending.ViewerEntityId,
                true);
            for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
            {
                Combat.Monster candidate = candidates[candidateIndex];
                if (candidate == null || pending.Hit.Contains(candidate.EntityId))
                    continue;
                if (!Combat.CombatRuntime.Instance.TryGetMonsterClientVisiblePositionFixed(
                        candidate,
                        pending.ViewerEntityId,
                        out int candidateFixedX,
                        out int candidateFixedY,
                        out int candidateFixedZ))
                    continue;
                if (WorldCollision.Instance.TrySegmentHitFixed(
                        pending.ZoneName,
                        pending.InstanceKey,
                        centerFixedX,
                        centerFixedY,
                        centerFixedZ,
                        candidateFixedX,
                        candidateFixedY,
                        candidateFixedZ,
                        0,
                        out _))
                    continue;
                next = candidate;
                nextFixedX = candidateFixedX;
                nextFixedY = candidateFixedY;
                nextFixedZ = candidateFixedZ;
                return true;
            }
            return false;
        }


        private void HandleSelfCastSpell(
            RRConnection conn,
            PlayerState state,
            byte slotID,
            ushort componentId,
            byte responseId,
            byte spellSessionId,
            uint actionReceiveTick,
            uint actionResponseWriterTick,
            uint actionResponseSimulationApplyTick,
            uint skillUseTick,
            bool startsAfterSkillsChild)
        {
            Combat.SpellDatabase.Initialize();

            var spell = ResolveSpellFromManip(conn, slotID);

            if (spell == null)
            {
                Debug.LogError($"[SPELL-0x52] action=resolveSelfCast manipId={slotID} state=missing");
                SkillEffectTracker.RecordPlayerGraph(conn?.Avatar != null ? (uint)conn.Avatar.Id : 0u, 0, null, "SELF", 0, "rejected", "skill-unresolved", _combatTick);
                return;
            }

            if (!Combat.SpellDatabase.TryValidatePlayerSelfRuntime(spell, out string selfRuntimeReason))
            {
                SkillEffectTracker.RecordPlayerGraph(conn?.Avatar != null ? (uint)conn.Avatar.Id : 0u, 0, spell.SkillId, spell.TargetType, spell.OrderedEffects?.Count ?? 0, "rejected", selfRuntimeReason, _combatTick);
                Debug.LogError($"[SKILL-USE] action=self result=rejected manipId={slotID} gc={spell.SkillId} reason={selfRuntimeReason ?? "unsupported-self-effect"} sourceFunction=ActiveSkill::validateUse@0x00538710");
                return;
            }
            SkillEffectTracker.RecordPlayerGraph(conn?.Avatar != null ? (uint)conn.Avatar.Id : 0u, 0, spell.SkillId, spell.TargetType, spell.OrderedEffects.Count, "accepted", null, _combatTick);

            int skillLevel = GetPlayerSkillLevel(conn, spell);
            Debug.LogError($"[SPELL-0x52] action=handleSelfCast class={state.ClassName} manipId={slotID} spell={spell?.DisplayName ?? "UNKNOWN"} skillLevel={skillLevel}");

            bool selfSkillUseCommitted = false;
            if (spell != null)
            {
                int selfTriggerFrames = ResolveActiveSkillTriggerFrames(spell, state);
                int selfAnimationFrames = Combat.SpellDatabase.TryResolvePlayerAnimationTiming(spell.AnimationId, state, out int selfFrames, out _) && selfFrames > 0
                    ? selfFrames
                    : spell.AnimationLengthFrames;
                Debug.LogError($"[SKILL-USE] action=self manipId={slotID} gc={spell.SkillId} spell={spell.DisplayName} level={skillLevel} anim={spell.AnimationId} frames={selfAnimationFrames} trigger={selfTriggerFrames} component=0x{componentId:X4} session={spellSessionId} tick={_combatTick}");
                selfSkillUseCommitted = CommitActiveSkillUse(conn, state, spell, componentId, slotID, skillLevel, unchecked((int)skillUseTick), startsAfterSkillsChild);
                if (!selfSkillUseCommitted)
                    return;
                if (!spell.IsAoE)
                {
                    Combat.SpellModifierData selfModifier = null;
                    if (spell.FriendlySummon?.IsHitProc == false || TryResolveSelfAttributeModifier(spell, out selfModifier))
                    {
                        QueuePendingSelfSpellEffect(
                            conn,
                            state,
                            spell,
                            selfModifier,
                            skillLevel,
                            unchecked((uint)(startsAfterSkillsChild
                                ? ResolveActiveSkillEffectTickAfterSkillsChild(skillUseTick, spell, state)
                                : ResolveActiveSkillEffectTickBeforeSkillsChild(skillUseTick, spell, state))));
                    }
                }
            }

            if (spell != null && spell.IsAoE)
            {
                int triggerFrames = ResolveActiveSkillTriggerFrames(spell, state);
                ResolveSpellSourcePoint(conn, spell, state,
                    out int castFixedX, out int castFixedY, out int castFixedZ, out int castHeadingFixed,
                    out bool castSourceOffsetResolved, out int castSourceOffsetFixedX, out int castSourceOffsetFixedY, out int castSourceOffsetFixedZ);
                var pending = new PendingAoECast
                {
                    Sequence = ++_nextPendingAoECastSequence,
                    Conn = conn,
                    State = state,
                    Spell = spell,
                    SkillLevel = skillLevel,
                    InstanceKey = GetInstanceZoneKey(conn),
                    ComponentId = componentId,
                    ActionResponseId = responseId,
                    ActionResponseSessionId = spellSessionId,
                    SlotId = slotID,
                    SkillUseCommitted = selfSkillUseCommitted,
                    AwaitingActionResponsePacket = false,
                    ActionReceiveTick = actionReceiveTick,
                    ActionResponseWriterTick = actionResponseWriterTick,
                    ActionResponseSimulationApplyTick = actionResponseSimulationApplyTick,
                    CastFixedX = castFixedX,
                    CastFixedY = castFixedY,
                    CastFixedZ = castFixedZ,
                    CastHeadingFixed = castHeadingFixed,
                    CastTick = skillUseTick,
                    CastSourceOffsetResolved = castSourceOffsetResolved,
                    CastSourceOffsetFixedX = castSourceOffsetFixedX,
                    CastSourceOffsetFixedY = castSourceOffsetFixedY,
                    CastSourceOffsetFixedZ = castSourceOffsetFixedZ,
                    DueTick = unchecked((uint)(startsAfterSkillsChild
                        ? ResolveActiveSkillEffectTickAfterSkillsChild(skillUseTick, spell, state)
                        : ResolveActiveSkillEffectTickBeforeSkillsChild(skillUseTick, spell, state)))
                };
                lock (_pendingAoECastLock)
                {
                    _pendingAoECasts.Add(pending);
                }
                Debug.LogError($"[SPELL-0x52] action=aoeDefer spell={spell.DisplayName} triggerFrames={triggerFrames} receivedTick={actionReceiveTick} packetWriterTick={actionResponseWriterTick} castTick={skillUseTick} dueTick={pending.DueTick}");
            }
            else
            {
                Debug.LogError($"[SPELL-0x52] aoe=false action=selfCastDamage slotId={slotID} state=skipped");
            }
        }

    }
}
