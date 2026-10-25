using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

public sealed class ZoneBatchContinuationTests
{
    [Fact]
    public void Cleanup_does_not_advance_through_pending_replacements_and_finishes_after_the_batch()
    {
        var engine = Subsystems.Wave2();
        var owner = new PlayerId(0);
        var card = new CardDefinition("card", "Card", Layout.Normal,
            [new Face("Card", FaceRole.Main, new TypeLine(CardType.Creature))]);
        // Two harmless fixture replacements force an ordering pause for each discard.
        var replacement = new CardDefinition("replacement", "Replacement", Layout.Normal,
            [new Face("Replacement", FaceRole.Main, new TypeLine(CardType.Enchantment), Abilities:
                [new ReplacementAbility(new WouldBePutIntoZone(new ObjectFilter(In: Zone.Hand, Types: CardType.Creature), Zone.Graveyard), new ModifyAmount(Add: 0))])]);
        var state = GameReducer.Replay(
        [
            new GameStarted([new PlayerStart("A", [], []), new PlayerStart("B", [], [])], 1, 40, "commander"),
            new TurnBegan(1, owner, false), new StepBegan(Step.Cleanup),
            new ObjectCreated(new GameObject(new ObjectId(1), card, 0, owner, owner, ZoneKey.Of(Zone.Hand, owner), 1), -1),
            new ObjectCreated(new GameObject(new ObjectId(2), card, 0, owner, owner, ZoneKey.Of(Zone.Hand, owner), 2), -1),
            new ObjectCreated(new GameObject(new ObjectId(3), replacement, 0, owner, owner, ZoneKey.Shared(Zone.Battlefield), 3), -1),
            new ObjectCreated(new GameObject(new ObjectId(4), replacement, 0, owner, owner, ZoneKey.Shared(Zone.Battlefield), 4), -1),
        ]);
        var discard = new PendingChoice(new ChoiceId(1), owner, "discard", "Discard two",
            [new ChoiceOption("1", "Card", new ObjectId(1)), new ChoiceOption("2", "Card", new ObjectId(2))], 2, 2,
            new ChoiceContext(null, null, null, ImmutableDictionary<string, string>.Empty));
        state = new ChoiceRequested(discard).Apply(state);
        Assert.Empty(engine.Turn.Advance(state).Events);
        var answer = engine.Choices.Answer(state, new AnswerChoice(owner, discard.Id, ["1", "2"]));
        for (var index = 0; index < 2; index++)
        {
            var pending = Assert.IsType<PendingChoice>(answer.State.Pending);
            Assert.Equal(Step.Cleanup, answer.State.Turn.Step);
            Assert.Equal(owner, answer.State.Turn.ActivePlayer);
            Assert.True(pending.Context.AdvanceAfterZoneBatch);
            answer = engine.Choices.Answer(answer.State, new AnswerChoice(pending.Player, pending.Id, [pending.Options[0].Id]));
        }
        Assert.Null(answer.State.Pending);
        Assert.Equal(new PlayerId(1), answer.State.Turn.ActivePlayer);
        Assert.Equal(Step.Upkeep, answer.State.Turn.Step);
        Assert.Equal(2, answer.State.Objects.Values.Count(obj => obj.Zone.Zone == Zone.Graveyard));
    }

    [Fact]
    public void A_direct_batch_survives_two_commander_choices_and_serialized_replay()
    {
        var engine = Subsystems.Wave2();
        var definition = new CardDefinition("fixture", "Fixture", Layout.Normal,
            [new Face("Fixture", FaceRole.Main, new TypeLine(CardType.Creature))]);
        var log = ImmutableList.Create<GameEvent>(new GameStarted(
            [new PlayerStart("A", [], []), new PlayerStart("B", [], [])], 1, 40, "commander"),
            new TurnBegan(1, new PlayerId(0), false), new StepBegan(Step.PrecombatMain), new PriorityGranted(new PlayerId(0)));
        for (var index = 1; index <= 4; index++)
            log = log.Add(new ObjectCreated(new GameObject(new ObjectId(index), definition, 0,
                new PlayerId(index < 3 ? 0 : 1), new PlayerId(0), ZoneKey.Shared(Zone.Battlefield), index)
            { IsCommander = index is 1 or 3 }, -1));
        var requests = Enumerable.Range(1, 4).Select(id => new MoveRequest(new ObjectId(id), ZoneKey.Of(Zone.Hand, new PlayerId(0)))).ToArray();
        var batch = engine.Zones.MoveAll(GameReducer.Replay(log), requests);
        log = log.AddRange(batch.Events);
        Assert.Equal(3, batch.State.Pending!.Context.DeferredZoneMoves.Count);
        Assert.Empty(engine.Views.Project(batch.State, batch.State.Pending.Player).Pending!.Context.DeferredZoneMoves);
        var count = 0;
        while (GameReducer.Replay(log).Pending is { } pending)
        {
            Assert.True(count < 2);
            var restored = GameReducer.Replay(EventLogSerializer.Read(EventLogSerializer.Write(log)));
            var option = count++ == 0 ? "keep" : "return";
            var answered = engine.Choices.Answer(restored, new AnswerChoice(pending.Player, pending.Id, [option]));
            log = log.AddRange(answered.Events);
        }

        var final = GameReducer.Replay(log);
        Assert.Equal(2, count);
        Assert.Equal(4, log.OfType<ObjectMoved>().Count());
        Assert.Equal(3, final.Objects.Values.Count(card => card.Zone.Zone == Zone.Hand));
        Assert.Single(final.Objects.Values, card => card.Zone.Zone == Zone.Command);
        Assert.DoesNotContain(final.Objects.Values, card => card.Zone.Zone == Zone.Battlefield);
        Assert.Equal(4, final.Objects.Keys.Distinct().Count());
    }
}
