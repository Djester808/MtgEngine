using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Faces;
using MtgEngine.Rules.Layers;
using MtgEngine.Rules.Sba;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Views;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Tests;

[Trait("Scope", "TokenCopy")]
public sealed class TokenCopyContractTests
{
    private static readonly PlayerId Owner = new(0);
    private static readonly PlayerId Other = new(1);
    private static readonly ZoneSystem Zones = new();
    private static readonly CardDefinition Definition = new("card", "Contract object", Layout.Normal,
        [new Face("Contract object", FaceRole.Main, new TypeLine(CardType.Artifact), OracleText: "Descriptive text.")]);

    [Fact]
    public void A_token_has_object_identity_and_public_characteristics_without_a_deck_selection()
    {
        var start = Start();
        var made = Zones.CreateToken(start.State, new TokenSpec("Contract token", Color.Blue, CardType.Artifact), Other, true, false);
        var token = Created(made);
        Assert.True(token.IsToken);
        Assert.True(token.Tapped);
        Assert.Equal(Other, token.Owner);
        Assert.Equal(Other, token.InitialController);
        Assert.Equal(ZoneKey.Shared(Zone.Battlefield), token.Zone);
        Assert.Null(token.CardInstanceId);
        Assert.Null(token.DeckSelection);
        var restored = Replay(start.Events.AddRange(made.Events));
        Assert.True(restored.Objects[token.Id].IsToken);
        foreach (var viewer in restored.Players)
        {
            var visible = Assert.Single(new ViewProjector(new LayerSystem(), _ => true).Project(restored, viewer.Id).Battlefield);
            Assert.True(visible.IsToken);
            Assert.Equal("Contract token", visible.Name);
            Assert.Null(visible.PrintingId);
            Assert.Null(visible.Finish);
        }
    }

    [Fact]
    public void A_copy_token_takes_characteristics_but_not_the_sources_identity_status_or_ownership()
    {
        var start = Start();
        var source = start.State.Objects[new ObjectId(1)] with
        {
            Tapped = true,
            Damage = 4,
            Counters = ImmutableDictionary<string, int>.Empty.Add("counter", 2),
            IsCommander = true,
        };
        var made = Zones.CreateToken(start.State, CopyRules.CopyOf(source), Other, false, false);
        var copy = Created(made);
        Assert.True(copy.IsToken);
        Assert.Equal(source.Definition, copy.Definition);
        Assert.Equal(source.FaceIndex, copy.FaceIndex);
        Assert.NotEqual(source.Id, copy.Id);
        Assert.Equal(Other, copy.Owner);
        Assert.Equal(Other, copy.InitialController);
        Assert.Null(copy.CardInstanceId);
        Assert.Null(copy.DeckSelection);
        Assert.False(copy.IsCommander);
        Assert.False(copy.Tapped);
        Assert.Equal(0, copy.Damage);
        Assert.Empty(copy.Counters);
        Assert.Null(copy.Stack);
        Assert.Null(Replay(start.Events.AddRange(made.Events)).Objects[copy.Id].CardInstanceId);
    }

    [Fact]
    public void Becoming_a_copy_preserves_the_real_cards_identity_and_status()
    {
        var target = Start().State.Objects[new ObjectId(1)] with
        {
            InitialController = Other,
            Tapped = true,
            Damage = 2,
            IsCommander = true,
            Counters = ImmutableDictionary<string, int>.Empty.Add("counter", 3),
        };
        var definition = Definition with { OracleId = "other", Name = "Other characteristics" };
        var changed = CopyRules.Become(target, CopyRules.CopyOf(definition, 0));
        Assert.Equal(definition, changed.Definition);
        Assert.Equal(target.Id, changed.Id);
        Assert.Equal(target.CardInstanceId, changed.CardInstanceId);
        Assert.Equal(target.DeckSelection, changed.DeckSelection);
        Assert.Equal(target.Owner, changed.Owner);
        Assert.Equal(target.InitialController, changed.InitialController);
        Assert.Equal(target.Counters, changed.Counters);
        Assert.Equal(target.Damage, changed.Damage);
        Assert.True(changed.IsCommander);
        Assert.True(changed.Tapped);
        Assert.False(changed.IsToken);
        Assert.DoesNotContain(ZoneSystem.CopyOfCard, changed.Designations);
    }

    [Fact]
    public void A_card_copy_is_a_separate_ephemeral_object_in_its_owners_zone()
    {
        var start = Start();
        var made = Zones.CopyInPlace(start.State, new ObjectId(1), Other);
        var copy = Created(made);
        Assert.False(copy.IsToken);
        Assert.Null(copy.Stack);
        Assert.Contains(ZoneSystem.CopyOfCard, copy.Designations);
        Assert.Equal(ZoneKey.Of(Zone.Library, Other), copy.Zone);
        Assert.Equal(Other, copy.Owner);
        Assert.Null(copy.CardInstanceId);
        Assert.Null(copy.DeckSelection);
        Assert.Equal(start.State.Objects[new ObjectId(1)], made.State.Objects[new ObjectId(1)]);
        var restored = Replay(start.Events.AddRange(made.Events));
        var ceased = Check(restored);
        Assert.DoesNotContain(copy.Id, ceased.State.Objects.Keys);
        Assert.Contains(new ObjectId(1), ceased.State.Objects.Keys);
        Assert.Single(ceased.Events.OfType<ObjectCeasedToExist>());
    }

    [Theory]
    [InlineData(Zone.Graveyard)]
    [InlineData(Zone.Exile)]
    [InlineData(Zone.Hand)]
    public void Moving_a_card_copy_preserves_its_ephemeral_identity_until_state_based_cessation(Zone destination)
    {
        var start = Start();
        var made = Zones.CopyInPlace(start.State, new ObjectId(1), Owner);
        var copy = Created(made);
        var moved = Zones.Move(made.State, copy.Id, ZoneKey.Of(destination, Owner));
        var arrived = Assert.Single(moved.Events.OfType<ObjectMoved>()).To;
        var restored = Replay(start.Events.AddRange(made.Events).AddRange(moved.Events));
        var ceased = Check(restored);
        Assert.DoesNotContain(arrived.Id, ceased.State.Objects.Keys);
        Assert.Contains(new ObjectId(1), ceased.State.Objects.Keys);
        Assert.Single(ceased.Events.OfType<ObjectCeasedToExist>());
        Assert.Null(arrived.CardInstanceId);
        Assert.Null(arrived.DeckSelection);
        Assert.Contains(ZoneSystem.CopyOfCard, arrived.Designations);
        var final = Replay(start.Events.AddRange(made.Events).AddRange(moved.Events).AddRange(ceased.Events));
        Assert.DoesNotContain(arrived.Id, final.Objects.Keys);
    }

    [Fact]
    public void A_token_leaves_then_ceases_through_the_existing_event_services()
    {
        var start = Start();
        var made = Zones.CreateToken(start.State, new TokenSpec("Contract token", Color.Colorless, CardType.Artifact), Owner, false, false);
        var token = Created(made);
        Assert.Empty(Check(made.State).Events);
        var moved = Zones.Move(made.State, token.Id, ZoneKey.Of(Zone.Graveyard, Owner));
        var arrived = Assert.Single(moved.Events.OfType<ObjectMoved>()).To;
        Assert.True(arrived.IsToken);
        Assert.NotEqual(token.Id, arrived.Id);
        Assert.Null(arrived.CardInstanceId);
        var ceased = Check(moved.State);
        Assert.Single(ceased.Events.OfType<ObjectCeasedToExist>());
        var restored = Replay(start.Events.AddRange(made.Events).AddRange(moved.Events).AddRange(ceased.Events));
        Assert.DoesNotContain(arrived.Id, restored.Objects.Keys);
        Assert.Empty(restored.In(ZoneKey.Of(Zone.Graveyard, Owner)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void Stack_copies_keep_choices_and_ability_identity_without_becoming_physical_cards(int? abilityIndex)
    {
        var table = new InterpreterTable();
        var entry = new StackEntry(new ObjectId(99), abilityIndex, [], [Other], [0], 3, false);
        var original = table.OnStack(Owner, new Draw(new Fixed(0)), entry);
        var before = table.State;
        var events = table.Run(new CopySpell(new Remembered("original")), table.Context() with
        {
            Remembered = ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty.Add("original", [original]),
        });
        var copy = Assert.Single(events.OfType<ObjectCreated>()).Object;
        Assert.NotEqual(original, copy.Id);
        Assert.False(copy.IsToken);
        Assert.Null(copy.CardInstanceId);
        Assert.Null(copy.DeckSelection);
        Assert.True(copy.Stack!.IsCopy);
        Assert.Equal(abilityIndex, copy.Stack.AbilityIndex);
        Assert.Equal(entry.Source, copy.Stack.Source);
        Assert.Equal(entry.TargetPlayers, copy.Stack.TargetPlayers);
        Assert.Equal(entry.Modes, copy.Stack.Modes);
        Assert.Equal(entry.X, copy.Stack.X);
        var restored = GameReducer.Apply(before, EventLogSerializer.Read(EventLogSerializer.Write(events)));
        Assert.True(restored.Objects[copy.Id].Stack!.IsCopy);
        Assert.Equal(abilityIndex, restored.Objects[copy.Id].Stack!.AbilityIndex);
    }

    private static GameObject Created(Outcome outcome) => Assert.Single(outcome.Events.OfType<ObjectCreated>()).Object;
    private static GameState Replay(ImmutableList<GameEvent> events) => GameReducer.Replay(EventLogSerializer.Read(EventLogSerializer.Write(events)));
    private static Outcome Check(GameState state)
    {
        var layers = new LayerSystem();
        return new StateBasedActions(Zones, layers, new Query.Query(layers)).Check(state);
    }

    private static Outcome Start() => Zones.Create(new GameSetup(
        [new PlayerStart("A", [new StartingCard("card", "selected-printing", CardFinish.Etched)], []),
         new PlayerStart("B", [], []), new PlayerStart("C", [], []), new PlayerStart("D", [], [])],
        1, OpeningHandSize: 0), new Store());

    private sealed class Store : IDefinitionStore
    {
        public CardDefinition? ByOracleId(string oracleId) => Definition;
        public CardDefinition? ByName(string name) => Definition;
    }
}
