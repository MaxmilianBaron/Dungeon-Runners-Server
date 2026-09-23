using System;
using System.Collections.Generic;
using System.IO;
using DungeonRunners.Engine;
using DungeonRunners.Utilities;
using DungeonRunners.Core;
using DungeonRunners.Data;
using DungeonRunners.Gameplay;
using DungeonRunners.Combat;
using DungeonRunners.Combat.Behavior;

namespace DungeonRunners.Networking
{
    public class BlingGnomeRuntime : MonoBehaviour
    {
        private static BlingGnomeRuntime _instance;
        public static BlingGnomeRuntime Instance
        {
            get
            {
                if (_instance == null)
                {
                    GameObject go = new GameObject("BlingGnomeRuntime");
                    _instance = go.AddComponent<BlingGnomeRuntime>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        private class GnomeState
        {
            public ushort EntityId;
            public ushort BehaviorId;
            public ushort ModifiersId;
            public ushort ManipulatorsId;
            public uint OwnerEntityId;
            public string InstanceKey;

            public int PosFixedX, PosFixedY, PosFixedZ;
            public int HeadingFixed;
            public int SpawnFixedX, SpawnFixedY;

            public int ItemsConverted;
            public int BonusEligibleItemsConverted;
            public uint GoldGenerated;
            public bool BonusResolved;
            public uint HitPointsWire;
            public uint MaxHitPointsWire;
            public ushort HealthRegenBase;

            public bool BehaviorCreated;
            public uint EntityUpdateAdmissionTick;
            public bool IsActive;
            public bool BlingModifierActive;
            public uint BlingModifierId;
            public uint LocalModifierCounter;
            public bool BlingModifierIsLocal;
            public uint BlingModifierRemainingTicks;
            public string BlingModifierType;
            public string BlingModifierVisual;
            public string ItemGeneratorTable;
            public byte SnapshotLevel = 1;
            public bool ReplicatedToOwner;
            public bool UnSpawnRequested;
            public bool RespawnAfterUnSpawn;
            public SendPacketDelegate RespawnSendPacket;
            public SendMessageDelegate RespawnSendMessage;
            public readonly System.Collections.Generic.HashSet<int> ReplicatedToConnIds = new System.Collections.Generic.HashSet<int>();
            public readonly Dictionary<int, string> ReplicatedInstanceKeys = new Dictionary<int, string>();
        }

        private readonly struct BlingConversionContract
        {
            public readonly ushort SearchRadius;
            public readonly int SearchRadiusFixed;
            public readonly int ConversionRatioFixed;
            public readonly uint EffectDurationTicks;
            public readonly string ModifierType;
            public readonly string ModifierVisual;
            public readonly string ItemGeneratorTable;

            public BlingConversionContract(ushort searchRadius, int searchRadiusFixed, int conversionRatioFixed, uint effectDurationTicks, string modifierType, string modifierVisual, string itemGeneratorTable)
            {
                SearchRadius = searchRadius;
                SearchRadiusFixed = searchRadiusFixed;
                ConversionRatioFixed = conversionRatioFixed;
                EffectDurationTicks = effectDurationTicks;
                ModifierType = modifierType;
                ModifierVisual = modifierVisual;
                ItemGeneratorTable = itemGeneratorTable;
            }
        }

        private Dictionary<int, GnomeState> _gnomes = new Dictionary<int, GnomeState>();
        private readonly List<int> _gnomeOrder = new List<int>();
        private readonly Queue<(RRConnection Owner, GnomeState State)> _pendingSpawnCommits = new Queue<(RRConnection Owner, GnomeState State)>();
        private const ushort GNOME_ENTITY_ID_MIN = 0x6000;
        private const ushort GNOME_ENTITY_ID_MAX = 0x60FF;
        private const ushort GNOME_COMPONENT_ID_MIN = 0x6105;
        private const ushort GNOME_COMPONENT_ID_MAX = 0x70F7;
        private readonly object _networkEntityIdGate = new object();
        private uint _nextEntityId = GNOME_ENTITY_ID_MIN;


        private const string ENTITY_GC_TYPE = "creatures.summon.blinggnome.base.BlingGnome_Summon";
        private const string BEHAVIOR_GC_TYPE = "creatures.summon.blinggnome.base.BlingGnome_Summon.Behavior";
        private const string SKILL_TYPE = "skills.generic.SummonBlingGnome";
        private const string ITEM_GENERATOR_CHANCE_TABLE = "BlingGnomeIGTable";

        private const int Fixed8Scale = 256;
        private const byte UnitMoverApplyMovementAnimationsFlag = 0x40;
        private const byte UnitMoverMovingThisFrameFlag = 0x08;
        private const byte UnitMoverFreshFlags = 0xC5;
        private const uint BLING_MODIFIER_ID = 1;

        private const ushort ANIM_FIDGET_FIRST = 101;
        private const ushort ANIM_FIDGET_LAST = 104;
        private const ushort ANIM_ACTIVE_SKILL = 140;
        private const ushort ANIM_SPAWN = 180;
        private const ushort ANIM_SPAWN_LENGTH = 24;
        private const byte ANIM_STATE_DIRECT = 0x00;
        private const byte ANIM_STATE_ACTIVE = 0x06;

        private static ushort ComputeNPCComponentId(ushort entityId, ushort slot)
        {
            if (entityId == 0) return slot;
            uint id = ((uint)entityId & 0xFF00u) + 0x0100u + ((uint)entityId & 0x00FFu) * 0x10u + slot;
            if (id <= 0xFFFF) return (ushort)id;
            return (ushort)(entityId + 0x0100 + slot);
        }

        private const int COMPONENT_CREATE_MODE = 3;

        private GameServer _server;
        private static bool VerboseGnomeLog => ServerDiagnostics.IsEnabled("verboseGnomeLogging");

        public delegate void SendPacketDelegate(RRConnection conn, byte dest, byte type, byte[] data);
        public delegate void SendMessageDelegate(RRConnection conn, string message);

        public void SetServer(GameServer server) => _server = server;
        public void SetNextEntityId(uint id)
        {
            if (id < GNOME_ENTITY_ID_MIN || id > GNOME_ENTITY_ID_MAX)
                throw new ArgumentOutOfRangeException(nameof(id));
            lock (_networkEntityIdGate)
                _nextEntityId = id;
        }
        public uint GetNextEntityId()
        {
            if (!TryAllocateGnomeNetworkIds(out ushort entityId, out _, out _, out _))
                throw new InvalidOperationException("Bling Gnome entity id range is exhausted");
            return entityId;
        }
        public bool HasGnome(int connId) => _gnomes.ContainsKey(connId);

        private bool TryAllocateGnomeNetworkIds(out ushort entityId, out ushort modifiersId, out ushort manipulatorsId, out ushort behaviorId)
        {
            entityId = 0;
            modifiersId = 0;
            manipulatorsId = 0;
            behaviorId = 0;
            lock (_networkEntityIdGate)
            {
                int range = GNOME_ENTITY_ID_MAX - GNOME_ENTITY_ID_MIN + 1;
                uint candidate = _nextEntityId;
                if (candidate < GNOME_ENTITY_ID_MIN || candidate > GNOME_ENTITY_ID_MAX)
                    candidate = GNOME_ENTITY_ID_MIN;
                for (int attempt = 0; attempt < range; attempt++)
                {
                    ushort candidateEntityId = checked((ushort)candidate);
                    ushort candidateModifiersId = ComputeNPCComponentId(candidateEntityId, 0x05);
                    ushort candidateManipulatorsId = ComputeNPCComponentId(candidateEntityId, 0x06);
                    ushort candidateBehaviorId = ComputeNPCComponentId(candidateEntityId, 0x07);
                    if (candidateModifiersId < GNOME_COMPONENT_ID_MIN || candidateBehaviorId > GNOME_COMPONENT_ID_MAX)
                    {
                        candidate = candidate < GNOME_ENTITY_ID_MAX ? candidate + 1u : GNOME_ENTITY_ID_MIN;
                        continue;
                    }
                    ushort[] networkIds = { candidateEntityId, candidateModifiersId, candidateManipulatorsId, candidateBehaviorId };
                    if (NetworkEntityIdRegistry.TryReserveBatch(NetworkEntityIdDomain.BlingGnome, networkIds))
                    {
                        entityId = candidateEntityId;
                        modifiersId = candidateModifiersId;
                        manipulatorsId = candidateManipulatorsId;
                        behaviorId = candidateBehaviorId;
                        _nextEntityId = candidate < GNOME_ENTITY_ID_MAX ? candidate + 1u : GNOME_ENTITY_ID_MIN;
                        return true;
                    }
                    candidate = candidate < GNOME_ENTITY_ID_MAX ? candidate + 1u : GNOME_ENTITY_ID_MIN;
                }
            }
            return false;
        }

        private void ReleaseGnomeNetworkIds(GnomeState g)
        {
            if (g == null)
                return;
            ushort[] networkIds = { g.EntityId, g.ModifiersId, g.ManipulatorsId, g.BehaviorId };
            NetworkEntityIdRegistry.ReleaseBatch(NetworkEntityIdDomain.BlingGnome, networkIds);
        }

        public bool TryGetParitySnapshot(uint entityId, int viewerConnId, out int ownerConnId, out bool knownToViewer, out uint hitPointsWire, out uint maxHitPointsWire, out ushort healthRegenBase)
        {
            foreach (int candidateOwnerConnId in _gnomeOrder)
            {
                if (!_gnomes.TryGetValue(candidateOwnerConnId, out GnomeState state) || state.EntityId != entityId)
                    continue;
                ownerConnId = candidateOwnerConnId;
                knownToViewer = candidateOwnerConnId == viewerConnId
                    ? state.ReplicatedToOwner
                    : state.ReplicatedToConnIds.Contains(viewerConnId)
                        && state.ReplicatedInstanceKeys.TryGetValue(viewerConnId, out string replicatedInstanceKey)
                        && string.Equals(replicatedInstanceKey, state.InstanceKey, StringComparison.OrdinalIgnoreCase);
                hitPointsWire = state.HitPointsWire;
                maxHitPointsWire = state.MaxHitPointsWire;
                healthRegenBase = state.HealthRegenBase;
                return true;
            }
            ownerConnId = 0;
            knownToViewer = false;
            hitPointsWire = 0;
            maxHitPointsWire = 0;
            healthRegenBase = 0;
            return false;
        }

        private void BroadcastGnomePacket(RRConnection ownerConn, GnomeState g, byte[] packet)
        {
            if (g == null)
                return;
            if (g.ReplicatedToOwner)
                EnqueueGnomeBlock(ownerConn, packet);
            if (_server == null)
                return;
            var replicaConnIds = new List<int>(g.ReplicatedToConnIds);
            for (int replicaIndex = 0; replicaIndex < replicaConnIds.Count; replicaIndex++)
            {
                RRConnection peer = _server.GetConnectionByConnId(replicaConnIds[replicaIndex]);
                if (peer == null || !peer.IsConnected)
                    continue;
                EnqueueGnomeBlock(peer, packet);
            }
        }

        private static void EnqueueGnomeBlock(RRConnection conn, byte[] packet)
        {
            if (conn == null || packet == null || packet.Length < 2) return;
            if (packet[0] == 0x07 && packet[packet.Length - 1] == 0x06)
            {
                var inner = new byte[packet.Length - 2];
                Array.Copy(packet, 1, inner, 0, inner.Length);
                conn.MessageQueue.Enqueue(inner);
            }
            else
            {
                conn.MessageQueue.Enqueue(packet);
            }
        }

        public void SendGnomesToConnection(RRConnection viewer)
        {
            if (viewer == null || _server == null) return;
            foreach (int ownerConnId in _gnomeOrder)
            {
                if (_gnomes.TryGetValue(ownerConnId, out GnomeState g))
                    SendGnomeToConnection(viewer, g.EntityId);
            }
        }

        public void SendGnomeToConnection(RRConnection viewer, uint entityId, bool freshLifecycle = false)
        {
            if (viewer == null || _server == null)
                return;
            GnomeState g = null;
            RRConnection ownerConn = null;
            foreach (int ownerConnId in _gnomeOrder)
            {
                if (!_gnomes.TryGetValue(ownerConnId, out GnomeState candidate) || candidate.EntityId != entityId)
                    continue;
                g = candidate;
                ownerConn = _server.GetConnectionByConnId(ownerConnId);
                break;
            }
            if (g == null || ownerConn == null || !ownerConn.IsConnected || !ownerConn.IsSpawned || !g.BehaviorCreated)
                return;
            if (!string.Equals(RoomRuntime.NormalizeInstanceKey(ownerConn.RuntimeInstanceKey), RoomRuntime.NormalizeInstanceKey(viewer.RuntimeInstanceKey), StringComparison.OrdinalIgnoreCase))
                return;
            bool ownerViewer = ownerConn.ConnId == viewer.ConnId;
            string viewerInstanceKey = RoomRuntime.NormalizeInstanceKey(viewer.RuntimeInstanceKey);
            bool replicatedInEpoch = g.ReplicatedToConnIds.Contains(viewer.ConnId)
                && g.ReplicatedInstanceKeys.TryGetValue(viewer.ConnId, out string replicatedInstanceKey)
                && string.Equals(replicatedInstanceKey, viewerInstanceKey, StringComparison.OrdinalIgnoreCase);
            if ((ownerViewer && g.ReplicatedToOwner) || (!ownerViewer && replicatedInEpoch))
                return;
            ushort ownerPlayerEntityId = ownerViewer
                ? ownerConn.Player != null && ownerConn.Player.Id > 0 ? (ushort)ownerConn.Player.Id : (ushort)_server.GetPlayerAvatarId(ownerConn.LoginName)
                : _server.ResolveRemotePlayerEntityId(viewer, ownerConn);
            ushort followTargetEntityId = _server.ResolveRemoteAvatarEntityIdForViewer(viewer, ownerConn);
            if (ownerPlayerEntityId == 0 || followTargetEntityId == 0)
                return;
            byte[] entityCreateInitPacket = BuildEntityCreateInitPacket(g, ownerPlayerEntityId, g.SnapshotLevel, g.HitPointsWire);
            byte[] behaviorPacket = freshLifecycle
                ? BuildFreshComponentSnapshotPacket(g)
                : BuildLiveComponentSnapshotPacket(g, followTargetEntityId);
            byte[] spawnActionPacket = freshLifecycle ? BuildSpawnActionPacket(g) : null;
            if (behaviorPacket == null)
                return;
            EnqueueGnomeBlock(viewer, entityCreateInitPacket);
            EnqueueGnomeBlock(viewer, behaviorPacket);
            if (spawnActionPacket != null)
                EnqueueGnomeBlock(viewer, spawnActionPacket);
            if (ownerViewer)
                g.ReplicatedToOwner = true;
            else
            {
                g.ReplicatedToConnIds.Add(viewer.ConnId);
                g.ReplicatedInstanceKeys[viewer.ConnId] = viewerInstanceKey;
            }
            string lifecycle = freshLifecycle ? "fresh" : "live";
            Debug.LogError($"[GNOME-SPAWN] lifecycle={lifecycle} entity=0x{g.EntityId:X4} owner={ownerConn.LoginName} viewer={viewer.LoginName} ownerPlayerEntity=0x{ownerPlayerEntityId:X4} followTargetAvatar=0x{followTargetEntityId:X4}");
        }

        private static uint ResolveBlingGnomeHitPointsWire(int level)
        {
            GCDatabase database = GCDatabase.Instance ?? throw new InvalidDataException("Authored database is not loaded");
            GCNode entity = database.ResolveWithInheritance(ENTITY_GC_TYPE)
                ?? throw new InvalidDataException($"Missing authored Bling Gnome entity '{ENTITY_GC_TYPE}'");
            GCNode description = database.ResolveWithInheritance(entity.GetChild("Description"))
                ?? throw new InvalidDataException($"Missing authored Bling Gnome entity description '{ENTITY_GC_TYPE}.Description'");
            if (!description.HasProperty("MaxHealth"))
                throw new InvalidDataException($"Missing authored {ENTITY_GC_TYPE}.Description.MaxHealth");
            if (!GCNode.TryParseFixed32(description.GetString("MaxHealth", string.Empty), out int healthScaleF32))
                throw new InvalidDataException($"Invalid authored {ENTITY_GC_TYPE}.Description.MaxHealth");
            int curveValueF32 = database.RequireCurveValueFixed32("HenchmanHealth", Math.Max(1, level));
            long healthF32 = checked(((long)curveValueF32 * healthScaleF32) >> 8);
            long health = healthF32 >> 8;
            if (health < 1)
                throw new InvalidDataException($"Authored Bling Gnome health resolved to {health}");
            return (uint)health * 256u;
        }

        public void AdvanceGnomeUnit(uint entityId)
        {
            foreach (int ownerConnId in _gnomeOrder)
            {
                if (!_gnomes.TryGetValue(ownerConnId, out GnomeState g) || g.EntityId != entityId)
                    continue;
                if (g.BlingModifierActive && g.BlingModifierRemainingTicks != 0)
                {
                    g.BlingModifierRemainingTicks--;
                    if (g.BlingModifierRemainingTicks == 0)
                    {
                        RRConnection owner = _server?.GetConnectionByConnId(ownerConnId);
                        if (owner != null && owner.IsConnected && !g.BlingModifierIsLocal)
                            BroadcastGnomePacket(owner, g, BuildBlingModifierRemovePacket(g));
                        g.BlingModifierActive = false;
                        Debug.LogError($"[GNOME-MODIFIER] entity={g.EntityId} event=remove id={g.BlingModifierId} sourceFunction=Modifier::update@0x004FF1B0->Modifiers::processRemoveModifier@0x00502390");
                    }
                }
                if (g.HitPointsWire >= g.MaxHitPointsWire)
                    return;
                long regen = ((long)g.HealthRegenBase * g.MaxHitPointsWire) / 3000L + 1L;
                ulong next = (ulong)g.HitPointsWire + (ulong)Math.Max(0L, regen);
                g.HitPointsWire = next >= g.MaxHitPointsWire ? g.MaxHitPointsWire : (uint)next;
                return;
            }
        }

        public uint GetGnomeEntityId(int connId)
        {
            if (_gnomes.TryGetValue(connId, out var g)) return g.EntityId;
            return 0;
        }

        public bool TryGetGnomeFixedSnapshot(int connId, out uint entityId, out int fixedX, out int fixedY, out int fixedZ, out int headingFixed)
        {
            entityId = 0;
            fixedX = 0;
            fixedY = 0;
            fixedZ = 0;
            headingFixed = 0;
            if (!_gnomes.TryGetValue(connId, out var g) || g == null)
                return false;
            entityId = g.EntityId;
            fixedX = g.PosFixedX;
            fixedY = g.PosFixedY;
            fixedZ = g.PosFixedZ;
            headingFixed = g.HeadingFixed;
            return entityId != 0;
        }

        public void UpdateGnomeLockstepPose(int connId, uint entityId, int fixedX, int fixedY, int fixedZ, int headingFixed)
        {
            if (!_gnomes.TryGetValue(connId, out var g) || g == null || g.EntityId != entityId)
                return;
            g.PosFixedX = fixedX;
            g.PosFixedY = fixedY;
            g.PosFixedZ = fixedZ;
            g.HeadingFixed = headingFixed;
        }

        public bool TryPrepareConversionItem(RRConnection conn, GameServer.DroppedItemInfo item, int conversionRatioFixed, out uint goldAmount)
        {
            goldAmount = 0;
            if (!CanConvertItem(conn, item))
                return false;
            uint baseGold = _server.GetItemGoldValue(item);
            goldAmount = ApplyConversionRatioFixed8(baseGold, conversionRatioFixed);
            return goldAmount != 0;
        }

        public void RecordConvertedItem(RRConnection conn, uint gnomeEntityId, ushort itemEntityId, uint goldAmount, bool bonusEligible)
        {
            if (conn == null || !_gnomes.TryGetValue(conn.ConnId, out GnomeState g) || g.EntityId != gnomeEntityId)
                return;
            g.ItemsConverted++;
            if (bonusEligible)
                g.BonusEligibleItemsConverted++;
            g.GoldGenerated += goldAmount;
            Debug.LogError($"[GNOME-GOLDGENERATED] entity={gnomeEntityId} item={itemEntityId} gold={goldAmount} items={g.ItemsConverted} bonusEligibleItems={g.BonusEligibleItemsConverted} total={g.GoldGenerated}");
        }

        public void CompleteActiveConversion(RRConnection conn, uint gnomeEntityId)
        {
            if (conn == null || !_gnomes.TryGetValue(conn.ConnId, out GnomeState g) || g.EntityId != gnomeEntityId)
                return;
            if (!g.BonusResolved)
            {
                g.BonusResolved = true;
                ResolveConversionBonus(conn, g);
            }
            g.IsActive = false;
        }

        private void ResolveConversionBonus(RRConnection conn, GnomeState g)
        {
            if (_server == null || conn == null || g == null || g.ItemsConverted <= 0 || g.BonusEligibleItemsConverted <= 0)
                return;
            if (!RollConversionBonus(g.EntityId, g.ItemsConverted))
                return;
            PlayerState playerState = _server.GetPlayerState(conn.ConnId.ToString());
            int playerLevel = Math.Max(1, playerState?.Level ?? 1);
            List<LootDrop> drops = GCObjectGeneratorTable.Instance.GenerateAuthoredGeneratorLoot(
                g.ItemGeneratorTable,
                1,
                playerLevel,
                !_server.IsPlayerFreePublic(conn.LoginName),
                "bling-gnome-active-skill",
                conn.AvatarGcType);
            int emitted = 0;
            for (int index = 0; index < drops.Count; index++)
            {
                LootDrop drop = drops[index];
                if (drop == null || !drop.IsItem)
                    continue;
                if (_server.SpawnBlingGnomeGeneratedItem(conn, drop, g.PosFixedX, g.PosFixedY, g.PosFixedZ, g.HeadingFixed))
                    emitted++;
            }
            Debug.LogError($"[GNOME-BONUS] entity={g.EntityId} items={g.ItemsConverted} generated={drops.Count} emitted={emitted} generator={g.ItemGeneratorTable} source=authored-server-reconstruction");
        }

        private static bool RollConversionBonus(uint gnomeEntityId, int itemCount)
        {
            int denominatorF32 = GCDatabase.Instance.RequireCurveValueFixed32(ITEM_GENERATOR_CHANCE_TABLE, Math.Min(itemCount, 10));
            if (denominatorF32 < Fixed8Scale)
                throw new InvalidDataException("BlingGnomeIGTable reciprocal denominator must be at least one");
            int rawIndex = RandomStreams.GlobalStaticCalls;
            uint raw = RandomStreams.GenerateGlobalStatic(
                "BlingGnomeIGTable.chance",
                $"BlingGnome:{gnomeEntityId}:items={itemCount}");
            int rollF32 = unchecked((int)(raw % (uint)denominatorF32));
            bool passed = rollF32 < Fixed8Scale;
            Debug.LogError($"[GNOME-BONUS] entity={gnomeEntityId} items={itemCount} stream=globalStatic rawIndex={rawIndex} raw=0x{raw:X8} rollF32={rollF32} denominatorF32={denominatorF32} passed={passed} source=reciprocal-server-policy");
            return passed;
        }

        public bool TryResolveGnomeTarget(RRConnection conn, ushort targetEntityId,
            out uint entityId, out ushort behaviorId, out bool behaviorCreated, out string reason)
        {
            entityId = 0;
            behaviorId = 0;
            behaviorCreated = false;

            if (!TryResolveGnomeState(conn, targetEntityId, out var g, out _, out reason))
                return false;

            entityId = g.EntityId;
            behaviorId = g.BehaviorId;
            behaviorCreated = g.BehaviorCreated;
            return targetEntityId == 0 || g.EntityId == targetEntityId;
        }

        public bool TryResolveGnomeTargetPositionFixed(RRConnection viewer, ushort targetEntityId,
            out int posFixedX, out int posFixedY, out string reason)
        {
            posFixedX = 0;
            posFixedY = 0;
            reason = "none";

            if (viewer == null)
            {
                reason = "nil-viewer";
                return false;
            }

            if (targetEntityId == 0)
            {
                reason = "nil-target";
                return false;
            }

            foreach (int ownerConnId in _gnomeOrder)
            {
                if (!_gnomes.TryGetValue(ownerConnId, out GnomeState g))
                    continue;
                if (g == null || g.EntityId != targetEntityId)
                    continue;

                var ownerConn = _server?.GetConnectionByConnId(ownerConnId);
                if (ownerConn == null || !ownerConn.IsConnected || !ownerConn.IsSpawned)
                {
                    reason = "owner-unavailable";
                    return false;
                }

                if (!string.Equals(ownerConn.RuntimeInstanceKey ?? "", viewer.RuntimeInstanceKey ?? "", StringComparison.OrdinalIgnoreCase))
                {
                    reason = "instance-mismatch";
                    return false;
                }

                posFixedX = g.PosFixedX;
                posFixedY = g.PosFixedY;
                reason = ownerConnId == viewer.ConnId ? "owned-gnome" : "peer-gnome";
                return true;
            }

            reason = "no-gnome-target";
            return false;
        }

        private bool TryResolveGnomeState(RRConnection conn, ushort targetEntityId,
            out GnomeState g, out int stateConnId, out string reason)
        {
            g = null;
            stateConnId = 0;
            reason = "none";

            if (conn == null)
            {
                reason = "nil-conn";
                return false;
            }

            if (_gnomes.TryGetValue(conn.ConnId, out g))
            {
                stateConnId = conn.ConnId;
                if (targetEntityId == 0 || g.EntityId == targetEntityId)
                {
                    reason = "owned-conn";
                    return true;
                }

                reason = $"owned-target-mismatch expected={g.EntityId} got={targetEntityId}";
                return false;
            }

            if (targetEntityId != 0)
            {
                int foundConnId = 0;
                GnomeState found = null;
                foreach (int ownerConnId in _gnomeOrder)
                {
                    if (!_gnomes.TryGetValue(ownerConnId, out GnomeState candidate) || candidate.EntityId != targetEntityId)
                        continue;
                    foundConnId = ownerConnId;
                    found = candidate;
                    break;
                }

                if (found != null)
                {
                    uint avatarEntityId = conn.Avatar != null ? (uint)conn.Avatar.Id : 0;
                    if (found.OwnerEntityId != 0 && avatarEntityId != 0 && found.OwnerEntityId != avatarEntityId)
                    {
                        reason = $"target-owned-by-other owner={found.OwnerEntityId} avatar={avatarEntityId}";
                        return false;
                    }

                    if (foundConnId != conn.ConnId)
                    {
                        _gnomes.Remove(foundConnId);
                        _gnomes[conn.ConnId] = found;
                        int orderIndex = _gnomeOrder.IndexOf(foundConnId);
                        if (orderIndex >= 0)
                            _gnomeOrder[orderIndex] = conn.ConnId;
                        reason = $"target-rekey oldConn={foundConnId} newConn={conn.ConnId}";
                        Debug.LogError($"[GNOME-TARGET] Rebound Bling Gnome entity={found.EntityId} from conn={foundConnId} to conn={conn.ConnId}");
                    }
                    else
                    {
                        reason = "target-entity";
                    }

                    g = found;
                    stateConnId = conn.ConnId;
                    return true;
                }
            }

            reason = "no-gnome";
            return false;
        }

        public void CleanupForZoneTransition(int connId)
        {
            var leaverConn = _server?.GetConnectionByConnId(connId);
            if (_gnomes.TryGetValue(connId, out var g))
            {
                Debug.LogError($"[GNOME] Cleaning up gnome (ID=0x{g.EntityId:X4}) for zone transition");
                if (_server != null)
                {
                    var despawn = new LEWriter();
                    despawn.WriteByte(0x07);
                    despawn.WriteByte(0x05);
                    despawn.WriteUInt16(g.EntityId);
                    despawn.WriteByte(0x06);
                    byte[] despawnPacket = despawn.ToArray();
                    foreach (var peer in _server.GetInstancePeerConnections(leaverConn))
                    {
                        if (!g.ReplicatedToConnIds.Contains(peer.ConnId)) continue;
                        int purged = peer.MessageQueue.RemoveComponentUpdates(g.BehaviorId);
                        if (purged > 0)
                            Debug.LogError($"[GNOME] Purged {purged} queued updates for gnome behavior 0x{g.BehaviorId:X4} before zone despawn to conn={peer.ConnId}");
                        EnqueueGnomeBlock(peer, despawnPacket);
                        g.ReplicatedToConnIds.Remove(peer.ConnId);
                        g.ReplicatedInstanceKeys.Remove(peer.ConnId);
                    }
                }
                g.BehaviorCreated = false;
                EntitySynchInfo.EntitySynchInfoAuthority.Instance.UnregisterBlingGnome(g.EntityId);
                BlingGnomeLockstepRuntime.Instance.Unregister(g.EntityId);
                _gnomes.Remove(connId);
                _gnomeOrder.Remove(connId);
                ReleaseGnomeNetworkIds(g);
            }
            if (leaverConn != null)
            {
                foreach (int ownerConnId in _gnomeOrder)
                {
                    if (ownerConnId == connId || !_gnomes.TryGetValue(ownerConnId, out GnomeState peerGnome)) continue;
                    if (!peerGnome.ReplicatedToConnIds.Contains(connId)) continue;
                    var despawn = new LEWriter();
                    despawn.WriteByte(0x07);
                    despawn.WriteByte(0x05);
                    despawn.WriteUInt16(peerGnome.EntityId);
                    despawn.WriteByte(0x06);
                    int purgedPeer = leaverConn.MessageQueue.RemoveComponentUpdates(peerGnome.BehaviorId);
                    if (purgedPeer > 0)
                        Debug.LogError($"[GNOME] Purged {purgedPeer} queued updates for peer gnome behavior 0x{peerGnome.BehaviorId:X4} before zone despawn from conn={connId}");
                    EnqueueGnomeBlock(leaverConn, despawn.ToArray());
                    peerGnome.ReplicatedToConnIds.Remove(connId);
                    peerGnome.ReplicatedInstanceKeys.Remove(connId);
                    Debug.LogError($"[GNOME] Despawned peer gnome (ID=0x{peerGnome.EntityId:X4}) from leaver conn={connId} for zone transition");
                }
            }
        }


        public void TryExecute(RRConnection conn, byte[] data, SendPacketDelegate sendPacket, SendMessageDelegate sendMessage)
        {
            try
            {
                if (data == null || data.Length < 1) return;
                var reader = new LEReader(data);
                string message = reader.ReadCString();
                if (message.StartsWith("@"))
                {
                    string command = message.Substring(1).ToLower().Trim();
                    if (command == "gnome" || command == "bling" || command == "blinggnome")
                        ToggleGnomeFromChat(conn, sendPacket, sendMessage);
                    else if (command == "gnomestatus" || command == "gs")
                        ShowGnomeStatus(conn, sendMessage);
                }
            }
            catch (Exception ex) { Debug.LogError($"[GNOME-CHAT] state=failed message='{ex.Message}'"); }
        }

        private void ShowGnomeStatus(RRConnection conn, SendMessageDelegate sendMessage)
        {
            if (!_gnomes.TryGetValue(conn.ConnId, out var gnome))
            {
                sendMessage(conn, "[Bling Gnome] No gnome active. Use skill or @gnome to summon!");
                return;
            }
            uint remainingSeconds = (gnome.BlingModifierRemainingTicks + 29u) / 30u;
            string activeStatus = gnome.IsActive ? $" | ACTIVE ({remainingSeconds}s)" : "";
            sendMessage(conn, $"[Bling Gnome] Items: {gnome.ItemsConverted} | Gold: {gnome.GoldGenerated} | Persistent{activeStatus}");
        }

        private static void ResolveOwnerAnchorFixed(RRConnection conn, out int posFixedX, out int posFixedY, out int posFixedZ, out int headingFixed)
        {
            if (conn != null && conn.HasLivePlayerPosition)
            {
                posFixedX = conn.LivePlayerPosFixedX;
                posFixedY = conn.LivePlayerPosFixedY;
                posFixedZ = conn.LivePlayerPosFixedZ;
                headingFixed = conn.LivePlayerHeadingFixed;
                return;
            }
            posFixedX = conn?.PlayerPosFixedX ?? 0;
            posFixedY = conn?.PlayerPosFixedY ?? 0;
            posFixedZ = conn?.PlayerPosFixedZ ?? 0;
            headingFixed = conn?.PlayerHeadingFixed ?? 0;
        }

        private static int ResolveGroundHeightFixed(RRConnection conn, string zoneName, int fixedX, int fixedY, int fallbackFixedZ)
        {
            string instanceKey = conn?.RuntimeInstanceKey;
            PathMap instancePathMap = null;
            if (!string.IsNullOrWhiteSpace(instanceKey))
            {
                instancePathMap = PathMapCatalog.Instance.GetPathMap(instanceKey);
                if (instancePathMap != null && instancePathMap.TryGetHeightAtFixed(fixedX, fixedY, out int instanceGroundFixedZ))
                    return instanceGroundFixedZ;

                if (WorldCollision.Instance.TryGetTerrainHeightFixed(zoneName, instanceKey, fixedX, fixedY, fallbackFixedZ, out int instanceWorldFixedZ, out _))
                    return instanceWorldFixedZ;
            }

            if ((string.IsNullOrWhiteSpace(instanceKey) || instancePathMap == null) &&
                PathMapCatalog.Instance.TryGetHeightFixed(zoneName, fixedX, fixedY, out int zoneGroundFixedZ))
                return zoneGroundFixedZ;

            if (WorldCollision.Instance.TryGetTerrainHeightFixed(zoneName, null, fixedX, fixedY, fallbackFixedZ, out int zoneWorldFixedZ, out _))
                return zoneWorldFixedZ;

            return fallbackFixedZ;
        }


        public void ToggleGnome(RRConnection conn, SendPacketDelegate sendPacket, SendMessageDelegate sendMessage)
        {
            if (_gnomes.ContainsKey(conn.ConnId))
            {
                ActivateGnome(conn);
            }
            else
            {
                SpawnGnome(conn, sendPacket, sendMessage);
            }
        }

        public void ToggleGnomeFromChat(RRConnection conn, SendPacketDelegate sendPacket, SendMessageDelegate sendMessage)
        {
            if (_gnomes.ContainsKey(conn.ConnId))
            {
                var g = _gnomes[conn.ConnId];
                if (g.GoldGenerated > 0)
                    sendMessage(conn, $"[Bling Gnome] Converted {g.ItemsConverted} items into {g.GoldGenerated} gold!");
                DespawnGnome(conn, sendPacket);
            }
            else
            {
                SpawnGnome(conn, sendPacket, sendMessage);
            }
        }

        public bool ActivateGnome(RRConnection conn)
        {
            return ActivateGnome(conn, 0);
        }

        public bool ActivateGnome(RRConnection conn, ushort targetEntityId)
        {
            if (!TryResolveGnomeState(conn, targetEntityId, out var g, out _, out string reason))
            {
                Debug.LogError($"[GNOME-ACTIVATE] skipped target={targetEntityId} reason={reason}");
                return false;
            }

            if (!g.BehaviorCreated)
            {
                Debug.LogError($"[GNOME-ACTIVATE] skipped target={targetEntityId} entity={g.EntityId} reason=behavior-not-created match={reason}");
                return false;
            }

            BlingConversionContract contract = ResolveConversionContract();
            ResolveOwnerAnchorFixed(conn, out int sourceFixedX, out int sourceFixedY, out _, out _);
            if (!BlingGnomeLockstepRuntime.Instance.StartConvertItemsToGold(
                _server,
                conn,
                g.EntityId,
                sourceFixedX,
                sourceFixedY,
                contract.SearchRadiusFixed,
                contract.ConversionRatioFixed))
            {
                Debug.LogError($"[GNOME-ACTIVATE] skipped target={targetEntityId} entity={g.EntityId} reason=lockstep-not-ready match={reason}");
                return false;
            }

            g.IsActive = true;
            g.ItemsConverted = 0;
            g.BonusEligibleItemsConverted = 0;
            g.GoldGenerated = 0;
            g.BonusResolved = false;
            g.BlingModifierActive = true;
            g.BlingModifierIsLocal = false;
            g.BlingModifierId = BLING_MODIFIER_ID;
            g.BlingModifierRemainingTicks = contract.EffectDurationTicks;
            g.BlingModifierType = contract.ModifierType;
            g.BlingModifierVisual = contract.ModifierVisual;
            g.ItemGeneratorTable = contract.ItemGeneratorTable;
            BroadcastGnomePacket(conn, g, BuildBlingModifierAddPacket(g));
            SendConvertItemsToGoldAction(conn, g, sourceFixedX, sourceFixedY, contract.SearchRadius, contract.ConversionRatioFixed);

            Debug.LogError($"[GNOME-ACTIVATE] Conversion window open for {contract.EffectDurationTicks} ticks, radius={contract.SearchRadius}, ratioFixed8={contract.ConversionRatioFixed}, target={targetEntityId}, match={reason}");
            return true;
        }


        public bool ApplyConversionSkillEffect(RRConnection conn, ushort entityId, uint simulationTick)
        {
            if (_server == null
                || !TryResolveGnomeState(conn, entityId, out GnomeState g, out _, out _)
                || g.EntityId != entityId
                || !g.BehaviorCreated
                || !BlingGnomeLockstepRuntime.Instance.CanStartConvertItemsToGold(conn, entityId))
                return false;
            BlingConversionContract contract = ResolveConversionContract();
            ResolveOwnerAnchorFixed(conn, out int sourceFixedX, out int sourceFixedY, out _, out _);
            g.BlingModifierActive = true;
            g.BlingModifierIsLocal = true;
            g.LocalModifierCounter = unchecked(g.LocalModifierCounter - 1u);
            g.BlingModifierId = g.LocalModifierCounter;
            g.BlingModifierRemainingTicks = contract.EffectDurationTicks;
            g.BlingModifierType = contract.ModifierType;
            g.BlingModifierVisual = contract.ModifierVisual;
            g.IsActive = true;
            g.ItemsConverted = 0;
            g.BonusEligibleItemsConverted = 0;
            g.GoldGenerated = 0;
            g.BonusResolved = false;
            g.ItemGeneratorTable = contract.ItemGeneratorTable;
            return BlingGnomeLockstepRuntime.Instance.StartLocalConvertItemsToGold(_server, conn, entityId,
                sourceFixedX, sourceFixedY, contract.SearchRadiusFixed, contract.ConversionRatioFixed, simulationTick);
        }

        public void SpawnGnome(RRConnection conn, SendPacketDelegate sendPacket, SendMessageDelegate sendMessage)
        {
            if (conn == null)
                throw new ArgumentNullException(nameof(conn));
            Debug.LogError($"[GNOME-SPAWN] Spawning BlingGnome_Summon for {conn.LoginName}");
            if (_gnomes.ContainsKey(conn.ConnId))
            {
                Debug.LogError($"[GNOME-SPAWN] state=blocked owner={conn.ConnId} reason=already-active");
                return;
            }
            string zoneName = conn.CurrentZoneName;
            if (string.IsNullOrWhiteSpace(zoneName))
                throw new InvalidOperationException("Cannot spawn Bling Gnome without the owner's current zone");
            string instanceKey = RoomRuntime.NormalizeInstanceKey(conn.RuntimeInstanceKey);
            ResolveOwnerAnchorFixed(conn, out int ownerFixedX, out int ownerFixedY, out int ownerFixedZ, out int ownerHeadingFixed);
            int posFixedX = ownerFixedX + (5 * Fixed8Scale);
            int posFixedY = ownerFixedY + (5 * Fixed8Scale);
            int posFixedZ = ResolveGroundHeightFixed(conn, zoneName, posFixedX, posFixedY, ownerFixedZ);
            int headingFixed = ownerHeadingFixed;
            if (!TryAllocateGnomeNetworkIds(out ushort gnomeId, out ushort modifiersId, out ushort manipulatorsId, out ushort behaviorId))
            {
                Debug.LogError($"[GNOME-SPAWN] state=blocked owner={conn.ConnId} reason=entity-id-exhausted rootRange=0x{GNOME_ENTITY_ID_MIN:X4}-0x{GNOME_ENTITY_ID_MAX:X4}");
                return;
            }
            Debug.LogError($"[GNOME-SPAWN] entity=0x{gnomeId:X4} mod=0x{modifiersId:X4} manip=0x{manipulatorsId:X4} beh=0x{behaviorId:X4}");
            var g = new GnomeState
            {
                EntityId = gnomeId,
                BehaviorId = behaviorId,
                ModifiersId = modifiersId,
                ManipulatorsId = manipulatorsId,
                OwnerEntityId = conn.Avatar != null ? (uint)conn.Avatar.Id : 0,
                InstanceKey = instanceKey,
                PosFixedX = posFixedX,
                PosFixedY = posFixedY,
                PosFixedZ = posFixedZ,
                HeadingFixed = headingFixed,
                SpawnFixedX = posFixedX,
                SpawnFixedY = posFixedY,
                BehaviorCreated = false,
                IsActive = false,
            };
            try
            {
                _gnomes.Add(conn.ConnId, g);
                _gnomeOrder.Add(conn.ConnId);
                _pendingSpawnCommits.Enqueue((conn, g));
            }
            catch
            {
                if (_gnomes.TryGetValue(conn.ConnId, out GnomeState current) && object.ReferenceEquals(current, g))
                    _gnomes.Remove(conn.ConnId);
                _gnomeOrder.Remove(conn.ConnId);
                ReleaseGnomeNetworkIds(g);
                throw;
            }
        }

        public void EnsureGnomeForHotbar(RRConnection conn, SendPacketDelegate sendPacket, SendMessageDelegate sendMessage)
        {
            if (!_gnomes.TryGetValue(conn.ConnId, out GnomeState g))
            {
                SpawnGnome(conn, sendPacket, sendMessage);
                return;
            }
            if (!g.UnSpawnRequested)
                return;
            g.RespawnAfterUnSpawn = true;
            g.RespawnSendPacket = sendPacket;
            g.RespawnSendMessage = sendMessage;
            Debug.LogError($"[GNOME-UNSPAWN] entity={g.EntityId} event=respawn-queued owner={conn.ConnId}");
        }

        public IReadOnlyList<uint> CommitPendingSpawns(GameServer server, uint simulationTick, string instanceKeyFilter = null, bool replicateFreshLifecycle = true)
        {
            var committedEntityIds = new List<uint>();
            if (server != null)
                _server = server;
            bool filterByInstance = !string.IsNullOrWhiteSpace(instanceKeyFilter);
            string normalizedInstanceKeyFilter = filterByInstance
                ? RoomRuntime.NormalizeInstanceKey(instanceKeyFilter)
                : string.Empty;
            int pendingCount = _pendingSpawnCommits.Count;
            for (int pendingIndex = 0; pendingIndex < pendingCount; pendingIndex++)
            {
                var pending = _pendingSpawnCommits.Dequeue();
                RRConnection conn = pending.Owner;
                GnomeState g = pending.State;
                if (conn == null || g == null || !conn.IsConnected || !conn.IsSpawned)
                {
                    if (conn != null && g != null)
                        RemoveGnomeState(conn.ConnId, g);
                    if (!IsTrackedGnomeState(g))
                        ReleaseGnomeNetworkIds(g);
                    continue;
                }
                if (!_gnomes.TryGetValue(conn.ConnId, out GnomeState current) || !object.ReferenceEquals(current, g))
                {
                    if (!IsTrackedGnomeState(g))
                        ReleaseGnomeNetworkIds(g);
                    continue;
                }
                string instanceKey = g.InstanceKey;
                if (filterByInstance && !string.Equals(RoomRuntime.NormalizeInstanceKey(instanceKey), normalizedInstanceKeyFilter, StringComparison.OrdinalIgnoreCase))
                {
                    _pendingSpawnCommits.Enqueue(pending);
                    continue;
                }
                if (!filterByInstance && (!conn.AllowFlush || !conn.TickUpdatesActive))
                {
                    _pendingSpawnCommits.Enqueue(pending);
                    continue;
                }
                if (DungeonRunners.Combat.CombatRuntime.Instance.GetRoomRngForInstance(instanceKey) == null)
                {
                    _pendingSpawnCommits.Enqueue(pending);
                    continue;
                }
                uint admissionTick = filterByInstance
                    ? simulationTick
                    : DungeonRunners.Combat.CombatRuntime.Instance.NextClientEntityUpdateTick;
                g.EntityUpdateAdmissionTick = admissionTick;
                InitializeGnomeEntityState(conn, g);
                BlingGnomeLockstepRuntime.Instance.RegisterSpawned(conn, g.EntityId, g.PosFixedX, g.PosFixedY, g.PosFixedZ, g.HeadingFixed, admissionTick);
                CommitBehaviorCreate(conn, g, admissionTick, replicateFreshLifecycle);
                committedEntityIds.Add(g.EntityId);
            }
            return committedEntityIds;
        }

        private void InitializeGnomeEntityState(RRConnection conn, GnomeState g)
        {
            int level = Math.Max(1, Math.Min(255, conn.PlayerLevel));
            if (_server != null)
            {
                PlayerState playerState = _server.GetPlayerState(conn.ConnId.ToString());
                if (playerState != null && playerState.Level > 0)
                    level = Math.Max(1, Math.Min(255, playerState.Level));
            }
            g.SnapshotLevel = (byte)level;
            g.MaxHitPointsWire = ResolveBlingGnomeHitPointsWire(level);
            g.HitPointsWire = g.MaxHitPointsWire;
            g.HealthRegenBase = 0;
            EntitySynchInfo.EntitySynchInfoAuthority.Instance.RegisterBlingGnome(g.EntityId, g.BehaviorId, g.HitPointsWire, $"BlingGnome#{g.EntityId}");
        }

        private byte[] BuildEntityCreateInitPacket(GnomeState g, ushort ownerEntityId, byte level, uint hitPointsWire)
        {
            int posX_fx = g.PosFixedX;
            int posY_fx = g.PosFixedY;
            int posZ_fx = g.PosFixedZ;
            int heading_fx = g.HeadingFixed;
            byte unitFlags = 0x16;
            if (ownerEntityId != 0)
                unitFlags = (byte)(unitFlags | 0x01);
            const uint manaPointsWire = 0;

            var writer = new LEWriter();
            writer.WriteByte(0x07);

            writer.WriteByte(0x08);
            writer.WriteUInt16(g.EntityId);
            writer.WriteByte(0xFF);
            writer.WriteCString(ENTITY_GC_TYPE);

            writer.WriteUInt32(0x06);
            writer.WriteInt32(posX_fx);
            writer.WriteInt32(posY_fx);
            writer.WriteInt32(posZ_fx);
            writer.WriteInt32(heading_fx);
            writer.WriteByte(0x00);

            writer.WriteByte(unitFlags);
            writer.WriteByte(level);
            writer.WriteUInt16(0);
            writer.WriteUInt16(0);
            if ((unitFlags & 0x01) != 0)
                writer.WriteUInt16(ownerEntityId);
            writer.WriteUInt32(hitPointsWire);
            writer.WriteUInt32(manaPointsWire);
            writer.WriteByte(0x00);

            writer.WriteByte(0x00);
            writer.WriteUInt16(0); writer.WriteUInt16(0);
            writer.WriteByte(0x00);
            writer.WriteUInt16(0); writer.WriteUInt32(0);
            writer.WriteByte(0x00);
            writer.WriteUInt32(0); writer.WriteUInt32(0); writer.WriteUInt32(0);
            Debug.LogError($"[GNOME-SPAWN]  StockUnit init: unitFlags=0x{unitFlags:X2}, level={level}, owner=0x{ownerEntityId:X4}, hp=0x{hitPointsWire:X8}, mana=0x{manaPointsWire:X8}");

            writer.WriteByte(0x06);
            return writer.ToArray();
        }

        private static StateMachineSnapshot BuildBlingOuterStateMachine(BlingGnomeLockstepSnapshot snapshot)
        {
            var messages = new List<(StateMachineMessageSnapshot Message, ulong Order)>();
            if (snapshot.SearchDue > 0)
                messages.Add((new StateMachineMessageSnapshot(snapshot.SearchDue, 0x23, 0xFFFF, snapshot.SearchRepeat, false), snapshot.SearchMessageOrder));
            if (snapshot.FidgetDue > 0)
                messages.Add((new StateMachineMessageSnapshot(snapshot.FidgetDue, 0x0C, 0xFFFF, snapshot.FidgetRepeat, false), snapshot.FidgetMessageOrder));
            messages.Sort((left, right) =>
            {
                int dueComparison = left.Message.DueTick.CompareTo(right.Message.DueTick);
                return dueComparison != 0 ? dueComparison : left.Order.CompareTo(right.Order);
            });
            var orderedMessages = new StateMachineMessageSnapshot[messages.Count];
            for (int i = 0; i < messages.Count; i++)
                orderedMessages[i] = messages[i].Message;
            return new StateMachineSnapshot(snapshot.BlingCounter, orderedMessages);
        }

        private static StateMachineSnapshot BuildBlingFollowStateMachine(BlingGnomeLockstepSnapshot snapshot)
        {
            StateMachineMessageSnapshot[] messages = snapshot.FollowDue > 0
                ? new[] { new StateMachineMessageSnapshot(snapshot.FollowDue, 0x0F, 0xFFFF, snapshot.FollowRepeat, false) }
                : Array.Empty<StateMachineMessageSnapshot>();
            return new StateMachineSnapshot(snapshot.FollowCounter, messages);
        }

        private static void WriteBlingRetrieveAction(LEWriter writer, BlingGnomeLockstepSnapshot snapshot, bool pending)
        {
            writer.WriteByte(0xA0);
            writer.WriteByte(0);
            ushort itemId = pending ? snapshot.RetrievePendingItemEntityId : snapshot.RetrieveItemEntityId;
            if (Instance._server != null && !Instance._server.TryGetBlingDroppedItem(itemId, out _))
                itemId = 0;
            writer.WriteUInt16(itemId);
            writer.WriteByte(!pending && snapshot.RetrieveState == 4 && snapshot.RetrieveTimer == 0 ? (byte)1 : (byte)0);
            if (pending)
                return;
            int state = snapshot.RetrieveState;
            StateMachineMessageSnapshot[] messages = snapshot.RetrieveTimer > 0
                ? new[] { new StateMachineMessageSnapshot(snapshot.Actions.RetrieveCounter + snapshot.RetrieveTimer, 1, (ushort)state, 0, false) }
                : Array.Empty<StateMachineMessageSnapshot>();
            CombatPackets.WriteStateMachineSnapshot(writer,
                snapshot.Actions.RetrievePreviousState >= 0 ? (ushort)snapshot.Actions.RetrievePreviousState : (ushort)0xFFFF,
                (ushort)state, state == 0 ? (ushort)2 : (ushort)state,
                new StateMachineSnapshot(snapshot.Actions.RetrieveCounter, messages), state == 0);
        }

        private static void WriteBlingConvertAction(LEWriter writer, BlingGnomeLockstepSnapshot snapshot)
        {
            writer.WriteByte(0xA1);
            writer.WriteByte(0);
            writer.WriteUInt32(5);
            writer.WriteUInt16((ushort)(snapshot.Actions.ConvertRadiusFixed >> 8));
            writer.WriteInt32(snapshot.Actions.ConvertCenterFixedX);
            writer.WriteInt32(snapshot.Actions.ConvertCenterFixedY);
            writer.WriteInt32(snapshot.Actions.ConvertRatioFixed);
            int state = snapshot.ConvertState;
            ushort previous = state == 0 ? (ushort)0xFFFF : state == 3 ? (ushort)0 : state == 10 ? (ushort)3 : (ushort)10;
            ushort next = state == 0 ? (ushort)3 : state == 3 ? (ushort)10 : (ushort)state;
            StateMachineMessageSnapshot[] messages = state == 0
                ? Array.Empty<StateMachineMessageSnapshot>()
                : new[] { new StateMachineMessageSnapshot(state == 4 ? 152 : 136, 1, state == 4 ? (ushort)4 : (ushort)10, 0, false) };
            CombatPackets.WriteStateMachineSnapshot(writer, previous, (ushort)state, next,
                new StateMachineSnapshot(snapshot.Actions.ConvertCounter, messages), state == 0 || state == 3);
            writer.WriteInt32(snapshot.Actions.ConvertOriginalItemCount);
            writer.WriteInt32(-snapshot.Actions.ConvertOriginalItemCount);
        }

        private static void WriteLiveBehaviorReadInit(LEWriter writer, GnomeState g, ushort followTargetEntityId, BlingGnomeLockstepSnapshot snapshot)
        {
            bool hasFollow = snapshot.FollowStarted && snapshot.FollowState >= 0 && followTargetEntityId != 0;
            writer.WriteByte(snapshot.ConvertActive ? (byte)1 : (byte)0);
            if (snapshot.SpawnAnimationActionActive)
            {
                writer.WriteByte(0x0E);
                writer.WriteByte(0x00);
                writer.WriteUInt16(ANIM_SPAWN);
                writer.WriteUInt16(ANIM_SPAWN_LENGTH);
                writer.WriteByte(snapshot.SpawnAnimationPhase);
                writer.WriteByte(snapshot.SpawnAnimationCounter);
            }
            else if (snapshot.UnSpawnActionActive)
            {
                writer.WriteByte(0x0F);
                writer.WriteByte(0x00);
                writer.WriteInt32(snapshot.FixedX);
                writer.WriteInt32(snapshot.FixedY);
                writer.WriteInt32(snapshot.FixedZ);
                writer.WriteUInt16((ushort)snapshot.HeadingFixed);
                writer.WriteByte(snapshot.UnSpawnPhase);
                writer.WriteByte(snapshot.UnSpawnCounter);
            }
            else if (snapshot.ConvertActive)
            {
                WriteBlingConvertAction(writer, snapshot);
            }
            else if (snapshot.RetrieveActive)
            {
                WriteBlingRetrieveAction(writer, snapshot, false);
            }
            else if (hasFollow)
            {
                writer.WriteByte(0x16);
                writer.WriteByte(0x00);
                writer.WriteUInt16(followTargetEntityId);
                CombatPackets.WriteStateMachineSnapshot(
                    writer,
                    snapshot.FollowPreviousState >= 0 ? (ushort)snapshot.FollowPreviousState : (ushort)0xFFFF,
                    (ushort)snapshot.FollowState,
                    (ushort)snapshot.FollowState,
                    BuildBlingFollowStateMachine(snapshot),
                    snapshot.FollowState == 5 && !snapshot.FollowMoving);
                writer.WriteUInt16(BlingGnomeLockstepRuntime.FollowBaseSpeedMod);
                writer.WriteUInt16((ushort)snapshot.FollowSpeedMod);
            }
            else
            {
                writer.WriteByte(0x00);
            }
            if (snapshot.RetrievePending)
                WriteBlingRetrieveAction(writer, snapshot, true);
            else
                writer.WriteByte(0x00);
            writer.WriteByte(snapshot.Actions.SessionCounter);
            writer.WriteByte(snapshot.FollowMoverMovingThisFrame
                ? (byte)(UnitMoverApplyMovementAnimationsFlag | UnitMoverMovingThisFrameFlag)
                : UnitMoverApplyMovementAnimationsFlag);
            int moverHeadingFixed = snapshot.FollowMoverMode == 3
                ? snapshot.FollowDirectionHeadingFixed
                : snapshot.HeadingFixed;
            writer.WriteInt32(moverHeadingFixed);
            writer.WriteInt32(moverHeadingFixed);
            if (snapshot.FollowMoverMode == 3)
            {
                writer.WriteByte(0x03);
            }
            else if (snapshot.FollowMoverMode == 2)
            {
                writer.WriteByte(0x02);
                writer.WriteUInt16(1);
                writer.WriteInt32(snapshot.FollowTargetFixedX);
                writer.WriteInt32(snapshot.FollowTargetFixedY);
            }
            else
            {
                writer.WriteByte(0x01);
            }
            writer.WriteByte(0xFF);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            CombatPackets.WriteStateMachineSnapshot(
                writer,
                snapshot.BlingPreviousState >= 0 ? (ushort)snapshot.BlingPreviousState : (ushort)0xFFFF,
                snapshot.BlingState >= 0 ? (ushort)snapshot.BlingState : (ushort)0xFFFF,
                snapshot.BlingState >= 0 ? (ushort)snapshot.BlingState : (ushort)0xFFFF,
                BuildBlingOuterStateMachine(snapshot));
            writer.WriteInt32(g.SpawnFixedX);
            writer.WriteInt32(g.SpawnFixedY);
        }

        private static void WriteGnomeModifiersReadInit(LEWriter writer, GnomeState g)
        {
            writer.WriteUInt32(g.LocalModifierCounter);
            writer.WriteUInt32(0);
            writer.WriteByte(g.BlingModifierActive ? (byte)1 : (byte)0);
            if (!g.BlingModifierActive)
                return;
            writer.WriteByte(0xFF);
            writer.WriteCString(g.BlingModifierType.ToLowerInvariant());
            writer.WriteUInt32(g.BlingModifierId);
            writer.WriteByte(1);
            writer.WriteUInt32(0x100);
            writer.WriteUInt32(g.BlingModifierRemainingTicks);
            writer.WriteByte(1);
            writer.WriteByte(1);
        }

        private byte[] BuildLiveComponentSnapshotPacket(GnomeState g, ushort followTargetEntityId)
        {
            if (g == null || !BlingGnomeLockstepRuntime.Instance.TryGetSnapshot(g.EntityId, out BlingGnomeLockstepSnapshot snapshot) || !snapshot.Initialized)
                return null;

            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x32);
            writer.WriteUInt16(g.EntityId);
            writer.WriteUInt16(g.ModifiersId);
            writer.WriteByte(0xFF);
            writer.WriteCString("Modifiers");
            writer.WriteByte(0x01);
            WriteGnomeModifiersReadInit(writer, g);

            if (COMPONENT_CREATE_MODE >= 2)
            {
                writer.WriteByte(0x32);
                writer.WriteUInt16(g.EntityId);
                writer.WriteUInt16(g.ManipulatorsId);
                writer.WriteByte(0xFF);
                writer.WriteCString("Manipulators");
                writer.WriteByte(0x01);
                writer.WriteByte(0x00);
            }

            if (COMPONENT_CREATE_MODE >= 3)
            {
                writer.WriteByte(0x32);
                writer.WriteUInt16(g.EntityId);
                writer.WriteUInt16(g.BehaviorId);
                writer.WriteByte(0xFF);
                writer.WriteCString(BEHAVIOR_GC_TYPE);
                writer.WriteByte(0x01);
                WriteLiveBehaviorReadInit(writer, g, followTargetEntityId, snapshot);
            }

            writer.WriteByte(0x06);
            return writer.ToArray();
        }

        private byte[] BuildSpawnActionPacket(GnomeState g)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x35);
            writer.WriteUInt16(g.BehaviorId);
            writer.WriteByte(0x04);
            writer.WriteByte(0x0E);
            writer.WriteByte(0x00);
            writer.WriteUInt16(ANIM_SPAWN);
            writer.WriteUInt16(ANIM_SPAWN_LENGTH);
            WriteGnomeEntitySynchInfo(writer, g);
            writer.WriteByte(0x06);
            return writer.ToArray();
        }

        private byte[] BuildUnSpawnActionPacket(GnomeState g)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x35);
            writer.WriteUInt16(g.BehaviorId);
            writer.WriteByte(0x04);
            writer.WriteByte(0x0F);
            writer.WriteByte(0x00);
            WriteGnomeEntitySynchInfo(writer, g);
            writer.WriteByte(0x06);
            return writer.ToArray();
        }

        private static byte[] BuildFreshComponentSnapshotPacket(GnomeState g)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x32);
            writer.WriteUInt16(g.EntityId);
            writer.WriteUInt16(g.ModifiersId);
            writer.WriteByte(0xFF);
            writer.WriteCString("Modifiers");
            writer.WriteByte(0x01);
            WriteGnomeModifiersReadInit(writer, g);

            writer.WriteByte(0x32);
            writer.WriteUInt16(g.EntityId);
            writer.WriteUInt16(g.ManipulatorsId);
            writer.WriteByte(0xFF);
            writer.WriteCString("Manipulators");
            writer.WriteByte(0x01);
            writer.WriteByte(0x00);

            writer.WriteByte(0x32);
            writer.WriteUInt16(g.EntityId);
            writer.WriteUInt16(g.BehaviorId);
            writer.WriteByte(0xFF);
            writer.WriteCString(BEHAVIOR_GC_TYPE);
            writer.WriteByte(0x01);
            writer.WriteByte(0xFF);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            writer.WriteByte(UnitMoverFreshFlags);
            writer.WriteByte(0x00);
            writer.WriteUInt32(0x00000000);
            writer.WriteUInt32(0x00000000);
            writer.WriteUInt32(0x00000000);
            writer.WriteUInt32(0x00000000);
            writer.WriteUInt32(0x00000000);
            writer.WriteByte(0x01);
            writer.WriteByte(0xFF);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            CombatPackets.WriteStateMachineSnapshot(writer, 0xFFFF, 0xFFFF, 0xFFFF, new StateMachineSnapshot(0, Array.Empty<StateMachineMessageSnapshot>()));
            writer.WriteInt32(g.SpawnFixedX);
            writer.WriteInt32(g.SpawnFixedY);
            writer.WriteByte(0x06);
            return writer.ToArray();
        }

        private void CommitBehaviorCreate(RRConnection conn, GnomeState g, uint admissionTick, bool replicateFreshLifecycle)
        {
            if (!_gnomes.TryGetValue(conn.ConnId, out GnomeState current) || !object.ReferenceEquals(current, g) || !conn.IsConnected)
                return;
            if (COMPONENT_CREATE_MODE < 1)
                return;

            if (COMPONENT_CREATE_MODE >= 3)
            {
                g.BehaviorCreated = true;
                BlingGnomeLockstepRuntime.Instance.OnBehaviorCreated(conn, g.EntityId, g.BehaviorId, admissionTick);
            }

            if (_server == null || !replicateFreshLifecycle)
                return;
            SendGnomeToConnection(conn, g.EntityId, freshLifecycle: true);
            foreach (RRConnection peer in _server.GetInstancePeerConnections(conn))
                SendGnomeToConnection(peer, g.EntityId, freshLifecycle: true);
            Debug.LogError($"[GNOME-COMPONENT] entity=0x{g.EntityId:X4} mode=fresh components={COMPONENT_CREATE_MODE}");
        }


        public void DespawnGnome(RRConnection conn, SendPacketDelegate sendPacket, bool playDeathAnim = true)
        {
            if (!_gnomes.TryGetValue(conn.ConnId, out var g)) return;
            bool hasReplicas = g.ReplicatedToOwner || g.ReplicatedToConnIds.Count != 0;
            if (playDeathAnim && g.BehaviorCreated && hasReplicas && conn.IsConnected)
            {
                if (g.UnSpawnRequested)
                {
                    g.RespawnAfterUnSpawn = false;
                    g.RespawnSendPacket = null;
                    g.RespawnSendMessage = null;
                    Debug.LogError($"[GNOME-UNSPAWN] entity={g.EntityId} event=respawn-cancelled owner={conn.ConnId}");
                    return;
                }
                if (!BlingGnomeLockstepRuntime.Instance.RequestUnSpawn(g.EntityId))
                {
                    Debug.LogError($"[GNOME-UNSPAWN] entity={g.EntityId} event=rejected reason=lockstep-not-ready");
                    return;
                }
                g.UnSpawnRequested = true;
                BroadcastGnomePacket(conn, g, BuildUnSpawnActionPacket(g));
                Debug.LogError($"[GNOME-UNSPAWN] entity={g.EntityId} event=queued action=15 sourceFunction=Behavior::doInterruptLocal@0x00515290->UnSpawn::start@0x00530AE0");
            }
            else
            {
                RemoveGnomeState(conn.ConnId, g);
                if (conn.IsConnected)
                    SendEntityRemove(conn, g, sendPacket);
                EntitySynchInfo.EntitySynchInfoAuthority.Instance.UnregisterBlingGnome(g.EntityId);
                BlingGnomeLockstepRuntime.Instance.Unregister(g.EntityId);
                ReleaseGnomeNetworkIds(g);
            }
        }

        public void MarkUnSpawnPacketsFlushed(RRConnection conn, IReadOnlyList<byte[]> messages, uint packetWriterTick)
        {
            if (conn == null || messages == null || !_gnomes.TryGetValue(conn.ConnId, out GnomeState g) || !g.UnSpawnRequested)
                return;
            bool found = false;
            for (int messageIndex = 0; messageIndex < messages.Count && !found; messageIndex++)
            {
                byte[] message = messages[messageIndex];
                found = message != null
                    && message.Length >= 5
                    && message[0] == 0x35
                    && message[1] == (byte)g.BehaviorId
                    && message[2] == (byte)(g.BehaviorId >> 8)
                    && message[3] == 0x04
                    && message[4] == 0x0F;
            }
            if (!found)
                return;
            BlingGnomeLockstepRuntime.Instance.MarkUnSpawnPacketFlushed(g.EntityId, packetWriterTick);
            Debug.LogError($"[GNOME-UNSPAWN] entity={g.EntityId} event=writer-flushed tick={packetWriterTick} action=15 sourceFunction=ServerEntityManager::update@0x005DEB90->ClientEntityManager::processMessage@0x005DA460");
        }

        public void CompleteUnSpawn(RRConnection conn, uint entityId)
        {
            if (conn == null || !_gnomes.TryGetValue(conn.ConnId, out GnomeState g) || g.EntityId != entityId || !g.UnSpawnRequested)
                return;
            bool respawn = g.RespawnAfterUnSpawn;
            SendPacketDelegate respawnSendPacket = g.RespawnSendPacket;
            SendMessageDelegate respawnSendMessage = g.RespawnSendMessage;
            if (conn.IsConnected
                && string.Equals(RoomRuntime.NormalizeInstanceKey(conn.RuntimeInstanceKey), RoomRuntime.NormalizeInstanceKey(g.InstanceKey), StringComparison.OrdinalIgnoreCase))
                SendEntityRemove(conn, g, null);
            RemoveGnomeState(conn.ConnId, g);
            EntitySynchInfo.EntitySynchInfoAuthority.Instance.UnregisterBlingGnome(g.EntityId);
            BlingGnomeLockstepRuntime.Instance.Unregister(g.EntityId);
            ReleaseGnomeNetworkIds(g);
            Debug.LogError($"[GNOME-UNSPAWN] entity={g.EntityId} event=complete phase=100 sourceFunction=UnSpawn::update@0x00530B00->Action::terminate@0x0052C1C0");
            if (respawn && conn.IsConnected)
            {
                Debug.LogError($"[GNOME-UNSPAWN] entity={g.EntityId} event=respawn owner={conn.ConnId}");
                SpawnGnome(conn, respawnSendPacket, respawnSendMessage);
            }
        }

        private void RemoveGnomeState(int connId, GnomeState g)
        {
            if (_gnomes.TryGetValue(connId, out GnomeState current) && object.ReferenceEquals(current, g))
                _gnomes.Remove(connId);
            _gnomeOrder.Remove(connId);
        }

        private bool IsTrackedGnomeState(GnomeState g)
        {
            if (g == null)
                return false;
            for (int index = 0; index < _gnomeOrder.Count; index++)
            {
                if (_gnomes.TryGetValue(_gnomeOrder[index], out GnomeState current) && object.ReferenceEquals(current, g))
                    return true;
            }
            return false;
        }

        private void SendEntityRemove(RRConnection conn, GnomeState g, SendPacketDelegate sendPacket)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x05);
            writer.WriteUInt16(g.EntityId);
            writer.WriteByte(0x06);
            byte[] packet = writer.ToArray();
            if (g.ReplicatedToOwner && conn != null && conn.IsConnected)
            {
                conn.MessageQueue.RemoveComponentUpdates(g.BehaviorId);
                EnqueueGnomeBlock(conn, packet);
                g.ReplicatedToOwner = false;
            }
            if (_server != null)
            {
                var replicaConnIds = new List<int>(g.ReplicatedToConnIds);
                for (int replicaIndex = 0; replicaIndex < replicaConnIds.Count; replicaIndex++)
                {
                    int replicaConnId = replicaConnIds[replicaIndex];
                    RRConnection peer = _server.GetConnectionByConnId(replicaConnId);
                    if (peer == null || !peer.IsConnected)
                    {
                        g.ReplicatedToConnIds.Remove(replicaConnId);
                        g.ReplicatedInstanceKeys.Remove(replicaConnId);
                        continue;
                    }
                    peer.MessageQueue.RemoveComponentUpdates(g.BehaviorId);
                    EnqueueGnomeBlock(peer, packet);
                    g.ReplicatedToConnIds.Remove(peer.ConnId);
                    g.ReplicatedInstanceKeys.Remove(peer.ConnId);
                }
            }
            Debug.LogError($"[GNOME] Despawned 0x{g.EntityId:X4} - {g.ItemsConverted} items -> {g.GoldGenerated}g");
        }

        private bool IsAllowedForOwner(RRConnection conn, GameServer.DroppedItemInfo item)
        {
            if (conn == null || item == null || item.OwnerCharacterId == 0)
                return false;
            uint ownerCharacterId = _server != null ? _server.GetCharSqlIdPublic(conn) : conn.CharSqlId;
            return ownerCharacterId != 0 && item.OwnerCharacterId == ownerCharacterId;
        }

        private bool CanConvertItem(RRConnection conn, GameServer.DroppedItemInfo item)
        {
            if (item == null || item.IsGoldDrop || !IsAllowedForOwner(conn, item))
                return false;
            if (item.Item == null)
            {
                Debug.LogError($"[GNOME-CONVERT-FILTER] item=Currency owner={item.OwnerCharacterId} requester={(_server != null ? _server.GetCharSqlIdPublic(conn) : conn?.CharSqlId ?? 0)} accepted=false sourceFunction=ItemFinder::findItems@0x00586F20 flags=0x0E->ConvertItemsToGold::States@0x00524530");
                return false;
            }
            GCNode description = GCObject.ResolveItemDescription(item.Item.GCClass);
            int descriptionQuality = RPGSettings.ResolveNativeItemQuality(description);
            int bestQuality = RPGSettings.ResolveBestNativeItemQuality(item.Item, description);
            bool accepted = descriptionQuality != 0 && descriptionQuality != 6 && bestQuality < 5;
            Debug.LogError($"[GNOME-CONVERT-FILTER] item={item.Item.GCClass} owner={item.OwnerCharacterId} requester={(_server != null ? _server.GetCharSqlIdPublic(conn) : conn?.CharSqlId ?? 0)} descriptionQuality={descriptionQuality} bestQuality={bestQuality} accepted={accepted} sourceFunction=ItemFinder::findItems@0x00586F20->ConvertItemsToGold::States@0x00524530");
            return accepted;
        }

        private static BlingConversionContract ResolveConversionContract()
        {
            GCDatabase database = GCDatabase.Instance;
            if (database == null || !database.IsLoaded)
                throw new InvalidDataException("Authored database is not loaded for Bling Gnome conversion");
            GCNode behavior = database.ResolveWithInheritance(BEHAVIOR_GC_TYPE)
                ?? throw new InvalidDataException($"Missing authored Bling Gnome behavior '{BEHAVIOR_GC_TYPE}'");
            GCNode behaviorDescription = database.ResolveWithInheritance(behavior.GetChild("Description"))
                ?? throw new InvalidDataException($"Missing authored Bling Gnome behavior description '{BEHAVIOR_GC_TYPE}.Description'");
            int conversionRatioFixed = RequireAuthoredFixed32(behaviorDescription, "ConversionRatio");
            if (conversionRatioFixed <= 0)
                throw new InvalidDataException($"Bling Gnome ConversionRatio must be positive, got {conversionRatioFixed}");
            string itemGeneratorTable = RequireAuthoredString(behaviorDescription, "ItemGeneratorTable");

            GCNode skill = database.ResolveWithInheritance(SKILL_TYPE)
                ?? throw new InvalidDataException($"Missing authored Bling Gnome skill '{SKILL_TYPE}'");
            GCNode skillDescription = database.ResolveWithInheritance(skill.GetChild("Description"))
                ?? throw new InvalidDataException($"Missing authored Bling Gnome skill description '{SKILL_TYPE}.Description'");
            string effectPath = RequireAuthoredString(skillDescription, "Effect");
            GCNode effectRoot = database.ResolveWithInheritance(effectPath)
                ?? throw new InvalidDataException($"Missing authored Bling Gnome effect '{effectPath}'");
            GCNode modifierEffect = RequireSingleAuthoredEffect(effectRoot, "SpellModEffect");
            GCNode conversionEffect = RequireSingleAuthoredEffect(effectRoot, "SpellConvertItemsToGoldEffect");

            int durationFixed = RequireAuthoredFixed32(modifierEffect, "Duration");
            RequireAuthoredFixed32(modifierEffect, "DurationInc");
            long durationTicks = (((long)durationFixed * 30) + 0x100) >> 8;
            if (durationTicks <= 0 || durationTicks > uint.MaxValue)
                throw new InvalidDataException($"Bling Gnome modifier duration is outside the logical tick range: {durationTicks}");
            string modifierType = RequireAuthoredString(modifierEffect, "Modifier");
            GCNode modifier = database.ResolveWithInheritance(modifierType)
                ?? throw new InvalidDataException($"Missing authored Bling Gnome modifier '{modifierType}'");
            GCNode modifierDescription = database.ResolveWithInheritance(modifier.GetChild("Description"))
                ?? throw new InvalidDataException($"Missing authored Bling Gnome modifier description '{modifierType}.Description'");
            string modifierVisual = RequireAuthoredString(modifierDescription, "Visual");

            int searchRadius = RequireAuthoredInt(conversionEffect, "SearchRadius");
            if (searchRadius <= 0 || searchRadius > ushort.MaxValue)
                throw new InvalidDataException($"Bling Gnome SearchRadius is outside the wire range: {searchRadius}");
            int searchRadiusFixed = checked(searchRadius * Fixed8Scale);
            return new BlingConversionContract(
                (ushort)searchRadius,
                searchRadiusFixed,
                conversionRatioFixed,
                (uint)durationTicks,
                modifierType,
                modifierVisual,
                itemGeneratorTable);
        }

        private static GCNode RequireSingleAuthoredEffect(GCNode effectRoot, string clientType)
        {
            GCNode match = null;
            foreach (GCNode child in effectRoot.EnumerateChildrenInOrder())
            {
                if (!AuthoredExtends(child, clientType))
                    continue;
                if (match != null)
                    throw new InvalidDataException($"Authored effect '{effectRoot.CanonicalPath}' contains multiple {clientType} nodes");
                match = GCDatabase.Instance.ResolveWithInheritance(child);
            }
            return match ?? throw new InvalidDataException($"Authored effect '{effectRoot.CanonicalPath}' is missing {clientType}");
        }

        private static bool AuthoredExtends(GCNode node, string clientType)
        {
            for (int depth = 0; node != null && depth < 32; depth++)
            {
                string extends = node.Extends?.Trim() ?? string.Empty;
                if (extends.Equals(clientType, StringComparison.OrdinalIgnoreCase)
                    || extends.EndsWith("." + clientType, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (string.IsNullOrWhiteSpace(extends))
                    return false;
                node = GCDatabase.Instance.ResolveWithInheritance(extends);
            }
            return false;
        }

        private static string RequireAuthoredString(GCNode node, string property)
        {
            if (node == null || !node.HasProperty(property))
                throw new InvalidDataException($"Authored node '{node?.CanonicalPath}' is missing {property}");
            string value = node.GetString(property, string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException($"Authored node '{node.CanonicalPath}' has an empty {property}");
            return value;
        }

        private static int RequireAuthoredFixed32(GCNode node, string property)
        {
            string raw = RequireAuthoredString(node, property);
            if (!GCNode.TryParseFixed32(raw, out int value))
                throw new InvalidDataException($"Authored node '{node.CanonicalPath}' has invalid fixed32 {property}='{raw}'");
            return value;
        }

        private static int RequireAuthoredInt(GCNode node, string property)
        {
            string raw = RequireAuthoredString(node, property);
            if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int value))
                throw new InvalidDataException($"Authored node '{node.CanonicalPath}' has invalid integer {property}='{raw}'");
            return value;
        }

        private static uint ApplyConversionRatioFixed8(uint baseGold, int conversionRatioFixed)
        {
            if (baseGold == 0)
                throw new InvalidDataException("Bling conversion requires a positive native item value");
            if (conversionRatioFixed <= 0)
                throw new InvalidDataException($"Bling conversion ratio must be positive, got {conversionRatioFixed}");
            long scaled = ((long)baseGold * conversionRatioFixed) >> 8;
            if (scaled < 1)
                scaled = 1;
            if (scaled > uint.MaxValue)
                throw new OverflowException("Bling conversion gold exceeds UInt32");
            return (uint)scaled;
        }


        private void WritePlayAnimationSubmsg(LEWriter writer, GnomeState g, ushort logicalId, byte animState, uint durationTicks)
        {
            byte wireState = animState;
            uint wireArg = logicalId;

            if (logicalId == ANIM_ACTIVE_SKILL)
            {
                wireState = ANIM_STATE_ACTIVE;
                wireArg = 40;
            }
            else if (logicalId >= ANIM_FIDGET_FIRST && logicalId <= ANIM_FIDGET_LAST)
            {
                wireState = ANIM_STATE_DIRECT;
                wireArg = logicalId;
            }

            writer.WriteByte(0x35);
            writer.WriteUInt16(g.BehaviorId);
            writer.WriteByte(0x04);
            writer.WriteByte(0x20);
            writer.WriteByte(0x00);
            writer.WriteUInt32(wireState);
            writer.WriteUInt32(wireArg);
            writer.WriteUInt32(durationTicks);
            writer.WriteUInt32(0x3F800000);
            WriteGnomeEntitySynchInfo(writer, g);
        }

        private void SendPlayAnimation(RRConnection conn, GnomeState g, ushort logicalId, byte animState, uint durationTicks)
        {
            if (!g.BehaviorCreated) return;
            if (!_gnomes.TryGetValue(conn.ConnId, out var liveG) || !ReferenceEquals(liveG, g)) return;

            var writer = new LEWriter();
            writer.WriteByte(0x07);

            WritePlayAnimationSubmsg(writer, g, logicalId, animState, durationTicks);

            writer.WriteByte(0x06);

            BroadcastGnomePacket(conn, g, writer.ToArray());
        }

        private void SendConvertItemsToGoldAction(RRConnection conn, GnomeState g, int sourceFixedX, int sourceFixedY, ushort searchRadius, int conversionRatioFixed)
        {
            if (!g.BehaviorCreated) return;
            if (!_gnomes.TryGetValue(conn.ConnId, out var liveG) || !ReferenceEquals(liveG, g)) return;

            var writer = new LEWriter();
            writer.WriteByte(0x07);

            writer.WriteByte(0x35);
            writer.WriteUInt16(g.BehaviorId);
            writer.WriteByte(0x04);
            writer.WriteByte(0xA1);

            writer.WriteByte(0x00);
            writer.WriteUInt32(5);
            writer.WriteUInt16(searchRadius);
            writer.WriteInt32(sourceFixedX);
            writer.WriteInt32(sourceFixedY);
            writer.WriteInt32(conversionRatioFixed);

            WriteGnomeEntitySynchInfo(writer, g);
            writer.WriteByte(0x06);

            BroadcastGnomePacket(conn, g, writer.ToArray());
            Debug.LogError($"[GNOME-CONVERT] ConvertItemsToGold action sent, radius={searchRadius}, ratioFixed8={conversionRatioFixed}");
        }

        private byte[] BuildBlingModifierAddPacket(GnomeState g)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x35);
            writer.WriteUInt16(g.ModifiersId);
            writer.WriteByte(0x00);
            writer.WriteByte(0xFF);
            writer.WriteCString(g.BlingModifierType);
            writer.WriteUInt32(g.BlingModifierId);
            writer.WriteByte(1);
            writer.WriteUInt32(0x100);
            writer.WriteUInt32(g.BlingModifierRemainingTicks);
            writer.WriteByte(1);
            WriteGnomeEntitySynchInfo(writer, g);
            writer.WriteByte(0x06);
            Debug.LogError($"[GNOME-MODIFIER] entity={g.EntityId} event=add id={g.BlingModifierId} duration={g.BlingModifierRemainingTicks} visual={g.BlingModifierVisual} sourceFunction=SpellModEffect::doEffect@0x00554460->Modifiers::processAddModifier@0x00502280");
            return writer.ToArray();
        }

        private byte[] BuildBlingModifierRemovePacket(GnomeState g)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x35);
            writer.WriteUInt16(g.ModifiersId);
            writer.WriteByte(0x01);
            writer.WriteUInt32(g.BlingModifierId);
            WriteGnomeEntitySynchInfo(writer, g);
            writer.WriteByte(0x06);
            return writer.ToArray();
        }


        private void WriteGnomeEntitySynchInfo(LEWriter writer, GnomeState g)
        {
            writer.WriteByte(0x02);
            writer.WriteUInt32(g.HitPointsWire);
            if (VerboseGnomeLog)
                Debug.LogError($"[ENTITY-SYNCH-INFO] packet=GNOME owner=BlingGnome entity={g.EntityId} component={g.BehaviorId} flags=0x02 hp={g.HitPointsWire}");
        }

        public void OnPlayerDisconnect(int connId)
        {
            if (_gnomes.TryGetValue(connId, out var g))
            {
                var despawn = new LEWriter();
                despawn.WriteByte(0x07);
                despawn.WriteByte(0x05);
                despawn.WriteUInt16(g.EntityId);
                despawn.WriteByte(0x06);
                byte[] despawnPacket = despawn.ToArray();
                if (_server != null)
                {
                    var peerConnIds = new List<int>(g.ReplicatedToConnIds);
                    peerConnIds.Sort();
                    foreach (int peerConnId in peerConnIds)
                    {
                        RRConnection peer = _server.GetConnectionByConnId(peerConnId);
                        if (peer == null || !peer.IsConnected)
                            continue;
                        int purged = peer.MessageQueue.RemoveComponentUpdates(g.BehaviorId);
                        EnqueueGnomeBlock(peer, despawnPacket);
                        Debug.LogError($"[GNOME] disconnect remove entity=0x{g.EntityId:X4} peer={peer.ConnId} purged={purged}");
                    }
                }
                EntitySynchInfo.EntitySynchInfoAuthority.Instance.UnregisterBlingGnome(g.EntityId);
                BlingGnomeLockstepRuntime.Instance.Unregister(g.EntityId);
                _gnomes.Remove(connId);
                _gnomeOrder.Remove(connId);
                ReleaseGnomeNetworkIds(g);
            }
            foreach (int ownerConnId in _gnomeOrder)
            {
                if (!_gnomes.TryGetValue(ownerConnId, out GnomeState peerGnome))
                    continue;
                peerGnome.ReplicatedToConnIds.Remove(connId);
                peerGnome.ReplicatedInstanceKeys.Remove(connId);
            }
        }
    }
}
