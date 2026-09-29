using System.Diagnostics;

namespace Valheim.Testing.Game;

/// <summary>One live (not closing) extension registration as <c>cli_extensions</c> lists it.</summary>
public sealed record ExtensionInstance(string Id, string Version, string Instance);

/// <summary>
/// The steps of a hot-reload smoke test of a game-side extension (see examples/ReloadCheck): replace the extension's DLL
/// in the reloader's watched directory without it ever seeing a half-written file, then wait until the replacement has
/// registered as a new instance, or until a removed extension is gone. Waiting re-reads <c>cli_extensions</c> (read-only)
/// at an interval, because a registration announces itself nowhere else. Use a <see cref="GameActor"/> whose pins allow
/// the reload (<see cref="GameActor.WaitForEnvironment(string, TimeSpan, CancellationToken)"/> first) and rediscover
/// capabilities afterwards: the new instance's are new.
/// </summary>
public static class ExtensionReload
{
    /// <summary>The live registration of <paramref name="id"/>, or null. Two live registrations of one ID fail.</summary>
    public static ExtensionInstance? Find(GameActor actor, string id)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrEmpty(id);
        using var document = GameActor.ParseLine(actor.Execute("cli_extensions"), "EXTENSIONS ");
        ExtensionInstance? live = null;
        foreach (var item in document.RootElement.GetProperty("extensions").EnumerateArray())
        {
            if (item.GetProperty("id").GetString() != id || item.GetProperty("closing").GetBoolean()) continue;
            if (live != null) throw new InvalidOperationException("Two live registrations of " + id + ".");
            live = new ExtensionInstance(id, item.GetProperty("version").GetString() ?? "", item.GetProperty("instance").GetString() ?? "");
        }
        return live;
    }

    /// <summary>
    /// Replaces <paramref name="deployed"/> with a copy of <paramref name="artifact"/>: copied to <c>deployed.incoming</c>
    /// beside it first, then renamed over it, so a directory watcher sees one complete file.
    /// </summary>
    public static void Install(string artifact, string deployed)
    {
        ArgumentException.ThrowIfNullOrEmpty(artifact);
        ArgumentException.ThrowIfNullOrEmpty(deployed);
        string incoming = deployed + ".incoming";
        File.Copy(artifact, incoming, overwrite: true);
        File.Move(incoming, deployed, overwrite: true);
    }

    /// <summary>
    /// Waits until <paramref name="id"/> is live at <paramref name="version"/> with an instance other than
    /// <paramref name="replacing"/> (the instance before the reload; null for a first install) and returns it.
    /// </summary>
    public static async Task<ExtensionInstance> WaitForReplacement(GameActor actor, string id, string version, string? replacing,
        TimeSpan timeout, TimeSpan interval, CancellationToken cancellation = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(version);
        var found = await Until(actor, id, live => live != null && live.Version == version && live.Instance != replacing,
            $"{id} {version} registered as a new instance", timeout, interval, cancellation).ConfigureAwait(false);
        return found!;
    }

    /// <summary>Waits until no live registration of <paramref name="id"/> remains.</summary>
    public static Task WaitForRemoval(GameActor actor, string id, TimeSpan timeout, TimeSpan interval, CancellationToken cancellation = default) =>
        Until(actor, id, live => live == null, id + " unregistered", timeout, interval, cancellation);

    private static async Task<ExtensionInstance?> Until(GameActor actor, string id, Func<ExtensionInstance?, bool> done, string target,
        TimeSpan timeout, TimeSpan interval, CancellationToken cancellation)
    {
        WaitText.RequireTimeout(timeout);
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval), "Give a positive interval.");
        var clock = Stopwatch.StartNew();
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var live = Find(actor, id);
            if (done(live)) return live;
            var remaining = timeout - clock.Elapsed;
            if (remaining <= TimeSpan.Zero)
                throw new WaitTimeoutException(target, clock.Elapsed, live == null ? "no live " + id : $"{id} {live.Version} instance {live.Instance}");
            await Task.Delay(remaining < interval ? remaining : interval, cancellation).ConfigureAwait(false);
        }
    }
}
