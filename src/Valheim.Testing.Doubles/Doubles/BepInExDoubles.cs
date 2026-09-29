// Valheim.Testing.Doubles: compile-time stand-ins for the Unity, Valheim, BepInEx and Jotunn types a mod's
// pure-logic sources use, so those sources compile and run in an ordinary test project without the game.
// Source package: these files are compiled into the consuming test project. Every type is partial; add the
// members your mod needs in your own files. Behaviour mirrors the game where mod code depends on it.
#nullable enable
// ReSharper disable InconsistentNaming
// BepInEx logging (capturable) and the Harmony attribute mod sources carry.
using System;

namespace BepInEx.Logging
{
    public partial class ManualLogSource
    {
        /// <summary>
        /// When non-null, every log line is also appended here — tests use
        /// this to observe mod behavior (e.g. which roads were generated).
        /// </summary>
        public static System.Collections.Generic.List<string>? Captured;

        public void LogDebug(object data) => Write("DEBUG", data);
        public void LogInfo(object data) => Write("INFO ", data);
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

namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed partial class HarmonyPatch : Attribute { public HarmonyPatch(Type type, string method) { } }
}
