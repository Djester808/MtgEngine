using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Planeswalkers: entering with loyalty, being attacked, and dying at zero (CR 306, 508.1b).
/// </summary>
/// <remarks>
/// Planeswalkers were a card type the engine knew the name of and nothing else — they could not
/// be attacked, damage did nothing to them, and they never left the battlefield. A permanent
/// that cannot be interacted with is worse than an unimplemented one, because it sits on the
/// board looking like it works.
/// </remarks>
public sealed class PlaneswalkerTests
{
    private static CardDefinition Walker(string name = "Chandra", int loyalty = 4) => new()
    {
        OracleId = "oracle-" + name.ToLowerInvariant(),
        Name = name,
        CardTypes = CardType.Planeswalker,
        Supertypes = ["Legendary"],
        Subtypes = [name],
        StartingLoyalty = loyalty,
    };

    private static (Game Game, Guid Alice, Guid Bob) BeforeCombat()
    {
        var (game, alice, bob) = TestCards.TwoPlayer(deckSize: 40);
        game.BeginPlay(withMulligans: false);
        TestCards.PassToStep(game, TurnStep.PrecombatMain);
        return (game, alice, bob);
    }

    private static void Ready(Game game)
    {
        TestCards.PassToTurn(game, 3);
        TestCards.PassToStep(game, TurnStep.DeclareAttackers);
    }

    private static int LoyaltyOf(Game game, ObjectId id) =>
        game.State.GetObject(id).Permanent!.Counters.GetValueOrDefault(CounterKinds.Loyalty);

    [Fact]
    public void A_planeswalker_enters_with_its_printed_loyalty()
    {
        // CR 306.5b. Not a replacement effect a card carries — it is what the rules do for every
        // planeswalker, so it needs no definition per card.
        var (game, alice, _) = BeforeCombat();

        var walker = game.Create(alice, Walker(loyalty: 4), Zone.Battlefield);

        Assert.Equal(4, LoyaltyOf(game, walker));
    }

    [Fact]
    public void A_creature_can_attack_a_planeswalker()
    {
        // CR 508.1b.
        var (game, alice, bob) = BeforeCombat();
        var bear = game.Create(alice, TestCards.Creature("Bear", 2, 2), Zone.Battlefield);
        var walker = game.Create(bob, Walker(), Zone.Battlefield);
        Ready(game);

        game.DeclareAttackers(alice, new Dictionary<ObjectId, AttackTarget>
        {
            [bear] = AttackTarget.At(bob, walker),
        });

        Assert.Single(game.State.Combat.Attackers);
        Assert.True(game.State.Combat.Attackers[bear].IsPlaneswalker);
    }

    [Fact]
    public void Damage_to_a_planeswalker_removes_loyalty_rather_than_life()
    {
        // CR 306.7: a planeswalker has no toughness for damage to be compared against, so the
        // damage removes that many loyalty counters instead.
        var (game, alice, bob) = BeforeCombat();
        var bear = game.Create(alice, TestCards.Creature("Bear", 2, 2), Zone.Battlefield);
        var walker = game.Create(bob, Walker(loyalty: 4), Zone.Battlefield);
        Ready(game);

        game.DeclareAttackers(alice, new Dictionary<ObjectId, AttackTarget>
        {
            [bear] = AttackTarget.At(bob, walker),
        });
        TestCards.PassUntil(game, () => game.State.CurrentStep == TurnStep.EndOfCombat);

        Assert.Equal(2, LoyaltyOf(game, walker));
        // The player took nothing: the attack went at the planeswalker.
        Assert.Equal(20, game.State.GetPlayer(bob).Life);
    }

    [Fact]
    public void A_planeswalker_at_zero_loyalty_goes_to_the_graveyard()
    {
        // CR 704.5i. It is put into the graveyard rather than destroyed, so indestructible does
        // not save it — the same shape as a creature at zero toughness.
        var (game, alice, bob) = BeforeCombat();
        var giant = game.Create(alice, TestCards.Creature("Giant", 5, 5), Zone.Battlefield);
        var walker = game.Create(bob, Walker(loyalty: 4), Zone.Battlefield);
        Ready(game);

        game.DeclareAttackers(alice, new Dictionary<ObjectId, AttackTarget>
        {
            [giant] = AttackTarget.At(bob, walker),
        });
        TestCards.PassUntil(game, () => game.State.CurrentStep == TurnStep.EndOfCombat);

        Assert.False(game.State.TryGetObject(walker, out _));
        // Named rather than counted: the graveyard also holds whatever was discarded to hand
        // size on the way here, and a count would be asserting the wrong thing.
        Assert.Contains(
            game.State.GetPlayer(bob).Graveyard,
            id => game.State.GetObject(id).Card.Name == "Chandra");
    }

    [Fact]
    public void A_planeswalker_can_be_blocked_for()
    {
        // CR 509.1a: the defending player blocks, whether the attack was at them or at their
        // planeswalker — so a chump block still saves it.
        var (game, alice, bob) = BeforeCombat();
        var bear = game.Create(alice, TestCards.Creature("Bear", 2, 2), Zone.Battlefield);
        var walker = game.Create(bob, Walker(loyalty: 4), Zone.Battlefield);
        var chump = game.Create(bob, TestCards.Creature("Chump", 0, 1), Zone.Battlefield);
        Ready(game);

        game.DeclareAttackers(alice, new Dictionary<ObjectId, AttackTarget>
        {
            [bear] = AttackTarget.At(bob, walker),
        });
        TestCards.PassUntil(game, () => game.State.CurrentStep == TurnStep.DeclareBlockers);
        game.DeclareBlockers(bob, new Dictionary<ObjectId, IReadOnlyList<ObjectId>>
        {
            [bear] = [chump],
        });
        TestCards.PassUntil(game, () => game.State.CurrentStep == TurnStep.EndOfCombat);

        Assert.Equal(4, LoyaltyOf(game, walker));
    }

    [Fact]
    public void Trample_over_a_blocker_hits_the_planeswalker_not_the_player()
    {
        // CR 702.19b: the excess goes to what the creature was attacking, which is the
        // planeswalker — sending it to the player instead would be a free extra hit.
        var (game, alice, bob) = BeforeCombat();
        var trampler = game.Create(
            alice, TestCards.WithKeyword("Wurm", KeywordAbility.Trample, 5, 5), Zone.Battlefield);
        var walker = game.Create(bob, Walker(loyalty: 6), Zone.Battlefield);
        var chump = game.Create(bob, TestCards.Creature("Chump", 0, 1), Zone.Battlefield);
        Ready(game);

        game.DeclareAttackers(alice, new Dictionary<ObjectId, AttackTarget>
        {
            [trampler] = AttackTarget.At(bob, walker),
        });
        TestCards.PassUntil(game, () => game.State.CurrentStep == TurnStep.DeclareBlockers);
        game.DeclareBlockers(bob, new Dictionary<ObjectId, IReadOnlyList<ObjectId>>
        {
            [trampler] = [chump],
        });
        TestCards.PassUntil(game, () => game.State.CurrentStep == TurnStep.EndOfCombat);

        Assert.Equal(2, LoyaltyOf(game, walker));
        Assert.Equal(20, game.State.GetPlayer(bob).Life);
    }

    [Fact]
    public void A_creature_attacking_a_planeswalker_that_has_gone_deals_nothing()
    {
        // CR 508.1b: it does not fall through to the player.
        var (game, alice, bob) = BeforeCombat();
        var bear = game.Create(alice, TestCards.Creature("Bear", 2, 2), Zone.Battlefield);
        var walker = game.Create(bob, Walker(), Zone.Battlefield);
        Ready(game);

        game.DeclareAttackers(alice, new Dictionary<ObjectId, AttackTarget>
        {
            [bear] = AttackTarget.At(bob, walker),
        });
        game.Move(walker, Zone.Graveyard, MoveCause.Destroy);
        TestCards.PassUntil(game, () => game.State.CurrentStep == TurnStep.EndOfCombat);

        Assert.Equal(20, game.State.GetPlayer(bob).Life);
        Assert.Contains(game.Log, e => e is NothingHappened);
    }

    [Fact]
    public void Only_a_planeswalker_the_defending_player_controls_can_be_attacked()
    {
        var (game, alice, bob) = BeforeCombat();
        var bear = game.Create(alice, TestCards.Creature("Bear", 2, 2), Zone.Battlefield);
        var mine = game.Create(alice, Walker(), Zone.Battlefield);
        Ready(game);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            game.DeclareAttackers(alice, new Dictionary<ObjectId, AttackTarget>
            {
                [bear] = AttackTarget.At(bob, mine),
            }));

        Assert.Contains("508.1b", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_game_with_a_planeswalker_still_replays()
    {
        var (game, alice, bob) = BeforeCombat();
        var bear = game.Create(alice, TestCards.Creature("Bear", 2, 2), Zone.Battlefield);
        var walker = game.Create(bob, Walker(), Zone.Battlefield);
        Ready(game);
        game.DeclareAttackers(alice, new Dictionary<ObjectId, AttackTarget>
        {
            [bear] = AttackTarget.At(bob, walker),
        });
        TestCards.PassToTurn(game, 4);

        Assert.Equal(game.State, GameReducer.Replay(game.Log));
    }
}
