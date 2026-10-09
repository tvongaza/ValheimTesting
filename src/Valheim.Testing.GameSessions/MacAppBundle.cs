using System.Text;
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
/// host answers <see cref="MacBundleState.None"/>.
/// </summary>
internal static class MacAppBundle
{
    /// <summary>Whether <paramref name="listing"/> is a macOS client install (its bundle's executable is there).</summary>
    public static bool IsMacClient(HostListing listing) => listing.Files.ContainsKey(GameLaunch.ClientMacBundle + "/Contents/MacOS/Valheim");

    /// <summary>Read only: the source bundle's seal and Gatekeeper's verdict on it.</summary>
    public static Task<MacBundleVerdict> InspectAsync(IGameHost host, string install, TimeSpan timeout, CancellationToken cancellation = default) =>
        RunAsync(host, install, repair: false, timeout, cancellation);

    /// <summary>On a disposable copy only: removes the files added to its bundle and requires macOS to accept it.</summary>
    public static Task<MacBundleVerdict> RepairAsync(IGameHost host, string copy, TimeSpan timeout, CancellationToken cancellation = default) =>
        RunAsync(host, copy, repair: true, timeout, cancellation);

    /// <summary>Why a source bundle cannot give a copy macOS launches without a dialog, or null (accepted, or only files added).</summary>
    public static string? SourceRefusal(MacBundleVerdict verdict) => verdict.State switch
    {
        MacBundleState.None or MacBundleState.Accepted or MacBundleState.Fixable => null,
        MacBundleState.Broken => $"a copy of this {GameLaunch.ClientMacBundle} would not launch without a macOS dialog ({Describe(verdict)}). " +
            "Verify the game's files in Steam (Properties, Installed Files), which restores changed or missing files; the run never repairs the source install.",
        MacBundleState.Rejected => $"macOS rejects this {GameLaunch.ClientMacBundle} even with nothing added to it ({Describe(verdict)}), so no copy of it " +
            "launches without a dialog. Use Steam's own signed and notarized build; the run never re-signs or repairs the source install.",
        _ => $"macOS's verdict on this {GameLaunch.ClientMacBundle} cannot be read ({Describe(verdict)}), so the run cannot show that a copy " +
            "launches without a dialog. codesign and spctl ship with macOS in /usr/bin; check that the host's shell finds them.",
    };

    /// <summary>The verdict in words, with the report's first lines.</summary>
    public static string Describe(MacBundleVerdict verdict) => verdict.State switch
    {
        MacBundleState.Broken => $"{verdict.Count} sealed file(s) changed or missing: {Shorten(verdict.Detail)}",
        MacBundleState.Rejected => "codesign or Gatekeeper rejects it: " + Shorten(verdict.Detail),
        MacBundleState.Unknown => "macOS's verdict cannot be read: " + verdict.Detail,
        MacBundleState.Fixable => $"{verdict.Count} file(s) added inside the bundle",
        _ => verdict.State.ToString(),
    };

    private static string Shorten(string text)
    {
        string line = string.Join("; ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3));
        return line.Length > 300 ? line[..300] + "..." : line;
    }

    private static async Task<MacBundleVerdict> RunAsync(IGameHost host, string root, bool repair, TimeSpan timeout, CancellationToken cancellation)
    {
        if (host.Shell.Kind != HostShellKind.Bash) return new(MacBundleState.None, 0, "");
        var result = (await host.RunAsync(Bash, new Dictionary<string, string>
            { ["app"] = HostPath.Join(root, GameLaunch.ClientMacBundle), ["repair"] = repair ? "1" : "" }, timeout, cancellation).ConfigureAwait(false))
            .EnsureSuccess($"Checking {GameLaunch.ClientMacBundle}'s signature on {host.Name}");
        string? line = InteractiveClient.Line(result.Stdout, "VT-BUNDLE ");
        var words = (line ?? "").Trim().Split(' ', 3);
        int count = words.Length > 1 && int.TryParse(words[1], out int n) ? n : 0;
        string detail = words.Length > 2 ? Decode(words[2]) : "";
        return words[0] switch
        {
            "none" => new(MacBundleState.None, 0, ""),
            "accepted" => new(MacBundleState.Accepted, count, detail),
            "fixable" => new(MacBundleState.Fixable, count, detail),
            "broken" => new(MacBundleState.Broken, count, detail),
            "rejected" => new(MacBundleState.Rejected, count, detail),
            "tools" => new(MacBundleState.Unknown, 0, "codesign or spctl is not available"),
            _ => throw new HostOperationException($"Unexpected reply while checking {GameLaunch.ClientMacBundle}'s signature on {host.Name}", result),
        };

        static string Decode(string encoded)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(encoded)).Trim(); }
            catch (FormatException) { return encoded; }
        }
    }

    // Shared with the one-shot runner. Variables: app (the bundle), repair ("1" on a disposable copy).
    // One line: VT-BUNDLE none | tools | accepted <removed> <b64> |
    // fixable <added> <b64 list> | broken <n> <b64 codesign report> | rejected <removed> <b64 report>. The removal is limited to
    // regular files codesign names as added, inside this bundle; nothing outside it is read or written.
    internal static readonly string Bash = MacBundleInspection.Bash;
}

/// <summary>macOS's verdict on a client bundle (<see cref="MacAppBundle"/>).</summary>
internal enum MacBundleState
{
    /// <summary>Not a macOS bundle on this host: nothing to check.</summary>
    None,
    /// <summary>The seal holds and Gatekeeper accepts it: it launches without a dialog.</summary>
    Accepted,
    /// <summary>Only files added inside the bundle break the seal: a disposable copy can be repaired.</summary>
    Fixable,
    /// <summary>A sealed file changed or is missing: no copy of it will launch without a dialog.</summary>
    Broken,
    /// <summary>After repair, codesign or Gatekeeper still rejects it.</summary>
    Rejected,
    /// <summary>The host cannot tell (no codesign or spctl).</summary>
    Unknown,
}

internal sealed record MacBundleVerdict(MacBundleState State, int Count, string Detail);
