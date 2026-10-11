using System.Text.Json;
using valheim_cli.Testing;

namespace Valheim.Testing.Game;

/// <summary>
/// Checks, right after a client answers, that its ValheimCLI offers the capabilities a run will use, and says which build
/// is missing: an older ValheimCLI may answer <c>cli_expect</c> but lack <c>cli_extensions</c>
/// or the required built-in command. Read-only: one
/// <c>cli_extensions</c> call through the strictly pinned actor.
/// </summary>
public static class CliCapabilities
{
    /// <summary>The default plugin's manifest-visible contract for the bounded teleport commands used by signal arrival.</summary>
    public const string TeleportSignals = "valheim.session/teleport-signals";
    /// <summary>What <see cref="ClientRounds"/> uses on the host: its session state, a confirmed save and the leave to its menu.</summary>
    public static readonly IReadOnlyList<string> HostedRounds = ["valheim.session/state", "valheim.session/save", "valheim.session/leave"];
    /// <summary>
    /// Every schema-1 built-in command supplied by ValheimCLI that this toolkit calls directly. A release checks the pinned
    /// plugin bundle against this list, including optional observation paths, before recording its transport contract.
    /// Mod adapters supply their own capabilities and are checked when a scenario runs.
    /// </summary>
    public static readonly IReadOnlyList<string> Toolkit = Array.AsReadOnly(HostedRounds
        .Concat(["valheim.session/join", TeleportSignals, "valheim.world/player-support-wait", "valheim.world/player-support",
            "valheim.world/terrain", "valheim.world/terrain-surface", "valheim.world/terrain-paint", "valheim.world/terrain-grid",
            "valheim.observe/zones", "valheim.observe/harmony", "valheim.observe/content-census",
            "valheim.observe/dungeon-rooms", "valheim.observe/unresolved-prefabs", "valheim.observe/globalkeys",
            "valheim.observe/config", "valheim.observe/custom-data", "valheim.observe/review-begin",
            "valheim.observe/review-restore", "valheim.observe/review-mist-off", "valheim.observe/review-clutter-off",
            "valheim.observe/review-clip-frames"])
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());

    // Logical owners of commands compiled into the one default plugin.
    private static readonly HashSet<string> BuiltInOwners = new(StringComparer.Ordinal)
    {
        "valheim.session", "valheim.world", "valheim.observe",
    };

    /// <summary>Only these owners belong to ValheimCLI's built-in manifest. A mod adapter's
    /// extension is checked from the live game after it loads, not from ValheimCLI's DLL set.</summary>
    internal static bool IsBuiltInCapability(string path) => BuiltInOwners.Contains(path.Split('/')[0]);

    /// <summary>
    /// Refuses unless every one of <paramref name="paths"/> (<c>owner/command</c>) is a live command of a schema-1 result,
    /// naming every missing one and the default plugin that provides it.
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
            " Install one current valheimCLI.dll in BepInEx/plugins and remove old first-party pack DLLs.");
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
        var reply = actor.ReadOnlyReply("cli_extensions"); // an old core's unknown_command is named below
        if (reply.Ok) return reply;
        if (reply.ErrorCode == "unknown_command")
            throw new InvalidOperationException($"The {actor.Name}'s ValheimCLI has no cli_extensions ({reply.Message}): it predates the built-in command API. " +
                "Install the current valheimCLI.dll in BepInEx/plugins and pin its hash.");
        throw new InvalidOperationException($"{reply.ErrorCode}: {reply.Message}");
    }

    /// <summary>Which plugin provides an extension owner, for messages.</summary>
    internal static string Provider(string owner) => BuiltInOwners.Contains(owner)
        ? $"{owner} comes from the default valheimCLI.dll."
        : $"{owner} is not a ValheimCLI built-in; load the mod extension that registers it.";
}
