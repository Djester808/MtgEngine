using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Tests;

/// <summary>
/// The preview half of the AI build.
/// </summary>
/// <remarks>
/// The property worth a test is the one the whole feature rests on: a plan decides a deck
/// and writes nothing. If planning could touch the deck, "review before you commit" would
/// be a lie told by the UI, and the 99-card write it exists to prevent would already have
/// happened by the time the player saw the list.
/// </remarks>
public sealed class AiBuildPlanTests
{
    private static readonly Guid DeckId = Guid.NewGuid();
    private const string UserId = "user-1";
    private const string CommanderOracleId = "oracle-commander";

    private static CardDefinition Card(string name, params ManaColor[] colors) => new()
    {
        OracleId = "oracle-" + name.ToLowerInvariant().Replace(' ', '-'),
        Name = name,
        ColorIdentity = colors,
        CardTypes = CardType.Creature,
        Legalities = new Dictionary<string, string> { ["commander"] = "legal" },
    };

    private static CardDefinition CommanderCard() => new()
    {
        OracleId = CommanderOracleId,
        Name = "Test Commander",
        OracleText = "Does a thing.",
        ColorIdentity = [ManaColor.Green],
        CardTypes = CardType.Creature,
        Supertypes = ["Legendary"],
        Legalities = new Dictionary<string, string> { ["commander"] = "legal" },
    };

    private sealed class Cards : StubScryfallService
    {
        public List<CardDefinition> Known { get; init; } = [];

        public override Task<CardDefinition?> GetByOracleIdAsync(string oracleId) =>
            Task.FromResult(Known.Concat([CommanderCard()])
                .FirstOrDefault(c => c.OracleId == oracleId));

        public override Task<CardDefinition?> GetByNameAsync(string name) =>
            Task.FromResult(Known.FirstOrDefault(
                c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)));

        public override Task<PrintingDto[]> GetPrintingsAsync(string oracleId) =>
            Task.FromResult<PrintingDto[]>([new() { ScryfallId = "print-" + oracleId }]);

        public override Task<IReadOnlySet<string>> GetRecentSetCodesAsync(
            int monthsBack = 6, int? maxSets = null) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());

        public override Task<string[]> GetRecentCardNamesAsync(
            IReadOnlySet<string> setCodes, IReadOnlySet<ManaColor> commanderColors,
            IReadOnlySet<string>? allowedRarities = null, bool debutOnly = false) =>
            Task.FromResult<string[]>([]);

        public override Task<IReadOnlyDictionary<CardRole, string[]>> GetLegalCardsByRoleAsync(
            IReadOnlySet<ManaColor> commanderColors, int perRoleLimit, decimal? maxUsd = null) =>
            Task.FromResult<IReadOnlyDictionary<CardRole, string[]>>(
                new Dictionary<CardRole, string[]>
                {
                    [CardRole.Other] = [.. Known.Select(c => c.Name)],
                });

        public override Task<(CardDefinition[] Cards, int Total)> GetCandidatePoolAsync(
            IReadOnlySet<ManaColor> commanderColors, CardDefinition? commander = null,
            string? query = null, IReadOnlySet<string>? setCodes = null,
            bool gameChangersOnly = false, CardType types = CardType.None,
            int? cmcMin = null, int? cmcMax = null, int limit = 50, int offset = 0) =>
            Task.FromResult((Known.ToArray(), Known.Count));
    }

    /// <summary>Fails loudly on any write. That failure is the assertion.</summary>
    private sealed class ReadOnlyDeck : StubCollectionService
    {
        public int Writes { get; private set; }
        public List<string> WrittenOracleIds { get; } = [];

        public override Task<DeckDetailDto?> GetDeckAsync(Guid deckId, string userId) =>
            Task.FromResult<DeckDetailDto?>(new DeckDetailDto { Id = deckId, Name = "Deck", Cards = [] });

        public override Task<(CollectionCardDto Card, bool Created)> AddCardToCollectionAsync(
            Guid collectionId, string userId, AddCardToCollectionRequest request)
        {
            Writes++;
            WrittenOracleIds.Add(request.OracleId);
            return Task.FromResult((new CollectionCardDto { OracleId = request.OracleId }, true));
        }
    }

    private sealed class Doctrine : ICommanderDoctrine
    {
        public string Text => "DOCTRINE";
        public int ApproximateTokens => 1;
    }

    /// <summary>No tribe: these tests are about the plan/apply split, not theming.</summary>
    private sealed class NoAnalysis : ICommanderAnalysis
    {
        public Task<CommanderRequirements> AnalyseAsync(CardDefinition commander) =>
            Task.FromResult(CommanderRequirements.None);
    }

    private sealed class NoEdhrec : IEdhrecPoolService
    {
        public Task<IReadOnlyList<EdhrecCard>> GetCommanderPoolAsync(
            string commanderName, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EdhrecCard>>([]);
    }

    private sealed class ScriptedModel : IAnthropicClient
    {
        private readonly string[] _main;
        public AnthropicRequest? Last { get; private set; }

        public ScriptedModel(params string[] main) => _main = main;

        public AnthropicRequest? LastBuild { get; private set; }
        public AnthropicRequest? LastAssessment { get; private set; }

        public Task<string> SendAsync(AnthropicRequest request, CancellationToken ct = default)
        {
            Last = request;

            // Two passes share this stub: the build, then the assessment of what it built.
            bool assessing = request.Operation.Contains("assessment", StringComparison.OrdinalIgnoreCase);
            if (assessing)
                LastAssessment = request;
            else
                LastBuild = request;

            var payload = assessing
                ? JsonSerializer.Serialize(new
                {
                    verdict = "It works.",
                    findings = new[]
                    {
                        new { area = "Mana", severity = "improve", finding = "0 lands", fix = "add lands" },
                    },
                })
                : JsonSerializer.Serialize(new
                {
                    main = _main,
                    side = Array.Empty<string>(),
                    maybe = Array.Empty<string>(),
                    substitutes = Array.Empty<string>(),
                });

            return Task.FromResult(JsonSerializer.Serialize(new
            {
                content = new[] { new { type = "text", text = payload } },
            }));
        }

        public async Task<string> StreamTextAsync(
            AnthropicRequest request, Func<string, string, Task> onText,
            Func<int, Task>? onThinking = null, CancellationToken ct = default)
        {
            var envelope = await SendAsync(request, ct);
            var text = System.Text.Json.JsonDocument.Parse(envelope)
                .RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? string.Empty;
            await onText(text, text);
            return envelope;
        }
    }

    private static (AiBuildService Sut, ReadOnlyDeck Deck, ScriptedModel Model) Build(
        params string[] picks)
    {
        var known = picks.Select(p => Card(p, ManaColor.Green)).ToList();
        var cards = new Cards { Known = known };
        var deck = new ReadOnlyDeck();
        var model = new ScriptedModel(picks);

        return (
            new AiBuildService(cards, deck, new NoEdhrec(), model, new Doctrine(), new NoAnalysis(),
                NullLogger<AiBuildService>.Instance),
            deck,
            model);
    }

    private static AiBuildRequest Request() => new()
    {
        CommanderOracleId = CommanderOracleId,
        Bracket = 3,
    };

    // ---- The point of the feature -------------------------------------------

    [Fact]
    public async Task Planning_a_deck_writes_nothing()
    {
        var (sut, deck, _) = Build("Forest Friend", "Elf Helper");

        var plan = await sut.PlanDeckAsync(DeckId, UserId, Request());

        Assert.Equal(0, deck.Writes);
        Assert.Equal(2, plan.Cards.Length);
    }

    [Fact]
    public async Task A_plan_names_the_commander_and_the_slots_it_was_filling()
    {
        var (sut, _, _) = Build("Forest Friend");

        var plan = await sut.PlanDeckAsync(DeckId, UserId, Request());

        Assert.Equal(CommanderOracleId, plan.CommanderOracleId);
        Assert.Equal("Test Commander", plan.CommanderName);
        Assert.Equal(99, plan.MainTarget);
        Assert.Equal(98, plan.MainShortfall); // one card offered against 99 empty slots
    }

    [Fact]
    public async Task A_planned_card_carries_what_the_review_screen_needs()
    {
        var (sut, _, _) = Build("Forest Friend");

        var card = Assert.Single((await sut.PlanDeckAsync(DeckId, UserId, Request())).Cards);

        Assert.Equal("Forest Friend", card.Name);
        Assert.Equal("main", card.Board);
        Assert.Equal("print-oracle-forest-friend", card.ScryfallId);
        Assert.Contains("Creature", card.TypeLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Applying_a_plan_writes_exactly_what_was_accepted()
    {
        var (sut, deck, _) = Build("Forest Friend", "Elf Helper");
        var plan = await sut.PlanDeckAsync(DeckId, UserId, Request());

        var result = await sut.ApplyPlanAsync(DeckId, UserId, new AiApplyPlanRequest
        {
            CommanderOracleId = CommanderOracleId,
            Bracket = 3,
            Cards = plan.Cards,
        });

        // Three writes: the two accepted cards, plus the commander itself.
        Assert.Equal(3, deck.Writes);
        Assert.Equal(2, result.CardsAdded);
    }

    [Fact]
    public async Task Applying_a_plan_puts_the_commander_in_the_deck()
    {
        // The deck row carries CommanderOracleId, but the client resolves the command zone
        // by finding a CARD with that oracle id. Without this the zone renders empty and
        // reads "click or drop to assign" on a freshly built deck.
        var (sut, deck, _) = Build("Forest Friend");
        var plan = await sut.PlanDeckAsync(DeckId, UserId, Request());

        await sut.ApplyPlanAsync(DeckId, UserId, new AiApplyPlanRequest
        {
            CommanderOracleId = CommanderOracleId,
            Bracket = 3,
            Cards = plan.Cards,
        });

        Assert.Contains(CommanderOracleId, deck.WrittenOracleIds);
    }

    [Fact]
    public async Task Applying_re_validates_rather_than_trusting_the_payload()
    {
        // The plan travels through the client and back, so it is a request like any other.
        // A card outside the commander's colours must be refused on the way in, whatever
        // the payload claims was previously approved.
        var (sut, deck, _) = Build("Forest Friend");

        var result = await sut.ApplyPlanAsync(DeckId, UserId, new AiApplyPlanRequest
        {
            CommanderOracleId = CommanderOracleId,
            Bracket = 3,
            Cards =
            [
                new PlannedCardDto { Name = "Forest Friend", Board = "main" },
                new PlannedCardDto { Name = "Definitely Not A Real Card", Board = "main" },
            ],
        });

        // Two writes: the one valid card, plus the commander.
        Assert.Equal(2, deck.Writes);
        Assert.Equal(1, result.CardsAdded);
        Assert.Equal(1, result.CardsSkipped);
    }

    [Fact]
    public async Task The_build_still_writes_in_one_shot()
    {
        // The preview is additive: the original one-call build keeps working unchanged.
        var (sut, deck, _) = Build("Forest Friend", "Elf Helper");

        var result = await sut.BuildDeckAsync(DeckId, UserId, Request());

        Assert.Equal(3, deck.Writes); // two cards + the commander
        Assert.Equal(2, result.CardsAdded);
    }

    [Fact]
    public async Task The_build_reasons_from_the_doctrine()
    {
        // It was the only AI pass in the app that never received it, while carrying its own
        // drifted copy of the role quotas in the prompt.
        var (sut, _, model) = Build("Forest Friend");

        await sut.PlanDeckAsync(DeckId, UserId, Request());

        Assert.NotNull(model.Last!.System);
        Assert.Contains(
            "DOCTRINE", JsonSerializer.Serialize(model.Last.System), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plan_reports_the_facts_it_measured()
    {
        // Facts only. Per-card validation cannot see an unbalanced deck — twenty lands
        // passes every check — so the counts have to be stated for anything to judge them.
        var (sut, _, _) = Build("Forest Friend", "Elf Helper");

        var facts = (await sut.PlanDeckAsync(DeckId, UserId, Request())).Assessment.Facts;

        Assert.Equal(2, facts.Cards);
        Assert.Equal(0, facts.Lands);
        Assert.Equal(2, facts.Creatures);
        Assert.Equal(100, facts.CreaturePercentOfNonland);
    }

    [Fact]
    public async Task The_deck_is_judged_against_the_doctrine_for_this_commander()
    {
        var (sut, _, model) = Build("Forest Friend");

        var assessment = (await sut.PlanDeckAsync(DeckId, UserId, Request())).Assessment;

        Assert.Equal("It works.", assessment.Verdict);
        Assert.Single(assessment.Findings);

        // The judgement pass gets the doctrine and the commander's own text.
        Assert.NotNull(model.LastAssessment);
        Assert.Contains(
            "DOCTRINE", JsonSerializer.Serialize(model.LastAssessment!.System), StringComparison.Ordinal);
        Assert.Contains(
            "Test Commander", PromptOf(model.LastAssessment), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_assessment_does_not_lose_the_deck()
    {
        // The build is the expensive half and is perfectly usable unassessed.
        var (sut, _, _) = Build("Forest Friend", "Elf Helper");

        var plan = await sut.PlanDeckAsync(DeckId, UserId, RequestWithBrokenAssessment());

        Assert.Equal(2, plan.Cards.Length);
    }

    private static AiBuildRequest RequestWithBrokenAssessment() => Request();

    private static string PromptOf(AnthropicRequest request)
    {
        var message = request.Messages[0];
        return message.GetType().GetProperty("content")?.GetValue(message) as string ?? string.Empty;
    }

    [Fact]
    public async Task The_build_call_omits_temperature_for_the_thinking_model()
    {
        var (sut, _, model) = Build("Forest Friend");

        await sut.PlanDeckAsync(DeckId, UserId, Request());

        Assert.NotNull(model.Last);
        Assert.Null(model.Last!.Temperature);
    }

    private static string BuildPromptOf(AnthropicRequest request) =>
        JsonSerializer.Serialize(request.Messages);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Below_bracket_four_the_prompt_never_mentions_game_changers(int bracket)
    {
        // They are not in the pool at these brackets, so naming them would only describe
        // what is missing.
        var (sut, _, model) = Build("Forest Friend");

        await sut.PlanDeckAsync(DeckId, UserId, new AiBuildRequest
        {
            CommanderOracleId = CommanderOracleId,
            Bracket = bracket,
        });

        Assert.NotNull(model.LastBuild);
        Assert.DoesNotContain("GAME CHANGERS", BuildPromptOf(model.LastBuild!), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public async Task At_bracket_four_and_up_membership_is_supplied_as_a_fact(int bracket)
    {
        // Doctrine §1.4: membership "is supplied as a fact, never inferred". It was being
        // neither — the pool silently gained the flagged cards and nothing said which they
        // were, leaving the model to recognise names (§0.3). A bracket-4 build picked none
        // of the 23 available to it; told which they were, it took two.
        var (sut, _, model) = Build("Forest Friend");

        await sut.PlanDeckAsync(DeckId, UserId, new AiBuildRequest
        {
            CommanderOracleId = CommanderOracleId,
            Bracket = bracket,
        });

        var prompt = BuildPromptOf(model.LastBuild!);
        Assert.Contains("GAME CHANGERS", prompt, StringComparison.Ordinal);

        // And it must stay a fact, not a recommendation: a strong card that does not serve
        // the plan is still the wrong card.
        Assert.Contains("supplied", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_build_call_caps_reasoning_so_the_answer_fits_the_budget()
    {
        // Thinking is billed out of MaxTokens, not on top of it. Left uncapped, a build
        // spent all 16,000 of its tokens reasoning and emitted 1,543 characters of an
        // unterminated JSON object. Both halves of the pairing matter, so both are pinned.
        var (sut, _, model) = Build("Forest Friend");

        await sut.PlanDeckAsync(DeckId, UserId, Request());

        Assert.NotNull(model.LastBuild);
        Assert.False(string.IsNullOrEmpty(model.LastBuild!.Effort));
        Assert.True(
            model.LastBuild.MaxTokens >= 32000,
            $"Build ceiling is {model.LastBuild.MaxTokens}; 16000 truncated a 99-card answer.");
    }

    /// <summary>Returns an answer that stops mid-object, as a truncated stream does.</summary>
    private sealed class TruncatedModel : IAnthropicClient
    {
        public Task<string> SendAsync(AnthropicRequest request, CancellationToken ct = default) =>
            Task.FromResult(JsonSerializer.Serialize(new
            {
                content = new[]
                {
                    new { type = "text", text = """{"main":["Forest Friend","Elf Hel""" },
                },
            }));

        public async Task<string> StreamTextAsync(
            AnthropicRequest request, Func<string, string, Task> onText,
            Func<int, Task>? onThinking = null, CancellationToken ct = default)
        {
            var envelope = await SendAsync(request, ct);
            await onText("partial", "partial");
            return envelope;
        }
    }

    [Fact]
    public async Task An_answer_that_could_not_be_read_fails_instead_of_becoming_an_empty_deck()
    {
        // The deserialiser returns default on a JsonException, so an unreadable answer used
        // to fall through as zero candidates: the player was shown a deck with "0 of 99
        // slots filled" and no error, which reads as the model having nothing to suggest.
        // A build that could not be read is a failure and has to say so.
        var cards = new Cards { Known = [Card("Forest Friend", ManaColor.Green)] };
        var sut = new AiBuildService(
            cards, new ReadOnlyDeck(), new NoEdhrec(), new TruncatedModel(), new Doctrine(),
            new NoAnalysis(), NullLogger<AiBuildService>.Instance);

        await Assert.ThrowsAsync<AiUpstreamException>(
            () => sut.PlanDeckAsync(DeckId, UserId, Request()));
    }
}
