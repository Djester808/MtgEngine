using MtgEngine.Api.Services;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Tests;

/// <summary>
/// Role buckets drive how the candidate pool is presented, and the build prompt asks for
/// specific counts per role. Cards landing in the wrong bucket skews those quotas.
/// </summary>
public class CardRoleClassifierTests
{
    private static CardDefinition Card(string name, string text, CardType types = CardType.Sorcery) =>
        new() { Name = name, OracleText = text, CardTypes = types };

    [Fact]
    public void Land_IsClassifiedByType_NotText()
    {
        // Cabal Coffers adds mana, but it is a land and belongs in the land count.
        var card = Card("Cabal Coffers", "{2}, {T}: Add {B} for each Swamp you control.", CardType.Land);
        Assert.Equal(CardRole.Land, CardRoleClassifier.Classify(card));
    }

    [Theory]
    [InlineData("Sol Ring", "{T}: Add {C}{C}.", CardType.Artifact)]
    [InlineData("Charcoal Diamond", "{T}: Add {B}.", CardType.Artifact)]
    [InlineData("Cultivate", "Search your library for a basic land card, put it onto the battlefield tapped.", CardType.Sorcery)]
    public void ManaProducers_AndLandFetch_AreRamp(string name, string text, CardType type) =>
        Assert.Equal(CardRole.Ramp, CardRoleClassifier.Classify(Card(name, text, type)));

    /// <remarks>
    /// The shapes the brace-only pattern missed. MeasureFacts derives the ramp count and
    /// the mana-source total from this classifier, so a miss here is a wrong number on the
    /// review screen and in the assessment prompt — a four-colour deck reported 3 ramp and
    /// 41 sources while its own assessment counted 11 and 49.
    /// </remarks>
    [Theory]
    [InlineData("Birds of Paradise", "Flying. {T}: Add one mana of any color.", CardType.Creature)]
    [InlineData("Arcane Signet", "{T}: Add one mana of any color in your commander's color identity.", CardType.Artifact)]
    [InlineData("Fellwar Stone", "{T}: Add one mana of any color that a land an opponent controls could produce.", CardType.Artifact)]
    [InlineData("Chromatic Lantern", "{T}: Add one mana of any color.", CardType.Artifact)]
    [InlineData("Three Visits", "Search your library for a Forest card and put it onto the battlefield tapped.", CardType.Sorcery)]
    [InlineData("Nature's Lore", "Search your library for a Forest card, put it onto the battlefield.", CardType.Sorcery)]
    [InlineData("Wood Elves", "When this creature enters, search your library for a Forest card and put it onto the battlefield.", CardType.Creature)]
    [InlineData("Farseek", "Search your library for a Plains, Island, Swamp, or Mountain card and put it onto the battlefield tapped.", CardType.Sorcery)]
    public void WordyManaAndTypedLandFetch_AreAlsoRamp(string name, string text, CardType type) =>
        Assert.Equal(CardRole.Ramp, CardRoleClassifier.Classify(Card(name, text, type)));

    /// <remarks>
    /// The widened pattern must not swallow the format's other use of "add". Counters are
    /// added constantly, and a +1/+1 counter is not a mana source.
    /// </remarks>
    [Theory]
    [InlineData("Counter Adder", "Put a +1/+1 counter on target creature. Add a counter to it each upkeep.")]
    [InlineData("Loyalty Gainer", "Adds a loyalty counter to target planeswalker.")]
    public void AddingSomethingThatIsNotMana_IsNotRamp(string name, string text) =>
        Assert.NotEqual(CardRole.Ramp, CardRoleClassifier.Classify(Card(name, text, CardType.Sorcery)));

    [Fact]
    public void A_typed_land_fetch_on_a_land_is_still_a_land()
    {
        // Same reasoning as Cabal Coffers: it belongs in the land count whatever its text
        // does, or the land total and the ramp total both go wrong at once.
        var card = Card(
            "Krosan Verge",
            "{T}: Add {C}. {2}, {T}, Sacrifice: Search your library for a Forest and a Plains card.",
            CardType.Land);

        Assert.Equal(CardRole.Land, CardRoleClassifier.Classify(card));
    }

    [Theory]
    [InlineData("Murder", "Destroy target creature.")]
    [InlineData("Damnation", "Destroy all creatures. They can't be regenerated.")]
    [InlineData("Go for the Throat", "Destroy target nonartifact creature.")]
    [InlineData("Bloodchief's Thirst", "Destroy target creature or planeswalker with mana value 2 or less.")]
    [InlineData("Counterspell", "Counter target spell.")]
    public void DestroyAndCounter_AreRemoval(string name, string text) =>
        Assert.Equal(CardRole.Removal, CardRoleClassifier.Classify(Card(name, text)));

    [Theory]
    [InlineData("Night's Whisper", "You lose 2 life and draw two cards.")]
    [InlineData("Sign in Blood", "Target player draws two cards and loses 2 life.")]
    [InlineData("Read the Bones", "Scry 2, then draw two cards. You lose 2 life.")]
    public void DrawSpells_AreDraw(string name, string text) =>
        Assert.Equal(CardRole.Draw, CardRoleClassifier.Classify(Card(name, text)));

    [Fact]
    public void PlainCreature_FallsToOther()
    {
        var card = Card("Grizzly Bears", "", CardType.Creature);
        Assert.Equal(CardRole.Other, CardRoleClassifier.Classify(card));
    }

    // ---- Priority: cards that do several jobs get exactly one bucket ----

    [Fact]
    public void RampBeatsDraw_WhenCardDoesBoth()
    {
        // Solemn Simulacrum ramps and cantrips; it is played for the mana.
        var card = Card("Solemn Simulacrum",
            "When this enters, search your library for a basic land card, put it onto the battlefield tapped. " +
            "When this dies, draw a card.",
            CardType.Artifact | CardType.Creature);
        Assert.Equal(CardRole.Ramp, CardRoleClassifier.Classify(card));
    }

    [Fact]
    public void RemovalBeatsDraw_WhenCardDoesBoth()
    {
        var card = Card("Vendetta Plus", "Destroy target creature. Draw a card.");
        Assert.Equal(CardRole.Removal, CardRoleClassifier.Classify(card));
    }

    [Fact]
    public void LandBeatsEverything()
    {
        // A land that draws and fetches is still counted against the land quota.
        var card = Card("Weird Land",
            "{T}: Add {B}. {3}, {T}: Draw a card. Search your library for a land card.",
            CardType.Land);
        Assert.Equal(CardRole.Land, CardRoleClassifier.Classify(card));
    }

    [Fact]
    public void EveryRoleHasADistinctLabel()
    {
        var roles = Enum.GetValues<CardRole>();
        var labels = roles.Select(CardRoleClassifier.Label).ToArray();

        Assert.Equal(roles.Length, labels.Distinct().Count());
        Assert.All(labels, l => Assert.False(string.IsNullOrWhiteSpace(l)));
    }

    [Fact]
    public void EmptyOracleText_DoesNotThrow() =>
        Assert.Equal(CardRole.Other, CardRoleClassifier.Classify(Card("Blank", "")));

    [Fact]
    public void NullOracleText_DoesNotThrow()
    {
        var card = new CardDefinition { Name = "Null Text", OracleText = null!, CardTypes = CardType.Creature };
        Assert.Equal(CardRole.Other, CardRoleClassifier.Classify(card));
    }
}
