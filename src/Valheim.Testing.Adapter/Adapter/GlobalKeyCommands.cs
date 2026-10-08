// Source from Valheim.Testing.Adapter, compiled into a mod's test-only game-side adapter. A mutation
// is guarded by the adapter's fixture gate; the optional Observe pack owns the read-only key list.
#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using valheimCLI.Extensions;

namespace Valheim.Testing.Adapter
{
    /// <summary>
    /// A fixture command that sets or removes a global key on the server. ValheimCLI's optional Observe pack owns the
    /// read-only key list. In Valheim 1.0.16 a change goes to the server, which applies it and sends
    /// its whole list to every client; each client replaces its own list with it. Keys are stored lower case, as
    /// <c>name</c> or <c>name value</c>. The runner's <c>GlobalKeyFixture</c> in Valheim.Testing.Game issues a change once
    /// and waits until a joined client lists the server's set.
    /// The game's own <c>setkey</c> console command is not used: in 1.0.16 it refuses a key that is not a world-modifier
    /// setting (a boss key, for example) unless the game already counts as cheated, and that check reads the world
    /// modifiers menu. A world-modifier key (one the world-modifiers menu sets, such as <c>nomap</c>) set or removed
    /// here is also written into, or removed from, the world's saved settings (its starting global keys), as the game
    /// does for any change of such a key; other keys (boss keys, for example) are saved only in the world's own key
    /// list. Written against the Valheim 1.0.16 decompile and run on a 1.0.16 server and client.
    /// </summary>
    public static class GlobalKeyCommands
    {
        public const string ChangeSource = "global-key-change";

        /// <summary>
        /// A mutating fixture command <paramref name="name"/> for the server: <c>set &lt;name&gt; [value]</c> or
        /// <c>remove &lt;name&gt;</c>. It refuses unless <see cref="FixtureGate"/> allows fixture commands in this process.
        /// It replies <c>{source: "global-key-change", complete: true, action, name, value, keys}</c> with the server's
        /// keys after the change; the clients get the new list with the server's next message to them.
        /// </summary>
        public static ExtensionCommand Change(string enableVariable, string tokenVariable, string name = "globalkey")
        {
            if (string.IsNullOrEmpty(enableVariable)) throw new ArgumentException("Name the enabling variable.", nameof(enableVariable));
            if (string.IsNullOrEmpty(tokenVariable)) throw new ArgumentException("Name the session token variable.", nameof(tokenVariable));
            return new ExtensionCommand(name, "Set or remove a global key on the server: set <name> [value] | remove <name>",
                context => ChangeKey(context, enableVariable, tokenVariable), readOnly: false, role: ExtensionRole.Server, needsWorld: true);
        }

        /// <summary>This process's global keys, in ordinal order for the change receipt.</summary>
        private static List<string> Keys()
        {
            var system = ZoneSystem.instance ?? throw new InvalidOperationException("No ZoneSystem: load a world first.");
            var keys = system.GetGlobalKeys();
            keys.Sort(StringComparer.Ordinal);
            return keys;
        }

        private static IEnumerator ChangeKey(ExtensionContext context, string enableVariable, string tokenVariable)
        {
            if (FixtureGate.Refusal(enableVariable, tokenVariable) is string refused) { context.Fail("fixture_disabled", refused); yield break; }
            var arguments = context.Arguments;
            bool set = arguments.Count is 2 or 3 && arguments[0] == "set", remove = arguments.Count == 2 && arguments[0] == "remove";
            if ((!set && !remove) || arguments.Skip(1).Any(string.IsNullOrWhiteSpace))
            { context.Fail("usage", "set <name> [value] | remove <name>"); yield break; }
            var system = ZoneSystem.instance;
            if (system == null) { context.Fail("no_world", "No ZoneSystem: load a world first."); yield break; }
            string key = arguments[1].ToLowerInvariant();
            string? value = arguments.Count == 3 ? arguments[2] : null;
            // On the server the game handles its own routed call at once, so Keys() below already shows the change.
            if (set) system.SetGlobalKey(value == null ? key : key + " " + value);
            else system.RemoveGlobalKey(key);
            context.Succeed(new Dictionary<string, object?>
            {
                ["source"] = ChangeSource, ["complete"] = true, ["action"] = set ? "set" : "remove", ["name"] = key, ["value"] = value, ["keys"] = Keys(),
            });
        }
    }
}
