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
    /// Enable it in the test adapter's <c>Awake</c>; production mods never need it. It changes no BepInEx configuration,
    /// removes no listener, changes no log level and never touches Harmony. Before the game quits, logging is buffered
    /// exactly as without it.
    /// </para>
    /// </summary>
    public static class QuitLogFlush
    {
        private static FlushListener? _listener;
        private static ManualLogSource? _log;

        /// <summary>Whether <see cref="Enable"/> has run in this process.</summary>
        public static bool Enabled => _listener != null;

        /// <summary>The quit signal that armed the flush, or null while the game is not quitting.</summary>
        public static string? ArmedBy { get; private set; }

        /// <summary>Adds the listener and subscribes to the quit signals. Idempotent.</summary>
        public static void Enable()
        {
            if (_listener != null) return;
            _log = BepInEx.Logging.Logger.CreateLogSource("ValheimTesting");
            _listener = new FlushListener();
            BepInEx.Logging.Logger.Listeners.Add(_listener);
            Application.quitting += () => Quitting("Application.quitting");
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Quitting("AppDomain.ProcessExit");
            AppDomain.CurrentDomain.DomainUnload += (_, _) => Quitting("AppDomain.DomainUnload");
        }

        /// <summary>
        /// Arms the flush (once; later calls flush again). Call it from the adapter's <c>OnApplicationQuit</c>, naming the
        /// caller. Not from <c>OnDestroy</c>: a script reload destroys the plugin without quitting.
        /// </summary>
        public static void Quitting(string signal)
        {
            if (_listener == null) return;
            if (ArmedBy == null)
            {
                ArmedBy = signal;
                try { _log?.LogInfo("Quit log flush armed by " + signal + "."); } catch (Exception) { }
            }
            Flush();
        }

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
