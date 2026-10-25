using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Library;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Tests;

[Trait("Scope", "Deck")]
public sealed class CardContractTests
{
    private static readonly PlayerId Owner = new(0);
    private static readonly CardDefinition Definition = new("card", "Front // Back", Layout.Transform,
        [new Face("Front", FaceRole.Main, new TypeLine(CardType.Creature), OracleText: "Descriptive front text."),
         new Face("Back", FaceRole.Back, new TypeLine(CardType.Land), OracleText: "Descriptive back text.")]);

    [Fact]
    public void Creation_preserves_selection_owner_and_both_descriptive_faces()
    {
        var selected = new StartingCard("card", "printing", CardFinish.Etched);
        var card = GameObject.CreateCard(new ObjectId(1), Definition, selected, Owner, ZoneKey.Of(Zone.Library, Owner), 1);
        Assert.Equal(new CardInstanceId(1), card.CardInstanceId);
        Assert.Equal(Owner, card.Owner);
        Assert.Equal(Owner, card.InitialController);
        Assert.Same(selected, card.DeckSelection);
        Assert.Same(Definition, card.Definition);
        Assert.Equal("Descriptive front text.", card.Face.OracleText);
        Assert.Equal("Descriptive back text.", card.Definition.GetFace(1).OracleText);
        Assert.All(card.Definition.Faces, face => Assert.Empty(face.AbilityList));
    }

    [Theory]
    [InlineData("", null, CardFinish.Nonfoil)]
    [InlineData(" ", null, CardFinish.Nonfoil)]
    [InlineData("other-card", null, CardFinish.Nonfoil)]
    [InlineData("card", " ", CardFinish.Nonfoil)]
    [InlineData("card", null, (CardFinish)99)]
    public void Invalid_selection_is_refused_at_library_creation(string oracleId, string? printing, CardFinish finish)
    {
        var setup = new GameSetup([new PlayerStart("A", [new StartingCard(oracleId, printing, finish)], [])], 1, OpeningHandSize: 0);
        Assert.ThrowsAny<ArgumentException>(() => new ZoneSystem().Create(setup, new Store(Definition)));
    }

    [Fact]
    public void A_definition_without_faces_is_refused_before_a_library_is_returned()
    {
        var setup = new GameSetup([new PlayerStart("A", ["card"], [])], 1, OpeningHandSize: 0);
        Assert.Throws<ArgumentException>(() => new ZoneSystem().Create(setup, new Store(Definition with { Faces = [] })));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void Face_access_refuses_indices_outside_the_definition(int index) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Definition.GetFace(index));

    [Fact]
    public void A_card_cannot_start_in_another_players_library()
    {
        Assert.Throws<ArgumentException>(() => GameObject.CreateCard(new ObjectId(1), Definition,
            new StartingCard("card"), Owner, ZoneKey.Of(Zone.Library, new PlayerId(1)), 1));
    }

    [Fact]
    public void Search_order_shuffle_draw_and_reload_preserve_individual_selected_cards()
    {
        var zones = new ZoneSystem();
        var created = zones.Create(new GameSetup([new PlayerStart("A",
            [new StartingCard("card", "first", CardFinish.Foil), new StartingCard("card", "second", CardFinish.Etched)], [])],
            17, OpeningHandSize: 0), new Store(Definition));
        var library = ZoneKey.Of(Zone.Library, Owner);
        var original = created.State.Objects.Values.ToDictionary(card => card.CardInstanceId!.Value);
        var first = Assert.Single(original.Values, card => card.DeckSelection!.PrintingId == "first");
        var search = LibrarySearch.Find(created.State.In(library), id => created.State.Objects[id].DeckSelection!.Finish == CardFinish.Foil);
        Assert.Equal(first.Id, Assert.Single(search.Matching));
        var order = LibraryOrder.Place(created.State.In(library), first.Id, 0);
        var reordered = new ZoneReordered(library, order);
        var state = reordered.Apply(created.State);
        Assert.Equal(first.Id, state.In(library)[0]);
        var shuffled = LibraryShuffle.Apply(state, library);
        Assert.Equal(original.Values.Select(card => card.Id).OrderBy(id => id.Value), shuffled.State.In(library).OrderBy(id => id.Value));
        var top = shuffled.State.Objects[shuffled.State.In(library)[0]];
        var drawn = LibraryDraw.Apply(shuffled.State, Owner);
        var arrived = drawn.State.Objects[Assert.Single(drawn.State.In(ZoneKey.Of(Zone.Hand, Owner)))];
        Assert.NotEqual(top.Id, arrived.Id);
        Assert.Equal(top.CardInstanceId, arrived.CardInstanceId);
        var log = created.Events.Add(reordered).AddRange(shuffled.Events).AddRange(drawn.Events);
        var restored = GameReducer.Replay(EventLogSerializer.Read(EventLogSerializer.Write(log)));
        Assert.Equal(drawn.State.In(library), restored.In(library));
        Assert.Equal(2, restored.Objects.Count);
        foreach (var card in restored.Objects.Values)
        {
            var before = original[card.CardInstanceId!.Value];
            Assert.Equal(before.DeckSelection, card.DeckSelection);
            Assert.Equal(before.Owner, card.Owner);
            Assert.Equal(before.Definition.OracleId, card.Definition.OracleId);
            Assert.Equal(before.Definition.GetFace(1).OracleText, card.Definition.GetFace(1).OracleText);
        }
    }

    private sealed class Store(CardDefinition definition) : IDefinitionStore
    {
        public CardDefinition? ByOracleId(string oracleId) => definition;
        public CardDefinition? ByName(string name) => definition;
    }
}
