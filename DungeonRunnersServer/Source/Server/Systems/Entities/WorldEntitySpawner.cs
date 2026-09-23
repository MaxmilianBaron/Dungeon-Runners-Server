using System;
using System.Collections.Generic;
using System.Linq;
using DungeonRunners.Data;
using DungeonRunners.Engine;
using DungeonRunners.Utilities;

namespace DungeonRunners.Gameplay
{
    public class WorldEntitySpawner
    {
        private static WorldEntitySpawner _instance;
        public static WorldEntitySpawner Instance => _instance ??= new WorldEntitySpawner();

        private Dictionary<string, List<WorldEntityData>> _entitiesByZone
            = new Dictionary<string, List<WorldEntityData>>(StringComparer.OrdinalIgnoreCase);

        private Dictionary<ushort, WorldEntityData> _spawnedEntities
            = new Dictionary<ushort, WorldEntityData>();


        public void Initialize()
        {
            if (!AuthoredGameplayCatalog.IsLoaded)
                throw new InvalidOperationException("Authored gameplay catalog must be loaded before world entities");
            _entitiesByZone.Clear();
            foreach (var entry in AuthoredGameplayCatalog.WorldEntitiesByZone)
            {
                _entitiesByZone.Add(entry.Key, new List<WorldEntityData>(entry.Value));
            }
            int count = _entitiesByZone.Sum(entry => entry.Value.Count);
            Debug.LogError($"[WORLD-ENTITY-SPAWNER] loaded={count} zones={_entitiesByZone.Count}");
        }


        public List<SpawnedWorldEntity> GetEntitiesForZone(string zoneName)
        {
            var result = new List<SpawnedWorldEntity>();
            if (string.IsNullOrEmpty(zoneName)) return result;

            string key = zoneName.ToLower();
            if (!_entitiesByZone.TryGetValue(key, out var entities))
                return result;

            foreach (var e in entities)
                result.Add(new SpawnedWorldEntity { Data = e });

            return result;
        }

        public static void WriteEntitySpawn(LEWriter writer, ushort entityId, ushort behaviorId, WorldEntityData data)
        {
            writer.WriteByte(0x01);
            writer.WriteUInt16(entityId);
            WriteGCType(writer, data.GCType);

            writer.WriteByte(0x02);
            writer.WriteUInt16(entityId);
            writer.WriteUInt32(data.Flags);

            writer.WriteInt32(data.PosFixedX);
            writer.WriteInt32(data.PosFixedY);
            writer.WriteInt32(data.PosFixedZ);
            writer.WriteInt32(data.HeadingFixed);

            writer.WriteByte(0x00);

            if (data.IsGate)
            {
                writer.WriteByte(0x00);
                writer.WriteByte(0x00);
            }
            else
            {
                writer.WriteByte(0x00);
                writer.WriteByte(0x00);
                writer.WriteUInt16(0);
                writer.WriteUInt16(0);

                WriteNonCombatInteractiveReadInit(writer, data.GCType);

                writer.WriteByte(0x32);
                writer.WriteUInt16(entityId);
                writer.WriteUInt16(behaviorId);
                WriteGCType(writer, "base.noncombatinteractive.behavior");

                writer.WriteByte(0x01);

                writer.WriteByte(0xFF);
                writer.WriteByte(0x00);
                writer.WriteByte(0x00);
                writer.WriteByte(0x01);

                writer.WriteByte(0x08);
                writer.WriteInt32(data.HeadingFixed);
                writer.WriteInt32(data.HeadingFixed);
                writer.WriteByte(0x00);

                writer.WriteByte(0xFF);
                writer.WriteByte(0x00);
                writer.WriteByte(0x00);
            }
        }

        internal static void WriteNonCombatInteractiveReadInit(LEWriter writer, string gcType)
        {
            var description = GCDatabase.Instance.ResolveWithInheritance(gcType)?.GetChild("Description")
                ?? throw new InvalidOperationException($"NonCombatInteractive description is unresolved: '{gcType}'");
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            writer.WriteUInt16(0);
            if (description.GetBool("TrackActivatingPlayers", false))
                writer.WriteUInt32(0);
        }


        public void TrackSpawnedEntity(ushort entityId, WorldEntityData data)
        {
            _spawnedEntities[entityId] = data;
        }

        public bool TryGetEntity(ushort entityId, out WorldEntityData data)
        {
            return _spawnedEntities.TryGetValue(entityId, out data);
        }

        public bool FindEntityByName(string name, string zoneName, out ushort entityId, out WorldEntityData data)
        {
            string baseZone = zoneName;
            int instIdx = zoneName.IndexOf("_inst", StringComparison.OrdinalIgnoreCase);
            if (instIdx > 0) baseZone = zoneName.Substring(0, instIdx);

            foreach (var entityEntry in _spawnedEntities)
            {
                var entity = entityEntry.Value;
                if (string.Equals(entity.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(entity.Zone, baseZone, StringComparison.OrdinalIgnoreCase))
                {
                    entityId = entityEntry.Key;
                    data = entity;
                    return true;
                }
            }
            entityId = 0;
            data = null;
            return false;
        }

        public Dictionary<ushort, WorldEntityData> GetSpawnedEntities()
        {
            return _spawnedEntities;
        }

        public void RemoveEntity(ushort entityId)
        {
            _spawnedEntities.Remove(entityId);
        }

        public void ClearZoneEntities()
        {
            _spawnedEntities.Clear();
        }

        private static void WriteGCType(LEWriter writer, string typeName)
        {
            writer.WriteByte(0xFF);
            writer.WriteCString(typeName);
        }
    }


    [Serializable]
    public class WorldEntityData
    {
        public int Id;
        public string Zone;
        public string Name;
        public string GCType;
        public string EntityType;
        public int PosFixedX;
        public int PosFixedY;
        public int PosFixedZ;
        public int HeadingFixed;
        public uint Flags;

        public string ItemGenerator;
        public int ItemCount;
        public string ItemGenerator2;
        public int ItemCount2;
        public string ItemGenerator3;
        public int ItemCount3;
        public string ItemGenerator4;
        public int ItemCount4;
        public string ItemGenerator5;
        public int ItemCount5;

        public string TargetZone;
        public string TargetSpawn;

        public string Label;
        public bool AllowMultiple;

        public bool IsChest => EntityType == "chest";
        public bool IsTeleporter => EntityType == "teleporter";
        public bool IsShrine => EntityType == "shrine";
        public bool IsGate => EntityType == "gate";
        public bool IsNPC => EntityType == "npc";

        public IEnumerable<(string Generator, int Count, int Slot)> GetChestGenerators()
        {
            var storedGenerators = new (string Generator, int Count)[]
            {
                (ItemGenerator, ItemCount),
                (ItemGenerator2, ItemCount2),
                (ItemGenerator3, ItemCount3),
                (ItemGenerator4, ItemCount4),
                (ItemGenerator5, ItemCount5)
            };
            foreach (var generator in WorldEntityLoot.GetNonCombatInteractiveItemGenerators(GCType, storedGenerators))
                yield return generator;
        }
    }

    public class SpawnedWorldEntity
    {
        public WorldEntityData Data;
        public ushort EntityId;
    }
}
