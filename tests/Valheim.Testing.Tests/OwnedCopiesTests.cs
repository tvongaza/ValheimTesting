using System.Text.Json;
using Valheim.Testing.Game;
using Xunit;
using Valheim.Testing.GameSessions;

// #194: finding the copies runs left behind, and removing only the chosen ones, never one a process uses.
public sealed class OwnedCopiesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "owned-copies-" + Guid.NewGuid().ToString("N"));
    private string Runs => Path.Combine(_root, "runs");
    public void Dispose() { OwnedCopies.ProcessesOverride = null; if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private string Source(string name, params string[] files)
    {
        string source = Path.Combine(_root, "sources", name);
        foreach (string file in files) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(source, file))!); File.WriteAllText(Path.Combine(source, file), name + ":" + file); }
        return source;
    }
    private static void Result(string directory, bool passed) { Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new { Passed = passed })); }
    private string Copy(string source, string into) { Directory.CreateDirectory(into); var copy = WorldFixture.Copy(source, into, WorldFixture.Manifest(source)); copy.Preserve = true; copy.Dispose(); return copy.DirectoryPath; }

    // A pinned run's output (copies beside result.json), a NativeSmoke output (staged copies beside evidence/), and things that are not copies.
    private (string Server, string Client, string World, string Staged) Layout()
    {
        string server = Source("server", GameLaunch.ServerWindowsExecutable, "valheim_server_Data/Managed/assembly_valheim.dll");
        string client = Source("client", GameLaunch.ClientWindowsExecutable, "valheim_Data/Managed/assembly_valheim.dll");
        string world = Source("world", "worlds_local/Test.db");
        string first = Path.Combine(Runs, "first"); Result(first, passed: true);
        string smoke = Path.Combine(Runs, "smoke"); Result(Path.Combine(smoke, "evidence"), passed: false);
        var copies = (Copy(server, first), Copy(client, Path.Combine(Runs, "orphan")), Copy(world, first), Copy(server, Path.Combine(smoke, "staged-runtime")));
        Directory.CreateDirectory(Path.Combine(Runs, "valheim-test-" + new string('a', 32))); // the name without the manifest: not a copy
        Directory.CreateDirectory(Path.Combine(Runs, "unrelated", "game")); File.WriteAllText(Path.Combine(Runs, "unrelated", "game", GameLaunch.ServerWindowsExecutable), "a live install");
        return copies;
    }

    [Fact] public void FindListsOnlyCopiesWithTheirKindRunAndUseAndChangesNothing()
    {
        var (server, client, world, staged) = Layout();
        OwnedCopies.ProcessesOverride = () => [(42, Path.Combine(client, GameLaunch.ClientWindowsExecutable)), (7, Path.Combine(_root, "elsewhere.exe"))];
        var copies = OwnedCopies.Find(Runs);
        Assert.Equal(new[] { client, server, staged, world }.Order(StringComparer.Ordinal), copies.Select(copy => copy.Path).Order(StringComparer.Ordinal));
        var byPath = copies.ToDictionary(copy => copy.Path);
        Assert.Equal((OwnedCopyKind.ServerRuntime, true), (byPath[server].Kind, byPath[server].Passed!.Value));
        Assert.Equal((OwnedCopyKind.ServerRuntime, false), (byPath[staged].Kind, byPath[staged].Passed!.Value)); // its run's evidence/ beside staged-runtime/
        Assert.Equal(OwnedCopyKind.World, byPath[world].Kind);
        Assert.Equal((OwnedCopyKind.ClientRuntime, (bool?)null, (string?)null), (byPath[client].Kind, byPath[client].Passed, byPath[client].Result));
        Assert.Equal(new[] { 42 }, byPath[client].InUseBy); Assert.False(byPath[server].InUse);
        Assert.All(copies, copy => Assert.True(Directory.Exists(copy.Path)));
        Assert.True(copies[0].Bytes >= copies[^1].Bytes);
    }

    [Fact] public void RemoveTakesOnlyAChosenIdleGameCopyAndKeepsWhatItsRunChanged()
    {
        var (server, client, world, _) = Layout();
        File.WriteAllText(Path.Combine(server, "toolkit-unity.log"), "written by the run");
        OwnedCopies.ProcessesOverride = () => [(42, Path.Combine(client, GameLaunch.ClientWindowsExecutable))];

        var retired = OwnedCopies.Remove(server);
        Assert.False(Directory.Exists(server));
        Assert.Equal("written by the run", File.ReadAllText(Path.Combine(server + "-changes", "toolkit-unity.log")));
        Assert.Equal(new[] { "toolkit-unity.log" }, retired.Added);

        Assert.Contains("Process 42", Assert.Throws<InvalidOperationException>(() => OwnedCopies.Remove(client)).Message);
        Assert.True(Directory.Exists(client));
        Assert.Contains("world copy", Assert.Throws<InvalidOperationException>(() => OwnedCopies.Remove(world)).Message);
        Assert.True(Directory.Exists(world));
        OwnedCopies.Remove(world, allowWorld: true);
        Assert.False(Directory.Exists(world));
        Assert.Throws<ArgumentException>(() => OwnedCopies.Remove(Path.Combine(Runs, "valheim-test-" + new string('a', 32))));
        Assert.Throws<ArgumentException>(() => OwnedCopies.Remove(Path.Combine(Runs, "unrelated", "game")));
        Assert.True(File.Exists(Path.Combine(Runs, "unrelated", "game", GameLaunch.ServerWindowsExecutable)));
    }

    // valheim-test env status lists copies no journal names (made before runs journalled them) and changes nothing;
    // env teardown --copy removes one by name, keeping what its run changed, and refuses a copy a run's journal names.
    [Fact] public async Task EnvStatusListsUnjournalledCopiesAndTeardownRemovesOneByName()
    {
        // Made by an older process: journalled on another machine's data folder, so this one has no record of them.
        (string Server, string Client, string World, string Staged) layout;
        using (RunJournal.UseLocalDirectory(Path.Combine(_root, "older", "journal"))) layout = Layout();
        var (server, client, world, staged) = layout;
        OwnedCopies.ProcessesOverride = () => [(42, Path.Combine(client, GameLaunch.ClientWindowsExecutable))];
        string data = Path.Combine(_root, "data");
        string inventory = Path.Combine(_root, "inventory.json");
        File.WriteAllText(inventory, JsonSerializer.Serialize(new
        {
            hosts = new { local = new { kind = "local", @lock = Path.Combine(data, "lock") } },
            environments = new[] { new { name = "c", host = "local", roles = new[] { "client" }, install = Path.Combine(_root, "game"), runtime = Runs, cliPort = 5700 } },
            leaseHost = "local", leaseDirectory = Path.Combine(data, "leases"),
        }));
        using var machine = EnvironmentInventory.UseMachine(new FakeMachine(HostProfile.CurrentPlatform) { DataRoot = data });
        using var localJournal = RunJournal.UseLocalDirectory(Path.Combine(data, "journal"));

        var output = new StringWriter();
        Assert.Equal(3, await EnvCommand.RunAsync(["status", "--inventory", inventory], output, new StringWriter()));
        foreach (string copy in new[] { server, client, world, staged })
            Assert.Contains($"UNJOURNALLED copy {copy} (", output.ToString());
        Assert.Contains($"valheim-test env teardown --copy \"{staged}\"", output.ToString());
        Assert.Contains("used by process 42", output.ToString());
        Assert.All(new[] { server, client, world, staged }, path => Assert.True(Directory.Exists(path)));

        output = new StringWriter();
        Assert.Equal(0, await EnvCommand.RunAsync(["teardown", "--copy", staged, "--inventory", inventory], output, new StringWriter()));
        Assert.StartsWith("REMOVED removed " + staged, output.ToString());
        Assert.False(Directory.Exists(staged));
        Assert.True(Directory.Exists(staged + "-changes"));
        output = new StringWriter();
        Assert.Equal(3, await EnvCommand.RunAsync(["teardown", "--copy", client, "--inventory", inventory], output, new StringWriter()));
        Assert.Contains("Process 42", output.ToString());
        Assert.True(Directory.Exists(client));
        // A world named on purpose goes too (its save is kept beside it like any change).
        Assert.Equal(0, await EnvCommand.RunAsync(["teardown", "--copy", world, "--inventory", inventory], new StringWriter(), new StringWriter()));
        Assert.False(Directory.Exists(world));

        // A copy this process made is journalled: teardown --copy refuses it and names the run.
        string source = Source("journalled", GameLaunch.ServerWindowsExecutable);
        using var held = WorldFixture.Copy(source, Runs, WorldFixture.Manifest(source));
        output = new StringWriter();
        Assert.Equal(3, await EnvCommand.RunAsync(["teardown", "--copy", held.DirectoryPath, "--inventory", inventory], output, new StringWriter()));
        Assert.Contains($"run {RunJournal.ThisProcess.RunId} (LIVE) journalled it", output.ToString());
        // Named through a link (on macOS the temp folder is /var, which is /private/var), it is still that run's.
        output = new StringWriter();
        Assert.Equal(3, await EnvCommand.RunAsync(["teardown", "--copy", OwnedCopies.Resolved(held.DirectoryPath), "--inventory", inventory], output, new StringWriter()));
        Assert.Contains("journalled it", output.ToString());
        Assert.True(Directory.Exists(held.DirectoryPath));

        foreach (string[] args in new[] { new[] { "teardown" }, ["teardown", "--copy", staged, "--run", "x"], ["teardown", "--copy", staged, "--json"], ["recover", "--copy", staged] })
            Assert.Equal(2, await EnvCommand.RunAsync(args, new StringWriter(), new StringWriter()));
    }

    [Fact] public void AnEmptyOrCorruptProvenanceCannotAuthorizeRemoval()
    {
        string copy = Copy(Source("server", GameLaunch.ServerWindowsExecutable), Runs);
        string provenance = Path.Combine(copy, "fixture-provenance.json");
        File.WriteAllText(provenance, "{}");
        Assert.Throws<InvalidDataException>(() => OwnedCopies.Remove(copy));
        File.WriteAllText(provenance, "{\"valheim_server.exe\":\"not-a-sha256\"}");
        Assert.Throws<InvalidDataException>(() => OwnedCopies.Remove(copy));
        File.WriteAllText(provenance, "{\"valheim_server.exe\":null}");
        Assert.Throws<InvalidDataException>(() => OwnedCopies.Remove(copy));
        Assert.True(Directory.Exists(copy));
    }

    // A copy is journalled on this machine before it is made (#257): while its process runs it is that run's, LIVE; a copy
    // handed over (preserved) or removed leaves nothing; one kept for a reason is KEPT. Owner records of older copies still count.
    [Fact] public async Task TheRunThatMadeACopyJournalsItUntilItLetsGo()
    {
        string data = Path.Combine(_root, "data");
        using var machine = EnvironmentInventory.UseMachine(new FakeMachine(HostProfile.CurrentPlatform) { DataRoot = data });
        using var localJournal = RunJournal.UseLocalDirectory(Path.Combine(data, "journal"));
        string source = Source("server", GameLaunch.ServerWindowsExecutable);
        var host = OperatingSystem.IsWindows() ? new LocalGameHost("local", HostShell.WindowsPowerShell) : new LocalGameHost("local", HostShell.Bash);
        var hosts = new Dictionary<string, HostProfile> { ["local"] = new() { Kind = "local", Lock = Path.Combine(data, "lock") } };
        async Task<JournalRunStatus> Status() =>
            Assert.Single((await RunJournalStatus.InspectAsync(hosts, _ => host, TimeSpan.FromSeconds(60))).Runs, run => run.Run == RunJournal.ThisProcess.RunId);

        var held = WorldFixture.Copy(source, Runs, WorldFixture.Manifest(source));
        Assert.False(File.Exists(held.DirectoryPath + ".owner.json"));
        // Held by this process, by its journal, before any game runs from it.
        Assert.Equal(new[] { Environment.ProcessId }, OwnedCopies.Find(held.DirectoryPath).Single().InUseBy);
        Assert.Contains($"Process {Environment.ProcessId}", Assert.Throws<InvalidOperationException>(() => OwnedCopies.Remove(held.DirectoryPath)).Message);
        var live = await Status();
        Assert.Equal(JournalRunState.Live, live.State);
        Assert.Contains(live.Items, item => item.What == held.DirectoryPath && item.Status == "copied, not retired");
        held.Preserve = true; held.Dispose();
        Assert.DoesNotContain((await Status()).Items, item => item.What == held.DirectoryPath);

        var kept = WorldFixture.Copy(source, Runs, WorldFixture.Manifest(source));
        kept.Preserve = true; kept.KeepReason = "kept on request"; kept.Dispose();
        Assert.Equal("kept: kept on request", Assert.Single((await Status()).Items, item => item.What == kept.DirectoryPath).Status);
        using (var removed = WorldFixture.Copy(source, Runs, WorldFixture.Manifest(source))) { }
        Assert.Single((await Status()).Items);

        // An owner record an older process wrote still holds its copy while that process runs, by ID and start time.
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        File.WriteAllText(held.DirectoryPath + ".owner.json", JsonSerializer.Serialize(new { Pid = Environment.ProcessId, StartedUtc = self.StartTime.ToUniversalTime() }));
        Assert.Contains(Environment.ProcessId, OwnedCopies.Find(held.DirectoryPath).Single().InUseBy);
        File.WriteAllText(held.DirectoryPath + ".owner.json", JsonSerializer.Serialize(new { Pid = Environment.ProcessId, StartedUtc = DateTime.UtcNow.AddDays(-3) }));
        Assert.False(OwnedCopies.Find(held.DirectoryPath).Single().InUse); // this process's id, but not its start time
    }

    // A 1.0 world is a <name>/ directory of chunks with _main.N.fwl2, and is protected like any world.
    [Fact] public void AChunkedWorldCopyIsAWorld()
    {
        string source = Source("chunked", "VTDefaultSmoke/_main.2.fwl2", "VTDefaultSmoke/_main.2.db2", "VTDefaultSmoke/1e_20__1_1.chunk");
        string copy = Copy(source, Runs);
        Assert.Equal(OwnedCopyKind.World, OwnedCopies.Find(Runs).Single().Kind);
        Assert.Throws<InvalidOperationException>(() => OwnedCopies.Remove(copy));
    }

    // A retry after a removal that could not finish keeps its changes beside the first attempt's.
    [Fact] public void ARetryKeepsItsChangesInANewFolder()
    {
        string copy = Copy(Source("server", GameLaunch.ServerWindowsExecutable), Runs);
        Directory.CreateDirectory(copy + "-changes"); File.WriteAllText(Path.Combine(copy + "-changes", "changes.json"), "{}");
        var retired = OwnedCopies.Remove(copy);
        Assert.Equal(copy + "-changes-2", retired.KeptIn);
        Assert.False(Directory.Exists(copy));
    }

    // A copy named through a linked directory (macOS's /var is /private/var) is matched to the program running from it.
    [Fact] public void ACopyReachedThroughALinkStillMatchesItsProgram()
    {
        if (OperatingSystem.IsWindows()) return; // the system names Windows executables as given; links there need privileges
        string real = Path.Combine(_root, "real"), linked = Path.Combine(_root, "linked");
        string copy = Path.Combine(real, "valheim-test-" + new string('c', 32));
        Directory.CreateDirectory(copy);
        File.WriteAllText(Path.Combine(copy, "fixture-provenance.json"), "{}");
        Directory.CreateSymbolicLink(linked, real);
        // The system names the executable with every link resolved, this test's temp root included.
        OwnedCopies.ProcessesOverride = () => [(99, Path.Combine(OwnedCopies.Resolved(copy), "valheim_server.x86_64"))];
        Assert.DoesNotContain("linked", OwnedCopies.Resolved(Path.Combine(linked, "valheim-test-" + new string('c', 32))));
        Assert.Equal(new[] { 99 }, Assert.Single(OwnedCopies.Find(Path.Combine(linked))).InUseBy);
    }

    // The real process list: a program running from inside a copy makes it in use, on every platform.
    // The real scan, not the seam: this test host is a running process, so its own executable must be listed, in the form
    // the runtime reports it (Windows and Linux ask for the image path alone; macOS reads MainModule).
    [Fact] public void TheScanNamesThisProcessOwnExecutable()
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var own = OwnedCopies.Processes().Where(process => process.Pid == Environment.ProcessId).ToList();
        Assert.Equal(OwnedCopies.Resolved(Environment.ProcessPath!), OwnedCopies.Resolved(Assert.Single(own).Executable), comparer);
    }

    [Fact] public void AProgramRunningFromACopyMakesItInUse()
    {
        string copy = Path.Combine(_root, "valheim-test-" + new string('b', 32));
        Directory.CreateDirectory(copy);
        File.WriteAllText(Path.Combine(copy, "fixture-provenance.json"), "{}");
        string program = Path.Combine(copy, OperatingSystem.IsWindows() ? "cmd.exe" : "sleep");
        File.Copy(OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : "/bin/sleep", program);
        // macOS kills a copied Apple system binary at launch (exit 137); an ad-hoc signature lets the copy run.
        if (OperatingSystem.IsMacOS()) System.Diagnostics.Process.Start("codesign", ["--force", "--sign", "-", program])!.WaitForExit();
        var start = new System.Diagnostics.ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in OperatingSystem.IsWindows() ? new[] { "/c", "ping -n 60 127.0.0.1 >nul" } : ["60"]) start.ArgumentList.Add(argument);
        using var running = System.Diagnostics.Process.Start(start)!;
        try
        {
            Thread.Sleep(200);
            Assert.False(running.HasExited, $"the program running from the copy ended at once (exit {(running.HasExited ? running.ExitCode : 0)})");
            var found = OwnedCopies.Find(_root).Single();
            Assert.Contains(running.Id, found.InUseBy);
            Assert.Throws<InvalidOperationException>(() => OwnedCopies.Remove(copy));
            Assert.True(Directory.Exists(copy));
        }
        finally { running.Kill(entireProcessTree: true); running.WaitForExit(); }
        Assert.False(OwnedCopies.Find(_root).Single().InUse);
    }
}
