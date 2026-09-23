using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DungeonRunners.Gameplay;

namespace DungeonRunners.Networking
{
    public partial class GameServer
    {
        private readonly ConditionalWeakTable<DungeonMazeSpawner.ProceduralDungeonSnapshot, List<ZoneNPC>> _authoredNpcsBySnapshot = new();

        private bool TryGetAuthoredWorldSnapshot(RRConnection conn, out DungeonMazeSpawner.ProceduralDungeonSnapshot snapshot)
        {
            snapshot = null;
            if (conn == null) return false;
            if (DungeonMazeSpawner.IsProceduralZone(conn.CurrentZoneName)) return TryGetProceduralDungeonSnapshot(conn, out snapshot);
            if (!DungeonMazeSpawner.HasStaticEncounterData(conn.CurrentZoneName)) return false;
            snapshot = ZoneSpawner.Instance.GetOrCreateStaticSnapshot(conn.CurrentZoneName, GetInstanceZoneKey(conn), ResolveZoneLayoutSeed(conn, conn.CurrentZoneName));
            return snapshot != null;
        }

        private bool TryGetZoneNpcsForConnection(RRConnection conn, out List<ZoneNPC> npcs)
        {
            npcs = null;
            if (conn == null) return false;
            if (!DungeonMazeSpawner.IsProceduralZone(conn.CurrentZoneName) && !DungeonMazeSpawner.HasStaticEncounterData(conn.CurrentZoneName))
                return _zoneNPCs.TryGetValue(conn.CurrentZoneId, out npcs);
            if (!TryGetAuthoredWorldSnapshot(conn, out var snapshot)) return false;
            lock (_authoredNpcsBySnapshot)
                return GetOrCreateSnapshotNpcs(snapshot, out npcs);
        }

        private bool GetOrCreateSnapshotNpcs(DungeonMazeSpawner.ProceduralDungeonSnapshot snapshot, out List<ZoneNPC> npcs)
        {
            if (_authoredNpcsBySnapshot.TryGetValue(snapshot, out npcs))
            {
                return true;
            }
            npcs = new List<ZoneNPC>();
            foreach (NPCData definition in snapshot.Npcs)
            {
                bool merchant = MerchantRuntime.IsMerchant(definition.gcType);
                bool trainer = definition.gcType.IndexOf("Trainer", StringComparison.OrdinalIgnoreCase) >= 0;
                bool bank = definition.gcType.EndsWith(".Bank", StringComparison.OrdinalIgnoreCase);
                bool posse = definition.gcType.EndsWith(".PosseMagnate", StringComparison.OrdinalIgnoreCase);
                npcs.Add(new ZoneNPC
                {
                    GCClass = definition.gcType, Name = definition.name, PosFixedX = definition.PosFixedX, PosFixedY = definition.PosFixedY,
                    PosFixedZ = definition.PosFixedZ, HeadingFixed = definition.HeadingFixed, HasFixedPosition = true,
                    Id = AllocateGeneralEntityId(), UnitBehaviorId = AllocateGeneralEntityId(),
                    IsMerchant = merchant, MerchantId = merchant ? AllocateGeneralEntityId() : 0,
                    IsTrainer = trainer, TrainerId = trainer ? AllocateGeneralEntityId() : 0,
                    TrainerSkills = trainer ? GetTrainerSkillList(definition.gcType) : null,
                    IsBank = bank, BankComponentId = bank ? AllocateGeneralEntityId() : 0,
                    IsPosseMagnate = posse, PosseOptionComponentId = posse ? AllocateGeneralEntityId() : 0
                });
            }
            _authoredNpcsBySnapshot.Add(snapshot, npcs);
            return true;
        }
    }
}
