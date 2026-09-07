using System.Globalization;
using System.Text.Json;
using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Layers;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Views;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// What a viewer may see, CR 400.2. Every game here is a real one built through
/// <c>Subsystems.Wave1()</c>, and the strongest assertion is the one made on the serialised
/// view: a name a viewer is not entitled to must not appear anywhere in the text, however the
/// projector arranges its records.
/// </summary>
public sealed class HiddenInformationTests
{
    private const string MineOracle = "oracle-mine";
    private const string TheirsOracle = "oracle-theirs";
    private const string MineName = "quiet fixture of the first seat";
    private const string TheirsName = "quiet fixture of the second seat";

    private static PlayerId Me => new(0);

    private static PlayerId Them => new(1);

    [Fact]
    [RuleRef("400.2")]
    public void An_opponents_hand_is_a_count_and_never_a_card()
    {
        var state = Start();

        var view = Project(state, Me);

        Assert.Equal(7, view.Players[1].HandCount);
        Assert.Equal(7, view.Hand.Count);
        Assert.All(view.Hand, card => Assert.Equal(MineName, card.Name));

        var json = JsonSerializer.Serialize(view);
        Assert.DoesNotContain(TheirsName, json, StringComparison.Ordinal);
        Assert.DoesNotContain(TheirsOracle, json, StringComparison.Ordinal);
        Assert.Contains(MineName, json, StringComparison.Ordinal);
    }

    [Fact]
    [RuleRef("401.2")]
    public void A_library_is_a_count_and_never_a_card()
    {
        var state = Start(openingHand: 0);

        var view = Project(state, Me);

        Assert.Empty(view.Hand);
        Assert.Equal(10, view.Players[0].LibraryCount);
        Assert.Equal(10, view.Players[1].LibraryCount);

        // Nothing has left a library, so neither deck's name may be anywhere in the view.
        var json = JsonSerializer.Serialize(view);
        Assert.DoesNotContain(MineName, json, StringComparison.Ordinal);
        Assert.DoesNotContain(TheirsName, json, StringComparison.Ordinal);
    }

    [Fact]
    [RuleRef("708.2")]
    public void A_face_down_permanent_shows_as_face_down_with_no_name_and_no_definition()
    {
        var (state, id) = MoveFromHand(Start(), Them, ZoneKey.Shared(Zone.Battlefield));
        state = state with { Objects = state.Objects.SetItem(id, state.Objects[id] with { FaceDown = true }) };

        var view = Project(state, Me);

        var permanent = Assert.Single(view.Battlefield);
        Assert.True(permanent.FaceDown);
        Assert.Equal(string.Empty, permanent.Name);
        Assert.Equal(string.Empty, permanent.OracleId);
        Assert.Equal(2, permanent.Power);
        Assert.Equal(2, permanent.Toughness);
        Assert.Equal(new[] { "Creature" }, permanent.Types);
        Assert.Empty(permanent.Keywords);
        Assert.DoesNotContain(TheirsName, JsonSerializer.Serialize(view), StringComparison.Ordinal);
    }

    [Fact]
    [RuleRef("400.2")]
    public void The_public_zones_are_listed_for_everyone()
    {
        var (state, id) = MoveFromHand(Start(), Them, ZoneKey.Of(Zone.Graveyard, Them));

        var view = Project(state, Me);

        var card = Assert.Single(view.Players[1].Graveyard);
        Assert.Equal(id, card.Id);
        Assert.Equal(TheirsName, card.Name);
        Assert.Equal(1, view.Players[1].GraveyardCount);
        Assert.Equal(6, view.Players[1].HandCount);
    }

    [Fact]
    [RuleRef("613.5")]
    public void Every_object_carries_computed_characteristics_and_the_stores_enforcement()
    {
        var (state, _) = MoveFromHand(Start(), Them, ZoneKey.Shared(Zone.Battlefield));

        var view = Project(state, Me);

        var permanent = Assert.Single(view.Battlefield);
        Assert.Equal(2, permanent.Power);
        Assert.Equal(3, permanent.Toughness);
        Assert.Contains("Creature", permanent.Types);
        Assert.Contains("Beast", permanent.Subtypes);
        Assert.Contains(KeywordKind.Flying.ToString(), permanent.Keywords);
        Assert.Equal(Them, permanent.Controller);

        // The store enforces the first seat's card and badges the second's honour-system.
        Assert.False(permanent.Enforced);
        Assert.True(view.Hand[0].Enforced);
    }

    [Fact]
    [RuleRef("101.4")]
    public void Another_players_choice_is_visible_as_a_question_and_never_as_its_options()
    {
        var asked = new PendingChoice(
            new ChoiceId(1),
            Them,
            "discard",
            $"Discard {TheirsName}?",
            [new ChoiceOption("a", TheirsName)],
            1,
            1,
            new ChoiceContext(null, null, null, ImmutableDictionary<string, string>.Empty));
        var state = Start() with { Pending = asked };

        Assert.Equal(asked, Project(state, Them).Pending);

        var onlooker = Project(state, Me);
        Assert.Equal(Them, onlooker.Pending!.Player);
        Assert.Equal("discard", onlooker.Pending.Kind);
        Assert.Empty(onlooker.Pending.Options);
        Assert.DoesNotContain(TheirsName, JsonSerializer.Serialize(onlooker), StringComparison.Ordinal);
    }

    private static GameView Project(GameState state, PlayerId viewer) =>
        new ViewProjector(new LayerSystem(), oracle => oracle == MineOracle).Project(state, viewer);

    private static GameState Start(int openingHand = 7)
    {
        var game = new Game(Subsystems.Wave1(), new HiddenInformationDefinitions());
        var setup = new GameSetup(
            [new PlayerStart("One", Deck(MineOracle), []), new PlayerStart("Two", Deck(TheirsOracle), [])],
            Seed: 11,
            OpeningHandSize: openingHand);
        return game.Create(setup).State;
    }

    private static ImmutableList<string> Deck(string oracleId) => [.. Enumerable.Repeat(oracleId, 10)];

    /// <summary>Moves one card out of a hand so a public zone has something in it, CR 400.7 giving it a new id.</summary>
    private static (GameState State, ObjectId Id) MoveFromHand(GameState state, PlayerId owner, ZoneKey to)
    {
        var card = state.In(ZoneKey.Of(Zone.Hand, owner))[0];
        var moved = new ZoneSystem().Move(state, card, to, controller: owner);
        return (moved.State, moved.Events.OfType<ObjectMoved>().Last().To.Id);
    }

    private sealed class HiddenInformationDefinitions : IDefinitionStore
    {
        private static readonly CardDefinition Mine = Card(MineOracle, MineName, 2, 2, []);

        private static readonly CardDefinition Theirs = Card(TheirsOracle, TheirsName, 2, 3, [new KeywordAbility(KeywordKind.Flying)]);

        public CardDefinition? ByName(string name) =>
            name == MineName ? Mine : name == TheirsName ? Theirs : null;

        public CardDefinition? ByOracleId(string oracleId) =>
            oracleId == MineOracle ? Mine : oracleId == TheirsOracle ? Theirs : null;

        private static CardDefinition Card(string oracleId, string name, int power, int toughness, Ability[] abilities) =>
            new(
                oracleId,
                name,
                Layout.Normal,
                [
                    new Face(
                        name,
                        FaceRole.Main,
                        new TypeLine(CardType.Creature, Supertype.None, ["Beast"]),
                        Power: new PrintedStat(power.ToString(CultureInfo.InvariantCulture)),
                        Toughness: new PrintedStat(toughness.ToString(CultureInfo.InvariantCulture)),
                        Abilities: abilities),
                ]);
    }
}
