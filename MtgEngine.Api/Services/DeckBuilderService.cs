using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

public interface IDeckBuilderService
{
    Task<IReadOnlyList<Card>> BuildDeckAsync(string[] presets, Guid ownerId);
    Task<(IReadOnlyList<Card> Deck, Card Commander)> BuildCommanderDeckAsync(string preset, Guid ownerId);
    Task<IReadOnlyList<Card>> BuildFromSavedDeckAsync(Guid collectionId, Guid ownerId);
    Task<(IReadOnlyList<Card> Deck, Card Commander)> BuildCommanderFromSavedDeckAsync(Guid collectionId, Guid ownerId);
}

/// <summary>
/// Builds playable decks from preset names or user-saved decks.
/// </summary>
public sealed class DeckBuilderService : IDeckBuilderService
{
    private readonly IScryfallService _scryfall;
    private readonly MtgEngineDbContext _db;

    public DeckBuilderService(IScryfallService scryfall, MtgEngineDbContext db)
    {
        _db = db;
        _scryfall = scryfall;
    }

    public async Task<IReadOnlyList<Card>> BuildDeckAsync(string[] presets, Guid ownerId)
    {
        var preset = presets.FirstOrDefault() ?? "mono-green";
        var list = GetDeckList(preset);
        var cards = new List<Card>();

        foreach (var (name, count) in list)
        {
            var def = await _scryfall.GetByNameAsync(name);
            if (def is null)
                continue;
            for (int i = 0; i < count; i++)
                cards.Add(new Card { Definition = def, OwnerId = ownerId });
        }

        return cards;
    }

    public async Task<(IReadOnlyList<Card> Deck, Card Commander)> BuildCommanderDeckAsync(string preset, Guid ownerId)
    {
        var (deckList, commanderName) = GetCommanderDeckList(preset);

        var commanderDef = await _scryfall.GetByNameAsync(commanderName)
            ?? throw new InvalidOperationException($"Commander '{commanderName}' not found in card database.");
        var commander = new Card { Definition = commanderDef, OwnerId = ownerId };

        var deck = new List<Card>();
        foreach (var (name, count) in deckList)
        {
            var def = await _scryfall.GetByNameAsync(name);
            if (def is null)
                continue;
            for (int i = 0; i < count; i++)
                deck.Add(new Card { Definition = def, OwnerId = ownerId });
        }

        return (deck, commander);
    }

    // ---- Saved deck methods --------------------------------

    public async Task<IReadOnlyList<Card>> BuildFromSavedDeckAsync(Guid collectionId, Guid ownerId)
    {
        var collection = await _db.Collections
            .Include(c => c.Cards)
            .FirstOrDefaultAsync(c => c.Id == collectionId && c.IsDeck)
            ?? throw new InvalidOperationException($"Saved deck {collectionId} not found.");

        var cards = new List<Card>();
        foreach (var cc in collection.Cards.Where(c => c.Board == "main"))
        {
            var def = await _scryfall.GetByOracleIdAsync(cc.OracleId);
            if (def is null)
                continue;
            for (int i = 0; i < cc.Quantity; i++)
                cards.Add(new Card { Definition = def, OwnerId = ownerId });
        }
        return cards;
    }

    public async Task<(IReadOnlyList<Card> Deck, Card Commander)> BuildCommanderFromSavedDeckAsync(Guid collectionId, Guid ownerId)
    {
        var collection = await _db.Collections
            .Include(c => c.Cards)
            .FirstOrDefaultAsync(c => c.Id == collectionId && c.IsDeck)
            ?? throw new InvalidOperationException($"Saved deck {collectionId} not found.");

        if (string.IsNullOrEmpty(collection.CommanderOracleId))
            throw new InvalidOperationException($"Deck '{collection.Name}' has no commander set.");

        var commanderDef = await _scryfall.GetByOracleIdAsync(collection.CommanderOracleId)
            ?? throw new InvalidOperationException($"Commander oracle ID {collection.CommanderOracleId} not found.");
        var commander = new Card { Definition = commanderDef, OwnerId = ownerId };

        var deck = new List<Card>();
        foreach (var cc in collection.Cards.Where(c => c.Board == "main"))
        {
            var def = await _scryfall.GetByOracleIdAsync(cc.OracleId);
            if (def is null)
                continue;
            for (int i = 0; i < cc.Quantity; i++)
                deck.Add(new Card { Definition = def, OwnerId = ownerId });
        }
        return (deck, commander);
    }

    // ---- Hard-coded preset deck lists ---------------------

    private static IReadOnlyList<(string Name, int Count)> GetDeckList(string preset) =>
        preset switch
        {
            "mono-green" => MonoGreen,
            "mono-red" => MonoRed,
            "wu-flyers" => WuFlyers,
            "rb-control" => RbControl,
            "gw-tokens" => GwTokens,
            _ => MonoGreen,
        };

    // ---- Commander deck lists ----------------------------

    private static readonly Dictionary<string, string> CommanderNames = new()
    {
        ["gruul-cmdr"] = "Ruric Thar, the Unbowed",
        ["rakdos-cmdr"] = "Kaalia of the Vast",
        ["simic-cmdr"] = "Zegana, Utopian Speaker",
        ["esper-cmdr"] = "Sharuum the Hegemon",
    };

    private static (IReadOnlyList<(string, int)> Deck, string CommanderName) GetCommanderDeckList(string preset) =>
        preset switch
        {
            "gruul-cmdr" => (GruulCmdr, CommanderNames["gruul-cmdr"]),
            "rakdos-cmdr" => (RakdosCmdr, CommanderNames["rakdos-cmdr"]),
            "simic-cmdr" => (SimicCmdr, CommanderNames["simic-cmdr"]),
            "esper-cmdr" => (EsperCmdr, CommanderNames["esper-cmdr"]),
            _ => (GruulCmdr, CommanderNames["gruul-cmdr"]),
        };

    // ---- Gruul Smash (Ruric Thar, the Unbowed – 6/6 reach, vigilance) -----
    // Deck is ~59 cards; commander is excluded from the library
    private static readonly IReadOnlyList<(string, int)> GruulCmdr =
    [
        ("Mountain", 16),
        ("Forest",   16),
        ("Llanowar Elves", 4),
        ("Elvish Mystic",  4),
        ("Grizzly Bears",  4),
        ("Giant Growth",   3),
        ("Titanic Growth", 3),
        ("Lightning Bolt", 4),
        ("Shock",          3),
        ("Volcanic Dragon", 2),
    ];

    // ---- Rakdos Angels (Kaalia of the Vast – 2/2 flying, haste) -----------
    private static readonly IReadOnlyList<(string, int)> RakdosCmdr =
    [
        ("Mountain", 12),
        ("Swamp",    12),
        ("Plains",   9),
        ("Lightning Bolt",  4),
        ("Shock",           4),
        ("Terminate",       4),
        ("Serra Angel",     4),
        ("Shivan Dragon",   4),
        ("Gray Merchant of Asphodel", 3),
        ("Dark Ritual",     3),
    ];

    // ---- Simic Growth (Zegana, Utopian Speaker – 4/4 trample) -------------
    private static readonly IReadOnlyList<(string, int)> SimicCmdr =
    [
        ("Island", 14),
        ("Forest", 14),
        ("Llanowar Elves",   4),
        ("Elvish Mystic",    4),
        ("Counterspell",     4),
        ("Mana Leak",        4),
        ("Giant Growth",     4),
        ("Garruk's Companion", 4),
        ("Titanic Growth",   3),
        ("Divination",       4),
    ];

    // ---- Esper Control (Sharuum the Hegemon – 5/5 flying) ------------------
    private static readonly IReadOnlyList<(string, int)> EsperCmdr =
    [
        ("Plains",  11),
        ("Island",  11),
        ("Swamp",   11),
        ("Serra Angel",      4),
        ("Counterspell",     4),
        ("Mana Leak",        4),
        ("Inquisition of Kozilek", 4),
        ("Pacifism",         4),
        ("Divination",       4),
        ("Gray Merchant of Asphodel", 2),
    ];

    // 40-card Mono Green Stompy
    private static readonly IReadOnlyList<(string, int)> MonoGreen =
    [
        ("Forest", 17),
        ("Llanowar Elves", 4),
        ("Elvish Mystic", 4),
        ("Grizzly Bears", 4),
        ("Wyluli Wolf", 2),
        ("Giant Growth", 4),
        ("Titanic Growth", 2),
        ("Garruk's Companion", 3),
    ];

    // 40-card Mono Red Aggro
    private static readonly IReadOnlyList<(string, int)> MonoRed =
    [
        ("Mountain", 17),
        ("Goblin Guide", 4),
        ("Monastery Swiftspear", 4),
        ("Lightning Bolt", 4),
        ("Shock", 4),
        ("Rift Bolt", 3),
        ("Light Up the Stage", 4),
    ];

    // 40-card White Blue Flyers
    private static readonly IReadOnlyList<(string, int)> WuFlyers =
    [
        ("Plains", 9),
        ("Island", 8),
        ("Aven Cloudchaser", 3),
        ("Warden of Evos Isle", 3),
        ("Winged Coatl", 3),
        ("Counterspell", 4),
        ("Mana Leak", 4),
        ("Pacifism", 3),
        ("Divination", 3),
    ];

    // 40-card Red Black Control
    private static readonly IReadOnlyList<(string, int)> RbControl =
    [
        ("Swamp", 9),
        ("Mountain", 8),
        ("Lightning Bolt", 4),
        ("Inquisition of Kozilek", 4),
        ("Terminate", 4),
        ("Blightning", 3),
        ("Gray Merchant of Asphodel", 4),
        ("Dark Ritual", 4),
    ];

    // 40-card Green White Tokens
    private static readonly IReadOnlyList<(string, int)> GwTokens =
    [
        ("Plains", 9),
        ("Forest", 8),
        ("Llanowar Elves", 4),
        ("Intangible Virtue", 3),
        ("Raise the Alarm", 4),
        ("Midnight Haunting", 3),
        ("Overrun", 3),
        ("Selesnya Charm", 3),
        ("Trostani's Judgment", 3),
    ];
}
