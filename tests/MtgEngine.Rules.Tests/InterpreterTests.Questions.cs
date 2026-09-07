using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The questions a resolution asks and the limits it obeys, CR 601.2b and 104.3b. A question is
/// asked with the set it is chosen from, because CR 101.4 holds the game until it is answered and
/// an answer outside the options is no answer at all; and an instruction that says a player wins
/// or loses is not obeyed while a continuous effect says they can't (CR 101.2).
/// </summary>
public sealed partial class InterpreterTests
{
    /* ---------------------------------------------------------------------------------------- *
     * CR 601.2b — a choice is made from a set, and the question carries it.
     * ---------------------------------------------------------------------------------------- */

    [Fact]
    [RuleRef("601.2b")]
    public void A_colour_question_offers_the_five_colours_and_the_answer_is_kept()
    {
        var table = new InterpreterTable();
        var permanent = table.Permanent(table.A, CardType.Enchantment, null, null);

        // The card narrows nothing: "choose a color" with no options of its own.
        table.Resolve(table.OnStack(table.A, new ChooseValue(ChooseWhat.Color, "color"), Asked(permanent)));

        // CR 105.1: the five colours, and no sixth — colorless is not one of them.
        Assert.Equal(
            ["White", "Blue", "Black", "Red", "Green"],
            table.Pending.Options.Select(o => o.Id));
        Assert.All(table.Pending.Options, o => Assert.Equal(o.Id, o.Label));

        var events = table.Answer("Green");

        // CR 608.2d: an answer among the options is accepted and the permanent keeps it.
        Assert.Contains(events, e => e is ValueChosen { Key: "color", Value: "Green" });
        Assert.Equal("Green", table.Object(permanent).Chosen["color"]);
    }

    [Fact]
    [RuleRef("205.3m")]
    public void A_creature_type_question_offers_the_creature_types_the_rules_define()
    {
        var table = new InterpreterTable();
        var permanent = table.Permanent(table.A, CardType.Enchantment, null, null);

        table.Resolve(table.OnStack(table.A, new ChooseValue(ChooseWhat.CreatureType, "type"), Asked(permanent)));

        // CR 205.3m: the set is the generated table the rest of the engine matches subtypes
        // against, so a question and a later "is it that type" can never disagree.
        Assert.Equal(CreatureTypes.All.Count, table.Pending.Options.Count);
        Assert.All(table.Pending.Options, o => Assert.True(CreatureTypes.IsCreatureType(o.Id)));

        var chosen = table.Pending.Options[0].Id;
        table.Answer(chosen);
        Assert.Equal(chosen, table.Object(permanent).Chosen["type"]);
    }

    [Fact]
    [RuleRef("601.2b")]
    public void A_number_question_offers_the_range_the_card_gave_it()
    {
        var table = new InterpreterTable();
        var permanent = table.Permanent(table.A, CardType.Enchantment, null, null);
        var choose = new ChooseValue(ChooseWhat.Number, "n", Options: ["1", "2", "3"]);

        table.Resolve(table.OnStack(table.A, choose, Asked(permanent)));

        // The card's own range wins over any set the game would supply, and it is asked in the
        // order the card wrote it.
        Assert.Equal(["1", "2", "3"], table.Pending.Options.Select(o => o.Id));
        table.Answer("2");
        Assert.Equal("2", table.Object(permanent).Chosen["n"]);
    }

    [Fact]
    [RuleRef("601.2b")]
    public void A_choice_with_no_set_and_no_options_is_refused_rather_than_asked()
    {
        var table = new InterpreterTable();
        var spell = table.OnStack(table.A, new ChooseValue(ChooseWhat.CardName, "name"));

        // MEMORY, and CR 101.4: a question with an empty option list can never be answered, so the
        // game would stop there for ever. The refusal names the rule and the definition fails
        // instead, which a runner reports.
        var refused = Assert.Throws<RefusedException>(() => table.Resolve(spell));

        Assert.Equal("601.2b", refused.Rule);
        Assert.Null(table.State.Pending);
    }

    /* ---------------------------------------------------------------------------------------- *
     * CR 104.3b with 101.2 — "can't" beats "does".
     * ---------------------------------------------------------------------------------------- */

    [Fact]
    [RuleRef("104.3b")]
    public void A_lose_the_game_effect_makes_a_player_lose()
    {
        var table = new InterpreterTable();

        var events = table.Resolve(table.OnStack(table.A, new LoseGame()));

        Assert.Contains(events, e => e is PlayerLost { Reason: "effect" });
    }

    [Fact]
    [RuleRef("104.3b")]
    public void A_lose_the_game_effect_emits_nothing_while_the_player_cant_lose()
    {
        var table = new InterpreterTable();
        table.Layers.Restrict(table.A, new PlayerLimits(PlayerRestriction.Lose));

        var events = table.Resolve(table.OnStack(table.A, new LoseGame()));

        // CR 101.2 with 104.3b: the prohibition has the last word, so nothing happens at all —
        // there is no event for a loss that did not occur.
        Assert.DoesNotContain(events, e => e is PlayerLost);
        Assert.False(table.State.Player(table.A).HasLost);
    }

    [Fact]
    [RuleRef("104.2a")]
    public void A_win_the_game_effect_ends_the_game_unless_that_player_cant_win()
    {
        var table = new InterpreterTable();
        Assert.Contains(table.Resolve(table.OnStack(table.A, new WinGame())), e => e is GameEnded);

        var stopped = new InterpreterTable();
        stopped.Layers.Restrict(stopped.A, new PlayerLimits(CantWin: true));

        // CR 101.2: "can't win the game" is a prohibition, so the game does not end and no
        // winner is named.
        Assert.DoesNotContain(stopped.Resolve(stopped.OnStack(stopped.A, new WinGame())), e => e is GameEnded);
    }

    /* ---------------------------------------------------------------------------------------- *
     * CR 106.6a and 603.7 — a delayed ability that watches keyed mana.
     * ---------------------------------------------------------------------------------------- */

    [Fact]
    [RuleRef("106.6a")]
    public void A_delayed_mana_spent_ability_is_still_there_after_a_spend_that_was_not_its_mana()
    {
        var table = new InterpreterTable();
        var source = table.Permanent(table.A, CardType.Land, null, null);
        table.Resolve(table.OnStack(table.A, new Sequence(
        [
            new AddMana(new ManaProduction("{G}"), Key: "k"),
            new DelayedTrigger(new ManaSpent("k"), new Draw(new Fixed(1))),
            new AddMana(new ManaProduction("{R}")),
        ]), Asked(source)));

        var delayed = Assert.Single(table.State.DelayedTriggers);
        Assert.Equal("k#0", Assert.IsType<ManaSpent>(delayed.When).ManaKey);

        // A cost paid out of the pool with the other mana: CR 106.6a links the ability to the mana
        // its own key is on, so a spend of anything else leaves both the ability and that mana
        // alone. Nothing here consumes the ability — only the firing does, CR 603.7b.
        table.Resolve(table.OnStack(table.A, new MayPay(new ManaCost("{R}"), new GainLife(new Fixed(1)))));
        table.Answer("yes");

        Assert.Equal(0, table.State.Player(table.A).Mana.Red);
        Assert.Equal(21, table.Life(table.A));
        Assert.Single(table.State.DelayedTriggers);
        Assert.Contains(table.State.Player(table.A).Mana.Provenance, p => p.Key == "k#0");
    }

    [Fact]
    [RuleRef("603.7")]
    public void A_delayed_abilitys_instruction_is_read_off_the_state_and_not_off_a_card()
    {
        var table = new InterpreterTable();
        var source = table.Permanent(table.A, CardType.Land, null, null);
        table.Resolve(table.OnStack(table.A, new DelayedTrigger(
            new ManaSpent("k"),
            new GainLife(new Fixed(3)),
            Once: false), Asked(source)));

        Assert.Single(table.State.DelayedTriggers);

        // CR 603.7: the ability on the stack is not an ability of the card, so its stack entry
        // points at no index of the source and the instruction is found in the game state. Before
        // this, the object resolved and did nothing at all.
        var onStack = table.OnStack(table.A, new DoNothing(), new StackEntry(source, -1, [], [], [], null, false));
        table.Resolve(onStack);

        Assert.Equal(23, table.Life(table.A));
    }

    /// <summary>An ability of that permanent on the stack, CR 603.3: the source is what is asked.</summary>
    private static StackEntry Asked(ObjectId source) => new(source, 0, [], [], [], null, false);
}
