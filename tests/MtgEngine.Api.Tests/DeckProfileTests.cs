using MtgEngine.Api.Services;
using MtgEngine.Domain.Enums;

namespace MtgEngine.Api.Tests;

/// <summary>
/// The deck profile is what lets a card be judged on what the <em>deck</em> does rather than
/// only on the commander's text. A land producing mana per creature is ordinary in a
/// twelve-creature deck and an engine in a token deck, and nothing in the commander's rules
/// text can tell those apart.
/// </summary>
public class DeckProfileTests
{
    private static DeckProfile.ProfileCard Card(
        string name = "c",
        int cmc = 2,
        string mana = "{1}{G}",
        string? text = null,
        CardType types = CardType.Creature,
        string[]? subtypes = null) =>
        new(name, cmc, mana, text, types, subtypes ?? [], []);

    private static IEnumerable<DeckProfile.ProfileCard> Many(int n, Func<int, DeckProfile.ProfileCard> make) =>
        Enumerable.Range(0, n).Select(make);

    // ---- Composition -----------------------------------------------------

    [Fact]
    public void An_empty_deck_profiles_as_empty()
    {
        var p = DeckProfile.Build([]);

        Assert.Equal(0, p.TotalCards);
        Assert.True(p.IsTooSmallForGapAnalysis);
        Assert.Empty(p.Gaps);
    }

    [Fact]
    public void Lands_and_nonlands_are_counted_separately()
    {
        var p = DeckProfile.Build(
            Many(10, i => Card($"land{i}", 0, "", types: CardType.Land))
            .Concat(Many(5, i => Card($"spell{i}"))));

        Assert.Equal(15, p.TotalCards);
        Assert.Equal(10, p.Lands);
        Assert.Equal(5, p.NonLands);
    }

    /// <summary>
    /// Thirty-seven zero-cost lands would drag the average far below what the deck actually
    /// costs to operate, so the curve is measured over nonlands only.
    /// </summary>
    [Fact]
    public void The_curve_excludes_lands()
    {
        var p = DeckProfile.Build(
            Many(10, i => Card($"land{i}", 0, "", types: CardType.Land))
            .Concat(Many(10, i => Card($"spell{i}", 4))));

        Assert.Equal(4, p.AverageManaValue);
    }

    // ---- Colour ----------------------------------------------------------

    [Fact]
    public void Coloured_pips_are_counted_from_mana_costs()
    {
        var p = DeckProfile.Build([
            Card(mana: "{1}{G}"),
            Card(mana: "{B}{G}"),
            Card(mana: "{2}"),
        ]);

        Assert.Equal(2, p.ColourPips[ManaColor.Green]);
        Assert.Equal(1, p.ColourPips[ManaColor.Black]);
        Assert.False(p.ColourPips.ContainsKey(ManaColor.White));
    }

    /// <summary>Doctrine §3.2: a dual is a source for both its colours, which is the point of it.</summary>
    [Fact]
    public void A_dual_land_counts_as_a_source_for_both_colours()
    {
        var p = DeckProfile.Build([
            Card("dual", 0, "", types: CardType.Land, subtypes: ["Swamp", "Forest"]),
        ]);

        Assert.Equal(1, p.ColourSources[ManaColor.Black]);
        Assert.Equal(1, p.ColourSources[ManaColor.Green]);
    }

    [Fact]
    public void A_rock_adding_any_colour_is_a_source_for_every_colour()
    {
        var p = DeckProfile.Build([
            Card("rock", 2, "{2}", "{T}: Add one mana of any color.", CardType.Artifact),
        ]);

        Assert.Equal(5, p.ColourSources.Count);
    }

    // ---- Archetype signals, doctrine §7 ----------------------------------

    [Fact]
    public void A_creature_dense_deck_registers_as_creature_centric()
    {
        var p = DeckProfile.Build(Many(30, i => Card($"creature{i}")));

        Assert.Contains(p.Archetypes, a => a.Contains("creature-centric"));
    }

    [Fact]
    public void A_spell_heavy_deck_does_not()
    {
        var p = DeckProfile.Build(
            Many(30, i => Card($"spell{i}", 3, "{2}{B}", "Draw a card.", CardType.Instant)));

        Assert.DoesNotContain(p.Archetypes, a => a.Contains("creature"));
        Assert.Contains(p.Archetypes, a => a.Contains("spellslinger"));
    }

    [Fact]
    public void Token_production_registers_once_enough_cards_make_them()
    {
        var p = DeckProfile.Build(
            Many(8, i => Card($"maker{i}", 3, "{2}{G}", "Create a 2/2 green Wolf creature token.")));

        Assert.Contains(p.Archetypes, a => a.StartsWith("tokens"));
    }

    [Fact]
    public void A_single_token_maker_does_not_make_it_a_token_deck()
    {
        var p = DeckProfile.Build(
            Many(1, i => Card("maker", 3, "{2}{G}", "Create a 2/2 green Wolf creature token."))
            .Concat(Many(29, i => Card($"other{i}"))));

        Assert.DoesNotContain(p.Archetypes, a => a.StartsWith("tokens"));
    }

    [Fact]
    public void A_dense_creature_type_registers_as_tribal()
    {
        var p = DeckProfile.Build(Many(15, i => Card($"wolf{i}", subtypes: ["Wolf"])));

        Assert.Contains(p.Archetypes, a => a.Contains("Wolf tribal"));
    }

    [Fact]
    public void A_deck_can_register_several_archetypes_at_once()
    {
        var p = DeckProfile.Build(Many(20, i =>
            Card($"wolf{i}", 3, "{2}{G}", "Create a 2/2 green Wolf creature token.", subtypes: ["Wolf"])));

        Assert.Contains(p.Archetypes, a => a.Contains("creature"));
        Assert.Contains(p.Archetypes, a => a.StartsWith("tokens"));
        Assert.Contains(p.Archetypes, a => a.Contains("Wolf tribal"));
    }

    // ---- Gaps, doctrine §2 -----------------------------------------------

    /// <summary>
    /// Doctrine §10.2. Gap analysis on a near-empty deck is noise — "you are short 36 lands"
    /// is true and useless when the deck has four cards in it.
    /// </summary>
    [Fact]
    public void Gaps_are_not_reported_for_a_deck_too_small_to_analyse()
    {
        var p = DeckProfile.Build(Many(5, i => Card($"c{i}")));

        Assert.True(p.IsTooSmallForGapAnalysis);
        Assert.Empty(p.Gaps);
    }

    [Fact]
    public void A_deck_short_on_lands_is_told_so()
    {
        var p = DeckProfile.Build(
            Many(10, i => Card($"land{i}", 0, "", types: CardType.Land))
            .Concat(Many(60, i => Card($"spell{i}"))));

        Assert.False(p.IsTooSmallForGapAnalysis);
        Assert.Contains(p.Gaps, g => g.StartsWith("lands:"));
    }

    [Fact]
    public void Total_mana_sources_are_checked_against_the_combined_target()
    {
        // 30 lands and no ramp: land count alone is close, but §2.1 wants lands + ramp ≈ 45-48.
        var p = DeckProfile.Build(
            Many(30, i => Card($"land{i}", 0, "", types: CardType.Land))
            .Concat(Many(40, i => Card($"spell{i}"))));

        Assert.Contains(p.Gaps, g => g.Contains("total mana sources"));
    }

    // ---- Cache signature --------------------------------------------------

    /// <summary>
    /// Deck-aware scores are cached against this, so two decks that would genuinely get the
    /// same advice must produce the same signature — otherwise the cache never hits.
    /// </summary>
    [Fact]
    public void Decks_with_the_same_shape_share_a_signature()
    {
        var a = DeckProfile.Build(Many(30, i => Card($"alpha{i}")));
        var b = DeckProfile.Build(Many(30, i => Card($"beta{i}")));

        Assert.Equal(a.GapSignature(), b.GapSignature());
    }

    [Fact]
    public void Decks_with_different_shapes_do_not()
    {
        var creatures = DeckProfile.Build(Many(30, i => Card($"c{i}")));
        var spells = DeckProfile.Build(
            Many(30, i => Card($"s{i}", 3, "{2}{B}", "Draw a card.", CardType.Instant)));

        Assert.NotEqual(creatures.GapSignature(), spells.GapSignature());
    }

    [Fact]
    public void An_empty_deck_has_a_stable_signature() =>
        Assert.Equal("empty", DeckProfile.Build([]).GapSignature());

    // ---- Rendering ---------------------------------------------------------

    [Fact]
    public void Describe_is_empty_for_an_empty_deck() =>
        Assert.Equal(string.Empty, DeckProfile.Build([]).Describe());

    [Fact]
    public void Describe_reports_the_signals_the_prompt_needs()
    {
        var text = DeckProfile.Build(Many(30, i => Card($"wolf{i}", subtypes: ["Wolf"]))).Describe();

        Assert.Contains("Cards: 30", text);
        Assert.Contains("Archetype signals:", text);
        Assert.Contains("Wolf tribal", text);
    }
}
