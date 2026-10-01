// Valheim.Testing.Doubles: compile-time stand-ins for the Unity, Valheim, BepInEx and Jotunn types a mod's
// pure-logic sources use, so those sources compile and run in an ordinary test project without the game.
// Source package: these files are compiled into the consuming test project. Every type is partial; add the
// members your mod needs in your own files. Behaviour mirrors the game where mod code depends on it.
#nullable enable
// ReSharper disable InconsistentNaming
// Unity's object model beyond UnityDoubles.cs: components on game objects, the transform hierarchy, activation, the
// MonoBehaviour messages, coroutines and Invoke driven one frame at a time (Object.RunFrame), Instantiate's copy of
// serialized fields, and the clock, log and random numbers mod code reads. Unity 6 member names, plus obsolete
// forwarders for the older names Unity 6 still compiles.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace UnityEngine
{
    /// <summary>Marks a non-public field Unity serializes, so <c>Instantiate</c> copies it.</summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed partial class SerializeField : Attribute { }

    /// <summary>Unity's exception for a call the engine refuses.</summary>
    public partial class UnityException : Exception
    {
        public UnityException() { }
        public UnityException(string message) : base(message) { }
    }

    public enum FindObjectsSortMode { None = 0, InstanceID = 1 }
    public enum FindObjectsInactive { Exclude = 0, Include = 1 }

    public partial class Object
    {
        private static int s_lastInstanceID;
        private int m_instanceID = System.Threading.Interlocked.Increment(ref s_lastInstanceID);
        /// <summary>Unique per object, as Unity's; a copy made by <c>Instantiate</c> gets its own.</summary>
        public int GetInstanceID() => m_instanceID;

        /// <summary>
        /// The components attached with <c>AddComponent</c> or made by <c>Instantiate</c>: what <see cref="FindObjectsByType{T}(FindObjectsSortMode)"/>
        /// searches and <see cref="RunFrame"/> drives. <c>ValheimWorldScope.WithScene</c> gives a test its own.
        /// </summary>
        internal static List<Component> s_unityComponents = new();
        /// <summary>
        /// Every GameObject made since the scene began, with <c>new GameObject</c> or by <c>Instantiate</c>, as Unity puts
        /// each one in the scene: what <see cref="FindObjectsByType{T}(FindObjectsSortMode)"/> searches for GameObjects.
        /// <c>ValheimWorldScope.WithScene</c> gives a test its own.
        /// </summary>
        internal static List<GameObject> s_unityGameObjects = new();

        /// <summary>
        /// As Unity 6's FindObjectsByType: every live GameObject, or component of type <typeparamref name="T"/>, in the
        /// scene and active in its hierarchy (inactive ones too with <see cref="FindObjectsInactive.Include"/>). A GameObject
        /// counts with no components. Destroyed objects and assets (<see cref="GameObject.IsAsset"/>, with their children
        /// and components) are not found. Only components attached with <c>AddComponent</c> or made by <c>Instantiate</c>
        /// are found.
        /// </summary>
        public static T[] FindObjectsByType<T>(FindObjectsSortMode sortMode) where T : Object => FindObjectsByType<T>(FindObjectsInactive.Exclude, sortMode);
        public static T[] FindObjectsByType<T>(FindObjectsInactive findObjectsInactive, FindObjectsSortMode sortMode) where T : Object
        {
            s_unityComponents.RemoveAll(c => c.Destroyed);
            s_unityGameObjects.RemoveAll(o => o.Destroyed);
            bool Found(GameObject go) => go.InScene && (findObjectsInactive == FindObjectsInactive.Include || go.activeInHierarchy);
            var found = new List<T>();
            foreach (var go in s_unityGameObjects) if (go is T match && Found(go)) found.Add(match);
            foreach (var component in s_unityComponents)
                if (component is T match && component.m_gameObject is { Destroyed: false } owner && Found(owner))
                    found.Add(match);
            if (sortMode == FindObjectsSortMode.InstanceID) found.Sort((a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
            return found.ToArray();
        }
        /// <summary>The found component with the lowest instance id, or null.</summary>
        public static T? FindFirstObjectByType<T>(FindObjectsInactive findObjectsInactive = FindObjectsInactive.Exclude) where T : Object
        {
            var all = FindObjectsByType<T>(findObjectsInactive, FindObjectsSortMode.InstanceID);
            return all.Length == 0 ? null : all[0];
        }
        /// <summary>Any found component, or null. Unity promises no order.</summary>
        public static T? FindAnyObjectByType<T>(FindObjectsInactive findObjectsInactive = FindObjectsInactive.Exclude) where T : Object
        {
            var all = FindObjectsByType<T>(findObjectsInactive, FindObjectsSortMode.None);
            return all.Length == 0 ? null : all[0];
        }

        // The pre-Unity-6 names still compile in Unity 6 (and the game uses them), with an obsolete warning.
        [Obsolete("FindObjectsOfType is obsolete in Unity 6: use FindObjectsByType (it sorts by instance id).")]
        public static T[] FindObjectsOfType<T>() where T : Object => FindObjectsByType<T>(FindObjectsInactive.Exclude, FindObjectsSortMode.InstanceID);
        [Obsolete("FindObjectsOfType is obsolete in Unity 6: use FindObjectsByType (it sorts by instance id).")]
        public static T[] FindObjectsOfType<T>(bool includeInactive) where T : Object =>
            FindObjectsByType<T>(includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude, FindObjectsSortMode.InstanceID);
        [Obsolete("FindObjectOfType is obsolete in Unity 6: use FindFirstObjectByType or FindAnyObjectByType.")]
        public static T? FindObjectOfType<T>() where T : Object => FindFirstObjectByType<T>();
        [Obsolete("FindObjectOfType is obsolete in Unity 6: use FindFirstObjectByType or FindAnyObjectByType.")]
        public static T? FindObjectOfType<T>(bool includeInactive) where T : Object =>
            FindFirstObjectByType<T>(includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude);

        /// <summary>
        /// Runs one frame of Unity's player loop over the components the doubles know: advances the clock
        /// (<c>Time.time</c>, <c>Time.realtimeSinceStartup</c>, <c>Time.deltaTime</c>, <c>Time.frameCount</c>), calls
        /// <c>Start</c> on enabled behaviours that have not started, <c>Update</c> on every enabled behaviour of an active
        /// object, resumes coroutines and runs due <c>Invoke</c> calls, calls <c>LateUpdate</c>, resumes coroutines waiting
        /// for the end of the frame, and ends the frame (<see cref="EndOfFrame"/>). A behaviour added during a frame starts
        /// in the next one.
        /// </summary>
        public static void RunFrame(float deltaTime = 0.02f)
        {
            if (!(deltaTime >= 0f) || float.IsInfinity(deltaTime)) throw new ArgumentOutOfRangeException(nameof(deltaTime));
            Time.deltaTime = deltaTime; Time.time += deltaTime; Time.realtimeSinceStartup += deltaTime; Time.frameCount++;
            var behaviours = new List<MonoBehaviour>();
            foreach (var component in s_unityComponents.ToArray()) if (component is MonoBehaviour behaviour && !behaviour.Destroyed) behaviours.Add(behaviour);
            foreach (var behaviour in behaviours)
                if (behaviour.UnityRunning && !behaviour.m_unityStarted) { behaviour.m_unityStarted = true; behaviour.UnityRunStart(); }
            foreach (var behaviour in behaviours) if (behaviour.UnityRunning) behaviour.UnitySendMessage("Update");
            foreach (var behaviour in behaviours) if (behaviour.UnityAlive) { behaviour.UnityResumeCoroutines(endOfFrame: false); behaviour.UnityRunDueInvokes(); }
            foreach (var behaviour in behaviours) if (behaviour.UnityRunning) behaviour.UnitySendMessage("LateUpdate");
            foreach (var behaviour in behaviours) if (behaviour.UnityAlive) behaviour.UnityResumeCoroutines(endOfFrame: true);
            EndOfFrame();
        }

        /// <summary>A copy of <paramref name="original"/>: a GameObject with its components and children, the matching component of a copied object, or a copy of any other object's serialized fields.</summary>
        public static T Instantiate<T>(T original) where T : Object => (T)CloneObject(original, null, false);
        /// <summary>A copy under <paramref name="parent"/>, keeping the original's local position (Unity's default).</summary>
        public static T Instantiate<T>(T original, global::Transform parent) where T : Object => (T)CloneObject(original, parent, false);
        /// <summary>A copy under <paramref name="parent"/>; with <paramref name="instantiateInWorldSpace"/> it keeps the original's world position.</summary>
        public static T Instantiate<T>(T original, global::Transform parent, bool instantiateInWorldSpace) where T : Object => (T)CloneObject(original, parent, instantiateInWorldSpace);
        public static GameObject Instantiate(GameObject original, Vector3 position, Quaternion rotation, global::Transform parent)
        {
            s_unityPendingParent = parent; s_unityPendingWorldStays = true;
            try { return original.Clone(position, rotation); }
            finally { s_unityPendingParent = null; }
        }

        [ThreadStatic] internal static global::Transform? s_unityPendingParent;
        [ThreadStatic] internal static bool s_unityPendingWorldStays;
        [ThreadStatic] internal static Dictionary<Object, Object>? s_unityLastCloneMap;

        private static Object CloneObject(Object original, global::Transform? parent, bool worldStays)
        {
            if (original is null) throw new ArgumentNullException(nameof(original));
            original.ThrowIfDestroyed();
            if (original is GameObject go)
            {
                s_unityPendingParent = parent; s_unityPendingWorldStays = worldStays;
                try { return go.Clone(go.Position, go.Rotation); }
                finally { s_unityPendingParent = null; }
            }
            if (original is Component component)
            {
                var owner = component.m_gameObject ?? throw new ArgumentException("The component is on no GameObject; Unity copies a component's whole object.", nameof(original));
                Instantiate(owner, parent!, worldStays);
                return s_unityLastCloneMap![component];
            }
            return original.CopyForInstantiate();
        }
    }

    public partial class Component
    {
        /// <summary>The transform of the object this component sits on; a transform's own is itself.</summary>
        public global::Transform transform => this is global::Transform self ? self : gameObject?.transform!;
        public string tag { get => gameObject.tag; set => gameObject.tag = value; }
        public bool CompareTag(string tag) => gameObject.CompareTag(tag);
        public T[] GetComponents<T>() where T : class { ThrowIfDestroyed(); return m_gameObject is { } owner ? owner.GetComponents<T>() : new T[0]; }
        public bool TryGetComponent<T>(out T component) where T : class { component = GetComponent<T>(); return component != null; }
        public T GetComponentInChildren<T>(bool includeInactive = false) where T : class { ThrowIfDestroyed(); return m_gameObject is { } owner ? owner.GetComponentInChildren<T>(includeInactive) : null!; }
        public T[] GetComponentsInChildren<T>(bool includeInactive = false) where T : class { ThrowIfDestroyed(); return m_gameObject is { } owner ? owner.GetComponentsInChildren<T>(includeInactive) : new T[0]; }
        public T GetComponentInParent<T>(bool includeInactive = false) where T : class { ThrowIfDestroyed(); return m_gameObject is { } owner ? owner.GetComponentInParent<T>(includeInactive) : null!; }
    }

    public partial class Behaviour
    {
        internal bool m_behaviourEnabled = true;
        /// <summary>
        /// Switches the behaviour on or off. On a MonoBehaviour that has woken, on an object active in its hierarchy, this
        /// sends OnEnable or OnDisable at once, as Unity does.
        /// </summary>
        public bool enabled
        {
            get => m_behaviourEnabled;
            set { if (m_behaviourEnabled == value) return; m_behaviourEnabled = value; UnityEnabledChanged(); }
        }
        /// <summary>Enabled and on an object active in its hierarchy.</summary>
        public bool isActiveAndEnabled => m_behaviourEnabled && m_gameObject is { Destroyed: false } owner && owner.activeInHierarchy;
        private protected virtual void UnityEnabledChanged() { }
    }

    /// <summary>
    /// Unity's MonoBehaviour messages, called by name on the most derived declaration, private or public, as Unity does:
    /// <c>Awake</c> when the component is added to (or its object becomes) active in the hierarchy, then <c>OnEnable</c>
    /// if enabled; <c>Start</c> (which may be a coroutine) before its first <c>Update</c>; <c>Update</c> and
    /// <c>LateUpdate</c> each <see cref="Object.RunFrame"/> while enabled and active; <c>OnDisable</c> when disabled,
    /// deactivated or destroyed; <c>OnDestroy</c> when destroyed after having woken. Unity logs an exception thrown by a
    /// message and carries on; here it reaches the test. A behaviour built with <c>new</c> is on no object and gets no messages.
    /// </summary>
    public partial class MonoBehaviour
    {
        internal bool m_unityAwoken, m_unityStarted, m_unityEnableSent;
        private readonly List<Coroutine> m_coroutines = new();
        private readonly List<ScheduledInvoke> m_invokes = new();

        internal bool UnityAlive => !Destroyed && m_unityAwoken && m_gameObject is { Destroyed: false } owner && owner.activeInHierarchy;
        internal bool UnityRunning => UnityAlive && m_behaviourEnabled;

        internal void UnityBecameActive()
        {
            if (Destroyed) return;
            if (!m_unityAwoken) { m_unityAwoken = true; UnitySendMessage("Awake"); if (Destroyed) return; }
            if (m_behaviourEnabled && !m_unityEnableSent) { m_unityEnableSent = true; UnitySendMessage("OnEnable"); }
        }
        internal void UnityBecameInactive()
        {
            m_coroutines.Clear(); // Unity stops a deactivated object's coroutines for good
            if (m_unityEnableSent) { m_unityEnableSent = false; UnitySendMessage("OnDisable"); }
        }
        private protected override void UnityEnabledChanged()
        {
            if (!m_unityAwoken || Destroyed || m_gameObject is not { } owner || !owner.activeInHierarchy) return;
            if (m_behaviourEnabled && !m_unityEnableSent) { m_unityEnableSent = true; UnitySendMessage("OnEnable"); }
            else if (!m_behaviourEnabled && m_unityEnableSent) { m_unityEnableSent = false; UnitySendMessage("OnDisable"); }
        }
        private protected override void UnityDestroying()
        {
            if (m_unityEnableSent) { m_unityEnableSent = false; UnitySendMessage("OnDisable"); }
            m_coroutines.Clear(); m_invokes.Clear();
            if (m_unityAwoken) UnitySendMessage("OnDestroy");
        }

        internal void UnityRunStart()
        {
            var start = UnityFindMessage(GetType(), "Start");
            if (start == null) return;
            if (typeof(IEnumerator).IsAssignableFrom(start.ReturnType)) StartCoroutine((IEnumerator)Call(start)!);
            else Call(start);
        }
        internal void UnitySendMessage(string name) { var method = UnityFindMessage(GetType(), name); if (method != null) Call(method); }
        private object? Call(MethodInfo method)
        {
            try { return method.Invoke(this, null); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        private static readonly Dictionary<(Type, string), MethodInfo?> s_messages = new();
        internal static MethodInfo? UnityFindMessage(Type type, string name)
        {
            lock (s_messages)
            {
                if (s_messages.TryGetValue((type, name), out var cached)) return cached;
                MethodInfo? found = null;
                for (var t = type; t != null && t != typeof(MonoBehaviour) && found == null; t = t.BaseType)
                    found = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                return s_messages[(type, name)] = found;
            }
        }

        // ---- coroutines ----

        /// <summary>
        /// Runs the routine up to its first yield now, as Unity does, then resumes it in later frames: after <c>yield
        /// return null</c> (or any other value) in the next frame, after a <see cref="WaitForSeconds"/> once
        /// <c>Time.time</c> has advanced that far, after a <see cref="WaitForEndOfFrame"/> at the end of the frame, after
        /// a nested <see cref="IEnumerator"/> or <see cref="Coroutine"/> once it finishes. On an inactive object Unity
        /// refuses with an error and returns null; so does this. An exception from the routine ends it and reaches the test.
        /// </summary>
        public Coroutine StartCoroutine(IEnumerator routine)
        {
            if (routine == null) throw new ArgumentNullException(nameof(routine));
            if (m_gameObject is null) throw new InvalidOperationException("A coroutine needs a behaviour on a GameObject: attach it with AddComponent.");
            if (!UnityAlive)
            {
                Debug.LogError($"Coroutine couldn't be started because the the game object '{m_gameObject.name}' is inactive!");
                return null!;
            }
            var coroutine = new Coroutine(this, routine);
            m_coroutines.Add(coroutine);
            coroutine.Advance();
            return coroutine;
        }
        public void StopCoroutine(Coroutine routine) { if (routine != null && m_coroutines.Remove(routine)) routine.m_done = true; }
        public void StopCoroutine(IEnumerator routine)
        {
            foreach (var coroutine in m_coroutines.ToArray()) if (coroutine.m_routine == routine) StopCoroutine(coroutine);
        }
        public void StopAllCoroutines() { foreach (var coroutine in m_coroutines.ToArray()) StopCoroutine(coroutine); }
        internal void UnityRemoveCoroutine(Coroutine coroutine) => m_coroutines.Remove(coroutine);
        internal void UnityResumeCoroutines(bool endOfFrame)
        {
            foreach (var coroutine in m_coroutines.ToArray())
                if (!coroutine.m_done && m_coroutines.Contains(coroutine) && coroutine.IsDue(endOfFrame)) coroutine.Advance();
        }

        // ---- Invoke ----

        private sealed class ScheduledInvoke { public string Method = ""; public float Due; public float Repeat; }
        /// <summary>Calls the named method (any visibility, no parameters) once <c>Time.time</c> has advanced <paramref name="time"/> seconds, in a later frame.</summary>
        public void Invoke(string methodName, float time) => Schedule(methodName, time, 0f);
        /// <summary>As <see cref="Invoke"/>, then every <paramref name="repeatRate"/> seconds. A rate of 0.00001 or less throws, as in Unity.</summary>
        public void InvokeRepeating(string methodName, float time, float repeatRate)
        {
            if (repeatRate <= 0.00001f) throw new UnityException("Invoke repeat rate has to be larger than 0.00001F");
            Schedule(methodName, time, repeatRate);
        }
        public void CancelInvoke() => m_invokes.Clear();
        public void CancelInvoke(string methodName) => m_invokes.RemoveAll(i => i.Method == methodName);
        public bool IsInvoking() => m_invokes.Count > 0;
        public bool IsInvoking(string methodName) => m_invokes.Exists(i => i.Method == methodName);
        private void Schedule(string methodName, float time, float repeat)
        {
            if (UnityFindMessage(GetType(), methodName) == null) { Debug.LogError($"Trying to Invoke method: {GetType().Name}.{methodName} couldn't be called."); return; }
            m_invokes.Add(new ScheduledInvoke { Method = methodName, Due = Time.time + time, Repeat = repeat });
        }
        internal void UnityRunDueInvokes()
        {
            foreach (var invoke in m_invokes.ToArray())
                while (m_invokes.Contains(invoke) && invoke.Due <= Time.time && UnityAlive)
                {
                    if (invoke.Repeat > 0f) invoke.Due += invoke.Repeat; else m_invokes.Remove(invoke);
                    UnitySendMessage(invoke.Method);
                }
        }
    }

    /// <summary>Base of what a coroutine can wait for.</summary>
    public partial class YieldInstruction { }

    /// <summary>A running coroutine: <c>yield return</c> it to wait until it finishes.</summary>
    public sealed partial class Coroutine : YieldInstruction
    {
        internal readonly MonoBehaviour m_owner;
        internal readonly IEnumerator m_routine;
        private readonly Stack<IEnumerator> m_stack = new();
        private int m_yieldFrame;
        private float? m_resumeTime;
        private Coroutine? m_waitFor;
        private bool m_endOfFrame;
        internal bool m_done;

        internal Coroutine(MonoBehaviour owner, IEnumerator routine) { m_owner = owner; m_routine = routine; m_stack.Push(routine); }

        internal bool IsDue(bool endOfFramePhase)
        {
            if (m_endOfFrame) return endOfFramePhase;
            if (endOfFramePhase) return false;
            if (m_waitFor != null) return m_waitFor.m_done;
            if (m_resumeTime is float at) return Time.time >= at;
            return Time.frameCount > m_yieldFrame;
        }

        internal void Advance()
        {
            m_waitFor = null; m_resumeTime = null; m_endOfFrame = false;
            while (m_stack.Count > 0 && !m_done)
            {
                var top = m_stack.Peek();
                bool more;
                try { more = top.MoveNext(); }
                catch { Finish(); throw; }
                if (!more) { m_stack.Pop(); continue; }
                m_yieldFrame = Time.frameCount;
                switch (top.Current)
                {
                    case Coroutine other: if (other.m_done) continue; m_waitFor = other; return;
                    case WaitForSeconds wait: m_resumeTime = Time.time + wait.m_seconds; return;
                    case WaitForEndOfFrame: m_endOfFrame = true; return;
                    case IEnumerator nested: m_stack.Push(nested); continue;
                    default: return;
                }
            }
            Finish();
        }

        private void Finish() { m_done = true; m_owner.UnityRemoveCoroutine(this); }
    }

    /// <summary>Waits until <c>Time.time</c> has advanced by the given seconds.</summary>
    public sealed partial class WaitForSeconds : YieldInstruction
    {
        internal readonly float m_seconds;
        public WaitForSeconds(float seconds) => m_seconds = seconds;
    }

    /// <summary>Waits until the end of the current frame.</summary>
    public sealed partial class WaitForEndOfFrame : YieldInstruction { }

    /// <summary>A wait a coroutine checks every frame until <see cref="keepWaiting"/> is false.</summary>
    public abstract partial class CustomYieldInstruction : IEnumerator
    {
        public abstract bool keepWaiting { get; }
        public object? Current => null;
        public bool MoveNext() => keepWaiting;
        public virtual void Reset() { }
    }
    public sealed partial class WaitUntil : CustomYieldInstruction
    {
        private readonly Func<bool> m_predicate;
        public WaitUntil(Func<bool> predicate) => m_predicate = predicate;
        public override bool keepWaiting => !m_predicate();
    }
    public sealed partial class WaitWhile : CustomYieldInstruction
    {
        private readonly Func<bool> m_predicate;
        public WaitWhile(Func<bool> predicate) => m_predicate = predicate;
        public override bool keepWaiting => m_predicate();
    }

    /// <summary>
    /// Components, children and activation. <see cref="AddComponent{T}"/> wakes a MonoBehaviour at once when the object
    /// is active in its hierarchy, and otherwise when it becomes so, as Unity does; a prefab kept under an inactive
    /// parent therefore wakes only in its copies.
    /// </summary>
    public partial class GameObject
    {
        private readonly List<Component> m_components = new();
        private global::Transform? m_transform;
        internal bool m_activeSelf = true;
        public int layer;
        private string m_tag = "Untagged";

        public GameObject() : this("New Game Object") { }
        public GameObject(string name, params Type[] components) : this(name) { foreach (var type in components) AddComponent(type); }

        /// <summary>The object's transform, made on first use. Throws once the object is destroyed.</summary>
        public global::Transform transform { get { ThrowIfDestroyed(); return OwnTransform; } }
        internal global::Transform OwnTransform => m_transform ??= new global::Transform { gameObject = this };
        /// <summary>The world position (the transform's). Not a Unity member: the doubles' shorthand, kept for existing tests.</summary>
        public Vector3 Position { get => OwnTransform.position; set => OwnTransform.position = value; }
        /// <summary>The rotation (the transform's). Not a Unity member.</summary>
        public Quaternion Rotation { get => OwnTransform.rotation; set => OwnTransform.rotation = value; }

        public string tag { get { ThrowIfDestroyed(); return m_tag; } set { ThrowIfDestroyed(); m_tag = value; } }
        public bool CompareTag(string tag) { ThrowIfDestroyed(); return m_tag == tag; }

        public bool activeSelf => m_activeSelf;
        /// <summary>Active itself and every parent active, as Unity's.</summary>
        public bool activeInHierarchy => m_activeSelf && (m_transform?.m_parent?.m_gameObject is not { } parent || parent.activeInHierarchy);

        /// <summary>Activates or deactivates the object; the behaviours whose hierarchy activity changes get Awake/OnEnable or OnDisable, as in Unity.</summary>
        public void SetActive(bool value)
        {
            ThrowIfDestroyed();
            if (m_activeSelf == value) return;
            bool was = activeInHierarchy;
            m_activeSelf = value;
            if (activeInHierarchy != was) HierarchyActivityChanged(this, !was);
        }

        internal static void HierarchyActivityChanged(GameObject go, bool active)
        {
            foreach (var component in go.m_components.ToArray())
                if (component is MonoBehaviour behaviour && !behaviour.Destroyed) { if (active) behaviour.UnityBecameActive(); else behaviour.UnityBecameInactive(); }
            if (go.m_transform is { } t)
                foreach (var child in t.m_children.ToArray())
                    if (child.m_gameObject is { m_activeSelf: true } childObject && !childObject.Destroyed) HierarchyActivityChanged(childObject, active);
        }

        /// <summary>Adds a new component of the type, as Unity's; a MonoBehaviour wakes at once if the object is active in its hierarchy.</summary>
        public T AddComponent<T>() where T : Component => (T)AddComponent(typeof(T));
        public Component AddComponent(Type componentType)
        {
            ThrowIfDestroyed();
            if (!typeof(Component).IsAssignableFrom(componentType) || componentType.IsAbstract)
                throw new ArgumentException($"{componentType.Name} is not a concrete component type.", nameof(componentType));
            var component = UnitySerialization.NewInstance(componentType) as Component
                ?? throw new ArgumentException($"{componentType.Name} is not a component.", nameof(componentType));
            Attach(component);
            if (component is MonoBehaviour behaviour && activeInHierarchy) behaviour.UnityBecameActive();
            return component;
        }
        internal void Attach(Component component)
        {
            component.m_gameObject = this;
            m_components.Add(component);
            s_unityComponents.Add(component);
        }

        /// <summary>The transform, then the ZNetView and WearNTear, then added components in order: Unity lists the transform first.</summary>
        private IEnumerable<Component> AllComponents()
        {
            yield return OwnTransform;
            if (View is { } view) yield return view;
            if (Wear is { } wear) yield return wear;
            foreach (var component in m_components) yield return component;
        }

        /// <summary>The first live component of type <typeparamref name="T"/> (a base type or interface matches too), or null. Throws once the object is destroyed.</summary>
        public T GetComponent<T>() where T : class
        {
            ThrowIfDestroyed();
            foreach (var component in AllComponents()) if (!component.Destroyed && component is T match) return match;
            return null!;
        }
        /// <summary>
        /// The first live component that is a <paramref name="type"/> (a base type or interface matches too), or null, as
        /// <see cref="GetComponent{T}"/>. As Unity, refuses a type that is neither a component nor an interface.
        /// </summary>
        public Component GetComponent(Type type)
        {
            ThrowIfDestroyed();
            CheckComponentType(type);
            foreach (var component in AllComponents()) if (!component.Destroyed && type.IsInstanceOfType(component)) return component;
            return null!;
        }
        internal static void CheckComponentType(Type type)
        {
            if (type is null) throw new ArgumentNullException(nameof(type));
            if (!typeof(Component).IsAssignableFrom(type) && !type.IsInterface)
                throw new ArgumentException($"GetComponent requires that the requested component '{type.Name}' derives from MonoBehaviour or Component or is an interface.");
        }

        /// <summary>
        /// Not a Unity member: true for an object that stands for one of the game's assets, such as a prefab loaded from
        /// its asset bundles (<see cref="global::ZNetScene.AddPrefab"/> sets it). An asset, its children and their
        /// components are not in the scene, so FindObjectsByType does not find them; <c>Instantiate</c>'s copies are.
        /// </summary>
        public bool IsAsset;
        /// <summary>In the scene: neither it nor any parent is an asset.</summary>
        internal bool InScene
        {
            get
            {
                for (GameObject? go = this; go is not null; go = go.m_transform?.m_parent?.m_gameObject) if (go.IsAsset) return false;
                return true;
            }
        }
        public T[] GetComponents<T>() where T : class
        {
            ThrowIfDestroyed();
            var found = new List<T>();
            foreach (var component in AllComponents()) if (!component.Destroyed && component is T match) found.Add(match);
            return found.ToArray();
        }
        public bool TryGetComponent<T>(out T component) where T : class { component = GetComponent<T>(); return component != null; }

        /// <summary>Depth first through this object and its children; without <paramref name="includeInactive"/> only objects active in their hierarchy count, as in Unity.</summary>
        public T GetComponentInChildren<T>(bool includeInactive = false) where T : class
        {
            var all = GetComponentsInChildren<T>(includeInactive);
            return all.Length == 0 ? null! : all[0];
        }
        public T[] GetComponentsInChildren<T>(bool includeInactive = false) where T : class
        {
            ThrowIfDestroyed();
            var found = new List<T>();
            Collect(this, includeInactive, found);
            return found.ToArray();
        }
        private static void Collect<T>(GameObject go, bool includeInactive, List<T> found) where T : class
        {
            if (includeInactive || go.activeInHierarchy)
                foreach (var component in go.AllComponents()) if (!component.Destroyed && component is T match) found.Add(match);
            if (go.m_transform is { } t)
                foreach (var child in t.m_children)
                    if (child.m_gameObject is { Destroyed: false } childObject) Collect(childObject, includeInactive, found);
        }
        /// <summary>This object, then its parents; without <paramref name="includeInactive"/> only objects active in their hierarchy count.</summary>
        public T GetComponentInParent<T>(bool includeInactive = false) where T : class
        {
            ThrowIfDestroyed();
            for (GameObject? go = this; go != null; go = go.m_transform?.m_parent?.m_gameObject)
                if (includeInactive || go.activeInHierarchy)
                    foreach (var component in go.AllComponents()) if (!component.Destroyed && component is T match) return match;
            return null!;
        }

        // Destroying an object disables its hierarchy (OnDisable), then destroys its behaviours (OnDestroy) and children
        // while they are still alive, and takes it out of its parent, as Unity does.
        private protected override void UnityDestroying()
        {
            if (activeInHierarchy) HierarchyActivityChanged(this, false);
            foreach (var component in m_components.ToArray()) DestroyImmediate(component);
            if (m_transform is { } t)
            {
                foreach (var child in t.m_children.ToArray()) if (child.m_gameObject is { } childObject) DestroyImmediate(childObject);
                t.Detach();
                DestroyImmediate(t);
            }
        }

        // Called by Clone (UnityDoubles.cs): copies the components, children and activation of this object into the copy,
        // with Unity's rules for serialized fields (UnitySerialization), then parents it and wakes it.
        internal void CopyHierarchyInto(GameObject copy)
        {
            // Take this copy's parent now: an Instantiate in a woken Awake must not see it (nor leave its own map behind).
            var parent = s_unityPendingParent; bool worldStays = s_unityPendingWorldStays;
            s_unityPendingParent = null;
            var map = new Dictionary<Object, Object>();
            var pairs = new List<(Component Source, Component Copy)>();
            MapHierarchy(this, copy, map, pairs);
            foreach (var (source, target) in pairs) UnitySerialization.CopySerializedFields(source, target, map);
            if (copy.View is { } view) s_unityComponents.Add(view);
            if (copy.Wear is { } wear) s_unityComponents.Add(wear);
            if (parent is not null)
            {
                if (!worldStays)
                {
                    copy.OwnTransform.position = OwnTransform.localPosition;
                    copy.OwnTransform.rotation = OwnTransform.localRotation;
                }
                copy.OwnTransform.SetParent(parent, worldStays);
            }
            if (copy.activeInHierarchy) HierarchyActivityChanged(copy, true);
            s_unityLastCloneMap = map;
        }
        private static void MapHierarchy(GameObject source, GameObject copy, Dictionary<Object, Object> map, List<(Component, Component)> pairs)
        {
            map[source] = copy; map[source.OwnTransform] = copy.OwnTransform;
            if (source.View is { } view && copy.View is { } copiedView) map[view] = copiedView;
            if (source.Wear is { } wear && copy.Wear is { } copiedWear) map[wear] = copiedWear;
            copy.m_activeSelf = source.m_activeSelf; copy.m_tag = source.m_tag; copy.layer = source.layer;
            copy.OwnTransform.localScale = source.OwnTransform.localScale;
            foreach (var component in source.m_components)
            {
                if (component.Destroyed) continue;
                var target = (Component)UnitySerialization.NewInstance(component.GetType());
                if (component is Behaviour behaviour) ((Behaviour)target).m_behaviourEnabled = behaviour.m_behaviourEnabled;
                copy.Attach(target);
                map[component] = target;
                pairs.Add((component, target));
            }
            foreach (var child in source.OwnTransform.m_children)
            {
                if (child.m_gameObject is not { Destroyed: false } childObject) continue;
                var childCopy = new GameObject(childObject.name) { Networked = false };
                childCopy.OwnTransform.SetParent(copy.OwnTransform, false);
                childCopy.OwnTransform.localPosition = child.localPosition;
                childCopy.OwnTransform.localRotation = child.localRotation;
                MapHierarchy(childObject, childCopy, map, pairs);
            }
        }
    }

    /// <summary>A reusable data asset, as Unity's ScriptableObject.</summary>
    public partial class ScriptableObject : Object
    {
        public static T CreateInstance<T>() where T : ScriptableObject => (T)UnitySerialization.NewInstance(typeof(T));
        public static ScriptableObject CreateInstance(Type type) => (ScriptableObject)UnitySerialization.NewInstance(type);
    }

    /// <summary>
    /// What Unity copies when it instantiates: fields that are public (or carry <see cref="SerializeField"/>), not static,
    /// readonly or <see cref="NonSerializedAttribute"/>, of a type Unity serializes: primitives, enums, strings, structs,
    /// references to Unity objects, <c>[Serializable]</c> classes, and arrays and <c>List&lt;T&gt;</c> of those. Lists,
    /// arrays and serializable classes are copied, so the copy has its own; references to objects inside the copied
    /// hierarchy point at their copies, other references are shared. Every other field keeps the value the type's
    /// constructor gives it (dictionaries, delegates, private state).
    /// </summary>
    internal static class UnitySerialization
    {
        private const int MaxDepth = 10;

        internal static Object NewInstance(Type type)
        {
            try { return (Object)Activator.CreateInstance(type, nonPublic: true)!; }
            catch (MissingMethodException) { throw new InvalidOperationException($"{type.Name} has no parameterless constructor, so the doubles cannot create it as Unity does."); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                // The constructor itself threw (a plugin without its BepInPlugin attribute, say): report that exception.
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        internal static void CopySerializedFields(object source, object target, Dictionary<Object, Object> map) => CopyFields(source, target, source.GetType(), map, 0);

        private static void CopyFields(object source, object target, Type type, Dictionary<Object, Object> map, int depth)
        {
            for (var t = type; t != null && t != typeof(object) && t != typeof(Object) && t != typeof(Component) && t != typeof(Behaviour) && t != typeof(MonoBehaviour); t = t.BaseType)
                foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (IsSerialized(field)) field.SetValue(target, CopyValue(field.GetValue(source), field.FieldType, map, depth));
        }

        // [NonSerialized] and [Serializable] are read as attributes: FieldInfo.IsNotSerialized and TypeAttributes.Serializable
        // name the same metadata flags but are obsolete on .NET 8 and later (SYSLIB0050), and this source compiles into the
        // consumer's project, where the warning would be the mod's.
        private static bool IsSerialized(FieldInfo field) =>
            !field.IsInitOnly && !field.IsLiteral && !field.IsDefined(typeof(NonSerializedAttribute), false) &&
            (field.IsPublic || field.IsDefined(typeof(SerializeField), false)) && IsSerializable(field.FieldType);

        private static bool IsSerializable(Type type)
        {
            if (type == typeof(object)) return false;
            if (type.IsPrimitive || type.IsEnum || type == typeof(string) || typeof(Object).IsAssignableFrom(type)) return true;
            if (type.IsArray) return type.GetArrayRank() == 1 && IsSerializable(type.GetElementType()!);
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)) return IsSerializable(type.GetGenericArguments()[0]);
            if (type.IsValueType) return true;
            if (type.IsGenericType || type.IsAbstract || type.IsInterface || typeof(Delegate).IsAssignableFrom(type)) return false;
            return type.IsDefined(typeof(SerializableAttribute), false);
        }

        private static object? CopyValue(object? value, Type type, Dictionary<Object, Object> map, int depth)
        {
            if (value == null) return null;
            if (value is Object reference) return map.TryGetValue(reference, out var copied) ? copied : reference;
            if (type.IsPrimitive || type.IsEnum || type == typeof(string)) return value;
            if (type.IsArray)
            {
                var array = (Array)value; var element = type.GetElementType()!;
                var copy = Array.CreateInstance(element, array.Length);
                for (int i = 0; i < array.Length; i++) copy.SetValue(CopyValue(array.GetValue(i), element, map, depth), i);
                return copy;
            }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                var element = type.GetGenericArguments()[0];
                var list = (IList)Activator.CreateInstance(type)!;
                foreach (var item in (IList)value) list.Add(CopyValue(item, element, map, depth));
                return list;
            }
            if (type.IsValueType) return value;
            if (depth >= MaxDepth) return null;
            var instance = Activator.CreateInstance(type, nonPublic: true)!;
            CopyFields(value, instance, type, map, depth + 1);
            return instance;
        }
    }

    public static partial class Time
    {
        /// <summary>Game time in seconds; <see cref="Object.RunFrame"/> advances it.</summary>
        public static float time;
        /// <summary>The last frame's length in seconds.</summary>
        public static float deltaTime;
        /// <summary>Frames run so far.</summary>
        public static int frameCount;
    }

    /// <summary>Unity's log. Lines go to the log capture (<c>ManualLogSource.Captured</c>) and the console.</summary>
    public static partial class Debug
    {
        public static void Log(object message) => Write("INFO ", message);
        public static void LogWarning(object message) => Write("WARN ", message);
        public static void LogError(object message) => Write("ERROR", message);
        public static void LogException(Exception exception) => Write("ERROR", exception);
        private static void Write(string level, object message)
        {
            string line = message?.ToString() ?? "Null";
            BepInEx.Logging.ManualLogSource.Captured?.Add(line);
            Console.WriteLine($"[{level}] {line}");
        }
    }

    /// <summary>
    /// Unity's random numbers with Unity's ranges (an int range excludes its maximum, a float range includes it), from a
    /// seeded generator. The ranges and the seeding are Unity's; the sequence is not, so a test fixes outcomes by seed.
    /// </summary>
    public static partial class Random
    {
        private static System.Random s_random = new(0);
        public static void InitState(int seed) => s_random = new System.Random(seed);
        /// <summary>A float from 0 to 1, both included.</summary>
        public static float value => (float)(s_random.Next(0, 16777217) / 16777216.0);
        public static float Range(float minInclusive, float maxInclusive) => minInclusive + (maxInclusive - minInclusive) * value;
        /// <summary>An int from the minimum up to but not including the maximum; the minimum when both are equal, and a reversed range is swapped, as Unity documents.</summary>
        public static int Range(int minInclusive, int maxExclusive) =>
            maxExclusive == minInclusive ? minInclusive : maxExclusive > minInclusive ? s_random.Next(minInclusive, maxExclusive) : s_random.Next(maxExclusive, minInclusive);
        public static Vector2 insideUnitCircle
        {
            get
            {
                while (true)
                {
                    var point = new Vector2(Range(-1f, 1f), Range(-1f, 1f));
                    if (point.sqrMagnitude <= 1f) return point;
                }
            }
        }
    }
}

/// <summary>
/// The transform hierarchy. Parent rotation composes with child rotation, but does not rotate child position;
/// world position is the parent's position plus its scale times local position. New transforms start unrotated.
/// Re-parenting keeps world position and rotation by default and sends OnEnable/OnDisable when it changes
/// whether the object is active in its hierarchy, as Unity does.
/// </summary>
public partial class Transform : UnityEngine.Component, IEnumerable
{
    internal Transform? m_parent;
    internal readonly List<Transform> m_children = new();
    private UnityEngine.Vector3 m_localPosition;
    private UnityEngine.Vector3 m_localScale = new(1f, 1f, 1f);

    public UnityEngine.Vector3 position
    {
        get => m_parent is null ? m_localPosition : m_parent.TransformPoint(m_localPosition);
        set => m_localPosition = m_parent is null ? value : m_parent.InverseTransformPoint(value);
    }
    public UnityEngine.Vector3 localPosition { get => m_localPosition; set => m_localPosition = value; }
    private UnityEngine.Quaternion m_localRotation = UnityEngine.Quaternion.identity;
    public UnityEngine.Quaternion rotation
    {
        get => m_parent is { } up ? up.rotation * m_localRotation : m_localRotation;
        set => m_localRotation = m_parent is { } up ? UnityEngine.Quaternion.Inverse(up.rotation) * value : value;
    }
    /// <summary>The rotation relative to the parent.</summary>
    public UnityEngine.Quaternion localRotation { get => m_localRotation; set => m_localRotation = value; }
    /// <summary>The world rotation as Euler angles in degrees (<see cref="UnityEngine.Quaternion.eulerAngles"/>).</summary>
    public UnityEngine.Vector3 eulerAngles { get => rotation.eulerAngles; set => rotation = UnityEngine.Quaternion.Euler(value); }
    public UnityEngine.Vector3 localScale { get => m_localScale; set => m_localScale = value; }
    public UnityEngine.Vector3 lossyScale => m_parent is null ? m_localScale : Scale(m_parent.lossyScale, m_localScale);

    /// <summary>A local point in world space: position plus scale times the point (no rotation).</summary>
    public UnityEngine.Vector3 TransformPoint(UnityEngine.Vector3 point) => position + Scale(lossyScale, point);
    /// <summary>A world point in local space (no rotation).</summary>
    public UnityEngine.Vector3 InverseTransformPoint(UnityEngine.Vector3 point)
    {
        var offset = point - position; var scale = lossyScale;
        return new UnityEngine.Vector3(offset.x / scale.x, offset.y / scale.y, offset.z / scale.z);
    }
    private static UnityEngine.Vector3 Scale(UnityEngine.Vector3 a, UnityEngine.Vector3 b) => new(a.x * b.x, a.y * b.y, a.z * b.z);

    public Transform? parent { get => m_parent; set => SetParent(value, true); }
    public Transform root { get { var t = this; while (t.m_parent is { } up) t = up; return t; } }
    public int childCount => m_children.Count;
    public Transform GetChild(int index) => m_children[index];
    public IEnumerator GetEnumerator() => m_children.ToArray().GetEnumerator();
    public bool IsChildOf(Transform parent) { for (Transform? t = this; t is not null; t = t.m_parent) if (ReferenceEquals(t, parent)) return true; return false; }
    public int GetSiblingIndex() => m_parent is null ? 0 : m_parent.m_children.IndexOf(this);

    /// <summary>The child with that name, or a descendant by a path of names separated by '/'; null when there is none.</summary>
    public Transform? Find(string n)
    {
        Transform? at = this;
        foreach (var part in n.Split('/'))
        {
            at = at!.m_children.Find(c => c.m_gameObject is { Destroyed: false } child && child.name == part);
            if (at is null) return null;
        }
        return at;
    }

    public void SetParent(Transform? parent) => SetParent(parent, true);
    /// <summary>Moves this transform under <paramref name="parent"/> (or to the root); with <paramref name="worldPositionStays"/> the world position and rotation are kept, otherwise the local ones.</summary>
    public void SetParent(Transform? parent, bool worldPositionStays)
    {
        if (parent is not null && parent.IsChildOf(this)) throw new InvalidOperationException("A transform cannot become a child of itself or of one of its children.");
        var go = m_gameObject;
        bool wasActive = go?.activeInHierarchy ?? false;
        var world = position;
        var worldRotation = rotation;
        Detach();
        m_parent = parent;
        parent?.m_children.Add(this);
        if (worldPositionStays) { position = world; rotation = worldRotation; }
        if (go is { Destroyed: false } && go.activeInHierarchy != wasActive) UnityEngine.GameObject.HierarchyActivityChanged(go, !wasActive);
    }
    internal void Detach()
    {
        m_parent?.m_children.Remove(this);
        m_parent = null;
    }
}
