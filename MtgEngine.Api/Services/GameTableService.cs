using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Api.Dtos;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.Engine;

namespace MtgEngine.Api.Services;

/// <summary>
/// Turns two saved decks into a game.
/// </summary>
/// <remarks>
/// The legality gate lives here: a deck containing a card the engine does not implement cannot be
/// played. That was a deliberate choice over the alternatives — blocking is the only one of them
/// that never produces a game that is quietly wrong. A card treated as vanilla looks like it
/// works and does not, and players cannot tell which of the two they are looking at.
/// </remarks>
public sealed class GameTableService
{
    private readonly MtgEngineDbContext _db;
    private readonly BulkDataService _cards;
    private readonly IAbilitySource _abilities;
    private readonly GameSessionService _sessions;

    public GameTableService(
        MtgEngineDbContext db,
        BulkDataService cards,
        IAbilitySource abilities,
        GameSessionService sessions)
    {
        _db = db;
        _cards = cards;
        _abilities = abilities;
        _sessions = sessions;
    }

    /// <summary>Starts a game, or explains why these decks cannot play one.</summary>
    /// <remarks>
    /// Each deck is loaded against its own owner, so a caller cannot start a game with somebody
    /// else's deck by naming its id.
    /// </remarks>
    public async Task<Guid> StartAsync(
        Guid firstUserId,
        Guid firstDeckId,
        Guid secondUserId,
        Guid secondDeckId,
        int startingLife,
        CancellationToken ct = default)
    {
        var first = await LoadAsync(firstDeckId, firstUserId, ct).ConfigureAwait(false);
        var second = await LoadAsync(secondDeckId, secondUserId, ct).ConfigureAwait(false);

        // A deck that names a commander plays as Commander: the card goes to the command zone
        // before the shuffle (CR 903.6), casting it from there is taxed (CR 903.8), and
        // twenty-one damage from it is a loss (CR 903.10a). The builder already records which
        // card that is, so a Commander deck plays as one without anybody choosing a format.
        var setups = new List<PlayerSetup>
        {
            new(firstUserId, first.Name, startingLife, first.Cards)
            {
                CommanderOracleId = first.CommanderOracleId,
            },
            new(secondUserId, second.Name, startingLife, second.Cards)
            {
                CommanderOracleId = second.CommanderOracleId,
            },
        };

        return _sessions.Create(setups);
    }

    /// <summary>The caller's decks, for the lobby to offer.</summary>
    public async Task<PlayableDeckDto[]> PlayableDecksAsync(
        Guid userId, CancellationToken ct = default)
    {
        var decks = await _db.Collections
            .AsNoTracking()
            .Where(c => c.IsDeck && c.UserId == userId.ToString())
            .Select(c => new PlayableDeckDto(
                c.Id,
                c.Name,
                c.Cards.Where(card => card.Board == "main").Sum(card => card.Quantity + card.QuantityFoil)))
            .ToArrayAsync(ct)
            .ConfigureAwait(false);

        return decks;
    }

    /// <summary>
    /// Which cards in a deck the engine cannot play yet.
    /// </summary>
    /// <remarks>
    /// A card counts as playable when it needs no behaviour at all — a vanilla creature or a
    /// basic land does exactly what the rules already do — or when the pool has a definition for
    /// it. Anything with rules text and no definition is named, so the answer to "why can't I
    /// play this deck" is a list of cards rather than a refusal.
    /// </remarks>
    public IReadOnlyList<string> Unsupported(IEnumerable<CardDefinition> cards)
    {
        ArgumentNullException.ThrowIfNull(cards);

        return
        [
            .. cards
                .Where(card => !IsPlayable(card))
                .Select(card => card.Name)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];
    }

    private bool IsPlayable(CardDefinition card)
    {
        // Nothing to implement: the rules already handle a body with no text on it.
        if (string.IsNullOrWhiteSpace(card.OracleText))
            return true;

        return _abilities.SpellOf(card) is not null
            || _abilities.TriggersOf(card).Count > 0
            || _abilities.StaticsOf(card).Count > 0
            || _abilities.ActivatedOf(card).Count > 0
            || _abilities.ReplacementsOf(card).Count > 0;
    }

    private async Task<(string Name, List<CardDefinition> Cards, string? CommanderOracleId)> LoadAsync(
        Guid deckId, Guid ownerId, CancellationToken ct)
    {
        var deck = await _db.Collections
            .AsNoTracking()
            .Include(c => c.Cards)
            .FirstOrDefaultAsync(c => c.Id == deckId && c.IsDeck, ct)
            .ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("That deck does not exist.");

        if (!string.Equals(deck.UserId, ownerId.ToString(), StringComparison.OrdinalIgnoreCase))
            throw new ResourceNotFoundException("That deck does not exist.");

        var cards = new List<CardDefinition>();
        var missing = new List<string>();

        // CR 100.4: the sideboard is outside the game, so only the main deck is dealt.
        foreach (var entry in deck.Cards.Where(c =>
            string.Equals(c.Board, "main", StringComparison.OrdinalIgnoreCase)))
        {
            var card = await _cards.GetByOracleIdAsync(entry.OracleId).ConfigureAwait(false);
            if (card is null)
            {
                missing.Add(entry.OracleId);
                continue;
            }

            for (var i = 0; i < entry.Quantity + entry.QuantityFoil; i++)
                cards.Add(card);
        }

        if (missing.Count > 0)
        {
            throw new InvalidResourceStateException(
                $"{deck.Name} has {missing.Count} card(s) that are not in the card database.");
        }

        var unsupported = Unsupported(cards);
        if (unsupported.Count > 0)
        {
            // Named rather than counted: "why can't I play this deck" deserves a list.
            throw new InvalidResourceStateException(
                $"The engine does not implement {unsupported.Count} card(s) in {deck.Name} yet: "
                + string.Join(", ", unsupported.Take(10))
                + (unsupported.Count > 10 ? ", …" : string.Empty));
        }

        if (cards.Count < 2)
            throw new InvalidResourceStateException($"{deck.Name} has too few cards to play.");

        // Only a commander that is actually in the deck counts: a deck carrying a stale
        // commanderOracleId whose card has been removed is not a Commander deck, and starting
        // one would fail at the command zone rather than here.
        var commander = deck.CommanderOracleId;
        if (!string.IsNullOrEmpty(commander)
            && !cards.Any(c => string.Equals(c.OracleId, commander, StringComparison.Ordinal)))
        {
            commander = null;
        }

        return (deck.Name, cards, commander);
    }
}
