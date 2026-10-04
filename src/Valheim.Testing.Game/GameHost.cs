using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Valheim.Testing.Game;

public enum GameHostKind { Local, Ssh, Container }
public enum HostShellKind { Bash, PowerShell }

/// <summary>
/// The shell a host runs scripts in, found on the host's PATH (or an absolute path without spaces). <see cref="WindowsPowerShell"/>
/// is Windows PowerShell 5.1 (<c>powershell</c>), the one every Windows host has; <see cref="Pwsh"/> is PowerShell 7 on any OS.
/// </summary>
public sealed class HostShell
{
    public static HostShell Bash { get; } = new(HostShellKind.Bash, "bash");
    public static HostShell WindowsPowerShell { get; } = new(HostShellKind.PowerShell, "powershell");
    public static HostShell Pwsh { get; } = new(HostShellKind.PowerShell, "pwsh");
    public HostShellKind Kind { get; }
    public string Executable { get; }
    public HostShell(HostShellKind kind, string executable)
    {
        // It becomes a word of a command line the host's login shell parses (cmd.exe, bash or PowerShell).
        if (!Regex.IsMatch(executable ?? "", @"^[A-Za-z0-9_.:/\\-]+$"))
            throw new ArgumentException("The shell executable must be a name or a path without spaces or quotes.", nameof(executable));
        Kind = kind; Executable = executable!;
    }
    /// <summary><c>bash</c>, <c>pwsh</c> or <c>powershell</c> (Windows PowerShell 5.1).</summary>
    public static HostShell Parse(string name) => name switch
    {
        "bash" => Bash, "pwsh" => Pwsh, "powershell" => WindowsPowerShell,
        _ => throw new ArgumentException($"Unknown shell '{name}'; use bash, pwsh or powershell.", nameof(name)),
    };
    public override string ToString() => Executable;
}

/// <summary>What is known about a host script once its command returned.</summary>
public enum HostOutcome
{
    /// <summary>The script ran to its end; <see cref="HostResult.ExitCode"/> is its own exit code.</summary>
    Exited,
    /// <summary>
    /// The host was not reached or the command could not be started (ssh exit 255, a docker error, a missing executable) and
    /// no exit was reported. The script most likely did not run, but a connection can also drop mid-run: do not assume either.
    /// </summary>
    TransportFailed,
    /// <summary>
    /// No exit was reported: the deadline passed (<see cref="HostResult.TimedOut"/>) or the command ended without the report.
    /// The script may not have run, may have finished or may still be running. Never a pass and never a failure.
    /// </summary>
    Unknown,
}

/// <summary>One host script run. Output has carriage returns removed; <see cref="ExitCode"/> is set only when <see cref="Outcome"/> is <see cref="HostOutcome.Exited"/>.</summary>
public sealed record HostResult(HostOutcome Outcome, int? ExitCode, string Stdout, string Stderr, TimeSpan Elapsed, bool TimedOut)
{
    /// <summary>The script exited 0.</summary>
    public bool Succeeded => Outcome == HostOutcome.Exited && ExitCode == 0;
    /// <summary>The script ran and exited non-zero. A transport failure or an unknown outcome is not a failure of the script.</summary>
    public bool Failed => Outcome == HostOutcome.Exited && ExitCode != 0;
    /// <summary>Returns this result if the script exited 0; otherwise throws <see cref="HostOperationException"/> describing <paramref name="what"/>.</summary>
    public HostResult EnsureSuccess(string what) => Succeeded ? this : throw new HostOperationException(what, this);
    public string Describe()
    {
        string state = Outcome switch
        {
            HostOutcome.Exited => "exited with code " + ExitCode?.ToString(CultureInfo.InvariantCulture),
            HostOutcome.TransportFailed => "the transport failed; the script may or may not have run",
            _ when TimedOut => "timed out; the outcome is unknown and the script may still be running",
            _ => "ended without the host's exit report; the outcome is unknown",
        };
        string error = Stderr.Trim();
        return state + " after " + WaitText.Seconds(Elapsed) + (error.Length == 0 ? "" : "; stderr: " + (error.Length > 600 ? "..." + error[^600..] : error));
    }
}

/// <summary>A host operation did not succeed. <see cref="Outcome"/> tells a script failure from a transport failure and an unknown outcome.</summary>
public sealed class HostOperationException(string what, HostResult result) : InvalidOperationException(what + ": " + result.Describe())
{
    public HostResult Result { get; } = result;
    public HostOutcome Outcome => Result.Outcome;
}

/// <summary>What a claim, release or check of a host lock established.</summary>
public enum HostLockState
{
    /// <summary>This call created the lock for the owner.</summary>
    Claimed,
    /// <summary>The lock names exactly this owner (a repeated claim, or a check by the holder). Nothing was changed.</summary>
    Yours,
    /// <summary>The lock names another owner, given in <see cref="HostLockResult.Holder"/>. Nothing was changed.</summary>
    HeldByOther,
    /// <summary>This call removed the owner's lock.</summary>
    Released,
    /// <summary>No lock is held (a check, or a release of a lock that was already gone).</summary>
    Free,
    /// <summary>
    /// Nothing could be proven: a transport failure, a timeout, an error on the host, an empty owner file (a claim in progress or
    /// an interrupted one) or an unexpected reply. Treat it as neither yours nor free; a claim may still have happened.
    /// </summary>
    Unknown,
}
public sealed record HostLockResult(HostLockState State, string? Holder, string Detail);

/// <summary>A lock could not be taken or released: another run holds it (<see cref="HostLockState.HeldByOther"/>) or the outcome is unknown.</summary>
public sealed class HostLockException(HostLockResult result) : InvalidOperationException(result.Detail)
{
    public HostLockResult Result { get; } = result;
    public HostLockState State => Result.State;
    public string? Holder => Result.Holder;
}

/// <summary>
/// An exclusive lock on one host, held by one run. Dispose releases it; a release that cannot be proven throws
/// <see cref="HostLockException"/>, because a failed teardown is a failure to report. Call <see cref="ReleaseAsync"/> to inspect it instead.
/// </summary>
public sealed class HostLock : IAsyncDisposable
{
    private readonly IGameHost _host;
    private readonly TimeSpan _timeout;
    private int _released;
    internal HostLock(IGameHost host, string path, string owner, TimeSpan timeout) { _host = host; Path = path; Owner = owner; _timeout = timeout; }
    public string HostName => _host.Name;
    public string Path { get; }
    /// <summary>The exact claimant in the lock: the owner given to <c>AcquireLockAsync</c> plus a unique id for this acquisition.</summary>
    public string Owner { get; }
    /// <summary>Removes the lock if it still names <see cref="Owner"/>. Only the first call acts.</summary>
    public async Task<HostLockResult> ReleaseAsync(CancellationToken cancellation = default)
    {
        if (Interlocked.Exchange(ref _released, 1) == 1) return new(HostLockState.Free, null, "The lock was already released by this handle.");
        return await _host.ReleaseLockAsync(Path, Owner, _timeout, cancellation).ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _released) == 1) return;
        var result = await ReleaseAsync().ConfigureAwait(false);
        if (result.State is not (HostLockState.Released or HostLockState.Free)) throw new HostLockException(result);
    }
}

public enum HostLogOutcome { Matched, FailureMatched, TimedOut }

/// <summary>The end of a host log wait: the matching line, or the last line seen before the deadline.</summary>
public sealed record HostLogResult(HostLogOutcome Outcome, string Target, string? Line, TimeSpan Elapsed, string? LastLine)
{
    /// <summary>Returns the matching line; a failure line throws <see cref="WaitFailedException"/> and expiry <see cref="WaitTimeoutException"/>.</summary>
    public string EnsureMatched() => Outcome switch
    {
        HostLogOutcome.Matched => Line!,
        HostLogOutcome.FailureMatched => throw new WaitFailedException(Target, "a line matched a failure pattern", Elapsed, Line),
        _ => throw new WaitTimeoutException(Target, Elapsed, LastLine),
    };
}

/// <summary>Files extracted into a new directory on the host. <see cref="Sha256"/> is the archive's hash, checked on both ends; <see cref="Commit"/> and <see cref="Tree"/> are set for a git revision.</summary>
public sealed record Shipment(string HostDirectory, string Sha256, long Bytes, string? Commit, string? Tree);
/// <summary>A host directory extracted locally. <see cref="Sha256"/> and <see cref="Bytes"/> describe the archive, checked on both ends.</summary>
public sealed record FetchedDirectory(string LocalDirectory, string Sha256, long Bytes, int Files);

/// <summary>
/// A local loopback endpoint for a ValheimCLI that listens on its host's loopback only. For a remote host it is an ssh local
/// forward this process started; disposing stops that process, and only that one. Otherwise nothing was started.
/// </summary>
public sealed class CliTunnel : IDisposable
{
    private readonly IStartedProcess? _process;
    private int _disposed;
    internal CliTunnel(IStartedProcess? process, int localPort, int hostPort) { _process = process; LocalPort = localPort; HostPort = hostPort; }
    /// <summary>Always loopback: a tunnel never listens on another address.</summary>
    public string Address => "127.0.0.1";
    /// <summary>The port on 127.0.0.1 here.</summary>
    public int LocalPort { get; }
    /// <summary>The ValheimCLI port on the host's 127.0.0.1.</summary>
    public int HostPort { get; }
    /// <summary>True when an ssh process forwards the port; false when the host's loopback is this machine's.</summary>
    public bool Forwarded => _process != null;
    /// <summary>True once a forward ended, for example because the connection dropped, and after <see cref="Dispose"/>.</summary>
    public bool HasExited => _process != null && (Volatile.Read(ref _disposed) == 1 || _process.HasExited);
    /// <summary>What the forwarding ssh wrote to stderr so far, such as a refused channel to the host's port.</summary>
    public string StandardError => _process?.Stderr ?? "";
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1 || _process == null) return;
        try { _process.Stop(TimeSpan.FromSeconds(10)); } finally { _process.Dispose(); }
    }
}

/// <summary>
/// A machine or container that runs a game server or client for a test run. Every operation takes an explicit deadline.
/// A remote operation that times out or loses its reply reports an unknown outcome, never a pass or a failure, and is never
/// retried here: a mutation must not be repeated blindly.
/// </summary>
public interface IGameHost
{
    string Name { get; }
    GameHostKind Kind { get; }
    HostShell Shell { get; }
    /// <summary>Runs a script with variables that arrive as literals, never parsed by a shell.</summary>
    Task<HostResult> RunAsync(string script, IReadOnlyDictionary<string, string>? variables, TimeSpan timeout, CancellationToken cancellation = default);
    /// <summary>Takes the host's exclusive lock for <paramref name="owner"/> (a run id); throws <see cref="HostLockException"/> if another run holds it or the claim is unproven.</summary>
    Task<HostLock> AcquireLockAsync(string lockPath, string owner, TimeSpan timeout, CancellationToken cancellation = default);
    Task<HostLockResult> CheckLockAsync(string lockPath, string owner, TimeSpan timeout, CancellationToken cancellation = default);
    Task<HostLockResult> ReleaseLockAsync(string lockPath, string owner, TimeSpan timeout, CancellationToken cancellation = default);
    /// <summary>Ships one committed git revision (tracked files only) into a new host directory.</summary>
    Task<Shipment> ShipRevisionAsync(string repository, string revision, string hostDirectory, TimeSpan timeout, CancellationToken cancellation = default);
    /// <summary>Ships a local directory's files into a new host directory.</summary>
    Task<Shipment> ShipFilesAsync(string localDirectory, string hostDirectory, TimeSpan timeout, CancellationToken cancellation = default);
    /// <summary>The log's current length in bytes (0 if it does not exist), for a later <see cref="WaitForLogAsync"/>.</summary>
    Task<long> LogOffsetAsync(string logPath, TimeSpan timeout, CancellationToken cancellation = default);
    /// <summary>Follows a host log from a byte offset until a line matches <paramref name="success"/> or one of <paramref name="failures"/>, or the deadline.</summary>
    Task<HostLogResult> WaitForLogAsync(string logPath, long fromOffset, Regex success, IReadOnlyList<Regex>? failures, TimeSpan timeout, CancellationToken cancellation = default);
    /// <summary>Copies a host directory (evidence: logs, reports) into a new local directory.</summary>
    Task<FetchedDirectory> FetchDirectoryAsync(string hostDirectory, string localDirectory, TimeSpan timeout, CancellationToken cancellation = default);
    /// <summary>A local loopback endpoint for the ValheimCLI listening on the host's 127.0.0.1:<paramref name="hostPort"/>.</summary>
    Task<CliTunnel> OpenCliTunnelAsync(int hostPort, TimeSpan readyTimeout, int localPort = 0, CancellationToken cancellation = default);
}

/// <summary>
/// The shared implementation: every operation is a fixed script run through the host's shell, so local, SSH and container
/// hosts behave alike and differ only in how the shell is started and how the ValheimCLI port is reached.
/// <para>
/// A script travels on standard input, never on a command line: a small fixed wrapper on the host reads it, writes it to a
/// temporary file, runs it and reports its exit code on its own line of stderr. That report, not the transport's exit status,
/// is the script's exit code, so a Windows host whose login shell is PowerShell (which reports only 0 or 1) still returns the
/// real one. Without the report the outcome is a transport failure or unknown.
/// </para>
/// </summary>
public abstract class ScriptedGameHost : IGameHost
{
    internal const string ExitMarker = "[vt-exit] ";
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly Regex VariableName = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> BashReserved = new(StringComparer.Ordinal)
    {
        "PATH", "IFS", "HOME", "PWD", "OLDPWD", "SHELL", "UID", "EUID", "PPID", "ENV", "BASH", "BASH_ENV", "BASHPID", "CDPATH", "SHELLOPTS",
        "BASHOPTS", "GLOBIGNORE", "PS4", "LD_PRELOAD", "LD_LIBRARY_PATH", "SECONDS", "RANDOM", "LINENO", "VT_UPLOAD", "VT_SECRETS",
    };
    // PowerShell's automatic variables; names ending in Preference are refused separately.
    private static readonly HashSet<string> PowerShellReserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "_", "args", "ConsoleFileName", "Error", "Event", "EventArgs", "EventSubscriber", "ExecutionContext", "false", "foreach", "HOME",
        "Host", "input", "IsCoreCLR", "IsLinux", "IsMacOS", "IsWindows", "LASTEXITCODE", "Matches", "MyInvocation", "NestedPromptLevel",
        "null", "PID", "PROFILE", "PSBoundParameters", "PSCmdlet", "PSCommandPath", "PSCulture", "PSDebugContext", "PSHOME", "PSItem",
        "PSScriptRoot", "PSSenderInfo", "PSUICulture", "PSVersionTable", "PWD", "Sender", "ShellId", "StackTrace", "switch", "this", "true",
    };
    private protected readonly IProcessLauncher Launcher;

    private protected ScriptedGameHost(string name, HostShell shell, IProcessLauncher launcher)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(shell);
        Name = name; Shell = shell; Launcher = launcher;
    }

    public string Name { get; }
    public abstract GameHostKind Kind { get; }
    public HostShell Shell { get; }

    /// <summary>The executable and arguments that start the wrapper in the host's shell.</summary>
    internal abstract (string Executable, IReadOnlyList<string> Arguments) WrapperCommand();
    /// <summary>Variables for the local process <see cref="WrapperCommand"/> starts; none unless a host sets the wrapper's own environment.</summary>
    internal virtual IReadOnlyDictionary<string, string> WrapperEnvironment() => new Dictionary<string, string>();
    /// <summary>Whether a command that ended without the exit report failed in its transport rather than on the host.</summary>
    internal abstract bool IsTransportFailure(ProcessExit exit);
    public abstract Task<CliTunnel> OpenCliTunnelAsync(int hostPort, TimeSpan readyTimeout, int localPort = 0, CancellationToken cancellation = default);

    /// <summary>
    /// Runs <paramref name="script"/> on the host. Each variable arrives as a literal assignment before the script, never parsed by
    /// any shell: bash <c>name='value'</c>, PowerShell <c>$name = 'value'</c>. A PowerShell script runs with
    /// <c>$ErrorActionPreference = 'Stop'</c>, from its own temporary .ps1 file, and ends with its <c>exit N</c>, 0 when it falls off
    /// its end, or 1 for an uncaught error. A bash script's CRLF line endings become LF; its exit status is bash's. Its standard
    /// input is empty. The working directory is the ssh user's home, the container's working directory, or this process's.
    /// </summary>
    public Task<HostResult> RunAsync(string script, IReadOnlyDictionary<string, string>? variables, TimeSpan timeout, CancellationToken cancellation = default) =>
        RunCoreAsync(script, variables, null, null, timeout, cancellation);

    private static readonly Regex SecretToken = new("^[A-Za-z0-9+/]+={0,2}$", RegexOptions.CultureInvariant);
    /// <summary>
    /// <see cref="RunAsync"/> with secret values that never enter the composed script, which the host's wrapper writes to a
    /// temporary file. Each token (base64, so one word) travels on its own line of standard input after the script; the wrapper
    /// keeps that line in memory and hands it to the script as the environment variable <c>VT_SECRETS</c> (the tokens separated
    /// by spaces), which the script should read and clear first. Callers redact the replies themselves.
    /// </summary>
    internal Task<HostResult> RunWithSecretsAsync(string script, IReadOnlyDictionary<string, string>? variables, IReadOnlyList<string> secrets, TimeSpan timeout,
        CancellationToken cancellation = default)
    {
        foreach (string token in secrets)
            if (!SecretToken.IsMatch(token ?? "")) throw new ArgumentException("A secret travels as base64 tokens.", nameof(secrets));
        return RunCoreAsync(script, variables, null, null, timeout, cancellation, string.Join(' ', secrets));
    }

    /// <summary>
    /// Takes an advisory lock at <paramref name="lockPath"/>, a directory on the host, for <paramref name="owner"/> (a run id).
    /// The claim is the exclusive creation of an owner file inside it (O_EXCL, or CreateNew on Windows), which exactly one claimer
    /// can win; the directory itself may already exist. Each acquisition claims as <paramref name="owner"/> plus a unique id
    /// (<see cref="HostLock.Owner"/>), so two runs that pass the same owner never both hold the lock, and only this handle can
    /// release it. Another claimant's lock, or an unproven claim, throws <see cref="HostLockException"/>; nothing is waited for or
    /// retried. An unproven claim's message names the claimant it tried: check the lock with it before deciding what to do.
    /// A lock left by a crashed run stays until a person removes it.
    /// </summary>
    public async Task<HostLock> AcquireLockAsync(string lockPath, string owner, TimeSpan timeout, CancellationToken cancellation = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        if (owner.Length > 200) throw new ArgumentException("An owner is at most 200 characters; a unique id is added to it.", nameof(owner));
        string claimant = owner + " [" + Guid.NewGuid().ToString("N") + "]";
        var result = await LockAsync("claim", lockPath, claimant, timeout, cancellation).ConfigureAwait(false);
        if (result.State is HostLockState.Claimed or HostLockState.Yours) return new HostLock(this, lockPath, claimant, timeout);
        if (result.State == HostLockState.Unknown) result = result with { Detail = result.Detail + $" The claim was made as '{claimant}'." };
        throw new HostLockException(result);
    }
    /// <summary>Reads the lock: <see cref="HostLockState.Free"/>, <see cref="HostLockState.Yours"/> or <see cref="HostLockState.HeldByOther"/>. Changes nothing.</summary>
    public Task<HostLockResult> CheckLockAsync(string lockPath, string owner, TimeSpan timeout, CancellationToken cancellation = default) =>
        LockAsync("check", lockPath, owner, timeout, cancellation);
    /// <summary>Removes the lock only if it names exactly <paramref name="owner"/>; another owner's lock is left alone.</summary>
    public Task<HostLockResult> ReleaseLockAsync(string lockPath, string owner, TimeSpan timeout, CancellationToken cancellation = default) =>
        LockAsync("release", lockPath, owner, timeout, cancellation);

    /// <summary>
    /// Ships one committed revision: <c>git archive</c> of <paramref name="revision"/> (tracked files only, never the working tree)
    /// is sent to the host, its SHA-256 checked there, and extracted into <paramref name="hostDirectory"/>, which must not exist yet.
    /// <c>SOURCE.txt</c> there records the commit, tree and hash. A directory this call created is removed again if extraction fails.
    /// </summary>
    public async Task<Shipment> ShipRevisionAsync(string repository, string revision, string hostDirectory, TimeSpan timeout, CancellationToken cancellation = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(repository);
        ArgumentException.ThrowIfNullOrEmpty(revision);
        if (revision.StartsWith('-') || revision.Any(char.IsControl)) throw new ArgumentException("Invalid revision.", nameof(revision));
        WaitText.RequireTimeout(timeout);
        RequireAbsolute(hostDirectory, nameof(hostDirectory));
        var clock = Stopwatch.StartNew();
        string commit = (await GitAsync(repository, timeout, cancellation, "rev-parse", "--verify", "--end-of-options", revision + "^{commit}").ConfigureAwait(false)).Trim();
        string tree = (await GitAsync(repository, timeout, cancellation, "rev-parse", "--verify", "--end-of-options", commit + "^{tree}").ConfigureAwait(false)).Trim();
        string archive = Path.Combine(Path.GetTempPath(), "vt-ship-" + Guid.NewGuid().ToString("N") + ".tar");
        try
        {
            await GitAsync(repository, timeout, cancellation, "archive", "--format=tar", "-o", archive, commit).ConfigureAwait(false);
            var left = timeout - clock.Elapsed;
            if (left <= TimeSpan.Zero) throw new WaitTimeoutException("git archive of " + commit, clock.Elapsed, null);
            return await ShipArchiveAsync(archive, hostDirectory, $"commit={commit}\ntree={tree}\n", commit, tree, left, cancellation).ConfigureAwait(false);
        }
        finally { File.Delete(archive); }
    }

    /// <summary>
    /// Ships the files under <paramref name="localDirectory"/> (as a tar archive, checked by SHA-256 on the host) into
    /// <paramref name="hostDirectory"/>, which must not exist yet. <c>SOURCE.txt</c> there records the hash.
    /// </summary>
    public async Task<Shipment> ShipFilesAsync(string localDirectory, string hostDirectory, TimeSpan timeout, CancellationToken cancellation = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(localDirectory);
        WaitText.RequireTimeout(timeout);
        RequireAbsolute(hostDirectory, nameof(hostDirectory));
        string local = Path.GetFullPath(localDirectory);
        if (!Directory.Exists(local)) throw new DirectoryNotFoundException("Nothing to ship: " + local + " does not exist.");
        string archive = Path.Combine(Path.GetTempPath(), "vt-ship-" + Guid.NewGuid().ToString("N") + ".tar");
        try
        {
            await TarFile.CreateFromDirectoryAsync(local, archive, includeBaseDirectory: false, cancellation).ConfigureAwait(false);
            return await ShipArchiveAsync(archive, hostDirectory, "files=" + Path.GetFileName(Path.TrimEndingDirectorySeparator(local)) + "\n", null, null, timeout, cancellation).ConfigureAwait(false);
        }
        finally { File.Delete(archive); }
    }

    public async Task<long> LogOffsetAsync(string logPath, TimeSpan timeout, CancellationToken cancellation = default)
    {
        RequireAbsolute(logPath, nameof(logPath));
        var result = (await RunAsync(HostScripts.Size(Shell.Kind), new Dictionary<string, string> { ["log"] = logPath }, timeout, cancellation).ConfigureAwait(false))
            .EnsureSuccess("Reading the size of " + logPath + " on " + Name);
        var size = Regex.Match(result.Stdout, @"^VT-SIZE ([0-9]+)$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        if (!size.Success) throw new HostOperationException("Unexpected reply while reading the size of " + logPath + " on " + Name, result);
        return long.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Follows <paramref name="logPath"/> on the host from byte <paramref name="fromOffset"/>, event driven there (<c>tail -F</c>,
    /// or a file watcher under PowerShell), and matches each complete line here with .NET regular expressions, so a pattern means
    /// the same on every host. A line matching one of <paramref name="failures"/> (checked first) ends the wait as
    /// <see cref="HostLogOutcome.FailureMatched"/>. The log may not exist yet. Take the offset with <see cref="LogOffsetAsync"/>
    /// before the action whose line you await; a log the game recreates for each boot is best given a fresh path (a disposable
    /// runtime) and offset 0, because a replacement longer than the offset cannot be told apart. The host stops following on its
    /// own a little after the deadline. A transport failure or a follower that ends early throws <see cref="HostOperationException"/>.
    /// </summary>
    public async Task<HostLogResult> WaitForLogAsync(string logPath, long fromOffset, Regex success, IReadOnlyList<Regex>? failures, TimeSpan timeout, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(success);
        ArgumentOutOfRangeException.ThrowIfNegative(fromOffset);
        WaitText.RequireTimeout(timeout);
        RequireAbsolute(logPath, nameof(logPath));
        string target = $"a line matching /{success}/ in {logPath} on {Name}";
        var sync = new object();
        string? last = null, line = null;
        HostLogOutcome? outcome = null;
        bool Seen(string text)
        {
            lock (sync)
            {
                if (outcome != null) return true;
                string current = text.TrimEnd('\r').TrimStart('﻿');
                if (current.Length != 0) last = current; // A blank line says nothing about where the log got to.
                if (failures?.Any(failure => failure.IsMatch(current)) == true) outcome = HostLogOutcome.FailureMatched;
                else if (success.IsMatch(current)) outcome = HostLogOutcome.Matched;
                if (outcome != null) line = current;
                return outcome != null;
            }
        }
        int seconds = (int)Math.Ceiling(timeout.TotalSeconds) + 30;
        var exit = await Launcher.RunAsync(Call(HostScripts.Follow(Shell.Kind), new Dictionary<string, string>
        {
            ["log"] = logPath, ["offset"] = fromOffset.ToString(CultureInfo.InvariantCulture), ["seconds"] = seconds.ToString(CultureInfo.InvariantCulture),
        }, null, null, Seen, timeout), cancellation).ConfigureAwait(false);
        lock (sync)
        {
            if (outcome != null) return new HostLogResult(outcome.Value, target, line, exit.Elapsed, last);
            if (exit.End == ProcessEnd.TimedOut) return new HostLogResult(HostLogOutcome.TimedOut, target, null, exit.Elapsed, last);
        }
        throw new HostOperationException("Following " + logPath + " on " + Name + " ended before a match", Interpret(exit));
    }

    /// <summary>
    /// Copies <paramref name="hostDirectory"/> into <paramref name="localDirectory"/>, which must not exist yet: the host packs it with
    /// tar (no macOS AppleDouble files), the archive's SHA-256 and size are checked on both ends, and it is extracted here (entries
    /// that would land outside it are refused). A local directory this call created is removed again if anything fails.
    /// </summary>
    public async Task<FetchedDirectory> FetchDirectoryAsync(string hostDirectory, string localDirectory, TimeSpan timeout, CancellationToken cancellation = default)
    {
        RequireAbsolute(hostDirectory, nameof(hostDirectory));
        ArgumentException.ThrowIfNullOrEmpty(localDirectory);
        WaitText.RequireTimeout(timeout);
        string local = Path.GetFullPath(localDirectory);
        if (Directory.Exists(local) || File.Exists(local)) throw new InvalidOperationException("Fetch into a new local directory; " + local + " already exists.");
        string archive = Path.Combine(Path.GetTempPath(), "vt-fetch-" + Guid.NewGuid().ToString("N") + ".tar");
        bool created = false;
        try
        {
            HostResult result;
            await using (var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write))
                result = await RunCoreAsync(HostScripts.Fetch(Shell.Kind), new Dictionary<string, string> { ["dir"] = hostDirectory }, null, output, timeout, cancellation).ConfigureAwait(false);
            result.EnsureSuccess("Packing " + hostDirectory + " on " + Name);
            var reported = Regex.Match(result.Stderr, @"^VT-FETCH ([0-9a-f]{64}) ([0-9]+)$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
            if (!reported.Success) throw new HostOperationException("The host did not report the archive's hash", result);
            string sha256 = await Sha256Async(archive, cancellation).ConfigureAwait(false);
            long bytes = new FileInfo(archive).Length;
            if (sha256 != reported.Groups[1].Value || bytes.ToString(CultureInfo.InvariantCulture) != reported.Groups[2].Value)
                throw new IOException($"The archive of {hostDirectory} arrived as {bytes} bytes with SHA-256 {sha256}; the host sent {reported.Groups[2].Value} bytes with {reported.Groups[1].Value}.");
            Directory.CreateDirectory(local); created = true;
            await TarFile.ExtractToDirectoryAsync(archive, local, overwriteFiles: false, cancellation).ConfigureAwait(false);
            return new FetchedDirectory(local, sha256, bytes, Directory.EnumerateFiles(local, "*", SearchOption.AllDirectories).Count());
        }
        catch
        {
            if (created) Directory.Delete(local, recursive: true);
            throw;
        }
        finally { File.Delete(archive); }
    }

    private async Task<Shipment> ShipArchiveAsync(string archive, string hostDirectory, string record, string? commit, string? tree, TimeSpan timeout, CancellationToken cancellation)
    {
        string sha256 = await Sha256Async(archive, cancellation).ConfigureAwait(false);
        long bytes = new FileInfo(archive).Length;
        HostResult result;
        var variables = new Dictionary<string, string> { ["dest"] = hostDirectory, ["sha256"] = sha256, ["record"] = record };
        if (this is SshGameHost ssh && Shell.Kind == HostShellKind.PowerShell)
        {
            // Windows OpenSSH can leave a PowerShell child waiting for EOF partway through a binary stdin upload.
            // Move the archive with SFTP/SCP, then run the same hash-checked extraction with only a short script on stdin.
            string remoteName = "vt-upload-" + Guid.NewGuid().ToString("N") + ".tar";
            await ssh.UploadArchiveAsync(archive, remoteName, timeout, cancellation).ConfigureAwait(false);
            variables["uploadName"] = remoteName;
            result = await RunCoreAsync(HostScripts.PowerShellShipFile, variables, null, null, timeout, cancellation).ConfigureAwait(false);
        }
        else
        {
            await using var upload = File.OpenRead(archive);
            result = await RunCoreAsync(HostScripts.Ship(Shell.Kind), variables, upload, null, timeout, cancellation).ConfigureAwait(false);
        }
        return ReadShipVerdict(result, hostDirectory, sha256, bytes, commit, tree);
    }

    internal static Shipment ReadShipVerdict(HostResult result, string hostDirectory, string sha256, long bytes, string? commit, string? tree)
    {
        result.EnsureSuccess("Shipping to " + hostDirectory);
        string verdict = result.Stdout.Split('\n')[0];
        if (verdict == "VT-SHIP exists") throw new InvalidOperationException(hostDirectory + " already exists on the host; ship each revision into a new directory.");
        if (verdict == "VT-SHIP source-txt") throw new InvalidOperationException("The shipped files have their own SOURCE.txt at their root; nothing was left in " + hostDirectory + ".");
        if (verdict.StartsWith("VT-SHIP hash ", StringComparison.Ordinal))
            throw new IOException($"The archive arrived with SHA-256 {verdict["VT-SHIP hash ".Length..]}; {sha256} was sent. Nothing was extracted.");
        if (verdict != "VT-SHIP shipped " + sha256) throw new HostOperationException("Unexpected reply while shipping to " + hostDirectory, result);
        return new Shipment(hostDirectory, sha256, bytes, commit, tree);
    }

    private async Task<HostLockResult> LockAsync(string action, string lockPath, string owner, TimeSpan timeout, CancellationToken cancellation)
    {
        RequireAbsolute(lockPath, nameof(lockPath));
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        if (owner.Length > 240 || owner.Any(char.IsControl)) throw new ArgumentException("An owner is one line of at most 240 characters.", nameof(owner));
        var result = await RunAsync(HostScripts.Lock(Shell.Kind), new Dictionary<string, string> { ["action"] = action, ["lock"] = lockPath, ["owner"] = owner },
            timeout, cancellation).ConfigureAwait(false);
        return ReadLockVerdict(action, owner, lockPath, Name, result);
    }

    internal static HostLockResult ReadLockVerdict(string action, string owner, string lockPath, string hostName, HostResult result)
    {
        string where = lockPath + " on " + hostName;
        if (!result.Succeeded) return new(HostLockState.Unknown, null, $"The {action} of {where} is not proven: {result.Describe()}");
        string[] parts = result.Stdout.Split('\n', 2);
        string holder = parts.Length > 1 ? parts[1].TrimEnd('\n') : "";
        if (parts[0] == "VT-LOCK unowned")
            return new(HostLockState.Unknown, null, $"{where} has an empty owner file: a claim in progress or an interrupted one. Inspect it by hand.");
        HostLockState? state = (action, parts[0]) switch
        {
            ("claim", "VT-LOCK claimed") => HostLockState.Claimed,
            ("claim" or "check", "VT-LOCK yours") => HostLockState.Yours,
            (_, "VT-LOCK held") when holder.Length > 0 => HostLockState.HeldByOther,
            ("release", "VT-LOCK released") => HostLockState.Released,
            ("release" or "check", "VT-LOCK free") => HostLockState.Free,
            _ => null,
        };
        return state switch
        {
            null => new(HostLockState.Unknown, null, $"Unexpected reply to the {action} of {where}: {result.Stdout.Trim()}"),
            HostLockState.HeldByOther => new(HostLockState.HeldByOther, holder, $"{where} is held by another run: {holder}"),
            HostLockState.Claimed or HostLockState.Yours => new(state.Value, owner, $"{where} is held by {owner}"),
            _ => new(state.Value, null, $"{where} is free"),
        };
    }

    private async Task<HostResult> RunCoreAsync(string script, IReadOnlyDictionary<string, string>? variables, Stream? upload, Stream? output, TimeSpan timeout, CancellationToken cancellation,
        string secrets = "")
    {
        WaitText.RequireTimeout(timeout);
        var exit = await Launcher.RunAsync(Call(script, variables, upload, output, null, timeout, secrets), cancellation).ConfigureAwait(false);
        return Interpret(exit);
    }

    private ProcessCall Call(string script, IReadOnlyDictionary<string, string>? variables, Stream? upload, Stream? output, Func<string, bool>? lines, TimeSpan timeout,
        string secrets = "")
    {
        var (executable, arguments) = WrapperCommand();
        return new(executable, arguments, Payload(Compose(Shell.Kind, script, variables), secrets), upload, output, lines, timeout) { Environment = WrapperEnvironment() };
    }

    /// <summary>The full script the host runs: preferences (PowerShell), variables as literals, the script and (PowerShell) a final <c>exit 0</c>.</summary>
    internal static string Compose(HostShellKind kind, string script, IReadOnlyDictionary<string, string>? variables)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        var text = new StringBuilder();
        if (kind == HostShellKind.PowerShell) text.Append("$ErrorActionPreference = 'Stop'\n$ProgressPreference = 'SilentlyContinue'\n");
        var seen = new HashSet<string>(kind == HostShellKind.PowerShell ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach ((string name, string value) in (variables ?? new Dictionary<string, string>()).OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            CheckVariable(kind, name);
            if (!seen.Add(name)) throw new ArgumentException($"Variable '{name}' is given twice; PowerShell names ignore case.", nameof(variables));
            ArgumentNullException.ThrowIfNull(value, name);
            text.Append(kind == HostShellKind.Bash ? name + "=" : "$" + name + " = ").Append(Literal(kind, value)).Append('\n');
        }
        // A bash script with CRLF endings can never run; PowerShell reads either.
        text.Append(kind == HostShellKind.Bash ? script.Replace("\r\n", "\n") : script);
        if (!script.EndsWith('\n')) text.Append('\n');
        if (kind == HostShellKind.PowerShell) text.Append("exit 0\n");
        return text.ToString();
    }

    /// <summary>
    /// A literal nothing in it is interpreted. Bash: single quotes, each quote as <c>'\''</c>. PowerShell: single quotes with every
    /// single-quote character doubled, including the typographic ones PowerShell also accepts as quotes (U+2018 to U+201B).
    /// </summary>
    internal static string Literal(HostShellKind kind, string value)
    {
        if (value.Contains('\0')) throw new ArgumentException("A value cannot contain a NUL character; no shell variable can hold one.", nameof(value));
        if (kind == HostShellKind.Bash) return "'" + value.Replace("'", "'\\''") + "'";
        var text = new StringBuilder("'", value.Length + 2);
        foreach (char ch in value)
        {
            text.Append(ch);
            if (ch is '\'' or '‘' or '’' or '‚' or '‛') text.Append(ch);
        }
        return text.Append('\'').ToString();
    }

    /// <summary>
    /// What standard input carries: the script as one line of base64 (UTF-8), then the secrets line (space-separated base64 tokens,
    /// usually empty), then any upload's raw bytes.
    /// </summary>
    internal static byte[] Payload(string composed, string secrets = "") =>
        Encoding.ASCII.GetBytes(Convert.ToBase64String(Utf8.GetBytes(composed)) + "\n" + secrets + "\n");

    /// <summary>The shell's own arguments that start the wrapper, when no other shell parses them first.</summary>
    internal static IReadOnlyList<string> WrapperArguments(HostShell shell) => shell.Kind == HostShellKind.Bash
        ? ["-c", HostScripts.BashWrapper]
        : ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", EncodedPowerShellWrapper];
    internal static string EncodedPowerShellWrapper => Convert.ToBase64String(Encoding.Unicode.GetBytes(HostScripts.PowerShellWrapper));

    /// <summary>
    /// The variable that keeps a PowerShell 7 wrapper off its startup JIT profile, and its value. pwsh records which methods it
    /// compiled at startup in one file per user (<c>StartupProfileData-NonInteractive</c> in its cache directory) and reads it on the
    /// next start; pwsh processes starting at once corrupt that file, and every later start that reads it can fail: an invalid
    /// assembly or culture name, a stack overflow or a segmentation fault (PowerShell/PowerShell#26528, dotnet/runtime#121977;
    /// #133 here: 11 of 3000 parallel starts on macOS, 1 of 3000 on Linux, pwsh 7.6.6 on .NET 10). The runtime uses multi-core JIT
    /// (which reads and writes that file) only with at least <c>MultiCoreJitMinNumCpus</c> processors; the value is hexadecimal, so
    /// FFFF turns it off. pwsh then neither reads nor writes the file and starts a little slower. Windows PowerShell 5.1 runs on
    /// .NET Framework and ignores it. Processes the script starts inherit it, which only affects .NET programs' startup.
    /// A workaround: #145 tracks the upstream fixes and when to remove it (<c>scripts/pwsh-startup-stress.cs</c> checks the race).
    /// </summary>
    internal static readonly KeyValuePair<string, string> NoStartupJitProfile = new("DOTNET_MultiCoreJitMinNumCpus", "FFFF");

    /// <summary>
    /// Maps a command's end to an outcome. The wrapper's report (the last <c>[vt-exit] N</c> line on stderr) is the script's exit
    /// code whatever the transport returned; without it the host decides between a transport failure and an unknown outcome.
    /// A deadline always leaves the outcome unknown.
    /// </summary>
    internal HostResult Interpret(ProcessExit exit)
    {
        string stdout = exit.Stdout.Replace("\r", ""), stderr = exit.Stderr.Replace("\r", "");
        if (exit.End == ProcessEnd.NotStarted) return new HostResult(HostOutcome.TransportFailed, null, stdout, stderr, exit.Elapsed, false);
        if (exit.End != ProcessEnd.Exited) return new HostResult(HostOutcome.Unknown, null, stdout, ReadableStderr(stderr), exit.Elapsed, exit.End == ProcessEnd.TimedOut);
        int at = stderr.LastIndexOf("\n" + ExitMarker, StringComparison.Ordinal);
        if (at >= 0)
        {
            int start = at + 1 + ExitMarker.Length, end = stderr.IndexOf('\n', start);
            if (end < 0) end = stderr.Length;
            if (int.TryParse(stderr.AsSpan(start, end - start), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int code))
            {
                string rest = stderr[..at] + stderr[Math.Min(end + 1, stderr.Length)..];
                return new HostResult(HostOutcome.Exited, code, stdout, ReadableStderr(rest), exit.Elapsed, false);
            }
        }
        return new HostResult(IsTransportFailure(exit) ? HostOutcome.TransportFailed : HostOutcome.Unknown, null, stdout, ReadableStderr(stderr), exit.Elapsed, false);
    }

    private static readonly Regex ClixmlRecord = new(@"<S S=""(\w+)"">(.*?)</S>", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex ClixmlEscape = new("_x([0-9A-Fa-f]{4})_", RegexOptions.CultureInvariant);
    /// <summary>
    /// PowerShell started with -EncodedCommand may write its error, warning and progress streams to stderr as CLIXML
    /// (<c>#&lt; CLIXML</c> then one <c>&lt;Objs&gt;</c> line). This turns each record into plain text and drops progress.
    /// </summary>
    internal static string ReadableStderr(string stderr)
    {
        if (!stderr.Contains("#< CLIXML", StringComparison.Ordinal)) return stderr;
        var text = new StringBuilder();
        foreach (string line in stderr.Split('\n'))
        {
            if (line == "#< CLIXML") continue;
            if (!line.StartsWith("<Objs", StringComparison.Ordinal)) { text.Append(line).Append('\n'); continue; }
            bool lineStart = true;
            foreach (Match record in ClixmlRecord.Matches(line))
            {
                string prefix = record.Groups[1].Value switch { "Warning" => "WARNING: ", "Verbose" => "VERBOSE: ", "Debug" => "DEBUG: ", _ => "" };
                string value = WebUtility.HtmlDecode(ClixmlEscape.Replace(record.Groups[2].Value,
                    m => ((char)int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString())).Replace("\r", "");
                string[] pieces = value.Split('\n');
                for (int i = 0; i < pieces.Length; i++)
                {
                    if (lineStart && pieces[i].Length > 0) { text.Append(prefix); lineStart = false; }
                    text.Append(pieces[i]);
                    if (i < pieces.Length - 1) { text.Append('\n'); lineStart = true; }
                }
            }
            if (!lineStart) text.Append('\n');
        }
        return text.ToString().TrimEnd('\n') + (stderr.EndsWith('\n') ? "\n" : "");
    }

    private static void CheckVariable(HostShellKind kind, string name)
    {
        if (!VariableName.IsMatch(name ?? "")) throw new ArgumentException($"'{name}' is not a variable name; use letters, digits and _.", nameof(name));
        bool reserved = kind == HostShellKind.Bash ? BashReserved.Contains(name!)
            : PowerShellReserved.Contains(name!) || name!.EndsWith("Preference", StringComparison.OrdinalIgnoreCase);
        if (reserved) throw new ArgumentException($"'{name}' is a variable the {kind} host sets itself; choose another name.", nameof(name));
    }

    /// <summary>A host path must not depend on the user's home or working directory. PowerShell hosts also accept drive and UNC paths.</summary>
    internal static bool IsAbsolute(HostShellKind kind, string path) =>
        path.StartsWith('/') || (kind == HostShellKind.PowerShell && (Regex.IsMatch(path, @"^[A-Za-z]:[\\/]") || path.StartsWith(@"\\", StringComparison.Ordinal)));

    private protected void RequireAbsolute(string path, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(path, name);
        if (path.Any(char.IsControl)) throw new ArgumentException("A host path is one line.", name);
        if (!IsAbsolute(Shell.Kind, path)) throw new ArgumentException($"{name} must be an absolute path on the host; '{path}' would depend on the user's home or working directory.", name);
    }

    private static async Task<string> GitAsync(string repository, TimeSpan timeout, CancellationToken cancellation, params string[] arguments)
    {
        var start = new ProcessStartInfo("git");
        foreach (string argument in new[] { "-C", repository }.Concat(arguments)) start.ArgumentList.Add(argument);
        var exit = await ProcessRunner.RunAsync(start, [], null, null, null, timeout, cancellation).ConfigureAwait(false);
        if (exit.End != ProcessEnd.Exited || exit.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} in {repository} failed ({(exit.End == ProcessEnd.Exited ? "exit " + exit.ExitCode : exit.End.ToString())}): {exit.Stderr.Trim()}");
        return exit.Stdout;
    }

    private static async Task<string> Sha256Async(string file, CancellationToken cancellation)
    {
        await using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellation).ConfigureAwait(false));
    }
}
