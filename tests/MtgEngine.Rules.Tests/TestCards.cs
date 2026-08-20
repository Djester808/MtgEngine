using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

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
    // Games here begin with `withMulligans: false`. Mulligans are a real part of starting a
    // game (CR 103.5) and have their own tests; asking every test about priority or combat to
    // answer two mulligan questions first would put a preamble in front of each one that has
    // nothing to do with what it is checking.
    //
    // These fixtures cost nothing to cast, deliberately. The tests using them are about timing,
    // priority, the stack, layers and combat; giving each one a mana cost would add a subplot
    // about paying it to every single test, and the cost rules have their own file.

    /// <summary>A creature with no abilities — enough to be a permanent and to attack.</summary>
    public static CardDefinition Creature(string name = "Bear", int power = 2, int toughness = 2) => new()
    {
        OracleId = $"oracle-{name.ToLowerInvariant()}",
        Name = name,
        Cmc = 0,
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

    /// <summary>A creature that cannot be destroyed (CR 702.12).</summary>
    public static CardDefinition Indestructible(string name = "Stone Wall") => new()
    {
        OracleId = $"oracle-{name.ToLowerInvariant()}",
        Name = name,
        Cmc = 0,
        CardTypes = CardType.Creature,
        Subtypes = ["Wall"],
        Power = 0,
        Toughness = 4,
        Keywords = KeywordAbility.Indestructible,
    };

    /// <summary>A legendary creature, for the legend rule (CR 704.5j).</summary>
    public static CardDefinition Legend(string name) => new()
    {
        OracleId = $"oracle-{name.ToLowerInvariant()}",
        Name = name,
        Cmc = 0,
        CardTypes = CardType.Creature,
        Supertypes = ["Legendary"],
        Subtypes = ["Human"],
        Power = 3,
        Toughness = 3,
    };

    /// <summary>A token, which ceases to exist anywhere but the battlefield (CR 704.5d).</summary>
    public static CardDefinition Token(string name = "Wolf", int power = 2, int toughness = 2) => new()
    {
        OracleId = $"oracle-token-{name.ToLowerInvariant()}",
        Name = name,
        Cmc = 0,
        CardTypes = CardType.Creature | CardType.Token,
        Subtypes = [name],
        Power = power,
        Toughness = toughness,
    };

    /// <summary>A creature whose oracle id the trigger tests hang an ability on.</summary>
    public static CardDefinition Watcher(string name = "Watcher") => new()
    {
        OracleId = "oracle-watcher-" + name.ToLowerInvariant(),
        Name = name,
        Cmc = 0,
        CardTypes = CardType.Creature,
        Subtypes = ["Human"],
        Power = 1,
        Toughness = 3,
    };

    /// <summary>A creature whose static ability the layer tests hang a lord effect on.</summary>
    public static CardDefinition Lord(string name = "Lord") => new()
    {
        OracleId = "oracle-lord-" + name.ToLowerInvariant(),
        Name = name,
        Cmc = 0,
        CardTypes = CardType.Creature,
        Subtypes = ["Elf"],
        OracleText = "Other creatures you control get +1/+1.",
        Power = 2,
        Toughness = 2,
    };

    /// <summary>A noncreature permanent whose static ability pumps, for layer ordering.</summary>
    public static CardDefinition Anthem(string name = "Anthem") => new()
    {
        OracleId = "oracle-anthem-" + name.ToLowerInvariant(),
        Name = name,
        Cmc = 0,
        CardTypes = CardType.Enchantment,
        OracleText = "Creatures you control get +0/+2.",
    };

    /// <summary>A permanent the replacement-effect tests hang an effect on.</summary>
    public static CardDefinition Shield(string name = "Shield") => new()
    {
        OracleId = "oracle-shield-" + name.ToLowerInvariant(),
        Name = name,
        Cmc = 0,
        CardTypes = CardType.Enchantment,
    };

    /// <summary>A creature that enters with counters, for CR 614.1c.</summary>
    public static CardDefinition Grower(string name = "Grower") => new()
    {
        OracleId = "oracle-grower-" + name.ToLowerInvariant(),
        Name = name,
        Cmc = 0,
        CardTypes = CardType.Creature,
        Subtypes = ["Beast"],
        OracleText = "This creature enters with two +1/+1 counters on it.",
        Power = 1,
        Toughness = 1,
    };

    /// <summary>A creature with a given keyword, for the combat rules that turn on one.</summary>
    public static CardDefinition WithKeyword(
        string name, KeywordAbility keywords, int power = 2, int toughness = 2) => new()
        {
            OracleId = "oracle-kw-" + name.ToLowerInvariant(),
            Name = name,
            Cmc = 0,
            CardTypes = CardType.Creature,
            Subtypes = ["Soldier"],
            Power = power,
            Toughness = toughness,
            Keywords = keywords,
        };

    /// <summary>A card with a real mana cost, for the cost rules (CR 601.2h).</summary>
    public static CardDefinition Costed(string name, string manaCost, int cmc) => new()
    {
        OracleId = "oracle-costed-" + name.ToLowerInvariant(),
        Name = name,
        ManaCostRaw = manaCost,
        Cmc = cmc,
        CardTypes = CardType.Creature,
        Subtypes = ["Human"],
        Power = 1,
        Toughness = 1,
    };

    /// <summary>An instant, for timing and stack tests.</summary>
    public static CardDefinition Instant(string name = "Shock") => new()
    {
        OracleId = $"oracle-{name.ToLowerInvariant()}",
        Name = name,
        Cmc = 0,
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

    /// <summary>
    /// A game of any size, seated in the order returned, with the first seat active.
    /// </summary>
    /// <remarks>
    /// Priority, APNAP and the stack are all written against the seating list rather than
    /// against "the opponent", so the four-player case is worth running everywhere the
    /// two-player one is — it is the case the previous engine could not represent at all.
    /// </remarks>
    public static (Game Game, IReadOnlyList<Guid> Seats) MultiPlayer(
        int players, int deckSize = 30, int seed = 5)
    {
        var seats = Enumerable.Range(1, players)
            .Select(i => new Guid($"{i:D8}-0000-0000-0000-000000000000"))
            .ToList();

        var setups = seats
            .Select((id, i) => new PlayerSetup(id, $"P{i + 1}", 40, Deck(deckSize, $"P{i + 1}")))
            .ToList();

        var game = Game.Start(Guid.NewGuid(), setups, new GameRandom(seed), startingPlayerId: seats[0]);
        return (game, seats);
    }

    /// <summary>
    /// Puts a specific card into a player's hand, so a test can name the card it is about.
    /// </summary>
    /// <remarks>
    /// Emits <c>ObjectCreated</c> rather than editing state, so the log stays a complete account
    /// of the game and the replay tests keep meaning something. The card arrives in hand the way
    /// a conjured card would (CR 400.11b) rather than by being drawn, which keeps the test's
    /// setup out of the library order it is not about.
    /// </remarks>
    public static ObjectId PutInHand(Game game, Guid playerId, CardDefinition card)
    {
        var onTop = game.Create(playerId, card, Zone.Hand);
        return onTop;
    }

    /// <summary>
    /// Passes priority until the condition holds, discarding down to hand size when cleanup
    /// asks (CR 514.1).
    /// </summary>
    /// <remarks>
    /// The discard is why this exists. A game left to run passes through cleanup every turn, and
    /// cleanup stops dead while anyone holds eight cards — correctly, because which card to pitch
    /// is a decision the engine must not make. A test walking several turns has to answer that
    /// question, and answering it with "the first card" keeps the choice out of the engine while
    /// still letting the turn end.
    /// </remarks>
    public static void PassUntil(Game game, Func<bool> until, int guard = 1000)
    {
        for (var i = 0; i < guard; i++)
        {
            if (until())
                return;

            foreach (var playerId in game.PendingDiscards.ToList())
                game.Discard(playerId, game.State.GetPlayer(playerId).Hand[0]);

            // Combat steps wait for a declaration before anyone gets priority (CR 508.1,
            // 509.1). A test that is not about combat still has to answer, and the answer that
            // changes nothing is "nothing attacks".
            if (game.State.CurrentStep == TurnStep.DeclareAttackers
                && !game.State.Combat.AttackersDeclared)
            {
                game.DeclareAttackers(game.State.ActivePlayerId, new Dictionary<ObjectId, Guid>());
                continue;
            }

            if (game.State.CurrentStep == TurnStep.DeclareBlockers
                && !game.State.Combat.BlockersDeclared)
            {
                var defender = game.State.Combat.Attackers.Values.First();
                game.DeclareBlockers(defender, new Dictionary<ObjectId, IReadOnlyList<ObjectId>>());
                continue;
            }

            if (until())
                return;

            // A pending decision stops the game dead, by design. Saying which one beats a test
            // that reports "nobody has priority" and leaves you to guess why.
            if (game.State.Choice is { } choice)
            {
                throw new InvalidOperationException(
                    $"The game is waiting on {choice.PlayerId:N} to answer: {choice.Prompt}");
            }

            var holder = game.State.Priority.Holder
                ?? throw new InvalidOperationException(
                    $"Nobody has priority in {game.State.CurrentStep} and nothing is pending.");

            game.PassPriority(holder);
        }

        throw new InvalidOperationException("Gave up passing priority; the condition never held.");
    }

    /// <summary>Plays on until the given turn begins.</summary>
    public static void PassToTurn(Game game, int turnNumber) =>
        PassUntil(game, () => game.State.TurnNumber >= turnNumber);

    /// <summary>Plays on until the game is in the given step.</summary>
    public static void PassToStep(Game game, TurnStep step) =>
        PassUntil(game, () => game.State.CurrentStep == step);
}
