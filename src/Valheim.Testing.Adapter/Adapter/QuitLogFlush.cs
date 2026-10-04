// Source from the Valheim.Testing.Adapter package: compiled into a mod's game-side test adapter (a BepInEx plugin that
// references ValheimCLI and the game). Not part of the mod itself; install the adapter only in test runtimes.
#nullable enable
using System;
using System.Linq;
using BepInEx.Logging;
using UnityEngine;

namespace Valheim.Testing.Adapter
{
    /// <summary>
    /// Makes the lines plugins log while the game quits reach BepInEx's disk log (<c>LogOutput.log</c>), so the runner's
    /// teardown log scan can read them. BepInEx 5.4's <c>DiskLogListener</c> writes through a buffered writer that a 2-second
    /// timer flushes, and nothing flushes it when the game quits: on the Valheim 1.0.16 Windows dedicated server a boot that
    /// quit cleanly sometimes kept its plugins' last lines and sometimes lost them.
    /// <para>
    /// <see cref="Enable"/> adds one log listener after BepInEx's own, and does nothing else until the game starts quitting.
    /// The first quit signal arms it: <c>Application.quitting</c>, the adapter's own <c>OnApplicationQuit</c> (call
    /// <see cref="Quitting"/> there), or <c>AppDomain.ProcessExit</c>/<c>DomainUnload</c>. Arming flushes every disk log
    /// once, which saves the lines logged so far; from then on the listener flushes after each line any plugin logs, which
    /// saves the rest. A line is lost only if the process stops running managed code before any of those signals, or if it
    /// is logged after the last managed code runs. Arming logs one info line naming the signal that armed it.
    /// </para>
    /// <para>
    /// Enable it in the test adapter's <c>Awake</c> and call <see cref="Disable"/> from its <c>OnDestroy</c>; production
    /// mods never need it. It changes no BepInEx configuration, removes no listener but its own, changes no log level and
    /// never touches Harmony. Before the game quits, logging is buffered exactly as without it.
    /// </para>
    /// <para>
    /// The state is process-wide, not per load: a ScriptEngine reload loads the adapter again with fresh statics, and
    /// two mods' adapters each compile this source, while BepInEx's listener list lives on. So the process keeps one
    /// listener whichever load added it, a quit signal seen by any load arms the flush for all, and the listener is
    /// removed only when the last enabled load calls <see cref="Disable"/> (a load reloaded without it counts as still
    /// enabled, so the flush stays: the safe direction).
    /// </para>
    /// </summary>
    public static class QuitLogFlush
    {
        // AppDomain data is shared by every load of this class; its statics are not.
        private const string ArmedKey = "Valheim.Testing.Adapter.QuitLogFlush.ArmedBy";
        private const string UsersKey = "Valheim.Testing.Adapter.QuitLogFlush.Users";
        private static bool _enabled;
        private static ManualLogSource? _log;
        private static readonly Action OnQuitting = () => Quitting("Application.quitting");
        private static readonly EventHandler OnProcessExit = (_, _) => Quitting("AppDomain.ProcessExit");
        private static readonly EventHandler OnDomainUnload = (_, _) => Quitting("AppDomain.DomainUnload");

        /// <summary>Whether this load of the adapter has enabled the flush (and not disabled it).</summary>
        public static bool Enabled => _enabled;

        /// <summary>The quit signal that armed the flush (seen by any load in this process), or null while the game is not quitting.</summary>
        public static string? ArmedBy => AppDomain.CurrentDomain.GetData(ArmedKey) as string;

        /// <summary>
        /// Subscribes this load to the quit signals and makes sure the process has the one listener. Idempotent, also
        /// across script reloads and several adapters.
        /// </summary>
        public static void Enable()
        {
            if (_enabled) return;
            _enabled = true;
            AppDomain.CurrentDomain.SetData(UsersKey, Users + 1);
            _log ??= BepInEx.Logging.Logger.CreateLogSource("ValheimTesting");
            if (!Listeners().Any()) BepInEx.Logging.Logger.Listeners.Add(new FlushListener());
            Application.quitting += OnQuitting;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            AppDomain.CurrentDomain.DomainUnload += OnDomainUnload;
        }

        /// <summary>
        /// Unsubscribes this load, for the adapter's <c>OnDestroy</c> before a script reload; the last enabled load also
        /// removes the listener. Does nothing once the game is quitting (<see cref="ArmedBy"/> set): plugins are destroyed
        /// at quit too, and the lines they log then are the ones this flush exists to save.
        /// </summary>
        public static void Disable()
        {
            if (!_enabled || ArmedBy != null) return;
            _enabled = false;
            Application.quitting -= OnQuitting;
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
            AppDomain.CurrentDomain.DomainUnload -= OnDomainUnload;
            int users = Math.Max(0, Users - 1);
            AppDomain.CurrentDomain.SetData(UsersKey, users);
            if (users == 0) foreach (var listener in Listeners().ToArray()) BepInEx.Logging.Logger.Listeners.Remove(listener);
        }

        /// <summary>
        /// Arms the flush (once; later calls flush again). Call it from the adapter's <c>OnApplicationQuit</c>, naming the
        /// caller. Not from <c>OnDestroy</c>: a script reload destroys the plugin without quitting.
        /// </summary>
        public static void Quitting(string signal)
        {
            if (!_enabled) return;
            if (ArmedBy == null)
            {
                AppDomain.CurrentDomain.SetData(ArmedKey, signal);
                try { _log?.LogInfo("Quit log flush armed by " + signal + "."); } catch (Exception) { }
            }
            Flush();
        }

        private static int Users => AppDomain.CurrentDomain.GetData(UsersKey) is int n ? n : 0;

        // Every load's listener has this full type name, whichever adapter assembly compiled it.
        private static System.Collections.Generic.IEnumerable<ILogListener> Listeners() =>
            BepInEx.Logging.Logger.Listeners.Where(l => l.GetType().FullName == typeof(FlushListener).FullName);

        /// <summary>Flushes every BepInEx disk log now.</summary>
        public static void Flush()
        {
            foreach (var disk in BepInEx.Logging.Logger.Listeners.OfType<DiskLogListener>().ToArray())
            {
                try { disk.LogWriter?.Flush(); }
                catch (ObjectDisposedException) { }
            }
        }

        private sealed class FlushListener : ILogListener
        {
            // BepInEx sends each line to its listeners in the order they were added, so this runs after the disk
            // listener has written the line.
            public void LogEvent(object sender, LogEventArgs eventArgs)
            {
                if (ArmedBy != null) Flush();
            }

            public void Dispose() { }
        }
    }
}
