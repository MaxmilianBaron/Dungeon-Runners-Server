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
    {
        private static void MaterializeMonsterUnitSlots(Monster monster)
        {
            if (monster == null)
                return;

            monster.Slots ??= new UnitSlotState();
            monster.Slots.ResetDefaults();
            monster.Slots[UnitSlot.AttackRating] = monster.AttackRatingF32;
            monster.Slots[UnitSlot.DamageMod] = monster.DamageModPercent;
            monster.Slots[UnitSlot.DpsModifier] = monster.DamageFactorF32;
            monster.Slots[UnitSlot.DefenseRating] = monster.DefenseRatingF32;
            monster.Slots[UnitSlot.CriticalChance] = monster.CritChanceF32;
            monster.Slots[UnitSlot.StunMod] = monster.StunMod;
            monster.Slots[UnitSlot.StunResist] = monster.StunResist;
            monster.Slots[UnitSlot.MaxHealth] = (int)Math.Min(int.MaxValue, monster.MaxHPWire);
            monster.Slots[UnitSlot.MaxMana] = (int)Math.Min(int.MaxValue, monster.MaxManaWire);
            monster.Slots[UnitSlot.HealthRegen] = monster.HealthRegenF32;
            monster.Slots[UnitSlot.ManaRegen] = monster.ManaRegenF32;
            monster.Slots[UnitSlot.GlobalDamageTakenMod] = monster.DamageTakenModPercent;
            monster.Slots[UnitSlot.DamageImmunity] = monster.DamageImmunityPercent;
            monster.Slots[UnitSlot.DamageResist] = monster.DamageResistPercent;
            monster.Slots[UnitSlot.CrushingResist] = monster.CrushingResistPercent;
            monster.Slots[UnitSlot.PiercingResist] = monster.PiercingResistPercent;
            monster.Slots[UnitSlot.SlashingResist] = monster.SlashingResistPercent;
            monster.Slots[UnitSlot.FireResist] = monster.FireResistPercent;
            monster.Slots[UnitSlot.IceResist] = monster.IceResistPercent;
            monster.Slots[UnitSlot.PoisonResist] = monster.PoisonResistPercent;
            monster.Slots[UnitSlot.ShadowResist] = monster.ShadowResistPercent;
            monster.Slots[UnitSlot.DivineResist] = monster.DivineResistPercent;
            monster.Slots[UnitSlot.MagicResist] = monster.MagicDamageResistPercent;
            monster.Slots[UnitSlot.FireDamageTakenMod] = monster.FireDamageTakenModPercent;
            monster.Slots[UnitSlot.IceDamageTakenMod] = monster.IceDamageTakenModPercent;
            monster.Slots[UnitSlot.PoisonDamageTakenMod] = monster.PoisonDamageTakenModPercent;
            monster.Slots[UnitSlot.ShadowDamageTakenMod] = monster.ShadowDamageTakenModPercent;
            monster.Slots[UnitSlot.DivineDamageTakenMod] = monster.DivineDamageTakenModPercent;
            Debug.LogError($"[CLIENT-SLOTS] monster={monster.Name}#{monster.EntityId} source=Unit::computeAttributes slots='{monster.Slots.DescribeDamageCore()}' status=materialized-desc-only activeSkillEffects=validated-or-fail-closed");
        }

        private void InitializeMonsterAiRuntime(Monster monster)
        {
            if (monster == null)
                return;

            monster.Ai ??= new MonsterAiRuntime();
            monster.Ai.SetState(MonsterStateId.IdleSearch, "spawn->idle");
            monster.Ai.SkillListBuilt = false;
            monster.Ai.TargetEntityId = 0;
            monster.Ai.AlertSourceEntityId = 0;
            if (HasEncounterObject(monster))
            {
                EncounterRuntime encounter = GetOrCreateEncounterRuntime(monster);
                encounter.AddUnit(monster.EntityId);
                monster.Encounter = encounter;
                ApplyEncounterGroup(encounter);
            }
            Debug.LogError($"[MON-FSM] monster={monster.Name}#{monster.EntityId} clientState={monster.Ai.StateId} encounterState={monster.EncounterObjectState} live={monster.EncounterLiveUnitCount} returning={monster.EncounterReturningUnitCount} sourceFunction=MonsterBehavior2::States+EncounterObject::update status=schema-only");
        }

        private string ResolveEncounterRuntimeKey(Monster monster)
        {
            if (monster == null || string.IsNullOrWhiteSpace(monster.EncounterGroupKey))
                return null;
            string instance = !string.IsNullOrWhiteSpace(monster.InstanceKey)
                ? RoomRuntime.NormalizeInstanceKey(monster.InstanceKey)
                : RoomRuntime.NormalizeInstanceKey(monster.ZoneName);
            return $"{instance}:{monster.EncounterGroupKey}";
        }

        private EncounterRuntime GetOrCreateEncounterRuntime(Monster monster)
        {
            string key = ResolveEncounterRuntimeKey(monster);
            if (string.IsNullOrWhiteSpace(key))
                return null;
            if (!_encounterRuntimes.TryGetValue(key, out EncounterRuntime runtime))
            {
                runtime = new EncounterRuntime { Key = key };
                _encounterRuntimes[key] = runtime;
                _encounterRuntimeOrder.Add(key);
                Debug.LogError($"[ENCOUNTER-RUNTIME] create key='{key}' sourceFunction=EncounterObject::update shared=True packetRuntime=unresolved");
            }
            return runtime;
        }

        private static void ApplyEncounterMirror(Monster monster)
        {
            EncounterRuntime encounter = monster?.Encounter;
            if (monster == null || encounter == null)
                return;

            monster.EncounterObjectState = encounter.StateByte;
            monster.EncounterLiveUnitCount = encounter.LiveUnitCount;
            monster.EncounterReturningUnitCount = encounter.ReturningUnitCount;
            monster.EncounterActiveTimer = encounter.ActiveTimer;
            monster.EncounterScanTimer = encounter.ScanTimer;
            monster.EncounterScanEnabled = encounter.ScanEnabled;
        }

        private void ApplyEncounterGroup(EncounterRuntime encounter)
        {
            if (encounter == null)
                return;
            foreach (var monster in GetAllMonsters())
            {
                if (monster?.Encounter == encounter)
                    ApplyEncounterMirror(monster);
            }
        }

        private void TickEncounterDeactivation()
        {
            if (_encounterRuntimes.Count == 0 || _players.Count == 0)
                return;
            foreach (string encounterKey in _encounterRuntimeOrder)
            {
                if (!_encounterRuntimes.TryGetValue(encounterKey, out EncounterRuntime encounter))
                    continue;
                if (encounter == null || encounter.StateByte != 2)
                    continue;
                bool playerInRange = false;
                foreach (var monster in GetAllMonsters())
                {
                    if (monster == null || monster.Encounter != encounter || !monster.IsAlive)
                        continue;
                    int keepF32 = monster.ShoutRangeF32 > 0 ? monster.ShoutRangeF32 : 50 * UnitMover.Fixed;
                    long keepRadiusSq = (long)keepF32 * keepF32;
                    foreach (var player in GetPlayersInEntityOrder())
                    {
                        if (player == null) continue;
                        long dx = (long)player.PosFixedX - monster.PosFixedX;
                        long dy = (long)player.PosFixedY - monster.PosFixedY;
                        if (dx * dx + dy * dy <= keepRadiusSq)
                        {
                            playerInRange = true;
                            break;
                        }
                    }
                    if (playerInRange)
                        break;
                }
                if (encounter.TickActive(playerInRange))
                    Debug.LogError($"[ENCOUNTER-RUNTIME] key='{encounter.Key}' deactivate state={encounter.StateByte} live={encounter.LiveUnitCount} reason=player-left sourceFunction=EncounterObject::update@0x00563040+0x13c");
            }
        }

        private void EncounterMarkActive(Monster monster, string reason)
        {
            if (!HasEncounterObject(monster))
                return;
            EncounterRuntime encounter = monster.Encounter ?? GetOrCreateEncounterRuntime(monster);
            if (encounter == null)
                return;
            monster.Encounter = encounter;
            encounter.MarkActive();
            ApplyEncounterGroup(encounter);
            Debug.LogError($"[ENCOUNTER-RUNTIME] key='{encounter.Key}' state={encounter.StateByte} live={encounter.LiveUnitCount} returning={encounter.ReturningUnitCount} reason={reason ?? "active"} sourceFunction=EncounterObject+0x142 shared=True");
        }

        private void EncounterLogAssistNotActive(Monster monster, string reason)
        {
            if (!HasEncounterObject(monster))
                return;
            EncounterRuntime encounter = monster.Encounter ?? GetOrCreateEncounterRuntime(monster);
            if (encounter == null)
                return;
            monster.Encounter = encounter;
            ApplyEncounterMirror(monster);
            Debug.LogError($"[ENCOUNTER-RUNTIME] key='{encounter.Key}' state={encounter.StateByte} live={encounter.LiveUnitCount} returning={encounter.ReturningUnitCount} reason={reason ?? "assist"} action=not-active sourceFunction=MonsterBehavior2::States@0x0051BB30 assist=0x0C no-EncounterObject+0x142-write");
        }

        private void EncounterOnUnitDied(Monster monster, string reason)
        {
            if (!HasEncounterObject(monster))
                return;
            EncounterRuntime encounter = monster.Encounter ?? GetOrCreateEncounterRuntime(monster);
            if (encounter == null)
                return;
            monster.Encounter = encounter;
            encounter.MarkUnitDied(monster.EntityId);
            ApplyEncounterGroup(encounter);
            ApplyEncounterMirror(monster);
            Debug.LogError($"[ENCOUNTER-RUNTIME] key='{encounter.Key}' onUnitDied unit={monster.EntityId} state={encounter.StateByte} live={encounter.LiveUnitCount} returning={encounter.ReturningUnitCount} scan={encounter.ScanEnabled} activeTimer={encounter.ActiveTimer} reason={reason ?? "death"} sourceFunction=EncounterObject::OnUnitDied shared=True");
        }

        private void EncounterOnUnitRemoved(Monster monster, string reason)
        {
            if (!HasEncounterObject(monster))
                return;
            EncounterRuntime encounter = monster.Encounter ?? GetOrCreateEncounterRuntime(monster);
            if (encounter == null)
                return;
            monster.Encounter = encounter;
            bool reset = encounter.MarkUnitRemoved(monster.EntityId);
            ApplyEncounterGroup(encounter);
            ApplyEncounterMirror(monster);
            Debug.LogError($"[ENCOUNTER-RUNTIME] key='{encounter.Key}' onUnitRemoved unit={monster.EntityId} reset={reset} state={encounter.StateByte} live={encounter.LiveUnitCount} returning={encounter.ReturningUnitCount} scan={encounter.ScanEnabled} scanTimer={encounter.ScanTimer} activeTimer={encounter.ActiveTimer} reason={reason ?? "remove"} sourceFunction=EncounterObject::OnUnitRemoved shared=True");
        }

        private List<string> BuildAuthoredArchetypeAncestry(params string[] roots)
        {
            var ancestry = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var gc = GCDatabase.Instance;
            if (gc == null || !gc.IsLoaded)
                return ancestry;

            foreach (string root in roots)
            {
                if (string.IsNullOrWhiteSpace(root))
                    continue;
                foreach (string path in gc.GetInheritanceChainPaths(root))
                {
                    if (seen.Add(path))
                        ancestry.Add(path);
                }
            }
            return ancestry;
        }
        private string ResolveDungeonCreaturePath(string zoneName, string baseGcType)
        {
            if (string.IsNullOrEmpty(zoneName) || string.IsNullOrEmpty(baseGcType))
                return null;
            var gc = GCDatabase.Instance;
            if (gc == null || !gc.IsLoaded)
                return null;

            string lookupZone = zoneName;
            int instIdx = zoneName.IndexOf("_inst", StringComparison.OrdinalIgnoreCase);
            if (instIdx > 0)
                lookupZone = zoneName.Substring(0, instIdx);

            if (!lookupZone.StartsWith("dungeon", StringComparison.OrdinalIgnoreCase))
                return null;
            int underscoreIdx = lookupZone.IndexOf('_');
            if (underscoreIdx <= 0) return null;
            string dungeonPrefix = lookupZone.Substring(0, underscoreIdx);

            int lvlIdx = lookupZone.IndexOf("_level", StringComparison.OrdinalIgnoreCase);
            if (lvlIdx < 0 || lvlIdx + 8 > lookupZone.Length) return null;
            string numStr = lookupZone.Substring(lvlIdx + 6, 2);
            if (!int.TryParse(numStr, out int levelNum) || levelNum < 1 || levelNum > 3)
                return null;
            int rank = levelNum;

            if (baseGcType.Equals("creatures.whiskers.broodling.basic.champion", StringComparison.OrdinalIgnoreCase))
                return null;

            string rankSuffix = $".rank{rank}";
            string dungeonMobPrefix = $"world.{dungeonPrefix}.mob.";
            foreach (string candidate in gc.RegisteredPaths)
            {
                if (string.IsNullOrWhiteSpace(candidate) ||
                    !candidate.EndsWith(rankSuffix, StringComparison.OrdinalIgnoreCase))
                    continue;

                string resolved = null;
                if (candidate.StartsWith(dungeonMobPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    resolved = candidate;
                }
                else if (candidate.IndexOf('.') == candidate.LastIndexOf('.'))
                {
                    resolved = dungeonMobPrefix + candidate;
                }

                if (string.IsNullOrEmpty(resolved))
                    continue;
                var candidateAncestry = BuildResolvedAuthoredAncestrySet(gc, resolved);
                if (!candidateAncestry.Contains(baseGcType))
                    continue;

                Debug.LogError($"[SPAWN-ARCHETYPE] base='{baseGcType}' zone='{lookupZone}' resolved='{resolved}' source=authored-ancestry");
                return resolved;
            }

            RuntimeEvidence.LogFallbackHit("spawn-archetype", "dungeon-family-unresolved", $"zone={lookupZone} base={baseGcType} rank={rank}", 32);
            return null;
        }

        private static HashSet<string> BuildResolvedAuthoredAncestrySet(GCDatabase gc, string path)
        {
            var ancestry = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (gc == null || string.IsNullOrWhiteSpace(path))
                return ancestry;

            string current = path.Trim();
            for (int depth = 0; depth < 32 && !string.IsNullOrWhiteSpace(current); depth++)
            {
                if (!ancestry.Add(current))
                    break;
                var node = gc.Resolve(current);
                if (node == null)
                    break;
                if (!string.IsNullOrWhiteSpace(node.Name))
                    ancestry.Add(node.Name);
                current = node.Extends;
            }

            return ancestry;
        }

        private GCNode ResolveAuthoredCreatureNode(string spawnGcType, string baseGcType)
        {
            var gc = GCDatabase.Instance;
            if (gc == null || !gc.IsLoaded) return null;

            GCNode node = null;
            if (!string.IsNullOrEmpty(spawnGcType))
                node = gc.ResolveWithInheritance(spawnGcType);
            if (node == null && !string.IsNullOrEmpty(baseGcType))
                node = gc.ResolveWithInheritance(baseGcType);
            return node;
        }

        private GCNode ResolveAuthoredBehaviorNode(string behaviorGcType)
        {
            var gc = GCDatabase.Instance;
            if (gc == null || !gc.IsLoaded || string.IsNullOrEmpty(behaviorGcType)) return null;
            return gc.ResolveWithInheritance(behaviorGcType);
        }

        private string ResolveAuthoredChildPath(string rootPath, string childPath)
        {
            if (string.IsNullOrEmpty(rootPath) || string.IsNullOrEmpty(childPath)) return null;
            var gc = GCDatabase.Instance;
            if (gc == null || !gc.IsLoaded) return null;

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string currentPath = rootPath;
            while (!string.IsNullOrEmpty(currentPath) && visited.Add(currentPath))
            {
                var node = gc.Resolve(currentPath);
                if (node == null) return null;
                if (RawChildPathExists(node, childPath))
                    return currentPath + "." + childPath;
                currentPath = node.Extends;
            }
            return null;
        }

        private bool RawChildPathExists(GCNode node, string childPath)
        {
            if (node == null || string.IsNullOrEmpty(childPath)) return false;
            var current = node;
            foreach (string part in childPath.Split('.'))
            {
                if (current == null || !current.Children.TryGetValue(part, out current))
                    return false;
            }
            return true;
        }

        private GCNode ResolveEffectiveAuthoredChild(string rootPath, string childPath)
        {
            if (string.IsNullOrEmpty(rootPath) || string.IsNullOrEmpty(childPath)) return null;
            var gc = GCDatabase.Instance;
            if (gc == null || !gc.IsLoaded) return null;
            return gc.ResolveWithInheritance(rootPath)?.ResolvePath(childPath);
        }

        private Dictionary<string, ManipulatorData> BuildSpawnManipulators(string spawnGcType, string baseGcType, GCNode authoredCreature, Dictionary<string, ManipulatorData> fallback)
        {
            var result = new Dictionary<string, ManipulatorData>(StringComparer.OrdinalIgnoreCase);
            var seenSkillPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var manipulatorCandidates = new[]
            {
                authoredCreature?.GetChild("Manipulators"),
                ResolveEffectiveAuthoredChild(spawnGcType, "Manipulators"),
                ResolveEffectiveAuthoredChild(baseGcType, "Manipulators")
            };
            bool hadManipulatorNode = false;
            bool hadPrimaryWeaponNode = false;
            int skillIndex = 1;
            foreach (var manipulators in manipulatorCandidates)
            {
                if (manipulators == null)
                    continue;
                hadManipulatorNode = true;
                var primaryWeapon = manipulators.GetChild("PrimaryWeapon");
                if (primaryWeapon != null && !result.ContainsKey("primaryweapon"))
                {
                    hadPrimaryWeaponNode = true;
                    string primaryWeaponPath = ResolveManipulatorRuntimeType(primaryWeapon);
                    if (!string.IsNullOrEmpty(primaryWeaponPath))
                        result["primaryweapon"] = CreateSpawnWeaponManipulator(primaryWeaponPath, primaryWeapon);
                    else
                    {
                        RuntimeEvidence.LogFallbackHit(
                            "spawn-manipulator",
                            "primaryweapon-unresolved",
                            $"spawn='{spawnGcType ?? ""}' base='{baseGcType ?? ""}'",
                            16);
                    }
                }

                foreach (GCNode child in manipulators.EnumerateChildrenInOrder())
                {
                    if (child == null || string.Equals(child.Name, "PrimaryWeapon", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string manipulatorPath = ResolveManipulatorRuntimeType(child);
                    if (string.IsNullOrEmpty(manipulatorPath) && !child.IsAnonymous && !string.IsNullOrWhiteSpace(child.Name))
                        manipulatorPath = ResolveAuthoredChildPath(spawnGcType, "Manipulators." + child.Name) ?? ResolveAuthoredChildPath(baseGcType, "Manipulators." + child.Name);
                    if (string.IsNullOrEmpty(manipulatorPath) && IsActiveSkillManipulatorPath(child.Extends))
                        manipulatorPath = child.Extends;
                    if (string.IsNullOrEmpty(manipulatorPath)) continue;
                    if (!IsActiveSkillManipulatorPath(manipulatorPath) && !IsActiveSkillManipulatorPath(child.Extends)) continue;
                    if (!seenSkillPaths.Add(manipulatorPath)) continue;
                    result[$"skill{skillIndex++}"] = CreateSpawnManipulator(manipulatorPath, child);
                }
            }

            if (result.Count > 0)
            {
                if (hadManipulatorNode && skillIndex > 1)
                    Debug.LogError($"[MON-SKILL-INHERIT] spawn='{spawnGcType ?? ""}' base='{baseGcType ?? ""}' skills={skillIndex - 1} sourceFunction=GCObject::createChildInstances@0x005E9840");
                return result;
            }

            if (authoredCreature != null)
            {
                string reason = hadManipulatorNode
                    ? (hadPrimaryWeaponNode ? "authored-primary-unresolved" : "authored-empty")
                    : "authored-missing-manipulators";
                RuntimeEvidence.LogFallbackHit(
                    "spawn-manipulator",
                    reason,
                    $"spawn='{spawnGcType ?? ""}' base='{baseGcType ?? ""}'",
                    16);
                Debug.LogError($"[AUTHORED-COVERAGE] area=spawn-manipulator reason={reason} spawn='{spawnGcType ?? ""}' base='{baseGcType ?? ""}' dbFallbackSuppressed=True");
                return result;
            }

            RuntimeEvidence.LogFallbackHit(
                "spawn-manipulator",
                hadManipulatorNode ? "authored-empty" : "missing-authored",
                $"spawn='{spawnGcType ?? ""}' base='{baseGcType ?? ""}' dbFallbackSuppressed=True dbCount={fallback?.Count ?? 0}",
                16);
            Debug.LogError($"[AUTHORED-COVERAGE] area=spawn-manipulator reason={(hadManipulatorNode ? "authored-empty" : "missing-authored")} spawn='{spawnGcType ?? ""}' base='{baseGcType ?? ""}' dbFallbackSuppressed=True dbCount={fallback?.Count ?? 0}");
            return result;
        }

        private string ResolveManipulatorRuntimeType(GCNode manipulatorNode)
        {
            if (manipulatorNode == null) return null;
            if (!string.IsNullOrEmpty(manipulatorNode.Extends))
                return manipulatorNode.Extends;
            return null;
        }

        private ManipulatorData CreateSpawnManipulator(string gcType, GCNode node)
        {
            var data = new ManipulatorData { gcType = gcType };
            CopyManipulatorProperties(data, ResolveAuthoredWeaponDescription(node));
            CopyManipulatorProperties(data, node);
            CopyManipulatorProperties(data, node?.GetChild("Description"));
            return data;
        }

        private ManipulatorData CreateSpawnWeaponManipulator(string gcType, GCNode node)
        {
            var data = new ManipulatorData { gcType = gcType };
            CopyManipulatorProperties(data, ResolveSpawnWeaponDescription(data));
            if (node?.Properties.TryGetValue("ID", out string nodeId) == true)
                data.properties["ID"] = nodeId;
            if (node?.GetChild("Description")?.Properties.TryGetValue("ID", out string descriptionId) == true)
                data.properties["ID"] = descriptionId;
            return data;
        }

        private GCNode ResolveSpawnWeaponDescription(ManipulatorData manipulator)
        {
            if (manipulator == null || string.IsNullOrWhiteSpace(manipulator.gcType))
                return null;
            GCNode weapon = GCDatabase.Instance?.ResolveWithInheritance(manipulator.gcType);
            return weapon?.GetChild("Description") ?? weapon;
        }

        private void InitializeMonsterState0Idle(Monster monster, MersenneTwister roomRng)
        {
            if (monster == null)
                return;

            monster.State0IdleDelayTicks = 0;
            monster.State0IdleInitialized = true;
            if (!monster.UseIdleTime)
            {
                Debug.LogError($"[MON-STATE0] {monster.Name}#{monster.EntityId} idleDelay=skip useIdleTime=false base={monster.BaseTime} variable={monster.VariableTime} roomRngPos={roomRng?.CallsSinceReseed} sourceFunction=MonsterBehavior2::States@0x0051bd7f");
                return;
            }

            uint variableSeconds = 0;
            uint raw = 0;
            if (monster.VariableTime != 0)
            {
                if (roomRng == null)
                    throw new InvalidOperationException($"Missing room RNG for MonsterBehavior2 state0 idle delay: {monster.Name}#{monster.EntityId}");
                raw = RngLedger.Generate(roomRng, "room", "monster-state0:MonsterBehavior2::States+idle-delay", monster.InstanceKey);
                variableSeconds = raw % monster.VariableTime;
            }

            uint delayTicks = ((uint)monster.BaseTime + variableSeconds) * CLIENT_TICKS_PER_SECOND;
            monster.State0IdleDelayTicks = delayTicks;
            Debug.LogError($"[MON-STATE0] {monster.Name}#{monster.EntityId} idleDelay=armed raw=0x{raw:X8} useIdleTime=true base={monster.BaseTime} variable={monster.VariableTime} residue={variableSeconds} delayTicks={monster.State0IdleDelayTicks} roomRngPos={roomRng?.CallsSinceReseed} sourceFunction=MonsterBehavior2::States@0x0051bdae StateMachine::SendMessageA@0x005f09f0 Random::generate@0x0044b1f0");
        }

        private void ConfigureMonsterPrimaryActiveSkill(Monster monster)
        {
            if (monster == null) return;
            monster.ActiveSkills.Clear();
            monster.BlockedProcModifierPaths.Clear();
            monster.PrimaryAttackSkill = null;
            monster.SelectedActiveSkill = null;
            monster.SelectedActiveSkillTargetEntityId = 0;
            monster.ActiveSkillUseCommittedThisAttack = false;
            monster.PrimaryActiveSkillPath = null;
            monster.PrimaryActiveSkillId = 10;
            monster.PrimaryActiveSkillRangeF32 = 0;
            monster.PrimaryActiveSkillSpellUseRangeF32 = 0;
            monster.PrimaryActiveSkillMinimumRangeF32 = -0x100;
            monster.PrimaryActiveSkillTargetType = null;
            monster.PrimaryActiveSkillSpellUse = null;
            monster.PrimaryActiveSkillHasSelfHealthPct = false;
            monster.PrimaryActiveSkillSelfHealthPctF32 = 0;
            monster.PrimaryActiveSkillHasTargetHealthPct = false;
            monster.PrimaryActiveSkillTargetHealthPctF32 = 0;
            monster.PrimaryActiveSkillCooldownTicks = 0;
            monster.PrimaryActiveSkillCooldownRemainingTicks = 0;
            monster.PrimaryActiveSkillCooldownLastTick = CombatTick;
            monster.PrimaryActiveSkillAnimationId = 0;
            monster.PrimaryActiveSkillEffect = null;
            monster.PrimaryActiveSkillCastModifier = null;
            monster.UsePrimaryActiveSkillThisAttack = false;

            if (monster.Manipulators == null) return;
            for (int skillIndex = 1; ; skillIndex++)
            {
                if (!monster.Manipulators.TryGetValue("skill" + skillIndex, out ManipulatorData manipulator))
                    break;
                if (!IsActiveSkillManipulatorPath(manipulator?.gcType))
                {
                    if (IsProcModifierManipulatorPath(manipulator?.gcType))
                    {
                        monster.BlockedProcModifierPaths.Add(manipulator.gcType);
                        SkillEffectTracker.RecordRejected("monster-proc", monster.EntityId, manipulator.gcType, 0, "BLOCKED_EVIDENCE:monster-proc-runtime-unproven", CombatTick);
                        Debug.LogError($"[MON-SKILL-EFFECT-SUPPORT] state=BLOCKED_EVIDENCE monster={monster.Name}#{monster.EntityId} manipulator=skill{skillIndex} path={manipulator.gcType} reason=monster-proc-runtime-unproven resourceCommit=False rngDraw=False");
                    }
                    continue;
                }

                var skill = CreateMonsterActiveSkillRuntime(monster, manipulator, IsPrimaryActiveSkillManipulator(manipulator));
                if (skill == null)
                    continue;
                skill.CooldownLastTick = CombatTick;
                if (skill.IsPrimaryAttack && monster.PrimaryAttackSkill == null)
                    monster.PrimaryAttackSkill = skill;
                else
                    monster.ActiveSkills.Add(skill);
                LogMonsterActiveSkillEffectSupport(monster, skill);
            }

            Debug.LogError($"[MON-SKILL] build-list {monster.Name}#{monster.EntityId} active={monster.ActiveSkills.Count} primary={monster.PrimaryAttackSkill?.Path ?? "none"} sourceFunction=MonsterBehavior2::BuildSkillLists@0x0051D6D0");
        }

        private MonsterActiveSkillRuntime CreateMonsterActiveSkillRuntime(Monster monster, ManipulatorData manipulator, bool isPrimaryAttack)
        {
            GCNode description = ResolveAuthoredActiveSkillDescription(manipulator);
            if (description == null)
            {
                Debug.LogError($"[MON-SKILL] blocked reason=missing-authored-description path={manipulator?.gcType ?? ""} sourceFunction=ActiveSkillDesc::ActiveSkillDesc@0x0053B740");
                return null;
            }

            int cooldownBaseF32 = Math.Max(0, ResolveSkillFixed32(description, manipulator, "CoolDown", 0));
            int cooldownInc = ResolveSkillInt(description, manipulator, "CoolDownInc", 0);
            bool hasMinimumRange = TryResolveSkillFixed32(description, manipulator, "SpellUseMinimumRange", out int minimumRangeF32);
            int selfHealthPctF32 = ResolveSkillFixed32(description, manipulator, "SelfHealthPct", 0x100);
            int targetHealthPctF32 = ResolveSkillFixed32(description, manipulator, "TargetHealthPct", 0x100);
            int rangeF32 = ResolveSkillFixed32(description, manipulator, "Range", 75 * 0x100);
            int spellUseRangeF32 = ResolveSkillFixed32(description, manipulator, "SpellUseRange", 75 * 0x100);
            int requiredLevel = ResolveRequiredSkillInt(description, manipulator, "RequiredLevel");
            int requiredLevelInc = ResolveRequiredSkillInt(description, manipulator, "RequiredLevelInc");
            int maxSkillLevel = ResolveRequiredSkillInt(description, manipulator, "MaxSkillLevel");
            int ownerLevel = Math.Max(1, (int)(monster?.Level ?? 1));
            int skillLevel = ownerLevel < requiredLevel
                ? 1
                : 1 + (ownerLevel - requiredLevel) / requiredLevelInc;
            skillLevel = Math.Clamp(skillLevel, 1, Math.Min(byte.MaxValue, maxSkillLevel));
            long cooldownF32Long = (long)cooldownBaseF32 + (long)(skillLevel - 1) * cooldownInc * 0x100L;
            int cooldownF32 = (int)Math.Clamp(cooldownF32Long, 0L, int.MaxValue);
            int cooldownTicks = Fixed32SecondsToClientTicks(cooldownF32);
            uint manaCostWire = 0x100;
            string targetType = ResolveRequiredSkillString(description, manipulator, "TargetType");
            string spellUse = ResolveRequiredSkillString(description, manipulator, "SpellUse");
            string professionType = ResolveSkillString(description, manipulator, "ProfessionType", "");
            return new MonsterActiveSkillRuntime
            {
                Path = manipulator.gcType,
                Id = GetManipulatorByte(manipulator, "ID", 10),
                RangeF32 = rangeF32,
                SpellUseRangeF32 = spellUseRangeF32,
                SpellUseMinimumRangeF32 = hasMinimumRange ? minimumRangeF32 : -1,
                TargetType = targetType,
                SpellUse = spellUse,
                HasSelfHealthPct = true,
                SelfHealthPctF32 = selfHealthPctF32,
                HasTargetHealthPct = true,
                TargetHealthPctF32 = targetHealthPctF32,
                CooldownTicks = (ushort)Math.Clamp(cooldownTicks, 0, ushort.MaxValue),
                CooldownRemainingTicks = 0,
                DelayBeforeUseTicks = isPrimaryAttack
                    ? (ushort)0
                    : (ushort)Math.Clamp(ResolveSkillInt(description, manipulator, "DelayBeforeUse", 0), 0, ushort.MaxValue),
                ManaCostWire = manaCostWire,
                SkillLevel = (byte)skillLevel,
                RepeatCount = (byte)Math.Clamp(ResolveSkillInt(description, manipulator, "RepeatCount", 1), byte.MinValue, byte.MaxValue),
                AnimationId = ResolveSkillInt(description, manipulator, "AnimationID", 60),
                ProfessionTypeMask = ResolveMonsterProfessionTypeMask(professionType),
                Effect = ResolveSkillString(description, manipulator, "Effect", null),
                CastModifier = ResolveSkillString(description, manipulator, "CastModifier", null),
                InstantUse = ResolveSkillBool(description, manipulator, "InstantUse", false),
                AddModifierWhileClosing = ResolveSkillBool(description, manipulator, "AddModifierWhileClosing", false),
                IsPrimaryAttack = isPrimaryAttack
            };
        }

        private static int ResolveMonsterProfessionTypeMask(string value)
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

        private static GCNode ResolveAuthoredActiveSkillDescription(ManipulatorData manipulator)
        {
            if (manipulator == null || string.IsNullOrWhiteSpace(manipulator.gcType))
                return null;
            GCNode skill = GCDatabase.Instance?.ResolveWithInheritance(manipulator.gcType);
            if (skill == null)
                return null;
            return skill.GetChild("Description") ?? skill;
        }

        private static bool TryGetAuthoredSkillValue(GCNode description, string property, out string value)
        {
            value = null;
            if (description?.Properties == null || !description.Properties.TryGetValue(property, out string authoredValue))
                return false;
            if (string.IsNullOrWhiteSpace(authoredValue))
                throw new InvalidDataException($"Authored active skill property '{property}' is empty for '{description.Name}'.");
            value = authoredValue.Trim().Trim('"');
            return true;
        }

        private static string ResolveSkillString(GCNode description, ManipulatorData manipulator, string property, string nativeDefault)
        {
            if (TryGetAuthoredSkillValue(description, property, out string authoredValue))
                return authoredValue;
            return GetManipulatorString(manipulator, property, nativeDefault);
        }

        private static bool ResolveSkillBool(GCNode description, ManipulatorData manipulator, string property, bool nativeDefault)
        {
            string raw = ResolveSkillString(description, manipulator, property, nativeDefault ? "true" : "false");
            if (bool.TryParse(raw, out bool parsed))
                return parsed;
            if (int.TryParse(raw, out int numeric))
                return numeric != 0;
            throw new InvalidDataException($"Authored active skill property '{property}' has invalid boolean value '{raw}' for '{description?.Name ?? ""}'.");
        }

        private static string ResolveRequiredSkillString(GCNode description, ManipulatorData manipulator, string property)
        {
            string value = ResolveSkillString(description, manipulator, property, null);
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException($"Authored active skill property '{property}' is missing for '{description?.Name ?? ""}'.");
            return value;
        }

        private static int ResolveSkillInt(GCNode description, ManipulatorData manipulator, string property, int nativeDefault)
        {
            if (TryGetAuthoredSkillValue(description, property, out string authoredValue))
            {
                if (!int.TryParse(authoredValue, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int authoredInt))
                    throw new InvalidDataException($"Authored active skill property '{property}' is not an integer for '{description?.Name ?? ""}'.");
                return authoredInt;
            }
            return GetManipulatorInt(manipulator, property, nativeDefault);
        }

        private static int ResolveRequiredSkillInt(GCNode description, ManipulatorData manipulator, string property)
        {
            if (!TryGetAuthoredSkillValue(description, property, out string authoredValue)
                && (manipulator?.properties == null || !manipulator.properties.ContainsKey(property)))
                throw new InvalidDataException($"Authored active skill property '{property}' is missing for '{description?.Name ?? ""}'.");
            return ResolveSkillInt(description, manipulator, property, 0);
        }

        private static int ResolveSkillFixed32(GCNode description, ManipulatorData manipulator, string property, int nativeDefaultF32)
        {
            return TryResolveSkillFixed32(description, manipulator, property, out int valueF32)
                ? valueF32
                : nativeDefaultF32;
        }

        private static bool TryResolveSkillFixed32(GCNode description, ManipulatorData manipulator, string property, out int valueF32)
        {
            valueF32 = 0;
            if (TryGetAuthoredSkillValue(description, property, out string authoredValue))
            {
                if (!GCNode.TryParseFixed32(authoredValue, out valueF32))
                    throw new InvalidDataException($"Authored active skill property '{property}' is not fixed32 for '{description?.Name ?? ""}'.");
                return true;
            }
            return TryGetManipulatorFixed32(manipulator, property, out valueF32);
        }

        private static void ApplyMonsterActiveSkillSelection(Monster monster, MonsterActiveSkillRuntime skill)
        {
            if (monster == null || skill == null) return;
            monster.SelectedActiveSkill = skill;
            monster.PrimaryActiveSkillPath = skill.Path;
            monster.PrimaryActiveSkillId = skill.Id;
            monster.PrimaryActiveSkillRangeF32 = skill.RangeF32;
            monster.PrimaryActiveSkillSpellUseRangeF32 = skill.SpellUseRangeF32;
            monster.PrimaryActiveSkillMinimumRangeF32 = skill.SpellUseMinimumRangeF32;
            monster.PrimaryActiveSkillTargetType = skill.TargetType;
            monster.PrimaryActiveSkillSpellUse = skill.SpellUse;
            monster.PrimaryActiveSkillHasSelfHealthPct = skill.HasSelfHealthPct;
            monster.PrimaryActiveSkillSelfHealthPctF32 = skill.SelfHealthPctF32;
            monster.PrimaryActiveSkillHasTargetHealthPct = skill.HasTargetHealthPct;
            monster.PrimaryActiveSkillTargetHealthPctF32 = skill.TargetHealthPctF32;
            monster.PrimaryActiveSkillCooldownTicks = skill.CooldownTicks;
            monster.PrimaryActiveSkillCooldownRemainingTicks = skill.CooldownRemainingTicks;
            monster.PrimaryActiveSkillCooldownLastTick = skill.CooldownLastTick;
            monster.PrimaryActiveSkillAnimationId = skill.AnimationId;
            monster.PrimaryActiveSkillEffect = skill.Effect;
            monster.PrimaryActiveSkillCastModifier = skill.CastModifier;
        }

        private bool IsPrimaryActiveSkillManipulator(ManipulatorData manipulator)
        {
            if (manipulator == null || string.IsNullOrWhiteSpace(manipulator.gcType))
                return false;
            if (!IsActiveSkillManipulatorPath(manipulator.gcType))
                return false;
            if (TryGetManipulatorBool(manipulator, "IsPrimaryAttack", out bool primaryFromManipulator))
                return primaryFromManipulator;

            var node = GCDatabase.Instance?.ResolveWithInheritance(manipulator.gcType);
            var desc = node?.GetChild("Description") ?? node;
            return desc != null && desc.GetBool("IsPrimaryAttack", false);
        }

        private bool IsActiveSkillManipulatorPath(string gcType)
        {
            var gc = GCDatabase.Instance;
            string current = gcType;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (!string.IsNullOrWhiteSpace(current) && visited.Add(current))
            {
                if (current.Equals("ActiveSkill", StringComparison.OrdinalIgnoreCase)
                    || current.EndsWith(".ActiveSkill", StringComparison.OrdinalIgnoreCase))
                    return true;

                var node = gc?.Resolve(current);
                current = node?.Extends;
            }
            return false;
        }

        private static bool IsProcModifierManipulatorPath(string gcType)
        {
            if (string.IsNullOrWhiteSpace(gcType))
                return false;
            GCNode node = GCDatabase.Instance?.ResolveWithInheritance(gcType);
            return node != null && AuthoredExtends(node, "ProcModifier");
        }

        private void CopyManipulatorProperties(ManipulatorData data, GCNode node)
        {
            if (data == null || node == null) return;
            foreach (var propertyEntry in node.EnumeratePropertiesInOrder())
                data.properties[propertyEntry.Key] = propertyEntry.Value;
        }

        private static int GetManipulatorInt(ManipulatorData manipulator, string property, int fallback)
        {
            if (manipulator?.properties == null || !manipulator.properties.TryGetValue(property, out string raw))
                return fallback;
            return int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int value)
                ? value
                : fallback;
        }

        private static byte GetManipulatorByte(ManipulatorData manipulator, string property, byte fallback)
        {
            int value = GetManipulatorInt(manipulator, property, fallback);
            return (byte)Mathf.Clamp(value, byte.MinValue, byte.MaxValue);
        }

        private static string GetManipulatorString(ManipulatorData manipulator, string property, string fallback)
        {
            if (manipulator?.properties == null || !manipulator.properties.TryGetValue(property, out string raw))
                return fallback;
            return string.IsNullOrWhiteSpace(raw) ? fallback : raw.Trim().Trim('"');
        }

        private static bool TryGetManipulatorBool(ManipulatorData manipulator, string property, out bool value)
        {
            value = false;
            if (manipulator?.properties == null || !manipulator.properties.TryGetValue(property, out string raw))
                return false;
            raw = raw?.Trim().Trim('"');
            if (bool.TryParse(raw, out value))
                return true;
            if (int.TryParse(raw, out int intValue))
            {
                value = intValue != 0;
                return true;
            }
            return false;
        }

        private GCNode GetAuthoredBehaviorDescription(GCNode creatureNode)
        {
            var behavior = creatureNode?.GetChild("Behavior");
            return ResolveAuthoredBehaviorDescription(behavior);
        }

        private GCNode ResolveAuthoredBehaviorDescription(GCNode behaviorNode)
        {
            return ResolveAuthoredBehaviorDescription(behaviorNode, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        private GCNode ResolveAuthoredBehaviorDescription(GCNode behaviorNode, HashSet<string> visited)
        {
            if (behaviorNode == null) return null;
            string key = behaviorNode.Name + "|" + (behaviorNode.Extends ?? "");
            if (!visited.Add(key)) return behaviorNode.GetChild("Description") ?? behaviorNode;

            GCNode baseDescription = null;
            if (!string.IsNullOrEmpty(behaviorNode.Extends))
            {
                var baseBehavior = GCDatabase.Instance?.ResolveWithInheritance(behaviorNode.Extends);
                baseDescription = ResolveAuthoredBehaviorDescription(baseBehavior, visited);
            }

            var description = behaviorNode.GetChild("Description") ?? behaviorNode;
            return baseDescription == null ? description : MergeAuthoredNodes(baseDescription, description);
        }

        private GCNode ResolveAuthoredWeaponDescription(GCNode weaponNode)
        {
            return ResolveAuthoredWeaponDescription(weaponNode, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        private GCNode ResolveAuthoredWeaponDescription(GCNode weaponNode, HashSet<string> visited)
        {
            if (weaponNode == null) return null;
            string key = weaponNode.Name + "|" + (weaponNode.Extends ?? "");
            if (!visited.Add(key)) return weaponNode.GetChild("Description") ?? weaponNode;

            GCNode baseDescription = null;
            if (!string.IsNullOrEmpty(weaponNode.Extends))
            {
                var baseWeapon = GCDatabase.Instance?.ResolveWithInheritance(weaponNode.Extends);
                baseDescription = ResolveAuthoredWeaponDescription(baseWeapon, visited);
            }

            var description = weaponNode.GetChild("Description") ?? weaponNode;
            return baseDescription == null ? description : MergeAuthoredNodes(baseDescription, description);
        }

        private GCNode MergeAuthoredNodes(GCNode parent, GCNode child)
        {
            return GCNode.MergeInherited(parent, child);
        }

        private string GetAuthoredString(GCNode node, string property, string fallback)
        {
            return node != null && node.HasProperty(property) ? node.GetString(property, fallback) : fallback;
        }

        private int GetAuthoredInt(GCNode node, string property, int fallback)
        {
            return node != null && node.HasProperty(property) ? node.GetInt(property, fallback) : fallback;
        }

        private bool GetAuthoredBool(GCNode node, string property, bool fallback)
        {
            return node != null && node.HasProperty(property) ? node.GetBool(property, fallback) : fallback;
        }

        private static uint ResolveUnitDescMaxManaWire(GCNode authoredDesc)
        {
            bool enabled = GetAuthoredFixed32(authoredDesc, "MaxMana", 0x100) != 0;
            return (enabled ? 10000u : 1u) * 256u;
        }

        private static int GetRequiredGlobalKnobFixed32(string property)
        {
            if (string.IsNullOrWhiteSpace(property))
                throw new InvalidDataException("Global knob name is missing.");
            return GCDatabase.Instance.GetRequiredKnobFixed32(property);
        }

        private static int ResolveUnitDescStunMod(GCNode authoredDesc, bool isAlive)
        {
            if (!isAlive)
                return 0;
            long product = (long)GetAuthoredFixed32(authoredDesc, "StunMod", 1 << 8)
                * GetRequiredGlobalKnobFixed32("MonsterStunMod");
            return unchecked((ushort)(product >> 16));
        }

        private static int ResolveUnitDescStunResist(GCNode authoredDesc, bool isAlive)
        {
            if (!isAlive)
                return 0;
            long product = (long)GetAuthoredFixed32(authoredDesc, "StunResist", 1 << 8)
                * GetRequiredGlobalKnobFixed32("MonsterStunResist");
            return unchecked((ushort)(product >> 16));
        }

        private static int GetAuthoredFixed32(GCNode node, string property, int fallback)
        {
            if (node == null || string.IsNullOrEmpty(property) || !node.Properties.TryGetValue(property, out string text))
                return fallback;
            return GCNode.TryParseFixed32(text, out int valueF32) ? valueF32 : fallback;
        }

        private int GetAuthoredBehaviorFixed32(GCNode behaviorNode, GCNode creatureNode, string property, int fallbackF32)
        {
            var behaviorDesc = ResolveAuthoredBehaviorDescription(behaviorNode);
            if (behaviorDesc != null && behaviorDesc.HasProperty(property))
                return GetAuthoredFixed32(behaviorDesc, property, fallbackF32);
            var creatureDesc = GetAuthoredBehaviorDescription(creatureNode);
            return GetAuthoredFixed32(creatureDesc, property, fallbackF32);
        }

        private string GetAuthoredBehaviorString(GCNode behaviorNode, GCNode creatureNode, string property, string fallback)
        {
            var behaviorDesc = ResolveAuthoredBehaviorDescription(behaviorNode);
            if (behaviorDesc != null && behaviorDesc.HasProperty(property))
                return behaviorDesc.GetString(property, fallback);
            var creatureDesc = GetAuthoredBehaviorDescription(creatureNode);
            return GetAuthoredString(creatureDesc, property, fallback);
        }

        private int GetAuthoredBehaviorInt(GCNode behaviorNode, GCNode creatureNode, string property, int fallback)
        {
            var behaviorDesc = ResolveAuthoredBehaviorDescription(behaviorNode);
            if (behaviorDesc != null && behaviorDesc.HasProperty(property))
                return behaviorDesc.GetInt(property, fallback);
            var creatureDesc = GetAuthoredBehaviorDescription(creatureNode);
            return GetAuthoredInt(creatureDesc, property, fallback);
        }

        private bool GetAuthoredBehaviorBool(GCNode behaviorNode, GCNode creatureNode, string property, bool fallback)
        {
            var behaviorDesc = ResolveAuthoredBehaviorDescription(behaviorNode);
            if (behaviorDesc != null && behaviorDesc.HasProperty(property))
                return behaviorDesc.GetBool(property, fallback);
            var creatureDesc = GetAuthoredBehaviorDescription(creatureNode);
            return GetAuthoredBool(creatureDesc, property, fallback);
        }

        private bool ShouldRegisterWander(string idleAction, int wanderRangeF32, string zoneName)
        {
            string action = NormalizeAuthoredEnumToken(idleAction);
            if (!string.IsNullOrEmpty(action))
            {
                if (IsWanderIdleAction(action))
                    return true;
                if (action.Equals("FOLLOW", StringComparison.OrdinalIgnoreCase))
                    return false;
                if (action.Equals("GUARD", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (action.Equals("NOTHING", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return wanderRangeF32 > 0;
        }

        private static bool IsWanderIdleAction(string idleAction)
        {
            string action = NormalizeAuthoredEnumToken(idleAction);
            return action.Equals("WANDER", StringComparison.OrdinalIgnoreCase) ||
                   action.Equals("3", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeAuthoredEnumToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            return value.Trim().Trim('"', '\'');
        }

        private static bool HasEncounterObject(Monster monster)
        {
            return monster != null && !string.IsNullOrEmpty(monster.EncounterGroupKey);
        }

        private static bool HasWanderEncounterOwner(Monster monster)
        {
            return HasEncounterObject(monster);
        }

        private static bool HasEncounterObjectActiveSearch(Monster monster)
        {
            ApplyEncounterMirror(monster);
            return HasEncounterObject(monster) && monster.EncounterObjectState == 2;
        }

        private static bool HasAlertEncounterRelation(Monster listener, Monster source, out string relation)
        {
            relation = "invalid";
            if (listener == null || source == null)
                return false;
            if (!HasEncounterObject(listener))
            {
                relation = "listener-no-encounter";
                return true;
            }
            if (!HasEncounterObject(source))
            {
                relation = "source-no-encounter";
                return false;
            }
            if (string.Equals(listener.EncounterGroupKey, source.EncounterGroupKey, StringComparison.OrdinalIgnoreCase))
            {
                relation = "same-encounter";
                return true;
            }
            relation = "different-encounter";
            return false;
        }

        private int GetAuthoredMoveSpeedF32(GCNode authoredDesc, CreatureData creature)
        {
            if (authoredDesc != null && authoredDesc.HasProperty("Speed"))
                return GetAuthoredFixed32(authoredDesc, "Speed", 50 * 0x100);
            return creature?.GetFixed32(creature.speed, 50 * 0x100) ?? 50 * 0x100;
        }

        private int GetAuthoredWalkSpeedF32(GCNode authoredCreature, GCNode authoredDesc, CreatureData creature)
        {
            if (authoredDesc != null && authoredDesc.HasProperty("WalkSpeed"))
                return GetAuthoredFixed32(authoredDesc, "WalkSpeed", 25 * 0x100);
            if (authoredCreature != null && authoredCreature.HasProperty("WalkSpeed"))
                return GetAuthoredFixed32(authoredCreature, "WalkSpeed", 25 * 0x100);
            if (creature != null && !string.IsNullOrWhiteSpace(creature.walkSpeed))
                return creature.GetFixed32(creature.walkSpeed, 25 * 0x100);
            var stock = GCDatabase.Instance?.ResolveWithInheritance("creatures.base.UnitStock");
            if (stock != null && stock.HasProperty("WalkSpeed"))
                return GetAuthoredFixed32(stock, "WalkSpeed", 25 * 0x100);
            return Math.Min(GetAuthoredMoveSpeedF32(authoredDesc, creature), 25 * 0x100);
        }

        private int GetAuthoredWanderRangeF32(GCNode behaviorNode, GCNode creatureNode)
        {
            if (creatureNode != null && creatureNode.HasProperty("WanderRange"))
                return GetAuthoredFixed32(creatureNode, "WanderRange", 0);
            return GetAuthoredBehaviorFixed32(behaviorNode, creatureNode, "WanderRange", 100 * 0x100);
        }

        private struct AttackTiming
        {
            public int[] TotalFrames;
            public int[] HitFrames;
            public int[] SoundFrames;
            public bool[] VariantResolved;
            public ResolutionSource Source;
            public string Reason;
        }

        private AttackTiming ResolveAttackTiming(GCNode authoredDesc, bool weaponUsesProjectile, string source)
        {
            var timing = new AttackTiming
            {
                TotalFrames = new[] { 0, 0, 0 },
                HitFrames = new[] { 0, 0, 0 },
                SoundFrames = new[] { 0, 0, 0 },
                VariantResolved = new[] { false, false, false },
                Source = ResolutionSource.Blocked,
                Reason = "unresolved"
            };

            string animationsPath = GetAuthoredString(authoredDesc, "Animations", "");
            if (string.IsNullOrEmpty(animationsPath))
            {
                timing.Reason = "missing-animations-path";
                Debug.LogError($"[ATTACK-TIMING] source='{source ?? "unknown"}' reason={timing.Reason} sourceKind={timing.Source} sourceFunction={(weaponUsesProjectile ? "RangedWeapon::computeAttackTicks@0x00596560" : "MeleeWeapon::computeAttackTicks@0x00592520")}");
                return timing;
            }

            var animations = GCDatabase.Instance.ResolveWithInheritance(animationsPath);
            if (animations == null || animations.AnonymousChildren == null || animations.AnonymousChildren.Count == 0)
            {
                timing.Reason = "empty-animation-node";
                Debug.LogError($"[ATTACK-TIMING] source='{source ?? "unknown"}' path='{animationsPath}' reason={timing.Reason} sourceKind={timing.Source} sourceFunction={(weaponUsesProjectile ? "RangedWeapon::computeAttackTicks@0x00596560" : "MeleeWeapon::computeAttackTicks@0x00592520")}");
                return timing;
            }

            var animationById = new Dictionary<int, GCNode>();
            foreach (var animation in animations.AnonymousChildren)
            {
                if (animation == null) continue;
                int id = animation.GetInt("ID", 0);
                if (id > 0 && !animationById.ContainsKey(id))
                    animationById[id] = animation;
            }

            bool foundAny = false;
            bool invalidAny = false;
            for (int variant = 0; variant < 3; variant++)
            {
                int normalId = 110 + variant;
                int specialId = 510 + variant;
                GCNode row = null;
                int resolvedId = 0;
                if (animationById.TryGetValue(normalId, out row))
                    resolvedId = normalId;
                else if (animationById.TryGetValue(specialId, out row))
                    resolvedId = specialId;

                if (row == null)
                {
                    Debug.LogError($"[ATTACK-TIMING-ROW] source='{source ?? "unknown"}' path='{animationsPath}' variant={variant} animationId=missing sourceKind={ResolutionSource.Blocked} reason=missing-animation");
                    continue;
                }

                int total = row.GetInt("NumFrames", 0);
                int hit = row.GetInt("TriggerTime", 0);
                int sound = row.GetInt("SoundTriggerTime", 0);
                if (total <= 0 || hit <= 0 || sound <= 0)
                {
                    invalidAny = true;
                    RuntimeEvidence.LogFallbackHit(
                        "attack-animation",
                        "invalid-authored-attack-frames",
                        $"source='{source ?? "unknown"}' path='{animationsPath}' variant={variant} animationId={resolvedId} total={total} hit={hit} sound={sound}",
                        32);
                    continue;
                }
                timing.TotalFrames[variant] = total;
                timing.HitFrames[variant] = hit;
                timing.SoundFrames[variant] = sound;
                timing.VariantResolved[variant] = true;
                foundAny = true;
                Debug.LogError($"[ATTACK-TIMING-ROW] source='{source ?? "unknown"}' path='{animationsPath}' variant={variant} animationId={resolvedId} total={timing.TotalFrames[variant]} hit={timing.HitFrames[variant]} sound={timing.SoundFrames[variant]} sourceKind={ResolutionSource.Client}");
            }

            if (foundAny && !invalidAny)
            {
                timing.Source = ResolutionSource.Client;
                timing.Reason = "animation-id-map";
            }
            else if (!foundAny)
            {
                timing.Source = ResolutionSource.Blocked;
                timing.Reason = invalidAny ? "invalid-authored-attack-frames" : "missing-attack-trigger";
            }
            else
            {
                timing.Source = ResolutionSource.Blocked;
                timing.Reason = "invalid-authored-attack-frames";
            }

            Debug.LogError($"[ATTACK-TIMING] source='{source ?? "unknown"}' path='{animationsPath}' total=[{string.Join(",", timing.TotalFrames)}] hit=[{string.Join(",", timing.HitFrames)}] sound=[{string.Join(",", timing.SoundFrames)}] sourceKind={timing.Source} reason={timing.Reason}");
            return timing;
        }

        private int CountAuthoredSounds(GCNode authoredDesc, string soundId)
        {
            string soundsPath = GetAuthoredString(authoredDesc, "Sounds", "");
            if (string.IsNullOrEmpty(soundsPath)) return 0;
            var sounds = GCDatabase.Instance.ResolveWithInheritance(soundsPath);
            if (sounds?.AnonymousChildren == null) return 0;
            foreach (var sound in sounds.AnonymousChildren)
            {
                string current = sound.GetString("SoundId", "");
                if (!string.Equals(current, soundId, StringComparison.OrdinalIgnoreCase)) continue;
                string list = sound.GetString("Sounds", "");
                if (string.IsNullOrWhiteSpace(list)) return 0;
                int count = 0;
                foreach (string item in list.Split(','))
                {
                    if (!string.IsNullOrWhiteSpace(item.Trim().Trim('"'))) count++;
                }
                return count;
            }
            return 0;
        }

        private bool HasAuthoredAttackSound(GCNode authoredDesc)
        {
            return CountAuthoredSounds(authoredDesc, "ATTACK") > 0 || CountAuthoredSounds(authoredDesc, "WEAPONATTACK") > 0;
        }

        public List<Monster> SpawnFactionGroupFixed(string faction, string tier, int centerFixedX, int centerFixedY, int centerFixedZ, int count, int radiusFixed = 10 << 8, string zoneName = null)
        {
            var spawned = new List<Monster>();
            var creatures = AuthoredGameplayCatalog.GetCreaturesByFaction(faction);

            if (!string.IsNullOrEmpty(tier))
                creatures = creatures.FindAll(c => c.tier.Equals(tier, StringComparison.OrdinalIgnoreCase));

            if (creatures.Count == 0)
            {
                Debug.LogError($"[COMBAT] faction='{faction}' tier='{tier}' state=noCreatures");
                return spawned;
            }

            for (int spawnIndex = 0; spawnIndex < count; spawnIndex++)
            {
                var randomCreature = creatures[DungeonRunners.Engine.Random.Range(0, creatures.Count)];
                int headingDegrees = DungeonRunners.Engine.Random.Range(0, 360);
                int clampedRadiusFixed = Math.Max(0, radiusFixed);
                int distanceFixed = clampedRadiusFixed > 0 ? DungeonRunners.Engine.Random.Range(0, clampedRadiusFixed + 1) : 0;
                int spawnFixedX = centerFixedX + (int)(((long)UnitMover.ZRotateCosFixed(headingDegrees) * distanceFixed) >> 8);
                int spawnFixedY = centerFixedY + (int)(((long)UnitMover.ZRotateSinFixed(headingDegrees) * distanceFixed) >> 8);

                var monster = SpawnMonsterFixed(randomCreature.gcType, spawnFixedX, spawnFixedY, centerFixedZ, headingDegrees << 8, zoneName);
                if (monster != null)
                    spawned.Add(monster);
            }

            return spawned;
        }
        public Monster GetMonsterByComponent(uint componentId)
        {
            if (_componentToEntityMap.TryGetValue(componentId, out uint entityId))
                return GetMonster(entityId);
            return null;
        }
        public void ResetAllMonsters()
        {
            foreach (Monster monster in GetAllMonsters())
            {
                monster.IsAlive = true;
                SetRuntimeMonsterHPWire(monster, monster.MaxHPWire, false, "RESET");
                monster.UseTargetCount = 0;
                monster.AlertSourceEntityId = 0;
                monster.BehaviorAssistSourceEntityId = 0;
            }
            Debug.LogError($"[COMBAT] resetForZoneTransition monsters={_activeMonsters.Count}");
        }

    }
}
