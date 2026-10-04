namespace Valheim.Testing.Game;
public static class Check
{
    public static void Near(double actual, double expected, double tolerance)
    {
        if (!double.IsFinite(actual) || !double.IsFinite(expected) || !double.IsFinite(tolerance) || tolerance < 0 || Math.Abs(actual - expected) > tolerance)
            throw new InvalidOperationException($"Expected {expected} ± {tolerance}; observed {actual}.");
    }
    // Multiplicities matter: duplicate pieces are not hidden by a set comparison.
    public static void SameIdentities(IEnumerable<string> actual, IEnumerable<string> expected)
    {
        if (!actual.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(expected.OrderBy(x => x, StringComparer.Ordinal)))
            throw new InvalidOperationException("Semantic identities or their multiplicities differ.");
    }
}
