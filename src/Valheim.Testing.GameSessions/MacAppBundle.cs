using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>
/// What macOS makes of a game client's <c>Valheim.app</c>, so a run never makes macOS show a dialog ("Valheim is damaged and
/// can't be opened", or a first-launch prompt) and kill the client (run A, #258). A launched app's code signature must seal
/// every file in the bundle: a file added inside it (BepInEx preloader logs a launch from <c>Contents/MacOS</c> left behind, a
/// mod build's <c>publicized_assemblies</c>) breaks the seal. macOS tolerates that for an install it already let run, but a
/// fresh copy at a new path is assessed again and rejected. <see cref="InspectAsync"/> reads a source install: accepted as it
/// is, fixable (only files added to the bundle), or broken (a sealed file changed or missing). <see cref="RepairAsync"/> makes a
/// disposable copy acceptable: it removes the files the seal reports as added (only inside the copy's bundle; the source
/// install is never touched), drops a quarantine attribute, and requires <c>codesign --verify --deep --strict</c> and Gatekeeper's
/// own assessment (<c>spctl -a -t exec</c>) to accept the copy before anything launches it. Bash on macOS only; every other
/// host answers <see cref="MacBundleInspection.State.None"/>.
/// </summary>
internal static class MacAppBundle
{
    /// <summary>Whether <paramref name="listing"/> is a macOS client install (its bundle's executable is there).</summary>
    public static bool IsMacClient(HostListing listing) => listing.Files.ContainsKey(GameLaunch.ClientMacBundle + "/Contents/MacOS/Valheim");

    /// <summary>Read only: the source bundle's seal and Gatekeeper's verdict on it.</summary>
    public static Task<MacBundleInspection.Verdict> InspectAsync(IGameHost host, string install, TimeSpan timeout, CancellationToken cancellation = default) =>
        RunAsync(host, install, repair: false, timeout, cancellation);

    /// <summary>On a disposable copy only: removes the files added to its bundle and requires macOS to accept it.</summary>
    public static Task<MacBundleInspection.Verdict> RepairAsync(IGameHost host, string copy, TimeSpan timeout, CancellationToken cancellation = default) =>
        RunAsync(host, copy, repair: true, timeout, cancellation);

    private static async Task<MacBundleInspection.Verdict> RunAsync(IGameHost host, string root, bool repair, TimeSpan timeout, CancellationToken cancellation)
    {
        if (host.Shell.Kind != HostShellKind.Bash) return new(MacBundleInspection.State.None, 0, "not a macOS host");
        var result = (await host.RunAsync(Bash, new Dictionary<string, string>
            { ["app"] = HostPath.Join(root, GameLaunch.ClientMacBundle), ["repair"] = repair ? "1" : "" }, timeout, cancellation).ConfigureAwait(false))
            .EnsureSuccess($"Checking {GameLaunch.ClientMacBundle}'s signature on {host.Name}");
        return MacBundleInspection.ParseReport(result.Stdout, result.Stderr);
    }

    // Shared with the one-shot runner. Variables: app (the bundle), repair ("1" on a disposable copy).
    // One line: VT-BUNDLE none | tools | accepted <removed> <b64> |
    // fixable <added> <b64 list> | broken <n> <b64 codesign report> | rejected <removed> <b64 report>. The removal is limited to
    // regular files codesign names as added, inside this bundle; nothing outside it is read or written.
    internal static readonly string Bash = MacBundleInspection.Bash;
}
