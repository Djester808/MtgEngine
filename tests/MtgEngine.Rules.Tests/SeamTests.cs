using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Sba;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Zones;
using SpellCast = MtgEngine.Rules.Events.SpellCast;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The seams wave 1 asked for and the coordinator cut after it: deathtouch remembered on the
/// recipient (704.5h), the empty-library flag consumed with its loss (704.5b), the turn's
/// history folded by the events that make it, a commander that stays one across zones (903.3),
/// the starting player carried in the state (103.1), and the wave-1 subsystems composed into
/// a game that starts.
/// </summary>
public sealed class SeamTests
{
    private readonly StateBasedActions sba;

    public SeamTests()
    {
        var characteristics = new FakeCharacteristics();
        sba = new StateBasedActions(new FakeZones(), characteristics, new FakeQuery(characteristics));
    }

    [Fact]
    [RuleRef("704.5h")]
    public void A_creature_dealt_damage_by_a_deathtouch_source_is_destroyed()
    {
        var board = new Board();
        var bear = board.Put(Board.Creature(2, 3), damage: 1);
        board.Fold(new ObjectChanged(bear, DealtDeathtouchDamage: true));

        var outcome = sba.Check(board.State);

        Assert.Equal("destroyed", Assert.Single(outcome.Events.OfType<ObjectLeftBy>()).Verb);
        Assert.DoesNotContain(bear, outcome.State.Objects.Keys);
    }

    [Fact]
    [RuleRef("704.5h")]
    public void The_deathtouch_mark_is_consumed_by_the_check_that_read_it()
    {
        // 702.12b: indestructible survives 704.5h, which is what leaves a mark to consume.
        var board = new Board();
        var bear = board.Put(Board.Creature(2, 3, abilities: [new KeywordAbility(KeywordKind.Indestructible)]), damage: 1);
        board.Fold(new ObjectChanged(bear, DealtDeathtouchDamage: true));

        var outcome = sba.Check(board.State);

        Assert.Empty(outcome.Events.OfType<ObjectLeftBy>());
        Assert.False(outcome.State.Objects[bear].DealtDeathtouchDamage);
        Assert.Contains(outcome.Events, e => e is ObjectChanged { DealtDeathtouchDamage: false });

        // "Since the last time state-based actions were checked": the next check sees nothing.
        Assert.Empty(sba.Check(outcome.State).Events);
    }

    [Fact]
    [RuleRef("704.5b")]
    public void Losing_to_an_empty_library_consumes_the_flag()
    {
        var board = new Board(players: 3).DrewFromEmptyLibrary(0);

        var outcome = sba.Check(board.State);

        Assert.Single(outcome.Events.OfType<PlayerLost>());
        var cleared = Assert.Single(outcome.Events.OfType<PlayerFlagCleared>());
        Assert.Equal(new PlayerId(0), cleared.Player);
        Assert.False(outcome.State.Player(new PlayerId(0)).DrewFromEmptyLibrary);
    }

    [Fact]
    [RuleRef("702.2b")]
    public void Combat_damage_from_a_deathtouch_source_marks_what_it_hits()
    {
        var t = new CombatTable();
        var poisoned = t.Creature(t.A, 1, 1, [KeywordKind.Deathtouch]);
        var plain = t.Creature(t.A, 1, 1);
        var wallOne = t.Creature(t.B, 0, 5);
        var wallTwo = t.Creature(t.B, 0, 5);

        t.Attack(t.A, new AttackDeclaration(poisoned, new CombatTarget(t.B, null)), new AttackDeclaration(plain, new CombatTarget(t.B, null)));
        t.Block(t.B, new BlockDeclaration(wallOne, poisoned), new BlockDeclaration(wallTwo, plain));
        var events = t.Strike(firstStrike: false);

        Assert.True(t.State.Objects[wallOne].DealtDeathtouchDamage);
        Assert.False(t.State.Objects[wallTwo].DealtDeathtouchDamage);
        Assert.Contains(events, e => e is DamageDealt { Deathtouch: true, ToObject: { } o } && o == wallOne);
    }

    [Fact]
    [RuleRef("603.10")]
    public void The_turn_history_is_folded_by_the_events_that_make_it_and_cleared_by_the_next_turn()
    {
        var board = new Board();
        var seat = new PlayerId(0);
        var inHand = board.Put(Board.Creature(2, 2, "hand bear"), zone: Zone.Hand);
        var onField = board.Put(Board.Creature(2, 2, "field bear"));

        var entered = new ObjectMoved(inHand, Reborn(board.State, inHand, ZoneKey.Shared(Zone.Battlefield)), -1);
        board.Fold(entered);
        var died = new ObjectMoved(onField, Reborn(board.State, onField, ZoneKey.Of(Zone.Graveyard, seat)), -1);
        board.Fold(
            died,
            new SpellCast(new ObjectId(99), seat, Zone.Hand),
            new DamageDealt(entered.To.Id, null, new PlayerId(1), 2, Combat: false, FromCommander: false));

        var h = board.State.History;
        Assert.Equal(new[] { entered.To.Id }, h.EnteredBattlefield);
        Assert.Equal(new[] { onField }, h.Died.Select(o => o.Id));
        Assert.Equal(new[] { onField }, h.LeftBattlefield.Select(o => o.Id));
        Assert.Equal(seat, Assert.Single(h.SpellsCast).By);
        Assert.Equal(2, h.DamageDealtBy[entered.To.Id]);
        Assert.Equal(2, h.DamageReceivedByPlayer[new PlayerId(1)]);

        board.Fold(new TurnBegan(2, new PlayerId(1), Extra: false));
        Assert.Equal(TurnHistory.Empty, board.State.History);
    }

    [Fact]
    [RuleRef("903.3")]
    public void A_commander_stays_a_commander_when_it_changes_zones()
    {
        var board = new Board();
        var seat = new PlayerId(0);
        var id = new ObjectId(board.State.NextObjectId);
        board.Fold(new ObjectCreated(
            new GameObject(id, Board.Creature(3, 3, "the general"), 0, seat, seat, ZoneKey.Shared(Zone.Command), board.State.NextTimestamp) { IsCommander = true },
            -1));

        var moved = new ZoneSystem().Move(board.State, id, ZoneKey.Shared(Zone.Battlefield));

        var onField = Assert.Single(moved.State.In(ZoneKey.Shared(Zone.Battlefield)));
        Assert.True(moved.State.Objects[onField].IsCommander);
    }

    [Fact]
    [RuleRef("103.1")]
    public void The_starting_player_is_decided_at_creation_and_takes_the_first_turn()
    {
        var setup = new GameSetup([new PlayerStart("One", Deck(), []), new PlayerStart("Two", Deck(), [])], Seed: 7, StartingPlayer: 1);
        var game = new Game(Subsystems.Wave1(), new Definitions());

        var created = game.Create(setup);

        var started = Assert.IsType<GameStarted>(created.Events[0]);
        Assert.Equal(1, started.StartingPlayer);
        Assert.Equal(new PlayerId(1), Assert.Single(created.Events.OfType<TurnBegan>()).ActivePlayer);
        Assert.Equal(new PlayerId(1), created.State.Turn.ActivePlayer);
        Assert.Equal(1, created.State.Turn.TurnOrderIndex);
        Assert.Equal("commander", created.State.Format);
        Assert.Null(created.State.Pending);
        Assert.Null(created.State.Outcome);
    }

    private static GameObject Reborn(GameState state, ObjectId from, ZoneKey to)
    {
        var old = state.Objects[from];
        return new GameObject(new ObjectId(state.NextObjectId), old.Definition, 0, old.Owner, old.Owner, to, state.NextTimestamp);
    }

    private static ImmutableList<string> Deck() => [.. Enumerable.Repeat("oracle-vanilla", 10)];

    private sealed class Definitions : IDefinitionStore
    {
        private static readonly CardDefinition Vanilla = Board.Creature(2, 2, "vanilla bear");

        public CardDefinition? ByName(string name) => name == Vanilla.Name ? Vanilla : null;

        public CardDefinition? ByOracleId(string oracleId) => oracleId == "oracle-vanilla" ? Vanilla : null;
    }
}
