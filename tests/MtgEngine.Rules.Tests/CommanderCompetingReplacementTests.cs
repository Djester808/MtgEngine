using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Interpreter;
using MtgEngine.Rules.Layers;
using MtgEngine.Rules.Replacement;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Tests;

public sealed class CommanderCompetingReplacementTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void Commander_return_participates_in_ordering_and_preserves_the_other_effect(bool commanderFirst, bool returns)
    {
        var owner = new PlayerId(0);
        var controller = new PlayerId(1);
        var zones = new ZoneSystem();
        var layers = new LayerSystem();
        var query = new Query.Query(layers);
        var forward = new ForwardingInterpreter();
        var replacements = new ReplacementSystem(query, layers, forward);
        forward.Target = new EffectInterpreter(query, zones, replacements, layers);
        zones.Replacements = replacements;
        var leader = new CardDefinition("leader", "Leader", Layout.Normal,
            [new Face("Leader", FaceRole.Main, new TypeLine(CardType.Creature))]);
        var ward = new CardDefinition("ward", "Ward", Layout.Normal,
            [new Face("Ward", FaceRole.Main, new TypeLine(CardType.Enchantment), Abilities:
                [new ReplacementAbility(new WouldBePutIntoZone(ObjectFilter.Creature, Zone.Hand),
                    new ReplaceWith(new GainLife(new Fixed(2)))) { Text = "Gain life instead" }])]);
        var state = GameReducer.Replay(
        [
            new GameStarted([new PlayerStart("A", [], []), new PlayerStart("B", [], [])], 1, 40, "commander"),
            new ObjectCreated(new GameObject(new ObjectId(1), leader, 0, owner, controller, ZoneKey.Shared(Zone.Battlefield), 1) { IsCommander = true }, -1),
            new ObjectCreated(new GameObject(new ObjectId(2), ward, 0, controller, controller, ZoneKey.Shared(Zone.Battlefield), 2), -1),
        ]);
        var halted = zones.Move(state, new ObjectId(1), ZoneKey.Of(Zone.Hand, owner));
        var order = Assert.IsType<PendingChoice>(halted.State.Pending);
        Assert.Equal(controller, order.Player);
        Assert.Equal(2, order.Options.Count);
        var selected = Assert.Single(order.Options, o => commanderFirst ? o.Label.Contains("Commander", StringComparison.Ordinal) : o.Label == "Gain life instead");
        var ordered = replacements.Resume(new ChoiceAnswered(order.Id, order.Player, [selected.Id]).Apply(halted.State),
            new Resumption(order.Kind, order.Player, [selected], order.Context));
        if (commanderFirst)
        {
            var decision = Assert.IsType<PendingChoice>(ordered.State.Pending);
            Assert.Equal(owner, decision.Player);
            var option = Assert.Single(decision.Options, o => o.Id == (returns ? "return" : "keep"));
            ordered = replacements.Resume(new ChoiceAnswered(decision.Id, owner, [option.Id]).Apply(ordered.State),
                new Resumption(decision.Kind, owner, [option], decision.Context));
        }

        Assert.Null(ordered.State.Pending);
        Assert.Equal(returns ? Zone.Command : Zone.Battlefield,
            Assert.Single(ordered.State.Objects.Values, card => card.IsCommander).Zone.Zone);
        Assert.Equal(returns ? 40 : 42, ordered.State.Player(controller).Life);
        Assert.Equal(40, ordered.State.Player(owner).Life);
    }
}
