using System;
using System.Collections.Generic;
using DungeonRunners.Engine;

namespace DungeonRunners.Networking
{
    public static class DRLog
    {
        [Flags]
        public enum Cat
        {
            None = 0,
            Social = 1 << 0,
            Chat = 1 << 1,
            Combat = 1 << 2,
            Zone = 1 << 3,
            Net = 1 << 4,
            Entity = 1 << 5,
            Group = 1 << 6,
            Quest = 1 << 7,
            Inventory = 1 << 8,
            Skill = 1 << 9,
            Admin = 1 << 10,
            Auth = 1 << 11,
            All = ~0
        }

        private static Cat _enabled = Cat.All;
        private static bool _masterEnabled = true;
        private static bool _initializedFromConfig = false;

        private static readonly Dictionary<string, Cat> _nameMap = new Dictionary<string, Cat>(StringComparer.OrdinalIgnoreCase)
        {
            { "social",    Cat.Social },
            { "chat",      Cat.Chat },
            { "combat",    Cat.Combat },
            { "zone",      Cat.Zone },
            { "net",       Cat.Net },
            { "entity",    Cat.Entity },
            { "group",     Cat.Group },
            { "quest",     Cat.Quest },
            { "inventory", Cat.Inventory },
            { "inv",       Cat.Inventory },
            { "skill",     Cat.Skill },
            { "admin",     Cat.Admin },
            { "auth",      Cat.Auth },
            { "all",       Cat.All },
        };


        public static void Log(Cat cat, string tag, string message)
        {
            if (_masterEnabled && (_enabled & cat) != 0)
                Debug.Log($"[{tag}] {message}");
        }

        public static void Warn(Cat cat, string tag, string message)
        {
            if (_masterEnabled && (_enabled & cat) != 0)
                Debug.LogWarning($"[{tag}] {message}");
        }

        public static void Error(Cat cat, string tag, string message)
        {
            if (_masterEnabled && (_enabled & cat) != 0)
                Debug.LogError($"[{tag}] {message}");
        }


        public static void Social(string message) => Warn(Cat.Social, "SOCIAL", message);
        public static void Chat(string message) => Log(Cat.Chat, "CHAT", message);
        public static void Combat(string message) => Log(Cat.Combat, "COMBAT", message);
        public static void Zone(string message) => Log(Cat.Zone, "ZONE", message);
        public static void Net(string message) => Log(Cat.Net, "NET", message);
        public static void Entity(string message) => Log(Cat.Entity, "ENTITY", message);
        public static void Group(string message) => Log(Cat.Group, "GROUP", message);
        public static void Quest(string message) => Log(Cat.Quest, "QUEST", message);
        public static void Inv(string message) => Log(Cat.Inventory, "INV", message);
        public static void Skill(string message) => Log(Cat.Skill, "SKILL", message);
        public static void Admin(string message) => Log(Cat.Admin, "ADMIN", message);
        public static void Auth(string message) => Log(Cat.Auth, "AUTH", message);


        public static void Enable(Cat cat) => _enabled |= cat;
        public static void Disable(Cat cat) => _enabled &= ~cat;
        public static void Toggle(Cat cat) => _enabled ^= cat;
        public static bool IsEnabled(Cat cat) => (_enabled & cat) != 0;

        public static void SetAll(bool on)
        {
            _enabled = on ? Cat.All : Cat.None;
        }

        public static void SetMasterLog(bool on)
        {
            _masterEnabled = on;
            DungeonRunners.Core.ServerDiagnostics.SetMasterOverride(on);
            ApplyLogFilter();
            DungeonRunners.Core.RuntimeEvidence.ApplyConfiguredDiagnostics();
        }

        private static void ApplyLogFilter()
        {
            bool focused = DungeonRunners.Core.ServerSettings.GetCfgBool("focusedDebugLog", true);
            DungeonRunners.Core.RuntimeEvidence.SetFocusedLogFilter(!_masterEnabled || focused);
        }

        public static void InitFromConfig()
        {
            _initializedFromConfig = true;
            DungeonRunners.Core.ServerDiagnostics.ClearMasterOverride();

            bool masterEnabled = DungeonRunners.Core.ServerDiagnostics.Enabled;
            _masterEnabled = masterEnabled;
            ApplyLogFilter();
            if (!masterEnabled)
            {
                SetAll(false);
                Debug.LogError("[DR-LOG] enabled=false source=server.cfg key=enableDebugLog");
                DungeonRunners.Core.RuntimeEvidence.ApplyConfiguredDiagnostics();
                return;
            }

            string categories = DungeonRunners.Core.ServerSettings.GetCfgString("logCategories", "all").Trim().ToLower();
            if (string.IsNullOrEmpty(categories) || categories == "all")
            {
                SetAll(true);
                Debug.LogError("[DR-LOG] categories=all enabled=true");
            }
            else if (categories == "none")
            {
                SetAll(false);
                Debug.LogError("[DR-LOG] categories=all enabled=false");
            }
            else
            {
                SetAll(false);
                string[] parts = categories.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string part in parts)
                {
                    string trimmed = part.Trim();
                    if (_nameMap.TryGetValue(trimmed, out Cat cat))
                    {
                        Enable(cat);
                    }
                }
                Debug.LogError($"[DR-LOG] categories={categories} source=server.cfg");
            }
            DungeonRunners.Core.RuntimeEvidence.ApplyConfiguredDiagnostics();
        }

        public static string HandleLogCommand(string args)
        {
            if (string.IsNullOrWhiteSpace(args))
            {
                return GetStatusString();
            }

            string[] parts = args.Trim().ToLower().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 1 && parts[0] == "off")
            {
                SetMasterLog(false);
                SetAll(false);
                return "log master=false categories=false";
            }
            if (parts.Length == 1 && parts[0] == "on")
            {
                SetMasterLog(true);
                SetAll(true);
                return "log master=true categories=all";
            }

            if (parts.Length == 2 && parts[0] == "all")
            {
                if (parts[1] == "on") { SetAll(true); SetMasterLog(true); return "log master=true categories=all"; }
                if (parts[1] == "off") { SetAll(false); return "log categories=false master=unchanged"; }
            }

            if (parts.Length == 2 && _nameMap.ContainsKey(parts[0]))
            {
                Cat cat = _nameMap[parts[0]];
                if (parts[1] == "on") { Enable(cat); return $"log category={parts[0]} enabled=true"; }
                if (parts[1] == "off") { Disable(cat); return $"log category={parts[0]} enabled=false"; }
            }

            string result = "";
            foreach (var part in parts)
            {
                if (_nameMap.TryGetValue(part, out Cat cat))
                {
                    Toggle(cat);
                    result += $"{part}={IsEnabled(cat)} ";
                }
                else
                {
                    result += $"{part}=? ";
                }
            }

            return string.IsNullOrEmpty(result) ? GetStatusString() : $"log {result.Trim()}";
        }

        private static string GetStatusString()
        {
            bool master = _masterEnabled;
            if (!master) return "log master=false command=@log on";
            if (_enabled == Cat.All) return "log master=true categories=all";
            if (_enabled == Cat.None) return "log categories=false master=true command=@log off";

            var on = new List<string>();
            var off = new List<string>();
            foreach (var categoryEntry in _nameMap)
            {
                if (categoryEntry.Key == "all" || categoryEntry.Key == "inv") continue;
                if ((_enabled & categoryEntry.Value) != 0)
                    on.Add(categoryEntry.Key);
                else
                    off.Add(categoryEntry.Key);
            }
            return $"enabled={string.Join(",", on)} disabled={string.Join(",", off)}";
        }
    }
}
