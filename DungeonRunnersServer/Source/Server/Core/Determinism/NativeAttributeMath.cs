namespace DungeonRunners.Utilities
{
    public static class NativeAttributeMath
    {
        public static int AddBasePrimaryAttribute(int value, int baseValue, int allocated)
        {
            return unchecked(value + (ushort)(baseValue + allocated));
        }

        public static int FinalizePrimaryAttribute(int value, int modifier, int primaryModifier)
        {
            int primary = value < 1 ? 1 : value;
            int scaled = ScalePercent(primary, unchecked(modifier + primaryModifier));
            return scaled < 1 ? 1 : scaled;
        }

        public static int ScalePercent(int value, int modifier)
        {
            int modifierF32 = unchecked(modifier << 8);
            if (modifierF32 == 0) return value;
            int factorF32 = unchecked(modifierF32 + (100 << 8));
            int ratioF32 = (int)(((long)factorF32 << 8) / (100 << 8));
            int valueF32 = unchecked(value << 8);
            int resultF32 = unchecked((int)(((long)valueF32 * ratioF32) >> 8));
            return resultF32 >> 8;
        }

        public static uint ScaleWirePercent(uint wire, int modifier)
        {
            int value = unchecked((int)wire) >> 8;
            return unchecked((uint)(ScalePercent(value, modifier) << 8));
        }
    }
}
