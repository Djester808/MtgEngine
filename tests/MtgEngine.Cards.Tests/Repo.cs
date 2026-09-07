namespace MtgEngine.Cards.Tests;

/// <summary>Finds the repository root from wherever the test host runs, so tests can read the rulebook and the committed schema.</summary>
internal static class Repo
{
    public static string Root { get; } = Find();

    public static string Path(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);

    private static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "MtgEngine.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("MtgEngine.sln not found above " + AppContext.BaseDirectory);
    }
}
