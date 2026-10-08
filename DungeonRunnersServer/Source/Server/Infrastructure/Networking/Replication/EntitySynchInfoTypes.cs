using DungeonRunners.Combat;
using DungeonRunners.Networking;

namespace DungeonRunners.Networking.EntitySynchInfo
{
    public enum EntitySynchInfoOwnerKind
    {
        Unknown = 0,
        PlayerAvatar,
        Monster,
        NpcUnit,
        NonUnit,
        BlingGnome
    }

    public enum EntitySynchInfoReportSource
    {
        Unknown = 0,
        ClientEntitySynchSuffix,
        ClientSendUpdate,
        ClientPlayerStateEntitySynchInfo,
        ServerMonsterAttack,
        ServerPlayerAttack,
        ServerSkillDamage,
        ServerPotionHeal,
        ServerSkillHeal,
        ServerRegen,
        ServerEquipmentChange,
        ServerStatAllocation,
        ServerLevelUp,
        ServerRespawn,
        ServerZoneLoad,
        PersistenceLoad
    }

    public enum EntitySynchInfoMaxHPMode
    {
        ClampCurrent,
        PreserveFlat,
        PreservePercent,
        FillToMax,
        AddDeltaToCurrent
    }

    public readonly struct EntitySynchInfoOwnerRef
    {
        public readonly EntitySynchInfoOwnerKind Kind;
        public readonly uint EntityId;
        public readonly RRConnection Connection;
        public readonly Monster Monster;
        public readonly string OwnerName;

        public EntitySynchInfoOwnerRef(EntitySynchInfoOwnerKind kind, uint entityId, RRConnection connection, Monster monster, string ownerName)
        {
            Kind = kind;
            EntityId = entityId;
            Connection = connection;
            Monster = monster;
            OwnerName = ownerName;
        }

        public static EntitySynchInfoOwnerRef Player(uint entityId, RRConnection conn)
        {
            return new EntitySynchInfoOwnerRef(EntitySynchInfoOwnerKind.PlayerAvatar, entityId, conn, null, conn?.LoginName ?? conn?.ConnId.ToString() ?? entityId.ToString());
        }

        public static EntitySynchInfoOwnerRef MonsterOwner(Monster monster)
        {
            return new EntitySynchInfoOwnerRef(EntitySynchInfoOwnerKind.Monster, monster != null ? monster.EntityId : 0, null, monster, monster?.Name ?? "monster");
        }

        public static EntitySynchInfoOwnerRef NonUnit(uint entityId, string ownerName)
        {
            return new EntitySynchInfoOwnerRef(EntitySynchInfoOwnerKind.NonUnit, entityId, null, null, ownerName ?? "non-unit");
        }

        public static EntitySynchInfoOwnerRef BlingGnomeOwner(uint entityId, string ownerName)
        {
            return new EntitySynchInfoOwnerRef(EntitySynchInfoOwnerKind.BlingGnome, entityId, null, null, ownerName ?? "blinggnome");
        }

        public static EntitySynchInfoOwnerRef Unknown(uint entityId, string ownerName)
        {
            return new EntitySynchInfoOwnerRef(EntitySynchInfoOwnerKind.Unknown, entityId, null, null, ownerName ?? "unknown");
        }
    }

    public readonly struct EntitySynchInfoReportDecision
    {
        public readonly bool Accepted;
        public readonly string Reason;

        private EntitySynchInfoReportDecision(bool accepted, string reason)
        {
            Accepted = accepted;
            Reason = reason;
        }

        public static EntitySynchInfoReportDecision AcceptObserved(string reason)
        {
            return new EntitySynchInfoReportDecision(true, reason ?? "accepted");
        }

        public static EntitySynchInfoReportDecision Reject(string reason)
        {
            return new EntitySynchInfoReportDecision(false, reason ?? "rejected");
        }
    }

    public readonly struct EntitySynchInfoResolveResult
    {
        public readonly bool AllowPacket;
        public readonly EntitySynchInfoPayload Payload;
        public readonly EntitySynchInfoOwnerKind OwnerKind;
        public readonly uint OwnerEntityId;
        public readonly string Reason;

        public bool HasHP => Payload.HasHP;

        private EntitySynchInfoResolveResult(bool allowPacket, EntitySynchInfoPayload payload, EntitySynchInfoOwnerKind ownerKind, uint ownerEntityId, string reason)
        {
            AllowPacket = allowPacket;
            Payload = payload;
            OwnerKind = ownerKind;
            OwnerEntityId = ownerEntityId;
            Reason = reason;
        }

        public static EntitySynchInfoResolveResult AllowEmpty(EntitySynchInfoOwnerKind ownerKind, uint ownerEntityId, string reason)
        {
            return new EntitySynchInfoResolveResult(true, EntitySynchInfoPayload.Empty, ownerKind, ownerEntityId, reason ?? "empty");
        }

        public static EntitySynchInfoResolveResult AllowHP(EntitySynchInfoOwnerKind ownerKind, uint ownerEntityId, uint hpWire, string reason)
        {
            return new EntitySynchInfoResolveResult(true, EntitySynchInfoPayload.FromHP(hpWire), ownerKind, ownerEntityId, reason ?? "hp");
        }

        public static EntitySynchInfoResolveResult Block(EntitySynchInfoOwnerKind ownerKind, uint ownerEntityId, string reason)
        {
            return new EntitySynchInfoResolveResult(false, EntitySynchInfoPayload.Empty, ownerKind, ownerEntityId, reason ?? "blocked");
        }
    }
}
