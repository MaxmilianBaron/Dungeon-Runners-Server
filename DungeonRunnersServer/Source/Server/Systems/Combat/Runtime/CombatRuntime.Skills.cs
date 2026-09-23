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
    {        private bool TryResolveMonsterStunActionSkillEffect(Monster monster, out MonsterStunActionSkillEffect effect)
        {
            effect = null;
            if (monster == null || string.IsNullOrWhiteSpace(monster.PrimaryActiveSkillPath))
                return false;

            var gc = GCDatabase.Instance;
            if (gc == null || !gc.IsLoaded)
                return false;

            GCNode skill = gc.ResolveWithInheritance(monster.PrimaryActiveSkillPath);
            GCNode skillDesc = skill?.GetChild("Description") ?? skill;
            string effectPath = skillDesc?.GetString("Effect", monster.PrimaryActiveSkillEffect) ?? monster.PrimaryActiveSkillEffect;
            if (string.IsNullOrWhiteSpace(effectPath))
                return false;

            GCNode effectNode = ResolveAuthoredNodeReference(effectPath, skill);
            if (effectNode == null)
                return false;
            if (FindEffectChildByExtends(effectNode, "SpellWeaponDamageEffect") != null || FindEffectChildByExtends(effectNode, "SpellDamageEffect") != null)
                return false;

            GCNode knockDown = FindEffectChildByExtends(effectNode, "SpellKnockDownEffect");
            GCNode knockBack = FindEffectChildByExtends(effectNode, "SpellKnockBackEffect");
            GCNode action = knockDown ?? knockBack;
            if (action == null)
                return false;

            int skillLevel = 1;
            bool isKnockDown = knockDown != null;
            string family = isKnockDown ? "SpellKnockDownEffect" : "SpellKnockBackEffect";
            effect = new MonsterStunActionSkillEffect
            {
                SkillPath = monster.PrimaryActiveSkillPath,
                EffectPath = effectPath,
                ActionEffectPath = BuildAuthoredEffectPath(effectPath, action),
                ActionFamily = family,
                Strength = ResolveSkillLinearMod(action, "Strength", skillLevel),
                ChanceWire = ResolveSpellEffectChanceWire(action),
                IsKnockDown = isKnockDown
            };
            return true;
        }

        private bool TryResolveMonsterDamageModifierSkillEffect(GCNode effectNode, GCNode skill, string skillPath, string effectPath, out MonsterDamageModifierSkillEffect effect)
        {
            effect = null;
            if (effectNode == null)
                return false;

            GCNode modifierEffectRoot = effectNode;
            GCNode weaponDamage = FindEffectChild(effectNode, "SpellWeaponDamageEffect");
            string nestedEffectPath = weaponDamage?.GetString("Effect", null);
            if (!string.IsNullOrWhiteSpace(nestedEffectPath))
            {
                GCNode nestedEffectNode = ResolveAuthoredNodeReference(nestedEffectPath, skill);
                if (nestedEffectNode != null)
                    modifierEffectRoot = nestedEffectNode;
            }

            GCNode modEffect = FindEffectChild(modifierEffectRoot, "SpellModEffect");
            if (modEffect == null && modifierEffectRoot != effectNode)
                modEffect = FindEffectChild(effectNode, "SpellModEffect");
            if (modEffect == null)
                return false;

            string modifierPath = modEffect.GetString("Modifier", null);
            if (string.IsNullOrWhiteSpace(modifierPath))
                return false;

            GCNode modifier = ResolveAuthoredNodeReference(modifierPath, skill);
            GCNode modifierDesc = modifier?.GetChild("Description") ?? modifier;
            if (modifierDesc == null)
                return false;

            string modifierEffectPath = modifierDesc.GetString("Effect", null);
            if (string.IsNullOrWhiteSpace(modifierEffectPath))
                return false;

            GCNode modifierEffect = ResolveAuthoredNodeReference(modifierEffectPath, modifier ?? skill);
            if (modifierEffect == null)
                return false;

            GCNode damageEffect = FindEffectChild(modifierEffect, "SpellDamageEffect");
            if (damageEffect == null)
                return false;

            string attackType = damageEffect.GetString("AttackType", "MAGIC");
            string damageType = damageEffect.GetString("DamageType", "POISON");
            if (!DamageResolver.TryResolveDamageTypeId(damageType, out int damageTypeId))
                return false;

            int damageModF32 = damageEffect.GetFixed32Ceiling("DamageMod", 0);
            if (damageModF32 <= 0)
                return false;

            int durationF32 = modEffect.GetFixed32("Duration", 0);
            if (durationF32 <= 0)
                durationF32 = modifierDesc.GetFixed32("Duration", 0);
            int frequencyF32 = modifierDesc.GetFixed32("Frequency", 0);
            if (frequencyF32 <= 0)
                frequencyF32 = 0x100 / CLIENT_TICKS_PER_SECOND;

            effect = new MonsterDamageModifierSkillEffect
            {
                SkillPath = skillPath,
                EffectPath = nestedEffectPath ?? effectPath,
                ModifierPath = modifierPath,
                ModifierEffectPath = modifierEffectPath,
                AttackType = attackType,
                DamageType = damageType,
                DamageTypeId = damageTypeId,
                DamageKind = string.Equals(attackType, "MAGIC", StringComparison.OrdinalIgnoreCase) ? (byte)3 : (byte)0,
                DamageModF32 = damageModF32,
                DamageVolatilityF32 = damageEffect.GetFixed32Ceiling("DamageVolatility", 0),
                ChanceWire = ResolveSpellEffectChanceWire(damageEffect),
                CriticalChanceF32 = damageEffect.GetFixed32("CriticalChance", 0),
                DamageStunMod = damageEffect.GetInt("StunMod", 50),
                DamageStunModFactorF32 = damageEffect.GetFixed32("StunModFactor", 0x100),
                DamageStunModIncF32 = damageEffect.GetFixed32("StunModInc", 0),
                DurationF32 = durationF32,
                DurationTicks = ComputeSpellModDurationTicks(durationF32),
                FrequencyF32 = frequencyF32,
                FrequencyTicks = ComputeEffectModFrequencyTicks(frequencyF32),
                RemoveOnDeath = modifierDesc.GetBool("RemoveOnDeath", false),
                StackRule = modifierDesc.GetString("StackRule", "")
            };
            return true;
        }

        private static int ResolveSkillLinearMod(GCNode node, string prefix, int skillLevel)
        {
            if (node == null || string.IsNullOrWhiteSpace(prefix))
                return 0;
            int level = Mathf.Max(0, skillLevel);
            int min = node.GetInt(prefix + "Min", 0);
            int inc = node.GetInt(prefix + "Inc", 0);
            int value = min + inc * level;
            if (node.HasProperty(prefix + "Max"))
            {
                int max = node.GetInt(prefix + "Max", value);
                if (value > max) value = max;
            }
            return value;
        }

        private static string BuildAuthoredEffectPath(string effectPath, GCNode child)
        {
            if (string.IsNullOrWhiteSpace(effectPath))
                return child?.Extends ?? "unknown";
            if (child == null)
                return effectPath;
            if (!string.IsNullOrWhiteSpace(child.Name) && !child.IsAnonymous)
                return $"{effectPath}.{child.Name}";
            return $"{effectPath}.{child.Extends ?? "anonymous"}";
        }

        private void ApplyMonsterStunActionSkillEffect(Monster monster, CombatTarget target, MonsterStunActionSkillEffect effect, string marker, string source)
        {
            if (monster == null || target == null || effect == null)
                return;
            RoomRuntime runtime = GetRoomRuntimeForMonster(monster);
            MersenneTwister rng = runtime?.RoomRng;
            if (rng == null)
            {
                Debug.LogError($"[SPELL-STUN-ACTION] attacker={monster.Name}#{monster.EntityId} target={target.Name}#{target.EntityId} skill={effect.SkillPath} effect={effect.ActionEffectPath} family={effect.ActionFamily} source={source ?? marker ?? "unknown"} result=NO_RNG reason=missing-room-rng sourceFunction=SpellEffect::CheckChance@0x00545FF0 Unit::CheckStunResist@0x0050C630");
                return;
            }
            ConsumeStunActionEffectRng(rng, monster, target, effect.SkillPath, effect.ActionEffectPath, effect.ActionFamily, effect.Strength, effect.ChanceWire, marker, source);
        }

        private void ConsumeWeaponStunActionRng(MersenneTwister rng, Monster monster, CombatTarget target, MonsterWeaponDamageSkillEffect effect, string marker, string source)
        {
            if (effect == null)
                return;
            if (effect.HasKnockBack)
                ConsumeStunActionEffectRng(rng, monster, target, effect.SkillPath, effect.KnockBackEffectPath, "SpellKnockBackEffect", effect.KnockBackStrength, effect.KnockBackChanceWire, marker, source);
            if (effect.HasKnockDown)
                ConsumeStunActionEffectRng(rng, monster, target, effect.SkillPath, effect.KnockDownEffectPath, "SpellKnockDownEffect", effect.KnockDownStrength, effect.KnockDownChanceWire, marker, source);
        }

        private bool ConsumeStunActionEffectRng(MersenneTwister rng, Monster monster, CombatTarget target, string skillPath, string effectPath, string effectFamily, int strength, int chanceWire, string marker, string source)
        {
            if (rng == null)
                return false;
            string family = string.IsNullOrWhiteSpace(effectFamily) ? "SpellKnockBackEffect" : effectFamily;
            string client = family == "SpellKnockDownEffect" ? "SpellKnockDownEffect::doEffect@0x00553360" : "SpellKnockBackEffect::doEffect@0x00552C80";
            if (!ConsumeSpellEffectChanceRng(rng, monster, target, skillPath, effectPath, family, chanceWire, marker, source, out uint chanceRaw, out uint chanceRoll))
            {
                Debug.LogError($"[SPELL-STUN-ACTION] attacker={monster?.Name ?? "monster"}#{monster?.EntityId ?? 0} target={target?.Name ?? "unknown"}#{target?.EntityId ?? 0} skill={skillPath ?? "none"} effect={effectPath ?? "none"} family={family} result=CHANCE_FAIL strength={strength} chanceWire={chanceWire} chance={chanceWire / 256f:F2} chanceRaw=0x{chanceRaw:X8} chanceRoll={chanceRoll} rngPos={rng.CallsSinceReseed} packet=NOT_SENT source={source ?? marker ?? "unknown"} sourceFunction=SpellEffect::CheckChance@0x00545FF0 {client}");
                return false;
            }

            int resistWire = target.Monster != null ? ResolveMonsterStunResistChanceWire(target.Monster, monster?.Level ?? 0) : ResolvePlayerStunResistChanceWire(target, monster?.Level ?? 0);
            uint stunRaw = RngLedger.Generate(rng, "unitOwnedCombat", $"{marker ?? "MON-SKILL"}:{family}::CheckStunResist", "Unit::CheckStunResist");
            uint stunRoll = stunRaw % 0x6400u;
            bool resisted = stunRoll < (uint)Mathf.Max(0, resistWire);
            if (resisted)
            {
                Debug.LogError($"[SPELL-STUN-ACTION] attacker={monster?.Name ?? "monster"}#{monster?.EntityId ?? 0} target={target?.Name ?? "unknown"}#{target?.EntityId ?? 0} skill={skillPath ?? "none"} effect={effectPath ?? "none"} family={family} result=RESIST strength={strength} chanceWire={chanceWire} chanceRaw=0x{chanceRaw:X8} chanceRoll={chanceRoll} stunResistWire={resistWire} stunRaw=0x{stunRaw:X8} stunRoll={stunRoll} rngPos={rng.CallsSinceReseed} packet=NOT_SENT source={source ?? marker ?? "unknown"} sourceFunction={client} SpellEffect::CheckChance@0x00545FF0 Unit::CheckStunResist@0x0050C630");
                return false;
            }

            PlayerStunActionResolved action = BuildPlayerStunAction(monster, target, skillPath, effectPath, family, strength, chanceWire, chanceRaw, chanceRoll, resistWire, stunRaw, stunRoll, source ?? marker ?? "unknown");
            if (target.Monster != null)
                StartMonsterDamageReaction(target.Monster, action.ActionClassId, action.StrengthWire, monster.EntityId, source, false, true, monster.PosFixedX, monster.PosFixedY);
            else
                OnPlayerStunActionResolved?.Invoke(monster, target.Player, action);
            Debug.LogError($"[SPELL-STUN-ACTION] attacker={monster?.Name ?? "monster"}#{monster?.EntityId ?? 0} target={target?.Name ?? "unknown"}#{target?.EntityId ?? 0} skill={skillPath ?? "none"} effect={effectPath ?? "none"} family={family} result=ACTION_QUEUED action={action.ActionClassName} actionId=0x{action.ActionClassId:X2} heading={action.HeadingWire} strengthWire={action.StrengthWire} authoredStrength={action.AuthoredStrength} usesKnockDownAction={action.UsesKnockDownAction} chanceWire={chanceWire} chanceRaw=0x{chanceRaw:X8} chanceRoll={chanceRoll} stunResistWire={resistWire} stunRaw=0x{stunRaw:X8} stunRoll={stunRoll} rngPos={rng.CallsSinceReseed} source={source ?? marker ?? "unknown"} sourceFunction={client} SpellEffect::CheckChance@0x00545FF0 Unit::CheckStunResist@0x0050C630 KnockBack::writeData@0x0052A320");
            return true;
        }

        private PlayerStunActionResolved BuildPlayerStunAction(Monster monster, CombatTarget target, string skillPath, string effectPath, string family, int strength, int chanceWire, uint chanceRaw, uint chanceRoll, int resistWire, uint stunRaw, uint stunRoll, string source)
        {
            int sourceFixedX = monster?.PosFixedX ?? 0;
            int sourceFixedY = monster?.PosFixedY ?? 0;
            if (monster != null)
                TryGetMonsterClientVisiblePositionFixed(monster, target?.EntityId ?? 0, out sourceFixedX, out sourceFixedY);
            int targetFixedX = target?.PosFixedX ?? sourceFixedX;
            int targetFixedY = target?.PosFixedY ?? sourceFixedY;
            bool usesKnockDownAction;
            ushort strengthWire = ResolvePlayerStunActionStrengthWire(family, strength, out usesKnockDownAction);
            byte actionClassId = usesKnockDownAction
                ? CLIENT_PLAYER_STUN_ACTION_KNOCKDOWN_ID
                : CLIENT_PLAYER_STUN_ACTION_KNOCKBACK_ID;
            return new PlayerStunActionResolved
            {
                SkillPath = skillPath,
                EffectPath = effectPath,
                EffectFamily = family,
                ActionClassName = usesKnockDownAction ? "KnockDown" : "KnockBack",
                ActionClassId = actionClassId,
                HeadingWire = ResolveDestHeadingWire(sourceFixedX, sourceFixedY, targetFixedX, targetFixedY),
                StrengthWire = strengthWire,
                AuthoredStrength = strength,
                ChanceWire = chanceWire,
                ChanceRaw = chanceRaw,
                ChanceRoll = chanceRoll,
                StunResistWire = resistWire,
                StunRaw = stunRaw,
                StunRoll = stunRoll,
                Source = source,
                UsesKnockDownAction = usesKnockDownAction
            };
        }

        private static ushort ResolvePlayerStunActionStrengthWire(string family, int strength, out bool usesKnockDownAction)
        {
            usesKnockDownAction = string.Equals(family, "SpellKnockDownEffect", StringComparison.OrdinalIgnoreCase);
            long value = strength;
            if (value < 0) value = 0;
            if (value > ushort.MaxValue) value = ushort.MaxValue;
            return (ushort)value;
        }

        private static ushort ResolveDestHeadingWire(int sourceFixedX, int sourceFixedY, int targetFixedX, int targetFixedY)
        {
            int headingFixed = UnitMover.VectorToHeadingFixed(targetFixedX - sourceFixedX, targetFixedY - sourceFixedY);
            return (ushort)Math.Clamp(headingFixed >> 8, 0, 360);
        }

        private bool ConsumeSpellEffectChanceRng(MersenneTwister rng, Monster monster, CombatTarget target, string skillPath, string effectPath, string effectFamily, int chanceWire, string marker, string source, out uint raw, out uint roll)
        {
            raw = 0;
            roll = 0;
            int normalizedChance = Mathf.Clamp(chanceWire, 0, 0x6400);
            if (normalizedChance >= 0x6400)
                return true;
            raw = RngLedger.Generate(rng, "unitOwnedCombat", $"{marker ?? "MON-SKILL"}:{effectFamily}::CheckChance", "SpellEffect::CheckChance");
            roll = raw % 0x6464u;
            return roll < (uint)normalizedChance;
        }

        private static int ResolveSpellEffectChanceWire(GCNode node)
        {
            if (node == null)
                return 0x6400;
            return Math.Clamp(node.GetFixed32("Chance", 0x6400), 0, 0x6400);
        }

        private static int ResolvePlayerStunResistChanceWire(CombatTarget target, int attackerLevel)
        {
            int resist = (target?.PlayerState?.StunResist ?? 0) * 0x100;
            int targetLevel = target?.PlayerState?.Level ?? 0;
            int diff = (targetLevel * 0x100) - (attackerLevel * 0x100);
            if (diff > 0x500)
                resist += (int)(((long)(diff - 0x500) * 0x500) >> 8);
            if (resist > 0x5A00) resist = 0x5A00;
            if (resist < 0x500) resist = 0x500;
            return resist;
        }

        private static GCNode ResolveAuthoredNodeReference(string path, GCNode contextRoot)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            var gc = GCDatabase.Instance;
            GCNode node = gc?.ResolveWithInheritance(path);
            if (node != null)
                return node;

            if (contextRoot == null)
                return null;

            string rootName = contextRoot.Name;
            if (!string.IsNullOrWhiteSpace(rootName) &&
                path.StartsWith(rootName + ".", StringComparison.OrdinalIgnoreCase))
            {
                string subPath = path.Substring(rootName.Length + 1);
                return ResolveChildPath(contextRoot, subPath);
            }

            int dot = path.IndexOf('.');
            while (dot >= 0 && dot + 1 < path.Length)
            {
                string suffix = path.Substring(dot + 1);
                GCNode child = ResolveChildPath(contextRoot, suffix);
                if (child != null)
                    return child;
                dot = path.IndexOf('.', dot + 1);
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

        private static GCNode FindEffectChild(GCNode node, string clientExtends)
        {
            if (node == null)
                return null;
            if (AuthoredExtends(node, clientExtends) || node.HasProperty("Attribute"))
                return node;
            foreach (GCNode child in node.EnumerateChildrenInOrder())
            {
                if (AuthoredExtends(child, clientExtends) || child.HasProperty("Attribute"))
                    return child;
            }
            return null;
        }

        private static GCNode FindEffectChildByExtends(GCNode node, string clientExtends)
        {
            if (node == null)
                return null;
            if (AuthoredExtends(node, clientExtends))
                return node;
            foreach (GCNode child in node.EnumerateChildrenInOrder())
            {
                if (AuthoredExtends(child, clientExtends))
                    return child;
            }
            return null;
        }

        private static bool AuthoredExtends(GCNode node, string clientExtends)
        {
            if (node == null || string.IsNullOrWhiteSpace(clientExtends))
                return false;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (node != null && !string.IsNullOrWhiteSpace(node.Extends))
            {
                string ext = node.Extends;
                if (string.Equals(ext, clientExtends, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (!visited.Add(ext))
                    return false;
                node = GCDatabase.Instance?.Resolve(ext);
            }
            return false;
        }

        private static ushort ComputeSpellModDurationTicks(int durationF32)
        {
            if (durationF32 <= 0)
                return 0;
            long ticks = ((long)durationF32 * CLIENT_TICKS_PER_SECOND + 0x100L) >> 8;
            if (ticks <= 0) return 0;
            if (ticks > ushort.MaxValue) return ushort.MaxValue;
            return (ushort)ticks;
        }

        private static ushort ComputeEffectModFrequencyTicks(int frequencyF32)
        {
            if (frequencyF32 <= 0)
                return 0;
            long ticks = ((long)frequencyF32 * CLIENT_TICKS_PER_SECOND) >> 8;
            if (ticks <= 0) return 0;
            if (ticks > ushort.MaxValue) return ushort.MaxValue;
            return (ushort)ticks;
        }

        private static int ComputeEffectModApplyTickBudget(ushort durationTicks, ushort frequencyTicks)
        {
            int period = Math.Max(1, (int)frequencyTicks);
            if (durationTicks == 0)
                return int.MaxValue;
            if (durationTicks <= period)
                return 0;
            return (durationTicks - 1) / period;
        }

        private IEnumerable<Monster> SelectMonsters(Monster onlyMonster)
        {
            if (onlyMonster != null)
            {
                yield return onlyMonster;
                yield break;
            }

            foreach (var monster in GetAllMonsters())
                yield return monster;
        }

        private uint PeekMonsterHPWireForTrace(Monster monster)
        {
            return monster?.CurrentHPWire ?? 0;
        }

        private void TraceMonsterState(Monster monster, string phase, CombatTarget target = null, int distFixed = -1, int rangeFixed = -1, string reason = null)
        {
            if (monster == null) return;

            ApplyEncounterMirror(monster);
            uint hp = PeekMonsterHPWireForTrace(monster);
            string targetText = target != null ? $"{target.Name}#{target.EntityId}" : (monster.TargetId != 0 ? monster.TargetId.ToString() : "none");
            string signature = $"{monster.State}|{monster.IsAlive}|{monster.AggroTriggered}|{monster.TargetId}|{monster.AlertSourceEntityId}|{monster.AttackPending}|{monster.AttackClientVisible}|{monster.AttackContactOnly}|{monster.AttackHitResolved}|{monster.UsePrimaryActiveSkillThisAttack}|{monster.StockUnitState}|{hp}|{monster.CurrentManaWire}|{monster.PosFixedX}|{monster.PosFixedY}|{monster.CombatContactTargetId}|{monster.AttackCommitTick}|{monster.AttackEndTick}|{monster.AttackWeaponCycleFromUseTargetInterrupt}|{monster.ActiveSkillEffectPending}|{monster.ActiveSkillEffectResolved}|{monster.ActiveSkillEffectTargetEntityId}|{monster.ActiveSkillEffectStartTick}|{monster.ActiveSkillEffectCommitTick}|{monster.ActiveSkillEffectEndTick}|{monster.PrimaryActiveSkillCooldownRemainingTicks}";
            if (_monsterStateTraceSignatures.TryGetValue(monster.EntityId, out var previous) && previous == signature)
                return;

            _monsterStateTraceSignatures[monster.EntityId] = signature;
            string distText = distFixed >= 0 ? distFixed.ToString() : "n/a";
            string rangeText = rangeFixed >= 0 ? rangeFixed.ToString() : "n/a";
            RoomRuntime runtime = GetRoomRuntimeForMonster(monster);
            string instance = runtime?.InstanceKey ?? monster.InstanceKey ?? "<missing>";
            uint seed = runtime?.Seed ?? 0u;
            int rngPos = runtime?.RngCallsSinceReseed ?? -1;
            if (VerboseMonsterDiag) Debug.LogError($"[MON-STATE] phase={phase ?? "unknown"} monster={monster.Name}#{monster.EntityId} behavior={monster.BehaviorId} unit={monster.UnitId} gc='{monster.GCType}' spawnGc='{monster.SpawnGCType}' zone='{monster.ZoneName}' instance='{instance}' state={monster.State} clientState={monster.Ai?.StateId ?? 0} clientMessage={monster.Ai?.LastMessageId ?? 0} encounterState={monster.EncounterObjectState} encounterLive={monster.EncounterLiveUnitCount} encounterReturning={monster.EncounterReturningUnitCount} alive={monster.IsAlive} aggro={monster.AggroTriggered} target={targetText} alertSource={monster.AlertSourceEntityId} deathLifecycle={monster.DeathLifecycleActive} stockState={monster.StockUnitState} hp={hp}/{monster.MaxHPWire} mana={monster.CurrentManaWire}/{monster.MaxManaWire} posFixed8=({monster.PosFixedX},{monster.PosFixedY},{monster.PosFixedZ}) distFixed8={distText} rangeFixed8={rangeText} pending={monster.AttackPending} clientVisible={monster.AttackClientVisible} clientContactOnly={monster.AttackContactOnly} hitResolved={monster.AttackHitResolved} session={monster.AttackSessionId} commitTick={monster.AttackCommitTick} endTick={monster.AttackEndTick} weaponInterrupt={monster.AttackWeaponCycleFromUseTargetInterrupt} skillEffect={monster.ActiveSkillEffectPending}/{monster.ActiveSkillEffectResolved}/{monster.ActiveSkillEffectTargetEntityId}/{monster.ActiveSkillEffectStartTick}/{monster.ActiveSkillEffectCommitTick}/{monster.ActiveSkillEffectEndTick} contactTarget={monster.CombatContactTargetId} contactUntilTick={monster.CombatContactUntilTick} skill={monster.PrimaryActiveSkillPath ?? "none"} useSkill={monster.UsePrimaryActiveSkillThisAttack} skillCd={monster.PrimaryActiveSkillCooldownRemainingTicks}/{monster.PrimaryActiveSkillCooldownTicks} rngSeed=0x{seed:X8} rngPos={rngPos} reason={reason ?? "state-change"}");
            TraceDesyncForensics(monster, phase, reason, target);
        }

        public void TickMonsterProjectileSubEntity(long sequence, string instanceKey, uint simulationTick)
        {
            int projectileIndex = _pendingMonsterProjectiles.FindIndex(pending => pending != null && pending.Sequence == sequence);
            if (projectileIndex < 0)
            {
                RemoveClientSubEntity(ClientSubEntityKind.MonsterProjectile, sequence, instanceKey);
                return;
            }

            PendingMonsterProjectileImpact pending = _pendingMonsterProjectiles[projectileIndex];
            int nowTick = (int)simulationTick;
            if (pending.ActiveSkillProjectile)
            {
                TickMonsterActiveSkillProjectile(pending, projectileIndex, nowTick);
                return;
            }
            if (pending.DueTick > nowTick) return;
            _pendingMonsterProjectiles.RemoveAt(projectileIndex);
            RemoveClientSubEntity(ClientSubEntityKind.MonsterProjectile, sequence, pending.InstanceKey);

            Monster monster = pending.Monster;
            CombatTarget target = GetPlayer(pending.TargetEntityId);
            if (monster == null
                || monster.EntityId != pending.MonsterEntityId
                || target == null
                || !target.IsAlive
                || !target.HasUnitState
                || !string.Equals(RoomRuntime.NormalizeInstanceKey(target.InstanceKey), pending.InstanceKey, StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogError($"[MON-PROJECTILE] impact-discard seq={pending.Sequence} monster={pending.MonsterEntityId} target={pending.TargetEntityId} marker={pending.Marker} source={pending.Source} dueTick={pending.DueTick} nowTick={nowTick} reason=actor-invalid");
                return;
            }

            Debug.LogError($"[MON-PROJECTILE] {monster.Name}#{monster.EntityId}->{target.Name} impact seq={pending.Sequence} marker={pending.Marker} source={pending.Source} fireTick={pending.FireTick} flightTicks={pending.FlightTicks} dueTick={pending.DueTick} nowTick={nowTick} sourceFunction=Projectile::doImpact@0x00594430");
            ResolveMonsterAttackDamage(monster, target, pending.DistF32, pending.Marker, $"{pending.Source}-projectile-impact");
        }

        private bool ProcessProximityAggro(uint playerEntityId = 0, Monster onlyMonster = null, bool queueFollowBehaviorInput = true)
        {
            if (_players.Count == 0) return false;
            bool acquired = false;
            var pathMaps = new Dictionary<string, PathMap>(StringComparer.OrdinalIgnoreCase);
            foreach (var monster in SelectMonsters(onlyMonster))
            {
                if (!monster.IsAlive || monster.AggroTriggered || monster.TargetId != 0) continue;
                PathMap pathMap = null;
                string pathMapKey = ResolveMonsterPathMapKey(monster);
                if (!string.IsNullOrWhiteSpace(pathMapKey))
                {
                    if (!pathMaps.TryGetValue(pathMapKey, out pathMap))
                    {
                        pathMap = PathMapCatalog.Instance.GetPathMap(pathMapKey);
                        pathMaps[pathMapKey] = pathMap;
                    }
                }
                if (!TryPeekMonsterClientVisiblePositionFixed(monster, 0, out int monsterFixedX, out int monsterFixedY, out int monsterFixedZ))
                    continue;
                int rangeFixed = ResolveMonsterAggroAdmissionRangeFixed(monster);
                if (rangeFixed <= 0) continue;
                long rangeSqFixed8 = ((long)rangeFixed * rangeFixed) >> 8;

                CombatPlayer nearest = null;
                long nearestSqFixed8 = long.MaxValue;
                long nearestSqFixed16 = long.MaxValue;
                long closestAnySqFixed8 = long.MaxValue;
                long closestAnySqFixed16 = long.MaxValue;
                bool closestAnyReach = true;
                PathReachability closestAnyReachability = PathReachability.Reachable;
                foreach (var player in GetPlayersInEntityOrder())
                {
                    if (playerEntityId != 0 && player.EntityId != playerEntityId) continue;
                    if (player == null || !player.IsAlive || player.PlayerState == null) continue;
                    if (!MatchesInstance(monster, player.InstanceKey)) continue;
                    if (player.PlayerState.CurrentHPWire == 0 && player.PlayerState.EntitySynchInfoHP == 0) continue;
                    long dxFixed = (long)player.PosFixedX - monsterFixedX;
                    long dyFixed = (long)player.PosFixedY - monsterFixedY;
                    long dzFixed = (long)player.PosFixedZ - monsterFixedZ;
                    long distSqFixed8 = ((dxFixed * dxFixed) >> 8)
                        + ((dyFixed * dyFixed) >> 8)
                        + ((dzFixed * dzFixed) >> 8);
                    long distSqFixed16 = dxFixed * dxFixed + dyFixed * dyFixed + dzFixed * dzFixed;
                    PathReachability reachability = PathReachability.Reachable;
                    if (pathMap != null)
                        reachability = pathMap.GetReachabilityFixed(monsterFixedX, monsterFixedY, player.PosFixedX, player.PosFixedY);
                    bool reach = reachability == PathReachability.Reachable;
                    if (distSqFixed8 < closestAnySqFixed8)
                    {
                        closestAnySqFixed8 = distSqFixed8;
                        closestAnySqFixed16 = distSqFixed16;
                        closestAnyReach = reach;
                        closestAnyReachability = reachability;
                    }
                    if (distSqFixed8 > rangeSqFixed8 || distSqFixed8 >= nearestSqFixed8) continue;
                    if (!reach) continue;
                    nearest = player;
                    nearestSqFixed8 = distSqFixed8;
                    nearestSqFixed16 = distSqFixed16;
                }

                if (nearest != null)
                {
                    long clientDxFixed = (long)nearest.ClientSimulationPosFixedX - monsterFixedX;
                    long clientDyFixed = (long)nearest.ClientSimulationPosFixedY - monsterFixedY;
                    long clientDzFixed = (long)nearest.ClientSimulationPosFixedZ - monsterFixedZ;
                    long clientDistSqFixed8 = ((clientDxFixed * clientDxFixed) >> 8)
                        + ((clientDyFixed * clientDyFixed) >> 8)
                        + ((clientDzFixed * clientDzFixed) >> 8);
                    PathReachability clientReachability = pathMap != null
                        ? pathMap.GetReachabilityFixed(monsterFixedX, monsterFixedY, nearest.ClientSimulationPosFixedX, nearest.ClientSimulationPosFixedY)
                        : PathReachability.Reachable;
                    bool clientTargetScanMatched = clientDistSqFixed8 <= rangeSqFixed8
                        && clientReachability == PathReachability.Reachable;
                    ApplyMonsterWanderClientVisiblePosition(monster, "proximity-acquire");
                    monster.PosFixedX = monsterFixedX;
                    monster.PosFixedY = monsterFixedY;
                    monster.PosFixedZ = monsterFixedZ;
                    bool monsterAcquired = AggroMonster(monster, nearest, "proximity", false, queueFollowBehaviorInput);
                    if (monsterAcquired)
                    {
                        monster.ProximityTargetScanMatched = clientTargetScanMatched;
                        monster.BehaviorTargetUsesClientSimulationPosition = clientTargetScanMatched;
                    }
                    Debug.LogError($"[AGGRO-OBSERVE] proximity admit {monster.Name}#{monster.EntityId}->{nearest.Name} distFixed={UnitMover.IntSqrt(nearestSqFixed16)} aggroFixed={rangeFixed} monsterFixed=({monsterFixedX},{monsterFixedY},{monsterFixedZ}) playerFixed=({nearest.PosFixedX},{nearest.PosFixedY},{nearest.PosFixedZ}) clientSimulationFixed=({nearest.ClientSimulationPosFixedX},{nearest.ClientSimulationPosFixedY},{nearest.ClientSimulationPosFixedZ}) clientScan={clientTargetScanMatched} clientReachability={clientReachability} targetSearchF32={ResolveMonsterTargetSearchRangeFixed(monster)} sourceFunction=MonsterBehavior2::UpdateTargets@0x0051CB50->UnitFinder2::findEnemies@0x00510F50");
                    acquired |= monsterAcquired;
                }
                else if (closestAnySqFixed8 < long.MaxValue)
                {
                    bool inRange = closestAnySqFixed8 <= rangeSqFixed8;
                    string verdict = !inRange
                        ? "OUT-OF-RANGE:position-divergence"
                        : (!closestAnyReach ? $"IN-RANGE-NO-PATH:{closestAnyReachability}" : "IN-RANGE-NO-ADMIT:gate");
                    if (VerboseMonsterDiag) Debug.LogError($"[AGGRO-DETAIL] no-admit {monster.Name}#{monster.EntityId} monsterPos=({FormatFixed8Diagnostic(monsterFixedX)},{FormatFixed8Diagnostic(monsterFixedY)},{FormatFixed8Diagnostic(monsterFixedZ)}) closestPlayerDistFixed={UnitMover.IntSqrt(closestAnySqFixed16)} aggroFixed={rangeFixed} inRange={inRange} reach={closestAnyReach} reachability={closestAnyReachability} pathMap={(pathMap != null)} verdict={verdict} sourceFunction=BasicScan@0x511740");
                }
            }
            return acquired;
        }

        private static int ResolveMonsterAggroAdmissionRangeFixed(Monster monster)
        {
            if (monster == null) return 0;
            int rangeFixed = HasEncounterObjectActiveSearch(monster)
                ? monster.PerceptionRangeF32
                : monster.AggroRangeF32;
            return ResolvePositiveFixed8(rangeFixed);
        }

        private static int ResolveMonsterShoutRangeFixed(Monster monster)
        {
            if (monster == null) return 0;
            return ResolvePositiveFixed8(monster.ShoutRangeF32);
        }

        private static int ResolveMonsterLeashRangeFixed(Monster monster)
        {
            if (monster == null) return 0;
            return ResolvePositiveFixed8(monster.LeashRangeF32);
        }

        private static int ResolveMonsterTargetSearchRangeFixed(Monster monster)
        {
            return ResolveMonsterAggroAdmissionRangeFixed(monster);
        }

        private static void CompleteMonsterMoveToPoint(Monster monster, byte owner)
        {
            if (monster == null)
                return;
            if (owner == MONSTER_MOVE_TO_POINT_SEARCH)
            {
                ClearMonsterAttackSearchMove(monster);
                monster.State = MonsterState.Combat;
            }
            else if (owner == MONSTER_MOVE_TO_POINT_FOLLOW_REPOSITION)
            {
                ClearMonsterFollowRepositionMove(monster);
            }
            else if (owner == MONSTER_MOVE_TO_POINT_FOLLOW_LINE_OF_SIGHT)
            {
                ClearMonsterFollowFindLineOfSightMove(monster);
            }
            else if (owner == MONSTER_MOVE_TO_POINT_RETURN)
                StopMonsterReturnMove(monster);
        }

        private bool TryResolveMonsterMoveToPointSteering(
            Monster monster,
            PathMap pathMap,
            int speedPerFrameFixed,
            int steeringArrivalDistanceFixed,
            ref int curFixedX,
            ref int curFixedY,
            out int targetFixedX,
            out int targetFixedY,
            out bool hold,
            out bool complete,
            out bool completeAfterResidualMove,
            out int residualFixedX,
            out int residualFixedY,
            out bool nearWaypoint)
        {
            targetFixedX = monster?.UnitMoverPathTargetFixedX ?? 0;
            targetFixedY = monster?.UnitMoverPathTargetFixedY ?? 0;
            hold = false;
            complete = false;
            completeAfterResidualMove = false;
            residualFixedX = 0;
            residualFixedY = 0;
            nearWaypoint = false;
            if (monster == null || monster.UnitMoverPathOwner == 0)
                return false;

            if (monster.UnitMoverPathRequestId >= 0)
            {
                hold = true;
                return true;
            }

            while (monster.UnitMoverPathIndex < monster.UnitMoverPathFixed.Count)
            {
                (int waypointFixedX, int waypointFixedY) = monster.UnitMoverPathFixed[monster.UnitMoverPathIndex];
                long waypointDeltaX = unchecked(waypointFixedX - curFixedX);
                long waypointDeltaY = unchecked(waypointFixedY - curFixedY);
                int waypointSquared = unchecked((int)((waypointDeltaX * waypointDeltaX >> 8) + (waypointDeltaY * waypointDeltaY >> 8)));
                if ((waypointFixedX != curFixedX || waypointFixedY != curFixedY) && waypointSquared != 0)
                    break;
                monster.UnitMoverPathIndex++;
                monster.UnitMoverPathRetryCountdown = 0;
            }

            if (monster.UnitMoverPathIndex >= monster.UnitMoverPathFixed.Count)
            {
                complete = true;
                return true;
            }

            (targetFixedX, targetFixedY) = monster.UnitMoverPathFixed[monster.UnitMoverPathIndex];
            bool currentReachable = pathMap != null
                && pathMap.TryCanReachPointFixed(curFixedX, curFixedY, targetFixedX, targetFixedY, out bool currentReach)
                && currentReach;
            if (!currentReachable)
            {
                if (monster.UnitMoverPathRetryCountdown == 0)
                {
                    monster.UnitMoverPathRetryCountdown = 15;
                }
                else
                {
                    monster.UnitMoverPathRetryCountdown--;
                    if (monster.UnitMoverPathRetryCountdown == 0)
                    {
                        byte owner = monster.UnitMoverPathOwner;
                        int finalTargetFixedX = monster.UnitMoverPathTargetFixedX;
                        int finalTargetFixedY = monster.UnitMoverPathTargetFixedY;
                        BeginMonsterMoveToPoint(monster, owner, finalTargetFixedX, finalTargetFixedY);
                        hold = true;
                        return true;
                    }
                }
            }
            else
                monster.UnitMoverPathRetryCountdown = 0;
            if (monster.UnitMoverPathIndex == monster.UnitMoverPathFixed.Count - 1)
            {
                long terminalDeltaFixedX = (long)targetFixedX - curFixedX;
                long terminalDeltaFixedY = (long)targetFixedY - curFixedY;
                int terminalDistanceSquaredFixed = unchecked((int)((terminalDeltaFixedX * terminalDeltaFixedX >> 8) + (terminalDeltaFixedY * terminalDeltaFixedY >> 8)));
                int speedPerFrameSquaredFixed = unchecked((int)((long)speedPerFrameFixed * speedPerFrameFixed >> 8));
                int steeringArrivalSquaredFixed = unchecked((int)((long)steeringArrivalDistanceFixed * steeringArrivalDistanceFixed >> 8));
                nearWaypoint = terminalDistanceSquaredFixed <= steeringArrivalSquaredFixed;
                if (terminalDistanceSquaredFixed <= speedPerFrameSquaredFixed
                    && terminalDistanceSquaredFixed <= steeringArrivalSquaredFixed)
                {
                    int previousPathIndex = monster.UnitMoverPathIndex;
                    monster.UnitMoverPathIndex++;
                    completeAfterResidualMove = true;
                    residualFixedX = unchecked((int)terminalDeltaFixedX);
                    residualFixedY = unchecked((int)terminalDeltaFixedY);
                    nearWaypoint = false;
                    Debug.LogError($"[MON-MOVETOPOINT-WAYPOINT] entity={monster.EntityId} tick={_combatTick} owner={monster.UnitMoverPathOwner} phase=terminal-residual pathIndex={previousPathIndex}->{monster.UnitMoverPathIndex} pathCount={monster.UnitMoverPathFixed.Count} position=({curFixedX},{curFixedY}) target=({targetFixedX},{targetFixedY}) distanceSquared={terminalDistanceSquaredFixed} speedSquared={speedPerFrameSquaredFixed} steeringSquared={steeringArrivalSquaredFixed} sourceFunction=UnitMover::UpdateSteering@0x00536380");
                }
                return true;
            }
            if (monster.UnitMoverPathIndex < monster.UnitMoverPathFixed.Count - 1)
            {
                (int nextFixedX, int nextFixedY) = monster.UnitMoverPathFixed[monster.UnitMoverPathIndex + 1];
                bool collided = pathMap.CastGroundRayFixed(
                    curFixedX,
                    curFixedY,
                    monster.PosFixedZ,
                    nextFixedX,
                    nextFixedY,
                    out _,
                    out _,
                    out _);
                if (!collided)
                {
                    int previousPathIndex = monster.UnitMoverPathIndex;
                    monster.UnitMoverPathIndex++;
                    Debug.LogError($"[MON-MOVETOPOINT-WAYPOINT] entity={monster.EntityId} tick={_combatTick} owner={monster.UnitMoverPathOwner} phase=lookahead pathIndex={previousPathIndex}->{monster.UnitMoverPathIndex} pathCount={monster.UnitMoverPathFixed.Count} position=({curFixedX},{curFixedY}) next=({nextFixedX},{nextFixedY}) collided=False sourceFunction=UnitMover::UpdateSteering@0x00536380->PathMap::CastGroundRay@0x004C60A0");
                }
                else
                {
                    long intermediateDeltaFixedX = (long)targetFixedX - curFixedX;
                    long intermediateDeltaFixedY = (long)targetFixedY - curFixedY;
                    int intermediateDistanceSquaredFixed = unchecked((int)((intermediateDeltaFixedX * intermediateDeltaFixedX >> 8) + (intermediateDeltaFixedY * intermediateDeltaFixedY >> 8)));
                    int speedPerFrameSquaredFixed = unchecked((int)((long)speedPerFrameFixed * speedPerFrameFixed >> 8));
                    int steeringArrivalSquaredFixed = unchecked((int)((long)steeringArrivalDistanceFixed * steeringArrivalDistanceFixed >> 8));
                    nearWaypoint = intermediateDistanceSquaredFixed <= steeringArrivalSquaredFixed;
                    if (intermediateDistanceSquaredFixed <= speedPerFrameSquaredFixed
                        && intermediateDistanceSquaredFixed <= steeringArrivalSquaredFixed)
                    {
                        int previousPathIndex = monster.UnitMoverPathIndex;
                        monster.UnitMoverPathIndex++;
                        residualFixedX = unchecked((int)intermediateDeltaFixedX);
                        residualFixedY = unchecked((int)intermediateDeltaFixedY);
                        nearWaypoint = false;
                        Debug.LogError($"[MON-MOVETOPOINT-WAYPOINT] entity={monster.EntityId} tick={_combatTick} owner={monster.UnitMoverPathOwner} phase=intermediate-residual pathIndex={previousPathIndex}->{monster.UnitMoverPathIndex} pathCount={monster.UnitMoverPathFixed.Count} position=({curFixedX},{curFixedY}) target=({targetFixedX},{targetFixedY}) distanceSquared={intermediateDistanceSquaredFixed} speedSquared={speedPerFrameSquaredFixed} steeringSquared={steeringArrivalSquaredFixed} sourceFunction=UnitMover::UpdateSteering@0x00536380");
                    }
                }
            }
            return true;
        }

        private void ProcessMonsterMovement(uint playerEntityId, Monster onlyMonster, bool emitPositionChanged)
        {
            foreach (var monster in SelectMonsters(onlyMonster))
            {
                if (monster == null || monster.ReturnRemovalPending)
                    continue;
                if (ProcessMonsterFleeMovement(monster, emitPositionChanged))
                    continue;
                bool returnMove = monster.ReturnMoveActive;
                bool useTargetMove = monster?.Behavior?.UseTargetActive == true
                    && monster.SelectedActiveSkillTargetEntityId != 0;
                bool followAction = monster?.Behavior?.FollowActive == true;
                uint movementTargetId = ResolveMonsterActionTargetEntityId(monster);
                if (!monster.IsAlive || (!returnMove && ((!monster.AggroTriggered && !useTargetMove && !followAction) || movementTargetId == 0))) continue;
                bool previousUnitMoverMovingThisFrame = monster.UnitMoverMovingThisFrame;
                monster.UnitMoverMovingThisFrame = false;
                if (!returnMove && playerEntityId != 0 && movementTargetId != playerEntityId) continue;
                TryGetCombatTarget(movementTargetId, out var target);
                if (!returnMove && (target == null
                    || !target.IsAlive
                    || !target.HasUnitState
                    || !MatchesInstance(monster, target.InstanceKey))) continue;
                if (!returnMove && target.PlayerState.CurrentHPWire == 0 && target.PlayerState.EntitySynchInfoHP == 0) continue;
                monster.ClientVisibleMovingThisFrameTick = CombatTick;
                monster.ClientVisibleMovingThisFrame = false;
                if (IsMonsterDamageReactionActive(monster)) continue;
                bool followMove = monster.Behavior?.FollowMovesTowardTarget == true;
                bool searchForAttackMove = monster.SearchForAttackMoveActive;
                bool followRepositionMove = monster.FollowRepositionMoveActive;
                bool followFindLineOfSightMove = monster.FollowFindLineOfSightMoveActive;
                bool moveInDirection = monster.MoveInDirectionHeadingInit && monster.UnitMoverPathOwner == 0;
                if (!returnMove && !followMove && !searchForAttackMove && !followRepositionMove && !followFindLineOfSightMove && !useTargetMove && !moveInDirection) continue;
                if (monster.FollowInputPending) continue;
                if (monster.AttackPending)
                {
                    if (emitPositionChanged)
                        OnMonsterPositionChanged?.Invoke(monster);
                    continue;
                }

                int allowedRangeFixed = useTargetMove
                    ? ResolveMonsterSkillTargetClearRangeF32(monster, monster.SelectedActiveSkill, target)
                    : ResolveMonsterEffectiveAttackRangeFixed(monster);
                bool pointMove = returnMove || searchForAttackMove || followRepositionMove || followFindLineOfSightMove;
                bool directionMove = moveInDirection && !pointMove && !useTargetMove;
                if (!pointMove && !directionMove && allowedRangeFixed <= 0) continue;
                int movementTargetFixedX;
                int movementTargetFixedY;
                if (returnMove)
                {
                    movementTargetFixedX = monster.UnitMoverPathTargetFixedX;
                    movementTargetFixedY = monster.UnitMoverPathTargetFixedY;
                }
                else if (searchForAttackMove)
                {
                    movementTargetFixedX = monster.SearchForAttackTargetFixedX;
                    movementTargetFixedY = monster.SearchForAttackTargetFixedY;
                }
                else if (followRepositionMove)
                {
                    movementTargetFixedX = monster.FollowRepositionTargetFixedX;
                    movementTargetFixedY = monster.FollowRepositionTargetFixedY;
                }
                else if (followFindLineOfSightMove)
                {
                    movementTargetFixedX = monster.FollowFindLineOfSightTargetFixedX;
                    movementTargetFixedY = monster.FollowFindLineOfSightTargetFixedY;
                }
                else
                {
                    ResolveMonsterFollowTargetStateFixed(target, out movementTargetFixedX, out movementTargetFixedY, out _, out _);
                }

                TryGetMonsterClientVisiblePositionFixed(monster, 0, out int behaviorMonsterFixedX, out int behaviorMonsterFixedY);
                long bdxFixed = (long)movementTargetFixedX - behaviorMonsterFixedX;
                long bdyFixed = (long)movementTargetFixedY - behaviorMonsterFixedY;
                int distFixed = UnitMover.IntSqrt(bdxFixed * bdxFixed + bdyFixed * bdyFixed);
                int contactThresholdFixed = allowedRangeFixed + CLIENT_CONTACT_RANGE_EPSILON_FIXED;
                bool movementTargetReached = useTargetMove
                    ? EvaluateMonsterActiveSkillTargetClearFixed(monster, target, allowedRangeFixed)
                    : distFixed <= contactThresholdFixed || distFixed <= 0;
                if (!pointMove && !directionMove && movementTargetReached)
                {
                    if (monster.State == MonsterState.Chase)
                        monster.State = MonsterState.Combat;
                    RefreshMonsterCombatContact(monster, target);
                    TraceMonsterState(monster, "movement", target, distFixed, allowedRangeFixed, "contact");
                    continue;
                }

                int speedFixed = ResolveMonsterMovementSpeedFixed(monster);
                if (speedFixed <= 0)
                {
                    monster.State = MonsterState.Chase;
                    TraceMonsterState(monster, "movement", target, distFixed, allowedRangeFixed, "no-speed");
                    continue;
                }

                int curFixedX = monster.PosFixedX;
                int curFixedY = monster.PosFixedY;
                int curFixedZ = monster.PosFixedZ;
                int movementStartFixedX = curFixedX;
                int movementStartFixedY = curFixedY;
                int targetFixedX = movementTargetFixedX;
                int targetFixedY = movementTargetFixedY;
                int rangeFixed = pointMove || directionMove ? 0 : allowedRangeFixed;
                int stepFixed = (int)(((long)speedFixed << 8) / 0x1e00);
                if (stepFixed < 1) stepFixed = 1;
                int steeringArrivalDistanceFixed = UnitMover.CacheSteeringArrivalDistance(speedFixed, monster.TurnRateDegrees << 8);
                var chasePm = PathMapCatalog.Instance.GetPathMap(!string.IsNullOrWhiteSpace(monster.InstanceKey) ? monster.InstanceKey : monster.ZoneName);
                byte pointMoveOwner = monster.UnitMoverPathOwner;
                bool completePointMoveAfterResidual = false;
                int pointResidualFixedX = 0;
                int pointResidualFixedY = 0;
                bool pointNearWaypoint = false;
                if (pointMove
                    && TryResolveMonsterMoveToPointSteering(
                        monster,
                        chasePm,
                        stepFixed,
                        steeringArrivalDistanceFixed,
                        ref curFixedX,
                        ref curFixedY,
                        out targetFixedX,
                        out targetFixedY,
                        out bool holdPointMove,
                        out bool completePointMove,
                        out completePointMoveAfterResidual,
                        out pointResidualFixedX,
                        out pointResidualFixedY,
                        out pointNearWaypoint))
                {
                    if (completePointMove)
                    {
                        CompleteMonsterMoveToPoint(monster, pointMoveOwner);
                        TraceMonsterState(monster, "movement", target, distFixed, allowedRangeFixed, "path-complete");
                        continue;
                    }
                    if (holdPointMove)
                    {
                        TraceMonsterState(monster, "movement", target, distFixed, allowedRangeFixed, "path-pending");
                        continue;
                    }
                }
                int chaseTurnRate = UnitMover.TurnRatePerTickFixed(monster.TurnRateDegrees);
                if (!monster.ChaseHeadingInit)
                {
                    monster.ChaseHeadingFixed = monster.HeadingFixed;
                    monster.ChaseHeadingInit = true;
                }
                int chaseTicks = 1;
                bool chaseArrived = false;
                for (int t = 0; t < chaseTicks && !chaseArrived; t++)
                {
                    if (pointMove)
                    {
                        int desiredHeading = completePointMoveAfterResidual
                            ? monster.ChaseHeadingFixed
                            : UnitMover.VectorToHeadingFixed(unchecked(targetFixedX - curFixedX), unchecked(targetFixedY - curFixedY));
                        monster.UnitMoverDesiredHeadingFixed = desiredHeading;
                        monster.UnitMoverDesiredHeadingInit = true;
                        int nextFixedX = curFixedX;
                        int nextFixedY = curFixedY;
                        int nextHeading = monster.ChaseHeadingFixed;
                        bool nextMovingThisFrame = false;
                        if (!completePointMoveAfterResidual)
                            UnitMover.StepInDirectionFixedHeading(curFixedX, curFixedY, monster.ChaseHeadingFixed, desiredHeading,
                                stepFixed, chaseTurnRate, pointNearWaypoint || monster.TurnBeforeMoving,
                                !pointNearWaypoint && previousUnitMoverMovingThisFrame,
                                out nextFixedX, out nextFixedY, out nextHeading, out nextMovingThisFrame);
                        nextFixedX = unchecked(nextFixedX + pointResidualFixedX);
                        nextFixedY = unchecked(nextFixedY + pointResidualFixedY);
                        ResolveMonsterMovementFixed(monster, chasePm, curFixedX, curFixedY, curFixedZ, nextFixedX, nextFixedY,
                            out nextFixedX, out nextFixedY, out int nextFixedZ, _combatTick);
                        curFixedX = nextFixedX;
                        curFixedY = nextFixedY;
                        curFixedZ = nextFixedZ;
                        monster.ChaseHeadingFixed = nextHeading;
                        monster.UnitMoverMovingThisFrame = nextMovingThisFrame;
                    }
                    else if (directionMove)
                    {
                        if (!monster.MoveInDirectionHeadingInit)
                            break;
                        monster.UnitMoverDesiredHeadingFixed = monster.MoveInDirectionHeadingFixed;
                        monster.UnitMoverDesiredHeadingInit = true;
                        UnitMover.StepInDirectionFixedHeading(curFixedX, curFixedY, monster.ChaseHeadingFixed, monster.MoveInDirectionHeadingFixed, stepFixed, chaseTurnRate, monster.TurnBeforeMoving, previousUnitMoverMovingThisFrame, out int nextFixedX, out int nextFixedY, out int nextHeading, out bool nextMovingThisFrame);
                        ResolveMonsterMovementFixed(monster, chasePm, curFixedX, curFixedY, curFixedZ, nextFixedX, nextFixedY, out nextFixedX, out nextFixedY, out int nextFixedZ, _combatTick);
                        curFixedX = nextFixedX;
                        curFixedY = nextFixedY;
                        curFixedZ = nextFixedZ;
                        monster.ChaseHeadingFixed = nextHeading;
                        monster.UnitMoverMovingThisFrame = nextMovingThisFrame;
                    }
                    else
                    {
                        long rdx = (long)targetFixedX - curFixedX;
                        long rdy = (long)targetFixedY - curFixedY;
                        if (UnitMover.IntSqrt(rdx * rdx + rdy * rdy) <= rangeFixed)
                        {
                            chaseArrived = true;
                            break;
                        }
                        monster.UnitMoverDesiredHeadingFixed = UnitMover.VectorToHeadingFixed((int)rdx, (int)rdy);
                        monster.UnitMoverDesiredHeadingInit = true;
                        UnitMover.StepTowardFixedHeading(curFixedX, curFixedY, monster.ChaseHeadingFixed, targetFixedX, targetFixedY, stepFixed, chaseTurnRate, out int nextFixedX, out int nextFixedY, out int nextHeading, out chaseArrived);
                        ResolveMonsterMovementFixed(monster, chasePm, curFixedX, curFixedY, curFixedZ, nextFixedX, nextFixedY, out nextFixedX, out nextFixedY, out int nextFixedZ, _combatTick);
                        monster.UnitMoverMovingThisFrame = nextFixedX != curFixedX || nextFixedY != curFixedY;
                        curFixedX = nextFixedX;
                        curFixedY = nextFixedY;
                        curFixedZ = nextFixedZ;
                        monster.ChaseHeadingFixed = nextHeading;
                    }
                }
                monster.ClientVisibleMovingThisFrame = curFixedX != movementStartFixedX || curFixedY != movementStartFixedY;
                monster.PosFixedX = curFixedX;
                monster.PosFixedY = curFixedY;
                monster.PosFixedZ = chasePm != null && chasePm.TryGetHeightAtFixed(curFixedX, curFixedY, out _)
                    ? curFixedZ
                    : ResolveTerrainHeightFixedValue(monster, curFixedX, curFixedY, curFixedZ);
                monster.HeadingFixed = monster.ChaseHeadingFixed;
                monster.State = MonsterState.Chase;
                if (completePointMoveAfterResidual)
                {
                    CompleteMonsterMoveToPoint(monster, pointMoveOwner);
                    monster.UnitMoverMovingThisFrame = false;
                    TraceMonsterState(monster, "movement", target, distFixed, allowedRangeFixed, "path-terminal-residual");
                }

                long chaseRdx = (long)targetFixedX - curFixedX;
                long chaseRdy = (long)targetFixedY - curFixedY;
                int remainingFixed = UnitMover.IntSqrt(chaseRdx * chaseRdx + chaseRdy * chaseRdy);
                int contactRangeFixed = rangeFixed + CLIENT_CONTACT_RANGE_EPSILON_FIXED;
                if (!pointMove && !directionMove && remainingFixed <= contactRangeFixed)
                {
                    monster.State = MonsterState.Combat;
                    RefreshMonsterCombatContact(monster, target);
                }
                if (emitPositionChanged)
                    OnMonsterPositionChanged?.Invoke(monster);
                TraceMonsterState(monster, "movement", target, remainingFixed, allowedRangeFixed, "move");
            }
        }

        private static int ResolveMonsterMovementSpeedFixed(Monster monster)
        {
            if (monster == null) return 0;
            int speedF32 = monster.MoveSpeedF32 > 0
                ? monster.MoveSpeedF32
                : monster.WalkSpeedF32 > 0 ? monster.WalkSpeedF32 : 0;
            UnitMover.CacheSpeedPerFrame(speedF32, monster.SpeedMod + monster.GetActiveAttributeModifierValue("SPEEDMOD"), out int effectiveSpeedF32);
            return effectiveSpeedF32;
        }

        public int GetMonsterMovementSpeedFixed(Monster monster)
        {
            return ResolveMonsterMovementSpeedFixed(monster);
        }

        public void AdvanceMonsterActiveSkillsForEntity(uint entityId)
        {
            if (!_activeMonsters.TryGetValue(entityId, out Monster monster) || monster == null)
                return;
            AdvanceMonsterSkillCooldown(monster, monster.PrimaryAttackSkill);
            if (monster.ActiveSkills != null)
                for (int skillIndex = 0; skillIndex < monster.ActiveSkills.Count; skillIndex++)
                    AdvanceMonsterSkillCooldown(monster, monster.ActiveSkills[skillIndex]);
            if (monster.SelectedActiveSkill != null)
                ApplyMonsterActiveSkillSelection(monster, monster.SelectedActiveSkill);
            ProcessMonsterActiveSkillEffect(monster);
            if (IsMonsterActiveSkillOnlyCycle(monster)
                && (int)_combatTick >= monster.ActiveSkillEffectEndTick)
                CompleteMonsterActiveSkillOnlyCycle(monster);
        }

        private void AdvanceMonsterSkillCooldown(Monster monster, MonsterActiveSkillRuntime skill)
        {
            if (monster == null || skill == null)
                return;
            if (skill.CooldownRemainingTicks == 0)
            {
                skill.CooldownLastTick = CombatTick;
                return;
            }
            if (skill.CooldownLastTick == CombatTick)
                return;
            skill.CooldownLastTick = CombatTick;
            ushort oldTicks = skill.CooldownRemainingTicks;
            skill.CooldownRemainingTicks = (ushort)(oldTicks - 1);
            if (oldTicks != skill.CooldownRemainingTicks)
                Debug.LogError($"[MON-SKILL-CD] advance {monster.Name}#{monster.EntityId} skill={skill.Path ?? "none"} ticks={oldTicks}->{skill.CooldownRemainingTicks}");
        }

        private const int ClientActiveRangeFixed = 700 * 0x100;

        private bool IsMonsterWithinClientActiveRange(Monster monster)
        {
            if (monster == null || _players.Count == 0)
                return false;
            TryGetMonsterWanderClientVisiblePositionFixed(monster, out int monsterFixedX, out int monsterFixedY);
            long rangeSq = (long)ClientActiveRangeFixed * ClientActiveRangeFixed;
            foreach (var player in GetPlayersInEntityOrder())
            {
                if (player == null || !player.IsAlive || player.PlayerState == null)
                    continue;
                long dx = (long)player.PosFixedX - monsterFixedX;
                long dy = (long)player.PosFixedY - monsterFixedY;
                if (dx * dx + dy * dy <= rangeSq)
                    return true;
            }
            return false;
        }

        public bool IsMonsterWithinClientActiveRangeForEntity(uint entityId)
        {
            return _activeMonsters.TryGetValue(entityId, out var monster) && IsMonsterWithinClientActiveRange(monster);
        }

        private Behavior.MonsterBehavior2.ActionSlot TickMonsterUpdateSkillsTimer(Monster monster)
        {
            if (monster == null || !monster.IsAlive)
                return Behavior.MonsterBehavior2.ActionSlot.None;
            if (monster.ActiveSkills == null || monster.ActiveSkills.Count == 0)
                return Behavior.MonsterBehavior2.ActionSlot.None;
            if (monster.SilenceAttributeValue > 0)
                return Behavior.MonsterBehavior2.ActionSlot.None;

            var skillRng = GetRoomRngForMonster(monster);
            if (skillRng == null)
                return Behavior.MonsterBehavior2.ActionSlot.None;

            bool idleState = monster.Behavior?.IsIdleState == true;
            if (idleState)
            {
                int rngBefore = skillRng.CallsSinceReseed;
                uint gateRaw = RngLedger.Generate(skillRng, "room", "MonsterBehavior2::UpdateSkills:idle-gate", $"{monster.Name}#{monster.EntityId}");
                uint gateRoll = gateRaw % 100u;
                Debug.LogError($"[MON-SKILL] idle-gate {monster.Name}#{monster.EntityId} gateRaw=0x{gateRaw:X8} roll={gateRoll} pos={rngBefore}->{skillRng.CallsSinceReseed} sourceFunction=MonsterBehavior2::UpdateSkills@0x0051CDE0");
                if (gateRoll > 0x1e)
                    return Behavior.MonsterBehavior2.ActionSlot.None;
            }

            int selfHealthRatioF32 = ResolveHealthRatioF32(monster.CurrentHPWire, monster.MaxHPWire);
            for (int skillIndex = 0; skillIndex < monster.ActiveSkills.Count; skillIndex++)
            {
                MonsterActiveSkillRuntime skill = monster.ActiveSkills[skillIndex];
                if (skill == null || ReferenceEquals(skill, monster.PrimaryAttackSkill))
                    continue;
                if (skill.CooldownRemainingTicks != 0 || monster.CurrentManaWire < skill.ManaCostWire)
                    continue;
                int spellUse = ResolveMonsterSpellUse(skill.SpellUse);
                if ((spellUse == 1 && !idleState) || (spellUse == 2 && idleState))
                    continue;
                if (skill.DelayBeforeUseTicks != 0)
                {
                    skill.DelayBeforeUseTicks--;
                    continue;
                }
                if (selfHealthRatioF32 > skill.SelfHealthPctF32)
                    continue;

                int targetType = ResolveMonsterTargetType(skill.TargetType);
                if (targetType == 0)
                {
                    ApplyMonsterActiveSkillSelection(monster, skill);
                    monster.SelectedActiveSkillTargetEntityId = monster.EntityId;
                    monster.UsePrimaryActiveSkillThisAttack = true;
                    monster.ActiveSkillUseCommittedThisAttack = false;
                    return Behavior.MonsterBehavior2.ActionSlot.Use;
                }
                if (targetType == 1)
                {
                    List<(Monster Monster, long DistanceSquaredFixed8)> friendCandidates = CollectMonsterActiveSkillFriendCandidates(monster, skill);
                    if (friendCandidates.Count == 0)
                        continue;
                    uint friendTargetRaw = RngLedger.Generate(skillRng, "room", "MonsterBehavior2::UpdateSkills:target", $"{monster.Name}#{monster.EntityId}", monster.EntityId);
                    int friendSelectedIndex = (int)(friendTargetRaw % (uint)friendCandidates.Count);
                    Monster selectedFriend = friendCandidates[friendSelectedIndex].Monster;
                    ApplyMonsterActiveSkillSelection(monster, skill);
                    monster.SelectedActiveSkillTargetEntityId = selectedFriend.EntityId;
                    monster.UsePrimaryActiveSkillThisAttack = true;
                    monster.ActiveSkillUseCommittedThisAttack = false;
                    Debug.LogError($"[MON-SKILL] select {monster.Name}#{monster.EntityId}->{selectedFriend.Name}#{selectedFriend.EntityId} skill={skill.Path} skillIndex={skillIndex} targetIndex={friendSelectedIndex}/{friendCandidates.Count} targetRaw=0x{friendTargetRaw:X8} targetType=FRIEND scanRangeF32={skill.SpellUseRangeF32} useRangeF32={skill.RangeF32} minRangeF32={skill.SpellUseMinimumRangeF32} cooldown={skill.CooldownRemainingTicks}/{skill.CooldownTicks} mana={monster.CurrentManaWire}/{skill.ManaCostWire} sourceFunction=MonsterBehavior2::UpdateSkills@0x0051CDE0->UseTarget::UseTarget@0x00547E00");
                    return Behavior.MonsterBehavior2.ActionSlot.UseTarget;
                }
                if (targetType == 6)
                {
                    List<(Monster Monster, long DistanceSquaredFixed8)> corpseCandidates = CollectMonsterActiveSkillFriendCorpseCandidates(monster, skill);
                    if (corpseCandidates.Count == 0)
                        continue;
                    uint corpseTargetRaw = RngLedger.Generate(skillRng, "room", "MonsterBehavior2::UpdateSkills:target", $"{monster.Name}#{monster.EntityId}", monster.EntityId);
                    int corpseSelectedIndex = (int)(corpseTargetRaw % (uint)corpseCandidates.Count);
                    Monster selectedCorpse = corpseCandidates[corpseSelectedIndex].Monster;
                    ApplyMonsterActiveSkillSelection(monster, skill);
                    monster.SelectedActiveSkillTargetEntityId = selectedCorpse.EntityId;
                    monster.UsePrimaryActiveSkillThisAttack = true;
                    monster.ActiveSkillUseCommittedThisAttack = false;
                    SkillEffectTracker.RecordMonsterSelection(monster, selectedCorpse, skill, targetType, corpseSelectedIndex, corpseCandidates.Count, corpseTargetRaw, _combatTick);
                    return Behavior.MonsterBehavior2.ActionSlot.UseTarget;
                }
                if (targetType != 2)
                    continue;

                List<(CombatTarget Player, long DistanceSquaredFixed8)> candidates = CollectMonsterActiveSkillEnemyCandidates(monster, skill);
                if (candidates.Count == 0)
                    continue;
                uint targetRaw = RngLedger.Generate(skillRng, "room", "MonsterBehavior2::UpdateSkills:target", $"{monster.Name}#{monster.EntityId}", monster.EntityId);
                int selectedIndex = (int)(targetRaw % (uint)candidates.Count);
                CombatTarget selectedTarget = candidates[selectedIndex].Player;
                ApplyMonsterActiveSkillSelection(monster, skill);
                monster.SelectedActiveSkillTargetEntityId = selectedTarget.EntityId;
                monster.UsePrimaryActiveSkillThisAttack = true;
                monster.ActiveSkillUseCommittedThisAttack = false;
                Debug.LogError($"[MON-SKILL] select {monster.Name}#{monster.EntityId}->{selectedTarget.Name} skill={skill.Path} skillIndex={skillIndex} targetIndex={selectedIndex}/{candidates.Count} targetRaw=0x{targetRaw:X8} scanRangeF32={skill.SpellUseRangeF32} useRangeF32={skill.RangeF32} minRangeF32={skill.SpellUseMinimumRangeF32} cooldown={skill.CooldownRemainingTicks}/{skill.CooldownTicks} mana={monster.CurrentManaWire}/{skill.ManaCostWire} sourceFunction=MonsterBehavior2::UpdateSkills@0x0051CDE0->UseTarget::UseTarget@0x00547E00");
                return Behavior.MonsterBehavior2.ActionSlot.UseTarget;
            }
            return Behavior.MonsterBehavior2.ActionSlot.None;
        }

    }
}
