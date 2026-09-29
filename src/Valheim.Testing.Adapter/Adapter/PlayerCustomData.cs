// Source from the Valheim.Testing.Adapter package: compiled into a mod's game-side test adapter (a BepInEx plugin that
// references ValheimCLI and the game). Not part of the mod itself; install the adapter only in test runtimes.
#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using valheimCLI.Extensions;

namespace Valheim.Testing.Adapter
{
    /// <summary>
    /// The local player's custom data (<c>Player.m_customData</c>, which mods use to keep their own per-character state)
    /// and the profile it is saved in, for the runner's <c>LogoutCycle</c> in Valheim.Testing.Game. The game writes the
    /// custom data into the character file when it saves the player (on logout, among other times) and reads it back when
    /// the character next spawns. Read-only, client only. Written against the Valheim 1.0.16 decompile; not yet run in game.
    /// </summary>
    public static class PlayerCustomData
    {
        public const string Source = "local-player-custom-data";

        /// <summary>
        /// A read-only client extension command <paramref name="name"/>: <c>custom-data [key-prefix]</c>. With a prefix only
        /// the keys starting with it are listed, which keeps the reply within ValheimCLI's result bound when other mods keep
        /// large values there.
        /// </summary>
        public static ExtensionCommand Command(string name = "custom-data") =>
            new ExtensionCommand(name, "Read the local player's custom data and its profile: [key-prefix]", Run,
                readOnly: true, role: ExtensionRole.Client, needsWorld: true);

        private static IEnumerator Run(ExtensionContext context)
        {
            if (context.Arguments.Count > 1) { context.Fail("usage", "custom-data [key-prefix]"); yield break; }
            context.Succeed(Observe(context.Arguments.Count == 1 ? context.Arguments[0] : null));
        }

        /// <summary>
        /// The reading as extension result data: <c>{source, complete, prefix, character, profileFile, fileSource,
        /// profilePath, entries: [{key, value}]}</c>, entries in ordinal key order. <c>character</c> is the profile's
        /// name, <c>profileFile</c> its file name without <c>.fch</c>, <c>fileSource</c> where the game saves it
        /// (<c>Local</c> or <c>Cloud</c>) and <c>profilePath</c> the file the game writes. Incomplete until the local player
        /// has spawned.
        /// </summary>
        public static Dictionary<string, object?> Observe(string? prefix = null)
        {
            var player = Player.m_localPlayer;
            var game = Game.instance;
            var profile = game == null ? null : game.GetPlayerProfile();
            if (player == null || profile == null || player.m_customData == null)
                return new Dictionary<string, object?> { ["source"] = Source, ["complete"] = false, ["prefix"] = prefix };
            var entries = player.m_customData
                .Where(entry => prefix == null || entry.Key.StartsWith(prefix, StringComparison.Ordinal))
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => (object?)new Dictionary<string, object?> { ["key"] = entry.Key, ["value"] = entry.Value })
                .ToArray();
            return new Dictionary<string, object?>
            {
                ["source"] = Source, ["complete"] = true, ["prefix"] = prefix,
                ["character"] = profile.GetName(), ["profileFile"] = profile.GetFilename(), ["fileSource"] = profile.m_fileSource.ToString(),
                ["profilePath"] = profile.GetPath(), ["entries"] = entries,
            };
        }
    }
}
