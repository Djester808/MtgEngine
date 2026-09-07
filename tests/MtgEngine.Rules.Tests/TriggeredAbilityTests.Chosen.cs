using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Mana;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Triggers;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// What a firing carries besides its objects: the colour the permanent was tapped for (CR 106.12a),
/// the values its own card chose before the trigger existed (CR 608.2d), the spell a spend paid for
/// (CR 106.6a), and the single reading of a player filter (CR 702.5b).
/// </summary>
public sealed partial class TriggeredAbilityTests
{
    /// <summary>
    /// CR 106.12a with 608.2d: the other half of "whenever a land you control is tapped for mana,
    /// add an additional one mana of that color" is an <c>AddMana</c> reading the produced colour
    /// back out of the resolution's context. The colour is on the mana block and not in any context,
    /// so the firing has to carry it onto the stack object for the resolution to find it.
    /// </summary>
    [Fact]
    [RuleRef("106.12a")]
    public void TheColourAPermanentWasTappedForRidesOntoTheStackObject()
    {
        var board = new TriggeredAbilityBoard();
        board.Put(TriggeredAbilityBoard.Definition(
            "watcher",
            CardType.Enchantment,
            [Ability(new TappedForMana(LandYouControl))]));
        var land = board.Put(TriggeredAbilityBoard.Definition("bog", CardType.Land));

        var outcome = board.Collect(TappedFor(board.Id(land), Color.Green));

        var onStack = outcome.State.Objects[Assert.Single(outcome.State.In(ZoneKey.Shared(Zone.Stack)))];
        var entry = onStack.Stack!;
        Assert.NotNull(entry.Chosen);
        Assert.Equal(nameof(Color.Green), entry.Chosen[TriggerSystem.TappedForManaColor]);

        // What the interpreter does with it: the resolution's context is seeded from the entry, and
        // ManaProduction's chosen-colour key reads a colour back out of it.
        var resolving = ContextFrom(entry);
        Assert.Equal(Color.Green, ManaSymbols.ColorFromChoice(resolving.Chosen[TriggerSystem.TappedForManaColor]));
    }

    /// <summary>
    /// CR 608.2d: a colour chosen while an ability resolved is kept on the object, so an ability of
    /// that object triggering later — outside any resolution — reads it. Before the choice the card
    /// has no chosen colour and nothing is tapped for it, which is not the same as every colour.
    /// </summary>
    [Fact]
    [RuleRef("608.2d")]
    public void ATriggerReadsTheColourItsOwnCardChoseBeforeTheTriggerExisted()
    {
        var board = new TriggeredAbilityBoard();
        var watcher = board.Put(TriggeredAbilityBoard.Definition(
            "watcher",
            CardType.Enchantment,
            [Ability(new TappedForMana(LandYouControl, OfChosenColorKey: "color"))]));
        var land = board.Put(TriggeredAbilityBoard.Definition("bog", CardType.Land));

        var beforeChoosing = board.Collect(TappedFor(board.Id(land), Color.Green));
        board.Fold(new ValueChosen(board.Id(watcher), "color", nameof(Color.Green)));
        var green = board.Collect(TappedFor(board.Id(land), Color.Green));
        var red = board.Collect(TappedFor(board.Id(land), Color.Red));

        Assert.Empty(beforeChoosing.Events.OfType<TriggerFired>());
        Assert.Single(green.Events.OfType<TriggerFired>());
        Assert.Empty(red.Events.OfType<TriggerFired>());
    }

    /// <summary>
    /// CR 106.6a: "when that mana is spent to cast a creature spell". The spend event names the
    /// object the payment was for, so the filter is asked about that object; a spend that names
    /// nothing is not a spend on a spell the filter admits and fires nothing.
    /// </summary>
    [Fact]
    [RuleRef("106.6")]
    public void ManaSpentOnASpellFiresForTheSpellTheFilterAdmitsAndForNoOther()
    {
        var board = new TriggeredAbilityBoard();
        board.Fold(new TurnBegan(1, new PlayerId(0), false));
        var land = board.Put(TriggeredAbilityBoard.Definition("bog", CardType.Land));
        var instant = board.Put(TriggeredAbilityBoard.Definition("bolt", CardType.Instant), zone: Zone.Stack);
        var creature = board.Put(TriggeredAbilityBoard.Creature("beast"), zone: Zone.Stack);
        board.Fold(new DelayedForTest(new DelayedTriggerState(
            1,
            new ManaSpent("ritual", new ObjectFilter(In: Zone.Stack, Types: CardType.Instant)),
            Gain,
            null,
            new PlayerId(0),
            Once: true,
            ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty)));

        var spend = Spend(board.Id(land), Color.Green, "ritual");
        var onInstant = board.Collect(spend with { SpentOn = board.Id(instant) });
        var onCreature = board.Collect(spend with { SpentOn = board.Id(creature) });
        var onNothing = board.Collect(spend);

        Assert.Single(onInstant.Events.OfType<TriggerFired>());
        Assert.Empty(onCreature.Events.OfType<TriggerFired>());
        Assert.Empty(onNothing.Events.OfType<TriggerFired>());
    }

    /// <summary>
    /// CR 702.5b: the trigger system no longer has its own reading of a <see cref="PlayerFilter"/>.
    /// It asks the query, which is the same answer the state-based actions get — the divergence
    /// <c>seam-requests/E5d.md</c> reported. The spy proves the ask, both ways round.
    /// </summary>
    [Fact]
    [RuleRef("702.5b")]
    public void TheDamagedPlayerFilterIsAnsweredByTheQueryAndNotByTheTriggerSystem()
    {
        var board = new TriggeredAbilityBoard();
        var archer = board.Put(TriggeredAbilityBoard.Creature("archer"));
        var trigger = new DealsDamage(ObjectFilter.Creature, ToPlayer: new PlayerFilter(Opponent: true));
        var damage = new DamageDealt(board.Id(archer), null, new PlayerId(1), 2, false, false);

        var admitted = MatchWithSpy(board, trigger, damage, answers: true);
        var refused = MatchWithSpy(board, trigger, damage, answers: false);

        Assert.Equal(board.Id(archer), Assert.Single(Assert.Single(admitted.Subjects).Objects));
        Assert.Empty(refused.Subjects);
        Assert.Equal(new PlayerId(1), Assert.Single(admitted.Asked));
        Assert.Equal(new PlayerId(1), Assert.Single(refused.Asked));
    }

    /// <summary>The context the interpreter builds from a stack object, as far as this test needs it.</summary>
    private static EffectContext ContextFrom(StackEntry entry) => new(
        entry.Source,
        new PlayerId(0),
        [],
        [],
        null,
        entry.Remembered ?? ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty,
        entry.Chosen ?? ImmutableDictionary<string, string>.Empty,
        ImmutableDictionary<string, int>.Empty,
        entry.Triggering ?? [],
        null);

    private static (ImmutableList<TriggerSubject> Subjects, IReadOnlyList<PlayerId> Asked) MatchWithSpy(
        TriggeredAbilityBoard board,
        Trigger trigger,
        GameEvent happened,
        bool answers)
    {
        var layers = TriggeredAbilityBoard.Layers;
        var spy = new TriggeredAbilityQuerySpy(layers, answers);
        var matching = new TriggerMatching(spy, layers);
        var context = new EffectContext(
            null,
            new PlayerId(0),
            [],
            [],
            null,
            ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty,
            ImmutableDictionary<string, string>.Empty,
            ImmutableDictionary<string, int>.Empty,
            [],
            null);
        return (matching.Match(trigger, happened, context, new TriggerWindow(board.State, [happened], 0)), spy.Asked);
    }

    /// <summary>
    /// A query that answers <c>PlayerMatches</c> itself and hands every other question to the real
    /// one, so a test can see which component decided. The real query's own answer is E0e's.
    /// </summary>
    private sealed class TriggeredAbilityQuerySpy(ICharacteristics layers, bool answers) : IQuery
    {
        private readonly IQuery _real = new Query.Query(layers);
        private readonly bool _answers = answers;
        private readonly List<PlayerId> _asked = [];

        public string Name => "query spy";

        public IReadOnlyList<PlayerId> Asked => _asked;

        public bool Matches(GameState state, ObjectFilter filter, ObjectId candidate, EffectContext context) =>
            _real.Matches(state, filter, candidate, context);

        public bool Holds(GameState state, Condition condition, EffectContext context) =>
            _real.Holds(state, condition, context);

        public int Evaluate(GameState state, Amount amount, EffectContext context) =>
            _real.Evaluate(state, amount, context);

        public ImmutableList<ObjectId> Resolve(GameState state, Selector selector, EffectContext context) =>
            _real.Resolve(state, selector, context);

        public ImmutableList<PlayerId> Players(GameState state, PlayerRef player, EffectContext context) =>
            _real.Players(state, player, context);

        public bool PlayerMatches(GameState state, PlayerFilter filter, PlayerId candidate, EffectContext context)
        {
            _asked.Add(candidate);
            return _answers;
        }
    }
}
