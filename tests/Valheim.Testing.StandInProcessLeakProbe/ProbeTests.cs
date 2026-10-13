using Xunit;
using Xunit.Sdk;

[assembly: TestFramework("StandInProcessTestFramework", "Valheim.Testing.Tests")]

namespace Valheim.Testing.StandInProcessLeakProbe;

public sealed class ProbeTests
{
    [Fact]
    public async Task DeliberatelyLeavesARegisteredStandInAlive()
    {
        string root = Environment.GetEnvironmentVariable("VT_STANDIN_GUARD_PROBE_ROOT")
            ?? throw new InvalidOperationException("The parent test did not provide the probe root.");
        await global::StandInProcessLeakProbeDriver.StartAsync(root,
            nameof(DeliberatelyLeavesARegisteredStandInAlive));
    }
}
