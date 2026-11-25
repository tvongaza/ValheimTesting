using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>
/// The client's own save folders a run held at the main menu checks (<c>start --menu-only</c>, #1009): <c>characters_local</c> and
/// <c>worlds_local</c> in the client's save directory, settled by the one source-check owner (<see cref="ProfileSourceCheck"/>) as a
/// profile's install is; this type only gives that owner a journalled run-owned folder. Before the launch the folders' listing is
/// persisted beside an empty run-owned <c>vt-prep-*/runtime</c>, journalled as a client copy, so <c>valheim-test env status</c> lists
/// it and <c>env recover</c> settles the folders if the run is killed. Once the client is stopped, every file the game added there (a
/// character a command made at the menu, with its backups) is moved into the run's evidence (<c>source-changes/client-saves/added/</c>)
/// and each folder it made there is removed once empty. A file that was there before is never moved: one that changed or went missing
/// fails the run, and nothing is moved.
/// </summary>
internal sealed class ClientSaveFolders
{
    /// <summary>The verdict's actor: <c>sourceIntegrity.client-saves</c>, and the evidence in <c>source-changes/client-saves/</c>.</summary>
    internal const string Actor = "client-saves";
    internal static readonly IReadOnlyList<string> Folders = ["characters_local", "worlds_local"];

    private readonly IGameHost _host;
    private readonly string _runtime;
    private readonly ProfileSourceCheck _check;
    private readonly Action<JournalEntry> _journal;
    private readonly TimeSpan _timeout;

    private ClientSaveFolders(IGameHost host, string runtime, ProfileSourceCheck check, Action<JournalEntry> journal, TimeSpan timeout)
    { _host = host; _runtime = runtime; _check = check; _journal = journal; _timeout = timeout; }

    /// <summary>
    /// Journals the run-owned folder <paramref name="parent"/> (a <c>vt-prep-*</c> folder outside the save directory) as a client
    /// copy, lists <see cref="Folders"/> under <paramref name="saveRoot"/> and persists that baseline there. A failure before the
    /// baseline is persisted removes the folder again: nothing has launched, so nothing is left to settle.
    /// </summary>
    internal static ClientSaveFolders Capture(IGameHost host, string saveRoot, string parent, Action<JournalEntry> journal, TimeSpan timeout)
    {
        string runtime = Path.Combine(parent, "runtime");
        journal(JournalEntry.Of(JournalEntry.CopyIntended, ("runtime", runtime), ("stage", ""), ("parent", parent), ("runtimeKind", "client")));
        try
        {
            Directory.CreateDirectory(runtime);
            ProfileSourceCheck check;
            try { check = ProfileSourceCheck.PrepareFoldersAsync(host, saveRoot, Folders, timeout).GetAwaiter().GetResult(); }
            catch (DirectoryNotFoundException missing)
            {
                throw new DirectoryNotFoundException($"The client's save directory {saveRoot} is not there: start Valheim once on this machine, so it makes it, then run again.", missing);
            }
            journal(JournalEntry.Of(JournalEntry.CopyDone, ("runtime", runtime), ("source", saveRoot), ("runtimeKind", "client")));
            check.PersistAsync(host, runtime, Actor, Journal(journal), timeout).GetAwaiter().GetResult();
            return new ClientSaveFolders(host, runtime, check, journal, timeout);
        }
        catch
        {
            HostedRuntimeStage.RetireAsync(host, runtime, "", timeout).GetAwaiter().GetResult();
            journal(JournalEntry.Of(JournalEntry.CopyRetired, ("runtime", runtime), ("failed", "true")));
            throw;
        }
    }

    /// <summary>
    /// The one settlement, once the client is proven stopped: the owner's verdict, naming every file it moved and where, and every
    /// one it left with the reason (a move that fails part way included).
    /// </summary>
    internal SourceVerdict Settle(string output) =>
        _check.SettleAsync(_host, SourceSettlement.Run(Actor, _runtime, output), Journal(_journal), _timeout).GetAwaiter().GetResult();

    /// <summary>
    /// Removes the run-owned folder once <paramref name="verdict"/> settled. One that did not (a stop not proven, a settlement that
    /// threw or did not finish) keeps it with its journalled baseline, so <c>env status</c> names it and <c>env recover</c> settles the
    /// folders again; false then.
    /// </summary>
    internal bool Retire(SourceVerdict verdict)
    {
        if (verdict.KeepsProfile) return false;
        HostedRuntimeStage.RetireAsync(_host, _runtime, "", _timeout).GetAwaiter().GetResult();
        _journal(JournalEntry.Of(JournalEntry.CopyRetired, ("runtime", _runtime)));
        return true;
    }

    private static Func<JournalEntry, Task> Journal(Action<JournalEntry> journal) => entry => { journal(entry); return Task.CompletedTask; };
}
