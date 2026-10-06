using Valheim.Testing.Game;

// A targeted native regression: one owned client hosts one disposable fixture world with one mod under test, once per arm
// (parent and candidate builds). Scenario.cs holds the rounds and assertions; regression.json the files to stage. The machine
// (the game install, where the disposable copy goes, the client's port) is this machine's Valheim, or the client environment
// an inventory names (--inventory environments.json, --client-env NAME).
//   preflight <regression.json> [machine]                   stage and check every arm; never opens the game
//   run <regression.json> <arm> <new-output-dir> [machine]  stage that arm, check it again, then run the scenario in the game
//   clean <regression.json> [machine]                       delete the disposable install this tool created
int positional = args is ["run", ..] ? 4 : 2;
var machine = new Dictionary<string, string>();
bool usage = args.Length < positional || args[0] is not ("preflight" or "run" or "clean");
for (int i = positional; !usage && i < args.Length; i += 2)
    usage = i + 1 >= args.Length || args[i] is not ("--inventory" or "--client-env") || !machine.TryAdd(args[i], args[i + 1]);
if (usage)
{
    Console.Error.WriteLine("Usage: targeted-regression preflight <regression.json> | run <regression.json> <arm> <new-output-directory> | clean <regression.json>, " +
        "each optionally followed by --inventory <environments.json> and --client-env <name>");
    return 2;
}
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, press) => { press.Cancel = true; cancel.Cancel(); };
try
{
    // No --inventory: the environments.json beside regression.json when there is one (valheim-test start writes it when its run
    // overrode this machine's client), as TargetedRegression.Read does; otherwise this machine.
    string beside = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1]))!, "environments.json");
    var inventory = EnvironmentInventory.Read(machine.GetValueOrDefault("--inventory") ?? (File.Exists(beside) ? beside : null));
    var regression = new TargetedRegression(RegressionInputs.Read(args[1]), Scenario.Capabilities, inventory, machine.GetValueOrDefault("--client-env"));
    Console.WriteLine($"client environment {regression.ClientEnvironment}: game {regression.Game}, disposable install {regression.Install}");
    switch (args[0])
    {
        case "preflight":
            foreach (var arm in regression.Preflight())
                foreach (string line in arm.Describe()) Console.WriteLine(line);
            Console.WriteLine("PREFLIGHT PASSED; nothing was launched. Review the arms' commits and hashes above before the run.");
            return 0;
        case "clean":
            regression.Remove();
            Console.WriteLine("REMOVED the disposable install " + regression.Install);
            return 0;
        default:
            var report = regression.Run(args[2], args[3], Scenario.Name, Scenario.Rounds, Scenario.Measure, cancel.Token);
            Console.WriteLine(report.Passed ? "PASS" : "FAIL");
            return report.Passed ? 0 : 1;
    }
}
catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or System.Text.Json.JsonException)
{
    // Refused before anything launched: the message names what to change.
    Console.Error.WriteLine("REFUSED: " + error.Message);
    return 3;
}
