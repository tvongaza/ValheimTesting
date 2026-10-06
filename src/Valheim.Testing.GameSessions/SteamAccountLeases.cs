using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>
/// The Steam accounts a set of test runs share, by name only: one lease per account at a time, so two runs never use the same
/// signed-in account simultaneously. A campaign builds it in memory from the identities observed signed in on its client hosts
/// (<see cref="SteamPoolAccount.LeaseKey"/>); there is no account file. The runner never signs into Steam: each host's Steam
/// client is signed in by a person ahead of time.
/// </summary>
internal sealed class SteamAccountPool
{
    internal static readonly Regex AccountName = new("^[A-Za-z0-9_]{3,64}$", RegexOptions.CultureInvariant);
    private static readonly Regex Name = new("^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant);

    /// <summary>The pool's name; its leases live in <c>&lt;leaseDirectory&gt;/&lt;pool&gt;</c>.</summary>
    public string Pool { get; set; } = "";
    /// <summary>An absolute directory on the lease host. Every run that shares the pool must use the same host and directory.</summary>
    public string LeaseDirectory { get; set; } = "";
    public List<SteamPoolAccount> Accounts { get; set; } = [];

    /// <summary>Every problem at once, as one <see cref="ArgumentException"/>.</summary>
    public void Validate()
    {
        var errors = new List<string>();
        if (!Name.IsMatch(Pool ?? "")) errors.Add("pool must be a name of letters, digits, '.', '_' or '-'.");
        if (string.IsNullOrWhiteSpace(LeaseDirectory) || LeaseDirectory.Any(char.IsControl)
            || !(ScriptedGameHost.IsAbsolute(HostShellKind.Bash, LeaseDirectory) || ScriptedGameHost.IsAbsolute(HostShellKind.PowerShell, LeaseDirectory)))
            errors.Add("leaseDirectory must be an absolute path on the lease host.");
        if (Accounts == null || Accounts.Count == 0) errors.Add("List at least one account.");
        foreach (var account in Accounts ?? [])
        {
            if (!AccountName.IsMatch(account.Name ?? "")) { errors.Add($"Account name '{account.Name}' must be a Steam account name: 3 to 64 letters, digits or '_'."); continue; }
            if (account.Host != null && !Name.IsMatch(account.Host)) errors.Add($"Account {account.Name}: host must be a host name from the environment.");
            if (account.SteamId != null && SteamPoolAccount.AccountId(account.SteamId) == null)
                errors.Add($"Account {account.Name}: steamId must be the account's SteamID64, 17 digits starting 7656119 (an individual account).");
        }
        foreach (var twice in (Accounts ?? []).GroupBy(account => account.Name, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            errors.Add($"Account {twice.Key} is listed twice; Steam account names ignore case.");
        if (errors.Count != 0) throw new ArgumentException("Invalid account pool: " + string.Join(" ", errors));
    }

    /// <summary>
    /// Leases the first free account (in the pool's order) for <paramref name="owner"/>, or throws
    /// <see cref="SteamAccountLeaseException"/>: <see cref="SteamAccountLeaseState.NoneFree"/> with each account's holder, or
    /// <see cref="SteamAccountLeaseState.Unknown"/> when the claim is not proven. Nothing waits or retries. A lease is the exclusive
    /// creation of the next numbered claim file in the account's directory on <paramref name="leaseHost"/>, which exactly one claimer
    /// can win; an account is free only once its latest claim was released. A lease never lapses on a timer (#257): only its run
    /// releases it, or <c>valheim-test env recover|teardown --run</c> once its client is proven stopped. An unproven claim that did
    /// happen is held until one of those releases it.
    /// </summary>
    /// <param name="clientHost">Only accounts for this environment host (or for any host): the host whose Steam client is signed in to it.</param>
    /// <param name="run">The run id the lease is for, written in its claim so a run refused by it is told which run to recover.</param>
    public async Task<SteamAccountLease> AcquireAsync(IGameHost leaseHost, string owner, TimeSpan timeout, string? clientHost = null, string? run = null,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(leaseHost);
        Validate();
        CheckOwner(owner);
        if (run != null && (!RunJournal.SafeName(run) || run == "-")) throw new ArgumentException("A run id is letters, digits, '.', '_' and '-'.", nameof(run));
        WaitText.RequireTimeout(timeout);
        CheckDirectory(leaseHost);
        var candidates = Accounts.Where(account => clientHost == null || account.Host == null || account.Host == clientHost).ToList();
        if (candidates.Count == 0) throw new ArgumentException($"No account in pool {Pool} is for host '{clientHost}'.", nameof(clientHost));
        string lease = Guid.NewGuid().ToString("N");
        var result = await RunAsync(leaseHost, "claim", timeout, cancellation, accounts: string.Join('\n', candidates.Select(account => account.Name)),
            owner: owner, lease: lease, run: run ?? "").ConfigureAwait(false);
        // An unproven claim was never journalled, so env recover cannot find it; no client started on it, so the escape hatch may release it.
        string unproven = $" A claim that did happen is held until it is released: see valheim-test env status{(run == null ? "" : $"; once run {run} is over, valheim-test env teardown --run {run} --machine-gone releases it (no client of it started on that claim)")}.";
        if (!result.Succeeded)
            throw new SteamAccountLeaseException(SteamAccountLeaseState.Unknown, Pool, [],
                $"The claim of an account from pool {Pool} on {leaseHost.Name} is not proven: {result.Describe()}{unproven}");
        string[] lines = result.Stdout.Split('\n');
        var claimed = Regex.Match(lines[0], "^VT-LEASE claimed ([A-Za-z0-9_]+) ([0-9]+)$", RegexOptions.CultureInvariant);
        var won = claimed.Success ? candidates.FirstOrDefault(candidate => candidate.Name == claimed.Groups[1].Value) : null;
        if (won != null && long.TryParse(claimed.Groups[2].Value, CultureInfo.InvariantCulture, out long number) && number > 0)
            return new SteamAccountLease(leaseHost, this, won, owner, run, lease, number, timeout);
        if (lines[0] == "VT-LEASE none")
        {
            var statuses = lines.Skip(1).Select(ReadStatus).OfType<SteamAccountStatus>().ToList();
            string holders = statuses.Count == 0 ? "" : " " + string.Join("; ", statuses.Select(status => status.Describe()));
            throw new SteamAccountLeaseException(SteamAccountLeaseState.NoneFree, Pool, statuses, $"No account of pool {Pool} is free on {leaseHost.Name}.{holders}");
        }
        throw new SteamAccountLeaseException(SteamAccountLeaseState.Unknown, Pool, [],
            $"Unexpected reply to the claim of an account from pool {Pool} on {leaseHost.Name}: {lines[0]}.{unproven}");
    }

    /// <summary>Each account's lease as the lease host sees it now. Changes nothing.</summary>
    public async Task<IReadOnlyList<SteamAccountStatus>> ListAsync(IGameHost leaseHost, TimeSpan timeout, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(leaseHost);
        Validate();
        WaitText.RequireTimeout(timeout);
        CheckDirectory(leaseHost);
        var result = (await RunAsync(leaseHost, "list", timeout, cancellation, accounts: string.Join('\n', Accounts.Select(account => account.Name))).ConfigureAwait(false))
            .EnsureSuccess($"Listing the leases of pool {Pool} on {leaseHost.Name}");
        if (!result.Stdout.Split('\n').Contains("VT-LEASE listed")) throw new HostOperationException($"Unexpected reply while listing the leases of pool {Pool}", result);
        return result.Stdout.Split('\n').Where(line => line.StartsWith("VT-LEASE-ACCOUNT ", StringComparison.Ordinal))
            .Select(line => ReadStatus(line["VT-LEASE-ACCOUNT ".Length..])).OfType<SteamAccountStatus>().ToList();
    }

    // The script's phase line is taken out of the reply (every caller reads its first line), and where its time went is kept
    // for the failure (#255): a timeout under load then says whether the shell ever began the script.
    internal async Task<HostResult> RunAsync(IGameHost host, string action, TimeSpan timeout, CancellationToken cancellation, string accounts = "", string owner = "",
        string lease = "", string run = "", string account = "", string number = "0")
    {
        var invoked = DateTimeOffset.UtcNow;
        return ShellPhases.Read(await host.RunAsync(host.Shell.Kind == HostShellKind.Bash ? LeaseScripts.Bash : LeaseScripts.PowerShell, new Dictionary<string, string>
        {
            ["action"] = action, ["directory"] = LeaseDirectory, ["pool"] = Pool, ["accounts"] = accounts, ["owner"] = owner, ["lease"] = lease,
            ["run"] = run, ["account"] = account, ["number"] = number,
        }, timeout, cancellation).ConfigureAwait(false), invoked, host.Kind == GameHostKind.Local);
    }

    /// <summary>This pool with only <paramref name="account"/>: the same pool name and lease directory, so its lease is the one every run of the pool sees.</summary>
    internal SteamAccountPool Only(SteamPoolAccount account) => new() { Pool = Pool, LeaseDirectory = LeaseDirectory, Accounts = [account] };

    internal static void CheckOwner(string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        if (owner.Length > 200 || owner.Any(char.IsControl)) throw new ArgumentException("An owner is one line of at most 200 characters.", nameof(owner));
    }

    private void CheckDirectory(IGameHost host)
    {
        if (!ScriptedGameHost.IsAbsolute(host.Shell.Kind, LeaseDirectory))
            throw new ArgumentException($"leaseDirectory '{LeaseDirectory}' is not an absolute path on {host.Name} ({host.Shell}).");
    }

    // "held <account> <run or -> <owner>", "taken <account>", "unreadable <account>" or "free <account>".
    private static SteamAccountStatus? ReadStatus(string line)
    {
        var parts = line.TrimEnd().Split(' ', 4);
        if (parts.Length < 2 || !AccountName.IsMatch(parts[1])) return null;
        string account = parts[1];
        return parts[0] switch
        {
            "held" when parts.Length == 4 =>
                new SteamAccountStatus(account, SteamAccountState.Held, parts[3], parts[2] != "-" && RunJournal.SafeName(parts[2]) ? parts[2] : null),
            "free" => new SteamAccountStatus(account, SteamAccountState.Free, null, null),
            "taken" => new SteamAccountStatus(account, SteamAccountState.Contended, null, null),
            "unreadable" => new SteamAccountStatus(account, SteamAccountState.Unreadable, null, null),
            _ => null,
        };
    }
}

/// <summary>One account of a pool: its Steam account name, never its password.</summary>
internal sealed class SteamPoolAccount
{
    /// <summary>The Steam account name (3 to 64 letters, digits or '_'). Recorded in reports; never a credential.</summary>
    public string Name { get; set; } = "";
    /// <summary>The environment host this account is for (the host whose Steam client is signed in to it); any host when null.</summary>
    public string? Host { get; set; }
    /// <summary>
    /// The account's SteamID64 as a string of digits, for the signed-in check only: an identifier, not a
    /// credential, observed on the client's host and kept in memory for the in-game identity check; lease filenames and run reports use a stable opaque key instead.
    /// </summary>
    public string? SteamId { get; set; }

    // An individual account's SteamID64 is 0x01100001 in its high 32 bits (public universe, individual type, desktop instance) and
    // its account id in the low 32 bits, which is what Steam records on Windows as the signed-in ActiveUser.
    internal static uint? AccountId(string? steamId) =>
        steamId is { Length: 17 } && steamId.All(char.IsAsciiDigit) && ulong.TryParse(steamId, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id)
            && id >> 32 == 0x01100001UL && (uint)id != 0 ? (uint)id : null;

    internal static string SteamId64(uint accountId) => accountId == 0
        ? throw new ArgumentOutOfRangeException(nameof(accountId), "A signed-in Steam account needs a nonzero account id.")
        : ((0x01100001UL << 32) | accountId).ToString(CultureInfo.InvariantCulture);

    // A stable lease key across inventories without printing the observed SteamID in lease filenames or reports.
    internal static string LeaseKey(string steamId) => AccountId(steamId) == null
        ? throw new ArgumentException("A lease key needs an individual SteamID64.", nameof(steamId))
        : "steam_" + FileHash.Sha256(Encoding.ASCII.GetBytes(steamId))[..40];
}

public enum SteamAccountState { Free, Held, Contended, Unreadable }

/// <summary>
/// An account's lease: who holds it, and for which run when its claim names one, or why it could not be taken. A held lease
/// ends only when it is released (#257). Never a credential.
/// </summary>
public sealed record SteamAccountStatus(string Account, SteamAccountState State, string? Holder, string? Run)
{
    public string Describe() => State switch
    {
        SteamAccountState.Held => $"{Account} is held by {Holder} until it is released" + (Run == null
            ? "; see valheim-test env status for the run that holds it"
            : $"; if run {Run} is over, valheim-test env recover --run {Run} releases it"),
        SteamAccountState.Contended => $"{Account} was just taken by another run",
        SteamAccountState.Unreadable => $"{Account} has an unreadable claim file; inspect it by hand",
        _ => $"{Account} is free",
    };
}

[ResultShape]
public enum SteamAccountLeaseState
{
    Claimed,
    /// <summary>This call released the lease, or it was released already.</summary>
    Released,
    /// <summary>Every account is held by another run.</summary>
    NoneFree,
    /// <summary>The lease is no longer this run's: a maintainer released it (<c>env teardown --run ID --machine-gone</c>), or another run took the account after that. Stop using the account.</summary>
    Lost,
    /// <summary>Nothing could be proven (a transport failure, a timeout or an unexpected reply).</summary>
    Unknown,
}

[ResultShape]
internal sealed record SteamAccountLeaseResult(SteamAccountLeaseState State, string Detail);

/// <summary>A lease could not be taken or released. <see cref="Accounts"/> lists the holders when none was free; never a credential.</summary>
public sealed class SteamAccountLeaseException(SteamAccountLeaseState state, string pool, IReadOnlyList<SteamAccountStatus> accounts, string message)
    : InvalidOperationException(message)
{
    public SteamAccountLeaseState State { get; } = state;
    public string Pool { get; } = pool;
    public IReadOnlyList<SteamAccountStatus> Accounts { get; } = accounts;
}

/// <summary>
/// One run's exclusive lease on one account. It never lapses (#257): it is held until <see cref="ReleaseAsync"/> (or Dispose)
/// releases it, or a recovery does once its client is proven stopped. A release that cannot be proven throws, because a failed
/// teardown is a failure to report. Its text is the account's name, never a credential.
/// </summary>
internal sealed class SteamAccountLease : IAsyncDisposable
{
    private readonly IGameHost _host;
    private readonly SteamAccountPool _pool;
    private readonly long _number;
    private readonly TimeSpan _timeout;
    private int _released;

    internal SteamAccountLease(IGameHost host, SteamAccountPool pool, SteamPoolAccount account, string owner, string? run, string leaseId, long number, TimeSpan timeout)
    {
        _host = host; _pool = pool; _number = number; _timeout = timeout;
        Account = account.Name; AccountHost = account.Host; Owner = owner; Run = run; LeaseId = leaseId;
    }

    public string Pool => _pool.Pool;
    /// <summary>The leased Steam account's name.</summary>
    public string Account { get; }
    /// <summary>The environment host the account is for, if the pool names one.</summary>
    public string? AccountHost { get; }
    public string Owner { get; }
    /// <summary>The run id written in the claim, if the lease was taken for one.</summary>
    public string? Run { get; }
    /// <summary>This lease's own id, written in its claim file; only a holder with it can release the claim.</summary>
    public string LeaseId { get; }
    public string LeaseHostName => _host.Name;
    /// <summary>This claim's number in the account's directory, which a release names with <see cref="LeaseId"/>.</summary>
    internal long Number => _number;
    /// <summary>The pool's lease directory on the lease host.</summary>
    internal string Directory => _pool.LeaseDirectory;

    /// <summary>
    /// Releases the lease if its claim is still this run's. Only the first call acts. <see cref="SteamAccountLeaseState.Lost"/> means
    /// nothing of this run's was held any more (a maintainer released it, or another run took it after that), which also ends the lease.
    /// </summary>
    public async Task<SteamAccountLeaseResult> ReleaseAsync(CancellationToken cancellation = default)
    {
        if (Interlocked.Exchange(ref _released, 1) == 1) return new(SteamAccountLeaseState.Released, "The lease was already released by this handle.");
        var result = await _pool.RunAsync(_host, "release", _timeout, cancellation, lease: LeaseId, account: Account, number: _number.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        string verdict = result.Succeeded ? result.Stdout.Split('\n')[0] : "";
        if (verdict == "VT-LEASE released") return new(SteamAccountLeaseState.Released, $"Released the lease on {Account}.");
        if (verdict.StartsWith("VT-LEASE lost ", StringComparison.Ordinal)) return new(SteamAccountLeaseState.Lost, $"The lease on {Account} had already ended ({verdict["VT-LEASE lost ".Length..]}).");
        return new(SteamAccountLeaseState.Unknown, $"The release of the lease on {Account} is not proven: {(result.Succeeded ? "unexpected reply " + verdict : result.Describe())} " +
            "It is held until it is released: see valheim-test env status" + (Run == null ? "." : $", then valheim-test env recover --run {Run}."));
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _released) == 1) return;
        var result = await ReleaseAsync().ConfigureAwait(false);
        if (result.State == SteamAccountLeaseState.Unknown) throw new SteamAccountLeaseException(SteamAccountLeaseState.Unknown, Pool, [], result.Detail);
    }

    /// <summary>Records the account's name (and pool) in the report's provenance under <c>steamAccount.&lt;role&gt;</c>. Never a credential.</summary>
    public void Record(ScenarioReport report, string role)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!Regex.IsMatch(role ?? "", "^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant)) throw new ArgumentException("A role is a name such as a client's.", nameof(role));
        report.Provenance["steamAccount." + role] = Account;
        report.Provenance["steamAccountPool." + role] = Pool;
    }

    public override string ToString() => $"Steam account {Account} from pool {Pool}, leased by {Owner} until released";
}

// The lease store's fixed scripts. A lease on account A is the exclusive creation of A/claim-NNNNNNNNN (N one more than the
// highest claim there), with its content complete from the start (a hard link of a written file, or its no-replace move on
// Windows): its lease id, a far-future expiry (older runners read the second line as one, so they too see it held), its owner
// and its run id. Only the holder, proven by its lease id, releases its claim, by adding claim-N.released. The account is free
// only when its highest claim is released: a claim never lapses on a timer (#257), whatever its second line says. Nothing is
// rewritten or removed in place except claims more than eight numbers old, and a claimer that finds a higher claim after its
// own creation withdraws it. "abandon" is the maintainer's escape hatch for a run whose machine is gone for good: it releases
// every unreleased latest claim in the lease directory that names that run (in its run line, or in an older claim's owner
// "<runner> <run> client <name>"). Variables: action (claim, release, list, abandon), directory, pool, accounts (one per line),
// owner, lease, run, account, number.
internal static class LeaseScripts
{
    /// <summary>The second line of a new claim: 9999-12-31T23:59:59Z, which an older runner reads as an expiry that never comes.</summary>
    internal const string Never = "253402300799";

    public static readonly string Bash = ShellPhases.Bash + """
        set -u
        root="$directory/$pool"
        name() { printf 'claim-%09d' "$1"; }
        highest() {
            local n=0 f b
            for f in "$1"/claim-[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]; do
                [ -e "$f" ] || continue
                b=${f##*/claim-}; b=$((10#$b))
                if [ "$b" -gt "$n" ]; then n=$b; fi
            done
            echo "$n"
        }
        # Sets id, holder, claimrun (- when none) and released for claim $2 of directory $1.
        load() {
            local f expires
            f="$1/$(name "$2")"
            { IFS= read -r id && IFS= read -r expires && IFS= read -r holder && { IFS= read -r claimrun || :; }; } 2> /dev/null < "$f" || return 1
            case "$expires" in ''|*[!0-9]*) return 1 ;; esac
            case "$claimrun" in ''|*[!A-Za-z0-9._-]*) claimrun=- ;; esac
            released=0
            if [ -e "$f.released" ]; then released=1; fi
            return 0
        }
        case "$action" in
        claim)
            mkdir -p -- "$root" || exit 3
            report=
            while IFS= read -r account; do
                [ -n "$account" ] || continue
                d="$root/$account"
                mkdir -p -- "$d" || exit 3
                n=$(highest "$d")
                if [ "$n" -gt 0 ]; then
                    if ! load "$d" "$n"; then report="${report}unreadable $account"$'\n'; continue; fi
                    if [ "$released" = 0 ]; then report="${report}held $account $claimrun $holder"$'\n'; continue; fi
                fi
                next=$((n + 1))
                f="$d/$(name "$next")"
                t="$d/.new-$lease"
                printf '%s\n%s\n%s\n%s\n' "$lease" "VT_NEVER" "$owner" "$run" > "$t" || exit 3
                if ln -- "$t" "$f" 2> /dev/null; then
                    rm -f -- "$t"
                    if [ "$(highest "$d")" != "$next" ]; then rm -f -- "$f"; report="${report}taken $account"$'\n'; continue; fi
                    for old in "$d"/claim-[0-9]*; do
                        [ -e "$old" ] || continue
                        b=${old##*/claim-}; b=${b%%.*}
                        case "$b" in ''|*[!0-9]*) continue ;; esac
                        if [ $((10#$b)) -lt $((next - 8)) ]; then rm -f -- "$old"; fi
                    done
                    echo "VT-LEASE claimed $account $next"; exit 0
                fi
                rm -f -- "$t"
                if [ ! -e "$f" ]; then echo "vt: could not create $f; the lease directory needs hard links" >&2; exit 3; fi
                report="${report}taken $account"$'\n'
            done <<< "$accounts"
            echo "VT-LEASE none"
            printf '%s' "$report" ;;
        release)
            d="$root/$account"
            f="$d/$(name "$number")"
            if ! load "$d" "$number" || [ "$id" != "$lease" ]; then echo "VT-LEASE lost gone"; exit 0; fi
            if [ "$(highest "$d")" != "$number" ]; then echo "VT-LEASE lost taken"; exit 0; fi
            if [ "$released" = 0 ]; then : > "$f.released" || exit 3; fi
            echo "VT-LEASE released" ;;
        list)
            while IFS= read -r account; do
                [ -n "$account" ] || continue
                d="$root/$account"
                n=$(highest "$d")
                if [ "$n" = 0 ]; then echo "VT-LEASE-ACCOUNT free $account"; continue; fi
                if ! load "$d" "$n"; then echo "VT-LEASE-ACCOUNT unreadable $account"; continue; fi
                if [ "$released" = 0 ]; then echo "VT-LEASE-ACCOUNT held $account $claimrun $holder"; else echo "VT-LEASE-ACCOUNT free $account"; fi
            done <<< "$accounts"
            echo "VT-LEASE listed" ;;
        abandon)
            for p in "$directory"/*/; do
                [ -d "$p" ] || continue
                pool=${p%/}; pool=${pool##*/}
                for d in "$p"*/; do
                    [ -d "$d" ] || continue
                    d=${d%/}; account=${d##*/}
                    n=$(highest "$d")
                    [ "$n" -gt 0 ] || continue
                    if ! load "$d" "$n"; then echo "VT-LEASE-UNREADABLE $pool $account"; continue; fi
                    [ "$released" = 0 ] || continue
                    case "$claimrun" in
                        "$run") ;;
                        -) case "$holder" in *" $run client "*) ;; *) continue ;; esac ;;
                        *) continue ;;
                    esac
                    : > "$d/$(name "$n").released" || exit 3
                    echo "VT-LEASE-ABANDONED $pool $account $n $id $holder"
                done
            done
            echo "VT-LEASE abandoned" ;;
        *) exit 2 ;;
        esac
        """.Replace("VT_NEVER", Never).ReplaceLineEndings("\n");

    // On Windows the claim is [IO.File]::Move, a no-replace rename that refuses an existing destination. On Unix .NET's Move
    // checks that the destination is absent and then renames, which replaces a claim another run created in between: both runs
    // then held one account (four leases from three accounts in CI). There the claim is a hard link, as in bash, which link(2)
    // refuses to make over an existing file. [Environment]::OSVersion, because Windows PowerShell 5.1 has no $IsWindows.
    public static readonly string PowerShell = ShellPhases.PowerShell + """
        $utf8 = New-Object Text.UTF8Encoding $false
        $vtUnix = [Environment]::OSVersion.Platform -eq [PlatformID]::Unix
        $root = Join-Path $directory $pool
        function Get-VtName([long]$n) { 'claim-' + $n.ToString('000000000', [Globalization.CultureInfo]::InvariantCulture) }
        function Get-VtHighest([string]$d) {
            $high = [long]0
            if ([IO.Directory]::Exists($d)) {
                foreach ($f in [IO.Directory]::GetFiles($d, 'claim-*')) {
                    if ([IO.Path]::GetFileName($f) -cmatch '^claim-([0-9]{9})$') { $n = [long]$Matches[1]; if ($n -gt $high) { $high = $n } }
                }
            }
            $high
        }
        function Get-VtClaim([string]$d, [long]$n) {
            $f = Join-Path $d (Get-VtName $n)
            try { $lines = [IO.File]::ReadAllText($f, $utf8).Split("`n") } catch { return $null }
            if ($lines.Length -lt 3 -or $lines[1] -cnotmatch '^[0-9]+$') { return $null }
            $run = '-'
            if ($lines.Length -ge 4 -and $lines[3].TrimEnd("`r") -cmatch '^[A-Za-z0-9._-]+$') { $run = $lines[3].TrimEnd("`r") }
            @{ Id = $lines[0]; Holder = $lines[2]; Run = $run; Released = [IO.File]::Exists($f + '.released') }
        }
        if ($action -ceq 'claim') {
            [void][IO.Directory]::CreateDirectory($root)
            $report = New-Object Text.StringBuilder
            foreach ($account in ($accounts -split "`n")) {
                if (-not $account) { continue }
                $d = Join-Path $root $account
                [void][IO.Directory]::CreateDirectory($d)
                $n = Get-VtHighest $d
                if ($n -gt 0) {
                    $claim = Get-VtClaim $d $n
                    if ($null -eq $claim) { [void]$report.Append('unreadable ' + $account + "`n"); continue }
                    if (-not $claim.Released) { [void]$report.Append('held ' + $account + ' ' + $claim.Run + ' ' + $claim.Holder + "`n"); continue }
                }
                $next = $n + 1
                $f = Join-Path $d (Get-VtName $next)
                $t = Join-Path $d ('.new-' + $lease)
                [IO.File]::WriteAllText($t, $lease + "`n" + 'VT_NEVER' + "`n" + $owner + "`n" + $run + "`n", $utf8)
                $won = $false
                if ($vtUnix) {
                    & ln -- $t $f 2> $null
                    $won = $LASTEXITCODE -eq 0
                    [IO.File]::Delete($t)
                    if (-not $won -and -not [IO.File]::Exists($f)) { throw ('could not create ' + $f + '; the lease directory needs hard links') }
                } else {
                    try { [IO.File]::Move($t, $f); $won = $true }
                    catch { if (-not [IO.File]::Exists($f)) { throw } }
                    finally { if ([IO.File]::Exists($t)) { [IO.File]::Delete($t) } }
                }
                if (-not $won) { [void]$report.Append('taken ' + $account + "`n"); continue }
                if ((Get-VtHighest $d) -ne $next) { [IO.File]::Delete($f); [void]$report.Append('taken ' + $account + "`n"); continue }
                foreach ($old in [IO.Directory]::GetFiles($d, 'claim-*')) {
                    if ([IO.Path]::GetFileName($old) -cmatch '^claim-([0-9]{9})' -and [long]$Matches[1] -lt $next - 8) { [IO.File]::Delete($old) }
                }
                'VT-LEASE claimed ' + $account + ' ' + $next
                exit 0
            }
            'VT-LEASE none'
            $report.ToString().TrimEnd("`n")
        } elseif ($action -ceq 'release') {
            $d = Join-Path $root $account
            $n = [long]$number
            $claim = Get-VtClaim $d $n
            if ($null -eq $claim -or $claim.Id -cne $lease) { 'VT-LEASE lost gone'; exit 0 }
            if ((Get-VtHighest $d) -ne $n) { 'VT-LEASE lost taken'; exit 0 }
            if (-not $claim.Released) { [IO.File]::WriteAllText((Join-Path $d (Get-VtName $n)) + '.released', '', $utf8) }
            'VT-LEASE released'
        } elseif ($action -ceq 'list') {
            foreach ($account in ($accounts -split "`n")) {
                if (-not $account) { continue }
                $d = Join-Path $root $account
                $n = Get-VtHighest $d
                if ($n -eq 0) { 'VT-LEASE-ACCOUNT free ' + $account; continue }
                $claim = Get-VtClaim $d $n
                if ($null -eq $claim) { 'VT-LEASE-ACCOUNT unreadable ' + $account; continue }
                if (-not $claim.Released) { 'VT-LEASE-ACCOUNT held ' + $account + ' ' + $claim.Run + ' ' + $claim.Holder }
                else { 'VT-LEASE-ACCOUNT free ' + $account }
            }
            'VT-LEASE listed'
        } elseif ($action -ceq 'abandon') {
            if ([IO.Directory]::Exists($directory)) {
                foreach ($p in [IO.Directory]::GetDirectories($directory)) {
                    foreach ($d in [IO.Directory]::GetDirectories($p)) {
                        $n = Get-VtHighest $d
                        if ($n -eq 0) { continue }
                        $claim = Get-VtClaim $d $n
                        if ($null -eq $claim) { 'VT-LEASE-UNREADABLE ' + [IO.Path]::GetFileName($p) + ' ' + [IO.Path]::GetFileName($d); continue }
                        if ($claim.Released) { continue }
                        if ($claim.Run -cne $run -and -not ($claim.Run -ceq '-' -and $claim.Holder.Contains(' ' + $run + ' client '))) { continue }
                        [IO.File]::WriteAllText((Join-Path $d (Get-VtName $n)) + '.released', '', $utf8)
                        'VT-LEASE-ABANDONED ' + [IO.Path]::GetFileName($p) + ' ' + [IO.Path]::GetFileName($d) + ' ' + $n + ' ' + $claim.Id + ' ' + $claim.Holder
                    }
                }
            }
            'VT-LEASE abandoned'
        } else { exit 2 }
        """.Replace("VT_NEVER", Never).ReplaceLineEndings("\n");
}
