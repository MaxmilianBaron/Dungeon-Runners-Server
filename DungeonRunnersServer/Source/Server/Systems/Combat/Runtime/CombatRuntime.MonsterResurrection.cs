using System;
using System.Collections.Generic;
using DungeonRunners.Core;
using DungeonRunners.Data;

namespace DungeonRunners.Combat
{
    public partial class CombatRuntime
    {
        private enum MonsterCorpseEffectNodeKind : byte
        {
            Sequence,
            VisualOnly,
            Ressurect
        }

        private sealed class MonsterCorpseEffectNode
        {
            public MonsterCorpseEffectNodeKind Kind;
            public readonly List<MonsterCorpseEffectNode> Children = new List<MonsterCorpseEffectNode>();
            public string EffectPath;
            public int PackageEntryId;
            public int SiblingOrder;
            public int Depth;
        }

        private bool CanExecuteMonsterCorpseActiveSkillEffect(Monster monster, out string reason)
        {
            reason = null;
            MonsterActiveSkillRuntime activeSkill = monster?.SelectedActiveSkill;
            if (activeSkill == null)
            {
                reason = "missing-selected-skill";
                return false;
            }
            if (activeSkill.RepeatCount != 1)
            {
                reason = "ressurect-repeat-count-unproven";
                return false;
            }
            if (!activeSkill.InstantUse && !string.IsNullOrWhiteSpace(activeSkill.CastModifier))
            {
                reason = "ressurect-cast-modifier-unimplemented";
                return false;
            }
            if (!TryBuildMonsterCorpseActiveSkillEffectPlan(monster, out MonsterCorpseEffectNode plan, out reason))
                return false;
            monster.ActiveSkillEffectPlan = plan;
            SkillEffectTracker.RecordValidated("monster", monster.EntityId, activeSkill.Path, monster.SelectedActiveSkillTargetEntityId, "FRIENDCORPSE", _combatTick);
            return true;
        }

        private bool TryBuildMonsterCorpseActiveSkillEffectPlan(Monster monster, out MonsterCorpseEffectNode plan, out string reason)
        {
            plan = null;
            reason = null;
            MonsterActiveSkillRuntime activeSkill = monster?.SelectedActiveSkill;
            if (activeSkill == null || string.IsNullOrWhiteSpace(activeSkill.Path))
            {
                reason = "missing-selected-skill";
                return false;
            }
            GCDatabase gc = GCDatabase.Instance;
            if (gc == null || !gc.IsLoaded)
            {
                reason = "authored-database-unavailable";
                return false;
            }
            GCNode skill = gc.ResolveWithInheritance(activeSkill.Path);
            GCNode skillDescription = skill?.GetChild("Description") ?? skill;
            string effectPath = skillDescription?.GetString("Effect", activeSkill.Effect) ?? activeSkill.Effect;
            GCNode effect = ResolveAuthoredNodeReference(effectPath, skill);
            if (effect == null)
            {
                reason = "missing-authored-effect";
                return false;
            }
            if (!TryBuildMonsterCorpseEffectNode(effect, effectPath, skill, 0, 0, out plan, out reason))
                return false;
            int ressurectCount = CountMonsterCorpseEffectNodes(plan, MonsterCorpseEffectNodeKind.Ressurect);
            if (ressurectCount != 1)
            {
                reason = $"ressurect-effect-count:{ressurectCount}";
                plan = null;
                return false;
            }
            return true;
        }

        private bool TryBuildMonsterCorpseEffectNode(
            GCNode authoredNode,
            string effectPath,
            GCNode skillContext,
            int siblingOrder,
            int depth,
            out MonsterCorpseEffectNode plan,
            out string reason)
        {
            plan = null;
            reason = null;
            if (authoredNode == null)
            {
                reason = "missing-effect-node";
                return false;
            }
            int chanceWire = ResolveSpellEffectChanceWire(authoredNode);
            if (chanceWire != 0x6400)
            {
                reason = "ressurect-effect-chance-unproven";
                return false;
            }
            string nodePath = BuildAuthoredEffectPath(effectPath, authoredNode);
            MonsterCorpseEffectNodeKind kind;
            if (AuthoredExtends(authoredNode, "SpellRessurectEffect"))
                kind = MonsterCorpseEffectNodeKind.Ressurect;
            else if (AuthoredExtends(authoredNode, "SpellSoundEffect") || AuthoredExtends(authoredNode, "SpellEffectEffect"))
                kind = MonsterCorpseEffectNodeKind.VisualOnly;
            else if (AuthoredExtends(authoredNode, "SpellEffect")
                || AuthoredExtends(authoredNode, "SpellSnapToGroundEffect")
                || string.IsNullOrWhiteSpace(authoredNode.Extends))
                kind = MonsterCorpseEffectNodeKind.Sequence;
            else
            {
                reason = $"unsupported-corpse-effect-family:{authoredNode.Extends ?? "unknown"}";
                return false;
            }
            plan = new MonsterCorpseEffectNode
            {
                Kind = kind,
                EffectPath = nodePath,
                PackageEntryId = authoredNode.PackageEntryId,
                SiblingOrder = siblingOrder,
                Depth = depth
            };
            if (kind != MonsterCorpseEffectNodeKind.Sequence)
                return true;
            int childOrder = 0;
            foreach (GCNode child in authoredNode.EnumerateChildrenInOrder())
            {
                if (!TryBuildMonsterCorpseEffectNode(child, nodePath, skillContext, childOrder, depth + 1, out MonsterCorpseEffectNode childPlan, out reason))
                    return false;
                plan.Children.Add(childPlan);
                childOrder++;
            }
            if (kind == MonsterCorpseEffectNodeKind.Sequence && plan.Children.Count == 0)
            {
                reason = "empty-corpse-effect-sequence";
                return false;
            }
            return true;
        }

        private static int CountMonsterCorpseEffectNodes(MonsterCorpseEffectNode plan, MonsterCorpseEffectNodeKind kind)
        {
            if (plan == null)
                return 0;
            int count = plan.Kind == kind ? 1 : 0;
            for (int childIndex = 0; childIndex < plan.Children.Count; childIndex++)
                count += CountMonsterCorpseEffectNodes(plan.Children[childIndex], kind);
            return count;
        }

        private bool TryApplyMonsterCorpseActiveSkillEffect(Monster source, Monster target)
        {
            MonsterCorpseEffectNode plan = source?.ActiveSkillEffectPlan as MonsterCorpseEffectNode;
            if (plan == null || !IsMonsterFriendCorpseTarget(source, target))
                return false;
            return ExecuteMonsterCorpseEffectPlan(plan, source, target);
        }

        private bool ExecuteMonsterCorpseEffectPlan(MonsterCorpseEffectNode plan, Monster source, Monster target)
        {
            if (plan == null || source == null || target == null)
                return false;
            if (plan.Kind == MonsterCorpseEffectNodeKind.Sequence)
            {
                bool handled = true;
                for (int childIndex = 0; childIndex < plan.Children.Count; childIndex++)
                    handled &= ExecuteMonsterCorpseEffectPlan(plan.Children[childIndex], source, target);
                return handled;
            }
            if (plan.Kind == MonsterCorpseEffectNodeKind.VisualOnly)
            {
                SkillEffectTracker.RecordEffectNode("monster", source.EntityId, target.EntityId, source.SelectedActiveSkill?.Path, plan.EffectPath, plan.PackageEntryId, plan.SiblingOrder, plan.Depth, "visual", _combatTick);
                return true;
            }
            SkillEffectTracker.RecordEffectNode("monster", source.EntityId, target.EntityId, source.SelectedActiveSkill?.Path, plan.EffectPath, plan.PackageEntryId, plan.SiblingOrder, plan.Depth, "ressurect", _combatTick);
            return RessurectMonsterFromSpell(source, target, plan.EffectPath);
        }

        private bool RessurectMonsterFromSpell(Monster source, Monster target, string effectPath)
        {
            if (!IsMonsterFriendCorpseTarget(source, target))
                return false;
            MeleeBehaviorContext behaviorContext = BuildMeleeBehaviorContextForMonster(target);
            if (behaviorContext == null)
                return false;
            uint oldFlags = target.UnitFlags;
            uint oldHPWire = target.CurrentHPWire;
            byte oldStockState = target.StockUnitState;
            StopMonsterMoving(target);
            target.ClearTarget();
            target.AggroTriggered = false;
            target.AggroSent = false;
            target.AttackPending = false;
            target.AttackSoundPending = false;
            target.AttackClientVisible = false;
            target.AttackActionQueued = false;
            target.AttackActionAdmissionTick = 0;
            target.AttackContactOnly = false;
            target.AttackHitResolved = false;
            target.AttackStartTick = 0;
            target.AttackCommitTick = 0;
            target.AttackSoundTick = 0;
            target.AttackEndTick = 0;
            target.AttackUseRaw = 0;
            target.UsePrimaryActiveSkillThisAttack = false;
            target.SelectedActiveSkill = null;
            target.SelectedActiveSkillTargetEntityId = 0;
            target.ActiveSkillUseCommittedThisAttack = false;
            ClearMonsterActiveSkillEffectCycle(target);
            ClearMonsterAttackCommitTarget(target);
            target.CombatContactTargetId = 0;
            target.CombatContactUntilTick = 0;
            ClearMonsterDamageReaction(target);
            target.FleeActionActive = false;
            target.MoveInDirectionHeadingInit = false;
            target.UnitMoverMovingThisFrame = false;
            target.ClientVisibleMovingThisFrame = false;
            target.SpeedMod = target.BaseSpeedMod;
            target.UnitFlags = (target.UnitFlags & 0xFFFFE7FFu) | 0x400u;
            target.UnitState31B = 0;
            target.IsAlive = true;
            target.CombatCredit.Clear();
            target.State = MonsterState.Idle;
            target.DeathLifecycleActive = false;
            target.DeathRemoveSent = false;
            target.StockUnitState = 8;
            InitializeMonsterStockUnitLifespan(target);
            target.StockUnitStateTicksRemaining = 0;
            target.OnDeadTicksRemaining = 0;
            target.OnDeadDispatched = false;
            _pendingMonsterFinalRemovals.Remove(target.EntityId);
            _entityUpdateRemovalTicks.Remove(target.EntityId);
            _entityUpdateRemovalOrder.Remove(target.EntityId);
            SetRuntimeMonsterHPWire(target, target.MaxHPWire, false, "SpellRessurectEffect::doEffect");
            _monsterHPRegenLastTick[target.EntityId] = _combatTick;
            _monsterManaRegenLastTick[target.EntityId] = _combatTick;
            if (target.Ai != null)
            {
                target.Ai.TargetEntityId = 0;
                target.Ai.AlertSourceEntityId = 0;
                target.Ai.NextStateId = 0;
                target.Ai.RemovalDone = false;
                target.Ai.LastMessageId = 0;
                target.Ai.SetState(MonsterStateId.IdleSearch, "ressurect");
            }
            EncounterRuntime encounter = target.Encounter;
            if (encounter != null)
            {
                encounter.MarkUnitSpawned(target.EntityId);
                ApplyEncounterGroup(encounter);
            }
            ApplyEncounterMirror(target);
            ResetMonsterClientVisiblePositionFixed(target, target.PosFixedX, target.PosFixedY, "SpellRessurectEffect::doEffect");
            foreach (CombatPlayer player in GetPlayersInEntityOrder())
                if (player != null && MatchesInstance(target, player.InstanceKey))
                    ResetMonsterClientVisiblePositionFixed(target, player.EntityId, target.PosFixedX, target.PosFixedY, "SpellRessurectEffect::doEffect");
            if (target.Behavior == null)
                target.Behavior = new Behavior.MonsterBehavior2();
            target.Behavior.ResetAfterRessurect(behaviorContext);
            OnStockUnitRespawned?.Invoke(target);
            SkillEffectTracker.RecordRessurect(source, target, effectPath, oldHPWire, target.CurrentHPWire, oldFlags, target.UnitFlags, oldStockState, target.StockUnitState, _combatTick);
            return true;
        }
    }
}
