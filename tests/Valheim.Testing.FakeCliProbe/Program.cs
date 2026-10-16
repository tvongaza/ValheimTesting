using Valheim.Testing.Game;

if (args is ["dispose-before-accept"])
{
    using var server = new FakeCliServer(StateWait.Loading, pauseBeforeAccept: true);
    server.Dispose();
    Require(server.Completed.IsCompletedSuccessfully, "serving did not complete after Stop-before-Accept");
    Console.WriteLine("PASS: disposal stopped the listener before Accept");
    return;
}

// Global thread-pool settings belong only to this child process, never to the xUnit host.
ThreadPool.GetMinThreads(out int minimum, out int minimumIo);
ThreadPool.GetMaxThreads(out int maximum, out int maximumIo);
if (!ThreadPool.SetMinThreads(1, minimumIo) || !ThreadPool.SetMaxThreads(1, maximumIo))
    throw new InvalidOperationException("Could not cap the child thread pool at one worker.");
using var entered = new ManualResetEventSlim();
using var release = new ManualResetEventSlim();
Task blocker = Task.Run(() => { entered.Set(); release.Wait(); });
try
{
    if (!entered.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Pool blocker did not enter.");
    // A queued sentinel proves the only worker is occupied throughout the actual socket exchanges.
    Task sentinel = Task.Run(() => { });
    Require(!sentinel.IsCompleted, "pool was not blocked before serving");
    using (var server = new FakeCliServer(StateWait.Loading) { PushBeforeAnswer = StateWait.InWorldNoPlayer })
    {
        using var client = server.Connect();
        Require(!sentinel.IsCompleted, "handshake depended on the pool");
        var pushed = new List<string>();
        client.OnStateChanged += pushed.Add;
        Require(client.SubscribeToStateChanges(), "subscription was refused");
        Require(client.GetState() == StateWait.Loading, "STATE answer was lost");
        Require(pushed.SequenceEqual([StateWait.InWorldNoPlayer]), "push did not precede the STATE answer");
        Require(server.Received.SequenceEqual(["SUBSCRIBE_STATE", "STATE"]), "unexpected wire requests");
        // Stop while its connection is still open, with no pool worker available for cleanup.
        server.Dispose();
        Require(server.Completed.IsCompletedSuccessfully, "serving did not complete");
        Require(!sentinel.IsCompleted, "serving or disposal depended on the pool");
    }
    Console.WriteLine("PASS: handshake, subscription, push-before-answer and disposal with the pool blocked");
}
finally
{
    release.Set();
    if (!blocker.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Pool blocker did not stop.");
    ThreadPool.SetMaxThreads(maximum, maximumIo);
    ThreadPool.SetMinThreads(minimum, minimumIo);
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

namespace Valheim.Testing.FakeCliProbe { public sealed class Marker { } }
