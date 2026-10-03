using System.Globalization;

namespace Valheim.Testing.Game;

/// <summary>
/// One ValheimCLI teleport trace, in milliseconds from arming the trace. A phase that did not occur is -1.
/// The raw line is retained for audit; the numeric fields are suitable for comparisons and reports.
/// </summary>
public sealed record TeleportTrace(
    int Id, bool Distant, long RequestedMs, long MovedMs, long AreaReadyMs, long FloorReadyMs,
    long DoneMs, bool FloorAtDone, float FinalX, float FinalY, float FinalZ, string Raw)
{
    /// <summary>Parse one complete trace from the one armed teleport, refusing missing, duplicate or contradictory fields.</summary>
    public static TeleportTrace Parse(string line, int expectedId)
    {
        const string prefix = "OK: TELEPORT_TRACE ";
        if (expectedId < 1 || line == null || !line.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException("The client returned no complete teleport trace for the armed hop.");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string token in line[prefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = token.IndexOf('=');
            if (equals < 1 || equals == token.Length - 1 || !fields.TryAdd(token[..equals], token[(equals + 1)..]))
                throw new InvalidDataException("The teleport trace has a malformed or repeated field: " + line);
        }
        string[] required = ["id", "distant", "requestedMs", "movedMs", "areaReadyMs", "floorReadyMs", "doneMs", "floorAtDone", "final"];
        if (fields.Count != required.Length || required.Any(key => !fields.ContainsKey(key)))
            throw new InvalidDataException("The teleport trace is missing a required field or has an unknown field: " + line);
        if (!int.TryParse(fields["id"], NumberStyles.None, CultureInfo.InvariantCulture, out int id) || id != expectedId ||
            !bool.TryParse(fields["distant"], out bool distant) || !bool.TryParse(fields["floorAtDone"], out bool floorAtDone))
            throw new InvalidDataException("The teleport trace identity or boolean fields are invalid: " + line);
        long Milliseconds(string key)
        {
            if (!long.TryParse(fields[key], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value))
                throw new InvalidDataException("The teleport trace has invalid " + key + ": " + line);
            return value;
        }
        long requested = Milliseconds("requestedMs"), moved = Milliseconds("movedMs"), area = Milliseconds("areaReadyMs");
        long floor = Milliseconds("floorReadyMs"), done = Milliseconds("doneMs");
        string[] final = fields["final"].Split(',');
        if (final.Length != 3 ||
            !float.TryParse(final[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
            !float.TryParse(final[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) ||
            !float.TryParse(final[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z) ||
            !float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
            throw new InvalidDataException("The teleport trace has an invalid final position: " + line);
        bool InHop(long value) => value == -1 || value >= requested && value <= done;
        if (requested < 0 || done < requested || !InHop(moved) || !InHop(area) || !InHop(floor) ||
            (floor >= 0) != floorAtDone || floor >= 0 && (area < 0 || floor < area))
            throw new InvalidDataException("The teleport trace has contradictory phase times: " + line);
        return new TeleportTrace(id, distant, requested, moved, area, floor, done, floorAtDone, x, y, z, line);
    }
}
