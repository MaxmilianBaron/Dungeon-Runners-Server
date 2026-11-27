using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using DungeonRunners.Engine;
using DungeonRunners.Utilities;
using DungeonRunners.Data;
using DungeonRunners.Core;
using System.Text;
using System.Reflection;
using System.Runtime.CompilerServices;
using Org.BouncyCastle.Utilities;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;
using DungeonRunners.Gameplay;
using DungeonRunners.Database;
using DungeonRunners.Engine.Playables;
using System.Security.Cryptography;
using DungeonRunners.Combat;
using DungeonRunners.Networking.EntitySynchInfo;

namespace DungeonRunners.Networking
{
    public partial class GameServer
    {
        private static bool VerboseSynchLogging => ServerDiagnostics.IsEnabled("verboseSynchLogging");
        private const uint NonCombatInteractiveHPWire = 60u * 256u;
        public void SendSocialViaAuthPublic(RRConnection conn, byte dest, byte messageType, byte[] data)
            => SendSocialViaAuth(conn, dest, messageType, data);
    }
}
