using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Valheim.Testing.Doubles;
using Xunit;

// Every member of a doubled enum has the game's number (GameBytes/game-enums.txt, captured from the game's metadata by
// tools/doubles-members/capture-game-members.cs with game-members.txt). A member the game added in the middle, as 1.0.30's
// defeated_queen, defeated_fader and defeated_frozenking in GlobalKeys, moves every value after it; a double that misses
// one fails here even when every name is right.
public sealed class GameEnumValuesTests
{
    private const string CapturePath = "tests/Valheim.Testing.Doubles.Tests/GameBytes/game-enums.txt";

    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "src", "Valheim.Testing.Doubles", "Valheim.Testing.Doubles.csproj"))) return dir.FullName;
        throw new InvalidOperationException("No repository root above " + AppContext.BaseDirectory);
    }

    private static Dictionary<string, Dictionary<string, long>> Game()
    {
        var game = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
        foreach (string line in File.ReadAllLines(Path.Combine(Root(), CapturePath)))
        {
            if (line.StartsWith("#", StringComparison.Ordinal) || line.Length == 0) continue;
            string[] f = line.Split('\t');
            Assert.True(f.Length == 3, "An enum line has type, member and value: " + line);
            if (!game.TryGetValue(f[0], out var members)) game[f[0]] = members = new Dictionary<string, long>(StringComparer.Ordinal);
            members.Add(f[1], long.Parse(f[2], System.Globalization.CultureInfo.InvariantCulture));
        }
        return game;
    }

    [Fact] public void TheCaptureComesFromTheGameTheDoublesCopy() =>
        Assert.Contains(File.ReadLines(Path.Combine(Root(), CapturePath)), l => l == "# game version " + DoubledGame.Version);

    [Fact] public void EveryDoubledEnumMemberHasTheGamesValue()
    {
        var game = Game();
        var source = DoublesSource.Load(typeof(GameEnumValuesTests).Assembly);
        var wrong = new List<string>();
        int checkedEnums = 0;
        foreach (var type in typeof(GameEnumValuesTests).Assembly.GetTypes()
                     .Where(t => t.IsEnum && MemberIndex.IsDoubledNamespace(t.Namespace) && MemberIndex.VisibleType(t) && source.Declares(t)))
        {
            if (!game.TryGetValue(MemberIndex.TypeName(type), out var theirs)) continue; // a test-only or game-absent enum: the member index judges it
            checkedEnums++;
            foreach (string name in Enum.GetNames(type))
            {
                long ours = Convert.ToInt64(Enum.Parse(type, name), System.Globalization.CultureInfo.InvariantCulture);
                if (!theirs.TryGetValue(name, out long value)) wrong.Add($"{MemberIndex.TypeName(type)}.{name}: not in the game");
                else if (value != ours) wrong.Add($"{MemberIndex.TypeName(type)}.{name}: double {ours}, game {value}");
            }
        }
        Assert.True(checkedEnums > 0, "No doubled enum was found in the capture.");
        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    [Fact] public void GlobalKeysHasTheGamesOrder()
    {
        var keys = Game()["GlobalKeys"];
        Assert.Equal((long)GlobalKeys.defeated_queen, keys["defeated_queen"]);
        Assert.Equal((long)GlobalKeys.defeated_frozenking, keys["defeated_frozenking"]);
        Assert.Equal((long)GlobalKeys.Count, keys["Count"]);
    }
}
