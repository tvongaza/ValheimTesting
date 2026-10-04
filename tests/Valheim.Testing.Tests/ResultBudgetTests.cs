using System.Text.Json;
using Valheim.Testing.Adapter;
using Xunit;

// The adapter's check that a reply fits ValheimCLI's extension result bound, with a writer shaped like ValheimCLI's
// ExtensionJson.Write: JSON text, and an ArgumentException once the text passes 262,144 characters.
public class ResultBudgetTests
{
    private static string Write(object? value)
    {
        string json = JsonSerializer.Serialize(value);
        if (json.Length > ResultBudget.MaxResultChars) throw new ArgumentException("Extension result exceeds bounds.");
        return json;
    }

    private static Dictionary<string, object?> Items(int count, int nameLength) => new()
    {
        ["items"] = Enumerable.Range(0, count).Select(i => new Dictionary<string, object?> { ["name"] = i.ToString().PadLeft(nameLength, 'x') }).ToArray(),
    };

    [Fact] public void AReplyFitsUntilTheEnvelopeNoLongerDoes()
    {
        Assert.Null(ResultBudget.Exceeds(Items(10, 20), Write, "The census"));
        // Fits ValheimCLI's bound on its own, but not with the result envelope around it.
        var tight = Items(1, 1); int bare = Write(tight).Length;
        tight = Items(1, 1 + ResultBudget.MaxResultChars - ResultBudget.Envelope / 2 - bare);
        int length = Write(tight).Length;
        Assert.InRange(length, ResultBudget.MaxResultChars - ResultBudget.Envelope + 1, ResultBudget.MaxResultChars);
        Assert.Equal($"The census is larger than ValheimCLI's 256 KiB extension result ({length} characters).", ResultBudget.Exceeds(tight, Write, "The census"));
    }

    [Fact] public void TheWritersOwnRefusalIsReportedAndOtherErrorsAreNot()
    {
        Assert.Equal("The census of Mod_ (3000 items) is larger than ValheimCLI's 256 KiB extension result.",
            ResultBudget.Exceeds(Items(3000, 100), Write, "The census of Mod_ (3000 items)"));
        Assert.Throws<InvalidOperationException>(() => ResultBudget.Exceeds(Items(1, 1), _ => throw new InvalidOperationException("not a size"), "x"));
        // ValheimCLI's other refusals are a malformed reply, not a large one: never "narrow the prefixes".
        Assert.Throws<ArgumentException>(() => ResultBudget.Exceeds(Items(1, 1), _ => throw new ArgumentException("Non-finite result."), "x"));
    }
}
