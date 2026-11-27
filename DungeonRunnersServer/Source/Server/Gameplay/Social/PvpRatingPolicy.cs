using System;
using System.Collections.Generic;
using DungeonRunners.Utilities;

namespace DungeonRunners.Gameplay
{
    public readonly struct PvpTeamRatingParameters
    {
        public readonly int WeightFixed32;
        public readonly int ExpectedPercentFixed32;

        public PvpTeamRatingParameters(int weightFixed32, int expectedPercentFixed32)
        {
            if ((weightFixed32 != 0 && weightFixed32 != 256)
                || expectedPercentFixed32 < 0 || expectedPercentFixed32 > 0x6400)
                throw new ArgumentOutOfRangeException();
            WeightFixed32 = weightFixed32;
            ExpectedPercentFixed32 = expectedPercentFixed32;
        }

        public int GetDeltaFixed32(int outcomePercentFixed32)
        {
            if (outcomePercentFixed32 != 0 && outcomePercentFixed32 != 0x3200
                && outcomePercentFixed32 != 0x6400)
                throw new ArgumentOutOfRangeException(nameof(outcomePercentFixed32));
            var deviation = new Fixed32(unchecked(outcomePercentFixed32 - ExpectedPercentFixed32));
            return ((new Fixed32(0x2000) * deviation * new Fixed32(WeightFixed32))
                / new Fixed32(0x6400)).RawValue;
        }
    }

    public sealed class PvpMatchRatingParameters
    {
        public static PvpMatchRatingParameters Unrated { get; } = new PvpMatchRatingParameters(default, default);
        public PvpTeamRatingParameters Red { get; }
        public PvpTeamRatingParameters Blue { get; }

        private PvpMatchRatingParameters(PvpTeamRatingParameters red, PvpTeamRatingParameters blue)
        {
            Red = red;
            Blue = blue;
        }

        public static PvpMatchRatingParameters Freeze(IReadOnlyList<int> redRatingsFixed32,
            IReadOnlyList<int> blueRatingsFixed32, bool ranked)
        {
            if (redRatingsFixed32 == null || redRatingsFixed32.Count == 0
                || blueRatingsFixed32 == null || blueRatingsFixed32.Count == 0)
                throw new ArgumentException("Both rating rosters are required");
            long redSum = 0;
            long blueSum = 0;
            for (int index = 0; index < redRatingsFixed32.Count; index++)
                redSum = checked(redSum + redRatingsFixed32[index]);
            for (int index = 0; index < blueRatingsFixed32.Count; index++)
                blueSum = checked(blueSum + blueRatingsFixed32[index]);
            double difference = (double)blueSum / blueRatingsFixed32.Count
                - (double)redSum / redRatingsFixed32.Count;
            double expected = 0x6400 / (1.0 + Math.Pow(10.0, difference / (400.0 * 256.0)));
            int redExpected = (int)Math.Floor(expected + 0.5);
            int weight = ranked ? 256 : 0;
            return new PvpMatchRatingParameters(new PvpTeamRatingParameters(weight, redExpected),
                new PvpTeamRatingParameters(weight, 0x6400 - redExpected));
        }
    }
}
