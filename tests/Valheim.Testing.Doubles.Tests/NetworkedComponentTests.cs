using System;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;
using Object = UnityEngine.Object;

// ZNetView and WearNTear are ordinary components (issue #306): the GameObject.Networked/Health/View/Wear shorthands read
// and add them, a copy's view takes its ZDO when it wakes, as the game's ZNetView.Awake (1.0.16) does, and no code path
// special-cases them any more. Each test below covers one path the special cases used to take.
public sealed class NetworkedComponentTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new ValheimWorldScope().WithScene().WithZdos();
    public void Dispose() => _scope.Dispose();

    // A component that holds a serialized reference to its object's view, as the game's components keep m_nview.
    public sealed class Holder : MonoBehaviour { public ZNetView? m_view; public WearNTear? m_wear; }

    private static GameObject Template(string name)
    {
        var holder = new GameObject("templates"); holder.SetActive(false);
        var template = new GameObject(name); template.transform.SetParent(holder.transform);
        return template;
    }

    // MWL's fixtures (#93) build prefabs with AddComponent<ZNetView>() and set m_persistent as the game's prefabs do.
    [Fact] public void AFixtureViewAddedWithAddComponentGetsItsZdoWhenItsCopyWakes()
    {
        var template = Template("loot_chest_wood");
        var view = template.AddComponent<ZNetView>(); view.m_persistent = true;
        Assert.False(view.IsValid()); // asleep under the inactive holder: no ZDO yet

        var copy = Object.Instantiate(template, new Vector3(3, 30, 4), Quaternion.Euler(0, 90, 0));
        var zdo = copy.GetComponent<ZNetView>().GetZDO();
        Assert.Equal("loot_chest_wood".GetStableHashCode(), zdo.GetPrefab());
        Assert.True(zdo.Persistent); Assert.True(zdo.IsOwner());
        Assert.Equal(new Vector3(3, 30, 4), zdo.GetPosition());
        Assert.Equal(90f, zdo.GetRotation().eulerAngles.y, 3);
        Assert.Same(copy.View, ZNetScene.instance!.FindInstance(zdo));
        Assert.True(copy.Networked);

        ZNetScene.instance.Destroy(copy);
        Assert.Equal(new[] { zdo }, ZDOMan.instance!.DestroyQueue);
    }

    [Fact] public void AViewIsNotPersistentUnlessItsPrefabSaysSo()
    {
        var template = Template("fx_hit");
        template.AddComponent<ZNetView>();
        var copy = Object.Instantiate(template);
        Assert.False(copy.GetComponent<ZNetView>().GetZDO().Persistent); // the game's default
    }

    // As the game: ZNetView.Awake destroys a view that wakes outside a world.
    [Fact] public void AViewThatWakesWithoutAZdoManDestroysItself()
    {
        ZDOMan.instance = null;
        var go = new GameObject("early");
        var view = go.AddComponent<ZNetView>();
        Assert.False(view.IsValid()); Assert.False(view.Destroyed); // Destroy waits for the end of the frame
        Object.EndOfFrame();
        Assert.True(view == null); Assert.Null(go.GetComponent<ZNetView>()); Assert.False(go.Networked);
    }

    // Was: GameObject.AllComponents yielded the View and Wear fields ahead of the added components.
    [Fact] public void TheShorthandsAreTheObjectsOwnComponentsInTheOrderAdded()
    {
        var prefab = ZNetScene.instance!.AddPrefab("stone_wall", health: 1500f);
        var holder = prefab.AddComponent<Holder>();
        Assert.Equal(new Component[] { prefab.transform, prefab.View!, prefab.Wear!, holder }, prefab.GetComponents<Component>());
        Assert.Same(prefab.View, prefab.GetComponent<ZNetView>()); Assert.Same(prefab.Wear, prefab.GetComponent<WearNTear>());
        Assert.Equal(1500f, prefab.Health);
        prefab.Health = null; prefab.Networked = false;
        Assert.Null(prefab.GetComponent<WearNTear>()); Assert.Null(prefab.GetComponent<ZNetView>());
        Assert.Null(prefab.Health); Assert.False(prefab.Networked);
    }

    // Was: CopyHierarchyInto added the copy's View and Wear to the scene's component list by hand.
    [Fact] public void ACopysViewAndWearAreInTheSceneLikeAnyComponent()
    {
        var prefab = ZNetScene.instance!.AddPrefab("stone_floor", health: 400f);
        Assert.Empty(Object.FindObjectsByType<ZNetView>(FindObjectsSortMode.None)); // the prefab is an asset
        var copy = Object.Instantiate(prefab, Vector3.zero, Quaternion.identity);
        Assert.Equal(new[] { copy.View! }, Object.FindObjectsByType<ZNetView>(FindObjectsSortMode.None));
        Assert.Equal(new[] { copy.Wear! }, Object.FindObjectsByType<WearNTear>(FindObjectsSortMode.None));
        Assert.Equal(400f, copy.Health);
    }

    // Was: MapHierarchy mapped the View and Wear fields by hand, so references to them followed the copy.
    [Fact] public void ReferencesToTheViewAndWearPointAtTheCopysOwn()
    {
        var prefab = ZNetScene.instance!.AddPrefab("piece_chest");
        var holder = prefab.AddComponent<Holder>(); holder.m_view = prefab.View; holder.m_wear = prefab.Wear;
        var copy = Object.Instantiate(prefab, Vector3.zero, Quaternion.identity);
        var copied = copy.GetComponent<Holder>();
        Assert.Same(copy.View, copied.m_view); Assert.Same(copy.Wear, copied.m_wear);
        Assert.NotSame(prefab.View, copied.m_view);
    }

    // Was: GameObject.OnDestroyed destroyed the View and Wear fields; now they go with the object's other components.
    [Fact] public void DestroyingTheObjectDestroysItsViewAndWearAndTheSceneStillFindsTheView()
    {
        var copy = Object.Instantiate(ZNetScene.instance!.AddPrefab("wood_wall"), Vector3.zero, Quaternion.identity);
        var (view, wear, zdo) = (copy.View!, copy.Wear!, copy.View!.GetZDO());
        Object.Destroy(copy); Object.EndOfFrame();
        Assert.True(view == null); Assert.True(wear == null);
        Assert.Same(view, ZNetScene.instance.FindInstance(zdo)); // a plain Destroy leaves it in the live scene, as in the game
        Assert.True(ZNetScene.instance.FindInstance(zdo) == null);
    }

    // ZNetScene.CreateObject sets m_initZDO before it instantiates the object for a loaded ZDO; the view takes it.
    [Fact] public void AViewTakesTheLoadedZdoItIsGivenAndItsScale()
    {
        var template = Template("Greydwarf_Root");
        var view = template.AddComponent<ZNetView>(); view.m_persistent = true; view.m_syncInitialScale = true;
        var loaded = ZDOMan.instance!.CreateNewZDO(new Vector3(5, 30, 5), "Greydwarf_Root".GetStableHashCode());
        loaded.Set(ZDOVars.s_scaleHash, new Vector3(2f, 2f, 2f));
        int zdos = ZDOMan.instance.Zdos.Count;
        ZNetView.m_useInitZDO = true; ZNetView.m_initZDO = loaded;
        var copy = Object.Instantiate(template, loaded.GetPosition(), loaded.GetRotation());
        ZNetView.m_useInitZDO = false;
        Assert.Same(loaded, copy.GetComponent<ZNetView>().GetZDO());
        Assert.Null(ZNetView.m_initZDO);
        Assert.Equal(zdos, ZDOMan.instance.Zdos.Count); // no new ZDO
        Assert.Equal(new Vector3(2f, 2f, 2f), copy.transform.localScale);
        Assert.Contains(copy, ZNetScene.instance!.Live);
    }

    [Fact] public void ANewZdoCarriesTheAuthoredScaleAndThePrefabName()
    {
        var template = Template("TreasureChest_dvergrtown (1)");
        var view = template.AddComponent<ZNetView>(); view.m_syncInitialScale = true;
        template.transform.localScale = new Vector3(1.2f, 1.2f, 1.2f);
        var zdo = Object.Instantiate(template).GetComponent<ZNetView>().GetZDO();
        Assert.Equal("TreasureChest_dvergrtown".GetStableHashCode(), zdo.GetPrefab()); // Utils.GetPrefabName, as the game's
        Assert.Equal(new Vector3(1.2f, 1.2f, 1.2f), zdo.GetVec3(ZDOVars.s_scaleHash, Vector3.zero));
        var plain = Template("rock"); plain.AddComponent<ZNetView>().m_syncInitialScale = true;
        Assert.False(Object.Instantiate(plain).GetComponent<ZNetView>().GetZDO().GetVec3(ZDOVars.s_scaleHash, out _)); // unit scale is not written
    }

    [Fact] public void AViewWakingWhileInitIsDisabledDestroysItself()
    {
        var template = Template("piece");
        template.AddComponent<ZNetView>();
        ZNetView.m_forceDisableInit = true;
        var copy = Object.Instantiate(template);
        ZNetView.m_forceDisableInit = false;
        Assert.False(copy.GetComponent<ZNetView>().IsValid());
        Object.EndOfFrame();
        Assert.Null(copy.GetComponent<ZNetView>());
        Assert.Empty(ZDOMan.instance!.Zdos);
    }

    // A prefab loaded from the game's bundles never gets Awake; only its copies do.
    [Fact] public void AnAssetsBehavioursNeverWake()
    {
        var prefab = ZNetScene.instance!.AddPrefab("Beehive");
        Assert.False(prefab.View!.IsValid());
        Assert.Empty(ZDOMan.instance!.Zdos);
        Assert.Empty(ZNetScene.instance.Live);
    }
}
