using System.Text.Json;
using MtgEngine.Api.Services;

namespace MtgEngine.Api.Tests;

/// <summary>
/// The model is asked for JSON by prompt instruction only, so the response is JSON
/// by convention rather than contract. These cover the salvage paths that used to be
/// implemented three different ways across five services.
/// </summary>
public class AnthropicResponseTests
{
    private static string Response(params (string Type, string Text)[] blocks)
    {
        var content = blocks.Select(b => b.Type == "text"
            ? new { type = b.Type, text = b.Text }
            : (object)new { type = b.Type, text = b.Text });
        return JsonSerializer.Serialize(new { content });
    }

    // ---- ExtractText -------------------------------------------------

    [Fact]
    public void ExtractText_ReadsSingleTextBlock() =>
        Assert.Equal("hello", AnthropicResponse.ExtractText(Response(("text", "hello"))));

    [Fact]
    public void ExtractText_SkipsNonTextLeadingBlock()
    {
        // Enabling extended thinking or a server tool puts a non-text block first.
        // The old content[0] lookup threw here; this is the regression guard.
        var json = Response(("thinking", "internal reasoning"), ("text", "the answer"));
        Assert.Equal("the answer", AnthropicResponse.ExtractText(json));
    }

    [Fact]
    public void ExtractText_ReturnsEmpty_WhenNoTextBlock() =>
        Assert.Equal(string.Empty, AnthropicResponse.ExtractText(Response(("thinking", "only thinking"))));

    [Fact]
    public void ExtractText_ReturnsEmpty_WhenContentMissing() =>
        Assert.Equal(string.Empty, AnthropicResponse.ExtractText("""{"id":"msg_1"}"""));

    [Fact]
    public void ExtractText_ReturnsEmpty_WhenContentNotArray() =>
        Assert.Equal(string.Empty, AnthropicResponse.ExtractText("""{"content":"oops"}"""));

    // ---- ExtractJsonObject -------------------------------------------

    [Fact]
    public void ExtractJson_PassesThroughBareObject() =>
        Assert.Equal("""{"a":1}""", AnthropicResponse.ExtractJsonObject("""{"a":1}"""));

    [Fact]
    public void ExtractJson_StripsMarkdownFences()
    {
        var fenced = "```json\n{\"score\":95}\n```";
        Assert.Equal("""{"score":95}""", AnthropicResponse.ExtractJsonObject(fenced));
    }

    [Fact]
    public void ExtractJson_StripsPreambleAndPostamble()
    {
        // The case the old fence-strippers in Synergy/CardVision could not handle.
        var chatty = "Sure! Here is the result:\n{\"score\":42}\nLet me know if you need more.";
        Assert.Equal("""{"score":42}""", AnthropicResponse.ExtractJsonObject(chatty));
    }

    [Fact]
    public void ExtractJson_HandlesBracesInsideStringLiterals()
    {
        var tricky = """{"reason":"synergises with {T}: add mana","score":80}""";
        Assert.Equal(tricky, AnthropicResponse.ExtractJsonObject(tricky));
    }

    [Fact]
    public void ExtractJson_HandlesEscapedQuotesInsideStrings()
    {
        var tricky = """{"reason":"the \"best\" card","score":80}""";
        Assert.Equal(tricky, AnthropicResponse.ExtractJsonObject(tricky));
    }

    [Fact]
    public void ExtractJson_HandlesNestedObjects()
    {
        var nested = """{"outer":{"inner":{"deep":1}},"n":2}""";
        Assert.Equal(nested, AnthropicResponse.ExtractJsonObject(nested));
    }

    [Fact]
    public void ExtractJson_StopsAtFirstCompleteObject() =>
        Assert.Equal("""{"a":1}""", AnthropicResponse.ExtractJsonObject("""{"a":1} {"b":2}"""));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no json here at all")]
    public void ExtractJson_ReturnsEmptyObject_WhenNoObjectPresent(string input) =>
        Assert.Equal("{}", AnthropicResponse.ExtractJsonObject(input));

    // ---- DeserializeJson ---------------------------------------------

    private sealed record Score(int score, string reason);

    [Fact]
    public void DeserializeJson_EndToEnd_ThroughFencesAndPreamble()
    {
        var json = Response(("text", "Here you go:\n```json\n{\"score\":88,\"reason\":\"good fit\"}\n```"));

        var result = AnthropicResponse.DeserializeJson<Score>(json);

        Assert.NotNull(result);
        Assert.Equal(88, result!.score);
        Assert.Equal("good fit", result.reason);
    }

    [Fact]
    public void DeserializeJson_ReturnsDefault_OnMalformedPayload() =>
        Assert.Null(AnthropicResponse.DeserializeJson<Score>(Response(("text", "{not valid json"))));

    [Fact]
    public void DeserializeJson_IsCaseInsensitive()
    {
        var json = Response(("text", """{"Score":7,"REASON":"x"}"""));
        Assert.Equal(7, AnthropicResponse.DeserializeJson<Score>(json)!.score);
    }
}
