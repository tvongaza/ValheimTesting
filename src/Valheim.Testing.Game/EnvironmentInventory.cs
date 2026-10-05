using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Valheim.Testing.Game;

/// <summary>One preferred launch recipe in a private operator inventory; it is never a running process.</summary>
public sealed class EnvironmentRecipe
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    /// <summary><c>server</c> or <c>client</c>; a client recipe may list only client, a server recipe only server.</summary>
    public List<string> Roles { get; set; } = [];
    public string Install { get; set; } = "";
    public string Runtime { get; set; } = "";
    public int CliPort { get; set; }
    public int LocalCliPort { get; set; }
    public int GamePort { get; set; }
    public string? LoaderPackage { get; set; }

    internal GameRole Role() => new()
    {
        Host = Host, Install = Install, Runtime = Runtime, CliPort = CliPort,
        LocalCliPort = LocalCliPort, GamePort = GamePort,
    };
}

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
/// Ordered, private operator inventory. Client identities are discovered from their signed-in Steam environments;
/// a campaign supplies actors and its own dependency locks. Resolution is deterministic and has no host effects.
/// </summary>
public sealed class EnvironmentInventory
{
    private static readonly Regex NamePattern = new("^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
    };

    public Dictionary<string, HostProfile> Hosts { get; set; } = [];
    public List<EnvironmentRecipe> Environments { get; set; } = [];
    /// <summary>The host that stores Steam-account leases; required if any client recipe exists.</summary>
    public string LeaseHost { get; set; } = "";
    /// <summary>An absolute lease directory on <see cref="LeaseHost"/>, shared by inventories using these Steam accounts.</summary>
    public string LeaseDirectory { get; set; } = "";

    /// <summary>
    /// What the this-machine default detected and assumed, one line each, for preflight to print; empty for an inventory
    /// that was not read through <see cref="Read(string?)"/>.
    /// </summary>
    [JsonIgnore] public IReadOnlyList<string> Detected => _detected;
    /// <summary>What the this-machine default looked for and did not find, with every path tried.</summary>
    [JsonIgnore] public IReadOnlyList<string> Missing => _missing;
    /// <summary>This machine's Steam <c>userdata</c> under the detected Steam root, when it exists; null otherwise or when nothing was detected.</summary>
    [JsonIgnore] public string? SteamUserData { get; private set; }
    private readonly List<string> _detected = [], _missing = [];

    /// <summary>
    /// The inventory at <paramref name="path"/>, or this machine alone when <paramref name="path"/> is null: host <c>local</c>
    /// with <c>local-server</c> (Valheim Dedicated Server, Steam app 896660) and <c>local-client</c> (Valheim), found in its
    /// Steam libraries. A file lists the environments to use, in preference order; one on this machine (a host of kind
    /// <c>local</c>, or no host) needs only its name and roles, and its install, runtime and ports default as
    /// <see cref="Detected"/> reports. A file never gains environments it does not list.
    /// </summary>
    public static EnvironmentInventory Read(string? path) => Read(path, ThisMachine);

    /// <summary>
    /// The machine <see cref="Read(string?)"/> detects. Controlled tests replace it so the test machine's own Steam never
    /// decides a result: the default for every test, or one for a test's own flow (<see cref="UseMachine"/>).
    /// </summary>
    internal static ISteamLocator ThisMachine { get => FlowMachine.Value ?? _thisMachine; set => _thisMachine = value; }
    private static ISteamLocator _thisMachine = new LocalSteamLocator();
    private static readonly AsyncLocal<ISteamLocator?> FlowMachine = new();
    /// <summary>Detects <paramref name="machine"/> in this flow until disposed.</summary>
    internal static IDisposable UseMachine(ISteamLocator machine)
    {
        var previous = FlowMachine.Value;
        FlowMachine.Value = machine;
        return new FlowReset(previous);
    }
    private sealed class FlowReset(ISteamLocator? previous) : IDisposable { public void Dispose() => FlowMachine.Value = previous; }

    internal static EnvironmentInventory Read(string? path, ISteamLocator machine)
    {
        var inventory = Load(path, machine);
        inventory.Validate(path == null ? Environment.CurrentDirectory : Path.GetDirectoryName(Path.GetFullPath(path))!);
        return inventory;
    }

    // The file (or nothing) with this machine's host and environments filled in, not yet validated.
    private static EnvironmentInventory Load(string? path, ISteamLocator machine)
    {
        var inventory = path == null ? new EnvironmentInventory()
            : JsonSerializer.Deserialize<EnvironmentInventory>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Empty environment inventory.");
        inventory.AddThisMachine(machine, fromFile: path != null);
        return inventory;
    }

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
        var inventory = ReadHosts(inventoryPath, ThisMachine);
        if (!json) foreach (string line in inventory.Detected) output.WriteLine("detected: " + line);
        var report = await inventory.StatusAsync(cancellation).ConfigureAwait(false);
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
        var inventory = ReadHosts(inventoryPath, ThisMachine);
        if (!json) foreach (string line in inventory.Detected) output.WriteLine("detected: " + line);
        var (hosts, _) = inventory.JournalScope();
        var report = await RunRecovery.RecoverAsync(hosts, new ResolvedEnvironment { Hosts = hosts }.CreateHost, runId, teardown,
            TimeSpan.FromSeconds(60), cancellation, inventory.LeaseHost, inventory.LeaseDirectory).ConfigureAwait(false);
        RunRecovery.Write(report, output, json);
        return report.Recovered;
    }

    /// <summary>
    /// Removes one copy on this machine that no run's journal names (made before runs journalled their copies), keeping what
    /// its run changed in <c>&lt;copy&gt;-changes</c> beside it (<see cref="OwnedCopies.Remove"/>). Refuses a copy a journal names
    /// (<see cref="RecoverRunAsync"/> owns those) and one a running process uses. Returns true when it was removed.
    /// </summary>
    public static async Task<bool> TeardownCopyAsync(string? inventoryPath, string copyPath, TextWriter output, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        var inventory = ReadHosts(inventoryPath, ThisMachine);
        string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(copyPath));
        var (hosts, _) = inventory.JournalScope();
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

    private Task<JournalStatusReport> StatusAsync(CancellationToken cancellation)
    {
        var (hosts, roots) = JournalScope();
        return RunJournalStatus.InspectAsync(hosts, new ResolvedEnvironment { Hosts = hosts }.CreateHost, TimeSpan.FromSeconds(60), cancellation,
            LeaseHost, LeaseDirectory, roots);
    }

    // The hosts whose journals are read: the inventory's, and this machine's own journal (where its copies are journalled)
    // when no local host of the inventory already reads it. The roots searched for copies no journal names: this machine's
    // data folder and the runtimes of its environments.
    private (Dictionary<string, HostProfile> Hosts, IReadOnlyList<string> CopyRoots) JournalScope()
    {
        var hosts = new Dictionary<string, HostProfile>(Hosts, StringComparer.Ordinal);
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
        var locals = Hosts.Where(host => host.Value.Kind == "local").Select(host => host.Key).ToHashSet(StringComparer.Ordinal);
        var roots = Environments.Where(recipe => locals.Contains(recipe.Host) && !string.IsNullOrEmpty(recipe.Runtime)).Select(recipe => recipe.Runtime)
            .Prepend(ThisMachine.DataRoot).ToList();
        return (hosts, roots);
    }
    private const string ThisMachineHost = "this-machine";

    // As Read, but checks only the hosts: a host's journal needs the host and its lock, not an install or an environment.
    internal static EnvironmentInventory ReadHosts(string? path, ISteamLocator machine)
    {
        var inventory = Load(path, machine);
        if (inventory.Hosts == null || inventory.Hosts.Any(host => host.Value == null))
            throw new ArgumentException("Invalid environment inventory: hosts must contain objects, not null.");
        var errors = new List<string>();
        if (inventory.Hosts.Count == 0) errors.Add("List at least one host.");
        foreach (var (name, host) in inventory.Hosts)
        {
            if (!NamePattern.IsMatch(name)) errors.Add("Invalid host name: " + name);
            host.Validate(name, errors);
        }
        if (errors.Count != 0) throw new ArgumentException("Invalid environment inventory: " + string.Join(" ", errors));
        return inventory;
    }

    internal const string LocalHost = "local", LocalServer = "local-server", LocalClient = "local-client";
    // valheim-test's own defaults, away from Valheim's standard 2456: a personal server on this machine or network keeps its
    // port, and a crossplay lobby (keyed by public IP and port) never takes another server's joins.
    private const int FirstCliPort = 5688, FirstGamePort = 2486;
    private readonly Dictionary<string, string> _noInstall = new(StringComparer.Ordinal);

    // Fills this machine's host and the left-out fields of its environments, saying what it detected and assumed. With no
    // file it also lists the detected environments; a file's list is kept as written.
    private void AddThisMachine(ISteamLocator machine, bool fromFile)
    {
        if (Hosts == null || Environments == null || Environments.Any(recipe => recipe == null || recipe.Roles == null) ||
            Hosts.Any(host => host.Value == null)) return; // Validate names these.
        var locals = Hosts.Where(host => host.Value.Kind == "local").Select(host => host.Key).ToList();
        if (locals.Count > 1) return; // Validate's host rules and assignment see each; nothing is defaulted between two.
        bool needed = !fromFile || Environments.Any(recipe => string.IsNullOrEmpty(recipe.Host) || recipe.Host == (locals.FirstOrDefault() ?? LocalHost));
        // A file's own local host gets this machine's platform, shell and lock even when no environment uses it (env status reads its journal).
        if (!needed && locals.Count == 0) return;
        string name = locals.FirstOrDefault() ?? LocalHost;
        if (!Hosts.TryGetValue(name, out var local))
        {
            Hosts[name] = local = new HostProfile { Kind = "local" };
            _detected.Add($"host {name}: this machine ({machine.Platform})");
        }
        if (string.IsNullOrEmpty(local.Kind)) local.Kind = "local";
        if (local.Kind != "local") return; // A file's own host of that name that is not this machine: Validate judges it as written.
        bool windows = machine.Platform == "windows";
        if (string.IsNullOrEmpty(local.Platform)) local.Platform = machine.Platform;
        if (string.IsNullOrEmpty(local.Shell)) local.Shell = windows ? "powershell" : "bash";
        if (string.IsNullOrEmpty(local.Lock)) _detected.Add("assumed host lock " + (local.Lock = HostInstall.Join(machine.DataRoot, "lock")));
        if (!needed) return;

        var steam = SteamDetection.Find(machine, SteamDetection.GameApp, SteamDetection.DedicatedServerApp);
        if (steam.Root != null && machine.DirectoryExists(HostInstall.Join(steam.Root, "userdata"))) SteamUserData = HostInstall.Join(steam.Root, "userdata");
        _detected.Add(steam.Root == null ? "Steam: not found (tried " + string.Join(", ", steam.RootsTried) + ")"
            : $"Steam: {steam.Root} (from {steam.RootRule}); libraries {string.Join(", ", steam.Libraries)}");
        string? game = Installed(machine, steam, SteamDetection.GameApp, windows ? ClientLaunch.WindowsExecutable
            : machine.Platform == "macos" ? ClientLaunch.MacBundle + "/Contents/MacOS/Valheim" : ClientLaunch.LinuxExecutable, "Valheim", out string? noGame);
        string? server = null, noServer;
        if (machine.Platform == "macos")
            noServer = "No local dedicated server: the campaign runner does not run a macOS dedicated server. Add a Windows or Linux " +
                "server environment, or run `valheim-test start` for a hosted local world.";
        else
        {
            server = Installed(machine, steam, SteamDetection.DedicatedServerApp, windows ? ServerLaunch.WindowsExecutable : ServerLaunch.LinuxExecutable,
                "Valheim Dedicated Server", out noServer);
            if (noServer != null)
                noServer += " Install Valheim Dedicated Server from Steam (it is free), add a server environment, or run `valheim-test start` for a hosted local world.";
        }
        if (game != null) _detected.Add($"Valheim (Steam app {SteamDetection.GameApp}): {game}");
        if (server != null) _detected.Add($"Valheim Dedicated Server (Steam app {SteamDetection.DedicatedServerApp}): {server}");
        if (!fromFile)
            foreach (var (recipeName, role, install, missing) in new[] { (LocalServer, "server", server, noServer), (LocalClient, "client", game, noGame) })
            {
                if (install == null) _missing.Add(missing!);
                else Environments.Add(new EnvironmentRecipe { Name = recipeName, Host = name, Roles = [role] });
            }

        // First every left-out host, so each port choice sees every environment on this machine.
        foreach (var recipe in Environments.Where(recipe => string.IsNullOrEmpty(recipe.Host))) recipe.Host = name;
        // Ports reached on this machine: a local or container host's CLI port, an ssh environment's tunnel port.
        var taken = Environments.Select(recipe => Hosts.TryGetValue(recipe.Host, out var host) && host.Kind is "local" or "container"
            ? recipe.CliPort : recipe.LocalCliPort).Where(port => port != 0).ToHashSet();
        // A server binds its game port and the next two (crossplay); a local or host-network container server binds them here.
        var games = Environments.Where(recipe => Hosts.TryGetValue(recipe.Host, out var host) && host.Kind is "local" or "container")
            .Select(recipe => recipe.GamePort).Where(port => port != 0).ToList();
        foreach (var recipe in Environments.Where(recipe => recipe.Host == name))
        {
            bool serves = recipe.Roles.Contains("server");
            if (string.IsNullOrEmpty(recipe.Install))
            {
                recipe.Install = (serves ? server : recipe.Roles.Contains("client") ? game : null) ?? "";
                if (recipe.Install.Length == 0)
                {
                    _noInstall[recipe.Name ?? ""] = (serves ? noServer : noGame) ?? "";
                    _missing.Add($"Environment {recipe.Name} has no install: {_noInstall[recipe.Name ?? ""]}");
                }
            }
            var assumed = new List<string>();
            if (string.IsNullOrEmpty(recipe.Runtime)) assumed.Add("runtime " + (recipe.Runtime = HostInstall.Join(machine.DataRoot, "runs", recipe.Name ?? "")));
            if (recipe.CliPort == 0)
            {
                taken.Add(recipe.CliPort = Enumerable.Range(FirstCliPort, 1000).First(port => !taken.Contains(port)));
                assumed.Add("ValheimCLI port " + recipe.CliPort);
            }
            if (serves && recipe.GamePort == 0)
            {
                games.Add(recipe.GamePort = Enumerable.Range(0, 100).Select(step => FirstGamePort + 10 * step)
                    .First(port => games.All(other => Math.Abs(other - port) >= 3)));
                assumed.Add("game port " + recipe.GamePort);
            }
            _detected.Add($"{recipe.Name} ({string.Join(",", recipe.Roles)}) on {name}: install {(recipe.Install.Length == 0 ? "not found" : recipe.Install)}" +
                (assumed.Count == 0 ? "" : "; assumed " + string.Join(", ", assumed)));
        }
        // Leases default to this machine only when every client is on it: clients elsewhere share a lease host the file names.
        var clients = Environments.Where(recipe => recipe.Roles.Contains("client")).ToList();
        if (string.IsNullOrWhiteSpace(LeaseHost) && string.IsNullOrWhiteSpace(LeaseDirectory) && clients.Count != 0 && clients.All(recipe => recipe.Host == name))
        {
            LeaseHost = name;
            LeaseDirectory = HostInstall.Join(machine.DataRoot, "leases");
            _detected.Add($"assumed Steam-account leases on {name} in {LeaseDirectory}");
        }
    }

    // The app's install when its executable is there; otherwise null, with every path tried.
    private static string? Installed(ISteamLocator machine, SteamDetection steam, string appId, string executable, string title, out string? missing)
    {
        var app = steam.Apps[appId];
        missing = null;
        if (app.Install != null && machine.FileExists(HostInstall.Join(app.Install, executable))) return app.Install;
        missing = app.Install != null
            ? $"No {title} (Steam app {appId}) on this machine: {app.Install} has no {executable}."
            : $"No {title} (Steam app {appId}) on this machine: " + (steam.Root == null
                ? "Steam was not found (tried " + string.Join(", ", steam.RootsTried) + ")."
                : "tried " + string.Join(", ", app.Tried) + ".");
        return null;
    }

    /// <summary>Validate the whole inventory before any actor assignment or host contact.</summary>
    public void Validate(string directory)
    {
        var errors = new List<string>();
        if (Hosts == null || Environments == null || Environments.Any(recipe => recipe == null) ||
            Hosts.Any(host => host.Value == null))
            throw new ArgumentException("Invalid environment inventory: hosts and environments must contain objects, not null.");
        if (Hosts.Count == 0) errors.Add("List at least one host.");
        foreach (var (name, host) in Hosts)
        {
            if (!NamePattern.IsMatch(name)) errors.Add("Invalid host name: " + name);
            host.Validate(name, errors);
        }
        if (Environments.Count == 0) errors.Add("List at least one environment recipe in preference order." +
            (_missing.Count == 0 ? "" : " This machine has none: " + string.Join(" ", _missing)));
        foreach (var recipe in Environments)
        {
            if (recipe.Roles == null)
            { errors.Add($"Environment {recipe.Name} needs a roles list."); continue; }
            if (!NamePattern.IsMatch(recipe.Name ?? "")) errors.Add("Invalid environment name: " + recipe.Name);
            if (recipe.Host == null || !Hosts.TryGetValue(recipe.Host, out var host))
            { errors.Add($"Environment {recipe.Name} names unknown host {recipe.Host}."); continue; }
            if (recipe.Roles is not (["server"] or ["client"]))
                errors.Add($"Environment {recipe.Name} needs roles [\"server\"] or [\"client\"]: a dedicated server recipe cannot also be a client process.");
            if (!host.IsAbsolutePath(recipe.Install) || !host.IsAbsolutePath(recipe.Runtime))
                errors.Add($"Environment {recipe.Name} needs absolute install and runtime paths on {recipe.Host}." +
                    (_noInstall.TryGetValue(recipe.Name ?? "", out string? why) ? " " + why : ""));
            if (recipe.CliPort is < 1024 or > 65535 || recipe.LocalCliPort is < 0 or > 65535)
                errors.Add($"Environment {recipe.Name} needs valid ValheimCLI ports.");
            bool serves = recipe.Roles.Contains("server");
            if (serves ? recipe.GamePort is < 1024 or > 65534 : recipe.GamePort != 0)
                errors.Add($"Environment {recipe.Name} needs a gamePort only when it serves a world.");
            foreach (string role in recipe.Roles)
                recipe.Role().Validate(role == "server" ? "server" : "client " + recipe.Name, host, errors);
            if (recipe.Roles.Contains("server") && host.Platform == "macos")
                errors.Add($"Environment {recipe.Name}: remote macOS dedicated servers are not supported by this campaign runner.");
            if (recipe.LoaderPackage != null)
                recipe.LoaderPackage = Path.GetFullPath(recipe.LoaderPackage, directory);
        }
        foreach (var duplicate in Environments.GroupBy(recipe => recipe.Name, StringComparer.Ordinal).Where(group => group.Count() > 1))
            errors.Add("Environment " + duplicate.Key + " is listed twice.");
        bool hasClients = Environments.Any(recipe => recipe.Roles?.Contains("client") == true);
        if (hasClients && (string.IsNullOrWhiteSpace(LeaseHost) || !Hosts.TryGetValue(LeaseHost, out var leaseHost) ||
            string.IsNullOrWhiteSpace(LeaseDirectory) || !leaseHost.IsAbsolutePath(LeaseDirectory)))
            errors.Add("Client environments need a listed leaseHost and an absolute leaseDirectory on it.");
        if (errors.Count != 0) throw new ArgumentException("Invalid environment inventory: " + string.Join(" ", errors));
    }

    /// <summary>Assign the campaign's actors in order, backtracking when an earlier choice blocks a later one.</summary>
    internal ResolvedEnvironmentInventory Resolve(HostedCampaignManifest campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        var actors = new[] { (Name: "server", Kind: "server", Input: campaign.Server) }
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
            var candidates = wanted.Count == 0 ? Environments : wanted.Select(name =>
                Environments.FirstOrDefault(recipe => recipe.Name == name) ??
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
        if (!Search(0)) throw new ArgumentException($"No environment assignment for {failedActor}: " + string.Join("; ", refusals) + MissingNote);
        var profile = new ResolvedEnvironment
        {
            Hosts = Hosts,
            Server = chosen["server"].Role(),
            Clients = campaign.Clients.Keys.ToDictionary(name => name, name => chosen[name].Role(), StringComparer.Ordinal),
        };
        if (profile.Clients.Count > 0)
        {
            profile.SteamAccounts = new SteamAccountsProfile
            {
                LeaseHost = LeaseHost, CheckSignedIn = true, ObservedLeaseDirectory = LeaseDirectory,
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
    internal (ResolvedEnvironment Environment, EnvironmentAssignment Assignment) PlaceServer(ServerRunPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var skipped = new List<string>();
        foreach (var recipe in Environments.Where(recipe => recipe.Roles.Contains("server")))
        {
            var role = recipe.Role();
            if (recipe.LoaderPackage != null)
            { skipped.Add(recipe.Name + ": it names a loaderPackage, which only a campaign's preparation applies"); continue; }
            if (HostedServerRun.Refusal(Hosts[recipe.Host], role, plan) is { } refusal) { skipped.Add(recipe.Name + ": " + refusal); continue; }
            var environment = new ResolvedEnvironment { Hosts = Hosts, Server = role };
            environment.Validate();
            return (environment, new EnvironmentAssignment("server", recipe.Name, "dedicated-server", recipe.Host,
                skipped.Count == 0 ? "first server recipe in inventory order" : "first server recipe that can run the plan after " + string.Join("; ", skipped)));
        }
        throw new ArgumentException("No server environment in the inventory can run this plan" +
            (skipped.Count == 0 ? ": it lists none." : ": " + string.Join("; ", skipped)) + MissingNote);
    }

    private string MissingNote => _missing.Count == 0 ? "" : ". This machine: " + string.Join(" ", _missing);

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
