using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Interpreter;
using MtgEngine.Rules.Layers;
using MtgEngine.Rules.Replacement;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Tests;

public sealed class LibraryDrawContinuationTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public void Library_draw_limits_stop_forbidden_draws_before_movement_or_empty_draw_failure(bool empty, int limit)
    {
        var (zones, _) = Engine();
        var owner = new PlayerId(0);
        var state = Start(empty, false);
        state = new ObjectCeasedToExist(new ObjectId(3)).Apply(state);
        state = new ObjectCeasedToExist(new ObjectId(4)).Apply(state);
        var restriction = Definition("draw-limit", CardType.Enchantment,
            new StaticAbility(new CantDrawMoreThan(new You(), new Fixed(limit))));
        state = new ObjectCreated(new GameObject(new ObjectId(6), restriction, 0, owner, owner,
            ZoneKey.Shared(Zone.Battlefield), 6), -1).Apply(state);
        var drawn = zones.Draw(state, owner, 2);
        Assert.Equal(limit, drawn.State.In(ZoneKey.Of(Zone.Hand, owner)).Count);
        Assert.Equal(limit, drawn.State.Player(owner).ObjectsDrawnThisTurn);
        Assert.False(drawn.State.Player(owner).DrewFromEmptyLibrary);
    }

    [Fact]
    public void Standalone_multi_draw_resumes_each_remaining_draw_after_reload()
    {
        var (zones, interpreter) = Engine();
        var initial = Start(false, false);
        var first = zones.Draw(initial, new PlayerId(0), 2);
        Assert.Empty(Choices.ChoiceSecrets.Withheld(first.State.Pending)!.Context.DeferredDraws);
        var state = GameReducer.Apply(initial, EventLogSerializer.Read(EventLogSerializer.Write(first.Events)));
        var choices = new Choices.ChoiceSystem(zones, new ChoiceResumptionFakeTurn(), interpreter);
        var pending = state.Pending!;
        var second = choices.Answer(state, new Commands.AnswerChoice(pending.Player, pending.Id,
            [pending.Options.Single(option => option.Label == "Gain 2 instead").Id]));
        Assert.NotNull(second.State.Pending);
        state = GameReducer.Apply(state, EventLogSerializer.Read(EventLogSerializer.Write(second.Events)));
        pending = state.Pending!;
        var final = choices.Answer(state, new Commands.AnswerChoice(pending.Player, pending.Id,
            [pending.Options.Single(option => option.Label == "Gain 2 instead").Id]));
        Assert.Null(final.State.Pending);
        Assert.Equal(6, final.State.Player(new PlayerId(0)).Life);
        Assert.Equal(0, final.State.Player(new PlayerId(0)).ObjectsDrawnThisTurn);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Draws_wait_for_each_replacement_and_preserve_the_original_amount(bool empty, bool dynamicAmount)
    {
        var (zones, interpreter) = Engine();
        var state = Start(empty, dynamicAmount);
        var first = interpreter.Resolve(state, new ObjectId(5));
        Assert.Equal(2, first.State.Player(new PlayerId(0)).Life);
        Assert.DoesNotContain(first.Events, e => e is StackObjectResolved);
        Assert.Equal("replacement-order", first.State.Pending!.Kind);

        // Rebuild the game and services between answers: continuation must live in the log.
        state = GameReducer.Apply(state, EventLogSerializer.Read(EventLogSerializer.Write(first.Events)));
        var second = Answer(Engine().Interpreter, state);
        Assert.Equal(4, second.State.Player(new PlayerId(0)).Life);
        Assert.Equal("replacement-order", second.State.Pending!.Kind);
        Assert.DoesNotContain(second.Events, e => e is StackObjectResolved);
        var final = Answer(Engine().Interpreter, GameReducer.Apply(state, EventLogSerializer.Read(EventLogSerializer.Write(second.Events))));
        Assert.Null(final.State.Pending);
        Assert.Equal(7, final.State.Player(new PlayerId(0)).Life);
        Assert.False(final.State.Player(new PlayerId(0)).DrewFromEmptyLibrary);
        Assert.Equal(0, final.State.Player(new PlayerId(0)).ObjectsDrawnThisTurn);
        Assert.Single(final.Events.OfType<StackObjectResolved>());
        Assert.Empty(final.State.In(ZoneKey.Of(Zone.Hand, new PlayerId(0))));
    }

    [Fact]
    public void A_resumed_draw_that_is_no_longer_replaced_moves_the_card_before_recording_the_draw()
    {
        var (zones, interpreter) = Engine();
        var initial = Start(false, false);
        var started = interpreter.Resolve(initial, new ObjectId(5));
        var state = GameReducer.Apply(initial, EventLogSerializer.Read(EventLogSerializer.Write(started.Events)));
        // Exercise the replacement contract when the offered candidates no longer apply.
        state = zones.Move(state, new ObjectId(3), ZoneKey.Of(Zone.Graveyard, new PlayerId(0))).State;
        state = zones.Move(state, new ObjectId(4), ZoneKey.Of(Zone.Graveyard, new PlayerId(0))).State;
        var final = Answer(interpreter, state);
        Assert.Null(final.State.Pending);
        var owner = new PlayerId(0);
        Assert.Empty(final.State.In(ZoneKey.Of(Zone.Library, owner)));
        Assert.Equal(2, final.State.In(ZoneKey.Of(Zone.Hand, owner)).Count);
        Assert.Equal(2, final.State.Player(owner).ObjectsDrawnThisTurn);
        Assert.Equal(3, final.State.Player(owner).Life);
        var events = final.Events.ToList();
        foreach (var drawn in events.OfType<ObjectDrawn>())
            Assert.InRange(events.FindIndex(e => e is ObjectMoved moved && moved.To.Id == drawn.Object), 0, events.IndexOf(drawn) - 1);
        Assert.Equal(final.State.In(ZoneKey.Of(Zone.Hand, owner)), GameReducer.Apply(state, final.Events).In(ZoneKey.Of(Zone.Hand, owner)));
    }

    private static Outcome Answer(EffectInterpreter interpreter, GameState state)
    {
        var choice = state.Pending!;
        var selected = choice.Options.Single(option => option.Label == "Gain 2 instead");
        return interpreter.Resume(new ChoiceAnswered(choice.Id, choice.Player, [selected.Id]).Apply(state),
            new Resumption(choice.Kind, choice.Player, [selected], choice.Context));
    }

    private static (ZoneSystem Zones, EffectInterpreter Interpreter) Engine()
    {
        var zones = new ZoneSystem();
        var layers = new LayerSystem();
        var query = new Query.Query(layers);
        var forward = new ForwardingInterpreter();
        var replacements = new ReplacementSystem(query, layers, forward);
        var interpreter = new EffectInterpreter(query, zones, replacements, layers);
        forward.Target = interpreter;
        zones.Replacements = replacements;
        return (zones, interpreter);
    }

    private static GameState Start(bool empty, bool dynamicAmount)
    {
        var owner = new PlayerId(0);
        var spell = Definition("spell", CardType.Instant, new SpellAbility(new Sequence([
            new Draw(dynamicAmount ? new LifeOf(new You()) : new Fixed(2)), new GainLife(new Fixed(1))])));
        var events = new List<GameEvent>
        {
            new GameStarted([new PlayerStart("A", [], []), new PlayerStart("B", [], [])], 1, 2, "commander"),
        };
        if (!empty)
            for (var id = 1; id <= 2; id++)
                events.Add(new ObjectCreated(new GameObject(new ObjectId(id), Definition("card", CardType.Land), 0,
                    owner, owner, ZoneKey.Of(Zone.Library, owner), id), -1));
        for (var id = 3; id <= 4; id++)
        {
            var amount = id - 1;
            var ward = Definition("ward" + id, CardType.Enchantment,
                new ReplacementAbility(new WouldDraw(new You()), new ReplaceWith(new GainLife(new Fixed(amount))))
                { Text = $"Gain {amount} instead" });
            events.Add(new ObjectCreated(new GameObject(new ObjectId(id), ward, 0, owner, owner, ZoneKey.Shared(Zone.Battlefield), id), -1));
        }
        events.Add(new ObjectCreated(new GameObject(new ObjectId(5), spell, 0, owner, owner, ZoneKey.Shared(Zone.Stack), 5)
        { Stack = new StackEntry(null, null, [], [], [], null, false) }, -1));
        return GameReducer.Replay(events);
    }

    private static CardDefinition Definition(string name, CardType type, params Ability[] abilities) =>
        new(name, name, Layout.Normal, [new Face(name, FaceRole.Main, new TypeLine(type), Abilities: abilities)]);
}
