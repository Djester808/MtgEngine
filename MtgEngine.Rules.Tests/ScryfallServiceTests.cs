using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MtgEngine.Api.Services;
using MtgEngine.Domain.Enums;
using Xunit;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Tests for ScryfallService two-layer cache (memory + disk).
/// Each test gets its own isolated temp directory.
/// </summary>
public sealed class ScryfallServiceTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"scryfall-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ---- Helpers --------------------------------------------------

    private ScryfallService MakeService(FakeHttpHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.scryfall.com/") },
            NullLogger<ScryfallService>.Instance,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ScryfallCache:Directory"] = _tempDir })
                .Build());

    private static string CreatureJson(
        string oracleId = "aaaaaaaa-0000-0000-0000-000000000001",
        string name     = "Test Creature") => $$"""
        {
          "oracle_id":    "{{oracleId}}",
          "name":         "{{name}}",
          "type_line":    "Creature — Beast",
          "oracle_text":  "Trample",
          "mana_cost":    "{1}{G}",
          "power":        "2",
          "toughness":    "3",
          "color_identity": ["G"],
          "keywords":     ["Trample"],
          "image_uris": {
            "normal":   "https://example.com/normal.jpg",
            "small":    "https://example.com/small.jpg",
            "art_crop": "https://example.com/art.jpg"
          },
          "flavor_text": "It roams free.",
          "artist":      "Jane Artist",
          "set":         "m21"
        }
        """;

    private static string LandJson(
        string oracleId = "bbbbbbbb-0000-0000-0000-000000000001") => $$"""
        {
          "oracle_id":    "{{oracleId}}",
          "name":         "Forest",
          "type_line":    "Basic Land — Forest",
          "oracle_text":  "",
          "color_identity": ["G"],
          "keywords":     [],
          "image_uris": { "normal": null, "small": null, "art_crop": null }
        }
        """;

    // ---- HTTP call count -----------------------------------------

    [Fact]
    public async Task GetByNameAsync_FirstCall_HitsHttp()
    {
        var handler = new FakeHttpHandler(CreatureJson());
        var svc = MakeService(handler);

        await svc.GetByNameAsync("Test Creature");

        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task GetByNameAsync_SecondCallSameName_HitsMemoryNotHttp()
    {
        var handler = new FakeHttpHandler(CreatureJson());
        var svc = MakeService(handler);

        await svc.GetByNameAsync("Test Creature");
        await svc.GetByNameAsync("Test Creature");

        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task GetByNameAsync_NewServiceInstance_HitsDiskNotHttp()
    {
        // First instance populates disk
        var handler1 = new FakeHttpHandler(CreatureJson());
        await MakeService(handler1).GetByNameAsync("Test Creature");
        handler1.CallCount.Should().Be(1);

        // Second instance (fresh memory) should serve from disk
        var handler2 = new FakeHttpHandler(CreatureJson());
        var result   = await MakeService(handler2).GetByNameAsync("Test Creature");

        handler2.CallCount.Should().Be(0);
        result.Should().NotBeNull();
        result!.Name.Should().Be("Test Creature");
    }

    [Fact]
    public async Task GetByOracleIdAsync_AfterNameFetch_HitsDiskNotHttp()
    {
        const string oracleId = "aaaaaaaa-0000-0000-0000-000000000001";

        // Fetch by name → should cross-populate oracle disk cache
        var handler1 = new FakeHttpHandler(CreatureJson(oracleId));
        await MakeService(handler1).GetByNameAsync("Test Creature");

        // Fresh instance: oracle lookup should be served from disk
        var handler2 = new FakeHttpHandler(CreatureJson(oracleId));
        var result   = await MakeService(handler2).GetByOracleIdAsync(oracleId);

        handler2.CallCount.Should().Be(0);
        result.Should().NotBeNull();
        result!.OracleId.Should().Be(oracleId);
    }

    [Fact]
    public async Task GetByOracleIdAsync_SecondCallSameId_HitsMemoryNotHttp()
    {
        const string oracleId = "aaaaaaaa-0000-0000-0000-000000000001";
        var handler = new FakeHttpHandler(CreatureJson(oracleId));
        var svc = MakeService(handler);

        await svc.GetByOracleIdAsync(oracleId);
        await svc.GetByOracleIdAsync(oracleId);

        handler.CallCount.Should().Be(1);
    }

    // ---- Disk file creation -------------------------------------

    [Fact]
    public async Task GetByNameAsync_CreatesNameDiskFile()
    {
        var handler = new FakeHttpHandler(CreatureJson());
        await MakeService(handler).GetByNameAsync("Test Creature");

        var files = Directory.GetFiles(Path.Combine(_tempDir, "by-name"), "*.json");
        files.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetByNameAsync_CrossPopulatesOracleDiskFile()
    {
        const string oracleId = "aaaaaaaa-0000-0000-0000-000000000001";
        var handler = new FakeHttpHandler(CreatureJson(oracleId));
        await MakeService(handler).GetByNameAsync("Test Creature");

        var oraclePath = Path.Combine(_tempDir, "by-oracle", $"{oracleId}.json");
        File.Exists(oraclePath).Should().BeTrue();
    }

    // ---- Error handling -----------------------------------------

    [Fact]
    public async Task GetByNameAsync_HttpError_ReturnsNull()
    {
        var handler = new FakeHttpHandler("{}", HttpStatusCode.NotFound);
        var svc = MakeService(handler);

        var result = await svc.GetByNameAsync("Nonexistent Card");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetByNameAsync_CorruptedDiskCache_FallsBackToHttp()
    {
        // Write garbage into the name cache file
        Directory.CreateDirectory(Path.Combine(_tempDir, "by-name"));
        File.WriteAllText(
            Path.Combine(_tempDir, "by-name", "Test Creature.json"),
            "{ this is not valid json !!!!");

        var handler = new FakeHttpHandler(CreatureJson());
        var result  = await MakeService(handler).GetByNameAsync("Test Creature");

        handler.CallCount.Should().Be(1);
        result.Should().NotBeNull();
    }

    // ---- Parsing ------------------------------------------------

    [Fact]
    public async Task GetByNameAsync_ParsesAllFields()
    {
        var handler = new FakeHttpHandler(CreatureJson(name: "Test Creature"));
        var result  = await MakeService(handler).GetByNameAsync("Test Creature");

        result.Should().NotBeNull();
        result!.Name.Should().Be("Test Creature");
        result.ManaCost.ManaValue.Should().Be(2);
        result.Power.Should().Be(2);
        result.Toughness.Should().Be(3);
        result.CardTypes.Should().HaveFlag(CardType.Creature);
        result.Keywords.Should().HaveFlag(KeywordAbility.Trample);
        result.FlavorText.Should().Be("It roams free.");
        result.Artist.Should().Be("Jane Artist");
        result.SetCode.Should().Be("m21");
        result.ImageUriNormal.Should().Be("https://example.com/normal.jpg");
        result.ImageUriSmall.Should().Be("https://example.com/small.jpg");
        result.ImageUriArtCrop.Should().Be("https://example.com/art.jpg");
        result.CastingSpeed.Should().Be(SpeedRestriction.Sorcery);
    }

    [Fact]
    public async Task GetByNameAsync_LandCard_HasEmptyManaCostString()
    {
        var handler = new FakeHttpHandler(LandJson());
        var result  = await MakeService(handler).GetByNameAsync("Forest");

        result.Should().NotBeNull();
        result!.ManaCost.ToString().Should().BeEmpty();
        result.CardTypes.Should().HaveFlag(CardType.Land);
        result.Supertypes.Should().Contain("Basic");
    }

    [Fact]
    public async Task GetByNameAsync_InstantCard_HasInstantCastingSpeed()
    {
        var instantJson = """
            {
              "oracle_id":    "cccccccc-0000-0000-0000-000000000001",
              "name":         "Counterspell",
              "type_line":    "Instant",
              "oracle_text":  "Counter target spell.",
              "mana_cost":    "{U}{U}",
              "color_identity": ["U"],
              "keywords":     [],
              "image_uris": {}
            }
            """;
        var handler = new FakeHttpHandler(instantJson);
        var result  = await MakeService(handler).GetByNameAsync("Counterspell");

        result!.CastingSpeed.Should().Be(SpeedRestriction.Instant);
    }

    [Fact]
    public async Task GetByNameAsync_MultipleDistinctCards_IndependentDiskFiles()
    {
        var handler = new FakeHttpHandler();
        handler.AddResponse("fuzzy=Test+Creature", CreatureJson("aaa00001-0000-0000-0000-000000000001", "Test Creature"));
        handler.AddResponse("fuzzy=Forest",         LandJson("bbb00001-0000-0000-0000-000000000001"));

        var svc = MakeService(handler);
        await svc.GetByNameAsync("Test Creature");
        await svc.GetByNameAsync("Forest");

        var nameFiles = Directory.GetFiles(Path.Combine(_tempDir, "by-name"), "*.json");
        nameFiles.Should().HaveCount(2);
        handler.CallCount.Should().Be(2);
    }

    // ---- Fake handler ------------------------------------------

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _responses = new(StringComparer.OrdinalIgnoreCase);
        private readonly string? _defaultJson;
        private readonly HttpStatusCode _defaultStatus;

        public int CallCount { get; private set; }

        public FakeHttpHandler(string json = "{}", HttpStatusCode status = HttpStatusCode.OK)
        {
            _defaultJson   = json;
            _defaultStatus = status;
        }

        public FakeHttpHandler() { }

        public void AddResponse(string urlSubstring, string json) =>
            _responses[urlSubstring] = json;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;

            var url = request.RequestUri?.ToString() ?? "";
            var json = _responses
                .FirstOrDefault(kv => url.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
                .Value ?? _defaultJson ?? "{}";

            var status = _defaultStatus != HttpStatusCode.OK && _defaultJson != null
                ? _defaultStatus
                : HttpStatusCode.OK;

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
