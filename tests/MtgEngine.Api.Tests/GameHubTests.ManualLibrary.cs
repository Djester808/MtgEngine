using System.Text.Json;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;
using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Api.Tests;

public sealed partial class GameHubTests
{
    [Trait("Scope", "Deck")]
    [Fact]
    public async Task Full_library_arrangement_requires_inspection_and_preserves_objects_through_restart()
    {
        var pool = Deck(ImplementationStatus.Verified);
        var table = await TableAsync(pool: pool);
        var owner = new PlayerId(0);
        var library = ZoneKey.Of(Zone.Library, owner);
        var cards = table.Session.State.In(library);
        var order = cards.Reverse().ToArray();
        GameCommandDto action = new ManualLibraryArrangeDto(Guid.NewGuid(), [.. order.Select(card => card.Value)]);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        action = JsonSerializer.Deserialize<GameCommandDto>(JsonSerializer.Serialize(action, options), options)!;
        Assert.IsType<Refused>(await table.Sessions.ApplyAsync(table.Session, action.ToCommand(owner)));
        Assert.IsType<Applied>(await table.Sessions.ApplyAsync(table.Session, new ManualLibraryLook(owner, Guid.NewGuid())));
        var before = table.Session.State;
        var count = table.Session.Log.Count;
        await table.Hub.Command(table.Session.Id, action);
        Assert.Empty(table.Clients.Refusals);
        Assert.Equal(order, table.Session.State.In(library));
        Assert.False(table.Session.ProjectFor(owner).View!.ManualLibraryLookActive);
        Assert.All(cards, id => Assert.Equal(before.Objects[id], table.Session.State.Objects[id]));
        var events = table.Session.Log.Skip(count).ToArray();
        Assert.Single(events.OfType<ZoneReordered>());
        Assert.Empty(events.OfType<ObjectMoved>());
        Assert.Empty(events.OfType<LibrarySearched>());
        Assert.DoesNotContain(events.OfType<ZoneReordered>(), change => change.Shuffle);
        var restarted = new GameSessionService(table.Store, pool, new DeckGate(pool));
        var restored = (await restarted.FindAsync(table.Session.Id))!;
        Assert.Equal(order, restored.State.In(library));
        Assert.IsType<Refused>(await restarted.ApplyAsync(restored, action.ToCommand(owner)));
        Assert.Empty(restored.ProjectFor(new PlayerId(1)).View!.InspectedLibraryCards);
        var enforced = await TableAsync(allowHonourSystem: false, status: ImplementationStatus.Verified);
        await enforced.Hub.Command(enforced.Session.Id, action);
        Assert.Single(enforced.Clients.Refusals);
        Assert.IsType<Refused>(await enforced.Sessions.ApplyAsync(enforced.Session, action.ToCommand(owner)));
    }

    [Trait("Scope", "Deck")]
    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("foreign")]
    [InlineData("null")]
    public async Task Invalid_library_arrangement_is_atomic(string invalid)
    {
        var table = await TableAsync(status: ImplementationStatus.Verified);
        var owner = new PlayerId(0);
        Assert.IsType<Applied>(await table.Sessions.ApplyAsync(table.Session, new ManualLibraryLook(owner, Guid.NewGuid())));
        var library = ZoneKey.Of(Zone.Library, owner);
        var cards = table.Session.State.In(library);
        var order = cards.Select(card => card.Value).ToArray();
        if (invalid == "missing")
            order = order[1..];
        if (invalid == "duplicate")
            order[0] = order[1];
        if (invalid == "foreign")
            order[0] = table.Session.State.In(ZoneKey.Of(Zone.Library, new PlayerId(1)))[0].Value;
        var before = table.Session.Log.Count;
        await table.Hub.Command(table.Session.Id, new ManualLibraryArrangeDto(Guid.NewGuid(), invalid == "null" ? null : order));
        Assert.Single(table.Clients.Refusals);
        Assert.Equal(before, table.Session.Log.Count);
        Assert.Equal(cards, table.Session.State.In(library));
        Assert.True(table.Session.ProjectFor(owner).View!.ManualLibraryLookActive);
    }

    [Trait("Scope", "Deck")]
    [Theory]
    [InlineData(-1, Zone.Hand, Placement.Default)]
    [InlineData(1, Zone.Stack, Placement.Default)]
    [InlineData(1, Zone.Hand, (Placement)999)]
    public async Task Invalid_manual_library_search_leaves_the_log_and_inspection_unchanged(int count, Zone destination, Placement where)
    {
        var table = await TableAsync(status: ImplementationStatus.Verified);
        var before = table.Session.Log.Count;
        await table.Hub.Command(table.Session.Id,
            new ManualLibrarySearchDto(Guid.NewGuid(), count, destination, Where: where));
        Assert.Single(table.Clients.Refusals);
        Assert.Equal(before, table.Session.Log.Count);
        Assert.Null(table.Session.State.Pending);
        Assert.Empty(table.Session.State.LibraryInspections);
    }

    [Trait("Scope", "Deck")]
    [Fact]
    public async Task Manual_library_search_is_private_restarts_and_requires_table_permission()
    {
        var pool = Deck(ImplementationStatus.Verified);
        var table = await TableAsync(pool: pool);
        var owner = new PlayerId(0);
        var other = new PlayerId(1);
        var library = ZoneKey.Of(Zone.Library, owner);
        var before = table.Session.State.In(library).Count;
        var hand = table.Session.State.In(ZoneKey.Of(Zone.Hand, owner)).Count;
        GameCommandDto action = new ManualLibrarySearchDto(Guid.NewGuid(), 1, Zone.Hand);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        action = JsonSerializer.Deserialize<GameCommandDto>(JsonSerializer.Serialize(action, options), options)!;
        Assert.True(action.IsManual);
        Assert.Equal(owner, action.ToCommand(owner).Player);
        await table.Hub.Command(table.Session.Id, action);
        Assert.Empty(table.Clients.Refusals);
        var pending = Assert.IsType<PendingChoice>(table.Session.State.Pending);
        Assert.All(pending.Options, option => Assert.Contains(option.Object!.Value, table.Session.State.In(library)));
        Assert.Empty(table.Session.ProjectFor(other).View!.InspectedLibraryCards);
        Assert.Empty(table.Session.ProjectFor(other).View!.Pending!.Options);
        var restarted = new GameSessionService(table.Store, pool, new DeckGate(pool));
        var restored = (await restarted.FindAsync(table.Session.Id))!;
        Assert.IsType<Refused>(await restarted.ApplyAsync(restored, action.ToCommand(owner)));
        Assert.IsType<Applied>(await restarted.ApplyAsync(restored,
            new AnswerChoice(owner, pending.Id, [pending.Options[0].Id])));
        Assert.Equal(before - 1, restored.State.In(library).Count);
        Assert.Equal(hand + 1, restored.State.In(ZoneKey.Of(Zone.Hand, owner)).Count);
        Assert.Empty(restored.State.LibraryInspections);
        Assert.IsType<Refused>(await restarted.ApplyAsync(restored, action.ToCommand(owner)));

        var enforced = await TableAsync(allowHonourSystem: false, status: ImplementationStatus.Verified);
        var count = enforced.Session.Log.Count;
        await enforced.Hub.Command(enforced.Session.Id, action);
        Assert.Single(enforced.Clients.Refusals);
        Assert.IsType<Refused>(await enforced.Sessions.ApplyAsync(enforced.Session, action.ToCommand(owner)));
        Assert.Equal(count, enforced.Session.Log.Count);
    }

    [Trait("Scope", "Deck")]
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Library_reposition_requires_current_own_inspection_and_is_retry_safe(int position)
    {
        var pool = Deck(ImplementationStatus.Verified);
        var table = await TableAsync(pool: pool);
        var owner = new PlayerId(0);
        var library = ZoneKey.Of(Zone.Library, owner);
        var cards = table.Session.State.In(library);
        var card = cards[1];
        GameCommandDto action = new ManualLibraryRepositionDto(Guid.NewGuid(), card.Value, position);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        action = JsonSerializer.Deserialize<GameCommandDto>(JsonSerializer.Serialize(action, options), options)!;
        var count = table.Session.Log.Count;
        Assert.IsType<Refused>(await table.Sessions.ApplyAsync(table.Session, action.ToCommand(owner)));
        Assert.Equal(count, table.Session.Log.Count);
        Assert.IsType<Applied>(await table.Sessions.ApplyAsync(table.Session, new ManualLibraryLook(owner, Guid.NewGuid())));
        var otherCard = table.Session.State.In(ZoneKey.Of(Zone.Library, new PlayerId(1)))[0];
        Assert.IsType<Refused>(await table.Sessions.ApplyAsync(table.Session,
            new ManualLibraryReposition(owner, Guid.NewGuid(), otherCard, position)));
        await table.Hub.Command(table.Session.Id, action);
        Assert.Empty(table.Clients.Refusals);
        var expected = cards.Remove(card).Insert(position == 0 ? 0 : cards.Count - 1, card);
        Assert.Equal(expected, table.Session.State.In(library));
        Assert.False(table.Session.ProjectFor(owner).View!.ManualLibraryLookActive);
        Assert.Empty(table.Session.ProjectFor(owner).View!.InspectedLibraryCards);
        var restarted = new GameSessionService(table.Store, pool, new DeckGate(pool));
        var restored = (await restarted.FindAsync(table.Session.Id))!;
        Assert.Equal(expected, restored.State.In(library));
        Assert.IsType<Refused>(await restarted.ApplyAsync(restored, action.ToCommand(owner)));

        var enforced = await TableAsync(allowHonourSystem: false, status: ImplementationStatus.Verified);
        await enforced.Hub.Command(enforced.Session.Id, action);
        Assert.Single(enforced.Clients.Refusals);
        Assert.IsType<Refused>(await enforced.Sessions.ApplyAsync(enforced.Session, action.ToCommand(owner)));
    }

    [Trait("Scope", "Deck")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Simultaneous_library_inspections_keep_independent_lifetimes_after_draw_and_replay(bool shuffle)
    {
        var pool = Deck(ImplementationStatus.Verified);
        var table = await TableAsync(pool: pool);
        var owner = new PlayerId(0);
        var other = new PlayerId(1);
        var libraries = new[] { owner, other }.ToDictionary(player => player,
            player => table.Session.State.In(ZoneKey.Of(Zone.Library, player)));
        foreach (var player in libraries.Keys)
            Assert.IsType<Applied>(await table.Sessions.ApplyAsync(table.Session,
                new ManualLibraryLook(player, Guid.NewGuid())));

        Assert.IsType<Applied>(await table.Sessions.ApplyAsync(table.Session,
            new ManualLibraryDraw(owner, Guid.NewGuid())));
        Assert.True(table.Session.ProjectFor(owner).View!.ManualLibraryLookActive);
        Assert.Equal(libraries[owner].Count - 1, table.Session.ProjectFor(owner).View!.InspectedLibraryCards.Count);
        Assert.Equal(table.Session.State.In(ZoneKey.Of(Zone.Library, owner)).OrderBy(id => id.Value),
            table.Session.ProjectFor(owner).View!.InspectedLibraryCards.Select(card => card.Id).OrderBy(id => id.Value));
        Assert.Equal(libraries[other].Count, table.Session.ProjectFor(other).View!.InspectedLibraryCards.Count);

        ManualLibraryCommand finish = shuffle
            ? new ManualLibraryShuffle(owner, Guid.NewGuid())
            : new ManualLibraryStopLooking(owner, Guid.NewGuid());
        Assert.IsType<Applied>(await table.Sessions.ApplyAsync(table.Session, finish));
        var restarted = new GameSessionService(table.Store, pool, new DeckGate(pool));
        var restored = (await restarted.FindAsync(table.Session.Id))!;
        Assert.False(restored.ProjectFor(owner).View!.ManualLibraryLookActive);
        Assert.Empty(restored.ProjectFor(owner).View!.InspectedLibraryCards);
        Assert.True(restored.ProjectFor(other).View!.ManualLibraryLookActive);
        Assert.Equal(libraries[other].Count, restored.ProjectFor(other).View!.InspectedLibraryCards.Count);
        Assert.Equal(libraries[other].OrderBy(id => id.Value),
            restored.ProjectFor(other).View!.InspectedLibraryCards.Select(card => card.Id).OrderBy(id => id.Value));
        Assert.Equal(libraries[other], restored.State.In(ZoneKey.Of(Zone.Library, other)));
        Assert.Empty(restored.Log.OfType<LibrarySearched>());
    }

    [Trait("Scope", "Deck")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Manual_library_look_is_private_replayable_and_expires(bool shuffle)
    {
        var pool = Deck(ImplementationStatus.Verified);
        var table = await TableAsync(pool: pool);
        var owner = new PlayerId(0);
        var other = new PlayerId(1);
        var library = table.Session.State.In(ZoneKey.Of(Zone.Library, owner));
        var request = Guid.NewGuid();
        GameCommandDto action = new ManualLibraryLookDto(request);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        action = JsonSerializer.Deserialize<GameCommandDto>(JsonSerializer.Serialize(action, options), options)!;
        await table.Hub.Command(table.Session.Id, action);
        Assert.Empty(table.Clients.Refusals);
        Assert.True(table.Session.ProjectFor(owner).View!.ManualLibraryLookActive);
        Assert.Equal(library.Count, table.Session.ProjectFor(owner).View!.InspectedLibraryCards.Count);
        Assert.False(table.Session.ProjectFor(other).View!.ManualLibraryLookActive);
        Assert.Empty(table.Session.ProjectFor(other).View!.InspectedLibraryCards);
        Assert.Equal(library, table.Session.State.In(ZoneKey.Of(Zone.Library, owner)));
        Assert.Empty(table.Session.Log.OfType<LibrarySearched>());
        var restarted = new GameSessionService(table.Store, pool, new DeckGate(pool));
        var restored = (await restarted.FindAsync(table.Session.Id))!;
        Assert.True(restored.ProjectFor(owner).View!.ManualLibraryLookActive);
        Assert.Equal(library.Count, restored.ProjectFor(owner).View!.InspectedLibraryCards.Count);
        Assert.IsType<Refused>(await restarted.ApplyAsync(restored, action.ToCommand(owner)));
        GameCommandDto finish = shuffle ? new ManualLibraryShuffleDto(Guid.NewGuid()) : new ManualLibraryStopLookingDto(Guid.NewGuid());
        finish = JsonSerializer.Deserialize<GameCommandDto>(JsonSerializer.Serialize(finish, options), options)!;
        Assert.IsType<Applied>(await restarted.ApplyAsync(restored, finish.ToCommand(owner)));
        Assert.False(restored.ProjectFor(owner).View!.ManualLibraryLookActive);
        Assert.Empty(restored.ProjectFor(owner).View!.InspectedLibraryCards);
        var final = (await new GameSessionService(table.Store, pool, new DeckGate(pool)).FindAsync(restored.Id))!;
        Assert.False(final.ProjectFor(owner).View!.ManualLibraryLookActive);
        Assert.Empty(final.ProjectFor(owner).View!.InspectedLibraryCards);
    }

    [Trait("Scope", "Deck")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Enforced_tables_reject_manual_library_inspection(bool stop)
    {
        var table = await TableAsync(allowHonourSystem: false, status: ImplementationStatus.Verified);
        GameCommandDto action = stop ? new ManualLibraryStopLookingDto(Guid.NewGuid()) : new ManualLibraryLookDto(Guid.NewGuid());
        var count = table.Session.Log.Count;
        await table.Hub.Command(table.Session.Id, action);
        Assert.Single(table.Clients.Refusals);
        Assert.IsType<Refused>(await table.Sessions.ApplyAsync(table.Session, action.ToCommand(new PlayerId(0))));
        Assert.Equal(count, table.Session.Log.Count);
        Assert.Empty(table.Session.State.LibraryInspections);
    }

    [Trait("Scope", "Deck")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_manual_library_retries_apply_once_and_request_identity_is_player_scoped(bool shuffle)
    {
        var pool = Deck(ImplementationStatus.Verified);
        var table = await TableAsync(pool: pool);
        var owner = new PlayerId(0);
        var other = new PlayerId(1);
        var before = table.Session.State;
        var saves = table.Store.Saves;
        var request = Guid.NewGuid();
        ManualLibraryCommand action = shuffle
            ? new ManualLibraryShuffle(owner, request)
            : new ManualLibraryDraw(owner, request);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 8).Select(async _ =>
        {
            await start.Task;
            return await table.Sessions.ApplyAsync(table.Session, action);
        }).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(attempts);
        Assert.Single(results.OfType<Applied>());
        Assert.Equal(7, results.OfType<Refused>().Count());
        Assert.Equal(saves + 1, table.Store.Saves);
        Assert.Single(table.Session.Log.OfType<ManualActionTaken>());
        Assert.Equal(before.In(ZoneKey.Of(Zone.Library, owner)).Count - (shuffle ? 0 : 1),
            table.Session.State.In(ZoneKey.Of(Zone.Library, owner)).Count);

        var restarted = new GameSessionService(table.Store, pool, new DeckGate(pool));
        var restored = await restarted.FindAsync(table.Session.Id);
        Assert.NotNull(restored);
        ManualLibraryCommand differentAction = shuffle
            ? new ManualLibraryDraw(owner, request)
            : new ManualLibraryShuffle(owner, request);
        Assert.IsType<Refused>(await restarted.ApplyAsync(restored, differentAction));
        Assert.Equal(saves + 1, table.Store.Saves);
        Assert.IsType<Applied>(await restarted.ApplyAsync(restored, new ManualLibraryDraw(other, request)));
        Assert.Equal(saves + 2, table.Store.Saves);
        Assert.Equal(before.In(ZoneKey.Of(Zone.Library, other)).Count - 1,
            restored.State.In(ZoneKey.Of(Zone.Library, other)).Count);
        Assert.Equal(new[] { owner, other }, restored.Log.OfType<ManualActionTaken>().Select(item => item.Actor));
        Assert.All(restored.Log.OfType<ManualActionTaken>(), item => Assert.Equal(request, item.RequestId));
    }

    [Trait("Scope", "Deck")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Manual_library_action_is_owned_logged_and_not_repeated_after_restart(bool shuffle, bool failDelivery = false)
    {
        var pool = Deck(ImplementationStatus.Verified);
        var table = await TableAsync(pool: pool);
        var owner = new PlayerId(0);
        var other = new PlayerId(1);
        var before = table.Session.State;
        var saves = table.Store.Saves;
        Assert.True(table.Session.ProjectFor(owner).View!.ManualLibraryActionsAllowed);
        var request = Guid.NewGuid();
        GameCommandDto action = shuffle ? new ManualLibraryShuffleDto(request) : new ManualLibraryDrawDto(request);
        var wire = JsonSerializer.Serialize(action, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        action = JsonSerializer.Deserialize<GameCommandDto>(wire, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        if (failDelivery)
        {
            table.Clients.DelaySend = (user, method) => user == Alice && method == "ViewUpdated"
                ? Task.FromException(new IOException("Simulated lost delivery after save.")) : Task.CompletedTask;
            await Assert.ThrowsAsync<IOException>(() => table.Hub.Command(table.Session.Id, action));
            table.Clients.DelaySend = null;
            Assert.Contains(Bob, table.Clients.ViewedBy);
        }
        else
            await table.Hub.Command(table.Session.Id, action);
        Assert.Empty(table.Clients.Refusals);
        Assert.Equal(saves + 1, table.Store.Saves);
        Assert.Equal(before.In(ZoneKey.Of(Zone.Library, other)), table.Session.State.In(ZoneKey.Of(Zone.Library, other)));
        Assert.Equal(before.In(ZoneKey.Of(Zone.Hand, other)), table.Session.State.In(ZoneKey.Of(Zone.Hand, other)));
        Assert.Equal(before.In(ZoneKey.Of(Zone.Hand, owner)).Count + (shuffle ? 0 : 1),
            table.Session.State.In(ZoneKey.Of(Zone.Hand, owner)).Count);
        var recorded = Assert.Single(table.Session.Log.OfType<ManualActionTaken>());
        Assert.Equal(request, recorded.RequestId);
        Assert.Equal(owner, recorded.Actor);
        Assert.Equal(shuffle ? "shuffle" : "draw", recorded.Action);
        if (shuffle)
            Assert.NotEqual(before.Rng, table.Session.State.Rng);
        else
            Assert.Equal(before.Player(owner).ObjectsDrawnThisTurn + 1, table.Session.State.Player(owner).ObjectsDrawnThisTurn);
        var count = table.Session.Log.Count;
        var savedLog = EventLogSerializer.Write(table.Session.Log);
        var savedViews = new[] { owner, other }.Select(player => JsonSerializer.Serialize(table.Session.ProjectFor(player).View)).ToArray();
        var savedRng = table.Session.State.Rng;
        await table.Hub.Command(table.Session.Id, action);
        Assert.Single(table.Clients.Refusals);
        Assert.Equal(count, table.Session.Log.Count);
        Assert.Equal(savedLog, EventLogSerializer.Write(table.Session.Log));
        var restarted = new GameSessionService(table.Store, pool, new DeckGate(pool));
        var restored = await restarted.FindAsync(table.Session.Id);
        Assert.NotNull(restored);
        var restoredLog = EventLogSerializer.Write(restored.Log);
        Assert.IsType<Refused>(await restarted.ApplyAsync(restored, action.ToCommand(owner)));
        Assert.Equal(count, restored.Log.Count);
        Assert.Equal(saves + 1, table.Store.Saves);
        Assert.Equal(restoredLog, EventLogSerializer.Write(restored.Log));
        Assert.Equal(savedViews, new[] { owner, other }.Select(player => JsonSerializer.Serialize(restored.ProjectFor(player).View)));
        Assert.Equal(savedRng, restored.State.Rng);
    }

    [Trait("Scope", "Deck")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Enforced_tables_reject_manual_library_commands_at_both_entry_points(bool shuffle)
    {
        var table = await TableAsync(allowHonourSystem: false, status: ImplementationStatus.Verified);
        GameCommandDto action = shuffle ? new ManualLibraryShuffleDto(Guid.NewGuid()) : new ManualLibraryDrawDto(Guid.NewGuid());
        var before = table.Session.Log.Count;
        Assert.False(table.Session.ProjectFor(new PlayerId(0)).View!.ManualLibraryActionsAllowed);
        await table.Hub.Command(table.Session.Id, action);
        Assert.Single(table.Clients.Refusals);
        Assert.IsType<Refused>(await table.Sessions.ApplyAsync(table.Session, action.ToCommand(new PlayerId(0))));
        Assert.Equal(before, table.Session.Log.Count);
    }

    [Trait("Scope", "Deck")]
    [Fact]
    public async Task Manual_draw_uses_the_normal_empty_library_loss_rule_and_rejects_missing_request_ids()
    {
        var table = await TableAsync(status: ImplementationStatus.Verified);
        var owner = new PlayerId(0);
        Assert.IsType<Refused>(await table.Sessions.ApplyAsync(table.Session, new ManualLibraryDraw(owner, Guid.Empty)));
        var count = table.Session.State.In(ZoneKey.Of(Zone.Library, owner)).Count;
        for (var i = 0; i <= count; i++)
            Assert.IsType<Applied>(await table.Sessions.ApplyAsync(table.Session, new ManualLibraryDraw(owner, Guid.NewGuid())));
        Assert.True(table.Session.State.Player(owner).HasLost);
        Assert.False(table.Session.ProjectFor(owner).View!.ManualLibraryActionsAllowed);
    }
}
