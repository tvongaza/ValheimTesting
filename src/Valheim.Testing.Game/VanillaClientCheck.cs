using System.Globalization;

namespace Valheim.Testing.Game;

/// <summary>
/// The client half of a "clients do not need it" check for a server-side mod: a client with BepInEx, ValheimCLI and the
/// mod's test adapter but not the mod (pin the mod <c>absent</c> in the client plan) goes to each place the mod touches
/// and must see nothing it cannot handle. Use <see cref="Measure"/> as, or inside, a <see cref="ClientRounds"/>
/// measurement. For each of <see cref="Points"/> (or once where the player stands, when there are none) it arrives there
/// (<see cref="PlayerPlacement.Arrive"/>, <c>{round}-vanilla-client-{n}-arrival.json</c>), waits for a complete census of
/// the objects around the player and fails on any prefab hash the client cannot resolve, naming each
/// (<c>{round}-vanilla-client-{n}.json</c>). Then, with <see cref="ClientLogs"/>, it scans the client's logs so far and
/// fails on the three quiet failures of a client without the mod: objects whose prefab is missing
/// (<c>missing-prefab-hash</c>), per-object RPCs with no handler (<c>rpc-method-missing</c>) and errors while objects
/// unload (<c>nre-remove-objects</c>) (<c>{round}-vanilla-client-logs.json</c>). Pass <see cref="LogClassifications"/> to
/// the run's teardown scan too, so the same lines fail the whole run wherever they appear.
/// </summary>
public sealed class VanillaClientCheck
{
    /// <summary>The log scan's patterns this check fails on.</summary>
    public static readonly IReadOnlyList<string> LogPatterns = ["missing-prefab-hash", "rpc-method-missing", "nre-remove-objects"];

    /// <summary>
    /// Teardown classifications that make <see cref="LogPatterns"/> failures, each with its reason (the log scan requires
    /// one). <c>nre-remove-objects</c> is already a failure by default.
    /// </summary>
    public static IReadOnlyDictionary<string, LogClassification> LogClassifications => new Dictionary<string, LogClassification>
    {
        ["missing-prefab-hash"] = new() { Severity = LogSeverity.Failure, Reason = "A vanilla client must be able to create every object the server sends it." },
        ["rpc-method-missing"] = new() { Severity = LogSeverity.Failure, Reason = "A vanilla client must have a handler for every per-object RPC it receives." },
        ["nre-remove-objects"] = new() { Severity = LogSeverity.Failure, Reason = "A vanilla client must unload the server's objects without errors." },
    };

    /// <summary>The adapter's census capability on the client, for example <c>mymod.testing/unresolved-prefabs</c>.</summary>
    public required string Capability { get; init; }
    /// <summary>The places the mod touches, one arrival and census each; empty checks only where the player stands.</summary>
    public IReadOnlyList<HeightExpectation> Points { get; init; } = [];
    /// <summary>The census radius around the player, in metres (at most 256).</summary>
    public float Radius { get; init; } = 64;
    /// <summary>Prefab names that label an unresolved hash in a failure, typically the mod's own server-side prefabs.</summary>
    public IReadOnlyList<string> KnownPrefabs { get; init; } = [];
    /// <summary>The client's logs to scan after the census, for example an owned <see cref="ClientSession.Logs"/>; null skips the scan.</summary>
    public Func<IReadOnlyList<RunLog>>? ClientLogs { get; init; }
    /// <summary>Each arrival's deadline (<see cref="PlayerPlacement.Arrive"/>): more than zero, at most 10 minutes.</summary>
    public TimeSpan ArrivalTimeout { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>How long the area around the player may take to load before the census is complete.</summary>
    public TimeSpan CensusTimeout { get; init; } = TimeSpan.FromMinutes(1);
    /// <summary>How often the census is re-read while the area loads.</summary>
    public TimeSpan CensusInterval { get; init; } = TimeSpan.FromSeconds(1);
    public CancellationToken Cancellation { get; init; }

    /// <summary>Runs the check in <paramref name="round"/>, recording each part as a round step; the first failure is rethrown.</summary>
    public void Measure(ClientRound round)
    {
        ArgumentNullException.ThrowIfNull(round);
        if (string.IsNullOrWhiteSpace(Capability)) throw new ArgumentException("Capability: name the adapter's unresolved-prefabs capability.");
        if (!(Radius > 0 && Radius <= 256)) throw new ArgumentException("Radius: 0 < radius <= 256 m.");
        if (ArrivalTimeout <= TimeSpan.Zero || ArrivalTimeout > TimeSpan.FromMinutes(10)) throw new ArgumentException("ArrivalTimeout: more than zero and at most 10 minutes (an arrival's limit).");
        int count = Math.Max(1, Points.Count);
        for (int i = 0; i < count; i++)
        {
            string n = (i + 1).ToString(CultureInfo.InvariantCulture);
            string where = "where the player stands";
            if (Points.Count > 0)
            {
                var point = Points[i];
                where = $"at vanilla-client point {n} ({F(point.X)}, {F(point.Z)})";
                round.Step("arrive " + where, () => round.Write($"vanilla-client-{n}-arrival",
                    PlayerPlacement.Arrive(round.Server, round.Client, point, ArrivalTimeout, Cancellation).Support));
            }
            round.Step("the client resolves every prefab hash " + where, () =>
            {
                var scan = UnresolvedPrefabs.WaitForComplete(round.Client, Capability, Radius, CensusTimeout, CensusInterval, Cancellation).GetAwaiter().GetResult();
                round.Write($"vanilla-client-{n}", scan);
                scan.RequireNone(KnownPrefabs);
            });
        }
        if (ClientLogs != null)
            round.Step("the client's logs show no missing prefabs, missing RPC handlers or RemoveObjects errors", () =>
            {
                var scans = ClientLogs().Select(log => LogScanner.Scan(log, LogClassifications)).ToArray();
                round.Write("vanilla-client-logs", scans);
                var problems = Problems(scans);
                if (problems.Count > 0) throw new InvalidOperationException("The client logged what a client without the mod must not: " + string.Join("; ", problems) + ".");
            });
    }

    /// <summary>
    /// One line per <see cref="LogPatterns"/> count above zero in <paramref name="scans"/> (with its first line), and one per
    /// required log that is missing. Empty when the logs are clean.
    /// </summary>
    public static IReadOnlyList<string> Problems(IEnumerable<LogFileScan> scans)
    {
        ArgumentNullException.ThrowIfNull(scans);
        var problems = new List<string>();
        foreach (var scan in scans)
        {
            if (scan.Problem != null) problems.Add($"{scan.Role}: {scan.Problem}");
            foreach (var count in scan.Counts.Where(c => LogPatterns.Contains(c.Pattern) && c.Count > 0))
                problems.Add($"{scan.Role}: {count.Pattern} x{count.Count}, first at line {count.FirstLine}: {count.First}");
        }
        return problems;
    }

    private static string F(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
