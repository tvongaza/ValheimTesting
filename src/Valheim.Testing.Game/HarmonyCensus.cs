using System.Text.Json;

namespace Valheim.Testing.Game;

/// <summary>
/// One applied Harmony patch, as the adapter's census reports it. <see cref="Method"/> and <see cref="Patch"/> are
/// <c>Type::Name(ParameterType,...)</c>; <see cref="Kind"/> is prefix, postfix, transpiler, finalizer or ilmanipulator.
/// </summary>
public sealed record AppliedPatch(string Method, string Owner, string Kind, int Priority, int Index, IReadOnlyList<string> Before, IReadOnlyList<string> After, string? Patch)
{
    public override string ToString() => $"{Owner} {Kind} {Patch ?? "?"} on {Method} (priority {Priority})";
}

/// <summary>
/// A patch the mod declares: its target, <c>Type::Name</c> for any overload or <c>Type::Name(ParameterType,...)</c> for
/// one; its kind; and optionally the patch method (<c>Type::Name</c>, with or without parameters). Types are written as
/// .NET writes them: <c>Terminal</c>, <c>MyMod.Plugin+RegisterCommands</c>, <c>System.Int32</c>.
/// </summary>
public sealed record DeclaredPatch(string Target, string Kind, string? Patch = null)
{
    public override string ToString() => $"{Kind} {Patch ?? "(any method)"} on {Target}";
}

/// <summary>
/// The Harmony patches applied in the game, read from the adapter's census (<c>HarmonyCensus.Command()</c> in
/// Valheim.Testing.Adapter). HarmonyX only logs a warning when a patch's target is missing, so a half-patched mod passes
/// every test that does not ask; <see cref="Check"/> asks. The census is read-only and may be re-read.
/// </summary>
public sealed class HarmonyCensus
{
    public const string Source = "harmony-patches";
    private static readonly string[] Kinds = ["prefix", "postfix", "transpiler", "finalizer", "ilmanipulator"];
    /// <summary>Every method the census lists, including one whose patches have all been removed.</summary>
    public IReadOnlyList<string> Methods { get; }
    public IReadOnlyList<AppliedPatch> Patches { get; }
    private HarmonyCensus(IReadOnlyList<string> methods, IReadOnlyList<AppliedPatch> patches) { Methods = methods; Patches = patches; }

    /// <summary>
    /// Reads a complete census through <paramref name="capabilityPath"/> (for example <c>mymod.testing/harmony</c>). With
    /// <paramref name="owner"/> the adapter lists only the methods that Harmony ID patches, with every owner's patches on
    /// them: enough for <see cref="Check"/> and smaller than a whole mod list's census.
    /// </summary>
    public static HarmonyCensus Read(GameActor actor, string capabilityPath, string? owner = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var capability = actor.RequireCapability(capabilityPath);
        var observation = owner == null ? actor.ObserveComplete(capability, Source) : actor.ObserveComplete(capability, Source, owner);
        return Parse(observation.Data);
    }

    /// <summary>Parses census data; anything malformed or incomplete throws rather than reading as "no patches".</summary>
    public static HarmonyCensus Parse(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("source", out var source) || source.GetString() != Source ||
            !data.TryGetProperty("complete", out var complete) || complete.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("Not a complete Harmony census.");
        var methods = new List<string>();
        var patches = new List<AppliedPatch>();
        foreach (var entry in data.GetProperty("methods").EnumerateArray())
        {
            string method = Text(entry, "method");
            methods.Add(method);
            foreach (var patch in entry.GetProperty("patches").EnumerateArray())
            {
                string kind = Text(patch, "kind");
                if (!Kinds.Contains(kind)) throw new InvalidOperationException("Unknown Harmony patch kind: " + kind);
                patches.Add(new AppliedPatch(method, Text(patch, "owner"), kind, patch.GetProperty("priority").GetInt32(), patch.GetProperty("index").GetInt32(),
                    Strings(patch, "before"), Strings(patch, "after"), patch.GetProperty("patch").GetString()));
            }
        }
        return new HarmonyCensus(methods, patches);
    }

    /// <summary>
    /// Compares <paramref name="owner"/>'s applied patches with the ones the mod declares: which declared patch is missing,
    /// which applied one the list does not declare, and which patches of other owners sit on the same methods.
    /// </summary>
    public HarmonyPatchCheck Check(string owner, IEnumerable<DeclaredPatch> declared)
    {
        ArgumentException.ThrowIfNullOrEmpty(owner);
        ArgumentNullException.ThrowIfNull(declared);
        var wanted = declared.ToArray();
        foreach (var patch in wanted)
            if (!Kinds.Contains(patch.Kind)) throw new ArgumentException("Unknown Harmony patch kind: " + patch.Kind, nameof(declared));
        var own = Patches.Where(p => p.Owner == owner).ToArray();
        var missing = wanted.Where(d => !own.Any(p => Matches(d, p))).ToArray();
        var undeclared = own.Where(p => !wanted.Any(d => Matches(d, p))).ToArray();
        var methods = own.Select(p => p.Method).Concat(Methods.Where(m => wanted.Any(d => Targets(d.Target, m)))).ToHashSet(StringComparer.Ordinal);
        var others = Patches.Where(p => p.Owner != owner && methods.Contains(p.Method)).ToArray();
        return new HarmonyPatchCheck(owner, missing, undeclared, others);
    }

    /// <summary>
    /// How other owners' patches differ between two censuses, ignoring <paramref name="owner"/>'s: for example before and
    /// after that owner's extension or plugin reloads, which must leave everybody else's patches in place. Empty when
    /// nothing changed; otherwise one line per removed or added patch.
    /// </summary>
    public static IReadOnlyList<string> OthersChanged(HarmonyCensus before, HarmonyCensus after, string owner)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentException.ThrowIfNullOrEmpty(owner);
        // Index is a position among a method's patches; it shifts when the owner's own patches go, so it is not compared.
        static string Key(AppliedPatch p) => $"{p.Owner} {p.Kind} {p.Patch} on {p.Method} priority={p.Priority} before=[{string.Join(",", p.Before)}] after=[{string.Join(",", p.After)}]";
        var remaining = after.Patches.Where(p => p.Owner != owner).Select(Key).ToList();
        var changes = new List<string>();
        foreach (string key in before.Patches.Where(p => p.Owner != owner).Select(Key))
            if (!remaining.Remove(key)) changes.Add("removed: " + key);
        changes.AddRange(remaining.Select(key => "added: " + key));
        return changes;
    }

    private static bool Matches(DeclaredPatch declared, AppliedPatch applied) =>
        declared.Kind == applied.Kind && Targets(declared.Target, applied.Method) &&
        (declared.Patch == null || (applied.Patch != null && Targets(declared.Patch, applied.Patch)));
    // "Type::Name" names every overload; with a parameter list it names exactly one.
    private static bool Targets(string declared, string method) =>
        declared.Contains('(') ? declared == method : declared == (method.IndexOf('(') is var open and >= 0 ? method[..open] : method);
    private static string Text(JsonElement element, string name) =>
        element.GetProperty(name).GetString() is { Length: > 0 } text ? text : throw new InvalidOperationException("Harmony census entry without " + name + ".");
    private static string[] Strings(JsonElement element, string name) =>
        [.. element.GetProperty(name).EnumerateArray().Select(item => item.GetString() ?? throw new InvalidOperationException("Harmony census " + name + " holds a non-string."))];
}

/// <summary>The result of <see cref="HarmonyCensus.Check"/>.</summary>
public sealed record HarmonyPatchCheck(string Owner, IReadOnlyList<DeclaredPatch> Missing, IReadOnlyList<AppliedPatch> Undeclared, IReadOnlyList<AppliedPatch> OtherOwners)
{
    /// <summary>
    /// Throws unless every declared patch is applied, naming each missing one. Other owners' patches on the same methods
    /// are listed in the message for context but never fail it: sharing a method is normal, and only a test that knows the
    /// load order can judge it.
    /// </summary>
    public void RequireApplied()
    {
        if (Missing.Count == 0) return;
        string others = OtherOwners.Count == 0 ? "" : " Other owners on these methods: " + string.Join("; ", OtherOwners) + ".";
        throw new InvalidOperationException($"{Missing.Count} declared Harmony patch(es) of {Owner} not applied: {string.Join("; ", Missing)}.{others}");
    }
}
