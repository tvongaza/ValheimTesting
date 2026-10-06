using System.Globalization;
using System.Text.Json;

namespace Valheim.Testing.Game;

/// <summary>One prefab hash the observing process cannot resolve: how many saved objects carry it, and where the first is.</summary>
[ResultShape]
public sealed record UnresolvedPrefab(int Hash, int Count, float X, float Y, float Z);

/// <summary>
/// A census of the saved objects (ZDOs) within <see cref="Radius"/> metres of (<see cref="X"/>, <see cref="Z"/>) whose
/// prefab hash the observing process's <c>ZNetScene</c> cannot resolve. <see cref="Complete"/> once every zone the circle
/// touches was loaded there; <see cref="Scanned"/> counts the objects read, leaving out the observing player's own object,
/// and <see cref="WithoutPrefab"/> those without a prefab (which the game creates nothing for on purpose).
/// </summary>
public sealed record UnresolvedPrefabScan(bool Complete, float X, float Z, float Radius, int Zones, int ZonesLoaded, int Scanned, int WithoutPrefab,
    IReadOnlyList<UnresolvedPrefab> Unresolved)
{
    /// <summary>
    /// Throws unless the census is complete, read at least one object and found no unresolved hash. The message names
    /// every unresolved hash with its count and first position, and the prefab name when one of
    /// <paramref name="knownPrefabs"/> (for example the server-side mod's own prefabs) hashes to it: the client itself
    /// cannot name a prefab it does not have.
    /// </summary>
    public void RequireNone(IEnumerable<string>? knownPrefabs = null)
    {
        if (!Complete) throw new InvalidOperationException($"Incomplete census: {ZonesLoaded} of {Zones} zones loaded around ({F(X)}, {F(Z)}).");
        // The adapter leaves out the observing player's own object (a client always holds it), so an empty census means the
        // area delivered nothing to judge.
        if (Scanned == 0) throw new InvalidOperationException($"The census around ({F(X)}, {F(Z)}) read no objects; an empty census proves nothing.");
        if (Unresolved.Count == 0) return;
        var names = knownPrefabs?.ToArray();
        throw new InvalidOperationException($"{Unresolved.Sum(u => u.Count)} object(s) within {F(Radius)} m of ({F(X)}, {F(Z)}) have {Unresolved.Count} prefab hash(es) this process cannot resolve: " +
            string.Join("; ", Unresolved.Select(u => $"{u.Hash}{(StableHash.Name(u.Hash, names) is string name ? " (" + name + ")" : "")} x{u.Count}, first at ({F(u.X)}, {F(u.Y)}, {F(u.Z)})")) + ".");
    }

    private static string F(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>
/// Reads the census of prefab hashes a process cannot resolve, through an adapter's <c>UnresolvedPrefabs.Command()</c>
/// (Valheim.Testing.Adapter), around that process's reference position (the local player on a client). On a client without
/// a server-side mod, an object whose prefab only the mod registers is never created there: Valheim 1.0.16 logs "Missing
/// prefab hash" and skips it. <see cref="VanillaClientCheck"/> runs it as a scenario step.
/// </summary>
public static class UnresolvedPrefabs
{
    public const string Source = "unresolved-prefabs";

    /// <summary>
    /// One census, complete or not, through <paramref name="capabilityPath"/> (for example
    /// <c>mymod.testing/unresolved-prefabs</c>). On a client keep <paramref name="radius"/> within the area it loads, about
    /// near simulation distance x 64 + 32 m (160 m at default settings): a larger circle never becomes complete there.
    /// </summary>
    public static UnresolvedPrefabScan Read(GameActor actor, string capabilityPath, float radius = 64)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!(radius > 0 && radius <= 256)) throw new ArgumentOutOfRangeException(nameof(radius), "0 < radius <= 256 m.");
        var observation = actor.Observe(actor.RequireCapability(capabilityPath), radius.ToString("R", CultureInfo.InvariantCulture));
        if (observation.Source != Source) throw new InvalidOperationException("Wrong observation layer: " + observation.Source + ".");
        var scan = Parse(observation.Data);
        if (scan.Complete != observation.Complete) throw new InvalidOperationException("A census that disagrees with itself about completeness.");
        return scan;
    }

    /// <summary>
    /// Waits until a census is complete and has read at least one object (the area around the player has loaded), at most
    /// <paramref name="interval"/> apart, and returns it; unresolved hashes do not end the wait early, they are the answer.
    /// </summary>
    public static Task<UnresolvedPrefabScan> WaitForComplete(GameActor actor, string capabilityPath, float radius, TimeSpan timeout, TimeSpan interval, CancellationToken cancellation = default) =>
        ObservedWait.UntilAsync("a complete census of unresolved prefab hashes", () => Read(actor, capabilityPath, radius),
            scan => scan.Complete && scan.Scanned > 0, timeout, interval, cancellation,
            describe: scan => $"{scan.ZonesLoaded} of {scan.Zones} zones loaded, {scan.Scanned} objects read");

    /// <summary>Parses census data; anything malformed throws rather than reading as "nothing unresolved".</summary>
    public static UnresolvedPrefabScan Parse(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("source", out var source) || source.GetString() != Source ||
            !data.TryGetProperty("complete", out var complete) || complete.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidOperationException("Not an unresolved-prefab census.");
        var unresolved = data.GetProperty("unresolved").EnumerateArray().Select(entry => new UnresolvedPrefab(entry.GetProperty("hash").GetInt32(),
            entry.GetProperty("count").GetInt32() is var count and > 0 ? count : throw new InvalidOperationException("An unresolved hash without objects."),
            entry.GetProperty("x").GetSingle(), entry.GetProperty("y").GetSingle(), entry.GetProperty("z").GetSingle())).ToArray();
        if (unresolved.Any(u => u.Hash == 0) || unresolved.Select(u => u.Hash).Distinct().Count() != unresolved.Length)
            throw new InvalidOperationException("A census that lists hash 0 or one hash twice.");
        int zones = data.GetProperty("zones").GetInt32(), loaded = data.GetProperty("zonesLoaded").GetInt32(), scanned = data.GetProperty("scanned").GetInt32();
        if (zones < 1 || loaded < 0 || loaded > zones || scanned < 0 || unresolved.Sum(u => u.Count) > scanned)
            throw new InvalidOperationException("A census whose counts do not add up.");
        return new UnresolvedPrefabScan(complete.GetBoolean(), data.GetProperty("x").GetSingle(), data.GetProperty("z").GetSingle(), data.GetProperty("radius").GetSingle(),
            zones, loaded, scanned, data.GetProperty("withoutPrefab").GetInt32(), unresolved);
    }
}
