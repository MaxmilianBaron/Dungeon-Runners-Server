using System;
using System.Collections.Generic;
using System.Linq;
using DungeonRunners.Engine;
using DungeonRunners.Networking;

namespace DungeonRunners.Combat
{
    public partial class CombatRuntime
    {
        private long _nextCombatModifierOrder;

        private void DiscardRemovedCombatTargetModifiers(uint entityId)
        {
            string[] monsterKeys = _activeMonsterModifiers.Where(pair => pair.Value.TargetEntityId == entityId).Select(pair => pair.Key).ToArray();
            for (int i = 0; i < monsterKeys.Length; i++)
                _activeMonsterModifiers.Remove(monsterKeys[i]);
            string[] damageKeys = _activePlayerDamageModifiers.Where(pair => pair.Value.TargetEntityId == entityId).Select(pair => pair.Key).ToArray();
            for (int i = 0; i < damageKeys.Length; i++)
                _activePlayerDamageModifiers.Remove(damageKeys[i]);
            string[] auraKeys = _activePlayerAnchoredMonsterAuras.Where(pair => pair.Value.AnchorEntityId == entityId).Select(pair => pair.Key).ToArray();
            for (int i = 0; i < auraKeys.Length; i++)
            {
                _activePlayerAnchoredMonsterAuras.Remove(auraKeys[i]);
                _activePlayerAnchoredMonsterAuraKeys.Remove(auraKeys[i]);
            }
            string prefix = entityId + ":";
            string[] networkKeys = _playerModifierNetworkIds.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            for (int i = 0; i < networkKeys.Length; i++)
                _playerModifierNetworkIds.Remove(networkKeys[i]);
        }

        public void AdvanceMonsterModifiersChild(uint entityId, uint simulationTick)
        {
            if (_activeMonsterModifiers.Count == 0 && _activePlayerDamageModifiers.Count == 0 && _activePlayerAnchoredMonsterAuras.Count == 0)
                return;
            var entries = _activeMonsterModifiers.Values.Where(mod => mod.TargetEntityId == entityId).Select(mod => (mod.Order, Kind: 0))
                .Concat(_activePlayerDamageModifiers.Values.Where(mod => mod.TargetEntityId == entityId).Select(mod => (mod.Order, Kind: 1)))
                .Concat(_activePlayerAnchoredMonsterAuras.Values.Where(mod => mod.AnchorEntityId == entityId).Select(mod => (mod.Order, Kind: 2)))
                .OrderBy(entry => entry.Order).ToArray();
            foreach (var entry in entries)
            {
                if (entry.Kind == 0)
                    AdvanceMonsterModifierRuntime(GetRoomRngForMonster(GetMonster(entityId)), simulationTick, "Modifiers::update", entityId, entry.Order);
                else if (entry.Kind == 1)
                    AdvancePlayerDamageModifierRuntime(entityId, simulationTick, "Modifiers::update", entry.Order);
                else
                    AdvancePlayerAnchoredMonsterAurasForEntity(entityId, entry.Order);
            }
        }

        public bool TryGetCombatTarget(uint entityId, out CombatTarget target)
        {
            target = null;
            if (_players.TryGetValue(entityId, out CombatPlayer player))
                target = player;
            else if (_activeMonsters.TryGetValue(entityId, out Monster monster))
                target = monster;
            return target != null;
        }

        private IEnumerable<CombatTarget> GetCombatTargetsInEntityOrder()
        {
            foreach (uint entityId in _entityOrder)
                if (!IsSummonInitializationPending(entityId) && TryGetCombatTarget(entityId, out CombatTarget target))
                    yield return target;
        }

        private static bool IsMonsterEnemyOfTarget(Monster source, CombatTarget target)
        {
            return source != null && target != null && source.EntityId != target.EntityId
                && UnitFactionRules.IsEnemyFaction(source.FactionID, target.Monster?.FactionID ?? 0,
                    source.UnitDescIsAlwaysFriendly, target.Monster?.UnitDescIsAlwaysFriendly ?? false);
        }

        private static bool IsCombatTargetEnemy(CombatTarget source, CombatTarget target)
        {
            return source != null && target != null && source.EntityId != target.EntityId
                && UnitFactionRules.IsEnemyFaction(source.Monster?.FactionID ?? 0, target.Monster?.FactionID ?? 0,
                    source.Monster?.UnitDescIsAlwaysFriendly ?? false, target.Monster?.UnitDescIsAlwaysFriendly ?? false);
        }

        private int ResolveTargetCombatRadiusFixed(CombatTarget target)
        {
            return target?.Monster != null ? Math.Max(0, target.Monster.CollisionRadiusF32) : ResolveAvatarCombatRadiusFixed();
        }

        private void ApplyCombatTargetQueriedDamage(Monster sourceMonster, CombatTarget target, uint damageWire, string source)
        {
            if (target?.PlayerState != null)
            {
                uint playerHPBefore = target.PlayerState.CurrentHPWire;
                target.PlayerState.TakeQueriedDamage(damageWire);
                RecordMonsterCombatParticipation(sourceMonster, target.EntityId, playerHPBefore - target.PlayerState.CurrentHPWire);
                return;
            }
            Monster victim = target?.Monster;
            if (victim == null || !victim.IsAlive || damageWire == 0)
                return;
            uint before = PeekRuntimeMonsterHPWire(victim);
            uint after = damageWire >= before ? 0 : before - damageWire;
            NotifyMonsterOnAttackedAdmission(victim, sourceMonster?.EntityId ?? 0, source);
            RecordMonsterCombatParticipation(victim, sourceMonster?.EntityId ?? 0, before - after);
            RecordMonsterCombatParticipation(sourceMonster, victim.EntityId, before - after);
            SetRuntimeMonsterHPWire(victim, after, true, source);
            if (after == 0)
            {
                MarkMonsterDead(victim, source);
                _pendingModifierKills.Enqueue(new PendingModifierKill
                {
                    SourceEntityId = sourceMonster?.EntityId ?? 0,
                    TargetEntityId = victim.EntityId,
                    DamageTick = _combatTick,
                    Source = source
                });
            }
        }

        public CombatPlayer ResolveCombatRewardOwner(uint sourceEntityId, string instanceKey)
        {
            if (!TryGetCombatTarget(sourceEntityId, out CombatTarget source))
                return null;
            CombatPlayer player = source.Player ?? source.Monster?.Summoner;
            if (player == null || !ReferenceEquals(GetPlayer(player.EntityId), player)
                || !string.Equals(RoomRuntime.NormalizeInstanceKey(player.InstanceKey), RoomRuntime.NormalizeInstanceKey(instanceKey), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(RoomRuntime.NormalizeInstanceKey(source.InstanceKey), RoomRuntime.NormalizeInstanceKey(instanceKey), StringComparison.OrdinalIgnoreCase))
                return null;
            return player;
        }

        public void RecordMonsterCombatParticipation(Monster victim, uint sourceEntityId, uint damageWire)
        {
            if (victim == null || damageWire == 0 || string.IsNullOrWhiteSpace(victim.InstanceKey))
                return;
            CombatPlayer player = ResolveCombatRewardOwner(sourceEntityId, victim.InstanceKey);
            if (player != null && UnitFactionRules.IsEnemyFaction(0, victim.FactionID, false, victim.UnitDescIsAlwaysFriendly))
                victim.CombatCredit.Record(player, damageWire);
        }

        public bool HasMonsterCombatParticipation(Monster victim, uint playerEntityId)
        {
            CombatPlayer player = GetPlayer(playerEntityId);
            return victim != null && player != null && MatchesInstance(victim, player.InstanceKey) && victim.CombatCredit.Contains(player);
        }

        public CombatPlayer ResolveMonsterLootParticipant(Monster victim)
        {
            return victim?.CombatCredit.Resolve(player => ReferenceEquals(GetPlayer(player.EntityId), player)
                && player.IsAlive && player.PlayerState != null && player.PlayerState.CurrentHPWire != 0
                && MatchesInstance(victim, player.InstanceKey));
        }

        private void RaiseCombatTargetDamageResolved(Monster source, CombatTarget target, bool damaged, uint hpWire, string reason)
        {
            if (target?.Player != null)
                OnPlayerDamageResolved?.Invoke(source, target.Player, damaged, hpWire, reason);
            else if (target?.Monster != null)
                OnMonsterAttackResolved?.Invoke(source, target, damaged, hpWire);
        }

        private static void DispatchCombatTargetDamageEvent(CombatTarget target, MersenneTwister rng, uint entityId, string name, string source)
        {
            target?.PlayerState?.DoAttributeModifierDamageEvent(rng, entityId, name, source);
            if (target?.Monster?.AttributeModifiers == null)
                return;
            foreach (MonsterAttributeModifier modifier in target.Monster.AttributeModifiers)
            {
                if (modifier == null || modifier.RemainingTicks <= 1 || modifier.TerminateWhenHitChance <= 0)
                    continue;
                uint raw = RngLedger.Generate(rng, "room", "Modifier::doEvent@0x004FEB40:TerminateWhenHitChance", name, entityId);
                if (raw % 100u + 1 <= modifier.TerminateWhenHitChance)
                    modifier.RemainingTicks = 1;
            }
        }

        private bool TryCommitCombatTargetDeathEvent(CombatTarget target, string source)
        {
            return target?.Monster != null || TryCommitPlayerAttributeModifierDeathEvent(target?.Player, source);
        }

        private uint ConsumeOnApplyDamageEffectRng(MersenneTwister rng, string actor, Monster attacker, CombatTarget target, uint oldHPWire, uint newHPWire, uint damageWire, string source)
        {
            return ConsumeOnApplyDamageEffectRng(rng, actor, attacker, target, oldHPWire, newHPWire, damageWire, ResolveMonsterWeaponDamageStunMod(attacker), source);
        }

        private uint ConsumeOnApplyDamageEffectRng(MersenneTwister rng, string actor, Monster attacker, CombatTarget target, uint oldHPWire, uint newHPWire, uint damageWire, int stunMod, string source)
        {
            return target?.Monster != null
                ? ConsumeOnApplyDamageEffectRng(rng, actor, target.Monster, oldHPWire, newHPWire, damageWire, stunMod, attacker.Level, attacker.EntityId, source, true, attacker.PosFixedX, attacker.PosFixedY)
                : ConsumeOnApplyDamageEffectRng(rng, actor, attacker, target?.Player, oldHPWire, newHPWire, damageWire, stunMod, source);
        }
    }
}
