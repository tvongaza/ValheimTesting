using System.Text.RegularExpressions;

namespace Valheim.Testing.Game;

/// <summary>One registered, disposable local character for one named campaign client.</summary>
public sealed class HostedCampaignCharacter
{
    /// <summary>A <see cref="DisposableCharacterStore"/> created from a test character, never a personal save.</summary>
    public string Store { get; set; } = "";
    public string RegisteredName { get; set; } = "";
    /// <summary>Fresh filename without .fch on this host. Two simultaneous clients need different player IDs too.</summary>
    public string FileName { get; set; } = "";
    /// <summary>
    /// Optional: the client host user's <c>characters_local</c>. Left out, the client host's standard folder for its platform,
    /// resolved on that host (Windows <c>%USERPROFILE%\AppData\LocalLow\IronGate\Valheim\characters_local</c>, Linux
    /// <c>~/.config/unity3d/IronGate/Valheim/characters_local</c>, macOS <c>~/Library/Application Support/IronGate/Valheim/characters_local</c>).
    /// </summary>
    public string CharactersLocalDirectory { get; set; } = "";
    /// <summary>
    /// Optional: the client host's Steam <c>userdata</c>, searched for a same-named Steam Cloud character. Left out, the first that
    /// exists on that host: Windows Steam's registered <c>SteamPath</c>, then <c>Program Files (x86)\Steam</c>; Linux
    /// <c>~/.local/share/Steam</c>, <c>~/.steam/steam</c>, then the Flatpak's; macOS <c>~/Library/Application Support/Steam</c>.
    /// </summary>
    public string SteamUserDataDirectory { get; set; } = "";

    internal HostedCampaignCharacter WithDirectories(string characters, string userdata)
    {
        var copy = (HostedCampaignCharacter)MemberwiseClone();
        copy.CharactersLocalDirectory = characters; copy.SteamUserDataDirectory = userdata;
        return copy;
    }
}

/// <summary>A client host's character and Steam userdata folders as resolved there (null when missing), and what is missing.</summary>
internal sealed record CharacterDirectories(string? Characters, string? UserData, string? Missing);

internal sealed record HostedCharacterSelection(HostedCampaignCharacter Input, DisposableCharacter Handle, string File);

/// <summary>Stages and retires a registered character on its client's host, with collision checks on local and cloud saves.</summary>
internal static class HostedCharacterStage
{
    private static readonly Regex Name = new("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$", RegexOptions.CultureInvariant);

    internal static HostedCharacterSelection Select(HostedCampaignCharacter input, string localDirectory)
    {
        if (string.IsNullOrWhiteSpace(input.Store) || string.IsNullOrWhiteSpace(input.RegisteredName) ||
            string.IsNullOrWhiteSpace(input.FileName) || !Name.IsMatch(input.FileName))
            throw new ArgumentException("A campaign character needs a store, registeredName and fresh fileName of letters, digits, _ or -.");
        input.Store = Path.GetFullPath(input.Store, localDirectory);
        var store = DisposableCharacterStore.Open(input.Store);
        var handle = store.Get(input.RegisteredName);
        string file = store.StoredFile(handle.Name);
        if (!FileHash.Sha256(file).Equals(handle.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The registered character changed before staging: " + input.RegisteredName);
        return new(input, handle, file);
    }

    /// <summary>
    /// Resolves the character's folders on its client host: each given one as given, each left out from the host's standard
    /// paths for <paramref name="platform"/> (<c>windows</c>, <c>linux</c> or <c>macos</c>). <see cref="CharacterDirectories.Missing"/>
    /// names the folder and every path tried when one does not exist. <paramref name="userHome"/> replaces the host user's home
    /// in controlled tests.
    /// </summary>
    internal static async Task<CharacterDirectories> ResolveDirectoriesAsync(IGameHost host, string platform, HostedCampaignCharacter input,
        TimeSpan timeout, CancellationToken cancellation, string userHome = "", string steamRoot = "")
    {
        var reply = await host.RunAsync(host.Shell.Kind == HostShellKind.PowerShell ? PowerShellDirectories : BashDirectories,
            new Dictionary<string, string>
            {
                ["platform"] = platform, ["characters"] = input.CharactersLocalDirectory, ["userdata"] = input.SteamUserDataDirectory,
                ["userhome"] = userHome, ["steamroot"] = steamRoot,
            }, timeout, cancellation).ConfigureAwait(false);
        reply.EnsureSuccess($"Resolving the character folders on {host.Name}");
        string? characters = null, userdata = null;
        var tried = new Dictionary<string, List<string>> { ["characters"] = [], ["userdata"] = [] };
        foreach (string line in reply.Stdout.Split('\n').Select(line => line.TrimEnd('\r')))
        {
            var match = Regex.Match(line, "^VT-CHARDIR (characters|userdata) (found|tried) (.+)$", RegexOptions.CultureInvariant);
            if (!match.Success) continue;
            if (match.Groups[2].Value == "tried") tried[match.Groups[1].Value].Add(match.Groups[3].Value);
            else if (match.Groups[1].Value == "characters") characters = match.Groups[3].Value;
            else userdata = match.Groups[3].Value;
        }
        if (tried["characters"].Count == 0 || tried["userdata"].Count == 0)
            throw new HostOperationException($"Unexpected reply while resolving the character folders on {host.Name}", reply);
        var missing = new List<string>();
        if (characters == null) missing.Add("characters_local (tried " + string.Join(", ", tried["characters"]) + ")");
        if (userdata == null) missing.Add("Steam userdata (tried " + string.Join(", ", tried["userdata"]) + ")");
        if (missing.Count != 0) return new CharacterDirectories(characters, userdata, $"No {string.Join(" and no ", missing)} on {host.Name}.");
        // A resolved folder is staged into: the same shape rules as a given one, before anything is copied.
        CheckHostPaths(host, input.WithDirectories(characters!, userdata!));
        return new CharacterDirectories(characters, userdata, null);
    }

    internal static async Task StageAsync(IGameHost host, HostedCharacterSelection selected, string staging, TimeSpan timeout,
        CancellationToken cancellation)
    {
        CheckHostPaths(host, selected.Input);
        HostInstall.RequireHostPath(host, staging, nameof(staging));
        string payload = Path.Combine(Path.GetTempPath(), "vt-char-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(payload);
        bool shipped = false;
        try
        {
            string saveFile = DisposableCharacterStore.SaveFile(selected.Input.FileName);
            string target = Path.Combine(payload, saveFile);
            File.Copy(selected.File, target);
            if (!FileHash.Sha256(target).Equals(selected.Handle.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The local copy of the registered character changed.");
            shipped = true;
            await host.ShipFilesAsync(payload, staging, timeout, cancellation).ConfigureAwait(false);
            var listing = await HostInstall.ListAsync(host, staging, timeout, cancellation: cancellation).ConfigureAwait(false);
            if (!listing.Files.TryGetValue(saveFile, out string? hash) ||
                !hash.Equals(selected.Handle.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"The staged character on {host.Name} differs from its registered source.");
            HostResult reply;
            try
            {
                reply = await host.RunAsync(host.Shell.Kind == HostShellKind.PowerShell ? WindowsInstall : BashInstall,
                    Variables(selected.Input, staging), timeout, cancellation).ConfigureAwait(false);
                reply.EnsureSuccess($"Staging disposable character on {host.Name}");
            }
            catch (Exception error)
            {
                // A lost reply can follow a successful atomic install. We cannot retire by name here: a collision
                // with a pre-existing save has not been ruled out by a proven verdict. Name the possible residue.
                throw new IOException($"Character staging on {host.Name} was not proven. Inspect the disposable name " +
                    $"{selected.Input.FileName} in characters_local before retrying; cleanup will not delete an unowned save.", error);
            }
            switch (InteractiveClient.Line(reply.Stdout, "VT-CHAR "))
            {
                case "staged":
                    await DropStageAsync(host, staging, timeout).ConfigureAwait(false);
                    shipped = false;
                    return;
                case "collision": throw new IOException($"A local or Steam Cloud character named {selected.Input.FileName} already exists on {host.Name}; choose a fresh disposable filename.");
                case "missing-directory": throw new DirectoryNotFoundException($"The client character or Steam userdata folder is missing on {host.Name}.");
                default: throw new HostOperationException($"Character staging on {host.Name} was not proven; inspect the disposable name " +
                    $"{selected.Input.FileName} in characters_local before retrying.", reply);
            }
        }
        catch (Exception original)
        {
            if (shipped)
            {
                try { await DropStageAsync(host, staging, timeout).ConfigureAwait(false); }
                catch (Exception cleanup)
                {
                    throw new AggregateException("Character staging failed and its remote staging directory was not proven removed.", original, cleanup);
                }
            }
            throw;
        }
        finally
        {
            Directory.Delete(payload, recursive: true);
        }
    }

    internal static async Task RetireAsync(IGameHost host, HostedCampaignCharacter input, TimeSpan timeout, CancellationToken cancellation = default)
    {
        CheckHostPaths(host, input);
        var reply = await host.RunAsync(host.Shell.Kind == HostShellKind.PowerShell ? WindowsRetire : BashRetire,
            Variables(input, ""), timeout, cancellation).ConfigureAwait(false);
        reply.EnsureSuccess($"Retiring disposable character on {host.Name}");
        if (InteractiveClient.Line(reply.Stdout, "VT-CHAR-RETIRED") == null)
            throw new HostOperationException($"Unexpected character retirement reply from {host.Name}", reply);
    }

    private static async Task DropStageAsync(IGameHost host, string staging, TimeSpan timeout)
    {
        var reply = await host.RunAsync(host.Shell.Kind == HostShellKind.PowerShell ? WindowsDropStage : BashDropStage,
            new Dictionary<string, string> { ["stage"] = staging }, timeout).ConfigureAwait(false);
        reply.EnsureSuccess($"Removing character staging on {host.Name}");
        if (InteractiveClient.Line(reply.Stdout, "VT-CHAR-STAGE-DROPPED") == null)
            throw new HostOperationException($"Character staging cleanup on {host.Name} was not proven", reply);
    }

    // The character-file rule comes from its one owner (DisposableCharacterStore.HostScriptVariables); the scripts only apply it.
    internal static Dictionary<string, string> Variables(HostedCampaignCharacter input, string staging) =>
        new(DisposableCharacterStore.HostScriptVariables(input.FileName, input.CharactersLocalDirectory))
        {
            ["stage"] = staging, ["characters"] = input.CharactersLocalDirectory, ["userdata"] = input.SteamUserDataDirectory,
        };

    internal static void CheckHostPaths(IGameHost host, HostedCampaignCharacter input)
    {
        HostInstall.RequireHostPath(host, input.CharactersLocalDirectory, nameof(input.CharactersLocalDirectory));
        HostInstall.RequireHostPath(host, input.SteamUserDataDirectory, nameof(input.SteamUserDataDirectory));
        string local = input.CharactersLocalDirectory.Replace('\\', '/').TrimEnd('/');
        string steam = input.SteamUserDataDirectory.Replace('\\', '/').TrimEnd('/');
        if (!local.EndsWith("/characters_local", StringComparison.OrdinalIgnoreCase) ||
            !steam.EndsWith("/userdata", StringComparison.OrdinalIgnoreCase) ||
            local.Split('/').Concat(steam.Split('/')).Any(part => part is "." or "..") || !Name.IsMatch(input.FileName))
            throw new ArgumentException("Stage only a fresh character filename into characters_local with the matching Steam userdata folder.");
    }

    // Whether a file is one of the character's own: DisposableCharacterStore.IsCharacterFile, its table given as variables.
    private const string WindowsOwned = """
        $characterNames = @($names -split "`n"); $characterPrefixes = @($prefixes -split "`n")
        function Test-CharacterFile([string] $file) {
            $base = [IO.Path]::GetFileName($file)
            foreach ($entry in $characterNames) { if ($base.Equals($entry, [StringComparison]::OrdinalIgnoreCase)) { return $true } }
            foreach ($entry in $characterPrefixes) { if ($entry.Length -ne 0 -and $base.StartsWith($entry, [StringComparison]::OrdinalIgnoreCase)) { return $true } }
            return $false
        }

        """;

    private const string BashOwned = """
        set -eu
        shopt -s nocasematch
        is_character_file() {
          local base=${1##*/} entry
          while IFS= read -r entry; do [[ $base == "$entry" ]] && return 0; done <<< "$names"
          while IFS= read -r entry; do [[ -n $entry && $base == "$entry"* ]] && return 0; done <<< "$prefixes"
          return 1
        }

        """;

    // Variables: platform, characters, userdata, userhome and steamroot (each may be empty; userhome and steamroot only for
    // controlled tests). One "found" or "tried" line per path considered. Steam's userdata is searched in the order the signed-in
    // identity is read (SteamSignedInUsers); Valheim's characters_local is the one beside it: a Flatpak Steam's game keeps its
    // saves inside the Flatpak's own home.
    internal static readonly string BashDirectories = """
        set -u
        home=${userhome:-$HOME}
        if [ "$platform" = macos ]; then
          set -- "$home/Library/Application Support/Steam/userdata"
        else
          set -- "$home/.local/share/Steam/userdata" "$home/.steam/steam/userdata" "$home/.var/app/com.valvesoftware.Steam/.local/share/Steam/userdata"
        fi
        if [ -n "$userdata" ]; then set -- "$userdata"; fi
        steamfound=""
        for folder in "$@"; do
          echo "VT-CHARDIR userdata tried $folder"
          if [ -d "$folder" ]; then echo "VT-CHARDIR userdata found $folder"; steamfound=$folder; break; fi
        done
        if [ "$platform" = macos ]; then
          standard="$home/Library/Application Support/IronGate/Valheim/characters_local"
        else
          case "$steamfound" in
            "$home/.var/app/com.valvesoftware.Steam/"*) config="$home/.var/app/com.valvesoftware.Steam/.config" ;;
            *) if [ -n "$userhome" ]; then config="$home/.config"; else config="${XDG_CONFIG_HOME:-$home/.config}"; fi ;;
          esac
          standard="$config/unity3d/IronGate/Valheim/characters_local"
        fi
        folder=${characters:-$standard}
        echo "VT-CHARDIR characters tried $folder"
        if [ -d "$folder" ]; then echo "VT-CHARDIR characters found $folder"; fi
        """.ReplaceLineEndings("\n");

    internal static readonly string PowerShellDirectories = """
        $userHomeDir = if ($userhome) { $userhome } elseif ($platform -eq 'windows' -and $env:USERPROFILE) { $env:USERPROFILE } elseif ($env:HOME) { $env:HOME } else { [Environment]::GetFolderPath('UserProfile') }
        $candidates = @()
        if ($platform -eq 'windows') {
            $steam = $steamroot
            if (-not $steam) { try { $steam = (Get-ItemProperty -Path 'HKCU:\Software\Valve\Steam' -Name SteamPath -ErrorAction Stop).SteamPath } catch { } }
            # Steam writes its registered path with forward slashes.
            if ($steam) { $candidates += [IO.Path]::Combine(($steam -replace '/', '\'), 'userdata') }
            $x86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
            if ($x86 -and -not $steamroot) { $candidates += [IO.Path]::Combine($x86, 'Steam', 'userdata') }
        } elseif ($platform -eq 'macos') {
            $candidates += "$userHomeDir/Library/Application Support/Steam/userdata"
        } else {
            $candidates += "$userHomeDir/.local/share/Steam/userdata", "$userHomeDir/.steam/steam/userdata", "$userHomeDir/.var/app/com.valvesoftware.Steam/.local/share/Steam/userdata"
        }
        if ($userdata) { $candidates = @($userdata) }
        $steamFound = $null
        foreach ($candidate in $candidates) {
            "VT-CHARDIR userdata tried $candidate"
            if ([IO.Directory]::Exists($candidate)) { "VT-CHARDIR userdata found $candidate"; $steamFound = $candidate; break }
        }
        if ($platform -eq 'windows') { $standard = [IO.Path]::Combine($userHomeDir, 'AppData', 'LocalLow', 'IronGate', 'Valheim', 'characters_local') }
        elseif ($platform -eq 'macos') { $standard = "$userHomeDir/Library/Application Support/IronGate/Valheim/characters_local" }
        else {
            $flatpak = "$userHomeDir/.var/app/com.valvesoftware.Steam/"
            $config = if ($steamFound -and $steamFound.StartsWith($flatpak)) { $flatpak + '.config' } elseif (-not $userhome -and $env:XDG_CONFIG_HOME) { $env:XDG_CONFIG_HOME } else { "$userHomeDir/.config" }
            $standard = "$config/unity3d/IronGate/Valheim/characters_local"
        }
        $folder = if ($characters) { $characters } else { $standard }
        "VT-CHARDIR characters tried $folder"
        if ([IO.Directory]::Exists($folder)) { "VT-CHARDIR characters found $folder" }
        """.ReplaceLineEndings("\n");

    internal static readonly string WindowsInstall = (WindowsOwned + """
        if (-not [IO.Directory]::Exists($characters) -or -not [IO.Directory]::Exists($userdata)) { 'VT-CHAR missing-directory'; exit 0 }
        $folders = @($characters, $cloud)
        foreach ($account in [IO.Directory]::GetDirectories($userdata)) { $folders += [IO.Path]::Combine($account, $remote) }
        foreach ($folder in $folders) {
            if (-not [IO.Directory]::Exists($folder)) { continue }
            foreach ($file in [IO.Directory]::GetFiles($folder)) { if (Test-CharacterFile $file) { 'VT-CHAR collision'; exit 0 } }
        }
        $source = [IO.Path]::Combine($stage, $save)
        $target = [IO.Path]::Combine($characters, $save)
        $temporary = [IO.Path]::Combine($characters, '.vt-character-' + [Guid]::NewGuid().ToString('N'))
        try {
            [IO.File]::Copy($source, $temporary, $false)
            [IO.File]::Move($temporary, $target)
        } finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
        'VT-CHAR staged'
        """).ReplaceLineEndings("\n");

    internal static readonly string BashInstall = (BashOwned + """
        if [ ! -d "$characters" ] || [ ! -d "$userdata" ]; then echo 'VT-CHAR missing-directory'; exit 0; fi
        for folder in "$characters" "$cloud" "$userdata"/*/"$remote"; do
          [ -d "$folder" ] || continue
          for file in "$folder"/*; do
            [ -f "$file" ] || continue
            if is_character_file "$file"; then echo 'VT-CHAR collision'; exit 0; fi
          done
        done
        temporary=$(mktemp "$characters/.vt-character.XXXXXXXX")
        trap 'rm -f "$temporary"' EXIT
        cp "$stage/$save" "$temporary"
        ln "$temporary" "$characters/$save"
        rm -f "$temporary"
        trap - EXIT
        echo 'VT-CHAR staged'
        """).ReplaceLineEndings("\n");

    internal static readonly string WindowsRetire = (WindowsOwned + """
        if (-not [IO.Directory]::Exists($characters)) { 'VT-CHAR-RETIRED'; exit 0 }
        foreach ($file in [IO.Directory]::GetFiles($characters)) { if (Test-CharacterFile $file) { [IO.File]::Delete($file) } }
        'VT-CHAR-RETIRED'
        """).ReplaceLineEndings("\n");

    internal static readonly string BashRetire = (BashOwned + """
        if [ -d "$characters" ]; then
          for file in "$characters"/*; do
            [ -f "$file" ] || continue
            if is_character_file "$file"; then rm -f "$file"; fi
          done
        fi
        echo 'VT-CHAR-RETIRED'
        """).ReplaceLineEndings("\n");

    internal static readonly string WindowsDropStage = "if ([IO.Directory]::Exists($stage)) { [IO.Directory]::Delete($stage, $true) }; 'VT-CHAR-STAGE-DROPPED'";
    internal static readonly string BashDropStage = "if [ -d \"$stage\" ]; then rm -rf -- \"$stage\"; fi; echo VT-CHAR-STAGE-DROPPED";
}
