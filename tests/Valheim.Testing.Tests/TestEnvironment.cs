using System.Text.Json;
using System.Text.Json.Serialization;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;

/// <summary>
/// Test fixtures only: builds the in-memory <see cref="ResolvedEnvironment"/> an inventory or campaign would resolve, from a
/// compact JSON description (hosts, server, clients and an optional steamAccounts section naming a pool file beside it).
/// The toolkit itself reads no such file: a run gets its environment from an inventory (<c>--inventory</c>) or a campaign.
/// </summary>
internal static class TestEnvironment
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
    };

    private sealed class Shape
    {
        public Dictionary<string, HostProfile> Hosts { get; set; } = [];
        public GameRole? Server { get; set; }
        public Dictionary<string, GameRole> Clients { get; set; } = [];
        public Leases? SteamAccounts { get; set; }
    }

    private sealed class Leases
    {
        public string Pool { get; set; } = "";
        public string LeaseHost { get; set; } = "";
        public bool CheckSignedIn { get; set; }
    }

    /// <summary>Reads a fixture file and validates the environment it describes.</summary>
    public static ResolvedEnvironment Read(string path) => Parse(File.ReadAllText(path), Path.GetDirectoryName(Path.GetFullPath(path))!);

    /// <summary>Parses a fixture description; a pool file is relative to <paramref name="directory"/>.</summary>
    public static ResolvedEnvironment Parse(string json, string? directory = null)
    {
        var shape = JsonSerializer.Deserialize<Shape>(json, Json)!;
        var environment = new ResolvedEnvironment { Hosts = shape.Hosts, Server = shape.Server, Clients = shape.Clients };
        if (shape.SteamAccounts is { } leases)
            environment.SteamAccounts = new SteamAccountsProfile
            {
                LeaseHost = leases.LeaseHost, CheckSignedIn = leases.CheckSignedIn,
                Accounts = Pool(File.ReadAllText(Path.Combine(directory ?? Environment.CurrentDirectory, leases.Pool))),
            };
        environment.Validate();
        return environment;
    }

    /// <summary>An account pool from its JSON fields (pool, leaseDirectory, accounts), as a campaign builds it in memory.</summary>
    public static SteamAccountPool Pool(string json)
    {
        var pool = JsonSerializer.Deserialize<SteamAccountPool>(json, Json)!;
        pool.Validate();
        return pool;
    }
}
