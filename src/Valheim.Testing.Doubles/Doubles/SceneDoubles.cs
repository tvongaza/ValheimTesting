// Valheim.Testing.Doubles: compile-time stand-ins for the Unity, Valheim, BepInEx and Jotunn types a mod's
// pure-logic sources use, so those sources compile and run in an ordinary test project without the game.
// Source package: these files are compiled into the consuming test project. Every type is partial; add the
// members your mod needs in your own files. Behaviour mirrors the game where mod code depends on it.
#nullable enable
// ReSharper disable InconsistentNaming
// Scene doubles: prefabs, the live networked objects, and object destruction. An instantiated networked prefab
// creates its ZDO; ZDO destruction is QUEUED until the test calls ZDOMan.ProcessDestroyed, as the game's is, and
// object destruction until UnityEngine.Object.EndOfFrame, as Unity's is.
using System.Collections.Generic;
using System.Linq;
using Valheim.Testing.Doubles;

/// <summary>Shim for WearNTear: the prefab's full health.</summary>
public partial class WearNTear : UnityEngine.MonoBehaviour { public float m_health; }

/// <summary>Shim for ZNetScene: registered prefabs and the live networked objects. The registry by name hash is in RegistryDoubles.cs.</summary>
public partial class ZNetScene
{
    public static ZNetScene? instance;
    /// <summary>The prefabs <see cref="AddPrefab"/> made, by name.</summary>
    [TestOnly] public readonly Dictionary<string, UnityEngine.GameObject> Prefabs = new();
    [TestOnly] public readonly List<UnityEngine.GameObject> Live = new();
    /// <summary>A networked prefab with a WearNTear, registered at once (in <c>m_prefabs</c> and by name hash); a second one with the same name replaces the first. It is an asset (<see cref="UnityEngine.GameObject.IsAsset"/>), as the game's prefabs are, so only its copies are in the scene.</summary>
    [TestOnly] public UnityEngine.GameObject AddPrefab(string name, float health = 1000f)
    {
        var prefab = new UnityEngine.GameObject(name) { Networked = true, Health = health, IsAsset = true };
        if (Prefabs.TryGetValue(name, out var replaced)) m_prefabs.Remove(replaced);
        Prefabs[name] = prefab;
        m_prefabs.Add(prefab);
        m_namedPrefabs[name.GetStableHashCode()] = prefab;
        return prefab;
    }
    /// <summary>The live instance of the ZDO, as the game's: one destroyed with a plain Object.Destroy is still found (it equals null).</summary>
    public ZNetView? FindInstance(ZDO zdo) => Live.FirstOrDefault(o => o.View?.GetZDO() == zdo)?.View;
    /// <summary>
    /// As the game's: the view lets go of its ZDO (GetZDO() is null afterwards), the object leaves the live scene at
    /// once, its ZDO is queued for destruction when this session owns it, and the object itself is destroyed at the end
    /// of the frame (UnityEngine.Object.EndOfFrame).
    /// </summary>
    public void Destroy(UnityEngine.GameObject gameObject)
    {
        var view = gameObject.GetComponent<ZNetView>();
        if (view != null && view.GetZDO() is { } zdo)
        {
            view.ResetZDO();
            Live.Remove(gameObject);
            if (zdo.IsOwner()) ZDOMan.instance!.DestroyZDO(zdo);
        }
        UnityEngine.Object.Destroy(gameObject);
    }
}
