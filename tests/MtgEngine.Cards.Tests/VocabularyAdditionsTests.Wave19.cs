namespace MtgEngine.Cards.Tests;

/// <summary>
/// Wave 19 (seam-requests/V11.md): the nine gaps three independent translators each hit on a real
/// card of the first corpus slice. A card drawn this turn (CR 121.1), a zone change with an excluded
/// source and no destination (CR 603.6), a floor under a life total against damage (CR 120.4), a
/// printed alternative cost (CR 118.9), an opening-hand permission (CR 103.6), one firing per
/// declaration of attackers with a count to read (CR 508.1), the triggering damage as an amount and
/// a delayed trigger with a deadline (CR 603.10, 603.7c), the life lost this way (CR 119.4) and a
/// copy that keeps the ability that made it (CR 707.9b). Each has to round-trip, lint where its
/// halves can contradict, and leave the IR version where it is.
/// </summary>
public sealed partial class VocabularyAdditionsTests
{
    private static CardDefinition Wave19DefinitionWith(params Ability[] abilities) =>
        Wave19DefinitionOf(Wave19Face(abilities));

    private static Face Wave19Face(Ability[] abilities, IReadOnlyList<AlternativeCost>? alternativeCosts = null) => new(
        Name: "Wave Nineteen Addition",
        Role: FaceRole.Main,
        Types: new TypeLine(CardType.Instant),
        ManaCost: "{2}{B}",
        Abilities: abilities,
        OracleText: "Wave nineteen text.",
        AlternativeCosts: alternativeCosts);

    private static CardDefinition Wave19DefinitionOf(Face face) => new(
        OracleId: "00000000-0000-0000-0000-0000000000f9",
        Name: "Wave Nineteen Addition",
        Layout: Layout.Normal,
        Faces: [face]);

    private static CardFile Wave19FileOf(CardDefinition definition) => new(
        OracleId: definition.OracleId,
        Name: definition.Name,
        OracleHash: OracleHashing.Of(definition.Faces.Select(f => f.OracleText)),
        IrVersion: IrVersion.Current,
        Definition: definition,
        Status: ImplementationStatus.Translated,
        Source: new TemplateProvenance("test"));

    private static CardDefinition Wave19RoundTrip(CardDefinition definition) =>
        CardFileSerializer.ReadDefinition(CardFileSerializer.WriteDefinition(definition));

    private static LintReport Wave19Lint(params Ability[] abilities) => Linter.Check(Wave19FileOf(Wave19DefinitionWith(abilities)));

    private static T Wave19FirstEffect<T>(CardDefinition definition)
        where T : Effect => Assert.IsType<T>(Assert.IsType<SpellAbility>(definition.Faces[0].Abilities![0]).Do);

    [Fact]
    public void A_hand_filter_can_name_the_cards_drawn_this_turn()
    {
        // CR 121.1: "choose two cards in your hand that you drew this turn".
        var choose = new Choose(
            new ObjectFilter(In: Zone.Hand, OwnedBy: new You(), Also: new DrawnThisTurn()),
            new You(),
            new Fixed(2));
        var ability = new SpellAbility(new Move(choose, Zone.Library));
        var back = Wave19RoundTrip(Wave19DefinitionWith(ability));

        var read = Assert.IsType<Choose>(Wave19FirstEffect<Move>(back).What);
        Assert.IsType<DrawnThisTurn>(read.Filter.Also);
        Assert.Equal(Zone.Hand, read.Filter.In);
        Assert.Empty(Wave19Lint(ability).Findings);
    }

    [Fact]
    public void A_zone_change_can_exclude_the_zone_it_does_not_watch()
    {
        // CR 603.6: "whenever a card is put into a graveyard from anywhere other than the battlefield".
        var trigger = new ZoneChange(new ObjectFilter(), To: Zone.Graveyard, NotFrom: Zone.Battlefield);
        var ability = new TriggeredAbility(trigger, new LoseLife(new Fixed(1), new EachOpponent()));
        var back = Wave19RoundTrip(Wave19DefinitionWith(ability));

        var read = Assert.IsType<ZoneChange>(Assert.IsType<TriggeredAbility>(back.Faces[0].Abilities![0]).When);
        Assert.Equal(Zone.Graveyard, read.To);
        Assert.Equal(Zone.Battlefield, read.NotFrom);
        Assert.Null(read.From);
        Assert.Empty(Wave19Lint(ability).Findings);
    }

    [Fact]
    public void A_zone_change_out_of_a_zone_needs_no_destination()
    {
        // CR 603.6: "whenever a card leaves your graveyard" — out of one zone, to anywhere.
        var trigger = new ZoneChange(new ObjectFilter(OwnedBy: new You()), From: Zone.Graveyard);
        var ability = new TriggeredAbility(trigger, new Draw(new Fixed(1)));
        var back = Wave19RoundTrip(Wave19DefinitionWith(ability));

        var read = Assert.IsType<ZoneChange>(Assert.IsType<TriggeredAbility>(back.Faces[0].Abilities![0]).When);
        Assert.Equal(Zone.Graveyard, read.From);
        Assert.Null(read.To);
        Assert.Empty(Wave19Lint(ability).Findings);
    }

    [Fact]
    public void A_zone_change_that_names_no_zone_and_one_that_names_two_are_refused()
    {
        // L025, CR 603.6: one printed phrase names the zone watched or the zone excluded, and a
        // trigger with no zone at all fires on every zone change in the game.
        var both = new TriggeredAbility(new ZoneChange(new ObjectFilter(), From: Zone.Hand, To: Zone.Graveyard, NotFrom: Zone.Battlefield), new Draw(new Fixed(1)));
        var neither = new TriggeredAbility(new ZoneChange(new ObjectFilter()), new Draw(new Fixed(1)));

        Assert.Contains(Wave19Lint(both).Findings, f => f.Code == "L025" && f.Severity == LintSeverity.Error);
        Assert.Contains(Wave19Lint(neither).Findings, f => f.Code == "L025" && f.Severity == LintSeverity.Error);
    }

    [Fact]
    public void Damage_can_be_floored_at_a_life_total()
    {
        // CR 120.4 with 119.3: "damage that would reduce your life total to less than 1 reduces it to 1 instead".
        var ability = new SpellAbility(new ApplyToPlayer(new DamageDoesNotReduceLifeBelow(new Fixed(1)), new You(), new UntilEndOfTurn()));
        var back = Wave19RoundTrip(Wave19DefinitionWith(ability));

        var applied = Wave19FirstEffect<ApplyToPlayer>(back);
        var floor = Assert.IsType<DamageDoesNotReduceLifeBelow>(applied.What);
        Assert.Equal(new Fixed(1), floor.Floor);
        Assert.IsType<UntilEndOfTurn>(applied.Until);
        Assert.Empty(Wave19Lint(ability).Findings);
    }

    [Fact]
    public void A_face_can_print_an_alternative_cost_gated_on_a_condition()
    {
        // CR 118.9: "you may pay {B} rather than pay this spell's mana cost if a creature died this turn".
        var alternative = new AlternativeCost(new ManaCost("{B}"), new Compare(new CardsInZoneOf(new You(), Zone.Graveyard), ComparisonOp.Ge, new Fixed(1)));
        var definition = Wave19DefinitionOf(Wave19Face([new SpellAbility(new Draw(new Fixed(1)))], [alternative]));
        var back = Wave19RoundTrip(definition);

        var read = Assert.Single(back.Faces[0].AlternativeCosts!);
        Assert.Equal("{B}", Assert.IsType<ManaCost>(read.Cost).Mana);
        Assert.IsType<Compare>(read.OnlyIf);
        Assert.Empty(Linter.Check(Wave19FileOf(definition)).Findings);
    }

    [Fact]
    public void An_alternative_cost_that_asks_for_nothing_is_refused()
    {
        // L026, CR 118.9: the alternative replaces the mana cost, so one that asks for nothing makes
        // the spell free — a cost the reading dropped.
        var definition = Wave19DefinitionOf(Wave19Face([new SpellAbility(new Draw(new Fixed(1)))], [new AlternativeCost(new Costs([]))]));
        var empty = Wave19DefinitionOf(Wave19Face([new SpellAbility(new Draw(new Fixed(1)))], []));

        Assert.Contains(Linter.Check(Wave19FileOf(definition)).Findings, f => f.Code == "L026" && f.Severity == LintSeverity.Error);
        Assert.Contains(Linter.Check(Wave19FileOf(empty)).Findings, f => f.Code == "L026" && f.Severity == LintSeverity.Error);
    }

    [Fact]
    public void An_opening_hand_ability_may_begin_the_game_on_the_battlefield()
    {
        // CR 103.6: "if this card is in your opening hand ... you may begin the game with it on the
        // battlefield with a luck counter on it. If you do, exile a card from your hand."
        var effect = new MayBeginGameOnBattlefield(
            new NotCondition(new IsYourTurn()),
            [new CounterSpec("luck", new Fixed(1))],
            new Exile(new Choose(new ObjectFilter(In: Zone.Hand, OwnedBy: new You()), new You())));
        var ability = new StaticAbility(effect, Where: Zone.Hand);
        var back = Wave19RoundTrip(Wave19DefinitionWith(ability));

        var read = Assert.IsType<MayBeginGameOnBattlefield>(Assert.IsType<StaticAbility>(back.Faces[0].Abilities![0]).Effect);
        Assert.Equal("luck", Assert.Single(read.WithCounters!).Counter);
        Assert.IsType<Exile>(read.IfYouDo);
        Assert.NotNull(read.OnlyIf);
        Assert.Empty(Wave19Lint(ability).Findings);
    }

    [Fact]
    public void An_opening_hand_ability_outside_the_hand_is_refused()
    {
        // L027, CR 103.6: the ability functions from the opening hand, so a battlefield-scoped
        // static ability would never be read.
        var battlefield = new StaticAbility(new MayBeginGameOnBattlefield(), Where: Zone.Battlefield);

        Assert.Contains(Wave19Lint(battlefield).Findings, f => f.Code == "L027" && f.Severity == LintSeverity.Error);
    }

    [Fact]
    public void An_attack_fires_once_per_declaration_and_its_attackers_are_countable()
    {
        // CR 508.1: "whenever an opponent attacks with creatures, if two or more of those creatures
        // are attacking you and/or planeswalkers you control, draw a card".
        var ability = new TriggeredAbility(
            new AttacksWithCreatures(new EachOpponent()),
            new Draw(new Fixed(1)),
            If: new Compare(new AttackersAgainst(new You()), ComparisonOp.Ge, new Fixed(2)));
        var back = Wave19RoundTrip(Wave19DefinitionWith(ability));

        var read = Assert.IsType<TriggeredAbility>(back.Faces[0].Abilities![0]);
        Assert.IsType<EachOpponent>(Assert.IsType<AttacksWithCreatures>(read.When).By);
        var compare = Assert.IsType<Compare>(read.If);
        var attackers = Assert.IsType<AttackersAgainst>(compare.Left);
        Assert.IsType<You>(attackers.Who);
        Assert.True(attackers.IncludingPermanentsTheyControl);
        Assert.Empty(Wave19Lint(ability).Findings);
    }

    [Fact]
    public void A_delayed_damage_trigger_lasts_a_turn_and_reads_its_own_damage()
    {
        // CR 603.7c with 603.10: "until end of turn, whenever a creature you control deals damage to
        // a player, draw that many cards".
        var delayed = new DelayedTrigger(
            new DealsDamage(new ObjectFilter(Types: CardType.Creature, ControlledBy: new You()), ToPlayer: new PlayerFilter()),
            new Draw(new TriggeringDamage()),
            Once: false,
            Until: new UntilEndOfTurn());
        var ability = new SpellAbility(delayed);
        var back = Wave19RoundTrip(Wave19DefinitionWith(ability));

        var read = Wave19FirstEffect<DelayedTrigger>(back);
        Assert.False(read.Once);
        Assert.IsType<UntilEndOfTurn>(read.Until);
        Assert.IsType<TriggeringDamage>(Assert.IsType<Draw>(read.Do).N);
        Assert.Empty(Wave19Lint(ability).Findings);
    }

    [Fact]
    public void The_triggering_damage_needs_a_damage_event_to_read()
    {
        // L028, CR 603.10: the amount is the triggering event's information, so an ability that does
        // not trigger on damage has nothing to read.
        var noDamage = new TriggeredAbility(new AtStep(Step.Upkeep, new You()), new Draw(new TriggeringDamage()));
        var onDamage = new TriggeredAbility(new PlayerIsDealtDamage(new You()), new Draw(new TriggeringDamage()));

        Assert.Contains(Wave19Lint(noDamage).Findings, f => f.Code == "L028" && f.Severity == LintSeverity.Error);
        Assert.Empty(Wave19Lint(onDamage).Findings);
    }

    [Fact]
    public void Life_lost_this_way_is_stored_by_the_step_that_took_it()
    {
        // CR 119.4: "each opponent loses X life ... you gain life equal to the life lost this way".
        var ability = new SpellAbility(new Sequence(
        [
            new LoseLife(new Devotion(new You(), Color.Black), new EachOpponent(), StoreAs: "drained"),
            new GainLife(new Stored("drained")),
        ]));
        var back = Wave19RoundTrip(Wave19DefinitionWith(ability));

        var steps = Wave19FirstEffect<Sequence>(back).Steps;
        Assert.Equal("drained", Assert.IsType<LoseLife>(steps[0]).StoreAs);
        Assert.Equal("drained", Assert.IsType<Stored>(Assert.IsType<GainLife>(steps[1]).N).Key);
        Assert.Empty(Wave19Lint(ability).Findings);
    }

    [Fact]
    public void A_copy_can_keep_the_ability_that_made_it()
    {
        // CR 707.9b: "target land becomes a copy of this land, except it has this ability".
        var effect = new BecomesCopyOf(new Self(), ExceptThisAbility: true);
        var ability = new ActivatedAbility(
            [new ManaCost("{2}"), new TapSelf()],
            new Apply(effect, new Target(new ObjectFilter(Types: CardType.Land)), new Permanently()));
        var back = Wave19RoundTrip(Wave19DefinitionWith(ability));

        var read = Assert.IsType<BecomesCopyOf>(Assert.IsType<Apply>(Assert.IsType<ActivatedAbility>(back.Faces[0].Abilities![0]).Do).What);
        Assert.True(read.ExceptThisAbility);
        Assert.Null(read.Except);
        Assert.Empty(Wave19Lint(ability).Findings);
    }

    [Fact]
    public void A_copy_exception_on_a_spell_ability_has_no_ability_to_keep()
    {
        // L029, CR 707.9b with 109.5: "this ability" names the activated or triggered ability the
        // effect is part of; a spell ability leaves the stack with the card.
        var spell = new SpellAbility(new Apply(new BecomesCopyOf(new Self(), ExceptThisAbility: true), new Target(new ObjectFilter(Types: CardType.Land)), new Permanently()));

        Assert.Contains(Wave19Lint(spell).Findings, f => f.Code == "L029" && f.Severity == LintSeverity.Error);
    }

    [Fact]
    public void The_wave_nineteen_records_leave_the_ir_version_where_it_is()
    {
        // Every addition is an optional field or a new record, so a file written before this wave
        // still reads: nothing to migrate, and IrVersion does not move.
        Assert.Equal(1, IrVersion.Current);
    }
}
