# Content and Harmony census

Reconcile what each game process registered (items, recipes, prefabs, pieces, status effects) with what your mod declares, and check that the mod's Harmony patches are applied in a running game.

## Registered content census

A registration double shows that a mod calls `ObjectDB` or `ZNetScene`. Only the running game shows what each process ended up with: an item missing on a client whose registration ran too early, a recipe pointing at a station the game cannot find, or two names sharing one hash.

The census asks each process for its items, recipes, network prefabs, build-table pieces and status effects by stable identity, then reconciles them with what the mod declares for each side.

Setup:

- Install the pinned default `valheimCLI.dll` on the server and the client.
- Require `valheim.observe/content-census` on each actor. It is read-only and needs a loaded world.
- Keep the reply under ValheimCLI's 256 KiB extension result. A larger census (a big content mod, or a broad prefix) fails with `result_too_large`, naming the prefixes and how many entries of each kind it held. Name fewer or narrower prefixes.

## Declare the expectations

Write the expectations from the mod's design, never from a census. A census copied into its own expectations can only pass.

```json
{
  "owner": "example.mymod",
  "scope": ["MyMod_", "Recipe_MyMod_"],
  "items":   [{ "name": "MyMod_SurveyStake", "sides": ["server", "client"] }],
  "prefabs": [{ "name": "MyMod_SurveyStake", "sides": ["server", "client"] },
              { "name": "MyMod_SurveyPost", "sides": ["server", "client"] }],
  "pieces": [{ "name": "MyMod_SurveyPost", "tool": "Hammer", "sides": ["server", "client"],
               "station": "none", "resources": ["Wood"] }],
  "statusEffects": [{ "name": "MyMod_SurveyBlessing", "sides": ["server", "client"] }],
  "recipes": [{ "name": "Recipe_MyMod_SurveyStake", "sides": ["server", "client"],
                "item": "MyMod_SurveyStake", "station": "piece_workbench", "resources": ["Wood"] }]
}
```

| Key | Meaning | Refused when |
|---|---|---|
| `owner` | The plugin GUID whose build must be pinned on each side | Missing |
| `scope` | The mod's own name prefixes; only names in scope can be unexpected | An expected name starts with none of them |
| `items`, `prefabs`, `statusEffects` | Entries with `name` and `sides` | Duplicate declaration, invalid side |
| `pieces` | Entries with `name`, `sides` and `tool`; optional `station`, `resources` | Missing `tool` |
| `recipes` | Entries with `name`, `sides`; optional `item`, `station`, `resources` | Duplicate declaration |
| `name` | The prefab, recipe, piece or effect object | Outside `scope` |
| `sides` | `server`, `client` or both: where the entry must be registered | Invalid side |
| `item` | The item prefab a recipe crafts | |
| `station` | A prefab name, or `none` for crafting by hand | |
| `resources` | Item prefab names a recipe or piece consumes | |
| `tool` | The tool whose `PieceTable` contains the piece | |

Unknown keys are refused. Optional dependencies you leave out are not checked. A prefab that exists in `ZNetScene` without the piece in its tool's table is `unresolved`, never `present`. Status effects resolve by `ObjectDB.GetStatusEffect(NameHash())`, with collisions detected across the full status-effect list.

`ContentExpectations.Load(path)` reads the file.

## Read and reconcile

Read each side through its own [strictly pinned actor](game-actors-and-pins.md#strict-pins-from-tests). Write the evidence before you assert.

```csharp
var expectations = ContentExpectations.Load("content-expectations.json");
var server = ContentCensus.Read(serverActor, "valheim.observe/content-census", expectations);
var client = ContentCensus.Read(clientActor, "valheim.observe/content-census", expectations);
var report = ContentCensus.Reconcile(expectations,
    new SideObservation(CensusSide.Server, plan.Pins["example.mymod"], server),
    new SideObservation(CensusSide.Client, plan.Client.Pins["example.mymod"], client));
round.Write("content-census", new { server, client, report }); // evidence first
report.RequirePassed();
```

`Read` throws, and none of these is an empty census, for:

| Reply | Why it throws |
|---|---|
| Not ready | No world, registries not populated yet, or a client whose player has not spawned |
| Empty registry | A registry with nothing in it is a fault, not a result |
| Another owner or scope | The reply is not for these expectations |
| Malformed | The reply does not match the census format |

**Which checks need a client.** Every process builds its own registries from its own plugins, so a server census says nothing about a client. A client expectation is satisfied only by that client's own census, and a server census handed in as the client's is refused. Declare `client` for items and recipes players craft or carry, pieces they place, status effects applied to them, and prefabs created on the client; a server-only entry declares `["server"]`, and the client then must not have it in scope. Read a client census only once the client has joined and its player has spawned; later registrations need a later census.

**When the census reads.** On demand, never at a registration hook. In the world scene, `ObjectDB.Awake` indexes its items and `ZNetScene.Awake` its prefabs as the scene loads, and mods register in postfixes on those, so both have settled before a world loads; the main menu's own ObjectDB (`FejdStartup.SetupObjectDB`) is never read. A recipe's item and resources resolve when `ObjectDB.GetItemPrefab` returns a prefab of that name whose shared item name is the reference's; its station when `ZNetScene.GetPrefab` returns a prefab of that name with a crafting station of the same station name.

## Census states

`Reconcile` gives every expected entry a state per side. Every in-scope entry a side was not expected to have is `unexpected`.

| State | Meaning |
|---|---|
| `present` | Registered once and the game's own lookup resolves it; a piece also sits in its tool's table with an enabled `Piece` component |
| `missing` | Not registered on that side, not observed there, or listed but not found by the game's lookup |
| `unexpected` | In scope on that side but not declared for it, or a recipe resource that is not declared |
| `duplicate` | Listed more than once, its hash shared with another name, or two recipes of one name |
| `unresolved` | A recipe or piece dependency the game cannot find, a piece missing from its tool's table, or a table piece missing from `ZNetScene` |
| `unsupported` | A check the census cannot answer: an adapter that omits the kind, a duplicated recipe's dependencies, a reference without a name. Never a pass |

A side is refused, and all its expected entries are `missing`, when:

- no observation was supplied for it;
- the observation reports being the other side;
- its owner plugin is not loaded, or is another build than pinned (`stale build: ...`).

The census reports the MD5 of the owner's file as ValheimCLI's strict pins hash it. `Reconcile` takes the pinned MD5, or a prefix of at least 8 hex characters. The actor's strict pins still check every loaded plugin before each command.

| Member | Meaning |
|---|---|
| `Passed` | Every side with expectations was accepted and every entry is `present` |
| `Failures` | Every problem as `side: state kind name (why)` |
| `RequirePassed()` | Throws unless `Passed` |

## Harmony census

`valheim.observe/harmony` lists every patched method with each patch's owner, kind, priority, index, before/after and patch method, from HarmonyX's own record.

A patch whose target method is missing is easy to miss. In Valheim 1.0.16, on the Windows dedicated server, the missing target leaves one `accesstools-not-found` warning (`AccessTools.DeclaredMethod: Could not find method ...`) in the BepInEx log. Then `PatchAll` throws "Undefined target method", and only Unity's own log (`-logFile`) records the exception. The rest of that plugin's `Awake`, including its later patch classes, never runs, and the mod stays loaded half-patched. The teardown [log scan](game-log-scan.md#patterns) fails on both lines, but only after the run. The census names the missing patch while the run goes on.

Declare the patches and check them:

```csharp
var declared = new[] { new DeclaredPatch("Terminal::InitTerminal", "postfix", "MyMod.Plugin+RegisterCommands::Postfix") };
HarmonyCensus.Read(actor, "valheim.observe/harmony", owner).Check(owner, declared).RequireApplied();
```

| Member | Does |
|---|---|
| `DeclaredPatch(Target, Kind, Patch)` | One patch the mod declares; a target without a parameter list names every overload |
| `HarmonyCensus.Read(actor, "valheim.observe/harmony", owner)` | Reads the census, filtered to `owner` |
| `Check(owner, declared)` | Compares the census with the declared patches |
| `RequireApplied()` | Fails naming each declared patch that is not applied; other owners' patches on the same methods are reported, never failed |
| `HarmonyCensus.OthersChanged(before, after, owner)` | Lists other owners' patches that a reload or unload removed or added |

An adapter that patches unpatches only its own Harmony ID (`harmony.UnpatchSelf()`), never `Harmony.UnpatchAll()`. A mod that calls `Harmony.UnpatchAll()` removes ValheimCLI's patches too, so a run can lose its own transport; the log scan names it as `harmony-unpatch-all`.

[FullLifecycle](../../examples/FullLifecycle/README.md) checks its mod's patch first, before the scenario runs.

## Limits

- Ownership of an entry is not observable: the registries do not record which plugin added it. That is why `scope` uses name prefixes.
- The census checks registration and resolution, not whether a player can build a piece or whether an effect behaves correctly.
- Localization tokens are out of scope.
