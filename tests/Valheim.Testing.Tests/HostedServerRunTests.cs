using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

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
    public List<string> Releases { get; } = [];
    /// <summary>Another run's claimant holding the lock.</summary>
    public string? HeldBy { get; set; }
    public Dictionary<string, HostResult> Failures { get; } = [];
    public Exception? TunnelFailure { get; set; }
    public bool PortBusy { get; set; }
    /// <summary>The port check's whole reply when set; else free or busy by <see cref="PortBusy"/>.</summary>
    public string? PortReply { get; set; }
    public bool GameActive { get; set; }
    public long AvailableCopyBytes { get; set; } = 100L << 30;
    /// <summary>The server ignores the clean stop's SIGINT, so it is killed after the wait.</summary>
    public bool IgnoreQuit { get; set; }
    public bool ClientWritesBepInExLog { get; set; } = true;
    /// <summary>The crossplay library check's reply: by default libparty.so loads.</summary>
    public string PartyReply { get; set; } = "VT-LDD \tlibc.so.6 => /lib/x86_64-linux-gnu/libc.so.6 (0x1)\nVT-PARTY checked valheim_server_Data/Plugins/libparty.so 0\n";
    /// <summary>The signed-in Steam user check's reply.</summary>
    public string SteamUserReply { get; set; } = "VT-STEAMUSER unreadable the host user has no loginusers.vdf in its Steam directories\n";
    private string? _runtime; // The host runtime of the last server start, whose log a clean stop appends to.
    public List<FakeForward> Tunnels { get; } = [];
    /// <summary>What the copy does to the runtime after copying, for example editing a file.</summary>
    public Action<string>? AfterCopy { get; set; }
    /// <summary>What the staging apply leaves in the runtime after applying, for example a loader file it failed to remove.</summary>
    public Action<string>? AfterApply { get; set; }
    /// <summary>Controlled delay for tests that prove independent actors prepare concurrently.</summary>
    public Func<Task>? BeforeShip { get; set; }
    /// <summary>Simulates a transport failure after the remote staging directory has been populated.</summary>
    public Action<string>? AfterShip { get; set; }
    public List<(string Game, string Start)> Stops { get; } = [];
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

    private static string ScriptName(string script) =>
        ReferenceEquals(script, HostInstallScripts.Copy) ? "copy" :
        ReferenceEquals(script, HostInstallScripts.PowerShellCopy) ? "copy" :
        ReferenceEquals(script, HostInstallScripts.BashList) ? "list" :
        ReferenceEquals(script, HostInstallScripts.PowerShellList) ? "list" :
        ReferenceEquals(script, HostInstallScripts.BashPort) ? "port" :
        ReferenceEquals(script, HostInstallScripts.PowerShellPort) ? "port" :
        ReferenceEquals(script, HostServerScripts.Start) ? "start" :
        ReferenceEquals(script, HostServerScripts.WindowsStart) ? "start" :
        ReferenceEquals(script, HostServerScripts.Keep) ? "keep" :
        ReferenceEquals(script, HostServerScripts.WindowsKeep) ? "keep" :
        ReferenceEquals(script, HostedRunScripts.Retire) ? "retire" :
        ReferenceEquals(script, HostedRunScripts.WindowsRetire) ? "retire" :
        ReferenceEquals(script, HostedRunScripts.DropKept) ? "drop-kept" :
        ReferenceEquals(script, HostedRunScripts.WindowsDropKept) ? "drop-kept" :
        ReferenceEquals(script, HostedRuntimeStage.WindowsApply) ? "apply-stage" :
        ReferenceEquals(script, HostedRuntimeStage.BashApply) ? "apply-stage" :
        ReferenceEquals(script, HostedRuntimeStage.WindowsCleanup) ? "cleanup-stage" :
        ReferenceEquals(script, HostedRuntimeStage.BashCleanup) ? "cleanup-stage" :
        ReferenceEquals(script, HostedCharacterStage.WindowsInstall) ? "character-install" :
        ReferenceEquals(script, HostedCharacterStage.BashInstall) ? "character-install" :
        ReferenceEquals(script, HostedCharacterStage.WindowsRetire) ? "character-retire" :
        ReferenceEquals(script, HostedCharacterStage.BashRetire) ? "character-retire" :
        ReferenceEquals(script, HostedCharacterStage.PowerShellDirectories) ? "character-folders" :
        ReferenceEquals(script, HostedCharacterStage.BashDirectories) ? "character-folders" :
        ReferenceEquals(script, HostedCharacterStage.WindowsDropStage) ? "character-drop" :
        ReferenceEquals(script, HostedCharacterStage.BashDropStage) ? "character-drop" :
        ReferenceEquals(script, HostedRuntimeStage.WindowsProcessCheck) ? "game-process" :
        ReferenceEquals(script, HostedRuntimeStage.BashProcessCheck) ? "game-process" :
        ReferenceEquals(script, HostCopyCapacityProbe.Windows) ? "copy-space" :
        ReferenceEquals(script, HostCopyCapacityProbe.Bash) ? "copy-space" :
        ReferenceEquals(script, CrossplayLibraryScripts.Check) ? "party" :
        ReferenceEquals(script, HostServerScripts.WindowsServerLogonCheck) ? "server-logon" :
        ReferenceEquals(script, InteractiveScripts.LinuxWait) ? "wait" :
        ReferenceEquals(script, InteractiveScripts.WindowsWait) ? "wait" :
        ReferenceEquals(script, InteractiveScripts.LinuxStop) ? "stop" :
        ReferenceEquals(script, HostServerScripts.WindowsStop) ? "stop" :
        ReferenceEquals(script, InteractiveScripts.LinuxStart) ? "client-start" :
        ReferenceEquals(script, HostedClientScripts.BashKeep) ? "client-keep" :
        ReferenceEquals(script, HostedClientScripts.BashMoveAside) ? "move-aside" :
        ReferenceEquals(script, HostClientPreflight.BashRead) ? "preflight-read" :
        ReferenceEquals(script, HostClientPreflight.PowerShellRead) ? "preflight-read" :
        ReferenceEquals(script, HostClientPreflight.BashExists) ? "preflight-exists" :
        ReferenceEquals(script, HostClientPreflight.PowerShellExists) ? "preflight-exists" :
        ReferenceEquals(script, SteamSignedInUsers.PowerShell) ? "steam-user" :
        ReferenceEquals(script, SteamSignedInUsers.Bash) ? "steam-user" : "other";

    public async Task<HostResult> RunAsync(string script, IReadOnlyDictionary<string, string>? variables, TimeSpan timeout, CancellationToken cancellation = default)
    {
        var v = variables ?? new Dictionary<string, string>();
        string name = ScriptName(script);
        lock (_sync) Runs.Add((name, v));
        if (Failures.TryGetValue(name, out var failure)) return failure;
        switch (name)
        {
            case "game-process": return Ok(GameActive ? "VT-GAME busy\n" : "VT-GAME idle\n");
            case "server-logon": return Ok("VT-LOGON " + ServerTaskLogon + "\n");
            case "copy-space":
                return Ok($"VT-STORAGE {DiskSpace.DirectoryBytes(Local(v["source"]))} {AvailableCopyBytes} " +
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(Windows ? "C:\\" : "/")) + "\n");
            case "preflight-exists":
                return Ok(string.Concat(v["paths"].Split('\n').Where(relative => relative.Length != 0 && File.Exists(Local(HostInstall.Join(v["root"], relative))))
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
                AfterCopy?.Invoke(Local(v["dest"]));
                return Ok("VT-COPY copied\n");
            case "apply-stage":
            {
                string runtime = Local(v["runtime"]), stage = Local(v["stage"]);
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
                Directory.Delete(stage, recursive: true);
                AfterApply?.Invoke(runtime);
                return Ok("VT-STAGED selected files only\n");
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
            case "list":
            {
                string root = Local(v["root"]);
                if (!Directory.Exists(root)) return Ok("VT-LIST missing\n");
                var text = new StringBuilder();
                foreach (var (relative, sha) in WorldFixture.Manifest(root)) text.Append(sha).Append("  ./").Append(relative.Replace('\\', '/')).Append('\n');
                if (File.Exists(Path.Combine(root, ServerLaunch.LinuxExecutable))) text.Append("VT-EXEC ").Append(ServerLaunch.LinuxExecutable).Append('\n');
                return Ok(text.Append("VT-LIST done\n").ToString());
            }
            case "port": return Ok(PortReply ?? (PortBusy ? "VT-PORT busy\n" : "VT-PORT free\n"));
            case "party": return Ok(PartyReply);
            case "steam-user": return Ok(SteamUserReply);
            case "start":
            {
                string token = Spec(v["spec"]).Single(line => line.Kind == "env" && line.Text.StartsWith("TEST_SESSION_TOKEN=", StringComparison.Ordinal)).Text["TEST_SESSION_TOKEN=".Length..];
                var process = (FakeOwnedProcess)_server!.Launch(token);
                string boot = Local(v["dir"]);
                Directory.CreateDirectory(boot);
                File.WriteAllText(Path.Combine(boot, "stdout.log"), "server stdout\n");
                _runtime = v["runtime"];
                string log = Path.Combine(Local(v["runtime"]), "BepInEx", "LogOutput.log");
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
    public Task<HostLockResult> CheckLockAsync(string lockPath, string owner, TimeSpan timeout, CancellationToken cancellation = default) => throw new NotSupportedException();
    public Task<HostLockResult> ReleaseLockAsync(string lockPath, string owner, TimeSpan timeout, CancellationToken cancellation = default)
    {
        lock (_sync) Releases.Add(owner);
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
    public Task<HostLogResult> WaitForLogAsync(string logPath, long fromOffset, Regex success, IReadOnlyList<Regex>? failures, TimeSpan timeout, CancellationToken cancellation = default)
    {
        lock (_sync) Runs.Add(("follow", new Dictionary<string, string> { ["log"] = logPath, ["offset"] = fromOffset.ToString() }));
        string? line = File.Exists(Local(logPath)) ? File.ReadAllLines(Local(logPath)).FirstOrDefault(success.IsMatch) : null;
        return Task.FromResult(new HostLogResult(line != null ? HostLogOutcome.Matched : HostLogOutcome.TimedOut, "listening", line, TimeSpan.Zero, line));
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

// The hosted runner: the dedicated server on its environment's server host, against a fake host (no shell, no game).
public sealed partial class HostedServerRunTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("hosted-run-").FullName;
    private const string Install = "/opt/valheim/server", Runs = "/srv/vt/runs", RunId = "run-test";
    private const string RunDirectory = Runs + "/" + RunId;
    private string Mirror => Path.Combine(_root, "host");
    private string Output => Path.Combine(_root, "out");
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private FakeServerHost NewHost(FakeOwnedServer? server = null) => new("linux-box", Mirror, server);
    private FakeOwnedServer NewServer() => new("test.mod", saveRoot: RunDirectory + "/world");
    private static void StageLoader(string root)
    {
        File.WriteAllText(Path.Combine(root, "winhttp.dll"), "MZ target_assembly");
        File.WriteAllText(Path.Combine(root, "doorstop_config.ini"), "[General]\nenabled=true\ntarget_assembly=BepInEx\\core\\BepInEx.Preloader.dll\n");
    }

    [Fact] public void WindowsPowerShellDedicatedServerProfileIsAcceptedBeforeAnyHostOperation()
    {
        var host = NewHost();
        var (planPath, profilePath) = Write(host, hostPlatform: "windows", hostShell: "powershell");
        var profile = TestEnvironment.Read(profilePath);
        var hosted = HostedServerRun.Create(profile, ServerRunPlan.Read<ServerRunPlan>(planPath), "test", new HostedSeams { Host = _ => host, RunId = RunId });
        Assert.Equal("windows", hosted.HostProfile.Platform);
        Assert.Empty(host.Runs);
    }

    // #257: a campaign's prepared install is the server's runtime, verified where it is and never copied again (the campaign
    // test proves the passing path); one that changed between preparation and the run is refused before anything launches.
    [Fact] public async Task APreparedRuntimeThatChangedSincePreparationIsRefusedAndNotCopied()
    {
        var host = NewHost();
        var (planPath, profilePath) = Write(host);
        var plan = ServerRunPlan.Read<ServerRunPlan>(planPath);
        var hosted = HostedServerRun.Create(TestEnvironment.Read(profilePath), plan, "test", new HostedSeams { Host = _ => host, RunId = RunId }, prepared: true);
        Assert.True(hosted.Prepared);
        Assert.Equal(Install, hosted.RuntimeDirectory);
        Assert.Equal(RunDirectory, hosted.RunDirectory); // world and boot evidence stay in the run's own directory
        File.AppendAllText(Path.Combine(host.Local(Install), ServerLaunch.LinuxExecutable), " changed after preparation");
        var report = new ScenarioReport("prepared runtime");
        await Assert.ThrowsAnyAsync<Exception>(() => hosted.LockAndCopyRuntimeAsync(report, plan, pinned: true, CancellationToken.None));
        var step = Assert.Single(report.Steps, step => step.Name == "verify the prepared runtime on the server host");
        Assert.False(step.Passed);
        Assert.Contains(ServerLaunch.LinuxExecutable, step.Error);
        Assert.DoesNotContain("copy", host.Scripts);
        Assert.DoesNotContain("start", host.Scripts);
    }

    [Fact] public async Task WindowsPowerShellProfileRunsTheWholeServerLifecycleAndKeepsOnlyItsEvidence()
    {
        const string windowsRuns = @"C:\vt\runs";
        var server = new FakeOwnedServer("test.mod", saveRoot: windowsRuns + @"\run-test\world");
        var host = new FakeServerHost("windows-server", Mirror, server, windows: true);
        var (plan, profile) = Write(host, hostPlatform: "windows", hostShell: "powershell");
        Assert.Equal(0, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server)));
        Assert.Equal(host.Claims, host.Releases);
        Assert.Contains("start", host.Scripts);
        Assert.Contains("stop", host.Scripts);
        Assert.Contains("keep", host.Scripts);
        Assert.Contains("fetch", host.Scripts);
        Assert.False(Directory.Exists(host.Local(windowsRuns + @"\run-test\runtime")));
        Assert.Contains("fake boot", File.ReadAllText(Path.Combine(Output, "boot-1", "game-0.log")));
    }

    // #295: the hosted server run's own loader step refuses a copied Windows runtime whose proxy (Doorstop 4) and
    // doorstop_config.ini (Doorstop 3) are from different versions, before the server starts.
    [Fact] public async Task AHostedWindowsServerRunRefusesAMixedDoorstopBeforeItStarts()
    {
        var server = new FakeOwnedServer("test.mod", saveRoot: @"C:\vt\runs\run-test\world");
        var host = new FakeServerHost("windows-server", Mirror, server, windows: true);
        var (plan, profile) = Write(host, hostPlatform: "windows", hostShell: "powershell", doorstopConfig: DoorstopMixPathsTests.Doorstop3Config);
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server)));
        Assert.DoesNotContain("start", host.Scripts);
        var step = JsonDocument.Parse(File.ReadAllText(Path.Combine(Output, "result.json"))).RootElement.GetProperty("Steps").EnumerateArray()
            .Single(s => s.GetProperty("Name").GetString() == "copied Windows runtime has a coherent Doorstop loader");
        Assert.False(step.GetProperty("Passed").GetBoolean());
        DoorstopMixPathsTests.AssertRefusal(new InvalidOperationException(step.GetProperty("Error").GetString()), "server runtime",
            "is Doorstop 4, which reads only [General] in doorstop_config.ini, but that file is written for Doorstop 3 ([UnityDoorstop])");
    }

    [Fact] public async Task OneHostedPreparationCopiesOnlySelectedFilesWithoutChangingTheSource()
    {
        var host = new FakeServerHost("windows-server", Mirror, windows: true);
        const string source = @"C:\game\server", runtime = @"C:\runs\one\runtime", staging = @"C:\runs\one\staging";
        string install = host.Local(source);
        FakeInstalls.Server(install);
        File.WriteAllText(Path.Combine(install, ServerLaunch.WindowsExecutable), "server");
        StageLoader(install);
        string old = Path.Combine(install, "BepInEx", "plugins", "unrelated.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(old)!);
        File.WriteAllText(old, "unrelated");
        string chosen = Path.Combine(_root, "chosen.dll");
        File.WriteAllText(chosen, "selected plugin");
        var listing = await HostedRuntimeStage.PrepareAsync(host, HostedRuntimeKind.Server, source, runtime, staging,
            [new HostedRuntimeFile(chosen, "BepInEx/plugins/chosen.dll")], TimeSpan.FromSeconds(30));
        Assert.Equal(FileHash.Sha256(chosen), listing.Files["BepInEx/plugins/chosen.dll"]);
        Assert.True(File.Exists(old));
        Assert.False(File.Exists(Path.Combine(host.Local(runtime), "BepInEx", "plugins", "unrelated.dll")));
        Assert.False(Directory.Exists(host.Local(staging)));
        Assert.Contains("apply-stage", host.Scripts);
        Assert.Contains("copy", host.Scripts);
    }

    [Fact] public async Task ReviewedLoaderReplacesAnIncoherentSourceOnlyInTheDisposableRuntime()
    {
        var host = new FakeServerHost("windows-client", Mirror, windows: true);
        const string source = @"C:\game\client", runtime = @"C:\runs\loader\runtime", staging = @"C:\runs\loader\staging";
        string install = host.Local(source);
        FakeInstalls.Client(install);
        File.WriteAllText(Path.Combine(install, ClientLaunch.WindowsExecutable), "client");
        StageLoader(install);
        string packageRoot = Path.Combine(_root, "approved-loader");
        Directory.CreateDirectory(Path.Combine(packageRoot, "BepInEx/core"));
        File.WriteAllText(Path.Combine(packageRoot, BepInExLoader.Core), "core");
        File.WriteAllText(Path.Combine(packageRoot, BepInExLoader.Preloader), "preloader");
        StageLoader(packageRoot);
        var package = BepInExLoaderPackage.Capture(packageRoot, "test-loader", "1");
        File.WriteAllText(Path.Combine(install, "winhttp.dll"), "target_assembly");
        File.WriteAllText(Path.Combine(install, "doorstop_config.ini"), "[General]\nenabled=true\ntargetAssembly=BepInEx/core/BepInEx.Preloader.dll\n");
        File.WriteAllText(Path.Combine(install, "BepInEx/core/stale.dll"), "must disappear");
        var before = WorldFixture.Manifest(install);
        string plugin = Path.Combine(_root, "selected.dll");
        File.WriteAllText(plugin, "plugin");
        var files = new[] { new HostedRuntimeFile(plugin, "BepInEx/plugins/selected.dll") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => HostedRuntimeStage.PrepareAsync(host,
            HostedRuntimeKind.Client, source, runtime, staging, files, TimeSpan.FromSeconds(30)));
        Assert.DoesNotContain("copy", host.Scripts);
        var listing = await HostedRuntimeStage.PrepareAsync(host, HostedRuntimeKind.Client, source, runtime, staging,
            files, TimeSpan.FromSeconds(30), loaderPackage: package);
        foreach (var file in package.Files) Assert.Equal(file.Value, listing.Files[file.Key]);
        Assert.False(listing.Files.ContainsKey("BepInEx/core/stale.dll"));
        Assert.Equal(package.Loader, HostInstall.Pins(listing).Loader);
        WorldFixture.Verify(install, before);
        Assert.Equal(before.Count, WorldFixture.Manifest(install).Count);
    }

    [Fact] public async Task ALoaderFileOutsideTheReviewedPackageFailsPreparation()
    {
        var host = new FakeServerHost("windows-client", Mirror, windows: true);
        const string source = @"C:\game\client", runtime = @"C:\runs\loader\runtime", staging = @"C:\runs\loader\staging";
        string install = host.Local(source);
        FakeInstalls.Client(install);
        File.WriteAllText(Path.Combine(install, ClientLaunch.WindowsExecutable), "client");
        StageLoader(install);
        string packageRoot = Path.Combine(_root, "approved-loader");
        Directory.CreateDirectory(Path.Combine(packageRoot, "BepInEx/core"));
        File.WriteAllText(Path.Combine(packageRoot, BepInExLoader.Core), "core");
        File.WriteAllText(Path.Combine(packageRoot, BepInExLoader.Preloader), "preloader");
        StageLoader(packageRoot);
        var package = BepInExLoaderPackage.Capture(packageRoot, "test-loader", "1");
        // A replacement that left a loader file the package does not hold.
        host.AfterApply = prepared =>
        {
            Directory.CreateDirectory(Path.Combine(prepared, "doorstop_libs"));
            File.WriteAllText(Path.Combine(prepared, "doorstop_libs", "libdoorstop_x64.so"), "left behind");
        };
        string plugin = Path.Combine(_root, "selected.dll");
        File.WriteAllText(plugin, "plugin");
        var files = new[] { new HostedRuntimeFile(plugin, "BepInEx/plugins/selected.dll") };
        var error = await Assert.ThrowsAsync<IOException>(() => HostedRuntimeStage.PrepareAsync(host, HostedRuntimeKind.Client, source, runtime, staging,
            files, TimeSpan.FromSeconds(30), loaderPackage: package));
        Assert.Contains("is not the reviewed package's (" + package.Loader + ")", error.Message);
    }

    [Fact] public async Task ClientPreparationRefusesAServerAndLeavesItsSourceAlone()
    {
        var host = new FakeServerHost("windows-client", Mirror, windows: true);
        const string source = @"C:\game\client", runtime = @"C:\runs\client\runtime", staging = @"C:\runs\client\staging";
        string install = host.Local(source);
        FakeInstalls.Client(install);
        File.WriteAllText(Path.Combine(install, ClientLaunch.WindowsExecutable), "client");
        StageLoader(install);
        string old = Path.Combine(install, "BepInEx", "plugins", "unrelated.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(old)!);
        File.WriteAllText(old, "unrelated");
        string chosen = Path.Combine(_root, "cli.dll");
        File.WriteAllText(chosen, "selected CLI");
        var files = new[] { new HostedRuntimeFile(chosen, "BepInEx/plugins/cli.dll") };
        var listing = await HostedRuntimeStage.PrepareAsync(host, HostedRuntimeKind.Client, source, runtime, staging, files, TimeSpan.FromSeconds(30));
        Assert.Equal(FileHash.Sha256(chosen), listing.Files["BepInEx/plugins/cli.dll"]);
        Assert.False(listing.Files.ContainsKey("BepInEx/plugins/unrelated.dll"));
        Assert.True(File.Exists(old));
        Assert.False(Directory.Exists(host.Local(staging)));
        await Assert.ThrowsAsync<FileNotFoundException>(() => HostedRuntimeStage.PrepareAsync(host, HostedRuntimeKind.Server, source,
            @"C:\runs\server\runtime", @"C:\runs\server\staging", files, TimeSpan.FromSeconds(30)));
    }

    // The source inspection every campaign preparation starts with: an unrecognised Windows proxy and a Linux install without
    // its Doorstop library are refused before anything is copied, each naming what it found.
    [Fact] public async Task SourceInspectionRefusesAnUnrecognisedProxyAndAnIncompleteLinuxLoader()
    {
        var windows = new FakeServerHost("windows-client", Path.Combine(_root, "windows-source"), windows: true);
        string winSource = windows.Local(@"C:\game\source");
        FakeInstalls.Client(winSource);
        File.WriteAllText(Path.Combine(winSource, ClientLaunch.WindowsExecutable), "game");
        File.WriteAllText(Path.Combine(winSource, "winhttp.dll"), "MZ fake");
        File.WriteAllText(Path.Combine(winSource, "doorstop_config.ini"), "[General]\nenabled=true\ntarget_assembly=BepInEx\\core\\BepInEx.Preloader.dll\n");
        var unrecognised = await Assert.ThrowsAsync<DoorstopPairingException>(() =>
            HostedRuntimeStage.InspectSourceAsync(windows, HostedRuntimeKind.Client, @"C:\game\source", null, TimeSpan.FromSeconds(30)));
        Assert.Contains("not a Doorstop proxy this check recognises", unrecognised.Message);
        // A reviewed package complete for another platform is refused as the package's fault, before the source is listed.
        string linuxPackage = Path.Combine(_root, "linux-package");
        foreach (string relative in new[] { "BepInEx/core/BepInEx.Preloader.dll", "BepInEx/core/BepInEx.dll", "doorstop_libs/libdoorstop_x64.so" })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(linuxPackage, relative))!);
            File.WriteAllText(Path.Combine(linuxPackage, relative), relative);
        }
        var wrongPlatform = await Assert.ThrowsAsync<InvalidDataException>(() => HostedRuntimeStage.InspectSourceAsync(windows, HostedRuntimeKind.Client, @"C:\game\source",
            BepInExLoaderPackage.Capture(linuxPackage, "BepInExPack_Valheim", "5.4.2333"), TimeSpan.FromSeconds(30)));
        Assert.Contains("does not match the host platform", wrongPlatform.Message);
        Assert.Equal(1, windows.Runs.Count(run => run.Script == "list")); // the unrecognised proxy's listing only; the package was refused first

        var linux = new FakeServerHost("linux-client", Path.Combine(_root, "linux-source"));
        string linuxSource = linux.Local("/game/source");
        FakeInstalls.Client(linuxSource);
        File.WriteAllText(Path.Combine(linuxSource, ClientLaunch.LinuxExecutable), "game");
        var incomplete = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            HostedRuntimeStage.InspectSourceAsync(linux, HostedRuntimeKind.Client, "/game/source", null, TimeSpan.FromSeconds(30)));
        Assert.Contains("doorstop_libs/libdoorstop_x64.so", incomplete.Message);
        FakeInstalls.LinuxLoader(linuxSource);
        await HostedRuntimeStage.InspectSourceAsync(linux, HostedRuntimeKind.Client, "/game/source", null, TimeSpan.FromSeconds(30));
        // A macOS install loads BepInEx through either Doorstop library: BepInExPack's own x64 one is enough.
        var mac = new FakeServerHost("mac-client", Path.Combine(_root, "mac-source"), kind: GameHostKind.Local);
        string macSource = mac.Local("/game/mac");
        FakeInstalls.Client(macSource);
        File.WriteAllText(Path.Combine(macSource, "valheim_Data", "Managed", InstallPins.GameAssemblyName), "game build 1");
        Directory.CreateDirectory(Path.Combine(macSource, "Valheim.app", "Contents", "MacOS"));
        File.WriteAllText(Path.Combine(macSource, "Valheim.app", "Contents", "MacOS", "Valheim"), "game");
        Assert.Contains("doorstop_libs/libdoorstop_x64.dylib or libdoorstop.dylib", (await Assert.ThrowsAsync<FileNotFoundException>(() =>
            HostedRuntimeStage.InspectSourceAsync(mac, HostedRuntimeKind.Client, "/game/mac", null, TimeSpan.FromSeconds(30)))).Message);
        Directory.CreateDirectory(Path.Combine(macSource, "doorstop_libs"));
        File.WriteAllText(Path.Combine(macSource, "doorstop_libs", "libdoorstop_x64.dylib"), "pack doorstop");
        await HostedRuntimeStage.InspectSourceAsync(mac, HostedRuntimeKind.Client, "/game/mac", null, TimeSpan.FromSeconds(30));
        Assert.All(new[] { windows, linux }, host => Assert.DoesNotContain(host.Scripts, script => script is "copy" or "ship" or "start"));
    }

    [Fact] public async Task MacClientBundleCanBePreparedFromItsContainingInstall()
    {
        var host = new FakeServerHost("mac-client", Mirror);
        const string source = "/game/client", runtime = "/runs/vt-one/runtime", staging = "/runs/vt-one/staging";
        string install = host.Local(source);
        string managed = Path.Combine(install, "Valheim.app", "Contents", "Resources", "Data", "Managed");
        Directory.CreateDirectory(managed);
        File.WriteAllText(Path.Combine(managed, InstallPins.GameAssemblyName), "mac game");
        string executable = Path.Combine(install, "Valheim.app", "Contents", "MacOS", "Valheim");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "mac executable");
        string core = Path.Combine(install, "BepInEx", "core", "BepInEx.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(core)!);
        File.WriteAllText(core, "core");
        File.WriteAllText(Path.Combine(install, "BepInEx", "core", "BepInEx.Preloader.dll"), "preloader");
        FakeInstalls.MacLoader(install);
        string chosen = Path.Combine(_root, "mac-cli.dll");
        File.WriteAllText(chosen, "selected CLI");
        var listing = await HostedRuntimeStage.PrepareAsync(host, HostedRuntimeKind.Client, source, runtime, staging,
            [new HostedRuntimeFile(chosen, "BepInEx/plugins/mac-cli.dll")], TimeSpan.FromSeconds(30));
        Assert.Equal(FileHash.Sha256(chosen), listing.Files["BepInEx/plugins/mac-cli.dll"]);
        Assert.True(File.Exists(executable));
    }

    [Fact] public async Task PreparationRejectsMixedLoaderFilesAndSourceNestedStagingBeforeShipping()
    {
        var host = new FakeServerHost("windows-server", Mirror, windows: true);
        const string source = @"C:\game\server", runtime = @"C:\game\server\runs\one\runtime", staging = @"C:\game\server\runs\one\staging";
        string install = host.Local(source);
        FakeInstalls.Server(install);
        File.WriteAllText(Path.Combine(install, ServerLaunch.WindowsExecutable), "server");
        StageLoader(install);
        string chosen = Path.Combine(_root, "chosen.dll");
        File.WriteAllText(chosen, "selected");
        await Assert.ThrowsAsync<ArgumentException>(() => HostedRuntimeStage.PrepareAsync(host, HostedRuntimeKind.Server, source, runtime, staging,
            [new HostedRuntimeFile(chosen, "BepInEx/plugins/chosen.dll")], TimeSpan.FromSeconds(30)));
        await Assert.ThrowsAsync<ArgumentException>(() => HostedRuntimeStage.PrepareAsync(host, HostedRuntimeKind.Server, source,
            @"C:\runs\one\runtime", @"C:\runs\one\staging",
            [new HostedRuntimeFile(chosen, "winhttp.dll")], TimeSpan.FromSeconds(30)));
        Assert.DoesNotContain("copy", host.Scripts);
    }

    [Fact] public async Task FailedPreparationRetiresOnlyItsNewCopyAndStaging()
    {
        var host = new FakeServerHost("windows-server", Mirror, windows: true);
        const string source = @"C:\game\server", runtime = @"C:\runs\failed\runtime", staging = @"C:\runs\failed\staging";
        string install = host.Local(source);
        FakeInstalls.Server(install);
        File.WriteAllText(Path.Combine(install, ServerLaunch.WindowsExecutable), "server");
        StageLoader(install);
        string unrelated = Path.Combine(install, "BepInEx", "plugins", "unrelated.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(unrelated)!);
        File.WriteAllText(unrelated, "keep me");
        string chosen = Path.Combine(_root, "selected.dll");
        File.WriteAllText(chosen, "selected");
        host.Failures["apply-stage"] = FakeServerHost.TransportFailure;
        await Assert.ThrowsAsync<HostOperationException>(() => HostedRuntimeStage.PrepareAsync(host, HostedRuntimeKind.Server, source, runtime, staging,
            [new HostedRuntimeFile(chosen, "BepInEx/plugins/selected.dll")], TimeSpan.FromSeconds(30)));
        Assert.True(File.Exists(unrelated));
        Assert.False(Directory.Exists(host.Local(runtime)));
        Assert.False(Directory.Exists(host.Local(staging)));
        Assert.Contains("cleanup-stage", host.Scripts);
    }

    [Theory]
    [InlineData("ship")]
    [InlineData("copy")]
    public async Task PartialPreparationIsCleanedAfterTheRemoteOperationThrows(string operation)
    {
        var host = new FakeServerHost("windows-server", Mirror, windows: true);
        const string source = @"C:\game\server", runtime = @"C:\runs\partial\runtime", staging = @"C:\runs\partial\staging";
        string install = host.Local(source);
        FakeInstalls.Server(install);
        File.WriteAllText(Path.Combine(install, ServerLaunch.WindowsExecutable), "server");
        StageLoader(install);
        string chosen = Path.Combine(_root, "selected.dll");
        File.WriteAllText(chosen, "selected");
        if (operation == "ship") host.AfterShip = _ => throw new IOException("ship reply lost after copy");
        else host.AfterCopy = _ => throw new IOException("copy reply lost after copy");

        var error = await Assert.ThrowsAsync<IOException>(() => HostedRuntimeStage.PrepareAsync(host,
            HostedRuntimeKind.Server, source, runtime, staging,
            [new HostedRuntimeFile(chosen, "BepInEx/plugins/selected.dll")], TimeSpan.FromSeconds(30)));

        Assert.Contains("reply lost", error.Message);
        Assert.False(Directory.Exists(host.Local(runtime)));
        Assert.False(Directory.Exists(host.Local(staging)));
        Assert.Contains("cleanup-stage", host.Scripts);
        Assert.True(File.Exists(Path.Combine(install, ServerLaunch.WindowsExecutable)));
    }

    [Fact] public async Task FailedCleanupKeepsBothTheOriginalErrorAndTheUnprovenResidue()
    {
        var host = new FakeServerHost("windows-server", Mirror, windows: true);
        const string source = @"C:\game\server", runtime = @"C:\runs\residue\runtime", staging = @"C:\runs\residue\staging";
        string install = host.Local(source);
        FakeInstalls.Server(install);
        File.WriteAllText(Path.Combine(install, ServerLaunch.WindowsExecutable), "server");
        StageLoader(install);
        string chosen = Path.Combine(_root, "selected.dll");
        File.WriteAllText(chosen, "selected");
        host.AfterShip = _ => throw new IOException("ship reply lost after copy");
        host.Failures["cleanup-stage"] = FakeServerHost.TransportFailure;

        var error = await Assert.ThrowsAsync<AggregateException>(() => HostedRuntimeStage.PrepareAsync(host,
            HostedRuntimeKind.Server, source, runtime, staging,
            [new HostedRuntimeFile(chosen, "BepInEx/plugins/selected.dll")], TimeSpan.FromSeconds(30)));

        Assert.Contains("reply lost", error.InnerExceptions[0].Message);
        Assert.Contains("Cleaning failed preparation", error.InnerExceptions[1].Message);
        Assert.True(Directory.Exists(host.Local(staging)));
    }

    [Fact] public async Task CancelledPreparationStillCleansAStagedRemoteCopy()
    {
        var host = new FakeServerHost("windows-server", Mirror, windows: true);
        const string source = @"C:\game\server", runtime = @"C:\runs\cancelled\runtime", staging = @"C:\runs\cancelled\staging";
        string install = host.Local(source);
        FakeInstalls.Server(install);
        File.WriteAllText(Path.Combine(install, ServerLaunch.WindowsExecutable), "server");
        StageLoader(install);
        string chosen = Path.Combine(_root, "selected.dll");
        File.WriteAllText(chosen, "selected");
        using var cancellation = new CancellationTokenSource();
        host.AfterShip = _ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HostedRuntimeStage.PrepareAsync(host,
            HostedRuntimeKind.Server, source, runtime, staging,
            [new HostedRuntimeFile(chosen, "BepInEx/plugins/selected.dll")], TimeSpan.FromSeconds(30), cancellation.Token));

        Assert.False(Directory.Exists(host.Local(staging)));
        Assert.Contains("cleanup-stage", host.Scripts);
    }

    // The host's install (the runtime the plan pins) and a local world; returns the plan and the profile.
    private (string Plan, string Profile) Write(FakeServerHost host, int planPort = 5577, string hostPlatform = "linux", string hostShell = "bash", bool withClient = false, bool unpinned = false,
        bool crossplay = false, string portOption = "-port", string gamePort = "2456", object? steamAccounts = null, string? steamAccount = null,
        string doorstopConfig = DoorstopMixPathsTests.Doorstop4Config)
    {
        bool windows = hostPlatform == "windows";
        string hostInstall = windows ? @"C:\valheim\server" : Install;
        string install = host.Local(hostInstall);
        Directory.CreateDirectory(install);
        FakeInstalls.Server(install);
        if (windows)
        {
            File.Delete(Path.Combine(install, ServerLaunch.LinuxExecutable));
            File.WriteAllText(Path.Combine(install, ServerLaunch.WindowsExecutable), "server");
            File.WriteAllText(Path.Combine(install, "winhttp.dll"), "MZ target_assembly");
            File.WriteAllText(Path.Combine(install, "doorstop_config.ini"), doorstopConfig);
        }
        else File.WriteAllText(Path.Combine(install, ServerLaunch.LinuxExecutable), "server");
        File.WriteAllText(Path.Combine(install, "BepInEx", "core", "BepInEx.Preloader.dll"), "preloader");
        string world = Path.Combine(_root, "world");
        Directory.CreateDirectory(Path.Combine(world, "worlds_local"));
        File.WriteAllText(Path.Combine(world, "worlds_local", "Test.db"), "world");
        var plan = new Dictionary<string, object>
        {
            ["scenario"] = "smoke",
            ["runtime"] = new { source = install, sha256 = unpinned ? new Dictionary<string, string>() : WorldFixture.Manifest(install) },
            ["world"] = new { source = world, sha256 = WorldFixture.Manifest(world) },
            ["arguments"] = new[] { "-batchmode", "-nographics", "-savedir", "{world}", portOption, gamePort, "-logFile", "{runtime}/toolkit-unity.log" },
            ["pins"] = unpinned ? new Dictionary<string, string>() : new Dictionary<string, string> { ["worlduid"] = "1" },
            ["port"] = planPort,
        };
        if (crossplay) plan["crossplay"] = true;
        if (unpinned) plan["pinning"] = "none"; else plan["runtimePins"] = InstallPins.Of(install);
        string planPath = Path.Combine(_root, "plan.json");
        File.WriteAllText(planPath, JsonSerializer.Serialize(plan));
        var hosts = new Dictionary<string, object>
        {
            ["linux-box"] = new { kind = "ssh", platform = hostPlatform, shell = hostShell, destination = "tester@linux-box.example", @lock = hostPlatform == "windows" ? @"C:\vt\lock" : "/var/tmp/vt/lock" },
        };
        var clients = new Dictionary<string, object>();
        if (withClient)
        {
            hosts["linux-gpu"] = new { kind = "ssh", platform = "linux", shell = "bash", destination = "tester@linux-gpu.example", @lock = "/home/tester/lock" };
            var player = new Dictionary<string, object> { ["host"] = "linux-gpu", ["install"] = "/home/tester/valheim", ["runtime"] = "/home/tester/runs", ["cliPort"] = 5578 };
            if (steamAccount != null) player["steamAccount"] = steamAccount;
            clients["player"] = player;
        }
        if (steamAccounts != null) hosts[LeaseBox.Name] = LeaseBox.Profile;
        var server = hostPlatform == "windows"
            ? new { host = "linux-box", install = @"C:\valheim\server", runtime = @"C:\vt\runs", cliPort = 5577, gamePort = 2456 }
            : new { host = "linux-box", install = Install, runtime = Runs, cliPort = 5577, gamePort = 2456 };
        string profilePath = Path.Combine(_root, "environment.json");
        File.WriteAllText(profilePath, JsonSerializer.Serialize(steamAccounts == null ? new { hosts, server, clients } : (object)new { hosts, server, clients, steamAccounts }));
        return (planPath, profilePath);
    }

    private static PinnedServerRunOptions<ServerRunPlan> Options(FakeServerHost host, FakeOwnedServer? server, Func<PinnedServerRunContext<ServerRunPlan>, Task>? scenario = null,
        FakeServerHost? clientHost = null, IGameTransport? clientTransport = null, IGameHost? leaseHost = null, TimeSpan? renewEvery = null, string name = "toolkit-smoke") => new()
    {
        Name = name,
        ReadPlan = path => { var plan = ServerRunPlan.Read<ServerRunPlan>(path); plan.ValidateServerPlan([], "TEST_SESSION_TOKEN"); return plan; },
        SessionCapability = "test.mod/session", SessionTokenVariable = "TEST_SESSION_TOKEN",
        Scenario = scenario ?? (_ => Task.CompletedTask),
        HostSeams = new HostedSeams
        {
            Host = hostName => hostName == "linux-box" ? host : hostName == LeaseBox.Name && leaseHost != null ? leaseHost : clientHost ?? throw new InvalidOperationException("No fake host " + hostName),
            Connect = port => port == 15578 ? clientTransport! : server!.Connect(),
            StateWaits = false, RunId = RunId, SteamRenewEvery = renewEvery,
        },
    };
    private JsonElement Result() => JsonDocument.Parse(File.ReadAllText(Path.Combine(Output, "result.json"))).RootElement;
    private IReadOnlyList<string?> StepNames() => Result().GetProperty("Steps").EnumerateArray().Select(step => step.GetProperty("Name").GetString()).ToList();
    private JsonElement Step(string name) => Result().GetProperty("Steps").EnumerateArray().Single(step => step.GetProperty("Name").GetString() == name);

    // #194: what the run wrote in the host's runtime copy is kept; a copy kept on request, or whose server may still run, stays on the host.
    [Fact] public async Task TheHostRuntimeCopyKeepsWhatTheRunWroteAndStaysWhenItMustOrIsAskedTo()
    {
        var server = NewServer(); var host = NewHost(server);
        var (plan, profile) = Write(host);
        Assert.Equal(0, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server, run =>
        {
            string cache = Path.Combine(host.Local(run.RuntimeDirectory), "BepInEx", "cache");
            Directory.CreateDirectory(cache); File.WriteAllText(Path.Combine(cache, "audit.txt"), "written on the host");
            return Task.CompletedTask;
        })));
        Assert.Equal("written on the host", File.ReadAllText(Path.Combine(Output, "runtime-changes", "BepInEx", "cache", "audit.txt")));
        var changes = JsonDocument.Parse(File.ReadAllText(Path.Combine(Output, "runtime-changes", "changes.json"))).RootElement;
        Assert.Contains("BepInEx/cache/audit.txt", changes.GetProperty("Added").EnumerateArray().Select(e => e.GetString()));
        Assert.False(Directory.Exists(host.Local(RunDirectory + "/runtime")));
        Assert.False(Directory.Exists(host.Local(RunDirectory + "/runtime-changes"))); // fetched, so not kept twice

        // A cleanup the host could not finish fails the Cleanup step: the run fails (exit 1), its scenario still passed, and
        // the host's lock is released.
        Directory.Delete(Output, true); Directory.Delete(host.Local(RunDirectory), true);
        host.Failures["retire"] = new HostResult(HostOutcome.Exited, 3, "", "rm: cannot remove", TimeSpan.Zero, false);
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server)));
        Assert.StartsWith("cleanup failed", Result().GetProperty("Provenance").GetProperty("runtimeCopy").GetString());
        // This scenario records no step of its own, so the runtime-ready state is the one that says the run got that far.
        Assert.Equal((false, true, false), (Result().GetProperty("Passed").GetBoolean(), Result().GetProperty("RuntimeReady").GetBoolean(), Result().GetProperty("CleanupVerified").GetBoolean()));
        var failed = Assert.Single(Result().GetProperty("Steps").EnumerateArray(), s => !s.GetProperty("Passed").GetBoolean());
        Assert.Equal(("remove the server host's runtime copy, keeping what the run changed", "Cleanup"), (failed.GetProperty("Name").GetString(), failed.GetProperty("Phase").GetString()));
        Assert.Equal(host.Claims.Count, host.Releases.Count);
        host.Failures.Remove("retire");

        // Kept on request: no retire script, and the report names the copy.
        Directory.Delete(Output, true); Directory.Delete(host.Local(RunDirectory), true);
        RunRetirement.KeepOverride.Value = true; // VALHEIM_TESTING_KEEP_RUNTIME=1, in this test's flow only
        try { Assert.Equal(0, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server))); }
        finally { RunRetirement.KeepOverride.Value = null; }
        Assert.True(Directory.Exists(host.Local(RunDirectory + "/runtime")));
        Assert.Contains("kept on request", Result().GetProperty("Provenance").GetProperty("runtimeCopy").GetString());
    }
    [Fact] public async Task AStagedRuntimeCannotStandInForAHostCopy()
    {
        var server = NewServer(); var host = NewHost(server);
        var (plan, profile) = Write(host);
        using var staged = WorldFixture.Copy(host.Local(Install), Path.Combine(_root, "staged"), WorldFixture.Manifest(host.Local(Install)));
        var options = Options(host, server);
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], new PinnedServerRunOptions<ServerRunPlan>
        {
            Name = options.Name, ReadPlan = options.ReadPlan, SessionCapability = options.SessionCapability, SessionTokenVariable = options.SessionTokenVariable,
            Scenario = options.Scenario, HostSeams = options.HostSeams, StagedRuntime = staged,
        }));
        Assert.DoesNotContain(host.Runs, run => run.Script == "copy");
    }
    [Fact] public async Task ARemoteRunCopiesAndVerifiesOnTheHostReachesTheCliThroughTheTunnelAndStopsOnlyItsServer()
    {
        var server = NewServer(); var host = NewHost(server);
        var (plan, profile) = Write(host);
        IGameHost? seenHost = null; string? seenRuntime = null;
        int code = await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server, run =>
        {
            seenHost = run.ServerHost; seenRuntime = run.RuntimeDirectory;
            run.Session.Restart(); // A second boot: its own boot directory and log, and a stop of only the first.
            return Task.CompletedTask;
        }));
        Assert.Equal(0, code);
        // Every owned server boot gets its test access, the restart's included; no runner option turns it off (#257 Q5).
        Assert.Equal(["devcommands", "confirmcheats", "devcommands", "confirmcheats"],
            server.Events.Where(e => e.StartsWith("devcommands") || e.StartsWith("confirmcheats")).Select(e => e.TrimEnd("0123456789".ToCharArray())));
        Assert.Same(host, seenHost); Assert.Equal(RunDirectory + "/runtime", seenRuntime);
        Assert.Equal(new[] { "enough free disk space for the copies", "take the server host's lock", "copy and verify pinned runtime on the server host", "copy and verify pinned world", "ship and verify the world copy on the server host",
                "copied runtime has the plan's server executable", "copied runtime is the pinned game build, loader and patchers",
                "CLI port is free on the server host", "open the loopback CLI tunnel to the server host", "start and verify owned dedicated fixture", "stop only owned server",
                "fetch the server host's world copy", "remove the server host's runtime copy, keeping what the run changed", "close the CLI tunnel", "release the server host's lock", "scan run logs" }, StepNames());

        // The copy is the host's install, into this run's own directory; the world copy is shipped there.
        var copy = Assert.Single(host.Runs, run => run.Script == "copy");
        Assert.Equal(Install, copy.Variables["source"]); Assert.Equal(RunDirectory + "/runtime", copy.Variables["dest"]);
        Assert.Equal(RunDirectory + "/world", Assert.Single(host.Runs, run => run.Script == "ship").Variables["dest"]);
        // Each boot starts with the plan's arguments on the host's paths, its own boot directory and the logs moved aside.
        var starts = host.Runs.Where(run => run.Script == "start").ToList();
        Assert.Equal(new[] { RunDirectory + "/boot-1", RunDirectory + "/boot-2" }, starts.Select(start => start.Variables["dir"]));
        Assert.Equal("BepInEx/LogOutput.log\ntoolkit-unity.log", starts[0].Variables["logs"]);
        var spec = FakeServerHost.Spec(starts[0].Variables["spec"]);
        Assert.Equal(new[] { "-batchmode", "-nographics", "-savedir", RunDirectory + "/world", "-port", "2456", "-logFile", RunDirectory + "/runtime/toolkit-unity.log" },
            spec.Where(line => line.Kind == "arg").Select(line => line.Text));
        Assert.Contains(("env", "DOORSTOP_TARGET_ASSEMBLY=" + RunDirectory + "/runtime/BepInEx/core/BepInEx.Preloader.dll"), spec);
        Assert.Contains(("prepend", "LD_LIBRARY_PATH=" + RunDirectory + "/runtime/linux64:" + RunDirectory + "/runtime/doorstop_libs"), spec);
        // The listening line is awaited in this boot's log on the host, from its start.
        Assert.All(host.Runs.Where(run => run.Script == "follow"), run => { Assert.Equal(RunDirectory + "/runtime/BepInEx/LogOutput.log", run.Variables["log"]); Assert.Equal("0", run.Variables["offset"]); });
        // Only the two processes this run started were stopped, each by its own identity, and each boot's evidence came back.
        Assert.Equal(new[] { ("1", "9001"), ("2", "9002") }, host.Stops);
        Assert.Equal(new[] { "launch1", "stop1", "launch2", "stop2" }, server.Events.Where(e => e.StartsWith("launch") || e.StartsWith("stop")));
        Assert.Contains("fake boot 1", File.ReadAllText(Path.Combine(Output, "boot-1", "game-0.log")));
        Assert.Contains("fake boot 2", File.ReadAllText(Path.Combine(Output, "boot-2", "game-0.log")));
        Assert.True(File.Exists(Path.Combine(Output, "host-world", "worlds_local", "Test.db")));
        // #194: the host's runtime copy went at teardown, under the lock; what the run changed in it came back.
        Assert.False(Directory.Exists(host.Local(RunDirectory + "/runtime")));
        Assert.True(Directory.Exists(host.Local(RunDirectory + "/world"))); Assert.True(Directory.Exists(host.Local(RunDirectory + "/boot-1")));
        Assert.True(File.Exists(Path.Combine(Output, "runtime-changes", "changes.json")));
        Assert.StartsWith("removed linux-box:" + RunDirectory + "/runtime", Result().GetProperty("Provenance").GetProperty("runtimeCopy").GetString());
        int retire = host.Runs.FindIndex(run => run.Script == "retire");
        Assert.True(retire > host.Runs.FindLastIndex(run => run.Script == "stop") && retire < host.Runs.Count);
        var process = JsonDocument.Parse(File.ReadAllText(Path.Combine(Output, "boot-1.process.json"))).RootElement;
        Assert.Equal(1, process.GetProperty("pid").GetInt32()); Assert.Equal("9001", process.GetProperty("startIdentity").GetString());
        Assert.Equal(RunDirectory + "/boot-1", process.GetProperty("bootDirectory").GetString());
        // The tunnel closed, the lock was taken and released once, by the same claimant.
        Assert.True(Assert.Single(host.Tunnels).Stopped);
        Assert.Equal(host.Claims, host.Releases); Assert.StartsWith("toolkit-smoke run-test", Assert.Single(host.Claims));
        var provenance = Result().GetProperty("Provenance");
        Assert.Equal("linux-box", provenance.GetProperty("serverHost").GetString());
        Assert.Equal(RunDirectory, provenance.GetProperty("hostRunDirectory").GetString());
        Assert.Equal("linux-box:" + Install, provenance.GetProperty("runtimeSource").GetString());
        Assert.Equal(InstallPins.Of(host.Local(Install)).Game, provenance.GetProperty("runtimeGameSha256").GetString());
        Assert.Equal("1,2", provenance.GetProperty("ownedPids").GetString());
        Assert.False(provenance.TryGetProperty("outcome", out _));
    }

    // The remote launch is the plan's launch arguments: a crossplay plan's server starts with -crossplay, any other without it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ACrossplayPlansServerStartsOnTheHostWithCrossplay(bool crossplay)
    {
        var server = NewServer(); var host = NewHost(server);
        var (plan, profile) = Write(host, crossplay: crossplay);
        Assert.Equal(0, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server)));
        var arguments = FakeServerHost.Spec(Assert.Single(host.Runs, run => run.Script == "start").Variables["spec"])
            .Where(line => line.Kind == "arg").Select(line => line.Text).ToList();
        if (crossplay) Assert.Equal("-crossplay", arguments.Last()); else Assert.DoesNotContain("-crossplay", arguments);
        Assert.Equal(crossplay ? "true" : "false", Result().GetProperty("Provenance").GetProperty("crossplay").GetString());
    }

    [Fact] public async Task ACrossplayRunWhoseServerHadToBeKilledFailsAndSaysWhy()
    {
        var server = NewServer(); var host = NewHost(server);
        host.IgnoreQuit = true;
        var (plan, profile) = Write(host, crossplay: true);
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server)));
        var step = Step("every boot quit cleanly and retired its crossplay lobby");
        Assert.False(step.GetProperty("Passed").GetBoolean());
        Assert.Contains("boot-1 was killed", step.GetProperty("Error").GetString());
        Assert.Contains("not a clean crossplay run", step.GetProperty("Error").GetString());
        Assert.StartsWith("boot-1 killed", Result().GetProperty("Provenance").GetProperty("serverStops").GetString());
        Assert.Equal("120", Assert.Single(host.Runs, run => run.Script == "stop").Variables["quit"]);
    }

    [Fact] public async Task ACleanCrossplayRunRecordsEachBootsRetiredLobby()
    {
        var server = NewServer(); var host = NewHost(server);
        var (plan, profile) = Write(host, crossplay: true);
        Assert.Equal(0, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server)));
        Assert.True(Step("every boot quit cleanly and retired its crossplay lobby").GetProperty("Passed").GetBoolean());
        var provenance = Result().GetProperty("Provenance");
        Assert.StartsWith("boot-1 clean", provenance.GetProperty("serverStops").GetString());
        Assert.Contains("lobby lobby-1; retired yes; PlayFab confirmation logged", provenance.GetProperty("crossplayLobbies").GetString());
    }

    // A crossplay plan checks that the host's runtime copy can load libparty.so before anything starts, in validate too; a host
    // missing libpulse fails there with the packages to install, and no server starts. A plan without crossplay is not checked.
    [Theory]
    [InlineData("run")]
    [InlineData("validate")]
    public async Task ACrossplayHostMissingLibrariesIsRefusedBeforeAnythingStarts(string mode)
    {
        var server = NewServer(); var host = NewHost(server);
        host.PartyReply = "VT-LDD \tlibpulse.so.0 => not found\nVT-LDD \tlibc.so.6 => /lib/libc.so.6 (0x1)\nVT-PARTY checked valheim_server_Data/Plugins/libparty.so 1\n";
        var (plan, profile) = Write(host, crossplay: true);
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), [mode, plan, Output], Options(host, server)));
        var step = Step("the server host can load crossplay's libraries");
        Assert.False(step.GetProperty("Passed").GetBoolean());
        Assert.Contains("libpulse.so.0 (package libpulse0) is missing", step.GetProperty("Error").GetString());
        Assert.Equal(Runs + "/" + RunId + "/runtime", Assert.Single(host.Runs, run => run.Script == "party").Variables["runtime"]);
        Assert.DoesNotContain(host.Runs, run => run.Script == "start");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OnlyACrossplayPlanChecksTheLibrariesAndRecordsWhichLoaded(bool crossplay)
    {
        var server = NewServer(); var host = NewHost(server);
        var (plan, profile) = Write(host, crossplay: crossplay);
        Assert.Equal(0, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server)));
        var provenance = Result().GetProperty("Provenance");
        if (crossplay)
        {
            Assert.True(Step("the server host can load crossplay's libraries").GetProperty("Passed").GetBoolean());
            Assert.Equal("valheim_server_Data/Plugins/libparty.so loads on linux-box", provenance.GetProperty("crossplayLibraries").GetString());
            Assert.Equal("1", Assert.Single(host.Runs, run => run.Script == "start").Variables["crossplay"]);
        }
        else
        {
            Assert.DoesNotContain("the server host can load crossplay's libraries", StepNames());
            Assert.False(provenance.TryGetProperty("crossplayLibraries", out _));
            Assert.DoesNotContain(host.Runs, run => run.Script == "party");
        }
    }

    // The game reads its arguments lowercased: -Port names the game port, and one that differs from the profile's is refused.
    [Fact] public async Task AGamePortInAnyCaseMustBeTheProfilesGamePort()
    {
        var host = NewHost();
        var (plan, profile) = Write(host, portOption: "-Port", gamePort: "2457");
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, null)));
        Assert.False(Directory.Exists(Output)); Assert.Empty(host.Runs);
    }

    [Fact] public async Task ValidateOnAHostCopiesAndVerifiesThereAndStartsNothing()
    {
        var host = NewHost();
        var (plan, profile) = Write(host);
        Assert.Equal(0, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["validate", plan, Output], Options(host, null)));
        Assert.Contains("prepared only; no game launched", StepNames());
        Assert.DoesNotContain(host.Runs, run => run.Script is "port" or "tunnel" or "start" or "fetch");
        Assert.Equal(host.Claims, host.Releases); Assert.Single(host.Claims);
        // Nothing was launched, so the host copy just goes (#194), with nothing to keep or fetch.
        Assert.False(Directory.Exists(host.Local(RunDirectory + "/runtime")));
        Assert.EndsWith("nothing was launched", Result().GetProperty("Provenance").GetProperty("runtimeCopy").GetString());
    }

    [Fact] public async Task ARuntimeCopyThatDiffersOnTheHostFailsBeforeAnythingStarts()
    {
        var host = NewHost(NewServer());
        var (plan, profile) = Write(host);
        host.AfterCopy = runtime => File.WriteAllText(Path.Combine(runtime, "BepInEx", "core", "BepInEx.dll"), "another bepinex");
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, NewServer())));
        var copy = Step("copy and verify pinned runtime on the server host");
        Assert.False(copy.GetProperty("Passed").GetBoolean());
        Assert.Contains("different hash: BepInEx/core/BepInEx.dll", copy.GetProperty("Error").GetString());
        Assert.DoesNotContain(host.Runs, run => run.Script is "start" or "tunnel");
        Assert.Equal(host.Claims, host.Releases);
    }

    [Fact] public async Task AnotherRunsLockRefusesTheRunBeforeTheHostIsTouched()
    {
        var host = NewHost(NewServer()); host.HeldBy = "other-runner run-x [0123]";
        var (plan, profile) = Write(host);
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, NewServer())));
        var step = Step("take the server host's lock");
        Assert.False(step.GetProperty("Passed").GetBoolean()); Assert.Contains("other-runner run-x [0123]", step.GetProperty("Error").GetString());
        Assert.Empty(host.Runs);
        Assert.Empty(host.Releases);
        Assert.DoesNotContain("release the server host's lock", StepNames());
    }

    [Fact] public async Task ALostStartReplyIsAnUnknownOutcomeAndKeepsTheLock()
    {
        var server = NewServer(); var host = NewHost(server);
        host.Failures["start"] = FakeServerHost.TransportFailure;
        var (plan, profile) = Write(host);
        Assert.Equal(3, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server)));
        var result = Result();
        Assert.False(result.GetProperty("Passed").GetBoolean());
        Assert.StartsWith("unknown: ", result.GetProperty("Provenance").GetProperty("outcome").GetString());
        // A server may be running there that no session knows: nothing is stopped by guesswork, and the host stays locked.
        Assert.Empty(host.Stops);
        Assert.Empty(host.Releases);
        Assert.Contains("may still run", Step("release the server host's lock").GetProperty("Error").GetString());
        Assert.True(Assert.Single(host.Tunnels).Stopped);
        // ...and the copy it may run from stays, named, never removed (#257).
        Assert.True(Directory.Exists(host.Local(RunDirectory + "/runtime")));
        Assert.DoesNotContain("retire", host.Scripts);
        Assert.Contains("may still run", result.GetProperty("Provenance").GetProperty("runtimeCopy").GetString());
    }

    [Fact] public async Task AnUnprovenStopIsAnUnknownOutcomeAndKeepsTheLock()
    {
        var server = NewServer(); var host = NewHost(server);
        host.Failures["stop"] = new HostResult(HostOutcome.Unknown, null, "", "", TimeSpan.FromSeconds(45), true);
        var (plan, profile) = Write(host);
        Assert.Equal(3, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server)));
        Assert.False(Step("stop only owned server").GetProperty("Passed").GetBoolean());
        Assert.Empty(host.Releases);
        Assert.DoesNotContain("fetch the server host's world copy", StepNames());
        Assert.StartsWith("unknown: ", Result().GetProperty("Provenance").GetProperty("outcome").GetString());
    }

    [Fact] public async Task AFailingScenarioIsAFailureEvenWhenTeardownIsUnknown()
    {
        var server = NewServer(); var host = NewHost(server);
        host.Failures["stop"] = FakeServerHost.TransportFailure;
        var (plan, profile) = Write(host);
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server, _ => throw new InvalidOperationException("marker missing"))));
        Assert.False(Result().GetProperty("Provenance").TryGetProperty("outcome", out _));
    }

    [Fact] public async Task ATunnelThatCannotOpenFailsTheRunBeforeTheServerStarts()
    {
        var server = NewServer(); var host = NewHost(server);
        host.TunnelFailure = new WaitFailedException("the forward to listen on 127.0.0.1:15577", "ssh exited with code 255", TimeSpan.FromSeconds(1), "bind [127.0.0.1]:15577: Address already in use");
        var (plan, profile) = Write(host);
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server)));
        Assert.False(Step("open the loopback CLI tunnel to the server host").GetProperty("Passed").GetBoolean());
        Assert.DoesNotContain(host.Runs, run => run.Script == "start");
        Assert.Empty(server.Events);
        Assert.Equal(host.Claims, host.Releases);
    }

    [Fact] public async Task ABusyCliPortOnTheHostIsRefusedBeforeTheTunnel()
    {
        var host = NewHost(NewServer()); host.PortBusy = true;
        var (plan, profile) = Write(host);
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, NewServer())));
        Assert.Contains("already listens on port 5577", Step("CLI port is free on the server host").GetProperty("Error").GetString());
        Assert.DoesNotContain(host.Runs, run => run.Script is "tunnel" or "start");
    }

    [Fact] public async Task AProfileThatDoesNotFitThePlanIsRefusedBeforeAnythingIsWritten()
    {
        var host = NewHost();
        var (plan, profile) = Write(host, planPort: 5590);
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, null)));
        Assert.False(Directory.Exists(Output)); Assert.Empty(host.Runs);
    }

    // --inventory: a standalone run places its one actor, the dedicated server, on the first server environment in inventory
    // order that can run the plan, records why, and refuses before anything is written when none can. --profile is gone.
    [Fact] public async Task AnInventoryPlacesTheServerOnTheFirstEnvironmentThatFitsThePlan()
    {
        var server = NewServer(); var host = NewHost(server);
        var (plan, _) = Write(host);
        string inventory = Path.Combine(_root, "environments.json");
        object Environment(string name, int cliPort) => new { name, host = "linux-box", roles = new[] { "server" }, install = Install, runtime = Runs, cliPort, gamePort = 2456 };
        void Inventory(params object[] environments) => File.WriteAllText(inventory, JsonSerializer.Serialize(new
        {
            hosts = new Dictionary<string, object> { ["linux-box"] = new { kind = "ssh", platform = "linux", shell = "bash", destination = "tester@linux-box.example", @lock = "/var/tmp/vt/lock" } },
            environments,
        }));
        // Skipped in order: a Windows host for this Linux runtime, a local host of another platform than this machine's, an
        // environment whose loader package only a campaign applies, and a CLI port that is not the plan's.
        string other = HostProfile.CurrentPlatform == "linux" ? "windows" : "linux";
        File.WriteAllText(inventory, JsonSerializer.Serialize(new
        {
            hosts = new Dictionary<string, object>
            {
                ["linux-box"] = new { kind = "ssh", platform = "linux", shell = "bash", destination = "tester@linux-box.example", @lock = "/var/tmp/vt/lock" },
                ["windows-box"] = new { kind = "ssh", platform = "windows", shell = "powershell", destination = "tester@windows-box.example", @lock = @"C:\vt\lock" },
                ["elsewhere"] = new { kind = "local", platform = other, shell = other == "windows" ? "powershell" : "bash", @lock = other == "windows" ? @"C:\vt\lock" : "/var/tmp/vt/lock" },
            },
            environments = new object[]
            {
                new { name = "windows", host = "windows-box", roles = new[] { "server" }, install = @"C:\valheim\server", runtime = @"C:\vt\runs", cliPort = 5577, gamePort = 2456 },
                new { name = "local-other", host = "elsewhere", roles = new[] { "server" }, install = other == "windows" ? @"C:\valheim\server" : Install,
                      runtime = other == "windows" ? @"C:\vt\runs" : Runs, cliPort = 5577, gamePort = 2456 },
                new { name = "packaged", host = "linux-box", roles = new[] { "server" }, install = Install, runtime = Runs, cliPort = 5577, gamePort = 2456, loaderPackage = "/srv/loader.json" },
                Environment("other-port", 5590), Environment("fits", 5577),
            },
        }));
        Assert.Equal(0, await PinnedServerRun.MainAsync(["--inventory", inventory, "run", plan, Output], Options(host, server)));
        var provenance = Result().GetProperty("Provenance");
        string placed = provenance.GetProperty("serverEnvironment").GetString()!;
        Assert.StartsWith("fits: first server recipe that can run the plan after windows: the plan's runtime is a linux server, but the host is windows.", placed);
        Assert.Contains($"local-other: it is a local {other} host, but this machine is {HostProfile.CurrentPlatform}.", placed);
        Assert.Contains("packaged: it names a loaderPackage, which only a campaign's preparation applies", placed);
        Assert.Contains("other-port: the plan's ValheimCLI port 5577 is not its cliPort 5590", placed);
        Assert.Equal(FileHash.Sha256(inventory), provenance.GetProperty("inventorySha256").GetString());
        Assert.Contains("start", host.Scripts);

        Inventory(Environment("other-port", 5590));
        string refused = Path.Combine(_root, "refused");
        int scripts = host.Runs.Count;
        Assert.Equal(1, await PinnedServerRun.MainAsync(["--inventory", inventory, "run", plan, refused], Options(host, server)));
        Assert.False(Directory.Exists(refused)); Assert.Equal(scripts, host.Runs.Count);
        // The removed option is refused as bad usage, never read as a file.
        Assert.Equal(2, await PinnedServerRun.MainAsync(["--profile", inventory, "run", plan, refused], Options(host, server)));
        Assert.False(Directory.Exists(refused));
    }

    [Fact] public async Task AProfileClientStartsInItsHostsDesktopSessionAndStopsOnlyThatClient()
    {
        var server = NewServer(); var host = NewHost(server);
        var clientHost = new FakeServerHost("linux-gpu", Path.Combine(_root, "gpu"), tunnelPort: 15578);
        string clientInstall = clientHost.Local("/home/tester/valheim");
        Directory.CreateDirectory(Path.Combine(clientInstall, "BepInEx", "core"));
        FakeInstalls.Client(clientInstall);
        File.WriteAllText(Path.Combine(clientInstall, ClientLaunch.LinuxExecutable), "client");
        FakeInstalls.LinuxLoader(clientInstall);
        File.WriteAllText(Path.Combine(clientInstall, "BepInEx", "LogOutput.log"), "an earlier run's log\n");
        var (plan, profile) = Write(host, withClient: true);
        var clientTransport = new ScriptedTransport();
        var client = new ClientRunPlan { Mode = "owned", Install = _root, Port = 5578, Pinning = "none", StartSeconds = 30, LaunchArguments = ["+connect", "linux-box:2456"] };
        int? pid = null;
        int code = await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server, run =>
        {
            // A scenario reaches a campaign client's host by its name.
            Assert.Equal(["player"], run.CampaignClients);
            Assert.Same(clientHost, run.ClientHost("player"));
            using (var session = run.OpenClient(client)) pid = session.ProcessId;
            return Task.CompletedTask;
        }, clientHost, clientTransport));
        Assert.Equal(0, code);
        Assert.Equal(77, pid);
        // The launch used the profile's install and a launch directory in this run's directory on the client's host.
        var start = Assert.Single(clientHost.Runs, run => run.Script == "client-start");
        Assert.Equal("/home/tester/valheim", start.Variables["install"]);
        Assert.Equal("/home/tester/runs/" + RunId + "/client-1", start.Variables["dir"]);
        Assert.Contains(FakeServerHost.Spec(start.Variables["spec"]), line => line.Kind == "arg" && line.Text == "+connect");
        // The earlier log moved aside first, the new one was awaited from its start; only that client was stopped.
        Assert.Equal("/home/tester/runs/" + RunId + "/client-1.previous-LogOutput.log", Assert.Single(clientHost.Runs, run => run.Script == "move-aside").Variables["to"]);
        Assert.Equal(2, clientHost.Runs.Count(run => run.Script == "follow")); // fresh BepInEx line, then ValheimCLI listening
        Assert.All(clientHost.Runs.Where(run => run.Script == "follow"), run => Assert.Equal("0", run.Variables["offset"]));
        Assert.Equal(new[] { ("77", "555") }, clientHost.Stops);
        Assert.Contains("listening on 127.0.0.1:5578", File.ReadAllText(Path.Combine(Output, "client-1", "game-0.log")));
        Assert.True(Assert.Single(clientHost.Tunnels).Stopped);
        // The client's host was locked for the run and released at teardown; its logs were scanned with the server's.
        Assert.Equal(clientHost.Claims, clientHost.Releases); Assert.Single(clientHost.Claims);
        Assert.Contains("release client host linux-gpu's lock", StepNames());
        Assert.Contains(Result().GetProperty("Logs").EnumerateArray(), log => log.GetProperty("Role").GetString() == "client-1 BepInEx log");
    }

    // #248's remaining half: a remote Linux client's loader is checked before launch, as a Windows client's is. Through the run:
    // the client's host is never asked to start a game without its Doorstop library.
    [Fact] public async Task ARemoteLinuxClientWithoutItsDoorstopLibraryIsRefusedBeforeLaunch()
    {
        var server = NewServer(); var host = NewHost(server);
        var clientHost = new FakeServerHost("linux-gpu", Path.Combine(_root, "gpu"), tunnelPort: 15578);
        string clientInstall = clientHost.Local("/home/tester/valheim");
        FakeInstalls.Client(clientInstall);
        File.WriteAllText(Path.Combine(clientInstall, ClientLaunch.LinuxExecutable), "client");
        var (plan, profile) = Write(host, withClient: true);
        var client = new ClientRunPlan { Mode = "owned", Install = _root, Port = 5578, Pinning = "none", StartSeconds = 30, LaunchArguments = ["+connect", "linux-box:2456"] };
        Exception? refused = null;
        int code = await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server, run =>
        {
            refused = Record.Exception(() => run.OpenClient(client));
            return Task.CompletedTask;
        }, clientHost, new ScriptedTransport()));
        Assert.Contains("doorstop_libs/libdoorstop_x64.so", Assert.IsType<FileNotFoundException>(refused).Message);
        Assert.DoesNotContain(clientHost.Runs, run => run.Script == "client-start");
        Assert.Equal(0, code); // The scenario recorded the refusal; nothing else in the run failed.
    }

    [Fact] public async Task ARemoteWindowsClientRefusesMixedLoaderAndInheritedStandingPinsBeforeLaunch()
    {
        var host = new FakeServerHost("windows-client", Path.Combine(_root, "remote-client"));
        const string install = "/client/valheim";
        string local = host.Local(install);
        Directory.CreateDirectory(Path.Combine(local, "BepInEx", "config"));
        FakeInstalls.Client(local);
        File.WriteAllText(Path.Combine(local, "winhttp.dll"), "MZ fake"); // a proxy that shows no Doorstop version
        File.WriteAllText(Path.Combine(local, "doorstop_config.ini"), "[General]\nenabled=true\ntarget_assembly=BepInEx\\core\\BepInEx.Preloader.dll\n");
        var unrecognised = await Assert.ThrowsAsync<DoorstopPairingException>(() => HostClientPreflight.CheckAsync(host, install, ClientPlatform.Windows,
            new ClientRunPlan { Mode = "owned", Pinning = "none" }, TimeSpan.FromSeconds(5), default));
        Assert.Contains("not a Doorstop proxy this check recognises", unrecognised.Message);
        File.WriteAllText(Path.Combine(local, "winhttp.dll"), "MZ target_assembly"); // Doorstop 4 signature
        File.WriteAllText(Path.Combine(local, "doorstop_config.ini"), "[UnityDoorstop]\nenabled=true\ntargetAssembly=BepInEx\\core\\BepInEx.Preloader.dll\n");
        var plan = new ClientRunPlan { Mode = "owned", Pinning = "none", Pins = new() { ["valheimCLI.valheimCLI"] = new string('a', 32) } };
        var mismatch = await Assert.ThrowsAsync<DoorstopPairingException>(() =>
            HostClientPreflight.CheckAsync(host, install, ClientPlatform.Windows, plan, TimeSpan.FromSeconds(5), default));
        DoorstopMixPathsTests.AssertRefusal(mismatch, "client install on windows-client",
            "is Doorstop 4, which reads only [General] in doorstop_config.ini, but that file is written for Doorstop 3 ([UnityDoorstop])");
        Assert.DoesNotContain(host.Runs, run => run.Script == "client-start");

        File.WriteAllText(Path.Combine(local, "doorstop_config.ini"), "[General]\nenabled=true\ntarget_assembly=BepInEx\\core\\BepInEx.Preloader.dll\n");
        string config = Path.Combine(local, "BepInEx", "config", OwnedClientPreflight.CliConfig);
        File.WriteAllText(config, "[Expectations]\nFile = C:\\Users\\Public\\station\\expect-client.txt\nStrict = true\n");
        var global = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HostClientPreflight.CheckAsync(host, install, ClientPlatform.Windows, plan, TimeSpan.FromSeconds(5), default));
        Assert.Contains("host-global standing file", global.Message);

        File.WriteAllText(config, "[Expectations]\nFile = expect.txt\nStrict = true\n");
        string standing = Path.Combine(local, "BepInEx", "config", "expect.txt");
        File.WriteAllText(standing, $"valheimCLI.valheimCLI={new string('a', 32)}\ncom.bepis.bepinex.scriptengine=any\nworld=any\n");
        var unrelated = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HostClientPreflight.CheckAsync(host, install, ClientPlatform.Windows, plan, TimeSpan.FromSeconds(5), default));
        Assert.Contains("plugin the plan does not pin", unrelated.Message);
        File.WriteAllText(standing, $"valheimCLI.valheimCLI={new string('a', 32)}\nworld=any\n");
        await HostClientPreflight.CheckAsync(host, install, ClientPlatform.Windows, plan, TimeSpan.FromSeconds(5), default);
    }

    [Fact] public async Task WindowsPowerShellReadsTheRemoteLoaderFilesForPreflight()
    {
        if (!OperatingSystem.IsWindows()) return;
        string install = Path.Combine(_root, "windows-client");
        Directory.CreateDirectory(install);
        FakeInstalls.Client(install); // The preloader and core, found by the real existence script.
        File.WriteAllText(Path.Combine(install, "winhttp.dll"), "MZ target_assembly");
        string config = Path.Combine(install, "doorstop_config.ini");
        File.WriteAllText(config, "[General]\nenabled=true\ntarget_assembly=BepInEx\\core\\BepInEx.Preloader.dll\n");
        var host = new LocalGameHost("windows-client", HostShell.WindowsPowerShell);
        var plan = new ClientRunPlan { Mode = "owned", Pinning = "none" };
        await HostClientPreflight.CheckAsync(host, install, ClientPlatform.Windows, plan, TimeSpan.FromSeconds(30), default);
        File.WriteAllText(config, "[UnityDoorstop]\nenabled=true\ntargetAssembly=BepInEx\\core\\BepInEx.Preloader.dll\n");
        var error = await Assert.ThrowsAsync<DoorstopPairingException>(() =>
            HostClientPreflight.CheckAsync(host, install, ClientPlatform.Windows, plan, TimeSpan.FromSeconds(30), default));
        Assert.Contains("Doorstop 4", error.Message);
    }

    // The bash existence script for real: a Linux client's loader files are found, and a missing Doorstop library is named.
    [Fact] public async Task BashChecksTheRemoteLoaderFilesForPreflight()
    {
        if (OperatingSystem.IsWindows()) return;
        string install = Path.Combine(_root, "linux client"); // A space in the path, as a host install may have.
        FakeInstalls.Client(install);
        var host = new LocalGameHost("linux-client", HostShell.Bash);
        var plan = new ClientRunPlan { Mode = "owned", Pinning = "none" };
        Assert.Contains("doorstop_libs/libdoorstop_x64.so", (await Assert.ThrowsAsync<FileNotFoundException>(() =>
            HostClientPreflight.CheckAsync(host, install, ClientPlatform.Linux, plan, TimeSpan.FromSeconds(30), default))).Message);
        FakeInstalls.LinuxLoader(install);
        await HostClientPreflight.CheckAsync(host, install, ClientPlatform.Linux, plan, TimeSpan.FromSeconds(30), default);
    }

    // A client that started but never reached its menu (here its pins do not hold) is stopped; its fetched logs are still scanned.
    [Fact] public async Task AProfileClientWhoseStartupFailsStillHasItsLogsScannedAndListed()
    {
        var server = NewServer(); var host = NewHost(server);
        var clientHost = new FakeServerHost("linux-gpu", Path.Combine(_root, "gpu"), tunnelPort: 15578);
        string clientInstall = clientHost.Local("/home/tester/valheim");
        Directory.CreateDirectory(Path.Combine(clientInstall, "BepInEx", "core"));
        FakeInstalls.Client(clientInstall);
        File.WriteAllText(Path.Combine(clientInstall, ClientLaunch.LinuxExecutable), "client");
        FakeInstalls.LinuxLoader(clientInstall);
        var (plan, profile) = Write(host, withClient: true);
        // Strict, so the menu pins are checked; the scripted client does not hold them.
        var client = new ClientRunPlan
        {
            Mode = "owned", Install = _root, Port = 5578, StartSeconds = 30, Pins = new() { ["valheimCLI.valheimCLI"] = new string('a', 32) }, InstallPins = InstallPins.Of(clientInstall),
        };
        Exception? failed = null;
        Assert.Equal(0, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server, run =>
        {
            failed = Record.Exception(() => run.OpenClient(client));
            return Task.CompletedTask;
        }, clientHost, new ScriptedTransport { PinsHold = false })));
        Assert.IsType<InvalidOperationException>(failed);
        Assert.Single(clientHost.Stops);
        Assert.Contains("listening on 127.0.0.1:5578", File.ReadAllText(Path.Combine(Output, "client-1", "game-0.log")));
        var roles = Result().GetProperty("Logs").EnumerateArray().Select(log => log.GetProperty("Role").GetString()).ToList();
        Assert.Contains("client-1 BepInEx log", roles);
        Assert.Contains("client-1 Player.log", roles);
    }

    [Fact] public async Task AProfileClientWithoutAFreshBepInExLogFailsAtTheLoaderDeadline()
    {
        var server = NewServer(); var host = NewHost(server);
        var clientHost = new FakeServerHost("linux-gpu", Path.Combine(_root, "gpu"), tunnelPort: 15578) { ClientWritesBepInExLog = false };
        string clientInstall = clientHost.Local("/home/tester/valheim");
        Directory.CreateDirectory(Path.Combine(clientInstall, "BepInEx", "core"));
        FakeInstalls.Client(clientInstall);
        File.WriteAllText(Path.Combine(clientInstall, ClientLaunch.LinuxExecutable), "client");
        FakeInstalls.LinuxLoader(clientInstall);
        var (plan, profile) = Write(host, withClient: true);
        var client = new ClientRunPlan { Mode = "owned", Install = _root, Port = 5578, Pinning = "none", StartSeconds = 300, BepInExSeconds = 30 };
        Exception? failure = null;
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server, run =>
        {
            failure = Record.Exception(() => run.OpenClient(client));
            return Task.CompletedTask;
        }, clientHost, new ScriptedTransport())));
        Assert.Contains("BepInEx wrote no fresh log line", failure?.ToString());
        Assert.Contains("within 30s", failure?.ToString());
        Assert.Single(clientHost.Stops);
        Assert.DoesNotContain(clientHost.Runs, run => run.Script == "follow" && run.Variables["offset"] != "0");
    }

    [Fact] public async Task AnArm64ProfileClientIsRefusedBeforeItsHostIsLockedOrAnythingStarts()
    {
        var server = NewServer(); var host = NewHost(server);
        var clientHost = new FakeServerHost("linux-gpu", Path.Combine(_root, "gpu"), tunnelPort: 15578);
        var (plan, profile) = Write(host, withClient: true);
        var client = new ClientRunPlan { Mode = "owned", Install = _root, Port = 5578, Pinning = "none", StartSeconds = 30, Architecture = "arm64" };
        Exception? refused = null;
        Assert.Equal(0, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server, run =>
        {
            refused = Record.Exception(() => run.OpenClient(client));
            return Task.CompletedTask;
        }, clientHost, new ScriptedTransport())));
        Assert.Contains("architecture arm64 is for a macOS client launched locally in this runner's GUI session", Assert.IsType<ArgumentException>(refused).Message);
        Assert.Empty(clientHost.Claims); Assert.Empty(clientHost.Runs); Assert.Empty(clientHost.Tunnels);
    }
}

// The retire script itself, in bash on this machine (Linux only: a hosted server's host runs Linux, with GNU stat and du).
public sealed class HostedRetireScriptTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("hosted-retire-").FullName;
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private static string B64(string text) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));

    [Fact] public async Task ItKeepsListedFilesWithinTheLimitsAndRemovesOnlyTheRunsRuntime()
    {
        if (!OperatingSystem.IsLinux()) return;
        string run = Path.Combine(_root, "runs", "run-x"), runtime = Path.Combine(run, "runtime"), keep = Path.Combine(run, "runtime-changes");
        Directory.CreateDirectory(Path.Combine(runtime, "sub"));
        File.WriteAllText(Path.Combine(runtime, "a.txt"), "kept");
        File.WriteAllText(Path.Combine(runtime, "sub", "b.cfg"), "kept too");
        File.WriteAllBytes(Path.Combine(runtime, "big.bin"), new byte[2000]);
        File.WriteAllText(Path.Combine(run, "outside.txt"), "never copied through ..");
        string elsewhere = Path.Combine(_root, "elsewhere"); Directory.CreateDirectory(elsewhere); File.WriteAllText(Path.Combine(elsewhere, "secret.txt"), "outside the copy");
        Directory.CreateSymbolicLink(Path.Combine(runtime, "linked"), elsewhere);
        var host = new LocalGameHost("local-bash", HostShell.Bash);
        var variables = new Dictionary<string, string>
        {
            ["runtime"] = runtime, ["keep"] = keep, ["run"] = "run-x", ["perfile"] = "1000", ["total"] = "5000",
            ["files"] = string.Join('\n', new[] { "a.txt", "sub/b.cfg", "big.bin", "../outside.txt", "missing.txt", "linked/secret.txt" }.Select(B64)),
        };
        var result = (await host.RunAsync(HostedRunScripts.Retire, variables, GameHostChecks.Generous)).EnsureSuccess("retire");
        Assert.Contains("VT-RETIRED ", result.Stdout);
        Assert.False(Directory.Exists(runtime));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(keep, "a.txt")));
        Assert.Equal("kept too", File.ReadAllText(Path.Combine(keep, "sub", "b.cfg")));
        Assert.False(File.Exists(Path.Combine(keep, "big.bin")));
        Assert.Contains($"VT-NOTKEPT {B64("big.bin")} 2000", result.Stdout);
        Assert.Contains($"VT-NOTKEPT {B64("../outside.txt")} -1", result.Stdout);
        Assert.Contains($"VT-NOTKEPT {B64("missing.txt")} -1", result.Stdout);
        Assert.Contains($"VT-NOTKEPT {B64("linked/secret.txt")} -1", result.Stdout); // through a linked directory: not the copy's
        Assert.True(File.Exists(Path.Combine(elsewhere, "secret.txt"))); // removing the copy removed the link only
        Assert.True(File.Exists(Path.Combine(run, "outside.txt")));

        // Anything but <run>/runtime is refused and left alone.
        string other = Path.Combine(run, "world"); Directory.CreateDirectory(other); File.WriteAllText(Path.Combine(other, "w.db"), "a save");
        var refused = await host.RunAsync(HostedRunScripts.Retire, new Dictionary<string, string>(variables) { ["runtime"] = other, ["files"] = "" }, GameHostChecks.Generous);
        Assert.Equal(3, refused.ExitCode);
        Assert.True(File.Exists(Path.Combine(other, "w.db")));
        var wrongRun = await host.RunAsync(HostedRunScripts.Retire, new Dictionary<string, string>(variables) { ["run"] = "another-run", ["files"] = "" }, GameHostChecks.Generous);
        Assert.Equal(3, wrongRun.ExitCode);

        // The keep destination is as constrained as the directory being removed.
        string second = Path.Combine(run, "runtime"); Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(second, "a.txt"), "kept");
        string unrelated = Path.Combine(_root, "unrelated"); Directory.CreateDirectory(unrelated);
        var wrongKeep = await host.RunAsync(HostedRunScripts.Retire,
            new Dictionary<string, string>(variables) { ["keep"] = unrelated, ["files"] = B64("a.txt") }, GameHostChecks.Generous);
        Assert.Equal(3, wrongKeep.ExitCode);
        Assert.True(File.Exists(Path.Combine(second, "a.txt")));
        Assert.False(File.Exists(Path.Combine(unrelated, "a.txt")));

        var existingKeep = await host.RunAsync(HostedRunScripts.Retire,
            new Dictionary<string, string>(variables) { ["files"] = B64("a.txt") }, GameHostChecks.Generous);
        Assert.Equal(3, existingKeep.ExitCode);
        Assert.True(File.Exists(Path.Combine(second, "a.txt")));
        Directory.Delete(keep, recursive: true);
        Directory.CreateSymbolicLink(keep, unrelated);
        var linkedKeep = await host.RunAsync(HostedRunScripts.Retire,
            new Dictionary<string, string>(variables) { ["files"] = B64("a.txt") }, GameHostChecks.Generous);
        Assert.Equal(3, linkedKeep.ExitCode);
        Assert.True(File.Exists(Path.Combine(second, "a.txt")));
        Assert.False(File.Exists(Path.Combine(unrelated, "a.txt")));
    }
}
