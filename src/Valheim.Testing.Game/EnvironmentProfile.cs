using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Valheim.Testing.Game;

/// <summary>
/// Where a test run's game processes live: the hosts, which one runs the dedicated server and which runs each client, their
/// install and runtime paths and their ports. A JSON file kept beside (not inside) a mod's tests, because it describes one
/// developer's machines; <see cref="Read"/> refuses unknown fields and <see cref="Validate"/> refuses an inconsistent profile
/// before anything is started. It holds no credentials: SSH uses keys or an agent, named through the user's own ssh config.
/// </summary>
public sealed class EnvironmentProfile
{
    private static readonly Regex Name = new("^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
    };

    public Dictionary<string, HostProfile> Hosts { get; set; } = [];
    /// <summary>The dedicated server, if the run has one.</summary>
    public GameRole? Server { get; set; }
    /// <summary>The game clients by name.</summary>
    public Dictionary<string, GameRole> Clients { get; set; } = [];

    /// <summary>Reads and validates a profile file.</summary>
    public static EnvironmentProfile Read(string path) => Parse(File.ReadAllText(path));
    public static EnvironmentProfile Parse(string json)
    {
        var profile = JsonSerializer.Deserialize<EnvironmentProfile>(json, Json) ?? throw new ArgumentException("Empty environment profile.");
        profile.Validate();
        return profile;
    }

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
        if (Server != null && Hosts.TryGetValue(Server.Host ?? "", out var serverHost) && serverHost.Platform == "macos")
            errors.Add($"The server's host '{Server.Host}' is macOS, which has no dedicated server; use a Linux container or a Windows or Linux host.");
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
        if (errors.Count != 0) throw new ArgumentException("Invalid environment profile: " + string.Join(" ", errors));
    }

    /// <summary>The named host, ready to use. A local host must describe this machine's platform.</summary>
    public IGameHost CreateHost(string name)
    {
        if (!Hosts.TryGetValue(name, out var host)) throw new ArgumentException($"No host '{name}' in the profile.", nameof(name));
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
    public IGameHost CreateServerHost() => CreateHost((Server ?? throw new InvalidOperationException("The profile names no server.")).Host);
    /// <summary>The named client's host.</summary>
    public IGameHost CreateClientHost(string client) =>
        CreateHost(Clients.TryGetValue(client, out var role) ? role.Host : throw new ArgumentException($"No client '{client}' in the profile.", nameof(client)));
}

/// <summary>One machine or container. <see cref="Kind"/> is <c>local</c>, <c>ssh</c> or <c>container</c>.</summary>
public sealed class HostProfile
{
    internal static string CurrentPlatform => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    public string Kind { get; set; } = "";
    /// <summary><c>windows</c>, <c>linux</c> or <c>macos</c>: the host's own OS (a container's is linux).</summary>
    public string Platform { get; set; } = "";
    /// <summary><c>bash</c>, <c>pwsh</c> or <c>powershell</c> (Windows PowerShell 5.1, Windows only).</summary>
    public string Shell { get; set; } = "";
    /// <summary>The host lock's directory, an absolute path on the host. Every run takes it before touching the host.</summary>
    public string Lock { get; set; } = "";
    /// <summary>ssh: <c>user@host</c>, <c>ssh://user@host:port</c> or an ssh-config alias.</summary>
    public string? Destination { get; set; }
    /// <summary>ssh: the port; 0 leaves it to the destination or the ssh config.</summary>
    public int Port { get; set; }
    /// <summary>ssh: extra <c>-o</c> options as <c>Name=value</c>, the value taken literally (spaces, quotes and backslashes are passed quoted for ssh).</summary>
    public string[] SshOptions { get; set; } = [];
    public int ConnectSeconds { get; set; } = 10;
    /// <summary>ssh: the OpenSSH client executable.</summary>
    public string Ssh { get; set; } = "ssh";
    /// <summary>container: its name or id on this machine's Docker daemon.</summary>
    public string? Container { get; set; }
    /// <summary>container: the user scripts run as.</summary>
    public string? User { get; set; }
    /// <summary>container: the Docker CLI executable.</summary>
    public string Docker { get; set; } = "docker";

    internal void Validate(string name, List<string> errors)
    {
        string where = $"Host '{name}'";
        if (Kind is not ("local" or "ssh" or "container")) errors.Add($"{where}: kind must be local, ssh or container.");
        if (Platform is not ("windows" or "linux" or "macos")) errors.Add($"{where}: platform must be windows, linux or macos.");
        if (Shell is not ("bash" or "pwsh" or "powershell")) errors.Add($"{where}: shell must be bash, pwsh or powershell.");
        else if (Shell == "powershell" && Platform != "windows") errors.Add($"{where}: Windows PowerShell (powershell) runs only on Windows; use pwsh.");
        else if (Shell == "bash" && Platform == "windows") errors.Add($"{where}: bash on Windows (WSL or Git Bash) sees other paths and tools; use powershell or pwsh.");
        if (Kind == "container" && Platform != "linux") errors.Add($"{where}: a container host's platform is linux.");
        if ((Kind == "ssh") == string.IsNullOrWhiteSpace(Destination)) errors.Add($"{where}: " + (Kind == "ssh" ? "an ssh host needs a destination." : "only an ssh host has a destination."));
        if ((Kind == "container") == string.IsNullOrWhiteSpace(Container)) errors.Add($"{where}: " + (Kind == "container" ? "a container host needs a container." : "only a container host has a container."));
        if (Kind != "ssh" && (Port != 0 || SshOptions.Length != 0)) errors.Add($"{where}: port and sshOptions belong to an ssh host.");
        if (Kind != "container" && User != null) errors.Add($"{where}: user belongs to a container host.");
        if (Port is < 0 or > 65535) errors.Add($"{where}: port must be 0 to 65535.");
        if (Kind == "ssh" && !string.IsNullOrWhiteSpace(Destination))
        {
            if (Port != 0 && Destination.StartsWith("ssh://", StringComparison.Ordinal)) errors.Add($"{where}: give the port once, in the ssh:// destination or as port.");
            Collect(errors, where, () => SshGameHost.CheckDestination(Destination));
            foreach (string option in SshOptions ?? []) Collect(errors, where, () => SshGameHost.CheckOption(option));
        }
        if (ConnectSeconds is < 1 or > 300) errors.Add($"{where}: connectSeconds must be 1 to 300.");
        if (!IsAbsolutePath(Lock)) errors.Add($"{where}: lock must be an absolute path on the host.");
    }

    private static void Collect(List<string> errors, string where, Action check)
    {
        try { check(); }
        catch (ArgumentException error) { errors.Add($"{where}: {error.Message.Split(" (Parameter", 2)[0]}"); }
    }

    /// <summary>A path on this host that does not depend on a home or working directory: drive or UNC on Windows, rooted elsewhere.</summary>
    internal bool IsAbsolutePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && !path.Any(char.IsControl) &&
        (Platform == "windows" ? Regex.IsMatch(path, @"^([A-Za-z]:[\\/]|\\\\)") : path.StartsWith('/'));
}

/// <summary>A server or client: the host it runs on, its install and runtime directories there, and its ports.</summary>
public sealed class GameRole
{
    /// <summary>The name of a host in the profile.</summary>
    public string Host { get; set; } = "";
    /// <summary>The prepared game install (with BepInEx) on the host. Runs copy from it and never change it.</summary>
    public string Install { get; set; } = "";
    /// <summary>The directory on the host under which each run makes its own disposable runtime copy, logs and evidence.</summary>
    public string Runtime { get; set; } = "";
    /// <summary>The ValheimCLI port on the host's loopback.</summary>
    public int CliPort { get; set; }
    /// <summary>Server only: the game's UDP port (Valheim also uses the next one).</summary>
    public int GamePort { get; set; }
    /// <summary>The local end of the CLI tunnel; 0 picks a free port.</summary>
    public int LocalCliPort { get; set; }

    internal void Validate(string role, HostProfile host, List<string> errors)
    {
        string where = "The " + role;
        if (!host.IsAbsolutePath(Install)) errors.Add($"{where}: install must be an absolute path on host '{Host}'.");
        if (!host.IsAbsolutePath(Runtime)) errors.Add($"{where}: runtime must be an absolute path on host '{Host}'.");
        else if (host.IsAbsolutePath(Install) && Inside(Runtime, Install, host.Platform == "windows"))
            errors.Add($"{where}: runtime must be outside the install, which runs never change.");
        if (CliPort is < 1024 or > 65535) errors.Add($"{where}: cliPort must be 1024 to 65535.");
        if (LocalCliPort != 0 && LocalCliPort is < 1024 or > 65535) errors.Add($"{where}: localCliPort must be 0 or 1024 to 65535.");
        if (host.Kind is "local" or "container" && LocalCliPort != 0 && LocalCliPort != CliPort)
            errors.Add($"{where}: a {host.Kind} host's CLI is reached on cliPort itself; leave localCliPort 0.");
        bool server = role == "server";
        if (server && GamePort is < 1024 or > 65534) errors.Add($"{where}: gamePort must be 1024 to 65534.");
        if (!server && GamePort != 0) errors.Add($"{where}: only the server has a gamePort.");
    }

    private static bool Inside(string path, string root, bool windows)
    {
        var comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string Trim(string value) => windows ? value.Replace('\\', '/').TrimEnd('/') : value.TrimEnd('/');
        string child = Trim(path), parent = Trim(root);
        return child.Equals(parent, comparison) || child.StartsWith(parent + "/", comparison);
    }
}
