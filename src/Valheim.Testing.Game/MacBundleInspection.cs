using System.Diagnostics;
using System.Text;

namespace Valheim.Testing.Game;

/// <summary>Shared macOS bundle assessment used by local one-shot runs and hosted copies.</summary>
internal static class MacBundleInspection
{
    internal enum State { None, Accepted, Fixable, Broken, Rejected, Unknown }
    internal sealed record Verdict(State State, int Count, string Detail);

    // The one-shot runner does not use IGameHost. Run the same bounded assessment that hosted preparation sends
    // to a Mac host; pass paths through the environment, never through shell interpolation.
    internal static Verdict Inspect(string install) => Run(install, repair: false);
    internal static Verdict Repair(string install) => Run(install, repair: true);

    internal static string? SourceRefusal(Verdict verdict) => verdict.State switch
    {
        State.Accepted or State.Fixable => null,
        State.Broken => "The source Valheim.app has changed or missing signed files. Verify the game in Steam before running a copied client. " + verdict.Detail,
        State.Rejected => "Gatekeeper rejects the source Valheim.app. Use a Steam-installed, signed copy. " + verdict.Detail,
        _ => "The source Valheim.app could not be assessed by codesign and Gatekeeper; refusing before copying or launching. " + verdict.Detail,
    };

    private static Verdict Run(string install, bool repair)
    {
        if (!OperatingSystem.IsMacOS()) return new(State.None, 0, "not macOS");
        string app = Path.Combine(install, GameLaunch.ClientMacBundle);
        if (!Directory.Exists(app)) return new(State.Unknown, 0, app + " is missing");
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("/bin/bash")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add(Bash);
        process.StartInfo.Environment["app"] = app;
        process.StartInfo.Environment["repair"] = repair ? "1" : "";
        process.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(deadline.Token);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(deadline.Token);
        try { process.WaitForExitAsync(deadline.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new TimeoutException("macOS did not assess Valheim.app within 60 seconds; no client was launched.");
        }
        string output = outputTask.GetAwaiter().GetResult();
        string error = errorTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new IOException($"macOS bundle assessment failed before launch: {error.Trim()}");
        string line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(text => text.StartsWith("VT-BUNDLE ", StringComparison.Ordinal))
            ?? throw new IOException("macOS bundle assessment returned no verdict before launch.");
        string[] parts = line["VT-BUNDLE ".Length..].Trim().Split(' ', 3);
        int count = parts.Length > 1 && int.TryParse(parts[1], out int parsed) ? parsed : 0;
        string detail = "";
        if (parts.Length > 2)
        {
            try { detail = Encoding.UTF8.GetString(Convert.FromBase64String(parts[2])); }
            catch (FormatException) { detail = parts[2]; }
        }
        var state = parts[0] switch
        {
            "accepted" => State.Accepted, "fixable" => State.Fixable, "broken" => State.Broken,
            "rejected" => State.Rejected, "tools" => State.Unknown, _ => State.Unknown,
        };
        return new(state, count, detail.Trim());
    }

    internal static readonly string Bash = """
        set -u
        if [ "$(uname -s)" != Darwin ] || [ ! -d "$app" ]; then echo "VT-BUNDLE none"; exit 0; fi
        command -v codesign > /dev/null && command -v spctl > /dev/null || { echo "VT-BUNDLE tools"; exit 0; }
        # codesign names files by their physical path (/tmp is /private/tmp), so compare against that.
        app=$(cd "$app" && pwd -P) || { echo "VT-BUNDLE none"; exit 0; }
        b64() { base64 | tr -d '\n'; }
        added=""; other=""; initial_valid=0
        if ! report=$(codesign --verify --deep --strict -vvvv "$app" 2>&1); then
          added=$(printf '%s\n' "$report" | sed -n 's/^file added: //p')
          # Anything but added files (a changed or missing sealed file, a nested component's signature) no repair can fix.
          other=$(printf '%s\n' "$report" | grep -vE '^(file added: |--prepared:|--validated:)|: a sealed resource is missing or invalid$' | grep . || true)
          [ -n "$added" ] || [ -n "$other" ] || other=$report
        else
          initial_valid=1
        fi
        if [ -n "$other" ]; then echo "VT-BUNDLE broken $(printf '%s\n' "$other" | grep -c .) $(printf '%s\n' "$other" | head -20 | b64)"; exit 0; fi
        removed=0
        if [ -n "${repair:-}" ]; then
          xattr -dr com.apple.quarantine "$app" 2> /dev/null || true
          while IFS= read -r file; do
            [ -n "$file" ] || continue
            case "$file" in "$app"/*) ;; *) continue;; esac
            # A linked parent could make a textual path inside the app point outside the disposable copy.
            parent=$(cd "$(dirname "$file")" && pwd -P) || continue
            case "$parent" in "$app"|"$app"/*) ;; *) continue;; esac
            if [ -f "$file" ] && [ ! -L "$file" ]; then rm -f -- "$file" && removed=$((removed + 1)); fi
          done <<< "$added"
        elif [ -n "$added" ]; then
          relative=""
          while IFS= read -r file; do relative+="${file#"$app"/}"$'\n'; done <<< "$added"
          echo "VT-BUNDLE fixable $(printf '%s\n' "$added" | grep -c .) $(printf '%s' "$relative" | head -20 | b64)"; exit 0
        fi
        # The first verification already proved an unchanged bundle. Recheck only after removing added files.
        verify=""
        if [ "$initial_valid" -eq 0 ] || [ "$removed" -ne 0 ]; then
          if ! verify=$(codesign --verify --deep --strict "$app" 2>&1); then
            echo "VT-BUNDLE rejected $removed $(printf '%s' "$verify" | head -20 | b64)"; exit 0
          fi
        fi
        if assess=$(spctl -a -t exec -vv "$app" 2>&1); then
          echo "VT-BUNDLE accepted $removed $(printf '%s' "${assess:-}" | b64)"
        else
          echo "VT-BUNDLE rejected $removed $(printf '%s\n%s\n' "${verify:-}" "${assess:-}" | head -20 | b64)"
        fi
        """.ReplaceLineEndings("\n");
}
