using System;
using System.Collections.Generic;
using DungeonRunners.Core;
using DungeonRunners.Data;
using DungeonRunners.Engine;

namespace DungeonRunners.Combat
{
    public partial class CombatRuntime
    {
        private const string MonsterRetreatModifierPath = "creatures.base.RetreatModifier";

        private static int MonsterHomeDistanceSquaredF32(Monster monster)
        {
            int dx = unchecked(monster.PosFixedX - monster.SpawnPosFixedX);
            int dy = unchecked(monster.PosFixedY - monster.SpawnPosFixedY);
            int xSquared = unchecked((int)(((long)dx * dx) >> 8));
            int ySquared = unchecked((int)(((long)dy * dy) >> 8));
            return unchecked(xSquared + ySquared);
        }

        private bool ValidateMonsterReturnMove(Monster monster)
        {
            if (monster == null || !monster.IsAlive || monster.ReturnRemovalPending)
                return false;
            PathMap map = PathMapCatalog.Instance.GetPathMap(ResolveMonsterPathMapKey(monster));
            return map != null && map.CanPathToFixed(monster.PosFixedX, monster.PosFixedY,
                monster.SpawnPosFixedX, monster.SpawnPosFixedY);
        }

        private bool StartMonsterReturnMove(Monster monster)
        {
            if (!ValidateMonsterReturnMove(monster))
                return false;
            monster.ReturnMoveTargetFixedX = monster.SpawnPosFixedX;
            monster.ReturnMoveTargetFixedY = monster.SpawnPosFixedY;
            BeginMonsterMoveToPoint(monster, MONSTER_MOVE_TO_POINT_RETURN,
                monster.SpawnPosFixedX, monster.SpawnPosFixedY);
            return true;
        }

        private static void StopMonsterReturnMove(Monster monster)
        {
            if (monster == null)
                return;
            monster.ReturnMoveActive = false;
            ClearMonsterMoveToPointPath(monster, MONSTER_MOVE_TO_POINT_RETURN);
        }

        private void SetMonsterRetreatModifier(Monster monster, bool active)
        {
            if (!active)
            {
                RemoveMonsterAuthoredAttributeModifier(monster, MonsterRetreatModifierPath, "MonsterBehavior2::States-retreat-exit");
                return;
            }
            GCNode modifier = GCDatabase.Instance.ResolveWithInheritance(MonsterRetreatModifierPath);
            GCNode desc = ResolveMonsterModifierDescription(modifier);
            if (modifier == null || desc == null)
                throw new InvalidOperationException("Missing authored retreat modifier");
            var nodes = new List<GCNode>();
            CollectMonsterModifierAttributeNodes(desc, nodes, new HashSet<GCNode>());
            var attributes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (GCNode node in nodes)
                attributes[node.GetString("Attribute", "")] = ResolveMonsterAttributeLevelValue(node, 1, 256);
            ApplyMonsterAuthoredAttributeModifier(monster, MonsterRetreatModifierPath, attributes, 0,
                desc.GetBool("RemoveOnDeath", false), desc.GetString("StackRule", ""), 256, 0, null, null);
        }

        private bool ShouldMonsterDespawn(Monster monster)
        {
            if (monster == null || monster.ReturnRemovalPending || monster.Encounter == null
                || IsMonsterBehaviorPrimaryTargetValid(monster, monster.TargetId))
                return false;
            foreach (CombatPlayer player in GetPlayersInEntityOrder())
            {
                if (player == null || player.EntityId == 0 || !MatchesInstance(monster, player.InstanceKey))
                    continue;
                int dx = unchecked(monster.PosFixedX - player.PosFixedX);
                int dy = unchecked(monster.PosFixedY - player.PosFixedY);
                int dz = unchecked(-player.PosFixedZ);
                int xSquared = unchecked((int)(((long)dx * dx) >> 8));
                int ySquared = unchecked((int)(((long)dy * dy) >> 8));
                int zSquared = unchecked((int)(((long)dz * dz) >> 8));
                if (unchecked(xSquared + ySquared + zSquared) <= 500 * 500 * 256)
                    return false;
            }
            return true;
        }

        private void SetMonsterEncounterReturning(Monster monster, bool returning)
        {
            if (monster?.Encounter == null || monster.ReturnRemovalPending)
                return;
            if (returning)
                monster.Encounter.MarkReturning(monster.EntityId);
            else
                monster.Encounter.ClearReturning(monster.EntityId);
            ApplyEncounterGroup(monster.Encounter);
        }

        private void FinishMonsterEncounterReturn(Monster monster)
        {
            if (monster == null || monster.ReturnRemovalPending)
                return;
            EncounterOnUnitRemoved(monster, "MonsterBehavior2::States-return");
            monster.ReturnRemovalPending = true;
            DespawnMonster(monster.EntityId);
        }
    }
}
