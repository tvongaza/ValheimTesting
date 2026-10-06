using System.Text;
using System.Text.RegularExpressions;

namespace Valheim.Testing.Game;

/// <summary>
/// Steam's <c>logs/connection_log.txt</c>, watched while a client starts (#257, part 2 of the lease decision). When the account
/// already plays on another computer, Steam on this machine logs <c>RecvMsgClientLoggedOff('Logged In Elsewhere')</c> the moment
/// it registers the new game process, signs out and exits by itself a few seconds later; the game quits before its menu with
/// nothing about it in the game's own logs. Seen natively on 5 Oct 2026 (steamdup1 and steamdup2). The watch turns that into one
/// clear failure. It never answers a Steam prompt and never starts or stops Steam. This half is the line, the message and this
/// machine's log; a client host's log is watched through its shell by <c>SteamSessionLogOnHost</c>.
/// </summary>
internal static class SteamSessionLog
{
    /// <summary>The connection_log lines Steam writes when another computer takes the account's session.</summary>
    public static readonly Regex LoggedInElsewhere = new(@"RecvMsgClientLoggedOff\('Logged In Elsewhere'\)|not auto reconnecting due to Logged In Elsewhere",
        RegexOptions.CultureInvariant);

    /// <summary>The decided failure text (#257). <paramref name="account"/> is the leased account's name, when the client has one.</summary>
    public static string Message(string? account, string machine) =>
        $"{(account != null ? "Steam account " + account : "The Steam account signed in here")} is playing on another computer; Steam on this machine ({machine}) has exited; " +
        "when that session has ended, start Steam here and retry. (Steam logged 'Logged In Elsewhere' as the client started; this tool never signs in or starts Steam.)";

    /// <summary>What a client's early exit adds when Steam logged the sign-out: the decided message.</summary>
    public static string ExitHint(string message) => " after Steam logged 'Logged In Elsewhere'. " + message;

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
}

/// <summary>A client's start ended because its Steam account plays on another computer (Steam logged "Logged In Elsewhere").</summary>
internal sealed class SteamLoggedInElsewhereException : InvalidOperationException
{
    public SteamLoggedInElsewhereException(string message) : base(message) { }
    public SteamLoggedInElsewhereException(string message, Exception inner) : base(message, inner) { }
}
