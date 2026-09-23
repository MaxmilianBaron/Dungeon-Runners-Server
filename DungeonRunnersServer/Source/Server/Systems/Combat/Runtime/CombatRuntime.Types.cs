using DungeonRunners.Core;
using DungeonRunners.Data;
using DungeonRunners.Networking;
using System.Collections.Generic;

namespace DungeonRunners.Combat
{
    public class PendingMonsterProjectileImpact
    {
        public long Sequence;
        public Monster Monster;
        public uint MonsterEntityId;
        public uint TargetEntityId;
        public string InstanceKey;
        public string ZoneName;
        public string Marker;
        public string Source;
        public int DistF32;
        public int FireTick;
        public int FlightTicks;
        public int DueTick;
        public bool ActiveSkillProjectile;
        public object ActiveSkillImpactPlan;
        public int StartFixedX;
        public int StartFixedY;
        public int StartFixedZ;
        public int CurrentFixedX;
        public int CurrentFixedY;
        public int CurrentFixedZ;
        public int DirectionFixedX;
        public int DirectionFixedY;
        public int VelocityFixedX;
        public int VelocityFixedY;
        public int TargetFixedX;
        public int TargetFixedY;
        public int ProjectileSpeedF32;
        public int ProjectileSizeF32;
        public int StepDistanceF32;
        public int InitialDistanceF32;
        public int CurrentDistanceF32;
        public int MaxDistanceF32;
        public int MaxLifetimeTicks;
        public int LastUpdateTick;
        public int UpdatesCompleted;
        public int DelayRemainingTicks;
        public uint DelayRaw;
        public bool Penetrate;
        public int RemainingPenetrations;
        public bool IgnoreCollisions;
        public bool SnapToGround;
        public bool SeekTargets;
        public int SeekDistanceF32;
        public int TurnRateF32;
        public int DirectionHeadingFixed;
        public int GroundOffsetFixedZ;
        public HashSet<uint> HitEntityIds = new HashSet<uint>();
    }

    public class CombatPlayer
    {
        private CombatTarget _targetView;
        public CombatTarget TargetView => _targetView ??= new CombatTarget(this);
        public uint EntityId;
        public string Name;
        public PlayerState PlayerState;
        private int _posFixedX;
        private int _posFixedY;
        private int _posFixedZ;
        public int PosFixedX
        {
            get => _posFixedX;
            set => _posFixedX = value;
        }
        public int PosFixedY
        {
            get => _posFixedY;
            set => _posFixedY = value;
        }
        public int PosFixedZ
        {
            get => _posFixedZ;
            set => _posFixedZ = value;
        }
        public int ClientSimulationPosFixedX;
        public int ClientSimulationPosFixedY;
        public int ClientSimulationPosFixedZ;
        public int UnitFinderMembershipPosFixedX;
        public int UnitFinderMembershipPosFixedY;
        public int OwnerAckFollowClientPosFixedX;
        public int OwnerAckFollowClientPosFixedY;
        public int OwnerAckFollowClientPosFixedZ;
        public byte OwnerAckFollowClientMoverMode = UnitMover.StoppedMode;
        public int UnitFollowClientPosFixedX;
        public int UnitFollowClientPosFixedY;
        public int UnitFollowClientPosFixedZ;
        public bool UnitFollowClientMovingThisFrame;
        public int MonsterFollowTargetPosFixedX;
        public int MonsterFollowTargetPosFixedY;
        public int MonsterFollowTargetPosFixedZ;
        public int MonsterActionTargetPosFixedX;
        public int MonsterActionTargetPosFixedY;
        public int MonsterActionTargetPosFixedZ;
        public byte MonsterActionTargetMoverMode = UnitMover.StoppedMode;
        public int PredictedLocation2DFixedX;
        public int PredictedLocation2DFixedY;
        public byte ClientSimulationMoverMode = UnitMover.StoppedMode;
        public bool ClientSimulationMovingThisFrame;
        public bool OwnerAckFollowClientMovingThisFrame;
        public bool MonsterFollowTargetMovingThisFrame;
        public bool OwnerAckFollowClientTouchingMonsters;
        public uint OwnerAckFollowClientTouchingMonsterEntityId;
        public int OwnerAckFollowClientEffectiveSpeedModF32;
        public int OwnerAckFollowClientEffectiveSpeedF32;
        public int OwnerAckFollowClientSpeedPerFrameF32;
        public bool IsAlive = true;
        public bool HasActiveClientAttack;
        public uint ActiveClientAttackTargetId;
        public string InstanceKey;
    }

    public class DamageResult
    {
        public bool Success;
        public int DamageDealt;
        public bool IsCritical;
        public bool DefenderDied;
        public uint NewHPWire;
    }
}
