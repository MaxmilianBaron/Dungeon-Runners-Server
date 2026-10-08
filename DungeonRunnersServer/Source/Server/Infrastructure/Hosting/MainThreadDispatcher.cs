using System;
using System.Collections.Generic;
using DungeonRunners.Engine;

namespace DungeonRunners.Core
{
    public class MainThreadDispatcher : MonoBehaviour
    {
        private static MainThreadDispatcher _instance;
        private static readonly Queue<Action> _executionQueue = new Queue<Action>();
        private static readonly object _lock = new object();

        public static MainThreadDispatcher Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("MainThreadDispatcher");
                    _instance = go.AddComponent<MainThreadDispatcher>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        void Awake()
        {
            if (_instance == null)
            {
                _instance = this;
                DontDestroyOnLoad(gameObject);
            }
            else if (_instance != this)
            {
                Destroy(gameObject);
            }
        }

        void Update()
        {
            lock (_lock)
            {
                while (_executionQueue.Count > 0)
                {
                    try
                    {
                        _executionQueue.Dequeue().Invoke();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[MAIN-THREAD-DISPATCHER] state=failed message='{ex}'");
                    }
                }
            }
        }

        public static void Enqueue(Action action)
        {
            if (action == null) return;

            lock (_lock)
            {
                _executionQueue.Enqueue(action);
            }
        }

    }
}
