using System;
using System.IO;
using DungeonRunners.Engine;
using DungeonRunners.Utilities;
using DungeonRunners.Data;
using DungeonRunners.Combat;
using DungeonRunners.Gameplay;
using DungeonRunners.Database;
using DungeonRunners.Core;
using System.Linq;
using System.Collections.Generic;


namespace DungeonRunners.Networking
{
    public class ChatCommands
    {
        private GameServer _server;
        private Dictionary<string, float> _levelCooldown = new Dictionary<string, float>();

        public ChatCommands(GameServer server)
        {
            _server = server;
        }

        private int ParseQty(string command)
        {
            string[] parts = command.Split(' ');
            if (parts.Length >= 2 && int.TryParse(parts[1], out int qty))
                return Math.Max(1, Math.Min(qty, 99));
            return 1;
        }

        public bool TryExecute(RRConnection conn, byte[] data, Action<RRConnection, string> sendMessage)
        {
            if (data == null || data.Length < 1) return false;

            var chatReader = new LEReader(data);
            string message = chatReader.ReadCString();

            if (!message.StartsWith("@")) return false;

            string command = message.Substring(1).Trim().ToLower();

            if (command == "duel" || command.StartsWith("duel "))
            {
                string duelArgs = command.Length > 4 ? command.Substring(4).Trim() : "";
                return ExecuteDuelCommand(conn, duelArgs, sendMessage);
            }

            if (!_server.IsPlayerAdmin(conn.LoginName))
            {
                sendMessage(conn, "You do not have permission to use this command.");
                return true;
            }

            if (command.StartsWith("pvp"))
            {
                string args = command.Length > 3 ? command.Substring(3).Trim() : "";
                return ExecutePvpCommand(conn, args, sendMessage);
            }

            if (command.StartsWith("group"))
            {
                string args = command.Length > 5 ? command.Substring(5).Trim() : "";
                return ExecuteGroupCommand(conn, args, sendMessage);
            }

            if (command.StartsWith("g "))
            {
                string args = command.Substring(2).Trim();
                return ExecuteGroupCommand(conn, args, sendMessage);
            }

            if (command.StartsWith("kick "))
            {
                string targetName = command.Substring(5).Trim();
                return ExecuteGroupCommand(conn, "kick " + targetName, sendMessage);
            }

            if (command.StartsWith("promote "))
            {
                string targetName = command.Substring(8).Trim();
                return ExecuteGroupCommand(conn, "leader " + targetName, sendMessage);
            }

            if (command.StartsWith("duelarena "))
            {
                string targetName = command.Substring(10).Trim();
                return ExecuteDuelArena(conn, "PVPGroupDuelMatch", targetName, sendMessage);
            }
            if (command.StartsWith("duelpractice "))
            {
                string targetName = command.Substring(13).Trim();
                return ExecuteDuelArena(conn, "PVPGroupPracticeMatch", targetName, sendMessage);
            }
            if (command.StartsWith("duelzone "))
            {
                string rest = command.Substring(9).Trim();
                int sp = rest.IndexOf(' ');
                if (sp <= 0) { sendMessage(conn, "[DUELZONE] usage=@duelzone <zoneName> <playerName>"); return true; }
                string zone = rest.Substring(0, sp).Trim();
                string targetName = rest.Substring(sp + 1).Trim();
                return ExecuteDuelArena(conn, zone, targetName, sendMessage);
            }

            if (command.StartsWith("z "))
            {
                string zoneName = command.Substring(2).Trim();
                Debug.LogError($"[CHAT-CMD] @z zone='{zoneName}'");
                sendMessage(conn, $"[ZONE] changeTo={zoneName}");
                _server.ChatChangeZone(conn, zoneName);
                return true;
            }

            if (command.StartsWith("scroll") || command.StartsWith("portal"))
            {
                int qty = ParseQty(command);
                for (int itemIndex = 0; itemIndex < qty; itemIndex++)
                    GiveConsumableItem(conn, "items.consumables.Consumable_TownPortal", "Town Portal Scroll", sendMessage);
                return true;
            }

            if (command.StartsWith("hppot") || command.StartsWith("hpot"))
            {
                int qty = ParseQty(command);
                for (int itemIndex = 0; itemIndex < qty; itemIndex++)
                    GiveConsumableItem(conn, "items.consumables.Consumable_MajorHealthPotion", "Major Health Potion", sendMessage);
                return true;
            }

            if (command.StartsWith("hp") || command.StartsWith("health"))
            {
                int qty = ParseQty(command);
                for (int itemIndex = 0; itemIndex < qty; itemIndex++)
                    GiveConsumableItem(conn, "potionpal.healthpotion_noob", "Health Bottle", sendMessage);
                return true;
            }

            if (command.StartsWith("mpot") || command.StartsWith("manapot"))
            {
                int qty = ParseQty(command);
                for (int itemIndex = 0; itemIndex < qty; itemIndex++)
                    GiveConsumableItem(conn, "items.consumables.Consumable_MajorManaPotion", "Major Mana Potion", sendMessage);
                return true;
            }

            if (command.StartsWith("mp") || command.StartsWith("mana"))
            {
                int qty = ParseQty(command);
                for (int itemIndex = 0; itemIndex < qty; itemIndex++)
                    GiveConsumableItem(conn, "potionpal.manapotion_noob", "Mana Bottle", sendMessage);
                return true;
            }

            if (command.StartsWith("damage ") || command.StartsWith("dmg "))
            {
                string amountStr = command.StartsWith("damage ") ? command.Substring(7).Trim() : command.Substring(4).Trim();
                if (int.TryParse(amountStr, out int damageAmount) && damageAmount > 0)
                    ExecuteDamageCommand(conn, damageAmount, sendMessage);
                else
                    sendMessage(conn, "[DAMAGE] usage=@damage <amount>");
                return true;
            }

            if (command.StartsWith("heal "))
            {
                string amountStr = command.Substring(5).Trim();
                if (int.TryParse(amountStr, out int healAmount) && healAmount > 0)
                    ExecuteHealCommand(conn, healAmount, sendMessage);
                else
                    sendMessage(conn, "[HEAL] usage=@heal <amount>");
                return true;
            }

            if (command == "fullheal" || command == "fh")
            {
                ExecuteFullHealCommand(conn, sendMessage);
                return true;
            }


            if (command == "pup") { SpawnMob(conn, "creatures.forestcreatures.warg.basic.pup", sendMessage); return true; }
            if (command == "wolf") { SpawnMob(conn, "creatures.forestcreatures.warg.basic.grunt", sendMessage); return true; }
            if (command == "rat" || command == "ratling") { SpawnMob(conn, "creatures.whiskers.broodling.basic.grunt", sendMessage); return true; }
            if (command == "blade" || command == "blademaster") { SpawnMob(conn, "creatures.whiskers.blademaster.basic.grunt", sendMessage); return true; }
            if (command == "boss" || command == "rattletooth") { SpawnMob(conn, "creatures.whiskers.broodling.basic.champion", sendMessage); return true; }

            if (command == "barrel" || command.StartsWith("barrel "))
            {
                int barrelCount = 1;
                if (command.StartsWith("barrel "))
                {
                    string arg = command.Substring(7).Trim();
                    if (int.TryParse(arg, out int bc) && bc >= 1 && bc <= 10)
                        barrelCount = bc;
                }
                string barrelGcType = "terrain.misc.interactives.Breakiable_Barrel_01";
                uint avatarId = _server.GetPlayerAvatarId(conn.LoginName);
                var playerObject = CombatRuntime.Instance.GetPlayer(avatarId);
                int baseFixedX = playerObject?.PosFixedX ?? conn.PlayerPosFixedX;
                int baseFixedY = playerObject?.PosFixedY ?? conn.PlayerPosFixedY;
                int baseFixedZ = conn.PlayerPosFixedZ;
                int spawned = 0;
                for (int barrelIndex = 0; barrelIndex < barrelCount; barrelIndex++)
                {
                    int offsetFixedX = (50 + barrelIndex * 30) << 8;
                    string gcType = barrelGcType;
                    var monster = CombatRuntime.Instance.SpawnMonsterFixed(gcType, baseFixedX + offsetFixedX, baseFixedY, baseFixedZ, 0, GetZoneName(conn));
                    if (monster != null)
                    {
                        _server.SendMonsterToClient(conn, monster);
                        spawned++;
                    }
                }
                sendMessage(conn, $"[BARREL] spawned={spawned}/{barrelCount}");
                return true;
            }


            if (command.StartsWith("spawn "))
            {
                string gcType = command.Substring(6).Trim();
                SpawnMob(conn, gcType, sendMessage);
                return true;
            }

            if (command.StartsWith("spawngroup "))
            {
                var spawnGroupParts = command.Substring(11).Trim().Split(' ');
                if (spawnGroupParts.Length >= 3)
                {
                    string faction = spawnGroupParts[0];
                    int count = int.TryParse(spawnGroupParts[1], out int parsedCount) ? parsedCount : 3;
                    string tier = spawnGroupParts[2];
                    var monsters = CombatRuntime.Instance.SpawnFactionGroupFixed(faction, tier, conn.PlayerPosFixedX, conn.PlayerPosFixedY, conn.PlayerPosFixedZ, count, 10 << 8, GetZoneName(conn));
                    foreach (var monster in monsters)
                        _server.SendMonsterToClient(conn, monster);
                    sendMessage(conn, $"[SPAWN] count={monsters.Count} tier={tier} faction={faction}");
                }
                else
                    sendMessage(conn, "[SPAWN] usage=@spawngroup <faction> <count> <tier>");
                return true;
            }

            if (command == "creatures" || command == "factions")
            {
                var factions = AuthoredGameplayCatalog.Creatures.Select(cr => cr.faction).Distinct().Take(10).ToList();
                sendMessage(conn, "[FACTIONS] " + string.Join(", ", factions));
                return true;
            }

            if (command.StartsWith("listmobs "))
            {
                string faction = command.Substring(9).Trim();
                var mobs = AuthoredGameplayCatalog.GetCreaturesByFaction(faction).Take(8).ToList();
                foreach (var cr in mobs)
                    sendMessage(conn, $"  {cr.gcType}");
                return true;
            }

            if (command == "killall")
            {
                CombatRuntime.Instance.ClearAll();
                sendMessage(conn, "[COMBAT] monstersCleared=true");
                return true;
            }


            if (command == "zones" || command.StartsWith("zones "))
            {
                string filter = command.Length > 5 ? command.Substring(5).Trim() : "";
                var allZones = _server.GetZoneNames();
                List<string> matching;
                if (string.IsNullOrEmpty(filter))
                    matching = allZones;
                else
                    matching = allZones.Where(zn => zn.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

                if (matching.Count == 0)
                {
                    sendMessage(conn, $"[ZONES] state=noMatch filter='{filter}'");
                    return true;
                }
                sendMessage(conn, $"[ZONES] count={matching.Count}{(string.IsNullOrEmpty(filter) ? "" : $" filter='{filter}'")}:");
                for (int zoneIndex = 0; zoneIndex < matching.Count && zoneIndex < 100; zoneIndex += 5)
                {
                    var batch = matching.Skip(zoneIndex).Take(5);
                    sendMessage(conn, "  " + string.Join(", ", batch));
                }
                if (matching.Count > 100)
                    sendMessage(conn, $"  ... and {matching.Count - 100} more. Use @zones <filter>");
                return true;
            }


            if (command.StartsWith("items ") || command.StartsWith("search ") || command.StartsWith("find "))
            {
                string search = command.Substring(command.IndexOf(' ') + 1).Trim().ToLower();
                if (search.Length < 2)
                {
                    sendMessage(conn, "[ITEMS] state=searchTooShort min=2");
                    return true;
                }

                var results = AuthoredGameplayCatalog.Items
                    .Where(item => (item.gcType?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                        || (item.Label?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
                    .Select(item => GCObject.GetPacketGCClassFor(item.gcType))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(gcType => gcType, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (results.Count == 0)
                {
                    sendMessage(conn, $"[ITEMS] state=noMatch search='{search}'");
                    return true;
                }
                sendMessage(conn, $"[ITEMS] count={results.Count} search='{search}':");
                foreach (var r in results.Take(15))
                {
                    string[] parts = r.Split('.');
                    string display = parts.Length >= 2 ? parts[parts.Length - 2] + "." + parts[parts.Length - 1] : r;
                    sendMessage(conn, $"  {display}");
                }
                if (results.Count > 15)
                    sendMessage(conn, $"  ... {results.Count - 15} more. Narrow your search.");
                sendMessage(conn, "  Use @give <shortname> to add to inventory");
                return true;
            }


            if (command.StartsWith("give "))
            {
                string gcType = command.Substring(5).Trim();
                if (string.IsNullOrEmpty(gcType))
                {
                    sendMessage(conn, "[GIVE] usage=@give <gcType>");
                    return true;
                }
                GiveAnyItem(conn, gcType, sendMessage);
                return true;
            }


            if (command.StartsWith("level ") || command.StartsWith("setlevel "))
            {
                float now = DungeonRunners.Engine.Time.time;
                if (_levelCooldown.TryGetValue(conn.LoginName, out float lastTime) && now - lastTime < 3.0f)
                {
                    sendMessage(conn, "[LEVEL] state=cooldown");
                    return true;
                }
                _levelCooldown[conn.LoginName] = now;
                string numStr = command.Substring(command.IndexOf(' ') + 1).Trim();
                if (!int.TryParse(numStr, out int targetLevel) || targetLevel < 1 || targetLevel > 100)
                {
                    sendMessage(conn, "[LEVEL] usage=@level <1-100>");
                    return true;
                }
                var savedChar = _server.GetSavedCharacterForConn(conn)?.DeepClone();
                if (savedChar == null) { sendMessage(conn, "[LEVEL] state=missing target=character"); return true; }

                PlayerState lvlPs = _server.GetPlayerState(conn.ConnId.ToString());
                if (lvlPs == null) { sendMessage(conn, "[LEVEL] state=missing target=playerState"); return true; }

                int oldLevel = lvlPs.Level;

                byte oldPersistedLevel = savedChar.level;
                uint oldExperience = savedChar.experience;
                savedChar.level = SavedCharacterLevel.ResolvePersistedLevel(targetLevel);
                savedChar.experience = 0;
                if (!_server.TrySaveCharacterForConn(conn, savedChar, "chat-set-level"))
                {
                    savedChar.level = oldPersistedLevel;
                    savedChar.experience = oldExperience;
                    sendMessage(conn, "[LEVEL] state=save-failed");
                    return true;
                }
                lvlPs.InitializeStats(savedChar.className, targetLevel, preserveAttributeModifiers: true);
                lvlPs.Experience = 0;

                if (conn.Avatar != null)
                    _server.CalculateEquipmentBonuses(conn.ConnId.ToString(), conn.Avatar);
                lvlPs.RestoreToFull();

                if (targetLevel > oldLevel)
                {
                    for (int lv = oldLevel; lv < targetLevel; lv++)
                    {
                        uint threshold = PlayerState.GetClientThreshold(lv + 1);
                        uint packetXP = threshold * 256 / 5 + 100;
                        _server.SendAdminXPUpdate(conn, packetXP, (uint)lv);
                    }
                }
                else if (targetLevel < oldLevel)
                {
                    sendMessage(conn, $"[LEVEL] old={oldLevel} new={targetLevel} state=rezoneRequired");
                    return true;
                }

                _server.SendAdminEntitySynchInfoHP(conn, lvlPs);

                sendMessage(conn, $"[LEVEL] old={oldLevel} new={targetLevel}");
                return true;
            }


            if (command.StartsWith("gold ") || command.StartsWith("addgold "))
            {
                string numStr = command.Substring(command.IndexOf(' ') + 1).Trim();
                if (!int.TryParse(numStr, out int amount)) { sendMessage(conn, "[GOLD] usage=@gold <amount>"); return true; }

                var savedChar = _server.GetSavedCharacterForConn(conn)?.DeepClone();
                if (savedChar == null) { sendMessage(conn, "[GOLD] state=missing target=character"); return true; }

                uint oldGold = savedChar.gold;
                long nextGold = (long)savedChar.gold + amount;
                savedChar.gold = (uint)Math.Clamp(nextGold, 0L, int.MaxValue);
                if (!_server.TrySaveCharacterForConn(conn, savedChar, "chat-gold"))
                {
                    savedChar.gold = oldGold;
                    sendMessage(conn, "[GOLD] state=save-failed");
                    return true;
                }
                sendMessage(conn, $"[GOLD] delta={(amount >= 0 ? "+" : "")}{amount:N0} total={savedChar.gold:N0}");
                return true;
            }


            if (command.StartsWith("xp ") || command.StartsWith("addxp ") || command.StartsWith("exp "))
            {
                string numStr = command.Substring(command.IndexOf(' ') + 1).Trim();
                if (!uint.TryParse(numStr, out uint amount)) { sendMessage(conn, "[XP] usage=@xp <amount>"); return true; }

                var savedChar = _server.GetSavedCharacterForConn(conn)?.DeepClone();
                if (savedChar == null) { sendMessage(conn, "[XP] state=missing target=character"); return true; }

                if (amount > int.MaxValue || savedChar.experience > (uint)int.MaxValue - amount)
                {
                    sendMessage(conn, "[XP] state=overflow");
                    return true;
                }
                uint oldExperience = savedChar.experience;
                savedChar.experience += amount;
                if (!_server.TrySaveCharacterForConn(conn, savedChar, "chat-xp"))
                {
                    savedChar.experience = oldExperience;
                    sendMessage(conn, "[XP] state=save-failed");
                    return true;
                }
                sendMessage(conn, $"[XP] +{amount:N0} -> Total: {savedChar.experience:N0}");
                return true;
            }


            if (command == "stats" || command == "info" || command == "me")
            {
                var savedChar = _server.GetSavedCharacterForConn(conn)?.DeepClone();
                PlayerState playerState = _server.GetPlayerState(conn.ConnId.ToString());

                sendMessage(conn, $"[PLAYER] name={conn.LoginName}");
                if (savedChar != null)
                {
                    sendMessage(conn, $"  {savedChar.name} | {savedChar.className} Lv{savedChar.level}");
                    sendMessage(conn, $"  Gold: {savedChar.gold:N0} | XP: {savedChar.experience:N0}");
                }
                if (playerState != null)
                    sendMessage(conn, $"  HP: {playerState.CurrentHPWire / 256}/{playerState.MaxHPWire / 256} | Mana: {playerState.CurrentManaWire / 256}/{playerState.MaxManaWire / 256}");
                sendMessage(conn, $"  Zone: {conn.CurrentZoneName} | PosFixed: ({conn.PlayerPosFixedX}, {conn.PlayerPosFixedY})");
                return true;
            }


            if (command == "pos" || command == "loc" || command == "location")
            {
                sendMessage(conn, $"[POS] zone={conn.CurrentZoneName} posFixed=({conn.PlayerPosFixedX},{conn.PlayerPosFixedY},{conn.PlayerPosFixedZ})");
                return true;
            }


            if (command == "who" || command == "online" || command == "players")
            {
                var connections = _server.GetConnections();
                var online = connections.Values.Where(onlineConnection => onlineConnection.IsConnected && !string.IsNullOrEmpty(onlineConnection.LoginName)).ToList();
                sendMessage(conn, $"[ONLINE] count={online.Count}:");
                foreach (var onlineConnection in online)
                    sendMessage(conn, $"  {onlineConnection.LoginName} - {onlineConnection.CurrentZoneName ?? "???"} fixed=({onlineConnection.PlayerPosFixedX}, {onlineConnection.PlayerPosFixedY})");
                return true;
            }


            if (command.StartsWith("goto ") || command.StartsWith("tp "))
            {
                string target = command.Substring(command.IndexOf(' ') + 1).Trim();
                var targetConn = _server.FindConnectionByName(target);
                if (targetConn != null && !string.IsNullOrEmpty(targetConn.CurrentZoneName))
                {
                    sendMessage(conn, $"[TP] player={targetConn.LoginName} zone={targetConn.CurrentZoneName}");
                    _server.ChatChangeZone(conn, targetConn.CurrentZoneName);
                }
                else
                {
                    sendMessage(conn, $"[TP] zone={target}");
                    _server.ChatChangeZone(conn, target);
                }
                return true;
            }


            if (command == "shop close" || command == "shop off" || command == "store close")
            {
                _server.DespawnAdminShop(conn, sendMessage);
                return true;
            }

            if (command == "shop" || command == "store" || command == "merchant")
            {
                _server.SpawnAdminShop(conn, sendMessage);
                return true;
            }


            if (command.StartsWith("set "))
            {
                string[] setParts = message.Substring(5).Trim().Split(new[] { ' ' }, 2);
                if (setParts.Length < 2)
                {
                    sendMessage(conn, "[CONFIG] usage=@set <key> <value>");
                    return true;
                }
                string setKey = setParts[0].Trim();
                string setValue = setParts[1].Trim();
                if (!ServerSettings.IsRuntimeMutableKey(setKey))
                {
                    sendMessage(conn, $"[CONFIG] key='{setKey}' state=clientAuthoritative action=ignored");
                    return true;
                }
                string oldValue = ServerSettings.GetString(setKey, "(not set)");
                if (ServerSettings.Set(setKey, setValue))
                    sendMessage(conn, $"[CONFIG] key={setKey} old='{oldValue}' new='{setValue}' source=db");
                else
                    sendMessage(conn, $"[CONFIG] key='{setKey}' state=unchanged");
                return true;
            }

            if (command.StartsWith("get "))
            {
                string getKey = command.Substring(4).Trim();
                if (!ServerSettings.IsRuntimeMutableKey(getKey))
                {
                    sendMessage(conn, $"[CONFIG] key='{getKey}' state=clientAuthoritative action=ignored");
                    return true;
                }
                var all = ServerSettings.GetAll();
                if (all.TryGetValue(getKey, out var entry))
                    sendMessage(conn, $"[CONFIG] key={getKey} value='{entry.value}' source={entry.source}");
                else
                    sendMessage(conn, $"[CONFIG] key='{getKey}' state=notSet");
                return true;
            }

            if (command == "reload")
            {
                ServerSettings.Reload();
                DRLog.InitFromConfig();
                sendMessage(conn, "[CONFIG] action=reload state=done");
                return true;
            }


            if (command.StartsWith("setfree"))
            {
                string targetName = command.Length > 8 ? command.Substring(8).Trim() : conn.LoginName;
                if (string.IsNullOrEmpty(targetName)) targetName = conn.LoginName;
                _server.SetPlayerMembershipPublic(targetName, true);
                sendMessage(conn, $"[MEMBERSHIP] player={targetName} mode=FREE_PLAYER action=relogRequired");
                return true;
            }

            if (command.StartsWith("setmember"))
            {
                string targetName = command.Length > 10 ? command.Substring(10).Trim() : conn.LoginName;
                if (string.IsNullOrEmpty(targetName)) targetName = conn.LoginName;
                _server.SetPlayerMembershipPublic(targetName, false);
                sendMessage(conn, $"[MEMBERSHIP] player={targetName} mode=MEMBER action=relogRequired");
                return true;
            }

            if (command == "membership" || command == "member")
            {
                bool isFree = _server.IsPlayerFreePublic(conn.LoginName);
                string mode = isFree ? "FREE PLAYER" : "MEMBER";
                float xpMult = GCDatabase.Instance.GetRequiredKnobFixed32("FreePlayerExperienceMult") / 256f;
                float expMod = GCDatabase.Instance.GetRequiredKnobFixed32("ExperienceMod") / 256f;
                sendMessage(conn, $"[MEMBERSHIP] mode={mode}");
                if (isFree)
                    sendMessage(conn, $"  XP penalty: x{xpMult} | Shadow items: ON | Item restrictions: ON");
                else
                    sendMessage(conn, $"  Full XP | All items usable | No restrictions");
                sendMessage(conn, $"  Base ExperienceMod: {expMod}");
                return true;
            }

            if (command == "members" || command == "memberlist")
            {
                var members = _server.GetAllMembershipsPublic();
                sendMessage(conn, $"[MEMBERSHIP] count={members.Count}:");
                foreach (var membershipEntry in members)
                    sendMessage(conn, $"  {membershipEntry.Key} = {(membershipEntry.Value ? "FREE" : "MEMBER")}");
                return true;
            }


            if (command.StartsWith("ban "))
            {
                string targetName = command.Substring(4).Trim();
                if (string.IsNullOrEmpty(targetName)) { sendMessage(conn, "[ADMIN] usage=@ban <player>"); return true; }
                _server.SetPlayerBannedPublic(targetName, true);
                sendMessage(conn, $"[ADMIN] player={targetName} banned=true");
                return true;
            }

            if (command.StartsWith("unban "))
            {
                string targetName = command.Substring(6).Trim();
                if (string.IsNullOrEmpty(targetName)) { sendMessage(conn, "[ADMIN] usage=@unban <player>"); return true; }
                _server.SetPlayerBannedPublic(targetName, false);
                sendMessage(conn, $"[ADMIN] player={targetName} banned=false");
                return true;
            }

            if (command.StartsWith("setadmin "))
            {
                string targetName = command.Substring(9).Trim();
                if (string.IsNullOrEmpty(targetName)) { sendMessage(conn, "[ADMIN] usage=@setadmin <player>"); return true; }
                _server.SetPlayerAdminPublic(targetName, true);
                sendMessage(conn, $"[ADMIN] player={targetName} admin=true action=relogRequired");
                return true;
            }

            if (command.StartsWith("removeadmin "))
            {
                string targetName = command.Substring(12).Trim();
                if (string.IsNullOrEmpty(targetName)) { sendMessage(conn, "[ADMIN] usage=@removeadmin <player>"); return true; }
                _server.SetPlayerAdminPublic(targetName, false);
                sendMessage(conn, $"[ADMIN] player={targetName} admin=false action=relogRequired");
                return true;
            }

            if (command == "config" || command == "settings" || command == "cfg")
            {
                var all = ServerSettings.GetAll()
                    .Where(settingEntry => ServerSettings.IsRuntimeMutableKey(settingEntry.Key))
                    .ToList();
                int totalSettings = all.Count;
                sendMessage(conn, $"[CONFIG] count={totalSettings}:");
                int shown = 0;
                foreach (var settingEntry in all)
                {
                    string src = settingEntry.Value.source == "db" ? " [DB]" : "";
                    sendMessage(conn, $"  {settingEntry.Key} = {settingEntry.Value.value}{src}");
                    shown++;
                    if (shown >= 30)
                    {
                        sendMessage(conn, $"  ... and {totalSettings - 30} more. Use @get <key>");
                        break;
                    }
                }
                return true;
            }

            if (command.StartsWith("unset ") || command.StartsWith("reset "))
            {
                string unsetKey = command.Substring(command.IndexOf(' ') + 1).Trim();
                ServerSettings.Remove(unsetKey);
                if (!ServerSettings.IsRuntimeMutableKey(unsetKey))
                {
                    sendMessage(conn, $"[CONFIG] key='{unsetKey}' state=clientAuthoritative action=removedIgnoredDbOverride");
                    return true;
                }
                string cfgVal = ServerSettings.GetString(unsetKey, "(default)");
                sendMessage(conn, $"[CONFIG] key='{unsetKey}' action=removedDbOverride value='{cfgVal}'");
                return true;
            }


            if (command.StartsWith("announce ") || command.StartsWith("ann "))
            {
                string announcementMessage = message.Substring(message.IndexOf(' ') + 1).Trim();
                string color = ServerSettings.GetString("announceColor", "#FF4444");
                string effect = ServerSettings.GetString("announceEffect", "glow");
                string formatted = GameServer.WrapChatColor($"[ANNOUNCE] {announcementMessage}", color, effect);
                foreach (var connectionEntry in _server.GetConnections())
                {
                    var target = connectionEntry.Value;
                    if (target.IsConnected && !string.IsNullOrEmpty(target.LoginName))
                        sendMessage(target, formatted);
                }
                Debug.LogError($"[ADMIN] {conn.LoginName} announced: {announcementMessage}");
                return true;
            }

            if (command == "motd")
            {
                string motd = ServerSettings.GetString("motd", "Welcome to Dungeon Runners!");
                string motdColor = ServerSettings.GetString("motdColor", "");
                sendMessage(conn, GameServer.WrapChatColor($"[MOTD] {motd}", motdColor, ""));
                return true;
            }

            if (command.StartsWith("motd "))
            {
                string newMotd = message.Substring(message.IndexOf(' ') + 1).Trim();
                ServerSettings.Set("motd", newMotd);
                sendMessage(conn, $"[MOTD] action=update message='{newMotd}'");
                return true;
            }

            if (command.StartsWith("kick "))
            {
                string targetName = command.Substring(5).Trim();
                var target = _server.FindConnectionByName(targetName);
                if (target == null)
                {
                    sendMessage(conn, $"[KICK] player='{targetName}' state=notFound");
                    return true;
                }
                sendMessage(target, "[SERVER] state=kicked");
                sendMessage(conn, $"[KICK] player={targetName} state=kicked");
                Debug.LogError($"[ADMIN] {conn.LoginName} kicked {targetName}");
                target.Disconnect();
                return true;
            }

            if (command == "uptime")
            {
                var uptime = System.TimeSpan.FromSeconds(DungeonRunners.Engine.Time.realtimeSinceStartup);
                string uptimeStr = $"{(int)uptime.TotalHours}h {uptime.Minutes}m {uptime.Seconds}s";
                var connections = _server.GetConnections();
                int online = 0;
                foreach (var connectionEntry in connections)
                    if (connectionEntry.Value.IsConnected && !string.IsNullOrEmpty(connectionEntry.Value.LoginName))
                        online++;
                sendMessage(conn, $"[SERVER] uptime='{uptimeStr}' online={online}/{QueueConnection.MaxPlayers}");
                return true;
            }

            if (command.StartsWith("broadcast ") || command.StartsWith("bc "))
            {
                string broadcastMessage = message.Substring(message.IndexOf(' ') + 1).Trim();
                foreach (var connectionEntry in _server.GetConnections())
                {
                    var target = connectionEntry.Value;
                    if (target.IsConnected && !string.IsNullOrEmpty(target.LoginName))
                        sendMessage(target, broadcastMessage);
                }
                return true;
            }

            if (command.StartsWith("say "))
            {
                string sayMessage = message.Substring(message.IndexOf(' ') + 1).Trim();
                foreach (var connectionEntry in _server.GetConnections())
                {
                    var target = connectionEntry.Value;
                    if (target.IsConnected && !string.IsNullOrEmpty(target.LoginName))
                        sendMessage(target, $"[SERVER] {sayMessage}");
                }
                return true;
            }

            if (command.StartsWith("color "))
            {
                string rest = message.Substring(message.IndexOf(' ') + 1).Trim();
                int spaceIdx = rest.IndexOf(' ');
                if (spaceIdx < 0)
                {
                    sendMessage(conn, "[COLOR] usage=@color #RRGGBB message text");
                    return true;
                }
                string colorValue = rest.Substring(0, spaceIdx).Trim();
                string colorText = rest.Substring(spaceIdx + 1).Trim();
                sendMessage(conn, GameServer.WrapChatColor(colorText, colorValue, ""));
                return true;
            }

            if (command.StartsWith("glow "))
            {
                string glowMessage = message.Substring(message.IndexOf(' ') + 1).Trim();
                string glowColor = ServerSettings.GetString("announceColor", "#FF4444");
                sendMessage(conn, GameServer.WrapChatColor(glowMessage, glowColor, "glow"));
                return true;
            }

            if (command == "colors")
            {
                sendMessage(conn, GameServer.WrapChatColor("Red", "#FF0000", ""));
                sendMessage(conn, GameServer.WrapChatColor("Green", "#00FF00", ""));
                sendMessage(conn, GameServer.WrapChatColor("Blue", "#6464FF", ""));
                sendMessage(conn, GameServer.WrapChatColor("Gold", "#FFCC66", ""));
                sendMessage(conn, GameServer.WrapChatColor("Cyan", "#99FFFF", ""));
                sendMessage(conn, GameServer.WrapChatColor("Pink", "#FF66CC", ""));
                sendMessage(conn, GameServer.WrapChatColor("Orange", "#FF6600", ""));
                sendMessage(conn, GameServer.WrapChatColor("Glow Red", "#FF4444", "glow"));
                sendMessage(conn, GameServer.WrapChatColor("Glow Gold", "#FFCC66", "glow"));
                sendMessage(conn, "[COLORS] usage=@color #RRGGBB text");
                return true;
            }


            if (command == "help" || command == "?")
            {
                sendMessage(conn, "[NAVIGATION]");
                sendMessage(conn, "@zones [filter] @z <zone> @tp <player|zone> @pos");
                sendMessage(conn, "[PLAYER]");
                sendMessage(conn, "@stats @level <n> @gold <n> @xp <n> @who");
                sendMessage(conn, "[HEALING]");
                sendMessage(conn, "@fh @heal <n> @dmg <n>");
                sendMessage(conn, "[ITEMS]");
                sendMessage(conn, "@hp [n] @hppot [n] @mp [n] @mpot [n] @scroll [n]");
                sendMessage(conn, "@items <search> @give <gcType>");
                sendMessage(conn, "@shop - spawn admin vendors | @shop close - despawn");
                sendMessage(conn, "[MONSTERS]");
                sendMessage(conn, "@pup @wolf @rat @blade @boss @barrel [n]");
                sendMessage(conn, "@spawn <gc> @spawngroup <faction> <n> <tier>");
                sendMessage(conn, "@creatures @listmobs <faction> @killall");
                sendMessage(conn, "[GROUP]");
                sendMessage(conn, "@group create|invite|accept|leave|kick|reset|seed");
                sendMessage(conn, "[CONFIG]");
                sendMessage(conn, "@config @get <key> @set <key> <val> @unset <key> @reload");
                sendMessage(conn, "[ADMIN]");
                sendMessage(conn, "@announce <msg> @motd [msg] @kick <player> @uptime");
                sendMessage(conn, "@broadcast <msg> @say <msg>");
                sendMessage(conn, "@colors @color #RRGGBB <text> @glow <text>");
                sendMessage(conn, "[MEMBERSHIP]");
                sendMessage(conn, "@membership @setfree [player] @setmember [player] @members");
                sendMessage(conn, "[ACCOUNT]");
                sendMessage(conn, "@ban <player> @unban <player> @setadmin <player> @removeadmin <player>");
                return true;
            }

            return false;
        }

        private void SpawnMob(RRConnection conn, string gcType, Action<RRConnection, string> sendMessage)
        {
            uint avatarId = _server.GetPlayerAvatarId(conn.LoginName);
            var combatPlayer = CombatRuntime.Instance.GetPlayer(avatarId);

            int spawnFixedX = (combatPlayer?.PosFixedX ?? conn.PlayerPosFixedX) + (70 << 8);
            int spawnFixedY = combatPlayer?.PosFixedY ?? conn.PlayerPosFixedY;
            int spawnFixedZ = conn.PlayerPosFixedZ;

            var monster = CombatRuntime.Instance.SpawnMonsterFixed(gcType, spawnFixedX, spawnFixedY, spawnFixedZ, 0, GetZoneName(conn));
            if (monster != null)
            {
                _server.SendMonsterToClient(conn, monster);
                sendMessage(conn, $"[SPAWN] name='{monster.Name}' hp={monster.MaxHP} damage={monster.BaseDamage}");
            }
            else
            {
                sendMessage(conn, $"[SPAWN] state=missing gcType={gcType}");
            }
        }

        private string GetZoneName(RRConnection conn)
        {
            string baseName = !string.IsNullOrEmpty(conn.CurrentZoneName)
                ? conn.CurrentZoneName
                : conn.CurrentZoneGcType?.Replace("world.", "") ?? "dungeon00_level01";

            string lower = baseName.ToLower();
            bool isPublic = lower.Contains("town") || lower.Contains("tutorial") ||
                            lower.Contains("dew") || lower.Contains("valley");
            if (isPublic) return baseName;

            return $"{baseName}_inst{conn.InstanceId}";
        }

        private void GiveAnyItem(RRConnection conn, string gcType, Action<RRConnection, string> sendMessage)
        {
            if (conn.UnitContainerId == 0)
            {
                sendMessage(conn, "[GIVE] state=missing unitContainerId");
                return;
            }

            var itemData = AuthoredGameplayCatalog.FindItem(gcType);
            var genItem = AuthoredGameplayCatalog.FindGeneralItem(gcType);
            bool validGcType = (itemData != null || genItem != null);

            if (!validGcType && !gcType.StartsWith("items."))
            {
                string prefixed = "items.pal." + gcType;
                var prefixedItem = AuthoredGameplayCatalog.FindItem(prefixed);
                var prefixedGen = AuthoredGameplayCatalog.FindGeneralItem(prefixed);
                if (prefixedItem != null || prefixedGen != null)
                {
                    gcType = prefixed;
                    itemData = prefixedItem;
                    genItem = prefixedGen;
                    validGcType = true;
                }
            }

            if (!validGcType)
            {
                string search = gcType.ToLower();
                var matches = AuthoredGameplayCatalog.Items
                    .Where(item => item.gcType?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                    .Select(item => GCObject.GetPacketGCClassFor(item.gcType))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var exactSuffix = matches.Where(m => {
                    string suffix = m.Contains(".") ? m.Substring(m.LastIndexOf('.') + 1).ToLower() : m.ToLower();
                    return suffix == search;
                }).ToList();

                var best = exactSuffix.Count > 0 ? exactSuffix : matches;

                if (best.Count == 1)
                {
                    gcType = best[0];
                    itemData = AuthoredGameplayCatalog.FindItem(gcType);
                    genItem = AuthoredGameplayCatalog.FindGeneralItem(gcType);
                    validGcType = true;
                    sendMessage(conn, $"[GIVE] gcType={gcType} state=found");
                }
                else if (best.Count > 1 && best.Count <= 10)
                {
                    sendMessage(conn, $"[GIVE] count={best.Count} search='{gcType}':");
                    foreach (var matchGcType in best)
                    {
                        string[] matchParts = matchGcType.Split('.'); string shortName = matchParts.Length >= 2 ? matchParts[matchParts.Length - 2] + "." + matchParts[matchParts.Length - 1] : matchGcType;
                        sendMessage(conn, $"  {shortName}");
                    }
                    return;
                }
                else if (best.Count > 10)
                {
                    sendMessage(conn, $"[GIVE] count={best.Count} state=ambiguous");
                    foreach (var matchGcType in best.Take(8))
                    {
                        string[] matchParts = matchGcType.Split('.'); string shortName = matchParts.Length >= 2 ? matchParts[matchParts.Length - 2] + "." + matchParts[matchParts.Length - 1] : matchGcType;
                        sendMessage(conn, $"  {shortName}");
                    }
                    return;
                }
                else
                {
                    sendMessage(conn, $"[GIVE] gcType='{gcType}' state=notFound action=@items <search>");
                    return;
                }
            }

            int itemWidth = genItem?.InventoryWidth ?? itemData?.inventoryWidth ?? 0;
            int itemHeight = genItem?.InventoryHeight ?? itemData?.inventoryHeight ?? 0;
            if (itemWidth <= 0 || itemHeight <= 0)
                throw new InvalidDataException($"Authored item has invalid dimensions gcType='{gcType}' width={itemWidth} height={itemHeight}");

            string connId = conn.ConnId.ToString();
            byte slotX = 0, slotY = 0;
            bool found = false;
            for (byte row = 0; row < 8 && !found; row++)
            {
                for (byte col = 0; col < 10 && !found; col++)
                {
                    if (!_server.IsInventorySlotOccupied(connId, col, row, itemWidth, itemHeight))
                    {
                        slotX = col;
                        slotY = row;
                        found = true;
                    }
                }
            }

            if (!found)
            {
                sendMessage(conn, "[GIVE] state=inventoryFull");
                return;
            }

            PlayerState playerState = _server.GetPlayerState(connId);
            uint slot = _server.GetNextInventorySlot(connId);

            string gcTypeToSend = GCObject.GetPacketGCClassFor(genItem?.gcType ?? itemData?.gcType ?? gcType);

            string packetGcType = gcTypeToSend;
            if (packetGcType.StartsWith("items.pal.", StringComparison.OrdinalIgnoreCase))
                packetGcType = packetGcType.Substring(10);
            packetGcType = packetGcType.ToLowerInvariant();

            int modSlots = itemData?.modCount ?? genItem?.modCount ?? 0;

            var giveItemMessage = new LEWriter();
            giveItemMessage.WriteByte(0x07);
            giveItemMessage.WriteByte(0x35);
            giveItemMessage.WriteUInt16(conn.UnitContainerId);
            giveItemMessage.WriteByte(0x1E);
            giveItemMessage.WriteByte(0x0B);
            var newItem = new GCObject
            {
                GCClass = gcType,
                DFCClass = "Item",
                StoredRarity = 3,
                StoredLevel = 0x37,
                HasGeneratedItemState = true,
                GeneratedItemModifiers = new List<string> { "ScaleModPAL.Rare.Mod1" }
            };
            newItem.WriteInitForInventory(giveItemMessage, slotX, slotY, slot, newItem.StoredLevel);
            _server.WritePlayerEntitySynch(conn, giveItemMessage);
            giveItemMessage.WriteByte(0x06);

            byte[] givePacket = giveItemMessage.ToArray();
            Debug.LogError($"[GIVE] gcType={packetGcType}, modSlots={modSlots}, slot={slot}, pos=({slotX},{slotY}), packetBytes={givePacket.Length}");
            Debug.LogError($"[GIVE] packet={BitConverter.ToString(givePacket)}");
            _server.SendToClient(conn, givePacket);

            _server.TrackInventoryItem(connId, slot, newItem, slotX, slotY);
            _server.OccupyInventorySlots(connId, slotX, slotY, itemWidth, itemHeight);
            _server.SetStackCount(connId, slot, 1);

            string itemLabel = genItem?.Label ?? itemData?.name ?? gcType;
            sendMessage(conn, $"[GIVE] item='{itemLabel}' size={itemWidth}x{itemHeight}");
        }

        private void ExecuteDamageCommand(RRConnection conn, int damageAmount, Action<RRConnection, string> sendMessage)
        {
            string connId = conn.ConnId.ToString();
            PlayerState playerState = _server.GetPlayerState(connId);
            uint maxHP = playerState.MaxHPWire;
            uint damageWire = (uint)(damageAmount * 256);
            playerState.TakeDamage(damageWire);
            uint newHP = playerState.CurrentHPWire;
            sendMessage(conn, $"[DAMAGE] amount={damageAmount} hp={newHP / 256}/{maxHP / 256}");
        }

        private void ExecuteHealCommand(RRConnection conn, int healAmount, Action<RRConnection, string> sendMessage)
        {
            string connId = conn.ConnId.ToString();
            PlayerState playerState = _server.GetPlayerState(connId);
            uint maxHP = playerState.MaxHPWire;
            uint healWire = (uint)(healAmount * 256);
            playerState.Heal(healWire);
            uint newHP = playerState.CurrentHPWire;
            sendMessage(conn, $"[HEAL] amount={healAmount} hp={newHP / 256}/{maxHP / 256}");
        }

        private void ExecuteFullHealCommand(RRConnection conn, Action<RRConnection, string> sendMessage)
        {
            string connId = conn.ConnId.ToString();
            PlayerState playerState = _server.GetPlayerState(connId);
            playerState.RestoreToFull();
            uint maxHP = playerState.MaxHPWire;
            sendMessage(conn, $"[FULL-HEAL] hp={maxHP / 256}/{maxHP / 256}");
        }

        private void GiveConsumableItem(RRConnection conn, string gcClass, string itemName, Action<RRConnection, string> sendMessage)
        {
            if (conn.UnitContainerId == 0)
            {
                sendMessage(conn, "[ITEM] state=missing unitContainerId");
                return;
            }

            string connId = conn.ConnId.ToString();
            PlayerState playerState = _server.GetPlayerState(connId);

            var existing = _server.FindInventoryItemByGCClass(connId, gcClass);
            if (existing != null)
            {
                uint oldSlot = existing.Value.slot;
                byte oldX = existing.Value.x;
                byte oldY = existing.Value.y;
                int oldCount = _server.GetStackCount(connId, oldSlot);
                int newCount = Math.Min(oldCount + 1, 99);

                var writer = new LEWriter();
                writer.WriteByte(0x07);
                writer.WriteByte(0x35);
                writer.WriteUInt16(conn.UnitContainerId);
                writer.WriteByte(0x1F);
                writer.WriteUInt32(oldSlot);
                _server.WritePlayerEntitySynch(conn, writer);

                uint newSlot = _server.GetNextInventorySlot(connId);
                writer.WriteByte(0x35);
                writer.WriteUInt16(conn.UnitContainerId);
                writer.WriteByte(0x1E);
                writer.WriteByte(0x0B);
                GCObject item = existing.Value.item;
                int itemLevel = item.StoredLevel >= 0 ? item.StoredLevel : Math.Max(1, item.GetItemRequiredLevel());
                item.WriteInitForInventory(writer, oldX, oldY, newSlot, itemLevel, (byte)Math.Min(newCount, byte.MaxValue));
                _server.WritePlayerEntitySynch(conn, writer);

                writer.WriteByte(0x06);
                _server.SendToClient(conn, writer.ToArray());

                _server.RemoveInventoryItemBySlot(connId, oldSlot);
                _server.TrackInventoryItem(connId, newSlot, item, oldX, oldY);
                _server.SetStackCount(connId, newSlot, newCount);

                sendMessage(conn, $"[ITEM] name='{itemName}' count={newCount}");
                return;
            }

            byte slotX = 0, slotY = 0;
            bool found = false;
            for (byte row = 0; row < 8 && !found; row++)
            {
                for (byte col = 0; col < 10 && !found; col++)
                {
                    if (!_server.IsInventorySlotOccupied(connId, col, row, 1, 1))
                    {
                        slotX = col;
                        slotY = row;
                        found = true;
                    }
                }
            }

            if (!found)
            {
                sendMessage(conn, "[ITEM] state=inventoryFull");
                return;
            }

            uint equipSlot = _server.GetNextInventorySlot(connId);

            var giveNamedItemMessage = new LEWriter();
            giveNamedItemMessage.WriteByte(0x07);
            giveNamedItemMessage.WriteByte(0x35);
            giveNamedItemMessage.WriteUInt16(conn.UnitContainerId);
            giveNamedItemMessage.WriteByte(0x1E);
            giveNamedItemMessage.WriteByte(0x0B);
            var newItem = new GCObject
            {
                GCClass = gcClass,
                DFCClass = "Item",
                StoredLevel = Math.Max(1, ItemData.GetRequiredLevelFromGCClass(gcClass))
            };
            newItem.WriteInitForInventory(giveNamedItemMessage, slotX, slotY, equipSlot, newItem.StoredLevel);
            _server.WritePlayerEntitySynch(conn, giveNamedItemMessage);
            giveNamedItemMessage.WriteByte(0x06);

            _server.SendToClient(conn, giveNamedItemMessage.ToArray());

            _server.TrackInventoryItem(connId, equipSlot, newItem, slotX, slotY);
            _server.OccupyInventorySlots(connId, slotX, slotY, 1, 1);
            _server.SetStackCount(connId, equipSlot, 1);

            sendMessage(conn, $"[ITEM] name='{itemName}' state=added");
        }

        private bool ExecutePvpCommand(RRConnection conn, string args, Action<RRConnection, string> sendMessage)
        {
            args = (args ?? "").Trim();
            if (args.Length == 0)
            {
                sendMessage(conn, "[PVP] usage=@pvp queue <duel|practice|dm|dmunrated> | cancel | status | leave");
                return true;
            }

            string[] parts = args.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            string sub = parts[0].ToLowerInvariant();

            if (sub == "queue" || sub == "q")
            {
                if (parts.Length < 2)
                {
                    sendMessage(conn, "[PVP] Specify archetype: duel | practice | dm | dmunrated");
                    return true;
                }
                string atypeStr = parts[1].Trim().ToLowerInvariant();
                Gameplay.PVPMatchmaking.Archetype atype;
                switch (atypeStr)
                {
                    case "duel": atype = Gameplay.PVPMatchmaking.Archetype.GroupDuelMatch; break;
                    case "practice": atype = Gameplay.PVPMatchmaking.Archetype.GroupPracticeMatch; break;
                    case "dm": atype = Gameplay.PVPMatchmaking.Archetype.GroupDeathMatch; break;
                    case "dmunrated":
                    case "dmcasual": atype = Gameplay.PVPMatchmaking.Archetype.GroupDeathMatchUnrated; break;
                    default:
                        sendMessage(conn, $"[PVP] Unknown archetype '{atypeStr}'. Use: duel | practice | dm | dmunrated");
                        return true;
                }

                int rating = 1500;
                try
                {
                    var ch = _server.GetSavedCharacterForConn(conn);
                    if (ch != null && ch.pvpRating > 0) rating = ch.pvpRating;
                }
                catch { }

                bool ok = Gameplay.PVPMatchmaking.Instance.EnqueuePlayer(conn.LoginName, conn.CharSqlId, atype, rating, "solo:" + conn.LoginName, SimulationClock.SimulationTick);
                sendMessage(conn, ok
                    ? $"[PVP] Queued for {atype} (rating {rating}). Need {Gameplay.PVPMatchmaking.MinGroupCount(atype)} teams."
                    : "[PVP] Could not queue (already queued or in match).");
                return true;
            }

            if (sub == "cancel" || sub == "c")
            {
                bool removed = Gameplay.PVPMatchmaking.Instance.DequeuePlayer(conn.LoginName);
                sendMessage(conn, removed ? "[PVP] Removed from queue." : "[PVP] You weren't queued.");
                return true;
            }

            if (sub == "leave")
            {
                Gameplay.PVPMatchmaking.Instance.DequeuePlayer(conn.LoginName);
                var match = Gameplay.PVPMatchmaking.Instance.HandleDisconnect(conn.LoginName, SimulationClock.SimulationTick);
                sendMessage(conn, match != null
                    ? $"[PVP] Forfeited match {match.MatchId}."
                    : "[PVP] Not in a match.");
                return true;
            }

            if (sub == "status" || sub == "s")
            {
                var state = Gameplay.PVPMatchmaking.Instance.GetState(conn.LoginName);
                var match = Gameplay.PVPMatchmaking.Instance.GetMatchForPlayer(conn.LoginName);
                sendMessage(conn, $"[PVP] State: {state}. " +
                    (match != null ? $"Match {match.MatchId} ({match.Archetype}) zone={match.ZoneName} players={string.Join(",", match.ParticipantLogins)}"
                               : $"Total queued: {Gameplay.PVPMatchmaking.Instance.TotalQueued}, " +
                                 $"active matches: {Gameplay.PVPMatchmaking.Instance.TotalActiveMatches}"));
                return true;
            }

            sendMessage(conn, $"[PVP] Unknown subcommand '{sub}'. Try: queue | cancel | status | leave");
            return true;
        }

        private bool ExecuteDuelCommand(RRConnection conn, string args, Action<RRConnection, string> sendMessage)
        {
            args = (args ?? "").Trim();
            if (args.Length == 0)
            {
                sendMessage(conn, "[DUEL] usage=@duel <playername> | accept | decline | status");
                return true;
            }

            string sub = args.ToLowerInvariant();

            if (sub == "accept" || sub == "a")
            {
                _server.AcceptDuel(conn);
                sendMessage(conn, "[DUEL] Accept sent.");
                return true;
            }

            if (sub == "decline" || sub == "d")
            {
                bool had = _server.DeclineDuel(conn);
                sendMessage(conn, had ? "[DUEL] Declined." : "[DUEL] No pending duel to decline.");
                return true;
            }

            if (sub == "status" || sub == "s")
            {
                sendMessage(conn, $"[DUEL] {_server.GetDuelStatusFor(conn.LoginName)}");
                return true;
            }

            string targetName = args;
            var targetConn = _server.FindConnectionByName(targetName);
            if (targetConn == null)
            {
                sendMessage(conn, $"[DUEL] Player '{targetName}' not online.");
                return true;
            }
            string err = _server.IssueDuelChallenge(conn, targetConn);
            sendMessage(conn, err == null
                ? $"[DUEL] Challenge sent to {targetName}. They have 30s to accept."
                : $"[DUEL] Cannot challenge {targetName}: {err}");
            return true;
        }

        private bool ExecuteDuelArena(RRConnection conn, string zoneName, string targetName, Action<RRConnection, string> sendMessage)
        {
            if (string.IsNullOrEmpty(targetName))
            {
                sendMessage(conn, $"[DUELARENA] usage=@duelarena <playername> zone={zoneName}");
                return true;
            }
            var targetConn = _server.FindConnectionByName(targetName);
            if (targetConn == null || targetConn == conn)
            {
                sendMessage(conn, $"[DUELARENA] Target '{targetName}' not online (or same as you).");
                return true;
            }

            GroupDirectory.Instance.LeaveGroup(conn.ConnId);
            GroupDirectory.Instance.LeaveGroup(targetConn.ConnId);

            var g = GroupDirectory.Instance.CreateGroup(conn.ConnId, conn.LoginName, conn.LoginName);
            bool invited = GroupDirectory.Instance.InvitePlayer(conn.ConnId, targetConn.ConnId);
            if (!invited)
            {
                sendMessage(conn, "[DUELARENA] Failed to invite target.");
                return true;
            }
            var joined = GroupDirectory.Instance.AcceptInvite(targetConn.ConnId, targetConn.LoginName, targetConn.LoginName);
            if (joined == null)
            {
                sendMessage(conn, "[DUELARENA] Target failed to join group.");
                return true;
            }

            Debug.LogError($"[DUELARENA] group={g.GroupId} leader={conn.LoginName} target={targetConn.LoginName} zone={zoneName}");
            sendMessage(conn, $"[DUELARENA] Group {g.GroupId} formed with {targetName}. Teleporting both to {zoneName}...");

            _server.ChatChangeZone(conn, zoneName);
            _server.ChatChangeZone(targetConn, zoneName);
            Debug.LogError($"[DUELARENA] teleported zone={zoneName} leader={conn.LoginName} target={targetConn.LoginName}");
            return true;
        }

        private bool ExecuteGroupCommand(RRConnection conn, string args, Action<RRConnection, string> sendMessage)
        {
            if (string.IsNullOrEmpty(args))
            {
                var group = GroupDirectory.Instance.GetGroupForConn(conn.ConnId);
                if (group != null)
                {
                    string members = string.Join(", ", group.Members.Select(m =>
                        (m.ConnId == group.LeaderConnId ? "*" : "") + m.Name +
                        (m.IsOnline ? "" : " (offline)") +
                        (!string.IsNullOrEmpty(m.CurrentZoneName) ? $" [{m.CurrentZoneName}]" : "")));
                    sendMessage(conn, $"[GROUP] group={group.GroupId} members={members} seed=0x{group.InstanceSeed:X8}");
                }
                else
                {
                    sendMessage(conn, "[GROUP] state=noGroup action=@group create");
                }
                return true;
            }

            string[] parts = args.Split(' ');
            string sub = parts[0].ToLower();

            switch (sub)
            {
                case "create":
                    {
                        var group = GroupDirectory.Instance.CreateGroup(conn.ConnId, conn.LoginName, conn.LoginName);
                        sendMessage(conn, $"[GROUP] group={group.GroupId} state=created leader=true action=@group invite <name>");
                        return true;
                    }

                case "invite":
                    {
                        if (parts.Length < 2) { sendMessage(conn, "[GROUP] usage=@group invite <name>"); return true; }
                        string targetName = parts[1];
                        var target = _server.FindConnectionByName(targetName);
                        if (target == null) { sendMessage(conn, $"[GROUP] player='{targetName}' state=notFound"); return true; }
                        if (!GroupDirectory.Instance.IsInGroup(conn.ConnId))
                        {
                            GroupDirectory.Instance.CreateGroup(conn.ConnId, conn.LoginName, conn.LoginName);
                            sendMessage(conn, "[GROUP] state=created leader=true");
                        }
                        if (GroupDirectory.Instance.InvitePlayer(conn.ConnId, target.ConnId))
                        {
                            var group = GroupDirectory.Instance.GetGroupForConn(conn.ConnId);
                            uint inviterCharId = _server.GetPlayerAvatarId(conn.LoginName);
                            string inviterCharName = conn.LoginName;
                            var selChar = _server.GetSelectedCharacter(conn.LoginName);
                            if (selChar != null) inviterCharName = selChar.Name ?? inviterCharName;

                            byte[] invitePacket = GroupPackets.BuildProcessInvitation(
                                inviterCharId, group.GroupId, inviterCharName, 0x00);
                            _server.SendToClient(target, invitePacket);
                            sendMessage(conn, $"[GROUP] invite={targetName} state=sent");
                            Debug.LogError($"[GROUP] @invite: Sent processInvitation(0x32) to {target.LoginName}: inviteId=0x{inviterCharId:X8} group={group.GroupId} name='{inviterCharName}'");
                        }
                        else
                            sendMessage(conn, $"[GROUP] invite={targetName} state=failed");
                        return true;
                    }

                case "accept":
                    {
                        var group = GroupDirectory.Instance.AcceptInvite(conn.ConnId, conn.LoginName, conn.LoginName);
                        if (group != null)
                        {
                            sendMessage(conn, $"[GROUP] group={group.GroupId} state=joined");
                            _server.SendGroupConnectedToAll(group);
                        }
                        else
                            sendMessage(conn, "[GROUP] state=noInvite");
                        return true;
                    }

                case "decline":
                    GroupDirectory.Instance.DeclineInvite(conn.ConnId);
                    sendMessage(conn, "[GROUP] state=inviteDeclined");
                    return true;

                case "leave":
                    {
                        var group = GroupDirectory.Instance.GetGroupForConn(conn.ConnId);
                        if (group != null)
                        {
                            foreach (var member in group.Members)
                            {
                                if (member.ConnId == conn.ConnId) continue;
                                var memberConn = _server.FindConnectionById(member.ConnId);
                                if (memberConn != null)
                                    _server.SendSystemMessage(memberConn, $"[GROUP] player={conn.LoginName} state=left");
                            }
                        }
                        GroupDirectory.Instance.LeaveGroup(conn.ConnId);
                        sendMessage(conn, "[GROUP] state=left");
                        return true;
                    }

                case "kick":
                    {
                        if (parts.Length < 2) { sendMessage(conn, "[GROUP] usage=@group kick <name>"); return true; }
                        var target = _server.FindConnectionByName(parts[1]);
                        if (target == null) { sendMessage(conn, $"[GROUP] player='{parts[1]}' state=notFound"); return true; }
                        var group = GroupDirectory.Instance.GetGroupForConn(conn.ConnId);
                        if (group == null || group.LeaderConnId != conn.ConnId)
                        { sendMessage(conn, "[GROUP] state=notLeader"); return true; }
                        if (target.ConnId == conn.ConnId)
                        { sendMessage(conn, "[GROUP] state=selfKickBlocked"); return true; }
                        uint kickCharId = _server.GetCharSqlIdPublic(target);
                        byte[] kickPacket = GroupPackets.BuildProcessRemoveUser(group.GroupId, kickCharId);
                        foreach (var member in group.Members)
                        {
                            var memberConnection = _server.FindConnectionById(member.ConnId);
                            if (memberConnection != null) _server.SendToClient(memberConnection, kickPacket);
                        }
                        GroupDirectory.Instance.LeaveGroup(target.ConnId);
                        target.GroupConnectedSent = false;
                        _server.SendSoloGroupState(target);
                        if (group.Members.Count == 1)
                        {
                            var lastConn = _server.FindConnectionById(group.Members[0].ConnId);
                            if (lastConn != null)
                            {
                                _server.SendSoloGroupState(lastConn);
                                lastConn.GroupConnectedSent = false;
                            }
                            GroupDirectory.Instance.LeaveGroup(group.Members[0].ConnId);
                        }
                        else
                        {
                            _server.SendGroupConnectedToAll(group);
                        }
                        SocialRuntime.Instance.PushWhoListToAll(_server.SendSocialViaAuthPublic);
                        _server.SendSystemMessage(target, "[GROUP] state=removed");
                        sendMessage(conn, $"[GROUP] player={parts[1]} state=kicked");
                        Debug.LogError($"[GROUP] @kick: Kicked {parts[1]} (charSqlId=0x{kickCharId:X8}) from group {group.GroupId}");
                        return true;
                    }

                case "leader":
                case "promote":
                    {
                        if (parts.Length < 2) { sendMessage(conn, "[GROUP] usage=@group leader <n>"); return true; }
                        var target = _server.FindConnectionByName(parts[1]);
                        if (target == null) { sendMessage(conn, $"[GROUP] player='{parts[1]}' state=notFound"); return true; }
                        var group = GroupDirectory.Instance.GetGroupForConn(conn.ConnId);
                        if (group == null || group.LeaderConnId != conn.ConnId)
                        { sendMessage(conn, "[GROUP] state=notLeader"); return true; }
                        if (target.ConnId == conn.ConnId)
                        { sendMessage(conn, "[GROUP] state=alreadyLeader"); return true; }
                        uint newLeaderId = _server.GetCharSqlIdPublic(target);
                        GroupDirectory.Instance.SetLeader(conn.ConnId, target.ConnId);
                        foreach (var member in group.Members)
                        {
                            var memberConnection = _server.FindConnectionById(member.ConnId);
                            if (memberConnection != null)
                                memberConnection.GroupConnectedSent = false;
                        }
                        _server.SendGroupConnectedToAll(group);
                        sendMessage(conn, $"[GROUP] player={parts[1]} state=leader");
                        Debug.LogError($"[GROUP] @leader: Promoted {parts[1]} (charSqlId=0x{newLeaderId:X8}) in group {group.GroupId}");
                        return true;
                    }

                case "reset":
                    GroupDirectory.Instance.ResetInstances(conn.ConnId);
                    _server.ResetSoloDungeonInstances(conn);
                    sendMessage(conn, "[GROUP] action=reset state=done");
                    return true;

                case "difficulty":
                case "diff":
                    {
                        if (parts.Length < 2 || !byte.TryParse(parts[1], out byte diff))
                        { sendMessage(conn, "[GROUP] usage=@group difficulty <0-3>"); return true; }
                        if (!GroupDirectory.Instance.SetMonsterDifficulty(conn.ConnId, diff, out bool personalOnly))
                        { sendMessage(conn, "[GROUP] state=invalidDifficulty"); return true; }
                        sendMessage(conn, personalOnly
                            ? $"[GROUP] difficulty={diff} scope=personal"
                            : $"[GROUP] difficulty={diff} scope=group");
                        return true;
                    }

                case "seed":
                    {
                        uint seed = GroupDirectory.Instance.GetGroupSeed(conn.ConnId);
                        sendMessage(conn, $"[GROUP] seed=0x{seed:X8}");
                        return true;
                    }

                default:
                    sendMessage(conn, "[GROUP] commands=create,invite,accept,decline,leave,kick,leader,reset,difficulty,seed");
                    return true;
            }
        }

        private static uint GetClientThresholdFixed88(int targetLevel)
        {
            int[][] entries = new int[][] {
                new int[] { 2 << 8, 10 << 8 },
                new int[] { 3 << 8, 25 << 8 },
                new int[] { 4 << 8, 45 << 8 },
                new int[] { 5 << 8, 65 << 8 },
                new int[] { 12 << 8, 160 << 8 },
                new int[] { 22 << 8, 365 << 8 },
                new int[] { 75 << 8, 1740 << 8 },
                new int[] { 100 << 8, 5000 << 8 }
            };
            int targetLevelFixed = targetLevel << 8;
            int curveF88 = entries[entries.Length - 1][1];
            for (int entryIndex = 0; entryIndex < entries.Length; entryIndex++)
            {
                if (targetLevelFixed <= entries[entryIndex][0])
                {
                    if (entryIndex == 0) { curveF88 = entries[0][1]; }
                    else
                    {
                        int previousLevelFixed = entries[entryIndex - 1][0], previousValueFixed = entries[entryIndex - 1][1];
                        int levelFixed = entries[entryIndex][0], valueFixed = entries[entryIndex][1];
                        curveF88 = previousValueFixed + ((targetLevelFixed - previousLevelFixed) * (valueFixed - previousValueFixed)) / (levelFixed - previousLevelFixed);
                    }
                    break;
                }
            }
            return (uint)((curveF88 >> 8) * 100);
        }
    }
}
