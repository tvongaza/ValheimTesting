// Source compiled into a mod's game-side test adapter. A review capture borrows one owned client's visual state and
// restores it even when a capture fails or the adapter unloads. The snapshot never leaves this process.
#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using valheimCLI.Extensions;

namespace Valheim.Testing.Adapter
{
    /// <summary>
    /// A single in-flight visual review lease. Register <see cref="BeginCommand"/> and <see cref="RestoreCommand"/>
    /// together with <see cref="MistOffCommand"/>, and call <see cref="RestoreOnUnload"/> before disposing their
    /// extension registration. Begin only reads state; the runner then changes player safety, weather and camera for a
    /// screenshot. Mist-off snapshots and changes each loaded volume in one game command, including zones loaded after
    /// the player arrives. Restore puts the exact
    /// starting values back, including mist volumes that were already inactive. A pre-existing free-fly camera is
    /// refused because returning its pose reliably would need more state than the game exposes here.
    /// </summary>
    public static class ReviewState
    {
        private sealed class Snapshot
        {
            public string Id = "";
            public Player Player = null!;
            public EnvMan Environment = null!;
            public GameCamera Camera = null!;
            public bool God, Ghost, Debug, Fly, Cheats, DebugTimeEnabled;
            public float DebugTime;
            public string? DebugEnvironment;
            public Dictionary<Mister, bool> Mist = new Dictionary<Mister, bool>();
            public ClutterSystem? Clutter;
            public Dictionary<ClutterSystem.Clutter, bool> ClutterEntries = new Dictionary<ClutterSystem.Clutter, bool>();
        }

        private static Snapshot? _active;
        private static string? _lastRestored;

        /// <summary>Whether a visual capture still owns this client's review-state lease.</summary>
        public static bool Owns(string id) => _active != null && _active.Id == id;

        /// <summary>Read the client's starting visual and safety state: <c>review-begin &lt;run-id&gt;</c>.</summary>
        public static ExtensionCommand BeginCommand(string name = "review-begin") =>
            new ExtensionCommand(name, "Snapshot the owned client's review state: <run-id>", Begin,
                readOnly: true, role: ExtensionRole.Client, needsWorld: true);

        /// <summary>Restore the matching snapshot: <c>review-restore &lt;run-id&gt;</c>.</summary>
        public static ExtensionCommand RestoreCommand(string name = "review-restore") =>
            new ExtensionCommand(name, "Restore the owned client's review state: <run-id>", Restore,
                role: ExtensionRole.Client, needsWorld: true);

        /// <summary>Turn off loaded mist, recording each volume's own prior state: <c>review-mist-off &lt;run-id&gt;</c>.</summary>
        public static ExtensionCommand MistOffCommand(string name = "review-mist-off") =>
            new ExtensionCommand(name, "Hide mist for this review capture and remember each volume: <run-id>", MistOff,
                role: ExtensionRole.Client, needsWorld: true);

        /// <summary>Hide decorative clutter while recording each entry's prior flag: <c>review-clutter-off &lt;run-id&gt;</c>.</summary>
        public static ExtensionCommand ClutterOffCommand(string name = "review-clutter-off") =>
            new ExtensionCommand(name, "Hide ground clutter for this review capture and remember each entry: <run-id>", ClutterOff,
                role: ExtensionRole.Client, needsWorld: true);

        private static IEnumerator Begin(ExtensionContext context)
        {
            if (!Id(context, out var id)) yield break;
            if (_active != null)
            {
                if (_active.Id != id) context.Fail("busy", "another review capture owns this client's state");
                else context.Succeed(Describe(_active, "begun")); // A lost reply may be read again without replacing the snapshot.
                yield break;
            }
            var player = Player.m_localPlayer;
            var environment = EnvMan.instance;
            var camera = GameCamera.instance;
            if (player == null || environment == null || camera == null)
            {
                context.Fail("not_ready", "player, environment and camera must be loaded"); yield break;
            }
            if (Members.Field<bool>(camera, "m_freeFly"))
            {
                context.Fail("camera_busy", "release the existing free-fly camera before beginning review"); yield break;
            }
            var mist = UnityEngine.Object.FindObjectsByType<Mister>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .ToDictionary(volume => volume, volume => volume.gameObject.activeSelf);
            _active = new Snapshot
            {
                Id = id, Player = player, Environment = environment, Camera = camera,
                God = player.InGodMode(), Ghost = player.InGhostMode(), Debug = Player.m_debugMode,
                Fly = player.InDebugFlyMode(), Cheats = Members.StaticField<bool>(typeof(Terminal), "m_cheat"),
                DebugTimeEnabled = environment.m_debugTimeOfDay, DebugTime = environment.m_debugTime,
                DebugEnvironment = environment.m_debugEnv, Mist = mist,
            };
            _lastRestored = null;
            context.Succeed(Describe(_active, "begun"));
        }

        private static IEnumerator Restore(ExtensionContext context)
        {
            if (!Id(context, out var id)) yield break;
            if (_active == null)
            {
                if (_lastRestored == id) context.Succeed(new Dictionary<string, object?> { ["source"] = "review-state", ["complete"] = true, ["id"] = id, ["state"] = "restored" });
                else context.Fail("not_owned", "no matching review snapshot remains");
                yield break;
            }
            if (_active.Id != id) { context.Fail("not_owned", "another review snapshot owns the client"); yield break; }
            try
            {
                RestoreNow(_active);
                _active = null;
                _lastRestored = id;
                context.Succeed(new Dictionary<string, object?> { ["source"] = "review-state", ["complete"] = true, ["id"] = id, ["state"] = "restored" });
            }
            catch (Exception error) { context.Fail("restore_failed", error.Message); }
        }

        private static IEnumerator MistOff(ExtensionContext context)
        {
            if (!Id(context, out var id)) yield break;
            if (_active == null || _active.Id != id) { context.Fail("not_owned", "begin this review before changing mist"); yield break; }
            var volumes = UnityEngine.Object.FindObjectsByType<Mister>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var volume in volumes)
            {
                if (!_active.Mist.ContainsKey(volume)) _active.Mist.Add(volume, volume.gameObject.activeSelf);
                volume.gameObject.SetActive(false);
            }
            context.Succeed(new Dictionary<string, object?> { ["source"] = "review-state", ["complete"] = true,
                ["id"] = id, ["state"] = "mist-off", ["volumes"] = volumes.Length });
        }

        private static IEnumerator ClutterOff(ExtensionContext context)
        {
            if (!Id(context, out var id)) yield break;
            if (_active == null || _active.Id != id) { context.Fail("not_owned", "begin this review before changing clutter"); yield break; }
            var system = ClutterSystem.instance;
            if (system == null) { context.Fail("not_ready", "the clutter system is not loaded"); yield break; }
            if (_active.Clutter != null && _active.Clutter != system) { context.Fail("world_changed", "the clutter system changed during review"); yield break; }
            _active.Clutter = system;
            foreach (var entry in system.m_clutter)
            {
                if (!_active.ClutterEntries.ContainsKey(entry)) _active.ClutterEntries.Add(entry, entry.m_enabled);
                entry.m_enabled = false;
            }
            system.ClearAll();
            context.Succeed(new Dictionary<string, object?> { ["source"] = "review-state", ["complete"] = true,
                ["id"] = id, ["state"] = "clutter-off", ["entries"] = system.m_clutter.Count });
        }

        /// <summary>Best effort when an adapter is replaced or the game quits; no later command can use this snapshot.</summary>
        public static void RestoreOnUnload()
        {
            var snapshot = _active;
            _active = null;
            if (snapshot == null) return;
            try { RestoreNow(snapshot); }
            catch { /* A destroyed world has no state left to restore; the owned run's log/teardown reports its loss. */ }
        }

        private static void RestoreNow(Snapshot snapshot)
        {
            if (Player.m_localPlayer != snapshot.Player || EnvMan.instance != snapshot.Environment || GameCamera.instance != snapshot.Camera)
                throw new InvalidOperationException("the player, world environment or camera changed during review");
            if (Members.Field<bool>(snapshot.Camera, "m_freeFly")) snapshot.Camera.ToggleFreeFly();
            if (snapshot.Player.InDebugFlyMode() != snapshot.Fly) snapshot.Player.ToggleDebugFly();
            snapshot.Player.SetGodMode(snapshot.God);
            snapshot.Player.SetGhostMode(snapshot.Ghost);
            Player.m_debugMode = snapshot.Debug;
            var cheats = typeof(Terminal).GetField("m_cheat", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                ?? throw new MissingFieldException(typeof(Terminal).FullName, "m_cheat");
            cheats.SetValue(null, snapshot.Cheats);
            if (Console.instance != null)
                Members.Call(Members.Method(typeof(Console), "updateCommandList"), Console.instance);
            snapshot.Environment.m_debugTimeOfDay = snapshot.DebugTimeEnabled;
            snapshot.Environment.m_debugTime = snapshot.DebugTime;
            snapshot.Environment.m_debugEnv = snapshot.DebugEnvironment;
            foreach (var entry in snapshot.Mist)
                if (entry.Key != null && entry.Key.gameObject != null) entry.Key.gameObject.SetActive(entry.Value);
            if (snapshot.Clutter != null && snapshot.Clutter == ClutterSystem.instance)
            {
                foreach (var entry in snapshot.ClutterEntries) entry.Key.m_enabled = entry.Value;
                snapshot.Clutter.ClearAll();
            }
            // A newly loaded mist volume was never changed by the capture and keeps its own state.
        }

        private static bool Id(ExtensionContext context, out string id)
        {
            id = context.Arguments.Count == 1 ? context.Arguments[0] : "";
            if (id.Length is > 0 and <= 64 && id.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-')) return true;
            context.Fail("usage", "one run id of 1-64 letters, digits or hyphens is required");
            return false;
        }

        private static Dictionary<string, object?> Describe(Snapshot snapshot, string state) => new Dictionary<string, object?>
        {
            ["source"] = "review-state", ["complete"] = true, ["id"] = snapshot.Id, ["state"] = state,
            ["god"] = snapshot.God, ["ghost"] = snapshot.Ghost, ["debug"] = snapshot.Debug,
            ["fly"] = snapshot.Fly, ["cheats"] = snapshot.Cheats,
            ["debugTimeEnabled"] = snapshot.DebugTimeEnabled, ["debugTime"] = snapshot.DebugTime,
            ["debugEnvironment"] = snapshot.DebugEnvironment, ["mistVolumes"] = snapshot.Mist.Count,
        };
    }
}
