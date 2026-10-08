using System;
using System.Collections.Generic;
using DungeonRunners.Core;
using DungeonRunners.Data;
using DungeonRunners.Engine;

namespace DungeonRunners.Combat
{
    public partial class CombatRuntime
    {
        private void ConfigureMonsterProcModifiers(Monster monster, GCNode authoredCreature)
        {
            if (monster == null)
                return;
            monster.ProcModifiers.Clear();
            monster.BlockedProcModifierPaths.Clear();
            GCNode modifiers = authoredCreature?.GetChild("Modifiers");
            if (modifiers == null)
                return;

            foreach (GCNode modifierSlot in modifiers.EnumerateChildrenInOrder())
            {
                if (!IsAuthoredNodeKindOf(modifierSlot, "ProcModifier"))
                    continue;
                string slotPath = ResolveMonsterProcSlotPath(modifierSlot);
                if (!TryBuildMonsterProcModifierRuntime(modifierSlot, out MonsterProcModifierRuntime runtime, out string reason))
                {
                    monster.BlockedProcModifierPaths.Add(slotPath);
                    SkillEffectTracker.RecordRejected("monster-proc", monster.EntityId, slotPath, 0, reason ?? "unsupported-authored-proc", CombatTick);
                    Debug.LogError($"[MON-SKILL-EFFECT-SUPPORT] state=BLOCKED_EVIDENCE monster={monster.Name}#{monster.EntityId} modifier={slotPath} reason={reason ?? "unsupported-authored-proc"} resourceCommit=False rngDraw=False");
                    continue;
                }
                monster.ProcModifiers.Add(runtime);
                Debug.LogError($"[MON-SKILL-EFFECT-SUPPORT] state=PROVEN monster={monster.Name}#{monster.EntityId} modifier={runtime.ModifierPath} slot={runtime.SlotPath} effect={runtime.EffectPath} chance={runtime.ChanceDivisor} target={runtime.TargetKind} conditions={runtime.Conditions.Count} sourceFunction=GCObject::createChildInstances@0x005E9840 ProcModifier::doEvent@0x0056E840");
            }
        }

        private bool TryBuildMonsterProcModifierRuntime(
            GCNode modifierSlot,
            out MonsterProcModifierRuntime runtime,
            out string reason)
        {
            runtime = null;
            reason = null;
            GCDatabase gc = GCDatabase.Instance;
            if (gc == null || !gc.IsLoaded)
            {
                reason = "authored-database-unavailable";
                return false;
            }
            GCNode modifier = gc.ResolveWithInheritance(modifierSlot) ?? modifierSlot;
            GCNode description = modifier.GetChild("Description") ?? modifier;
            description = gc.ResolveWithInheritance(description) ?? description;
            string modifierPath = string.IsNullOrWhiteSpace(modifierSlot.Extends)
                ? ResolveMonsterProcSlotPath(modifierSlot)
                : modifierSlot.Extends.Trim();
            string effectPath = description.GetString("Effect", null)?.Trim();
            if (string.IsNullOrWhiteSpace(effectPath))
            {
                reason = "missing-proc-effect";
                return false;
            }
            GCNode effect = ResolveAuthoredNodeReference(effectPath, modifier);
            if (effect == null)
            {
                reason = "missing-authored-proc-effect";
                return false;
            }
            string requiredZone = description.GetString("RequiredZone", null)?.Trim();
            if (!string.IsNullOrWhiteSpace(requiredZone))
            {
                reason = $"required-zone-unimplemented:{requiredZone}";
                return false;
            }
            int manaCostF32 = description.GetFixed32("ManaCost", 0);
            if (manaCostF32 != 0)
            {
                reason = $"unsupported-proc-mana-cost:{manaCostF32}";
                return false;
            }
            int chanceDivisor = description.GetInt("Chance", 1);
            if (chanceDivisor < 0)
            {
                reason = $"invalid-proc-chance:{chanceDivisor}";
                return false;
            }
            if (!TryParseMonsterProcTarget(description.GetString("Target", "SELF"), out MonsterProcTargetKind targetKind))
            {
                reason = $"unsupported-proc-target:{description.GetString("Target", "SELF")}";
                return false;
            }

            runtime = new MonsterProcModifierRuntime
            {
                SlotPath = ResolveMonsterProcSlotPath(modifierSlot),
                ModifierPath = modifierPath,
                EffectPath = effectPath,
                ChanceDivisor = chanceDivisor,
                TargetKind = targetKind,
                RemoveOnDeath = description.GetBool("RemoveOnDeath", false),
                SkillLevel = 1,
                PowerLevelF32 = 0x100
            };

            foreach (GCNode conditionNode in description.EnumerateChildrenInOrder())
            {
                if (!IsAuthoredNodeKindOf(conditionNode, "ProcCondition"))
                    continue;
                if (!TryBuildMonsterProcCondition(conditionNode, out MonsterProcConditionRuntime condition, out reason))
                    return false;
                runtime.Conditions.Add(condition);
            }
            if (runtime.Conditions.Count == 0)
            {
                reason = "missing-proc-condition";
                return false;
            }

            bool hasHit = false;
            bool hasDeath = false;
            bool hasProjectileHit = false;
            foreach (MonsterProcConditionRuntime condition in runtime.Conditions)
            {
                hasHit |= condition.EventKind == MonsterProcEventKind.Hit;
                hasDeath |= condition.EventKind == MonsterProcEventKind.Death;
                hasProjectileHit |= condition.EventKind == MonsterProcEventKind.ProjectileHit;
            }
            int eventFamilies = (hasHit ? 1 : 0) + (hasDeath ? 1 : 0) + (hasProjectileHit ? 1 : 0);
            if (eventFamilies != 1)
            {
                reason = "mixed-proc-event-families-unimplemented";
                return false;
            }

            if (hasHit)
            {
                if (runtime.TargetKind != MonsterProcTargetKind.Object)
                {
                    reason = "hit-proc-target-must-be-object";
                    return false;
                }
                if (!TryBuildMonsterTargetEffectNode(effect, effectPath, modifier, modifierPath, runtime.SkillLevel, unchecked((uint)runtime.PowerLevelF32), out MonsterTargetEffectNode targetPlan, out reason))
                    return false;
                if (!IsSupportedMonsterHitProcPlan(targetPlan, out reason))
                    return false;
                runtime.EffectPlan = targetPlan;
                return true;
            }

            if (hasDeath)
            {
                if (runtime.TargetKind != MonsterProcTargetKind.Self)
                {
                    reason = "death-proc-target-must-be-self";
                    return false;
                }
                if (!TryBuildMonsterTargetEffectNode(effect, effectPath, modifier, modifierPath, runtime.SkillLevel, unchecked((uint)runtime.PowerLevelF32), out MonsterTargetEffectNode deathPlan, out reason))
                    return false;
                if (!IsSupportedMonsterDeathProcPlan(deathPlan, false, out reason))
                    return false;
                runtime.EffectPlan = deathPlan;
                return true;
            }

            if (runtime.TargetKind != MonsterProcTargetKind.Self)
            {
                reason = "projectile-hit-proc-target-must-be-self";
                return false;
            }
            if (!TryBuildMonsterSelfEffectNode(effect, effectPath, modifier, runtime.SkillLevel, runtime.PowerLevelF32, out MonsterSelfEffectNode selfPlan, out reason))
                return false;
            if (!IsSupportedMonsterProjectileHitSelfProcPlan(selfPlan, out reason))
                return false;
            runtime.EffectPlan = selfPlan;
            return true;
        }

        private bool TryBuildMonsterProcCondition(
            GCNode authoredCondition,
            out MonsterProcConditionRuntime condition,
            out string reason)
        {
            condition = null;
            reason = null;
            GCNode resolved = GCDatabase.Instance.ResolveWithInheritance(authoredCondition) ?? authoredCondition;
            string effectObjectType = resolved.GetString("EffectObjectType", null)?.Trim();
            string sourceObjectType = resolved.GetString("SourceObjectType", null)?.Trim();
            string targetObjectType = resolved.GetString("TargetObjectType", null)?.Trim();
            if (!string.IsNullOrWhiteSpace(effectObjectType)
                || !string.IsNullOrWhiteSpace(sourceObjectType)
                || !string.IsNullOrWhiteSpace(targetObjectType))
            {
                reason = "proc-condition-object-type-filter-unimplemented";
                return false;
            }
            string eventType = resolved.GetString("EventType", "ON_HIT").Trim();
            if (!TryParseMonsterProcEvent(eventType, out MonsterProcEventKind eventKind))
            {
                reason = $"unsupported-proc-event:{eventType}";
                return false;
            }
            string attackType = resolved.GetString("AttackType", "ANY").Trim().ToUpperInvariant();
            if (!string.Equals(attackType, "ANY", StringComparison.Ordinal)
                && !string.Equals(attackType, "MELEE", StringComparison.Ordinal)
                && !string.Equals(attackType, "RANGED", StringComparison.Ordinal)
                && !string.Equals(attackType, "MAGIC", StringComparison.Ordinal))
            {
                reason = $"unsupported-proc-attack-type:{attackType}";
                return false;
            }
            string damageType = resolved.GetString("DamageType", "ANY").Trim().ToUpperInvariant();
            int damageTypeId = -1;
            if (!string.Equals(damageType, "ANY", StringComparison.Ordinal)
                && !DamageResolver.TryResolveDamageTypeId(damageType, out damageTypeId))
            {
                reason = $"unsupported-proc-damage-type:{damageType}";
                return false;
            }
            string projectileObjectType = resolved.GetString("ProjectileObjectType", null)?.Trim();
            if (eventKind != MonsterProcEventKind.ProjectileHit && !string.IsNullOrWhiteSpace(projectileObjectType))
            {
                reason = "projectile-type-filter-on-non-projectile-event";
                return false;
            }
            if (!string.IsNullOrWhiteSpace(projectileObjectType))
            {
                GCNode projectileType = GCDatabase.Instance.Resolve(projectileObjectType);
                if (projectileType == null)
                {
                    reason = $"missing-projectile-object-type:{projectileObjectType}";
                    return false;
                }
                projectileObjectType = string.IsNullOrWhiteSpace(projectileType.CanonicalPath)
                    ? projectileObjectType
                    : projectileType.CanonicalPath;
            }
            condition = new MonsterProcConditionRuntime
            {
                EventKind = eventKind,
                AttackType = attackType,
                DamageTypeId = damageTypeId,
                ProjectileObjectType = projectileObjectType
            };
            return true;
        }

        private static bool TryParseMonsterProcEvent(string value, out MonsterProcEventKind eventKind)
        {
            string normalized = (value ?? string.Empty).Trim().ToUpperInvariant();
            if (normalized == "ON_HIT")
            {
                eventKind = MonsterProcEventKind.Hit;
                return true;
            }
            if (normalized == "ON_DEATH")
            {
                eventKind = MonsterProcEventKind.Death;
                return true;
            }
            if (normalized == "ON_PROJECTILE_HIT")
            {
                eventKind = MonsterProcEventKind.ProjectileHit;
                return true;
            }
            eventKind = default;
            return false;
        }

        private static bool TryParseMonsterProcTarget(string value, out MonsterProcTargetKind targetKind)
        {
            string normalized = (value ?? string.Empty).Trim().ToUpperInvariant();
            if (normalized == "SELF" || normalized == "0")
            {
                targetKind = MonsterProcTargetKind.Self;
                return true;
            }
            if (normalized == "OBJECT" || normalized == "3")
            {
                targetKind = MonsterProcTargetKind.Object;
                return true;
            }
            targetKind = default;
            return false;
        }

        private static string ResolveMonsterProcSlotPath(GCNode modifierSlot)
        {
            if (!string.IsNullOrWhiteSpace(modifierSlot?.CanonicalPath))
                return modifierSlot.CanonicalPath;
            if (!string.IsNullOrWhiteSpace(modifierSlot?.Extends))
                return modifierSlot.Extends;
            return modifierSlot?.Name ?? "unknown-proc";
        }

        private static bool IsAuthoredNodeKindOf(GCNode node, string typeName)
        {
            if (node == null || string.IsNullOrWhiteSpace(typeName))
                return false;
            string current = node.Extends;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int depth = 0; depth < 64 && !string.IsNullOrWhiteSpace(current) && visited.Add(current); depth++)
            {
                if (string.Equals(current, typeName, StringComparison.OrdinalIgnoreCase)
                    || current.EndsWith("." + typeName, StringComparison.OrdinalIgnoreCase))
                    return true;
                GCNode ancestor = GCDatabase.Instance?.Resolve(current);
                current = ancestor?.Extends;
            }
            return false;
        }

        private static bool IsSupportedMonsterHitProcPlan(MonsterTargetEffectNode plan, out string reason)
        {
            reason = null;
            if (plan == null)
            {
                reason = "missing-hit-proc-plan";
                return false;
            }
            switch (plan.Kind)
            {
                case MonsterTargetEffectNodeKind.Sequence:
                    foreach (MonsterTargetEffectNode child in plan.Children)
                        if (!IsSupportedMonsterHitProcPlan(child, out reason))
                            return false;
                    return true;
                case MonsterTargetEffectNodeKind.VisualOnly:
                case MonsterTargetEffectNodeKind.SpellDamage:
                case MonsterTargetEffectNodeKind.StunAction:
                case MonsterTargetEffectNodeKind.AttributeModifier:
                case MonsterTargetEffectNodeKind.DamageModifier:
                    return true;
                default:
                    reason = $"unsupported-hit-proc-effect-kind:{plan.Kind}";
                    return false;
            }
        }

        private static bool IsSupportedMonsterDeathProcPlan(MonsterTargetEffectNode plan, bool hasPlayerTarget, out string reason)
        {
            reason = null;
            if (plan == null)
            {
                reason = "missing-death-proc-plan";
                return false;
            }
            if (hasPlayerTarget)
                return IsSupportedMonsterHitProcPlan(plan, out reason);
            if (plan.Kind == MonsterTargetEffectNodeKind.Sequence)
            {
                foreach (MonsterTargetEffectNode child in plan.Children)
                    if (!IsSupportedMonsterDeathProcPlan(child, false, out reason))
                        return false;
                return true;
            }
            if (plan.Kind == MonsterTargetEffectNodeKind.VisualOnly || plan.Kind == MonsterTargetEffectNodeKind.Spawn)
                return true;
            if (plan.Kind == MonsterTargetEffectNodeKind.EnemyAoe)
            {
                foreach (MonsterTargetEffectNode child in plan.Children)
                    if (!IsSupportedMonsterDeathProcPlan(child, true, out reason))
                        return false;
                return true;
            }
            reason = $"unsupported-death-self-proc-effect-kind:{plan.Kind}";
            return false;
        }

        private static bool IsSupportedMonsterProjectileHitSelfProcPlan(MonsterSelfEffectNode plan, out string reason)
        {
            reason = null;
            if (plan == null)
            {
                reason = "missing-projectile-hit-proc-plan";
                return false;
            }
            if (plan.Kind == MonsterSelfEffectNodeKind.Sequence)
            {
                foreach (MonsterSelfEffectNode child in plan.Children)
                    if (!IsSupportedMonsterProjectileHitSelfProcPlan(child, out reason))
                        return false;
                return true;
            }
            if (plan.Kind == MonsterSelfEffectNodeKind.VisualOnly
                || plan.Kind == MonsterSelfEffectNodeKind.HitPointRegen
                || plan.Kind == MonsterSelfEffectNodeKind.AuthoredAttributeModifier)
                return true;
            reason = $"unsupported-projectile-hit-self-proc-effect-kind:{plan.Kind}";
            return false;
        }

        private bool DispatchMonsterHitProcs(
            Monster monster,
            CombatTarget target,
            string attackType,
            int damageTypeId,
            uint appliedDamageWire,
            string source)
        {
            if (monster == null
                || target == null || !target.HasUnitState
                || appliedDamageWire == 0
                || !monster.IsAlive
                || !target.IsAlive
                || target.CurrentHPWire == 0)
                return false;
            string normalizedAttackType = (attackType ?? string.Empty).Trim().ToUpperInvariant();
            if (normalizedAttackType != "MELEE" && normalizedAttackType != "RANGED" && normalizedAttackType != "MAGIC")
                return false;
            bool executed = false;
            foreach (MonsterProcModifierRuntime proc in monster.ProcModifiers)
            {
                if (proc == null || proc.Executing)
                    continue;
                if (!monster.IsAlive
                    || !target.IsAlive
                    || target.CurrentHPWire == 0)
                    continue;
                proc.Executing = true;
                try
                {
                    if (!PassesMonsterProcChance(monster, proc, source))
                        continue;
                    MonsterProcConditionRuntime matched = null;
                    foreach (MonsterProcConditionRuntime condition in proc.Conditions)
                    {
                        if (condition.EventKind != MonsterProcEventKind.Hit)
                            continue;
                        if (!string.Equals(condition.AttackType, "ANY", StringComparison.Ordinal)
                            && !string.Equals(condition.AttackType, normalizedAttackType, StringComparison.Ordinal))
                            continue;
                        if (condition.DamageTypeId >= 0 && condition.DamageTypeId != damageTypeId)
                            continue;
                        matched = condition;
                        break;
                    }
                    if (matched == null || proc.EffectPlan is not MonsterTargetEffectNode plan)
                        continue;
                    executed |= ExecuteMonsterTargetEffectNode(plan, monster, target, 0, "MON-PROC", source ?? "ProcModifier::DoHitProc", out _, out _);
                }
                finally
                {
                    proc.Executing = false;
                }
            }
            return executed;
        }

        private bool DispatchMonsterDeathProcs(Monster monster, string source)
        {
            if (monster == null || monster.ProcModifiers.Count == 0)
                return false;
            bool executed = false;
            var removals = new List<MonsterProcModifierRuntime>();
            foreach (MonsterProcModifierRuntime proc in monster.ProcModifiers)
            {
                if (proc == null || proc.Executing)
                    continue;
                proc.Executing = true;
                try
                {
                    if (PassesMonsterProcChance(monster, proc, source))
                    {
                        MonsterProcConditionRuntime matched = null;
                        foreach (MonsterProcConditionRuntime condition in proc.Conditions)
                            if (condition.EventKind == MonsterProcEventKind.Death)
                            {
                                matched = condition;
                                break;
                            }
                        if (matched != null && proc.EffectPlan is MonsterTargetEffectNode plan)
                            executed |= ExecuteMonsterDeathProcEffectNode(plan, monster, source ?? "ProcModifier::DoDeathProc");
                    }
                    if (proc.RemoveOnDeath)
                        removals.Add(proc);
                }
                finally
                {
                    proc.Executing = false;
                }
            }
            foreach (MonsterProcModifierRuntime removal in removals)
                monster.ProcModifiers.Remove(removal);
            return executed;
        }

        public bool DispatchMonsterProjectileHitProcs(Monster monster, string projectileObjectPath, string source)
        {
            if (monster == null || monster.ProcModifiers.Count == 0)
                return false;
            if (!TryBuildProjectileObjectAncestry(projectileObjectPath, out HashSet<string> projectileAncestry))
                return false;
            bool executed = false;
            foreach (MonsterProcModifierRuntime proc in monster.ProcModifiers)
            {
                if (proc == null)
                    continue;
                if (!PassesMonsterProcChance(monster, proc, source))
                    continue;
                MonsterProcConditionRuntime matched = null;
                foreach (MonsterProcConditionRuntime condition in proc.Conditions)
                {
                    if (condition.EventKind != MonsterProcEventKind.ProjectileHit)
                        continue;
                    if (!string.IsNullOrWhiteSpace(condition.ProjectileObjectType)
                        && !projectileAncestry.Contains(condition.ProjectileObjectType))
                        continue;
                    matched = condition;
                    break;
                }
                if (matched == null || proc.EffectPlan is not MonsterSelfEffectNode plan)
                    continue;
                executed |= ExecuteMonsterProjectileHitSelfProcEffectNode(plan, proc, monster, source ?? "ProcModifier::OnProjectileHit");
            }
            return executed;
        }

        private bool PassesMonsterProcChance(Monster monster, MonsterProcModifierRuntime proc, string source)
        {
            if (proc.ChanceDivisor == 0)
                return false;
            if (proc.ChanceDivisor == 1)
                return true;
            MersenneTwister rng = GetRoomRngForMonster(monster);
            if (rng == null)
                return false;
            uint raw = RngLedger.Generate(rng, "room", $"{proc.ModifierPath}:ProcModifier::CheckChance", monster.InstanceKey ?? monster.ZoneName, monster.EntityId);
            return raw % unchecked((uint)proc.ChanceDivisor) == 0;
        }

        private bool ExecuteMonsterDeathProcEffectNode(MonsterTargetEffectNode plan, Monster sourceMonster, string source)
        {
            if (plan == null || sourceMonster == null)
                return false;
            if (plan.Kind == MonsterTargetEffectNodeKind.Sequence)
            {
                bool handled = true;
                foreach (MonsterTargetEffectNode child in plan.Children)
                    handled &= ExecuteMonsterDeathProcEffectNode(child, sourceMonster, source);
                return handled;
            }
            if (plan.Kind == MonsterTargetEffectNodeKind.EnemyAoe)
            {
                if (!PassesMonsterTargetEffectChance(sourceMonster, null, plan.EffectPath, plan.ChanceWire))
                    return true;
                List<CombatTarget> targets = CollectMonsterAuraEnemyTargets(sourceMonster, plan.RadiusF32, plan.MaxTargets);
                bool handled = true;
                foreach (CombatTarget target in targets)
                {
                    bool hpShifted = false;
                    foreach (MonsterTargetEffectNode child in plan.Children)
                    {
                        handled &= ExecuteMonsterTargetEffectNode(child, sourceMonster, target, 0, "MON-PROC", source, out bool childHpShifted, out _);
                        hpShifted |= childHpShifted;
                    }
                    if (hpShifted)
                        RaiseCombatTargetDamageResolved(sourceMonster, target, true, target.CurrentHPWire, source ?? "ProcModifier::DoDeathProc");
                }
                return handled;
            }
            if (plan.Kind == MonsterTargetEffectNodeKind.Spawn)
            {
                if (!PassesMonsterTargetEffectChance(sourceMonster, null, plan.EffectPath, plan.ChanceWire))
                    return true;
                SpawnMonsterFixed(
                    plan.SpawnUnitPath,
                    sourceMonster.PosFixedX,
                    sourceMonster.PosFixedY,
                    sourceMonster.PosFixedZ,
                    sourceMonster.HeadingFixed,
                    sourceMonster.ZoneName,
                    null,
                    sourceMonster.DifficultyF32,
                    null,
                    sourceMonster.InstanceKey);
                Debug.LogError($"[MON-SKILL-SPAWN] source={sourceMonster.Name}#{sourceMonster.EntityId} target=self unit={plan.SpawnUnitPath} position=({sourceMonster.PosFixedX},{sourceMonster.PosFixedY},{sourceMonster.PosFixedZ}) sourceFunction=ProcModifier::DoDeathProc@0x0056E460->SpellSpawnEffect::doEffect");
                return true;
            }
            if (!PassesMonsterTargetEffectChance(sourceMonster, null, plan.EffectPath, plan.ChanceWire))
                return true;
            Debug.LogError($"[MON-SKILL-VISUAL] source={sourceMonster.Name}#{sourceMonster.EntityId} target=self effect={plan.EffectPath ?? "none"} sourceFunction=ProcModifier::DoDeathProc@0x0056E460->SpellEffect::doEffect@0x00545DD0");
            return true;
        }

        private bool ExecuteMonsterProjectileHitSelfProcEffectNode(
            MonsterSelfEffectNode plan,
            MonsterProcModifierRuntime proc,
            Monster monster,
            string source)
        {
            if (plan == null || proc == null || monster == null)
                return false;
            if (plan.Kind == MonsterSelfEffectNodeKind.Sequence)
            {
                bool handled = true;
                foreach (MonsterSelfEffectNode child in plan.Children)
                    handled &= ExecuteMonsterProjectileHitSelfProcEffectNode(child, proc, monster, source);
                return handled;
            }
            if (!PassesMonsterSelfEffectChance(monster, monster, plan))
                return true;
            if (plan.Kind == MonsterSelfEffectNodeKind.HitPointRegen)
            {
                ApplyMonsterRegenBonusModifier(
                    monster,
                    plan.ModifierPath,
                    plan.HitPointRegenBonus,
                    plan.OverrideTable,
                    plan.DurationTicks,
                    plan.RemoveOnDeath);
                return true;
            }
            if (plan.Kind == MonsterSelfEffectNodeKind.AuthoredAttributeModifier)
                return ApplyMonsterAuthoredAttributeModifier(
                    monster,
                    plan.ModifierPath,
                    plan.Attributes,
                    plan.DurationTicks,
                    plan.RemoveOnDeath,
                    plan.StackRule,
                    unchecked((uint)Math.Max(0, plan.PowerLevelF32)),
                    monster.EntityId,
                    proc.ModifierPath,
                    plan.EffectPath,
                    proc.SkillLevel);
            Debug.LogError($"[MON-SKILL-VISUAL] source={monster.Name}#{monster.EntityId} target=self effect={plan.EffectPath ?? "none"} modifier={plan.ModifierPath ?? "none"} durationTicks={plan.DurationTicks} sourceFunction=ProcModifier::OnProjectileHit@0x0056EA70->SpellEffect::doEffect@0x00545DD0");
            return true;
        }

        private static bool TryBuildProjectileObjectAncestry(string projectileObjectPath, out HashSet<string> ancestry)
        {
            ancestry = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(projectileObjectPath))
                return false;
            GCDatabase gc = GCDatabase.Instance;
            GCNode projectile = gc?.Resolve(projectileObjectPath);
            if (projectile == null)
                return false;
            string canonicalPath = string.IsNullOrWhiteSpace(projectile.CanonicalPath)
                ? projectileObjectPath.Trim()
                : projectile.CanonicalPath;
            foreach (string path in gc.GetInheritanceChainPaths(canonicalPath, 64))
            {
                ancestry.Add(path);
                GCNode node = gc.Resolve(path);
                if (!string.IsNullOrWhiteSpace(node?.CanonicalPath))
                    ancestry.Add(node.CanonicalPath);
            }
            return ancestry.Count > 0;
        }
    }
}
