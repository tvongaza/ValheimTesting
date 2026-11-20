using System.Runtime.CompilerServices;
using Valheim.Testing.Doubles;
// Valheim.Testing.Doubles: compile-time stand-ins for the Unity, Valheim, BepInEx and Jotunn types a mod's
// pure-logic sources use, so those sources compile and run in an ordinary test project without the game.
// Source package: these files are compiled into the consuming test project. Every type is partial; add the
// members your mod needs in your own files. Behaviour mirrors the game where mod code depends on it.
#nullable enable
// ReSharper disable InconsistentNaming
// UnityEngine: managed math as Unity computes it, and scene objects (the scene itself is in SceneDoubles.cs).
namespace UnityEngine;

// The math structs copy the game's UnityEngine.CoreModule: the same [MethodImpl(AggressiveInlining)] marks (Mono honours
// them, so a timing taken offline on the game's runtime matches the game), IEquatable<T>, an exact Equals and Unity's
// GetHashCode, and Unity's == (approximate for the float types, so == and Equals can disagree as in the game).
public partial struct Vector2 : System.IEquatable<Vector2>
{
    public const float kEpsilon = 1e-5f;

    public float x;
    public float y;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector2(float x, float y)
    {
        this.x = x;
        this.y = y;
    }

    public float sqrMagnitude { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => x * x + y * y; }
    public float magnitude { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => (float)System.Math.Sqrt(x * x + y * y); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Normalize()
    {
        float m = magnitude;
        if (m > kEpsilon) { x /= m; y /= m; }
        else { x = 0; y = 0; }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Distance(Vector2 a, Vector2 b)
    {
        float dx = a.x - b.x, dy = a.y - b.y;
        return (float)System.Math.Sqrt(dx * dx + dy * dy);
    }

    public Vector2 normalized
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var v = this;
            v.Normalize();
            return v;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float SqrMagnitude(Vector2 a) => a.x * a.x + a.y * a.y;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Dot(Vector2 a, Vector2 b) => a.x * b.x + a.y * b.y;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector2 Lerp(Vector2 a, Vector2 b, float t)
    {
        t = Mathf.Clamp01(t);
        return new Vector2(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector2 operator +(Vector2 a, Vector2 b) => new(a.x + b.x, a.y + b.y);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector2 operator -(Vector2 a, Vector2 b) => new(a.x - b.x, a.y - b.y);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector2 operator -(Vector2 a) => new(-a.x, -a.y);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector2 operator *(Vector2 a, float d) => new(a.x * d, a.y * d);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector2 operator *(float d, Vector2 a) => new(a.x * d, a.y * d);

    /// <summary>As Unity's: every component exactly equal (unlike <c>==</c>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override bool Equals(object? other) => other is Vector2 v && Equals(v);

    /// <summary>As Unity's: every component exactly equal (unlike <c>==</c>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(Vector2 other) => x == other.x && y == other.y;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode() => x.GetHashCode() ^ (y.GetHashCode() << 2);

    /// <summary>As Unity's: equal when the squared distance between the two is under <c>kEpsilon * kEpsilon</c> (1e-10).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(Vector2 lhs, Vector2 rhs)
    {
        float diffX = lhs.x - rhs.x, diffY = lhs.y - rhs.y;
        return diffX * diffX + diffY * diffY < kEpsilon * kEpsilon;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(Vector2 lhs, Vector2 rhs) => !(lhs == rhs);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override string ToString() => $"({x:F1}, {y:F1})";
}

public partial struct Vector3 : System.IEquatable<Vector3>
{
    public const float kEpsilon = 1e-5f;

    public float x;
    public float y;
    public float z;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector3(float x, float y, float z)
    {
        this.x = x;
        this.y = y;
        this.z = z;
    }

    public static Vector3 zero { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new(0f, 0f, 0f); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Distance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
        return (float)System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float SqrMagnitude(Vector3 a) => a.x * a.x + a.y * a.y + a.z * a.z;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 Lerp(Vector3 a, Vector3 b, float t)
    {
        t = Mathf.Clamp01(t);
        return new Vector3(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.x + b.x, a.y + b.y, a.z + b.z);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.x - b.x, a.y - b.y, a.z - b.z);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector3 operator *(Vector3 a, float d) => new(a.x * d, a.y * d, a.z * d);

    /// <summary>As Unity's: every component exactly equal (unlike <c>==</c>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override bool Equals(object? other) => other is Vector3 v && Equals(v);

    /// <summary>As Unity's: every component exactly equal (unlike <c>==</c>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(Vector3 other) => x == other.x && y == other.y && z == other.z;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode() => x.GetHashCode() ^ (y.GetHashCode() << 2) ^ (z.GetHashCode() >> 2);

    /// <summary>As Unity's: equal when the squared distance between the two is under <c>kEpsilon * kEpsilon</c> (1e-10).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(Vector3 lhs, Vector3 rhs)
    {
        float diffX = lhs.x - rhs.x, diffY = lhs.y - rhs.y, diffZ = lhs.z - rhs.z;
        return diffX * diffX + diffY * diffY + diffZ * diffZ < kEpsilon * kEpsilon;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(Vector3 lhs, Vector3 rhs) => !(lhs == rhs);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override string ToString() => $"({x:F1}, {y:F1}, {z:F1})";
}

// The game's Color marks ToString AggressiveInlining and nothing else the doubles define.
public partial struct Color : System.IEquatable<Color>
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

    /// <summary>As Unity's: equal when the squared distance between the two (as 4-vectors) is under 1e-10.</summary>
    public static bool operator ==(Color lhs, Color rhs)
    {
        float dr = lhs.r - rhs.r, dg = lhs.g - rhs.g, db = lhs.b - rhs.b, da = lhs.a - rhs.a;
        return dr * dr + dg * dg + db * db + da * da < 9.99999944E-11f;
    }
    public static bool operator !=(Color lhs, Color rhs) => !(lhs == rhs);
    /// <summary>As Unity's: every component exactly equal (unlike <c>==</c>).</summary>
    public override bool Equals(object? other) => other is Color c && Equals(c);
    /// <summary>As Unity's: every component exactly equal (unlike <c>==</c>).</summary>
    public bool Equals(Color other) => r.Equals(other.r) && g.Equals(other.g) && b.Equals(other.b) && a.Equals(other.a);
    public override int GetHashCode() => r.GetHashCode() ^ (g.GetHashCode() << 2) ^ (b.GetHashCode() >> 2) ^ (a.GetHashCode() >> 1);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override string ToString() => $"RGBA({r:F3}, {g:F3}, {b:F3}, {a:F3})";
}

public partial struct Vector2Int : System.IEquatable<Vector2Int>
{
    public int x;
    public int y;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector2Int(int x, int y)
    {
        this.x = x;
        this.y = y;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override bool Equals(object? other) => other is Vector2Int v && Equals(v);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(Vector2Int other) => x == other.x && y == other.y;
    /// <summary>As Unity's: the components times two primes, xor'd.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode() => unchecked((x * 73856093) ^ (y * 83492791));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(Vector2Int lhs, Vector2Int rhs) => lhs.x == rhs.x && lhs.y == rhs.y;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(Vector2Int lhs, Vector2Int rhs) => !(lhs == rhs);
}

public partial struct Mathf
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

/// <summary>
/// Shim for UnityEngine.Quaternion: its components, which a package carries, and the Euler angles the bridge code builds
/// it from. <see cref="Euler"/> sets both, in Unity's rotation order (z, then x, then y); a quaternion read back from a
/// package has its components only.
/// </summary>
public partial struct Quaternion : System.IEquatable<Quaternion>
{
    public const float kEpsilon = 1e-6f;

    public float x, y, z, w;
    [TestOnly] public float EulerX, EulerY, EulerZ;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; EulerX = EulerY = EulerZ = 0f; }
    public static Quaternion identity { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new(0f, 0f, 0f, 1f); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Quaternion Euler(float x, float y, float z)
    {
        double hx = x * System.Math.PI / 360.0, hy = y * System.Math.PI / 360.0, hz = z * System.Math.PI / 360.0;
        double cx = System.Math.Cos(hx), sx = System.Math.Sin(hx), cy = System.Math.Cos(hy), sy = System.Math.Sin(hy), cz = System.Math.Cos(hz), sz = System.Math.Sin(hz);
        return new Quaternion(
            (float)(sx * cy * cz + cx * sy * sz), (float)(cx * sy * cz - sx * cy * sz),
            (float)(cx * cy * sz - sx * sy * cz), (float)(cx * cy * cz + sx * sy * sz)) { EulerX = x, EulerY = y, EulerZ = z };
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Quaternion Euler(Vector3 euler) => Euler(euler.x, euler.y, euler.z);

    /// <summary>
    /// The rotation as Euler angles in degrees, each in [0, 360), in Unity's convention (the inverse of <see cref="Euler(float, float, float)"/>:
    /// z, then x, then y). At x = ±90 the split between y and z is not unique; Unity's own split may differ there.
    /// </summary>
    public Vector3 eulerAngles
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            double sinX = 2.0 * ((double)w * x - (double)y * z);
            double ax = System.Math.Abs(sinX) >= 1.0 ? System.Math.PI / 2 * System.Math.Sign(sinX) : System.Math.Asin(sinX);
            double ay = System.Math.Atan2(2.0 * ((double)w * y + (double)x * z), 1.0 - 2.0 * ((double)x * x + (double)y * y));
            double az = System.Math.Atan2(2.0 * ((double)w * z + (double)x * y), 1.0 - 2.0 * ((double)x * x + (double)z * z));
            return new Vector3(Degrees(ax), Degrees(ay), Degrees(az));
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => this = Euler(value);
    }
    private static float Degrees(double radians)
    {
        double degrees = radians * 180.0 / System.Math.PI % 360.0;
        return (float)(degrees < 0 ? degrees + 360.0 : degrees);
    }

    /// <summary>Unity's composition: <paramref name="lhs"/> after <paramref name="rhs"/> (the Hamilton product).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Quaternion operator *(Quaternion lhs, Quaternion rhs) => new(
        lhs.w * rhs.x + lhs.x * rhs.w + lhs.y * rhs.z - lhs.z * rhs.y,
        lhs.w * rhs.y + lhs.y * rhs.w + lhs.z * rhs.x - lhs.x * rhs.z,
        lhs.w * rhs.z + lhs.z * rhs.w + lhs.x * rhs.y - lhs.y * rhs.x,
        lhs.w * rhs.w - lhs.x * rhs.x - lhs.y * rhs.y - lhs.z * rhs.z);

    /// <summary>Rotates <paramref name="point"/> by <paramref name="rotation"/>.</summary>
    public static Vector3 operator *(Quaternion rotation, Vector3 point)
    {
        // v + 2w(q x v) + 2 q x (q x v), for a unit quaternion q = (x, y, z, w).
        float tx = 2f * (rotation.y * point.z - rotation.z * point.y);
        float ty = 2f * (rotation.z * point.x - rotation.x * point.z);
        float tz = 2f * (rotation.x * point.y - rotation.y * point.x);
        return new Vector3(
            point.x + rotation.w * tx + (rotation.y * tz - rotation.z * ty),
            point.y + rotation.w * ty + (rotation.z * tx - rotation.x * tz),
            point.z + rotation.w * tz + (rotation.x * ty - rotation.y * tx));
    }

    /// <summary>The inverse of a unit rotation (its conjugate).</summary>
    public static Quaternion Inverse(Quaternion rotation) => new(-rotation.x, -rotation.y, -rotation.z, rotation.w);

    /// <summary>
    /// As Unity's: the same rotation when the dot product of the two is within <see cref="kEpsilon"/> of 1 (unlike
    /// <see cref="Equals(Quaternion)"/>). Unity computes it with Quaternion.Dot, which the doubles do not define.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(Quaternion lhs, Quaternion rhs) =>
        lhs.x * rhs.x + lhs.y * rhs.y + lhs.z * rhs.z + lhs.w * rhs.w > 1.0f - kEpsilon;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(Quaternion lhs, Quaternion rhs) => !(lhs == rhs);

    /// <summary>As Unity's: every component exactly equal (unlike <c>==</c>); the test-only Euler fields do not count.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override bool Equals(object? other) => other is Quaternion q && Equals(q);
    /// <summary>As Unity's: every component exactly equal (unlike <c>==</c>); the test-only Euler fields do not count.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(Quaternion other) => x.Equals(other.x) && y.Equals(other.y) && z.Equals(other.z) && w.Equals(other.w);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode() => x.GetHashCode() ^ (y.GetHashCode() << 2) ^ (z.GetHashCode() >> 2) ^ (w.GetHashCode() >> 1);
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
    /// <summary>What <see cref="Destroy"/> queued for the end of the frame. <c>ValheimWorldScope.WithScene</c> gives a test its own.</summary>
    internal static System.Collections.Generic.List<Object> s_pendingDestroy = new();
    private string m_name = "";

    /// <summary>True once destroyed (after <see cref="EndOfFrame"/> or <see cref="DestroyImmediate"/>). Never throws.</summary>
    [TestOnly] public bool Destroyed { get; private set; }

    /// <summary>The object's name; a component's is its GameObject's. Throws once destroyed.</summary>
    public string name
    {
        get { ThrowIfDestroyed(); return NameHolder.m_name; }
        set { ThrowIfDestroyed(); NameHolder.m_name = value; }
    }
    private Object NameHolder => this is Component { m_gameObject: { } owner } ? owner : this;

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

    /// <summary>
    /// Destroys everything <see cref="Destroy"/> queued, as the end of Unity's frame does; returns how many of the queued
    /// objects went, whether directly or with their GameObject, so the count does not depend on the order. They go in the
    /// order they were queued (reversed with <c>ValheimWorldScope.WithUnityOrder(UnityOrder.Reversed)</c>); Unity promises
    /// no order.
    /// </summary>
    [TestOnly] public static int EndOfFrame()
    {
        var due = UnityOrdered(s_pendingDestroy);
        s_pendingDestroy.Clear();
        due.RemoveAll(obj => obj.Destroyed);
        foreach (var obj in due) obj.DestroyNow();
        return due.Count;
    }

    private void DestroyNow()
    {
        if (Destroyed) return;
        UnityDestroying();
        Destroyed = true;
        OnDestroyed();
    }

    // Runs while the object is still alive, as Unity's OnDisable and OnDestroy messages do (UnityComponentDoubles.cs).
    private protected virtual void UnityDestroying() { }
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
    }

    public T GetComponent<T>() where T : class
    {
        ThrowIfDestroyed();
        return m_gameObject is { } owner ? owner.GetComponent<T>() : null!;
    }
    /// <summary>The first live component of the type on this component's object, or null, as <see cref="GetComponent{T}"/>.</summary>
    public Component GetComponent(System.Type type)
    {
        ThrowIfDestroyed();
        GameObject.CheckComponentType(type);
        return m_gameObject is { } owner ? owner.GetComponent(type) : null!;
    }
}

/// <summary>Shim for UnityEngine.Behaviour.</summary>
public partial class Behaviour : Component { }

/// <summary>Shim for UnityEngine.MonoBehaviour: the base of the game's components (ZNetView, WearNTear).</summary>
public partial class MonoBehaviour : Behaviour { }

/// <summary>
/// Shim for a prefab or scene object. Its components (a <see cref="ZNetView"/> and a <see cref="WearNTear"/> among them),
/// transform hierarchy and activation are in UnityComponentDoubles.cs; the members here are fixture shorthands over them.
/// </summary>
public partial class GameObject : Object
{
    /// <summary>
    /// Fixture shorthand for a <see cref="ZNetView"/> on the object. Setting it adds a persistent one, as the game's
    /// prefabs carry. On an active scene object the view wakes at once and takes its ZDO from the object's position then,
    /// so set <see cref="IsAsset"/>, the position and the parent first (in an object initializer, before this). Clearing
    /// it destroys the view component at once; as in the game, that leaves a ZDO the view had, and its place in
    /// <c>ZNetScene.Live</c>, behind.
    /// </summary>
    [TestOnly] public bool Networked
    {
        get => View is { Destroyed: false };
        set { if (value == Networked) return; if (value) AddComponent<ZNetView>(view => view.m_persistent = true); else DestroyImmediate(View); }
    }
    /// <summary>Fixture shorthand for a <see cref="WearNTear"/> with this full health; null removes it.</summary>
    [TestOnly] public float? Health
    {
        get => Wear is { Destroyed: false } wear ? wear.m_health : null;
        set
        {
            if (value is not float health) { if (Wear is { Destroyed: false } wear) DestroyImmediate(wear); }
            else if (Wear is { Destroyed: false } wear) wear.m_health = health;
            else AddComponent<WearNTear>(added => added.m_health = health);
        }
    }
    /// <summary>The object's first <see cref="ZNetView"/>, or null. Never throws: on a destroyed object it is the destroyed view, which <c>ZNetScene.FindInstance</c> still finds, as in the game.</summary>
    [TestOnly] public ZNetView? View => Own<ZNetView>();
    /// <summary>The object's first <see cref="WearNTear"/>, or null, as <see cref="View"/>.</summary>
    [TestOnly] public WearNTear? Wear => Own<WearNTear>();
    /// <summary>A new object in the scene, as Unity's: FindObjectsByType finds it, with or without components.</summary>
    public GameObject(string name) { this.name = name; s_unityGameObjects.Add(this); }

    internal GameObject Clone(Vector3 position, Quaternion rotation)
    {
        var copy = new GameObject(name) { Position = position, Rotation = rotation };
        CopyHierarchyInto(copy);
        return copy;
    }
}

/// <summary>Shim for UnityEngine.Time: the clock mod code reads; tests advance it with Object.RunFrame.</summary>
public static partial class Time { public static float realtimeSinceStartup { get; private set; } }
