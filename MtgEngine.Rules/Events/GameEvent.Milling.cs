using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Events;

/// <summary>Legacy log discriminator retained for replay and private historical observations; no active operation emits this event.</summary>
public sealed record CardMilled(PlayerId Player, GameObject Card) : GameEvent
{
    public Library.LibraryDepartureObservation? LibraryDeparture { get; init; }
    public override GameState Apply(GameState state) => state;
}
