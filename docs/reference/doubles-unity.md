# Unity, config and Harmony in the doubles

How `Valheim.Testing.Doubles` models Unity objects, their order, BepInEx config and Harmony. Setup and `ValheimWorldScope` are on the [core page](../packages/Valheim.Testing.Doubles.md).

## Purpose

The doubles give your mod's Unity, BepInEx and Harmony calls real behaviour where mod logic depends on it. Everything else is a plain field or a no-op. World state is on [World state page](doubles-world.md); serialization, RPCs and the handshake on [Networking page](doubles-network.md). Members marked `[TestOnly]` in the [member index](../packages/Valheim.Testing.Doubles.members.txt) exist only in tests.

## Unity names

| Area | The doubles do | Fails or differs when |
|---|---|---|
| Current names | Unity 6 names: `FindObjectsByType`, `Rigidbody.linearVelocity`, `LightType.Rectangle`, `AnimatorUpdateMode.Fixed` | — |
| Obsolete forwarders | `FindObjectsOfType`, `FindObjectOfType`, `Rigidbody.velocity`/`drag`/`angularDrag`, `LightType.Area` compile with a warning | `AnimatorUpdateMode.AnimatePhysics` does not exist |

## Game objects and components

| Area | The doubles do | Fails or differs when |
|---|---|---|
| Component queries | `AddComponent`, `GetComponent(s)`, `GetComponent(s)InChildren`, `GetComponentInParent`, `TryGetComponent`, by base type or interface | `GetComponent(Type)` refuses a type that is neither component nor interface |
| Inactive objects | `SetActive` and `activeInHierarchy` work as in Unity | Without `includeInactive`, only objects active in their hierarchy count |
| Transform hierarchy | `SetParent` (keeps world position), `parent`, `GetChild`, `Find("a/b")`, `root`, `localPosition`, `localScale` | Rotation is not composed: under a rotated parent, world position and `TransformPoint` are wrong |
| Rotation | `rotation` is stored (identity by default); `localRotation` derived; `eulerAngles` as Unity's Euler angles | Collider bounds ignore the object's own rotation |
| `Quaternion`, `Color` | `*`, `eulerAngles` in [0, 360), `Inverse`; `Color` `==` with tolerance, `Equals` exact | — |
| `Instantiate` | Copies components, children, public and `[SerializeField]` fields, lists, arrays, `[Serializable]` classes | Dictionaries, delegates and private state get the constructor's values |
| Copy names | A copy keeps the prefab's name; `Utils.GetPrefabName` handles both forms | Code matching `name + "(Clone)"` finds none |
| Scene search | `FindObjectsByType`, `FindFirstObjectByType`, `FindAnyObjectByType` find every GameObject and component; `FindObjectsInactive.Include` adds inactive ones | Assets (`ZNetScene.AddPrefab` prefabs, `GameObject.IsAsset`), their children and components are not found; copies are |
| Component behaviour | Colliders (`Box`, `Sphere`, `Capsule`, `Mesh`) with `bounds`, `ClosestPoint`, `attachedRigidbody`; `Renderer.material` and `MeshFilter.mesh` copy on first use; `Animator` parameters; `Canvas.rootCanvas`; `Light` defaults | Collider `bounds` are empty while off; `Mesh` refuses out-of-range triangle indices |
| Component fields | Unity's own components keep settings in properties, as in native code | Reflection over public fields finds none; `Instantiate` still copies them |
| `UnityEngine.Random` | Unity's ranges, seeded with `Random.InitState` | The sequence is not Unity's |
| `UnityEngine.Debug` | Writes to the log capture | — |

## Messages and frames

| Area | The doubles do | Fails or differs when |
|---|---|---|
| `Awake` | Runs when a component joins an object active in its hierarchy, or the object becomes active | A prefab under an inactive parent wakes only in its copies |
| Sequence | `Awake`, `OnEnable`, `Start` before the first `Update`; `Update` and `LateUpdate` once per frame; `OnDisable`; `OnDestroy` while alive | — |
| `UnityEngine.Object.RunFrame(deltaTime)` | Advances `Time.time` and `Time.frameCount`, resumes coroutines, runs due `Invoke`/`InvokeRepeating`, ends the frame | — |
| Coroutines | `yield return null`, `WaitForSeconds`, `WaitForEndOfFrame`, nested routines, `WaitUntil`/`WaitWhile` | — |
| Exceptions | An exception thrown by a message reaches the test | Unity logs it and carries on |

## Order across objects

Unity promises: `Awake` then `OnEnable` per behaviour, `Awake` inside `AddComponent` on an active object, every woken object before any `Start`, and each frame phase (`Start`, `Update`, coroutines and `Invoke`, `LateUpdate`) for every behaviour before the next. The doubles keep those. Where Unity promises nothing, the doubles use insertion order.

| Area | The doubles do | Fails or differs when |
|---|---|---|
| Unordered cases | Insertion order for `Awake` across a hierarchy, frame phases across [equal execution order](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/DefaultExecutionOrder.html), `OnDestroy` in one frame, `FindObjectsSortMode.None` | A test can pass only because two objects were made in a lucky order |
| `WithUnityOrder(UnityOrder.Reversed)` | Runs exactly those orders backwards; the scope restores the order | Two orders cover two objects, not every order of three or more |
| Script execution order | Not modelled: `DefaultExecutionOrder`, project settings, scene loading | A result that depends on them needs a native check ([`Valheim.Testing.Game`](../packages/Valheim.Testing.Game.md)) |

Run a case with two objects once in each order:

```csharp
[Theory] [InlineData(UnityOrder.Insertion)] [InlineData(UnityOrder.Reversed)]
public void ...(UnityOrder order) { scope.WithUnityOrder(order); ... }
```

[UnityOrderTests](../../tests/Valheim.Testing.Doubles.Tests/UnityOrderTests.cs) has the discriminating case: a registry read in `Awake` fails reversed; the same read in `Start` passes in both.

## Destroyed objects

| Area | The doubles do | Fails or differs when |
|---|---|---|
| Null comparison | Unity's `==`, `!=`, `Equals` and `bool`: a destroyed object equals null, `if (obj)` is false | `is null`, `?.` and `??` skip the overload and see a live reference, as in Unity |
| Engine members | `name`, `gameObject`, `GetComponent` on a destroyed object throw `NullReferenceException` naming it | `UnityEngine.MissingReferenceException` exists only so code compiles |
| Own members | A component's fields and methods (`WearNTear.m_health`, `ZNetView.GetZDO()`) still answer | Destroyed objects stay distinct dictionary keys and list entries |
| `Object.Destroy` | Deferred until `UnityEngine.Object.EndOfFrame()`, which returns how many objects went | `DestroyImmediate` destroys at once |
| What goes | A `GameObject` takes its `ZNetView` and `WearNTear`; a component alone makes `GetComponent` return null | A plain `Destroy` leaves the view in `ZNetScene.Live`; `FindInstance` still returns it ([ZNetScene.RemoveObjects](https://github.com/Valheim-Modding/Wiki/wiki/Client-Troubleshooting#znetsceneremoveobjects)) |
| Pending destroys | Process-wide; `ValheimWorldScope` ends the frame on dispose | Without a scope, call `EndOfFrame()` in the test or its `Dispose` |

This catches the `?.` mistake the [modding wiki](https://github.com/Valheim-Modding/Wiki/wiki/Best-Practices#do-not-use-null-operators-on-gameobject) warns about. Say `PieceHealth.Total(pieces)` adds up `piece?.GetComponent<WearNTear>()?.m_health ?? 0f`. This test fails with `NullReferenceException` until the mod checks `if (piece == null) continue;` before `GetComponent`:

```csharp
[Fact] public void DestroyedPiecesAreSkipped()
{
    using var world = new ValheimWorldScope().WithZdos().WithScene();
    var prefab = ZNetScene.instance!.AddPrefab("stone_wall", health: 1500f);
    var kept = UnityEngine.Object.Instantiate(prefab, new UnityEngine.Vector3(1, 30, 1), UnityEngine.Quaternion.Euler(0, 0, 0));
    var removed = UnityEngine.Object.Instantiate(prefab, new UnityEngine.Vector3(2, 30, 2), UnityEngine.Quaternion.Euler(0, 0, 0));
    UnityEngine.Object.Destroy(removed);
    UnityEngine.Object.EndOfFrame();
    Assert.Equal(1500f, PieceHealth.Total(new[] { kept, removed }));
}
```

## Localization and preferences

| Area | The doubles do | Fails or differs when |
|---|---|---|
| `Localization` | Translates `$word`, `$KEY_<binding>` (from `ZInput.SetBinding`) and `$1`; keeps the game's 100-entry cache | `AddWord` does not clear the cache; `SetLanguage` drops added words |
| `PlatformPrefs` | In memory; falls back to PlayerPrefs on a dedicated server, so `Localization.instance` works under `AsDedicatedServer()` | Set `PlatformPrefs.Unavailable` to a reason: every call, and a first `Localization.instance`, throws until cleared |

## Config

| Area | The doubles do | Fails or differs when |
|---|---|---|
| `ConfigFile` | BepInEx 5's `ConfigFile`: every `Bind` overload, `ConfigEntry<T>.Value`, `BoxedValue`, `SettingChanged`, `ConfigReloaded`, `Reload`, `Save`, `SaveOnConfigSet`, `TryGetEntry`, acceptable ranges and lists | `Vector2`, `Vector3`, `Vector4`, `Quaternion` and `Rect` throw on `Bind`; add one with `TomlTypeConverter.AddConverter` |
| Value text | Escaped strings, invariant floats, enums by name, colours as RRGGBBAA | — |
| Files | An in-memory disk; `FileText` or `ConfigFile.WriteFile` edits a file, `Save` writes BepInEx's format; `WithConfigFiles()` gives an empty disk | `Paths.ConfigPath` is not a real directory: a `FileSystemWatcher` cannot run; call `Reload()` |
| `SettingChanged` | Fires once per real change; values in the file for unbound settings apply when bound | Binding with a non-default default raises the file's `SettingChanged`, as in BepInEx |
| Config sync | Not modelled. Set the entry values a client would receive and test the mod's decisions | Test the sync natively: read the entry on server and client after a join and an admin change, re-reading with a bound as `LogWait` does |

Never wait on a single `FileSystemWatcher` event: it fires several times for one save and can miss events on Linux and mounted or container filesystems.

## Harmony

| Area | The doubles do | Fails or differs when |
|---|---|---|
| Attributes | Every `HarmonyPatch` constructor (argument types, `ArgumentType` variations, `MethodType`), `HarmonyPriority`, `HarmonyBefore`, `HarmonyAfter` record their target in `info`; `HarmonyMethod.Merge` combines them | Follows HarmonyX, the Harmony BepInEx 5 ships |
| `new Harmony(id).PatchAll()` | Lists the patch classes it finds in `Patches` | Patches nothing: a test passes even when the target does not exist. Call the prefix or postfix yourself |
| `Traverse`, `AccessTools` | Reach members of any visibility on real objects | A typed `Traverse` read casts and throws on another type |
| `[ModuleInitializer]` | A `ModuleInitializerAttribute` polyfill for net48 | Define `VALHEIM_TESTING_NO_POLYFILLS` if your project declares its own |

## Limits

- Nothing is simulated or drawn: components keep their settings and answer queries, with no physics or rendering.
- Transform rotation is not composed; collider bounds ignore rotation.
- Unity's script execution order and the game's scene loading are not modelled.
- Config sync and Harmony targets are not modelled; check both natively with [`Valheim.Testing.Game`](../packages/Valheim.Testing.Game.md).
