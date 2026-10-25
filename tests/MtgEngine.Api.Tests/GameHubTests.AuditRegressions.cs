using MtgEngine.Api.Dtos;
using MtgEngine.Api.Hubs;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Api.Tests;

public sealed partial class GameHubTests
{
    [Fact]
    public async Task Cold_load_is_shared_and_one_cancelled_waiter_does_not_cancel_others()
    {
        var table = await TableAsync();
        var pool = Deck(Cards.ImplementationStatus.Translated);
        var restarted = new Services.GameSessionService(table.Store, pool, new Services.DeckGate(pool));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        table.Store.LoadDelay = release.Task;
        var before = table.Store.Loads;
        using var cancellation = new CancellationTokenSource();
        var first = restarted.FindAsync(table.Session.Id, cancellation.Token);
        var second = restarted.FindAsync(table.Session.Id);
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            Assert.Equal(before + 1, table.Store.Loads);
            Assert.False(second.IsCompleted);
        }
        finally { release.SetResult(); }
        var loaded = await second;
        Assert.NotNull(loaded);
        Assert.Same(loaded, await restarted.FindAsync(table.Session.Id));
    }

    [Fact]
    public async Task Joining_another_table_removes_the_connections_previous_subscription()
    {
        var table = await TableAsync();
        var other = await table.Sessions.CreateAsync(new Services.NewGameRequest(
            [new Services.NewGameSeat(Alice, "Alice", [.. DeckIds()], []), new Services.NewGameSeat(Bob, "Bob", [.. DeckIds()], [])],
            2, true, "commander", 20));
        await table.Hub.Join(table.Session.Id);
        await table.Hub.Join(other.Id);
        var groups = Assert.IsType<NoGroups>(table.Hub.Groups);
        Assert.Equal(GameHub.Group(table.Session.Id, Alice), Assert.Single(groups.Left));
        Assert.Equal(GameHub.Group(other.Id, Alice), groups.Joined[^1]);
    }
    [Fact]
    public async Task Failed_persistence_does_not_publish_or_replay_a_command()
    {
        var table = await TableAsync();
        var state = table.Session.State;
        var log = table.Session.Log;
        var saved = table.Store.Rows[table.Session.Id].LogJson;
        table.Store.FailSave = true;
        await Assert.ThrowsAsync<IOException>(() => table.Hub.Command(table.Session.Id, new ConcedeDto()));
        Assert.Same(state, table.Session.State);
        Assert.Same(log, table.Session.Log);
        Assert.Equal(saved, table.Store.Rows[table.Session.Id].LogJson);
        Assert.Empty(table.Clients.ViewedBy);
        table.Store.FailSave = false;
        await table.Hub.Command(table.Session.Id, new ConcedeDto());
        Assert.True(table.Session.Finished);
    }

    [Fact]
    public async Task Updates_are_addressed_to_private_table_subscriptions()
    {
        var first = await TableAsync();
        var second = await TableAsync();
        await first.Hub.Join(first.Session.Id);
        await second.Hub.Join(second.Session.Id);
        Assert.Contains(GameHub.Group(first.Session.Id, Alice), first.Clients.SentGroups);
        Assert.DoesNotContain(GameHub.Group(second.Session.Id, Alice), first.Clients.SentGroups);
        await first.Hub.Command(first.Session.Id, new ManualLifeDto(0, 1));
        Assert.Contains(GameHub.Group(first.Session.Id, Bob), first.Clients.SentGroups);
        Assert.All(first.Clients.SentGroups, group => Assert.StartsWith($"game:{first.Session.Id:N}:user:", group));
    }

    [Fact]
    public async Task Later_updates_wait_until_the_previous_delivery_has_completed()
    {
        var table = await TableAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        table.Clients.DelaySend = (_, method) => method == "ViewUpdated" ? release.Task : Task.CompletedTask;
        var first = table.Hub.Command(table.Session.Id, new ManualLifeDto(0, 1));
        var second = table.Hub.Command(table.Session.Id, new ManualLifeDto(0, 2));
        try
        {
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.Equal(2, table.Clients.ViewedBy.Count);
        }
        finally
        {
            release.SetResult();
            await Task.WhenAll(first, second);
        }
        Assert.Equal(4, table.Clients.ViewedBy.Count);
    }

    [Fact]
    public async Task Null_nested_attack_is_refused_without_an_invocation_failure()
    {
        var table = await TableAsync();
        await table.Hub.Command(table.Session.Id, new DeclareAttackersDto([null!]));
        Assert.Single(table.Clients.Refusals);
        Assert.Empty(table.Clients.ViewedBy);
    }

    [Fact]
    public async Task Nested_annotation_is_checked_before_the_engine_runs()
    {
        var table = await TableAsync();
        await table.Hub.Command(table.Session.Id, new DeclareBlockersDto([new BlockDto(0, 0)]));
        var refusal = Assert.Single(table.Clients.Refusals);
        Assert.Contains("Blocker", refusal.Reason, StringComparison.Ordinal);
        Assert.Contains("between", refusal.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(table.Clients.ViewedBy);
    }

    [Fact]
    public async Task Session_boundary_enforces_manual_policy_without_the_hub()
    {
        var table = await TableAsync(allowHonourSystem: false, status: Cards.ImplementationStatus.Verified);
        var result = await table.Sessions.ApplyAsync(table.Session, new ManualLife(new PlayerId(0), new PlayerId(0), 1));
        Assert.IsType<Refused>(result);
    }
}
