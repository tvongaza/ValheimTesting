using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Valheim.Testing.GameSessions;

/// <summary>
/// Refuses a run from an MSIX-packaged app on Windows (#406), such as a desktop app that starts valheim-test for you. Windows
/// redirects every AppData write by a packaged app and its child processes into the package's private LocalCache; the
/// scheduled task that starts the dedicated server, and the game, run outside the package and cannot see those files, so the run
/// would fail at the server task with 0x8007010B, which names no cause. Other platforms have no package identity.
/// </summary>
internal static class PackagedApp
{
    /// <summary>Throws the refusal when this process runs inside a Windows package.</summary>
    internal static void Require()
    {
        if (Refusal() is { } refusal) throw new InvalidOperationException(refusal);
    }

    /// <summary>Why this process cannot run valheim-test, or null: it has no package identity.</summary>
    internal static string? Refusal() => OperatingSystem.IsWindows() ? RefusalFor(CurrentPackage()) : null;

    /// <summary>The refusal for a process whose package full name is <paramref name="package"/> (null: not packaged).</summary>
    internal static string? RefusalFor(string? package) => package == null ? null :
        $"valheim-test is running inside the packaged app {package}. Windows redirects a packaged app's AppData writes into the package, " +
        "where the dedicated server's scheduled task and the game cannot see them. Run valheim-test from an ordinary terminal " +
        "(Windows Terminal, PowerShell or cmd) instead.";

    private const int NoPackage = 15700; // APPMODEL_ERROR_NO_PACKAGE
    private const int InsufficientBuffer = 122; // ERROR_INSUFFICIENT_BUFFER

    // This process's package full name, or null when it has no package identity.
    [SupportedOSPlatform("windows")]
    private static string? CurrentPackage()
    {
        int length = 0;
        int result = GetCurrentPackageFullName(ref length, null);
        if (result == NoPackage) return null;
        if (result != InsufficientBuffer) throw new InvalidOperationException($"Could not read this process's package identity (Windows error {result}).");
        var name = new char[length];
        result = GetCurrentPackageFullName(ref length, name);
        if (result != 0) throw new InvalidOperationException($"Could not read this process's package identity (Windows error {result}).");
        return new string(name, 0, Math.Max(0, length - 1)); // length counts the terminating null
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, [Out] char[]? packageFullName);
}
