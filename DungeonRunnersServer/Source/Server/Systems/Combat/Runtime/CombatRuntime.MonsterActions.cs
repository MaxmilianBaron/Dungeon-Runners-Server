using System;
using System.Collections.Generic;
using DungeonRunners.Engine;
using DungeonRunners.Networking;
using DungeonRunners.Networking.EntitySynchInfo;
using DungeonRunners.Core;
using DungeonRunners.Data;
using DungeonRunners.Combat.Behavior;
using System.Linq;
using System.IO;
using System.Text;
namespace DungeonRunners.Combat
{
public partial class CombatRuntime
    {        private static int ResolveMonsterActiveSkillMinimumRangeF32(MonsterActiveSkillRuntime skill)
        {
            return skill != null ? skill.SpellUseMinimumRangeF32 : -1;
        }

        private List<(CombatTarget Player, long DistanceSquaredFixed8)> CollectMonsterActiveSkillEnemyCandidates(Monster monster, MonsterActiveSkillRuntime skill)
        {
            var candidates = new List<(CombatTarget Player, long DistanceSquaredFixed8)>();
            if (monster == null || skill == null)
                return candidates;
            int scanRangeF32 = Math.Max(0, skill.SpellUseRangeF32);
            long scanRangeSquaredFixed8 = ((long)scanRangeF32 * scanRangeF32) >> 8;
            int minimumRangeF32 = ResolveMonsterActiveSkillMinimumRangeF32(skill);
            long minimumRangeSquaredFixed8 = minimumRangeF32 > 0
                ? ((long)minimumRangeF32 * minimumRangeF32) >> 8
                : 0;
            foreach (CombatTarget player in GetCombatTargetsInEntityOrder())
            {
                if (player == null || !player.IsAlive || !player.HasUnitState)
                    continue;
                if (!MatchesInstance(monster, player.InstanceKey) || !IsMonsterEnemyOfTarget(monster, player))
                    continue;
                if (!PassesMonsterActiveSkillHealthPct(player.CurrentHPWire, player.MaxHPWire, true, skill.TargetHealthPctF32))
                    continue;
                TryGetMonsterClientVisiblePositionFixed(monster, player.EntityId, out int monsterFixedX, out int monsterFixedY);
                long dx = (long)player.ClientSimulationPosFixedX - monsterFixedX;
                long dy = (long)player.ClientSimulationPosFixedY - monsterFixedY;
                long dz = (long)player.ClientSimulationPosFixedZ - monster.PosFixedZ;
                long distanceSquaredFixed8 = ((dx * dx) >> 8) + ((dy * dy) >> 8) + ((dz * dz) >> 8);
                if (distanceSquaredFixed8 > scanRangeSquaredFixed8)
                    continue;
                long distanceSquared2DFixed8 = ((dx * dx) >> 8) + ((dy * dy) >> 8);
                if (minimumRangeF32 > 0 && distanceSquared2DFixed8 < minimumRangeSquaredFixed8)
                    continue;
                candidates.Add((player, distanceSquaredFixed8));
            }
            candidates.Sort((left, right) =>
            {
                int distanceOrder = left.DistanceSquaredFixed8.CompareTo(right.DistanceSquaredFixed8);
                return distanceOrder != 0
                    ? distanceOrder
                    : left.Player.EntityId.CompareTo(right.Player.EntityId);
            });
            return candidates;
        }

        private List<(Monster Monster, long DistanceSquaredFixed8)> CollectMonsterActiveSkillFriendCandidates(Monster monster, MonsterActiveSkillRuntime skill)
        {
            var candidates = new List<(Monster Monster, long DistanceSquaredFixed8)>();
            if (monster == null || skill == null)
                return candidates;
            int scanRangeF32 = Math.Max(0, skill.SpellUseRangeF32);
            long scanRangeSquaredFixed8 = ((long)scanRangeF32 * scanRangeF32) >> 8;
            int minimumRangeF32 = ResolveMonsterActiveSkillMinimumRangeF32(skill);
            long minimumRangeSquaredFixed8 = minimumRangeF32 > 0
                ? ((long)minimumRangeF32 * minimumRangeF32) >> 8
                : 0;
            foreach (Monster candidate in GetAllMonsters())
            {
                if (candidate == null
                    || candidate.EntityId == monster.EntityId
                    || !candidate.IsAlive
                    || candidate.CurrentHPWire == 0
                    || !MatchesInstance(monster, candidate.InstanceKey)
                    || !AreMonstersFriendsForAuthoredAoe(monster, candidate)
                    || !PassesMonsterActiveSkillHealthPct(candidate.CurrentHPWire, candidate.MaxHPWire, skill.HasTargetHealthPct, skill.TargetHealthPctF32))
                    continue;
                long dx = (long)candidate.PosFixedX - monster.PosFixedX;
                long dy = (long)candidate.PosFixedY - monster.PosFixedY;
                long dz = (long)candidate.PosFixedZ - monster.PosFixedZ;
                long distanceSquaredFixed8 = ((dx * dx) >> 8) + ((dy * dy) >> 8) + ((dz * dz) >> 8);
                if (distanceSquaredFixed8 > scanRangeSquaredFixed8)
                    continue;
                long distanceSquared2DFixed8 = ((dx * dx) >> 8) + ((dy * dy) >> 8);
                if (minimumRangeF32 > 0 && distanceSquared2DFixed8 < minimumRangeSquaredFixed8)
                    continue;
                candidates.Add((candidate, distanceSquaredFixed8));
            }
            candidates.Sort((left, right) =>
            {
                int distanceOrder = left.DistanceSquaredFixed8.CompareTo(right.DistanceSquaredFixed8);
                return distanceOrder != 0
                    ? distanceOrder
                    : left.Monster.EntityId.CompareTo(right.Monster.EntityId);
            });
            return candidates;
        }

        private List<(Monster Monster, long DistanceSquaredFixed8)> CollectMonsterActiveSkillFriendCorpseCandidates(Monster monster, MonsterActiveSkillRuntime skill)
        {
            var candidates = new List<(Monster Monster, long DistanceSquaredFixed8)>();
            if (monster == null || skill == null)
                return candidates;
            int scanRangeF32 = Math.Max(0, skill.SpellUseRangeF32);
            long scanRangeSquaredFixed8 = ((long)scanRangeF32 * scanRangeF32) >> 8;
            int minimumRangeF32 = ResolveMonsterActiveSkillMinimumRangeF32(skill);
            long minimumRangeSquaredFixed8 = minimumRangeF32 > 0
                ? ((long)minimumRangeF32 * minimumRangeF32) >> 8
                : 0;
            foreach (Monster candidate in GetAllMonsters())
            {
                if (!IsMonsterFriendCorpseTarget(monster, candidate)
                    || !PassesMonsterActiveSkillHealthPct(candidate.CurrentHPWire, candidate.MaxHPWire, skill.HasTargetHealthPct, skill.TargetHealthPctF32))
                    continue;
                long dx = (long)candidate.PosFixedX - monster.PosFixedX;
                long dy = (long)candidate.PosFixedY - monster.PosFixedY;
                long dz = (long)candidate.PosFixedZ - monster.PosFixedZ;
                long distanceSquaredFixed8 = ((dx * dx) >> 8) + ((dy * dy) >> 8) + ((dz * dz) >> 8);
                if (distanceSquaredFixed8 > scanRangeSquaredFixed8)
                    continue;
                long distanceSquared2DFixed8 = ((dx * dx) >> 8) + ((dy * dy) >> 8);
                if (minimumRangeF32 > 0 && distanceSquared2DFixed8 < minimumRangeSquaredFixed8)
                    continue;
                candidates.Add((candidate, distanceSquaredFixed8));
            }
            candidates.Sort((left, right) =>
            {
                int distanceOrder = left.DistanceSquaredFixed8.CompareTo(right.DistanceSquaredFixed8);
                return distanceOrder != 0
                    ? distanceOrder
                    : left.Monster.EntityId.CompareTo(right.Monster.EntityId);
            });
            return candidates;
        }

        private bool IsMonsterFriendCorpseTarget(Monster source, Monster target)
        {
            if (source == null
                || target == null
                || source.EntityId == target.EntityId
                || !source.IsAlive
                || source.CurrentHPWire == 0
                || target.IsAlive
                || target.CurrentHPWire != 0
                || !target.DeathLifecycleActive
                || target.DeathRemoveSent
                || (target.UnitFlags & 0x800) == 0
                || (target.UnitFlags & 0x1000) != 0
                || target.StockUnitState != 7 && target.StockUnitState != 8)
                return false;
            string sourceInstance = RoomRuntime.NormalizeInstanceKey(!string.IsNullOrWhiteSpace(source.InstanceKey) ? source.InstanceKey : source.ZoneName);
            string targetInstance = RoomRuntime.NormalizeInstanceKey(!string.IsNullOrWhiteSpace(target.InstanceKey) ? target.InstanceKey : target.ZoneName);
            return !string.IsNullOrWhiteSpace(sourceInstance)
                && string.Equals(sourceInstance, targetInstance, StringComparison.OrdinalIgnoreCase)
                && AreMonstersFriendsForAuthoredAoe(source, target);
        }

        private static int ResolveMonsterSpellUse(string spellUse)
        {
            if (int.TryParse(spellUse, out int numeric))
                return numeric;
            if (string.Equals(spellUse, "PEACE", StringComparison.OrdinalIgnoreCase)
                || string.Equals(spellUse, "IDLE", StringComparison.OrdinalIgnoreCase))
                return 1;
            if (string.Equals(spellUse, "COMBAT", StringComparison.OrdinalIgnoreCase))
                return 2;
            return 3;
        }

        private static int ResolveMonsterTargetType(string targetType)
        {
            if (int.TryParse(targetType, out int numeric))
                return numeric;
            if (string.Equals(targetType, "SELF", StringComparison.OrdinalIgnoreCase))
                return 0;
            if (string.Equals(targetType, "FRIEND", StringComparison.OrdinalIgnoreCase))
                return 1;
            if (string.Equals(targetType, "ENEMY", StringComparison.OrdinalIgnoreCase))
                return 2;
            if (string.Equals(targetType, "FRIENDCORPSE", StringComparison.OrdinalIgnoreCase)
                || string.Equals(targetType, "FRIEND_CORPSE", StringComparison.OrdinalIgnoreCase))
                return 6;
            return -1;
        }

        private static int ResolveHealthRatioF32(uint currentHPWire, uint maxHPWire)
        {
            if (maxHPWire == 0)
                return int.MaxValue;
            return (int)Math.Min(int.MaxValue, ((ulong)currentHPWire << 8) / maxHPWire);
        }

        private static bool PassesMonsterActiveSkillHealthPct(uint currentHPWire, uint maxHPWire, bool hasLimit, int limitF32)
        {
            if (!hasLimit)
                return true;
            return ResolveHealthRatioF32(currentHPWire, maxHPWire) <= Math.Max(0, limitF32);
        }

        public void CommitMonsterPrimarySkillUse(Monster monster, string source)
        {
            if (monster == null || !monster.UsePrimaryActiveSkillThisAttack || string.IsNullOrWhiteSpace(monster.PrimaryActiveSkillPath))
                return;
            if (monster.ActiveSkillUseCommittedThisAttack)
                return;
            var selected = monster.SelectedActiveSkill;
            if (selected == null)
                return;
            if (monster.SilenceAttributeValue > 0
                || selected.CooldownRemainingTicks != 0
                || monster.CurrentManaWire < selected.ManaCostWire)
            {
                Debug.LogError($"[MON-SKILL-USE] {monster.Name}#{monster.EntityId} blocked skill={selected.Path ?? "none"} silence={monster.SilenceAttributeValue} cooldown={selected.CooldownRemainingTicks} mana={monster.CurrentManaWire} cost={selected.ManaCostWire} source={source ?? "unknown"} sourceFunction=ActiveSkill::validateUse@0x00538710");
                return;
            }
            monster.ActiveSkillUseCommittedThisAttack = true;
            selected.CooldownRemainingTicks = selected.CooldownTicks;
            selected.CooldownLastTick = CombatTick;
            uint oldManaWire = monster.CurrentManaWire;
            monster.CurrentManaWire = oldManaWire - selected.ManaCostWire;
            if (monster.CurrentManaWire < oldManaWire)
                _monsterManaRegenCooldownTicks[monster.EntityId] = 0x96;
            ApplyMonsterActiveSkillSelection(monster, selected);
            Debug.LogError($"[MON-SKILL-USE] {monster.Name}#{monster.EntityId} skill={selected.Path} cooldown={selected.CooldownRemainingTicks}/{selected.CooldownTicks} mana={oldManaWire}->{monster.CurrentManaWire} cost={selected.ManaCostWire} source={source ?? "unknown"} sourceFunction=ActiveSkill::use@0x00538DD0");
        }

        private bool CanExecuteMonsterSelfActiveSkillEffect(Monster monster, out string reason)
        {
            reason = null;
            if (monster?.SelectedActiveSkill == null)
            {
                reason = "missing-selected-skill";
                return false;
            }
            if (!TryBuildMonsterSelfActiveSkillEffectPlan(monster, out MonsterSelfSkillPlan plan, out reason))
                return false;
            monster.ActiveSkillEffectPlan = plan;
            return true;
        }

        private bool TryResolveMonsterActiveSkillCycle(
            MonsterActiveSkillRuntime skill,
            int animationFrames,
            int triggerFrames,
            int speed,
            out ushort busyTicks,
            out ushort animationTicks,
            out ushort triggerTicks)
        {
            busyTicks = 0;
            animationTicks = 0;
            triggerTicks = 0;
            if (skill == null || animationFrames <= 0 || speed <= 0)
                return false;
            busyTicks = unchecked((ushort)(skill.RepeatCount * unchecked((ushort)animationFrames)));
            if (speed != 100)
                busyTicks = unchecked((ushort)(((uint)busyTicks * 100u) / (uint)speed));
            int scaledAnimationTicks = animationFrames;
            int scaledTriggerTicks = triggerFrames;
            if (speed != 100)
            {
                scaledAnimationTicks = (scaledAnimationTicks * 100) / speed;
                scaledTriggerTicks = (scaledTriggerTicks * 100) / speed;
            }
            if (scaledAnimationTicks <= 0 || scaledAnimationTicks > ushort.MaxValue)
                return false;
            scaledTriggerTicks = Math.Max(1, scaledTriggerTicks);
            animationTicks = (ushort)scaledAnimationTicks;
            triggerTicks = (ushort)Math.Min(ushort.MaxValue, scaledTriggerTicks);
            return true;
        }

        private void ArmMonsterSelfActiveSkillEffect(
            Monster monster,
            uint startTick,
            ushort busyTicks,
            ushort animationTicks,
            ushort triggerTicks,
            int authoredAnimationFrames,
            int authoredTriggerFrames,
            int speed)
        {
            if (monster?.SelectedActiveSkill == null)
                return;
            int start = unchecked((int)startTick);
            monster.ActiveSkillSelfCycle = true;
            monster.ActiveSkillBusyTicksRemaining = busyTicks;
            monster.ActiveSkillAnimationTicks = animationTicks;
            monster.ActiveSkillTriggerTicks = triggerTicks;
            monster.ActiveSkillEffectPending = busyTicks != 0;
            monster.ActiveSkillEffectResolved = false;
            monster.ActiveSkillEffectTargetEntityId = monster.EntityId;
            monster.ActiveSkillEffectStartTick = start;
            monster.ActiveSkillEffectCommitTick = ResolveFirstMonsterActiveSkillEffectTick(start, busyTicks, animationTicks, triggerTicks);
            monster.ActiveSkillEffectEndTick = busyTicks == 0 ? start : unchecked(start + busyTicks - 1);
            Debug.LogError($"[MON-SKILL-CYCLE] {monster.Name}#{monster.EntityId}->{monster.Name} startTick={monster.ActiveSkillEffectStartTick} effectTick={monster.ActiveSkillEffectCommitTick} endTick={monster.ActiveSkillEffectEndTick} animation={monster.SelectedActiveSkill.AnimationId} frames={authoredAnimationFrames}/{authoredTriggerFrames} repeat={monster.SelectedActiveSkill.RepeatCount} speed={speed} busy={busyTicks} sourceFunction=ActiveSkill::use@0x00538DD0->ActiveSkill::update@0x005392F0");
        }

        private void ArmMonsterFriendActiveSkillEffect(
            Monster monster,
            Monster target,
            uint startTick,
            ushort busyTicks,
            ushort animationTicks,
            ushort triggerTicks,
            int authoredAnimationFrames,
            int authoredTriggerFrames,
            int speed)
        {
            if (monster?.SelectedActiveSkill == null || target == null)
                return;
            int start = unchecked((int)startTick);
            monster.ActiveSkillSelfCycle = true;
            monster.ActiveSkillBusyTicksRemaining = busyTicks;
            monster.ActiveSkillAnimationTicks = animationTicks;
            monster.ActiveSkillTriggerTicks = triggerTicks;
            monster.ActiveSkillEffectPending = busyTicks != 0;
            monster.ActiveSkillEffectResolved = false;
            monster.ActiveSkillEffectTargetEntityId = target.EntityId;
            monster.ActiveSkillEffectStartTick = start;
            monster.ActiveSkillEffectCommitTick = ResolveFirstMonsterActiveSkillEffectTick(start, busyTicks, animationTicks, triggerTicks);
            monster.ActiveSkillEffectEndTick = busyTicks == 0 ? start : unchecked(start + busyTicks - 1);
            Debug.LogError($"[MON-SKILL-CYCLE] {monster.Name}#{monster.EntityId}->{target.Name} startTick={monster.ActiveSkillEffectStartTick} effectTick={monster.ActiveSkillEffectCommitTick} endTick={monster.ActiveSkillEffectEndTick} animation={monster.SelectedActiveSkill.AnimationId} frames={authoredAnimationFrames}/{authoredTriggerFrames} repeat={monster.SelectedActiveSkill.RepeatCount} speed={speed} busy={busyTicks} sourceFunction=ActiveSkill::useTarget@0x00538F00->ActiveSkill::update@0x005392F0");
        }

        private bool TryResolveMonsterActiveSkillAnimation(Monster monster, MonsterActiveSkillRuntime skill, out int animationFrames, out int triggerFrames, out int speed)
        {
            animationFrames = 30;
            triggerFrames = 15;
            speed = 100;
            if (monster == null || skill == null)
                return false;
            GCNode creature = ResolveAuthoredCreatureNode(monster.SpawnGCType, monster.GCType);
            GCNode description = creature?.GetChild("Description") ?? creature;
            string animationsPath = GetAuthoredString(description, "Animations", "");
            GCNode animations = string.IsNullOrWhiteSpace(animationsPath) ? null : GCDatabase.Instance?.ResolveWithInheritance(animationsPath);
            int animationKey = skill.AnimationId + Math.Max(0, monster.WeaponClassId) * 100;
            GCNode animation = animations?.AnonymousChildren?.FirstOrDefault(row => row != null && row.GetInt("ID", 0) == animationKey);
            if (animation != null)
            {
                animationFrames = animation.GetInt("NumFrames", 0);
                triggerFrames = animation.GetInt("TriggerTime", 0);
                if (animationFrames <= 0)
                {
                    Debug.LogError($"[MON-SKILL-CYCLE] {monster.Name}#{monster.EntityId} blocked reason=invalid-animation-row path={animationsPath ?? "none"} animationKey={animationKey} frames={animationFrames} trigger={triggerFrames} sourceFunction=Unit::getAnimation@0x0050AE80");
                    return false;
                }
            }
            if ((skill.ProfessionTypeMask & 1) != 0)
            {
                int authoredSpeedF32 = GCDatabase.Instance.GetRequiredKnobFixed32("MonsterAttackSpeed");
                int multiplierF32 = monster.AttackSpeedF32 > 0 ? monster.AttackSpeedF32 : 0x100;
                int attackSpeedF32 = (int)Math.Min(int.MaxValue, ((long)authoredSpeedF32 * Math.Max(1, multiplierF32)) >> 8);
                ushort attackSpeed = unchecked((ushort)Math.Max(0, GCDatabase.RoundFixed32ToInt(attackSpeedF32)));
                ushort castSpeedMod = unchecked((ushort)(monster.Slots?.Get(UnitSlot.CastSpeedMod, 0) ?? 0));
                int authoredAttackSpeedMod = monster.GetActiveAttributeModifierValue("ATTACK_SPEED_MOD");
                speed = unchecked((ushort)Math.Max(0, attackSpeed + castSpeedMod + authoredAttackSpeedMod));
                if (speed == 0)
                    return false;
            }
            return true;
        }

        private static int ResolveFirstMonsterActiveSkillEffectTick(int startTick, ushort busyTicks, int animationTicks, int triggerTicks)
        {
            for (int offset = 0; offset < busyTicks; offset++)
            {
                int remaining = busyTicks - offset;
                int elapsed = animationTicks - remaining % animationTicks;
                if (elapsed == triggerTicks)
                    return unchecked(startTick + offset);
            }
            return 0;
        }

        private void ProcessMonsterSelfActiveSkillUpdate(Monster monster)
        {
            if (monster == null || !monster.ActiveSkillSelfCycle || monster.ActiveSkillBusyTicksRemaining == 0)
                return;
            int animationTicks = monster.ActiveSkillAnimationTicks;
            int triggerTicks = monster.ActiveSkillTriggerTicks;
            int elapsed = animationTicks - monster.ActiveSkillBusyTicksRemaining % animationTicks;
            if (elapsed == triggerTicks)
            {
                int targetType = ResolveMonsterTargetType(monster.SelectedActiveSkill?.TargetType);
                Monster effectTarget = targetType == 1 || targetType == 6
                    ? GetMonster(monster.ActiveSkillEffectTargetEntityId)
                    : monster;
                bool handled;
                if (targetType == 6)
                {
                    handled = IsMonsterFriendCorpseTarget(monster, effectTarget)
                        && TryApplyMonsterCorpseActiveSkillEffect(monster, effectTarget);
                }
                else
                {
                    handled = effectTarget != null
                        && effectTarget.IsAlive
                        && effectTarget.CurrentHPWire != 0
                        && MatchesInstance(monster, effectTarget.InstanceKey)
                        && (targetType != 1 || AreMonstersFriendsForAuthoredAoe(monster, effectTarget))
                        && TryApplyMonsterSelfActiveSkillEffect(monster, effectTarget);
                }
                monster.ActiveSkillEffectResolved |= handled;
                Debug.LogError($"[MON-SKILL-CYCLE] {monster.Name}#{monster.EntityId}->{effectTarget?.Name ?? "missing"} effectTick={_combatTick} handled={handled} sourceFunction=ActiveSkill::update@0x005392F0->ActiveSkill::doSkillEffect@0x00539630");
            }
            monster.ActiveSkillBusyTicksRemaining--;
            if (monster.ActiveSkillBusyTicksRemaining == 0)
                monster.ActiveSkillEffectPending = false;
        }

        private bool TryApplyMonsterSelfActiveSkillEffect(Monster monster, Monster target)
        {
            MonsterSelfSkillPlan skillPlan = monster?.ActiveSkillEffectPlan as MonsterSelfSkillPlan;
            if (skillPlan == null
                && !TryBuildMonsterSelfActiveSkillEffectPlan(monster, out skillPlan, out _))
                return false;
            return ExecuteMonsterSelfEffectPlan(skillPlan.Root, monster, target);
        }

        private void ArmMonsterActiveSkillEffect(
            Monster monster,
            CombatTarget target,
            uint startTick,
            ushort busyTicks,
            ushort animationTicks,
            ushort triggerTicks,
            int authoredAnimationFrames,
            int authoredTriggerFrames,
            int speed)
        {
            if (monster == null || target == null)
                return;
            int start = unchecked((int)startTick);
            monster.ActiveSkillSelfCycle = false;
            monster.ActiveSkillBusyTicksRemaining = busyTicks;
            monster.ActiveSkillAnimationTicks = animationTicks;
            monster.ActiveSkillTriggerTicks = triggerTicks;
            monster.ActiveSkillEffectPending = busyTicks != 0;
            monster.ActiveSkillEffectResolved = false;
            monster.ActiveSkillEffectTargetEntityId = target.EntityId;
            monster.ActiveSkillEffectStartTick = start;
            monster.ActiveSkillEffectCommitTick = ResolveFirstMonsterActiveSkillEffectTick(start, busyTicks, animationTicks, triggerTicks);
            monster.ActiveSkillEffectEndTick = busyTicks == 0 ? start : unchecked(start + busyTicks - 1);
            Debug.LogError($"[MON-SKILL-CYCLE] {monster.Name}#{monster.EntityId}->{target.Name} startTick={monster.ActiveSkillEffectStartTick} effectTick={monster.ActiveSkillEffectCommitTick} endTick={monster.ActiveSkillEffectEndTick} animation={monster.SelectedActiveSkill?.AnimationId ?? 0} frames={authoredAnimationFrames}/{authoredTriggerFrames} repeat={monster.SelectedActiveSkill?.RepeatCount ?? 0} speed={speed} busy={busyTicks} sourceFunction=ActiveSkill::useTarget@0x00538F00->ActiveSkill::update@0x005392F0");
        }

        private void ProcessMonsterActiveSkillEffect(Monster monster)
        {
            if (monster?.ActiveSkillSelfCycle == true)
            {
                ProcessMonsterSelfActiveSkillUpdate(monster);
                return;
            }
            if (monster == null || !monster.ActiveSkillEffectPending || monster.ActiveSkillBusyTicksRemaining == 0)
                return;
            int animationTicks = monster.ActiveSkillAnimationTicks;
            int triggerTicks = monster.ActiveSkillTriggerTicks;
            int elapsed = animationTicks - monster.ActiveSkillBusyTicksRemaining % animationTicks;
            if (elapsed == triggerTicks)
            {
                uint targetEntityId = monster.ActiveSkillEffectTargetEntityId;
                if (TryGetCombatTarget(targetEntityId, out CombatTarget target)
                    && target != null
                    && target.IsAlive
                    && target.HasUnitState
                    && MatchesInstance(monster, target.InstanceKey))
                {
                    TryGetMonsterClientVisiblePositionFixed(monster, target.EntityId, out int monsterFixedX, out int monsterFixedY);
                    ResolveMonsterActionTargetPositionFixed(target, out int targetFixedX, out int targetFixedY, out _);
                    int dxFixed = targetFixedX - monsterFixedX;
                    int dyFixed = targetFixedY - monsterFixedY;
                    int distFixed = UnitMover.IntSqrt((long)dxFixed * dxFixed + (long)dyFixed * dyFixed);
                    bool applied = TryExecuteMonsterTargetActiveSkillEffect(monster, target, distFixed, "MON-SKILL", "ActiveSkill::update", out bool handled, out bool hpShifted, out bool deferred);
                    monster.ActiveSkillEffectResolved |= handled;
                    target.IsAlive = target.CurrentHPWire > 0;
                    if (applied && handled && !deferred)
                        OnMonsterAttackResolved?.Invoke(monster, target, hpShifted, target.CurrentHPWire);
                    Debug.LogError($"[MON-SKILL-CYCLE] {monster.Name}#{monster.EntityId}->{target.Name} effectTick={_combatTick} handled={handled} hpShifted={hpShifted} deferred={deferred} sourceFunction=ActiveSkill::update@0x005392F0->ActiveSkill::doSkillEffect@0x00539630");
                }
            }
            monster.ActiveSkillBusyTicksRemaining--;
            if (monster.ActiveSkillBusyTicksRemaining == 0)
                monster.ActiveSkillEffectPending = false;
        }

        private void ClearMonsterActiveSkillEffectCycle(Monster monster, bool preservePreparedTargetSkill = false)
        {
            if (monster == null)
                return;
            if (!preservePreparedTargetSkill && monster.ActiveSkillCastModifierApplied)
                RemoveMonsterAuthoredAttributeModifier(monster, monster.ActiveSkillCastModifierKey, "active-skill-cycle-clear");
            monster.ActiveSkillEffectPending = false;
            monster.ActiveSkillEffectResolved = false;
            monster.ActiveSkillEffectTargetEntityId = 0;
            monster.ActiveSkillEffectStartTick = 0;
            monster.ActiveSkillEffectCommitTick = 0;
            monster.ActiveSkillEffectEndTick = 0;
            monster.ActiveSkillSelfCycle = false;
            monster.ActiveSkillBusyTicksRemaining = 0;
            monster.ActiveSkillAnimationTicks = 0;
            monster.ActiveSkillTriggerTicks = 0;
            if (!preservePreparedTargetSkill)
            {
                monster.ActiveSkillEffectPlan = null;
                monster.ActiveSkillCastModifierKey = null;
                monster.ActiveSkillCastModifierApplied = false;
            }
            monster.AttackWeaponCycleFromUseTargetInterrupt = false;
        }

        private static bool IsMonsterActiveSkillOnlyCycle(Monster monster)
        {
            return monster != null
                && monster.AttackPending
                && monster.UsePrimaryActiveSkillThisAttack
                && !monster.AttackWeaponCycleFromUseTargetInterrupt
                && monster.AttackCommitTick <= 0
                && monster.ActiveSkillEffectEndTick > 0;
        }

        private void CompleteMonsterActiveSkillOnlyCycle(Monster monster)
        {
            if (monster == null)
                return;
            int startTick = monster.ActiveSkillEffectStartTick;
            int endTick = monster.ActiveSkillEffectEndTick;
            monster.AttackPending = false;
            monster.AttackClientVisible = false;
            monster.AttackActionQueued = false;
            monster.AttackActionAdmissionTick = 0;
            monster.AttackContactOnly = false;
            monster.AttackSoundPending = false;
            monster.AttackHitResolved = false;
            monster.AttackStartTick = 0;
            monster.AttackEndTick = 0;
            monster.AttackCommitTick = 0;
            monster.AttackSoundTick = 0;
            ClearMonsterActiveSkillEffectCycle(monster);
            if (monster.IsAlive)
                monster.State = MonsterState.Combat;
            Debug.LogError($"[MON-SKILL-CYCLE] {monster.Name}#{monster.EntityId} complete tick={_combatTick} startTick={startTick} endTick={endTick} sourceFunction=ActiveSkill::update@0x005392F0->UseTarget::States@0x00548370");
        }

        private void ProcessMonsterAttacks(Monster onlyMonster)
        {
            foreach (var monster in SelectMonsters(onlyMonster))
            {
                if (!monster.IsAlive)
                    continue;
                if (GetRoomRngForMonster(monster) == null)
                    continue;
                if (IsMonsterActiveSkillOnlyCycle(monster))
                    continue;
                if (!monster.AttackPending || monster.AttackCommitTick <= 0)
                    continue;
                uint runtimeTargetId = ResolveMonsterRuntimeAttackTargetId(monster);
                if (!TryGetCombatTarget(runtimeTargetId, out var target) || !target.IsAlive || !target.HasUnitState) continue;
                if (!string.IsNullOrWhiteSpace(target.InstanceKey) && !MatchesInstance(monster, target.InstanceKey))
                {
                    Debug.LogError($"[COMBAT-LIFECYCLE] {monster.Name}#{monster.EntityId} dropping target {target.Name}#{runtimeTargetId}: left monster instance '{monster.InstanceKey}' for '{target.InstanceKey}' sourceFunction=MonsterBehavior2::UpdateTargets@0x0051CB50 target-watcher-invalidated-out-of-world");
                    ClearMonsterTargetStateForLostPlayer(monster, runtimeTargetId, "target-left-instance");
                    continue;
                }
                if (target.CurrentHPWire == 0)
                {
                    target.IsAlive = false;
                    CancelMonsterPendingAttack(monster, "target_dead");
                    continue;
                }
                if (IsMonsterDamageReactionActive(monster)) continue;
                int allowedRangeFixed = monster.UsePrimaryActiveSkillThisAttack && !monster.AttackWeaponCycleFromUseTargetInterrupt
                    ? ResolveMonsterSkillTargetClearRangeF32(monster, monster.SelectedActiveSkill, target)
                    : ResolveMonsterEffectiveAttackRangeFixed(monster);
                if (allowedRangeFixed <= 0) continue;
                TryGetMonsterClientVisiblePositionFixed(monster, target.EntityId, out int attackMonsterFixedX, out int attackMonsterFixedY);
                ResolveMonsterActionTargetPositionFixed(target, out int targetFixedX, out int targetFixedY, out _);
                int dxFixed = targetFixedX - attackMonsterFixedX;
                int dyFixed = targetFixedY - attackMonsterFixedY;
                int distFixed = UnitMover.IntSqrt((long)dxFixed * dxFixed + (long)dyFixed * dyFixed);
                bool attackPathClear = IsMonsterAttackPathClear(monster, target, null);
                bool clientTargetAction = HasMonsterTargetAction(monster, target, distFixed);
                bool initUseGateway = IsMonsterActionTargetClearFixed(monster, target, allowedRangeFixed);
                TraceMonsterState(monster, "attack-loop", target, distFixed, allowedRangeFixed, monster.AttackPending ? "pending" : "ready");

                if (monster.AttackPending)
                {
                    if (monster.AttackCommitTick <= 0)
                    {
                        if (!attackPathClear)
                        {
                            CancelMonsterPendingAttack(monster, $"world_blocked distFixed8={distFixed} rangeF32={allowedRangeFixed}");
                            if (monster.IsAlive)
                                monster.State = MonsterState.Chase;
                            Debug.LogError($"[MON-DAMAGE] {monster.Name}#{monster.EntityId}->{target.Name} cancel worldBlocked distFixed8={distFixed} monsterRangeF32={allowedRangeFixed}");
                            OnMonsterAttackResolved?.Invoke(monster, target, false, target.CurrentHPWire);
                            continue;
                        }
                        if (!monster.IsAlive)
                        {
                            CancelMonsterPendingAttack(monster, "dead_unarmed");
                            continue;
                        }
                        if (!initUseGateway && !monster.UsePrimaryActiveSkillThisAttack)
                        {
                            if (clientTargetAction)
                                TraceFarTargetAction(monster, target, distFixed, allowedRangeFixed, "pending-start");
                            DelayMonsterAttackRetry(monster, "init_use_gateway_pending_start");
                            CancelMonsterPendingAttack(monster, $"init_use_gateway_pending_start distFixed8={distFixed} rangeF32={allowedRangeFixed} targetRangeF32={ResolveMonsterTargetSearchRangeFixed(monster)}");
                            if (monster.IsAlive)
                                monster.State = MonsterState.Chase;
                            continue;
                        }
                        if (!monster.AttackClientVisible)
                        {
                            int handlerCount = OnMonsterAttackStarted?.GetInvocationList().Length ?? 0;
                            Debug.LogError($"[MON-ATTACK] dispatch start {monster.Name}->{target.Name} session={monster.AttackSessionId} handlers={handlerCount} distFixed8={distFixed} rangeF32={allowedRangeFixed}");
                            OnMonsterAttackStarted?.Invoke(monster, target, monster.AttackSessionId);
                            if (!monster.AttackPending)
                            {
                                Debug.LogError($"[MON-ATTACK] {monster.Name}->{target.Name} START canceled session={monster.AttackSessionId}");
                                continue;
                            }
                        }
                        continue;
                    }
                    if (monster.AttackHitResolved)
                    {
                        if ((int)_combatTick < monster.AttackEndTick) continue;
                        monster.AttackPending = false;
                        monster.AttackClientVisible = false;
                        monster.AttackActionQueued = false;
                        monster.AttackActionAdmissionTick = 0;
                        monster.AttackContactOnly = false;
                        monster.AttackSoundPending = false;
                        monster.AttackHitResolved = false;
                        monster.AttackStartTick = 0;
                        monster.AttackEndTick = 0;
                        monster.AttackCommitTick = 0;
                        monster.AttackSoundTick = 0;
                        ClearMonsterActiveSkillEffectCycle(monster);
                        if (monster.IsAlive)
                            monster.State = MonsterState.Combat;
                        TraceMonsterState(monster, "attack-complete", target, distFixed, allowedRangeFixed, "end");
                        continue;
                    }
                    if (monster.AttackSoundPending && (int)_combatTick >= monster.AttackSoundTick)
                        ConsumeMonsterAttackSoundRng(monster);
                    if ((int)_combatTick < monster.AttackCommitTick) continue;
                    if (monster.AttackSoundPending)
                        ConsumeMonsterAttackSoundRng(monster);
                    monster.AttackHitResolved = true;
                    if (monster.AttackEndTick < (int)_combatTick)
                        monster.AttackEndTick = (int)_combatTick;
                    if (!monster.IsAlive)
                    {
                        CancelMonsterPendingAttack(monster, "killed_by_subentity_before_attack");
                        continue;
                    }
                    if (IsMonsterDamageReactionActive(monster))
                    {
                        CancelMonsterPendingAttack(monster, "knockdown_at_hit_resolve");
                        Debug.LogError($"[MON-DAMAGE] {monster.Name}#{monster.EntityId}->{target.Name} cancel damageReactionActive actionId=0x{monster.DamageReactionActionId:X2} phase={monster.DamageReactionPhase} nowTick={_combatTick} sourceFunction=KnockDown::States@0x0052A380");
                        OnMonsterAttackResolved?.Invoke(monster, target, false, target.CurrentHPWire);
                        continue;
                    }
                    if (!TryDeferMonsterProjectileImpact(monster, target, distFixed, "MON-DAMAGE", "ProcessMonsterAttacks"))
                        ResolveMonsterAttackDamage(monster, target, distFixed, "MON-DAMAGE", "ProcessMonsterAttacks", !monster.AttackWeaponCycleFromUseTargetInterrupt);
                    continue;
                }
                else
                    continue;
            }
        }

        public bool CommitMonsterAttackActionInput(Monster monster, CombatTarget target, byte sessionId, uint applyTick, bool replaceCurrentAction = false, bool useTargetAction = false)
        {
            if (monster == null || target == null || !monster.IsAlive || !target.IsAlive)
                return false;
            if (!monster.AttackPending || monster.AttackSessionId != sessionId)
                return false;
            if (monster.AttackCommitTick > 0)
            {
                Debug.LogError($"[MON-ATTACK-INPUT] {monster.Name}#{monster.EntityId}->{target.Name} session={sessionId} applyTick={applyTick} action=local-current sourceFunction=ClientEntityManager::processMessage@0x005DA460->Behavior::processUpdate@0x00515620");
                return true;
            }
            if (monster.Behavior?.AttackTarget2Active == true || monster.Behavior?.UseTargetActive == true)
            {
                monster.AttackActionQueued = false;
                monster.AttackActionAdmissionTick = 0;
                return true;
            }
            if (monster.AttackActionQueued)
                return true;

            if (!replaceCurrentAction && monster.Behavior?.UseTargetActive == true)
            {
                MonsterBehavior2.ActionSlot action = useTargetAction
                    ? MonsterBehavior2.ActionSlot.UseTarget
                    : MonsterBehavior2.ActionSlot.AttackTarget2;
                bool queued = monster.Behavior.QueueReceivedActionPreservingCurrent(BuildMeleeBehaviorContextForMonster(monster), action);
                if (!queued)
                    return false;
                monster.AttackActionQueued = false;
                monster.AttackActionAdmissionTick = 0;
                Debug.LogError($"[MON-ATTACK-INPUT] {monster.Name}#{monster.EntityId}->{target.Name} session={sessionId} applyTick={applyTick} current=UseTarget pending={action} action=pending-preserve-current sourceFunction=ClientEntityManager::processMessage@0x005DA460->Behavior::processUpdate@0x00515620->Behavior::doActionLocal@0x00515130");
                return true;
            }

            if (replaceCurrentAction)
            {
                WanderSimulator.Instance.UnregisterEntity(monster.EntityId);
                monster.WanderActionTerminationPending = false;
            }
            monster.AttackActionQueued = true;
            monster.AttackActionAdmissionTick = replaceCurrentAction
                ? applyTick
                : unchecked(applyTick + 2);
            Debug.LogError($"[MON-ATTACK-INPUT] {monster.Name}#{monster.EntityId}->{target.Name} session={sessionId} applyTick={applyTick} admissionTick={monster.AttackActionAdmissionTick} replaceCurrent={replaceCurrentAction} action=queued sourceFunction=ClientEntityManager::processMessage@0x005DA460->Behavior::processUpdate@0x00515620");
            return true;
        }

        public bool CommitMonsterAttackActionWireInput(Monster monster, CombatTarget target, byte sessionId, uint applyTick)
        {
            if (monster == null || target == null || !monster.IsAlive || !target.IsAlive)
                return false;
            if (!monster.AttackPending || monster.AttackSessionId != sessionId)
                return false;
            if (monster.AttackCommitTick == 0
                && (monster.Behavior?.AttackTarget2Active == true || monster.Behavior?.UseTargetActive == true))
            {
                if (monster.UsePrimaryActiveSkillThisAttack && monster.Behavior?.UseTargetActive == true)
                {
                    if (!ValidateMonsterWeaponUseTargetAction(
                            monster,
                            target,
                            "ClientEntityManager::processMessage@0x005DA460->Behavior::processUpdate@0x00515620"))
                    {
                        monster.Behavior.OnDoActionFailed();
                        Debug.LogError($"[MON-ATTACK-WIRE-INPUT] {monster.Name}#{monster.EntityId}->{target.Name} session={sessionId} applyTick={applyTick} result=action-failed message=8 reason=weapon-validate sourceFunction=ClientEntityManager::processMessage@0x005DA460->Behavior::processUpdate@0x00515620->Behavior::doInterruptLocal@0x00515290->MonsterBehavior2::onDoActionFailed@0x00516860");
                        return true;
                    }
                    MeleeBehaviorContext interruptContext = BuildMeleeBehaviorContextForMonster(monster);
                    if (interruptContext == null)
                        return false;
                    interruptContext.UseTargetWeaponInterruptInput = true;
                    bool processed = monster.Behavior.ProcessUseTargetWeaponInterruptInput(interruptContext);
                    interruptContext.UseTargetWeaponInterruptInput = false;
                    if (!processed)
                        return false;
                    return true;
                }
                ResolveMonsterActionTargetPositionFixed(target, out int targetFixedX, out int targetFixedY, out _);
                SetMonsterAttackCommitTargetFixed(monster, targetFixedX, targetFixedY);
                FaceMonsterAttackTarget(monster, target);
                monster.State = MonsterState.Attacking;
                monster.Ai?.SetState(MonsterStateId.Attack, monster.UsePrimaryActiveSkillThisAttack ? "UseTarget::start" : "AttackTarget2::start");
                if (monster.UsePrimaryActiveSkillThisAttack)
                {
                    CommitMonsterPrimarySkillUse(monster, "UseTarget::States@0x00548370->ActiveSkill::use@0x00538DD0");
                    if (!monster.ActiveSkillUseCommittedThisAttack)
                    {
                        CancelMonsterPendingAttack(monster, "active-skill-validate-failed");
                        return true;
                    }
                }
                ArmMonsterRuntimeAttack(monster, target, monster.UsePrimaryActiveSkillThisAttack ? "USETARGET-WIRE" : "ATTACKTARGET2-WIRE", applyTick);
                monster.AttackActionQueued = false;
                monster.AttackActionAdmissionTick = 0;
                return true;
            }
            if (monster.AttackCommitTick == 0 || monster.Behavior == null || !monster.Behavior.AttackTarget2Active)
                return true;
            MeleeBehaviorContext attackTarget2InterruptContext = BuildMeleeBehaviorContextForMonster(monster);
            if (attackTarget2InterruptContext == null)
                return false;
            bool attackTarget2InterruptProcessed = monster.Behavior.ProcessAttackTarget2WeaponInterruptInput(attackTarget2InterruptContext);
            Debug.LogError($"[MON-ATTACK-WIRE-INPUT] {monster.Name}#{monster.EntityId}->{target.Name} session={sessionId} applyTick={applyTick} result={(attackTarget2InterruptProcessed ? "interrupt-locked" : "action-failed")} generation={monster.Behavior.ActionGeneration} current={monster.Behavior.CurrentAction} alternate={monster.Behavior.AlternateAction} pending={monster.Behavior.PendingAction} interruptLocked={monster.Behavior.CurrentActionInterruptLocked} sourceFunction=ClientEntityManager::processMessage@0x005DA460->Behavior::processUpdate@0x00515620->Behavior::doInterruptLocal@0x00515290");
            return true;
        }

        private void StartQueuedMonsterAttackTargetAction(Monster monster)
        {
            if (monster == null || !monster.AttackPending || !monster.AttackActionQueued || monster.AttackCommitTick > 0)
                return;
            if (_combatTick < monster.AttackActionAdmissionTick)
                return;
            uint targetEntityId = ResolveMonsterRuntimeAttackTargetId(monster);
            if (!TryGetCombatTarget(targetEntityId, out CombatTarget target) || target == null || !target.IsAlive)
            {
                CancelMonsterPendingAttack(monster, "UnitAction-start-missing-target");
                return;
            }
            ResolveMonsterActionTargetPositionFixed(target, out int targetFixedX, out int targetFixedY, out _);
            SetMonsterAttackCommitTargetFixed(monster, targetFixedX, targetFixedY);
            FaceMonsterAttackTarget(monster, target);
            monster.State = MonsterState.Attacking;
            monster.Ai?.SetState(MonsterStateId.Attack, monster.UsePrimaryActiveSkillThisAttack ? "UseTarget::start" : "AttackTarget2::start");
            if (monster.UsePrimaryActiveSkillThisAttack)
            {
                CommitMonsterPrimarySkillUse(monster, "UseTarget::States@0x00548370->ActiveSkill::use@0x00538DD0");
                if (!monster.ActiveSkillUseCommittedThisAttack)
                {
                    CancelMonsterPendingAttack(monster, "active-skill-validate-failed");
                    return;
                }
            }
            ArmMonsterRuntimeAttack(monster, target, monster.UsePrimaryActiveSkillThisAttack ? "USETARGET-START" : "ATTACKTARGET2-START", _combatTick);
            monster.AttackActionAdmissionTick = 0;
            Debug.LogError($"[MON-ATTACK-INPUT] {monster.Name}#{monster.EntityId}->{target.Name} session={monster.AttackSessionId} startTick={_combatTick} action={(monster.UsePrimaryActiveSkillThisAttack ? "UseTarget" : "AttackTarget2")} sourceFunction=Behavior::update@0x005154B0->Behavior::startAction@0x00515CF0");
        }

        private void FaceMonsterAttackTarget(Monster monster, CombatTarget target)
        {
            if (monster == null || target == null)
                return;
            TryGetMonsterClientVisiblePositionFixed(monster, target.EntityId, out int monsterFixedX, out int monsterFixedY);
            ResolveMonsterActionTargetPositionFixed(target, out int targetFixedX, out int targetFixedY, out _);
            int headingFixed = UnitMover.VectorToHeadingFixed(
                targetFixedX - monsterFixedX,
                targetFixedY - monsterFixedY);
            monster.HeadingFixed = headingFixed;
            monster.ChaseHeadingFixed = headingFixed;
            monster.ChaseHeadingInit = true;
            monster.UnitMoverDesiredHeadingFixed = headingFixed;
            monster.UnitMoverDesiredHeadingInit = true;
            monster.ClientVisibleHeadingFixed = headingFixed;
            monster.ClientVisibleHeadingInit = true;
            Debug.LogError($"[MON-ATTACK-FACING] entity={monster.EntityId} tick={_combatTick} actorFixed=({monsterFixedX},{monsterFixedY}) targetFixed=({targetFixedX},{targetFixedY}) heading={headingFixed} sourceFunction=AttackTarget2::States@0x00523DA0->UnitMover::FaceTarget@0x00535EF0");
        }

        private static void ClearMonsterLocalAttackActionUse(Monster monster)
        {
            if (monster == null)
                return;
            monster.LocalAttackActionUsePending = false;
            monster.LocalAttackActionUseTargetId = 0;
            monster.LocalAttackActionUseSuppressPacket = false;
            monster.LocalAttackActionUseTick = 0;
        }

        private void ArmMonsterRuntimeAttack(Monster monster, CombatTarget target, string marker, uint applyTick)
        {
            if (monster == null || target == null) return;
            if (IsMonsterDamageReactionActive(monster))
                return;
            int nowTick = (int)applyTick;
            if (monster.AttackStartTick <= 0)
                monster.AttackStartTick = nowTick;
            if (monster.AttackCommitTick > 0)
                return;
            AdvanceMonsterAttackAnimation(monster);
            int hitTicks = ResolveMonsterAttackWindupTicks(monster);
            ResolveMonsterAttackAnimationFrames(monster, out int totalFrames, out int hitFrame, out int soundFrame);
            int totalTicks = ResolveMonsterAttackTotalTicks(monster);
            if (hitTicks <= 0 || totalTicks <= 0 || soundFrame <= 0)
            {
                CancelMonsterPendingAttack(monster, "attack-timing-unresolved");
                Debug.LogError($"[MON-ATTACK] {monster.Name}#{monster.EntityId} blocked marker={marker} reason=attack-timing-unresolved sourceFunction=Weapon::computeAttackTicks@0x00598E50");
                return;
            }
            int startTick = monster.AttackStartTick > 0 ? monster.AttackStartTick : nowTick;
            int soundTicks = ResolveMonsterAttackSoundDelayTicks(monster, hitTicks);
            int cooldownTicks = ResolveMonsterAttackCooldownTicks(monster);
            monster.AttackCommitTick = ResolveMeleeWeaponUpdateTick(startTick, hitTicks);
            monster.AttackSoundTick = ResolveMeleeWeaponUpdateTick(startTick, soundTicks);
            monster.AttackEndTick = Math.Max(monster.ActiveSkillEffectEndTick, ResolveMeleeWeaponUpdateTick(startTick, totalTicks));
            monster.NextAttackTick = startTick + Math.Max(0, cooldownTicks);
            monster.AttackHitResolved = false;
            monster.AttackSoundPending = monster.HasAttackSound || monster.AttackWeaponSoundCount > 0 || monster.AttackRepeatSoundCount > 0;
            Debug.LogError($"[MON-ATTACK] {monster.Name}->{target.Name} {marker} anim={monster.AttackAnimationIndex} session={monster.AttackSessionId} use=0x{monster.AttackUseRaw:X8} frames={totalFrames}/{hitFrame}/{soundFrame} soundAtTick={monster.AttackSoundTick} hitAtTick={monster.AttackCommitTick} endAtTick={monster.AttackEndTick} weaponSounds={monster.AttackWeaponSoundCount} attackSounds={monster.AttackRepeatSoundCount}");
            TraceMonsterState(monster, "attack-arm", target, -1, ResolveMonsterEffectiveAttackRangeFixed(monster), marker);
        }

        private static int ResolveMeleeWeaponUpdateTick(int startTick, int durationTicks)
        {
            return startTick + Math.Max(0, durationTicks - 1);
        }

        public void AdvanceMonsterUnitUpdateForEntity(uint entityId)
        {
            if (!_activeMonsters.TryGetValue(entityId, out var monster) || monster == null)
                return;
            AdvanceMonsterAttributeModifierTick(monster);
            AdvanceMonsterCowardiceModifierTick(monster);
            AdvanceMonsterAuraModifierTick(monster);
            if ((monster.UnitFlags & 0x400) == 0)
            {
                _monsterHPRegenLastTick[monster.EntityId] = _combatTick;
                _monsterManaRegenLastTick[monster.EntityId] = _combatTick;
                return;
            }
            ApplyMonsterVitalsRegen(monster, _combatTick, "Unit::update@0x5093E0");
        }

        public void AdvanceMonsterWeaponManipulatorChild(uint entityId)
        {
            if (!_activeMonsters.TryGetValue(entityId, out Monster monster) || monster == null)
                return;
            ProcessMonsterAttacks(monster);
        }

        public void UpdateMaintenance()
        {
        }

        public void ClearAll()
        {
            foreach (uint entityId in new List<uint>(_entityOrder))
                if (_activeMonsters.ContainsKey(entityId))
                    DespawnMonster(entityId, true, true);
            ClearAllClientSubEntities();
            _players.Clear();
            _monsterRuntimeDamageCommitted.Clear();
            _monsterHPRegenLastTick.Clear();
            _monsterHPRegenCooldownTicks.Clear();
            _monsterManaRegenLastTick.Clear();
            _monsterManaRegenCooldownTicks.Clear();
            _monsterStateTraceSignatures.Clear();
            _encounterRuntimes.Clear();
            _encounterRuntimeOrder.Clear();
            _activeMonsterModifiers.Clear();
            _activePlayerDamageModifiers.Clear();
            _activeMonsterAuras.Clear();
            _activeMonsterAuraKeys.Clear();
            _activePlayerAnchoredMonsterAuras.Clear();
            _activePlayerAnchoredMonsterAuraKeys.Clear();
            _nextMonsterModifierOrder = 0;
            _nextPlayerModifierOrder = 0;
            _nextMonsterAuraOrder = 0;
            _nextPlayerAnchoredMonsterAuraOrder = 0;
            _playerModifierNetworkIds.Clear();
            _monsterViewerClientVisible.Clear();
            _monsterViewerClientVisibleOrder.Clear();
            _pendingModifierKills.Clear();
            _monsterFarTargetActionLogTick.Clear();
            _pendingMonsterProjectiles.Clear();
            _nextMonsterProjectileSequence = 0;
            _clientSubEntityOrder.Clear();
            _nextClientSubEntityOrder = 0;
            _moveToPointPathQueues.Clear();
            _itemObjectInstanceKeys.Clear();
            _encounterObjectInstanceKeys.Clear();
            _entityOrder.Clear();
            _entityOrderSet.Clear();
            _entityUpdateAdmissionTicks.Clear();
            _entityUpdateRemovalTicks.Clear();
            _entityUpdateRemovalOrder.Clear();
            _pendingMonsterFinalRemovals.Clear();
            _hasCompletedEntityUpdate = false;
            _lastCompletedEntityUpdateTick = 0;
            _hasCompletedSubEntityUpdate = false;
            _lastCompletedSubEntityUpdateTick = 0;
        }

        private byte GetLevelForTier(string tier)
        {
            return DungeonRunners.Gameplay.ServerReconstructionPolicy.ResolveTierLevelOffset(tier);
        }

        private byte GetZoneBaseLevel(string zoneName)
        {
            if (string.IsNullOrEmpty(zoneName)) return 1;

            string lower = zoneName.ToLower();

            if (lower.Contains("tutorial")) return 1;

            if (lower.StartsWith("dungeon") && lower.Length >= 9)
            {
                if (int.TryParse(lower.Substring(7, 2), out int dungeonNum))
                {
                    return (byte)(dungeonNum * 4);
                }
            }

            return 1;
        }

        public Monster GetMonsterByBehaviorId(uint behaviorId)
        {
            foreach (var monster in GetAllMonsters())
            {
                if (monster.BehaviorId == behaviorId)
                    return monster;
            }
            return null;
        }

        public Monster GetMonsterByManipulatorsId(uint manipulatorsId)
        {
            foreach (var monster in GetAllMonsters())
            {
                if (monster.ManipulatorsId == manipulatorsId)
                    return monster;
            }
            return null;
        }

        public Monster GetMonsterBySkillsId(uint skillsId)
        {
            foreach (var monster in GetAllMonsters())
            {
                if (monster.SkillsId == skillsId)
                    return monster;
            }
            return null;
        }

        public List<Monster> GetMonstersInSpellEffectRangeFixed(int xF32, int yF32, int zF32, int rangeF32, string instanceKey)
        {
            var ranked = new List<(Monster Monster, long DistanceSq)>();
            long rangeSq = rangeF32 > 0 ? ((long)rangeF32 * rangeF32) >> 8 : 0;
            string normalizedInstanceKey = string.IsNullOrWhiteSpace(instanceKey)
                ? null
                : RoomRuntime.NormalizeInstanceKey(instanceKey);
            foreach (var monster in GetAllMonsters())
            {
                if (!monster.IsAlive) continue;
                if (!MatchesInstance(monster, normalizedInstanceKey)) continue;
                if (!TryGetMonsterClientVisiblePositionFixed(monster, 0, out int visibleFixedX, out int visibleFixedY, out int visibleFixedZ))
                    continue;

                long distanceSq = ProjectileFinderDistanceSqFixed8(
                    visibleFixedX,
                    visibleFixedY,
                    visibleFixedZ,
                    xF32,
                    yF32,
                    zF32);
                if (distanceSq <= rangeSq)
                    ranked.Add((monster, distanceSq));
            }

            ranked.Sort((left, right) =>
            {
                int distanceCompare = left.DistanceSq.CompareTo(right.DistanceSq);
                return distanceCompare != 0
                    ? distanceCompare
                    : ProjectileFinderTieId(left.Monster).CompareTo(ProjectileFinderTieId(right.Monster));
            });

            var result = new List<Monster>(ranked.Count);
            foreach (var item in ranked)
                result.Add(item.Monster);
            return result;
        }

        private class MonsterClientVisibleState
        {
            public bool Initialized;
            public bool Active;
            public uint LastTick;
            public int FixedX, FixedY;
            public int FixedZ;
            public bool FixedInit;
            public bool FixedZInit;
            public int TargetFixedX, TargetFixedY;
            public bool TargetFixedInit;
            public int HeadingFixed;
            public bool HeadingInit;
        }

    }
}
