using MtgEngine.Api.Services;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Tests;

[Trait("Scope", "Deck")]
public sealed class CommanderDeckValidatorTests
{
    private static readonly CardDefinition Leader = Card("leader", CardType.Creature, ["Legendary"], [ManaColor.Red]);
    private static readonly CardDefinition Mountain = Card("Mountain", CardType.Land, ["Basic"], [ManaColor.Red]);

    [Fact]
    public void Two_commanders_count_toward_one_hundred_and_combine_their_color_identities()
    {
        var red = Card("red", CardType.Creature, ["Legendary"], [ManaColor.Red], "Partner");
        var blue = Card("blue", CardType.Creature, ["Legendary"], [ManaColor.Blue], "Partner");
        var island = Card("Island", CardType.Land, ["Basic"], [ManaColor.Blue]);
        CommanderDeckValidator.Validate([red, blue, island, .. Enumerable.Repeat(Mountain, 97)], [red, blue]);
        Assert.Throws<InvalidRequestException>(() => CommanderDeckValidator.Validate(
            [red, blue, island, .. Enumerable.Repeat(Mountain, 98)], [red, blue]));
    }

    [Fact]
    public void A_hundred_card_deck_includes_its_commander_and_allows_basic_lands() =>
        CommanderDeckValidator.Validate([Leader, .. Enumerable.Repeat(Mountain, 99)], [Leader]);

    [Theory]
    [InlineData(98)]
    [InlineData(100)]
    public void The_commander_counts_toward_exactly_one_hundred_cards(int lands) =>
        Assert.Throws<InvalidRequestException>(() => CommanderDeckValidator.Validate([Leader, .. Enumerable.Repeat(Mountain, lands)], [Leader]));

    [Fact]
    public void A_colorless_commander_does_not_allow_colored_cards()
    {
        var leader = Card("colorless", CardType.Creature, ["Legendary"]);
        Assert.Throws<InvalidRequestException>(() => CommanderDeckValidator.Validate([leader, .. Enumerable.Repeat(Mountain, 99)], [leader]));
    }

    [Fact]
    public void Repeated_nonbasic_names_are_rejected()
    {
        var spell = Card("spell", CardType.Instant);
        Assert.Throws<InvalidRequestException>(() => CommanderDeckValidator.Validate([Leader, spell, spell, .. Enumerable.Repeat(Mountain, 97)], [Leader]));
    }

    [Fact]
    public void A_basic_land_type_is_checked_even_when_metadata_has_no_color_identity()
    {
        var island = new CardDefinition
        {
            OracleId = "island",
            Name = "Island",
            CardTypes = CardType.Land,
            Supertypes = ["Basic"],
            Subtypes = ["Island"],
            Legalities = new Dictionary<string, string> { ["commander"] = "legal" },
        };
        Assert.Throws<InvalidRequestException>(() => CommanderDeckValidator.Validate([Leader, island, .. Enumerable.Repeat(Mountain, 98)], [Leader]));
    }

    [Fact]
    public void A_normal_creature_cannot_be_the_commander()
    {
        var creature = Card("creature", CardType.Creature);
        Assert.Throws<InvalidRequestException>(() => CommanderDeckValidator.Validate([creature, .. Enumerable.Repeat(Mountain, 99)], [creature]));
    }

    [Fact]
    public void A_banned_card_is_rejected_even_if_its_color_matches()
    {
        var banned = new CardDefinition { Name = "banned", OracleId = "banned", Legalities = new Dictionary<string, string> { ["commander"] = "banned" } };
        Assert.Throws<InvalidRequestException>(() => CommanderDeckValidator.Validate([Leader, banned, .. Enumerable.Repeat(Mountain, 98)], [Leader]));
    }

    [Theory]
    [InlineData("A deck can have any number of cards named exception.", 20, true)]
    [InlineData("A deck can have up to seven cards named exception.", 7, true)]
    [InlineData("A deck can have up to seven cards named exception.", 8, false)]
    public void Explicit_copy_limits_replace_singleton(string text, int copies, bool allowed)
    {
        var exception = Card("exception", CardType.Creature, text: text);
        CardDefinition[] deck = [Leader, .. Enumerable.Repeat(exception, copies), .. Enumerable.Repeat(Mountain, 99 - copies)];
        if (allowed)
            CommanderDeckValidator.Validate(deck, [Leader]);
        else
            Assert.Throws<InvalidRequestException>(() => CommanderDeckValidator.Validate(deck, [Leader]));
    }

    private static CardDefinition Card(string name, CardType type, string[]? supertypes = null, ManaColor[]? colors = null, string text = "") => new()
    {
        OracleId = name,
        Name = name,
        CardTypes = type,
        Supertypes = supertypes ?? [],
        ColorIdentity = colors ?? [],
        OracleText = text,
        Legalities = new Dictionary<string, string> { ["commander"] = "legal" },
    };
}
