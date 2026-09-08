using MtgEngine.Cards;
using MtgEngine.Rules.Casting;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Interpreter;
using MtgEngine.Rules.Stack;
using MtgEngine.Rules.State;
using CardZone = MtgEngine.Cards.Zone;
using SpellCast = MtgEngine.Rules.Events.SpellCast;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The three costs a spell is offered as it is announced: a printed alternative cost that replaces
/// the mana cost (CR 118.9, <see cref="Face.AlternativeCosts"/>), an optional additional cost a
/// lasting effect offers on other spells and what paying it buys (CR 118.8 with 601.2f,
/// <see cref="OptionalAdditionalCost"/>), and a budget the announced modes are priced against
/// (CR 700.2, <see cref="ChooseMode.Budget"/>). Every fixture here is invented; no card file is read.
/// </summary>
public sealed partial class CastingTests
{
    [Fact]
    [RuleRef("118.9")]
    public void A_printed_alternative_cost_replaces_the_mana_cost_and_is_recorded_on_the_spell()
    {
        // CR 118.9: "you may pay {B} rather than pay this spell's mana cost". The alternative is paid
        // and the mana cost is not, so a caster who could never afford {3}{B} casts the spell.
        var table = new CastingTable();
        var card = table.Add(Wave23Sorcery("{3}{B}", new AlternativeCost(new ManaCost("{B}"))), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 1, 0, 0, 0));

        Assert.NotNull(casting.Check(table.State, new CastSpell(CastingTable.A, card)));
        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, card, CostChoices: [CastingTestsCosts.Alternative]));

        Assert.Contains(cast.Events, e => e is SpellCast);
        Assert.Equal(0, cast.State.Player(CastingTable.A).Mana.Total);

        // The marker "PaidAlternativeCost" with no keyword named reads: the condition asks whether
        // this spell was cast for an alternative cost, and a printed one is one (CR 118.9).
        var entry = cast.State.Objects[StackZone.Top(cast.State)!.Value].Stack;
        Assert.NotNull(entry);
        Assert.Contains(CastingTestsCosts.PaidAlternative, entry.OptionalCostsPaid!);
    }

    [Fact]
    [RuleRef("118.9")]
    public void An_alternative_cost_whose_condition_does_not_hold_cannot_be_taken()
    {
        // CR 118.9: the OnlyIf on the printed alternative is read once, in the caster's context, as
        // the spell is announced — with it false the alternative is not on offer at all.
        var (table, query, casting) = Wave23Table();
        var card = table.Add(
            Wave23Sorcery("{3}{B}", new AlternativeCost(new ManaCost("{B}"), Wave23Condition())),
            CastingTable.A,
            CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 1, 0, 0, 0));

        query.ConditionHolds = false;
        Assert.Equal("118.9", casting.Check(table.State, new CastSpell(CastingTable.A, card, CostChoices: [CastingTestsCosts.Alternative]))?.Rule);
        Assert.Equal(1, query.HoldsReads);
        Assert.Equal(CastingTable.A, query.AskedFor);

        query.ConditionHolds = true;
        Assert.Null(casting.Check(table.State, new CastSpell(CastingTable.A, card, CostChoices: [CastingTestsCosts.Alternative])));
    }

    [Fact]
    [RuleRef("601.2b")]
    public void An_alternative_cost_still_pays_the_additional_cost_the_face_prints()
    {
        // CR 118.9 with 601.2b: an alternative cost replaces the mana cost and nothing else, so the
        // printed additional cost is paid beside it.
        var table = new CastingTable();
        var card = table.Add(
            Wave23Sorcery("{3}{B}", new AlternativeCost(new ManaCost("{B}")), new PayLife(new Fixed(2))),
            CastingTable.A,
            CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 1, 0, 0, 0));

        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, card, CostChoices: [CastingTestsCosts.Alternative]));

        Assert.Equal(18, cast.State.Player(CastingTable.A).Life);
        Assert.Equal(0, cast.State.Player(CastingTable.A).Mana.Total);
        Assert.Contains(cast.Events, e => e is SpellCast);
    }

    [Fact]
    [RuleRef("118.9c")]
    public void One_alternative_cost_per_spell_and_no_more()
    {
        // CR 118.9c: a spell is cast for at most one alternative cost, so naming two printed ones is
        // refused, and so is naming one beside a permission that waives the mana cost — the waiver is
        // itself CR 118.9's alternative.
        var table = new CastingTable();
        var two = table.Add(
            Wave23Sorcery("{3}{B}", new AlternativeCost(new ManaCost("{B}")), null, new AlternativeCost(new ManaCost("{1}"))),
            CastingTable.A,
            CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 1, 0, 0, 1));

        Assert.Equal("118.9c", casting.Check(table.State, new CastSpell(CastingTable.A, two, CostChoices: ["alt:0", "alt:1"]))?.Rule);
        Assert.Null(casting.Check(table.State, new CastSpell(CastingTable.A, two, CostChoices: ["alt:1"])));

        var free = table.Add(Wave23Sorcery("{3}{B}", new AlternativeCost(new ManaCost("{B}"))), CastingTable.A, CardZone.Exile);
        table.Apply([CastingTestsPermissions.ToPlay(table.State, free, CardZone.Exile, free: true)]);

        Assert.Null(casting.Check(table.State, new CastSpell(CastingTable.A, free)));
        Assert.Equal("118.9c", casting.Check(table.State, new CastSpell(CastingTable.A, free, CostChoices: [CastingTestsCosts.Alternative]))?.Rule);
    }

    [Fact]
    [RuleRef("118.9")]
    public void An_alternative_cost_the_face_does_not_print_is_refused_and_never_cast_for_free()
    {
        // CR 118.9: the alternative is the printed one or there is none. A claim naming nothing is
        // refused rather than honoured, the same way CR 118.9's mana-cost waiver is.
        var table = new CastingTable();
        var plain = table.Add(TestFaces.Sorcery("{2}"), CastingTable.A, CardZone.Hand);
        var one = table.Add(Wave23Sorcery("{3}{B}", new AlternativeCost(new ManaCost("{B}"))), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 1, 0, 0, 2));

        Assert.Equal("118.9", casting.Check(table.State, new CastSpell(CastingTable.A, plain, CostChoices: [CastingTestsCosts.Alternative]))?.Rule);
        Assert.Equal("118.9", casting.Check(table.State, new CastSpell(CastingTable.A, one, CostChoices: ["alt:1"]))?.Rule);
        Assert.Equal("601.2h", casting.Check(table.State, new CastSpell(CastingTable.A, one, CostChoices: ["alt:none"]))?.Rule);
        Assert.Null(casting.Check(table.State, new CastSpell(CastingTable.A, plain)));
    }

    [Fact]
    [RuleRef("118.8")]
    public void An_offered_additional_cost_is_paid_and_buys_the_reduction_for_that_cast_only()
    {
        // CR 118.8 with 601.2f: "as an additional cost to cast green permanent spells you may pay 2
        // life; those spells cost {G} less if you paid life this way". The payment and the reduction
        // are one casting's story: the caster who pays casts a spell they could not otherwise afford.
        var (table, query, casting) = Wave23Table();
        var card = table.Add(TestFaces.Creature("{1}{G}{G}"), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 1, 1));
        table.Apply([CastingTestsCosts.Offer(table.State, Wave23Reduction(-1, Color.Green))]);
        query.FilterMatches = true;

        Assert.NotNull(casting.Check(table.State, new CastSpell(CastingTable.A, card)));
        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, card, CostChoices: [CastingTestsCosts.Additional]));

        Assert.Equal(18, cast.State.Player(CastingTable.A).Life);
        Assert.Equal(0, cast.State.Player(CastingTable.A).Mana.Total);
        var entry = cast.State.Objects[StackZone.Top(cast.State)!.Value].Stack;
        Assert.NotNull(entry);
        Assert.Contains(CastingTestsCosts.PaidAdditional, entry.OptionalCostsPaid!);
    }

    [Fact]
    [RuleRef("118.8")]
    public void A_caster_who_declines_the_offer_pays_the_printed_cost_and_nothing_else()
    {
        // CR 118.8: the cost is optional, and declining changes nothing else about the spell — no
        // life is paid and no reduction applies.
        var (table, query, casting) = Wave23Table();
        var card = table.Add(TestFaces.Creature("{1}{G}{G}"), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 2, 1));
        table.Apply([CastingTestsCosts.Offer(table.State, Wave23Reduction(-1, Color.Green))]);
        query.FilterMatches = true;

        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, card));

        Assert.Equal(20, cast.State.Player(CastingTable.A).Life);
        Assert.DoesNotContain(cast.Events, e => e is LifeChanged);
        Assert.Equal(0, cast.State.Player(CastingTable.A).Mana.Total);
    }

    [Fact]
    [RuleRef("601.2f")]
    public void A_colored_reduction_takes_only_that_colour_and_a_generic_one_only_the_generic()
    {
        // CR 601.2f with 118.7: "this effect reduces only the amount of green mana you pay" — a cost
        // with no green left is not reduced further, and the generic road takes the generic part.
        var (table, query, casting) = Wave23Table();
        var colorless = table.Add(TestFaces.Sorcery("{2}"), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 2));
        table.Apply([CastingTestsCosts.Offer(table.State, Wave23Reduction(-1, Color.Green))]);
        query.FilterMatches = true;

        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, colorless, CostChoices: [CastingTestsCosts.Additional]));

        Assert.Equal(18, cast.State.Player(CastingTable.A).Life);
        Assert.Equal(0, cast.State.Player(CastingTable.A).Mana.Total);

        var (generic, other, casting2) = Wave23Table();
        var three = generic.Add(TestFaces.Sorcery("{3}"), CastingTable.A, CardZone.Hand);
        generic.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 1));
        generic.Apply([CastingTestsCosts.Offer(generic.State, Wave23Reduction(-2, null))]);
        other.FilterMatches = true;

        var reduced = casting2.Cast(generic.State, new CastSpell(CastingTable.A, three, CostChoices: [CastingTestsCosts.Additional]));

        Assert.Equal(18, reduced.State.Player(CastingTable.A).Life);
        Assert.Equal(0, reduced.State.Player(CastingTable.A).Mana.Total);
    }

    [Fact]
    [RuleRef("118.8")]
    public void An_offer_nothing_makes_and_one_taken_twice_are_both_refused()
    {
        // CR 118.8: the offer is an effect's, never a caster's to assert — a spell the effect's
        // filter does not admit is offered nothing. CR 601.2b: one offer is one additional cost.
        var (table, query, casting) = Wave23Table();
        var card = table.Add(TestFaces.Creature("{1}{G}{G}"), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 2, 1));

        Assert.Equal("118.8", casting.Check(table.State, new CastSpell(CastingTable.A, card, CostChoices: [CastingTestsCosts.Additional]))?.Rule);

        table.Apply([CastingTestsCosts.Offer(table.State, Wave23Reduction(-1, Color.Green))]);
        query.FilterMatches = false;
        Assert.Equal("118.8", casting.Check(table.State, new CastSpell(CastingTable.A, card, CostChoices: [CastingTestsCosts.Additional]))?.Rule);

        query.FilterMatches = true;
        Assert.Equal("601.2b", casting.Check(table.State, new CastSpell(CastingTable.A, card, CostChoices: ["addcost", "addcost:0"]))?.Rule);
    }

    [Fact]
    [RuleRef("118.8")]
    public void An_offer_that_buys_a_shape_the_engine_cannot_apply_is_refused_and_not_paid()
    {
        // A crash is never the answer: what paying buys is a continuous effect, and one this slice
        // cannot apply to a single casting is refused naming CR 118.8 rather than paid for and lost.
        var (table, query, casting) = Wave23Table();
        var card = table.Add(TestFaces.Creature("{1}{G}{G}"), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 2, 1));
        table.Apply([CastingTestsCosts.Offer(table.State, new MayPlayFromZone(new You(), CardZone.Graveyard))]);
        query.FilterMatches = true;

        Assert.Equal("118.8", casting.Check(table.State, new CastSpell(CastingTable.A, card, CostChoices: [CastingTestsCosts.Additional]))?.Rule);
        Assert.Equal(20, table.State.Player(CastingTable.A).Life);
    }

    [Fact]
    [RuleRef("700.2")]
    public void Priced_modes_are_held_to_the_budget_and_not_to_the_printed_maximum()
    {
        // CR 700.2: "choose up to five {P} worth of modes. You may choose the same mode more than
        // once." The prices total no more than the budget; Max does not count modes while a budget
        // rules them, so three of the cheap mode is legal under a Max of one.
        var table = new CastingTable();
        var card = table.Add(TestFaces.Instant("{1}", new SpellAbility(Wave23Budget())), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 1));

        Assert.Null(casting.Check(table.State, new CastSpell(CastingTable.A, card, Modes: [0, 1])));
        Assert.Null(casting.Check(table.State, new CastSpell(CastingTable.A, card, Modes: [0, 0, 0])));
        Assert.Equal("700.2", casting.Check(table.State, new CastSpell(CastingTable.A, card, Modes: [1, 1]))?.Rule);
        Assert.Equal("700.2", casting.Check(table.State, new CastSpell(CastingTable.A, card, Modes: [0, 1, 1]))?.Rule);

        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, card, Modes: [0, 0, 0]));
        var entry = cast.State.Objects[Assert.IsType<SpellCast>(cast.Events.Last()).Spell].Stack;
        Assert.NotNull(entry);
        Assert.Equal(new[] { 0, 0, 0 }, entry.Modes);
    }

    [Fact]
    [RuleRef("700.2d")]
    public void A_budget_leaves_the_repeat_rule_and_the_floor_where_they_were()
    {
        // CR 700.2d: SameModeTwice still governs a repeat under a budget, and CR 700.2's Min still
        // is the floor — a budget is a ceiling on price, not on how few modes may be chosen. Min 0
        // is "up to", so an announcement naming none is legal and costs nothing.
        var table = new CastingTable();
        var once = table.Add(TestFaces.Instant("{1}", new SpellAbility(Wave23Budget(sameModeTwice: false))), CastingTable.A, CardZone.Hand);
        var two = table.Add(TestFaces.Instant("{1}", new SpellAbility(Wave23Budget(min: 2))), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 1));

        Assert.Equal("700.2d", casting.Check(table.State, new CastSpell(CastingTable.A, once, Modes: [0, 0]))?.Rule);
        Assert.Equal("700.2", casting.Check(table.State, new CastSpell(CastingTable.A, two, Modes: [0]))?.Rule);
        Assert.Null(casting.Check(table.State, new CastSpell(CastingTable.A, once)));
    }

    [Fact]
    [RuleRef("700.2")]
    public void A_mode_a_budget_cannot_price_is_refused_rather_than_taken_for_nothing()
    {
        // CR 700.2: a budget and the prices mean nothing apart. A mode with no price under a budget
        // is refused naming the rule, never counted as free.
        var table = new CastingTable();
        var free = table.Add(
            TestFaces.Instant("{1}", new SpellAbility(Wave23Budget() with { Modes = [new Mode("Draw a card.", new Draw(new Fixed(1)))] })),
            CastingTable.A,
            CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 1));

        Assert.Equal("700.2", casting.Check(table.State, new CastSpell(CastingTable.A, free, Modes: [0]))?.Rule);
    }

    /// <summary>A table whose query answers a condition and a filter as told, for the two roads that read one.</summary>
    private (CastingTable Table, CastingTestsCostQuery Query, CastingSystem Casting) Wave23Table()
    {
        var query = new CastingTestsCostQuery();
        return (new CastingTable(), query, new CastingSystem(new CastingFakeZones(), query, new PrintedCharacteristics(), interpreter));
    }

    /// <summary>A sorcery with printed alternative costs and, optionally, a printed additional cost.</summary>
    private static CardDefinition Wave23Sorcery(string cost, AlternativeCost first, Cost? additional = null, AlternativeCost? second = null) => new(
        "CASTING TESTS EDICT",
        "Casting Tests Edict",
        Layout.Normal,
        [
            new Face(
                "Casting Tests Edict",
                FaceRole.Main,
                new TypeLine(CardType.Sorcery),
                cost,
                AdditionalCosts: additional is null ? null : [additional],
                AlternativeCosts: second is null ? [first] : [first, second]),
        ]);

    /// <summary>What paying the offered cost buys: a reduction of one, tied to a colour or generic.</summary>
    private static CostModifier Wave23Reduction(int delta, Color? color) => new(
        new ObjectFilter(),
        new Fixed(delta),
        new You(),
        GenericOnly: color is null,
        OnlyColor: color);

    /// <summary>An opaque condition the fake query answers as told; what it says is not this reader's business.</summary>
    private static Condition Wave23Condition() =>
        new Controls(new You(), new ObjectFilter(IsCommander: true), new Comparison(ComparisonOp.Ge, new Fixed(1)));

    /// <summary>Two modes priced {1} and {2} against a budget of three, CR 700.2.</summary>
    private static ChooseMode Wave23Budget(int min = 0, bool sameModeTwice = true) => new(
        [
            new Mode("Draw a card.", new Draw(new Fixed(1)), new ManaCost("{1}")),
            new Mode("Draw two cards.", new Draw(new Fixed(2)), new ManaCost("{2}")),
        ],
        min,
        Max: 1,
        SameModeTwice: sameModeTwice,
        Budget: new Fixed(3));
}

/// <summary>
/// The words a command announces an offered cost with and the lasting effect that offers one, held
/// here rather than spelled in each test so a rename of the channel breaks the tests that ride it.
/// </summary>
internal static class CastingTestsCosts
{
    /// <summary>Takes the first printed alternative cost, CR 118.9.</summary>
    public const string Alternative = CastPlan.AlternativeCostChoice;

    /// <summary>Takes the first offered additional cost, CR 118.8.</summary>
    public const string Additional = CastPlan.AdditionalCostChoice;

    /// <summary>What a spell cast for a printed alternative cost records, CR 118.9.</summary>
    public const string PaidAlternative = CastPlan.PaidPrintedAlternativeCost;

    /// <summary>What a spell whose caster took an offered additional cost records, CR 118.8.</summary>
    public const string PaidAdditional = CastPlan.PaidOfferedAdditionalCost;

    /// <summary>
    /// "As an additional cost to cast spells my filter admits, you may pay 2 life" as a static
    /// ability's lasting effect stands (CR 118.8, 604.1): no card named and no player named, so the
    /// filter and the <c>Who</c> half are read through the query, which is what a real board does.
    /// </summary>
    public static ContinuousEffectCreated Offer(GameState state, ContinuousEffect? bought)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new ContinuousEffectCreated(new ActiveEffect(
            1,
            new OptionalAdditionalCost(new ObjectFilter(), new PayLife(new Fixed(2)), new You(), bought),
            null,
            CastingTable.A,
            [],
            [],
            new UntilEndOfTurn(),
            state.NextTimestamp,
            FromStaticAbility: true));
    }
}

/// <summary>
/// Answers the one condition and the one filter these two roads read, as told, and counts the
/// condition's reads so a test can see that CR 118.9's OnlyIf was asked once at announcement.
/// </summary>
internal sealed class CastingTestsCostQuery : IQuery
{
    public string Name => "casting cost query";

    public bool ConditionHolds { get; set; }

    public bool FilterMatches { get; set; }

    public int HoldsReads { get; private set; }

    public PlayerId? AskedFor { get; private set; }

    public int Evaluate(GameState state, Amount amount, EffectContext context) =>
        amount is Fixed number ? number.N : throw new NotSupportedException();

    public bool Matches(GameState state, ObjectFilter filter, ObjectId candidate, EffectContext context) => FilterMatches;

    public bool Holds(GameState state, Condition condition, EffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        HoldsReads++;
        AskedFor = context.Controller;
        return ConditionHolds;
    }

    public ImmutableList<ObjectId> Resolve(GameState state, Selector selector, EffectContext context) => throw new NotSupportedException();

    public ImmutableList<PlayerId> Players(GameState state, PlayerRef player, EffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return [context.Controller];
    }
}
