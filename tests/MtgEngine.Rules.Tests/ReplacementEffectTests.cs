using MtgEngine.Domain.Models;
using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Replacement effects (CR 614).
/// </summary>
/// <remarks>
/// A replacement effect is not a trigger and does not use the stack. It changes the event before
/// it happens, which is why the original event never occurred and never triggered anything
/// watching for it (CR 603.2g). That distinction is the whole point: preventing damage is not
/// "dealing damage and then undoing it", and an engine that models it that way fires every
/// damage trigger in the game.
/// </remarks>
public sealed class ReplacementEffectTests
{
    private sealed class Abilities(params (string Fragment, ReplacementEffectDefinition Effect)[] defs)
        : IAbilitySource
    {
        public IReadOnlyList<TriggeredAbilityDefinition> TriggersOf(CardDefinition card) => [];

        public IReadOnlyList<ReplacementEffectDefinition> ReplacementsOf(CardDefinition card) =>
            [.. defs.Where(d => card.OracleId.Contains(d.Fragment, StringComparison.Ordinal))
                .Select(d => d.Effect)];
    }

    private static (Game Game, Guid Alice, Guid Bob) InMainPhase(IAbilitySource abilities)
    {
        var alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var bob = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var game = Game.Start(
            Guid.NewGuid(),
            [
                new PlayerSetup(alice, "Alice", 20, TestCards.Deck(40, "Alice")),
                new PlayerSetup(bob, "Bob", 20, TestCards.Deck(40, "Bob")),
            ],
            new GameRandom(1),
            startingPlayerId: alice,
            abilities: abilities);
        game.BeginPlay();
        TestCards.PassToStep(game, TurnStep.PrecombatMain);
        return (game, alice, bob);
    }

    [Fact]
    public void Damage_can_be_prevented_entirely()
    {
        // CR 615.1: prevention is a replacement effect that replaces the damage with nothing.
        var shield = new ReplacementEffectDefinition
        {
            Id = "prevent-all",
            Applies = (e, state, source) => e is DamageMarked,
            Replace = (e, state, source) => [],
        };
        var (game, alice, _) = InMainPhase(new Abilities(("shield", shield)));
        game.Create(alice, TestCards.Shield(), Zone.Battlefield);
        var bear = game.Create(alice, TestCards.Creature("Bear", 2, 2), Zone.Battlefield);

        game.MarkDamage(bear, 5);
        game.PassPriority(alice);

        Assert.Equal(0, game.State.GetObject(bear).Permanent!.DamageMarked);
        Assert.Equal(2, game.State.Battlefield.Count);
    }

    [Fact]
    public void A_prevented_event_never_happened()
    {
        // CR 603.2g: an event that is replaced does not trigger anything. The log records that
        // the replacement occurred, and no DamageMarked was ever applied.
        var shield = new ReplacementEffectDefinition
        {
            Id = "prevent-all",
            Applies = (e, state, source) => e is DamageMarked,
            Replace = (e, state, source) => [],
        };
        var (game, alice, _) = InMainPhase(new Abilities(("shield", shield)));
        game.Create(alice, TestCards.Shield(), Zone.Battlefield);
        var bear = game.Create(alice, TestCards.Creature("Bear", 2, 2), Zone.Battlefield);

        game.MarkDamage(bear, 5);

        Assert.DoesNotContain(game.Log, e => e is DamageMarked);
        Assert.Contains(game.Log, e => e is EventReplaced);
    }

    [Fact]
    public void Damage_can_be_reduced_rather_than_prevented()
    {
        var shield = new ReplacementEffectDefinition
        {
            Id = "prevent-2",
            Applies = (e, state, source) => e is DamageMarked { Amount: > 2 },
            Replace = (e, state, source) =>
                [new DamageMarked(((DamageMarked)e).Id, ((DamageMarked)e).Amount - 2)],
        };
        var (game, alice, _) = InMainPhase(new Abilities(("shield", shield)));
        game.Create(alice, TestCards.Shield(), Zone.Battlefield);
        var bear = game.Create(alice, TestCards.Creature("Bear", 5, 5), Zone.Battlefield);

        game.MarkDamage(bear, 5);

        Assert.Equal(3, game.State.GetObject(bear).Permanent!.DamageMarked);
    }

    [Fact]
    public void A_replacement_applies_only_once_to_the_same_event()
    {
        // CR 614.5. This effect replaces damage with less damage; without the once-only rule it
        // would replace its own output forever and never terminate.
        var shield = new ReplacementEffectDefinition
        {
            Id = "prevent-1",
            Applies = (e, state, source) => e is DamageMarked { Amount: > 0 },
            Replace = (e, state, source) =>
                [new DamageMarked(((DamageMarked)e).Id, ((DamageMarked)e).Amount - 1)],
        };
        var (game, alice, _) = InMainPhase(new Abilities(("shield", shield)));
        game.Create(alice, TestCards.Shield(), Zone.Battlefield);
        var bear = game.Create(alice, TestCards.Creature("Bear", 9, 9), Zone.Battlefield);

        game.MarkDamage(bear, 5);

        Assert.Equal(4, game.State.GetObject(bear).Permanent!.DamageMarked);
    }

    [Fact]
    public void A_permanent_can_enter_with_counters_on_it()
    {
        // CR 614.1c: "as this enters" is a replacement effect, applied to the event of it
        // entering rather than a trigger that happens afterwards. The difference is visible: the
        // creature is never on the battlefield without its counters.
        var entersWithCounters = new ReplacementEffectDefinition
        {
            Id = "enters-with-two",
            FunctionsFrom = Zone.Stack,
            Applies = (e, state, source) =>
                e is ObjectMoved { To: Zone.Battlefield } m && m.OldId == source.Id,
            Replace = (e, state, source) =>
            {
                var move = (ObjectMoved)e;
                return
                [
                    move,
                    new CountersChanged(move.NewId, CounterKinds.PlusOnePlusOne, 2),
                ];
            },
        };
        var (game, alice, bob) = InMainPhase(new Abilities(("grower", entersWithCounters)));
        var card = TestCards.PutInHand(game, alice, TestCards.Grower());

        game.CastSpell(alice, card);
        game.PassPriority(alice);
        game.PassPriority(bob);

        var permanent = game.State.GetObject(game.State.Battlefield.Single());
        Assert.Equal(2, permanent.Permanent!.Counters[CounterKinds.PlusOnePlusOne]);
        Assert.Equal(3, game.CharacteristicsOf(permanent.Id).Power);
    }

    [Fact]
    public void Dying_can_be_replaced_with_exile()
    {
        // CR 614.1b: "if it would die, exile it instead".
        var exileInstead = new ReplacementEffectDefinition
        {
            Id = "exile-instead",
            Applies = (e, state, source) =>
                e is ObjectMoved { From: Zone.Battlefield, To: Zone.Graveyard },
            Replace = (e, state, source) =>
            {
                var move = (ObjectMoved)e;
                return [move with { To = Zone.Exile }];
            },
        };
        var (game, alice, _) = InMainPhase(new Abilities(("shield", exileInstead)));
        game.Create(alice, TestCards.Shield(), Zone.Battlefield);
        var bear = game.Create(alice, TestCards.Creature("Bear", 2, 2), Zone.Battlefield);

        game.Move(bear, Zone.Graveyard, MoveCause.Destroy);

        Assert.Empty(game.State.GetPlayer(alice).Graveyard);
        Assert.Single(game.State.Exile);
    }

    [Fact]
    public void A_replacement_does_not_apply_from_the_wrong_zone()
    {
        // CR 614.6.
        var shield = new ReplacementEffectDefinition
        {
            Id = "prevent-all",
            Applies = (e, state, source) => e is DamageMarked,
            Replace = (e, state, source) => [],
        };
        var (game, alice, _) = InMainPhase(new Abilities(("shield", shield)));
        TestCards.PutInHand(game, alice, TestCards.Shield());
        var bear = game.Create(alice, TestCards.Creature("Bear", 5, 5), Zone.Battlefield);

        game.MarkDamage(bear, 3);

        Assert.Equal(3, game.State.GetObject(bear).Permanent!.DamageMarked);
    }

    [Fact]
    public void A_game_with_replacement_effects_still_replays()
    {
        var exileInstead = new ReplacementEffectDefinition
        {
            Id = "exile-instead",
            Applies = (e, state, source) =>
                e is ObjectMoved { From: Zone.Battlefield, To: Zone.Graveyard },
            Replace = (e, state, source) => [((ObjectMoved)e) with { To = Zone.Exile }],
        };
        var (game, alice, _) = InMainPhase(new Abilities(("shield", exileInstead)));
        game.Create(alice, TestCards.Shield(), Zone.Battlefield);
        var bear = game.Create(alice, TestCards.Creature("Bear", 2, 2), Zone.Battlefield);
        game.Move(bear, Zone.Graveyard, MoveCause.Destroy);
        TestCards.PassToTurn(game, 2);

        Assert.Equal(game.State, GameReducer.Replay(game.Log));
    }
}
