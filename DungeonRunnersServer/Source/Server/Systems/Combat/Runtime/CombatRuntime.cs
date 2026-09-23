using System;
using System.Collections.Generic;
using DungeonRunners.Engine;
using DungeonRunners.Networking;
using DungeonRunners.Networking.EntitySynchInfo;
using DungeonRunners.Core;
using DungeonRunners.Data;
using DungeonRunners.Combat.Behavior;
using System.Linq;
using System.IO;
using System.Text;
namespace DungeonRunners.Combat
{
    public enum ClientSubEntityKind : byte
    {
        PlayerWeaponProjectile = 1,
        PlayerSpellProjectile = 2,
        MonsterProjectile = 3,
        PlayerSpellChainProjectile = 4,
        MonsterSpellChainProjectile = 5,
        PlayerPvpWeaponProjectile = 6
    }

    public readonly struct ClientSubEntityEntry
    {
        public readonly long Order;
        public readonly ClientSubEntityKind Kind;
        public readonly long LocalId;
        public readonly string InstanceKey;
        private readonly Action<long> _removeRuntime;

        public ClientSubEntityEntry(long order, ClientSubEntityKind kind, long localId, string instanceKey, Action<long> removeRuntime)
        {
            Order = order;
            Kind = kind;
            LocalId = localId;
            InstanceKey = instanceKey;
            _removeRuntime = removeRuntime;
        }

        public void RemoveRuntime()
        {
            _removeRuntime(LocalId);
        }
    }

    public enum ProjectileUnitFinderMode
    {
        RepeatEnemies,
        FirstTimeHittableUnits
    }

    public partial class CombatRuntime
    {private static CombatRuntime _instance;
        public static CombatRuntime Instance => _instance ??= new CombatRuntime();
        private static bool VerboseMonsterDiag => ServerDiagnostics.IsEnabled("verboseMonsterDiagnostics");

        private Dictionary<uint, Monster> _activeMonsters = new Dictionary<uint, Monster>();
        private Dictionary<uint, CombatPlayer> _players = new Dictionary<uint, CombatPlayer>();
        private readonly Dictionary<string, RoomRuntime> _roomRuntimes = new Dictionary<string, RoomRuntime>(StringComparer.OrdinalIgnoreCase);
        private string _currentRoomRuntimeKey = RoomRuntime.DefaultInstanceKey;
        private readonly List<uint> _entityOrder = new List<uint>();
        private readonly HashSet<uint> _entityOrderSet = new HashSet<uint>();
        private readonly Dictionary<uint, uint> _entityUpdateAdmissionTicks = new Dictionary<uint, uint>();
        private readonly Dictionary<uint, uint> _entityUpdateRemovalTicks = new Dictionary<uint, uint>();
        private readonly List<uint> _entityUpdateRemovalOrder = new List<uint>();
        private readonly HashSet<uint> _pendingMonsterFinalRemovals = new HashSet<uint>();
        private readonly Dictionary<uint, string> _itemObjectInstanceKeys = new Dictionary<uint, string>();
        private readonly Dictionary<uint, string> _encounterObjectInstanceKeys = new Dictionary<uint, string>();
        private HashSet<uint> _monsterRuntimeDamageCommitted = new HashSet<uint>();
        private Dictionary<uint, uint> _monsterHPRegenLastTick = new Dictionary<uint, uint>();
        private Dictionary<uint, ushort> _monsterHPRegenCooldownTicks = new Dictionary<uint, ushort>();
        private Dictionary<uint, uint> _monsterManaRegenLastTick = new Dictionary<uint, uint>();
        private Dictionary<uint, ushort> _monsterManaRegenCooldownTicks = new Dictionary<uint, ushort>();
        private Dictionary<uint, string> _monsterStateTraceSignatures = new Dictionary<uint, string>();
        private readonly Dictionary<string, EncounterRuntime> _encounterRuntimes = new Dictionary<string, EncounterRuntime>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _encounterRuntimeOrder = new List<string>();
        private Dictionary<string, ActiveMonsterModifier> _activeMonsterModifiers = new Dictionary<string, ActiveMonsterModifier>(StringComparer.Ordinal);
        private Dictionary<string, ActivePlayerDamageModifier> _activePlayerDamageModifiers = new Dictionary<string, ActivePlayerDamageModifier>(StringComparer.Ordinal);
        private readonly Dictionary<string, uint> _playerModifierNetworkIds = new Dictionary<string, uint>(StringComparer.Ordinal);
        private readonly Dictionary<ulong, MonsterClientVisibleState> _monsterViewerClientVisible = new Dictionary<ulong, MonsterClientVisibleState>();
        private readonly List<ulong> _monsterViewerClientVisibleOrder = new List<ulong>();
        private const uint FirstPlayerModifierNetworkId = 0x40000000u;
        private const uint LastPlayerModifierNetworkId = 0x5FFFFFFFu;
        private uint _nextPlayerModifierNetworkId = FirstPlayerModifierNetworkId;
        private uint _nextPlayerModifierStackSerial = 1u;
        private long _nextMonsterModifierOrder;
        private long _nextPlayerModifierOrder;
        private Queue<PendingModifierKill> _pendingModifierKills = new Queue<PendingModifierKill>();
        private Dictionary<uint, uint> _monsterFarTargetActionLogTick = new Dictionary<uint, uint>();
        private readonly List<PendingMonsterProjectileImpact> _pendingMonsterProjectiles = new List<PendingMonsterProjectileImpact>();
        private long _nextMonsterProjectileSequence;
        private long _nextClientSubEntityOrder;
        private readonly List<ClientSubEntityEntry> _clientSubEntityOrder = new List<ClientSubEntityEntry>();
        private readonly List<MoveToPointPathQueue> _moveToPointPathQueues = new List<MoveToPointPathQueue>();
        private bool _advancingMonsterModifiers;
        private uint _combatTick => SimulationClock.SimulationTick;
        private uint _nextClientEntityUpdateTick = 4u;
        private bool _hasCompletedEntityUpdate;
        private uint _lastCompletedEntityUpdateTick;
        private bool _hasCompletedSubEntityUpdate;
        private uint _lastCompletedSubEntityUpdateTick;
        private const int KNOCKDOWN_STATE_MESSAGE_TICKS = 11;
        private const ushort CLIENT_DAMAGE_REGEN_COOLDOWN_TICKS = 300;
        private const int CLIENT_UNIT_REGEN_DIVISOR = 3000;
        private const int CLIENT_PERCENT_SCALE = 100;
        private const ushort CLIENT_STOCKUNIT_FADE_TICKS = 35;
        private const int CLIENT_TICKS_PER_SECOND = 30;
        private const int CLIENT_CONTACT_RANGE_EPSILON_FIXED = 0x10;
        private const int CLIENT_DEFAULT_UNIT_COLLISION_BAND = 1;
        private const int CLIENT_DEFAULT_UNIT_COLLISION_PRIORITY = 0;
        private const bool CLIENT_DEFAULT_UNIT_AUTO_SCAN = false;
        private const bool CLIENT_DEFAULT_UNIT_AVOID_UNITS = true;
        private const bool CLIENT_DEFAULT_UNIT_TURN_BEFORE_MOVING = true;
        private const bool CLIENT_DEFAULT_UNIT_PLAYER_CONTROLLED = true;
        private const int CLIENT_DEFAULT_MONSTER_BASE_TIME = 30;
        private const int CLIENT_DEFAULT_MONSTER_VARIABLE_TIME = 0;
        private const bool CLIENT_DEFAULT_MONSTER_RETREATABLE = true;
        private const bool CLIENT_DEFAULT_MONSTER_LEASHED = false;
        private const bool CLIENT_DEFAULT_MONSTER_USE_IDLE_TIME = false;
        private const int PROJECTILECHECKER_FIND_ENEMIES_FLAGS = 0x837;
        private const int PROJECTILECHECKER_FIND_HITTABLE_UNITS_FLAGS = 0x60817;
        private const int UNITFINDER_FLAG_PUSH_RESULTS = 0x800;
        private const int UNITFINDER_FLAG_REQUIRE_ENEMY = 0x20;
        private const int UNITFINDER_FLAG_REQUIRE_IS_HITTABLE = 0x20000;
        private const int UNITFINDER_FLAG_IS_HITTABLE_FILTER_ENABLED = 0x40000;
        private const int AVATAR_TOUCHING_MONSTERS_SPEED_MOD_F32 = 0x3200;
        private const byte CLIENT_PLAYER_STUN_ACTION_KNOCKBACK_ID = 0x0A;
        private const byte CLIENT_PLAYER_STUN_ACTION_KNOCKDOWN_ID = 0x0B;
        private const byte CLIENT_PLAYER_STUN_ACTION_STUN_ID = 0x0C;
        private const byte CLIENT_RUNTIME_MODIFIER_SOURCE_IS_SELF = 1;
        private const byte MONSTER_MOVE_TO_POINT_SEARCH = 1;
        private const byte MONSTER_MOVE_TO_POINT_FOLLOW_REPOSITION = 2;
        private const byte MONSTER_MOVE_TO_POINT_FOLLOW_LINE_OF_SIGHT = 3;
        private const byte MONSTER_MOVE_TO_POINT_RETURN = 4;
        private const int PATH_MANAGER_TOTAL_BUDGET = 100;
        private const int PATH_MANAGER_REQUEST_QUANTUM = 20;
        public struct EntitySynchInfoVisibilityCutoff
        {
            public uint Tick;
            public bool IncludeSubEntityEffects;
            public string Reason;
            public string Phase;
            public EntitySynchInfoContext Context;
            public string SourceContext;
            public bool HasEntityCutoff;
            public uint LastEntityTick;
            public bool HasSubEntityCutoff;
            public uint LastSubEntityTick;
        }

        private class ActiveMonsterModifier
        {
            public long Order;
            public uint TargetEntityId;
            public uint SourceEntityId;
            public PlayerState SourceState;
            public SpellData Spell;
            public int SkillLevel;
            public int LastTick;
            public ushort DurationTicksInitial;
            public int DurationTicksRemaining;
            public ushort FrequencyTicks;
            public int FrequencyCountdownTicks;
            public int TicksApplied;
            public string ModifierKey;
            public uint PowerLevel;
            public string StackRule;
        }

        private class ActivePlayerDamageModifier
        {
            public long Order;
            public uint TargetEntityId;
            public uint SourceEntityId;
            public string SkillPath;
            public string EffectPath;
            public string ModifierPath;
            public string ModifierEffectPath;
            public string AttackType;
            public string DamageType;
            public int DamageTypeId;
            public byte DamageKind;
            public int DamageModF32;
            public int DamageVolatilityF32;
            public int ChanceWire;
            public int CriticalChanceF32;
            public int DamageStunMod;
            public int DamageStunModFactorF32;
            public int DamageStunModIncF32;
            public int SourceLevel;
            public int SourceIntellect;
            public int SourceAgility;
            public uint PowerLevel;
            public ushort DurationTicks;
            public ushort FrequencyTicks;
            public int ApplyTick;
            public int NextTick;
            public int ExpireTick;
            public int MaxTicks;
            public int TicksApplied;
            public bool RemoveOnDeath;
            public string StackRule;
            public string ModifierKey;
        }

        private sealed class MoveToPointPathRequest
        {
            public int RequestId;
            public string InstanceKey;
            public PathMap PathMap;
            public string OwnerKind;
            public uint OwnerEntityId;
            public int TargetFixedX;
            public int TargetFixedY;
            public Pathfinder Pathfinder;
            public Func<int, bool> IsCurrent;
            public Action<int, Pathfinder, uint> OnComplete;
        }

        private sealed class MoveToPointPathQueue
        {
            public string InstanceKey;
            public PathMap PathMap;
            public readonly List<MoveToPointPathRequest> Requests = new List<MoveToPointPathRequest>();
            public int Cursor;
            public int LastRequestId = 1;
        }

        public class MonsterModifierApplyResult
        {
            public bool AppliedModifier;
            public bool DamageApplied;
            public bool Died;
            public uint OldHPWire;
            public uint NewHPWire;
            public int TicksApplied;
            public string Reason;
        }

        private class MonsterHitPointRegenSkillEffect
        {
            public string SkillPath;
            public string EffectPath;
            public string ModifierPath;
            public int HitPointRegenBonus;
            public bool OverrideTable;
            public ushort DurationTicks;
            public int DurationF32;
            public bool RemoveOnDeath;
            public string StackRule;
        }

        private class MonsterWeaponDamageSkillEffect
        {
            public string SkillPath;
            public string EffectPath;
            public string WeaponEffectPath;
            public int WeaponEffectChanceWire;
            public int AttackRatingMod;
            public int DamageMod;
            public bool HasKnockBack;
            public string KnockBackEffectPath;
            public int KnockBackStrength;
            public int KnockBackChanceWire;
            public bool HasKnockDown;
            public string KnockDownEffectPath;
            public int KnockDownStrength;
            public int KnockDownChanceWire;
            public MonsterDamageModifierSkillEffect DamageModifier;
        }

        private class MonsterStunActionSkillEffect
        {
            public string SkillPath;
            public string EffectPath;
            public string ActionEffectPath;
            public string ActionFamily;
            public int Strength;
            public int ChanceWire;
            public bool IsKnockDown;
        }

        public class PlayerStunActionResolved
        {
            public string SkillPath;
            public string EffectPath;
            public string EffectFamily;
            public string ActionClassName;
            public byte ActionClassId;
            public ushort HeadingWire;
            public ushort StrengthWire;
            public int AuthoredStrength;
            public int ChanceWire;
            public uint ChanceRaw;
            public uint ChanceRoll;
            public int StunResistWire;
            public uint StunRaw;
            public uint StunRoll;
            public string Source;
            public bool UsesKnockDownAction;
        }

        public class PlayerModifierLifecycle
        {
            public string Visual;
            public string InitSound;
            public string InitEffect;
            public string RemoveEffect;
            public string OverlayIcon;
            public int OverlayDuration;
            public bool HasClientLocalLifecycle => !string.IsNullOrWhiteSpace(Visual)
                || !string.IsNullOrWhiteSpace(InitSound)
                || !string.IsNullOrWhiteSpace(InitEffect)
                || !string.IsNullOrWhiteSpace(RemoveEffect)
                || !string.IsNullOrWhiteSpace(OverlayIcon)
                || OverlayDuration > 0;
        }

        public class PlayerModifierNetworkEvent
        {
            public bool Add;
            public string ModifierKey;
            public string GCType;
            public uint ModifierId;
            public byte Level;
            public uint PowerLevel;
            public uint DurationTicks;
            public byte SourceIsSelf;
            public bool Replace;
            public string SkillPath;
            public string EffectPath;
            public string Source;
            public string SourceFunction;
            public PlayerModifierLifecycle Lifecycle;
        }

        private class MonsterDamageModifierSkillEffect
        {
            public string SkillPath;
            public string EffectPath;
            public string ModifierPath;
            public string ModifierEffectPath;
            public string AttackType;
            public string DamageType;
            public int DamageTypeId;
            public byte DamageKind;
            public int DamageModF32;
            public int DamageVolatilityF32;
            public int ChanceWire;
            public int CriticalChanceF32;
            public int DamageStunMod;
            public int DamageStunModFactorF32;
            public int DamageStunModIncF32;
            public int DurationF32;
            public ushort DurationTicks;
            public int FrequencyF32;
            public ushort FrequencyTicks;
            public bool RemoveOnDeath;
            public string StackRule;
        }

        private class MonsterSkillEffectSupport
        {
            public string SkillPath;
            public string EffectPath;
            public readonly List<string> Families = new List<string>();
            public readonly List<string> UnsupportedFamilies = new List<string>();
            public string Status = "UNKNOWN";
            public string ModifierPath;
            public string Attribute;
            public string Reason;
        }

        public class PendingModifierKill
        {
            public uint SourceEntityId;
            public uint TargetEntityId;
            public string Source;
            public uint DamageTick;
        }

        public bool HasPendingModifierKills => _pendingModifierKills.Count > 0;
        public uint CombatTick => _combatTick;
        public uint NextClientEntityUpdateTick => _nextClientEntityUpdateTick;
        public uint LastCompletedEntityUpdateTick => _hasCompletedEntityUpdate ? _lastCompletedEntityUpdateTick : 0u;
        public uint LastCompletedSubEntityUpdateTick => _hasCompletedSubEntityUpdate ? _lastCompletedSubEntityUpdateTick : 0u;

        public void GetMonsterRegenCooldownTicks(uint entityId, out ushort health, out ushort mana)
        {
            _monsterHPRegenCooldownTicks.TryGetValue(entityId, out health);
            _monsterManaRegenCooldownTicks.TryGetValue(entityId, out mana);
        }

        public long RegisterClientSubEntity(ClientSubEntityKind kind, long localId, string instanceKey, Action<long> removeRuntime)
        {
            if (localId <= 0) return 0;
            if (removeRuntime == null) throw new ArgumentNullException(nameof(removeRuntime));
            string normalizedInstanceKey = RoomRuntime.NormalizeInstanceKey(instanceKey);
            int existingIndex = _clientSubEntityOrder.FindIndex(entry =>
                entry.Kind == kind
                && entry.LocalId == localId
                && string.Equals(entry.InstanceKey, normalizedInstanceKey, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
                return _clientSubEntityOrder[existingIndex].Order;
            long order = ++_nextClientSubEntityOrder;
            _clientSubEntityOrder.Add(new ClientSubEntityEntry(order, kind, localId, normalizedInstanceKey, removeRuntime));
            return order;
        }

        public void RemoveClientSubEntity(ClientSubEntityKind kind, long localId, string instanceKey)
        {
            string normalizedInstanceKey = RoomRuntime.NormalizeInstanceKey(instanceKey);
            _clientSubEntityOrder.RemoveAll(entry =>
                entry.Kind == kind
                && entry.LocalId == localId
                && string.Equals(entry.InstanceKey, normalizedInstanceKey, StringComparison.OrdinalIgnoreCase));
        }

        public List<ClientSubEntityEntry> GetClientSubEntityOrderSnapshot()
        {
            return new List<ClientSubEntityEntry>(_clientSubEntityOrder);
        }

        private int ClearClientSubEntitiesForInstance(string instanceKey)
        {
            string normalizedInstanceKey = RoomRuntime.NormalizeInstanceKey(instanceKey);
            int removed = 0;
            foreach (ClientSubEntityEntry entry in GetClientSubEntityOrderSnapshot())
            {
                if (!string.Equals(entry.InstanceKey, normalizedInstanceKey, StringComparison.OrdinalIgnoreCase))
                    continue;
                entry.RemoveRuntime();
                RemoveClientSubEntity(entry.Kind, entry.LocalId, entry.InstanceKey);
                removed++;
            }
            return removed;
        }

        private int ClearAllClientSubEntities()
        {
            int removed = 0;
            foreach (ClientSubEntityEntry entry in GetClientSubEntityOrderSnapshot())
            {
                entry.RemoveRuntime();
                RemoveClientSubEntity(entry.Kind, entry.LocalId, entry.InstanceKey);
                removed++;
            }
            return removed;
        }

        private uint PreviousSimulationTick()
        {
            return _combatTick > 0 ? _combatTick - 1u : 0u;
        }

        public void MarkEntityUpdateCompleted(uint tick, string source = null)
        {
            if (_hasCompletedEntityUpdate && tick < _lastCompletedEntityUpdateTick)
                return;

            _hasCompletedEntityUpdate = true;
            _lastCompletedEntityUpdateTick = tick;
        }

        public void SetNextClientEntityUpdateTick(uint tick)
        {
            if (tick < _combatTick)
                throw new InvalidOperationException($"Client entity update tick {tick} precedes simulation tick {_combatTick}");

            _nextClientEntityUpdateTick = tick;
        }

        public bool IsMonsterEntityUpdateAdmitted(Monster monster, uint simulationTick)
        {
            return monster != null && !IsSummonInitializationPending(monster.EntityId)
                && simulationTick >= monster.EntityUpdateAdmissionTick;
        }

        public void MarkSubEntityUpdateCompleted(uint tick, string source = null)
        {
            if (_hasCompletedSubEntityUpdate && tick < _lastCompletedSubEntityUpdateTick)
                return;

            _hasCompletedSubEntityUpdate = true;
            _lastCompletedSubEntityUpdateTick = tick;
        }

        public void GetValidationCutoff(out uint tick)
        {
            if (_hasCompletedEntityUpdate)
            {
                tick = _lastCompletedEntityUpdateTick;
                return;
            }

            if (_hasCompletedSubEntityUpdate)
            {
                tick = _lastCompletedSubEntityUpdateTick;
                return;
            }

            tick = PreviousSimulationTick();
        }

        public EntitySynchInfoVisibilityCutoff GetEntitySynchInfoValidationCutoff(EntitySynchInfoContext context, string source = null)
        {
            EntitySynchInfoVisibilityCutoff cutoff = new EntitySynchInfoVisibilityCutoff
            {
                Tick = PreviousSimulationTick(),
                IncludeSubEntityEffects = false,
                Reason = "fallback-previous-entity-update",
                Phase = "fallback-previous-entity",
                Context = context,
                SourceContext = source ?? "unknown",
                HasEntityCutoff = _hasCompletedEntityUpdate,
                LastEntityTick = _hasCompletedEntityUpdate ? _lastCompletedEntityUpdateTick : 0u,
                HasSubEntityCutoff = _hasCompletedSubEntityUpdate,
                LastSubEntityTick = _hasCompletedSubEntityUpdate ? _lastCompletedSubEntityUpdateTick : 0u
            };

            if (_hasCompletedEntityUpdate)
            {
                cutoff.Tick = _lastCompletedEntityUpdateTick;
                cutoff.Reason = "completed-entity-update";
                cutoff.Phase = "entity";
            }
            else if (_hasCompletedSubEntityUpdate)
            {
                cutoff.Tick = _lastCompletedSubEntityUpdateTick;
                cutoff.Reason = "completed-subentity-update";
                cutoff.Phase = "subentity";
            }

            Debug.LogError($"[CLIENT-HP-VISIBILITY] context={context} phase={cutoff.Phase} tick={cutoff.Tick} includeSubEntity=False reason={cutoff.Reason} source={source ?? "unknown"} lastEntity={(_hasCompletedEntityUpdate ? _lastCompletedEntityUpdateTick.ToString() : "none")} lastSubEntity={(_hasCompletedSubEntityUpdate ? _lastCompletedSubEntityUpdateTick.ToString() : "none")}");
            if (cutoff.Reason.StartsWith("fallback", StringComparison.OrdinalIgnoreCase) ||
                cutoff.Phase.StartsWith("fallback", StringComparison.OrdinalIgnoreCase))
            {
                RuntimeEvidence.LogFallbackHit(
                    "hp-cutoff",
                    cutoff.Reason,
                    $"context={context} phase={cutoff.Phase} source='{source ?? "unknown"}' tick={cutoff.Tick}",
                    64);
            }
            return cutoff;
        }

        public PendingModifierKill DequeuePendingModifierKill()
        {
            return _pendingModifierKills.Count > 0 ? _pendingModifierKills.Dequeue() : null;
        }

        private Dictionary<uint, uint> _componentToEntityMap = new Dictionary<uint, uint>();


        public const ushort CombatNetworkIdMin = 0x8000;
        public const ushort CombatNetworkIdMax = 0xBFFF;
        private readonly object _combatNetworkIdGate = new object();
        private readonly HashSet<uint> _reservedCombatNetworkIds = new HashSet<uint>();
        private uint _nextMonsterId = CombatNetworkIdMin;
        private int? _avatarCombatRadiusFixed;
        private int? _avatarBoundingBoxRadiusXYF32;
        private int? _avatarBoundingBoxMinZF32;
        private int? _avatarBoundingBoxMaxZF32;
        private int? _avatarCollisionBand;
        private int? _avatarCollisionPriority;

        public event Action<Monster> OnMonsterSpawned;
        public Func<Monster, byte> ResolveMonsterDifficulty { get; set; }
        public event Action<Monster> OnMonsterDespawned;
        public event Action<Monster> OnStockUnitDead;
        public event Action<Monster> OnStockUnitRespawned;
        public event Action<Monster> OnMonsterPositionChanged;
        public event Action<Monster, CombatTarget, byte> OnMonsterAttackStarted;
        public event Action<Monster, CombatTarget> OnMonsterFollowReachedNearStop;
        public event Action<Monster, CombatTarget, bool, uint> OnMonsterAttackResolved;
        public event Action<Monster, CombatPlayer, bool, uint, string> OnPlayerDamageResolved;
        public event Action<Monster, CombatPlayer, PlayerStunActionResolved> OnPlayerStunActionResolved;
        public event Action<Monster, CombatTarget, PlayerModifierNetworkEvent> OnPlayerModifierNetworkEvent;
        public event Action<CombatPlayer, PlayerState.AttributeModifierRemoval> OnPlayerAttributeModifierRemoved;
        public event Func<CombatPlayer, string, bool> OnPlayerAttributeModifiersRemovingForDeath;


        public bool TryAllocateMonsterComponentIds(
            out uint entityId,
            out uint behaviorId,
            out uint skillsId,
            out uint manipulatorsId,
            out uint modifiersId,
            out uint unitId)
        {
            entityId = 0;
            behaviorId = 0;
            skillsId = 0;
            manipulatorsId = 0;
            modifiersId = 0;
            unitId = 0;
            if (!TryReserveCombatNetworkIds(6, true, out uint[] ids))
                return false;
            entityId = ids[0];
            behaviorId = ids[1];
            skillsId = ids[2];
            manipulatorsId = ids[3];
            modifiersId = ids[4];
            unitId = ids[5];
            return true;
        }

        public bool TryAllocateEncounterObjectIds(int count, out uint[] entityIds)
        {
            return TryReserveCombatNetworkIds(count, false, out entityIds);
        }

        private bool TryReserveCombatNetworkIds(int count, bool requireContiguous, out uint[] entityIds)
        {
            entityIds = Array.Empty<uint>();
            int capacity = CombatNetworkIdMax - CombatNetworkIdMin + 1;
            if (count <= 0 || count > capacity)
                return false;

            lock (_combatNetworkIdGate)
            {
                if (count > capacity - _reservedCombatNetworkIds.Count)
                    return false;

                uint candidate = _nextMonsterId;
                if (candidate < CombatNetworkIdMin || candidate > CombatNetworkIdMax)
                    candidate = CombatNetworkIdMin;

                if (requireContiguous)
                {
                    for (int attempt = 0; attempt < capacity; attempt++)
                    {
                        if (candidate + (uint)count - 1u <= CombatNetworkIdMax)
                        {
                            bool available = true;
                            for (int offset = 0; offset < count; offset++)
                            {
                                if (!IsCombatNetworkIdAvailableUnsafe(candidate + (uint)offset))
                                {
                                    available = false;
                                    break;
                                }
                            }
                            if (available)
                            {
                                var wireIds = new ushort[count];
                                for (int offset = 0; offset < count; offset++)
                                    wireIds[offset] = checked((ushort)(candidate + (uint)offset));
                                if (!NetworkEntityIdRegistry.TryReserveBatch(NetworkEntityIdDomain.Combat, wireIds))
                                {
                                    candidate = candidate < CombatNetworkIdMax ? candidate + 1u : CombatNetworkIdMin;
                                    continue;
                                }
                                entityIds = new uint[count];
                                for (int offset = 0; offset < count; offset++)
                                {
                                    uint reservedId = candidate + (uint)offset;
                                    _reservedCombatNetworkIds.Add(reservedId);
                                    entityIds[offset] = reservedId;
                                }
                                uint next = candidate + (uint)count;
                                _nextMonsterId = next <= CombatNetworkIdMax ? next : CombatNetworkIdMin;
                                return true;
                            }
                        }
                        candidate = candidate < CombatNetworkIdMax ? candidate + 1u : CombatNetworkIdMin;
                    }
                    return false;
                }

                var selected = new List<uint>(count);
                for (int attempt = 0; attempt < capacity && selected.Count < count; attempt++)
                {
                    if (IsCombatNetworkIdAvailableUnsafe(candidate))
                        selected.Add(candidate);
                    candidate = candidate < CombatNetworkIdMax ? candidate + 1u : CombatNetworkIdMin;
                }
                if (selected.Count != count)
                    return false;
                ushort[] selectedWireIds = selected.Select(checkedId => checked((ushort)checkedId)).ToArray();
                if (!NetworkEntityIdRegistry.TryReserveBatch(NetworkEntityIdDomain.Combat, selectedWireIds))
                    return false;
                foreach (uint reservedId in selected)
                    _reservedCombatNetworkIds.Add(reservedId);
                entityIds = selected.ToArray();
                _nextMonsterId = candidate;
                return true;
            }
        }

        private bool IsCombatNetworkIdAvailableUnsafe(uint entityId)
        {
            return !_reservedCombatNetworkIds.Contains(entityId)
                && !_componentToEntityMap.ContainsKey(entityId)
                && !_encounterObjectInstanceKeys.ContainsKey(entityId)
                && !NetworkEntityIdRegistry.IsReserved(checked((ushort)entityId));
        }

        private void ReleaseCombatNetworkId(uint entityId)
        {
            if (entityId < CombatNetworkIdMin || entityId > CombatNetworkIdMax)
                return;
            lock (_combatNetworkIdGate)
            {
                if (_reservedCombatNetworkIds.Remove(entityId))
                    NetworkEntityIdRegistry.Release(NetworkEntityIdDomain.Combat, checked((ushort)entityId));
            }
        }
        public CombatRuntime()
        {
            Debug.LogError("[COMBAT-RUNTIME] state=initialized");
            Debug.LogError("[COMBAT-RUNTIME] roomRngSeed=pending");
        }

        private void RegisterEntityOrder(uint entityId, uint admissionTick = 0)
        {
            if (entityId == 0)
                return;
            if (_entityOrderSet.Contains(entityId))
            {
                if (_entityUpdateAdmissionTicks.TryGetValue(entityId, out uint existingAdmissionTick) && admissionTick < existingAdmissionTick)
                    _entityUpdateAdmissionTicks[entityId] = admissionTick;
                return;
            }
            _entityOrderSet.Add(entityId);
            _entityOrder.Add(entityId);
            _entityUpdateAdmissionTicks[entityId] = admissionTick;
        }

        private void UnregisterEntityOrder(uint entityId)
        {
            if (!_entityOrderSet.Remove(entityId))
                return;
            _entityOrder.Remove(entityId);
            _entityUpdateAdmissionTicks.Remove(entityId);
            _entityUpdateRemovalTicks.Remove(entityId);
            _entityUpdateRemovalOrder.Remove(entityId);
        }

        public List<uint> GetEntityOrderSnapshot()
        {
            return new List<uint>(_entityOrder);
        }

        public List<uint> GetEntityUpdateOrderSnapshot(uint simulationTick)
        {
            var result = new List<uint>(_entityOrder.Count);
            var deferred = new List<uint>(_entityOrder.Count);
            foreach (uint entityId in _entityOrder)
            {
                if (_entityUpdateAdmissionTicks.TryGetValue(entityId, out uint admissionTick) && simulationTick < admissionTick)
                    continue;
                if (_entityUpdateRemovalTicks.TryGetValue(entityId, out uint removalTick) && simulationTick >= removalTick)
                    continue;
                if (_players.ContainsKey(entityId))
                    result.Add(entityId);
                else
                    deferred.Add(entityId);
            }
            result.AddRange(deferred);
            return result;
        }

        private void ScheduleEntityOrderRemoval(uint entityId, uint removalTick)
        {
            if (!_entityOrderSet.Contains(entityId))
                return;
            if (_entityUpdateRemovalTicks.TryGetValue(entityId, out uint existingRemovalTick))
            {
                if (removalTick < existingRemovalTick)
                    _entityUpdateRemovalTicks[entityId] = removalTick;
                return;
            }
            _entityUpdateRemovalTicks[entityId] = removalTick;
            _entityUpdateRemovalOrder.Add(entityId);
        }

        public void CommitEntityWriterRemovals(uint simulationTick)
        {
            if (_entityUpdateRemovalOrder.Count == 0)
                return;
            var due = new List<uint>();
            foreach (uint entityId in _entityUpdateRemovalOrder)
                if (_entityUpdateRemovalTicks.TryGetValue(entityId, out uint removalTick) && simulationTick >= removalTick)
                    due.Add(entityId);
            foreach (uint entityId in due)
            {
                if (_pendingMonsterFinalRemovals.Remove(entityId))
                    FinalizeMonsterDespawn(entityId, false, false);
                else if (_itemObjectInstanceKeys.ContainsKey(entityId))
                    UnregisterItemObjectEntity(entityId);
                else
                    UnregisterEntityOrder(entityId);
            }
        }

        public bool IsMonsterEntity(uint entityId)
        {
            return _activeMonsters.ContainsKey(entityId);
        }

        public bool IsPlayerEntity(uint entityId)
        {
            return _players.ContainsKey(entityId);
        }

        public void RegisterItemObjectEntity(uint entityId, string instanceKey)
        {
            if (entityId == 0)
                return;
            _itemObjectInstanceKeys[entityId] = RoomRuntime.NormalizeInstanceKey(instanceKey);
            RegisterEntityOrder(entityId, _nextClientEntityUpdateTick);
        }

        public void ScheduleItemObjectEntityRemoval(uint entityId)
        {
            ScheduleEntityOrderRemoval(entityId, _nextClientEntityUpdateTick);
        }

        public void UnregisterItemObjectEntity(uint entityId)
        {
            _itemObjectInstanceKeys.Remove(entityId);
            UnregisterEntityOrder(entityId);
        }

        public bool IsItemObjectEntity(uint entityId)
        {
            return _itemObjectInstanceKeys.ContainsKey(entityId);
        }

        public bool TryGetItemObjectInstanceKey(uint entityId, out string instanceKey)
        {
            return _itemObjectInstanceKeys.TryGetValue(entityId, out instanceKey);
        }

        public void RegisterEncounterObjectEntity(uint entityId, string instanceKey)
        {
            if (entityId == 0)
                return;
            _encounterObjectInstanceKeys[entityId] = RoomRuntime.NormalizeInstanceKey(instanceKey);
            RegisterEntityOrder(entityId, _nextClientEntityUpdateTick);
        }

        public void UnregisterEncounterObjectEntity(uint entityId)
        {
            bool removed = _encounterObjectInstanceKeys.Remove(entityId);
            UnregisterEntityOrder(entityId);
            if (removed)
                ReleaseCombatNetworkId(entityId);
        }

        public bool TryGetEncounterObjectInstanceKey(uint entityId, out string instanceKey)
        {
            return _encounterObjectInstanceKeys.TryGetValue(entityId, out instanceKey);
        }

        public bool TryGetCombatPlayerForController(uint entityId, out CombatPlayer player)
        {
            return _players.TryGetValue(entityId, out player);
        }

        public IEnumerable<Monster> GetMonstersInZone(string zoneName)
        {
            string instanceKey = string.IsNullOrWhiteSpace(zoneName)
                ? null
                : RoomRuntime.NormalizeInstanceKey(zoneName);
            foreach (uint entityId in _entityOrder)
            {
                if (!_activeMonsters.TryGetValue(entityId, out Monster monster))
                    continue;
                if (string.Equals(monster.ZoneName, zoneName, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrWhiteSpace(instanceKey) && MatchesInstance(monster, instanceKey)))
                    yield return monster;
            }
        }

        public int ClearZoneMobs(string zoneName)
        {
            if (string.IsNullOrWhiteSpace(zoneName))
                return 0;

            var toRemove = new List<uint>();
            var instanceKeys = new List<string>();
            foreach (uint entityId in new List<uint>(_entityOrder))
            {
                if (!_activeMonsters.TryGetValue(entityId, out Monster monster) || !MatchesZoneScope(monster.ZoneName, zoneName))
                    continue;
                toRemove.Add(entityId);
                AddNormalizedInstanceKey(instanceKeys, monster.InstanceKey);
            }
            foreach (MoveToPointPathQueue queue in _moveToPointPathQueues)
                if (queue != null && MatchesZoneRuntimeScope(queue.InstanceKey, zoneName))
                    AddNormalizedInstanceKey(instanceKeys, queue.InstanceKey);
            foreach (PendingMonsterProjectileImpact pending in _pendingMonsterProjectiles)
                if (pending != null && MatchesZoneRuntimeScope(pending.InstanceKey, zoneName))
                    AddNormalizedInstanceKey(instanceKeys, pending.InstanceKey);
            foreach (ClientSubEntityEntry subEntity in _clientSubEntityOrder)
                if (MatchesZoneRuntimeScope(subEntity.InstanceKey, zoneName))
                    AddNormalizedInstanceKey(instanceKeys, subEntity.InstanceKey);

            foreach (uint entityId in toRemove)
                DespawnMonster(entityId, true, true);
            foreach (string instanceKey in instanceKeys)
                DestroyInstanceRuntimeAdjacencies(instanceKey);

            Debug.LogError($"[BEHAVIOR] ClearZoneMobs('{zoneName}'): removed {toRemove.Count} monsters instances={instanceKeys.Count}");
            return toRemove.Count;
        }

        public int ClearInstanceMobs(string instanceKey)
        {
            string normalizedInstanceKey = RoomRuntime.NormalizeInstanceKey(instanceKey);
            if (string.IsNullOrWhiteSpace(normalizedInstanceKey))
                return 0;

            var toRemove = new List<uint>();
            foreach (uint entityId in new List<uint>(_entityOrder))
                if (_activeMonsters.TryGetValue(entityId, out Monster monster) && MatchesInstance(monster, normalizedInstanceKey))
                    toRemove.Add(entityId);

            foreach (uint entityId in toRemove)
                DespawnMonster(entityId, true, true);
            DestroyInstanceRuntimeAdjacencies(normalizedInstanceKey);

            Debug.LogError($"[ZONE-JOIN] ClearInstanceMobs('{normalizedInstanceKey}'): removed {toRemove.Count} monsters");
            return toRemove.Count;
        }

        private static bool MatchesZoneScope(string candidateZoneName, string zoneName)
        {
            return !string.IsNullOrWhiteSpace(candidateZoneName)
                && (string.Equals(candidateZoneName, zoneName, StringComparison.OrdinalIgnoreCase)
                    || candidateZoneName.StartsWith(zoneName + "_inst", StringComparison.OrdinalIgnoreCase));
        }

        private static bool MatchesZoneRuntimeScope(string instanceKey, string zoneName)
        {
            string normalized = RoomRuntime.NormalizeInstanceKey(instanceKey);
            return string.Equals(normalized, zoneName, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(zoneName + "_inst", StringComparison.OrdinalIgnoreCase);
        }

        private static void AddNormalizedInstanceKey(List<string> instanceKeys, string instanceKey)
        {
            string normalizedInstanceKey = RoomRuntime.NormalizeInstanceKey(instanceKey);
            for (int index = 0; index < instanceKeys.Count; index++)
                if (string.Equals(instanceKeys[index], normalizedInstanceKey, StringComparison.OrdinalIgnoreCase))
                    return;
            instanceKeys.Add(normalizedInstanceKey);
        }

        private void DestroyInstanceRuntimeAdjacencies(string instanceKey)
        {
            string normalizedInstanceKey = RoomRuntime.NormalizeInstanceKey(instanceKey);
            if (_roomRuntimes.TryGetValue(normalizedInstanceKey, out RoomRuntime room))
                room.MonsterDifficulty = null;
            int pathRequests = RemoveMoveToPointPathManagersForInstance(normalizedInstanceKey);
            int subEntities = ClearClientSubEntitiesForInstance(normalizedInstanceKey);
            string encounterPrefix = normalizedInstanceKey + ":";
            var encounterKeys = new List<string>();
            foreach (string encounterKey in _encounterRuntimeOrder)
                if (encounterKey.StartsWith(encounterPrefix, StringComparison.OrdinalIgnoreCase))
                    encounterKeys.Add(encounterKey);
            foreach (string encounterKey in encounterKeys)
            {
                _encounterRuntimes.Remove(encounterKey);
                _encounterRuntimeOrder.Remove(encounterKey);
            }
            Debug.LogError($"[INSTANCE-TEARDOWN] instance='{normalizedInstanceKey}' paths={pathRequests} subEntities={subEntities} encounters={encounterKeys.Count}");
        }

        public CombatPlayer RegisterPlayer(uint entityId, string name, PlayerState state, int posFixedX, int posFixedY, int posFixedZ, string instanceKey = null)
        {
            string normalizedInstanceKey = string.IsNullOrWhiteSpace(instanceKey)
                ? null
                : RoomRuntime.NormalizeInstanceKey(instanceKey);
            if (string.IsNullOrWhiteSpace(normalizedInstanceKey))
            {
                RuntimeEvidence.LogFallbackHit("rng-instance", "register-player-missing-instance", $"player={entityId} name='{name ?? ""}'", 64);
                Debug.LogError($"[RNG-INSTANCE] source=RegisterPlayer reason=missing-instance player={entityId} name='{name ?? ""}'");
                return null;
            }

            if (_players.TryGetValue(entityId, out CombatPlayer existingPlayer)
                && existingPlayer != null
                && !string.Equals(
                    RoomRuntime.NormalizeInstanceKey(existingPlayer.InstanceKey),
                    RoomRuntime.NormalizeInstanceKey(normalizedInstanceKey),
                    StringComparison.OrdinalIgnoreCase))
            {
                RemovePlayerAnchoredMonsterAurasForAnchor(entityId, "RegisterPlayer-instance-change");
                RemovePlayerDamageModifiersForTarget(entityId, "RegisterPlayer-instance-change", false);
            }

            var player = new CombatPlayer
            {
                EntityId = entityId,
                Name = name,
                PlayerState = state,
                PosFixedX = posFixedX,
                PosFixedY = posFixedY,
                PosFixedZ = posFixedZ,
                ClientSimulationPosFixedX = posFixedX,
                ClientSimulationPosFixedY = posFixedY,
                ClientSimulationPosFixedZ = posFixedZ,
                UnitFinderMembershipPosFixedX = posFixedX,
                UnitFinderMembershipPosFixedY = posFixedY,
                OwnerAckFollowClientPosFixedX = posFixedX,
                OwnerAckFollowClientPosFixedY = posFixedY,
                OwnerAckFollowClientPosFixedZ = posFixedZ,
                OwnerAckFollowClientMoverMode = UnitMover.StoppedMode,
                MonsterFollowTargetPosFixedX = posFixedX,
                MonsterFollowTargetPosFixedY = posFixedY,
                MonsterFollowTargetPosFixedZ = posFixedZ,
                MonsterActionTargetPosFixedX = posFixedX,
                MonsterActionTargetPosFixedY = posFixedY,
                MonsterActionTargetPosFixedZ = posFixedZ,
                MonsterActionTargetMoverMode = UnitMover.StoppedMode,
                PredictedLocation2DFixedX = posFixedX,
                PredictedLocation2DFixedY = posFixedY,
                OwnerAckFollowClientEffectiveSpeedModF32 = state != null ? state.SpeedMod << 8 : 0,
                InstanceKey = normalizedInstanceKey,
                IsAlive = true
            };
            if (state != null)
            {
                player.OwnerAckFollowClientSpeedPerFrameF32 = UnitMover.CacheSpeedPerFrameF32(
                    state.SpeedF32,
                    player.OwnerAckFollowClientEffectiveSpeedModF32,
                    out player.OwnerAckFollowClientEffectiveSpeedF32);
            }

            _players[entityId] = player;
            int droppedCrossInstance = 0;
            foreach (var monster in GetAllMonsters())
            {
                if (monster == null) continue;
                bool targetsPlayer = monster.TargetId == entityId
                    || ResolveMonsterFollowTargetEntityId(monster) == entityId
                    || monster.CombatContactTargetId == entityId
                    || monster.SelectedActiveSkillTargetEntityId == entityId;
                if (!targetsPlayer || MatchesInstance(monster, normalizedInstanceKey)) continue;
                if (ClearMonsterTargetStateForLostPlayer(monster, entityId, "player-changed-instance"))
                    droppedCrossInstance++;
            }
            if (droppedCrossInstance > 0)
                Debug.LogError($"[COMBAT-LIFECYCLE] player {name}#{entityId} entered instance '{normalizedInstanceKey}'; dropped {droppedCrossInstance} cross-instance monster targets sourceFunction=MonsterBehavior2::UpdateTargets@0x0051CB50 out-of-world-target-watcher");
            if (state != null)
            {
                state.AnchorEntitySynchInfoHPToTick(_combatTick);
                state.OnAttributeModifierRemoved -= HandlePlayerAttributeModifierRemoved;
                state.OnAttributeModifierRemoved += HandlePlayerAttributeModifierRemoved;
            }
            RegisterEntityOrder(entityId);
            Debug.LogError($"[COMBAT] registerPlayer name='{name}' id={entityId} pos=({FormatFixed8Diagnostic(posFixedX)},{FormatFixed8Diagnostic(posFixedY)},{FormatFixed8Diagnostic(posFixedZ)})");
            return player;
        }
        public string DumpPlayerIds()
        {
            var ids = string.Join(", ", _players.Keys);
            return $"[{ids}] (count={_players.Count})";
        }
        public void ApplyRoomEpochResetTransients(string instanceKey, Func<uint, bool> resetPlayerMover)
        {
            string normalized = RoomRuntime.NormalizeInstanceKey(instanceKey);
            int cancelledPathRequests = CancelMoveToPointPathRequestsForInstance(normalized);
            int monsterMoverReissues = 0;
            int playerMoverReissues = 0;
            foreach (uint entityId in _entityOrder)
            {
                if (_activeMonsters.TryGetValue(entityId, out Monster monster))
                {
                    if (MatchesInstance(monster, normalized) && ReissueMonsterMoveToPointPathAfterReset(monster))
                        monsterMoverReissues++;
                    continue;
                }
                if (!_players.TryGetValue(entityId, out CombatPlayer player)
                    || player == null
                    || !string.Equals(RoomRuntime.NormalizeInstanceKey(player.InstanceKey), normalized, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (resetPlayerMover?.Invoke(entityId) == true)
                    playerMoverReissues++;
            }
            Debug.LogError($"[ROOM-EPOCH] instance='{normalized}' serverSimulationPreserved=True asyncPathRequestHandles={cancelledPathRequests} monsterMoverReissues={monsterMoverReissues} playerMoverReissues={playerMoverReissues} reset=entity-insertion-order sourceFunction=ServerEntityManager::resetEntities@0x005DEDB0 UnitBehavior::processReset@0x0051FC70 UnitMover::ResetToInit@0x00535880");
        }

        public void RegisterBlingGnomeEntity(uint entityId, uint admissionTick)
        {
            RegisterEntityOrder(entityId, admissionTick);
        }

        public void UnregisterBlingGnomeEntity(uint entityId)
        {
            UnregisterEntityOrder(entityId);
        }

        public void UpdatePlayerPositionFixed(uint entityId, int posFixedX, int posFixedY, int posFixedZ)
        {
            if (!_players.TryGetValue(entityId, out var player)) return;
            player.PosFixedX = posFixedX;
            player.PosFixedY = posFixedY;
            player.PosFixedZ = posFixedZ;
        }

        public void UpdatePlayerClientSimulationPositionFixed(uint entityId, int posFixedX, int posFixedY, int posFixedZ)
        {
            if (!_players.TryGetValue(entityId, out var player)) return;
            player.ClientSimulationPosFixedX = posFixedX;
            player.ClientSimulationPosFixedY = posFixedY;
            player.ClientSimulationPosFixedZ = posFixedZ;
            player.UnitFinderMembershipPosFixedX = posFixedX;
            player.UnitFinderMembershipPosFixedY = posFixedY;
        }

        public void UpdatePlayerPredictedLocation2DFixed(uint entityId, int posFixedX, int posFixedY)
        {
            if (!_players.TryGetValue(entityId, out var player)) return;
            player.PredictedLocation2DFixedX = posFixedX;
            player.PredictedLocation2DFixedY = posFixedY;
        }

        public void UpdatePlayerOwnerAckFollowClientStateFixed(uint entityId, int posFixedX, int posFixedY, int posFixedZ, bool movingThisFrame, byte moverMode)
        {
            if (!_players.TryGetValue(entityId, out var player)) return;
            player.OwnerAckFollowClientPosFixedX = posFixedX;
            player.OwnerAckFollowClientPosFixedY = posFixedY;
            player.OwnerAckFollowClientPosFixedZ = posFixedZ;
            player.OwnerAckFollowClientMovingThisFrame = movingThisFrame;
            player.OwnerAckFollowClientMoverMode = moverMode;
        }

        public void UpdatePlayerUnitFollowClientStateFixed(uint entityId, int posFixedX, int posFixedY, int posFixedZ, bool movingThisFrame)
        {
            if (!_players.TryGetValue(entityId, out var player)) return;
            player.UnitFollowClientPosFixedX = posFixedX;
            player.UnitFollowClientPosFixedY = posFixedY;
            player.UnitFollowClientPosFixedZ = posFixedZ;
            player.UnitFollowClientMovingThisFrame = movingThisFrame;
        }

        public void UpdatePlayerUnitMoverTouchingMonsters(uint entityId, uint simulationTick)
        {
            if (!_players.TryGetValue(entityId, out CombatPlayer player)
                || player == null
                || !player.IsAlive
                || player.PlayerState == null)
                return;

            bool touchingMonsters = false;
            uint touchingMonsterEntityId = 0;
            int actorRadiusFixed = ResolveAvatarCombatRadiusFixed();
            string instanceKey = RoomRuntime.NormalizeInstanceKey(player.InstanceKey);

            foreach (uint candidateEntityId in _entityOrder)
            {
                if (!_activeMonsters.TryGetValue(candidateEntityId, out Monster monster)
                    || monster == null
                    || !monster.IsAlive
                    || !monster.UnitDescIsAlive
                    || monster.Behavior?.SpawnActive == true
                    || !IsMonsterEntityUpdateAdmitted(monster, simulationTick)
                    || !string.Equals(instanceKey, RoomRuntime.NormalizeInstanceKey(monster.InstanceKey), StringComparison.OrdinalIgnoreCase)
                    || !TryPeekMonsterClientVisiblePositionFixed(monster, entityId, out int monsterFixedX, out int monsterFixedY, out _))
                    continue;

                if (!TestUnitCollisionFixed(
                    player.PredictedLocation2DFixedX,
                    player.PredictedLocation2DFixedY,
                    actorRadiusFixed,
                    monsterFixedX,
                    monsterFixedY,
                    ResolveUnitBehaviorRadius130F32(monster)))
                    continue;

                touchingMonsters = true;
                touchingMonsterEntityId = monster.EntityId;
                break;
            }

            int speedModF32 = player.PlayerState.SpeedMod << 8;
            if (touchingMonsters)
                speedModF32 = UnitMover.ApplyPercentModifierF32(speedModF32, AVATAR_TOUCHING_MONSTERS_SPEED_MOD_F32);

            player.OwnerAckFollowClientTouchingMonsters = touchingMonsters;
            player.OwnerAckFollowClientTouchingMonsterEntityId = touchingMonsterEntityId;
            player.OwnerAckFollowClientEffectiveSpeedModF32 = speedModF32;
            player.OwnerAckFollowClientSpeedPerFrameF32 = UnitMover.CacheSpeedPerFrameF32(
                player.PlayerState.SpeedF32,
                speedModF32,
                out player.OwnerAckFollowClientEffectiveSpeedF32);
        }

        public void UpdatePlayerClientSimulationMoverMode(uint entityId, byte mode)
        {
            if (!_players.TryGetValue(entityId, out var player)) return;
            player.ClientSimulationMoverMode = mode;
        }

        public void UpdatePlayerClientSimulationMovingThisFrame(uint entityId, bool movingThisFrame)
        {
            if (!_players.TryGetValue(entityId, out var player)) return;
            player.ClientSimulationMovingThisFrame = movingThisFrame;
        }

        private static void ResolveMonsterBehaviorTargetPositionFixed(Monster monster, CombatTarget target, out int posFixedX, out int posFixedY, out int posFixedZ)
        {
            if (target == null)
            {
                posFixedX = 0;
                posFixedY = 0;
                posFixedZ = 0;
                return;
            }
            bool useClientSimulation = monster != null
                && monster.BehaviorTargetUsesClientSimulationPosition
                && monster.TargetId == target.EntityId;
            posFixedX = useClientSimulation ? target.ClientSimulationPosFixedX : target.PredictedLocation2DFixedX;
            posFixedY = useClientSimulation ? target.ClientSimulationPosFixedY : target.PredictedLocation2DFixedY;
            posFixedZ = useClientSimulation ? target.ClientSimulationPosFixedZ : target.PosFixedZ;
        }

        private static void ResolveSearchForAttackTargetPositionFixed(CombatTarget target, out int posFixedX, out int posFixedY, out int posFixedZ)
        {
            if (target == null)
            {
                posFixedX = 0;
                posFixedY = 0;
                posFixedZ = 0;
                return;
            }
            posFixedX = target.PredictedLocation2DFixedX;
            posFixedY = target.PredictedLocation2DFixedY;
            posFixedZ = target.MonsterActionTargetPosFixedZ;
        }

        private static void ResolveMonsterFollowTargetStateFixed(CombatTarget target, out int posFixedX, out int posFixedY, out int posFixedZ, out bool movingThisFrame)
        {
            if (target == null)
            {
                posFixedX = 0;
                posFixedY = 0;
                posFixedZ = 0;
                movingThisFrame = false;
                return;
            }
            posFixedX = target.UnitFollowClientPosFixedX;
            posFixedY = target.UnitFollowClientPosFixedY;
            posFixedZ = target.UnitFollowClientPosFixedZ;
            movingThisFrame = target.UnitFollowClientMovingThisFrame;
        }

        private static void ResolveMonsterActionTargetPositionFixed(CombatTarget target, out int posFixedX, out int posFixedY, out int posFixedZ)
        {
            if (target == null)
            {
                posFixedX = 0;
                posFixedY = 0;
                posFixedZ = 0;
                return;
            }
            posFixedX = target.MonsterActionTargetPosFixedX;
            posFixedY = target.MonsterActionTargetPosFixedY;
            posFixedZ = target.MonsterActionTargetPosFixedZ;
        }

        public void CaptureMonsterFollowTargetStates()
        {
            foreach (uint entityId in _entityOrder)
            {
                CaptureMonsterFollowTargetStateForEntity(entityId);
            }
        }

        public void CaptureMonsterFollowTargetStateForEntity(
            uint entityId,
            bool useUnitFollowClientActionTarget = false,
            byte? actionTargetMoverMode = null)
        {
            if (!_players.TryGetValue(entityId, out CombatPlayer player) || player == null)
                return;
            player.MonsterFollowTargetPosFixedX = player.UnitFollowClientPosFixedX;
            player.MonsterFollowTargetPosFixedY = player.UnitFollowClientPosFixedY;
            player.MonsterFollowTargetPosFixedZ = player.UnitFollowClientPosFixedZ;
            player.MonsterFollowTargetMovingThisFrame = player.UnitFollowClientMovingThisFrame;
            if (useUnitFollowClientActionTarget)
            {
                player.MonsterActionTargetPosFixedX = player.UnitFollowClientPosFixedX;
                player.MonsterActionTargetPosFixedY = player.UnitFollowClientPosFixedY;
                player.MonsterActionTargetPosFixedZ = player.UnitFollowClientPosFixedZ;
            }
            else
            {
                player.MonsterActionTargetPosFixedX = player.OwnerAckFollowClientPosFixedX;
                player.MonsterActionTargetPosFixedY = player.OwnerAckFollowClientPosFixedY;
                player.MonsterActionTargetPosFixedZ = player.OwnerAckFollowClientPosFixedZ;
            }
            if (actionTargetMoverMode.HasValue)
                player.MonsterActionTargetMoverMode = actionTargetMoverMode.Value;
        }

        public void TraverseAvatarMovementSampleFixed(uint entityId, int posFixedX, int posFixedY, int posFixedZ)
        {
            UpdatePlayerPositionFixed(entityId, posFixedX, posFixedY, posFixedZ);
        }

        private static ulong MonsterViewerClientVisibleKey(uint monsterEntityId, uint playerEntityId)
        {
            return ((ulong)monsterEntityId << 32) | playerEntityId;
        }

        private MonsterClientVisibleState GetMonsterViewerClientVisibleState(Monster monster, uint playerEntityId)
        {
            if (monster == null || playerEntityId == 0)
                return null;
            ulong key = MonsterViewerClientVisibleKey(monster.EntityId, playerEntityId);
            if (!_monsterViewerClientVisible.TryGetValue(key, out var state))
            {
                state = new MonsterClientVisibleState();
                _monsterViewerClientVisible[key] = state;
                _monsterViewerClientVisibleOrder.Add(key);
            }
            return state;
        }

        private void ClearMonsterViewerClientVisible(uint monsterEntityId)
        {
            if (monsterEntityId == 0 || _monsterViewerClientVisible.Count == 0)
                return;
            ulong prefix = (ulong)monsterEntityId << 32;
            for (int keyIndex = _monsterViewerClientVisibleOrder.Count - 1; keyIndex >= 0; keyIndex--)
            {
                ulong key = _monsterViewerClientVisibleOrder[keyIndex];
                if ((key & 0xFFFFFFFF00000000ul) == prefix)
                {
                    _monsterViewerClientVisible.Remove(key);
                    _monsterViewerClientVisibleOrder.RemoveAt(keyIndex);
                }
            }
        }

        public bool ApplyMonsterWanderClientVisiblePosition(Monster monster, string source)
        {
            if (monster == null || !monster.IsAlive || monster.AggroTriggered)
                return false;
            if (!WanderSimulator.Instance.TryGetClientVisiblePositionFixed(monster.EntityId, out int visualFixedX, out int visualFixedY))
                return false;

            long deltaX = (long)monster.PosFixedX - visualFixedX;
            long deltaY = (long)monster.PosFixedY - visualFixedY;
            long deltaSqFixed = deltaX * deltaX + deltaY * deltaY;
            if (deltaSqFixed == 0)
                return false;

            int oldFixedX = monster.PosFixedX;
            int oldFixedY = monster.PosFixedY;
            monster.PosFixedX = visualFixedX;
            monster.PosFixedY = visualFixedY;
            monster.PosFixedZ = ResolveTerrainHeightFixedValue(monster, visualFixedX, visualFixedY, monster.PosFixedZ);
            ResetMonsterClientVisiblePositionFixed(monster, visualFixedX, visualFixedY, source ?? "wander");
            if (VerboseMonsterDiag) Debug.LogError($"[MON-WANDER-POS] {monster.Name}#{monster.EntityId} source={source ?? "unknown"} authoritativeFixed8=({oldFixedX},{oldFixedY})->clientVisibleFixed8=({visualFixedX},{visualFixedY}) deltaFixedSq={deltaSqFixed}");
            return true;
        }

        private static int ResolveTerrainHeightFixedValue(Monster monster, int worldFixedX, int worldFixedY, int fallbackFixedZ)
        {
            return TryResolveTerrainHeightFixed(monster, worldFixedX, worldFixedY, fallbackFixedZ, out int groundFixedZ, out _)
                ? groundFixedZ
                : fallbackFixedZ;
        }

        private static bool TryResolveTerrainHeightFixed(Monster monster, int worldFixedX, int worldFixedY, int fallbackFixedZ, out int groundFixedZ, out string source)
        {
            groundFixedZ = fallbackFixedZ;
            source = null;
            if (monster == null) return false;
            string instanceKey = !string.IsNullOrWhiteSpace(monster.InstanceKey) ? monster.InstanceKey : monster.ZoneName;
            string zoneName = monster.ZoneName;
            var pathMap = !string.IsNullOrWhiteSpace(instanceKey) ? PathMapCatalog.Instance.GetPathMap(instanceKey) : null;
            if (pathMap == null && !string.IsNullOrWhiteSpace(zoneName))
                pathMap = PathMapCatalog.Instance.GetPathMap(zoneName);
            if (DungeonRunners.Gameplay.DungeonMazeSpawner.IsProceduralZone(zoneName) && pathMap != null)
            {
                if (pathMap.TryGetHeightAtFixed(worldFixedX, worldFixedY, out int pathGroundFixedZ))
                {
                    groundFixedZ = pathGroundFixedZ;
                    source = "PathMap";
                    return true;
                }
            }
            if (WorldCollision.Instance.TryGetTerrainHeightFixed(zoneName, instanceKey, worldFixedX, worldFixedY, fallbackFixedZ, out int worldZFixed, out string worldSource))
            {
                groundFixedZ = worldZFixed;
                source = $"WorldCollision:{worldSource}";
                return true;
            }
            if (pathMap != null)
            {
                if (pathMap.TryGetHeightAtFixed(worldFixedX, worldFixedY, out int pathGroundFixedZ))
                {
                    groundFixedZ = pathGroundFixedZ;
                    source = "PathMap";
                    return true;
                }
            }
            return false;
        }

        public bool TryGetMonsterWanderClientVisiblePositionFixed(Monster monster, out int visualFixedX, out int visualFixedY)
        {
            visualFixedX = monster?.PosFixedX ?? 0;
            visualFixedY = monster?.PosFixedY ?? 0;
            if (monster == null || !monster.IsAlive || monster.AggroTriggered)
                return false;
            return WanderSimulator.Instance.TryGetClientVisiblePositionFixed(monster.EntityId, out visualFixedX, out visualFixedY);
        }

        public void ResetMonsterClientVisiblePositionFixed(Monster monster, int posFixedX, int posFixedY, string source)
        {
            if (monster == null) return;
            monster.ClientVisibleMoveInitialized = true;
            monster.ClientVisibleMoveActive = false;
            monster.ClientVisibleMoveLastTick = _combatTick;
            monster.ClientVisibleFixedX = posFixedX;
            monster.ClientVisibleFixedY = posFixedY;
            monster.ClientVisibleFixedInit = true;
            monster.ClientVisibleMoveTargetFixedX = monster.ClientVisibleFixedX;
            monster.ClientVisibleMoveTargetFixedY = monster.ClientVisibleFixedY;
            monster.ClientVisibleMoveTargetFixedInit = true;
            monster.ClientVisibleHeadingInit = false;
            if (VerboseMonsterDiag) Debug.LogError($"[MON-CLIENT-POS] reset {monster.Name}#{monster.EntityId} source={source ?? "unknown"} visibleFixed8=({posFixedX},{posFixedY}) serverFixed8=({monster.PosFixedX},{monster.PosFixedY}) tick={_combatTick}");
        }

        public void ResetMonsterClientVisiblePositionFixed(Monster monster, uint playerEntityId, int posFixedX, int posFixedY, string source)
        {
            var state = GetMonsterViewerClientVisibleState(monster, playerEntityId);
            if (state == null) return;
            state.Initialized = true;
            state.Active = false;
            state.LastTick = _combatTick;
            state.FixedX = posFixedX;
            state.FixedY = posFixedY;
            state.FixedZ = monster.PosFixedZ;
            state.FixedInit = true;
            state.FixedZInit = true;
            state.TargetFixedX = state.FixedX;
            state.TargetFixedY = state.FixedY;
            state.TargetFixedInit = true;
            state.HeadingInit = false;
            if (VerboseMonsterDiag) Debug.LogError($"[MON-CLIENT-POS] reset-viewer {monster.Name}#{monster.EntityId} viewer={playerEntityId} source={source ?? "unknown"} visibleFixed8=({posFixedX},{posFixedY}) serverFixed8=({monster.PosFixedX},{monster.PosFixedY}) tick={_combatTick}");
        }

        public void CommitMonsterClientVisibleSnapshotFixed(
            Monster monster,
            uint playerEntityId,
            int posFixedX,
            int posFixedY,
            int posFixedZ,
            int targetFixedX,
            int targetFixedY,
            int headingFixed,
            bool active)
        {
            MonsterClientVisibleState state = GetMonsterViewerClientVisibleState(monster, playerEntityId);
            if (state == null)
                return;
            state.Initialized = true;
            state.Active = active;
            state.LastTick = _combatTick;
            state.FixedX = posFixedX;
            state.FixedY = posFixedY;
            state.FixedZ = posFixedZ;
            state.FixedInit = true;
            state.FixedZInit = true;
            state.TargetFixedX = targetFixedX;
            state.TargetFixedY = targetFixedY;
            state.TargetFixedInit = true;
            state.HeadingFixed = headingFixed;
            state.HeadingInit = true;
        }

        public void RecordMonsterMoveClientVisibleFixed(Monster monster, int targetFixedX, int targetFixedY, string source)
        {
            if (monster == null || !monster.IsAlive) return;
            if (!monster.ClientVisibleMoveInitialized)
                ResetMonsterClientVisiblePositionFixed(monster, monster.PosFixedX, monster.PosFixedY, $"{source ?? "unknown"}-lazy");

            AdvanceMonsterClientVisiblePosition(monster);
            monster.ClientVisibleMoveLastTick = _combatTick;
            monster.ClientVisibleMoveTargetFixedX = targetFixedX;
            monster.ClientVisibleMoveTargetFixedY = targetFixedY;
            monster.ClientVisibleMoveTargetFixedInit = true;
            monster.ClientVisibleMoveActive = monster.ClientVisibleFixedX != monster.ClientVisibleMoveTargetFixedX
                || monster.ClientVisibleFixedY != monster.ClientVisibleMoveTargetFixedY;
            if (VerboseMonsterDiag) Debug.LogError($"[MON-CLIENT-POS] move {monster.Name}#{monster.EntityId} source={source ?? "unknown"} visibleFixed8=({monster.ClientVisibleFixedX},{monster.ClientVisibleFixedY}) destFixed8=({targetFixedX},{targetFixedY}) serverFixed8=({monster.PosFixedX},{monster.PosFixedY}) speedF32={ResolveMonsterMovementSpeedFixed(monster)} tick={_combatTick}");
        }

        public void RecordMonsterMoveClientVisibleFixed(Monster monster, uint playerEntityId, int targetFixedX, int targetFixedY, string source)
        {
            if (monster == null || !monster.IsAlive || playerEntityId == 0) return;
            var state = GetMonsterViewerClientVisibleState(monster, playerEntityId);
            if (state == null) return;
            if (!state.Initialized)
                ResetMonsterClientVisiblePositionFixed(monster, playerEntityId, monster.PosFixedX, monster.PosFixedY, $"{source ?? "unknown"}-lazy");

            AdvanceMonsterClientVisiblePosition(monster, state, source);
            state.LastTick = _combatTick;
            state.TargetFixedX = targetFixedX;
            state.TargetFixedY = targetFixedY;
            state.TargetFixedInit = true;
            state.Active = state.FixedX != state.TargetFixedX || state.FixedY != state.TargetFixedY;
            if (VerboseMonsterDiag) Debug.LogError($"[MON-CLIENT-POS] move-viewer {monster.Name}#{monster.EntityId} viewer={playerEntityId} source={source ?? "unknown"} visibleFixed8=({state.FixedX},{state.FixedY}) destFixed8=({targetFixedX},{targetFixedY}) serverFixed8=({monster.PosFixedX},{monster.PosFixedY}) speedF32={ResolveMonsterMovementSpeedFixed(monster)} tick={_combatTick}");
        }

        public bool TryGetMonsterClientVisiblePositionFixed(Monster monster, uint playerEntityId, out int visualFixedX, out int visualFixedY)
        {
            visualFixedX = monster != null ? monster.PosFixedX : 0;
            visualFixedY = monster != null ? monster.PosFixedY : 0;
            if (monster == null || !monster.IsAlive)
                return false;

            if (!monster.AggroTriggered)
            {
                if (WanderSimulator.Instance.TryGetClientVisiblePositionFixed(monster.EntityId, out visualFixedX, out visualFixedY))
                    return true;
                visualFixedX = monster.PosFixedX;
                visualFixedY = monster.PosFixedY;
                return true;
            }
            visualFixedX = monster.PosFixedX;
            visualFixedY = monster.PosFixedY;
            return true;
        }

        public bool TryGetMonsterClientVisiblePositionFixed(Monster monster, uint playerEntityId, out int visualFixedX, out int visualFixedY, out int visualFixedZ)
        {
            visualFixedZ = monster != null ? monster.PosFixedZ : 0;
            if (!TryGetMonsterClientVisiblePositionFixed(monster, playerEntityId, out visualFixedX, out visualFixedY))
                return false;
            TryResolveTerrainHeightFixed(monster, visualFixedX, visualFixedY, visualFixedZ, out visualFixedZ, out _);
            return true;
        }

        public bool TryPeekMonsterClientVisiblePositionFixed(Monster monster, uint playerEntityId, out int visualFixedX, out int visualFixedY, out int visualFixedZ)
        {
            visualFixedX = monster != null ? monster.PosFixedX : 0;
            visualFixedY = monster != null ? monster.PosFixedY : 0;
            visualFixedZ = monster != null ? monster.PosFixedZ : 0;
            if (monster == null || !monster.IsAlive)
                return false;

            if (!monster.AggroTriggered)
            {
                if (!WanderSimulator.Instance.TryGetClientVisiblePositionFixed(monster.EntityId, out visualFixedX, out visualFixedY))
                {
                    visualFixedX = monster.PosFixedX;
                    visualFixedY = monster.PosFixedY;
                }
                TryResolveTerrainHeightFixed(monster, visualFixedX, visualFixedY, visualFixedZ, out visualFixedZ, out _);
                return true;
            }
            visualFixedX = monster.PosFixedX;
            visualFixedY = monster.PosFixedY;
            visualFixedZ = monster.PosFixedZ;
            return true;
        }

        private static uint ResolveMonsterFollowTargetEntityId(Monster monster)
        {
            if (monster?.Behavior?.FollowActive != true)
                return 0;
            return monster.Behavior.FollowTargetEntityId;
        }

        private static uint ResolveMonsterActionTargetEntityId(Monster monster)
        {
            if (monster == null)
                return 0;
            if (monster.Behavior?.UseTargetActive == true && monster.SelectedActiveSkillTargetEntityId != 0)
                return monster.SelectedActiveSkillTargetEntityId;
            uint followTargetEntityId = ResolveMonsterFollowTargetEntityId(monster);
            return followTargetEntityId != 0 ? followTargetEntityId : monster.TargetId != 0 ? monster.TargetId : monster.IdleFollowTargetEntityId;
        }

        public bool TryPeekMonsterClientVisibleMoverFixed(
            Monster monster,
            uint playerEntityId,
            out int fixedX,
            out int fixedY,
            out int fixedZ,
            out int targetFixedX,
            out int targetFixedY,
            out int headingFixed,
            out bool active)
        {
            fixedX = monster?.PosFixedX ?? 0;
            fixedY = monster?.PosFixedY ?? 0;
            fixedZ = monster?.PosFixedZ ?? 0;
            targetFixedX = fixedX;
            targetFixedY = fixedY;
            headingFixed = monster?.HeadingFixed ?? 0;
            active = false;
            if (monster == null || !monster.IsAlive)
                return false;

            if (!monster.AggroTriggered && WanderSimulator.Instance.TryGetSnapshot(monster.EntityId, out WanderStateSnapshot wander))
            {
                fixedX = wander.FixedX;
                fixedY = wander.FixedY;
                fixedZ = wander.FixedZInit ? wander.FixedZ : fixedZ;
                targetFixedX = wander.TargetFixedX;
                targetFixedY = wander.TargetFixedY;
                headingFixed = wander.HeadingInit ? wander.HeadingFixed : headingFixed;
                active = wander.State == 2 && wander.HasTarget;
                return true;
            }

            if (monster.AggroTriggered)
            {
                fixedX = monster.PosFixedX;
                fixedY = monster.PosFixedY;
                fixedZ = monster.PosFixedZ;
                headingFixed = monster.HeadingFixed;
                uint movementTargetId = ResolveMonsterActionTargetEntityId(monster);
                if (TryGetCombatTarget(movementTargetId, out CombatTarget target)
                    && target != null
                    && target.IsAlive
                    && MatchesInstance(monster, target.InstanceKey))
                {
                    targetFixedX = target.PosFixedX;
                    targetFixedY = target.PosFixedY;
                    active = monster.State == MonsterState.Chase
                        && (fixedX != targetFixedX || fixedY != targetFixedY);
                }
                return true;
            }

            if (playerEntityId != 0)
            {
                ulong key = MonsterViewerClientVisibleKey(monster.EntityId, playerEntityId);
                if (_monsterViewerClientVisible.TryGetValue(key, out MonsterClientVisibleState viewer) && viewer.Initialized)
                {
                    fixedX = viewer.FixedInit ? viewer.FixedX : fixedX;
                    fixedY = viewer.FixedInit ? viewer.FixedY : fixedY;
                    fixedZ = viewer.FixedZInit ? viewer.FixedZ : fixedZ;
                    targetFixedX = viewer.TargetFixedInit ? viewer.TargetFixedX : fixedX;
                    targetFixedY = viewer.TargetFixedInit ? viewer.TargetFixedY : fixedY;
                    headingFixed = viewer.HeadingInit ? viewer.HeadingFixed : headingFixed;
                    active = viewer.Active;
                    return true;
                }
            }

            if (monster.ClientVisibleMoveInitialized)
            {
                fixedX = monster.ClientVisibleFixedInit ? monster.ClientVisibleFixedX : fixedX;
                fixedY = monster.ClientVisibleFixedInit ? monster.ClientVisibleFixedY : fixedY;
                targetFixedX = monster.ClientVisibleMoveTargetFixedInit ? monster.ClientVisibleMoveTargetFixedX : fixedX;
                targetFixedY = monster.ClientVisibleMoveTargetFixedInit ? monster.ClientVisibleMoveTargetFixedY : fixedY;
                headingFixed = monster.ClientVisibleHeadingInit ? monster.ClientVisibleHeadingFixed : headingFixed;
                active = monster.ClientVisibleMoveActive;
            }
            return true;
        }


    }

}
