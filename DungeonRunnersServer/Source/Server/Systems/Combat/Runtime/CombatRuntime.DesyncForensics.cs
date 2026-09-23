using System;
using DungeonRunners.Core;
using DungeonRunners.Engine;

namespace DungeonRunners.Combat
{
    public partial class CombatRuntime
    {
        public void TraceDesyncForensics(
            Monster monster,
            string phase,
            string source,
            CombatTarget target = null,
            uint? hpBeforeWire = null,
            uint? hpAfterWire = null,
            uint? damageWire = null,
            uint? writerTick = null,
            uint? cutoffTick = null)
        {
            if (!ServerDiagnostics.IsEnabled("desyncForensicsTracking") || monster == null)
                return;

            CombatTarget resolvedTarget = target ?? (TryGetCombatTarget(monster.TargetId, out CombatTarget candidate) ? candidate : null);
            string instanceKey = RoomRuntime.NormalizeInstanceKey(
                !string.IsNullOrWhiteSpace(monster.InstanceKey) ? monster.InstanceKey : monster.ZoneName);
            RoomRuntime roomRuntime = null;
            PathMap pathMap = null;
            PathNode pathNode = null;
            string pathMapState = "missing";
            string pathMapRay = "target=none";
            string pathMapReachability = "target=none";
            bool pointBlocked = false;
            bool segmentBlocked = false;
            WorldCollisionHit pointHit = null;
            WorldCollisionHit segmentHit = null;
            string collisionState = "not-queried";
            int targetFixedX = monster.PosFixedX;
            int targetFixedY = monster.PosFixedY;
            int targetFixedZ = monster.PosFixedZ;
            int targetServerFixedX = monster.PosFixedX;
            int targetServerFixedY = monster.PosFixedY;
            int targetServerFixedZ = monster.PosFixedZ;

            if (resolvedTarget != null)
            {
                targetFixedX = resolvedTarget.ClientSimulationPosFixedX;
                targetFixedY = resolvedTarget.ClientSimulationPosFixedY;
                targetFixedZ = resolvedTarget.ClientSimulationPosFixedZ;
                targetServerFixedX = resolvedTarget.PosFixedX;
                targetServerFixedY = resolvedTarget.PosFixedY;
                targetServerFixedZ = resolvedTarget.PosFixedZ;
            }

            try
            {
                TryGetRoomRuntime(instanceKey, out roomRuntime);
                pathMap = PathMapCatalog.Instance.GetPathMap(instanceKey);
                if (pathMap == null && !string.Equals(instanceKey, monster.ZoneName, StringComparison.OrdinalIgnoreCase))
                    pathMap = PathMapCatalog.Instance.GetPathMap(monster.ZoneName);
                if (pathMap != null)
                {
                    pathMapState = $"zone={pathMap.ZoneName ?? ""};nodes={pathMap.NodeCount};walkable={pathMap.WalkableCount};bounds=({pathMap.MinWorldFixedX},{pathMap.MinWorldFixedY})->({pathMap.MaxWorldFixedX},{pathMap.MaxWorldFixedY})";
                    pathNode = pathMap.GetClosestNodeFixed(monster.PosFixedX, monster.PosFixedY, monster.PosFixedZ);
                    if (resolvedTarget != null)
                    {
                        pathMapRay = LimitForensicText(pathMap.DescribeGroundRayFixed(monster.PosFixedX, monster.PosFixedY, targetFixedX, targetFixedY));
                        pathMapReachability = pathMap.GetReachabilityFixed(monster.PosFixedX, monster.PosFixedY, targetFixedX, targetFixedY).ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                pathMapState = $"error={ex.GetType().Name}";
                pathMapRay = "error";
                pathMapReachability = "error";
            }

            try
            {
                int collisionRadiusFixed = Math.Max(0, ResolveProjectileUnitCollisionRadiusF32(monster));
                pointBlocked = WorldCollision.Instance.TryGetPointBlockerFixed(
                    monster.ZoneName,
                    instanceKey,
                    monster.PosFixedX,
                    monster.PosFixedY,
                    monster.PosFixedZ,
                    collisionRadiusFixed,
                    out pointHit);
                if (resolvedTarget != null)
                {
                    segmentBlocked = WorldCollision.Instance.TrySegmentHitFixed(
                        monster.ZoneName,
                        instanceKey,
                        monster.PosFixedX,
                        monster.PosFixedY,
                        monster.PosFixedZ,
                        targetFixedX,
                        targetFixedY,
                        targetFixedZ,
                        collisionRadiusFixed,
                        out segmentHit);
                }
                collisionState = $"radiusFixed={collisionRadiusFixed};point={(pointBlocked ? "blocked" : "clear")};segment={(segmentBlocked ? "blocked" : "clear")}";
            }
            catch (Exception ex)
            {
                collisionState = $"error={ex.GetType().Name}";
            }

            uint currentHPWire = PeekMonsterCurrentHPWire(monster);
            string behaviorAction = monster.Behavior == null ? "none" : monster.Behavior.CurrentAction.ToString();
            string pathNodeText = pathNode == null
                ? "none"
                : $"grid=({pathNode.GridX},{pathNode.GridY});world=({pathNode.WorldFixedX},{pathNode.WorldFixedY});height={pathNode.HeightFixed};flags=0x{pathNode.ConnectionFlags:X2};solid=0x{pathNode.SolidFlag:X2};walkable={pathNode.IsWalkable}";
            string roomRng = roomRuntime == null
                ? "none"
                : $"seed=0x{roomRuntime.Seed:X8};pos={roomRuntime.RngCallsSinceReseed}";
            string unitRng = monster.Rng == null
                ? $"seed=0x{monster.RngSeed:X8};pos=none"
                : $"seed=0x{monster.Rng.LastSeed:X8};configured=0x{monster.RngSeed:X8};pos={monster.Rng.CallsSinceReseed}";
            string targetText = resolvedTarget == null
                ? "none"
                : $"{resolvedTarget.Name ?? ""}#{resolvedTarget.EntityId};alive={resolvedTarget.IsAlive};server=({targetServerFixedX},{targetServerFixedY},{targetServerFixedZ});client=({targetFixedX},{targetFixedY},{targetFixedZ})";

            Debug.LogError(
                $"[DESYNC-FORENSICS] phase={SanitizeForensicToken(phase)} source={SanitizeForensicToken(source)} tick={CombatTick} writerTick={(writerTick.HasValue ? writerTick.Value.ToString() : "none")} cutoffTick={(cutoffTick.HasValue ? cutoffTick.Value.ToString() : "none")} " +
                $"instance='{instanceKey}' zone='{monster.ZoneName ?? ""}' entity={monster.EntityId} name='{monster.Name ?? ""}' gc='{monster.GCType ?? ""}' " +
                $"state={monster.State} fsmTop={monster.Behavior?.TopState ?? -1} fsmAction={behaviorAction} fsmGeneration={monster.Behavior?.ActionGeneration ?? 0} " +
                $"alive={monster.IsAlive} stockState={monster.StockUnitState} aggro={monster.AggroTriggered} targetId={monster.TargetId} target='{targetText}' " +
                $"hp={currentHPWire}/{monster.MaxHPWire} hpBefore={(hpBeforeWire.HasValue ? hpBeforeWire.Value.ToString() : "none")} hpAfter={(hpAfterWire.HasValue ? hpAfterWire.Value.ToString() : "none")} damageWire={(damageWire.HasValue ? damageWire.Value.ToString() : "none")} " +
                $"mana={monster.CurrentManaWire}/{monster.MaxManaWire} pos=({monster.PosFixedX},{monster.PosFixedY},{monster.PosFixedZ}) heading={monster.HeadingFixed} " +
                $"attack={monster.AttackPending}/{monster.AttackClientVisible}/{monster.AttackContactOnly}/{monster.AttackHitResolved} session={monster.AttackSessionId} commit={monster.AttackCommitTick} end={monster.AttackEndTick} " +
                $"skill='{monster.PrimaryActiveSkillPath ?? "none"}' effect='{monster.PrimaryActiveSkillEffect ?? "none"}' skillPending={monster.ActiveSkillEffectPending} skillResolved={monster.ActiveSkillEffectResolved} skillTarget={monster.ActiveSkillEffectTargetEntityId} " +
                $"modifiers={monster.AttributeModifiers?.Count ?? 0} procModifiers={monster.ProcModifiers?.Count ?? 0} slots='{monster.Slots?.DescribeDamageCore() ?? "none"}' " +
                $"rngRoom='{roomRng}' rngUnit='{unitRng}' pathMap='{pathMapState}' pathNode='{pathNodeText}' pathRay='{pathMapRay}' pathReachability={pathMapReachability} " +
                $"collision='{collisionState}' pointObject='{pointHit?.ObjectPath ?? ""}' pointCollision='{pointHit?.CollisionObject ?? ""}' pointHybrid={pointHit?.Hybrid ?? false} pointHybridSource='{pointHit?.HybridSource ?? ""}' " +
                $"segmentObject='{segmentHit?.ObjectPath ?? ""}' segmentCollision='{segmentHit?.CollisionObject ?? ""}' segmentHybrid={segmentHit?.Hybrid ?? false} segmentHybridSource='{segmentHit?.HybridSource ?? ""}' " +
                "sourceFunction=EntitySynchInfo::Validate@0x005DD900->CombatRuntime::TraceDesyncForensics");
        }

        private static string SanitizeForensicToken(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "unknown" : value.Replace(" ", "_").Replace("'", "_");
        }

        private static string LimitForensicText(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "none";
            const int maxLength = 2048;
            return value.Length <= maxLength ? value : value.Substring(0, maxLength) + "...truncated";
        }
    }
}
