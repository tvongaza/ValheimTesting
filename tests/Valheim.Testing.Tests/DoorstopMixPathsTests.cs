using Valheim.Testing.Game;
using Xunit;
using Valheim.Testing.GameSessions;

// #295 acceptance: a proxy and a doorstop_config.ini from different Doorstop versions (a mod manager's proxy left beside the
// pack's configuration) are refused by every path that checks an install, with the same message: each path calls the one
// check, BepInExLoader.RequireWindowsLoader. One test per path, each naming its install; the hosted server run's own step
// and the remote client preflight are in HostedServerRunTests (AHostedWindowsServerRunRefusesAMixedDoorstopBeforeItStarts,
// ARemoteWindowsClientRefusesMixedLoaderAndInheritedStandingPinsBeforeLaunch). An install pinned before its proxy was
// replaced is refused by the loader pin as well (PinningTests).
public sealed class DoorstopMixPathsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("doorstop-mix-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    internal const string Doorstop3Proxy = "MZ targetAssembly", Doorstop4Proxy = "MZ target_assembly";
    internal const string Doorstop3Config = "[UnityDoorstop]\nenabled=true\ntargetAssembly=BepInEx\\core\\BepInEx.Preloader.dll\n";
    internal const string Doorstop4Config = "[General]\nenabled=true\ntarget_assembly=BepInEx\\core\\BepInEx.Preloader.dll\n";

    // Each mix, and the controls with one Doorstop version: proxy, configuration, and the refusal's version clause (null: accepted).
    public static TheoryData<string, string, string?> Loaders => new()
    {
        { Doorstop4Proxy, Doorstop3Config, "is Doorstop 4, which reads only [General] in doorstop_config.ini, but that file is written for Doorstop 3 ([UnityDoorstop])" },
        // The incident: a [General] section added to the pack's Doorstop 3 file did not make a Doorstop 4 proxy load BepInEx.
        { Doorstop4Proxy, Doorstop4Config + Doorstop3Config, "is Doorstop 4, which reads only [General] in doorstop_config.ini, but that file is written for Doorstop 3 ([UnityDoorstop])" },
        { Doorstop3Proxy, Doorstop4Config, "is Doorstop 3, which reads only [UnityDoorstop] in doorstop_config.ini, but that file is written for Doorstop 4 ([General])" },
        { Doorstop4Proxy, Doorstop4Config, null },
        { Doorstop3Proxy, Doorstop3Config, null },
    };

    // The one refusal, naming the install that path checked.
    internal static void AssertRefusal(Exception? error, string kind, string? expected)
    {
        if (expected == null) { Assert.Null(error); return; }
        Assert.IsAssignableFrom<InvalidOperationException>(error); // a remote check rethrows the reply as its base type
        Assert.Contains($"The {kind}'s winhttp.dll {expected}", error!.Message);
        Assert.Contains("Install winhttp.dll and doorstop_config.ini from one BepInExPack", error.Message);
    }

    private static void Loader(string root, string proxy, string config)
    {
        File.WriteAllText(Path.Combine(root, "winhttp.dll"), proxy);
        File.WriteAllText(Path.Combine(root, "doorstop_config.ini"), config);
    }

    [Theory] [MemberData(nameof(Loaders))]
    public void TheLocalClientLaunch(string proxy, string config, string? expected)
    {
        string install = Path.Combine(_root, "client");
        FakeInstalls.Client(install);
        File.WriteAllText(Path.Combine(install, GameLaunch.ClientWindowsExecutable), "game");
        Loader(install, proxy, config);
        AssertRefusal(Record.Exception(() => GameLaunch.LocalClient(install, [], null, ClientArchitecture.X64, true, ClientPlatform.Windows).ToStartInfo()), "install", expected);
    }

    [Theory] [MemberData(nameof(Loaders))]
    public void TheLocalServerLaunch(string proxy, string config, string? expected)
    {
        string runtime = Path.Combine(_root, "server");
        FakeInstalls.Server(runtime);
        File.WriteAllText(Path.Combine(runtime, GameLaunch.ServerWindowsExecutable), "server");
        Loader(runtime, proxy, config);
        AssertRefusal(Record.Exception(() => GameLaunch.LocalServer(runtime, [], null, ServerHost.Windows, GameLaunch.MacServerArchitecture).ToStartInfo()), "runtime", expected);
    }

    [Theory] [MemberData(nameof(Loaders))]
    public void ALoaderPackage(string proxy, string config, string? expected)
    {
        string package = Path.Combine(_root, "package");
        FakeInstalls.Client(package); // Its BepInEx/core, captured with the loader beside it.
        Loader(package, proxy, config);
        AssertRefusal(Record.Exception(() => BepInExLoaderPackage.Capture(package, "loader", "1")), "package", expected);
    }

    [Theory] [MemberData(nameof(Loaders))]
    public async Task TheHostedSourceInspection(string proxy, string config, string? expected)
    {
        var host = new FakeServerHost("windows-client", Path.Combine(_root, "mirror"), windows: true);
        string source = host.Local(@"C:\game\source");
        FakeInstalls.Client(source);
        File.WriteAllText(Path.Combine(source, GameLaunch.ClientWindowsExecutable), "game");
        Loader(source, proxy, config);
        AssertRefusal(await Record.ExceptionAsync(() => HostedRuntimeStage.InspectSourceAsync(host, HostedRuntimeKind.Client, @"C:\game\source", null, TimeSpan.FromSeconds(30))),
            "source install on windows-client", expected);
    }
}
