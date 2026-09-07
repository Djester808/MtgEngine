using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;
using MtgEngine.Cards;

namespace MtgEngine.Api.Tests;

/// <summary>
/// The status endpoints over the generated card files: what one card's implementation looks
/// like, what the whole corpus adds up to, and what happens when a person settles a dispute.
/// The service is built directly over a temporary <c>cards/</c> directory, because the files are
/// the ledger — there is no database behind this.
/// </summary>
public sealed class CardsImplementationTests : IDisposable
{
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "CardsImplementationTests", Guid.NewGuid().ToString("n"));

    public CardsImplementationTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void The_implementation_endpoint_reports_the_status_and_the_scenarios()
    {
        var file = Write(Card("verified-1", "a verified card") with
        {
            Status = ImplementationStatus.Verified,
            Scenarios = [ScenarioNamed("smoke"), ScenarioNamed("a ruling")],
            Verification = new Verification("engine:abc", "run-9", 2, 2, DateTimeOffset.UnixEpoch),
        });

        var dto = Service(file).Implementation(file.OracleId);

        Assert.Equal(file.OracleId, dto.OracleId);
        Assert.Equal("a verified card", dto.Name);
        Assert.Equal(ImplementationStatus.Verified, dto.Status);
        Assert.Equal(2, dto.Scenarios.Count);
        Assert.Contains(dto.Scenarios, s => s.Name == "smoke");
        Assert.True(dto.Scenarios[0].Passed);
        Assert.Equal("engine:abc", dto.LastVerification!.EngineHash);
        Assert.Equal("run-9", dto.LastVerification.RunId);
    }

    [Fact]
    public void An_oracle_id_no_file_covers_is_a_not_found()
    {
        var service = Service();

        Assert.Throws<ResourceNotFoundException>(() => service.Implementation("nothing-here"));
    }

    [Fact]
    public void The_coverage_endpoint_counts_by_status_and_ranks_the_primitive_backlog()
    {
        Write(Card("cov-1", "one") with { Status = ImplementationStatus.Verified });
        Write(Card("cov-2", "two") with { Status = ImplementationStatus.Verified });
        Write(Blocked("cov-3", "three", "no primitive for daybound"));
        Write(Blocked("cov-4", "four", "no primitive for daybound"));
        Write(Blocked("cov-5", "five", "no primitive for subgames"));

        var coverage = Service().Coverage();

        Assert.Equal(5, coverage.Total);
        Assert.Equal(2, coverage.ByStatus[nameof(ImplementationStatus.Verified)]);
        Assert.Equal(3, coverage.ByStatus[nameof(ImplementationStatus.Translated)]);
        Assert.Equal("no primitive for daybound", coverage.PrimitiveBacklog[0].Reason);
        Assert.Equal(2, coverage.PrimitiveBacklog[0].CardsBlocked);
        Assert.Equal(1, coverage.PrimitiveBacklog[1].CardsBlocked);
    }

    [Fact]
    public void Coverage_filtered_to_disputed_shows_both_readings_and_where_they_differ()
    {
        Write(Disputed("dis-1", "a disputed card"));

        var coverage = Service().Coverage(ImplementationStatus.Disputed);

        var disputed = Assert.Single(coverage.Disputed!);
        Assert.Equal("dis-1", disputed.OracleId);
        Assert.Equal(2, disputed.Readings.Count);
        Assert.Equal("claude-opus-5", disputed.Readings[0].Model);
        Assert.NotNull(disputed.FirstDifference);
    }

    [Fact]
    public void Coverage_without_a_status_carries_no_disputed_list()
    {
        Write(Disputed("dis-2", "a disputed card"));

        Assert.Null(Service().Coverage().Disputed);
    }

    [Fact]
    public void Choosing_a_reading_rewrites_the_file_as_a_translated_card()
    {
        var file = Write(Disputed("choose-1", "a disputed card"));

        var dto = Service(file).Choose(file.OracleId, "claude-sonnet-5");

        Assert.Equal(ImplementationStatus.Translated, dto.Status);
        var written = CardFileSerializer.Read(File.ReadAllText(Path.Combine(_directory, "choose-1.json")));
        Assert.Equal(ImplementationStatus.Translated, written.Status);
        Assert.Null(written.Candidates);
        // The chosen reading is now the definition: the sonnet reading is the one with two faces.
        Assert.Equal(2, written.Definition.Faces.Count);
        var provenance = Assert.IsType<TranslatedProvenance>(written.Source);
        Assert.Equal("claude-sonnet-5", Assert.Single(provenance.Models));
        Assert.False(provenance.Agreed);
    }

    [Fact]
    public void A_second_choose_on_a_card_that_is_no_longer_disputed_is_refused()
    {
        var file = Write(Disputed("choose-2", "a disputed card"));
        var service = Service(file);
        service.Choose(file.OracleId, "claude-sonnet-5");

        Assert.Throws<InvalidResourceStateException>(() => service.Choose(file.OracleId, "claude-opus-5"));
    }

    [Fact]
    public void Choosing_a_model_that_never_read_the_card_is_a_not_found()
    {
        var file = Write(Disputed("choose-3", "a disputed card"));
        var service = Service(file);

        Assert.Throws<ResourceNotFoundException>(() => service.Choose(file.OracleId, "some-other-model"));
    }

    [Fact]
    public void The_batch_endpoint_answers_in_the_order_asked_and_calls_an_unknown_id_untranslated()
    {
        var verified = Write(Card("batch-1", "one") with { Status = ImplementationStatus.Verified });
        var translated = Write(Card("batch-2", "two"));

        var dtos = Service(verified, translated).Implementations(["batch-2", "nothing-here", "batch-1"]);

        Assert.Equal(3, dtos.Count);
        Assert.Equal(["batch-2", "nothing-here", "batch-1"], dtos.Select(d => d.OracleId).ToArray());
        Assert.Equal(ImplementationStatus.Translated, dtos[0].Status);
        Assert.Equal(ImplementationStatus.Untranslated, dtos[1].Status);
        Assert.Empty(dtos[1].Scenarios);
        Assert.Equal(ImplementationStatus.Verified, dtos[2].Status);
    }

    [Fact]
    public void The_batch_request_refuses_an_empty_list()
    {
        Assert.False(Valid(new CardImplementationsRequest([])));
        Assert.True(Valid(new CardImplementationsRequest(["one"])));
        Assert.False(Valid(new CardImplementationsRequest([.. Enumerable.Range(0, CardImplementationsRequest.MaxIds + 1).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture))])));
    }

    /// <summary>
    /// The client's status union is the enum's names, so the wire has to carry "Verified" and not
    /// 3. The converter is registered in <c>Program.cs</c>'s <c>AddJsonOptions</c>; this asserts
    /// what that registration produces for this DTO.
    /// </summary>
    [Fact]
    public void Status_serialises_as_the_enum_name()
    {
        var file = Write(Card("json-1", "a verified card") with { Status = ImplementationStatus.Verified });

        var json = JsonSerializer.Serialize(Service(file).Implementations([file.OracleId]), MvcJson);

        Assert.Contains("\"status\":\"Verified\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"status\":3", json, StringComparison.Ordinal);
    }

    /// <summary>What <c>builder.Services.AddControllers().AddJsonOptions(...)</c> leaves the API writing.</summary>
    private static readonly JsonSerializerOptions MvcJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// The attributes on a positional record target the constructor parameter, which is what MVC
    /// validates when it binds the body — <c>[property: ...]</c> throws at bind time, so the check
    /// has to read the parameter the same way.
    /// </summary>
    private static bool Valid(CardImplementationsRequest request) =>
        typeof(CardImplementationsRequest)
            .GetConstructors()[0]
            .GetParameters()
            .Single(p => string.Equals(p.Name, nameof(CardImplementationsRequest.OracleIds), StringComparison.Ordinal))
            .GetCustomAttributes<ValidationAttribute>()
            .All(a => a.IsValid(request.OracleIds));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private CardImplementationService Service(params CardFile[] known) =>
        new(new CardsImplementationPool(known), new CardImplementationOptions(_directory));

    private CardFile Write(CardFile file)
    {
        File.WriteAllText(Path.Combine(_directory, file.OracleId + ".json"), CardFileSerializer.Write(file));
        return file;
    }

    private static CardFile Card(string oracleId, string name, IReadOnlyList<Ability>? abilities = null) => new(
        oracleId,
        name,
        OracleHashing.Of([name]),
        IrVersion.Current,
        Definition(oracleId, name, abilities),
        ImplementationStatus.Translated,
        new TemplateProvenance("test"));

    private static CardDefinition Definition(string oracleId, string name, IReadOnlyList<Ability>? abilities = null, int faces = 1) => new(
        oracleId,
        name,
        Layout.Normal,
        [.. Enumerable.Range(0, faces).Select(i => new Face(
            i == 0 ? name : name + " reverse",
            FaceRole.Main,
            new TypeLine(CardType.Instant),
            ManaCost: "{1}",
            Abilities: i == 0 ? abilities : null))]);

    private static CardFile Blocked(string oracleId, string name, string reason) =>
        Card(oracleId, name, [new SpellAbility(new Unsupported(reason))]);

    private static CardFile Disputed(string oracleId, string name) => Card(oracleId, name) with
    {
        Status = ImplementationStatus.Disputed,
        Candidates =
        [
            new TranslationCandidate("claude-opus-5", Definition(oracleId, name)),
            new TranslationCandidate("claude-sonnet-5", Definition(oracleId, name, faces: 2)),
        ],
    };

    private static Scenario ScenarioNamed(string title) => new(
        title,
        title,
        new GeneratedSource("test"),
        new ScenarioSetup([new PlayerSetup("A")], "A"),
        [],
        [new Life("A", 20)]);

    /// <summary>The pool the deck gate reads. Only the files the test named are known to it.</summary>
    private sealed class CardsImplementationPool(params CardFile[] files) : ICardFilePool
    {
        public int Count => files.Length;

        public CardDefinition? ByName(string name) => files.FirstOrDefault(f => f.Name == name)?.Definition;

        public CardDefinition? ByOracleId(string oracleId) => files.FirstOrDefault(f => f.OracleId == oracleId)?.Definition;

        public CardStatus StatusOf(string oracleId)
        {
            var file = files.FirstOrDefault(f => f.OracleId == oracleId);
            return file is null ? CardStatus.Unknown(oracleId) : new CardStatus(file.OracleId, file.Name, file.Status);
        }
    }
}
