using System.Text.Json;
using MtgEngine.Cards.Pipeline.Translate;
using MtgEngine.Cards.Pipeline.Verify;

namespace MtgEngine.Cards.Pipeline.Tests;

/// <summary>The translator's self-check: the collect step's parse and lint, over one local batch's answer files.</summary>
public sealed class AnswersCheckCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mtg-answers-check-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_good_answer_is_ok_a_bad_one_is_a_problem_and_a_missing_one_is_counted()
    {
        var client = new FileTranslationClient(_root);
        var batchId = await client.SubmitBatch("model-a", [("good", "s", "u"), ("bad", "s", "u"), ("absent", "s", "u")], CancellationToken.None);
        var answers = Path.Combine(client.Directory(batchId), FileTranslationClient.AnswersDirectory);
        var good = new CardDefinition("good", "Good Card", Layout.Normal, [new Face(Name: "Good Card", Role: FaceRole.Main, Types: new TypeLine(CardType.Creature, Subtypes: ["Bear"]), ManaCost: "{1}{G}", Power: new PrintedStat("2"), Toughness: new PrintedStat("2"), OracleText: string.Empty)]);
        File.WriteAllText(Path.Combine(answers, "good.json"), JsonSerializer.Serialize(good, CardFileSerializer.Options));
        File.WriteAllText(Path.Combine(answers, "bad.json"), "this is not json");
        var log = new StringWriter();

        var code = AnswersCheckCommand.Run(client.Directory(batchId), id => Card(id), log);

        Assert.Equal(AnswersCheckCommand.Problems, code);
        var text = log.ToString();
        Assert.Contains("ok      Good Card", text, StringComparison.Ordinal);
        Assert.Contains("PARSE   Bad Card", text, StringComparison.Ordinal);
        Assert.Contains("1 ok, 1 with problems, 1 missing of 3", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_directory_without_a_batch_file_is_reported()
    {
        var code = AnswersCheckCommand.Run(_root, _ => null, new StringWriter());

        Assert.Equal(AnswersCheckCommand.NoBatch, code);
    }

    private static CorpusCard Card(string id) =>
        new(id, id == "good" ? "Good Card" : "Bad Card", "normal", [new CorpusFace(id == "good" ? "Good Card" : "Bad Card", "Creature — Bear", "{1}{G}", string.Empty, "2", "2", null, null)], [], []);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
