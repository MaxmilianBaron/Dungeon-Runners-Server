using System;
using System.Collections.Generic;
using DungeonRunners.Core;
using DungeonRunners.Data;
using DungeonRunners.Engine;
using DungeonRunners.Utilities;

namespace DungeonRunners.Combat
{
    public partial class CombatRuntime
    {
        private enum MonsterTargetEffectNodeKind : byte
        {
            Sequence,
            VisualOnly,
            Projectile,
            SpellDamage,
            WeaponDamage,
            StunAction,
            AttributeModifier,
            DamageModifier,
            PlayerAura,
            EnemyAoe,
            Spawn,
            Chain
        }

        private sealed class MonsterTargetSkillPlan
        {
            public string SkillPath;
            public string EffectPath;
            public MonsterTargetEffectNode Root;
            public MonsterCastModifierPlan CastModifier;
        }

        private sealed class MonsterTargetEffectNode
        {
            public MonsterTargetEffectNodeKind Kind;
            public readonly List<MonsterTargetEffectNode> Children = new List<MonsterTargetEffectNode>();
            public string EffectPath;
            public int ChanceWire = 0x6400;
            public bool AffectsFriends = true;
            public int RadiusF32;
            public int MaxTargets = int.MaxValue;
            public MonsterSpellDamagePlan Damage;
            public MonsterWeaponDamageSkillEffect WeaponDamage;
            public MonsterStunActionSkillEffect StunAction;
            public MonsterAttributeModifierPlan AttributeModifier;
            public MonsterDamageModifierSkillEffect DamageModifier;
            public MonsterAuraPlan Aura;
            public MonsterProjectilePlan Projectile;
            public MonsterChainPlan Chain;
            public string SpawnUnitPath;
        }

        private sealed class MonsterSpellDamagePlan
        {
            public string SkillPath;
            public string EffectPath;
            public string AttackType;
            public string DamageType;
            public int DamageTypeId;
            public byte DamageKind;
            public int DamageModF32;
            public int DamageVolatilityF32;
            public int ChanceWire;
            public int CriticalChanceF32;
            public int DamageStunMod;
        }

        private sealed class MonsterAttributeModifierPlan
        {
            public string SkillPath;
            public string EffectPath;
            public string ModifierPath;
            public string StackRule;
            public ushort DurationTicks;
            public bool RemoveOnDeath;
            public int TerminateWhenHitChance;
            public uint PowerLevel;
            public byte SkillLevel;
            public Dictionary<string, int> Attributes;
        }

        private sealed class MonsterCastModifierPlan
        {
            public string ModifierPath;
            public string StackRule;
            public int DurationTicks;
            public bool RemoveOnDeath;
            public uint PowerLevel;
            public bool VisualOnly;
            public string VisualPath;
            public Dictionary<string, int> Attributes;
        }

        private sealed class MonsterProjectilePlan
        {
            public string ProjectilePath;
            public int SpeedF32;
            public int SizeF32;
            public int OffsetF32;
            public byte Lifespan;
            public int DelayTicks;
            public byte Gravity;
            public bool Penetrate;
            public int MaxPenetrations;
            public int ProjectileFilter;
            public bool IgnoreCollisions;
            public bool SnapToGround;
            public bool SeekTargets;
            public int SeekDistanceF32;
            public int TurnRateF32;
            public MonsterTargetEffectNode Impact;
        }

        private sealed class MonsterChainPlan
        {
            public string ProjectilePath;
            public int NumChains;
            public int ChainRangeF32;
            public int ChainDelayTicks;
            public int ChainLifespanTicks;
            public int NumForks;
            public bool SkipSourceChain;
            public MonsterTargetEffectNode Impact;
        }

        private sealed class PendingMonsterChainRuntime
        {
            public long Sequence;
            public Monster SourceMonster;
            public uint SourceMonsterEntityId;
            public string InstanceKey;
            public string ZoneName;
            public string Marker;
            public string Source;
            public MonsterChainPlan Plan;
            public readonly List<MonsterChainBranch> Branches = new List<MonsterChainBranch>();
        }

        private sealed class MonsterChainBranch
        {
            public uint TargetEntityId;
            public int RemainingLifespanTicks;
            public int Generation;
        }

        private readonly List<PendingMonsterChainRuntime> _pendingMonsterChains = new List<PendingMonsterChainRuntime>();
        private long _nextMonsterChainSequence;

        private bool CanExecuteMonsterTargetActiveSkillEffect(Monster monster, out string reason)
        {
            if (!TryBuildMonsterTargetActiveSkillEffectPlan(monster, out MonsterTargetSkillPlan plan, out reason))
                return false;
            monster.ActiveSkillEffectPlan = plan;
            return true;
        }

        private bool TryBuildMonsterTargetActiveSkillEffectPlan(Monster monster, out MonsterTargetSkillPlan plan, out string reason)
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
            uint powerLevel = unchecked((uint)Math.Max(0, spell.ResolvePowerLevelF32(skillLevel)));
            if (!TryBuildMonsterTargetEffectNode(effect, effectPath, skill, activeSkill.Path, skillLevel, powerLevel, out MonsterTargetEffectNode root, out reason))
                return false;
            MonsterCastModifierPlan castModifier = null;
            if (!activeSkill.InstantUse
                && !string.IsNullOrWhiteSpace(activeSkill.CastModifier)
                && !TryBuildMonsterCastModifierPlan(activeSkill.CastModifier, skill, activeSkill.Path, skillLevel, powerLevel, out castModifier, out reason))
                return false;
            plan = new MonsterTargetSkillPlan
            {
                SkillPath = activeSkill.Path,
                EffectPath = effectPath,
                Root = root,
                CastModifier = castModifier
            };
            return true;
        }

        private bool TryBuildMonsterTargetEffectNode(
            GCNode authoredNode,
            string effectPath,
            GCNode skillContext,
            string skillPath,
            int skillLevel,
            uint powerLevel,
            out MonsterTargetEffectNode plan,
            out string reason)
        {
            plan = null;
            reason = null;
            if (authoredNode == null)
            {
                reason = "missing-effect-node";
                return false;
            }
            GCNode node = GCDatabase.Instance.ResolveWithInheritance(authoredNode) ?? authoredNode;
            string nodePath = BuildAuthoredEffectPath(effectPath, authoredNode);
            if (AuthoredExtends(node, "SpellSoundEffect") || AuthoredExtends(node, "SpellEffectEffect"))
            {
                plan = new MonsterTargetEffectNode
                {
                    Kind = MonsterTargetEffectNodeKind.VisualOnly,
                    EffectPath = nodePath,
                    ChanceWire = ResolveSpellEffectChanceWire(node)
                };
                return true;
            }
            if (AuthoredExtends(node, "SpellProjectileEffect"))
                return TryBuildMonsterProjectileEffectNode(node, nodePath, skillContext, skillPath, skillLevel, powerLevel, out plan, out reason);
            if (AuthoredExtends(node, "SpellDamageEffect"))
            {
                SpellDamageEffectData damageInputs = SpellDamageEffectData.Read(node);
                int damageModF32 = damageInputs.DamageModF32;
                plan = new MonsterTargetEffectNode
                {
                    Kind = MonsterTargetEffectNodeKind.SpellDamage,
                    EffectPath = nodePath,
                    AffectsFriends = node.GetBool("AffectsFriends", true),
                    Damage = new MonsterSpellDamagePlan
                    {
                        SkillPath = skillPath,
                        EffectPath = nodePath,
                        AttackType = ((AttackType)damageInputs.AttackTypeId).ToString(),
                        DamageType = ((DamageElement)damageInputs.DamageTypeId).ToString(),
                        DamageTypeId = damageInputs.DamageTypeId,
                        DamageKind = damageInputs.AttackTypeId,
                        DamageModF32 = damageModF32,
                        DamageVolatilityF32 = damageInputs.DamageVolatilityF32,
                        ChanceWire = damageInputs.ChanceF32,
                        CriticalChanceF32 = damageInputs.CriticalChanceF32,
                        DamageStunMod = damageInputs.StunMod
                    }
                };
                return true;
            }
            if (AuthoredExtends(node, "SpellWeaponDamageEffect"))
            {
                var weapon = new MonsterWeaponDamageSkillEffect
                {
                    SkillPath = skillPath,
                    EffectPath = effectPath,
                    WeaponEffectPath = nodePath,
                    WeaponEffectChanceWire = ResolveSpellEffectChanceWire(node),
                    AttackRatingMod = ResolveSkillLinearMod(node, "ARMod", skillLevel),
                    DamageMod = ResolveSkillLinearMod(node, "DamageMod", skillLevel)
                };
                plan = new MonsterTargetEffectNode
                {
                    Kind = MonsterTargetEffectNodeKind.WeaponDamage,
                    EffectPath = nodePath,
                    WeaponDamage = weapon
                };
                string nestedEffectPath = node.GetString("Effect", null);
                if (!string.IsNullOrWhiteSpace(nestedEffectPath))
                {
                    GCNode nestedEffect = ResolveAuthoredNodeReference(nestedEffectPath, skillContext);
                    if (nestedEffect == null
                        || !TryBuildMonsterTargetEffectNode(nestedEffect, nestedEffectPath, skillContext, skillPath, skillLevel, powerLevel, out MonsterTargetEffectNode nestedPlan, out reason))
                    {
                        reason ??= "missing-weapon-impact-effect";
                        return false;
                    }
                    plan.Children.Add(nestedPlan);
                }
                return true;
            }
            if (AuthoredExtends(node, "SpellKnockBackEffect") || AuthoredExtends(node, "SpellKnockDownEffect"))
            {
                bool knockDown = AuthoredExtends(node, "SpellKnockDownEffect");
                plan = new MonsterTargetEffectNode
                {
                    Kind = MonsterTargetEffectNodeKind.StunAction,
                    EffectPath = nodePath,
                    StunAction = new MonsterStunActionSkillEffect
                    {
                        SkillPath = skillPath,
                        EffectPath = effectPath,
                        ActionEffectPath = nodePath,
                        ActionFamily = knockDown ? "SpellKnockDownEffect" : "SpellKnockBackEffect",
                        Strength = ResolveSkillLinearMod(node, "Strength", skillLevel),
                        ChanceWire = ResolveSpellEffectChanceWire(node),
                        IsKnockDown = knockDown
                    }
                };
                return true;
            }
            if (AuthoredExtends(node, "SpellModEffect"))
            {
                if (!TryBuildMonsterTargetModifierNode(node, nodePath, skillContext, skillPath, skillLevel, powerLevel, out plan, out reason))
                    return false;
                plan.AffectsFriends = node.GetBool("AffectsFriends", true);
                return true;
            }
            if (AuthoredExtends(node, "SpellAOEEffect"))
            {
                string targetType = node.GetString("TargetType", "ENEMY");
                if (!string.Equals(targetType, "ENEMY", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(targetType, "2", StringComparison.OrdinalIgnoreCase))
                {
                    reason = $"unsupported-target-aoe-type:{targetType}";
                    return false;
                }
                int radiusF32 = ResolveMonsterAoeRadiusF32(node, skillLevel);
                if (radiusF32 <= 0)
                {
                    reason = "missing-positive-target-aoe-radius";
                    return false;
                }
                plan = new MonsterTargetEffectNode
                {
                    Kind = MonsterTargetEffectNodeKind.EnemyAoe,
                    EffectPath = nodePath,
                    ChanceWire = ResolveSpellEffectChanceWire(node),
                    RadiusF32 = radiusF32 > int.MaxValue - 0xA00 ? int.MaxValue : radiusF32 + 0xA00,
                    MaxTargets = ResolveMonsterAoeNumTargets(node, skillLevel)
                };
                return TryBuildMonsterTargetEffectChildren(node, nodePath, skillContext, skillPath, skillLevel, powerLevel, plan, out reason);
            }
            if (AuthoredExtends(node, "SpellBurstEffect"))
            {
                int countMinF32 = node.GetFixed32("BurstCountMin", 0x100);
                int countMaxF32 = node.GetFixed32("BurstCountMax", countMinF32);
                if (countMinF32 != 0x100 || countMaxF32 != 0x100)
                {
                    reason = $"unsupported-burst-count:{countMinF32}:{countMaxF32}";
                    return false;
                }
                plan = new MonsterTargetEffectNode
                {
                    Kind = MonsterTargetEffectNodeKind.Sequence,
                    EffectPath = nodePath
                };
                return TryBuildMonsterTargetEffectChildren(node, nodePath, skillContext, skillPath, skillLevel, powerLevel, plan, out reason);
            }
            if (AuthoredExtends(node, "SpellSpawnEffect"))
            {
                string spawnUnit = node.GetString("SpawnUnit", null);
                if (string.IsNullOrWhiteSpace(spawnUnit) || GCDatabase.Instance.ResolveWithInheritance(spawnUnit) == null)
                {
                    reason = "missing-spawn-unit";
                    return false;
                }
                plan = new MonsterTargetEffectNode
                {
                    Kind = MonsterTargetEffectNodeKind.Spawn,
                    EffectPath = nodePath,
                    ChanceWire = ResolveSpellEffectChanceWire(node),
                    SpawnUnitPath = spawnUnit
                };
                return true;
            }
            if (AuthoredExtends(node, "SpellChainEffect"))
                return TryBuildMonsterChainEffectNode(node, nodePath, skillContext, skillPath, skillLevel, powerLevel, out plan, out reason);
            if (AuthoredExtends(node, "SpellSnapToGroundEffect")
                || AuthoredExtends(node, "SpellEffect")
                || string.IsNullOrWhiteSpace(node.Extends))
            {
                plan = new MonsterTargetEffectNode
                {
                    Kind = MonsterTargetEffectNodeKind.Sequence,
                    EffectPath = nodePath
                };
                return TryBuildMonsterTargetEffectChildren(node, nodePath, skillContext, skillPath, skillLevel, powerLevel, plan, out reason);
            }
            reason = $"unsupported-target-effect-family:{node.Extends ?? "unknown"}";
            return false;
        }

        private bool TryBuildMonsterTargetEffectChildren(
            GCNode authoredNode,
            string effectPath,
            GCNode skillContext,
            string skillPath,
            int skillLevel,
            uint powerLevel,
            MonsterTargetEffectNode parent,
            out string reason)
        {
            reason = null;
            foreach (GCNode child in authoredNode.EnumerateChildrenInOrder())
            {
                if (!TryBuildMonsterTargetEffectNode(child, effectPath, skillContext, skillPath, skillLevel, powerLevel, out MonsterTargetEffectNode childPlan, out reason))
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

        private bool TryBuildMonsterChainEffectNode(
            GCNode chainEffect,
            string effectPath,
            GCNode skillContext,
            string skillPath,
            int skillLevel,
            uint powerLevel,
            out MonsterTargetEffectNode plan,
            out string reason)
        {
            plan = null;
            reason = null;
            string targetType = chainEffect.GetString("TargetType", "ENEMY");
            if (!string.Equals(targetType, "ENEMY", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(targetType, "2", StringComparison.OrdinalIgnoreCase))
            {
                reason = $"unsupported-chain-target-type:{targetType}";
                return false;
            }
            string projectilePath = chainEffect.GetString("ChainProjectile", null);
            GCNode projectile = ResolveAuthoredNodeReference(projectilePath, skillContext);
            GCNode projectileDesc = ResolveMonsterModifierDescription(projectile);
            if (projectile == null || projectileDesc == null)
            {
                reason = "missing-chain-projectile-description";
                return false;
            }
            string impactEffectPath = projectileDesc.GetString("Effect", null);
            GCNode impactEffect = ResolveAuthoredNodeReference(impactEffectPath, projectile ?? skillContext);
            if (impactEffect == null
                || !TryBuildMonsterTargetEffectNode(impactEffect, impactEffectPath, projectile ?? skillContext, skillPath, skillLevel, powerLevel, out MonsterTargetEffectNode impactPlan, out reason))
            {
                reason ??= "missing-chain-impact-effect";
                return false;
            }
            int numChains = projectileDesc.GetInt("NumChains", 3);
            int numChainsIncrementF32 = projectileDesc.GetFixed32("NumChainsIncrement", 0);
            if (numChainsIncrementF32 != 0)
                numChains = Math.Max(0, numChains + ((skillLevel * numChainsIncrementF32) >> 8));
            int chainRangeF32 = projectileDesc.GetFixed32("ChainRange", 50 * 0x100);
            int chainLifespanTicks = projectileDesc.GetInt("ChainLifespan", 30);
            int numForks = projectileDesc.GetInt("NumForks", 1);
            if (numChains < 0 || chainRangeF32 <= 0 || chainLifespanTicks <= 0 || numForks <= 0)
            {
                reason = $"invalid-chain-geometry:{numChains}:{chainRangeF32}:{chainLifespanTicks}:{numForks}";
                return false;
            }
            plan = new MonsterTargetEffectNode
            {
                Kind = MonsterTargetEffectNodeKind.Chain,
                EffectPath = effectPath,
                ChanceWire = ResolveSpellEffectChanceWire(chainEffect),
                Chain = new MonsterChainPlan
                {
                    ProjectilePath = projectilePath,
                    NumChains = numChains,
                    ChainRangeF32 = chainRangeF32,
                    ChainDelayTicks = Math.Max(0, projectileDesc.GetInt("ChainDelay", 10)),
                    ChainLifespanTicks = chainLifespanTicks,
                    NumForks = numForks,
                    SkipSourceChain = projectileDesc.GetBool("SkipSourceChain", false),
                    Impact = impactPlan
                }
            };
            return true;
        }

        private bool TryBuildMonsterProjectileEffectNode(
            GCNode projectileEffect,
            string effectPath,
            GCNode skillContext,
            string skillPath,
            int skillLevel,
            uint powerLevel,
            out MonsterTargetEffectNode plan,
            out string reason)
        {
            plan = null;
            reason = null;
            string projectilePath = projectileEffect.GetString("Projectile", null);
            GCNode projectile = ResolveAuthoredNodeReference(projectilePath, skillContext);
            GCNode projectileDesc = ResolveMonsterModifierDescription(projectile);
            if (projectile == null || projectileDesc == null)
            {
                reason = "missing-projectile-description";
                return false;
            }
            int speedF32 = projectileDesc.GetFixed32("ProjectileSpeed", 0);
            int sizeF32 = projectileDesc.GetFixed32("ProjectileSize", 0);
            if (speedF32 <= 0 || sizeF32 <= 0)
            {
                reason = $"invalid-projectile-geometry:{speedF32}:{sizeF32}";
                return false;
            }
            string impactEffectPath = projectileDesc.GetString("Effect", null);
            GCNode impactEffect = ResolveAuthoredNodeReference(impactEffectPath, projectile ?? skillContext);
            if (impactEffect == null
                || !TryBuildMonsterTargetEffectNode(impactEffect, impactEffectPath, projectile ?? skillContext, skillPath, skillLevel, powerLevel, out MonsterTargetEffectNode impactPlan, out reason))
            {
                reason ??= "missing-projectile-impact-effect";
                return false;
            }
            plan = new MonsterTargetEffectNode
            {
                Kind = MonsterTargetEffectNodeKind.Projectile,
                EffectPath = effectPath,
                ChanceWire = ResolveSpellEffectChanceWire(projectileEffect),
                Projectile = new MonsterProjectilePlan
                {
                    ProjectilePath = projectile.CanonicalPath ?? projectilePath,
                    SpeedF32 = speedF32,
                    SizeF32 = sizeF32,
                    OffsetF32 = Math.Max(0, projectileDesc.GetFixed32("ProjectileOffset", 5 * 0x100)),
                    Lifespan = unchecked((byte)projectileDesc.GetInt("ProjectileLifespan", 90)),
                    DelayTicks = Math.Max(0, projectileDesc.GetInt("ProjectileDelay", 0)),
                    Gravity = unchecked((byte)projectileDesc.GetInt("ProjectileGravity", 0)),
                    Penetrate = projectileDesc.GetBool("Penetrate", false),
                    MaxPenetrations = unchecked((byte)projectileDesc.GetInt("MaxPenetrations", 50)),
                    ProjectileFilter = projectileDesc.GetInt("ProjectileFilter", -1),
                    IgnoreCollisions = projectileDesc.GetBool("IgnoreCollisions", false),
                    SnapToGround = projectileDesc.GetBool("SnapToGround", false),
                    SeekTargets = projectileDesc.GetBool("SeekTargets", false),
                    SeekDistanceF32 = Math.Max(0, projectileDesc.GetFixed32("SeekDistance", 100 * 0x100)),
                    TurnRateF32 = projectileDesc.GetFixed32("TurnRate", 30 * 0x100),
                    Impact = impactPlan
                }
            };
            return true;
        }

        private bool TryBuildMonsterTargetModifierNode(
            GCNode modEffect,
            string effectPath,
            GCNode skillContext,
            string skillPath,
            int skillLevel,
            uint powerLevel,
            out MonsterTargetEffectNode plan,
            out string reason)
        {
            plan = null;
            reason = null;
            string modifierPath = modEffect.GetString("Modifier", null);
            GCNode modifier = ResolveAuthoredNodeReference(modifierPath, skillContext);
            GCNode modifierDesc = ResolveMonsterModifierDescription(modifier);
            if (modifier == null || modifierDesc == null)
            {
                reason = "missing-target-modifier";
                return false;
            }
            if (AuthoredExtends(modifier, "AuraMod"))
            {
                int auraDurationF32 = ResolveMonsterSpellModDurationF32(modEffect, skillLevel);
                if (!TryBuildMonsterAuraPlan(
                    skillContext,
                    skillLevel,
                    unchecked((int)Math.Min(int.MaxValue, powerLevel)),
                    modifierPath,
                    modifier,
                    modifierDesc,
                    ComputeSpellModDurationTicks(auraDurationF32),
                    out MonsterAuraPlan aura,
                    out reason))
                    return false;
                if (!string.Equals(aura.TargetType, "FRIENDSELF", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(aura.TargetType, "11", StringComparison.OrdinalIgnoreCase))
                {
                    reason = $"unsupported-target-aura-target:{aura.TargetType}";
                    return false;
                }
                aura.DamageEffect.SkillPath = skillPath;
                plan = new MonsterTargetEffectNode
                {
                    Kind = MonsterTargetEffectNodeKind.PlayerAura,
                    EffectPath = effectPath,
                    ChanceWire = ResolveSpellEffectChanceWire(modEffect),
                    Aura = aura
                };
                return true;
            }
            if (AuthoredExtends(modifier, "EffectMod"))
            {
                if (!TryResolveMonsterDamageModifierSkillEffect(modEffect, skillContext, skillPath, effectPath, out MonsterDamageModifierSkillEffect damageModifier))
                {
                    reason = "unsupported-effect-modifier-payload";
                    return false;
                }
                plan = new MonsterTargetEffectNode
                {
                    Kind = MonsterTargetEffectNodeKind.DamageModifier,
                    EffectPath = effectPath,
                    ChanceWire = ResolveSpellEffectChanceWire(modEffect),
                    DamageModifier = damageModifier
                };
                return true;
            }
            if (!AuthoredExtends(modifier, "AttributeModifier") && !AuthoredExtends(modifier, "CowardiceModifier"))
            {
                reason = $"unsupported-target-modifier-family:{modifier.Extends ?? "unknown"}";
                return false;
            }
            var attributes = new List<GCNode>();
            CollectMonsterModifierAttributeNodes(modifierDesc, attributes, new HashSet<GCNode>());
            if (attributes.Count == 0)
            {
                reason = "empty-target-attribute-modifier";
                return false;
            }
            var authoredAttributes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (GCNode attribute in attributes)
            {
                string attributeName = attribute.GetString("Attribute", null);
                if (string.IsNullOrWhiteSpace(attributeName))
                {
                    reason = "missing-target-attribute-name";
                    return false;
                }
                if (!SpellDatabase.IsPlayerAuthoredAttributeSupported(attributeName))
                {
                    reason = $"unsupported-target-attribute:{attributeName}";
                    return false;
                }
                authoredAttributes[attributeName] = ResolveMonsterAttributeLevelValue(attribute, skillLevel, powerLevel);
            }
            int durationF32 = ResolveMonsterSpellModDurationF32(modEffect, skillLevel);
            plan = new MonsterTargetEffectNode
            {
                Kind = MonsterTargetEffectNodeKind.AttributeModifier,
                EffectPath = effectPath,
                ChanceWire = ResolveSpellEffectChanceWire(modEffect),
                AttributeModifier = new MonsterAttributeModifierPlan
                {
                    SkillPath = skillPath,
                    EffectPath = effectPath,
                    ModifierPath = modifierPath,
                    StackRule = modifierDesc.GetString("StackRule", ""),
                    DurationTicks = ComputeSpellModDurationTicks(durationF32),
                    RemoveOnDeath = modifierDesc.GetBool("RemoveOnDeath", false),
                    TerminateWhenHitChance = modifierDesc.GetInt("TerminateWhenHitChance", 0),
                    PowerLevel = powerLevel,
                    SkillLevel = unchecked((byte)skillLevel),
                    Attributes = authoredAttributes
                }
            };
            return true;
        }

        private bool TryBuildMonsterCastModifierPlan(
            string modifierPath,
            GCNode skillContext,
            string skillPath,
            int skillLevel,
            uint powerLevel,
            out MonsterCastModifierPlan plan,
            out string reason)
        {
            plan = null;
            reason = null;
            GCNode modifier = ResolveAuthoredNodeReference(modifierPath, skillContext);
            GCNode modifierDesc = ResolveMonsterModifierDescription(modifier);
            if (modifier == null || modifierDesc == null)
            {
                reason = "unsupported-cast-modifier";
                return false;
            }
            bool attributeModifier = AuthoredExtends(modifier, "AttributeModifier");
            string modifierExtends = modifier.Extends ?? string.Empty;
            int modifierExtendsDot = modifierExtends.LastIndexOf('.');
            if (modifierExtendsDot >= 0)
                modifierExtends = modifierExtends.Substring(modifierExtendsDot + 1);
            string visualPath = modifierDesc.GetString("Visual", null);
            bool visualOnly = !attributeModifier
                && string.Equals(modifierExtends, "Modifier", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(visualPath);
            if (!attributeModifier && !visualOnly)
            {
                reason = "unsupported-cast-modifier-family";
                return false;
            }
            var attributes = new List<GCNode>();
            if (attributeModifier)
                CollectMonsterModifierAttributeNodes(modifierDesc, attributes, new HashSet<GCNode>());
            var authoredAttributes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (GCNode attribute in attributes)
            {
                string attributeName = attribute.GetString("Attribute", null);
                if (string.IsNullOrWhiteSpace(attributeName))
                {
                    reason = "missing-cast-modifier-attribute";
                    return false;
                }
                if (!IsMonsterAuthoredAttributeSupported(attributeName))
                {
                    reason = $"unsupported-cast-modifier-attribute:{attributeName}";
                    return false;
                }
                authoredAttributes[attributeName] = ResolveMonsterAttributeLevelValue(attribute, skillLevel, powerLevel);
            }
            if (authoredAttributes.Count == 0 && !visualOnly)
            {
                reason = "empty-cast-modifier";
                return false;
            }
            int durationF32 = modifier.GetFixed32("Duration", 0);
            plan = new MonsterCastModifierPlan
            {
                ModifierPath = modifierPath,
                StackRule = modifierDesc.GetString("StackRule", "UNIQUEBYSOURCE"),
                DurationTicks = durationF32 > 0 ? ComputeSpellModDurationTicks(durationF32) : 0,
                RemoveOnDeath = modifierDesc.GetBool("RemoveOnDeath", true),
                PowerLevel = powerLevel,
                VisualOnly = visualOnly,
                VisualPath = visualPath,
                Attributes = authoredAttributes
            };
            return true;
        }

        private static GCNode ResolveMonsterModifierDescription(GCNode modifier)
        {
            if (modifier == null)
                return null;
            GCNode description = modifier.GetChild("Description") ?? modifier.GetChild("Descristption");
            if (description != null)
                return GCDatabase.Instance.ResolveWithInheritance(description) ?? description;
            foreach (GCNode child in modifier.EnumerateChildrenInOrder())
                if (!string.IsNullOrWhiteSpace(child?.Extends) && child.Extends.EndsWith("Desc", StringComparison.OrdinalIgnoreCase))
                    return GCDatabase.Instance.ResolveWithInheritance(child) ?? child;
            return GCDatabase.Instance.ResolveWithInheritance(modifier) ?? modifier;
        }

        private bool ApplyMonsterActiveSkillCastModifier(Monster monster, bool whileClosing)
        {
            if (monster == null || monster.ActiveSkillCastModifierApplied || monster.SelectedActiveSkill?.InstantUse != false)
                return true;
            MonsterCastModifierPlan cast;
            string skillPath;
            string effectPath;
            if (monster.ActiveSkillEffectPlan is MonsterTargetSkillPlan targetSkillPlan)
            {
                cast = targetSkillPlan.CastModifier;
                skillPath = targetSkillPlan.SkillPath;
                effectPath = targetSkillPlan.EffectPath;
            }
            else if (monster.ActiveSkillEffectPlan is MonsterSelfSkillPlan selfSkillPlan)
            {
                cast = selfSkillPlan.CastModifier;
                skillPath = selfSkillPlan.SkillPath;
                effectPath = selfSkillPlan.EffectPath;
            }
            else
                return true;
            if (cast == null)
                return true;
            if (whileClosing != monster.SelectedActiveSkill.AddModifierWhileClosing)
                return true;
            if (cast.Attributes == null || cast.Attributes.Count == 0 && !cast.VisualOnly)
                return false;
            bool applied = ApplyMonsterAuthoredAttributeModifier(
                monster,
                cast.ModifierPath,
                cast.Attributes,
                cast.DurationTicks,
                cast.RemoveOnDeath,
                cast.StackRule,
                cast.PowerLevel,
                monster.EntityId,
                skillPath,
                effectPath,
                monster.SelectedActiveSkill.SkillLevel);
            if (!applied)
                return true;
            monster.ActiveSkillCastModifierApplied = true;
            monster.ActiveSkillCastModifierKey = string.Equals(cast.StackRule, "UNIQUEBYSOURCE", StringComparison.OrdinalIgnoreCase)
                ? $"{monster.EntityId}:{cast.ModifierPath}"
                : cast.ModifierPath;
            if (cast.VisualOnly)
                SkillEffectTracker.RecordValidated("monster-cast-visual", monster.EntityId, skillPath, monster.EntityId, cast.VisualPath, _combatTick);
            Debug.LogError($"[MON-SKILL-CASTMOD] add monster={monster.Name}#{monster.EntityId} skill={skillPath} modifier={cast.ModifierPath} whileClosing={whileClosing} visualOnly={cast.VisualOnly} visual={cast.VisualPath ?? "none"} attributes={string.Join(",", cast.Attributes)} sourceFunction=ActiveSkill::addCastModifier@0x00538C50");
            return true;
        }

        private bool TryExecuteMonsterTargetActiveSkillEffect(
            Monster monster,
            CombatTarget target,
            int distF32,
            string marker,
            string source,
            out bool handled,
            out bool hpShifted,
            out bool deferred)
        {
            handled = false;
            hpShifted = false;
            deferred = false;
            if (monster == null || target == null || !target.HasUnitState)
                return false;
            MonsterTargetSkillPlan skillPlan = monster.ActiveSkillEffectPlan as MonsterTargetSkillPlan;
            if (skillPlan == null
                && !TryBuildMonsterTargetActiveSkillEffectPlan(monster, out skillPlan, out string reason))
            {
                Debug.LogError($"[MON-SKILL-EFFECT] state=blocked monster={monster.Name}#{monster.EntityId} target={target.Name} skill={monster.SelectedActiveSkill?.Path ?? monster.PrimaryActiveSkillPath ?? "none"} reason={reason ?? "unsupported-target-effect"} source={source ?? marker ?? "unknown"}");
                return false;
            }
            monster.ActiveSkillEffectPlan = skillPlan;
            handled = ExecuteMonsterTargetEffectNode(skillPlan.Root, monster, target, distF32, marker, source, out hpShifted, out deferred);
            return handled;
        }

        private bool ExecuteMonsterTargetEffectNode(
            MonsterTargetEffectNode plan,
            Monster sourceMonster,
            CombatTarget target,
            int distF32,
            string marker,
            string source,
            out bool hpShifted,
            out bool deferred,
            (int X, int Y, int Z)? targetPosition = null,
            List<CombatTarget> projectileAreaDamageTargets = null,
            bool friendlyProjectileTarget = false)
        {
            hpShifted = false;
            deferred = false;
            if (plan == null || sourceMonster == null || (target == null && !targetPosition.HasValue))
                return false;
            if (friendlyProjectileTarget && !plan.AffectsFriends)
                return true;
            if (plan.Kind == MonsterTargetEffectNodeKind.Sequence)
            {
                bool handled = true;
                foreach (MonsterTargetEffectNode child in plan.Children)
                {
                    handled &= ExecuteMonsterTargetEffectNode(child, sourceMonster, target, distF32, marker, source,
                        out bool childHp, out bool childDeferred, targetPosition, projectileAreaDamageTargets, friendlyProjectileTarget);
                    hpShifted |= childHp;
                    deferred |= childDeferred;
                }
                return handled;
            }
            if (plan.Kind == MonsterTargetEffectNodeKind.EnemyAoe)
            {
                if (!PassesMonsterTargetEffectChance(sourceMonster, target, plan.EffectPath, plan.ChanceWire))
                    return true;
                List<CombatTarget> targets = targetPosition is { } center
                    ? CollectMonsterEnemyAoeTargets(sourceMonster, center.X, center.Y, center.Z, plan.RadiusF32, plan.MaxTargets)
                    : CollectMonsterEnemyAoeTargets(sourceMonster, target, plan.RadiusF32, plan.MaxTargets);
                bool handled = true;
                foreach (CombatTarget aoeTarget in targets)
                {
                    bool memberHpShifted = false;
                    foreach (MonsterTargetEffectNode child in plan.Children)
                    {
                        handled &= ExecuteMonsterTargetEffectNode(child, sourceMonster, aoeTarget, distF32, marker, source,
                            out bool childHp, out bool childDeferred, projectileAreaDamageTargets: projectileAreaDamageTargets);
                        memberHpShifted |= childHp;
                        deferred |= childDeferred;
                    }
                    hpShifted |= memberHpShifted;
                    if (memberHpShifted && projectileAreaDamageTargets != null && !projectileAreaDamageTargets.Contains(aoeTarget))
                        projectileAreaDamageTargets.Add(aoeTarget);
                }
                return handled;
            }
            if (target == null && plan.Kind != MonsterTargetEffectNodeKind.VisualOnly)
                return false;
            if (plan.Kind == MonsterTargetEffectNodeKind.Projectile)
            {
                if (!PassesMonsterTargetEffectChance(sourceMonster, target, plan.EffectPath, plan.ChanceWire))
                    return true;
                deferred = QueueMonsterActiveSkillProjectile(sourceMonster, target, plan.Projectile, marker, source);
                return deferred;
            }
            if (plan.Kind == MonsterTargetEffectNodeKind.Chain)
            {
                if (!PassesMonsterTargetEffectChance(sourceMonster, target, plan.EffectPath, plan.ChanceWire))
                    return true;
                deferred = QueueMonsterActiveSkillChain(sourceMonster, target, plan.Chain, marker, source);
                return deferred;
            }
            if (plan.Kind == MonsterTargetEffectNodeKind.SpellDamage)
            {
                hpShifted = ApplyMonsterSpellDamageEffect(sourceMonster, target, plan.Damage, marker, source);
                return true;
            }
            if (plan.Kind == MonsterTargetEffectNodeKind.WeaponDamage)
            {
                hpShifted = ApplyMonsterWeaponDamageSkillEffect(sourceMonster, target, plan.WeaponDamage, distF32, marker, source, out bool landed);
                if (landed)
                    foreach (MonsterTargetEffectNode child in plan.Children)
                    {
                        ExecuteMonsterTargetEffectNode(child, sourceMonster, target, distF32, marker, source, out bool childHp, out bool childDeferred);
                        hpShifted |= childHp;
                        deferred |= childDeferred;
                    }
                return true;
            }
            if (plan.Kind == MonsterTargetEffectNodeKind.StunAction)
            {
                ApplyMonsterStunActionSkillEffect(sourceMonster, target, plan.StunAction, marker, source);
                return true;
            }
            if (plan.Kind == MonsterTargetEffectNodeKind.AttributeModifier)
                return ApplyMonsterTargetAttributeModifier(sourceMonster, target, plan.AttributeModifier, plan.ChanceWire, marker, source);
            if (plan.Kind == MonsterTargetEffectNodeKind.DamageModifier)
            {
                if (!PassesMonsterTargetEffectChance(sourceMonster, target, plan.EffectPath, plan.ChanceWire))
                    return true;
                return ApplyPlayerDamageModifierFromMonster(sourceMonster, target, plan.DamageModifier, source ?? marker ?? "SpellModEffect");
            }
            if (plan.Kind == MonsterTargetEffectNodeKind.PlayerAura)
            {
                if (!PassesMonsterTargetEffectChance(sourceMonster, target, plan.EffectPath, plan.ChanceWire))
                    return true;
                return ApplyPlayerAnchoredMonsterAura(sourceMonster, target, plan.Aura, marker, source);
            }
            if (plan.Kind == MonsterTargetEffectNodeKind.Spawn)
            {
                if (!PassesMonsterTargetEffectChance(sourceMonster, target, plan.EffectPath, plan.ChanceWire))
                    return true;
                SpawnMonsterFixed(
                    plan.SpawnUnitPath,
                    target.PosFixedX,
                    target.PosFixedY,
                    target.PosFixedZ,
                    sourceMonster.HeadingFixed,
                    sourceMonster.ZoneName,
                    null,
                    sourceMonster.DifficultyF32,
                    null,
                    sourceMonster.InstanceKey);
                Debug.LogError($"[MON-SKILL-SPAWN] source={sourceMonster.Name}#{sourceMonster.EntityId} target={target.Name}#{target.EntityId} unit={plan.SpawnUnitPath} position=({target.PosFixedX},{target.PosFixedY},{target.PosFixedZ}) sourceFunction=SpellSpawnEffect::doEffect");
                return true;
            }
            if (!PassesMonsterTargetEffectChance(sourceMonster, target, plan.EffectPath, plan.ChanceWire))
                return true;
            Debug.LogError($"[MON-SKILL-VISUAL] source={sourceMonster.Name}#{sourceMonster.EntityId} target={target?.Name ?? "none"}#{target?.EntityId ?? 0} effect={plan.EffectPath ?? "none"} sourceFunction=SpellEffect::doEffect@0x00545DD0");
            return true;
        }

        private bool QueueMonsterActiveSkillProjectile(
            Monster monster,
            CombatTarget target,
            MonsterProjectilePlan projectile,
            string marker,
            string source)
        {
            if (monster == null || target == null || !target.HasUnitState || projectile?.Impact == null)
                return false;
            if (!TryPeekMonsterClientVisiblePositionFixed(monster, target.EntityId, out int sourceFixedX, out int sourceFixedY, out int sourceFixedZ))
                return false;
            ResolveMonsterActionTargetPositionFixed(target, out int targetFixedX, out int targetFixedY, out int targetFixedZ);
            return EnqueueMonsterActiveSkillProjectile(monster, target, projectile, marker, source,
                (sourceFixedX, sourceFixedY, sourceFixedZ), (targetFixedX, targetFixedY, targetFixedZ));
        }

        private bool QueueMonsterPositionProjectile(Monster monster, MonsterProjectilePlan projectile,
            (int X, int Y, int Z) launch, (int X, int Y, int Z) aim)
        {
            return EnqueueMonsterActiveSkillProjectile(monster, null, projectile, "MON-POSITION", "SpellProjectileEffect",
                launch, aim);
        }

        private static int MultiplyMonsterSpellFixed(int left, int right)
        {
            return unchecked((int)(((long)left * right) >> 8));
        }

        private bool EnqueueMonsterActiveSkillProjectile(Monster monster, CombatTarget target, MonsterProjectilePlan projectile,
            string marker, string source, (int X, int Y, int Z) launch, (int X, int Y, int Z) aim)
        {
            MersenneTwister rng = GetRoomRngForMonster(monster);
            if (rng == null)
                return false;
            uint targetEntityId = target?.EntityId ?? 0;
            int delayRemainingTicks = 0;
            uint delayRaw = 0;
            if (projectile.DelayTicks > 0)
            {
                delayRaw = RngLedger.Generate(rng, "room", $"{projectile.ProjectilePath}:SpellProjectile::init-delay", $"{monster.Name}#{monster.EntityId}->{target?.Name ?? "position"}#{targetEntityId}");
                delayRemainingTicks = (int)(delayRaw % (uint)projectile.DelayTicks);
            }

            var direction = (X: unchecked(aim.X - launch.X), Y: unchecked(aim.Y - launch.Y), Z: unchecked(aim.Z - launch.Z));
            int aimDistanceF32 = UnitMover.TableSquareRoot(unchecked((uint)(MultiplyMonsterSpellFixed(direction.X, direction.X) +
                MultiplyMonsterSpellFixed(direction.Y, direction.Y) + MultiplyMonsterSpellFixed(direction.Z, direction.Z))));
            if (aimDistanceF32 > 0)
            {
                direction = (unchecked((int)(((long)direction.X << 8) / aimDistanceF32)),
                    unchecked((int)(((long)direction.Y << 8) / aimDistanceF32)),
                    unchecked((int)(((long)direction.Z << 8) / aimDistanceF32)));
            }

            string pathMapKey = ResolveMonsterPathMapKey(monster);
            PathMap projectilePathMap = !string.IsNullOrWhiteSpace(pathMapKey) ? PathMapCatalog.Instance.GetPathMap(pathMapKey) : null;
            int groundOffsetFixedZ = 0;
            if (projectile.SnapToGround)
            {
                if (projectilePathMap != null && projectilePathMap.TryGetHeightAtFixed(launch.X, launch.Y, out int launchGroundFixedZ))
                    groundOffsetFixedZ = unchecked(launch.Z - launchGroundFixedZ);
                direction.Z = 0;
            }

            int speedF32 = Math.Max(0x100, projectile.SpeedF32);
            int stepDistanceF32 = WeaponUseRuntime.ProjectileStepDistanceF32(speedF32);
            int maxLifetimeTicks = projectile.Lifespan + 1;
            int initialDistanceF32 = projectile.OffsetF32;
            int maxDistanceF32 = initialDistanceF32 + stepDistanceF32 * maxLifetimeTicks;
            int startFixedX = unchecked(launch.X + MultiplyMonsterSpellFixed(direction.X, initialDistanceF32));
            int startFixedY = unchecked(launch.Y + MultiplyMonsterSpellFixed(direction.Y, initialDistanceF32));
            int startFixedZ = unchecked(launch.Z + MultiplyMonsterSpellFixed(direction.Z, initialDistanceF32));
            if (projectilePathMap != null)
            {
                projectilePathMap.CastGroundRayFixed(monster.PosFixedX, monster.PosFixedY, monster.PosFixedZ,
                    startFixedX, startFixedY, out startFixedX, out startFixedY, out int rayFixedZ);
                if (projectile.SnapToGround)
                {
                    startFixedZ = projectilePathMap.TryGetHeightAtFixed(startFixedX, startFixedY, out int startGroundFixedZ)
                        ? unchecked(startGroundFixedZ + groundOffsetFixedZ)
                        : rayFixedZ;
                }
            }
            int fireTick = unchecked((int)_combatTick);
            var pending = new PendingMonsterProjectileImpact
            {
                Sequence = ++_nextMonsterProjectileSequence,
                Monster = monster,
                MonsterEntityId = monster.EntityId,
                TargetEntityId = targetEntityId,
                InstanceKey = RoomRuntime.NormalizeInstanceKey(monster.InstanceKey),
                ZoneName = monster.ZoneName,
                Marker = marker,
                Source = source,
                DistF32 = Math.Max(0, aimDistanceF32),
                FireTick = fireTick,
                FlightTicks = WeaponUseRuntime.ProjectileFlightTicksFixed32(Math.Max(0, aimDistanceF32), speedF32),
                DueTick = unchecked(fireTick + 1),
                ActiveSkillProjectile = true,
                ActiveSkillImpactPlan = projectile.Impact,
                ActiveSkillProjectileObjectPath = projectile.ProjectilePath,
                ProjectileFilter = projectile.ProjectileFilter,
                StartFixedX = startFixedX,
                StartFixedY = startFixedY,
                StartFixedZ = startFixedZ,
                CurrentFixedX = startFixedX,
                CurrentFixedY = startFixedY,
                CurrentFixedZ = startFixedZ,
                DirectionFixedX = direction.X,
                DirectionFixedY = direction.Y,
                VelocityFixedX = MultiplyMonsterSpellFixed(direction.X, stepDistanceF32),
                VelocityFixedY = MultiplyMonsterSpellFixed(direction.Y, stepDistanceF32),
                VelocityFixedZ = MultiplyMonsterSpellFixed(direction.Z, stepDistanceF32),
                GravityPerTickFixed = (projectile.Gravity << 8) / 30,
                TargetFixedX = aim.X,
                TargetFixedY = aim.Y,
                ProjectileSpeedF32 = speedF32,
                ProjectileSizeF32 = Math.Max(0, projectile.SizeF32),
                StepDistanceF32 = stepDistanceF32,
                InitialDistanceF32 = initialDistanceF32,
                CurrentDistanceF32 = initialDistanceF32,
                MaxDistanceF32 = maxDistanceF32,
                MaxLifetimeTicks = maxLifetimeTicks,
                LastUpdateTick = fireTick,
                UpdatesCompleted = 0,
                DelayRemainingTicks = delayRemainingTicks,
                DelayRaw = delayRaw,
                Penetrate = projectile.Penetrate,
                RemainingPenetrations = projectile.MaxPenetrations,
                IgnoreCollisions = projectile.IgnoreCollisions,
                SnapToGround = projectile.SnapToGround,
                SeekTargets = projectile.SeekTargets,
                SeekDistanceF32 = projectile.SeekDistanceF32,
                TurnRateF32 = projectile.TurnRateF32,
                GroundOffsetFixedZ = groundOffsetFixedZ
            };
            _pendingMonsterProjectiles.Add(pending);
            RegisterClientSubEntity(ClientSubEntityKind.MonsterProjectile, pending.Sequence, pending.InstanceKey, RemoveMonsterProjectileRuntime);
            Debug.LogError($"[MON-SKILL-PROJECTILE] create source={monster.Name}#{monster.EntityId} requested={target?.Name ?? "position"}#{targetEntityId} seq={pending.Sequence} projectile={projectile.ProjectilePath} fireTick={fireTick} firstUpdateTick={fireTick + 1} startFixed=({startFixedX},{startFixedY},{startFixedZ}) targetFixed=({aim.X},{aim.Y}) directionFixed=({direction.X},{direction.Y},{direction.Z}) velocityFixed=({pending.VelocityFixedX},{pending.VelocityFixedY},{pending.VelocityFixedZ}) speedF32={speedF32} sizeF32={pending.ProjectileSizeF32} offsetF32={initialDistanceF32} maxDistanceF32={maxDistanceF32} maxLifetimeTicks={maxLifetimeTicks} delayRaw=0x{delayRaw:X8} delay={delayRemainingTicks}/{projectile.DelayTicks} penetrate={pending.Penetrate}/{pending.RemainingPenetrations} ignoreCollisions={pending.IgnoreCollisions} snapToGround={pending.SnapToGround} seek={pending.SeekTargets}/{pending.SeekDistanceF32}/{pending.TurnRateF32} sourceFunction=SpellProjectileEffect::doEffect@0x00557A20->SpellProjectile::init@0x00554CF0->EntityManager::addSubEntity");
            return true;
        }

        private void TickMonsterActiveSkillProjectile(PendingMonsterProjectileImpact pending, int projectileIndex, int nowTick)
        {
            if (pending == null
                || projectileIndex < 0
                || projectileIndex >= _pendingMonsterProjectiles.Count
                || !ReferenceEquals(_pendingMonsterProjectiles[projectileIndex], pending))
                return;
            Monster monster = pending.Monster;
            if (monster == null
                || GetMonster(pending.MonsterEntityId) != monster
                || !string.Equals(RoomRuntime.NormalizeInstanceKey(monster.InstanceKey), pending.InstanceKey, StringComparison.OrdinalIgnoreCase))
            {
                RemoveMonsterProjectileAt(projectileIndex, pending);
                return;
            }
            if (nowTick <= pending.LastUpdateTick)
                return;
            pending.LastUpdateTick = nowTick;
            if (pending.DelayRemainingTicks > 0)
            {
                pending.DelayRemainingTicks--;
                Debug.LogError($"[MON-SKILL-PROJECTILE] delay source={monster.Name}#{monster.EntityId} seq={pending.Sequence} tick={nowTick} remaining={pending.DelayRemainingTicks} sourceFunction=SpellProjectile::update@0x00555790");
                return;
            }
            int beforeDistanceF32 = pending.CurrentDistanceF32;
            int afterDistanceF32 = beforeDistanceF32 + pending.StepDistanceF32;
            UpdateMonsterActiveSkillProjectileSeeking(pending);
            int segmentStartFixedX = pending.CurrentFixedX;
            int segmentStartFixedY = pending.CurrentFixedY;
            int segmentStartFixedZ = pending.CurrentFixedZ;
            int nextFixedX = unchecked(segmentStartFixedX + pending.VelocityFixedX);
            int nextFixedY = unchecked(segmentStartFixedY + pending.VelocityFixedY);
            int nextFixedZ = unchecked(segmentStartFixedZ + pending.VelocityFixedZ);
            pending.VelocityFixedZ = unchecked(pending.VelocityFixedZ - pending.GravityPerTickFixed);
            if (pending.SnapToGround)
            {
                string pathMapKey = ResolveMonsterPathMapKey(monster);
                PathMap pathMap = !string.IsNullOrWhiteSpace(pathMapKey) ? PathMapCatalog.Instance.GetPathMap(pathMapKey) : null;
                nextFixedZ = pathMap != null && pathMap.TryGetHeightAtFixed(nextFixedX, nextFixedY, out int groundFixedZ)
                    ? unchecked(groundFixedZ + pending.GroundOffsetFixedZ)
                    : segmentStartFixedZ;
            }
            byte flightAge = unchecked((byte)pending.UpdatesCompleted);
            pending.UpdatesCompleted++;
            var endpoint = (nextFixedX, nextFixedY, nextFixedZ);
            bool hitUnits = false;
            if (pending.Penetrate)
            {
                List<(CombatTarget Target, long DistanceSquared)> hits =
                    FindPenetratingMonsterProjectileEndpointHits(pending, endpoint, flightAge != 0);
                hitUnits = hits.Count != 0;
                foreach (var hit in hits)
                {
                    if (ResolveMonsterSpellProjectileUnitImpact(pending, projectileIndex, hit.Target, afterDistanceF32, endpoint))
                        return;
                }
            }
            else if (FindMonsterProjectileEndpointTarget(pending, nextFixedX, nextFixedY, nextFixedZ) is { } target)
            {
                ResolveMonsterSpellProjectileUnitImpact(pending, projectileIndex, target, afterDistanceF32, endpoint);
                return;
            }

            if (!hitUnits)
            {
                bool worldContact = pending.ProjectileSpeedF32 != 0
                    && WorldCollision.Instance.IsProjectileWorldEndpointBlockedFixed(pending.ZoneName, pending.InstanceKey,
                        segmentStartFixedX, segmentStartFixedY, segmentStartFixedZ, nextFixedX, nextFixedY, nextFixedZ,
                        pending.ProjectileSizeF32);
                if (worldContact ? !pending.IgnoreCollisions : flightAge >= pending.MaxLifetimeTicks - 1)
                {
                    ResolveMonsterSpellProjectileImpact(pending, null, afterDistanceF32, endpoint);
                    if (!worldContact)
                        Debug.LogError($"[MON-SKILL-PROJECTILE] expire source={monster.Name}#{monster.EntityId} seq={pending.Sequence} tick={nowTick} distanceF32={afterDistanceF32}/{pending.MaxDistanceF32} updates={pending.UpdatesCompleted}/{pending.MaxLifetimeTicks} reason=lifespan sourceFunction=SpellProjectile::update@0x00555790");
                    RemoveMonsterProjectileAt(projectileIndex, pending);
                    return;
                }
            }

            pending.CurrentFixedX = nextFixedX;
            pending.CurrentFixedY = nextFixedY;
            pending.CurrentFixedZ = nextFixedZ;
            pending.CurrentDistanceF32 = afterDistanceF32;
        }

        private bool ResolveMonsterSpellProjectileUnitImpact(
            PendingMonsterProjectileImpact pending, int projectileIndex, CombatTarget hitTarget, int distanceF32,
            (int X, int Y, int Z) endpoint)
        {
            if (pending.RemainingPenetrations > 0)
                pending.RemainingPenetrations--;
            if (TryGetCombatTarget(hitTarget.EntityId, out CombatTarget currentTarget)
                && ReferenceEquals(currentTarget, hitTarget)
                && hitTarget.HasUnitState
                && string.Equals(RoomRuntime.NormalizeInstanceKey(hitTarget.InstanceKey), pending.InstanceKey, StringComparison.OrdinalIgnoreCase))
                ResolveMonsterSpellProjectileImpact(pending, hitTarget, distanceF32, endpoint);
            if (!_pendingMonsterProjectiles.Contains(pending))
                return true;
            if (pending.Penetrate && pending.RemainingPenetrations != 0)
                return false;
            RemoveMonsterProjectileAt(projectileIndex, pending);
            return true;
        }

        private void ResolveMonsterSpellProjectileImpact(
            PendingMonsterProjectileImpact pending, CombatTarget hitTarget, int distanceF32, (int X, int Y, int Z) position)
        {
            Monster monster = pending.Monster;
            var areaDamageTargets = new List<CombatTarget>();
            uint primaryHPBefore = hitTarget?.CurrentHPWire ?? 0;
            bool deferred = false;
            bool friendlyContact = hitTarget != null && !IsMonsterTargetEnemy(monster, hitTarget);
            bool handled = (!friendlyContact || HasMatchingOwnedProjectileFilter(pending, hitTarget))
                && ExecuteMonsterTargetEffectNode(pending.ActiveSkillImpactPlan as MonsterTargetEffectNode,
                    monster, hitTarget, distanceF32, pending.Marker, $"{pending.Source ?? "ActiveSkill"}-projectile-impact",
                    out _, out deferred, position, areaDamageTargets, friendlyContact);
            if (hitTarget != null)
                hitTarget.IsAlive = hitTarget.CurrentHPWire > 0;
            DispatchMonsterProjectileProcs(monster, pending.ActiveSkillProjectileObjectPath,
                MonsterProcEventKind.ProjectileImpact, "ProcModifier::OnProjectileImpact");
            if (hitTarget?.Monster is { } hitMonster)
                DispatchMonsterProjectileHitProcs(hitMonster, pending.ActiveSkillProjectileObjectPath, null);

            foreach (CombatTarget damagedTarget in areaDamageTargets)
                OnMonsterAttackResolved?.Invoke(monster, damagedTarget, true, damagedTarget.CurrentHPWire);
            bool primaryHPShifted = hitTarget != null && primaryHPBefore != hitTarget.CurrentHPWire;
            if (hitTarget != null && handled && !deferred && !areaDamageTargets.Contains(hitTarget))
                OnMonsterAttackResolved?.Invoke(monster, hitTarget, primaryHPShifted, hitTarget.CurrentHPWire);
            Debug.LogError($"[MON-SKILL-PROJECTILE] impact source={monster.Name}#{monster.EntityId} requested={pending.TargetEntityId} target={hitTarget?.Name ?? "none"}#{hitTarget?.EntityId ?? 0} seq={pending.Sequence} pointFixed=({position.X},{position.Y},{position.Z}) distanceF32={distanceF32} handled={handled} hpShifted={primaryHPShifted} areaDamage={areaDamageTargets.Count} deferred={deferred} sourceFunction=SpellProjectile::doImpact@0x005562E0");
        }

        private void UpdateMonsterActiveSkillProjectileSeeking(PendingMonsterProjectileImpact pending)
        {
            if (!pending.SeekTargets)
                return;
            CombatTarget target = pending.SeekingTarget;
            if (target == null || !TryGetCombatTarget(target.EntityId, out CombatTarget current) || !ReferenceEquals(target, current)
                || !string.Equals(RoomRuntime.NormalizeInstanceKey(target.InstanceKey), pending.InstanceKey, StringComparison.OrdinalIgnoreCase))
            {
                pending.SeekingTarget = FindMonsterProjectileSeekingTarget(pending);
                return;
            }
            int currentHeading = UnitMover.GetSpellProjectileHeadingFixed(pending.VelocityFixedX, pending.VelocityFixedY);
            int desiredHeading = UnitMover.GetSpellProjectileHeadingFixed(
                unchecked(target.PosFixedX - pending.CurrentFixedX), unchecked(target.PosFixedY - pending.CurrentFixedY));
            int heading = TurnMonsterSpellProjectileHeading(currentHeading, desiredHeading, pending.TurnRateF32 / 30);
            int angle = 360 - (heading >> 8);
            int x = UnitMover.ZRotateSinFixed(angle);
            int y = UnitMover.ZRotateCosFixed(angle);
            if ((x > 0 || y > 0) && PathMap.TryBuildNativeRayDirectionFixed(x, y, out int normalizedX, out int normalizedY, out _))
            {
                x = normalizedX;
                y = normalizedY;
            }
            pending.VelocityFixedX = MultiplyMonsterSpellFixed(x, pending.StepDistanceF32);
            pending.VelocityFixedY = MultiplyMonsterSpellFixed(y, pending.StepDistanceF32);
        }

        private CombatTarget FindMonsterProjectileSeekingTarget(PendingMonsterProjectileImpact pending)
        {
            long closestDistance = ((long)pending.SeekDistanceF32 * pending.SeekDistanceF32) >> 8;
            CombatTarget closest = null;
            foreach (CombatTarget candidate in GetCombatTargetsInEntityOrder())
            {
                if (candidate.EntityId == 0 || !candidate.HasUnitState || !HasActiveMonsterTargetFlags(candidate)
                    || (candidate.Player != null && !candidate.IsAlive)
                    || !MatchesInstance(pending.Monster, candidate.InstanceKey) || !IsMonsterTargetEnemy(pending.Monster, candidate))
                    continue;
                long distance = ProjectileFinderDistanceSqFixed8(candidate.PosFixedX, candidate.PosFixedY, candidate.PosFixedZ,
                    pending.CurrentFixedX, pending.CurrentFixedY, pending.CurrentFixedZ);
                if (distance > closestDistance || (distance == closestDistance && closest != null && candidate.EntityId >= closest.EntityId))
                    continue;
                int forward = unchecked(
                    MultiplyMonsterSpellFixed(candidate.PosFixedX - pending.CurrentFixedX, pending.VelocityFixedX)
                    + MultiplyMonsterSpellFixed(candidate.PosFixedY - pending.CurrentFixedY, pending.VelocityFixedY)
                    + MultiplyMonsterSpellFixed(candidate.PosFixedZ - pending.CurrentFixedZ, pending.VelocityFixedZ));
                if (forward <= 0)
                    continue;
                closest = candidate;
                closestDistance = distance;
            }
            return closest;
        }

        private static int TurnMonsterSpellProjectileHeading(int current, int desired, int step)
        {
            if (current == desired)
                return current;
            int delta = unchecked(desired - current) >> 8;
            if (delta > 180)
                delta -= 360;
            else if (delta < -180)
                delta += 360;
            if (delta > 0)
                current = (delta << 8) < step ? desired : unchecked(current + step);
            else if (delta < 0)
                current = (-delta << 8) < step ? desired : unchecked(current - step);
            int wrapped = current % 0x16800;
            return wrapped < 0 ? wrapped + 0x16800 : wrapped;
        }

        private List<(CombatTarget Target, long DistanceSquared)> FindPenetratingMonsterProjectileEndpointHits(
            PendingMonsterProjectileImpact pending, (int X, int Y, int Z) endpoint, bool requireNewContact)
        {
            var hits = new List<(CombatTarget Target, long DistanceSquared)>();
            int scanRadius = pending.ProjectileSizeF32 + 0x1e00;
            long scanRadiusSquared = ((long)scanRadius * scanRadius) >> 8;
            foreach (CombatTarget candidate in GetCombatTargetsInEntityOrder())
            {
                if (TryTestMonsterProjectileEndpointCandidate(pending, candidate, endpoint, scanRadiusSquared,
                    requireNewContact, out long distanceSquared))
                    hits.Add((candidate, distanceSquared));
            }
            hits.Sort((left, right) =>
            {
                int order = left.DistanceSquared.CompareTo(right.DistanceSquared);
                return order != 0 ? order : left.Target.EntityId.CompareTo(right.Target.EntityId);
            });
            return hits;
        }

        private void RemoveMonsterProjectileAt(int projectileIndex, PendingMonsterProjectileImpact pending)
        {
            if (projectileIndex >= 0
                && projectileIndex < _pendingMonsterProjectiles.Count
                && ReferenceEquals(_pendingMonsterProjectiles[projectileIndex], pending))
                _pendingMonsterProjectiles.RemoveAt(projectileIndex);
            else if (pending != null)
                _pendingMonsterProjectiles.Remove(pending);
            if (pending != null)
                RemoveClientSubEntity(ClientSubEntityKind.MonsterProjectile, pending.Sequence, pending.InstanceKey);
        }

        private bool QueueMonsterActiveSkillChain(
            Monster monster,
            CombatTarget target,
            MonsterChainPlan plan,
            string marker,
            string source)
        {
            if (monster == null || target == null || !target.HasUnitState || plan?.Impact == null)
                return false;
            var pending = new PendingMonsterChainRuntime
            {
                Sequence = ++_nextMonsterChainSequence,
                SourceMonster = monster,
                SourceMonsterEntityId = monster.EntityId,
                InstanceKey = RoomRuntime.NormalizeInstanceKey(monster.InstanceKey),
                ZoneName = monster.ZoneName,
                Marker = marker,
                Source = source,
                Plan = plan
            };
            pending.Branches.Add(new MonsterChainBranch
            {
                TargetEntityId = target.EntityId,
                RemainingLifespanTicks = plan.ChainLifespanTicks,
                Generation = 0
            });
            _pendingMonsterChains.Add(pending);
            RegisterClientSubEntity(ClientSubEntityKind.MonsterSpellChainProjectile, pending.Sequence, pending.InstanceKey, RemoveMonsterChainRuntime);
            bool handled = true;
            bool hpShifted = false;
            bool nestedDeferred = false;
            if (!plan.SkipSourceChain)
            {
                TryGetMonsterClientVisiblePositionFixed(monster, target.EntityId, out int monsterFixedX, out int monsterFixedY);
                int dx = target.PosFixedX - monsterFixedX;
                int dy = target.PosFixedY - monsterFixedY;
                int distanceF32 = UnitMover.IntSqrt((long)dx * dx + (long)dy * dy);
                handled = ExecuteMonsterTargetEffectNode(plan.Impact, monster, target, distanceF32, marker, $"{source ?? "ActiveSkill"}-chain-initial", out hpShifted, out nestedDeferred);
                target.IsAlive = target.CurrentHPWire > 0;
                if (handled && !nestedDeferred)
                    OnMonsterAttackResolved?.Invoke(monster, target, hpShifted, target.CurrentHPWire);
            }
            Debug.LogError($"[MON-SKILL-CHAIN] init source={monster.Name}#{monster.EntityId} target={target.Name}#{target.EntityId} seq={pending.Sequence} chains={plan.NumChains} forks={plan.NumForks} rangeF32={plan.ChainRangeF32} delay={plan.ChainDelayTicks} lifespan={plan.ChainLifespanTicks} skipSource={plan.SkipSourceChain} handled={handled} hpShifted={hpShifted} nestedDeferred={nestedDeferred} sourceFunction=SpellChainEffect::doEffect@0x0054A7D0->SpellChainProjectile::init@0x0054BD80");
            return true;
        }

        public void TickMonsterChainSubEntity(long sequence, string instanceKey, uint simulationTick)
        {
            int pendingIndex = _pendingMonsterChains.FindIndex(pending => pending != null && pending.Sequence == sequence);
            if (pendingIndex < 0)
            {
                RemoveClientSubEntity(ClientSubEntityKind.MonsterSpellChainProjectile, sequence, instanceKey);
                return;
            }
            PendingMonsterChainRuntime pending = _pendingMonsterChains[pendingIndex];
            Monster monster = pending.SourceMonster;
            if (monster == null
                || GetMonster(pending.SourceMonsterEntityId) != monster
                || !string.Equals(RoomRuntime.NormalizeInstanceKey(monster.InstanceKey), pending.InstanceKey, StringComparison.OrdinalIgnoreCase))
            {
                RemoveMonsterChainAt(pendingIndex, pending);
                return;
            }
            int branchCountAtEntry = pending.Branches.Count;
            var expired = new List<MonsterChainBranch>();
            for (int branchIndex = 0; branchIndex < branchCountAtEntry; branchIndex++)
            {
                MonsterChainBranch branch = pending.Branches[branchIndex];
                TryGetCombatTarget(branch.TargetEntityId, out CombatTarget branchTarget);
                if (branchTarget == null
                    || !branchTarget.HasUnitState
                    || !string.Equals(RoomRuntime.NormalizeInstanceKey(branchTarget.InstanceKey), pending.InstanceKey, StringComparison.OrdinalIgnoreCase))
                {
                    expired.Add(branch);
                    continue;
                }
                branch.RemainingLifespanTicks--;
                if (branch.RemainingLifespanTicks == pending.Plan.ChainLifespanTicks - pending.Plan.ChainDelayTicks
                    && branch.Generation < pending.Plan.NumChains)
                    ForkMonsterActiveSkillChain(pending, branch, branchTarget, unchecked((int)simulationTick));
                if (branch.RemainingLifespanTicks <= 0)
                    expired.Add(branch);
            }
            foreach (MonsterChainBranch branch in expired)
                pending.Branches.Remove(branch);
            if (pending.Branches.Count == 0)
                RemoveMonsterChainAt(pendingIndex, pending);
        }

        private void ForkMonsterActiveSkillChain(
            PendingMonsterChainRuntime pending,
            MonsterChainBranch parent,
            CombatTarget center,
            int simulationTick)
        {
            var activeTargets = new HashSet<uint>();
            foreach (MonsterChainBranch branch in pending.Branches)
                activeTargets.Add(branch.TargetEntityId);
            long rangeSquaredFixed8 = ((long)pending.Plan.ChainRangeF32 * pending.Plan.ChainRangeF32) >> 8;
            var candidates = new List<(CombatTarget Player, long DistanceSquaredFixed8)>();
            PathMap pathMap = !string.IsNullOrWhiteSpace(pending.InstanceKey) ? PathMapCatalog.Instance.GetPathMap(pending.InstanceKey) : null;
            if (pathMap == null && !string.IsNullOrWhiteSpace(pending.ZoneName))
                pathMap = PathMapCatalog.Instance.GetPathMap(pending.ZoneName);
            foreach (CombatTarget candidate in GetCombatTargetsInEntityOrder())
            {
                if (candidate == null
                    || !candidate.IsAlive
                    || !candidate.HasUnitState
                    || activeTargets.Contains(candidate.EntityId)
                    || !string.Equals(RoomRuntime.NormalizeInstanceKey(candidate.InstanceKey), pending.InstanceKey, StringComparison.OrdinalIgnoreCase))
                    continue;
                long dx = (long)candidate.PosFixedX - center.PosFixedX;
                long dy = (long)candidate.PosFixedY - center.PosFixedY;
                long distanceSquaredFixed8 = ((dx * dx) >> 8) + ((dy * dy) >> 8);
                if (distanceSquaredFixed8 > rangeSquaredFixed8)
                    continue;
                if (pathMap != null
                    && pathMap.GetReachabilityFixed(center.PosFixedX, center.PosFixedY, candidate.PosFixedX, candidate.PosFixedY) != PathReachability.Reachable)
                    continue;
                candidates.Add((candidate, distanceSquaredFixed8));
            }
            candidates.Sort((left, right) =>
            {
                int distanceOrder = left.DistanceSquaredFixed8.CompareTo(right.DistanceSquaredFixed8);
                return distanceOrder != 0 ? distanceOrder : left.Player.EntityId.CompareTo(right.Player.EntityId);
            });
            int forkCount = Math.Min(pending.Plan.NumForks, candidates.Count);
            for (int candidateIndex = 0; candidateIndex < forkCount; candidateIndex++)
            {
                CombatTarget next = candidates[candidateIndex].Player;
                pending.Branches.Add(new MonsterChainBranch
                {
                    TargetEntityId = next.EntityId,
                    RemainingLifespanTicks = pending.Plan.ChainLifespanTicks,
                    Generation = parent.Generation + 1
                });
                bool handled = ExecuteMonsterTargetEffectNode(
                    pending.Plan.Impact,
                    pending.SourceMonster,
                    next,
                    UnitMover.IntSqrt(candidates[candidateIndex].DistanceSquaredFixed8 << 8),
                    pending.Marker,
                    $"{pending.Source ?? "ActiveSkill"}-chain-fork",
                    out bool hpShifted,
                    out bool nestedDeferred);
                next.IsAlive = next.CurrentHPWire > 0;
                if (handled && !nestedDeferred)
                    OnMonsterAttackResolved?.Invoke(pending.SourceMonster, next, hpShifted, next.CurrentHPWire);
                Debug.LogError($"[MON-SKILL-CHAIN] fork source={pending.SourceMonster.Name}#{pending.SourceMonster.EntityId} from={center.Name}#{center.EntityId} target={next.Name}#{next.EntityId} seq={pending.Sequence} tick={simulationTick} generation={parent.Generation + 1}/{pending.Plan.NumChains} lifespan={pending.Plan.ChainLifespanTicks} handled={handled} hpShifted={hpShifted} nestedDeferred={nestedDeferred} sourceFunction=SpellChainProjectile::fork@0x0054C9C0->SpellChainProjectile::doImpact@0x0054CE00");
            }
        }

        private void RemoveMonsterChainRuntime(long sequence)
        {
            _pendingMonsterChains.RemoveAll(pending => pending != null && pending.Sequence == sequence);
        }

        private void RemoveMonsterChainAt(int pendingIndex, PendingMonsterChainRuntime pending)
        {
            if (pendingIndex >= 0 && pendingIndex < _pendingMonsterChains.Count)
                _pendingMonsterChains.RemoveAt(pendingIndex);
            if (pending != null)
                RemoveClientSubEntity(ClientSubEntityKind.MonsterSpellChainProjectile, pending.Sequence, pending.InstanceKey);
        }

        private void ClearMonsterChainsForMonster(uint monsterEntityId)
        {
            if (monsterEntityId == 0 || _pendingMonsterChains.Count == 0)
                return;
            foreach (PendingMonsterChainRuntime pending in _pendingMonsterChains)
                if (pending != null && pending.SourceMonsterEntityId == monsterEntityId)
                    RemoveClientSubEntity(ClientSubEntityKind.MonsterSpellChainProjectile, pending.Sequence, pending.InstanceKey);
            _pendingMonsterChains.RemoveAll(pending => pending != null && pending.SourceMonsterEntityId == monsterEntityId);
        }

        private bool PassesMonsterTargetEffectChance(Monster sourceMonster, CombatTarget target, string effectPath, int chanceWire)
        {
            int chance = Math.Clamp(chanceWire, 0, 0x6400);
            if (chance >= 0x6400)
                return true;
            MersenneTwister rng = GetRoomRngForMonster(sourceMonster);
            if (rng == null)
                return false;
            uint raw = RngLedger.Generate(rng, "room", $"MON-TARGET:{effectPath ?? "effect"}:SpellEffect::CheckChance", sourceMonster.InstanceKey ?? sourceMonster.ZoneName, sourceMonster.EntityId);
            uint roll = raw % 0x6464u;
            bool passed = roll < (uint)chance;
            Debug.LogError($"[MON-SKILL-CHANCE] source={sourceMonster.Name}#{sourceMonster.EntityId} target={target?.Name ?? "none"}#{target?.EntityId ?? 0} effect={effectPath ?? "none"} chanceWire={chance} raw=0x{raw:X8} roll={roll} passed={passed} sourceFunction=SpellEffect::CheckChance@0x00545FF0");
            return passed;
        }

        private List<CombatTarget> CollectMonsterEnemyAoeTargets(Monster sourceMonster, CombatTarget center, int radiusF32, int maxTargets)
        {
            return center == null ? new List<CombatTarget>() : CollectMonsterEnemyAoeTargets(sourceMonster,
                center.PosFixedX, center.PosFixedY, center.PosFixedZ, radiusF32, maxTargets);
        }

        private List<CombatTarget> CollectMonsterEnemyAoeTargets(Monster sourceMonster, int centerX, int centerY, int centerZ, int radiusF32, int maxTargets)
        {
            var ranked = new List<(CombatTarget Player, long DistanceSquaredF32)>();
            if (radiusF32 <= 0 || maxTargets <= 0)
                return new List<CombatTarget>();
            long radiusSquaredF32 = ((long)radiusF32 * radiusF32) >> 8;
            foreach (CombatTarget candidate in GetCombatTargetsInEntityOrder())
            {
                if (candidate == null || !candidate.IsAlive || !candidate.HasUnitState || !MatchesInstance(sourceMonster, candidate.InstanceKey) || !IsMonsterEnemyOfTarget(sourceMonster, candidate))
                    continue;
                long dx = (long)candidate.PosFixedX - centerX;
                long dy = (long)candidate.PosFixedY - centerY;
                long dz = (long)candidate.PosFixedZ - centerZ;
                long distanceSquaredF32 = ((dx * dx) >> 8) + ((dy * dy) >> 8) + ((dz * dz) >> 8);
                if (distanceSquaredF32 <= radiusSquaredF32)
                    ranked.Add((candidate, distanceSquaredF32));
            }
            ranked.Sort((left, right) =>
            {
                int distanceOrder = left.DistanceSquaredF32.CompareTo(right.DistanceSquaredF32);
                return distanceOrder != 0 ? distanceOrder : left.Player.EntityId.CompareTo(right.Player.EntityId);
            });
            int count = Math.Min(maxTargets, ranked.Count);
            var result = new List<CombatTarget>(count);
            for (int index = 0; index < count; index++)
                result.Add(ranked[index].Player);
            return result;
        }

        private bool ApplyMonsterTargetAttributeModifier(
            Monster sourceMonster,
            CombatTarget target,
            MonsterAttributeModifierPlan modifier,
            int chanceWire,
            string marker,
            string source)
        {
            if (sourceMonster == null || target == null || !target.HasUnitState || modifier == null)
                return false;
            if (!PassesMonsterTargetEffectChance(sourceMonster, target, modifier.EffectPath, chanceWire))
                return true;
            if (target.Monster != null)
                return ApplyMonsterAuthoredAttributeModifier(target.Monster, modifier.ModifierPath, modifier.Attributes,
                    (int)modifier.DurationTicks, modifier.RemoveOnDeath, modifier.StackRule, modifier.PowerLevel,
                    sourceMonster.EntityId, modifier.SkillPath, modifier.EffectPath, modifier.SkillLevel, modifier.TerminateWhenHitChance);
            bool locallySimulated = string.Equals(modifier.ModifierPath, "skills.creature.CreatureRend.Modifier", StringComparison.OrdinalIgnoreCase);
            byte sourceIsSelf = locallySimulated ? (byte)0 : CLIENT_RUNTIME_MODIFIER_SOURCE_IS_SELF;
            string modifierKey = BuildPlayerRuntimeModifierKey(target.EntityId, sourceMonster.EntityId, modifier.ModifierPath, modifier.StackRule, sourceIsSelf);
            bool replace = false;
            uint modifierId = 0;
            if (!locallySimulated)
            {
                replace = _playerModifierNetworkIds.ContainsKey(modifierKey);
                if (!target.PlayerState.ShouldAcceptAttributeModifier(modifierKey, modifier.ModifierPath, modifier.StackRule, modifier.PowerLevel, modifier.DurationTicks))
                {
                    RaisePlayerModifierAdd(sourceMonster, target, modifierKey, modifier.ModifierPath, modifier.SkillLevel, modifier.PowerLevel, modifier.DurationTicks, sourceIsSelf, replace, modifier.SkillPath, modifier.EffectPath, source ?? marker ?? "SpellModEffect", "SpellModEffect::doEffect@0x00554460 Modifiers::processAddModifier@0x00502280", false);
                    return true;
                }
                if (!TryAllocatePlayerModifierNetworkId(modifierKey, true, out modifierId))
                    return false;
            }
            bool applied = target.PlayerState.ApplyAuthoredAttributeModifier(
                modifier.ModifierPath,
                modifier.Attributes,
                modifier.DurationTicks,
                modifier.RemoveOnDeath,
                source ?? marker ?? "SpellModEffect",
                modifierKey,
                sourceMonster.EntityId,
                modifier.SkillPath,
                modifier.EffectPath,
                modifier.PowerLevel,
                modifier.StackRule,
                modifier.TerminateWhenHitChance,
                modifier.SkillLevel,
                sourceIsSelf,
                persistsAfterSourceRemoval: locallySimulated);
            if (locallySimulated)
                return true;
            if (!applied)
            {
                ReleasePlayerModifierNetworkId(modifierKey, modifierId);
                return false;
            }
            RaisePlayerModifierAddWithId(sourceMonster, target, modifierKey, modifier.ModifierPath, modifierId, modifier.SkillLevel, modifier.PowerLevel, modifier.DurationTicks, sourceIsSelf, replace, modifier.SkillPath, modifier.EffectPath, source ?? marker ?? "SpellModEffect", "SpellModEffect::doEffect@0x00554460 Modifiers::processAddModifier@0x00502280");
            return true;
        }

        private bool ApplyMonsterSpellDamageEffect(Monster monster, CombatTarget target, MonsterSpellDamagePlan damage, string marker, string source)
        {
            if (monster == null || target == null || !target.HasUnitState || damage == null || !target.IsAlive)
                return false;
            MersenneTwister rng = GetRoomRngForMonster(monster);
            if (rng == null)
                return false;
            if (!PassesMonsterTargetEffectChance(monster, target, damage.EffectPath, damage.ChanceWire))
                return false;
            int minDamage;
            int maxDamage;
            ComputeMonsterSpellDamageRange(monster, damage.DamageTypeId, damage.DamageModF32, damage.DamageVolatilityF32, out minDamage, out maxDamage);
            uint damageRaw = 0;
            if ((minDamage >> 8) != (maxDamage >> 8))
                damageRaw = RngLedger.Generate(rng, "room", $"{damage.SkillPath}:SpellDamageEffect::damage", $"{monster.Name}#{monster.EntityId}->{target.Name}#{target.EntityId}");
            int rawDamage = DamageResolver.RollSpellDamageRange(minDamage, maxDamage, damageRaw);
            bool critical = false;
            uint criticalRaw = 0;
            int criticalThreshold = 0;
            if (damage.CriticalChanceF32 > 0)
            {
                int baseChanceF32 = unchecked(monster.Slots.Get(UnitSlot.MagicCriticalChance) << 8);
                int modifierPercentF32 = unchecked(monster.Slots.Get(UnitSlot.MagicCriticalChanceMod) << 8);
                int modifierF32 = (int)(((long)modifierPercentF32 << 8) / 0x6400);
                int chanceF32 = unchecked(baseChanceF32 + (int)(((long)baseChanceF32 * modifierF32) >> 8));
                criticalThreshold = unchecked((int)(((long)chanceF32 * damage.CriticalChanceF32) >> 8));
                if (criticalThreshold > 0)
                {
                    criticalThreshold = Math.Min(criticalThreshold, 0x5A00);
                    criticalRaw = RngLedger.Generate(rng, "room", $"{damage.SkillPath}:SpellDamageEffect::critical", $"{monster.Name}#{monster.EntityId}->{target.Name}#{target.EntityId}");
                    critical = criticalRaw % 0x6464u < (uint)criticalThreshold;
                    if (critical)
                    {
                        int criticalDamagePercent = monster.Slots.Get(UnitSlot.CriticalDamagePercent, 200);
                        if (criticalDamagePercent > 100)
                            rawDamage = unchecked((int)(unchecked((uint)rawDamage * (uint)criticalDamagePercent) / 100u));
                    }
                }
            }
            uint damageWire = unchecked((uint)Math.Max(0, rawDamage));
            uint beforeHP = target.CurrentHPWire;
            DamageQueryResult query = ApplyPlayerDamageQueryWire(damageWire, target, monster, damage.DamageTypeId, damage.DamageKind, source ?? marker ?? "SpellDamageEffect", monster.Level, rng, true);
            uint adjustedDamageWire = query.AdjustedDamageWire;
            uint afterHP = beforeHP;
            uint effectRaw = 0;
            if (adjustedDamageWire > 0)
            {
                ApplyCombatTargetQueriedDamage(monster, target, adjustedDamageWire, source ?? marker);
                afterHP = target.CurrentHPWire;
                target.IsAlive = afterHP > 0;
                uint appliedDamage = beforeHP > afterHP ? beforeHP - afterHP : 0;
                int stunMod = ResolveSpellDamageStunMod(damage.DamageStunMod, monster.Slots?.Get(UnitSlot.StunMod, monster.StunMod) ?? monster.StunMod);
                effectRaw = ConsumeOnApplyDamageEffectRng(rng, "monster-spell", monster, target, beforeHP, afterHP, appliedDamage, stunMod, source ?? marker ?? "SpellDamageEffect");
            }
            if (adjustedDamageWire > 0)
                DispatchMonsterHitProcs(monster, target, damage.AttackType, damage.DamageTypeId, adjustedDamageWire, source ?? marker ?? "SpellDamageEffect");
            DispatchCombatTargetDamageEvent(target, rng, target.EntityId, target.Name, source ?? marker ?? "SpellDamageEffect");
            if (damage.DamageKind != 3 && beforeHP > afterHP)
                ApplyMonsterOnDamageCallback(monster, beforeHP - afterHP, source ?? marker ?? "SpellDamageEffect");
            if (afterHP == 0)
            {
                TryCommitCombatTargetDeathEvent(target, source ?? marker ?? "SpellDamageEffect");
                RemovePlayerDamageModifiersForTarget(target.EntityId, source ?? marker ?? "SpellDamageEffect");
            }
            string result = adjustedDamageWire > 0 ? (critical ? "CRITICAL" : "HIT") : query.ResultName;
            Debug.LogError($"[SPELL-DAMAGE] source={monster.Name}#{monster.EntityId} target={target.Name}#{target.EntityId} result={result} skill={damage.SkillPath} effect={damage.EffectPath} attackType={damage.AttackType} damageType={damage.DamageType}/{damage.DamageTypeId} range=[{minDamage},{maxDamage}] damageRaw=0x{damageRaw:X8} preQueryWire={damageWire} damageWire={adjustedDamageWire} hp={beforeHP}->{afterHP}/{target.MaxHPWire} criticalRaw=0x{criticalRaw:X8} criticalThreshold={criticalThreshold} effectRaw=0x{effectRaw:X8} rngPos={rng.CallsSinceReseed} sourceFunction=SpellDamageEffect::doEffect@0x0054FD20 Damage::apply@0x004F6580");
            return afterHP != beforeHP;
        }
    }
}
