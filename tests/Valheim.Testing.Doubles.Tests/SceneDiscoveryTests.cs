using System;
using System.Linq;
using ExampleMod;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;
using Object = UnityEngine.Object;

// FindObjectsByType<GameObject> and GetComponent(Type) (#154), as Unity 6's: scene objects only, active ones unless
// inactive ones are asked for, never destroyed objects or assets. Each test holds an object that must not be found, so
// none can pass on an empty search.
public sealed class SceneDiscoveryTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new ValheimWorldScope().WithScene().WithZdos();
    public void Dispose() { Object.EndOfFrame(); _scope.Dispose(); }

    private static GameObject[] Find(FindObjectsInactive inactive = FindObjectsInactive.Exclude) =>
        Object.FindObjectsByType<GameObject>(inactive, FindObjectsSortMode.InstanceID);

    [Fact] public void SceneGameObjectsAreFoundWithOrWithoutComponentsAndInactiveOnesOnRequest()
    {
        var root = new GameObject("root"); // no components but its transform
        var child = new GameObject("child"); child.transform.SetParent(root.transform); child.SetActive(false);
        var grandchild = new GameObject("grandchild"); grandchild.transform.SetParent(child.transform); grandchild.AddComponent<BoxCollider>();
        var gone = new GameObject("gone"); var goneChild = new GameObject("gone child"); goneChild.transform.SetParent(gone.transform);
        Object.Destroy(gone);
        Assert.Contains(gone, Find()); // Destroy waits for the end of the frame, as Unity's does
        Object.EndOfFrame();

        Assert.Equal(new[] { root }, Find());
        Assert.Equal(new[] { root, child, grandchild }, Find(FindObjectsInactive.Include)); // the inactive child and its active child
        Assert.Empty(Object.FindObjectsByType<BoxCollider>(FindObjectsSortMode.None)); // its component, on an inactive object
        Assert.Equal(new[] { root }, Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None));
        Assert.Same(root, Object.FindFirstObjectByType<GameObject>());
    }

    [Fact] public void AssetsAreNotInTheSceneButTheirCopiesAre()
    {
        var prefab = ZNetScene.instance!.AddPrefab("piece_chest");
        Assert.True(prefab.IsAsset);
        var lid = new GameObject("lid"); lid.transform.SetParent(prefab.transform); lid.AddComponent<BoxCollider>();
        var marker = prefab.AddComponent<MarkedBehaviour>();
        Assert.Empty(Find(FindObjectsInactive.Include)); // the prefab, its child and their components
        Assert.Empty(Object.FindObjectsByType<MarkedBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None));

        var copy = Object.Instantiate(prefab, new Vector3(1, 2, 3), Quaternion.identity);
        Assert.False(copy.IsAsset);
        var copiedLid = copy.transform.GetChild(0).gameObject;
        Assert.Equal(new[] { copy, copiedLid }, Find());
        var found = Assert.Single(Object.FindObjectsByType<MarkedBehaviour>(FindObjectsSortMode.None));
        Assert.NotSame(marker, found); Assert.Same(copy, found.gameObject);
    }

    [Fact] public void EachSceneHasItsOwnObjectsAndTheOuterOnesComeBack()
    {
        var outer = new GameObject("outer");
        GameObject inner;
        using (new ValheimWorldScope().WithScene())
        {
            Assert.Empty(Find(FindObjectsInactive.Include));
            inner = new GameObject("inner");
            Assert.Equal(new[] { inner }, Find());
        }
        Assert.Equal(new[] { outer }, Find());
        using (new ValheimWorldScope()) // a scope without WithScene shares the scene
        {
            var shared = new GameObject("shared");
            Assert.Equal(new[] { outer, shared }, Find());
        }
        Assert.DoesNotContain(inner, Find(FindObjectsInactive.Include));
    }

    [Fact] public void GetComponentByTypeMatchesTheGenericMethod()
    {
        var go = new GameObject("lookup");
        var sphere = go.AddComponent<SphereCollider>(); var marked = go.AddComponent<MarkedBehaviour>();
        foreach (var owner in new Func<Type, Component>[] { go.GetComponent, sphere.GetComponent })
        {
            Assert.Same(go.GetComponent<SphereCollider>(), owner(typeof(SphereCollider)));
            Assert.Same(go.GetComponent<Collider>(), owner(typeof(Collider))); // a base type
            Assert.Same(marked, owner(typeof(IMarked))); // an interface
            Assert.Same(go.transform, owner(typeof(Transform)));
            Assert.Null(owner(typeof(Rigidbody)));
            Assert.Throws<ArgumentNullException>(() => owner(null!));
            Assert.Contains("derives from MonoBehaviour or Component or is an interface", Assert.Throws<ArgumentException>(() => owner(typeof(string))).Message);
        }
        Object.DestroyImmediate(sphere);
        Assert.Null(go.GetComponent(typeof(SphereCollider))); // a destroyed component is skipped, as by GetComponent<T>
        Assert.Throws<NullReferenceException>(() => sphere.GetComponent(typeof(MarkedBehaviour)));
        Object.DestroyImmediate(go);
        Assert.Throws<NullReferenceException>(() => go.GetComponent(typeof(MarkedBehaviour)));
    }

    // The consumer's deletion path, compiled from its source (SceneSweep.cs): it must select only live copies that carry
    // the configured component, delete them through ZNetScene, and find nothing left afterwards.
    [Fact] public void AModsSceneSweepSelectsAndDeletesOnlyMatchingLiveCopies()
    {
        var prefab = ZNetScene.instance!.AddPrefab("beacon");
        prefab.AddComponent<Beacon>();
        var near = Object.Instantiate(prefab, Vector3.zero, Quaternion.identity);
        var far = Object.Instantiate(prefab, new Vector3(100, 0, 0), Quaternion.identity);
        var stripped = Object.Instantiate(prefab, new Vector3(5, 0, 0), Quaternion.identity);
        Object.DestroyImmediate(stripped.GetComponent<Beacon>());
        var other = new GameObject("beacon_decoy"); other.AddComponent<Beacon>();

        Assert.Equal(new[] { near, far }.OrderBy(o => o.GetInstanceID()), SceneSweep.Select("beacon", typeof(Beacon)).OrderBy(o => o.GetInstanceID()));
        Assert.Equal(3, SceneSweep.Select("beacon", typeof(ZNetView)).Count); // every live copy is networked
        Assert.Equal(2, SceneSweep.Delete("beacon", typeof(Beacon)));
        Object.EndOfFrame();
        Assert.Empty(SceneSweep.Select("beacon", typeof(Beacon)));
        Assert.Equal(new[] { stripped, other }, Find());
        Assert.False(prefab.Destroyed); // the asset itself was never selected
    }
}
