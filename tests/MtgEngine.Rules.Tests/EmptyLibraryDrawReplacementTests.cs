using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Interpreter;
using MtgEngine.Rules.Layers;
using MtgEngine.Rules.Replacement;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Tests;

public sealed class EmptyLibraryDrawReplacementTests
{
    [Fact]
    public void Replacing_empty_library_draws_does_not_mark_a_failed_draw_or_increment_draw_count()
    {
        var owner = new PlayerId(0);
        var zones = new ZoneSystem();
        var layers = new LayerSystem();
        var query = new Query.Query(layers);
        var forward = new ForwardingInterpreter();
        var replacements = new ReplacementSystem(query, layers, forward);
        forward.Target = new EffectInterpreter(query, zones, replacements, layers);
        zones.Replacements = replacements;
        var ward = new CardDefinition("ward", "Ward", Layout.Normal,
            [new Face("Ward", FaceRole.Main, new TypeLine(CardType.Enchantment), Abilities:
                [new ReplacementAbility(new WouldDraw(new You()), new ReplaceWith(new GainLife(new Fixed(2))))])]);
        var state = GameReducer.Replay([
            new GameStarted([new PlayerStart("A", [], []), new PlayerStart("B", [], [])], 1, 20, "commander"),
            new ObjectCreated(new GameObject(new ObjectId(1), ward, 0, owner, owner, ZoneKey.Shared(Zone.Battlefield), 1), -1),
        ]);
        var outcome = zones.Draw(state, owner, 2);
        Assert.Equal(24, outcome.State.Player(owner).Life);
        Assert.False(outcome.State.Player(owner).DrewFromEmptyLibrary);
        Assert.Equal(0, outcome.State.Player(owner).ObjectsDrawnThisTurn);
        Assert.DoesNotContain(outcome.Events, e => e is ObjectDrawn or ObjectMoved or DrawFromEmptyLibraryAttempted);
        Assert.Equal(2, outcome.Events.OfType<LifeChanged>().Count());
        Assert.Equal(outcome.State.Player(owner), GameReducer.Apply(state, outcome.Events).Player(owner));
    }
}
