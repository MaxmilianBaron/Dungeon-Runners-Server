namespace DungeonRunners.Data
{
    internal static class AuthoredWeaponModes
    {
        internal static int GetShotType(GCNode description)
        {
            if (!NativeAuthoredClasses.IsDerivedFrom(description?.NativeClassName, "RangedWeaponDesc"))
                return 0;
            return description.GetString("ShotType") switch
            {
                "SINGLESHOT" => 0,
                "RAPIDFIRE" => 1,
                _ => unchecked((byte)description.GetInt("ShotType", 0))
            };
        }

        internal static bool GetUseProjectile(GCNode description)
        {
            return NativeAuthoredClasses.IsDerivedFrom(description?.NativeClassName, "RangedWeaponDesc")
                && description.GetBool("UseProjectile", true);
        }
    }
}
