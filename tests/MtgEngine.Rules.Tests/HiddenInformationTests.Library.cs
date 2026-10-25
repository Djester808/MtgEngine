using System.Text.Json;
using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Tests;

public sealed partial class HiddenInformationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Static_top_card_permission_is_scoped_and_revoked_with_its_source(bool revealed)
    {
        var state = Start(openingHand: 0);
        var id = new ObjectId(state.NextObjectId);
        ContinuousEffect effect = revealed ? new PlayWithTopCardRevealed(new You()) : new MayLookAtTopCard(new You());
        var definition = new CardDefinition("permission", "Permission", Layout.Normal,
            [new Face("Permission", FaceRole.Main, new TypeLine(CardType.Enchantment), Abilities: [new StaticAbility(effect)])]);
        state = new ObjectCreated(new GameObject(id, definition, 0, Me, Me, ZoneKey.Shared(Zone.Battlefield), state.NextTimestamp), -1).Apply(state);
        var top = state.In(ZoneKey.Of(Zone.Library, Me))[0];
        var mine = Project(state, Me).Players[0];
        Assert.Equal(top, (revealed ? mine.RevealedTopCard : mine.LookableTopCard)?.Id);
        var theirs = Project(state, Them).Players[0];
        Assert.Null(theirs.LookableTopCard);
        Assert.Equal(revealed ? top : (ObjectId?)null, theirs.RevealedTopCard?.Id);
        if (!revealed)
            Assert.DoesNotContain(MineName, JsonSerializer.Serialize(Project(state, Them)), StringComparison.Ordinal);

        var hiddenSource = state with { Objects = state.Objects.SetItem(id, state.Objects[id] with { FaceDown = true }) };
        Assert.Null(Project(hiddenSource, Me).Players[0].LookableTopCard);
        Assert.Null(Project(hiddenSource, Me).Players[0].RevealedTopCard);
        var phasedSource = state with { Objects = state.Objects.SetItem(id, state.Objects[id] with { PhasedOut = true }) };
        Assert.Null(Project(phasedSource, Me).Players[0].LookableTopCard);
        Assert.Null(Project(phasedSource, Me).Players[0].RevealedTopCard);
        state = new ZoneSystem().Move(state, id, ZoneKey.Of(Zone.Graveyard, Me)).State;
        Assert.Null(Project(state, Me).Players[0].LookableTopCard);
        Assert.Null(Project(state, Them).Players[0].RevealedTopCard);
    }

    [Theory]
    [InlineData(ChoiceKinds.Search)]
    [InlineData(ChoiceKinds.Scry)]
    [InlineData(ChoiceKinds.Surveil)]
    [InlineData(ChoiceKinds.LibraryTopOrder)]
    public void Library_choice_cards_are_limited_to_offered_objects_and_the_choosing_viewer(string kind)
    {
        var state = Start(openingHand: 0);
        var library = state.In(ZoneKey.Of(Zone.Library, Me));
        state = state with
        {
            Pending = new PendingChoice(new ChoiceId(1), Me, kind, "Choose a card",
                [new ChoiceOption("first", "Offered", library[0])], 0, 1,
                new ChoiceContext(null, null, null, ImmutableDictionary<string, string>.Empty)),
        };
        Assert.Equal(library[0], Assert.Single(Project(state, Me).ChoiceCards).Id);
        Assert.Empty(Project(state, Them).ChoiceCards);
        Assert.DoesNotContain(MineName, JsonSerializer.Serialize(Project(state, Them)), StringComparison.Ordinal);
        Assert.Empty(Project(state with { Pending = null }, Me).ChoiceCards);
        Assert.Empty(Project(state with { Pending = state.Pending with { Kind = ChoiceKinds.Targets } }, Me).ChoiceCards);
    }

    [Fact]
    public void Private_top_permission_exposes_only_the_owners_current_top_card()
    {
        var state = Looking(Start(openingHand: 0), Me);
        var top = state.In(ZoneKey.Of(Zone.Library, Me))[0];
        Assert.Equal(top, Project(state, Me).Players[0].LookableTopCard?.Id);
        var other = Project(state, Them);
        Assert.Null(other.Players[0].LookableTopCard);
        Assert.DoesNotContain(MineName, JsonSerializer.Serialize(other), StringComparison.Ordinal);
        Assert.Null(Project(state, Me).Players[0].RevealedTopCard);

        state = new ZoneSystem().Move(state, top, ZoneKey.Of(Zone.Graveyard, Me)).State;
        Assert.Equal(state.In(ZoneKey.Of(Zone.Library, Me))[0], Project(state, Me).Players[0].LookableTopCard?.Id);
        Assert.Null(Project(state with { ActiveEffects = [] }, Me).Players[0].LookableTopCard);
    }

    [Fact]
    public void Private_permission_does_not_invent_a_card_in_an_empty_library()
    {
        var state = Looking(Start(openingHand: 0), Me);
        var zones = new ZoneSystem();
        foreach (var card in state.In(ZoneKey.Of(Zone.Library, Me)))
            state = zones.Move(state, card, ZoneKey.Of(Zone.Graveyard, Me)).State;
        Assert.Null(Project(state, Me).Players[0].LookableTopCard);
    }

    private static GameState Looking(GameState state, PlayerId owner) => state with
    {
        ActiveEffects = state.ActiveEffects.Add(new ActiveEffect(
            1, new MayLookAtTopCard(new You()), null, owner, [], [owner],
            new UntilEndOfTurn(), state.NextTimestamp, FromStaticAbility: true)),
    };
}
