using MtgEngine.Cards;
using MtgEngine.Rules.Casting;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Interpreter;
using MtgEngine.Rules.State;
using CardZone = MtgEngine.Cards.Zone;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Wave 28: an additional cost that is a choice between costs, CR 601.2b — "discard a card or pay
/// 3 life" — chosen as the spell is announced and then paid, and the CR 118.6 entry point read the
/// way the combat tax will call it (E3p § 3 with E4i § 2). Every refusal is checked by its rule
/// number rather than its wording.
/// </summary>
public sealed class CastingWave28Tests
{
    private readonly CastingSystem casting;

    public CastingWave28Tests()
    {
        var chars = new PrintedCharacteristics();
        var zones = new CastingFakeZones();
        var query = new Query.Query(chars);
        casting = new CastingSystem(zones, query, chars, new EffectInterpreter(query, zones, new CastingPassThroughReplacements(), chars));
    }

    [Fact]
    [RuleRef("601.2b")]
    public void A_choice_between_costs_pays_the_option_the_command_names()
    {
        // CR 601.2b: the choice is made as the spell is announced, and the command carries it on
        // CostChoices like every other cost choice — "cost:1" is the second option as printed.
        var table = Wave28Table(out var spell, Wave28Choice(), lands: 2);

        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, spell, CostChoices: ["cost:1"]));

        Assert.Equal(17, cast.State.Player(CastingTable.A).Life);
        Assert.Equal(2, Wave28Hand(cast.State));
    }

    [Fact]
    [RuleRef("601.2b")]
    public void A_choice_between_costs_the_command_names_none_of_takes_the_first_option_that_can_be_paid()
    {
        // CR 601.2b with 601.2c: "discard a card" over a hand of three is a choice nobody made, so
        // it cannot be paid (CR 601.2h: no object is picked for the player). The life payment can,
        // so it is the one taken — this is Bitter Triumph's shape, and before this the whole cost
        // was refused as one a ChooseCost could not pay.
        var table = Wave28Table(out var spell, Wave28Choice(), lands: 2);

        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, spell));

        Assert.Equal(17, cast.State.Player(CastingTable.A).Life);
        Assert.Equal(2, Wave28Hand(cast.State));
    }

    [Fact]
    [RuleRef("601.2b")]
    public void A_choice_between_costs_takes_the_first_option_when_the_command_names_the_object_that_pays_it()
    {
        // CR 601.2c: the discard becomes payable the moment the command names which card pays it,
        // and the printed order then puts it first — the life is not touched.
        var table = Wave28Table(out var spell, Wave28Choice(), lands: 2);
        var land = Wave28Land(table.State);

        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, spell, CostObjects: [land]));

        Assert.Equal(20, cast.State.Player(CastingTable.A).Life);
        Assert.Equal(1, Wave28Hand(cast.State));
        Assert.False(cast.State.Objects.ContainsKey(land));
    }

    [Fact]
    [RuleRef("601.2b")]
    public void A_choice_between_costs_is_refused_when_the_command_names_an_option_it_does_not_offer()
    {
        // CR 601.2b: a spell is announced for one of the costs it offers and no other.
        var table = Wave28Table(out var spell, Wave28Choice(), lands: 2);

        Assert.Equal("601.2b", Assert.Throws<RefusedException>(
            () => casting.Cast(table.State, new CastSpell(CastingTable.A, spell, CostChoices: ["cost:9"]))).Rule);
        Assert.Equal(20, table.State.Player(CastingTable.A).Life);
    }

    [Fact]
    [RuleRef("601.2h")]
    public void A_choice_between_costs_none_of_which_can_be_paid_is_refused_and_nothing_is_taken()
    {
        // CR 601.2h: partial payments are not allowed, so a choice whose every option is beyond
        // the payer refuses the announcement rather than taking the nearest thing to a payment.
        var table = Wave28Table(out var spell, new ChooseCost([new PayLife(new Fixed(99)), new PayLife(new Fixed(98))]), lands: 2);

        Assert.Equal("601.2h", Assert.Throws<RefusedException>(
            () => casting.Cast(table.State, new CastSpell(CastingTable.A, spell))).Rule);
        Assert.Equal(20, table.State.Player(CastingTable.A).Life);
        Assert.Equal(CardZone.Hand, table.State.Objects[spell].Zone.Zone);
    }

    [Fact]
    [RuleRef("601.2c")]
    public void The_cost_question_offers_what_the_option_a_choice_takes_is_asking_for()
    {
        // CR 601.2c: the question channel and the payment read the same table, so what a choice
        // between costs offers is what its chosen option offers — the hand, when the option named
        // is the discard, and nothing when it is the life payment.
        var table = Wave28Table(out var spell, Wave28Choice(), lands: 2);

        var discard = casting.CostChoices(table.State, new CastSpell(CastingTable.A, spell, CostChoices: ["cost:0"]));
        Assert.Equal(3, Assert.Single(discard).Legal.Count);
        Assert.Empty(casting.CostChoices(table.State, new CastSpell(CastingTable.A, spell, CostChoices: ["cost:1"])));
    }

    [Fact]
    [RuleRef("118.6")]
    public void A_cost_paid_for_a_rule_pays_a_cost_that_is_not_mana_and_needs_no_source()
    {
        // CR 118.6 with 508.1j: the attack tax is paid by a player, for a rule, with no spell and
        // no ability behind it. E4i § 2 asked for exactly this entry point, so it is asserted from
        // the caller's side: a non-mana cost, no source, and the payer's own resources.
        var table = new CastingTable();

        Assert.Null(casting.CanPayForRule(table.State, CastingTable.B, new PayLife(new Fixed(2))));
        var paid = casting.PayForRule(table.State, CastingTable.B, new PayLife(new Fixed(2)));

        Assert.Equal(18, paid.State.Player(CastingTable.B).Life);
        Assert.Equal(20, table.State.Player(CastingTable.B).Life);
    }

    [Fact]
    [RuleRef("118.6")]
    public void A_cost_paid_for_a_rule_that_wants_a_source_it_was_given_none_of_is_refused_and_not_thrown()
    {
        // CR 118.6: a cost the caller cannot describe is refused naming the rule. Combat calls this
        // with no source for a tax, so a cost that pays with a permanent must come back as a
        // Refusal the declaration can report — never as a bare throw the caller cannot read.
        var table = new CastingTable();

        var refused = casting.CanPayForRule(table.State, CastingTable.B, new SacrificeSelf());

        Assert.NotNull(refused);
        Assert.Equal("601.2h", refused.Rule);
    }

    [Fact]
    [RuleRef("118.6")]
    public void A_cost_paid_for_a_rule_reads_the_X_it_was_announced_with()
    {
        // CR 118.6 with 601.2h: X is the caller's, counted before the payment, so "pay {X}" for a
        // rule empties exactly X out of the pool and no more.
        var table = new CastingTable();
        table.AddMana(CastingTable.B, new ManaPool(0, 0, 0, 0, 0, 3));

        var paid = casting.PayForRule(table.State, CastingTable.B, new ManaCost("{X}"), x: 2);

        Assert.Equal(1, paid.State.Player(CastingTable.B).Mana.Total);
    }

    /// <summary>"As an additional cost to cast this spell, discard a card or pay 3 life", CR 601.2b.</summary>
    private static ChooseCost Wave28Choice() =>
        new([new DiscardCost(), new PayLife(new Fixed(3))]);

    /// <summary>A {1} instant whose one additional cost is a choice, with a hand to pay it from.</summary>
    private static CastingTable Wave28Table(out ObjectId spell, ChooseCost choice, int lands)
    {
        var table = new CastingTable();
        spell = table.Add(
            new CardDefinition(
                "CASTING TESTS CHOICE",
                "Casting Tests Choice",
                Layout.Normal,
                [
                    new Face(
                        "Casting Tests Choice",
                        FaceRole.Main,
                        new TypeLine(CardType.Instant),
                        "{1}",
                        Abilities: [new SpellAbility(new Draw(new Fixed(1)))],
                        AdditionalCosts: [choice]),
                ]),
            CastingTable.A,
            CardZone.Hand);
        for (var i = 0; i < lands; i++)
            table.Add(TestFaces.Land(), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 1));
        return table;
    }

    /// <summary>How many cards the caster still holds, CR 601.2h: a discard takes one, a life payment none.</summary>
    private static int Wave28Hand(GameState state) =>
        state.Objects.Values.Count(o => o.Zone.Zone == CardZone.Hand && o.Owner == CastingTable.A);

    /// <summary>The last land in the caster's hand, which is the card a named discard pays with.</summary>
    private static ObjectId Wave28Land(GameState state) =>
        state.Objects.Values
            .Where(o => o.Zone.Zone == CardZone.Hand && o.Owner == CastingTable.A)
            .OrderBy(o => o.Id.Value)
            .Last().Id;
}
