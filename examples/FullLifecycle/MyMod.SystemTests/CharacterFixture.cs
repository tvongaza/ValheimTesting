using System.Globalization;
using Valheim.Testing.Game;

namespace MyMod.SystemTests;

/// <summary>Opt-in preparation only; never stages into the game or launches a client.</summary>
public static class CharacterFixture
{
    public const string Mode = "prepare-character";

    public static int Run(string[] args)
    {
        if (args.Length != 7 || args[0] != Mode ||
            !long.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long uid) ||
            !float.TryParse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
            !float.TryParse(args[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) ||
            !float.TryParse(args[5], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
        {
            Console.Error.WriteLine("Usage: mymod-system-test prepare-character <local-character.fch> <world-uid> <x> <y> <z> <new-output-file.fch>");
            return 2;
        }
        try
        {
            string sha256 = CharacterStartCopy.Prepare(args[1], args[6], uid, x, y, z);
            Console.WriteLine($"PREPARED {args[6]} sha256={sha256} (copy only; not staged or joined)");
            return 0;
        }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException or KeyNotFoundException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Character preparation refused: " + error.Message);
            return 1;
        }
    }
}
