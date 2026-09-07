using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Layers;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Replacement and prevention, CR 614 and 615, against the real layer and query systems so a
/// filter sees what the layers made. Every definition is invented: no card name reaches the
/// engine or its tests.
/// </summary>
public sealed class ReplacementEffectTests
{
    private readonly ReplacementEffectFakeInterpreter _interpreter = new();

    private static ObjectFilter Land => new(Types: CardType.Land);

    /// <summary>CR 614.12: "as this enters, it enters tapped" is part of the entering itself.</summary>
    [Fact]
    public void Land_enters_tapped()
    {
        var board = new Board();
        var card = board.Put(WithReplacement("slow field", CardType.Land, new WouldEnterBattlefield(Land), new EntersTapped(), self: true), Zone.Hand);

        var outcome = System().Replace(board.State, Entering(board.State, card), new PlayerId(0));

        var moved = Assert.IsType<ObjectMoved>(Assert.Single(outcome.Events));
        Assert.True(moved.To.Tapped);
        Assert.True(outcome.State.Objects[moved.To.Id].Tapped);
    }

    /// <summary>CR 614.12: counters an object enters with are put on as it enters, not afterwards.</summary>
    [Fact]
    public void Creature_enters_with_two_counters()
    {
        var board = new Board();
        var card = board.Put(
            WithReplacement("grown beast", CardType.Creature, new WouldEnterBattlefield(ObjectFilter.Creature), new EntersWithCounters(Counters.PlusOnePlusOne, new Fixed(2)), self: true, power: 1, toughness: 1),
            Zone.Hand);

        var outcome = System().Replace(board.State, Entering(board.State, card), new PlayerId(0));

        var moved = Assert.IsType<ObjectMoved>(Assert.Single(outcome.Events));
        Assert.Equal(2, moved.To.Counters[Counters.PlusOnePlusOne]);
    }

    /// <summary>CR 614.1a: "if it would die, exile it instead" — the move to the graveyard never happens.</summary>
    [Fact]
    public void Dying_creature_is_exiled_instead()
    {
        var board = new Board();
        var creature = board.Put(Board.Creature(2, 2, "doomed thing"));
        board.Put(WithReplacement("shroud of ash", CardType.Enchantment, new WouldDie(ObjectFilter.Creature), new ReplaceWith(new Exile(new Self()))));

        var exiler = new ReplacementEffectFakeInterpreter
        {
            Instead = (state, _, context) =>
            {
                var moved = Moving(state, context.Triggering[0], ZoneKey.Shared(Zone.Exile));
                return new Outcome(moved.Apply(state), [moved]);
            },
        };
        var outcome = System(exiler).Replace(board.State, Dying(board.State, creature), new PlayerId(0));

        var moved = Assert.IsType<ObjectMoved>(Assert.Single(outcome.Events));
        Assert.Equal(Zone.Exile, moved.To.Zone.Zone);
        Assert.Equal(creature, moved.From);
        Assert.Single(exiler.Ran);
    }

    /// <summary>CR 615.1: preventing 3 of 5 leaves 2 to be dealt.</summary>
    [Fact]
    public void Preventing_three_of_five_damage_leaves_two()
    {
        var board = new Board();
        var creature = board.Put(Board.Creature(4, 4, "shielded thing"));
        var source = board.Put(Board.Creature(5, 5, "burner"), owner: 1);
        var state = Shield(board.State, creature, new Fixed(3));

        var outcome = System().Replace(state, new DamageDealt(source, creature, null, 5, Combat: false, FromCommander: false), new PlayerId(0));

        var damage = Assert.IsType<DamageDealt>(Assert.Single(outcome.Events));
        Assert.Equal(2, damage.Amount);
        Assert.Equal(2, outcome.State.Objects[creature].Damage);
    }

    /// <summary>CR 615.1: a shield with no amount prevents all of it, and then nothing happened.</summary>
    [Fact]
    public void Total_prevention_leaves_no_damage_event()
    {
        var board = new Board();
        var creature = board.Put(Board.Creature(4, 4, "warded thing"));
        var source = board.Put(Board.Creature(5, 5, "burner"), owner: 1);
        var state = Shield(board.State, creature, null);

        var outcome = System().Replace(state, new DamageDealt(source, creature, null, 5, Combat: false, FromCommander: false), new PlayerId(0));

        Assert.Empty(outcome.Events);
        Assert.Equal(0, outcome.State.Objects[creature].Damage);
    }

    /// <summary>CR 616.1: two effects apply and neither is self-replacement, so the affected player is asked.</summary>
    [Fact]
    public void Two_applicable_replacements_raise_the_choice()
    {
        var board = new Board();
        board.Put(WithReplacement("first watcher", CardType.Enchantment, new WouldEnterBattlefield(ObjectFilter.Creature), new EntersTapped()));
        board.Put(WithReplacement("second watcher", CardType.Enchantment, new WouldEnterBattlefield(ObjectFilter.Creature), new EntersWithCounters(Counters.PlusOnePlusOne, new Fixed(1))));
        var card = board.Put(Board.Creature(1, 1, "arriving thing"), Zone.Hand);

        var outcome = System().Replace(board.State, Entering(board.State, card), new PlayerId(0));

        var asked = Assert.IsType<ChoiceRequested>(Assert.Single(outcome.Events));
        Assert.Equal("replacement-order", asked.Choice.Kind);
        Assert.Equal(new PlayerId(0), asked.Choice.Player);
        Assert.Equal(2, asked.Choice.Options.Count);
        Assert.Equal(asked.Choice, outcome.State.Pending);
    }

    /// <summary>CR 614.15: a self-replacement is applied first, so two applicable effects ask nothing.</summary>
    [Fact]
    public void Self_replacement_applies_before_an_outside_one()
    {
        var board = new Board();
        board.Put(WithReplacement("outside watcher", CardType.Enchantment, new WouldEnterBattlefield(ObjectFilter.Creature), new EntersWithCounters(Counters.PlusOnePlusOne, new Fixed(1))));
        var card = board.Put(
            WithReplacement("self tapper", CardType.Creature, new WouldEnterBattlefield(ObjectFilter.Creature), new EntersTapped(), self: true, power: 1, toughness: 1),
            Zone.Hand);

        var outcome = System().Replace(board.State, Entering(board.State, card), new PlayerId(0));

        Assert.DoesNotContain(outcome.Events, e => e is ChoiceRequested);
        var moved = Assert.IsType<ObjectMoved>(Assert.Single(outcome.Events));
        Assert.True(moved.To.Tapped);
        Assert.Equal(1, moved.To.Counters[Counters.PlusOnePlusOne]);
    }

    private static CardDefinition WithReplacement(string name, CardType types, ReplacedEvent instead, Effect with, bool self = false, int? power = null, int? toughness = null) =>
        Board.Definition(name, types, power, toughness, abilities: [new ReplacementAbility(instead, with, SelfOnly: self) { Text = name }]);

    private MtgEngine.Rules.Replacement.ReplacementSystem System(IInterpreter? interpreter = null)
    {
        var layers = new LayerSystem();
        return new MtgEngine.Rules.Replacement.ReplacementSystem(new MtgEngine.Rules.Query.Query(layers), layers, interpreter ?? _interpreter);
    }

    private static GameState Shield(GameState state, ObjectId what, Amount? amount) =>
        state with { ActiveEffects = [new ActiveEffect(1, new PreventDamage(amount), null, new PlayerId(0), [what], [], new Permanently(), 1, FromStaticAbility: false)] };

    private static ObjectMoved Entering(GameState state, ObjectId card) =>
        Moving(state, card, ZoneKey.Shared(Zone.Battlefield));

    private static ObjectMoved Dying(GameState state, ObjectId card) =>
        Moving(state, card, ZoneKey.Of(Zone.Graveyard, state.Objects[card].Owner));

    private static ObjectMoved Moving(GameState state, ObjectId card, ZoneKey to)
    {
        var obj = state.Objects[card];
        return new ObjectMoved(card, obj with { Id = new ObjectId(state.NextObjectId), Zone = to, Timestamp = state.NextTimestamp }, -1);
    }
}
