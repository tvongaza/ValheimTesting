using System.Globalization;
using System.Text.Json;

namespace Valheim.Testing.Game;

/// <summary>A zone as the game numbers it: 64 m square, zone (0, 0) centred on the world origin.</summary>
public readonly record struct ZoneId(int X, int Z)
{
    public const float Size = 64;

    /// <summary>The zone holding the point (<paramref name="x"/>, <paramref name="z"/>), as the game computes it in 1.0.16.</summary>
    public static ZoneId Of(float x, float z) =>
        new((int)Math.Floor((float)(((double)x + 32.0) / 64.0)), (int)Math.Floor((float)(((double)z + 32.0) / 64.0)));

    /// <summary>Every zone that the square of half-width <paramref name="radius"/> round the point touches.</summary>
    public static IReadOnlyList<ZoneId> Around(float x, float z, float radius)
    {
        if (!float.IsFinite(x) || !float.IsFinite(z) || !float.IsFinite(radius) || radius < 0) throw new ArgumentException("Give a finite point and a nonnegative radius.");
        ZoneId min = Of(x - radius, z - radius), max = Of(x + radius, z + radius);
        var zones = new List<ZoneId>();
        for (int zz = min.Z; zz <= max.Z; zz++)
            for (int zx = min.X; zx <= max.X; zx++) zones.Add(new(zx, zz));
        return zones;
    }

    /// <summary>The larger of the x and z zone distances: how many rings of zones separate the two.</summary>
    public int Rings(ZoneId other) => Math.Max(Math.Abs(X - other.X), Math.Abs(Z - other.Z));

    /// <summary>The adapter's argument: <c>x,z</c>.</summary>
    public override string ToString() => X.ToString(CultureInfo.InvariantCulture) + "," + Z.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// The client's simulation distance, as the game syncs it with the server: <see cref="Near"/> rings of zones round the
/// player's zone are loaded with their terrain and every object, and distant objects (large trees, locations) stay up to
/// <see cref="Near"/> + <see cref="Far"/> rings. <see cref="Classic"/> areas are squares; the others leave out the corner
/// zones by distance. The graphics setting picks it (near 1 to 6 and beyond, far 2); the server lowers a client's to its own.
/// </summary>
public sealed record SimulationRange(int Near, int Far, bool Classic)
{
    /// <summary>Rings of zones within which the game can still keep an object of a zone on this client.</summary>
    public int Total => Near + Far;

    /// <summary>
    /// Whether the game keeps <paramref name="zone"/>'s terrain and nearby objects loaded while the player is in
    /// <paramref name="center"/>: within <see cref="Near"/> rings, and in a non-classic area also within
    /// <see cref="Near"/> × 64 + 32 m between the zones' centres, as the game decides it in 1.0.16.
    /// </summary>
    public bool KeepsLoaded(ZoneId center, ZoneId zone)
    {
        if (center.Rings(zone) > Near) return false;
        if (Classic) return true;
        double dx = (double)(center.X - zone.X) * ZoneId.Size, dz = (double)(center.Z - zone.Z) * ZoneId.Size, reach = Near * ZoneId.Size + ZoneId.Size / 2;
        return dx * dx + dz * dz < reach * reach;
    }
}

/// <summary>One zone as the client holds it: its terrain, the object instances in it and the saved objects that have none.</summary>
/// <param name="Instances">Object instances whose position is in the zone, distant ones included.</param>
/// <param name="NearInstances">Those not marked distant: while any is there, the game keeps the zone's terrain.</param>
/// <param name="Saved">Saved objects of known prefabs that this client holds for the zone.</param>
/// <param name="WithoutInstance">Of those, the ones not instantiated.</param>
[ResultShape]
public sealed record ZoneState(ZoneId Zone, bool TerrainLoaded, int Instances, int NearInstances, int Saved, int WithoutInstance)
{
    /// <summary>Nothing of the zone is in the scene: no terrain and no object instance, distant ones included.</summary>
    public bool Unloaded => !TerrainLoaded && Instances == 0;
    /// <summary>The terrain is loaded and every saved object of a known prefab has its instance.</summary>
    public bool Loaded => TerrainLoaded && WithoutInstance == 0;
    public override string ToString() => $"zone {Zone}: terrain {(TerrainLoaded ? "loaded" : "not loaded")}, {Instances} instance(s) ({NearInstances} near), {WithoutInstance} of {Saved} saved object(s) without an instance";
}

/// <summary>
/// One reading of the adapter's zone observation (<c>ZonePresence.Command()</c> in Valheim.Testing.Adapter): the zone of the
/// client's reference position (where its player is), its simulation distance, and the requested zones. <see cref="Data"/>
/// is the reading as the adapter returned it, for evidence.
/// </summary>
public sealed record ZoneReading(ZoneId Reference, SimulationRange Range, IReadOnlyList<ZoneState> Zones, JsonElement Data)
{
    public const string Source = "zone-presence";

    /// <summary>Reads <paramref name="zones"/> through <paramref name="capability"/>; incomplete or malformed readings throw.</summary>
    public static ZoneReading Read(GameActor client, Capability capability, IReadOnlyList<ZoneId> zones)
    {
        ArgumentNullException.ThrowIfNull(client);
        var observation = client.ObserveComplete(capability, Source, [.. zones.Select(zone => zone.ToString())]);
        return Parse(observation.Data, zones);
    }

    /// <summary>Parses a reading and requires exactly the <paramref name="zones"/> asked for, each once.</summary>
    public static ZoneReading Parse(JsonElement data, IReadOnlyList<ZoneId> zones)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("source", out var source) || source.GetString() != Source ||
            !data.TryGetProperty("complete", out var complete) || complete.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("Not a complete zone reading.");
        var reference = data.GetProperty("reference");
        var range = data.GetProperty("simulation");
        var parsed = new SimulationRange(range.GetProperty("near").GetInt32(), range.GetProperty("far").GetInt32(), range.GetProperty("classic").GetBoolean());
        if (parsed.Near < 0 || parsed.Far < 0) throw new InvalidOperationException("The zone reading reports a negative simulation distance.");
        var states = data.GetProperty("zones").EnumerateArray().Select(z => new ZoneState(new ZoneId(z.GetProperty("x").GetInt32(), z.GetProperty("z").GetInt32()),
            z.GetProperty("terrainLoaded").GetBoolean(), z.GetProperty("instances").GetInt32(), z.GetProperty("nearInstances").GetInt32(),
            z.GetProperty("saved").GetInt32(), z.GetProperty("withoutInstance").GetInt32())).ToArray();
        if (states.Length != zones.Count || !states.Select(s => s.Zone).OrderBy(z => (z.X, z.Z)).SequenceEqual(zones.OrderBy(z => (z.X, z.Z))))
            throw new InvalidOperationException($"The zone reading lists {string.Join(" ", states.Select(s => s.Zone))}, not the zones asked for ({string.Join(" ", zones)}).");
        if (states.Any(s => s.Instances < 0 || s.NearInstances < 0 || s.NearInstances > s.Instances || s.Saved < 0 || s.WithoutInstance < 0 || s.WithoutInstance > s.Saved))
            throw new InvalidOperationException("The zone reading has inconsistent counts: " + data.GetRawText());
        return new(new ZoneId(reference.GetProperty("x").GetInt32(), reference.GetProperty("z").GetInt32()), parsed, states, data.Clone());
    }

    public override string ToString() => $"player in zone {Reference}, simulation near {Range.Near} far {Range.Far}{(Range.Classic ? " classic" : "")}; " + string.Join("; ", Zones);
}

/// <summary>What <see cref="ZoneCycle.Run(GameActor, GameActor, CancellationToken)"/> observed, in order.</summary>
[ResultShape]
public sealed record ZoneCycleResult(ZoneReading Before, JsonElement AwayArrival, ZoneReading Unloaded, TimeSpan UnloadTook, JsonElement BackArrival, ZoneReading Reloaded, TimeSpan ReloadTook);

/// <summary>
/// Leave the area and come back: the lifecycle event in which the game destroys every object of a zone the player has
/// left and recreates it from its saved data on return. A mod's object that keeps state only in a component field loses
/// it here, and an object whose teardown throws shows up as <c>ZNetScene.RemoveObjects</c> errors in the client's log.
/// <para>
/// <see cref="Run(GameActor, GameActor, CancellationToken)"/> reads the <see cref="Zones"/> through the adapter's zone
/// observation and requires them loaded, then has the protected player arrive at <see cref="Away"/>
/// (<see cref="PlayerPlacement.Arrive"/>: one teleport by the server, arrival read on the client, never flying) and waits
/// until the client holds nothing of those zones: no terrain and no object instance. Then the player arrives at
/// <see cref="Back"/> and it waits until the zones are loaded again, every saved object instantiated. Re-observe the mod's
/// objects afterwards and compare them with what was there before; scan the client's log for teardown errors.
/// </para>
/// <para>
/// How far is far enough comes from the client's own simulation distance, read before leaving: in 1.0.16 a client keeps a
/// zone's objects while the player is within near + far rings of it (the distant ones that far out, the rest within near
/// rings) and its terrain within near rings, dropping the terrain once it has been out of that range for 4 s and its last
/// near object has gone. So <see cref="Away"/>'s zone must be at least near + far + 1 rings from every zone of interest;
/// closer is refused before anything moves, and a player that settles closer fails. Each wait reads the client at <see cref="Interval"/> until
/// <see cref="StepTimeout"/>; expiry names the zones and the last reading. The teleports are never repeated.
/// </para>
/// </summary>
public sealed class ZoneCycle
{
    /// <summary>The adapter's zone observation, for example <c>mymod.testing/zones</c>.</summary>
    public required string Capability { get; init; }
    /// <summary>The zones whose objects must unload and come back: 1 to 64, all different.</summary>
    public required IReadOnlyList<ZoneId> Zones { get; init; }
    /// <summary>Declared dry ground far enough away that the client unloads every zone of interest.</summary>
    public required HeightExpectation Away { get; init; }
    /// <summary>Declared dry ground to return to, within the loaded range of every zone of interest (usually the measurement point).</summary>
    public required HeightExpectation Back { get; init; }
    /// <summary>How long each of the four waits may take: arriving away, the unload, arriving back and the reload.</summary>
    public required TimeSpan StepTimeout { get; init; }
    /// <summary>How often the zones are re-read while waiting.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Rings of zones between the away point and every zone of interest that make the client drop them all.</summary>
    public static int RingsToLeave(SimulationRange range) => (range ?? throw new ArgumentNullException(nameof(range))).Total + 1;

    /// <summary>
    /// Runs the cycle within a <see cref="ClientRounds"/> measurement: each part is a <c>{round}: ...</c> step, and the
    /// readings so far are written to <c>{round}-{evidence}.json</c> (default <c>zone-cycle</c>) whether the cycle passes or
    /// fails. Name each cycle's evidence differently to run more than one in a round.
    /// </summary>
    public ZoneCycleResult Run(ClientRound round, CancellationToken cancellation = default, string evidence = "zone-cycle")
    {
        ArgumentNullException.ThrowIfNull(round);
        if (!ScenarioReport.ValidKind(evidence)) throw new ArgumentException("Name the evidence with 1-40 lower-case letters, digits or hyphens.", nameof(evidence));
        Validate();
        ZoneReading? before = null, unloaded = null, reloaded = null;
        JsonElement? away = null, back = null;
        TimeSpan? unloadTook = null, reloadTook = null;
        void Write() => round.Write(evidence, new
        {
            zones = Zones.Select(z => z.ToString()).ToArray(), away = Away, back = Back, before = before?.Data, awayArrival = away,
            unloaded = unloaded?.Data, unloadSeconds = unloadTook?.TotalSeconds, backArrival = back, reloaded = reloaded?.Data, reloadSeconds = reloadTook?.TotalSeconds,
        });
        try
        {
            var capability = round.Client.RequireCapability(Capability);
            round.Step("the zones of interest are loaded before leaving", () => before = Loaded(round.Client, capability));
            round.Step($"leave the area for ({Away.X}, {Away.Z})", () => away = Leave(round.Server, round.Client, before!, cancellation));
            round.Step("the client unloads the zones", () => { var (reading, took) = WaitFor(round.Client, capability, unload: true, cancellation); unloaded = reading; unloadTook = took; });
            round.Step($"return to ({Back.X}, {Back.Z})", () => back = PlayerPlacement.Arrive(round.Server, round.Client, Back, StepTimeout, cancellation).Support);
            round.Step("the client loads the zones again", () => { var (reading, took) = WaitFor(round.Client, capability, unload: false, cancellation); reloaded = reading; reloadTook = took; });
        }
        catch
        {
            try { Write(); } catch (IOException) { } // The failure on its way out is the one to report.
            throw;
        }
        Write();
        return new(before!, away!.Value, unloaded!, unloadTook!.Value, back!.Value, reloaded!, reloadTook!.Value);
    }

    /// <summary>Runs the cycle for the only player on <paramref name="server"/>, joined and protected on <paramref name="client"/>.</summary>
    public ZoneCycleResult Run(GameActor server, GameActor client, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(client);
        Validate();
        var capability = client.RequireCapability(Capability);
        var before = Loaded(client, capability);
        var away = Leave(server, client, before, cancellation);
        var (unloaded, unloadTook) = WaitFor(client, capability, unload: true, cancellation);
        var back = PlayerPlacement.Arrive(server, client, Back, StepTimeout, cancellation).Support;
        var (reloaded, reloadTook) = WaitFor(client, capability, unload: false, cancellation);
        return new(before, away, unloaded, unloadTook, back, reloaded, reloadTook);
    }

    private ZoneReading Loaded(GameActor client, Capability capability)
    {
        var reading = ZoneReading.Read(client, capability, Zones);
        var missing = reading.Zones.Where(z => !z.Loaded).ToArray();
        if (missing.Length != 0)
            throw new InvalidOperationException($"{missing.Length} zone(s) of interest are not loaded before leaving, so their unloading would prove nothing: {string.Join("; ", missing)}. Nothing was teleported.");
        RequireFarEnough(reading.Range, ZoneId.Of(Away.X, Away.Z), "The away point");
        return reading;
    }

    private JsonElement Leave(GameActor server, GameActor client, ZoneReading before, CancellationToken cancellation)
    {
        var arrived = PlayerPlacement.Arrive(server, client, Away, StepTimeout, cancellation).Support;
        // Arrival allows 2 m, which can cross into the next zone: where the client's player is decides, not the declared point.
        RequireFarEnough(before.Range, ZoneId.Of(arrived.GetProperty("x").GetSingle(), arrived.GetProperty("z").GetSingle()), "The player");
        return arrived;
    }

    private void RequireFarEnough(SimulationRange range, ZoneId at, string who)
    {
        int needed = RingsToLeave(range);
        var close = Zones.Where(zone => zone.Rings(at) < needed).ToArray();
        if (close.Length != 0)
            throw new InvalidOperationException($"{who} is in zone {at}, {close.Min(zone => zone.Rings(at))} ring(s) from zone {close.MinBy(zone => zone.Rings(at))}; with the client's simulation distance " +
                $"(near {range.Near}, far {range.Far}) nothing of a zone is unloaded closer than {needed} rings ({needed * (int)ZoneId.Size} m in x or z). Choose an away point at least that far from every zone of interest.");
    }

    private (ZoneReading, TimeSpan) WaitFor(GameActor client, Capability capability, bool unload, CancellationToken cancellation)
    {
        var done = ObservedWait.RunBlocking(unload ? $"the client to unload zone(s) {string.Join(" ", Zones)} (no terrain, no object instance)"
                : $"the client to load zone(s) {string.Join(" ", Zones)} again (terrain, and an instance for every saved object)",
            _ =>
            {
                var reading = ZoneReading.Read(client, capability, Zones);
                if (unload) RequireFarEnough(reading.Range, reading.Reference, "The player");
                return reading;
            },
            reading => reading.Zones.All(z => unload ? z.Unloaded : z.Loaded), StepTimeout, Interval, cancellation,
            describe: reading => $"still {(unload ? "loaded" : "not loaded")}: {string.Join("; ", reading.Zones.Where(z => unload ? !z.Unloaded : !z.Loaded))}; player in zone {reading.Reference}. The teleport was not repeated");
        return (done.Value, done.Elapsed);
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(Capability) || Capability.Split('/').Length != 2) throw new ArgumentException("Capability: name the adapter's zone observation, owner/name.");
        if (Zones is not { Count: >= 1 and <= 64 }) throw new ArgumentException("Zones: name 1 to 64 zones of interest.");
        if (Zones.Distinct().Count() != Zones.Count) throw new ArgumentException("Zones: each zone once.");
        if (Zones.Any(z => Math.Abs(z.X) > 255 || Math.Abs(z.Z) > 255)) throw new ArgumentException("Zones: outside the world (zone coordinates beyond ±255).");
        if (StepTimeout <= TimeSpan.Zero || StepTimeout > TimeSpan.FromMinutes(10)) throw new ArgumentException("StepTimeout: give each wait an explicit, positive timeout of at most 10 minutes (an arrival's limit).");
        if (Interval <= TimeSpan.Zero) throw new ArgumentException("Interval: give a positive interval.");
        TerrainProbe.Validate("loaded-ground", "away point", [Away], .3f);
        TerrainProbe.Validate("loaded-ground", "return point", [Back], .3f);
    }
}
