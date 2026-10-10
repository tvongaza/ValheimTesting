using System.Reflection;
using System.Runtime.Loader;
using System.Diagnostics;
using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;

/// <summary>Loads exactly one mod-owned scenario while sharing the running toolkit's actor types.</summary>
internal static class OneShotScenario
{
    internal sealed record Selection(IOneShotServerScenario Runner, string File, string Sha256);
    internal sealed record HostedSelection(IOneShotHostedScenario Runner, string File, string Sha256);

    internal static async Task<string> BuildAsync(string project, string output, CancellationToken cancellation,
        IReadOnlyDictionary<string, string>? properties = null)
    {
        project = Path.GetFullPath(project); output = Path.GetFullPath(output);
        if (!File.Exists(project) || Path.GetExtension(project) != ".csproj")
            throw new FileNotFoundException("Project must name an existing .csproj: " + project, project);
        if (ProtectedPaths.Contains(Path.GetDirectoryName(project)!, output) || Path.Exists(output))
            throw new ArgumentException("Test-project build output must be a new private folder outside its project: " + output);
        string packages = ModBuild.WritableCache("NUGET_PACKAGES", "packages");
        string http = ModBuild.WritableCache("NUGET_HTTP_CACHE_PATH", "http");
        var arguments = new List<string> { project, "-p:Configuration=Release", "-p:OutputPath=" + Path.Combine(output, "bin"),
            "-p:CopyOutputDLLPath=" + Path.Combine(output, "private-deployment"), "-p:CopyOutputDLLPath2=", "-p:CopyOutputDLLPath3=" };
        foreach (var property in properties ?? new Dictionary<string, string>())
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(property.Key, "^[A-Za-z][A-Za-z0-9_]*$") ||
                property.Key is "OutputPath" or "CopyOutputDLLPath" or "CopyOutputDLLPath2" or "CopyOutputDLLPath3")
                throw new ArgumentException("Unsafe build property name: " + property.Key);
            arguments.Add("-p:" + property.Key + "=" + property.Value);
        }
        Directory.CreateDirectory(output);
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = Path.GetDirectoryName(project)!,
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.Environment["NUGET_PACKAGES"] = packages; start.Environment["NUGET_HTTP_CACHE_PATH"] = http;
        start.ArgumentList.Add("build");
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var build = Process.Start(start) ?? throw new InvalidOperationException("Could not start the .NET SDK for the test-project build.");
        Task<string> stdout = build.StandardOutput.ReadToEndAsync(cancellation);
        Task<string> stderr = build.StandardError.ReadToEndAsync(cancellation);
        try { await build.WaitForExitAsync(cancellation).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            if (!build.HasExited) build.Kill(entireProcessTree: true);
            await build.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        File.WriteAllText(Path.Combine(output, "build.log"), (await stdout.ConfigureAwait(false)) + "\n" +
            (await stderr.ConfigureAwait(false)));
        if (build.ExitCode != 0)
            throw new InvalidOperationException("The test project failed to build; see " + Path.Combine(output, "build.log"));
        var query = new ProcessStartInfo("dotnet") { WorkingDirectory = Path.GetDirectoryName(project)!,
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        query.Environment["NUGET_PACKAGES"] = packages; query.Environment["NUGET_HTTP_CACHE_PATH"] = http;
        query.ArgumentList.Add("msbuild");
        foreach (string argument in arguments) query.ArgumentList.Add(argument);
        query.ArgumentList.Add("-getProperty:TargetPath");
        using var target = Process.Start(query) ?? throw new InvalidOperationException("Could not inspect the scenario target path.");
        Task<string> path = target.StandardOutput.ReadToEndAsync(cancellation);
        Task<string> error = target.StandardError.ReadToEndAsync(cancellation);
        await target.WaitForExitAsync(cancellation).ConfigureAwait(false);
        string file = (await path.ConfigureAwait(false)).Trim();
        if (target.ExitCode != 0 || !Path.IsPathFullyQualified(file) || !File.Exists(file))
            throw new InvalidDataException("The test project produced no inspectable DLL: " + (await error.ConfigureAwait(false)).Trim());
        return file;
    }

    internal static Selection Load(string file, string output)
    {
        var (runner, hash) = LoadScenario<IOneShotServerScenario>(file, output);
        return new Selection(runner, Path.GetFullPath(file), hash);
    }

    internal static HostedSelection LoadHosted(string file, string output)
    {
        var (runner, hash) = LoadScenario<IOneShotHostedScenario>(file, output);
        return new HostedSelection(runner, Path.GetFullPath(file), hash);
    }

    private static (T Runner, string Hash) LoadScenario<T>(string file, string output) where T : class
    {
        file = Path.GetFullPath(file);
        if (!File.Exists(file) || Path.GetExtension(file) != ".dll")
            throw new FileNotFoundException("--scenario must name a built .NET 10 test DLL: " + file, file);
        string recorded = Path.Combine(output, "scenario");
        Directory.CreateDirectory(recorded);
        string copy = Path.Combine(recorded, Path.GetFileName(file));
        string hash = FileHash.Sha256(file);
        // The scenario may carry test-only libraries. Capture the exact adjacent build together,
        // so a later incremental build cannot change code while this run is using it.
        var pinned = new List<object>();
        foreach (string source in Directory.EnumerateFiles(Path.GetDirectoryName(file)!)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal))
        {
            string sourceHash = FileHash.Sha256(source);
            string staged = Path.Combine(recorded, Path.GetFileName(source));
            File.Copy(source, staged);
            if (FileHash.Sha256(staged) != sourceHash)
                throw new InvalidDataException("Scenario build changed while being prepared; build it again: " + source);
            pinned.Add(new { source, sha256 = sourceHash, staged });
        }
        if (!File.Exists(copy)) throw new InvalidDataException("The scenario DLL was not captured: " + file);
        if (FileHash.Sha256(copy) != hash)
            throw new InvalidDataException("Scenario DLL changed while being prepared; build it again: " + file);
        File.WriteAllText(Path.Combine(recorded, "source.json"), JsonSerializer.Serialize(new
        { source = file, sha256 = hash, staged = copy, files = pinned }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        // A test host or application can already have loaded this exact assembly. Reuse it there;
        // production scenario DLLs load from the pinned private copy with their own dependency resolver.
        Assembly assembly = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(loaded =>
            !loaded.IsDynamic && string.Equals(loaded.Location, file,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            ?? new ScenarioLoadContext(copy).LoadFromAssemblyPath(copy);
        Type[] candidates;
        try { candidates = assembly.GetTypes().Where(type => type.IsClass && !type.IsAbstract &&
            typeof(T).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) != null).ToArray(); }
        catch (ReflectionTypeLoadException error)
        {
            throw new InvalidDataException("Scenario dependencies could not be loaded: " +
                string.Join("; ", error.LoaderExceptions.OfType<Exception>().Select(failure => failure.Message)), error);
        }
        if (candidates.Length != 1)
            throw new InvalidDataException($"--scenario {file} must have exactly one public, constructible {typeof(T).Name}; found {candidates.Length}.");
        var scenario = (T)Activator.CreateInstance(candidates[0])!;
        string name = scenario switch
        {
            IOneShotServerScenario server => server.Name,
            IOneShotHostedScenario hosted => hosted.Name,
            _ => throw new InvalidDataException("Unsupported scenario type: " + typeof(T).Name),
        };
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
            throw new InvalidDataException("The scenario Name must contain 1 to 100 characters.");
        return (scenario, hash);
    }

    private sealed class ScenarioLoadContext(string source) : AssemblyLoadContext(isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(source);
        protected override Assembly? Load(AssemblyName name)
        {
            // The scenario and the tool must agree on these public actor and report types.
            if (name.Name is "Valheim.Testing.GameSessions" or "Valheim.Testing.Game" or "Valheim.Testing") return null;
            string? path = _resolver.ResolveAssemblyToPath(name);
            return path == null ? null : LoadFromAssemblyPath(path);
        }
    }
}
