namespace MtgEngine.Cards.Tests;

/// <summary>
/// Wave 17 (seam-requests/V10.md): the two shapes the third reading still could not say. A search
/// whose destination depends on the card found (CR 701.23a) and a mode count that depends on a
/// condition read as the spell is cast (CR 601.2b, 700.2). Each has to round-trip, lint where its
/// rider can say nothing, and leave the IR version where it is.
/// </summary>
public sealed partial class VocabularyAdditionsTests
{
    private static CardDefinition Wave17DefinitionWith(params Ability[] abilities) => new(
        OracleId: "00000000-0000-0000-0000-0000000000f7",
        Name: "Wave Seventeen Addition",
        Layout: Layout.Normal,
        Faces:
        [
            new Face(
                Name: "Wave Seventeen Addition",
                Role: FaceRole.Main,
                Types: new TypeLine(CardType.Instant),
                ManaCost: "{G}{G}",
                Abilities: abilities,
                OracleText: "Wave seventeen text."),
        ]);

    private static CardFile Wave17FileWith(params Ability[] abilities)
    {
        var definition = Wave17DefinitionWith(abilities);
        return new CardFile(
            OracleId: definition.OracleId,
            Name: definition.Name,
            OracleHash: OracleHashing.Of(definition.Faces.Select(f => f.OracleText)),
            IrVersion: IrVersion.Current,
            Definition: definition,
            Status: ImplementationStatus.Translated,
            Source: new TemplateProvenance("test"));
    }

    private static CardDefinition Wave17RoundTrip(CardDefinition definition) =>
        CardFileSerializer.ReadDefinition(CardFileSerializer.WriteDefinition(definition));

    private static LintReport Wave17Lint(params Ability[] abilities) => Linter.Check(Wave17FileWith(abilities));

    /// <summary>"Search for a creature or land card, reveal it, put it into your hand. If it's a land card, put it onto the battlefield tapped instead."</summary>
    private static Search Wave17CreatureOrLandToHandOrLandTapped(params SearchDestination[] instead) => new(
        new ObjectFilter(AnyTypes: CardType.Creature | CardType.Land),
        Zone.Hand,
        Reveal: true,
        Instead: instead);

    private static SearchDestination Wave17LandOntoBattlefieldTapped => new(new ObjectFilter(Types: CardType.Land), Zone.Battlefield, Tapped: true);

    /// <summary>"Choose one. If you control a commander as you cast this spell, you may choose both instead."</summary>
    private static ChooseMode Wave17ChooseOneOrBothWithACommander(Condition? maxIf, int? maxIfMet) => new(
        [new Mode("Draw a card.", new Draw(new Fixed(1))), new Mode("You gain 3 life.", new GainLife(new Fixed(3)))],
        MaxIf: maxIf,
        MaxIfMet: maxIfMet);

    private static Condition Wave17ControlsACommander => new Controls(new You(), new ObjectFilter(IsCommander: true), new Comparison(ComparisonOp.Ge, new Fixed(1)));

    [Fact]
    public void A_search_can_send_the_card_found_where_its_type_says()
    {
        // CR 701.23a: one search of one card; the land goes to the battlefield tapped, anything else to hand.
        var ability = new SpellAbility(Wave17CreatureOrLandToHandOrLandTapped(Wave17LandOntoBattlefieldTapped));
        var back = Wave17RoundTrip(Wave17DefinitionWith(ability));

        var read = Assert.IsType<Search>(Assert.IsType<SpellAbility>(back.Faces[0].Abilities![0]).Do);
        Assert.Equal(Zone.Hand, read.To);
        Assert.False(read.Tapped);
        var entry = Assert.Single(read.Instead!);
        Assert.Equal(CardType.Land, entry.Matching.Types);
        Assert.Equal(Zone.Battlefield, entry.To);
        Assert.True(entry.Tapped);
        Assert.Empty(Wave17Lint(ability).Findings);
    }

    [Fact]
    public void A_search_without_instead_writes_no_instead()
    {
        var json = CardFileSerializer.WriteDefinition(Wave17DefinitionWith(new SpellAbility(new Search(ObjectFilter.Creature, Zone.Hand))));

        Assert.DoesNotContain("instead", json, StringComparison.Ordinal);
        Assert.Null(Assert.IsType<Search>(Assert.IsType<SpellAbility>(CardFileSerializer.ReadDefinition(json).Faces[0].Abilities![0]).Do).Instead);
    }

    [Fact]
    public void An_instead_entry_equal_to_the_plain_destination_is_an_error()
    {
        // Hand untapped is where the search already sends the card: the "instead" half was lost.
        var report = Wave17Lint(new SpellAbility(Wave17CreatureOrLandToHandOrLandTapped(new SearchDestination(new ObjectFilter(Types: CardType.Land), Zone.Hand))));

        Assert.Contains(report.Findings, f => f.Code == "L023" && f.Severity == LintSeverity.Error);
    }

    [Fact]
    public void An_instead_entry_tapping_a_card_outside_the_battlefield_is_an_error()
    {
        var report = Wave17Lint(new SpellAbility(Wave17CreatureOrLandToHandOrLandTapped(new SearchDestination(new ObjectFilter(Types: CardType.Land), Zone.Graveyard, Tapped: true))));

        Assert.Contains(report.Findings, f => f.Code == "L023" && f.Severity == LintSeverity.Error);
    }

    [Fact]
    public void An_empty_instead_list_is_an_error()
    {
        var report = Wave17Lint(new SpellAbility(Wave17CreatureOrLandToHandOrLandTapped()));

        Assert.Contains(report.Findings, f => f.Code == "L023" && f.Severity == LintSeverity.Error);
    }

    [Fact]
    public void A_search_with_no_instead_is_not_an_L023()
    {
        var report = Wave17Lint(new SpellAbility(new Search(ObjectFilter.Creature, Zone.Hand, Reveal: true)));

        Assert.DoesNotContain(report.Findings, f => f.Code == "L023");
    }

    [Fact]
    public void A_mode_count_can_depend_on_a_condition_read_as_the_spell_is_cast()
    {
        // CR 601.2b: the modes are announced as the spell is cast, and the count is read there.
        var ability = new SpellAbility(Wave17ChooseOneOrBothWithACommander(Wave17ControlsACommander, 2));
        var back = Wave17RoundTrip(Wave17DefinitionWith(ability));

        var read = Assert.IsType<ChooseMode>(Assert.IsType<SpellAbility>(back.Faces[0].Abilities![0]).Do);
        Assert.Equal(1, read.Max);
        Assert.Equal(2, read.MaxIfMet);
        var controls = Assert.IsType<Controls>(read.MaxIf);
        Assert.True(controls.What.IsCommander);
        Assert.Empty(Wave17Lint(ability).Findings);
    }

    [Fact]
    public void A_plain_choose_mode_writes_no_conditional_count()
    {
        var json = CardFileSerializer.WriteDefinition(Wave17DefinitionWith(new SpellAbility(Wave17ChooseOneOrBothWithACommander(null, null))));

        Assert.DoesNotContain("maxIf", json, StringComparison.Ordinal);
        Assert.DoesNotContain(Wave17Lint(new SpellAbility(Wave17ChooseOneOrBothWithACommander(null, null))).Findings, f => f.Code == "L024");
    }

    [Fact]
    public void A_condition_without_a_count_is_an_error()
    {
        var report = Wave17Lint(new SpellAbility(Wave17ChooseOneOrBothWithACommander(Wave17ControlsACommander, null)));

        Assert.Contains(report.Findings, f => f.Code == "L024" && f.Severity == LintSeverity.Error);
    }

    [Fact]
    public void A_count_without_a_condition_is_an_error()
    {
        var report = Wave17Lint(new SpellAbility(Wave17ChooseOneOrBothWithACommander(null, 2)));

        Assert.Contains(report.Findings, f => f.Code == "L024" && f.Severity == LintSeverity.Error);
    }

    [Fact]
    public void A_conditional_count_not_above_max_is_an_error()
    {
        // "You may choose both instead" raises the count; a MaxIfMet of one beside Max 1 says nothing.
        var report = Wave17Lint(new SpellAbility(Wave17ChooseOneOrBothWithACommander(Wave17ControlsACommander, 1)));

        Assert.Contains(report.Findings, f => f.Code == "L024" && f.Severity == LintSeverity.Error);
    }

    [Theory]
    [InlineData(typeof(SearchDestination), "701.23")]
    [InlineData(typeof(ChooseMode), "601.2b")]
    public void The_wave_seventeen_records_cite_the_rule_their_reading_comes_from(Type record, string rule)
    {
        Assert.Contains(rule, Wave15DocOf(record), StringComparison.Ordinal);
    }

    [Fact]
    public void The_wave_seventeen_additions_did_not_move_the_IR_version()
    {
        // One optional list on Search and two optional members on ChooseMode: every stored file still reads.
        Assert.Equal(1, IrVersion.Current);
    }
}
