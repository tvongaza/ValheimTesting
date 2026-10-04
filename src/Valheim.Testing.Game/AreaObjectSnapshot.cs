using System.Globalization;
using System.Text.RegularExpressions;
using valheim_cli.Testing;

namespace Valheim.Testing.Game;

/// <summary>One read-only ValheimCLI observation retained with the host role and its complete reply.</summary>
public sealed record AreaObjectCommand(string Role, string Command, DateTimeOffset AtUtc, IReadOnlyList<string> Reply);

/// <summary>
/// Optional saved-object and loaded-structure evidence for a small area. The server observes saved ZDOs and containers;
/// the client observes instantiated prefabs and current piece support. Counts are completeness checks, not assertions
/// about what a mod ought to place. Use terrain snapshots separately for height and paint.
/// </summary>
public sealed record AreaObjectSnapshot(string Site, string WorldUid, int X, int Z, int Radius,
    DateTimeOffset StartedUtc, DateTimeOffset FinishedUtc, int SavedObjects, int Containers,
    int LoadedPrefabs, int Pieces, IReadOnlyList<AreaObjectCommand> Commands)
{
    // The game raises no event for a loaded area: cli_area_ready is re-read at this interval.
    internal static readonly TimeSpan AreaReadInterval = TimeSpan.FromMilliseconds(100);
    private static readonly Regex Ready = new(@"^OK: AREA_READY (?<x>-?\d+\.\d),(?<z>-?\d+\.\d) ready=(?<ready>True|False) zone=-?\d+,-?\d+ loaded=(?<loaded>True|False) objects=\d+ without_instance=(?<missing>\d+)$", RegexOptions.CultureInvariant);
    private static readonly Regex Zdos = new(@"^OK: ZDOS_AT (?<x>-?\d+\.\d),(?<z>-?\d+\.\d) r=(?<radius>\d+\.\d) zones=(?<zones>\d+) objects=(?<count>\d+)$", RegexOptions.CultureInvariant);
    private static readonly Regex ContainersAt = new(@"^OK: CONTAINERS_AT (?<x>-?\d+\.\d),(?<z>-?\d+\.\d) r=(?<radius>\d+\.\d) containers=(?<count>\d+) unreadable=(?<unreadable>\d+)$", RegexOptions.CultureInvariant);
    private static readonly Regex Prefabs = new(@"^OK: NEARBY_PREFABS radius=(?<radius>\d+\.\d) count=(?<count>\d+)$", RegexOptions.CultureInvariant);
    private static readonly Regex Support = new(@"^OK: PIECE_SUPPORT (?<x>-?\d+\.\d),(?<z>-?\d+\.\d) r=(?<radius>\d+\.\d) pieces=(?<count>\d+) held=(?<held>\d+) unheld=(?<unheld>\d+) pending=(?<pending>\d+)$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Capture an already selected site. The client must have a ready player and loaded area at the point; the server
    /// must host the same world. Container and support reads are optional. Support is a read of the game's current value,
    /// including its reported pending/remote state; it does not settle or recompute support.
    /// </summary>
    public static AreaObjectSnapshot Capture(GameActor server, GameActor client, string site, string worldUid,
        int x, int z, int radius, TimeSpan readinessTimeout, bool includeContainers = false, bool includeSupport = false,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(client);
        if (string.IsNullOrWhiteSpace(site) || site.Length > 80 || site.Any(char.IsControl)) throw new ArgumentException("Give a short site name.", nameof(site));
        if (!long.TryParse(worldUid, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) throw new ArgumentException("Give the expected numeric world UID.", nameof(worldUid));
        if (Math.Abs((long)x) > 20000 || Math.Abs((long)z) > 20000 || radius is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(radius), "Use a centre in the supported world range and a 1..64 m radius.");
        if (readinessTimeout <= TimeSpan.Zero || readinessTimeout > TimeSpan.FromMinutes(2)) throw new ArgumentOutOfRangeException(nameof(readinessTimeout));
        if (!server.Pinned || !client.Pinned) throw new InvalidOperationException("Area snapshots require strict pins on both actors.");

        var started = DateTimeOffset.UtcNow;
        var commands = new List<AreaObjectCommand>();
        CheckWorld(server, worldUid, "server");
        CheckWorld(client, worldUid, "client");
        string last = "no area reply";
        ObservedWait.Until($"the client's area at {x},{z} ready", () =>
            {
                var reply = Lines(client, "client", $"cli_area_ready {x} {z} 0", commands);
                last = string.Join(" | ", reply);
                var match = ExactlyOneSummary(reply, Ready, x, z);
                return match.Groups["ready"].Value == "True" && match.Groups["loaded"].Value == "True" && match.Groups["missing"].Value == "0";
            }, ready => ready, readinessTimeout, AreaReadInterval, cancellation, describe: _ => last);

        var zdos = Lines(server, "server", $"cli_zdos_at {x} {z} {radius}", commands);
        int saved = Count(zdos, Zdos, "ZDO ", x, z, radius);
        int containers = -1;
        if (includeContainers)
        {
            var reply = Lines(server, "server", $"cli_containers_at {x} {z} {radius}", commands);
            containers = Count(reply, ContainersAt, "CONTAINER ", x, z, radius);
            if (int.Parse(ContainersAt.Match(reply[^1]).Groups["unreadable"].Value, CultureInfo.InvariantCulture) != 0)
                throw new InvalidOperationException("Container inventory could not be read completely.");
        }
        // The loaded client census requires a vertical centre; using the site's declared ground is explicit and avoids
        // quietly treating the saved server's ZDO positions as a client observation.
        var ground = Lines(client, "client", $"cli_ground_height {x} {z}", commands);
        var groundMatch = ExactlyOneSummary(ground, new Regex(@"^GROUND (?<x>-?\d+\.\d),(?<z>-?\d+\.\d) h=(?<height>-?\d+\.\d+)$", RegexOptions.CultureInvariant), x, z);
        double y = double.Parse(groundMatch.Groups["height"].Value, CultureInfo.InvariantCulture);
        if (!double.IsFinite(y)) throw new InvalidOperationException("Client ground height is not finite.");
        string yText = y.ToString("0.###", CultureInfo.InvariantCulture);
        var prefabs = Lines(client, "client", $"cli_prefabs_at {x} {yText} {z} {radius}", commands);
        int loaded = Count(prefabs, Prefabs, "PREFAB ", radius: radius);
        int pieces = -1;
        if (includeSupport)
        {
            var reply = Lines(client, "client", $"cli_piece_support {x} {z} {radius}", commands);
            pieces = Count(reply, Support, "SUPPORT ", x, z, radius);
            var summary = Support.Match(reply[^1]);
            int held = int.Parse(summary.Groups["held"].Value, CultureInfo.InvariantCulture);
            int unheld = int.Parse(summary.Groups["unheld"].Value, CultureInfo.InvariantCulture);
            int pending = int.Parse(summary.Groups["pending"].Value, CultureInfo.InvariantCulture);
            if (held + unheld != pieces || pending > pieces)
                throw new InvalidOperationException("Piece-support summary counts disagree.");
        }
        CheckWorld(server, worldUid, "server");
        CheckWorld(client, worldUid, "client");
        return new(site, worldUid, x, z, radius, started, DateTimeOffset.UtcNow, saved, containers, loaded, pieces, commands);
    }

    private static void CheckWorld(GameActor actor, string uid, string role)
    {
        var state = new SessionControl(actor).Read();
        if (!state.WorldReady || state.WorldUid != uid) throw new InvalidOperationException($"{role} is not ready in expected world {uid}.");
        if (role == "server" && !state.Server) throw new InvalidOperationException("Saved-object census requires the server actor.");
        if (role == "client" && (!state.LocalPlayer || !state.PlayerReady)) throw new InvalidOperationException("Loaded-object census requires a ready client player.");
    }

    private static IReadOnlyList<string> Lines(GameActor actor, string role, string command, List<AreaObjectCommand> commands)
    {
        // Recorded before judging, so a refused command is in the evidence.
        var result = actor.Execute(command, requireAccepted: false);
        string[] lines = result.Output.ToArray();
        commands.Add(new(role, command, DateTimeOffset.UtcNow, lines));
        if (!result.Accepted || lines.Length == 0)
            throw new InvalidOperationException($"{role} returned an error or incomplete reply for {command}: {string.Join(" | ", lines)}");
        return lines;
    }

    private static Match ExactlyOneSummary(IReadOnlyList<string> lines, Regex regex, int x, int z)
    {
        if (lines.Count != 1) throw new InvalidOperationException("Expected exactly one area reply.");
        var match = regex.Match(lines[0]);
        if (!match.Success || double.Parse(match.Groups["x"].Value, CultureInfo.InvariantCulture) != x ||
            double.Parse(match.Groups["z"].Value, CultureInfo.InvariantCulture) != z)
            throw new InvalidOperationException($"Area reply is incomplete or names another point: {lines[0]}");
        return match;
    }

    private static int Count(IReadOnlyList<string> lines, Regex summary, string prefix, int? x = null, int? z = null, int radius = 0)
    {
        var match = summary.Match(lines[^1]);
        if (!match.Success) throw new InvalidOperationException("Object/structure census has no complete terminator.");
        int rows = lines.Take(lines.Count - 1).Count(line => line.StartsWith(prefix, StringComparison.Ordinal));
        if (!int.TryParse(match.Groups["count"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int count) || count != rows ||
            lines.Take(lines.Count - 1).Any(line => !(line.StartsWith(prefix, StringComparison.Ordinal) || prefix == "CONTAINER " && line.StartsWith("  ITEM ", StringComparison.Ordinal))) ||
            x.HasValue && (double.Parse(match.Groups["x"].Value, CultureInfo.InvariantCulture) != x || double.Parse(match.Groups["z"].Value, CultureInfo.InvariantCulture) != z) ||
            radius != 0 && double.Parse(match.Groups["radius"].Value, CultureInfo.InvariantCulture) != radius)
            throw new InvalidOperationException("Object/structure census has an incomplete count, unexpected line, or wrong area.");
        return count;
    }
}
