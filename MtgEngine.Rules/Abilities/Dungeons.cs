using System.Collections.Immutable;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Abilities;

/// <summary>One room of a dungeon: what it does, and where its arrows lead (CR 309.4).</summary>
/// <remarks>
/// The room's own name is flavour text and affects nothing (CR 309.4b) — but it is what the
/// venture marker is recorded as sitting on, so it has to be stable and unique within its
/// dungeon. Rooms are identified by name rather than by index because a log that said "room 4"
/// would be unreadable, and would silently mean a different room if the list were ever reordered.
/// </remarks>
public sealed record DungeonRoom
{
    public required string Name { get; init; }

    /// <summary>The effect printed on the card, which is the room ability's text (CR 309.4c).</summary>
    public required string Text { get; init; }

    public required ImmutableList<IEffect> Effects { get; init; }

    public ImmutableList<TargetSpec> Targets { get; init; } = [];

    /// <summary>
    /// The rooms an arrow points to (CR 309.4). Empty on the bottommost room, which is what
    /// makes it the bottommost — there is no separate flag to fall out of step with the arrows.
    /// </summary>
    public ImmutableList<string> LeadsTo { get; init; } = [];
}

/// <summary>A dungeon card, as the rules print it rather than as any card carries it.</summary>
public sealed record DungeonDefinition
{
    public required string Name { get; init; }

    public required ImmutableList<DungeonRoom> Rooms { get; init; }

    /// <summary>The topmost room, where the venture marker starts (CR 309.4a).</summary>
    public DungeonRoom Top => Rooms[0];

    public DungeonRoom? Room(string name) =>
        Rooms.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal));
}

/// <summary>
/// The dungeons a player can venture into (CR 309), written out here rather than compiled.
/// </summary>
/// <remarks>
/// The same decision the Ring records, one mechanic along: a dungeon card is not a permanent,
/// cannot be cast, never leaves the command zone except to leave the game (CR 309.2c), and is not
/// part of anybody's deck (CR 309.2). No card in the corpus carries a room's text — the three
/// dungeon cards are in Scryfall's bulk data and legal in no format, so they never enter the
/// corpus at all — and there is nothing for the compiler to read.
/// <para>
/// It is a real object in the command zone all the same, and that is the point of doing it this
/// way rather than resolving a room's effect inside the venture. CR 309.4c makes each room a
/// <em>triggered ability</em> whose source is the dungeon card, so it uses the stack, can be
/// responded to, and chooses targets. Storeroom's "put a +1/+1 counter on target creature" has
/// nowhere to put a target if the room is not an ability, and a room that quietly aimed at
/// something the player did not choose would be a different card.
/// </para>
/// <para>
/// <b>One dungeon ships, and the other three are named below with the room that blocks each.</b>
/// A dungeon with a room the engine cannot express would be a game that stops doing anything at
/// that room, which is worse than a dungeon nobody owns: a player who brought one dungeon card is
/// playing a legal game of Magic, and every "venture into the dungeon" card is correct for them.
/// </para>
/// </remarks>
public static class Dungeons
{
    /// <summary>Lost Mine of Phandelver (CR 309), seven rooms and three forks.</summary>
    /// <remarks>
    /// The one dungeon whose every room the effect vocabulary can say. The other three each have
    /// at least one room that it cannot, and each is named here so the next pass does not have to
    /// re-derive the measurement:
    /// <list type="bullet">
    /// <item><b>Undercity</b> — "Throne of the Dead Three: reveal the top ten cards of your
    /// library, put a creature card from among them onto the battlefield with three +1/+1
    /// counters on it, it gains hexproof until your next turn, then shuffle." Four instructions
    /// welded together, none of which any existing effect performs on a card found by looking.
    /// This is also why <b>the initiative</b> (CR 726) is not built: all three of its inherent
    /// abilities venture into Undercity by name, so an initiative without Undercity would send
    /// players into a dungeon that stops working at its last room.</item>
    /// <item><b>Dungeon of the Mad Mage</b> — "Twisted Caverns: target creature can't attack
    /// until your next turn" (no can't-attack continuous effect exists) and "Mad Wizard's Lair:
    /// draw three cards and reveal them, you may cast one of them without paying its mana
    /// cost".</item>
    /// <item><b>Tomb of Annihilation</b> — three of its five rooms are "each player loses N life
    /// unless they [do something]", which is a payment offered to every player at once rather
    /// than to the controller, and no effect offers one that way.</item>
    /// </list>
    /// Adding any of them later is data in this file plus the effect each blocked room needs;
    /// none of them wants anything further from the machinery around it.
    /// </remarks>
    public const string LostMineOfPhandelver = "Lost Mine of Phandelver";

    /// <summary>
    /// Which dungeon a player choosing one gets (CR 701.49a).
    /// </summary>
    /// <remarks>
    /// The rule has the player choose a dungeon card they own from outside the game. This engine
    /// has no outside-the-game zone and ships one dungeon, so the choice has exactly one answer —
    /// which is the game a player who brought one dungeon card is playing, not a simplification
    /// of it. When a second dungeon ships this becomes a choice, like the room fork below.
    /// </remarks>
    public const string Default = LostMineOfPhandelver;

    /// <summary>The prefix the command-zone object's oracle id carries, so a log can name it.</summary>
    private const string OracleIdPrefix = "dungeon-";

    /// <summary>A 1/1 red Goblin, which no card describes because the dungeon does.</summary>
    private static readonly CardDefinition GoblinToken = new()
    {
        OracleId = "token-dungeon-goblin",
        Name = "Goblin",
        CardTypes = CardType.Creature | CardType.Token,
        Subtypes = ["Goblin"],
        Colors = [ManaColor.Red],
        ColorIdentity = [ManaColor.Red],
        Power = 1,
        Toughness = 1,
    };

    private static readonly DungeonDefinition Phandelver = new()
    {
        Name = LostMineOfPhandelver,
        Rooms =
        [
            new DungeonRoom
            {
                Name = "Cave Entrance",
                Text = "Scry 1.",
                Effects = [new Scry(new Amount(1))],
                LeadsTo = ["Goblin Lair", "Mine Tunnels"],
            },
            new DungeonRoom
            {
                Name = "Goblin Lair",
                Text = "Create a 1/1 red Goblin creature token.",
                Effects = [new CreateToken(GoblinToken)],
                LeadsTo = ["Storeroom", "Dark Pool"],
            },
            new DungeonRoom
            {
                Name = "Mine Tunnels",
                Text = "Create a Treasure token.",
                Effects = [new CreateToken(EffectPhrase.PredefinedToken("Treasure"))],
                LeadsTo = ["Dark Pool", "Fungi Cavern"],
            },
            new DungeonRoom
            {
                Name = "Storeroom",
                Text = "Put a +1/+1 counter on target creature.",
                Effects = [new PutCounters(CounterKinds.PlusOnePlusOne, new Amount(1))],
                Targets = [Aimed("target creature")],
                LeadsTo = ["Temple of Dumathoin"],
            },
            new DungeonRoom
            {
                Name = "Dark Pool",
                Text = "Each opponent loses 1 life and you gain 1 life.",
                Effects =
                [
                    new ChangeLifeOfEach(new Amount(-1), PlayerScope.EachOpponent),
                    new ChangeLife(new Amount(1)),
                ],
                LeadsTo = ["Temple of Dumathoin"],
            },
            new DungeonRoom
            {
                Name = "Fungi Cavern",
                Text = "Target creature gets -4/-0 until your next turn.",
                Effects =
                [
                    new PumpUntilEndOfTurn(GenerativeEffects.PumpId(-4, 0))
                    {
                        UntilYourNextTurn = true,
                    },
                ],
                Targets = [Aimed("target creature")],
                LeadsTo = ["Temple of Dumathoin"],
            },
            new DungeonRoom
            {
                Name = "Temple of Dumathoin",
                Text = "Draw a card.",
                Effects = [new DrawCards(new Amount(1))],
            },
        ],
    };

    private static readonly ImmutableDictionary<string, DungeonDefinition> ByName =
        new[] { Phandelver }.ToImmutableDictionary(d => d.Name, StringComparer.Ordinal);

    /// <summary>The dungeons this engine can run, by name.</summary>
    public static IReadOnlyList<string> Names { get; } = [.. ByName.Keys];

    /// <summary>The definition behind a dungeon's name, or null if nothing ships it.</summary>
    public static DungeonDefinition? Definition(string? name) =>
        name is not null && ByName.TryGetValue(name, out var found) ? found : null;

    /// <summary>Whether this card is a dungeon rather than something a player owns (CR 309.1).</summary>
    public static bool IsDungeon(CardDefinition card)
    {
        ArgumentNullException.ThrowIfNull(card);

        return card.OracleId.StartsWith(OracleIdPrefix, StringComparison.Ordinal);
    }

    /// <summary>The card the command-zone object carries (CR 309.2b).</summary>
    /// <remarks>
    /// Typed <see cref="CardType.Other"/> deliberately: dungeon is a card type of its own and is
    /// emphatically not a permanent type (CR 309.2c), so filing it under any of the five would
    /// make it findable by everything that sweeps for artifacts or enchantments.
    /// </remarks>
    public static CardDefinition CardFor(string name)
    {
        var dungeon = Definition(name)
            ?? throw new InvalidOperationException($"No dungeon named '{name}' ships.");

        return new CardDefinition
        {
            OracleId = OracleIdPrefix
                + dungeon.Name.ToLowerInvariant().Replace(" ", "-", StringComparison.Ordinal),
            Name = dungeon.Name,
            CardTypes = CardType.Other,
            OracleText = string.Join(
                Environment.NewLine,
                dungeon.Rooms.Select(r => r.Name + " — " + r.Text)),
        };
    }

    /// <summary>The dungeon this player owns in the command zone, if they own one (CR 309.3).</summary>
    public static GameObject? OwnedBy(GameState state, Guid playerId)
    {
        ArgumentNullException.ThrowIfNull(state);

        foreach (var id in state.Command)
        {
            if (state.TryGetObject(id, out var obj)
                && obj.OwnerId == playerId
                && IsDungeon(obj.Card))
            {
                return obj;
            }
        }

        return null;
    }

    /// <summary>
    /// Which rooms an arrow leads to from here (CR 309.5a). Empty on the bottommost room.
    /// </summary>
    public static ImmutableList<string> RoomsAfter(string? dungeon, string? room)
    {
        if (Definition(dungeon) is not { } found || room is null)
            return [];

        return found.Room(room)?.LeadsTo ?? [];
    }

    /// <summary>
    /// Whether the marker is on the bottommost room, which is what CR 309.6 watches for.
    /// </summary>
    public static bool IsBottommost(string? dungeon, string? room) =>
        room is not null
        && Definition(dungeon) is { } found
        && found.Room(room) is { LeadsTo.IsEmpty: true };

    /// <summary>
    /// The room abilities of a dungeon object, as triggered abilities (CR 309.4c).
    /// </summary>
    /// <remarks>
    /// "The full text of each room ability is 'When you move your venture marker into this room,
    /// [effect].'" — one trigger condition shared by every room in the game, which is why it is
    /// written here once rather than read off anything. The owner check is load-bearing at a
    /// table of more than two: two players can each own a copy of the same dungeon, and a
    /// condition that compared only room names would fire both.
    /// </remarks>
    public static IReadOnlyList<TriggeredAbilityDefinition> RoomAbilitiesOf(GameObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);

        if (obj.Zone != Zone.Command || Definition(obj.Card.Name) is not { } dungeon)
            return [];

        return
        [
            .. dungeon.Rooms.Select(room => new TriggeredAbilityDefinition
            {
                Id = AbilityIdOf(dungeon.Name, room.Name),
                Text = $"When you move your venture marker into {room.Name}, {room.Text}",
                FunctionsFrom = Zone.Command,
                Triggers = (e, _, source) =>
                    e is VentureMarkerMoved moved
                    && moved.PlayerId == source.OwnerId
                    && string.Equals(moved.Dungeon, dungeon.Name, StringComparison.Ordinal)
                    && string.Equals(moved.Room, room.Name, StringComparison.Ordinal),
                Effects = room.Effects,
                Targets = room.Targets,
            }),
        ];
    }

    /// <summary>Stable within its dungeon, so a pending trigger can name it across a replay.</summary>
    public static string AbilityIdOf(string dungeon, string room) =>
        "dungeon:" + dungeon + ":" + room;

    /// <summary>
    /// The target vocabulary, asked for a phrase the rules wrote rather than a card.
    /// </summary>
    /// <remarks>
    /// Refused loudly here for the reason the Ring's trigger conditions are: there is no card to
    /// leave unread, so a phrase the grammar could not read would become a room ability that
    /// targets nothing and silently does nothing.
    /// </remarks>
    private static TargetSpec Aimed(string phrase) =>
        EffectPhrase.Specs.Parse(phrase)
            ?? throw new InvalidOperationException(
                $"A dungeon room's target phrase '{phrase}' is not one the engine reads.");
}
