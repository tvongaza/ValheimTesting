using System.Globalization;
using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>
/// The one owner of a run's own folder on a host (#791): <c>&lt;runtime&gt;/&lt;runId&gt;</c> in one of its environments' runtime
/// folders, where a dedicated server's profile, world copy and boots, and a campaign client's launch folders, moved-aside log,
/// user-file backup and hosted world live while the run goes. Before anything is written in it, it is journalled (a copy that
/// holds <c>run-folder</c>, retained until fetched) and made, refusing a folder that already exists, so the journal proves the
/// run made it. At teardown, under the host's lock, once no process of the run may still use it and nothing the run journalled
/// inside it is still open, the whole folder is fetched into the output (hash-checked), its receipt journalled, and it is
/// removed through <see cref="HostedRuntimeStage.RetireAsync"/> (<see cref="ProtectedPaths"/>). Otherwise it stays, and
/// <c>env status</c> lists it: open while what it holds is unsettled (<c>env recover</c> removes it after that, once its fetch is
/// journalled or nothing is left in it), journalled kept on request, while a process may still use it, or when its fetch failed
/// (<c>env teardown</c> removes it).
/// </summary>
internal sealed class RunFolder
{
    /// <summary>What a run folder's journalled copy holds.</summary>
    internal const string Holds = "run-folder";
    /// <summary>Where the output keeps each fetched run folder: <c>host-runs/&lt;host&gt;/&lt;runtime folder's name&gt;</c>.</summary>
    internal const string OutputFolder = "host-runs";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Claims _claims;
    private readonly Func<JournalEntry, CancellationToken, Task> _journal;
    private readonly object _state = new();
    private bool _made, _settled;

    /// <param name="journalDirectory">The host's journal, which <paramref name="journal"/> appends to and teardown reads back.</param>
    /// <param name="claims">The run's folders so far, shared by every owner in the run (its server's and its clients'); a new set when null.</param>
    internal RunFolder(IGameHost host, string hostName, string journalDirectory, string runtime, string runId, Func<JournalEntry, CancellationToken, Task> journal,
        Claims? claims = null)
    {
        if (!RunJournal.SafeName(runId)) throw new ArgumentException("A run id is letters, digits, '.', '_' and '-'.", nameof(runId));
        Host = host; HostName = hostName; JournalDirectory = journalDirectory; Runtime = runtime; RunId = runId; _journal = journal;
        Path = HostPath.Join(runtime, runId);
        _claims = claims ?? new Claims();
    }

    /// <summary>
    /// The run folders one run made, by host: a second owner of one path in the run (two roles sharing a runtime on a host, which
    /// <see cref="ResolvedEnvironment.Validate"/> refuses) is refused before it journals anything, so it never touches the first's record.
    /// </summary>
    internal sealed class Claims
    {
        private readonly HashSet<string> _made = new(StringComparer.Ordinal);
        internal bool Add(string key) { lock (_made) return _made.Add(key); }
        internal void Remove(string key) { lock (_made) _made.Remove(key); }
    }

    public IGameHost Host { get; }
    public string HostName { get; }
    public string JournalDirectory { get; }
    /// <summary>The environment's runtime folder that holds the run folder; never removed.</summary>
    public string Runtime { get; }
    public string RunId { get; }
    /// <summary><c>&lt;runtime&gt;/&lt;runId&gt;</c> on the host.</summary>
    public string Path { get; }
    /// <summary>Whether this run made the folder (and so owns it).</summary>
    public bool Made { get { lock (_state) return _made; } }

    /// <summary>
    /// Journals the folder, then makes it, once: before the first write in it, under the host's lock. A folder that already
    /// exists is refused and journalled as not the run's.
    /// </summary>
    internal async Task MakeAsync(CancellationToken cancellation)
    {
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (Made) return;
            string key = HostName.ToUpperInvariant() + "|" + (Host.Shell.Kind == HostShellKind.PowerShell ? Path.Replace('/', '\\').ToUpperInvariant() : Path);
            if (!_claims.Add(key))
                throw new InvalidOperationException($"{Path} on {Host.Name} is already claimed by this run; another role or an earlier unconfirmed reservation may hold it. Give each environment on a host its own runtime folder.");
            try
            {
                await _journal(JournalEntry.Of(JournalEntry.CopyIntended, ("runtime", Path), ("stage", ""), ("parent", Runtime), ("holds", Holds),
                    ("retention", "after-fetch")), cancellation).ConfigureAwait(false);
            }
            catch { _claims.Remove(key); throw; }
            try { await HostedWorldOnHost.ReserveNewAsync(Host, Path, HostedTimeouts.Quick, cancellation).ConfigureAwait(false); }
            catch (HostOperationException error) when (error.Result is { Outcome: HostOutcome.Exited, ExitCode: 4 })
            {
                // Someone else's folder: never the run's to fetch or remove. The line that says so is required, not best-effort: without
                // it the open intent would let env teardown remove that folder, so a failed write fails the run here, before anything else.
                try
                {
                    await _journal(JournalEntry.Of(JournalEntry.CopyRetired, ("runtime", Path), ("notMade", "it already existed")), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception receiptError) when (receiptError is not OperationCanceledException)
                {
                    throw new InvalidOperationException($"{Path} already exists on {Host.Name}; this run did not create or own the folder. " +
                        $"Nothing was written in it, but its notMade refusal receipt could not be recorded. Reservation refusal: {error.Message}", receiptError);
                }
                throw new InvalidOperationException($"{Path} already exists on {Host.Name}; a run never writes into a folder it did not make. Nothing was written in it.", error);
            }
            lock (_state) _made = true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>A new folder in <paramref name="output"/> for this run folder's fetch: <c>host-runs/&lt;host&gt;/&lt;runtime folder's name&gt;</c>.</summary>
    internal string LocalDirectory(string output)
    {
        string name = Runtime.TrimEnd('/', '\\');
        name = name[(name.LastIndexOfAny(['/', '\\']) + 1)..];
        string first = System.IO.Path.Combine(output, OutputFolder, HostName, name.Length == 0 || name.EndsWith(':') ? "runtime" : name);
        string candidate = first;
        for (int n = 2; Directory.Exists(candidate) || File.Exists(candidate); n++) candidate = first + "-" + n.ToString(CultureInfo.InvariantCulture);
        return candidate;
    }

    /// <summary>
    /// The folder's end at teardown, once. With <paramref name="keep"/> (a person asked to keep runtimes, a process may still use
    /// it) it is journalled kept and stays. While anything the run journalled inside it is still open (a copy kept or not yet
    /// retired, user files not restored, a launch whose process was never journalled) it stays open in the journal, for
    /// <c>env recover</c> to settle after what it holds. Otherwise every file in it is fetched into <paramref name="localDirectory"/>,
    /// the receipt journalled, and the folder removed; a failed fetch keeps it, journalled. Each keep is warned about. Throws when the
    /// fetch, receipt or removal fails. Returns why it stays, or null once removed (or never made by this run).
    /// </summary>
    internal async Task<string?> SettleAsync(string localDirectory, string? keep, CancellationToken cancellation)
    {
        lock (_state)
        {
            if (!_made || _settled) return null;
            _settled = true;
        }
        if (keep != null)
        {
            await NoteAsync(JournalEntry.Of(JournalEntry.CopyKept, ("runtime", Path), ("why", keep))).ConfigureAwait(false);
            return Kept(keep);
        }
        if (await OpenInsideAsync(cancellation).ConfigureAwait(false) is { } open) return Kept(open);
        HostListing? listing;
        try { listing = await HostInstall.ListAsync(Host, Path, HostedTimeouts.Long, cancellation: cancellation).ConfigureAwait(false); }
        catch (DirectoryNotFoundException) { listing = null; } // its copies were all retired, and it with them
        int files = listing?.Files.Count ?? 0;
        if (files != 0)
            try { await Host.FetchDirectoryAsync(Path, localDirectory, HostedTimeouts.Long, cancellation).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                const string why = "its fetch failed, so it is the run's only copy of what it holds";
                await NoteAsync(JournalEntry.Of(JournalEntry.CopyKept, ("runtime", Path), ("why", why))).ConfigureAwait(false);
                throw new IOException($"Kept {Host.Name}:{Path}: {why} ({error.Message}). valheim-test env status lists it; env teardown --run {RunId} removes it.", error);
            }
        // The receipt is durable before the removal: a removal whose reply was lost is finished by env recover.
        await _journal(JournalEntry.Of(JournalEntry.CopyDone, ("runtime", Path), ("fetched", "true"),
            ("files", files.ToString(CultureInfo.InvariantCulture))), cancellation).ConfigureAwait(false);
        await HostedRuntimeStage.RetireAsync(Host, Path, "", HostedTimeouts.Quick, cancellation, RunId).ConfigureAwait(false);
        await NoteAsync(JournalEntry.Of(JournalEntry.CopyRetired, ("runtime", Path))).ConfigureAwait(false);
        return null;
    }

    private string Kept(string why)
    {
        Console.Error.WriteLine($"Kept {Host.Name}:{Path}: {why}. valheim-test env status lists it.");
        return why;
    }

    // What the run journalled inside the folder on this host and never settled; null when nothing is, or why it cannot be known.
    private async Task<string?> OpenInsideAsync(CancellationToken cancellation)
    {
        IReadOnlyList<JournalRecord> records;
        try { records = await RunJournalOnHost.ReadAsync(Host, JournalDirectory, RunId, HostedTimeouts.Quick, cancellation).ConfigureAwait(false); }
        catch (Exception error) when (error is HostOperationException or IOException or TimeoutException or InvalidOperationException or FormatException
                                          or System.Text.Json.JsonException)
        { return "its journal could not be read back, so what it holds is not proven settled: " + error.Message; }
        var open = RunJournalStatus.Left(HostName, records).Where(Inside).ToList();
        return open.Count == 0 ? null
            : "the run has not settled what it holds: " + string.Join("; ", open.Select(item => $"{item.Kind} {item.What} ({item.Status})"));
    }

    // A process is the caller's to judge (its stop proof is in memory; a client's stop is not journalled); a lease or lock lives elsewhere.
    private bool Inside(JournalItem item) => item.Kind is "copy" or "user-files" or "launch" or "mac-agent" &&
        new[] { "runtime", "backup", "launchDirectory", "bootDirectory", "keepIn" }.Select(key => item.Fields.GetValueOrDefault(key))
            .Any(path => path != null && Contains(path));

    /// <summary>Whether <paramref name="path"/> lies strictly inside the folder, as the host compares paths.</summary>
    internal bool Contains(string path)
    {
        bool windows = Host.Shell.Kind == HostShellKind.PowerShell;
        string Normal(string value) => (windows ? value.Replace('/', '\\') : value).TrimEnd('/', '\\');
        string folder = Normal(Path), candidate = Normal(path);
        return candidate.Length > folder.Length &&
            candidate.StartsWith(folder + (windows ? "\\" : "/"), windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    // After its effect a lost line is a warning: the entry before it already names the folder.
    private async Task NoteAsync(JournalEntry entry)
    {
        try { await _journal(entry, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error) { Console.Error.WriteLine($"Warning: could not journal {entry.Kind} for {Path} on {HostName}: {error.Message}"); }
    }
}
