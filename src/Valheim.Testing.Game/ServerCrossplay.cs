using System.Globalization;

namespace Valheim.Testing.Game;

// A crossplay (PlayFab) dedicated server: the only way to launch one is the plan's "crossplay" option, so the runner knows
// and reports which backend the server uses.
public partial class ServerRunPlan
{
    /// <summary>
    /// Launches the server with <c>-crossplay</c>, the game's PlayFab backend: clients join its lobby
    /// (<see cref="CrossplayServer.WaitForLobby"/>, <see cref="SessionControl.JoinCrossplay"/>), not its address, and the
    /// report records <c>crossplay</c>. <c>-crossplay</c> in <see cref="Arguments"/> is refused. A crossplay plan names
    /// its game port (<c>-port N</c>): the lobby is keyed by the public IP and that port, so two crossplay servers behind
    /// one public IP need different ports. Keep a crossplay fixture separate from a Steam one rather than switching one
    /// server between modes: a client remembers each server's mode in its recent-server list.
    /// </summary>
    public bool Crossplay { get; set; }

    /// <summary>
    /// Refuses <c>-crossplay</c> in <see cref="Arguments"/> (in any case, as the game reads it) and, for a
    /// <see cref="Crossplay"/> plan, anything but exactly one explicit <c>-port N</c>. The runner checks this for every plan.
    /// </summary>
    public void CheckCrossplay()
    {
        if (Arguments.Any(argument => argument.Equals("-crossplay", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("-crossplay is refused in arguments: set \"crossplay\": true, which the runner turns into -crossplay and records in the report.");
        if (!Crossplay) return;
        int port = Array.FindIndex(Arguments, argument => argument.Equals("-port", StringComparison.OrdinalIgnoreCase));
        if (port < 0 || Arguments.Count(argument => argument.Equals("-port", StringComparison.OrdinalIgnoreCase)) != 1 || port + 1 >= Arguments.Length ||
            !int.TryParse(Arguments[port + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value is < 1024 or > 65535)
            throw new ArgumentException("A crossplay server names its game port with exactly one -port N (1024-65535): its lobby is keyed by the public IP and port, so two crossplay servers behind one public IP must use different ports, and the default 2456 collides with any other.");
    }

    /// <summary>The server's launch arguments: <see cref="Arguments"/> expanded (<see cref="Expand"/>), then <c>-crossplay</c> for a <see cref="Crossplay"/> plan.</summary>
    public string[] LaunchArguments(string runtime, string world) =>
        [.. Arguments.Select(argument => Expand(argument, runtime, world)), .. (Crossplay ? new[] { "-crossplay" } : Array.Empty<string>())];
}
