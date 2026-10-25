using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

[Trait("Scope", "Deck")]
public sealed class MultiplayerLibrarySearchTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(false, true)]
    public void All_searchers_inspect_privately_then_choose_in_apnap_order_before_any_cards_move(bool oneFindsNothing, bool replacement = false)
    {
        var log = new List<GameEvent>
        {
            new GameStarted([.. Enumerable.Range(0, 4).Select(i => new PlayerStart($"Seat {i}", [], []))], 42, 40, "commander"),
            new TurnBegan(1, new PlayerId(2), false), new StepBegan(Step.PrecombatMain), new PriorityGranted(new PlayerId(2)),
        };
        for (var seat = 0; seat < 4; seat++)
            for (var index = 0; index < 2; index++)
            {
                var id = seat * 2 + index + 1;
                var card = InterpreterTable.Definition(index == 0 ? CardType.Creature : CardType.Land)
                    with
                { OracleId = $"library-seat-{seat}" };
                log.Add(new ObjectCreated(new GameObject(new ObjectId(id), card, 0, new PlayerId(seat), new PlayerId(seat),
                    ZoneKey.Of(Zone.Library, new PlayerId(seat)), id), -1));
            }
        log.Add(new ObjectCreated(new GameObject(new ObjectId(9), InterpreterTable.Definition(CardType.Instant,
            new SpellAbility(new Search(ObjectFilter.Creature, Zone.Hand, Who: new EachPlayer()))),
            0, new PlayerId(0), new PlayerId(0), ZoneKey.Shared(Zone.Stack), 9)
        { Stack = new StackEntry(null, null, [], [], [], null, false) }, -1));
        if (replacement)
            foreach (var amount in new[] { 2, 3 })
            {
                var ward = InterpreterTable.Definition(CardType.Enchantment,
                    new ReplacementAbility(new WouldBePutIntoZone(ObjectFilter.Creature with { In = Zone.Library, OwnedBy = new You() }, Zone.Hand),
                        new ReplaceWith(new GainLife(new Fixed(amount))))
                    { Text = $"Gain {amount} instead" });
                log.Add(new ObjectCreated(new GameObject(new ObjectId(amount + 8), ward, 0, new PlayerId(1), new PlayerId(1),
                    ZoneKey.Shared(Zone.Battlefield), amount + 8), -1));
            }
        var initial = GameReducer.Replay(log);
        var started = Subsystems.Wave2().Interpreter.Resolve(initial, new ObjectId(9));
        var events = started.Events;
        Assert.Equal(4, events.OfType<LibrarySearched>().Count());
        Assert.Equal(4, events.OfType<CardsLookedAt>().Count());
        var state = GameReducer.Apply(initial, EventLogSerializer.Read(EventLogSerializer.Write(events)));
        foreach (var seat in new[] { 2, 3, 0, 1 })
        {
            var pending = Assert.IsType<PendingChoice>(state.Pending);
            Assert.Equal(new PlayerId(seat), pending.Player);
            Assert.Single(pending.Options);
            Assert.Equal(new ObjectId(seat * 2 + 1), pending.Options[0].Object);
            Assert.DoesNotContain(events, e => e is ObjectMoved or ZoneReordered);
            var engine = Subsystems.Wave2();
            for (var viewer = 0; viewer < 4; viewer++)
            {
                var view = engine.Views.Project(state, new PlayerId(viewer));
                Assert.Equal(viewer == seat ? 1 : 0, view.ChoiceCards.Count);
                Assert.Equal(2, view.InspectedLibraryCards.Count);
                Assert.All(view.InspectedLibraryCards, card =>
                {
                    Assert.Equal(new PlayerId(viewer), card.Owner);
                    Assert.Equal($"library-seat-{viewer}", card.OracleId);
                });
                Assert.Equal(2, view.LastLook!.Cards.Count);
                Assert.All(view.LastLook.Cards, card => Assert.Equal($"library-seat-{viewer}", card.OracleId));
                Assert.Equal(2, state.In(ZoneKey.Of(Zone.Library, new PlayerId(viewer))).Count);
                Assert.DoesNotContain(view.Pending!.Context.Extra.Keys, key =>
                    key.StartsWith("remembered:", StringComparison.Ordinal) || key.StartsWith(":search-plan:", StringComparison.Ordinal));
            }
            var answer = engine.Choices.Answer(state, new AnswerChoice(pending.Player, pending.Id,
                oneFindsNothing && seat == 3 ? [] : [pending.Options[0].Id]));
            events = answer.Events;
            state = GameReducer.Apply(state, EventLogSerializer.Read(EventLogSerializer.Write(events)));
        }
        if (replacement)
        {
            var pending = Assert.IsType<PendingChoice>(state.Pending);
            Assert.Equal("replacement-order", pending.Kind);
            Assert.Contains(pending.Context.Extra.Keys, key =>
                key.StartsWith(":search-plan:", StringComparison.Ordinal) && key.EndsWith(":moves", StringComparison.Ordinal));
            for (var viewer = 0; viewer < 4; viewer++)
            {
                var view = Subsystems.Wave2().Views.Project(state, new PlayerId(viewer));
                Assert.Equal(viewer == 1 ? 2 : 1, view.InspectedLibraryCards.Count);
                Assert.All(view.InspectedLibraryCards, card =>
                    Assert.Contains(card.Id, state.In(ZoneKey.Of(Zone.Library, new PlayerId(viewer)))));
                Assert.DoesNotContain(view.Pending!.Context.Extra.Keys,
                    key => key.StartsWith(":search-plan:", StringComparison.Ordinal));
            }
            Assert.DoesNotContain(events, e => e is ZoneReordered { Shuffle: true });
            var answer = Subsystems.Wave2().Choices.Answer(state, new AnswerChoice(pending.Player, pending.Id,
                [pending.Options.Single(option => option.Label == "Gain 2 instead").Id]));
            events = answer.Events;
            state = GameReducer.Apply(state, EventLogSerializer.Read(EventLogSerializer.Write(events)));
            Assert.Equal(42, state.Player(new PlayerId(1)).Life);
            Assert.DoesNotContain(events.OfType<ObjectMoved>(), moved => moved.From.Value < 9);
        }
        Assert.Null(state.Pending);
        Assert.Empty(state.LibraryInspections);
        Assert.Equal(4, events.OfType<ZoneReordered>().Count(e => e.Shuffle));
        for (var seat = 0; seat < 4; seat++)
        {
            var found = (oneFindsNothing && seat == 3) || (replacement && seat == 1) ? 0 : 1;
            Assert.Equal(found, state.In(ZoneKey.Of(Zone.Hand, new PlayerId(seat))).Count);
            Assert.Equal(2 - found, state.In(ZoneKey.Of(Zone.Library, new PlayerId(seat))).Count);
            var view = Subsystems.Wave2().Views.Project(state, new PlayerId(seat));
            Assert.Empty(view.InspectedLibraryCards);
            Assert.Equal(2, view.LastLook!.Cards.Count);
        }
        Assert.DoesNotContain(events, e => e is LibrarySearched or CardsLookedAt);
    }
}
