using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Tests;

/// <summary>
/// What the refine pass is told about the deck it is changing.
/// </summary>
/// <remarks>
/// It was told nothing. Refine received the commander, the bracket, the price tier, a legal
/// pool and a flat list of card names, then was asked which cards were weakest — while the
/// doctrine it reasons from spends §2 and §3 on counts: lands, ramp, card advantage,
/// interaction, mana sources, creature density, coloured sources. It was being asked to
/// check numbers it could not see.
/// <para>
/// That is the same shape as two defects already fixed here. Game Changer membership is a
/// flag on the card data and the build prompt never sent it, so a bracket-4 deck took none
/// of the twenty-three available to it. Prices are on the card data and the budget tier only
/// asked in prose, so a budget build came back with cards at sixteen dollars. In both cases
/// the system held the fact and asked the model to recall it.
/// </para>
/// </remarks>
public sealed class RefineProfileTests
{
    private static readonly Guid DeckId = Guid.NewGuid();
    private const string UserId = "user-1";
    private const string CommanderOracleId = "oracle-commander";

    private static CardDefinition Commander() => new()
    {
        OracleId = CommanderOracleId,
        Name = "Test Commander",
        OracleText = "Does a thing.",
        ColorIdentity = [ManaColor.Green],
        CardTypes = CardType.Creature,
        Supertypes = ["Legendary"],
        Legalities = new Dictionary<string, string> { ["commander"] = "legal" },
    };

    private static CardDefinition Land(string name) => new()
    {
        OracleId = "oracle-" + name,
        Name = name,
        ColorIdentity = [ManaColor.Green],
        CardTypes = CardType.Land,
        Legalities = new Dictionary<string, string> { ["commander"] = "legal" },
    };

    private static CardDefinition Creature(string name) => new()
    {
        OracleId = "oracle-" + name,
        Name = name,
        OracleText = "A body.",
        ColorIdentity = [ManaColor.Green],
        CardTypes = CardType.Creature,
        Cmc = 3,
        Legalities = new Dictionary<string, string> { ["commander"] = "legal" },
    };

    private sealed class Cards : StubScryfallService
    {
        public required IReadOnlyList<CardDefinition> Known { get; init; }

        public override Task<CardDefinition?> GetByOracleIdAsync(string oracleId) =>
            Task.FromResult(Known.Concat([Commander()]).FirstOrDefault(c => c.OracleId == oracleId));

        public override Task<CardDefinition?> GetByNameAsync(string name) =>
            Task.FromResult(Known.FirstOrDefault(
                c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)));

        public override Task<IReadOnlyDictionary<CardRole, string[]>> GetLegalCardsByRoleAsync(
            IReadOnlySet<ManaColor> commanderColors, int bracket, decimal? maxUsd = null) =>
            Task.FromResult<IReadOnlyDictionary<CardRole, string[]>>(
                new Dictionary<CardRole, string[]> { [CardRole.Other] = [.. Known.Select(c => c.Name)] });

        public override Task<(CardDefinition[] Cards, int Total)> GetCandidatePoolAsync(
            IReadOnlySet<ManaColor> commanderColors, CardDefinition? commander = null,
            string? query = null, IReadOnlySet<string>? setCodes = null,
            bool gameChangersOnly = false, CardType types = CardType.None,
            int? cmcMin = null, int? cmcMax = null, int limit = 50, int offset = 0) =>
            Task.FromResult((Array.Empty<CardDefinition>(), 0));
    }

    /// <summary>A deck of two lands and one creature, one of the lands held four deep.</summary>
    private sealed class SavedDeck : StubCollectionService
    {
        public override Task<DeckDetailDto?> GetDeckAsync(Guid deckId, string userId) =>
            Task.FromResult<DeckDetailDto?>(new DeckDetailDto
            {
                Id = deckId,
                Name = "Deck",
                CommanderOracleId = CommanderOracleId,
                Cards =
                [
                    Row("Forest", 4),
                    Row("Grove", 1),
                    Row("Bear", 1),
                ],
            });

        private static CollectionCardDto Row(string name, int quantity) => new()
        {
            OracleId = "oracle-" + name,
            Board = "main",
            Quantity = quantity,
            CardDetails = new CardDto { Name = name, OracleId = "oracle-" + name },
        };
    }

    private sealed class Doctrine : ICommanderDoctrine
    {
        public string Text => "DOCTRINE";
        public int ApproximateTokens => 1;
    }

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

    /// <summary>Proposes nothing, and keeps the prompt it was handed.</summary>
    private sealed class Recorder : IAnthropicClient
    {
        public AnthropicRequest? Last { get; private set; }

        public Task<string> SendAsync(AnthropicRequest request, CancellationToken ct = default)
        {
            Last = request;
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                content = new[] { new { type = "text", text = """{"swaps":[]}""" } },
            }));
        }

        public Task<string> StreamTextAsync(
            AnthropicRequest request, Func<string, string, Task> onText,
            Func<int, Task>? onThinking = null, CancellationToken ct = default) =>
            SendAsync(request, ct);
    }

    private static (AiBuildService Sut, Recorder Model) Build()
    {
        var model = new Recorder();
        var cards = new Cards { Known = [Land("Forest"), Land("Grove"), Creature("Bear")] };
        return (
            new AiBuildService(cards, new SavedDeck(), new NoEdhrec(), model, new Doctrine(),
                new NoAnalysis(), NullLogger<AiBuildService>.Instance),
            model);
    }

    private static string PromptOf(AnthropicRequest request)
    {
        var message = request.Messages[0];
        return message.GetType().GetProperty("content")?.GetValue(message) as string ?? string.Empty;
    }

    [Fact]
    public async Task Refine_is_told_what_the_deck_measures()
    {
        var (sut, model) = Build();

        await sut.RefineDeckAsync(DeckId, UserId, new AiRefineRequest { Bracket = 3 });

        var prompt = PromptOf(model.Last!);
        Assert.Contains("DECK PROFILE", prompt, StringComparison.Ordinal);
        Assert.Contains("Lands", prompt, StringComparison.Ordinal);
        Assert.Contains("Mana sources", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_row_held_several_deep_counts_once_per_copy()
    {
        // Four Forests and one Grove is five lands. Counting rows rather than copies would
        // report two, and understate the mana base by more than half — in exactly the
        // number the profile exists to let the model check.
        var (sut, model) = Build();

        await sut.RefineDeckAsync(DeckId, UserId, new AiRefineRequest { Bracket = 3 });

        Assert.Contains("Lands 5", PromptOf(model.Last!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_profile_states_facts_and_never_a_target()
    {
        // The doctrine's bands move with the deck — land count follows the curve (§2.2) and
        // the value of mass removal inverts with creature density (§6.4) — so a fixed table
        // here would tell the model a correctly-built deck was wrong.
        var (sut, model) = Build();

        await sut.RefineDeckAsync(DeckId, UserId, new AiRefineRequest { Bracket = 3 });

        var profile = PromptOf(model.Last!)
            .Split("DECK PROFILE")[1]
            .Split("CURRENT DECK")[0];

        Assert.DoesNotContain("36-38", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("should", profile, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("target", profile, StringComparison.OrdinalIgnoreCase);
    }
}
