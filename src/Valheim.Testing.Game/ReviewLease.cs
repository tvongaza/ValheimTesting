using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Valheim.Testing.Game;

/// <summary>
/// What a review still and a review clip share: the capture id rule, the adapter's review-state replies, staging beside
/// the evidence directory and publishing it whole or not at all, and the provenance sidecar's common fields.
/// </summary>
internal static class ReviewLease
{
    // The adapter's ReviewState and ReviewClipFrames keep their own copy: they cannot reference this assembly.
    private static readonly Regex Id = new("^[A-Za-z0-9-]{1,64}$", RegexOptions.CultureInvariant);

    internal static bool ValidId(string? id) => Id.IsMatch(id ?? "");

    /// <summary>A fresh staging directory for <paramref name="evidenceDirectory"/>, which must not exist yet.</summary>
    internal static string Stage(string evidenceDirectory)
    {
        if (Directory.Exists(evidenceDirectory) || File.Exists(evidenceDirectory))
            throw new IOException("Review evidence already exists: " + evidenceDirectory);
        return evidenceDirectory + ".partial-" + Guid.NewGuid().ToString("N");
    }

    internal static void RequireState(JsonElement data, string id, string state)
    {
        if (data.GetProperty("source").GetString() != "review-state" || data.GetProperty("id").GetString() != id ||
            data.GetProperty("state").GetString() != state || !data.GetProperty("complete").GetBoolean())
            throw new InvalidDataException("The review adapter did not confirm " + state + " for " + id + ".");
    }

    /// <summary>
    /// Writes the sidecar: <c>schema</c>, <c>kind</c>, <c>visualVerdict: "not asserted"</c>, the id, world, build and
    /// plugin pins, then <paramref name="details"/>' own fields, then <c>recordedUtc</c>. Returns the sidecar's SHA-256:
    /// the sidecar is what a report links, since it records the image's or frames' own digests with the provenance.
    /// </summary>
    internal static string WriteSidecar(string path, string kind, string id, string worldUid, string gameBuild,
        IReadOnlyDictionary<string, string> pluginPins, object details)
    {
        var sidecar = new JsonObject
        {
            ["schema"] = 1, ["kind"] = kind, ["visualVerdict"] = "not asserted", ["Id"] = id,
            ["worldUid"] = worldUid, ["gameBuild"] = gameBuild, ["pluginPins"] = JsonSerializer.SerializeToNode(pluginPins),
        };
        foreach (var (name, value) in JsonSerializer.SerializeToNode(details)!.AsObject()) sidecar[name] = value?.DeepClone();
        sidecar["recordedUtc"] = DateTimeOffset.UtcNow;
        File.WriteAllText(path, sidecar.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return FileHash.Sha256(path);
    }

    /// <summary>
    /// After the review state was restored: on <paramref name="failure"/> delete the staging directory and rethrow it,
    /// otherwise move the staging directory into place, deleting it if the move fails.
    /// </summary>
    internal static void Publish(Exception? failure, string staging, string evidenceDirectory)
    {
        if (failure != null)
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        try { Directory.Move(staging, evidenceDirectory); }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            throw;
        }
    }
}
