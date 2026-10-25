using System.Collections.Immutable;
using MtgEngine.Api.Services;
using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Library;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Triggers;

namespace MtgEngine.Api.Tests;

public sealed partial class GameHubTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Direct_choice_push_uses_the_same_redacted_library_context_as_the_view(bool shuffleOnly)
    {
        var store = new MemoryLogStore();
        var pool = Deck(ImplementationStatus.Verified);
        var table = await TableAsync(store: store, pool: pool);
        var hiddenId = table.Session.State.In(ZoneKey.Of(Zone.Library, new PlayerId(0)))[0];
        var hidden = table.Session.State.Objects[hiddenId];
        const string observationMarker = "server-only-departure-appearance";
        var observation = new LibraryDepartureObservation(hidden,
            new MtgEngine.Rules.Layers.LayerSystem().Of(table.Session.State, hiddenId) with { Name = observationMarker });
        var proposal = new ObjectMoved(hidden.Id, hidden with { Zone = ZoneKey.Shared(Zone.Exile) }, -1, hidden)
        { LibraryDeparture = observation };
        var milled = new CardMilled(new PlayerId(0), proposal.To) { LibraryDeparture = observation };
        const string triggerMarker = "server-only-mill-trigger-choice";
        var deferredTrigger = new PendingTrigger(hiddenId, 0, new PlayerId(0),
            new TriggeredAbility(new Mills(new You()), new GainLife(new Fixed(1))),
            TriggerSubject.Of(hiddenId, new PlayerId(0)), null,
            ImmutableDictionary<string, string>.Empty.Add("private-trigger-choice", triggerMarker))
        { SourceObject = hidden, Cause = milled, CauseIndex = 1 };
        var context = new ChoiceContext(null, null, "0",
            ImmutableDictionary<string, string>.Empty.Add("remembered::library:0:looked", hiddenId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)), proposal)
        { DeferredDraws = [new PendingDrawBatch(new PlayerId(0), 2)] };
        if (shuffleOnly)
            context = new ChoiceContext(null, null, "0", ImmutableDictionary<string, string>.Empty);
        context = context with
        {
            DeferredShuffles = [new PlayerId(1), new PlayerId(0), new PlayerId(1)],
            DeferredTriggers = [deferredTrigger],
            RemainingPayment = new MtgEngine.Rules.Casting.RemainingCostPayment([new MillCost(new Fixed(2))], [hiddenId]),
        };
        context = context with
        {
            RemainingPayment = context.RemainingPayment with
            {
                Context = new MtgEngine.Rules.Casting.CostPaymentContext(hidden, null, new PlayerId(0), 3,
                    null, null, false, ["cost"]),
                Activation = new MtgEngine.Rules.Casting.ActivationPayment(
                    new MtgEngine.Rules.Commands.ActivateAbility(new PlayerId(0), hiddenId, 0, X: 3),
                    ImmutableDictionary<string, string>.Empty.Add("private-announcement", "private-value"), [proposal, milled]),
            },
        };
        var choice = new PendingChoice(new ChoiceId(table.Session.State.NextChoiceId), new PlayerId(1),
            "search", "Choose a card", [new ChoiceOption("allowed", "Allowed choice")], 1, 1, context);
        var log = table.Session.Log.Add(new ChoiceRequested(choice));
        Assert.Contains(observationMarker, EventLogSerializer.Write(log));
        await store.SaveLogAsync(table.Session.Id, EventLogSerializer.Write(log), log.Count, false);
        var sessions = new GameSessionService(store, pool, new DeckGate(pool));
        var restored = await sessions.FindAsync(table.Session.Id);
        Assert.NotNull(restored);
        var recipient = Hub(sessions, restored, store, Bob);
        await recipient.Hub.Join(restored.Id);

        var pushed = Assert.Single(recipient.Clients.ChoicePayloads);
        Assert.Null(pushed.Context.Event);
        Assert.Empty(pushed.Context.DeferredDraws);
        Assert.Empty(pushed.Context.DeferredShuffles);
        Assert.Empty(pushed.Context.DeferredTriggers);
        Assert.Null(pushed.Context.RemainingPayment);
        Assert.DoesNotContain(pushed.Context.Extra.Keys, key => key.StartsWith("remembered:", StringComparison.Ordinal));
        Assert.Equal(choice.Options, pushed.Options);
        var viewChoice = restored.ProjectFor(new PlayerId(1)).View!.Pending!;
        Assert.Equal(viewChoice, pushed);
        Assert.DoesNotContain(observationMarker, System.Text.Json.JsonSerializer.Serialize(pushed, EventLogSerializer.Options));
        Assert.DoesNotContain(triggerMarker, System.Text.Json.JsonSerializer.Serialize(pushed, EventLogSerializer.Options));
        foreach (var player in new[] { new PlayerId(0), new PlayerId(1) })
        {
            Assert.DoesNotContain(observationMarker,
                System.Text.Json.JsonSerializer.Serialize(restored.ProjectFor(player).View, EventLogSerializer.Options));
            Assert.DoesNotContain(triggerMarker,
                System.Text.Json.JsonSerializer.Serialize(restored.ProjectFor(player).View, EventLogSerializer.Options));
            Assert.Empty(restored.ProjectFor(player).View!.Pending!.Context.DeferredTriggers);
        }
        var restoredTrigger = Assert.Single(restored.State.Pending!.Context.DeferredTriggers);
        Assert.Equal(hiddenId, restoredTrigger.SourceObject!.Id);
        Assert.Equal(deferredTrigger.Ability, restoredTrigger.Ability);
        Assert.Equal(deferredTrigger.Subject.Objects, restoredTrigger.Subject.Objects);
        Assert.Equal(1, restoredTrigger.CauseIndex);
        Assert.Equal(triggerMarker, restoredTrigger.Chosen["private-trigger-choice"]);
        Assert.Equal(observationMarker, Assert.IsType<CardMilled>(restoredTrigger.Cause).LibraryDeparture!.Appearance.Name);
        Assert.Equal(shuffleOnly, restored.State.Pending!.Context.Event is null);
        Assert.Equal(shuffleOnly, restored.State.Pending.Context.Extra.IsEmpty);
        Assert.Equal(shuffleOnly, restored.State.Pending.Context.DeferredDraws.IsEmpty);
        Assert.Equal(new[] { new PlayerId(1), new PlayerId(0), new PlayerId(1) }, restored.State.Pending.Context.DeferredShuffles);
        Assert.Empty(restored.ProjectFor(new PlayerId(0)).View!.Pending!.Context.DeferredShuffles);
        Assert.Null(restored.ProjectFor(new PlayerId(0)).View!.Pending!.Context.RemainingPayment);
        Assert.Equal(new[] { hiddenId }, restored.State.Pending.Context.RemainingPayment!.ChosenObjects);
        Assert.Equal(new MillCost(new Fixed(2)), Assert.Single(restored.State.Pending.Context.RemainingPayment.Costs));
        Assert.Equal(hiddenId, restored.State.Pending.Context.RemainingPayment.Context!.Source!.Id);
        Assert.Equal(3, restored.State.Pending.Context.RemainingPayment.Context!.X);
        Assert.Equal(hiddenId, restored.State.Pending.Context.RemainingPayment.Activation!.Command.Source);
        var history = restored.State.Pending.Context.RemainingPayment.Activation!.History;
        Assert.Equal(2, history.Count);
        Assert.Equal(observationMarker, Assert.IsType<ObjectMoved>(history[0]).LibraryDeparture!.Appearance.Name);
        Assert.Equal(observationMarker, Assert.IsType<CardMilled>(history[1]).LibraryDeparture!.Appearance.Name);
        var other = Hub(sessions, restored, store, Alice);
        await other.Hub.Join(restored.Id);
        Assert.Empty(other.Clients.ChoicePayloads);
    }
}
