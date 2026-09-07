using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The log is the game. These tests hold the two properties that make that true: every event
/// type is named in events.txt so a stored game stays readable, and folding a log gives the
/// state the events produced.
/// </summary>
public sealed class EventLogTests
{
    [Fact]
    public void Every_event_type_is_listed_in_events_txt()
    {
        var listed = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "events.txt"))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(listed, EventLogSerializer.KnownEventNames);
    }

    [Fact]
    public void A_log_round_trips_and_replays_to_the_same_state()
    {
        var started = new GameStarted([new PlayerStart("A", [], []), new PlayerStart("B", [], [])], Seed: 42, StartingLife: 20, Format: "test");
        var log = new List<GameEvent>
        {
            started,
            new LifeChanged(new PlayerId(1), -3, "test"),
            new TurnBegan(1, new PlayerId(0), Extra: false),
            new StepBegan(Step.PrecombatMain),
            new PriorityGranted(new PlayerId(0)),
        };

        var json = EventLogSerializer.Write(log);
        var back = EventLogSerializer.Read(json);

        Assert.Equal(log.Count, back.Count);
        var replayed = GameReducer.Replay(back);
        var folded = GameReducer.Replay(log);
        Assert.Equal(17, replayed.Player(new PlayerId(1)).Life);
        Assert.Equal(folded.Turn, replayed.Turn);
        Assert.Equal(folded.Priority.Holder, replayed.Priority.Holder);
        Assert.Equal(42UL, replayed.Rng.Seed);
    }

    [Fact]
    public void Concession_ends_a_two_player_game()
    {
        var started = new GameStarted([new PlayerStart("A", [], []), new PlayerStart("B", [], [])], 1, 20, "test");
        var state = GameReducer.Replay([started]);
        var game = new Game(Subsystems.AllStubs(), new EmptyStore());

        var result = Assert.IsType<Applied>(game.Apply(state, new Commands.Concede(new PlayerId(0))));

        Assert.NotNull(result.State.Outcome);
        Assert.Equal([new PlayerId(1)], result.State.Outcome!.Winners);
        Assert.Contains(result.Events, e => e is PlayerLost);
        Assert.Contains(result.Events, e => e is GameEnded);
    }

    [Fact]
    public void A_command_after_the_game_ends_is_refused_with_the_rule()
    {
        var started = new GameStarted([new PlayerStart("A", [], []), new PlayerStart("B", [], [])], 1, 20, "test");
        var state = GameReducer.Replay([started, new GameEnded(new GameOutcome([new PlayerId(0)], false, "test"))]);
        var game = new Game(Subsystems.AllStubs(), new EmptyStore());

        var refused = Assert.IsType<Refused>(game.Apply(state, new Commands.PassPriority(new PlayerId(0))));
        Assert.Equal("104.1", refused.Rule);
    }

    [Fact]
    public void The_rng_is_deterministic_for_a_seed()
    {
        var a = Rng.Shuffle(new RngState(7, 0), Enumerable.Range(0, 20).ToList());
        var b = Rng.Shuffle(new RngState(7, 0), Enumerable.Range(0, 20).ToList());
        var c = Rng.Shuffle(new RngState(8, 0), Enumerable.Range(0, 20).ToList());

        Assert.Equal(a.Shuffled, b.Shuffled);
        Assert.NotEqual(a.Shuffled, c.Shuffled);
        Assert.Equal(19, a.Next.Counter);
    }

    private sealed class EmptyStore : IDefinitionStore
    {
        public CardDefinition? ByName(string name) => null;

        public CardDefinition? ByOracleId(string oracleId) => null;
    }
}
