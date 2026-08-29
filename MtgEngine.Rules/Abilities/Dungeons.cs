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

    /// <summary>
    /// The card's own entry restriction, printed above the rooms, or null for none.
    /// </summary>
    /// <remarks>
    /// Undercity's "You can't enter this dungeon unless you 'venture into Undercity'" — the line
    /// that keeps CR 701.49a's choice of a starting dungeon away from it. The engine enforces it
    /// by never offering Undercity as <see cref="Dungeons.Default"/>; carrying the text keeps the
    /// card the board displays honest about why.
    /// </remarks>
    public string? Restriction { get; init; }

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
    /// The first dungeon whose every room the effect vocabulary could say. Undercity became the
    /// second — its bottommost room's four welded instructions are now the look-and-take with the
    /// cards revealed, the taken card landing on the battlefield with counters and a granted
    /// keyword, and the shuffle after — which is what unblocked <b>the initiative</b> (CR 726):
    /// all three of its inherent abilities venture into Undercity by name. The other two dungeons
    /// are still blocked, and the room that blocks each is named here so the next pass does not
    /// have to re-derive the measurement:
    /// <list type="bullet">
    /// <item><b>Dungeon of the Mad Mage</b> — "Mad Wizard's Lair: draw three cards and reveal
    /// them, you may cast one of them without paying its mana cost" (nothing can refer to the
    /// cards a draw just drew, and the free-cast offer over them does not exist), and it is the
    /// bottommost room, so the dungeon would stop working exactly where completing it happens.
    /// "Runestone Caverns: exile the top two cards of your library, you may play them" grants a
    /// play permission with <em>no duration at all</em>, and both permissions the engine has
    /// expire (<c>MayPlayUntilTurn</c>, <c>MayPlayThroughOwnersNextTurn</c>) — reading it as
    /// either would be a room that quietly takes the cards back. "Twisted Caverns: target
    /// creature can't attack until your next turn" no longer blocks: defender is exactly
    /// "can't attack" (CR 702.3b), and the until-your-next-turn duration Fungi Cavern forced
    /// into existence carries the grant.</item>
    /// <item><b>Tomb of Annihilation</b> — two of its five rooms (not three, as an earlier
    /// measurement said: Trapped Entry is a plain "each player loses 1 life") are "each player
    /// loses 2 life unless they [discard / sacrifice]", a payment offered to every player at
    /// once with the consequence falling on whoever declines. <c>MayPay</c> asks exactly one
    /// player and resolves its branches as the room's controller, so each decline would drain
    /// the venturing player instead of the player who declined. Oubliette's mandatory discard
    /// and sacrifices are sayable now (<c>DiscardCards</c> and <c>ChooseAndMove</c> ask the
    /// right player), and Cradle of the Death God is a token.</item>
    /// </list>
    /// Adding either later is data in this file plus the effect each blocked room needs; neither
    /// wants anything further from the machinery around it.
    /// </remarks>
    public const string LostMineOfPhandelver = "Lost Mine of Phandelver";

    /// <summary>Undercity (CR 309, CR 701.49d), nine rooms and four forks.</summary>
    /// <remarks>
    /// The initiative's dungeon: nothing enters it except by name — the card itself prints "You
    /// can't enter this dungeon unless you 'venture into Undercity'", so CR 701.49a's choice of a
    /// dungeon to start never offers it and a plain venture still has exactly one answer. Its own
    /// venture instruction is CR 701.49d's variant: with no dungeon owned it starts this one, and
    /// with any dungeon already underway it advances that one instead.
    /// </remarks>
    public const string Undercity = "Undercity";

    /// <summary>
    /// Which dungeon a player choosing one gets (CR 701.49a).
    /// </summary>
    /// <remarks>
    /// The rule has the player choose a dungeon card they own from outside the game. This engine
    /// has no outside-the-game zone and ships two dungeons — and the choice still has exactly one
    /// answer, because the second is Undercity, whose card prints "You can't enter this dungeon
    /// unless you 'venture into Undercity'". A plain venture may only ever start Lost Mine, which
    /// is the rule as written rather than a simplification of it. When a third dungeon ships, a
    /// plain venture with no dungeon underway becomes a choice, like the room fork below.
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

    /// <summary>A 4/1 black Skeleton with menace, which only Undercity's Catacombs describes.</summary>
    private static readonly CardDefinition SkeletonToken = new()
    {
        OracleId = "token-dungeon-skeleton",
        Name = "Skeleton",
        CardTypes = CardType.Creature | CardType.Token,
        Subtypes = ["Skeleton"],
        Colors = [ManaColor.Black],
        ColorIdentity = [ManaColor.Black],
        Power = 4,
        Toughness = 1,
        Keywords = KeywordAbility.Menace,
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

    /// <remarks>
    /// The rooms are the printed card exactly, and the bottommost is the reason this dungeon
    /// exists at all: Throne of the Dead Three is CR 726's whole destination, and until its four
    /// welded instructions could be said, the initiative could not be built without shipping a
    /// dungeon that stops working at its last room.
    /// </remarks>
    private static readonly DungeonDefinition UndercityDungeon = new()
    {
        Name = Undercity,
        Restriction = "You can't enter this dungeon unless you \"venture into Undercity.\"",
        Rooms =
        [
            new DungeonRoom
            {
                Name = "Secret Entrance",
                Text = "Search your library for a basic land card, reveal it, put it into "
                    + "your hand, then shuffle.",
                Effects = [new SearchLibrary(SearchFilters.BasicLand, Zone.Hand)],
                LeadsTo = ["Forge", "Lost Well"],
            },
            new DungeonRoom
            {
                Name = "Forge",
                Text = "Put two +1/+1 counters on target creature.",
                Effects = [new PutCounters(CounterKinds.PlusOnePlusOne, new Amount(2))],
                Targets = [Aimed("target creature")],
                LeadsTo = ["Trap!", "Arena"],
            },
            new DungeonRoom
            {
                Name = "Lost Well",
                Text = "Scry 2.",
                Effects = [new Scry(new Amount(2))],
                LeadsTo = ["Arena", "Stash"],
            },
            new DungeonRoom
            {
                Name = "Trap!",
                Text = "Target player loses 5 life.",
                Effects = [new ChangeLife(new Amount(-5), 0)],
                Targets = [Aimed("target player")],
                LeadsTo = ["Archives"],
            },
            new DungeonRoom
            {
                Name = "Arena",
                Text = "Goad target creature.",
                Effects = [new GoadTarget()],
                Targets = [Aimed("target creature")],
                LeadsTo = ["Archives", "Catacombs"],
            },
            new DungeonRoom
            {
                Name = "Stash",
                Text = "Create a Treasure token.",
                Effects = [new CreateToken(EffectPhrase.PredefinedToken("Treasure"))],
                LeadsTo = ["Catacombs"],
            },
            new DungeonRoom
            {
                Name = "Archives",
                Text = "Draw a card.",
                Effects = [new DrawCards(new Amount(1))],
                LeadsTo = ["Throne of the Dead Three"],
            },
            new DungeonRoom
            {
                Name = "Catacombs",
                Text = "Create a 4/1 black Skeleton creature token with menace.",
                Effects = [new CreateToken(SkeletonToken)],
                LeadsTo = ["Throne of the Dead Three"],
            },
            new DungeonRoom
            {
                Name = "Throne of the Dead Three",
                Text = "Reveal the top ten cards of your library. Put a creature card from "
                    + "among them onto the battlefield with three +1/+1 counters on it. It "
                    + "gains hexproof until your next turn. Then shuffle.",
                Effects =
                [
                    new LookAndTake(new Amount(10), Zone.Battlefield, FilterId: "creature")
                    {
                        Reveal = true,
                        CountersOnTaken = 3,
                        TakenGrantId = GenerativeEffects.GrantId(KeywordAbility.Hexproof),
                        GrantUntilTakersNextTurn = true,
                        ShuffleAfter = true,
                    },
                ],
            },
        ],
    };

    private static readonly ImmutableDictionary<string, DungeonDefinition> ByName =
        new[] { Phandelver, UndercityDungeon }
            .ToImmutableDictionary(d => d.Name, StringComparer.Ordinal);

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

        var lines = dungeon.Rooms.Select(r => r.Name + " — " + r.Text);

        if (dungeon.Restriction is { } restricted)
            lines = lines.Prepend(restricted);

        return new CardDefinition
        {
            OracleId = OracleIdPrefix
                + dungeon.Name.ToLowerInvariant().Replace(" ", "-", StringComparison.Ordinal),
            Name = dungeon.Name,
            CardTypes = CardType.Other,
            OracleText = string.Join(Environment.NewLine, lines),
        };
    }

    /// <summary>
    /// The events one venture produces (CR 701.49), shared by everything that ventures.
    /// </summary>
    /// <remarks>
    /// In one place because four things venture and they must not drift: the printed keyword
    /// action (<see cref="VentureIntoTheDungeon"/>), a card that takes the initiative
    /// (CR 726.2's third inherent ability), and the initiative's own upkeep and combat-damage
    /// hooks in the engine. The three cases are CR 701.49a–b:
    /// <list type="bullet">
    /// <item>No dungeon in the command zone: <paramref name="into"/> — or the
    /// <see cref="Default"/> when the instruction named none — is put there and the marker goes
    /// on its topmost room. CR 701.49d's variant differs from the plain venture <em>only</em>
    /// here: a named dungeon decides what is started, never what is advanced.</item>
    /// <item>One arrow out of the current room: the marker moves.</item>
    /// <item>Several arrows: the player chooses, as a deferred question, because an effect
    /// cannot stop half way through and wait.</item>
    /// </list>
    /// The fourth case, venturing while already on the bottommost room (CR 701.49c), cannot be
    /// reached in a settled game: CR 309.6 removes that dungeon from the game as a state-based
    /// action before anybody has priority again, so the player owns none by the time the next
    /// venture happens and the first case applies. It returns nothing rather than guessing.
    /// </remarks>
    public static IReadOnlyList<GameEvent> VentureEvents(
        GameState state, Guid who, string? into = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (OwnedBy(state, who) is not { } owned)
        {
            var card = CardFor(into ?? Default);

            return
            [
                new ObjectCreated(ObjectId.New(), card, who, who, Zone.Command),
                new VentureMarkerMoved(who, card.Name, Definition(card.Name)!.Top.Name),
            ];
        }

        var dungeon = owned.Card.Name;
        var next = RoomsAfter(dungeon, state.GetPlayer(who).DungeonRoom);

        return next.Count switch
        {
            0 => [],
            1 => [new VentureMarkerMoved(who, dungeon, next[0])],
            _ => [new VentureRoomRequested(who, dungeon, next)],
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
