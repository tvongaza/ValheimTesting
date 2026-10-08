using System.Text.Json;
using valheim_cli.Testing;

namespace Valheim.Testing.Game;

/// <summary>
/// Checks, right after a client answers, that its ValheimCLI offers the capabilities a run will use, and says which build
/// or command pack is missing: an old monolithic ValheimCLI answers <c>cli_expect</c> but has no <c>cli_extensions</c>,
/// and a core without its packs lists no <c>valheim.session</c> or <c>valheim.world</c> commands. Read-only: one
/// <c>cli_extensions</c> call through the strictly pinned actor.
/// </summary>
public static class CliCapabilities
{
    /// <summary>The Standard pack's manifest-visible contract for the bounded teleport commands used by signal arrival.</summary>
    public const string TeleportSignals = "valheim.session/teleport-signals";
    /// <summary>What <see cref="ClientRounds"/> uses on the host: its session state, a confirmed save and the leave to its menu.</summary>
    public static readonly IReadOnlyList<string> HostedRounds = ["valheim.session/state", "valheim.session/save", "valheim.session/leave"];
    /// <summary>
    /// Every schema-1 command supplied by ValheimCLI packs that this toolkit calls directly. A release checks the pinned
    /// plugin bundle against this list, including optional observation paths, before recording its transport contract.
    /// Mod adapters supply their own capabilities and are checked when a scenario runs.
    /// </summary>
    public static readonly IReadOnlyList<string> Toolkit = Array.AsReadOnly(HostedRounds
        .Concat(["valheim.session/join", TeleportSignals, "valheim.world/player-support-wait", "valheim.world/player-support",
            "valheim.world/terrain", "valheim.world/terrain-surface", "valheim.world/terrain-paint", "valheim.world/terrain-grid"])
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());

    // The packs that register ValheimCLI's own extensions (valheimCLI's Packs/Standard and Packs/WorldTools).
    private static readonly Dictionary<string, string> Packs = new(StringComparer.Ordinal)
    {
        ["valheim.session"] = "the Standard pack (Valheim.Cli.Standard.dll, plugin valheimCLI.standard)",
        ["valheim.world"] = "the World Tools pack (Valheim.Cli.WorldTools.dll, plugin valheimCLI.worldtools)",
    };

    /// <summary>Only these owners belong to ValheimCLI's own pack manifest. A mod adapter's
    /// extension is checked from the live game after it loads, not from ValheimCLI's DLL set.</summary>
    internal static bool IsPackCapability(string path) => Packs.ContainsKey(path.Split('/')[0]);

    /// <summary>
    /// Refuses unless every one of <paramref name="paths"/> (<c>owner/command</c>) is a live command of a schema-1 result,
    /// naming every missing one and the pack that provides it, or saying that this ValheimCLI predates command packs.
    /// </summary>
    public static void Require(GameActor actor, params string[] paths) => Require(actor, (IEnumerable<string>)paths);
    /// <inheritdoc cref="Require(GameActor, string[])"/>
    public static void Require(GameActor actor, IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(paths);
        var wanted = paths.ToList();
        if (wanted.Count == 0 || wanted.Any(path => path == null || path.Split('/').Length != 2)) throw new ArgumentException("Name each capability as owner/command.", nameof(paths));
        var live = Listing(actor);
        var missing = wanted.Where(path => !live.TryGetValue(path, out int version) || version != 1).ToList();
        if (missing.Count == 0) return;
        throw new InvalidOperationException($"The {actor.Name}'s ValheimCLI lacks {string.Join(", ", missing.Select(path => live.ContainsKey(path) ? path + " (another result schema)" : path))}. " +
            string.Join(" ", missing.Select(path => path.Split('/')[0]).Distinct(StringComparer.Ordinal).Select(Provider)) +
            " Install the current ValheimCLI core in BepInEx/plugins with each pack the run needs, in plugins or scripts but not both.");
    }

    /// <summary>The live commands (<c>owner/command</c>) and their result schema versions, from one <c>cli_extensions</c> call.</summary>
    internal static Dictionary<string, int> Listing(GameActor actor)
    {
        using var document = GameActor.ParseLine(ListingReply(actor), "EXTENSIONS ");
        return Parse(document.RootElement);
    }

    /// <summary>The live commands of a <c>cli_extensions</c> result (its JSON after <c>EXTENSIONS </c>); closing owners are not live.</summary>
    internal static Dictionary<string, int> Parse(JsonElement root)
    {
        if (root.GetProperty("apiVersion").GetInt32() != 1) throw new InvalidOperationException("Unsupported extension API.");
        var live = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var extension in root.GetProperty("extensions").EnumerateArray())
        {
            if (extension.GetProperty("closing").GetBoolean()) continue;
            foreach (var command in extension.GetProperty("commands").EnumerateArray())
                live[extension.GetProperty("id").GetString() + "/" + command.GetProperty("name").GetString()] = command.GetProperty("resultVersion").GetInt32();
        }
        return live;
    }

    /// <summary>The <c>cli_extensions</c> reply; a ValheimCLI without the command is refused with what to install.</summary>
    internal static GameReply ListingReply(GameActor actor)
    {
        var reply = actor.Execute("cli_extensions", requireAccepted: false); // an old core's unknown_command is named below
        if (reply.Ok) return reply;
        if (reply.ErrorCode == "unknown_command")
            throw new InvalidOperationException($"The {actor.Name}'s ValheimCLI has no cli_extensions ({reply.Message}): it predates command packs, as an old monolithic build that still answers cli_expect does. " +
                "The toolkit discovers every session and observation command through cli_extensions: install the current ValheimCLI core in BepInEx/plugins with the Standard and World Tools packs, and pin their MD5s.");
        throw new InvalidOperationException($"{reply.ErrorCode}: {reply.Message}");
    }

    /// <summary>Which pack provides an extension owner, for messages.</summary>
    internal static string Provider(string owner) => Packs.TryGetValue(owner, out string? pack)
        ? $"{owner} comes from {pack}."
        : $"{owner} is not one of ValheimCLI's packs; load the plugin that registers it.";
}
