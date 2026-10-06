using System.Text;
using System.Text.RegularExpressions;

namespace Valheim.Testing.Game;

/// <summary>
/// Whether Valheim runs on another of the inventory's client hosts (#257, part 1 of the lease decision). A Steam account plays on
/// one computer at a time: a client started on account X while X plays elsewhere gets this machine's Steam signed out
/// ("Logged In Elsewhere"), and that Steam then exits until a person starts it again. Preflight asks every client host in the
/// inventory that the campaign does not already check: where Valheim runs signed in to an account a client would use, the
/// campaign is refused, naming that host. Only process ids leave the host, never an account id.
/// </summary>
internal static class SteamAccountInUse
{
    internal enum Playing
    {
        /// <summary>No Valheim client runs there.</summary>
        No,
        /// <summary>A Valheim client runs as the host's own user, the user whose Steam account the signed-in check reads.</summary>
        AsHostUser,
        /// <summary>A Valheim client runs, and which user runs it could not be read: its account is unknown.</summary>
        OwnerUnknown,
    }

    /// <summary>
    /// Whether a Valheim client runs on <paramref name="host"/>, with the process ids that showed it; throws
    /// <see cref="HostOperationException"/> when the host could not be asked. Another OS user's game is that user's Steam session,
    /// which the host user's signed-in account says nothing about, so it is left out.
    /// </summary>
    public static async Task<(Playing State, string Processes)> ReadAsync(IGameHost host, TimeSpan timeout, CancellationToken cancellation)
    {
        var result = (await host.RunAsync(host.Shell.Kind == HostShellKind.PowerShell ? PowerShell : Bash, null, timeout, cancellation).ConfigureAwait(false))
            .EnsureSuccess($"Asking {host.Name} whether Valheim runs there");
        string verdict = InteractiveClient.Line(result.Stdout, "VT-PLAYING ") ??
            throw new HostOperationException($"No Valheim-running verdict from {host.Name}", result);
        return verdict.Split(' ') switch
        {
            ["no"] => (Playing.No, ""),
            ["process", var ids] when ids.Length != 0 => (Playing.AsHostUser, ids),
            ["unowned", var ids] when ids.Length != 0 => (Playing.OwnerUnknown, ids),
            _ => throw new HostOperationException($"Unexpected Valheim-running verdict from {host.Name}", result),
        };
    }

    // Windows: Valheim.exe processes (a dedicated server signs in to no Steam user) whose owner is this user; one whose owner cannot
    // be read is reported as such. HKCU\Software\Valve\Steam\RunningAppID is not read: on the station (6 Oct 2026) it was readable
    // and 0 with no game running, but whether it names 892970 for a game started directly (not through Steam), as the tool starts
    // one, was not verified. pwsh elsewhere lists this user's processes by name.
    public static readonly string PowerShell = """
        $mine = @(); $unowned = @()
        if ([Environment]::OSVersion.Platform -ne [PlatformID]::Unix) {
            foreach ($p in @(Get-CimInstance Win32_Process -Filter "Name = 'Valheim.exe'" -ErrorAction Stop)) {
                $owner = Invoke-CimMethod -InputObject $p -MethodName GetOwner -ErrorAction SilentlyContinue
                if ($null -eq $owner -or $owner.ReturnValue -ne 0 -or -not $owner.User) { $unowned += [string]$p.ProcessId }
                elseif ($owner.User -eq $env:USERNAME) { $mine += [string]$p.ProcessId }
            }
        } else {
            $uid = (& id -u)
            foreach ($line in @(& ps -axo 'pid=,uid=,comm=')) {
                $f = ([string]$line).Trim() -split '\s+', 3
                if ($f.Count -eq 3 -and $f[1] -eq $uid -and (Split-Path -Leaf $f[2]) -in @('Valheim', 'valheim.x86_64')) { $mine += $f[0] }
            }
        }
        if ($mine.Count -ne 0) { 'VT-PLAYING process ' + ($mine -join ',') } elseif ($unowned.Count -ne 0) { 'VT-PLAYING unowned ' + ($unowned -join ',') } else { 'VT-PLAYING no' }
        """.ReplaceLineEndings("\n");

    // macOS and Linux: this user's processes named Valheim (the macOS app, the tool's copies included) or valheim.x86_64. Steam's
    // console_log ("Game process added/removed ... ProcID N") is not read: it would add only a game under another name, and after a
    // Steam crash its unremoved entry plus a reused process id would refuse a campaign for an unrelated process.
    public static readonly string Bash = """
        set -u
        uid=$(id -u) || exit 4
        processes=$(ps -axo pid=,uid=,comm=) || exit 4
        ids=$(printf '%s\n' "$processes" | awk -v me="$uid" '
            { line=$0; sub(/^[[:space:]]+/, "", line); pid=line; sub(/[[:space:]].*$/, "", pid); rest=line; sub(/^[0-9]+[[:space:]]+/, "", rest)
              owner=rest; sub(/[[:space:]].*$/, "", owner); path=rest; sub(/^[0-9]+[[:space:]]+/, "", path)
              name=path; sub(/^.*\//, "", name)
              if (owner==me && (name=="Valheim" || name=="valheim.x86_64")) { out=out (out=="" ? "" : ",") pid } }
            END { print out }') || exit 4
        if [ -n "$ids" ]; then echo "VT-PLAYING process $ids"; else echo "VT-PLAYING no"; fi
        """.ReplaceLineEndings("\n");
}

/// <summary>
/// The client host's Steam <c>logs/connection_log.txt</c>, watched while a client starts (#257, part 2 of the lease decision). When
/// the account already plays on another computer, Steam on this machine logs <c>RecvMsgClientLoggedOff('Logged In Elsewhere')</c>
/// the moment it registers the new game process, signs out and exits by itself a few seconds later; the game quits before its menu
/// with nothing about it in the game's own logs. Seen natively on 5 Oct 2026 (steamdup1 and steamdup2). The watch turns that
/// into one clear failure. It never answers a Steam prompt and never starts or stops Steam.
/// </summary>
internal static class SteamSessionLog
{
    /// <summary>The connection_log lines Steam writes when another computer takes the account's session.</summary>
    public static readonly Regex LoggedInElsewhere = new(@"RecvMsgClientLoggedOff\('Logged In Elsewhere'\)|not auto reconnecting due to Logged In Elsewhere",
        RegexOptions.CultureInvariant);

    // How long a failed start's last look at a host's log may take, the transport's start included (SSH and Windows PowerShell take
    // seconds); the line, when Steam wrote it, is already there, so the look ends as soon as the follower has read it.
    internal static readonly TimeSpan FinalLook = TimeSpan.FromSeconds(10);

    /// <summary>The decided failure text (#257). <paramref name="account"/> is the leased account's name, when the client has one.</summary>
    public static string Message(string? account, string machine) =>
        $"{(account != null ? "Steam account " + account : "The Steam account signed in here")} is playing on another computer; Steam on this machine ({machine}) has exited; " +
        "when that session has ended, start Steam here and retry. (Steam logged 'Logged In Elsewhere' as the client started; this tool never signs in or starts Steam.)";

    /// <summary>What a client's early exit adds when Steam logged the sign-out: the decided message.</summary>
    public static string ExitHint(string message) => " after Steam logged 'Logged In Elsewhere'. " + message;

    /// <summary>
    /// The connection_log on <paramref name="host"/> and its current length, for a watch from there; null, with a warning, when the
    /// host has no Steam log this can find or read. Never fails the run: the watch only turns a known failure into a clear one.
    /// </summary>
    public static async Task<(string Path, long Offset)?> MarkAsync(IGameHost host, TimeSpan timeout, CancellationToken cancellation, string steamDirectory = "")
    {
        try
        {
            var result = (await host.RunAsync(host.Shell.Kind == HostShellKind.PowerShell ? FindPowerShell : FindBash,
                new Dictionary<string, string> { ["steam"] = steamDirectory }, timeout, cancellation).ConfigureAwait(false))
                .EnsureSuccess($"Finding Steam's connection_log on {host.Name}");
            string? found = InteractiveClient.Line(result.Stdout, "VT-STEAMLOG ");
            if (found == "none")
            {
                Console.Error.WriteLine($"Warning: no Steam connection_log.txt found on {host.Name}; a client signed out by another computer will show only as its exit.");
                return null;
            }
            // One round trip: the log's length now, then its path (which may hold spaces).
            string[] parts = (found ?? "").Split(' ', 2);
            if (parts.Length != 2 || !long.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long length) || parts[1].Length == 0)
                throw new HostOperationException($"Unexpected reply while finding Steam's connection_log on {host.Name}", result);
            return (parts[1], length);
        }
        catch (Exception error) when (error is HostOperationException or ArgumentException or IOException or InvalidOperationException && !cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine($"Warning: could not read Steam's connection_log on {host.Name} ({error.Message}); a client signed out by another computer will show only as its exit.");
            return null;
        }
    }

    /// <summary>Whether the line appears in the host's log after <paramref name="log"/>'s offset within <paramref name="timeout"/>. Never throws.</summary>
    public static async Task<bool> SeenAsync(IGameHost host, (string Path, long Offset) log, TimeSpan timeout, CancellationToken cancellation)
    {
        try
        {
            if (timeout <= TimeSpan.Zero) return false;
            var result = await host.WaitForLogAsync(log.Path, log.Offset, LoggedInElsewhere, null, timeout, cancellation).ConfigureAwait(false);
            return result.Outcome == HostLogOutcome.Matched;
        }
        catch (Exception) { return false; }
    }

    /// <summary>This machine's Steam connection_log, or null when none is found.</summary>
    public static string? LocalPath(string? home = null, string? steamDirectory = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var directories = new List<string>();
        if (!string.IsNullOrEmpty(steamDirectory)) directories.Add(steamDirectory);
        if (OperatingSystem.IsWindows())
        {
            if (Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string steamPath) directories.Add(steamPath);
            if (Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) is string installPath) directories.Add(installPath);
            string programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (programs.Length != 0) directories.Add(Path.Combine(programs, "Steam"));
        }
        else
        {
            directories.Add(Path.Combine(home, "Library", "Application Support", "Steam"));
            directories.Add(Path.Combine(home, ".local", "share", "Steam"));
            directories.Add(Path.Combine(home, ".steam", "steam"));
            directories.Add(Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"));
        }
        return directories.Select(directory => Path.Combine(directory, "logs", "connection_log.txt")).FirstOrDefault(File.Exists);
    }

    /// <summary>Whether a local log holds the line after byte <paramref name="offset"/> (at most its last 4 MB). Never throws.</summary>
    public static bool SeenInFile(string path, long offset)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // A log shorter than the offset was rotated since: all of the new one is after the launch.
            long from = stream.Length < offset ? 0 : offset;
            from = Math.Max(from, stream.Length - 4 * 1024 * 1024);
            stream.Seek(from, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            for (string? line = reader.ReadLine(); line != null; line = reader.ReadLine())
                if (LoggedInElsewhere.IsMatch(line)) return true;
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// <paramref name="ready"/>, raced with <paramref name="watch"/> (true once Steam logged the line; never throws): the line ends
    /// the start at once with <see cref="SteamLoggedInElsewhereException"/>. When the start fails another way first (the client
    /// quit, a closed state connection, a timeout), <paramref name="finalLook"/> checks the log once more, so that failure is named
    /// for what caused it. A cancelled start (the client exited; its exit names it) is not looked at here.
    /// </summary>
    public static Func<TimeSpan, CancellationToken, Task> Guard(Func<TimeSpan, CancellationToken, Task> ready,
        Func<TimeSpan, CancellationToken, Task<bool>> watch, Func<Task<bool>> finalLook, Func<string> message) =>
        async (left, token) =>
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            var watching = watch(left, stop.Token);
            var waiting = ready(left, token);
            try
            {
                // The watch first: when both have ended, the sign-out is the cause (a signed-out client quits within seconds of its menu).
                if (await Task.WhenAny(watching, waiting).ConfigureAwait(false) == watching && await watching.ConfigureAwait(false))
                {
                    // The start goes on until the caller abandons it; its own failure is no longer the one to report.
                    _ = waiting.ContinueWith(task => task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                    throw new SteamLoggedInElsewhereException(message());
                }
                await waiting.ConfigureAwait(false);
            }
            catch (Exception error) when (error is not SteamLoggedInElsewhereException && !token.IsCancellationRequested)
            {
                stop.Cancel();
                if (await finalLook().ConfigureAwait(false)) throw new SteamLoggedInElsewhereException(message(), error);
                throw;
            }
            finally { stop.Cancel(); }
        };

    // Steam's connection_log on the host and its length ("VT-STEAMLOG <bytes> <path>"): Windows finds Steam from the user's SteamPath,
    // the machine's InstallPath, then Program Files; pwsh elsewhere and bash try the user's usual Steam directories.
    // Variable: steam (an optional Steam directory to try first; tests).
    public static readonly string FindPowerShell = """
        $dirs = @()
        if ($steam) { $dirs += $steam }
        if ([Environment]::OSVersion.Platform -ne [PlatformID]::Unix) {
            $p = [Microsoft.Win32.Registry]::GetValue('HKEY_CURRENT_USER\Software\Valve\Steam', 'SteamPath', $null)
            if ($p) { $dirs += [string]$p }
            $p = [Microsoft.Win32.Registry]::GetValue('HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam', 'InstallPath', $null)
            if ($p) { $dirs += [string]$p }
            $x86 = [Environment]::GetFolderPath('ProgramFilesX86')
            if ($x86) { $dirs += (Join-Path $x86 'Steam') }
        } else {
            $vtHome = [Environment]::GetFolderPath('UserProfile')
            $dirs += @((Join-Path $vtHome 'Library/Application Support/Steam'), (Join-Path $vtHome '.local/share/Steam'), (Join-Path $vtHome '.steam/steam'),
                (Join-Path $vtHome '.var/app/com.valvesoftware.Steam/.local/share/Steam'))
        }
        foreach ($d in $dirs) {
            $f = [IO.Path]::GetFullPath([IO.Path]::Combine($d, 'logs', 'connection_log.txt'))
            if ([IO.File]::Exists($f)) { 'VT-STEAMLOG ' + (New-Object IO.FileInfo $f).Length + ' ' + $f; exit 0 }
        }
        'VT-STEAMLOG none'
        """.ReplaceLineEndings("\n");

    public static readonly string FindBash = """
        set -u
        for d in ${steam:+"$steam"} "${HOME:-/nonexistent}/Library/Application Support/Steam" "${HOME:-/nonexistent}/.local/share/Steam" \
            "${HOME:-/nonexistent}/.steam/steam" "${HOME:-/nonexistent}/.var/app/com.valvesoftware.Steam/.local/share/Steam"; do
            f="$d/logs/connection_log.txt"
            if [ -f "$f" ]; then size=$(wc -c < "$f") || exit 4; echo "VT-STEAMLOG $(printf '%s' "$size" | tr -d ' \t') $f"; exit 0; fi
        done
        echo "VT-STEAMLOG none"
        """.ReplaceLineEndings("\n");
}

/// <summary>A client's start ended because its Steam account plays on another computer (Steam logged "Logged In Elsewhere").</summary>
internal sealed class SteamLoggedInElsewhereException : InvalidOperationException
{
    public SteamLoggedInElsewhereException(string message) : base(message) { }
    public SteamLoggedInElsewhereException(string message, Exception inner) : base(message, inner) { }
}
