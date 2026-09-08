using MtgEngine.Cards;
using MtgEngine.Rules.Keywords;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Wave 29 — storm, CR 702.40a: the keyword is a triggered ability that functions on the stack,
/// so it expands into the cast trigger it stands for instead of leaving an Unsupported marker.
/// The face is invented here; no card file takes part.
/// </summary>
public sealed class KeywordStormTests
{
    [Fact]
    [RuleRef("702.40")]
    public void Storm_is_a_triggered_ability_expansion_and_not_unsupported()
    {
        // CR 702.40a: "storm is a triggered ability that functions on the stack." Its entry moving
        // out of the unsupported list is what stops the expander leaving a marker for it.
        var semantics = KeywordSemantics.Of(KeywordKind.Storm);

        Assert.Equal(KeywordTreatment.TriggeredAbilityExpansion, semantics.Treatment);
        Assert.True(semantics.Expands);
        Assert.Equal("702.40", semantics.Rule);
    }

    [Fact]
    [RuleRef("702.40a")]
    public void Storm_expands_into_a_cast_trigger_that_copies_this_spell()
    {
        // CR 702.40a: "When you cast this spell, copy it …" — a SpellCast trigger whose filter is
        // this object, and whose effect copies the triggering spell (CR 707.10).
        var expanded = Assert.Single(KeywordExpander.ExpandKeyword(new KeywordAbility(KeywordKind.Storm)));

        var triggered = Assert.IsType<TriggeredAbility>(expanded);
        var cast = Assert.IsType<SpellCast>(triggered.When);
        Assert.IsType<You>(cast.By);
        Assert.True(cast.Spell.Spell);
        Assert.IsType<IsThisObject>(cast.Spell.Also);
        var copy = Assert.IsType<CopySpell>(triggered.Do);
        Assert.IsType<Triggering>(copy.What);
        Assert.True(copy.MayChooseNewTargets);
    }

    [Fact]
    [RuleRef("702.40a")]
    public void Storm_copies_the_spell_once_for_each_other_spell_cast_this_turn()
    {
        // CR 702.40a counts "each other spell that was cast before it this turn" — every player's,
        // not only yours, and not this one: the spell is already in the turn's record when the
        // trigger resolves, so the count is the turn's total less one.
        var expanded = Assert.Single(KeywordExpander.ExpandKeyword(new KeywordAbility(KeywordKind.Storm)));

        var copy = Assert.IsType<CopySpell>(Assert.IsType<TriggeredAbility>(expanded).Do);
        var less = Assert.IsType<Difference>(copy.N);
        var cast = Assert.IsType<SpellsCastThisTurn>(less.Left);
        Assert.IsType<EachPlayer>(cast.By);
        Assert.Null(cast.Matching);
        Assert.Equal(1, Assert.IsType<Fixed>(less.Right).N);
    }

    [Fact]
    [RuleRef("702.40b")]
    public void Two_instances_of_storm_expand_to_two_triggers()
    {
        // CR 702.40b: "if a spell has multiple instances of storm, each triggers separately." Two
        // keyword records expand to two abilities, which is the whole of that rule here.
        var face = new Face(
            "Keyword Tests Surge",
            FaceRole.Main,
            new TypeLine(CardType.Instant),
            "{1}{R}",
            Abilities: [new KeywordAbility(KeywordKind.Storm), new KeywordAbility(KeywordKind.Storm)]);

        var expanded = KeywordExpander.Expand(face);

        Assert.Equal(2, expanded.AbilityList.OfType<TriggeredAbility>().Count());
        Assert.DoesNotContain(expanded.AbilityList.OfType<SpellAbility>(), a => a.Do is Unsupported);
    }
}
