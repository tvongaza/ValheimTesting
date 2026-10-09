using Valheim.Testing.Game;

/// <summary>Runs the same disposable server smoke twice, removing exactly one selected mod in the second arm.</summary>
internal static class ServerLoadComparison
{
    public static async Task<int> RunAsync(string[] args, Func<string[], Task<int>>? runArm = null)
    {
        runArm ??= arm => ServerLoad.RunAsync(arm);
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, press) => { press.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += onCancel;
        try
        {
            // Arguments as server-load reads them, without --remove-mod: one value per option, or a switch.
            int removeAt = Array.IndexOf(args, "--remove-mod");
            if (removeAt < 0 || removeAt + 1 >= args.Length || Array.IndexOf(args, "--remove-mod", removeAt + 1) >= 0)
                throw new ArgumentException("Specify exactly one --remove-mod.");
            var rest = args.Take(removeAt).Concat(args.Skip(removeAt + 2)).ToArray();
            if (!ServerLoad.TryRead(rest, out var parsed, out string error)) throw new ArgumentException(error);
            if (parsed!.Switches.Contains("--preflight-only")) throw new ArgumentException("--preflight-only runs no arm; preflight each arm with server-load instead.");
            if (parsed.Switches.Contains("--hold")) throw new ArgumentException("--hold cannot run in a comparison: both arms must finish before their results can be compared. Use server-load --hold for interactive inspection.");
            var options = parsed!.Options;
            if (!options.TryGetValue("--output", out string? outputOption)) throw new ArgumentException("Specify --output for the comparison's two arms.");
            string output = Path.GetFullPath(outputOption);
            if (Path.Exists(output)) throw new IOException("--output must be new; comparison evidence will not be overwritten: " + output);
            string removed = Path.GetFullPath(args[removeAt + 1]);
            var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var mods = parsed.Mods.Select(Path.GetFullPath).ToList();
            if (mods.Count < 2 || mods.Count(mod => mod.Equals(removed, pathComparison)) != 1)
                throw new ArgumentException("--remove-mod must name exactly one of at least two selected --mod DLLs.");
            // The server install both arms resolve against: given, or the inventory's server environment on this machine.
            string server = options.TryGetValue("--server", out string? serverOption) ? Path.GetFullPath(serverOption)
                : ServerLoad.Choose(parsed, Path.Combine(output, "choice")).Server.Install;
            var serverLoader = options.TryGetValue("--loader-package", out string? loaderFile)
                ? BepInExLoaderPackage.Read(loaderFile) : null;
            var clientLoader = options.TryGetValue("--client-loader-package", out string? clientLoaderFile)
                ? BepInExLoaderPackage.Read(clientLoaderFile) : null;
            var (cliManifest, cliFiles) = SmokeInputs.Cli(options);
            string[] protectedRoots = new[] { server, cliFiles, options.TryGetValue("--client", out string? client) ? Path.GetFullPath(client) : null,
                options.TryGetValue("--steam-userdata", out string? userdata) ? Path.GetFullPath(userdata) : null,
                serverLoader?.Root, clientLoader?.Root }.OfType<string>().ToArray();
            SmokeOutput.RefuseInside(output, protectedRoots);
            var roots = parsed.Roots.Select(Path.GetFullPath).ToList();
            var optional = parsed.Optional.ToList();
            var capabilities = parsed.ServerOnly ? ["valheim.session/state"]
                : new List<string> { "valheim.session/state", "valheim.session/join", "valheim.session/leave" };
            NativeDependencyRequest Request(List<string> selected) => new()
            {
                Mods = selected, SearchRoots = roots,
                GameManaged = Path.GetDirectoryName(InstallPins.GameAssembly(server))!,
                BepInExCore = Path.Combine(serverLoader?.Root ?? server, InstallPins.CoreDirectory),
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

            Directory.CreateDirectory(output);
            string adapter = options.TryGetValue("--adapter", out string? chosenAdapter)
                ? Path.GetFullPath(chosenAdapter)
                : await SmokeAdapter.BuildAsync(server, before, output, cancel.Token, Path.Combine(serverLoader?.Root ?? server, InstallPins.CoreDirectory));

            // Both arms receive identical arguments and the same packaged fixture; only the selected DLL is omitted.
            // Pin the source installs and explicit assets as well as dependency files before launching either arm.
            var directoryInputs = new[] { server, cliFiles }.Concat(new[] { serverLoader?.Root, clientLoader?.Root }.OfType<string>())
                .Concat(parsed.PluginDirectories.Select(Path.GetFullPath))
                .Concat(options.TryGetValue("--client", out string? clientInstall) ? [Path.GetFullPath(clientInstall)] : [])
                .Distinct(StringComparer.Ordinal)
                .ToDictionary(path => path, WorldFixture.Manifest, StringComparer.Ordinal);
            var fileInputs = new[] { cliManifest, adapter }
                .Concat(parsed.Configs.Concat(parsed.PluginFiles).Select(Path.GetFullPath))
                .Concat(new[] { "--loader-package", "--client-loader-package" }
                    .Where(options.ContainsKey).Select(key => Path.GetFullPath(options[key])))
                .Distinct(StringComparer.Ordinal)
                .ToDictionary(path => path, FileHash.Sha256, StringComparer.Ordinal);
            before.Write(Path.Combine(output, "before-dependencies.lock.json"));
            after.Write(Path.Combine(output, "after-dependencies.lock.json"));

            string[] Arm(string name, bool omit)
            {
                var arm = new List<string>();
                void Add(string key, IEnumerable<string> values)
                {
                    foreach (string value in values) { arm.Add(key); arm.Add(value); }
                }
                // Rebuild from the validated parse, not raw tokens: flags never consume the next option.
                foreach (var (key, value) in options)
                    Add(key, [key == "--output" ? Path.Combine(output, name) : value]);
                arm.AddRange(parsed.Switches);
                Add("--mod", parsed.Mods.Where(mod => !omit || !Path.GetFullPath(mod).Equals(removed, pathComparison)));
                Add("--search-root", parsed.Roots);
                Add("--config", parsed.Configs);
                Add("--plugin-file", parsed.PluginFiles);
                Add("--plugin-dir", parsed.PluginDirectories);
                Add("--optional-reference", parsed.Optional);
                // Each arm chooses its ValheimCLI the same way and prints where it came from; the set is pinned between arms below.
                if (!options.ContainsKey("--adapter")) Add("--adapter", [adapter]);
                return [.. arm];
            }
            int beforeResult = await runArm(Arm("before", omit: false));
            // A native failure is precisely the case where removing one mod can be informative. An input refusal
            // cannot establish a mod interaction, so do not launch another arm after one.
            if (beforeResult != 0 && beforeResult != 1) return beforeResult;
            NativeDependencyLock.ReadReady(Path.Combine(output, "before-dependencies.lock.json"));
            NativeDependencyLock.ReadReady(Path.Combine(output, "after-dependencies.lock.json"));
            foreach (var (path, manifest) in directoryInputs) WorldFixture.Verify(path, manifest);
            foreach (var (path, hash) in fileInputs)
                if (!FileHash.Sha256(path).Equals(hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("A fixed A/B input changed after the first arm: " + path);
            int afterResult = await runArm(Arm("after", omit: true));
            Console.WriteLine(beforeResult == 0 && afterResult == 0
                ? "SERVER_MODSET_AB_PASS: both arms loaded the same fixture with exactly one selected mod removed."
                : $"SERVER_MODSET_AB_FAIL: full set exit {beforeResult}, removed-mod set exit {afterResult}; inspect both private arm results before attributing the difference.");
            return beforeResult != 0 ? beforeResult : afterResult;
        }
        catch (Exception failure) when (failure is ArgumentException or IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException or System.Text.Json.JsonException or FormatException)
        {
            Console.Error.WriteLine("REFUSED: " + failure.Message);
            return 3;
        }
        finally { Console.CancelKeyPress -= onCancel; }
    }
}
