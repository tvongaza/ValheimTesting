using System.Text;
using Valheim.Testing.Game;
using Xunit;
using Valheim.Testing.GameSessions;

// One rule for links, in its owners (#700): a local install's pins walk the paths a host's listing walks and refuse the same
// links, and a remote path that must stay apart from or inside a root is decided on the host, resolving links there
// (ProtectedPaths.BashInside and PowerShellInside), with this machine's own shell: bash, or Windows PowerShell on Windows.
public sealed class LinkRuleTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("link-rule-").FullName;
    private readonly LocalGameHost _host = new("this machine", OperatingSystem.IsWindows() ? HostShell.WindowsPowerShell : HostShell.Bash);
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

        var copy = await Assert.ThrowsAsync<ArgumentException>(() => HostedRuntimeStage.PrepareAsync(_host, HostedRuntimeKind.Server, source,
            Path.Combine(run, "runtime"), Path.Combine(run, "staging"), files, Generous));
        Assert.Contains("overlaps the source install", copy.Message);
        var profile = await Assert.ThrowsAsync<ArgumentException>(() => HostedRuntimeStage.PrepareProfileAsync(_host, HostedRuntimeKind.Server, source,
            Path.Combine(run, "runtime"), Path.Combine(run, "staging"), files, Generous));
        Assert.Contains("overlaps the source install", profile.Message);
        Assert.False(Directory.Exists(Path.Combine(source, "runs"))); // nothing was written into the install
        Assert.Equal("base.txt", Path.GetFileName(Assert.Single(Directory.GetFileSystemEntries(source))));
    }

    // Every shell this machine has: bash, Windows PowerShell on Windows, and pwsh wherever it is installed (on Linux and macOS too).
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
}
