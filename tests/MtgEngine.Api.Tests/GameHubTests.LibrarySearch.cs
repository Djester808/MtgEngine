using System.Text.Json;
using MtgEngine.Api.Services;
using MtgEngine.Cards;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Api.Tests;

public sealed partial class GameHubTests
{
    [Trait("Scope", "Deck")]
    [Fact]
    public async Task Four_player_library_search_resumes_after_restart_and_accepts_only_one_authorized_answer()
    {
        var searcher = new PlayerId(0);
        var owner = new PlayerId(1);
        var users = new[] { Alice, Bob, "user-three", "user-four" };
        var pool = Deck(ImplementationStatus.Verified);
        var store = new MemoryLogStore();
        var log = new List<GameEvent>
        {
            new GameStarted([.. users.Select(user => new PlayerStart(user, [], []))], 42, 40, "commander")
            { EnforcedOracleIds = [] },
            new TurnBegan(1, searcher, false), new StepBegan(Step.PrecombatMain), new PriorityGranted(searcher),
        };
        var definition = new MtgEngine.Cards.CardDefinition("library-fixture", "Private library fixture", Layout.Normal,
            [new Face("Private library fixture", FaceRole.Main, new TypeLine(CardType.Land))]);
        for (var i = 1; i <= 3; i++)
            log.Add(new ObjectCreated(new GameObject(new ObjectId(i), definition, 0, owner, owner,
                ZoneKey.Of(Zone.Library, owner), i), -1));
        var spell = definition with
        {
            OracleId = "search-fixture",
            Faces = [new Face("Search fixture", FaceRole.Main, new TypeLine(CardType.Instant),
                Abilities: [new SpellAbility(new Search(ObjectFilter.Any, Zone.Exile,
                    LibraryOf: new TargetPlayer(PlayerFilter.Any)))])],
        };
        log.Add(new ObjectCreated(new GameObject(new ObjectId(4), spell, 0, searcher, searcher,
            ZoneKey.Shared(Zone.Stack), 4)
        { Stack = new StackEntry(null, null, [], [owner], [], null, false) }, -1));
        log.AddRange(Subsystems.Wave2().Interpreter.Resolve(GameReducer.Replay(log), new ObjectId(4)).Events);
        var id = Guid.NewGuid();
        await store.CreateAsync(new PersistedGame
        {
            Id = id,
            SeatsJson = JsonSerializer.Serialize(users),
            LogJson = EventLogSerializer.Write(log),
            EventCount = log.Count,
        });

        var sessions = new GameSessionService(store, pool, new DeckGate(pool));
        var restored = await sessions.FindAsync(id);
        Assert.NotNull(restored);
        var pending = restored.State.Pending!;
        Assert.Equal(searcher, pending.Player);
        Assert.Equal(3, pending.Options.Count);
        for (var seat = 0; seat < 4; seat++)
        {
            var view = restored.ProjectFor(new PlayerId(seat)).View!;
            Assert.Equal(seat == 0 ? 3 : 0, view.ChoiceCards.Count);
            Assert.Equal(seat == 0 ? 3 : 0, view.Pending!.Options.Count);
        }
        var before = restored.Log.Count;
        Assert.IsType<Refused>(await sessions.ApplyAsync(restored,
            new AnswerChoice(owner, pending.Id, [pending.Options[0].Id])));
        Assert.IsType<Refused>(await sessions.ApplyAsync(restored,
            new AnswerChoice(searcher, new ChoiceId(pending.Id.Value + 1), [pending.Options[0].Id])));
        Assert.IsType<Refused>(await sessions.ApplyAsync(restored,
            new AnswerChoice(searcher, pending.Id, ["not-offered"])));
        Assert.Equal(before, restored.Log.Count);
        Assert.Equal(0, store.Saves);

        var answer = new AnswerChoice(searcher, pending.Id, [pending.Options[0].Id]);
        var results = await Task.WhenAll(sessions.ApplyAsync(restored, answer), sessions.ApplyAsync(restored, answer));
        Assert.Single(results.OfType<Applied>());
        Assert.Single(results.OfType<Refused>());
        Assert.Equal(1, store.Saves);
        var reloadedSessions = new GameSessionService(store, pool, new DeckGate(pool));
        var completed = await reloadedSessions.FindAsync(id);
        Assert.NotNull(completed);
        Assert.Null(completed.State.Pending);
        Assert.Equal(2, completed.State.In(ZoneKey.Of(Zone.Library, owner)).Count);
        Assert.Single(completed.State.In(ZoneKey.Shared(Zone.Exile)));
        Assert.Single(completed.Log.OfType<LibrarySearched>());
        Assert.Single(completed.Log.OfType<ZoneReordered>(), e => e.Shuffle);
        Assert.IsType<Refused>(await reloadedSessions.ApplyAsync(completed, answer));
        Assert.Equal(1, store.Saves);
        foreach (var seat in Enumerable.Range(0, 4))
            Assert.Empty(completed.ProjectFor(new PlayerId(seat)).View!.ChoiceCards);
    }
}
