using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>A host-lock claim whose exact owner is journalled before the atomic claim and whose release is journalled after it.</summary>
internal sealed class JournaledHostLock : IAsyncDisposable
{
    private readonly IGameHost _host;
    private readonly RunJournal _journal;
    private readonly string _journalDirectory;
    private readonly string _actor;
    private readonly TimeSpan _timeout;
    private readonly HostLock _held;
    private readonly SemaphoreSlim _releaseGate = new(1, 1);
    private int _released;

    private JournaledHostLock(IGameHost host, RunJournal journal, string journalDirectory, string actor, TimeSpan timeout, HostLock held)
    {
        _host = host;
        _journal = journal;
        _journalDirectory = journalDirectory;
        _actor = actor;
        _timeout = timeout;
        _held = held;
    }

    internal string HostName => _held.HostName;
    internal string Path => _held.Path;
    internal string Owner => _held.Owner;

    internal static async Task<JournaledHostLock> AcquireAsync(IGameHost host, string lockPath, string owner, RunJournal journal,
        string journalDirectory, string actor, TimeSpan timeout, CancellationToken cancellation = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        if (owner.Length > 200) throw new ArgumentException("An owner is at most 200 characters; a unique id is added to it.", nameof(owner));
        string claimant = owner + " [" + Guid.NewGuid().ToString("N") + "]";
        JournalEntry Entry(string kind) => JournalEntry.Of(kind, ("lock", lockPath), ("claimant", claimant));

        await journal.AppendAsync(host, journalDirectory, actor, Entry(JournalEntry.LockIntended), timeout, cancellation).ConfigureAwait(false);
        var result = await ClaimAsync(host, lockPath, claimant, timeout, cancellation).ConfigureAwait(false);
        if (result.State is not (HostLockState.Claimed or HostLockState.Yours))
        {
            if (result.State == HostLockState.Unknown)
                result = result with { Detail = result.Detail + $" The claim was made as '{claimant}'." };
            throw new HostLockException(result);
        }

        var held = new HostLock(host, lockPath, claimant, timeout);
        try
        {
            // The claim already happened. Cancellation must not strand it between the atomic effect and its durable held record.
            await journal.AppendAsync(host, journalDirectory, actor, Entry(JournalEntry.LockHeld), timeout, CancellationToken.None).ConfigureAwait(false);
            return new JournaledHostLock(host, journal, journalDirectory, actor, timeout, held);
        }
        catch (Exception journalFailure) when (journalFailure is not OperationCanceledException)
        {
            try
            {
                var released = await held.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
                if (released.State is not (HostLockState.Released or HostLockState.Free)) throw new HostLockException(released);
                await journal.AppendAsync(host, journalDirectory, actor, Entry(JournalEntry.LockReleased), timeout, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception releaseFailure)
            {
                throw new AggregateException("The lock was claimed after its intent was journalled, but completion was not journalled and release was not proven.",
                    journalFailure, releaseFailure);
            }
            throw;
        }
    }

    private static async Task<HostLockResult> ClaimAsync(IGameHost host, string lockPath, string claimant, TimeSpan timeout,
        CancellationToken cancellation)
    {
        var result = await host.RunAsync(HostScripts.Lock(host.Shell.Kind), new Dictionary<string, string>
        {
            ["action"] = "claim", ["lock"] = lockPath, ["owner"] = claimant,
        }, timeout, cancellation).ConfigureAwait(false);
        return ScriptedGameHost.ReadLockVerdict("claim", claimant, lockPath, host.Name, result);
    }

    internal async Task<HostLockResult> ReleaseAsync(CancellationToken cancellation = default)
    {
        await _releaseGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _released) == 1)
                return new(HostLockState.Free, null, "The journalled lock was already released by this handle.");
            // Do not use HostLock.ReleaseAsync here: that handle deliberately acts only once even when the host did not prove
            // the effect. A journalled handle must remain retryable so DisposeAsync can make another bounded release attempt.
            var result = await _host.ReleaseLockAsync(Path, Owner, _timeout, cancellation).ConfigureAwait(false);
            if (result.State is not (HostLockState.Released or HostLockState.Free)) throw new HostLockException(result);
            Volatile.Write(ref _released, 1);
            try
            {
                await _journal.AppendAsync(_host, _journalDirectory, _actor,
                    JournalEntry.Of(JournalEntry.LockReleased, ("lock", Path), ("claimant", Owner)), _timeout, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // The lock is already free. A lost note cannot turn a successful release into a cleanup failure.
                Console.Error.WriteLine($"Warning: could not journal {JournalEntry.LockReleased} for {_actor} on {_host.Name}: {error.Message}");
            }
            return result;
        }
        finally
        {
            _releaseGate.Release();
        }
    }

    public async ValueTask DisposeAsync() => await ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
}
