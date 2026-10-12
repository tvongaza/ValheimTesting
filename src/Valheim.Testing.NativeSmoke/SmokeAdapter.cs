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
            // A staged cache pair proves the adapter restores from NuGet.org alone; no shared cache can stand in.
            var build = new ProjectBuild(Path.Combine(stage, "NativeSmoke.SessionAdapter.csproj"),
                ["-p:ValheimPath=" + server, "-p:BepInExCore=" + bepInExCore, "-p:CliDll=" + cliCore[0].File], NuGetCaches.Staged(stage));
            var restore = await build.RestoreAsync(cancellation, ["--configfile", "NuGet.Config"]);
            if (restore.ExitCode != 0)
                throw new InvalidOperationException("The test-only session adapter could not restore its reference assemblies from NuGet.org (exit " +
                    restore.ExitCode + "): " + restore.Combined.Trim());
            var compile = await build.BuildAsync(cancellation, restore: false, options: ["-c", "Release"]);
            if (compile.ExitCode != 0)
                throw new InvalidOperationException("The test-only session adapter did not compile against the selected game and ValheimCLI (exit " +
                    compile.ExitCode + "): " + compile.Combined.Trim());
            string built = Path.Combine(stage, "bin", "Release", "net48", "NativeSmoke.SessionAdapter.dll");
            if (!File.Exists(built)) throw new IOException("The session adapter build succeeded but produced no DLL.");
            var metadata = PluginMetadata.Read(built);
            if (metadata.Plugins.Count != 1 || metadata.Plugins[0].Guid != SmokeSessionContract.SessionAdapterPluginGuid)
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
              <Import Project="Valheim.GameReferences.targets" />
            </Project>
            """);
        File.WriteAllText(Path.Combine(directory, "NuGet.Config"),
            "<configuration><packageSources><clear/><add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\"/></packageSources></configuration>\n");
    }
}
