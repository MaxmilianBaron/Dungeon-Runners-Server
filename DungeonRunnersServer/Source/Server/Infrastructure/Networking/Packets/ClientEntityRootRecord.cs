using System;
using System.Collections.Generic;
using DungeonRunners.Utilities;

namespace DungeonRunners.Networking
{
    internal sealed class ClientEntityRootRecord
    {
        internal sealed class Component
        {
            public ushort Id { get; }
            public bool Active { get; }
            public LEWriter Type { get; } = new LEWriter();
            public LEWriter Initialize { get; } = new LEWriter();

            internal Component(ushort id, bool active)
            {
                Id = id;
                Active = active;
            }
        }

        private readonly List<Component> _components = new List<Component>();

        public ushort Id { get; }
        public LEWriter Type { get; } = new LEWriter();
        public LEWriter Create { get; } = new LEWriter();
        public LEWriter Initialize { get; } = new LEWriter();

        public ClientEntityRootRecord(ushort id)
        {
            Id = id;
        }

        public Component AddComponent(ushort id, bool active)
        {
            var component = new Component(id, active);
            _components.Add(component);
            return component;
        }

        public static void WriteSnapshot(LEWriter writer, IReadOnlyList<ClientEntityRootRecord> roots)
        {
            if (!Validate(writer, roots))
                return;
            foreach (ClientEntityRootRecord root in roots)
            {
                root.WriteCreate(writer, 0x01);
            }
            foreach (ClientEntityRootRecord root in roots)
            {
                writer.WriteByte(0x02);
                writer.WriteUInt16(root.Id);
                writer.WriteBytes(root.Initialize.ToArray());
                root.WriteComponents(writer);
            }
        }

        public static void WriteAdmissions(LEWriter writer, IReadOnlyList<ClientEntityRootRecord> roots)
        {
            if (!Validate(writer, roots))
                return;
            foreach (ClientEntityRootRecord root in roots)
            {
                root.WriteCreate(writer, 0x08);
                writer.WriteBytes(root.Initialize.ToArray());
                root.WriteComponents(writer);
            }
        }

        public static void WriteComponentInitializations(LEWriter writer, IReadOnlyList<ClientEntityRootRecord> roots)
        {
            if (!Validate(writer, roots))
                return;
            foreach (ClientEntityRootRecord root in roots)
                root.WriteComponents(writer);
        }

        private static bool Validate(LEWriter writer, IReadOnlyList<ClientEntityRootRecord> roots)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            if (roots == null)
                throw new ArgumentNullException(nameof(roots));
            foreach (ClientEntityRootRecord root in roots)
            {
                if (root == null || root.Type.Aborted || root.Type.Length == 0 || root.Create.Aborted || root.Initialize.Aborted)
                {
                    writer.Abort();
                    return false;
                }
                foreach (Component component in root._components)
                {
                    if (component.Type.Aborted || component.Type.Length == 0 || component.Initialize.Aborted)
                    {
                        writer.Abort();
                        return false;
                    }
                }
            }
            return !writer.Aborted;
        }

        private void WriteCreate(LEWriter writer, byte operation)
        {
            writer.WriteByte(operation);
            writer.WriteUInt16(Id);
            writer.WriteBytes(Type.ToArray());
            writer.WriteBytes(Create.ToArray());
        }

        private void WriteComponents(LEWriter writer)
        {
            foreach (Component component in _components)
            {
                writer.WriteByte(0x32);
                writer.WriteUInt16(Id);
                writer.WriteUInt16(component.Id);
                writer.WriteBytes(component.Type.ToArray());
                writer.WriteByte(component.Active ? (byte)1 : (byte)0);
                writer.WriteBytes(component.Initialize.ToArray());
            }
        }
    }
}
