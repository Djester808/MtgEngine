using System.Text.Json;
using System.Text.Json.Nodes;

namespace MtgEngine.Cards.Tests;

/// <summary>
/// The schema is committed under cards/schema so a change to the vocabulary shows up as a
/// reviewable diff and the gate fails when the committed file lags the build. Set
/// UPDATE_SCHEMA=1 to rewrite the files from the current build.
/// </summary>
public sealed class SchemaSnapshotTests
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public static TheoryData<string, Func<JsonNode>> Schemas => new()
    {
        { "definition.schema.json", CardFileSerializer.DefinitionSchema },
        { "card-file.schema.json", CardFileSerializer.FileSchema },
        { "scenario.schema.json", CardFileSerializer.ScenarioSchema },
    };

    [Theory]
    [MemberData(nameof(Schemas))]
    public void The_committed_schema_matches_the_build(string fileName, Func<JsonNode> generate)
    {
        var path = Repo.Path("cards", "schema", fileName);
        var current = generate().ToJsonString(Pretty).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";

        if (Environment.GetEnvironmentVariable("UPDATE_SCHEMA") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, current);
        }

        Assert.True(File.Exists(path), $"{path} is missing; run the Cards tests with UPDATE_SCHEMA=1 to write it.");
        var committed = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.True(committed == current, $"{fileName} differs from the build; run the Cards tests with UPDATE_SCHEMA=1 and commit the result.");
    }
}
