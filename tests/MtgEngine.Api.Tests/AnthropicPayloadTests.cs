using System.Text.Json;
using MtgEngine.Api.Services;

namespace MtgEngine.Api.Tests;

/// <summary>
/// The shape of the request that actually goes on the wire.
/// </summary>
/// <remarks>
/// Every field here is one the provider rejects, silently ignores, or bills against the
/// answer — so a payload that is merely plausible is not good enough, and none of it is
/// visible from a response body. A deck build spent an entire 16,000-token budget thinking
/// and returned 1,543 characters of an unterminated JSON object; the caller's deserialiser
/// swallowed the failure, the build reported "0 candidates", and the user was shown an
/// empty deck with no error anywhere. These tests pin the two parameters that decide it.
/// </remarks>
public sealed class AnthropicPayloadTests
{
    private static AnthropicRequest Request() =>
        new("claude-opus-5", MaxTokens: 32000, Messages: [new { role = "user", content = "hi" }]);

    private static JsonElement Payload(AnthropicRequest request) =>
        JsonSerializer.SerializeToElement(AnthropicClient.BuildPayload(request));

    [Fact]
    public void Effort_is_sent_as_output_config_not_as_a_thinking_budget()
    {
        // Opus 5 reasons adaptively. A thinking.budget_tokens is a 400 from that model —
        // "not supported for this model. Use thinking.type.adaptive and output_config.effort".
        var payload = Payload(Request() with { Effort = "medium" });

        Assert.Equal("medium", payload.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.False(payload.TryGetProperty("thinking", out _));
    }

    [Fact]
    public void No_effort_means_no_output_config_at_all()
    {
        // Omitted, not sent empty: the provider default applies only when the key is absent.
        Assert.False(Payload(Request()).TryGetProperty("output_config", out _));
    }

    [Fact]
    public void A_null_temperature_is_omitted_rather_than_sent_as_null()
    {
        // The thinking models reject the key's presence, not just a non-default value.
        Assert.False(Payload(Request() with { Temperature = null }).TryGetProperty("temperature", out _));
    }

    [Fact]
    public void A_set_temperature_still_reaches_the_wire()
    {
        // The older models still steer on it; omitting it would silently move them to 1.0.
        Assert.Equal(0, Payload(Request() with { Temperature = 0 }).GetProperty("temperature").GetDouble());
    }
}
