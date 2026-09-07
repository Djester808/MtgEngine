using System.Xml.Linq;

namespace MtgEngine.Cards.Tests;

/// <summary>
/// Wave 15 (seam-requests/V9.md): the records the re-translation still could not say. A play
/// permission that waives the mana cost and can play a land (CR 702.75a, 118.9, 305.1), a zone
/// permission whose casts pay life equal to mana value in place of the mana cost (CR 118.9, 601.2f),
/// a choice among the objects an earlier step remembered (CR 700.2), a selector said inside a
/// filter (CR 301.5c), and the binding TotalAmong and GreatestAmong give their inner amount. Each
/// has to round-trip, lint where its flags can contradict, and leave the IR version where it is.
/// </summary>
public sealed partial class VocabularyAdditionsTests
{
    private static CardDefinition Wave15DefinitionWith(params Ability[] abilities) => new(
        OracleId: "00000000-0000-0000-0000-0000000000f5",
        Name: "Wave Fifteen Addition",
        Layout: Layout.Normal,
        Faces:
        [
            new Face(
                Name: "Wave Fifteen Addition",
                Role: FaceRole.Main,
                Types: new TypeLine(CardType.Artifact),
                ManaCost: "{2}",
                Abilities: abilities,
                OracleText: "Wave fifteen text."),
        ]);

    private static CardFile Wave15FileWith(params Ability[] abilities)
    {
        var definition = Wave15DefinitionWith(abilities);
        return new CardFile(
            OracleId: definition.OracleId,
            Name: definition.Name,
            OracleHash: OracleHashing.Of(definition.Faces.Select(f => f.OracleText)),
            IrVersion: IrVersion.Current,
            Definition: definition,
            Status: ImplementationStatus.Translated,
            Source: new TemplateProvenance("test"));
    }

    private static CardDefinition Wave15RoundTrip(CardDefinition definition) =>
        CardFileSerializer.ReadDefinition(CardFileSerializer.WriteDefinition(definition));

    private static LintReport Wave15Lint(params Ability[] abilities) => Linter.Check(Wave15FileWith(abilities));

    /// <summary>A static ability granting you the permission given, the shape a "you may play ... from" line takes.</summary>
    private static StaticAbility Wave15Grants(MayPlayFromZone permission) => new(permission, Players: new You());

    /// <summary>The summary the build wrote for a vocabulary record, as the reference text reads it.</summary>
    private static string Wave15DocOf(Type type)
    {
        var path = Path.ChangeExtension(typeof(Linter).Assembly.Location, ".xml");
        Assert.True(File.Exists(path), "MtgEngine.Cards.xml is not beside the assembly; the reference text is generated from it.");
        var member = XDocument.Load(path).Descendants("member").Single(m => m.Attribute("name")?.Value == "T:" + type.FullName);
        return member.Element("summary")?.Value ?? string.Empty;
    }

    [Fact]
    public void A_play_permission_can_waive_the_mana_cost()
    {
        // CR 702.75a: hideaway's play is "you may play the exiled card without paying its mana
        // cost", and the card may be a land (CR 305.1), so the permission is a MayPlay.
        var hideaway = new KeywordAbility(KeywordKind.Hideaway, N: new Fixed(4), RememberAs: "hidden");
        var play = new ActivatedAbility([new TapSelf()], new MayPlay(new Remembered("hidden"), WithoutPayingManaCost: true));
        var back = Wave15RoundTrip(Wave15DefinitionWith(hideaway, play));

        var read = Assert.IsType<MayPlay>(Assert.IsType<ActivatedAbility>(back.Faces[0].Abilities![1]).Do);
        Assert.True(read.WithoutPayingManaCost);
        Assert.Equal("hidden", Assert.IsType<Remembered>(read.What).Key);
        Assert.Empty(Wave15Lint(hideaway, play).Findings);
    }

    [Fact]
    public void A_play_permission_waives_nothing_by_default()
    {
        var back = Wave15RoundTrip(Wave15DefinitionWith(new SpellAbility(new MayPlay(new Top(new You()), new UntilEndOfTurn()))));

        Assert.False(Assert.IsType<MayPlay>(Assert.IsType<SpellAbility>(back.Faces[0].Abilities![0]).Do).WithoutPayingManaCost);
    }

    [Fact]
    public void A_zone_permission_can_charge_life_equal_to_mana_value_instead()
    {
        var permission = new MayPlayFromZone(new You(), Zone.Library, TopOfLibraryOnly: true, PayLifeEqualToManaValueInstead: true);
        var back = Wave15RoundTrip(Wave15DefinitionWith(Wave15Grants(permission)));

        var read = Assert.IsType<MayPlayFromZone>(Assert.IsType<StaticAbility>(back.Faces[0].Abilities![0]).Effect);
        Assert.True(read.PayLifeEqualToManaValueInstead);
        Assert.False(read.WithoutPayingManaCost);
        Assert.True(read.TopOfLibraryOnly);
        Assert.Empty(Wave15Lint(Wave15Grants(permission)).Findings);
    }

    [Fact]
    public void Waiving_the_cost_and_charging_life_for_it_together_is_an_error()
    {
        // CR 118.9: a cast pays one alternative cost; a card prints one of the two.
        var report = Wave15Lint(Wave15Grants(new MayPlayFromZone(new You(), Zone.Graveyard, WithoutPayingManaCost: true, PayLifeEqualToManaValueInstead: true)));

        Assert.Contains(report.Findings, f => f.Code == "L021" && f.Severity == LintSeverity.Error);
    }

    [Fact]
    public void Either_alternative_cost_alone_is_not_an_error()
    {
        Assert.DoesNotContain(Wave15Lint(Wave15Grants(new MayPlayFromZone(new You(), Zone.Graveyard, WithoutPayingManaCost: true))).Findings, f => f.Code == "L021");
        Assert.DoesNotContain(Wave15Lint(Wave15Grants(new MayPlayFromZone(new You(), Zone.Graveyard, PayLifeEqualToManaValueInstead: true))).Findings, f => f.Code == "L021");
    }

    /// <summary>"Reveal the top X cards; put any number of permanent cards from among them onto the battlefield."</summary>
    private static SpellAbility Wave15ChoosesAmongRevealed(ObjectFilter filter) => new(new Sequence(
    [
        new Reveal(new Top(new You(), new X()), RememberAs: "revealed"),
        new Move(new Choose(filter, new You(), UpTo: true, Among: new Remembered("revealed"), Key: "put"), Zone.Battlefield),
    ]));

    [Fact]
    public void A_choice_can_be_made_among_remembered_objects()
    {
        var ability = Wave15ChoosesAmongRevealed(new ObjectFilter(Permanent: true, ManaValue: new Comparison(ComparisonOp.Le, new X())));
        var back = Wave15RoundTrip(Wave15DefinitionWith(ability));

        var steps = Assert.IsType<Sequence>(Assert.IsType<SpellAbility>(back.Faces[0].Abilities![0]).Do).Steps;
        var choose = Assert.IsType<Choose>(Assert.IsType<Move>(steps[1]).What);
        Assert.Equal("revealed", Assert.IsType<Remembered>(choose.Among).Key);
        Assert.True(choose.UpTo);
        Assert.Null(choose.Count);
        Assert.Null(choose.Filter.In);

        // The remembered key is produced on the face, so the choice is not an L003, and nothing else fires.
        Assert.Empty(Wave15Lint(ability).Findings);
    }

    [Fact]
    public void A_choice_among_a_key_nothing_produced_is_an_error()
    {
        // CR 603.10: Among names what an earlier step set aside; the selector inside it is checked like any Remembered.
        var report = Wave15Lint(new SpellAbility(new Move(
            new Choose(ObjectFilter.Any, new You(), Among: new Remembered("never-remembered")),
            Zone.Battlefield)));

        Assert.Contains(report.Findings, f => f.Code == "L003" && f.Severity == LintSeverity.Error);
    }

    [Fact]
    public void A_zone_on_the_filter_beside_among_is_an_error()
    {
        var report = Wave15Lint(Wave15ChoosesAmongRevealed(new ObjectFilter(In: Zone.Library, Permanent: true)));

        Assert.Contains(report.Findings, f => f.Code == "L022" && f.Severity == LintSeverity.Error);
    }

    [Fact]
    public void A_choice_without_among_still_takes_a_zone()
    {
        var ability = new SpellAbility(new Move(new Choose(new ObjectFilter(In: Zone.Graveyard, Types: CardType.Creature), new You()), Zone.Hand));

        Assert.DoesNotContain(Wave15Lint(ability).Findings, f => f.Code == "L022");
    }

    [Fact]
    public void A_filter_can_say_the_candidate_is_what_a_selector_names()
    {
        var registered = CardFileSerializer.RegisteredNames(typeof(Predicate));
        Assert.True(registered.ContainsKey(typeof(IsOneOf)), "IsOneOf has no [JsonDerivedType] on Predicate.");
        Assert.Equal(nameof(IsOneOf), registered[typeof(IsOneOf)]);

        // CR 301.5c: "whenever equipped creature dies" is the creature this Equipment is attached to.
        var ability = new TriggeredAbility(new Dies(new ObjectFilter(Also: new IsOneOf(new Attached()))), new Draw(new Fixed(2)));
        var back = Wave15RoundTrip(Wave15DefinitionWith(ability));

        var when = Assert.IsType<Dies>(Assert.IsType<TriggeredAbility>(back.Faces[0].Abilities![0]).When);
        Assert.IsType<Attached>(Assert.IsType<IsOneOf>(when.What.Also).Of);
        Assert.Empty(Wave15Lint(ability).Findings);
    }

    [Fact]
    public void A_filter_naming_a_remembered_selector_is_checked_for_its_key()
    {
        var report = Wave15Lint(new TriggeredAbility(
            new Dies(new ObjectFilter(Also: new IsOneOf(new Remembered("unmade")))),
            new Draw(new Fixed(1))));

        Assert.Contains(report.Findings, f => f.Code == "L003" && f.Severity == LintSeverity.Error);
    }

    [Fact]
    public void The_equipment_fixture_names_the_equipped_creature_from_the_equipments_side()
    {
        // The fixture used to write AttachedTo(Self), which the AttachedTo doc says admits the Auras on
        // the Equipment and never the creature it is on; "equipped creature" in a filter is IsOneOf(Attached).
        var dies = Fixtures.Clamp().Faces[0].Abilities!.OfType<TriggeredAbility>().Select(t => t.When).OfType<Dies>().Single();

        Assert.IsType<Attached>(Assert.IsType<IsOneOf>(dies.What.Also).Of);
        Assert.DoesNotContain(Linter.Walk(Fixtures.Clamp()), n => n is AttachedTo);
    }

    [Theory]
    [InlineData(typeof(TotalAmong))]
    [InlineData(typeof(GreatestAmong))]
    public void The_among_amounts_say_they_bind_the_candidate_as_self(Type amount)
    {
        var doc = Wave15DocOf(amount);

        Assert.Contains("bound as Self", doc, StringComparison.Ordinal);
        Assert.Contains("Triggering", doc, StringComparison.Ordinal);
    }

    [Fact]
    public void The_wave_fifteen_additions_did_not_move_the_IR_version()
    {
        // Two optional flags, one optional selector and one new predicate: every stored file still reads.
        Assert.Equal(1, IrVersion.Current);
    }
}
