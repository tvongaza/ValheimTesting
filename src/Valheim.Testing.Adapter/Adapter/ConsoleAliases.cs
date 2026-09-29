// Source from the Valheim.Testing.Adapter package: compiled into a mod's game-side test adapter (a BepInEx plugin that
// references ValheimCLI and the game). Not part of the mod itself; install the adapter only in test runtimes.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using valheimCLI;
using valheimCLI.Extensions;

namespace Valheim.Testing.Adapter
{
    /// <summary>
    /// Console commands that run an adapter's extension commands under the names existing tests or players already type,
    /// for example a mod's old <c>mymod_status</c> command moved into its test adapter. Each alias goes through the same
    /// dispatch as <c>cli_extension</c> (<c>ExtensionHost.Execute</c>), so it keeps the extension's role, world,
    /// devcommands, cancellation and mutation-gate rules; it waives nothing.
    /// </summary>
    public static class ConsoleAliases
    {
        /// <summary>
        /// Adds each alias as a console command running <c>registration.Id/command</c> with the alias's arguments. A name
        /// that is already a console command refuses the whole set (and leaves the table as it was) rather than replacing
        /// another owner's command; so does a command the registration does not have. When the registration is disposed,
        /// only the aliases this call added and that are still these very commands are removed: a command another plugin
        /// has put under the same name since stays. Call on the main thread after the game's terminal has started.
        /// </summary>
        public static void Add(ExtensionRegistration registration, params (string Alias, string Command)[] aliases)
        {
            if (registration == null) throw new ArgumentNullException(nameof(registration));
            if (aliases == null || aliases.Length == 0) throw new ArgumentException("Name at least one alias.", nameof(aliases));
            if (registration.IsClosing) throw new ObjectDisposedException(registration.Id);
            // Unity's null test, as TestExtension waits: a destroyed plugin is not ready.
            if (valheimCLIPlugin.Instance == null || valheimCLIPlugin.Instance!.Extensions == null) throw new InvalidOperationException("ValheimCLI's extension API is not ready.");
            var registry = valheimCLIPlugin.Instance!.Extensions!;
            var commands = new HashSet<string>(registry.Commands(registration).Select(command => command.Name), StringComparer.Ordinal);
            foreach (var (alias, command) in aliases)
            {
                if (string.IsNullOrWhiteSpace(alias) || alias.Any(char.IsWhiteSpace)) throw new ArgumentException("An alias is one word: '" + alias + "'.", nameof(aliases));
                if (!commands.Contains(command)) throw new ArgumentException(registration.Id + " has no command " + command + ".", nameof(aliases));
            }
            if (aliases.Select(a => a.Alias.ToLowerInvariant()).Distinct().Count() != aliases.Length) throw new ArgumentException("An alias is named twice.", nameof(aliases));
            // The game's command table (private in the shipped game). Registration records which entries it added and
            // rolls back, restoring any replaced command, if a name was taken.
            var table = Members.StaticField<Dictionary<string, Terminal.ConsoleCommand>>(typeof(Terminal), "commands");
            var owned = OwnedCommandSet<Terminal.ConsoleCommand>.Register(table, () =>
            {
                foreach (var (alias, command) in aliases)
                {
                    string path = registration.Id + "/" + command;
                    new Terminal.ConsoleCommand(alias, "Test adapter: " + path, args =>
                        ExtensionHost.Execute(registry, path, args.Args.Skip(1).ToArray(), args.Context.AddString));
                }
            });
            try { registration.OnDispose(owned.Dispose); }
            catch { owned.Dispose(); throw; }
        }
    }
}
