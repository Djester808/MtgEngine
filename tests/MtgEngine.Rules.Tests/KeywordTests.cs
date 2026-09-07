using MtgEngine.Cards;
using MtgEngine.Rules.Keywords;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The CR 702 keyword table and the expander that turns keywords into the abilities they stand
/// for, CR 702.1. Every face here is made up in this file: no card name and no card file takes
/// part, so the table is tested against the rules and not against a printing.
/// </summary>
public sealed class KeywordTests
{
    // --- the table covers the vocabulary ---------------------------------------------------

    /// <summary>
    /// CR 702.1 — every keyword the rulebook defines needs an entry saying which subsystem gives
    /// it meaning. The enum is generated from the rules file, so a new release that adds a
    /// keyword fails here rather than silently arriving with no treatment.
    /// </summary>
    [Fact]
    public void Every_keyword_kind_has_a_semantics_entry()
    {
        var missing = Enum.GetValues<KeywordKind>()
            .Where(k => !KeywordSemantics.All.Any(s => s.Keyword == k))
            .ToList();

        Assert.Empty(missing);
        Assert.Equal(Enum.GetValues<KeywordKind>().Length, KeywordSemantics.All.Count);
    }

    /// <summary>Every entry is reachable by lookup, and no two families claim the same keyword.</summary>
    [Fact]
    public void Every_keyword_kind_resolves_to_exactly_one_treatment()
    {
        foreach (var kind in Enum.GetValues<KeywordKind>())
        {
            var semantic = KeywordSemantics.Of(kind);
            Assert.Equal(kind, semantic.Keyword);
            Assert.False(string.IsNullOrWhiteSpace(semantic.Note));
        }
    }

    /// <summary>
    /// CR 702 — each entry cites its rule. The number is read off the generated enum's own rule
    /// reference, so the table cannot drift from the rulebook the vocabulary was built from.
    /// </summary>
    [Fact]
    public void Every_keyword_cites_a_rule_in_the_702_series()
    {
        foreach (var kind in Enum.GetValues<KeywordKind>())
            Assert.StartsWith("702", KeywordSemantics.Of(kind).Rule, StringComparison.Ordinal);
    }

    /// <summary>Only the evasion family is a characteristic combat and the layers read directly, CR 613.1f.</summary>
    [Fact]
    public void The_evasion_family_is_the_characteristic_family()
    {
        Assert.True(KeywordSemantics.IsCharacteristic(KeywordKind.Flying));
        Assert.True(KeywordSemantics.IsCharacteristic(KeywordKind.Trample));
        Assert.False(KeywordSemantics.IsCharacteristic(KeywordKind.Flashback));
        Assert.False(KeywordSemantics.IsCharacteristic(KeywordKind.Prowess));
    }

    /// <summary>CR 118.9 — a cost keyword says which cost it is and which zone it is cast from.</summary>
    [Fact]
    public void A_cost_keyword_names_its_cost_shape_and_its_zone()
    {
        var flashback = KeywordSemantics.Of(KeywordKind.Flashback);
        Assert.Equal(KeywordTreatment.AlternativeOrOptionalCost, flashback.Treatment);
        Assert.Equal(CostShape.Alternative, flashback.Cost);
        Assert.Equal(Zone.Graveyard, flashback.CastFrom);

        var kicker = KeywordSemantics.Of(KeywordKind.Kicker);
        Assert.Equal(CostShape.Additional, kicker.Cost);

        var flash = KeywordSemantics.Of(KeywordKind.Flash);
        Assert.Equal(CostShape.TimingPermission, flash.Cost);
    }

    // --- expansion -------------------------------------------------------------------------

    /// <summary>
    /// CR 702.108a — prowess is a triggered ability: whenever you cast a noncreature spell, this
    /// creature gets +1/+1 until end of turn.
    /// </summary>
    [Fact]
    public void Prowess_expands_to_a_spell_cast_trigger_that_modifies_power_and_toughness()
    {
        var face = FaceWith(new KeywordAbility(KeywordKind.Prowess));

        var expanded = KeywordExpander.Expand(face);

        var trigger = Assert.IsType<TriggeredAbility>(Assert.Single(expanded.AbilityList));
        var cast = Assert.IsType<SpellCast>(trigger.When);
        Assert.True(cast.Spell.Spell);
        Assert.Equal(CardType.Creature, cast.Spell.NotTypes);
        Assert.IsType<You>(cast.By);

        var apply = Assert.IsType<Apply>(trigger.Do);
        var pt = Assert.IsType<ModifyPt>(apply.What);
        Assert.Equal(new Fixed(1), pt.Power);
        Assert.Equal(new Fixed(1), pt.Toughness);
        Assert.IsType<Self>(apply.To);
        Assert.IsType<UntilEndOfTurn>(apply.Until);
    }

    /// <summary>
    /// CR 702.29a — cycling is an activated ability that functions only from hand: pay the
    /// printed cost, discard this card, draw a card.
    /// </summary>
    [Fact]
    public void Cycling_expands_to_an_activated_ability_from_hand_with_the_printed_cost_and_a_draw()
    {
        var face = FaceWith(new KeywordAbility(KeywordKind.Cycling, Cost: "{2}"));

        var expanded = KeywordExpander.Expand(face);

        var activated = Assert.IsType<ActivatedAbility>(Assert.Single(expanded.AbilityList));
        Assert.Equal(Zone.Hand, activated.Where);
        Assert.Equal(Timing.Any, activated.Timing);
        Assert.Contains(new ManaCost("{2}"), activated.Costs);
        Assert.Contains(activated.Costs, c => c is DiscardCost);

        var draw = Assert.IsType<Draw>(activated.Do);
        Assert.Equal(new Fixed(1), draw.N);
    }

    /// <summary>CR 702.90a — exalted pumps the creature that attacked alone, not the source.</summary>
    [Fact]
    public void Exalted_expands_to_an_attacks_alone_trigger_on_the_triggering_creature()
    {
        var expanded = KeywordExpander.Expand(FaceWith(new KeywordAbility(KeywordKind.Exalted)));

        var trigger = Assert.IsType<TriggeredAbility>(Assert.Single(expanded.AbilityList));
        var alone = Assert.IsType<AttacksAlone>(trigger.When);
        Assert.Equal(CardType.Creature, alone.Who.Types);
        Assert.IsType<You>(alone.Who.ControlledBy);
        Assert.IsType<Triggering>(Assert.IsType<Apply>(trigger.Do).To);
    }

    /// <summary>CR 702.84a — unearth is an activated ability from the graveyard, at sorcery speed.</summary>
    [Fact]
    public void Unearth_expands_to_a_sorcery_speed_activated_ability_from_the_graveyard()
    {
        var expanded = KeywordExpander.Expand(FaceWith(new KeywordAbility(KeywordKind.Unearth, Cost: "{1}{B}")));

        var activated = Assert.IsType<ActivatedAbility>(Assert.Single(expanded.AbilityList));
        Assert.Equal(Zone.Graveyard, activated.Where);
        Assert.Equal(Timing.SorcerySpeed, activated.Timing);
        Assert.Contains(new ManaCost("{1}{B}"), activated.Costs);

        var steps = Assert.IsType<Sequence>(activated.Do).Steps;
        Assert.Equal(Zone.Battlefield, Assert.IsType<Move>(steps[0]).To);
        Assert.Equal(KeywordKind.Haste, Assert.IsType<GrantKeyword>(Assert.IsType<Apply>(steps[1]).What).Keyword);
        Assert.Equal(Zone.Exile, Assert.IsType<Move>(Assert.IsType<DelayedTrigger>(steps[2]).Do).To);
    }

    /// <summary>CR 702.113a — devoid is a characteristic-defining ability that makes the object colourless.</summary>
    [Fact]
    public void Devoid_expands_to_a_characteristic_defining_static_that_sets_no_colour()
    {
        var expanded = KeywordExpander.Expand(FaceWith(new KeywordAbility(KeywordKind.Devoid)));

        var stat = Assert.IsType<StaticAbility>(Assert.Single(expanded.AbilityList));
        Assert.True(stat.CharacteristicDefining);
        Assert.Equal(Color.Colorless, Assert.IsType<SetColors>(stat.Effect).Colors);
        Assert.IsType<Self>(stat.To);
    }

    // --- what is deliberately left alone ---------------------------------------------------

    /// <summary>
    /// CR 509.1b and CR 613.1f — combat and the layers read flying off characteristics, so the
    /// expander must leave it printed. Rewriting it would give the rule two writers.
    /// </summary>
    [Fact]
    public void An_evasion_keyword_is_left_in_place_for_combat_and_the_layers()
    {
        var flying = new KeywordAbility(KeywordKind.Flying);

        var expanded = KeywordExpander.Expand(FaceWith(flying));

        Assert.Same(flying, Assert.Single(expanded.AbilityList));
    }

    /// <summary>CR 118.9 — an alternative cost is a way of paying, not an ability that resolves.</summary>
    [Fact]
    public void A_cost_keyword_is_left_in_place_for_the_casting_system()
    {
        var flashback = new KeywordAbility(KeywordKind.Flashback, Cost: "{3}{R}");

        var expanded = KeywordExpander.Expand(FaceWith(flashback));

        Assert.Same(flashback, Assert.Single(expanded.AbilityList));
    }

    /// <summary>A face whose keywords all stay in place is returned untouched, allocations and all.</summary>
    [Fact]
    public void A_face_that_needs_no_expansion_is_returned_unchanged()
    {
        var face = FaceWith(new KeywordAbility(KeywordKind.Flying), new KeywordAbility(KeywordKind.Trample));

        Assert.Same(face, KeywordExpander.Expand(face));
    }

    // --- nothing is dropped ----------------------------------------------------------------

    /// <summary>
    /// A keyword with no engine treatment becomes an Unsupported marker naming it, never
    /// nothing: the pipeline ranks the primitive backlog by counting these, and a silently
    /// dropped keyword would be a card that plays wrongly rather than a card that is refused.
    /// </summary>
    [Fact]
    public void An_unsupported_keyword_becomes_a_marker_rather_than_disappearing()
    {
        Assert.Equal(KeywordTreatment.Unsupported, KeywordSemantics.Of(KeywordKind.Banding).Treatment);
        var banding = new KeywordAbility(KeywordKind.Banding) { Text = "Banding" };

        var expanded = KeywordExpander.Expand(FaceWith(banding));

        var spell = Assert.IsType<SpellAbility>(Assert.Single(expanded.AbilityList));
        var unsupported = Assert.IsType<Unsupported>(spell.Do);
        Assert.Contains("Banding", unsupported.Reason, StringComparison.Ordinal);
        Assert.Contains(KeywordSemantics.Of(KeywordKind.Banding).Rule, unsupported.Reason, StringComparison.Ordinal);
        Assert.Equal("Banding", unsupported.Text);
    }

    /// <summary>
    /// A keyword whose family says it expands but whose expansion needs a primitive the
    /// vocabulary lacks is marked too, and the marker names the missing primitive so triage can
    /// rank it. Undying's trigger is about this object and ObjectFilter cannot say "this".
    /// </summary>
    [Fact]
    public void An_expanding_keyword_with_no_arm_yet_names_the_missing_primitive()
    {
        var semantics = KeywordSemantics.Of(KeywordKind.Undying);
        Assert.True(semantics.Expands);

        var expanded = KeywordExpander.Expand(FaceWith(new KeywordAbility(KeywordKind.Undying)));

        var unsupported = Assert.IsType<Unsupported>(Assert.IsType<SpellAbility>(Assert.Single(expanded.AbilityList)).Do);
        Assert.Contains("Undying", unsupported.Reason, StringComparison.Ordinal);
        Assert.Contains("this", unsupported.Reason, StringComparison.Ordinal);
    }

    /// <summary>Non-keyword abilities pass through untouched and keep their order.</summary>
    [Fact]
    public void Other_abilities_are_carried_through_in_order()
    {
        var printed = new SpellAbility(new Draw(new Fixed(2)));
        var face = FaceWith(printed, new KeywordAbility(KeywordKind.Prowess));

        var expanded = KeywordExpander.Expand(face);

        Assert.Equal(2, expanded.AbilityList.Count);
        Assert.Same(printed, expanded.AbilityList[0]);
        Assert.IsType<TriggeredAbility>(expanded.AbilityList[1]);
    }

    private static Face FaceWith(params Ability[] abilities) =>
        new(
            "Keyword Test Subject",
            FaceRole.Main,
            new TypeLine(CardType.Creature, Subtypes: ["Beast"]),
            ManaCost: "{1}{G}",
            Power: new PrintedStat("2"),
            Toughness: new PrintedStat("2"),
            Abilities: abilities);
}
