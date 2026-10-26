using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.IO.Enumeration;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;
using Valheim.Testing.GameSessions;

/// <summary>
/// A host that keeps its directories in a local mirror and answers the server, install and client scripts the way a Linux server
/// host or a macOS client host does, without a shell: the full remote lifecycle runs against it. Every script run is recorded with its variables.
/// </summary>
internal sealed class FakeServerHost : IGameHost
{
    private readonly string? _mirror; // null: this machine itself, its host paths are this machine's (ThisMachine)
    private readonly FakeOwnedServer? _server;
    private readonly GameHostKind _kind;
    private readonly object _sync = new();
    private readonly Dictionary<int, (string Start, Func<CancellationToken, Task<int>> Exit, Action Stop)> _processes = [];
    private int _nextClient = 77;

    public FakeServerHost(string name, string mirror, FakeOwnedServer? server = null, int tunnelPort = 15577, GameHostKind kind = GameHostKind.Ssh, bool windows = false)
        : this(name, server, tunnelPort, kind, windows, mirror ?? throw new ArgumentNullException(nameof(mirror))) { }
    private FakeServerHost(string name, FakeOwnedServer? server, int tunnelPort, GameHostKind kind, bool windows, string? mirror)
    {
        Name = name; _mirror = mirror; _server = server; TunnelPort = tunnelPort; _kind = kind; Windows = windows;
    }

    /// <summary>
    /// This machine, answered in process: a local host whose paths are this machine's own (no mirror) and whose scripts answer as
    /// this machine's shell's do (pwsh on Windows, bash elsewhere), so a test of what the toolkit decides runs no shell.
    /// SourceScriptShellTests compares its settlement, listing and read answers with the real scripts, and names where the other
    /// scripts' real runs are.
    /// </summary>
    public static FakeServerHost ThisMachine(string name = "this machine", FakeOwnedServer? server = null) =>
        new(name, server, tunnelPort: 15577, GameHostKind.Local, OperatingSystem.IsWindows(), mirror: null);
    public string Name { get; }
    /// <summary>What a Windows host's server-task logon check replies: s4u, interactive, or "unsupported &lt;why&gt;".</summary>
    public string ServerTaskLogon { get; set; } = "s4u";
    public GameHostKind Kind => _kind;
    public bool Windows { get; }
    /// <summary>The host user's home as the fake reports it (its Valheim data folder lies under it, in the mirror).</summary>
    public string Home => Windows ? @"C:\Users\tester" : "/Users/tester";
    public HostShell Shell => Windows ? HostShell.Pwsh : HostShell.Bash;
    public int TunnelPort { get; }
    public List<(string Script, IReadOnlyDictionary<string, string> Variables)> Runs { get; } = [];
    public List<string> Claims { get; } = [];
    /// <summary>The journal kind whose append this host refuses (a full disk), or null.</summary>
    public string? JournalFailsFor { get; set; }
    public List<string> Releases { get; } = [];
    /// <summary>Explicit release verdicts returned before the normal successful fake release.</summary>
    public Queue<HostLockResult> ReleaseLockResults { get; } = new();
    /// <summary>Another run's claimant holding the lock.</summary>
    public string? HeldBy { get; set; }
    public Dictionary<string, HostResult> Failures { get; } = [];
    /// <summary>A complete journal-read-all reply for malformed transport-record tests, or null for the mirrored journal.</summary>
    public string? JournalReadAllReply { get; set; }
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
    /// <summary>Host paths the preflight read answers as unreadable by the host user.</summary>
    public HashSet<string> UnreadableFiles { get; } = [];
    /// <summary>The conflicting processes' IDs the game-process check names when <see cref="GameActive"/> (comma separated), or none.</summary>
    public string GameProcessIds { get; set; } = "";
    /// <summary>The server ignores the clean stop's SIGINT, so it is killed after the wait.</summary>
    public bool IgnoreQuit { get; set; }
    public bool ClientWritesBepInExLog { get; set; } = true;
    /// <summary>The crossplay library check's reply: by default libparty.so loads.</summary>
    public string PartyReply { get; set; } = "VT-LDD \tlibc.so.6 => /lib/x86_64-linux-gnu/libc.so.6 (0x1)\nVT-PARTY checked valheim_server_Data/Plugins/libparty.so 0\n";
    /// <summary>The signed-in Steam user check's reply.</summary>
    public string SteamUserReply { get; set; } = "VT-STEAMUSER unreadable the host user has no loginusers.vdf under its Steam root\n";
    /// <summary>Whether Valheim runs on this host, as SteamAccountInUse's scripts answer (#257): none by default.</summary>
    public string PlayingReply { get; set; } = "VT-PLAYING no\n";
    /// <summary>The host path of Steam's connection_log, as SteamSessionLog's scripts find it (#257); none by default, so no watch runs.</summary>
    public string? SteamLog { get; set; }
    private string? _runtime; // The host runtime of the last server start, whose log a clean stop appends to.
    public List<FakeForward> Tunnels { get; } = [];
    /// <summary>What the staging apply leaves in the runtime after applying, for example a loader file it failed to remove.</summary>
    public Action<string>? AfterApply { get; set; }
    /// <summary>What happens to a fetched directory after it arrived here, for example a file changed on the way.</summary>
    public Action<string>? AfterFetch { get; set; }
    /// <summary>Runs after the profile seed made its launch folder, for example to lose its reply.</summary>
    public Action<string>? AfterSeed { get; set; }
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
    /// <summary>Exact detached tasks registered on this fake Windows host and their current state.</summary>
    public Dictionary<string, (WindowsScheduledTask Task, WindowsScheduledTask.State State)> ScheduledTasks { get; } = [];
    /// <summary>Runs after each process check, for example to change a process between two checks.</summary>
    public Action? AfterProbe { get; set; }
    public static string CommandLineSha256(string pid) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("fake game " + pid)));
    /// <summary>The machine name the host reports (<c>[Environment]::MachineName</c>), as a runner on it journals it.</summary>
    public string MachineName { get; set; } = "FAKE-HOST";
    /// <summary>What the macOS bundle check answers on a source install (read only, MacAppBundle).</summary>
    public string MacBundleInspect { get; set; } = "VT-BUNDLE accepted 0 -";
    public string MacAgentReply { get; set; } = "VT-AGENT removed\n";
    /// <summary>What a Mac client host's console check reads: a user at the console with an unlocked GUI session.</summary>
    public string MacDesktopReply { get; set; } = "VT-MAC-USER tester\nVT-MAC-CONSOLE tester\nVT-MAC-GUI ready\n" +
        "\"IOConsoleUsers\" = ({\"kCGSSessionOnConsoleKey\"=Yes,\"kCGSessionLoginDoneKey\"=Yes,\"kCGSSessionUserNameKey\"=\"tester\"})\nVT-MAC-REGISTRY-END\n";
    /// <summary>The slices the Mac game and its Doorstop library hold, as the host's lipo reports them.</summary>
    public string MacSlices { get; set; } = "x86_64 arm64";
    /// <summary>The Windows profile-storage probe's source and runtime volumes.</summary>
    public WindowsStorageVolume ProfileSourceVolume { get; set; } = new(@"C:\", "volume-c", "NTFS", false);
    public WindowsStorageVolume ProfileRuntimeVolume { get; set; } = new(@"C:\", "volume-c", "NTFS", false);
    /// <summary>
    /// The system a bash host's profile-storage probe names (uname -s); by default a Mac's when the probed source holds a Mac
    /// dedicated server, else Linux, as only a real host of that system could hold it.
    /// </summary>
    public string? HostSystem { get; set; }
    /// <summary>The Unix profile-storage probe's mount for each probed path, by path prefix; by default the root's ext4 (APFS on a Mac).</summary>
    public Dictionary<string, UnixStorageVolume> UnixVolumes { get; } = new(StringComparer.Ordinal);
    /// <summary>The probe's trial link: its error when it fails (fs.protected_hardlinks), or null when it links.</summary>
    public string? LinkError { get; set; }
    /// <summary>A process this host reports as running until it is stopped; for journal tests.</summary>
    public void Running(int pid, string start)
    {
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync) _processes[pid] = (start, ct => exit.Task.WaitAsync(ct), () => exit.TrySetResult(137));
    }
    /// <summary>Whether a process this host started still runs.</summary>
    public bool IsRunning(int pid)
    {
        lock (_sync) return _processes.TryGetValue(pid, out var process) && !process.Exit(CancellationToken.None).IsCompleted;
    }
    public IReadOnlyList<string> Scripts { get { lock (_sync) return Runs.Select(run => run.Script).ToList(); } }

    public string Local(string hostPath) => _mirror == null ? hostPath
        : Path.Combine([_mirror, .. hostPath.Replace(':', '/').Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)]);

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
        ("list", [HostInstallScripts.BashList, HostInstallScripts.PowerShellList]),
        ("port", [HostInstallScripts.BashPort, HostInstallScripts.PowerShellPort]),
        ("apart", [HostInstallScripts.BashApart, HostInstallScripts.PowerShellApart]),
        ("start", [HostServerScripts.Start, HostServerScripts.WindowsStart]),
        ("keep", [HostServerScripts.Keep, HostServerScripts.WindowsKeep]),
        ("retire", [HostedRunScripts.Retire, HostedRunScripts.WindowsRetire]),
        ("drop-kept", [HostedRunScripts.DropKept, HostedRunScripts.WindowsDropKept]),
        ("apply-stage", [HostedRuntimeStage.WindowsApply, HostedRuntimeStage.BashApply]),
        ("profile-seed", [HostedRuntimeStage.WindowsProfileSeed, HostedRuntimeStage.BashProfileSeed]),
        ("profile-storage", [HostedRuntimeStage.WindowsProfileStorage, UnixStorageProbe.Script]),
        ("steam-runtime-verify", [MacSteamRuntimePackage.VerificationScript]),
        ("steam-runtime-stage", [MacSteamRuntimeOnHost.Stage]),
        ("keep-logs", [RunRecovery.BashKeepLogs, RunRecovery.WindowsKeepLogs]),
        ("cleanup-stage", [HostedRuntimeStage.WindowsCleanup, HostedRuntimeStage.BashCleanup]),
        ("character-install", [HostedCharacterStage.WindowsInstall, HostedCharacterStage.BashInstall]),
        ("character-retire", [HostedCharacterStage.WindowsRetire, HostedCharacterStage.BashRetire]),
        ("character-folders", [HostedCharacterStage.PowerShellDirectories, HostedCharacterStage.BashDirectories]),
        ("character-drop", [HostedCharacterStage.WindowsDropStage, HostedCharacterStage.BashDropStage]),
        ("game-process", [HostedRuntimeStage.WindowsProcessCheck, HostedRuntimeStage.BashProcessCheck]),
        ("party", [CrossplayLibraryScripts.Check]),
        ("server-logon", [HostServerScripts.WindowsServerLogonCheck]),
        ("wait", [InteractiveScripts.LinuxWait, InteractiveScripts.WindowsWait, MacProcess.Wait]),
        ("stop", [InteractiveScripts.LinuxStop, InteractiveScripts.WindowsStop, HostServerScripts.WindowsStop, MacProcess.Stop]),
        ("mac-client-stop", [InteractiveScripts.MacStop]),
        ("mac-agent-retire", [InteractiveScripts.MacAgentRetire]),
        ("mac-desktop", [RemoteMacClientPreflight.Desktop]),
        ("mac-game", [RemoteMacClientPreflight.GameSlice]),
        ("mac-loader", [RemoteMacClientPreflight.LoaderSlice]),
        ("client-start", [InteractiveScripts.MacStart, InteractiveScripts.WindowsStart]),
        ("client-keep", [HostedClientScripts.BashKeep, HostedClientScripts.PowerShellKeep]),
        ("preloader", [HostedClientScripts.BashPreloader, HostedClientScripts.PowerShellPreloader]),
        ("move-aside", [HostedClientScripts.BashMoveAside, HostedClientScripts.PowerShellMoveAside]),
        ("user-files", [UserSaveFiles.BashScript, UserSaveFiles.PowerShellScript]),
        ("mac-home", [HostedWorldOnHost.MacHome]),
        ("windows-profile", [HostedWorldOnHost.WindowsProfile]),
        ("preflight-read", [HostClientPreflight.BashRead, HostClientPreflight.PowerShellRead]),
        ("preflight-exists", [HostClientPreflight.BashExists, HostClientPreflight.PowerShellExists]),
        ("steam-user", [SteamSignedInUsers.PowerShell, SteamSignedInUsers.Bash]),
        ("steam-playing", [SteamAccountInUse.PowerShell, SteamAccountInUse.Bash]),
        ("steam-log", [SteamSessionLogOnHost.FindPowerShell, SteamSessionLogOnHost.FindBash]),
        ("journal", [RunJournalOnHost.BashAppend, RunJournalOnHost.WindowsAppend]),
        ("journal-read", [RunJournalOnHost.BashRead, RunJournalOnHost.WindowsRead]),
        ("journal-read-all", [RunJournalOnHost.BashReadAll, RunJournalOnHost.WindowsReadAll]),
        ("launch-gone", [RunRecovery.BashLaunchGone, RunRecovery.WindowsLaunchGone]),
        ("pid-file", [RunJournalStatus.BashPidFiles, RunJournalStatus.WindowsPidFiles]),
        ("process-probe", [HostProcessProbe.Unix, HostProcessProbe.Windows]),
        ("launcher-inspect", [HostServerLauncher.UnixInspect, HostServerLauncher.WindowsInspect]),
        ("launcher-retire", [HostServerLauncher.UnixRetire, HostServerLauncher.WindowsRetire]),
        ("mac-bundle", [MacAppBundle.Bash]),
        ("source-settle", [SourceSettleScripts.Bash, SourceSettleScripts.PowerShell]),
        ("source-write", [SourceSettleScripts.BashWrite, SourceSettleScripts.PowerShellWrite]),
        ("source-evidence", [SourceSettleScripts.BashEvidence, SourceSettleScripts.PowerShellEvidence]),
        ("source-reachable", [SourceSettleScripts.BashReachable, SourceSettleScripts.PowerShellReachable]),
        ("machine-name", [RunJournalStatus.WindowsMachineName]),
        ("lock", [HostScripts.Lock(HostShellKind.Bash), HostScripts.Lock(HostShellKind.PowerShell)]),
        ("scheduled-task-register", [WindowsScheduledTask.RegisterScript]),
        ("scheduled-task-inspect", [WindowsScheduledTask.InspectScript]),
        ("scheduled-task-retire", [WindowsScheduledTask.RetireScript]),
        ("lease", [LeaseScripts.Bash, LeaseScripts.PowerShell]),
        ("world-entries", [HostedWorldOnHost.WindowsEntries, HostedWorldOnHost.BashEntries]),
        ("world-move", [HostedWorldOnHost.WindowsMoveOut, HostedWorldOnHost.BashMoveOut, HostedWorldOnHost.WindowsPlace, HostedWorldOnHost.BashPlace]),
        ("world-reserve", [HostedWorldOnHost.WindowsReserve, HostedWorldOnHost.BashReserve]),
        ("world-meta", [HostedWorldOnHost.WindowsMetadata, HostedWorldOnHost.BashMetadata]));
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
            case "profile-storage" when !Windows:
            {
                string[] paths = v["paths"].Split('\n', StringSplitOptions.RemoveEmptyEntries);
                string system = HostSystem ?? (File.Exists(Path.Combine(Local(paths[0]), GameLaunch.ServerMacExecutable)) ? "Darwin" : "Linux");
                var text = new StringBuilder("VT-PROFILE-OS " + system + "\n");
                if (system is not ("Linux" or "Darwin")) return Ok(text.ToString());
                for (int i = 0; i < paths.Length; i++)
                {
                    var volume = UnixVolumes.Where(item => paths[i] == item.Key || paths[i].StartsWith(item.Key.TrimEnd('/') + "/", StringComparison.Ordinal))
                        .OrderByDescending(item => item.Key.Length).Select(item => item.Value).FirstOrDefault()
                        ?? new UnixStorageVolume("/", "2049", system == "Linux" ? "ext4" : "apfs", Writable: true);
                    text.Append($"VT-PROFILE-VOLUME {i} {volume.Device} {(volume.Writable ? 1 : 0)} {(volume.AtMountPoint ? 1 : 0)} {volume.FileSystem} ")
                        .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(volume.MountPoint))).Append('\n');
                }
                if (v.GetValueOrDefault("linktest") == "1")
                    text.Append(LinkError == null ? "VT-PROFILE-LINK ok\n" : "VT-PROFILE-LINK refused " + Convert.ToBase64String(Encoding.UTF8.GetBytes(LinkError)) + "\n");
                return Ok(text.Append("VT-PROFILE-VOLUMES\n").ToString());
            }
            case "profile-storage":
            {
                object Item(WindowsStorageVolume volume) => new
                {
                    Path = volume.Root,
                    volume.Root,
                    DriveType = volume.Network ? 4u : 3u,
                    volume.Identity,
                    volume.FileSystem,
                };
                string json = JsonSerializer.Serialize(new { Source = Item(ProfileSourceVolume), Runtime = Item(ProfileRuntimeVolume) });
                return Ok("VT-PROFILE-STORAGE " + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)) + "\n");
            }
            case "machine-name": return Ok("VT-MACHINE " + MachineName + "\n");
            case "lock":
            {
                string owner = v["owner"];
                string? current;
                lock (_sync) current = HeldBy ?? Claims.FirstOrDefault(claim => !Releases.Contains(claim));
                if (v["action"] == "claim")
                {
                    if (current == null) { lock (_sync) Claims.Add(owner); return Ok("VT-LOCK claimed\n"); }
                    return Ok(current == owner ? "VT-LOCK yours\n" : "VT-LOCK held\n" + current + "\n");
                }
                if (current == null) return Ok("VT-LOCK free\n");
                if (current != owner) return Ok("VT-LOCK held\n" + current + "\n");
                if (v["action"] == "check") return Ok("VT-LOCK yours\n");
                lock (_sync) Releases.Add(owner);
                AfterRelease?.Invoke(owner);
                return Ok("VT-LOCK released\n");
            }
            case "scheduled-task-register":
            {
                if (ScheduledTasks.ContainsKey(v["task"])) return new HostResult(HostOutcome.Exited, 1, "", "task exists", TimeSpan.Zero, false);
                ScheduledTasks.Add(v["task"], (new WindowsScheduledTask(v["task"], v["description"], v["executable"], v["launcher"], v["working"], v["principalUserId"]), WindowsScheduledTask.State.Running));
                return Ok("VT-TASK registered\n");
            }
            case "scheduled-task-inspect":
            {
                if (!ScheduledTasks.TryGetValue(v["task"], out var scheduled)) return Ok("VT-TASK missing\n");
                var expected = new WindowsScheduledTask(v["task"], v["description"], v["executable"], v["launcher"], v["working"], v["principalUserId"]);
                if (scheduled.Task != expected) return Ok("VT-TASK mismatch\n");
                string state = scheduled.State switch
                {
                    WindowsScheduledTask.State.Running => "running", WindowsScheduledTask.State.NotStarted => "not-started", _ => "stopped",
                };
                return Ok("VT-TASK owned " + state + "\n");
            }
            case "scheduled-task-retire":
            {
                if (!ScheduledTasks.TryGetValue(v["task"], out var scheduled)) return Ok("VT-TASK missing\n");
                var expected = new WindowsScheduledTask(v["task"], v["description"], v["executable"], v["launcher"], v["working"], v["principalUserId"]);
                if (scheduled.Task != expected) return Ok("VT-TASK mismatch\n");
                ScheduledTasks.Remove(v["task"]);
                return Ok("VT-TASK removed\n");
            }
            // As the scripts: the first path that overlaps the root once links are resolved (here in the mirror, by the local rule).
            case "apart":
                foreach (string path in v["paths"].Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    if (ProtectedPaths.Contains(Local(v["root"]), Local(path)) || ProtectedPaths.Contains(Local(path), Local(v["root"])))
                        return Ok("VT-APART overlap " + Convert.ToBase64String(Encoding.UTF8.GetBytes(path)) + "\n");
                return Ok("VT-APART ok\n");
            case "mac-agent-retire": return Ok(MacAgentReply);
            case "mac-desktop": return Ok(MacDesktopReply);
            case "mac-game": return Ok("VT-MAC-GAME " + MacSlices + "\n");
            case "mac-loader": return Ok("VT-MAC-DOORSTOPS libdoorstop.dylib|" + MacSlices + ";\n");
            case "mac-bundle": return Ok(MacBundleInspect + "\n");
            case "steam-runtime-verify":
            {
                string source = Local(v["source"]);
                string manifest = Path.Combine(source, "package", "steam_cmd_osx.manifest");
                if (!Directory.Exists(source)) return new HostResult(HostOutcome.Exited, 4, "VT-STEAM-RUNTIME source\n", "", TimeSpan.Zero, false);
                if (!File.Exists(manifest)) return new HostResult(HostOutcome.Exited, 4, "VT-STEAM-RUNTIME manifest\n", "", TimeSpan.Zero, false);
                if (!FileHash.Sha256(manifest).Equals(v["manifestSha"], StringComparison.OrdinalIgnoreCase))
                    return new HostResult(HostOutcome.Exited, 4, "VT-STEAM-RUNTIME manifest-changed\n", "", TimeSpan.Zero, false);
                var build = Regex.Match(File.ReadAllText(manifest), "\"version\"\\s+\"(?<version>[0-9]+)\"", RegexOptions.CultureInvariant);
                if (!build.Success || build.Groups["version"].Value != v["expectedVersion"])
                    return new HostResult(HostOutcome.Exited, 4, "VT-STEAM-RUNTIME version-changed\n", "", TimeSpan.Zero, false);
                var reply = new StringBuilder();
                foreach (string line in v["files"].Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] pair = line.Split(' ', 2);
                    string relative = Encoding.UTF8.GetString(Convert.FromBase64String(pair[1]));
                    string file = Path.Combine(source, relative);
                    if (!File.Exists(file)) return new HostResult(HostOutcome.Exited, 4, $"VT-STEAM-RUNTIME missing {relative}\n", "", TimeSpan.Zero, false);
                    if (!FileHash.Sha256(file).Equals(pair[0], StringComparison.OrdinalIgnoreCase))
                        return new HostResult(HostOutcome.Exited, 4, $"VT-STEAM-RUNTIME changed {relative}\n", "", TimeSpan.Zero, false);
                    string listing = relative + ":\n";
                    reply.Append("VT-STEAM-OTOOL ").Append(pair[1]).Append(' ')
                        .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(listing))).Append('\n');
                }
                return Ok(reply.Append("VT-STEAM-VERIFIED\n").ToString());
            }
            case "steam-runtime-stage":
            {
                string source = Local(v["source"]), target = Path.Combine(Local(v["runtime"]), "steam-runtime");
                if (!Directory.Exists(Local(v["runtime"])) || Directory.Exists(target))
                    return new HostResult(HostOutcome.Exited, 4, "VT-STEAM-RUNTIME exists\n", "", TimeSpan.Zero, false);
                Directory.CreateDirectory(target);
                foreach (string line in v["files"].Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    string relative = Encoding.UTF8.GetString(Convert.FromBase64String(line.Split(' ', 2)[1]));
                    string output = Path.Combine(target, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    File.Copy(Path.Combine(source, relative), output);
                }
                return Ok("VT-STEAM-STAGED\n");
            }
            case "game-process": return Ok(GameActive ? $"VT-GAME busy {GameProcessIds}\n".Replace(" \n", "\n") : "VT-GAME idle\n");
            case "server-logon": return Ok("VT-LOGON " + ServerTaskLogon + "\n");
            case "preflight-exists":
                return Ok(string.Concat(v["paths"].Split('\n').Where(relative => relative.Length != 0 && File.Exists(Local(HostPath.Join(v["root"], relative))))
                    .Select(relative => "VT-EXISTS file " + relative + "\n")) + "VT-EXISTS done\n");
            // As HostClientPreflight's scripts (SourceScriptShellTests runs them): missing (not a file), then unreadable by the host
            // user (or, on Windows, held open by another process: locked), then over the limit, then the bytes.
            case "preflight-read":
            {
                string file = Local(v["path"]);
                // A file, through any link (a dangling link is missing, as -f and File.Exists on the target say).
                if (!File.Exists(file) || new FileInfo(file).LinkTarget != null && new FileInfo(file).ResolveLinkTarget(returnFinalTarget: true) is not { Exists: true })
                    return Ok("VT-PREFLIGHT missing\n");
                if (UnreadableFiles.Contains(v["path"])) return Ok("VT-PREFLIGHT unreadable\n");
                try
                {
                    using var stream = File.OpenRead(file);
                    if (stream.Length > long.Parse(v["limit"], System.Globalization.CultureInfo.InvariantCulture)) return Ok("VT-PREFLIGHT too-large\n");
                    var bytes = new byte[stream.Length];
                    stream.ReadExactly(bytes);
                    return Ok("VT-PREFLIGHT " + Convert.ToBase64String(bytes) + "\n");
                }
                catch (UnauthorizedAccessException) { return Ok("VT-PREFLIGHT unreadable\n"); }
                catch (IOException error) when (Windows && (error.HResult & 0xFFFF) is 32 or 33) { return Ok("VT-PREFLIGHT locked\n"); }
                catch (IOException error) { return new HostResult(HostOutcome.Exited, Windows ? 1 : 3, "", error.Message, TimeSpan.Zero, false); }
            }
            // As SourceSettleScripts' pair does (SourceScriptShellTests runs both on the same folders and compares the replies).
            case "source-settle": return Ok(Settle(v));
            case "source-reachable":
                foreach (string path in new[] { v["root"], v["parent"] })
                    if (!Directory.Exists(Local(path))) return Ok("VT-REACHABLE no " + path + "\n");
                return Ok("VT-REACHABLE yes\n");
            case "source-write":
                try
                {
                    if (v.GetValueOrDefault("root") is { Length: > 0 } boundary)
                    {
                        ProtectedPaths.RequireUnlinkedPath(Local(boundary), Local(v["path"]));
                        ProtectedPaths.RequireUnlinkedPath(Local(boundary), Local(v["path"]) + ".vt-tmp");
                    }
                    if (v.GetValueOrDefault("exclusive") == "1" && Path.Exists(Local(v["path"])))
                        return new HostResult(HostOutcome.Exited, 3, "", "report already exists", TimeSpan.Zero, false);
                    Directory.CreateDirectory(Path.GetDirectoryName(Local(v["path"]))!);
                    bool exclusive = v.GetValueOrDefault("exclusive") == "1";
                    string temporary = Local(v["path"]) + ".vt-tmp" + (exclusive ? "." + Guid.NewGuid().ToString("N") : "");
                    try
                    {
                        File.WriteAllBytes(temporary, Convert.FromBase64String(v["content"]));
                        File.Move(temporary, Local(v["path"]), overwrite: !exclusive);
                    }
                    finally { if (exclusive && File.Exists(temporary)) File.Delete(temporary); }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { return new HostResult(HostOutcome.Exited, Windows ? 1 : 3, "", error.Message, TimeSpan.Zero, false); }
                return Ok("VT-SOURCE-WRITTEN\n");
            case "source-evidence": return Evidence(v);
            case "profile-seed":
            {
                string destination = Local(v["runtime"]);
                if (Directory.Exists(destination)) return Ok("VT-PROFILE-EXISTS\n");
                Directory.CreateDirectory(destination);
                foreach (string encoded in v["gameFiles"].Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Concat(v["files"].Split('\n', StringSplitOptions.RemoveEmptyEntries)))
                {
                    string relative = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                    // As the seed: a game file goes to the game folder (a Mac server's lies below the profile), a loader file to the profile.
                    bool gameFile = v["gameFiles"].Split('\n').Contains(encoded);
                    string target = Path.Combine(gameFile && v.TryGetValue("game", out string? game) ? Local(game) : destination, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(Path.Combine(Local(v["source"]), relative), target);
                }
                AfterSeed?.Invoke(destination);
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
                    string[] selected = line.Split(' ', 2);
                    if (selected.Length != 2 || !Enum.TryParse(selected[1], out HostedRuntimeStage.PayloadTargetKind kind) || !Enum.IsDefined(kind))
                        throw new InvalidDataException("Malformed selected payload entry: " + line);
                    string relative = Encoding.UTF8.GetString(Convert.FromBase64String(selected[0]));
                    if (!HostedRuntimeStage.PayloadTargetAllowed(relative, kind))
                        throw new InvalidDataException("Refusing a target outside the selected payload kinds: " + relative);
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
            // copy was made (since, Unix seconds); older ones are named.
            case "keep-logs":
            {
                var reply = new StringBuilder();
                var copied = DateTimeOffset.FromUnixTimeSeconds(long.Parse(v["since"], System.Globalization.CultureInfo.InvariantCulture)).UtcDateTime;
                void Save(string from, string name)
                {
                    if (!File.Exists(from)) return;
                    if (File.GetLastWriteTimeUtc(from) < copied) { reply.Append("VT-LOG-OLDER " + name + "\n"); return; }
                    string to = Path.Combine(Local(v["keep"]), name);
                    ProtectedPaths.RequireUnlinkedPath(Path.GetDirectoryName(Local(v["keep"]))!, to);
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!); File.Copy(from, to, overwrite: true);
                    reply.Append("VT-LOG-KEPT " + name + "\n");
                }
                string runtime = Local(v["runtime"]);
                if (Directory.Exists(runtime))
                {
                    Save(Path.Combine(runtime, "BepInEx", "LogOutput.log"), "BepInEx/LogOutput.log");
                    Save(Path.Combine(runtime, v["unitylog"]), v["unitylog"]);
                    foreach (string file in Directory.GetFiles(runtime, "preloader_*.log")) Save(file, Path.GetFileName(file));
                }
                return Ok(reply + "VT-LOGS-DONE\n");
            }
            case "cleanup-stage":
                foreach (string path in new[] { v["runtime"], v["stage"] }.Where(path => path.Length != 0))
                    if (Directory.Exists(Local(path))) Directory.Delete(Local(path), recursive: true);
                if (v["runtime"].Split('/', '\\').Last() == "runtime" && File.Exists(Path.Combine(Local(v["parent"]), ProfileSourceCheck.BaselineFile)))
                    File.Delete(Path.Combine(Local(v["parent"]), ProfileSourceCheck.BaselineFile));
                if (v.GetValueOrDefault("keepparent", "").Length == 0 && v.TryGetValue("parent", out string? parent) && Directory.Exists(Local(parent)) &&
                    !Directory.EnumerateFileSystemEntries(Local(parent)).Any())
                    Directory.Delete(Local(parent));
                return Ok("VT-STAGE-CLEANED\n");
            // As the real scripts do (HostedCharacterStageShellTests runs them): the rule arrives as variables.
            case "character-folders":
            {
                // The scripts' search, on this mirror (CharacterDirectoryTests runs the real scripts): a Windows host's user is
                // C:\Users\tester with Steam registered in Program Files (x86), a macOS host's /Users/tester.
                string home = v["userhome"].Length != 0 ? v["userhome"] : Windows ? @"C:\Users\tester" : "/Users/tester";
                string Join(params string[] parts) => Windows ? string.Join('\\', parts) : string.Join('/', parts);
                string characters = v["characters"].Length != 0 ? v["characters"] : Windows ? Join(home, "AppData", "LocalLow", "IronGate", "Valheim", "characters_local")
                    : home + "/Library/Application Support/IronGate/Valheim/characters_local";
                string[] userdata = v["userdata"].Length != 0 ? [v["userdata"]] : v["steam"].Length != 0 ? [Join(v["steam"], "userdata")]
                    : Windows ? [@"C:\Program Files (x86)\Steam\userdata"] : ["/Users/tester/Library/Application Support/Steam/userdata"];
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
                if (JournalReadAllReply != null) return Ok(JournalReadAllReply);
                string directory = Local(v["journal"]);
                var text = new StringBuilder();
                if (Directory.Exists(directory))
                    foreach (string file in Directory.GetFiles(directory, "*.jsonl", SearchOption.AllDirectories))
                    {
                        string hostFile = v["journal"].TrimEnd('/', '\\') + "/" + Path.GetRelativePath(directory, file).Replace('\\', '/');
                        text.Append("VT-JOURNAL-FILE ").Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(hostFile)))
                            .Append(' ').Append(Convert.ToBase64String(File.ReadAllBytes(file))).Append('\n');
                    }
                return Ok(text.Append("VT-JOURNAL-END\n").ToString());
            }
            case "launch-gone":
            {
                bool running;
                lock (_sync) running = _processes.Values.Any(process => !process.Exit(CancellationToken.None).IsCompleted);
                return Ok(running || GameActive ? "VT-LAUNCH-GONE busy 4242\n" : "VT-LAUNCH-GONE idle\n");
            }
            // As RunJournalStatus's pid-file scripts do: each launch directory's pid file, in order.
            case "pid-file":
            {
                var text = new StringBuilder();
                var dirs = v["dirs"].Split('\n');
                var owners = v.GetValueOrDefault("owners", "").Split('\n');
                for (int i = 0; i < dirs.Length; i++)
                {
                    string file = Path.Combine(Local(dirs[i]), "pid");
                    text.Append(File.Exists(file) ? $"VT-PIDFILE {i} file {Convert.ToBase64String(File.ReadAllBytes(file))}\n" : $"VT-PIDFILE {i} missing\n");
                    string owner = i < owners.Length ? owners[i] : "";
                    string ownership = owner.Length == 0 || owner == "-" ? "" : Path.Combine(Local(dirs[i]), owner);
                    text.Append(ownership.Length != 0 && File.Exists(ownership)
                        ? $"VT-OWNERFILE {i} file {Convert.ToBase64String(File.ReadAllBytes(ownership))}\n"
                        : $"VT-OWNERFILE {i} missing\n");
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
            case "launcher-inspect":
            case "launcher-retire":
            {
                int pid = int.Parse(v["launcherPid"], System.Globalization.CultureInfo.InvariantCulture);
                (string Start, Func<CancellationToken, Task<int>> Exit, Action Stop) process;
                bool known; lock (_sync) known = _processes.TryGetValue(pid, out process);
                if (!known || process.Exit(CancellationToken.None).IsCompleted) return Ok("VT-LAUNCHER gone\n");
                if (process.Start != v["launcherStart"]) return Ok("VT-LAUNCHER reused\n");
                if (name == "launcher-retire") { process.Stop(); return Ok("VT-LAUNCHER stopped\n"); }
                return Ok("VT-LAUNCHER owned\n");
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
                bool nocase = Windows || v.GetValueOrDefault("nocase") == "1";
                // As the scripts: the walk never enters a link, and a link the listing reaches (anywhere when it lists everything, else
                // a named path, one inside it or a linked folder on the way to it) is refused before anything is hashed.
                var (files, folders, links) = Walk(root);
                var refused = links.Where(link => patterns.Length == 0 || patterns.Any(pattern => LinkReached(root, link, pattern, nocase))).Take(5).ToList();
                if (refused.Count != 0) return Ok("VT-LIST links\n" + string.Concat(refused.Select(link => "./" + link + "\n")));
                foreach (string relative in files)
                {
                    string file = Path.Combine(root, relative);
                    if (patterns.Length != 0 && !patterns.Any(pattern => MatchesListedPath(relative, pattern, nocase))) continue;
                    if (v.GetValueOrDefault("metadata") == "1")
                    {
                        var info = new FileInfo(file);
                        text.Append("VT-STATE ").Append(info.Length).Append(' ').Append(info.LastWriteTimeUtc.Ticks).Append(' ')
                            .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(relative))).Append('\n');
                    }
                    if (v.GetValueOrDefault("content") != "0")
                        text.Append(FileHash.Sha256(file)).Append("  ./").Append(relative).Append('\n');
                }
                if (v.GetValueOrDefault("metadata") == "1")
                    foreach (string folder in folders)
                        text.Append("VT-DIR ").Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(folder))).Append('\n');
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
                File.WriteAllText(Path.Combine(boot, "stderr.log"), "");
                // Profile launches keep loader output under the owned profile, not the game's install.
                _runtime = v["logroot"];
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
                {
                    // As BepInEx does: its log goes to the BepInEx folder Doorstop loads, the owned profile's when the launch names
                    // one (a Unix profile runs the game from its install with the profile's preloader), else the install's.
                    const string preloader = "/" + BepInExLoader.CorePreloader;
                    string? target = Spec(v["spec"]).Where(line => line.Kind == "env" && line.Text.StartsWith("DOORSTOP_TARGET_ASSEMBLY=", StringComparison.Ordinal))
                        .Select(line => line.Text["DOORSTOP_TARGET_ASSEMBLY=".Length..]).SingleOrDefault();
                    string loaderRoot = target != null && target.StartsWith('/') && target.EndsWith(preloader, StringComparison.Ordinal)
                        ? target[..^preloader.Length] : v["install"];
                    string log = Path.Combine(Local(loaderRoot), "BepInEx", "LogOutput.log");
                    Directory.CreateDirectory(Path.GetDirectoryName(log)!);
                    File.WriteAllText(log, "[Info   :valheimCLI] Command server listening on 127.0.0.1:5578\n");
                }
                // As Unity does: its log goes to the file the launch's -logFile names (#822).
                var arguments = Spec(v["spec"]).Where(line => line.Kind == "arg").Select(line => line.Text).ToList();
                int logFile = arguments.IndexOf("-logFile");
                if (logFile >= 0 && logFile + 1 < arguments.Count)
                {
                    string unity = Local(arguments[logFile + 1]);
                    Directory.CreateDirectory(Path.GetDirectoryName(unity)!);
                    File.WriteAllText(unity, $"Unity player log of client {id}\n");
                }
                // As the game does whatever the launch asks (#822): it adds the server it joins to the host user's recent-servers list.
                string recent = Local(HostPath.Join(Home, Windows ? HostedWorld.WindowsSaveRelative : HostedWorld.MacSaveRelative, "serverlist_local", "recent"));
                Directory.CreateDirectory(Path.GetDirectoryName(recent)!);
                File.WriteAllText(recent, $"127.0.0.1:2456 joined by client {id}\n");
                lock (_sync) _processes[id] = ("555", ct => exit.Task.WaitAsync(ct), () => exit.TrySetResult(137));
                // A Windows start runs through a scheduled task, which it removes before it replies.
                return Ok($"VT-INTERACTIVE started {id} 555\n" + (Windows ? "VT-TASK removed\n" : ""));
            }
            case "wait":
            {
                (string Start, Func<CancellationToken, Task<int>> Exit, Action Stop) process;
                lock (_sync) process = _processes[int.Parse(v["game"])];
                if (process.Start != v["start"]) return Ok("VT-WAIT exited ?\n");
                return Ok($"VT-WAIT exited {await process.Exit(cancellation)}\n");
            }
            case "stop":
            case "mac-client-stop":
            {
                // A Mac client's stop also reports its run-owned GUI agent removed.
                string agent = name == "mac-client-stop" ? MacAgentReply : "";
                lock (_sync) Stops.Add((v["game"], v["start"]));
                (string Start, Func<CancellationToken, Task<int>> Exit, Action Stop) process;
                lock (_sync) if (!_processes.TryGetValue(int.Parse(v["game"]), out process) || process.Start != v["start"]) return Ok(agent + "VT-STOP gone\n");
                process.Stop();
                if (v.TryGetValue("quit", out string? quit) && quit != "0" && !IgnoreQuit)
                {
                    // The game quits by itself. A server's shutdown retires its boot's lobby, as 1.0.16 logs it; a client host has no server runtime.
                    if (_runtime == null) return Ok(agent + "VT-STOP quit\n");
                    string log = Path.Combine(Local(_runtime), "BepInEx", "LogOutput.log");
                    string text = File.Exists(log) ? File.ReadAllText(log) : "";
                    File.AppendAllText(log, "[Info   : Unity Log] Unregister PlayFab server \"MyModTest\" and leaving network \"n\"\n" +
                        string.Concat(CrossplayServer.LobbyCreated.Matches(text).Select(m => $"[Info   : Unity Log] Deactivated PlayFab lobby {m.Groups["lobby"].Value}\n")));
                    return Ok(agent + "VT-STOP quit\n");
                }
                return Ok(agent + "VT-STOP stopped\n");
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
            case "world-reserve":
            {
                string keep = Local(v["keep"]);
                if (v.GetValueOrDefault("root") is { Length: > 0 } root)
                    try { ProtectedPaths.RequireUnlinkedPath(Local(root), keep); }
                    catch (IOException) { return new HostResult(HostOutcome.Exited, 4, "", "linked destination", TimeSpan.Zero, false); }
                if (Path.Exists(keep) || v["stage"].Length != 0 && Path.Exists(Local(v["stage"]))) return new HostResult(HostOutcome.Exited, 4, "", "", TimeSpan.Zero, false);
                Directory.CreateDirectory(keep);
                return Ok("VT-WORLD-RESERVED done\n");
            }
            case "world-entries":
            case "world-move":
            {
                // Entries named for the world, as the host scripts select them for the refusal and the move-out alike: the one rule
                // (HostedWorld.IsNamedFor: <name>, <name>.*, <name>_backup*, any case); a move never goes over an existing destination.
                string worlds = Local(v["worlds"]), world = v["name"];
                if (name == "world-entries") Directory.CreateDirectory(worlds);
                bool Named(string leaf) => HostedWorld.IsNamedFor(leaf, world);
                var entries = Directory.Exists(worlds) ? Directory.EnumerateFileSystemEntries(worlds).Where(entry => Named(Path.GetFileName(entry))).ToList() : [];
                if (name == "world-entries") return Ok(string.Concat(entries.Select(entry => "VT-WORLD-ENTRY " + Path.GetFileName(entry) + "\n")) + "VT-WORLD-ENTRIES done\n");
                string to = Local(v["to"]);
                Directory.CreateDirectory(to);
                if ((script == HostedWorldOnHost.WindowsPlace || script == HostedWorldOnHost.BashPlace) &&
                    Directory.EnumerateFileSystemEntries(to).Any(entry => Named(Path.GetFileName(entry))))
                    return new HostResult(HostOutcome.Exited, 4, "VT-WORLD-EXISTS foreign\n", "", TimeSpan.Zero, false);
                if (v.GetValueOrDefault("selected") is { Length: > 0 } selected)
                    entries = entries.Where(entry => selected.Split('\n').Contains(Path.GetFileName(entry), StringComparer.Ordinal)).ToList();
                if (entries.FirstOrDefault(entry => Path.Exists(Path.Combine(to, Path.GetFileName(entry)))) is { } clash)
                    return new HostResult(HostOutcome.Exited, 4, "VT-WORLD-EXISTS " + Path.GetFileName(clash) + "\n", "", TimeSpan.FromMilliseconds(3), false);
                foreach (string entry in entries)
                    if (Directory.Exists(entry)) Directory.Move(entry, Path.Combine(to, Path.GetFileName(entry))); else File.Move(entry, Path.Combine(to, Path.GetFileName(entry)));
                return Ok(string.Concat(entries.Select(entry => "VT-WORLD-MOVED " + Path.GetFileName(entry) + "\n")) + "VT-WORLD-MOVE done\n");
            }
            case "world-meta":
            {
                // As the host scripts: whether an entry MoveOut would move is there, and the metadata of every such entry, base64:
                // the _main.<n>.fwl2 files of an <entry>/ folder; an entry that is not a folder is named.
                string worlds = Local(v["worlds"]), world = v["name"];
                var named = Directory.Exists(worlds) ? Directory.EnumerateFileSystemEntries(worlds).Where(entry => HostedWorld.IsNamedFor(Path.GetFileName(entry), world))
                    .Order(StringComparer.Ordinal).ToList() : [];
                var reply = new StringBuilder();
                foreach (string entry in named)
                {
                    string leaf = Path.GetFileName(entry);
                    if (Directory.Exists(entry))
                        foreach (string file in Directory.EnumerateFiles(entry).Where(file => Regex.IsMatch(Path.GetFileName(file), @"^_main\.[0-9]+\.fwl2$")).Order(StringComparer.Ordinal))
                            reply.Append("VT-WORLD-META " + leaf + "/" + Path.GetFileName(file) + " " + Convert.ToBase64String(File.ReadAllBytes(file)) + "\n");
                    else reply.Append("VT-WORLD-FILE " + leaf + "\n");
                }
                return Ok(reply + "VT-WORLD-METAS " + (named.Count != 0 ? "present" : "absent") + "\n");
            }
            case "client-keep":
            {
                string dir = Local(v["dir"]);
                string source = Path.Combine(Local(v["install"]), "BepInEx", "LogOutput.log");
                if (File.Exists(source)) File.Copy(source, Path.Combine(dir, "game-0.log"));
                else File.WriteAllText(Path.Combine(dir, "game-0.log.absent"), "absent");
                string unity = Local(v["unitylog"]); // As the real script: the launch's -logFile (#822).
                if (File.Exists(unity)) File.Copy(unity, Path.Combine(dir, "game-1.log"));
                else File.WriteAllText(Path.Combine(dir, "game-1.log.absent"), "absent");
                return Ok("VT-KEPT\n");
            }
            // The host user's home, as the real scripts report it; their Valheim data folder is under it in the mirror.
            case "mac-home": return Ok("VT-MAC-HOME " + Home + "\n");
            case "windows-profile": return Ok("VT-WINDOWS-PROFILE " + Home + "\n");
            // Map only transport paths. UserSaveFiles remains the policy owner here too.
            case "user-files":
                try { return Ok(UserSaveFiles.RunLocalStep(v, Local)); }
                catch (IOException error) { return new HostResult(HostOutcome.Exited, 4, "", error.Message, TimeSpan.Zero, false); }
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

    // Every file, folder and link under root, relative with / separators, as the listing scripts walk it: a link is never entered.
    private static (List<string> Files, List<string> Folders, List<string> Links) Walk(string root)
    {
        var files = new List<string>(); var folders = new List<string>(); var links = new List<string>();
        var pending = new Stack<string>([root]);
        while (pending.Count > 0)
            foreach (string entry in Directory.EnumerateFileSystemEntries(pending.Pop()).Order(StringComparer.Ordinal))
            {
                string relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                if (IsLink(entry)) links.Add(relative);
                else if (Directory.Exists(entry)) { folders.Add(relative); pending.Push(entry); }
                else files.Add(relative);
            }
        return (files, folders, links);
    }

    // Whether a listing of pattern reaches the link at relative: the link is the named path or lies inside it, or it is a linked
    // folder on the way to it (bash records one before the last segment; PowerShell refuses one whose target opens).
    private static bool LinkReached(string root, string relative, string pattern, bool ignoreCase)
    {
        string[] parts = relative.Split('/'), segments = pattern.Replace('\\', '/').Split('/');
        int shared = Math.Min(parts.Length, segments.Length);
        for (int i = 0; i < shared; i++)
            if (!FileSystemName.MatchesSimpleExpression(segments[i], parts[i], ignoreCase)) return false;
        return parts.Length >= segments.Length || Directory.Exists(Path.Combine(root, relative));
    }

    // A symbolic link or junction (any reparse point), dangling or not, as the scripts' -L and ReparsePoint checks see it.
    private static bool IsLink(string path)
    {
        var info = new FileInfo(path);
        return info.LinkTarget != null || ((info.Exists || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0);
    }

    // A symbolic link or junction (a name link), as bash's -L and PowerShell's Test-VtLink see it: another reparse point is not one.
    private static bool NameLink(string path) => new FileInfo(path).LinkTarget != null;

    // Every folder below path made writable and enterable for this user, and every read-only attribute cleared; links never entered.
    private static void Writable(string path)
    {
        if (OperatingSystem.IsWindows()) File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        else File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (string entry in Directory.EnumerateFileSystemEntries(path))
            if (NameLink(entry)) continue;
            else if (Directory.Exists(entry)) Writable(entry);
            else if (OperatingSystem.IsWindows()) File.SetAttributes(entry, File.GetAttributes(entry) & ~FileAttributes.ReadOnly);
    }

    // The path as a Windows host script opens it (\\?\ form): never trimmed of a trailing dot or space. Elsewhere unchanged.
    private static string Exact(string path) => !OperatingSystem.IsWindows() ? path
        : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    // A settlement line's relative path, by segment, or null when it is outside (SourceSettleScripts' vt_relative and Get-VtRelative):
    // not base64, empty, rooted, or holding an empty, "." or ".." segment (a Windows host splits on \ too).
    private string[]? SettleRelative(string line)
    {
        string relative;
        try { relative = Encoding.UTF8.GetString(Convert.FromBase64String(line)); } catch (FormatException) { return null; }
        string[] parts = Windows ? relative.Split('/', '\\') : relative.Split('/');
        if (relative.Length == 0 || parts.Any(part => part is "" or "." or "..") || Windows && relative.Length > 1 && relative[1] == ':') return null;
        return parts;
    }

    // Whether no link lies on the way from root to root/parts, root included (ProtectedPaths.BashUnlinked, PowerShellUnlinked).
    private static bool Unlinked(string root, string[] parts)
    {
        string current = root;
        if (IsLink(current)) return false;
        foreach (string part in parts)
        {
            current = Path.Combine(current, part);
            if (IsLink(Exact(current))) return false;
        }
        return true;
    }

    // SourceSettleScripts.Bash and .PowerShell: refused as a whole (no root or keep, a root that is not a folder, a keep folder that is
    // not outside the root once links are resolved), else each file moved or named with its reason, then each folder removed once empty.
    private string Settle(IReadOnlyDictionary<string, string> v)
    {
        if (v["root"].Length == 0 || v["keep"].Length == 0) return "VT-SETTLE-REFUSED no source install or keep folder was named\n";
        // Full paths with this system's separator, as PowerShell's GetFullPath makes them (the \\?\ form takes no /).
        string Full(string path) => Path.TrimEndingDirectorySeparator(OperatingSystem.IsWindows() ? Path.GetFullPath(path) : path);
        string root = Full(Local(v["root"])), keep = Full(Local(v["keep"]));
        if (!Directory.Exists(root) || IsLink(root)) return "VT-SETTLE-REFUSED the source install is not a folder\n";
        bool inside;
        try { inside = ProtectedPaths.Contains(root, keep); } catch (IOException) { inside = true; }
        if (inside) return "VT-SETTLE-REFUSED the keep folder is not outside the source install\n";
        var reply = new StringBuilder();
        void Line(string kind, string line, string? reason = null) => reply.Append(kind).Append(' ').Append(line).Append(reason == null ? "" : " " + reason).Append('\n');
        foreach (string line in v["files"].Split('\n'))
        {
            if (line.Length == 0) continue;
            if (SettleRelative(line) is not { } parts) { Line("VT-NOTMOVED", line, "outside"); continue; }
            string source = Exact(Path.Combine([root, .. parts])), target = Path.Combine([keep, .. parts]);
            // As PowerShell does before Get-VtExact: compare ordinary paths, then use the extended spelling only for I/O.
            // Comparing C:\... with \\?\C:\... would falsely classify an owned target as outside its boundary on Windows.
            if (v.GetValueOrDefault("keepRoot") is { Length: > 0 } boundary)
                try { ProtectedPaths.RequireUnlinkedPath(Local(boundary), target); }
                catch (IOException) { Line("VT-NOTMOVED", line, "link"); continue; }
            target = Exact(target);
            if (!Unlinked(root, parts)) { Line("VT-NOTMOVED", line, "link"); continue; }
            if (!File.Exists(source) && !Directory.Exists(source)) { Line("VT-GONE", line); continue; }
            if (!File.Exists(source)) { Line("VT-NOTMOVED", line, "not-a-file"); continue; }
            if (File.Exists(target) || Directory.Exists(target) || IsLink(target)) { Line("VT-NOTMOVED", line, "kept-already"); continue; }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(source, target);
                Line("VT-MOVED", line);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Line("VT-NOTMOVED", line, "move-failed"); }
        }
        foreach (string line in v["folders"].Split('\n'))
        {
            if (line.Length == 0) continue;
            if (SettleRelative(line) is not { } parts) { Line("VT-NOTMOVED", line, "outside"); continue; }
            string folder = Exact(Path.Combine([root, .. parts]));
            if (!Unlinked(root, parts)) { Line("VT-NOTMOVED", line, "link"); continue; }
            if (!Directory.Exists(folder)) { Line("VT-GONE", line); continue; }
            bool removed = false;
            try { if (!Directory.EnumerateFileSystemEntries(folder).Any()) { Directory.Delete(folder); removed = true; } }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            if (removed) Line("VT-RMDIR", line); else Line("VT-NOTMOVED", line, "not-empty");
        }
        return reply.Append("VT-SETTLED\n").ToString();
    }

    // SourceSettleScripts.BashEvidence and .PowerShellEvidence: the refusals that cannot change, then the moved-files folder adopted
    // into a recovery's folder (or removed once fetched), then the baseline, every delete strictly below the profile's folder.
    private HostResult Evidence(IReadOnlyDictionary<string, string> v)
    {
        string parent = v["parent"], keep = v["keep"], recovered = v.GetValueOrDefault("recovered", "");
        HostResult Refused(string why) => Ok("VT-SOURCE-REFUSED " + why + "\n");
        HostResult Failed(string why) => new(HostOutcome.Exited, Windows ? 1 : 3, "", why, TimeSpan.Zero, false);
        if (parent.Length == 0 || keep.Length == 0) return Refused("no profile folder or moved files folder was named");
        if (Windows)
        {
            // Both sides as full paths with one separator, compared ignoring case; the folder's name as -like 'source-added*'.
            string Full(string path) => (OperatingSystem.IsWindows() ? Path.GetFullPath(path) : path).Replace('/', '\\').TrimEnd('\\');
            string full = Full(keep);
            if (!string.Equals(Full(full[..Math.Max(full.LastIndexOf('\\'), 0)]), Full(parent), StringComparison.OrdinalIgnoreCase))
                return Refused(keep + " is not directly in " + parent);
            if (!full[(full.LastIndexOf('\\') + 1)..].StartsWith(ProfileSourceCheck.AddedFolder, StringComparison.OrdinalIgnoreCase))
                return Refused(keep + " is not a moved files folder");
        }
        else
        {
            // As dirname and basename: trailing slashes go first; the parent is compared as written.
            string trimmed = keep.TrimEnd('/');
            int slash = trimmed.LastIndexOf('/');
            string folder = slash < 0 ? "." : trimmed[..slash].TrimEnd('/') is { Length: > 0 } up ? up : "/";
            if (folder != parent) return Refused(keep + " is not directly in " + parent);
            string leaf = trimmed[(slash + 1)..];
            if (leaf != ProfileSourceCheck.AddedFolder && !leaf.StartsWith(ProfileSourceCheck.AddedFolder + "-", StringComparison.Ordinal))
                return Refused(keep + " is not a moved files folder");
        }
        string kept = Local(keep), root = Local(parent);
        if (recovered.Length != 0)
        {
            if (v.GetValueOrDefault("recoveredRoot") is { Length: > 0 } boundary)
                try { ProtectedPaths.RequireUnlinkedPath(Local(boundary), Path.Combine(Local(recovered), "added")); }
                catch (IOException) { return Refused("the recovered destination passes through a link"); }
            if (NameLink(kept)) return Refused("the moved files folder is a link");
            if (Directory.Exists(kept))
            {
                string to = Windows ? recovered.TrimEnd('\\', '/') + "\\added" : recovered + "/added";
                if (File.Exists(Local(to)) || Directory.Exists(Local(to)) || IsLink(Local(to))) return Refused(to + " already exists");
                try
                {
                    Directory.CreateDirectory(Local(recovered));
                    // bash's mv copies a folder to another volume; .NET's (and PowerShell's) Move refuses that.
                    try { Directory.Move(kept, Local(to)); }
                    catch (IOException) when (!Windows && Directory.Exists(kept) && !Path.Exists(Local(to))) { CopyDirectory(kept, Local(to)); Directory.Delete(kept, recursive: true); }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return Failed(error.Message); }
            }
        }
        else if (v.GetValueOrDefault("added") == "1" && RemoveBelow(kept, root) is { } notRemoved) return Failed(notRemoved);
        if (v.GetValueOrDefault("baseline") == "1" && RemoveBelow(Path.Combine(root, ProfileSourceCheck.BaselineFile), root) is { } baseline) return Failed(baseline);
        return Ok("VT-SOURCE-SETTLED\n");
    }

    // ProtectedPaths.BashRemove and PowerShellRemove: a link is unlinked (never entered) when it stands in root; anything else is
    // deleted only when it lies strictly below root once links are resolved; a path that is not there is gone. Null when done, else why not.
    private string? RemoveBelow(string path, string root)
    {
        var comparison = Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        path = Path.TrimEndingDirectorySeparator(path);
        try
        {
            string @base = Path.TrimEndingDirectorySeparator(ProtectedPaths.Resolve(root));
            if (Path.GetPathRoot(@base) is { } volume && Path.TrimEndingDirectorySeparator(volume) == @base) return "Refusing to remove below a volume root: " + path;
            bool Below(string real, bool orAt) => orAt && real.Equals(@base, comparison) || real.StartsWith(@base + Path.DirectorySeparatorChar, comparison);
            if (NameLink(path))
            {
                if (!Below(Path.TrimEndingDirectorySeparator(ProtectedPaths.Resolve(Path.GetDirectoryName(path)!)), orAt: true)) return "Refusing to remove a link outside " + root + ": " + path;
                if (OperatingSystem.IsWindows() && Directory.Exists(path)) Directory.Delete(path); else File.Delete(path);
                return null;
            }
            if (!File.Exists(path) && !Directory.Exists(path)) return null;
            if (!Below(Path.TrimEndingDirectorySeparator(ProtectedPaths.Resolve(path)), orAt: false)) return "Refusing to remove outside " + root + ": " + path;
            if (!Directory.Exists(path)) { File.Delete(path); return null; }
            // A read-only entry below keeps the delete from emptying the folder: bash makes each folder writable first (chmod u+rwx),
            // PowerShell clears the read-only attribute entry by entry; links are never entered.
            try { Directory.Delete(path, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Writable(path); Directory.Delete(path, recursive: true); }
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return error.Message; }
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
        bool yours;
        lock (_sync) yours = Claims.Contains(owner) && !Releases.Contains(owner);
        return Task.FromResult(yours ? new HostLockResult(HostLockState.Yours, owner, "yours")
            : HeldBy != null ? new HostLockResult(HostLockState.HeldByOther, HeldBy, $"{lockPath} on {Name} is held by another run: {HeldBy}")
            : new HostLockResult(HostLockState.Free, null, "free"));
    }
    public Task<HostLockResult> ReleaseLockAsync(string lockPath, string owner, TimeSpan timeout, CancellationToken cancellation = default)
    {
        lock (_sync)
        {
            if (ReleaseLockResults.TryDequeue(out var result)) return Task.FromResult(result);
            Releases.Add(owner);
        }
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
        AfterFetch?.Invoke(localDirectory);
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

    /// <summary>Copies a folder of the mirror, as a test sets up a host directory (a stand-in profile).</summary>
    internal static void CopyDirectory(string source, string target)
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
