// Source from the Valheim.Testing.Adapter package: compiled into a mod's game-side test adapter (a BepInEx plugin that
// references ValheimCLI and the game). Not part of the mod itself; install the adapter only in test runtimes.
#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx.Bootstrap;
using valheimCLI.Extensions;

namespace Valheim.Testing.Adapter
{
    /// <summary>
    /// Whether a plugin is loaded in this process, for an adapter that depends on its mod only softly (so it can also run,
    /// and say so, where the mod is absent) and for a runner that must know which side has the mod before judging a result.
    /// </summary>
    public static class InstalledPlugin
    {
        /// <summary>The loaded plugin's version, or null when BepInEx has no live instance of <paramref name="guid"/>.</summary>
        public static string? Version(string guid)
        {
            if (string.IsNullOrEmpty(guid)) throw new ArgumentException("Name the plugin GUID.", nameof(guid));
            return Chainloader.PluginInfos.TryGetValue(guid, out var info) && info != null && info.Instance != null
                ? info.Metadata.Version.ToString()
                : null;
        }

        /// <summary>
        /// A read-only extension command <paramref name="name"/> (no arguments) reporting
        /// <c>{source: "plugin-registry", complete: true, guid, installed, version}</c> for <paramref name="guid"/>.
        /// </summary>
        public static ExtensionCommand Command(string name, string guid)
        {
            if (string.IsNullOrEmpty(guid)) throw new ArgumentException("Name the plugin GUID.", nameof(guid));
            return new ExtensionCommand(name, "Report whether plugin " + guid + " is loaded, and its version", context => Report(context, guid), readOnly: true);
        }

        private static IEnumerator Report(ExtensionContext context, string guid)
        {
            if (context.Arguments.Count != 0) { context.Fail("usage", "takes no arguments"); yield break; }
            string? version = Version(guid);
            context.Succeed(new Dictionary<string, object?>
            {
                ["source"] = "plugin-registry", ["complete"] = true, ["guid"] = guid,
                ["installed"] = version != null, ["version"] = version,
            });
        }
    }
}
