using Valheim.Testing.Game;

// Turns a targeted native regression into a directory for a person to review before sharing it. Publishes nothing.
//   bundle <bundle.json> <new-dir>     check the evidence and toolkit pin, then write the scrubbed bundle
//   verify <bundle.json> <dir>         check a bundle again (after any edit, and right before sharing it); no network
//   build <dir>                        build the bundled runner as a stranger would: fresh directory, only NuGet.org, isolated cache
if (args is not ([("bundle" or "verify"), _, _] or ["build", _]))
{
    Console.Error.WriteLine("Usage: regression-bundle bundle <bundle.json> <new-bundle-directory> | verify <bundle.json> <bundle-directory> | build <bundle-directory>");
    return 2;
}
try
{
    switch (args[0])
    {
        case "bundle":
            var manifest = RegressionBundle.Create(BundleSpec.Read(args[1]), args[2], new PublicBundleSources());
            foreach (string check in manifest.Checks) Console.WriteLine("checked: " + check);
            Console.WriteLine($"BUNDLE WRITTEN to {args[2]} ({manifest.Files.Count + 1} files). Nothing was published: read every file, build it, and verify it again before sharing.");
            return 0;
        case "verify":
            RegressionBundle.Verify(args[2], BundleSpec.Read(args[1]));
            Console.WriteLine("BUNDLE VERIFIED. Read every file before sharing it; this tool publishes nothing.");
            return 0;
        default:
            RegressionBundle.Build(args[1]);
            Console.WriteLine("BUNDLE BUILDS in a clean directory with only NuGet.org and an isolated package cache.");
            return 0;
    }
}
catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or InvalidDataException or HttpRequestException or TimeoutException)
{
    // Refused: nothing was written or published, and the message names each problem.
    Console.Error.WriteLine("REFUSED: " + error.Message);
    return 3;
}
