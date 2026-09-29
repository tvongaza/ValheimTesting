namespace Valheim.Testing.Game;

/// <summary>A placed world object as observed or expected: prefab hash, position (x, y, z) and rotation quaternion (x, y, z, w).</summary>
public sealed record PlacedObject(int PrefabHash, double[] Position, double[] Rotation);

/// <summary>
/// One-to-one matching of placed objects by prefab, position and rotation. Counts must agree and every expected object
/// needs its own observed object: stacked identical prefabs cannot swap by sharing a horizontal position, and a
/// duplicate cannot stand in for a missing object. Rotation compares the quaternion angle, so q and -q are the same.
/// </summary>
public static class TransformMatch
{
    public static void Match(IReadOnlyList<PlacedObject> actual, IReadOnlyList<PlacedObject> expected, double positionTolerance, double angleToleranceDegrees)
    {
        if (positionTolerance < 0 || !double.IsFinite(positionTolerance) || angleToleranceDegrees < 0 || !double.IsFinite(angleToleranceDegrees)) throw new ArgumentException("Invalid tolerance.");
        if (actual.Count != expected.Count) throw new InvalidOperationException($"Expected {expected.Count} objects; observed {actual.Count}.");
        foreach (var placed in actual.Concat(expected))
            if (placed.Position.Length != 3 || placed.Rotation.Length != 4 || placed.Position.Concat(placed.Rotation).Any(x => !double.IsFinite(x)) || placed.Rotation.Sum(x => x * x) < 1e-12)
                throw new InvalidOperationException("Invalid object transform.");
        var assigned = Enumerable.Repeat(-1, actual.Count).ToArray();
        bool Matches(PlacedObject a, PlacedObject b)
        {
            if (a.PrefabHash != b.PrefabHash || Math.Sqrt(a.Position.Zip(b.Position, (x, y) => (x - y) * (x - y)).Sum()) > positionTolerance) return false;
            double dot = Math.Abs(a.Rotation.Zip(b.Rotation, (x, y) => x * y).Sum()) / Math.Sqrt(a.Rotation.Sum(x => x * x) * b.Rotation.Sum(x => x * x));
            return 2 * Math.Acos(Math.Min(1, dot)) * 180 / Math.PI <= angleToleranceDegrees;
        }
        // Augmenting paths (bipartite matching): a greedy first fit could strand a later requirement.
        bool Assign(int requirement, bool[] visited)
        {
            for (int i = 0; i < actual.Count; i++)
            {
                if (visited[i] || !Matches(actual[i], expected[requirement])) continue;
                visited[i] = true;
                if (assigned[i] < 0 || Assign(assigned[i], visited)) { assigned[i] = requirement; return true; }
            }
            return false;
        }
        for (int requirement = 0; requirement < expected.Count; requirement++)
            if (!Assign(requirement, new bool[actual.Count])) throw new InvalidOperationException("Object identity or transform mismatch.");
    }
}

/// <summary>Exact sample coverage: counts alone never establish that the intended samples were measured.</summary>
public static class Coverage
{
    /// <summary>
    /// Pairs each observed row with its expected entry by identity. An unknown or repeated identity, and any expected
    /// identity left unobserved, is refused. Returns the pairs in observation order.
    /// </summary>
    public static IReadOnlyList<(TKey Key, TExpected Expected, TRow Row)> Exact<TKey, TExpected, TRow>(
        IReadOnlyDictionary<TKey, TExpected> expected, IEnumerable<TRow> rows, Func<TRow, TKey> identity) where TKey : notnull
    {
        var remaining = new Dictionary<TKey, TExpected>(expected);
        var pairs = new List<(TKey, TExpected, TRow)>();
        foreach (var row in rows)
        {
            var key = identity(row);
            if (!remaining.Remove(key, out var wanted)) throw new InvalidOperationException($"Unexpected or duplicate sample identity: {key}.");
            pairs.Add((key, wanted, row));
        }
        if (remaining.Count != 0) throw new InvalidOperationException($"{remaining.Count} expected samples were not observed, for example {remaining.Keys.First()}.");
        return pairs;
    }
    /// <summary><see cref="Exact{TKey,TExpected,TRow}"/> for a set of identities with no expected value.</summary>
    public static IReadOnlyList<TRow> Exact<TKey, TRow>(IEnumerable<TKey> expected, IEnumerable<TRow> rows, Func<TRow, TKey> identity) where TKey : notnull =>
        Exact(expected.ToDictionary(k => k, _ => true), rows, identity).Select(p => p.Row).ToList();
}
