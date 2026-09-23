using System;
using System.Collections.Generic;
using System.Linq;
using DungeonRunners.Data;
using DungeonRunners.Engine;
using DungeonRunners.Networking;

namespace DungeonRunners.Combat
{
    public static partial class SpellDatabase
    {
        private static Dictionary<string, SpellData> _spells;
        private static List<SpellData> _spellOrder;
        private static Dictionary<string, int> _spellOrderIndex;
        private static bool _initialized;
        private static bool _authoredLoaded;
        private const int EXPECTED_PLAYER_SKILLS = 96;
        private const int EXPECTED_SHARED_CREATURE_SKILLS = 91;
        private const int EXPECTED_EMBEDDED_MONSTER_SKILLS = 150;
        private const int EXPECTED_DIRECT_SHARED_CREATURE_SKILL_PATHS = 61;
        private const int EXPECTED_DIRECT_MANIPULATOR_SKILL_PATHS = 211;
        private static int _directManipulatorSkillPaths;
        private static int _directSharedCreatureSkillPaths;
        private static int _directEmbeddedMonsterSkillPaths;
        private static readonly string[] PLAYER_ANIMATION_LIST_PATHS =
        {
            "avatar.races.humanmale.HumanMaleAnimations",
            "avatar.races.humanfemale.HumanFemaleAnimations",
            "HumanMaleAnimations",
            "HumanFemaleAnimations"
        };

        public static void Initialize()
        {
            EnsureStorage();
            EnsureAuthoredLoaded();
        }

        private static void EnsureStorage()
        {
            if (_initialized) return;
            _spells = new Dictionary<string, SpellData>(StringComparer.OrdinalIgnoreCase);
            _spellOrder = new List<SpellData>();
            _spellOrderIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _initialized = true;
        }

        private static void EnsureAuthoredLoaded()
        {
            EnsureStorage();
            if (_authoredLoaded) return;

            var gc = GCDatabase.Instance;
            if (gc == null || !gc.IsLoaded) return;
            var loadedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var manipulatorSkillPaths = new List<string>();
            var seenManipulatorNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenManipulatorSkillPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in gc.RegisteredPaths)
            {
                if (IsTopLevelSkillPath(path))
                    RegisterAuthoredSpellPath(path, loadedPaths);

                if (path.IndexOf(".Manipulators.", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                GCNode manipulator = gc.Resolve(path);
                string canonicalPath = NormalizePath(manipulator?.CanonicalPath);
                if (manipulator == null
                    || string.IsNullOrWhiteSpace(canonicalPath)
                    || !string.Equals(NormalizePath(path), canonicalPath, StringComparison.OrdinalIgnoreCase)
                    || !seenManipulatorNodes.Add(canonicalPath))
                    continue;
                string skillPath = NormalizePath(manipulator.Extends);
                if (!string.IsNullOrWhiteSpace(skillPath) && seenManipulatorSkillPaths.Add(skillPath))
                    manipulatorSkillPaths.Add(skillPath);
            }

            int directManipulatorSkills = 0;
            int directSharedCreatureSkills = 0;
            int directEmbeddedMonsterSkills = 0;
            for (int skillIndex = 0; skillIndex < manipulatorSkillPaths.Count; skillIndex++)
            {
                string skillPath = manipulatorSkillPaths[skillIndex];
                if (!IsAuthoredSkillDefinition(skillPath))
                    continue;
                directManipulatorSkills++;
                if (skillPath.StartsWith("skills.creature.", StringComparison.OrdinalIgnoreCase))
                    directSharedCreatureSkills++;
                else
                    directEmbeddedMonsterSkills++;
            }
            if (directManipulatorSkills != EXPECTED_DIRECT_MANIPULATOR_SKILL_PATHS
                || directSharedCreatureSkills != EXPECTED_DIRECT_SHARED_CREATURE_SKILL_PATHS
                || directEmbeddedMonsterSkills != EXPECTED_EMBEDDED_MONSTER_SKILLS)
                throw new InvalidOperationException($"Authored manipulator skill inventory mismatch total={directManipulatorSkills}/{EXPECTED_DIRECT_MANIPULATOR_SKILL_PATHS} sharedCreature={directSharedCreatureSkills}/{EXPECTED_DIRECT_SHARED_CREATURE_SKILL_PATHS} embeddedMonster={directEmbeddedMonsterSkills}/{EXPECTED_EMBEDDED_MONSTER_SKILLS}");
            _directManipulatorSkillPaths = directManipulatorSkills;
            _directSharedCreatureSkillPaths = directSharedCreatureSkills;
            _directEmbeddedMonsterSkillPaths = directEmbeddedMonsterSkills;

            LogLoadSummary();
            _authoredLoaded = true;
        }

        private static void RegisterAuthoredSpellPath(string path, HashSet<string> loadedPaths)
        {
            string normalized = NormalizePath(path);
            if (string.IsNullOrWhiteSpace(normalized) || !loadedPaths.Add(normalized))
                return;
            if (TryBuildAuthoredSpell(normalized, out SpellData spell))
                Register(spell.ShortName, spell);
        }

        private static bool IsTopLevelSkillPath(string path)
        {
            string normalized = NormalizePath(path);
            const string genericPrefix = "skills.generic.";
            const string creaturePrefix = "skills.creature.";
            string prefix = normalized.StartsWith(genericPrefix, StringComparison.OrdinalIgnoreCase)
                ? genericPrefix
                : normalized.StartsWith(creaturePrefix, StringComparison.OrdinalIgnoreCase)
                    ? creaturePrefix
                    : null;
            if (prefix == null)
                return false;
            string rest = normalized.Substring(prefix.Length);
            return rest.Length > 0 && rest.IndexOf('.') < 0;
        }

        private static bool IsAuthoredSkillDefinition(string nameOrPath)
        {
            var gc = GCDatabase.Instance;
            if (gc == null || !gc.IsLoaded)
                return false;
            string path = NormalizeSkillCandidate(nameOrPath);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool hasSkillShape = false;
            for (int depth = 0; depth <= 32 && !string.IsNullOrWhiteSpace(path) && visited.Add(path); depth++)
            {
                GCNode skill = gc.Resolve(path);
                if (skill == null)
                    return false;
                GCNode description = skill.GetChild("Description");
                if (HasAuthoredSkillShape(description ?? skill))
                    hasSkillShape = true;
                string parentPath = NormalizePath(skill.Extends);
                if (string.Equals(parentPath, "ActiveSkill", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(parentPath, "PassiveSkill", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(parentPath, "InnateSkill", StringComparison.OrdinalIgnoreCase)
                    || parentPath.EndsWith(".ActiveSkill", StringComparison.OrdinalIgnoreCase)
                    || parentPath.EndsWith(".PassiveSkill", StringComparison.OrdinalIgnoreCase)
                    || parentPath.EndsWith(".InnateSkill", StringComparison.OrdinalIgnoreCase))
                    return hasSkillShape;
                path = parentPath;
            }
            return false;
        }

        private static bool HasAuthoredSkillShape(GCNode node)
        {
            var gc = GCDatabase.Instance;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int depth = 0; depth <= 32 && node != null; depth++)
            {
                if (HasSkillShape(node))
                    return true;
                string parentPath = NormalizePath(node.Extends);
                if (string.IsNullOrWhiteSpace(parentPath) || !visited.Add(parentPath))
                    return false;
                node = gc?.Resolve(parentPath);
            }
            return false;
        }

        private static bool TryBuildAuthoredSpell(string nameOrPath, out SpellData spell)
        {
            spell = null;

            var gc = GCDatabase.Instance;
            if (gc == null || !gc.IsLoaded)
                return false;

            string path = NormalizeSkillCandidate(nameOrPath);
            GCNode skill = gc.ResolveWithInheritance(path);
            if (skill == null)
                return false;
            if (!AuthoredExtends(skill, "ActiveSkill")
                && !AuthoredExtends(skill, "PassiveSkill")
                && !AuthoredExtends(skill, "InnateSkill"))
                return false;

            GCNode desc = ResolveInheritedNode(skill.GetChild("Description") ?? skill);
            if (desc == null || !HasSkillShape(desc))
                return false;

            string skillId = ResolveSkillId(path, skill);
            string shortKey = LastSegment(skillId);
            string label = desc.GetString("Label", shortKey);
            string category = desc.GetString("Category", "");
            string professionType = desc.GetString("ProfessionType", "");

            spell = new SpellData
            {
                ShortName = shortKey,
                SkillId = skillId,
                DisplayName = string.IsNullOrWhiteSpace(label) ? shortKey : label,
                AttackType = ParseAttackType(desc.GetString("WeaponType", null), AttackType.MAGIC),
                DamageType = ParseDamageElement(desc.GetString("DamageType", null), DamageElement.PHYSICAL),
                CooldownF32 = desc.HasProperty("CoolDown") ? desc.GetFixed32("CoolDown", 0) : desc.GetFixed32("Cooldown", 0),
                CoolDownInc = desc.GetInt("CoolDownInc", 0),
                Range = desc.GetInt("Range", 0),
                InitUseRangeF32 = desc.GetFixed32("InitUseRange", 64000),
                ClientSyncToleranceF32 = desc.GetFixed32("ClientSyncTolerance", 0),
                RepeatCount = Math.Max(1, desc.GetInt("RepeatCount", 1)),
                AnimationId = desc.GetInt("AnimationID", 0),
                ManaCostModF32 = desc.GetFixed32Ceiling("ManaCostMod", 0),
                GoldValueModF32 = desc.GetFixed32("GoldValueMod", 0),
                MaxSkillLevel = desc.GetInt("MaxSkillLevel", 0),
                RequiredLevel = desc.GetInt("RequiredLevel", 1),
                RequiredLevelIncF32 = desc.GetFixed32("RequiredLevelInc", 5 * 0x100),
                ProfessionType = professionType,
                ProfessionTypeMask = ParseProfessionTypeMask(professionType),
                TargetType = desc.GetString("TargetType", ""),
                InstantUse = desc.GetBool("InstantUse", false),
                AddModifierWhileClosing = desc.GetBool("AddModifierWhileClosing", false),
                CastModifierId = desc.GetString("CastModifier", null),
                AdjustCooldownByWeapon = desc.GetBool("AdjustCooldownByWeapon", false),
                InitUseResetsCooldown = desc.GetBool("InitUseResetsCooldown", false),
                EffectId = desc.GetString("Effect", null)
            };
            spell.CooldownTicks = Fixed32SecondsToTicks(spell.CooldownF32);
            ParseCastModifier(spell, skill);

            string effectPath = desc.GetString("Effect", null);
            GCNode effectRoot = ResolveAuthoredNodeReference(effectPath, skill);
            if (effectRoot != null)
            {
                BuildOrderedEffectIR(spell, effectRoot, effectPath);
                ParseEffectTree(spell, effectRoot, skill);
            }

            if (TryResolvePlayerAnimationTimingKey(spell.AnimationId, null, out int frames, out int trigger))
            {
                spell.AnimationLengthFrames = frames;
                spell.AnimationTriggerFrames = trigger;
            }

            spell.SkillCategory = ParseSkillCategory(category, spell);
            ParseFriendlySummon(spell, skill, effectRoot);
            return true;
        }

        private static void ParseCastModifier(SpellData spell, GCNode skillContext)
        {
            if (spell == null || string.IsNullOrWhiteSpace(spell.CastModifierId))
                return;

            var cast = new SpellCastModifierData
            {
                ModifierId = spell.CastModifierId
            };
            spell.CastModifier = cast;

            GCNode modifier = ResolveAuthoredNodeReference(spell.CastModifierId, skillContext);
            if (modifier == null)
                return;

            cast.Resolved = true;
            cast.PackageEntryId = modifier.PackageEntryId;
            cast.CanonicalPath = NormalizePath(modifier.CanonicalPath ?? spell.CastModifierId);
            cast.Family = ResolveModifierFamily(modifier);
            cast.DurationTicks = modifier.GetInt("Duration", 0);
            foreach (KeyValuePair<string, string> property in modifier.EnumeratePropertiesInOrder())
                cast.ModifierProperties.Add(property.Key);

            GCNode description = null;
            foreach (GCNode child in modifier.EnumerateChildrenInOrder())
            {
                if (string.Equals(child?.Name, "Description", StringComparison.OrdinalIgnoreCase))
                {
                    description = ResolveInheritedNode(child);
                    continue;
                }
                cast.ModifierChildren.Add(ResolveCastModifierChildFamily(child));
            }
            if (description == null)
                return;

            cast.DescriptionPackageEntryId = description.PackageEntryId;
            cast.DescriptionCanonicalPath = NormalizePath(description.CanonicalPath);
            cast.DescriptionFamily = ResolveCastModifierChildFamily(description);
            cast.VisualId = description.GetString("Visual", null);
            cast.InitSoundId = description.GetString("InitSound", null);
            cast.RemoveOnDeath = description.GetBool("RemoveOnDeath", false);
            cast.StackRule = description.GetString("StackRule", null);
            foreach (KeyValuePair<string, string> property in description.EnumeratePropertiesInOrder())
                cast.DescriptionProperties.Add(property.Key);
            foreach (GCNode child in description.EnumerateChildrenInOrder())
                cast.DescriptionChildren.Add(ResolveCastModifierChildFamily(child));
        }

        private static string ResolveCastModifierChildFamily(GCNode node)
        {
            if (node == null)
                return "Unknown";
            if (AuthoredExtends(node, "AttributeModifierDesc")) return "AttributeModifierDesc";
            if (AuthoredExtends(node, "ProcModifierDesc")) return "ProcModifierDesc";
            if (AuthoredExtends(node, "ModifierDesc")) return "ModifierDesc";
            if (AuthoredExtends(node, "Attribute")) return "Attribute";
            return string.IsNullOrWhiteSpace(node.Extends) ? "Container" : NormalizePath(node.Extends);
        }

        private static bool HasSkillShape(GCNode desc)
        {
            return desc.HasProperty("Effect") ||
                   desc.HasProperty("MaxSkillLevel") ||
                   desc.HasProperty("CoolDown") ||
                   desc.HasProperty("Cooldown") ||
                   desc.HasProperty("ManaCostMod") ||
                   desc.HasProperty("ProfessionType") ||
                   desc.HasProperty("TargetType") ||
                   desc.HasProperty("Category");
        }

        private static void ParseEffectTree(SpellData spell, GCNode effectRoot, GCNode skillContext)
        {
            GCNode aoe = FindFirstEffectNode(effectRoot, "SpellAOEEffect");
            if (aoe != null)
                ParseAoE(spell, aoe);

            GCNode weaponDamage = FindFirstEffectNode(effectRoot, "SpellWeaponDamageEffect");
            if (weaponDamage != null)
            {
                ParseWeaponDamage(spell, weaponDamage);
                GCNode inheritedWeaponDamage = ResolveInheritedNode(weaponDamage);
                string nestedEffectPath = inheritedWeaponDamage?.GetString("Effect", null);
                if (!string.IsNullOrWhiteSpace(nestedEffectPath))
                {
                    GCNode nestedEffect = ResolveAuthoredNodeReference(nestedEffectPath, skillContext);
                    if (nestedEffect != null)
                    {
                        if (spell.HasImmediateWeaponDamageEffect && string.IsNullOrWhiteSpace(spell.ProjectileEffectId))
                            spell.ProjectileEffectId = nestedEffectPath;
                        ParseModifier(spell, nestedEffect, skillContext);
                        GCNode nestedDamage = FindFirstEffectNode(nestedEffect, "SpellDamageEffect");
                        if (nestedDamage != null && spell.DamageModF32 <= 0)
                            ParseDamage(spell, nestedDamage);
                        GCNode nestedKnockDown = FindFirstEffectNode(nestedEffect, "SpellKnockDownEffect");
                        if (nestedKnockDown != null)
                            ParseKnockDown(spell, nestedKnockDown);
                    }
                }
            }

            GCNode damage = FindFirstEffectNode(effectRoot, "SpellDamageEffect");
            if (damage != null)
                ParseDamage(spell, damage);

            GCNode chain = FindFirstEffectNode(effectRoot, "SpellChainEffect");
            if (chain != null)
                ParseChain(spell, chain, skillContext);

            ParseProjectile(spell, effectRoot, skillContext);
            ParseModifier(spell, effectRoot, skillContext);

            GCNode knockDown = FindFirstEffectNode(effectRoot, "SpellKnockDownEffect");
            if (knockDown != null)
                ParseKnockDown(spell, knockDown);
            spell.HasTeleportEffect = FindFirstEffectNode(effectRoot, "SpellTeleportEffect") != null;
            spell.HasFear = FindFirstEffectNode(effectRoot, "SpellFearEffect") != null;
            spell.HasSlow = FindFirstEffectNode(effectRoot, "SpellSlowEffect") != null;
        }

        private static void ParseAoE(SpellData spell, GCNode node)
        {
            node = ResolveInheritedNode(node);
            spell.IsAoE = true;
            spell.HasAoEEffect = true;
            spell.AoERadiusMinF32 = GetFixed32Any(node, "RadiusMin", "Radius", 0);
            spell.AoERadiusMaxF32 = GetFixed32Any(node, "RadiusMax", null, spell.AoERadiusMinF32);
            spell.AoERadiusIncF32 = GetFixed32Any(node, "RadiusInc", null, 0);
            spell.AoERadiusF32 = spell.ResolveAoERadiusF32(1);
            ParseNumTargets(spell, node);
        }

        private static void ParseWeaponDamage(SpellData spell, GCNode node)
        {
            node = ResolveInheritedNode(node);
            spell.IsWeaponSkill = true;
            spell.AttackType = ParseAttackType(node.GetString("AttackType", null), spell.AttackType);
            spell.DamageType = ParseDamageElement(node.GetString("DamageType", null), spell.DamageType);
            if (spell.DamageModF32 <= 0) spell.DamageModF32 = 0x100;
            spell.SkillDamageModMin = node.GetInt("DamageModMin", spell.SkillDamageModMin);
            spell.SkillDamageModMax = node.HasProperty("DamageModMax") ? node.GetInt("DamageModMax", 0) : spell.SkillDamageModMax;
            spell.SkillDamageModInc = node.GetInt("DamageModInc", spell.SkillDamageModInc);
            spell.WeaponEffectDamageModMin = spell.SkillDamageModMin;
            spell.WeaponEffectDamageModMax = spell.SkillDamageModMax;
            spell.WeaponEffectDamageModInc = spell.SkillDamageModInc;
            spell.ARModMin = node.GetInt("ARModMin", spell.ARModMin);
            spell.ARModMax = node.HasProperty("ARModMax") ? node.GetInt("ARModMax", 0) : spell.ARModMax;
            spell.ARModInc = node.GetInt("ARModInc", spell.ARModInc);
            spell.WeaponEffectArcMinF32 = node.GetFixed32("ArcMin", spell.WeaponEffectArcMinF32);
            spell.WeaponEffectArcMaxF32 = node.HasProperty("ArcMax") ? node.GetFixed32("ArcMax", 0) : spell.WeaponEffectArcMaxF32;
            spell.WeaponEffectArcIncF32 = node.GetFixed32("ArcInc", spell.WeaponEffectArcIncF32);
            spell.HasImmediateWeaponDamageEffect = true;
            ParseNumTargets(spell, node);
            if (spell.NumTargetsMaxF32 > 0x100 || spell.NumTargetsMinF32 > 0x100)
                spell.IsAoE = true;
        }

        private static void ParseDamage(SpellData spell, GCNode node)
        {
            node = ResolveInheritedNode(node);
            spell.HasSpellDamageEffect = true;
            spell.AttackType = ParseAttackType(node.GetString("AttackType", null), spell.AttackType);
            spell.DamageType = ParseDamageElement(node.GetString("DamageType", null), spell.DamageType);
            spell.DamageModF32 = node.GetFixed32Ceiling("DamageMod", spell.DamageModF32);
            spell.DamageVolatilityF32 = node.GetFixed32Ceiling("DamageVolatility", spell.DamageVolatilityF32);
            spell.CriticalChanceF32 = node.GetFixed32("CriticalChance", spell.CriticalChanceF32);
            spell.DamageStunMod = node.GetInt("StunMod", spell.DamageStunMod);
            spell.DamageStunModFactorF32 = node.GetFixed32("StunModFactor", spell.DamageStunModFactorF32);
            spell.DamageStunModIncF32 = node.GetFixed32("StunModInc", spell.DamageStunModIncF32);
            spell.ChanceF32 = ResolveSpellEffectChanceWire(node);
        }

        private static void ParseKnockDown(SpellData spell, GCNode node)
        {
            node = ResolveInheritedNode(node);
            spell.HasSpellKnockDownEffect = true;
            spell.SpellKnockDownStrengthMin = node.GetInt("StrengthMin", spell.SpellKnockDownStrengthMin);
            spell.SpellKnockDownStrengthMax = node.HasProperty("StrengthMax") ? node.GetInt("StrengthMax", spell.SpellKnockDownStrengthMin) : spell.SpellKnockDownStrengthMax;
            spell.SpellKnockDownStrengthInc = node.GetInt("StrengthInc", spell.SpellKnockDownStrengthInc);
            spell.SpellKnockDownChanceF32 = ResolveSpellEffectChanceWire(node);
        }

        private static void ParseChain(SpellData spell, GCNode node, GCNode skillContext)
        {
            node = ResolveInheritedNode(node);
            spell.IsChainSpell = true;
            spell.NumChains = node.GetInt("NumChains", node.GetInt("NumTargets", spell.NumChains));
            spell.NumChainsIncrementF32 = node.GetFixed32("NumChainsIncrement", spell.NumChainsIncrementF32);
            spell.ChainRange = node.GetInt("ChainRange", node.GetInt("Range", spell.ChainRange));
            spell.ChainDelayTicks = node.GetInt("ChainDelay", spell.ChainDelayTicks);
            spell.ChainLifespanTicks = node.GetInt("ChainLifespan", spell.ChainLifespanTicks);
            spell.NumForks = node.GetInt("NumForks", spell.NumForks);

            string chainProjectilePath = node.GetString("ChainProjectile", null);
            GCNode chainProjectile = ResolveAuthoredNodeReference(chainProjectilePath, skillContext);
            GCNode chainProjectileDesc = ResolveInheritedNode(chainProjectile?.GetChild("Description") ?? chainProjectile);
            if (chainProjectileDesc != null)
            {
                spell.NumChains = chainProjectileDesc.GetInt("NumChains", chainProjectileDesc.GetInt("NumTargets", spell.NumChains));
                spell.NumChainsIncrementF32 = chainProjectileDesc.GetFixed32("NumChainsIncrement", spell.NumChainsIncrementF32);
                spell.ChainRange = chainProjectileDesc.GetInt("ChainRange", chainProjectileDesc.GetInt("Range", spell.ChainRange));
                spell.ChainDelayTicks = chainProjectileDesc.GetInt("ChainDelay", spell.ChainDelayTicks);
                spell.ChainLifespanTicks = chainProjectileDesc.GetInt("ChainLifespan", spell.ChainLifespanTicks);
                spell.NumForks = chainProjectileDesc.GetInt("NumForks", spell.NumForks);
                spell.ProjectileEffectId = chainProjectileDesc.GetString("Effect", spell.ProjectileEffectId);
            }

            if (string.IsNullOrWhiteSpace(spell.ProjectileEffectId))
                return;

            GCNode chainProjectileRuntimeEffect = ResolveAuthoredNodeReference(spell.ProjectileEffectId, chainProjectile ?? skillContext);
            if (chainProjectileRuntimeEffect != null)
            {
                ParseProjectileRuntimeEffect(spell, chainProjectileRuntimeEffect, chainProjectile ?? skillContext);
            }
        }

        private static void ParseProjectile(SpellData spell, GCNode effectRoot, GCNode skillContext)
        {
            GCNode burstEffect = FindFirstEffectNode(effectRoot, "SpellBurstEffect");
            if (burstEffect != null)
            {
                burstEffect = ResolveInheritedNode(burstEffect);
                spell.HasBurstEffect = true;
                spell.BurstArcMinF32 = burstEffect.GetFixed32("ArcMin", spell.BurstArcMinF32);
                spell.BurstArcMaxF32 = burstEffect.GetFixed32("ArcMax", spell.BurstArcMaxF32);
                spell.BurstCountMinF32 = burstEffect.GetFixed32("BurstCountMin", spell.BurstCountMinF32);
                spell.BurstCountMaxF32 = burstEffect.GetFixed32("BurstCountMax", spell.BurstCountMaxF32);
                spell.BurstOffsetF32 = burstEffect.GetFixed32("BurstOffset", spell.BurstOffsetF32);
                spell.BurstDistanceF32 = burstEffect.GetFixed32("BurstDistance", spell.BurstDistanceF32);
            }

            GCNode projectileEffect = FindFirstEffectNode(effectRoot, "SpellProjectileEffect");
            if (projectileEffect == null)
                return;

            projectileEffect = ResolveInheritedNode(projectileEffect);
            string projectilePath = projectileEffect.GetString("Projectile", null);
            GCNode projectile = ResolveAuthoredNodeReference(projectilePath, skillContext);
            GCNode projectileDesc = ResolveInheritedNode(projectile?.GetChild("Description") ?? projectile);
            if (projectileDesc != null)
            {
                spell.ProjectileSpeedF32 = projectileDesc.GetFixed32("ProjectileSpeed", spell.ProjectileSpeedF32);
                spell.ProjectileSizeF32 = projectileDesc.GetFixed32("ProjectileSize", spell.ProjectileSizeF32);
                spell.ProjectileOffsetF32 = projectileDesc.GetFixed32("ProjectileOffset", spell.ProjectileOffsetF32);
                spell.ProjectileLifespanF32 = projectileDesc.GetFixed32("ProjectileLifespan", spell.ProjectileLifespanF32);
                spell.ProjectileEffectId = projectileDesc.GetString("Effect", spell.ProjectileEffectId);
            }

            spell.RepeatCount = Math.Max(1, projectileEffect.GetInt("RepeatCount", spell.RepeatCount));
            if (string.IsNullOrWhiteSpace(spell.ProjectileEffectId))
                return;

            GCNode projectileRuntimeEffect = ResolveAuthoredNodeReference(spell.ProjectileEffectId, projectile ?? skillContext);
            if (projectileRuntimeEffect == null)
                return;

            ParseProjectileRuntimeEffect(spell, projectileRuntimeEffect, projectile ?? skillContext);
        }

        private static void ParseProjectileRuntimeEffect(SpellData spell, GCNode projectileRuntimeEffect, GCNode skillContext)
        {
            GCNode damage = FindFirstEffectNode(projectileRuntimeEffect, "SpellDamageEffect");
            if (damage != null)
                ParseDamage(spell, damage);

            ParseModifier(spell, projectileRuntimeEffect, skillContext);

            GCNode knockDown = FindFirstEffectNode(projectileRuntimeEffect, "SpellKnockDownEffect");
            if (knockDown != null)
                ParseKnockDown(spell, knockDown);
        }

        private static void ParseModifier(SpellData spell, GCNode effectRoot, GCNode skillContext)
        {
            foreach (GCNode node in EnumerateEffectTree(effectRoot, 0, new HashSet<GCNode>()))
                if (AuthoredExtends(node, "SpellModEffect"))
                    ParseModifierNode(spell, node, skillContext);
        }

        private static void ParseModifierNode(SpellData spell, GCNode modEffect, GCNode skillContext)
        {
            GCNode authoredModEffect = modEffect;
            modEffect = ResolveInheritedNode(modEffect);
            if (modEffect == null)
                return;
            string modifierPath = modEffect.GetString("Modifier", null);
            if (string.IsNullOrWhiteSpace(modifierPath))
                return;

            var authoredModifier = new SpellModifierData
            {
                EffectId = !string.IsNullOrWhiteSpace(modEffect.CanonicalPath) ? modEffect.CanonicalPath : spell.EffectId,
                ModifierId = modifierPath,
                DurationF32 = modEffect.GetFixed32("Duration", 0),
                DurationIncF32 = modEffect.GetFixed32("DurationInc", 0),
                PackageEntryId = authoredModEffect?.PackageEntryId ?? modEffect.PackageEntryId,
                CanonicalPath = NormalizePath(authoredModEffect?.CanonicalPath ?? modEffect.CanonicalPath),
                EffectOrder = FindOrderedEffectOrder(spell, authoredModEffect ?? modEffect)
            };

            GCNode modifier = ResolveAuthoredNodeReference(modifierPath, skillContext);
            GCNode modifierDesc = ResolveInheritedNode(modifier?.GetChild("Description") ?? modifier);
            if (modifierDesc == null)
                return;

            authoredModifier.ModifierFamily = ResolveModifierFamily(modifier);
            authoredModifier.ModifierPackageEntryId = modifier?.PackageEntryId ?? 0;
            authoredModifier.ModifierCanonicalPath = NormalizePath(modifier?.CanonicalPath ?? modifierPath);
            authoredModifier.FrequencyF32 = modifierDesc.GetFixed32("Frequency", 0);
            authoredModifier.StackRule = modifierDesc.GetString("StackRule", "");
            authoredModifier.RemoveOnDeath = modifierDesc.GetBool("RemoveOnDeath", false);
            authoredModifier.TerminateWhenHitChance = modifierDesc.GetInt("TerminateWhenHitChance", 0);
            authoredModifier.ModifierEffectId = modifierDesc.GetString("Effect", null);
            int attributeOrder = 0;
            foreach (GCNode attributeNode in EnumerateEffectTree(modifierDesc, 0, new HashSet<GCNode>()))
            {
                if (!AuthoredExtends(attributeNode, "Attribute"))
                    continue;
                string attribute = attributeNode.GetString("Attribute", null);
                if (string.IsNullOrWhiteSpace(attribute))
                    continue;
                authoredModifier.Attributes.Add(new SpellAttributeModifierData
                {
                    Attribute = attribute,
                    OverrideTable = attributeNode.GetBool("OverrideTable", false),
                    ValueF32 = attributeNode.GetFixed32("Value", 0),
                    ValueIncF32 = attributeNode.GetFixed32("ValueInc", 0),
                    Order = attributeOrder++,
                    PackageEntryId = attributeNode.PackageEntryId,
                    CanonicalPath = NormalizePath(attributeNode.CanonicalPath)
                });
            }
            int existingModifierIndex = spell.ModifierEffects.FindIndex(existing =>
                string.Equals(existing.ModifierId, authoredModifier.ModifierId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(existing.CanonicalPath, authoredModifier.CanonicalPath, StringComparison.OrdinalIgnoreCase)
                && existing.PackageEntryId == authoredModifier.PackageEntryId);
            if (existingModifierIndex < 0)
                spell.ModifierEffects.Add(authoredModifier);

            bool materializeLegacyProjection = string.IsNullOrWhiteSpace(spell.ProjectileModifierId);
            if (!materializeLegacyProjection)
                return;
            spell.ProjectileModifierId = modifierPath;
            spell.ProjectileModifierDurationF32 = authoredModifier.DurationF32;
            spell.ProjectileModifierFrequencyF32 = authoredModifier.FrequencyF32;
            spell.ProjectileModifierStackRule = authoredModifier.StackRule;
            string modifierEffectPath = modifierDesc.GetString("Effect", null);
            if (string.IsNullOrWhiteSpace(modifierEffectPath))
                return;

            spell.ProjectileModifierEffectId = modifierEffectPath;
            GCNode modifierEffect = ResolveAuthoredNodeReference(modifierEffectPath, modifier ?? skillContext);
            if (modifierEffect != null)
            {
                authoredModifier.ModifierEffectPackageEntryId = modifierEffect.PackageEntryId;
                authoredModifier.ModifierEffectCanonicalPath = NormalizePath(modifierEffect.CanonicalPath ?? modifierEffectPath);
                int modifierEffectOrder = 0;
                authoredModifier.ModifierEffectRoot = BuildReferencedEffectNode(
                    modifierEffect,
                    modifierEffectPath,
                    0,
                    0,
                    ref modifierEffectOrder,
                    new HashSet<GCNode>());
            }
            GCNode damage = FindFirstEffectNode(modifierEffect, "SpellDamageEffect");
            if (damage == null)
                return;

            damage = ResolveInheritedNode(damage);
            spell.ProjectileModifierAttackType = ParseAttackType(damage.GetString("AttackType", null), spell.AttackType);
            spell.ProjectileModifierDamageType = ParseDamageElement(damage.GetString("DamageType", null), spell.DamageType);
            spell.ProjectileModifierDamageModF32 = damage.GetFixed32Ceiling("DamageMod", spell.ProjectileModifierDamageModF32);
            spell.ProjectileModifierDamageVolatilityF32 = damage.GetFixed32Ceiling("DamageVolatility", spell.ProjectileModifierDamageVolatilityF32);
            spell.ProjectileModifierCriticalChanceF32 = damage.GetFixed32("CriticalChance", spell.ProjectileModifierCriticalChanceF32);
            spell.ProjectileModifierChanceF32 = ResolveSpellEffectChanceWire(damage);
            spell.ProjectileModifierDamageStunMod = damage.GetInt("StunMod", spell.ProjectileModifierDamageStunMod);
            spell.ProjectileModifierDamageStunModFactorF32 = damage.GetFixed32("StunModFactor", spell.ProjectileModifierDamageStunModFactorF32);
            spell.ProjectileModifierDamageStunModIncF32 = damage.GetFixed32("StunModInc", spell.ProjectileModifierDamageStunModIncF32);
        }

        private static int FindOrderedEffectOrder(SpellData spell, GCNode node)
        {
            if (spell?.OrderedEffects == null || node == null)
                return -1;
            string canonicalPath = NormalizePath(node.CanonicalPath);
            if (node.PackageEntryId == 0 && string.IsNullOrWhiteSpace(canonicalPath))
                return -1;
            for (int nodeIndex = 0; nodeIndex < spell.OrderedEffects.Count; nodeIndex++)
            {
                SpellEffectNodeData candidate = spell.OrderedEffects[nodeIndex];
                if (candidate == null)
                    continue;
                if (node.PackageEntryId == candidate.PackageEntryId
                    && string.Equals(canonicalPath, candidate.CanonicalPath, StringComparison.OrdinalIgnoreCase))
                    return candidate.Order;
            }
            return -1;
        }

        private static string ResolveModifierFamily(GCNode modifier)
        {
            if (AuthoredExtends(modifier, "AttributeModifier")) return "AttributeModifier";
            if (AuthoredExtends(modifier, "ProcModifier")) return "ProcModifier";
            if (AuthoredExtends(modifier, "AuraMod")) return "AuraMod";
            if (AuthoredExtends(modifier, "EffectMod")) return "EffectMod";
            if (AuthoredExtends(modifier, "CowardiceModifier")) return "CowardiceModifier";
            if (AuthoredExtends(modifier, "Modifier")) return "Modifier";
            return string.IsNullOrWhiteSpace(modifier?.Extends) ? "Unknown" : NormalizePath(modifier.Extends);
        }

        private static IEnumerable<GCNode> EnumerateDirectEffectChildren(GCNode node)
        {
            if (node == null)
                yield break;
            foreach (GCNode child in node.EnumerateChildrenInOrder())
                yield return child;
        }

        private static void ParseNumTargets(SpellData spell, GCNode node)
        {
            spell.NumTargetsMinF32 = GetFixed32Any(node, "NumTargetsMin", "NumTargets", spell.NumTargetsMinF32);
            spell.NumTargetsMaxF32 = GetFixed32Any(node, "NumTargetsMax", null, spell.NumTargetsMaxF32);
            spell.NumTargetsIncF32 = GetFixed32Any(node, "NumTargetsInc", null, spell.NumTargetsIncF32);
        }

        private static SkillCategory ParseSkillCategory(string category, SpellData spell)
        {
            string value = CompactKey(category);
            if (value.Equals("Offensive", StringComparison.OrdinalIgnoreCase)) return spell.IsWeaponSkill ? SkillCategory.WeaponSkill : SkillCategory.Offensive;
            if (value.Equals("WeaponSkill", StringComparison.OrdinalIgnoreCase)) return SkillCategory.WeaponSkill;
            if (value.Equals("CrowdControl", StringComparison.OrdinalIgnoreCase)) return SkillCategory.CrowdControl;
            if (value.Equals("Debuff", StringComparison.OrdinalIgnoreCase) || value.Equals("Curse", StringComparison.OrdinalIgnoreCase) || value.Equals("Curses", StringComparison.OrdinalIgnoreCase)) return SkillCategory.Debuff;
            if (value.Equals("Heal", StringComparison.OrdinalIgnoreCase) || value.Equals("Healing", StringComparison.OrdinalIgnoreCase)) return SkillCategory.Heal;
            if (value.Equals("Summon", StringComparison.OrdinalIgnoreCase) || value.Equals("Summoning", StringComparison.OrdinalIgnoreCase)) return SkillCategory.Summon;
            if (value.Equals("Passive", StringComparison.OrdinalIgnoreCase)) return SkillCategory.Passive;
            if (spell.IsWeaponSkill) return SkillCategory.WeaponSkill;
            if (spell.HasAnyDamage) return SkillCategory.Offensive;
            string name = spell.ShortName ?? "";
            if (name.IndexOf("Passive", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Trait", StringComparison.OrdinalIgnoreCase) >= 0)
                return SkillCategory.Passive;
            return SkillCategory.Utility;
        }

        public static bool TryResolvePlayerAnimationTiming(int animationId, PlayerState state, out int frames, out int trigger)
        {
            int animationKey = ResolvePlayerAnimationKey(animationId, state);
            return TryResolvePlayerAnimationTimingKey(animationKey, state, out frames, out trigger);
        }

        public static bool TryResolvePlayerWeaponAnimation(
            int animationId,
            PlayerState state,
            out int animationKey,
            out int frames,
            out int trigger,
            out int soundTrigger,
            out int sourceOffsetFixedX,
            out int sourceOffsetFixedY,
            out int sourceOffsetFixedZ)
        {
            animationKey = ResolvePlayerAnimationKey(animationId, state);
            frames = 0;
            trigger = 0;
            soundTrigger = 0;
            sourceOffsetFixedX = 0;
            sourceOffsetFixedY = 0;
            sourceOffsetFixedZ = 12 * UnitMover.Fixed;
            if (animationKey <= 0)
                return false;

            var matches = new List<(int Frames, int Trigger, int SoundTrigger, int SourceX, int SourceY, int SourceZ)>();
            foreach (GCNode animations in EnumeratePlayerAnimationLists(state))
                AppendPlayerWeaponAnimationMatches(animations, animationKey, matches);
            if (matches.Count == 0)
                return false;

            var first = matches[0];
            if (matches.Any(match => match != first))
                return false;

            frames = first.Frames;
            trigger = first.Trigger;
            soundTrigger = first.SoundTrigger;
            sourceOffsetFixedX = first.SourceX;
            sourceOffsetFixedY = first.SourceY;
            sourceOffsetFixedZ = first.SourceZ;
            return true;
        }

        private static void BuildOrderedEffectIR(SpellData spell, GCNode effectRoot, string effectPath)
        {
            spell.EffectRoot = null;
            spell.OrderedEffects.Clear();
            int order = 0;
            spell.EffectRoot = BuildOrderedEffectNode(spell, effectRoot, effectPath, 0, 0, ref order, new HashSet<GCNode>());
        }

        private static SpellEffectNodeData BuildOrderedEffectNode(
            SpellData spell,
            GCNode authoredNode,
            string parentPath,
            int siblingOrder,
            int depth,
            ref int order,
            HashSet<GCNode> ancestry)
        {
            if (spell == null || authoredNode == null || depth > 48 || !ancestry.Add(authoredNode))
                return null;
            string canonicalPath = !string.IsNullOrWhiteSpace(authoredNode.CanonicalPath)
                ? NormalizePath(authoredNode.CanonicalPath)
                : !string.IsNullOrWhiteSpace(parentPath) && !string.IsNullOrWhiteSpace(authoredNode.Name)
                    ? NormalizePath(parentPath + "." + authoredNode.Name)
                    : NormalizePath(parentPath);
            var node = new SpellEffectNodeData
            {
                Order = order++,
                SiblingOrder = siblingOrder,
                Depth = depth,
                PackageEntryId = authoredNode.PackageEntryId,
                CanonicalPath = canonicalPath,
                Extends = NormalizePath(authoredNode.Extends),
                Family = ResolveOrderedEffectFamily(authoredNode),
                ModifierId = AuthoredExtends(authoredNode, "SpellModEffect") ? authoredNode.GetString("Modifier", null) : null,
                ReferencedEffectId = authoredNode.GetString("Effect", null),
                ChanceF32 = ResolveSpellEffectChanceWire(authoredNode)
            };
            spell.OrderedEffects.Add(node);
            if (string.Equals(node.Family, "SpellChainEffect", StringComparison.Ordinal))
                BuildChainImpactReference(node, authoredNode, ancestry);
            if (string.Equals(node.Family, "SpellWeaponDamageEffect", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(node.ReferencedEffectId))
            {
                GCNode referencedEffect = ResolveAuthoredNodeReference(node.ReferencedEffectId, authoredNode);
                if (referencedEffect != null)
                {
                    int referencedOrder = 0;
                    node.ReferencedEffect = BuildReferencedEffectNode(
                        referencedEffect,
                        node.ReferencedEffectId,
                        0,
                        0,
                        ref referencedOrder,
                        new HashSet<GCNode>(ancestry));
                }
            }
            if (string.Equals(node.Family, "SpellSoundEffect", StringComparison.Ordinal)
                || string.Equals(node.Family, "SpellEffectEffect", StringComparison.Ordinal)
                || string.Equals(node.Family, "SpellRessurectEffect", StringComparison.Ordinal))
                return node;
            int childOrder = 0;
            foreach (GCNode child in authoredNode.EnumerateChildrenInOrder())
            {
                var branch = new HashSet<GCNode>(ancestry);
                SpellEffectNodeData childNode = BuildOrderedEffectNode(spell, child, canonicalPath, childOrder, depth + 1, ref order, branch);
                if (childNode != null)
                    node.Children.Add(childNode);
                childOrder++;
            }
            return node;
        }

        private static SpellEffectNodeData BuildReferencedEffectNode(
            GCNode authoredNode,
            string parentPath,
            int siblingOrder,
            int depth,
            ref int order,
            HashSet<GCNode> ancestry)
        {
            if (authoredNode == null || depth > 48 || !ancestry.Add(authoredNode))
                return null;
            string canonicalPath = !string.IsNullOrWhiteSpace(authoredNode.CanonicalPath)
                ? NormalizePath(authoredNode.CanonicalPath)
                : !string.IsNullOrWhiteSpace(parentPath) && !string.IsNullOrWhiteSpace(authoredNode.Name)
                    ? NormalizePath(parentPath + "." + authoredNode.Name)
                    : NormalizePath(parentPath);
            var node = new SpellEffectNodeData
            {
                Order = order++,
                SiblingOrder = siblingOrder,
                Depth = depth,
                PackageEntryId = authoredNode.PackageEntryId,
                CanonicalPath = canonicalPath,
                Extends = NormalizePath(authoredNode.Extends),
                Family = ResolveOrderedEffectFamily(authoredNode),
                ModifierId = AuthoredExtends(authoredNode, "SpellModEffect") ? authoredNode.GetString("Modifier", null) : null,
                ReferencedEffectId = authoredNode.GetString("Effect", null),
                ChanceF32 = ResolveSpellEffectChanceWire(authoredNode)
            };
            if (string.Equals(node.Family, "SpellChainEffect", StringComparison.Ordinal))
                BuildChainImpactReference(node, authoredNode, ancestry);
            if (string.Equals(node.Family, "SpellWeaponDamageEffect", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(node.ReferencedEffectId))
            {
                GCNode nestedReference = ResolveAuthoredNodeReference(node.ReferencedEffectId, authoredNode);
                if (nestedReference != null)
                {
                    int nestedOrder = 0;
                    node.ReferencedEffect = BuildReferencedEffectNode(
                        nestedReference,
                        node.ReferencedEffectId,
                        0,
                        0,
                        ref nestedOrder,
                        new HashSet<GCNode>(ancestry));
                }
            }
            if (string.Equals(node.Family, "SpellSoundEffect", StringComparison.Ordinal)
                || string.Equals(node.Family, "SpellEffectEffect", StringComparison.Ordinal)
                || string.Equals(node.Family, "SpellRessurectEffect", StringComparison.Ordinal))
                return node;
            int childOrder = 0;
            foreach (GCNode child in authoredNode.EnumerateChildrenInOrder())
            {
                var branch = new HashSet<GCNode>(ancestry);
                SpellEffectNodeData childNode = BuildReferencedEffectNode(child, canonicalPath, childOrder, depth + 1, ref order, branch);
                if (childNode != null)
                    node.Children.Add(childNode);
                childOrder++;
            }
            return node;
        }

        private static void BuildChainImpactReference(
            SpellEffectNodeData node,
            GCNode authoredNode,
            HashSet<GCNode> ancestry)
        {
            node.ChainProjectileId = authoredNode.GetString("ChainProjectile", null);
            if (string.IsNullOrWhiteSpace(node.ChainProjectileId))
                return;
            GCNode projectile = ResolveAuthoredNodeReference(node.ChainProjectileId, authoredNode);
            if (projectile == null)
                return;
            node.ChainProjectilePackageEntryId = projectile.PackageEntryId;
            node.ChainProjectileCanonicalPath = NormalizePath(projectile.CanonicalPath ?? node.ChainProjectileId);
            GCNode description = ResolveInheritedNode(projectile.GetChild("Description") ?? projectile);
            node.ReferencedEffectId = description?.GetString("Effect", null);
            if (string.IsNullOrWhiteSpace(node.ReferencedEffectId))
                return;
            GCNode impact = ResolveAuthoredNodeReference(node.ReferencedEffectId, projectile);
            if (impact == null)
                return;
            int impactOrder = 0;
            node.ReferencedEffect = BuildReferencedEffectNode(
                impact,
                node.ReferencedEffectId,
                0,
                0,
                ref impactOrder,
                new HashSet<GCNode>(ancestry));
        }

        private static string ResolveOrderedEffectFamily(GCNode node)
        {
            if (AuthoredExtends(node, "SpellRessurectEffect")) return "SpellRessurectEffect";
            if (AuthoredExtends(node, "SpellAOEEffect")) return "SpellAOEEffect";
            if (AuthoredExtends(node, "SpellWeaponDamageEffect")) return "SpellWeaponDamageEffect";
            if (AuthoredExtends(node, "SpellDamageEffect")) return "SpellDamageEffect";
            if (AuthoredExtends(node, "SpellChainEffect")) return "SpellChainEffect";
            if (AuthoredExtends(node, "SpellBurstEffect")) return "SpellBurstEffect";
            if (AuthoredExtends(node, "SpellProjectileEffect")) return "SpellProjectileEffect";
            if (AuthoredExtends(node, "SpellModEffect")) return "SpellModEffect";
            if (AuthoredExtends(node, "SpellKnockDownEffect")) return "SpellKnockDownEffect";
            if (AuthoredExtends(node, "SpellKnockBackEffect")) return "SpellKnockBackEffect";
            if (AuthoredExtends(node, "SpellTeleportEffect")) return "SpellTeleportEffect";
            if (AuthoredExtends(node, "SpellFearEffect")) return "SpellFearEffect";
            if (AuthoredExtends(node, "SpellSlowEffect")) return "SpellSlowEffect";
            if (AuthoredExtends(node, "SpellSpawnEffect")) return "SpellSpawnEffect";
            if (AuthoredExtends(node, "SpellSoundEffect")) return "SpellSoundEffect";
            if (AuthoredExtends(node, "SpellEffectEffect")) return "SpellEffectEffect";
            if (AuthoredExtends(node, "SpellSnapToGroundEffect")) return "SpellSnapToGroundEffect";
            if (AuthoredExtends(node, "SpellEffect")) return "SpellEffect";
            return string.IsNullOrWhiteSpace(node?.Extends) ? "Container" : NormalizePath(node.Extends);
        }

        public static bool TryResolvePlayerAnimationSourceOffsetFixed(int animationId, PlayerState state, out int fixedX, out int fixedY, out int fixedZ)
        {
            int animationKey = ResolvePlayerAnimationKey(animationId, state);
            return TryResolvePlayerAnimationSourceOffsetKeyFixed(animationKey, state, out fixedX, out fixedY, out fixedZ);
        }

        private static int ResolvePlayerAnimationKey(int animationId, PlayerState state)
        {
            if (animationId <= 0)
                return animationId;
            int weaponClassId = DamageResolver.ResolveWeaponClassId(state);
            if (weaponClassId <= 0)
                return animationId;
            return animationId + weaponClassId * 100;
        }

        private static IEnumerable<GCNode> EnumeratePlayerAnimationLists(PlayerState state)
        {
            var gc = GCDatabase.Instance;
            if (gc == null || !gc.IsLoaded)
                yield break;

            if (!string.IsNullOrWhiteSpace(state?.AvatarGcType))
            {
                GCNode avatar = gc.ResolveWithInheritance(state.AvatarGcType);
                GCNode avatarDescription = ResolveInheritedNode(avatar?.GetChild("Description"));
                string animationPath = avatarDescription?.GetString("Animations", null);
                if (!string.IsNullOrWhiteSpace(animationPath))
                {
                    GCNode animations = gc.ResolveWithInheritance(animationPath);
                    if (animations != null)
                        yield return animations;
                }
                yield break;
            }

            var seen = new HashSet<GCNode>();
            foreach (string path in PLAYER_ANIMATION_LIST_PATHS)
            {
                GCNode animations = gc.ResolveWithInheritance(path);
                if (animations != null && seen.Add(animations))
                    yield return animations;
            }
            foreach (string path in gc.RegisteredPaths)
            {
                string normalized = NormalizePath(path);
                if (!normalized.StartsWith("avatar.races.", StringComparison.OrdinalIgnoreCase) ||
                    !normalized.EndsWith("Animations", StringComparison.OrdinalIgnoreCase))
                    continue;
                GCNode animations = gc.ResolveWithInheritance(path);
                if (animations != null && seen.Add(animations))
                    yield return animations;
            }
        }

        private static bool TryResolvePlayerAnimationTimingKey(int animationKey, PlayerState state, out int frames, out int trigger)
        {
            frames = 0;
            trigger = 0;
            if (animationKey <= 0)
                return false;

            var gc = GCDatabase.Instance;
            if (gc == null || !gc.IsLoaded)
                return false;

            var matches = new List<(int frames, int trigger)>();
            foreach (GCNode animations in EnumeratePlayerAnimationLists(state))
                AppendAnimationTimingMatches(animations, animationKey, matches);

            if (matches.Count == 0)
                return false;

            var first = matches[0];
            if (matches.Any(m => m.frames != first.frames || m.trigger != first.trigger))
                return false;

            frames = first.frames;
            trigger = first.trigger;
            return true;
        }

        private static bool TryResolvePlayerAnimationSourceOffsetKeyFixed(int animationKey, PlayerState state, out int fixedX, out int fixedY, out int fixedZ)
        {
            fixedX = 0;
            fixedY = 0;
            fixedZ = 0;
            if (animationKey <= 0)
                return false;

            var gc = GCDatabase.Instance;
            if (gc == null || !gc.IsLoaded)
                return false;

            var matches = new List<(int X, int Y, int Z)>();
            foreach (GCNode animations in EnumeratePlayerAnimationLists(state))
                AppendAnimationSourceOffsetMatches(animations, animationKey, matches);

            if (matches.Count == 0)
                return false;

            var first = matches[0];
            if (matches.Any(m => m.X != first.X || m.Y != first.Y || m.Z != first.Z))
                return false;

            fixedX = first.X;
            fixedY = first.Y;
            fixedZ = first.Z;
            return true;
        }

        private static void AppendAnimationTimingMatches(GCNode animations, int animationKey, List<(int frames, int trigger)> matches)
        {
            foreach (GCNode row in EnumerateAnimationRows(animations))
            {
                if (!row.HasProperty("ID"))
                    continue;
                int rowId = row.GetInt("ID", -1);
                if (rowId < 0)
                    continue;
                if (!MatchesAnimationKey(rowId, animationKey))
                    continue;
                int rowFrames = row.GetInt("NumFrames", 0);
                int rowTrigger = row.GetInt("TriggerTime", 0);
                if (rowFrames > 0 && rowTrigger > 0)
                    matches.Add((rowFrames, rowTrigger));
            }
        }

        private static void AppendAnimationSourceOffsetMatches(GCNode animations, int animationKey, List<(int X, int Y, int Z)> matches)
        {
            foreach (GCNode row in EnumerateAnimationRows(animations))
            {
                if (!row.HasProperty("ID"))
                    continue;
                int rowId = row.GetInt("ID", -1);
                if (rowId < 0)
                    continue;
                if (!MatchesAnimationKey(rowId, animationKey))
                    continue;
                if (TryParseSourceOffsetFixed(row, out int fixedX, out int fixedY, out int fixedZ))
                    matches.Add((fixedX, fixedY, fixedZ));
            }
        }

        private static void AppendPlayerWeaponAnimationMatches(
            GCNode animations,
            int animationKey,
            List<(int Frames, int Trigger, int SoundTrigger, int SourceX, int SourceY, int SourceZ)> matches)
        {
            foreach (GCNode row in EnumerateAnimationRows(animations))
            {
                if (!row.HasProperty("ID") || row.GetInt("ID", -1) != animationKey)
                    continue;
                int frames = row.GetInt("NumFrames", 0);
                int trigger = row.GetInt("TriggerTime", 0);
                int soundTrigger = row.GetInt("SoundTriggerTime", 0);
                if (frames <= 0 || trigger < 0 || soundTrigger < 0)
                    continue;
                int sourceX = 0;
                int sourceY = 0;
                int sourceZ = 12 * UnitMover.Fixed;
                if (TryParseSourceOffsetFixed(row, out int authoredX, out int authoredY, out int authoredZ)
                    && (authoredX != 0 || authoredY != 0 || authoredZ != 0))
                {
                    sourceX = authoredX;
                    sourceY = authoredY;
                    sourceZ = authoredZ;
                }
                matches.Add((frames, trigger, soundTrigger, sourceX, sourceY, sourceZ));
            }
        }

        private static bool TryParseSourceOffsetFixed(GCNode animation, out int fixedX, out int fixedY, out int fixedZ)
        {
            fixedX = 0;
            fixedY = 0;
            fixedZ = 0;
            string raw = animation?.GetString("SourceOffset", null);
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            int marker = raw.IndexOf("//", StringComparison.Ordinal);
            if (marker >= 0)
                raw = raw.Substring(0, marker);

            string[] parts = raw.Split(',');
            if (parts.Length < 3)
                return false;

            if (!GCNode.TryParseFixed32(parts[0].Trim(), out fixedX))
                return false;
            if (!GCNode.TryParseFixed32(parts[1].Trim(), out fixedY))
                return false;
            if (!GCNode.TryParseFixed32(parts[2].Trim(), out fixedZ))
                return false;
            return true;
        }

        private static bool MatchesAnimationKey(int rowId, int animationKey)
        {
            if (rowId < 0 || animationKey <= 0)
                return false;
            return rowId == animationKey;
        }

        private static IEnumerable<GCNode> EnumerateAnimationRows(GCNode animations)
        {
            foreach (GCNode row in animations.EnumerateChildrenInOrder())
                yield return ResolveInheritedNode(row);
        }

        private static GCNode ResolveAuthoredNodeReference(string path, GCNode contextRoot)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            string normalized = NormalizePath(path);
            var gc = GCDatabase.Instance;
            GCNode node = gc?.ResolveWithInheritance(normalized);
            if (node != null)
                return node;

            if (contextRoot == null)
                return null;

            string rootName = contextRoot.Name;
            if (!string.IsNullOrWhiteSpace(rootName) &&
                normalized.StartsWith(rootName + ".", StringComparison.OrdinalIgnoreCase))
            {
                string subPath = normalized.Substring(rootName.Length + 1);
                return ResolveChildPath(contextRoot, subPath);
            }

            int dot = normalized.IndexOf('.');
            while (dot >= 0 && dot + 1 < normalized.Length)
            {
                string suffix = normalized.Substring(dot + 1);
                GCNode child = ResolveChildPath(contextRoot, suffix);
                if (child != null)
                    return child;
                dot = normalized.IndexOf('.', dot + 1);
            }

            return null;
        }

        private static GCNode ResolveChildPath(GCNode root, string dottedPath)
        {
            if (root == null || string.IsNullOrWhiteSpace(dottedPath))
                return root;

            GCNode current = root;
            foreach (string part in dottedPath.Split('.'))
            {
                if (current == null || string.IsNullOrWhiteSpace(part))
                    return null;
                current = current.GetChild(part);
            }
            return current;
        }

        private static GCNode FindFirstEffectNode(GCNode root, string clientExtends)
        {
            foreach (GCNode node in EnumerateEffectTree(root, 0, new HashSet<GCNode>()))
                if (AuthoredExtends(node, clientExtends))
                    return node;
            return null;
        }

        private static IEnumerable<GCNode> EnumerateEffectTree(GCNode node, int depth, HashSet<GCNode> seen)
        {
            if (node == null || depth > 48)
                yield break;
            if (!seen.Add(node))
                yield break;

            GCNode resolved = ResolveInheritedNode(node);
            yield return resolved;

            foreach (GCNode child in resolved.EnumerateChildrenInOrder())
            {
                foreach (GCNode inner in EnumerateEffectTree(child, depth + 1, seen))
                    yield return inner;
            }
        }

        private static bool AuthoredExtends(GCNode node, string clientExtends)
        {
            return AuthoredExtends(node, clientExtends, 0);
        }

        private static bool AuthoredExtends(GCNode node, string clientExtends, int depth)
        {
            if (node == null || string.IsNullOrWhiteSpace(clientExtends) || depth > 32)
                return false;
            string ext = NormalizePath(node.Extends ?? string.Empty);
            string authoredName = NormalizePath(node.GetString("Name", string.Empty));
            if (string.Equals(authoredName, clientExtends, StringComparison.OrdinalIgnoreCase)
                || authoredName.EndsWith("." + clientExtends, StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.IsNullOrWhiteSpace(ext))
                return false;
            if (string.Equals(ext, clientExtends, StringComparison.OrdinalIgnoreCase))
                return true;
            if (ext.EndsWith("." + clientExtends, StringComparison.OrdinalIgnoreCase))
                return true;

            GCNode parent = GCDatabase.Instance?.ResolveWithInheritance(ext);
            return parent != null && AuthoredExtends(parent, clientExtends, depth + 1);
        }

        private static GCNode ResolveInheritedNode(GCNode node)
        {
            if (node == null || string.IsNullOrWhiteSpace(node.Extends))
                return node;

            GCNode parent = GCDatabase.Instance?.ResolveWithInheritance(NormalizePath(node.Extends));
            if (parent == null)
                return node;

            return MergeNodes(parent, node);
        }

        private static GCNode MergeNodes(GCNode parent, GCNode child)
        {
            return GCNode.MergeInherited(parent, child);
        }

        private static AttackType ParseAttackType(string value, AttackType fallback)
        {
            string key = CompactKey(value);
            if (key.Equals("Melee", StringComparison.OrdinalIgnoreCase)) return AttackType.MELEE;
            if (key.Equals("Magic", StringComparison.OrdinalIgnoreCase)) return AttackType.MAGIC;
            if (key.Equals("Ranged", StringComparison.OrdinalIgnoreCase) || key.Equals("Range", StringComparison.OrdinalIgnoreCase)) return AttackType.RANGED;
            return fallback;
        }

        private static DamageElement ParseDamageElement(string value, DamageElement fallback)
        {
            string key = CompactKey(value);
            if (key.Equals("Divine", StringComparison.OrdinalIgnoreCase)) return DamageElement.DIVINE;
            if (key.Equals("Fire", StringComparison.OrdinalIgnoreCase)) return DamageElement.FIRE;
            if (key.Equals("Ice", StringComparison.OrdinalIgnoreCase) || key.Equals("Cold", StringComparison.OrdinalIgnoreCase)) return DamageElement.ICE;
            if (key.Equals("Poison", StringComparison.OrdinalIgnoreCase)) return DamageElement.POISON;
            if (key.Equals("Shadow", StringComparison.OrdinalIgnoreCase)) return DamageElement.SHADOW;
            if (key.Equals("Physical", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("Crushing", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("Piercing", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("Slashing", StringComparison.OrdinalIgnoreCase))
                return DamageElement.PHYSICAL;
            return fallback;
        }

        private static int ResolveSpellEffectChanceWire(GCNode node)
        {
            if (node == null)
                return 0x6400;
            return Math.Max(0, Math.Min(0x6400, node.GetFixed32("Chance", 100 << 8)));
        }

        private static ushort Fixed32SecondsToTicks(int secondsF32)
        {
            if (secondsF32 <= 0) return 0;
            long ticks = ((long)secondsF32 * 30) >> 8;
            return (ushort)Math.Min(ushort.MaxValue, ticks);
        }

        public static int ResolveCooldownF32(SpellData spell, int skillLevel, PlayerState state)
        {
            if (spell == null)
                return 0;
            int resolvedLevel = Math.Max(1, skillLevel);
            long cooldownF32 = (long)spell.CooldownF32 + (long)(resolvedLevel - 1) * spell.CoolDownInc * 0x100L;
            cooldownF32 = Math.Clamp(cooldownF32, 0L, int.MaxValue);
            if (spell.AdjustCooldownByWeapon
                && state != null
                && state.WeaponStatsResolved
                && state.WeaponDamageF32 > 0)
                cooldownF32 = Math.Clamp((cooldownF32 * state.WeaponDamageF32) >> 8, 0L, int.MaxValue);
            return (int)cooldownF32;
        }

        public static ushort ResolveCooldownTicks(SpellData spell, int skillLevel, PlayerState state)
        {
            return Fixed32SecondsToTicks(ResolveCooldownF32(spell, skillLevel, state));
        }

        public static int ResolveActiveSkillSpeed(SpellData spell, PlayerState state)
        {
            if (spell == null || (spell.ProfessionTypeMask & 1) == 0)
                return 100;
            ushort attackSpeed = unchecked((ushort)(state?.AttackSpeed ?? 0));
            ushort castSpeedMod = unchecked((ushort)(state?.CastSpeedMod ?? 0));
            ushort speed = unchecked((ushort)(attackSpeed + castSpeedMod));
            return speed < 75 ? 75 : speed;
        }

        public static uint GetManaCost(SpellData spell, int skillLevel, int unitManaCost)
        {
            if (spell == null)
                throw new ArgumentNullException(nameof(spell));
            GCNode globalKnobs = GCDatabase.Instance.GlobalKnobs;
            if (globalKnobs == null
                || !globalKnobs.HasProperty("BaseSkillPowerCost")
                || !globalKnobs.HasProperty("SkillPowerCostPerLevel"))
                throw new InvalidOperationException("GlobalKnobs skill power cost fields missing");

            int resolvedSkillLevel = Math.Clamp(skillLevel, 1, byte.MaxValue);
            int requiredLevel = Math.Max(0, spell.RequiredLevel);
            int requiredLevelIncF32 = Math.Max(0, spell.RequiredLevelIncF32);
            int skillLevelOffsetF32 = (resolvedSkillLevel - 1) << 8;
            int skillPowerLevelF32 = unchecked((requiredLevel << 8) + FixedMultiply(skillLevelOffsetF32, requiredLevelIncF32)) & ~0xFF;
            int baseSkillPowerCostF32 = GCDatabase.Instance.GetRequiredKnobFixed32("BaseSkillPowerCost");
            int skillPowerCostPerLevelF32 = GCDatabase.Instance.GetRequiredKnobFixed32("SkillPowerCostPerLevel");
            int scaledSkillPowerCostF32 = FixedMultiply(skillPowerLevelF32, skillPowerCostPerLevelF32);
            long manaBaseF32 = (long)unitManaCost * 0x100 + baseSkillPowerCostF32 + scaledSkillPowerCostF32;
            long manaCostWire = (manaBaseF32 * spell.ManaCostModF32) >> 8;
            return (uint)Math.Clamp(manaCostWire, 0L, (long)uint.MaxValue);
        }

        private static int FixedMultiply(int leftF32, int rightF32)
        {
            return (int)(((long)leftF32 * rightF32) >> 8);
        }

        private static int ParseProfessionTypeMask(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return 0;
            if (int.TryParse(value, out int numeric))
                return numeric;
            int mask = 0;
            if (value.IndexOf("MAGE", StringComparison.OrdinalIgnoreCase) >= 0) mask |= 1;
            if (value.IndexOf("RANGER", StringComparison.OrdinalIgnoreCase) >= 0) mask |= 2;
            if (value.IndexOf("FIGHTER", StringComparison.OrdinalIgnoreCase) >= 0) mask |= 4;
            if (value.IndexOf("SUMMONER", StringComparison.OrdinalIgnoreCase) >= 0) mask |= 8;
            return mask;
        }

        private static int GetFixed32Any(GCNode node, string primary, string secondary, int fallbackF32)
        {
            if (node == null)
                return fallbackF32;
            if (!string.IsNullOrWhiteSpace(primary) && node.HasProperty(primary))
                return node.GetFixed32(primary, fallbackF32);
            if (!string.IsNullOrWhiteSpace(secondary) && node.HasProperty(secondary))
                return node.GetFixed32(secondary, fallbackF32);
            return fallbackF32;
        }

        private static string ResolveSkillId(string path, GCNode skill)
        {
            string normalized = NormalizePath(path);
            if (!string.IsNullOrWhiteSpace(normalized))
                return normalized;
            string name = skill?.Name;
            if (!string.IsNullOrWhiteSpace(name))
                return "skills.generic." + name;
            return normalized;
        }

        private static string NormalizeSkillCandidate(string nameOrPath)
        {
            string normalized = NormalizePath(nameOrPath);
            if (string.IsNullOrWhiteSpace(normalized))
                return normalized;
            if (normalized.IndexOf('.') < 0)
                return "skills.generic." + normalized;
            return normalized;
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "";
            string normalized = path.Trim().Trim('"').Replace('\\', '.').Replace('/', '.');
            if (normalized.EndsWith(".gc", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(0, normalized.Length - 3);
            while (normalized.Contains("..", StringComparison.Ordinal))
                normalized = normalized.Replace("..", ".");
            return normalized.Trim('.');
        }

        private static string LastSegment(string path)
        {
            string normalized = NormalizePath(path);
            int dot = normalized.LastIndexOf('.');
            return dot >= 0 && dot + 1 < normalized.Length ? normalized.Substring(dot + 1) : normalized;
        }

        private static string CompactKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";
            char[] buffer = new char[value.Length];
            int count = 0;
            foreach (char ch in value)
                if (char.IsLetterOrDigit(ch))
                    buffer[count++] = ch;
            return new string(buffer, 0, count);
        }

        private static void Register(string shortName, SpellData data)
        {
            if (data == null)
                return;
            if (string.IsNullOrWhiteSpace(data.ShortName))
                data.ShortName = shortName;
            string identity = data.SkillId ?? data.ShortName;
            if (!string.IsNullOrWhiteSpace(identity))
            {
                if (_spellOrderIndex.TryGetValue(identity, out int existingIndex))
                    _spellOrder[existingIndex] = data;
                else
                {
                    _spellOrderIndex[identity] = _spellOrder.Count;
                    _spellOrder.Add(data);
                }
            }
            bool playerSkill = (data.SkillId ?? "").StartsWith("skills.generic.", StringComparison.OrdinalIgnoreCase);
            if (playerSkill)
                RegisterKey(data.ShortName, data);
            RegisterKey(data.SkillId, data);
            RegisterKey(NormalizePath(data.SkillId), data);
            RegisterKey((data.SkillId ?? "").Replace('.', '/'), data);
            if (playerSkill)
                RegisterKey(CompactKey(data.DisplayName), data);
        }

        private static void RegisterKey(string key, SpellData data)
        {
            if (string.IsNullOrWhiteSpace(key) || data == null)
                return;
            _spells[key] = data;
        }

        private static void LogLoadSummary()
        {
            int player = 0, sharedCreature = 0, offensive = 0, weapon = 0, utility = 0, passive = 0, summon = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int spellIndex = 0; spellIndex < _spellOrder.Count; spellIndex++)
            {
                SpellData spell = _spellOrder[spellIndex];
                if (!seen.Add(spell.SkillId ?? spell.ShortName)) continue;
                if ((spell.SkillId ?? "").StartsWith("skills.generic.", StringComparison.OrdinalIgnoreCase)) player++;
                else if ((spell.SkillId ?? "").StartsWith("skills.creature.", StringComparison.OrdinalIgnoreCase)) sharedCreature++;
                switch (spell.SkillCategory)
                {
                    case SkillCategory.Offensive: offensive++; break;
                    case SkillCategory.WeaponSkill: weapon++; break;
                    case SkillCategory.Passive: passive++; break;
                    case SkillCategory.Summon: summon++; break;
                    default: utility++; break;
                }
            }
            int embeddedMonster = _directEmbeddedMonsterSkillPaths;
            if (player != EXPECTED_PLAYER_SKILLS
                || sharedCreature != EXPECTED_SHARED_CREATURE_SKILLS
                || embeddedMonster != EXPECTED_EMBEDDED_MONSTER_SKILLS
                || _directManipulatorSkillPaths != EXPECTED_DIRECT_MANIPULATOR_SKILL_PATHS
                || _directSharedCreatureSkillPaths != EXPECTED_DIRECT_SHARED_CREATURE_SKILL_PATHS)
                throw new InvalidOperationException($"Authored skill inventory mismatch player={player}/{EXPECTED_PLAYER_SKILLS} sharedCreature={sharedCreature}/{EXPECTED_SHARED_CREATURE_SKILLS} embeddedMonster={embeddedMonster}/{EXPECTED_EMBEDDED_MONSTER_SKILLS} manipulatorPaths={_directManipulatorSkillPaths}/{EXPECTED_DIRECT_MANIPULATOR_SKILL_PATHS} manipulatorShared={_directSharedCreatureSkillPaths}/{EXPECTED_DIRECT_SHARED_CREATURE_SKILL_PATHS}");
            Debug.LogError($"[SPELLDB] source=direct-gc registered={seen.Count} player={player} monster={sharedCreature + embeddedMonster} sharedCreature={sharedCreature} embeddedMonster={embeddedMonster} manipulatorPaths={_directManipulatorSkillPaths} manipulatorShared={_directSharedCreatureSkillPaths} offensive={offensive} weapon={weapon} utility={utility} summon={summon} passive={passive}");
        }

        public static SpellData GetSpell(string name)
        {
            EnsureAuthoredLoaded();
            if (string.IsNullOrWhiteSpace(name)) return null;
            if (_spells.TryGetValue(name, out SpellData data))
                return data;

            string normalized = NormalizePath(name);
            if (_spells.TryGetValue(normalized, out data))
                return data;

            string compact = CompactKey(name);
            if (_spells.TryGetValue(compact, out data))
                return data;

            if (TryBuildAuthoredSpell(name, out data))
            {
                Register(data.ShortName, data);
                return data;
            }

            return null;
        }

        public static IEnumerable<SpellData> GetAllSpells()
        {
            EnsureAuthoredLoaded();
            for (int spellIndex = 0; spellIndex < _spellOrder.Count; spellIndex++)
                yield return _spellOrder[spellIndex];
        }

        public static IEnumerable<SpellData> GetOffensiveSpells()
        {
            foreach (SpellData spell in GetAllSpells())
            {
                if ((spell.SkillCategory == SkillCategory.Offensive ||
                     spell.SkillCategory == SkillCategory.WeaponSkill) && spell.HasAnyDamage)
                    yield return spell;
            }
        }

        public static bool IsDamageSkill(string name)
        {
            SpellData spell = GetSpell(name);
            if (spell == null) return false;
            return (spell.SkillCategory == SkillCategory.Offensive ||
                    spell.SkillCategory == SkillCategory.WeaponSkill) && spell.HasAnyDamage;
        }

        public static bool TryValidatePlayerCastModifierRuntime(SpellData spell, out string reason)
        {
            reason = null;
            if (spell == null)
            {
                reason = "skill-unresolved";
                return false;
            }
            if (string.IsNullOrWhiteSpace(spell.CastModifierId))
                return true;
            if (spell.FriendlySummon?.IsHitProc == true)
                return true;

            SpellCastModifierData cast = spell.CastModifier;
            if (cast == null || !cast.Resolved)
            {
                reason = "unsupported-player-cast-modifier-unresolved";
                return false;
            }
            if (spell.AddModifierWhileClosing)
            {
                reason = "unsupported-player-cast-modifier-while-closing";
                return false;
            }
            if (!string.Equals(cast.Family, "Modifier", StringComparison.Ordinal))
            {
                reason = $"unsupported-player-cast-modifier-family:{cast.Family ?? "unknown"}";
                return false;
            }
            if (cast.DurationTicks < 0)
            {
                reason = $"unsupported-player-cast-modifier-duration:{cast.DurationTicks}";
                return false;
            }
            if (cast.ModifierChildren == null || cast.ModifierChildren.Count != 0)
            {
                reason = $"unsupported-player-cast-modifier-child-count:{cast.ModifierChildren?.Count ?? 0}";
                return false;
            }
            if (!string.Equals(cast.DescriptionFamily, "ModifierDesc", StringComparison.Ordinal))
            {
                reason = $"unsupported-player-cast-modifier-description-family:{cast.DescriptionFamily ?? "unknown"}";
                return false;
            }
            if (cast.DescriptionChildren == null || cast.DescriptionChildren.Count != 0)
            {
                reason = $"unsupported-player-cast-modifier-description-child-count:{cast.DescriptionChildren?.Count ?? 0}";
                return false;
            }
            for (int propertyIndex = 0; propertyIndex < cast.ModifierProperties.Count; propertyIndex++)
            {
                string property = cast.ModifierProperties[propertyIndex];
                if (!string.Equals(property, "Duration", StringComparison.OrdinalIgnoreCase))
                {
                    reason = $"unsupported-player-cast-modifier-property:{property ?? "unknown"}";
                    return false;
                }
            }
            for (int propertyIndex = 0; propertyIndex < cast.DescriptionProperties.Count; propertyIndex++)
            {
                string property = cast.DescriptionProperties[propertyIndex];
                if (!string.Equals(property, "Name", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(property, "InitSound", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(property, "RemoveOnDeath", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(property, "Visual", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(property, "StackRule", StringComparison.OrdinalIgnoreCase))
                {
                    reason = $"unsupported-player-cast-modifier-description-property:{property ?? "unknown"}";
                    return false;
                }
            }
            if (string.IsNullOrWhiteSpace(cast.VisualId) && string.IsNullOrWhiteSpace(cast.InitSoundId))
            {
                reason = "unsupported-player-cast-modifier-empty-lifecycle";
                return false;
            }
            return true;
        }

        private static bool TryValidatePlayerSelfAoEEffectModifierRuntime(
            SpellData spell,
            SpellEffectNodeData modifierNode,
            SpellEffectNodeData aoeNode,
            SpellEffectNodeData damageNode,
            out string reason)
        {
            reason = null;
            if (spell == null
                || modifierNode == null
                || aoeNode == null
                || damageNode == null
                || !string.Equals(modifierNode.Family, "SpellModEffect", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(modifierNode.ModifierId)
                || spell.ModifierEffects == null
                || spell.ModifierEffects.Count != 1)
            {
                reason = "unsupported-self-aoe-effect-modifier-unresolved";
                return false;
            }
            if (modifierNode.ChanceF32 != 0x6400
                || modifierNode.Order <= aoeNode.Order
                || modifierNode.Order >= damageNode.Order
                || modifierNode.Depth <= aoeNode.Depth)
            {
                reason = $"unsupported-self-aoe-effect-modifier-order:aoe={aoeNode.Order},modifier={modifierNode.Order},damage={damageNode.Order},depth={modifierNode.Depth}/{aoeNode.Depth},chance={modifierNode.ChanceF32}";
                return false;
            }
            SpellModifierData modifier = spell.ModifierEffects[0];
            if (modifier == null
                || !string.Equals(modifier.ModifierId, modifierNode.ModifierId, StringComparison.OrdinalIgnoreCase)
                || modifier.PackageEntryId == 0
                || string.IsNullOrWhiteSpace(modifier.CanonicalPath)
                || modifier.EffectOrder != modifierNode.Order
                || modifier.ModifierPackageEntryId == 0
                || string.IsNullOrWhiteSpace(modifier.ModifierCanonicalPath))
            {
                reason = "unsupported-self-aoe-effect-modifier-provenance";
                return false;
            }
            if (!string.Equals(modifier.ModifierFamily, "EffectMod", StringComparison.Ordinal)
                || modifier.DurationF32 <= 0
                || modifier.DurationIncF32 != 0
                || modifier.FrequencyF32 <= 0
                || !string.Equals(modifier.StackRule, "UNIQUEBYTYPE", StringComparison.OrdinalIgnoreCase)
                || !modifier.RemoveOnDeath
                || modifier.TerminateWhenHitChance != 0
                || modifier.Attributes == null
                || modifier.Attributes.Count != 0)
            {
                reason = "unsupported-self-aoe-effect-modifier-lifecycle";
                return false;
            }
            if (!string.IsNullOrWhiteSpace(modifier.ModifierEffectId)
                || modifier.ModifierEffectPackageEntryId != 0
                || !string.IsNullOrWhiteSpace(modifier.ModifierEffectCanonicalPath)
                || modifier.ModifierEffectRoot != null)
            {
                reason = "unsupported-self-aoe-effect-modifier-authoritative-effect";
                return false;
            }
            return true;
        }

        public static bool TryValidatePlayerSelfRuntime(SpellData spell, out string reason)
        {
            reason = null;
            if (spell == null)
            {
                reason = "skill-unresolved";
                return false;
            }
            if (!string.Equals(spell.TargetType, "SELF", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(spell.TargetType, "0", StringComparison.OrdinalIgnoreCase))
            {
                reason = $"self-target-type:{spell.TargetType ?? "unknown"}";
                return false;
            }
            if (spell.EffectRoot == null || spell.OrderedEffects == null || spell.OrderedEffects.Count == 0)
            {
                reason = "ordered-effect-graph-missing";
                return false;
            }
            if (!TryValidatePlayerCastModifierRuntime(spell, out reason))
                return false;
            if (spell.FriendlySummon?.IsHitProc == false)
                return true;
            for (int nodeIndex = 0; nodeIndex < spell.OrderedEffects.Count; nodeIndex++)
            {
                string family = spell.OrderedEffects[nodeIndex]?.Family ?? "Unknown";
                if (family == "Container"
                    || family == "SpellEffect"
                    || family == "SpellSnapToGroundEffect"
                    || family == "SpellEffectEffect"
                    || family == "SpellSoundEffect")
                    continue;
                if (spell.IsAoE && (family == "SpellAOEEffect" || family == "SpellDamageEffect" || family == "SpellModEffect" || family == "SpellKnockDownEffect"))
                    continue;
                if (!spell.IsAoE && family == "SpellModEffect")
                    continue;
                reason = $"unsupported-self-effect-family:{family}";
                return false;
            }
            if (spell.IsAoE)
            {
                int aoeCount = spell.OrderedEffects.Count(node => string.Equals(node?.Family, "SpellAOEEffect", StringComparison.Ordinal));
                int damageCount = spell.OrderedEffects.Count(node => string.Equals(node?.Family, "SpellDamageEffect", StringComparison.Ordinal));
                int modifierNodeCount = spell.OrderedEffects.Count(node => string.Equals(node?.Family, "SpellModEffect", StringComparison.Ordinal));
                int knockDownCount = spell.OrderedEffects.Count(node => string.Equals(node?.Family, "SpellKnockDownEffect", StringComparison.Ordinal));
                int modifierCount = spell.ModifierEffects?.Count ?? 0;
                if (aoeCount != 1 || damageCount != 1 || modifierNodeCount != 1 || !spell.HasAoEEffect || !spell.HasDirectDamageEffect || modifierCount != 1)
                {
                    reason = $"unsupported-self-aoe-shape:aoe={aoeCount},damage={damageCount},modifierNodes={modifierNodeCount},modifiers={modifierCount}";
                    return false;
                }
                if (spell.HasSpellKnockDownEffect != (knockDownCount == 1))
                {
                    reason = $"unsupported-self-aoe-knockdown-shape:authored={spell.HasSpellKnockDownEffect},nodes={knockDownCount}";
                    return false;
                }
                SpellEffectNodeData aoeNode = spell.OrderedEffects.FirstOrDefault(node => string.Equals(node?.Family, "SpellAOEEffect", StringComparison.Ordinal));
                SpellEffectNodeData modifierNode = spell.OrderedEffects.FirstOrDefault(node => string.Equals(node?.Family, "SpellModEffect", StringComparison.Ordinal));
                SpellEffectNodeData damageNode = spell.OrderedEffects.FirstOrDefault(node => string.Equals(node?.Family, "SpellDamageEffect", StringComparison.Ordinal));
                if (!TryValidatePlayerSelfAoEEffectModifierRuntime(spell, modifierNode, aoeNode, damageNode, out reason))
                    return false;
                return true;
            }
            int modEffectCount = spell.OrderedEffects.Count(node => string.Equals(node?.Family, "SpellModEffect", StringComparison.Ordinal));
            if (modEffectCount != 1 || spell.ModifierEffects == null || spell.ModifierEffects.Count != 1)
            {
                reason = $"unsupported-self-modifier-count:effects={modEffectCount},modifiers={spell.ModifierEffects?.Count ?? 0}";
                return false;
            }
            SpellModifierData modifier = spell.ModifierEffects[0];
            if (!string.Equals(modifier?.ModifierFamily, "AttributeModifier", StringComparison.Ordinal))
            {
                reason = $"unsupported-self-modifier-family:{modifier?.ModifierFamily ?? "unknown"}";
                return false;
            }
            if (modifier.EffectOrder < 0)
            {
                reason = "unsupported-self-referenced-modifier-order";
                return false;
            }
            if (modifier.Attributes == null || modifier.Attributes.Count == 0)
            {
                reason = $"unsupported-self-attribute-count:{modifier.Attributes?.Count ?? 0}";
                return false;
            }
            for (int attributeIndex = 0; attributeIndex < modifier.Attributes.Count; attributeIndex++)
            {
                SpellAttributeModifierData attribute = modifier.Attributes[attributeIndex];
                if (attribute == null || string.IsNullOrWhiteSpace(attribute.Attribute))
                {
                    reason = "unsupported-self-empty-attribute";
                    return false;
                }
                if (!attribute.OverrideTable)
                {
                    reason = $"unsupported-self-non-override-table:{attribute.Attribute}";
                    return false;
                }
                if (!IsPlayerAuthoredAttributeSupported(attribute.Attribute))
                {
                    reason = $"unsupported-self-attribute:{attribute.Attribute}";
                    return false;
                }
            }
            return true;
        }

        internal static bool IsPlayerAuthoredAttributeSupported(string attribute)
        {
            switch ((attribute ?? string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty).Trim().ToUpperInvariant())
            {
                case "SPEEDMOD":
                case "ATTACKSPEEDMOD":
                case "HITPOINTREGENBONUS":
                case "MANAPOINTREGENBONUS":
                case "STUNRESIST":
                case "DIVINEDAMAGERESIST":
                case "FIREDAMAGERESIST":
                case "ICEDAMAGERESIST":
                case "POISONDAMAGERESIST":
                case "SHADOWDAMAGERESIST":
                case "MINSPEEDMOD":
                case "RANGEATTACKSPEEDMOD":
                case "MELEE1HATTACKSPEEDMOD":
                case "MELEE2HATTACKSPEEDMOD":
                case "MELEECRITICALCHANCEMOD":
                    return true;
                default:
                    return false;
            }
        }

        public static bool TryValidatePlayerPveTargetRuntime(SpellData spell, out string reason)
        {
            reason = null;
            if (spell == null)
            {
                reason = "skill-unresolved";
                return false;
            }
            if (!string.Equals(spell.TargetType, "ENEMY", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(spell.TargetType, "2", StringComparison.OrdinalIgnoreCase))
            {
                reason = $"player-pve-target-type:{spell.TargetType ?? "unknown"}";
                return false;
            }
            if (spell.EffectRoot == null || spell.OrderedEffects == null || spell.OrderedEffects.Count == 0)
            {
                reason = "ordered-effect-graph-missing";
                return false;
            }
            if (!TryValidatePlayerCastModifierRuntime(spell, out reason))
                return false;
            if (spell.FriendlySummon?.IsHitProc == true)
                return true;
            if (spell.HasTeleportEffect || spell.HasFear || spell.HasSlow || spell.SkillCategory == SkillCategory.Summon)
            {
                reason = "unsupported-player-pve-scalar-effect-family";
                return false;
            }
            int directDamageCount = 0;
            int chainImpactDirectDamageCount = 0;
            int weaponDamageCount = 0;
            int knockDownCount = 0;
            int referencedKnockDownCount = 0;
            int referencedEffectModifierCount = 0;
            int projectileCount = 0;
            int burstCount = 0;
            int chainCount = 0;
            int modifierCount = 0;
            int aoeCount = 0;
            int directDamageOrder = -1;
            int weaponDamageOrder = -1;
            int knockDownOrder = -1;
            int burstOrder = -1;
            for (int nodeIndex = 0; nodeIndex < spell.OrderedEffects.Count; nodeIndex++)
            {
                SpellEffectNodeData node = spell.OrderedEffects[nodeIndex];
                string family = node?.Family ?? "Unknown";
                if (HasUnmaterializedPlayerReferencedEffect(node))
                {
                    if (!string.Equals(family, "SpellWeaponDamageEffect", StringComparison.Ordinal)
                        || !TryValidatePlayerWeaponReferencedEffectRuntime(
                            spell,
                            node,
                            out int referencedNodeKnockDownCount,
                            out int referencedNodeEffectModifierCount,
                            out reason))
                        return false;
                    referencedKnockDownCount += referencedNodeKnockDownCount;
                    referencedEffectModifierCount += referencedNodeEffectModifierCount;
                }
                switch (family)
                {
                    case "Container":
                    case "SpellEffect":
                    case "SpellSnapToGroundEffect":
                    case "SpellEffectEffect":
                    case "SpellSoundEffect":
                        break;
                    case "SpellDamageEffect":
                        directDamageCount++;
                        directDamageOrder = node.Order;
                        break;
                    case "SpellWeaponDamageEffect":
                        weaponDamageCount++;
                        weaponDamageOrder = node.Order;
                        break;
                    case "SpellKnockDownEffect":
                        knockDownCount++;
                        knockDownOrder = node.Order;
                        break;
                    case "SpellProjectileEffect":
                        projectileCount++;
                        break;
                    case "SpellBurstEffect":
                        burstCount++;
                        burstOrder = node.Order;
                        break;
                    case "SpellChainEffect":
                        chainCount++;
                        if (!TryValidatePlayerChainImpactRuntime(node, out int chainNodeDirectDamageCount, out reason))
                            return false;
                        chainImpactDirectDamageCount += chainNodeDirectDamageCount;
                        break;
                    case "SpellModEffect":
                        modifierCount++;
                        break;
                    case "SpellAOEEffect":
                        aoeCount++;
                        break;
                    default:
                        reason = $"unsupported-player-pve-ordered-family:{family}";
                        return false;
                }
            }
            int totalDirectDamageCount = directDamageCount + chainImpactDirectDamageCount;
            int totalKnockDownCount = knockDownCount + referencedKnockDownCount;
            if (totalDirectDamageCount > 1 || weaponDamageCount > 1 || totalKnockDownCount > 1 || projectileCount > 1)
            {
                reason = $"unsupported-player-pve-effect-count:direct={totalDirectDamageCount},weapon={weaponDamageCount},knockdown={totalKnockDownCount},projectile={projectileCount}";
                return false;
            }
            if (referencedKnockDownCount != 0
                && (referencedKnockDownCount != 1 || knockDownCount != 0 || directDamageCount != 0 || weaponDamageCount != 1))
            {
                reason = "unsupported-player-pve-referenced-knockdown-shape";
                return false;
            }
            if (referencedEffectModifierCount != 0
                && (referencedEffectModifierCount != 1
                    || referencedKnockDownCount != 0
                    || knockDownCount != 0
                    || directDamageCount != 0
                    || weaponDamageCount != 1))
            {
                reason = "unsupported-player-pve-referenced-effect-modifier-shape";
                return false;
            }
            if (aoeCount != 0 || spell.IsAoE || spell.HasAoEEffect)
            {
                reason = "unsupported-player-pve-target-aoe";
                return false;
            }
            if (spell.IsChainSpell != (chainCount == 1))
            {
                reason = "unsupported-player-pve-referenced-chain-order";
                return false;
            }
            if (chainCount != 0
                && (chainCount != 1
                    || chainImpactDirectDamageCount != 1
                    || directDamageCount != 0
                    || weaponDamageCount != 0
                    || knockDownCount != 0
                    || referencedKnockDownCount != 0
                    || projectileCount != 0
                    || burstCount != 0
                    || aoeCount != 0))
            {
                reason = "unsupported-player-pve-chain-impact-shape";
                return false;
            }
            if (chainCount == 1
                && (spell.NumChains < 2
                    || spell.NumChainsIncrementF32 != 0
                    || spell.ChainRange <= 0
                    || spell.ChainDelayTicks <= 0
                    || spell.ChainLifespanTicks <= spell.ChainDelayTicks
                    || spell.NumForks != 1))
            {
                reason = $"unsupported-player-pve-chain-lifecycle:chains={spell.NumChains},increment={spell.NumChainsIncrementF32},range={spell.ChainRange},delay={spell.ChainDelayTicks},lifespan={spell.ChainLifespanTicks},forks={spell.NumForks}";
                return false;
            }
            if (modifierCount != 0)
            {
                reason = "unsupported-player-pve-target-modifier-order";
                return false;
            }
            if (referencedEffectModifierCount == 0
                && (spell.ModifierEffects?.Count > 0 || spell.HasDeferredProjectileModifierDamage))
            {
                reason = "unsupported-player-pve-target-modifier-order";
                return false;
            }
            if (referencedEffectModifierCount == 1
                && (spell.ModifierEffects == null
                    || spell.ModifierEffects.Count != 1
                    || !spell.HasDeferredProjectileModifierDamage))
            {
                reason = "unsupported-player-pve-referenced-effect-modifier-projection";
                return false;
            }
            if (burstCount != 0 || spell.HasBurstEffect)
            {
                if (burstCount != 1 || spell.BurstCountMinF32 != 0x100 || spell.BurstCountMaxF32 != 0x100)
                {
                    reason = $"unsupported-player-pve-burst-count:nodes={burstCount},min={spell.BurstCountMinF32},max={spell.BurstCountMaxF32}";
                    return false;
                }
                int firstDamageOrder = directDamageOrder >= 0 && weaponDamageOrder >= 0
                    ? Math.Min(directDamageOrder, weaponDamageOrder)
                    : Math.Max(directDamageOrder, weaponDamageOrder);
                if (firstDamageOrder < 0 || burstOrder > firstDamageOrder)
                {
                    reason = "unsupported-player-pve-burst-order";
                    return false;
                }
            }
            if (spell.HasDirectDamageEffect != (totalDirectDamageCount == 1))
            {
                reason = "unsupported-player-pve-referenced-damage-order";
                return false;
            }
            if (spell.HasImmediateWeaponDamageEffect != (weaponDamageCount == 1))
            {
                reason = "unsupported-player-pve-referenced-weapon-damage-order";
                return false;
            }
            if (spell.HasSpellKnockDownEffect != (totalKnockDownCount == 1))
            {
                reason = "unsupported-player-pve-referenced-knockdown-order";
                return false;
            }
            if (totalDirectDamageCount == 0 && weaponDamageCount == 0)
            {
                reason = "unsupported-player-pve-empty-effect";
                return false;
            }
            if (directDamageCount == 1 && weaponDamageCount == 1 && weaponDamageOrder > directDamageOrder)
            {
                reason = "unsupported-player-pve-damage-order";
                return false;
            }
            if (knockDownCount == 1 && directDamageCount == 1 && weaponDamageCount == 1)
            {
                reason = "unsupported-player-pve-knockdown-duplicate-execution";
                return false;
            }
            if (knockDownCount == 1 && directDamageCount == 1 && knockDownOrder > directDamageOrder)
            {
                reason = "unsupported-player-pve-knockdown-order";
                return false;
            }
            if (knockDownCount == 1 && weaponDamageCount == 1 && weaponDamageOrder > knockDownOrder)
            {
                reason = "unsupported-player-pve-weapon-knockdown-order";
                return false;
            }
            return true;
        }

        public static bool TryValidatePlayerUsePositionRuntime(SpellData spell, out string reason)
        {
            reason = null;
            if (spell == null)
            {
                reason = "skill-unresolved";
                return false;
            }
            if (!string.Equals(NormalizePlayerTargetType(spell.TargetType), "POSITION", StringComparison.Ordinal))
            {
                reason = $"player-position-target-type:{spell.TargetType ?? "unknown"}";
                return false;
            }
            if (spell.EffectRoot == null || spell.OrderedEffects == null || spell.OrderedEffects.Count == 0)
            {
                reason = "ordered-effect-graph-missing";
                return false;
            }
            if (!TryValidatePlayerCastModifierRuntime(spell, out reason))
                return false;
            if (spell.SkillCategory == SkillCategory.Summon || spell.IsAoE || spell.HasAoEEffect || spell.IsChainSpell || spell.HasFear || spell.HasSlow)
            {
                reason = "unsupported-player-position-scalar-effect-family";
                return false;
            }

            int directDamageCount = 0;
            int weaponDamageCount = 0;
            int knockDownCount = 0;
            int projectileCount = 0;
            int burstCount = 0;
            int teleportCount = 0;
            int directDamageOrder = -1;
            int weaponDamageOrder = -1;
            int knockDownOrder = -1;
            int projectileOrder = -1;
            int burstOrder = -1;
            int teleportOrder = -1;
            for (int nodeIndex = 0; nodeIndex < spell.OrderedEffects.Count; nodeIndex++)
            {
                SpellEffectNodeData node = spell.OrderedEffects[nodeIndex];
                string family = node?.Family ?? "Unknown";
                if (HasUnmaterializedPlayerReferencedEffect(node))
                {
                    reason = $"unsupported-player-position-referenced-effect-graph:{family}:{node.ReferencedEffectId}";
                    return false;
                }
                switch (family)
                {
                    case "Container":
                    case "SpellEffect":
                    case "SpellSnapToGroundEffect":
                    case "SpellEffectEffect":
                    case "SpellSoundEffect":
                        break;
                    case "SpellDamageEffect":
                        directDamageCount++;
                        directDamageOrder = node.Order;
                        break;
                    case "SpellWeaponDamageEffect":
                        weaponDamageCount++;
                        weaponDamageOrder = node.Order;
                        break;
                    case "SpellKnockDownEffect":
                        knockDownCount++;
                        knockDownOrder = node.Order;
                        break;
                    case "SpellProjectileEffect":
                        projectileCount++;
                        projectileOrder = node.Order;
                        break;
                    case "SpellBurstEffect":
                        burstCount++;
                        burstOrder = node.Order;
                        break;
                    case "SpellTeleportEffect":
                        teleportCount++;
                        teleportOrder = node.Order;
                        break;
                    default:
                        reason = $"unsupported-player-position-ordered-family:{family}";
                        return false;
                }
            }

            if (directDamageCount > 1 || weaponDamageCount > 1 || knockDownCount > 1 || projectileCount > 1 || burstCount > 1 || teleportCount > 1)
            {
                reason = $"unsupported-player-position-effect-count:direct={directDamageCount},weapon={weaponDamageCount},knockdown={knockDownCount},projectile={projectileCount},burst={burstCount},teleport={teleportCount}";
                return false;
            }
            if (spell.ModifierEffects?.Count > 0 || spell.HasDeferredProjectileModifierDamage)
            {
                reason = "unsupported-player-position-modifier-order";
                return false;
            }
            if (!string.IsNullOrWhiteSpace(spell.ProjectileEffectId)
                || !string.IsNullOrWhiteSpace(spell.ProjectileModifierId)
                || !string.IsNullOrWhiteSpace(spell.ProjectileModifierEffectId))
            {
                reason = "unsupported-player-position-referenced-effect-graph";
                return false;
            }

            if (teleportCount == 1 || spell.HasTeleportEffect)
            {
                if (teleportCount != 1
                    || !spell.HasTeleportEffect
                    || directDamageCount != 0
                    || weaponDamageCount != 0
                    || knockDownCount != 0
                    || projectileCount != 0
                    || burstCount != 0
                    || spell.HasAnyDamage)
                {
                    reason = "unsupported-player-position-teleport-shape";
                    return false;
                }
                if (teleportOrder <= 0)
                {
                    reason = "unsupported-player-position-teleport-order";
                    return false;
                }
                return true;
            }

            if (spell.HasDirectDamageEffect != (directDamageCount == 1))
            {
                reason = "unsupported-player-position-referenced-damage-order";
                return false;
            }
            if (spell.HasImmediateWeaponDamageEffect != (weaponDamageCount == 1))
            {
                reason = "unsupported-player-position-referenced-weapon-damage-order";
                return false;
            }
            if (spell.HasSpellKnockDownEffect != (knockDownCount == 1))
            {
                reason = "unsupported-player-position-referenced-knockdown-order";
                return false;
            }
            if (spell.HasBurstEffect != (burstCount == 1))
            {
                reason = "unsupported-player-position-referenced-burst-order";
                return false;
            }
            if (directDamageCount + weaponDamageCount != 1)
            {
                reason = "unsupported-player-position-damage-shape";
                return false;
            }

            int damageOrder = directDamageCount == 1 ? directDamageOrder : weaponDamageOrder;
            if (projectileCount == 1 && projectileOrder > damageOrder)
            {
                reason = "unsupported-player-position-projectile-order";
                return false;
            }
            if (burstCount == 1)
            {
                if (spell.BurstCountMinF32 != 0x100 || spell.BurstCountMaxF32 != 0x100 || burstOrder > damageOrder)
                {
                    reason = $"unsupported-player-position-burst-shape:min={spell.BurstCountMinF32},max={spell.BurstCountMaxF32},order={burstOrder}";
                    return false;
                }
            }
            if (knockDownCount == 1 && knockDownOrder > damageOrder)
            {
                reason = "unsupported-player-position-knockdown-order";
                return false;
            }
            return true;
        }

        public static bool TryValidatePlayerResourceCommitRuntime(SpellData spell, out string reason)
        {
            reason = null;
            if (spell == null)
            {
                reason = "skill-unresolved";
                return false;
            }
            if (IsBlingGnomeConversionSkill(spell))
                return TryValidatePlayerBlingGnomeRuntime(spell, out reason);
            if (spell.SkillCategory == SkillCategory.Passive)
            {
                reason = "unsupported-player-active-passive-category";
                return false;
            }

            string targetType = NormalizePlayerTargetType(spell.TargetType);
            switch (targetType)
            {
                case "SELF":
                case "0":
                case "ENEMY":
                case "2":
                    return true;
                case "POSITION":
                    return TryValidatePlayerUsePositionRuntime(spell, out reason);
                case "FRIEND":
                case "1":
                    reason = "unsupported-player-friend-runtime";
                    return false;
                case "FRIENDSELF":
                case "11":
                    reason = "unsupported-player-friendself-runtime";
                    return false;
                case "FRIENDCORPSE":
                case "6":
                    reason = "unsupported-player-friend-corpse-runtime";
                    return false;
                case "ENEMYCORPSE":
                    reason = "unsupported-player-enemy-corpse-runtime";
                    return false;
                case "HENCHMAN":
                    reason = "unsupported-player-henchman-runtime";
                    return false;
                default:
                    reason = $"unsupported-player-target-type:{spell.TargetType ?? "unknown"}";
                    return false;
            }
        }

        public static bool IsBlingGnomeConversionSkill(SpellData spell)
        {
            return string.Equals(NormalizePath(spell?.SkillId), "skills.generic.SummonBlingGnome", StringComparison.OrdinalIgnoreCase);
        }

        public static bool TryValidatePlayerBlingGnomeRuntime(SpellData spell, out string reason)
        {
            reason = null;
            if (!IsBlingGnomeConversionSkill(spell) || NormalizePlayerTargetType(spell.TargetType) != "HENCHMAN")
            {
                reason = "unsupported-player-bling-skill";
                return false;
            }
            if (spell.OrderedEffects == null || spell.OrderedEffects.Count != 3
                || spell.OrderedEffects[0].Family != "SpellEffect" || spell.OrderedEffects[0].Depth != 0
                || spell.OrderedEffects[1].Family != "SpellModEffect" || spell.OrderedEffects[1].Depth != 1
                || spell.OrderedEffects[2].Family != "SpellConvertItemsToGoldEffect" || spell.OrderedEffects[2].Depth != 1)
            {
                reason = "unsupported-player-bling-effect-order";
                return false;
            }
            return true;
        }

        private static string NormalizePlayerTargetType(string targetType)
        {
            return (targetType ?? string.Empty)
                .Replace("_", string.Empty)
                .Replace("-", string.Empty)
                .Replace(" ", string.Empty)
                .Trim()
                .ToUpperInvariant();
        }

        private static bool HasUnmaterializedPlayerReferencedEffect(SpellEffectNodeData node)
        {
            if (node == null || string.IsNullOrWhiteSpace(node.ReferencedEffectId))
                return false;
            return string.Equals(node.Family, "SpellWeaponDamageEffect", StringComparison.Ordinal)
                || string.Equals(node.Family, "SpellDamageEffect", StringComparison.Ordinal);
        }

        private static bool TryValidatePlayerWeaponReferencedEffectRuntime(
            SpellData spell,
            SpellEffectNodeData weaponNode,
            out int knockDownCount,
            out int effectModifierCount,
            out string reason)
        {
            knockDownCount = 0;
            effectModifierCount = 0;
            reason = null;
            if (weaponNode == null || string.IsNullOrWhiteSpace(weaponNode.ReferencedEffectId))
                return true;
            if (weaponNode.ReferencedEffect == null)
            {
                reason = $"unsupported-player-pve-referenced-effect-unresolved:{weaponNode.ReferencedEffectId}";
                return false;
            }
            var pending = new Stack<SpellEffectNodeData>();
            pending.Push(weaponNode.ReferencedEffect);
            while (pending.Count > 0)
            {
                SpellEffectNodeData node = pending.Pop();
                if (node == null)
                {
                    reason = "unsupported-player-pve-referenced-effect-null-node";
                    return false;
                }
                if (HasUnmaterializedPlayerReferencedEffect(node))
                {
                    reason = $"unsupported-player-pve-nested-referenced-effect:{node.Family}:{node.ReferencedEffectId}";
                    return false;
                }
                if (node.ChanceF32 != 0x6400)
                {
                    reason = $"unsupported-player-pve-referenced-effect-chance:{node.Family}:{node.ChanceF32}";
                    return false;
                }
                switch (node.Family ?? "Unknown")
                {
                    case "Container":
                    case "SpellEffect":
                    case "SpellSnapToGroundEffect":
                    case "SpellEffectEffect":
                    case "SpellSoundEffect":
                        break;
                    case "SpellKnockDownEffect":
                        knockDownCount++;
                        break;
                    case "SpellModEffect":
                        effectModifierCount++;
                        if (!TryValidatePlayerWeaponReferencedEffectModifierRuntime(spell, node, out reason))
                            return false;
                        break;
                    default:
                        reason = $"unsupported-player-pve-referenced-effect-family:{node.Family ?? "Unknown"}";
                        return false;
                }
                for (int childIndex = node.Children.Count - 1; childIndex >= 0; childIndex--)
                    pending.Push(node.Children[childIndex]);
            }
            if (knockDownCount + effectModifierCount != 1)
            {
                reason = $"unsupported-player-pve-referenced-semantic-count:knockdown={knockDownCount},modifier={effectModifierCount}";
                return false;
            }
            return true;
        }

        private static bool TryValidatePlayerWeaponReferencedEffectModifierRuntime(
            SpellData spell,
            SpellEffectNodeData modifierNode,
            out string reason)
        {
            reason = null;
            if (spell == null
                || modifierNode == null
                || string.IsNullOrWhiteSpace(modifierNode.ModifierId)
                || spell.ModifierEffects == null
                || spell.ModifierEffects.Count != 1)
            {
                reason = "unsupported-player-pve-referenced-effect-modifier-unresolved";
                return false;
            }
            SpellModifierData modifier = spell.ModifierEffects[0];
            if (modifier == null
                || !string.Equals(modifier.ModifierId, modifierNode.ModifierId, StringComparison.OrdinalIgnoreCase)
                || modifier.PackageEntryId == 0
                || string.IsNullOrWhiteSpace(modifier.CanonicalPath)
                || modifier.ModifierPackageEntryId == 0
                || string.IsNullOrWhiteSpace(modifier.ModifierCanonicalPath)
                || modifier.ModifierEffectPackageEntryId == 0
                || string.IsNullOrWhiteSpace(modifier.ModifierEffectCanonicalPath)
                || modifier.ModifierEffectRoot == null)
            {
                reason = "unsupported-player-pve-referenced-effect-modifier-provenance";
                return false;
            }
            if (!string.Equals(modifier.ModifierFamily, "EffectMod", StringComparison.Ordinal)
                || modifier.EffectOrder != -1
                || modifier.DurationF32 <= 0
                || modifier.DurationIncF32 != 0
                || modifier.FrequencyF32 <= 0
                || !string.Equals(modifier.StackRule, "UNIQUEBYTYPE", StringComparison.OrdinalIgnoreCase)
                || !modifier.RemoveOnDeath
                || modifier.TerminateWhenHitChance != 0
                || modifier.Attributes == null
                || modifier.Attributes.Count != 0)
            {
                reason = "unsupported-player-pve-referenced-effect-modifier-lifecycle";
                return false;
            }
            if (!string.Equals(spell.ProjectileModifierId, modifier.ModifierId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(spell.ProjectileModifierEffectId, modifier.ModifierEffectId, StringComparison.OrdinalIgnoreCase)
                || spell.ProjectileModifierDurationF32 != modifier.DurationF32
                || spell.ProjectileModifierFrequencyF32 != modifier.FrequencyF32
                || !string.Equals(spell.ProjectileModifierStackRule, modifier.StackRule, StringComparison.OrdinalIgnoreCase)
                || spell.ProjectileModifierDamageModF32 <= 0
                || spell.ProjectileModifierDamageVolatilityF32 <= 0
                || spell.ProjectileModifierChanceF32 != 0x6400)
            {
                reason = "unsupported-player-pve-referenced-effect-modifier-scalar-projection";
                return false;
            }
            int directDamageCount = 0;
            var pending = new Stack<SpellEffectNodeData>();
            pending.Push(modifier.ModifierEffectRoot);
            while (pending.Count > 0)
            {
                SpellEffectNodeData node = pending.Pop();
                if (node == null || HasUnmaterializedPlayerReferencedEffect(node))
                {
                    reason = "unsupported-player-pve-referenced-effect-modifier-damage-reference";
                    return false;
                }
                if (node.ChanceF32 != 0x6400)
                {
                    reason = $"unsupported-player-pve-referenced-effect-modifier-damage-chance:{node.ChanceF32}";
                    return false;
                }
                switch (node.Family ?? "Unknown")
                {
                    case "Container":
                    case "SpellEffect":
                    case "SpellSnapToGroundEffect":
                    case "SpellEffectEffect":
                    case "SpellSoundEffect":
                        break;
                    case "SpellDamageEffect":
                        directDamageCount++;
                        break;
                    default:
                        reason = $"unsupported-player-pve-referenced-effect-modifier-damage-family:{node.Family ?? "Unknown"}";
                        return false;
                }
                for (int childIndex = node.Children.Count - 1; childIndex >= 0; childIndex--)
                    pending.Push(node.Children[childIndex]);
            }
            if (directDamageCount != 1)
            {
                reason = $"unsupported-player-pve-referenced-effect-modifier-damage-count:{directDamageCount}";
                return false;
            }
            return true;
        }

        private static bool TryValidatePlayerChainImpactRuntime(
            SpellEffectNodeData chainNode,
            out int directDamageCount,
            out string reason)
        {
            directDamageCount = 0;
            reason = null;
            if (chainNode == null
                || string.IsNullOrWhiteSpace(chainNode.ChainProjectileId)
                || chainNode.ChainProjectilePackageEntryId == 0
                || string.IsNullOrWhiteSpace(chainNode.ChainProjectileCanonicalPath)
                || string.IsNullOrWhiteSpace(chainNode.ReferencedEffectId)
                || chainNode.ReferencedEffect == null)
            {
                reason = "unsupported-player-pve-chain-impact-unresolved";
                return false;
            }
            if (chainNode.ChanceF32 != 0x6400)
            {
                reason = $"unsupported-player-pve-chain-chance:{chainNode.ChanceF32}";
                return false;
            }
            var pending = new Stack<SpellEffectNodeData>();
            pending.Push(chainNode.ReferencedEffect);
            while (pending.Count > 0)
            {
                SpellEffectNodeData node = pending.Pop();
                if (node == null)
                {
                    reason = "unsupported-player-pve-chain-impact-null-node";
                    return false;
                }
                if (HasUnmaterializedPlayerReferencedEffect(node))
                {
                    reason = $"unsupported-player-pve-chain-impact-reference:{node.Family}:{node.ReferencedEffectId}";
                    return false;
                }
                switch (node.Family ?? "Unknown")
                {
                    case "Container":
                    case "SpellEffect":
                    case "SpellSnapToGroundEffect":
                    case "SpellEffectEffect":
                    case "SpellSoundEffect":
                        break;
                    case "SpellDamageEffect":
                        if (node.ChanceF32 != 0x6400)
                        {
                            reason = $"unsupported-player-pve-chain-impact-damage-chance:{node.ChanceF32}";
                            return false;
                        }
                        directDamageCount++;
                        break;
                    default:
                        reason = $"unsupported-player-pve-chain-impact-family:{node.Family ?? "Unknown"}";
                        return false;
                }
                for (int childIndex = node.Children.Count - 1; childIndex >= 0; childIndex--)
                    pending.Push(node.Children[childIndex]);
            }
            if (directDamageCount != 1)
            {
                reason = $"unsupported-player-pve-chain-impact-damage-count:{directDamageCount}";
                return false;
            }
            return true;
        }

        public static bool TryValidatePvpOrderedRuntime(SpellData spell, out string reason)
        {
            reason = null;
            if (spell?.EffectRoot == null || spell.OrderedEffects == null || spell.OrderedEffects.Count == 0)
            {
                reason = "ordered-effect-graph-missing";
                return false;
            }
            if (!TryValidatePlayerCastModifierRuntime(spell, out reason))
                return false;
            int directDamageCount = 0;
            int weaponDamageCount = 0;
            int modifierNodeCount = 0;
            int aoeCount = 0;
            int chainCount = 0;
            int burstCount = 0;
            int projectileCount = 0;
            int directDamageOrder = -1;
            int weaponDamageOrder = -1;
            int outerEffectOrder = -1;
            int lastDamageOrder = -1;
            for (int nodeIndex = 0; nodeIndex < spell.OrderedEffects.Count; nodeIndex++)
            {
                SpellEffectNodeData node = spell.OrderedEffects[nodeIndex];
                string family = node?.Family ?? "Unknown";
                if (HasUnmaterializedPlayerReferencedEffect(node))
                {
                    reason = $"unsupported-pvp-referenced-effect-graph:{family}:{node.ReferencedEffectId}";
                    return false;
                }
                switch (family)
                {
                    case "Container":
                    case "SpellEffect":
                    case "SpellSnapToGroundEffect":
                    case "SpellEffectEffect":
                    case "SpellSoundEffect":
                        break;
                    case "SpellAOEEffect":
                        aoeCount++;
                        outerEffectOrder = Math.Max(outerEffectOrder, node.Order);
                        break;
                    case "SpellChainEffect":
                        chainCount++;
                        outerEffectOrder = Math.Max(outerEffectOrder, node.Order);
                        break;
                    case "SpellBurstEffect":
                        burstCount++;
                        outerEffectOrder = Math.Max(outerEffectOrder, node.Order);
                        break;
                    case "SpellProjectileEffect":
                        projectileCount++;
                        outerEffectOrder = Math.Max(outerEffectOrder, node.Order);
                        break;
                    case "SpellModEffect":
                        modifierNodeCount++;
                        break;
                    case "SpellDamageEffect":
                        directDamageCount++;
                        directDamageOrder = node.Order;
                        lastDamageOrder = Math.Max(lastDamageOrder, node.Order);
                        break;
                    case "SpellWeaponDamageEffect":
                        weaponDamageCount++;
                        weaponDamageOrder = node.Order;
                        lastDamageOrder = Math.Max(lastDamageOrder, node.Order);
                        break;
                    default:
                        reason = $"unsupported-pvp-ordered-family:{family}";
                        return false;
                }
            }
            if (directDamageCount > 1 || weaponDamageCount > 1 || aoeCount > 1 || chainCount > 1 || burstCount > 1 || projectileCount > 1)
            {
                reason = $"unsupported-pvp-ordered-effect-count:direct={directDamageCount},weapon={weaponDamageCount},aoe={aoeCount},chain={chainCount},burst={burstCount},projectile={projectileCount}";
                return false;
            }
            if (spell.HasDirectDamageEffect != (directDamageCount == 1))
            {
                reason = "unsupported-pvp-referenced-damage-order";
                return false;
            }
            if (spell.HasImmediateWeaponDamageEffect != (weaponDamageCount == 1))
            {
                reason = "unsupported-pvp-referenced-weapon-damage-order";
                return false;
            }
            if (spell.IsAoE != (aoeCount == 1) || spell.IsChainSpell != (chainCount == 1) || spell.HasBurstEffect != (burstCount == 1))
            {
                reason = "unsupported-pvp-referenced-wrapper-order";
                return false;
            }
            if (directDamageCount == 1 && weaponDamageCount == 1 && weaponDamageOrder > directDamageOrder)
            {
                reason = "unsupported-pvp-damage-order";
                return false;
            }
            int modifierCount = spell.ModifierEffects?.Count ?? 0;
            if (modifierNodeCount != modifierCount)
            {
                reason = $"unsupported-pvp-modifier-node-count:nodes={modifierNodeCount},modifiers={modifierCount}";
                return false;
            }
            int firstSemanticOrder = directDamageOrder >= 0 && weaponDamageOrder >= 0
                ? Math.Min(directDamageOrder, weaponDamageOrder)
                : Math.Max(directDamageOrder, weaponDamageOrder);
            int previousModifierOrder = -1;
            if (spell.ModifierEffects != null)
            {
                for (int modifierIndex = 0; modifierIndex < spell.ModifierEffects.Count; modifierIndex++)
                {
                    SpellModifierData modifier = spell.ModifierEffects[modifierIndex];
                    if (modifier == null || modifier.EffectOrder < 0)
                    {
                        reason = "unsupported-pvp-referenced-modifier-order";
                        return false;
                    }
                    if (!spell.OrderedEffects.Any(node => node != null
                        && node.Order == modifier.EffectOrder
                        && string.Equals(node.Family, "SpellModEffect", StringComparison.Ordinal)))
                    {
                        reason = "unsupported-pvp-referenced-modifier-node";
                        return false;
                    }
                    if (lastDamageOrder >= 0 && modifier.EffectOrder < lastDamageOrder)
                    {
                        reason = "unsupported-pvp-modifier-before-damage";
                        return false;
                    }
                    if (modifier.EffectOrder <= previousModifierOrder)
                    {
                        reason = "unsupported-pvp-modifier-order";
                        return false;
                    }
                    previousModifierOrder = modifier.EffectOrder;
                    if (firstSemanticOrder < 0)
                        firstSemanticOrder = modifier.EffectOrder;
                }
            }
            if (outerEffectOrder >= 0 && (firstSemanticOrder < 0 || outerEffectOrder > firstSemanticOrder))
            {
                reason = "unsupported-pvp-wrapper-order";
                return false;
            }
            return true;
        }

    }

    public sealed class SpellEffectNodeData
    {
        public int Order;
        public int SiblingOrder;
        public int Depth;
        public int PackageEntryId;
        public string CanonicalPath;
        public string Extends;
        public string Family;
        public string ModifierId;
        public string ReferencedEffectId;
        public int ChanceF32;
        public string ChainProjectileId;
        public int ChainProjectilePackageEntryId;
        public string ChainProjectileCanonicalPath;
        public SpellEffectNodeData ReferencedEffect;
        public List<SpellEffectNodeData> Children = new List<SpellEffectNodeData>();
    }

    public sealed class SpellCastModifierData
    {
        public bool Resolved;
        public string ModifierId;
        public int PackageEntryId;
        public string CanonicalPath;
        public string Family;
        public int DurationTicks;
        public int DescriptionPackageEntryId;
        public string DescriptionCanonicalPath;
        public string DescriptionFamily;
        public string VisualId;
        public string InitSoundId;
        public bool RemoveOnDeath;
        public string StackRule;
        public List<string> ModifierProperties = new List<string>();
        public List<string> DescriptionProperties = new List<string>();
        public List<string> ModifierChildren = new List<string>();
        public List<string> DescriptionChildren = new List<string>();
    }

    public class SpellData
    {
        public FriendlySummonPlan FriendlySummon { get; internal set; }
        public string ShortName;
        public string SkillId;
        public string DisplayName;
        public AttackType AttackType;
        public DamageElement DamageType;
        public int ChanceF32 = 0x6400;
        public int DamageModF32;
        public int DamageVolatilityF32;
        public int CriticalChanceF32;
        public int DamageStunMod = 50;
        public int DamageStunModFactorF32 = 0x100;
        public int DamageStunModIncF32;
        public int CooldownF32;
        public int CoolDownInc;
        public ushort CooldownTicks;
        public int Range;
        public int InitUseRangeF32 = 64000;
        public int ClientSyncToleranceF32;
        public int ProjectileSpeedF32;
        public int ProjectileSizeF32;
        public int ProjectileOffsetF32 = 5 * 0x100;
        public int ProjectileLifespanF32;
        public bool HasBurstEffect;
        public int BurstArcMinF32;
        public int BurstArcMaxF32;
        public int BurstCountMinF32;
        public int BurstCountMaxF32;
        public int BurstOffsetF32 = 10 * 0x100;
        public int BurstDistanceF32 = 100 * 0x100;
        public int RepeatCount = 1;
        public int AnimationId;
        public int AnimationLengthFrames = 30;
        public int AnimationTriggerFrames;
        public int ManaCostModF32;
        public int GoldValueModF32;
        public int MaxSkillLevel;
        public int RequiredLevel;
        public int RequiredLevelIncF32;
        public string ProfessionType;
        public int ProfessionTypeMask;
        public string TargetType;
        public bool InstantUse;
        public bool AddModifierWhileClosing;
        public string CastModifierId;
        public SpellCastModifierData CastModifier;
        public SkillCategory SkillCategory;
        public bool IsAoE;
        public bool HasAoEEffect;
        public bool IsChainSpell;
        public int NumChains;
        public int NumChainsIncrementF32;
        public int ChainRange;
        public int ChainDelayTicks;
        public int ChainLifespanTicks;
        public int NumForks = 1;
        public bool IsWeaponSkill;
        public bool HasSpellDamageEffect;
        public bool HasSpellKnockDownEffect;
        public bool HasTeleportEffect;
        public int SpellKnockDownStrengthMin;
        public int SpellKnockDownStrengthMax;
        public int SpellKnockDownStrengthInc;
        public int SpellKnockDownChanceF32 = 0x6400;
        public bool HasFear;
        public bool HasSlow;
        public bool AdjustCooldownByWeapon;
        public bool InitUseResetsCooldown;
        public string EffectId;
        public SpellEffectNodeData EffectRoot;
        public List<SpellEffectNodeData> OrderedEffects = new List<SpellEffectNodeData>();
        public List<SpellModifierData> ModifierEffects = new List<SpellModifierData>();
        public bool HasImmediateWeaponDamageEffect;
        public string ProjectileEffectId;
        public string ProjectileModifierId;
        public string ProjectileModifierEffectId;
        public AttackType? ProjectileModifierAttackType;
        public DamageElement? ProjectileModifierDamageType;
        public int ProjectileModifierDurationF32;
        public int ProjectileModifierFrequencyF32;
        public string ProjectileModifierStackRule;
        public int ProjectileModifierDamageModF32;
        public int ProjectileModifierDamageVolatilityF32;
        public int ProjectileModifierCriticalChanceF32;
        public int ProjectileModifierChanceF32 = 0x6400;
        public int ProjectileModifierDamageStunMod = 50;
        public int ProjectileModifierDamageStunModFactorF32 = 0x100;
        public int ProjectileModifierDamageStunModIncF32;
        public int ARModMin;
        public int ARModMax;
        public int ARModInc;
        public int WeaponEffectDamageModMin;
        public int WeaponEffectDamageModMax;
        public int WeaponEffectDamageModInc;
        public int WeaponEffectArcMinF32;
        public int WeaponEffectArcMaxF32;
        public int WeaponEffectArcIncF32;
        public int SkillDamageModMin;
        public int SkillDamageModMax;
        public int SkillDamageModInc;
        public int NumTargetsMinF32;
        public int NumTargetsMaxF32;
        public int NumTargetsIncF32;
        public int AoERadiusF32;
        public int AoERadiusMinF32;
        public int AoERadiusMaxF32;
        public int AoERadiusIncF32;

        public bool HasDirectDamageEffect => HasSpellDamageEffect;

        public bool HasDeferredProjectileModifierDamage =>
            !string.IsNullOrEmpty(ProjectileModifierEffectId) &&
            ProjectileModifierDamageModF32 > 0 &&
            ProjectileModifierFrequencyF32 > 0;

        public bool HasProjectileModifierDamage => HasDeferredProjectileModifierDamage;
        public bool HasAnyDamage => HasDirectDamageEffect || HasImmediateWeaponDamageEffect || HasDeferredProjectileModifierDamage;
        public AttackType EffectiveProjectileModifierAttackType => ProjectileModifierAttackType ?? AttackType;
        public DamageElement EffectiveProjectileModifierDamageType => ProjectileModifierDamageType ?? DamageType;

        public int ResolveAoERadiusF32(int skillLevel)
        {
            int minF32 = AoERadiusMinF32 > 0 ? AoERadiusMinF32 : AoERadiusF32;
            if (minF32 <= 0)
                return 0;
            long valueF32 = (long)minF32 + (long)Math.Max(1, skillLevel) * AoERadiusIncF32;
            int maxF32 = AoERadiusMaxF32 > 0 ? AoERadiusMaxF32 : 0;
            if (maxF32 > 0 && valueF32 > maxF32)
                valueF32 = maxF32;
            return valueF32 >= int.MaxValue ? int.MaxValue : (int)valueF32;
        }

        public int ResolveNumTargets(int skillLevel)
        {
            if (NumTargetsMinF32 <= 0 && NumTargetsMaxF32 <= 0)
                return int.MaxValue;
            long valueF32 = (long)NumTargetsMinF32 + (long)Math.Max(1, skillLevel) * NumTargetsIncF32;
            if (NumTargetsMaxF32 > 0 && valueF32 > NumTargetsMaxF32)
                valueF32 = NumTargetsMaxF32;
            int result = valueF32 >= ((long)int.MaxValue << 8) ? int.MaxValue : (int)(valueF32 >> 8);
            return result < 1 ? 1 : result;
        }

        public int ResolveWeaponEffectArcF32(int skillLevel)
        {
            long value = (long)WeaponEffectArcMinF32 + (long)Math.Max(1, skillLevel) * WeaponEffectArcIncF32;
            if (WeaponEffectArcMaxF32 > 0 && value > WeaponEffectArcMaxF32)
                value = WeaponEffectArcMaxF32;
            if (value > int.MaxValue)
                return int.MaxValue;
            if (value < int.MinValue)
                return int.MinValue;
            return (int)value;
        }

        public bool UsesWeaponArcHitContext(int skillLevel)
        {
            return HasImmediateWeaponDamageEffect
                && (string.Equals(TargetType, "POSITION", StringComparison.OrdinalIgnoreCase)
                    || ResolveWeaponEffectArcF32(skillLevel) != 0);
        }

        public int ResolveSpellKnockDownStrength(int skillLevel)
        {
            int level = Math.Max(0, skillLevel);
            int value = SpellKnockDownStrengthMin + SpellKnockDownStrengthInc * level;
            if (SpellKnockDownStrengthMax > 0 && value > SpellKnockDownStrengthMax)
                value = SpellKnockDownStrengthMax;
            return value;
        }

        public int ResolvePowerLevelF32(int skillLevel)
        {
            return ResolvePowerLevelF32(RequiredLevel, RequiredLevelIncF32, skillLevel);
        }

        public static int ResolvePowerLevelF32(int requiredLevel, int requiredLevelIncF32, int skillLevel)
        {
            int levelF32 = unchecked(Math.Max(1, skillLevel) << 8);
            int scaledIncrementF32 = unchecked((int)(((long)unchecked(levelF32 - 0x100) * requiredLevelIncF32) >> 8));
            return unchecked(((requiredLevel << 8) + scaledIncrementF32) & ~0xFF);
        }
    }

    public class SpellModifierData
    {
        public string EffectId;
        public string ModifierId;
        public string ModifierEffectId;
        public string ModifierFamily;
        public int PackageEntryId;
        public string CanonicalPath;
        public int EffectOrder = -1;
        public int ModifierPackageEntryId;
        public string ModifierCanonicalPath;
        public int ModifierEffectPackageEntryId;
        public string ModifierEffectCanonicalPath;
        public SpellEffectNodeData ModifierEffectRoot;
        public int DurationF32;
        public int DurationIncF32;
        public int FrequencyF32;
        public string StackRule;
        public bool RemoveOnDeath;
        public int TerminateWhenHitChance;
        public List<SpellAttributeModifierData> Attributes = new List<SpellAttributeModifierData>();

        public int ResolveDurationF32(int skillLevel)
        {
            long levelF32 = (long)Math.Max(1, skillLevel) << 8;
            long duration = DurationF32 + (((levelF32 - 0x100L) * DurationIncF32) >> 8);
            if (duration <= 0)
                return 0;
            return duration >= int.MaxValue ? int.MaxValue : (int)duration;
        }

        public ushort ResolveDurationTicks(int skillLevel)
        {
            long ticks = ((long)ResolveDurationF32(skillLevel) * 30L + 0x100L) >> 8;
            if (ticks <= 0)
                return 0;
            return ticks >= ushort.MaxValue ? ushort.MaxValue : (ushort)ticks;
        }
    }

    public class SpellAttributeModifierData
    {
        public string Attribute;
        public bool OverrideTable;
        public int ValueF32;
        public int ValueIncF32;
        public int Order;
        public int PackageEntryId;
        public string CanonicalPath;

        public int ResolveLevelValue(int skillLevel)
        {
            long levelF32 = (long)Math.Max(1, skillLevel) << 8;
            long valueF32 = ValueF32 + (((levelF32 - 0x100L) * ValueIncF32) >> 8);
            short nativeValue = unchecked((short)(valueF32 >> 8));
            return nativeValue;
        }
    }

    public enum AttackType { MELEE, MAGIC, RANGED }
    public enum DamageElement { PHYSICAL, DIVINE, FIRE, ICE, POISON, SHADOW }
    public enum SkillCategory
    {
        Offensive,
        WeaponSkill,
        CrowdControl,
        Debuff,
        Heal,
        Utility,
        Summon,
        Passive
    }
}
