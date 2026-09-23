using System;
using System.Collections.Generic;
using DungeonRunners.Data;
using DungeonRunners.Core;
using DungeonRunners.Networking;

namespace DungeonRunners.Combat
{
    public partial class CombatRuntime
    {
        public event Action<uint, Monster, uint, string> OnPlayerHitEvent;

        public void DispatchPlayerDamageEvents(uint sourceEntityId, Monster target, uint damageWire, string effectPath, string source)
        {
            MersenneTwister rng = GetRoomRngForMonster(target);
            if (target == null || rng == null)
                return;
            OnPlayerHitEvent?.Invoke(sourceEntityId, target, damageWire, effectPath);
            DispatchCombatTargetDamageEvent(target, rng, target.EntityId, target.Name, source);
        }

        public IReadOnlyList<Monster> SpawnFriendlySummons(CombatPlayer owner, SpellData spell, int skillLevel,
            int sourceFixedX, int sourceFixedY, int sourceFixedZ, int destinationFixedX, int destinationFixedY)
        {
            var created = new List<Monster>();
            FriendlySummonPlan plan = spell?.FriendlySummon;
            if (plan == null || owner?.PlayerState == null || !owner.IsAlive || owner.PlayerState.CurrentHPWire == 0
                || !ReferenceEquals(GetPlayer(owner.EntityId), owner))
                return created;
            PathMap map = PathMapCatalog.Instance.GetPathMap(owner.InstanceKey);
            if (map == null)
                return created;
            int heading = UnitMover.VectorToHeadingFixed(destinationFixedX - sourceFixedX, destinationFixedY - sourceFixedY) >> 8;
            int sin = UnitMover.ZRotateSinFixed(heading);
            int cos = UnitMover.ZRotateCosFixed(heading);
            int burstX = sourceFixedX + (int)(((long)cos * plan.BurstOffsetF32) >> 8);
            int burstY = sourceFixedY + (int)(((long)sin * plan.BurstOffsetF32) >> 8);
            int destX = burstX + (int)(((long)cos * plan.BurstDistanceF32) >> 8);
            int destY = burstY + (int)(((long)sin * plan.BurstDistanceF32) >> 8);
            map.CastGroundRayFixed(burstX, burstY, sourceFixedZ, destX, destY, out int centerX, out int centerY, out int centerZ);
            byte level = checked((byte)Math.Clamp((spell.ResolvePowerLevelF32(skillLevel) >> 8) + plan.LevelOffset, 1, 110));
            for (int ordinal = 0; ordinal < plan.Quantity; ordinal++)
            {
                bool placed = false;
                for (int radius = plan.InnerRadiusF32; !placed && radius <= plan.OuterRadiusF32; radius += 5 * 256)
                {
                    for (int attempt = 0; attempt < 24; attempt++)
                    {
                        int angle = UnitMover.WrapDegrees(heading + ordinal * 360 / plan.Quantity + attempt * 15);
                        int x = centerX + (int)(((long)UnitMover.ZRotateCosFixed(angle) * radius) >> 8);
                        int y = centerY + (int)(((long)UnitMover.ZRotateSinFixed(angle) * radius) >> 8);
                        if (!map.IsWalkableFixed(x, y) || map.CastGroundRayBlockedFixed(centerX, centerY, x, y))
                            continue;
                        int z = map.GetHeightAtFixed(x, y, centerZ);
                        Monster summon = SpawnMonsterFixed(plan.UnitPath, x, y, z, heading << 8,
                            map.ZoneName, instanceKey: owner.InstanceKey, summoner: owner, summonLevel: level);
                        if (summon != null)
                            created.Add(summon);
                        placed = true;
                        break;
                    }
                }
            }
            return created;
        }

        private readonly HashSet<uint> _pendingSummonInitializations = new HashSet<uint>();
        private readonly Dictionary<PlayerState, List<FriendlySummonTransfer>> _transferringFriendlySummons = new();

        public sealed class FriendlySummonTransfer
        {
            public string UnitPath;
            public byte Level;
            public uint Health;
            public uint Mana;
            public ushort HealthCooldown;
            public ushort ManaCooldown;
        }

        private void RemoveOwnedSummons(CombatPlayer owner, bool zoning)
        {
            if (owner?.PlayerState == null)
                return;
            var transfers = new List<FriendlySummonTransfer>();
            var owned = new List<Monster>();
            foreach (Monster monster in GetAllMonsters())
                if (ReferenceEquals(monster.Summoner, owner))
                    owned.Add(monster);
            foreach (Monster summon in owned)
            {
                if (zoning && summon.IsAlive && summon.CurrentHPWire > 0
                    && string.Equals(summon.ZoneAction, "FollowOnZone", StringComparison.OrdinalIgnoreCase))
                {
                    GetMonsterRegenCooldownTicks(summon.EntityId, out ushort health, out ushort mana);
                    transfers.Add(new FriendlySummonTransfer { UnitPath = summon.SpawnGCType ?? summon.GCType,
                        Level = summon.Level, Health = summon.CurrentHPWire, Mana = summon.CurrentManaWire,
                        HealthCooldown = health, ManaCooldown = mana });
                }
                DespawnMonster(summon.EntityId, true);
            }
            if (transfers.Count > 0)
                _transferringFriendlySummons[owner.PlayerState] = transfers;
            else if (!zoning)
                _transferringFriendlySummons.Remove(owner.PlayerState);
        }

        public void RestoreFriendlySummons(CombatPlayer owner)
        {
            if (owner?.PlayerState == null || !owner.IsAlive || owner.PlayerState.CurrentHPWire == 0
                || !ReferenceEquals(GetPlayer(owner.EntityId), owner)
                || !_transferringFriendlySummons.TryGetValue(owner.PlayerState, out var transfers))
                return;
            PathMap map = PathMapCatalog.Instance.GetPathMap(owner.InstanceKey);
            if (map == null || !map.IsWalkableFixed(owner.PosFixedX, owner.PosFixedY))
                return;
            _transferringFriendlySummons.Remove(owner.PlayerState);
            foreach (FriendlySummonTransfer transfer in transfers)
                SpawnMonsterFixed(transfer.UnitPath, owner.PosFixedX, owner.PosFixedY, owner.PosFixedZ, 0,
                    map.ZoneName, instanceKey: owner.InstanceKey, summoner: owner, summonLevel: transfer.Level, summonTransfer: transfer);
        }

        public void ComputeMonsterSpellDamageRange(Monster monster, int damageTypeId, int effectScaleF32, int volatilityF32, out int minimum, out int maximum)
        {
            UnitSlotState slots = monster.Slots;
            int typeModifierSlot = damageTypeId switch
            {
                0 => UnitSlot.CrushingDamageMod, 1 => UnitSlot.PiercingDamageMod, 2 => UnitSlot.SlashingDamageMod,
                3 => UnitSlot.FireDamageMod, 4 => UnitSlot.IceDamageMod, 5 => UnitSlot.PoisonDamageMod,
                6 => UnitSlot.ShadowDamageMod, 7 => UnitSlot.DivineDamageMod, _ => -1
            };
            int bonus = slots.Get(UnitSlot.DamageBonus) + slots.Get(0x2a8) + (typeModifierSlot < 0 ? 0 : slots.Get(typeModifierSlot + 4));
            int baseF32 = ResolveMonsterDamageTableF32(monster.Level, monster.UseHenchmanCurveTables);
            baseF32 = unchecked((int)(((long)baseF32 * GCDatabase.Instance.GetRequiredKnobFixed32("DPSModifier")) >> 8));
            int damageMod = monster.DamageModPercent + monster.GetActiveAttributeModifierValue("DAMAGE_MOD")
                + (typeModifierSlot < 0 ? 0 : slots.Get(typeModifierSlot));
            damageMod = unchecked((int)(((long)(damageMod << 8) * slots.Get(0x300, 0x100)) >> 8)) >> 8;
            int value = unchecked(baseF32 + (bonus << 8));
            value = unchecked((int)(((long)value * effectScaleF32) >> 8));
            value = unchecked((int)(((long)value * (100 + slots.Get(0x2a4))) / 100));
            value = unchecked((int)(((long)value * damageMod) / 100));
            int spread = unchecked((int)(((long)value * volatilityF32) >> 8));
            minimum = RoundMonsterSpellDamage(unchecked(value - spread));
            maximum = RoundMonsterSpellDamage(unchecked(value + spread));
        }

        private static int RoundMonsterSpellDamage(int value)
        {
            if ((value & 255) >= 127) value = unchecked(value + 256);
            return Math.Max(256, value & ~255);
        }

        public bool IsSummonInitializationPending(uint entityId) => _pendingSummonInitializations.Contains(entityId);

        public void AdmitSummonInitialization(uint entityId, uint simulationTick)
        {
            if (!_pendingSummonInitializations.Remove(entityId))
                return;
            Monster monster = GetMonster(entityId);
            if (monster == null)
                return;
            UnregisterEntityOrder(entityId);
            RegisterEntityOrder(entityId, simulationTick);
            monster.EntityUpdateAdmissionTick = simulationTick;
            InitializeMonsterClientSimulation(monster);
        }

        private void InitializeMonsterClientSimulation(Monster monster)
        {
            MersenneTwister rng = GetRoomRngForMonster(monster);
            monster.Behavior.EnterSimulation(BuildMeleeBehaviorContext(rng, monster, null, int.MaxValue,
                ResolveMonsterEffectiveAttackRangeFixed(monster)));
            monster.UnitFlags |= 0x404;
            monster.StockUnitState = 8;
            InitializeMonsterState0Idle(monster, rng);
            InitializeMonsterStockUnitLifespan(monster);
        }

        private void ConfigureSummonAuthoredAttributeModifiers(Monster monster, GCNode authoredUnit)
        {
            GCNode modifiers = authoredUnit?.GetChild("Modifiers");
            if (modifiers == null)
                return;
            foreach (GCNode slot in modifiers.EnumerateChildrenInOrder())
            {
                GCNode modifier = GCDatabase.Instance.ResolveWithInheritance(slot) ?? slot;
                if (!AuthoredExtends(modifier, "AttributeModifier"))
                    continue;
                GCNode description = ResolveMonsterModifierDescription(modifier);
                var attributes = new List<GCNode>();
                CollectMonsterModifierAttributeNodes(description, attributes, new HashSet<GCNode>());
                var values = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (GCNode attribute in attributes)
                    values[attribute.GetString("Attribute", "")] = ResolveMonsterAttributeLevelValue(attribute, 1, (uint)monster.Level << 8);
                ApplyMonsterAuthoredAttributeModifier(monster, slot.CanonicalPath, values,
                    modifier.GetInt("Duration", 0), description.GetBool("RemoveOnDeath", false),
                    description.GetString("StackRule", ""), (uint)monster.Level << 8,
                    monster.EntityId, null, null, 1, description.GetInt("TerminateWhenHitChance", 0));
            }
        }

        private bool PrepareSummonIdleFollow(MeleeBehaviorContext context)
        {
            Monster monster = context?.Monster;
            string action = NormalizeAuthoredEnumToken(monster?.IdleAction);
            if (monster == null || (action != "FOLLOW" && action != "4"))
                return false;
            CombatPlayer owner = monster.Summoner;
            if (owner?.PlayerState == null || !ReferenceEquals(GetPlayer(owner.EntityId), owner) || !MatchesInstance(monster, owner.InstanceKey))
                return false;
            monster.IdleFollowTargetEntityId = owner.EntityId;
            long dx = (long)owner.PosFixedX - monster.PosFixedX;
            long dy = (long)owner.PosFixedY - monster.PosFixedY;
            BuildMeleeBehaviorContext(context.Rng, monster, owner, UnitMover.IntSqrt(dx * dx + dy * dy),
                ResolveMonsterEffectiveAttackRangeFixed(monster));
            return true;
        }
    }
}
