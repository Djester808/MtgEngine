namespace MtgEngine.Rules.Views;

/// <summary>Current projected objects only. Historical reveals are not current object access.</summary>
public static class VisibleGameObjects
{
    public static IEnumerable<ObjectView> Current(GameView view) =>
        view.Battlefield.Concat(view.Stack).Concat(view.Hand).Concat(view.Exile).Concat(view.ChoiceCards)
            .Concat(view.InspectedLibraryCards)
            .Concat(view.Players.SelectMany(player => player.Graveyard.Concat(player.Command)))
            .Concat(view.Players.Select(player => player.RevealedTopCard).OfType<ObjectView>())
            .Concat(view.Players.Select(player => player.LookableTopCard).OfType<ObjectView>());
}
