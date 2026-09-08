namespace MtgEngine.Cards.Tests;

/// <summary>
/// Wave 30 (seam-requests/V19.md): the vocabulary slice 010 could not say. "Players can't lose life"
/// (CR 119.3), the player half of hexproof (CR 702.11c), an ability that triggers an additional time
/// because of a named cause (CR 603.2d), one player's count against the greatest among the others
/// (CR 400.2), the free cast as an alternative cost (CR 118.9), and the store's enumeration of the
/// names the game knows (CR 201.3). Each round-trips, lints where its halves can contradict, and
/// leaves the IR version where it is.
/// </summary>
public sealed partial class VocabularyAdditionsTests
{
    private static CardDefinition Wave30DefinitionWith(params Ability[] abilities) => Wave30Definition(null, abilities);

    private static CardDefinition Wave30Definition(IReadOnlyList<AlternativeCost>? alternatives, params Ability[] abilities) => new(
        OracleId: "00000000-0000-0000-0000-000000000f30",
        Name: "Wave Thirty Addition",
        Layout: Layout.Normal,
        Faces:
        [
            new Face(
                Name: "Wave Thirty Addition",
                Role: FaceRole.Main,
                Types: new TypeLine(CardType.Instant),
                ManaCost: "{2}{U}",
                Abilities: abilities,
                AlternativeCosts: alternatives,
                OracleText: "Wave thirty text."),
        ]);

    private static CardDefinition Wave30RoundTrip(CardDefinition definition) =>
        CardFileSerializer.ReadDefinition(CardFileSerializer.WriteDefinition(definition));

    private static IReadOnlyList<LintFinding> Wave30Errors(CardDefinition definition) => Linter.Check(new CardFile(
        OracleId: definition.OracleId,
        Name: definition.Name,
        OracleHash: OracleHashing.Of(["Wave thirty text."]),
        IrVersion: IrVersion.Current,
        Definition: definition,
        Status: ImplementationStatus.Translated,
        Source: new TemplateProvenance("test"))).Findings.Where(f => f.Severity == LintSeverity.Error).ToList();

    private static IReadOnlyList<LintFinding> Wave30Errors(params Ability[] abilities) => Wave30Errors(Wave30DefinitionWith(abilities));

    // 1. "Players can't lose life this turn" (Everybody Lives!), CR 119.3.
    [Fact]
    public void Players_cant_lose_life_is_a_restriction_of_its_own()
    {
        var ability = new StaticAbility(new PlayerCant(PlayerRestriction.LoseLife), Players: new EachPlayer());
        var read = Wave30RoundTrip(Wave30DefinitionWith(ability));

        var cant = Assert.IsType<PlayerCant>(Assert.IsType<StaticAbility>(read.Faces[0].AbilityList[0]).Effect);
        Assert.True(cant.What.HasFlag(PlayerRestriction.LoseLife));
        Assert.False(cant.What.HasFlag(PlayerRestriction.LifeTotalChanges));
        Assert.Empty(Wave30Errors(ability));
    }

    [Fact]
    public void Cant_lose_life_beside_cant_change_is_refused()
    {
        var ability = new StaticAbility(
            new PlayerCant(PlayerRestriction.LoseLife | PlayerRestriction.LifeTotalChanges),
            Players: new EachPlayer());

        Assert.Contains(Wave30Errors(ability), f => f.Code == "L050");
    }

    // 2. "Players gain hexproof until end of turn", CR 702.11c.
    [Fact]
    public void Players_gain_hexproof_is_the_player_half_of_the_keyword()
    {
        var ability = new SpellAbility(new ApplyToPlayer(new PlayerHexproof(), new EachPlayer(), new UntilEndOfTurn()));
        var read = Wave30RoundTrip(Wave30DefinitionWith(ability));

        var applied = Assert.IsType<ApplyToPlayer>(Assert.IsType<SpellAbility>(read.Faces[0].AbilityList[0]).Do);
        Assert.IsType<PlayerHexproof>(applied.What);
        Assert.Empty(Wave30Errors(ability));
    }

    // 3. "If a land entering causes a triggered ability of a permanent you control to trigger, that
    //    ability triggers an additional time", CR 603.2d.
    [Fact]
    public void Abilities_trigger_twice_names_the_cause()
    {
        var ability = new StaticAbility(new AbilitiesTriggerTwice(
            new You(),
            OfSources: new ObjectFilter(ControlledBy: new You()),
            Cause: new EntersBattlefield(new ObjectFilter(Types: CardType.Land))));
        var read = Wave30RoundTrip(Wave30DefinitionWith(ability));

        var twice = Assert.IsType<AbilitiesTriggerTwice>(Assert.IsType<StaticAbility>(read.Faces[0].AbilityList[0]).Effect);
        var cause = Assert.IsType<EntersBattlefield>(twice.Cause);
        Assert.Equal(CardType.Land, cause.What.Types);
        Assert.Empty(Wave30Errors(ability));
    }

    // 4. "Draw a card if that player has more cards in hand than each other player", CR 400.2.
    [Fact]
    public void More_than_each_other_player_is_one_condition()
    {
        var condition = new MoreThanEachOtherPlayer(new TriggeringPlayer(), new CardsInHandOf(new EachPlayer()));
        var ability = new TriggeredAbility(new EntersBattlefield(new ObjectFilter()), new Draw(new Fixed(1), new You()), If: condition);
        var read = Wave30RoundTrip(Wave30DefinitionWith(ability));

        var more = Assert.IsType<MoreThanEachOtherPlayer>(Assert.IsType<TriggeredAbility>(read.Faces[0].AbilityList[0]).If);
        Assert.IsType<TriggeringPlayer>(more.Who);
        Assert.IsType<CardsInHandOf>(more.Of);
        Assert.False(more.OrEqual);
        Assert.Empty(Wave30Errors(ability));
    }

    [Fact]
    public void More_than_each_other_player_whose_amount_names_a_fixed_player_is_refused()
    {
        var ability = new TriggeredAbility(
            new EntersBattlefield(new ObjectFilter()),
            new Draw(new Fixed(1), new You()),
            If: new MoreThanEachOtherPlayer(new You(), new CardsInHandOf(new You())));

        Assert.Contains(Wave30Errors(ability), f => f.Code == "L050");
    }

    // 7. "If you control a commander, you may cast this spell without paying its mana cost", CR 118.9.
    [Fact]
    public void The_free_cast_is_an_alternative_cost_with_the_flag()
    {
        var alternative = new AlternativeCost(
            new Costs([]),
            OnlyIf: new Controls(new You(), new ObjectFilter(IsCommander: true), new Comparison(ComparisonOp.Ge, new Fixed(1))),
            WithoutPayingManaCost: true);
        var definition = Wave30Definition([alternative], new SpellAbility(new Draw(new Fixed(1), new You())));
        var read = Wave30RoundTrip(definition);

        var back = Assert.Single(read.Faces[0].AlternativeCosts!);
        Assert.True(back.WithoutPayingManaCost);
        Assert.Empty(Assert.IsType<Costs>(back.Cost).All);
        Assert.Empty(Wave30Errors(definition));
    }

    [Fact]
    public void The_free_cast_written_as_a_cost_of_zero_is_refused()
    {
        var definition = Wave30Definition(
            [new AlternativeCost(new ManaCost("{0}"))],
            new SpellAbility(new Draw(new Fixed(1), new You())));

        Assert.Contains(Wave30Errors(definition), f => f.Code == "L050");
    }

    [Fact]
    public void The_free_cast_flag_beside_a_cost_is_refused()
    {
        var definition = Wave30Definition(
            [new AlternativeCost(new PayLife(new Fixed(2)), WithoutPayingManaCost: true)],
            new SpellAbility(new Draw(new Fixed(1), new You())));

        Assert.Contains(Wave30Errors(definition), f => f.Code == "L050");
    }

    // 6. A damage trigger granted through GrantAbility is a damage trigger, CR 613.1f with 603.10.
    [Fact]
    public void Triggering_damage_inside_a_granted_trigger_is_admitted()
    {
        var granted = new TriggeredAbility(
            new DealsDamage(new ObjectFilter(), ToPlayer: PlayerFilter.Any, Combat: true),
            new Draw(new TriggeringDamage(), new You()));
        var ability = new StaticAbility(new GrantAbility(granted), To: new Attached());

        Assert.DoesNotContain(Wave30Errors(ability), f => f.Code == "L028");
    }

    // 8. The store enumerates the names the game knows, CR 201.3.
    [Fact]
    public void A_definition_store_enumerates_no_names_by_default()
    {
        IDefinitionStore store = new Wave30EmptyStore();

        Assert.Empty(store.Names);
    }

    [Fact]
    public void A_definition_store_may_enumerate_its_names()
    {
        IDefinitionStore store = new Wave30NamedStore();

        Assert.Equal(["Wave Thirty Addition"], store.Names);
    }

    private sealed class Wave30EmptyStore : IDefinitionStore
    {
        public CardDefinition? ByName(string name) => null;

        public CardDefinition? ByOracleId(string oracleId) => null;
    }

    private sealed class Wave30NamedStore : IDefinitionStore
    {
        public CardDefinition? ByName(string name) => name == "Wave Thirty Addition" ? Wave30DefinitionWith() : null;

        public CardDefinition? ByOracleId(string oracleId) => null;

        public IEnumerable<string> Names => ["Wave Thirty Addition"];
    }
}
