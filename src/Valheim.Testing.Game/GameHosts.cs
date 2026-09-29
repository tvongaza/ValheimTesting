using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Valheim.Testing.Game;

/// <summary>
/// This machine. Scripts run in a local shell process (bash, pwsh or Windows PowerShell), so the same lock, ship, log and fetch
/// scripts run as on a remote host. The only transport failure is a shell that cannot be started.
/// </summary>
public sealed class LocalGameHost : ScriptedGameHost
{
    public LocalGameHost(string name, HostShell shell) : this(name, shell, SystemProcessLauncher.Instance) { }
    internal LocalGameHost(string name, HostShell shell, IProcessLauncher launcher) : base(name, shell, launcher) { }
    public override GameHostKind Kind => GameHostKind.Local;
    internal override (string Executable, IReadOnlyList<string> Arguments) WrapperCommand() => (Shell.Executable, WrapperArguments(Shell));
    internal override bool IsTransportFailure(ProcessExit exit) => false;

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
/// A machine reached through the system OpenSSH client (<c>ssh</c>, on Windows, macOS and Linux) with key or agent authentication
/// only: BatchMode is always on, so ssh never prompts, and nothing here accepts a password. The destination is <c>user@host</c>,
/// <c>ssh://user@host:port</c> or an ssh-config alias; the host key must already be known (or pass <c>StrictHostKeyChecking=accept-new</c>).
/// A bash host needs a POSIX login shell; a PowerShell host works under any login shell, including Windows' cmd.exe.
/// ssh exit 255 without the host's exit report is a transport failure.
/// </summary>
public sealed class SshGameHost : ScriptedGameHost
{
    private static readonly Regex OptionName = new("^[A-Za-z]+$", RegexOptions.CultureInvariant);
    // Always set first (ssh keeps the first value it reads for an option), so a caller's option cannot switch them off.
    private static readonly string[] FixedOptions = ["BatchMode", "ConnectTimeout", "ExitOnForwardFailure", "GatewayPorts", "ClearAllForwardings"];
    // A caller never adds listeners or picks the port twice.
    private static readonly string[] RefusedOptions = ["LocalForward", "RemoteForward", "DynamicForward", "Port"];
    private readonly string[] _options;
    private readonly string _ssh;

    /// <param name="destination"><c>user@host</c>, <c>ssh://user@host:port</c> or an ssh-config alias. Never a password.</param>
    /// <param name="port">The ssh port; 0 leaves it to the destination or the ssh config.</param>
    /// <param name="sshOptions">Extra <c>-o</c> options as <c>Name=value</c>, for example <c>IdentityFile=/path/key</c>.</param>
    /// <param name="connectTimeout">ssh's ConnectTimeout; 10 s by default.</param>
    /// <param name="sshExecutable">The OpenSSH client; <c>ssh</c> from PATH by default.</param>
    public SshGameHost(string name, string destination, HostShell shell, int port = 0, IEnumerable<string>? sshOptions = null, TimeSpan? connectTimeout = null, string sshExecutable = "ssh")
        : this(name, destination, shell, port, sshOptions, connectTimeout, sshExecutable, SystemProcessLauncher.Instance) { }

    internal SshGameHost(string name, string destination, HostShell shell, int port, IEnumerable<string>? sshOptions, TimeSpan? connectTimeout, string sshExecutable, IProcessLauncher launcher)
        : base(name, shell, launcher)
    {
        Destination = CheckDestination(destination);
        if (port != 0) GameHostPorts.Check(port, nameof(port));
        if (port != 0 && Destination.StartsWith("ssh://", StringComparison.Ordinal)) throw new ArgumentException("Give the port once: in the ssh:// destination or as the port.", nameof(port));
        Port = port;
        ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(10);
        if (ConnectTimeout < TimeSpan.FromSeconds(1)) throw new ArgumentOutOfRangeException(nameof(connectTimeout), "Use a connect timeout of at least one second.");
        ArgumentException.ThrowIfNullOrWhiteSpace(sshExecutable);
        _ssh = sshExecutable;
        _options = (sshOptions ?? []).Select(CheckOption).ToArray();
    }

    public override GameHostKind Kind => GameHostKind.Ssh;
    public string Destination { get; }
    public int Port { get; }
    public TimeSpan ConnectTimeout { get; }

    internal override (string Executable, IReadOnlyList<string> Arguments) WrapperCommand() => (_ssh, SshArguments(forward: false, ["-T"], RemoteCommand(Shell)));
    internal override bool IsTransportFailure(ProcessExit exit) => exit.ExitCode == 255;

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
            await GameHostPorts.WaitUntilListeningAsync(process, port, readyTimeout, cancellation).ConfigureAwait(false);
            return new CliTunnel(process, port, hostPort);
        }
        catch
        {
            try { process.Stop(TimeSpan.FromSeconds(10)); } finally { process.Dispose(); }
            throw;
        }
    }

    // ssh -G prints the configuration ssh would use for this destination, forwards from the user's and the system's config included.
    private async Task RefuseConfiguredForwardsAsync(TimeSpan timeout, CancellationToken cancellation)
    {
        var exit = await Launcher.RunAsync(new ProcessCall(_ssh, SshArguments(forward: true, ["-G"], null), [], null, null, null, timeout), cancellation).ConfigureAwait(false);
        if (exit.End != ProcessEnd.Exited || exit.ExitCode != 0)
            throw new InvalidOperationException($"Could not read the ssh configuration for {Destination} ({(exit.End == ProcessEnd.Exited ? "exit " + exit.ExitCode : exit.End.ToString())}): {exit.Stderr.Trim()}");
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
        if (option == null || equals <= 0 || !OptionName.IsMatch(option[..equals]) || option.Any(char.IsControl))
            throw new ArgumentException($"An ssh option is Name=value, for example IdentityFile=/path/key; got '{option}'.", nameof(option));
        string name = option[..equals];
        if (FixedOptions.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException(name + " is set by SshGameHost and cannot be overridden.", nameof(option));
        if (RefusedOptions.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException(name + " is refused: " + (name.Equals("Port", StringComparison.OrdinalIgnoreCase) ? "pass the port setting instead." : "the host opens only its own loopback CLI tunnel."), nameof(option));
        return option;
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
    public ContainerGameHost(string name, string container, HostShell shell, string? user = null, string dockerExecutable = "docker")
        : this(name, container, shell, user, dockerExecutable, SystemProcessLauncher.Instance) { }

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

    public static async Task WaitUntilListeningAsync(IOwnedProcess process, int port, TimeSpan timeout, CancellationToken cancellation)
    {
        var clock = Stopwatch.StartNew();
        var exited = process.WaitForExitAsync(CancellationToken.None);
        string target = "the forward to listen on 127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
        Exception Exited() => new WaitFailedException(target, "ssh exited with code " + process.ExitCode.ToString(CultureInfo.InvariantCulture), clock.Elapsed, LastLine(process.Stderr));
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            if (process.HasExited) throw Exited();
            using (var client = new TcpClient(AddressFamily.InterNetwork))
            using (var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                attempt.CancelAfter(TimeSpan.FromSeconds(1));
                try
                {
                    await client.ConnectAsync(IPAddress.Loopback, port, attempt.Token).ConfigureAwait(false);
                    if (process.HasExited) throw Exited();
                    return;
                }
                catch (Exception error) when (error is SocketException || (error is OperationCanceledException && !cancellation.IsCancellationRequested)) { }
            }
            var left = timeout - clock.Elapsed;
            if (left <= TimeSpan.Zero) throw new WaitTimeoutException(target, clock.Elapsed, LastLine(process.Stderr));
            // ssh announces its listener only in debug output, so it is probed: a local connection every 100 ms, raced against
            // ssh's exit (a refused forward ends it at once).
            await Task.WhenAny(Task.Delay(left < TimeSpan.FromMilliseconds(100) ? left : TimeSpan.FromMilliseconds(100), cancellation), exited).ConfigureAwait(false);
        }
    }

    private static string? LastLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
}
