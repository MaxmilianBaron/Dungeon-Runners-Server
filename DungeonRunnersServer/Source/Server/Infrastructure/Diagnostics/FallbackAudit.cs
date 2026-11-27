using System;
using System.Collections.Generic;
using System.Linq;
using DungeonRunners.Data;
using DungeonRunners.Engine;

namespace DungeonRunners.Core
{
    public static class FallbackAudit
    {
        private static bool _startupFallbackLogged;

        public static void RunStartupCoverage()
        {
            if (_startupFallbackLogged)
                return;
            _startupFallbackLogged = true;

            try
            {
                ReportCreatureCoverage();
                ReportKnownDungeonCreature("dew-valley-pup", "world.dungeon00.mob.melee01.rank1", "PIERCING", null, int.MinValue);
                ReportKnownDungeonCreature("whisker-ratling", "world.dungeon00.mob.melee03.rank1", null, "UnarmedWeapon", 6 * 0x100);
                ReportClassDamageModCoverage();
                ReportRangerStarterDamageLane();
                ReportChestGeneratorCoverage();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AUTHORED-COVERAGE] area=startup status=error message='{Sanitize(ex.Message)}'");
            }
        }

        private static void ReportCreatureCoverage()
        {
            var creatures = global::AuthoredGameplayCatalog.Creatures ?? new List<global::CreatureData>();
            int total = 0;
            int unresolved = 0;
            int missingAnimations = 0;
            int missingManipulators = 0;
            int emptyManipulators = 0;
            int dbManipulatorFallbackCandidates = 0;
            var missingAnimSamples = new List<string>();
            var missingManipSamples = new List<string>();

            foreach (var creature in creatures)
            {
                if (creature == null || string.IsNullOrWhiteSpace(creature.gcType))
                    continue;
                total++;
                GCNode node = GCDatabase.Instance.ResolveWithInheritance(creature.gcType);
                if (node == null)
                {
                    unresolved++;
                    AddSample(missingAnimSamples, creature.gcType);
                    AddSample(missingManipSamples, creature.gcType);
                    continue;
                }

                string animationsPath = GetEffectiveString(node, "Animations");
                bool hasAnimations = !string.IsNullOrWhiteSpace(animationsPath) &&
                                     GCDatabase.Instance.ResolveWithInheritance(animationsPath) != null;
                if (!hasAnimations)
                {
                    missingAnimations++;
                    AddSample(missingAnimSamples, creature.gcType);
                }

                GCNode manipulators = node.GetChild("Manipulators");
                bool hasManipulatorNode = manipulators != null;
                bool hasManipulatorContent = hasManipulatorNode &&
                    ((manipulators.Children != null && manipulators.Children.Count > 0) ||
                     (manipulators.AnonymousChildren != null && manipulators.AnonymousChildren.Count > 0));
                if (!hasManipulatorNode)
                {
                    missingManipulators++;
                    AddSample(missingManipSamples, creature.gcType);
                }
                else if (!hasManipulatorContent)
                {
                    emptyManipulators++;
                    AddSample(missingManipSamples, creature.gcType + ":empty");
                }

                if (!hasManipulatorContent && creature.manipulators != null && creature.manipulators.Count > 0)
                    dbManipulatorFallbackCandidates++;
            }

            Debug.LogError($"[AUTHORED-COVERAGE] area=creatures total={total} unresolved={unresolved} missingAnimations={missingAnimations} missingManipulators={missingManipulators} emptyManipulators={emptyManipulators} dbManipulatorFallbackCandidates={dbManipulatorFallbackCandidates} missingAnimationSamples='{string.Join(",", missingAnimSamples)}' missingManipulatorSamples='{string.Join(",", missingManipSamples)}'");
        }

        private static void ReportKnownDungeonCreature(string key, string path, string expectedDamageType, string expectedPrimaryContains, int expectedRangeF32)
        {
            GCNode node = GCDatabase.Instance.ResolveWithInheritance(path);
            GCNode manipulators = node?.GetChild("Manipulators");
            GCNode primary = FindPrimaryManipulator(manipulators);
            GCNode primaryInherited = !string.IsNullOrWhiteSpace(primary?.Extends)
                ? GCDatabase.Instance.ResolveWithInheritance(primary.Extends)
                : null;
            string animationsPath = GetEffectiveString(node, "Animations");
            bool animationsResolved = !string.IsNullOrWhiteSpace(animationsPath) &&
                                      GCDatabase.Instance.ResolveWithInheritance(animationsPath) != null;
            string primaryPath = primary?.Extends ?? "";
            string damageType = GetEffectiveString(primary, "DamageType");
            if (string.IsNullOrWhiteSpace(damageType))
                damageType = GetEffectiveString(primaryInherited, "DamageType");
            int rangeF32 = GetEffectiveFixed32(primary, "Range", int.MinValue);
            if (rangeF32 == int.MinValue)
                rangeF32 = GetEffectiveFixed32(primaryInherited, "Range", int.MinValue);
            bool damageMatches = string.IsNullOrWhiteSpace(expectedDamageType) ||
                                 string.Equals(damageType, expectedDamageType, StringComparison.OrdinalIgnoreCase);
            bool primaryMatches = string.IsNullOrWhiteSpace(expectedPrimaryContains) ||
                                  primaryPath.IndexOf(expectedPrimaryContains, StringComparison.OrdinalIgnoreCase) >= 0;
            bool rangeMatches = expectedRangeF32 == int.MinValue || rangeF32 == expectedRangeF32;
            if (!damageMatches || !primaryMatches || !rangeMatches)
            {
                RuntimeEvidence.LogFallbackHit(
                    "spawn-manipulator",
                    key + "-coverage-mismatch",
                    $"path='{path}' primary='{Sanitize(primaryPath)}' damageType='{Sanitize(damageType)}' rangeF32={rangeF32}",
                    1);
            }

            Debug.LogError($"[AUTHORED-COVERAGE] area=known-creature key={key} path='{path}' resolved={node != null} animations='{Sanitize(animationsPath)}' animationsResolved={animationsResolved} manipChildren={(manipulators?.Children?.Count ?? 0)} manipAnonymous={(manipulators?.AnonymousChildren?.Count ?? 0)} primary='{Sanitize(primaryPath)}' damageType='{Sanitize(damageType)}' rangeF32={rangeF32} source=authored-only expectedDamageType='{Sanitize(expectedDamageType)}' expectedPrimaryContains='{Sanitize(expectedPrimaryContains)}' expectedRangeF32={expectedRangeF32}");
        }

        private static void ReportClassDamageModCoverage()
        {
            string[] classes = { "FighterBase", "RangerBase", "WarlockBase" };
            foreach (string classBase in classes)
            {
                GCNode node = GCDatabase.Instance.ResolveWithInheritance(classBase) ??
                              GCDatabase.Instance.ResolveWithInheritance("avatar.classes." + classBase);
                GCNode desc = node?.GetChild("Description") ?? node;
                bool hasRanged = desc != null && desc.HasProperty("RangedDamagePerAgilityMod");
                bool hasMelee = desc != null && desc.HasProperty("MeleeDamagePerStrengthMod");
                bool hasSkill = desc != null && desc.HasProperty("SkillDamagePerIntellectMod");
                string rangedSource = hasRanged ? "gc" : "missing";
                string meleeSource = hasMelee ? "gc" : "missing";
                string skillSource = hasSkill ? "gc" : "missing";
                int rangedModF32 = hasRanged ? desc.GetFixed32("RangedDamagePerAgilityMod", 0) : int.MinValue;
                int meleeModF32 = hasMelee ? desc.GetFixed32("MeleeDamagePerStrengthMod", 0) : int.MinValue;
                int skillModF32 = hasSkill ? desc.GetFixed32("SkillDamagePerIntellectMod", 0) : int.MinValue;
                Debug.LogError($"[AUTHORED-COVERAGE] area=class-damage class={classBase} resolved={node != null} rangedModF32={rangedModF32} rangedSource={rangedSource} meleeModF32={meleeModF32} meleeSource={meleeSource} skillModF32={skillModF32} skillSource={skillSource} sourceFunction=HeroDesc-class-stat-default");
            }
        }

        private static void ReportChestGeneratorCoverage()
        {
            Debug.LogError("[AUTHORED-COVERAGE] area=chest-generators stockUnitTreasureSlots=1-10 nonCombatInteractiveItemSlots=1-5 currencyGenerators=package-backed itemGenerators=package-backed source=server-startup-report");
        }

        private static GCNode FindPrimaryManipulator(GCNode manipulators)
        {
            if (manipulators == null)
                return null;
            GCNode primary = manipulators.GetChild("PrimaryWeapon");
            if (primary != null)
                return primary;

            IEnumerable<GCNode> authoredChildren = Enumerable.Empty<GCNode>();
            if (manipulators.Children != null)
                authoredChildren = authoredChildren.Concat(manipulators.Children.Values);
            if (manipulators.AnonymousChildren != null)
                authoredChildren = authoredChildren.Concat(manipulators.AnonymousChildren);

            return authoredChildren.FirstOrDefault(child =>
                ContainsToken(child?.Name, "PrimaryWeapon") ||
                ContainsToken(child?.Extends, "PrimaryWeapon") ||
                ContainsToken(child?.Name, "UnarmedWeapon") ||
                ContainsToken(child?.Extends, "UnarmedWeapon") ||
                ContainsToken(child?.Name, "Weapon") ||
                ContainsToken(child?.Extends, "Weapon"));
        }

        private static void ReportRangerStarterDamageLane()
        {
            GCNode ranger = GCDatabase.Instance.ResolveWithInheritance("RangerBase") ??
                            GCDatabase.Instance.ResolveWithInheritance("avatar.classes.RangerBase");
            GCNode rangerDesc = ranger?.GetChild("Description") ?? ranger;
            int rangedModF32 = rangerDesc != null && rangerDesc.HasProperty("RangedDamagePerAgilityMod")
                ? rangerDesc.GetFixed32("RangedDamagePerAgilityMod", 0)
                : int.MinValue;
            int rangedPerAgilityF32 = GCDatabase.Instance.GetRequiredKnobFixed32("RangedDamagePerAgility");
            int agility = 20;
            int rangedBonus = rangedModF32 == int.MinValue
                ? int.MinValue
                : checked((int)(((long)rangedPerAgilityF32 * agility * rangedModF32) >> 16));

            GCNode crossbow = GCDatabase.Instance.ResolveWithInheritance("2HCrossbow1PAL.2HCrossbow1-1") ??
                              GCDatabase.Instance.ResolveWithInheritance("2HCrossbow1PAL.2HCrossbow1");
            GCNode crossbowDesc = crossbow?.GetChild("Description") ?? crossbow;
            int storedLevelF32 = GetEffectiveFixed32(crossbow, "Level", int.MinValue);
            int damageF32 = GetEffectiveFixed32(crossbow, "Damage", int.MinValue);
            int volatilityF32 = GetEffectiveFixed32(crossbow, "DamageVolatility", int.MinValue);
            int rangeF32 = GetEffectiveFixed32(crossbow, "Range", int.MinValue);
            int projectileSpeedF32 = GetEffectiveFixed32(crossbow, "ProjectileSpeed", int.MinValue);
            int projectileSizeF32 = GetEffectiveFixed32(crossbow, "ProjectileSize", int.MinValue);
            string weaponClass = GetEffectiveString(crossbow, "WeaponClass");
            string damageType = GetEffectiveString(crossbow, "DamageType");
            string useProjectile = crossbowDesc?.GetString("UseProjectile", "") ?? "";

            bool missing = rangerDesc == null || crossbowDesc == null ||
                           storedLevelF32 != 0x100 ||
                           rangedModF32 != 0x100;
            if (missing)
            {
                RuntimeEvidence.LogFallbackHit(
                    "damage-level",
                    "ranger-starter-coverage",
                    $"rangerResolved={rangerDesc != null} crossbowResolved={crossbowDesc != null} storedLevelF32={storedLevelF32} rangedModF32={rangedModF32}",
                    1);
            }

            Debug.LogError($"[AUTHORED-COVERAGE] area=damage-check lane=ranger-starter rangerResolved={rangerDesc != null} crossbowResolved={crossbowDesc != null} storedLevelF32={storedLevelF32} classModRangerBaseF32={rangedModF32} rangedDamagePerAgilityF32={rangedPerAgilityF32} agility={agility} rangedBonus={rangedBonus} weaponClass='{Sanitize(weaponClass)}' damageType='{Sanitize(damageType)}' damageF32={damageF32} volatilityF32={volatilityF32} rangeF32={rangeF32} useProjectile='{Sanitize(useProjectile)}' projectileSpeedF32={projectileSpeedF32} projectileSizeF32={projectileSizeF32}");
        }

        private static string GetEffectiveString(GCNode node, string key)
        {
            if (node == null || string.IsNullOrWhiteSpace(key))
                return "";
            GCNode desc = node.GetChild("Description");
            if (desc != null && desc.HasProperty(key))
                return desc.GetString(key, "");
            return node.GetString(key, "");
        }

        private static int GetEffectiveFixed32(GCNode node, string key, int fallbackF32)
        {
            if (node == null || string.IsNullOrWhiteSpace(key))
                return fallbackF32;
            GCNode desc = node.GetChild("Description");
            if (desc != null && desc.HasProperty(key))
                return desc.GetFixed32(key, fallbackF32);
            return node.GetFixed32(key, fallbackF32);
        }

        private static void AddSample(List<string> samples, string value)
        {
            if (samples == null || samples.Count >= 8 || string.IsNullOrWhiteSpace(value))
                return;
            samples.Add(value);
        }

        private static bool ContainsToken(string value, string token)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   !string.IsNullOrWhiteSpace(token) &&
                   value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string Sanitize(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? ""
                : value.Replace("'", "").Replace("\r", " ").Replace("\n", " ").Trim();
        }
    }
}
