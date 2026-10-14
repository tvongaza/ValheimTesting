namespace Valheim.Testing.GameSessions;

/// <summary>
/// A detached run's control lock (#811): an exclusive handle on <c>supervisor.lock</c> in the run's journalled control directory.
/// The scheduled supervisor takes it before it adopts the run and holds it for as long as it runs; <c>env recover</c> takes it before
/// it stops or removes anything of a detached run. So a supervisor never adopts a run a recovery is clearing, and a recovery never
/// clears a run under a supervisor that adopted it after the recovery's first read. The handle is the lock (a sharing lock on
/// Windows, <c>flock</c> elsewhere): the OS releases it when its process ends, so a dead supervisor never leaves it held. The file
/// stays; it goes with the control directory.
/// </summary>
internal sealed class DetachedControlLock : IDisposable
{
    internal const string FileName = "supervisor.lock";
    private FileStream? _handle;

    private DetachedControlLock(FileStream handle) => _handle = handle;

    /// <summary>
    /// Takes the lock in <paramref name="control"/>, or returns null while another process (or another handle in this one) holds it.
    /// Throws <see cref="DirectoryNotFoundException"/> when the control directory does not exist.
    /// </summary>
    internal static DetachedControlLock? TryTake(string control)
    {
        if (!Directory.Exists(control)) throw new DirectoryNotFoundException("The detached control directory does not exist: " + control);
        try { return new(new FileStream(Path.Combine(control, FileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
        catch (IOException error) when (error is not (DirectoryNotFoundException or FileNotFoundException or PathTooLongException)) { return null; }
    }

    public void Dispose()
    {
        _handle?.Dispose();
        _handle = null;
    }
}
