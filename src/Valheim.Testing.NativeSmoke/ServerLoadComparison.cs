using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;

/// <summary>Runs the same disposable server smoke twice, removing exactly one selected mod in the second arm.</summary>
internal static class ServerLoadComparison
{
    public static async Task<int> RunAsync(string[] args, Func<string[], Task<int>>? runArm = null,
        Func<string, string, ShippedLoader.Choice?>? shippedLoader = null,
        Func<NativeDependencyRequest, NativeDependencyLock>? resolve = null,
        Func<string, NativeDependencyLock, string, CancellationToken, string?, Task<string>>? buildAdapter = null,
        Func<string[], ServerLoad.Seams, Task<int>>? runArmWithSeams = null,
        bool freezeInputs = false)
    {
        // Real arms always use one frozen environment. A scripted arm may opt in to
        // exercise that path without launching the game.
        freezeInputs |= runArm == null && runArmWithSeams == null;
        shippedLoader ??= ShippedLoader.Instead;
        resolve ??= NativeDependencyResolver.Resolve;
        buildAdapter ??= SmokeAdapter.BuildAsync;
        using var cancel = new CancellationTokenSource();
        string? output = null;
        bool outputChecked = false;
        ConsoleCancelEventHandler onCancel = (_, press) => { press.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += onCancel;
        try
        {
            if (!SmokeCommandOptions.TryRead(args, SmokeCommandOptions.Command.ServerLoadAb,
                allowImplicitMod: false, out var read, out string error)) throw new ArgumentException(error);
            string removed = Path.GetFullPath(read!.Options["--remove-mod"]);
            read.Options.Remove("--remove-mod");
            var parsed = ServerLoad.FromParsed(read);
            var options = parsed!.Options;
            output = SmokeCommandOptions.Output(options);
            var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var mods = parsed.Mods.Select(Path.GetFullPath).ToList();
            if (mods.Count < 2 || mods.Count(mod => mod.Equals(removed, pathComparison)) != 1)
                throw new ArgumentException("--remove-mod must name exactly one of at least two selected --mod DLLs.");
            // The server install both arms resolve against: given, or the inventory's server environment on this machine.
            // Freeze the first environment decision. The dependency locks and both arms
            // must use the same choice even if the inventory changes before launch.
            var selected = freezeInputs || !options.ContainsKey("--server")
                ? ServerLoad.Choose(parsed, Path.Combine(output, "choice")) : null;
            var serverRecipe = selected?.Server ?? new EnvironmentRecipe { Install = Path.GetFullPath(options["--server"]) };
            string server = serverRecipe.Install;
            var (loaderManifest, automaticLoader) = ServerLoad.SelectServerLoader(parsed, serverRecipe, shippedLoader);
            var armSeams = new ServerLoad.Seams(Loader: ShippedLoader.Instead, FrozenServerLoader: automaticLoader);
            runArm ??= arm => runArmWithSeams == null
                ? ServerLoad.RunAsync(arm, armSeams) : runArmWithSeams(arm, armSeams);
            var serverLoader = loaderManifest == null ? null : BepInExLoaderPackage.Read(loaderManifest);
            string serverCore = Path.Combine(serverLoader?.Root ?? server, InstallPins.CoreDirectory);
            var clientLoader = options.TryGetValue("--client-loader-package", out string? clientLoaderFile)
                ? BepInExLoaderPackage.Read(clientLoaderFile) : null;
            var (cliManifest, cliFiles) = SmokeInputs.Cli(options);
            var clientRecipe = selected?.Client ?? (options.TryGetValue("--client", out string? client)
                ? new EnvironmentRecipe { Install = Path.GetFullPath(client) } : null);
            SmokeOutput.RefuseResolved(output, cliFiles, selected?.Inventory, [serverRecipe, clientRecipe],
                serverLoader?.Root, clientLoader?.Root);
            outputChecked = true;
            var roots = parsed.Roots.Select(Path.GetFullPath).ToList();
            var optional = parsed.Optional.ToList();
            var capabilities = parsed.ServerOnly ? ["valheim.session/state"]
                : new List<string> { "valheim.session/state", "valheim.session/join", "valheim.session/leave" };
            NativeDependencyRequest Request(List<string> selected) => SmokeDependencyInputs.Request(selected, server,
                serverCore, cliManifest, cliFiles, roots, optional, capabilities);
            var before = resolve(Request(mods));
            var after = resolve(Request(mods.Where(mod => !mod.Equals(removed, pathComparison)).ToList()));
            if (!before.Ready || !after.Ready)
            {
                throw new InvalidDataException("Dependency choices remain before launch: " +
                    (before.Ready ? "before ready" : "before [" + SmokeDependencyInputs.Gaps(before) + "]") + "; " +
                    (after.Ready ? "after ready" : "after [" + SmokeDependencyInputs.Gaps(after) + "]"));
            }
            before.RequireSameExceptRemovedMod(after, removed);

            string? frozenInventory = null;
            if (freezeInputs)
            {
                frozenInventory = SmokeInputResolver.RecordSelected(selected!.Inventory,
                    Path.Combine(output, "selection"),
                    new[] { selected.Server, selected.Client }.OfType<EnvironmentRecipe>());
            }
            Directory.CreateDirectory(output);
            string adapter = options.TryGetValue("--adapter", out string? chosenAdapter)
                ? Path.GetFullPath(chosenAdapter)
                : await buildAdapter(server, before, output, cancel.Token, serverCore);

            // Both arms receive identical arguments and the same packaged fixture; only the selected DLL is omitted.
            // Pin the source installs and explicit assets as well as dependency files before launching either arm.
            var directoryInputs = new[] { server, cliFiles }.Concat(new[] { serverLoader?.Root, clientLoader?.Root }.OfType<string>())
                .Concat(parsed.PluginDirectories.Select(Path.GetFullPath))
                .Concat(options.TryGetValue("--client", out string? clientInstall) ? [Path.GetFullPath(clientInstall)] : [])
                .Distinct(StringComparer.Ordinal)
                .ToDictionary(path => path, WorldFixture.Manifest, StringComparer.Ordinal);
            var fileInputs = new[] { cliManifest, adapter, loaderManifest }.OfType<string>()
                .Concat(frozenInventory == null ? [] : [frozenInventory])
                .Concat(parsed.Configs.Concat(parsed.PluginFiles).Select(Path.GetFullPath))
                .Concat(new[] { "--client-loader-package" }
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
                {
                    if (frozenInventory != null && key is "--server" or "--client" or "--inventory") continue;
                    Add(key, [key == "--output" ? Path.Combine(output, name) : value]);
                }
                if (frozenInventory != null) Add("--inventory", [frozenInventory]);
                // Freeze the environment or shipped-package decision for both arms. No arm can silently
                // compile against one core and run against a different one.
                if (!options.ContainsKey("--loader-package") && loaderManifest != null)
                    Add("--loader-package", [loaderManifest]);
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
            if (beforeResult != 0 && beforeResult != 1)
            {
                SmokeOutput.MarkRefused(output, "server-load-ab", ["The first arm refused; the comparison did not complete."]);
                return beforeResult;
            }
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
            if (outputChecked) SmokeOutput.MarkRefused(output, "server-load-ab", [failure.Message]);
            return 3;
        }
        finally { Console.CancelKeyPress -= onCancel; }
    }
}
