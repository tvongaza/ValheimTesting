using System.Globalization;
using System.Text.Json;
using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>
/// A hosted fixture world on a campaign client's own host (#258 step 8b, part b), this machine included (#807): placed into that
/// host user's <c>worlds_local</c> before the client hosts it, and moved out again into the run's directory on that host (then
/// fetched into the evidence) once no client can still host it. The one placement owner of a hosting client's fixture world (#829):
/// a campaign's, <c>start</c>'s, a session's and rounds a caller drives. Only entries named for the world are ever touched, by one
/// rule (<see cref="HostedWorld.IsNamedFor"/>: <c>&lt;name&gt;</c>, <c>&lt;name&gt;.*</c>, <c>&lt;name&gt;_backup*</c>, in any case),
/// which the refusal, the move-out and recovery's presence check share (<see cref="BashNamed"/>, <see cref="WindowsNamed"/>); the
/// placement refuses when one exists, so a user's world is never overwritten or used. The placement is journalled on the host before the ship (a
/// <see cref="JournalEntry.CopyIntended"/> that <c>holds</c> <c>hosted-world</c>), so <c>env recover</c> moves a world a killed run
/// left behind out of the user's worlds in the same way (<see cref="RunRecovery"/>), only after a transfer was confirmed and
/// the world there states the journalled world UID (<see cref="RequireOwnWorldAsync"/>). Intent alone is never ownership.
/// </summary>
internal static class HostedWorldOnHost
{
    internal const string Holds = "hosted-world";

    /// <summary>The game's user-data root on an SSH Mac, from that host's own home rather than the runner's home.</summary>
    internal static async Task<string> MacSaveRootAsync(IGameHost host, TimeSpan timeout, CancellationToken cancellation)
    {
        var result = (await host.RunAsync(MacHome, null, timeout, cancellation).ConfigureAwait(false)).EnsureSuccess($"Finding the Mac save root on {host.Name}");
        string home = InteractiveClient.Line(result.Stdout, "VT-MAC-HOME ")
            ?? throw new HostOperationException($"The Mac on {host.Name} did not name its home directory", result);
        if (!home.StartsWith('/') || home.Any(char.IsControl) || home.Split('/').Any(part => part is "." or ".."))
            throw new InvalidDataException($"The Mac on {host.Name} reported an unsafe home directory.");
        return HostPath.Join(home, HostedWorld.MacSaveRelative);
    }

    /// <summary>
    /// A game client's user-data root on its host (where the game keeps its recent-servers list, whatever the launch asks): on a
    /// Mac (bash) <see cref="MacSaveRootAsync"/>, on Windows (PowerShell) the host user's <c>AppData\LocalLow\IronGate\Valheim</c>.
    /// </summary>
    internal static async Task<string> ClientSaveRootAsync(IGameHost host, TimeSpan timeout, CancellationToken cancellation)
    {
        if (host.Shell.Kind != HostShellKind.PowerShell) return await MacSaveRootAsync(host, timeout, cancellation).ConfigureAwait(false);
        var result = (await host.RunAsync(WindowsProfile, null, timeout, cancellation).ConfigureAwait(false)).EnsureSuccess($"Finding the client save root on {host.Name}");
        string profile = InteractiveClient.Line(result.Stdout, "VT-WINDOWS-PROFILE ")
            ?? throw new HostOperationException($"{host.Name} did not name its user profile", result);
        if (!System.Text.RegularExpressions.Regex.IsMatch(profile, @"^[A-Za-z]:\\") || profile.Any(char.IsControl) || profile.Split('\\', '/').Any(part => part is "." or ".."))
            throw new InvalidDataException($"{host.Name} reported an unsafe user profile.");
        return HostPath.Join(profile, HostedWorld.WindowsSaveRelative);
    }

    internal static readonly string MacHome = """
        set -eu
        [ "$(uname -s)" = Darwin ] && [ -d "$HOME" ] || exit 4
        printf 'VT-MAC-HOME %s\n' "$HOME"
        """.ReplaceLineEndings("\n");

    internal static readonly string WindowsProfile = """
        $profileDirectory = [Environment]::GetFolderPath('UserProfile')
        if (-not $profileDirectory -or -not [IO.Directory]::Exists($profileDirectory)) { exit 4 }
        'VT-WINDOWS-PROFILE ' + $profileDirectory
        """.ReplaceLineEndings("\n");

    /// <summary>
    /// Where a placed world goes on its host (<paramref name="WorldsDirectory"/>), the run's folder it is shipped to first
    /// (<paramref name="Stage"/>) and moved out to after (<paramref name="KeepIn"/>), and how its moves are journalled.
    /// </summary>
    /// <param name="Hold">Before anything in the host's worlds is read or changed: the host's lock is this run's, and no game runs there.</param>
    internal sealed record Site(IGameHost Host, string HostName, string WorldsDirectory, string Stage, string KeepIn,
        Func<JournalEntry, CancellationToken, Task> Journal, Func<CancellationToken, Task> Hold) : IHostedWorldSite
    {
        // A run that owns its run folder on the host (a dedicated server's, a campaign client's: RunFolder) owns the staging and
        // collected directories in it until their verified fetch, then removes them. Null for a site in an output on this machine.
        internal string? RetireRunCopies { get; init; }
        public HostedWorld Place(HostWorldPlan plan, string output, CancellationToken cancellation) =>
            HostedWorldOnHost.Place(this, plan, output, cancellation);
    }

    /// <summary>
    /// Where a campaign's hosting client <paramref name="client"/> places its world: its host user's own <c>worlds_local</c>, beside
    /// the <c>characters_local</c> its character was staged in, shipped through and moved out into the run's folder in its role's
    /// runtime there. The same on every host, this machine included: a local hosting client's world is journalled as a remote one's
    /// is, so <c>env status</c> lists it and <c>env recover</c> moves it out after a killed run (#807).
    /// </summary>
    internal static Site ForCampaignClient(ResolvedEnvironment environment, string client, string runId, HostedCampaignCharacter character,
        IGameHost clientHost, Func<JournalEntry, CancellationToken, Task> journal, Func<CancellationToken, Task> hold)
    {
        var role = environment.Clients[client];
        return ForClient(clientHost, role.Host, WorldsBeside(character.CharactersLocalDirectory), role.Runtime, runId, journal, hold) with { RetireRunCopies = runId };
    }

    /// <summary>
    /// Where any hosting client's world goes: <paramref name="worldsDirectory"/> on <paramref name="host"/>, shipped through and
    /// moved out into the run's own folder under <paramref name="root"/> there (<c>&lt;root&gt;/&lt;runId&gt;/host-world</c>, the
    /// folder <c>env recover</c> requires): a campaign client's role runtime (<see cref="ForCampaignClient"/>), or the arm's evidence
    /// for <c>start</c>'s client on this machine (<c>TargetedRegression</c>, #818), or the output of a session's or a caller's rounds
    /// on this machine (<see cref="ThisMachine"/>). All are placed and journalled the same way.
    /// </summary>
    internal static Site ForClient(IGameHost host, string hostName, string worldsDirectory, string root, string runId,
        Func<JournalEntry, CancellationToken, Task> journal, Func<CancellationToken, Task> hold) =>
        new(host, hostName, worldsDirectory, HostPath.Join(root, runId, "host-world-stage"), HostPath.Join(root, runId, "host-world"), journal, hold);

    /// <summary>
    /// Where a hosting client on this machine places its world when its runner has no site of its own (a session's
    /// <see cref="HostingClientActor"/>, or rounds a caller drives with <see cref="HostedClientRounds.Run"/>): the checked
    /// <c>worlds_local</c> here, shipped through and moved out into <c>&lt;output&gt;/&lt;runId&gt;/host-world</c> (a later placement of
    /// the same run into the same output: <c>&lt;output&gt;/placement-&lt;n&gt;/&lt;runId&gt;/host-world</c>), journalled in this
    /// machine's journal under the run the flow journals when it places the world (<see cref="RunJournal.ThisProcess"/>) as <paramref name="actor"/>, as the
    /// fixture's own copy is. <c>env status</c> lists a world a killed run left and <c>env recover --run</c> moves it out.
    /// </summary>
    internal static Func<string, IHostedWorldSite> ThisMachine(string output, string actor)
    {
        var host = LocalGameHost.ThisMachine();
        return worlds =>
        {
            var journal = RunJournal.ThisProcess; // The run this flow journals as it places the world, for every line of the placement.
            // Each placement its own folder (#829 review): another hosted run into the same output, in the same run, takes the next
            // free root (<output>, then <output>/placement-2 and so on), so <root>/<runId>/host-world is never one an earlier run made.
            string root = output;
            for (int n = 2; ; n++)
            {
                var reservation = ReserveStorage(host, "", Path.Combine(root, journal.RunId), CancellationToken.None);
                if (reservation.Failed && reservation.ExitCode == 4) { root = Path.Combine(output, $"placement-{n}"); continue; }
                RequireReservation(reservation, host.Name);
                break;
            }
            return ForClient(host, host.Name, worlds, root, journal.RunId,
                (entry, _) => { journal.AppendLocal(actor, entry); return Task.CompletedTask; },
                _ => Task.CompletedTask); // No lock: the caller owns this machine's client, as it owns the client it opens.
        };
    }

    /// <summary>
    /// The client's <c>worlds_local</c> on its host, beside the <c>characters_local</c> the campaign staged its character in
    /// (both are the host user's Valheim data directory's).
    /// </summary>
    internal static string WorldsBeside(string charactersLocal)
    {
        string path = charactersLocal.TrimEnd('/', '\\');
        int at = path.LastIndexOfAny(['/', '\\']);
        if (at <= 0 || !path[(at + 1)..].Equals("characters_local", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"The client's characters folder {charactersLocal} is not a Valheim characters_local, so its worlds_local is unknown.", nameof(charactersLocal));
        return HostPath.Join(path[..at], "worlds_local");
    }

    /// <summary>
    /// Refuses when the host's worlds already hold an entry named for the world, before anything is copied (a refused run leaves
    /// nothing, #820), then copies <paramref name="plan"/>'s fixture into <paramref name="output"/> (verified, the input evidence,
    /// kept), journals the placement, ships the fixture into the run's stage folder on the host
    /// and verifies every file there, then moves its entries (all named for the world) into the host's worlds, refusing one that
    /// appeared meanwhile.
    /// </summary>
    internal static HostedWorld Place(Site site, HostWorldPlan plan, string output, CancellationToken cancellation)
    {
        // The pinned manifest names the world; the copy below refuses any other set of files.
        string name = HostedWorld.NameOf(plan.World.Sha256.Keys);
        string world = HostPath.Join(site.WorldsDirectory, name);
        site.Hold(cancellation).GetAwaiter().GetResult();
        var existing = Entries(site.Host, site.WorldsDirectory, name, cancellation);
        if (existing.Count != 0)
            throw new InvalidOperationException($"The client's worlds on {site.HostName} already hold {string.Join(", ", existing)} in {site.WorldsDirectory}: a fixture world is never copied over a world. Remove or rename that one yourself, or rename the fixture world.");
        Reserve(site, cancellation);
        var copy = plan.Admission is { } admission
            ? WorldFixture.CopyAdmitted(plan.World.Source, output, plan.World.Sha256, admission, "")
            : WorldFixture.Copy(plan.World.Source, output, plan.World.Sha256);
        copy.Preserve = true;
        try
        {
            if (plan.Admission is { } admitted)
            {
                plan.RequireUid(admitted.Identity);
            }
            else WorldFixture.Verify(plan.World.Source, plan.World.Sha256);

            // Journalled before the ship: no journal line, no world in the user's worlds.
            site.Journal(JournalEntry.Of(JournalEntry.CopyIntended, ("runtime", world), ("stage", ""), ("parent", site.WorldsDirectory), ("holds", Holds),
                ("world", name), ("worldUid", plan.WorldUid), ("keepIn", site.KeepIn), ("placementState", "intended")), cancellation).GetAwaiter().GetResult();
            if (site.RetireRunCopies != null)
            {
                string parent = HostedRuntimeStage.RequirePrepared(site.Host, site.Stage, site.RetireRunCopies);
                HostedRuntimeStage.RequirePrepared(site.Host, site.KeepIn, site.RetireRunCopies);
                site.Journal(JournalEntry.Of(JournalEntry.CopyIntended, ("runtime", site.Stage), ("stage", ""),
                    ("parent", parent), ("holds", "world-stage")), cancellation).GetAwaiter().GetResult();
                site.Journal(JournalEntry.Of(JournalEntry.CopyIntended, ("runtime", site.KeepIn), ("stage", ""),
                    ("parent", parent), ("holds", "world"), ("retention", "after-fetch")), cancellation).GetAwaiter().GetResult();
            }
            // Shipped into the run's stage first (with the ship's own SOURCE.txt, which never reaches the user's worlds), verified there.
            site.Host.ShipFilesAsync(copy.DirectoryPath, site.Stage, HostedTimeouts.Long, cancellation).GetAwaiter().GetResult();
            var listing = HostInstall.ListAsync(site.Host, site.Stage, HostedTimeouts.Long, null, cancellation).GetAwaiter().GetResult();
            HostInstall.RequireSame(copy.SourceHashes, listing, "hosted world", ["SOURCE.txt", WorldFixture.ProvenanceFile]);
            var expected = copy.SourceHashes.Keys.Select(path => path.Split('/', '\\')[0]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            IReadOnlyList<string> proven = [];
            try
            {
                // Intent/attempt is never ownership, even when a foreign world has the fixture's UID or identical bytes.
                site.Journal(JournalEntry.Of(JournalEntry.CopyIntended, ("runtime", world), ("placementState", "attempted")), cancellation).GetAwaiter().GetResult();
                var result = site.Host.RunAsync(site.Host.Shell.Kind == HostShellKind.PowerShell ? WindowsPlace : BashPlace,
                    new Dictionary<string, string> { ["worlds"] = site.Stage, ["name"] = name, ["to"] = site.WorldsDirectory },
                    HostedTimeouts.Long, cancellation).GetAwaiter().GetResult();
                // Only an exited script's well-formed receipts prove individual transfers. A lost reply can still mean a
                // running mutation: leave the pending journal and all files for inspection, never guess from UID or hashes.
                if (result.Outcome == HostOutcome.Exited) proven = ProvenTransfers(result, expected);
                result.EnsureSuccess($"Placing the hosted world on {site.HostName}");
                site.Journal(JournalEntry.Of(JournalEntry.CopyDone, ("runtime", world), ("files", listing.Files.Count.ToString(CultureInfo.InvariantCulture)),
                    ("verified", "true"), ("placementState", "transferred")), cancellation).GetAwaiter().GetResult();
            }
            catch
            {
                if (proven.Count != 0)
                {
                    try
                    {
                        // Preserve the proof in the same journal if cleanup itself fails or this process dies.
                        site.Journal(JournalEntry.Of(JournalEntry.CopyIntended, ("runtime", world),
                            ("placementState", proven.Count == expected.Count ? "partial" : "uncertain"),
                            ("transferredEntries", JsonSerializer.Serialize(proven))), CancellationToken.None).GetAwaiter().GetResult();
                        // The same owner retires only the entries this transfer proved. Foreign siblings remain untouched.
                        MoveOut(site.Host, site.WorldsDirectory, name, site.KeepIn, HostedTimeouts.Long, CancellationToken.None, proven).GetAwaiter().GetResult();
                        if (proven.Count == expected.Count)
                            site.Journal(JournalEntry.Of(JournalEntry.CopyRetired, ("runtime", world), ("handedOver", "true"), ("keptIn", site.KeepIn)), CancellationToken.None).GetAwaiter().GetResult();
                        else Console.Error.WriteLine($"Warning: only the confirmed hosted-world entries on {site.HostName} were rolled back; the remaining transfer is uncertain and its placement stays pending for inspection.");
                    }
                    catch (Exception cleanup)
                    {
                        Console.Error.WriteLine($"Warning: hosted-world rollback on {site.HostName} is incomplete; keep the pending placement and inspect it before env recover ({cleanup.GetType().Name}).");
                    }
                }
                else Console.Error.WriteLine($"Warning: hosted-world transfer on {site.HostName} was not proven; the pending placement requires inspection before env recover.");
                throw;
            }
            return HostedWorld.OnHost(name, plan.WorldUid, site.WorldsDirectory, copy.DirectoryPath, output, site.HostName, target => Collect(site, name, target));
        }
        finally { copy.Dispose(); } // kept (Preserve) as input evidence; nothing uses it after placement
    }

    private static IReadOnlyList<string> ProvenTransfers(HostResult result, IReadOnlyList<string> expected)
    {
        var moved = result.Stdout.Split('\n').Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith("VT-WORLD-MOVED ", StringComparison.Ordinal)).Select(line => line["VT-WORLD-MOVED ".Length..]).ToList();
        if ((result.Succeeded && (InteractiveClient.Line(result.Stdout, "VT-WORLD-MOVE ") != "done" ||
                !moved.Order(StringComparer.Ordinal).SequenceEqual(expected, StringComparer.Ordinal))) ||
            result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Any(line =>
                !line.StartsWith("VT-WORLD-MOVED ", StringComparison.Ordinal) && !line.StartsWith("VT-WORLD-EXISTS ", StringComparison.Ordinal) && line.TrimEnd('\r') != "VT-WORLD-MOVE done") ||
            moved.Distinct(StringComparer.Ordinal).Count() != moved.Count || moved.Any(leaf => !expected.Contains(leaf, StringComparer.Ordinal)))
            throw new HostOperationException("Malformed hosted-world transfer receipt; preserve the pending placement for inspection", result);
        return moved;
    }

    private static void Reserve(Site site, CancellationToken cancellation) =>
        RequireReservation(ReserveStorage(site.Host, site.Stage, site.KeepIn, cancellation), site.HostName);

    private static HostResult ReserveStorage(IGameHost host, string stage, string keep, CancellationToken cancellation) =>
        host.RunAsync(host.Shell.Kind == HostShellKind.PowerShell ? WindowsReserve : BashReserve,
            new Dictionary<string, string> { ["stage"] = stage, ["keep"] = keep }, HostedTimeouts.Quick, cancellation).GetAwaiter().GetResult();

    private static void RequireReservation(HostResult result, string host, string what = "hosted-world storage", string why = "each placement needs new staging and collection directories")
    {
        result.EnsureSuccess($"Reserving {what} on {host}; {why}");
        if (InteractiveClient.Line(result.Stdout, "VT-WORLD-RESERVED ") != "done")
            throw new HostOperationException($"The reservation of {what} on {host} was not confirmed", result);
    }

    /// <summary>
    /// Makes <paramref name="directory"/> on <paramref name="host"/> with the same atomic reservation a placement's collection
    /// directory gets, refusing one that already exists: a folder only the caller made (a run's own folder, <see cref="RunFolder"/>).
    /// With <paramref name="unlinkedRoot"/> (a toolkit-made parent, such as a recovery's run folder), a link at or below that root is
    /// refused too. A run's own folder passes none: its parent is the environment's runtime folder, which may itself be a link.
    /// </summary>
    internal static async Task ReserveNewAsync(IGameHost host, string directory, TimeSpan timeout, CancellationToken cancellation, string? unlinkedRoot = null)
    {
        var result = await host.RunAsync(host.Shell.Kind == HostShellKind.PowerShell ? WindowsReserve : BashReserve,
            new Dictionary<string, string> { ["stage"] = "", ["keep"] = directory, ["root"] = unlinkedRoot ?? "" }, timeout, cancellation).ConfigureAwait(false);
        RequireReservation(result, host.Name, directory, "a run makes its own folder, and never reuses one that already exists");
    }

    // Recovery shares this owner's placement decision. A UID describes a world; it never proves a transfer occurred.
    internal static async Task<IReadOnlyList<string>> RecoverAsync(IGameHost host, IReadOnlyDictionary<string, string> fields,
        string runtime, string runId, TimeSpan timeout, CancellationToken cancellation)
    {
        var (name, uid) = RequireJournalled(fields, runtime, runId);
        string? placement = fields.GetValueOrDefault("placementState");
        if (placement is "attempted" or "uncertain")
            throw new InvalidDataException("the hosted-world transfer was never confirmed and its host script may still run; preserve the pending placement even if no world is present. Inspect the host and settle the interrupted transfer before retrying env recover");
        if (placement == "partial")
        {
            var selected = JsonSerializer.Deserialize<string[]>(fields.GetValueOrDefault("transferredEntries") ?? "[]") ?? [];
            if (selected.Length == 0 || selected.Distinct(StringComparer.Ordinal).Count() != selected.Length ||
                selected.Any(leaf => leaf.Length == 0 || leaf.IndexOfAny(['/', '\\', '\n', '\r']) >= 0 || !HostedWorld.IsNamedFor(leaf, name)))
                throw new InvalidDataException("the partial placement has no valid transfer receipts; preserve the pending placement for inspection");
            // An old receipt proves the earlier move, not ownership of a file later written at that path. Partial setup
            // never launched the game, so there is no game-modified-save path to preserve here. Only settle missing entries.
            var remaining = Entries(host, fields["parent"], name, cancellation);
            if (remaining.Any(leaf => selected.Contains(leaf, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidDataException("the partial hosted-world transfer still has entries in the client's worlds; its historical receipts cannot prove those files are still the run's. Preserve the pending placement and inspect them before retrying env recover");
            return [];
        }
        bool present = await RequireOwnWorldAsync(host, fields["parent"], name, uid, timeout, cancellation).ConfigureAwait(false);
        if (!present) return [];
        if (fields.GetValueOrDefault("verified") != "true" ||
            (fields.TryGetValue("placementState", out string? state) && state != "transferred"))
            throw new InvalidDataException("the hosted-world transfer was never confirmed; a same-named save may be personal even with the fixture UID. Inspect it and the pending journal, move foreign files aside for safekeeping, then retry env recover");
        return await MoveOut(host, fields["parent"], name, fields["keepIn"], timeout, cancellation).ConfigureAwait(false);
    }

    // Reserve the existing collection directory atomically; shipping still gets a new, absent stage directory. Refusal is
    // permanent for this site, so repeated callers cannot share its storage even when the first shipment has not started.
    internal static readonly string BashReserve = ProtectedPaths.BashUnlinked + """
        set -eu
        [ -z "${root:-}" ] || vt_unlinked "$root" "$keep" || exit 4
        [ ! -e "$stage" ] && [ ! -L "$stage" ] || exit 4
        mkdir -p -- "${keep%/*}"
        if ! mkdir -- "$keep"; then
            if [ -e "$keep" ] || [ -L "$keep" ]; then exit 4; fi
            exit 5
        fi
        echo 'VT-WORLD-RESERVED done'
        """;
    internal static readonly string WindowsReserve = ProtectedPaths.PowerShellUnlinked + """
        $ErrorActionPreference = 'Stop'
        if ($root -and -not (Test-VtUnlinked $root $keep)) { exit 4 }
        if ($stage -and (Test-Path -LiteralPath $stage)) { exit 4 }
        $parent = [IO.Path]::GetDirectoryName($keep)
        [void][IO.Directory]::CreateDirectory($parent)
        $candidate = Join-Path $parent ('.placement-' + [Guid]::NewGuid().ToString('N'))
        [void][IO.Directory]::CreateDirectory($candidate)
        try { [IO.Directory]::Move($candidate, $keep) }
        catch {
            [IO.Directory]::Delete($candidate)
            if (Test-Path -LiteralPath $keep) { exit 4 }
            throw
        }
        'VT-WORLD-RESERVED done'
        """;

    // Moves every entry named for the world out of the user's worlds into the run's directory on the host (the run's save, handed
    // over there), then fetches that into the evidence. Returns where the evidence is.
    private static string Collect(Site site, string name, string target)
    {
        string world = HostPath.Join(site.WorldsDirectory, name);
        MoveOut(site.Host, site.WorldsDirectory, name, site.KeepIn, HostedTimeouts.Long, CancellationToken.None).GetAwaiter().GetResult();
        site.Journal(JournalEntry.Of(JournalEntry.CopyRetired, ("runtime", world), ("handedOver", "true"), ("keptIn", site.KeepIn)), CancellationToken.None).GetAwaiter().GetResult();
        site.Host.FetchDirectoryAsync(site.KeepIn, target, HostedTimeouts.Long).GetAwaiter().GetResult();
        if (site.RetireRunCopies != null)
        {
            site.Journal(JournalEntry.Of(JournalEntry.CopyDone, ("runtime", site.KeepIn), ("fetched", "true")), CancellationToken.None).GetAwaiter().GetResult();
            // The stage holds only the input fixture and shipment metadata, never a game save.
            HostedRuntimeStage.RetireAsync(site.Host, site.Stage, "", HostedTimeouts.Quick, runId: site.RetireRunCopies).GetAwaiter().GetResult();
            site.Journal(JournalEntry.Of(JournalEntry.CopyRetired, ("runtime", site.Stage)), CancellationToken.None).GetAwaiter().GetResult();
            HostedRuntimeStage.RetireAsync(site.Host, site.KeepIn, "", HostedTimeouts.Quick, runId: site.RetireRunCopies).GetAwaiter().GetResult();
            site.Journal(JournalEntry.Of(JournalEntry.CopyRetired, ("runtime", site.KeepIn)), CancellationToken.None).GetAwaiter().GetResult();
        }
        return target;
    }

    private static List<string> Entries(IGameHost host, string worlds, string name, CancellationToken cancellation)
    {
        var result = host.RunAsync(host.Shell.Kind == HostShellKind.PowerShell ? WindowsEntries : BashEntries,
            new Dictionary<string, string> { ["worlds"] = worlds, ["name"] = name }, HostedTimeouts.Quick, cancellation).GetAwaiter().GetResult()
            .EnsureSuccess($"Listing the client's worlds on {host.Name}");
        if (InteractiveClient.Line(result.Stdout, "VT-WORLD-ENTRIES ") != "done") throw new HostOperationException($"Unexpected reply while listing the client's worlds on {host.Name}", result);
        return result.Stdout.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.StartsWith("VT-WORLD-ENTRY ", StringComparison.Ordinal))
            .Select(line => line["VT-WORLD-ENTRY ".Length..]).ToList();
    }

    /// <summary>Moves every entry named for <paramref name="name"/> in <paramref name="worlds"/> into <paramref name="keepIn"/> (created); returns the moved entries.</summary>
    internal static async Task<IReadOnlyList<string>> MoveOut(IGameHost host, string worlds, string name, string keepIn, TimeSpan timeout, CancellationToken cancellation, IReadOnlyList<string>? selected = null)
    {
        var result = (await host.RunAsync(host.Shell.Kind == HostShellKind.PowerShell ? WindowsMoveOut : BashMoveOut,
            new Dictionary<string, string> { ["worlds"] = worlds, ["name"] = name, ["to"] = keepIn, ["selected"] = selected == null ? "" : string.Join("\n", selected) }, timeout, cancellation).ConfigureAwait(false))
            .EnsureSuccess($"Moving the hosted world out of the client's worlds on {host.Name}");
        if (InteractiveClient.Line(result.Stdout, "VT-WORLD-MOVE") == null) throw new HostOperationException($"Unexpected reply while moving the hosted world on {host.Name}", result);
        return result.Stdout.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.StartsWith("VT-WORLD-MOVED ", StringComparison.Ordinal))
            .Select(line => line["VT-WORLD-MOVED ".Length..]).ToList();
    }

    /// <summary>
    /// What env recover requires of a journalled hosted world before it moves anything: the folder is a <c>worlds_local</c>, the
    /// world name is a fixture world's, the copy is that folder's entry of that name, and the folder it moves to is in this run's
    /// directory, and the world UID placed is named. Returns the world's name and UID.
    /// </summary>
    internal static (string Name, string WorldUid) RequireJournalled(IReadOnlyDictionary<string, string> fields, string runtime, string runId)
    {
        string worlds = fields.GetValueOrDefault("parent") ?? "", name = fields.GetValueOrDefault("world") ?? "", keepIn = fields.GetValueOrDefault("keepIn") ?? "";
        string uid = fields.GetValueOrDefault("worldUid") ?? "";
        if (!long.TryParse(uid, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
            throw new InvalidDataException("its journal names no world UID, so the world in the client's worlds cannot be proven the run's own");
        string Leaf(string path) => path.Replace('\\', '/').TrimEnd('/').Split('/').Last();
        if (!Leaf(worlds).Equals("worlds_local", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"its journal names {worlds}, which is not a client's worlds_local");
        _ = WorldIdentity.RequireName(name); // a fixture world's name
        string separator = worlds.Contains('\\') ? "\\" : "/";
        if (!runtime.Equals(worlds.TrimEnd('/', '\\') + separator + name, StringComparison.Ordinal))
            throw new InvalidDataException($"its journal names {runtime}, which is not {name} in {worlds}");
        // <runtime>/<runId>/host-world, with no relative segments: the run's own folder on that host.
        var segments = keepIn.Replace('\\', '/').TrimEnd('/').Split('/');
        if (!RunJournal.SafeName(runId) || segments.Length < 3 || segments[^1] != "host-world" || segments[^2] != runId || segments.Any(segment => segment is "." or ".."))
            throw new InvalidDataException($"its journal names {keepIn}, which is not this run's host-world folder (<runtime>/{runId}/host-world)");
        return (name, uid);
    }

    /// <summary>
    /// Before env recover moves a journalled world out of a client's worlds: every metadata file of the world there
    /// (<c>&lt;name&gt;/_main.&lt;n&gt;.fwl2</c> in Valheim 1.0's save folder, and in the game's backups of it) must state
    /// <paramref name="worldUid"/>, the fixture's UID the run journalled before placing it. A world the user made under that name has
    /// another UID and is refused, as is one whose identity cannot be read (entries named for the world but no readable metadata, or a
    /// file named for it, which only a pre-1.0 world has).
    /// The game rewrites the files once it hosted the world, so the UID, not the bytes, is what stays the run's. Returns false when
    /// nothing named for the world is there.
    /// </summary>
    internal static async Task<bool> RequireOwnWorldAsync(IGameHost host, string worlds, string name, string worldUid, TimeSpan timeout, CancellationToken cancellation)
    {
        var result = (await host.RunAsync(host.Shell.Kind == HostShellKind.PowerShell ? WindowsMetadata : BashMetadata,
            new Dictionary<string, string> { ["worlds"] = worlds, ["name"] = name }, timeout, cancellation).ConfigureAwait(false))
            .EnsureSuccess($"Reading the hosted world's identity in the client's worlds on {host.Name}");
        string? done = InteractiveClient.Line(result.Stdout, "VT-WORLD-METAS ");
        if (done is not ("present" or "absent")) throw new HostOperationException($"Unexpected reply while reading the hosted world's identity on {host.Name}", result);
        var lines = result.Stdout.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        if (lines.FirstOrDefault(line => line.StartsWith("VT-WORLD-META-LARGE ", StringComparison.Ordinal)) is { } large)
            throw new InvalidDataException($"{large["VT-WORLD-META-LARGE ".Length..]} in {worlds} is too large to be world metadata, so the world there cannot be proven the run's");
        // A file named for the world (a pre-1.0 <name>.fwl or .db a user put there): no run placed it and Valheim 1.0 writes none,
        // so nothing proves it the run's, and the move-out would take it.
        if (lines.FirstOrDefault(line => line.StartsWith("VT-WORLD-FILE ", StringComparison.Ordinal)) is { } file)
            throw new InvalidDataException($"{file["VT-WORLD-FILE ".Length..]} in {worlds} is a file named for the world {name}, which no run places and Valheim 1.0 does not write (a pre-1.0 world's?), so it cannot be proven the run's; inspect it and move it out yourself");
        var read = lines.Where(line => line.StartsWith("VT-WORLD-META ", StringComparison.Ordinal)).Select(line =>
        {
            string[] parts = line["VT-WORLD-META ".Length..].Split(' ');
            if (parts.Length != 2) throw new HostOperationException($"Unexpected world metadata line from {host.Name}", result);
            return WorldIdentity.Parse(Convert.FromBase64String(parts[1]), parts[0]);
        }).ToList();
        if (read.Count == 0)
        {
            if (done == "absent") return false;
            throw new InvalidDataException($"{name} in {worlds} holds no world metadata ({name}/_main.<n>.fwl2), so it cannot be proven the run's world UID {worldUid}; inspect it and move it out yourself");
        }
        var other = read.FirstOrDefault(identity => identity.UidText != worldUid);
        if (other != null)
            throw new InvalidDataException($"{other.File} in {worlds} states world {other.Name} with UID {other.UidText}, not the fixture's {worldUid} the run placed: a world of the same name that is not the run's stays where it is");
        return true;
    }

    // The one rule of what is named for a world on a host (HostedWorld.IsNamedFor): <name>, <name>.* and the game's <name>_backup*,
    // in any case. A hosting client runs on Windows or macOS, whose folders ignore case, so the game takes campaign for
    // Campaign: the refusal must see it, and a move-out may take it, because the refusal proved nothing named for the world was
    // there before the placement. Failed placement rollback uses individual transfer receipts instead. Lower-casing is culture-free
    // (C locale, invariant culture). Each script below uses it.
    internal const string WindowsNamed = """
        $lower = $name.ToLowerInvariant()
        function Test-VtNamed([string]$leaf) {
            $l = $leaf.ToLowerInvariant()
            return ($l -ceq $lower -or $l.StartsWith($lower + '.', [StringComparison]::Ordinal) -or $l.StartsWith($lower + '_backup', [StringComparison]::Ordinal))
        }

        """;
    internal const string BashNamed = """
        lower=$(printf '%s' "$name" | LC_ALL=C tr '[:upper:]' '[:lower:]')
        vt_named() {
            l=$(printf '%s' "$1" | LC_ALL=C tr '[:upper:]' '[:lower:]')
            case "$l" in "$lower"|"$lower".*|"$lower"_backup*) return 0 ;; esac
            return 1
        }

        """;

    // The metadata of every entry named for the world (the one rule, any case, as MoveOut moves them), base64: the _main.<n>.fwl2
    // files of an <entry>/ folder (Valheim 1.0's save, or the game's backup of it), each labelled with its path in worlds_local. An
    // entry that is not a folder is named (VT-WORLD-FILE): neither a run nor Valheim 1.0 writes one for a world.
    // VT-WORLD-METAS says whether any entry named for the world is there at all. Metadata is a few hundred bytes; a file over 1 MiB
    // is named, not read.
    internal static readonly string WindowsMetadata = "$ErrorActionPreference = 'Stop'\n" + WindowsNamed + """
        $present = $false; $files = @()
        if ([IO.Directory]::Exists($worlds)) {
            foreach ($entry in @([IO.Directory]::EnumerateFileSystemEntries($worlds))) {
                $leaf = [IO.Path]::GetFileName($entry)
                if (-not (Test-VtNamed $leaf)) { continue }
                $present = $true
                if ([IO.Directory]::Exists($entry)) {
                    foreach ($file in @([IO.Directory]::EnumerateFiles($entry))) {
                        $inner = [IO.Path]::GetFileName($file)
                        if ($inner -match '^_main\.[0-9]+\.fwl2$') { $files += ,@($file, ($leaf + '/' + $inner)) }
                    }
                }
                else { 'VT-WORLD-FILE ' + $leaf }
            }
            foreach ($file in $files) {
                if ((Get-Item -LiteralPath $file[0]).Length -gt 1048576) { 'VT-WORLD-META-LARGE ' + $file[1]; continue }
                'VT-WORLD-META ' + $file[1] + ' ' + [Convert]::ToBase64String([IO.File]::ReadAllBytes($file[0]))
            }
        }
        if ($present) { 'VT-WORLD-METAS present' } else { 'VT-WORLD-METAS absent' }
        """;
    internal static readonly string BashMetadata = "set -eu\n" + BashNamed + """
        present=absent
        meta() {
            size=$(wc -c < "$1" | tr -d ' ')
            if [ "$size" -gt 1048576 ]; then printf 'VT-WORLD-META-LARGE %s\n' "$2"; return; fi
            printf 'VT-WORLD-META %s %s\n' "$2" "$(base64 < "$1" | tr -d '\n\r')"
        }
        if [ -d "$worlds" ]; then
            for entry in "$worlds"/* "$worlds"/.[!.]*; do
                [ -e "$entry" ] || [ -L "$entry" ] || continue
                leaf=${entry##*/}
                vt_named "$leaf" || continue
                present=present
                if [ -d "$entry" ]; then
                    for file in "$entry"/_main.*.fwl2; do
                        [ -f "$file" ] || continue
                        inner=${file##*/}
                        case "$inner" in _main.*[!0-9]*.fwl2|_main..fwl2) continue ;; esac
                        meta "$file" "$leaf/$inner"
                    done
                else
                    printf 'VT-WORLD-FILE %s\n' "$leaf"
                fi
            done
        fi
        printf 'VT-WORLD-METAS %s\n' "$present"
        """;

    // Entries named for the world: the placement refuses any of them, so a user's world is never used. worlds_local is created
    // when missing.
    internal static readonly string WindowsEntries = "$ErrorActionPreference = 'Stop'\n" + WindowsNamed + """
        if (-not [IO.Directory]::Exists($worlds)) { [void][IO.Directory]::CreateDirectory($worlds) }
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($worlds)) {
            $leaf = [IO.Path]::GetFileName($entry)
            if (Test-VtNamed $leaf) { 'VT-WORLD-ENTRY ' + $leaf }
        }
        'VT-WORLD-ENTRIES done'
        """;
    internal static readonly string BashEntries = "set -eu\n" + BashNamed + """
        mkdir -p -- "$worlds"
        for entry in "$worlds"/* "$worlds"/.[!.]*; do
            [ -e "$entry" ] || [ -L "$entry" ] || continue
            leaf=${entry##*/}
            if vt_named "$leaf"; then printf 'VT-WORLD-ENTRY %s\n' "$leaf"; fi
        done
        echo 'VT-WORLD-ENTRIES done'
        """;
    // Moves the world's own entries (the same rule) from $worlds into $to (created). Every destination is checked first, and none
    // may exist: nothing is ever overwritten, merged or deleted. Across volumes (Windows) an entry is copied, then removed from where it was.
    internal static readonly string WindowsMoveOut = ProtectedPaths.PowerShellInside + ProtectedPaths.PowerShellRemove + "$ErrorActionPreference = 'Stop'\n" + WindowsNamed + """
        [void][IO.Directory]::CreateDirectory($to)
        $moves = @()
        if ([IO.Directory]::Exists($worlds)) {
            foreach ($entry in @([IO.Directory]::EnumerateFileSystemEntries($worlds))) {
                $leaf = [IO.Path]::GetFileName($entry)
                if (-not (Test-VtNamed $leaf)) { continue }
                if ($selected -and $leaf -cnotin @($selected.Split("`n"))) { continue }
                $dest = Join-Path $to $leaf
                if ([IO.File]::Exists($dest) -or [IO.Directory]::Exists($dest)) { 'VT-WORLD-EXISTS ' + $leaf; exit 4 }
                $moves += ,@($entry, $dest, $leaf)
            }
        }
        foreach ($move in $moves) {
            $entry = $move[0]; $dest = $move[1]
            if ([IO.Path]::GetPathRoot($entry) -ne [IO.Path]::GetPathRoot($dest)) {
                # Build a complete copy on the destination volume, then publish at the exact path with no replacement.
                # CreateDirectoryW's true result proves this temporary directory is new; CreateDirectory alone is idempotent.
                if (-not ('VtHostedWorldCopy' -as [type])) {
                    Add-Type -TypeDefinition @'
        using System;
        using System.Runtime.InteropServices;
        public static class VtHostedWorldCopy {
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool CreateDirectory(string path, IntPtr attributes);
        }
        '@
                }
                $temporary = Join-Path $to ('.vt-world-copy-' + [Guid]::NewGuid().ToString('N'))
                if (-not [VtHostedWorldCopy]::CreateDirectory($temporary, [IntPtr]::Zero)) {
                    throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())
                }
                try {
                    $copy = Join-Path $temporary 'entry'
                    Copy-Item -LiteralPath $entry -Destination $copy -Recurse
                    if ([IO.Directory]::Exists($copy)) { [IO.Directory]::Move($copy, $dest) }
                    else { [IO.File]::Move($copy, $dest) }
                    Remove-VtTree $entry $worlds
                } finally { Remove-VtTree $temporary $to }
            }
            elseif ([IO.Directory]::Exists($entry)) { [IO.Directory]::Move($entry, $dest) }
            else { [IO.File]::Move($entry, $dest) }
            'VT-WORLD-MOVED ' + $move[2]
        }
        'VT-WORLD-MOVE done'
        """;
    internal static readonly string BashMoveOut = "set -eu\n" + BashNamed + """
        mkdir -p -- "$to"
        moves=""
        if [ -d "$worlds" ]; then
            for entry in "$worlds"/* "$worlds"/.[!.]*; do
                [ -e "$entry" ] || [ -L "$entry" ] || continue
                leaf=${entry##*/}
                vt_named "$leaf" || continue
                if [ -n "${selected-}" ]; then
                    found=false
                    while IFS= read -r choice; do [ "$choice" != "$leaf" ] || found=true; done <<EOF
        $selected
        EOF
                    [ "$found" = true ] || continue
                fi
                if [ -e "$to/$leaf" ] || [ -L "$to/$leaf" ]; then printf 'VT-WORLD-EXISTS %s\n' "$leaf"; exit 4; fi
                moves="$moves$leaf
        "
            done
        fi
        printf '%s' "$moves" | while IFS= read -r leaf; do
            [ -n "$leaf" ] || continue
            # The destination is the parent, never an explicit child path that mv could treat as a newly arrived directory.
            mv -n -- "$worlds/$leaf" "$to/"
            if [ -e "$worlds/$leaf" ] || [ -L "$worlds/$leaf" ]; then printf 'VT-WORLD-EXISTS %s\n' "$leaf"; exit 4; fi
            printf 'VT-WORLD-MOVED %s\n' "$leaf"
        done
        echo 'VT-WORLD-MOVE done'
        """;
    internal static readonly string WindowsPlace = WindowsMoveOut.Replace("[void][IO.Directory]::CreateDirectory($to)", """
        [void][IO.Directory]::CreateDirectory($to)
        foreach ($existing in [IO.Directory]::EnumerateFileSystemEntries($to)) {
            if (Test-VtNamed ([IO.Path]::GetFileName($existing))) { 'VT-WORLD-EXISTS ' + [IO.Path]::GetFileName($existing); exit 4 }
        }
        """);
    internal static readonly string BashPlace = BashMoveOut.Replace("mkdir -p -- \"$to\"", """
        mkdir -p -- "$to"
        for existing in "$to"/* "$to"/.[!.]*; do
            [ -e "$existing" ] || [ -L "$existing" ] || continue
            leaf=${existing##*/}
            if vt_named "$leaf"; then printf 'VT-WORLD-EXISTS %s\n' "$leaf"; exit 4; fi
        done
        """);

}
