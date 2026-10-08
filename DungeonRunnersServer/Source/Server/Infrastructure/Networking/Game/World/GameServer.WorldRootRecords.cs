using DungeonRunners.Gameplay;
using DungeonRunners.Utilities;

namespace DungeonRunners.Networking
{
    public partial class GameServer
    {
        private static string ResolveNpcEntityGcType(string gcType)
        {
            if (gcType.Contains("AdminWeaponVendor")) return "world.town.npc.VendorWeapon1";
            if (gcType.Contains("AdminArmorVendor")) return "world.town.npc.VendorWeapon2";
            if (gcType.Contains("AdminMiscVendor")) return "world.town.npc.VendorWeapon3";
            return gcType;
        }

        private static ClientEntityRootRecord BuildNpcRootRecord(
            ZoneNPC npc, ushort skillsId, ushort manipulatorsId, ushort modifiersId)
        {
            var root = new ClientEntityRootRecord((ushort)npc.Id);
            WriteGCType(root.Type, ResolveNpcEntityGcType(npc.GCClass), preserveCase: true);
            root.Initialize.WriteUInt32(0x06);
            root.Initialize.WriteInt32(npc.PosFixedX);
            root.Initialize.WriteInt32(npc.PosFixedY);
            root.Initialize.WriteInt32(npc.PosFixedZ);
            root.Initialize.WriteInt32(npc.HeadingFixed);
            root.Initialize.WriteByte(0x00);
            root.Initialize.WriteByte(0x01);
            for (int index = 0; index < 8; index++)
                root.Initialize.WriteUInt32(0);

            ClientEntityRootRecord.Component behavior = root.AddComponent((ushort)npc.UnitBehaviorId, true);
            WriteGCType(behavior.Type, "npc.base.behavior", preserveCase: false);
            behavior.Initialize.WriteByte(0xFF);
            behavior.Initialize.WriteByte(0x00);
            behavior.Initialize.WriteByte(0x00);
            behavior.Initialize.WriteByte(0x01);
            behavior.Initialize.WriteByte(0x85);
            behavior.Initialize.WriteByte(0x00);
            for (int index = 0; index < 5; index++)
                behavior.Initialize.WriteUInt32(0);
            behavior.Initialize.WriteByte(0x00);
            behavior.Initialize.WriteByte(0xFF);
            behavior.Initialize.WriteByte(0x00);
            behavior.Initialize.WriteByte(0x00);
            behavior.Initialize.WriteByte(0x00);
            behavior.Initialize.WriteByte(0x00);
            behavior.Initialize.WriteUInt32(0);
            behavior.Initialize.WriteUInt32(0);

            ClientEntityRootRecord.Component skills = root.AddComponent(skillsId, true);
            WriteGCType(skills.Type, "skills", preserveCase: false);
            skills.Initialize.WriteUInt32(uint.MaxValue);
            skills.Initialize.WriteByte(0x00);
            skills.Initialize.WriteByte(0x01);
            WriteGCType(skills.Initialize, "skills.professions.Warrior", preserveCase: true);

            ClientEntityRootRecord.Component manipulators = root.AddComponent(manipulatorsId, true);
            WriteGCType(manipulators.Type, "manipulators", preserveCase: false);
            manipulators.Initialize.WriteByte(0x00);

            ClientEntityRootRecord.Component modifiers = root.AddComponent(modifiersId, true);
            WriteGCType(modifiers.Type, "modifiers", preserveCase: false);
            modifiers.Initialize.WriteUInt32(0);
            modifiers.Initialize.WriteByte(0x00);
            modifiers.Initialize.WriteUInt32(0);

            if (npc.IsMerchant)
                MerchantRuntime.AddMerchantComponent(root, npc.GCClass, (ushort)npc.MerchantId);
            if (npc.IsTrainer)
            {
                int separator = npc.GCClass.LastIndexOf('.');
                string type = npc.GCClass.Substring(0, separator) + ".base."
                    + npc.GCClass.Substring(separator + 1) + "Base.SkillTrainer";
                ClientEntityRootRecord.Component trainer = root.AddComponent((ushort)npc.TrainerId, false);
                WriteGCType(trainer.Type, type, preserveCase: true);
            }
            if (npc.IsBank)
            {
                ClientEntityRootRecord.Component banker = root.AddComponent((ushort)npc.BankComponentId, false);
                WriteGCType(banker.Type, "banker", preserveCase: false);
            }
            if (npc.IsPosseMagnate)
            {
                ClientEntityRootRecord.Component posse = root.AddComponent((ushort)npc.PosseOptionComponentId, false);
                WriteGCType(posse.Type, "PosseRegistry", preserveCase: false);
            }
            return root;
        }

        private static void WriteNpcPlacementUpdate(LEWriter writer, ZoneNPC npc, uint hitPointsWire)
        {
            writer.WriteByte(0x35);
            writer.WriteUInt16((ushort)npc.UnitBehaviorId);
            writer.WriteByte(0x04);
            writer.WriteByte(0x11);
            writer.WriteByte(0x00);
            writer.WriteInt32(npc.PosFixedX);
            writer.WriteInt32(npc.PosFixedY);
            writer.WriteInt32(npc.PosFixedZ);
            writer.WriteByte(0x02);
            writer.WriteUInt32(hitPointsWire);
        }

        private static ClientEntityRootRecord BuildZonePortalRootRecord(ushort id, ZonePortal portal)
        {
            var root = new ClientEntityRootRecord(id);
            WriteGCType(root.Type, portal.GCType, preserveCase: true);
            root.Initialize.WriteUInt32(0x06);
            root.Initialize.WriteInt32(portal.PosFixedX);
            root.Initialize.WriteInt32(portal.PosFixedY);
            root.Initialize.WriteInt32(portal.PosFixedZ);
            root.Initialize.WriteInt32(portal.HeadingFixed);
            root.Initialize.WriteByte(0x00);
            root.Initialize.WriteCString(portal.SpawnPoint ?? "");
            root.Initialize.WriteCString(portal.TargetZone ?? "");
            root.Initialize.WriteUInt16((ushort)portal.Width);
            root.Initialize.WriteUInt16((ushort)portal.Height);
            root.Initialize.WriteUInt32(portal.Color);
            return root;
        }

        private static ClientEntityRootRecord BuildCheckpointRootRecord(ushort id, ZoneCheckpoint checkpoint)
        {
            var root = new ClientEntityRootRecord(id);
            WriteGCType(root.Type, checkpoint.GCType, preserveCase: true);
            root.Initialize.WriteUInt32(0x06);
            root.Initialize.WriteInt32(checkpoint.PosFixedX);
            root.Initialize.WriteInt32(checkpoint.PosFixedY);
            root.Initialize.WriteInt32(checkpoint.PosFixedZ);
            root.Initialize.WriteInt32(checkpoint.HeadingFixed);
            root.Initialize.WriteByte(0x00);
            return root;
        }

        private static ClientEntityRootRecord BuildReturnTownPortalRootRecord(ushort id, RRConnection conn)
        {
            var root = new ClientEntityRootRecord(id);
            root.Type.WriteByte(0xFF);
            root.Type.WriteCString("items.townportal.TownPortalBlue");
            root.Initialize.WriteUInt32(0x04);
            root.Initialize.WriteInt32(conn.TownPortalPosFixedX);
            root.Initialize.WriteInt32(conn.TownPortalPosFixedY);
            root.Initialize.WriteInt32(conn.TownPortalPosFixedZ);
            root.Initialize.WriteInt32(0);
            root.Initialize.WriteByte(0x01);
            root.Initialize.WriteUInt16((ushort)(conn.Avatar?.Id ?? 0));
            root.Initialize.WriteCString(conn.TownPortalTargetZone);
            root.Initialize.WriteCString("");
            root.Initialize.WriteByte(0x01);
            root.Initialize.WriteUInt32(0x00);
            root.Initialize.WriteUInt32(conn.TownPortalZoneId);
            return root;
        }

        private static ClientEntityRootRecord BuildPvpControllerRootRecord(ushort id, PVPMatchmaking.Match match)
        {
            var root = new ClientEntityRootRecord(id);
            WriteGCType(root.Type, "PVPMatchController", preserveCase: true);
            root.Initialize.WriteUInt32(0);
            root.Initialize.WriteByte(0x00);
            root.Initialize.WriteByte(0x00);
            if (match != null && match.Archetype == PVPMatchmaking.Archetype.GroupPracticeMatch)
            {
                root.Initialize.WriteByte(0x01);
                WritePVPTeamScore(root.Initialize, "pvp.FFATeam");
            }
            else
            {
                string teamList = match != null && match.Archetype == PVPMatchmaking.Archetype.GroupDuelMatch
                    ? "pvp.DuelTeamList" : "pvp.DefaultTeamList";
                root.Initialize.WriteByte(0x02);
                WritePVPTeamScore(root.Initialize, teamList + ".RedTeam");
                WritePVPTeamScore(root.Initialize, teamList + ".BlueTeam");
            }
            return root;
        }
    }
}
