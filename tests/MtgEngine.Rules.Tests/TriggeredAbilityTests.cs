using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Mana;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Triggers;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Triggered abilities, CR 603: what triggers, what does not, what order they go on the stack in,
/// and what a trigger that looks back is allowed to see.
/// </summary>
public sealed partial class TriggeredAbilityTests
{
    private static readonly Effect Gain = new GainLife(new Fixed(1));

    private static TriggeredAbility Ability(Trigger when, Condition? intervening = null) =>
        new(when, Gain, intervening);

    [Fact]
    [RuleRef("603.6a")]
    public void EntersTriggerFiresOnceForTheCreatureThatEntered()
    {
        var board = new TriggeredAbilityBoard();
        board.Put(TriggeredAbilityBoard.Definition(
            "watcher",
            CardType.Enchantment,
            [Ability(new EntersBattlefield(ObjectFilter.Creature))]));

        var entered = board.Put(TriggeredAbilityBoard.Creature("beast"));
        var outcome = board.Collect(entered);

        var fired = Assert.Single(outcome.Events.OfType<TriggerFired>());
        Assert.Equal(entered.Object.Id, Assert.Single(fired.Subjects));
        Assert.Equal(0, fired.AbilityIndex);
    }

    [Fact]
    [RuleRef("603.2")]
    public void EntersTriggerDoesNotFireForAnObjectTheFilterDoesNotDescribe()
    {
        var board = new TriggeredAbilityBoard();
        board.Put(TriggeredAbilityBoard.Definition(
            "watcher",
            CardType.Enchantment,
            [Ability(new EntersBattlefield(ObjectFilter.Creature))]));

        var land = board.Put(TriggeredAbilityBoard.Definition("waste", CardType.Land));
        var outcome = board.Collect(land);

        Assert.Empty(outcome.Events);
        Assert.Equal(board.State, outcome.State);
    }

    [Fact]
    [RuleRef("603.3")]
    public void ATriggeredAbilityGoesOnTheStackAsAnObjectNamingItsSource()
    {
        var board = new TriggeredAbilityBoard();
        var watcher = board.Put(TriggeredAbilityBoard.Definition(
            "watcher",
            CardType.Enchantment,
            [Ability(new EntersBattlefield(ObjectFilter.Creature))]));

        var outcome = board.Collect(board.Put(TriggeredAbilityBoard.Creature("beast")));

        var stack = outcome.State.In(ZoneKey.Shared(Zone.Stack));
        var onStack = outcome.State.Objects[Assert.Single(stack)];
        Assert.Equal(watcher.Object.Id, onStack.Stack!.Source);
        Assert.Equal(0, onStack.Stack.AbilityIndex);
    }

    [Fact]
    [RuleRef("603.10")]
    public void ALeavesTheBattlefieldTriggerSeesTheObjectThatLeft()
    {
        var board = new TriggeredAbilityBoard();
        board.Put(TriggeredAbilityBoard.Definition(
            "mourner",
            CardType.Enchantment,
            [Ability(new LeavesBattlefield(ObjectFilter.Creature))]));
        var beast = board.Put(TriggeredAbilityBoard.Creature("beast")).Object.Id;

        var outcome = board.Collect(board.Kill(beast));

        var fired = Assert.Single(outcome.Events.OfType<TriggerFired>());
        Assert.Equal(beast, Assert.Single(fired.Subjects));

        // The look-back is for the match only: the object is still gone afterwards, CR 400.7.
        Assert.False(outcome.State.Objects.ContainsKey(beast));
    }

    [Fact]
    [RuleRef("603.10")]
    public void ALookBackSeesTheCountersThePermanentHadWhenItLeft()
    {
        var board = new TriggeredAbilityBoard();
        board.Put(TriggeredAbilityBoard.Definition(
            "mourner",
            CardType.Enchantment,
            [Ability(new LeavesBattlefield(new ObjectFilter(Types: CardType.Creature, WithCounter: "+1/+1")))]));
        var beast = board.Put(TriggeredAbilityBoard.Creature("beast"), counter: "+1/+1").Object.Id;

        // The card in the graveyard is a new object with no counters, CR 400.7; the permanent the
        // trigger looks back at had one.
        var died = board.Kill(beast);
        Assert.Empty(died.To.Counters);

        var fired = Assert.Single(board.Collect(died).Events.OfType<TriggerFired>());
        Assert.Equal(beast, Assert.Single(fired.Subjects));
    }

    [Fact]
    [RuleRef("603.10")]
    public void ALookBackSeesWhoControlledThePermanentThatLeft()
    {
        var board = new TriggeredAbilityBoard();
        board.Put(TriggeredAbilityBoard.Definition(
            "mourner",
            CardType.Enchantment,
            [Ability(new LeavesBattlefield(new ObjectFilter(Types: CardType.Creature, ControlledBy: new You())))]));

        // A creature seat 0 had taken control of: the card that goes to the graveyard is its
        // owner's (CR 108.4), so only the departed permanent says seat 0 controlled it.
        var stolen = board.Put(TriggeredAbilityBoard.Creature("beast"), controller: 0, owner: 1).Object.Id;
        var died = board.Kill(stolen);
        Assert.Equal(new PlayerId(1), died.To.InitialController);

        var fired = Assert.Single(board.Collect(died).Events.OfType<TriggerFired>());
        Assert.Equal(stolen, Assert.Single(fired.Subjects));
    }

    [Fact]
    [RuleRef("603.10")]
    public void AMoveThatCarriesNoDepartedObjectStillMatchesOnWhatTheNewObjectSays()
    {
        var board = new TriggeredAbilityBoard();
        board.Put(TriggeredAbilityBoard.Definition(
            "mourner",
            CardType.Enchantment,
            [Ability(new LeavesBattlefield(ObjectFilter.Creature))]));
        var beast = board.Put(TriggeredAbilityBoard.Creature("beast"), counter: "+1/+1").Object.Id;

        // The fallback: an event with no departed object is rebuilt from the card that arrived, so
        // the type still matches and the counter is not there to be read.
        var died = board.Kill(beast, carryDeparted: false);
        Assert.Null(died.Departed);

        var fired = Assert.Single(board.Collect(died).Events.OfType<TriggerFired>());
        Assert.Equal(beast, Assert.Single(fired.Subjects));
    }

    [Fact]
    [RuleRef("700.4")]
    public void ADiesTriggerOnAnAuraSeesTheCreatureItWasAttachedTo()
    {
        var board = new TriggeredAbilityBoard();
        var beast = board.Put(TriggeredAbilityBoard.Creature("beast")).Object.Id;
        var aura = board.Put(
            TriggeredAbilityBoard.Definition("shroud", CardType.Enchantment, [Ability(new Dies(ObjectFilter.Creature))]),
            attachedTo: beast);
        Assert.Equal(beast, board.State.Objects[aura.Object.Id].AttachedTo);

        var outcome = board.Collect(board.Kill(beast));

        var fired = Assert.Single(outcome.Events.OfType<TriggerFired>());
        Assert.Equal(aura.Object.Id, fired.Source);
        Assert.Equal(beast, Assert.Single(fired.Subjects));
    }

    [Fact]
    [RuleRef("603.2")]
    public void AnUpkeepTriggerFiresOnlyForThePlayerWhoseUpkeepItIs()
    {
        var board = new TriggeredAbilityBoard();
        var mine = board.Put(
            TriggeredAbilityBoard.Definition("mine", CardType.Enchantment, [Ability(new AtStep(Step.Upkeep, new You()))]),
            controller: 0);
        board.Put(
            TriggeredAbilityBoard.Definition("theirs", CardType.Enchantment, [Ability(new AtStep(Step.Upkeep, new You()))]),
            controller: 1);
        board.ActivePlayer(0);

        var outcome = board.Collect(new StepBegan(Step.Upkeep));

        var fired = Assert.Single(outcome.Events.OfType<TriggerFired>());
        Assert.Equal(mine.Object.Id, fired.Source);
        Assert.Equal(new PlayerId(0), fired.Controller);
    }

    [Fact]
    [RuleRef("603.2")]
    public void AStepTriggerDoesNotFireForADifferentStep()
    {
        var board = new TriggeredAbilityBoard();
        board.Put(TriggeredAbilityBoard.Definition("mine", CardType.Enchantment, [Ability(new AtStep(Step.Upkeep, new You()))]));
        board.ActivePlayer(0);

        Assert.Empty(board.Collect(new StepBegan(Step.End)).Events);
    }

    [Fact]
    [RuleRef("603.3b")]
    public void TriggersGoOnTheStackInApnapOrder()
    {
        var board = new TriggeredAbilityBoard();
        var first = board.Put(
            TriggeredAbilityBoard.Definition("theirs", CardType.Enchantment, [Ability(new AtStep(Step.Upkeep, new EachPlayer()))]),
            controller: 0);
        var second = board.Put(
            TriggeredAbilityBoard.Definition("mine", CardType.Enchantment, [Ability(new AtStep(Step.Upkeep, new EachPlayer()))]),
            controller: 1);
        board.ActivePlayer(1);

        var outcome = board.Collect(new StepBegan(Step.Upkeep));

        var fired = outcome.Events.OfType<TriggerFired>().ToList();
        Assert.Equal(2, fired.Count);
        Assert.Equal(second.Object.Id, fired[0].Source);
        Assert.Equal(first.Object.Id, fired[1].Source);

        // One trigger each, so neither player has an order to choose, CR 603.3b.
        Assert.Null(outcome.State.Pending);
    }

    [Fact]
    [RuleRef("603.3b")]
    public void APlayerWithMoreThanOneTriggerIsAskedForTheOrder()
    {
        var board = new TriggeredAbilityBoard();
        board.Put(TriggeredAbilityBoard.Definition("one", CardType.Enchantment, [Ability(new AtStep(Step.Upkeep, new You()))]));
        board.Put(TriggeredAbilityBoard.Definition("two", CardType.Enchantment, [Ability(new AtStep(Step.Upkeep, new You()))]));
        board.ActivePlayer(0);

        var outcome = board.Collect(new StepBegan(Step.Upkeep));

        Assert.Equal(2, outcome.Events.OfType<TriggerFired>().Count());
        var pending = Assert.IsType<PendingChoice>(outcome.State.Pending);
        Assert.Equal(TriggerSystem.OrderChoice, pending.Kind);
        Assert.Equal(new PlayerId(0), pending.Player);
        Assert.Equal(2, pending.Options.Count);
    }

    [Fact]
    [RuleRef("603.4")]
    public void AnInterveningIfThatIsFalsePutsNothingOnTheStack()
    {
        var board = new TriggeredAbilityBoard();
        board.Put(
            TriggeredAbilityBoard.Definition(
                "patient",
                CardType.Enchantment,
                [Ability(new EntersBattlefield(ObjectFilter.Creature), new IsYourTurn())]),
            controller: 1);
        board.ActivePlayer(0);

        var outcome = board.Collect(board.Put(TriggeredAbilityBoard.Creature("beast")));

        Assert.Empty(outcome.Events);
        Assert.Empty(outcome.State.In(ZoneKey.Shared(Zone.Stack)));
    }

    [Fact]
    [RuleRef("603.4")]
    public void AnInterveningIfThatIsTrueStillTriggers()
    {
        var board = new TriggeredAbilityBoard();
        board.Put(
            TriggeredAbilityBoard.Definition(
                "patient",
                CardType.Enchantment,
                [Ability(new EntersBattlefield(ObjectFilter.Creature), new IsYourTurn())]),
            controller: 0);
        board.ActivePlayer(0);

        var outcome = board.Collect(board.Put(TriggeredAbilityBoard.Creature("beast")));

        Assert.Single(outcome.Events.OfType<TriggerFired>());
    }

    [Fact]
    [RuleRef("603.6d")]
    public void AnAbilityDoesNotTriggerFromAZoneItDoesNotFunctionIn()
    {
        var board = new TriggeredAbilityBoard();
        board.Put(
            TriggeredAbilityBoard.Definition("watcher", CardType.Enchantment, [Ability(new EntersBattlefield(ObjectFilter.Creature))]),
            zone: Zone.Graveyard);

        Assert.Empty(board.Collect(board.Put(TriggeredAbilityBoard.Creature("beast"))).Events);
    }

    [Fact]
    [RuleRef("603.7")]
    public void ADelayedTriggerFiresOnceAndIsSpent()
    {
        var board = new TriggeredAbilityBoard();
        board.Fold(new TurnBegan(1, new PlayerId(0), false));
        board.Put(TriggeredAbilityBoard.Creature("beast"));
        var delayed = new DelayedTriggerState(
            1,
            new AtStep(Step.End, new You()),
            Gain,
            null,
            new PlayerId(0),
            Once: true,
            ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty);
        board.Fold(new DelayedForTest(delayed));

        var outcome = board.Collect(new StepBegan(Step.End));

        Assert.Single(outcome.Events.OfType<TriggerFired>());
        Assert.Empty(outcome.State.DelayedTriggers);
    }

    /// <summary>A land you control, which is what "whenever a land you control is tapped for mana" describes.</summary>
    private static ObjectFilter LandYouControl { get; } = new(Types: CardType.Land, ControlledBy: new You());

    /// <summary>
    /// CR 106.12a: a mana ability resolved and produced. The event is the <see cref="ManaChanged"/>
    /// whose <see cref="ManaProvenance"/> block names the permanent that produced it (CR 106.6) —
    /// the mana subsystem writes those blocks; a test builds one by hand.
    /// </summary>
    private static ManaChanged TappedFor(ObjectId source, Color color, string? key = null) =>
        new(
            new PlayerId(0),
            ManaSymbols.OfColor(color) with { Provenance = [new ManaProvenance(color, 1, source, key)] },
            "mana ability");

    /// <summary>The same block leaving the pool, CR 106.6a: a spend is a negative amount on the key.</summary>
    private static ManaChanged Spend(ObjectId source, Color color, string key)
    {
        var made = ManaSymbols.OfColor(color);
        var delta = new ManaPool(-made.White, -made.Blue, -made.Black, -made.Red, -made.Green, -made.Colorless)
        {
            Provenance = [new ManaProvenance(color, -1, source, key)],
        };
        return new ManaChanged(new PlayerId(0), delta, "paid");
    }

    [Fact]
    [RuleRef("106.12a")]
    public void ALandTappedForManaFiresAndACreaturesManaDoesNot()
    {
        var board = new TriggeredAbilityBoard();
        board.Put(TriggeredAbilityBoard.Definition(
            "watcher",
            CardType.Enchantment,
            [Ability(new TappedForMana(LandYouControl))]));
        var land = board.Put(TriggeredAbilityBoard.Definition("bog", CardType.Land));
        var elf = board.Put(TriggeredAbilityBoard.Creature("elf"));

        var fromLand = board.Collect(TappedFor(board.Id(land), Color.Green));
        var fromCreature = board.Collect(TappedFor(board.Id(elf), Color.Green));

        var fired = Assert.Single(fromLand.Events.OfType<TriggerFired>());
        Assert.Equal(board.Id(land), Assert.Single(fired.Subjects));
        Assert.Empty(fromCreature.Events.OfType<TriggerFired>());
    }

    [Fact]
    [RuleRef("106.12a")]
    public void BecomingTappedWithoutProducingManaFiresNothing()
    {
        var board = new TriggeredAbilityBoard();
        board.Put(TriggeredAbilityBoard.Definition(
            "watcher",
            CardType.Enchantment,
            [Ability(new TappedForMana(LandYouControl))]));
        var land = board.Put(TriggeredAbilityBoard.Definition("bog", CardType.Land));

        var tapped = board.Collect(new ObjectChanged(board.Id(land), Tapped: true));
        var nothingProduced = board.Collect(new ManaChanged(new PlayerId(0), ManaPool.Empty, "mana ability"));

        Assert.Empty(tapped.Events.OfType<TriggerFired>());
        Assert.Empty(nothingProduced.Events.OfType<TriggerFired>());
    }

    [Fact]
    [RuleRef("106.12a")]
    public void ATriggerRestrictedToTheChosenColourFiresForThatColourOnly()
    {
        var board = new TriggeredAbilityBoard();
        var land = board.Put(TriggeredAbilityBoard.Definition("bog", CardType.Land));
        var trigger = new TappedForMana(LandYouControl, OfChosenColorKey: "color");

        var green = MatchWithChosenColor(board, trigger, TappedFor(board.Id(land), Color.Green), "green");
        var red = MatchWithChosenColor(board, trigger, TappedFor(board.Id(land), Color.Red), "green");
        var unchosen = MatchWithChosenColor(board, trigger, TappedFor(board.Id(land), Color.Green), chosen: null);

        Assert.Equal(board.Id(land), Assert.Single(Assert.Single(green).Objects));
        Assert.Empty(red);
        Assert.Empty(unchosen);
    }

    [Fact]
    [RuleRef("106.6")]
    public void ManaSpentFiresOnTheSpendOfItsKeyedManaAndNotOnItsProduction()
    {
        var board = new TriggeredAbilityBoard();
        board.Fold(new TurnBegan(1, new PlayerId(0), false));
        var land = board.Put(TriggeredAbilityBoard.Definition("bog", CardType.Land));
        board.Fold(new DelayedForTest(new DelayedTriggerState(
            1,
            new ManaSpent("ritual"),
            Gain,
            null,
            new PlayerId(0),
            Once: true,
            ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty)));

        var produced = board.Collect(TappedFor(board.Id(land), Color.Green, key: "ritual"));
        var spentOther = board.Collect(Spend(board.Id(land), Color.Green, "other"));
        var spent = board.Collect(Spend(board.Id(land), Color.Green, "ritual"));

        Assert.Empty(produced.Events.OfType<TriggerFired>());
        Assert.Empty(spentOther.Events.OfType<TriggerFired>());
        Assert.Single(spent.Events.OfType<TriggerFired>());
    }

    /// <summary>
    /// The matcher on its own, in a context that carries a chosen colour. Nothing in the state
    /// stores the colour a card chose — <c>ChooseValue</c> writes it into the resolution's own
    /// <see cref="EffectContext"/> and it is gone when that resolution ends — so
    /// <see cref="TriggerSystem"/> builds the trigger's context with an empty <c>Chosen</c> and
    /// cannot answer <c>OfChosenColorKey</c> end to end. seam-requests/E6c.md asks for the store.
    /// The arm under test is the one the collector calls.
    /// </summary>
    private static ImmutableList<TriggerSubject> MatchWithChosenColor(TriggeredAbilityBoard board, Trigger trigger, GameEvent happened, string? chosen)
    {
        var layers = TriggeredAbilityBoard.Layers;
        var matching = new TriggerMatching(new Query.Query(layers), layers);
        var context = new EffectContext(
            null,
            new PlayerId(0),
            [],
            [],
            null,
            ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty,
            chosen is null ? ImmutableDictionary<string, string>.Empty : ImmutableDictionary<string, string>.Empty.SetItem("color", chosen),
            ImmutableDictionary<string, int>.Empty,
            [],
            null);
        return matching.Match(trigger, happened, context, new TriggerWindow(board.State, [happened], 0));
    }

    /// <summary>Puts a delayed trigger into the state; the effect that creates one is the interpreter's, CR 603.7a.</summary>
    private sealed record DelayedForTest(DelayedTriggerState Trigger) : GameEvent
    {
        public override GameState Apply(GameState state) =>
            state with { DelayedTriggers = state.DelayedTriggers.Add(Trigger) };
    }
}
