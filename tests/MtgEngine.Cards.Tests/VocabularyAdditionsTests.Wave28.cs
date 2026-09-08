namespace MtgEngine.Cards.Tests;

/// <summary>
/// Wave 28 (seam-requests/V17.md): seven gaps the corpus could not say. A token whose power and
/// toughness are worked out as it is created (CR 111.3), an edict that keeps one permanent of each
/// permanent type (CR 701.21 with 110.4), a play permission limited to once a turn and charging an
/// additional cost (CR 118.8, 116.2a), a continuous effect that forbids entering the battlefield
/// (CR 613.11) over a filter naming two zones (CR 400.1), a cast restriction that names two zones
/// (CR 601.3), storm as the keyword the casting system reads (CR 702.40), and a scenario expectation
/// for a phased-out permanent (CR 702.26b). Each round-trips, lints where its halves can contradict,
/// and leaves the IR version where it is.
/// </summary>
public sealed partial class VocabularyAdditionsTests
{
    private static CardDefinition Wave28DefinitionWith(params Ability[] abilities) => new(
        OracleId: "00000000-0000-0000-0000-000000000f28",
        Name: "Wave Twenty Eight Addition",
        Layout: Layout.Normal,
        Faces:
        [
            new Face(
                Name: "Wave Twenty Eight Addition",
                Role: FaceRole.Main,
                Types: new TypeLine(CardType.Enchantment),
                ManaCost: "{2}{B}",
                Abilities: abilities,
                OracleText: "Wave twenty eight text."),
        ]);

    private static CardDefinition Wave28RoundTrip(CardDefinition definition) =>
        CardFileSerializer.ReadDefinition(CardFileSerializer.WriteDefinition(definition));

    private static CardFile Wave28File(CardDefinition definition, IReadOnlyList<Scenario>? scenarios = null) => new(
        OracleId: definition.OracleId,
        Name: definition.Name,
        OracleHash: OracleHashing.Of(["Wave twenty eight text."]),
        IrVersion: IrVersion.Current,
        Definition: definition,
        Status: ImplementationStatus.Translated,
        Source: new TemplateProvenance("test"),
        Scenarios: scenarios);

    private static IReadOnlyList<LintFinding> Wave28Errors(params Ability[] abilities) =>
        Linter.Check(Wave28File(Wave28DefinitionWith(abilities))).Findings.Where(f => f.Severity == LintSeverity.Error).ToList();

    private static Scenario Wave28Scenario(params Expectation[] expect) => new(
        Id: "wave28",
        Title: "Wave twenty eight",
        Source: new GeneratedSource("test"),
        Setup: new ScenarioSetup([new PlayerSetup("Alice"), new PlayerSetup("Bob")], "Alice"),
        Steps: [],
        Expect: expect);

    [Fact]
    public void A_token_takes_its_power_and_toughness_from_an_amount()
    {
        // CR 111.3: "create an X/X token, where X is the number of creatures destroyed this way".
        var destroyed = new Destroy(new Target(ObjectFilter.Creature), RememberAs: "gone");
        var token = new TokenSpec(
            "Zombie",
            Color.Black,
            CardType.Creature,
            Subtypes: ["Zombie"],
            PowerAmount: new CountOf(new ObjectFilter(Also: new IsRemembered("gone"))),
            ToughnessAmount: new CountOf(new ObjectFilter(Also: new IsRemembered("gone"))));
        var ability = new SpellAbility(new Sequence([destroyed, new CreateToken(token)]));
        var back = Wave28RoundTrip(Wave28DefinitionWith(ability));

        var created = Assert.IsType<CreateToken>(Assert.IsType<Sequence>(Assert.IsType<SpellAbility>(back.Faces[0].Abilities![0]).Do).Steps[1]);
        Assert.IsType<CountOf>(created.Token.PowerAmount);
        Assert.IsType<CountOf>(created.Token.ToughnessAmount);
        Assert.Null(created.Token.Power);
        Assert.Null(created.Token.Toughness);
        Assert.Empty(Wave28Errors(ability));
    }

    [Fact]
    public void A_token_power_is_defined_once_and_only_on_a_creature()
    {
        // CR 111.3 with 208.1: two definitions of one characteristic, and a power on a non-creature.
        var both = new SpellAbility(new CreateToken(new TokenSpec("Zombie", Color.Black, CardType.Creature, Power: 2, PowerAmount: new Fixed(2))));
        Assert.Contains(Wave28Errors(both), f => f.Code == "L047" && f.Message.Contains("Power and PowerAmount", StringComparison.Ordinal));

        var notACreature = new SpellAbility(new CreateToken(new TokenSpec("Treasure", Color.Colorless, CardType.Artifact, PowerAmount: new Fixed(1))));
        Assert.Contains(Wave28Errors(notACreature), f => f.Code == "L047" && f.Message.Contains("208.1", StringComparison.Ordinal));
    }

    [Fact]
    public void An_edict_keeps_one_permanent_of_each_permanent_type()
    {
        // CR 701.21 with 110.4: "each opponent chooses a permanent they control of each permanent
        // type, then sacrifices the rest".
        var ability = new SpellAbility(new Sacrifice(Who: new EachOpponent(), KeepOnePerPermanentType: true));
        var back = Wave28RoundTrip(Wave28DefinitionWith(ability));

        var sacrifice = Assert.IsType<Sacrifice>(Assert.IsType<SpellAbility>(back.Faces[0].Abilities![0]).Do);
        Assert.True(sacrifice.KeepOnePerPermanentType);
        Assert.Null(sacrifice.N);
        Assert.Empty(Wave28Errors(ability));

        var counted = new SpellAbility(new Sacrifice(Who: new EachOpponent(), N: new Fixed(1), KeepOnePerPermanentType: true));
        Assert.Contains(Wave28Errors(counted), f => f.Code == "L047" && f.Message.Contains("110.4", StringComparison.Ordinal));
    }

    [Fact]
    public void A_play_permission_is_limited_to_once_a_turn_and_charges_a_cost()
    {
        // CR 118.8 with 116.2a: "once during each of your turns, you may cast a permanent spell from
        // your graveyard by sacrificing a land in addition to paying its other costs".
        var permission = new MayPlayFromZone(
            new You(),
            Zone.Graveyard,
            What: new ObjectFilter(Permanent: true),
            OncePerTurn: true,
            AdditionalCost: new SacrificeCost(new ObjectFilter(Types: CardType.Land)));
        var ability = new StaticAbility(permission);
        var back = Wave28RoundTrip(Wave28DefinitionWith(ability));

        var read = Assert.IsType<MayPlayFromZone>(Assert.IsType<StaticAbility>(back.Faces[0].Abilities![0]).Effect);
        Assert.True(read.OncePerTurn);
        Assert.IsType<SacrificeCost>(read.AdditionalCost);
        Assert.False(read.WithoutPayingManaCost);
        Assert.Empty(Wave28Errors(ability));

        var free = new StaticAbility(permission with { WithoutPayingManaCost = true });
        Assert.Contains(Wave28Errors(free), f => f.Code == "L047" && f.Message.Contains("118.8", StringComparison.Ordinal));
    }

    [Fact]
    public void Cards_in_two_zones_cant_enter_the_battlefield()
    {
        // CR 613.11 with 400.1: "creature cards in graveyards and libraries can't enter the
        // battlefield".
        var ability = new StaticAbility(new CantEnterBattlefield(
            new ObjectFilter(Types: CardType.Creature, InAnyOf: [Zone.Graveyard, Zone.Library])));
        var back = Wave28RoundTrip(Wave28DefinitionWith(ability));

        var effect = Assert.IsType<CantEnterBattlefield>(Assert.IsType<StaticAbility>(back.Faces[0].Abilities![0]).Effect);
        Assert.Equal([Zone.Graveyard, Zone.Library], effect.What.InAnyOf);
        Assert.Null(effect.What.In);
        Assert.Empty(Wave28Errors(ability));
    }

    [Fact]
    public void A_filter_names_one_zone_or_the_list_of_zones_it_admits()
    {
        // CR 400.1: an object is in exactly one zone, so both readings cannot be read.
        var both = new StaticAbility(new CantEnterBattlefield(
            new ObjectFilter(In: Zone.Graveyard, InAnyOf: [Zone.Graveyard, Zone.Library])));
        Assert.Contains(Wave28Errors(both), f => f.Code == "L047" && f.Message.Contains("InAnyOf", StringComparison.Ordinal));

        var one = new StaticAbility(new CantEnterBattlefield(new ObjectFilter(InAnyOf: [Zone.Graveyard])));
        Assert.Contains(Wave28Errors(one), f => f.Code == "L047" && f.Message.Contains("1 zone(s)", StringComparison.Ordinal));
    }

    [Fact]
    public void A_cast_restriction_names_two_zones()
    {
        // CR 601.3: "players can't cast spells from graveyards or libraries".
        var ability = new StaticAbility(new CantCastSpells(
            new EachPlayer(),
            CastFrom: [Zone.Graveyard, Zone.Library]));
        var back = Wave28RoundTrip(Wave28DefinitionWith(ability));

        var effect = Assert.IsType<CantCastSpells>(Assert.IsType<StaticAbility>(back.Faces[0].Abilities![0]).Effect);
        Assert.Equal([Zone.Graveyard, Zone.Library], effect.CastFrom);
        Assert.Empty(Wave28Errors(ability));

        var twice = new StaticAbility(new CantCastSpells(
            new EachPlayer(),
            Matching: new ObjectFilter(Spell: true, NotCastFrom: Zone.Hand),
            CastFrom: [Zone.Graveyard, Zone.Library]));
        Assert.Contains(Wave28Errors(twice), f => f.Code == "L047" && f.Message.Contains("601.3", StringComparison.Ordinal));
    }

    [Fact]
    public void Storm_is_a_keyword_the_casting_system_reads()
    {
        // CR 702.40: the copies are the keyword's own triggered ability, so the card prints the
        // keyword and nothing else.
        var ability = new KeywordAbility(KeywordKind.Storm);
        var back = Wave28RoundTrip(Wave28DefinitionWith(ability));

        Assert.Equal(KeywordKind.Storm, Assert.IsType<KeywordAbility>(back.Faces[0].Abilities![0]).Keyword);
        Assert.Empty(Wave28Errors(ability));

        var rule = typeof(KeywordKind).GetField(nameof(KeywordKind.Storm))!
            .GetCustomAttributes(typeof(RuleRefAttribute), false)
            .Cast<RuleRefAttribute>()
            .Single();
        Assert.Equal("702.40", rule.Rule);
    }

    [Fact]
    public void A_scenario_expects_a_permanent_to_be_phased_out()
    {
        // CR 702.26b: phasing is a status, and a phased-out permanent stays on the battlefield.
        var scenario = Wave28Scenario(new PhasedOutIs("Galadriel's Dismissal Target"), new InZone("Galadriel's Dismissal Target", Zone.Battlefield));
        var file = Wave28File(Wave28DefinitionWith(new SpellAbility(new PhaseOut(new Target(ObjectFilter.Creature)))), [scenario]);
        var back = CardFileSerializer.Read(CardFileSerializer.Write(file));

        var expectation = Assert.IsType<PhasedOutIs>(back.ScenarioList[0].Expect[0]);
        Assert.True(expectation.PhasedOut);
        Assert.DoesNotContain(Linter.Check(file).Findings, f => f.Severity == LintSeverity.Error);
    }

    [Fact]
    public void A_phased_out_permanent_is_not_in_another_zone()
    {
        // CR 702.26b: its zone does not change, so the two expectations cannot both hold.
        var scenario = Wave28Scenario(new PhasedOutIs("Bear"), new InZone("Bear", Zone.Exile));
        var file = Wave28File(Wave28DefinitionWith(new SpellAbility(new PhaseOut(new Target(ObjectFilter.Creature)))), [scenario]);

        var errors = Linter.Check(file).Findings.Where(f => f.Severity == LintSeverity.Error).ToList();
        Assert.Contains(errors, f => f.Code == "L048" && f.Message.Contains("702.26b", StringComparison.Ordinal));
    }

    [Fact]
    public void The_wave_twenty_eight_additions_do_not_move_the_ir_version()
    {
        // Two new kinds under bases that already exist, five optional fields appended to existing
        // records: every stored card file still reads, so IrVersion stays where it is.
        Assert.Equal(1, IrVersion.Current);
    }
}
