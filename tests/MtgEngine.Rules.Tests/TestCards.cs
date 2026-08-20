using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Engine;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Synthetic cards for exercising the engine.
/// </summary>
/// <remarks>
/// Deliberately not real cards. The backbone is being settled before any card behaviour exists,
/// and a test that leans on a real card's text starts failing for reasons that have nothing to
/// do with the rule under test the moment that card's implementation changes. These are the
/// smallest things that make each subsystem move.
/// </remarks>
internal static class TestCards
{
    /// <summary>A creature with no abilities — enough to be a permanent and to attack.</summary>
    public static CardDefinition Creature(string name = "Bear", int power = 2, int toughness = 2) => new()
    {
        OracleId = $"oracle-{name.ToLowerInvariant()}",
        Name = name,
        ManaCostRaw = "{1}{G}",
        Cmc = 2,
        CardTypes = CardType.Creature,
        Subtypes = ["Bear"],
        Power = power,
        Toughness = toughness,
        ColorIdentity = [ManaColor.Green],
    };

    /// <summary>A basic land: the one permanent that is played rather than cast (CR 305.1).</summary>
    public static CardDefinition BasicLand(string name = "Forest") => new()
    {
        OracleId = $"oracle-{name.ToLowerInvariant()}",
        Name = name,
        Cmc = 0,
        CardTypes = CardType.Land,
        Supertypes = ["Basic"],
        Subtypes = [name],
        ColorIdentity = [ManaColor.Green],
    };

    /// <summary>An instant, for timing and stack tests.</summary>
    public static CardDefinition Instant(string name = "Shock") => new()
    {
        OracleId = $"oracle-{name.ToLowerInvariant()}",
        Name = name,
        ManaCostRaw = "{R}",
        Cmc = 1,
        CardTypes = CardType.Instant,
        OracleText = "Shock deals 2 damage to any target.",
        ColorIdentity = [ManaColor.Red],
    };

    /// <summary>A deck of distinguishable cards, so an order can be asserted.</summary>
    public static IReadOnlyList<CardDefinition> Deck(int size, string prefix = "Card") =>
        [.. Enumerable.Range(1, size).Select(i => Creature($"{prefix} {i:D2}"))];

    /// <summary>
    /// A two-player game with fixed seats and a fixed starting player, so no test depends on a
    /// coin flip. Libraries are shuffled by <see cref="Game.Start"/> with the given seed.
    /// </summary>
    public static (Game Game, Guid Alice, Guid Bob) TwoPlayer(int deckSize = 10, int seed = 1)
    {
        var alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var bob = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var game = Game.Start(
            Guid.Parse("99999999-9999-9999-9999-999999999999"),
            [
                new PlayerSetup(alice, "Alice", 20, Deck(deckSize, "Alice")),
                new PlayerSetup(bob, "Bob", 20, Deck(deckSize, "Bob")),
            ],
            new GameRandom(seed),
            startingPlayerId: alice);

        return (game, alice, bob);
    }
}
