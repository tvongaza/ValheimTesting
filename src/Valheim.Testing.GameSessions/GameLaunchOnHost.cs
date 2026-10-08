using System.Text;
using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>A <see cref="GameLaunch"/> built for a host, as the host's start scripts and process probe read it.</summary>
internal static class GameLaunchOnHost
{
    /// <summary>The hash of the command line the started game has on its host (<see cref="HostProcessProbe.ExpectedCommandLineSha256"/>).</summary>
    internal static string CommandLineSha256(this GameLaunch launch) => launch.Platform == ClientPlatform.MacOS
        ? HostProcessProbe.ExpectedMacCommandLineSha256(launch.Executable, launch.Arguments)
        : HostProcessProbe.ExpectedCommandLineSha256(launch.Platform == ClientPlatform.Windows, launch.Executable, launch.Arguments);

    /// <summary>
    /// The launch as the host start scripts read it: one line per item, <c>kind base64(UTF-8)</c>, in the order exe, dir, the
    /// arguments (Windows: one <c>args</c> command line; Linux: one <c>arg</c> each), unset, env and prepend. A script ignores the
    /// kinds it does not use. Never written to disk by this library, and the host scripts delete their copy once read: arguments may
    /// hold a server password.
    /// </summary>
    internal static string Spec(this GameLaunch launch)
    {
        var text = new StringBuilder();
        void Line(string kind, string value) => text.Append(kind).Append(' ').Append(InteractiveClient.Base64(value)).Append('\n');
        Line("exe", launch.Executable);
        Line("dir", launch.WorkingDirectory);
        if (launch.Platform == ClientPlatform.Windows) Line("args", WindowsCommandLine.Join(launch.Arguments));
        else foreach (string argument in launch.Arguments) Line("arg", argument);
        foreach (string name in launch.Unset) Line("unset", name);
        foreach (var (name, value) in launch.Environment.OrderBy(pair => pair.Key, StringComparer.Ordinal)) Line("env", name + "=" + value);
        foreach (var (name, value) in launch.Prepended.OrderBy(pair => pair.Key, StringComparer.Ordinal)) Line("prepend", name + "=" + value);
        return text.ToString();
    }
}
