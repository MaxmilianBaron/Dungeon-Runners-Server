namespace DungeonRunners.Core
{
    public static class ServerDiagnostics
    {
        internal static readonly string[] SwitchKeys =
        {
            "enableDebugLog",
            "focusedDebugLog",
            "runtimeEvidenceTracking",
            "simulationParityTracking",
            "desyncForensicsTracking",
            "fallbackHitTracking",
            "trackerLogging",
            "wirePacketTracking",
            "databaseTracking",
            "pathMapTracking",
            "collisionTracking",
            "fsmTracking",
            "skillEffectTracking",
            "questLootTracking",
            "respawnTracking",
            "encounterTracking",
            "verboseEvidenceLogging",
            "verbosePacketLogging",
            "verboseMonsterWireLogging",
            "verboseSynchLogging",
            "verboseMerchantItemLogging",
            "verboseRngLogging",
            "verboseGnomeLogging",
            "verboseMovementLogging",
            "verboseProjectileLogging",
            "verboseWanderLogging",
            "verboseCombatClockLogging",
            "verboseMonsterDiagnostics"
        };
        private static volatile bool _hasMasterOverride;
        private static volatile bool _masterOverride;

        public static bool Enabled => _hasMasterOverride
            ? _masterOverride
            : ServerSettings.GetCfgBool("enableDebugLog", false);

        public static bool IsEnabled(string key, bool defaultValue = false)
        {
            return Enabled && ServerSettings.GetCfgBool(key, defaultValue);
        }

        public static void SetMasterOverride(bool enabled)
        {
            _masterOverride = enabled;
            _hasMasterOverride = true;
        }

        public static void ClearMasterOverride()
        {
            _hasMasterOverride = false;
        }
    }
}
