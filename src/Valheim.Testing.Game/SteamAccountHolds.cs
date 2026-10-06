using System.Globalization;

namespace Valheim.Testing.Game;

/// <summary>What the optional signed-in check found on a client's host.</summary>
[ResultShape]
public enum SteamSignedInState
{
    /// <summary>The host's account signal matches the leased account. On Linux and macOS this is the last recorded login.</summary>
    Matches,
    /// <summary>The host's account signal names another account.</summary>
    OtherAccount,
    /// <summary>The host's account signal names no account.</summary>
    NotSignedIn,
    /// <summary>The host's account signal could not be read. Never taken as a pass.</summary>
    Unknown,
}

/// <summary>The signed-in check refused a client before it started. The message names the account and the host, never a SteamID.</summary>
public sealed class SteamSignedInException(SteamSignedInState state, string account, string hostName, string message) : InvalidOperationException(message)
{
    public SteamSignedInState State { get; } = state;
    public string Account { get; } = account;
    public string HostName { get; } = hostName;
}

/// <summary>
/// One campaign client's Steam account for a run (<see cref="ResolvedEnvironment.SteamAccounts"/>): the lease on it and the optional
/// signed-in check. <see cref="AcquireAsync"/> leases the account the client names, or the first free one for its host, and refuses at
/// once when another run holds it, naming that holder run; nothing waits. The lease never lapses on a timer (#257): it is held until
/// <see cref="ReleaseAsync"/>, which belongs after the client's teardown, or until <c>valheim-test env recover|teardown --run</c>
/// releases it once its client is proven stopped. A release that finds the lease no longer this run's, or cannot be proven, fails.
/// Its text names the account, never a credential or a SteamID.
/// </summary>
public sealed class SteamAccountHold : IAsyncDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    private readonly SteamAccountLease _lease;
    private readonly SteamPoolAccount _account;
    private readonly TimeSpan _timeout;
    private int _released;

    private SteamAccountHold(SteamAccountLease lease, SteamPoolAccount account, string client, string clientHost, bool checkSignedIn, TimeSpan timeout)
    {
        _lease = lease; _account = account; _timeout = timeout;
        Client = client; ClientHost = clientHost; CheckSignedIn = checkSignedIn;
    }

    /// <summary>The campaign client this account is for.</summary>
    public string Client { get; }
    /// <summary>The profile host the client runs on.</summary>
    public string ClientHost { get; }
    /// <summary>The leased Steam account's name.</summary>
    public string Account => _lease.Account;
    public string Pool => _lease.Pool;
    /// <summary>Who holds the lease, as other runs are told when they are refused.</summary>
    public string Owner => _lease.Owner;
    public string LeaseHostName => _lease.LeaseHostName;
    // What a later recovery needs to release this lease by its own id (RunJournal's lease-held entry): never a credential.
    internal string LeaseId => _lease.LeaseId;
    internal long LeaseNumber => _lease.Number;
    internal string LeaseDirectory => _lease.Directory;
    /// <summary>Whether the client's host must be signed in to this account before it starts (always, outside controlled tests).</summary>
    public bool CheckSignedIn { get; }
    /// <summary>Whether <see cref="CheckSignedInAsync"/> found the client's host signed in to this account.</summary>
    public bool SignedInChecked { get; private set; }

    /// <summary>
    /// Leases <paramref name="client"/>'s observed Steam identity on <paramref name="leaseHost"/> (the environment's
    /// <see cref="SteamAccountsProfile.LeaseHost"/>) for <paramref name="owner"/> (other runs see it as the holder) and run
    /// <paramref name="run"/>. Throws <see cref="SteamAccountLeaseException"/> when the account is held (<see cref="SteamAccountLeaseState.NoneFree"/>,
    /// naming its holder run) or the claim is not proven. A campaign's runner calls it for each client it opens.
    /// </summary>
    internal static async Task<SteamAccountHold> AcquireAsync(ResolvedEnvironment profile, string client, string owner, IGameHost leaseHost, string? run = null,
        TimeSpan? timeout = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(leaseHost);
        var section = profile.SteamAccounts ?? throw new InvalidOperationException("The environment has no Steam leases.");
        var pool = section.Accounts ?? throw new InvalidOperationException("The environment's Steam identities have not been observed yet.");
        if (!profile.Clients.TryGetValue(client, out var role)) throw new ArgumentException($"No client '{client}' in the environment.", nameof(client));
        if (leaseHost.Name != section.LeaseHost)
            throw new ArgumentException($"The leases of pool {pool.Pool} live on host '{section.LeaseHost}', not '{leaseHost.Name}'.", nameof(leaseHost));
        var candidates = section.Candidates(role);
        if (candidates.Count == 0) throw new ArgumentException($"No account of pool {pool.Pool} is for the client {client} on host '{role.Host}'.", nameof(client));
        // A named account is leased by itself, from the same pool name and directory as every other run of the pool.
        var source = role.SteamAccount != null ? pool.Only(candidates[0]) : pool;
        var wait = timeout ?? DefaultTimeout;
        var lease = await source.AcquireAsync(leaseHost, owner, wait, role.Host, run, cancellation).ConfigureAwait(false);
        return new SteamAccountHold(lease, candidates.First(account => account.Name == lease.Account), client, role.Host, section.CheckSignedIn, wait);
    }

    /// <summary>
    /// The optional signed-in check, on the client's own host: its Steam client must be signed in to this account (the pool's
    /// <see cref="SteamPoolAccount.SteamId"/>). Windows reads the signed-in user from <c>HKCU\Software\Valve\Steam\ActiveProcess\ActiveUser</c>;
    /// Linux and macOS read the account Steam last signed in from the host user's <c>loginusers.vdf</c> (its <c>MostRecent</c> user, or
    /// with current clients that write no <c>MostRecent</c>, the single newest <c>Timestamp</c>). Another account, none, or a state that
    /// cannot be read throws <see cref="SteamSignedInException"/>: unreadable is refused, never passed. The SteamID is kept in
    /// the private prepared profile for the later in-game check, but never written to the run report.
    /// </summary>
    public async Task CheckSignedInAsync(IGameHost clientHost, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(clientHost);
        if (clientHost.Name != ClientHost) throw new ArgumentException($"The client {Client} runs on host '{ClientHost}', not '{clientHost.Name}'.", nameof(clientHost));
        ThrowIfReleased();
        uint expected = SteamPoolAccount.AccountId(_account.SteamId) ?? throw new SteamSignedInException(SteamSignedInState.Unknown, Account, clientHost.Name,
            $"Pool {Pool} records no steamId for {Account}, so the signed-in check on {clientHost.Name} has nothing to compare; add it to the pool. Refused before launch.");
        var (state, id, detail) = await SteamSignedInUsers.ReadAsync(clientHost, _timeout, cancellation).ConfigureAwait(false);
        if (state == SteamSignedInState.Matches && id != expected) state = SteamSignedInState.OtherAccount;
        switch (state)
        {
            case SteamSignedInState.Matches: SignedInChecked = true; return;
            case SteamSignedInState.OtherAccount:
                throw new SteamSignedInException(state, Account, clientHost.Name, $"The Steam account check on {clientHost.Name} reported another account than {Account} (the pool's steamId for it). " +
                    $"Refused before launch: sign it in to {Account}, or correct the pool.");
            case SteamSignedInState.NotSignedIn:
                throw new SteamSignedInException(state, Account, clientHost.Name, $"The Steam account check on {clientHost.Name} found no account; the client there needs {Account}. Refused before launch.");
            default:
                throw new SteamSignedInException(state, Account, clientHost.Name, $"The Steam account check on {clientHost.Name} is inconclusive ({detail}), so it cannot " +
                    $"confirm it is {Account}. Refused before launch.");
        }
    }

    /// <summary>Confirms the running game's Steam identity against the leased account, including on Unix where preflight reads a remembered login.</summary>
    public void CheckGameIdentity(GameActor actor)
    {
        ThrowIfReleased();
        var reply = actor.Execute("cli_multiplayer_identity");
        var ids = reply.Output.SelectMany(line => System.Text.RegularExpressions.Regex.Matches(line,
            @"\bsteamId=([0-9]{17})(?=,|\s|$)").Select(match => match.Groups[1].Value)).ToArray();
        if (ids.Length != 1 || ids[0] != _account.SteamId)
            throw new InvalidOperationException($"The running client {Client} did not confirm the leased Steam identity for {Account}; setup is refused.");
    }

    /// <summary>Throws when the lease was released: the client must not start on this account.</summary>
    private void ThrowIfReleased()
    {
        if (Volatile.Read(ref _released) == 1) throw new InvalidOperationException($"The lease on Steam account {Account} was released.");
    }

    /// <summary>
    /// What a client start requires: a live lease, a passed signed-in check when the profile asks for one and, given
    /// <paramref name="hostName"/>, the client's own host.
    /// </summary>
    internal void RequireReady(string? hostName)
    {
        ThrowIfReleased();
        if (hostName != null && hostName != ClientHost)
            throw new ArgumentException($"Steam account {Account} was leased for the client {Client} on host '{ClientHost}', not for a client on '{hostName}'.");
        if (CheckSignedIn && !SignedInChecked)
            throw new InvalidOperationException($"The profile asks for the signed-in check: run CheckSignedInAsync on {ClientHost} before the client {Client} starts.");
    }

    /// <summary>Records the account's name and pool in the report's provenance under the client's name (<see cref="SteamAccountLease.Record"/>). Never a credential or SteamID.</summary>
    public void Record(ScenarioReport report) => _lease.Record(report, Client);

    /// <summary>
    /// After the client's teardown: releases the lease. Only the first call acts. Throws <see cref="SteamAccountLeaseException"/>
    /// when the lease was no longer this run's (<see cref="SteamAccountLeaseState.Lost"/>: a maintainer released it) or its release
    /// cannot be proven (<see cref="SteamAccountLeaseState.Unknown"/>; the lease is then held until a recovery releases it): a failed teardown.
    /// </summary>
    public async Task ReleaseAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) == 1) return;
        var result = await _lease.ReleaseAsync().ConfigureAwait(false);
        if (result.State == SteamAccountLeaseState.Lost)
            throw new SteamAccountLeaseException(SteamAccountLeaseState.Lost, Pool, [], $"{result.Detail} Another run may have used Steam account {Account} meanwhile.");
        if (result.State == SteamAccountLeaseState.Unknown) throw new SteamAccountLeaseException(SteamAccountLeaseState.Unknown, Pool, [], result.Detail);
    }

    /// <summary>Keeps the claim (a client may still run on the account): it is held until a recovery proves the client stopped. Only acts before a release.</summary>
    internal bool Keep() => Interlocked.Exchange(ref _released, 1) == 0;

    public ValueTask DisposeAsync() => new(ReleaseAsync());

    public override string ToString() => $"Steam account {Account} from pool {Pool} for client {Client}, leased by {Owner} until released";
}

/// <summary>Reads which Steam account a host's user is signed in to, through the host's own shell. Only account ids leave the host.</summary>
internal static class SteamSignedInUsers
{
    /// <param name="steamDirectory">A Steam directory to read first (tests); the host user's usual ones otherwise.</param>
    public static async Task<(SteamSignedInState State, uint? AccountId, string Detail)> ReadAsync(IGameHost host, TimeSpan timeout, CancellationToken cancellation, string steamDirectory = "")
    {
        var invoked = DateTimeOffset.UtcNow;
        var result = ShellPhases.Read(await host.RunAsync(host.Shell.Kind == HostShellKind.Bash ? Bash : PowerShell, new Dictionary<string, string> { ["steam"] = steamDirectory }, timeout, cancellation)
            .ConfigureAwait(false), invoked, host.Kind == GameHostKind.Local);
        // Only the outcome and where its time went are repeated, never the reply: it is about an account.
        if (!result.Succeeded)
            return (SteamSignedInState.Unknown, null, $"reading it on {host.Name} did not complete: " + (result.Outcome == HostOutcome.Exited ? "exit " + result.ExitCode : result.TimedOut ? "timed out" : result.Outcome.ToString()) +
                $" after {WaitText.Seconds(result.Elapsed)} ({result.Phases})");
        string? verdict = InteractiveClient.Line(result.Stdout, "VT-STEAMUSER ");
        string[] parts = (verdict ?? "").Split(' ', 2);
        return parts[0] switch
        {
            // A SteamID64's low 32 bits are its account id, which the Windows registry records.
            "id" when parts.Length == 2 && SteamPoolAccount.AccountId(parts[1]) is { } id => (SteamSignedInState.Matches, (uint?)id, ""),
            "account" when parts.Length == 2 && uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint account) && account != 0 => (SteamSignedInState.Matches, (uint?)account, ""),
            "none" => (SteamSignedInState.NotSignedIn, (uint?)null, ""),
            "unreadable" when parts.Length == 2 => (SteamSignedInState.Unknown, (uint?)null, parts[1]),
            _ => (SteamSignedInState.Unknown, (uint?)null, $"unexpected reply from {host.Name}"),
        };
    }

    // Linux and macOS: the account Steam last signed in, from the first loginusers.vdf found. This file does not prove
    // Steam is running or that the remembered account still has an active session.
    // Older clients mark it MostRecent "1"; the current macOS client (September 2026) writes no MostRecent key at all and records
    // each user's last sign-in as Timestamp, so without any MostRecent key the single newest Timestamp is that account, and a tie or
    // no Timestamp is unreadable. A user is a bare "<SteamID64>" line; only its id is printed. Variable: steam (optional Steam directory).
    public static readonly string Bash = ShellPhases.Bash + """
        set -u
        found=
        for f in ${steam:+"$steam/config/loginusers.vdf"} "${HOME:-/nonexistent}/Library/Application Support/Steam/config/loginusers.vdf" \
            "${HOME:-/nonexistent}/.local/share/Steam/config/loginusers.vdf" "${HOME:-/nonexistent}/.steam/steam/config/loginusers.vdf" \
            "${HOME:-/nonexistent}/.var/app/com.valvesoftware.Steam/.local/share/Steam/config/loginusers.vdf"; do
            if [ -f "$f" ]; then found=$f; break; fi
        done
        if [ -z "$found" ]; then echo "VT-STEAMUSER unreadable the host user has no loginusers.vdf in its Steam directories"; exit 0; fi
        if [ ! -r "$found" ]; then echo "VT-STEAMUSER unreadable the host user cannot read its loginusers.vdf"; exit 0; fi
        awk '
            { line = tolower($0) }
            line ~ /^[ \t]*"[0-9]+"[ \t\r]*$/ { id = line; gsub(/[^0-9]/, "", id); users++; next }
            line ~ /^[ \t]*"mostrecent"[ \t]/ { marked = 1; if (line ~ /"mostrecent"[ \t]+"1"/ && id != "" && recent == "") recent = id; next }
            line ~ /^[ \t]*"timestamp"[ \t]+"[0-9]+"/ && id != "" {
                t = line; sub(/^[ \t]*"timestamp"[ \t]+"/, "", t); sub(/".*/, "", t); t += 0
                if (t > best) { best = t; newest = id; tie = 0 } else if (t == best) tie = 1
            }
            END {
                if (marked) { if (recent != "") print "VT-STEAMUSER id " recent; else print "VT-STEAMUSER none"; exit }
                if (users == 0) { print "VT-STEAMUSER none"; exit }
                if (newest == "" || tie) { print "VT-STEAMUSER unreadable loginusers.vdf marks no MostRecent user and has no single newest Timestamp"; exit }
                print "VT-STEAMUSER id " newest
            }
        ' "$found"
        """.ReplaceLineEndings("\n");

    // Windows: Steam's ActiveUser (the signed-in account id, 0 when none) in the host user's registry. pwsh on Linux or macOS reads
    // loginusers.vdf as the bash script does. [Environment]::OSVersion, because Windows PowerShell 5.1 has no $IsWindows.
    public static readonly string PowerShell = ShellPhases.PowerShell + """
        if ([Environment]::OSVersion.Platform -ne [PlatformID]::Unix) {
            $active = [Microsoft.Win32.Registry]::GetValue('HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess', 'ActiveUser', $null)
            if ($null -eq $active) { 'VT-STEAMUSER unreadable the host user has no ActiveUser under HKCU\Software\Valve\Steam\ActiveProcess (Steam has not run for it)'; exit 0 }
            $n = [long]0
            if (-not [long]::TryParse([string]$active, [Globalization.NumberStyles]::AllowLeadingSign, [Globalization.CultureInfo]::InvariantCulture, [ref]$n)) { 'VT-STEAMUSER unreadable ActiveUser is not a number'; exit 0 }
            if ($n -lt 0) { $n += 4294967296 }
            if ($n -eq 0) { 'VT-STEAMUSER none' } else { 'VT-STEAMUSER account ' + $n.ToString([Globalization.CultureInfo]::InvariantCulture) }
            exit 0
        }
        $vtHome = [Environment]::GetFolderPath('UserProfile')
        $candidates = @()
        if ($steam) { $candidates += (Join-Path $steam 'config/loginusers.vdf') }
        $candidates += @((Join-Path $vtHome 'Library/Application Support/Steam/config/loginusers.vdf'), (Join-Path $vtHome '.local/share/Steam/config/loginusers.vdf'),
            (Join-Path $vtHome '.steam/steam/config/loginusers.vdf'), (Join-Path $vtHome '.var/app/com.valvesoftware.Steam/.local/share/Steam/config/loginusers.vdf'))
        $found = $null
        foreach ($candidate in $candidates) { if ([IO.File]::Exists($candidate)) { $found = $candidate; break } }
        if ($null -eq $found) { 'VT-STEAMUSER unreadable the host user has no loginusers.vdf in its Steam directories'; exit 0 }
        try { $lines = [IO.File]::ReadAllLines($found) } catch { 'VT-STEAMUSER unreadable the host user cannot read its loginusers.vdf'; exit 0 }
        $id = ''; $marked = $false; $recent = ''; $users = 0; $best = [long]0; $newest = ''; $tie = $false
        foreach ($line in $lines) {
            $l = $line.Trim().ToLowerInvariant()
            if ($l -cmatch '^"([0-9]+)"$') { $id = $Matches[1]; $users++ }
            elseif ($l -cmatch '^"mostrecent"\s') { $marked = $true; if ($l -cmatch '^"mostrecent"\s+"1"' -and $id -and -not $recent) { $recent = $id } }
            elseif ($l -cmatch '^"timestamp"\s+"([0-9]+)"' -and $id) {
                $t = [long]$Matches[1]
                if ($t -gt $best) { $best = $t; $newest = $id; $tie = $false } elseif ($t -eq $best) { $tie = $true }
            }
        }
        if ($marked) { if ($recent) { 'VT-STEAMUSER id ' + $recent } else { 'VT-STEAMUSER none' }; exit 0 }
        if ($users -eq 0) { 'VT-STEAMUSER none'; exit 0 }
        if (-not $newest -or $tie) { 'VT-STEAMUSER unreadable loginusers.vdf marks no MostRecent user and has no single newest Timestamp'; exit 0 }
        'VT-STEAMUSER id ' + $newest
        """.ReplaceLineEndings("\n");
}
