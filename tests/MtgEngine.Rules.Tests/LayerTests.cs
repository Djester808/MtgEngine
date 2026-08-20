using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The layer system (CR 613).
/// </summary>
/// <remarks>
/// This is the file the rebuild exists for. The previous engine's static abilities were
/// <c>IStaticAbility.Apply(state) =&gt; state</c> — they wrote the bonus into the creature — so
/// when the lord granting it left the battlefield, nothing took it back off. The first test here
/// is that exact scenario, and it can only pass in a design where characteristics are computed
/// rather than stored.
/// </remarks>
public sealed class LayerTests
{
    private sealed class Abilities(params (string Fragment, ContinuousEffectDefinition Effect)[] statics)
        : IAbilitySource
    {
        private readonly Dictionary<string, ContinuousEffectDefinition> _floating = [];

        public IReadOnlyList<TriggeredAbilityDefinition> TriggersOf(CardDefinition card) => [];

        public IReadOnlyList<ContinuousEffectDefinition> StaticsOf(CardDefinition card) =>
            [.. statics.Where(s => card.OracleId.Contains(s.Fragment, StringComparison.Ordinal))
                .Select(s => s.Effect)];

        public ContinuousEffectDefinition? FloatingEffect(string definitionId) =>
            _floating.GetValueOrDefault(definitionId);

        public Abilities WithFloating(ContinuousEffectDefinition effect)
        {
            _floating[effect.Id] = effect;
            return this;
        }
    }

    /// <summary>"Other creatures you control get +1/+1" — the shape that broke the old engine.</summary>
    private static ContinuousEffectDefinition Lord(string id = "lord", int power = 1, int toughness = 1) => new()
    {
        Id = id,
        Layer = EffectLayer.PowerToughnessModify,
        Applies = (state, source, target) =>
            source is not null
            && target.Id != source.Id
            && target.Zone == Zone.Battlefield
            && target.ControllerId == source.ControllerId
            && target.Card.CardTypes.HasFlag(CardType.Creature),
        Apply = builder => builder.Modify(power, toughness),
    };

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
        game.BeginPlay(withMulligans: false);
        TestCards.PassToStep(game, TurnStep.PrecombatMain);
        return (game, alice, bob);
    }

    [Fact]
    public void A_lords_bonus_goes_away_with_the_lord()
    {
        // The bug this engine was rebuilt to make impossible. The old one wrote the +1/+1 into
        // the creature, so a 2/2 stayed 3/3 after its lord died.
        var (game, alice, _) = InMainPhase(new Abilities(("lord", Lord())));
        var lord = game.Create(alice, TestCards.Lord(), Zone.Battlefield);
        var bear = game.Create(alice, TestCards.Creature("Bear", 2, 2), Zone.Battlefield);

        Assert.Equal(3, game.CharacteristicsOf(bear).Power);

        game.Move(lord, Zone.Graveyard, MoveCause.Destroy);

        Assert.Equal(2, game.CharacteristicsOf(bear).Power);
        Assert.Equal(2, game.CharacteristicsOf(bear).Toughness);
    }

    [Fact]
    public void A_lord_does_not_pump_itself()
    {
        var (game, alice, _) = InMainPhase(new Abilities(("lord", Lord())));
        var lord = game.Create(alice, TestCards.Lord(), Zone.Battlefield);

        Assert.Equal(2, game.CharacteristicsOf(lord).Power);
    }

    [Fact]
    public void A_lord_does_not_pump_another_players_creatures()
    {
        var (game, alice, bob) = InMainPhase(new Abilities(("lord", Lord())));
        game.Create(alice, TestCards.Lord(), Zone.Battlefield);
        var theirs = game.Create(bob, TestCards.Creature("Bear", 2, 2), Zone.Battlefield);

        Assert.Equal(2, game.CharacteristicsOf(theirs).Power);
    }

    [Fact]
    public void Two_lords_both_apply()
    {
        var (game, alice, _) = InMainPhase(new Abilities(("lord", Lord())));
        game.Create(alice, TestCards.Lord("First"), Zone.Battlefield);
        game.Create(alice, TestCards.Lord("Second"), Zone.Battlefield);
        var bear = game.Create(alice, TestCards.Creature("Bear", 2, 2), Zone.Battlefield);

        Assert.Equal(4, game.CharacteristicsOf(bear).Power);
    }

    [Fact]
    public void Counters_modify_power_and_toughness()
    {
        // CR 613.4c: counters apply in layer 7c, alongside other modifying effects.
        var (game, alice, _) = InMainPhase(new Abilities());
        var bear = game.Create(alice, TestCards.Creature("Bear", 2, 2), Zone.Battlefield);

        game.ChangeCounters(bear, CounterKinds.PlusOnePlusOne, 2);

        Assert.Equal(4, game.CharacteristicsOf(bear).Power);
        Assert.Equal(4, game.CharacteristicsOf(bear).Toughness);
    }

    [Fact]
    public void Setting_power_and_toughness_happens_before_modifying_it()
    {
        // CR 613.4b before 613.4c — the rules' own example. A creature that "becomes 0/1" with a
        // +1/+1 counter on it is 1/2, not 0/1: the counter applies after the setting effect,
        // whatever order they arrived in.
        var abilities = new Abilities().WithFloating(new ContinuousEffectDefinition
        {
            Id = "becomes-0-1",
            Layer = EffectLayer.PowerToughnessSet,
            Applies = (_, _, _) => true,
            Apply = builder => builder.Set(0, 1),
        });
        var (game, alice, _) = InMainPhase(abilities);
        var bear = game.Create(alice, TestCards.Creature("Bear", 2, 2), Zone.Battlefield);

        game.ChangeCounters(bear, CounterKinds.PlusOnePlusOne, 1);
        game.CreateContinuousEffect("becomes-0-1", [bear]);

        Assert.Equal(1, game.CharacteristicsOf(bear).Power);
        Assert.Equal(2, game.CharacteristicsOf(bear).Toughness);
    }

    [Fact]
    public void The_rules_own_worked_example_comes_out_right()
    {
        // CR 613.5's example, exactly: a 2/2, a +1/+1 counter, a +4/+4 until end of turn, a
        // +0/+2 from an enchantment, and then "becomes 0/1". The answer is 5/8, and it is 5/8
        // only if layer 7b is applied before every 7c effect regardless of arrival order.
        var abilities = new Abilities(("anthem", new ContinuousEffectDefinition
        {
            Id = "anthem",
            Layer = EffectLayer.PowerToughnessModify,
            Applies = (state, source, target) =>
                source is not null && target.Zone == Zone.Battlefield
                && target.ControllerId == source.ControllerId
                && target.Card.CardTypes.HasFlag(CardType.Creature),
            Apply = builder => builder.Modify(0, 2),
        }))
            .WithFloating(new ContinuousEffectDefinition
            {
                Id = "pump",
                Layer = EffectLayer.PowerToughnessModify,
                Applies = (_, _, _) => true,
                Apply = builder => builder.Modify(4, 4),
            })
            .WithFloating(new ContinuousEffectDefinition
            {
                Id = "becomes-0-1",
                Layer = EffectLayer.PowerToughnessSet,
                Applies = (_, _, _) => true,
                Apply = builder => builder.Set(0, 1),
            });

        var (game, alice, _) = InMainPhase(abilities);
        var ogre = game.Create(alice, TestCards.Creature("Gray Ogre", 2, 2), Zone.Battlefield);

        game.ChangeCounters(ogre, CounterKinds.PlusOnePlusOne, 1);
        game.CreateContinuousEffect("pump", [ogre]);
        game.Create(alice, TestCards.Anthem(), Zone.Battlefield);
        game.CreateContinuousEffect("becomes-0-1", [ogre]);

        var computed = game.CharacteristicsOf(ogre);
        Assert.Equal(5, computed.Power);
        Assert.Equal(8, computed.Toughness);
    }

    [Fact]
    public void Switching_power_and_toughness_happens_last()
    {
        // CR 613.4d, and the rules' example: a 1/3 given +0/+1 then switched is 4/1.
        var abilities = new Abilities()
            .WithFloating(new ContinuousEffectDefinition
            {
                Id = "plus-0-1",
                Layer = EffectLayer.PowerToughnessModify,
                Applies = (_, _, _) => true,
                Apply = builder => builder.Modify(0, 1),
            })
            .WithFloating(new ContinuousEffectDefinition
            {
                Id = "switch",
                Layer = EffectLayer.PowerToughnessSwitch,
                Applies = (_, _, _) => true,
                Apply = builder => builder.Switch(),
            });

        var (game, alice, _) = InMainPhase(abilities);
        var creature = game.Create(alice, TestCards.Creature("Wall", 1, 3), Zone.Battlefield);

        game.CreateContinuousEffect("switch", [creature]);
        game.CreateContinuousEffect("plus-0-1", [creature]);

        var computed = game.CharacteristicsOf(creature);
        Assert.Equal(4, computed.Power);
        Assert.Equal(1, computed.Toughness);
    }

    [Fact]
    public void A_type_changing_effect_applies_before_power_and_toughness()
    {
        // CR 613.1d before 613.4: a land that becomes a creature can then be pumped by an anthem
        // that only sees creatures.
        var abilities = new Abilities(("anthem", new ContinuousEffectDefinition
        {
            Id = "anthem",
            Layer = EffectLayer.PowerToughnessModify,
            // Reads the printed type, which is what makes the ordering observable: the anthem
            // is written against creatures and the land only became one in layer 4.
            Applies = (state, source, target) =>
                source is not null && target.Zone == Zone.Battlefield && target.Id != source.Id,
            Apply = builder =>
            {
                if (builder.CardTypes.HasFlag(CardType.Creature))
                    builder.Modify(1, 1);
            },
        }))
            .WithFloating(new ContinuousEffectDefinition
            {
                Id = "animate",
                Layer = EffectLayer.Type,
                Applies = (_, _, _) => true,
                Apply = builder =>
                {
                    builder.CardTypes |= CardType.Creature;
                    builder.Power = 3;
                    builder.Toughness = 3;
                },
            });

        var (game, alice, _) = InMainPhase(abilities);
        var land = game.Create(alice, TestCards.BasicLand(), Zone.Battlefield);
        game.Create(alice, TestCards.Anthem(), Zone.Battlefield);

        Assert.False(game.CharacteristicsOf(land).IsCreature);
        game.CreateContinuousEffect("animate", [land]);

        var computed = game.CharacteristicsOf(land);
        Assert.True(computed.IsCreature);
        Assert.Equal(4, computed.Power);
    }

    [Fact]
    public void An_effect_that_grants_a_keyword_applies_in_layer_six()
    {
        // CR 613.1f.
        var abilities = new Abilities().WithFloating(new ContinuousEffectDefinition
        {
            Id = "grant-flying",
            Layer = EffectLayer.Ability,
            Applies = (_, _, _) => true,
            Apply = builder => builder.Keywords |= KeywordAbility.Flying,
        });
        var (game, alice, _) = InMainPhase(abilities);
        var bear = game.Create(alice, TestCards.Creature("Bear", 2, 2), Zone.Battlefield);

        Assert.False(game.CharacteristicsOf(bear).Has(KeywordAbility.Flying));
        game.CreateContinuousEffect("grant-flying", [bear]);

        Assert.True(game.CharacteristicsOf(bear).Has(KeywordAbility.Flying));
    }

    [Fact]
    public void Until_end_of_turn_effects_end_during_cleanup()
    {
        // CR 514.2, and not at the beginning of the end step — the difference decides whether a
        // creature pumped this turn is still big when the turn's last combat damage is dealt.
        var abilities = new Abilities().WithFloating(new ContinuousEffectDefinition
        {
            Id = "pump",
            Layer = EffectLayer.PowerToughnessModify,
            Applies = (_, _, _) => true,
            Apply = builder => builder.Modify(3, 3),
        });
        var (game, alice, _) = InMainPhase(abilities);
        var bear = game.Create(alice, TestCards.Creature("Bear", 2, 2), Zone.Battlefield);
        game.CreateContinuousEffect("pump", [bear]);

        Assert.Equal(5, game.CharacteristicsOf(bear).Power);

        TestCards.PassToStep(game, TurnStep.End);
        Assert.Equal(5, game.CharacteristicsOf(bear).Power);

        TestCards.PassToTurn(game, 2);
        Assert.Equal(2, game.CharacteristicsOf(bear).Power);
        Assert.Empty(game.State.FloatingEffects);
    }

    [Fact]
    public void State_based_actions_see_the_computed_toughness()
    {
        // A creature at 2 toughness with 2 damage lives if a lord is making it 3/3, and dies the
        // moment the lord does. Only true because state-based actions ask for the computed value
        // rather than the printed one (CR 704.5g).
        var (game, alice, _) = InMainPhase(new Abilities(("lord", Lord())));
        var lord = game.Create(alice, TestCards.Lord(), Zone.Battlefield);
        var bear = game.Create(alice, TestCards.Creature("Bear", 2, 2), Zone.Battlefield);

        game.MarkDamage(bear, 2);
        game.PassPriority(alice);
        Assert.Equal(2, game.State.Battlefield.Count);

        game.Move(lord, Zone.Graveyard, MoveCause.Destroy);
        game.PassPriority(game.State.Priority.Holder!.Value);

        Assert.Empty(game.State.Battlefield);
    }

    [Fact]
    public void A_game_with_continuous_effects_still_replays()
    {
        var abilities = new Abilities(("lord", Lord())).WithFloating(new ContinuousEffectDefinition
        {
            Id = "pump",
            Layer = EffectLayer.PowerToughnessModify,
            Applies = (_, _, _) => true,
            Apply = builder => builder.Modify(3, 3),
        });
        var (game, alice, bob) = InMainPhase(abilities);
        game.Create(alice, TestCards.Lord(), Zone.Battlefield);
        var bear = game.Create(alice, TestCards.Creature("Bear", 2, 2), Zone.Battlefield);
        game.CreateContinuousEffect("pump", [bear]);
        game.ChangeCounters(bear, CounterKinds.PlusOnePlusOne, 1);
        TestCards.PassToTurn(game, 2);

        Assert.Equal(game.State, GameReducer.Replay(game.Log));
    }
}
