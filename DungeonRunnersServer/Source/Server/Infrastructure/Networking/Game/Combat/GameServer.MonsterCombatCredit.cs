using DungeonRunners.Combat;
using DungeonRunners.Networking.EntitySynchInfo;

namespace DungeonRunners.Networking
{
    public partial class GameServer
    {
        private ushort ResolveCombatSummonOwnerForViewer(RRConnection viewer, Monster monster)
        {
            if (monster?.Summoner == null || viewer == null)
                return 0;
            RRConnection owner = FindConnectionByAvatarEntityId(monster.Summoner.EntityId);
            if (owner == null || !owner.IsConnected || !owner.IsSpawned || !CombatRuntime.Instance.MatchesInstance(monster, owner.RuntimeInstanceKey))
                return 0;
            if (ReferenceEquals(owner, viewer))
                return owner.Player != null ? checked((ushort)owner.Player.Id) : (ushort)0;
            return _remotePlayerIds.TryGetValue(viewer.LoginName, out var players)
                && players.TryGetValue(owner.LoginName, out ushort id) ? id : (ushort)0;
        }
        private RRConnection FindMonsterSimulationConnection(Monster monster)
        {
            if (monster == null)
                return null;
            foreach (RRConnection connection in GetConnectionInsertionOrderSnapshot())
                if (connection != null && connection.IsConnected && connection.IsSpawned
                    && connection.Avatar != null && CombatRuntime.Instance.MatchesInstance(monster, connection.RuntimeInstanceKey))
                    return connection;
            return null;
        }

        private void SendMonsterTargetModifier(Monster target, CombatRuntime.PlayerModifierNetworkEvent modifier)
        {
            if (target == null || modifier == null)
                return;
            ushort componentId = checked((ushort)target.ModifiersId);
            byte subtype = modifier.Add ? (byte)0 : (byte)1;
            foreach (RRConnection recipient in GetMonsterInstanceRecipients(target.EntityId))
            {
                if (!EnsureMonsterSpawnQueuedForRecipient(recipient, target, "MON-MODIFIER")
                    || !ResolveEntitySynchInfoForComponent(recipient, componentId, subtype, EntitySynchInfoContext.MonsterAction,
                        target.EntityId, "MON-MODIFIER", out EntitySynchInfoDecision decision))
                    continue;
                ResolvedEntitySynchInfo state = decision.ToResolved(target.EntityId, componentId, subtype, modifier.SourceFunction);
                byte[] packet = modifier.Add
                    ? CombatPackets.BuildPlayerModifierAddPacket(componentId, modifier.GCType, modifier.ModifierId, modifier.Level,
                        modifier.PowerLevel, modifier.DurationTicks, modifier.SourceIsSelf, state)
                    : CombatPackets.BuildPlayerModifierRemovePacket(componentId, modifier.ModifierId, state);
                QueueClientEntityStream(recipient, packet);
            }
        }

        private void HandleMonsterTargetDamageResolved(Monster source, Monster target, bool damaged, uint hpWire)
        {
            if (target == null || !damaged || target.IsAlive || hpWire != 0)
                return;
            CombatPlayer owner = source != null ? CombatRuntime.Instance.ResolveCombatRewardOwner(source.EntityId, target.InstanceKey) : null;
            owner ??= CombatRuntime.Instance.ResolveMonsterLootParticipant(target);
            RRConnection connection = owner != null ? FindConnectionByAvatarEntityId(owner.EntityId) : null;
            TryFinalizeMonsterKill(connection, target, "unit-combat");
        }
    }
}
