using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Triggers;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The seams cut after wave 2, from the builders' requests: a log that stores combat, a
/// permanent that turns over, a continuous effect that is edited, a move that carries what
/// left, a shuffle that says it was one, a stack object that carries what it triggered on, and
/// the triggers of a command collected before anyone gets priority.
/// </summary>
public sealed class WaveTwoSeamTests
{
    private static readonly PlayerId P0 = new(0);
    private static readonly PlayerId P1 = new(1);

    [Fact]
    [RuleRef("508.1")]
    public void A_log_with_combat_declarations_is_stored_and_read_back()
    {
        var log = new List<GameEvent>
        {
            new GameStarted([new PlayerStart("A", [], []), new PlayerStart("B", [], [])], 1, 20, "test"),
            new AttackersDeclared(P0, ImmutableDictionary<ObjectId, CombatTarget>.Empty.Add(new ObjectId(5), new CombatTarget(P1, null))),
            new BlockersDeclared(P1, ImmutableDictionary<ObjectId, ImmutableList<ObjectId>>.Empty.Add(new ObjectId(5), [new ObjectId(9)])),
        };

        var json = EventLogSerializer.Write(log);
        var read = EventLogSerializer.Read(json);

        Assert.Equal(json, EventLogSerializer.Write(read));
        var attackers = Assert.IsType<AttackersDeclared>(read[1]);
        Assert.Equal(P1, attackers.Attackers[new ObjectId(5)].Player);
        var blockers = Assert.IsType<BlockersDeclared>(read[2]);
        Assert.Equal(new ObjectId(9), Assert.Single(blockers.Blockers[new ObjectId(5)]));
        Assert.Contains("\"5\":", json, StringComparison.Ordinal);
    }

    [Fact]
    [RuleRef("701.27")]
    public void Turning_over_folds_the_face_index_and_the_face_down_flag()
    {
        var board = new Board();
        var id = board.Put(Board.Creature(2, 2));

        board.Fold(new ObjectTurnedOver(id, 1, TurnKind.Transform));
        Assert.Equal(1, board.State.Objects[id].FaceIndex);
        Assert.False(board.State.Objects[id].FaceDown);

        // CR 708.2: turned face down, the face that is up no longer shows.
        board.Fold(new ObjectTurnedOver(id, 0, TurnKind.TurnedFaceDown));
        Assert.True(board.State.Objects[id].FaceDown);
        Assert.Equal(0, board.State.Objects[id].FaceIndex);

        board.Fold(new ObjectTurnedOver(id, 0, TurnKind.TurnedFaceUp));
        Assert.False(board.State.Objects[id].FaceDown);
    }

    [Fact]
    [RuleRef("615.9")]
    public void An_active_effect_is_edited_and_then_ended()
    {
        var board = new Board();
        var shield = new ActiveEffect(1, new PreventDamage(new Fixed(3)), null, P0, [], [], new UntilEndOfTurn(), 1, false);
        var state = board.State with { ActiveEffects = [shield] };

        var spent = new ActiveEffectChanged(1, new PreventDamage(new Fixed(1))).Apply(state);
        Assert.Equal(new Fixed(1), Assert.IsType<PreventDamage>(Assert.Single(spent.ActiveEffects).Effect).Amount);

        var ended = new ActiveEffectChanged(1, null).Apply(spent);
        Assert.Empty(ended.ActiveEffects);

        // An id nobody has is nothing to fold.
        Assert.Same(ended, new ActiveEffectChanged(7, null).Apply(ended));
    }

    [Fact]
    [RuleRef("603.10")]
    public void A_move_carries_the_object_as_it_last_existed_and_a_shuffle_says_it_was_one()
    {
        var board = new Board();
        var id = board.Put(Board.Creature(2, 2), damage: 1);
        var zones = new ZoneSystem();

        var moved = zones.Move(board.State, id, ZoneKey.Of(Zone.Graveyard, P0));

        var move = Assert.Single(moved.Events.OfType<ObjectMoved>());
        Assert.NotNull(move.Departed);
        Assert.Equal(id, move.Departed!.Id);
        Assert.Equal(1, move.Departed.Damage);
        Assert.Equal(Zone.Battlefield, move.Departed.Zone.Zone);

        board.Put(Board.Creature(1, 1, "seam card a"), zone: Zone.Library);
        board.Put(Board.Creature(1, 1, "seam card b"), zone: Zone.Library);
        var shuffled = zones.Shuffle(board.State, P0);
        Assert.True(Assert.Single(shuffled.Events.OfType<ZoneReordered>()).Shuffle);
    }

    [Fact]
    [RuleRef("603.3")]
    public void What_a_command_did_triggers_before_anyone_gets_priority_and_the_trigger_resolves()
    {
        // "When this enters, draw a card." It enters by an honour-system move — the same zone
        // change a cast would end in — so no mana is involved and only the seam is under test.
        var drawer = Board.Creature(2, 2, "seam-drawer", abilities: [new TriggeredAbility(new EntersBattlefield(ObjectFilter.Creature), new Draw(new Fixed(1)))]);
        var filler = Board.Creature(1, 1, "seam-filler");
        var game = new Game(Subsystems.Wave2(), new Definitions(drawer, filler));
        var created = game.Create(new GameSetup(
            [new PlayerStart("One", Deck(filler), [drawer.OracleId]), new PlayerStart("Two", Deck(filler), [])],
            Seed: 3,
            Format: "test"));
        var state = created.State;
        var commander = Assert.Single(state.In(ZoneKey.Shared(Zone.Command)));
        var handBefore = state.In(ZoneKey.Of(Zone.Hand, P0)).Count;

        var entered = Assert.IsType<Applied>(game.Apply(state, new ManualMove(P0, commander, ZoneKey.Shared(Zone.Battlefield))));

        // The trigger went on the stack in the same command, carrying what it triggered on (CR 603.3d).
        var fired = Assert.Single(entered.Events.OfType<TriggerFired>());
        var onStack = Assert.Single(entered.State.In(ZoneKey.Shared(Zone.Stack)));
        Assert.Equal(fired.StackObject, onStack);
        Assert.Equal(fired.Subjects, entered.State.Objects[onStack].Stack!.Triggering);
        Assert.Equal(handBefore, entered.State.In(ZoneKey.Of(Zone.Hand, P0)).Count);

        // CR 608.1: both players pass with it on the stack, and it resolves through the interpreter.
        state = entered.State;
        var first = Assert.IsType<Applied>(game.Apply(state, new PassPriority(state.Priority.Holder!.Value)));
        var second = Assert.IsType<Applied>(game.Apply(first.State, new PassPriority(first.State.Priority.Holder!.Value)));

        Assert.Contains(second.Events, e => e is StackObjectResolved);
        Assert.Empty(second.State.In(ZoneKey.Shared(Zone.Stack)));
        Assert.Equal(handBefore + 1, second.State.In(ZoneKey.Of(Zone.Hand, P0)).Count);
    }

    [Fact]
    [RuleRef("109.5")]
    public void A_filter_can_say_this_object()
    {
        var board = new Board();
        var self = board.Put(Board.Creature(2, 2, "seam self"));
        var other = board.Put(Board.Creature(2, 2, "seam other"));
        var layers = new Layers.LayerSystem();
        var query = new Query.Query(layers);
        var filter = new ObjectFilter(Also: new IsThisObject());
        var context = new EffectContext(self, P0, [], [], null, ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty, ImmutableDictionary<string, string>.Empty, ImmutableDictionary<string, int>.Empty, [], null);

        Assert.True(query.Matches(board.State, filter, self, context));
        Assert.False(query.Matches(board.State, filter, other, context));
    }

    [Fact]
    [RuleRef("702.29a")]
    public void Discarding_this_card_pays_a_cycling_cost()
    {
        // Cycling as the vocabulary spells it: an ability that works from the hand, whose cost is this card.
        var cycler = Board.Creature(1, 1, "seam-cycler", abilities: [new ActivatedAbility([new DiscardSelfCost()], new Draw(new Fixed(1)), Where: Zone.Hand)]);
        var game = new Game(Subsystems.Wave2(), new Definitions(cycler));
        var state = game.Create(new GameSetup([new PlayerStart("One", Deck(cycler), []), new PlayerStart("Two", Deck(cycler), [])], Seed: 5, Format: "test")).State;
        var card = state.In(ZoneKey.Of(Zone.Hand, P0))[0];
        var handBefore = state.In(ZoneKey.Of(Zone.Hand, P0)).Count;

        var activated = Assert.IsType<Applied>(game.Apply(state, new ActivateAbility(P0, card, 0)));

        Assert.Contains(activated.Events, e => e is ObjectLeftBy { Verb: "discarded" });
        Assert.Equal(handBefore - 1, activated.State.In(ZoneKey.Of(Zone.Hand, P0)).Count);
        Assert.Single(activated.State.In(ZoneKey.Of(Zone.Graveyard, P0)));
        Assert.Single(activated.State.In(ZoneKey.Shared(Zone.Stack)));

        state = activated.State;
        var first = Assert.IsType<Applied>(game.Apply(state, new PassPriority(state.Priority.Holder!.Value)));
        var second = Assert.IsType<Applied>(game.Apply(first.State, new PassPriority(first.State.Priority.Holder!.Value)));
        Assert.Equal(handBefore, second.State.In(ZoneKey.Of(Zone.Hand, P0)).Count);
    }

    private static ImmutableList<string> Deck(CardDefinition filler) => [.. Enumerable.Repeat(filler.OracleId, 10)];

    private sealed class Definitions(params CardDefinition[] definitions) : IDefinitionStore
    {
        public CardDefinition? ByName(string name) => definitions.FirstOrDefault(d => d.Name == name);

        public CardDefinition? ByOracleId(string oracleId) => definitions.FirstOrDefault(d => d.OracleId == oracleId);
    }
}
