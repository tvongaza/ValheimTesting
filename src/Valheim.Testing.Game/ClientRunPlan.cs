using valheimCLI;

namespace Valheim.Testing.Game;

/// <summary>
/// The game client a system test joins to its server, as a plan section. <c>owned</c>: the runner launches it from
/// <see cref="Install"/> and stops only that process (<see cref="ClientSession.Launch(ClientRunPlan, string, CancellationToken)"/>);
/// it must run in the desktop session where Steam is running and signed in, with a display. <c>attach</c>: an operator
/// launched and signed in the client; the runner only connects to its ValheimCLI port and never touches the process.
/// The join password stays in the client's own process environment, named by <see cref="PasswordVariable"/>.
/// </summary>
public sealed class ClientRunPlan
{
    public string Mode { get; set; } = "";
    /// <summary>Owned only: the client install to launch, with BepInEx and ValheimCLI.</summary>
    public string Install { get; set; } = "";
    /// <summary>Owned only: extra game arguments; <see cref="ClientLaunch"/> adds <c>-console</c>.</summary>
    public string[] LaunchArguments { get; set; } = [];
    public string Host { get; set; } = "127.0.0.1";
    /// <summary>The client's ValheimCLI port (its <c>[Server] Port</c> setting); it must differ from the server's.</summary>
    public int Port { get; set; }
    /// <summary>Every plugin the client loads by exact MD5, or <c>absent</c>. No world key: the runner adds the server's.</summary>
    public Dictionary<string, string> Pins { get; set; } = [];
    /// <summary>The server address the client joins, host:port.</summary>
    public string Join { get; set; } = "";
    /// <summary>An existing, disposable local character (never a cloud character).</summary>
    public string Character { get; set; } = "";
    /// <summary>The environment variable, in the client's process, that holds the join password.</summary>
    public string? PasswordVariable { get; set; }
    public int StartSeconds { get; set; } = 300;
    public int JoinSeconds { get; set; } = 180;
    public int ArrivalSeconds { get; set; } = 120;

    public bool Owned => Mode == "owned";

    /// <summary>
    /// The rules every client section follows, plus <paramref name="absentPlugins"/>, which must be pinned <c>absent</c>
    /// when the claim is what a client without them sees (a server-only mod).
    /// </summary>
    public void Validate(params string[] absentPlugins)
    {
        if (Mode is not ("owned" or "attach")) throw new ArgumentException("Client mode is owned or attach.");
        if (Owned && !Path.IsPathFullyQualified(Install)) throw new ArgumentException("An owned client needs the full path of its install.");
        if (!Owned && (Install.Length != 0 || LaunchArguments.Length != 0)) throw new ArgumentException("An attached client is launched by its operator; leave out install and launch arguments.");
        if (string.IsNullOrWhiteSpace(Host) || Port is < 1024 or > 65535) throw new ArgumentException("Give the client's ValheimCLI host and port.");
        if (Owned && Host is not ("127.0.0.1" or "localhost")) throw new ArgumentException("An owned client runs on this machine; its ValheimCLI host is 127.0.0.1.");
        foreach (string? token in new[] { Join, Character, PasswordVariable })
            if (token != null && (token.Length == 0 || token.Any(char.IsWhiteSpace))) throw new ArgumentException("Join address, character and password variable must be single tokens.");
        if (StartSeconds is < 10 or > 1800 || JoinSeconds is < 10 or > 900 || ArrivalSeconds is < 10 or > 600) throw new ArgumentException("Client timeouts are out of range.");
        if (Pins.ContainsKey("worlduid") || Pins.ContainsKey("world")) throw new ArgumentException("Leave the world out of the client pins; the runner pins the server's world.");
        if (!Pins.TryGetValue("valheimCLI.valheimCLI", out var cli) || cli.Length != 32 || !cli.All(Uri.IsHexDigit)) throw new ArgumentException("Pin the client's exact ValheimCLI MD5.");
        foreach (string plugin in absentPlugins)
            if (!Pins.TryGetValue(plugin, out var value) || value != "absent") throw new ArgumentException($"The client must pin {plugin}=absent: the check is what a client without it sees.");
        foreach (var pin in Pins)
            if (pin.Value != "absent" && (pin.Value.Length != 32 || !pin.Value.All(Uri.IsHexDigit))) throw new ArgumentException($"Client plugin {pin.Key} needs an exact MD5 or absent.");
        _ = MenuExpectations; // Parses the pins before anything launches.
    }

    /// <summary>Strict pins at the menu: plugins only.</summary>
    public string MenuExpectations => Expect(Pins.Select(p => p.Key + "=" + p.Value));
    /// <summary>Strict pins once joined: plugins and the server's world.</summary>
    public string WorldExpectations(string worldUid) => Expect(Pins.Select(p => p.Key + "=" + p.Value).Append("worlduid=" + worldUid));
    private static string Expect(IEnumerable<string> lines)
    {
        var errors = new List<string>();
        var parsed = Expectations.ParseLines(lines, errors);
        if (errors.Count != 0) throw new ArgumentException("Invalid client pins: " + string.Join("; ", errors));
        return Expectations.ExpectCommand(parsed, strict: true);
    }
}

/// <summary>A file a plan depends on, pinned by path and SHA256.</summary>
public sealed class PinnedFile
{
    public string Source { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public void Validate(string what)
    {
        if (!Path.IsPathFullyQualified(Source) || Sha256.Length != 64 || !Sha256.All(Uri.IsHexDigit)) throw new ArgumentException($"Pin the {what} by full path and SHA256.");
    }
    /// <summary>The file's path after checking that its hash still matches.</summary>
    public string Verified()
    {
        if (!string.Equals(WorldFixture.Hash(Source), Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"{Source} changed after it was pinned.");
        return Source;
    }
}
