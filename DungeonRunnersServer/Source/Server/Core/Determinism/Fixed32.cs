using System;

namespace DungeonRunners.Utilities
{
    public readonly struct Fixed32 : IEquatable<Fixed32>, IComparable<Fixed32>
    {
        public readonly int RawValue;

        public const int FractionalBits = 8;
        public const int OneRaw = 1 << FractionalBits;

        public static readonly Fixed32 Zero = new Fixed32(0);
        public static readonly Fixed32 One = new Fixed32(OneRaw);
        public static readonly Fixed32 MinusOne = new Fixed32(-OneRaw);
        public static readonly Fixed32 MaxValue = new Fixed32(int.MaxValue);
        public static readonly Fixed32 MinValue = new Fixed32(int.MinValue);

        public Fixed32(int rawValue)
        {
            RawValue = rawValue;
        }

        public static Fixed32 FromFloat(float value)
        {
            return new Fixed32((int)(value * OneRaw));
        }

        public static Fixed32 FromInt(int worldUnits)
        {
            return new Fixed32(worldUnits * OneRaw);
        }

        public static Fixed32 FromRaw(int raw)
        {
            return new Fixed32(raw);
        }

        public float ToFloat()
        {
            return RawValue / (float)OneRaw;
        }

        public int ToInt()
        {
            return RawValue >> FractionalBits;
        }


        public static Fixed32 operator +(Fixed32 a, Fixed32 b)
        {
            return new Fixed32(a.RawValue + b.RawValue);
        }

        public static Fixed32 operator -(Fixed32 a, Fixed32 b)
        {
            return new Fixed32(a.RawValue - b.RawValue);
        }

        public static Fixed32 operator -(Fixed32 a)
        {
            return new Fixed32(-a.RawValue);
        }

        public static Fixed32 operator *(Fixed32 a, Fixed32 b)
        {
            long product = (long)a.RawValue * b.RawValue;
            return new Fixed32((int)(product >> FractionalBits));
        }

        public static Fixed32 operator *(Fixed32 a, int scalar)
        {
            return new Fixed32(a.RawValue * scalar);
        }

        public static Fixed32 operator *(int scalar, Fixed32 a)
        {
            return new Fixed32(a.RawValue * scalar);
        }

        public static Fixed32 operator /(Fixed32 a, Fixed32 b)
        {
            if (b.RawValue == 0)
                throw new DivideByZeroException("Fixed32 division by zero");
            long dividend = (long)a.RawValue << FractionalBits;
            return new Fixed32((int)(dividend / b.RawValue));
        }

        public static Fixed32 operator /(Fixed32 a, int scalar)
        {
            if (scalar == 0)
                throw new DivideByZeroException("Fixed32 integer division by zero");
            return new Fixed32(a.RawValue / scalar);
        }


        public static bool operator ==(Fixed32 a, Fixed32 b) => a.RawValue == b.RawValue;
        public static bool operator !=(Fixed32 a, Fixed32 b) => a.RawValue != b.RawValue;
        public static bool operator <(Fixed32 a, Fixed32 b) => a.RawValue < b.RawValue;
        public static bool operator >(Fixed32 a, Fixed32 b) => a.RawValue > b.RawValue;
        public static bool operator <=(Fixed32 a, Fixed32 b) => a.RawValue <= b.RawValue;
        public static bool operator >=(Fixed32 a, Fixed32 b) => a.RawValue >= b.RawValue;

        public bool Equals(Fixed32 other) => RawValue == other.RawValue;
        public override bool Equals(object obj) => obj is Fixed32 f && Equals(f);
        public override int GetHashCode() => RawValue.GetHashCode();
        public int CompareTo(Fixed32 other) => RawValue.CompareTo(other.RawValue);


        public static Fixed32 Abs(Fixed32 a)
        {
            return a.RawValue >= 0 ? a : new Fixed32(-a.RawValue);
        }

        public static int Sign(Fixed32 a)
        {
            if (a.RawValue == 0) return 0;
            return a.RawValue > 0 ? 1 : -1;
        }

        public static Fixed32 Min(Fixed32 a, Fixed32 b)
        {
            return a.RawValue <= b.RawValue ? a : b;
        }

        public static Fixed32 Max(Fixed32 a, Fixed32 b)
        {
            return a.RawValue >= b.RawValue ? a : b;
        }

        public static Fixed32 Clamp(Fixed32 value, Fixed32 min, Fixed32 max)
        {
            if (value.RawValue < min.RawValue) return min;
            if (value.RawValue > max.RawValue) return max;
            return value;
        }

        public override string ToString()
        {
            return $"{ToFloat():F4} (raw 0x{RawValue:X8})";
        }
    }
}
