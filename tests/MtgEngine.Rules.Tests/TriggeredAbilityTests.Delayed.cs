using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Interpreter;
using MtgEngine.Rules.Mana;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Triggers;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The delayed ability CR 106.6a creates, played through the engine's front door: the spend event
/// is the one it watches (CR 603.7), the firing spends it (CR 603.7b), and — the part that was
/// missing — the stack object it makes carries the instruction, so the resolution has something to
/// run. A delayed ability's effect is held in the game state and not printed on any card, so an
/// index into the source's ability list cannot name it; stacked without one it resolved to nothing
/// while the firing had already discarded it, which is why the card's second sentence disappeared.
/// </summary>
public sealed partial class TriggeredAbilityTests
{
    /// <summary>
    /// CR 106.6a with 603.7: "{T}: Add {G}. When that mana is spent to cast a spell, draw a card."
    /// The mana carries the key, the spend folds that key back with a negative amount, and the
    /// delayed ability fires once and draws. Everything before the resolution goes through
    /// <see cref="Game.Settle"/> on <see cref="Subsystems.Wave2"/> — the engine's own settle loop,
    /// CR 117.5 — and the resolution through the two passes of priority that empty the stack.
    /// </summary>
    [Fact]
    [RuleRef("106.6a")]
    public void AKeyedSpendFiresItsDelayedAbilityAndTheCardIsDrawn()
    {
        var table = new DelayedManaTable();
        var spell = table.Board.Id(table.Board.Put(TriggeredAbilityBoard.Creature("ox"), zone: Zone.Stack));

        var settled = table.Spend(TheKey + "#0", spell);

        Assert.Single(settled.Events.OfType<TriggerFired>());
        Assert.Empty(settled.State.DelayedTriggers);

        var drawn = table.EmptyTheStack(settled.State);
        Assert.Equal(table.LibraryBefore - 1, drawn.In(ZoneKey.Of(Zone.Library, DelayedManaTable.Controller)).Count);
        Assert.Single(drawn.In(ZoneKey.Of(Zone.Hand, DelayedManaTable.Controller)));
    }

    /// <summary>
    /// The other half of CR 106.6a: mana that is not that mana is not the event the ability watches.
    /// Nothing fires, nothing is drawn, and — the state EINT4 § 1 found was not kept — the ability
    /// is still in <see cref="GameState.DelayedTriggers"/> afterwards, waiting for its own mana.
    /// </summary>
    [Fact]
    [RuleRef("106.6a")]
    public void OtherManaDoesNotFireItAndLeavesTheAbilityWaiting()
    {
        var table = new DelayedManaTable();
        var spell = table.Board.Id(table.Board.Put(TriggeredAbilityBoard.Creature("ox"), zone: Zone.Stack));

        var settled = table.Spend(key: null, spell);

        Assert.Empty(settled.Events.OfType<TriggerFired>());
        Assert.DoesNotContain(settled.State.In(ZoneKey.Shared(Zone.Stack)), id => settled.State.Objects[id].Stack?.Source is not null);
        var waiting = Assert.Single(settled.State.DelayedTriggers);
        Assert.Equal(new ManaSpent(TheKey + "#0", AnySpell), waiting.When);

        // And it is still the ability it was: the mana it does watch fires it from that same state.
        var later = table.Spend(TheKey + "#0", spell, settled.State);
        Assert.Single(later.Events.OfType<TriggerFired>());
        Assert.Empty(later.State.DelayedTriggers);
    }

    /// <summary>
    /// CR 106.6a: the ability fires once and is spent (CR 603.7b), so a second spend of mana under
    /// the same key finds nothing to fire — the draw does not happen twice.
    /// </summary>
    [Fact]
    [RuleRef("603.7b")]
    public void ADelayedAbilityFiresOnceAndIsSpent()
    {
        var table = new DelayedManaTable();
        var spell = table.Board.Id(table.Board.Put(TriggeredAbilityBoard.Creature("ox"), zone: Zone.Stack));

        var first = table.Spend(TheKey + "#0", spell);
        var second = table.Spend(TheKey + "#0", spell, first.State);

        Assert.Single(first.Events.OfType<TriggerFired>());
        Assert.Empty(second.Events.OfType<TriggerFired>());
    }

    /// <summary>
    /// CR 106.6: the two halves of a linked pair may sit on different objects, and then the
    /// resolution that created the delayed ability produced no mana to key it to and leaves the bare
    /// key the card wrote. Every single mana under that key is mana it watches, so a "k#0" block
    /// fires an ability that names "k" — while a key that merely begins the same way is another key.
    /// </summary>
    [Fact]
    [RuleRef("106.6")]
    public void ABareKeyWatchesEverySingleManaUnderIt()
    {
        var table = new DelayedManaTable(TheKey);
        var spell = table.Board.Id(table.Board.Put(TriggeredAbilityBoard.Creature("ox"), zone: Zone.Stack));

        var second = table.Spend(TheKey + "#1", spell);
        var other = table.Spend(TheKey + "ish#0", spell);

        Assert.Single(second.Events.OfType<TriggerFired>());
        Assert.Empty(other.Events.OfType<TriggerFired>());
    }

    /// <summary>
    /// The log is the game, for a stack object nothing printed: the events of the firing and the
    /// draw serialise, read back and replay to the state the game reached.
    /// </summary>
    [Fact]
    [RuleRef("603.7")]
    public void TheLogOfAFiredDelayedAbilityRoundTripsAndReplays()
    {
        var table = new DelayedManaTable();
        var spell = table.Board.Id(table.Board.Put(TriggeredAbilityBoard.Creature("ox"), zone: Zone.Stack));
        var settled = table.Spend(TheKey + "#0", spell);
        var played = table.EmptyTheStack(settled.State);

        var log = table.Log;
        var replayed = GameReducer.Replay(EventLogSerializer.Read(EventLogSerializer.Write(log)));

        Assert.Equal(played.Objects.Count, replayed.Objects.Count);
        Assert.Equal(
            played.In(ZoneKey.Of(Zone.Hand, DelayedManaTable.Controller)),
            replayed.In(ZoneKey.Of(Zone.Hand, DelayedManaTable.Controller)));
        Assert.Equal(played.DelayedTriggers.Count, replayed.DelayedTriggers.Count);
    }

    private const string TheKey = "k";

    private static ObjectFilter AnySpell { get; } = new(In: Zone.Stack);

    /// <summary>
    /// A board with the two halves of CR 106.6a already apart: a land that produced keyed mana, the
    /// delayed ability that watches it, and a library to draw from. The mana-producing half is the
    /// interpreter's and is not under test here, so the ability is folded in as the state the
    /// interpreter leaves behind and the spend is the event the casting system emits.
    /// </summary>
    private sealed class DelayedManaTable
    {
        public static PlayerId Controller { get; } = new(0);

        public DelayedManaTable(string? key = null)
        {
            Board = new TriggeredAbilityBoard();
            Board.Fold(new TurnBegan(1, Controller, Extra: false), new StepBegan(Step.PrecombatMain));
            Land = Board.Id(Board.Put(TriggeredAbilityBoard.Definition("grove", CardType.Land)));
            Board.Put(TriggeredAbilityBoard.Creature("stag"), zone: Zone.Library);
            Board.Fold(new DelayedTriggerCreated(new DelayedTriggerState(
                Board.State.NextTimestamp,
                new ManaSpent(key ?? TheKey + "#0", AnySpell),
                new Draw(new Fixed(1)),
                Land,
                Controller,
                Once: true,
                ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty)));
            LibraryBefore = Board.State.In(ZoneKey.Of(Zone.Library, Controller)).Count;

            // The board's own list, not a copy: whatever a test puts on the board afterwards is part
            // of the same log, and the engine's events are appended to it as they happen.
            Log = Board.Recorded;
        }

        public TriggeredAbilityBoard Board { get; }

        public ObjectId Land { get; }

        public int LibraryBefore { get; }

        public List<GameEvent> Log { get; }

        private Game Table { get; } = new(Subsystems.Wave2(), new DelayedManaStore());

        /// <summary>
        /// The spend the casting system emits, CR 106.6: one negative provenance block per single
        /// mana, naming the spell it paid for. Settled through the front door, so what fires is what
        /// would fire in a game.
        /// </summary>
        public Outcome Spend(string? key, ObjectId spell, GameState? from = null)
        {
            var delta = ManaPool.Empty with
            {
                Provenance = [new ManaProvenance(Color.Green, -1, Land, key)],
            };
            var spent = new ManaChanged(Controller, delta, "spent", spell);
            var state = from ?? Board.State;
            var settled = Table.Settle(state, [spent]);
            if (from is null)
            {
                Log.Add(spent);
                Log.AddRange(settled.Events);
            }

            return new Outcome(settled.State, settled.Events);
        }

        /// <summary>
        /// Both players pass, so the top of the stack resolves, CR 117.4 and 608.2 — the front door
        /// for the resolution as <see cref="Game.Settle"/> is for the firing.
        /// </summary>
        public GameState EmptyTheStack(GameState state)
        {
            var granted = new PriorityGranted(Controller);
            Log.Add(granted);
            state = granted.Apply(state);
            foreach (var seat in new[] { 0, 1 })
            {
                var applied = Assert.IsType<Applied>(Table.Apply(state, new PassPriority(new PlayerId(seat))));
                Log.AddRange(applied.Events);
                state = applied.State;
            }

            return state;
        }

        /// <summary>Nothing is created from a name here: every definition is put on the board by hand.</summary>
        private sealed class DelayedManaStore : IDefinitionStore
        {
            public CardDefinition? ByName(string name) => null;

            public CardDefinition? ByOracleId(string oracleId) => null;
        }
    }
}
