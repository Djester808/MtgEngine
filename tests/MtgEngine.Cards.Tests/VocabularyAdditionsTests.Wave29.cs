namespace MtgEngine.Cards.Tests;

/// <summary>
/// Wave 29 (seam-requests/V18.md): nine gaps the corpus could not say. A target range with a low end
/// (CR 601.2c), life lost this turn by a group of players (CR 119.3), a count of distinct names
/// (CR 201.2b), the commander tax's count (CR 903.8), a granted keyword whose cost is not mana
/// (CR 613.1f with 702.21a), a spell cast when a sorcery could not have been (CR 307.5a), a
/// permanent entering prepared (CR 722.3a), a named card narrowed to a card type (CR 201.2), and a
/// damage replacement that names its recipients (CR 614.1). Each round-trips, lints where its halves
/// can contradict, and leaves the IR version where it is.
/// </summary>
public sealed partial class VocabularyAdditionsTests
{
    private static CardDefinition Wave29DefinitionWith(params Ability[] abilities) => new(
        OracleId: "00000000-0000-0000-0000-000000000f29",
        Name: "Wave Twenty Nine Addition",
        Layout: Layout.Normal,
        Faces:
        [
            new Face(
                Name: "Wave Twenty Nine Addition",
                Role: FaceRole.Main,
                Types: new TypeLine(CardType.Enchantment),
                ManaCost: "{2}{B}",
                Abilities: abilities,
                OracleText: "Wave twenty nine text."),
        ]);

    private static CardDefinition Wave29RoundTrip(CardDefinition definition) =>
        CardFileSerializer.ReadDefinition(CardFileSerializer.WriteDefinition(definition));

    private static IReadOnlyList<LintFinding> Wave29Errors(params Ability[] abilities) => Linter.Check(new CardFile(
        OracleId: Wave29DefinitionWith(abilities).OracleId,
        Name: "Wave Twenty Nine Addition",
        OracleHash: OracleHashing.Of(["Wave twenty nine text."]),
        IrVersion: IrVersion.Current,
        Definition: Wave29DefinitionWith(abilities),
        Status: ImplementationStatus.Translated,
        Source: new TemplateProvenance("test"))).Findings.Where(f => f.Severity == LintSeverity.Error).ToList();

    private static Ability Wave29FirstAbility(CardDefinition definition) => definition.Faces[0].Abilities![0];

    [Fact]
    public void A_target_names_the_low_end_of_its_range()
    {
        // CR 601.2c: "one or two target creatures" announces a number inside a range.
        var ability = new SpellAbility(new DealDamage(new Fixed(2), new Target(ObjectFilter.Creature, Count: new Fixed(2), Min: new Fixed(1))));
        var back = Wave29RoundTrip(Wave29DefinitionWith(ability));

        var target = Assert.IsType<Target>(Assert.IsType<DealDamage>(Assert.IsType<SpellAbility>(Wave29FirstAbility(back)).Do).To);
        Assert.Equal(1, Assert.IsType<Fixed>(target.Min).N);
        Assert.Equal(2, Assert.IsType<Fixed>(target.Count).N);
        Assert.False(target.UpTo);
        Assert.Empty(Wave29Errors(ability));
    }

    [Fact]
    public void A_target_range_needs_a_high_end_and_is_never_up_to()
    {
        // CR 601.2c: "up to" is the range whose low end is zero, and a low end above the high end
        // is a range no number of targets satisfies.
        var upTo = new SpellAbility(new DealDamage(new Fixed(2), new Target(ObjectFilter.Creature, Count: new Fixed(2), UpTo: true, Min: new Fixed(1))));
        Assert.Contains(Wave29Errors(upTo), f => f.Code == "L049" && f.Message.Contains("UpTo", StringComparison.Ordinal));

        var noCount = new SpellAbility(new DealDamage(new Fixed(2), new Target(ObjectFilter.Creature, Min: new Fixed(1))));
        Assert.Contains(Wave29Errors(noCount), f => f.Code == "L049" && f.Message.Contains("no Count", StringComparison.Ordinal));

        var backwards = new SpellAbility(new DealDamage(new Fixed(2), new Target(ObjectFilter.Creature, Count: new Fixed(1), Min: new Fixed(3))));
        Assert.Contains(Wave29Errors(backwards), f => f.Code == "L049" && f.Message.Contains("above its Count", StringComparison.Ordinal));
    }

    [Fact]
    public void Life_lost_this_turn_is_read_over_a_group_of_players()
    {
        // CR 119.3: "if an opponent lost life this turn" is the opponents' total against zero.
        var ability = new SpellAbility(new If(
            new Compare(new LifeLostThisTurn(PlayerFilter.Opponents), ComparisonOp.Gt, new Fixed(0)),
            new Draw(new Fixed(1))));
        var back = Wave29RoundTrip(Wave29DefinitionWith(ability));

        var compare = Assert.IsType<Compare>(Assert.IsType<If>(Assert.IsType<SpellAbility>(Wave29FirstAbility(back)).Do).Test);
        var lost = Assert.IsType<LifeLostThisTurn>(compare.Left);
        Assert.True(lost.Who.Opponent);
        Assert.Empty(Wave29Errors(ability));
    }

    [Fact]
    public void A_count_of_distinct_names_is_not_a_count_of_objects()
    {
        // CR 201.2b: "lands you control with different names".
        var ability = new SpellAbility(new Draw(new DistinctNameCount(new ObjectFilter(Types: CardType.Land, ControlledBy: new You()))));
        var back = Wave29RoundTrip(Wave29DefinitionWith(ability));

        var count = Assert.IsType<DistinctNameCount>(Assert.IsType<Draw>(Assert.IsType<SpellAbility>(Wave29FirstAbility(back)).Do).N);
        Assert.Equal(CardType.Land, count.Filter.Types);
        Assert.Empty(Wave29Errors(ability));
    }

    [Fact]
    public void The_commander_tax_counts_casts_from_the_command_zone()
    {
        // CR 903.8: the number of previous times it was cast from the command zone this game.
        var ability = new SpellAbility(new Draw(new CommanderCastsThisGame()));
        var back = Wave29RoundTrip(Wave29DefinitionWith(ability));

        var count = Assert.IsType<CommanderCastsThisGame>(Assert.IsType<Draw>(Assert.IsType<SpellAbility>(Wave29FirstAbility(back)).Do).N);
        Assert.Null(count.Which);
        Assert.Empty(Wave29Errors(ability));
    }

    [Fact]
    public void A_granted_keyword_carries_a_cost_that_is_not_mana()
    {
        // CR 613.1f with 702.21a: creatures you control have "Ward-Pay 2 life".
        var grant = new GrantKeyword(KeywordKind.Ward, Costs: [new PayLife(new Fixed(2))]);
        var ability = new StaticAbility(grant);
        var back = Wave29RoundTrip(Wave29DefinitionWith(ability));

        var read = Assert.IsType<GrantKeyword>(Assert.IsType<StaticAbility>(Wave29FirstAbility(back)).Effect);
        Assert.Null(read.Cost);
        Assert.Equal(2, Assert.IsType<Fixed>(Assert.IsType<PayLife>(read.Costs![0]).Amount).N);
        Assert.Empty(Wave29Errors(ability));

        var twice = new StaticAbility(new GrantKeyword(KeywordKind.Ward, Cost: "{2}", Costs: [new PayLife(new Fixed(2))]));
        Assert.Contains(Wave29Errors(twice), f => f.Code == "L049" && f.Message.Contains("both Cost and Costs", StringComparison.Ordinal));
    }

    [Fact]
    public void A_spell_asks_whether_it_was_cast_at_instant_speed()
    {
        // CR 307.5a: "if you cast it any time a sorcery couldn't have been cast".
        var ability = new SpellAbility(new If(new CastAtInstantSpeed(), new Draw(new Fixed(1))));
        var back = Wave29RoundTrip(Wave29DefinitionWith(ability));

        Assert.IsType<CastAtInstantSpeed>(Assert.IsType<If>(Assert.IsType<SpellAbility>(Wave29FirstAbility(back)).Do).Test);
        Assert.Empty(Wave29Errors(ability));
    }

    [Fact]
    public void A_permanent_enters_prepared()
    {
        // CR 722.3a with 614.1c: "this creature enters prepared".
        var ability = new ReplacementAbility(new WouldEnterBattlefield(ObjectFilter.Any), new EntersPrepared(), SelfOnly: true);
        var back = Wave29RoundTrip(Wave29DefinitionWith(ability));

        Assert.IsType<EntersPrepared>(Assert.IsType<ReplacementAbility>(Wave29FirstAbility(back)).With);
        Assert.Empty(Wave29Errors(ability));

        var loose = new SpellAbility(new EntersPrepared());
        Assert.Contains(Wave29Errors(loose), f => f.Code == "L049" && f.Message.Contains("EntersPrepared", StringComparison.Ordinal));
    }

    [Fact]
    public void A_chosen_card_name_is_narrowed_to_a_card_type()
    {
        // CR 201.2 with 205.2a: "choose a land card name".
        var ability = new SpellAbility(new ChooseValue(ChooseWhat.CardName, "named", OfType: CardType.Land));
        var back = Wave29RoundTrip(Wave29DefinitionWith(ability));

        var chosen = Assert.IsType<ChooseValue>(Assert.IsType<SpellAbility>(Wave29FirstAbility(back)).Do);
        Assert.Equal(CardType.Land, chosen.OfType);
        Assert.Empty(Wave29Errors(ability));

        var wrongChoice = new SpellAbility(new ChooseValue(ChooseWhat.Color, "picked", OfType: CardType.Land));
        Assert.Contains(Wave29Errors(wrongChoice), f => f.Code == "L049" && f.Message.Contains("OfType", StringComparison.Ordinal));
    }

    [Fact]
    public void A_damage_replacement_names_a_player_or_an_object()
    {
        // CR 614.1: "if a source you control would deal damage to an opponent or a permanent an
        // opponent controls".
        var replaced = new WouldDealDamage(
            new ObjectFilter(ControlledBy: new You()),
            To: new ObjectFilter(Types: CardType.Creature, ControlledBy: new EachOpponent()),
            ToPlayer: PlayerFilter.Opponents);
        var ability = new ReplacementAbility(replaced, new ModifyAmount(Add: 1));
        var back = Wave29RoundTrip(Wave29DefinitionWith(ability));

        var read = Assert.IsType<WouldDealDamage>(Assert.IsType<ReplacementAbility>(Wave29FirstAbility(back)).Instead);
        Assert.Equal(CardType.Creature, read.To!.Types);
        Assert.True(read.ToPlayer!.Opponent);
        Assert.Empty(Wave29Errors(ability));

        var elsewhere = new ReplacementAbility(
            new WouldDealDamage(new ObjectFilter(), To: new ObjectFilter(In: Zone.Graveyard)),
            new ModifyAmount(Add: 1));
        Assert.Contains(Wave29Errors(elsewhere), f => f.Code == "L049" && f.Message.Contains("Graveyard", StringComparison.Ordinal));
    }

    [Fact]
    public void The_wave_twenty_nine_records_leave_the_ir_version_alone()
    {
        // Three new amounts, one new condition and one new effect under bases that already exist,
        // and four optional fields appended last to existing records: every stored card file reads.
        Assert.Equal(1, IrVersion.Current);
    }
}
