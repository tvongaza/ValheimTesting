// Source from the Valheim.Testing.Adapter package: compiled into a mod's game-side test adapter (a BepInEx plugin that
// references ValheimCLI and the game). Not part of the mod itself; install the adapter only in test runtimes.
#nullable enable
using System;

namespace Valheim.Testing.Adapter
{
    /// <summary>
    /// Whether an extension reply fits ValheimCLI's result bound. ValheimCLI's <c>ExtensionJson</c> refuses a result over
    /// 262,144 characters, and its host then answers only <c>result_serialization</c> ("unsupported or excessive data"),
    /// which names neither the command nor what to narrow. A command measures its reply with the same writer first and
    /// fails with its own message instead.
    /// </summary>
    internal static class ResultBudget
    {
        /// <summary>ValheimCLI's bound on one serialized extension result (fork <c>ExtensionJson.cs</c>).</summary>
        public const int MaxResultChars = 262144;
        /// <summary>Left for the result envelope (schema version, extension id, instance, code, message) around the data.</summary>
        public const int Envelope = 4096;

        /// <summary>
        /// Null when <paramref name="data"/>, written by <paramref name="write"/> (ValheimCLI's <c>ExtensionJson.Write</c>),
        /// fits with room for the envelope; otherwise why not, starting with <paramref name="what"/>.
        /// </summary>
        public static string? Exceeds(object data, Func<object?, string> write, string what)
        {
            int length;
            try { length = write(data).Length; }
            // The writer's size refusal ("... exceeds bounds."): over the bound before it finished. Its other refusals (an
            // unsupported value, a non-finite number) are a bug in the reply, not its size, and propagate.
            catch (ArgumentException e) when (e.Message.Contains("exceeds bounds")) { length = -1; }
            if (length >= 0 && length <= MaxResultChars - Envelope) return null;
            return what + " is larger than ValheimCLI's " + (MaxResultChars / 1024) + " KiB extension result" +
                (length >= 0 ? " (" + length + " characters)" : "") + ".";
        }
    }
}
