using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.State;

namespace MtgEngine.Api.Services;

public sealed record LobbyDeckChoice(Guid Id, string Name, string? Format, int CardCount);

public sealed record LobbyDeck(Guid Id, string Name, IReadOnlyList<StartingCard> Library, IReadOnlyList<StartingCard> Command);

public interface IGameLobbyDecks
{
    Task<IReadOnlyList<LobbyDeckChoice>> ListAsync(string account, CancellationToken ct = default);

    Task<LobbyDeck> LoadAsync(Guid deckId, string account, string format, bool allowHonourSystem, CancellationToken ct = default);
}

/// <summary>Loads only the caller's deck. Library contents never belong in a public lobby response.</summary>
public sealed class GameLobbyDecks(MtgEngineDbContext db, DeckGate gate, CommanderDeckAdmission commanderAdmission) : IGameLobbyDecks
{
    private const int MaxCards = 1000;

    public async Task<IReadOnlyList<LobbyDeckChoice>> ListAsync(string account, CancellationToken ct = default) =>
        await db.Collections.AsNoTracking().Where(deck => deck.UserId == account && deck.IsDeck)
            .OrderBy(deck => deck.Name).ThenBy(deck => deck.Id).Take(200)
            .Select(deck => new LobbyDeckChoice(deck.Id, deck.Name, deck.Format,
                deck.Cards.Where(card => card.Board == "main").Sum(card => card.Quantity + card.QuantityFoil + card.QuantityEtched)))
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<LobbyDeck> LoadAsync(Guid deckId, string account, string format, bool allowHonourSystem, CancellationToken ct = default)
    {
        var deck = await db.Collections.AsNoTracking().Include(deck => deck.Cards)
            .SingleOrDefaultAsync(deck => deck.Id == deckId && deck.UserId == account && deck.IsDeck, ct).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("Deck not found.");
        if (!string.Equals(deck.Format, format, StringComparison.OrdinalIgnoreCase))
            throw new InvalidRequestException("Choose a deck matching this table's format.");
        var main = deck.Cards.Where(card => card.Board == "main").OrderBy(card => card.Id).ToArray();
        if (main.Any(card => card.Quantity < 0 || card.QuantityFoil < 0 || card.QuantityEtched < 0 || string.IsNullOrWhiteSpace(card.OracleId)))
            throw new InvalidRequestException("This deck has invalid card quantities or identities.");
        var total = main.Sum(card => (long)card.Quantity + card.QuantityFoil + card.QuantityEtched);
        if (total is < 1 or > MaxCards)
            throw new InvalidRequestException("A playable deck must contain between one and 1000 cards.");
        var cards = main.SelectMany(Expand).ToList();
        var command = new List<StartingCard>();
        if (string.Equals(format, "commander", StringComparison.OrdinalIgnoreCase))
        {
            var commander = deck.CommanderOracleId;
            if (string.IsNullOrWhiteSpace(commander) || !TakeCommander(cards, command, commander))
                throw new InvalidRequestException("Choose a commander present in the deck's main cards.");
            if (!string.IsNullOrWhiteSpace(deck.SecondaryCommanderOracleId))
            {
                if (!TakeCommander(cards, command, deck.SecondaryCommanderOracleId))
                    throw new InvalidRequestException("The second commander must be present in the deck's main cards.");
            }
            await commanderAdmission.ValidateAsync([.. cards.Concat(command).Select(card => card.OracleId)],
                [.. command.Select(card => card.OracleId)], ct).ConfigureAwait(false);
        }
        gate.Admit([.. cards.Concat(command).Select(card => card.OracleId)], allowHonourSystem);
        return new LobbyDeck(deck.Id, deck.Name, cards, command);
    }

    private static IEnumerable<StartingCard> Expand(CollectionCard entry) =>
        Enumerable.Repeat(new StartingCard(entry.OracleId, entry.ScryfallId), entry.Quantity)
            .Concat(Enumerable.Repeat(new StartingCard(entry.OracleId, entry.ScryfallId, CardFinish.Foil), entry.QuantityFoil))
            .Concat(Enumerable.Repeat(new StartingCard(entry.OracleId, entry.ScryfallId, CardFinish.Etched), entry.QuantityEtched));

    private static bool TakeCommander(List<StartingCard> cards, List<StartingCard> commanders, string oracleId)
    {
        var index = cards.FindIndex(card => string.Equals(card.OracleId, oracleId, StringComparison.Ordinal));
        if (index < 0)
            return false;
        commanders.Add(cards[index]);
        cards.RemoveAt(index);
        return true;
    }
}
