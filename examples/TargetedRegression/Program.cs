using Valheim.Testing.Game;

// A targeted native regression: one owned client hosts one disposable fixture world with one mod under test, once per arm
// (parent and candidate builds). Scenario.cs holds the rounds and assertions; the manifest holds everything machine-specific.
//   preflight <manifest>                      stage and check every arm; never opens the game
//   run <manifest> <arm> <new-output-dir>     stage that arm, check it again, then run the scenario in the game
//   clean <manifest>                          delete the disposable install this tool created
if (args is not ([("preflight" or "clean"), _] or ["run", _, _, _]))
{
    Console.Error.WriteLine("Usage: targeted-regression preflight <manifest.json> | run <manifest.json> <arm> <new-output-directory> | clean <manifest.json>");
    return 2;
}
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, press) => { press.Cancel = true; cancel.Cancel(); };
try
{
    var environment = RegressionEnvironment.Read(args[1]);
    var regression = new TargetedRegression(environment, Scenario.Capabilities);
    switch (args[0])
    {
        case "preflight":
            foreach (var arm in regression.Preflight())
                foreach (string line in arm.Describe()) Console.WriteLine(line);
            Console.WriteLine("PREFLIGHT PASSED; nothing was launched. Review the arms' commits and hashes above before the run.");
            return 0;
        case "clean":
            TargetedRegression.Remove(environment);
            Console.WriteLine("REMOVED the disposable install " + environment.Install);
            return 0;
        default:
            var report = regression.Run(args[2], args[3], Scenario.Name, Scenario.Rounds, Scenario.Measure, cancel.Token);
            Console.WriteLine(report.Passed ? "PASS" : "FAIL");
            return report.Passed ? 0 : 1;
    }
}
catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException)
{
    // Refused before anything launched: the message names what to change.
    Console.Error.WriteLine("REFUSED: " + error.Message);
    return 3;
}
