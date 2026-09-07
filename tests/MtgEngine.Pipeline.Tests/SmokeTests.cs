using MtgEngine.Cards.Pipeline;

namespace MtgEngine.Cards.Pipeline.Tests;

/// <summary>The test project exists before the stages do, so each stage's builder adds tests here rather than creating the project.</summary>
public sealed class SmokeTests
{
    [Fact]
    public void The_vocabulary_reference_lists_the_effects()
    {
        var text = VocabularyReference.Text();
        Assert.Contains("## Effect kinds", text, StringComparison.Ordinal);
        Assert.Contains("- Draw(", text, StringComparison.Ordinal);
        Assert.Contains("- KeywordAbility(", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fenced_or_prefixed_answer_still_parses()
    {
        const string text = "Here you go:\n```json\n{\"oracleId\":\"x\",\"name\":\"Test\",\"layout\":\"Normal\",\"faces\":[{\"name\":\"Test\",\"role\":\"Main\",\"types\":{\"types\":[\"Creature\"]}}]}\n```";
        var (definition, error) = Translator.Parse(text);
        Assert.Null(error);
        Assert.Equal("Test", definition!.Name);
    }
}
