using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MtgEngine.Api.Data;
using MtgEngine.Api.Services;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Tests;

public sealed partial class GameLobbyStoreTests
{
    [Fact]
    public async Task Appending_events_preserves_the_initial_log_and_rejects_stale_writers()
    {
        var logs = new GameLogStore(services.GetRequiredService<IServiceScopeFactory>());
        var id = Guid.NewGuid();
        Assert.True(await logs.TryCreateAsync(new PersistedGame { Id = id, LogJson = "[1]", EventCount = 1 }));
        await logs.AppendAsync(id, "[2,3]", 1, 2, false);
        await logs.AppendAsync(id, "[4]", 3, 1, true);
        await Assert.ThrowsAsync<InvalidResourceStateException>(() => logs.AppendAsync(id, "[99]", 1, 1, false));
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MtgEngineDbContext>();
        Assert.Equal("[1]", (await db.PersistedGames.SingleAsync(game => game.Id == id)).LogJson);
        Assert.Equal(2, await db.Set<PersistedGameEvent>().CountAsync(batch => batch.GameId == id));
        var restored = await logs.LoadAsync(id);
        Assert.NotNull(restored);
        Assert.Equal("[1,2,3,4]", restored.LogJson);
        Assert.Equal(4, restored.EventCount);
        Assert.True(restored.Finished);
    }
}
