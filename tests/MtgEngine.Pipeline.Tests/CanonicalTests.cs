using MtgEngine.Cards.Pipeline.Agree;

namespace MtgEngine.Cards.Pipeline.Tests;

/// <summary>
/// The canonical reading: one spelling per card before agreement is judged. Every definition here
/// is made up, no real card is named, and nothing touches the network or the corpus.
///
/// The rule each test protects is the same one: a difference that is only a way of writing the
/// same instruction is not a disagreement, and a difference that changes what the card does is.
/// The second half of that is the part with teeth — <see cref="Nothing_semantic_is_canonicalised_away"/>
/// is the test that stops this class from turning agreement into a rubber stamp.
/// </summary>
public sealed partial class CanonicalTests
{
    private const string OracleId = "00000000-0000-0000-0000-0000000000c1";

    private static CardDefinition Card(params Ability[] abilities) => new(
        OracleId: OracleId,
        Name: "Canonical Scribe",
        Layout: Layout.Normal,
        Faces:
        [
            new Face(
                Name: "Canonical Scribe",
                Role: FaceRole.Main,
                Types: new TypeLine(CardType.Creature, Subtypes: ["Human"]),
                ManaCost: "{1}{U}",
                Power: new PrintedStat("1"),
                Toughness: new PrintedStat("2"),
                Abilities: abilities,
                OracleText: "Canonical Scribe"),
        ]);

    private static CardDefinition Spell(Effect effect) => Card(new SpellAbility(effect));

    private static Effect Do(CardDefinition definition) => ((SpellAbility)definition.Faces[0].AbilityList[0]).Do;

    /// <summary>The two readings say the same thing: the diff, which canonicalises both, finds nothing.</summary>
    private static void Same(CardDefinition left, CardDefinition right) => Assert.Null(JsonDiff.Between(left, right));

    /// <summary>The two readings disagree about the card, and no rewrite may hide that.</summary>
    private static void Differs(CardDefinition left, CardDefinition right) => Assert.NotNull(JsonDiff.Between(left, right));

    [Fact]
    public void An_explicit_you_is_dropped_wherever_absent_already_means_you()
    {
        Same(Spell(new Draw(new Fixed(1), new You())), Spell(new Draw(new Fixed(1))));
        Same(Spell(new GainLife(new Fixed(2), new You())), Spell(new GainLife(new Fixed(2))));
        Same(
            Spell(new Sequence([new LoseLife(new Fixed(1), new You()), new Mill(new Fixed(2), new You())])),
            Spell(new Sequence([new LoseLife(new Fixed(1)), new Mill(new Fixed(2))])));

        // And the record really loses it, so the card file carries one spelling too.
        Assert.Null(((Draw)Do(Canonical.Of(Spell(new Draw(new Fixed(1), new You()))))).Who);
    }

    [Fact]
    public void A_player_that_is_not_the_default_is_kept()
    {
        // DealDamage.ToPlayer absent means no player is damaged, not "you", so You stays.
        var dealt = (DealDamage)Do(Canonical.Of(Spell(new DealDamage(new Fixed(2), ToPlayer: new You()))));
        Assert.IsType<You>(dealt.ToPlayer);
        Differs(Spell(new DealDamage(new Fixed(2), ToPlayer: new You())), Spell(new DealDamage(new Fixed(2))));

        // A named player is a different card from an unnamed one.
        Differs(Spell(new Draw(new Fixed(1), new EachOpponent())), Spell(new Draw(new Fixed(1))));
    }

    [Fact]
    public void Controlled_by_you_is_dropped_inside_a_cost_and_kept_outside_one()
    {
        var yours = new ObjectFilter(Types: CardType.Creature, ControlledBy: new You());
        var bare = new ObjectFilter(Types: CardType.Creature);

        // "Sacrifice a creature" is already a creature you control, CR 701.16a.
        Same(
            Card(new ActivatedAbility([new SacrificeCost(yours)], new Draw(new Fixed(1)))),
            Card(new ActivatedAbility([new SacrificeCost(bare)], new Draw(new Fixed(1)))));

        // "Destroy target creature you control" is not "destroy target creature".
        Differs(Spell(new Destroy(new Target(yours))), Spell(new Destroy(new Target(bare))));
    }

    [Fact]
    public void A_permanent_filter_drops_battlefield_and_a_search_filter_drops_library()
    {
        // Query reads a filter with no zone as the battlefield, so writing it out says nothing.
        Same(
            Spell(new Destroy(new Target(new ObjectFilter(Types: CardType.Creature, In: Zone.Battlefield)))),
            Spell(new Destroy(new Target(new ObjectFilter(Types: CardType.Creature)))));

        // A search says its zone in From; the filter repeating it says nothing.
        Same(
            Spell(new Search(new ObjectFilter(Types: CardType.Land, In: Zone.Library), Zone.Battlefield)),
            Spell(new Search(new ObjectFilter(Types: CardType.Land), Zone.Battlefield)));

        // Inside a zone-bound effect the battlefield is not the default, so nothing is stripped.
        Differs(
            Spell(new Discard(new Fixed(1), What: new ObjectFilter(Types: CardType.Creature, In: Zone.Battlefield))),
            Spell(new Discard(new Fixed(1), What: new ObjectFilter(Types: CardType.Creature))));
    }

    /// <summary>
    /// "Any target" has no spelling of its own: DealDamage carries To and ToPlayer separately and
    /// nothing in the vocabulary names the triple. So both readings are left exactly as written and
    /// the card stays disputed — the honest outcome until the vocabulary gains the record.
    /// </summary>
    [Fact]
    public void Any_target_is_left_as_written_because_the_vocabulary_has_no_spelling_for_it()
    {
        var anyTarget = new ObjectFilter(AnyTypes: CardType.Creature | CardType.Planeswalker | CardType.Battle);
        var spelled = Spell(new DealDamage(new Fixed(3), new Target(anyTarget), new TargetPlayer(PlayerFilter.Any)));
        var implied = Spell(new DealDamage(new Fixed(3), new Target(ObjectFilter.Any), new TargetPlayer(PlayerFilter.Any)));

        Differs(spelled, implied);

        var kept = (Target)((DealDamage)Do(Canonical.Of(spelled))).To!;
        Assert.Equal(CardType.Creature | CardType.Planeswalker | CardType.Battle, kept.Filter.AnyTypes);
    }

    [Fact]
    public void Keys_are_renumbered_by_order_of_first_definition_within_a_face()
    {
        static CardDefinition Coloured(string key) => Spell(new Sequence(
        [
            new ChooseValue(ChooseWhat.Color, key),
            new Destroy(new Each(new ObjectFilter(Types: CardType.Creature, Also: new IsChosenColor(key)))),
        ]));

        Same(Coloured("color"), Coloured("chosenColor"));
        Assert.Equal("k1", ((ChooseValue)((Sequence)Do(Canonical.Of(Coloured("chosenColor")))).Steps[0]).Key);

        static CardDefinition Twice(string first, string second) => Spell(new Sequence(
        [
            new ChooseValue(ChooseWhat.Color, first),
            new ChooseValue(ChooseWhat.CreatureType, second),
            new Destroy(new Each(new ObjectFilter(Also: new AllOf([new IsChosenColor(first), new IsChosenType(second)])))),
        ]));

        Same(Twice("color", "type"), Twice("chosenColor", "chosentype"));

        var renumbered = (Sequence)Do(Canonical.Of(Twice("chosenColor", "chosentype")));
        Assert.Equal("k1", ((ChooseValue)renumbered.Steps[0]).Key);
        Assert.Equal("k2", ((ChooseValue)renumbered.Steps[1]).Key);

        // Two keys are still two keys: nothing is merged.
        Differs(Twice("a", "b"), Coloured("a"));
    }

    [Fact]
    public void Redundant_constructs_collapse_to_the_one_spelling()
    {
        // A keyword granted as an ability is the keyword, CR 613.1f either way.
        Same(
            Card(new StaticAbility(new GrantAbility(new KeywordAbility(KeywordKind.Flying)), To: new Self())),
            Card(new StaticAbility(new GrantKeyword(KeywordKind.Flying), To: new Self())));

        // One instruction is not a sequence.
        Same(Spell(new Sequence([new Draw(new Fixed(1))])), Spell(new Draw(new Fixed(1))));

        // An empty filter says what leaving it out says.
        Same(
            Card(new StaticAbility(new GrantKeyword(KeywordKind.Flying, Of: ObjectFilter.Any), To: new Self())),
            Card(new StaticAbility(new GrantKeyword(KeywordKind.Flying), To: new Self())));

        // One selection is not a union, and one predicate is not a conjunction.
        Same(Spell(new Destroy(new Union([new Self()]))), Spell(new Destroy(new Self())));
        Same(
            Spell(new Destroy(new Each(new ObjectFilter(Also: new AllOf([new IsThisObject()]))))),
            Spell(new Destroy(new Each(new ObjectFilter(Also: new IsThisObject())))));
    }

    /// <summary>
    /// The differences the pilot listed that are real. Each of these two readings means something
    /// different, and canonicalisation must leave every one of them a difference.
    /// </summary>
    [Fact]
    public void Nothing_semantic_is_canonicalised_away()
    {
        var land = new ObjectFilter(Types: CardType.Land);
        Differs(
            Spell(new Search(land, Zone.Hand, UpTo: new Fixed(1))),
            Spell(new Search(land, Zone.Hand)));

        Differs(
            Spell(new AddMana(new ManaProduction(AnyColor: true))),
            Spell(new AddMana(new ManaProduction(AnyColor: false))));

        Differs(
            Card(new ReplacementAbility(new WouldDie(ObjectFilter.Creature), new ReplaceWith(new Exile(new Triggering())), SelfOnly: true)),
            Card(new ReplacementAbility(new WouldDie(ObjectFilter.Creature), new ReplaceWith(new Exile(new Triggering())), SelfOnly: false)));

        Differs(
            Spell(new If(new Compare(new Fixed(1), ComparisonOp.Ge, new Fixed(2)), new Draw(new Fixed(1)))),
            Spell(new If(new Controls(new You(), ObjectFilter.Creature, new Comparison(ComparisonOp.Ge, new Fixed(2))), new Draw(new Fixed(1)))));
    }

    [Fact]
    public void Canonicalising_a_canonical_reading_changes_nothing()
    {
        var written = Spell(new Sequence([new Draw(new Fixed(1), new You())]));
        var once = Canonical.Of(written);
        var twice = Canonical.Of(once);
        Assert.Equal(CardFileSerializer.WriteDefinition(once), CardFileSerializer.WriteDefinition(twice));
        Assert.IsType<Draw>(Do(once));
    }

    [Fact]
    public void Two_readings_differing_only_in_canonical_ways_agree_and_the_file_is_canonical()
    {
        var verbose = Spell(new Sequence([new Draw(new Fixed(1), new You())]));
        var terse = Spell(new Draw(new Fixed(1)));

        var result = AgreeStage.Agree(new AgreeRequest(OracleId, "Canonical Scribe", "sha256:cccc", "prompt-v1",
            [new ModelReading("model-a", verbose), new ModelReading("model-b", terse)]));

        Assert.Equal(ImplementationStatus.Translated, result.Status);
        Assert.True(result.Agreed);
        Assert.Null(result.Difference);

        var written = (Draw)((SpellAbility)result.File!.Definition.Faces[0].AbilityList[0]).Do;
        Assert.Null(written.Who);
    }

    [Fact]
    public void A_disputed_file_keeps_both_readings_in_their_canonical_spelling()
    {
        var result = AgreeStage.Agree(new AgreeRequest(OracleId, "Canonical Scribe", "sha256:cccc", "prompt-v1",
            [
                new ModelReading("model-a", Spell(new Draw(new Fixed(1), new You()))),
                new ModelReading("model-b", Spell(new Mill(new Fixed(1), new You()))),
            ]));

        Assert.Equal(ImplementationStatus.Disputed, result.Status);
        Assert.Equal("faces[0].abilities[0].do.kind: \"Draw\" vs \"Mill\"", result.Difference);
        Assert.All(result.File!.Candidates!, c => Assert.Null(WhoOf(c.Definition)));
    }

    private static PlayerRef? WhoOf(CardDefinition definition) => ((SpellAbility)definition.Faces[0].AbilityList[0]).Do switch
    {
        Draw d => d.Who,
        Mill m => m.Who,
        _ => throw new InvalidOperationException("This fixture only writes Draw and Mill."),
    };

    [Fact]
    public void The_report_groups_the_remaining_first_differences_by_property_with_counts()
    {
        var report = AgreeReport.Of(
        [
            Judged(Spell(new Draw(new Fixed(1))), Spell(new Mill(new Fixed(1)))),
            Judged(Spell(new Draw(new Fixed(2))), Spell(new GainLife(new Fixed(2)))),
            Judged(Spell(new Draw(new Fixed(1))), Spell(new Draw(new Fixed(2)))),
            Judged(Spell(new Draw(new Fixed(1), new You())), Spell(new Draw(new Fixed(1)))),
        ]);

        Assert.Equal(4, report.Cards);
        Assert.Equal(1, report.Agreed);
        Assert.Equal(3, report.Disputed);
        Assert.Equal(0, report.Untranslated);

        Assert.Equal(["kind", "n"], report.Differences.Select(g => g.Property));
        Assert.Equal([2, 1], report.Differences.Select(g => g.Cards));
        Assert.Contains("vs", report.Differences[0].Example, StringComparison.Ordinal);
        Assert.Contains("kind: 2", report.Text(), StringComparison.Ordinal);
    }

    /// <summary>The pilot's own first differences, grouped the way the next pass needs to read them.</summary>
    [Fact]
    public void The_property_of_a_difference_is_its_last_path_segment_without_the_index()
    {
        Assert.Equal("who", AgreeReport.PropertyOf("faces[0].abilities[1].do.who: {\"kind\":\"You\"} vs absent"));
        Assert.Equal("abilities", AgreeReport.PropertyOf("faces[0].abilities: 2 items vs 1 items"));
        Assert.Equal("in", AgreeReport.PropertyOf("faces[0].abilities[0].do.steps[0].what.filter.in: \"Battlefield\" vs absent"));
        Assert.Equal("kind", AgreeReport.PropertyOf("faces[0].abilities[0].effect.kind: \"GrantKeyword\" vs \"GrantAbility\""));
        Assert.Equal("$", AgreeReport.PropertyOf("$: 1 items vs 2 items"));
    }

    private static AgreeResult Judged(CardDefinition left, CardDefinition right) =>
        AgreeStage.Agree(new AgreeRequest(OracleId, "Canonical Scribe", "sha256:cccc", "prompt-v1",
            [new ModelReading("model-a", left), new ModelReading("model-b", right)]));
}
