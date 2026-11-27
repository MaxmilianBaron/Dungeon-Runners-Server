using System;
using DungeonRunners.Networking;

namespace DungeonRunners.Combat
{
    public sealed class CombatTarget
    {
        public CombatPlayer Player { get; }
        public Monster Monster { get; }
        internal CombatTarget(CombatPlayer player) { Player = player ?? throw new ArgumentNullException(nameof(player)); }
        internal CombatTarget(Monster monster) { Monster = monster ?? throw new ArgumentNullException(nameof(monster)); }
        public static implicit operator CombatTarget(CombatPlayer player) => player?.TargetView;
        public static implicit operator CombatTarget(Monster monster) => monster?.TargetView;
        public uint EntityId => Player?.EntityId ?? Monster.EntityId;
        public string Name => Player != null ? Player.Name : Monster.Name;
        public string InstanceKey => Player != null ? Player.InstanceKey : Monster.InstanceKey;
        public PlayerState PlayerState => Player?.PlayerState;
        public bool HasUnitState => Monster != null || PlayerState != null;
        public bool IsAlive
        {
            get => Player?.IsAlive ?? Monster.IsAlive;
            set { if (Player != null) Player.IsAlive = value; else Monster.IsAlive = value; }
        }
        public uint CurrentHPWire => PlayerState?.CurrentHPWire ?? Monster?.CurrentHPWire ?? 0;
        public uint MaxHPWire => PlayerState?.MaxHPWire ?? Monster?.MaxHPWire ?? 0;
        public uint CurrentManaWire => PlayerState?.CurrentManaWire ?? Monster?.CurrentManaWire ?? 0;
        public uint MaxManaWire => PlayerState?.MaxManaWire ?? Monster?.MaxManaWire ?? 0;
        public int Level => PlayerState?.Level ?? Monster?.Level ?? 0;
        public int PosFixedX => Player?.PosFixedX ?? Monster.PosFixedX;
        public int PosFixedY => Player?.PosFixedY ?? Monster.PosFixedY;
        public int PosFixedZ => Player?.PosFixedZ ?? Monster.PosFixedZ;
        public int ClientSimulationPosFixedX => Player?.ClientSimulationPosFixedX ?? Monster.PosFixedX;
        public int ClientSimulationPosFixedY => Player?.ClientSimulationPosFixedY ?? Monster.PosFixedY;
        public int ClientSimulationPosFixedZ => Player?.ClientSimulationPosFixedZ ?? Monster.PosFixedZ;
        public int UnitFinderMembershipPosFixedX => Player?.UnitFinderMembershipPosFixedX ?? Monster.PosFixedX;
        public int UnitFinderMembershipPosFixedY => Player?.UnitFinderMembershipPosFixedY ?? Monster.PosFixedY;
        public int UnitFollowClientPosFixedX => Player?.UnitFollowClientPosFixedX ?? Monster.PosFixedX;
        public int UnitFollowClientPosFixedY => Player?.UnitFollowClientPosFixedY ?? Monster.PosFixedY;
        public int UnitFollowClientPosFixedZ => Player?.UnitFollowClientPosFixedZ ?? Monster.PosFixedZ;
        public bool UnitFollowClientMovingThisFrame => Player?.UnitFollowClientMovingThisFrame ?? Monster.UnitMoverMovingThisFrame;
        public int MonsterActionTargetPosFixedX => Player?.MonsterActionTargetPosFixedX ?? Monster.PosFixedX;
        public int MonsterActionTargetPosFixedY => Player?.MonsterActionTargetPosFixedY ?? Monster.PosFixedY;
        public int MonsterActionTargetPosFixedZ => Player?.MonsterActionTargetPosFixedZ ?? Monster.PosFixedZ;
        public int PredictedLocation2DFixedX => Player?.PredictedLocation2DFixedX ?? (Monster.UnitMoverPathIndex < Monster.UnitMoverPathFixed.Count ? Monster.UnitMoverPathFixed[Monster.UnitMoverPathIndex].FixedX : Monster.PosFixedX);
        public int PredictedLocation2DFixedY => Player?.PredictedLocation2DFixedY ?? (Monster.UnitMoverPathIndex < Monster.UnitMoverPathFixed.Count ? Monster.UnitMoverPathFixed[Monster.UnitMoverPathIndex].FixedY : Monster.PosFixedY);
    }
}
