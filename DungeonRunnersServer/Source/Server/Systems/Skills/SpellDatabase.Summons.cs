using System;
using System.Linq;
using DungeonRunners.Data;

namespace DungeonRunners.Combat
{
    public sealed class FriendlySummonPlan
    {
        public string UnitPath { get; internal set; }
        public string EffectPath { get; internal set; }
        public string HitEffectPath { get; internal set; }
        public int Quantity { get; internal set; }
        public int LevelOffset { get; internal set; }
        public int InnerRadiusF32 { get; internal set; }
        public int OuterRadiusF32 { get; internal set; }
        public int BurstOffsetF32 { get; internal set; }
        public int BurstDistanceF32 { get; internal set; }
        public int ChanceDivisor { get; internal set; }
        public int ManaCostF32 { get; internal set; }
        public SpellData WeaponImpact { get; internal set; }
        public bool IsHitProc => WeaponImpact != null;
    }

    public static partial class SpellDatabase
    {
        private static void ParseFriendlySummon(SpellData spell, GCNode skill, GCNode effect)
        {
            bool fire = string.Equals(spell.SkillId, "skills.generic.FireMeleeSummon", StringComparison.OrdinalIgnoreCase);
            bool snow = string.Equals(spell.SkillId, "skills.generic.SummonSnowMan", StringComparison.OrdinalIgnoreCase);
            if (!fire && !snow)
                return;
            GCNode procDesc = null;
            GCNode damage = null;
            if (fire)
            {
                GCNode modifier = ResolveAuthoredNodeReference(spell.CastModifierId, skill);
                procDesc = ResolveInheritedNode(modifier?.GetChild("Description"));
                if (!AuthoredExtends(modifier, "ProcModifier") || procDesc == null || spell.AddModifierWhileClosing)
                    return;
                var conditions = procDesc.EnumerateChildrenInOrder().Where(n => AuthoredExtends(n, "ProcCondition")).ToList();
                if (conditions.Count != 1 || conditions[0].GetString("EventType", "") != "ON_HIT"
                    || !string.Equals(procDesc.GetString("Target", ""), "OBJECT", StringComparison.OrdinalIgnoreCase)
                    || procDesc.GetInt("Chance", 1) < 1)
                    return;
                GCNode weapon = FindFirstEffectNode(effect, "SpellWeaponDamageEffect");
                GCNode impact = ResolveAuthoredNodeReference(weapon?.GetString("Effect", null), skill);
                damage = FindFirstEffectNode(impact, "SpellDamageEffect");
                if (damage == null || !string.Equals(conditions[0].GetString("EffectObjectType", ""), damage.CanonicalPath, StringComparison.OrdinalIgnoreCase))
                    return;
                effect = ResolveAuthoredNodeReference(procDesc.GetString("Effect", null), skill);
            }
            GCNode burst = FindFirstEffectNode(effect, "SpellBurstEffect");
            GCNode spawn = FindFirstEffectNode(burst, "SpellSpawnEffect");
            if (burst == null || spawn == null || burst.GetFixed32("BurstCountMin", 0x100) != 0x100
                || burst.GetFixed32("BurstCountMax", 0x100) != 0x100
                || spawn.GetFixed32("Chance", 0x6400) != 0x6400 || spawn.GetFixed32("QuantityInc", 0) != 0)
                return;
            string unitPath = spawn.GetString("SpawnUnit", "");
            if (AuthoredGameplayCatalog.FindSummonedUnit(unitPath) == null)
                return;
            int quantity = spawn.GetFixed32("Quantity", 0x100) >> 8;
            int inner = spawn.GetFixed32("InnerRadius", 10 * 256);
            int outer = spawn.GetFixed32("OuterRadius", 50 * 256);
            if (quantity < 1 || quantity > 32 || inner < 0 || outer < inner)
                return;
            var plan = new FriendlySummonPlan
            {
                UnitPath = unitPath,
                EffectPath = spawn.CanonicalPath,
                Quantity = quantity,
                LevelOffset = spawn.GetInt("LevelOffset", 0),
                InnerRadiusF32 = inner,
                OuterRadiusF32 = outer,
                BurstOffsetF32 = burst.GetFixed32("BurstOffset", 10 * 256),
                BurstDistanceF32 = burst.GetFixed32("BurstDistance", 100 * 256),
                ChanceDivisor = procDesc?.GetInt("Chance", 1) ?? 1,
                ManaCostF32 = procDesc?.GetFixed32("ManaCost", 0) ?? 0,
                HitEffectPath = damage?.CanonicalPath
            };
            if (fire)
            {
                var impact = new SpellData
                {
                    SkillId = spell.SkillId, EffectId = damage.CanonicalPath, DisplayName = spell.DisplayName,
                    RequiredLevel = spell.RequiredLevel, RequiredLevelIncF32 = spell.RequiredLevelIncF32,
                    MaxSkillLevel = spell.MaxSkillLevel, TargetType = spell.TargetType,
                    AttackType = spell.AttackType, DamageType = spell.DamageType
                };
                ParseDamage(impact, damage);
                plan.WeaponImpact = impact;
            }
            spell.FriendlySummon = plan;
        }
    }
}
