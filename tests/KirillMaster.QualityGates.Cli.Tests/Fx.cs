namespace KirillMaster.QualityGates.Cli.Tests;

internal static class Fx
{
    public static string Root { get; } = FindRoot();

    public static string Path(params string[] parts) =>
        System.IO.Path.Combine([Root, "fixtures", .. parts]);

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(System.IO.Path.Combine(dir.FullName, "fixtures")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("solution root not found");
    }
}
