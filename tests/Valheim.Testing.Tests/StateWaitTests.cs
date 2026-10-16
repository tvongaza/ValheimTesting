using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Valheim.Testing.Game;
using valheim_cli.Testing;
#if !FAKE_CLI_PROBE
using System.Diagnostics;
using Xunit;

public class StateWaitTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(60);
    [Fact] public async Task TheCurrentStateCountsWithoutAPush()
    {
        using var server = new FakeCliServer(StateWait.InWorldNoPlayer);
        using var states = StateWait.Connect("127.0.0.1", server.Port);
        Assert.Equal(StateWait.InWorldNoPlayer, await states.WaitAsync(StateWait.WorldLoaded, Generous));
    }
    [Fact] public async Task APushEndsTheWaitAndNothingIsSentWhileWaitingForIt()
    {
        // The server keeps answering Loading: only the push can end this wait.
        using var server = new FakeCliServer(StateWait.Loading);
        using var states = new StateWait(server.Connect());
        var wait = states.WaitAsync(StateWait.WorldLoaded, Generous);
        await server.Answered.WaitAsync(Generous);
        // An absence can only be shown over time. The previous design asked for the state every 2 s; a slow machine can
        // only make traffic less likely here, never invent it.
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.False(wait.IsCompleted);
        Assert.Equal(new[] { "SUBSCRIBE_STATE", "STATE" }, server.Received);
        server.Push(StateWait.InWorldNoPlayer);
        Assert.Equal(StateWait.InWorldNoPlayer, await wait);
        Assert.Equal(new[] { "SUBSCRIBE_STATE", "STATE" }, server.Received);
    }
    [Fact] public async Task AStatePushedWhileTheQuestionIsAnsweredCounts()
    {
        // The push reaches the client ahead of the STATE answer, which already says Loading again.
        using var server = new FakeCliServer(StateWait.Loading) { PushBeforeAnswer = StateWait.InWorldNoPlayer };
        using var states = new StateWait(server.Connect());
        Assert.Equal(StateWait.InWorldNoPlayer, await states.WaitAsync(StateWait.WorldLoaded, Generous));
        Assert.Equal(StateWait.Loading, states.LastState);
    }
    [Fact] public async Task AFailureStateEndsTheWaitAtOnce()
    {
        using var server = new FakeCliServer(StateWait.Loading);
        using var states = new StateWait(server.Connect());
        var wait = states.WaitAsync(StateWait.WorldLoaded, Generous, [StateWait.MainMenu]);
        await server.Answered.WaitAsync(Generous);
        server.Push(StateWait.MainMenu);
        var error = await Assert.ThrowsAsync<WaitFailedException>(() => wait);
        Assert.Equal("state MainMenu", error.LastSeen); Assert.Contains("MainMenu", error.Reason);
    }
    [Fact] public async Task ExpiryReportsTheLastStateAndRetiresTheConnection()
    {
        using var server = new FakeCliServer(StateWait.Loading);
        using var states = new StateWait(server.Connect());
        // First learn the current state, so the expiry below has something to report on any machine.
        Assert.Equal(StateWait.Loading, await states.WaitAsync([StateWait.Loading], Generous));
        var error = await Assert.ThrowsAsync<WaitTimeoutException>(() => states.WaitAsync(StateWait.WorldLoaded, TimeSpan.FromMilliseconds(300)));
        Assert.Equal("state Loading", error.LastSeen); Assert.Contains("InWorldNoPlayer", error.Target);
        var retired = await Assert.ThrowsAsync<WaitFailedException>(() => states.WaitAsync(StateWait.WorldLoaded, Generous));
        Assert.Contains("connect a new StateWait", retired.Reason);
    }
    [Fact] public async Task ALostConnectionEndsTheWait()
    {
        using var server = new FakeCliServer(StateWait.Loading);
        using var states = new StateWait(server.Connect());
        var wait = states.WaitAsync(StateWait.WorldLoaded, Generous);
        await server.Answered.WaitAsync(Generous);
        server.Drop();
        await Assert.ThrowsAsync<WaitFailedException>(() => wait);
    }
    [Fact] public async Task ALineThatIsNotAPushEndsTheWait()
    {
        using var server = new FakeCliServer(StateWait.Loading);
        using var states = new StateWait(server.Connect());
        var wait = states.WaitAsync(StateWait.WorldLoaded, Generous);
        await server.Answered.WaitAsync(Generous);
        server.Send("OUTPUT:1");
        var error = await Assert.ThrowsAsync<WaitFailedException>(() => wait);
        Assert.Contains("protocol error", error.Reason);
    }
    [Fact] public async Task AWaitNeedsATargetAndAnExplicitTimeout()
    {
        using var server = new FakeCliServer(StateWait.Loading);
        using var states = new StateWait(server.Connect());
        await Assert.ThrowsAsync<ArgumentException>(() => states.WaitAsync([], Generous));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => states.WaitAsync(StateWait.WorldLoaded, TimeSpan.Zero));
    }
    [Fact] public async Task TheFakeServesWhileTheChildProcessThreadPoolIsBlocked()
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(Valheim.Testing.FakeCliProbe.Marker).Assembly.Location);
        using var child = Process.Start(start)!;
        Task<string> output = child.StandardOutput.ReadToEndAsync(), error = child.StandardError.ReadToEndAsync();
        try
        {
            await child.WaitForExitAsync().WaitAsync(Generous);
            string said = await output + await error;
            Assert.True(child.ExitCode == 0, said);
            Assert.Contains("PASS: handshake, subscription, push-before-answer and disposal with the pool blocked", said);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }
    [Fact] public void TheFakeStopsWhileWaitingForItsOnlyConnection()
    {
        var server = new FakeCliServer(StateWait.Loading);
        server.Dispose();
        Assert.True(server.Completed.IsCompletedSuccessfully);
        server.Dispose();
    }
    [Fact] public void TheFakeStopsWhileReadingAnOpenConnection()
    {
        var server = new FakeCliServer(StateWait.Loading);
        using var client = server.Connect();
        server.Dispose();
        Assert.True(server.Completed.IsCompletedSuccessfully);
    }
    [Fact] public async Task TheFakeCompletesWhenTheClientCloses()
    {
        using var server = new FakeCliServer(StateWait.Loading);
        server.Connect().Dispose();
        await server.Completed.WaitAsync(Generous);
    }
}

#endif

// Speaks the part of ValheimCLI's line protocol a state wait uses: greeting, STATE, SUBSCRIBE_STATE and pushes.
// One connection. Push sends a change without altering what STATE answers; every line received is recorded.
internal sealed class FakeCliServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly object _write = new();
    private readonly ConcurrentQueue<string> _received = new();
    private readonly TaskCompletionSource _subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _answered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly TimeSpan LifecycleBound = TimeSpan.FromSeconds(10);
    private readonly ManualResetEventSlim _ready = new();
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _serve;
    private int _closed;
    private TcpClient? _client;
    private StreamWriter? _writer;
    public string State { get; }
    /// <summary>A push written just ahead of each STATE answer, as when the state changes while the question is in flight.</summary>
    public string? PushBeforeAnswer { get; init; }
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public Task Subscribed => _subscribed.Task;
    /// <summary>Completes once a STATE question has been answered.</summary>
    public Task Answered => _answered.Task;
    public Task Completed => _completed.Task;
    public string[] Received => _received.ToArray();
    public FakeCliServer(string state)
    {
        State = state;
        _listener.Start();
        // The synchronous client handshake must not need a free thread-pool worker. The entire loop stays on this
        // thread: starting an async Serve with LongRunning would move its continuations back onto the pool.
        _serve = new Thread(Serve) { IsBackground = true, Name = nameof(FakeCliServer) };
        _serve.Start();
        if (!_ready.Wait(LifecycleBound))
        {
            Dispose();
            throw new TimeoutException("Fake CLI serving thread did not become ready.");
        }
    }
    public ValheimClient Connect()
    {
        var client = new ValheimClient("127.0.0.1", Port);
        if (!client.Connect()) { client.Dispose(); throw new IOException("Fake CLI server refused the connection."); }
        return client;
    }
    private void Serve()
    {
        try
        {
            _ready.Set();
            using var client = _listener.AcceptTcpClient();
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            lock (_write)
            {
                if (Volatile.Read(ref _closed) != 0) return;
                _client = client;
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                _writer.WriteLine("VALHEIM_CLI_READY"); _writer.WriteLine("VALHEIM_CLI_CAPS completion");
            }
            while (reader.ReadLine() is { } line)
            {
                _received.Enqueue(line);
                lock (_write)
                {
                    if (line == "STATE")
                    {
                        if (PushBeforeAnswer != null) _writer.WriteLine("STATE_CHANGED:" + PushBeforeAnswer);
                        _writer.WriteLine("STATE:" + State); _answered.TrySetResult();
                    }
                    else if (line == "SUBSCRIBE_STATE") { _writer.WriteLine("SUBSCRIBED"); _subscribed.TrySetResult(); }
                }
            }
        }
        catch (Exception error) when (Volatile.Read(ref _closed) != 0 && error is IOException or ObjectDisposedException or SocketException)
        { /* Drop/Dispose closed the listener or connection. */ }
        catch (Exception error)
        {
            _subscribed.TrySetException(error);
            _answered.TrySetException(error);
            _completed.TrySetException(error);
        }
        finally { _completed.TrySetResult(); }
    }
    public void Push(string state) => Send("STATE_CHANGED:" + state);
    public void Send(string line) { lock (_write) _writer!.WriteLine(line); }
    public void Drop() { Interlocked.Exchange(ref _closed, 1); lock (_write) _client?.Close(); }
    public void Dispose()
    {
        Interlocked.Exchange(ref _closed, 1);
        _listener.Stop();
        lock (_write) _client?.Dispose();
        if (!_serve.Join(LifecycleBound)) throw new TimeoutException("Fake CLI serving thread did not stop.");
        _ready.Dispose();
        _completed.Task.GetAwaiter().GetResult();
    }
}
