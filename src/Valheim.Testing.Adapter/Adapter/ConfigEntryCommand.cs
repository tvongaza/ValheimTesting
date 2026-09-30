// Source from the Valheim.Testing.Adapter package: compiled into a mod's game-side test adapter (a BepInEx plugin that
// references ValheimCLI and BepInEx). Not part of the mod itself; install the adapter only in test runtimes.
#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using valheimCLI.Extensions;

namespace Valheim.Testing.Adapter
{
    /// <summary>
    /// A plugin's live config entry in this process: the value its <c>ConfigEntry</c> holds now, on a server or a client.
    /// How a mod syncs its config (ServerSync embedded in the mod, Jötunn, its own RPC) is the mod's business; the ones that
    /// sync set the client's entry, so reading the entry on each side shows what each side uses. Reading changes nothing,
    /// not even the config file. Only entries in the plugin's own <c>Config</c> (<c>BepInEx/config/&lt;GUID&gt;.cfg</c>) are
    /// found; a mod that binds entries in another <c>ConfigFile</c> needs its own observation.
    /// </summary>
    public static class ConfigEntryCommand
    {
        public const string Source = "bepinex-config";

        /// <summary>
        /// A read-only extension command <paramref name="name"/>: <c>&lt;guid&gt; &lt;section&gt; &lt;key&gt;</c>, each
        /// percent-encoded (<c>Enable%20Mod</c>), since extension arguments are single tokens. It replies
        /// <see cref="Observe"/>'s data.
        /// </summary>
        public static ExtensionCommand Command(string name = "config") =>
            new ExtensionCommand(name, "Read a plugin's live config entry: <guid> <section> <key>, percent-encoded", Run, readOnly: true);

        private static IEnumerator Run(ExtensionContext context)
        {
            if (context.Arguments.Count != 3) { context.Fail("usage", "config <guid> <section> <key> (percent-encoded)"); yield break; }
            context.Succeed(Observe(Uri.UnescapeDataString(context.Arguments[0]), Uri.UnescapeDataString(context.Arguments[1]), Uri.UnescapeDataString(context.Arguments[2])));
        }

        /// <summary>
        /// <c>{source: "bepinex-config", complete: true, guid, section, key, server, installed, found, type, value,
        /// defaultValue}</c>. <c>value</c> and <c>defaultValue</c> are written as BepInEx writes them to the config file
        /// (<c>true</c>, <c>5</c>, <c>1.5</c>), so they compare as text whatever the entry's type. <c>installed</c> is false
        /// when BepInEx has no live instance of the plugin, <c>found</c> when it has no entry with this section and key
        /// (both compared ordinally, as BepInEx does); both are answers, not failures.
        /// </summary>
        public static Dictionary<string, object?> Observe(string guid, string section, string key)
        {
            if (string.IsNullOrEmpty(guid)) throw new ArgumentException("Name the plugin GUID.", nameof(guid));
            var data = new Dictionary<string, object?>
            {
                ["source"] = Source, ["complete"] = true, ["guid"] = guid, ["section"] = section, ["key"] = key,
                ["server"] = ZNet.instance != null && ZNet.instance.IsServer(),
                ["installed"] = false, ["found"] = false, ["type"] = null, ["value"] = null, ["defaultValue"] = null,
            };
            if (!Chainloader.PluginInfos.TryGetValue(guid, out var info) || info == null || info.Instance == null) return data;
            data["installed"] = true;
            foreach (KeyValuePair<ConfigDefinition, ConfigEntryBase> entry in info.Instance.Config)
            {
                if (!string.Equals(entry.Key.Section, section, StringComparison.Ordinal) || !string.Equals(entry.Key.Key, key, StringComparison.Ordinal)) continue;
                data["found"] = true;
                data["type"] = entry.Value.SettingType.FullName;
                data["value"] = entry.Value.GetSerializedValue();
                data["defaultValue"] = TomlTypeConverter.ConvertToString(entry.Value.DefaultValue, entry.Value.SettingType);
                break;
            }
            return data;
        }
    }
}
