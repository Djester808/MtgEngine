using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

public sealed partial class HiddenInformationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Leaving_revokes_inspection_as_viewer_or_library_owner(bool viewerLeaves)
    {
        var state = Start(openingHand: 0);
        var card = state.In(ZoneKey.Of(Zone.Library, Them))[0];
        state = new LibraryInspectionChanged("search", new LibraryInspection(Me, Them, [card])).Apply(state);
        state = new LibraryInspectionChanged("unrelated", new LibraryInspection(Them, Them, [card])).Apply(state);
        Assert.Single(Project(state, Me).InspectedLibraryCards);

        state = new PlayerLost(viewerLeaves ? Me : Them, "concession").Apply(state);

        Assert.False(state.LibraryInspections.ContainsKey("search"));
        Assert.Empty(Project(state, Me).InspectedLibraryCards);
        Assert.Equal(viewerLeaves, state.LibraryInspections.ContainsKey("unrelated"));
    }

    [Fact]
    public void Ending_the_game_revokes_active_library_inspections()
    {
        var state = Start(openingHand: 0);
        var card = state.In(ZoneKey.Of(Zone.Library, Them))[0];
        state = new LibraryInspectionChanged("search", new LibraryInspection(Me, Them, [card])).Apply(state);

        state = new GameEnded(new GameOutcome([Me], false, "test")).Apply(state);

        Assert.Empty(state.LibraryInspections);
        Assert.Empty(Project(state, Me).InspectedLibraryCards);
    }
}
