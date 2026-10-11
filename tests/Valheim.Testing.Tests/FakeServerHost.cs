using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.IO.Enumeration;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;
using Valheim.Testing.GameSessions;

/// <summary>
/// A host that keeps its directories in a local mirror and answers the server, install and client scripts the way a Linux host
/// does, without a shell: the full remote lifecycle runs against it. Every script run is recorded with its variables.
/// </summary>
internal sealed class FakeServerHost : IGameHost
{
    private readonly string _mirror;
    private readonly FakeOwnedServer? _server;
    private readonly GameHostKind _kind;
    private readonly object _sync = new();
    private readonly Dictionary<int, (string Start, Func<CancellationToken, Task<int>> Exit, Action Stop)> _processes = [];
    private int _nextClient = 77;

    public FakeServerHost(string name, string mirror, FakeOwnedServer? server = null, int tunnelPort = 15577, GameHostKind kind = GameHostKind.Ssh, bool windows = false)
    {
        Name = name; _mirror = mirror; _server = server; TunnelPort = tunnelPort; _kind = kind; Windows = windows;
    }
    public string Name { get; }
    /// <summary>What a Windows host's server-task logon check replies: s4u, interactive, or "unsupported &lt;why&gt;".</summary>
    public string ServerTaskLogon { get; set; } = "s4u";
    public GameHostKind Kind => _kind;
    public bool Windows { get; }
    public HostShell Shell => Windows ? HostShell.WindowsPowerShell : HostShell.Bash;
    public int TunnelPort { get; }
    public List<(string Script, IReadOnlyDictionary<string, string> Variables)> Runs { get; } = [];
    public List<string> Claims { get; } = [];
    /// <summary>The journal kind whose append this host refuses (a full disk), or null.</summary>
    public string? JournalFailsFor { get; set; }
    public List<string> Releases { get; } = [];
    /// <summary>Another run's claimant holding the lock.</summary>
    public string? HeldBy { get; set; }
    public Dictionary<string, HostResult> Failures { get; } = [];
    /// <summary>What the preloader-log check replies (HostedClientScripts.Preloader): none by default.</summary>
    public string PreloaderReply { get; set; } = "VT-PRELOADER-END\n";
    /// <summary>Scripts that hang until cancelled.</summary>
    public HashSet<string> Hang { get; } = [];
    /// <summary>Runs when a script starts, by its name.</summary>
    public Action<string>? BeforeScript { get; set; }
    public Exception? TunnelFailure { get; set; }
    public bool PortBusy { get; set; }
    /// <summary>The port check's whole reply when set; else free or busy by <see cref="PortBusy"/>.</summary>
    public string? PortReply { get; set; }
    public bool GameActive { get; set; }
    /// <summary>The conflicting processes' IDs the game-process check names when <see cref="GameActive"/> (comma separated), or none.</summary>
    public string GameProcessIds { get; set; } = "";
    public long AvailableCopyBytes { get; set; } = 100L << 30;
    /// <summary>The server ignores the clean stop's SIGINT, so it is killed after the wait.</summary>
    public bool IgnoreQuit { get; set; }
    public bool ClientWritesBepInExLog { get; set; } = true;
    /// <summary>The crossplay library check's reply: by default libparty.so loads.</summary>
    public string PartyReply { get; set; } = "VT-LDD \tlibc.so.6 => /lib/x86_64-linux-gnu/libc.so.6 (0x1)\nVT-PARTY checked valheim_server_Data/Plugins/libparty.so 0\n";
    /// <summary>The signed-in Steam user check's reply.</summary>
    public string SteamUserReply { get; set; } = "VT-STEAMUSER unreadable the host user has no loginusers.vdf in its Steam directories\n";
    /// <summary>Whether Valheim runs on this host, as SteamAccountInUse's scripts answer (#257): none by default.</summary>
    public string PlayingReply { get; set; } = "VT-PLAYING no\n";
    /// <summary>The host path of Steam's connection_log, as SteamSessionLog's scripts find it (#257); none by default, so no watch runs.</summary>
    public string? SteamLog { get; set; }
    private string? _runtime; // The host runtime of the last server start, whose log a clean stop appends to.
    public List<FakeForward> Tunnels { get; } = [];
    /// <summary>What the copy does to the runtime after copying, for example editing a file.</summary>
    public Action<string>? AfterCopy { get; set; }
    /// <summary>What the staging apply leaves in the runtime after applying, for example a loader file it failed to remove.</summary>
    public Action<string>? AfterApply { get; set; }
    /// <summary>Controlled delay for tests that prove independent actors prepare concurrently.</summary>
    public Func<Task>? BeforeShip { get; set; }
    /// <summary>Runs after a lock release is recorded, with the releasing owner.</summary>
    public Action<string>? AfterRelease { get; set; }
    /// <summary>Simulates a transport failure after the remote staging directory has been populated.</summary>
    public Action<string>? AfterShip { get; set; }
    public List<(string Game, string Start)> Stops { get; } = [];
    /// <summary>The Steam-account leases on this (lease) host: account → holder and lease id.</summary>
    public Dictionary<string, (string Holder, string LeaseId)> Leases { get; } = [];
    /// <summary>What the process check reads as a running process's command-line hash, by process ID; by default <see cref="CommandLineSha256"/> of its ID.</summary>
    public Dictionary<int, string> CommandLines { get; } = [];
    /// <summary>Runs after each process check, for example to change a process between two checks.</summary>
    public Action? AfterProbe { get; set; }
    public static string CommandLineSha256(string pid) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("fake game " + pid)));
    /// <summary>The machine name the host reports (<c>[Environment]::MachineName</c>), as a runner on it journals it.</summary>
    public string MachineName { get; set; } = "FAKE-HOST";
    /// <summary>What the macOS bundle check answers on a source install (read only) and on a copy it repairs (MacAppBundle).</summary>
    public string MacBundleInspect { get; set; } = "VT-BUNDLE accepted 0 -";
    public string MacBundleRepair { get; set; } = "VT-BUNDLE accepted 0 -";
    /// <summary>A process this host reports as running until it is stopped; for journal tests.</summary>
    public void Running(int pid, string start)
    {
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync) _processes[pid] = (start, ct => exit.Task.WaitAsync(ct), () => exit.TrySetResult(137));
    }
    public IReadOnlyList<string> Scripts { get { lock (_sync) return Runs.Select(run => run.Script).ToList(); } }

    public string Local(string hostPath) => Path.Combine([_mirror, .. hostPath.Replace(':', '/').Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)]);

    private static bool CharacterFile(string path, IReadOnlyDictionary<string, string> v)
    {
        string name = Path.GetFileName(path);
        return v["names"].Split('\n').Any(entry => name.Equals(entry, StringComparison.OrdinalIgnoreCase)) ||
            v["prefixes"].Split('\n').Any(entry => name.StartsWith(entry, StringComparison.OrdinalIgnoreCase));
    }
    private static HostResult Ok(string stdout) => new(HostOutcome.Exited, 0, stdout, "", TimeSpan.FromMilliseconds(3), false);
    public static HostResult TransportFailure => new(HostOutcome.TransportFailed, null, "", "ssh: connect to host test port 22: Connection refused", TimeSpan.FromMilliseconds(3), false);

    // Each embedded script by identity (the very constant the production code passes; ReferenceEqualityComparer, never its
    // text) → the name Runs records and RunAsync answers by. A script missing here is refused, naming its first line.
    private static readonly Dictionary<string, string> s_scriptNames = Names(
        ("copy", [HostInstallScripts.Copy, HostInstallScripts.PowerShellCopy]),
        ("list", [HostInstallScripts.BashList, HostInstallScripts.PowerShellList]),
        ("port", [HostInstallScripts.BashPort, HostInstallScripts.PowerShellPort]),
        ("start", [HostServerScripts.Start, HostServerScripts.WindowsStart]),
        ("keep", [HostServerScripts.Keep, HostServerScripts.WindowsKeep]),
        ("retire", [HostedRunScripts.Retire, HostedRunScripts.WindowsRetire]),
        ("drop-kept", [HostedRunScripts.DropKept, HostedRunScripts.WindowsDropKept]),
        ("apply-stage", [HostedRuntimeStage.WindowsApply, HostedRuntimeStage.BashApply]),
        ("profile-seed", [HostedRuntimeStage.WindowsProfileSeed, HostedRuntimeStage.BashProfileSeed]),
        ("keep-logs", [RunRecovery.BashKeepLogs, RunRecovery.WindowsKeepLogs]),
        ("cleanup-stage", [HostedRuntimeStage.WindowsCleanup, HostedRuntimeStage.BashCleanup]),
        ("character-install", [HostedCharacterStage.WindowsInstall, HostedCharacterStage.BashInstall]),
        ("character-retire", [HostedCharacterStage.WindowsRetire, HostedCharacterStage.BashRetire]),
        ("character-folders", [HostedCharacterStage.PowerShellDirectories, HostedCharacterStage.BashDirectories]),
        ("character-drop", [HostedCharacterStage.WindowsDropStage, HostedCharacterStage.BashDropStage]),
        ("game-process", [HostedRuntimeStage.WindowsProcessCheck, HostedRuntimeStage.BashProcessCheck]),
        ("copy-space", [HostCopyCapacityProbe.Windows, HostCopyCapacityProbe.Bash]),
        ("party", [CrossplayLibraryScripts.Check]),
        ("server-logon", [HostServerScripts.WindowsServerLogonCheck]),
        ("wait", [InteractiveScripts.LinuxWait, InteractiveScripts.WindowsWait]),
        ("stop", [InteractiveScripts.LinuxStop, HostServerScripts.WindowsStop]),
        ("client-start", [InteractiveScripts.LinuxStart]),
        ("client-keep", [HostedClientScripts.BashKeep]),
        ("preloader", [HostedClientScripts.BashPreloader, HostedClientScripts.PowerShellPreloader]),
        ("move-aside", [HostedClientScripts.BashMoveAside]),
        ("preflight-read", [HostClientPreflight.BashRead, HostClientPreflight.PowerShellRead]),
        ("preflight-exists", [HostClientPreflight.BashExists, HostClientPreflight.PowerShellExists]),
        ("steam-user", [SteamSignedInUsers.PowerShell, SteamSignedInUsers.Bash]),
        ("steam-playing", [SteamAccountInUse.PowerShell, SteamAccountInUse.Bash]),
        ("steam-log", [SteamSessionLogOnHost.FindPowerShell, SteamSessionLogOnHost.FindBash]),
        ("journal", [RunJournalOnHost.BashAppend, RunJournalOnHost.WindowsAppend]),
        ("journal-read", [RunJournalOnHost.BashRead, RunJournalOnHost.WindowsRead]),
        ("journal-read-all", [RunJournalOnHost.BashReadAll, RunJournalOnHost.WindowsReadAll]),
        ("pid-file", [RunJournalStatus.BashPidFiles, RunJournalStatus.WindowsPidFiles]),
        ("process-probe", [HostProcessProbe.Bash, HostProcessProbe.Windows]),
        ("mac-bundle", [MacAppBundle.Bash]),
        ("machine-name", [RunJournalStatus.WindowsMachineName]),
        ("lease", [LeaseScripts.Bash, LeaseScripts.PowerShell]),
        ("world-entries", [HostedWorldOnHost.WindowsEntries, HostedWorldOnHost.BashEntries]),
        ("world-move", [HostedWorldOnHost.WindowsMoveOut, HostedWorldOnHost.BashMoveOut]));
    private static Dictionary<string, string> Names(params (string Name, string[] Scripts)[] table)
    {
        var names = new Dictionary<string, string>(ReferenceEqualityComparer.Instance);
        foreach (var (name, scripts) in table) foreach (string script in scripts) names.Add(script, name); // the same constant twice throws
        return names;
    }
    private static string ScriptName(string script) => s_scriptNames.TryGetValue(script, out string? name) ? name
        : throw new InvalidOperationException("Unexpected script on the fake host: " + script.Split('\n')[0]);

    public async Task<HostResult> RunAsync(string script, IReadOnlyDictionary<string, string>? variables, TimeSpan timeout, CancellationToken cancellation = default)
    {
        var v = variables ?? new Dictionary<string, string>();
        // As a real host does: a script is not started on a cancelled token.
        cancellation.ThrowIfCancellationRequested();
        string name = ScriptName(script);
        lock (_sync) Runs.Add((name, v));
        BeforeScript?.Invoke(name);
        // A script that never answers until the caller gives up: a hung SSH session, a stuck removal.
        if (Hang.Contains(name)) await Task.Delay(Timeout.Infinite, cancellation);
        if (Failures.TryGetValue(name, out var failure)) return failure;
        switch (name)
        {
            case "machine-name": return Ok("VT-MACHINE " + MachineName + "\n");
            case "mac-bundle": return Ok((v["repair"] == "1" ? MacBundleRepair : MacBundleInspect) + "\n");
            case "game-process": return Ok(GameActive ? $"VT-GAME busy {GameProcessIds}\n".Replace(" \n", "\n") : "VT-GAME idle\n");
            case "server-logon": return Ok("VT-LOGON " + ServerTaskLogon + "\n");
            case "copy-space":
                return Ok($"VT-STORAGE {DiskSpace.DirectoryBytes(Local(v["source"]))} {AvailableCopyBytes} " +
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(Windows ? "C:\\" : "/")) + "\n");
            case "preflight-exists":
                return Ok(string.Concat(v["paths"].Split('\n').Where(relative => relative.Length != 0 && File.Exists(Local(HostPath.Join(v["root"], relative))))
                    .Select(relative => "VT-EXISTS file " + relative + "\n")) + "VT-EXISTS done\n");
            case "preflight-read":
            {
                string file = Local(v["path"]);
                if (!File.Exists(file)) return Ok("VT-PREFLIGHT missing\n");
                var bytes = File.ReadAllBytes(file);
                return Ok(bytes.Length > 4194304 ? "VT-PREFLIGHT too-large\n" : "VT-PREFLIGHT " + Convert.ToBase64String(bytes) + "\n");
            }
            case "copy":
                CopyDirectory(Local(v["source"]), Local(v["dest"]));
                // As the scripts: a skipped top-level directory of the source is not copied.
                foreach (string skipped in v.GetValueOrDefault("skip", "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    if (Directory.Exists(Path.Combine(Local(v["dest"]), skipped))) Directory.Delete(Path.Combine(Local(v["dest"]), skipped), recursive: true);
                AfterCopy?.Invoke(Local(v["dest"]));
                return Ok("VT-COPY copied\n");
            case "profile-seed":
            {
                string destination = Local(v["runtime"]);
                if (Directory.Exists(destination)) return Ok("VT-PROFILE-EXISTS\n");
                Directory.CreateDirectory(destination);
                foreach (string encoded in v["gameFiles"].Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Concat(v["files"].Split('\n', StringSplitOptions.RemoveEmptyEntries)))
                {
                    string relative = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                    string target = Path.Combine(destination, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(Path.Combine(Local(v["source"]), relative), target);
                }
                return Ok("VT-PROFILE-SEEDED\n");
            }
            case "apply-stage":
            {
                string runtime = Local(v["runtime"]), stage = Local(v["stage"]);
                string sourceConfig = Path.Combine(runtime, "BepInEx", "config", "BepInEx.cfg");
                byte[]? retainedConfig = v.GetValueOrDefault("preserveConfig") == "true" ? File.ReadAllBytes(sourceConfig) : null;
                foreach (string folder in new[] { "plugins", "scripts", "config", "patchers" })
                {
                    string directory = Path.Combine(runtime, "BepInEx", folder);
                    if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                    Directory.CreateDirectory(directory);
                }
                // As the scripts: each loader file or folder named, removed when a reviewed package replaces the loader.
                foreach (string entry in v["loader"].Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (File.Exists(Path.Combine(runtime, entry))) File.Delete(Path.Combine(runtime, entry));
                    else if (Directory.Exists(Path.Combine(runtime, entry))) Directory.Delete(Path.Combine(runtime, entry), true);
                }
                foreach (string line in v["files"].Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    string relative = Encoding.UTF8.GetString(Convert.FromBase64String(line));
                    string target = Path.Combine(runtime, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(Path.Combine(stage, relative), target, overwrite: true);
                }
                if (retainedConfig != null) File.WriteAllBytes(sourceConfig, retainedConfig);
                Directory.Delete(stage, recursive: true);
                AfterApply?.Invoke(runtime);
                return Ok("VT-STAGED selected files only\n");
            }
            // As RunRecovery's real scripts do (RunRecoveryTests runs them on this machine): each log in the copy written since the
            // copy was made (since, Unix seconds), and a client's Player.log (playerlog, a mirrored path here); older ones are named.
            case "keep-logs":
            {
                var reply = new StringBuilder();
                var copied = DateTimeOffset.FromUnixTimeSeconds(long.Parse(v["since"], System.Globalization.CultureInfo.InvariantCulture)).UtcDateTime;
                void Save(string from, string name)
                {
                    if (!File.Exists(from)) return;
                    if (File.GetLastWriteTimeUtc(from) < copied) { reply.Append("VT-LOG-OLDER " + name + "\n"); return; }
                    string to = Path.Combine(Local(v["keep"]), name);
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!); File.Copy(from, to, overwrite: true);
                    reply.Append("VT-LOG-KEPT " + name + "\n");
                }
                string runtime = Local(v["runtime"]);
                if (Directory.Exists(runtime))
                {
                    Save(Path.Combine(runtime, "BepInEx", "LogOutput.log"), "BepInEx/LogOutput.log");
                    Save(Path.Combine(runtime, "toolkit-unity.log"), "toolkit-unity.log");
                    foreach (string file in Directory.GetFiles(runtime, "preloader_*.log")) Save(file, Path.GetFileName(file));
                }
                if (v["client"] == "true" && v["playerlog"].Length != 0) Save(Local(v["playerlog"]), "Player.log");
                return Ok(reply + "VT-LOGS-DONE\n");
            }
            case "cleanup-stage":
                foreach (string path in new[] { v["runtime"], v["stage"] }.Where(path => path.Length != 0))
                    if (Directory.Exists(Local(path))) Directory.Delete(Local(path), recursive: true);
                if (v.TryGetValue("parent", out string? parent) && Directory.Exists(Local(parent)) && !Directory.EnumerateFileSystemEntries(Local(parent)).Any())
                    Directory.Delete(Local(parent));
                return Ok("VT-STAGE-CLEANED\n");
            // As the real scripts do (HostedCharacterStageShellTests runs them): the rule arrives as variables.
            case "character-folders":
            {
                // The scripts' search, on this mirror (CharacterDirectoryTests runs the real scripts): a Windows host's user is
                // C:\Users\tester with Steam in Program Files and no registered SteamPath, a Linux host's /home/tester (no Flatpak
                // pairing here), a macOS host's /Users/tester.
                string home = v["userhome"].Length != 0 ? v["userhome"] : Windows ? @"C:\Users\tester" : v["platform"] == "macos" ? "/Users/tester" : "/home/tester";
                string Join(params string[] parts) => Windows ? string.Join('\\', parts) : string.Join('/', parts);
                string characters = v["characters"].Length != 0 ? v["characters"] : Windows ? Join(home, "AppData", "LocalLow", "IronGate", "Valheim", "characters_local")
                    : v["platform"] == "macos" ? home + "/Library/Application Support/IronGate/Valheim/characters_local" : home + "/.config/unity3d/IronGate/Valheim/characters_local";
                string[] userdata = v["userdata"].Length != 0 ? [v["userdata"]] : Windows ? [@"C:\Program Files (x86)\Steam\userdata"]
                    : v["platform"] == "macos" ? [home + "/Library/Application Support/Steam/userdata"] : [home + "/.local/share/Steam/userdata", home + "/.steam/steam/userdata", home + "/.var/app/com.valvesoftware.Steam/.local/share/Steam/userdata"];
                var reply = new StringBuilder();
                if (Directory.Exists(Local(characters))) reply.Append("VT-CHARDIR characters found " + characters + "\n");
                reply.Append("VT-CHARDIR characters tried " + characters + "\n");
                foreach (string folder in userdata)
                {
                    reply.Append("VT-CHARDIR userdata tried " + folder + "\n");
                    if (Directory.Exists(Local(folder))) { reply.Append("VT-CHARDIR userdata found " + folder + "\n"); break; }
                }
                return Ok(reply.ToString());
            }
            case "character-install":
            {
                string characters = Local(v["characters"]), userdata = Local(v["userdata"]);
                if (!Directory.Exists(characters) || !Directory.Exists(userdata)) return Ok("VT-CHAR missing-directory\n");
                var folders = new[] { characters, Local(v["cloud"]) }.Concat(Directory.GetDirectories(userdata)
                    .Select(account => Path.Combine(account, v["remote"])));
                if (folders.Where(Directory.Exists).SelectMany(folder => Directory.GetFiles(folder)).Any(path => CharacterFile(path, v)))
                    return Ok("VT-CHAR collision\n");
                File.Copy(Path.Combine(Local(v["stage"]), v["save"]), Path.Combine(characters, v["save"]));
                return Ok("VT-CHAR staged\n");
            }
            case "character-retire":
            {
                string characters = Local(v["characters"]);
                if (Directory.Exists(characters))
                    foreach (string file in Directory.GetFiles(characters).Where(path => CharacterFile(path, v))) File.Delete(file);
                return Ok("VT-CHAR-RETIRED\n");
            }
            case "character-drop":
                if (Directory.Exists(Local(v["stage"]))) Directory.Delete(Local(v["stage"]), recursive: true);
                return Ok("VT-CHAR-STAGE-DROPPED\n");
            // As RunJournal's scripts do (RunJournalShellTests runs the real bash pair): one decoded line appended per entry.
            case "journal":
            {
                if (JournalFailsFor != null && Encoding.UTF8.GetString(Convert.FromBase64String(v["line"])).Contains($"\"kind\":\"{JournalFailsFor}\"", StringComparison.Ordinal))
                    return new HostResult(HostOutcome.Exited, 4, "", "No space left on device", TimeSpan.Zero, false);
                string file = Local(v["journal"] + "/" + v["run"] + "/" + v["actor"] + ".jsonl");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.AppendAllText(file, Encoding.UTF8.GetString(Convert.FromBase64String(v["line"])) + "\n");
                return Ok("VT-JOURNALED\n");
            }
            case "journal-read":
            {
                string directory = Local(v["journal"] + "/" + v["run"]);
                var text = new StringBuilder();
                if (Directory.Exists(directory))
                    foreach (string file in Directory.GetFiles(directory, "*.jsonl"))
                        foreach (string line in File.ReadAllLines(file).Where(line => line.Length != 0))
                            text.Append("VT-JOURNAL ").Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(line))).Append('\n');
                return Ok(text.Append("VT-JOURNAL-END\n").ToString());
            }
            case "journal-read-all":
            {
                string directory = Local(v["journal"]);
                var text = new StringBuilder();
                if (Directory.Exists(directory))
                    foreach (string file in Directory.GetFiles(directory, "*.jsonl", SearchOption.AllDirectories))
                        text.Append("VT-JOURNAL-FILE ").Append(Convert.ToBase64String(File.ReadAllBytes(file))).Append('\n');
                return Ok(text.Append("VT-JOURNAL-END\n").ToString());
            }
            // As RunJournalStatus's pid-file scripts do: each launch directory's pid file, in order.
            case "pid-file":
            {
                var text = new StringBuilder();
                var dirs = v["dirs"].Split('\n');
                for (int i = 0; i < dirs.Length; i++)
                {
                    string file = Path.Combine(Local(dirs[i]), "pid");
                    text.Append(File.Exists(file) ? $"VT-PIDFILE {i} file {Convert.ToBase64String(File.ReadAllBytes(file))}\n" : $"VT-PIDFILE {i} missing\n");
                }
                return Ok(text.Append("VT-PIDFILE-END\n").ToString());
            }
            // As HostProcessProbe's scripts do: a process this host started runs until it exits, with a command line of its ID.
            case "process-probe":
            {
                var text = new StringBuilder();
                foreach (string pair in v["processes"].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    string id = pair[..pair.IndexOf(':')], start = pair[(pair.IndexOf(':') + 1)..], asked = start.Length == 0 ? "-" : start;
                    (string Start, Func<CancellationToken, Task<int>> Exit, Action Stop) process;
                    bool known; lock (_sync) known = _processes.TryGetValue(int.Parse(id), out process);
                    if (!known || process.Exit(CancellationToken.None).IsCompleted) text.Append($"VT-PROC {id} {asked} gone - -\n");
                    else if (start.Length != 0 && start != process.Start) text.Append($"VT-PROC {id} {asked} reused {process.Start} -\n");
                    else text.Append($"VT-PROC {id} {asked} same {process.Start} {CommandLines.GetValueOrDefault(int.Parse(id)) ?? CommandLineSha256(id)}\n");
                }
                AfterProbe?.Invoke();
                return Ok(text.Append("VT-PROC-END\n").ToString());
            }
            // As LeaseScripts do for list and release: an account is held by its holder until released with its own lease id.
            case "lease":
                switch (v["action"])
                {
                    case "claim":
                        // The first free account, as the real claim takes it (one claim number per account here).
                        lock (_sync)
                            foreach (string account in v["accounts"].Split('\n', StringSplitOptions.RemoveEmptyEntries))
                                if (!Leases.ContainsKey(account)) { Leases[account] = (v["owner"], v["lease"]); return Ok($"VT-LEASE claimed {account} 1\n"); }
                        return Ok("VT-LEASE none\n");
                    case "list":
                    {
                        var text = new StringBuilder();
                        foreach (string account in v["accounts"].Split('\n', StringSplitOptions.RemoveEmptyEntries))
                            lock (_sync)
                                text.Append(Leases.TryGetValue(account, out var held) ? $"VT-LEASE-ACCOUNT held {account} - {held.Holder}\n" : $"VT-LEASE-ACCOUNT free {account}\n");
                        return Ok(text.Append("VT-LEASE listed\n").ToString());
                    }
                    case "release":
                        lock (_sync)
                        {
                            if (!Leases.TryGetValue(v["account"], out var held) || held.LeaseId != v["lease"]) return Ok("VT-LEASE lost released\n");
                            Leases.Remove(v["account"]);
                            return Ok("VT-LEASE released\n");
                        }
                }
                break;
            case "list":
            {
                string root = Local(v["root"]);
                if (!Directory.Exists(root)) return Ok("VT-LIST missing\n");
                var text = new StringBuilder();
                string[] patterns = v["dirs"].Split('\n', StringSplitOptions.RemoveEmptyEntries);
                foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                    if (patterns.Length != 0 && !patterns.Any(pattern => MatchesListedPath(relative, pattern, Windows || v.GetValueOrDefault("nocase") == "1"))) continue;
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                        return Ok("VT-LIST links\n./" + relative + "\n");
                    text.Append(FileHash.Sha256(file)).Append("  ./").Append(relative).Append('\n');
                }
                if (File.Exists(Path.Combine(root, GameLaunch.ServerLinuxExecutable))) text.Append("VT-EXEC ").Append(GameLaunch.ServerLinuxExecutable).Append('\n');
                return Ok(text.Append("VT-LIST done\n").ToString());
            }
            case "port": return Ok(PortReply ?? (PortBusy ? "VT-PORT busy\n" : "VT-PORT free\n"));
            case "party": return Ok(PartyReply);
            case "steam-user": return Ok(SteamUserReply);
            case "steam-playing": return Ok(PlayingReply);
            case "steam-log": return Ok("VT-STEAMLOG " + (SteamLog is { } steamLog ? (File.Exists(Local(steamLog)) ? new FileInfo(Local(steamLog)).Length : 0) + " " + steamLog : "none") + "\n");
            case "start":
            {
                string token = Spec(v["spec"]).Single(line => line.Kind == "env" && line.Text.StartsWith("TEST_SESSION_TOKEN=", StringComparison.Ordinal)).Text["TEST_SESSION_TOKEN=".Length..];
                var process = (FakeOwnedProcess)_server!.Launch(token);
                string boot = Local(v["dir"]);
                Directory.CreateDirectory(boot);
                File.WriteAllText(Path.Combine(boot, "stdout.log"), "server stdout\n");
                _runtime = v["logroot"];
                // Unix profiles execute the installed game while BepInEx writes under the owned loader profile.
                string log = Path.Combine(Local(v["logroot"]), "BepInEx", "LogOutput.log");
                File.WriteAllText(log, $"[Info   :   BepInEx] fake boot {process.Id}\n[Info   :valheimCLI] Command server listening on 127.0.0.1:5577\n");
                // A crossplay server opens a lobby per boot, as the game logs it.
                if (Spec(v["spec"]).Any(line => line.Kind == "arg" && line.Text == "-crossplay"))
                    File.AppendAllText(log, $"[Info   : Unity Log] Created PlayFab lobby with ID \"lobby-{process.Id}\", ConnectionString \"c\" and owned by \"P\"\n");
                string start = (9000 + process.Id).ToString();
                lock (_sync) _processes[process.Id] = (start, ct => process.WaitForExitAsync(ct), () => process.Stop(TimeSpan.FromSeconds(1)));
                return Ok($"VT-SERVER started {process.Id} {start}\n");
            }
            case "client-start":
            {
                var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                int id; lock (_sync) id = _nextClient++;
                Directory.CreateDirectory(Local(v["dir"]));
                if (ClientWritesBepInExLog)
                    File.WriteAllText(Path.Combine(Local(v["install"]), "BepInEx", "LogOutput.log"), "[Info   :valheimCLI] Command server listening on 127.0.0.1:5578\n");
                lock (_sync) _processes[id] = ("555", ct => exit.Task.WaitAsync(ct), () => exit.TrySetResult(137));
                return Ok($"VT-INTERACTIVE started {id} 555\n");
            }
            case "wait":
            {
                (string Start, Func<CancellationToken, Task<int>> Exit, Action Stop) process;
                lock (_sync) process = _processes[int.Parse(v["game"])];
                if (process.Start != v["start"]) return Ok("VT-WAIT exited ?\n");
                return Ok($"VT-WAIT exited {await process.Exit(cancellation)}\n");
            }
            case "stop":
            {
                lock (_sync) Stops.Add((v["game"], v["start"]));
                (string Start, Func<CancellationToken, Task<int>> Exit, Action Stop) process;
                lock (_sync) if (!_processes.TryGetValue(int.Parse(v["game"]), out process) || process.Start != v["start"]) return Ok("VT-STOP gone\n");
                process.Stop();
                if (v.TryGetValue("quit", out string? quit) && quit != "0" && !IgnoreQuit)
                {
                    // The game quits by itself. A server's shutdown retires its boot's lobby, as 1.0.16 logs it; a client host has no server runtime.
                    if (_runtime == null) return Ok("VT-STOP quit\n");
                    string log = Path.Combine(Local(_runtime), "BepInEx", "LogOutput.log");
                    string text = File.Exists(log) ? File.ReadAllText(log) : "";
                    File.AppendAllText(log, "[Info   : Unity Log] Unregister PlayFab server \"MyModTest\" and leaving network \"n\"\n" +
                        string.Concat(CrossplayServer.LobbyCreated.Matches(text).Select(m => $"[Info   : Unity Log] Deactivated PlayFab lobby {m.Groups["lobby"].Value}\n")));
                    return Ok("VT-STOP quit\n");
                }
                return Ok("VT-STOP stopped\n");
            }
            case "keep":
            {
                string runtime = Local(v["runtime"]), boot = Local(v["dir"]);
                var logs = v["logs"].Split('\n');
                for (int i = 0; i < logs.Length; i++)
                {
                    string source = Path.Combine(runtime, logs[i]);
                    if (File.Exists(source)) File.Move(source, Path.Combine(boot, $"game-{i}.log"));
                    else File.WriteAllText(Path.Combine(boot, $"game-{i}.log.absent"), "absent");
                }
                return Ok("VT-KEPT\n");
            }
            case "retire":
            {
                // As the bash script: only <run>/runtime, listed files within the limits, then the copy goes.
                string runtimePath = v["runtime"];
                if (!runtimePath.Replace('\\', '/').EndsWith("/" + v["run"] + "/runtime", StringComparison.Ordinal)) return new HostResult(HostOutcome.Exited, 3, "", "", TimeSpan.Zero, false);
                string runtime = Local(runtimePath), keep = Local(v["keep"]);
                Directory.CreateDirectory(keep);
                if (!Directory.Exists(runtime)) return Ok("VT-RETIRED 0 0\n");
                long perFile = long.Parse(v["perfile"]), total = long.Parse(v["total"]), kept = 0;
                var reply = new StringBuilder();
                if (v.TryGetValue("replaceLoader", out var replace) && replace == "true")
                {
                    foreach (string file in new[] { "winhttp.dll", "doorstop_config.ini", "libdoorstop.dylib" })
                        File.Delete(Path.Combine(runtime, file));
                    foreach (string dir in new[] { "BepInEx/core", "doorstop_libs" })
                        if (Directory.Exists(Path.Combine(runtime, dir))) Directory.Delete(Path.Combine(runtime, dir), true);
                }
                foreach (string line in v["files"].Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    string relative = Encoding.UTF8.GetString(Convert.FromBase64String(line)), source = Path.Combine(runtime, relative);
                    if (!File.Exists(source)) { reply.Append($"VT-NOTKEPT {line} -1\n"); continue; }
                    long size = new FileInfo(source).Length;
                    if (size > perFile || kept + size > total) { reply.Append($"VT-NOTKEPT {line} {size}\n"); continue; }
                    string target = Path.Combine(keep, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(source, target);
                    kept += size;
                }
                long bytes = Directory.EnumerateFiles(runtime, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
                Directory.Delete(runtime, recursive: true);
                return Ok(reply.Append($"VT-RETIRED {bytes} {kept}\n").ToString());
            }
            case "drop-kept":
                if (!v["keep"].Replace('\\', '/').EndsWith("/" + v["run"] + "/runtime-changes", StringComparison.Ordinal)) return new HostResult(HostOutcome.Exited, 3, "", "", TimeSpan.Zero, false);
                if (Directory.Exists(Local(v["keep"]))) Directory.Delete(Local(v["keep"]), recursive: true);
                return Ok("VT-DROPPED\n");
            case "preloader": return Ok(PreloaderReply);
            case "world-entries":
            case "world-move":
            {
                // Entries named for the world (<name>, <name>.*, <name>_*, any case), as the host scripts select them.
                // As the host scripts select them: any case for the refusal; for a move, the name's own case (any on Windows), its
                // <name>.* files and the game's <name>_backup* entries, never over an existing destination.
                string worlds = Local(v["worlds"]), world = v["name"], lower = world.ToLowerInvariant();
                if (name == "world-entries") Directory.CreateDirectory(worlds);
                var comparison = Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                bool Named(string leaf) => name == "world-entries"
                    ? leaf.ToLowerInvariant() is var l && (l == lower || l.StartsWith(lower + ".", StringComparison.Ordinal) || l.StartsWith(lower + "_", StringComparison.Ordinal))
                    : leaf.Equals(world, comparison) || leaf.StartsWith(world + ".", comparison) || leaf.StartsWith(world + "_backup", comparison);
                var entries = Directory.Exists(worlds) ? Directory.EnumerateFileSystemEntries(worlds).Where(entry => Named(Path.GetFileName(entry))).ToList() : [];
                if (name == "world-entries") return Ok(string.Concat(entries.Select(entry => "VT-WORLD-ENTRY " + Path.GetFileName(entry) + "\n")) + "VT-WORLD-ENTRIES done\n");
                string to = Local(v["to"]);
                Directory.CreateDirectory(to);
                if (entries.FirstOrDefault(entry => Path.Exists(Path.Combine(to, Path.GetFileName(entry)))) is { } clash)
                    return new HostResult(HostOutcome.Exited, 4, "VT-WORLD-EXISTS " + Path.GetFileName(clash) + "\n", "", TimeSpan.FromMilliseconds(3), false);
                foreach (string entry in entries)
                    if (Directory.Exists(entry)) Directory.Move(entry, Path.Combine(to, Path.GetFileName(entry))); else File.Move(entry, Path.Combine(to, Path.GetFileName(entry)));
                return Ok(string.Concat(entries.Select(entry => "VT-WORLD-MOVED " + Path.GetFileName(entry) + "\n")) + "VT-WORLD-MOVE done\n");
            }
            case "client-keep":
            {
                string dir = Local(v["dir"]);
                string source = Path.Combine(Local(v["install"]), "BepInEx", "LogOutput.log");
                if (File.Exists(source)) File.Copy(source, Path.Combine(dir, "game-0.log"));
                else File.WriteAllText(Path.Combine(dir, "game-0.log.absent"), "absent");
                File.WriteAllText(Path.Combine(dir, "game-1.log.absent"), "absent");
                return Ok("VT-KEPT\n");
            }
            case "move-aside":
            {
                string log = Local(v["log"]);
                if (!File.Exists(log)) return Ok("VT-NONE\n");
                Directory.CreateDirectory(Path.GetDirectoryName(Local(v["to"]))!);
                File.Move(log, Local(v["to"]));
                return Ok("VT-MOVED\n");
            }
        }
        throw new InvalidOperationException("Unexpected script on the fake host.");
    }

    public static IReadOnlyList<(string Kind, string Text)> Spec(string spec) =>
        spec.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split(' ', 2)).Select(parts => (parts[0], Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])))).ToList();

    public Task<HostLock> AcquireLockAsync(string lockPath, string owner, TimeSpan timeout, CancellationToken cancellation = default)
    {
        if (HeldBy != null) throw new HostLockException(new HostLockResult(HostLockState.HeldByOther, HeldBy, $"{lockPath} on {Name} is held by another run: {HeldBy}"));
        string claimant = owner + " [fake]";
        lock (_sync) Claims.Add(claimant);
        return Task.FromResult(new HostLock(this, lockPath, claimant, timeout));
    }
    public Task<HostLockResult> CheckLockAsync(string lockPath, string owner, TimeSpan timeout, CancellationToken cancellation = default)
    {
        bool held; lock (_sync) held = Claims.Contains(owner) && !Releases.Contains(owner);
        return Task.FromResult(held ? new HostLockResult(HostLockState.Yours, owner, "yours") : new HostLockResult(HostLockState.Free, null, "free"));
    }
    public Task<HostLockResult> ReleaseLockAsync(string lockPath, string owner, TimeSpan timeout, CancellationToken cancellation = default)
    {
        lock (_sync) Releases.Add(owner);
        AfterRelease?.Invoke(owner);
        return Task.FromResult(new HostLockResult(HostLockState.Released, null, "released"));
    }
    public Task<Shipment> ShipRevisionAsync(string repository, string revision, string hostDirectory, TimeSpan timeout, CancellationToken cancellation = default) => throw new NotSupportedException();
    public async Task<Shipment> ShipFilesAsync(string localDirectory, string hostDirectory, TimeSpan timeout, CancellationToken cancellation = default)
    {
        if (BeforeShip != null) await BeforeShip().ConfigureAwait(false);
        lock (_sync) Runs.Add(("ship", new Dictionary<string, string> { ["dest"] = hostDirectory }));
        CopyDirectory(localDirectory, Local(hostDirectory));
        File.WriteAllText(Path.Combine(Local(hostDirectory), "SOURCE.txt"), "files=world\n");
        AfterShip?.Invoke(Local(hostDirectory));
        return new Shipment(hostDirectory, new string('a', 64), 1, null, null);
    }
    public Task<long> LogOffsetAsync(string logPath, TimeSpan timeout, CancellationToken cancellation = default) => throw new NotSupportedException();
    public async Task<HostLogResult> WaitForLogAsync(string logPath, long fromOffset, Regex success, IReadOnlyList<Regex>? failures, TimeSpan timeout, CancellationToken cancellation = default)
    {
        lock (_sync) Runs.Add(("follow", new Dictionary<string, string> { ["log"] = logPath, ["offset"] = fromOffset.ToString() }));
        // As for a script: "follow" in BeforeScript and Hang (a log that never gets its line until the caller gives up).
        BeforeScript?.Invoke("follow");
        if (Hang.Contains("follow")) await Task.Delay(Timeout.Infinite, cancellation);
        // From the offset, as the real follower reads; a log shorter than it was replaced, so all of it counts.
        string? line = null;
        if (File.Exists(Local(logPath)))
        {
            byte[] bytes = File.ReadAllBytes(Local(logPath));
            int from = fromOffset > bytes.Length ? 0 : (int)fromOffset;
            line = Encoding.UTF8.GetString(bytes, from, bytes.Length - from).Split('\n').Select(text => text.TrimEnd('\r')).FirstOrDefault(success.IsMatch);
        }
        return new HostLogResult(line != null ? HostLogOutcome.Matched : HostLogOutcome.TimedOut, "listening", line, TimeSpan.Zero, line);
    }
    public Task<FetchedDirectory> FetchDirectoryAsync(string hostDirectory, string localDirectory, TimeSpan timeout, CancellationToken cancellation = default)
    {
        lock (_sync) Runs.Add(("fetch", new Dictionary<string, string> { ["dir"] = hostDirectory, ["local"] = localDirectory }));
        if (Directory.Exists(localDirectory)) throw new InvalidOperationException("Fetch into a new local directory.");
        CopyDirectory(Local(hostDirectory), localDirectory);
        return Task.FromResult(new FetchedDirectory(localDirectory, new string('b', 64), 1, Directory.GetFiles(localDirectory, "*", SearchOption.AllDirectories).Length));
    }
    public Task<CliTunnel> OpenCliTunnelAsync(int hostPort, TimeSpan readyTimeout, int localPort = 0, CancellationToken cancellation = default)
    {
        lock (_sync) Runs.Add(("tunnel", new Dictionary<string, string> { ["port"] = hostPort.ToString() }));
        if (TunnelFailure != null) throw TunnelFailure;
        var forward = new FakeForward(["-L", $"127.0.0.1:{TunnelPort}:127.0.0.1:{hostPort}"], listen: false);
        Tunnels.Add(forward);
        return Task.FromResult(new CliTunnel(forward, TunnelPort, hostPort));
    }

    private static bool MatchesListedPath(string relative, string pattern, bool windows)
    {
        string[] parts = relative.Split('/');
        int count = pattern.Replace('\\', '/').Split('/').Length;
        if (parts.Length < count) return false;
        string prefix = string.Join('/', parts.Take(count));
        return FileSystemName.MatchesSimpleExpression(pattern.Replace('\\', '/'), prefix, ignoreCase: windows);
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }
}
