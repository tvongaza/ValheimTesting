using System.Diagnostics;

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
    public static async Task<T> Eventually<T>(Func<T> observe, Func<T, bool> matches, TimeSpan timeout, TimeSpan interval, CancellationToken cancellation = default)
    {
        if (timeout <= TimeSpan.Zero || interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        var clock = Stopwatch.StartNew();
        while (true)
        {
            cancellation.ThrowIfCancellationRequested(); T value = observe(); cancellation.ThrowIfCancellationRequested();
            if (clock.Elapsed <= timeout && matches(value)) return value;
            var remaining = timeout - clock.Elapsed;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("Observation did not satisfy the condition before the deadline.");
            await Task.Delay(remaining < interval ? remaining : interval, cancellation);
        }
    }
}
