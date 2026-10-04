namespace Valheim.Testing.Game.Fakes;

/// <summary>
/// For no-game tests only: within this scope (the current async flow), a simulated game client keeps its data in
/// <see cref="Directory"/>, so <see cref="HostedWorld.DefaultSaveDirectory"/> returns it on every platform. A hosted-world
/// test can then place its fixture in a temporary folder while every rule about the client's data directory still runs,
/// including the macOS rule that refuses any other <see cref="HostWorldPlan.SaveDirectory"/>. A real macOS client always
/// uses the signed-in user's own directory (Valheim 1.0.16 ignores <c>-savedir</c> there); never use this scope in a run
/// with a game. This scope is the only way to change the default: there is no environment variable, setter or plan field.
/// Scopes nest: disposing one restores the one it was opened in. Dispose the innermost first; disposing an outer scope
/// while an inner one is open throws and changes nothing, so a stale directory never stays in effect.
/// </summary>
public sealed class FakeClientDataDirectory : IDisposable
{
    private readonly FakeClientDataDirectory? _outer;
    private bool _disposed;

    public FakeClientDataDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Name the simulated client's data directory.", nameof(directory));
        Directory = Path.GetFullPath(directory);
        _outer = HostedWorld.SimulatedClientData.Value;
        HostedWorld.SimulatedClientData.Value = this;
    }

    /// <summary>The simulated client's data directory, which holds its <c>worlds_local</c>.</summary>
    public string Directory { get; }

    public void Dispose()
    {
        if (_disposed) return;
        if (!ReferenceEquals(HostedWorld.SimulatedClientData.Value, this))
            throw new InvalidOperationException("Dispose the innermost FakeClientDataDirectory first: another scope opened inside this one is still in effect.");
        _disposed = true;
        HostedWorld.SimulatedClientData.Value = _outer;
    }
}
