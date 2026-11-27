using System;

namespace DungeonRunners.Data
{
    public static class PlayerOptions
    {
        public static string DifficultyName(byte difficulty)
        {
            return difficulty switch
            {
                0 => "Normal",
                1 => "Formidable",
                2 => "Extreme",
                3 => "Insane",
                _ => throw new ArgumentOutOfRangeException(nameof(difficulty))
            };
        }
    }
}
