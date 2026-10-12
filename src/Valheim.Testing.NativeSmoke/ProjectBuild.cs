using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Valheim.Testing.Game;

/// <summary>
/// The NuGet package and HTTP caches one .NET SDK invocation uses. A tool build shares the owned pair; a staged pair
/// makes a restore prove it can fetch from its feed alone, so a stale global cache cannot stand in.
/// </summary>
internal sealed record NuGetCaches(string Packages, string Http)
{
    /// <summary>
    /// The owned pair for tool builds: each variable's value when that folder is writable, else a folder under the
    /// ValheimTesting data root. A per-run cache would accumulate gigabytes in retained evidence.
    /// </summary>
    internal static NuGetCaches Owned() => new(Writable("NUGET_PACKAGES", "packages"), Writable("NUGET_HTTP_CACHE_PATH", "http"));

    /// <summary>A new pair under <paramref name="directory"/> that nothing else has filled.</summary>
    internal static NuGetCaches Staged(string directory)
    {
        var caches = new NuGetCaches(Path.Combine(directory, "packages"), Path.Combine(directory, "http-cache"));
        Directory.CreateDirectory(caches.Packages);
        Directory.CreateDirectory(caches.Http);
        return caches;
    }

    private static string Writable(string variable, string leaf)
    {
        string? chosen = Environment.GetEnvironmentVariable(variable);
        if (chosen != null && CanWrite(chosen)) return Path.GetFullPath(chosen);
        string owned = Path.Combine(CliBundle.DataRoot, "nuget", leaf);
        if (CanWrite(owned)) return owned;
        throw new IOException($"Neither {variable} nor the ValheimTesting-owned NuGet cache is writable: {owned}.");
    }

    private static bool CanWrite(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string probe = Path.Combine(directory, ".valheim-test-write-" + Guid.NewGuid().ToString("N"));
            using (File.Create(probe)) { }
            File.Delete(probe);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}

/// <summary>What a finished process wrote. The exit code is the caller's to judge, because each caller words its own refusal.</summary>
internal sealed record ProcessOutput(int ExitCode, string Output, string Error, ProjectQuery? Asked = null)
{
    /// <summary>Standard output, a newline, then standard error.</summary>
    internal string Combined => Output + "\n" + Error;

    /// <summary>The values the <see cref="ProjectQuery"/> given to the build asked for, read from standard output.</summary>
    internal QueryResult Query => QueryResult.Parse(Output, Asked ?? throw new InvalidOperationException("No project query was asked of this process."));
}

/// <summary>Item types and properties MSBuild reports as JSON (<c>-getItem</c>, <c>-getProperty</c>).</summary>
internal sealed record ProjectQuery(IReadOnlyList<string> Items, IReadOnlyList<string> Properties)
{
    internal ProjectQuery WithProperty(string property) => this with { Properties = [.. Properties, property] };
}

/// <summary>MSBuild's JSON answer to a <see cref="ProjectQuery"/>: each item's identity and metadata as strings.</summary>
internal sealed record QueryResult(IReadOnlyDictionary<string, string> Properties,
    IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string>>> Items)
{
    internal static QueryResult Parse(string json, ProjectQuery asked)
    {
        // MSBuild prints a single requested property as its bare value, and anything else as JSON.
        if (asked.Items.Count == 0 && asked.Properties.Count == 1)
            return new QueryResult(new Dictionary<string, string> { [asked.Properties[0]] = json.Trim() },
                new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string>>>());
        try
        {
            using var document = JsonDocument.Parse(json);
            var properties = new Dictionary<string, string>(StringComparer.Ordinal);
            if (document.RootElement.TryGetProperty("Properties", out var queried))
                foreach (var property in queried.EnumerateObject()) properties[property.Name] = property.Value.ToString();
            var items = new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string>>>(StringComparer.Ordinal);
            if (document.RootElement.TryGetProperty("Items", out var queriedItems))
                foreach (var type in queriedItems.EnumerateObject())
                    items[type.Name] = type.Value.EnumerateArray().Select(entry => (IReadOnlyDictionary<string, string>)
                        entry.EnumerateObject().ToDictionary(metadata => metadata.Name, metadata => metadata.Value.ToString(),
                            StringComparer.Ordinal)).ToArray();
            return new QueryResult(properties, items);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            throw new InvalidDataException("MSBuild did not report the queried build values: " + error.Message, error);
        }
    }
}

/// <summary>
/// The one place that starts the .NET SDK for a project: restore, build, evaluation, the owned NuGet caches,
/// output capture and cancellation that kills the whole process tree. A build can ask for item and property values in the
/// same invocation, so no caller builds a second time to read them.
/// </summary>
internal sealed class ProjectBuild(string project, IReadOnlyList<string> properties, NuGetCaches caches)
{
    internal string Project { get; } = Path.GetFullPath(project);
    internal NuGetCaches Caches { get; } = caches;
    private string WorkingDirectory => Path.GetDirectoryName(Project)!;

    /// <summary><c>dotnet restore</c> of the project with its properties and <paramref name="options"/>.</summary>
    internal Task<ProcessOutput> RestoreAsync(CancellationToken cancellation, IReadOnlyList<string>? options = null) =>
        RunAsync("restore", options ?? [], null, cancellation);

    /// <summary>
    /// <c>dotnet build -target:Build</c>. <paramref name="restore"/> false adds <c>--no-restore</c>. <paramref name="logFile"/>
    /// receives the build log (standard output then carries only the query). <paramref name="query"/> is answered after
    /// the build from the same invocation; read it with <see cref="ProcessOutput.Query"/> when the exit code is zero.
    /// </summary>
    internal Task<ProcessOutput> BuildAsync(CancellationToken cancellation, bool restore = true, string? logFile = null,
        IReadOnlyList<string>? options = null, ProjectQuery? query = null)
    {
        var arguments = new List<string> { "-target:Build" };
        if (!restore) arguments.Add("--no-restore");
        if (logFile != null)
        {
            if (logFile.Contains(';')) throw new ArgumentException("A build log path cannot contain ';': " + logFile);
            arguments.Add("-flp:logfile=" + logFile + ";verbosity=minimal");
        }
        arguments.AddRange(options ?? []);
        if (query != null) arguments.AddRange(QueryArguments(query));
        return BuildAndLogAsync(arguments, logFile, query, cancellation);
    }

    private async Task<ProcessOutput> BuildAndLogAsync(List<string> arguments, string? logFile, ProjectQuery? query, CancellationToken cancellation)
    {
        var run = await RunAsync("build", arguments, query, cancellation).ConfigureAwait(false);
        // Failures before MSBuild's file logger starts (a bad switch or property, SDK resolution) only reach the process's own streams.
        if (logFile != null)
            File.AppendAllText(logFile, "\n" + run.Error + (query == null || run.ExitCode != 0 ? "\n" + run.Output : ""));
        return run;
    }

    /// <summary>
    /// The project's evaluated values without running any target (no compile): the <paramref name="query"/> answer,
    /// readable with <see cref="ProcessOutput.Query"/>.
    /// </summary>
    internal Task<ProcessOutput> EvaluateAsync(ProjectQuery query, CancellationToken cancellation) =>
        RunAsync("msbuild", QueryArguments(query), query, cancellation);

    /// <summary>Writes the project and every import it evaluates, as one XML file, to <paramref name="destination"/> (no compile).</summary>
    internal Task<ProcessOutput> PreprocessAsync(string destination, CancellationToken cancellation) =>
        RunAsync("msbuild", ["-preprocess:" + destination], null, cancellation);

    private static IEnumerable<string> QueryArguments(ProjectQuery query) =>
        query.Items.Select(item => "-getItem:" + item).Concat(query.Properties.Select(property => "-getProperty:" + property));

    private async Task<ProcessOutput> RunAsync(string verb, IEnumerable<string> options, ProjectQuery? asked, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = WorkingDirectory };
        start.Environment["NUGET_PACKAGES"] = Caches.Packages;
        start.Environment["NUGET_HTTP_CACHE_PATH"] = Caches.Http;
        // Standard output carries the queried values, so the CLI must print no banner or notice there.
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "1";
        start.ArgumentList.Add(verb);
        start.ArgumentList.Add(Project);
        foreach (string argument in properties.Concat(options)) start.ArgumentList.Add(argument);
        return (await RunAsync(start, cancellation).ConfigureAwait(false)) with { Asked = asked };
    }

    /// <summary>
    /// Runs <paramref name="start"/> with its output captured. Cancelling kills the process and everything it started
    /// (a build leaves compiler and node processes behind otherwise) before the cancellation propagates.
    /// </summary>
    internal static async Task<ProcessOutput> RunAsync(ProcessStartInfo start, CancellationToken cancellation)
    {
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        cancellation.ThrowIfCancellationRequested();
        Process process;
        try { process = Process.Start(start) ?? throw new InvalidOperationException("Could not start " + start.FileName + "."); }
        catch (Win32Exception error)
        {
            throw new InvalidOperationException($"{start.FileName} could not start; install the .NET SDK for the project build.", error);
        }
        using (process)
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(cancellation).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception error) when (error is InvalidOperationException or AggregateException or Win32Exception or NotSupportedException) { } // exited meanwhile, or a descendant could not be killed
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            return new ProcessOutput(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
    }
}
