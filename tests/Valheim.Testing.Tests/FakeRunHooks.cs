using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Valheim.Testing.GameSessions;

/// <summary>
/// A hosted run's hooks for no-game tests: fake hosts and transports, a fixed run id, a stand-in
/// macOS session and launch, and a Ctrl+C owner the test signals. Whatever is not set is the production hook.
/// </summary>
internal sealed class FakeRunHooks : IHostedRunHooks
{
    private static IHostedRunHooks Real => HostedRunHooks.Production;
    /// <summary>The host of that name instead of the environment's.</summary>
    public Func<string, IGameHost>? Host { get; init; }
    /// <summary>The transport for a port (a tunnel's local end, or an attached client's port) instead of a CLI socket.</summary>
    public Func<int, IGameTransport>? Connect { get; init; }
    /// <summary>False skips the state pushes (a fake transport has none).</summary>
    public bool StateWaits { get; init; } = true;
    /// <summary>The run's id, even in a campaign (whose prepared installs keep the campaign's own id), as the tests' fixed paths expect.</summary>
    public string? RunId { get; init; }
    /// <summary>The macOS GUI-session check instead of the real probe.</summary>
    public Action? RequireMacGui { get; init; }
    /// <summary>Starts a local macOS client without opening the game; also lets a non-macOS test reach that path.</summary>
    public Func<ClientRunPlan, string, SteamAccountHold?, CancellationToken, Action<IOwnedProcess>?, ClientSession>? LocalMacLaunch { get; init; }
    /// <summary>The run's Ctrl+C owner, which the test keeps (the run does not dispose it).</summary>
    public RunCancellation? Cancellation { get; init; }
    /// <summary>The pinned-loader rule for the run's actors (#711); none by default, so a test's fake installs keep their own fake loader.</summary>
    public ShippedLoader.Rule? PinnedLoader { get; init; }
    /// <summary>The ValheimCLI set a standalone server's profile stages; by default the tests' own (<see cref="TestCliSet"/>), never this build's bundle.</summary>
    public Func<CliBundleSource?>? CliSet { get; init; }

    IGameHost IHostedRunHooks.CreateHost(ResolvedEnvironment environment, string name) => Host?.Invoke(name) ?? Real.CreateHost(environment, name);
    IGameTransport IHostedRunHooks.Connect(string address, int port) => Connect?.Invoke(port) ?? Real.Connect(address, port);
    string IHostedRunHooks.RunId(string? campaignRunId) => RunId ?? Real.RunId(campaignRunId);
    Task<SteamAccountHold> IHostedRunHooks.LeaseAsync(ResolvedEnvironment environment, string client, string owner, string run, IGameHost leaseHost, TimeSpan timeout,
        CancellationToken cancellation) => Real.LeaseAsync(environment, client, owner, run, leaseHost, timeout, cancellation);
    bool IHostedRunHooks.LocalMacClients => LocalMacLaunch != null || Real.LocalMacClients;
    void IHostedRunHooks.RequireMacGui() { if (RequireMacGui != null) RequireMacGui(); else Real.RequireMacGui(); }
    ClientSession IHostedRunHooks.LaunchLocalMac(ClientRunPlan plan, string output, SteamAccountHold? account, CancellationToken cancellation, Action<IOwnedProcess>? processStarted) =>
        LocalMacLaunch != null ? LocalMacLaunch(plan, output, account, cancellation, processStarted) : Real.LaunchLocalMac(plan, output, account, cancellation, processStarted);
    ShippedLoader.Choice? IHostedRunHooks.PinnedLoader(string actor, string install, string platform) => PinnedLoader?.Invoke(actor, install, platform);
    CliBundleSource? IHostedRunHooks.CliSet() => CliSet != null ? CliSet() : TestCliSet.Source;
    string IHostedRunHooks.LocalProcessStartFileTimeUtc(int pid, string hostStartIdentity) => hostStartIdentity;
    RunCancellation IHostedRunHooks.Cancellation(out bool owned)
    {
        if (Cancellation == null) return Real.Cancellation(out owned);
        owned = false;
        return Cancellation;
    }
}

/// <summary>
/// A ValheimCLI set for no-game tests: one core plugin (<c>valheimCLI.dll</c>, the GUID <c>valheimCLI.valheimCLI</c>) with its
/// capability manifest beside it, as a bundle holds them. Made once per test process, in its own temporary folder.
/// </summary>
internal static class TestCliSet
{
    private static readonly Lazy<CliBundleSource> Made = new(() =>
    {
        string folder = Directory.CreateTempSubdirectory("vt-test-cli-set-").FullName;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } };
        string core = Path.Combine(folder, "valheimCLI.dll");
        File.WriteAllBytes(core, RegressionRig.Assembly("valheimCLI", new("valheimCLI.valheimCLI", "0.4.0")));
        string manifest = Path.Combine(folder, CliBundle.ManifestFile);
        new CliCapabilityManifest
        {
            Build = "valheimCLI test set",
            Plugin = new() { File = "valheimCLI.dll", Sha256 = FileHash.Sha256(core), Plugins = ["valheimCLI.valheimCLI"] },
        }.Write(manifest);
        return new CliBundleSource(manifest, folder, "the tests' ValheimCLI set");
    });

    /// <summary>The set: its manifest and the folder of its DLLs.</summary>
    public static CliBundleSource Source => Made.Value;
    /// <summary>The MD5 a plan's ValheimCLI pin takes once bound to a profile that staged this set.</summary>
    public static string CorePin => FileHash.Md5(Path.Combine(Source.Files, "valheimCLI.dll"));
}

/// <summary>
/// This machine as a standalone run without <c>--inventory</c> places its server on it (#730), for no-game tests: a machine of the
/// runner's own kind (<see cref="FakeMachine"/>: Windows on a Windows runner, else Linux, so a plan's paths are full paths here)
/// whose <c>local</c> host is a <see cref="FakeServerHost"/> over a mirror, holding a modded dedicated-server install of that
/// platform at <see cref="Install"/>. A plan names <see cref="Install"/> as its runtime source; the run stages its server's
/// profile on that host. Run the runner inside <see cref="Use"/>.
/// </summary>
internal sealed class LocalServerMachine
{
    public static readonly bool Windows = OperatingSystem.IsWindows();
    public static readonly string Install = Windows ? @"C:\valheim\server" : "/opt/valheim/server";
    public FakeServerHost Host { get; }
    /// <summary>The install's folder in the mirror, where a test writes and hashes its files.</summary>
    public string InstallDirectory => Host.Local(Install);

    public LocalServerMachine(string mirror, FakeOwnedServer? server = null)
    {
        Host = new FakeServerHost(EnvironmentInventory.LocalHost, mirror, server, windows: Windows);
        Directory.CreateDirectory(InstallDirectory);
        FakeInstalls.Server(InstallDirectory);
        if (Windows)
        {
            File.WriteAllText(Path.Combine(InstallDirectory, GameLaunch.ServerWindowsExecutable), "server");
            File.WriteAllText(Path.Combine(InstallDirectory, "winhttp.dll"), "MZ target_assembly");
            File.WriteAllText(Path.Combine(InstallDirectory, "doorstop_config.ini"), DoorstopMixPathsTests.Doorstop4Config);
        }
        else
        {
            File.WriteAllText(Path.Combine(InstallDirectory, GameLaunch.ServerLinuxExecutable), "server");
            FakeInstalls.LinuxLoader(InstallDirectory);
        }
    }

    /// <summary>This machine, for the runner's flow: of the install's platform, so its server fits the local host.</summary>
    public static IDisposable Use() => EnvironmentInventory.UseMachine(new FakeMachine(Windows ? "windows" : "linux"));

    /// <summary>The hooks that make <see cref="Host"/> this machine's host.</summary>
    public FakeRunHooks Hooks(RunCancellation? cancellation = null) => new() { Host = _ => Host, StateWaits = false, Cancellation = cancellation };
}
