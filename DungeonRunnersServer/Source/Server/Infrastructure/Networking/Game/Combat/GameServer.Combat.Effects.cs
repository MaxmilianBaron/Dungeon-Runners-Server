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
    {        private System.Collections.Concurrent.ConcurrentQueue<PendingSpell> _pendingSpells = new System.Collections.Concurrent.ConcurrentQueue<PendingSpell>();
        private long _nextPendingSpellProjectileSequence;
        private readonly System.Collections.Concurrent.ConcurrentQueue<PendingChainSpell> _pendingChainSpells = new System.Collections.Concurrent.ConcurrentQueue<PendingChainSpell>();
        private long _nextPendingChainSpellSequence;
        private struct PendingAoECast
        {
            public long Sequence;
            public RRConnection Conn;
            public PlayerState State;
            public Combat.SpellData Spell;
            public int SkillLevel;
            public string InstanceKey;
            public ushort ComponentId;
            public byte ActionResponseId;
            public byte ActionResponseSessionId;
            public byte SlotId;
            public bool AwaitingActionResponsePacket;
            public uint ActionReceiveTick;
            public uint ActionResponseWriterTick;
            public uint ActionResponseSimulationApplyTick;
            public bool SkillUseCommitted;
            public int CastFixedX;
            public int CastFixedY;
            public int CastFixedZ;
            public int CastHeadingFixed;
            public uint CastTick;
            public bool CastSourceOffsetResolved;
            public int CastSourceOffsetFixedX;
            public int CastSourceOffsetFixedY;
            public int CastSourceOffsetFixedZ;
            public uint DueTick;
        }
        private struct PendingSelfCastAction
        {
            public long Sequence;
            public RRConnection Conn;
            public PlayerState State;
            public uint PlayerEntityId;
            public string InstanceKey;
            public ushort ComponentId;
            public byte ActionResponseId;
            public byte ActionResponseSessionId;
            public byte SlotId;
            public bool AwaitingActionResponsePacket;
            public uint ActionReceiveTick;
            public uint ActionResponseWriterTick;
            public uint ActionResponseSimulationApplyTick;
            public int ActionResponseWireMessageIndex;
        }
        private struct PendingSelfSpellEffect
        {
            public long Sequence;
            public RRConnection Conn;
            public PlayerState State;
            public Combat.SpellData Spell;
            public Combat.SpellModifierData Modifier;
            public int SkillLevel;
            public string InstanceKey;
            public uint DueTick;
        }
        private readonly List<PendingAoECast> _pendingAoECasts = new List<PendingAoECast>();
        private readonly object _pendingAoECastLock = new object();
        private long _nextPendingAoECastSequence;
        private readonly List<PendingSelfCastAction> _pendingSelfCastActions = new List<PendingSelfCastAction>();
        private readonly object _pendingSelfCastActionLock = new object();
        private long _nextPendingSelfCastActionSequence;
        private readonly Dictionary<int, PendingSelfCastAction> _pendingSelfCastBehaviorActions = new Dictionary<int, PendingSelfCastAction>();
        private readonly List<PendingSelfSpellEffect> _pendingSelfSpellEffects = new List<PendingSelfSpellEffect>();
        private readonly object _pendingSelfSpellEffectLock = new object();
        private long _nextPendingSelfSpellEffectSequence;

        private void QueuePendingSelfCastAction(RRConnection conn, PlayerState state, byte slotId, ushort componentId, byte responseId, byte sessionId)
        {
            if (conn == null || state == null)
                return;
            var pending = new PendingSelfCastAction
            {
                Conn = conn,
                State = state,
                PlayerEntityId = conn.Avatar != null ? (uint)conn.Avatar.Id : 0u,
                InstanceKey = GetInstanceZoneKey(conn),
                ComponentId = componentId,
                ActionResponseId = responseId,
                ActionResponseSessionId = sessionId,
                SlotId = slotId,
                AwaitingActionResponsePacket = true,
                ActionReceiveTick = _combatTick
            };
            lock (_pendingSelfCastActionLock)
            {
                pending.Sequence = ++_nextPendingSelfCastActionSequence;
                _pendingSelfCastActions.Add(pending);
            }
            Debug.LogError($"[SPELL-0x52] action=queueSelfCast seq={pending.Sequence} component={componentId} responseId={responseId} session={sessionId} slot={slotId} receivedTick={pending.ActionReceiveTick} simulationApplyTick=awaiting-response");
        }

        private bool HasPendingSelfCastBehaviorAction(RRConnection conn)
        {
            return conn != null && _pendingSelfCastBehaviorActions.ContainsKey(conn.ConnId);
        }

        private bool IsSelfCastActionCurrent(PendingSelfCastAction pending)
        {
            return pending.Conn != null
                && pending.Conn.IsConnected
                && pending.Conn.IsSpawned
                && pending.State != null
                && pending.PlayerEntityId != 0
                && pending.Conn.Avatar?.Id == pending.PlayerEntityId
                && IsPendingConnectionInstanceCurrent(pending.Conn, pending.InstanceKey);
        }

        private bool CanAdmitSelfCastBehaviorAction(PendingSelfCastAction pending)
        {
            if (!IsSelfCastActionCurrent(pending) || pending.State.CurrentHPWire == 0)
                return false;
            Combat.SpellData spell = ResolveSpellFromManip(pending.Conn, pending.SlotId);
            return spell != null
                && Combat.SpellDatabase.TryValidatePlayerSelfRuntime(spell, out _)
                && Combat.SpellDatabase.TryValidatePlayerResourceCommitRuntime(spell, out _)
                && !IsActiveSkillCooldown(pending.Conn, spell, pending.SlotId, out _)
                && pending.State.CurrentManaWire >= Combat.SpellDatabase.GetManaCost(spell, GetPlayerSkillLevel(pending.Conn, spell), 0);
        }

        private void DiscardPendingSelfCastBehaviorAction(RRConnection conn)
        {
            if (conn != null)
                _pendingSelfCastBehaviorActions.Remove(conn.ConnId);
        }

        private void ApplyPendingSelfCastActionInputs(uint simulationTick)
        {
            List<PendingSelfCastAction> due = null;
            lock (_pendingSelfCastActionLock)
            {
                for (int index = 0; index < _pendingSelfCastActions.Count;)
                {
                    PendingSelfCastAction pending = _pendingSelfCastActions[index];
                    if (!IsPendingClientActionMessageSelected(pending.Conn?.ConnId ?? 0, pending.ActionResponseWriterTick, pending.ActionResponseWireMessageIndex)
                        || pending.AwaitingActionResponsePacket
                        || simulationTick < pending.ActionResponseSimulationApplyTick)
                    {
                        index++;
                        continue;
                    }
                    _pendingSelfCastActions.RemoveAt(index);
                    due ??= new List<PendingSelfCastAction>();
                    due.Add(pending);
                }
            }
            if (due == null)
                return;
            foreach (PendingSelfCastAction pending in due)
            {
                if (!CanAdmitSelfCastBehaviorAction(pending))
                    continue;
                DiscardPendingUseTargetBehaviorAction(pending.Conn, "Use-replaces-pending-UseTarget");
                DiscardPendingPlayerUsePositionWeaponBehaviorAction(pending.Conn, "Use-replaces-pending-UsePosition");
                DiscardPendingPlayerSpellBehaviorAction(pending.Conn, 0, "Use-replaces-pending-UsePosition");
                DiscardPendingSelfCastBehaviorAction(pending.Conn);
                if (HasCurrentPlayerBehaviorSlot(pending.Conn))
                {
                    _pendingSelfCastBehaviorActions[pending.Conn.ConnId] = pending;
                    Debug.LogError($"[SPELL-0x52] action=pendingBehavior seq={pending.Sequence} conn={pending.Conn.ConnId} slot={pending.SlotId} tick={simulationTick} sourceFunction=Behavior::doActionLocal@0x00515130 slot=Behavior+0x78");
                    continue;
                }
                StartSelfCastBehaviorAction(pending, simulationTick, false);
            }
        }

        private void ProcessPendingSelfCastActions(uint playerEntityId, uint simulationTick)
        {
            RRConnection conn = FindConnectionByAvatarEntityId(playerEntityId);
            if (conn == null || !_pendingSelfCastBehaviorActions.TryGetValue(conn.ConnId, out PendingSelfCastAction pending))
                return;
            if (!IsSelfCastActionCurrent(pending))
            {
                _pendingSelfCastBehaviorActions.Remove(conn.ConnId);
                return;
            }
            if (conn.PlayerBehaviorActionStoppedTick == simulationTick || HasCurrentPlayerBehaviorSlot(conn))
                return;
            _pendingSelfCastBehaviorActions.Remove(conn.ConnId);
            StartSelfCastBehaviorAction(pending, simulationTick, true);
        }

        private void StartSelfCastBehaviorAction(PendingSelfCastAction pending, uint simulationTick, bool startsAfterSkillsChild)
        {
            if (!CanAdmitSelfCastBehaviorAction(pending))
                return;
            Debug.LogError($"[SPELL-0x52] action=startBehavior seq={pending.Sequence} conn={pending.Conn.ConnId} slot={pending.SlotId} tick={simulationTick} afterSkillsChild={startsAfterSkillsChild} sourceFunction=Use::start@0x00546620->Use::States@0x005467D0");
            HandleSelfCastSpell(
                pending.Conn, pending.State, pending.SlotId, pending.ComponentId,
                pending.ActionResponseId, pending.ActionResponseSessionId,
                pending.ActionReceiveTick, pending.ActionResponseWriterTick,
                pending.ActionResponseSimulationApplyTick, simulationTick, startsAfterSkillsChild);
        }

        private static bool TryResolveSelfAttributeModifier(Combat.SpellData spell, out Combat.SpellModifierData modifier)
        {
            modifier = null;
            if (spell == null
                || (!string.Equals(spell.TargetType, "SELF", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(spell.TargetType, "0", StringComparison.OrdinalIgnoreCase))
                || spell.ModifierEffects == null
                || spell.ModifierEffects.Count != 1
                || !Combat.SpellDatabase.TryValidatePlayerSelfRuntime(spell, out _))
                return false;
            modifier = spell.ModifierEffects[0];
            return modifier != null && modifier.Attributes != null && modifier.Attributes.Count > 0;
        }

        private void QueuePendingSelfSpellEffect(RRConnection conn, PlayerState state, Combat.SpellData spell, Combat.SpellModifierData modifier, int skillLevel, uint dueTick)
        {
            var pending = new PendingSelfSpellEffect
            {
                Conn = conn,
                State = state,
                Spell = spell,
                Modifier = modifier,
                SkillLevel = skillLevel,
                InstanceKey = GetInstanceZoneKey(conn),
                DueTick = dueTick
            };
            lock (_pendingSelfSpellEffectLock)
            {
                pending.Sequence = ++_nextPendingSelfSpellEffectSequence;
                _pendingSelfSpellEffects.Add(pending);
            }
            Debug.LogError($"[SELF-SPELL-EFFECT] action=queue seq={pending.Sequence} player={conn?.LoginName ?? "unknown"} spell={spell?.SkillId ?? ""} modifier={modifier?.ModifierId ?? ""} attributes={modifier?.Attributes?.Count ?? 0} level={skillLevel} dueTick={dueTick} sourceFunction=ActiveSkill::update@0x005392F0 ActiveSkill::doSkillEffect@0x00539630");
        }

        private void ProcessPendingSelfSpellEffects(uint playerEntityId, uint simulationTick)
        {
            List<PendingSelfSpellEffect> due = null;
            lock (_pendingSelfSpellEffectLock)
            {
                for (int pendingIndex = 0; pendingIndex < _pendingSelfSpellEffects.Count;)
                {
                    PendingSelfSpellEffect pending = _pendingSelfSpellEffects[pendingIndex];
                    uint pendingPlayerEntityId = pending.Conn?.Avatar != null ? (uint)pending.Conn.Avatar.Id : 0u;
                    if (pendingPlayerEntityId != playerEntityId || simulationTick < pending.DueTick)
                    {
                        pendingIndex++;
                        continue;
                    }
                    _pendingSelfSpellEffects.RemoveAt(pendingIndex);
                    due ??= new List<PendingSelfSpellEffect>();
                    due.Add(pending);
                }
            }
            if (due == null)
                return;
            foreach (PendingSelfSpellEffect pending in due)
            {
                if (pending.Conn == null
                    || !pending.Conn.IsConnected
                    || !pending.Conn.IsSpawned
                    || pending.Conn.Avatar == null
                    || pending.State == null
                    || pending.Spell == null
                    || (pending.Modifier == null && pending.Spell.FriendlySummon?.IsHitProc != false)
                    || !string.Equals(RoomRuntime.NormalizeInstanceKey(GetInstanceZoneKey(pending.Conn)), RoomRuntime.NormalizeInstanceKey(pending.InstanceKey), StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!_playerStates.TryGetValue(pending.Conn.ConnId.ToString(), out PlayerState currentState)
                    || !ReferenceEquals(currentState, pending.State))
                    continue;
                if (pending.Spell.FriendlySummon?.IsHitProc == false)
                {
                    ResolveSpellSourcePoint(pending.Conn, pending.Spell, pending.State,
                        out int x, out int y, out int z, out int heading, out _, out _, out _, out _);
                    CombatRuntime.Instance.SpawnFriendlySummons(CombatRuntime.Instance.GetPlayer(playerEntityId), pending.Spell, pending.SkillLevel,
                        x, y, z, x + UnitMover.ZRotateCosFixed(heading >> 8), y + UnitMover.ZRotateSinFixed(heading >> 8));
                    continue;
                }
                var attributes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                bool attributesValid = pending.Modifier.Attributes != null && pending.Modifier.Attributes.Count > 0;
                for (int attributeIndex = 0; attributesValid && attributeIndex < pending.Modifier.Attributes.Count; attributeIndex++)
                {
                    Combat.SpellAttributeModifierData attribute = pending.Modifier.Attributes[attributeIndex];
                    if (attribute == null || string.IsNullOrWhiteSpace(attribute.Attribute))
                    {
                        attributesValid = false;
                        break;
                    }
                    int value = attribute.ResolveLevelValue(pending.SkillLevel);
                    attributes[attribute.Attribute] = attributes.TryGetValue(attribute.Attribute, out int existingValue)
                        ? checked(existingValue + value)
                        : value;
                }
                string modifierType = !string.IsNullOrWhiteSpace(pending.Modifier.ModifierId)
                    ? pending.Modifier.ModifierId
                    : pending.Modifier.EffectId;
                if (!attributesValid || attributes.Count == 0 || string.IsNullOrWhiteSpace(modifierType))
                    continue;
                ushort durationTicks = pending.Modifier.ResolveDurationTicks(pending.SkillLevel);
                uint powerLevel = unchecked((uint)pending.Spell.ResolvePowerLevelF32(pending.SkillLevel));
                uint sourceEntityId = (uint)pending.Conn.Avatar.Id;
                if (!_activeModifiers.TryNextId(out uint modifierId))
                {
                    Debug.LogError($"[SELF-SPELL-EFFECT] action=reject seq={pending.Sequence} player={pending.Conn.LoginName} spell={pending.Spell.SkillId} reason=modifier-id-range-exhausted tick={simulationTick}");
                    continue;
                }
                bool applied = pending.State.ApplyAuthoredAttributeModifier(
                    modifierType,
                    attributes,
                    durationTicks,
                    pending.Modifier.RemoveOnDeath,
                    $"ActiveSkill::doSkillEffect:{pending.Spell.SkillId}",
                    modifierType,
                    sourceEntityId,
                    pending.Spell.SkillId,
                    pending.Modifier.EffectId ?? pending.Spell.EffectId,
                    powerLevel,
                    pending.Modifier.StackRule,
                    pending.Modifier.TerminateWhenHitChance,
                    (byte)Math.Clamp(pending.SkillLevel, 0, byte.MaxValue),
                    0x01);
                if (applied)
                {
                    RecordModifierSent(
                        pending.Conn.LoginName,
                        modifierType,
                        modifierId,
                        (byte)Math.Clamp(pending.SkillLevel, 0, byte.MaxValue),
                        powerLevel,
                        durationTicks,
                        0x01);
                }
                Debug.LogError($"[SELF-SPELL-EFFECT] action=apply seq={pending.Sequence} player={pending.Conn.LoginName} entity={sourceEntityId} spell={pending.Spell.SkillId} modifier={modifierType} attributes={string.Join(",", attributes)} level={pending.SkillLevel} durationF32={pending.Modifier.ResolveDurationF32(pending.SkillLevel)} durationTicks={durationTicks} power={powerLevel} terminateWhenHitChance={pending.Modifier.TerminateWhenHitChance} applied={applied} tick={simulationTick} sourceFunction=ActiveSkill::doSkillEffect@0x00539630 SpellModEffect::doEffect@0x00554460 AttributeModifier::doEvent@0x005689B0");
            }
        }

        private void ProcessPendingAoECasts(uint playerEntityId, uint simulationTick)
        {
            List<PendingAoECast> due = null;
            lock (_pendingAoECastLock)
            {
                if (_pendingAoECasts.Count == 0) return;
                for (int i = 0; i < _pendingAoECasts.Count;)
                {
                    var pending = _pendingAoECasts[i];
                    uint pendingPlayerEntityId = pending.Conn?.Avatar != null ? (uint)pending.Conn.Avatar.Id : 0u;
                    if (pendingPlayerEntityId != playerEntityId || pending.AwaitingActionResponsePacket) { i++; continue; }
                    if (!pending.SkillUseCommitted && simulationTick >= pending.CastTick)
                    {
                        _pendingAoECasts.RemoveAt(i);
                        Debug.LogError($"[SPELL-0x52] action=aoeDrop seq={pending.Sequence} spell={pending.Spell?.SkillId ?? "unknown"} reason=resource-use-not-committed tick={simulationTick}");
                        continue;
                    }
                    if (!pending.SkillUseCommitted || simulationTick < pending.DueTick) { i++; continue; }
                    _pendingAoECasts.RemoveAt(i);
                    due ??= new List<PendingAoECast>();
                    due.Add(pending);
                }
            }
            if (due == null) return;
            foreach (var pending in due)
            {
                if (pending.Conn == null || !pending.Conn.IsConnected) continue;
                PendingAoECast resolved = pending;
                ResolveSpellSourcePoint(
                    resolved.Conn,
                    resolved.Spell,
                    resolved.State,
                    out resolved.CastFixedX,
                    out resolved.CastFixedY,
                    out resolved.CastFixedZ,
                    out resolved.CastHeadingFixed,
                    out resolved.CastSourceOffsetResolved,
                    out resolved.CastSourceOffsetFixedX,
                    out resolved.CastSourceOffsetFixedY,
                    out resolved.CastSourceOffsetFixedZ);
                ApplyAoECast(resolved);
            }
        }

        private static void ResolveActiveSkillAnimationTiming(Combat.SpellData spell, PlayerState state, out int totalFrames, out int triggerFrames)
        {
            int authoredTotalFrames = 0;
            int authoredTriggerFrames = 0;
            bool resolved = spell != null
                && Combat.SpellDatabase.TryResolvePlayerAnimationTiming(spell.AnimationId, state, out authoredTotalFrames, out authoredTriggerFrames);
            totalFrames = resolved && authoredTotalFrames > 0
                ? authoredTotalFrames
                : spell != null && spell.AnimationLengthFrames > 0 ? spell.AnimationLengthFrames : 30;
            triggerFrames = resolved && authoredTriggerFrames > 0
                ? authoredTriggerFrames
                : spell != null && spell.AnimationTriggerFrames > 0 ? spell.AnimationTriggerFrames : 15;
            int speed = Combat.SpellDatabase.ResolveActiveSkillSpeed(spell, state);
            if (speed != 100)
            {
                totalFrames = (int)(((long)unchecked((ushort)totalFrames) * 100L) / speed);
                triggerFrames = (int)(((long)triggerFrames * 100L) / speed);
            }
            triggerFrames = Math.Max(1, triggerFrames);
            totalFrames = Math.Max(1, totalFrames);
        }

        private static int ResolveActiveSkillTriggerFrames(Combat.SpellData spell, PlayerState state)
        {
            ResolveActiveSkillAnimationTiming(spell, state, out _, out int triggerFrames);
            return triggerFrames;
        }

        private static int ResolveActiveSkillEffectTickBeforeSkillsChild(uint skillUseTick, Combat.SpellData spell, PlayerState state)
        {
            return unchecked((int)skillUseTick) + ResolveActiveSkillTriggerFrames(spell, state);
        }

        private static int ResolveActiveSkillEffectTickAfterSkillsChild(uint skillUseTick, Combat.SpellData spell, PlayerState state)
        {
            return unchecked((int)skillUseTick) + ResolveActiveSkillTriggerFrames(spell, state) + 1;
        }

        private static int ResolvePendingSpellDueTick(int fireTick, Combat.SpellData spell, int projectileDelayTicks)
        {
            return spell != null && spell.HasTeleportEffect
                ? fireTick
                : unchecked(fireTick + projectileDelayTicks + 1);
        }

        private void ApplyAoECast(PendingAoECast pending)
        {
            RRConnection conn = pending.Conn;
            PlayerState state = pending.State;
            Combat.SpellData spell = pending.Spell;
            int skillLevel = pending.SkillLevel;
            if (conn == null || spell == null) return;
            bool sourceOffsetResolved = pending.CastSourceOffsetResolved;
            if (!spell.HasAoEEffect)
            {
                Debug.LogError($"[SPELL-0x52] action=aoeScan spell={spell.DisplayName} state=skipped reason=no-SpellAOEEffect");
                return;
            }
            int authoredRangeF32 = spell.ResolveAoERadiusF32(skillLevel);
            if (authoredRangeF32 <= 0)
            {
                Debug.LogError($"[SPELL-0x52] action=aoeScan spell={spell.DisplayName} state=skipped reason=no-authored-radius");
                return;
            }
            int rangeF32 = authoredRangeF32 > int.MaxValue - 0xA00
                ? int.MaxValue
                : authoredRangeF32 + 0xA00;
            var monstersInRange = Combat.CombatRuntime.Instance.GetMonstersInSpellEffectRangeFixed(
                pending.CastFixedX,
                pending.CastFixedY,
                pending.CastFixedZ,
                rangeF32,
                pending.InstanceKey);
            int maxTargets = spell.ResolveNumTargets(skillLevel);
            string targets = monstersInRange != null
                ? string.Join(",", monstersInRange.Take(maxTargets).Select(m => m.EntityId.ToString()))
                : "";
            string detail = BuildSpellEffectTargetDetail(monstersInRange, pending.CastFixedX, pending.CastFixedY, pending.CastFixedZ, maxTargets);
            Debug.LogError($"[SPELL-0x52] action=aoeScan count={monstersInRange?.Count ?? 0} maxTargets={maxTargets} targets={targets} rangeF32={rangeF32} posFixed=({pending.CastFixedX},{pending.CastFixedY},{pending.CastFixedZ}) castTick={pending.CastTick} headingFixed={pending.CastHeadingFixed} sourceOffsetFixed=({pending.CastSourceOffsetFixedX},{pending.CastSourceOffsetFixedY},{pending.CastSourceOffsetFixedZ}) sourceOffsetResolved={sourceOffsetResolved} detail={detail}");

            if (monstersInRange == null) return;
            int hits = 0;
            foreach (var monster in monstersInRange)
            {
                if (!monster.IsAlive) continue;
                if (hits >= maxTargets) break;

                ApplySpellDamageToMonster(conn, state, spell, monster, CombatRuntime.Instance.GetRoomRngForMonster(monster), skillLevel, true,
                    effectSourcePosition: (pending.CastFixedX, pending.CastFixedY));
                hits++;
            }
            if (hits > 0)
                Debug.LogError($"[SPELL-0x52] spell={spell.DisplayName} hits={hits} maxTargets={maxTargets} rangeF32={rangeF32}");
        }

        private static void ResolveSpellSourcePoint(RRConnection conn, Combat.SpellData spell, PlayerState state,
            out int sourceFixedX, out int sourceFixedY, out int sourceFixedZ, out int headingFixed,
            out bool sourceOffsetResolved, out int sourceOffsetFixedX, out int sourceOffsetFixedY, out int sourceOffsetFixedZ)
        {
            ResolveSpellActorPoint(conn, out sourceFixedX, out sourceFixedY, out sourceFixedZ);
            headingFixed = ResolveSpellSourceHeadingFixed(conn);
            ResolveSpellSourcePoint(
                sourceFixedX,
                sourceFixedY,
                sourceFixedZ,
                headingFixed,
                spell,
                state,
                out sourceFixedX,
                out sourceFixedY,
                out sourceFixedZ,
                out headingFixed,
                out sourceOffsetResolved,
                out sourceOffsetFixedX,
                out sourceOffsetFixedY,
                out sourceOffsetFixedZ);
        }

        private static void ResolveSpellSourcePoint(
            int actorFixedX,
            int actorFixedY,
            int actorFixedZ,
            int actorHeadingFixed,
            Combat.SpellData spell,
            PlayerState state,
            out int sourceFixedX,
            out int sourceFixedY,
            out int sourceFixedZ,
            out int headingFixed,
            out bool sourceOffsetResolved,
            out int sourceOffsetFixedX,
            out int sourceOffsetFixedY,
            out int sourceOffsetFixedZ)
        {
            sourceFixedX = actorFixedX;
            sourceFixedY = actorFixedY;
            sourceFixedZ = actorFixedZ;
            headingFixed = actorHeadingFixed;
            sourceOffsetFixedX = 0;
            sourceOffsetFixedY = 0;
            sourceOffsetFixedZ = 0;
            sourceOffsetResolved = spell != null && Combat.SpellDatabase.TryResolvePlayerAnimationSourceOffsetFixed(
                spell.AnimationId,
                state,
                out sourceOffsetFixedX,
                out sourceOffsetFixedY,
                out sourceOffsetFixedZ);
            if (sourceOffsetResolved)
            {
                int headingDegrees = UnitMover.WrapDegrees(headingFixed >> 8);
                int sin = UnitMover.ZRotateSinFixed(headingDegrees);
                int cos = UnitMover.ZRotateCosFixed(headingDegrees);
                sourceFixedX += (int)(((long)cos * sourceOffsetFixedX) >> 8)
                    - (int)(((long)sin * sourceOffsetFixedY) >> 8);
                sourceFixedY += (int)(((long)sin * sourceOffsetFixedX) >> 8)
                    + (int)(((long)cos * sourceOffsetFixedY) >> 8);
                sourceFixedZ += sourceOffsetFixedZ;
            }
        }

        private static void ResolveSpellActorPoint(RRConnection conn, out int actorFixedX, out int actorFixedY, out int actorFixedZ)
        {
            actorFixedX = conn != null && conn.HasOwnerAckFollowClientPosition
                ? conn.OwnerAckFollowClientPosFixedX
                : conn != null && conn.HasUnitFollowClientPosition
                    ? conn.UnitFollowClientPosFixedX
                : conn != null && conn.HasReflectedAvatarPosition
                    ? conn.ReflectedAvatarPosFixedX
                : conn != null && conn.HasLivePlayerPosition
                    ? conn.LivePlayerPosFixedX
                    : conn?.PlayerPosFixedX ?? 0;
            actorFixedY = conn != null && conn.HasOwnerAckFollowClientPosition
                ? conn.OwnerAckFollowClientPosFixedY
                : conn != null && conn.HasUnitFollowClientPosition
                    ? conn.UnitFollowClientPosFixedY
                : conn != null && conn.HasReflectedAvatarPosition
                    ? conn.ReflectedAvatarPosFixedY
                : conn != null && conn.HasLivePlayerPosition
                    ? conn.LivePlayerPosFixedY
                    : conn?.PlayerPosFixedY ?? 0;
            actorFixedZ = conn != null && conn.HasOwnerAckFollowClientPosition
                ? conn.OwnerAckFollowClientPosFixedZ
                : conn != null && conn.HasUnitFollowClientPosition
                    ? conn.UnitFollowClientPosFixedZ
                : conn != null && conn.HasReflectedAvatarPosition
                    ? conn.ReflectedAvatarPosFixedZ
                : conn != null && conn.HasLivePlayerPosition
                    ? conn.LivePlayerPosFixedZ
                    : conn?.PlayerPosFixedZ ?? 0;
        }

        private static int ResolveSpellSourceHeadingFixed(RRConnection conn)
        {
            if (conn == null)
                return 0;
            int headingFixed = conn.HasOwnerAckFollowClientPosition
                ? conn.OwnerAckFollowClientHeadingFixed
                : conn.HasUnitFollowClientPosition
                    ? conn.UnitFollowClientHeadingFixed
                : conn.HasReflectedAvatarPosition
                    ? conn.ReflectedAvatarHeadingFixed
                : conn.HasLivePlayerPosition
                    ? conn.LivePlayerHeadingFixed
                    : conn.PlayerHeadingFixed;
            return WrapHeadingFixed(headingFixed);
        }

        private static void ResolvePlayerWeaponProjectileActorPoint(RRConnection conn, out int actorFixedX, out int actorFixedY, out int actorFixedZ)
        {
            actorFixedX = conn != null && conn.HasOwnerAckFollowClientPosition
                ? conn.OwnerAckFollowClientPosFixedX
                : conn != null && conn.HasUnitFollowClientPosition
                    ? conn.UnitFollowClientPosFixedX
                : conn != null && conn.HasReflectedAvatarPosition
                    ? conn.ReflectedAvatarPosFixedX
                : conn != null && conn.HasLivePlayerPosition
                    ? conn.LivePlayerPosFixedX
                    : conn?.PlayerPosFixedX ?? 0;
            actorFixedY = conn != null && conn.HasOwnerAckFollowClientPosition
                ? conn.OwnerAckFollowClientPosFixedY
                : conn != null && conn.HasUnitFollowClientPosition
                    ? conn.UnitFollowClientPosFixedY
                : conn != null && conn.HasReflectedAvatarPosition
                    ? conn.ReflectedAvatarPosFixedY
                : conn != null && conn.HasLivePlayerPosition
                    ? conn.LivePlayerPosFixedY
                    : conn?.PlayerPosFixedY ?? 0;
            actorFixedZ = conn != null && conn.HasOwnerAckFollowClientPosition
                ? conn.OwnerAckFollowClientPosFixedZ
                : conn != null && conn.HasUnitFollowClientPosition
                    ? conn.UnitFollowClientPosFixedZ
                : conn != null && conn.HasReflectedAvatarPosition
                    ? conn.ReflectedAvatarPosFixedZ
                : conn != null && conn.HasLivePlayerPosition
                    ? conn.LivePlayerPosFixedZ
                    : conn?.PlayerPosFixedZ ?? 0;
        }

        private static int ResolvePlayerWeaponProjectileSourceHeadingFixed(RRConnection conn)
        {
            if (conn == null)
                return 0;
            int headingFixed = conn.HasOwnerAckFollowClientPosition
                ? conn.OwnerAckFollowClientHeadingFixed
                : conn.HasUnitFollowClientPosition
                    ? conn.UnitFollowClientHeadingFixed
                : conn.HasReflectedAvatarPosition
                    ? conn.ReflectedAvatarHeadingFixed
                : conn.HasLivePlayerPosition
                    ? conn.LivePlayerHeadingFixed
                    : conn.PlayerHeadingFixed;
            return WrapHeadingFixed(headingFixed);
        }

        private static string BuildSpellEffectTargetDetail(List<Combat.Monster> monsters, int sourceFixedX, int sourceFixedY, int sourceFixedZ, int maxTargets)
        {
            if (monsters == null || monsters.Count == 0 || maxTargets <= 0)
                return "";

            var parts = new List<string>();
            int count = 0;
            foreach (Combat.Monster monster in monsters)
            {
                if (monster == null)
                    continue;
                if (count >= maxTargets)
                    break;
                if (!Combat.CombatRuntime.Instance.TryGetMonsterClientVisiblePositionFixed(monster, 0, out int targetFixedX, out int targetFixedY, out int targetFixedZ))
                    continue;
                long distanceSqFixed8 = Combat.CombatRuntime.ProjectileFinderDistanceSqFixed8(
                    targetFixedX,
                    targetFixedY,
                    targetFixedZ,
                    sourceFixedX,
                    sourceFixedY,
                    sourceFixedZ);
                int distanceFixed8 = UnitMover.IntSqrt(distanceSqFixed8 << 8);
                parts.Add($"{monster.EntityId}:distF8={distanceFixed8}@({targetFixedX},{targetFixedY},{targetFixedZ})");
                count++;
            }
            return string.Join(",", parts);
        }

        private bool TryFinalizeMonsterKill(RRConnection conn, Combat.Monster monster, string source)
        {
            if (monster == null || monster.IsAlive || CombatRuntime.Instance.PeekMonsterCurrentHPWire(monster) != 0)
                return false;

            if (conn?.Avatar == null || !CombatRuntime.Instance.HasMonsterCombatParticipation(monster, (uint)conn.Avatar.Id))
            {
                CombatPlayer participant = CombatRuntime.Instance.ResolveMonsterLootParticipant(monster);
                conn = participant == null ? null : FindConnectionByAvatarEntityId(participant.EntityId);
            }

            string killSource = source ?? "unknown";
            Debug.LogError($"[KILL-FINALIZE] {killSource}: {monster.Name} eid={monster.EntityId} conn={(conn != null ? conn.ConnId.ToString() : "null")}");
            if (!_finalizedMonsterKills.Add(monster.EntityId))
            {
                Debug.LogError($"[KILL-DEDUP] {killSource}: {monster.Name} already finalized");
                return false;
            }
            try
            {
                ProcessMonsterKill(conn, monster, killSource);
                return true;
            }
            catch (Exception ex)
            {
                bool worldDeathCommitted = monster.DeathLifecycleActive;
                if (!worldDeathCommitted)
                    _finalizedMonsterKills.Remove(monster.EntityId);
                Debug.LogError($"[KILL-ERROR] {killSource}: failed to finalize {monster.Name}#{monster.EntityId} worldDeathCommitted={worldDeathCommitted} dedupRetained={worldDeathCommitted}: {ex}");
                if (worldDeathCommitted)
                    return true;
                throw;
            }
        }

        private static bool TryAdmitMonsterKillOwnerRewards(
            RRConnection conn,
            Combat.Monster monster,
            out string monsterInstanceKey,
            out string ownerInstanceKey,
            out string reason)
        {
            monsterInstanceKey = RoomRuntime.NormalizeInstanceKey(monster?.InstanceKey);
            ownerInstanceKey = RoomRuntime.NormalizeInstanceKey(conn?.RuntimeInstanceKey);
            if (monster == null || string.IsNullOrWhiteSpace(monster.InstanceKey))
            {
                reason = "monster-instance-missing";
                return false;
            }
            if (conn == null)
            {
                reason = "killer-connection-missing";
                return false;
            }
            if (!conn.IsConnected)
            {
                reason = "killer-disconnected";
                return false;
            }
            if (!conn.IsSpawned || conn.Avatar == null)
            {
                reason = "killer-not-spawned";
                return false;
            }
            if (string.IsNullOrWhiteSpace(conn.RuntimeInstanceKey))
            {
                reason = "killer-instance-missing";
                return false;
            }
            if (!string.Equals(ownerInstanceKey, monsterInstanceKey, StringComparison.OrdinalIgnoreCase))
            {
                reason = "killer-instance-mismatch";
                return false;
            }
            reason = "admitted";
            return true;
        }

        private static bool IsUseTargetingMonster(RRConnection conn, Combat.Monster monster)
        {
            if (conn == null || monster == null || !conn.HasActiveUseTarget)
                return false;

            ushort targetId = conn.ActiveUseTargetId;
            return targetId == (ushort)monster.EntityId ||
                   targetId == (ushort)monster.BehaviorId ||
                   targetId == (ushort)monster.UnitId ||
                   CombatRuntime.Instance.GetMonster(targetId) == monster ||
                   CombatRuntime.Instance.GetMonsterByComponent(targetId) == monster;
        }

        private bool TryAdmitPartyLootRecipient(RRConnection candidate, Combat.Monster monster, out int distanceSquaredF32, out string reason)
        {
            distanceSquaredF32 = 0;
            reason = "invalid";
            if (candidate == null || !candidate.IsConnected || !candidate.IsSpawned || candidate.Avatar == null)
                return false;
            if (GetCharSqlId(candidate) == 0)
            {
                reason = "character";
                return false;
            }
            string monsterInstance = RoomRuntime.NormalizeInstanceKey(monster?.InstanceKey);
            if (string.IsNullOrWhiteSpace(monsterInstance)
                || !string.Equals(RoomRuntime.NormalizeInstanceKey(candidate.RuntimeInstanceKey), monsterInstance, StringComparison.OrdinalIgnoreCase))
            {
                reason = "world";
                return false;
            }
            CombatPlayer player = candidate.Avatar.Id > 0
                ? CombatRuntime.Instance.GetPlayer((uint)candidate.Avatar.Id)
                : null;
            if (player == null || player.PlayerState == null
                || !string.Equals(RoomRuntime.NormalizeInstanceKey(player.InstanceKey), monsterInstance, StringComparison.OrdinalIgnoreCase))
            {
                reason = "combat-player";
                return false;
            }
            if (!player.IsAlive || player.PlayerState.CurrentHPWire == 0)
            {
                reason = "dead";
                return false;
            }
            distanceSquaredF32 = WorldEntityDistanceToSquaredF32(
                monster.PosFixedX,
                monster.PosFixedY,
                monster.PosFixedZ,
                player.PosFixedX,
                player.PosFixedY,
                player.PosFixedZ);
            if (distanceSquaredF32 >= 0x049DA400)
            {
                reason = "distance";
                return false;
            }
            if (!CombatRuntime.Instance.HasMonsterCombatParticipation(monster, player.EntityId))
            {
                reason = "not-combat-participant";
                return false;
            }
            reason = "admitted";
            return true;
        }

        private List<RRConnection> ResolvePartyLootRecipients(RRConnection killer, Combat.Monster monster, out Group group)
        {
            group = killer == null ? null : GroupDirectory.Instance.GetGroupForConn(killer.ConnId);
            var recipients = new List<RRConnection>();
            var seen = new HashSet<int>();
            if (group == null)
            {
                if (TryAdmitPartyLootRecipient(killer, monster, out _, out _))
                    recipients.Add(killer);
                return recipients;
            }

            foreach (GroupMember member in group.Members)
            {
                if (member == null || !member.IsOnline)
                    continue;
                RRConnection candidate = FindConnectionById(member.ConnId);
                if (candidate == null || !seen.Add(candidate.ConnId))
                    continue;
                if (TryAdmitPartyLootRecipient(candidate, monster, out _, out _))
                    recipients.Add(candidate);
            }
            if (killer != null && seen.Add(killer.ConnId)
                && TryAdmitPartyLootRecipient(killer, monster, out _, out _))
                recipients.Add(killer);
            return recipients;
        }

        private List<LootGenerationProfile> BuildLootGenerationProfiles(IReadOnlyList<RRConnection> recipients)
        {
            var profiles = new List<LootGenerationProfile>(recipients?.Count ?? 0);
            if (recipients == null)
                return profiles;
            foreach (RRConnection recipient in recipients)
            {
                PlayerState recipientState = GetPlayerState(recipient.ConnId.ToString());
                profiles.Add(new LootGenerationProfile
                {
                    CharacterId = GetCharSqlId(recipient),
                    PlayerLevel = Math.Clamp(recipientState?.Level ?? recipient.PlayerLevel, 1, 110),
                    IsMember = !IsPlayerFree(recipient.LoginName),
                    UnitGcType = recipient.AvatarGcType,
                    ClassName = recipientState?.ClassName ?? recipient.ClassName ?? string.Empty
                });
            }
            return profiles;
        }

        private RRConnection ResolveLootDropRecipient(LootDrop drop, IReadOnlyList<RRConnection> recipients)
        {
            if (drop == null || drop.RecipientCharacterId == 0 || recipients == null)
                return null;
            foreach (RRConnection recipient in recipients)
                if (GetCharSqlId(recipient) == drop.RecipientCharacterId)
                    return recipient;
            return null;
        }

        private static void ApplyServerReconstructionLootScaling(List<LootDrop> drops, Combat.Monster monster)
        {
            if (drops == null || monster == null)
                return;
            foreach (LootDrop drop in drops)
            {
                if (drop == null)
                    continue;
                if (drop.IsGold)
                    drop.GoldAmount = ServerReconstructionPolicy.ScaleNonNegative(drop.GoldAmount, monster.ServerReconstructionGoldPercent);
                else if (drop.IsItem)
                    drop.ItemLevel = ServerReconstructionPolicy.ScaleItemLevel(drop.ItemLevel, monster.ServerReconstructionItemLevelBonus);
            }
        }

        private void UpgradePartyLootPotions(List<LootDrop> drops, IReadOnlyList<RRConnection> recipients)
        {
            if (drops == null || recipients == null)
                return;
            foreach (RRConnection recipient in recipients)
            {
                uint characterId = GetCharSqlId(recipient);
                List<LootDrop> ownedDrops = drops.Where(drop => drop != null && drop.RecipientCharacterId == characterId).ToList();
                if (ownedDrops.Count > 0)
                    UpgradePotionsForMembers(ownedDrops, recipient);
            }
        }

        private void ProcessMonsterKill(RRConnection conn, Combat.Monster monster, string source)
        {
            bool ownerRewardEligible = TryAdmitMonsterKillOwnerRewards(
                conn,
                monster,
                out string monsterInstanceKey,
                out string ownerInstanceKey,
                out string ownerRewardReason);
            PlayerState playerState = ownerRewardEligible ? GetPlayerState(conn.ConnId.ToString()) : null;
            CombatRuntime.Instance.SetMonsterHPWire(monster, 0, true);
            CombatRuntime.Instance.MarkMonsterDead(monster, source);
            _serverKillCount++;
            Debug.LogError($"[KILL] ");
            Debug.LogError($"[KILL]  KILL #{_serverKillCount}: {monster.Name} via [{source}]");
            Debug.LogError($"[KILL] EntityId={monster.EntityId} GCType={monster.GCType} Level={monster.Level}");

            int experienceF32 = ResolveStockUnitOnDeadExperienceF32(monster);
            uint sourceLevel = monster.Level;
            if (experienceF32 > 0)
            {
                _pendingMonsterDeathExperience[monster.EntityId] = (monster, experienceF32, sourceLevel, source ?? "unknown");
                Debug.LogError($"[XP-ONDEAD] staged monster={monster.Name}#{monster.EntityId} experienceF32={experienceF32} sourceLevel={sourceLevel} source={source ?? "unknown"}");
            }
            CombatRuntime.Instance.BeginMonsterDeathLifecycle(monster, source);
            bool targetingKilledMonster = conn != null && IsUseTargetingMonster(conn, monster);
            targetingKilledMonster &= ownerRewardEligible;
            bool killedTargetWeaponBusy = targetingKilledMonster && Combat.WeaponUseRuntime.Instance.IsWeaponBusyOnKilledTarget(conn, monster);
            if (targetingKilledMonster)
            {
                if (killedTargetWeaponBusy)
                {
                    ushort actionFailureComponentId = conn.ActiveUseTargetComponentId != 0
                        ? conn.ActiveUseTargetComponentId
                        : ResolveClientControlComponentId(conn, 0);
                    byte actionFailureResponseId = conn.ActiveUseTargetResponseId;
                    bool pendingActionFailureReplacement = HasPendingUseTargetActionResponseForTarget(
                        conn,
                        monster.EntityId,
                        actionFailureComponentId,
                        actionFailureResponseId);
                    bool actionFailureQueued = false;
                    if (!pendingActionFailureReplacement
                        && !conn.ActiveUseTargetActionFailureQueued
                        && conn.IsConnected
                        && actionFailureComponentId != 0)
                    {
                        actionFailureQueued = SendUseTargetActionFailure(
                            conn,
                            actionFailureComponentId,
                            actionFailureResponseId,
                            "ProcessMonsterKill-target-dead",
                            conn.ActiveUseTargetActionSequence);
                    }
                    if (actionFailureQueued || pendingActionFailureReplacement)
                        conn.ActiveUseTargetActionFailureQueued = true;
                    Debug.LogError($"[PLAYER-ACTION-FAILURE] action=UseTarget conn={conn.ConnId} target={monster.EntityId} component={actionFailureComponentId} responseId={actionFailureResponseId} queued={actionFailureQueued} pendingResponseRewrite={pendingActionFailureReplacement} sourceFunction=ProcessMonsterKill->Behavior::processUpdate@0x00515620 opcode=0x03");
                    conn.ActiveUseTargetRemoved = true;
                    Combat.WeaponUseRuntime.Instance.CancelConnectionUseTargetIntent(conn.ConnId.ToString(), "ProcessMonsterKill");
                    if (conn.Avatar != null)
                        Combat.CombatRuntime.Instance.SetPlayerActiveClientAttack((uint)conn.Avatar.Id, false);
                }
                else
                    ClearUseTargetAndReleaseControl(conn, "ProcessMonsterKill", sendClientControlReset: true, requireActiveUseTargetForReset: true);
            }
            else if (ownerRewardEligible)
                Debug.LogError($"[CONTROL] Skip ProcessMonsterKill reset target={monster.EntityId} targetingKilledMonster={targetingKilledMonster} killedTargetWeaponBusy={killedTargetWeaponBusy}");

            if (monster.GCType != null && (
                monster.GCType.Equals("creatures.whiskers.broodling.basic.champion", StringComparison.OrdinalIgnoreCase) ||
                monster.GCType.Equals("world.dungeon00.mob.boss", StringComparison.OrdinalIgnoreCase)))
            {
                Debug.LogError($"[BOSS]  RATTLE TOOTH KILLED! Opening boss gate ");

                if (WorldEntitySpawner.Instance.FindEntityByName("BossGate", monster.ZoneName, out ushort gateId, out var gateData))
                {
                    var gateWriter = new LEWriter();
                    gateWriter.WriteByte(0x03);
                    gateWriter.WriteUInt16(gateId);
                    gateWriter.WriteByte(0x0A);
                    gateWriter.WriteByte(0x00);
                    byte[] gatePacket = gateWriter.ToArray();

                    Debug.LogError($"[BOSS] Gate entity {gateId} (0x{gateId:X4}), monster.ZoneName={monster.ZoneName}, monster.InstanceKey={monsterInstanceKey}");

                    int sentCount = 0;
                    var bossInstanceConnections = new System.Collections.Generic.List<RRConnection>();
                    foreach (var zoneConn in GetConnectionInsertionOrderSnapshot())
                    {
                        if (zoneConn == null || !zoneConn.IsConnected || !zoneConn.IsSpawned) continue;
                        if (string.IsNullOrWhiteSpace(monster.InstanceKey) || string.IsNullOrWhiteSpace(zoneConn.RuntimeInstanceKey)) continue;
                        if (!string.Equals(RoomRuntime.NormalizeInstanceKey(zoneConn.RuntimeInstanceKey), monsterInstanceKey, StringComparison.OrdinalIgnoreCase)) continue;
                        bossInstanceConnections.Add(zoneConn);
                        zoneConn.MessageQueue.Enqueue(gatePacket);
                        sentCount++;
                    }

                    Debug.LogError($"[BOSS] Sent gate open (0x03/0x0A flags=0) for entity {gateId} to {sentCount} players");

                    const string bossGateMessage = "The boss gate is open!";
                    var _bossMsgSeen = new System.Collections.Generic.HashSet<int>();
                    int bossMessageSent = 0;

                    foreach (RRConnection bossConn in bossInstanceConnections)
                    {
                        if (!_bossMsgSeen.Add(bossConn.ConnId)) continue;
                        SendSystemMessage(bossConn, bossGateMessage);
                        bossMessageSent++;
                    }

                    Debug.LogError($"[BOSS] Boss-gate message '{bossGateMessage}' sent to {bossMessageSent} player(s) (zone+instance+group dedup'd)");

                    try
                    {
                        int _popupSent = 0;
                        var _popupSeen = new System.Collections.Generic.HashSet<int>();
                        foreach (RRConnection popupConn in bossInstanceConnections)
                        {
                            if (popupConn.QuestManagerId == 0) continue;
                            if (!_popupSeen.Add(popupConn.ConnId)) continue;
                            SendBossGatePopup(popupConn);
                            _popupSent++;
                        }
                        Debug.LogError($"[BOSS-POPUP] Triggered popup sequence for {_popupSent} player(s)");
                    }
                    catch (Exception _popupEx)
                    {
                        Debug.LogError($"[BOSS-POPUP] FAILED (non-fatal): {_popupEx.Message}");
                    }
                }
                else
                {
                    Debug.LogError($"[BOSS] bossGate missing zone={monster.ZoneName}");
                }
            }

            if (!ownerRewardEligible)
            {
                Debug.LogError($"[KILL-OWNER-REWARDS] state=skipped monster={monster.Name}#{monster.EntityId} reason={ownerRewardReason} monsterInstance='{monsterInstanceKey}' ownerInstance='{ownerInstanceKey}' conn={(conn != null ? conn.ConnId.ToString() : "null")}");
                Debug.LogError($"[KILL] ");
                return;
            }

            var candidateGcTypes = new System.Collections.Generic.List<string>();
            if (monster.AuthoredArchetypeAncestry != null)
            {
                foreach (string ancestryPath in monster.AuthoredArchetypeAncestry)
                {
                    if (!string.IsNullOrWhiteSpace(ancestryPath) &&
                        !candidateGcTypes.Contains(ancestryPath, StringComparer.OrdinalIgnoreCase))
                        candidateGcTypes.Add(ancestryPath);
                }
            }
            if (!string.IsNullOrEmpty(monster.SpawnGCType))
                if (!candidateGcTypes.Contains(monster.SpawnGCType, StringComparer.OrdinalIgnoreCase))
                    candidateGcTypes.Add(monster.SpawnGCType);
            if (!string.IsNullOrEmpty(monster.GCType))
                if (!candidateGcTypes.Contains(monster.GCType, StringComparer.OrdinalIgnoreCase))
                    candidateGcTypes.Add(monster.GCType);
            QuestProgressMutation killQuestMutation = QuestManager.Instance.StageCreatureKill(conn, candidateGcTypes);
            if (killQuestMutation.Updates.Count > 0)
            {
                if (!SavePlayerQuests(conn))
                {
                    QuestManager.Instance.RollbackProgress(killQuestMutation);
                    Debug.LogError($"[KILL] Quest objectives state=rollback count={killQuestMutation.Updates.Count} reason=save-failed");
                }
                else
                {
                    QuestManager.Instance.CommitProgress(conn, killQuestMutation);
                    Debug.LogError($"[KILL] Quest objectives state=committed count={killQuestMutation.Updates.Count}");
                }
            }

            try
            {
                List<RRConnection> lootRecipients = ResolvePartyLootRecipients(conn, monster, out Group lootGroup);
                List<LootGenerationProfile> lootProfiles = BuildLootGenerationProfiles(lootRecipients);
                List<LootDrop> drops = new List<LootDrop>();
                int recipientAssignments = 0;
                if (lootProfiles.Count > 0)
                {
                    int startRecipientIndex = lootGroup == null
                        ? 0
                        : (int)(lootGroup.LootRecipientCursor % (ulong)lootProfiles.Count);
                    if (WorldObjectSpawner.IsDestroyableObject(monster.GCType))
                        drops = GCObjectGeneratorTable.Instance.GenerateDestroyableLoot(monster, lootProfiles, startRecipientIndex, out recipientAssignments);
                    else
                        drops = GCObjectGeneratorTable.Instance.GenerateMobLoot(monster, lootProfiles, startRecipientIndex, out recipientAssignments);
                    if (lootGroup != null && recipientAssignments > 0)
                        lootGroup.LootRecipientCursor += (ulong)recipientAssignments;
                    ApplyServerReconstructionLootScaling(drops, monster);
                    UpgradePartyLootPotions(drops, lootRecipients);
                }
                Debug.LogError($"[SERVER-RECONSTRUCTION] area=party-loot monster={monster.Name}#{monster.EntityId} group={(lootGroup == null ? 0u : lootGroup.GroupId)} eligible={lootRecipients.Count} startCursor={(lootGroup == null ? 0UL : lootGroup.LootRecipientCursor - (ulong)Math.Max(0, recipientAssignments))} assignments={recipientAssignments} drops={drops.Count} goldPercent={monster.ServerReconstructionGoldPercent} itemLevelBonus={monster.ServerReconstructionItemLevelBonus} policy=conservative-v1");

                foreach (var drop in drops)
                {
                    RRConnection dropOwner = ResolveLootDropRecipient(drop, lootRecipients);
                    if (dropOwner == null)
                    {
                        Debug.LogError($"[LOOT] state=skipped reason=recipient-unresolved characterId={drop?.RecipientCharacterId ?? 0} monster={monster.Name}#{monster.EntityId}");
                        continue;
                    }
                    int recipientLevel = Math.Clamp(drop.RecipientPlayerLevel, 1, 110);
                    if (drop.IsGold)
                    {
                        uint lootGold = (uint)drop.GoldAmount;
                        Debug.LogError($"[LOOT] +{lootGold} gold pile from {monster.Name} owner={dropOwner.LoginName}");
                        try
                        {
                            string goldOwner = $"mob-gold:{monster.Name}#{monster.EntityId}";
                            var goldPlacement = ResolveItemDropPlacement(
                                conn,
                                conn.CurrentZoneName,
                                conn.InstanceId,
                                monster.PosFixedX,
                                monster.PosFixedY,
                                monster.PosFixedZ,
                                monster.HeadingFixed,
                                goldOwner);
                            if (!goldPlacement.Success)
                            {
                                Debug.LogError($"[LOOT] gold pile blocked reason=missing-pathmap monster={monster.Name}#{monster.EntityId}");
                                continue;
                            }
                            int goldHeadingFixed = ConsumeItemAddToWorldHeading(goldOwner);
                            ushort goldEntityId = GetNextLootEntityId();
                            var goldItem = new GCObject
                            {
                                GCClass = "Currency",
                                DFCClass = "Currency",
                                StoredLevel = recipientLevel
                            };
                            if (!TrackDroppedItem(
                                    goldEntityId,
                                    goldItem,
                                    dropOwner,
                                    1,
                                    goldPlacement.FixedX,
                                    goldPlacement.FixedY,
                                    goldPlacement.FixedZ,
                                    recipientLevel,
                                    goldHeadingFixed,
                                    requirePublicPersistence: true,
                                    goldAmount: lootGold))
                            {
                                Debug.LogError($"[LOOT] gold pile blocked reason=persistence monster={monster.Name}#{monster.EntityId}");
                                continue;
                            }
                            DroppedItemInfo goldInfo = _droppedItems[goldEntityId];
                            BroadcastGoldPileSpawnPacket(dropOwner, goldEntityId, goldInfo);
                        }
                        catch (Exception goldLootEx)
                        {
                            Debug.LogError($"[LOOT]  gold pile spawn failed: {goldLootEx.Message}");
                        }
                    }
                    else if (drop.IsKingsCoin)
                    {
                        try
                        {
                            int kcCount = drop.KingsCoinCount > 0 ? drop.KingsCoinCount : 1;
                            for (int kcIdx = 0; kcIdx < kcCount; kcIdx++)
                            {
                                var kcItem = new GCObject
                                {
                                    GCClass = "QuestItemPAL.Token",
                                    DFCClass = "Item",
                                    PresetScaleMod = "ScaleModPAL.Binder.Mod1",
                                    StoredRarity = (int)ItemRarity.Normal,
                                    StoredLevel = recipientLevel
                                };
                                string kingsCoinOwner = $"mob-kingscoin:{monster.Name}#{monster.EntityId}:{kcIdx}";
                                var kcPlacement = ResolveItemDropPlacement(
                                    conn,
                                    conn.CurrentZoneName,
                                    conn.InstanceId,
                                    monster.PosFixedX,
                                    monster.PosFixedY,
                                    monster.PosFixedZ,
                                    monster.HeadingFixed,
                                    kingsCoinOwner);
                                if (!kcPlacement.Success)
                                {
                                    Debug.LogError($"[LOOT-KC] drop blocked reason=missing-pathmap monster={monster.Name}#{monster.EntityId} index={kcIdx}");
                                    continue;
                                }
                                int kingsCoinHeadingFixed = ConsumeItemAddToWorldHeading(kingsCoinOwner);
                                ushort kcEntityId = GetNextLootEntityId();
                                if (!TrackDroppedItem(kcEntityId, kcItem, dropOwner, 1, kcPlacement.FixedX, kcPlacement.FixedY, kcPlacement.FixedZ, recipientLevel, kingsCoinHeadingFixed, requirePublicPersistence: true))
                                {
                                    Debug.LogError($"[LOOT-KC] drop blocked reason=persistence monster={monster.Name}#{monster.EntityId} index={kcIdx}");
                                    continue;
                                }
                                BroadcastDroppedItemSpawnPacket(dropOwner, kcEntityId, _droppedItems[kcEntityId]);
                            }
                            Debug.LogError($"[LOOT-KC] dropped {kcCount} Kings Coin(s) on ground from {monster.Name}");
                        }
                        catch (Exception kcEx)
                        {
                            Debug.LogError($"[LOOT-KC] ground drop failed (non-fatal): {kcEx.Message}");
                        }
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(drop.GCType)) { Debug.LogError($"[LOOT]  Skipping null GCType from {monster.Name}"); continue; }
                        string _detectedClass = ResolveAuthoredItemClass(drop.GCType);

                        var item = new GCObject
                        {
                            GCClass = drop.GCType,
                            DFCClass = _detectedClass,
                            PresetScaleMod = drop.ScaleMod,
                            StoredRarity = (int)drop.Rarity,
                            StoredLevel = drop.ItemLevel,
                            HasGeneratedItemState = drop.HasGeneratedItemState,
                            RolledRequiresMembership = drop.RolledRequiresMembership,
                            GeneratedRequiresMembership = drop.RequiresMembership,
                            GeneratedItemModifiers = new List<string>(drop.ItemModifiers)
                        };
                        string itemOwner = $"mob-item:{monster.Name}#{monster.EntityId}:{drop.GCType}";
                        var itemPlacement = ResolveItemDropPlacement(
                            conn,
                            conn.CurrentZoneName,
                            conn.InstanceId,
                            monster.PosFixedX,
                            monster.PosFixedY,
                            monster.PosFixedZ,
                            monster.HeadingFixed,
                            itemOwner);
                        if (!itemPlacement.Success)
                        {
                            Debug.LogError($"[LOOT] item={drop.GCType} blocked reason=missing-pathmap monster={monster.Name}#{monster.EntityId}");
                            continue;
                        }

                        int itemHeadingFixed = ConsumeItemAddToWorldHeading(itemOwner);
                        ushort lootEntityId = GetNextLootEntityId();
                        if (!TrackDroppedItem(lootEntityId, item, dropOwner, 1, itemPlacement.FixedX, itemPlacement.FixedY, itemPlacement.FixedZ, recipientLevel, itemHeadingFixed, requirePublicPersistence: true))
                        {
                            Debug.LogError($"[LOOT] item={drop.GCType} blocked reason=persistence monster={monster.Name}#{monster.EntityId}");
                            continue;
                        }

                        BroadcastDroppedItemSpawnPacket(dropOwner, lootEntityId, _droppedItems[lootEntityId]);
                        Debug.LogError($"[LOOT]  {drop.Label} ({drop.Rarity}) owner={dropOwner.LoginName} at fixed ({itemPlacement.FixedX},{itemPlacement.FixedY},{itemPlacement.FixedZ})");
                    }
                }

                if (drops.Count > 0)
                    Debug.LogError($"[LOOT] {monster.Name}: {drops.Count} drops");
            }
            catch (Exception lootEx)
            {
                Debug.LogError($"[LOOT] state=failed message='{lootEx.Message}' stack='{lootEx.StackTrace}'");
            }

            try
            {
                var questState = QuestManager.Instance.GetPlayerState(conn.ConnId.ToString());
                if (questState != null && questState.ActiveQuests != null && questState.ActiveQuests.Count > 0)
                {
                    int questPlayerLevel = playerState?.Level ?? 1;
                    var questDropCandidates = new System.Collections.Generic.List<string>();
                    if (monster.AuthoredArchetypeAncestry != null)
                    {
                        foreach (string ancestryPath in monster.AuthoredArchetypeAncestry)
                        {
                            if (!string.IsNullOrWhiteSpace(ancestryPath) &&
                                !questDropCandidates.Contains(ancestryPath, StringComparer.OrdinalIgnoreCase))
                                questDropCandidates.Add(ancestryPath);
                        }
                    }
                    if (!string.IsNullOrEmpty(monster.SpawnGCType))
                        if (!questDropCandidates.Contains(monster.SpawnGCType, StringComparer.OrdinalIgnoreCase))
                            questDropCandidates.Add(monster.SpawnGCType);
                    if (!string.IsNullOrEmpty(monster.GCType))
                        if (!questDropCandidates.Contains(monster.GCType, StringComparer.OrdinalIgnoreCase))
                            questDropCandidates.Add(monster.GCType);

                    var activeQuestsById = new System.Collections.Generic.Dictionary<string, ActiveQuest>(StringComparer.OrdinalIgnoreCase);
                    foreach (var activeQuest in questState.ActiveQuests)
                        if (!string.IsNullOrEmpty(activeQuest.QuestId) && !activeQuestsById.ContainsKey(activeQuest.QuestId))
                            activeQuestsById.Add(activeQuest.QuestId, activeQuest);

                    var processedRules = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    int questDropRolls = 0, questDropHits = 0;
                    foreach (var candidateGcType in questDropCandidates)
                    {
                        if (string.IsNullOrEmpty(candidateGcType)) continue;
                        if (!AuthoredGameplayCatalog.QuestKillDropsByMonster.TryGetValue(candidateGcType, out var rules)) continue;

                        foreach (var rule in rules)
                        {
                            if (!activeQuestsById.TryGetValue(rule.QuestId, out ActiveQuest activeQuest)) continue;
                            if (!processedRules.Add(rule.SourcePath)) continue;
                            if (rule.ObjectiveIndex < 0
                                || activeQuest.Objectives == null
                                || rule.ObjectiveIndex >= activeQuest.Objectives.Count
                                || activeQuest.Objectives[rule.ObjectiveIndex].IsComplete)
                            {
                                if (QuestLootTracking)
                                    Debug.LogError($"[QUEST-LOOT-TRACK] source=kill-drop phase=skipped reason=objective-complete-or-unbound characterId={GetCharSqlId(conn)} quest='{rule.QuestId}' objective={rule.ObjectiveIndex} rule='{rule.SourcePath}' monsterEntity={monster.EntityId} instance='{RoomRuntime.NormalizeInstanceKey(GetInstanceZoneKey(conn))}'");
                                continue;
                            }
                            if (questPlayerLevel < rule.MinLevel || questPlayerLevel > rule.MaxLevel) continue;

                            questDropRolls++;
                            int killDropRoll = RandomNumberGenerator.GetInt32(100);
                            RngLedger.LogSystemRandom(
                                "KillDropTrigger::server-chance",
                                killDropRoll,
                                $"quest={rule.QuestId}:rule={rule.SourcePath}:monster={monster.Name}#{monster.EntityId}");
                            Debug.LogError($"[KILLDROP-SERVER] quest={rule.QuestId} rule={rule.SourcePath} monster={monster.Name}#{monster.EntityId} chancePct={rule.Chance} roll={killDropRoll} count={rule.ChanceCount} levelRange={rule.MinLevel}-{rule.MaxLevel} stream=serverNon evidence=BLOCKED_EVIDENCE source=server-side-reconstruction clientHook=KillDropTrigger::doEvent@0x005CACB0");
                            if (QuestLootTracking)
                                Debug.LogError($"[QUEST-LOOT-TRACK] source=kill-drop phase=roll characterId={GetCharSqlId(conn)} quest='{rule.QuestId}' objective={rule.ObjectiveIndex} rule='{rule.SourcePath}' monsterEntity={monster.EntityId} roll={killDropRoll} chance={rule.Chance} count={rule.ChanceCount} instance='{RoomRuntime.NormalizeInstanceKey(GetInstanceZoneKey(conn))}'");
                            if (killDropRoll >= rule.Chance) continue;

                            if (!string.IsNullOrWhiteSpace(rule.TreasureGenerator))
                            {
                                List<LootDrop> generatedDrops = GCObjectGeneratorTable.Instance.GenerateAuthoredGeneratorLoot(
                                    rule.TreasureGenerator,
                                    rule.ChanceCount,
                                    questPlayerLevel,
                                    !IsPlayerFree(conn.LoginName),
                                    $"quest-killdrop:{rule.SourcePath}",
                                    conn.AvatarGcType);
                                if (generatedDrops.Count == 0)
                                    Debug.LogError($"[QUEST-DROP] result=no-generated-item generator={rule.TreasureGenerator} player={conn.LoginName} quest={rule.QuestId} rule={rule.SourcePath}");
                                int generatedIndex = 0;
                                foreach (LootDrop generatedDrop in generatedDrops)
                                {
                                    if (generatedDrop == null || !generatedDrop.IsItem || string.IsNullOrWhiteSpace(generatedDrop.GCType))
                                        throw new InvalidDataException($"Quest kill drop generator '{rule.TreasureGenerator}' emitted a non-item result for '{rule.SourcePath}'");
                                    var generatedItem = new GCObject
                                    {
                                        GCClass = generatedDrop.GCType,
                                        DFCClass = ResolveAuthoredItemClass(generatedDrop.GCType),
                                        PresetScaleMod = generatedDrop.ScaleMod,
                                        StoredRarity = (int)generatedDrop.Rarity,
                                        StoredLevel = generatedDrop.ItemLevel,
                                        HasGeneratedItemState = generatedDrop.HasGeneratedItemState,
                                        RolledRequiresMembership = generatedDrop.RolledRequiresMembership,
                                        GeneratedRequiresMembership = generatedDrop.RequiresMembership,
                                        GeneratedItemModifiers = new List<string>(generatedDrop.ItemModifiers)
                                    };
                                    if (SpawnQuestKillDropItem(conn, monster, rule, generatedItem, Math.Max(1, generatedDrop.ItemLevel), generatedIndex))
                                        questDropHits++;
                                    generatedIndex++;
                                }
                                continue;
                            }

                            for (int itemIndex = 0; itemIndex < rule.ChanceCount; itemIndex++)
                            {
                                var questDropItem = new GCObject
                                {
                                    GCClass = rule.ItemGcType,
                                    DFCClass = ResolveAuthoredItemClass(rule.ItemGcType),
                                    StoredLevel = 1
                                };
                                if (SpawnQuestKillDropItem(conn, monster, rule, questDropItem, 1, itemIndex))
                                    questDropHits++;
                            }
                        }
                    }
                    if (questDropRolls > 0)
                        Debug.LogError($"[QUEST-DROP] monster='{monster.Name}' rolled={questDropRolls} credited={questDropHits}");
                }
            }
            catch (Exception questDropEx)
            {
                Debug.LogError($"[QUEST-DROP] state=failed message='{questDropEx.Message}' stack='{questDropEx.StackTrace}'");
            }

            Debug.LogError($"[KILL] ");
        }

        private bool SpawnQuestKillDropItem(RRConnection conn, Monster monster, QuestKillDropEntry rule, GCObject item, int itemLevel, int itemIndex)
        {
            string owner = $"killdrop-item:{monster.Name}#{monster.EntityId}:{rule.SourcePath}:{itemIndex}";
            var placement = ResolveItemDropPlacement(
                conn,
                conn.CurrentZoneName,
                conn.InstanceId,
                monster.PosFixedX,
                monster.PosFixedY,
                monster.PosFixedZ,
                monster.HeadingFixed,
                owner);
            if (!placement.Success)
            {
                Debug.LogError($"[QUEST-DROP] item={item.GCClass} blocked reason=missing-pathmap monster={monster.Name}#{monster.EntityId} quest={rule.QuestId} rule={rule.SourcePath} index={itemIndex}");
                return false;
            }
            int headingFixed = ConsumeItemAddToWorldHeading(owner);
            ushort entityId = GetNextLootEntityId();
            if (!TrackDroppedItem(entityId, item, conn, 1, placement.FixedX, placement.FixedY, placement.FixedZ, itemLevel, headingFixed, requirePublicPersistence: true))
            {
                Debug.LogError($"[QUEST-DROP] item={item.GCClass} blocked reason=persistence monster={monster.Name}#{monster.EntityId} quest={rule.QuestId} rule={rule.SourcePath} index={itemIndex}");
                return false;
            }
            if (!_droppedItems.TryGetValue(entityId, out var info))
                throw new InvalidDataException($"Quest kill drop entity {entityId} was not tracked for '{rule.SourcePath}'");
            info.IsQuestItem = string.IsNullOrWhiteSpace(rule.TreasureGenerator);
            BroadcastDroppedItemSpawnPacket(conn, entityId, info);
            Debug.LogError($"[QUEST-DROP] item={item.GCClass} player={conn.LoginName} quest={rule.QuestId} rule={rule.SourcePath} chance={rule.Chance}% count={rule.ChanceCount} index={itemIndex} generated={!string.IsNullOrWhiteSpace(rule.TreasureGenerator)}");
            if (QuestLootTracking)
                Debug.LogError($"[QUEST-LOOT-TRACK] source=kill-drop phase=spawn characterId={GetCharSqlId(conn)} quest='{rule.QuestId}' objective={rule.ObjectiveIndex} rule='{rule.SourcePath}' item='{item.GCClass}' quantity=1 ownerCharacter={info.OwnerCharacterId} dropEntity={entityId} instance='{RoomRuntime.NormalizeInstanceKey(GetInstanceZoneKey(conn))}'");
            return true;
        }

        private void SendHeroAddExperienceUpdate(RRConnection conn, uint baseXP, uint sourceLevel)
        {
            uint experienceF32 = checked((uint)(((ulong)baseXP << 8) / 100u));
            SendHeroAddExperienceUpdateF32(conn, experienceF32, sourceLevel);
            Debug.LogError($"[XP-PACKET] baseXP={baseXP} experienceF32={experienceF32} sourceLevel={sourceLevel}");
        }

        private void SendHeroAddExperienceUpdateF32(RRConnection conn, uint experienceF32, uint sourceLevel)
        {
            uint avatarId = conn?.Avatar != null ? (uint)Math.Max(0, conn.Avatar.Id) : 0u;
            if (avatarId == 0) return;
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x03);
            writer.WriteUInt16((ushort)avatarId);
            writer.WriteByte(0x0F);
            writer.WriteUInt32(experienceF32);
            writer.WriteByte((byte)sourceLevel);
            WritePlayerEntitySynch(conn, writer);
            writer.WriteByte(0x06);

            QueueClientEntityStream(conn, writer.ToArray());
            Debug.LogError($"[XP-PACKET] Queued 0x03 in client entity stream: experienceF32={experienceF32} sourceLevel={sourceLevel}");
        }

        public void SendAdminXPUpdate(RRConnection conn, uint baseXP, uint sourceLevel)
        {
            SendHeroAddExperienceUpdate(conn, baseXP, sourceLevel);
        }

        public bool SendFreePlayerModifier(RRConnection conn)
        {
            if (conn.ModifiersId == 0)
            {
                Debug.LogError("[XP-MOD] Cannot send FreePlayerModifier - ModifiersId not set");
                return false;
            }

            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x35);
            writer.WriteUInt16((ushort)conn.ModifiersId);
            writer.WriteByte(0x00);
            WriteGCType(writer, "avatar.base.FreePlayerExperienceModifier", preserveCase: true);
            writer.WriteUInt32(1);
            writer.WriteByte(0);
            writer.WriteUInt32(0);
            writer.WriteUInt32(0x00000000);
            writer.WriteByte(0x01);
            WritePlayerEntitySynch(conn, writer);
            writer.WriteByte(0x06);

            bool queued = QueueClientEntityStream(conn, writer.ToArray());
            Debug.LogError($"[XP-MOD] FreePlayerModifier queued={queued} modifiersId={conn.ModifiersId} sourceFunction=Modifiers::processAddModifier@0x00502280");
            return queued;
        }

        private void SendTrackedModifier(RRConnection conn, ActiveModifier mod)
        {
            if (conn.ModifiersId == 0)
            {
                Debug.LogError($"[MOD-RESEND] Cannot send '{mod.GCType}' - ModifiersId not set");
                return;
            }

            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x35);
            writer.WriteUInt16((ushort)conn.ModifiersId);
            writer.WriteByte(0x00);
            WriteGCType(writer, mod.GCType, preserveCase: true);
            writer.WriteUInt32(mod.Id);
            writer.WriteByte(mod.Level);
            writer.WriteUInt32(mod.PowerLevel);
            writer.WriteUInt32(mod.Duration);
            writer.WriteByte(mod.SourceIsSelf);
            WritePlayerEntitySynch(conn, writer);
            writer.WriteByte(0x06);

            SendCompressedA(conn, 0x01, 0x0F, writer.ToArray());
            Debug.LogError($"[MOD-RESEND] Sent '{mod.GCType}' id={mod.Id} durationTicks={mod.Duration} tickRate={SIMULATION_TICKS_PER_SECOND} to {conn.LoginName}");
        }

        public void RecordModifierSent(string loginName, string gcType, uint modId,
            byte level = 0, uint powerLevel = 0, uint duration = 0, byte sourceIsSelf = 0)
        {
            _activeModifiers.AddOrReplace(loginName, new ActiveModifier
            {
                GCType = gcType,
                Id = modId,
                Level = level,
                PowerLevel = powerLevel,
                Duration = duration,
                SourceIsSelf = sourceIsSelf,
                AddedSimulationTick = _combatTick
            });
        }

        public void UntrackModifier(string loginName, string gcType)
        {
            _activeModifiers.Remove(loginName, gcType);
        }

        private void ResendAllModifiers(RRConnection conn)
        {
            if (conn.LoginName == null || conn.ModifiersId == 0) return;
            PlayerState playerState = GetPlayerState(conn.ConnId.ToString());
            if (playerState != null)
                _activeModifiers.ReconcileRuntimeDurations(conn.LoginName, playerState.GetAttributeModifierSnapshots(), _combatTick);
            var mods = _activeModifiers.ListFor(conn.LoginName, _combatTick);
            if (mods.Count == 0) return;

            Debug.LogError($"[MOD-RESEND] Re-sending {mods.Count} modifiers for {conn.LoginName} after zone transition");
            foreach (var mod in mods)
            {
                SendTrackedModifier(conn, mod);
            }
        }

        private List<(string modGcType, uint durationTicks)> ResolveMonsterDebuffs(Combat.Monster monster)
        {
            var result = new List<(string, uint)>();
            if (monster?.Manipulators == null) return result;

            foreach (var manipulatorEntry in monster.ManipulatorOrder)
            {
                string gcType = !string.IsNullOrEmpty(manipulatorEntry.Value?.gcType) ? manipulatorEntry.Value.gcType : manipulatorEntry.Key;
                if (string.IsNullOrEmpty(gcType)) continue;

                string shortName = gcType.ToLowerInvariant();
                int dot = shortName.LastIndexOf('.');
                if (dot >= 0) shortName = shortName.Substring(dot + 1);

                string debuffKey = null;
                if (_weaponDebuffMap.TryGetValue(shortName, out var wk)) debuffKey = wk;
                else if (_creatureDebuffMap.ContainsKey(shortName)) debuffKey = shortName;

                if (debuffKey == null) continue;
                if (!_creatureDebuffMap.TryGetValue(debuffKey, out var debuffInfo)) continue;

                uint ticks = debuffInfo.durationSeconds <= 0 ? 0
                    : (uint)Math.Min(uint.MaxValue, ((long)debuffInfo.durationSeconds * SIMULATION_TICKS_PER_SECOND) + 1L);
                result.Add((debuffInfo.modGcType, ticks));
            }
            return result;
        }

        private void ApplyMonsterDebuffs(RRConnection conn, Combat.Monster monster)
        {
            if (conn.ModifiersId == 0 || conn.LoginName == null) return;

            var debuffs = ResolveMonsterDebuffs(monster);
            if (debuffs.Count == 0) return;
            Debug.LogError($"[MON-DEBUFF] skipped state-message debuff lane player={conn.LoginName} monster={monster?.Name ?? "unknown"}#{monster?.EntityId ?? 0} count={debuffs.Count} sourceFunction=ActiveSkill::doSkillEffect@0x00539630/SpellModEffect::doEffect@0x00554460 reason=not-owned-by-StateMachine-message");
        }


        private void SendHeroRemoveExperienceUpdate(RRConnection conn, uint xpAmount)
        {
            uint avatarId = conn?.Avatar != null ? (uint)Math.Max(0, conn.Avatar.Id) : 0u;
            if (avatarId == 0) return;

            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x03);
            writer.WriteUInt16((ushort)avatarId);
            writer.WriteByte(0x10);
            writer.WriteUInt32(xpAmount);
            WritePlayerEntitySynch(conn, writer);
            writer.WriteByte(0x06);

            SendCompressedA(conn, 0x01, 0x0F, writer.ToArray());
            Debug.LogError($"[XP-PACKET] Sent RemoveExperience: {xpAmount}");
        }

        public void SendAdminEntitySynchInfoHP(RRConnection conn, PlayerState playerState)
        {
            if (conn.UnitBehaviorId == 0) return;

            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x35);
            writer.WriteUInt16((ushort)conn.UnitBehaviorId);
            writer.WriteByte(0x65);
            writer.WriteByte(conn.SessionID++);
            writer.WriteByte(0x01);
            writer.WriteByte(0x03);
            writer.WriteInt32(conn.PlayerHeadingFixed);
            writer.WriteInt32(conn.PlayerPosFixedX);
            writer.WriteInt32(conn.PlayerPosFixedY);
            WritePlayerEntitySynch(conn, writer);
            writer.WriteByte(0x06);
            QueueClientEntityStream(conn, writer.ToArray());
            Debug.LogError($"[ADMIN-HP] Queued EntitySynchInfo HP via UnitBehavior: {playerState.CurrentHPWire / 256}/{playerState.MaxHPWire / 256}");
        }

        private void HandleAdminLevelUp(RRConnection conn, PlayerState playerState, int oldLevel, int newLevel)
        {
            if (!IsPlayerAdmin(conn.LoginName))
            {
                SendSystemMessage(conn, "You do not have permission.");
                return;
            }

            if (conn.Avatar != null)
                CalculateEquipmentBonuses(conn.ConnId.ToString(), conn.Avatar);
            playerState.RestoreToFull();

            for (int lv = oldLevel; lv < newLevel; lv++)
            {
                uint threshold = PlayerState.GetClientThreshold(lv + 1);
                uint packetXP = threshold * 256 / 5 + 100;
                SendAdminXPUpdate(conn, packetXP, (uint)lv);
                Debug.LogError($"[ADMIN-LEVELUP] Level {lv}->{lv + 1}: threshold={threshold} packetXP={packetXP}");
            }

            SendAdminEntitySynchInfoHP(conn, playerState);
            Debug.LogError($"[ADMIN-LEVELUP] Sent {newLevel - oldLevel} XP packet(s) + EntitySynchInfo HP for level {oldLevel}->{newLevel}");
            try { if (conn.CharSqlId != 0) PosseRuntime.Instance.NotifyMemberStateChange(conn.CharSqlId, this); }
            catch (Exception posseException) { Debug.LogError($"[POSSE] level-up notify failed: {posseException.Message}"); }
        }

    }
}
