using System;

namespace DungeonRunners.Data
{
    internal static class AuthoredNumberParser
    {
        private static readonly UInt128 MantissaBit = (UInt128)1 << 79;
        private static readonly UInt128 MantissaMask = ((UInt128)1 << 80) - 1;
        private static readonly UInt128 ProductBit = (UInt128)1 << 95;
        private static readonly UInt128 ProductMask = ((UInt128)1 << 96) - 1;
        private static readonly (UInt128 Mantissa, int Exponent)[] PositivePowers =
        {
            (new UInt128(0xa000, 0x0000000000000000UL), 16386),
            (new UInt128(0xc800, 0x0000000000000000UL), 16389),
            (new UInt128(0xfa00, 0x0000000000000000UL), 16392),
            (new UInt128(0x9c40, 0x0000000000000000UL), 16396),
            (new UInt128(0xc350, 0x0000000000000000UL), 16399),
            (new UInt128(0xf424, 0x0000000000000000UL), 16402),
            (new UInt128(0x9896, 0x8000000000000000UL), 16406),
            (new UInt128(0xbebc, 0x2000000000000000UL), 16409),
            (new UInt128(0x8e1b, 0xc9bf040000000000UL), 16436),
            (new UInt128(0xd3c2, 0x1bcecceda1000000UL), 16462),
            (new UInt128(0x9dc5, 0xada82b70b59ef020UL), 16489),
            (new UInt128(0xeb19, 0x4f8e1ae525fd5dd0UL), 16515),
            (new UInt128(0xaf29, 0x8d050e4395d79671UL), 16542),
            (new UInt128(0x8281, 0x8f1281ed44a0bff9UL), 16569),
            (new UInt128(0xc278, 0x1f49ffcfa6d53cbfUL), 16595),
            (new UInt128(0x93ba, 0x47c980e98ce0c66fUL), 16808),
            (new UInt128(0xe070, 0xf78d3927556b85bcUL), 17020),
            (new UInt128(0xaa7e, 0xebfb9df9de8eddbcUL), 17233),
            (new UInt128(0x8184, 0x2f29f2cce376e6a1UL), 17446),
            (new UInt128(0xc4c5, 0xe310aef8aa171028UL), 17658),
            (new UInt128(0x957a, 0x4ae1ebf7f3d4a7ebUL), 17871),
            (new UInt128(0xe319, 0xa0aea60e91c7cc65UL), 18083),
            (new UInt128(0xc976, 0x758681750c17650dUL), 19784),
            (new UInt128(0xb2b8, 0x353b3993a7e44258UL), 21485),
            (new UInt128(0x9e8b, 0x3b5dc53d5de5a74dUL), 23186),
            (new UInt128(0x8ca5, 0x54c020a1f0a65dffUL), 24887),
            (new UInt128(0xf989, 0x5d25d88b5a8bfdd1UL), 26587),
            (new UInt128(0xdd5d, 0xc8a2bf27f3f895aaUL), 28288),
            (new UInt128(0xc460, 0x52028a20979bc94cUL), 29989),
        };
        private static readonly (UInt128 Mantissa, int Exponent)[] NegativePowers =
        {
            (new UInt128(0xcccc, 0xcccccccccccdcccdUL), 16379),
            (new UInt128(0xa3d7, 0x0a3d70a3d70a3d71UL), 16376),
            (new UInt128(0x8312, 0x6e978d4fdf3b645aUL), 16373),
            (new UInt128(0xd1b7, 0x1758e219652cd3c3UL), 16369),
            (new UInt128(0xa7c5, 0xac471b4784230fd0UL), 16366),
            (new UInt128(0x8637, 0xbd05af6c69b6a640UL), 16363),
            (new UInt128(0xd6bf, 0x94d5e57a42bc3d33UL), 16359),
            (new UInt128(0xabcc, 0x77118461cefdfdc2UL), 16356),
            (new UInt128(0xe695, 0x94bec44de15b4c2fUL), 16329),
            (new UInt128(0x9abe, 0x14cd44753b53c492UL), 16303),
            (new UInt128(0xcfb1, 0x1ead453994ba67deUL), 16276),
            (new UInt128(0x8b61, 0x313bbabce2c62324UL), 16250),
            (new UInt128(0xbb12, 0x7c53b17ec1595561UL), 16223),
            (new UInt128(0xfb15, 0x8592be068d2feed7UL), 16196),
            (new UInt128(0xa87f, 0xea27a539e9a53f24UL), 16170),
            (new UInt128(0xddd0, 0x467c64bce4a1ac7dUL), 15957),
            (new UInt128(0x91ff, 0x83775423cc067b63UL), 15745),
            (new UInt128(0xc031, 0x4325637a193afa91UL), 15532),
            (new UInt128(0xfd00, 0xb897478238d18921UL), 15319),
            (new UInt128(0xa686, 0xe3e8b11b085888dcUL), 15107),
            (new UInt128(0xdb37, 0x7599b607424584c6UL), 14894),
            (new UInt128(0x9049, 0xee32db23d21c7133UL), 14682),
            (new UInt128(0xa2a6, 0x82a5da57c0be87a6UL), 12981),
            (new UInt128(0xb759, 0x449f52a711b268e2UL), 11280),
            (new UInt128(0xceae, 0x534f34362de44925UL), 9579),
            (new UInt128(0xe8fb, 0x7dc2dec0a404598fUL), 7878),
            (new UInt128(0x8350, 0xbf3c91575a88e79eUL), 6178),
            (new UInt128(0x9406, 0xaf8f83fd62654b4eUL), 4477),
            (new UInt128(0xa6dd, 0x04c8d2ce9fde2de4UL), 2776),
        };

        internal static bool IsWhitespace(char value)
        {
            return value == ' ' || value >= '\t' && value <= '\r';
        }

        internal static bool TryParsePrefix(string text, out double result, out bool underflow)
        {
            result = 0;
            underflow = false;
            if (string.IsNullOrEmpty(text))
                return false;
            int index = 0;
            while (index < text.Length && IsWhitespace(text[index]))
                index++;
            bool negative = index < text.Length && text[index] == '-';
            if (index < text.Length && (text[index] == '+' || text[index] == '-'))
                index++;
            Span<byte> digits = stackalloc byte[25];
            int count = 0;
            long decimalPower = 0;
            bool afterDecimal = false;
            bool sawDigit = false;
            while (index < text.Length)
            {
                char ch = text[index];
                if (ch >= '0' && ch <= '9')
                {
                    sawDigit = true;
                    if (count == 0 && ch == '0')
                    {
                        if (afterDecimal)
                            decimalPower--;
                    }
                    else if (count < digits.Length)
                    {
                        digits[count++] = (byte)(ch - '0');
                        if (afterDecimal)
                            decimalPower--;
                    }
                    else if (!afterDecimal)
                        decimalPower++;
                    index++;
                    continue;
                }
                if (ch == '.' && !afterDecimal)
                {
                    afterDecimal = true;
                    index++;
                    continue;
                }
                break;
            }
            if (!sawDigit)
                return false;
            if (index < text.Length && (text[index] == 'e' || text[index] == 'E' || text[index] == 'd' || text[index] == 'D'))
            {
                index++;
                bool exponentNegative = index < text.Length && text[index] == '-';
                if (index < text.Length && (text[index] == '+' || text[index] == '-'))
                    index++;
                int exponent = 0;
                while (index < text.Length && text[index] >= '0' && text[index] <= '9')
                {
                    exponent = Math.Min(5201, exponent * 10 + text[index] - '0');
                    index++;
                }
                decimalPower += exponentNegative ? -exponent : exponent;
            }
            if (count == 0)
            {
                result = BitConverter.UInt64BitsToDouble(negative ? 0x8000000000000000UL : 0);
                return true;
            }
            if (count == 25)
            {
                if (digits[23] >= 5)
                    digits[23]++;
                count--;
                decimalPower++;
            }
            while (digits[count - 1] == 0)
            {
                count--;
                decimalPower++;
            }
            UInt128 mantissa = 0;
            for (int i = 0; i < count; i++)
                mantissa = mantissa * 10 + digits[i];
            int binaryExponent = 16383 + 79;
            while (mantissa < MantissaBit)
            {
                mantissa <<= 1;
                binaryExponent--;
            }
            if (decimalPower > 5200)
            {
                mantissa = MantissaBit;
                binaryExponent = 32767;
            }
            else if (decimalPower < -5200)
            {
                mantissa = 0;
                binaryExponent = 0;
                underflow = true;
            }
            else if (decimalPower != 0)
            {
                var powers = decimalPower < 0 ? NegativePowers : PositivePowers;
                int power = (int)Math.Abs(decimalPower);
                int group = 0;
                mantissa = mantissa >> 16 << 16;
                while (power != 0)
                {
                    int digit = power & 7;
                    if (digit != 0)
                    {
                        var factor = powers[group * 7 + digit - 1];
                        if ((ushort)factor.Mantissa > 32767)
                        {
                            uint adjusted = unchecked((uint)(factor.Mantissa >> 16) - 1);
                            factor.Mantissa = (factor.Mantissa & ~((UInt128)uint.MaxValue << 16)) | (UInt128)adjusted << 16;
                        }
                        (mantissa, binaryExponent) = Multiply(mantissa, binaryExponent, factor.Mantissa, factor.Exponent);
                    }
                    group++;
                    power >>= 3;
                }
            }
            ulong bits = ToDoubleBits(mantissa, binaryExponent, negative, out bool conversionUnderflow);
            underflow |= conversionUnderflow;
            result = BitConverter.UInt64BitsToDouble(bits);
            return true;
        }

        private static (UInt128 Mantissa, int Exponent) Multiply(UInt128 left, int leftExponent, UInt128 right, int rightExponent)
        {
            int exponentSum = leftExponent + rightExponent;
            if (leftExponent >= 32767 || rightExponent >= 32767 || exponentSum >= 49150)
                return (MantissaBit, 32767);
            if (exponentSum <= 16319 || left == 0 || right == 0)
                return (0, 0);
            if (leftExponent == 0)
                exponentSum++;
            if (rightExponent == 0)
                exponentSum++;
            Span<ushort> words = stackalloc ushort[7];
            words.Clear();
            for (int column = 0; column < 5; column++)
            {
                for (int i = column; i < 5; i++)
                {
                    int j = column + 4 - i;
                    uint prior = (uint)words[column] | (uint)words[column + 1] << 16;
                    uint product = (uint)(ushort)(left >> (16 * i)) * (ushort)(right >> (16 * j));
                    ulong total = (ulong)prior + product;
                    if (total > uint.MaxValue)
                        words[column + 2] = unchecked((ushort)(words[column + 2] + 1));
                    words[column] = (ushort)total;
                    words[column + 1] = (ushort)(total >> 16);
                }
            }
            UInt128 combined = 0;
            for (int i = 0; i < 6; i++)
                combined |= (UInt128)words[i] << (16 * i);
            int exponent = exponentSum - 16382;
            while (exponent > 0 && combined < ProductBit)
            {
                combined = (combined << 1) & ProductMask;
                exponent--;
            }
            if (exponent <= 0)
            {
                int shift = 1 - exponent;
                UInt128 lost = combined & (((UInt128)1 << shift) - 1);
                combined >>= shift;
                if (lost != 0)
                    combined |= 1;
                exponent = 0;
            }
            ushort discarded = (ushort)combined;
            UInt128 mantissa = combined >> 16;
            if (discarded > 32768 || discarded == 32768 && (mantissa & 1) != 0)
            {
                mantissa++;
                if (mantissa > MantissaMask)
                {
                    mantissa = MantissaBit;
                    exponent++;
                }
            }
            return exponent >= 32767 ? (MantissaBit, 32767) : (mantissa, exponent);
        }

        private static UInt128 RoundSignificand(UInt128 value, out bool carry)
        {
            UInt128 roundBit = (UInt128)1 << 42;
            if ((value & roundBit) != 0 && (value & (roundBit - 1)) != 0)
                value += roundBit << 1;
            carry = value > ProductMask;
            return value & ProductMask & ~(roundBit - 1);
        }

        private static ulong ToDoubleBits(UInt128 mantissa, int exponent, bool negative, out bool underflow)
        {
            ulong sign = negative ? 0x8000000000000000UL : 0;
            underflow = false;
            if (mantissa == 0)
                return sign;
            if (exponent == 0)
            {
                underflow = true;
                return sign;
            }
            UInt128 original = mantissa << 16;
            int originalExponent = exponent - 16383;
            UInt128 rounded = RoundSignificand(original, out bool carry);
            int effectiveExponent = originalExponent + (carry ? 1 : 0);
            if (effectiveExponent < -1076)
            {
                underflow = true;
                return sign;
            }
            if (effectiveExponent <= -1023)
            {
                rounded = RoundSignificand(original >> (-1023 - originalExponent), out _);
                underflow = true;
                return sign | (ulong)(rounded >> 44);
            }
            if (effectiveExponent >= 1024)
                return sign | 0x7ff0000000000000UL;
            ulong exponentBits = (ulong)(effectiveExponent + 1023) << 52;
            ulong significandBits = (ulong)((rounded & ~ProductBit) >> 43) & 0x000fffffffffffffUL;
            return sign | exponentBits | significandBits;
        }
    }
}
