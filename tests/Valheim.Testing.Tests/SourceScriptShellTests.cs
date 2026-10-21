using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;
using Xunit;

// #612: the source settlement's host scripts (settle, evidence, write, reachability), the install listing it compares and the
// preflight read that reads its baseline back, on this machine's real shell (bash, or pwsh on Windows), each once per shell, here.
// Every scenario also runs on FakeServerHost.ThisMachine, in its own copy of the same folders: the fake's reply, its success (and
// exit code on success: a caller reads only whether a script failed) and what it leaves on disk must be the real script's. The
// tests that check what the toolkit decides from these replies (ProfileSourceCheckTests, LocalClientCopyTests,
// TargetedRegressionTests, HostingClientActorTests) run on that fake and start no shell. Their other steps (the profile's storage
// probe, seed, stage and retire; the hosted world's scripts; the process check and the journal) use the fake's answers every
// remote-host test already uses; those scripts' real runs are WindowsProfileStorageScriptTests, HostedRuntimeStageTests,
// HostedWorldOnHostShellTests, HostInstallListingTests and RunJournalTests.
[Trait("Category", "CiShell")]
public sealed class SourceScriptShellTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("source-scripts-").FullName;
    public void Dispose() => DirectoryLinks.DeleteTree(_root);

    private static string B64(string path) => Convert.ToBase64String(Encoding.UTF8.GetBytes(path));
    private static string Files(params string[] paths) => string.Join('\n', paths.Select(B64));

    // The scenario arranged in its own folder and run on the real shell, then arranged in another and run on the fake: the same
    // outcome and exit, the same reply (each folder's own path written <dir>) and the same entries left on disk. Returns the real
    // reply. arrange may return what it holds open during the run (a lock), disposed after it.
    private async Task<string> RunBoth(string shell, string bash, string pwsh, Func<string, IDisposable?> arrange,
        Func<string, Dictionary<string, string>> variables, Action<string>? real = null)
    {
        var outcomes = new List<(HostOutcome Outcome, bool Succeeded, int? Exit, string Reply, IReadOnlyList<string> Left)>();
        foreach (var (name, host) in new (string, IGameHost)[] { ("real", new LocalGameHost("local-" + shell, HostShell.Parse(shell))), ("fake", FakeServerHost.ThisMachine("local-" + shell)) })
        {
            string dir = Directory.CreateDirectory(Path.Combine(_root, name + "-" + Guid.NewGuid().ToString("N")[..6])).FullName;
            HostResult result;
            using (arrange(dir)) result = await host.RunAsync(shell == "pwsh" ? pwsh : bash, variables(dir), TimeSpan.FromSeconds(60));
            if (name == "real") real?.Invoke(dir);
            outcomes.Add((result.Outcome, result.Succeeded, result.Succeeded ? result.ExitCode : null, result.Stdout.Replace(dir, "<dir>").ReplaceLineEndings("\n"), Left(dir)));
        }
        Assert.Equal(outcomes[0].Outcome, outcomes[1].Outcome);
        Assert.Equal(outcomes[0].Succeeded, outcomes[1].Succeeded);
        Assert.Equal(outcomes[0].Exit, outcomes[1].Exit);
        Assert.Equal(outcomes[0].Reply, outcomes[1].Reply);
        Assert.Equal(outcomes[0].Left, outcomes[1].Left);
        return outcomes[0].Reply;
    }

    // Every entry under dir, links never entered: a file with its content's hash, a folder, a link.
    private static IReadOnlyList<string> Left(string dir)
    {
        var entries = new List<string>();
        void Walk(string folder)
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(folder).Order(StringComparer.Ordinal))
            {
                string relative = Path.GetRelativePath(dir, entry).Replace('\\', '/');
                if (new FileInfo(entry).LinkTarget != null) entries.Add("link " + relative);
                else if (Directory.Exists(entry)) { entries.Add("folder " + relative); Walk(entry); }
                else entries.Add("file " + relative + " " + (TryHash(entry) ?? "unreadable"));
            }
        }
        Walk(dir);
        return entries;
    }
    private static string? TryHash(string file)
    {
        try { return FileHash.Sha256(file); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    // ---- the settlement (SourceSettleScripts.Bash, .PowerShell) ----

    private Task<string> Settle(string shell, Func<string, IDisposable?> arrange, Func<string, string> root, Func<string, string> keep, string[] files,
        string[] folders, Action<string>? real = null) =>
        RunBoth(shell, SourceSettleScripts.Bash, SourceSettleScripts.PowerShell, arrange, dir => new()
        {
            ["root"] = root(dir), ["keep"] = keep(dir), ["files"] = Files(files), ["folders"] = Files(folders),
        }, real);

    // Each refusal of one path, and folders removed deepest first.
    [Theory, MemberData(nameof(LocalGameHostShellTests.Shells), MemberType = typeof(LocalGameHostShellTests))]
    public async Task TheSettleScriptRefusesLinksEscapesNonFilesAndKeptNamesAndRemovesFoldersDeepestFirst(string shell)
    {
        static string Install(string dir) => Path.Combine(dir, "install");
        static string Outside(string dir) => Path.Combine(dir, "runs");
        string reply = await Settle(shell, dir =>
        {
            Write(Path.Combine(Outside(dir), "elsewhere", "target.txt"), "outside the install");
            Directory.CreateDirectory(Install(dir));
            DirectoryLinks.Create(Path.Combine(Install(dir), "linked"), Path.Combine(Outside(dir), "elsewhere"));
            Directory.CreateDirectory(Path.Combine(Install(dir), "a-folder"));
            Write(Path.Combine(Install(dir), "kept.log"), "added");
            Write(Path.Combine(Outside(dir), "keep", "kept.log"), "an earlier move");
            Directory.CreateDirectory(Path.Combine(Install(dir), "a", "b", "c"));
            Write(Path.Combine(Install(dir), "moved.log"), "moved");
            return null;
        }, Install, dir => Path.Combine(Outside(dir), "keep"), ["linked/target.txt", "../outside.txt", "a-folder", "kept.log", "moved.log", "gone.log"],
            ["a/b/c", "a/b", "a", "a-folder/", "gone"], real: dir =>
            {
                Assert.Equal("outside the install", File.ReadAllText(Path.Combine(Outside(dir), "elsewhere", "target.txt")));
                Assert.Equal("added", File.ReadAllText(Path.Combine(Install(dir), "kept.log")));
                Assert.Equal("moved", File.ReadAllText(Path.Combine(Outside(dir), "keep", "moved.log")));
                Assert.False(File.Exists(Path.Combine(Install(dir), "moved.log")));
            });

        Assert.Contains("VT-NOTMOVED " + B64("linked/target.txt") + " link", reply);
        Assert.Contains("VT-NOTMOVED " + B64("../outside.txt") + " outside", reply);
        Assert.Contains("VT-NOTMOVED " + B64("a-folder") + " not-a-file", reply);
        Assert.Contains("VT-NOTMOVED " + B64("kept.log") + " kept-already", reply);
        Assert.Contains("VT-MOVED " + B64("moved.log"), reply);
        Assert.Contains("VT-GONE " + B64("gone.log"), reply);
        Assert.Contains("VT-NOTMOVED " + B64("a-folder/") + " outside", reply);
        Assert.Contains("VT-GONE " + B64("gone"), reply);
        Assert.Equal(["a/b/c", "a/b", "a"], reply.Split('\n').Where(line => line.StartsWith("VT-RMDIR ", StringComparison.Ordinal))
            .Select(line => Encoding.UTF8.GetString(Convert.FromBase64String(line["VT-RMDIR ".Length..].Trim()))));
        Assert.EndsWith("VT-SETTLED\n", reply);
    }

    // The whole settlement is refused, before anything moves: no install or keep folder named, an install that is not a folder (gone,
    // or a link), and a keep folder inside the install (on macOS also one that is the install in another letter case).
    [Theory, MemberData(nameof(LocalGameHostShellTests.Shells), MemberType = typeof(LocalGameHostShellTests))]
    public async Task TheSettleScriptRefusesAWholeSettlementBeforeAnythingMoves(string shell)
    {
        static string Install(string dir) => Path.Combine(dir, "install");
        static IDisposable? Arrange(string dir)
        {
            Write(Path.Combine(Install(dir), "kept.log"), "added");
            Directory.CreateDirectory(Path.Combine(dir, "real install"));
            DirectoryLinks.Create(Path.Combine(dir, "linked install"), Path.Combine(dir, "real install"));
            Write(Path.Combine(dir, "real install", "kept.log"), "added");
            return null;
        }
        string Keep(string dir) => Path.Combine(dir, "keep");
        foreach (var (root, keep, refusal) in new (Func<string, string>, Func<string, string>, string)[]
        {
            (_ => "", Keep, "no source install or keep folder was named"),
            (Install, _ => "", "no source install or keep folder was named"),
            (dir => Path.Combine(dir, "gone"), Keep, "the source install is not a folder"),
            (dir => Path.Combine(dir, "linked install"), Keep, "the source install is not a folder"),
            (Install, dir => Path.Combine(Install(dir), "keep"), "the keep folder is not outside the source install"),
            (Install, dir => Path.Combine(Install(dir), "deeper", "keep"), "the keep folder is not outside the source install"),
        })
            Assert.Equal("VT-SETTLE-REFUSED " + refusal + "\n", await Settle(shell, Arrange, root, keep, ["kept.log"], [],
                real: dir => Assert.Equal("added", File.ReadAllText(Path.Combine(Install(dir), "kept.log")))));
        if (OperatingSystem.IsMacOS())
            Assert.Equal("VT-SETTLE-REFUSED the keep folder is not outside the source install\n", await Settle(shell, Arrange, Install,
                dir => Path.Combine(dir, "INSTALL", "keep"), ["kept.log"], []));
    }

    // A name that ends in a line break is moved as itself (bash's $( ) would otherwise strip it).
    [UnixFact]
    public async Task TheSettleScriptKeepsANamesTrailingLineBreak()
    {
        static string Install(string dir) => Path.Combine(dir, "install");
        static string Keep(string dir) => Path.Combine(dir, "runs", "keep");
        string reply = await Settle("bash", dir =>
        {
            Write(Path.Combine(Install(dir), "note\n"), "a name ending in a line break");
            Write(Path.Combine(Install(dir), "note"), "the install's own");
            Write(Path.Combine(Install(dir), "folder\n", "inside.log"), "in a folder whose name ends in a line break");
            return null;
        }, Install, Keep, ["note\n", "folder\n/inside.log"], [], real: dir =>
        {
            Assert.Equal("a name ending in a line break", File.ReadAllText(Path.Combine(Keep(dir), "note\n")));
            Assert.Equal("in a folder whose name ends in a line break", File.ReadAllText(Path.Combine(Keep(dir), "folder\n", "inside.log")));
            Assert.Equal("the install's own", File.ReadAllText(Path.Combine(Install(dir), "note")));
        });

        Assert.Contains("VT-MOVED " + B64("note\n"), reply);
        Assert.Contains("VT-MOVED " + B64("folder\n/inside.log"), reply);
    }

    // ---- the evidence step (SourceSettleScripts.BashEvidence, .PowerShellEvidence) ----

    private static string Parent(string dir) => Path.Combine(dir, "vt-prep-run-client-1234");
    private static string Keep(string dir) => Path.Combine(Parent(dir), ProfileSourceCheck.AddedFolder + "-1234");
    private static string Baseline(string dir) => Path.Combine(Parent(dir), ProfileSourceCheck.BaselineFile);
    private static string Recovered(string dir) => Path.Combine(dir, "run", "recovered-client", "source-changes-1234");

    // A profile's folder: its runtime, its baseline, and what the settlement moved out of the install.
    private static IDisposable? Profile(string dir)
    {
        Write(Path.Combine(Parent(dir), "runtime", "game.bin"), "the profile");
        Write(Baseline(dir), "{}");
        Write(Path.Combine(Keep(dir), "BepInEx", "preloader_1.log"), "the game's");
        return null;
    }

    private Task<string> Evidence(string shell, Func<string, IDisposable?> arrange, Func<string, string> parent, Func<string, string> keep,
        bool added, Func<string, string>? recovered, bool baseline, Action<string>? real = null) =>
        RunBoth(shell, SourceSettleScripts.BashEvidence, SourceSettleScripts.PowerShellEvidence, arrange, dir => new()
        {
            ["parent"] = parent(dir), ["keep"] = keep(dir), ["added"] = added ? "1" : "", ["recovered"] = recovered?.Invoke(dir) ?? "",
            ["baseline"] = baseline ? "1" : "",
        }, real);

    // Each refusal that cannot change, with nothing moved or removed.
    [Theory, MemberData(nameof(LocalGameHostShellTests.Shells), MemberType = typeof(LocalGameHostShellTests))]
    public async Task TheEvidenceScriptRefusesWhatCannotChangeAndRemovesNothing(string shell)
    {
        void Untouched(string dir)
        {
            Assert.Equal("the game's", File.ReadAllText(Path.Combine(Keep(dir), "BepInEx", "preloader_1.log")));
            Assert.True(File.Exists(Baseline(dir)));
        }
        string Elsewhere(string dir) => Path.Combine(dir, "elsewhere", ProfileSourceCheck.AddedFolder + "-1234");
        foreach (var (keep, recovered, refusal) in new (Func<string, string>, Func<string, string>?, string)[]
        {
            (_ => "", null, "no profile folder or moved files folder was named"),
            (Elsewhere, null, "<dir>/elsewhere/source-added-1234 is not directly in <dir>/vt-prep-run-client-1234"),
            (dir => Path.Combine(Parent(dir), "runtime"), null, "<dir>/vt-prep-run-client-1234/runtime is not a moved files folder"),
            (dir => Path.Combine(Parent(dir), "runtime"), Recovered, "<dir>/vt-prep-run-client-1234/runtime is not a moved files folder"),
        })
        {
            string expected = "VT-SOURCE-REFUSED " + (shell == "pwsh" ? refusal.Replace('/', '\\') : refusal) + "\n";
            Assert.Equal(expected, await Evidence(shell, Profile, Parent, keep, added: true, recovered, baseline: true, Untouched));
        }
        Assert.Equal("VT-SOURCE-REFUSED no profile folder or moved files folder was named\n",
            await Evidence(shell, Profile, _ => "", Keep, added: true, null, baseline: true, Untouched));

        // A recovery never adopts a moved-files folder that is a link, nor moves one over a recovered folder already there.
        string linked = await Evidence(shell, dir =>
        {
            Write(Path.Combine(dir, "target", "preloader_1.log"), "elsewhere");
            Directory.CreateDirectory(Parent(dir));
            DirectoryLinks.Create(Keep(dir), Path.Combine(dir, "target"));
            return null;
        }, Parent, Keep, added: false, Recovered, baseline: false, real: dir => Assert.False(Directory.Exists(Recovered(dir))));
        Assert.Equal("VT-SOURCE-REFUSED the moved files folder is a link\n", linked);
        string there = await Evidence(shell, dir => { Profile(dir); Directory.CreateDirectory(Path.Combine(Recovered(dir), "added")); return null; },
            Parent, Keep, added: false, Recovered, baseline: false, Untouched);
        Assert.Equal("VT-SOURCE-REFUSED " + (shell == "pwsh" ? @"<dir>\run\recovered-client\source-changes-1234\added" : "<dir>/run/recovered-client/source-changes-1234/added") + " already exists\n", there);
    }

    // A recovery adopts the moved-files folder as its recovered folder's added, creating that folder; a run removes it once fetched,
    // and the baseline last. A moved-files folder that is a link is unlinked, its target never entered.
    [Theory, MemberData(nameof(LocalGameHostShellTests.Shells), MemberType = typeof(LocalGameHostShellTests))]
    public async Task TheEvidenceScriptAdoptsOrRemovesTheMovedFilesAndTheBaselineLast(string shell)
    {
        Assert.Equal("VT-SOURCE-SETTLED\n", await Evidence(shell, Profile, Parent, Keep, added: false, Recovered, baseline: false, real: dir =>
        {
            Assert.Equal("the game's", File.ReadAllText(Path.Combine(Recovered(dir), "added", "BepInEx", "preloader_1.log")));
            Assert.False(Directory.Exists(Keep(dir)));
            Assert.True(File.Exists(Baseline(dir)));
        }));
        Assert.Equal("VT-SOURCE-SETTLED\n", await Evidence(shell, Profile, Parent, Keep, added: true, null, baseline: true, real: dir =>
        {
            Assert.False(Directory.Exists(Keep(dir)));
            Assert.False(File.Exists(Baseline(dir)));
            Assert.True(File.Exists(Path.Combine(Parent(dir), "runtime", "game.bin")));
        }));
        // A read-only entry in the moved files (a folder this user cannot write on Unix, a read-only file on Windows) is removed too.
        Assert.Equal("VT-SOURCE-SETTLED\n", await Evidence(shell, dir =>
        {
            Profile(dir);
            string locked = Path.Combine(Keep(dir), "locked");
            Write(Path.Combine(locked, "held.log"), "read only");
            if (OperatingSystem.IsWindows()) File.SetAttributes(Path.Combine(locked, "held.log"), FileAttributes.ReadOnly);
            else File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            return null;
        }, Parent, Keep, added: true, null, baseline: false, real: dir => Assert.False(Directory.Exists(Keep(dir)))));
        // Kept on the host (added not set): the folder stays, the baseline goes.
        Assert.Equal("VT-SOURCE-SETTLED\n", await Evidence(shell, Profile, Parent, Keep, added: false, null, baseline: true, real: dir =>
        {
            Assert.True(File.Exists(Path.Combine(Keep(dir), "BepInEx", "preloader_1.log")));
            Assert.False(File.Exists(Baseline(dir)));
        }));
        Assert.Equal("VT-SOURCE-SETTLED\n", await Evidence(shell, dir =>
        {
            Write(Path.Combine(dir, "target", "preloader_1.log"), "elsewhere");
            Directory.CreateDirectory(Parent(dir));
            DirectoryLinks.Create(Keep(dir), Path.Combine(dir, "target"));
            return null;
        }, Parent, Keep, added: true, null, baseline: true, real: dir =>
        {
            Assert.False(Path.Exists(Keep(dir)));
            Assert.Equal("elsewhere", File.ReadAllText(Path.Combine(dir, "target", "preloader_1.log")));
        }));
    }

    // ---- reachability (SourceSettleScripts.BashReachable, .PowerShellReachable) ----

    [Theory, MemberData(nameof(LocalGameHostShellTests.Shells), MemberType = typeof(LocalGameHostShellTests))]
    public async Task TheReachabilityScriptNamesTheFirstFolderThatDoesNotAnswer(string shell)
    {
        static IDisposable? Arrange(string dir) { Directory.CreateDirectory(Path.Combine(dir, "install")); Directory.CreateDirectory(Path.Combine(dir, "profile")); return null; }
        Task<string> Reachable(string root, string parent) => RunBoth(shell, SourceSettleScripts.BashReachable, SourceSettleScripts.PowerShellReachable, Arrange,
            dir => new() { ["root"] = Path.Combine(dir, root), ["parent"] = Path.Combine(dir, parent) });
        string separator = shell == "pwsh" ? "\\" : "/";

        Assert.Equal("VT-REACHABLE yes\n", await Reachable("install", "profile"));
        Assert.Equal($"VT-REACHABLE no <dir>{separator}gone\n", await Reachable("gone", "profile"));
        Assert.Equal($"VT-REACHABLE no <dir>{separator}gone\n", await Reachable("install", "gone"));
    }

    // ---- the baseline and report write (SourceSettleScripts.BashWrite, .PowerShellWrite) ----

    // Written whole into a folder it makes, and over an earlier file of the same name.
    [Theory, MemberData(nameof(LocalGameHostShellTests.Shells), MemberType = typeof(LocalGameHostShellTests))]
    public async Task TheWriteScriptMakesItsFolderAndReplacesAnEarlierFile(string shell)
    {
        byte[] content = [0x7b, 0x0a, 0x00, 0xff, 0x7d];
        Task<string> Written(string name) => RunBoth(shell, SourceSettleScripts.BashWrite, SourceSettleScripts.PowerShellWrite,
            dir => { Write(Path.Combine(dir, "profile", "source-baseline.json"), "an earlier baseline"); return null; },
            dir => new() { ["path"] = Path.Combine(dir, name), ["content"] = Convert.ToBase64String(content) },
            real: dir => Assert.Equal(content, File.ReadAllBytes(Path.Combine(dir, name))));
        Assert.Equal("VT-SOURCE-WRITTEN\n", await Written(Path.Combine("profile", "source-baseline.json")));
        Assert.Equal("VT-SOURCE-WRITTEN\n", await Written(Path.Combine("recovered-client", "source-changes-1234", "source-changes.json")));
    }

    // ---- the install listing the settlement compares (HostInstallScripts.BashList, .PowerShellList) ----

    // As each host lists it for the baseline and the settlement: every file with its hash and size, every folder (an empty one too);
    // an install that holds a link anywhere is refused as holding links, and one that is not there as missing. The fake's reply is
    // read to the same listing (the scripts' own runs are HostInstallListingTests').
    [Theory, MemberData(nameof(LocalGameHostShellTests.Shells), MemberType = typeof(LocalGameHostShellTests))]
    public async Task TheSettlementsListingIsTheSameOnTheFake(string shell)
    {
        var listings = new List<string>();
        foreach (IGameHost host in new IGameHost[] { new LocalGameHost("local-" + shell, HostShell.Parse(shell)), FakeServerHost.ThisMachine("local-" + shell) })
        {
            string install = Path.Combine(_root, "install-" + Guid.NewGuid().ToString("N")[..6]);
            Write(Path.Combine(install, "game.bin"), "game");
            Write(Path.Combine(install, "BepInEx", "core", "BepInEx.dll"), "loader");
            Write(Path.Combine(install, "mods", "deep", "state.json"), "a mod wrote this");
            Directory.CreateDirectory(Path.Combine(install, "empty", "inner"));
            var listing = await HostInstall.ListProfileSourceAsync(host, install, TimeSpan.FromSeconds(60));
            var metadata = await HostInstall.ListMetadataAsync(host, install, TimeSpan.FromSeconds(60));
            listings.Add(string.Join("\n", listing.Files.OrderBy(file => file.Key, StringComparer.Ordinal).Select(file => $"file {file.Key} {file.Value} {listing.States[file.Key].Size}")
                .Concat(listing.Directories.Order(StringComparer.Ordinal).Select(folder => "folder " + folder))
                .Concat(metadata.States.OrderBy(file => file.Key, StringComparer.Ordinal).Select(file => $"state {file.Key} {file.Value.Size} {file.Value.Modified == listing.States[file.Key].Modified}"))
                .Concat(metadata.Directories.Order(StringComparer.Ordinal).Select(folder => "metadata folder " + folder))));

            Write(Path.Combine(_root, "elsewhere", "target.txt"), "outside");
            DirectoryLinks.Create(Path.Combine(install, "mods", "linked"), Path.Combine(_root, "elsewhere"));
            Assert.True(Assert.ThrowsAny<IOException>(() => HostInstall.ListMetadataAsync(host, install, TimeSpan.FromSeconds(60)).GetAwaiter().GetResult())
                .Data.Contains(HostInstall.LinksFound), host.GetType().Name);
            // A named listing (a before-boot check's) refuses a link it reaches: on the way to the named path, or inside it; one
            // elsewhere in the install is not listed.
            foreach (string reached in new[] { "mods/linked/*", "mods" })
                Assert.True(Assert.ThrowsAny<IOException>(() => HostInstall.ListAsync(host, install, TimeSpan.FromSeconds(60), [reached]).GetAwaiter().GetResult())
                    .Data.Contains(HostInstall.LinksFound), host.GetType().Name + " " + reached);
            var named = await HostInstall.ListAsync(host, install, TimeSpan.FromSeconds(60), ["BepInEx/*", "game.bin"]);
            listings[^1] += "\n" + string.Join("\n", named.Files.OrderBy(file => file.Key, StringComparer.Ordinal).Select(file => $"named {file.Key} {file.Value}"));
            DirectoryLinks.Remove(Path.Combine(install, "mods", "linked"));
            Assert.Throws<DirectoryNotFoundException>(() => HostInstall.ListMetadataAsync(host, install + "-gone", TimeSpan.FromSeconds(60)).GetAwaiter().GetResult());
        }
        Assert.Equal(listings[0], listings[1]);
        Assert.Contains("folder empty/inner", listings[0]);
        Assert.Contains("named BepInEx/core/BepInEx.dll", listings[0]);
    }

    // ---- the preflight read (HostClientPreflight.BashRead, .PowerShellRead) ----

    private Task<string> Read(string shell, Func<string, IDisposable?> arrange, string name, long limit) =>
        RunBoth(shell, HostClientPreflight.BashRead, HostClientPreflight.PowerShellRead, arrange,
            dir => new() { ["path"] = Path.Combine(dir, name), ["limit"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture) });

    private static readonly byte[] Bytes = [0x61, 0x0a, 0x00, 0xff, 0x7b];
    private static IDisposable? AFile(string dir) { File.WriteAllBytes(Path.Combine(dir, "baseline.json"), Bytes); Directory.CreateDirectory(Path.Combine(dir, "a folder")); return null; }

    [Theory, MemberData(nameof(LocalGameHostShellTests.Shells), MemberType = typeof(LocalGameHostShellTests))]
    public async Task ThePreflightReadAnswersMissingTooLargeOrTheBytes(string shell)
    {
        Assert.Equal("VT-PREFLIGHT " + Convert.ToBase64String(Bytes) + "\n", await Read(shell, AFile, "baseline.json", Bytes.Length));
        Assert.Equal("VT-PREFLIGHT too-large\n", await Read(shell, AFile, "baseline.json", Bytes.Length - 1));
        Assert.Equal("VT-PREFLIGHT missing\n", await Read(shell, AFile, "gone.json", Bytes.Length));
        Assert.Equal("VT-PREFLIGHT missing\n", await Read(shell, AFile, "a folder", Bytes.Length));
        // A link whose target is gone (Unix: a file symlink needs no privilege there).
        if (!OperatingSystem.IsWindows())
            Assert.Equal("VT-PREFLIGHT missing\n", await Read(shell, dir => { File.CreateSymbolicLink(Path.Combine(dir, "dangling.json"), Path.Combine(dir, "gone.json")); return null; },
                "dangling.json", Bytes.Length));
    }

    // A file the host user cannot read: mode 000 on Unix (root reads it anyway), read access denied to this user on Windows.
    [Theory, MemberData(nameof(LocalGameHostShellTests.Shells), MemberType = typeof(LocalGameHostShellTests))]
    public async Task ThePreflightReadAnswersUnreadable(string shell)
    {
        if (!OperatingSystem.IsWindows() && Environment.UserName == "root") return;
        Assert.Equal("VT-PREFLIGHT unreadable\n", await Read(shell, dir =>
        {
            AFile(dir);
            string file = Path.Combine(dir, "baseline.json");
            if (OperatingSystem.IsWindows()) DenyRead(file); else File.SetUnixFileMode(file, UnixFileMode.None);
            return null;
        }, "baseline.json", Bytes.Length));
    }

    // Windows only: a file another process holds open without sharing is "locked" (a sharing violation), never unreadable or an error.
    [Theory, MemberData(nameof(LocalGameHostShellTests.Shells), MemberType = typeof(LocalGameHostShellTests))]
    public async Task ThePreflightReadAnswersLockedOnWindows(string shell)
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal("VT-PREFLIGHT locked\n", await Read(shell, dir =>
        {
            AFile(dir);
            return new FileStream(Path.Combine(dir, "baseline.json"), FileMode.Open, FileAccess.Read, FileShare.None);
        }, "baseline.json", Bytes.Length));
    }

    // This user may see the file but not read its content.
    [SupportedOSPlatform("windows")]
    private static void DenyRead(string file)
    {
        string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        using var icacls = Process.Start(new ProcessStartInfo("icacls", [file, "/deny", "*" + sid + ":(RD)"])
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        var error = icacls.StandardError.ReadToEndAsync();
        string output = icacls.StandardOutput.ReadToEnd() + error.GetAwaiter().GetResult();
        icacls.WaitForExit();
        if (icacls.ExitCode != 0) throw new IOException("Could not deny reading " + file + ": " + output);
    }
}
