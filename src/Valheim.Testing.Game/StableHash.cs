namespace Valheim.Testing.Game;

/// <summary>
/// The game's stable string hash (<c>string.GetStableHashCode()</c>), by which Valheim keys prefabs, dungeon rooms and ZDO
/// fields. Two accumulators over alternate UTF-16 code units, stopping at the end or at a <c>'\0'</c>, as Valheim 1.0.16
/// computes it. Use it to name the hashes an observation reports: a client that cannot resolve a hash cannot name it.
/// </summary>
public static class StableHash
{
    public static int Of(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        unchecked
        {
            int first = 5381, second = 5381;
            for (int i = 0; i < text.Length && text[i] != '\0'; i += 2)
            {
                first = ((first << 5) + first) ^ text[i];
                if (i == text.Length - 1 || text[i + 1] == '\0') break;
                second = ((second << 5) + second) ^ text[i + 1];
            }
            return first + second * 1566083941;
        }
    }

    /// <summary>The first of <paramref name="names"/> whose hash is <paramref name="hash"/>, or null.</summary>
    public static string? Name(int hash, IEnumerable<string>? names) => names?.FirstOrDefault(name => Of(name) == hash);
}
