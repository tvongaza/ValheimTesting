using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Valheim.Testing.Game;
using valheim_cli.Testing;
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
}

// Speaks the part of ValheimCLI's line protocol a state wait uses: greeting, STATE, SUBSCRIBE_STATE and pushes.
// One connection. Push sends a change without altering what STATE answers; every line received is recorded.
internal sealed class FakeCliServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly object _write = new();
    private readonly ConcurrentQueue<string> _received = new();
    private readonly TaskCompletionSource _subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _answered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TcpClient? _client;
    private StreamWriter? _writer;
    public string State { get; }
    /// <summary>A push written just ahead of each STATE answer, as when the state changes while the question is in flight.</summary>
    public string? PushBeforeAnswer { get; init; }
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public Task Subscribed => _subscribed.Task;
    /// <summary>Completes once a STATE question has been answered.</summary>
    public Task Answered => _answered.Task;
    public string[] Received => _received.ToArray();
    public FakeCliServer(string state) { State = state; _listener.Start(); _ = Task.Run(Serve); }
    public ValheimClient Connect()
    {
        var client = new ValheimClient("127.0.0.1", Port);
        if (!client.Connect()) { client.Dispose(); throw new IOException("Fake CLI server refused the connection."); }
        return client;
    }
    private async Task Serve()
    {
        try
        {
            var client = await _listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var reader = new StreamReader(stream, new UTF8Encoding(false));
            lock (_write)
            {
                _client = client;
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                _writer.WriteLine("VALHEIM_CLI_READY"); _writer.WriteLine("VALHEIM_CLI_CAPS completion");
            }
            while (await reader.ReadLineAsync() is { } line)
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
        catch (Exception error) when (error is IOException or ObjectDisposedException or SocketException) { /* Dropped or disposed. */ }
    }
    public void Push(string state) => Send("STATE_CHANGED:" + state);
    public void Send(string line) { lock (_write) _writer!.WriteLine(line); }
    public void Drop() { lock (_write) _client?.Close(); }
    public void Dispose() { _listener.Stop(); lock (_write) _client?.Dispose(); }
}
