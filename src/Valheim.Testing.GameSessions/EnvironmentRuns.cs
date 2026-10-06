using System.Text.Json;

namespace Valheim.Testing.Game;

/// <summary>Why a named campaign actor was assigned to one inventory recipe.</summary>
internal sealed record EnvironmentAssignment(string Actor, string Environment, string Role, string Host, string Reason);

/// <summary>The resolved environment a run's host lifecycle uses, and its reviewable assignments.</summary>
internal sealed record ResolvedEnvironmentInventory(ResolvedEnvironment Environment,
    IReadOnlyList<EnvironmentAssignment> Assignments)
{
    /// <summary>Loader selected for each actor without changing the caller's campaign declaration.</summary>
    public IReadOnlyDictionary<string, string?> LoaderPackages { get; init; } = new Dictionary<string, string?>();
}

/// <summary>
/// What an <see cref="EnvironmentInventory"/> does across its hosts (<c>valheim-test env status|recover|teardown</c> and a bare
/// <c>env preflight</c>'s journal check): reads each host's run journal, recovers or tears down what one run left, releases the
/// leases of a run whose machine is gone, removes one unjournalled copy. Also where a session's actors and a standalone run's
/// server are placed on the inventory's environments. The inventory itself only describes the hosts and environments.
/// </summary>
public static class EnvironmentRuns
{
    /// <summary>
    /// Writes what earlier runs left on each host of the inventory at <paramref name="inventoryPath"/> (this machine when null),
    /// from each host's run journal (<c>journal</c> beside the host's lock): every run that has not ended, or left copies,
    /// disposable characters, processes, Steam leases or host locks it never journalled as gone, each checked on its host.
    /// Only the hosts are needed, not an install. Changes nothing. Returns true when every host was read and every run left
    /// nothing. <paramref name="json"/> writes the report as JSON instead of text (without the <c>detected:</c> lines).
    /// </summary>
    public static async Task<bool> WriteRunStatusAsync(string? inventoryPath, TextWriter output, bool json = false, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        var inventory = EnvironmentInventory.ReadHosts(inventoryPath, EnvironmentInventory.ThisMachine);
        if (!json) foreach (string line in inventory.Detected) output.WriteLine("detected: " + line);
        var report = await StatusAsync(inventory, cancellation).ConfigureAwait(false);
        RunJournalStatus.Write(report, output, json);
        return report.Clean;
    }

    /// <summary>
    /// Clears what run <paramref name="runId"/> left on the hosts of the inventory at <paramref name="inventoryPath"/> (this
    /// machine when null), as <see cref="WriteRunStatusAsync"/> judged it, and writes what was done. A run that is still going,
    /// whose runner cannot be checked, or that left anything that cannot be proven its own is refused before anything changes.
    /// A process is stopped only when its ID, start time and command line still all match the run's journal. Copies and leases
    /// the run kept on purpose stay unless <paramref name="teardown"/>; a world copy, a run's save, is handed over to the output
    /// it is in, never removed by a run's recovery. Returns true when nothing of the run is left.
    /// </summary>
    public static async Task<bool> RecoverRunAsync(string? inventoryPath, string runId, bool teardown, TextWriter output, bool json = false,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        var inventory = EnvironmentInventory.ReadHosts(inventoryPath, EnvironmentInventory.ThisMachine);
        if (!json) foreach (string line in inventory.Detected) output.WriteLine("detected: " + line);
        var (hosts, _) = JournalScope(inventory);
        var report = await RunRecovery.RecoverAsync(hosts, new ResolvedEnvironment { Hosts = hosts }.CreateHost, runId, teardown,
            TimeSpan.FromSeconds(60), cancellation, inventory.LeaseHost, inventory.LeaseDirectory).ConfigureAwait(false);
        RunRecovery.Write(report, output, json);
        return report.Recovered;
    }

    /// <summary>
    /// The escape hatch for a run whose machine is gone for good (<c>valheim-test env teardown --run ID --machine-gone</c>): a
    /// lease never lapses (#257), so a run that can no longer be recovered would otherwise hold its Steam accounts forever (as
    /// would a claim whose reply was lost, which no journal names). On the inventory's lease host, it releases every unreleased
    /// lease that names <paramref name="runId"/> and journals each release there as the maintainer's (<c>machineGone</c>). The
    /// flag is the maintainer's confirmation that no client of that run can still run; a client that does is not told. When it
    /// released a lease of a run no readable journal had ended (unknown, or journalled only on the gone machine), the run is
    /// journalled there as ended too, so <c>env status</c> and preflight stop waiting for it; nothing else of the run is touched.
    /// Refused when the run is still going, when the lease host's journal cannot be read, and when every host was read and
    /// <c>env recover|teardown --run</c> can settle the run (its clients can be proven stopped). Returns true when nothing the
    /// run held is left unreleased.
    /// </summary>
    public static async Task<bool> ReleaseLeasesOfGoneRunAsync(string? inventoryPath, string runId, TextWriter output, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!RunJournal.SafeName(runId) || runId == "-") throw new ArgumentException("A run id is letters, digits, '.', '_' and '-'.", nameof(runId));
        var inventory = EnvironmentInventory.ReadHosts(inventoryPath, EnvironmentInventory.ThisMachine);
        foreach (string line in inventory.Detected) output.WriteLine("detected: " + line);
        if (!inventory.Hosts.TryGetValue(inventory.LeaseHost ?? "", out var leaseProfile) || string.IsNullOrWhiteSpace(inventory.LeaseDirectory))
            throw new ArgumentException("The inventory names no leaseHost and leaseDirectory, so there is no lease to release.");
        var (hosts, _) = JournalScope(inventory);
        var factory = new ResolvedEnvironment { Hosts = hosts }.CreateHost;
        var timeout = TimeSpan.FromSeconds(60);
        var status = await RunJournalStatus.InspectAsync(hosts, factory, timeout, cancellation, inventory.LeaseHost, inventory.LeaseDirectory).ConfigureAwait(false);
        var known = status.Runs.SingleOrDefault(run => run.Run == runId);
        var unread = status.Hosts.Where(host => host.Error != null).Select(host => host.Name).ToList();
        string? refused = unread.Contains(inventory.LeaseHost!) ? $"the lease host {inventory.LeaseHost}'s journal cannot be read, so whether the run is going is unknown"
            : known?.State switch
            {
                JournalRunState.Live => "it is still going: " + known.Reason,
                // With every host read, recovery can prove its clients stopped; a gone machine's host is one that cannot be read.
                JournalRunState.Recoverable or JournalRunState.Kept when unread.Count == 0 =>
                    $"every host was read, so valheim-test env teardown --run {runId} proves its clients stopped and releases its leases the ordinary way",
                _ => null,
            };
        if (refused != null) { output.WriteLine($"REFUSED {runId}: {refused}"); return false; }
        if (unread.Count != 0) output.WriteLine($"Not read: host {string.Join(", ", unread)}; releasing on the maintainer's word that no client of run {runId} runs there.");
        var host = factory(inventory.LeaseHost!);
        if (!leaseProfile.IsAbsolutePath(inventory.LeaseDirectory)) throw new ArgumentException($"leaseDirectory '{inventory.LeaseDirectory}' is not an absolute path on {inventory.LeaseHost}.");
        var pool = new SteamAccountPool { Pool = "abandon", LeaseDirectory = inventory.LeaseDirectory };
        var result = (await pool.RunAsync(host, "abandon", timeout, cancellation, run: runId).ConfigureAwait(false))
            .EnsureSuccess($"Releasing the leases of run {runId} on {inventory.LeaseHost}");
        var lines = result.Stdout.Split('\n').Select(line => line.TrimEnd()).ToList();
        if (!lines.Contains("VT-LEASE abandoned")) throw new HostOperationException($"Unexpected reply while releasing the leases of run {runId} on {inventory.LeaseHost}", result);
        bool whole = true;
        int released = 0;
        var journal = new RunJournal(runId);
        async Task Note(JournalEntry entry)
        {
            // After the effect: a lost line leaves the run in env status, never a lease held.
            try { await journal.AppendAsync(host, RunJournal.DirectoryFor(leaseProfile), RunRecovery.Actor, entry, timeout, cancellation).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException) { output.WriteLine($"  {entry.Kind} could not be journalled on {inventory.LeaseHost}: {error.Message}"); }
        }
        foreach (string line in lines)
        {
            var parts = line.Split(' ', 6);
            if (parts[0] == "VT-LEASE-UNREADABLE" && parts.Length == 3)
            {
                output.WriteLine($"UNREADABLE lease on Steam account {parts[2]} ({parts[1]}): its claim cannot be read, so whether run {runId} holds it is unknown; inspect it by hand");
                whole = false;
            }
            if (parts[0] != "VT-LEASE-ABANDONED" || parts.Length != 6) continue;
            released++;
            output.WriteLine($"RELEASED lease on Steam account {parts[2]} ({parts[1]}), held by {parts[5]}: its machine was declared gone");
            await Note(JournalEntry.Of(JournalEntry.LeaseReleased, ("account", parts[2]), ("pool", parts[1]), ("owner", parts[5]), ("leaseId", parts[4]),
                ("number", parts[3]), ("directory", inventory.LeaseDirectory), ("machineGone", "true"))).ConfigureAwait(false);
        }
        if (released == 0 && whole) output.WriteLine($"Run {runId} holds no lease in {inventory.LeaseDirectory} on {inventory.LeaseHost}.");
        // Its end, which its gone machine can no longer journal: only for a run whose end no readable journal holds.
        if (released != 0 && (known == null || known.State == JournalRunState.Unknown))
        {
            await Note(JournalEntry.Of(JournalEntry.RunEnded, ("state", "abandoned: its machine is gone"), ("cleanupVerified", "false"))).ConfigureAwait(false);
            output.WriteLine($"Journalled run {runId} as ended on {inventory.LeaseHost}: its machine is gone.");
        }
        return whole;
    }

    /// <summary>
    /// Removes one copy on this machine that no run's journal names (made before runs journalled their copies), keeping what
    /// its run changed in <c>&lt;copy&gt;-changes</c> beside it (<see cref="OwnedCopies.Remove"/>). Refuses a copy a journal names
    /// (<see cref="RecoverRunAsync"/> owns those) and one a running process uses. Returns true when it was removed.
    /// </summary>
    public static async Task<bool> TeardownCopyAsync(string? inventoryPath, string copyPath, TextWriter output, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        var inventory = EnvironmentInventory.ReadHosts(inventoryPath, EnvironmentInventory.ThisMachine);
        string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(copyPath));
        var (hosts, _) = JournalScope(inventory);
        var report = await RunJournalStatus.InspectAsync(hosts, new ResolvedEnvironment { Hosts = hosts }.CreateHost, TimeSpan.FromSeconds(60), cancellation,
            copyRoots: [Path.GetDirectoryName(path)!]).ConfigureAwait(false);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        // The same copy named through a link (macOS /var and /private/var) is the same copy.
        string resolved = OwnedCopies.Resolved(path);
        bool Same(string other) => string.Equals(Path.TrimEndingDirectorySeparator(other), path, comparison)
            || string.Equals(OwnedCopies.Resolved(Path.TrimEndingDirectorySeparator(other)), resolved, comparison);
        if (report.Runs.FirstOrDefault(run => run.Items.Any(item => Same(item.What))) is { } owner)
        {
            output.WriteLine($"REFUSED {path}: run {owner.Run} ({owner.State.ToString().ToUpperInvariant()}) journalled it; use valheim-test env recover|teardown --run {owner.Run}.");
            return false;
        }
        if (!report.Unjournalled.Any(copy => Same(copy.Path)) && report.Hosts.Any(host => host.Error != null))
        {
            output.WriteLine($"REFUSED {path}: a journal could not be read, so whether a run owns this copy is unknown.");
            return false;
        }
        try { output.WriteLine("REMOVED " + OwnedCopies.Remove(path, allowWorld: true)); return true; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            output.WriteLine($"REFUSED {path}: {error.Message}");
            return false;
        }
    }

    /// <summary>
    /// What a bare <c>valheim-test env preflight</c> refuses from this machine's journals (its own and the inventory's local
    /// hosts'; another host's is read by <c>valheim-test session check --hosts</c>): a run of another process still going here, one
    /// that left something (recoverable or not) and was not recovered, one that may still be going, and a journal that cannot be
    /// read. The same wording as the campaign's journal check. Changes nothing.
    /// </summary>
    public static async Task<IReadOnlyList<CampaignPreflightProblem>> LocalJournalProblemsAsync(this EnvironmentInventory inventory, CancellationToken cancellation = default)
    {
        JournalStatusReport status;
        try
        {
            var (all, _) = JournalScope(inventory);
            var hosts = all.Where(host => host.Value.Kind == "local").ToDictionary(host => host.Key, host => host.Value, StringComparer.Ordinal);
            status = await RunJournalStatus.InspectAsync(hosts, new ResolvedEnvironment { Hosts = hosts }.CreateHost, TimeSpan.FromSeconds(60), cancellation,
                inventory.LeaseHost, inventory.LeaseDirectory).ConfigureAwait(false);
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return [new(ThisMachineHost, "run journal", RunJournalStatus.UnreadableProblem(error.Message))];
        }
        // As the campaign check: a run going elsewhere that only holds a Steam lease on this machine (its lease host) uses no
        // game here; one whose runner runs here, or that left anything but leases here, does.
        return RunJournalStatus.Problems(status, run => run.State == JournalRunState.Live || run.Items.Any(item => item.Kind != "lease"));
    }

    private static Task<JournalStatusReport> StatusAsync(EnvironmentInventory inventory, CancellationToken cancellation)
    {
        var (hosts, roots) = JournalScope(inventory);
        return RunJournalStatus.InspectAsync(hosts, new ResolvedEnvironment { Hosts = hosts }.CreateHost, TimeSpan.FromSeconds(60), cancellation,
            inventory.LeaseHost, inventory.LeaseDirectory, roots);
    }

    // The hosts whose journals are read: the inventory's, and this machine's own journal (where its copies are journalled)
    // when no local host of the inventory already reads it. The roots searched for copies no journal names: this machine's
    // data folder and the runtimes of its environments.
    private static (Dictionary<string, HostProfile> Hosts, IReadOnlyList<string> CopyRoots) JournalScope(EnvironmentInventory inventory)
    {
        var hosts = new Dictionary<string, HostProfile>(inventory.Hosts, StringComparer.Ordinal);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!hosts.Values.Any(host => host.Kind == "local" && string.Equals(RunJournal.DirectoryFor(host), RunJournal.LocalDirectory, comparison)))
        {
            string name = hosts.ContainsKey(ThisMachineHost) ? ThisMachineHost + "-" + Guid.NewGuid().ToString("N")[..6] : ThisMachineHost;
            hosts[name] = new HostProfile
            {
                Kind = "local", Platform = HostProfile.CurrentPlatform, Shell = OperatingSystem.IsWindows() ? "powershell" : "bash",
                Lock = Path.Combine(Path.GetDirectoryName(RunJournal.LocalDirectory)!, "lock"),
            };
        }
        var locals = inventory.Hosts.Where(host => host.Value.Kind == "local").Select(host => host.Key).ToHashSet(StringComparer.Ordinal);
        var roots = inventory.Environments.Where(recipe => locals.Contains(recipe.Host) && !string.IsNullOrEmpty(recipe.Runtime)).Select(recipe => recipe.Runtime)
            .Prepend(EnvironmentInventory.ThisMachine.DataRoot).ToList();
        return (hosts, roots);
    }
    private const string ThisMachineHost = "this-machine";

    /// <summary>Assign the campaign's actors in order, backtracking when an earlier choice blocks a later one.</summary>
    internal static ResolvedEnvironmentInventory Resolve(this EnvironmentInventory inventory, HostedCampaignManifest campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        var actors = (campaign.Server == null ? [] : new[] { (Name: "server", Kind: "server", Input: campaign.Server) })
            .Concat(campaign.Clients.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => (Name: pair.Key, Kind: "client", Input: pair.Value))).ToArray();
        var names = actors.Select(actor => actor.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var actor in actors)
            foreach (string other in actor.Input.DifferentHostFrom)
                if (other == actor.Name || !names.Contains(other))
                    throw new ArgumentException($"Actor {actor.Name} names unknown or self differentHostFrom actor {other}.");
        var chosen = new Dictionary<string, EnvironmentRecipe>(StringComparer.Ordinal);
        var reasons = new Dictionary<string, string>(StringComparer.Ordinal);
        string? failedActor = null;
        var refusals = new List<string>();
        bool Search(int index)
        {
            if (index == actors.Length) return true;
            var actor = actors[index];
            var wanted = actor.Input.EnvironmentCandidates;
            var candidates = wanted.Count == 0 ? inventory.Environments : wanted.Select(name =>
                inventory.Environments.FirstOrDefault(recipe => recipe.Name == name) ??
                throw new ArgumentException($"Actor {actor.Name} requests unknown environment {name}.")).ToList();
            var skipped = new List<string>();
            foreach (var recipe in candidates)
            {
                string? refusal = Refusal(actor.Name, actor.Kind, actor.Input, recipe, chosen, actors);
                if (refusal != null) { skipped.Add(recipe.Name + ": " + refusal); continue; }
                chosen[actor.Name] = recipe;
                reasons[actor.Name] = skipped.Count == 0 ? "first compatible recipe in inventory order"
                    : "first compatible recipe after " + string.Join("; ", skipped);
                if (Search(index + 1)) return true;
                chosen.Remove(actor.Name);
                skipped.Add(recipe.Name + ": leaves a later actor without a compatible environment");
            }
            if (failedActor == null) { failedActor = actor.Name; refusals.AddRange(skipped); }
            return false;
        }
        if (!Search(0)) throw new ArgumentException($"No environment assignment for {failedActor}: " + string.Join("; ", refusals) + MissingNote(inventory));
        var profile = new ResolvedEnvironment
        {
            Hosts = inventory.Hosts,
            Server = campaign.Server == null ? null : chosen["server"].Role(),
            Clients = campaign.Clients.Keys.ToDictionary(name => name, name => chosen[name].Role(), StringComparer.Ordinal),
        };
        if (profile.Clients.Count > 0)
        {
            profile.SteamAccounts = new SteamAccountsProfile
            {
                LeaseHost = inventory.LeaseHost, CheckSignedIn = true, ObservedLeaseDirectory = inventory.LeaseDirectory,
                InventoryClientHosts = inventory.Environments.Where(recipe => recipe.Roles.Contains("client")).Select(recipe => recipe.Host)
                    .Distinct(StringComparer.Ordinal).ToList(),
            };
        }
        profile.Validate();
        var assignments = actors.Select(actor => new EnvironmentAssignment(actor.Name, chosen[actor.Name].Name,
            actor.Kind == "server" ? "dedicated-server" : "client", chosen[actor.Name].Host,
            reasons[actor.Name])).ToArray();
        return new ResolvedEnvironmentInventory(profile, assignments)
        {
            LoaderPackages = actors.ToDictionary(actor => actor.Name,
                actor => actor.Input.LoaderPackage ?? chosen[actor.Name].LoaderPackage, StringComparer.Ordinal),
        };
    }

    /// <summary>
    /// A standalone run's one actor, the dedicated server: the first server environment in inventory order whose host and
    /// ports can run <paramref name="plan"/>, with the reason it was chosen. Clients go through a campaign, which declares them.
    /// </summary>
    internal static (ResolvedEnvironment Environment, EnvironmentAssignment Assignment) PlaceServer(this EnvironmentInventory inventory, ServerRunPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var skipped = new List<string>();
        foreach (var recipe in inventory.Environments.Where(recipe => recipe.Roles.Contains("server")))
        {
            var role = recipe.Role();
            if (recipe.LoaderPackage != null)
            { skipped.Add(recipe.Name + ": it names a loaderPackage, which only a campaign's preparation applies"); continue; }
            if (HostedServerRun.Refusal(inventory.Hosts[recipe.Host], role, plan) is { } refusal) { skipped.Add(recipe.Name + ": " + refusal); continue; }
            var environment = new ResolvedEnvironment { Hosts = inventory.Hosts, Server = role };
            environment.Validate();
            return (environment, new EnvironmentAssignment("server", recipe.Name, "dedicated-server", recipe.Host,
                skipped.Count == 0 ? "first server recipe in inventory order" : "first server recipe that can run the plan after " + string.Join("; ", skipped)));
        }
        throw new ArgumentException("No server environment in the inventory can run this plan" +
            (skipped.Count == 0 ? ": it lists none." : ": " + string.Join("; ", skipped)) + MissingNote(inventory));
    }

    private static string MissingNote(EnvironmentInventory inventory) =>
        inventory.Missing.Count == 0 ? "" : ". This machine: " + string.Join(" ", inventory.Missing);

    private static string? Refusal(string actor, string kind, HostedCampaignRole input, EnvironmentRecipe recipe,
        IReadOnlyDictionary<string, EnvironmentRecipe> chosen,
        IReadOnlyList<(string Name, string Kind, HostedCampaignRole Input)> actors)
    {
        if (!recipe.Roles.Contains(kind)) return "does not support " + kind;
        if (input.LoaderPackage != null && recipe.LoaderPackage != null &&
            !input.LoaderPackage.Equals(recipe.LoaderPackage, StringComparison.Ordinal))
            return "campaign and recipe name different loader packages";
        if (chosen.Values.Any(other => other.Name == recipe.Name)) return "already assigned to another actor";
        if (chosen.Values.Any(other => other.Host == recipe.Host && other.CliPort == recipe.CliPort))
            return "ValheimCLI port conflicts on host " + recipe.Host;
        if (recipe.LocalCliPort != 0 && chosen.Values.Any(other => other.LocalCliPort == recipe.LocalCliPort))
            return "local ValheimCLI tunnel port conflicts";
        if (kind == "client")
        {
            if (chosen.Any(other => other.Key != "server" && other.Value.Host == recipe.Host))
                return "another client uses this host's desktop session";
        }
        foreach (var other in chosen)
        {
            bool distinct = actors.First(item => item.Name == actor).Input.DifferentHostFrom.Contains(other.Key) ||
                actors.First(item => item.Name == other.Key).Input.DifferentHostFrom.Contains(actor);
            if (distinct && other.Value.Host == recipe.Host) return "differentHostFrom requires a different host than " + other.Key;
        }
        return null;
    }
}
