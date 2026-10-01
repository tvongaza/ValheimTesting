using System.Globalization;
using System.Text.RegularExpressions;
using Valheim.Testing.Game;

/// <summary>
/// The scenario: the rounds and assertions that are the same in every environment, so this file can be shared as it was
/// run. Paths, ports, the character and which files are staged live in the manifest, never here.
/// <para>
/// This one tests the <see href="../FullLifecycle/README.md">FullLifecycle</see> example mod, MyMod, hosted with its test
/// adapter as the probe: its Harmony patches are applied, and its <c>mymod_mark</c> command marks the ground where the
/// host stands with exactly one pole in the host's saved objects. Replace <see cref="Measure"/> with your mod's steps.
/// </para>
/// </summary>
public static class Scenario
{
    /// <summary>The report's name.</summary>
    public const string Name = "mymod-mark-where-the-host-stands";
    /// <summary>The hosted rounds; between two, the world is saved with confirmation and hosted again in the same process.</summary>
    public static readonly string[] Rounds = ["first"];
    /// <summary>The ValheimCLI extension commands the steps use, beyond the hosted rounds' own: checked live once the client answers.</summary>
    public static readonly string[] Capabilities = ["mymod.testing/harmony"];

    private const string Mod = "example.mymod", Marker = "wood_pole2";
    private static readonly DeclaredPatch[] Patches =
    [
        new("Terminal::InitTerminal", "postfix", "MyMod.Plugin+RegisterCommands::Postfix"),
        new("ZNet::OnNewConnection", "prefix", "MyMod.VersionHandshake+SendVersion::Prefix"),
        new("ZNet::RPC_PeerInfo", "prefix", "MyMod.VersionHandshake+RefuseMismatched::Prefix"),
        new("ZNet::Awake", "postfix", "MyMod.SyncedGreeting+RegisterRpc::Postfix"),
    ];
    private static readonly Regex Position = new(@"position=(-?[\d.]+),(-?[\d.]+),(-?[\d.]+)", RegexOptions.CultureInvariant);
    private static readonly Regex ZdoLine = new(@"^ZDO (\S+) id=\S+ pos=(-?[\d.]+),(-?[\d.]+),(-?[\d.]+) ", RegexOptions.CultureInvariant);
    private static readonly Regex ZdoSummary = new(@"^OK: ZDOS_AT .* objects=(\d+)$", RegexOptions.CultureInvariant);

    /// <summary>One round, run once the fixture world is hosted and the host's player is protected.</summary>
    public static void Measure(ClientRound round)
    {
        var host = round.Server; // A host is the server of its world and the client at once.
        double x = 0, z = 0;
        round.Step("the mod's Harmony patches are applied", () =>
            HarmonyCensus.Read(host, "mymod.testing/harmony", Mod).Check(Mod, Patches).RequireApplied());
        round.Step("record where the host stands", () =>
        {
            var reply = host.Execute("cli_player_state");
            var match = Position.Match(string.Join("\n", reply.Output));
            if (!match.Success) throw new InvalidOperationException("No player position in: " + string.Join(" | ", reply.Output));
            x = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            z = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            round.Write("player-position", new { x, z, reply = reply.Output });
        });
        string At() => string.Create(CultureInfo.InvariantCulture, $"{x:F1} {z:F1}"); // Once the position is recorded.
        round.Step("the mod marks the ground where the host stands", () =>
        {
            var reply = host.Execute("mymod_mark " + At());
            round.Write("mark", new { reply = reply.Output });
            if (reply.Output.Count(line => line.StartsWith("OK: marked ", StringComparison.Ordinal)) != 1)
                throw new InvalidOperationException("MyMod did not confirm one marker: " + string.Join(" | ", reply.Output));
        });
        round.Step("exactly one marker stands there in the host's saved objects", () =>
        {
            var output = host.Execute("cli_zdos_at " + At() + " 8").Output;
            var objects = output.Select(line => ZdoLine.Match(line)).Where(match => match.Success).ToList();
            var totals = output.Select(line => ZdoSummary.Match(line)).Where(match => match.Success).ToList();
            // An observation counts only when it is complete: its summary lists exactly the objects returned.
            if (totals.Count != 1 || int.Parse(totals[0].Groups[1].Value, CultureInfo.InvariantCulture) != objects.Count)
                throw new InvalidOperationException("Incomplete census: the reply's summary does not account for the objects listed.");
            int markers = objects.Count(match => match.Groups[1].Value == Marker &&
                Math.Abs(double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) - x) <= 1.5 &&
                Math.Abs(double.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) - z) <= 1.5);
            if (markers != 1) throw new InvalidOperationException($"{markers} {Marker} marker(s) stand at ({At()}); expected 1.");
        });
    }
}
