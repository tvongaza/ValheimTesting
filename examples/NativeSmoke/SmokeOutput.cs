internal static class SmokeOutput
{
    // Keep disposable copies and evidence outside installs and account data, before creating any output directory.
    public static void RefuseInside(string output, params string[] sources)
    {
        output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(output));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (string source in sources)
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
            if (output.Equals(root, comparison) || output.StartsWith(root + Path.DirectorySeparatorChar, comparison))
                throw new ArgumentException($"--output must be outside the prepared install and account directories: {root}");
        }
    }
}
