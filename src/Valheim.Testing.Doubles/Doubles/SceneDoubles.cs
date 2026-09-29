// Valheim.Testing.Doubles: compile-time stand-ins for the Unity, Valheim, BepInEx and Jotunn types a mod's
// pure-logic sources use, so those sources compile and run in an ordinary test project without the game.
// Source package: these files are compiled into the consuming test project. Every type is partial; add the
// members your mod needs in your own files. Behaviour mirrors the game where mod code depends on it.
#nullable enable
// ReSharper disable InconsistentNaming
// Scene doubles: prefabs, the live networked objects, and object destruction. An instantiated networked prefab
// creates its ZDO; ZDO destruction is QUEUED until the test calls ZDOMan.ProcessDestroyed, as the game's is.
using System.Collections.Generic;
using System.Linq;

/// <summary>Shim for WearNTear: the prefab's full health.</summary>
public partial class WearNTear { public float m_health; }

/// <summary>Shim for ZNetScene: registered prefabs and the live networked objects.</summary>
public partial class ZNetScene
{
    public static ZNetScene? instance;
    public readonly Dictionary<string, UnityEngine.GameObject> Prefabs = new();
    public readonly List<UnityEngine.GameObject> Live = new();
    public UnityEngine.GameObject AddPrefab(string name, float health = 1000f)
    {
        var prefab = new UnityEngine.GameObject(name) { Networked = true, Health = health };
        Prefabs[name] = prefab;
        return prefab;
    }
    public UnityEngine.GameObject? GetPrefab(string name) => Prefabs.TryGetValue(name, out var prefab) ? prefab : null;
    public ZNetView? FindInstance(ZDO zdo) => Live.FirstOrDefault(o => !o.Destroyed && o.View?.GetZDO() == zdo)?.View;
    /// <summary>As the game's: the object goes, and its ZDO is queued for destruction when this session owns it.</summary>
    public void Destroy(UnityEngine.GameObject gameObject)
    {
        UnityEngine.Object.Destroy(gameObject);
        if (gameObject.View?.GetZDO() is { } zdo && zdo.IsOwner()) ZDOMan.instance!.DestroyZDO(zdo);
    }
}
