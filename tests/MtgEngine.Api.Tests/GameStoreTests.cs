using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MtgEngine.Api.Data;
using MtgEngine.Api.Services;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.Engine;

namespace MtgEngine.Api.Tests;

/// <summary>
/// Games on disk, against a real database rather than a stand-in.
/// </summary>
/// <remarks>
/// The session tests prove the persistence <em>mechanism</em> — save on every action, rebuild by
/// replaying — but they run against <see cref="InMemoryGameStore"/>, so every one of them would
/// still pass if the EF mapping were wrong, the migration missing, or the column too small for a
/// log. That is the half a player actually depends on: nothing about a restart involves a store
/// that lives in the process that just died.
/// </remarks>
public sealed class GameStoreTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly ServiceProvider _provider;
    private readonly SqliteGameStore _sut;

    public GameStoreTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var services = new ServiceCollection();
        services.AddDbContext<MtgEngineDbContext>(o => o.UseSqlite(_conn));
        _provider = services.BuildServiceProvider();

        using (var scope = _provider.CreateScope())
            scope.ServiceProvider.GetRequiredService<MtgEngineDbContext>().Database.EnsureCreated();

        _sut = new SqliteGameStore(_provider.GetRequiredService<IServiceScopeFactory>());
    }

    private static CardDefinition Card(string name) => new()
    {
        OracleId = "oracle-" + name.ToLowerInvariant(),
        Name = name,
        CardTypes = CardType.Creature,
        Power = 1,
        Toughness = 1,
    };

    private static IReadOnlyList<PlayerSetup> Seats(Guid alice, Guid bob) =>
    [
        new(alice, "Alice", 20, [.. Enumerable.Range(1, 30).Select(i => Card($"A{i}"))]),
        new(bob, "Bob", 20, [.. Enumerable.Range(1, 30).Select(i => Card($"B{i}"))]),
    ];

    [Fact]
    public async Task A_game_written_to_the_database_comes_back()
    {
        var id = Guid.NewGuid();
        var when = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

        await _sut.SaveAsync(new StoredGameLog(id, "{\"version\":1}", when, IsOver: false));
        var loaded = await _sut.LoadAsync(id);

        Assert.NotNull(loaded);
        Assert.Equal(id, loaded.GameId);
        Assert.Equal("{\"version\":1}", loaded.Log);
        Assert.False(loaded.IsOver);
        // The context stores every DateTime as UTC and reads it back saying so; a game whose
        // last-activity drifted by the machine's offset would be swept early or late.
        Assert.Equal(when, loaded.LastActivityUtc);
    }

    [Fact]
    public async Task Saving_the_same_game_twice_updates_it_rather_than_duplicating_it()
    {
        // Every action rewrites the whole log, so this path runs hundreds of times per game.
        // Inserting instead would fail on the primary key at the second move.
        var id = Guid.NewGuid();
        var when = DateTimeOffset.UtcNow;

        await _sut.SaveAsync(new StoredGameLog(id, "first", when, IsOver: false));
        await _sut.SaveAsync(new StoredGameLog(id, "second", when.AddMinutes(1), IsOver: true));

        var loaded = await _sut.LoadAsync(id);
        Assert.Equal("second", loaded!.Log);
        Assert.True(loaded.IsOver);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MtgEngineDbContext>();
        Assert.Equal(1, await db.PersistedGames.CountAsync());
    }

    [Fact]
    public async Task A_log_the_length_of_a_real_game_survives_the_round_trip()
    {
        // The column has no length cap, and this is the assertion that says so: a log truncated
        // at some driver or column limit would come back as text that no longer parses, and the
        // game would be unloadable rather than visibly wrong.
        var (alice, bob) = (Guid.NewGuid(), Guid.NewGuid());
        var game = Game.Start(Guid.NewGuid(), Seats(alice, bob), new GameRandom(7), alice);
        game.BeginPlay();

        var written = MtgEngine.Rules.Events.EventLogSerializer.Write(game.Log);
        var id = Guid.NewGuid();

        await _sut.SaveAsync(new StoredGameLog(id, written, DateTimeOffset.UtcNow, IsOver: false));
        var loaded = await _sut.LoadAsync(id);

        Assert.Equal(written, loaded!.Log);
        Assert.Equal(
            game.State,
            GameReducer.Replay(MtgEngine.Rules.Events.EventLogSerializer.Read(loaded.Log)));
    }

    [Fact]
    public async Task A_missing_game_is_null_rather_than_an_error()
    {
        Assert.Null(await _sut.LoadAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Only_games_past_the_cutoff_are_expired()
    {
        var now = DateTimeOffset.UtcNow;
        var old = Guid.NewGuid();
        var recent = Guid.NewGuid();

        await _sut.SaveAsync(new StoredGameLog(old, "{}", now.AddDays(-40), IsOver: false));
        await _sut.SaveAsync(new StoredGameLog(recent, "{}", now.AddHours(-1), IsOver: false));

        var expired = await _sut.ExpiredAsync(now.AddDays(-30));

        Assert.Equal([old], expired);
    }

    [Fact]
    public async Task Deleting_a_game_removes_it()
    {
        var id = Guid.NewGuid();
        await _sut.SaveAsync(new StoredGameLog(id, "{}", DateTimeOffset.UtcNow, IsOver: false));

        await _sut.DeleteAsync(id);

        Assert.Null(await _sut.LoadAsync(id));
    }

    [Fact]
    public async Task Deleting_a_game_that_is_not_there_is_not_an_error()
    {
        // The sweeper and an explicit end can both reach the same game.
        await _sut.DeleteAsync(Guid.NewGuid());
    }

    [Fact]
    public async Task A_game_played_through_the_real_store_survives_a_restart()
    {
        // The whole point, end to end and through EF: play a game, throw away everything that
        // was holding it, and open it again from the database alone.
        var sessions = new GameSessionService(NoAbilities.Instance, _sut);
        var (alice, bob) = (Guid.NewGuid(), Guid.NewGuid());
        var gameId = await sessions.CreateAsync(Seats(alice, bob), seed: 42);

        var before = await sessions.Find(gameId)!.MutateAsync(game =>
        {
            for (var guard = 0; guard < 10 && game.State.Choice is { } choice; guard++)
                game.Choose(choice.PlayerId, ["keep"]);

            return game.State;
        });

        var restarted = new GameSessionService(NoAbilities.Instance, _sut);
        var resumed = await restarted.FindAsync(gameId);

        Assert.NotNull(resumed);
        Assert.Equal(before, await resumed.MutateAsync(game => game.State));
        Assert.True(await restarted.IsSeatedAsync(gameId, alice));
        Assert.True(await restarted.IsSeatedAsync(gameId, bob));

        // And it is still a game: the next action lands and is stored in its turn.
        var passed = await resumed.MutateAsync(game =>
        {
            if (game.State.Priority.Holder is not { } holder)
                return false;

            game.PassPriority(holder);
            return true;
        });

        Assert.True(passed);
        Assert.Contains("PriorityPassed", (await _sut.LoadAsync(gameId))!.Log, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _conn.Dispose();
    }
}
