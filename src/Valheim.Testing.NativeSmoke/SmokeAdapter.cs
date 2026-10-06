using System.Diagnostics;
using System.Reflection;
using Valheim.Testing.Game;

/// <summary>Build the test-only session plugin against exactly the server game and ValheimCLI selected for this run.</summary>
internal static class SmokeAdapter
{
    internal static async Task<string> BuildAsync(string server, NativeDependencyLock dependencies, string output,
        CancellationToken cancellation, string? bepInExCore = null)
    {
        GameLaunch.DetectServer(server);
        var cliCore = dependencies.CliFiles.Where(file => dependencies.CliManifest.Files.Any(entry =>
            entry.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase) &&
            entry.Plugins.Contains("valheimCLI.valheimCLI", StringComparer.Ordinal))).ToArray();
        if (cliCore.Length != 1) throw new InvalidDataException("Build the session adapter against exactly one pinned ValheimCLI core.");
        bepInExCore ??= Path.Combine(server, InstallPins.CoreDirectory);
        string stage = Path.Combine(Path.GetTempPath(), "valheimtesting-smoke-adapter-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(stage);
            WriteSource(stage);
            string packages = Path.Combine(stage, "packages"), httpCache = Path.Combine(stage, "http-cache");
            Directory.CreateDirectory(packages);
            Directory.CreateDirectory(httpCache);
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = stage, UseShellExecute = false,
                RedirectStandardError = true, RedirectStandardOutput = true };
            start.Environment["NUGET_PACKAGES"] = packages;
            start.Environment["NUGET_HTTP_CACHE_PATH"] = httpCache;
            foreach (string argument in new[] { "restore", "NativeSmoke.SessionAdapter.csproj",
                "--configfile", "NuGet.Config", "-p:ValheimPath=" + server,
                "-p:BepInExCore=" + bepInExCore, "-p:CliDll=" + cliCore[0].File })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("The .NET SDK could not start to build the test-only session adapter.");
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellation);
            Task<string> stderr = process.StandardError.ReadToEndAsync(cancellation);
            try { await process.WaitForExitAsync(cancellation); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                throw;
            }
            if (process.ExitCode != 0)
                throw new InvalidOperationException("The test-only session adapter could not restore its reference assemblies from NuGet.org (exit " +
                    process.ExitCode + "): " + ((await stdout) + "\n" + (await stderr)).Trim());
            var build = new ProcessStartInfo("dotnet") { WorkingDirectory = stage, UseShellExecute = false,
                RedirectStandardError = true, RedirectStandardOutput = true };
            build.Environment["NUGET_PACKAGES"] = packages;
            build.Environment["NUGET_HTTP_CACHE_PATH"] = httpCache;
            foreach (string argument in new[] { "build", "NativeSmoke.SessionAdapter.csproj", "-c", "Release", "--no-restore",
                "-p:ValheimPath=" + server, "-p:BepInExCore=" + bepInExCore,
                "-p:CliDll=" + cliCore[0].File }) build.ArgumentList.Add(argument);
            using var compiler = Process.Start(build) ?? throw new InvalidOperationException("The .NET SDK could not compile the test-only session adapter.");
            Task<string> buildOutput = compiler.StandardOutput.ReadToEndAsync(cancellation);
            Task<string> buildError = compiler.StandardError.ReadToEndAsync(cancellation);
            try { await compiler.WaitForExitAsync(cancellation); }
            catch (OperationCanceledException)
            {
                if (!compiler.HasExited) compiler.Kill(entireProcessTree: true);
                await compiler.WaitForExitAsync(CancellationToken.None);
                throw;
            }
            if (compiler.ExitCode != 0)
                throw new InvalidOperationException("The test-only session adapter did not compile against the selected game and ValheimCLI (exit " +
                    compiler.ExitCode + "): " + ((await buildOutput) + "\n" + (await buildError)).Trim());
            string built = Path.Combine(stage, "bin", "Release", "net48", "NativeSmoke.SessionAdapter.dll");
            if (!File.Exists(built)) throw new IOException("The session adapter build succeeded but produced no DLL.");
            var metadata = PluginMetadata.Read(built);
            if (metadata.Plugins.Count != 1 || metadata.Plugins[0].Guid != NativeServerRuntime.SessionAdapterPluginGuid)
                throw new InvalidDataException("The built adapter does not declare the expected test-only plugin identity.");
            string project = Path.Combine(output, "adapter-source");
            Directory.CreateDirectory(project);
            foreach (string name in new[] { "Plugin.cs", "TestExtension.cs", "Members.cs", "Valheim.GameReferences.props",
                "Valheim.GameReferences.targets", "NativeSmoke.SessionAdapter.csproj", "NuGet.Config" })
                File.Copy(Path.Combine(stage, name), Path.Combine(project, name));
            string destination = Path.Combine(output, "adapter", "NativeSmoke.SessionAdapter.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(built, destination);
            return destination;
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
    }

    internal static void WriteSource(string directory)
    {
        Directory.CreateDirectory(directory);
        var assembly = typeof(SmokeAdapter).Assembly;
        foreach (string name in new[] { "Plugin.cs", "TestExtension.cs", "Members.cs", "Valheim.GameReferences.props",
            "Valheim.GameReferences.targets" })
        {
            using Stream stream = assembly.GetManifestResourceStream("NativeSmoke.Adapter." + name)
                ?? throw new InvalidDataException("The native-smoke tool is missing its adapter source " + name + ". Reinstall the complete tool package.");
            using var target = File.Create(Path.Combine(directory, name));
            stream.CopyTo(target);
        }
        File.WriteAllText(Path.Combine(directory, "NativeSmoke.SessionAdapter.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="Valheim.GameReferences.props" />
              <PropertyGroup>
                <TargetFramework>net48</TargetFramework><LangVersion>10</LangVersion><Nullable>enable</Nullable>
                <AssemblyName>NativeSmoke.SessionAdapter</AssemblyName><UseValheimCli>true</UseValheimCli>
              </PropertyGroup>
              <ItemGroup><PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies" Version="1.0.3" PrivateAssets="all" /></ItemGroup>
              <Import Project="Valheim.GameReferences.targets" />
            </Project>
            """);
        File.WriteAllText(Path.Combine(directory, "NuGet.Config"),
            "<configuration><packageSources><clear/><add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\"/></packageSources></configuration>\n");
    }
}
