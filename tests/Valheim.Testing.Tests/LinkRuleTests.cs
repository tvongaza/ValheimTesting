using System.Text;
using Valheim.Testing.Game;
using Xunit;
using Valheim.Testing.GameSessions;

// One rule for links, in its owners (#700): a local install's pins walk the paths a host's listing walks and refuse the same
// links, and a remote path that must stay apart from or inside a root is decided on the host, resolving links there
// (ProtectedPaths.BashInside and PowerShellInside), with this machine's own shell: bash, or pwsh on Windows.
public sealed class LinkRuleTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("link-rule-").FullName;
    private readonly LocalGameHost _host = new("this machine", HostShell.ThisMachine);
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(60);
    public void Dispose() => DirectoryLinks.DeleteTree(_root);

    private string Folder(string name)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private string Alias(string name, string target)
    {
        string alias = Path.Combine(_root, name);
        DirectoryLinks.Create(alias, target);
        return alias;
    }

    // ---- InstallPins: the local walk refuses a linked folder on the way, as the host's listing does ----

    // A linked valheim_server_Data or BepInEx: before #700 InstallPins hashed through it here while the host's listing refused it.
    [Theory]
    [InlineData("valheim_server_Data"), InlineData("BepInEx")]
    public async Task ALinkedFolderOnTheWayToAPinnedPathIsRefusedHereAndOnTheHost(string linked)
    {
        string install = Folder("install"), elsewhere = Folder("elsewhere");
        FakeInstalls.Server(install);
        FakeInstalls.Server(elsewhere);
        Directory.Delete(Path.Combine(install, linked), recursive: true);
        DirectoryLinks.Create(Path.Combine(install, linked), Path.Combine(elsewhere, linked));

        Assert.Contains("Links are unsupported", Assert.Throws<IOException>(() => InstallPins.Of(install)).Message);
        Assert.Contains("holds links", (await Assert.ThrowsAsync<IOException>(() => HostInstall.ListAsync(_host, install, Generous, InstallPins.PinPaths))).Message);
    }

    // The install itself may be reached through a link (/tmp, ~/.steam/steam): the rule applies below it, here and on the host.
    [Fact]
    public async Task AnInstallReachedThroughALinkHasTheSamePinsHereAndOnTheHost()
    {
        string install = Folder("install");
        FakeInstalls.Server(install);
        string alias = Alias("install-alias", install);
        var pins = InstallPins.Of(install);
        Assert.Equal(pins.Game, InstallPins.Of(alias).Game);
        Assert.Equal(pins.Loader, InstallPins.Of(alias).Loader);
        var listed = HostInstall.Pins(await HostInstall.ListAsync(_host, alias, Generous, InstallPins.PinPaths));
        Assert.Equal((pins.Game, pins.Loader, pins.Patchers), (listed.Game, listed.Loader, listed.Patchers));
    }

    // A link on the way that leads nowhere has nothing to enter: it names nothing, here as on the host.
    [Fact]
    public async Task ADanglingLinkOnTheWayNamesNothingHereOrOnTheHost()
    {
        string install = Folder("install"), gone = Folder("gone");
        FakeInstalls.Server(install);
        DirectoryLinks.Create(Path.Combine(install, "other_Data"), gone);
        Directory.Delete(gone);
        var pins = InstallPins.Of(install);
        var listed = HostInstall.Pins(await HostInstall.ListAsync(_host, install, Generous, InstallPins.PinPaths));
        Assert.Equal((pins.Game, pins.Loader, pins.Patchers), (listed.Game, listed.Loader, listed.Patchers));
    }

    // ---- the host's containment rule ----

    // A runtime reached through a link into the install is inside it: refused on the host. Before #700 only the spelling was
    // compared, so a disposable runtime under such a link was staged into the install that runs never change.
    [Fact]
    public async Task PreparationRefusesARuntimeReachedThroughALinkIntoTheInstall()
    {
        string source = Folder("install");
        File.WriteAllText(Path.Combine(source, "base.txt"), "source");
        string alias = Alias("runs-alias", source);
        string run = Path.Combine(alias, "runs", "one");
        string plugin = Path.Combine(Folder("plugins"), "selected.dll");
        File.WriteAllText(plugin, "plugin");
        HostedRuntimeFile[] files = [new(plugin, "BepInEx/plugins/selected.dll")];

        var profile = await Assert.ThrowsAsync<ArgumentException>(() => HostedRuntimeStage.PrepareProfileAsync(_host, HostedRuntimeKind.Server, source,
            Path.Combine(run, "runtime"), Path.Combine(run, "staging"), files, Generous));
        Assert.Contains("overlaps the source install", profile.Message);
        Assert.False(Directory.Exists(Path.Combine(source, "runs"))); // nothing was written into the install
        Assert.Equal("base.txt", Path.GetFileName(Assert.Single(Directory.GetFileSystemEntries(source))));
    }

    // This machine's one shell: bash, or pwsh on Windows.
    public static TheoryData<string> Shells => LocalGameHostShellTests.Shells;
    private static LocalGameHost Shell(string shell) => new("local-" + shell, HostShell.Parse(shell));

    [Theory]
    [MemberData(nameof(Shells))]
    public async Task TheHostDecidesContainmentWithLinksResolved(string shell)
    {
        var host = Shell(shell);
        string install = Folder("install"), runs = Folder("runs");
        string intoInstall = Alias("into-install", install), installAlias = Alias("install-alias", install);
        Task Apart(string root, params string[] paths) => HostInstall.RequireApartAsync(host, root, paths, "the install", Generous, default);

        await Apart(install, Path.Combine(runs, "r", "runtime"), Path.Combine(Folder("install2"), "runtime")); // a sibling sharing a prefix is apart
        await Apart(installAlias, Path.Combine(runs, "r", "runtime")); // the root itself may be a link
        await Assert.ThrowsAsync<ArgumentException>(() => Apart(install, Path.Combine(intoInstall, "runs", "r", "runtime"))); // inside, through a link
        await Assert.ThrowsAsync<ArgumentException>(() => Apart(installAlias, Path.Combine(install, "runtime"))); // inside a linked root
        await Assert.ThrowsAsync<ArgumentException>(() => Apart(ProtectedPaths.Resolve(install), Path.Combine(installAlias, "new", "x"))); // physical root, linked path
        await Assert.ThrowsAsync<ArgumentException>(() => Apart(install, intoInstall)); // the install itself
        await Assert.ThrowsAsync<ArgumentException>(() => Apart(install, _root)); // holds the install
        await Assert.ThrowsAsync<ArgumentException>(() => Apart(install, Path.Combine(runs, "r"), Path.Combine(intoInstall, "x"))); // any path named
        if (!OperatingSystem.IsWindows())
        {
            // A .. after a link goes up from the link's target, not from its spelling (Windows collapses .. as text, so not there).
            string subLink = Alias("sub-link", Folder(Path.Combine("install", "sub")));
            await Assert.ThrowsAsync<ArgumentException>(() => Apart(install, subLink + "/../runs"));
        }
        if (OperatingSystem.IsMacOS() && new DirectoryInfo("/tmp").LinkTarget != null)
        {
            // A top-level link with a relative target (macOS's /tmp is private/tmp) and a path that does not exist yet below it.
            string fresh = "vt-link-rule-" + Guid.NewGuid().ToString("N");
            await Assert.ThrowsAsync<ArgumentException>(() => Apart("/tmp", Path.Combine("/private/tmp", fresh, "x")));
            await Assert.ThrowsAsync<ArgumentException>(() => Apart(Path.Combine("/private/tmp", fresh), Path.Combine("/tmp", fresh, "x")));
        }
    }

    // A relative link target is joined to the link's physical parent, never to the spelling that reached it.
    [Theory]
    [MemberData(nameof(Shells))]
    public async Task ARelativeLinkTargetIsFollowedFromWhereTheLinkReallyIs(string shell)
    {
        if (OperatingSystem.IsWindows()) return; // a relative symlink needs a privilege there; junctions are always absolute
        var host = Shell(shell);
        string install = Folder(Path.Combine("real", "install")), inner = Folder(Path.Combine("real", "a"));
        Directory.CreateSymbolicLink(Path.Combine(inner, "rel"), Path.Combine("..", "install", "x"));
        string reached = Alias("lnk", inner);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            HostInstall.RequireApartAsync(host, install, [Path.Combine(reached, "rel", "run")], "the install", Generous, default));
        Assert.True(ProtectedPaths.Contains(install, Path.Combine(reached, "rel", "run"))); // as the local rule decides
    }

    // A dangling link into the install still names a place inside it (its target may be made later), or, where the host cannot
    // read where it leads, is unknown: refused either way, never taken as apart. Bash follows its target as the local rule does.
    [Theory]
    [MemberData(nameof(Shells))]
    public async Task ADanglingLinkIntoTheInstallIsRefused(string shell)
    {
        string install = Folder("install"), later = Folder(Path.Combine("install", "later"));
        string dangling = Alias("dangling", later);
        Directory.Delete(later);
        var error = await Record.ExceptionAsync(() => HostInstall.RequireApartAsync(Shell(shell), install, [Path.Combine(dangling, "run")], "the install", Generous, default));
        if (shell == "bash") Assert.IsType<ArgumentException>(error);
        else Assert.True(error is ArgumentException or IOException, error?.ToString() ?? "not refused");
    }

    [Theory]
    [MemberData(nameof(Shells))]
    public async Task ALinkLoopIsUnknownAndRefused(string shell)
    {
        string install = Folder("install");
        string a = Path.Combine(_root, "a"), b = Path.Combine(_root, "b");
        DirectoryLinks.Create(a, b);
        DirectoryLinks.Create(b, a);
        await Assert.ThrowsAsync<IOException>(() => HostInstall.RequireApartAsync(Shell(shell), install, [Path.Combine(a, "run")], "the install", Generous, default));
    }

    // ---- retiring a runtime: a kept file must lie inside the copy once links are resolved ----

    // A link inside the copy to another folder of the copy keeps its file (it is the copy's); one that leaves the copy does not.
    // Before #700 the Windows script refused any reparse point inside the copy while bash resolved it.
    [Fact]
    public async Task ARetiredRuntimeKeepsAFileBehindALinkInsideTheCopyOnly()
    {
        string run = Path.Combine(_root, "runs", "run-x"), runtime = Path.Combine(run, "runtime"), keep = Path.Combine(run, "runtime-changes");
        Directory.CreateDirectory(Path.Combine(runtime, "logs"));
        File.WriteAllText(Path.Combine(runtime, "logs", "game.log"), "inside");
        string outside = Folder("outside");
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "outside the copy");
        DirectoryLinks.Create(Path.Combine(runtime, "logs-link"), Path.Combine(runtime, "logs"));
        DirectoryLinks.Create(Path.Combine(runtime, "out-link"), outside);
        static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        var result = (await _host.RunAsync(OperatingSystem.IsWindows() ? HostedRunScripts.WindowsRetire : HostedRunScripts.Retire, new Dictionary<string, string>
        {
            ["runtime"] = runtime, ["keep"] = keep, ["run"] = "run-x", ["perfile"] = "1000", ["total"] = "5000",
            ["files"] = string.Join('\n', new[] { "logs-link/game.log", "out-link/secret.txt" }.Select(B64)),
        }, Generous)).EnsureSuccess("retire");
        Assert.Contains("VT-RETIRED ", result.Stdout);
        Assert.Equal("inside", File.ReadAllText(Path.Combine(keep, "logs-link", "game.log")));
        Assert.Contains($"VT-NOTKEPT {B64("out-link/secret.txt")} -1", result.Stdout);
        Assert.False(File.Exists(Path.Combine(keep, "out-link", "secret.txt")));
        Assert.True(File.Exists(Path.Combine(outside, "secret.txt"))); // removing the copy removed its links only
    }

    // ---- removing a copy and its staging folder (#738): through the one remover, a link is unlinked and a stray path is refused ----

    private string Prepared(string name)
    {
        string parent = Folder("vt-prep-" + name);
        Directory.CreateDirectory(Path.Combine(parent, "runtime", "BepInEx"));
        File.WriteAllText(Path.Combine(parent, "runtime", "game.txt"), "the copy");
        return parent;
    }

    private string Elsewhere(string name)
    {
        string folder = Folder(name);
        File.WriteAllText(Path.Combine(folder, "precious.txt"), "not ours");
        return folder;
    }

    // A staging folder that is a link goes as a link: its target's contents stay, with the shell's own rm (BSD on macOS).
    [Theory]
    [MemberData(nameof(Shells))]
    public async Task RetiringACopyUnlinksALinkedStagingFolderAndLeavesItsTargetAlone(string shell)
    {
        string parent = Prepared("linked-stage"), elsewhere = Elsewhere("elsewhere");
        string staging = Path.Combine(parent, "staging");
        DirectoryLinks.Create(staging, elsewhere);

        await HostedRuntimeStage.RetireAsync(Shell(shell), Path.Combine(parent, "runtime"), staging, Generous);

        Assert.False(Directory.Exists(parent)); // the copy, the link, then the empty parent
        Assert.Equal("not ours", File.ReadAllText(Path.Combine(elsewhere, "precious.txt")));
    }

    [Theory]
    [MemberData(nameof(Shells))]
    public async Task RetiringACopyUnlinksALinkedRuntimeAndLeavesItsTargetAlone(string shell)
    {
        string parent = Folder("vt-prep-linked-runtime"), elsewhere = Elsewhere("elsewhere");
        string runtime = Path.Combine(parent, "runtime");
        DirectoryLinks.Create(runtime, elsewhere);

        await HostedRuntimeStage.RetireAsync(Shell(shell), runtime, Path.Combine(parent, "staging"), Generous);

        Assert.False(Directory.Exists(runtime));
        Assert.Equal("not ours", File.ReadAllText(Path.Combine(elsewhere, "precious.txt")));
    }

    // A journal's stage that is not <copy's parent>/staging is refused before the host is asked; nothing is deleted.
    [Theory]
    [MemberData(nameof(Shells))]
    public async Task RetiringACopyRefusesAStagingPathThatIsNotItsOwn(string shell)
    {
        string parent = Prepared("stray-stage"), elsewhere = Elsewhere("elsewhere");
        foreach (string stage in new[] { elsewhere, Path.Combine(parent, "..", "elsewhere", "staging"), Path.Combine(parent, "staging", "..", "runtime"), Path.Combine(elsewhere, "staging") })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => HostedRuntimeStage.RetireAsync(Shell(shell), Path.Combine(parent, "runtime"), stage, Generous));
            Assert.Equal("not ours", File.ReadAllText(Path.Combine(elsewhere, "precious.txt")));
            Assert.True(File.Exists(Path.Combine(parent, "runtime", "game.txt")));
        }
    }

    // The host's own script refuses what resolves outside the copy's parent, whatever the caller checked: a staging folder whose
    // name is right but that sits beyond a link in the parent is not removed.
    [Theory]
    [MemberData(nameof(Shells))]
    public async Task TheCleanupScriptRefusesAPathBeyondALinkOutOfTheParent(string shell)
    {
        string parent = Prepared("beyond-link"), elsewhere = Elsewhere("elsewhere");
        Directory.CreateDirectory(Path.Combine(elsewhere, "staging"));
        File.WriteAllText(Path.Combine(elsewhere, "staging", "precious.txt"), "not ours either");
        string door = Path.Combine(parent, "door");
        DirectoryLinks.Create(door, elsewhere);
        var host = Shell(shell);

        var result = await host.RunAsync(host.Shell.Kind == HostShellKind.Bash ? HostedRuntimeStage.BashCleanup : HostedRuntimeStage.WindowsCleanup, new Dictionary<string, string>
        {
            ["runtime"] = Path.Combine(parent, "runtime"), ["stage"] = Path.Combine(door, "staging"), ["parent"] = parent,
        }, Generous);

        Assert.True(result.Failed, result.Describe() + result.Stderr);
        Assert.DoesNotContain("VT-STAGE-CLEANED", result.Stdout);
        Assert.Equal("not ours either", File.ReadAllText(Path.Combine(elsewhere, "staging", "precious.txt")));
        Assert.True(File.Exists(Path.Combine(parent, "runtime", "game.txt"))); // every path is checked before any is deleted
        Assert.Equal("not ours", File.ReadAllText(Path.Combine(elsewhere, "precious.txt")));
    }

    // The same for a path with no link at all: outside the parent, or the parent itself.
    [Theory]
    [MemberData(nameof(Shells))]
    public async Task TheCleanupScriptRefusesAPathOutsideTheParentOrTheParentItself(string shell)
    {
        string parent = Prepared("outside"), elsewhere = Elsewhere("elsewhere");
        var host = Shell(shell);
        foreach (string stage in new[] { elsewhere, parent })
        {
            var result = await host.RunAsync(host.Shell.Kind == HostShellKind.Bash ? HostedRuntimeStage.BashCleanup : HostedRuntimeStage.WindowsCleanup, new Dictionary<string, string>
            {
                ["runtime"] = "", ["stage"] = stage, ["parent"] = parent,
            }, Generous);
            Assert.True(result.Failed, result.Describe() + result.Stderr);
            Assert.Equal("not ours", File.ReadAllText(Path.Combine(elsewhere, "precious.txt")));
            Assert.True(File.Exists(Path.Combine(parent, "runtime", "game.txt")));
        }
    }

    // The remover itself: a trailing slash on a link (BSD rm follows "link/" and empties its target) names the link, not the target.
    [Theory]
    [MemberData(nameof(Shells))]
    public async Task TheRemoverTakesALinkWithATrailingSlashAsTheLinkAndNeverEmptiesItsTarget(string shell)
    {
        string parent = Folder("parent"), elsewhere = Elsewhere("elsewhere");
        string link = Path.Combine(parent, "stage");
        DirectoryLinks.Create(link, elsewhere);
        var host = Shell(shell);
        string script = host.Shell.Kind == HostShellKind.Bash
            ? "set -eu\n" + ProtectedPaths.BashInside + ProtectedPaths.BashRemove + "vt_remove \"$path/\" \"$parent\"\necho VT-DONE\n"
            : ProtectedPaths.PowerShellInside + ProtectedPaths.PowerShellRemove + "Remove-VtTree ($path + [IO.Path]::DirectorySeparatorChar) $parent\n'VT-DONE'\n";

        var result = (await host.RunAsync(script, new Dictionary<string, string> { ["path"] = link, ["parent"] = parent }, Generous)).EnsureSuccess("remove");

        Assert.Contains("VT-DONE", result.Stdout);
        Assert.False(Directory.Exists(link));
        Assert.Equal("not ours", File.ReadAllText(Path.Combine(elsewhere, "precious.txt")));
    }

    // A hosted run's runtime that is a link is unlinked, not left behind and not emptied.
    [Fact]
    public async Task ARetiredRunUnlinksARuntimeThatIsALinkAndKeepsItsTarget()
    {
        string run = Path.Combine(_root, "runs", "run-link"), runtime = Path.Combine(run, "runtime"), keep = Path.Combine(run, "runtime-changes");
        Directory.CreateDirectory(run);
        string elsewhere = Elsewhere("elsewhere");
        DirectoryLinks.Create(runtime, elsewhere);

        var result = (await _host.RunAsync(OperatingSystem.IsWindows() ? HostedRunScripts.WindowsRetire : HostedRunScripts.Retire, new Dictionary<string, string>
        {
            ["runtime"] = runtime, ["keep"] = keep, ["run"] = "run-link", ["perfile"] = "1000", ["total"] = "5000", ["files"] = "",
        }, Generous)).EnsureSuccess("retire");

        Assert.Contains("VT-RETIRED 0 0", result.Stdout);
        Assert.False(Directory.Exists(runtime));
        Assert.True(Directory.Exists(keep));
        Assert.Equal("not ours", File.ReadAllText(Path.Combine(elsewhere, "precious.txt")));
    }

    // ---- The rest of the host scripts' recursive deletes (#743): each is below an explicit root, through the one remover ----

    private static string Run(LocalGameHost host, string bash, string powershell) => host.Shell.Kind == HostShellKind.Bash ? bash : powershell;

    private static string Selected(params (string Path, string Kind)[] files) => string.Join('\n', files.Select(file =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(file.Path)) + " " + file.Kind));

    private static async Task HardLink(LocalGameHost host, string source, string target)
    {
        string script = host.Shell.Kind == HostShellKind.Bash
            ? "ln \"$source\" \"$target\""
            : "New-Item -ItemType HardLink -Path $target -Target $source -ErrorAction Stop | Out-Null";
        (await host.RunAsync(script, new Dictionary<string, string> { ["source"] = source, ["target"] = target }, Generous))
            .EnsureSuccess("create hard link");
    }

    // The character staging folder goes, and a stage beyond a link out of its root is refused with nothing deleted.
    [Theory]
    [MemberData(nameof(Shells))]
    public async Task DroppingTheCharacterStageRemovesItAndRefusesOneBeyondALinkOutOfItsRoot(string shell)
    {
        string root = Folder("characters-root"), stage = Path.Combine(root, "character-stage"), elsewhere = Elsewhere("elsewhere");
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "disposable.fch"), "a copy");
        var host = Shell(shell);
        string script = Run(host, HostedCharacterStage.BashDropStage, HostedCharacterStage.WindowsDropStage);

        var dropped = (await host.RunAsync(script, new Dictionary<string, string> { ["stage"] = stage, ["root"] = root }, Generous)).EnsureSuccess("drop");
        Assert.Contains("VT-CHAR-STAGE-DROPPED", dropped.Stdout);
        Assert.False(Directory.Exists(stage));

        Directory.CreateDirectory(Path.Combine(elsewhere, "character-stage"));
        File.WriteAllText(Path.Combine(elsewhere, "character-stage", "precious.txt"), "not ours either");
        string door = Path.Combine(root, "door");
        DirectoryLinks.Create(door, elsewhere);
        var refused = await host.RunAsync(script, new Dictionary<string, string> { ["stage"] = Path.Combine(door, "character-stage"), ["root"] = root }, Generous);
        Assert.True(refused.Failed, refused.Describe() + refused.Stderr);
        Assert.DoesNotContain("VT-CHAR-STAGE-DROPPED", refused.Stdout);
        Assert.Equal("not ours either", File.ReadAllText(Path.Combine(elsewhere, "character-stage", "precious.txt")));
    }

    // The apply step removes its staging folder, and refuses a runtime path that resolves beyond a link out of the runtime.
    [Theory]
    [MemberData(nameof(Shells))]
    public async Task ApplyingAProfileRemovesItsStagingAndRefusesAFolderBeyondALinkOutOfTheRuntime(string shell)
    {
        string parent = Folder("vt-prep-apply"), runtime = Path.Combine(parent, "runtime"), stage = Path.Combine(parent, "staging");
        foreach (string name in new[] { "plugins", "scripts", "config", "patchers" }) Directory.CreateDirectory(Path.Combine(runtime, "BepInEx", name));
        Directory.CreateDirectory(Path.Combine(stage, "BepInEx", "plugins"));
        File.WriteAllText(Path.Combine(stage, "BepInEx", "plugins", "mod.dll"), "the mod");
        var host = Shell(shell);
        string script = Run(host, HostedRuntimeStage.BashApply, HostedRuntimeStage.WindowsApply);
        string names = Selected(("BepInEx/plugins/mod.dll", "BepInEx"));
        Dictionary<string, string> Variables(string loader) => new()
        {
            ["runtime"] = runtime, ["stage"] = stage, ["parent"] = parent, ["files"] = names,
            ["loader"] = loader, ["preserveConfig"] = "false",
        };

        // BepInEx is a link out of the runtime: nothing below it is deleted, and the staging stays for recovery.
        string elsewhere = Elsewhere("elsewhere");
        Directory.CreateDirectory(Path.Combine(elsewhere, "plugins"));
        File.WriteAllText(Path.Combine(elsewhere, "plugins", "precious.txt"), "not ours either");
        string real = Path.Combine(runtime, "BepInEx"), moved = Path.Combine(parent, "BepInEx-real");
        Directory.Move(real, moved);
        DirectoryLinks.Create(real, elsewhere);
        var refused = await host.RunAsync(script, Variables(""), Generous);
        Assert.True(refused.Failed, refused.Describe() + refused.Stderr);
        Assert.Equal("not ours either", File.ReadAllText(Path.Combine(elsewhere, "plugins", "precious.txt")));
        Assert.True(Directory.Exists(stage));

        // The ordinary case: the files land, the old folders are replaced and the staging is gone.
        DirectoryLinks.Remove(real);
        Directory.Move(moved, real);
        var applied = (await host.RunAsync(script, Variables(""), Generous)).EnsureSuccess("apply");
        Assert.Contains("VT-STAGED", applied.Stdout);
        Assert.Equal("the mod", File.ReadAllText(Path.Combine(runtime, "BepInEx", "plugins", "mod.dll")));
        Assert.False(Directory.Exists(stage));
    }

    // BepInEx/core is not touched by the preliminary plugins/scripts/config/patchers cleanup, and the loader list is empty.
    // The apply step itself must unlink this selected target before copying, or cp/File.Copy rewrites the source hard link (#656).
    [Theory]
    [MemberData(nameof(Shells))]
    public async Task ApplyingAProfileUnlinksAHardLinkedSelectedTargetBeforeCopying(string shell)
    {
        string parent = Folder("vt-prep-hard-linked-selected"), runtime = Path.Combine(parent, "runtime"), stage = Path.Combine(parent, "staging");
        const string relative = "BepInEx/core/sentinel.dll";
        Directory.CreateDirectory(Path.Combine(runtime, "BepInEx", "core"));
        Directory.CreateDirectory(Path.Combine(stage, "BepInEx", "core"));
        string source = Path.Combine(Folder("source-selected"), "sentinel.dll");
        File.WriteAllText(source, "source bytes");
        string target = Path.Combine(runtime, "BepInEx", "core", "sentinel.dll");
        var host = Shell(shell);
        await HardLink(host, source, target);
        File.WriteAllText(Path.Combine(stage, "BepInEx", "core", "sentinel.dll"), "staged bytes");

        var result = (await host.RunAsync(Run(host, HostedRuntimeStage.BashApply, HostedRuntimeStage.WindowsApply), new Dictionary<string, string>
        {
            ["runtime"] = runtime, ["stage"] = stage, ["parent"] = parent,
            ["files"] = Selected((relative, "BepInEx")),
            ["loader"] = "", ["preserveConfig"] = "false",
        }, Generous)).EnsureSuccess("apply hard-linked selected target");

        Assert.Contains("VT-STAGED", result.Stdout);
        Assert.Equal("source bytes", File.ReadAllText(source));
        Assert.Equal("staged bytes", File.ReadAllText(target));
        Assert.False(Directory.Exists(stage));
    }

    // A local Mac server's reviewed SteamCMD closure is part of the selected payload. Its kind admits that exact payload
    // family without broadening the apply script to unrelated game-root targets.
    [Theory]
    [MemberData(nameof(Shells))]
    public async Task ApplyingAProfileAcceptsASelectedMacSteamRuntimePayload(string shell)
    {
        string parent = Folder("vt-prep-steam-runtime"), runtime = Path.Combine(parent, "runtime"), stage = Path.Combine(parent, "staging");
        const string relative = "steam-runtime/steamclient.dylib";
        Directory.CreateDirectory(runtime);
        Directory.CreateDirectory(Path.Combine(stage, "steam-runtime"));
        File.WriteAllText(Path.Combine(stage, "steam-runtime", "steamclient.dylib"), "reviewed Steam runtime");
        var host = Shell(shell);

        var result = (await host.RunAsync(Run(host, HostedRuntimeStage.BashApply, HostedRuntimeStage.WindowsApply), new Dictionary<string, string>
        {
            ["runtime"] = runtime, ["stage"] = stage, ["parent"] = parent,
            ["files"] = Selected((relative, "SteamRuntime")),
            ["loader"] = "", ["preserveConfig"] = "false",
        }, Generous)).EnsureSuccess("apply selected Steam runtime");

        Assert.Contains("VT-STAGED", result.Stdout);
        Assert.Equal("reviewed Steam runtime", File.ReadAllText(Path.Combine(runtime, "steam-runtime", "steamclient.dylib")));
        Assert.False(Directory.Exists(stage));
    }

    // The API never selects game paths, and the host enforces that boundary before deleting BepInEx or writing a file. A
    // malformed/replayed apply request therefore cannot write through one of the launch folder's hard links to the source.
    [Theory]
    [MemberData(nameof(Shells))]
    public async Task ApplyingAProfileRefusesAHardLinkedGameTargetBeforeWriting(string shell)
    {
        string parent = Folder("vt-prep-hard-linked-game"), runtime = Path.Combine(parent, "runtime"), stage = Path.Combine(parent, "staging");
        string plugins = Path.Combine(runtime, "BepInEx", "plugins");
        Directory.CreateDirectory(plugins);
        string marker = Path.Combine(plugins, "keep.dll");
        File.WriteAllText(marker, "untouched");
        Directory.CreateDirectory(stage);
        string source = Path.Combine(Folder("source-game"), "valheim_server.exe");
        File.WriteAllText(source, "source bytes");
        string target = Path.Combine(runtime, "valheim_server.exe");
        var host = Shell(shell);
        await HardLink(host, source, target);
        File.WriteAllText(Path.Combine(stage, "valheim_server.exe"), "staged bytes");

        var refused = await host.RunAsync(Run(host, HostedRuntimeStage.BashApply, HostedRuntimeStage.WindowsApply), new Dictionary<string, string>
        {
            ["runtime"] = runtime, ["stage"] = stage, ["parent"] = parent,
            ["files"] = Selected(("valheim_server.exe", "BepInEx")),
            ["loader"] = "", ["preserveConfig"] = "false",
        }, Generous);

        Assert.True(refused.Failed, refused.Describe() + refused.Stderr);
        Assert.Contains("valheim_server.exe", refused.Describe() + refused.Stderr);
        Assert.Equal("source bytes", File.ReadAllText(source));
        Assert.Equal("source bytes", File.ReadAllText(target));
        Assert.Equal("untouched", File.ReadAllText(marker));
        Assert.True(Directory.Exists(stage));
    }

    // Selection and PowerShell compare these path families without case. Bash must enforce the same contract even on a
    // case-sensitive host, for both BepInEx and the reviewed loader entries.
    [Theory]
    [MemberData(nameof(Shells))]
    public async Task ApplyingAProfileMatchesSelectedPrefixesWithoutCase(string shell)
    {
        string parent = Folder("vt-prep-case"), runtime = Path.Combine(parent, "runtime"), stage = Path.Combine(parent, "staging");
        string bepinex = "bePiNeX/core/mixed.dll", loader = "DOORSTOP_LIBS/libdoorstop_x64.so";
        Directory.CreateDirectory(Path.Combine(stage, "bePiNeX", "core"));
        Directory.CreateDirectory(Path.Combine(stage, "DOORSTOP_LIBS"));
        File.WriteAllText(Path.Combine(stage, "bePiNeX", "core", "mixed.dll"), "plugin");
        File.WriteAllText(Path.Combine(stage, "DOORSTOP_LIBS", "libdoorstop_x64.so"), "loader");
        Directory.CreateDirectory(runtime);
        var host = Shell(shell);

        var result = (await host.RunAsync(Run(host, HostedRuntimeStage.BashApply, HostedRuntimeStage.WindowsApply), new Dictionary<string, string>
        {
            ["runtime"] = runtime, ["stage"] = stage, ["parent"] = parent,
            ["files"] = Selected((bepinex, "BepInEx"), (loader, "Loader")),
            ["loader"] = "doorstop_libs", ["preserveConfig"] = "false",
        }, Generous)).EnsureSuccess("apply case-insensitive selected prefixes");

        Assert.Contains("VT-STAGED", result.Stdout);
        Assert.Equal("plugin", File.ReadAllText(Path.Combine(runtime, "bePiNeX", "core", "mixed.dll")));
        Assert.Equal("loader", File.ReadAllText(Path.Combine(runtime, "DOORSTOP_LIBS", "libdoorstop_x64.so")));
    }

    // A replay cannot borrow permission from another file's kind. The old union check accepted both swapped entries.
    [Theory]
    [MemberData(nameof(Shells))]
    public async Task ApplyingAProfileChecksEachSelectedFileAgainstItsOwnKind(string shell)
    {
        string parent = Folder("vt-prep-own-kind"), runtime = Path.Combine(parent, "runtime"), stage = Path.Combine(parent, "staging");
        string marker = Path.Combine(runtime, "BepInEx", "plugins", "keep.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        Directory.CreateDirectory(stage);
        File.WriteAllText(marker, "untouched");
        var host = Shell(shell);

        var refused = await host.RunAsync(Run(host, HostedRuntimeStage.BashApply, HostedRuntimeStage.WindowsApply), new Dictionary<string, string>
        {
            ["runtime"] = runtime, ["stage"] = stage, ["parent"] = parent,
            ["files"] = Selected(("BepInEx/core/wrong.dll", "SteamRuntime"), ("steam-runtime/wrong.dylib", "BepInEx")),
            ["loader"] = "", ["preserveConfig"] = "false",
        }, Generous);

        Assert.True(refused.Failed, refused.Describe() + refused.Stderr);
        Assert.Contains("outside the selected payload kinds", refused.Describe() + refused.Stderr);
        Assert.Equal("untouched", File.ReadAllText(marker));
        Assert.True(Directory.Exists(stage));
    }

    // Prefix checks are not containment checks: reject absolute names and every .. segment before cleanup or copying.
    [Theory]
    [MemberData(nameof(Shells))]
    public async Task ApplyingAProfileRefusesAbsoluteAndTraversingSelectedTargets(string shell)
    {
        string parent = Folder("vt-prep-target-path"), runtime = Path.Combine(parent, "runtime"), stage = Path.Combine(parent, "staging");
        string marker = Path.Combine(runtime, "BepInEx", "plugins", "keep.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        Directory.CreateDirectory(stage);
        File.WriteAllText(marker, "untouched");
        var host = Shell(shell);

        foreach (string relative in new[]
        {
            "/absolute.dll", @"C:\x.dll", @"\\server\share\x.dll", @"\\?\C:\x.dll",
            "BepInEx/../../outside.dll", @"BepInEx\..\..\x.dll",
        })
        {
            var refused = await host.RunAsync(Run(host, HostedRuntimeStage.BashApply, HostedRuntimeStage.WindowsApply), new Dictionary<string, string>
            {
                ["runtime"] = runtime, ["stage"] = stage, ["parent"] = parent,
                ["files"] = Selected((relative, "BepInEx")), ["loader"] = "", ["preserveConfig"] = "false",
            }, Generous);
            Assert.True(refused.Failed, refused.Describe() + refused.Stderr);
            bool traversing = relative.Replace('\\', '/').Split('/').Contains("..");
            Assert.Contains(traversing ? "travers" : "absolute", refused.Describe() + refused.Stderr, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("untouched", File.ReadAllText(marker));
            Assert.True(Directory.Exists(stage));
        }
    }

    // A loader entry beyond a link out of the runtime is refused as well (the entries come from the pins, but the host decides).
    [Theory]
    [MemberData(nameof(Shells))]
    public async Task ApplyingAProfileRefusesALoaderEntryBeyondALinkOutOfTheRuntime(string shell)
    {
        string parent = Folder("vt-prep-loader"), runtime = Path.Combine(parent, "runtime"), stage = Path.Combine(parent, "staging");
        foreach (string name in new[] { "plugins", "scripts", "config", "patchers" }) Directory.CreateDirectory(Path.Combine(runtime, "BepInEx", name));
        Directory.CreateDirectory(stage);
        string elsewhere = Elsewhere("elsewhere");
        Directory.CreateDirectory(Path.Combine(elsewhere, "core"));
        File.WriteAllText(Path.Combine(elsewhere, "core", "precious.txt"), "not ours either");
        DirectoryLinks.Create(Path.Combine(runtime, "door"), elsewhere);
        var host = Shell(shell);

        var refused = await host.RunAsync(Run(host, HostedRuntimeStage.BashApply, HostedRuntimeStage.WindowsApply), new Dictionary<string, string>
        {
            ["runtime"] = runtime, ["stage"] = stage, ["parent"] = parent, ["files"] = "",
            ["loader"] = "door/core", ["preserveConfig"] = "false",
        }, Generous);

        Assert.True(refused.Failed, refused.Describe() + refused.Stderr);
        Assert.Equal("not ours either", File.ReadAllText(Path.Combine(elsewhere, "core", "precious.txt")));
        Assert.True(Directory.Exists(stage));
    }

    // The kept-changes folder goes as the link it may be, never emptied through it (a regression guard for the move to the remover: the old
    // rm -rf and Directory.Delete already unlinked a link).
    [Theory]
    [MemberData(nameof(Shells))]
    public async Task DroppingTheKeptChangesRemovesThemAndUnlinksALinkedFolder(string shell)
    {
        string run = Folder("run-kept"), keep = Path.Combine(run, "runtime-changes"), elsewhere = Elsewhere("elsewhere");
        Directory.CreateDirectory(keep);
        File.WriteAllText(Path.Combine(keep, "changed.txt"), "kept");
        var host = Shell(shell);
        string script = Run(host, HostedRunScripts.DropKept, HostedRunScripts.WindowsDropKept);

        (await host.RunAsync(script, new Dictionary<string, string> { ["keep"] = keep, ["run"] = "run-kept" }, Generous)).EnsureSuccess("drop");
        Assert.False(Directory.Exists(keep));

        DirectoryLinks.Create(keep, elsewhere);
        (await host.RunAsync(script, new Dictionary<string, string> { ["keep"] = keep, ["run"] = "run-kept" }, Generous)).EnsureSuccess("drop link");
        Assert.False(Directory.Exists(keep));
        Assert.Equal("not ours", File.ReadAllText(Path.Combine(elsewhere, "precious.txt")));
    }

    // ---- Read-only entries (#747): Remove-Item -Force used to delete them, and the one remover still must ----

    private static void MakeReadOnly(string path)
    {
        if (OperatingSystem.IsWindows()) File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        else File.SetUnixFileMode(path, Directory.Exists(path) ? UnixFileMode.UserRead | UnixFileMode.UserExecute : UnixFileMode.UserRead);
    }

    private static void MakeWritable(string path)
    {
        if (OperatingSystem.IsWindows()) File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        else File.SetUnixFileMode(path, Directory.Exists(path) ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute : UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static bool IsReadOnly(string path) => OperatingSystem.IsWindows()
        ? File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly)
        : !File.GetUnixFileMode(path).HasFlag(UnixFileMode.UserWrite);

    // A tree with a read-only file and a read-only folder (with a read-only file inside it) goes, and a link inside it is unlinked (read-only on Windows): the clearing
    // of the read-only bit never enters the link, so the read-only file in its target stays read-only and in place.
    [Theory]
    [MemberData(nameof(Shells))]
    public async Task TheRemoverDeletesReadOnlyFilesAndFoldersAndLeavesALinksTargetAlone(string shell)
    {
        if (!OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess) return; // root deletes a read-only folder's contents anyway, so nothing would be proved
        string parent = Folder("parent"), tree = Path.Combine(parent, "tree"), elsewhere = Elsewhere("elsewhere");
        string guarded = Path.Combine(tree, "guarded"), precious = Path.Combine(elsewhere, "precious.txt"), fenced = Path.Combine(elsewhere, "fenced");
        Directory.CreateDirectory(fenced); // a read-only folder beyond the link: the clearing must not reach it either
        Directory.CreateDirectory(Path.Combine(guarded, "inner"));
        File.WriteAllText(Path.Combine(tree, "locked.txt"), "read-only");
        File.WriteAllText(Path.Combine(guarded, "inner", "deep.txt"), "read-only too");
        File.WriteAllText(Path.Combine(guarded, "inner.txt"), "in a read-only folder");
        DirectoryLinks.Create(Path.Combine(tree, "door"), elsewhere);
        DirectoryLinks.Create(Path.Combine(guarded, "inner", "zdoor"), elsewhere); // after the read-only file, inside read-only folders
        // The system's own recursive delete unlinks a link by itself, so only a read-only link (Windows) is left for the walk to meet.
        if (OperatingSystem.IsWindows()) foreach (string door in new[] { Path.Combine(tree, "door"), Path.Combine(guarded, "inner", "zdoor") }) MakeReadOnly(door);
        MakeReadOnly(precious);
        MakeReadOnly(fenced);
        MakeReadOnly(Path.Combine(tree, "locked.txt"));
        MakeReadOnly(Path.Combine(guarded, "inner", "deep.txt"));
        MakeReadOnly(Path.Combine(guarded, "inner"));
        MakeReadOnly(guarded);
        var host = Shell(shell);
        try
        {
            var result = (await host.RunAsync(Run(host, TreeBash, TreePowerShell), new Dictionary<string, string> { ["path"] = tree, ["parent"] = parent }, Generous)).EnsureSuccess("remove");

            Assert.Contains("VT-DONE", result.Stdout);
            Assert.False(Directory.Exists(tree));
            Assert.True(File.Exists(precious));
            Assert.True(IsReadOnly(precious));
            Assert.True(IsReadOnly(fenced));
            Assert.Equal("not ours", File.ReadAllText(precious));
        }
        finally
        {
            if (Directory.Exists(fenced)) MakeWritable(fenced);
            if (File.Exists(precious)) MakeWritable(precious);
            foreach (string stuck in new[] { guarded, Path.Combine(guarded, "inner") }.Where(Directory.Exists)) MakeWritable(stuck);
        }
    }

    // Hard links share their attributes on NTFS: a read-only file that is also linked elsewhere (a game file in the install, hard-linked into a
    // runtime) is not made writable through the tree being removed. The delete fails and the other name is untouched.
    [Fact]
    public async Task TheRemoverDoesNotClearTheReadOnlyBitOfAFileThatHasAnotherHardLink()
    {
        if (!OperatingSystem.IsWindows()) return; // elsewhere rm -rf needs no attribute, and a hard link shares none
        string parent = Folder("parent"), tree = Path.Combine(parent, "tree"), install = Folder("install");
        string original = Path.Combine(install, "game.dll"), linked = Path.Combine(tree, "game.dll");
        Directory.CreateDirectory(tree);
        File.WriteAllText(original, "the install's file");
        var link = await Shell("pwsh").RunAsync("New-Item -ItemType HardLink -Path $to -Target $from | Out-Null", new Dictionary<string, string> { ["to"] = linked, ["from"] = original }, Generous);
        link.EnsureSuccess("hard link");
        MakeReadOnly(original);
        try
        {
            var result = await Shell("pwsh").RunAsync(TreePowerShell, new Dictionary<string, string> { ["path"] = tree, ["parent"] = parent }, Generous);

            Assert.True(result.Failed, result.Describe() + result.Stderr);
            Assert.Contains("another hard link", result.Stderr);
            Assert.True(IsReadOnly(original));
            Assert.Equal("the install's file", File.ReadAllText(original));
        }
        finally { MakeWritable(original); }
    }

    // The same remover still refuses a path outside its root before it clears anything.
    [Theory]
    [MemberData(nameof(Shells))]
    public async Task TheRemoverLeavesAReadOnlyFileOutsideItsRootAsItIs(string shell)
    {
        string parent = Folder("parent"), elsewhere = Elsewhere("elsewhere"), precious = Path.Combine(elsewhere, "precious.txt");
        MakeReadOnly(precious);
        var host = Shell(shell);
        try
        {
            var result = await host.RunAsync(Run(host, TreeBash, TreePowerShell), new Dictionary<string, string> { ["path"] = elsewhere, ["parent"] = parent }, Generous);

            Assert.True(result.Failed, result.Describe() + result.Stderr);
            Assert.True(IsReadOnly(precious));
            Assert.Equal("not ours", File.ReadAllText(precious));
        }
        finally { MakeWritable(precious); }
    }

    private static readonly string TreeBash = "set -eu\n" + ProtectedPaths.BashInside + ProtectedPaths.BashRemove + "vt_remove \"$path\" \"$parent\"\necho VT-DONE\n";
    private static readonly string TreePowerShell = ProtectedPaths.PowerShellInside + ProtectedPaths.PowerShellRemove + "Remove-VtTree $path $parent\n'VT-DONE'\n";
}
