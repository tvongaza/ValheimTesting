using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>Runs <see cref="UserSaveFiles"/>' steps on a host, through its shell.</summary>
internal static class UserSaveFilesOnHost
{
    /// <summary>The host's runner for <see cref="UserSaveFiles"/>; each step is bounded by <paramref name="timeout"/>.</summary>
    internal static UserSaveFiles.Remote Remote(IGameHost host, TimeSpan timeout) =>
        new(host.Shell.Kind == HostShellKind.PowerShell, async (script, variables) =>
            (await host.RunAsync(script, variables, timeout).ConfigureAwait(false)).EnsureSuccess($"Keeping the user's Valheim files as found on {host.Name}").Stdout);

    /// <summary>
    /// Where a host's captures keep their owner markers: this machine's (<see cref="UserSaveFiles.LocalMarkers"/>) for a local host,
    /// so every run here meets the same ones, else <c>user-files</c> beside the host's journal (its lock's folder).
    /// </summary>
    internal static string Markers(IGameHost host, HostProfile profile)
    {
        if (host.Kind == GameHostKind.Local) return UserSaveFiles.LocalMarkers;
        string journal = RunJournal.DirectoryFor(profile);
        return HostPath.Join(journal[..journal.LastIndexOfAny(['/', '\\'])], "user-files");
    }

    /// <summary>
    /// The guard of a game client started on <paramref name="host"/>: its recent-servers list in the host user's data folder,
    /// backed up into <paramref name="runDirectory"/> (the run's folder there) and journalled through <paramref name="journal"/>.
    /// </summary>
    internal static UserSaveFilesGuard ForClient(IGameHost host, HostProfile profile, string runDirectory, string runId, string label,
        Func<JournalEntry, Task> journal, Func<JournalEntry, Task> note, TimeSpan timeout, CancellationToken cancellation) =>
        new(async () =>
        {
            string root = await HostedWorldOnHost.ClientSaveRootAsync(host, timeout, cancellation).ConfigureAwait(false);
            return await UserSaveFiles.CaptureAsync(UserSaveFiles.Kind.ClientServerList, root,
                HostPath.Join(runDirectory, UserSaveFiles.BackupName(runId, label)), Markers(host, profile),
                Remote(host, timeout)).ConfigureAwait(false);
        }, journal, note);
}
