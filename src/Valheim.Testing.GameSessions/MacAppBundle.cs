using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>
/// What macOS makes of a game client's <c>Valheim.app</c>, so a run never makes macOS show a dialog ("Valheim is damaged and
/// can't be opened", or a first-launch prompt) and kill the client (run A, #258). A launched app's code signature must seal
/// every file in the bundle: a file added inside it (BepInEx preloader logs a launch from <c>Contents/MacOS</c> left behind, a
/// mod build's <c>publicized_assemblies</c>) breaks the seal. macOS tolerates that for an install it already let run, which is
/// the one a profile launches in place. <see cref="InspectAsync"/> reads a source install: accepted as it is, fixable (only
/// files added to the bundle, which an install macOS already let run tolerates), or broken (a sealed file changed or missing),
/// which is refused before anything launches it. Bash on macOS only; every other host answers
/// <see cref="MacBundleInspection.State.None"/>.
/// </summary>
internal static class MacAppBundle
{
    /// <summary>Whether <paramref name="listing"/> is a macOS client install (its bundle's executable is there).</summary>
    public static bool IsMacClient(HostListing listing) => listing.Files.ContainsKey(GameLaunch.ClientMacBundle + "/Contents/MacOS/Valheim");

    /// <summary>Read only: the source bundle's seal and Gatekeeper's verdict on it.</summary>
    public static Task<MacBundleInspection.Verdict> InspectAsync(IGameHost host, string install, TimeSpan timeout, CancellationToken cancellation = default) =>
        RunAsync(host, install, timeout, cancellation);

    private static async Task<MacBundleInspection.Verdict> RunAsync(IGameHost host, string root, TimeSpan timeout, CancellationToken cancellation)
    {
        if (host.Shell.Kind != HostShellKind.Bash) return new(MacBundleInspection.State.None, 0, "not a macOS host");
        var result = (await host.RunAsync(Bash, new Dictionary<string, string>
            { ["app"] = HostPath.Join(root, GameLaunch.ClientMacBundle) }, timeout, cancellation).ConfigureAwait(false))
            .EnsureSuccess($"Checking {GameLaunch.ClientMacBundle}'s signature on {host.Name}");
        return MacBundleInspection.ParseReport(result.Stdout, result.Stderr);
    }

    // Shared with the one-shot runner. Variable: app (the bundle). One line: VT-BUNDLE none | tools | accepted 0 <b64> |
    // fixable <added> <b64 list> | broken <n> <b64 codesign report> | rejected 0 <b64 report>. Read only.
    internal static readonly string Bash = MacBundleInspection.Bash;
}
