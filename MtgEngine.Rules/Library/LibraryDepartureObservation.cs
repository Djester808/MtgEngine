using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Library;

/// <summary>Only the publicly visible departing card and its evaluated characteristics.</summary>
public sealed record LibraryDepartureObservation(GameObject Card, Characteristics Appearance)
{
    internal static LibraryDepartureObservation? Capture(GameState state, Events.ObjectMoved move, ICharacteristics? layers)
    {
        if (layers is null || move.LeftFrom?.Zone != Zone.Library || move.To.Zone.Zone is not (Zone.Hand or Zone.Library)
            || !state.Objects.TryGetValue(move.From, out var card) || card.FaceDown)
            return null;
        var explicitlyRevealed = state.LibraryInspections.Values.Any(inspection =>
            inspection.PublicReveal && inspection.Resolution is not null
            && inspection.Owner == card.Owner && inspection.Cards.Contains(card.Id));
        var revealedTop = state.RevealedLibraryTops.TryGetValue(card.Owner, out var top) && top == card.Id
            && LibraryDisclosurePolicy.TopAvailable(state, card.Owner)
            && state.In(card.Zone).FirstOrDefault() == card.Id
            && layers.PlayerRestrictions(state, card.Owner).TopCardRevealed;
        if (!explicitlyRevealed && !revealedTop)
            return null;
        return new(card, layers.Of(state, card.Id));
    }
}
