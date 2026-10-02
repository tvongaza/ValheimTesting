using System.Globalization;
using Valheim.Testing.Game;

namespace MyMod.SystemTests;

/// <summary>
/// Opt-in character preparation only; never stages into the game or launches a client. A character is registered once as
/// disposable in a store outside the game's folders; only registered characters can be prepared.
/// </summary>
public static class CharacterFixture
{
    public const string RegisterMode = "register-character";
    public const string RefreshMode = "refresh-character";
    public const string PrepareMode = "prepare-character";
    public const string PrepareNewWorldMode = "prepare-character-new-world";

    public static bool Handles(string mode) => mode is RegisterMode or RefreshMode or PrepareMode or PrepareNewWorldMode;

    public static int Run(string[] args)
    {
        try
        {
            switch (args)
            {
                case [RegisterMode, var store, var name, var local]:
                {
                    var characters = Directory.Exists(store) && File.Exists(Path.Combine(store, DisposableCharacterStore.ManifestFile))
                        ? DisposableCharacterStore.Open(store) : DisposableCharacterStore.Create(store);
                    var character = characters.Register(name, local);
                    Console.WriteLine($"REGISTERED {character.Name} in {characters.Root} (copy only; the local original is unchanged)");
                    return 0;
                }
                case [RefreshMode, var store, var name, var local]:
                {
                    var character = DisposableCharacterStore.Open(store).Refresh(name, local);
                    Console.WriteLine($"REFRESHED {character.Name} sha256={character.Sha256}");
                    return 0;
                }
                case [PrepareMode or PrepareNewWorldMode, var store, var name, var worldUid, var x, var y, var z, var output]
                    when long.TryParse(worldUid, NumberStyles.Integer, CultureInfo.InvariantCulture, out long uid) &&
                         float.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out float px) &&
                         float.TryParse(y, NumberStyles.Float, CultureInfo.InvariantCulture, out float py) &&
                         float.TryParse(z, NumberStyles.Float, CultureInfo.InvariantCulture, out float pz):
                {
                    var character = DisposableCharacterStore.Open(store).Get(name);
                    string sha256 = args[0] == PrepareNewWorldMode
                        ? CharacterStartCopy.PrepareForNewWorld(character, output, uid, px, py, pz)
                        : CharacterStartCopy.Prepare(character, output, uid, px, py, pz);
                    Console.WriteLine($"PREPARED {output} sha256={sha256} (copy only; not staged or joined)");
                    return 0;
                }
            }
            Console.Error.WriteLine("Usage: mymod-system-test register-character <store-dir> <name> <characters_local/character.fch>");
            Console.Error.WriteLine("       mymod-system-test refresh-character <store-dir> <name> <characters_local/character.fch>");
            Console.Error.WriteLine("       mymod-system-test prepare-character <store-dir> <name> <world-uid> <x> <y> <z> <new-output-file.fch>");
            Console.Error.WriteLine("       mymod-system-test prepare-character-new-world <store-dir> <name> <world-uid> <x> <y> <z> <new-output-file.fch>");
            return 2;
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or NotSupportedException or
            KeyNotFoundException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Character preparation refused: " + error.Message);
            return 1;
        }
    }
}
