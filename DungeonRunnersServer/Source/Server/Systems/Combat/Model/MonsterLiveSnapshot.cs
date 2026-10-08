using DungeonRunners.Combat.Behavior;

namespace DungeonRunners.Combat
{
    public enum MonsterLiveActionKind : byte
    {
        None,
        Spawn,
        Wander,
        Follow,
        MoveTo
    }

    public sealed class MonsterLiveSnapshot
    {
        public int FixedX { get; }
        public int FixedY { get; }
        public int FixedZ { get; }
        public int HeadingFixed { get; }
        public int TargetFixedX { get; }
        public int TargetFixedY { get; }
        public bool Moving { get; }
        public byte WorldEntityAnimationState { get; }
        public ushort WorldEntityAnimationId { get; }
        public uint WorldEntityAnimationPlayTime { get; }
        public uint WorldEntityAnimationSpeed { get; }
        public MonsterLiveActionKind ActionKind { get; }
        public MonsterLiveActionKind PendingActionKind { get; }
        public byte SpawnPhase { get; }
        public byte SpawnCounter { get; }
        public WanderStateSnapshot Wander { get; }
        public ushort ActionTargetEntityId { get; }
        public ushort PrimaryTargetEntityId { get; }
        public ushort SecondaryTargetEntityId { get; }
        public ushort CombatTimer { get; }
        public byte ActionGeneration { get; }
        public ushort TopPreviousState { get; }
        public ushort TopCurrentState { get; }
        public ushort TopNextState { get; }
        public StateMachineSnapshot TopStateMachine { get; }
        public ushort FollowPreviousState { get; }
        public ushort FollowCurrentState { get; }
        public ushort FollowNextState { get; }
        public StateMachineSnapshot FollowStateMachine { get; }

        public MonsterLiveSnapshot(
            int fixedX,
            int fixedY,
            int fixedZ,
            int headingFixed,
            int targetFixedX,
            int targetFixedY,
            bool moving,
            byte worldEntityAnimationState,
            ushort worldEntityAnimationId,
            uint worldEntityAnimationPlayTime,
            uint worldEntityAnimationSpeed,
            MonsterLiveActionKind actionKind,
            MonsterLiveActionKind pendingActionKind,
            byte spawnPhase,
            byte spawnCounter,
            WanderStateSnapshot wander,
            ushort actionTargetEntityId,
            ushort primaryTargetEntityId,
            ushort secondaryTargetEntityId,
            ushort combatTimer,
            byte actionGeneration,
            ushort topPreviousState,
            ushort topCurrentState,
            ushort topNextState,
            StateMachineSnapshot topStateMachine,
            ushort followPreviousState,
            ushort followCurrentState,
            ushort followNextState,
            StateMachineSnapshot followStateMachine)
        {
            FixedX = fixedX;
            FixedY = fixedY;
            FixedZ = fixedZ;
            HeadingFixed = headingFixed;
            TargetFixedX = targetFixedX;
            TargetFixedY = targetFixedY;
            Moving = moving;
            WorldEntityAnimationState = worldEntityAnimationState;
            WorldEntityAnimationId = worldEntityAnimationId;
            WorldEntityAnimationPlayTime = worldEntityAnimationPlayTime;
            WorldEntityAnimationSpeed = worldEntityAnimationSpeed;
            ActionKind = actionKind;
            PendingActionKind = pendingActionKind;
            SpawnPhase = spawnPhase;
            SpawnCounter = spawnCounter;
            Wander = wander;
            ActionTargetEntityId = actionTargetEntityId;
            PrimaryTargetEntityId = primaryTargetEntityId;
            SecondaryTargetEntityId = secondaryTargetEntityId;
            CombatTimer = combatTimer;
            ActionGeneration = actionGeneration;
            TopPreviousState = topPreviousState;
            TopCurrentState = topCurrentState;
            TopNextState = topNextState;
            TopStateMachine = topStateMachine;
            FollowPreviousState = followPreviousState;
            FollowCurrentState = followCurrentState;
            FollowNextState = followNextState;
            FollowStateMachine = followStateMachine;
        }
    }
}
