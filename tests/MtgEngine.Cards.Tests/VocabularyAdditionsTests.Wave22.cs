namespace MtgEngine.Cards.Tests;

/// <summary>
/// Wave 22 (seam-requests/V12.md): the five gaps three independent translators each hit on a real
/// card of the second corpus slice. An optional additional cost a permanent puts on other spells
/// with a colour-restricted reduction gated on paying it (CR 118.8, 601.2f), the excess of a damage
/// event dealt to a player instead (CR 120.3d with 702.19c), a mode list spent against a budget
/// counted in symbols (CR 700.2), the draw exception "except the first one they draw in each of
/// their draw steps" (CR 121.1) and a mandatory mid-resolution cost with an "if you do"
/// (CR 608.2c, 118.12). Each has to round-trip, lint where its halves can contradict, and leave the
/// IR version where it is.
/// </summary>
public sealed partial class VocabularyAdditionsTests
{
    private static CardDefinition Wave22DefinitionWith(params Ability[] abilities) => new(
        OracleId: "00000000-0000-0000-0000-000000000f22",
        Name: "Wave Twenty Two Addition",
        Layout: Layout.Normal,
        Faces:
        [
            new Face(
                Name: "Wave Twenty Two Addition",
                Role: FaceRole.Main,
                Types: new TypeLine(CardType.Creature),
                ManaCost: "{3}{G}{G}",
                Abilities: abilities,
                OracleText: "Wave twenty two text."),
        ]);

    private static CardFile Wave22FileOf(CardDefinition definition) => new(
        OracleId: definition.OracleId,
        Name: definition.Name,
        OracleHash: OracleHashing.Of(definition.Faces.Select(f => f.OracleText)),
        IrVersion: IrVersion.Current,
        Definition: definition,
        Status: ImplementationStatus.Translated,
        Source: new TemplateProvenance("test"));

    private static CardDefinition Wave22RoundTrip(CardDefinition definition) =>
        CardFileSerializer.ReadDefinition(CardFileSerializer.WriteDefinition(definition));

    /// <summary>The errors a file of these abilities lints to; a reduction's negative literal is a warning of its own (L004).</summary>
    private static IReadOnlyList<LintFinding> Wave22Errors(params Ability[] abilities) =>
        Linter.Check(Wave22FileOf(Wave22DefinitionWith(abilities))).Findings.Where(f => f.Severity == LintSeverity.Error).ToList();

    private static T Wave22FirstEffect<T>(CardDefinition definition)
        where T : Effect => Assert.IsType<T>(Assert.IsType<SpellAbility>(definition.Faces[0].Abilities![0]).Do);

    private static ObjectFilter Wave22GreenPermanentSpells => new(Colors: Color.Green, Spell: true, Permanent: true);

    [Fact]
    public void A_permanent_can_offer_an_additional_cost_on_other_spells_and_gate_a_coloured_reduction_on_it()
    {
        // CR 118.8 with 601.2f: "as an additional cost to cast green permanent spells, you may pay
        // 2 life. Those spells cost {G} less to cast if you paid life this way. This effect reduces
        // only the amount of green mana you pay."
        var reduction = new CostModifier(Wave22GreenPermanentSpells, new Fixed(-1), new You(), GenericOnly: false, OnlyColor: Color.Green);
        var effect = new OptionalAdditionalCost(Wave22GreenPermanentSpells, new PayLife(new Fixed(2)), new You(), reduction);
        var ability = new StaticAbility(effect);
        var back = Wave22RoundTrip(Wave22DefinitionWith(ability));

        var read = Assert.IsType<OptionalAdditionalCost>(Assert.IsType<StaticAbility>(back.Faces[0].Abilities![0]).Effect);
        Assert.Equal(new Fixed(2), Assert.IsType<PayLife>(read.Cost).Amount);
        var paid = Assert.IsType<CostModifier>(read.IfPaid);
        Assert.Equal(Color.Green, paid.OnlyColor);
        Assert.False(paid.GenericOnly);
        Assert.Empty(Wave22Errors(ability));
    }

    [Fact]
    public void An_additional_cost_that_buys_nothing_and_a_reduction_that_takes_both_halves_are_refused()
    {
        // L030, CR 118.8 with 601.2b and 601.2f: an optional cost with nothing gated on it is a
        // cost no caster would take, and a reduction comes off the generic half or off one colour.
        var buysNothing = new StaticAbility(new OptionalAdditionalCost(Wave22GreenPermanentSpells, new PayLife(new Fixed(2)), new You()));
        var bothHalves = new StaticAbility(new CostModifier(Wave22GreenPermanentSpells, new Fixed(-1), new You(), GenericOnly: true, OnlyColor: Color.Green));

        Assert.Contains(Wave22Errors(buysNothing), f => f.Code == "L030");
        Assert.Contains(Wave22Errors(bothHalves), f => f.Code == "L030");
    }

    [Fact]
    public void Excess_damage_can_go_to_the_damaged_creatures_controller_under_the_condition_the_card_prints()
    {
        // CR 120.3d with 702.19c: "target creature you control deals damage equal to its power to
        // target creature you don't control. If the creature you control has trample, excess damage
        // is dealt to that creature's controller instead."
        var yours = new Target(new ObjectFilter(Types: CardType.Creature, ControlledBy: new You()));
        var theirs = new Target(new ObjectFilter(Types: CardType.Creature, ControlledBy: new EachOpponent()));
        var ability = new SpellAbility(new DealDamage(
            new PowerOf(yours),
            To: theirs,
            Source: yours,
            ExcessTo: new ControllerOf(theirs),
            ExcessIf: new HasKeyword(yours, KeywordKind.Trample)));
        var back = Wave22RoundTrip(Wave22DefinitionWith(ability));

        var read = Wave22FirstEffect<DealDamage>(back);
        Assert.IsType<ControllerOf>(read.ExcessTo);
        Assert.Equal(KeywordKind.Trample, Assert.IsType<HasKeyword>(read.ExcessIf).Keyword);
        Assert.Empty(Wave22Errors(ability));
    }

    [Fact]
    public void Excess_damage_with_nothing_damaged_and_a_gate_with_nowhere_to_send_it_are_refused()
    {
        // L031, CR 120.3d: damage to a player is never in excess of anything, and a condition on a
        // redirection that is not there gates nothing.
        var noObject = new SpellAbility(new DealDamage(new Fixed(3), ToPlayer: new TargetPlayer(new PlayerFilter()), ExcessTo: new You()));
        var noRedirect = new SpellAbility(new DealDamage(new Fixed(3), To: new Target(new ObjectFilter(Types: CardType.Creature)), ExcessIf: new HasKeyword(new Self(), KeywordKind.Trample)));

        Assert.Contains(Wave22Errors(noObject), f => f.Code == "L031");
        Assert.Contains(Wave22Errors(noRedirect), f => f.Code == "L031");
    }

    [Fact]
    public void Modes_can_carry_a_price_spent_against_a_budget_counted_in_symbols()
    {
        // CR 700.2: "choose up to five {P} worth of modes. You may choose the same mode more than
        // once." Each printed mode carries the symbols it costs against that budget.
        var choose = new ChooseMode(
            [
                new Mode("{P}", new PutCounters(new Target(new ObjectFilter(Types: CardType.Creature)), "+1/+1", new Fixed(1)), new ManaCost("{P}")),
                new Mode("{P}{P}", new Destroy(new Each(new ObjectFilter(Types: CardType.Artifact))), new ManaCost("{P}{P}")),
                new Mode("{P}{P}{P}", new Draw(new Fixed(3)), new ManaCost("{P}{P}{P}")),
            ],
            Min: 0,
            SameModeTwice: true,
            Budget: new Fixed(5));
        var ability = new SpellAbility(choose);
        var back = Wave22RoundTrip(Wave22DefinitionWith(ability));

        var read = Wave22FirstEffect<ChooseMode>(back);
        Assert.Equal(new Fixed(5), read.Budget);
        Assert.True(read.SameModeTwice);
        Assert.Equal("{P}{P}{P}", Assert.IsType<ManaCost>(read.Modes[2].Cost).Mana);
        Assert.Empty(Wave22Errors(ability));
    }

    [Fact]
    public void A_budget_without_prices_and_a_price_without_a_budget_are_refused()
    {
        // L032, CR 700.2: the budget and the prices are the two halves of one printed line, and
        // either half alone leaves a choice nothing can rule.
        var unpriced = new SpellAbility(new ChooseMode([new Mode("one", new Draw(new Fixed(1)))], Budget: new Fixed(5)));
        var unbudgeted = new SpellAbility(new ChooseMode([new Mode("{P}", new Draw(new Fixed(1)), new ManaCost("{P}"))]));

        Assert.Contains(Wave22Errors(unpriced), f => f.Code == "L032");
        Assert.Contains(Wave22Errors(unbudgeted), f => f.Code == "L032");
    }

    [Fact]
    public void A_draw_trigger_can_spare_the_first_draw_of_each_draw_step()
    {
        // CR 121.1 with 504.1: "whenever an opponent draws a card except the first one they draw in
        // each of their draw steps, this creature deals 1 damage to any target."
        var ability = new TriggeredAbility(
            new Draws(new EachOpponent(), ExceptFirstInDrawStep: true),
            new DealDamage(new Fixed(1), new AnyTarget()));
        var back = Wave22RoundTrip(Wave22DefinitionWith(ability));

        var read = Assert.IsType<Draws>(Assert.IsType<TriggeredAbility>(back.Faces[0].Abilities![0]).When);
        Assert.True(read.ExceptFirstInDrawStep);
        Assert.False(read.FirstEachTurn);
        Assert.Null(read.DuringDrawStep);
        Assert.Empty(Wave22Errors(ability));
    }

    [Fact]
    public void The_spared_draw_cannot_also_be_the_only_draw_that_counts()
    {
        // L033, CR 121.1: one reading admits the first draw alone and the other admits every draw
        // but it; and a trigger that has already refused the draw step has no first draw to spare.
        var both = new TriggeredAbility(new Draws(new EachOpponent(), FirstEachTurn: true, ExceptFirstInDrawStep: true), new Draw(new Fixed(1)));
        var outside = new TriggeredAbility(new Draws(new EachOpponent(), DuringDrawStep: false, ExceptFirstInDrawStep: true), new Draw(new Fixed(1)));

        Assert.Contains(Wave22Errors(both), f => f.Code == "L033");
        Assert.Contains(Wave22Errors(outside), f => f.Code == "L033");
    }

    [Fact]
    public void A_mandatory_cost_can_carry_the_if_you_do_the_card_prints()
    {
        // CR 608.2c with 118.12: "sacrifice a creature. If you do, return the chosen cards to the
        // battlefield tapped." The player pays if they can, and the second half runs only then.
        var ability = new SpellAbility(new PayCost(
            new SacrificeCost(new ObjectFilter(Types: CardType.Creature)),
            Then: new Draw(new Fixed(2))));
        var back = Wave22RoundTrip(Wave22DefinitionWith(ability));

        var read = Wave22FirstEffect<PayCost>(back);
        Assert.IsType<SacrificeCost>(read.Cost);
        Assert.Equal(new Fixed(2), Assert.IsType<Draw>(read.Then).N);
        Assert.Null(read.Who);
        Assert.Empty(Wave22Errors(ability));
    }

    [Fact]
    public void The_optional_and_the_mandatory_cost_never_both_spell_one_sentence()
    {
        // L034, CR 118.12: "you may pay ... if you do" is one MayPay, so a May around a PayCost that
        // carries its own "if you do" is the shape MayPay replaced; and an "if you do" that does
        // nothing is the bare instruction with a clause no reader can act on.
        var wrapped = new SpellAbility(new May(new PayCost(new PayLife(new Fixed(2)), Then: new Draw(new Fixed(1)))));
        var empty = new SpellAbility(new PayCost(new PayLife(new Fixed(2)), Then: new DoNothing()));

        Assert.Contains(Wave22Errors(wrapped), f => f.Code == "L034");
        Assert.Contains(Wave22Errors(empty), f => f.Code == "L034");
    }

    [Fact]
    public void The_wave_twenty_two_records_leave_the_ir_version_where_it_is()
    {
        // One new ContinuousEffect and six optional fields: a file written before this wave still
        // reads, so there is nothing to migrate and IrVersion does not move.
        Assert.Equal(1, IrVersion.Current);
    }
}
