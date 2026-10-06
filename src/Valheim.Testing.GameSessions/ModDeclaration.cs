using Valheim.Testing.Game;
namespace Valheim.Testing.GameSessions;

/// <summary>
/// What a mod declares to the runner once (#258): the read-only session capability its test adapter serves (for example
/// <c>my.mod.testing/session</c>) and the environment variable that passes the owned session token to it, and, optionally,
/// the Harmony patches it applies. With <see cref="HarmonyCapability"/>, <see cref="Owner"/> and <see cref="Patches"/>, a
/// <see cref="GameSession"/> checks at runtime-ready, as the Setup step "server: the mod's Harmony patches are applied", that
/// every declared patch is applied on its owned server (<see cref="RequirePatchesApplied"/>) when the server's pins load the
/// mod (<see cref="Plugin"/> pinned to a build) or the plan is unpinned, so no scenario runs on a server where the mod is
/// loaded but not patched in. Clients are not checked by the session yet.
/// </summary>
public sealed record ModDeclaration(string SessionCapability, string SessionTokenVariable)
{
    /// <summary>The test adapter's Harmony census capability, for example <c>my.mod.testing/harmony</c>; null declares no check.</summary>
    public string? HarmonyCapability { get; init; }
    /// <summary>The mod's Harmony ID (<c>new Harmony(id)</c>), whose patches the census lists.</summary>
    public string? Owner { get; init; }
    /// <summary>The plugin GUID an actor's pins name when it loads the mod; the <see cref="Owner"/> when left out.</summary>
    public string? Plugin { get; init; }
    /// <summary>The patches the mod applies; every one must be in the census.</summary>
    public IReadOnlyList<DeclaredPatch> Patches { get; init; } = [];

    /// <summary>Whether a Harmony check is declared (after <see cref="Validate"/>).</summary>
    internal bool ChecksPatches => HarmonyCapability != null;

    /// <summary>Refuses a declaration that names only part of the Harmony check, or a patch kind Harmony has not, before anything is copied.</summary>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SessionCapability);
        ArgumentException.ThrowIfNullOrWhiteSpace(SessionTokenVariable);
        if (HarmonyCapability == null && Owner == null && Plugin == null && Patches.Count == 0) return;
        if (string.IsNullOrWhiteSpace(HarmonyCapability) || string.IsNullOrWhiteSpace(Owner) || Patches.Count == 0)
            throw new ArgumentException("Declare the mod's Harmony check whole: HarmonyCapability, Owner and at least one patch.");
        if (Patches.FirstOrDefault(patch => !HarmonyCensus.Kinds.Contains(patch.Kind)) is { } unknown)
            throw new ArgumentException("Unknown Harmony patch kind in the mod's declaration: " + unknown);
    }

    /// <summary>Whether <paramref name="pins"/> (an actor's strict pins) load the mod: its <see cref="Plugin"/> pinned to a build, not absent.</summary>
    internal bool PinnedIn(IReadOnlyDictionary<string, string> pins) =>
        (Plugin ?? Owner) is { } plugin && pins.TryGetValue(plugin, out var pin) && pin != "absent";

    /// <summary>Every declared patch is applied on <paramref name="actor"/> (its adapter's census, <see cref="HarmonyCensus.Check"/>).</summary>
    public void RequirePatchesApplied(GameActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!ChecksPatches) throw new InvalidOperationException("This declaration names no Harmony check.");
        HarmonyCensus.Read(actor, HarmonyCapability!, Owner).Check(Owner!, Patches).RequireApplied();
    }
}
