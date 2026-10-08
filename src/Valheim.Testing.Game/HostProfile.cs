using System.Text.RegularExpressions;

namespace Valheim.Testing.Game;

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
    /// <summary>ssh: extra <c>-o</c> options as <c>Name=value</c>, the value taken literally (spaces, quotes and backslashes are passed quoted for ssh; a ProxyCommand, KnownHostsCommand or LocalCommand as written, since ssh runs it with the user's shell).</summary>
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
            Collect(errors, where, () => SshChecks.CheckDestination(Destination));
            foreach (string option in SshOptions ?? []) Collect(errors, where, () => SshChecks.CheckOption(option));
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
internal sealed class GameRole
{
    /// <summary>The name of a host in the environment.</summary>
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
    /// <summary>The selected client slice; a command-line option may override it.</summary>
    public string Architecture { get; set; } = "x64";
    /// <summary>Clients only: the lease key of the Steam identity observed signed in on its host (an opaque hash; never the SteamID).</summary>
    public string? SteamAccount { get; set; }

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

/// <summary>An ssh host's destination and <c>-o</c> options, checked where an inventory is read and again where the host is made.</summary>
internal static class SshChecks
{
    private static readonly Regex OptionName = new("^[A-Za-z]+$", RegexOptions.CultureInvariant);
    // Always set first (ssh keeps the first value it reads for an option), so a caller's option cannot switch them off.
    private static readonly string[] FixedOptions = ["BatchMode", "ConnectTimeout", "ExitOnForwardFailure", "GatewayPorts", "ClearAllForwardings"];
    // A caller never adds listeners, picks the port twice or replaces the scripts the host runs.
    private static readonly string[] RefusedOptions = ["LocalForward", "RemoteForward", "DynamicForward", "Port", "RemoteCommand"];

    internal static string CheckDestination(string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        if (destination.StartsWith('-') || destination.Any(ch => char.IsWhiteSpace(ch) || char.IsControl(ch)))
            throw new ArgumentException("An ssh destination is one word that does not start with '-'.", nameof(destination));
        // user:password@host is not an ssh form; a colon belongs only in ssh://user@host:port.
        if (destination.Contains(':') && !destination.StartsWith("ssh://", StringComparison.Ordinal))
            throw new ArgumentException("Use user@host, ssh://user@host:port or an ssh-config alias. Passwords are never accepted; use keys or an agent.", nameof(destination));
        return destination;
    }

    internal static string CheckOption(string option)
    {
        int equals = option?.IndexOf('=') ?? -1;
        if (option == null || equals <= 0 || !OptionName.IsMatch(option[..equals]))
            throw new ArgumentException($"An ssh option is Name=value, for example IdentityFile=/path/key; got '{option}'.", nameof(option));
        string name = option[..equals];
        if (equals == option.Length - 1)
            throw new ArgumentException($"The ssh option {name} has no value.", nameof(option));
        if (option.Any(char.IsControl))
            throw new ArgumentException($"The ssh option {name} has a control character (such as a tab or a line break) in its value, which an ssh option cannot carry.", nameof(option));
        if (FixedOptions.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException(name + " is set by SshGameHost and cannot be overridden.", nameof(option));
        if (RefusedOptions.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException(name + " is refused: " + name.ToLowerInvariant() switch
            {
                "port" => "pass the port setting instead.",
                "remotecommand" => "the host runs its own scripts.",
                _ => "the host opens only its own loopback CLI tunnel.",
            }, nameof(option));
        return option;
    }
}

/// <summary>Paths on a host, which may not be this machine's platform.</summary>
internal static class HostPath
{
    /// <summary>A path on a host under <paramref name="root"/>: joined with <c>\</c> when the root is a Windows path, else <c>/</c>.</summary>
    internal static string Join(string root, params string[] parts)
    {
        bool windows = root.Contains('\\') || Regex.IsMatch(root, "^[A-Za-z]:");
        char separator = windows ? '\\' : '/';
        string path = root.Length > 1 ? root.TrimEnd('/', '\\') : root;
        foreach (string part in parts) path += (path.EndsWith(separator) ? "" : separator.ToString()) + (windows ? part.Replace('/', '\\') : part);
        return path;
    }
}
