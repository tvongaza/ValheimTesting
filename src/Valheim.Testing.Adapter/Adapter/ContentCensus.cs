// Source from the Valheim.Testing.Adapter package: compiled into a mod's game-side test adapter (a BepInEx plugin that
// references ValheimCLI and the game). Not part of the mod itself; install the adapter only in test runtimes.
#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using BepInEx.Bootstrap;
using UnityEngine;
using valheimCLI.Extensions;

namespace Valheim.Testing.Adapter
{
    /// <summary>
    /// The items, recipes and network prefabs this process has registered, by stable identity, for the runner's
    /// <c>ContentCensus</c> in Valheim.Testing.Game. It lists the entries whose names start with one of the declared prefixes
    /// (a mod's own content; vanilla content is never listed), each with the hash the game computes for its name, how
    /// often its registry's list holds it and what the game's own lookup by that hash returns; every hash two different
    /// names share anywhere in a registry; and, for each listed recipe, whether the game can resolve the item it crafts,
    /// its crafting station and its resources. With it, which side observed (server or client) and the owner plugin's
    /// build (version and the MD5 of its file, as ValheimCLI's pins hash it), so a report can never be read as another
    /// side's or another build's. Reading changes nothing.
    /// <para>
    /// When: on demand, once a world is loaded, never at a registration hook. In Valheim 1.0.16 the world scene's
    /// <c>ObjectDB.Awake</c> indexes its items (<c>UpdateRegisters</c>) and <c>ZNetScene.Awake</c> indexes its prefabs as the
    /// scene loads, and mods register in postfixes on those, so both registries have settled before the world loads. The
    /// main menu has its own <c>ObjectDB</c> (<c>FejdStartup.SetupObjectDB</c>: <c>Awake</c> on an empty database, then
    /// <c>CopyOtherDB</c>) and no <c>ZNetScene</c>; that database is not the world's. Some mods register later still (after
    /// a server's config sync reaches a client, or at spawn), so a census is read when the runner asks, and on a client
    /// only once the local player has spawned. Before that the reply is <c>complete: false</c> with <c>ready: false</c> and
    /// the reason, never empty lists.
    /// </para>
    /// Written against the Valheim 1.0.16 decompile; not yet run in game.
    /// </summary>
    public static class ContentCensus
    {
        public const string Source = "content-census";
        /// <summary>The most name prefixes one census takes.</summary>
        public const int MaxPrefixes = 16;
        /// <summary>The most entries of one kind a census lists before refusing, rather than returning a truncated list.</summary>
        public const int MaxEntries = 2000;
        /// <summary>The most shared hashes a census lists before refusing.</summary>
        public const int MaxCollisions = 200;

        /// <summary>
        /// A read-only extension command <paramref name="name"/>: <c>&lt;owner-guid&gt; &lt;prefix&gt; [&lt;prefix&gt; ...]</c>.
        /// The owner is the plugin whose build the reply names; the prefixes say which names are that mod's content. It
        /// replies <see cref="Observe"/>'s data.
        /// </summary>
        public static ExtensionCommand Command(string name = "content-census") =>
            new ExtensionCommand(name, "List registered items, recipes and prefabs whose names start with a prefix: <owner-guid> <prefix> [<prefix> ...]",
                Run, readOnly: true, needsWorld: true);

        private static IEnumerator Run(ExtensionContext context)
        {
            var arguments = context.Arguments;
            if (arguments.Count < 2 || arguments.Count > MaxPrefixes + 1 || arguments.Any(string.IsNullOrEmpty))
            { context.Fail("usage", "content-census <owner-guid> <prefix> [<prefix> ...], at most " + MaxPrefixes + " prefixes"); yield break; }
            context.Succeed(Observe(arguments[0], arguments.Skip(1).ToArray()));
        }

        /// <summary>
        /// <c>{source, complete, ready, reason, side, dedicated, owner: {guid, installed, version, md5}, scope,
        /// totals: {items, itemIndex, recipes, prefabs, prefabIndex}, items: [entry], prefabs: [entry], recipes: [recipe],
        /// collisions: [{registry, hash, names, indexed}]}</c>. An entry is <c>{name, hash, listed, resolves}</c>: the hash of
        /// its name as the game computes it, how many times the registry's list holds it (<c>ObjectDB.m_items</c>;
        /// <c>ZNetScene.m_prefabs</c> and <c>m_nonNetViewPrefabs</c>; 0 for an entry only in the index), and the name of what
        /// the game's lookup by that hash returns (<c>ObjectDB.GetItemPrefab</c>, <c>ZNetScene.GetPrefab</c>) or null. A recipe
        /// is <c>{name, enabled, amount, item, station, minStationLevel, resources: [{name, amount, lookup}]}</c>, each of
        /// <c>item</c> and <c>station</c> <c>{name, lookup}</c>; <c>lookup</c> is <c>resolved</c>, <c>unresolved</c>,
        /// <c>unsupported</c> (a reference without a name, which no lookup can answer) or, for a station, <c>none</c>
        /// (crafted by hand). A collision is a hash that two different names share in a registry's list or index, with the
        /// name the index holds for it. Lists are in ordinal name order.
        /// </summary>
        public static Dictionary<string, object?> Observe(string owner, IReadOnlyList<string> prefixes)
        {
            if (string.IsNullOrEmpty(owner)) throw new ArgumentException("Name the owner plugin's GUID.", nameof(owner));
            if (prefixes == null || prefixes.Count == 0 || prefixes.Count > MaxPrefixes || prefixes.Any(string.IsNullOrEmpty))
                throw new ArgumentException("Name 1 to " + MaxPrefixes + " non-empty prefixes.", nameof(prefixes));
            string? notReady = NotReady();
            if (notReady != null)
                return new Dictionary<string, object?> { ["source"] = Source, ["complete"] = false, ["ready"] = false, ["reason"] = notReady };

            var net = ZNet.instance; var database = ObjectDB.instance; var scene = ZNetScene.instance;
            var itemIndex = Members.Field<Dictionary<int, GameObject>>(database, "m_itemByHash");
            var prefabIndex = Members.Field<Dictionary<int, GameObject>>(scene, "m_namedPrefabs");
            var prefabList = scene.m_prefabs.Concat(scene.m_nonNetViewPrefabs).ToList();
            bool InScope(string? name) => !string.IsNullOrEmpty(name) && prefixes.Any(prefix => name!.StartsWith(prefix, StringComparison.Ordinal));

            var items = Entries(database.m_items, itemIndex, InScope, hash => database.GetItemPrefab(hash), "items");
            var prefabs = Entries(prefabList, prefabIndex, InScope, hash => scene.GetPrefab(hash), "prefabs");
            var recipes = new List<(string Name, Dictionary<string, object?> Data)>();
            foreach (Recipe recipe in database.m_recipes)
            {
                if (recipe == null) continue;
                string? item = recipe.m_item == null ? null : recipe.m_item.gameObject.name;
                if (!InScope(recipe.name) && !InScope(item)) continue;
                if (recipes.Count >= MaxEntries) throw new InvalidOperationException($"More than {MaxEntries} recipes in scope; nothing is returned rather than a partial census.");
                recipes.Add((recipe.name, DescribeRecipe(recipe, database, scene)));
            }
            var collisions = Collisions(database.m_items, itemIndex, "items").Concat(Collisions(prefabList, prefabIndex, "prefabs")).ToList();
            if (collisions.Count > MaxCollisions) throw new InvalidOperationException($"More than {MaxCollisions} shared hashes; nothing is returned rather than a partial census.");

            return new Dictionary<string, object?>
            {
                ["source"] = Source, ["complete"] = true, ["ready"] = true, ["reason"] = null,
                ["side"] = net.IsServer() ? "server" : "client", ["dedicated"] = net.IsDedicated(),
                ["owner"] = Owner(owner), ["scope"] = prefixes.ToArray(),
                ["totals"] = new Dictionary<string, object?>
                {
                    ["items"] = database.m_items.Count, ["itemIndex"] = itemIndex.Count, ["recipes"] = database.m_recipes.Count,
                    ["prefabs"] = prefabList.Count, ["prefabIndex"] = prefabIndex.Count,
                },
                ["items"] = items, ["prefabs"] = prefabs,
                ["recipes"] = recipes.OrderBy(r => r.Name, StringComparer.Ordinal).Select(r => r.Data).ToArray(),
                ["collisions"] = collisions.ToArray(),
            };
        }

        /// <summary>
        /// Why a census would not describe the world's registries yet, or null: no network session or world scene, a
        /// database or scene index still empty, or (on a client) no spawned local player.
        /// </summary>
        public static string? NotReady()
        {
            var net = ZNet.instance;
            if (net == null) return "No network session: load or join a world first.";
            if (ZNetScene.instance == null) return "No ZNetScene: the world scene has not loaded.";
            var database = ObjectDB.instance;
            if (database == null || database.m_items.Count == 0) return "ObjectDB is not populated yet.";
            if (Members.Field<Dictionary<int, GameObject>>(ZNetScene.instance, "m_namedPrefabs").Count == 0) return "ZNetScene has indexed no prefabs yet.";
            if (!net.IsServer() && Player.m_localPlayer == null) return "The local player has not spawned yet; a client's late registrations may still be coming.";
            return null;
        }

        private static object[] Entries(IEnumerable<GameObject> list, Dictionary<int, GameObject> index, Func<string?, bool> inScope,
            Func<int, GameObject> lookup, string kind)
        {
            var listed = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (GameObject prefab in list)
                if (prefab != null && inScope(prefab.name)) listed[prefab.name] = listed.TryGetValue(prefab.name, out int count) ? count + 1 : 1;
            foreach (GameObject prefab in index.Values)
                if (prefab != null && inScope(prefab.name) && !listed.ContainsKey(prefab.name)) listed[prefab.name] = 0;
            if (listed.Count > MaxEntries) throw new InvalidOperationException($"More than {MaxEntries} {kind} in scope; nothing is returned rather than a partial census.");
            return listed.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry =>
            {
                int hash = entry.Key.GetStableHashCode();
                GameObject found = lookup(hash);
                return (object)new Dictionary<string, object?>
                {
                    ["name"] = entry.Key, ["hash"] = hash, ["listed"] = entry.Value, ["resolves"] = found == null ? null : found.name,
                };
            }).ToArray();
        }

        // Every hash that two different names share in the list or the index, and what the index holds for it: the game's
        // indexes use Dictionary.Add, so the second name of a shared hash either threw at indexing or was never indexed.
        private static IEnumerable<Dictionary<string, object?>> Collisions(IEnumerable<GameObject> list, Dictionary<int, GameObject> index, string registry)
        {
            var names = new Dictionary<int, SortedSet<string>>();
            void Add(int hash, string name)
            {
                if (!names.TryGetValue(hash, out var set)) names.Add(hash, set = new SortedSet<string>(StringComparer.Ordinal));
                set.Add(name);
            }
            foreach (GameObject prefab in list) if (prefab != null) Add(prefab.name.GetStableHashCode(), prefab.name);
            // An index entry counts under its key: a prefab indexed under another name's hash shares that hash.
            foreach (var entry in index) if (entry.Value != null) Add(entry.Key, entry.Value.name);
            foreach (var entry in names.Where(e => e.Value.Count > 1).OrderBy(e => e.Key))
                yield return new Dictionary<string, object?>
                {
                    ["registry"] = registry, ["hash"] = entry.Key, ["names"] = entry.Value.ToArray(),
                    ["indexed"] = index.TryGetValue(entry.Key, out var held) && held != null ? held.name : null,
                };
        }

        private static Dictionary<string, object?> DescribeRecipe(Recipe recipe, ObjectDB database, ZNetScene scene) => new Dictionary<string, object?>
        {
            ["name"] = recipe.name, ["enabled"] = recipe.m_enabled, ["amount"] = recipe.m_amount,
            ["item"] = Item(recipe.m_item, database), ["station"] = Station(recipe.m_craftingStation, scene), ["minStationLevel"] = recipe.m_minStationLevel,
            ["resources"] = (recipe.m_resources ?? new Piece.Requirement[0]).Select(requirement =>
            {
                var resource = Item(requirement?.m_resItem, database);
                resource["amount"] = requirement?.m_amount ?? 0;
                return (object)resource;
            }).ToArray(),
        };

        // An item reference resolves when the game's lookup by its prefab name returns a prefab of that name whose shared
        // item name is the reference's: crafting adds the result by prefab name and counts resources by shared name.
        private static Dictionary<string, object?> Item(ItemDrop? reference, ObjectDB database)
        {
            if (reference == null) return Dependency(null, "unresolved");
            string name = reference.gameObject.name;
            if (string.IsNullOrEmpty(name)) return Dependency(name, "unsupported");
            GameObject found = database.GetItemPrefab(name.GetStableHashCode());
            ItemDrop? drop = found == null ? null : found.GetComponent<ItemDrop>();
            bool resolved = found != null && found.name == name && drop != null &&
                drop.m_itemData.m_shared.m_name == reference.m_itemData.m_shared.m_name;
            return Dependency(name, resolved ? "resolved" : "unresolved");
        }

        // A station resolves when the scene's lookup by its prefab name returns a prefab of that name with a crafting
        // station of the same station name: the game finds a nearby station by that name.
        private static Dictionary<string, object?> Station(CraftingStation? reference, ZNetScene scene)
        {
            if (reference == null) return Dependency(null, "none");
            string name = reference.gameObject.name;
            if (string.IsNullOrEmpty(name)) return Dependency(name, "unsupported");
            GameObject found = scene.GetPrefab(name.GetStableHashCode());
            CraftingStation? station = found == null ? null : found.GetComponent<CraftingStation>();
            bool resolved = found != null && found.name == name && station != null && station.m_name == reference.m_name;
            return Dependency(name, resolved ? "resolved" : "unresolved");
        }

        private static Dictionary<string, object?> Dependency(string? name, string lookup) =>
            new Dictionary<string, object?> { ["name"] = name, ["lookup"] = lookup };

        // The owner's build as ValheimCLI's strict pins see it: the MD5 of the plugin's file on disk.
        private static Dictionary<string, object?> Owner(string guid)
        {
            bool installed = Chainloader.PluginInfos.TryGetValue(guid, out var info) && info != null && info.Instance != null;
            string? md5 = null;
            if (installed && !string.IsNullOrEmpty(info!.Location) && File.Exists(info.Location))
                using (var hash = MD5.Create())
                using (var stream = File.OpenRead(info.Location))
                    md5 = string.Concat(hash.ComputeHash(stream).Select(b => b.ToString("x2")));
            return new Dictionary<string, object?>
            {
                ["guid"] = guid, ["installed"] = installed, ["version"] = installed ? info!.Metadata.Version.ToString() : null, ["md5"] = md5,
            };
        }
    }
}
