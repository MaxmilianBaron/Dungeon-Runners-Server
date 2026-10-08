using System;
using System.Collections.Generic;

namespace DungeonRunners.Combat
{
    public sealed class MonsterCombatCredit
    {
        private readonly List<CombatPlayer> _participants = new List<CombatPlayer>();
        private readonly Dictionary<CombatPlayer, ulong> _damage = new Dictionary<CombatPlayer, ulong>();

        public void Record(CombatPlayer player, uint damageWire)
        {
            if (player == null || damageWire == 0)
                return;
            if (!_damage.TryGetValue(player, out ulong current))
                _participants.Add(player);
            _damage[player] = ulong.MaxValue - current < damageWire ? ulong.MaxValue : current + damageWire;
        }

        public bool Contains(CombatPlayer player) => player != null && _damage.ContainsKey(player);

        public CombatPlayer Resolve(Func<CombatPlayer, bool> eligible)
        {
            CombatPlayer selected = null;
            ulong maximum = 0;
            foreach (CombatPlayer player in _participants)
            {
                if (!eligible(player))
                    continue;
                ulong damage = _damage[player];
                if (selected == null || damage > maximum)
                {
                    selected = player;
                    maximum = damage;
                }
            }
            return selected;
        }

        public void Remove(CombatPlayer player)
        {
            _damage.Remove(player);
            _participants.Remove(player);
        }

        public void Clear()
        {
            _damage.Clear();
            _participants.Clear();
        }
    }
}
