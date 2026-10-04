// Valheim.Testing.Doubles: compile-time stand-ins for the Unity, Valheim, BepInEx and Jotunn types a mod's
// pure-logic sources use, so those sources compile and run in an ordinary test project without the game.
// Source package: these files are compiled into the consuming test project. Every type is partial; add the
// members your mod needs in your own files. Behaviour mirrors the game where mod code depends on it.
#nullable enable
// ReSharper disable InconsistentNaming
// BepInEx logging (capturable). Harmony's attributes and helpers are in HarmonyDoubles.cs, configuration in ConfigDoubles.cs.
using System;
using Valheim.Testing.Doubles;

namespace BepInEx.Logging
{
    public partial class ManualLogSource
    {
        /// <summary>
        /// When non-null, every log line is also appended here — tests use
        /// this to observe mod behavior (e.g. what a mod logged at startup).
        /// </summary>
        [TestOnly] public static System.Collections.Generic.List<string>? Captured;

        /// <summary>
        /// Makes the next <see cref="LogInfo"/> throw, once: a test's way to prove that a failing report cannot decide
        /// what the mod does. Cleared when it fires.
        /// </summary>
        [TestOnly] public static bool ThrowOnNextInfo;

        public void LogDebug(object data) => Write("DEBUG", data);
        public void LogInfo(object data)
        {
            if (ThrowOnNextInfo) { ThrowOnNextInfo = false; throw new InvalidOperationException("injected log sink failure"); }
            Write("INFO ", data);
        }
        public void LogWarning(object data) => Write("WARN ", data);
        public void LogError(object data) => Write("ERROR", data);

        private static void Write(string level, object data)
        {
            string line = data?.ToString() ?? "";
            Captured?.Add(line);
            System.Console.WriteLine($"[{level}] {line}");
        }
    }
}

