namespace Valheim.Testing.Game;

/// <summary>One independent setup problem found before a host is contacted or a fixture is copied.</summary>
public sealed record CampaignPreflightProblem(string Actor, string Input, string Message);

/// <summary>One statically selected actor. Host eligibility does not claim that the host is currently ready.</summary>
public sealed record CampaignPreflightActor(string Name, string Kind, string Host, string Platform)
{
    /// <summary>The selected inventory recipe, when resolution used an ordered inventory.</summary>
    public string? Environment { get; init; }
    /// <summary>Why this recipe was selected after considering earlier choices.</summary>
    public string? SelectionReason { get; init; }
    /// <summary>A client's <c>characters_local</c> as the host check resolved it on its host; null before the host check.</summary>
    public string? CharactersDirectory { get; init; }
    /// <summary>A client's Steam <c>userdata</c> as the host check resolved it on its host; null before the host check.</summary>
    public string? SteamUserDataDirectory { get; init; }
}

/// <summary>A deterministic, read-only review of the locally selected actors and fixture.</summary>
public sealed record CampaignPreflightReport(IReadOnlyList<CampaignPreflightProblem> Problems)
{
    public IReadOnlyList<CampaignPreflightActor> Actors { get; init; } = [];
    /// <summary>What the inventory's this-machine default detected and assumed (<see cref="EnvironmentInventory.Detected"/>), one line each.</summary>
    public IReadOnlyList<string> Detected { get; init; } = [];
    public bool Ready => Problems.Count == 0;

    public void RequireReady()
    {
        if (!Ready)
            throw new ArgumentException("Campaign preflight failed: " + string.Join("; ",
                Problems.Select(problem => $"{problem.Actor} {problem.Input}: {problem.Message}")));
    }
}
