using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Valheim.Testing.Game;

/// <summary>Retains the sources the compiler embedded in a one-shot adapter, plus its evaluated build inputs.</summary>
internal static class AdapterSourceInputs
{
    private static readonly string[] OtherItemTypes = ["AdditionalFiles", "EditorConfigFiles", "EmbeddedResource", "Analyzer"];
    private static readonly Guid Sha1 = new("ff1816ec-aa5e-4d10-87f7-6f4963833460");
    private static readonly Guid Sha256 = new("8829d00f-11b8-4213-878b-770e8597ac16");
    private static readonly Guid EmbeddedSource = new("0e8a571b-6926-466e-b4ad-8ab04611f5fe");

    internal sealed record Input(string Path, string Sha256, string Retained);
    internal sealed record Source(string Document, string HashAlgorithm, string Checksum, string Sha256, string Retained);
    internal sealed record Record(string Project, string Adapter, string AdapterSha256, string SourceInputSha256,
        string CompiledSourceSha256, string EvaluatedBuildSha256, IReadOnlyList<Input> OtherInputs,
        IReadOnlyList<Source> Sources);

    internal sealed class Snapshot(string project, string output, string evaluatedHash, IReadOnlyList<Input> inputs)
    {
        internal string Project { get; } = project;
        internal string Output { get; } = output;
        internal string EvaluatedHash { get; } = evaluatedHash;
        internal IReadOnlyList<Input> Inputs { get; } = inputs;
    }

    internal static async Task<Snapshot> BeforeAsync(string project, string output, IReadOnlyList<string> arguments,
        string packages, string http, CancellationToken cancellation)
    {
        string evidence = Path.Combine(output, "source-inputs");
        Directory.CreateDirectory(evidence);
        string evaluated = Path.Combine(evidence, "evaluated-before.xml");
        await EvaluateAsync(project, arguments, packages, http, evaluated, cancellation).ConfigureAwait(false);
        var inputs = await ReadOtherInputsAsync(project, evaluated, evidence, arguments, packages, http, cancellation).ConfigureAwait(false);
        foreach (var input in inputs) Retain(input.Path, input.Retained, input.Sha256);
        return new Snapshot(project, output, FileHash.Sha256(evaluated), inputs);
    }

    internal static async Task<Record> AfterAsync(Snapshot before, string adapter, IReadOnlyList<string> arguments,
        string packages, string http, CancellationToken cancellation)
    {
        string evidence = Path.Combine(before.Output, "source-inputs");
        string evaluated = Path.Combine(evidence, "evaluated-after.xml");
        await EvaluateAsync(before.Project, arguments, packages, http, evaluated, cancellation).ConfigureAwait(false);
        if (FileHash.Sha256(evaluated) != before.EvaluatedHash)
            throw new InvalidDataException("The adapter's imported build settings changed during compilation; rebuild it without editing the project or its imports.");
        var after = await ReadOtherInputsAsync(before.Project, evaluated, evidence, arguments, packages, http, cancellation).ConfigureAwait(false);
        File.Delete(evaluated);
        if (!before.Inputs.Select(input => (input.Path, input.Sha256)).SequenceEqual(after.Select(input => (input.Path, input.Sha256))))
            throw new InvalidDataException("An adapter build input changed during compilation; rebuild it without editing its resources, analyzer files or project.");
        foreach (var input in before.Inputs)
            if (!File.Exists(input.Retained) || FileHash.Sha256(input.Retained) != input.Sha256)
                throw new InvalidDataException("A retained adapter build input changed: " + input.Path);

        IReadOnlyList<Source> sources = RetainCompiledSources(adapter, evidence);
        string compiled = Digest(sources.Select(source => source.Sha256));
        // Raw project/import files and compiler-produced source are stable across A/B arms even when ModDll differs.
        // The expanded MSBuild settings and exact command-line properties are recorded separately in inputs.json.
        string sourceInput = Digest(sources.Select(source => source.Sha256)
            .Concat(before.Inputs.Select(input => input.Sha256)));
        return new Record(before.Project, adapter, FileHash.Sha256(adapter), sourceInput, compiled,
            before.EvaluatedHash, before.Inputs, sources);
    }

    private static async Task EvaluateAsync(string project, IReadOnlyList<string> arguments, string packages, string http,
        string destination, CancellationToken cancellation)
    {
        var command = Start("msbuild", project, packages, http);
        foreach (string argument in arguments) command.ArgumentList.Add(argument);
        command.ArgumentList.Add("-preprocess:" + destination);
        await RunAsync(command, cancellation).ConfigureAwait(false);
        if (!File.Exists(destination) || new FileInfo(destination).Length == 0)
            throw new InvalidDataException("MSBuild did not produce the adapter's evaluated project and imports.");
    }

    private static async Task<IReadOnlyList<Input>> ReadOtherInputsAsync(string project, string evaluated, string evidence,
        IReadOnlyList<string> arguments, string packages, string http, CancellationToken cancellation)
    {
        var command = Start("msbuild", project, packages, http);
        foreach (string argument in arguments) command.ArgumentList.Add(argument);
        foreach (string item in OtherItemTypes)
            command.ArgumentList.Add("-getItem:" + item);
        command.ArgumentList.Add("-getProperty:ProjectAssetsFile");
        string output = await RunAsync(command, cancellation).ConfigureAwait(false);
        using var document = JsonDocument.Parse(output);
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        paths.Add(Path.GetFullPath(project));
        if (!document.RootElement.TryGetProperty("Properties", out var properties) ||
            !properties.TryGetProperty("ProjectAssetsFile", out var assets) ||
            string.IsNullOrWhiteSpace(assets.GetString()))
            throw new InvalidDataException("MSBuild did not identify the restored adapter package assets file.");
        string assetsFile = assets.GetString()!;
        paths.Add(Path.GetFullPath(Path.IsPathFullyQualified(assetsFile) ? assetsFile :
            Path.Combine(Path.GetDirectoryName(project)!, assetsFile)));
        foreach (Match match in Regex.Matches(File.ReadAllText(evaluated),
            @"(?m)^[ \t]*(?<path>(?:[A-Za-z]:[\\/]|/)[^\r\n<>]*\.(?:props|targets|csproj))[ \t]*\r?$",
            RegexOptions.IgnoreCase))
        {
            string path = Path.GetFullPath(match.Groups["path"].Value);
            if (!File.Exists(path))
                throw new InvalidDataException("An imported adapter build setting cannot be retained: " + path);
            paths.Add(path);
        }
        foreach (string item in OtherItemTypes)
            foreach (var entry in document.RootElement.GetProperty("Items").GetProperty(item).EnumerateArray())
            {
                if (!entry.TryGetProperty("FullPath", out var full) || string.IsNullOrWhiteSpace(full.GetString()))
                    throw new InvalidDataException("MSBuild returned an unresolved adapter " + item + " item.");
                paths.Add(Path.GetFullPath(full.GetString()!));
            }
        return paths.Select(path =>
        {
            if (!File.Exists(path)) throw new InvalidDataException("Adapter build input is missing: " + path);
            string hash = FileHash.Sha256(path);
            return new Input(path, hash, Path.Combine(evidence, "inputs", hash + Path.GetExtension(path)));
        }).ToArray();
    }

    private static IReadOnlyList<Source> RetainCompiledSources(string adapter, string evidence)
    {
        using var stream = File.OpenRead(adapter);
        using var pe = new PEReader(stream);
        var entries = pe.ReadDebugDirectory().Where(entry => entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb).ToArray();
        if (entries.Length != 1)
            throw new InvalidDataException("Adapter must contain exactly one embedded portable PDB with its compiled sources.");
        using var provider = pe.ReadEmbeddedPortablePdbDebugDirectoryData(entries[0]);
        var reader = provider.GetMetadataReader();
        var result = new List<Source>();
        string blobs = Path.Combine(evidence, "sources");
        Directory.CreateDirectory(blobs);
        foreach (DocumentHandle handle in reader.Documents)
        {
            var document = reader.GetDocument(handle);
            string name = reader.GetString(document.Name);
            Guid algorithm = reader.GetGuid(document.HashAlgorithm);
            byte[] checksum = reader.GetBlobBytes(document.Hash);
            var embedded = reader.GetCustomDebugInformation(handle).Select(reader.GetCustomDebugInformation)
                .Where(info => reader.GetGuid(info.Kind) == EmbeddedSource).ToArray();
            if (embedded.Length != 1 || checksum.Length == 0)
                throw new InvalidDataException("Adapter source has no embedded bytes or checksum: " + name);
            byte[] data = reader.GetBlobBytes(embedded[0].Value);
            if (data.Length < sizeof(int)) throw new InvalidDataException("Invalid embedded adapter source: " + name);
            int length = BinaryPrimitives.ReadInt32LittleEndian(data);
            if (length < 0 || length > 32 * 1024 * 1024)
                throw new InvalidDataException("Embedded adapter source has an invalid length: " + name);
            byte[] source;
            if (length == 0) source = data[sizeof(int)..];
            else
            {
                using var compressed = new MemoryStream(data, sizeof(int), data.Length - sizeof(int));
                using var inflate = new DeflateStream(compressed, CompressionMode.Decompress);
                using var copy = new MemoryStream();
                inflate.CopyTo(copy);
                source = copy.ToArray();
                if (source.Length != length) throw new InvalidDataException("Embedded adapter source length differs from its PDB: " + name);
            }
            if (source.Length > 32 * 1024 * 1024)
                throw new InvalidDataException("Embedded adapter source is too large to retain: " + name);
            byte[] actual = algorithm == Sha256 ? SHA256.HashData(source) :
                algorithm == Sha1 ? SHA1.HashData(source) : throw new InvalidDataException("Unsupported adapter source checksum: " + name);
            if (!actual.SequenceEqual(checksum))
                throw new InvalidDataException("Embedded adapter source disagrees with its compiler checksum: " + name);
            string hash = Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();
            string retained = Path.Combine(blobs, hash + ".cs");
            if (!File.Exists(retained)) File.WriteAllBytes(retained, source);
            else if (!File.ReadAllBytes(retained).SequenceEqual(source))
                throw new InvalidDataException("Adapter source hash collision: " + name);
            result.Add(new Source(name, algorithm.ToString(), Convert.ToHexString(checksum).ToLowerInvariant(), hash, retained));
        }
        if (result.Count == 0) throw new InvalidDataException("Adapter PDB lists no compiled source documents.");
        return result.OrderBy(source => source.Document, StringComparer.Ordinal).ToArray();
    }

    private static string Digest(IEnumerable<string> hashes)
    {
        string canonical = string.Join("\n", hashes.Order(StringComparer.Ordinal)) + "\n";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static void Retain(string source, string destination, string hash)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (!File.Exists(destination)) File.Copy(source, destination);
        if (FileHash.Sha256(destination) != hash)
            throw new InvalidDataException("Adapter build input changed while it was being retained: " + source);
    }

    private static ProcessStartInfo Start(string verb, string project, string packages, string http)
    {
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = Path.GetDirectoryName(project)!,
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.Environment["NUGET_PACKAGES"] = packages;
        start.Environment["NUGET_HTTP_CACHE_PATH"] = http;
        start.ArgumentList.Add(verb);
        return start;
    }

    private static async Task<string> RunAsync(ProcessStartInfo start, CancellationToken cancellation)
    {
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not inspect adapter build inputs with .NET MSBuild.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellation);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellation);
        try { await process.WaitForExitAsync(cancellation).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        string output = await stdout.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidDataException("Could not inspect adapter build inputs: " + (await stderr.ConfigureAwait(false)).Trim());
        return output;
    }
}
