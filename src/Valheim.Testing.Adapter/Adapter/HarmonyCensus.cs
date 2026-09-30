// Source from the Valheim.Testing.Adapter package: compiled into a mod's game-side test adapter (a BepInEx plugin that
// references ValheimCLI, HarmonyX and the game). Not part of the mod itself; install the adapter only in test runtimes.
#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using valheimCLI.Extensions;

namespace Valheim.Testing.Adapter
{
    /// <summary>
    /// The Harmony patches applied in this process, by every owner. On the Valheim 1.0.16 dedicated server (BepInEx 5.4.23.5,
    /// HarmonyX 2.9.0) a patch whose target method is missing showed up as one <c>AccessTools</c> "Could not find" warning and
    /// no error line in the server's BepInEx log; the census, not the log scan, is what catches it. The mod is then
    /// half-patched and still loaded, which no test notices unless it asks. Another mod can also patch the same method, or remove
    /// patches it does not own. The census reads HarmonyX's own record (<c>Harmony.GetAllPatchedMethods</c> and
    /// <c>Harmony.GetPatchInfo</c>); the runner's <c>HarmonyCensus</c> in Valheim.Testing.Game compares it with the patches
    /// the mod declares. Reading it changes nothing. The census never unpatches anything; an adapter that patches
    /// unpatches only its own Harmony ID (<c>harmony.UnpatchSelf()</c>), never <c>Harmony.UnpatchAll()</c>.
    /// </summary>
    public static class HarmonyCensus
    {
        public const string Source = "harmony-patches";

        /// <summary>
        /// A read-only extension command <paramref name="name"/>. With no argument it lists every patched method; with one
        /// Harmony ID, only the methods that ID patches (still with every owner's patches on them), which keeps the reply
        /// within ValheimCLI's result bound in a large mod list.
        /// </summary>
        public static ExtensionCommand Command(string name = "harmony") =>
            new ExtensionCommand(name, "List applied Harmony patches: [owner]", Run, readOnly: true);

        private static IEnumerator Run(ExtensionContext context)
        {
            if (context.Arguments.Count > 1) { context.Fail("usage", "harmony [owner]"); yield break; }
            context.Succeed(Observe(context.Arguments.Count == 1 ? context.Arguments[0] : null));
        }

        /// <summary>
        /// The census as extension result data: <c>{source, complete, owner, methods: [{method, patches: [{owner, kind,
        /// priority, index, before, after, patch}]}]}</c>. <c>method</c> and <c>patch</c> are <see cref="Members.Describe"/>
        /// identities; <c>kind</c> is prefix, postfix, transpiler, finalizer or ilmanipulator. Methods are in a stable order.
        /// </summary>
        public static Dictionary<string, object?> Observe(string? owner = null)
        {
            var methods = new List<(string Method, object[] Patches)>();
            foreach (MethodBase original in Harmony.GetAllPatchedMethods())
            {
                var info = Harmony.GetPatchInfo(original);
                if (info == null) continue;
                var patches = Kinds(info).SelectMany(kind => kind.Patches.Select(patch => Describe(kind.Name, patch))).ToArray();
                if (owner != null && !patches.Any(patch => (string?)patch["owner"] == owner)) continue;
                methods.Add((Members.Describe(original), patches));
            }
            return new Dictionary<string, object?>
            {
                ["source"] = Source, ["complete"] = true, ["owner"] = owner,
                ["methods"] = methods.OrderBy(m => m.Method, StringComparer.Ordinal)
                    .Select(m => new Dictionary<string, object?> { ["method"] = m.Method, ["patches"] = m.Patches }).ToArray(),
            };
        }

        private static IEnumerable<(string Name, IEnumerable<Patch> Patches)> Kinds(Patches info)
        {
            yield return ("prefix", info.Prefixes ?? Enumerable.Empty<Patch>());
            yield return ("postfix", info.Postfixes ?? Enumerable.Empty<Patch>());
            yield return ("transpiler", info.Transpilers ?? Enumerable.Empty<Patch>());
            yield return ("finalizer", info.Finalizers ?? Enumerable.Empty<Patch>());
            yield return ("ilmanipulator", info.ILManipulators ?? Enumerable.Empty<Patch>());
        }

        private static Dictionary<string, object?> Describe(string kind, Patch patch) => new Dictionary<string, object?>
        {
            ["owner"] = patch.owner, ["kind"] = kind, ["priority"] = patch.priority, ["index"] = patch.index,
            ["before"] = patch.before ?? Array.Empty<string>(), ["after"] = patch.after ?? Array.Empty<string>(),
            ["patch"] = patch.PatchMethod == null ? null : Members.Describe(patch.PatchMethod),
        };
    }
}
