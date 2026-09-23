using System;
using System.Collections.Generic;
using System.Linq;
using DungeonRunners.Combat;
using DungeonRunners.Combat.Behavior;
using DungeonRunners.Core;

namespace DungeonRunners.Networking
{
    public partial class GameServer
    {
        private readonly Dictionary<int, HashSet<ulong>> _simulationParityNpcKnownByConn = new Dictionary<int, HashSet<ulong>>();

        private void CaptureSimulationParityFrame(uint simulationTick)
        {
            if (!SimulationParityLedger.ShouldCaptureTick(simulationTick))
                return;
            List<RRConnection> connections = GetConnectionInsertionOrderSnapshot();
            List<uint> rootOrder = CombatRuntime.Instance.GetEntityUpdateOrderSnapshot(simulationTick);
            CaptureSimulationParityTickDigest(simulationTick, connections, rootOrder);
            if (!SimulationParityLedger.ShouldCaptureFrame(simulationTick))
                return;
            var views = new List<SimulationParityServerView>();
            foreach (RRConnection viewer in connections)
            {
                if (viewer == null || !viewer.IsConnected || !viewer.IsSpawned || !viewer.TickUpdatesActive || viewer.Avatar == null)
                    continue;
                views.Add(CaptureSimulationParityView(viewer, connections, simulationTick));
            }
            SimulationParityLedger.EnqueueFrame(simulationTick, views);
        }

        private void CaptureSimulationParityTickDigest(uint simulationTick, List<RRConnection> connections, List<uint> rootOrder)
        {
            var inputs = new List<SimulationParityTickInput>();
            var entities = new List<SimulationParityTickEntity>();
            var instanceOrder = new List<string>();
            var instanceSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (RRConnection connection in connections)
            {
                if (connection == null || !connection.IsConnected)
                    continue;
                string instanceKey = RoomRuntime.NormalizeInstanceKey(GetInstanceZoneKey(connection));
                if (instanceSet.Add(instanceKey))
                    instanceOrder.Add(instanceKey);
                inputs.Add(new SimulationParityTickInput
                {
                    ConnectionId = connection.ConnId,
                    EntityId = connection.Avatar?.Id ?? 0,
                    InstanceKey = instanceKey,
                    ClientEntityUpdateSerial = connection.LastClientEntityUpdateSerial,
                    ReflectedReceivedOrdinal = connection.ReflectedAvatarMovementReceivedOrdinal,
                    ReflectedAppliedOrdinal = connection.ReflectedAvatarMovementAppliedOrdinal,
                    ReflectedReadyCount = connection.ReadyReflectedAvatarMovementSamples.Count,
                    ReflectedFrontOrdinal = connection.ReadyReflectedAvatarMovementSamples.Count > 0 ? connection.ReadyReflectedAvatarMovementSamples.Peek().Ordinal : null,
                    ReflectedFrontMoveType = connection.ReadyReflectedAvatarMovementSamples.Count > 0 ? connection.ReadyReflectedAvatarMovementSamples.Peek().MoveType : null,
                    ReflectedFrontX = connection.ReadyReflectedAvatarMovementSamples.Count > 0 ? connection.ReadyReflectedAvatarMovementSamples.Peek().X : null,
                    ReflectedFrontY = connection.ReadyReflectedAvatarMovementSamples.Count > 0 ? connection.ReadyReflectedAvatarMovementSamples.Peek().Y : null,
                    ReflectedX = connection.ReflectedAvatarPosFixedX,
                    ReflectedY = connection.ReflectedAvatarPosFixedY,
                    OwnerAckReceivedOrdinal = connection.OwnerAckFollowClientReceivedOrdinal,
                    OwnerAckAppliedOrdinal = connection.OwnerAckFollowClientAppliedOrdinal,
                    OwnerAckQueueCount = connection.OwnerAckFollowClientMovementSamples.Count,
                    OwnerAckFrontOrdinal = connection.OwnerAckFollowClientMovementSamples.Count > 0 ? connection.OwnerAckFollowClientMovementSamples.Peek().Ordinal : null,
                    OwnerAckFrontApplyTick = connection.OwnerAckFollowClientMovementSamples.Count > 0 ? connection.OwnerAckFollowClientMovementSamples.Peek().SimulationApplyTick : null,
                    OwnerAckFrontMoveType = connection.OwnerAckFollowClientMovementSamples.Count > 0 ? connection.OwnerAckFollowClientMovementSamples.Peek().MoveType : null,
                    OwnerAckFrontX = connection.OwnerAckFollowClientMovementSamples.Count > 0 ? connection.OwnerAckFollowClientMovementSamples.Peek().X : null,
                    OwnerAckFrontY = connection.OwnerAckFollowClientMovementSamples.Count > 0 ? connection.OwnerAckFollowClientMovementSamples.Peek().Y : null,
                    OwnerAckX = connection.OwnerAckFollowClientPosFixedX,
                    OwnerAckY = connection.OwnerAckFollowClientPosFixedY,
                    UnitFollowReceivedOrdinal = connection.UnitFollowClientReceivedOrdinal,
                    UnitFollowAppliedOrdinal = connection.UnitFollowClientAppliedOrdinal,
                    UnitFollowQueueCount = connection.UnitFollowClientMovementSamples.Count,
                    UnitFollowFrontOrdinal = connection.UnitFollowClientMovementSamples.Count > 0 ? connection.UnitFollowClientMovementSamples.Peek().Ordinal : null,
                    UnitFollowFrontApplyTick = connection.UnitFollowClientMovementSamples.Count > 0 ? connection.UnitFollowClientMovementSamples.Peek().SimulationApplyTick : null,
                    UnitFollowFrontMoveType = connection.UnitFollowClientMovementSamples.Count > 0 ? connection.UnitFollowClientMovementSamples.Peek().MoveType : null,
                    UnitFollowFrontX = connection.UnitFollowClientMovementSamples.Count > 0 ? connection.UnitFollowClientMovementSamples.Peek().X : null,
                    UnitFollowFrontY = connection.UnitFollowClientMovementSamples.Count > 0 ? connection.UnitFollowClientMovementSamples.Peek().Y : null,
                    UnitFollowX = connection.UnitFollowClientPosFixedX,
                    UnitFollowY = connection.UnitFollowClientPosFixedY,
                    HasActiveUseTarget = connection.HasActiveUseTarget,
                    ActiveUseTargetId = connection.ActiveUseTargetId,
                    ActiveUseTargetFlags = connection.ActiveUseTargetFlags,
                    ActiveUseTargetSessionId = connection.ActiveUseTargetSessionId,
                    ActiveUseTargetEvaluationTick = connection.ActiveUseTargetInitUseEvaluationTick,
                    HasPendingUseTarget = connection.HasPendingUseTargetAction,
                    PendingUseTargetReceivedTick = connection.PendingUseTargetReceivedTick,
                    UsePositionActive = connection.UsePositionActionMirrored,
                    UsePositionApplyTick = connection.UsePositionActionApplyTick,
                    UsePositionBusyUntilTick = connection.UsePositionActionBusyUntilTick
                });
            }
            for (int rootOrdinal = 0; rootOrdinal < rootOrder.Count; rootOrdinal++)
            {
                uint entityId = rootOrder[rootOrdinal];
                SimulationParityTickEntity entity = CaptureSimulationParityTickEntity(entityId, rootOrdinal, connections);
                if (entity == null)
                    continue;
                entities.Add(entity);
                if (!string.IsNullOrWhiteSpace(entity.InstanceKey) && instanceSet.Add(entity.InstanceKey))
                    instanceOrder.Add(entity.InstanceKey);
            }
            var subEntityOrder = new List<string>();
            foreach (ClientSubEntityEntry entry in CombatRuntime.Instance.GetClientSubEntityOrderSnapshot())
                subEntityOrder.Add($"{entry.Order}:{entry.Kind}:{entry.LocalId}:{entry.InstanceKey}");
            var rng = new List<SimulationParityTickRng>();
            foreach (string instanceKey in instanceOrder)
            {
                rng.Add(new SimulationParityTickRng
                {
                    InstanceKey = instanceKey,
                    Seed = CombatRuntime.Instance.GetRoomSeedForInstance(instanceKey),
                    Position = CombatRuntime.Instance.GetRoomRngPosForInstance(instanceKey)
                });
            }
            string inputHash = HashSimulationParityTickInputs(inputs);
            string stateHash = HashSimulationParityTickState(rootOrder, subEntityOrder, entities, rng);
            SimulationParityLedger.EnqueueTickDigest(
                simulationTick,
                rootOrder,
                subEntityOrder,
                inputs,
                entities,
                rng,
                inputHash,
                stateHash);
        }

        private SimulationParityTickEntity CaptureSimulationParityTickEntity(uint entityId, int rootOrdinal, List<RRConnection> connections)
        {
            if (CombatRuntime.Instance.IsPlayerEntity(entityId))
            {
                CombatPlayer player = CombatRuntime.Instance.GetPlayer(entityId);
                if (player == null)
                    return null;
                RRConnection owner = FindSimulationParityPlayerOwner(connections, entityId);
                PlayerState state = player.PlayerState;
                string action = owner?.UsePositionActionMirrored == true
                    ? "use-position"
                    : owner?.HasActiveUseTarget == true
                        ? "use-target"
                        : owner?.HasPendingUseTargetAction == true
                            ? "pending-use-target"
                            : "none";
                return new SimulationParityTickEntity
                {
                    RootOrdinal = rootOrdinal,
                    EntityId = entityId,
                    Kind = "player",
                    InstanceKey = RoomRuntime.NormalizeInstanceKey(player.InstanceKey),
                    ChildOrder = "Manipulators,Equipment,UnitContainer,Modifiers,Skills,UnitBehavior,Unit",
                    X = player.ClientSimulationPosFixedX,
                    Y = player.ClientSimulationPosFixedY,
                    Z = player.ClientSimulationPosFixedZ,
                    Heading = owner?.HasUnitFollowClientPosition == true
                        ? owner.UnitFollowClientHeadingFixed
                        : owner?.HasReflectedAvatarPosition == true
                            ? owner.ReflectedAvatarHeadingFixed
                            : owner?.PlayerHeadingFixed,
                    Hp = state?.CurrentHPWire,
                    Mana = state?.CurrentManaWire,
                    HpCooldown = state?.HealthRegenCooldownTicks,
                    ManaCooldown = state?.ManaRegenCooldownTicks,
                    TargetEntityId = owner?.HasActiveUseTarget == true ? owner.ActiveUseTargetId : null,
                    Action = action,
                    PendingAction = owner?.HasPendingUseTargetAction == true ? "use-target" : null,
                    MoverMode = player.ClientSimulationMoverMode,
                    MovingThisFrame = player.ClientSimulationMovingThisFrame,
                    Alive = player.IsAlive
                };
            }
            if (CombatRuntime.Instance.IsMonsterEntity(entityId))
            {
                Monster monster = CombatRuntime.Instance.GetMonster(entityId);
                if (monster == null)
                    return null;
                MonsterBehavior2Snapshot? behavior = monster.Behavior != null ? monster.Behavior.GetSnapshot() : null;
                CombatRuntime.Instance.GetMonsterRegenCooldownTicks(entityId, out ushort hpCooldown, out ushort manaCooldown);
                return new SimulationParityTickEntity
                {
                    RootOrdinal = rootOrdinal,
                    EntityId = entityId,
                    Kind = "monster",
                    InstanceKey = RoomRuntime.NormalizeInstanceKey(monster.InstanceKey),
                    ChildOrder = "MonsterBehavior2,Skills,Manipulators,Modifiers,Unit",
                    X = monster.PosFixedX,
                    Y = monster.PosFixedY,
                    Z = monster.PosFixedZ,
                    Heading = monster.HeadingFixed,
                    Hp = monster.CurrentHPWire,
                    Mana = monster.CurrentManaWire,
                    HpCooldown = hpCooldown,
                    ManaCooldown = manaCooldown,
                    TargetEntityId = monster.TargetId,
                    Action = behavior?.CurrentAction.ToString(),
                    AlternateAction = behavior?.AlternateAction.ToString(),
                    PendingAction = behavior?.PendingAction.ToString(),
                    ActionGeneration = behavior?.ActionGeneration,
                    TopState = behavior?.TopState,
                    StateClock = behavior?.StateMachine?.Clock,
                    FollowState = behavior?.FollowState,
                    NextAttackTick = monster.NextAttackTick,
                    AttackStartTick = monster.AttackStartTick,
                    AttackEndTick = monster.AttackEndTick,
                    SkillCooldownTicks = monster.PrimaryActiveSkillCooldownRemainingTicks,
                    MoverMode = monster.UnitMoverPathOwner,
                    MovingThisFrame = monster.UnitMoverMovingThisFrame,
                    Alive = monster.IsAlive,
                    Aggro = monster.AggroTriggered,
                    AttackPending = monster.AttackPending
                };
            }
            if (BlingGnomeLockstepRuntime.Instance.IsEntity(entityId)
                && BlingGnomeLockstepRuntime.Instance.TryGetSnapshot(entityId, out BlingGnomeLockstepSnapshot gnome))
            {
                string action = gnome.RetrieveActive
                    ? "retrieve-item"
                    : gnome.ConvertActive
                        ? "convert-items-to-gold"
                        : "follow";
                return new SimulationParityTickEntity
                {
                    RootOrdinal = rootOrdinal,
                    EntityId = entityId,
                    Kind = "friendly-bling",
                    InstanceKey = RoomRuntime.NormalizeInstanceKey(gnome.InstanceKey),
                    X = gnome.FixedX,
                    Y = gnome.FixedY,
                    Z = gnome.FixedZ,
                    Heading = gnome.HeadingFixed,
                    Action = action,
                    TopState = gnome.BlingState,
                    StateClock = gnome.BlingCounter,
                    FollowState = gnome.FollowState,
                    MoverMode = gnome.FollowMoverMode,
                    MovingThisFrame = gnome.FollowMoverMovingThisFrame,
                    Alive = gnome.Initialized && !gnome.UnSpawnRemovalPending
                };
            }
            if (CombatRuntime.Instance.TryGetItemObjectInstanceKey(entityId, out string itemInstanceKey))
            {
                return new SimulationParityTickEntity
                {
                    RootOrdinal = rootOrdinal,
                    EntityId = entityId,
                    Kind = "item",
                    InstanceKey = RoomRuntime.NormalizeInstanceKey(itemInstanceKey)
                };
            }
            if (CombatRuntime.Instance.TryGetEncounterObjectInstanceKey(entityId, out string encounterInstanceKey))
            {
                return new SimulationParityTickEntity
                {
                    RootOrdinal = rootOrdinal,
                    EntityId = entityId,
                    Kind = "encounter",
                    InstanceKey = RoomRuntime.NormalizeInstanceKey(encounterInstanceKey)
                };
            }
            return new SimulationParityTickEntity
            {
                RootOrdinal = rootOrdinal,
                EntityId = entityId,
                Kind = "unknown"
            };
        }

        private static string HashSimulationParityTickInputs(List<SimulationParityTickInput> inputs)
        {
            ulong hash = 14695981039346656037UL;
            foreach (SimulationParityTickInput input in inputs)
            {
                MixSimulationParityHash(ref hash, input.ConnectionId);
                MixSimulationParityHash(ref hash, input.EntityId);
                MixSimulationParityHash(ref hash, input.InstanceKey);
                MixSimulationParityHash(ref hash, input.ClientEntityUpdateSerial);
                MixSimulationParityHash(ref hash, input.ReflectedReceivedOrdinal);
                MixSimulationParityHash(ref hash, input.ReflectedAppliedOrdinal);
                MixSimulationParityHash(ref hash, input.ReflectedReadyCount);
                MixSimulationParityHash(ref hash, input.ReflectedFrontOrdinal ?? ulong.MaxValue);
                MixSimulationParityHash(ref hash, input.ReflectedFrontMoveType ?? byte.MaxValue);
                MixSimulationParityHash(ref hash, input.ReflectedFrontX ?? int.MinValue);
                MixSimulationParityHash(ref hash, input.ReflectedFrontY ?? int.MinValue);
                MixSimulationParityHash(ref hash, input.ReflectedX);
                MixSimulationParityHash(ref hash, input.ReflectedY);
                MixSimulationParityHash(ref hash, input.OwnerAckReceivedOrdinal);
                MixSimulationParityHash(ref hash, input.OwnerAckAppliedOrdinal);
                MixSimulationParityHash(ref hash, input.OwnerAckQueueCount);
                MixSimulationParityHash(ref hash, input.OwnerAckFrontOrdinal ?? ulong.MaxValue);
                MixSimulationParityHash(ref hash, input.OwnerAckFrontApplyTick ?? uint.MaxValue);
                MixSimulationParityHash(ref hash, input.OwnerAckFrontMoveType ?? byte.MaxValue);
                MixSimulationParityHash(ref hash, input.OwnerAckFrontX ?? int.MinValue);
                MixSimulationParityHash(ref hash, input.OwnerAckFrontY ?? int.MinValue);
                MixSimulationParityHash(ref hash, input.OwnerAckX);
                MixSimulationParityHash(ref hash, input.OwnerAckY);
                MixSimulationParityHash(ref hash, input.UnitFollowReceivedOrdinal);
                MixSimulationParityHash(ref hash, input.UnitFollowAppliedOrdinal);
                MixSimulationParityHash(ref hash, input.UnitFollowQueueCount);
                MixSimulationParityHash(ref hash, input.UnitFollowFrontOrdinal ?? ulong.MaxValue);
                MixSimulationParityHash(ref hash, input.UnitFollowFrontApplyTick ?? uint.MaxValue);
                MixSimulationParityHash(ref hash, input.UnitFollowFrontMoveType ?? byte.MaxValue);
                MixSimulationParityHash(ref hash, input.UnitFollowFrontX ?? int.MinValue);
                MixSimulationParityHash(ref hash, input.UnitFollowFrontY ?? int.MinValue);
                MixSimulationParityHash(ref hash, input.UnitFollowX);
                MixSimulationParityHash(ref hash, input.UnitFollowY);
                MixSimulationParityHash(ref hash, input.HasActiveUseTarget);
                MixSimulationParityHash(ref hash, input.ActiveUseTargetId);
                MixSimulationParityHash(ref hash, input.ActiveUseTargetFlags);
                MixSimulationParityHash(ref hash, input.ActiveUseTargetSessionId);
                MixSimulationParityHash(ref hash, input.ActiveUseTargetEvaluationTick);
                MixSimulationParityHash(ref hash, input.HasPendingUseTarget);
                MixSimulationParityHash(ref hash, input.PendingUseTargetReceivedTick);
                MixSimulationParityHash(ref hash, input.UsePositionActive);
                MixSimulationParityHash(ref hash, input.UsePositionApplyTick);
                MixSimulationParityHash(ref hash, input.UsePositionBusyUntilTick);
            }
            return hash.ToString("X16");
        }

        private static string HashSimulationParityTickState(
            List<uint> rootOrder,
            List<string> subEntityOrder,
            List<SimulationParityTickEntity> entities,
            List<SimulationParityTickRng> rng)
        {
            ulong hash = 14695981039346656037UL;
            foreach (uint entityId in rootOrder)
                MixSimulationParityHash(ref hash, entityId);
            foreach (string entry in subEntityOrder)
                MixSimulationParityHash(ref hash, entry);
            foreach (SimulationParityTickEntity entity in entities)
            {
                MixSimulationParityHash(ref hash, entity.RootOrdinal);
                MixSimulationParityHash(ref hash, entity.EntityId);
                MixSimulationParityHash(ref hash, entity.Kind);
                MixSimulationParityHash(ref hash, entity.InstanceKey);
                MixSimulationParityHash(ref hash, entity.ChildOrder);
                MixSimulationParityHash(ref hash, entity.X ?? int.MinValue);
                MixSimulationParityHash(ref hash, entity.Y ?? int.MinValue);
                MixSimulationParityHash(ref hash, entity.Z ?? int.MinValue);
                MixSimulationParityHash(ref hash, entity.Heading ?? int.MinValue);
                MixSimulationParityHash(ref hash, entity.Hp ?? uint.MaxValue);
                MixSimulationParityHash(ref hash, entity.Mana ?? uint.MaxValue);
                MixSimulationParityHash(ref hash, entity.HpCooldown ?? int.MinValue);
                MixSimulationParityHash(ref hash, entity.ManaCooldown ?? int.MinValue);
                MixSimulationParityHash(ref hash, entity.TargetEntityId ?? uint.MaxValue);
                MixSimulationParityHash(ref hash, entity.Action);
                MixSimulationParityHash(ref hash, entity.AlternateAction);
                MixSimulationParityHash(ref hash, entity.PendingAction);
                MixSimulationParityHash(ref hash, entity.ActionGeneration ?? int.MinValue);
                MixSimulationParityHash(ref hash, entity.TopState ?? int.MinValue);
                MixSimulationParityHash(ref hash, entity.StateClock ?? int.MinValue);
                MixSimulationParityHash(ref hash, entity.FollowState ?? int.MinValue);
                MixSimulationParityHash(ref hash, entity.NextAttackTick ?? int.MinValue);
                MixSimulationParityHash(ref hash, entity.AttackStartTick ?? int.MinValue);
                MixSimulationParityHash(ref hash, entity.AttackEndTick ?? int.MinValue);
                MixSimulationParityHash(ref hash, entity.SkillCooldownTicks ?? int.MinValue);
                MixSimulationParityHash(ref hash, entity.MoverMode ?? int.MinValue);
                MixSimulationParityHash(ref hash, entity.MovingThisFrame ?? false);
                MixSimulationParityHash(ref hash, entity.Alive ?? false);
                MixSimulationParityHash(ref hash, entity.Aggro ?? false);
                MixSimulationParityHash(ref hash, entity.AttackPending ?? false);
            }
            foreach (SimulationParityTickRng entry in rng)
            {
                MixSimulationParityHash(ref hash, entry.InstanceKey);
                MixSimulationParityHash(ref hash, entry.Seed);
                MixSimulationParityHash(ref hash, entry.Position);
            }
            return hash.ToString("X16");
        }

        private static void MixSimulationParityHash(ref ulong hash, string value)
        {
            if (value == null)
            {
                MixSimulationParityHash(ref hash, ulong.MaxValue);
                return;
            }
            MixSimulationParityHash(ref hash, value.Length);
            foreach (char character in value)
                MixSimulationParityHash(ref hash, character);
        }

        private static void MixSimulationParityHash(ref ulong hash, bool value)
        {
            MixSimulationParityHash(ref hash, value ? 1UL : 0UL);
        }

        private static void MixSimulationParityHash(ref ulong hash, long value)
        {
            MixSimulationParityHash(ref hash, unchecked((ulong)value));
        }

        private static void MixSimulationParityHash(ref ulong hash, ulong value)
        {
            for (int index = 0; index < 8; index++)
            {
                hash ^= value & 0xFFUL;
                hash *= 1099511628211UL;
                value >>= 8;
            }
        }

        private SimulationParityServerView CaptureSimulationParityView(RRConnection viewer, List<RRConnection> connections, uint simulationTick)
        {
            string viewerInstance = RoomRuntime.NormalizeInstanceKey(GetInstanceZoneKey(viewer));
            var view = new SimulationParityServerView
            {
                Viewer = viewer.LoginName ?? viewer.ConnId.ToString(),
                ConnectionId = viewer.ConnId,
                ZoneId = viewer.CurrentZoneId,
                Zone = viewer.CurrentZoneGcType ?? viewer.CurrentZoneName ?? string.Empty,
                InstanceKey = viewerInstance,
                RoomSeed = CombatRuntime.Instance.GetRoomSeedForInstance(viewerInstance),
                ClientEntityUpdateSerial = viewer.LastClientEntityUpdateSerial,
                SimulationTick = simulationTick,
                Units = new List<SimulationParityUnit>()
            };
            foreach (uint entityId in CombatRuntime.Instance.GetEntityUpdateOrderSnapshot(simulationTick))
            {
                if (CombatRuntime.Instance.IsMonsterEntity(entityId))
                {
                    Monster monster = CombatRuntime.Instance.GetMonster(entityId);
                    if (monster != null && SameSimulationParityInstance(viewerInstance, monster.InstanceKey))
                        view.Units.Add(CaptureSimulationParityMonster(viewer, monster, simulationTick));
                    continue;
                }
                if (CombatRuntime.Instance.IsPlayerEntity(entityId))
                {
                    CombatPlayer player = CombatRuntime.Instance.GetPlayer(entityId);
                    RRConnection owner = FindSimulationParityPlayerOwner(connections, entityId);
                    if (player != null && owner != null && IsSameMovementRuntime(viewer, owner))
                        view.Units.Add(CaptureSimulationParityPlayer(viewer, owner, player));
                    continue;
                }
                if (BlingGnomeLockstepRuntime.Instance.IsEntity(entityId)
                    && BlingGnomeLockstepRuntime.Instance.TryGetSnapshot(entityId, out BlingGnomeLockstepSnapshot gnome)
                    && SameSimulationParityInstance(viewerInstance, gnome.InstanceKey))
                {
                    view.Units.Add(CaptureSimulationParityBling(viewer, gnome, simulationTick));
                    continue;
                }
                if (entityId <= ushort.MaxValue
                    && CombatRuntime.Instance.IsItemObjectEntity(entityId)
                    && _droppedItems.TryGetValue((ushort)entityId, out DroppedItemInfo item)
                    && item != null
                    && SameSimulationParityInstance(viewerInstance, item.RuntimeInstanceKey))
                {
                    view.Units.Add(CaptureSimulationParityItem(item, (ushort)entityId));
                }
            }
            if (_zoneNPCs.TryGetValue(viewer.CurrentZoneId, out List<ZoneNPC> npcs))
            {
                foreach (ZoneNPC npc in npcs)
                {
                    if (npc != null)
                        view.Units.Add(CaptureSimulationParityNpc(viewer, viewer.CurrentZoneId, npc, viewerInstance));
                }
            }
            return view;
        }

        private static bool SameSimulationParityInstance(string left, string right)
        {
            return string.Equals(
                RoomRuntime.NormalizeInstanceKey(left),
                RoomRuntime.NormalizeInstanceKey(right),
                StringComparison.OrdinalIgnoreCase);
        }

        private static RRConnection FindSimulationParityPlayerOwner(List<RRConnection> connections, uint entityId)
        {
            foreach (RRConnection connection in connections)
            {
                if (connection?.Avatar != null && connection.Avatar.Id == entityId)
                    return connection;
            }
            return null;
        }

        private SimulationParityUnit CaptureSimulationParityPlayer(RRConnection viewer, RRConnection owner, CombatPlayer player)
        {
            bool localOwner = viewer.ConnId == owner.ConnId;
            uint? clientEntityId = null;
            bool knownToViewer = false;
            if (localOwner && owner.Avatar != null)
            {
                clientEntityId = owner.Avatar.Id;
                knownToViewer = true;
            }
            else if (_remoteAvatarIds.TryGetValue(viewer.LoginName ?? string.Empty, out Dictionary<string, ushort> remoteAvatars)
                && remoteAvatars.TryGetValue(owner.LoginName ?? string.Empty, out ushort remoteAvatarId)
                && remoteAvatarId != 0)
            {
                clientEntityId = remoteAvatarId;
                knownToViewer = true;
            }
            ResolveRemoteAvatarWorldPosition(owner, out int fixedX, out int fixedY, out int fixedZ, out int headingFixed);
            PlayerState state = player.PlayerState;
            RemotePeerActionState peerAction = localOwner ? null : GetRemotePeerActionState(viewer, owner, false);
            var reflectedFront = owner.ReadyReflectedAvatarMovementSamples.Count > 0
                ? owner.ReadyReflectedAvatarMovementSamples.Peek()
                : ((byte MoveType, int Heading, int X, int Y, bool RequiresAdmission, bool TerminalRestartAdmission, ulong Ordinal)?)null;
            return new SimulationParityUnit
            {
                EntityId = clientEntityId,
                ServerEntityId = player.EntityId,
                StableKey = $"player:{owner.LoginName ?? owner.ConnId.ToString()}",
                Kind = "player",
                Name = owner.LoginName ?? player.Name,
                GcType = owner.AvatarGcType,
                InstanceKey = player.InstanceKey,
                KnownToViewer = knownToViewer,
                PositionModel = localOwner ? "owner-prediction-input" : "remote-state-sync",
                Position = Position(fixedX, fixedY, fixedZ, headingFixed),
                Stance = MissingStance(),
                Resources = new SimulationParityResources
                {
                    MaxHp = state?.MaxHPWire,
                    MaxMana = state?.MaxManaWire,
                    Hp = state?.CurrentHPWire,
                    Mana = state?.CurrentManaWire,
                    HpCooldown = state?.HealthRegenCooldownTicks,
                    ManaCooldown = state?.ManaRegenCooldownTicks
                },
                BehaviorState = new SimulationParityBehaviorState(),
                ServerState = new
                {
                    localOwner,
                    owner.IsConnected,
                    owner.IsSpawned,
                    owner.TickUpdatesActive,
                    owner.CurrentZoneId,
                    owner.CurrentZoneName,
                    owner.CurrentZoneGcType,
                    owner.PendingPortalTransition,
                    owner.PendingPortalTransitionReflected,
                    owner.PortalClientEntityEpochClosing,
                    owner.PendingPortalTransitionResponseWriterSerial,
                    owner.PendingPortalTransitionTargetZone,
                    owner.SessionID,
                    owner.MovementGeneration,
                    owner.HasLivePlayerPosition,
                    owner.HasReflectedAvatarPosition,
                    reflectedPosition = owner.HasReflectedAvatarPosition
                        ? Position(owner.ReflectedAvatarPosFixedX, owner.ReflectedAvatarPosFixedY, owner.ReflectedAvatarPosFixedZ, owner.ReflectedAvatarHeadingFixed)
                        : null,
                    owner.ReflectedAvatarMovingThisFrame,
                    owner.ReflectedAvatarUnitMoverMode,
                    owner.OwnerUnitBehaviorMoverMode,
                    player.ClientSimulationMoverMode,
                    player.ClientSimulationMovingThisFrame,
                    clientSimulationPosition = Position(
                        player.ClientSimulationPosFixedX,
                        player.ClientSimulationPosFixedY,
                        player.ClientSimulationPosFixedZ,
                        owner.ReflectedAvatarHeadingFixed),
                    unitFinderMembershipPosition = new
                    {
                        x = player.UnitFinderMembershipPosFixedX,
                        y = player.UnitFinderMembershipPosFixedY
                    },
                    reflectedReadyCount = owner.ReadyReflectedAvatarMovementSamples.Count,
                    reflectedHeldCount = owner.HeldReflectedAvatarMovementSamples.Count,
                    reflectedAdmittedCount = owner.AdmittedReflectedAvatarMovementSamples.Count,
                    reflectedPendingCount = owner.PendingReflectedAvatarMovementSamples.Count,
                    reflectedStreamAdmitted = owner.ReflectedAvatarMovementStreamAdmitted,
                    reflectedTerminalRestartPending = owner.ReflectedAvatarMovementTerminalRestartPending,
                    reflectedPostAdmissionBarrierPending = owner.ReflectedAvatarMovementPostAdmissionBarrierPending,
                    reflectedIdleBatchBarrierPending = owner.ReflectedAvatarMovementIdleBatchBarrierPending,
                    reflectedReceivedOrdinal = owner.ReflectedAvatarMovementReceivedOrdinal,
                    reflectedAppliedOrdinal = owner.ReflectedAvatarMovementAppliedOrdinal,
                    ownerAckFollowClientReceivedOrdinal = owner.OwnerAckFollowClientReceivedOrdinal,
                    ownerAckFollowClientAppliedOrdinal = owner.OwnerAckFollowClientAppliedOrdinal,
                    ownerAckFollowClientPendingRecords = owner.OwnerAckFollowClientReceivedOrdinal - owner.OwnerAckFollowClientAppliedOrdinal,
                    owner.HasOwnerAckFollowClientPosition,
                    ownerAckFollowClientPosition = owner.HasOwnerAckFollowClientPosition
                        ? Position(owner.OwnerAckFollowClientPosFixedX, owner.OwnerAckFollowClientPosFixedY, owner.OwnerAckFollowClientPosFixedZ, owner.OwnerAckFollowClientHeadingFixed)
                        : null,
                    owner.OwnerAckFollowClientMovingThisFrame,
                    player.OwnerAckFollowClientMoverMode,
                    ownerAckFollowClientQueueCount = owner.OwnerAckFollowClientMovementSamples.Count,
                    ownerAckFollowClientFront = owner.OwnerAckFollowClientMovementSamples.Count > 0
                        ? new
                        {
                            owner.OwnerAckFollowClientMovementSamples.Peek().MoveType,
                            owner.OwnerAckFollowClientMovementSamples.Peek().Heading,
                            owner.OwnerAckFollowClientMovementSamples.Peek().X,
                            owner.OwnerAckFollowClientMovementSamples.Peek().Y,
                            owner.OwnerAckFollowClientMovementSamples.Peek().SimulationApplyTick,
                            owner.OwnerAckFollowClientMovementSamples.Peek().Ordinal
                        }
                        : null,
                    ownerAckFollowClientBack = owner.OwnerAckFollowClientMovementSamples.Count > 0
                        ? new
                        {
                            owner.OwnerAckFollowClientMovementSamples.Last().MoveType,
                            owner.OwnerAckFollowClientMovementSamples.Last().Heading,
                            owner.OwnerAckFollowClientMovementSamples.Last().X,
                            owner.OwnerAckFollowClientMovementSamples.Last().Y,
                            owner.OwnerAckFollowClientMovementSamples.Last().SimulationApplyTick,
                            owner.OwnerAckFollowClientMovementSamples.Last().Ordinal
                        }
                        : null,
                    unitFollowClientReceivedOrdinal = owner.UnitFollowClientReceivedOrdinal,
                    unitFollowClientAppliedOrdinal = owner.UnitFollowClientAppliedOrdinal,
                    unitFollowClientPendingRecords = owner.UnitFollowClientReceivedOrdinal - owner.UnitFollowClientAppliedOrdinal,
                    owner.HasUnitFollowClientPosition,
                    unitFollowClientPosition = owner.HasUnitFollowClientPosition
                        ? Position(owner.UnitFollowClientPosFixedX, owner.UnitFollowClientPosFixedY, owner.UnitFollowClientPosFixedZ, owner.UnitFollowClientHeadingFixed)
                        : null,
                    owner.UnitFollowClientMovingThisFrame,
                    owner.UnitFollowClientMoverMode,
                    unitFollowClientQueueCount = owner.UnitFollowClientMovementSamples.Count,
                    unitFollowClientFront = owner.UnitFollowClientMovementSamples.Count > 0
                        ? new
                        {
                            owner.UnitFollowClientMovementSamples.Peek().MoveType,
                            owner.UnitFollowClientMovementSamples.Peek().Heading,
                            owner.UnitFollowClientMovementSamples.Peek().X,
                            owner.UnitFollowClientMovementSamples.Peek().Y,
                            owner.UnitFollowClientMovementSamples.Peek().SimulationApplyTick,
                            owner.UnitFollowClientMovementSamples.Peek().Ordinal
                        }
                        : null,
                    unitFollowClientBack = owner.UnitFollowClientMovementSamples.Count > 0
                        ? new
                        {
                            owner.UnitFollowClientMovementSamples.Last().MoveType,
                            owner.UnitFollowClientMovementSamples.Last().Heading,
                            owner.UnitFollowClientMovementSamples.Last().X,
                            owner.UnitFollowClientMovementSamples.Last().Y,
                            owner.UnitFollowClientMovementSamples.Last().SimulationApplyTick,
                            owner.UnitFollowClientMovementSamples.Last().Ordinal
                        }
                        : null,
                    predictedLocation2D = new
                    {
                        x = player.PredictedLocation2DFixedX,
                        y = player.PredictedLocation2DFixedY
                    },
                    followTargetCurrent = new
                    {
                        x = player.MonsterFollowTargetPosFixedX,
                        y = player.MonsterFollowTargetPosFixedY,
                        z = player.MonsterFollowTargetPosFixedZ,
                        movingThisFrame = player.MonsterFollowTargetMovingThisFrame
                    },
                    actionTargetCurrent = new
                    {
                        x = player.MonsterActionTargetPosFixedX,
                        y = player.MonsterActionTargetPosFixedY,
                        z = player.MonsterActionTargetPosFixedZ,
                        moverMode = player.MonsterActionTargetMoverMode
                    },
                    player.OwnerAckFollowClientTouchingMonsters,
                    player.OwnerAckFollowClientTouchingMonsterEntityId,
                    player.OwnerAckFollowClientEffectiveSpeedModF32,
                    player.OwnerAckFollowClientEffectiveSpeedF32,
                    player.OwnerAckFollowClientSpeedPerFrameF32,
                    owner.UseTargetMovingUnitFollowClientWaitActive,
                    owner.UseTargetMovingUnitFollowClientWaitThroughOrdinal,
                    reflectedFront = reflectedFront.HasValue
                        ? new
                        {
                            reflectedFront.Value.MoveType,
                            reflectedFront.Value.Heading,
                            reflectedFront.Value.X,
                            reflectedFront.Value.Y,
                            reflectedFront.Value.RequiresAdmission,
                            reflectedFront.Value.TerminalRestartAdmission,
                            reflectedFront.Value.Ordinal
                        }
                        : null,
                    speedF32 = state?.SpeedF32,
                    speedMod = state?.SpeedMod,
                    speedPerFrame = state != null ? (int?)UnitMover.CacheSpeedPerFrame(state.SpeedF32, state.SpeedMod, out _) : null,
                    owner.UseTargetMovingActive,
                    owner.UseTargetMovingActionMirrored,
                    owner.UseTargetMovingHasFixedState,
                    owner.UseTargetMovingMoverActive,
                    owner.UseTargetMovingMoverStarted,
                    owner.UseTargetMovingUsing,
                    owner.UseTargetMovingAdmissionTick,
                    owner.UseTargetMovingLastAdvancedTick,
                    owner.UseTargetMovingPathRequestId,
                    owner.UseTargetMovingPathRequestInstanceKey,
                    owner.UseTargetMovingPathRequestTick,
                    owner.UseTargetMovingPathReadyTick,
                    owner.UseTargetMovingPathRetryCountdown,
                    owner.UseTargetMovingPathTargetFixedX,
                    owner.UseTargetMovingPathTargetFixedY,
                    useTargetMovingPathCount = owner.UseTargetMovingPathFixed.Count,
                    owner.UseTargetMovingPathIndex,
                    useTargetMovingPathWaypoint = owner.UseTargetMovingPathIndex >= 0
                        && owner.UseTargetMovingPathIndex < owner.UseTargetMovingPathFixed.Count
                        ? new
                        {
                            x = owner.UseTargetMovingPathFixed[owner.UseTargetMovingPathIndex].X,
                            y = owner.UseTargetMovingPathFixed[owner.UseTargetMovingPathIndex].Y
                        }
                        : null,
                    useTargetMovingPathFirst = owner.UseTargetMovingPathFixed.Count > 0
                        ? new
                        {
                            x = owner.UseTargetMovingPathFixed[0].X,
                            y = owner.UseTargetMovingPathFixed[0].Y
                        }
                        : null,
                    useTargetMovingPathLast = owner.UseTargetMovingPathFixed.Count > 0
                        ? new
                        {
                            x = owner.UseTargetMovingPathFixed[owner.UseTargetMovingPathFixed.Count - 1].X,
                            y = owner.UseTargetMovingPathFixed[owner.UseTargetMovingPathFixed.Count - 1].Y
                        }
                        : null,
                    useTargetMovingPosition = owner.UseTargetMovingHasFixedState
                        ? Position(owner.UseTargetMovingFixedX, owner.UseTargetMovingFixedY, owner.UseTargetMovingFixedZ, owner.UseTargetMovingHeadingFixed)
                        : null,
                    droppedItemActivation = owner.PendingDroppedItemTargetEntityId != 0
                        ? new
                        {
                            owner.PendingDroppedItemComponentId,
                            owner.PendingDroppedItemTargetEntityId,
                            owner.PendingDroppedItemResponseId,
                            owner.PendingDroppedItemSessionId,
                            owner.PendingDroppedItemInstanceKey
                        }
                        : null,
                    player.HasActiveClientAttack,
                    player.ActiveClientAttackTargetId,
                    peerActionActive = peerAction?.ActionActive,
                    peerActionOpcode = peerAction?.ActionOpcode,
                    peerActionSessionId = peerAction?.ActionSessionId,
                    peerHasSourceUseTargetSession = peerAction?.HasSourceUseTargetSession,
                    peerSourceUseTargetSessionId = peerAction?.SourceUseTargetSessionId,
                    peerActionTerminationTick = peerAction?.ActionTerminationTick,
                    peerAwaitingOwnerMovement = peerAction?.AwaitingOwnerMovement,
                    peerClientControlEnabled = peerAction?.ClientControlEnabled,
                    peerFollowingClient = peerAction?.FollowingClient,
                    peerMoverSession = peerAction != null ? (byte?)GetRemoteBehaviorSession(viewer, owner) : null,
                    peerBehaviorGeneration = peerAction?.BehaviorGeneration,
                    peerPendingActionActive = peerAction?.PendingActionActive,
                    peerPendingActionOpcode = peerAction?.PendingActionOpcode,
                    peerPendingActionSessionId = peerAction?.PendingActionSessionId,
                    peerPendingTargetEntityId = peerAction?.PendingTargetEntityId,
                    peerLastQueuedTargetActionTick = peerAction?.LastQueuedTargetActionTick
                }
            };
        }

        private SimulationParityUnit CaptureSimulationParityMonster(RRConnection viewer, Monster monster, uint simulationTick)
        {
            uint viewerEntityId = viewer.Avatar != null ? viewer.Avatar.Id : 0u;
            CombatRuntime.Instance.TryPeekMonsterClientVisibleMoverFixed(
                monster,
                viewerEntityId,
                out int fixedX,
                out int fixedY,
                out int fixedZ,
                out int targetFixedX,
                out int targetFixedY,
                out int headingFixed,
                out bool moving);
            bool knownToViewer = _monsterSpawnSentByConn.TryGetValue(viewer.ConnId, out HashSet<uint> sentMonsters)
                && sentMonsters.Contains(monster.EntityId);
            WanderStateSnapshot wander = default;
            bool hasWander = WanderSimulator.Instance.TryGetSnapshot(monster.EntityId, out wander);
            MonsterBehavior2Snapshot? behavior = monster.Behavior != null ? monster.Behavior.GetSnapshot() : null;
            bool includeDetail = simulationTick % 30u == 0u
                || monster.TargetId != 0
                || monster.AttackPending
                || monster.UnitMoverPathOwner != 0
                || (behavior.HasValue
                    && behavior.Value.CurrentAction != MonsterBehavior2.ActionSlot.Wander
                    && behavior.Value.CurrentAction != MonsterBehavior2.ActionSlot.Spawn);
            CombatRuntime.Instance.GetMonsterRegenCooldownTicks(monster.EntityId, out ushort healthCooldown, out ushort manaCooldown);
            int speedF32 = monster.MoveSpeedF32 > 0 ? monster.MoveSpeedF32 : monster.WalkSpeedF32;
            int speedPerFrameF32 = UnitMover.CacheSpeedPerFrame(speedF32, monster.SpeedMod, out int effectiveSpeedF32);
            int pathIndex = monster.UnitMoverPathIndex;
            bool hasPathWaypoint = pathIndex >= 0 && pathIndex < monster.UnitMoverPathFixed.Count;
            int moverMode = CombatRuntime.Instance.GetMonsterUnitMoverMode(monster);
            int moverTargetFixedX = monster.UnitMoverPathOwner != 0
                ? monster.UnitMoverPathTargetFixedX
                : hasWander && wander.HasTarget ? wander.TargetFixedX : 0;
            int moverTargetFixedY = monster.UnitMoverPathOwner != 0
                ? monster.UnitMoverPathTargetFixedY
                : hasWander && wander.HasTarget ? wander.TargetFixedY : 0;
            return new SimulationParityUnit
            {
                EntityId = monster.EntityId,
                ServerEntityId = monster.EntityId,
                StableKey = $"monster:{monster.EntityId}",
                Kind = "monster",
                Name = monster.Name,
                GcType = monster.GCType,
                InstanceKey = monster.InstanceKey,
                KnownToViewer = knownToViewer,
                PositionModel = "viewer-client-visible",
                Position = Position(fixedX, fixedY, fixedZ, headingFixed),
                Stance = MissingStance(),
                Resources = new SimulationParityResources
                {
                    MaxHp = monster.MaxHPWire,
                    MaxMana = monster.MaxManaWire,
                    Hp = monster.CurrentHPWire,
                    Mana = monster.CurrentManaWire,
                    HpCooldown = healthCooldown,
                    ManaCooldown = manaCooldown
                },
                EntityFlags = monster.EntityFlags,
                UnitFlags = monster.UnitFlags,
                SilenceAttribute = monster.SilenceAttributeValue,
                UnitState31B = monster.UnitState31B,
                ActivityGate = monster.UnitState31B,
                StockState = monster.StockUnitState,
                BehaviorState = new SimulationParityBehaviorState
                {
                    Generation = behavior.HasValue ? behavior.Value.ActionGeneration : null,
                    InterruptLocked = behavior.HasValue ? behavior.Value.CurrentActionInterruptLocked : null,
                    Primary = CaptureSimulationParityMonsterPrimaryAction(monster, behavior, moving, targetFixedX, targetFixedY),
                    Alternate = CaptureSimulationParityMonsterAlternateAction(monster, behavior),
                    Pending = CaptureSimulationParityMonsterPendingAction(monster, behavior),
                    TopStateMachine = behavior.HasValue ? StateMachine(behavior.Value.StateMachine, behavior.Value.TopState) : null,
                    FollowStateMachine = behavior.HasValue && behavior.Value.FollowActive ? StateMachine(behavior.Value.FollowStateMachine, behavior.Value.FollowState) : null,
                    Mover = new SimulationParityMoverState
                    {
                        Speed = effectiveSpeedF32,
                        SpeedPerFrame = speedPerFrameF32,
                        TurnRatePerFrame = UnitMover.TurnRatePerTickFixed(monster.TurnRateDegrees),
                        CurrentHeading = monster.ChaseHeadingInit ? monster.ChaseHeadingFixed : monster.HeadingFixed,
                        MovingThisFrame = monster.UnitMoverMovingThisFrame,
                        Mode = moverMode,
                        DesiredHeading = monster.UnitMoverDesiredHeadingInit && (moverMode != 2 || monster.UnitMoverPathOwner != 0)
                            ? monster.UnitMoverDesiredHeadingFixed
                            : null,
                        RequestedHeading = monster.UnitMoverDesiredHeadingInit && (moverMode != 2 || monster.UnitMoverPathOwner != 0)
                            ? monster.UnitMoverDesiredHeadingFixed
                            : null,
                        AngularVelocity = moverMode == 3 ? 0 : null,
                        TargetX = moverMode == 2 ? moverTargetFixedX : null,
                        TargetY = moverMode == 2 ? moverTargetFixedY : null
                    }
                },
                ServerState = includeDetail ? new
                {
                    state = monster.State.ToString(),
                    monster.IsAlive,
                    monster.AggroTriggered,
                    monster.TargetId,
                    followTargetEntityId = behavior.HasValue ? behavior.Value.FollowTargetEntityId : 0,
                    followStartSimulationTick = monster.Behavior?.FollowStartSimulationTick,
                    monster.AlertSourceEntityId,
                    monster.BehaviorAssistSourceEntityId,
                    monster.AttackPending,
                    monster.AttackClientVisible,
                    monster.AttackContactOnly,
                    monster.AttackHitResolved,
                    monster.AttackAnimationIndex,
                    monster.AttackStartTick,
                    monster.AttackCommitTick,
                    monster.AttackEndTick,
                    monster.AttackWeaponCycleFromUseTargetInterrupt,
                    monster.AttackSessionId,
                    monster.SilenceAttributeValue,
                    monster.UsePrimaryActiveSkillThisAttack,
                    monster.SelectedActiveSkillTargetEntityId,
                    monster.ActiveSkillUseCommittedThisAttack,
                    monster.ActiveSkillEffectPending,
                    monster.ActiveSkillEffectResolved,
                    monster.ActiveSkillEffectTargetEntityId,
                    monster.ActiveSkillEffectStartTick,
                    monster.ActiveSkillEffectCommitTick,
                    monster.ActiveSkillEffectEndTick,
                    primaryAttackSkill = monster.PrimaryAttackSkill != null
                        ? new
                        {
                            monster.PrimaryAttackSkill.Path,
                            monster.PrimaryAttackSkill.Id,
                            monster.PrimaryAttackSkill.SkillLevel,
                            monster.PrimaryAttackSkill.CooldownTicks,
                            monster.PrimaryAttackSkill.CooldownRemainingTicks,
                            monster.PrimaryAttackSkill.CooldownLastTick,
                            monster.PrimaryAttackSkill.DelayBeforeUseTicks,
                            monster.PrimaryAttackSkill.ManaCostWire,
                            monster.PrimaryAttackSkill.RangeF32,
                            monster.PrimaryAttackSkill.SpellUseRangeF32,
                            monster.PrimaryAttackSkill.SpellUseMinimumRangeF32,
                            monster.PrimaryAttackSkill.TargetType,
                            monster.PrimaryAttackSkill.SpellUse
                        }
                        : null,
                    selectedActiveSkill = monster.SelectedActiveSkill != null
                        ? new
                        {
                            monster.SelectedActiveSkill.Path,
                            monster.SelectedActiveSkill.Id,
                            monster.SelectedActiveSkill.SkillLevel,
                            monster.SelectedActiveSkill.CooldownTicks,
                            monster.SelectedActiveSkill.CooldownRemainingTicks,
                            monster.SelectedActiveSkill.CooldownLastTick,
                            monster.SelectedActiveSkill.DelayBeforeUseTicks,
                            monster.SelectedActiveSkill.ManaCostWire,
                            monster.SelectedActiveSkill.RangeF32,
                            monster.SelectedActiveSkill.SpellUseRangeF32,
                            monster.SelectedActiveSkill.SpellUseMinimumRangeF32,
                            monster.SelectedActiveSkill.TargetType,
                            monster.SelectedActiveSkill.SpellUse
                        }
                        : null,
                    activeSkills = monster.ActiveSkills.Select((skill, index) => new
                    {
                        index,
                        skill.Path,
                        skill.Id,
                        skill.SkillLevel,
                        skill.CooldownTicks,
                        skill.CooldownRemainingTicks,
                        skill.CooldownLastTick,
                        skill.DelayBeforeUseTicks,
                        skill.ManaCostWire,
                        skill.RangeF32,
                        skill.SpellUseRangeF32,
                        skill.SpellUseMinimumRangeF32,
                        skill.TargetType,
                        skill.SpellUse
                    }).ToArray(),
                    monster.DamageReactionActionId,
                    monster.DamageReactionPhase,
                    monster.DamageReactionHeadingFixed,
                    monster.DamageReactionStrength,
                    monster.DamageReactionMoveTicksRemaining,
                    monster.DamageReactionRecoveryEndTick,
                    monster.SessionId,
                    monster.EntityFlags,
                    monster.UnitFlags,
                    monster.StockUnitState,
                    monster.OnDeadTicksRemaining,
                    monster.OnDeadDispatched,
                    monster.CorpseTicksRemaining,
                    monster.FadeTicksRemaining,
                    monster.MoveSpeedF32,
                    monster.WalkSpeedF32,
                    monster.SpeedMod,
                    monster.CollisionRadiusF32,
                    monster.SizeModPercent,
                    monster.WeaponRangeF32,
                    monster.UnitDescAttackRangeF32,
                    unitBehaviorRadius130F32 = CombatRuntime.ResolveUnitBehaviorRadius130F32(monster),
                    unitBehaviorRange138F32 = CombatRuntime.ResolveUnitBehaviorRadius130F32(monster)
                        + (monster.Manipulators != null && monster.Manipulators.ContainsKey("primaryweapon")
                            ? Math.Max(0, monster.WeaponRangeF32)
                            : Math.Max(0, monster.UnitDescAttackRangeF32)),
                    effectiveAttackRangeF32 = CombatRuntime.Instance.GetMonsterEffectiveAttackRangeF32(monster),
                    avatarUnitBehaviorRadiusF32 = CombatRuntime.Instance.ResolveAvatarUnitBehaviorRadiusF32(),
                    effectiveSpeedF32,
                    speedPerFrameF32,
                     monster.ChaseHeadingFixed,
                     monster.ChaseHeadingInit,
                     monster.MoveInDirectionHeadingFixed,
                     monster.MoveInDirectionHeadingInit,
                     monster.UnitMoverMovingThisFrame,
                     monster.UnitMoverPathOwner,
                    monster.UnitMoverPathRequestId,
                    monster.UnitMoverPathIndex,
                    unitMoverPathCount = monster.UnitMoverPathFixed.Count,
                    monster.UnitMoverPathTargetFixedX,
                    monster.UnitMoverPathTargetFixedY,
                    monster.UnitMoverPathRetryCountdown,
                    monster.UnitMoverPathRequestTick,
                    monster.UnitMoverPathReadyTick,
                    unitMoverPathWaypoint = hasPathWaypoint
                        ? new
                        {
                            FixedX = monster.UnitMoverPathFixed[pathIndex].FixedX,
                            FixedY = monster.UnitMoverPathFixed[pathIndex].FixedY
                        }
                        : null,
                    runtimePosition = Position(monster.PosFixedX, monster.PosFixedY, monster.PosFixedZ, monster.HeadingFixed),
                    wander = hasWander ? new
                    {
                        wander.State,
                        wander.Timer,
                        wander.CanWander,
                        wander.ArriveTicks,
                        wander.HasTarget,
                        wander.TargetAttempt,
                        wander.IdleSubTick,
                        wander.FixedX,
                        wander.FixedY,
                        wander.FixedZ,
                        wander.HeadingFixed,
                        wander.TargetFixedX,
                        wander.TargetFixedY,
                        wander.MoveCommand,
                        wander.LastConsumedMoveCommand
                    } : null,
                    behavior = behavior.HasValue ? new
                    {
                        behavior.Value.TopState,
                        behavior.Value.CombatTimer,
                        behavior.Value.FollowState,
                        behavior.Value.FollowActive,
                        behavior.Value.FollowTargetEntityId,
                        behavior.Value.ScanState,
                        behavior.Value.ScanCountdown,
                        behavior.Value.ManeuverActive,
                        behavior.Value.ScanMoveGapTicks,
                        behavior.Value.AttackUseIndex,
                        behavior.Value.PreviousUseIndex,
                        behavior.Value.SpawnPhase,
                        behavior.Value.SpawnCounter,
                        behavior.Value.SpawnActive,
                        behavior.Value.ActionGeneration,
                        behavior.Value.CurrentActionInterruptLocked,
                        behavior.Value.WanderActionPending,
                        behavior.Value.ManeuverPending,
                        behavior.Value.AttackTarget2Pending,
                        behavior.Value.AttackTarget2Active,
                        behavior.Value.PrimaryTargetEntityId,
                        behavior.Value.AlternateTargetEntityId,
                        behavior.Value.PrimaryTargetObserved,
                        behavior.Value.AlternateTargetObserved,
                        currentAction = behavior.Value.CurrentAction.ToString(),
                        alternateAction = behavior.Value.AlternateAction.ToString(),
                        pendingAction = behavior.Value.PendingAction.ToString(),
                        behavior.Value.CurrentDamageReactionActionId,
                        behavior.Value.AlternateDamageReactionActionId,
                        behavior.Value.PendingDamageReactionActionId
                    } : null
                } : null
            };
        }

        private static SimulationParityActionState CaptureSimulationParityMonsterPrimaryAction(
            Monster monster,
            MonsterBehavior2Snapshot? behavior,
            bool moving,
            int targetFixedX,
            int targetFixedY)
        {
            if (!behavior.HasValue)
                return SimulationParityMonsterAction(MonsterBehavior2.ActionSlot.None, 0, monster.TargetId, true, moving, targetFixedX, targetFixedY);
            MonsterBehavior2Snapshot state = behavior.Value;
            uint targetEntityId = state.CurrentAction == MonsterBehavior2.ActionSlot.Follow
                ? state.FollowTargetEntityId
                : state.CurrentAction == MonsterBehavior2.ActionSlot.UseTarget
                || state.CurrentAction == MonsterBehavior2.ActionSlot.Use
                ? monster.SelectedActiveSkillTargetEntityId
                : monster.TargetId;
            return SimulationParityMonsterAction(
                state.CurrentAction,
                state.CurrentDamageReactionActionId,
                targetEntityId,
                true,
                moving,
                targetFixedX,
                targetFixedY);
        }

        private static SimulationParityActionState CaptureSimulationParityMonsterAlternateAction(
            Monster monster,
            MonsterBehavior2Snapshot? behavior)
        {
            if (!behavior.HasValue)
                return null;
            MonsterBehavior2Snapshot state = behavior.Value;
            return SimulationParityMonsterAction(
                state.AlternateAction,
                state.AlternateDamageReactionActionId,
                monster.TargetId,
                false,
                null,
                null,
                null);
        }

        private static SimulationParityActionState CaptureSimulationParityMonsterPendingAction(
            Monster monster,
            MonsterBehavior2Snapshot? behavior)
        {
            if (!behavior.HasValue)
                return null;
            MonsterBehavior2Snapshot state = behavior.Value;
            return SimulationParityMonsterAction(
                state.PendingAction,
                state.PendingDamageReactionActionId,
                monster.TargetId,
                false,
                null,
                null,
                null);
        }

        private static SimulationParityActionState SimulationParityMonsterAction(
            MonsterBehavior2.ActionSlot action,
            byte damageReactionActionId,
            uint targetEntityId,
            bool includeNone,
            bool? moving,
            int? targetFixedX,
            int? targetFixedY)
        {
            string kind;
            string vtableRva;
            switch (action)
            {
                case MonsterBehavior2.ActionSlot.Spawn:
                    kind = "spawn";
                    vtableRva = "0x47AF88";
                    break;
                case MonsterBehavior2.ActionSlot.Kill:
                    kind = "kill";
                    vtableRva = "0x47A500";
                    break;
                case MonsterBehavior2.ActionSlot.Follow:
                    kind = "follow";
                    vtableRva = "0x47A138";
                    break;
                case MonsterBehavior2.ActionSlot.SearchForAttack:
                    kind = "search-for-attack";
                    vtableRva = "0x47ADA0";
                    break;
                case MonsterBehavior2.ActionSlot.AttackTarget2:
                    kind = "attack-target2";
                    vtableRva = "0x479978";
                    break;
                case MonsterBehavior2.ActionSlot.Use:
                    kind = "use";
                    vtableRva = "0x480078";
                    break;
                case MonsterBehavior2.ActionSlot.UseTarget:
                    kind = "use-target";
                    vtableRva = "0x480280";
                    break;
                case MonsterBehavior2.ActionSlot.Wander:
                    kind = "wander";
                    vtableRva = "0x47B528";
                    break;
                case MonsterBehavior2.ActionSlot.DamageReaction:
                    if (damageReactionActionId == 0x0A)
                    {
                        kind = "knock-back";
                        vtableRva = "0x47A5F0";
                    }
                    else if (damageReactionActionId == 0x0B)
                    {
                        kind = "knock-down";
                        vtableRva = "0x47A6F0";
                    }
                    else if (damageReactionActionId == 0x0C)
                    {
                        kind = "stun";
                        vtableRva = "0x47B170";
                    }
                    else
                    {
                        kind = $"damage-reaction-0x{damageReactionActionId:X2}";
                        vtableRva = null;
                    }
                    break;
                default:
                    if (!includeNone)
                        return null;
                    kind = "none";
                    vtableRva = null;
                    break;
            }
            return new SimulationParityActionState
            {
                Kind = kind,
                VtableRva = vtableRva,
                Phase = vtableRva != null ? 0 : null,
                Moving = moving,
                TargetX = targetFixedX,
                TargetY = targetFixedY,
                TargetEntityId = targetEntityId
            };
        }

        private SimulationParityUnit CaptureSimulationParityBling(RRConnection viewer, BlingGnomeLockstepSnapshot gnome, uint simulationTick)
        {
            BlingGnomeRuntime.Instance.TryGetParitySnapshot(gnome.EntityId, viewer.ConnId, out int ownerConnId, out bool knownToViewer, out uint hitPointsWire, out uint maxHitPointsWire, out ushort healthRegenBase);
            RRConnection owner = GetConnectionByConnId(ownerConnId);
            int followSpeedPerFrameF32 = UnitMover.CacheSpeedPerFrame(50 * 0x100, gnome.FollowSpeedMod, out int followEffectiveSpeedF32);
            return new SimulationParityUnit
            {
                EntityId = gnome.EntityId,
                ServerEntityId = gnome.EntityId,
                StableKey = $"bling:{gnome.EntityId}",
                Kind = "friendly-bling",
                Name = owner?.LoginName != null ? $"{owner.LoginName}:BlingGnome" : "BlingGnome",
                GcType = "creatures.summon.blinggnome.base.BlingGnome_Summon",
                InstanceKey = gnome.InstanceKey,
                KnownToViewer = knownToViewer,
                PositionModel = "shared-simulation",
                Position = Position(gnome.FixedX, gnome.FixedY, gnome.FixedZ, gnome.HeadingFixed),
                Stance = MissingStance(),
                Resources = new SimulationParityResources
                {
                    MaxHp = maxHitPointsWire,
                    Hp = hitPointsWire,
                    Mana = 0,
                    HpCooldown = 0,
                    ManaCooldown = 0
                },
                BehaviorState = new SimulationParityBehaviorState
                {
                    Primary = new SimulationParityActionState
                    {
                        Kind = gnome.RetrieveActive
                            ? "retrieve-item"
                            : gnome.ConvertActive
                                ? "convert-items-to-gold"
                                : "follow",
                        Moving = gnome.RetrieveActive ? gnome.RetrieveState == 2 : gnome.FollowMoving,
                        TargetX = gnome.FollowTargetFixedX,
                        TargetY = gnome.FollowTargetFixedY,
                        TargetEntityId = gnome.RetrieveActive
                            ? gnome.RetrieveItemEntityId
                            : owner?.Avatar != null
                                ? owner.Avatar.Id
                                : null
                    },
                    TopStateMachine = new SimulationParityStateMachine
                    {
                        Clock = gnome.BlingCounter,
                        PreviousState = gnome.BlingPreviousState,
                        CurrentState = gnome.BlingState,
                        NextState = gnome.BlingState,
                        MessageCount = 2
                    },
                    FollowStateMachine = new SimulationParityStateMachine
                    {
                        Clock = gnome.FollowCounter,
                        PreviousState = gnome.FollowPreviousState,
                        CurrentState = gnome.FollowState,
                        NextState = gnome.FollowState,
                        MessageCount = gnome.FollowStarted ? 1 : 0
                    }
                },
                ServerState = new
                {
                    ownerConnId,
                    healthRegenBase,
                    gnome.BehaviorId,
                    gnome.Initialized,
                    gnome.FollowStarted,
                    gnome.SearchDue,
                    gnome.SearchRepeat,
                    gnome.SearchMessageOrder,
                    gnome.FidgetDue,
                    gnome.FidgetRepeat,
                    gnome.FidgetMessageOrder,
                    gnome.FollowDue,
                    gnome.FollowRepeat,
                    gnome.FollowMoverMode,
                    gnome.FollowMoverMovingThisFrame,
                    gnome.FollowSpeedMod,
                    followSpeedPerFrameF32,
                    followEffectiveSpeedF32,
                    gnome.FollowDirectionHeadingFixed,
                    gnome.FollowTargetFixedZ,
                    gnome.AnimationState,
                    gnome.AnimationId,
                    gnome.AnimationFramesRemaining,
                    gnome.SpawnAnimationActionActive,
                    gnome.SpawnAnimationPhase,
                    gnome.SpawnAnimationCounter,
                    gnome.UnSpawnAdmissionPending,
                    gnome.UnSpawnPacketFlushed,
                    gnome.UnSpawnWriterTick,
                    gnome.UnSpawnActionActive,
                    gnome.UnSpawnPhase,
                    gnome.UnSpawnCounter,
                    gnome.UnSpawnRemovalPending,
                    gnome.UnSpawnRemovalDueTick,
                    gnome.RetrieveActive,
                    gnome.RetrievePending,
                    gnome.RetrievePendingItemEntityId,
                    gnome.RetrieveState,
                    gnome.RetrieveItemEntityId,
                    gnome.RetrieveTimer,
                    gnome.RetrieveTargetFixedX,
                    gnome.RetrieveTargetFixedY,
                    gnome.RetrieveTargetFixedZ,
                    gnome.RetrievePathRequestId,
                    gnome.RetrievePathRequestTick,
                    gnome.RetrievePathReadyTick,
                    gnome.RetrievePathCount,
                    gnome.RetrievePathIndex,
                    gnome.ConvertAdmissionPending,
                    gnome.ConvertRequested,
                    gnome.ConvertActive,
                    gnome.ConvertState,
                    gnome.ConvertActionTimer,
                    gnome.ConvertAnimationComplete,
                    gnome.ConvertItemsComplete,
                    gnome.ConvertStartedTick,
                    gnome.ConvertStateAdvanceDueTick,
                    gnome.ConvertAnimationDueTick,
                    gnome.ConvertAnimationCompletionTick,
                    gnome.ConvertItemsCompletionTick,
                    gnome.ConversionItemCount,
                    gnome.FirstConversionItemEntityId,
                    gnome.FirstConversionItemPhase,
                    gnome.FirstConversionItemTimer
                }
            };
        }

        private static SimulationParityUnit CaptureSimulationParityItem(DroppedItemInfo item, ushort entityId)
        {
            return new SimulationParityUnit
            {
                EntityId = entityId,
                ServerEntityId = entityId,
                StableKey = $"item:{entityId}",
                Kind = "item-object",
                Name = item.IsGoldDrop ? "Currency" : item.Item?.GCClass ?? "ItemObject",
                GcType = item.IsGoldDrop ? "Currency" : item.Item?.GCClass,
                InstanceKey = item.RuntimeInstanceKey,
                KnownToViewer = true,
                PositionModel = "shared-simulation",
                Position = Position(item.PosFixedX, item.PosFixedY, item.PosFixedZ, item.HeadingFixed),
                Stance = new SimulationParityStance
                {
                    State = item.ItemObjectState,
                    Duration = item.ItemObjectStateTimer,
                    Model = "ItemObject"
                },
                Resources = new SimulationParityResources(),
                BehaviorState = new SimulationParityBehaviorState(),
                ServerState = new
                {
                    item.ItemObjectStateCount,
                    item.ItemObjectState,
                    item.ItemObjectStateTimer,
                    item.ItemObjectLifetimeTicks,
                    item.ItemObjectLastSimulationTick,
                    item.HeadingFixed,
                    item.Quantity,
                    item.IsGoldDrop,
                    item.GoldAmount,
                    item.DbId,
                    item.DroppedBy,
                    item.OwnerCharacterId,
                    item.OwnerName,
                    AuxiliaryId = 0u,
                    OwnerFlag = 1
                }
            };
        }

        private void MarkSimulationParityNpcsKnown(RRConnection viewer, uint zoneId, List<ZoneNPC> npcs)
        {
            if (!SimulationParityLedger.Active || viewer == null || npcs == null)
                return;
            if (!_simulationParityNpcKnownByConn.TryGetValue(viewer.ConnId, out HashSet<ulong> known))
            {
                known = new HashSet<ulong>();
                _simulationParityNpcKnownByConn[viewer.ConnId] = known;
            }
            foreach (ZoneNPC npc in npcs)
            {
                if (npc != null)
                    known.Add(((ulong)zoneId << 32) | npc.Id);
            }
        }

        private bool IsSimulationParityNpcKnown(RRConnection viewer, uint zoneId, uint entityId)
        {
            return viewer != null
                && _simulationParityNpcKnownByConn.TryGetValue(viewer.ConnId, out HashSet<ulong> known)
                && known.Contains(((ulong)zoneId << 32) | entityId);
        }

        private SimulationParityUnit CaptureSimulationParityNpc(RRConnection viewer, uint zoneId, ZoneNPC npc, string instanceKey)
        {
            return new SimulationParityUnit
            {
                EntityId = npc.Id,
                ServerEntityId = npc.Id,
                StableKey = $"npc:{npc.Id}",
                Kind = "friendly-npc",
                Name = npc.Name,
                GcType = npc.GCClass,
                InstanceKey = instanceKey,
                KnownToViewer = IsSimulationParityNpcKnown(viewer, zoneId, npc.Id),
                PositionModel = "authored-static",
                Position = npc.HasFixedPosition ? Position(npc.PosFixedX, npc.PosFixedY, npc.PosFixedZ, npc.HeadingFixed) : new SimulationParityPosition(),
                Stance = MissingStance(),
                Resources = new SimulationParityResources
                {
                    MaxHp = npc.HitPointsWire,
                    Hp = npc.HitPointsWire,
                    Mana = 0,
                    HpCooldown = 0,
                    ManaCooldown = 0
                },
                BehaviorState = new SimulationParityBehaviorState(),
                ServerState = new
                {
                    simulated = false,
                    npc.UnitBehaviorId,
                    npc.IsMerchant,
                    npc.IsTrainer,
                    npc.IsBank,
                    npc.IsPosseMagnate
                }
            };
        }

        private static SimulationParityPosition Position(int x, int y, int z, int heading)
        {
            return new SimulationParityPosition
            {
                X = x,
                Y = y,
                Z = z,
                Heading = heading
            };
        }

        private static SimulationParityStance MissingStance()
        {
            return new SimulationParityStance
            {
                Model = "not-mirrored"
            };
        }

        private static SimulationParityStateMachine StateMachine(StateMachineSnapshot snapshot, int currentState)
        {
            if (snapshot == null)
                return null;
            return new SimulationParityStateMachine
            {
                Clock = snapshot.Clock,
                CurrentState = currentState,
                MessageCount = snapshot.Messages?.Count ?? 0
            };
        }
    }
}
