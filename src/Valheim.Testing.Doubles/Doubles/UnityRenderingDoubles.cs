// Valheim.Testing.Doubles: compile-time stand-ins for the Unity, Valheim, BepInEx and Jotunn types a mod's
// pure-logic sources use, so those sources compile and run in an ordinary test project without the game.
// Source package: these files are compiled into the consuming test project. Every type is partial; add the
// members your mod needs in your own files. Behaviour mirrors the game where mod code depends on it.
#nullable enable
// ReSharper disable InconsistentNaming
// Unity components a mod inspects or switches: colliders and their bounds, rigidbodies, renderers with Unity's
// material and mesh instancing, lights, animators and canvases. No physics, rendering or animation runs. Unity 6 names.
// Their settings are properties, as in Unity, where they live in native code: a mod that reads a component's public fields
// by reflection sees none here either. Each is backed by a private [SerializeField] field under Unity's serialized name,
// so Instantiate still copies it.
using System;
using System.Collections.Generic;

namespace UnityEngine
{
    /// <summary>An axis-aligned box, as Unity's: centre and extents, with inclusive containment and intersection.</summary>
    public partial struct Bounds : IEquatable<Bounds>
    {
        private Vector3 m_center, m_extents;
        public Bounds(Vector3 center, Vector3 size) { m_center = center; m_extents = size * 0.5f; }
        public Vector3 center { get => m_center; set => m_center = value; }
        public Vector3 extents { get => m_extents; set => m_extents = value; }
        public Vector3 size { get => m_extents * 2f; set => m_extents = value * 0.5f; }
        public Vector3 min { get => m_center - m_extents; set => SetMinMax(value, max); }
        public Vector3 max { get => m_center + m_extents; set => SetMinMax(min, value); }
        public void SetMinMax(Vector3 min, Vector3 max) { m_extents = (max - min) * 0.5f; m_center = min + m_extents; }
        public bool Contains(Vector3 point)
        {
            Vector3 lo = min, hi = max;
            return point.x >= lo.x && point.x <= hi.x && point.y >= lo.y && point.y <= hi.y && point.z >= lo.z && point.z <= hi.z;
        }
        public bool Intersects(Bounds bounds)
        {
            Vector3 lo = min, hi = max, otherLo = bounds.min, otherHi = bounds.max;
            return lo.x <= otherHi.x && hi.x >= otherLo.x && lo.y <= otherHi.y && hi.y >= otherLo.y && lo.z <= otherHi.z && hi.z >= otherLo.z;
        }
        public void Encapsulate(Vector3 point) => SetMinMax(VectorMath.Min(min, point), VectorMath.Max(max, point));
        public void Encapsulate(Bounds bounds) { Encapsulate(bounds.center - bounds.extents); Encapsulate(bounds.center + bounds.extents); }
        public void Expand(float amount) { float half = amount * 0.5f; m_extents = new Vector3(m_extents.x + half, m_extents.y + half, m_extents.z + half); }
        /// <summary>The point of the box nearest to <paramref name="point"/>: the point itself when inside.</summary>
        public Vector3 ClosestPoint(Vector3 point) => VectorMath.Min(VectorMath.Max(point, min), max);
        public float SqrDistance(Vector3 point) => Vector3.SqrMagnitude(point - ClosestPoint(point));
        public bool Equals(Bounds other) => VectorMath.Same(m_center, other.m_center) && VectorMath.Same(m_extents, other.m_extents);
        public override bool Equals(object? other) => other is Bounds bounds && Equals(bounds);
        public override int GetHashCode() => m_center.x.GetHashCode() ^ (m_extents.x.GetHashCode() << 2);
        public static bool operator ==(Bounds a, Bounds b) => a.Equals(b);
        public static bool operator !=(Bounds a, Bounds b) => !a.Equals(b);
        public override string ToString() => $"Center: {m_center}, Extents: {m_extents}";
    }

    internal static class VectorMath
    {
        internal static Vector3 Min(Vector3 a, Vector3 b) => new(Math.Min(a.x, b.x), Math.Min(a.y, b.y), Math.Min(a.z, b.z));
        internal static Vector3 Max(Vector3 a, Vector3 b) => new(Math.Max(a.x, b.x), Math.Max(a.y, b.y), Math.Max(a.z, b.z));
        internal static Vector3 Scale(Vector3 a, Vector3 b) => new(a.x * b.x, a.y * b.y, a.z * b.z);
        internal static Vector3 Abs(Vector3 a) => new(Math.Abs(a.x), Math.Abs(a.y), Math.Abs(a.z));
        internal static bool Same(Vector3 a, Vector3 b) => a.x == b.x && a.y == b.y && a.z == b.z;
    }

    /// <summary>
    /// A collider: switched on or off, solid or a trigger. <see cref="bounds"/> is the world-space box Unity reports: the
    /// shape at the object's position and scale (parent rotation is not applied), and an empty box at the origin while the
    /// collider is disabled or its object inactive, as in Unity.
    /// </summary>
    public partial class Collider : Component
    {
        [SerializeField] private bool m_Enabled = true;
        public bool enabled { get => m_Enabled; set => m_Enabled = value; }
        [SerializeField] private bool m_IsTrigger;
        public bool isTrigger { get => m_IsTrigger; set => m_IsTrigger = value; }
        public Rigidbody? attachedRigidbody => m_gameObject is { } owner ? owner.GetComponentInParent<Rigidbody>(true) : null;
        public Bounds bounds => enabled && m_gameObject is { Destroyed: false } owner && owner.activeInHierarchy ? ShapeBounds() : default;
        private protected virtual Bounds ShapeBounds() => new(transform.position, Vector3.zero);
        /// <summary>The point of the collider's bounds nearest to <paramref name="position"/> (a sphere's own surface for a sphere).</summary>
        public virtual Vector3 ClosestPoint(Vector3 position) => bounds.ClosestPoint(position);
    }

    public partial class BoxCollider : Collider
    {
        [SerializeField] private Vector3 m_Center;
        public Vector3 center { get => m_Center; set => m_Center = value; }
        [SerializeField] private Vector3 m_Size = new(1f, 1f, 1f);
        public Vector3 size { get => m_Size; set => m_Size = value; }
        private protected override Bounds ShapeBounds() => new(transform.TransformPoint(center), VectorMath.Abs(VectorMath.Scale(size, transform.lossyScale)));
    }

    public partial class SphereCollider : Collider
    {
        [SerializeField] private Vector3 m_Center;
        public Vector3 center { get => m_Center; set => m_Center = value; }
        [SerializeField] private float m_Radius = 0.5f;
        public float radius { get => m_Radius; set => m_Radius = value; }
        internal float WorldRadius { get { var s = VectorMath.Abs(transform.lossyScale); return Math.Abs(radius) * Math.Max(s.x, Math.Max(s.y, s.z)); } }
        private protected override Bounds ShapeBounds() { float r = WorldRadius * 2f; return new(transform.TransformPoint(center), new Vector3(r, r, r)); }
        public override Vector3 ClosestPoint(Vector3 position)
        {
            var middle = transform.TransformPoint(center); var offset = position - middle;
            float distance = Vector3.Distance(position, middle);
            return distance <= WorldRadius ? position : middle + offset * (WorldRadius / distance);
        }
    }

    public partial class CapsuleCollider : Collider
    {
        [SerializeField] private Vector3 m_Center;
        public Vector3 center { get => m_Center; set => m_Center = value; }
        [SerializeField] private float m_Radius = 0.5f;
        public float radius { get => m_Radius; set => m_Radius = value; }
        [SerializeField] private float m_Height = 2f;
        public float height { get => m_Height; set => m_Height = value; }
        /// <summary>The axis the capsule runs along: 0 = x, 1 = y (the default), 2 = z.</summary>
        [SerializeField] private int m_Direction = 1;
        public int direction { get => m_Direction; set => m_Direction = value; }
        private protected override Bounds ShapeBounds()
        {
            var s = VectorMath.Abs(transform.lossyScale);
            float axisScale = direction == 0 ? s.x : direction == 2 ? s.z : s.y;
            float sideScale = direction == 0 ? Math.Max(s.y, s.z) : direction == 2 ? Math.Max(s.x, s.y) : Math.Max(s.x, s.z);
            float r = Math.Abs(radius) * sideScale, half = Math.Max(Math.Abs(height) * axisScale * 0.5f, r);
            var extents = direction == 0 ? new Vector3(half, r, r) : direction == 2 ? new Vector3(r, r, half) : new Vector3(r, half, r);
            return new(transform.TransformPoint(center), extents * 2f);
        }
    }

    public partial class MeshCollider : Collider
    {
        [SerializeField] private bool m_Convex;
        public bool convex { get => m_Convex; set => m_Convex = value; }
        [SerializeField] private Mesh? m_Mesh;
        public Mesh? sharedMesh { get => m_Mesh; set => m_Mesh = value; }
        private protected override Bounds ShapeBounds()
        {
            if (sharedMesh is null) return new(transform.position, Vector3.zero);
            var local = sharedMesh.bounds;
            return new(transform.TransformPoint(local.center), VectorMath.Abs(VectorMath.Scale(local.size, transform.lossyScale)));
        }
    }

    public enum RigidbodyInterpolation { None = 0, Interpolate = 1, Extrapolate = 2 }

    /// <summary>A rigidbody's settings and velocities, under Unity 6's names (<c>linearVelocity</c>, <c>linearDamping</c>). Nothing moves it.</summary>
    public partial class Rigidbody : Component
    {
        [SerializeField] private Vector3 m_LinearVelocity;
        public Vector3 linearVelocity { get => m_LinearVelocity; set => m_LinearVelocity = value; }
        [SerializeField] private Vector3 m_AngularVelocity;
        public Vector3 angularVelocity { get => m_AngularVelocity; set => m_AngularVelocity = value; }
        [SerializeField] private float m_LinearDamping;
        public float linearDamping { get => m_LinearDamping; set => m_LinearDamping = value; }
        [SerializeField] private float m_AngularDamping = 0.05f;
        public float angularDamping { get => m_AngularDamping; set => m_AngularDamping = value; }
        [SerializeField] private float m_Mass = 1f;
        public float mass { get => m_Mass; set => m_Mass = value; }
        [SerializeField] private bool m_IsKinematic;
        public bool isKinematic { get => m_IsKinematic; set => m_IsKinematic = value; }
        [SerializeField] private bool m_UseGravity = true;
        public bool useGravity { get => m_UseGravity; set => m_UseGravity = value; }
        [SerializeField] private RigidbodyInterpolation m_Interpolate;
        public RigidbodyInterpolation interpolation { get => m_Interpolate; set => m_Interpolate = value; }
        public Vector3 position { get => transform.position; set => transform.position = value; }
        public Quaternion rotation { get => transform.rotation; set => transform.rotation = value; }
        // The pre-Unity-6 names still compile in Unity 6, with an obsolete warning.
        [Obsolete("velocity has been renamed to linearVelocity in Unity 6.")] public Vector3 velocity { get => linearVelocity; set => linearVelocity = value; }
        [Obsolete("drag has been renamed to linearDamping in Unity 6.")] public float drag { get => linearDamping; set => linearDamping = value; }
        [Obsolete("angularDrag has been renamed to angularDamping in Unity 6.")] public float angularDrag { get => angularDamping; set => angularDamping = value; }
    }

    /// <summary>A shader, known by name.</summary>
    public partial class Shader : Object
    {
        private Shader() { }
        public static Shader Find(string name) { var shader = new Shader(); shader.name = name; return shader; }
    }

    /// <summary>A material: its shader and the named properties set on it. Copies made with <c>new Material(source)</c> or <c>Instantiate</c> have their own properties.</summary>
    public partial class Material : Object
    {
        private readonly Dictionary<string, object> m_properties = new();
        private Material() { }
        public Material(Shader shader) { this.shader = shader; }
        public Material(Material source)
        {
            name = source.name; shader = source.shader;
            foreach (var property in source.m_properties) m_properties[property.Key] = property.Value;
        }
        public Shader? shader;
        /// <summary>The "_Color" property.</summary>
        public Color color { get => GetColor("_Color"); set => SetColor("_Color", value); }
        public bool HasProperty(string name) => m_properties.ContainsKey(name);
        public void SetColor(string name, Color value) => m_properties[name] = value;
        public Color GetColor(string name) => m_properties.TryGetValue(name, out var value) && value is Color c ? c : default;
        public void SetFloat(string name, float value) => m_properties[name] = value;
        public float GetFloat(string name) => m_properties.TryGetValue(name, out var value) && value is float f ? f : 0f;
        public void SetInt(string name, int value) => m_properties[name] = value;
        public int GetInt(string name) => m_properties.TryGetValue(name, out var value) && value is int i ? i : 0;
        private protected override Object CopyForInstantiate() => new Material(this);
    }

    /// <summary>
    /// A renderer: switched on or off, and its materials. <see cref="material"/> and <see cref="materials"/> do what
    /// Unity's do: the first read copies the shared material (named "&lt;name&gt; (Instance)"), puts the copy in the
    /// renderer's place and returns it, so a change reaches only this renderer; <see cref="sharedMaterial"/> never copies.
    /// </summary>
    public partial class Renderer : Component
    {
        [SerializeField] private bool m_Enabled = true;
        public bool enabled { get => m_Enabled; set => m_Enabled = value; }
        [SerializeField] private Material[] m_Materials = new Material[0];
        public Material[] sharedMaterials { get => m_Materials; set => m_Materials = value; }
        private readonly HashSet<Material> m_instances = new();
        public Material? sharedMaterial
        {
            get => sharedMaterials.Length == 0 ? null : sharedMaterials[0];
            set { if (sharedMaterials.Length == 0) sharedMaterials = new Material[1]; sharedMaterials[0] = value!; }
        }
        public Material? material
        {
            get
            {
                if (sharedMaterials.Length == 0 || sharedMaterials[0] is null) return null;
                return sharedMaterials[0] = Own(sharedMaterials[0]);
            }
            set => sharedMaterial = value;
        }
        public Material[] materials
        {
            get
            {
                for (int i = 0; i < sharedMaterials.Length; i++) if (sharedMaterials[i] is not null) sharedMaterials[i] = Own(sharedMaterials[i]);
                return (Material[])sharedMaterials.Clone();
            }
            set => sharedMaterials = (Material[])value.Clone();
        }
        private Material Own(Material shared)
        {
            if (m_instances.Contains(shared)) return shared;
            var copy = new Material(shared) { name = shared.name + " (Instance)" };
            m_instances.Add(copy);
            return copy;
        }
    }
    public partial class MeshRenderer : Renderer { }
    public partial class SkinnedMeshRenderer : Renderer { public Mesh? sharedMesh; }

    /// <summary>A mesh's vertices and triangles. As in Unity, assigning triangles recalculates the bounds; assigning vertices does not.</summary>
    public partial class Mesh : Object
    {
        private Vector3[] m_vertices = new Vector3[0];
        private int[] m_triangles = new int[0];
        public Mesh() { }
        public Vector3[] vertices { get => (Vector3[])m_vertices.Clone(); set => m_vertices = (Vector3[])value.Clone(); }
        public int[] triangles
        {
            get => (int[])m_triangles.Clone();
            set
            {
                foreach (int index in value)
                    if (index < 0 || index >= m_vertices.Length)
                    {
                        // Unity logs this and keeps the old triangles.
                        Debug.LogError($"Failed setting triangles. Some indices are referencing out of bounds vertices. IndexCount: {value.Length}, VertexCount: {m_vertices.Length}");
                        return;
                    }
                m_triangles = (int[])value.Clone(); RecalculateBounds();
            }
        }
        public int vertexCount => m_vertices.Length;
        public Bounds bounds { get; set; }
        public void RecalculateBounds()
        {
            if (m_vertices.Length == 0) { bounds = default; return; }
            var box = new Bounds(m_vertices[0], Vector3.zero);
            foreach (var vertex in m_vertices) box.Encapsulate(vertex);
            bounds = box;
        }
        private protected override Object CopyForInstantiate() { var copy = new Mesh { name = name, m_vertices = (Vector3[])m_vertices.Clone(), m_triangles = (int[])m_triangles.Clone() }; copy.bounds = bounds; return copy; }
    }

    /// <summary>The mesh an object draws. <see cref="mesh"/> copies a shared mesh on first read ("&lt;name&gt; Instance"), as Unity's does; <see cref="sharedMesh"/> never copies.</summary>
    public partial class MeshFilter : Component
    {
        [SerializeField] private Mesh? m_Mesh;
        public Mesh? sharedMesh { get => m_Mesh; set => m_Mesh = value; }
        private Mesh? m_instance;
        public Mesh mesh
        {
            get
            {
                if (sharedMesh is null) return sharedMesh = m_instance = new Mesh();
                if (!ReferenceEquals(sharedMesh, m_instance)) { var copy = (Mesh)Instantiate(sharedMesh); copy.name = sharedMesh.name + " Instance"; sharedMesh = m_instance = copy; }
                return sharedMesh;
            }
            set => sharedMesh = value;
        }
    }

    /// <summary>Unity 6's light types (Area is now Rectangle; the old name remains, obsolete).</summary>
    public enum LightType { Spot = 0, Directional = 1, Point = 2, Rectangle = 3, Disc = 4, Pyramid = 5, Box = 6, Tube = 7, [Obsolete("Area has been renamed to Rectangle.")] Area = 3 }
    public enum LightShadows { None = 0, Hard = 1, Soft = 2 }

    /// <summary>A light's settings, with Unity's defaults. It lights nothing.</summary>
    public partial class Light : Behaviour
    {
        [SerializeField] private LightType m_Type = LightType.Point;
        public LightType type { get => m_Type; set => m_Type = value; }
        [SerializeField] private Color m_Color = new(1f, 1f, 1f, 1f);
        public Color color { get => m_Color; set => m_Color = value; }
        [SerializeField] private float m_Intensity = 1f;
        public float intensity { get => m_Intensity; set => m_Intensity = value; }
        [SerializeField] private float m_Range = 10f;
        public float range { get => m_Range; set => m_Range = value; }
        [SerializeField] private float m_SpotAngle = 30f;
        public float spotAngle { get => m_SpotAngle; set => m_SpotAngle = value; }
        [SerializeField] private LightShadows m_Shadows;
        public LightShadows shadows { get => m_Shadows; set => m_Shadows = value; }
        [SerializeField] private float m_ShadowStrength = 1f;
        public float shadowStrength { get => m_ShadowStrength; set => m_ShadowStrength = value; }
    }

    /// <summary>Unity 6's animator update modes (AnimatePhysics is now Fixed; the old name remains, obsolete).</summary>
    public enum AnimatorUpdateMode { Normal = 0, Fixed = 1, UnscaledTime = 2, [Obsolete("AnimatePhysics has been renamed to Fixed.")] AnimatePhysics = 1 }

    /// <summary>
    /// An animator's parameters and settings. A parameter set by name and one set by <see cref="StringToHash"/> of that
    /// name are the same parameter. A trigger stays set until <see cref="ResetTrigger(string)"/> (no state machine
    /// consumes it). There is no controller, so every parameter name is accepted; Unity would warn about an undefined one.
    /// </summary>
    public partial class Animator : Behaviour
    {
        private readonly Dictionary<int, object> m_parameters = new();
        [SerializeField] private float m_Speed = 1f;
        public float speed { get => m_Speed; set => m_Speed = value; }
        [SerializeField] private AnimatorUpdateMode m_UpdateMode;
        public AnimatorUpdateMode updateMode { get => m_UpdateMode; set => m_UpdateMode = value; }
        [SerializeField] private bool m_KeepAnimatorStateOnDisable;
        public bool keepAnimatorStateOnDisable { get => m_KeepAnimatorStateOnDisable; set => m_KeepAnimatorStateOnDisable = value; }

        /// <summary>A CRC-32 of the name, the stable id Unity uses for a parameter or state (not checked against Unity's own values).</summary>
        public static int StringToHash(string name)
        {
            uint crc = 0xFFFFFFFFu;
            foreach (byte b in System.Text.Encoding.UTF8.GetBytes(name))
            {
                crc ^= b;
                for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
            return unchecked((int)~crc);
        }

        public void SetBool(string name, bool value) => SetBool(StringToHash(name), value);
        public void SetBool(int id, bool value) => m_parameters[id] = value;
        public bool GetBool(string name) => GetBool(StringToHash(name));
        public bool GetBool(int id) => m_parameters.TryGetValue(id, out var value) && value is true;
        public void SetFloat(string name, float value) => SetFloat(StringToHash(name), value);
        public void SetFloat(int id, float value) => m_parameters[id] = value;
        public float GetFloat(string name) => GetFloat(StringToHash(name));
        public float GetFloat(int id) => m_parameters.TryGetValue(id, out var value) && value is float f ? f : 0f;
        public void SetInteger(string name, int value) => SetInteger(StringToHash(name), value);
        public void SetInteger(int id, int value) => m_parameters[id] = value;
        public int GetInteger(string name) => GetInteger(StringToHash(name));
        public int GetInteger(int id) => m_parameters.TryGetValue(id, out var value) && value is int i ? i : 0;
        public void SetTrigger(string name) => SetTrigger(StringToHash(name));
        public void SetTrigger(int id) => m_parameters[id] = true;
        public void ResetTrigger(string name) => ResetTrigger(StringToHash(name));
        public void ResetTrigger(int id) => m_parameters[id] = false;
    }

    public enum RenderMode { ScreenSpaceOverlay = 0, ScreenSpaceCamera = 1, WorldSpace = 2 }

    /// <summary>A UI canvas: its settings and whether it is a root canvas (no canvas above it in the hierarchy). Nothing is drawn.</summary>
    public partial class Canvas : Behaviour
    {
        [SerializeField] private RenderMode m_RenderMode;
        public RenderMode renderMode { get => m_RenderMode; set => m_RenderMode = value; }
        [SerializeField] private int m_SortingOrder;
        public int sortingOrder { get => m_SortingOrder; set => m_SortingOrder = value; }
        [SerializeField] private float m_ScaleFactor = 1f;
        public float scaleFactor { get => m_ScaleFactor; set => m_ScaleFactor = value; }
        /// <summary>How many times <see cref="ForceUpdateCanvases"/> ran; a test can check a layout pass was requested.</summary>
        public static int ForceUpdateCount;
        public static void ForceUpdateCanvases() => ForceUpdateCount++;
        public bool isRootCanvas => ParentCanvas() == null;
        public Canvas rootCanvas { get { var root = this; for (var up = ParentCanvas(); up != null; up = up.ParentCanvas()) root = up; return root; } }
        private Canvas? ParentCanvas() => transform.parent is { } parent && parent.m_gameObject is { } owner ? owner.GetComponentInParent<Canvas>(true) : null;
    }

    public partial class Object
    {
        // What Instantiate makes of an object that is not a GameObject or component: a copy of its serialized fields.
        private protected virtual Object CopyForInstantiate()
        {
            var copy = UnitySerialization.NewInstance(GetType());
            UnitySerialization.CopySerializedFields(this, copy, new Dictionary<Object, Object>());
            copy.m_name = m_name;
            return copy;
        }
    }
}
