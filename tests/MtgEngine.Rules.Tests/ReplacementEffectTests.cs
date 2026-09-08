using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
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
public sealed partial class ReplacementEffectTests
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

    /// <summary>CR 615.7: preventing 3 of 5 leaves 2 to be dealt, and spends the whole shield.</summary>
    [Fact]
    public void Preventing_three_of_five_damage_leaves_two()
    {
        var board = new Board();
        var creature = board.Put(Board.Creature(4, 4, "shielded thing"));
        var source = board.Put(Board.Creature(5, 5, "burner"), owner: 1);
        var state = Shield(board.State, creature, new Fixed(3));

        var outcome = System().Replace(state, Damage(source, creature, 5), new PlayerId(0));

        Assert.Null(Assert.IsType<ActiveEffectChanged>(outcome.Events[0]).Effect);
        var damage = Assert.IsType<DamageDealt>(outcome.Events[^1]);
        Assert.Equal(2, damage.Amount);
        Assert.Equal(2, outcome.State.Objects[creature].Damage);
        Assert.Empty(outcome.State.ActiveEffects);
    }

    /// <summary>
    /// CR 615.7: a shield for a specific amount prevents that much once. Three damage spends a
    /// shield of 3 entirely, and the two damage after it are dealt normally.
    /// </summary>
    [Fact]
    public void A_spent_shield_prevents_nothing_afterwards()
    {
        var board = new Board();
        var creature = board.Put(Board.Creature(4, 4, "shielded thing"));
        var source = board.Put(Board.Creature(5, 5, "burner"), owner: 1);
        var system = System();

        var first = system.Replace(Shield(board.State, creature, new Fixed(3)), Damage(source, creature, 3), new PlayerId(0));

        // CR 615.13: the spending and the prevention it paid for are both in the log now (E8f).
        Assert.Null(Assert.IsType<ActiveEffectChanged>(first.Events[0]).Effect);
        Assert.Equal(3, Wave28Prevention(first).Prevented);
        Assert.Empty(first.State.ActiveEffects);
        Assert.Equal(0, first.State.Objects[creature].Damage);

        var second = system.Replace(first.State, Damage(source, creature, 2), new PlayerId(0));

        Assert.Equal(2, Assert.IsType<DamageDealt>(Assert.Single(second.Events)).Amount);
        Assert.Equal(2, second.State.Objects[creature].Damage);
    }

    /// <summary>CR 615.7: preventing 1 damage reduces the remaining shield by 1 and leaves it in force.</summary>
    [Fact]
    public void A_partly_spent_shield_keeps_the_remainder()
    {
        var board = new Board();
        var creature = board.Put(Board.Creature(9, 9, "shielded thing"));
        var source = board.Put(Board.Creature(5, 5, "burner"), owner: 1);
        var system = System();

        var first = system.Replace(Shield(board.State, creature, new Fixed(3)), Damage(source, creature, 1), new PlayerId(0));

        Assert.Equal(new PreventDamage(new Fixed(2)), Assert.IsType<ActiveEffectChanged>(first.Events[0]).Effect);
        Assert.Equal(1, Wave28Prevention(first).Prevented);
        Assert.Equal(new PreventDamage(new Fixed(2)), Assert.Single(first.State.ActiveEffects).Effect);

        var second = system.Replace(first.State, Damage(source, creature, 5), new PlayerId(0));

        Assert.Equal(3, Assert.IsType<DamageDealt>(second.Events[^1]).Amount);
        Assert.Empty(second.State.ActiveEffects);
    }

    /// <summary>
    /// CR 615.1: a shield with no amount prevents all of it, and then nothing happened. It is
    /// never used up, so it is still in force afterwards.
    /// </summary>
    [Fact]
    public void Total_prevention_leaves_no_damage_event()
    {
        var board = new Board();
        var creature = board.Put(Board.Creature(4, 4, "warded thing"));
        var source = board.Put(Board.Creature(5, 5, "burner"), owner: 1);
        var state = Shield(board.State, creature, null);

        var outcome = System().Replace(state, Damage(source, creature, 5), new PlayerId(0));

        // CR 615.13: no damage event, and the prevention is the only thing the log says happened.
        Assert.Equal(5, Wave28Prevention(outcome).Prevented);
        Assert.DoesNotContain(outcome.Events, e => e is DamageDealt);
        Assert.Equal(0, outcome.State.Objects[creature].Damage);
        Assert.Single(outcome.State.ActiveEffects);
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

    /// <summary>
    /// CR 616.1: the answer says which effect applies first, and what it leaves is offered to the
    /// rest. Doubling 3 and then adding 1 is 7; adding first and then doubling is 8, so which
    /// went first is visible in the damage that lands.
    /// </summary>
    [Fact]
    public void Resuming_applies_the_chosen_effect_first_and_the_other_second()
    {
        var board = new Board();
        var creature = board.Put(Board.Creature(9, 9, "struck thing"));
        var source = board.Put(Board.Creature(3, 3, "burner"), owner: 1);
        board.Put(WithReplacement("doubler", CardType.Enchantment, new WouldBeDealtDamage(ObjectFilter.Creature), new ModifyAmount(Multiply: 2)));
        board.Put(WithReplacement("adder", CardType.Enchantment, new WouldBeDealtDamage(ObjectFilter.Creature), new ModifyAmount(Add: 1)));
        var system = System();

        var halt = system.Replace(board.State, Damage(source, creature, 3), new PlayerId(0));
        var asked = Assert.IsType<ChoiceRequested>(Assert.Single(halt.Events));
        Assert.Same(halt.State.Pending, asked.Choice);

        var doubledFirst = Answer(system, halt.State, asked.Choice, "doubler");
        var addedFirst = Answer(system, halt.State, asked.Choice, "adder");

        Assert.Equal(7, Assert.IsType<DamageDealt>(Assert.Single(doubledFirst.Events)).Amount);
        Assert.Equal(7, doubledFirst.State.Objects[creature].Damage);
        Assert.Equal(8, Assert.IsType<DamageDealt>(Assert.Single(addedFirst.Events)).Amount);
    }

    /// <summary>
    /// CR 616.1: no effect applies to the same event twice, including one that applied before the
    /// question was raised. The self-replacement's counter is put on once, not once more when the
    /// answer comes back.
    /// </summary>
    [Fact]
    public void An_effect_that_applied_before_the_question_does_not_apply_again()
    {
        var board = new Board();
        board.Put(WithReplacement("tapper", CardType.Enchantment, new WouldEnterBattlefield(ObjectFilter.Creature), new EntersTapped()));
        board.Put(WithReplacement("outside grower", CardType.Enchantment, new WouldEnterBattlefield(ObjectFilter.Creature), new EntersWithCounters(Counters.PlusOnePlusOne, new Fixed(1))));
        var card = board.Put(
            WithReplacement("self grower", CardType.Creature, new WouldEnterBattlefield(ObjectFilter.Creature), new EntersWithCounters(Counters.PlusOnePlusOne, new Fixed(1)), self: true, power: 1, toughness: 1),
            Zone.Hand);
        var system = System();

        var halt = system.Replace(board.State, Entering(board.State, card), new PlayerId(0));
        var asked = Assert.IsType<ChoiceRequested>(Assert.Single(halt.Events));

        var resumed = Answer(system, halt.State, asked.Choice, "tapper");

        var moved = Assert.IsType<ObjectMoved>(Assert.Single(resumed.Events));
        Assert.True(moved.To.Tapped);
        Assert.Equal(2, moved.To.Counters[Counters.PlusOnePlusOne]);
    }

    /// <summary>
    /// CR 701.24: "if you would shuffle" watches a shuffle. Cards put back into a zone in a
    /// chosen order are the same event with the flag clear, and are not a shuffle.
    /// </summary>
    [Fact]
    public void A_shuffle_is_matched_and_a_reorder_is_not()
    {
        var board = new Board();
        board.Put(WithReplacement("still library", CardType.Enchantment, new WouldShuffle(new You()), new ReplaceWith(new GainLife(new Fixed(1)))));
        var library = ZoneKey.Of(Zone.Library, new PlayerId(0));
        var system = System();

        var shuffled = system.Replace(board.State, new ZoneReordered(library, [], Shuffle: true), new PlayerId(0));

        Assert.Empty(shuffled.Events);
        Assert.Single(_interpreter.Ran);

        var reordered = system.Replace(board.State, new ZoneReordered(library, [], Shuffle: false), new PlayerId(0));

        Assert.False(Assert.IsType<ZoneReordered>(Assert.Single(reordered.Events)).Shuffle);
        Assert.Single(_interpreter.Ran);
    }

    /// <summary>
    /// CR 702.136a and 614.1c: a mode chosen as the permanent enters — an additional +1/+1
    /// counter, or haste — is a replacement whose effect is a mode choice. The interpreter halts
    /// with its own question, and nothing else about the entering happens until it is answered.
    /// </summary>
    [Fact]
    public void A_mode_choice_as_a_permanent_enters_raises_the_question_and_stops()
    {
        var board = new Board();
        var modes = new ChooseMode(
        [
            new Mode("enters with an additional +1/+1 counter", new EntersWithCounters(Counters.PlusOnePlusOne, new Fixed(1))),
            new Mode("gains haste", new Apply(new GrantKeyword(KeywordKind.Haste), new Self(), new Permanently())),
        ]);
        var card = board.Put(
            WithReplacement("rioter", CardType.Creature, new WouldEnterBattlefield(ObjectFilter.Creature), modes, self: true, power: 3, toughness: 3),
            Zone.Hand);
        var halting = new ReplacementEffectFakeInterpreter { Instead = (state, _, _) => Halt(state) };

        var outcome = System(halting).Replace(board.State, Entering(board.State, card), new PlayerId(0));

        var asked = Assert.IsType<ChoiceRequested>(Assert.Single(outcome.Events));
        Assert.Equal("Choose a mode", asked.Choice.Prompt);
        Assert.Same(outcome.State.Pending, asked.Choice);
        Assert.DoesNotContain(outcome.Events, e => e is ObjectMoved);
        Assert.IsType<ChooseMode>(Assert.Single(halting.Ran));
    }

    /// <summary>The interpreter stopping to ask a question of its own, which is E9b's to answer.</summary>
    private static Outcome Halt(GameState state)
    {
        var ask = new ChoiceRequested(new PendingChoice(
            new ChoiceId(state.NextChoiceId),
            new PlayerId(0),
            ChoiceKinds.Value,
            "Choose a mode",
            [new ChoiceOption("mode0", "counter"), new ChoiceOption("mode1", "haste")],
            1,
            1,
            new ChoiceContext(null, null, null, ImmutableDictionary<string, string>.Empty)));

        return new Outcome(ask.Apply(state), [ask]);
    }

    /// <summary>What the choice system does with an answer: record it, then hand it to whoever asked.</summary>
    private static Outcome Answer(MtgEngine.Rules.Replacement.ReplacementSystem system, GameState halted, PendingChoice choice, string label)
    {
        var option = choice.Options.Single(o => string.Equals(o.Label, label, StringComparison.Ordinal));
        var answered = new ChoiceAnswered(choice.Id, choice.Player, [option.Id]).Apply(halted);
        return system.Resume(answered, new Resumption(choice.Kind, choice.Player, [option], choice.Context));
    }

    private static DamageDealt Damage(ObjectId source, ObjectId target, int amount) =>
        new(source, target, null, amount, Combat: false, FromCommander: false);

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
