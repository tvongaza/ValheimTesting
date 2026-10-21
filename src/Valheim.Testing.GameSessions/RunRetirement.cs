using System.Globalization;
using System.Text;
using System.Text.Json;
using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>
/// The one owner of what a run leaves on disk once its processes have stopped (#257): the clients' disposable characters,
/// then each actor's profile, keeping what the run changed in it, on this machine's host or another. Each owned path is one
/// <see cref="StepPhase.Cleanup"/> step in the report; a failure fails that step and is returned or rethrown to the
/// caller's own step, so the scenario's result stands. A copy whose server may still run stays, and so does every copy when
/// <see cref="PinnedServerRun.KeepRuntimeVariable"/> is <c>1</c>; the report says where. A path already retired is not
/// touched again.
/// </summary>
internal sealed class RunRetirement(ScenarioReport? report, string output)
{
    private readonly HashSet<string> _done = new(StringComparer.Ordinal);
    private readonly HashSet<string> _kept = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unstopped = new(StringComparer.Ordinal);

    /// <summary>Whether a person asked to keep every runtime copy (<see cref="PinnedServerRun.KeepRuntimeVariable"/>=1).</summary>
    public static bool KeepRequested => KeepOverride.Value ?? Environment.GetEnvironmentVariable(PinnedServerRun.KeepRuntimeVariable) == "1";

    /// <summary>Test seam: the variable is process-wide, so a test asks for the keep in its own async flow instead.</summary>
    internal static AsyncLocal<bool?> KeepOverride { get; } = new();

    internal static string KeptOnRequest => $"kept on request ({PinnedServerRun.KeepRuntimeVariable}=1)";

    /// <summary>Whether <paramref name="path"/> on <paramref name="host"/> (an inventory host name; null for this machine) stayed: kept on request or because its server may still run.</summary>
    public bool Kept(string? host, string path) => _kept.Contains(Key(host, path));

    // Host names compare without case, as the inventory's do; paths exactly.
    private static string Key(string? host, string path) => (host?.ToUpperInvariant() ?? "") + ":" + path;

    // The first call for a path does the work; any later one for the same path does nothing.
    private bool First(string key) => _done.Add(key);

    private void Step(string name, Action action)
    {
        if (report != null) report.Step(StepPhase.Cleanup, name, action);
        else action();
    }

    private Task StepAsync(string name, Func<Task> action) =>
        report != null ? report.StepAsync(StepPhase.Cleanup, name, action) : action();

    private void Provenance(string value) { if (report != null) report.Provenance["runtimeCopy"] = value; }

    /// <summary>
    /// A host's runtime copy (<paramref name="runtime"/>, listed as <paramref name="before"/> once it was ready). After a clean
    /// stop the host keeps what the run added or changed in <c>&lt;copy's directory&gt;/runtime-changes</c> within the failure
    /// limits, removes the copy, and the folder is fetched to <c>runtime-changes/</c> here and then dropped on the host. Still
    /// under the host's lock. A retire that fails keeps the copy (and what it kept so far) for inspection. Throws the step's failure.
    /// </summary>
    public Task HostAsync(string hostName, IGameHost host, HostListing before, string runtime, bool launched, bool serverStopped, CancellationToken cancellation = default) =>
        !First(Key(hostName, runtime)) ? Task.CompletedTask :
        StepAsync("remove the server host's runtime copy, keeping what the run changed", () => RetireHostCopyAsync(hostName, host, before, runtime, launched, serverStopped, cancellation));

    private async Task RetireHostCopyAsync(string hostName, IGameHost host, HostListing before, string runtime, bool launched, bool serverStopped, CancellationToken cancellation)
    {
        string where = host.Name + ":" + runtime;
        bool windows = host.Shell.Kind == HostShellKind.PowerShell;
        if (KeepRequested)
        {
            _kept.Add(Key(hostName, runtime));
            Provenance($"kept {where}: {KeptOnRequest}");
            Console.Error.WriteLine($"Kept {where}: {KeptOnRequest}");
            return;
        }
        if (!serverStopped)
        {
            _kept.Add(Key(hostName, runtime));
            _unstopped.Add(Key(hostName, runtime));
            Provenance($"kept {where}: the owned server there may still run; remove the directory once it has stopped");
            Console.Error.WriteLine($"Warning: kept {where}: the owned server there may still run; remove the directory once it has stopped");
            return;
        }
        string copyDirectory = runtime[..runtime.LastIndexOfAny(['/', '\\'])];
        string copyName = copyDirectory[(copyDirectory.LastIndexOfAny(['/', '\\']) + 1)..];
        try
        {
            var listed = before.Files;
            var after = launched ? (await HostInstall.ListAsync(host, runtime, HostedTimeouts.Long, cancellation: cancellation).ConfigureAwait(false)).Files : listed;
            var added = after.Keys.Where(name => !listed.ContainsKey(name)).Order(StringComparer.Ordinal).ToList();
            var changed = after.Where(file => listed.TryGetValue(file.Key, out var hash) && !hash.Equals(file.Value, StringComparison.OrdinalIgnoreCase))
                .Select(file => file.Key).Order(StringComparer.Ordinal).ToList();
            var missing = listed.Keys.Where(name => !after.ContainsKey(name)).Order(StringComparer.Ordinal).ToList();
            // The failure limits: the run's own result is not final yet (its log scan comes after this teardown, which first keeps
            // the logs of any client still open), so keep as much as a failed run would.
            var (perFile, total) = PinnedServerRun.RetainLimits(passed: false);
            string keepDirectory = HostPath.Join(copyDirectory, "runtime-changes");
            var result = (await host.RunAsync(windows ? HostedRunScripts.WindowsRetire : HostedRunScripts.Retire, new Dictionary<string, string>
            {
                ["runtime"] = runtime, ["keep"] = keepDirectory, ["run"] = copyName,
                ["files"] = string.Join('\n', added.Concat(changed).Select(name => Convert.ToBase64String(Encoding.UTF8.GetBytes(name)))),
                ["perfile"] = perFile.ToString(CultureInfo.InvariantCulture), ["total"] = total.ToString(CultureInfo.InvariantCulture),
            }, HostedTimeouts.Long, cancellation).ConfigureAwait(false)).EnsureSuccess($"Removing the runtime copy {where}");
            var done = InteractiveClient.Line(result.Stdout, "VT-RETIRED ")?.Split(' ');
            if (done is not [var freed, var kept]) throw new HostOperationException($"Unexpected reply while removing the runtime copy {where}", result);
            // "VT-NOTKEPT <base64 path> <bytes>", with -1 bytes for anything but a regular file inside the copy.
            var notKept = new List<NotKeptFile>();
            foreach (string line in result.Stdout.Split('\n'))
            {
                if (line.Split(' ') is not ["VT-NOTKEPT", var encoded, var size]) continue;
                string name = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                long bytes = long.Parse(size, CultureInfo.InvariantCulture);
                notKept.Add(bytes < 0 ? new(name, null, null, "not a regular file inside the copy")
                    : new(name, bytes, after.GetValueOrDefault(name), $"past the size limits ({DiskSpace.Format(perFile)} per file, {DiskSpace.Format(total)} in all)"));
            }
            async Task DropKeptAsync(string why) =>
                (await host.RunAsync(windows ? HostedRunScripts.WindowsDropKept : HostedRunScripts.DropKept,
                    new Dictionary<string, string> { ["keep"] = keepDirectory, ["run"] = copyName }, HostedTimeouts.Quick, cancellation).ConfigureAwait(false))
                    .EnsureSuccess($"Removing {host.Name}:{keepDirectory}{why}");
            if (!launched)
            {
                // Nothing ran, so nothing changed: the (empty) keep folder goes too, leaving the copy's directory empty for its owner.
                await DropKeptAsync("").ConfigureAwait(false);
                Provenance($"removed {where} ({DiskSpace.Format(long.Parse(freed, CultureInfo.InvariantCulture))}): nothing was launched");
                return;
            }
            string local = Path.Combine(output, "runtime-changes");
            await host.FetchDirectoryAsync(keepDirectory, local, HostedTimeouts.Long, cancellation).ConfigureAwait(false);
            // Fetched: the host's copy of the changes is not needed twice.
            await DropKeptAsync(" after fetching it").ConfigureAwait(false);
            var retired = new RetiredCopy(where, local, added, changed, missing, notKept, long.Parse(kept, CultureInfo.InvariantCulture), long.Parse(freed, CultureInfo.InvariantCulture));
            File.WriteAllText(Path.Combine(local, "changes.json"), JsonSerializer.Serialize(retired, new JsonSerializerOptions { WriteIndented = true }));
            Provenance(retired.ToString());
        }
        catch (Exception error) when (error is HostOperationException or IOException or InvalidOperationException or FormatException or UnauthorizedAccessException or OperationCanceledException)
        {
            // What the host kept beside the copy may not be fetched yet: nothing else removes the copy's directory now.
            _kept.Add(Key(hostName, runtime));
            Provenance($"cleanup failed, {where} may remain: {error.Message}");
            throw;
        }
    }

    /// <summary>
    /// A campaign's disposable characters, then its prepared installs (a server copy <see cref="HostAsync"/> already retired
    /// leaves only its staging and directory). Each host is checked once for a conflicting or still-running game first. The
    /// run already holds the lock of every host in <paramref name="lockedHosts"/>; any other host's lock is taken for the
    /// retire. A copy kept by <see cref="HostAsync"/> (kept on request, or its server may still run) stays with its staging.
    /// Returns the failures; it never throws.
    /// </summary>
    public async Task<IReadOnlyList<Exception>> CampaignAsync(PreparedHostedCampaign prepared, IReadOnlyCollection<string> lockedHosts,
        CancellationToken cancellation = default, IReadOnlyCollection<string>? blockedHosts = null)
    {
        var failures = new List<Exception>();
        var characters = prepared.Characters.Reverse().Where(item => First("character " + Key(item.Host, item.Character.FileName))).ToList();
        var copies = prepared.Copies.Reverse().Where(copy => First("prepared " + Key(copy.Host, copy.Runtime))).ToList();
        // Characters before copies, as before; a host's lock is taken once and its game processes checked once.
        foreach (string name in characters.Select(item => item.Host).Concat(copies.Select(copy => copy.Host)).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            var hostCharacters = characters.Where(item => item.Host.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            var hostCopies = copies.Where(copy => copy.Host.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            var retired = hostCopies.Where(copy => !Kept(copy.Host, copy.Runtime)).ToList();
            var keptByRun = hostCopies.Where(copy => Kept(copy.Host, copy.Runtime)).ToList();
            // Verification and retention are independent. A requested keep still gets a source verdict once its actor is stopped.
            var sourceCopies = hostCopies.Where(copy => prepared.SourceChecks.ContainsKey(copy.Actor)).ToList();
            var unstoppedSourceCopies = sourceCopies.Where(copy => _unstopped.Contains(Key(copy.Host, copy.Runtime))).ToList();
            var verifiableSourceCopies = sourceCopies.Except(unstoppedSourceCopies).ToList();
            if (KeepRequested && retired.Count != 0)
            {
                foreach (var copy in retired)
                {
                    _kept.Add(Key(copy.Host, copy.Runtime));
                    Step($"keep {copy.Host}:{copy.Runtime}: {KeptOnRequest}", () => { });
                    Console.Error.WriteLine($"Kept {copy.Host}:{copy.Runtime}: {KeptOnRequest}");
                }
                keptByRun.AddRange(retired);
                retired.Clear();
            }
            // The run's journal on this host says what went and what stayed, after each effect. A lost line only makes a later
            // recovery repeat a retire that finds nothing, so it is a warning, never a failed cleanup.
            string journal = prepared.JournalOf(name);
            async Task Journal(IGameHost on, string actor, JournalEntry entry)
            {
                try { await prepared.Journal.AppendAsync(on, journal, actor, entry, prepared.Timeout).ConfigureAwait(false); }
                catch (Exception error) { Console.Error.WriteLine($"Warning: could not journal {entry.Kind} for {actor} on {name}: {error.Message}"); }
            }
            // A kept copy still needs the lock and stop proof for its source verdict.
            if (keptByRun.Count != 0)
            {
                var keptOn = prepared.HostFor(name);
                foreach (var copy in keptByRun)
                    await Journal(keptOn, copy.Actor, JournalEntry.Of(JournalEntry.CopyKept, ("runtime", copy.Runtime),
                        ("why", KeepRequested ? KeptOnRequest : "its server may still run, or its retire failed"))).ConfigureAwait(false);
            }
            foreach (var copy in unstoppedSourceCopies)
                Record(SourceVerdict.Unknown(copy.Actor, "the owned server may still run from its kept copy"), failures: null);
            if (hostCharacters.Count == 0 && retired.Count == 0 && sourceCopies.Count == 0) continue;
            IGameHost host;
            HostLock? claim = null;
            try
            {
                host = prepared.HostFor(name);
                if (blockedHosts?.Contains(name, StringComparer.OrdinalIgnoreCase) == true)
                    throw new IOException("an owned Mac GUI agent was not proven removed; recover its journalled launch before retiring this host's test state");
                if (!lockedHosts.Contains(name, StringComparer.OrdinalIgnoreCase))
                    claim = await host.AcquireLockAsync(prepared.LockOf(name), "campaign-retire " + Guid.NewGuid().ToString("N"), prepared.Timeout, cancellation).ConfigureAwait(false);
                // Any client on a host with characters is a conflict; on a copies-only host only a process running from one of them is.
                await HostedRuntimeStage.RequireStoppedAsync(host, prepared.Timeout, cancellation,
                    runtimes: retired.Concat(verifiableSourceCopies).Select(copy => copy.Runtime).Distinct(StringComparer.Ordinal).ToList(),
                    clientSession: hostCharacters.Count != 0).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                failures.Add(new IOException($"Kept the campaign's characters and prepared installs on {name}: {error.Message}", error));
                if (claim != null)
                    try { await claim.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception release) { failures.Add(release); }
                foreach (var character in hostCharacters)
                {
                    Fail($"retire the disposable character {character.Character.FileName} on {name}", error);
                    _done.Remove("character " + Key(character.Host, character.Character.FileName)); // not attempted: a later retire tries again
                }
                foreach (var copy in verifiableSourceCopies)
                    Record(SourceVerdict.Unknown(copy.Actor, error.Message), failures: null);
                foreach (var copy in retired)
                {
                    Fail($"remove the prepared install {name}:{copy.Runtime}", error);
                    _done.Remove("prepared " + Key(copy.Host, copy.Runtime));
                }
                continue;
            }
            try
            {
                // Each actor's install settled before its copy goes (the copy's parent holds the baseline and the moved files).
                foreach (var copy in verifiableSourceCopies)
                    Record(await prepared.SourceChecks[copy.Actor].SettleAsync(host, SourceSettlement.Run(copy.Actor, copy.Runtime, output),
                        entry => prepared.Journal.AppendAsync(host, journal, copy.Actor, entry, prepared.Timeout), prepared.Timeout, cancellation).ConfigureAwait(false), failures);
                foreach (var character in hostCharacters)
                    if (await Try($"retire the disposable character {character.Character.FileName} on {name}",
                        () => HostedCharacterStage.RetireAsync(host, character.Character, prepared.Timeout, cancellation), failures).ConfigureAwait(false))
                        await Journal(host, character.Actor, JournalEntry.Of(JournalEntry.CharacterRetired, ("fileName", character.Character.FileName))).ConfigureAwait(false);
                foreach (var copy in retired)
                    if (await Try($"remove the prepared install {name}:{copy.Runtime}",
                        () => HostedRuntimeStage.RetireAsync(host, copy.Runtime, copy.Stage, prepared.Timeout, cancellation), failures).ConfigureAwait(false))
                        await Journal(host, copy.Actor, JournalEntry.Of(JournalEntry.CopyRetired, ("runtime", copy.Runtime))).ConfigureAwait(false);
            }
            finally
            {
                if (claim != null)
                    try { await claim.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception error) { failures.Add(error); }
            }
        }
        if (failures.Count == 0) prepared.Retired = true;
        return failures;
    }

    private async Task<bool> Try(string name, Func<Task> action, List<Exception> failures)
    {
        try { await StepAsync(name, action).ConfigureAwait(false); return true; }
        catch (Exception error) { failures.Add(new IOException($"Failed to {name}", error)); return false; }
    }

    private void Fail(string name, Exception error)
    {
        try { Step(name, () => throw new IOException("Not attempted: " + error.Message, error)); }
        catch (IOException) { } // Recorded as the failed step; the failure itself is returned once per host.
    }

    // The verdict's own step (SourceVerdict.Record); a failure goes to failures, when the caller returns them for this host. An unknown
    // verdict follows from a stop or host failure that is reported already.
    private void Record(SourceVerdict verdict, List<Exception>? failures)
    {
        try { verdict.Record(report); }
        catch (IOException error) { failures?.Add(error); }
    }
}
