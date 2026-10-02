// Inspect explicit local mod/CLI roots and write a private, editable dependency lock. Nothing is downloaded or launched.
//
//   dotnet run scripts/native-dependencies.cs -- resolve request.json dependency-lock.json
//   dotnet run scripts/native-dependencies.cs -- check dependency-lock.json
//
// A resolve with unresolved choices writes the lock for review and exits 2. Edit request.json (roots or confirmed
// optionalReferences), then resolve again. The check command verifies the pinned files without rediscovery.
#:project ../src/Valheim.Testing.Game/Valheim.Testing.Game.csproj
using Valheim.Testing.Game;

try
{
if (args is ["resolve", string requestFile, string output])
{
    var request = NativeDependencyRequest.Read(requestFile);
    var plan = NativeDependencyResolver.Resolve(request);
    plan.Write(output);
    Console.WriteLine($"Pinned {plan.Mods.Count} mod(s), {plan.Plugins.Count} dependency DLL(s) and {plan.CliFiles.Count} ValheimCLI file(s) in {output}.");
    foreach (var file in plan.Plugins) Console.WriteLine($"  {Path.GetFileName(file.File)}: {file.Reason}");
    foreach (var candidate in plan.OptionalCandidates) Console.WriteLine($"  soft integration candidate: {candidate} (not staged)");
    foreach (var gap in plan.Gaps)
    {
        Console.Error.WriteLine($"  {gap.Kind} {gap.Name}: {gap.Reason}");
        foreach (string candidate in gap.Candidates) Console.Error.WriteLine($"    candidate: {candidate}");
    }
    return plan.Ready ? 0 : 2;
}
if (args is ["check", string lockFile])
{
    var plan = NativeDependencyLock.ReadReady(lockFile);
    Console.WriteLine($"PASS: {lockFile} pins {plan.Mods.Count} mod(s), {plan.Plugins.Count} dependency DLL(s) and {plan.CliFiles.Count} ValheimCLI file(s).");
    return 0;
}
Console.Error.WriteLine("Usage: dotnet run scripts/native-dependencies.cs -- resolve request.json dependency-lock.json | check dependency-lock.json");
return 2;
}
catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException)
{
    Console.Error.WriteLine("Dependency setup: " + error.Message);
    return 2;
}
