using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Casting;

// Saved-data compatibility only; payment execution and recovery have been removed.
public sealed record RemainingCostPayment(ImmutableList<Cost> Costs, ImmutableList<ObjectId> ChosenObjects)
{
    public CostPaymentContext? Context { get; init; }
    public ActivationPayment? Activation { get; init; }
}

public sealed record ActivationPayment(Commands.ActivateAbility Command,
    ImmutableDictionary<string, string>? Announced, ImmutableList<Events.GameEvent> History)
{
    public ImmutableDictionary<PlayerId, ObjectId> InitialLibraryTops { get; init; } = ImmutableDictionary<PlayerId, ObjectId>.Empty;
}

public sealed record CostPaymentContext(GameObject? Source, Characteristics? Characteristics,
    PlayerId Player, int? X, Color? PayOnlyWith, ObjectFilter? PayOnlyFrom,
    bool PayPhyrexianWithLife, ImmutableList<string>? Choices);

/// <summary>Recognizes obsolete recovery logs and refuses to replay unsupported state.</summary>
public sealed record PaymentRecoveryApplied : Events.GameEvent
{
    public override GameState Apply(GameState state) =>
        throw new UnsupportedGameLogException();
}
