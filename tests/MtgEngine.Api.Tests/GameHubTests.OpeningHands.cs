using MtgEngine.Api.Services;
using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Api.Tests;

public sealed partial class GameHubTests
{
    [Trait("Scope", "Deck")]
    [Fact]
    public async Task Restart_after_an_opening_concession_restores_the_next_players_choice()
    {
        var store = new MemoryLogStore();
        var pool = Deck(ImplementationStatus.Verified);
        var sessions = new GameSessionService(store, pool, new DeckGate(pool));
        var seats = Enumerable.Range(0, 4).Select(index =>
            new NewGameSeat($"user-{index}", $"Player {index}", [.. DeckIds()], [])).ToArray();
        var game = await sessions.CreateAsync(new NewGameRequest(seats, 1, false, "commander", 40));
        var departed = game.State.Pending!.Player;
        var oldChoice = game.State.Pending.Id;
        Assert.IsType<Applied>(await sessions.ApplyAsync(game, new Concede(departed)));

        var restoredSessions = new GameSessionService(store, pool, new DeckGate(pool));
        var restored = await restoredSessions.FindAsync(game.Id);
        Assert.NotNull(restored);
        Assert.Equal(3, restored.State.OpeningHands!.Seats.Count);
        Assert.NotEqual(departed, restored.State.Pending!.Player);
        Assert.IsType<Refused>(await restoredSessions.ApplyAsync(restored,
            new AnswerChoice(departed, oldChoice, [ChoiceKinds.Keep])));
        for (var i = 0; i < 3; i++)
        {
            var pending = restored.State.Pending!;
            Assert.IsType<Applied>(await restoredSessions.ApplyAsync(restored,
                new AnswerChoice(pending.Player, pending.Id, [ChoiceKinds.Keep])));
        }
        Assert.Equal(1, restored.State.Turn.Number);
        Assert.False(restored.State.Player(restored.State.Turn.ActivePlayer).HasLost);
        Assert.Null(restored.State.Pending);
    }

    [Trait("Scope", "Deck")]
    [Fact]
    public async Task A_live_table_starts_with_opening_hand_decisions_and_restores_them_after_restart()
    {
        var store = new MemoryLogStore();
        var pool = Deck(ImplementationStatus.Verified);
        var sessions = new GameSessionService(store, pool, new DeckGate(pool));
        var seats = Enumerable.Range(0, 4).Select(index =>
            new NewGameSeat($"user-{index}", $"Player {index}", [.. DeckIds()], [])).ToArray();
        var game = await sessions.CreateAsync(new NewGameRequest(seats, 1, false, "commander", 40));
        Assert.Equal(0, game.State.Turn.Number);
        Assert.Equal(ChoiceKinds.Mulligan, game.State.Pending?.Kind);
        var restored = await new GameSessionService(store, pool, new DeckGate(pool)).FindAsync(game.Id);
        Assert.NotNull(restored);
        Assert.Equal(game.State.Pending?.Id, restored.State.Pending?.Id);
        Assert.Equal(4, restored.State.OpeningHands?.Seats.Count);
    }

    [Trait("Scope", "Deck")]
    [Fact]
    public async Task Four_seats_keep_private_bottom_choices_and_duplicate_answers_cannot_redraw_twice()
    {
        var store = new MemoryLogStore();
        var pool = Deck(ImplementationStatus.Verified);
        var sessions = new GameSessionService(store, pool, new DeckGate(pool));
        var seats = Enumerable.Range(0, 4).Select(index =>
            new NewGameSeat($"user-{index}", $"Player {index}", [.. DeckIds()], [])).ToArray();
        var game = await sessions.CreateAsync(new NewGameRequest(seats, 2, false, "commander", 40));
        var first = game.State.Pending!;
        var answer = new AnswerChoice(first.Player, first.Id, [ChoiceKinds.Mulligan]);
        var outcomes = await Task.WhenAll(sessions.ApplyAsync(game, answer), sessions.ApplyAsync(game, answer));
        Assert.Single(outcomes.OfType<Applied>());
        Assert.Single(outcomes.OfType<Refused>());
        for (var i = 0; i < 3; i++)
        {
            var pending = game.State.Pending!;
            Assert.IsType<Applied>(await sessions.ApplyAsync(game, new AnswerChoice(pending.Player, pending.Id, [ChoiceKinds.Keep])));
        }
        var again = game.State.Pending!;
        Assert.Equal(first.Player, again.Player);
        Assert.IsType<Applied>(await sessions.ApplyAsync(game, new AnswerChoice(again.Player, again.Id, [ChoiceKinds.Mulligan])));
        var bottom = game.State.Pending!;
        Assert.Equal(ChoiceKinds.MulliganBottom, bottom.Kind);
        Assert.Equal(1, bottom.Min);
        Assert.NotEmpty(game.ProjectFor(first.Player).View!.Pending!.Options);
        foreach (var seat in Enumerable.Range(1, 3))
            Assert.Empty(game.ProjectFor(new PlayerId(seat)).View!.Pending!.Options);
        var resumed = await new GameSessionService(store, pool, new DeckGate(pool)).FindAsync(game.Id);
        Assert.NotNull(resumed);
        Assert.Equal(bottom.Id, resumed.State.Pending!.Id);
        Assert.Equal(bottom.Options.Select(o => o.Id), resumed.State.Pending.Options.Select(o => o.Id));
        Assert.IsType<Applied>(await sessions.ApplyAsync(game, new AnswerChoice(bottom.Player, bottom.Id, [bottom.Options[0].Id])));
        var keep = game.State.Pending!;
        Assert.IsType<Applied>(await sessions.ApplyAsync(game, new AnswerChoice(keep.Player, keep.Id, [ChoiceKinds.Keep])));
        Assert.Equal(1, game.State.Turn.Number);
        Assert.Null(game.State.Pending);
        Assert.Equal(6, game.State.In(ZoneKey.Of(Zone.Hand, first.Player)).Count);
    }
}
