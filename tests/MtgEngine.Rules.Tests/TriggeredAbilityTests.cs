using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Triggers;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Triggered abilities, CR 603: what triggers, what does not, what order they go on the stack in,
/// and what a trigger that looks back is allowed to see.
/// </summary>
public sealed class TriggeredAbilityTests
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

    /// <summary>Puts a delayed trigger into the state; the effect that creates one is the interpreter's, CR 603.7a.</summary>
    private sealed record DelayedForTest(DelayedTriggerState Trigger) : GameEvent
    {
        public override GameState Apply(GameState state) =>
            state with { DelayedTriggers = state.DelayedTriggers.Add(Trigger) };
    }
}
