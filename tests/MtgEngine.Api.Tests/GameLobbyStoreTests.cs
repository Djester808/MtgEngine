using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MtgEngine.Api.Data;
using MtgEngine.Api.Services;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Tests;

public sealed partial class GameLobbyStoreTests : IDisposable
{
    private readonly SqliteConnection keeper;
    private readonly ServiceProvider services;
    private readonly GameLobbyStore store;

    public GameLobbyStoreTests()
    {
        var connection = $"Data Source=lobby-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        keeper = new SqliteConnection(connection);
        keeper.Open();
        services = new ServiceCollection().AddDbContext<MtgEngineDbContext>(options => options.UseSqlite(connection)).BuildServiceProvider();
        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<MtgEngineDbContext>().Database.EnsureCreated();
        store = new GameLobbyStore(services.GetRequiredService<IServiceScopeFactory>());
    }

    [Fact]
    public async Task A_game_log_can_only_be_inserted_once_even_for_two_concurrent_creators()
    {
        var logs = new GameLogStore(services.GetRequiredService<IServiceScopeFactory>());
        var id = Guid.NewGuid();
        var a = new PersistedGame { Id = id, LogJson = "first" };
        var b = new PersistedGame { Id = id, LogJson = "second" };
        var inserted = await Task.WhenAll(Task.Run(() => logs.TryCreateAsync(a)), Task.Run(() => logs.TryCreateAsync(b)));
        Assert.Single(inserted, success => success);
        var saved = await logs.LoadAsync(id);
        Assert.NotNull(saved);
        Assert.Equal(inserted[0] ? "first" : "second", saved.LogJson);
        Assert.False(await logs.TryCreateAsync(new PersistedGame { Id = id, LogJson = "overwrite" }));
        Assert.Equal(saved.LogJson, (await logs.LoadAsync(id))!.LogJson);
    }

    [Fact]
    public async Task Membership_controls_reads_and_changes_in_the_same_commit_as_the_snapshot()
    {
        var lobby = GameTableLobby.Create("host", "Host", Guid.NewGuid(), 2, "commander", 40, false);
        await store.CreateAsync(lobby);
        Assert.Null(await store.FindAsync(lobby.Id, "guest"));
        Assert.Empty(await store.ListAsync("guest"));
        var invited = lobby.Invite("host", "guest", "Guest");
        Assert.True(await store.TryUpdateAsync(invited, lobby.Revision));
        Assert.NotNull(await store.FindAsync(lobby.Id, "guest"));
        Assert.Single(await store.ListAsync("guest"));
        var left = invited.Leave("guest");
        Assert.True(await store.TryUpdateAsync(left, invited.Revision));
        Assert.Null(await store.FindAsync(lobby.Id, "guest"));
        Assert.Empty(await store.ListAsync("guest"));
        var restored = await new GameLobbyStore(services.GetRequiredService<IServiceScopeFactory>()).FindAsync(lobby.Id, "host");
        Assert.NotNull(restored);
        Assert.Single(restored.Seats);
        Assert.Equal(left.Revision, restored.Revision);
    }

    [Fact]
    public async Task A_stale_update_cannot_overwrite_consent_or_add_members()
    {
        var lobby = GameTableLobby.Create("host", "Host", Guid.NewGuid(), 2, "commander", 40, false);
        await store.CreateAsync(lobby);
        Assert.True(await store.TryUpdateAsync(lobby.Invite("host", "guest", "Guest"), 0));
        Assert.False(await store.TryUpdateAsync(lobby.Invite("host", "stranger", "Stranger"), 0));
        Assert.NotNull(await store.FindAsync(lobby.Id, "guest"));
        Assert.Null(await store.FindAsync(lobby.Id, "stranger"));
    }

    [Fact]
    public async Task Two_start_requests_from_the_same_revision_have_only_one_winner()
    {
        var ready = GameTableLobby.Create("host", "Host", Guid.NewGuid(), 2, "commander", 40, false)
            .Invite("host", "guest", "Guest").BringDeck("guest", Guid.NewGuid());
        await store.CreateAsync(ready);
        var first = ready.BeginStart("host");
        var second = ready.BeginStart("host");
        var results = await Task.WhenAll(
            Task.Run(() => store.TryUpdateAsync(first, ready.Revision)),
            Task.Run(() => store.TryUpdateAsync(second, ready.Revision)));
        Assert.Single(results, won => won);
        var reloaded = await store.FindAsync(ready.Id, "guest");
        Assert.NotNull(reloaded);
        Assert.True(reloaded.Starting);
        Assert.Equal(ready.Revision + 1, reloaded.Revision);
        Assert.Equal(ready.Seats, reloaded.Seats);
    }

    public void Dispose()
    {
        services.Dispose();
        keeper.Dispose();
    }
}
