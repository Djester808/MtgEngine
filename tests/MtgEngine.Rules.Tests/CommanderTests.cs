using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The Commander variant (CR 903).
/// </summary>
/// <remarks>
/// The app this engine lives in is a Commander deck builder, and the engine had none of this:
/// no command zone, no tax, no commander damage. A Commander game played as a duel with a
/// hundred cards is not the format.
/// </remarks>
public sealed class CommanderTests
{
    private const string CommanderId = "oracle-tovolar";

    private static CardDefinition Commander() => new()
    {
        OracleId = CommanderId,
        Name = "Tovolar",
        Cmc = 3,
        ManaCostRaw = "{1}{R}{G}",
        CardTypes = CardType.Creature,
        Supertypes = ["Legendary"],
        Subtypes = ["Human", "Werewolf"],
        Power = 3,
        Toughness = 3,
    };

    private static (Game Game, Guid Alice, Guid Bob) Commander1v1(int seed = 3)
    {
        var alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var bob = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var deck = TestCards.Deck(40, "Alice").Concat([Commander()]).ToList();

        var game = Game.Start(
            Guid.NewGuid(),
            [
                new PlayerSetup(alice, "Alice", 40, deck) { CommanderOracleId = CommanderId },
                new PlayerSetup(bob, "Bob", 40, TestCards.Deck(40, "Bob")),
            ],
            new GameRandom(seed),
            startingPlayerId: alice);

        game.BeginPlay(withMulligans: false);
        TestCards.PassToStep(game, TurnStep.PrecombatMain);
        return (game, alice, bob);
    }

    /// <summary>Gives a player enough mana to pay whatever the test is about.</summary>
    private static void GiveMana(Game game, Guid playerId, ManaColor color, int amount)
    {
        for (var i = 0; i < amount; i++)
            game.AddMana(playerId, color);
    }

    [Fact]
    public void The_commander_starts_in_the_command_zone()
    {
        // CR 903.6, and it has to happen before the shuffle — a commander shuffled into the
        // library first is a commander that can be drawn.
        var (game, alice, _) = Commander1v1();

        var inCommand = Assert.Single(game.State.Command);
        Assert.Equal("Tovolar", game.State.GetObject(inCommand).Card.Name);
        Assert.Equal(alice, game.State.GetObject(inCommand).OwnerId);
        Assert.DoesNotContain(
            game.State.GetPlayer(alice).Library,
            id => game.State.GetObject(id).Card.OracleId == CommanderId);
    }

    [Fact]
    public void A_commander_game_starts_at_forty_life()
    {
        // CR 903.7.
        var (game, alice, bob) = Commander1v1();

        Assert.Equal(40, game.State.GetPlayer(alice).Life);
        Assert.Equal(40, game.State.GetPlayer(bob).Life);
    }

    [Fact]
    public void The_commander_can_be_cast_from_the_command_zone()
    {
        // CR 903.8.
        var (game, alice, _) = Commander1v1();
        // {1}{R}{G}: red covers the generic and the R, green the G.
        GiveMana(game, alice, ManaColor.Red, 2);
        GiveMana(game, alice, ManaColor.Green, 1);

        var onStack = game.CastSpell(alice, game.State.Command[0]);

        Assert.Equal(Zone.Stack, game.State.GetObject(onStack).Zone);
        Assert.Empty(game.State.Command);
    }

    [Fact]
    public void The_first_cast_costs_no_tax()
    {
        var (game, alice, _) = Commander1v1();
        // {1}{R}{G} is three mana; red covers the generic and the R, green the G.
        GiveMana(game, alice, ManaColor.Red, 2);
        GiveMana(game, alice, ManaColor.Green, 1);

        game.CastSpell(alice, game.State.Command[0]);

        Assert.True(game.State.GetPlayer(alice).ManaPool.IsEmpty);
    }

    [Fact]
    public void The_second_cast_from_the_command_zone_costs_two_more()
    {
        // CR 903.8: {2} for each previous cast from the command zone.
        var (game, alice, bob) = Commander1v1();
        GiveMana(game, alice, ManaColor.Red, 2);
        GiveMana(game, alice, ManaColor.Green, 1);
        var onStack = game.CastSpell(alice, game.State.Command[0]);

        // It resolves, then goes back to the command zone.
        game.PassPriority(alice);
        game.PassPriority(bob);
        var permanent = game.State.Battlefield.Single();
        game.Move(permanent, Zone.Command, MoveCause.Other, alice);

        Assert.Equal(1, game.State.GetPlayer(alice).CommanderCastsFromCommandZone);

        // Three mana is no longer enough.
        GiveMana(game, alice, ManaColor.Red, 2);
        GiveMana(game, alice, ManaColor.Green, 1);
        var ex = Assert.Throws<InvalidOperationException>(
            () => game.CastSpell(alice, game.State.Command[0]));
        Assert.Contains("601.2h", ex.Message, StringComparison.Ordinal);

        // Five is.
        GiveMana(game, alice, ManaColor.Red, 2);
        game.CastSpell(alice, game.State.Command[0]);
        Assert.Equal(2, game.State.GetPlayer(alice).CommanderCastsFromCommandZone);
    }

    [Fact]
    public void Casting_it_from_hand_is_not_taxed_and_does_not_add_tax()
    {
        // CR 903.8 counts casts from the command zone specifically.
        var (game, alice, _) = Commander1v1();
        var inHand = game.Move(game.State.Command[0], Zone.Hand, MoveCause.Return, alice);
        GiveMana(game, alice, ManaColor.Red, 2);
        GiveMana(game, alice, ManaColor.Green, 1);

        game.CastSpell(alice, inHand);

        Assert.Equal(0, game.State.GetPlayer(alice).CommanderCastsFromCommandZone);
    }

    [Fact]
    public void Combat_damage_from_a_commander_is_tracked_against_the_player()
    {
        // CR 903.10a.
        var (game, alice, bob) = Commander1v1();
        var onBattlefield = game.Create(alice, Commander(), Zone.Battlefield);

        game.MarkDamageToPlayer(bob, onBattlefield, 3);

        Assert.Equal(3, game.State.GetPlayer(bob).CommanderDamage[CommanderId]);
        Assert.Equal(37, game.State.GetPlayer(bob).Life);
    }

    [Fact]
    public void Twenty_one_commander_damage_loses_the_game()
    {
        // CR 903.10a — and at 40 life, from a 3/3, it is the only way that commander wins.
        var (game, alice, bob) = Commander1v1();
        var onBattlefield = game.Create(alice, Commander(), Zone.Battlefield);

        game.MarkDamageToPlayer(bob, onBattlefield, 20);
        game.PassPriority(alice);
        Assert.False(game.State.GetPlayer(bob).HasLost);
        Assert.True(game.State.GetPlayer(bob).Life > 0);

        game.MarkDamageToPlayer(bob, onBattlefield, 1);
        TestCards.PassUntil(game, () => game.State.IsOver);

        Assert.True(game.State.GetPlayer(bob).HasLost);
        Assert.Contains(game.Log, e => e is PlayerLost { LosingRule: "903.10a" });
    }

    [Fact]
    public void Damage_accumulates_across_the_commander_dying_and_returning()
    {
        // The reason the total is kept per commander and not per creature: a commander that
        // dies and comes back is a new object every time (CR 400.7), and the twenty-one is
        // still counted "over the course of the game".
        var (game, alice, bob) = Commander1v1();

        var first = game.Create(alice, Commander(), Zone.Battlefield);
        game.MarkDamageToPlayer(bob, first, 11);
        game.Move(first, Zone.Graveyard, MoveCause.Destroy);

        var second = game.Create(alice, Commander(), Zone.Battlefield);
        game.MarkDamageToPlayer(bob, second, 10);

        Assert.Equal(21, game.State.GetPlayer(bob).CommanderDamage[CommanderId]);
    }

    [Fact]
    public void Noncombat_damage_from_a_commander_does_not_count()
    {
        // CR 903.10a says combat damage.
        var (game, alice, bob) = Commander1v1();
        var onBattlefield = game.Create(alice, Commander(), Zone.Battlefield);

        game.MarkDamageToPlayer(bob, onBattlefield, 21, isCombat: false);

        Assert.False(game.State.GetPlayer(bob).CommanderDamage.ContainsKey(CommanderId));
        Assert.Equal(19, game.State.GetPlayer(bob).Life);
    }

    [Fact]
    public void A_game_with_no_commander_is_unaffected()
    {
        var (game, alice, bob) = TestCards.TwoPlayer();
        game.BeginPlay(withMulligans: false);

        Assert.Empty(game.State.Command);
        Assert.Null(game.State.GetPlayer(alice).CommanderOracleId);
        Assert.Equal(20, game.State.GetPlayer(bob).Life);
    }

    [Fact]
    public void A_commander_game_still_replays()
    {
        var (game, alice, bob) = Commander1v1();
        GiveMana(game, alice, ManaColor.Red, 2);
        GiveMana(game, alice, ManaColor.Green, 1);
        game.CastSpell(alice, game.State.Command[0]);
        game.PassPriority(alice);
        game.PassPriority(bob);
        TestCards.PassToTurn(game, 2);

        Assert.Equal(game.State, GameReducer.Replay(game.Log));
    }
}
