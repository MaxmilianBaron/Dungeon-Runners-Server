using DungeonRunners.Engine;

namespace DungeonRunners.Core
{
    [CreateAssetMenu(fileName = "ServerConfig", menuName = "DungeonRunners/Server Configuration")]
    public class ServerConfig : ScriptableObject
    {
        [Header("Auth Server Settings")]
        public string authServerIP = "0.0.0.0";
        public int authServerPort = 2110;
        
        [Header("Game Server Settings")]
        public string gameServerIP = "0.0.0.0";
        public int gameServerPort = 2603;
        public string gameServerName = "Dungeon Runners Server";
        
        [Header("Encryption Keys")]
        public string blowfishKey = "[;'.]94-31==-%&@!^+]";
        public string desKey = "TEST";
        
        [Header("Server Info")]
        public int maxPlayers = 100;
        public string serverVersion = "1.0.0";
        public bool enableDebugLogging = false;
        
        [Header("World Settings")]
        public int defaultWorldId = 1;
    }
}
