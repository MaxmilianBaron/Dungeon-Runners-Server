using System;
using DungeonRunners.Core;
using DungeonRunners.Utilities;

namespace DungeonRunners.Combat
{
    public static class UnitMover
    {
        public const int Fixed = 0x100;
        public const int InitialHeading = 0;
        public const byte StoppedMode = 1;
        public const byte MoveToPointMode = 2;
        public const byte MoveInDirectionMode = 3;
        public const byte FollowClientMode = 4;

        public static bool IsMoving(byte mode)
        {
            return mode != StoppedMode;
        }

        private static readonly int[] SinTable =
        {
            0, 4, 8, 13, 17, 22, 26, 31, 35, 40, 44, 48,
            53, 57, 61, 66, 70, 74, 79, 83, 87, 91, 95, 100,
            104, 108, 112, 116, 120, 124, 127, 131, 135, 139, 143, 146,
            150, 154, 157, 161, 164, 167, 171, 174, 177, 181, 184, 187,
            190, 193, 196, 198, 201, 204, 207, 209, 212, 214, 217, 219,
            221, 223, 226, 228, 230, 232, 233, 235, 237, 238, 240, 242,
            243, 244, 246, 247, 248, 249, 250, 251, 252, 252, 253, 254,
            254, 255, 255, 255, 255, 255, 256, 255, 255, 255, 255, 255,
            254, 254, 253, 252, 252, 251, 250, 249, 248, 247, 246, 244,
            243, 242, 240, 238, 237, 235, 233, 232, 230, 228, 226, 223,
            221, 219, 217, 214, 212, 209, 207, 204, 201, 198, 196, 193,
            190, 187, 184, 181, 177, 174, 171, 167, 164, 161, 157, 154,
            150, 146, 143, 139, 135, 131, 127, 124, 120, 116, 112, 108,
            104, 100, 95, 91, 87, 83, 79, 74, 70, 66, 61, 57,
            53, 48, 44, 40, 35, 31, 26, 22, 17, 13, 8, 4,
            0, -4, -8, -13, -17, -22, -26, -31, -35, -40, -44, -48,
            -53, -57, -61, -66, -70, -74, -79, -83, -87, -91, -95, -100,
            -104, -108, -112, -116, -120, -124, -128, -131, -135, -139, -143, -146,
            -150, -154, -157, -161, -164, -167, -171, -174, -177, -181, -184, -187,
            -190, -193, -196, -198, -201, -204, -207, -209, -212, -214, -217, -219,
            -221, -223, -226, -228, -230, -232, -233, -235, -237, -238, -240, -242,
            -243, -244, -246, -247, -248, -249, -250, -251, -252, -252, -253, -254,
            -254, -255, -255, -255, -255, -255, -256, -255, -255, -255, -255, -255,
            -254, -254, -253, -252, -252, -251, -250, -249, -248, -247, -246, -244,
            -243, -242, -240, -238, -237, -235, -233, -232, -230, -228, -226, -223,
            -221, -219, -217, -214, -212, -209, -207, -204, -201, -198, -196, -193,
            -190, -187, -184, -181, -177, -174, -171, -167, -164, -161, -157, -154,
            -150, -146, -143, -139, -135, -131, -128, -124, -120, -116, -112, -108,
            -104, -100, -95, -91, -87, -83, -79, -74, -70, -66, -61, -57,
            -53, -48, -44, -40, -35, -31, -26, -22, -17, -13, -8, -4,
        };

        private static readonly int[] CosTable =
        {
            256, 255, 255, 255, 255, 255, 254, 254, 253, 252, 252, 251,
            250, 249, 248, 247, 246, 244, 243, 242, 240, 238, 237, 235,
            233, 232, 230, 228, 226, 223, 221, 219, 217, 214, 212, 209,
            207, 204, 201, 198, 196, 193, 190, 187, 184, 181, 177, 174,
            171, 167, 164, 161, 157, 154, 150, 146, 143, 139, 135, 131,
            128, 124, 120, 116, 112, 108, 104, 100, 95, 91, 87, 83,
            79, 74, 70, 66, 61, 57, 53, 48, 44, 40, 35, 31,
            26, 22, 17, 13, 8, 4, 0, -4, -8, -13, -17, -22,
            -26, -31, -35, -40, -44, -48, -53, -57, -61, -66, -70, -74,
            -79, -83, -87, -91, -95, -100, -104, -108, -112, -116, -120, -124,
            -127, -131, -135, -139, -143, -146, -150, -154, -157, -161, -164, -167,
            -171, -174, -177, -181, -184, -187, -190, -193, -196, -198, -201, -204,
            -207, -209, -212, -214, -217, -219, -221, -223, -226, -228, -230, -232,
            -233, -235, -237, -238, -240, -242, -243, -244, -246, -247, -248, -249,
            -250, -251, -252, -252, -253, -254, -254, -255, -255, -255, -255, -255,
            -256, -255, -255, -255, -255, -255, -254, -254, -253, -252, -252, -251,
            -250, -249, -248, -247, -246, -244, -243, -242, -240, -238, -237, -235,
            -233, -232, -230, -228, -226, -223, -221, -219, -217, -214, -212, -209,
            -207, -204, -201, -198, -196, -193, -190, -187, -184, -181, -177, -174,
            -171, -167, -164, -161, -157, -154, -150, -146, -143, -139, -135, -131,
            -128, -124, -120, -116, -112, -108, -104, -100, -95, -91, -87, -83,
            -79, -74, -70, -66, -61, -57, -53, -48, -44, -40, -35, -31,
            -26, -22, -17, -13, -8, -4, 0, 4, 8, 13, 17, 22,
            26, 31, 35, 40, 44, 48, 53, 57, 61, 66, 70, 74,
            79, 83, 87, 91, 95, 100, 104, 108, 112, 116, 120, 124,
            128, 131, 135, 139, 143, 146, 150, 154, 157, 161, 164, 167,
            171, 174, 177, 181, 184, 187, 190, 193, 196, 198, 201, 204,
            207, 209, 212, 214, 217, 219, 221, 223, 226, 228, 230, 232,
            233, 235, 237, 238, 240, 242, 243, 244, 246, 247, 248, 249,
            250, 251, 252, 252, 253, 254, 254, 255, 255, 255, 255, 255,
        };

        private static readonly int[] SquareRootTable =
        {
            0, 4103, 5803, 7108, 8207, 9176, 10052, 10858, 11607, 12311, 12977, 13611,
            14216, 14797, 15355, 15894, 16415, 16921, 17411, 17888, 18353, 18806, 19249, 19681,
            20105, 20519, 20926, 21324, 21716, 22100, 22478, 22849, 23215, 23575, 23929, 24279,
            24623, 24963, 25298, 25629, 25955, 26278, 26596, 26911, 27222, 27530, 27834, 28135,
            28433, 28727, 29019, 29308, 29594, 29877, 30157, 30435, 30711, 30984, 31254, 31523,
            31789, 32052, 32314, 32574, 32831, 33087, 33340, 33592, 33842, 34090, 34336, 34580,
            34823, 35064, 35303, 35541, 35777, 36012, 36245, 36476, 36706, 36935, 37162, 37388,
            37613, 37836, 38058, 38279, 38498, 38716, 38933, 39149, 39363, 39577, 39789, 40000,
            40210, 40419, 40627, 40833, 41039, 41244, 41447, 41650, 41852, 42053, 42252, 42451,
            42649, 42846, 43042, 43237, 43432, 43625, 43818, 44010, 44201, 44391, 44580, 44768,
            44956, 45143, 45329, 45515, 45699, 45883, 46066, 46249, 46431, 46612, 46792, 46971,
            47150, 47329, 47506, 47683, 47859, 48035, 48210, 48384, 48558, 48731, 48904, 49076,
            49247, 49418, 49588, 49757, 49926, 50095, 50263, 50430, 50597, 50763, 50928, 51093,
            51258, 51422, 51585, 51748, 51911, 52073, 52234, 52395, 52556, 52716, 52875, 53034,
            53193, 53351, 53509, 53666, 53822, 53979, 54134, 54290, 54445, 54599, 54753, 54907,
            55060, 55213, 55365, 55517, 55668, 55819, 55970, 56120, 56270, 56420, 56569, 56717,
            56866, 57014, 57161, 57308, 57455, 57601, 57747, 57893, 58038, 58183, 58328, 58472,
            58616, 58759, 58902, 59045, 59188, 59330, 59472, 59613, 59754, 59895, 60035, 60175,
            60315, 60455, 60594, 60733, 60871, 61009, 61147, 61285, 61422, 61559, 61696, 61832,
            61968, 62104, 62239, 62374, 62509, 62644, 62778, 62912, 63046, 63179, 63312, 63445,
            63578, 63710, 63842, 63974, 64105, 64237, 64368, 64498, 64629, 64759, 64889, 65018,
            65148, 65277, 65406, 65535
        };

        public static int TableSquareRoot(uint value)
        {
            if ((int)value <= 0) return 0;
            int shift = 0;
            uint scaled = value >> 6;
            while ((scaled >>= 1) != 0) shift++;
            return (int)(((uint)(SquareRootTable[value >> (shift & 0x1e)] << (shift >> 1 & 0x1f)) + 1) >> 8);
        }

        public static int IntSqrt(long value)
        {
            if (value <= 0) return 0;
            long x = 0;
            long bit = 1L << 62;
            while (bit > value) bit >>= 2;
            while (bit != 0)
            {
                if (value >= x + bit)
                {
                    value -= x + bit;
                    x = (x >> 1) + bit;
                }
                else
                {
                    x >>= 1;
                }
                bit >>= 2;
            }
            return (int)x;
        }

        public static int WrapDegrees(int deg)
        {
            deg %= 360;
            if (deg < 0) deg += 360;
            return deg;
        }

        public static int ZRotateCosFixed(int degrees) => CosTable[WrapDegrees(degrees)];
        public static int ZRotateSinFixed(int degrees) => SinTable[WrapDegrees(degrees)];

        public const int FullCircleFixed = 0x16800;

        public static int CacheSpeedPerFrame(int speedF32, int speedMod, out int effectiveSpeedF32)
        {
            return CacheSpeedPerFrameF32(speedF32, speedMod << 8, out effectiveSpeedF32);
        }

        public static int CacheSpeedPerFrameF32(int speedF32, int speedModF32, out int effectiveSpeedF32)
        {
            effectiveSpeedF32 = (int)(((long)speedF32 * speedModF32) / 0x6400);
            return (int)(((long)effectiveSpeedF32 << 8) / 0x1e00);
        }

        public static int ApplyPercentModifierF32(int valueF32, int modifierF32)
        {
            return (int)(((long)valueF32 * modifierF32) / 0x6400);
        }

        public static int TurnRatePerTickFixed(int turnRateDegrees)
        {
            return (turnRateDegrees << 8) / 30;
        }

        public static int CacheSteeringArrivalDistance(int effectiveSpeedF32, int turnRatePerSecondF32)
        {
            if (turnRatePerSecondF32 == 0) return 0xC35000;
            long circleRatioF32 = ((long)FullCircleFixed << 8) / turnRatePerSecondF32;
            long distanceF32 = (circleRatioF32 * effectiveSpeedF32) >> 8;
            distanceF32 = (distanceF32 << 8) / 0x324;
            distanceF32 = (distanceF32 << 8) / 0x200;
            return (int)distanceF32;
        }

        public static int InterpolateHeading(int current, int target, int turnRate)
        {
            int diff = target - current;
            if (diff >= 0xB401) diff -= FullCircleFixed;
            else if (diff < -0xB400) diff += FullCircleFixed;
            int result;
            if (diff < 0)
                result = -diff < turnRate ? target : current - turnRate;
            else if (diff == 0)
                result = current;
            else
                result = diff < turnRate ? target : current + turnRate;
            if (result < FullCircleFixed)
            {
                if (result < 0)
                    result = result + FullCircleFixed + (int)((uint)(-result - 1) / FullCircleFixed) * FullCircleFixed;
            }
            else if (result > FullCircleFixed)
            {
                result = result + (int)((uint)(result - (FullCircleFixed + 1)) / FullCircleFixed) * (-FullCircleFixed) + (-FullCircleFixed);
            }
            return result;
        }

        private static readonly int[] AsinTable =
        {
            -23040, -21743, -21205, -20792, -20443, -20136, -19858, -19602, -19363, -19139, -18926, -18724,
            -18531, -18345, -18166, -17994, -17826, -17664, -17506, -17353, -17203, -17057, -16914, -16774,
            -16637, -16503, -16372, -16242, -16115, -15990, -15867, -15746, -15627, -15510, -15394, -15279,
            -15167, -15055, -14945, -14837, -14729, -14623, -14518, -14414, -14312, -14210, -14109, -14010,
            -13911, -13813, -13716, -13620, -13525, -13430, -13337, -13244, -13152, -13060, -12969, -12879,
            -12790, -12701, -12613, -12526, -12439, -12352, -12267, -12181, -12097, -12012, -11929, -11846,
            -11763, -11681, -11599, -11518, -11437, -11357, -11277, -11197, -11118, -11040, -10961, -10883,
            -10806, -10729, -10652, -10575, -10499, -10423, -10348, -10273, -10198, -10124, -10050, -9976,
            -9902, -9829, -9756, -9683, -9611, -9539, -9467, -9395, -9324, -9253, -9182, -9111,
            -9041, -8971, -8901, -8832, -8762, -8693, -8624, -8555, -8487, -8418, -8350, -8282,
            -8215, -8147, -8080, -8013, -7946, -7879, -7812, -7746, -7680, -7613, -7548, -7482,
            -7416, -7351, -7286, -7220, -7156, -7091, -7026, -6962, -6897, -6833, -6769, -6705,
            -6641, -6578, -6514, -6451, -6387, -6324, -6261, -6198, -6136, -6073, -6011, -5948,
            -5886, -5824, -5762, -5700, -5638, -5576, -5514, -5453, -5391, -5330, -5269, -5208,
            -5147, -5086, -5025, -4964, -4903, -4843, -4782, -4722, -4661, -4601, -4541, -4481,
            -4421, -4361, -4301, -4241, -4181, -4122, -4062, -4002, -3943, -3884, -3824, -3765,
            -3706, -3647, -3588, -3528, -3470, -3411, -3352, -3293, -3234, -3176, -3117, -3058,
            -3000, -2941, -2883, -2824, -2766, -2708, -2649, -2591, -2533, -2475, -2417, -2359,
            -2301, -2243, -2185, -2127, -2069, -2011, -1953, -1896, -1838, -1780, -1722, -1665,
            -1607, -1549, -1492, -1434, -1377, -1319, -1262, -1204, -1147, -1089, -1032, -974,
            -917, -859, -802, -745, -687, -630, -573, -515, -458, -401, -343, -286,
            -229, -171, -114, -57, 0, 57, 114, 171, 229, 286, 343, 401,
            458, 515, 573, 630, 687, 745, 802, 859, 917, 974, 1032, 1089,
            1147, 1204, 1262, 1319, 1377, 1434, 1492, 1549, 1607, 1665, 1722, 1780,
            1838, 1896, 1953, 2011, 2069, 2127, 2185, 2243, 2301, 2359, 2417, 2475,
            2533, 2591, 2649, 2708, 2766, 2824, 2883, 2941, 3000, 3058, 3117, 3176,
            3234, 3293, 3352, 3411, 3470, 3528, 3588, 3647, 3706, 3765, 3824, 3884,
            3943, 4002, 4062, 4122, 4181, 4241, 4301, 4361, 4421, 4481, 4541, 4601,
            4661, 4722, 4782, 4843, 4903, 4964, 5025, 5086, 5147, 5208, 5269, 5330,
            5391, 5453, 5514, 5576, 5638, 5700, 5762, 5824, 5886, 5948, 6011, 6073,
            6136, 6198, 6261, 6324, 6387, 6451, 6514, 6578, 6641, 6705, 6769, 6833,
            6897, 6962, 7026, 7091, 7156, 7220, 7286, 7351, 7416, 7482, 7548, 7613,
            7680, 7746, 7812, 7879, 7946, 8013, 8080, 8147, 8215, 8282, 8350, 8418,
            8487, 8555, 8624, 8693, 8762, 8832, 8901, 8971, 9041, 9111, 9182, 9253,
            9324, 9395, 9467, 9539, 9611, 9683, 9756, 9829, 9902, 9976, 10050, 10124,
            10198, 10273, 10348, 10423, 10499, 10575, 10652, 10729, 10806, 10883, 10961, 11040,
            11118, 11197, 11277, 11357, 11437, 11518, 11599, 11681, 11763, 11846, 11929, 12012,
            12097, 12181, 12267, 12352, 12439, 12526, 12613, 12701, 12790, 12879, 12969, 13060,
            13152, 13244, 13337, 13430, 13525, 13620, 13716, 13813, 13911, 14010, 14109, 14210,
            14312, 14414, 14518, 14623, 14729, 14837, 14945, 15055, 15167, 15279, 15394, 15510,
            15627, 15746, 15867, 15990, 16115, 16242, 16372, 16503, 16637, 16774, 16914, 17057,
            17203, 17353, 17506, 17664, 17826, 17994, 18166, 18345, 18531, 18724, 18926, 19139,
            19363, 19602, 19858, 20136, 20443, 20792, 21205, 21743, 23040
        };

        private static readonly int[] AcosTable =
        {
            46080, 44783, 44245, 43832, 43483, 43176, 42898, 42642, 42403, 42179, 41966, 41764,
            41571, 41385, 41206, 41034, 40866, 40704, 40546, 40393, 40243, 40097, 39954, 39814,
            39677, 39543, 39412, 39282, 39155, 39030, 38907, 38786, 38667, 38550, 38434, 38319,
            38207, 38095, 37985, 37877, 37769, 37663, 37558, 37454, 37352, 37250, 37149, 37050,
            36951, 36853, 36756, 36660, 36565, 36470, 36377, 36284, 36192, 36100, 36009, 35919,
            35830, 35741, 35653, 35566, 35479, 35392, 35307, 35221, 35137, 35052, 34969, 34886,
            34803, 34721, 34639, 34558, 34477, 34397, 34317, 34237, 34158, 34080, 34001, 33923,
            33846, 33769, 33692, 33615, 33539, 33463, 33388, 33313, 33238, 33164, 33090, 33016,
            32942, 32869, 32796, 32723, 32651, 32579, 32507, 32435, 32364, 32293, 32222, 32151,
            32081, 32011, 31941, 31872, 31802, 31733, 31664, 31595, 31527, 31458, 31390, 31322,
            31255, 31187, 31120, 31053, 30986, 30919, 30852, 30786, 30720, 30653, 30588, 30522,
            30456, 30391, 30326, 30260, 30196, 30131, 30066, 30002, 29937, 29873, 29809, 29745,
            29681, 29618, 29554, 29491, 29427, 29364, 29301, 29238, 29176, 29113, 29051, 28988,
            28926, 28864, 28802, 28740, 28678, 28616, 28554, 28493, 28431, 28370, 28309, 28248,
            28187, 28126, 28065, 28004, 27943, 27883, 27822, 27762, 27701, 27641, 27581, 27521,
            27461, 27401, 27341, 27281, 27221, 27162, 27102, 27042, 26983, 26924, 26864, 26805,
            26746, 26687, 26628, 26568, 26510, 26451, 26392, 26333, 26274, 26216, 26157, 26098,
            26040, 25981, 25923, 25864, 25806, 25748, 25689, 25631, 25573, 25515, 25457, 25399,
            25341, 25283, 25225, 25167, 25109, 25051, 24993, 24936, 24878, 24820, 24762, 24705,
            24647, 24589, 24532, 24474, 24417, 24359, 24302, 24244, 24187, 24129, 24072, 24014,
            23957, 23899, 23842, 23785, 23727, 23670, 23613, 23555, 23498, 23441, 23383, 23326,
            23269, 23211, 23154, 23097, 23040, 22982, 22925, 22868, 22810, 22753, 22696, 22638,
            22581, 22524, 22466, 22409, 22352, 22294, 22237, 22180, 22122, 22065, 22007, 21950,
            21892, 21835, 21777, 21720, 21662, 21605, 21547, 21490, 21432, 21374, 21317, 21259,
            21201, 21143, 21086, 21028, 20970, 20912, 20854, 20796, 20738, 20680, 20622, 20564,
            20506, 20448, 20390, 20331, 20273, 20215, 20156, 20098, 20039, 19981, 19922, 19863,
            19805, 19746, 19687, 19628, 19569, 19511, 19451, 19392, 19333, 19274, 19215, 19155,
            19096, 19037, 18977, 18917, 18858, 18798, 18738, 18678, 18618, 18558, 18498, 18438,
            18378, 18317, 18257, 18196, 18136, 18075, 18014, 17953, 17892, 17831, 17770, 17709,
            17648, 17586, 17525, 17463, 17401, 17339, 17277, 17215, 17153, 17091, 17028, 16966,
            16903, 16841, 16778, 16715, 16652, 16588, 16525, 16461, 16398, 16334, 16270, 16206,
            16142, 16077, 16013, 15948, 15883, 15819, 15753, 15688, 15623, 15557, 15491, 15426,
            15359, 15293, 15227, 15160, 15093, 15026, 14959, 14892, 14824, 14757, 14689, 14621,
            14552, 14484, 14415, 14346, 14277, 14207, 14138, 14068, 13998, 13928, 13857, 13786,
            13715, 13644, 13572, 13500, 13428, 13356, 13283, 13210, 13137, 13063, 12989, 12915,
            12841, 12766, 12691, 12616, 12540, 12464, 12387, 12310, 12233, 12156, 12078, 11999,
            11921, 11842, 11762, 11682, 11602, 11521, 11440, 11358, 11276, 11193, 11110, 11027,
            10942, 10858, 10772, 10687, 10600, 10513, 10426, 10338, 10249, 10160, 10070, 9979,
            9887, 9795, 9702, 9609, 9514, 9419, 9323, 9226, 9128, 9029, 8930, 8829,
            8727, 8625, 8521, 8416, 8310, 8202, 8094, 7984, 7872, 7760, 7645, 7529,
            7412, 7293, 7172, 7049, 6924, 6797, 6667, 6536, 6402, 6265, 6125, 5982,
            5836, 5686, 5533, 5375, 5213, 5045, 4873, 4694, 4508, 4315, 4113, 3900,
            3676, 3437, 3181, 2903, 2596, 2247, 1834, 1296, 0
        };

        public static int VectorToHeadingFixed(int dxFixed, int dyFixed)
        {
            if (dxFixed == 0 && dyFixed == 0) return 0;
            int xSq = (int)(((long)dxFixed * dxFixed) >> 8);
            int ySq = (int)(((long)dyFixed * dyFixed) >> 8);
            int sq = TableSquareRoot((uint)(xSq + ySq));
            if (sq == 0) return 0;
            int h;
            if (Math.Abs((long)dyFixed) < Math.Abs((long)dxFixed))
            {
                int ratio = (int)(((long)dyFixed << 8) / sq) + 0x100;
                if (ratio < 0) ratio = 0; else if (ratio > 0x200) ratio = 0x200;
                if (dxFixed < 0) h = (AsinTable[ratio] >> 8) + 0x10e;
                else h = 0x5a - (AsinTable[ratio] >> 8);
            }
            else
            {
                int ratio = (int)(((long)dxFixed << 8) / sq) + 0x100;
                if (ratio < 0) ratio = 0; else if (ratio > 0x200) ratio = 0x200;
                if (dyFixed < 0) h = (AcosTable[ratio] >> 8) + 0x5a;
                else h = 0x5a - (AcosTable[ratio] >> 8);
            }
            int deg = ((0x168 - h) % 0x168 + 0x168) % 0x168;
            return deg << 8;
        }

        public static int NormalizedVectorToHeadingFixed(int dxFixed, int dyFixed)
        {
            int xSq = (int)(((long)dxFixed * dxFixed) >> 8);
            int ySq = (int)(((long)dyFixed * dyFixed) >> 8);
            int distanceFixed = TableSquareRoot((uint)(xSq + ySq));
            if (distanceFixed <= 0)
                return 0;
            int normalizedX = (int)(((long)dxFixed << 8) / distanceFixed);
            int normalizedY = (int)(((long)dyFixed << 8) / distanceFixed);
            return VectorToHeadingFixed(normalizedX, normalizedY);
        }

        public static void StepTowardFixedHeading(int curX, int curY, int headingCur, int tgtX, int tgtY, int stepFixed, int turnRate, out int newX, out int newY, out int newHeading, out bool arrived)
        {
            long dx = (long)tgtX - curX;
            long dy = (long)tgtY - curY;
            if (dx == 0 && dy == 0)
            {
                newX = tgtX; newY = tgtY; newHeading = headingCur; arrived = true;
                return;
            }
            int targetHeading = VectorToHeadingFixed((int)dx, (int)dy);
            long distanceSquared = dx * dx + dy * dy;
            if (distanceSquared <= (long)stepFixed * stepFixed)
            {
                newX = tgtX; newY = tgtY; newHeading = headingCur; arrived = true;
                return;
            }
            newHeading = InterpolateHeading(headingCur, targetHeading, turnRate);
            var (fvx, fvy) = VectorType2D.FromHeading(new Fixed32(newHeading));
            var (tvx, tvy) = VectorType2D.FromHeading(new Fixed32(targetHeading));
            int fx = fvx.RawValue;
            int fy = fvy.RawValue;
            int tx = tvx.RawValue;
            int ty = tvy.RawValue;
            int dot = (fx * tx + fy * ty) >> 8;
            if (dot <= 0xE5)
            {
                newX = curX; newY = curY; arrived = false;
                return;
            }
            newX = curX + (int)(((long)fx * stepFixed) >> 8);
            newY = curY + (int)(((long)fy * stepFixed) >> 8);
            arrived = false;
        }

        public static void StepInDirectionFixedHeading(int curX, int curY, int headingCur, int directionHeading, int stepFixed, int turnRate, bool turnBeforeMoving, bool movingThisFrame, out int newX, out int newY, out int newHeading, out bool nextMovingThisFrame)
        {
            newHeading = InterpolateHeading(headingCur, directionHeading, turnRate);
            var (fvx, fvy) = VectorType2D.FromHeading(new Fixed32(newHeading));
            int fx = fvx.RawValue;
            int fy = fvy.RawValue;
            if (turnBeforeMoving && !movingThisFrame)
            {
                var (tvx, tvy) = VectorType2D.FromHeading(new Fixed32(directionHeading));
                int tx = tvx.RawValue;
                int ty = tvy.RawValue;
                int dot = (fx * tx + fy * ty) >> 8;
                if (dot <= 0xE5)
                {
                    newX = curX;
                    newY = curY;
                    nextMovingThisFrame = false;
                    return;
                }
            }
            newX = curX + (int)(((long)fx * stepFixed) >> 8);
            newY = curY + (int)(((long)fy * stepFixed) >> 8);
            nextMovingThisFrame = stepFixed > 0;
        }

        public static void StepInDirectionFixedHeading(int curX, int curY, int headingCur, int directionHeading, int stepFixed, int turnRate, out int newX, out int newY, out int newHeading)
        {
            StepInDirectionFixedHeading(curX, curY, headingCur, directionHeading, stepFixed, turnRate, true, false, out newX, out newY, out newHeading, out _);
        }

        public static void ResolveMovement(PathMap pathMap, int curFixedX, int curFixedY, int candFixedX, int candFixedY, out int outFixedX, out int outFixedY)
        {
            if (candFixedX == curFixedX && candFixedY == curFixedY)
            {
                outFixedX = curFixedX;
                outFixedY = curFixedY;
                return;
            }
            if (pathMap == null) { outFixedX = candFixedX; outFixedY = candFixedY; return; }
            pathMap.CastGroundRaySlideFixed(curFixedX, curFixedY, candFixedX, candFixedY, out outFixedX, out outFixedY);
        }

        public static void ResolveMovement(PathMap pathMap, int curFixedX, int curFixedY, int curFixedZ, int candFixedX, int candFixedY, out int outFixedX, out int outFixedY, out int outFixedZ)
        {
            if (candFixedX == curFixedX && candFixedY == curFixedY)
            {
                outFixedX = curFixedX;
                outFixedY = curFixedY;
                outFixedZ = curFixedZ;
                return;
            }
            if (pathMap == null)
            {
                outFixedX = candFixedX;
                outFixedY = candFixedY;
                outFixedZ = curFixedZ;
                return;
            }

            pathMap.CastGroundRaySlideFixed(curFixedX, curFixedY, curFixedZ, candFixedX, candFixedY, out outFixedX, out outFixedY, out int _);
            outFixedZ = pathMap.GetHeightAtFixed(outFixedX, outFixedY, curFixedZ);
        }
    }
}
