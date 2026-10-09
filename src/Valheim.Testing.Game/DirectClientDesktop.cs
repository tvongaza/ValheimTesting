using System.Diagnostics;

namespace Valheim.Testing.Game;

// Game's direct owned-client opener has no Windows desktop-task route. Keep this decision in Game,
// so generated consumers and direct ClientSession callers get the same early refusal.
internal static class DirectClientDesktop
{
    internal static (bool Windows, int SessionId) Current()
    {
        using var process = Process.GetCurrentProcess();
        return (OperatingSystem.IsWindows(), process.SessionId);
    }

    internal static void Require((bool Windows, int SessionId) session)
    {
        if (session is (true, 0))
            throw new InvalidOperationException("This direct Valheim client launch cannot run from Windows SSH/session 0. " +
                "Run it in an interactive desktop terminal, or use valheim-test start, which preflights and launches through the desktop. " +
                "No client was launched.");
    }

    internal static void RequireCurrent() => Require(Current());
}
