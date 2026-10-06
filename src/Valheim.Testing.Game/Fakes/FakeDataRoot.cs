namespace Valheim.Testing.Game.Fakes;

/// <summary>
/// For no-game tests only: within this scope (the current async flow), ValheimTesting's own folder on this machine is
/// <see cref="Directory"/> instead of <c>%LOCALAPPDATA%\ValheimTesting</c> (macOS <c>~/Library/Application Support/ValheimTesting</c>,
/// Linux <c>$XDG_DATA_HOME/ValheimTesting</c>). The run journal that records every copy this process makes
/// (<see cref="WorldFixture.Copy"/>, a run's runtime copies), the default host lock, leases and runs, and
/// <see cref="CliBundle.DataRoot"/> all move with it, so a test that copies a fixture never adds a run to the machine's
/// <c>valheim-test env status</c>, and a test killed mid-run never leaves one that makes <c>env preflight</c> refuse real runs.
/// A copy keeps journalling where it was made, whichever flow retires it. Never use this scope in a run with a game: the
/// machine's tools would not see that run. This scope is the only way to change the folder: there is no environment variable,
/// setter or plan field. Scopes nest: disposing one restores the one it was opened in. Dispose the innermost first; disposing
/// an outer scope while an inner one is open throws and changes nothing.
/// </summary>
public sealed class FakeDataRoot : IDisposable
{
    private readonly FakeDataRoot? _outer;
    private bool _disposed;

    public FakeDataRoot(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Name the folder that stands in for ValheimTesting's own.", nameof(directory));
        Directory = Path.GetFullPath(directory);
        _outer = LocalSteamLocator.SimulatedDataRoot.Value;
        LocalSteamLocator.SimulatedDataRoot.Value = this;
    }

    /// <summary>The folder standing in for ValheimTesting's own; the run journal is its <c>journal</c> folder.</summary>
    public string Directory { get; }

    public void Dispose()
    {
        if (_disposed) return;
        if (LocalSteamLocator.SimulatedDataRoot.Value is not { } current || !IsInside(current))
            throw new InvalidOperationException("This FakeDataRoot is not in effect where it is disposed: open and dispose it in the same flow (a using block around the code under test).");
        if (!ReferenceEquals(current, this))
            throw new InvalidOperationException("Dispose the innermost FakeDataRoot first: another scope opened inside this one is still in effect.");
        _disposed = true;
        LocalSteamLocator.SimulatedDataRoot.Value = _outer;
    }

    // Whether this scope is current or encloses the current one.
    private bool IsInside(FakeDataRoot current)
    {
        for (FakeDataRoot? scope = current; scope != null; scope = scope._outer) if (ReferenceEquals(scope, this)) return true;
        return false;
    }
}
