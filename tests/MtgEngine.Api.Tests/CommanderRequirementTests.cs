using MtgEngine.Api.Services;
using MtgEngine.Domain.Enums;

namespace MtgEngine.Api.Tests;

/// <summary>
/// The fact sheet compares card fields against the commander's requirements. It does no text
/// parsing at all — reading the commander's sentence is <see cref="ICommanderAnalysis"/>'s
/// job, and reading a card's own text is the doctrine's (§0.2).
/// <para>
/// That split exists because parsing prose in C# failed twice: matching "enters tapped"
/// misread every land with a conditional clause, and matching "power N or greater" missed
/// the ~180 printings phrased "or more" or "greater than" — silently. What remains here is
/// arithmetic, which is the half the model gets wrong: it once called a 3/2 an enabler for a
/// power-4 trigger.
/// </para>
/// </summary>
public class CommanderRequirementTests
{
    private static readonly ManaColor[] Golgari = [ManaColor.Black, ManaColor.Green];

    /// <summary>The Chief Warg's requirement, as the analysis pass would return it.</summary>
    private static CommanderRequirements Warg(
        ManaColor[]? colours = null, string[]? keywords = null) =>
        new([new Threshold("power", 4, OrGreater: true)], ["Wolf"], keywords ?? [], colours ?? []);

    private static CardFactSheet.FactCard Card(
        string name,
        int? power = null,
        int? toughness = null,
        int cmc = 0,
        CardType types = CardType.Creature,
        string[]? subtypes = null,
        ManaColor[]? colours = null,
        string[]? keywords = null,
        bool gameChanger = false) =>
        new(Guid.NewGuid().ToString(), name, "{1}{G}", cmc, null, types.ToString(),
            types, subtypes ?? [], power, toughness, colours ?? [], keywords ?? [], gameChanger);

    private static string Facts(
        CardFactSheet.FactCard card, CommanderRequirements req, DeckProfile? profile = null) =>
        CardFactSheet.For(card, req, profile);

    // ---- Threshold comparison -------------------------------------------

    [Fact]
    public void A_creature_meeting_the_threshold_is_told_so()
    {
        var facts = Facts(Card("four-power wolf", 4, 3, 4, subtypes: ["Wolf"]), Warg());

        Assert.Contains("MEETS", facts);
        Assert.Contains("power 4", facts);
    }

    [Fact]
    public void A_creature_below_the_threshold_is_told_so() =>
        Assert.Contains("does NOT meet",
            Facts(Card("three-power wolf", 3, 2, 2, subtypes: ["Wolf"]), Warg()));

    /// <summary>
    /// An artifact has no power. "power n/a does not meet the requirement" is noise at best,
    /// and invites the model to hold it against the card.
    /// </summary>
    [Fact]
    public void A_card_with_no_power_is_told_nothing_about_a_power_threshold() =>
        Assert.DoesNotContain("power",
            Facts(Card("mana rock", cmc: 1, types: CardType.Artifact), Warg()),
            StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void An_or_less_threshold_is_met_by_a_small_creature_not_a_large_one()
    {
        var req = new CommanderRequirements(
            [new Threshold("power", 2, OrGreater: false)], [], [], []);

        Assert.Contains("MEETS", Facts(Card("small", 1, 1), req));
        Assert.Contains("does NOT meet", Facts(Card("large", 7, 7), req));
    }

    [Theory]
    [InlineData("toughness", 5, 5, true)]
    [InlineData("toughness", 5, 4, false)]
    [InlineData("mana value", 3, 2, true)]
    public void Thresholds_apply_to_whichever_field_they_name(
        string attribute, int bar, int actual, bool shouldMeet)
    {
        // "mana value 3 or less" is an or-less bar; the others here are or-greater.
        var orGreater = attribute != "mana value";
        var req = new CommanderRequirements([new Threshold(attribute, bar, orGreater)], [], [], []);

        var card = attribute switch
        {
            "toughness" => Card("c", 1, actual),
            "mana value" => Card("c", 1, 1, actual),
            _ => Card("c", actual, 1),
        };

        Assert.Contains(shouldMeet ? "MEETS" : "does NOT meet", Facts(card, req));
    }

    /// <summary>
    /// The bar arrives already normalised, so "power greater than 3" and "power 4 or more"
    /// are indistinguishable here by design — the phrasing never reaches this code.
    /// </summary>
    [Fact]
    public void A_normalised_threshold_reads_back_inclusively() =>
        Assert.Equal("power 4 or greater", new Threshold("power", 4, true).Describe());

    // ---- Tribes ----------------------------------------------------------

    [Fact]
    public void A_card_of_a_tribe_the_deck_cares_about_is_credited() =>
        Assert.Contains("a creature type this deck cares about",
            Facts(Card("some wolf", 3, 1, 3, subtypes: ["Wolf"]), Warg()));

    [Fact]
    public void A_card_of_an_unrelated_type_is_not() =>
        Assert.DoesNotContain("creature type this deck cares about",
            Facts(Card("a rabbit", 3, 1, 3, subtypes: ["Rabbit"]), Warg()));

    // ---- Colour identity, doctrine §1.2 ----------------------------------

    [Fact]
    public void A_colourless_nonland_is_reported_as_legal_anywhere() =>
        Assert.Contains("legal in any deck",
            Facts(Card("mana rock", cmc: 1, types: CardType.Artifact), Warg(Golgari)));

    /// <summary>
    /// The pool already filters on identity, so this should never reach a prompt. It is
    /// stated rather than dropped so a filtering bug shows up instead of passing silently.
    /// </summary>
    [Fact]
    public void A_card_outside_the_commanders_colours_is_flagged_illegal() =>
        Assert.Contains("ILLEGAL in this deck",
            Facts(Card("white wipe", cmc: 4, types: CardType.Sorcery, colours: [ManaColor.White]),
                Warg(Golgari)));

    [Fact]
    public void A_card_inside_the_commanders_colours_is_not_flagged() =>
        Assert.DoesNotContain("ILLEGAL",
            Facts(Card("golgari removal", cmc: 3, types: CardType.Instant, colours: Golgari),
                Warg(Golgari)));

    /// <summary>
    /// Doctrine §3.2. A land's colour identity already is the set of colours it can produce —
    /// mana symbols in its text and basic land types both feed it — so fixing is answered
    /// from a field, with no "Add {B}" clause parsed anywhere.
    /// </summary>
    [Fact]
    public void A_land_covering_both_commander_colours_is_credited_as_a_source_for_each()
    {
        var facts = Facts(
            Card("a dual", types: CardType.Land, subtypes: ["Swamp", "Forest"], colours: Golgari),
            Warg(Golgari));

        Assert.Contains("ALL 2 of the commander's colours", facts);
        Assert.Contains("a source for each", facts);
    }

    [Fact]
    public void A_land_covering_one_commander_colour_is_credited_for_that_one()
    {
        var facts = Facts(
            Card("a mono land", types: CardType.Land, subtypes: ["Swamp"], colours: [ManaColor.Black]),
            Warg(Golgari));

        Assert.Contains("covers Black", facts);
        Assert.DoesNotContain("ALL", facts);
    }

    [Fact]
    public void A_colourless_land_is_reported_as_producing_no_coloured_mana() =>
        Assert.Contains("produces no coloured mana",
            Facts(Card("utility land", types: CardType.Land), Warg(Golgari)));

    // ---- Keywords, mana value, Game Changers -----------------------------

    [Fact]
    public void A_keyword_the_commander_names_is_reported() =>
        Assert.Contains("named in the commander's text",
            Facts(Card("menacing wolf", 2, 2, 3, keywords: ["Menace"], subtypes: ["Wolf"]),
                Warg(Golgari, ["Menace"])));

    [Fact]
    public void A_keyword_the_commander_does_not_name_is_not_reported() =>
        Assert.DoesNotContain("named in the commander's text",
            Facts(Card("flying wolf", 2, 2, 3, keywords: ["Flying"], subtypes: ["Wolf"]),
                Warg(Golgari, ["Menace"])));

    [Fact]
    public void Mana_value_is_always_stated() =>
        Assert.Contains("mana value 5", Facts(Card("something", cmc: 5), Warg(Golgari)));

    [Fact]
    public void A_game_changer_is_reported_as_one() =>
        Assert.Contains("Game Changers list",
            Facts(Card("a strong land", types: CardType.Land, gameChanger: true), Warg(Golgari)));

    // ---- The architectural guard -----------------------------------------

    /// <summary>
    /// Fails if anyone starts interpreting rules text into facts again. That is the
    /// maintenance treadmill this design exists to avoid: a phrasing list is out of date the
    /// day it is written, and its gaps are invisible.
    /// </summary>
    [Fact]
    public void Rules_text_is_never_interpreted_into_facts()
    {
        var card = new CardFactSheet.FactCard(
            Guid.NewGuid().ToString(), "conditional dual", "{0}", 0,
            "As this land enters, you may pay 2 life. If you don't, it enters tapped. " +
            "{T}: Add {B} or {G}. Create a 2/2 green Wolf creature token.",
            "Land", CardType.Land, ["Swamp", "Forest"], null, null, Golgari, [], false);

        var facts = Facts(card, Warg(Golgari));

        Assert.DoesNotContain("tapped", facts, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", facts, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("net mana", facts, StringComparison.OrdinalIgnoreCase);
    }
}
