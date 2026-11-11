namespace SimpleXray.Core.Tests;

internal static class RepoPaths
{
    /// <summary>Repository root (the directory containing SimpleXray.slnx).</summary>
    public static string Root { get; } = FindRoot();

    public static string XrayExe => Path.Combine(Root, "assets", "core", "xray.exe");

    public static string GeoDirectory => Path.Combine(Root, "assets", "geo");

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SimpleXray.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
