namespace DungeonRunners.Data
{
    internal static class AuthoredWeaponTypes
    {
        internal static byte GetWeaponClassId(GCNode description)
        {
            return description.GetString("WeaponClass", "HTH") switch
            {
                "HTH" => 1,
                "1HMELEE" => 5,
                "2HMELEE" => 6,
                "1HRANGED" => 9,
                "2HRANGED" => 3,
                "POLEARM" => 8,
                "2HCANNON" => 13,
                _ => unchecked((byte)description.GetInt("WeaponClass", 1))
            };
        }

        internal static byte GetDamageTypeId(GCNode description)
        {
            return description.GetString("DamageType", "CRUSHING") switch
            {
                "CRUSHING" => 0,
                "PIERCING" => 1,
                "SLASHING" => 2,
                "FIRE" => 3,
                "ICE" => 4,
                "POISON" => 5,
                "SHADOW" => 6,
                "DIVINE" => 7,
                "LIGHTNING" => 8,
                _ => unchecked((byte)description.GetInt("DamageType", 0))
            };
        }
    }
}
