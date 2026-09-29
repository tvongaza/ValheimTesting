// Valheim.Testing.Doubles: compile-time stand-ins for the Unity, Valheim, BepInEx and Jotunn types a mod's
// pure-logic sources use, so those sources compile and run in an ordinary test project without the game.
// Source package: these files are compiled into the consuming test project. Every type is partial; add the
// members your mod needs in your own files. Behaviour mirrors the game where mod code depends on it.
#nullable enable
// ReSharper disable InconsistentNaming
// UnityEngine: managed math as Unity computes it, and scene objects (the scene itself is in SceneDoubles.cs).
namespace UnityEngine;

public partial struct Vector2
{
    public float x;
    public float y;

    public Vector2(float x, float y)
    {
        this.x = x;
        this.y = y;
    }

    public float sqrMagnitude => x * x + y * y;
    public float magnitude => (float)System.Math.Sqrt(x * x + y * y);

    public void Normalize()
    {
        float m = magnitude;
        if (m > 1e-5f) { x /= m; y /= m; }
        else { x = 0; y = 0; }
    }

    public static float Distance(Vector2 a, Vector2 b)
    {
        float dx = a.x - b.x, dy = a.y - b.y;
        return (float)System.Math.Sqrt(dx * dx + dy * dy);
    }

    public Vector2 normalized
    {
        get
        {
            var v = this;
            v.Normalize();
            return v;
        }
    }

    public static float SqrMagnitude(Vector2 a) => a.x * a.x + a.y * a.y;

    public static float Dot(Vector2 a, Vector2 b) => a.x * b.x + a.y * b.y;

    public static Vector2 Lerp(Vector2 a, Vector2 b, float t)
    {
        t = Mathf.Clamp01(t);
        return new Vector2(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t);
    }

    public static Vector2 operator +(Vector2 a, Vector2 b) => new(a.x + b.x, a.y + b.y);
    public static Vector2 operator -(Vector2 a, Vector2 b) => new(a.x - b.x, a.y - b.y);
    public static Vector2 operator -(Vector2 a) => new(-a.x, -a.y);
    public static Vector2 operator *(Vector2 a, float d) => new(a.x * d, a.y * d);
    public static Vector2 operator *(float d, Vector2 a) => new(a.x * d, a.y * d);

    public override bool Equals(object? other) =>
        other is Vector2 v && v.x == x && v.y == y;

    public override int GetHashCode() => x.GetHashCode() ^ (y.GetHashCode() << 2);

    public static bool operator ==(Vector2 a, Vector2 b) => a.x == b.x && a.y == b.y;
    public static bool operator !=(Vector2 a, Vector2 b) => !(a == b);

    public override string ToString() => $"({x:F1}, {y:F1})";
}

public partial struct Vector3
{
    public float x;
    public float y;
    public float z;

    public Vector3(float x, float y, float z)
    {
        this.x = x;
        this.y = y;
        this.z = z;
    }

    public static Vector3 zero => new(0f, 0f, 0f);

    public static float Distance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
        return (float)System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    public static float SqrMagnitude(Vector3 a) => a.x * a.x + a.y * a.y + a.z * a.z;

    public static Vector3 Lerp(Vector3 a, Vector3 b, float t)
    {
        t = Mathf.Clamp01(t);
        return new Vector3(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t);
    }

    public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.x + b.x, a.y + b.y, a.z + b.z);
    public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.x - b.x, a.y - b.y, a.z - b.z);
    public static Vector3 operator *(Vector3 a, float d) => new(a.x * d, a.y * d, a.z * d);

    public override string ToString() => $"({x:F1}, {y:F1}, {z:F1})";
}

public partial struct Color
{
    public float r, g, b, a;

    public Color(float r, float g, float b, float a = 1f)
    {
        this.r = r;
        this.g = g;
        this.b = b;
        this.a = a;
    }

    public static Color Lerp(Color x, Color y, float t)
    {
        t = Mathf.Clamp01(t);
        return new Color(
            x.r + (y.r - x.r) * t,
            x.g + (y.g - x.g) * t,
            x.b + (y.b - x.b) * t,
            x.a + (y.a - x.a) * t);
    }
}

public partial struct Vector2Int
{
    public int x;
    public int y;

    public Vector2Int(int x, int y)
    {
        this.x = x;
        this.y = y;
    }
}

public static partial class Mathf
{
    public const float PI = (float)System.Math.PI;

    public static float Sqrt(float f) => (float)System.Math.Sqrt(f);
    public static float Abs(float f) => System.Math.Abs(f);
    public static float Min(float a, float b) => System.Math.Min(a, b);
    public static float Max(float a, float b) => System.Math.Max(a, b);
    public static int Min(int a, int b) => System.Math.Min(a, b);
    public static int Max(int a, int b) => System.Math.Max(a, b);

    public static float Min(params float[] values)
    {
        float m = values[0];
        for (int i = 1; i < values.Length; i++) m = System.Math.Min(m, values[i]);
        return m;
    }

    public static float Max(params float[] values)
    {
        float m = values[0];
        for (int i = 1; i < values.Length; i++) m = System.Math.Max(m, values[i]);
        return m;
    }

    public static int CeilToInt(float f) => (int)System.Math.Ceiling(f);
    public static int FloorToInt(float f) => (int)System.Math.Floor(f);
    public static int Clamp(int value, int min, int max) =>
        value < min ? min : value > max ? max : value;
    public static float Cos(float f) => (float)System.Math.Cos(f);
    public static float Sin(float f) => (float)System.Math.Sin(f);
    public static float Pow(float f, float p) => (float)System.Math.Pow(f, p);
    public static float Atan2(float y, float x) => (float)System.Math.Atan2(y, x);
    public static float Round(float f) => (float)System.Math.Round(f);
    public static float Floor(float f) => (float)System.Math.Floor(f);
    public static int RoundToInt(float f) => (int)System.Math.Round(f);

    public static float Clamp01(float value) => value < 0f ? 0f : value > 1f ? 1f : value;

    public static float Clamp(float value, float min, float max) =>
        value < min ? min : value > max ? max : value;

    public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);

    public static float SmoothStep(float from, float to, float t)
    {
        t = Clamp01(t);
        t = -2f * t * t * t + 3f * t * t;
        return to * t + from * (1f - t);
    }
}

/// <summary>Shim for UnityEngine.Quaternion: the Euler angles the bridge code builds it from.</summary>
public partial struct Quaternion
{
    public float EulerX, EulerY, EulerZ;
    public static Quaternion Euler(float x, float y, float z) => new() { EulerX = x, EulerY = y, EulerZ = z };
}

/// <summary>
/// Shim for UnityEngine.Object: instantiation, destruction and Unity's "fake null". As in Unity, a destroyed object
/// is still a live C# reference but its overloaded <c>==</c>, <c>!=</c>, <c>Equals</c> and <c>bool</c> conversion say
/// it is null; <c>is null</c>, <c>?.</c> and <c>??</c> do not use the overload and see a real reference. Engine members
/// (<c>name</c>, <c>gameObject</c>, <c>GetComponent</c>) of a destroyed object throw a <see cref="System.NullReferenceException"/>,
/// as the game's player does (the modding wiki's Best-Practices page, "Do NOT use null operators on GameObject", shows
/// the log); a component's own C# fields and methods keep working, as they do in Unity.
/// <c>Destroy</c> is deferred as Unity's is: the object stays alive until the test ends the frame with
/// <see cref="EndOfFrame"/>. <c>DestroyImmediate</c> destroys at once.
/// </summary>
public partial class Object
{
    private static readonly System.Collections.Generic.List<Object> s_pendingDestroy = new();
    private string m_name = "";

    /// <summary>True once destroyed (after <see cref="EndOfFrame"/> or <see cref="DestroyImmediate"/>). Never throws.</summary>
    public bool Destroyed { get; private set; }

    /// <summary>The object's name; a component's is its GameObject's. Throws once destroyed.</summary>
    public string name
    {
        get { ThrowIfDestroyed(); return NameHolder.m_name; }
        set { ThrowIfDestroyed(); NameHolder.m_name = value; }
    }
    private Object NameHolder => this is Component { m_gameObject: { } owner } ? owner : this;

    public static GameObject Instantiate(GameObject original, Vector3 position, Quaternion rotation) => original.Clone(position, rotation);

    /// <summary>Queues the object; it is destroyed when the test calls <see cref="EndOfFrame"/>, as Unity destroys at the end of the frame.</summary>
    public static void Destroy(Object? obj)
    {
        if (obj is null || obj.Destroyed || s_pendingDestroy.Contains(obj)) return;
        s_pendingDestroy.Add(obj);
    }

    /// <summary>Destroys the object now, as Unity's does. Destroying a GameObject destroys its components.</summary>
    public static void DestroyImmediate(Object? obj, bool allowDestroyingAssets = false)
    {
        if (obj is null) return;
        s_pendingDestroy.Remove(obj);
        obj.DestroyNow();
    }

    /// <summary>Destroys everything <see cref="Destroy"/> queued, as the end of Unity's frame does; returns how many went.</summary>
    public static int EndOfFrame()
    {
        var due = s_pendingDestroy.ToArray();
        s_pendingDestroy.Clear();
        int destroyed = 0;
        foreach (var obj in due)
            if (!obj.Destroyed) { obj.DestroyNow(); destroyed++; }
        return destroyed;
    }

    private void DestroyNow()
    {
        if (Destroyed) return;
        Destroyed = true;
        OnDestroyed();
    }

    private protected virtual void OnDestroyed() { }

    private protected void ThrowIfDestroyed()
    {
        if (Destroyed)
            throw new System.NullReferenceException(
                $"Object reference not set to an instance of an object: the {GetType().Name} '{NameHolder.m_name}' was destroyed. " +
                "Check it with == null or its bool conversion; ?. and is null do not see a destroyed object.");
    }

    // Unity's comparison: two references are equal when they are the same object; a null reference equals a destroyed one.
    public static bool operator ==(Object? x, Object? y)
    {
        if (x is null) return y is null || y.Destroyed;
        if (y is null) return x.Destroyed;
        return ReferenceEquals(x, y);
    }
    public static bool operator !=(Object? x, Object? y) => !(x == y);

    /// <summary>False for null and for a destroyed object, as Unity's <c>if (obj)</c>.</summary>
    public static implicit operator bool(Object? exists) => !(exists == null);

    public override bool Equals(object? other) => other is null ? Destroyed : other is Object obj && ReferenceEquals(this, obj);

    public override int GetHashCode() => base.GetHashCode();
}

/// <summary>
/// Shim for UnityEngine.MissingReferenceException, so code that names it compiles. The doubles never throw it: the
/// game's player throws a NullReferenceException for a destroyed object (see <see cref="Object"/>).
/// </summary>
public partial class MissingReferenceException : System.SystemException
{
    public MissingReferenceException() { }
    public MissingReferenceException(string message) : base(message) { }
}

/// <summary>Shim for UnityEngine.Component: sits on a GameObject and is destroyed with it.</summary>
public partial class Component : Object
{
    internal GameObject? m_gameObject;

    /// <summary>The object this component sits on; null for one a test builds on its own. Throws once destroyed.</summary>
    public GameObject gameObject
    {
        get { ThrowIfDestroyed(); return m_gameObject!; }
        set => m_gameObject = value;
    }

    public T GetComponent<T>() where T : class
    {
        ThrowIfDestroyed();
        return m_gameObject is { } owner ? owner.GetComponent<T>() : null!;
    }
}

/// <summary>Shim for UnityEngine.Behaviour.</summary>
public partial class Behaviour : Component { }

/// <summary>Shim for UnityEngine.MonoBehaviour: the base of the game's components (ZNetView, WearNTear).</summary>
public partial class MonoBehaviour : Behaviour { }

/// <summary>Shim for a prefab or scene object: optionally networked (a ZNetView) and damageable (a WearNTear).</summary>
public partial class GameObject : Object
{
    public Vector3 Position;
    public Quaternion Rotation;
    public bool Networked;
    public float? Health;
    public ZNetView? View;
    public WearNTear? Wear;
    public GameObject(string name) => this.name = name;

    internal GameObject Clone(Vector3 position, Quaternion rotation)
    {
        var copy = new GameObject(name) { Position = position, Rotation = rotation, Networked = Networked, Health = Health };
        if (Health is float health) copy.Wear = new WearNTear { m_health = health, gameObject = copy };
        if (Networked)
        {
            // As ZNetView.Awake does: a new object gets a new ZDO of its prefab, owned by this session.
            var zdo = global::ZDOMan.instance!.CreateNewZDO(position, name.GetStableHashCode());
            zdo.SetOwner(global::ZDOMan.instance.m_sessionID);
            zdo.Persistent = true;
            copy.View = new ZNetView(zdo) { gameObject = copy };
            if (!ZNetView.GhostInit) global::ZNetScene.instance?.Live.Add(copy);
        }
        return copy;
    }

    // A destroyed object's components go with it. It stays in ZNetScene.Live, as in the game, where only ZNetScene
    // removes an instance; a plain Destroy leaves a destroyed view there.
    private protected override void OnDestroyed()
    {
        DestroyImmediate(View);
        DestroyImmediate(Wear);
    }

    /// <summary>The ZNetView or WearNTear on this object; null when it has none or it was destroyed. Throws once the object is destroyed.</summary>
    public T GetComponent<T>() where T : class
    {
        ThrowIfDestroyed();
        Component? found = null;
        if (typeof(T) == typeof(ZNetView)) found = View;
        else if (typeof(T) == typeof(WearNTear)) found = Wear;
        return (found is { Destroyed: false } ? found as T : null)!;
    }
}

/// <summary>Shim for UnityEngine.Time: the clock mod code reads; tests set it.</summary>
public static partial class Time { public static float realtimeSinceStartup; }
