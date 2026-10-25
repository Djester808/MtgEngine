using MtgEngine.Cards;
using MtgEngine.Rules.Stack;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Turn;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Tests;

public sealed class FoundationModelTests
{
    private static readonly PlayerId Owner = new(0);
    private static readonly PlayerId Other = new(1);
    private static readonly CardDefinition Definition = new("fixture", "Fixture", Layout.Normal,
        [new Face("Fixture", FaceRole.Main, new TypeLine(CardType.Creature), Power: new("*"), Toughness: new("2"))]);

    private static GameState State(int seats = 2, params GameObject[] objects) => new()
    {
        Players = [.. Enumerable.Range(0, seats).Select(i => PlayerState.Create(new PlayerId(i), "Player", 20))],
        Objects = objects.ToImmutableDictionary(o => o.Id),
        Zones = objects.GroupBy(o => o.Zone).ToImmutableDictionary(g => g.Key, g => g.Select(o => o.Id).ToImmutableList()),
        Turn = new TurnState(0, Owner, Step.Untap, 0),
    };

    private static GameObject Card(int id, Zone zone) => new(new ObjectId(id), Definition, 0, Owner, Owner,
        zone is Zone.Hand or Zone.Graveyard or Zone.Library ? ZoneKey.Of(zone, Owner) : ZoneKey.Shared(zone), id);

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Tables_accept_distinct_game_local_seats_with_duplicate_names(int seats) => State(seats).Validate();

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void Unsupported_table_sizes_are_rejected(int seats) => Assert.Throws<InvalidOperationException>(() => State(seats).Validate());

    [Fact]
    public void Inconsistent_seat_identity_is_rejected()
    {
        var state = State();
        state = state with { Players = state.Players.SetItem(0, PlayerState.Create(Other, "Player", 20)) };
        Assert.Throws<InvalidOperationException>(state.Validate);
    }

    [Fact]
    public void A_physical_card_retains_printing_and_identity_independent_of_object_identity()
    {
        var selected = new StartingCard("fixture", "printing", CardFinish.Etched);
        var card = GameObject.CreateCard(new ObjectId(1), Definition, selected, Owner, ZoneKey.Of(Zone.Library, Owner), 1);
        var laterSnapshot = card with { Id = new ObjectId(2), Zone = ZoneKey.Of(Zone.Hand, Owner) };
        Assert.Equal(card.CardInstanceId, laterSnapshot.CardInstanceId);
        Assert.Equal(selected, laterSnapshot.DeckSelection);
        Assert.NotEqual(card.Id, laterSnapshot.Id);
        Assert.Equal("*", laterSnapshot.Face.Power!.Text);
        Assert.Null(laterSnapshot.Face.Power.Number);
        Assert.Equal(Zone.Library, card.Zone.Zone);
    }

    [Fact]
    public void Card_creation_refuses_a_definition_for_another_selection() =>
        Assert.Throws<ArgumentException>(() => GameObject.CreateCard(new ObjectId(1), Definition,
            new StartingCard("different"), Owner, ZoneKey.Of(Zone.Library, Owner), 1));

    [Fact]
    public void Duplicate_or_missing_zone_membership_is_rejected()
    {
        var card = Card(1, Zone.Hand);
        var state = State(2, card);
        Assert.Throws<InvalidOperationException>(() => (state with { Zones = state.Zones.Clear() }).Validate());
        Assert.Throws<InvalidOperationException>(() => (state with
        {
            Zones = state.Zones.Add(ZoneKey.Shared(Zone.Exile), [card.Id]),
        }).Validate());
    }

    [Fact]
    public void Shared_and_personal_zone_keys_are_not_interchangeable()
    {
        var state = State();
        Assert.Throws<InvalidOperationException>(() => (state with
        {
            Zones = state.Zones.Add(ZoneKey.Shared(Zone.Hand), []),
        }).Validate());
        Assert.Throws<InvalidOperationException>(() => (state with
        {
            Zones = state.Zones.Add(ZoneKey.Of(Zone.Battlefield, Owner), []),
        }).Validate());
    }

    [Fact]
    public void Library_and_graveyard_are_top_first_but_stack_is_top_last()
    {
        var state = State(2, Card(1, Zone.Library), Card(2, Zone.Library), Card(3, Zone.Graveyard),
            Card(4, Zone.Graveyard), Card(5, Zone.Stack), Card(6, Zone.Stack));
        state.Validate();
        Assert.Equal(new ObjectId(1), new Library(state, Owner).Top!.Id);
        Assert.Equal(new ObjectId(3), new Graveyard(state, Owner).Top!.Id);
        Assert.Equal(new ObjectId(6), new StackZoneSnapshot(state).Top!.Id);
        Assert.Null(new StackZoneSnapshot(state).Lookup(new ObjectId(1)));
    }

    [Fact]
    public void Hand_exile_and_command_keep_membership_without_inventing_commanders()
    {
        var state = State(2, Card(1, Zone.Hand), Card(2, Zone.Exile), Card(3, Zone.Command));
        Assert.Equal(new ObjectId(1), Assert.Single(new Hand(state, Owner).Members));
        Assert.Empty(new Hand(state, Other).Members);
        Assert.Equal(Owner, new ExileZone(state).Get(new ObjectId(2))!.Owner);
        Assert.False(new CommandZone(state).Get(new ObjectId(3))!.IsCommander);
    }

    [Fact]
    public void Battlefield_distinguishes_ownership_control_and_phasing()
    {
        var card = Card(1, Zone.Battlefield) with { Controller = Other, PhasedOut = true };
        var field = new Battlefield(State(2, card));
        Assert.True(card.IsPermanent);
        Assert.Equal(card.Id, Assert.Single(field.OwnedBy(Owner)));
        Assert.Equal(card.Id, Assert.Single(field.ControlledBy(Other)));
        Assert.Empty(field.ActiveMembers);
        Assert.False((card with { Zone = ZoneKey.Shared(Zone.Exile) }).IsPermanent);
    }

    [Fact]
    public void Tokens_and_copies_do_not_require_physical_card_identity()
    {
        var token = Card(1, Zone.Battlefield) with { IsToken = true };
        var copy = Card(2, Zone.Stack) with { IsCopy = true, Stack = new(null, null, [], [], [], null, true) };
        State(2, token, copy).Validate();
        Assert.Null(token.CardInstanceId);
        Assert.Null(copy.DeckSelection);
        Assert.True(copy.Stack.IsCopy);
    }

    [Fact]
    public void Priority_is_separate_from_turn_and_is_suspended_by_a_choice()
    {
        var state = State() with { Priority = new PriorityState(Other, []) };
        Assert.Equal(Owner, new TurnSnapshot(state).ActivePlayer);
        Assert.True(new PrioritySnapshot(state).CanPass(Other));
        state = state with { Pending = new(new ChoiceId(1), Owner, "selection", "Choose", [], 0, 0) };
        var snapshot = new PrioritySnapshot(state);
        Assert.Equal(Other, snapshot.Holder);
        Assert.Equal(Owner, snapshot.PendingChooser);
        Assert.False(snapshot.IsAvailable);
    }

    [Fact]
    public void Finished_games_do_not_offer_priority()
    {
        var state = State() with { Priority = new(Owner, []), Outcome = new([Owner], false, "Finished") };
        Assert.False(new PrioritySnapshot(state).IsAvailable);
    }

    [Fact]
    public void Historical_disclosure_has_no_live_object_or_card_identity()
    {
        var properties = typeof(RevealedCard).GetProperties().Select(p => p.PropertyType);
        Assert.DoesNotContain(typeof(ObjectId), properties);
        Assert.DoesNotContain(typeof(CardInstanceId), properties);
        Assert.Equal("fixture/printing", new RevealedCard("fixture", "Fixture", null, null, "printing").ArtworkKey);
    }

    [Fact]
    public void Foundation_allows_only_the_authorized_turn_coordinator_execution_scope()
    {
        var forbidden = new[] { "Engine", "Casting", "Combat", "Interpreter", "Keywords", "Replacement", "Sba", "Triggers", "Events", "Legal" };
        Assert.DoesNotContain(typeof(GameState).Assembly.GetTypes(), t =>
            forbidden.Any(name => (t.Namespace == "MtgEngine.Rules." + name
                || (t.Namespace?.StartsWith("MtgEngine.Rules." + name + ".", StringComparison.Ordinal) ?? false))
                && !(name == "Engine" && (t == typeof(Engine.TurnExecution)
                    || t == typeof(Engine.ExecutionBoundary) || t.DeclaringType == typeof(Engine.TurnExecution)))));
        Assert.DoesNotContain(typeof(CardDefinition).Assembly.GetTypes(), t => t.Name is "Effect" or "KeywordSemantics" or "Ability");
    }
}
