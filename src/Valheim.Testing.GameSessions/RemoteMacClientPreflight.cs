using System.Reflection;
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

    // A read-only check on the source game before any profile is staged. A Mac client always needs an
    // explicit slice; even a universal binary must not silently fall back to Rosetta.
    internal static async Task RequireGameAsync(IGameHost host, string gameRoot, ClientArchitecture architecture,
        TimeSpan timeout, CancellationToken cancellation)
    {
        var result = (await host.RunAsync(GameSlice, new Dictionary<string, string>
        {
            ["game"] = HostPath.Join(gameRoot, "Valheim.app/Contents/MacOS/Valheim"),
        }, timeout, cancellation).ConfigureAwait(false)).EnsureSuccess($"Checking the Mac client slice on {host.Name}");
        string? slices = InteractiveClient.Line(result.Stdout, "VT-MAC-GAME ");
        if (slices == null) throw new HostOperationException($"The Mac client slice check on {host.Name} gave no verdict", result);
        string wanted = architecture == ClientArchitecture.Arm64 ? "arm64" : "x86_64";
        if (!(" " + slices + " ").Contains(" " + wanted + " ", StringComparison.Ordinal))
            throw new InvalidDataException($"The Mac client on {host.Name} has no {wanted} slice (found: {slices}).");
    }

    internal static async Task<string> SelectLoaderAsync(IGameHost host, string loaderRoot, ClientArchitecture architecture,
        TimeSpan timeout, CancellationToken cancellation)
    {
        var result = (await host.RunAsync(LoaderSlice, new Dictionary<string, string> { ["loader"] = loaderRoot },
            timeout, cancellation).ConfigureAwait(false)).EnsureSuccess($"Checking the Mac client loader on {host.Name}");
        string? available = InteractiveClient.Line(result.Stdout, "VT-MAC-DOORSTOPS ");
        if (available == null) throw new HostOperationException($"The Mac loader check on {host.Name} gave no verdict", result);
        string? choice = Select(available.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(entry => entry.Split('|', 2)), architecture);
        if (choice == null) throw new InvalidDataException($"The Mac client loader on {host.Name} has no {architecture} Doorstop library ({available}).");
        if (architecture == ClientArchitecture.Arm64)
        {
            byte[]? core = await HostClientPreflight.Read(host, HostPath.Join(loaderRoot, "BepInEx/core/MonoMod.RuntimeDetour.dll"),
                timeout, cancellation).ConfigureAwait(false);
            RequireNativeCore(core);
        }
        return choice;
    }

    internal static string SelectPackage(BepInExLoaderPackage package, ClientArchitecture architecture)
    {
        package.RequireFor(ClientPlatform.MacOS, "remote Mac client loader package");
        string? choice = GameLaunch.MacDoorstopFiles.FirstOrDefault(relative =>
            package.Files.ContainsKey(relative) && GameLaunch.MachOArchitectures(Path.Combine(package.Root,
                relative.Replace('/', Path.DirectorySeparatorChar))).Contains(architecture));
        if (choice == null) throw new InvalidDataException($"The remote Mac client loader package has no {architecture} Doorstop library.");
        if (architecture == ClientArchitecture.Arm64 && GameLaunch.MacNativeCoreProblem(package.Root) is { } problem)
            throw new InvalidDataException(problem);
        return choice;
    }

    private static string? Select(IEnumerable<string[]> entries, ClientArchitecture architecture)
    {
        string wanted = architecture == ClientArchitecture.Arm64 ? "arm64" : "x86_64";
        foreach (string relative in GameLaunch.MacDoorstopFiles)
            if (entries.Any(entry => entry.Length == 2 && entry[0] == relative &&
                (" " + entry[1] + " ").Contains(" " + wanted + " ", StringComparison.Ordinal))) return relative;
        return null;
    }

    private static void RequireNativeCore(byte[]? core)
    {
        if (core == null) throw new InvalidDataException("A native arm64 Mac client needs BepInEx's MonoMod.RuntimeDetour.dll (version 25 or newer).");
        string temporary = Path.Combine(Path.GetTempPath(), "vt-mac-core-" + Guid.NewGuid().ToString("N") + ".dll");
        try
        {
            File.WriteAllBytes(temporary, core);
            Version? version;
            try { version = AssemblyName.GetAssemblyName(temporary).Version; }
            catch (Exception error) when (error is BadImageFormatException or FileLoadException) { version = null; }
            if (version is { Major: >= 25 }) return;
            throw new InvalidDataException($"A native arm64 Mac client needs MonoMod.RuntimeDetour.dll version 25 or newer (found {version?.ToString() ?? "unreadable"}).");
        }
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
