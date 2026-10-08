using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using DungeonRunners.Engine;

namespace DungeonRunners.Runtime
{
    public static class ServerLog
    {
        private static readonly object _gate = new object();
        private static TextWriter _file;
        private static readonly System.Collections.Concurrent.BlockingCollection<LogEntry> _queue =
            new System.Collections.Concurrent.BlockingCollection<LogEntry>(new System.Collections.Concurrent.ConcurrentQueue<LogEntry>());
        private static Thread _writerThread;
        private static volatile bool _startupConsoleMode = true;
        private const int ConsoleSkipQueueDepth = 4096;

        private readonly struct LogEntry
        {
            public readonly string Line;
            public readonly bool ShowOnConsole;

            public LogEntry(string line, bool showOnConsole)
            {
                Line = line;
                ShowOnConsole = showOnConsole;
            }
        }

        public static void Init(string logFilePath)
        {
            lock (_gate)
            {
                _startupConsoleMode = true;
                try
                {
                    string logDir = Path.GetDirectoryName(logFilePath);
                    Directory.CreateDirectory(logDir);
                    _file = new StreamWriter(new FileStream(logFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = false };
                }
                catch { _file = null; }

                if (_writerThread == null)
                {
                    _writerThread = new Thread(DrainLoop) { IsBackground = true, Name = "ServerLogWriter" };
                    _writerThread.Start();
                }
            }
        }

        private static void DrainLoop()
        {
            var batch = new System.Text.StringBuilder(16384);
            var consoleBatch = new System.Text.StringBuilder(4096);
            foreach (LogEntry first in _queue.GetConsumingEnumerable())
            {
                batch.Clear();
                consoleBatch.Clear();
                batch.AppendLine(first.Line);
                if (first.ShowOnConsole)
                    consoleBatch.AppendLine(first.Line);
                while (batch.Length < 65536 && _queue.TryTake(out LogEntry entry))
                {
                    batch.AppendLine(entry.Line);
                    if (entry.ShowOnConsole)
                        consoleBatch.AppendLine(entry.Line);
                }
                string text = batch.ToString();
                lock (_gate)
                {
                    try
                    {
                        _file?.Write(text);
                        _file?.Flush();
                    }
                    catch { }
                }
                if (_queue.Count < ConsoleSkipQueueDepth && consoleBatch.Length > 0)
                {
                    try { Console.Out.Write(consoleBatch.ToString()); } catch { }
                }
            }
            lock (_gate)
            {
                try { _file?.Flush(); } catch { }
            }
        }

        public static void Write(LogType type, object message)
        {
            string text = message == null ? "null" : message.ToString();
            string line = $"{DateTime.Now:HH:mm:ss} {text}";
            bool showOnConsole = !_startupConsoleMode
                || text.StartsWith("[BOOT]", StringComparison.Ordinal)
                || type == LogType.Exception;
            try
            {
                if (!_queue.IsAddingCompleted)
                    _queue.Add(new LogEntry(line, showOnConsole));
            }
            catch (InvalidOperationException) { }
        }

        public static void CompleteStartup()
        {
            _startupConsoleMode = false;
        }

        public static void Shutdown()
        {
            try { _queue.CompleteAdding(); } catch { }
            _writerThread?.Join(2000);
        }
    }

    public static class EngineRuntime
    {
        private static readonly object _gate = new object();
        private static readonly List<GameObject> _objects = new List<GameObject>();
        private static readonly List<Component> _pendingAwake = new List<Component>();
        private static readonly List<Component> _pendingStart = new List<Component>();
        private static readonly List<Coroutine> _coroutines = new List<Coroutine>();
        private static readonly List<Coroutine> _coroutinesToAdd = new List<Coroutine>();
        private static readonly Dictionary<Type, LifecycleHooks> _hookCache = new Dictionary<Type, LifecycleHooks>();

        public static float Time { get; private set; }
        public static float DeltaTime { get; private set; }
        public static float FixedDeltaTime { get; set; } = 0.02f;
        public static int FrameCount { get; private set; }
        public static string DataPath { get; set; } = AppContext.BaseDirectory;
        public static string PersistentDataPath { get; set; } = AppContext.BaseDirectory;
        public static bool QuitRequested { get; private set; }
        public static int ExitCode { get; private set; }

        public static void Register(GameObject go) { lock (_gate) _objects.Add(go); }

        public static void OnComponentAdded(Component c)
        {
            if (c is MonoBehaviour) lock (_gate) { _pendingAwake.Add(c); }
        }

        public static T FindObjectOfType<T>() where T : DungeonRunners.Engine.Object
        {
            lock (_gate)
                foreach (var go in _objects)
                    foreach (var c in go._components)
                        if (c is T t) return t;
            return null;
        }

        public static void Destroy(DungeonRunners.Engine.Object obj)
        {
            lock (_gate)
            {
                if (obj is GameObject go) { _objects.Remove(go); foreach (var c in go._components) Invoke(c, GetHooks(c.GetType()).OnDestroy); }
                else if (obj is Component comp) { comp.gameObject?._components.Remove(comp); Invoke(comp, GetHooks(comp.GetType()).OnDestroy); }
            }
        }

        public static void RequestQuit(int exitCode)
        {
            ExitCode = exitCode;
            QuitRequested = true;
            try { Application.RaiseQuitting(); } catch { }
        }

        public static void SetExitCode(int exitCode)
        {
            ExitCode = exitCode;
        }

        public static Coroutine StartCoroutine(MonoBehaviour owner, IEnumerator routine)
        {
            if (routine == null) return null;
            var co = new Coroutine(routine) { Owner = owner, Stack = new Stack<IEnumerator>() };
            co.Stack.Push(routine);
            lock (_gate) _coroutinesToAdd.Add(co);
            return co;
        }

        public static void StopCoroutine(Coroutine co) { lock (_gate) { _coroutines.Remove(co); _coroutinesToAdd.Remove(co); } }

        public static void StopCoroutine(IEnumerator routine)
        {
            lock (_gate)
            {
                _coroutines.RemoveAll(c => c.Routine == routine);
                _coroutinesToAdd.RemoveAll(c => c.Routine == routine);
            }
        }

        public static void StopAllCoroutines(MonoBehaviour owner)
        {
            lock (_gate)
            {
                _coroutines.RemoveAll(c => ReferenceEquals(c.Owner, owner));
                _coroutinesToAdd.RemoveAll(c => ReferenceEquals(c.Owner, owner));
            }
        }

        public static void Tick(float deltaTime, float now)
        {
            DeltaTime = deltaTime;
            Time = now;
            FrameCount++;

            Component[] awakeBatch;
            lock (_gate) { awakeBatch = _pendingAwake.ToArray(); _pendingAwake.Clear(); }
            foreach (var c in awakeBatch)
            {
                var h = GetHooks(c.GetType());
                Invoke(c, h.Awake);
                Invoke(c, h.OnEnable);
                lock (_gate) _pendingStart.Add(c);
            }

            Component[] startBatch;
            lock (_gate) { startBatch = _pendingStart.ToArray(); _pendingStart.Clear(); }
            foreach (var c in startBatch) Invoke(c, GetHooks(c.GetType()).Start);

            Component[] updateTargets;
            lock (_gate) { updateTargets = CollectActive(); }
            foreach (var c in updateTargets) Invoke(c, GetHooks(c.GetType()).Update);

            PumpCoroutines();

            foreach (var c in updateTargets) Invoke(c, GetHooks(c.GetType()).LateUpdate);
        }

        public static void FixedTick()
        {
            Component[] targets;
            lock (_gate) { targets = CollectActive(); }
            foreach (var c in targets) Invoke(c, GetHooks(c.GetType()).FixedUpdate);
        }

        public static void Shutdown()
        {
            Component[] all;
            lock (_gate)
            {
                var list = new List<Component>();
                foreach (var go in _objects)
                    foreach (var c in go._components)
                        if (c is MonoBehaviour) list.Add(c);
                all = list.ToArray();
            }
            foreach (var c in all) Invoke(c, GetHooks(c.GetType()).OnApplicationQuit);
            foreach (var c in all) Invoke(c, GetHooks(c.GetType()).OnDisable);
            foreach (var c in all) Invoke(c, GetHooks(c.GetType()).OnDestroy);
        }

        private static Component[] CollectActive()
        {
            var list = new List<Component>();
            foreach (var go in _objects)
            {
                if (!go.activeInHierarchy) continue;
                foreach (var c in go._components)
                    if (c is MonoBehaviour mb && mb.enabled) list.Add(c);
            }
            return list.ToArray();
        }

        private static void PumpCoroutines()
        {
            lock (_gate) { if (_coroutinesToAdd.Count > 0) { _coroutines.AddRange(_coroutinesToAdd); _coroutinesToAdd.Clear(); } }
            Coroutine[] active;
            lock (_gate) active = _coroutines.ToArray();
            foreach (var co in active)
            {
                if (co.ResumeAt > Time) continue;
                if (!Step(co)) lock (_gate) { _coroutines.Remove(co); }
            }
        }

        private static bool Step(Coroutine co)
        {
            while (co.Stack.Count > 0)
            {
                var top = co.Stack.Peek();
                bool moved;
                try { moved = top.MoveNext(); }
                catch (Exception e) { Debug.LogException(e); return false; }
                if (!moved) { co.Stack.Pop(); continue; }

                object yielded = top.Current;
                switch (yielded)
                {
                    case null:
                        return true;
                    case WaitForSeconds w:
                        co.ResumeAt = Time + w.seconds; return true;
                    case WaitForSecondsRealtime wr:
                        co.ResumeAt = Time + wr.seconds; return true;
                    case WaitForEndOfFrame:
                    case WaitForFixedUpdate:
                        return true;
                    case Coroutine inner:
                        if (inner.Stack != null && inner.Stack.Count > 0) { return true; }
                        return true;
                    case IEnumerator nested:
                        co.Stack.Push(nested); continue;
                    default:
                        return true;
                }
            }
            return false;
        }

        private static void Invoke(Component c, MethodInfo m)
        {
            if (m == null) return;
            try { m.Invoke(c, null); }
            catch (TargetInvocationException tie) { Debug.LogException(tie.InnerException ?? tie); }
            catch (Exception e) { Debug.LogException(e); }
        }

        private static LifecycleHooks GetHooks(Type t)
        {
            lock (_hookCache)
            {
                if (_hookCache.TryGetValue(t, out var h)) return h;
                const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
                h = new LifecycleHooks
                {
                    Awake = Find(t, "Awake", F),
                    OnEnable = Find(t, "OnEnable", F),
                    Start = Find(t, "Start", F),
                    Update = Find(t, "Update", F),
                    FixedUpdate = Find(t, "FixedUpdate", F),
                    LateUpdate = Find(t, "LateUpdate", F),
                    OnDisable = Find(t, "OnDisable", F),
                    OnDestroy = Find(t, "OnDestroy", F),
                    OnApplicationQuit = Find(t, "OnApplicationQuit", F),
                };
                _hookCache[t] = h;
                return h;
            }
        }

        private static MethodInfo Find(Type t, string name, BindingFlags f)
        {
            var m = t.GetMethod(name, f, null, Type.EmptyTypes, null);
            return (m != null && m.GetParameters().Length == 0) ? m : null;
        }

        private sealed class LifecycleHooks
        {
            public MethodInfo Awake, OnEnable, Start, Update, FixedUpdate, LateUpdate, OnDisable, OnDestroy, OnApplicationQuit;
        }
    }
}

namespace DungeonRunners.Engine
{
    public sealed partial class Coroutine
    {
        internal MonoBehaviour Owner;
        internal Stack<System.Collections.IEnumerator> Stack;
        internal float ResumeAt;
    }
}
