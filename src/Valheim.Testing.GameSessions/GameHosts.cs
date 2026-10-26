using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>
/// This machine. Scripts run in a local shell process (pwsh on Windows, bash elsewhere), so the same lock, ship, log and fetch
/// scripts run as on a remote host. The only transport failure is a shell that cannot be started.
/// </summary>
public sealed class LocalGameHost : ScriptedGameHost
{
    /// <exception cref="ArgumentException">PowerShell on macOS or Linux: those hosts run bash.</exception>
    public LocalGameHost(string name, HostShell shell) : this(name, UnixBash(shell, !OperatingSystem.IsWindows(), "this machine"), SystemProcessLauncher.Instance) { }
    internal LocalGameHost(string name, HostShell shell, IProcessLauncher launcher) : base(name, shell, launcher) { }
    public override GameHostKind Kind => GameHostKind.Local;

    // Test seam: the host ThisMachine returns in this flow instead of a shell on this machine.
    private static readonly AsyncLocal<IGameHost?> s_thisMachineForTest = new();

    /// <summary>
    /// This machine as every owned path that runs here reaches it (a local client's profile and its source settlement, its hosted
    /// world, the process checks around its launch): a <see cref="LocalGameHost"/> on this machine's shell, unless a test replaced it
    /// for its flow (<see cref="UseThisMachineForTest"/>).
    /// </summary>
    internal static IGameHost ThisMachine() => s_thisMachineForTest.Value ?? new LocalGameHost("this machine", HostShell.ThisMachine);

    /// <summary>Test seam: <see cref="ThisMachine"/> returns <paramref name="host"/> in this flow until the scope is disposed.</summary>
    internal static IDisposable UseThisMachineForTest(IGameHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var previous = s_thisMachineForTest.Value;
        s_thisMachineForTest.Value = host;
        return new ThisMachineScope(previous);
    }

    private sealed class ThisMachineScope(IGameHost? previous) : IDisposable
    {
        public void Dispose() => s_thisMachineForTest.Value = previous;
    }
    internal override (string Executable, IReadOnlyList<string> Arguments) WrapperCommand() => (Shell.Executable, WrapperArguments(Shell));
    internal override bool IsTransportFailure(ProcessExit exit) => false;
    // A failure to launch can also mean access denial, an invalid cwd or resource pressure. Only the native missing-file
    // classification for this shell justifies install advice; an injected/legacy result without that evidence does not.
    internal override bool ShellNotFound(ProcessExit exit) => exit.End == ProcessEnd.NotStarted &&
        exit.StartFailure is { Kind: ProcessStartFailureKind.ExecutableNotFound } failure &&
        string.Equals(failure.Executable, Shell.Executable, StringComparison.OrdinalIgnoreCase);

    /// <summary>The host's loopback is this machine's: nothing is started and the endpoint is the ValheimCLI port itself.</summary>
    public override Task<CliTunnel> OpenCliTunnelAsync(int hostPort, TimeSpan readyTimeout, int localPort = 0, CancellationToken cancellation = default)
    {
        GameHostPorts.Check(hostPort, nameof(hostPort));
        WaitText.RequireTimeout(readyTimeout);
        if (localPort != 0 && localPort != hostPort)
            throw new ArgumentException($"A local host's ValheimCLI is reached on its own port {hostPort}; nothing forwards {localPort}.", nameof(localPort));
        return Task.FromResult(new CliTunnel(null, hostPort, hostPort));
    }
}

/// <summary>
/// A machine reached through OpenSSH (<c>ssh</c> from PATH; on Windows the inbox client, or Git for Windows' client when the runner itself is in an SSH session) with key or agent authentication
/// only: BatchMode is always on, so ssh never prompts, and nothing here accepts a password. The destination is <c>user@host</c>,
/// <c>ssh://user@host:port</c> or an ssh-config alias; the host key must already be known (or pass <c>StrictHostKeyChecking=accept-new</c>).
/// A bash host needs a POSIX login shell; a Windows host's pwsh works under any login shell, including Windows' cmd.exe.
/// ssh exit 255 without the host's exit report is a transport failure.
/// </summary>
public sealed class SshGameHost : ScriptedGameHost
{
    // ssh takes these values as the rest of the line, quotes included, and runs them with the user's shell: quoting would make
    // the whole command one word (zsh: "no such file or directory: ssh -i ... -W %h:%p").
    private static readonly string[] CommandOptions = ["ProxyCommand", "KnownHostsCommand", "LocalCommand"];
    private readonly string[] _options;
    private readonly string _ssh;
    private readonly string _sshReason;

    /// <param name="destination"><c>user@host</c>, <c>ssh://user@host:port</c> or an ssh-config alias. Never a password.</param>
    /// <param name="port">The ssh port; 0 leaves it to the destination or the ssh config.</param>
    /// <param name="sshOptions">
    /// Extra <c>-o</c> options as <c>Name=value</c>, for example <c>IdentityFile=/path/key</c>; the value is taken literally and may
    /// contain spaces, quotes and backslashes (<c>User=Some Name</c>, <c>IdentityFile=C:\Users\Some Name\.ssh\key</c>). ssh reads a
    /// <c>-o</c> value like a config line, so such a value is passed as <c>Name="value"</c>, escaped the way ssh reads it. The value
    /// must not be empty or contain a control character such as a tab or a line break. The space-separated <c>Name value</c> form is
    /// not accepted. A command (<c>ProxyCommand=ssh -W %h:%p jump</c>) is passed as written: ssh runs it with the user's shell.
    /// RemoteCommand is refused.
    /// </param>
    /// <param name="connectTimeout">ssh's ConnectTimeout; 10 s by default.</param>
    /// <param name="sshExecutable">The OpenSSH client. The default is <c>ssh</c> from PATH, which on Windows is the inbox client and reaches keys
    /// held by the Windows ssh-agent service. When the runner itself is inside an SSH session on Windows, the default is instead Git for Windows'
    /// matching ssh/scp pair, because the inbox ssh can hang there when a .NET process sends its script through redirected stdin. Git's msys ssh cannot
    /// open the named pipe that carries a forwarded agent in a Win32-OpenSSH session, so a host reached from such a session needs a key file (<c>IdentityFile=</c>).
    /// Anything but the bare default name, a path above all, is an explicit choice and always wins. On Windows <c>ssh</c>, <c>ssh.exe</c> and any capitalisation of either are the default, so
    /// forcing the inbox client from inside an SSH session takes its full path. A run's provenance records which client was chosen and why.</param>
    public SshGameHost(string name, string destination, HostShell shell, int port = 0, IEnumerable<string>? sshOptions = null, TimeSpan? connectTimeout = null, string sshExecutable = "ssh")
        : this(name, destination, shell, port, sshOptions, connectTimeout, sshExecutable, SystemProcessLauncher.Instance) { }

    internal SshGameHost(string name, string destination, HostShell shell, int port, IEnumerable<string>? sshOptions, TimeSpan? connectTimeout, string sshExecutable, IProcessLauncher launcher,
        (string Executable, string Reason)? clientChoice = null)
        : base(name, shell, launcher)
    {
        Destination = SshChecks.CheckDestination(destination);
        if (port != 0) GameHostPorts.Check(port, nameof(port));
        if (port != 0 && Destination.StartsWith("ssh://", StringComparison.Ordinal)) throw new ArgumentException("Give the port once: in the ssh:// destination or as the port.", nameof(port));
        Port = port;
        ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(10);
        if (ConnectTimeout < TimeSpan.FromSeconds(1)) throw new ArgumentOutOfRangeException(nameof(connectTimeout), "Use a connect timeout of at least one second.");
        ArgumentException.ThrowIfNullOrWhiteSpace(sshExecutable);
        // Tests can carry a choice from DefaultSshExecutable through a fake launcher without depending on this machine.
        (_ssh, _sshReason) = clientChoice ?? (ReferenceEquals(launcher, SystemProcessLauncher.Instance)
            ? DefaultSshExecutable(sshExecutable, OperatingSystem.IsWindows(), SshSession.IsActive(),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), File.Exists)
            : (sshExecutable, "set by the caller"));
        _options = (sshOptions ?? []).Select(option => OptionArgument(SshChecks.CheckOption(option))).ToArray();
    }

    /// <summary>The ssh client and why it was chosen, one line. Reported in the run's provenance.</summary>
    internal string SshClientChoice => _ssh + " (" + _sshReason + ")";

    // Last, so HostResult.Describe keeps the remedy when it shortens long stderr. No options or second command.
    internal override string FailureContext(string text) => text + "\nSSH client: " + SshClientChoice;

    // The bare client name, which asks for the default rather than a client: "ssh", and on Windows also "ssh.exe", in any case (the file
    // system and the command search are case-insensitive there). Anything else (a path above all) is an explicit choice.
    private static bool IsDefaultSshName(string requested, bool windows) =>
        string.Equals(requested, "ssh", windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
        windows && string.Equals(requested, "ssh.exe", StringComparison.OrdinalIgnoreCase);

    // One owner of the choice. Git for Windows' client only when the runner is itself in an SSH session (where the inbox client hangs on
    // redirected stdin, #635): Git's msys ssh cannot use the Windows ssh-agent pipe, so choosing it elsewhere breaks agent-held keys (#661).
    internal static (string Executable, string Reason) DefaultSshExecutable(string requested, bool windows, bool inSshSession, string programFiles, string localAppData,
        Func<string, bool> exists)
    {
        if (!IsDefaultSshName(requested, windows)) return (requested, "the host's configured ssh path");
        if (!windows) return (requested, "ssh from PATH");
        if (!inSshSession) return (requested, "ssh from PATH, the inbox client unless PATH puts another first: this runner is not in an SSH session, and keys in the Windows ssh-agent work only with it");
        foreach (string root in new[] { Path.Combine(programFiles, "Git"), Path.Combine(localAppData, "Programs", "Git") })
        {
            string ssh = Path.Combine(root, "usr", "bin", "ssh.exe");
            string scp = Path.Combine(root, "usr", "bin", "scp.exe");
            if (exists(ssh) && exists(scp))
                return (ssh, "Git for Windows' ssh: this runner is in an SSH session, where the inbox ssh can hang; Git's client cannot use the Windows ssh-agent (a forwarded agent in a Win32-OpenSSH session is a named pipe it cannot open), so the host needs a key file (IdentityFile)");
        }
        throw new InvalidOperationException("Windows' inbox ssh can hang while receiving a ValheimTesting script from a .NET runner in an SSH session. " +
            "Install Git for Windows (its ssh.exe and scp.exe are used together), or set this host's ssh field to a tested OpenSSH client path before staging. " +
            "Either way, Git's client cannot open the named pipe that carries a forwarded agent in a Win32-OpenSSH session, so give the host a key file (IdentityFile=).");
    }

    public override GameHostKind Kind => GameHostKind.Ssh;
    public string Destination { get; }
    public int Port { get; }
    public TimeSpan ConnectTimeout { get; }

    internal override (string Executable, IReadOnlyList<string> Arguments) WrapperCommand() => (_ssh, SshArguments(forward: false, ["-T"], RemoteCommand(Shell)));
    internal override bool IsTransportFailure(ProcessExit exit) => exit.ExitCode == 255;
    // The login shell could not find the command: cmd.exe (OpenSSH's default on Windows) exits 9009, which an ssh client on macOS
    // or Linux reports as its low byte, 49; a POSIX shell exits 127. Either way the wrapper never ran, so it wrote nothing. The
    // English words cover Windows PowerShell as the login shell (exit 1) too. A localised cmd.exe is caught by its exit code; a
    // localised Windows PowerShell login shell is not (exit 1 says nothing), and the outcome stays a plain unknown.
    internal override bool ShellNotFound(ProcessExit exit) => exit.End == ProcessEnd.Exited && (Regex.IsMatch(exit.Stderr,
        "'" + Regex.Escape(Shell.Executable) + "' is not recognized as|" + Regex.Escape(Shell.Executable) + ": (command )?not found", RegexOptions.CultureInvariant) ||
        (exit.ExitCode is 9009 or 9009 % 256 or 127 && exit.Stdout.Length == 0));

    /// <summary>
    /// Forwards a local loopback port to the host's loopback-only ValheimCLI port (<c>ssh -N -L 127.0.0.1:local:127.0.0.1:host</c>
    /// with ExitOnForwardFailure). <paramref name="localPort"/> 0 picks a free port; a port already in use is refused before ssh
    /// starts. Ready means a local connection succeeded within <paramref name="readyTimeout"/>: ssh listens here. Whether the host's
    /// port answers is the next check, made through the tunnel. Neither end ever listens on a non-loopback address: ssh would also
    /// open the LocalForward, RemoteForward and DynamicForward entries an ssh config gives this host (ClearAllForwardings would
    /// clear the tunnel's own forward too), so a host whose effective config (<c>ssh -G</c>) has any is refused.
    /// <para>
    /// The free-port check and ssh's own bind are separate steps: another process can take the port in between. ssh then fails
    /// to bind and exits (ExitOnForwardFailure), which fails the tunnel, but a readiness probe made before ssh has exited could
    /// reach that other listener. Pass a <paramref name="localPort"/> reserved for tests on a shared machine.
    /// </para>
    /// </summary>
    public override async Task<CliTunnel> OpenCliTunnelAsync(int hostPort, TimeSpan readyTimeout, int localPort = 0, CancellationToken cancellation = default)
    {
        GameHostPorts.Check(hostPort, nameof(hostPort));
        if (localPort != 0) GameHostPorts.Check(localPort, nameof(localPort));
        WaitText.RequireTimeout(readyTimeout);
        int port = localPort == 0 ? GameHostPorts.FreeLoopbackPort() : GameHostPorts.RequireFreeLoopbackPort(localPort);
        await RefuseConfiguredForwardsAsync(readyTimeout, cancellation).ConfigureAwait(false);
        var process = Launcher.Start(_ssh, SshArguments(forward: true, ["-N", "-n", "-T", "-L", ForwardSpec(port, hostPort)], null));
        try
        {
            await GameHostPorts.WaitUntilListeningAsync(process, port, readyTimeout, cancellation, FailureContext).ConfigureAwait(false);
            return new CliTunnel(process, port, hostPort);
        }
        catch
        {
            try { process.Stop(TimeSpan.FromSeconds(10)); } finally { process.Dispose(); }
            throw;
        }
    }

    /// <summary>Uploads one archive into the SSH user's home using SFTP/SCP, avoiding the binary stdin pipe some Windows SSH sessions stop reading.</summary>
    internal async Task UploadArchiveAsync(string archive, string remoteName, TimeSpan timeout, CancellationToken cancellation)
    {
        if (!Regex.IsMatch(remoteName, @"^vt-upload-[0-9a-f]{32}\.tar$", RegexOptions.CultureInvariant))
            throw new ArgumentException("An upload name must be generated by the campaign.", nameof(remoteName));
        string scp = Path.IsPathRooted(_ssh) ? Path.Combine(Path.GetDirectoryName(_ssh)!, OperatingSystem.IsWindows() ? "scp.exe" : "scp") : "scp";
        var arguments = new List<string>
        {
            "-q", "-o", "BatchMode=yes", "-o", "ConnectTimeout=" + ((int)Math.Ceiling(ConnectTimeout.TotalSeconds)).ToString(CultureInfo.InvariantCulture),
            "-o", "ClearAllForwardings=yes",
        };
        foreach (string option in _options) arguments.AddRange(["-o", option]);
        string destination = Destination;
        int port = Port;
        if (destination.StartsWith("ssh://", StringComparison.Ordinal))
        {
            var uri = new Uri(destination);
            destination = (uri.UserInfo.Length == 0 ? "" : uri.UserInfo + "@") +
                (uri.Host.Contains(':') ? "[" + uri.Host + "]" : uri.Host);
            if (!uri.IsDefaultPort) port = uri.Port;
        }
        if (port != 0) arguments.AddRange(["-P", port.ToString(CultureInfo.InvariantCulture)]);
        arguments.AddRange(["--", archive, destination + ":" + remoteName]);
        var exit = await Launcher.RunAsync(new ProcessCall(scp, arguments, [], null, null, null, timeout), cancellation).ConfigureAwait(false);
        if (exit.End != ProcessEnd.Exited || exit.ExitCode != 0)
            throw new IOException(FailureContext($"The archive transfer to {Name} was not proven ({exit.End}, exit {exit.ExitCode}). Inspect {remoteName} in the SSH user's home before retrying. {exit.Stderr.Trim()}"));
    }

    // ssh -G prints the configuration ssh would use for this destination, forwards from the user's and the system's config included.
    private async Task RefuseConfiguredForwardsAsync(TimeSpan timeout, CancellationToken cancellation)
    {
        var exit = await Launcher.RunAsync(new ProcessCall(_ssh, SshArguments(forward: true, ["-G"], null), [], null, null, null, timeout), cancellation).ConfigureAwait(false);
        if (exit.End != ProcessEnd.Exited || exit.ExitCode != 0)
            throw new InvalidOperationException(FailureContext($"Could not read the ssh configuration for {Destination} ({(exit.End == ProcessEnd.Exited ? "exit " + exit.ExitCode : exit.End.ToString())}): {exit.Stderr.Trim()}"));
        var forwards = ConfiguredForwards(exit.Stdout);
        if (forwards.Count != 0)
            throw new InvalidOperationException($"The ssh configuration for {Destination} adds forwards that a tunnel would also open: {string.Join("; ", forwards)}. " +
                "Use a Host entry or alias without LocalForward, RemoteForward or DynamicForward for tests.");
    }

    internal static IReadOnlyList<string> ConfiguredForwards(string sshG) =>
        sshG.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("localforward ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("remoteforward ", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("dynamicforward ", StringComparison.OrdinalIgnoreCase))
            .ToList();

    internal static string ForwardSpec(int localPort, int hostPort) =>
        "127.0.0.1:" + localPort.ToString(CultureInfo.InvariantCulture) + ":127.0.0.1:" + hostPort.ToString(CultureInfo.InvariantCulture);

    /// <summary>The ssh arguments, fixed options first. A command follows <c>--</c> and the destination, so it is never read as an option.</summary>
    internal IReadOnlyList<string> SshArguments(bool forward, IEnumerable<string> flags, string? command)
    {
        var arguments = new List<string>
        {
            "-o", "BatchMode=yes", "-o", "ConnectTimeout=" + ((int)Math.Ceiling(ConnectTimeout.TotalSeconds)).ToString(CultureInfo.InvariantCulture), "-o", "GatewayPorts=no",
            // A script run opens no forwards, not even ones an ssh config adds for this host.
            "-o", forward ? "ExitOnForwardFailure=yes" : "ClearAllForwardings=yes",
        };
        foreach (string option in _options) arguments.AddRange(["-o", option]);
        // Defaults a caller's option may override: a dead link is noticed within about 45 s.
        arguments.AddRange(["-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=3"]);
        if (Port != 0) arguments.AddRange(["-p", Port.ToString(CultureInfo.InvariantCulture)]);
        // No agent or X11 forwarding to a test host.
        arguments.AddRange(["-a", "-x"]);
        arguments.AddRange(flags);
        arguments.AddRange(["--", Destination]);
        if (command != null) arguments.Add(command);
        return arguments;
    }

    /// <summary>The fixed wrapper the host's login shell starts. It never contains the script or a value.</summary>
    internal static string RemoteCommand(HostShell shell) => shell.Kind == HostShellKind.Bash
        ? shell.Executable + " -c '" + HostScripts.BashWrapper + "'"
        : shell.Executable + " " + string.Join(' ', WrapperArguments(shell));

    /// <summary>
    /// The <c>-o</c> argument for a checked <c>Name=value</c> option. ssh parses it like a config line (OpenSSH 8.7 and later, on every
    /// OS): spaces separate words, single and double quotes group, a backslash escapes a quote, a backslash or (outside quotes) a space, and a word starting with <c>#</c> is a comment. A value that ssh would not read back unchanged is passed as
    /// <c>Name="value"</c>, with a backslash before each <c>"</c> and before each backslash that precedes a backslash, a quote or the
    /// closing quote. Any other value is passed unchanged. Older OpenSSH groups double quotes without escapes, so a value with spaces
    /// that needs no escape (a Windows path with a space, for example) reads the same there. ProxyCommand, KnownHostsCommand and
    /// LocalCommand are the exception: ssh takes their value as the rest of the line, quotes included, so it is passed unchanged.
    /// </summary>
    internal static string OptionArgument(string option)
    {
        int equals = option.IndexOf('=');
        if (CommandOptions.Contains(option[..equals], StringComparer.OrdinalIgnoreCase)) return option;
        string value = option[(equals + 1)..];
        if (!value.StartsWith('#') && value.IndexOfAny([' ', '"', '\'']) < 0 && !value.Contains(@"\\", StringComparison.Ordinal)) return option;
        var quoted = new StringBuilder(option, 0, equals + 1, option.Length + 8).Append('"');
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '"' || (value[i] == '\\' && (i + 1 == value.Length || value[i + 1] is '\\' or '"' or '\''))) quoted.Append('\\');
            quoted.Append(value[i]);
        }
        return quoted.Append('"').ToString();
    }
}

/// <summary>
/// A running container on this machine's Docker daemon, driven with <c>docker exec -i</c>. Scripts run as the given user in the
/// container's shell. A Docker error, or a shell the container lacks, without the host's exit report is a transport failure.
/// The container's ValheimCLI is reachable only when the container shares this machine's network (<c>--network host</c>, Linux):
/// a published port reaches the container's own interface, never its loopback, and the CLI must not listen anywhere else.
/// </summary>
public sealed class ContainerGameHost : ScriptedGameHost
{
    private static readonly Regex ContainerName = new("^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant);
    private static readonly Regex UserName = new("^[A-Za-z0-9_][A-Za-z0-9_.-]*(:[A-Za-z0-9_][A-Za-z0-9_.-]*)?$", RegexOptions.CultureInvariant);
    private static readonly string[] DockerErrors = ["Error response from daemon", "No such container", "is not running", "Cannot connect to the Docker daemon"];
    private readonly string _docker;

    /// <param name="container">The container's name or id.</param>
    /// <param name="user">The user scripts run as (<c>docker exec --user</c>); the container's default when null.</param>
    /// <param name="dockerExecutable">The Docker CLI; <c>docker</c> from PATH by default.</param>
    /// <exception cref="ArgumentException">A PowerShell shell: a container is a Linux host, which runs bash.</exception>
    public ContainerGameHost(string name, string container, HostShell shell, string? user = null, string dockerExecutable = "docker")
        : this(name, container, UnixBash(shell, true, "a container"), user, dockerExecutable, SystemProcessLauncher.Instance) { }

    internal ContainerGameHost(string name, string container, HostShell shell, string? user, string dockerExecutable, IProcessLauncher launcher) : base(name, shell, launcher)
    {
        if (!ContainerName.IsMatch(container ?? "")) throw new ArgumentException("A container is a Docker name or id.", nameof(container));
        if (user != null && !UserName.IsMatch(user)) throw new ArgumentException("A container user is name or name:group.", nameof(user));
        ArgumentException.ThrowIfNullOrWhiteSpace(dockerExecutable);
        Container = container!; User = user; _docker = dockerExecutable;
    }

    public override GameHostKind Kind => GameHostKind.Container;
    public string Container { get; }
    public string? User { get; }

    internal override (string Executable, IReadOnlyList<string> Arguments) WrapperCommand()
    {
        var arguments = new List<string> { "exec", "-i" };
        if (User != null) arguments.AddRange(["--user", User]);
        arguments.AddRange([Container, Shell.Executable]);
        arguments.AddRange(WrapperArguments(Shell));
        return (_docker, arguments);
    }

    // docker exec reports its own failures with 125, and a shell it cannot start with 126 or 127.
    internal override bool IsTransportFailure(ProcessExit exit) =>
        exit.ExitCode is 125 or 126 or 127 || DockerErrors.Any(error => exit.Stderr.Contains(error, StringComparison.Ordinal));

    /// <summary>
    /// Returns the ValheimCLI port itself when the container uses the host network; otherwise refuses with
    /// <see cref="NotSupportedException"/> rather than suggesting a CLI that listens beyond loopback.
    /// </summary>
    public override async Task<CliTunnel> OpenCliTunnelAsync(int hostPort, TimeSpan readyTimeout, int localPort = 0, CancellationToken cancellation = default)
    {
        GameHostPorts.Check(hostPort, nameof(hostPort));
        WaitText.RequireTimeout(readyTimeout);
        if (localPort != 0 && localPort != hostPort)
            throw new ArgumentException($"A host-network container's ValheimCLI is reached on its own port {hostPort}; nothing forwards {localPort}.", nameof(localPort));
        var exit = await Launcher.RunAsync(new ProcessCall(_docker, ["inspect", "--format", "{{.HostConfig.NetworkMode}}", Container], [], null, null, null, readyTimeout), cancellation).ConfigureAwait(false);
        if (exit.End != ProcessEnd.Exited || exit.ExitCode != 0)
            throw new InvalidOperationException($"Could not read the network mode of container {Container} ({(exit.End == ProcessEnd.Exited ? "exit " + exit.ExitCode : exit.End.ToString())}): {exit.Stderr.Trim()}");
        string mode = exit.Stdout.Trim();
        if (mode != "host")
            throw new NotSupportedException($"Container {Container} uses network mode '{mode}'. Its ValheimCLI listens on the container's own loopback, which a published port cannot reach; " +
                "run the container with --network host on a Linux Docker host, or reach the Docker host over SSH.");
        return new CliTunnel(null, hostPort, hostPort);
    }
}

internal static class GameHostPorts
{
    public static void Check(int port, string name)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(name, "A port is 1 to 65535.");
    }

    public static int FreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; } finally { listener.Stop(); }
    }

    public static int RequireFreeLoopbackPort(int port)
    {
        // A listener already there would answer the readiness probe while ssh fails to bind, and a client would reach it instead.
        var listener = new TcpListener(IPAddress.Loopback, port);
        try { listener.Start(); }
        catch (SocketException error) { throw new InvalidOperationException($"127.0.0.1:{port} is already in use; refusing to forward it: {error.Message}", error); }
        listener.Stop();
        return port;
    }

    public static async Task WaitUntilListeningAsync(IStartedProcess process, int port, TimeSpan timeout, CancellationToken cancellation, Func<string, string>? failureContext = null)
    {
        var exited = process.WaitForExitAsync(CancellationToken.None);
        string target = "the forward to listen on 127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
        if (failureContext != null) target = failureContext(target);
        // ssh announces its listener only in debug output, so it is probed: a local connection every 100 ms, the pause
        // raced against ssh's exit (a refused forward ends it at once).
        await ObservedWait.Run(target, async (_, token) =>
            {
                if (process.HasExited) return false;
                using var client = new TcpClient(AddressFamily.InterNetwork);
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
                attempt.CancelAfter(TimeSpan.FromSeconds(1));
                try { await client.ConnectAsync(IPAddress.Loopback, port, attempt.Token).ConfigureAwait(false); return !process.HasExited; }
                catch (Exception error) when (error is SocketException || (error is OperationCanceledException && !token.IsCancellationRequested)) { return false; }
            }, listening => listening, timeout, TimeSpan.FromMilliseconds(100), cancellation,
            _ => process.HasExited ? "ssh exited with code " + process.ExitCode.ToString(CultureInfo.InvariantCulture) : null, _ => LastLine(process.Stderr) ?? "nothing",
            ExitRace.Pause(exited)).ConfigureAwait(false);
    }

    private static string? LastLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
}
