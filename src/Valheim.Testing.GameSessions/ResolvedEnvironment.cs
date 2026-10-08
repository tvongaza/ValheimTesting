using System.Text.RegularExpressions;
using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>
/// Where a run's game processes live once an <see cref="EnvironmentInventory"/> placed its actors: the hosts, which one runs
/// the dedicated server and which runs each client, their install and runtime paths, their ports and, for a campaign, the
/// observed Steam identities to lease. It exists only in memory: an inventory resolves it for one run, nothing reads it from
/// a file and nothing writes it to one.
/// </summary>
internal sealed class ResolvedEnvironment
{
    private static readonly Regex Name = new("^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant);

    public Dictionary<string, HostProfile> Hosts { get; set; } = [];
    /// <summary>The dedicated server, if the run has one.</summary>
    public GameRole? Server { get; set; }
    /// <summary>The game clients by name.</summary>
    public Dictionary<string, GameRole> Clients { get; set; } = [];
    /// <summary>The lease host, directory and observed identities the clients lease their Steam accounts by.</summary>
    public SteamAccountsProfile? SteamAccounts { get; set; }

    /// <summary>Every problem at once, as one <see cref="ArgumentException"/>.</summary>
    public void Validate()
    {
        var errors = new List<string>();
        if (Hosts.Count == 0) errors.Add("List at least one host.");
        foreach (var (name, host) in Hosts)
        {
            if (!Name.IsMatch(name)) errors.Add($"Host name '{name}' must be letters, digits, '.', '_' or '-'.");
            host.Validate(name, errors);
        }
        if (Server == null && Clients.Count == 0) errors.Add("Name a server or at least one client.");
        var roles = new List<(string Role, GameRole Value)>();
        if (Server != null) roles.Add(("server", Server));
        foreach (var (name, client) in Clients)
        {
            if (!Name.IsMatch(name)) errors.Add($"Client name '{name}' must be letters, digits, '.', '_' or '-'.");
            roles.Add(("client " + name, client));
        }
        foreach (var (role, value) in roles)
        {
            if (!Hosts.TryGetValue(value.Host ?? "", out var host)) { errors.Add($"The {role}'s host '{value.Host}' is not listed under hosts."); continue; }
            value.Validate(role, host, errors);
        }
        if (Server != null && Hosts.TryGetValue(Server.Host ?? "", out var serverHost) && serverHost.Platform == "macos" && serverHost.Kind != "local")
            errors.Add($"The server's host '{Server.Host}' is macOS: a macOS dedicated server must run on this machine, not a remote host.");
        // One Valheim client per machine: Steam runs one copy of the game per signed-in session.
        foreach (var shared in Clients.GroupBy(client => client.Value.Host).Where(group => group.Count() > 1))
            errors.Add($"Clients {string.Join(", ", shared.Select(client => client.Key))} share host '{shared.Key}'; a host runs one game client.");
        foreach (var shared in roles.GroupBy(role => (role.Value.Host, role.Value.CliPort)).Where(group => group.Count() > 1))
            errors.Add($"{string.Join(" and ", shared.Select(role => role.Role))} use the same ValheimCLI port {shared.Key.CliPort} on host '{shared.Key.Host}'.");
        // The ports each role is reached on here: a local host's and a (host-network) container's CLI port is this machine's
        // own, and an ssh role's tunnel listens on its localCliPort. Two roles must never meet on one.
        var here = roles.Select(role => (role.Role, Port: Hosts.TryGetValue(role.Value.Host ?? "", out var roleHost)
                ? ((roleHost.Kind is "local" or "container") ? role.Value.CliPort : role.Value.LocalCliPort) : 0))
            .Where(role => role.Port != 0);
        foreach (var shared in here.GroupBy(role => role.Port).Where(group => group.Count() > 1))
            errors.Add($"{string.Join(" and ", shared.Select(role => role.Role))} would all be reached on local port {shared.Key}.");
        SteamAccounts?.Validate(this, errors);
        if (SteamAccounts == null)
            foreach (var (name, client) in Clients.Where(client => client.Value.SteamAccount != null))
                errors.Add($"The client {name} names Steam account {client.SteamAccount}, but the environment has no Steam leases to lease it from.");
        if (Server?.SteamAccount != null) errors.Add("The server's steamAccount: a dedicated server needs no Steam account; only clients name one.");
        if (errors.Count != 0) throw new ArgumentException("Invalid environment: " + string.Join(" ", errors));
    }

    /// <summary>The named host, ready to use. A local host must describe this machine's platform.</summary>
    public IGameHost CreateHost(string name)
    {
        if (!Hosts.TryGetValue(name, out var host)) throw new ArgumentException($"No host '{name}' in the environment.", nameof(name));
        var errors = new List<string>();
        host.Validate(name, errors);
        if (errors.Count != 0) throw new ArgumentException(string.Join(" ", errors));
        var shell = HostShell.Parse(host.Shell);
        return host.Kind switch
        {
            "local" => host.Platform == HostProfile.CurrentPlatform ? new LocalGameHost(name, shell)
                : throw new PlatformNotSupportedException($"Host '{name}' is a local {host.Platform} host, but this machine is {HostProfile.CurrentPlatform}."),
            "ssh" => new SshGameHost(name, host.Destination!, shell, host.Port, host.SshOptions, TimeSpan.FromSeconds(host.ConnectSeconds), host.Ssh),
            _ => new ContainerGameHost(name, host.Container!, shell, host.User, host.Docker),
        };
    }
    /// <summary>The server's host.</summary>
    public IGameHost CreateServerHost() => CreateHost((Server ?? throw new InvalidOperationException("The environment places no server.")).Host);
    /// <summary>The named client's host.</summary>
    public IGameHost CreateClientHost(string client) =>
        CreateHost(Clients.TryGetValue(client, out var role) ? role.Host : throw new ArgumentException($"No client '{client}' in the environment.", nameof(client)));
}

/// <summary>
/// The clients' Steam leases: the one host and directory the leases live on (an inventory's <c>leaseHost</c> and
/// <c>leaseDirectory</c>, shared by every run on these accounts), and the pool of identities observed signed in on the
/// clients' hosts. Each client leases its identity before it starts (<see cref="SteamAccountHold"/>), holds it while it runs
/// and releases it after teardown; before it starts, its host's signed-in user is checked against it.
/// </summary>
internal sealed class SteamAccountsProfile
{
    /// <summary>The environment host that keeps the leases.</summary>
    public string LeaseHost { get; set; } = "";
    /// <summary>Whether a client's host must be signed in to its leased identity before it starts; false only in controlled tests.</summary>
    public bool CheckSignedIn { get; set; } = true;
    /// <summary>The observed identities, once the host preflight read them (<see cref="HostedCampaignPreparation"/>).</summary>
    public SteamAccountPool? Accounts { get; set; }
    // Set by inventory resolution, before the host preflight observed any identity; preparation replaces it with Accounts.
    public string? ObservedLeaseDirectory { get; set; }
    /// <summary>
    /// Every host an inventory client recipe names, set by inventory resolution: preflight asks each one the campaign does not
    /// already check whether Valheim runs there on an account a client would use (#257), since a Steam account plays on one computer at a time.
    /// </summary>
    public List<string> InventoryClientHosts { get; set; } = [];

    internal void Validate(ResolvedEnvironment profile, List<string> errors)
    {
        if (!profile.Hosts.TryGetValue(LeaseHost ?? "", out var leaseHost)) errors.Add($"Steam leases: the lease host '{LeaseHost}' is not listed under hosts.");
        foreach (string host in InventoryClientHosts.Where(host => !profile.Hosts.ContainsKey(host)))
            errors.Add($"Steam leases: the inventory client host '{host}' is not listed under hosts.");
        if (ObservedLeaseDirectory != null)
        {
            if (!CheckSignedIn) errors.Add("Steam leases: observed identities require the signed-in check.");
            if (Accounts != null) errors.Add("Steam leases: identities not yet observed cannot also have an account pool.");
            if (leaseHost != null && !leaseHost.IsAbsolutePath(ObservedLeaseDirectory))
                errors.Add("Steam leases: the lease directory must be an absolute path on the lease host.");
            if (profile.Clients.Values.Any(client => client.SteamAccount != null))
                errors.Add("Steam leases: identities not yet observed must not preselect a client account.");
            return;
        }
        if (profile.Clients.Count == 0) errors.Add("Steam leases: list the clients that lease accounts under clients.");
        if (Accounts == null) { errors.Add("Steam leases: no observed identities."); return; }
        try { Accounts.Validate(); }
        catch (ArgumentException error) { errors.Add("Steam leases: " + error.Message); return; }
        if (leaseHost != null && !leaseHost.IsAbsolutePath(Accounts.LeaseDirectory))
            errors.Add($"Steam leases: the lease directory must be an absolute path on the lease host '{LeaseHost}'.");
        foreach (var (name, client) in profile.Clients)
        {
            var candidates = Candidates(client);
            if (client.SteamAccount != null && candidates.Count == 0)
                errors.Add($"The client {name} names Steam identity {client.SteamAccount}, which the observed identities do not list.");
            else if (candidates.Count == 0) errors.Add($"No observed Steam identity is for the client {name}'s host '{client.Host}'.");
            if (CheckSignedIn)
                foreach (var account in candidates.Where(account => account.SteamId == null))
                    errors.Add($"The signed-in check compares the client {name}'s signed-in Steam user with identity {account.Name}, which has no SteamID64.");
        }
        foreach (var shared in profile.Clients.Where(client => client.Value.SteamAccount != null)
                     .GroupBy(client => client.Value.SteamAccount!, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            errors.Add($"Clients {string.Join(", ", shared.Select(client => client.Key))} name the same Steam identity {shared.Key}; an account runs one client at a time.");
    }

    /// <summary>The accounts <paramref name="client"/> may lease: the one it names, or every account for its host or for any host.</summary>
    internal IReadOnlyList<SteamPoolAccount> Candidates(GameRole client) => (Accounts?.Accounts ?? [])
        .Where(account => (client.SteamAccount == null || string.Equals(account.Name, client.SteamAccount, StringComparison.OrdinalIgnoreCase))
            && (account.Host == null || account.Host == client.Host)).ToList();
}
