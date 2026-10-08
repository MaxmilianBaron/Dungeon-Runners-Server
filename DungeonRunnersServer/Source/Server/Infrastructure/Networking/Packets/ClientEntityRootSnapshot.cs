using System;
using System.Collections.Generic;
using DungeonRunners.Data;
using DungeonRunners.Utilities;

namespace DungeonRunners.Networking
{
    internal sealed class ClientEntityRootSnapshot
    {
        private readonly GCObject _root;
        private readonly ushort _id;
        private readonly Dictionary<GCObject, ClientEntityRootRecord.Component> _components = new Dictionary<GCObject, ClientEntityRootRecord.Component>();

        public LEWriter Type { get; } = new LEWriter();
        public LEWriter Create { get; } = new LEWriter();
        public LEWriter Initialize { get; } = new LEWriter();

        public ClientEntityRootSnapshot(GCObject root) : this(root, (ushort)(root?.Id ?? 0))
        {
        }

        public ClientEntityRootSnapshot(GCObject root, ushort id)
        {
            _root = root ?? throw new ArgumentNullException(nameof(root));
            _id = id;
        }

        public ClientEntityRootRecord.Component AddComponent(GCObject component, ushort id, bool active)
        {
            if (component == null || component.Id == 0 || !_root.Children.Contains(component))
                throw new InvalidOperationException("Snapshot component must belong to its root and have an entity ID");
            var record = new ClientEntityRootRecord.Component(id, active);
            _components.Add(component, record);
            return record;
        }

        public ClientEntityRootRecord GetRecord()
        {
            var record = new ClientEntityRootRecord(_id);
            Copy(Type, record.Type);
            Copy(Create, record.Create);
            Copy(Initialize, record.Initialize);
            int componentsFound = 0;
            foreach (GCObject child in _root.Children)
            {
                if (_components.TryGetValue(child, out ClientEntityRootRecord.Component component))
                {
                    componentsFound++;
                    ClientEntityRootRecord.Component target = record.AddComponent(component.Id, component.Active);
                    Copy(component.Type, target.Type);
                    Copy(component.Initialize, target.Initialize);
                }
            }
            if (componentsFound != _components.Count)
                record.Type.Abort();
            return record;
        }

        private static void Copy(LEWriter source, LEWriter target)
        {
            if (source.Aborted)
                target.Abort();
            else
                target.WriteBytes(source.ToArray());
        }

        public static void WriteRoots(LEWriter writer, IReadOnlyList<ClientEntityRootSnapshot> roots)
        {
            var records = new List<ClientEntityRootRecord>(roots.Count);
            foreach (ClientEntityRootSnapshot root in roots)
                records.Add(root.GetRecord());
            ClientEntityRootRecord.WriteSnapshot(writer, records);
        }
    }
}
