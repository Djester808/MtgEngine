using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;
using Xunit;

namespace MtgEngine.Rules.Tests;

public sealed class ManaFineTuneServiceTests
{
    private const string FakeApiKey = "test-api-key-123";

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// Wraps innerJson in the Anthropic messages API response envelope.
    private static string AnthropicEnvelope(string innerJson) =>
        JsonSerializer.Serialize(new
        {
            id      = "msg_test",
            type    = "message",
            content = new[] { new { type = "text", text = innerJson } },
        });

    private static (ManaFineTuneService service, Mock<HttpMessageHandler> handler)
        CreateService(string responseBody, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = status,
                Content    = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            });

        var httpClient = new HttpClient(handler.Object)
        {
            BaseAddress = new Uri("https://api.anthropic.com/"),
        };

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("AnthropicApi")).Returns(httpClient);

        var config = new Mock<IConfiguration>();
        config.Setup(c => c["Anthropic:ApiKey"]).Returns(FakeApiKey);

        var logger = Mock.Of<ILogger<ManaFineTuneService>>();

        return (new ManaFineTuneService(factory.Object, config.Object, logger), handler);
    }

    private static (ManaFineTuneService service, Mock<HttpMessageHandler> handler)
        CreateServiceWithCapture(Action<string> onBody)
    {
        var responseEnvelope = AnthropicEnvelope("""{"advice":[],"landSuggestions":[]}""");
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>(async (r, _) =>
            {
                onBody(await r.Content!.ReadAsStringAsync());
                return new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content    = new StringContent(responseEnvelope, Encoding.UTF8, "application/json"),
                };
            });

        var httpClient = new HttpClient(handler.Object) { BaseAddress = new Uri("https://api.anthropic.com/") };
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("AnthropicApi")).Returns(httpClient);
        var config = new Mock<IConfiguration>();
        config.Setup(c => c["Anthropic:ApiKey"]).Returns(FakeApiKey);
        return (new ManaFineTuneService(factory.Object, config.Object, Mock.Of<ILogger<ManaFineTuneService>>()), handler);
    }

    private static ManaFineTuneRequest DefaultRequest() => new()
    {
        Format           = "commander",
        DeckCardNames    = ["Sol Ring", "Counterspell"],
        CurrentLands     = 30,
        RecommendedLands = 36,
        AvgCmc           = 3.2,
        ActiveColors     = ["U", "W"],
    };

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetFineTuneAsync_ParsesAdviceAndLandSuggestions()
    {
        var inner = """{"advice":["Add 2 more lands","Use fetch lands"],"landSuggestions":[{"name":"Flooded Strand","reason":"Fetches blue mana"}]}""";
        var (service, _) = CreateService(AnthropicEnvelope(inner));

        var result = await service.GetFineTuneAsync(DefaultRequest());

        result.Advice.Should().HaveCount(2);
        result.Advice[0].Should().Be("Add 2 more lands");
        result.Advice[1].Should().Be("Use fetch lands");
        result.LandSuggestions.Should().HaveCount(1);
        result.LandSuggestions[0].Name.Should().Be("Flooded Strand");
        result.LandSuggestions[0].Reason.Should().Be("Fetches blue mana");
    }

    [Fact]
    public async Task GetFineTuneAsync_HandlesEmptyAdviceAndSuggestions()
    {
        var inner = """{"advice":[],"landSuggestions":[]}""";
        var (service, _) = CreateService(AnthropicEnvelope(inner));

        var result = await service.GetFineTuneAsync(DefaultRequest());

        result.Advice.Should().BeEmpty();
        result.LandSuggestions.Should().BeEmpty();
    }

    [Fact]
    public async Task GetFineTuneAsync_HandlesMultipleLandSuggestions()
    {
        var inner = """
            {
              "advice": ["tip"],
              "landSuggestions": [
                {"name": "Flooded Strand",   "reason": "Fetches blue"},
                {"name": "Hallowed Fountain", "reason": "Dual land"},
                {"name": "Celestial Colonnade","reason": "Utility"},
                {"name": "Tundra",            "reason": "Original dual"}
              ]
            }
            """;
        var (service, _) = CreateService(AnthropicEnvelope(inner));

        var result = await service.GetFineTuneAsync(DefaultRequest());

        result.LandSuggestions.Should().HaveCount(4);
        result.LandSuggestions.Should().AllSatisfy(ls =>
        {
            ls.Name.Should().NotBeNullOrWhiteSpace();
            ls.Reason.Should().NotBeNullOrWhiteSpace();
        });
    }

    // ── Markdown stripping ────────────────────────────────────────────────────

    [Fact]
    public async Task GetFineTuneAsync_StripsMarkdownCodeFence()
    {
        var inner = "```json\n{\"advice\":[\"Use duals\"],\"landSuggestions\":[]}\n```";
        var (service, _) = CreateService(AnthropicEnvelope(inner));

        var result = await service.GetFineTuneAsync(DefaultRequest());

        result.Advice.Should().ContainSingle().Which.Should().Be("Use duals");
    }

    [Fact]
    public async Task GetFineTuneAsync_StripsPlainCodeFence()
    {
        var inner = "```\n{\"advice\":[\"Add ramp\"],\"landSuggestions\":[]}\n```";
        var (service, _) = CreateService(AnthropicEnvelope(inner));

        var result = await service.GetFineTuneAsync(DefaultRequest());

        result.Advice.Should().ContainSingle().Which.Should().Be("Add ramp");
    }

    // ── Request construction ──────────────────────────────────────────────────

    [Fact]
    public async Task GetFineTuneAsync_SendsRequestToAnthropicEndpoint()
    {
        var inner = """{"advice":[],"landSuggestions":[]}""";
        var (service, handler) = CreateService(AnthropicEnvelope(inner));

        await service.GetFineTuneAsync(DefaultRequest());

        handler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(r =>
                r.Method == HttpMethod.Post &&
                r.RequestUri!.ToString().Contains("v1/messages")),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task GetFineTuneAsync_IncludesApiKeyInRequestHeader()
    {
        var inner = """{"advice":[],"landSuggestions":[]}""";
        var (service, handler) = CreateService(AnthropicEnvelope(inner));

        await service.GetFineTuneAsync(DefaultRequest());

        handler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(r =>
                r.Headers.Contains("x-api-key") &&
                r.Headers.GetValues("x-api-key").First() == FakeApiKey),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task GetFineTuneAsync_IncludesDeckCardNamesInPrompt()
    {
        string? capturedBody = null;
        var responseEnvelope = AnthropicEnvelope("""{"advice":[],"landSuggestions":[]}""");

        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>(async (r, _) =>
            {
                capturedBody = await r.Content!.ReadAsStringAsync();
                return new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content    = new StringContent(responseEnvelope, Encoding.UTF8, "application/json"),
                };
            });

        var httpClient = new HttpClient(handler.Object) { BaseAddress = new Uri("https://api.anthropic.com/") };
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("AnthropicApi")).Returns(httpClient);
        var config = new Mock<IConfiguration>();
        config.Setup(c => c["Anthropic:ApiKey"]).Returns(FakeApiKey);
        var service = new ManaFineTuneService(factory.Object, config.Object, Mock.Of<ILogger<ManaFineTuneService>>());

        await service.GetFineTuneAsync(DefaultRequest() with { DeckCardNames = ["Sol Ring", "Counterspell", "Atraxa"] });

        capturedBody.Should().NotBeNull();
        capturedBody.Should().Contain("Sol Ring").And.Contain("Atraxa");
    }

    // ── Error handling ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetFineTuneAsync_ThrowsHttpRequestException_OnNonSuccessStatus()
    {
        var (service, _) = CreateService("""{"error":"unauthorized"}""", HttpStatusCode.Unauthorized);

        var act = async () => await service.GetFineTuneAsync(DefaultRequest());

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task GetFineTuneAsync_ThrowsHttpRequestException_OnServerError()
    {
        var (service, _) = CreateService("""{"error":"internal error"}""", HttpStatusCode.InternalServerError);

        var act = async () => await service.GetFineTuneAsync(DefaultRequest());

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public void Constructor_ThrowsInvalidOperationException_WhenApiKeyMissing()
    {
        var factory = new Mock<IHttpClientFactory>();
        var config  = new Mock<IConfiguration>();
        config.Setup(c => c["Anthropic:ApiKey"]).Returns((string?)null);
        var logger = Mock.Of<ILogger<ManaFineTuneService>>();

        var act = () => new ManaFineTuneService(factory.Object, config.Object, logger);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Anthropic:ApiKey*");
    }

    // ── Color name mapping ────────────────────────────────────────────────────

    [Fact]
    public async Task GetFineTuneAsync_MapsColorCodesToNamesInPrompt()
    {
        string? body = null;
        var (svc, _) = CreateServiceWithCapture(b => body = b);

        await svc.GetFineTuneAsync(DefaultRequest() with { ActiveColors = ["W", "U", "B", "R", "G"] });

        body.Should().Contain("White").And.Contain("Blue").And.Contain("Black")
            .And.Contain("Red").And.Contain("Green");
    }

    [Fact]
    public async Task GetFineTuneAsync_UsesColorlessLabel_WhenNoActiveColors()
    {
        string? body = null;
        var (svc, _) = CreateServiceWithCapture(b => body = b);

        await svc.GetFineTuneAsync(DefaultRequest() with { ActiveColors = [] });

        body.Should().Contain("Colorless");
    }
}
