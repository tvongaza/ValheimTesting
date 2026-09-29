using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;
using Object = UnityEngine.Object;

// A mod behaviour that records Unity's messages, in the private-method form mods write them.
public sealed class Recorder : MonoBehaviour
{
    public static readonly List<string> Log = new();
    public string Label = "";
    private void Awake() => Log.Add(Label + ":Awake");
    private void OnEnable() => Log.Add(Label + ":OnEnable");
    private void Start() => Log.Add(Label + ":Start");
    private void Update() => Log.Add(Label + ":Update");
    private void LateUpdate() => Log.Add(Label + ":LateUpdate");
    private void OnDisable() => Log.Add(Label + ":OnDisable");
    private void OnDestroy() => Log.Add(Label + ":OnDestroy of " + gameObject.name); // the object is still alive here, as in Unity
}

public sealed class Ticker : MonoBehaviour
{
    public readonly List<string> Steps = new();
    public IEnumerator Waits()
    {
        Steps.Add("first@" + Time.frameCount);
        yield return null;
        Steps.Add("next@" + Time.frameCount);
        yield return new WaitForSeconds(0.5f);
        Steps.Add("seconds@" + Time.frameCount);
        yield return Nested();
        Steps.Add("after-nested@" + Time.frameCount);
    }
    private IEnumerator Nested() { Steps.Add("nested@" + Time.frameCount); yield return null; }
    private int m_ticks;
    private void Tick() => Steps.Add("tick" + ++m_ticks);
}

public interface IMarked { }
public sealed class MarkedBehaviour : MonoBehaviour, IMarked { }

// Serialized fields as a mod's component has them: a list, a [Serializable] class, a reference into its own hierarchy,
// a private [SerializeField], a dictionary (not serialized) and a private field (not serialized).
public sealed class LootHolder : MonoBehaviour
{
    [Serializable] public sealed class Entry { public string Item = ""; public int Count; }
    public List<Entry> Entries = new();
    public GameObject? Lid;
    public GameObject? External;
    [SerializeField] private int m_serializedPrivate;
    public Dictionary<string, int> NotSerialized = new();
    private int m_state;
    public int SerializedPrivate { get => m_serializedPrivate; set => m_serializedPrivate = value; }
    public int State { get => m_state; set => m_state = value; }
}

public sealed class UnityComponentTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new ValheimWorldScope().WithScene();
    public UnityComponentTests() => Recorder.Log.Clear();
    public void Dispose() { Object.EndOfFrame(); _scope.Dispose(); }

    [Fact] public void AddComponentWakesAtOnceOnAnActiveObjectAndOnlyOnActivationUnderAnInactiveParent()
    {
        var active = new GameObject("active");
        active.AddComponent<Recorder>().Label = "a"; // Awake and OnEnable already ran, before the label was set
        Assert.Equal(new[] { ":Awake", ":OnEnable" }, Recorder.Log);
        Recorder.Log.Clear();
        var container = new GameObject("container"); container.SetActive(false);
        var prefab = new GameObject("prefab"); prefab.transform.SetParent(container.transform);
        var dormant = prefab.AddComponent<Recorder>(); dormant.Label = "p";
        Assert.Empty(Recorder.Log); Assert.False(prefab.activeInHierarchy); Assert.True(prefab.activeSelf);
        container.SetActive(true);
        Assert.Equal(new[] { "p:Awake", "p:OnEnable" }, Recorder.Log);
    }

    [Fact] public void StartRunsBeforeTheFirstUpdateAndDisablingStopsUpdates()
    {
        var go = new GameObject("go");
        var recorder = go.AddComponent<Recorder>(); recorder.Label = "r"; Recorder.Log.Clear();
        Object.RunFrame(); Object.RunFrame();
        Assert.Equal(new[] { "r:Start", "r:Update", "r:LateUpdate", "r:Update", "r:LateUpdate" }, Recorder.Log);
        Recorder.Log.Clear();
        recorder.enabled = false; Object.RunFrame(); recorder.enabled = false;
        recorder.enabled = true; Object.RunFrame();
        Assert.Equal(new[] { "r:OnDisable", "r:OnEnable", "r:Update", "r:LateUpdate" }, Recorder.Log);
    }

    [Fact] public void DestroySendsOnDisableAndOnDestroyAtTheEndOfTheFrameWhileTheObjectIsStillAlive()
    {
        var parent = new GameObject("parent"); var child = new GameObject("child"); child.transform.SetParent(parent.transform);
        parent.AddComponent<Recorder>().Label = "p"; child.AddComponent<Recorder>().Label = "c"; Recorder.Log.Clear();
        Object.Destroy(parent);
        Assert.Empty(Recorder.Log);
        Object.EndOfFrame();
        // The hierarchy is disabled first, then each behaviour is destroyed.
        Assert.Equal(new[] { "p:OnDisable", "c:OnDisable", "p:OnDestroy of parent", "c:OnDestroy of child" }, Recorder.Log);
        Assert.True(child == null); Assert.NotEqual(parent.GetInstanceID(), child!.GetInstanceID());
    }

    [Fact] public void CoroutinesResumeByFrameByGameTimeAndAfterANestedRoutine()
    {
        var ticker = new GameObject("t").AddComponent<Ticker>();
        int start = Time.frameCount;
        ticker.StartCoroutine(ticker.Waits());
        Assert.Equal(new[] { $"first@{start}" }, ticker.Steps);
        Object.RunFrame(0.2f); // next frame
        Object.RunFrame(0.2f); Object.RunFrame(0.2f); // 0.4 s: still waiting for 0.5 s
        Assert.Equal(2, ticker.Steps.Count);
        Object.RunFrame(0.2f); // 0.6 s
        Assert.Equal(new[] { $"first@{start}", $"next@{start + 1}", $"seconds@{start + 4}", $"nested@{start + 4}" }, ticker.Steps);
        Object.RunFrame();
        Assert.Equal($"after-nested@{start + 5}", ticker.Steps.Last());
    }

    [Fact] public void ACoroutineStopsWhenItsObjectIsDeactivatedAndCannotStartOnAnInactiveOne()
    {
        var log = _scope.CaptureLog();
        var ticker = new GameObject("t").AddComponent<Ticker>();
        ticker.StartCoroutine(ticker.Waits());
        ticker.gameObject.SetActive(false); ticker.gameObject.SetActive(true);
        Object.RunFrame(); Object.RunFrame();
        Assert.Single(ticker.Steps); // not resumed after reactivation, as in Unity
        ticker.gameObject.SetActive(false);
        Assert.Null(ticker.StartCoroutine(ticker.Waits()));
        Assert.Contains(log, line => line.Contains("Coroutine couldn't be started") && line.Contains("'t' is inactive"));
        Assert.Throws<InvalidOperationException>(() => new Ticker().StartCoroutine(ticker.Waits())); // not on an object at all
    }

    [Fact] public void InvokeRunsByGameTimeAndCanBeCancelled()
    {
        var ticker = new GameObject("t").AddComponent<Ticker>();
        ticker.InvokeRepeating("Tick", 0.1f, 0.25f);
        Object.RunFrame(0.05f); Assert.Empty(ticker.Steps);
        Object.RunFrame(0.06f); Assert.Equal(new[] { "tick1" }, ticker.Steps);
        Object.RunFrame(0.5f); Assert.Equal(new[] { "tick1", "tick2", "tick3" }, ticker.Steps); // 0.35 and 0.60 were due by 0.61
        Assert.True(ticker.IsInvoking("Tick"));
        ticker.CancelInvoke("Tick"); Object.RunFrame(1f);
        Assert.Equal(3, ticker.Steps.Count); Assert.False(ticker.IsInvoking());
        Assert.Throws<UnityException>(() => ticker.InvokeRepeating("Tick", 0f, 0f));
        var log = _scope.CaptureLog(); ticker.Invoke("Missing", 0f);
        Assert.False(ticker.IsInvoking()); Assert.Contains("Trying to Invoke method: Ticker.Missing couldn't be called.", log);
    }

    [Fact] public void ComponentLookupsFollowTheHierarchyActivityAndTypes()
    {
        var root = new GameObject("root"); var hidden = new GameObject("hidden"); var leaf = new GameObject("leaf");
        hidden.transform.SetParent(root.transform); leaf.transform.SetParent(hidden.transform);
        var rootBox = root.AddComponent<BoxCollider>(); var leafSphere = leaf.AddComponent<SphereCollider>(); leaf.AddComponent<MarkedBehaviour>();
        hidden.SetActive(false);
        Assert.Equal(new Collider[] { rootBox }, root.GetComponentsInChildren<Collider>());
        Assert.Equal(new Collider[] { rootBox, leafSphere }, root.GetComponentsInChildren<Collider>(includeInactive: true));
        Assert.Null(leaf.GetComponentInParent<SphereCollider>()); // on an inactive object
        Assert.Same(leafSphere, leaf.GetComponentInParent<SphereCollider>(includeInactive: true));
        Assert.Same(rootBox, leaf.GetComponentInParent<BoxCollider>());
        Assert.NotNull(leaf.GetComponent<IMarked>()); Assert.True(leaf.TryGetComponent<SphereCollider>(out var found)); Assert.Same(leafSphere, found);
        Assert.Same(leaf.transform, leaf.GetComponent<Transform>()); Assert.Same(leaf.transform, leafSphere.transform);
        Assert.Equal(new[] { leafSphere }, Object.FindObjectsByType<SphereCollider>(FindObjectsInactive.Include, FindObjectsSortMode.None));
        Assert.Empty(Object.FindObjectsByType<SphereCollider>(FindObjectsSortMode.None));
        Assert.Same(rootBox, Object.FindFirstObjectByType<Collider>());
    }

    [Fact] public void InstantiateCopiesSerializedFieldsAndRemapsReferencesIntoTheCopy()
    {
        var external = new GameObject("elsewhere");
        var chest = new GameObject("chest"); var lid = new GameObject("lid"); lid.transform.SetParent(chest.transform, false);
        lid.transform.localPosition = new Vector3(1, 1, 0);
        var holder = chest.AddComponent<LootHolder>();
        holder.Entries.Add(new LootHolder.Entry { Item = "Coins", Count = 5 }); holder.Lid = lid; holder.External = external;
        holder.SerializedPrivate = 7; holder.State = 9; holder.NotSerialized["x"] = 1;
        var copy = Object.Instantiate(chest, new Vector3(10, 0, 0), default(Quaternion));
        var copied = copy.GetComponent<LootHolder>();
        Assert.NotSame(holder, copied); Assert.NotSame(holder.Entries, copied.Entries); Assert.NotSame(holder.Entries[0], copied.Entries[0]);
        Assert.Equal("Coins", copied.Entries[0].Item); Assert.Equal(7, copied.SerializedPrivate);
        Assert.Equal(0, copied.State); Assert.Empty(copied.NotSerialized); // not serialized: the constructor's values
        Assert.Same(copy.transform.GetChild(0).gameObject, copied.Lid); Assert.Same(external, copied.External);
        Assert.Equal(11f, copied.Lid!.transform.position.x); Assert.Equal(1f, copied.Lid.transform.position.y);
        copied.Entries[0].Count = 1; Assert.Equal(5, holder.Entries[0].Count);
        var component = Object.Instantiate(holder); // a component copies its whole object
        Assert.NotSame(chest, component.gameObject); Assert.Equal("chest", component.gameObject.name);
    }

    [Fact] public void AnInstantiatedInactivePrefabStaysAsleepAndACopyUnderAParentKeepsItsLocalPosition()
    {
        var prefab = new GameObject("prefab"); prefab.SetActive(false);
        prefab.AddComponent<Recorder>().Label = "copy";
        var copy = Object.Instantiate(prefab);
        Assert.False(copy.activeSelf); Assert.Empty(Recorder.Log);
        copy.SetActive(true);
        Assert.Equal(new[] { "copy:Awake", "copy:OnEnable" }, Recorder.Log);
        var parent = new GameObject("parent") { Position = new Vector3(100, 0, 0) };
        var source = new GameObject("source") { Position = new Vector3(1, 2, 3) };
        var local = Object.Instantiate(source, parent.transform);
        var world = Object.Instantiate(source, parent.transform, instantiateInWorldSpace: true);
        Assert.Equal(101f, local.transform.position.x); Assert.Equal(1f, world.transform.position.x);
        Assert.Same(parent.transform, local.transform.parent);
    }

    [Fact] public void ChildrenFollowTheirParentAndReparentingKeepsTheWorldPositionByDefault()
    {
        var parent = new GameObject("parent"); var child = new GameObject("child");
        child.Position = new Vector3(5, 0, 0);
        child.transform.SetParent(parent.transform);
        Assert.Equal(5f, child.transform.localPosition.x);
        parent.transform.position = new Vector3(10, 0, 0); parent.transform.localScale = new Vector3(2, 2, 2);
        Assert.Equal(20f, child.transform.position.x); Assert.Equal(2f, child.transform.lossyScale.x);
        child.transform.SetParent(null, worldPositionStays: false);
        Assert.Equal(5f, child.transform.position.x);
        child.transform.SetParent(parent.transform); var grand = new GameObject("grand"); grand.transform.SetParent(child.transform);
        Assert.Same(grand.transform, parent.transform.Find("child/grand")); Assert.Null(parent.transform.Find("grand"));
        Assert.Same(parent.transform, grand.transform.root);
        Assert.Throws<InvalidOperationException>(() => parent.transform.SetParent(grand.transform));
        Assert.Equal("/parent/child/grand", grand.transform.GetPath());
    }

    [Fact] public void RenderersCopyTheSharedMaterialOnFirstUseOfMaterial()
    {
        var shared = new Material(Shader.Find("Standard")) { name = "stone" };
        shared.color = new Color(1, 0, 0);
        var a = new GameObject("a").AddComponent<MeshRenderer>(); var b = new GameObject("b").AddComponent<MeshRenderer>();
        a.sharedMaterial = shared; b.sharedMaterial = shared;
        var own = a.material!;
        own.color = new Color(0, 1, 0);
        Assert.Equal("stone (Instance)", own.name); Assert.Same(own, a.material); Assert.Same(own, a.sharedMaterial);
        Assert.Equal(1f, shared.color.r); Assert.Same(shared, b.sharedMaterial); Assert.Equal("Standard", own.shader!.name);
        var filter = new GameObject("m").AddComponent<MeshFilter>();
        var mesh = new Mesh { name = "rock" }; filter.sharedMesh = mesh;
        Assert.Equal("rock Instance", filter.mesh.name); Assert.NotSame(mesh, filter.sharedMesh); Assert.Same(filter.mesh, filter.mesh);
    }

    [Fact] public void MeshBoundsFollowTheTrianglesAndBadIndicesAreRefused()
    {
        var log = _scope.CaptureLog();
        var mesh = new Mesh { vertices = new[] { new Vector3(0, 0, 0), new Vector3(2, 0, 0), new Vector3(0, 4, 0) } };
        Assert.Equal(default, mesh.bounds); // vertices alone do not recalculate
        mesh.triangles = new[] { 0, 1, 2 };
        Assert.Equal(2f, mesh.bounds.size.x); Assert.Equal(4f, mesh.bounds.max.y);
        mesh.triangles = new[] { 0, 1, 3 };
        Assert.Equal(new[] { 0, 1, 2 }, mesh.triangles); Assert.Contains(log, l => l.StartsWith("Failed setting triangles."));
    }

    [Fact] public void ColliderBoundsUseTheShapePositionAndScaleAndAreEmptyWhenOff()
    {
        var go = new GameObject("box") { Position = new Vector3(10, 0, 0) };
        go.transform.localScale = new Vector3(2, 1, 1);
        var box = go.AddComponent<BoxCollider>(); box.size = new Vector3(1, 2, 3);
        Assert.Equal(new Vector3(2, 2, 3).x, box.bounds.size.x); Assert.True(box.bounds.Contains(new Vector3(10.9f, 0.9f, 1.4f)));
        Assert.False(box.bounds.Contains(new Vector3(11.1f, 0, 0)));
        var sphere = go.AddComponent<SphereCollider>(); sphere.radius = 1f;
        Assert.Equal(4f, sphere.bounds.size.y); // radius scaled by the largest axis
        Assert.Equal(12f, sphere.ClosestPoint(new Vector3(20, 0, 0)).x);
        box.enabled = false; Assert.Equal(default, box.bounds);
        var capsule = go.AddComponent<CapsuleCollider>(); Assert.Equal(2f, capsule.bounds.size.y); Assert.Equal(2f, capsule.bounds.size.x);
        var body = go.AddComponent<Rigidbody>(); body.linearVelocity = new Vector3(0, 4, 0);
        Assert.Same(body, box.attachedRigidbody); Assert.Equal(4f, body.linearVelocity.y); Assert.Equal(10f, body.position.x);
    }

    [Fact] public void AnimatorParametersAreOneParameterByNameOrHash()
    {
        var animator = new GameObject("a").AddComponent<Animator>();
        int hash = Animator.StringToHash("running");
        animator.SetBool("running", true); Assert.True(animator.GetBool(hash));
        animator.SetFloat(Animator.StringToHash("speed"), 1.5f); Assert.Equal(1.5f, animator.GetFloat("speed"));
        animator.SetInteger("stance", 2); Assert.Equal(2, animator.GetInteger("stance"));
        animator.SetTrigger("attack"); Assert.True(animator.GetBool("attack")); animator.ResetTrigger("attack"); Assert.False(animator.GetBool("attack"));
        Assert.Equal(unchecked((int)0xCBF43926), Animator.StringToHash("123456789")); // the CRC-32 check value
        Assert.Equal(AnimatorUpdateMode.Fixed, (AnimatorUpdateMode)1);
        var light = new GameObject("l").AddComponent<Light>(); Assert.Equal(LightType.Point, light.type); Assert.Equal(10f, light.range);
        light.enabled = false; Assert.False(light.isActiveAndEnabled);
    }

    [Fact] public void ACanvasKnowsItsRootAndCountsForcedUpdates()
    {
        var root = new GameObject("root").AddComponent<Canvas>(); var nested = new GameObject("nested").AddComponent<Canvas>();
        nested.transform.SetParent(root.transform);
        Assert.True(root.isRootCanvas); Assert.False(nested.isRootCanvas); Assert.Same(root, nested.rootCanvas);
        int before = Canvas.ForceUpdateCount; Canvas.ForceUpdateCanvases(); Assert.Equal(before + 1, Canvas.ForceUpdateCount);
    }

    [Fact] public void RandomUsesUnitysRangesAndIsRepeatableBySeed()
    {
        UnityEngine.Random.InitState(7); var first = Enumerable.Range(0, 20).Select(_ => UnityEngine.Random.Range(0, 3)).ToArray();
        UnityEngine.Random.InitState(7); var again = Enumerable.Range(0, 20).Select(_ => UnityEngine.Random.Range(0, 3)).ToArray();
        Assert.Equal(first, again); Assert.All(first, v => Assert.InRange(v, 0, 2)); // the int maximum is excluded
        Assert.Equal(5, UnityEngine.Random.Range(5, 5));
        Assert.InRange(UnityEngine.Random.Range(1f, 2f), 1f, 2f);
    }

    [Fact] public void OnModernDotNetTheRuntimesModuleInitializerAttributeIsUsed() =>
        // The net48 polyfill must not shadow the runtime's own type where it exists.
        Assert.Equal(typeof(object).Assembly, typeof(System.Runtime.CompilerServices.ModuleInitializerAttribute).Assembly);
}
