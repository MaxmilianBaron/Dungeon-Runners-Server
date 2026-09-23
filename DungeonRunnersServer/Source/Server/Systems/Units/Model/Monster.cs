using System;
using System.Collections.Generic;
using DungeonRunners.Core;
using DungeonRunners.Engine;

namespace DungeonRunners.Combat
{
    public class Monster
    {
        private CombatTarget _targetView;
        public CombatTarget TargetView => _targetView ??= new CombatTarget(this);
        public CombatPlayer Summoner { get; internal set; }
        public bool UseHenchmanCurveTables;
        public string ZoneAction;
        public uint IdleFollowTargetEntityId;
        public MonsterCombatCredit CombatCredit { get; } = new MonsterCombatCredit();
        public uint EntityId;
        public uint BehaviorId;
        public uint SkillsId;
        public uint ManipulatorsId;
        public uint ModifiersId;
        public uint UnitId;
        public int UseTargetCount;
        public string GCType;
        public string BehaviourType;
        public string SpawnBehaviourType;
        public string Name;
        public string Faction;
        public int FactionID;
        public bool UnitDescIsAlwaysFriendly;
        public string CreatureType;
        public string Element;
        public string Tier;
        public byte Level = 10;
        public int DifficultyF32 = 0x100;
        public int ExperienceDifficultyF32 = 0x100;
        public int ExperienceValueMultF32 = 0x100;
        public ushort HealthFactorF32 = 0x100;
        public ushort DamageFactorF32 = 0x100;
        public ushort LootFactorF32 = 0x100;
        public byte LootPlayerLimit = 1;
        public bool ServerReconstructionScalingApplied;
        public byte ServerReconstructionPartySize = 1;
        public byte ServerReconstructionDifficulty;
        public int ServerReconstructionGoldPercent = 100;
        public int ServerReconstructionItemLevelBonus;
        public string SpawnGCType;
        public List<string> AuthoredArchetypeAncestry = new List<string>();
        public string InstanceKey;
        public uint MaxHPWire;
        public uint CurrentHPWire;
        public uint LastOutboundHPWire;
        public byte EntityFlags;
        public uint UnitFlags = 0x6;
        public bool UnitDescIsAlive = true;
        public byte UnitState31B;
        public byte StockUnitState;
        public ushort StockUnitStateTicksRemaining;
        public ushort StockUnitLifespanTicksRemaining;
        public int StockUnitLifespanF32;
        public int StockUnitLifespanIncrementF32;
        public uint StockUnitRuntimeFlags;
        public byte StockUnitRuntimePercent = 100;
        public byte StockUnitRuntimeState;
        public byte OnDeadTicksRemaining;
        public bool OnDeadDispatched;
        public bool DeathExperienceDispatched;
        public ushort CorpseTicksRemaining => StockUnitState == 7 ? StockUnitStateTicksRemaining : (ushort)0;
        public ushort FadeTicksRemaining => StockUnitState == 9 ? StockUnitStateTicksRemaining : (ushort)0;
        public ushort RespawnTicksRemaining => StockUnitState == 6 ? StockUnitStateTicksRemaining : (ushort)0;
        public bool DeathLifecycleActive;
        public bool DeathRemoveSent;
        public uint MaxManaWire;
        public uint CurrentManaWire;
        public int SilenceAttributeValue;
        public int BaseDamage;
        public int AttackRatingF32;
        public int DamageModPercent = 100;
        public int DamageTakenModPercent = 100;
        public int DamageVolatilityF32;
        public int WeaponDamageF32;
        public int WeaponStunMod = 100;
        public int StunMod = 100;
        public int StunResist = 1;
        public string WeaponClass = "HTH";
        public string WeaponDamageType = "CRUSHING";
        public bool WeaponUsesProjectile = false;
        public int WeaponShotType;
        public int WeaponProjectileSpeedF32;
        public int WeaponProjectileSizeF32;
        public int WeaponRangeF32;
        public int WeaponClassId = 1;
        public int DamageTypeId = 0;
        public int HealthRegenF32;
        public bool HasAuthoredHealthRegen;
        public System.Collections.Generic.List<MonsterAttributeModifier> AttributeModifiers;
        public int ManaRegenF32;
        public bool HasAuthoredManaRegen;
        public int CritChanceF32;
        public int DefenseRatingF32;
        public int DamageImmunityPercent;
        public int DamageResistPercent;
        public int CrushingResistPercent;
        public int PiercingResistPercent;
        public int SlashingResistPercent;
        public int DivineResistPercent;
        public int FireResistPercent;
        public int IceResistPercent;
        public int PoisonResistPercent;
        public int ShadowResistPercent;
        public int MagicDamageResistPercent;
        public int DivineDamageTakenModPercent = 100;
        public int FireDamageTakenModPercent = 100;
        public int IceDamageTakenModPercent = 100;
        public int PoisonDamageTakenModPercent = 100;
        public int ShadowDamageTakenModPercent = 100;
        public int PosFixedX, PosFixedY, PosFixedZ;
        public int SpawnPosFixedX, SpawnPosFixedY, SpawnPosFixedZ;
        public bool ClientVisibleMoveInitialized;
        public bool ClientVisibleMoveActive;
        public uint ClientVisibleMoveLastTick;
        public int ClientVisibleFixedX, ClientVisibleFixedY;
        public bool ClientVisibleFixedInit;
        public int ClientVisibleMoveTargetFixedX, ClientVisibleMoveTargetFixedY;
        public bool ClientVisibleMoveTargetFixedInit;
        public int ClientVisibleHeadingFixed;
        public bool ClientVisibleHeadingInit;
        public bool ClientVisibleMovingThisFrame;
        public uint ClientVisibleMovingThisFrameTick;
        public int ChaseHeadingFixed;
        public bool ChaseHeadingInit;
        public int MoveInDirectionHeadingFixed;
        public bool MoveInDirectionHeadingInit;
        public bool UnitMoverMovingThisFrame;
        public int UnitMoverDesiredHeadingFixed;
        public bool UnitMoverDesiredHeadingInit;
        public int TurnRateDegrees = 360;
        public byte SessionId;
        public int HeadingFixed;
        public byte WorldEntityAnimationState = 1;
        public ushort WorldEntityAnimationId = 100;
        public uint WorldEntityAnimationPlayTime;
        public uint WorldEntityAnimationSpeed = 0x100;

        public int AggroRangeF32;
        public int PerceptionRangeF32;
        public int ShoutRangeF32;
        public int LeashRangeF32;
        public int AttackRangeF32;
        public int UnitDescAttackRangeF32;
        public int ClientSyncToleranceF32;
        public int CollisionRadiusF32;
        public int BoundingBoxRadiusXYF32;
        public int BoundingBoxMinZF32;
        public int BoundingBoxMaxZF32;
        public int SizeModPercent = 100;
        public string AttackType;
        public string IdleAction;
        public string LogicType;
        public string AttackStyle;
        public bool Retreatable;
        public bool Leashed;
        public bool UseIdleTime;
        public ushort BaseTime;
        public ushort VariableTime;
        public uint State0IdleDelayTicks;
        public bool State0IdleInitialized;
        public bool AutoScan;
        public bool AvoidUnits;
        public bool TurnBeforeMoving;
        public bool PlayerControlled;
        public int CollisionBand;
        public int CollisionPriority;
        public int ScanFrequencyF32;
        public short ProximityScanCountdownTicks = 30;
        public ushort CorpseLingerTicks = 900;
        public bool AutoRespawn = false;
        public ushort RespawnRateTicks = 3600;
        public bool RespawnWhenClear = true;
        public int AttackSpeedF32;
        public int AttackCooldownF32;
        public int MoveSpeedF32;
        public int WalkSpeedF32;
        public int SpeedMod = 100;
        public int BaseSpeedMod = 100;
        public int WanderRangeF32;
        public uint LastStateCounter;

        public uint RngSeed;
        public MersenneTwister Rng;
        public uint EntityUpdateAdmissionTick;
        public uint EntityUpdateAdmissionDelayTicks;
        public UnitSlotState Slots = new UnitSlotState();
        public MonsterAiRuntime Ai = new MonsterAiRuntime();

        public Dictionary<string, ManipulatorData> Manipulators;
        public List<KeyValuePair<string, ManipulatorData>> ManipulatorOrder = new List<KeyValuePair<string, ManipulatorData>>();
        public bool AggroSent { get; set; }

        public byte UpdateNumber = 0;

        private volatile int _stateInt = (int)MonsterState.Idle;
        public MonsterState State
        {
            get => (MonsterState)_stateInt;
            set => _stateInt = (int)value;
        }
        public bool IsAlive = true;
        private volatile uint _targetId = 0;
        public uint TargetId
        {
            get => _targetId;
            set => _targetId = value;
        }
        public int NextAttackTick;
        public bool AttackPending;
        public int AttackCommitTick;
        public int AttackSoundTick;
        public bool AttackSoundPending;
        public bool HasAttackSound;
        public uint AttackSoundRaw;
        public uint AttackSoundGateRaw;
        public uint AttackSoundRepeatRaw;
        public byte AttackSessionId;
        public byte AttackAnimationIndex;
        public uint AttackUseRaw;
        public byte DamageReactionActionId;
        public byte DamageReactionPhase;
        public int DamageReactionHeadingFixed;
        public int DamageReactionStrength;
        public int DamageReactionMoveTicksRemaining;
        public uint DamageReactionRecoveryEndTick;
        public int MeleeSmClock;
        public uint MeleeScanTargetId;
        public bool FollowMsgArmed;
        public int FollowMsgNextTick;
        public int ScanSubstate;
        public int ScanCountdown;
        public int ScanMsgNextTick;
        public int ScanRepositionCount;
        public bool FollowInputPending;
        public uint FollowInputTargetId;
        public uint FollowInputApplyTick;
        public uint FollowInputDirectPlayerWeaponHitTick;
        public uint FollowInputPlayerDamageTick;
        public bool FollowAdmissionReachable = true;
        public bool ProximityFollowAdmissionPending;
        public bool ProximityTargetScanMatched;
        public uint ProximityAttackInputApplyTick;
        public bool DeferredTargetScanMessagePending;
        public bool DeferredTargetScanMessageReleased;
        public uint BehaviorAssistSourceEntityId;
        public bool BehaviorTargetUsesClientSimulationPosition;
        public bool WanderActionTerminationPending;
        public bool RetiredWanderMoverActive;
        public int RetiredWanderTargetFixedX;
        public int RetiredWanderTargetFixedY;
        public bool SearchForAttackMoveActive;
        public int SearchForAttackTargetFixedX;
        public int SearchForAttackTargetFixedY;
        public bool FollowRepositionMoveActive;
        public int FollowRepositionTargetFixedX;
        public int FollowRepositionTargetFixedY;
        public bool FollowFindLineOfSightMoveActive;
        public int FollowFindLineOfSightTargetFixedX;
        public int FollowFindLineOfSightTargetFixedY;
        public byte UnitMoverPathOwner;
        public int RetreatRangeSquaredF32 = 640000 * 256;
        public bool ReturnMoveActive;
        public bool ReturnRemovalPending;
        public int ReturnMoveTargetFixedX;
        public int ReturnMoveTargetFixedY;
        public int ModifierLocalIdGenerator;
        public int UnitMoverPathRequestId = -1;
        public string UnitMoverPathRequestInstanceKey;
        public PathMap UnitMoverPathRequestMap;
        public List<(int FixedX, int FixedY)> UnitMoverPathFixed = new List<(int FixedX, int FixedY)>();
        public int UnitMoverPathIndex;
        public int UnitMoverPathTargetFixedX;
        public int UnitMoverPathTargetFixedY;
        public byte UnitMoverPathRetryCountdown;
        public uint UnitMoverPathRequestTick;
        public uint UnitMoverPathReadyTick;
        public Behavior.MonsterBehavior2 Behavior;
        public bool FollowClientVisible;
        public bool AttackClientVisible;
        public bool AttackActionQueued;
        public uint AttackActionAdmissionTick;
        public bool LocalAttackActionUsePending;
        public uint LocalAttackActionUseTargetId;
        public bool LocalAttackActionUseSuppressPacket;
        public uint LocalAttackActionUseTick;
        public bool AttackContactOnly;
        public bool AttackHitResolved;
        public bool UsePrimaryActiveSkillThisAttack;
        public bool ActiveSkillEffectPending;
        public bool ActiveSkillEffectResolved;
        public uint ActiveSkillEffectTargetEntityId;
        public int ActiveSkillEffectStartTick;
        public int ActiveSkillEffectCommitTick;
        public int ActiveSkillEffectEndTick;
        public bool ActiveSkillSelfCycle;
        public ushort ActiveSkillBusyTicksRemaining;
        public ushort ActiveSkillAnimationTicks;
        public ushort ActiveSkillTriggerTicks;
        public object ActiveSkillEffectPlan;
        public string ActiveSkillCastModifierKey;
        public bool ActiveSkillCastModifierApplied;
        public string CowardiceModifierKey;
        public string CowardiceSkillPath;
        public string CowardiceEffectPath;
        public uint CowardiceSourceEntityId;
        public int CowardicePowerLevelF32;
        public int CowardiceRemainingTicks;
        public bool CowardicePermanent;
        public bool CowardiceRemoveOnDeath;
        public bool CowardiceAvoidCorners = true;
        public bool FleeActionActive;
        public byte FleeTurnCountdownTicks;
        public bool FleeTouchingCorner;
        public bool AttackWeaponCycleFromUseTargetInterrupt;
        public List<MonsterActiveSkillRuntime> ActiveSkills = new List<MonsterActiveSkillRuntime>();
        public List<MonsterProcModifierRuntime> ProcModifiers = new List<MonsterProcModifierRuntime>();
        public List<string> BlockedProcModifierPaths = new List<string>();
        public MonsterActiveSkillRuntime PrimaryAttackSkill;
        public MonsterActiveSkillRuntime SelectedActiveSkill;
        public uint SelectedActiveSkillTargetEntityId;
        public bool ActiveSkillUseCommittedThisAttack;
        public string PrimaryActiveSkillPath;
        public byte PrimaryActiveSkillId = 10;
        public int PrimaryActiveSkillRangeF32;
        public int PrimaryActiveSkillSpellUseRangeF32;
        public int PrimaryActiveSkillMinimumRangeF32 = -1;
        public string PrimaryActiveSkillTargetType;
        public string PrimaryActiveSkillSpellUse;
        public bool PrimaryActiveSkillHasSelfHealthPct;
        public int PrimaryActiveSkillSelfHealthPctF32;
        public bool PrimaryActiveSkillHasTargetHealthPct;
        public int PrimaryActiveSkillTargetHealthPctF32;
        public ushort PrimaryActiveSkillCooldownTicks;
        public ushort PrimaryActiveSkillCooldownRemainingTicks;
        public uint PrimaryActiveSkillCooldownLastTick;
        public int PrimaryActiveSkillAnimationId;
        public string PrimaryActiveSkillEffect;
        public string PrimaryActiveSkillCastModifier;
        public int AttackStartTick;
        public int AttackEndTick;
        public int AttackWeaponSoundCount;
        public int AttackRepeatSoundCount;
        public int[] AttackTotalFrames = new int[] { 0, 0, 0 };
        public int[] AttackHitFrames = new int[] { 0, 0, 0 };
        public int[] AttackSoundFrames = new int[] { 0, 0, 0 };
        public bool[] AttackFrameResolved = new bool[] { false, false, false };
        public ResolutionSource AttackTimingSource = ResolutionSource.Blocked;
        public string AttackTimingReason = "unresolved";
        public int AttackCommitTargetFixedX;
        public int AttackCommitTargetFixedY;
        public bool AttackCommitTargetFixedInit;
        public uint CombatContactTargetId;
        public uint CombatContactUntilTick;
        public uint AlertSourceEntityId;
        private volatile bool _aggroTriggered = false;
        public bool AggroTriggered
        {
            get => _aggroTriggered;
            set => _aggroTriggered = value;
        }
        private Dictionary<uint, int> _threatTable = new Dictionary<uint, int>();
        public string ZoneName;
        public string EncounterGroupKey;
        public EncounterRuntime Encounter;
        public uint EncounterObjectEntityId;
        public byte EncounterObjectState;
        public byte EncounterLiveUnitCount;
        public byte EncounterReturningUnitCount;
        public ushort EncounterActiveTimer;
        public ushort EncounterScanTimer = 0x1E;
        public bool EncounterScanEnabled = true;
        public int HP => (int)(CurrentHPWire / 256);
        public int MaxHP => (int)(MaxHPWire / 256);
        public int GetActiveAttributeModifierValue(string attribute)
        {
            if (AttributeModifiers == null || AttributeModifiers.Count == 0 || string.IsNullOrWhiteSpace(attribute))
                return 0;
            string key = CanonicalAttributeName(attribute);
            int value = 0;
            for (int modifierIndex = 0; modifierIndex < AttributeModifiers.Count; modifierIndex++)
            {
                MonsterAttributeModifier modifier = AttributeModifiers[modifierIndex];
                if (modifier?.Attributes != null && modifier.Attributes.TryGetValue(key, out int modifierValue))
                    value = checked(value + modifierValue);
            }
            return value;
        }

        private static string CanonicalAttributeName(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Replace("_", string.Empty).Replace(" ", string.Empty).Trim().ToUpperInvariant();
        }

        public void AddThreat(uint playerId, int amount)
        {
            if (_threatTable.ContainsKey(playerId))
                _threatTable[playerId] += amount;
            else
                _threatTable[playerId] = amount;
        }

        public void ClearTarget()
        {
            TargetId = 0;
            AlertSourceEntityId = 0;
            FollowAdmissionReachable = true;
            ProximityFollowAdmissionPending = false;
            ProximityTargetScanMatched = false;
            ProximityAttackInputApplyTick = 0;
            LocalAttackActionUsePending = false;
            LocalAttackActionUseTargetId = 0;
            LocalAttackActionUseSuppressPacket = false;
            LocalAttackActionUseTick = 0;
            DeferredTargetScanMessagePending = false;
            DeferredTargetScanMessageReleased = false;
            BehaviorAssistSourceEntityId = 0;
            BehaviorTargetUsesClientSimulationPosition = false;
            WanderActionTerminationPending = false;
            RetiredWanderMoverActive = false;
            RetiredWanderTargetFixedX = 0;
            RetiredWanderTargetFixedY = 0;
            SearchForAttackMoveActive = false;
            SearchForAttackTargetFixedX = 0;
            SearchForAttackTargetFixedY = 0;
            FollowRepositionMoveActive = false;
            FollowRepositionTargetFixedX = 0;
            FollowRepositionTargetFixedY = 0;
            FollowFindLineOfSightMoveActive = false;
            FollowFindLineOfSightTargetFixedX = 0;
            FollowFindLineOfSightTargetFixedY = 0;
            UnitMoverPathOwner = 0;
            ReturnMoveActive = false;
            ReturnRemovalPending = false;
            UnitMoverPathRequestId = -1;
            UnitMoverPathRequestInstanceKey = null;
            UnitMoverPathRequestMap = null;
            UnitMoverPathFixed.Clear();
            UnitMoverPathIndex = 0;
            UnitMoverPathTargetFixedX = 0;
            UnitMoverPathTargetFixedY = 0;
            UnitMoverPathRetryCountdown = 0;
            UnitMoverPathRequestTick = 0;
            UnitMoverPathReadyTick = 0;
            _threatTable.Clear();
        }
    }

    public enum MonsterState
    {
        Idle,
        Chase,
        Combat,
        Attacking,
        Return,
        Dead
    }

    public class MonsterActiveSkillRuntime
    {
        public string Path;
        public byte Id = 10;
        public int RangeF32;
        public int SpellUseRangeF32;
        public int SpellUseMinimumRangeF32 = -1;
        public string TargetType;
        public string SpellUse;
        public bool HasSelfHealthPct;
        public int SelfHealthPctF32;
        public bool HasTargetHealthPct;
        public int TargetHealthPctF32;
        public ushort CooldownTicks;
        public ushort CooldownRemainingTicks;
        public uint CooldownLastTick;
        public ushort DelayBeforeUseTicks;
        public uint ManaCostWire;
        public byte SkillLevel = 1;
        public byte RepeatCount = 1;
        public int AnimationId;
        public int ProfessionTypeMask;
        public string Effect;
        public string CastModifier;
        public bool InstantUse;
        public bool AddModifierWhileClosing;
        public bool IsPrimaryAttack;
    }

    public enum MonsterProcEventKind : byte
    {
        Hit,
        Death,
        ProjectileHit
    }

    public enum MonsterProcTargetKind : byte
    {
        Self,
        Object
    }

    public sealed class MonsterProcConditionRuntime
    {
        public MonsterProcEventKind EventKind;
        public string AttackType = "ANY";
        public int DamageTypeId = -1;
        public string ProjectileObjectType;
    }

    public sealed class MonsterProcModifierRuntime
    {
        public string SlotPath;
        public string ModifierPath;
        public string EffectPath;
        public int ChanceDivisor = 1;
        public MonsterProcTargetKind TargetKind;
        public bool RemoveOnDeath;
        public byte SkillLevel = 1;
        public int PowerLevelF32 = 0x100;
        public readonly List<MonsterProcConditionRuntime> Conditions = new List<MonsterProcConditionRuntime>();
        public object EffectPlan;
        public bool Executing;
    }

    public class MonsterAttributeModifier
    {
        public int TerminateWhenHitChance;
        public uint LocalId;
        public byte SkillLevel = 1;
        public string ModifierKey;
        public string ModifierPath;
        public string SkillPath;
        public string EffectPath;
        public uint SourceEntityId;
        public uint PowerLevel;
        public string StackRule;
        public System.Collections.Generic.Dictionary<string, int> Attributes;
        public int HitPointRegenBonus;
        public bool OverrideTable;
        public int RemainingTicks;
        public bool Permanent;
        public bool RemoveOnDeath;
    }
}
