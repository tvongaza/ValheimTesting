using Valheim.Testing.Game;

/// <summary>Runs the same disposable server smoke twice, removing exactly one selected mod in the second arm.</summary>
public static class ServerLoadComparison
{
    public static async Task<int> RunAsync(string[] args, Func<string[], Task<int>>? runArm = null)
    {
        runArm ??= ServerLoad.RunAsync;
        try
        {
            if (args.Length % 2 != 0) throw new ArgumentException("Every option needs one value.");
            var pairs = Enumerable.Range(0, args.Length / 2)
                .Select(index => (Key: args[index * 2], Value: args[index * 2 + 1])).ToList();
            var plain = pairs.Where(pair => pair.Key != "--remove-mod")
                .SelectMany(pair => new[] { pair.Key, pair.Value }).ToArray();
            if (!ServerLoad.TryRead(plain, out var options, out _, out _, out _, out _, out _, out _, out string error))
                throw new ArgumentException(error);
            string One(string key) => pairs.Where(pair => pair.Key == key).Select(pair => pair.Value)
                .SingleOrDefault() ?? throw new ArgumentException("Specify exactly one " + key + ".");
            string output = Path.GetFullPath(One("--output"));
            if (Path.Exists(output)) throw new IOException("--output must be new; comparison evidence will not be overwritten: " + output);
            string removed = Path.GetFullPath(One("--remove-mod"));
            var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var mods = pairs.Where(pair => pair.Key == "--mod").Select(pair => Path.GetFullPath(pair.Value)).ToList();
            if (mods.Count < 2 || mods.Count(mod => mod.Equals(removed, pathComparison)) != 1)
                throw new ArgumentException("--remove-mod must name exactly one of at least two selected --mod DLLs.");
            string server = Path.GetFullPath(One("--server"));
            string cliManifest = Path.GetFullPath(One("--cli-manifest"));
            string cliFiles = Path.GetFullPath(One("--cli-files"));
            string[] protectedRoots = options!.TryGetValue("--client", out string? client)
                ? [server, cliFiles, Path.GetFullPath(client), Path.GetFullPath(options["--steam-userdata"])]
                : [server, cliFiles];
            SmokeOutput.RefuseInside(output, protectedRoots);
            var roots = pairs.Where(pair => pair.Key == "--search-root").Select(pair => Path.GetFullPath(pair.Value)).ToList();
            var optional = pairs.Where(pair => pair.Key == "--optional-reference").Select(pair => pair.Value).ToList();
            var capabilities = pairs.Any(pair => pair.Key == "--client")
                ? new List<string> { "valheim.session/state", "valheim.session/join", "valheim.session/leave" }
                : ["valheim.session/state"];
            NativeDependencyRequest Request(List<string> selected) => new()
            {
                Mods = selected, SearchRoots = roots,
                GameManaged = Path.GetDirectoryName(InstallPins.GameAssembly(server))!,
                BepInExCore = Path.Combine(server, InstallPins.CoreDirectory),
                CliManifest = cliManifest, CliFiles = cliFiles,
                Capabilities = capabilities, OptionalReferences = optional,
            };
            var before = NativeDependencyResolver.Resolve(Request(mods));
            var after = NativeDependencyResolver.Resolve(Request(mods.Where(mod => !mod.Equals(removed, pathComparison)).ToList()));
            if (!before.Ready || !after.Ready)
            {
                static string Gaps(NativeDependencyLock arm) => string.Join("; ", arm.Gaps.Select(gap =>
                    gap.Kind + " " + gap.Name + ": " + gap.Reason));
                throw new InvalidDataException("Dependency choices remain before launch: " +
                    (before.Ready ? "before ready" : "before [" + Gaps(before) + "]") + "; " +
                    (after.Ready ? "after ready" : "after [" + Gaps(after) + "]"));
            }
            before.RequireSameExceptRemovedMod(after, removed);

            // Both arms receive identical arguments and the same packaged fixture; only the selected DLL is omitted.
            // Pin the source installs and explicit assets as well as dependency files before launching either arm.
            var directoryInputs = new[] { server, cliFiles }
                .Concat(pairs.Where(pair => pair.Key is "--client" or "--plugin-dir")
                    .Select(pair => Path.GetFullPath(pair.Value)))
                .Distinct(StringComparer.Ordinal)
                .ToDictionary(path => path, WorldFixture.Manifest, StringComparer.Ordinal);
            var fileInputs = new[] { cliManifest, Path.GetFullPath(One("--adapter")) }
                .Concat(pairs.Where(pair => pair.Key is "--config" or "--plugin-file")
                    .Select(pair => Path.GetFullPath(pair.Value)))
                .Distinct(StringComparer.Ordinal)
                .ToDictionary(path => path, WorldFixture.Hash, StringComparer.Ordinal);
            Directory.CreateDirectory(output);
            before.Write(Path.Combine(output, "before-dependencies.lock.json"));
            after.Write(Path.Combine(output, "after-dependencies.lock.json"));

            string[] Arm(string name, bool omit) => pairs.Where(pair => pair.Key != "--remove-mod" &&
                    !(omit && pair.Key == "--mod" && Path.GetFullPath(pair.Value).Equals(removed, pathComparison)))
                .SelectMany(pair => pair.Key == "--output" ? new[] { pair.Key, Path.Combine(output, name) }
                    : new[] { pair.Key, pair.Value }).ToArray();
            int beforeResult = await runArm(Arm("before", omit: false));
            // A native failure is precisely the case where removing one mod can be informative. An input refusal
            // cannot establish a mod interaction, so do not launch another arm after one.
            if (beforeResult != 0 && beforeResult != 1) return beforeResult;
            NativeDependencyLock.ReadReady(Path.Combine(output, "before-dependencies.lock.json"));
            NativeDependencyLock.ReadReady(Path.Combine(output, "after-dependencies.lock.json"));
            foreach (var (path, manifest) in directoryInputs) WorldFixture.Verify(path, manifest);
            foreach (var (path, hash) in fileInputs)
                if (!WorldFixture.Hash(path).Equals(hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("A fixed A/B input changed after the first arm: " + path);
            int afterResult = await runArm(Arm("after", omit: true));
            Console.WriteLine(beforeResult == 0 && afterResult == 0
                ? "SERVER_MODSET_AB_PASS: both arms loaded the same fixture with exactly one selected mod removed."
                : $"SERVER_MODSET_AB_FAIL: full set exit {beforeResult}, removed-mod set exit {afterResult}; inspect both private arm results before attributing the difference.");
            return beforeResult != 0 ? beforeResult : afterResult;
        }
        catch (Exception failure) when (failure is ArgumentException or IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("REFUSED: " + failure.Message);
            return 3;
        }
    }
}
