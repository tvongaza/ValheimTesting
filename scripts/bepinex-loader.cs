// Pin one already extracted BepInEx/UnityDoorstop package; never changes the package or a Valheim install.
//
//   dotnet run scripts/bepinex-loader.cs -- capture /path/to/extracted-pack BepInExPack_Valheim 5.4.2202 /private/loader.json
//   dotnet run scripts/bepinex-loader.cs -- check /private/loader.json
#:project ../src/Valheim.Testing.Game/Valheim.Testing.Game.csproj
using Valheim.Testing.Game;

try
{
    if (args is ["capture", string root, string name, string version, string output])
    {
        if (File.Exists(output)) throw new IOException($"{output} already exists; choose a new manifest so an existing reviewed package is not silently replaced.");
        var package = BepInExLoaderPackage.Capture(root, name, version);
        package.Write(output);
        Console.WriteLine($"Pinned {package.Identity}: {package.Files.Count} loader/core file(s) in {output}.");
        return 0;
    }
    if (args is ["check", string manifest])
    {
        var package = BepInExLoaderPackage.Read(manifest);
        Console.WriteLine($"PASS: {package.Identity}: {package.Files.Count} loader/core file(s) match.");
        return 0;
    }
    Console.Error.WriteLine("Usage: dotnet run scripts/bepinex-loader.cs -- capture ROOT NAME VERSION MANIFEST | check MANIFEST");
    return 2;
}
catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or InvalidOperationException)
{
    Console.Error.WriteLine(error.Message);
    return 3;
}
