namespace MtgEngine.Cards.Tests;

/// <summary>
/// Wave 13 (seam-requests/V8.md): the records the subscription re-translation of the disputed pilot
/// set could not find a spelling for. Lands another player controls surveyed for mana (CR 106.1),
/// the colour a land was actually tapped for, the colours of a remembered card (CR 105.2), a cost
/// that remembers what paid it, an honest marker in a static ability's slot (CR 604.1), "that
/// creature" said inside a filter (CR 603.10), a printed mana cost (CR 202.1), an activation
/// restricted to a chosen colour (CR 106.6b), the card a hideaway set aside (CR 702.75a), and one
/// search with two destinations (CR 701.23a). Each has to be in the name table where a base is
/// involved, lint where its value can be nonsense, and survive a round trip, or a card carrying it
/// is lost with no sign of it.
/// </summary>
public sealed partial class VocabularyAdditionsTests
{
    private static CardDefinition Wave13DefinitionWith(params Ability[] abilities) => new(
        OracleId: "00000000-0000-0000-0000-0000000000d3",
        Name: "Wave Thirteen Addition",
        Layout: Layout.Normal,
        Faces:
        [
            new Face(
                Name: "Wave Thirteen Addition",
                Role: FaceRole.Main,
                Types: new TypeLine(CardType.Artifact),
                ManaCost: "{2}",
                Abilities: abilities,
                OracleText: "Wave thirteen text."),
        ]);

    private static CardFile Wave13FileWith(params Ability[] abilities)
    {
        var definition = Wave13DefinitionWith(abilities);
        return new CardFile(
            OracleId: definition.OracleId,
            Name: definition.Name,
            OracleHash: OracleHashing.Of(definition.Faces.Select(f => f.OracleText)),
            IrVersion: IrVersion.Current,
            Definition: definition,
            Status: ImplementationStatus.Translated,
            Source: new TemplateProvenance("test"));
    }

    private static CardDefinition Wave13RoundTrip(CardDefinition definition) =>
        CardFileSerializer.ReadDefinition(CardFileSerializer.WriteDefinition(definition));

    private static LintReport Wave13Lint(params Ability[] abilities) => Linter.Check(Wave13FileWith(abilities));

    /// <summary>An activated mana ability whose production is the one given. CR 605.1a.</summary>
    private static ActivatedAbility Wave13Adds(ManaProduction mana) =>
        new([new TapSelf()], new AddMana(mana));

    [Fact]
    public void A_mana_production_surveys_the_lands_its_filter_names()
    {
        var mana = new ManaProduction(
            LandCouldProduce: true,
            LandCouldProduceOf: new ObjectFilter(Types: CardType.Land, ControlledBy: new EachOpponent()));
        var back = Wave13RoundTrip(Wave13DefinitionWith(Wave13Adds(mana)));

        var produced = Assert.IsType<AddMana>(Assert.IsType<ActivatedAbility>(back.Faces[0].Abilities![0]).Do).Mana;
        Assert.True(produced.LandCouldProduce);
        Assert.Equal(CardType.Land, produced.LandCouldProduceOf?.Types);
        Assert.IsType<EachOpponent>(produced.LandCouldProduceOf?.ControlledBy);
        Assert.Empty(Wave13Lint(Wave13Adds(mana)).Findings);
    }

    [Fact]
    public void A_survey_filter_with_nothing_surveying_it_is_an_error()
    {
        var mana = new ManaProduction(LandCouldProduceOf: new ObjectFilter(Types: CardType.Land));
        var report = Wave13Lint(Wave13Adds(mana));

        Assert.Contains(report.Findings, f => f.Code == "L020" && f.Severity == LintSeverity.Error);
    }

    [Fact]
    public void The_colour_a_land_was_tapped_for_is_the_engines_own_key()
    {
        // CR 106.12a: the firing stores the colour; the mana instruction reads it by that key alone,
        // so no step on the card produces it and the file is still lint clean (L003).
        var ability = new TriggeredAbility(
            new TappedForMana(new ObjectFilter(Types: CardType.Land, ControlledBy: new You())),
            new AddMana(new ManaProduction(ChosenColorKey: "tapped-for-mana-color")));
        var back = Wave13RoundTrip(Wave13DefinitionWith(ability));

        var read = Assert.IsType<AddMana>(Assert.IsType<TriggeredAbility>(back.Faces[0].Abilities![0]).Do);
        Assert.Equal("tapped-for-mana-color", read.Mana.ChosenColorKey);
        Assert.Empty(Wave13Lint(ability).Findings);
    }

    [Fact]
    public void A_mana_production_takes_the_colours_of_a_remembered_object()
    {
        var ability = new SpellAbility(new Sequence(
        [
            new Exile(new Top(new You()), RememberAs: "imprinted"),
            new AddMana(new ManaProduction(ColorsOfRemembered: "imprinted")),
        ]));
        var back = Wave13RoundTrip(Wave13DefinitionWith(ability));

        var steps = Assert.IsType<Sequence>(Assert.IsType<SpellAbility>(back.Faces[0].Abilities![0]).Do).Steps;
        Assert.Equal("imprinted", Assert.IsType<AddMana>(steps[1]).Mana.ColorsOfRemembered);
        Assert.Empty(Wave13Lint(ability).Findings);
    }

    [Fact]
    public void Remembered_colours_beside_another_colour_choice_are_an_error()
    {
        var report = Wave13Lint(Wave13Adds(new ManaProduction(AnyColor: true, ColorsOfRemembered: "imprinted")));

        Assert.Contains(report.Findings, f => f.Code == "L020" && f.Severity == LintSeverity.Error);
    }

    [Fact]
    public void The_three_costs_that_can_be_referred_back_to_remember_what_paid_them()
    {
        var ability = new ActivatedAbility(
            [
                new SacrificeCost(ObjectFilter.Creature, RememberAs: "sacrificed"),
                new DiscardCost(new Fixed(1), RememberAs: "discarded"),
                new ExileFromZoneCost(Zone.Graveyard, RememberAs: "exiled"),
            ],
            new Sequence(
            [
                new Draw(new Fixed(1)),
                new PutCounters(new Remembered("sacrificed"), "+1/+1", new Fixed(1)),
            ]));
        var back = Wave13RoundTrip(Wave13DefinitionWith(ability));

        var costs = Assert.IsType<ActivatedAbility>(back.Faces[0].Abilities![0]).Costs;
        Assert.Equal("sacrificed", Assert.IsType<SacrificeCost>(costs[0]).RememberAs);
        Assert.Equal("discarded", Assert.IsType<DiscardCost>(costs[1]).RememberAs);
        Assert.Equal("exiled", Assert.IsType<ExileFromZoneCost>(costs[2]).RememberAs);

        // The key a cost produced satisfies L003 for the effect that names it.
        Assert.DoesNotContain(Wave13Lint(ability).Findings, f => f.Code == "L003");
    }

    [Fact]
    public void An_unsupported_continuous_effect_is_registered_and_lints_as_L001()
    {
        var registered = CardFileSerializer.RegisteredNames(typeof(ContinuousEffect));
        Assert.True(registered.ContainsKey(typeof(UnsupportedContinuous)), "UnsupportedContinuous has no [JsonDerivedType] on ContinuousEffect.");
        Assert.Equal(nameof(UnsupportedContinuous), registered[typeof(UnsupportedContinuous)]);

        var ability = new StaticAbility(
            new UnsupportedContinuous("no spelling for the printed clause", "A printed line."),
            To: new Each(ObjectFilter.Creature));
        var back = Wave13RoundTrip(Wave13DefinitionWith(ability));

        var marker = Assert.IsType<UnsupportedContinuous>(Assert.IsType<StaticAbility>(back.Faces[0].Abilities![0]).Effect);
        Assert.Equal("no spelling for the printed clause", marker.Reason);
        Assert.Equal("A printed line.", marker.Text);
        Assert.Contains(Wave13Lint(ability).Findings, f => f.Code == "L001" && f.Severity == LintSeverity.Error);
    }

    [Fact]
    public void A_filter_can_require_the_remembered_object()
    {
        var registered = CardFileSerializer.RegisteredNames(typeof(Predicate));
        Assert.True(registered.ContainsKey(typeof(IsRemembered)), "IsRemembered has no [JsonDerivedType] on Predicate.");

        var filter = new ObjectFilter(Types: CardType.Creature, Also: new IsRemembered("chosen"));
        var back = Wave13RoundTrip(Wave13DefinitionWith(new StaticAbility(new CantAttack(), To: new Each(filter))));

        var read = Assert.IsType<StaticAbility>(back.Faces[0].Abilities![0]);
        Assert.Equal("chosen", Assert.IsType<IsRemembered>(Assert.IsType<Each>(read.To).Filter.Also).Key);
    }

    [Fact]
    public void A_remembered_predicate_reads_a_key_rather_than_producing_one()
    {
        // CR 603.10: it names what an earlier step set aside, so an unproduced key is L003 — the
        // same rule the Remembered selector answers to.
        var report = Wave13Lint(new StaticAbility(
            new CantAttack(),
            To: new Each(new ObjectFilter(Also: new IsRemembered("nothing-makes-this")))));

        Assert.Contains(report.Findings, f => f.Code == "L003" && f.Severity == LintSeverity.Error);
    }

    [Fact]
    public void A_filter_can_name_the_printed_mana_cost()
    {
        var filter = new ObjectFilter(Types: CardType.Artifact, ManaCostIs: ["{0}", "{1}"]);
        var ability = new StaticAbility(new CantAttack(), To: new Each(filter));
        var back = Wave13RoundTrip(Wave13DefinitionWith(ability));

        var read = Assert.IsType<Each>(Assert.IsType<StaticAbility>(back.Faces[0].Abilities![0]).To);
        Assert.Equal(["{0}", "{1}"], read.Filter.ManaCostIs);
        Assert.Empty(Wave13Lint(ability).Findings);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("")]
    public void A_printed_cost_that_is_not_a_symbol_string_is_an_error(string entry)
    {
        var report = Wave13Lint(new StaticAbility(
            new CantAttack(),
            To: new Each(new ObjectFilter(ManaCostIs: [entry]))));

        Assert.Contains(report.Findings, f => f.Code == "L018" && f.Severity == LintSeverity.Error);
    }

    [Fact]
    public void An_empty_printed_cost_list_matches_nothing_and_is_an_error()
    {
        var report = Wave13Lint(new StaticAbility(new CantAttack(), To: new Each(new ObjectFilter(ManaCostIs: []))));

        Assert.Contains(report.Findings, f => f.Code == "L018" && f.Severity == LintSeverity.Error);
    }

    [Fact]
    public void A_filter_can_name_the_colours_outside_the_commanders_identity()
    {
        var filter = new ObjectFilter(NotInCommanderIdentityColors: true);
        var ability = new StaticAbility(new CantAttack(), To: new Each(filter));
        var back = Wave13RoundTrip(Wave13DefinitionWith(ability));

        var read = Assert.IsType<Each>(Assert.IsType<StaticAbility>(back.Faces[0].Abilities![0]).To);
        Assert.True(read.Filter.NotInCommanderIdentityColors);
    }

    [Fact]
    public void An_activation_can_be_restricted_to_the_chosen_colour()
    {
        var ability = new ActivatedAbility(
            [new ManaCost("{1}")],
            new Draw(new Fixed(1)),
            PayOnlyWithChosenColorKey: "chosen-color");
        var back = Wave13RoundTrip(Wave13DefinitionWith(ability));

        Assert.Equal("chosen-color", Assert.IsType<ActivatedAbility>(back.Faces[0].Abilities![0]).PayOnlyWithChosenColorKey);
        Assert.DoesNotContain(Wave13Lint(ability).Findings, f => f.Code == "L016");
    }

    [Fact]
    public void Two_of_the_three_mana_spend_riders_together_are_an_error()
    {
        var report = Wave13Lint(new ActivatedAbility(
            [new ManaCost("{1}")],
            new Draw(new Fixed(1)),
            PayOnlyWith: Color.Red,
            PayOnlyWithChosenColorKey: "chosen-color"));

        Assert.Contains(report.Findings, f => f.Code == "L016" && f.Severity == LintSeverity.Error);
    }

    [Fact]
    public void A_keyword_can_remember_what_it_set_aside()
    {
        // CR 702.75a: hideaway exiles a card the permanent's other ability then names.
        var hideaway = new KeywordAbility(KeywordKind.Hideaway, N: new Fixed(4), RememberAs: "hidden");
        var play = new ActivatedAbility([new TapSelf()], new MayCast(new Remembered("hidden"), WithoutPayingManaCost: true));
        var back = Wave13RoundTrip(Wave13DefinitionWith(hideaway, play));

        Assert.Equal("hidden", Assert.IsType<KeywordAbility>(back.Faces[0].Abilities![0]).RememberAs);

        // The keyword's key is produced, so the ability that plays the card is not an L003.
        Assert.DoesNotContain(Wave13Lint(hideaway, play).Findings, f => f.Code == "L003");
    }

    [Fact]
    public void One_search_can_have_two_destinations()
    {
        var search = new Search(
            new ObjectFilter(Types: CardType.Land, Supertypes: Supertype.Basic),
            Zone.Battlefield,
            UpTo: new Fixed(2),
            Tapped: true,
            Reveal: true,
            OthersTo: Zone.Hand);
        var back = Wave13RoundTrip(Wave13DefinitionWith(new SpellAbility(search)));

        var read = Assert.IsType<Search>(Assert.IsType<SpellAbility>(back.Faces[0].Abilities![0]).Do);
        Assert.Equal(Zone.Battlefield, read.To);
        Assert.True(read.Tapped);
        Assert.Equal(Zone.Hand, read.OthersTo);
        Assert.False(read.OthersTapped);
        Assert.Empty(Wave13Lint(new SpellAbility(search)).Findings);
    }

    [Fact]
    public void Tapping_the_other_cards_outside_the_battlefield_is_an_error()
    {
        var report = Wave13Lint(new SpellAbility(new Search(
            ObjectFilter.Creature,
            Zone.Battlefield,
            UpTo: new Fixed(2),
            OthersTo: Zone.Hand,
            OthersTapped: true)));

        Assert.Contains(report.Findings, f => f.Code == "L019" && f.Severity == LintSeverity.Error);
    }

    [Fact]
    public void A_second_destination_equal_to_the_first_is_a_warning()
    {
        var report = Wave13Lint(new SpellAbility(new Search(
            ObjectFilter.Creature,
            Zone.Hand,
            UpTo: new Fixed(2),
            OthersTo: Zone.Hand)));

        Assert.Contains(report.Findings, f => f.Code == "L019" && f.Severity == LintSeverity.Warning);
    }

    [Fact]
    public void The_wave_thirteen_additions_did_not_move_the_IR_version()
    {
        // Every record this wave added is an optional field or a new derived type, so a file written
        // before it still reads. A bumped version would make every stored card need a migration.
        Assert.Equal(1, IrVersion.Current);
    }
}
