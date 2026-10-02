using System;
using System.Collections.Generic;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;
using Object = UnityEngine.Object;

// A registry another object depends on, as a mod's manager is: it registers itself in its own Awake.
public sealed class OrderRegistry : MonoBehaviour
{
    public static OrderRegistry? Instance;
    public readonly List<string> Names = new();
    private void Awake() { Instance = this; Names.Add("default"); }
}

// Reads the registry in its own Awake: right only when the registry happens to wake first, which Unity does not promise.
public sealed class AwakeReader : MonoBehaviour
{
    public int Seen = -1;
    private void Awake() => Seen = OrderRegistry.Instance?.Names.Count ?? -1;
}

// The fix: reads the registry in Start, which Unity calls only after every object that woke with it has woken.
public sealed class StartReader : MonoBehaviour
{
    public int Seen = -1;
    private void Start() => Seen = OrderRegistry.Instance?.Names.Count ?? -1;
}

// Logs its object's name on waking: AddComponent wakes it before a test could set a label.
public sealed class NamedWaker : MonoBehaviour
{
    private void Awake() => Recorder.Log.Add(gameObject.name + ":Awake");
}

// Deactivates a chosen object from its Awake, as a mod that hides itself until it is configured does.
public sealed class DeactivateOnAwake : MonoBehaviour
{
    public static GameObject? Target;
    private void Awake() { Recorder.Log.Add(gameObject.name + ":Awake"); Target?.SetActive(false); }
}

// #152: the doubles call behaviours in insertion order where Unity promises none. UnityOrder.Reversed calls them the
// other way round, so a test run in both orders finds a dependency on which of two objects goes first.
public sealed class UnityOrderTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new ValheimWorldScope().WithScene();
    public UnityOrderTests() { Recorder.Log.Clear(); OrderRegistry.Instance = null; }
    public void Dispose() { OrderRegistry.Instance = null; _scope.Dispose(); }

    // The registry and its reader wake together, as two objects of a loaded scene or an instantiated prefab do.
    private static GameObject Scene<TReader>(out TReader reader) where TReader : MonoBehaviour
    {
        var root = new GameObject("root"); root.SetActive(false);
        var registry = new GameObject("registry"); registry.transform.SetParent(root.transform);
        registry.AddComponent<OrderRegistry>();
        var client = new GameObject("client"); client.transform.SetParent(root.transform);
        reader = client.AddComponent<TReader>();
        return root;
    }

    [Fact] public void AnAwakeThatReadsAnotherObjectsAwakeWorksInOneOrderOnly()
    {
        _scope.WithUnityOrder(UnityOrder.Insertion);
        var root = Scene<AwakeReader>(out var first);
        root.SetActive(true);
        Assert.Equal(1, first.Seen); // passes by luck: the registry was made first

        OrderRegistry.Instance = null;
        _scope.WithUnityOrder(UnityOrder.Reversed);
        var other = Scene<AwakeReader>(out var second);
        other.SetActive(true);
        Assert.Equal(-1, second.Seen); // the same code, the other valid order: no registry yet
    }

    [Theory]
    [InlineData(UnityOrder.Insertion)]
    [InlineData(UnityOrder.Reversed)]
    public void AStartThatReadsAnotherObjectsAwakeWorksInEitherOrder(UnityOrder order)
    {
        _scope.WithUnityOrder(order);
        var root = Scene<StartReader>(out var reader);
        root.SetActive(true);
        Object.RunFrame();
        Assert.Equal(1, reader.Seen);
    }

    // Each behaviour still gets Awake then OnEnable before the next; only the order across them is reversed, children included.
    [Fact] public void ReversedActivationIsTheExactReverseAcrossObjectsAndKeepsEachBehavioursSequence()
    {
        string[] Activate(UnityOrder order)
        {
            _scope.WithUnityOrder(order);
            var parent = new GameObject("p"); parent.SetActive(false);
            parent.AddComponent<Recorder>().Label = "a"; parent.AddComponent<Recorder>().Label = "b";
            foreach (var name in new[] { "c1", "c2" })
            {
                var child = new GameObject(name); child.transform.SetParent(parent.transform);
                child.AddComponent<Recorder>().Label = name;
            }
            Recorder.Log.Clear();
            parent.SetActive(true);
            return Recorder.Log.ToArray();
        }
        Assert.Equal(new[] { "a:Awake", "a:OnEnable", "b:Awake", "b:OnEnable", "c1:Awake", "c1:OnEnable", "c2:Awake", "c2:OnEnable" }, Activate(UnityOrder.Insertion));
        Assert.Equal(new[] { "c2:Awake", "c2:OnEnable", "c1:Awake", "c1:OnEnable", "b:Awake", "b:OnEnable", "a:Awake", "a:OnEnable" }, Activate(UnityOrder.Reversed));
    }

    [Fact] public void ReversedFramesReverseEachPhaseButFinishAPhaseBeforeTheNext()
    {
        _scope.WithUnityOrder(UnityOrder.Reversed);
        new GameObject("x").AddComponent<Recorder>().Label = "x";
        new GameObject("y").AddComponent<Recorder>().Label = "y";
        Recorder.Log.Clear();
        Object.RunFrame();
        Assert.Equal(new[] { "y:Start", "x:Start", "y:Update", "x:Update", "y:LateUpdate", "x:LateUpdate" }, Recorder.Log);
    }

    // AddComponent on an active object wakes the behaviour inside the call, in Unity too: the test's own code decides
    // that order, and the switch does not change it.
    [Fact] public void AddComponentOnAnActiveObjectStillWakesInTheCallersOrder()
    {
        _scope.WithUnityOrder(UnityOrder.Reversed);
        new GameObject("x").AddComponent<NamedWaker>();
        new GameObject("y").AddComponent<NamedWaker>();
        Assert.Equal(new[] { "x:Awake", "y:Awake" }, Recorder.Log);
    }

    [Fact] public void ReversedDestroysTheQueueAndAHierarchyLastFirst()
    {
        _scope.WithUnityOrder(UnityOrder.Reversed);
        var parent = new GameObject("parent"); parent.AddComponent<Recorder>().Label = "p";
        var child = new GameObject("child"); child.transform.SetParent(parent.transform); child.AddComponent<Recorder>().Label = "c";
        var other = new GameObject("other"); other.AddComponent<Recorder>().Label = "o";
        Recorder.Log.Clear();
        Object.Destroy(parent); Object.Destroy(other);
        Assert.Equal(2, Object.EndOfFrame());
        Assert.Equal(new[] { "o:OnDisable", "o:OnDestroy of other", "c:OnDisable", "p:OnDisable", "c:OnDestroy of child", "p:OnDestroy of parent" }, Recorder.Log);
        Assert.True(parent == null); Assert.True(child == null);
    }

    [Fact] public void ReversedReversesUnsortedFindsOnly()
    {
        var first = new GameObject("first").AddComponent<MarkedBehaviour>();
        var second = new GameObject("second").AddComponent<MarkedBehaviour>();
        Assert.Same(first, Object.FindAnyObjectByType<MarkedBehaviour>());
        _scope.WithUnityOrder(UnityOrder.Reversed);
        Assert.Equal(new[] { second, first }, Object.FindObjectsByType<MarkedBehaviour>(FindObjectsSortMode.None));
        Assert.Same(second, Object.FindAnyObjectByType<MarkedBehaviour>());
        Assert.Equal(new[] { first, second }, Object.FindObjectsByType<MarkedBehaviour>(FindObjectsSortMode.InstanceID));
        Assert.Same(first, Object.FindFirstObjectByType<MarkedBehaviour>());
    }

    // A prefab instantiated or reparented under an active object wakes through the same order switch.
    [Theory]
    [InlineData(UnityOrder.Insertion, new[] { "a:Awake", "b:Awake" })]
    [InlineData(UnityOrder.Reversed, new[] { "b:Awake", "a:Awake" })]
    public void InstantiateAndReparentingWakeInTheChosenOrder(UnityOrder order, string[] expected)
    {
        _scope.WithUnityOrder(order);
        var shelf = new GameObject("shelf"); shelf.SetActive(false);
        var prefab = new GameObject("prefab"); prefab.transform.SetParent(shelf.transform);
        foreach (var name in new[] { "a", "b" }) { var part = new GameObject(name); part.transform.SetParent(prefab.transform); part.AddComponent<NamedWaker>(); }
        Assert.Empty(Recorder.Log);
        Object.Instantiate(prefab);
        Assert.Equal(expected, Recorder.Log);
        Recorder.Log.Clear();
        prefab.transform.SetParent(null);
        Assert.Equal(expected, Recorder.Log);
    }

    // A behaviour that deactivates its hierarchy while it wakes stops the rest of that activation, in either order.
    [Theory]
    [InlineData(UnityOrder.Insertion)]
    [InlineData(UnityOrder.Reversed)]
    public void NothingWakesOnAnObjectAnEarlierAwakeDeactivated(UnityOrder order)
    {
        _scope.WithUnityOrder(order);
        var parent = new GameObject("parent"); parent.SetActive(false);
        var first = new GameObject("first"); first.transform.SetParent(parent.transform); first.AddComponent<DeactivateOnAwake>();
        var second = new GameObject("second"); second.transform.SetParent(parent.transform); var other = second.AddComponent<DeactivateOnAwake>();
        DeactivateOnAwake.Target = parent;
        try
        {
            parent.SetActive(true);
            Assert.False(parent.activeInHierarchy);
            Assert.Single(Recorder.Log); // only the behaviour that ran first woke
            DeactivateOnAwake.Target = null;
            parent.SetActive(true);
            Assert.Equal(2, Recorder.Log.Count); // the other wakes when the object is next active
            Assert.True(other.isActiveAndEnabled);
        }
        finally { DeactivateOnAwake.Target = null; }
    }

    [Fact] public void TheEndOfFrameCountDoesNotDependOnTheOrder()
    {
        int Count(UnityOrder order)
        {
            _scope.WithUnityOrder(order);
            var go = new GameObject("go"); var part = go.AddComponent<Recorder>();
            Object.Destroy(part); Object.Destroy(go);
            return Object.EndOfFrame();
        }
        Assert.Equal(2, Count(UnityOrder.Insertion));
        Assert.Equal(2, Count(UnityOrder.Reversed));
    }

    [Fact] public void TheScopePutsTheOrderBackAndRefusesAnUnknownOne()
    {
        using (new ValheimWorldScope().WithUnityOrder(UnityOrder.Reversed)) { }
        new GameObject("x").AddComponent<Recorder>().Label = "x";
        new GameObject("y").AddComponent<Recorder>().Label = "y";
        Recorder.Log.Clear();
        Object.RunFrame();
        Assert.Equal("x:Start", Recorder.Log[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => _scope.WithUnityOrder((UnityOrder)2));
    }
}
