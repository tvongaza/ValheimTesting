using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;

// A test process whose identity belongs to this machine. Use the ordinary FakeOwnedProcess when locality
// is irrelevant, and an IClientProcessIdentity-only test double when modelling a process on another host.
internal sealed class LocalFakeOwnedProcess(int id = 1) : IOwnedProcess, ILocalClientProcessIdentity
{
    private readonly FakeOwnedProcess _process = new(id);
    public int Id => _process.Id;
    public string StartFileTimeUtc => "fake-start-" + Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public bool HasExited => _process.HasExited;
    public int Stops => _process.Stops;
    public int Disposals => _process.Disposals;
    public bool IgnoreQuit { get => _process.IgnoreQuit; set => _process.IgnoreQuit = value; }
    public Func<Exception>? StopFailure { get => _process.StopFailure; set => _process.StopFailure = value; }
    public void Exit(int code) => _process.Exit(code);
    public Task<int> WaitForExitAsync(CancellationToken cancellation) => _process.WaitForExitAsync(cancellation);
    public void Stop(TimeSpan timeout) => _process.Stop(timeout);
    public ProcessStop StopCleanly(TimeSpan quit, TimeSpan kill) => _process.StopCleanly(quit, kill);
    public void Dispose() => _process.Dispose();
}
