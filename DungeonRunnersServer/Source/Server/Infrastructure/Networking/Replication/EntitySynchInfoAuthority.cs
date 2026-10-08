using System;
using System.Collections.Generic;
using DungeonRunners.Combat;
using DungeonRunners.Networking;
using DungeonRunners.Engine;

namespace DungeonRunners.Networking.EntitySynchInfo
{
    public sealed class EntitySynchInfoAuthority
    {
        public static EntitySynchInfoAuthority Instance { get; } = new EntitySynchInfoAuthority();

        public const uint WirePerHP = 256u;

        private readonly Dictionary<uint, EntitySynchInfoOwnerState> _byEntity = new Dictionary<uint, EntitySynchInfoOwnerState>();
        private readonly Dictionary<uint, EntitySynchInfoOwnerState> _playerByConn = new Dictionary<uint, EntitySynchInfoOwnerState>();
        private readonly Dictionary<ushort, uint> _componentOwnerEntity = new Dictionary<ushort, uint>();
        private readonly Dictionary<ushort, EntitySynchInfoOwnerKind> _componentKind = new Dictionary<ushort, EntitySynchInfoOwnerKind>();
        private readonly List<ushort> _componentOrder = new List<ushort>();
        private readonly Dictionary<uint, RRConnection> _playerConnections = new Dictionary<uint, RRConnection>();
        private readonly Dictionary<uint, Monster> _monsters = new Dictionary<uint, Monster>();

        private EntitySynchInfoAuthority()
        {
        }

        public EntitySynchInfoOwnerState RegisterPlayer(RRConnection conn, PlayerState playerState, uint avatarEntityId)
        {
            if (conn == null || playerState == null || avatarEntityId == 0) return null;
            EntitySynchInfoOwnerState s = GetOrCreate(avatarEntityId, EntitySynchInfoOwnerKind.PlayerAvatar);
            s.OwnerKind = EntitySynchInfoOwnerKind.PlayerAvatar;
            s.ConnectionId = (uint)Math.Max(0, conn.ConnId);
            s.OwnerName = conn.LoginName ?? conn.ConnId.ToString();
            _playerByConn[(uint)Math.Max(0, conn.ConnId)] = s;
            _playerConnections[avatarEntityId] = conn;
            RegisterPlayerComponents(conn, avatarEntityId);
            MirrorPlayer(conn, playerState, s);
            return s;
        }

        public EntitySynchInfoOwnerState RegisterMonster(Monster monster)
        {
            if (monster == null || monster.EntityId == 0) return null;
            if (!_byEntity.TryGetValue(monster.EntityId, out EntitySynchInfoOwnerState s))
                s = GetOrCreate(monster.EntityId, EntitySynchInfoOwnerKind.Monster);
            s.OwnerKind = EntitySynchInfoOwnerKind.Monster;
            s.OwnerName = monster.Name ?? monster.EntityId.ToString();
            _monsters[monster.EntityId] = monster;
            RegisterMonsterComponents(monster);
            return s;
        }

        public EntitySynchInfoOwnerState RegisterBlingGnome(uint gnomeEntityId, ushort behaviorComponentId, uint maxHPWire, string name)
        {
            if (gnomeEntityId == 0) return null;
            if (!_byEntity.TryGetValue(gnomeEntityId, out EntitySynchInfoOwnerState s))
                s = GetOrCreate(gnomeEntityId, EntitySynchInfoOwnerKind.BlingGnome);
            s.OwnerKind = EntitySynchInfoOwnerKind.BlingGnome;
            s.OwnerName = name ?? gnomeEntityId.ToString();
            RegisterBlingGnomeComponents(gnomeEntityId, behaviorComponentId);
            return s;
        }

        public void UnregisterBlingGnome(uint gnomeEntityId)
        {
            if (gnomeEntityId == 0) return;
            _byEntity.Remove(gnomeEntityId);
            RemoveComponentOwner(gnomeEntityId);
        }

        public bool TryResolveBlingGnomeOwner(uint gnomeEntityId, out EntitySynchInfoOwnerRef owner)
        {
            owner = default;
            if (gnomeEntityId == 0 || !_byEntity.TryGetValue(gnomeEntityId, out EntitySynchInfoOwnerState s)) return false;
            owner = EntitySynchInfoOwnerRef.BlingGnomeOwner(gnomeEntityId, s.OwnerName);
            return true;
        }

        public bool IsBlingGnomeComponent(ushort componentId)
        {
            return componentId != 0
                && _componentKind.TryGetValue(componentId, out EntitySynchInfoOwnerKind kind)
                && kind == EntitySynchInfoOwnerKind.BlingGnome;
        }

        public bool ObserveClientBlingGnomeHP(RRConnection conn, ushort componentId, uint clientHPWire, string source)
        {
            if (componentId == 0) return false;
            if (!_componentKind.TryGetValue(componentId, out EntitySynchInfoOwnerKind kind) || kind != EntitySynchInfoOwnerKind.BlingGnome)
                return false;
            if (!TryResolveComponentOwner(conn, componentId, 0, out EntitySynchInfoOwnerRef owner) || owner.Kind != EntitySynchInfoOwnerKind.BlingGnome)
                return false;
            return ObserveClientHpReport(owner, clientHPWire, ClassifyReportSource(source), source ?? "client-gnome-hp", true, out _);
        }

        public void UnregisterPlayer(RRConnection conn)
        {
            if (conn == null) return;
            uint avatarEntityId = conn.Avatar != null ? (uint)conn.Avatar.Id : 0;
            _playerByConn.Remove((uint)Math.Max(0, conn.ConnId));
            if (avatarEntityId != 0)
                _playerConnections.Remove(avatarEntityId);
            UnregisterPlayerComponents(conn, avatarEntityId);
        }

        public void UnregisterMonster(uint monsterEntityId)
        {
            if (monsterEntityId == 0) return;
            _byEntity.Remove(monsterEntityId);
            _monsters.Remove(monsterEntityId);
            RemoveComponentOwner(monsterEntityId);
        }

        public EntitySynchInfoOwnerState GetPlayerState(RRConnection conn, PlayerState playerState, uint avatarEntityId)
        {
            return RegisterPlayer(conn, playerState, avatarEntityId);
        }

        public EntitySynchInfoOwnerState GetMonsterState(Monster monster)
        {
            return RegisterMonster(monster);
        }

        public bool TryResolvePlayerOwner(RRConnection conn, PlayerState playerState, uint avatarEntityId, out EntitySynchInfoOwnerRef owner)
        {
            owner = default;
            if (RegisterPlayer(conn, playerState, avatarEntityId) == null) return false;
            owner = EntitySynchInfoOwnerRef.Player(avatarEntityId, conn);
            return true;
        }

        public bool TryResolveMonsterOwner(Monster monster, out EntitySynchInfoOwnerRef owner)
        {
            owner = default;
            if (RegisterMonster(monster) == null) return false;
            owner = EntitySynchInfoOwnerRef.MonsterOwner(monster);
            return true;
        }

        public bool TryResolveComponentOwner(RRConnection conn, ushort componentId, uint entityId, out EntitySynchInfoOwnerRef owner)
        {
            owner = default;
            if (componentId != 0 && _componentKind.TryGetValue(componentId, out EntitySynchInfoOwnerKind kind) && kind == EntitySynchInfoOwnerKind.NonUnit)
            {
                owner = EntitySynchInfoOwnerRef.NonUnit(componentId, "player-empty-component");
                return true;
            }
            if (componentId != 0 && _componentOwnerEntity.TryGetValue(componentId, out uint ownerEntity))
            {
                if (_byEntity.TryGetValue(ownerEntity, out EntitySynchInfoOwnerState state))
                {
                    if (state.OwnerKind == EntitySynchInfoOwnerKind.PlayerAvatar)
                    {
                        _playerConnections.TryGetValue(ownerEntity, out RRConnection playerConn);
                        owner = EntitySynchInfoOwnerRef.Player(ownerEntity, playerConn ?? conn);
                        return true;
                    }
                    if (state.OwnerKind == EntitySynchInfoOwnerKind.Monster)
                    {
                        _monsters.TryGetValue(ownerEntity, out Monster monster);
                        owner = new EntitySynchInfoOwnerRef(EntitySynchInfoOwnerKind.Monster, ownerEntity, null, monster, state.OwnerName);
                        return true;
                    }
                    if (state.OwnerKind == EntitySynchInfoOwnerKind.BlingGnome)
                    {
                        owner = EntitySynchInfoOwnerRef.BlingGnomeOwner(ownerEntity, state.OwnerName);
                        return true;
                    }
                }
            }
            if (entityId != 0 && _byEntity.TryGetValue(entityId, out EntitySynchInfoOwnerState entityState))
            {
                if (entityState.OwnerKind == EntitySynchInfoOwnerKind.PlayerAvatar)
                {
                    _playerConnections.TryGetValue(entityId, out RRConnection playerConn);
                    owner = EntitySynchInfoOwnerRef.Player(entityId, playerConn ?? conn);
                    return true;
                }
                if (entityState.OwnerKind == EntitySynchInfoOwnerKind.Monster)
                {
                    _monsters.TryGetValue(entityId, out Monster monster);
                    owner = new EntitySynchInfoOwnerRef(EntitySynchInfoOwnerKind.Monster, entityId, null, monster, entityState.OwnerName);
                    return true;
                }
                if (entityState.OwnerKind == EntitySynchInfoOwnerKind.BlingGnome)
                {
                    owner = EntitySynchInfoOwnerRef.BlingGnomeOwner(entityId, entityState.OwnerName);
                    return true;
                }
            }
            return false;
        }

        public EntitySynchInfoResolveResult ResolveOutboundPlayer(RRConnection conn, PlayerState playerState, uint avatarEntityId, EntitySynchInfoContext context, string packetName, out uint hpWire)
        {
            hpWire = 0;
            EntitySynchInfoOwnerState s = RegisterPlayer(conn, playerState, avatarEntityId);
            if (s == null)
                return EntitySynchInfoResolveResult.Block(EntitySynchInfoOwnerKind.PlayerAvatar, avatarEntityId, "missing-player-state");
            hpWire = playerState.EntitySynchInfoHP;
            MarkOutbound(s, hpWire, packetName);
            MirrorPlayer(conn, playerState, s);
            return EntitySynchInfoResolveResult.AllowHP(EntitySynchInfoOwnerKind.PlayerAvatar, avatarEntityId, hpWire, "client-visible-hp");
        }

        public EntitySynchInfoResolveResult ResolveOutboundMonster(Monster monster, EntitySynchInfoContext context, string packetName, out uint hpWire)
        {
            hpWire = 0;
            EntitySynchInfoOwnerState s = RegisterMonster(monster);
            if (s == null)
                return EntitySynchInfoResolveResult.Block(EntitySynchInfoOwnerKind.Monster, monster != null ? monster.EntityId : 0, "missing-monster-state");
            hpWire = monster.CurrentHPWire;
            MarkOutbound(s, hpWire, packetName);
            return EntitySynchInfoResolveResult.AllowHP(EntitySynchInfoOwnerKind.Monster, monster.EntityId, hpWire, "client-visible-hp");
        }

        public bool ObserveClientHpReport(EntitySynchInfoOwnerRef owner, uint hpWire, EntitySynchInfoReportSource source, string packetName, bool reportCameFromEntitySynchInfo, out EntitySynchInfoReportDecision decision)
        {
            decision = EntitySynchInfoReportDecision.Reject("client-hp-is-not-authoritative");
            return false;
        }

        public void RecordPlayerOutboundHP(RRConnection conn, PlayerState playerState, uint avatarEntityId, uint hpWire, string source)
        {
            EntitySynchInfoOwnerState s = RegisterPlayer(conn, playerState, avatarEntityId);
            if (s == null) return;
            MarkOutbound(s, hpWire, source ?? "outbound");
            MirrorPlayer(conn, playerState, s);
        }

        public void RecordMonsterOutboundHP(Monster monster, uint hpWire, string source)
        {
            EntitySynchInfoOwnerState s = RegisterMonster(monster);
            if (s == null) return;
            MarkOutbound(s, hpWire, source ?? "outbound");
        }

        public static EntitySynchInfoReportSource ClassifyReportSource(string source)
        {
            if (string.IsNullOrEmpty(source)) return EntitySynchInfoReportSource.Unknown;
            if (source.StartsWith("ENTITY-SYNCH-INFO", StringComparison.Ordinal)) return EntitySynchInfoReportSource.ClientEntitySynchSuffix;
            if (source.StartsWith("SEND-UPDATE", StringComparison.Ordinal)) return EntitySynchInfoReportSource.ClientSendUpdate;
            if (source.StartsWith("PLAYER-STATE-ENTITY-SYNCH-INFO", StringComparison.Ordinal)) return EntitySynchInfoReportSource.ClientPlayerStateEntitySynchInfo;
            if (source.StartsWith("ACTION-0x50-ENTITY-SYNCH-INFO", StringComparison.Ordinal)) return EntitySynchInfoReportSource.ClientEntitySynchSuffix;
            if (source.StartsWith("MONSTER-MOVE-HP", StringComparison.Ordinal)) return EntitySynchInfoReportSource.ClientEntitySynchSuffix;
            if (source.StartsWith("MONSTER-SM-HP", StringComparison.Ordinal)) return EntitySynchInfoReportSource.ClientEntitySynchSuffix;
            if (source.StartsWith("ENTITY-SYNCH-INFO", StringComparison.Ordinal)) return EntitySynchInfoReportSource.ClientEntitySynchSuffix;
            return EntitySynchInfoReportSource.Unknown;
        }

        private EntitySynchInfoOwnerState GetOrCreate(uint entityId, EntitySynchInfoOwnerKind kind)
        {
            if (!_byEntity.TryGetValue(entityId, out EntitySynchInfoOwnerState s))
            {
                s = new EntitySynchInfoOwnerState { OwnerEntityId = entityId, OwnerKind = kind };
                _byEntity[entityId] = s;
            }
            return s;
        }

        private void RegisterPlayerComponents(RRConnection conn, uint avatarEntityId)
        {
            RegisterComponent((ushort)avatarEntityId, avatarEntityId, EntitySynchInfoOwnerKind.PlayerAvatar);
            RegisterComponent((ushort)conn.UnitBehaviorId, avatarEntityId, EntitySynchInfoOwnerKind.PlayerAvatar);
            RegisterComponent(conn.BehaviorComponentId, avatarEntityId, EntitySynchInfoOwnerKind.PlayerAvatar);
            RegisterComponent(conn.SkillsComponentId, avatarEntityId, EntitySynchInfoOwnerKind.PlayerAvatar);
            RegisterComponent(conn.ManipulatorsComponentId, avatarEntityId, EntitySynchInfoOwnerKind.PlayerAvatar);
            RegisterComponent((ushort)conn.ModifiersId, avatarEntityId, EntitySynchInfoOwnerKind.PlayerAvatar);
            RegisterComponent(conn.ModifiersComponentId, avatarEntityId, EntitySynchInfoOwnerKind.PlayerAvatar);
            RegisterComponent((ushort)conn.UnitContainerId, avatarEntityId, EntitySynchInfoOwnerKind.PlayerAvatar);
            RegisterComponent((ushort)conn.DialogManagerId, avatarEntityId, EntitySynchInfoOwnerKind.NonUnit);
            RegisterComponent((ushort)conn.QuestManagerId, avatarEntityId, EntitySynchInfoOwnerKind.NonUnit);
        }

        private void UnregisterPlayerComponents(RRConnection conn, uint avatarEntityId)
        {
            if (conn == null) return;
            RemoveComponent((ushort)avatarEntityId);
            RemoveComponent((ushort)conn.UnitBehaviorId);
            RemoveComponent(conn.BehaviorComponentId);
            RemoveComponent(conn.SkillsComponentId);
            RemoveComponent(conn.ManipulatorsComponentId);
            RemoveComponent((ushort)conn.ModifiersId);
            RemoveComponent(conn.ModifiersComponentId);
            RemoveComponent((ushort)conn.UnitContainerId);
            RemoveComponent((ushort)conn.DialogManagerId);
            RemoveComponent((ushort)conn.QuestManagerId);
        }

        private void RegisterMonsterComponents(Monster monster)
        {
            if (monster == null) return;
            RegisterComponent((ushort)monster.EntityId, monster.EntityId, EntitySynchInfoOwnerKind.Monster);
            RegisterComponent((ushort)monster.BehaviorId, monster.EntityId, EntitySynchInfoOwnerKind.Monster);
            RegisterComponent((ushort)monster.SkillsId, monster.EntityId, EntitySynchInfoOwnerKind.Monster);
            RegisterComponent((ushort)monster.ManipulatorsId, monster.EntityId, EntitySynchInfoOwnerKind.Monster);
            RegisterComponent((ushort)monster.ModifiersId, monster.EntityId, EntitySynchInfoOwnerKind.Monster);
            RegisterComponent((ushort)monster.UnitId, monster.EntityId, EntitySynchInfoOwnerKind.Monster);
        }

        private void RegisterBlingGnomeComponents(uint gnomeEntityId, ushort behaviorComponentId)
        {
            RegisterComponent((ushort)gnomeEntityId, gnomeEntityId, EntitySynchInfoOwnerKind.BlingGnome);
            RegisterComponent(behaviorComponentId, gnomeEntityId, EntitySynchInfoOwnerKind.BlingGnome);
        }

        private void RegisterComponent(ushort componentId, uint ownerEntityId, EntitySynchInfoOwnerKind kind)
        {
            if (componentId == 0 || ownerEntityId == 0) return;
            if (!_componentOwnerEntity.ContainsKey(componentId))
                _componentOrder.Add(componentId);
            _componentOwnerEntity[componentId] = ownerEntityId;
            _componentKind[componentId] = kind;
        }

        private void RemoveComponent(ushort componentId)
        {
            if (componentId == 0) return;
            _componentOwnerEntity.Remove(componentId);
            _componentKind.Remove(componentId);
            _componentOrder.Remove(componentId);
        }

        private void RemoveComponentOwner(uint ownerEntityId)
        {
            if (ownerEntityId == 0) return;
            for (int componentIndex = _componentOrder.Count - 1; componentIndex >= 0; componentIndex--)
            {
                ushort componentId = _componentOrder[componentIndex];
                if (_componentOwnerEntity.TryGetValue(componentId, out uint componentOwnerEntityId) && componentOwnerEntityId == ownerEntityId)
                    RemoveComponent(componentId);
            }
        }

        private void MirrorPlayer(RRConnection conn, PlayerState playerState, EntitySynchInfoOwnerState s)
        {
            if (conn == null || s == null) return;
            conn.LastOutboundHPWire = s.LastOutboundHPWire;
            conn.LastOutboundHPSource = s.LastOutboundPacket;
        }

        private void MarkOutbound(EntitySynchInfoOwnerState s, uint hpWire, string packetName)
        {
            s.LastOutboundHPWire = hpWire;
            s.LastOutboundPacket = packetName ?? "unknown";
            s.LastOutboundFlags = 0x02;
        }

        private bool IsHpIncreaseSource(EntitySynchInfoReportSource source)
        {
            return source == EntitySynchInfoReportSource.ServerPotionHeal
                || source == EntitySynchInfoReportSource.ServerSkillHeal
                || source == EntitySynchInfoReportSource.ServerRegen
                || source == EntitySynchInfoReportSource.ServerEquipmentChange
                || source == EntitySynchInfoReportSource.ServerStatAllocation
                || source == EntitySynchInfoReportSource.ServerLevelUp
                || source == EntitySynchInfoReportSource.ServerRespawn
                || source == EntitySynchInfoReportSource.ServerZoneLoad
                || source == EntitySynchInfoReportSource.PersistenceLoad;
        }

    }
}
