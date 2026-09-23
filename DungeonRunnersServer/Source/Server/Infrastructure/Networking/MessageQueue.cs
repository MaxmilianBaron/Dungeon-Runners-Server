using System;
using System.Collections.Generic;

namespace DungeonRunners.Networking
{
    public enum ClientEntityMessageRole
    {
        None,
        OwnerClientControlReset
    }

    public class MessageQueue
    {
        private struct QueueEntry
        {
            public byte[] Data;
            public Func<byte[]> Builder;
            public bool RetryOnEmpty;
            public bool IsNativeMoverUpdate;
            public bool HasNativeMoverCadence;
            public ushort NativeMoverComponentId;
            public int NativeMoverRecords;
            public int NativeMoverDelayFlushes;
            public int DelayFlushes;
            public ulong NativeMoverBatchId;
            public ushort DeferredComponentId;
            public ClientEntityMessageRole Role;

            public QueueEntry(byte[] data, Func<byte[]> builder, bool retryOnEmpty, bool isNativeMoverUpdate = false, bool hasNativeMoverCadence = false, ushort nativeMoverComponentId = 0, int nativeMoverRecords = 0, int nativeMoverDelayFlushes = 0, int delayFlushes = 0, ulong nativeMoverBatchId = 0, ushort deferredComponentId = 0, ClientEntityMessageRole role = ClientEntityMessageRole.None)
            {
                Data = data;
                Builder = builder;
                RetryOnEmpty = retryOnEmpty;
                IsNativeMoverUpdate = isNativeMoverUpdate;
                HasNativeMoverCadence = hasNativeMoverCadence;
                NativeMoverComponentId = nativeMoverComponentId;
                NativeMoverRecords = nativeMoverRecords;
                NativeMoverDelayFlushes = nativeMoverDelayFlushes;
                DelayFlushes = delayFlushes;
                NativeMoverBatchId = nativeMoverBatchId;
                DeferredComponentId = deferredComponentId;
                Role = role;
            }
        }

        private readonly Queue<QueueEntry> _queue = new Queue<QueueEntry>();
        private readonly HashSet<ushort> _nativeMoverStreamAdmissions = new HashSet<ushort>();
        private readonly HashSet<ushort> _nativeMoverTerminalRestartAdmissions = new HashSet<ushort>();
        private readonly object _lock = new object();
        private ulong _nativeMoverBatchSequence;

        public void Enqueue(byte[] data)
        {
            data = StripClientEntityWrapper(data);
            if (data.Length == 0)
                return;
            lock (_lock)
                _queue.Enqueue(new QueueEntry(data, null, false));
        }

        public void EnqueueAfterFlushes(byte[] data, int delayFlushes)
        {
            data = StripClientEntityWrapper(data);
            if (data.Length == 0)
                return;
            int safeDelayFlushes = Math.Max(0, delayFlushes);
            lock (_lock)
                _queue.Enqueue(new QueueEntry(data, null, false, delayFlushes: safeDelayFlushes));
        }

        public void EnqueueNativeMover(byte[] data, ushort componentId, int recordCount = 1, int delayFlushes = 0)
        {
            data = StripClientEntityWrapper(data);
            if (data.Length == 0 || componentId == 0)
                return;
            int safeRecordCount = recordCount > 0 ? recordCount : 1;
            int safeDelayFlushes = Math.Max(0, delayFlushes);
            lock (_lock)
                _queue.Enqueue(new QueueEntry(data, null, false, true, true, componentId, safeRecordCount, safeDelayFlushes));
        }

        public void EnqueueNativeMoverBatch(IReadOnlyList<byte[]> packets, ushort componentId, int initialDelayFlushes = 0)
        {
            if (packets == null || packets.Count == 0 || componentId == 0)
                return;
            var normalized = new List<byte[]>(packets.Count);
            for (int i = 0; i < packets.Count; i++)
            {
                byte[] data = StripClientEntityWrapper(packets[i]);
                if (data.Length > 0)
                    normalized.Add(data);
            }
            if (normalized.Count == 0)
                return;
            int safeInitialDelayFlushes = Math.Max(0, initialDelayFlushes);
            lock (_lock)
            {
                ulong batchId = ++_nativeMoverBatchSequence;
                if (batchId == 0)
                    batchId = ++_nativeMoverBatchSequence;
                bool streamAdmitted = _nativeMoverStreamAdmissions.Contains(componentId);
                bool terminalRestartPending = _nativeMoverTerminalRestartAdmissions.Contains(componentId);
                for (int i = 0; i < normalized.Count; i++)
                {
                    bool terminalRecord = IsTerminalNativeMoverUpdate(normalized[i]);
                    int delayFlushes = !streamAdmitted && !terminalRecord
                        ? terminalRestartPending ? Math.Max(0, safeInitialDelayFlushes - 1) : safeInitialDelayFlushes
                        : 0;
                    _queue.Enqueue(new QueueEntry(normalized[i], null, false, true, true, componentId, 1, delayFlushes: delayFlushes, nativeMoverBatchId: batchId));
                    streamAdmitted = !terminalRecord;
                    terminalRestartPending = terminalRecord;
                }
                if (streamAdmitted)
                    _nativeMoverStreamAdmissions.Add(componentId);
                else
                    _nativeMoverStreamAdmissions.Remove(componentId);
                if (terminalRestartPending)
                    _nativeMoverTerminalRestartAdmissions.Add(componentId);
                else
                    _nativeMoverTerminalRestartAdmissions.Remove(componentId);
            }
        }

        private static bool IsTerminalNativeMoverUpdate(byte[] data)
        {
            return data != null
                && data.Length >= 7
                && data[0] == 0x35
                && data[3] == 0x65
                && data[5] == 0x01
                && (data[6] & 0x01) != 0;
        }

        public void EnqueueDeferred(Func<byte[]> builder, bool retryOnEmpty = false, ushort componentId = 0, ClientEntityMessageRole role = ClientEntityMessageRole.None)
        {
            if (builder == null)
                return;
            lock (_lock)
                _queue.Enqueue(new QueueEntry(null, builder, retryOnEmpty, deferredComponentId: componentId, role: role));
        }

        private static byte[] StripClientEntityWrapper(byte[] data)
        {
            if (data == null || data.Length == 0)
                return Array.Empty<byte>();
            if (data.Length >= 2 && data[0] == 0x07 && data[data.Length - 1] == 0x06)
            {
                var inner = new byte[data.Length - 2];
                Buffer.BlockCopy(data, 1, inner, 0, inner.Length);
                return inner;
            }
            return data;
        }

        public bool IsEmpty()
        {
            lock (_lock)
                return _queue.Count == 0;
        }

        public List<byte[]> DequeueAll(int nativeMoverRecordBudgetPerComponent = int.MaxValue, List<ClientEntityMessageRole> emittedRoles = null)
        {
            lock (_lock)
            {
                if (nativeMoverRecordBudgetPerComponent != int.MaxValue)
                    nativeMoverRecordBudgetPerComponent = Math.Max(0, nativeMoverRecordBudgetPerComponent);
                var messages = new List<byte[]>(_queue.Count);
                List<QueueEntry> requeueFront = null;
                Dictionary<ushort, int> nativeMoverRecordsEmitted = nativeMoverRecordBudgetPerComponent == int.MaxValue
                    ? null
                    : new Dictionary<ushort, int>();
                Dictionary<ushort, ulong> nativeMoverBatchEmitted = nativeMoverRecordsEmitted == null
                    ? null
                    : new Dictionary<ushort, ulong>();
                HashSet<ushort> deferredComponents = nativeMoverRecordsEmitted == null
                    ? null
                    : new HashSet<ushort>();
                int count = _queue.Count;
                for (int i = 0; i < count; i++)
                {
                    QueueEntry entry = _queue.Dequeue();
                    ushort componentId = entry.HasNativeMoverCadence
                        ? entry.NativeMoverComponentId
                        : entry.DeferredComponentId;
                    if (componentId != 0 && deferredComponents != null && deferredComponents.Contains(componentId))
                    {
                        requeueFront ??= new List<QueueEntry>();
                        requeueFront.Add(entry);
                        continue;
                    }
                    byte[] data = entry.Data ?? StripClientEntityWrapper(entry.Builder?.Invoke());
                    if (data.Length == 0)
                    {
                        if (entry.RetryOnEmpty)
                        {
                            requeueFront ??= new List<QueueEntry>();
                            requeueFront.Add(entry);
                        }
                        continue;
                    }

                    if (componentId == 0 && TryReadClientEntityComponentId(data, out ushort parsedComponentId))
                        componentId = parsedComponentId;
                    if (componentId != 0 && deferredComponents != null && deferredComponents.Contains(componentId))
                    {
                        requeueFront ??= new List<QueueEntry>();
                        requeueFront.Add(MaterializedEntry(entry, data));
                        continue;
                    }

                    if (entry.DelayFlushes > 0)
                    {
                        QueueEntry delayedEntry = MaterializedEntry(entry, data);
                        delayedEntry.DelayFlushes--;
                        requeueFront ??= new List<QueueEntry>();
                        requeueFront.Add(delayedEntry);
                        if (componentId != 0)
                            deferredComponents?.Add(componentId);
                        continue;
                    }

                    if (entry.HasNativeMoverCadence && nativeMoverRecordsEmitted != null && entry.NativeMoverDelayFlushes > 0)
                    {
                        QueueEntry delayedEntry = MaterializedEntry(entry, data);
                        delayedEntry.NativeMoverDelayFlushes--;
                        requeueFront ??= new List<QueueEntry>();
                        requeueFront.Add(delayedEntry);
                        deferredComponents.Add(entry.NativeMoverComponentId);
                        continue;
                    }

                    if (entry.HasNativeMoverCadence && nativeMoverRecordsEmitted != null)
                    {
                        nativeMoverRecordsEmitted.TryGetValue(entry.NativeMoverComponentId, out int emitted);
                        int entryRecords = entry.NativeMoverRecords > 0 ? entry.NativeMoverRecords : 1;
                        bool sameBatch = entry.NativeMoverBatchId != 0
                            && nativeMoverBatchEmitted.TryGetValue(entry.NativeMoverComponentId, out ulong emittedBatchId)
                            && emittedBatchId == entry.NativeMoverBatchId;
                        if (emitted > 0 && !sameBatch && emitted + entryRecords > nativeMoverRecordBudgetPerComponent)
                        {
                            requeueFront ??= new List<QueueEntry>();
                            requeueFront.Add(MaterializedEntry(entry, data));
                            deferredComponents.Add(entry.NativeMoverComponentId);
                            continue;
                        }
                        nativeMoverRecordsEmitted[entry.NativeMoverComponentId] = emitted + entryRecords;
                        if (entry.NativeMoverBatchId != 0)
                            nativeMoverBatchEmitted[entry.NativeMoverComponentId] = entry.NativeMoverBatchId;
                    }

                    messages.Add(data);
                    emittedRoles?.Add(entry.Role);
                }
                if (requeueFront != null)
                    RequeueFront(requeueFront);
                return messages;
            }
        }

        private static QueueEntry MaterializedEntry(QueueEntry entry, byte[] data)
        {
            return new QueueEntry(data, null, false, entry.IsNativeMoverUpdate, entry.HasNativeMoverCadence, entry.NativeMoverComponentId, entry.NativeMoverRecords, entry.NativeMoverDelayFlushes, entry.DelayFlushes, entry.NativeMoverBatchId, entry.DeferredComponentId, entry.Role);
        }

        private static bool TryReadClientEntityComponentId(byte[] data, out ushort componentId)
        {
            componentId = 0;
            if (data == null || data.Length < 3 || data[0] != 0x35)
                return false;
            componentId = (ushort)(data[1] | (data[2] << 8));
            return componentId != 0;
        }

        private void RequeueFront(List<QueueEntry> entries)
        {
            if (entries == null || entries.Count == 0)
                return;

            var tail = _queue.ToArray();
            _queue.Clear();
            foreach (var entry in entries)
                _queue.Enqueue(entry);
            foreach (var entry in tail)
                _queue.Enqueue(entry);
        }

        public int RemoveComponentUpdates(ushort componentId)
        {
            return PurgeComponentEntries(componentId);
        }

        public int PurgeComponentForEpochTransition(ushort componentId)
        {
            return PurgeComponentEntries(componentId);
        }

        private int PurgeComponentEntries(ushort componentId)
        {
            if (componentId == 0)
                return 0;
            lock (_lock)
            {
                _nativeMoverStreamAdmissions.Remove(componentId);
                _nativeMoverTerminalRestartAdmissions.Remove(componentId);
                int removed = 0;
                int count = _queue.Count;
                for (int i = 0; i < count; i++)
                {
                    QueueEntry entry = _queue.Dequeue();
                    bool remove = entry.DeferredComponentId == componentId;
                    if (entry.Data != null && entry.Data.Length >= 3 && entry.Data[0] == 0x35)
                    {
                        ushort entryComponentId = (ushort)(entry.Data[1] | (entry.Data[2] << 8));
                        remove = entryComponentId == componentId;
                    }

                    if (remove)
                    {
                        removed++;
                        continue;
                    }
                    _queue.Enqueue(entry);
                }
                return removed;
            }
        }

        public bool HasNativeMoverUpdates(ushort componentId)
        {
            if (componentId == 0)
                return false;
            lock (_lock)
            {
                foreach (QueueEntry entry in _queue)
                {
                    if (entry.IsNativeMoverUpdate && entry.NativeMoverComponentId == componentId)
                        return true;
                }
                return false;
            }
        }

        public void ResetNativeMoverAdmission(ushort componentId)
        {
            if (componentId == 0)
                return;
            lock (_lock)
            {
                _nativeMoverStreamAdmissions.Remove(componentId);
                _nativeMoverTerminalRestartAdmissions.Remove(componentId);
            }
        }

        public int CountPendingUnitMoverRecords(ushort componentId)
        {
            if (componentId == 0)
                return 0;
            lock (_lock)
            {
                int records = 0;
                foreach (QueueEntry entry in _queue)
                {
                    if (!TryReadUnitMoverRecordCount(entry.Data, componentId, out int entryRecords))
                        continue;
                    records = checked(records + entryRecords);
                }
                return records;
            }
        }

        public static int CountUnitMoverRecords(IReadOnlyList<byte[]> messages, ushort componentId)
        {
            if (messages == null || componentId == 0)
                return 0;
            int records = 0;
            for (int i = 0; i < messages.Count; i++)
            {
                records = checked(records + CountUnitMoverRecords(messages[i], componentId));
            }
            return records;
        }

        public static int CountUnitMoverRecords(byte[] message, ushort componentId)
        {
            return TryReadUnitMoverRecordCount(message, componentId, out int recordCount) ? recordCount : 0;
        }

        public static List<(byte MoveType, int Heading, int X, int Y)> ReadUnitMoverRecords(IReadOnlyList<byte[]> messages, ushort componentId)
        {
            var records = new List<(byte MoveType, int Heading, int X, int Y)>();
            if (messages == null || componentId == 0)
                return records;
            for (int messageIndex = 0; messageIndex < messages.Count; messageIndex++)
            {
                byte[] data = messages[messageIndex];
                if (!TryReadUnitMoverRecordCount(data, componentId, out int recordCount))
                    continue;
                int offset = 6;
                for (int recordIndex = 0; recordIndex < recordCount; recordIndex++)
                {
                    records.Add((
                        data[offset],
                        ReadInt32LittleEndian(data, offset + 1),
                        ReadInt32LittleEndian(data, offset + 5),
                        ReadInt32LittleEndian(data, offset + 9)));
                    offset += 13;
                }
            }
            return records;
        }

        public static List<(byte MoveType, int Heading, int X, int Y)> ReadUnitMoverRecordsForSession(
            IReadOnlyList<byte[]> messages,
            ushort componentId,
            byte expectedSessionId,
            out int observedRecords,
            out int rejectedRecords)
        {
            var records = new List<(byte MoveType, int Heading, int X, int Y)>();
            observedRecords = 0;
            rejectedRecords = 0;
            if (messages == null || componentId == 0)
                return records;
            for (int messageIndex = 0; messageIndex < messages.Count; messageIndex++)
            {
                byte[] data = messages[messageIndex];
                if (!TryReadUnitMoverRecordCountWithSession(data, componentId, out int recordCount, out byte sessionId))
                    continue;
                observedRecords = checked(observedRecords + recordCount);
                if (sessionId != expectedSessionId)
                {
                    rejectedRecords = checked(rejectedRecords + recordCount);
                    continue;
                }
                int offset = 6;
                for (int recordIndex = 0; recordIndex < recordCount; recordIndex++)
                {
                    records.Add((
                        data[offset],
                        ReadInt32LittleEndian(data, offset + 1),
                        ReadInt32LittleEndian(data, offset + 5),
                        ReadInt32LittleEndian(data, offset + 9)));
                    offset += 13;
                }
            }
            return records;
        }

        private static int ReadInt32LittleEndian(byte[] data, int offset)
        {
            uint value = (uint)data[offset]
                | ((uint)data[offset + 1] << 8)
                | ((uint)data[offset + 2] << 16)
                | ((uint)data[offset + 3] << 24);
            return unchecked((int)value);
        }

        private static bool TryReadUnitMoverRecordCount(byte[] data, ushort componentId, out int recordCount)
        {
            recordCount = 0;
            if (data == null || data.Length < 6 || data[0] != 0x35 || data[3] != 0x65)
                return false;
            ushort entryComponentId = (ushort)(data[1] | (data[2] << 8));
            if (entryComponentId != componentId)
                return false;
            recordCount = data[5];
            return recordCount > 0 && data.Length >= 6 + checked(recordCount * 13);
        }

        private static bool TryReadUnitMoverRecordCountWithSession(
            byte[] data,
            ushort componentId,
            out int recordCount,
            out byte sessionId)
        {
            recordCount = 0;
            sessionId = 0;
            if (data == null || data.Length < 6 || data[0] != 0x35 || data[3] != 0x65)
                return false;
            ushort entryComponentId = (ushort)(data[1] | (data[2] << 8));
            if (entryComponentId != componentId)
                return false;
            sessionId = data[4];
            recordCount = data[5];
            return recordCount > 0 && data.Length >= 6 + checked(recordCount * 13);
        }

        public int Count
        {
            get
            {
                lock (_lock)
                    return _queue.Count;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _queue.Clear();
                _nativeMoverStreamAdmissions.Clear();
                _nativeMoverTerminalRestartAdmissions.Clear();
            }
        }
    }
}
