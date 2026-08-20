using System.Reflection;
using System.Text.Json;
using MtgEngine.Api.Hubs;
using MtgEngine.Api.Services;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Views;

namespace MtgEngine.Api.Tests;

/// <summary>
/// The real-time layer: sessions, and the rule that a player only ever receives their own view.
/// </summary>
/// <remarks>
/// The engine this replaces broadcast one <c>GameState</c> to a SignalR group containing every
/// player at the table, which handed each of them the other's hand and both libraries. The
/// engine's own tests prove the projection drops hidden zones; these prove the transport cannot
/// route around it.
/// </remarks>
public sealed class GameSessionTests
{
    private static CardDefinition Card(string name) => new()
    {
        OracleId = "oracle-" + name.ToLowerInvariant(),
        Name = name,
        CardTypes = CardType.Creature,
        Power = 1,
        Toughness = 1,
    };

    private sealed record Table(
        GameSessionService Sessions, InMemoryGameStore Store, Guid GameId, Guid Alice, Guid Bob);

    private static async Task<Table> StartedAsync()
    {
        var store = new InMemoryGameStore();
        var sessions = new GameSessionService(NoAbilities.Instance, store);
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        var gameId = await sessions.CreateAsync(
        [
            new PlayerSetup(alice, "Alice", 20, [.. Enumerable.Range(1, 30).Select(i => Card($"A{i}"))]),
            new PlayerSetup(bob, "Bob", 20, [.. Enumerable.Range(1, 30).Select(i => Card($"B{i}"))]),
        ],
            seed: 42);

        return new Table(sessions, store, gameId, alice, bob);
    }

    [Fact]
    public async Task A_session_gives_each_player_their_own_view()
    {
        var (sessions, _, gameId, alice, bob) = await StartedAsync();
        var session = sessions.Find(gameId)!;

        var forAlice = await session.ReadAsync(alice);
        var forBob = await session.ReadAsync(bob);

        Assert.Equal(alice, forAlice.Viewer);
        Assert.Equal(bob, forBob.Viewer);
        Assert.NotNull(forAlice.Players.Single(p => p.PlayerId == alice).Hand);
        Assert.Null(forAlice.Players.Single(p => p.PlayerId == bob).Hand);
    }

    [Fact]
    public async Task What_a_player_receives_contains_no_hidden_card()
    {
        // The assertion that matters is about the bytes on the wire, not the record shape: a
        // future field, or a serializer that starts including private state, has to fail here.
        var (sessions, _, gameId, alice, _) = await StartedAsync();
        var session = sessions.Find(gameId)!;

        var json = JsonSerializer.Serialize(await session.ReadAsync(alice));

        // Named exactly, not by prefix: "B" alone matches the Battlefield property and would
        // have failed for a reason that has nothing to do with a leak.
        foreach (var name in Enumerable.Range(1, 30).Select(i => $"\"B{i}\""))
            Assert.DoesNotContain(name, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_player_who_is_not_seated_is_not_authorised()
    {
        var (sessions, _, gameId, alice, _) = await StartedAsync();

        Assert.True(await sessions.IsSeatedAsync(gameId, alice));
        Assert.False(await sessions.IsSeatedAsync(gameId, Guid.NewGuid()));
    }

    [Fact]
    public async Task Actions_are_serialised_per_game()
    {
        // One game is one critical section: Game is not thread-safe on purpose, so the session
        // is what makes it safe to share between two players' connections.
        var (sessions, _, gameId, _, _) = await StartedAsync();
        var session = sessions.Find(gameId)!;

        // A real game opens on the mulligan question (CR 103.5), so it is answered first —
        // through the same MutateAsync path a player's answer would take.
        await session.MutateAsync(game =>
        {
            for (var guard = 0; guard < 10 && game.State.Choice is { } choice; guard++)
                game.Choose(choice.PlayerId, ["keep"]);

            return true;
        });

        var passes = 0;
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
            await session.MutateAsync(game =>
            {
                var holder = game.State.Priority.Holder;
                if (holder is null)
                    return false;

                game.PassPriority(holder.Value);
                Interlocked.Increment(ref passes);
                return true;
            }))));

        Assert.Equal(20, passes);
        // A game that had been mutated concurrently would not still fold from its own log.
        var replayed = await session.MutateAsync(game =>
            GameReducer.Replay(game.Log) == game.State);
        Assert.True(replayed);
    }

    [Fact]
    public async Task A_session_reports_the_log_as_readable_lines()
    {
        var (sessions, _, gameId, _, _) = await StartedAsync();

        var log = await sessions.Find(gameId)!.LogAsync();

        Assert.NotEmpty(log);
        Assert.Contains(log, line => line.Contains("started", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Idle_games_are_evicted_and_live_ones_are_not()
    {
        var (sessions, store, gameId, _, _) = await StartedAsync();

        Assert.Empty(sessions.Stale(DateTimeOffset.UtcNow));
        Assert.Equal([gameId], sessions.Stale(DateTimeOffset.UtcNow + GameSessionService.Idle * 2));

        // Evicting frees the memory and nothing else. The game is still on disk, which is the
        // difference between a player who left a tab open for four hours losing their place and
        // losing their game.
        Assert.True(sessions.Evict(gameId));
        Assert.Null(sessions.Find(gameId));
        Assert.NotNull(await store.LoadAsync(gameId));
        Assert.NotNull(await sessions.FindAsync(gameId));
    }

    [Fact]
    public async Task A_game_survives_the_process_that_was_running_it()
    {
        // The point of the whole store. A second GameSessionService over the same data is what a
        // restart is: nothing is shared but the bytes.
        var (sessions, store, gameId, alice, bob) = await StartedAsync();
        var before = await sessions.Find(gameId)!.MutateAsync(game =>
        {
            for (var guard = 0; guard < 10 && game.State.Choice is { } choice; guard++)
                game.Choose(choice.PlayerId, ["keep"]);

            return game.State;
        });

        var restarted = new GameSessionService(NoAbilities.Instance, store);
        var resumed = await restarted.FindAsync(gameId);

        Assert.NotNull(resumed);
        Assert.Equal(before, await resumed.MutateAsync(game => game.State));

        // Including who is sitting where — seats come back off the log, so authorisation
        // survives the restart rather than being rebuilt from somewhere else.
        Assert.True(await restarted.IsSeatedAsync(gameId, alice));
        Assert.True(await restarted.IsSeatedAsync(gameId, bob));
        Assert.False(await restarted.IsSeatedAsync(gameId, Guid.NewGuid()));
    }

    [Fact]
    public async Task A_resumed_game_can_be_played_on()
    {
        // A restored position that cannot be acted on is a screenshot, not a game.
        var (sessions, store, gameId, _, _) = await StartedAsync();
        await sessions.Find(gameId)!.MutateAsync(game =>
        {
            for (var guard = 0; guard < 10 && game.State.Choice is { } choice; guard++)
                game.Choose(choice.PlayerId, ["keep"]);

            return true;
        });

        var resumed = await new GameSessionService(NoAbilities.Instance, store).FindAsync(gameId);
        var passed = await resumed!.MutateAsync(game =>
        {
            if (game.State.Priority.Holder is not { } holder)
                return false;

            game.PassPriority(holder);
            return true;
        });

        Assert.True(passed);
        // And the play was stored in turn, so the next restart starts from here.
        var stored = await store.LoadAsync(gameId);
        Assert.Contains("PriorityPassed", stored!.Log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removing_a_game_takes_it_off_disk_as_well()
    {
        var (sessions, store, gameId, _, _) = await StartedAsync();

        Assert.True(await sessions.RemoveAsync(gameId));

        Assert.Null(sessions.Find(gameId));
        Assert.Null(await store.LoadAsync(gameId));
        Assert.Null(await sessions.FindAsync(gameId));
    }

    [Fact]
    public async Task A_game_is_stored_before_anybody_has_played_it()
    {
        // Opening hands are dealt during creation, so a game that was only stored on its first
        // action would come back from a restart with different hands than the players saw.
        var (_, store, gameId, _, _) = await StartedAsync();

        Assert.NotNull(await store.LoadAsync(gameId));
    }

    [Fact]
    public async Task A_store_that_fails_does_not_reject_a_legal_play()
    {
        // The move has already happened in a game both players are watching. Throwing here would
        // tell them a legal play was refused, and they would have no way to tell that the game
        // they can see and the answer they got disagree.
        var store = new BrokenStore();
        var sessions = new GameSessionService(NoAbilities.Instance, store);
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        var gameId = await sessions.CreateAsync(
        [
            new PlayerSetup(alice, "Alice", 20, [.. Enumerable.Range(1, 30).Select(i => Card($"A{i}"))]),
            new PlayerSetup(bob, "Bob", 20, [.. Enumerable.Range(1, 30).Select(i => Card($"B{i}"))]),
        ]);

        var session = sessions.Find(gameId)!;
        var answered = await session.MutateAsync(game =>
        {
            for (var guard = 0; guard < 10 && game.State.Choice is { } choice; guard++)
                game.Choose(choice.PlayerId, ["keep"]);

            return true;
        });

        Assert.True(answered);
        // But it is not silent: the sweeper reads this on every tick and logs it, so a store
        // that has gone away is loud rather than being a game that quietly stops being saved.
        Assert.NotNull(session.SaveFailure);
        Assert.Equal([gameId], sessions.NotSaving().Select(s => s.GameId));
    }

    private sealed class BrokenStore : IGameStore
    {
        public Task SaveAsync(StoredGameLog game, CancellationToken ct = default) =>
            Task.FromException(new IOException("The disk is gone."));

        public Task<StoredGameLog?> LoadAsync(Guid gameId, CancellationToken ct = default) =>
            Task.FromResult<StoredGameLog?>(null);

        public Task DeleteAsync(Guid gameId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<Guid>> ExpiredAsync(
            DateTimeOffset before, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);
    }

    [Fact]
    public void The_hub_never_exposes_a_game_state()
    {
        // The structural half of the guarantee. The engine's projection can be correct and still
        // be bypassed if anything on the transport can hand out a GameState, so nothing here is
        // allowed to mention one.
        var offenders = new List<string>();

        foreach (var type in new[] { typeof(GameHub), typeof(GameSession), typeof(GameSessionService) })
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (Mentions(method.ReturnType))
                    offenders.Add($"{type.Name}.{method.Name} returns a GameState");

                foreach (var parameter in method.GetParameters().Where(p => Mentions(p.ParameterType)))
                    offenders.Add($"{type.Name}.{method.Name} takes a GameState ({parameter.Name})");
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (Mentions(property.PropertyType))
                    offenders.Add($"{type.Name}.{property.Name} exposes a GameState");
            }
        }

        Assert.True(offenders.Count == 0, string.Join("\n  ", offenders));
    }

    [Fact]
    public void The_check_would_notice_a_game_state_that_did_leak()
    {
        // Negative control: a reflection check that matched nothing would pass the test above
        // just as quietly.
        Assert.True(Mentions(typeof(GameState)));
        Assert.True(Mentions(typeof(Task<GameState>)));
        Assert.False(Mentions(typeof(GameView)));
        Assert.False(Mentions(typeof(Task<GameView>)));
    }

    /// <summary>Whether a type is, contains, or wraps a <see cref="GameState"/>.</summary>
    private static bool Mentions(Type type)
    {
        if (type == typeof(GameState))
            return true;

        return type.IsGenericType && type.GetGenericArguments().Any(Mentions);
    }
}
