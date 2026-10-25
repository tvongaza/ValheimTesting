using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;

/// <summary>Runs the same disposable server smoke twice, removing exactly one selected mod in the second arm.</summary>
internal static class ServerLoadComparison
{
    public static async Task<int> RunAsync(string[] args, Func<string[], Task<int>>? runArm = null,
        ShippedLoader.Rule? shippedLoader = null,
        Func<NativeDependencyRequest, NativeDependencyLock>? resolve = null,
        Func<string, NativeDependencyLock, string, CancellationToken, string?, Task<string>>? buildAdapter = null,
        Func<string[], ServerLoad.Seams, Task<int>>? runArmWithSeams = null,
        Action<EnvironmentInventory, EnvironmentRecipe, string, string?>? clientArchitecture = null)
    {
        // The pinned-loader rule extracts the real pack into this machine's data root: on for real arms, and for a test that passes one.
        shippedLoader ??= runArm == null && runArmWithSeams == null ? ShippedLoader.Choose : null;
        resolve ??= NativeDependencyResolver.Resolve;
        buildAdapter ??= SmokeAdapter.BuildAsync;
        using var cancel = new CancellationTokenSource();
        string? output = null;
        var progress = new ModInputStage.Progress();
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
            // The stage's own choice, taken once before either arm: the server and client, their loaders, the core, the output
            // guard and the ValheimCLI inputs. The dependency locks and both arms use it even if the inventory changes before launch.
            var stage = ModInputStage.Choose(new ModInputStage.Request(ModInputStage.Route.Dedicated, output, options, parsed.Switches,
                parsed.Mods, parsed.Roots, parsed.Optional, parsed.BuildDependencies) { Shipped = shippedLoader, ClientArchitecture = clientArchitecture, Progress = progress });
            _ = ModInputStage.VerifyAdmission(stage.Admission!);
            var server = stage.Server!;
            string serverInstall = server.Recipe.Install, serverCore = stage.Core;
            Task<int> RunArm(string name, string[] arm)
            {
                _ = ModInputStage.VerifyAdmission(stage.Admission!);
                if (runArm != null) return runArm(arm);
                var armSeams = new ServerLoad.Seams(Loader: ShippedLoader.Choose,
                    FrozenServerLoader: server.Shipped, SummaryLabel: name,
                    InitialHostingBuild: stage.HostingBuild, InitialWorldAdmission: stage.WorldAdmission);
                return runArmWithSeams == null
                    ? ServerLoad.RunAsync(arm, armSeams) : runArmWithSeams(arm, armSeams);
            }
            var roots = parsed.Roots.Select(Path.GetFullPath).ToList();
            var optional = parsed.Optional.ToList();
            var before = ModInputStage.Resolve(stage, mods, roots, optional, resolve);
            var after = ModInputStage.Resolve(stage, mods.Where(mod => !mod.Equals(removed, pathComparison)).ToList(), roots, optional, resolve);
            if (!before.Ready || !after.Ready)
            {
                throw new InvalidDataException("Dependency choices remain before launch: " +
                    (before.Ready ? "before ready" : "before [" + SmokeDependencyInputs.Gaps(before) + "]") + "; " +
                    (after.Ready ? "after ready" : "after [" + SmokeDependencyInputs.Gaps(after) + "]"));
            }
            before.RequireSameExceptRemovedMod(after, removed);

            // Both arms read one frozen copy of the chosen environments.
            _ = ModInputStage.VerifyAdmission(stage.Admission!);
            string frozenInventory = SmokeInputResolver.RecordSelected(stage.Inventory, Path.Combine(output, "selection"),
                stage.Actors.Select(actor => actor.Recipe));
            Directory.CreateDirectory(output);
            string adapter = options.TryGetValue("--adapter", out string? chosenAdapter)
                ? Path.GetFullPath(chosenAdapter)
                : await buildAdapter(serverInstall, before, output, cancel.Token, serverCore);

            // Both arms receive identical arguments and the same packaged fixture; only the selected DLL is omitted.
            // Pin the source installs and explicit assets as well as dependency files before launching either arm.
            var directoryInputs = new[] { stage.CliFiles }
                // A client on another machine has no install here to pin (its loader package is a local file either way).
                .Concat(stage.Actors.SelectMany(actor => new[] { stage.Inventory.Hosts[actor.Recipe.Host].Kind == "local" ? actor.Recipe.Install : null,
                    actor.LoaderRoot }.OfType<string>()))
                .Concat(parsed.PluginDirectories.Select(Path.GetFullPath))
                .Distinct(StringComparer.Ordinal)
                .ToDictionary(path => path, WorldFixture.Manifest, StringComparer.Ordinal);
            var fileInputs = new[] { stage.CliManifest, adapter, frozenInventory }
                .Concat(stage.Actors.Select(actor => actor.Loader).OfType<string>())
                .Concat(parsed.Configs.Concat(parsed.PluginFiles).Select(Path.GetFullPath))
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
                    if (key is "--server" or "--client" or "--inventory") continue;
                    Add(key, [key == "--output" ? Path.Combine(output, name) : value]);
                }
                Add("--inventory", [frozenInventory]);
                // Freeze the environment or shipped-package decision for both arms. No arm can silently
                // compile against one core and run against a different one.
                if (!options.ContainsKey("--loader-package") && server.Loader != null)
                    Add("--loader-package", [server.Loader]);
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
            int beforeResult = await RunArm("before", Arm("before", omit: false));
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
            int afterResult = await RunArm("after", Arm("after", omit: true));
            Console.WriteLine(beforeResult == 0 && afterResult == 0
                ? "SERVER_MODSET_AB_PASS: both arms loaded the same fixture with exactly one selected mod removed."
                : $"SERVER_MODSET_AB_FAIL: full set exit {beforeResult}, removed-mod set exit {afterResult}; inspect both private arm results before attributing the difference.");
            return beforeResult != 0 ? beforeResult : afterResult;
        }
        catch (Exception failure) when (failure is ArgumentException or IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException or System.Text.Json.JsonException or FormatException)
        {
            Console.Error.WriteLine("REFUSED: " + failure.Message);
            if (progress.OutputChecked) SmokeOutput.MarkRefused(output, "server-load-ab", [failure.Message]);
            return 3;
        }
        finally { Console.CancelKeyPress -= onCancel; }
    }
}
