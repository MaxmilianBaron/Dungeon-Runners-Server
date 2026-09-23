using System;
using System.Collections.Generic;
using System.Net.Sockets;
using DungeonRunners.Engine;
using DungeonRunners.Data;
using DungeonRunners.Core;

namespace DungeonRunners.Networking
{
    public class RRConnection
    {
        public int ConnId { get; private set; }
        public TcpClient Client { get; private set; }
        public NetworkStream Stream { get; private set; }
        public object SendLock { get; } = new object();
        public bool IsConnected { get; set; }
        public string LoginName { get; set; }
        public bool LoginAdmissionPending { get; set; }
        public bool LoginAdmitted { get; set; }
        public int ReplacementConnectionId { get; set; }
        public uint PeerId24 { get; set; }

        public uint CurrentZoneId { get; set; }
        public string CurrentZoneGcType { get; set; } = "world.town";

        private int _playerPosFixedX = 480 * 0x100;
        private int _playerPosFixedY = -191 * 0x100;
        private int _playerPosFixedZ;
        public int PlayerPosFixedX { get => _playerPosFixedX; set => _playerPosFixedX = value; }
        public int PlayerPosFixedY { get => _playerPosFixedY; set => _playerPosFixedY = value; }
        private int _playerHeadingFixed;
        public int PlayerHeadingFixed { get => _playerHeadingFixed; set => _playerHeadingFixed = value; }
        public readonly System.Collections.Generic.Queue<(int X, int Y)> AvatarAggroSampleQueue = new System.Collections.Generic.Queue<(int, int)>();
        public readonly System.Collections.Generic.Queue<(byte MoveType, int Heading, int X, int Y, bool RequiresAdmission, bool TerminalRestartAdmission, ulong Ordinal)> PendingReflectedAvatarMovementSamples = new System.Collections.Generic.Queue<(byte, int, int, int, bool, bool, ulong)>();
        public readonly System.Collections.Generic.Queue<(byte MoveType, int Heading, int X, int Y, bool RequiresAdmission, bool TerminalRestartAdmission, ulong Ordinal)> HeldReflectedAvatarMovementSamples = new System.Collections.Generic.Queue<(byte, int, int, int, bool, bool, ulong)>();
        public readonly System.Collections.Generic.Queue<(byte MoveType, int Heading, int X, int Y, bool RequiresAdmission, bool TerminalRestartAdmission, ulong Ordinal)> AdmittedReflectedAvatarMovementSamples = new System.Collections.Generic.Queue<(byte, int, int, int, bool, bool, ulong)>();
        public readonly System.Collections.Generic.Queue<(byte MoveType, int Heading, int X, int Y, bool RequiresAdmission, bool TerminalRestartAdmission, ulong Ordinal)> ReadyReflectedAvatarMovementSamples = new System.Collections.Generic.Queue<(byte, int, int, int, bool, bool, ulong)>();
        public bool ReflectedAvatarMovementStreamAdmitted { get; set; }
        public bool ReflectedAvatarMovementTerminalRestartPending { get; set; }
        public bool ReflectedAvatarMovementPostAdmissionBarrierPending { get; set; }
        public bool ReflectedAvatarMovementIdleBatchBarrierPending { get; set; }
        public ulong ReflectedAvatarMovementReceivedOrdinal { get; set; }
        public ulong ReflectedAvatarMovementAppliedOrdinal { get; set; }
        public readonly System.Collections.Generic.Queue<(byte MoveType, int Heading, int X, int Y, uint SimulationApplyTick, ulong Ordinal)> OwnerAckFollowClientMovementSamples = new System.Collections.Generic.Queue<(byte, int, int, int, uint, ulong)>();
        public ulong OwnerAckFollowClientReceivedOrdinal { get; set; }
        public ulong OwnerAckFollowClientAppliedOrdinal { get; set; }
        public bool HasOwnerAckFollowClientPosition { get; set; }
        public int OwnerAckFollowClientPosFixedX { get; set; }
        public int OwnerAckFollowClientPosFixedY { get; set; }
        public int OwnerAckFollowClientPosFixedZ { get; set; }
        public int OwnerAckFollowClientHeadingFixed { get; set; }
        public bool OwnerAckFollowClientMovingThisFrame { get; set; }
        public readonly System.Collections.Generic.Queue<(byte MoveType, int Heading, int X, int Y, uint SimulationApplyTick, ulong Ordinal)> UnitFollowClientMovementSamples = new System.Collections.Generic.Queue<(byte, int, int, int, uint, ulong)>();
        public ulong UnitFollowClientReceivedOrdinal { get; set; }
        public ulong UnitFollowClientAppliedOrdinal { get; set; }
        public bool HasUnitFollowClientPosition { get; set; }
        public int UnitFollowClientPosFixedX { get; set; }
        public int UnitFollowClientPosFixedY { get; set; }
        public int UnitFollowClientPosFixedZ { get; set; }
        public int UnitFollowClientHeadingFixed { get; set; }
        public bool UnitFollowClientMovingThisFrame { get; set; }
        public byte UnitFollowClientMoverMode { get; set; } = 1;
        public bool UnitFollowClientWriterStateInitialized { get; set; }
        public bool UnitFollowClientClientControlEnabled { get; set; } = true;
        public bool UnitFollowClientFollowingClient { get; set; } = true;
        public bool UnitFollowClientActionActive { get; set; }
        public bool HasReflectedAvatarPosition { get; set; }
        public int ReflectedAvatarPosFixedX { get; set; }
        public int ReflectedAvatarPosFixedY { get; set; }
        public int ReflectedAvatarPosFixedZ { get; set; }
        public int ReflectedAvatarHeadingFixed { get; set; }
        public byte ReflectedAvatarUnitMoverMode { get; set; } = 1;
        public byte OwnerUnitBehaviorMoverMode { get; set; } = 1;
        public bool ReflectedAvatarMovingThisFrame { get; set; }
        public int AggroSamplePosFixedX { get; set; } = 480 * 0x100;
        public int AggroSamplePosFixedY { get; set; } = -191 * 0x100;
        public byte SessionID { get; set; } = 0;
        public uint UnitBehaviorId { get; set; }
        public HashSet<uint> SentPassiveModifierIds { get; } = new HashSet<uint>();
        public byte LastRawMoveCount { get; set; } = 0;
        public byte[] LastRawMoveData { get; set; } = null;
        public uint LastOutboundHPWire { get; set; } = 0;
        public string LastOutboundHPSource { get; set; } = null;
        public bool HasActiveUseTarget { get; set; } = false;
        public ushort ActiveUseTargetId { get; set; } = 0;
        public int ActiveUseTargetPlayerConnId { get; set; } = 0;
        public bool ActiveUseTargetRemoved { get; set; } = false;
        public byte ActiveUseTargetFlags { get; set; } = 0;
        public ushort ActiveUseTargetComponentId { get; set; } = 0;
        public byte ActiveUseTargetSessionId { get; set; } = 0;
        public byte ActiveUseTargetResponseId { get; set; } = 0;
        public bool ActiveUseTargetActionFailureQueued { get; set; } = false;
        public long ActiveUseTargetActionSequence { get; set; }
        public long LastPlayerUseTargetActionSequence { get; set; }
        public bool ActiveUseTargetInitUsePassed { get; set; } = false;
        public uint ActiveUseTargetInitUseEvaluationTick { get; set; } = uint.MaxValue;
        public ushort ActiveUseTargetInitUseEvaluationTargetId { get; set; } = 0;
        public bool ActiveUseTargetStartedWeaponUse { get; set; } = false;
        public bool ActiveUseTargetVisibleHit { get; set; } = false;
        public int ActiveUseTargetInitUseRangeF32 { get; set; }
        public int ActiveUseTargetInitUseDistanceF32 { get; set; }
        public int ActiveUseTargetClientToleranceF32 { get; set; }
        public long ActiveUseTargetLastProjectileSeq { get; set; } = 0;
        public int ActiveUseTargetLastImpactTick { get; set; } = -1;
        public bool ActiveUseTargetWeaponReleasePending { get; set; }
        public bool UsePositionActionMirrored { get; set; }
        public bool UsePositionActionStoppedFollowClient { get; set; }
        public ushort UsePositionActionComponentId { get; set; }
        public byte UsePositionManipulatorId { get; set; }
        public byte UsePositionActionSessionId { get; set; }
        public int UsePositionActionTargetFixedX { get; set; }
        public int UsePositionActionTargetFixedY { get; set; }
        public int UsePositionActionTargetFixedZ { get; set; }
        public uint UsePositionActionApplyTick { get; set; }
        public uint UsePositionActionBusyUntilTick { get; set; }
        public bool UsePositionWeaponAction { get; set; }
        public int UsePositionActionAdmissionFollowClientRecords { get; set; }
        public bool HasPendingUseTargetAction { get; set; }
        public ushort PendingUseTargetActionId { get; set; }
        public uint PendingUseTargetMonsterEntityId { get; set; }
        public int PendingUseTargetPlayerConnId { get; set; }
        public byte PendingUseTargetFlags { get; set; }
        public ushort PendingUseTargetComponentId { get; set; }
        public byte PendingUseTargetSessionId { get; set; }
        public byte PendingUseTargetResponseId { get; set; }
        public uint PendingUseTargetReceivedTick { get; set; }
        public long PendingUseTargetActionSequence { get; set; }
        public uint PlayerBehaviorActionStoppedTick { get; set; } = uint.MaxValue;
        public bool PendingUseTargetIsRedundant { get; set; }
        public int LastAvatarPreSuffixActionSliceFrame { get; set; } = -1;
        public bool PendingClientControlReset { get; set; } = false;
        public bool RemoteActionRelayed { get; set; } = false;
        public ushort PendingClientControlResetComponentId { get; set; } = 0;
        public uint PendingClientControlResetNextAttemptTick { get; set; }
        public byte PendingClientControlResetAttempts { get; set; } = 0;
        public ushort UnitContainerId { get; set; } = 0;
        public ushort ModifiersId { get; set; } = 0;
        public bool ZoneSpawnInvulnerabilityActive { get; set; } = false;
        public bool ZoneSpawnInvulnerabilityPending { get; set; } = false;
        public bool ZoneSpawnInvulnerabilityQueued { get; set; } = false;
        public uint ZoneSpawnInvulnerabilityGeneration { get; set; }
        public uint ZoneSpawnInvulnerabilityDueTick { get; set; }
        public uint ZoneSpawnInvulnerabilityExpiresTick { get; set; }
        public uint ZoneSpawnInvulnerabilitySentTick { get; set; }

        public bool IsAdmin { get; set; } = true;

        public ushort DialogManagerId { get; set; } = 0;
        public string CurrentDialogNpcId { get; set; } = null;
        public ushort PendingPortalComponentId { get; set; } = 0;
        public ushort PendingPortalTargetEntityId { get; set; } = 0;
        public byte PendingPortalResponseId { get; set; } = 0;
        public byte PendingPortalSessionId { get; set; } = 0;
        public int PendingPortalTargetFixedX { get; set; }
        public int PendingPortalTargetFixedY { get; set; }
        public ushort PendingDroppedItemComponentId { get; set; } = 0;
        public ushort PendingDroppedItemTargetEntityId { get; set; } = 0;
        public byte PendingDroppedItemResponseId { get; set; } = 0;
        public byte PendingDroppedItemSessionId { get; set; } = 0;
        public string PendingDroppedItemInstanceKey { get; set; } = "";
        public ushort PendingWorldEntityChestComponentId { get; set; } = 0;
        public ushort PendingWorldEntityChestTargetEntityId { get; set; } = 0;
        public byte PendingWorldEntityChestResponseId { get; set; } = 0;
        public byte PendingWorldEntityChestSessionId { get; set; } = 0;
        public string PendingWorldEntityChestInstanceKey { get; set; } = "";
        public ushort PendingCheckpointComponentId { get; set; } = 0;
        public ushort PendingCheckpointTargetEntityId { get; set; } = 0;
        public byte PendingCheckpointResponseId { get; set; } = 0;
        public byte PendingCheckpointSessionId { get; set; } = 0;
        public string PendingCheckpointInstanceKey { get; set; } = "";
        public bool PendingPortalTransition { get; set; } = false;
        public bool PendingPortalTransitionReflected { get; set; } = false;
        public ushort PendingPortalTransitionComponentId { get; set; } = 0;
        public ushort PendingPortalTransitionTargetEntityId { get; set; } = 0;
        public byte PendingPortalTransitionResponseId { get; set; } = 0;
        public byte PendingPortalTransitionSessionId { get; set; } = 0;
        public string PendingPortalTransitionTargetZone { get; set; } = "";
        public string PendingPortalTransitionSpawnPoint { get; set; } = "";
        public uint NextQuestInstanceId = 1;
        public uint PendingQuestHash { get; set; } = 0;
        public ushort QuestManagerId { get; set; } = 0;
        public uint PendingQuestNpcEntityId { get; set; } = 0;
        public uint PendingTurnInInstanceId { get; set; } = 0;
        public bool IsAbandonConfirmed { get; set; } = false;
        public int AbandonClickCount { get; set; } = 0;

        public MessageQueue MessageQueue { get; private set; }
        public List<byte> InboundGameBytes { get; } = new List<byte>();
        public byte MovementGeneration = 0;
        public bool TickUpdatesActive { get; set; }
        public GCObject Avatar { get; set; }
        public GCObject Player { get; set; }
        public uint UpdateNumber { get; set; } = 0;
        public ClientEntitySchedulerMirror EntitySchedulerMirror { get; } = new ClientEntitySchedulerMirror();

        public int PendingSpawnFixedX;
        public int PendingSpawnFixedY;
        public int PendingSpawnFixedZ;
        public string PendingSpawnPoint { get; set; } = "";
        public bool PendingSpawnPreserveAuthoredPosition;
        public bool PendingSpawnHeadingOverride;
        public bool PendingSavedPositionResume;

        public string CurrentZoneName { get; set; } = "";
        public uint InstanceId { get; set; } = 0;
        public string RuntimeInstanceKey { get; set; } = "";
        public string PreviousRuntimeInstanceKey { get; set; } = "";
        public string AvatarGcType { get; set; } = "";
        public string ClassName { get; set; } = "Fighter";
        public ushort SkillsComponentId { get; set; } = 0;
        public ushort ManipulatorsComponentId { get; set; } = 0;
        public ushort ModifiersComponentId { get; set; } = 0;
        public ushort BehaviorComponentId { get; set; } = 0;
        public int PlayerLevel { get; set; } = 1;
        public uint CharSqlId { get; set; } = 0;
        public bool GroupConnectedSent { get; set; } = false;
        public int PlayerPosFixedZ { get => _playerPosFixedZ; set => _playerPosFixedZ = value; }
        public bool HasLivePlayerPosition { get; set; } = false;
        private int _livePlayerPosFixedX;
        private int _livePlayerPosFixedY;
        private int _livePlayerPosFixedZ;
        public int LivePlayerPosFixedX { get => _livePlayerPosFixedX; set => _livePlayerPosFixedX = value; }
        public int LivePlayerPosFixedY { get => _livePlayerPosFixedY; set => _livePlayerPosFixedY = value; }
        public int LivePlayerPosFixedZ { get => _livePlayerPosFixedZ; set => _livePlayerPosFixedZ = value; }
        private int _livePlayerHeadingFixed;
        public int LivePlayerHeadingFixed { get => _livePlayerHeadingFixed; set => _livePlayerHeadingFixed = value; }
        public bool LivePlayerMovingThisFrame { get; set; } = false;
        public bool IsSpawned { get; set; } = false;
        public bool UseTargetMovingActive { get; set; } = false;
        public bool UseTargetMovingActionMirrored { get; set; } = false;
        public bool UseTargetMovingActivate { get; set; } = false;
        public ushort UseTargetMovingActivateTargetId { get; set; } = 0;
        public int UseTargetMovingActivateRangeFixed { get; set; } = 0;
        public int UseTargetMovingTargetFixedX { get; set; } = 0;
        public int UseTargetMovingTargetFixedY { get; set; } = 0;
        public int UseTargetMovingFollowConnId { get; set; } = 0;
        public ushort UseTargetMovingEntityId { get; set; } = 0;
        public string UseTargetMovingInstanceKey { get; set; } = "";
        public bool UseTargetMovingClientDriven { get; set; } = false;
        public bool UseTargetMovingHasFixedState { get; set; } = false;
        public int UseTargetMovingFixedX { get; set; } = 0;
        public int UseTargetMovingFixedY { get; set; } = 0;
        public int UseTargetMovingFixedZ { get; set; } = 0;
        public int UseTargetMovingHeadingFixed { get; set; } = 0;
        public uint UseTargetMovingLastAdvancedTick { get; set; } = uint.MaxValue;
        public byte UseTargetMovingPendingMoveCount { get; set; } = 0;
        public byte[] UseTargetMovingPendingMoveData { get; set; } = Array.Empty<byte>();
        public List<(int X, int Y)> UseTargetMovingPathFixed { get; } = new List<(int, int)>();
        public int UseTargetMovingPathIndex { get; set; } = 0;
        public int UseTargetMovingPathTargetFixedX { get; set; } = 0;
        public int UseTargetMovingPathTargetFixedY { get; set; } = 0;
        public int UseTargetMovingPathRequestId { get; set; } = -1;
        public string UseTargetMovingPathRequestInstanceKey { get; set; } = "";
        public PathMap UseTargetMovingPathRequestMap { get; set; }
        public uint UseTargetMovingPathRequestTick { get; set; } = 0;
        public uint UseTargetMovingPathReadyTick { get; set; } = 0;
        public bool UseTargetMovingMoverActive { get; set; } = false;
        public bool UseTargetMovingMoverStarted { get; set; } = false;
        public bool UseTargetMovingMoverMovingThisFrame { get; set; } = false;
        public bool UseTargetMovingMoverInitialUpdatePending { get; set; } = false;
        public bool UseTargetMovingInitUseInitialized { get; set; } = false;
        public bool ActiveUseTargetInitUseCooldownStarted { get; set; } = false;
        public ushort ActiveUseTargetInitUseCooldownComponentId { get; set; } = 0;
        public byte ActiveUseTargetInitUseCooldownActionId { get; set; } = 0;
        public bool UseTargetMovingUnitFollowClientWaitActive { get; set; } = false;
        public ulong UseTargetMovingUnitFollowClientWaitThroughOrdinal { get; set; } = 0;
        public bool UseTargetMovingUsing { get; set; } = false;
        public bool UseTargetMovingInputAdmitted { get; set; } = true;
        public uint UseTargetMovingAdmissionTick { get; set; } = 0;
        public byte UseTargetMovingRetargetCountdown { get; set; } = 15;
        public byte UseTargetMovingPathRetryCountdown { get; set; } = 0;
        public uint LastClientEntityUpdateSerial { get; set; }
        public ushort ReplicaAvatarId { get; set; } = 0;
        public ushort ReplicaBehaviorId { get; set; } = 0;
        public ushort ReplicaPlayerId { get; set; } = 0;
        public ushort ReplicaSkillsId { get; set; } = 0;
        public ushort ReplicaManipId { get; set; } = 0;
        public ushort ReplicaModId { get; set; } = 0;
        public bool FullHPBaselineOnNextSpawn { get; set; } = false;
        public bool RespawnFullHPPending { get; set; } = false;
        public bool AllowFlush { get; set; } = false;
        public bool PortalClientEntityEpochClosing { get; set; } = false;
        public uint PendingPortalTransitionResponseWriterSerial { get; set; } = 0;

        public bool HasSavedTownPortal { get; set; } = false;
        public string TownPortalZoneName { get; set; } = "";
        public string TownPortalTargetZone { get; set; } = "";
        public uint TownPortalZoneId { get; set; } = 0;
        public int TownPortalPosFixedX { get; set; }
        public int TownPortalPosFixedY { get; set; }
        public int TownPortalPosFixedZ { get; set; }

        public string ZonePortalSource { get; set; } = "";
        public int ObeliskClickIndex { get; set; } = 0;

        public RRConnection(int connId, TcpClient client, NetworkStream stream)
        {
            ConnId = connId;
            Client = client;
            ConfigureLatencySensitiveSocket(client);
            Stream = stream;
            IsConnected = true;
            LoginName = null;
            PeerId24 = 0;
            CurrentZoneId = 0;
            MessageQueue = new MessageQueue();
        }

        private static void ConfigureLatencySensitiveSocket(TcpClient client)
        {
            if (client == null)
                return;
            try
            {
                client.NoDelay = true;
                if (client.Client != null)
                    client.Client.NoDelay = true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RR-CONNECTION] tcp-nodelay state=failed message='{ex.Message}'");
            }
        }

        public void Disconnect()
        {
            IsConnected = false;
            try
            {
                Stream?.Close();
                Client?.Close();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RR-CONNECTION] disconnect state=error message='{ex.Message}'");
            }
        }
    }
}
