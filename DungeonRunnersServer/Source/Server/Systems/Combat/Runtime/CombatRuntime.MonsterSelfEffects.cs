using System;
using System.Collections.Generic;
using DungeonRunners.Core;
using DungeonRunners.Data;
using DungeonRunners.Engine;

namespace DungeonRunners.Combat
{
    public partial class CombatRuntime
    {
        private enum MonsterSelfEffectNodeKind : byte
        {
            Sequence,
            FriendAoe,
            EnemyAoe,
            Cowardice,
            HitPointRegen,
            AuthoredAttributeModifier,
            EnemyDamageAura,
            VisualOnly
        }

        private sealed class MonsterSelfEffectNode
        {
            public MonsterSelfEffectNodeKind Kind;
            public readonly List<MonsterSelfEffectNode> Children = new List<MonsterSelfEffectNode>();
            public string EffectPath;
            public string ModifierPath;
            public int ChanceWire = 0x6400;
            public int RadiusF32;
            public int MaxTargets = int.MaxValue;
            public int PowerLevelF32;
            public byte SkillLevel = 1;
            public int DurationTicks;
            public bool RemoveOnDeath;
            public bool AvoidCorners = true;
            public int HitPointRegenBonus;
            public bool OverrideTable;
            public string StackRule;
            public Dictionary<string, int> Attributes;
            public MonsterAuraPlan Aura;
            public MonsterTargetEffectNode EnemyEffect;
        }

        private sealed class MonsterSelfSkillPlan
        {
            public string SkillPath;
            public string EffectPath;
            public MonsterSelfEffectNode Root;
            public MonsterCastModifierPlan CastModifier;
        }

        private bool TryBuildMonsterSelfActiveSkillEffectPlan(Monster monster, out MonsterSelfSkillPlan plan, out string reason)
        {
            plan = null;
            reason = null;
            MonsterActiveSkillRuntime activeSkill = monster?.SelectedActiveSkill;
            if (activeSkill == null || string.IsNullOrWhiteSpace(activeSkill.Path))
            {
                reason = "missing-selected-skill";
                return false;
            }
            var gc = GCDatabase.Instance;
            if (gc == null || !gc.IsLoaded)
            {
                reason = "authored-database-unavailable";
                return false;
            }
            GCNode skill = gc.ResolveWithInheritance(activeSkill.Path);
            GCNode skillDesc = skill?.GetChild("Description") ?? skill;
            string effectPath = skillDesc?.GetString("Effect", activeSkill.Effect) ?? activeSkill.Effect;
            GCNode effect = ResolveAuthoredNodeReference(effectPath, skill);
            if (effect == null)
            {
                reason = "missing-authored-effect";
                return false;
            }
            SpellData spell = SpellDatabase.GetSpell(activeSkill.Path);
            if (spell == null)
            {
                reason = "missing-authored-spell";
                return false;
            }
            int skillLevel = Math.Max(1, (int)activeSkill.SkillLevel);
            int powerLevelF32 = spell.ResolvePowerLevelF32(skillLevel);
            if (!TryBuildMonsterSelfEffectNode(
                    effect,
                    effectPath,
                    skill,
                    skillLevel,
                    powerLevelF32,
                    out MonsterSelfEffectNode root,
                    out reason))
                return false;
            MonsterCastModifierPlan castModifier = null;
            if (!activeSkill.InstantUse
                && !string.IsNullOrWhiteSpace(activeSkill.CastModifier)
                && activeSkill.AddModifierWhileClosing)
            {
                reason = "self-cast-modifier-while-closing-unproven";
                return false;
            }
            if (!activeSkill.InstantUse
                && !string.IsNullOrWhiteSpace(activeSkill.CastModifier)
                && !TryBuildMonsterCastModifierPlan(
                    activeSkill.CastModifier,
                    skill,
                    activeSkill.Path,
                    skillLevel,
                    unchecked((uint)Math.Max(0, powerLevelF32)),
                    out castModifier,
                    out reason))
                return false;
            plan = new MonsterSelfSkillPlan
            {
                SkillPath = activeSkill.Path,
                EffectPath = effectPath,
                Root = root,
                CastModifier = castModifier
            };
            return true;
        }

        private bool TryBuildMonsterSelfEffectNode(
            GCNode authoredNode,
            string effectPath,
            GCNode skillContext,
            int skillLevel,
            int powerLevelF32,
            out MonsterSelfEffectNode plan,
            out string reason)
        {
            plan = null;
            reason = null;
            if (authoredNode == null)
            {
                reason = "missing-effect-node";
                return false;
            }
            authoredNode = GCDatabase.Instance.ResolveWithInheritance(authoredNode) ?? authoredNode;
            string nodePath = BuildAuthoredEffectPath(effectPath, authoredNode);
            if (AuthoredExtends(authoredNode, "SpellSoundEffect"))
            {
                plan = new MonsterSelfEffectNode
                {
                    Kind = MonsterSelfEffectNodeKind.VisualOnly,
                    EffectPath = nodePath,
                    ChanceWire = ResolveSpellEffectChanceWire(authoredNode)
                };
                return true;
            }
            if (AuthoredExtends(authoredNode, "SpellEffectEffect"))
            {
                plan = new MonsterSelfEffectNode
                {
                    Kind = MonsterSelfEffectNodeKind.VisualOnly,
                    EffectPath = nodePath,
                    ChanceWire = ResolveSpellEffectChanceWire(authoredNode)
                };
                return true;
            }
            if (AuthoredExtends(authoredNode, "SpellAOEEffect"))
            {
                string targetType = authoredNode.GetString("TargetType", "ENEMY");
                if (string.Equals(targetType, "ENEMY", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(targetType, "2", StringComparison.OrdinalIgnoreCase))
                {
                    var enemyEffect = new MonsterTargetEffectNode { Kind = MonsterTargetEffectNodeKind.Sequence };
                    foreach (GCNode child in authoredNode.EnumerateChildrenInOrder())
                    {
                        if (!TryBuildMonsterTargetEffectNode(child, nodePath, skillContext, skillContext.CanonicalPath,
                                skillLevel, unchecked((uint)powerLevelF32), out MonsterTargetEffectNode childPlan, out reason))
                            return false;
                        enemyEffect.Children.Add(childPlan);
                    }
                    plan = new MonsterSelfEffectNode
                    {
                        Kind = MonsterSelfEffectNodeKind.EnemyAoe,
                        EffectPath = nodePath,
                        ChanceWire = ResolveSpellEffectChanceWire(authoredNode),
                        RadiusF32 = unchecked(ResolveMonsterAoeRadiusF32(authoredNode, skillLevel) + 0xA00),
                        MaxTargets = ResolveMonsterAoeNumTargets(authoredNode, skillLevel),
                        EnemyEffect = enemyEffect
                    };
                    return true;
                }
                if (!string.Equals(targetType, "FRIEND", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(targetType, "1", StringComparison.OrdinalIgnoreCase))
                {
                    reason = $"unsupported-self-aoe-target:{targetType}";
                    return false;
                }
                int chanceWire = ResolveSpellEffectChanceWire(authoredNode);
                if (chanceWire != 0x6400)
                {
                    reason = "aoe-effect-chance-unproven";
                    return false;
                }
                int authoredRadiusF32 = ResolveMonsterAoeRadiusF32(authoredNode, skillLevel);
                if (authoredRadiusF32 <= 0)
                {
                    reason = "missing-positive-aoe-radius";
                    return false;
                }
                plan = new MonsterSelfEffectNode
                {
                    Kind = MonsterSelfEffectNodeKind.FriendAoe,
                    EffectPath = nodePath,
                    ChanceWire = chanceWire,
                    RadiusF32 = authoredRadiusF32 > int.MaxValue - 0xA00 ? int.MaxValue : authoredRadiusF32 + 0xA00,
                    MaxTargets = ResolveMonsterAoeNumTargets(authoredNode, skillLevel)
                };
                return TryBuildMonsterSelfEffectChildren(authoredNode, nodePath, skillContext, skillLevel, powerLevelF32, plan, out reason);
            }
            if (AuthoredExtends(authoredNode, "SpellModEffect"))
                return TryBuildMonsterSelfModifierNode(authoredNode, nodePath, skillContext, skillLevel, powerLevelF32, out plan, out reason);
            if (AuthoredExtends(authoredNode, "SpellEffect")
                || AuthoredExtends(authoredNode, "SpellSnapToGroundEffect")
                || string.IsNullOrWhiteSpace(authoredNode.Extends))
            {
                plan = new MonsterSelfEffectNode
                {
                    Kind = MonsterSelfEffectNodeKind.Sequence,
                    EffectPath = nodePath
                };
                return TryBuildMonsterSelfEffectChildren(authoredNode, nodePath, skillContext, skillLevel, powerLevelF32, plan, out reason);
            }
            reason = $"unsupported-self-effect-family:{authoredNode.Extends ?? "unknown"}";
            return false;
        }

        private bool TryBuildMonsterSelfEffectChildren(
            GCNode authoredNode,
            string effectPath,
            GCNode skillContext,
            int skillLevel,
            int powerLevelF32,
            MonsterSelfEffectNode parent,
            out string reason)
        {
            reason = null;
            foreach (GCNode child in authoredNode.EnumerateChildrenInOrder())
            {
                if (!TryBuildMonsterSelfEffectNode(child, effectPath, skillContext, skillLevel, powerLevelF32, out MonsterSelfEffectNode childPlan, out reason))
                    return false;
                parent.Children.Add(childPlan);
            }
            if (parent.Children.Count == 0)
            {
                reason = "effect-node-has-no-child-effect";
                return false;
            }
            return true;
        }

        private bool TryBuildMonsterSelfModifierNode(
            GCNode modEffect,
            string effectPath,
            GCNode skillContext,
            int skillLevel,
            int powerLevelF32,
            out MonsterSelfEffectNode plan,
            out string reason)
        {
            plan = null;
            reason = null;
            string modifierPath = modEffect.GetString("Modifier", null);
            GCNode modifier = ResolveAuthoredNodeReference(modifierPath, skillContext);
            GCNode modifierDesc = modifier?.GetChild("Description") ?? modifier;
            if (modifier == null || modifierDesc == null)
            {
                reason = "missing-authored-modifier";
                return false;
            }
            int durationF32 = ResolveMonsterSpellModDurationF32(modEffect, skillLevel);
            int durationTicks = ComputeSpellModDurationTicks(durationF32);
            int chanceWire = ResolveSpellEffectChanceWire(modEffect);
            if (AuthoredExtends(modifier, "AuraMod"))
                return TryBuildMonsterAuraEffectNode(
                    modEffect,
                    effectPath,
                    skillContext,
                    skillLevel,
                    powerLevelF32,
                    modifierPath,
                    modifier,
                    modifierDesc,
                    durationTicks,
                    chanceWire,
                    out plan,
                    out reason);
            if (AuthoredExtends(modifier, "CowardiceModifier"))
            {
                string stackRule = modifierDesc.GetString("StackRule", "UNIQUEBYTYPE");
                if (!string.Equals(stackRule, "UNIQUEBYTYPE", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(stackRule, "1", StringComparison.OrdinalIgnoreCase))
                {
                    reason = $"unsupported-cowardice-stack:{stackRule}";
                    return false;
                }
                if (modifierDesc.GetInt("TerminateWhenHitChance", 0) != 0)
                {
                    reason = "cowardice-terminate-when-hit-unimplemented";
                    return false;
                }
                plan = new MonsterSelfEffectNode
                {
                    Kind = MonsterSelfEffectNodeKind.Cowardice,
                    EffectPath = effectPath,
                    ModifierPath = modifierPath,
                    ChanceWire = chanceWire,
                    PowerLevelF32 = powerLevelF32,
                    DurationTicks = durationTicks,
                    RemoveOnDeath = modifierDesc.GetBool("RemoveOnDeath", false),
                    AvoidCorners = modifierDesc.GetBool("AvoidCorners", true)
                };
                return true;
            }
            if (durationTicks <= 0)
                durationTicks = 0;
            if (!AuthoredExtends(modifier, "AttributeModifier"))
            {
                reason = $"unsupported-self-modifier-family:{modifier.Extends ?? "unknown"}";
                return false;
            }
            var attributes = new List<GCNode>();
            CollectMonsterModifierAttributeNodes(modifierDesc, attributes, new HashSet<GCNode>());
            if (attributes.Count == 0)
            {
                reason = "empty-attribute-modifier";
                return false;
            }
            var authoredAttributes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            bool onlyHitPointRegen = true;
            foreach (GCNode attribute in attributes)
            {
                string attributeName = attribute.GetString("Attribute", null);
                if (string.IsNullOrWhiteSpace(attributeName))
                {
                    reason = "missing-self-attribute-name";
                    return false;
                }
                if (!IsMonsterAuthoredAttributeSupported(attributeName))
                {
                    reason = $"unsupported-self-attribute:{attributeName}";
                    return false;
                }
                int value = ResolveMonsterAttributeLevelValue(attribute, skillLevel, unchecked((uint)powerLevelF32));
                authoredAttributes[attributeName] = value;
                onlyHitPointRegen &= string.Equals(attributeName, "HIT_POINT_REGEN_BONUS", StringComparison.OrdinalIgnoreCase);
            }
            if (onlyHitPointRegen && authoredAttributes.Count == 1 && authoredAttributes.TryGetValue("HIT_POINT_REGEN_BONUS", out int regenValue))
            {
                plan = new MonsterSelfEffectNode
                {
                    Kind = MonsterSelfEffectNodeKind.HitPointRegen,
                    EffectPath = effectPath,
                    ModifierPath = modifierPath,
                    ChanceWire = chanceWire,
                    DurationTicks = durationTicks,
                    RemoveOnDeath = modifierDesc.GetBool("RemoveOnDeath", false),
                    HitPointRegenBonus = regenValue,
                    OverrideTable = attributes[0].GetBool("OverrideTable", false)
                };
                return true;
            }
            plan = new MonsterSelfEffectNode
            {
                Kind = MonsterSelfEffectNodeKind.AuthoredAttributeModifier,
                SkillLevel = unchecked((byte)skillLevel),
                EffectPath = effectPath,
                ModifierPath = modifierPath,
                ChanceWire = chanceWire,
                DurationTicks = durationTicks,
                RemoveOnDeath = modifierDesc.GetBool("RemoveOnDeath", false),
                PowerLevelF32 = powerLevelF32,
                StackRule = modifierDesc.GetString("StackRule", ""),
                Attributes = authoredAttributes
            };
            return true;
        }

        private static bool IsMonsterAuthoredAttributeSupported(string attribute)
        {
            switch ((attribute ?? string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty).Trim().ToUpperInvariant())
            {
                case "HITPOINTREGENBONUS":
                case "DAMAGEIMMUNITY":
                case "SPEEDMOD":
                case "SIZEMOD":
                case "ATTACKSPEEDMOD":
                case "DAMAGEMOD":
                case "MELEEDAMAGEMOD":
                case "ATTACKRATINGMOD":
                case "DEFENSERATINGMOD":
                    return true;
                default:
                    return false;
            }
        }

        private static void CollectMonsterModifierAttributeNodes(GCNode node, List<GCNode> result, HashSet<GCNode> visited)
        {
            if (node == null || !visited.Add(node))
                return;
            if (AuthoredExtends(node, "Attribute") || node.HasProperty("Attribute"))
                result.Add(node);
            foreach (GCNode child in node.EnumerateChildrenInOrder())
                CollectMonsterModifierAttributeNodes(child, result, visited);
        }

        private static int ResolveMonsterSpellModDurationF32(GCNode modEffect, int skillLevel)
        {
            long duration = modEffect.GetFixed32("Duration", 0);
            long increment = modEffect.GetFixed32("DurationInc", 0);
            long resolved = duration + (long)Math.Max(0, skillLevel - 1) * increment;
            if (resolved <= 0)
                return 0;
            return resolved >= int.MaxValue ? int.MaxValue : (int)resolved;
        }

        private static int ResolveMonsterAttributeLevelValue(GCNode attribute, int skillLevel, uint powerLevel)
        {
            int inputF32 = attribute.GetBool("UsePowerLevel", false)
                ? unchecked((int)powerLevel) : unchecked(skillLevel << 8);
            if (!attribute.GetBool("OverrideTable", false))
            {
                var curve = new List<(int Level, int Value)>();
                foreach (GCNode child in attribute.EnumerateChildrenInOrder())
                    if (AuthoredExtends(child, "CurveTableEntry"))
                        curve.Add((child.GetFixed32("Level", 0), child.GetFixed32("Value", 0)));
                if (curve.Count > 0)
                {
                    curve.Sort((a, b) => a.Level.CompareTo(b.Level));
                    int valueF32 = curve[curve.Count - 1].Value;
                    if (inputF32 <= curve[0].Level)
                        valueF32 = curve[0].Value;
                    else for (int i = 1; i < curve.Count; i++)
                    {
                        if (inputF32 > curve[i].Level)
                            continue;
                        var lo = curve[i - 1];
                        var hi = curve[i];
                        int numerator = unchecked((int)(((long)unchecked(inputF32 - lo.Level) * 0x10000) >> 8));
                        int ratioF32 = unchecked((int)(((long)numerator << 8) / unchecked(hi.Level - lo.Level)));
                        int product = unchecked((int)(((long)unchecked(hi.Value - lo.Value) * Math.Abs((long)ratioF32)) >> 8));
                        valueF32 = unchecked(lo.Value + (int)(((long)product << 8) / 0x10000));
                        break;
                    }
                    if ((valueF32 & 0xFF) >= 0x7F)
                        valueF32 = unchecked(valueF32 + 0x100);
                    return unchecked((short)(valueF32 >> 8));
                }
            }
            int inputOffsetF32 = inputF32 >= 0x100 ? inputF32 - 0x100 : inputF32;
            int increment = unchecked((int)(((long)inputOffsetF32 * attribute.GetFixed32("ValueInc", 0)) >> 8));
            int resolved = unchecked(attribute.GetFixed32("Value", 0) + increment);
            return unchecked((short)(resolved >> 8));
        }

        private static int ResolveMonsterAoeRadiusF32(GCNode aoe, int skillLevel)
        {
            return aoe.GetFixed32("RadiusMin", 0x3200);
        }

        private static int ResolveMonsterAoeNumTargets(GCNode aoe, int skillLevel)
        {
            int minimum = unchecked((byte)aoe.GetInt("NumTargetsMin", 0));
            int maximum = unchecked((byte)aoe.GetInt("NumTargetsMax", 0));
            int incrementF32 = aoe.GetFixed32("NumTargetsInc", 0);
            int increment = unchecked((int)(((long)unchecked(skillLevel << 8) * incrementF32) >> 8));
            int count = Math.Min(unchecked((minimum << 8) + increment) >> 8, maximum);
            return count <= 0 ? int.MaxValue : count;
        }

        private bool ExecuteMonsterSelfEffectPlan(MonsterSelfEffectNode plan, Monster sourceMonster, Monster target)
        {
            if (plan == null || sourceMonster == null || target == null)
                return false;
            if (plan.Kind == MonsterSelfEffectNodeKind.Sequence)
            {
                bool handled = true;
                foreach (MonsterSelfEffectNode child in plan.Children)
                    handled &= ExecuteMonsterSelfEffectPlan(child, sourceMonster, target);
                return handled;
            }
            if (plan.Kind == MonsterSelfEffectNodeKind.FriendAoe)
            {
                List<Monster> targets = CollectMonsterFriendAoeTargets(target, plan.RadiusF32, plan.MaxTargets);
                Debug.LogError($"[MON-FRIEND-AOE] source={sourceMonster.Name}#{sourceMonster.EntityId} center={target.Name}#{target.EntityId} effect={plan.EffectPath ?? "none"} radiusF32={plan.RadiusF32} maxTargets={plan.MaxTargets} targets={string.Join(",", targets.ConvertAll(candidate => candidate.EntityId.ToString()))} sourceFunction=SpellAOEEffect::doEffect@0x00549250->UnitFinder2::findFriends@0x00510E50");
                bool handled = true;
                foreach (Monster friend in targets)
                    foreach (MonsterSelfEffectNode child in plan.Children)
                        handled &= ExecuteMonsterSelfEffectPlan(child, sourceMonster, friend);
                return handled;
            }
            if (!PassesMonsterSelfEffectChance(sourceMonster, target, plan))
                return true;
            if (plan.Kind == MonsterSelfEffectNodeKind.EnemyAoe)
            {
                bool handled = true;
                foreach (CombatTarget enemy in CollectMonsterEnemyAoeTargets(sourceMonster,
                             target.PosFixedX, target.PosFixedY, target.PosFixedZ, plan.RadiusF32, plan.MaxTargets))
                {
                    bool applied = ExecuteMonsterTargetEffectNode(plan.EnemyEffect, sourceMonster, enemy, 0,
                        "MON-SELF-AOE", "SpellAOEEffect", out bool hpShifted, out bool deferred);
                    handled &= applied;
                    enemy.IsAlive = enemy.PlayerState.CurrentHPWire > 0;
                    if (applied && !deferred)
                        OnMonsterAttackResolved?.Invoke(sourceMonster, enemy, hpShifted, enemy.PlayerState.CurrentHPWire);
                }
                return handled;
            }
            if (plan.Kind == MonsterSelfEffectNodeKind.Cowardice)
                return ApplyMonsterCowardiceModifier(
                    sourceMonster,
                    target,
                    sourceMonster.SelectedActiveSkill?.Path,
                    plan.EffectPath,
                    plan.ModifierPath,
                    plan.PowerLevelF32,
                    plan.DurationTicks,
                    plan.RemoveOnDeath,
                    plan.AvoidCorners);
            if (plan.Kind == MonsterSelfEffectNodeKind.HitPointRegen)
            {
                ApplyMonsterRegenBonusModifier(
                    target,
                    plan.ModifierPath,
                    plan.HitPointRegenBonus,
                    plan.OverrideTable,
                    plan.DurationTicks,
                    plan.RemoveOnDeath);
                return true;
            }
            if (plan.Kind == MonsterSelfEffectNodeKind.AuthoredAttributeModifier)
                return ApplyMonsterAuthoredAttributeModifier(
                    target,
                    plan.ModifierPath,
                    plan.Attributes,
                    plan.DurationTicks,
                    plan.RemoveOnDeath,
                    plan.StackRule,
                    unchecked((uint)Math.Max(0, plan.PowerLevelF32)),
                    sourceMonster.EntityId,
                    sourceMonster.SelectedActiveSkill?.Path,
                    plan.EffectPath,
                    plan.SkillLevel);
            if (plan.Kind == MonsterSelfEffectNodeKind.EnemyDamageAura)
                return ApplyMonsterAuraModifier(sourceMonster, plan.Aura);
            Debug.LogError($"[MON-SKILL-VISUAL] source={sourceMonster.Name}#{sourceMonster.EntityId} target={target.Name}#{target.EntityId} effect={plan.EffectPath ?? "none"} modifier={plan.ModifierPath ?? "none"} durationTicks={plan.DurationTicks} sourceFunction=SpellEffect::doEffect@0x00545DD0");
            return true;
        }

        private bool PassesMonsterSelfEffectChance(Monster sourceMonster, Monster target, MonsterSelfEffectNode plan)
        {
            int chanceWire = Math.Clamp(plan?.ChanceWire ?? 0x6400, 0, 0x6400);
            if (chanceWire >= 0x6400)
                return true;
            MersenneTwister rng = GetRoomRngForMonster(sourceMonster);
            if (rng == null)
                return false;
            uint raw = RngLedger.Generate(rng, "room", $"MON-SELF:{plan.EffectPath ?? "effect"}:SpellEffect::CheckChance", sourceMonster.InstanceKey ?? sourceMonster.ZoneName, sourceMonster.EntityId);
            uint roll = raw % 0x6464u;
            bool passed = roll < (uint)chanceWire;
            Debug.LogError($"[MON-SKILL-CHANCE] source={sourceMonster.Name}#{sourceMonster.EntityId} target={target.Name}#{target.EntityId} effect={plan.EffectPath ?? "none"} chanceWire={chanceWire} raw=0x{raw:X8} roll={roll} passed={passed} sourceFunction=SpellEffect::CheckChance@0x00545FF0");
            return passed;
        }
    }
}
