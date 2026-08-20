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

    private static (GameSessionService Sessions, Guid GameId, Guid Alice, Guid Bob) Started()
    {
        var sessions = new GameSessionService(NoAbilities.Instance);
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        var gameId = sessions.Create(
        [
            new PlayerSetup(alice, "Alice", 20, [.. Enumerable.Range(1, 30).Select(i => Card($"A{i}"))]),
            new PlayerSetup(bob, "Bob", 20, [.. Enumerable.Range(1, 30).Select(i => Card($"B{i}"))]),
        ],
            seed: 42);

        return (sessions, gameId, alice, bob);
    }

    [Fact]
    public async Task A_session_gives_each_player_their_own_view()
    {
        var (sessions, gameId, alice, bob) = Started();
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
        var (sessions, gameId, alice, bob) = Started();
        var session = sessions.Find(gameId)!;

        var json = JsonSerializer.Serialize(await session.ReadAsync(alice));

        // Named exactly, not by prefix: "B" alone matches the Battlefield property and would
        // have failed for a reason that has nothing to do with a leak.
        foreach (var name in Enumerable.Range(1, 30).Select(i => $"\"B{i}\""))
            Assert.DoesNotContain(name, json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_player_who_is_not_seated_is_not_authorised()
    {
        var (sessions, gameId, alice, _) = Started();

        Assert.True(sessions.IsSeated(gameId, alice));
        Assert.False(sessions.IsSeated(gameId, Guid.NewGuid()));
    }

    [Fact]
    public async Task Actions_are_serialised_per_game()
    {
        // One game is one critical section: Game is not thread-safe on purpose, so the session
        // is what makes it safe to share between two players' connections.
        var (sessions, gameId, alice, bob) = Started();
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
        var (sessions, gameId, _, _) = Started();

        var log = await sessions.Find(gameId)!.LogAsync();

        Assert.NotEmpty(log);
        Assert.Contains(log, line => line.Contains("started", StringComparison.Ordinal));
    }

    [Fact]
    public void Idle_games_are_swept_and_live_ones_are_not()
    {
        var (sessions, gameId, _, _) = Started();

        Assert.Empty(sessions.Stale(DateTimeOffset.UtcNow));
        Assert.Equal([gameId], sessions.Stale(DateTimeOffset.UtcNow + GameSessionService.Idle * 2));

        Assert.True(sessions.Remove(gameId));
        Assert.Null(sessions.Find(gameId));
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
