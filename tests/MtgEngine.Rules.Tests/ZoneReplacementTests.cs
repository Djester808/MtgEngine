using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Faces;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The zone system proposes its events to the replacement system, CR 614.1c and 616.1. Until
/// this existed <see cref="IReplacementSystem.Replace(GameState, GameEvent, PlayerId)"/> was called for damage and nothing else,
/// so every "enters tapped", "enters with counters" and "exile it instead" compiled, linted and
/// did nothing at a table. Each test here asserts what happened to the board and to the log,
/// because the log is the game.
/// </summary>
public sealed class ZoneReplacementTests
{
    private static readonly IDefinitionStore Store = new ZoneReplacementDefinitions();

    private static readonly ZoneKey Battlefield = ZoneKey.Shared(Zone.Battlefield);

    /* ------------------------------------------------------------------ *
     * CR 614.1c through the real replacement system.
     * ------------------------------------------------------------------ */

    /// <summary>CR 614.12: the land's own replacement is part of its entering, so it arrives tapped.</summary>
    [Fact]
    public void A_land_enters_tapped_when_a_replacement_system_is_attached()
    {
        var state = Board(ZoneReplacementDefinitions.SlowLand);
        var zones = WithRealReplacements();
        var land = First(state, ZoneReplacementDefinitions.SlowLand);

        var outcome = zones.Move(state, land, Battlefield);

        var moved = Assert.IsType<ObjectMoved>(Assert.Single(outcome.Events));
        Assert.True(moved.To.Tapped);
        Assert.True(outcome.State.Objects[moved.To.Id].Tapped);
        AssertLogFolds(state, outcome);
    }

    /// <summary>
    /// The same card and the same move with no replacement system: the event stands as proposed.
    /// That is what a wave-1 composition does, and it is why the defect was invisible.
    /// </summary>
    [Fact]
    public void The_same_land_enters_untapped_with_no_replacement_system_attached()
    {
        var state = Board(ZoneReplacementDefinitions.SlowLand);
        var zones = new ZoneSystem();
        var land = First(state, ZoneReplacementDefinitions.SlowLand);

        var outcome = zones.Move(state, land, Battlefield);

        var moved = Assert.IsType<ObjectMoved>(Assert.Single(outcome.Events));
        Assert.Null(zones.Replacements);
        Assert.False(moved.To.Tapped);
    }

    /* ------------------------------------------------------------------ *
     * What comes back: a different destination, nothing, or a question.
     * ------------------------------------------------------------------ */

    /// <summary>CR 614.1a: "if it would die, exile it instead" — the card ends in exile, not the graveyard.</summary>
    [Fact]
    public void A_creature_that_would_die_is_exiled_instead()
    {
        var state = Board(ZoneReplacementDefinitions.Creature);
        var plain = new ZoneSystem();
        state = plain.Move(state, First(state, ZoneReplacementDefinitions.Creature), Battlefield).State;
        var creature = state.In(Battlefield).Single();

        var zones = new ZoneSystem
        {
            Replacements = new ZoneReplacementFake { Instead = ExileInstead },
        };

        var outcome = zones.Move(state, creature, ZoneKey.Of(Zone.Graveyard, new PlayerId(0)));

        var moved = Assert.IsType<ObjectMoved>(Assert.Single(outcome.Events));
        Assert.Equal(Zone.Exile, moved.To.Zone.Zone);
        Assert.Single(outcome.State.In(ZoneKey.Shared(Zone.Exile)));
        Assert.Empty(outcome.State.In(ZoneKey.Of(Zone.Graveyard, new PlayerId(0))));
        Assert.Empty(outcome.State.In(Battlefield));
        AssertLogFolds(state, outcome);
    }

    /// <summary>
    /// CR 121.1 and 614.1: a replaced draw is not a draw with the card put back — the card never
    /// leaves the library, because drawing is one action and the whole action was replaced.
    /// </summary>
    [Fact]
    public void A_prevented_draw_draws_nothing_and_leaves_the_library_as_it_was()
    {
        var state = Board(ZoneReplacementDefinitions.Creature, ZoneReplacementDefinitions.Creature);
        var library = ZoneKey.Of(Zone.Library, new PlayerId(0));
        var before = state.In(library);

        var fake = new ZoneReplacementFake { Instead = ZoneReplacementFake.Nothing };
        var outcome = new ZoneSystem { Replacements = fake }.Draw(state, new PlayerId(0), 1);

        Assert.Empty(outcome.Events);
        Assert.Equal(before, outcome.State.In(library));
        Assert.Empty(outcome.State.In(ZoneKey.Of(Zone.Hand, new PlayerId(0))));
        Assert.Equal(0, outcome.State.Player(new PlayerId(0)).ObjectsDrawnThisTurn);

        // CR 616.1: the drawing player is the affected one, and the draw itself is what was offered.
        Assert.IsType<ObjectDrawn>(Assert.Single(fake.Offered));
        Assert.Equal(new PlayerId(0), Assert.Single(fake.Affected));
    }

    /// <summary>
    /// CR 616.1 and 101.4: two effects apply, the affected player has to choose, and until they do
    /// the object has not moved. The rest of the batch waits with it.
    /// </summary>
    [Fact]
    public void A_replacement_that_stops_to_ask_leaves_the_object_where_it_was()
    {
        var state = Board(ZoneReplacementDefinitions.Creature);
        var creature = First(state, ZoneReplacementDefinitions.Creature);
        var library = ZoneKey.Of(Zone.Library, new PlayerId(0));

        var zones = new ZoneSystem { Replacements = new ZoneReplacementFake { Instead = ZoneReplacementFake.Asks } };
        var outcome = zones.Move(state, creature, Battlefield);

        Assert.IsType<ChoiceRequested>(Assert.Single(outcome.Events));
        Assert.NotNull(outcome.State.Pending);
        Assert.Equal(ChoiceKinds.ReplacementOrder, outcome.State.Pending!.Kind);
        Assert.Empty(outcome.State.In(Battlefield));
        Assert.Contains(creature, outcome.State.In(library));
        AssertLogFolds(state, outcome);
    }

    /// <summary>CR 616.1: a pending question stops the rest of a multi-card draw, not just its own card.</summary>
    [Fact]
    public void A_question_on_the_first_draw_stops_the_rest_of_the_draw()
    {
        var state = Board(ZoneReplacementDefinitions.Creature, ZoneReplacementDefinitions.Creature, ZoneReplacementDefinitions.Creature);
        var fake = new ZoneReplacementFake { Instead = ZoneReplacementFake.Asks };

        var outcome = new ZoneSystem { Replacements = fake }.Draw(state, new PlayerId(0), 3);

        Assert.IsType<ChoiceRequested>(Assert.Single(outcome.Events));
        Assert.Single(fake.Offered);
        Assert.Equal(3, outcome.State.In(ZoneKey.Of(Zone.Library, new PlayerId(0))).Count);
    }

    /// <summary>CR 400.7 and 704.5m: every move in a batch is read against the board before any of them happened.</summary>
    [Fact]
    public void Every_move_in_a_batch_is_offered_against_the_one_board()
    {
        var state = Board(ZoneReplacementDefinitions.Creature, ZoneReplacementDefinitions.Creature);
        var plain = new ZoneSystem();
        foreach (var id in state.In(ZoneKey.Of(Zone.Library, new PlayerId(0))).ToList())
            state = plain.Move(state, id, Battlefield).State;

        var board = state.In(Battlefield);
        var fake = new ZoneReplacementFake();
        var zones = new ZoneSystem { Replacements = fake };
        var graveyard = ZoneKey.Of(Zone.Graveyard, new PlayerId(0));

        var outcome = zones.MoveAll(state, [.. board.Select(id => new MoveRequest(id, graveyard))]);

        // Both were offered, both with the departing object still on the battlefield beside the other.
        Assert.Equal(2, fake.Offered.Count);
        Assert.All(fake.Offered, e => Assert.Equal(2, board.Count));
        Assert.Equal(2, outcome.State.In(graveyard).Count);
        Assert.Empty(outcome.State.In(Battlefield));

        // The two new objects are distinct: ids and timestamps come from the running state.
        var ids = outcome.Events.OfType<ObjectMoved>().Select(m => m.To.Id).ToList();
        Assert.Equal(2, ids.Distinct().Count());
        AssertLogFolds(state, outcome);
    }

    /* ------------------------------------------------------------------ *
     * The two overloads wave 3 asked for.
     * ------------------------------------------------------------------ */

    /// <summary>CR 712.12: a modal double-faced card's land face is played, so the move says which face is up.</summary>
    [Fact]
    public void A_move_with_a_face_index_rebirths_the_object_showing_that_face()
    {
        var state = Board(ZoneReplacementDefinitions.Modal);
        var zones = new ZoneSystem();
        var card = First(state, ZoneReplacementDefinitions.Modal);

        var outcome = zones.Move(state, card, Battlefield, Placement.Default, tapped: false, controller: null, faceIndex: 1);

        var moved = Assert.IsType<ObjectMoved>(Assert.Single(outcome.Events));
        Assert.Equal(1, moved.To.FaceIndex);
        Assert.Equal(CardType.Land, moved.To.Face.Types.Types);

        // Every other caller is unchanged: no index means the face the object already had.
        Assert.Equal(0, Assert.IsType<ObjectMoved>(Assert.Single(zones.Move(state, card, Battlefield).Events)).To.FaceIndex);
    }

    /// <summary>An index that names no face on the card is a bug at the seam, not a rules situation.</summary>
    [Fact]
    public void A_face_index_the_card_does_not_have_is_refused()
    {
        var state = Board(ZoneReplacementDefinitions.Modal);
        var card = First(state, ZoneReplacementDefinitions.Modal);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ZoneSystem().Move(state, card, Battlefield, Placement.Default, tapped: false, controller: null, faceIndex: 2));
    }

    /// <summary>
    /// CR 707.2 and 111.4: a token that is a copy has the copiable values of what it copies and
    /// nothing else — no counters, no damage, and it arrives through the zone system like anything
    /// else, so CR 614's "if a token would be created" reaches it.
    /// </summary>
    [Fact]
    public void A_copy_token_has_the_copiable_values_and_no_counters()
    {
        var state = Board(ZoneReplacementDefinitions.Modal);
        var plain = new ZoneSystem();
        var card = First(state, ZoneReplacementDefinitions.Modal);
        state = plain.Move(state, card, Battlefield, Placement.Default, tapped: false, controller: null, faceIndex: 1).State;
        var original = state.Objects[state.In(Battlefield).Single()];

        var fake = new ZoneReplacementFake();
        var zones = new ZoneSystem { Replacements = fake };

        var outcome = zones.CreateToken(state, CopyRules.CopyOf(original), new PlayerId(1), tapped: true, attacking: false);

        var created = Assert.IsType<ObjectCreated>(Assert.Single(outcome.Events)).Object;
        Assert.Equal(original.Definition, created.Definition);
        Assert.Equal(1, created.FaceIndex);
        Assert.True(created.IsToken);
        Assert.True(created.Tapped);
        Assert.Empty(created.Counters);
        Assert.Equal(0, created.Damage);
        Assert.Equal(new PlayerId(1), created.Owner);
        Assert.Equal(new PlayerId(1), created.InitialController);

        // CR 614.1c: the creation was offered, and its controller is the affected player.
        Assert.IsType<ObjectCreated>(Assert.Single(fake.Offered));
        Assert.Equal(new PlayerId(1), Assert.Single(fake.Affected));
        AssertLogFolds(state, outcome);
    }

    /// <summary>CR 614.1: a token creation replaced away creates nothing, and there is nothing to attack with.</summary>
    [Fact]
    public void A_token_creation_replaced_away_creates_nothing()
    {
        var state = Board(ZoneReplacementDefinitions.Creature);
        var zones = new ZoneSystem { Replacements = new ZoneReplacementFake { Instead = ZoneReplacementFake.Nothing } };

        var outcome = zones.CreateToken(state, Trooper(), new PlayerId(0), tapped: false, attacking: true);

        Assert.Empty(outcome.Events);
        Assert.Empty(outcome.State.In(Battlefield));
    }

    /* ------------------------------------------------------------------ *
     * Fixture.
     * ------------------------------------------------------------------ */

    /// <summary>The property every subsystem owes: the state it hands back is the fold of the events it hands back.</summary>
    private static void AssertLogFolds(GameState before, Outcome outcome) =>
        Assert.Equal(Describe(GameReducer.Apply(before, outcome.Events)), Describe(outcome.State));

    private static string Describe(GameState state) =>
        string.Join(
            "|",
            state.Zones
                .OrderBy(z => z.Key.Zone)
                .ThenBy(z => z.Key.Player?.Value ?? -1)
                .Select(z => $"{z.Key.Zone}/{z.Key.Player}:{string.Join(",", z.Value.Select(id => $"{id.Value}t{(state.Objects[id].Tapped ? 1 : 0)}f{state.Objects[id].FaceIndex}"))}"));

    private static ObjectId First(GameState state, string oracleId) =>
        state.Objects.Values.First(o => string.Equals(o.Definition.OracleId, oracleId, StringComparison.Ordinal)).Id;

    /// <summary>A land that would enter tapped, read through the real query and layer systems.</summary>
    private static ZoneSystem WithRealReplacements()
    {
        var layers = new MtgEngine.Rules.Layers.LayerSystem();
        var query = new MtgEngine.Rules.Query.Query(layers);
        return new ZoneSystem
        {
            Replacements = new MtgEngine.Rules.Replacement.ReplacementSystem(query, layers, new ZoneReplacementNoInterpreter()),
        };
    }

    /// <summary>"Exile it instead", CR 614.1a, without an interpreter: the destination changes and nothing else.</summary>
    private static Outcome ExileInstead(GameState state, GameEvent proposed)
    {
        var moved = (ObjectMoved)proposed;
        var exiled = moved with { To = moved.To with { Zone = ZoneKey.Shared(Zone.Exile) }, Position = -1 };
        return new Outcome(exiled.Apply(state), [exiled]);
    }

    /// <summary>A made-up token, so no card's text decides what this test proves.</summary>
    private static TokenSpec Trooper() =>
        new("Test Trooper", Color.White, CardType.Creature, ["Trooper"], Power: 1, Toughness: 1);

    /// <summary>A two-player game whose libraries hold exactly these cards and whose opening hands are empty.</summary>
    private static GameState Board(params string[] deck)
    {
        ImmutableList<string> cards = [.. deck];
        var setup = new GameSetup(
            [new PlayerStart("One", cards, []), new PlayerStart("Two", [], [])],
            Seed: 11,
            OpeningHandSize: 0);
        return new ZoneSystem().Create(setup, Store).State;
    }
}
