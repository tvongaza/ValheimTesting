using System.Text.RegularExpressions;

namespace Valheim.Testing.Game;

/// <summary>
/// Requested values for one new owned hosted world, created by the pinned game's cli_create_world command at its menu.
/// This is a request, not a created world, UID, save-path identity or proof of ownership. Names use the toolkit's portable
/// subset: 3–64 ASCII letters/digits/underscore/hyphen, beginning alphanumeric, excluding Windows device names. Seeds are
/// nonempty ASCII alphanumeric text, preserved exactly including case. These are supported input rules, not Valheim's full grammar.
/// </summary>
[ResultShape]
public sealed record SeededWorldRequest
{
    public SeededWorldRequest(string name, string seed)
    {
        if (name == null || name.Length is < 3 or > 64 || !NamePattern.IsMatch(name) || DevicePattern.IsMatch(name))
            throw new ArgumentException("A seeded world name uses 3–64 ASCII letters/digits/_/-, begins alphanumeric and is not a Windows device name.", nameof(name));
        if (seed == null || !SeedPattern.IsMatch(seed))
            throw new ArgumentException("A world seed is nonempty ASCII alphanumeric text; its case is preserved.", nameof(seed));
        _ = WorldIdentity.RequireName(name);
        Name = name; Seed = seed;
    }
    /// <summary>The requested fresh world name, never silently renamed.</summary>
    public string Name { get; }
    /// <summary>The exact requested seed text, including its case; no runner-computed game seed is substituted.</summary>
    public string Seed { get; }
    private static readonly Regex NamePattern = new(@"^[A-Za-z0-9][A-Za-z0-9_-]*\z", RegexOptions.CultureInvariant);
    private static readonly Regex DevicePattern = new(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SeedPattern = new(@"^[A-Za-z0-9]+\z", RegexOptions.CultureInvariant);
    internal static SeededWorldRequest FromSeed(string seed, string? name)
    {
        if (seed == null || !SeedPattern.IsMatch(seed)) throw new ArgumentException("A world seed is nonempty ASCII alphanumeric text.", nameof(seed));
        return new(name ?? "VT-" + seed[..Math.Min(seed.Length, 20)] + "-" + Guid.NewGuid().ToString("N")[..12], seed);
    }
}
