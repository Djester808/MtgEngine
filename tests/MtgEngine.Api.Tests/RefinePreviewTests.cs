using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Tests;

/// <summary>
/// Refine proposes before it writes.
/// </summary>
/// <remarks>
/// It rewrote a saved deck in place, with no confirmation and no undo. The builder tells
/// the player on screen that nothing is saved until they accept it, and the whole
/// plan-review-accept flow exists to honour that — an "improve this deck" button that
/// quietly swapped ten cards would be the opposite promise from the same feature.
/// <para>
/// The preview runs the identical validation ladder, so what comes back is what would
/// actually land rather than what the model wished for. Accepting it costs no model call:
/// the swaps go to the apply path, which validates them again because they arrive from the
/// caller like any other request.
/// </para>
/// </remarks>
public sealed class RefinePreviewTests
{
    private static readonly Guid DeckId = Guid.NewGuid();
    private const string UserId = "user-1";
    private const string CommanderOracleId = "oracle-commander";

    private static CardDefinition Card(string name, params ManaColor[] colors) => new()
    {
        OracleId = "oracle-" + name.ToLowerInvariant().Replace(' ', '-'),
        Name = name,
        OracleText = "A card.",
        ColorIdentity = colors.Length == 0 ? [ManaColor.Green] : colors,
        CardTypes = CardType.Creature,
        Cmc = 2,
        Legalities = new Dictionary<string, string> { ["commander"] = "legal" },
    };

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

    private sealed class Cards : StubScryfallService
    {
        public required IReadOnlyList<CardDefinition> Known { get; init; }

        public override Task<CardDefinition?> GetByOracleIdAsync(string oracleId) =>
            Task.FromResult(Known.Concat([Commander()]).FirstOrDefault(c => c.OracleId == oracleId));

        public override Task<CardDefinition?> GetByNameAsync(string name) =>
            Task.FromResult(Known.FirstOrDefault(
                c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)));

        public override Task<PrintingDto[]> GetPrintingsAsync(string oracleId) =>
            Task.FromResult<PrintingDto[]>([new() { ScryfallId = "print-" + oracleId }]);

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

    /// <summary>Counts every write. Zero is the assertion for a preview.</summary>
    private sealed class CountingDeck : StubCollectionService
    {
        public int Adds { get; private set; }
        public int Removes { get; private set; }

        public override Task<DeckDetailDto?> GetDeckAsync(Guid deckId, string userId) =>
            Task.FromResult<DeckDetailDto?>(new DeckDetailDto
            {
                Id = deckId,
                Name = "Deck",
                CommanderOracleId = CommanderOracleId,
                Cards =
                [
                    new CollectionCardDto
                    {
                        OracleId = "oracle-weak-card",
                        Board = "main",
                        Quantity = 1,
                        CardDetails = new CardDto { Name = "Weak Card", OracleId = "oracle-weak-card" },
                    },
                ],
            });

        public override Task<(CollectionCardDto Card, bool Created)> AddCardToCollectionAsync(
            Guid collectionId, string userId, AddCardToCollectionRequest request)
        {
            Adds++;
            return Task.FromResult((new CollectionCardDto { OracleId = request.OracleId }, true));
        }

        public override Task<bool> RemoveCardByOracleAsync(
            Guid collectionId, string oracleId, string userId, string? board = null)
        {
            Removes++;
            return Task.FromResult(true);
        }
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

    /// <summary>Always proposes swapping Weak Card for Strong Card.</summary>
    private sealed class OneSwap : IAnthropicClient
    {
        public int Calls { get; private set; }

        public Task<string> SendAsync(AnthropicRequest request, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                content = new[]
                {
                    new
                    {
                        type = "text",
                        text = """{"swaps":[{"out":"Weak Card","in":"Strong Card","why":"Better rate."}]}""",
                    },
                },
            }));
        }

        public Task<string> StreamTextAsync(
            AnthropicRequest request, Func<string, string, Task> onText,
            Func<int, Task>? onThinking = null, CancellationToken ct = default) =>
            SendAsync(request, ct);
    }

    private static (AiBuildService Sut, CountingDeck Deck, OneSwap Model) Build()
    {
        var deck = new CountingDeck();
        var model = new OneSwap();
        var cards = new Cards { Known = [Card("Weak Card"), Card("Strong Card")] };

        return (
            new AiBuildService(cards, deck, new NoEdhrec(), model, new Doctrine(), new NoAnalysis(),
                NullLogger<AiBuildService>.Instance),
            deck,
            model);
    }

    [Fact]
    public async Task A_preview_writes_nothing()
    {
        var (sut, deck, _) = Build();

        var result = await sut.RefineDeckAsync(DeckId, UserId, new AiRefineRequest { Preview = true });

        Assert.Equal(0, deck.Adds);
        Assert.Equal(0, deck.Removes);
        Assert.Single(result.Swaps);
        Assert.Equal("Strong Card", result.Swaps[0].In);
    }

    [Fact]
    public async Task Without_preview_it_still_writes()
    {
        // The existing endpoint keeps working; preview is opt-in.
        var (sut, deck, _) = Build();

        await sut.RefineDeckAsync(DeckId, UserId, new AiRefineRequest());

        Assert.Equal(1, deck.Adds);
        Assert.Equal(1, deck.Removes);
    }

    [Fact]
    public async Task Accepting_a_preview_costs_no_model_call()
    {
        // The whole point of previewing: the model already answered.
        var (sut, deck, model) = Build();

        await sut.ApplySwapsAsync(DeckId, UserId, new AiApplySwapsRequest
        {
            Swaps = [new CardSwapDto { Out = "Weak Card", In = "Strong Card", Why = "Better rate." }],
            Bracket = 3,
        });

        Assert.Equal(0, model.Calls);
        Assert.Equal(1, deck.Adds);
        Assert.Equal(1, deck.Removes);
    }

    [Fact]
    public async Task An_accepted_swap_is_validated_like_any_other_request()
    {
        // It arrives from the caller. That we proposed it a moment ago is not a reason to
        // trust it — the name could be anything by the time it comes back.
        var (sut, deck, _) = Build();

        var result = await sut.ApplySwapsAsync(DeckId, UserId, new AiApplySwapsRequest
        {
            Swaps = [new CardSwapDto { Out = "Weak Card", In = "Card That Does Not Exist", Why = "" }],
            Bracket = 3,
        });

        Assert.Empty(result.Swaps);
        Assert.Equal(0, deck.Adds);
        Assert.NotEmpty(result.RejectedByReason);
    }

    [Fact]
    public async Task A_swap_out_of_a_card_the_deck_does_not_hold_is_refused()
    {
        var (sut, deck, _) = Build();

        var result = await sut.ApplySwapsAsync(DeckId, UserId, new AiApplySwapsRequest
        {
            Swaps = [new CardSwapDto { Out = "Not In The Deck", In = "Strong Card", Why = "" }],
            Bracket = 3,
        });

        Assert.Empty(result.Swaps);
        Assert.Equal(0, deck.Removes);
        Assert.True(result.RejectedByReason.ContainsKey("out-card-not-in-deck"));
    }
}
