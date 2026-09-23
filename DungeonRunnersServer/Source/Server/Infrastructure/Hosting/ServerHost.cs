using DungeonRunners.Engine;
using DungeonRunners.Networking;

namespace DungeonRunners.Core
{
    public class ServerHost : MonoBehaviour
    {
        [Header("Configuration")]
        [SerializeField] private ServerConfig serverConfig;

        [Header("Server Components")]
        [SerializeField] private bool startAuthServer = true;
        [SerializeField] private bool startGameServer = true;

        private AuthServer _authServer;
        private GameServer _gameServer;

        void Awake()
        {
            RuntimeEvidence.EnsureStarted();
            if (RuntimeEvidence.ShouldAbortStartup)
            {
                StartupLog.Failure("Evidence", "startup validation failed; inspect logs/server.log");
                enabled = false;
                Application.Quit();
                return;
            }
            var dispatcher = MainThreadDispatcher.Instance;
        }

        void Start()
        {
            if (RuntimeEvidence.ShouldAbortStartup)
                return;

            if (serverConfig == null)
            {
                StartupLog.Failure("Configuration", "ServerConfig is missing");
                return;
            }

            RuntimeEvidence.LogBuildBinding("startup");
            StartupLog.Ready("Configuration", $"version={serverConfig.serverVersion} world={serverConfig.defaultWorldId} capacity={ServerSettings.Get("maxPlayers", serverConfig.maxPlayers)}");

            if (startAuthServer)
            {
                StartAuthServer();
            }

            if (startGameServer)
            {
                StartGameServer();
            }

        }

        private void StartAuthServer()
        {
            GameObject authGO = new GameObject("AuthServer");
            authGO.transform.SetParent(transform);
            _authServer = authGO.AddComponent<AuthServer>();

            var field = typeof(AuthServer).GetField("config",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field?.SetValue(_authServer, serverConfig);

        }

        private void StartGameServer()
        {
            GameObject gameGO = new GameObject("GameServer");
            gameGO.transform.SetParent(transform);
            _gameServer = gameGO.AddComponent<GameServer>();

            var field = typeof(GameServer).GetField("config",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field?.SetValue(_gameServer, serverConfig);

        }

    }
}
