using System.Text.Json;
using System.Text.Json.Serialization;

namespace Valheim.Testing.Game;

/// <summary>The process a content observation comes from: a dedicated server or host, or a joined client.</summary>
[JsonConverter(typeof(CamelCaseEnum<CensusSide>))]
[ResultShape]
public enum CensusSide { Server, Client }

/// <summary>
/// What a content census found for one expected entry, or for one in-scope entry nobody expected. Only
/// <see cref="Present"/> passes; <see cref="Unsupported"/> means the census cannot answer the check and is never a pass.
/// </summary>
[JsonConverter(typeof(CamelCaseEnum<CensusState>))]
[ResultShape]
public enum CensusState
{
    /// <summary>Registered once, and the game's own lookup by its hash returns it; declared recipe or piece dependencies resolve.</summary>
    Present,
    /// <summary>Not registered on that side, not observed there, or listed but not found by the game's lookup.</summary>
    Missing,
    /// <summary>Registered in the declared scope on that side but not expected there.</summary>
    Unexpected,
    /// <summary>Registered more than once by name, or its hash is shared with another name.</summary>
    Duplicate,
    /// <summary>A recipe or piece dependency the game cannot resolve, or a piece missing from its declared tool's build table.</summary>
    Unresolved,
    /// <summary>A check the census cannot answer reliably (an older adapter, a duplicated recipe's dependencies).</summary>
    Unsupported,
}

/// <summary>Writes an enum as its camelCase name (<c>server</c>, <c>missing</c>) in evidence files.</summary>
internal sealed class CamelCaseEnum<T>() : JsonStringEnumConverter<T>(JsonNamingPolicy.CamelCase) where T : struct, Enum { }

/// <summary>
/// One piece of content a mod declares by stable identity: its <see cref="Kind"/> (<c>item</c>, <c>prefab</c>,
/// <c>recipe</c>, <c>statusEffect</c> or <c>piece</c>),
/// its <see cref="Name"/> (the prefab or recipe object's name; items and prefabs are identified by the game's
/// stable hash of it) and the <see cref="Sides"/> that must have it. A recipe may also declare the <see cref="Item"/> it
/// crafts, its <see cref="Station"/> (a crafting station's prefab name, or <c>none</c> for crafting by hand) and its
/// <see cref="Resources"/> (item prefab names). A piece must name the <see cref="Tool"/> whose build table should contain
/// it, and may also declare its station and resources. An optional dependency left null is not checked.
/// </summary>
[ResultShape]
public sealed record ExpectedContent
{
    public required string Kind { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<CensusSide> Sides { get; init; }
    public string? Item { get; init; }
    public string? Tool { get; init; }
    public string? Station { get; init; }
    public IReadOnlyList<string>? Resources { get; init; }
    /// <summary>The game's hash of the name (<see cref="StableHash.Of"/>), by which it looks items and prefabs up.</summary>
    [JsonIgnore] public int Hash => StableHash.Of(Name);
}

/// <summary>
/// What a mod declares it registers, per side, for <see cref="ContentCensus"/>: the <see cref="Owner"/> plugin whose
/// build must be the pinned one on each side, the <see cref="Scope"/> (name prefixes that are the mod's own content;
/// every expected name starts with one, and only entries in scope can be <see cref="CensusState.Unexpected"/>, so vanilla
/// content never is) and the entries. Load it from a file (<see cref="Load"/>) written as
/// <code>
/// { "owner": "example.mymod", "scope": ["MyMod_", "Recipe_MyMod_"],
///   "items":   [{ "name": "MyMod_SurveyStake", "sides": ["server", "client"] }],
///   "prefabs": [{ "name": "MyMod_SurveyStake", "sides": ["server", "client"] }],
///   "recipes": [{ "name": "Recipe_MyMod_SurveyStake", "sides": ["server", "client"],
///                 "item": "MyMod_SurveyStake", "station": "piece_workbench", "resources": ["Wood"] }],
///   "pieces": [{ "name": "MyMod_SurveyPost", "tool": "Hammer", "sides": ["server", "client"],
///                "station": "none", "resources": ["Wood"] }],
///   "statusEffects": [{ "name": "MyMod_SurveyBlessing", "sides": ["server", "client"] }] }
/// </code>
/// Anything else in the file (another key, a side other than <c>server</c> or <c>client</c>, a name twice, a name outside
/// the scope) is refused. Derive it from the mod's design, never from a census: a census copied into its own expectations
/// can only pass.
/// </summary>
public sealed class ContentExpectations
{
    /// <summary>The kinds this census observes. An older adapter that omits a kind reports it unsupported.</summary>
    public static readonly IReadOnlyList<string> ObservedKinds = ["item", "prefab", "recipe", "piece", "statusEffect"];
    /// <summary>Reserved for format kinds without an observer; empty now that pieces and effects are observed.</summary>
    public static readonly IReadOnlyList<string> LaterKinds = [];
    private static readonly Dictionary<string, string> Sections = new(StringComparer.Ordinal)
    {
        ["items"] = "item", ["prefabs"] = "prefab", ["recipes"] = "recipe", ["pieces"] = "piece", ["statusEffects"] = "statusEffect",
    };

    public string Owner { get; }
    public IReadOnlyList<string> Scope { get; }
    public IReadOnlyList<ExpectedContent> Entries { get; }

    public ContentExpectations(string owner, IEnumerable<string> scope, IEnumerable<ExpectedContent> entries)
    {
        if (string.IsNullOrWhiteSpace(owner) || owner.Any(char.IsWhiteSpace)) throw new ArgumentException("Name the owner plugin's GUID (one word).", nameof(owner));
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(entries);
        Owner = owner;
        Scope = [.. scope];
        if (Scope.Count is 0 or > 16 || Scope.Any(prefix => string.IsNullOrEmpty(prefix) || prefix.Any(char.IsWhiteSpace)) || Scope.Distinct(StringComparer.Ordinal).Count() != Scope.Count)
            throw new ArgumentException("Declare 1 to 16 different name prefixes as the scope, each one word.", nameof(scope));
        Entries = [.. entries];
        if (Entries.Count == 0) throw new ArgumentException("Declare at least one expected entry: an empty expectation proves nothing.", nameof(entries));
        foreach (var entry in Entries) Validate(entry);
        if (Entries.GroupBy(e => (e.Kind, e.Name)).FirstOrDefault(g => g.Count() > 1) is { } twice)
            throw new ArgumentException($"The {twice.Key.Kind} {twice.Key.Name} is declared twice.", nameof(entries));
    }

    /// <summary>The entries <paramref name="side"/> must have.</summary>
    public IEnumerable<ExpectedContent> For(CensusSide side) => Entries.Where(e => e.Sides.Contains(side));
    /// <summary>Whether <paramref name="name"/> is in the declared scope.</summary>
    public bool InScope(string? name) => !string.IsNullOrEmpty(name) && Scope.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));

    public static ContentExpectations Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>Reads the file format above; anything it does not know is refused.</summary>
    public static ContentExpectations Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("Content expectations are a JSON object.");
        string? owner = null;
        List<string>? scope = null;
        var entries = new List<ExpectedContent>();
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name == "owner") owner = Text(property.Value, "owner");
            else if (property.Name == "scope") scope = Strings(property.Value, "scope");
            else if (Sections.TryGetValue(property.Name, out string? section))
            {
                string kind = section;
                if (property.Value.ValueKind != JsonValueKind.Array) throw new ArgumentException($"\"{property.Name}\" is a list.");
                entries.AddRange(property.Value.EnumerateArray().Select(entry => Entry(kind, entry)));
            }
            else throw new ArgumentException($"Unknown key \"{property.Name}\" in content expectations; use owner, scope, {string.Join(", ", Sections.Keys)}.");
        }
        return new ContentExpectations(owner ?? throw new ArgumentException("Name the owner plugin's GUID (\"owner\")."),
            scope ?? throw new ArgumentException("Declare the scope (\"scope\": name prefixes)."), entries);
    }

    private static ExpectedContent Entry(string kind, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new ArgumentException($"Each {kind} is an object.");
        string? name = null, item = null, station = null, tool = null;
        List<CensusSide>? sides = null;
        List<string>? resources = null;
        foreach (var property in element.EnumerateObject())
        {
            switch (property.Name)
            {
                case "name": name = Text(property.Value, "name"); break;
                case "sides":
                    sides = Strings(property.Value, "sides").Select(side => side switch
                    {
                        "server" => CensusSide.Server,
                        "client" => CensusSide.Client,
                        _ => throw new ArgumentException($"A side is \"server\" or \"client\", not \"{side}\"."),
                    }).ToList();
                    break;
                case "item" when kind == "recipe": item = Text(property.Value, "item"); break;
                case "tool" when kind == "piece": tool = Text(property.Value, "tool"); break;
                case "station" when kind is "recipe" or "piece": station = Text(property.Value, "station"); break;
                case "resources" when kind is "recipe" or "piece": resources = Strings(property.Value, "resources"); break;
                default: throw new ArgumentException($"Unknown key \"{property.Name}\" in a {kind}; a {kind} has name and sides{(kind is "recipe" or "piece" ? ", and optional dependency fields" : "")}.");
            }
        }
        return new ExpectedContent
        {
            Kind = kind, Name = name ?? throw new ArgumentException($"A {kind} without a name."),
            Sides = sides ?? throw new ArgumentException($"Say which sides must have the {kind} {name} (\"sides\": [\"server\", \"client\"])."),
            Item = item, Tool = tool, Station = station, Resources = resources,
        };
    }

    private void Validate(ExpectedContent entry)
    {
        if (!ObservedKinds.Contains(entry.Kind))
            throw new ArgumentException($"Unknown kind \"{entry.Kind}\"; use {string.Join(", ", ObservedKinds)}.");
        if (string.IsNullOrEmpty(entry.Name) || entry.Name.Any(char.IsWhiteSpace)) throw new ArgumentException($"A {entry.Kind}'s name is one word: \"{entry.Name}\".");
        if (!InScope(entry.Name)) throw new ArgumentException($"The {entry.Kind} {entry.Name} is outside the scope [{string.Join(", ", Scope)}]: add its prefix, so the census lists it.");
        if (entry.Sides == null || entry.Sides.Count == 0 || entry.Sides.Distinct().Count() != entry.Sides.Count)
            throw new ArgumentException($"Name each side that must have the {entry.Kind} {entry.Name} once.");
        if ((entry.Kind != "recipe" && entry.Item != null) || (entry.Kind is not ("recipe" or "piece") && (entry.Station != null || entry.Resources != null)))
            throw new ArgumentException($"Only recipes and pieces declare dependencies ({entry.Kind} {entry.Name}).");
        if (entry.Kind == "piece" && entry.Tool == null)
            throw new ArgumentException($"The piece {entry.Name} must name the tool whose PieceTable should contain it.");
        if (entry.Kind != "piece" && entry.Tool != null)
            throw new ArgumentException($"Only a piece declares its tool ({entry.Kind} {entry.Name}).");
        static bool Word(string? name) => name == null || (name.Length > 0 && !name.Any(char.IsWhiteSpace));
        if (!Word(entry.Item) || !Word(entry.Tool) || !Word(entry.Station) ||
            entry.Resources is { } list && (list.Count == 0 || !list.All(r => r != null && Word(r)) || list.Distinct(StringComparer.Ordinal).Count() != list.Count))
            throw new ArgumentException($"The {entry.Kind} {entry.Name}'s dependencies are prefab names, each resource once.");
    }

    private static string Text(JsonElement element, string what) =>
        element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } text ? text : throw new ArgumentException($"\"{what}\" is a non-empty string.");
    private static List<string> Strings(JsonElement element, string what) =>
        element.ValueKind == JsonValueKind.Array ? [.. element.EnumerateArray().Select(item => Text(item, what))] : throw new ArgumentException($"\"{what}\" is a list of strings.");
}

/// <summary>The owner plugin as the observing process has it: loaded or not, its version and the MD5 of its file.</summary>
[ResultShape]
public sealed record CensusOwner(string Guid, bool Installed, string? Version, string? Md5);
/// <summary>The sizes of the observing process's whole registries and its in-scope build-table entries.</summary>
[ResultShape]
public sealed record CensusTotals(int Items, int ItemIndex, int Recipes, int Prefabs, int PrefabIndex, int? StatusEffects = null, int? Pieces = null);
/// <summary>
/// An item or prefab in scope: the game's hash of its name, how many times its registry's list holds it (0: only in the
/// index) and the name of what the game's lookup by that hash returns (null: nothing).
/// </summary>
[ResultShape]
public sealed record ObservedContent(string Name, int Hash, int Listed, string? Resolves);
/// <summary>A recipe's item, station or resource: its prefab name (null for no reference) and the game's lookup of it.</summary>
[ResultShape]
public sealed record ObservedReference(string? Name, string Lookup, int Amount = 0);
[ResultShape]
public sealed record ObservedRecipe(string Name, bool Enabled, int Amount, ObservedReference Item, ObservedReference Station, int MinStationLevel, IReadOnlyList<ObservedReference> Resources);
/// <summary>A prefab found through a build tool's PieceTable, with the dependencies of its Piece component.</summary>
[ResultShape]
public sealed record ObservedPiece(string Name, int Hash, string Tool, string Table, int Listed, string? Resolves, bool HasComponent, bool Enabled,
    ObservedReference Station, IReadOnlyList<ObservedReference> Resources);
/// <summary>A hash two different names share in a registry (<c>items</c> or <c>prefabs</c>), and the name its index holds.</summary>
[ResultShape]
public sealed record CensusCollision(string Registry, int Hash, IReadOnlyList<string> Names, string? Indexed);

/// <summary>
/// One process's content census (ValheimCLI's <c>valheim.observe/content-census</c>): which side it
/// reports being, its owner plugin's build, the whole registries' sizes, and the registered content in scope with
/// every shared hash. An older adapter may omit <see cref="StatusEffects"/> or <see cref="Pieces"/>; those checks then
/// report unsupported, never missing or present. Read with <see cref="ContentCensus.Read"/>.
/// </summary>
[ResultShape]
public sealed record ContentObservation(CensusSide Side, bool Dedicated, CensusOwner Owner, IReadOnlyList<string> Scope, CensusTotals Totals,
    IReadOnlyList<ObservedContent> Items, IReadOnlyList<ObservedContent> Prefabs, IReadOnlyList<ObservedRecipe> Recipes, IReadOnlyList<CensusCollision> Collisions,
    IReadOnlyList<ObservedContent>? StatusEffects = null, IReadOnlyList<ObservedPiece>? Pieces = null);

/// <summary>
/// The observation one side supplies, and the build its owner plugin is pinned to there (the plan's MD5 for that side, or
/// <c>absent</c>). A null <see cref="Observation"/> is a side that was not observed.
/// </summary>
[ResultShape]
public sealed record SideObservation(CensusSide Side, string PinnedBuild, ContentObservation? Observation);

/// <summary>
/// One line of a census report: on which <see cref="Side"/>, which <see cref="Kind"/> (<c>item</c>, <c>prefab</c>,
/// <c>recipe</c>, a recipe's <c>recipe-item</c>, <c>recipe-station</c> or <c>recipe-resource</c>, and the later kinds)
/// and <see cref="Name"/> (a dependency as <c>recipe/dependency</c>), the game's hash for an item or prefab, the state and
/// why.
/// </summary>
[ResultShape]
public sealed record CensusEntry(CensusSide Side, string Kind, string Name, int? Hash, CensusState State, string Detail)
{
    public override string ToString() => $"{Side.ToString().ToLowerInvariant()}: {State.ToString().ToLowerInvariant()} {Kind} {Name}{(Detail.Length > 0 ? " (" + Detail + ")" : "")}";
}

/// <summary>
/// How a side's observation was taken: whether it was supplied, which side and build it reported, and why it was refused
/// (<see cref="Problem"/>; null when it was used).
/// </summary>
[ResultShape]
public sealed record CensusSideResult(CensusSide Side, bool Expected, bool Observed, bool? Dedicated, string PinnedBuild, string? ObservedBuild, string? Problem);

/// <summary>
/// The reconciliation of a mod's <see cref="ContentExpectations"/> with each side's observation. It passes only when every
/// side with expectations was observed, by that side itself, with the pinned build of the owner, and every entry is
/// <see cref="CensusState.Present"/>; a side problem, and every other state, fails it.
/// </summary>
public sealed class ContentCensusReport
{
    public string Owner { get; }
    public IReadOnlyList<CensusSideResult> Sides { get; }
    public IReadOnlyList<CensusEntry> Entries { get; }
    internal ContentCensusReport(string owner, IReadOnlyList<CensusSideResult> sides, IReadOnlyList<CensusEntry> entries) { Owner = owner; Sides = sides; Entries = entries; }

    public bool Passed => Sides.All(s => s.Problem == null) && Entries.Count > 0 && Entries.All(e => e.State == CensusState.Present);
    /// <summary>Every side problem and every entry that is not present, one line each.</summary>
    public IReadOnlyList<string> Failures =>
        [.. Sides.Where(s => s.Problem != null).Select(s => $"{s.Side.ToString().ToLowerInvariant()}: {s.Problem}"),
         .. Entries.Where(e => e.State != CensusState.Present).Select(e => e.ToString())];

    /// <summary>Throws unless the census passed, naming every failure.</summary>
    public void RequirePassed()
    {
        if (Passed) return;
        var failures = Failures;
        throw new InvalidOperationException($"Content census of {Owner} failed, {failures.Count} problem(s): {string.Join("; ", failures)}.");
    }
}

/// <summary>
/// Reads each side's registered content and reconciles it with what the mod declares. A registration double can show that
/// a mod calls ObjectDB or ZNetScene; only the running game shows what each process ended up with, and a client's
/// registries are its own: a server observation never stands for a client. <see cref="Read"/> takes one process's census
/// through a strictly pinned actor; <see cref="Reconcile"/> judges all sides together.
/// </summary>
public static class ContentCensus
{
    public const string Source = "content-census";

    /// <summary>
    /// One complete census through <paramref name="capabilityPath"/> (for example <c>valheim.observe/content-census</c>), for
    /// the owner and scope of <paramref name="expectations"/>. A census that is not ready (no world, registries not
    /// populated, a client's player not spawned), an empty registry, a reply for another owner or scope, or any malformed
    /// reply throws: none of them is an empty census.
    /// </summary>
    public static ContentObservation Read(GameActor actor, string capabilityPath, ContentExpectations expectations)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(expectations);
        var observation = actor.Observe(actor.RequireCapability(capabilityPath), [expectations.Owner, .. expectations.Scope]);
        if (observation.Source != Source) throw new InvalidOperationException("Wrong observation layer: " + observation.Source + ".");
        if (!observation.Complete)
            throw new InvalidOperationException($"The {actor.Name}'s content census is not ready: " +
                (observation.Data.TryGetProperty("reason", out var reason) && reason.GetString() is { Length: > 0 } why ? why.TrimEnd('.') : "no reason given") + ".");
        var parsed = Parse(observation.Data);
        if (parsed.Owner.Guid != expectations.Owner || !parsed.Scope.SequenceEqual(expectations.Scope, StringComparer.Ordinal))
            throw new InvalidOperationException($"The {actor.Name}'s census answers for {parsed.Owner.Guid} [{string.Join(", ", parsed.Scope)}], not the requested {expectations.Owner} [{string.Join(", ", expectations.Scope)}].");
        return parsed;
    }

    /// <summary>Parses a complete census; anything malformed, incomplete or empty throws rather than reading as "nothing registered".</summary>
    public static ContentObservation Parse(JsonElement data)
    {
        try { return ParseComplete(data); }
        catch (Exception error) when (error is KeyNotFoundException or FormatException)
        { throw new InvalidOperationException("A malformed content census: " + error.Message, error); }
    }

    private static ContentObservation ParseComplete(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("source", out var source) || source.GetString() != Source ||
            !data.TryGetProperty("complete", out var complete) || complete.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("Not a complete content census.");
        var side = Text(data, "side") switch
        {
            "server" => CensusSide.Server,
            "client" => CensusSide.Client,
            var other => throw new InvalidOperationException($"A census from an unknown side \"{other}\"."),
        };
        var owner = data.GetProperty("owner");
        var census = new ContentObservation(side, data.GetProperty("dedicated").GetBoolean(),
            new CensusOwner(Text(owner, "guid"), owner.GetProperty("installed").GetBoolean(), Optional(owner, "version"), Optional(owner, "md5")),
            [.. data.GetProperty("scope").EnumerateArray().Select(prefix => prefix.GetString() is { Length: > 0 } text ? text : throw new InvalidOperationException("A census scope with an empty prefix."))],
            Totals(data.GetProperty("totals")), Contents(data, "items"), Contents(data, "prefabs"),
            [.. data.GetProperty("recipes").EnumerateArray().Select(ParseRecipe)],
            [.. data.GetProperty("collisions").EnumerateArray().Select(Collision)],
            data.TryGetProperty("statusEffects", out _) ? Contents(data, "statusEffects") : null,
            data.TryGetProperty("pieces", out var pieces) ? [.. pieces.EnumerateArray().Select(ParsePiece)] : null);
        // A registry without anything, not even the game's own content, was not read from a loaded world.
        if (census.Totals.Items == 0 || census.Totals.ItemIndex == 0 || census.Totals.Prefabs == 0 || census.Totals.PrefabIndex == 0)
            throw new InvalidOperationException($"An empty registry is not a census: {census.Totals}.");
        return census;
    }

    /// <summary>
    /// Judges every side's observation against <paramref name="expectations"/>. For each side that must have content: the
    /// side is observed (a null observation, or none supplied, fails), by that side itself (a server observation supplied as
    /// the client's is refused), and with the owner's pinned build (<see cref="SideObservation.PinnedBuild"/>: the MD5, or a
    /// prefix of at least 8 hex characters, as strict pins take it). A refused or missing observation fails the side, and
    /// its expected entries are <see cref="CensusState.Missing"/>. On an accepted observation each expected entry gets its
    /// state, and every in-scope entry that side was not expected to have is <see cref="CensusState.Unexpected"/>.
    /// </summary>
    public static ContentCensusReport Reconcile(ContentExpectations expectations, params SideObservation[] observed)
    {
        ArgumentNullException.ThrowIfNull(expectations);
        ArgumentNullException.ThrowIfNull(observed);
        if (observed.GroupBy(o => o.Side).FirstOrDefault(g => g.Count() > 1) is { } twice)
            throw new ArgumentException($"The {twice.Key.ToString().ToLowerInvariant()} side is supplied twice.", nameof(observed));
        var sides = new List<CensusSideResult>();
        var entries = new List<CensusEntry>();
        foreach (var side in new[] { CensusSide.Server, CensusSide.Client })
        {
            var wanted = expectations.For(side).ToList();
            var supplied = observed.FirstOrDefault(o => o.Side == side);
            if (wanted.Count == 0 && supplied?.Observation == null) continue;
            string pinned = supplied?.PinnedBuild ?? "";
            var observation = supplied?.Observation;
            string? problem = Refusal(expectations, side, wanted.Count > 0, pinned, observation);
            sides.Add(new CensusSideResult(side, wanted.Count > 0, observation != null, observation?.Dedicated, pinned, observation?.Owner.Md5, problem));
            if (problem != null)
            {
                entries.AddRange(wanted.Select(e => new CensusEntry(side, e.Kind, e.Name, Hashed(e), CensusState.Missing, "no accepted " + Name(side) + " observation")));
                continue;
            }
            entries.AddRange(Judge(expectations, side, wanted, observation!));
        }
        return new ContentCensusReport(expectations.Owner, sides, entries);
    }

    private static string? Refusal(ContentExpectations expectations, CensusSide side, bool expected, string pinned, ContentObservation? observation)
    {
        string name = Name(side);
        if (observation == null)
            return expected ? $"no {name} observation: {name} expectations are never satisfied by another side's census" : null;
        if (observation.Side != side)
            return $"the observation supplied as the {name}'s reports being the {Name(observation.Side)}; a {name} expectation is never satisfied by another side's census";
        if (observation.Owner.Guid != expectations.Owner || !observation.Scope.SequenceEqual(expectations.Scope, StringComparer.Ordinal))
            return $"the observation answers for {observation.Owner.Guid} [{string.Join(", ", observation.Scope)}], not {expectations.Owner} [{string.Join(", ", expectations.Scope)}]";
        if (pinned == "absent")
            return expected ? $"{expectations.Owner} is pinned absent on the {name}, yet content is expected there" : null;
        if (pinned.Length is < 8 or > 32 || !pinned.All(Uri.IsHexDigit))
            return $"pin {expectations.Owner}'s build on the {name} by its MD5 (at least 8 hex characters), not \"{pinned}\"";
        if (!observation.Owner.Installed || observation.Owner.Md5 == null)
            return $"{expectations.Owner} is not loaded on the {name}";
        if (!observation.Owner.Md5.StartsWith(pinned, StringComparison.OrdinalIgnoreCase))
            return $"stale build: the {name}'s {expectations.Owner} is md5 {observation.Owner.Md5}, pinned {pinned}; its census is refused";
        return null;
    }

    private static IEnumerable<CensusEntry> Judge(ContentExpectations expectations, CensusSide side, List<ExpectedContent> wanted, ContentObservation census)
    {
        foreach (var entry in wanted)
        {
            switch (entry.Kind)
            {
                case "item": yield return Registered(side, entry, census.Items, census.Collisions, "items", "ObjectDB"); break;
                case "prefab": yield return Registered(side, entry, census.Prefabs, census.Collisions, "prefabs", "ZNetScene"); break;
                case "recipe": foreach (var line in JudgeRecipe(side, entry, census.Recipes)) yield return line; break;
                case "statusEffect": yield return census.StatusEffects == null
                    ? new CensusEntry(side, entry.Kind, entry.Name, entry.Hash, CensusState.Unsupported, "this adapter does not observe status effects")
                    : Registered(side, entry, census.StatusEffects, census.Collisions, "statusEffects", "ObjectDB status effects"); break;
                case "piece": foreach (var line in JudgePiece(side, entry, census)) yield return line; break;
                default: yield return new CensusEntry(side, entry.Kind, entry.Name, null, CensusState.Unsupported, $"this census does not observe {entry.Kind} registrations yet"); break;
            }
        }
        foreach (var (kind, names) in new[] { ("item", census.Items.Select(i => i.Name)), ("prefab", census.Prefabs.Select(p => p.Name)), ("recipe", census.Recipes.Select(r => r.Name)),
                     ("statusEffect", census.StatusEffects?.Select(e => e.Name) ?? []), ("piece", census.Pieces?.Select(e => e.Name) ?? []) })
            foreach (string name in names.Distinct(StringComparer.Ordinal))
                if (expectations.InScope(name) && !wanted.Any(e => e.Kind == kind && e.Name == name))
                    yield return new CensusEntry(side, kind, name, kind == "recipe" ? null : StableHash.Of(name), CensusState.Unexpected, $"registered on the {Name(side)} in the declared scope, not expected there");
    }

    private static CensusEntry Registered(CensusSide side, ExpectedContent entry, IReadOnlyList<ObservedContent> observed, IReadOnlyList<CensusCollision> collisions, string registry, string where)
    {
        int hash = entry.Hash;
        var found = observed.FirstOrDefault(o => o.Name == entry.Name);
        var shared = collisions.FirstOrDefault(c => c.Registry == registry && c.Hash == hash && c.Names.Contains(entry.Name));
        CensusEntry Line(CensusState state, string detail) => new(side, entry.Kind, entry.Name, hash, state, detail);
        if (found == null) return Line(CensusState.Missing, $"not registered in {where}");
        if (found.Listed > 1) return Line(CensusState.Duplicate, $"{where} lists it {found.Listed} times");
        if (shared != null) return Line(CensusState.Duplicate, $"hash {hash} is shared with {string.Join(", ", shared.Names.Where(n => n != entry.Name))}; the index holds {shared.Indexed ?? "nothing"} for it");
        if (found.Resolves == null) return Line(CensusState.Missing, $"listed in {where}, but the game's lookup of hash {hash} finds nothing: registered after the index was built");
        if (found.Resolves != entry.Name) return Line(CensusState.Duplicate, $"the game's lookup of hash {hash} returns {found.Resolves}");
        return Line(CensusState.Present, found.Listed == 0 ? $"in {where}'s index only, not its list" : "");
    }

    private static IEnumerable<CensusEntry> JudgeRecipe(CensusSide side, ExpectedContent entry, IReadOnlyList<ObservedRecipe> recipes)
    {
        var found = recipes.Where(r => r.Name == entry.Name).ToList();
        CensusEntry Line(string kind, string name, CensusState state, string detail) => new(side, kind, name, null, state, detail);
        var dependencies = new List<(string Kind, string Name)>();
        if (entry.Item != null) dependencies.Add(("recipe-item", entry.Item));
        if (entry.Station != null) dependencies.Add(("recipe-station", entry.Station));
        dependencies.AddRange((entry.Resources ?? Array.Empty<string>()).Select(r => ("recipe-resource", r)));
        if (found.Count == 0) { yield return Line("recipe", entry.Name, CensusState.Missing, "not registered in ObjectDB"); yield break; }
        if (found.Count > 1)
        {
            yield return Line("recipe", entry.Name, CensusState.Duplicate, $"ObjectDB lists {found.Count} recipes named {entry.Name}");
            // Which of them the crafting menu uses depends on list order; no dependency of "the" recipe can be judged.
            foreach (var (kind, name) in dependencies)
                yield return Line(kind, entry.Name + "/" + name, CensusState.Unsupported, "the recipe is registered more than once");
            yield break;
        }
        var recipe = found[0];
        yield return Line("recipe", entry.Name, CensusState.Present, recipe.Enabled ? "" : "disabled");
        if (entry.Item != null) yield return Dependency(side, entry.Name, "recipe-item", entry.Item, recipe.Item, "crafts");
        if (entry.Station != null)
        {
            string name = entry.Name + "/" + entry.Station;
            if (entry.Station == "none")
                yield return recipe.Station.Lookup == "none" ? Line("recipe-station", name, CensusState.Present, "crafted by hand")
                    : Line("recipe-station", name, CensusState.Unresolved, $"needs {recipe.Station.Name ?? "an unnamed station"}, declared to be crafted by hand");
            else if (recipe.Station.Lookup == "none")
                yield return Line("recipe-station", name, CensusState.Unresolved, $"crafted by hand, declared to need {entry.Station}");
            else yield return Dependency(side, entry.Name, "recipe-station", entry.Station, recipe.Station, "needs");
        }
        if (entry.Resources != null)
        {
            foreach (string resource in entry.Resources)
            {
                var matches = recipe.Resources.Where(r => r.Name == resource).ToList();
                yield return matches.Count switch
                {
                    0 => Line("recipe-resource", entry.Name + "/" + resource, CensusState.Unresolved, $"not among its resources [{string.Join(", ", recipe.Resources.Select(r => r.Name ?? "none"))}]"),
                    1 => Dependency(side, entry.Name, "recipe-resource", resource, matches[0], "uses"),
                    _ => Line("recipe-resource", entry.Name + "/" + resource, CensusState.Duplicate, $"listed {matches.Count} times among its resources"),
                };
            }
            foreach (var extra in recipe.Resources.Where(r => r.Name == null || !entry.Resources.Contains(r.Name)))
                yield return Line("recipe-resource", entry.Name + "/" + (extra.Name ?? "none"), CensusState.Unexpected,
                    extra.Name == null ? "a resource without an item" : "a resource that is not declared");
        }
    }

    private static IEnumerable<CensusEntry> JudgePiece(CensusSide side, ExpectedContent entry, ContentObservation census)
    {
        CensusEntry Line(string kind, string name, CensusState state, string detail) => new(side, kind, name, kind == "piece" ? entry.Hash : null, state, detail);
        if (census.Pieces == null)
        {
            yield return Line("piece", entry.Name, CensusState.Unsupported, "this adapter does not observe build tables");
            yield break;
        }
        var found = census.Pieces.Where(piece => piece.Name == entry.Name && piece.Tool == entry.Tool).ToList();
        if (found.Count == 0)
        {
            bool prefabOnly = census.Prefabs.Any(prefab => prefab.Name == entry.Name);
            yield return Line("piece", entry.Name, prefabOnly ? CensusState.Unresolved : CensusState.Missing,
                prefabOnly ? $"prefab exists, but {entry.Tool}'s PieceTable does not contain it" : $"not in {entry.Tool}'s PieceTable or ZNetScene");
            yield break;
        }
        if (found.Count > 1 || found[0].Listed > 1)
        {
            yield return Line("piece", entry.Name, CensusState.Duplicate, $"{entry.Tool}'s PieceTable lists it more than once");
            yield break;
        }
        var piece = found[0];
        if (census.Collisions.Any(collision => collision.Registry == "prefabs" && collision.Hash == entry.Hash && collision.Names.Contains(entry.Name)))
        {
            yield return Line("piece", entry.Name, CensusState.Duplicate, "its prefab hash is shared with another name in ZNetScene");
            yield break;
        }
        if (!piece.HasComponent || !piece.Enabled)
        {
            yield return Line("piece", entry.Name, CensusState.Unresolved, !piece.HasComponent ? "build table entry lacks a Piece component" : "the Piece component is disabled");
            yield break;
        }
        if (piece.Resolves != entry.Name || !census.Prefabs.Any(prefab => prefab.Name == entry.Name && prefab.Resolves == entry.Name))
        {
            yield return Line("piece", entry.Name, CensusState.Unresolved, "build table has it, but ZNetScene does not resolve its prefab");
            yield break;
        }
        yield return Line("piece", entry.Name, CensusState.Present, $"in {entry.Tool}'s {piece.Table}");
        if (entry.Station != null)
        {
            string name = entry.Name + "/" + entry.Station;
            if (entry.Station == "none")
                yield return piece.Station.Lookup == "none" ? Line("piece-station", name, CensusState.Present, "no crafting station")
                    : Line("piece-station", name, CensusState.Unresolved, $"needs {piece.Station.Name ?? "an unnamed station"}, declared none");
            else if (piece.Station.Lookup == "none")
                yield return Line("piece-station", name, CensusState.Unresolved, $"has no station, declared to need {entry.Station}");
            else yield return Dependency(side, entry.Name, "piece-station", entry.Station, piece.Station, "needs");
        }
        if (entry.Resources != null)
        {
            foreach (string resource in entry.Resources)
            {
                var matches = piece.Resources.Where(r => r.Name == resource).ToList();
                yield return matches.Count switch
                {
                    0 => Line("piece-resource", entry.Name + "/" + resource, CensusState.Unresolved, "not among its resources"),
                    1 => Dependency(side, entry.Name, "piece-resource", resource, matches[0], "uses"),
                    _ => Line("piece-resource", entry.Name + "/" + resource, CensusState.Duplicate, "resource listed more than once"),
                };
            }
            foreach (var extra in piece.Resources.Where(r => r.Name == null || !entry.Resources.Contains(r.Name)))
                yield return Line("piece-resource", entry.Name + "/" + (extra.Name ?? "none"), CensusState.Unexpected, "undeclared resource");
        }
    }

    private static CensusEntry Dependency(CensusSide side, string recipe, string kind, string declared, ObservedReference observed, string verb)
    {
        string name = recipe + "/" + declared;
        return observed.Lookup switch
        {
            _ when observed.Name != declared && observed.Lookup != "unsupported" =>
                new(side, kind, name, null, CensusState.Unresolved, $"{verb} {observed.Name ?? "nothing"}, not the declared {declared}"),
            "resolved" => new(side, kind, name, null, CensusState.Present, ""),
            "unresolved" => new(side, kind, name, null, CensusState.Unresolved, $"the game's lookup does not find {declared}"),
            "unsupported" => new(side, kind, name, null, CensusState.Unsupported, "a reference without a name, which no lookup can answer"),
            var other => new(side, kind, name, null, CensusState.Unsupported, $"unknown lookup result \"{other}\""),
        };
    }

    private static int? Hashed(ExpectedContent entry) => entry.Kind is "item" or "prefab" or "piece" or "statusEffect" ? entry.Hash : null;
    private static string Name(CensusSide side) => side == CensusSide.Server ? "server" : "client";

    private static CensusTotals Totals(JsonElement totals)
    {
        var values = new CensusTotals(totals.GetProperty("items").GetInt32(), totals.GetProperty("itemIndex").GetInt32(), totals.GetProperty("recipes").GetInt32(),
            totals.GetProperty("prefabs").GetInt32(), totals.GetProperty("prefabIndex").GetInt32(),
            totals.TryGetProperty("statusEffects", out var status) ? status.GetInt32() : null,
            totals.TryGetProperty("pieces", out var pieces) ? pieces.GetInt32() : null);
        if (values.Items < 0 || values.ItemIndex < 0 || values.Recipes < 0 || values.Prefabs < 0 || values.PrefabIndex < 0 || values.StatusEffects < 0 || values.Pieces < 0)
            throw new InvalidOperationException("Negative census totals.");
        return values;
    }

    private static List<ObservedContent> Contents(JsonElement data, string name)
    {
        var list = new List<ObservedContent>();
        foreach (var entry in data.GetProperty(name).EnumerateArray())
        {
            string text = Text(entry, "name");
            int hash = entry.GetProperty("hash").GetInt32();
            // The hash is the game's: a disagreement means this runner would name the wrong hashes.
            if (hash != StableHash.Of(text)) throw new InvalidOperationException($"The game hashes {text} to {hash}; StableHash.Of gives {StableHash.Of(text)}.");
            int listed = entry.GetProperty("listed").GetInt32();
            if (listed < 0) throw new InvalidOperationException($"A negative list count for {text}.");
            list.Add(new ObservedContent(text, hash, listed, Optional(entry, "resolves")));
        }
        if (list.Select(e => e.Name).Distinct(StringComparer.Ordinal).Count() != list.Count) throw new InvalidOperationException($"A census that lists one of its {name} twice.");
        return list;
    }

    private static ObservedRecipe ParseRecipe(JsonElement recipe) => new(Text(recipe, "name"), recipe.GetProperty("enabled").GetBoolean(), recipe.GetProperty("amount").GetInt32(),
        Reference(recipe.GetProperty("item")), Reference(recipe.GetProperty("station")), recipe.GetProperty("minStationLevel").GetInt32(),
        [.. recipe.GetProperty("resources").EnumerateArray().Select(Reference)]);

    private static ObservedPiece ParsePiece(JsonElement piece)
    {
        string name = Text(piece, "name");
        int hash = piece.GetProperty("hash").GetInt32();
        if (hash != StableHash.Of(name)) throw new InvalidOperationException($"The game hashes {name} to {hash}; StableHash.Of gives {StableHash.Of(name)}.");
        int listed = piece.GetProperty("listed").GetInt32();
        if (listed < 1) throw new InvalidOperationException($"A build table lists {name} {listed} times.");
        return new ObservedPiece(name, hash, Text(piece, "tool"), Text(piece, "table"), listed, Optional(piece, "resolves"), piece.GetProperty("hasComponent").GetBoolean(),
            piece.GetProperty("enabled").GetBoolean(), Reference(piece.GetProperty("station")),
            [.. piece.GetProperty("resources").EnumerateArray().Select(Reference)]);
    }

    private static ObservedReference Reference(JsonElement reference)
    {
        string lookup = Text(reference, "lookup");
        if (lookup is not ("resolved" or "unresolved" or "unsupported" or "none")) throw new InvalidOperationException($"Unknown lookup result \"{lookup}\".");
        return new ObservedReference(Optional(reference, "name"), lookup, reference.TryGetProperty("amount", out var amount) ? amount.GetInt32() : 0);
    }

    private static CensusCollision Collision(JsonElement collision)
    {
        string registry = Text(collision, "registry");
        if (registry is not ("items" or "prefabs" or "statusEffects")) throw new InvalidOperationException($"Unknown registry \"{registry}\".");
        string[] names = [.. collision.GetProperty("names").EnumerateArray().Select(n => n.GetString() ?? throw new InvalidOperationException("A collision with a null name."))];
        if (names.Length < 2) throw new InvalidOperationException("A collision needs two names.");
        return new CensusCollision(registry, collision.GetProperty("hash").GetInt32(), names, Optional(collision, "indexed"));
    }

    private static string Text(JsonElement element, string name) =>
        element.GetProperty(name).GetString() is { Length: > 0 } text ? text : throw new InvalidOperationException("A content census field without " + name + ".");
    private static string? Optional(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
