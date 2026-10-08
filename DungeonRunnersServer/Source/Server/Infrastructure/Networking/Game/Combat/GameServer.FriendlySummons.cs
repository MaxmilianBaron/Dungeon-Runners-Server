using System;
using System.Collections.Generic;
using DungeonRunners.Combat;
using DungeonRunners.Data;

namespace DungeonRunners.Networking
{
    public partial class GameServer
    {
        private sealed class FriendlySummonCast
        {
            public CombatPlayer Owner;
            public RRConnection Connection;
            public PlayerState State;
            public SpellData Spell;
            public int SkillLevel;
            public uint BusyUntilTick;
        }

        private readonly Dictionary<uint, FriendlySummonCast> _friendlySummonCasts = new();

        private void BeginFriendlySummonCast(RRConnection conn, PlayerState state, SpellData spell, int level, ushort componentId, byte actionId)
        {
            if (spell.FriendlySummon?.IsHitProc != true || conn.Avatar == null)
                return;
            uint entityId = (uint)conn.Avatar.Id;
            if (!TryGetActiveSkillBusyUntilTick(conn, componentId, actionId, out uint endTick))
                return;
            _friendlySummonCasts[entityId] = new FriendlySummonCast
            {
                Owner = CombatRuntime.Instance.GetPlayer(entityId), Connection = conn, State = state,
                Spell = spell, SkillLevel = level, BusyUntilTick = endTick
            };
        }

        private void AdvanceFriendlySummonCast(uint entityId, uint tick)
        {
            if (_friendlySummonCasts.TryGetValue(entityId, out FriendlySummonCast cast)
                && (unchecked(tick + 1u) >= cast.BusyUntilTick || cast.State.CurrentHPWire == 0
                    || !ReferenceEquals(CombatRuntime.Instance.GetPlayer(entityId), cast.Owner)))
                _friendlySummonCasts.Remove(entityId);
        }

        private void OnPlayerFriendlySummonHit(uint sourceEntityId, Monster target, uint damageWire, string effectPath)
        {
            if (damageWire == 0 || target == null || !target.IsAlive || target.CurrentHPWire == 0
                || !_friendlySummonCasts.TryGetValue(sourceEntityId, out FriendlySummonCast cast)
                || cast.Owner?.PlayerState == null || cast.Owner.PlayerState.CurrentHPWire == 0
                || !ReferenceEquals(CombatRuntime.Instance.GetPlayer(sourceEntityId), cast.Owner)
                || !CombatRuntime.Instance.MatchesInstance(target, cast.Owner.InstanceKey))
                return;
            FriendlySummonPlan plan = cast.Spell.FriendlySummon;
            MersenneTwister rng = CombatRuntime.Instance.GetRoomRngForMonster(target);
            if (plan.ChanceDivisor > 1
                && RngLedger.Generate(rng, "room", "ProcModifier::CheckChance@0x0056E180", cast.Spell.SkillId, sourceEntityId) % (uint)plan.ChanceDivisor != 0)
                return;
            if (!string.Equals(effectPath, plan.HitEffectPath, StringComparison.OrdinalIgnoreCase))
                return;
            int power = cast.Spell.ResolvePowerLevelF32(cast.SkillLevel);
            int perLevel = GCDatabase.Instance.GetRequiredKnobFixed32("SkillPowerCostPerLevel");
            int baseCost = GCDatabase.Instance.GetRequiredKnobFixed32("BaseSkillPowerCost");
            int scaled = unchecked(baseCost + (int)(((long)power * perLevel) >> 8));
            uint cost = checked((uint)Math.Max(0, ((long)scaled * plan.ManaCostF32) >> 8));
            if (cast.State.CurrentManaWire < cost)
                return;
            cast.State.SetCurrentMana(cast.State.CurrentManaWire - cost, "ProcModifier::DoEffect");
            if (_activeCharacter.TryGetValue(cast.Connection.LoginName, out var character))
                character.currentMana = cast.State.CurrentManaWire;
            ResolveSpellActorPoint(cast.Connection, out int x, out int y, out int z);
            CombatRuntime.Instance.SpawnFriendlySummons(cast.Owner, cast.Spell, cast.SkillLevel, x, y, z, target.PosFixedX, target.PosFixedY);
        }
    }
}
