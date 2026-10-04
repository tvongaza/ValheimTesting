namespace Valheim.Testing.Game;

/// <summary>
/// One piece of evidence linked from <c>result.json</c>: what it is (<see cref="Kind"/>, for example
/// <c>terrain-site</c>, <c>area-objects</c>, <c>review-still</c>, <c>review-clip</c>), the site or capture id, the world
/// it was taken in, the file (relative to the report directory when it lies inside it) and that file's SHA-256.
/// </summary>
public sealed record EvidenceReference(string Kind, string Site, string WorldUid, string File, string Sha256);

/// <summary>
/// Evidence that <see cref="ScenarioReport.Write"/> serializes itself, to <c>evidence/&lt;kind&gt;-NNN.json</c> beside
/// <c>result.json</c>, and links with its SHA-256. The snapshots implement it.
/// </summary>
public interface IEvidence
{
    /// <summary>Lower-case letters, digits and hyphens; it names the file.</summary>
    string Kind { get; }
    string Site { get; }
    string WorldUid { get; }
}
