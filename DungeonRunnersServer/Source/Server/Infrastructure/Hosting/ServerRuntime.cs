using System;
using System.Collections;
using DungeonRunners.Engine;

namespace DungeonRunners.Infrastructure
{
    public static class ServerRuntime
    {
        public static DateTime ReadUtc()
        {
            return DateTime.UtcNow;
        }

        public static long ReadUnixTimeSeconds()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        public static WaitForSeconds DelaySeconds(int seconds)
        {
            return new WaitForSeconds(seconds);
        }

        public static Coroutine RunRoutine(MonoBehaviour owner, IEnumerator routine)
        {
            return DungeonRunners.Runtime.EngineRuntime.StartCoroutine(owner, routine);
        }
    }
}
