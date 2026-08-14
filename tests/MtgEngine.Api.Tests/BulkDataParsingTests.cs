using MtgEngine.Api.Services;

namespace MtgEngine.Api.Tests;

/// <summary>
/// Covers the bulk-file reader. Scryfall switched from a JSON array to gzipped
/// newline-delimited JSON, and the reader has to keep understanding both.
/// </summary>
public class BulkDataParsingTests
{
    private static string[] NamesFrom(string content)
    {
        using var reader = new StringReader(content);
        return [.. BulkDataService.ReadCards(reader)
            .Select(el => el.GetProperty("name").GetString()!)];
    }

    [Fact]
    public void ReadCards_ParsesNewlineDelimitedJson()
    {
        var jsonl = """
            {"name":"Sol Ring","set":"ltr"}
            {"name":"Goblin Bombardment","set":"tmp"}
            """;

        Assert.Equal(["Sol Ring", "Goblin Bombardment"], NamesFrom(jsonl));
    }

    [Fact]
    public void ReadCards_StillParsesTheLegacyJsonArray()
    {
        var array = """
            [
            {"name":"Sol Ring","set":"ltr"},
            {"name":"Goblin Bombardment","set":"tmp"}
            ]
            """;

        Assert.Equal(["Sol Ring", "Goblin Bombardment"], NamesFrom(array));
    }

    [Fact]
    public void ReadCards_SkipsLeadingBlankLinesBeforeDetectingFormat()
    {
        var array = "\n\n  [\n{\"name\":\"Sol Ring\"}\n]";
        Assert.Equal(["Sol Ring"], NamesFrom(array));
    }

    [Fact]
    public void ReadCards_CountsUnparseableLinesInsteadOfThrowing()
    {
        var jsonl = """
            {"name":"Sol Ring"}
            {"name":"truncated"
            {"name":"Mana Crypt"}
            """;

        using var reader = new StringReader(jsonl);
        var stats = new BulkDataService.ParseStats();
        var names = BulkDataService.ReadCards(reader, stats)
            .Select(el => el.GetProperty("name").GetString()!)
            .ToArray();

        Assert.Equal(["Sol Ring", "Mana Crypt"], names);
        Assert.Equal(1, stats.BadLines);
    }

    [Fact]
    public void ReadCards_HandlesAnEmptyFile()
    {
        Assert.Empty(NamesFrom(string.Empty));
    }

    /// <summary>
    /// The reader yields elements owned by a document it disposes after each line, so
    /// values have to be copied out inside the loop. This pins that contract down.
    /// </summary>
    [Fact]
    public void ReadCards_ValuesReadInsideTheLoopStayValid()
    {
        var jsonl = string.Join("\n",
            Enumerable.Range(0, 500).Select(i => $$"""{"name":"Card {{i}}"}"""));

        var names = NamesFrom(jsonl);

        Assert.Equal(500, names.Length);
        Assert.Equal("Card 0", names[0]);
        Assert.Equal("Card 499", names[499]);
    }
}
