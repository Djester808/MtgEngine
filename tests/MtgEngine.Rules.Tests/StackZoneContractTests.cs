using System.Text.Json;
using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Layers;
using MtgEngine.Rules.Stack;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Views;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Tests;

[Trait("Scope", "Stack")]
public sealed class StackZoneContractTests
{
    private static readonly PlayerId Owner = new(0);
    private static readonly PlayerId Controller = new(1);
    private static readonly StackEntry Spell = new(null, null, [], [], [], null, false);

    [Fact]
    public void Empty_shared_stack_has_no_top_or_members()
    {
        var state = Start().State;
        var stack = StackZone.Capture(state);
        Assert.Null(stack.Key.Player);
        Assert.Equal(0, stack.Count);
        Assert.True(StackZone.IsEmpty(state));
        Assert.Null(StackZone.Top(state));
        Assert.Null(stack.Top);
        Assert.Null(stack.Lookup(new ObjectId(1)));
        Assert.False(stack.Contains(new ObjectId(1)));
        Assert.Empty(stack.Find(_ => true));
        Assert.Throws<ArgumentNullException>(() => StackZone.Capture(null!));
        Assert.Throws<ArgumentNullException>(() => stack.Find(null!));
    }

    [Fact]
    public void Spell_arrival_preserves_physical_identity_and_snapshot_is_immutable()
    {
        var initial = Start().State;
        var before = StackZone.Capture(initial);
        var card = initial.Objects[new ObjectId(1)];
        var move = StackZone.MoveSpellToStack(initial, card, Spell);
        var state = move.Apply(initial);
        var stack = StackZone.Capture(state);
        Assert.Empty(before.Order);
        Assert.Equal(1, stack.Count);
        Assert.Same(move.To, stack.Top);
        Assert.Equal(card.CardInstanceId, stack.Top!.CardInstanceId);
        Assert.Equal(card.DeckSelection, stack.Top.DeckSelection);
        Assert.NotEqual(card.Id, stack.Top.Id);
        Assert.Null(stack.Lookup(card.Id));
        Assert.Same(stack.Top, stack.Lookup(stack.Top.Id));
        Assert.False(StackZone.IsAbility(stack.Top));
    }

    [Fact]
    public void Ability_source_owner_and_controller_are_distinct_and_source_can_leave()
    {
        var initial = Start().State;
        var source = initial.Objects[new ObjectId(1)];
        var created = StackZone.CreateAbilityOnStack(initial, source, Controller,
            Spell with { Source = source.Id, AbilityIndex = 0 });
        var state = new ObjectCeasedToExist(source.Id).Apply(created.Apply(initial));
        var ability = Assert.IsType<GameObject>(StackZone.Capture(state).Top);
        Assert.Equal(Owner, ability.Owner);
        Assert.Equal(Controller, ability.InitialController);
        Assert.Equal(source.Id, ability.Stack!.Source);
        Assert.True(StackZone.IsAbility(ability));
        Assert.Null(ability.CardInstanceId);
        Assert.Null(ability.DeckSelection);
        Assert.False(state.Objects.ContainsKey(source.Id));
    }

    [Fact]
    public void Mixed_stack_replays_in_bottom_first_order_and_removes_in_lifo_order()
    {
        var start = Start();
        var move = StackZone.MoveSpellToStack(start.State, start.State.Objects[new ObjectId(1)], Spell);
        var state = move.Apply(start.State);
        var ability = StackZone.CreateAbilityOnStack(state, state.Objects[new ObjectId(2)], Controller,
            Spell with { Source = new ObjectId(2), AbilityIndex = 0, Triggering = [] });
        state = ability.Apply(state);
        // Exercise the existing persisted stack-copy representation without implementing copy rules.
        var copy = new ObjectCreated(new GameObject(new ObjectId(state.NextObjectId), move.To.Definition,
            0, Controller, Controller, StackZone.Key, state.NextTimestamp)
        { Stack = Spell with { IsCopy = true } }, -1);
        state = copy.Apply(state);
        var restored = GameReducer.Replay(EventLogSerializer.Read(EventLogSerializer.Write(
            start.Events.Add(move).Add(ability).Add(copy))));
        var stack = StackZone.Capture(restored);
        Assert.Equal(new[] { move.To.Id, ability.Object.Id, copy.Object.Id }, stack.Order);
        Assert.Equal(stack.Order, StackZone.Capture(state).Order);
        Assert.Equal(copy.Object.Id, StackZone.Top(restored));
        Assert.Equal(copy.Object.Id, Assert.Single(stack.Find(obj => obj.Stack!.IsCopy)));
        Assert.Null(stack.Top!.CardInstanceId);
        Assert.Null(stack.Top.DeckSelection);
        Assert.Equal(new ObjectId(2), restored.Objects[ability.Object.Id].Stack!.Source);
        foreach (var expected in stack.Order.Reverse())
        {
            Assert.Equal(expected, StackZone.Top(restored));
            restored = new ObjectCeasedToExist(expected).Apply(restored);
        }
        Assert.True(StackZone.IsEmpty(restored));
        Assert.Equal(3, stack.Count);
    }

    [Fact]
    public void Non_top_departure_preserves_survivor_order_and_card_selection()
    {
        var start = Start();
        var first = StackZone.MoveSpellToStack(start.State, start.State.Objects[new ObjectId(1)], Spell);
        var state = first.Apply(start.State);
        var second = StackZone.MoveSpellToStack(state, state.Objects[new ObjectId(2)], Spell);
        state = second.Apply(state);
        var moved = new ZoneSystem().Move(state, first.To.Id, ZoneKey.Of(Zone.Graveyard, Owner));
        Assert.Equal(second.To.Id, Assert.Single(StackZone.Capture(moved.State).Order));
        Assert.Null(StackZone.Capture(moved.State).Lookup(first.To.Id));
        var arrived = Assert.Single(moved.State.Objects.Values, obj => obj.Zone.Zone == Zone.Graveyard);
        Assert.Equal(first.To.DeckSelection, arrived.DeckSelection);
        Assert.Equal(first.To.CardInstanceId, arrived.CardInstanceId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Projection_preserves_public_order_and_hides_face_down_selection(int seat)
    {
        var initial = Start().State;
        var first = StackZone.MoveSpellToStack(initial, initial.Objects[new ObjectId(1)], Spell);
        var state = first.Apply(initial);
        var second = StackZone.MoveSpellToStack(state, state.Objects[new ObjectId(2)] with { FaceDown = true }, Spell);
        state = second.Apply(state);
        var view = new ViewProjector(new LayerSystem(), _ => true).Project(state, new PlayerId(seat));
        Assert.Equal(StackZone.Capture(state).Order, view.Stack.Select(obj => obj.Id));
        Assert.Equal("first-printing", view.Stack[0].PrintingId);
        Assert.Equal(CardFinish.Foil, view.Stack[0].Finish);
        Assert.Equal("Spell", view.Stack[1].StackKind);
        Assert.True(view.Stack[1].FaceDown);
        Assert.Empty(view.Stack[1].OracleId);
        Assert.Null(view.Stack[1].PrintingId);
        Assert.Null(view.Stack[1].Finish);
        Assert.DoesNotContain("second-printing", JsonSerializer.Serialize(view));
        Assert.DoesNotContain("CardInstanceId", JsonSerializer.Serialize(view));
    }

    private static Outcome Start() => new ZoneSystem().Create(new GameSetup(
        [new PlayerStart("A", [new StartingCard("card", "first-printing", CardFinish.Foil),
            new StartingCard("card", "second-printing", CardFinish.Etched)], []),
         new PlayerStart("B", [], []), new PlayerStart("C", [], []), new PlayerStart("D", [], [])],
        1, OpeningHandSize: 0), new Store());

    private sealed class Store : IDefinitionStore
    {
        private static readonly CardDefinition Definition = new("card", "Card", Layout.Normal,
            [new Face("Card", FaceRole.Main, new TypeLine(CardType.Instant), OracleText: "Printed text.")]);
        public CardDefinition? ByOracleId(string oracleId) => Definition;
        public CardDefinition? ByName(string name) => Definition;
    }
}
