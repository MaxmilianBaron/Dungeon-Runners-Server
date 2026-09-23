using System;
using System.Collections;
using System.Collections.Generic;

namespace DungeonRunners.Engine
{
    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public static Vector2 zero => new Vector2(0f, 0f);
        public static Vector2 one => new Vector2(1f, 1f);
        public float magnitude => (float)Math.Sqrt((double)x * x + (double)y * y);
        public float sqrMagnitude => x * x + y * y;
        public static Vector2 operator +(Vector2 a, Vector2 b) => new Vector2(a.x + b.x, a.y + b.y);
        public static Vector2 operator -(Vector2 a, Vector2 b) => new Vector2(a.x - b.x, a.y - b.y);
        public static Vector2 operator *(Vector2 a, float d) => new Vector2(a.x * d, a.y * d);
        public static Vector2 operator /(Vector2 a, float d) => new Vector2(a.x / d, a.y / d);
        public override string ToString() => $"({x:F2}, {y:F2})";
    }

    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public Vector3(float x, float y) { this.x = x; this.y = y; this.z = 0f; }
        public static Vector3 zero => new Vector3(0f, 0f, 0f);
        public static Vector3 one => new Vector3(1f, 1f, 1f);
        public static Vector3 up => new Vector3(0f, 1f, 0f);
        public static Vector3 forward => new Vector3(0f, 0f, 1f);
        public static Vector3 right => new Vector3(1f, 0f, 0f);
        public float magnitude => (float)Math.Sqrt((double)x * x + (double)y * y + (double)z * z);
        public float sqrMagnitude => x * x + y * y + z * z;
        public Vector3 normalized { get { float m = magnitude; return m > 1e-9f ? new Vector3(x / m, y / m, z / m) : zero; } }
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator -(Vector3 a) => new Vector3(-a.x, -a.y, -a.z);
        public static Vector3 operator *(Vector3 a, float d) => new Vector3(a.x * d, a.y * d, a.z * d);
        public static Vector3 operator *(float d, Vector3 a) => new Vector3(a.x * d, a.y * d, a.z * d);
        public static Vector3 operator /(Vector3 a, float d) => new Vector3(a.x / d, a.y / d, a.z / d);
        public static bool operator ==(Vector3 a, Vector3 b) => (a - b).sqrMagnitude < 9.99999944e-11f;
        public static bool operator !=(Vector3 a, Vector3 b) => !(a == b);
        public override bool Equals(object o) => o is Vector3 v && x == v.x && y == v.y && z == v.z;
        public override int GetHashCode() => x.GetHashCode() ^ (y.GetHashCode() << 2) ^ (z.GetHashCode() >> 2);
        public static float Distance(Vector3 a, Vector3 b) => (a - b).magnitude;
        public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        public static Vector3 Cross(Vector3 a, Vector3 b) => new Vector3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
        public static Vector3 Lerp(Vector3 a, Vector3 b, float t) { t = Mathf.Clamp01(t); return new Vector3(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t); }
        public static Vector3 Normalize(Vector3 v) => v.normalized;
        public override string ToString() => $"({x:F2}, {y:F2}, {z:F2})";
    }

    public struct Quaternion
    {
        public float x, y, z, w;
        public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public static Quaternion identity => new Quaternion(0f, 0f, 0f, 1f);
        public static Quaternion Euler(float x, float y, float z) => identity;
        public static Quaternion Euler(Vector3 e) => identity;
    }

    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public Color(float r, float g, float b) { this.r = r; this.g = g; this.b = b; this.a = 1f; }
        public static Color white => new Color(1f, 1f, 1f, 1f);
        public static Color black => new Color(0f, 0f, 0f, 1f);
        public static Color red => new Color(1f, 0f, 0f, 1f);
        public static Color green => new Color(0f, 1f, 0f, 1f);
        public static Color blue => new Color(0f, 0f, 1f, 1f);
        public static Color yellow => new Color(1f, 0.92f, 0.016f, 1f);
        public static Color clear => new Color(0f, 0f, 0f, 0f);
    }

    public static class Mathf
    {
        public const float PI = 3.14159265358979f;
        public const float Deg2Rad = PI / 180f;
        public const float Rad2Deg = 180f / PI;
        public const float Infinity = float.PositiveInfinity;
        public const float NegativeInfinity = float.NegativeInfinity;
        public const float Epsilon = 1.401298E-45f;

        public static float Max(float a, float b) => a > b ? a : b;
        public static int Max(int a, int b) => a > b ? a : b;
        public static float Max(params float[] v) { float m = float.NegativeInfinity; foreach (var x in v) if (x > m) m = x; return m; }
        public static float Min(float a, float b) => a < b ? a : b;
        public static int Min(int a, int b) => a < b ? a : b;
        public static float Min(params float[] v) { float m = float.PositiveInfinity; foreach (var x in v) if (x < m) m = x; return m; }
        public static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
        public static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
        public static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
        public static float Abs(float v) => Math.Abs(v);
        public static int Abs(int v) => Math.Abs(v);
        public static int RoundToInt(float v) => (int)Math.Round((double)v, MidpointRounding.ToEven);
        public static int FloorToInt(float v) => (int)Math.Floor((double)v);
        public static int CeilToInt(float v) => (int)Math.Ceiling((double)v);
        public static float Round(float v) => (float)Math.Round((double)v, MidpointRounding.ToEven);
        public static float Floor(float v) => (float)Math.Floor((double)v);
        public static float Ceil(float v) => (float)Math.Ceiling((double)v);
        public static float Sqrt(float v) => (float)Math.Sqrt(v);
        public static float Pow(float a, float b) => (float)Math.Pow(a, b);
        public static float Sin(float v) => (float)Math.Sin(v);
        public static float Cos(float v) => (float)Math.Cos(v);
        public static float Tan(float v) => (float)Math.Tan(v);
        public static float Atan(float v) => (float)Math.Atan(v);
        public static float Atan2(float y, float x) => (float)Math.Atan2(y, x);
        public static float Asin(float v) => (float)Math.Asin(v);
        public static float Acos(float v) => (float)Math.Acos(v);
        public static float Lerp(float a, float b, float t) { t = Clamp01(t); return a + (b - a) * t; }
        public static float LerpUnclamped(float a, float b, float t) => a + (b - a) * t;
        public static float Sign(float v) => v >= 0f ? 1f : -1f;
        public static float Repeat(float t, float length) => Clamp(t - Floor(t / length) * length, 0f, length);
        public static bool Approximately(float a, float b) => Math.Abs(b - a) < Math.Max(1e-6f * Math.Max(Math.Abs(a), Math.Abs(b)), Epsilon * 8f);
    }

    public enum LogType { Error, Assert, Warning, Log, Exception }

    public interface ILogHandler
    {
        void LogFormat(LogType logType, Object context, string format, params object[] args);
        void LogException(Exception exception, Object context);
    }

    public interface ILogger : ILogHandler
    {
        ILogHandler logHandler { get; set; }
        bool logEnabled { get; set; }
        LogType filterLogType { get; set; }
        void Log(object message);
        void Log(LogType logType, object message);
        void LogWarning(string tag, object message);
        void LogError(string tag, object message);
        void LogException(Exception exception);
        bool IsLogTypeAllowed(LogType logType);
    }

    public static class Debug
    {
        public static ILogger logger { get; } = new ShimLogger();
        public static void Log(object message) => logger.Log(LogType.Log, message);
        public static void Log(object message, object context) => logger.Log(LogType.Log, message);
        public static void LogWarning(object message) => logger.Log(LogType.Warning, message);
        public static void LogWarning(object message, object context) => logger.Log(LogType.Warning, message);
        public static void LogError(object message) => logger.Log(LogType.Error, message);
        public static void LogError(object message, object context) => logger.Log(LogType.Error, message);
        public static void LogException(Exception e) => logger.LogException(e);
        public static void LogException(Exception e, object context) => logger.LogException(e);
        public static void Assert(bool condition) { if (!condition) logger.Log(LogType.Assert, "Assertion failed"); }

        private static string Str(object o) => o == null ? "null" : o.ToString();
        private static void Raise(string text, LogType t) => Application.RaiseLog(text, string.Empty, t);

        private sealed class DefaultLogHandler : ILogHandler
        {
            public void LogFormat(LogType logType, Object context, string format, params object[] args)
            {
                string text = format;
                if (args != null && args.Length > 0)
                {
                    try { text = string.Format(format, args); } catch { text = format; }
                }
                DungeonRunners.Runtime.ServerLog.Write(logType, text);
            }
            public void LogException(Exception exception, Object context) => DungeonRunners.Runtime.ServerLog.Write(LogType.Exception, exception);
        }

        private sealed class ShimLogger : ILogger
        {
            public ILogHandler logHandler { get; set; } = new DefaultLogHandler();
            public bool logEnabled { get; set; } = true;
            public LogType filterLogType { get; set; } = LogType.Log;
            public bool IsLogTypeAllowed(LogType logType) => logEnabled;
            public void Log(object message) => Log(LogType.Log, message);
            public void Log(LogType logType, object message)
            {
                Raise(Str(message), logType);
                logHandler.LogFormat(logType, null, "{0}", Str(message));
            }
            public void LogWarning(string tag, object message) { Raise($"{tag}: {message}", LogType.Warning); logHandler.LogFormat(LogType.Warning, null, "{0}: {1}", tag, message); }
            public void LogError(string tag, object message) { Raise($"{tag}: {message}", LogType.Error); logHandler.LogFormat(LogType.Error, null, "{0}: {1}", tag, message); }
            public void LogException(Exception exception) { Raise(Str(exception), LogType.Exception); logHandler.LogException(exception, null); }
            public void LogFormat(LogType logType, Object context, string format, params object[] args) => logHandler.LogFormat(logType, context, format, args);
            void ILogHandler.LogException(Exception exception, Object context) => logHandler.LogException(exception, context);
        }
    }

    public static class Time
    {
        public static float time => DungeonRunners.Runtime.EngineRuntime.Time;
        public static float unscaledTime => DungeonRunners.Runtime.EngineRuntime.Time;
        public static float realtimeSinceStartup => DungeonRunners.Runtime.EngineRuntime.Time;
        public static float deltaTime => DungeonRunners.Runtime.EngineRuntime.DeltaTime;
        public static float unscaledDeltaTime => DungeonRunners.Runtime.EngineRuntime.DeltaTime;
        public static float fixedDeltaTime => DungeonRunners.Runtime.EngineRuntime.FixedDeltaTime;
        public static int frameCount => DungeonRunners.Runtime.EngineRuntime.FrameCount;
        public static float timeScale { get; set; } = 1f;
    }

    public static class Application
    {
        public static string dataPath => DungeonRunners.Runtime.EngineRuntime.DataPath;
        public static string persistentDataPath => DungeonRunners.Runtime.EngineRuntime.PersistentDataPath;
        public static string streamingAssetsPath => DungeonRunners.Runtime.EngineRuntime.DataPath;
        public static string version => "net10.0-shim";
        public static bool isEditor => false;
        public static bool isPlaying => true;
        public static event Action quitting;
        public static event Application.LogCallback logMessageReceivedThreaded;
        public delegate void LogCallback(string condition, string stackTrace, LogType type);
        public static void Quit() => DungeonRunners.Runtime.EngineRuntime.RequestQuit(0);
        public static void Quit(int exitCode) => DungeonRunners.Runtime.EngineRuntime.RequestQuit(exitCode);
        internal static void RaiseQuitting() => quitting?.Invoke();
        internal static void RaiseLog(string condition, string stack, LogType type) => logMessageReceivedThreaded?.Invoke(condition, stack, type);
    }

    public static class Random
    {
        private static System.Random _rng = new System.Random(0);
        public static float value => (float)_rng.NextDouble();
        public static int Range(int minInclusive, int maxExclusive) => _rng.Next(minInclusive, maxExclusive);
        public static float Range(float minInclusive, float maxInclusive) => minInclusive + (float)_rng.NextDouble() * (maxInclusive - minInclusive);
        public static void InitState(int seed) => _rng = new System.Random(seed);
    }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false)]
    public sealed class SerializeField : Attribute { }
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
    public sealed class RangeAttribute : Attribute { public RangeAttribute(float min, float max) { } }
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
    public sealed class HeaderAttribute : Attribute { public HeaderAttribute(string header) { } }
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
    public sealed class TooltipAttribute : Attribute { public TooltipAttribute(string tooltip) { } }
    [AttributeUsage(AttributeTargets.All, AllowMultiple = false)]
    public sealed class HideInInspector : Attribute { }
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class DisallowMultipleComponent : Attribute { }
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class DefaultExecutionOrder : Attribute { public DefaultExecutionOrder(int order) { } }
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class CreateAssetMenu : Attribute { public string fileName; public string menuName; public int order; }
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public sealed class MultilineAttribute : Attribute { public MultilineAttribute() { } public MultilineAttribute(int lines) { } }
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public sealed class TextAreaAttribute : Attribute { public TextAreaAttribute() { } public TextAreaAttribute(int min, int max) { } }
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public sealed class SpaceAttribute : Attribute { public SpaceAttribute() { } public SpaceAttribute(float height) { } }

    public abstract class YieldInstruction { }
    public sealed class WaitForSeconds : YieldInstruction { public readonly float seconds; public WaitForSeconds(float s) { seconds = s; } }
    public sealed class WaitForSecondsRealtime : YieldInstruction { public readonly float seconds; public WaitForSecondsRealtime(float s) { seconds = s; } }
    public sealed class WaitForEndOfFrame : YieldInstruction { }
    public sealed class WaitForFixedUpdate : YieldInstruction { }
    public abstract class CustomYieldInstruction : YieldInstruction, IEnumerator
    {
        public abstract bool keepWaiting { get; }
        public object Current => null;
        public bool MoveNext() => keepWaiting;
        public void Reset() { }
    }
    public sealed partial class Coroutine { internal IEnumerator Routine; internal Coroutine(IEnumerator r) { Routine = r; } }

    public class Object
    {
        public string name { get; set; } = "";
        public int GetInstanceID() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
        public override string ToString() => name;
        public static void Destroy(Object obj) => DungeonRunners.Runtime.EngineRuntime.Destroy(obj);
        public static void Destroy(Object obj, float delay) => DungeonRunners.Runtime.EngineRuntime.Destroy(obj);
        public static void DestroyImmediate(Object obj) => DungeonRunners.Runtime.EngineRuntime.Destroy(obj);
        public static void DontDestroyOnLoad(Object obj) { }
        public static T FindObjectOfType<T>() where T : Object => DungeonRunners.Runtime.EngineRuntime.FindObjectOfType<T>();
        public static bool operator ==(Object a, Object b) => ReferenceEquals(a, b) || (a is null && b is null);
        public static bool operator !=(Object a, Object b) => !(a == b);
        public override bool Equals(object o) => ReferenceEquals(this, o);
        public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
        public static implicit operator bool(Object o) => o is not null;
    }

    public class Transform : Component
    {
        public Vector3 position { get; set; }
        public Vector3 localPosition { get; set; }
        public Quaternion rotation { get; set; } = Quaternion.identity;
        public Vector3 eulerAngles { get; set; }
        public Vector3 localScale { get; set; } = Vector3.one;
        public Transform parent { get; set; }
        public int childCount => 0;
        public void SetParent(Transform p) { parent = p; }
        public void SetParent(Transform p, bool worldPositionStays) { parent = p; }
    }

    public class Component : Object
    {
        public GameObject gameObject { get; internal set; }
        public Transform transform => gameObject != null ? gameObject.transform : null;
        public T GetComponent<T>() where T : Component => gameObject != null ? gameObject.GetComponent<T>() : null;
        public Component GetComponent(Type t) => gameObject != null ? gameObject.GetComponent(t) : null;
        public bool TryGetComponent<T>(out T component) where T : Component { component = GetComponent<T>(); return component != null; }
        public T GetComponentInChildren<T>() where T : Component => GetComponent<T>();
        public T AddComponent<T>() where T : Component => gameObject.AddComponent<T>();
    }

    public class Behaviour : Component
    {
        public bool enabled { get; set; } = true;
        public bool isActiveAndEnabled => enabled && gameObject != null && gameObject.activeInHierarchy;
    }

    public class GameObject : Object
    {
        internal readonly List<Component> _components = new List<Component>();
        public Transform transform { get; private set; }
        public bool activeSelf { get; private set; } = true;
        public bool activeInHierarchy => activeSelf;
        public string tag { get; set; } = "Untagged";
        public int layer { get; set; }

        public GameObject() : this("GameObject") { }
        public GameObject(string name)
        {
            this.name = name;
            transform = new Transform();
            transform.gameObject = this;
            _components.Add(transform);
            DungeonRunners.Runtime.EngineRuntime.Register(this);
        }

        public T AddComponent<T>() where T : Component
        {
            var c = (T)Activator.CreateInstance(typeof(T));
            c.gameObject = this;
            _components.Add(c);
            DungeonRunners.Runtime.EngineRuntime.OnComponentAdded(c);
            return c;
        }
        public Component AddComponent(Type t)
        {
            var c = (Component)Activator.CreateInstance(t);
            c.gameObject = this;
            _components.Add(c);
            DungeonRunners.Runtime.EngineRuntime.OnComponentAdded(c);
            return c;
        }
        public T GetComponent<T>() where T : Component { foreach (var c in _components) if (c is T t) return t; return null; }
        public Component GetComponent(Type t) { foreach (var c in _components) if (t.IsInstanceOfType(c)) return c; return null; }
        public bool TryGetComponent<T>(out T component) where T : Component { component = GetComponent<T>(); return component != null; }
        public void SetActive(bool active) => activeSelf = active;
    }

    public class ScriptableObject : Object
    {
        public static T CreateInstance<T>() where T : ScriptableObject => (T)Activator.CreateInstance(typeof(T));
        public static ScriptableObject CreateInstance(Type t) => (ScriptableObject)Activator.CreateInstance(t);
    }

    public class MonoBehaviour : Behaviour
    {
        public Coroutine StartCoroutine(IEnumerator routine) => DungeonRunners.Runtime.EngineRuntime.StartCoroutine(this, routine);
        public Coroutine StartCoroutine(string methodName) => null;
        public void StopCoroutine(Coroutine routine) => DungeonRunners.Runtime.EngineRuntime.StopCoroutine(routine);
        public void StopCoroutine(IEnumerator routine) => DungeonRunners.Runtime.EngineRuntime.StopCoroutine(routine);
        public void StopAllCoroutines() => DungeonRunners.Runtime.EngineRuntime.StopAllCoroutines(this);
        public void Invoke(string methodName, float time) { }
        public void CancelInvoke() { }
        public bool IsInvoking() => false;
        public void print(object message) => Debug.Log(message);
    }
}

namespace DungeonRunners.Engine.Playables
{
    public struct PlayableGraph { public bool IsValid() => false; }
    public enum DirectorUpdateMode { DspClock, GameTime, UnscaledGameTime, Manual }
}
