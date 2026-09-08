namespace MtgEngine.Cards.Tests;

/// <summary>
/// Wave 23 (seam-requests/V13.md): the seven gaps three independent translators each hit on a real
/// card of the third corpus slice. A trigger on a player searching a library (CR 701.19), a secret
/// simultaneous number choice with its highest and lowest read back (CR 700.2 with 101.4), the two
/// per-player turn counts a card asks for (CR 121.1), a copy of a printed card by name (CR 707.9), a
/// spell described by what it targets (CR 115.1), a per-spell cast-from-zone restriction (CR 601.3)
/// and the harnessed condition, which needed no record at all. Each has to round-trip, lint where
/// its halves can contradict, and leave the IR version where it is.
/// </summary>
public sealed partial class VocabularyAdditionsTests
{
    private static CardDefinition Wave23DefinitionWith(params Ability[] abilities) => new(
        OracleId: "00000000-0000-0000-0000-000000000f23",
        Name: "Wave Twenty Three Addition",
        Layout: Layout.Normal,
        Faces:
        [
            new Face(
                Name: "Wave Twenty Three Addition",
                Role: FaceRole.Main,
                Types: new TypeLine(CardType.Enchantment),
                ManaCost: "{2}{U}",
                Abilities: abilities,
                OracleText: "Wave twenty three text."),
        ]);

    private static CardDefinition Wave23RoundTrip(CardDefinition definition) =>
        CardFileSerializer.ReadDefinition(CardFileSerializer.WriteDefinition(definition));

    private static IReadOnlyList<LintFinding> Wave23Errors(params Ability[] abilities) =>
        Linter.Check(new CardFile(
            OracleId: Wave23DefinitionWith(abilities).OracleId,
            Name: "Wave Twenty Three Addition",
            OracleHash: OracleHashing.Of(["Wave twenty three text."]),
            IrVersion: IrVersion.Current,
            Definition: Wave23DefinitionWith(abilities),
            Status: ImplementationStatus.Translated,
            Source: new TemplateProvenance("test"))).Findings.Where(f => f.Severity == LintSeverity.Error).ToList();

    private static T Wave23FirstEffect<T>(CardDefinition definition)
        where T : Effect => Assert.IsType<T>(Assert.IsType<SpellAbility>(definition.Faces[0].Abilities![0]).Do);

    [Fact]
    public void A_trigger_can_watch_an_opponent_searching_their_library()
    {
        // CR 701.19: "whenever an opponent searches their library, ..." — one firing per search,
        // whatever that search finds, and the searcher is described rather than named in advance.
        var ability = new TriggeredAbility(new Searches(PlayerFilter.Opponents), new Draw(new Fixed(1)));
        var back = Wave23RoundTrip(Wave23DefinitionWith(ability));

        var read = Assert.IsType<Searches>(Assert.IsType<TriggeredAbility>(back.Faces[0].Abilities![0]).When);
        Assert.True(read.Who.Opponent);
        Assert.Null(read.Who.IsYou);
        Assert.Empty(Wave23Errors(ability));
    }

    [Fact]
    public void Each_player_can_secretly_choose_a_number_and_the_highest_and_lowest_are_read_back()
    {
        // CR 700.2 with 101.4: "each player secretly chooses a number, then those numbers are
        // revealed. Each player who chose the highest number loses that much life, and each player
        // who chose the lowest gains that much life."
        var ability = new SpellAbility(new Sequence(
        [
            new ChooseNumberSecretly("bid", new EachPlayer()),
            new LoseLife(new HighestChosen("bid"), new PlayerWhoChose("bid")),
            new GainLife(new LowestChosen("bid"), new PlayerWhoChose("bid", Lowest: true)),
        ]));
        var back = Wave23RoundTrip(Wave23DefinitionWith(ability));

        var steps = Wave23FirstEffect<Sequence>(back).Steps;
        var choice = Assert.IsType<ChooseNumberSecretly>(steps[0]);
        Assert.Equal("bid", choice.Key);
        Assert.IsType<EachPlayer>(choice.Who);
        Assert.Null(choice.Min);
        Assert.Equal("bid", Assert.IsType<HighestChosen>(Assert.IsType<LoseLife>(steps[1]).N).Key);
        Assert.False(Assert.IsType<PlayerWhoChose>(Assert.IsType<LoseLife>(steps[1]).Who).Lowest);
        Assert.True(Assert.IsType<PlayerWhoChose>(Assert.IsType<GainLife>(steps[2]).Who).Lowest);
        Assert.Empty(Wave23Errors(ability));
    }

    [Fact]
    public void An_extreme_read_from_no_choice_and_a_choice_nothing_reads_back_are_refused()
    {
        // L035, CR 700.2 with 101.4: the step that asks for the numbers and the amounts that read
        // them back are the two halves of one printed line, and either half alone says nothing.
        var unasked = new SpellAbility(new LoseLife(new HighestChosen("bid"), new EachPlayer()));
        var unread = new SpellAbility(new Sequence([new ChooseNumberSecretly("bid", new EachPlayer()), new Draw(new Fixed(1))]));

        Assert.Contains(Wave23Errors(unasked), f => f.Code == "L035");
        Assert.Contains(Wave23Errors(unread), f => f.Code == "L035");
    }

    [Fact]
    public void A_turn_counts_the_cards_a_player_drew_and_the_lands_that_entered_under_their_control()
    {
        // CR 121.1: "draw a card for each card you have drawn this turn", "... for each land that
        // entered the battlefield under your control this turn". Both are the turn's own counts.
        var ability = new SpellAbility(new Sequence(
        [
            new Draw(new CardsDrawnThisTurn(new You())),
            new GainLife(new LandsEnteredThisTurn(new TargetPlayer(PlayerFilter.Any))),
        ]));
        var back = Wave23RoundTrip(Wave23DefinitionWith(ability));

        var steps = Wave23FirstEffect<Sequence>(back).Steps;
        Assert.IsType<You>(Assert.IsType<CardsDrawnThisTurn>(Assert.IsType<Draw>(steps[0]).N).Who);
        Assert.IsType<TargetPlayer>(Assert.IsType<LandsEnteredThisTurn>(Assert.IsType<GainLife>(steps[1]).N).Who);
        Assert.Empty(Wave23Errors(ability));
    }

    [Fact]
    public void An_enchanted_permanent_can_be_a_copy_of_a_printed_card_named_by_the_aura()
    {
        // CR 707.9 with 613.1a: "enchanted permanent is [a printed card]". The copied card is not in
        // the game, so the name is the only way to say which card, and it is data here.
        var ability = new StaticAbility(new BecomesCopyOfNamed("Snow-Covered Island"), To: new Attached());
        var back = Wave23RoundTrip(Wave23DefinitionWith(ability));

        var read = Assert.IsType<BecomesCopyOfNamed>(Assert.IsType<StaticAbility>(back.Faces[0].Abilities![0]).Effect);
        Assert.Equal("Snow-Covered Island", read.Name);
        Assert.Null(read.Except);
        Assert.Empty(Wave23Errors(ability));
    }

    [Fact]
    public void A_copy_of_a_card_with_no_name_is_refused()
    {
        // L036, CR 707.9: the copiable values come from the card the name names, so an empty name
        // copies nothing. That the name is a real card is the pipeline's check, against the corpus.
        var nameless = new StaticAbility(new BecomesCopyOfNamed("   "), To: new Attached());

        Assert.Contains(Wave23Errors(nameless), f => f.Code == "L036");
    }

    [Fact]
    public void A_spell_can_be_described_by_what_it_targets()
    {
        // CR 115.1: "counter target spell that targets a permanent you control." The candidate is
        // the spell doing the targeting, read from the targets it announced (CR 601.2c).
        var targeting = new ObjectFilter(
            In: Zone.Stack,
            Spell: true,
            Also: new Targets(new ObjectFilter(Permanent: true, ControlledBy: new You())));
        var ability = new SpellAbility(new Counter(new Target(targeting)));
        var back = Wave23RoundTrip(Wave23DefinitionWith(ability));

        var read = Assert.IsType<Target>(Wave23FirstEffect<Counter>(back).What);
        var predicate = Assert.IsType<Targets>(read.Filter.Also);
        Assert.True(predicate.Of.Permanent);
        Assert.IsType<You>(predicate.Of.ControlledBy);
        Assert.Empty(Wave23Errors(ability));
    }

    [Fact]
    public void A_permanent_described_by_what_it_targets_is_refused()
    {
        // L037, CR 115.1 with 601.2c: only a spell or ability on the stack has chosen targets, so a
        // filter that says this of a permanent describes no object at all.
        var permanent = new ObjectFilter(In: Zone.Battlefield, Permanent: true, Also: new Targets(ObjectFilter.Creature));

        Assert.Contains(Wave23Errors(new SpellAbility(new Destroy(new Target(permanent)))), f => f.Code == "L037");
    }

    [Fact]
    public void A_spell_filter_can_name_the_zone_a_spell_may_not_be_cast_from()
    {
        // CR 601.3: "players can't cast spells from anywhere other than their hands." The
        // restriction describes the spell, so the zone rides on the filter of spells it refuses.
        var ability = new StaticAbility(
            new CantCastSpells(new EachPlayer(), new ObjectFilter(Spell: true, NotCastFrom: Zone.Hand)),
            Players: new EachPlayer());
        var back = Wave23RoundTrip(Wave23DefinitionWith(ability));

        var read = Assert.IsType<CantCastSpells>(Assert.IsType<StaticAbility>(back.Faces[0].Abilities![0]).Effect);
        Assert.Equal(Zone.Hand, read.Matching!.NotCastFrom);
        Assert.Null(read.Matching.CastFrom);
        Assert.Empty(Wave23Errors(ability));
    }

    [Fact]
    public void The_zone_a_spell_was_cast_from_is_named_once_and_only_of_a_spell()
    {
        // L038, CR 601.3: a card names the zone it allows or the one it forbids — both together
        // admits no spell — and a permanent was never cast from anywhere the game still reads.
        var both = new StaticAbility(new CantCastSpells(new EachPlayer(), new ObjectFilter(Spell: true, CastFrom: Zone.Graveyard, NotCastFrom: Zone.Hand)));
        var permanent = new StaticAbility(new CantCastSpells(new EachPlayer(), new ObjectFilter(Permanent: true, NotCastFrom: Zone.Hand)));

        Assert.Contains(Wave23Errors(both), f => f.Code == "L038");
        Assert.Contains(Wave23Errors(permanent), f => f.Code == "L038");
    }

    [Fact]
    public void The_harnessed_condition_is_a_Matches_over_this_object_and_needs_no_record()
    {
        // CR 701.64: "as long as this permanent is harnessed" is the While of the static ability, a
        // Matches over Self whose filter carries the IsHarnessed predicate. There is no
        // IsHarnessedCondition, and one would be a second spelling of the same sentence.
        var ability = new StaticAbility(
            new ModifyPt(new Fixed(2), new Fixed(2)),
            To: new Self(),
            While: new Matches(new Self(), new ObjectFilter(Also: new IsHarnessed())));
        var back = Wave23RoundTrip(Wave23DefinitionWith(ability));

        var read = Assert.IsType<Matches>(Assert.IsType<StaticAbility>(back.Faces[0].Abilities![0]).While);
        Assert.IsType<Self>(read.Of);
        Assert.IsType<IsHarnessed>(read.Filter.Also);
        Assert.Null(typeof(Condition).Assembly.GetType("MtgEngine.Cards.IsHarnessedCondition"));
        Assert.Empty(Wave23Errors(ability));
    }

    [Fact]
    public void The_wave_twenty_three_records_leave_the_ir_version_where_it_is()
    {
        // Six new records and two optional fields appended to ObjectFilter: every file written
        // before this wave still reads, so there is nothing to migrate.
        Assert.Equal(1, IrVersion.Current);
    }
}
