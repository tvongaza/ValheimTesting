using System.Net;
using System.Net.Sockets;
using System.Text;
using Valheim.Testing.Game;
using valheim_cli.Testing;
using Xunit;

public sealed class CliTransportCancelTests
{
    [Fact]
    public async Task CancellingTheCommandClosesOnlyItsSocketAfterRecheckingPins()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var commandSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task server = Task.Run(async () =>
        {
            using var control = await listener.AcceptTcpClientAsync();
            await Greet(control);
            using var request = await listener.AcceptTcpClientAsync();
            await Greet(request);
            using var reader = new StreamReader(request.GetStream(), new UTF8Encoding(false));
            using var writer = new StreamWriter(request.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
            Assert.Equal("CMDT:30:cli_expect worlduid=7", await reader.ReadLineAsync());
            await writer.WriteLineAsync("OUTPUT:1\nOK: EXPECT\nEND_OUTPUT");
            Assert.Equal("CMDT:30:cli_extension example/capture", await reader.ReadLineAsync());
            commandSeen.SetResult();
            Assert.Null(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            disconnected.SetResult();
            // The control connection was kept alive for subsequent cleanup.
            Assert.True(control.Connected);
        });
        try
        {
            using var transport = new CliTransport("127.0.0.1", port);
            using var cancel = new CancellationTokenSource();
            Task<CommandResult> pending = transport.ExecuteCancelableAsync("cli_expect worlduid=7",
                "cli_extension example/capture", TimeSpan.FromSeconds(30), cancel.Token);
            await commandSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            listener.Stop();
            await server.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task Greet(TcpClient connection)
    {
        using var writer = new StreamWriter(connection.GetStream(), new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync("VALHEIM_CLI_READY");
        await writer.WriteLineAsync("VALHEIM_CLI_CAPS completion");
    }
}
