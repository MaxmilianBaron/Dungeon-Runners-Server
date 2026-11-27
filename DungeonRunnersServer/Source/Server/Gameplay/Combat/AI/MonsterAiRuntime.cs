namespace DungeonRunners.Combat
{
    public sealed class MonsterAiRuntime
    {
        public byte StateId = 5;
        public byte NextStateId;
        public bool Dirty;
        public ushort SkillDelayTimer;
        public uint TargetEntityId;
        public uint AlertSourceEntityId;
        public bool SkillListBuilt;
        public bool RemovalDone;
        public byte LastMessageId;
        public string LastTransitionReason = "spawn";

        public void SetState(byte stateId, string reason)
        {
            if (StateId != stateId)
                Dirty = true;
            StateId = stateId;
            LastTransitionReason = reason ?? LastTransitionReason;
        }

        public void PostMessage(byte messageId, string reason)
        {
            LastMessageId = messageId;
            Dirty = true;
            LastTransitionReason = reason ?? LastTransitionReason;
        }
    }

    public static class MonsterStateId
    {
        public const byte Initial = 0;
        public const byte IdleSearch = 5;
        public const byte Attack = 6;
        public const byte Flee = 9;
        public const byte DeathWarning = 0x0B;
        public const byte Assist = 0x0C;
        public const byte DespawnReturn = 0x14;
        public const byte LeashReturn = 0x1D;
    }

    public static class MonsterMessageId
    {
        public const byte TargetChanged = 0x09;
        public const byte AlertChanged = 0x0A;
        public const byte DeathWarning = 0x0B;
        public const byte Lost = 0x0D;
        public const byte DespawnRequest = 0x0E;
    }
}
