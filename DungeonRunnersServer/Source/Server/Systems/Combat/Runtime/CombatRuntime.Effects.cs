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
    {        public uint ConsumeSpellDamageReactionRng(MersenneTwister rng, string actor, Monster target, uint oldHPWire, uint newHPWire, uint damageWire, int stunMod, int attackerLevel, uint sourceEntityId, string source)
        {
            return ConsumeOnApplyDamageEffectRng(rng, actor, target, oldHPWire, newHPWire, damageWire, stunMod, attackerLevel, sourceEntityId, source);
        }

        private uint ConsumeOnApplyDamageEffectRng(MersenneTwister rng, string actor, Monster attacker, CombatPlayer target, uint oldHPWire, uint newHPWire, uint damageWire, string source)
        {
            return ConsumeOnApplyDamageEffectRng(rng, actor, attacker, target, oldHPWire, newHPWire, damageWire, ResolveMonsterWeaponDamageStunMod(attacker), source);
        }

        private uint ConsumeOnApplyDamageEffectRng(MersenneTwister rng, string actor, Monster attacker, CombatPlayer target, uint oldHPWire, uint newHPWire, uint damageWire, int stunMod, string source)
        {
            if (target?.PlayerState == null || attacker == null || rng == null || damageWire == 0 || oldHPWire == 0 || newHPWire == 0 || newHPWire >= oldHPWire)
                return 0;

            int rngBefore = rng.CallsSinceReseed;
            string owner = $"{actor ?? "unknown"}->{target.Name ?? "unknown"}#{target.EntityId}";
            uint reactionRaw = RngLedger.Generate(rng, "unitOwnedCombat", $"{source ?? "Unit::onApplyDamage"}:surviving-reaction-gate", owner, target.EntityId);
            uint reactionRoll = reactionRaw % 100u;
            Debug.LogError($"[ON-APPLY-DAMAGE-RNG] actor={actor ?? "unknown"} target={target.Name ?? "unknown"}#{target.EntityId} damageFlags=0x10 damageWire={damageWire} stunMod={stunMod} hp={oldHPWire}->{newHPWire}/{target.PlayerState.MaxHPWire} reactionRaw=0x{reactionRaw:X8} reactionRoll={reactionRoll} rngPos={rngBefore}->{rng.CallsSinceReseed} source={source ?? "Unit::onApplyDamage"} sourceFunction=Unit::onApplyDamage@0x0050BE50");
            if (reactionRoll != 0)
                return reactionRaw;

            int ratio = ComputeOnApplyDamageReactionRatio(damageWire, stunMod, target.PlayerState.MaxHPWire);
            if (ratio < 10)
                return reactionRaw;

            int stunResistWire = ResolvePlayerStunResistChanceWire(target, attacker.Level);
            uint stunRaw = RngLedger.Generate(rng, "unitOwnedCombat", $"{source ?? "Unit::onApplyDamage"}:CheckStunResist", "Unit::CheckStunResist", target.EntityId);
            uint stunRoll = stunRaw % 0x6400u;
            if (stunRoll < (uint)Math.Max(0, stunResistWire))
            {
                Debug.LogError($"[PLAYER-DAMAGE-REACTION] attacker={attacker.Name}#{attacker.EntityId} target={target.Name}#{target.EntityId} result=RESIST ratio={ratio} stunMod={stunMod} stunResistWire={stunResistWire} reactionRaw=0x{reactionRaw:X8} stunRaw=0x{stunRaw:X8} stunRoll={stunRoll} rngPos={rng.CallsSinceReseed} source={source ?? "Unit::onApplyDamage"} sourceFunction=Unit::CheckStunResist@0x0050C630");
                return reactionRaw;
            }

            byte actionId = ratio >= 50
                ? CLIENT_PLAYER_STUN_ACTION_KNOCKDOWN_ID
                : ratio >= 25
                    ? CLIENT_PLAYER_STUN_ACTION_KNOCKBACK_ID
                    : CLIENT_PLAYER_STUN_ACTION_STUN_ID;
            PlayerStunActionResolved action = BuildPlayerWeaponDamageReaction(attacker, target, actionId, reactionRaw, reactionRoll, stunResistWire, stunRaw, stunRoll, source);
            OnPlayerStunActionResolved?.Invoke(attacker, target, action);
            Debug.LogError($"[PLAYER-DAMAGE-REACTION] attacker={attacker.Name}#{attacker.EntityId} target={target.Name}#{target.EntityId} result=ACTION action={action.ActionClassName} actionId=0x{action.ActionClassId:X2} ratio={ratio} stunMod={stunMod} stunResistWire={stunResistWire} reactionRaw=0x{reactionRaw:X8} stunRaw=0x{stunRaw:X8} stunRoll={stunRoll} rngPos={rng.CallsSinceReseed} source={source ?? "Unit::onApplyDamage"} sourceFunction=Unit::onApplyDamage@0x0050BE50 Unit::CheckStunResist@0x0050C630");
            return reactionRaw;
        }

        private static int ComputeOnApplyDamageReactionRatio(uint damageWire, int stunMod, uint maxHPWire)
        {
            int maxHP = unchecked((int)maxHPWire);
            if (maxHP == 0)
                maxHP = 1;
            int numerator = unchecked((int)damageWire * 100);
            numerator = unchecked(numerator * unchecked((ushort)stunMod));
            numerator /= 100;
            return numerator / maxHP;
        }

        private PlayerStunActionResolved BuildPlayerWeaponDamageReaction(Monster attacker, CombatPlayer target, byte actionId, uint reactionRaw, uint reactionRoll, int stunResistWire, uint stunRaw, uint stunRoll, string source)
        {
            int sourceFixedX = attacker?.PosFixedX ?? 0;
            int sourceFixedY = attacker?.PosFixedY ?? 0;
            if (attacker != null)
                TryGetMonsterClientVisiblePositionFixed(attacker, target?.EntityId ?? 0, out sourceFixedX, out sourceFixedY);
            int targetFixedX = target?.PosFixedX ?? sourceFixedX;
            int targetFixedY = target?.PosFixedY ?? sourceFixedY;
            bool positional = actionId == CLIENT_PLAYER_STUN_ACTION_KNOCKBACK_ID || actionId == CLIENT_PLAYER_STUN_ACTION_KNOCKDOWN_ID;
            return new PlayerStunActionResolved
            {
                EffectFamily = "Unit::onApplyDamage",
                ActionClassName = actionId == CLIENT_PLAYER_STUN_ACTION_KNOCKDOWN_ID ? "KnockDown" : actionId == CLIENT_PLAYER_STUN_ACTION_KNOCKBACK_ID ? "KnockBack" : "Stun",
                ActionClassId = actionId,
                HeadingWire = positional ? ResolveDestHeadingWire(sourceFixedX, sourceFixedY, targetFixedX, targetFixedY) : (ushort)0,
                StrengthWire = positional ? (ushort)60 : (ushort)0,
                AuthoredStrength = positional ? 60 : 0,
                ChanceWire = 1,
                ChanceRaw = reactionRaw,
                ChanceRoll = reactionRoll,
                StunResistWire = stunResistWire,
                StunRaw = stunRaw,
                StunRoll = stunRoll,
                Source = source,
                UsesKnockDownAction = actionId == CLIENT_PLAYER_STUN_ACTION_KNOCKDOWN_ID
            };
        }

        private static string FormatFixed8Diagnostic(int value)
        {
            long magnitude = value < 0 ? -(long)value : value;
            long tenths = (magnitude * 10 + 128) / 256;
            return $"{(value < 0 ? "-" : "")}{tenths / 10}.{tenths % 10}";
        }

        private static int Distance2DFixed(int x1Fixed, int y1Fixed, int x2Fixed, int y2Fixed)
        {
            long dx = (long)x2Fixed - x1Fixed;
            long dy = (long)y2Fixed - y1Fixed;
            return UnitMover.IntSqrt(dx * dx + dy * dy);
        }

        private bool StartMonsterDamageReaction(Monster target, byte actionId, int strength, uint sourceEntityId, string source, bool preserveEffectContextPose, bool hasDamagePosition = false, int damagePositionFixedX = 0, int damagePositionFixedY = 0)
        {
            if (target?.Behavior == null)
                return false;
            int targetFixedX = preserveEffectContextPose || hasDamagePosition ? target.PosFixedX : target.PosFixedX & ~0xFF;
            int targetFixedY = preserveEffectContextPose || hasDamagePosition ? target.PosFixedY : target.PosFixedY & ~0xFF;
            int sourceFixedX = targetFixedX;
            int sourceFixedY = targetFixedY;
            if (hasDamagePosition)
            {
                sourceFixedX = damagePositionFixedX;
                sourceFixedY = damagePositionFixedY;
            }
            else if (sourceEntityId != 0 && _players.TryGetValue(sourceEntityId, out CombatPlayer player) && player != null)
            {
                sourceFixedX = preserveEffectContextPose ? player.PosFixedX : player.PosFixedX & ~0xFF;
                sourceFixedY = preserveEffectContextPose ? player.PosFixedY : player.PosFixedY & ~0xFF;
            }
            int headingFixed = UnitMover.VectorToHeadingFixed(targetFixedX - sourceFixedX, targetFixedY - sourceFixedY);
            MeleeBehaviorContext behaviorContext = BuildMeleeBehaviorContextForMonster(target);
            if (behaviorContext == null || !target.Behavior.BeginDamageReaction(behaviorContext, actionId))
                return false;
            target.DamageReactionActionId = actionId;
            target.DamageReactionPhase = 1;
            target.DamageReactionHeadingFixed = headingFixed;
            target.DamageReactionStrength = strength;
            target.DamageReactionMoveTicksRemaining = actionId == 0x0B ? KNOCKDOWN_STATE_MESSAGE_TICKS : actionId == 0x0A ? 6 : 0;
            target.DamageReactionRecoveryEndTick = actionId == 0x0C ? _combatTick + 15u : 0;
            if (actionId == 0x0C)
                target.DamageReactionPhase = 3;
            Debug.LogError($"[DAMAGE-REACTION-POSITION] target={target.Name}#{target.EntityId} sourceEntity={sourceEntityId} sourceFixed8=({sourceFixedX},{sourceFixedY}) targetFixed8=({targetFixedX},{targetFixedY}) damagePosition={hasDamagePosition} heading={headingFixed} source={source ?? "Unit::onApplyDamage"} sourceFunction=Projectile::doImpact@0x00593660 Weapon::applyDamage@0x00597E50 SpellKnockDownEffect::doEffect@0x00553360");
            CancelMonsterPendingAttack(target, $"{source ?? "Unit::onApplyDamage"}-damage-reaction");
            return true;
        }

        public bool IsMonsterDamageReactionActive(Monster monster)
        {
            return monster != null && monster.DamageReactionActionId != 0;
        }

        private void AdvanceMonsterDamageReaction(Monster monster, MersenneTwister rng)
        {
            if (!IsMonsterDamageReactionActive(monster))
                return;

            if (monster.DamageReactionPhase == 1)
            {
                if (monster.DamageReactionMoveTicksRemaining > 0)
                {
                    AdvanceMonsterDamageReactionPosition(monster);
                    monster.DamageReactionMoveTicksRemaining--;
                    if (monster.DamageReactionMoveTicksRemaining == 0)
                        monster.DamageReactionPhase = 2;
                    return;
                }
                monster.DamageReactionPhase = 2;
            }

            if (monster.DamageReactionPhase == 2)
            {
                uint delayRaw = 0;
                uint delayTicks = 30;
                if (monster.DamageReactionActionId == 0x0B)
                {
                    delayRaw = RngLedger.Generate(rng, "room", "KnockDown::States:recovery-delay", $"{monster.Name}#{monster.EntityId}", monster.EntityId);
                    delayTicks = delayRaw % 30u + 30u;
                    monster.DamageReactionRecoveryEndTick = _combatTick + delayTicks + 1u;
                }
                else
                {
                    monster.DamageReactionRecoveryEndTick = _combatTick + delayTicks;
                }
                monster.DamageReactionPhase = 3;
                Debug.LogError($"[DAMAGE-REACTION] target={monster.Name}#{monster.EntityId} phase=RECOVERY actionId=0x{monster.DamageReactionActionId:X2} delayRaw=0x{delayRaw:X8} delayTicks={delayTicks} endTick={monster.DamageReactionRecoveryEndTick} sourceFunction=KnockDown::States@0x0052A380 KnockBack::States@0x00529A20");
                return;
            }

            if (monster.DamageReactionPhase == 3)
            {
                if (_combatTick < monster.DamageReactionRecoveryEndTick)
                    return;
                if (monster.DamageReactionActionId == 0x0B)
                {
                    monster.DamageReactionPhase = 4;
                    monster.DamageReactionRecoveryEndTick = _combatTick + 32u;
                    Debug.LogError($"[DAMAGE-REACTION] target={monster.Name}#{monster.EntityId} phase=STAND actionId=0x{monster.DamageReactionActionId:X2} endTick={monster.DamageReactionRecoveryEndTick} sourceFunction=KnockDown::States@0x0052A380");
                    return;
                }
            }

            if (monster.DamageReactionPhase == 4 && _combatTick < monster.DamageReactionRecoveryEndTick)
                return;

            Debug.LogError($"[DAMAGE-REACTION] target={monster.Name}#{monster.EntityId} phase=END actionId=0x{monster.DamageReactionActionId:X2} tick={_combatTick} sourceFunction=KnockDown::States@0x0052A380");
            ClearMonsterDamageReaction(monster);
        }

        private void AdvanceMonsterDamageReactionPosition(Monster monster)
        {
            int headingDegrees = (monster.DamageReactionHeadingFixed >> 8) % 360;
            if (headingDegrees < 0)
                headingDegrees += 360;
            int stepFixed = (int)(((long)monster.DamageReactionStrength << 8) / CLIENT_TICKS_PER_SECOND);
            int tableIndex = 360 - headingDegrees;
            int rawFixedX = monster.PosFixedX + (int)(((long)UnitMover.ZRotateSinFixed(tableIndex) * stepFixed) >> 8);
            int rawFixedY = monster.PosFixedY + (int)(((long)UnitMover.ZRotateCosFixed(tableIndex) * stepFixed) >> 8);
            int nextFixedX = rawFixedX;
            int nextFixedY = rawFixedY;
            int nextFixedZ = monster.PosFixedZ;
            PathMap pathMap = PathMapCatalog.Instance.GetPathMap(!string.IsNullOrWhiteSpace(monster.InstanceKey) ? monster.InstanceKey : monster.ZoneName);
            if (pathMap == null)
                throw new InvalidOperationException($"PathMap unavailable for damage reaction entity {monster.EntityId}");
            bool collided = pathMap.CastGroundRayFixed(monster.PosFixedX, monster.PosFixedY, monster.PosFixedZ, rawFixedX, rawFixedY, out nextFixedX, out nextFixedY, out nextFixedZ);
            if (collided)
                Debug.LogError($"[DAMAGE-REACTION-COLLISION] target={monster.Name}#{monster.EntityId} tick={_combatTick} start=({monster.PosFixedX},{monster.PosFixedY},{monster.PosFixedZ}) raw=({rawFixedX},{rawFixedY}) resolved=({nextFixedX},{nextFixedY},{nextFixedZ}) sourceFunction=KnockDown::UpdateKnockDown@0x0052A6B0->PathMap::CastGroundRay@0x004C60A0");
            monster.PosFixedX = nextFixedX;
            monster.PosFixedY = nextFixedY;
            monster.PosFixedZ = nextFixedZ;
            monster.ClientVisibleFixedX = nextFixedX;
            monster.ClientVisibleFixedY = nextFixedY;
            monster.ClientVisibleFixedInit = true;
            OnMonsterPositionChanged?.Invoke(monster);
        }

        private static void ClearMonsterDamageReaction(Monster monster)
        {
            if (monster == null)
                return;
            monster.DamageReactionActionId = 0;
            monster.DamageReactionPhase = 0;
            monster.DamageReactionHeadingFixed = 0;
            monster.DamageReactionStrength = 0;
            monster.DamageReactionMoveTicksRemaining = 0;
            monster.DamageReactionRecoveryEndTick = 0;
        }

        private int ResolveMonsterFollowRepositionPointFixed(Monster monster, int headingFixed, int distanceFixed, out int fixedX, out int fixedY)
        {
            if (monster == null)
                throw new InvalidOperationException("Follow::PickRandomNearbyPoint missing monster");
            uint followTargetEntityId = ResolveMonsterFollowTargetEntityId(monster);
            if (!_players.TryGetValue(followTargetEntityId, out CombatPlayer target) || target == null || !target.IsAlive)
                throw new InvalidOperationException($"Follow::PickRandomNearbyPoint missing target entity={monster.EntityId} target={followTargetEntityId}");
            string pathMapKey = ResolveMonsterPathMapKey(monster);
            if (string.IsNullOrWhiteSpace(pathMapKey))
                throw new InvalidOperationException($"Follow::PickRandomNearbyPoint missing PathMap key entity={monster.EntityId}");
            PathMap pathMap = PathMapCatalog.Instance.GetPathMap(pathMapKey);
            if (pathMap == null)
                throw new InvalidOperationException($"Follow::PickRandomNearbyPoint missing PathMap entity={monster.EntityId} key={pathMapKey}");
            ResolveMonsterFollowTargetStateFixed(target, out int targetFixedX, out int targetFixedY, out _, out _);
            int headingDegrees = UnitMover.WrapDegrees(headingFixed >> 8);
            int directionFixedX = -UnitMover.ZRotateSinFixed(headingDegrees);
            int directionFixedY = UnitMover.ZRotateCosFixed(headingDegrees);
            int reachFixed = pathMap.CastGroundRayDistanceFixed(
                targetFixedX,
                targetFixedY,
                directionFixedX,
                directionFixedY,
                distanceFixed);
            fixedX = targetFixedX + (int)(((long)directionFixedX * reachFixed) >> 8);
            fixedY = targetFixedY + (int)(((long)directionFixedY * reachFixed) >> 8);
            return reachFixed;
        }

        private static void ClearMonsterMoveToPointPath(Monster monster, byte owner)
        {
            if (monster == null)
                return;
            if (owner != 0 && monster.UnitMoverPathOwner != owner)
                return;
            if (monster.UnitMoverPathOwner == MONSTER_MOVE_TO_POINT_RETURN)
                monster.ReturnMoveActive = false;
            if (monster.UnitMoverPathRequestId > 0)
                Instance.CancelMoveToPointPathRequest(
                    monster.UnitMoverPathRequestInstanceKey,
                    monster.UnitMoverPathRequestMap,
                    monster.UnitMoverPathRequestId);
            monster.UnitMoverPathOwner = 0;
            monster.UnitMoverPathRequestId = -1;
            monster.UnitMoverPathRequestInstanceKey = null;
            monster.UnitMoverPathRequestMap = null;
            monster.UnitMoverPathFixed.Clear();
            monster.UnitMoverPathIndex = 0;
            monster.UnitMoverPathTargetFixedX = 0;
            monster.UnitMoverPathTargetFixedY = 0;
            monster.UnitMoverPathRetryCountdown = 0;
            monster.UnitMoverPathRequestTick = 0;
            monster.UnitMoverPathReadyTick = 0;
        }

        private static void ClearMonsterAttackSearchMove(Monster monster)
        {
            if (monster == null)
                return;
            monster.SearchForAttackMoveActive = false;
            monster.SearchForAttackTargetFixedX = 0;
            monster.SearchForAttackTargetFixedY = 0;
            ClearMonsterMoveToPointPath(monster, MONSTER_MOVE_TO_POINT_SEARCH);
        }

        private static void ClearMonsterFollowRepositionMove(Monster monster)
        {
            if (monster == null)
                return;
            monster.FollowRepositionMoveActive = false;
            monster.FollowRepositionTargetFixedX = 0;
            monster.FollowRepositionTargetFixedY = 0;
            ClearMonsterMoveToPointPath(monster, MONSTER_MOVE_TO_POINT_FOLLOW_REPOSITION);
        }

        private static void ClearMonsterFollowFindLineOfSightMove(Monster monster)
        {
            if (monster == null)
                return;
            monster.FollowFindLineOfSightMoveActive = false;
            monster.FollowFindLineOfSightTargetFixedX = 0;
            monster.FollowFindLineOfSightTargetFixedY = 0;
            ClearMonsterMoveToPointPath(monster, MONSTER_MOVE_TO_POINT_FOLLOW_LINE_OF_SIGHT);
        }

        private static int AllocateMoveToPointPathRequestId(MoveToPointPathQueue queue)
        {
            queue.LastRequestId = queue.LastRequestId < int.MaxValue
                ? queue.LastRequestId + 1
                : 1;
            return queue.LastRequestId;
        }

        public int RequestMoveToPointPathSync(
            string instanceKey,
            PathMap pathMap,
            Pathfinder pathfinder,
            string ownerKind,
            uint ownerEntityId,
            int targetFixedX,
            int targetFixedY,
            Func<int, bool> isCurrent,
            Action<int, Pathfinder, uint> onComplete)
        {
            if (pathMap == null || pathfinder == null || isCurrent == null || onComplete == null)
                return -2;
            string normalizedInstanceKey = RoomRuntime.NormalizeInstanceKey(instanceKey);
            MoveToPointPathQueue queue = null;
            foreach (MoveToPointPathQueue candidate in _moveToPointPathQueues)
            {
                if (ReferenceEquals(candidate.PathMap, pathMap)
                    && string.Equals(candidate.InstanceKey, normalizedInstanceKey, StringComparison.OrdinalIgnoreCase))
                {
                    queue = candidate;
                    break;
                }
            }
            if (queue == null)
            {
                queue = new MoveToPointPathQueue
                {
                    InstanceKey = normalizedInstanceKey,
                    PathMap = pathMap
                };
                _moveToPointPathQueues.Add(queue);
            }
            int requestId = AllocateMoveToPointPathRequestId(queue);
            var request = new MoveToPointPathRequest
            {
                RequestId = requestId,
                InstanceKey = normalizedInstanceKey,
                PathMap = pathMap,
                OwnerKind = ownerKind ?? string.Empty,
                OwnerEntityId = ownerEntityId,
                TargetFixedX = targetFixedX,
                TargetFixedY = targetFixedY,
                Pathfinder = pathfinder,
                IsCurrent = isCurrent,
                OnComplete = onComplete
            };
            queue.Requests.Add(request);
            return requestId;
        }

        private static void RemoveMoveToPointPathRequestAt(MoveToPointPathQueue queue, int requestIndex)
        {
            if (queue == null || requestIndex < 0 || requestIndex >= queue.Requests.Count)
                return;
            queue.Requests.RemoveAt(requestIndex);
            if (requestIndex < queue.Cursor)
                queue.Cursor--;
            if (queue.Cursor < 0 || queue.Cursor >= queue.Requests.Count)
                queue.Cursor = 0;
        }

        public bool CancelMoveToPointPathRequest(string instanceKey, PathMap pathMap, int requestId)
        {
            if (pathMap == null || requestId <= 0)
                return false;
            string normalizedInstanceKey = RoomRuntime.NormalizeInstanceKey(instanceKey);
            for (int queueIndex = 0; queueIndex < _moveToPointPathQueues.Count; queueIndex++)
            {
                MoveToPointPathQueue queue = _moveToPointPathQueues[queueIndex];
                if (!ReferenceEquals(queue.PathMap, pathMap)
                    || !string.Equals(queue.InstanceKey, normalizedInstanceKey, StringComparison.OrdinalIgnoreCase))
                    continue;
                for (int requestIndex = 0; requestIndex < queue.Requests.Count; requestIndex++)
                {
                    MoveToPointPathRequest request = queue.Requests[requestIndex];
                    if (request.RequestId != requestId)
                        continue;
                    RemoveMoveToPointPathRequestAt(queue, requestIndex);
                    return true;
                }
            }
            return false;
        }

        public int CancelMoveToPointPathRequestsForInstance(string instanceKey)
        {
            string normalizedInstanceKey = RoomRuntime.NormalizeInstanceKey(instanceKey);
            int cancelled = 0;
            foreach (MoveToPointPathQueue queue in _moveToPointPathQueues)
            {
                if (!string.Equals(queue.InstanceKey, normalizedInstanceKey, StringComparison.OrdinalIgnoreCase))
                    continue;
                cancelled += queue.Requests.Count;
                queue.Requests.Clear();
                queue.Cursor = 0;
            }
            return cancelled;
        }

        public int RemoveMoveToPointPathManagersForInstance(string instanceKey)
        {
            string normalizedInstanceKey = RoomRuntime.NormalizeInstanceKey(instanceKey);
            int cancelled = CancelMoveToPointPathRequestsForInstance(normalizedInstanceKey);
            for (int queueIndex = _moveToPointPathQueues.Count - 1; queueIndex >= 0; queueIndex--)
            {
                if (string.Equals(_moveToPointPathQueues[queueIndex].InstanceKey, normalizedInstanceKey, StringComparison.OrdinalIgnoreCase))
                    _moveToPointPathQueues.RemoveAt(queueIndex);
            }
            return cancelled;
        }

        public int CancelMoveToPointPathRequestsForEntity(uint entityId)
        {
            int cancelled = 0;
            for (int queueIndex = _moveToPointPathQueues.Count - 1; queueIndex >= 0; queueIndex--)
            {
                MoveToPointPathQueue queue = _moveToPointPathQueues[queueIndex];
                for (int requestIndex = queue.Requests.Count - 1; requestIndex >= 0; requestIndex--)
                {
                    MoveToPointPathRequest request = queue.Requests[requestIndex];
                    if (request.OwnerEntityId != entityId)
                        continue;
                    RemoveMoveToPointPathRequestAt(queue, requestIndex);
                    cancelled++;
                }
            }
            return cancelled;
        }

        private bool ReissueMonsterMoveToPointPathAfterReset(Monster monster)
        {
            if (monster == null
                || !monster.IsAlive
                || (monster.UnitMoverPathOwner != MONSTER_MOVE_TO_POINT_SEARCH
                    && monster.UnitMoverPathOwner != MONSTER_MOVE_TO_POINT_FOLLOW_REPOSITION
                    && monster.UnitMoverPathOwner != MONSTER_MOVE_TO_POINT_FOLLOW_LINE_OF_SIGHT
                    && monster.UnitMoverPathOwner != MONSTER_MOVE_TO_POINT_RETURN))
                return false;

            byte owner = monster.UnitMoverPathOwner;
            int targetFixedX = monster.UnitMoverPathTargetFixedX;
            int targetFixedY = monster.UnitMoverPathTargetFixedY;
            monster.UnitMoverPathRequestId = -1;
            monster.UnitMoverPathRequestInstanceKey = null;
            monster.UnitMoverPathRequestMap = null;
            monster.UnitMoverPathFixed.Clear();
            monster.UnitMoverPathIndex = 0;
            monster.UnitMoverPathRetryCountdown = 0;
            monster.UnitMoverPathRequestTick = _combatTick;
            monster.UnitMoverPathReadyTick = 0;

            string pathMapKey = ResolveMonsterPathMapKey(monster);
            PathMap pathMap = !string.IsNullOrWhiteSpace(pathMapKey)
                ? PathMapCatalog.Instance.GetPathMap(pathMapKey)
                : null;
            string normalizedPathMapKey = RoomRuntime.NormalizeInstanceKey(pathMapKey);
            monster.UnitMoverPathRequestInstanceKey = normalizedPathMapKey;
            monster.UnitMoverPathRequestMap = pathMap;
            PathNode startNode = pathMap?.GetClosestNodeFixed(monster.PosFixedX, monster.PosFixedY, monster.PosFixedZ);
            int resolvedTargetFixedX = monster.PosFixedX;
            int resolvedTargetFixedY = monster.PosFixedY;
            PathNode goalNode = null;
            if (pathMap != null && startNode != null)
            {
                pathMap.FindValidDestPointFixed(
                    monster.PosFixedX,
                    monster.PosFixedY,
                    targetFixedX,
                    targetFixedY,
                    startNode,
                    0x6400,
                    out resolvedTargetFixedX,
                    out resolvedTargetFixedY,
                    out goalNode);
            }
            if (pathMap == null || startNode == null || goalNode == null)
            {
                monster.UnitMoverPathRequestId = -2;
                Debug.LogError($"[MON-MOVETOPOINT-RESET] entity={monster.EntityId} owner={owner} request=-2 start=({monster.PosFixedX},{monster.PosFixedY}) target=({targetFixedX},{targetFixedY}) tick={_combatTick} result=invalid-node sourceFunction=UnitMover::ResetToInit@0x00535880");
                return false;
            }

            var pathfinder = new Pathfinder(pathMap);
            pathfinder.RequestPath(
                monster.PosFixedX,
                monster.PosFixedY,
                startNode,
                resolvedTargetFixedX,
                resolvedTargetFixedY,
                goalNode);
            uint entityId = monster.EntityId;
            int requestId = RequestMoveToPointPathSync(
                pathMapKey,
                pathMap,
                pathfinder,
                "monster",
                entityId,
                targetFixedX,
                targetFixedY,
                candidateRequestId => IsMonsterMoveToPointPathRequestCurrent(
                    candidateRequestId,
                    entityId,
                    owner,
                    normalizedPathMapKey,
                    pathMap,
                    targetFixedX,
                    targetFixedY,
                    out _),
                (candidateRequestId, completedPathfinder, simulationTick) => InstallMonsterMoveToPointPath(
                    candidateRequestId,
                    entityId,
                    owner,
                    normalizedPathMapKey,
                    pathMap,
                    targetFixedX,
                    targetFixedY,
                    completedPathfinder,
                    simulationTick));
            monster.UnitMoverPathRequestId = requestId;
            Debug.LogError($"[MON-MOVETOPOINT-RESET] entity={monster.EntityId} owner={owner} request={requestId} start=({monster.PosFixedX},{monster.PosFixedY}) target=({targetFixedX},{targetFixedY}) tick={_combatTick} result=queued sourceFunction=UnitMover::ResetToInit@0x00535880");
            return requestId > 0;
        }

        public byte GetMonsterUnitMoverMode(Monster monster)
        {
            if (monster == null)
                return UnitMover.StoppedMode;
            if (monster.MoveInDirectionHeadingInit)
                return UnitMover.MoveInDirectionMode;
            if (monster.UnitMoverPathOwner != 0
                || (WanderSimulator.Instance.TryGetSnapshot(monster.EntityId, out WanderStateSnapshot wander) && wander.HasTarget))
                return UnitMover.MoveToPointMode;
            return UnitMover.StoppedMode;
        }

        private void BeginMonsterMoveToPoint(Monster monster, byte owner, int targetFixedX, int targetFixedY)
        {
            if (monster == null)
                return;

            ClearMonsterMoveToPointPath(monster, 0);
            if (owner == MONSTER_MOVE_TO_POINT_SEARCH)
            {
                monster.FollowRepositionMoveActive = false;
                monster.FollowRepositionTargetFixedX = 0;
                monster.FollowRepositionTargetFixedY = 0;
                monster.FollowFindLineOfSightMoveActive = false;
                monster.FollowFindLineOfSightTargetFixedX = 0;
                monster.FollowFindLineOfSightTargetFixedY = 0;
            }
            else if (owner == MONSTER_MOVE_TO_POINT_FOLLOW_REPOSITION)
            {
                monster.SearchForAttackMoveActive = false;
                monster.SearchForAttackTargetFixedX = 0;
                monster.SearchForAttackTargetFixedY = 0;
                monster.FollowFindLineOfSightMoveActive = false;
                monster.FollowFindLineOfSightTargetFixedX = 0;
                monster.FollowFindLineOfSightTargetFixedY = 0;
            }
            else
            {
                monster.SearchForAttackMoveActive = false;
                monster.SearchForAttackTargetFixedX = 0;
                monster.SearchForAttackTargetFixedY = 0;
                monster.FollowRepositionMoveActive = false;
                monster.FollowRepositionTargetFixedX = 0;
                monster.FollowRepositionTargetFixedY = 0;
                if (owner == MONSTER_MOVE_TO_POINT_RETURN)
                {
                    monster.FollowFindLineOfSightMoveActive = false;
                    monster.FollowFindLineOfSightTargetFixedX = 0;
                    monster.FollowFindLineOfSightTargetFixedY = 0;
                }
            }

            monster.MoveInDirectionHeadingInit = false;
            monster.UnitMoverPathOwner = owner;
            monster.ReturnMoveActive = owner == MONSTER_MOVE_TO_POINT_RETURN;
            monster.UnitMoverPathTargetFixedX = targetFixedX;
            monster.UnitMoverPathTargetFixedY = targetFixedY;
            monster.UnitMoverPathRequestTick = _combatTick;
            monster.ChaseHeadingInit = false;

            string pathMapKey = ResolveMonsterPathMapKey(monster);
            PathMap pathMap = !string.IsNullOrWhiteSpace(pathMapKey)
                ? PathMapCatalog.Instance.GetPathMap(pathMapKey)
                : null;
            string normalizedPathMapKey = RoomRuntime.NormalizeInstanceKey(pathMapKey);
            monster.UnitMoverPathRequestInstanceKey = normalizedPathMapKey;
            monster.UnitMoverPathRequestMap = pathMap;
            PathNode startNode = pathMap?.GetClosestNodeFixed(monster.PosFixedX, monster.PosFixedY, monster.PosFixedZ);
            int resolvedTargetFixedX = monster.PosFixedX;
            int resolvedTargetFixedY = monster.PosFixedY;
            PathNode goalNode = null;
            if (pathMap != null && startNode != null)
            {
                pathMap.FindValidDestPointFixed(
                    monster.PosFixedX,
                    monster.PosFixedY,
                    targetFixedX,
                    targetFixedY,
                    startNode,
                    0x6400,
                    out resolvedTargetFixedX,
                    out resolvedTargetFixedY,
                    out goalNode);
            }
            if (pathMap == null || startNode == null || goalNode == null)
            {
                monster.UnitMoverPathRequestId = -2;
                Debug.LogError($"[MON-MOVETOPOINT-PATH] entity={monster.EntityId} owner={owner} request=-2 start=({monster.PosFixedX},{monster.PosFixedY}) target=({targetFixedX},{targetFixedY}) tick={_combatTick} result=invalid-node sourceFunction=PathManager::RequestPathSync<UnitBehavior>@0x00519920");
                if (owner == MONSTER_MOVE_TO_POINT_SEARCH)
                    ClearMonsterAttackSearchMove(monster);
                else if (owner == MONSTER_MOVE_TO_POINT_FOLLOW_REPOSITION)
                    ClearMonsterFollowRepositionMove(monster);
                else if (owner == MONSTER_MOVE_TO_POINT_RETURN)
                    StopMonsterReturnMove(monster);
                else
                    ClearMonsterFollowFindLineOfSightMove(monster);
                return;
            }

            var pathfinder = new Pathfinder(pathMap);
            pathfinder.RequestPath(
                monster.PosFixedX,
                monster.PosFixedY,
                startNode,
                resolvedTargetFixedX,
                resolvedTargetFixedY,
                goalNode);
            uint entityId = monster.EntityId;
            int requestId = RequestMoveToPointPathSync(
                pathMapKey,
                pathMap,
                pathfinder,
                "monster",
                entityId,
                targetFixedX,
                targetFixedY,
                candidateRequestId => IsMonsterMoveToPointPathRequestCurrent(
                    candidateRequestId,
                    entityId,
                    owner,
                    normalizedPathMapKey,
                    pathMap,
                    targetFixedX,
                    targetFixedY,
                    out _),
                (candidateRequestId, completedPathfinder, simulationTick) => InstallMonsterMoveToPointPath(
                    candidateRequestId,
                    entityId,
                    owner,
                    normalizedPathMapKey,
                    pathMap,
                    targetFixedX,
                    targetFixedY,
                    completedPathfinder,
                    simulationTick));
            monster.UnitMoverPathRequestId = requestId;
            Debug.LogError($"[MON-MOVETOPOINT-PATH] entity={monster.EntityId} owner={owner} request={requestId} start=({monster.PosFixedX},{monster.PosFixedY}) target=({targetFixedX},{targetFixedY}) tick={_combatTick} result=queued sourceFunction=UnitMover::MoveToPoint@0x00535FB0->PathManager::RequestPathSync<UnitBehavior>@0x00519920");
        }

        private bool IsMonsterMoveToPointPathRequestCurrent(
            int requestId,
            uint entityId,
            byte owner,
            string instanceKey,
            PathMap pathMap,
            int targetFixedX,
            int targetFixedY,
            out Monster monster)
        {
            monster = null;
            return _activeMonsters.TryGetValue(entityId, out monster)
                && monster != null
                && monster.IsAlive
                && monster.UnitMoverPathRequestId == requestId
                && monster.UnitMoverPathOwner == owner
                && ReferenceEquals(monster.UnitMoverPathRequestMap, pathMap)
                && string.Equals(monster.UnitMoverPathRequestInstanceKey, instanceKey, StringComparison.OrdinalIgnoreCase)
                && monster.UnitMoverPathTargetFixedX == targetFixedX
                && monster.UnitMoverPathTargetFixedY == targetFixedY;
        }

        public static List<(int FixedX, int FixedY)> BuildUnitMoverPath(
            Pathfinder pathfinder,
            int targetFixedX,
            int targetFixedY)
        {
            var fixedPath = new List<(int FixedX, int FixedY)>();
            if (pathfinder == null)
                return fixedPath;
            if (!pathfinder.GetPathFixed(out List<(int FixedX, int FixedY)> pathPoints))
                return fixedPath;
            fixedPath.AddRange(pathPoints);
            return fixedPath;
        }

        private void InstallMonsterMoveToPointPath(
            int requestId,
            uint entityId,
            byte owner,
            string instanceKey,
            PathMap pathMap,
            int targetFixedX,
            int targetFixedY,
            Pathfinder pathfinder,
            uint simulationTick)
        {
            if (!IsMonsterMoveToPointPathRequestCurrent(
                requestId,
                entityId,
                owner,
                instanceKey,
                pathMap,
                targetFixedX,
                targetFixedY,
                out Monster monster))
                return;

            List<(int FixedX, int FixedY)> fixedPath = BuildUnitMoverPath(pathfinder, targetFixedX, targetFixedY);

            if (fixedPath.Count == 0)
            {
                Debug.LogError($"[MON-MOVETOPOINT-PATH] entity={monster.EntityId} owner={owner} request={requestId} tick={simulationTick} expanded={pathfinder.NodesExpanded} result=no-path sourceFunction=PathManager::UpdateRequests@0x004C3D40->UnitMover::OnPathRequestComplete@0x005369B0");
                if (owner == MONSTER_MOVE_TO_POINT_SEARCH)
                    ClearMonsterAttackSearchMove(monster);
                else if (owner == MONSTER_MOVE_TO_POINT_FOLLOW_REPOSITION)
                    ClearMonsterFollowRepositionMove(monster);
                else if (owner == MONSTER_MOVE_TO_POINT_RETURN)
                    StopMonsterReturnMove(monster);
                else
                    ClearMonsterFollowFindLineOfSightMove(monster);
                return;
            }

            monster.UnitMoverPathFixed.Clear();
            monster.UnitMoverPathFixed.AddRange(fixedPath);
            monster.UnitMoverPathIndex = 0;
            monster.UnitMoverPathRequestId = -1;
            monster.UnitMoverPathRequestInstanceKey = null;
            monster.UnitMoverPathRequestMap = null;
            monster.UnitMoverPathRetryCountdown = 0;
            monster.UnitMoverPathReadyTick = simulationTick;
            string waypointLedger = string.Join(";", fixedPath.Select((point, index) => $"{index}:{point.FixedX},{point.FixedY}"));
            Debug.LogError($"[MON-MOVETOPOINT-PATH] entity={monster.EntityId} owner={owner} request={requestId} tick={simulationTick} expanded={pathfinder.NodesExpanded} direct={pathfinder.DirectReach} reachedGoal={pathfinder.ReachedGoal} waypoints={fixedPath.Count} path='{waypointLedger}' result=ready sourceFunction=PathManager::UpdateRequests@0x004C3D40->UnitMover::OnPathRequestComplete@0x005369B0");
        }

        public void UpdateMoveToPointPathRequests(uint simulationTick)
        {
            for (int queueIndex = 0; queueIndex < _moveToPointPathQueues.Count;)
            {
                MoveToPointPathQueue queue = _moveToPointPathQueues[queueIndex];
                int budgetRemaining = PATH_MANAGER_TOTAL_BUDGET;
                var completed = new List<MoveToPointPathRequest>();

                while (queue.Requests.Count > 0 && budgetRemaining > 0)
                {
                    if (queue.Cursor >= queue.Requests.Count)
                        queue.Cursor = 0;

                    MoveToPointPathRequest request = queue.Requests[queue.Cursor];
                    if (!request.IsCurrent(request.RequestId))
                    {
                        RemoveMoveToPointPathRequestAt(queue, queue.Cursor);
                        continue;
                    }

                    if (!request.Pathfinder.IsDone)
                    {
                        int quantum = Math.Min(PATH_MANAGER_REQUEST_QUANTUM, budgetRemaining);
                        int expanded = request.Pathfinder.UpdateRequest(quantum);
                        budgetRemaining -= expanded;
                    }

                    if (request.Pathfinder.IsDone)
                    {
                        completed.Add(request);
                        RemoveMoveToPointPathRequestAt(queue, queue.Cursor);
                        continue;
                    }

                    queue.Cursor++;
                }

                foreach (MoveToPointPathRequest request in completed)
                    request.OnComplete(request.RequestId, request.Pathfinder, simulationTick);

                queueIndex++;
            }
        }

        private sealed class MeleeBehaviorContext : Behavior.IMonsterBehaviorContext
        {
            public CombatRuntime Runtime;
            public Monster Monster;
            public MersenneTwister Rng;
            public string Owner;
            public bool TargetPresent;
            public uint TargetEntityIdValue;
            public int DistFixed;
            public int DistSquaredFixed;
            public int RangeFixed;
            public bool TargetMoving;
            public bool DamageReactionActionActiveAtTickEntryValue;
            public bool DamageReactionActionActiveNowValue;
            public bool UseTargetWeaponInterruptInput;

            public uint RoomDraw(string site) => RngLedger.Generate(Rng, "room", site, Owner, Monster?.EntityId);
            public bool DispatchUpdateTargets() => Runtime?.DispatchMonsterBehaviorUpdateTargets(this) ?? false;
            public bool DispatchThreatNear() => Runtime?.DispatchMonsterBehaviorThreatNear(Monster) ?? false;
            public Behavior.MonsterBehavior2.ActionSlot DispatchUpdateSkills() => Runtime?.TickMonsterUpdateSkillsTimer(Monster) ?? Behavior.MonsterBehavior2.ActionSlot.None;
            public Behavior.MonsterBehavior2.ActionSlot ResolveAttackAction() => Runtime?.ResolveMonsterAttackAction(Monster) ?? Behavior.MonsterBehavior2.ActionSlot.AttackTarget2;
            public uint MonsterEntityId => Monster?.EntityId ?? 0;
            public uint SimulationTick => Runtime?.CombatTick ?? 0;
            public uint TargetEntityId => TargetEntityIdValue;
            public uint AssistSourceEntityId => Monster?.BehaviorAssistSourceEntityId ?? 0;
            public bool IsPrimaryTargetValid(uint targetEntityId) => Runtime?.IsMonsterBehaviorPrimaryTargetValid(Monster, targetEntityId) == true;
            public bool IsAssistSourceValid(uint sourceEntityId) => Runtime?.IsMonsterBehaviorAssistSourceValid(Monster, sourceEntityId) == true;
            public bool DoAssistAction(uint sourceEntityId, out uint targetEntityId)
            {
                if (Runtime != null)
                    return Runtime.DispatchMonsterBehaviorAssistAction(this, sourceEntityId, out targetEntityId);
                targetEntityId = 0;
                return false;
            }
            public bool IsFollowTargetValid(uint targetEntityId) => Runtime?.IsMonsterFollowTargetValid(Monster, targetEntityId) == true;
            public bool OwnerActionValid => Monster != null && Monster.IsAlive;
            public bool HasTarget => TargetPresent || (Monster?.SelectedActiveSkillTargetEntityId ?? 0) != 0;
            public void ClearTargets()
            {
                TargetPresent = false;
                TargetEntityIdValue = 0;
                DistFixed = int.MaxValue;
                DistSquaredFixed = int.MaxValue;
                TargetMoving = false;
                Runtime?.ClearMonsterBehaviorTargets(Monster);
            }
            public int DistanceToTargetFixed => DistFixed;
            public int DistanceToTargetSquaredFixed => DistSquaredFixed;
            public bool TargetMovingThisFrame => TargetMoving;
            public bool TargetInMeleeReach => DistFixed <= RangeFixed;
            public bool TargetIsPlayer => Runtime?.GetPlayer(TargetEntityId) != null;
            public bool HasAttackDistanceRange => Runtime != null && Runtime.ResolveMonsterSearchAttackDistanceSpanFixed(Monster) > 0;
            public int ResolveAttackDistance(uint raw) => Runtime?.ResolveMonsterSearchAttackDistanceFixed(Monster, raw) ?? 0;
            public bool HasRetreatDistanceRange => Runtime != null && Runtime.ResolveMonsterSearchRetreatDistanceSpanFixed(Monster) > 0;
            public int ResolveRetreatDistance(uint raw) => Runtime?.ResolveMonsterSearchRetreatDistanceFixed(Monster, raw) ?? 0;
            public int ScoreCurrentAttackLocation() => Runtime?.ScoreMonsterCurrentAttackLocationFixed(Monster) ?? 0;
            public void PrepareSearchAttackTargetActionUse() => Runtime?.PrepareMonsterSearchAttackTargetActionUse(Monster);
            public void FaceSearchTarget() => Runtime?.FaceMonsterSearchTarget(Monster);
            public int ScoreAttackLocation(int headingFixed, int distanceFixed, out int fixedX, out int fixedY)
            {
                if (Runtime != null)
                    return Runtime.ScoreMonsterAttackLocationFixed(Monster, headingFixed, distanceFixed, out fixedX, out fixedY);
                fixedX = 0;
                fixedY = 0;
                return 0;
            }
            public int ScoreRetreatLocation(int headingFixed, int distanceFixed, out int fixedX, out int fixedY)
            {
                if (Runtime != null)
                    return Runtime.ScoreMonsterRetreatLocationFixed(Monster, headingFixed, distanceFixed, out fixedX, out fixedY);
                fixedX = 0;
                fixedY = 0;
                return 0;
            }
            public bool SearchCurrentLocationCollides => Runtime?.MonsterSearchCurrentLocationCollides(Monster) == true;
            public void IncrementSearchCollisionCount()
            {
                if (Monster != null && Monster.UnitState31B != byte.MaxValue)
                    Monster.UnitState31B++;
            }
            public void DecrementSearchCollisionCount()
            {
                if (Monster != null && Monster.UnitState31B != 0)
                    Monster.UnitState31B--;
            }
            public void BeginAttackSearchMove(int fixedX, int fixedY)
            {
                if (Monster == null)
                    return;
                Monster.SearchForAttackTargetFixedX = fixedX;
                Monster.SearchForAttackTargetFixedY = fixedY;
                Monster.SearchForAttackMoveActive = true;
                Runtime?.BeginMonsterMoveToPoint(Monster, MONSTER_MOVE_TO_POINT_SEARCH, fixedX, fixedY);
            }
            public void StopAttackSearchMove() => ClearMonsterAttackSearchMove(Monster);
            public void UpdateAttackSearchMoveForTarget() => Runtime?.UpdateMonsterAttackSearchMoveForTarget(Monster);
            public bool AttackSearchMoveComplete => Monster == null || !Monster.SearchForAttackMoveActive;
            public bool AttackTargetActionQueued => Monster != null && Monster.AttackPending && Monster.AttackActionQueued && Monster.AttackCommitTick <= 0 && Runtime != null && Runtime.CombatTick >= Monster.AttackActionAdmissionTick;
            public bool AttackTargetActionActive => Monster != null && Monster.AttackPending && Monster.AttackCommitTick > 0;
            public bool AttackRuntimePending => Monster != null && Monster.AttackPending;
            public bool IsCurrentActionBusy(Behavior.MonsterBehavior2.ActionSlot action)
            {
                if (Monster == null)
                    return false;
                if (action == Behavior.MonsterBehavior2.ActionSlot.UseTarget)
                    return Monster.SelectedActiveSkill != null && Monster.SelectedActiveSkillTargetEntityId != 0;
                if (action == Behavior.MonsterBehavior2.ActionSlot.Use)
                    return Monster.ActiveSkillSelfCycle && Monster.ActiveSkillBusyTicksRemaining != 0;
                return false;
            }
            public bool DamageReactionActionActiveAtTickEntry => DamageReactionActionActiveAtTickEntryValue;
            public bool DamageReactionActionActiveNow => DamageReactionActionActiveNowValue;
            public bool UsesWanderIdleAction => Runtime?.ShouldRegisterWander(Monster?.IdleAction, Monster?.WanderRangeF32 ?? 0, Monster?.ZoneName) == true;
            public bool PrepareIdleFollow() => Runtime?.PrepareSummonIdleFollow(this) == true;
            public void UpdateWanderAction() => Runtime?.TickMonsterWanderAction(Monster);
            public bool ConsumeWanderActionTermination()
            {
                if (Monster == null || !Monster.WanderActionTerminationPending)
                    return false;
                Monster.WanderActionTerminationPending = false;
                WanderSimulator.Instance.UnregisterEntity(Monster.EntityId);
                return true;
            }
            public bool ValidateAttackTarget2Action() => Runtime?.ValidateMonsterAttackTarget2Action(Monster) == true;
            public bool DoAttackAction() => Runtime?.DoMonsterAttackAction(Monster) ?? false;
            public bool DoUseAction() => Runtime?.DoMonsterUseAction(Monster) ?? false;
            public bool DoUseTargetAction() => Runtime?.DoMonsterUseTargetAction(Monster, UseTargetWeaponInterruptInput) ?? false;
            public bool StartFleeAction() => Runtime?.StartMonsterFleeAction(Monster, this) ?? false;
            public void UpdateFleeAction() => Runtime?.UpdateMonsterFleeAction(Monster, this);
            public bool FleeActionActive => Monster?.FleeActionActive == true;
            public void StopFleeAction() => Runtime?.StopMonsterFleeAction(Monster);
            public void StopAttackTarget2Action() => Runtime?.StopMonsterAttackTarget2Action(Monster);
            public void ClearUseTargetAction() => Runtime?.ClearMonsterUseTargetAction(Monster);
            public void StartQueuedAttackTargetAction() => Runtime?.StartQueuedMonsterAttackTargetAction(Monster);
            public void UpdateFollowSpeedMod() => Runtime?.UpdateMonsterFollowSpeedMod(Monster);
            public void ResetFollowSpeedMod() => Runtime?.ResetMonsterFollowSpeedMod(Monster);
            public void StopMoving() => Runtime?.StopMonsterMoving(Monster);
            public bool TryMoveFollowTowardTarget() => Runtime?.TryMoveMonsterFollowTowardTarget(Monster) ?? false;
            public void BeginFollowFindLineOfSightMove() => Runtime?.BeginMonsterFollowFindLineOfSightMove(Monster);
            public void StopFollowFindLineOfSightMove() => ClearMonsterFollowFindLineOfSightMove(Monster);
            public bool FollowFindLineOfSightMoving => Monster != null && Monster.FollowFindLineOfSightMoveActive;
            public int ResolveFollowRepositionPointFixed(int headingFixed, int distanceFixed, out int fixedX, out int fixedY)
            {
                if (Runtime == null)
                    throw new InvalidOperationException("Follow::PickRandomNearbyPoint missing runtime");
                return Runtime.ResolveMonsterFollowRepositionPointFixed(Monster, headingFixed, distanceFixed, out fixedX, out fixedY);
            }
            public void BeginFollowRepositionMove(int fixedX, int fixedY)
            {
                if (Monster == null)
                    return;
                Monster.FollowRepositionTargetFixedX = fixedX;
                Monster.FollowRepositionTargetFixedY = fixedY;
                Monster.FollowRepositionMoveActive = true;
                Runtime?.BeginMonsterMoveToPoint(Monster, MONSTER_MOVE_TO_POINT_FOLLOW_REPOSITION, fixedX, fixedY);
            }
            public void StopFollowRepositionMove() => ClearMonsterFollowRepositionMove(Monster);
            public void QueueFollowReachedNearStop() => Runtime?.QueueMonsterFollowReachedNearStop(Monster);
            public bool RetreatRequired => Monster != null && Monster.Retreatable
                && MonsterHomeDistanceSquaredF32(Monster) > Monster.RetreatRangeSquaredF32;
            public bool ReturnMoveActive => Monster?.ReturnMoveActive == true;
            public bool ValidateReturnMove() => Runtime?.ValidateMonsterReturnMove(Monster) == true;
            public bool StartReturnMove() => Runtime?.StartMonsterReturnMove(Monster) == true;
            public void SetRetreatModifier(bool active) => Runtime?.SetMonsterRetreatModifier(Monster, active);
            public bool ShouldDespawn => Runtime?.ShouldMonsterDespawn(Monster) == true;
            public bool CanFinishReturn => Monster?.Encounter != null
                && MonsterHomeDistanceSquaredF32(Monster) <= 0x38400
                && Monster.Encounter.LiveUnitCount <= Monster.Encounter.ReturningUnitCount;
            public void SetEncounterReturning(bool returning) => Runtime?.SetMonsterEncounterReturning(Monster, returning);
            public void FinishEncounterReturn() => Runtime?.FinishMonsterEncounterReturn(Monster);
        }

        private void UpdateMonsterFollowSpeedMod(Monster monster)
        {
            uint followTargetEntityId = ResolveMonsterFollowTargetEntityId(monster);
            if (monster == null || !_players.TryGetValue(followTargetEntityId, out CombatPlayer target) || target?.PlayerState == null)
                return;
            int monsterFixedX = monster.PosFixedX;
            int monsterFixedY = monster.PosFixedY;
            ResolveMonsterFollowTargetStateFixed(target, out int targetFixedX, out int targetFixedY, out _, out _);
            long dx = (long)targetFixedX - monsterFixedX;
            long dy = (long)targetFixedY - monsterFixedY;
            long distanceSquared = ((dx * dx) >> 8) + ((dy * dy) >> 8);
            int followerSpeed = Math.Max(1, monster.MoveSpeedF32 >> 8);
            int targetSpeed = Math.Max(0, target.PlayerState.SpeedF32 >> 8);
            int speedModF32 = (int)(((long)targetSpeed * target.OwnerAckFollowClientEffectiveSpeedModF32) / followerSpeed);
            if (distanceSquared >= 0x132401)
            {
                speedModF32 += 0x2800;
            }
            else if (distanceSquared > 0x38400)
            {
                long numerator = (distanceSquared - 0x38400) << 8;
                int ratioF32 = (int)(numerator / 0xfa000);
                speedModF32 += (int)(((long)ratioF32 * 0x2800) >> 8);
            }
            monster.SpeedMod = speedModF32 >> 8;
        }

        private bool TryMoveMonsterFollowTowardTarget(Monster monster)
        {
            uint followTargetEntityId = ResolveMonsterFollowTargetEntityId(monster);
            if (monster == null
                || !_players.TryGetValue(followTargetEntityId, out CombatPlayer target)
                || target == null
                || !target.IsAlive)
                return false;
            string pathMapKey = ResolveMonsterPathMapKey(monster);
            PathMap pathMap = !string.IsNullOrWhiteSpace(pathMapKey)
                ? PathMapCatalog.Instance.GetPathMap(pathMapKey)
                : null;
            if (pathMap == null)
                return false;
            ResolveMonsterFollowTargetStateFixed(target, out int targetFixedX, out int targetFixedY, out _, out _);
            int deltaFixedX = targetFixedX - monster.PosFixedX;
            int deltaFixedY = targetFixedY - monster.PosFixedY;
            if (!pathMap.CanReachPointFixed(monster.PosFixedX, monster.PosFixedY, targetFixedX, targetFixedY))
            {
                monster.MoveInDirectionHeadingInit = false;
                return false;
            }
            if (monster.ReturnMoveActive)
                ClearMonsterMoveToPointPath(monster, MONSTER_MOVE_TO_POINT_RETURN);
            monster.MoveInDirectionHeadingFixed = UnitMover.NormalizedVectorToHeadingFixed(deltaFixedX, deltaFixedY);
            monster.MoveInDirectionHeadingInit = true;
            return true;
        }

    }
}
