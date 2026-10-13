using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

// The host remains the authority for the game and its GUI session. A loader package, when supplied, is
// checked at its pinned source before staging; the staged loader is checked again immediately before launch.
internal static class RemoteMacClientPreflight
{
    internal static async Task RequireDesktopAsync(IGameHost host, TimeSpan timeout, CancellationToken cancellation)
    {
        var result = (await host.RunAsync(Desktop, new Dictionary<string, string>(), timeout, cancellation)
            .ConfigureAwait(false)).EnsureSuccess($"Checking the macOS console on {host.Name}");
        string? user = InteractiveClient.Line(result.Stdout, "VT-MAC-USER ");
        string? console = InteractiveClient.Line(result.Stdout, "VT-MAC-CONSOLE ");
        string? gui = InteractiveClient.Line(result.Stdout, "VT-MAC-GUI ");
        if (user == null || console == null || gui == null || !result.Stdout.Contains("VT-MAC-REGISTRY-END", StringComparison.Ordinal))
            throw new HostOperationException($"The macOS console check on {host.Name} gave no complete verdict", result);
        MacGuiSession.RequireRemoteEvidence(user, console, gui == "ready", result.Stdout);
    }

    // A read-only check on the source game before any profile is staged: the host reads the slices (lipo), the one Mac
    // game-slice decision (GameLaunch.RequireMacGameSlice) decides. Even a universal binary never falls back to Rosetta.
    internal static async Task RequireGameAsync(IGameHost host, string gameRoot, ClientArchitecture architecture,
        TimeSpan timeout, CancellationToken cancellation)
    {
        string game = HostPath.Join(gameRoot, "Valheim.app/Contents/MacOS/Valheim");
        var result = (await host.RunAsync(GameSlice, new Dictionary<string, string> { ["game"] = game },
            timeout, cancellation).ConfigureAwait(false)).EnsureSuccess($"Checking the Mac client slice on {host.Name}");
        string? slices = InteractiveClient.Line(result.Stdout, "VT-MAC-GAME ");
        if (slices == null) throw new HostOperationException($"The Mac client slice check on {host.Name} gave no verdict", result);
        if (slices == "missing") throw new FileNotFoundException($"The Mac client on {host.Name} has no executable: {game}");
        GameLaunch.RequireMacGameSlice(GameLaunch.MacSlices(slices), architecture, $"{game} on {host.Name}");
    }

    // The host lists its Doorstop libraries' slices (lipo); the one Doorstop selector (GameLaunch.SelectMacDoorstop) chooses,
    // and an arm64 launch's core is read from the host for the one native-core rule. Returns the library, relative to the loader.
    internal static async Task<string> SelectLoaderAsync(IGameHost host, string loaderRoot, ClientArchitecture architecture,
        TimeSpan timeout, CancellationToken cancellation)
    {
        var result = (await host.RunAsync(LoaderSlice, new Dictionary<string, string> { ["loader"] = loaderRoot },
            timeout, cancellation).ConfigureAwait(false)).EnsureSuccess($"Checking the Mac client loader on {host.Name}");
        string? available = InteractiveClient.Line(result.Stdout, "VT-MAC-DOORSTOPS ");
        if (available == null) throw new HostOperationException($"The Mac loader check on {host.Name} gave no verdict", result);
        var present = available.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(entry => entry.Split('|', 2))
            .Where(entry => entry.Length == 2).Select(entry => (entry[0], GameLaunch.MacSlices(entry[1]))).ToList();
        string choice = GameLaunch.SelectMacDoorstop(present, architecture, $"Mac client loader on {host.Name}");
        if (architecture == ClientArchitecture.Arm64)
        {
            byte[]? core = await HostClientPreflight.Read(host, HostPath.Join(loaderRoot, GameLaunch.MacNativeDetour),
                timeout, cancellation).ConfigureAwait(false);
            if (GameLaunch.MacNativeCoreProblem(core != null, core == null ? null : Version(core)) is { } problem)
                throw new InvalidOperationException(problem);
        }
        return choice;
    }

    // A reviewed loader package on this machine, before it is staged to the host: the same selector and core rule, over the
    // package's own Doorstop files.
    internal static string SelectPackage(BepInExLoaderPackage package, ClientArchitecture architecture)
    {
        package.RequireFor(ClientPlatform.MacOS, "remote Mac client loader package");
        var present = GameLaunch.MacDoorstopFiles.Where(package.Files.ContainsKey).Select(relative =>
            (relative, GameLaunch.MachOArchitectures(Path.Combine(package.Root, relative.Replace('/', Path.DirectorySeparatorChar))))).ToList();
        string choice = GameLaunch.SelectMacDoorstop(present, architecture, "remote Mac client loader package");
        if (architecture == ClientArchitecture.Arm64 && GameLaunch.MacNativeCoreProblem(package.Root) is { } problem)
            throw new InvalidOperationException(problem);
        return choice;
    }

    // The assembly version of a core fetched from a host, read from a temporary copy without loading it.
    private static Version? Version(byte[] core)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "vt-mac-core-" + Guid.NewGuid().ToString("N") + ".dll");
        try { File.WriteAllBytes(temporary, core); return GameLaunch.AssemblyVersion(temporary); }
        finally { File.Delete(temporary); }
    }

    internal static readonly string Desktop = """
        set -eu
        [ "$(uname -s)" = Darwin ] || { echo 'VT-MAC-GUI unsupported'; exit 4; }
        user=$(id -un); uid=$(id -u)
        printf 'VT-MAC-USER %s\n' "$user"
        printf 'VT-MAC-CONSOLE %s\n' "$(/usr/bin/stat -f %Su /dev/console)"
        if /bin/launchctl print "gui/$uid" >/dev/null 2>&1; then echo 'VT-MAC-GUI ready'; else echo 'VT-MAC-GUI absent'; fi
        /usr/sbin/ioreg -n Root -d1
        echo VT-MAC-REGISTRY-END
        """.ReplaceLineEndings("\n");

    internal static readonly string GameSlice = """
        set -eu
        [ -x "$game" ] || { echo 'VT-MAC-GAME missing'; exit 0; }
        printf 'VT-MAC-GAME %s\n' "$(/usr/bin/lipo -archs "$game")"
        """.ReplaceLineEndings("\n");

    internal static readonly string LoaderSlice = """
        set -eu
        result=
        for relative in doorstop_libs/libdoorstop_x64.dylib libdoorstop.dylib; do
          path="$loader/$relative"
          [ -f "$path" ] || continue
          archs=$(/usr/bin/lipo -archs "$path")
          result="$result$relative|$archs;"
        done
        printf 'VT-MAC-DOORSTOPS %s\n' "$result"
        """.ReplaceLineEndings("\n");
}
