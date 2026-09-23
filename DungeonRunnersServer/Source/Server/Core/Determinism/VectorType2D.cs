namespace DungeonRunners.Utilities
{
    public readonly struct VectorType2D
    {
        public readonly Fixed32 X;
        public readonly Fixed32 Y;

        public VectorType2D(Fixed32 x, Fixed32 y)
        {
            X = x;
            Y = y;
        }

        public static VectorType2D FromHeading(Fixed32 heading)
        {
            int headingIndex = (360 - (heading.RawValue >> 8)) % 360;
            if (headingIndex < 0) headingIndex += 360;
            return new VectorType2D(new Fixed32(Fixed32Math.SIN_TABLE[headingIndex]), new Fixed32(Fixed32Math.COS_TABLE[headingIndex]));
        }

        public void Deconstruct(out Fixed32 x, out Fixed32 y)
        {
            x = X;
            y = Y;
        }
    }
}
