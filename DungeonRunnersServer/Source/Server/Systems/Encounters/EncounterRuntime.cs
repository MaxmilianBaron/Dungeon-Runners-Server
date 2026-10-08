using System.Collections.Generic;

namespace DungeonRunners.Combat
{
    public sealed class EncounterRuntime
    {
        private readonly HashSet<uint> _liveUnits = new HashSet<uint>();
        private readonly HashSet<uint> _returningUnits = new HashSet<uint>();

        public string Key;
        public byte StateByte;
        public ushort ActiveTimer;
        public ushort ScanTimer = 0x1E;
        public bool ScanEnabled = true;
        public bool SeenFlag;
        public uint ActiveWorldEncounterId;

        public byte LiveUnitCount => (byte)System.Math.Min(byte.MaxValue, _liveUnits.Count);
        public byte ReturningUnitCount => (byte)System.Math.Min(byte.MaxValue, _returningUnits.Count);

        public void AddUnit(uint entityId)
        {
            if (entityId != 0)
                _liveUnits.Add(entityId);
            ScanEnabled = true;
            if (ScanTimer == 0)
                ScanTimer = 0x1E;
        }

        public void MarkUnitSpawned(uint entityId)
        {
            if (entityId == 0)
                return;
            bool wasEmpty = _liveUnits.Count == 0;
            _returningUnits.Remove(entityId);
            if (_liveUnits.Add(entityId) && wasEmpty)
                ActiveTimer = 900;
        }

        public void MarkActive()
        {
            StateByte = 2;
            SeenFlag = true;
            if (ActiveTimer == 0)
                ActiveTimer = 0x96;
        }

        public bool TickActive(bool playerInRange)
        {
            if (StateByte != 2)
                return false;
            if (ActiveTimer > 0)
            {
                ActiveTimer--;
                if (ActiveTimer != 0)
                    return false;
            }
            if (playerInRange)
            {
                ActiveTimer = 0x96;
                return false;
            }
            StateByte = 1;
            return true;
        }

        public void MarkUnitDied(uint entityId)
        {
            if (entityId == 0 || !_liveUnits.Remove(entityId))
                return;

            _returningUnits.Remove(entityId);
            if (_liveUnits.Count == 0)
            {
                StateByte = 3;
                ScanEnabled = false;
                _returningUnits.Clear();
                ActiveTimer = 0x1C2;
            }
        }

        public bool MarkUnitRemoved(uint entityId)
        {
            if (entityId == 0 || !_liveUnits.Remove(entityId))
                return false;

            if (_liveUnits.Count != 0)
                return false;

            SeenFlag = false;
            StateByte = 0;
            ScanTimer = 0x1E;
            ScanEnabled = true;
            _returningUnits.Clear();
            ActiveTimer = 0x1C2;
            return true;
        }

        public void MarkReturning(uint entityId)
        {
            if (entityId != 0)
                _returningUnits.Add(entityId);
        }

        public void ClearReturning(uint entityId)
        {
            if (entityId != 0)
                _returningUnits.Remove(entityId);
        }
    }
}
