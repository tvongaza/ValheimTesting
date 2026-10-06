using System.IO.Compression;
using Valheim.Testing.Game;
using Xunit;

// The pinned ValheimCLI plugin bundle valheim-test ships: extracted once under ValheimTesting's own folder, by its hash, and
// the first choice only when neither --cli-* nor VALHEIMCLI_BUNDLE names one; an install's own plugins are never picked up.
public sealed class CliBundleTests : IDisposable
{
    private readonly RegressionRig _rig = new();
    public void Dispose() => _rig.Dispose();

    // A bundle zip of the rig's core and Standard pack with their manifest, as the published asset holds them.
    private byte[] Zip(Action<ZipArchive>? extra = null)
    {
        string manifest = _rig.CliManifest(save: true);
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntryFromFile(manifest, CliBundle.ManifestFile);
            foreach (string dll in new[] { "valheimCLI.dll", "Valheim.Cli.Standard.dll" })
                archive.CreateEntryFromFile(Path.Combine(_rig.Root, "cli", dll), dll);
            archive.CreateEntry("LICENSE").Open().Dispose();
            extra?.Invoke(archive);
        }
        return buffer.ToArray();
    }

    [Fact] public void ABundleIsExtractedOnceByItsHashAndExtractedAgainWhenAFileChanged()
    {
        byte[] zip = Zip();
        string sha = FileHash.Sha256(zip), data = Path.Combine(_rig.Root, "data");
        var first = CliBundle.Extract(new MemoryStream(zip), sha, "80fb6ce", "shipped", data);
        Assert.Equal(Path.Combine(data, "cli", "80fb6ce", CliBundle.ManifestFile), first.Manifest);
        Assert.Contains("extracted to", first.Origin);
        Assert.Contains(sha, first.Origin);
        Assert.Equal(FileHash.Sha256(Path.Combine(_rig.Root, "cli", "valheimCLI.dll")), FileHash.Sha256(Path.Combine(first.Files, "valheimCLI.dll")));
        // Reused while intact.
        Assert.Contains(", at ", CliBundle.Extract(new MemoryStream(zip), sha, "80fb6ce", "shipped", data).Origin);
        // A changed DLL in the extracted copy: extracted again, never staged as it is.
        File.AppendAllText(Path.Combine(first.Files, "Valheim.Cli.Standard.dll"), "tampered");
        var again = CliBundle.Extract(new MemoryStream(zip), sha, "80fb6ce", "shipped", data);
        Assert.Contains("extracted to", again.Origin);
        Assert.Equal(FileHash.Sha256(Path.Combine(_rig.Root, "cli", "Valheim.Cli.Standard.dll")), FileHash.Sha256(Path.Combine(again.Files, "Valheim.Cli.Standard.dll")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(data, "cli"), "*.extract-*"));
    }

    // A copy is trusted file by file against the zip: an edited manifest beside a swapped DLL is extracted again, not staged.
    [Fact] public void AnEditedManifestBesideASwappedDllIsNotTrusted()
    {
        byte[] zip = Zip();
        string sha = FileHash.Sha256(zip), data = Path.Combine(_rig.Root, "data");
        var first = CliBundle.Extract(new MemoryStream(zip), sha, "80fb6ce", "shipped", data);
        string dll = Path.Combine(first.Files, "valheimCLI.dll");
        File.AppendAllText(dll, "another build");
        var manifest = CliCapabilityManifest.Read(first.Manifest);
        manifest.Files.Single(file => file.File == "valheimCLI.dll").Sha256 = FileHash.Sha256(dll);
        manifest.Write(first.Manifest);
        var again = CliBundle.Extract(new MemoryStream(zip), sha, "80fb6ce", "shipped", data);
        Assert.Contains("extracted to", again.Origin);
        Assert.Equal(FileHash.Sha256(Path.Combine(_rig.Root, "cli", "valheimCLI.dll")), FileHash.Sha256(dll));
    }

    // Two runs extracting the same bundle at once both end with one intact copy, and neither fails.
    [Fact] public async Task TwoRunsExtractingAtOnceShareOneCopy()
    {
        byte[] zip = Zip();
        string sha = FileHash.Sha256(zip), data = Path.Combine(_rig.Root, "data");
        var runs = Enumerable.Range(0, 4).Select(_ => Task.Run(() => CliBundle.Extract(new MemoryStream(zip), sha, "80fb6ce", "shipped", data))).ToArray();
        var sources = await Task.WhenAll(runs);
        Assert.All(sources, source => Assert.Equal(Path.Combine(data, "cli", "80fb6ce"), source.Files));
        Assert.Equal(FileHash.Sha256(Path.Combine(_rig.Root, "cli", "valheimCLI.dll")), FileHash.Sha256(Path.Combine(data, "cli", "80fb6ce", "valheimCLI.dll")));
        Assert.Single(Directory.GetDirectories(Path.Combine(data, "cli")));
    }

    // Main's red run 37396097745: one of four concurrent extractions threw "Cannot create ... because a file or directory with
    // the same name already exists". Many runs released at once, onto no copy and onto a damaged one (every run then wants to
    // replace it): none throws, each is handed an intact copy, and one folder is left with no copy set aside or half extracted.
    [Fact] public void ManyRunsExtractingAtOnceNeverFailAndLeaveOneIntactCopy()
    {
        byte[] zip = Zip();
        string sha = FileHash.Sha256(zip), dll = FileHash.Sha256(Path.Combine(_rig.Root, "cli", "valheimCLI.dll")),
            standard = FileHash.Sha256(Path.Combine(_rig.Root, "cli", "Valheim.Cli.Standard.dll"));
        const int Runs = 8, Rounds = 24;
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var wrong = new List<string>();
        for (int round = 0; round < Rounds; round++)
        {
            string data = Path.Combine(_rig.Root, "stress-" + round), target = Path.Combine(data, "cli", "80fb6ce");
            if (round % 2 == 1)
            {
                CliBundle.Extract(new MemoryStream(zip), sha, "80fb6ce", "shipped", data);
                File.AppendAllText(Path.Combine(target, "Valheim.Cli.Standard.dll"), "damaged");
            }
            using var start = new Barrier(Runs);
            var sources = new System.Collections.Concurrent.ConcurrentQueue<CliBundleSource>();
            var threads = Enumerable.Range(0, Runs).Select(_ => new Thread(() =>
            {
                start.SignalAndWait();
                try { sources.Enqueue(CliBundle.Extract(new MemoryStream(zip), sha, "80fb6ce", "shipped", data)); }
                catch (Exception error) { failures.Enqueue(error); }
            })).ToList();
            threads.ForEach(thread => thread.Start());
            threads.ForEach(thread => thread.Join());
            // Every run that returned was handed the one copy, which is intact, and nothing else is left beside it.
            if (sources.Any(source => source.Files != target)) wrong.Add($"round {round}: a run was handed another folder");
            // Exactly one run extracted (the others found its copy current, outside the lock or again under it): a current
            // copy is never replaced (ExtractOnce, shared with the shipped BepInExPack).
            int extracted = sources.Count(source => source.Origin.Contains("extracted to", StringComparison.Ordinal));
            if (sources.Count == Runs && extracted != 1) wrong.Add($"round {round}: {extracted} runs extracted, not 1");
            if (!File.Exists(Path.Combine(target, "valheimCLI.dll")) || FileHash.Sha256(Path.Combine(target, "valheimCLI.dll")) != dll ||
                FileHash.Sha256(Path.Combine(target, "Valheim.Cli.Standard.dll")) != standard)
                wrong.Add($"round {round}: the copy is not intact");
            var left = Directory.GetDirectories(Path.Combine(data, "cli")).Where(folder => folder != target).Select(Path.GetFileName).ToList();
            if (left.Count != 0) wrong.Add($"round {round}: left beside the copy: {string.Join(", ", left)}");
        }
        Assert.True(failures.IsEmpty && wrong.Count == 0, $"{failures.Count} of {Runs * Rounds} runs threw; first: {failures.FirstOrDefault()}\n" + string.Join("\n", wrong.Take(10)));
    }

    [Fact] public void AnotherZipOrAZipThatIsNotABundleIsRefused()
    {
        byte[] zip = Zip();
        string data = Path.Combine(_rig.Root, "data");
        Assert.Contains("not the pinned", Assert.Throws<InvalidDataException>(() =>
            CliBundle.Extract(new MemoryStream(zip), new string('0', 64), "80fb6ce", "shipped", data)).Message);
        byte[] nested = Zip(archive => archive.CreateEntry("sub/evil.dll").Open().Dispose());
        Assert.Contains("not a file at its root", Assert.Throws<InvalidDataException>(() =>
            CliBundle.Extract(new MemoryStream(nested), FileHash.Sha256(nested), "80fb6ce", "shipped", data)).Message);
        // A manifest that names a file the zip does not hold.
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            archive.CreateEntryFromFile(_rig.CliManifest(save: true), CliBundle.ManifestFile);
        byte[] partial = buffer.ToArray();
        Assert.Contains("do not match its own manifest", Assert.Throws<InvalidDataException>(() =>
            CliBundle.Extract(new MemoryStream(partial), FileHash.Sha256(partial), "80fb6ce", "shipped", data)).Message);
        Assert.False(Directory.Exists(Path.Combine(data, "cli", "80fb6ce")));
    }

    // Precedence: --cli-* first, then (not tested here: process-wide) VALHEIMCLI_BUNDLE, then the shipped bundle; with
    // none of them the refusal names the choices. A game install's own ValheimCLI is not looked at.
    [Fact] public void TheShippedBundleIsUsedOnlyWhenNothingIsNamed()
    {
        if (Environment.GetEnvironmentVariable("VALHEIMCLI_BUNDLE") is { } named && !string.IsNullOrWhiteSpace(named)) return; // that variable comes first
        byte[] zip = Zip();
        string data = Path.Combine(_rig.Root, "data");
        CliBundleSource Shipped() => CliBundle.Extract(new MemoryStream(zip), FileHash.Sha256(zip), "80fb6ce", "the 80fb6ce bundle shipped with valheim-test", data);
        var (manifest, files) = SmokeInputs.Cli(new Dictionary<string, string>(), Shipped);
        Assert.Equal(Path.Combine(data, "cli", "80fb6ce"), files);
        Assert.Equal(Path.Combine(files, CliBundle.ManifestFile), manifest);

        string given = Path.Combine(_rig.Root, "given");
        Directory.CreateDirectory(given);
        File.Copy(_rig.CliManifest(save: true), Path.Combine(given, "cli-manifest.json"));
        bool asked = false;
        Assert.Equal(given, SmokeInputs.Cli(new Dictionary<string, string> { ["--cli-files"] = given }, () => { asked = true; return null; }).Files);
        Assert.False(asked);

        Assert.Contains("carries no ValheimCLI bundle", Assert.Throws<InvalidDataException>(() => SmokeInputs.Cli(new Dictionary<string, string>(), () => null)).Message);
    }
}
