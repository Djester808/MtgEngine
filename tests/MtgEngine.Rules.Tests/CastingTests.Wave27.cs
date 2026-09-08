using MtgEngine.Cards;
using MtgEngine.Rules.Casting;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Interpreter;
using MtgEngine.Rules.Stack;
using MtgEngine.Rules.State;
using AbilityActivated = MtgEngine.Rules.Events.AbilityActivated;
using CardZone = MtgEngine.Cards.Zone;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Wave 27: the flash permission the interpreter creates read at the timing check (CR 702.8b), a
/// cost paid for a rule rather than for a spell (CR 118.6 with 601.2h), and CR 605.1a read in one
/// place. Every refusal is checked by its rule number rather than its wording.
/// </summary>
public sealed class CastingWave27Tests
{
    private readonly CastingSystem casting;

    public CastingWave27Tests()
    {
        var chars = new PrintedCharacteristics();
        var zones = new CastingFakeZones();
        var query = new Query.Query(chars);
        casting = new CastingSystem(zones, query, chars, new EffectInterpreter(query, zones, new CastingPassThroughReplacements(), chars));
    }

    [Fact]
    [RuleRef("702.8b")]
    public void A_flash_permission_lets_a_sorcery_speed_spell_be_cast_whenever_its_caster_holds_priority()
    {
        // CR 307.1: a sorcery is cast in its controller's main phase with an empty stack, so the
        // end step refuses it. CR 702.8b: a permission to cast it as though it had flash makes it
        // castable any time its controller could cast an instant — timing and nothing else, so the
        // {2} is still paid and the pool empties.
        var table = Wave27Table(out var spell);
        table.MoveToStep(Step.End);

        Assert.Equal("307.1", casting.Check(table.State, new CastSpell(CastingTable.A, spell))?.Rule);

        table.Apply([Wave27Flash(table.State, CastingTable.A)]);
        Assert.Null(casting.Check(table.State, new CastSpell(CastingTable.A, spell)));

        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, spell));
        Assert.Single(cast.Events.OfType<Rules.Events.SpellCast>());
        Assert.Equal(0, cast.State.Player(CastingTable.A).Mana.Total);
    }

    [Fact]
    [RuleRef("702.8b")]
    public void A_flash_permission_admits_only_the_spells_its_filter_names()
    {
        // CR 702.8b: "you may cast creature spells as though they had flash" is a permission for
        // the spells the filter admits and for no others, so the sorcery is still held to CR 307.1
        // while the creature is not.
        var table = Wave27Table(out var spell);
        var creature = table.Add(TestFaces.Creature("{1}"), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 2));
        table.MoveToStep(Step.End);
        table.Apply([Wave27Flash(table.State, CastingTable.A, new ObjectFilter(Types: CardType.Creature))]);

        Assert.Equal("307.1", casting.Check(table.State, new CastSpell(CastingTable.A, spell))?.Rule);
        Assert.Null(casting.Check(table.State, new CastSpell(CastingTable.A, creature)));
    }

    [Fact]
    [RuleRef("702.8b")]
    public void A_flash_permission_granted_to_another_player_grants_this_one_nothing()
    {
        // CR 702.8b with 613.1: a continuous effect applies to what it says it applies to. The
        // permission is on the other player, so this caster is still held to CR 307.1.
        var table = Wave27Table(out var spell);
        table.MoveToStep(Step.End);
        table.Apply([Wave27Flash(table.State, CastingTable.B)]);

        Assert.Equal("307.1", casting.Check(table.State, new CastSpell(CastingTable.A, spell))?.Rule);
    }

    [Fact]
    [RuleRef("601.2a")]
    public void A_flash_filter_is_asked_about_the_card_in_the_zone_the_card_is_in()
    {
        // CR 601.2a: the card does not become a spell until it moves to the stack, and the timing
        // check happens before that, so there is nothing on the stack to match. A filter that
        // names no zone is read against the battlefield (Query.ZoneOf), which would admit no card
        // in a hand at all — the permission is asked about the card where the card is.
        var table = Wave27Table(out var spell);
        table.MoveToStep(Step.End);
        table.Apply([Wave27Flash(table.State, CastingTable.A, new ObjectFilter(Types: CardType.Sorcery))]);

        Assert.Null(casting.Check(table.State, new CastSpell(CastingTable.A, spell)));
        Assert.Equal(CardZone.Hand, table.State.Objects[spell].Zone.Zone);
    }

    [Fact]
    [RuleRef("118.6")]
    public void A_cost_paid_for_a_rule_is_refused_when_the_payer_cannot_pay_it()
    {
        // CR 118.6 with 601.2h: a player with nothing in their pool cannot pay {1}. The answer has
        // to be available before it is taken — an "unless that player pays" whose cost is
        // unpayable is not paid, and the unpaid half of the effect happens (CR 118.12a).
        var table = new CastingTable();

        var refused = casting.CanPayForRule(table.State, CastingTable.B, new ManaCost("{1}"));
        Assert.NotNull(refused);
        Assert.Equal("601.2h", refused.Rule);
        Assert.Equal("601.2h", Assert.Throws<RefusedException>(
            () => casting.PayForRule(table.State, CastingTable.B, new ManaCost("{1}"))).Rule);
        Assert.Equal(0, table.State.Player(CastingTable.B).Mana.Total);
    }

    [Fact]
    [RuleRef("118.6")]
    public void A_cost_paid_for_a_rule_empties_the_pool_it_was_paid_from()
    {
        // CR 118.6: the same entry point pays when the payer can. The trial payment CanPayForRule
        // makes changes nothing, so the pool it reports on is the pool PayForRule then spends.
        var table = new CastingTable();
        table.AddMana(CastingTable.B, new ManaPool(0, 0, 0, 0, 0, 1));

        Assert.Null(casting.CanPayForRule(table.State, CastingTable.B, new ManaCost("{1}")));
        Assert.Equal(1, table.State.Player(CastingTable.B).Mana.Total);

        var paid = casting.PayForRule(table.State, CastingTable.B, new ManaCost("{1}"));
        Assert.Equal(0, paid.State.Player(CastingTable.B).Mana.Total);
        Assert.Single(paid.Events.OfType<ManaChanged>());
    }

    [Fact]
    [RuleRef("605.1a")]
    public void An_ability_that_adds_mana_and_draws_a_card_is_not_a_mana_ability()
    {
        // CR 605.1a's fourth criterion: an ability whose cost or effect moves a card to or from a
        // library is not a mana ability however much mana it adds. It therefore uses the stack
        // (CR 602.2b) instead of resolving where it stands (CR 605.3a), so nothing is added to the
        // pool by the activation itself and the activation event names its stack object.
        var table = new CastingTable();
        var engine = table.Add(
            TestFaces.Artifact("{2}", new ActivatedAbility(
                [new TapSelf()],
                new Sequence([new Cards.AddMana(new ManaProduction(Fixed: "{G}")), new Draw(new Fixed(1))]))),
            CastingTable.A,
            CardZone.Battlefield,
            summoningSick: false);

        var activated = casting.Activate(table.State, new ActivateAbility(CastingTable.A, engine, 0));

        Assert.False(StackZone.IsEmpty(activated.State));
        Assert.NotNull(Assert.Single(activated.Events.OfType<AbilityActivated>()).StackObject);
        Assert.Equal(0, activated.State.Player(CastingTable.A).Mana.Total);
    }

    /// <summary>A table with a {2} sorcery in its caster's hand and exactly the mana to cast it.</summary>
    private static CastingTable Wave27Table(out ObjectId spell)
    {
        var table = new CastingTable();
        spell = table.Add(TestFaces.Sorcery(), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 2));
        return table;
    }

    /// <summary>
    /// "You may cast [spells] as though they had flash", CR 702.8b, as E9u's <c>Apply</c> arm
    /// creates it: a <see cref="MayCastAsThoughFlash"/> on <c>AppliesToPlayers</c> and never on
    /// <c>AppliesTo</c>, because the permission reaches a player and not an object.
    /// </summary>
    private static ContinuousEffectCreated Wave27Flash(GameState state, PlayerId who, ObjectFilter? what = null) =>
        new(new ActiveEffect(
            1,
            new MayCastAsThoughFlash(new You(), what),
            null,
            who,
            [],
            [who],
            new UntilEndOfTurn(),
            state.NextTimestamp,
            FromStaticAbility: false));
}
