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

    /// <summary>
    /// CR 603.6: the zone an object left is part of the move, so a stored game answers a
    /// zone-change trigger the same way a live one does. <c>ObjectMoved.LeftFrom</c> is derived and
    /// must not be written, or a log would carry two spellings of one fact.
    /// </summary>
    [Fact]
    [RuleRef("603.6")]
    public void A_move_round_trips_the_zone_it_left()
    {
        var definition = new CardDefinition("c", "c", Layout.Normal, [new Face("c", FaceRole.Main, new TypeLine(CardType.Creature))]);
        var owner = new PlayerId(0);
        var graveyard = ZoneKey.Of(Zone.Graveyard, owner);
        var departed = new GameObject(new ObjectId(1), definition, 0, owner, owner, graveyard, 1);
        var arrived = new GameObject(new ObjectId(2), definition, 0, owner, owner, ZoneKey.Of(Zone.Exile, owner), 2);
        var recorded = new ObjectMoved(new ObjectId(1), arrived, -1, FromZone: graveyard);
        var carried = new ObjectMoved(new ObjectId(1), arrived, -1, Departed: departed);

        var json = EventLogSerializer.Write([recorded, carried]);
        var back = EventLogSerializer.Read(json);

        Assert.DoesNotContain("leftFrom", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(graveyard, Assert.IsType<ObjectMoved>(back[0]).FromZone);
        Assert.Equal(graveyard, Assert.IsType<ObjectMoved>(back[0]).LeftFrom);

        // A move that carries the object that left says the zone by that object, which is why every
        // move the zone system makes already answers and no site had to change.
        Assert.Null(Assert.IsType<ObjectMoved>(back[1]).FromZone);
        Assert.Equal(graveyard, Assert.IsType<ObjectMoved>(back[1]).LeftFrom);
    }

    /// <summary>
    /// CR 701.23a with 701.23f: a search of a library is a logged event so that an ability which
    /// triggers on a library being searched has something to match, and because looking at a
    /// library changes nothing, folding the event returns the state untouched. The owner of the
    /// library and the searcher are separate fields — an effect can send a player into another
    /// player's library — so a stored game keeps both.
    /// </summary>
    [Fact]
    [RuleRef("701.23")]
    public void A_library_search_round_trips_and_folds_to_the_same_state()
    {
        var started = new GameStarted([new PlayerStart("A", [], []), new PlayerStart("B", [], [])], Seed: 9, StartingLife: 20, Format: "test");
        var own = new LibrarySearched(new PlayerId(0), new PlayerId(0));
        var theirs = new LibrarySearched(new PlayerId(1), new PlayerId(0));

        var json = EventLogSerializer.Write([started, own, theirs]);
        var back = EventLogSerializer.Read(json);

        Assert.Contains("LibrarySearched", json, StringComparison.Ordinal);
        Assert.Equal(own, Assert.IsType<LibrarySearched>(back[1]));
        var other = Assert.IsType<LibrarySearched>(back[2]);
        Assert.Equal(new PlayerId(1), other.Player);
        Assert.Equal(new PlayerId(0), other.Searcher);

        // CR 701.23a: the search itself is not a change to the game, so the fold is the identity —
        // a replay of a log that carries it lands where a log without it lands.
        var state = GameReducer.Replay([started]);
        Assert.Same(state, own.Apply(state));
        Assert.Same(state, other.Apply(state));
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
