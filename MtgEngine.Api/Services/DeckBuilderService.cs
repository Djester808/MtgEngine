using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Domain.ValueObjects;

namespace MtgEngine.Api.Services;

public interface IDeckBuilderService
{
    Task<IReadOnlyList<Card>> BuildDeckAsync(string[] presets, Guid ownerId);
}

/// <summary>
/// Builds playable decks from preset names.
/// In v1 these are hard-coded 40-card decks. In v2, wire to Scryfall + player deck lists.
/// </summary>
public sealed class DeckBuilderService : IDeckBuilderService
{
    private readonly IScryfallService _scryfall;

    public DeckBuilderService(IScryfallService scryfall)
    {
        _scryfall = scryfall;
    }

    public async Task<IReadOnlyList<Card>> BuildDeckAsync(string[] presets, Guid ownerId)
    {
        var preset = presets.FirstOrDefault() ?? "mono-green";
        var list   = GetDeckList(preset);
        var cards  = new List<Card>();

        foreach (var (name, count) in list)
        {
            var def = await _scryfall.GetByNameAsync(name);
            if (def is null) continue;
            for (int i = 0; i < count; i++)
                cards.Add(new Card { Definition = def, OwnerId = ownerId });
        }

        return cards;
    }

    // ---- Hard-coded preset deck lists ---------------------

    private static IReadOnlyList<(string Name, int Count)> GetDeckList(string preset) =>
        preset switch
        {
            "mono-green" => MonoGreen,
            "mono-red"   => MonoRed,
            "wu-flyers"  => WuFlyers,
            "rb-control" => RbControl,
            "gw-tokens"  => GwTokens,
            _ => MonoGreen,
        };

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
