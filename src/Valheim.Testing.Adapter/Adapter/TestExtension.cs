// Source from the Valheim.Testing.Adapter package: compiled into a mod's game-side test adapter (a BepInEx plugin that
// references ValheimCLI and the game). Not part of the mod itself; install the adapter only in test runtimes.
#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using valheimCLI;
using valheimCLI.Extensions;

namespace Valheim.Testing.Adapter
{
    /// <summary>
    /// Registers a mod's test extension with ValheimCLI once ValheimCLI's extension API is ready, together with the
    /// <c>session</c> capability that <c>OwnedServerSession</c> uses to prove it started this very server: the token from
    /// the runner's environment variable, this process's ID, the save root, whether it is dedicated, the raw devcommands
    /// flag, whether it accepts game connections yet, and whether the world and the mod are ready. Run it as a coroutine
    /// from the adapter plugin's <c>Start</c>:
    /// <code>
    /// private IEnumerator Start() => TestExtension.Register("mymod.testing", "0.1.0", "MYMOD_TEST_SESSION_TOKEN",
    ///     () => MyMod.Ready, registration => _registration = registration, Logger.LogError, extraCommands);
    /// </code>
    /// and dispose the registration in <c>OnDestroy</c>.
    /// </summary>
    public static class TestExtension
    {
        /// <summary>How long to wait for ValheimCLI's extension API before giving up; the adapter then stays inert.</summary>
        public static float ApiWaitSeconds = 30f;

        public static IEnumerator Register(string id, string version, string tokenVariable, Func<bool> modReady,
            Action<ExtensionRegistration> registered, Action<string> logError, params ExtensionCommand[] commands)
        {
            if (string.IsNullOrEmpty(tokenVariable)) throw new ArgumentException("Name the session token variable.", nameof(tokenVariable));
            float deadline = Time.realtimeSinceStartup + ApiWaitSeconds;
            while (valheimCLIPlugin.Instance == null || valheimCLIPlugin.Instance!.Extensions == null)
            {
                if (Time.realtimeSinceStartup > deadline) { logError("ValheimCLI's extension API did not become ready; " + id + " disabled."); yield break; }
                yield return null;
            }
            var session = new ExtensionCommand("session", "Read owned test process and save-root identity", context => Session(context, tokenVariable, modReady), readOnly: true);
            // The loop above ended because both are set.
            registered(valheimCLIPlugin.Instance!.Extensions!.Register(id, version, 1, new[] { session }.Concat(commands).ToArray()));
        }

        private static IEnumerator Session(ExtensionContext context, string tokenVariable, Func<bool> modReady)
        {
            if (context.Arguments.Count != 0) { context.Fail("usage", "session takes no arguments"); yield break; }
            var net = ZNet.instance;
            using (var process = System.Diagnostics.Process.GetCurrentProcess())
                context.Succeed(new Dictionary<string, object?>
                {
                    ["source"] = "owned-test-session", ["token"] = Environment.GetEnvironmentVariable(tokenVariable) ?? "",
                    ["pid"] = process.Id, ["saveRoot"] = Utils.GetSaveDataPath(FileHelpers.FileSource.Local),
                    ["dedicated"] = net != null && net.IsDedicated(),
                    ["devcommands"] = DevcommandsFlag(),
                    // A dedicated server opens its game socket only when world generation finishes, on a first boot well after
                    // the world has loaded; a join before that times out. Reported apart from complete, so a runner can do
                    // other work meanwhile and wait for it just before the first join (OwnedServerSession.WaitUntilJoinable).
                    ["acceptingConnections"] = AcceptingConnections(),
                    ["complete"] = net != null && net.IsServer() && ZoneSystem.instance != null && ZDOMan.instance != null && modReady(),
                });
        }

        /// <summary>
        /// Whether this server has opened its game socket (<c>ZNet.OpenServer</c>, logged as "Opened Steam server" or "Opened
        /// PlayFab server"), so a client can join. The field is private in the shipped game; a missing field fails loudly.
        /// </summary>
        public static bool AcceptingConnections() => ZNet.instance != null && Members.Field<object?>(ZNet.instance, "m_hostSocket") != null;

        /// <summary>
        /// The raw devcommands flag that ValheimCLI's extension gate reads. <c>IsCheatsEnabled</c> is not it: another mod can
        /// make that true on a dedicated server without enabling mutating extensions.
        /// </summary>
        public static bool DevcommandsFlag() => global::Console.instance != null && Members.StaticField<bool>(typeof(Terminal), "m_cheat");
    }
}
