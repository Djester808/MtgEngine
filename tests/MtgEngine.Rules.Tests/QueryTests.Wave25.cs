using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Interpreter;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The three readers wave 25 owed the query layer: the numbers a secret simultaneous choice
/// collected and the players who chose the extreme (CR 700.2 with 101.4), the two counts a turn
/// keeps per player (CR 121.1), and a player's commander wherever the card is (CR 903.3). The
/// world is the one <see cref="QueryTests"/> declares; the helpers below only reach into it for
/// the two things it has no builder for — a turn's draws and a secret number the interpreter
/// wrote.
/// </summary>
public sealed partial class QueryTests
{
    [Fact]
    [RuleRef("700.2")]
    public void The_highest_and_lowest_secret_numbers_are_read_under_the_keys_the_interpreter_wrote()
    {
        // CR 700.2 with 101.4: each player answered a question of their own and the interpreter
        // remembered the answer under its own per-seat key. The readers look under exactly that
        // prefix, so nothing else in the context can be mistaken for one of these numbers.
        var world = Wave25Chose(World.New(), "wager", (Alice, 7), (Bob, 3))
            .Choose("wager", "99")
            .Choose(EffectInterpreter.SecretNumbersUnder("other") + "0", "42");

        Assert.Equal(7, world.Value(new HighestChosen("wager")));
        Assert.Equal(3, world.Value(new LowestChosen("wager")));

        // A different choice's numbers are a different choice's: the other key reads its own.
        Assert.Equal(42, world.Value(new HighestChosen("other")));

        // One player is both the highest and the lowest, which is the number they chose.
        var alone = Wave25Chose(World.New(), "wager", (Alice, 5));
        Assert.Equal(5, alone.Value(new HighestChosen("wager")));
        Assert.Equal(5, alone.Value(new LowestChosen("wager")));

        // Text that is not a number was written by nothing this layer reads, and is passed over.
        var noisy = Wave25Chose(World.New(), "wager", (Alice, 2))
            .Choose(EffectInterpreter.SecretNumberKey("wager", Bob), "three");
        Assert.Equal(2, noisy.Value(new HighestChosen("wager")));
    }

    [Fact]
    [RuleRef("700.2")]
    public void A_secret_choice_nobody_answered_is_refused_by_name_rather_than_read_as_zero()
    {
        // Zero is a number a player could have chosen, so answering it would let a card that
        // damages each player by the highest number deal none and look as though it had played.
        var world = World.New().Choose("wager", "4");

        var highest = Assert.Throws<RefusedException>(() => world.Value(new HighestChosen("wager")));
        Assert.Equal("700.2", highest.Rule);
        Assert.Contains(nameof(HighestChosen), highest.Reason, StringComparison.Ordinal);

        var lowest = Assert.Throws<RefusedException>(() => world.Value(new LowestChosen("wager")));
        Assert.Contains(nameof(LowestChosen), lowest.Reason, StringComparison.Ordinal);

        var who = Assert.Throws<RefusedException>(() => world.Players(new PlayerWhoChose("wager")));
        Assert.Equal("700.2", who.Rule);
        Assert.Contains(nameof(PlayerWhoChose), who.Reason, StringComparison.Ordinal);
    }

    [Fact]
    [RuleRef("700.2")]
    public void Every_player_who_chose_the_extreme_is_named_in_turn_order()
    {
        // CR 700.2: "each player who chose the highest number" names them all — two players may
        // choose the same number — and CR 101.4 orders them from the active player.
        var world = Wave25Chose(World.New(3), "wager", (Alice, 4), (Bob, 4), (new PlayerId(2), 1));

        Assert.Equal(new[] { Alice, Bob }, world.Players(new PlayerWhoChose("wager")));
        Assert.Equal(new[] { new PlayerId(2) }, world.Players(new PlayerWhoChose("wager", Lowest: true)));

        // Turn order is read from the active player, not from the seat numbers: with Bob active,
        // the same tie names him first.
        var bobsTurn = world with { State = world.State with { Turn = world.State.Turn with { ActivePlayer = Bob } } };
        Assert.Equal(new[] { Bob, Alice }, bobsTurn.Players(new PlayerWhoChose("wager")));
    }

    [Fact]
    [RuleRef("121.1")]
    public void The_cards_drawn_this_turn_are_the_greatest_seat_and_never_the_total()
    {
        // CR 121.1: the turn counts draw events per player. "Any opponent drew two or more cards
        // this turn" asks whether some single opponent reached the number, so the fold over the
        // admitted seats is the greatest of them — a sum would answer yes for two opponents who
        // drew one card each, which is the reading this amount exists to refuse.
        var world = Wave25Drew(Wave25Drew(World.New(3), Alice, 3), Bob, 2);

        Assert.Equal(3, world.Value(new CardsDrawnThisTurn(new You())));
        Assert.Equal(2, world.Value(new CardsDrawnThisTurn(new EachOpponent())));
        Assert.Equal(3, world.Value(new CardsDrawnThisTurn(new EachPlayer())));

        // The third seat drew nothing, and a player who drew nothing is zero rather than a refusal.
        Assert.Equal(0, World.New().Value(new CardsDrawnThisTurn(new You())));
    }

    [Fact]
    [RuleRef("121.1")]
    public void The_lands_that_entered_this_turn_are_counted_from_the_turns_record_and_not_the_board()
    {
        // CR 121.1: the count is of the entering, so a land that has since left still counts and a
        // land that was already there when the turn began does not. World.Land places a card that
        // stands for one already on the battlefield; a token is created onto it (CR 111.1), which
        // is the entry the turn's record keeps.
        var world = World.New(3)
            .Land(1, Alice)
            .Token(2, Alice, CardType.Land, ["Forest"])
            .Token(3, Alice, CardType.Land, ["Island"])
            .Token(4, Bob, CardType.Land, ["Plains"])
            .Token(5, Bob, CardType.Creature, ["Beast"]);

        Assert.Equal(2, world.Value(new LandsEnteredThisTurn(new You())));
        Assert.Equal(1, world.Value(new LandsEnteredThisTurn(new EachOpponent())));

        // The greatest seat, not the total of the three: Alice's two, not Alice's two plus Bob's.
        Assert.Equal(2, world.Value(new LandsEnteredThisTurn(new EachPlayer())));

        // CR 121.1 with 400.7: a land that has since left the battlefield still entered it, so the
        // count does not shrink when one is put into a graveyard — the turn keeps the object as it
        // last existed and reads the land off that.
        Assert.Equal(2, Wave25LeftForGraveyard(world, 3, 6).Value(new LandsEnteredThisTurn(new You())));
        Assert.Equal(0, World.New().Value(new LandsEnteredThisTurn(new You())));
    }

    [Fact]
    [RuleRef("903.3")]
    public void A_players_commander_is_selected_wherever_the_card_is()
    {
        // CR 903.3: the designation is an attribute of the card and not of the object, so it
        // survives every zone change and this reads it in the command zone and the graveyard as
        // readily as on the battlefield. It belongs to the card's owner (CR 108.3), so an object
        // an opponent has taken control of is still that player's commander.
        var world = World.New()
            .Commander(1, Alice)
            .Commander(2, Bob)
            .Creature(3, Alice, 2, 2)
            .ControlledBy(1, Bob);

        Assert.Equal(new[] { new ObjectId(1) }, world.Query.Resolve(world.State, new Cards.Commander(new You()), world.Context));
        Assert.Equal(new[] { new ObjectId(2) }, world.Query.Resolve(world.State, new Cards.Commander(new EachOpponent()), world.Context));
        Assert.Equal(
            new[] { new ObjectId(1), new ObjectId(2) },
            world.Query.Resolve(world.State, new Cards.Commander(new EachPlayer()), world.Context));

        // A game that designated no commander selects nothing: that is a fact about the game and
        // not a gap in the state, so it is an answer rather than a refusal.
        var none = World.New().Creature(1, Alice, 2, 2);
        Assert.Empty(none.Query.Resolve(none.State, new Cards.Commander(new You()), none.Context));
    }

    /// <summary>
    /// The numbers one secret choice collected, written under the keys the interpreter writes —
    /// the test never spells the key itself, which is what makes it a test of the reader.
    /// </summary>
    private static World Wave25Chose(World world, string key, params (PlayerId Player, int Number)[] numbers)
    {
        foreach (var (player, number) in numbers)
        {
            world = world.Choose(
                EffectInterpreter.SecretNumberKey(key, player),
                number.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return world;
    }

    /// <summary>
    /// A permanent put into its owner's graveyard, CR 400.7: the move keeps the object as it last
    /// existed on the battlefield, which is what the turn's count reads a departed land off.
    /// </summary>
    private static World Wave25LeftForGraveyard(World world, int id, int newId)
    {
        var departed = world.State.Objects[new ObjectId(id)];
        var inGraveyard = departed with
        {
            Id = new ObjectId(newId),
            Zone = new ZoneKey(Cards.Zone.Graveyard, departed.Owner),
        };
        world.Layers.Set(new ObjectId(newId), world.Layers.Of(world.State, new ObjectId(id)));
        return world with { State = new ObjectMoved(new ObjectId(id), inGraveyard, -1).Apply(world.State) };
    }

    /// <summary>Draws a player has made this turn, CR 121.1: the fold <c>ObjectDrawn</c> applies, once per card.</summary>
    private static World Wave25Drew(World world, PlayerId who, int cards)
    {
        var player = world.State.Player(who);
        for (var i = 0; i < cards; i++)
            player = player.Drew();

        return world with { State = world.State with { Players = world.State.Players.SetItem(who.Value, player) } };
    }
}
