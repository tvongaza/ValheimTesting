namespace Valheim.Testing.Game;

/// <summary>
/// The pinned ValheimCLI bundle this package embeds: <c>cli-dependency.json</c>'s pinned commit and bundle hash, and the zip
/// (<c>.packages/valheimcli-bundle.zip</c>, which <c>scripts/bootstrap-cli.cs</c> fetches and checks before a build). The one
/// source of the pinned set for <c>valheim-test</c> and for a disposable client copy.
/// </summary>
internal static class PinnedCliBundle
{
    /// <summary>
    /// The embedded bundle, extracted once under <paramref name="dataRoot"/> (default <see cref="CliBundle.DataRoot"/>) by
    /// <see cref="CliBundle.Extract"/>; null when this build carries none (its build had no bundle to embed).
    /// </summary>
    internal static CliBundleSource? Source(string? dataRoot = null)
    {
        var assembly = typeof(PinnedCliBundle).Assembly;
        string commit, sha256;
        using (var pinStream = assembly.GetManifestResourceStream("cli-dependency.json"))
        {
            if (pinStream == null) return null;
            using var pin = System.Text.Json.JsonDocument.Parse(pinStream);
            if (!pin.RootElement.TryGetProperty("bundle", out var bundle)) return null;
            commit = pin.RootElement.GetProperty("commit").GetString() ?? "";
            sha256 = bundle.GetProperty("sha256").GetString() ?? "";
        }
        using var zip = assembly.GetManifestResourceStream("valheimcli-bundle.zip");
        if (zip == null) return null;
        return CliBundle.Extract(zip, sha256, commit, $"the pinned {commit[..Math.Min(7, commit.Length)]} bundle", dataRoot);
    }
}
