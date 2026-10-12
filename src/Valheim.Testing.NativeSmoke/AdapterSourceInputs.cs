using System.Buffers.Binary;
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
    private static readonly string[] OtherItemTypes = ["AdditionalFiles", "EditorConfigFiles", "EmbeddedResource", "Analyzer", "ReferencePath", "_ResolvedProjectReferencePaths"];
    // These evaluated properties affect the compiler without necessarily changing any source file.
    private static readonly string[] CompilerProperties = ["DefineConstants", "LangVersion", "Nullable", "AllowUnsafeBlocks",
        "CheckForOverflowUnderflow", "Optimize", "Deterministic", "Features", "WarningLevel", "NoWarn",
        "TreatWarningsAsErrors", "WarningsAsErrors", "WarningsNotAsErrors", "PlatformTarget", "Prefer32Bit",
        "AssemblyName", "RootNamespace", "OutputType", "DebugType", "GenerateDocumentationFile"];
    private static readonly Guid Sha1 = new("ff1816ec-aa5e-4d10-87f7-6f4963833460");
    private static readonly Guid Sha256 = new("8829d00f-11b8-4213-878b-770e8597ac16");
    private static readonly Guid EmbeddedSource = new("0e8a571b-6926-466e-b4ad-8ab04611f5fe");

    internal sealed record Input(string Kind, string Path, string Metadata, string Sha256, string? Retained, bool TestedBinary);
    internal sealed record Source(string Document, string HashAlgorithm, string Checksum, string Sha256, string Retained);
    internal sealed record Record(string Project, string Adapter, string AdapterSha256, string SourceInputSha256,
        string CompiledSourceSha256, string EvaluatedBuildSha256, IReadOnlyList<Input> OtherInputs,
        IReadOnlyList<Source> Sources);

    /// <summary>
    /// What the adapter's single build is asked for alongside compiling: the other item types and the compiler-affecting
    /// properties, so reading the inputs never needs a second build.
    /// </summary>
    internal static readonly ProjectQuery Query = new(OtherItemTypes, ["ProjectAssetsFile", "BaseIntermediateOutputPath", .. CompilerProperties]);

    internal sealed class Snapshot(ProjectBuild build, string output, string evaluatedHash, IReadOnlyList<Input> inputs,
        IReadOnlySet<string> testedBinaryHashes)
    {
        internal ProjectBuild Build { get; } = build;
        internal string Project => Build.Project;
        internal string Output { get; } = output;
        internal string EvaluatedHash { get; } = evaluatedHash;
        internal IReadOnlyList<Input> Inputs { get; } = inputs;
        internal IReadOnlySet<string> TestedBinaryHashes { get; } = testedBinaryHashes;
    }

    /// <summary>
    /// Before the build: evaluate the (restored) project without running any target, and retain every build input that
    /// exists already (the project, its imports, the assets file and the items that are declared rather than generated).
    /// </summary>
    internal static async Task<Snapshot> BeginAsync(ProjectBuild build, string output,
        IReadOnlyCollection<string>? testedBinaries, CancellationToken cancellation)
    {
        // Resolve by content: MSBuild may canonicalize a parent symlink (for example /tmp on macOS)
        // when it reports ReferencePath, while the selected mod was pinned under the alias.
        var tested = new HashSet<string>((testedBinaries ?? []).Select(FileHash.Sha256), StringComparer.Ordinal);
        string evidence = Path.Combine(output, "source-inputs");
        Directory.CreateDirectory(evidence);
        string evaluated = Path.Combine(evidence, "evaluated-before.xml");
        await PreprocessAsync(build, evaluated, cancellation).ConfigureAwait(false);
        var answers = Require(await build.EvaluateAsync(Query, cancellation).ConfigureAwait(false)).Query;
        // Generated inputs (editorconfig, resolved references) do not exist yet; the build's own answer lists them.
        var inputs = ReadInputs(build.Project, evaluated, evidence, answers, tested, requireFiles: false);
        foreach (var input in inputs)
            if (input.Retained != null) Retain(input.Path, input.Retained, input.Sha256);
        return new Snapshot(build, output, FileHash.Sha256(evaluated), inputs, tested);
    }

    /// <summary>
    /// After the build: <paramref name="built"/> is the answer the build itself gave to <see cref="Query"/>. Every input
    /// the build used that the project declared before the build began (outside its own intermediate folder) must still hash
    /// as it did, and the evaluated settings must be unchanged. Inputs only the build itself produces or resolves, such as
    /// reference assemblies, are recorded as the build left them.
    /// </summary>
    internal static async Task<Record> FinishAsync(Snapshot before, QueryResult built, string adapter, CancellationToken cancellation)
    {
        string evidence = Path.Combine(before.Output, "source-inputs");
        string evaluated = Path.Combine(evidence, "evaluated-after.xml");
        await PreprocessAsync(before.Build, evaluated, cancellation).ConfigureAwait(false);
        if (FileHash.Sha256(evaluated) != before.EvaluatedHash)
            throw new InvalidDataException("The adapter's imported build settings changed during compilation; rebuild it without editing the project or its imports.");
        var after = ReadInputs(before.Project, evaluated, evidence, built, before.TestedBinaryHashes, requireFiles: true);
        File.Delete(evaluated);
        // The change-during-build check: whatever the build reported that was already there when it began must hash as it did.
        // The project's intermediate folder is the build's own: a target there may legitimately rewrite a declared input.
        string projectDirectory = Path.GetDirectoryName(before.Project)!;
        string intermediate = Path.GetFullPath(built.Properties.GetValueOrDefault("BaseIntermediateOutputPath", "obj")
            .Replace('\\', Path.DirectorySeparatorChar), projectDirectory);
        // MSBuild may report the same folder under a canonical path (a symlinked parent), so the assets file's folder counts too.
        string? assetsFolder = after.Where(input => input.Kind == "ProjectAssetsFile").Select(input => Path.GetDirectoryName(input.Path)).FirstOrDefault();
        bool Under(string folder, string path) => Path.GetRelativePath(folder, path) is var relative &&
            relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
        // The assets file is a restore result the build must not change, so only other files there may be rewritten.
        bool Generated(Input input) => input.Kind != "ProjectAssetsFile" && (Under(intermediate, input.Path) ||
            (assetsFolder != null && Under(assetsFolder, input.Path)));
        var began = before.Inputs.Where(input => input.Kind != "CompilerProperty").ToLookup(input => input.Path, StringComparer.Ordinal);
        foreach (var input in after.Where(input => input.Kind != "CompilerProperty" && !Generated(input)))
            if (began[input.Path].Any(earlier => earlier.Sha256 != input.Sha256))
                throw new InvalidDataException("An adapter build input changed during compilation; rebuild it without editing its resources, analyzer files or project.");
        foreach (var input in after)
            if (input.Retained != null) Retain(input.Path, input.Retained, input.Sha256);

        IReadOnlyList<Source> sources = RetainCompiledSources(adapter, evidence);
        string compiled = Digest(sources.Select(source => "source|" + CanonicalPath(source.Document, before.Project, before.Output) + "|" + source.Sha256));
        // Raw project/import files and compiler-produced source are stable across A/B arms even when ModDll differs.
        // The expanded MSBuild settings and exact command-line properties are recorded separately in inputs.json.
        string sourceInput = Digest(sources.Select(source => "source|" + CanonicalPath(source.Document, before.Project, before.Output) + "|" + source.Sha256)
            .Concat(after.Where(input => !input.TestedBinary).Select(input =>
                input.Kind + "|" + (input.Kind == "CompilerProperty" ? input.Path : CanonicalPath(input.Path, before.Project, before.Output)) +
                "|" + input.Metadata + "|" + input.Sha256)));
        return new Record(before.Project, adapter, FileHash.Sha256(adapter), sourceInput, compiled,
            before.EvaluatedHash, after, sources);
    }

    private static async Task PreprocessAsync(ProjectBuild build, string destination, CancellationToken cancellation)
    {
        Require(await build.PreprocessAsync(destination, cancellation).ConfigureAwait(false));
        if (!File.Exists(destination) || new FileInfo(destination).Length == 0)
            throw new InvalidDataException("MSBuild did not produce the adapter's evaluated project and imports.");
    }

    private static ProcessOutput Require(ProcessOutput run) => run.ExitCode == 0 ? run
        : throw new InvalidDataException("Could not inspect adapter build inputs: " + run.Error.Trim());

    private static IReadOnlyList<Input> ReadInputs(string project, string evaluated, string evidence, QueryResult answers,
        IReadOnlySet<string> testedBinaryHashes, bool requireFiles)
    {
        var paths = new SortedSet<(string Kind, string Path, string Metadata)>(Comparer<(string Kind, string Path, string Metadata)>.Create((a, b) =>
        {
            int kind = StringComparer.Ordinal.Compare(a.Kind, b.Kind);
            if (kind != 0) return kind;
            int path = StringComparer.Ordinal.Compare(a.Path, b.Path);
            return path == 0 ? StringComparer.Ordinal.Compare(a.Metadata, b.Metadata) : path;
        }));
        paths.Add(("Project", Path.GetFullPath(project), ""));
        if (!answers.Properties.TryGetValue("ProjectAssetsFile", out string? assetsFile) || string.IsNullOrWhiteSpace(assetsFile))
            throw new InvalidDataException("MSBuild did not identify the restored adapter package assets file.");
        paths.Add(("ProjectAssetsFile", Path.GetFullPath(Path.IsPathFullyQualified(assetsFile) ? assetsFile :
            Path.Combine(Path.GetDirectoryName(project)!, assetsFile)), ""));
        // MSBuild's preprocessed file wraps every imported file in a banner ending with an equals line.
        // Imports may use any extension (or a UNC path), so recognize the banner rather than a suffix.
        foreach (Match match in Regex.Matches(File.ReadAllText(evaluated),
            @"(?m)^[ \t]*(?<path>(?:[A-Za-z]:[\\/]|/|\\\\)[^\r\n<>]+?)[ \t]*\r?\n[ \t]*={20,}[ \t]*\r?$"))
        {
            string path = Path.GetFullPath(match.Groups["path"].Value);
            if (!File.Exists(path))
                throw new InvalidDataException("An imported adapter build setting cannot be retained: " + path);
            paths.Add(("Import", path, ""));
        }
        foreach (string item in OtherItemTypes)
            foreach (var entry in answers.Items.GetValueOrDefault(item) ?? [])
            {
                if (!entry.TryGetValue("FullPath", out string? full) || string.IsNullOrWhiteSpace(full))
                    throw new InvalidDataException("MSBuild returned an unresolved adapter " + item + " item.");
                paths.Add((item, Path.GetFullPath(full), ItemMetadata(entry)));
            }
        var fileInputs = paths.Where(entry => requireFiles || File.Exists(entry.Path)).Select(entry =>
        {
            string path = entry.Path;
            if (!File.Exists(path)) throw new InvalidDataException("Adapter build input is missing: " + path);
            string hash = FileHash.Sha256(path);
            bool tested = testedBinaryHashes.Contains(hash);
            return new Input(entry.Kind, path, entry.Metadata, hash,
                tested ? null : Path.Combine(evidence, "inputs", hash + Path.GetExtension(path)), tested);
        });
        var compilerInputs = CompilerProperties.Select(name =>
        {
            string value = answers.Properties.GetValueOrDefault(name, "");
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
            return new Input("CompilerProperty", name, value, hash, null, false);
        });
        return fileInputs.Concat(compilerInputs).ToArray();
    }

    private static string ItemMetadata(IReadOnlyDictionary<string, string> entry)
    {
        string[] names = ["LogicalName", "ManifestResourceName", "Link", "TargetPath", "Aliases", "Culture", "Generator", "DependentUpon"];
        return string.Join(";", names.Select(name => name + "=" + JsonSerializer.Serialize(entry.GetValueOrDefault(name, ""))));
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

    private static string CanonicalPath(string path, string project, string output)
    {
        string full = Path.GetFullPath(path);
        foreach ((string label, string root) in new[] { ("project", Path.GetDirectoryName(project)!), ("output", output) })
        {
            string relative = Path.GetRelativePath(root, full);
            if (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !Path.IsPathRooted(relative))
                return label + "/" + relative.Replace('\\', '/');
        }
        return "absolute/" + full.Replace('\\', '/');
    }

    private static void Retain(string source, string destination, string hash)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (!File.Exists(destination)) File.Copy(source, destination);
        if (FileHash.Sha256(destination) != hash)
            throw new InvalidDataException("Adapter build input changed while it was being retained: " + source);
    }
}
