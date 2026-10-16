using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;
using Xunit;

public sealed class DetachedCommandQueueTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vt-detached-commands-").FullName;
    private readonly List<JournalEntry> _journal = [];
    private DetachedCommandQueue.Route Route(int port = 5577)
    {
        string control = Path.Combine(_root, "control"), evidence = Path.Combine(_root, "evidence");
        Directory.CreateDirectory(control); Directory.CreateDirectory(evidence);
        int pid = Environment.ProcessId;
        string started = OwnedClientCommandLease.LocalStartFileTimeUtc(pid);
        return new("run-commands", control, evidence, pid, started,
            new(pid, started, port, evidence) { Role = "server" });
    }
    private static DetachedCommandQueue.Request Request(DetachedCommandQueue.Route route) => new(
        Guid.NewGuid().ToString("N"), route, DateTime.UtcNow.AddSeconds(60), "one_mutation",
        "cli_expect --strict server.mod=" + new string('b', 32) + " worlduid=123", 1);
    private static DetachedCommandQueue.Reply Completed(DetachedCommandQueue.Request request) => new(request.Id, "completed", ["OK"], "", "private.jsonl");
    private static DetachedCommandQueue.Reply Reply(DetachedCommandQueue.Route route, int slot) =>
        DetachedCommandQueue.Read<DetachedCommandQueue.Reply>(DetachedCommandQueue.Slot(route, slot, "reply"));
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void IntentPrecedesDispatchAndOnlySupervisorWritesTheJournal()
    {
        var route = Route(); var request = Request(route);
        using var queue = new DetachedCommandQueue(route, _journal.Add, (item, admit) =>
        {
            admit(); Assert.Equal("detached-command-intended", Assert.Single(_journal).Kind);
            Assert.Equal(item.Command, _journal[0].Fields["detail"]);
            return Completed(item);
        });
        int slot = DetachedCommandQueue.Publish(route, request);
        Assert.Empty(_journal);
        queue.ProcessPending();
        Assert.Equal("completed", Reply(route, slot).Outcome);
        Assert.Equal(["detached-command-intended", "detached-command-completed"], _journal.Select(entry => entry.Kind));
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("supervisor-pid")]
    [InlineData("supervisor-start")]
    [InlineData("server-pid")]
    [InlineData("server-start")]
    [InlineData("server-port")]
    public void StaleRequestsNeverDispatch(string change)
    {
        var route = Route(); var request = Request(route);
        request = change switch
        {
            "expired" => request with { ExpiresUtc = DateTime.UtcNow.AddSeconds(-1) },
            "supervisor-pid" => request with { Route = route with { Pid = route.Pid + 1 } },
            "supervisor-start" => request with { Route = route with { Started = "1" } },
            "server-pid" => request with { Route = route with { Server = route.Server with { Pid = route.Server.Pid + 1 } } },
            "server-start" => request with { Route = route with { Server = route.Server with { StartFileTimeUtc = "1" } } },
            _ => request with { Route = route with { Server = route.Server with { Port = route.Server.Port + 1 } } },
        };
        using var queue = new DetachedCommandQueue(route, _journal.Add, (_, _) => throw new Xunit.Sdk.XunitException("Dispatched stale request"));
        int slot = DetachedCommandQueue.Publish(route, request);
        queue.ProcessPending();
        Assert.Equal("refused", Reply(route, slot).Outcome);
    }

    [Fact]
    public void DuplicateIdAndLostReplyCannotReplayTheMutation()
    {
        var route = Route(); var request = Request(route); int calls = 0;
        using var queue = new DetachedCommandQueue(route, _journal.Add, (item, _) => { calls++; return Completed(item); });
        int original = DetachedCommandQueue.Publish(route, request);
        queue.ProcessPending();
        File.Delete(DetachedCommandQueue.Slot(route, original, "reply")); // Caller lost its reply.
        int duplicate = DetachedCommandQueue.Publish(route, request);
        queue.ProcessPending(); queue.ProcessPending();
        Assert.Equal(1, calls);
        Assert.Equal("refused", Reply(route, duplicate).Outcome);
        Assert.Contains("Duplicate", Reply(route, duplicate).Message);
        Assert.Contains(request.Id, File.ReadAllText(Path.Combine(route.Evidence, "detached-command-00.json")));
    }

    [Fact]
    public async Task FinishSettlesCurrentOperationAndRefusesQueuedWorkBeforeTeardown()
    {
        var route = Route(); var first = Request(route); var second = Request(route);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using var queue = new DetachedCommandQueue(route, _journal.Add, (item, admit) =>
        {
            admit(); Interlocked.Increment(ref calls); started.SetResult();
            release.Task.GetAwaiter().GetResult();
            return Completed(item);
        });
        int current = DetachedCommandQueue.Publish(route, first);
        int pending = DetachedCommandQueue.Publish(route, second);
        Task wait = Task.Run(() => queue.WaitAsync(CancellationToken.None));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        File.WriteAllText(Path.Combine(route.Control, route.Run + ".finish"), "");
        Assert.False(wait.IsCompleted); // Caller cannot begin teardown while dispatch is unresolved.
        Assert.Throws<InvalidOperationException>(() => DetachedCommandQueue.Publish(route, Request(route)));
        release.SetResult();
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, calls);
        Assert.Equal("completed", Reply(route, current).Outcome);
        Assert.Equal("refused", Reply(route, pending).Outcome);
        Assert.Throws<InvalidOperationException>(() => DetachedCommandQueue.Publish(route, Request(route)));
    }

    [Fact]
    public void FinishBetweenIntentAndDispatchClosesAdmission()
    {
        var route = Route(); int calls = 0;
        using var queue = new DetachedCommandQueue(route, entry =>
        {
            _journal.Add(entry);
            if (entry.Kind == "detached-command-intended") File.WriteAllText(Path.Combine(route.Control, route.Run + ".finish"), "");
        }, (item, admit) =>
        {
            try { admit(); calls++; return Completed(item); }
            catch (InvalidOperationException failure) { return new(item.Id, "refused", [], failure.Message, ""); }
        });
        int slot = DetachedCommandQueue.Publish(route, Request(route));
        queue.ProcessPending();
        Assert.Equal(0, calls); Assert.Equal("refused", Reply(route, slot).Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JournalFailureNeverDispatchesWithoutIntentOrReplaysAnUnsettledOperation(bool afterDispatch)
    {
        var route = Route(); int calls = 0;
        using var queue = new DetachedCommandQueue(route, entry =>
        {
            if (afterDispatch == (entry.Kind == "detached-command-completed")) throw new IOException("journal full");
            _journal.Add(entry);
        }, (item, _) => { calls++; return Completed(item); });
        DetachedCommandQueue.Publish(route, Request(route));
        Assert.Throws<IOException>(() => queue.ProcessPending());
        queue.Close(); queue.ProcessPending();
        Assert.Equal(afterDispatch ? 1 : 0, calls);
        Assert.False(File.Exists(DetachedCommandQueue.Slot(route, 0, "reply")));
        Assert.Throws<InvalidOperationException>(() => DetachedCommandQueue.Publish(route, Request(route)));
    }

    [Fact]
    public void DeadSupervisorRefusesBeforeDispatchAndDoesNotTakeTheLifetimeLock()
    {
        var route = Route() with { Started = "1" };
        using var queue = new DetachedCommandQueue(route, _journal.Add, (_, _) => throw new Xunit.Sdk.XunitException("Dispatched under dead supervisor"));
        int slot = DetachedCommandQueue.Publish(route, Request(route));
        queue.ProcessPending();
        Assert.Contains("supervisor is gone", Reply(route, slot).Message);
        Assert.Equal("refused", Reply(route, slot).Outcome);
    }

    [Fact]
    public void QueueAndAtomicControlRecordsAreBoundedAndNeverOverwrite()
    {
        var route = Route();
        for (int i = 0; i < DetachedCommandQueue.Capacity; i++) Assert.Equal(i, DetachedCommandQueue.Publish(route, Request(route)));
        Assert.Throws<InvalidOperationException>(() => DetachedCommandQueue.Publish(route, Request(route)));
        string slot = DetachedCommandQueue.Slot(route, 0, "request"); string original = File.ReadAllText(slot);
        Assert.Throws<IOException>(() => DetachedCommandQueue.WriteNew(slot, Request(route)));
        Assert.Equal(original, File.ReadAllText(slot));
        Assert.Empty(Directory.GetFiles(route.Control, "*.tmp"));
        Assert.Throws<InvalidDataException>(() => DetachedCommandQueue.WriteNew(Path.Combine(route.Control, "large"), new string('a', DetachedCommandQueue.MaxBytes)));
    }

    [Fact]
    public void LargeCompletedOutputKeepsTheFullResultAndTheSupervisorAcceptsAnotherCommand()
    {
        var route = Route(); string large = new('\u2603', DetachedCommandQueue.MaxBytes);
        using var queue = new DetachedCommandQueue(route, _journal.Add, (item, _) => Completed(item) with { Output = [large] });
        int first = DetachedCommandQueue.Publish(route, Request(route)); queue.ProcessPending();
        Assert.Equal("completed", Reply(route, first).Outcome);
        Assert.True(new FileInfo(DetachedCommandQueue.Slot(route, first, "reply")).Length <= DetachedCommandQueue.MaxBytes);
        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(route.Evidence, "detached-command-00.json")));
        Assert.Equal(large, result.RootElement.GetProperty("Output")[0].GetString());
        int next = DetachedCommandQueue.Publish(route, Request(route)); queue.ProcessPending();
        Assert.Equal("completed", Reply(route, next).Outcome);
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("corrupt")]
    [InlineData("invalid-shape")]
    public void UnreadableReplyAfterPublicationIsUnknownAndNeverRefused(string failure)
    {
        var route = Route(); var request = Request(route);
        using var queue = new DetachedCommandQueue(route, _journal.Add, (item, _) => Completed(item));
        int slot = DetachedCommandQueue.Publish(route, request); queue.ProcessPending();
        using var output = new StringWriter(); using var error = new StringWriter();
        int exit = DetachedCommandQueue.WaitForReply(route, request, slot, output, error, path =>
        {
            if (failure == "deleted") File.Delete(path);
            else if (failure == "corrupt") File.WriteAllText(path, "{");
            else File.WriteAllText(path, JsonSerializer.Serialize(Completed(request) with { Output = null! }));
            return DetachedCommandQueue.Read<DetachedCommandQueue.Reply>(path);
        });
        Assert.Equal(3, exit); Assert.Contains("UNKNOWN", error.ToString());
        Assert.Contains(request.Id, error.ToString()); Assert.Contains("do not retry", error.ToString());
        Assert.DoesNotContain("REFUSED", error.ToString());
    }

    [Fact]
    public async Task CliSubmitProvesTheJournalledOwnerAndNeverFallsBackAfterFinish()
    {
        var route = Route();
        using var journalDirectory = RunJournal.UseLocalDirectory(Path.Combine(_root, "journal"));
        // A platform-neutral journal fixture names the exact cross-process start identity used by the route.
        var runner = new JournalRunner(Environment.MachineName, route.Pid,
            DateTime.FromFileTimeUtc(long.Parse(route.Started, System.Globalization.CultureInfo.InvariantCulture)));
        var task = new WindowsScheduledTask(WindowsScheduledTask.Prefix + route.Run, "ValheimTesting detached run " + route.Run,
            "pwsh.exe", Path.Combine(route.Control, "launcher.ps1"), route.Control, "test-user");
        void Journal(JournalEntry entry) => DetachedSessionTests.LocalLine(route.Run, "detached", runner, DateTime.UtcNow, entry);
        Journal(task.Entry(JournalEntry.ScheduledTaskStarted, ("control", route.Control), ("output", route.Evidence)));
        DetachedCommandQueue.WriteNew(Path.Combine(route.Evidence, DetachedCommandQueue.RouteFile), route);
        File.WriteAllText(Path.Combine(route.Evidence, OwnedClientCommandLease.ServerWorldPins), "server.mod=" + new string('b', 32) + "\nworlduid=123\n");
        int calls = 0;
        using var queue = new DetachedCommandQueue(route, Journal, (item, admit) => { admit(); calls++; return Completed(item); });
        using var cancellation = new CancellationTokenSource();
        Task wait = Task.Run(() => queue.WaitAsync(cancellation.Token));
        try
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            string[] args = ["--evidence", route.Evidence, "--role", "server", "--phase", "world", "--command", "one_mutation"];
            int result = OwnedCliCommand.Run(args, output, error);
            Assert.True(result == 0, output.ToString() + error.ToString());
            Assert.Contains("OK", output.ToString()); Assert.Empty(error.ToString()); Assert.Equal(1, calls);
            File.WriteAllText(Path.Combine(route.Evidence, DetachedCommandQueue.RouteFile), JsonSerializer.Serialize(route with { Started = "1" }));
            Assert.Equal(3, OwnedCliCommand.Run(args, output, error));
            Assert.Contains("exact journalled detached supervisor", error.ToString()); Assert.Equal(1, calls);
            File.WriteAllText(Path.Combine(route.Evidence, DetachedCommandQueue.RouteFile), JsonSerializer.Serialize(route));
            File.WriteAllText(Path.Combine(route.Control, route.Run + ".finish"), "");
            await wait.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(3, OwnedCliCommand.Run(args, output, error));
            Assert.Contains("admission is closed", error.ToString()); Assert.Equal(1, calls);
            var records = File.ReadAllLines(Path.Combine(RunJournal.LocalDirectory, route.Run, "detached.jsonl")).Select(RunJournal.ParseLine).ToArray();
            Assert.Equal(3, records.Length); Assert.Equal(runner, RunJournalStatus.LastNamedRunner(records));
        }
        finally
        {
            cancellation.Cancel();
            try { await wait; } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
    }

    [Theory]
    [InlineData("success", "completed", 1)]
    [InlineData("wrong-pins", "refused", 0)]
    [InlineData("lost-reply", "unknown", 1)]
    [InlineData("finish-after-pins", "refused", 0)]
    [InlineData("port-takeover", "refused", 0)]
    [InlineData("server-death", "refused", 0)]
    [InlineData("pid-reuse", "refused", 0)]
    public async Task RealActorTransportProvesPinsAndIdentityBeforeItsSingleDispatch(string mode, string outcome, int expectedMutations)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var route = Route(port);
        // The socket is this process. Negative cases supply another or stale boot and must never connect to it.
        if (mode == "port-takeover")
        {
            using var other = Process.Start(new ProcessStartInfo(OperatingSystem.IsWindows() ? "ping" : "/bin/sleep")
            { ArgumentList = { OperatingSystem.IsWindows() ? "127.0.0.1" : "10" }, UseShellExecute = false, RedirectStandardOutput = true })!;
            route = route with { Server = route.Server with { Pid = other.Id, StartFileTimeUtc = OwnedClientCommandLease.LocalStartFileTimeUtc(other.Id) } };
            try { await RunSocketCase(route, listener, mode, outcome, expectedMutations); }
            finally { if (!other.HasExited) { other.Kill(); await other.WaitForExitAsync(); } }
            return;
        }
        if (mode == "server-death") route = route with { Server = route.Server with { Pid = int.MaxValue } };
        if (mode == "pid-reuse") route = route with { Server = route.Server with { StartFileTimeUtc = "1" } };
        await RunSocketCase(route, listener, mode, outcome, expectedMutations);
    }

    private async Task RunSocketCase(DetachedCommandQueue.Route route, TcpListener listener, string mode, string outcome, int expectedMutations)
    {
        File.WriteAllText(Path.Combine(route.Evidence, OwnedClientCommandLease.ServerFileName), JsonSerializer.Serialize(route.Server));
        using var stopping = new CancellationTokenSource();
        int mutations = 0, pinChecks = 0;
        Task serve = Task.Run(async () =>
        {
            try
            {
                using var socket = await listener.AcceptTcpClientAsync(stopping.Token);
                using var stream = socket.GetStream();
                using var reader = new StreamReader(stream, new UTF8Encoding(false));
                using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                await writer.WriteLineAsync("VALHEIM_CLI_READY"); await writer.WriteLineAsync("VALHEIM_CLI_CAPS completion");
                while (await reader.ReadLineAsync(stopping.Token) is { } line)
                {
                    string command = line[(line.IndexOf(':', 5) + 1)..];
                    bool pin = command.StartsWith("cli_expect --strict ", StringComparison.Ordinal);
                    if (pin) pinChecks++; else mutations++;
                    if (!pin && mode == "lost-reply") return;
                    if (pin && pinChecks == 2 && mode == "finish-after-pins") File.WriteAllText(Path.Combine(route.Control, route.Run + ".finish"), "");
                    await writer.WriteLineAsync("OUTPUT:1");
                    await writer.WriteLineAsync(pin ? (mode == "wrong-pins" ? "ERROR: EXPECT mismatch" : "OK: EXPECT") : "OK: MUTATED");
                    await writer.WriteLineAsync("END_OUTPUT");
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        });
        try
        {
            using var queue = new DetachedCommandQueue(route, _journal.Add);
            var request = Request(route);
            int slot = DetachedCommandQueue.Publish(route, request);
            queue.ProcessPending(); queue.ProcessPending();
            Assert.Equal(outcome, Reply(route, slot).Outcome);
            Assert.Equal(expectedMutations, mutations);
            if (mode == "port-takeover") Assert.Contains("port is not listening", Reply(route, slot).Message);
            if (mode == "success") Assert.Equal(2, pinChecks);
        }
        finally { stopping.Cancel(); await serve.WaitAsync(TimeSpan.FromSeconds(5)); }
    }
}
