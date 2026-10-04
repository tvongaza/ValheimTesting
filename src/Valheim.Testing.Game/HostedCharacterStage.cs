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
    public string CharactersLocalDirectory { get; set; } = "";
    public string SteamUserDataDirectory { get; set; } = "";
}

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
        var handle = DisposableCharacterStore.Open(input.Store).Get(input.RegisteredName);
        string file = Path.Combine(input.Store, input.RegisteredName + ".fch");
        if (!WorldFixture.Hash(file).Equals(handle.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The registered character changed before staging: " + input.RegisteredName);
        return new(input, handle, file);
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
            string target = Path.Combine(payload, selected.Input.FileName + ".fch");
            File.Copy(selected.File, target);
            if (!WorldFixture.Hash(target).Equals(selected.Handle.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The local copy of the registered character changed.");
            shipped = true;
            await host.ShipFilesAsync(payload, staging, timeout, cancellation).ConfigureAwait(false);
            var listing = await HostInstall.ListAsync(host, staging, timeout, cancellation: cancellation).ConfigureAwait(false);
            if (!listing.Files.TryGetValue(selected.Input.FileName + ".fch", out string? hash) ||
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

    internal static async Task RetireAsync(IGameHost host, HostedCampaignCharacter input, TimeSpan timeout)
    {
        CheckHostPaths(host, input);
        var reply = await host.RunAsync(host.Shell.Kind == HostShellKind.PowerShell ? WindowsRetire : BashRetire,
            Variables(input, ""), timeout).ConfigureAwait(false);
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

    private static Dictionary<string, string> Variables(HostedCampaignCharacter input, string staging) => new()
    {
        ["stage"] = staging, ["characters"] = input.CharactersLocalDirectory, ["userdata"] = input.SteamUserDataDirectory,
        ["name"] = input.FileName,
    };

    private static void CheckHostPaths(IGameHost host, HostedCampaignCharacter input)
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

    internal static readonly string WindowsInstall = """
        if (-not [IO.Directory]::Exists($characters) -or -not [IO.Directory]::Exists($userdata)) { 'VT-CHAR missing-directory'; exit 0 }
        $parent = [IO.Directory]::GetParent($characters).FullName
        $cloud = Join-Path $parent 'characters'
        $folders = @($characters, $cloud)
        foreach ($account in [IO.Directory]::GetDirectories($userdata)) { $folders += (Join-Path $account '892970\remote\characters') }
        $prefix = $name + '.fch'
        foreach ($folder in $folders) {
            if (-not [IO.Directory]::Exists($folder)) { continue }
            foreach ($file in [IO.Directory]::GetFiles($folder)) {
                $base = [IO.Path]::GetFileName($file)
                if ($base.Equals($prefix, [StringComparison]::OrdinalIgnoreCase) -or
                    $base.Equals($prefix + '.old', [StringComparison]::OrdinalIgnoreCase) -or
                    $base.StartsWith($name + '_backup_auto-', [StringComparison]::OrdinalIgnoreCase)) {
                    'VT-CHAR collision'; exit 0
                }
            }
        }
        $source = Join-Path $stage ($name + '.fch')
        $target = Join-Path $characters ($name + '.fch')
        $temporary = Join-Path $characters ('.vt-character-' + [Guid]::NewGuid().ToString('N'))
        try {
            [IO.File]::Copy($source, $temporary, $false)
            [IO.File]::Move($temporary, $target)
        } finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
        'VT-CHAR staged'
        """.ReplaceLineEndings("\n");

    internal static readonly string BashInstall = """
        set -eu
        if [ ! -d "$characters" ] || [ ! -d "$userdata" ]; then echo 'VT-CHAR missing-directory'; exit 0; fi
        parent=$(dirname "$characters")
        for folder in "$characters" "$parent/characters" "$userdata"/*/892970/remote/characters; do
          [ -d "$folder" ] || continue
          for file in "$folder"/*; do
            [ -f "$file" ] || continue
            base=${file##*/}
            lower=$(printf '%s' "$base" | tr '[:upper:]' '[:lower:]')
            wanted=$(printf '%s' "$name" | tr '[:upper:]' '[:lower:]')
            case "$lower" in "$wanted.fch"|"$wanted.fch.old"|"${wanted}_backup_auto-"*) echo 'VT-CHAR collision'; exit 0;; esac
          done
        done
        temporary=$(mktemp "$characters/.vt-character.XXXXXXXX")
        trap 'rm -f "$temporary"' EXIT
        cp "$stage/$name.fch" "$temporary"
        ln "$temporary" "$characters/$name.fch"
        rm -f "$temporary"
        trap - EXIT
        echo 'VT-CHAR staged'
        """.ReplaceLineEndings("\n");

    internal static readonly string WindowsRetire = """
        if (-not [IO.Directory]::Exists($characters)) { 'VT-CHAR-RETIRED'; exit 0 }
        foreach ($file in [IO.Directory]::GetFiles($characters)) {
            $base = [IO.Path]::GetFileName($file)
            $prefix = $name + '.fch'
            if ($base.Equals($prefix, [StringComparison]::OrdinalIgnoreCase) -or
                $base.Equals($prefix + '.old', [StringComparison]::OrdinalIgnoreCase) -or
                $base.StartsWith($name + '_backup_auto-', [StringComparison]::OrdinalIgnoreCase)) { [IO.File]::Delete($file) }
        }
        'VT-CHAR-RETIRED'
        """.ReplaceLineEndings("\n");

    internal static readonly string BashRetire = """
        set -eu
        if [ -d "$characters" ]; then
          for file in "$characters"/*; do
            [ -f "$file" ] || continue
            base=${file##*/}
            lower=$(printf '%s' "$base" | tr '[:upper:]' '[:lower:]')
            wanted=$(printf '%s' "$name" | tr '[:upper:]' '[:lower:]')
            case "$lower" in "$wanted.fch"|"$wanted.fch.old"|"${wanted}_backup_auto-"*) rm -f "$file";; esac
          done
        fi
        echo 'VT-CHAR-RETIRED'
        """.ReplaceLineEndings("\n");

    internal static readonly string WindowsDropStage = "if ([IO.Directory]::Exists($stage)) { [IO.Directory]::Delete($stage, $true) }; 'VT-CHAR-STAGE-DROPPED'";
    internal static readonly string BashDropStage = "if [ -d \"$stage\" ]; then rm -rf -- \"$stage\"; fi; echo VT-CHAR-STAGE-DROPPED";
}
