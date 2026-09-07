using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Interpreter;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// CR 603.7 through the front door: a delayed triggered ability fires, is put on the stack by the
/// trigger system, and its body is what resolves. There is one road in — the ability rides on the
/// definition its stack object resolves from and the entry names its index — so this is played
/// through <see cref="Game.Apply"/> rather than by handing the interpreter a stack entry, which
/// is the only way to prove the two halves agree.
/// </summary>
public sealed partial class InterpreterTests
{
    [Fact]
    [RuleRef("603.7")]
    public void A_delayed_abilitys_body_resolves_through_the_game()
    {
        var game = new Game(Subsystems.Wave2(), new InterpreterNoCards());
        var one = new PlayerId(0);
        var two = new PlayerId(1);
        var state = GameReducer.Replay(
            [new GameStarted([new PlayerStart("one", [], []), new PlayerStart("two", [], [])], 7, 20, "test")]);
        state = game.Engine.Turn.Begin(state).State;

        // CR 603.7: the instruction is held by the game, not printed on a card, and this one has
        // no source object at all — the permanent that made it is long gone.
        state = new DelayedTriggerCreated(new DelayedTriggerState(
            1,
            new LosesLife(new EachPlayer()),
            new GainLife(new Fixed(3)),
            null,
            one,
            Once: true,
            ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty)).Apply(state);

        var lost = Assert.IsType<Applied>(game.Apply(state, new ManualLife(one, one, -1)));
        state = lost.State;

        // CR 603.7b: it fired, so the game no longer holds it, and the ability is on the stack.
        Assert.Empty(state.DelayedTriggers);
        var onStack = Assert.Single(state.In(ZoneKey.Shared(Zone.Stack)));
        Assert.Equal(19, state.Player(one).Life);

        // CR 608.2: both players pass and the top of the stack resolves. Before the firing carried
        // its ability, the object resolved and did nothing at all.
        state = Assert.IsType<Applied>(game.Apply(state, new PassPriority(one))).State;
        var resolved = Assert.IsType<Applied>(game.Apply(state, new PassPriority(two)));
        state = resolved.State;

        Assert.Contains(resolved.Events, e => e is StackObjectResolved r && r.StackObject == onStack);
        Assert.Equal(22, state.Player(one).Life);
    }
}

/// <summary>A game whose cards never come off a store: every object in this file is made by hand.</summary>
internal sealed class InterpreterNoCards : IDefinitionStore
{
    public CardDefinition? ByName(string name) => null;

    public CardDefinition? ByOracleId(string oracleId) => null;
}
