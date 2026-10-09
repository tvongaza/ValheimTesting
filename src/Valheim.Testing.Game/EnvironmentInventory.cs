using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

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
    /// <summary>Client slice. When omitted, inventory resolution selects arm64 on a local Apple Silicon Mac and x64 elsewhere; x64 can be selected for Rosetta.</summary>
    public string Architecture { get; set; } = "";

    internal GameRole Role() => new()
    {
        Host = Host, Install = Install, Runtime = Runtime, CliPort = CliPort,
        LocalCliPort = LocalCliPort, GamePort = GamePort, Architecture = Architecture,
    };
}

/// <summary>
/// Ordered, private operator inventory. Client identities are discovered from their signed-in Steam environments;
/// a campaign supplies actors and its own dependency locks. Reading it has no host effects; placing actors on it
/// (deterministic, no host effects either) and acting on its hosts are <c>EnvironmentRuns</c>'.
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

    internal static string DefaultClientArchitecture(string platform, Architecture osArchitecture) =>
        platform == "macos" && osArchitecture == Architecture.Arm64 ? "arm64" : "x64";

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
        if (string.IsNullOrEmpty(local.Lock)) _detected.Add("assumed host lock " + (local.Lock = HostPath.Join(machine.DataRoot, "lock")));
        if (!needed) return;

        var steam = SteamDetection.Find(machine, SteamDetection.GameApp, SteamDetection.DedicatedServerApp);
        if (steam.Root != null && machine.DirectoryExists(HostPath.Join(steam.Root, "userdata"))) SteamUserData = HostPath.Join(steam.Root, "userdata");
        _detected.Add(steam.Root == null ? "Steam: not found (tried " + string.Join(", ", steam.RootsTried) + ")"
            : $"Steam: {steam.Root} (from {steam.RootRule}); libraries {string.Join(", ", steam.Libraries)}");
        string? game = Installed(machine, steam, SteamDetection.GameApp, windows ? GameLaunch.ClientWindowsExecutable
            : machine.Platform == "macos" ? GameLaunch.ClientMacBundle + "/Contents/MacOS/Valheim" : GameLaunch.ClientLinuxExecutable, "Valheim", out string? noGame);
        string? server = Installed(machine, steam, SteamDetection.DedicatedServerApp,
            windows ? GameLaunch.ServerWindowsExecutable : machine.Platform == "macos" ? GameLaunch.ServerMacExecutable : GameLaunch.ServerLinuxExecutable,
            "Valheim Dedicated Server", out string? noServer);
        if (noServer != null)
            noServer += " Install Valheim Dedicated Server from Steam (it is free), add a server environment, or run `valheim-test start` for a hosted local world.";
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
            var assumed = new List<string>();
            if (string.IsNullOrEmpty(recipe.Architecture))
            {
                recipe.Architecture = recipe.Roles.Contains("client")
                    ? DefaultClientArchitecture(machine.Platform, machine.OsArchitecture) : "";
                if (recipe.Roles.Contains("client")) assumed.Add("client architecture " + recipe.Architecture);
            }
            if (string.IsNullOrEmpty(recipe.Install))
            {
                recipe.Install = (serves ? server : recipe.Roles.Contains("client") ? game : null) ?? "";
                if (recipe.Install.Length == 0)
                {
                    _noInstall[recipe.Name ?? ""] = (serves ? noServer : noGame) ?? "";
                    _missing.Add($"Environment {recipe.Name} has no install: {_noInstall[recipe.Name ?? ""]}");
                }
            }
            if (string.IsNullOrEmpty(recipe.Runtime)) assumed.Add("runtime " + (recipe.Runtime = HostPath.Join(machine.DataRoot, "runs", recipe.Name ?? "")));
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
            LeaseDirectory = HostPath.Join(machine.DataRoot, "leases");
            _detected.Add($"assumed Steam-account leases on {name} in {LeaseDirectory}");
        }
    }

    // The app's install when its executable is there; otherwise null, with every path tried.
    private static string? Installed(ISteamLocator machine, SteamDetection steam, string appId, string executable, string title, out string? missing)
    {
        var app = steam.Apps[appId];
        missing = null;
        if (app.Install != null && machine.FileExists(HostPath.Join(app.Install, executable))) return app.Install;
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
            if (string.IsNullOrEmpty(recipe.Architecture) && recipe.Roles.Contains("client"))
                recipe.Architecture = host.Kind == "local"
                    ? DefaultClientArchitecture(host.Platform, ThisMachine.OsArchitecture) : "x64";
            if (recipe.Roles is not (["server"] or ["client"]))
                errors.Add($"Environment {recipe.Name} needs roles [\"server\"] or [\"client\"]: a dedicated server recipe cannot also be a client process.");
            if (!host.IsAbsolutePath(recipe.Install) || !host.IsAbsolutePath(recipe.Runtime))
                errors.Add($"Environment {recipe.Name} needs absolute install and runtime paths on {recipe.Host}." +
                    (_noInstall.TryGetValue(recipe.Name ?? "", out string? why) ? " " + why : ""));
            if (recipe.CliPort is < 1024 or > 65535 || recipe.LocalCliPort is < 0 or > 65535)
                errors.Add($"Environment {recipe.Name} needs valid ValheimCLI ports.");
            bool serves = recipe.Roles.Contains("server");
            bool validArchitecture = serves ? recipe.Architecture.Length == 0 :
                (recipe.Architecture is "x64" or "arm64") && (recipe.Architecture != "arm64" || host.Platform == "macos");
            if (!validArchitecture)
                errors.Add($"Environment {recipe.Name}: architecture is client-only: x64, or arm64 for a macOS client.");
            if (serves ? recipe.GamePort is < 1024 or > 65534 : recipe.GamePort != 0)
                errors.Add($"Environment {recipe.Name} needs a gamePort only when it serves a world.");
            foreach (string role in recipe.Roles)
                recipe.Role().Validate(role == "server" ? "server" : "client " + recipe.Name, host, errors);
            if (recipe.Roles.Contains("server") && host.Platform == "macos" && host.Kind != "local")
                errors.Add($"Environment {recipe.Name}: a macOS dedicated server must run on this machine, not a remote host.");
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
}
