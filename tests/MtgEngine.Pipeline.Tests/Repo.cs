namespace MtgEngine.Cards.Pipeline.Tests;

internal static class Repo
{
    public static string Root { get; } = Find();

    public static string Path(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);

    /// <summary>The Scryfall corpus lives only in the API's bin folder; tests that need it skip when it is absent rather than pass on nothing.</summary>
    public static string? CorpusPath()
    {
        var path = Path("MtgEngine.Api", "bin", "Debug", "net10.0", "bulk-data", "oracle_cards.json");
        return File.Exists(path) ? path : null;
    }

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
