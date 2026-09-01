using MtgEngine.Rules.Cards;

namespace MtgEngine.Rules.Abilities;

/// <summary>
/// The abilities the emblem named The Ring gives its bearer (CR 701.54c).
/// </summary>
/// <remarks>
/// Written out here rather than compiled from card text, because no card carries them: the Ring
/// is an emblem the rules hand out, and its wording lives in CR 701.54c. They are granted to the
/// bearer as its characteristics are computed, so a creature that stops being the Ring-bearer
/// stops having them with no effect to end.
/// <para>
/// Each is "as long as the Ring has tempted you N or more times", so they accumulate and none is
/// ever taken away — which is why they are three separate abilities and not one that grows.
/// </para>
/// </remarks>
internal static class RingAbilities
{
    /// <summary>Two temptations: "whenever your Ring-bearer attacks, draw a card, then discard a card."</summary>
    public static TriggeredAbilityDefinition DrawsAndDiscards { get; } = new()
    {
        Id = "the-ring-2",
        Text = "Whenever your Ring-bearer attacks, draw a card, then discard a card.",
        Triggers = TriggerOn("~ attacks"),
        Effects =
        [
            new DrawCards(new Amount(1)),
            new DiscardCards(new Amount(1)),
        ],
    };

    /// <summary>
    /// Three temptations: "whenever your Ring-bearer becomes blocked by a creature, the blocking
    /// creature's controller sacrifices it at end of combat."
    /// </summary>
    public static TriggeredAbilityDefinition BlockersAreSacrificed { get; } = new()
    {
        Id = "the-ring-3",
        Text = "Whenever your Ring-bearer becomes blocked by a creature, the blocking creature's "
            + "controller sacrifices it at end of combat.",
        Triggers = TriggerOn("~ becomes blocked"),
        Effects = [new SacrificeBlockersAtEndOfCombat()],
    };

    /// <summary>
    /// Four temptations: "whenever your Ring-bearer deals combat damage to a player, each
    /// opponent loses 3 life."
    /// </summary>
    public static TriggeredAbilityDefinition DrainsOnDamage { get; } = new()
    {
        Id = "the-ring-4",
        Text = "Whenever your Ring-bearer deals combat damage to a player, each opponent loses 3 life.",
        Triggers = TriggerOn("~ deals combat damage to a player"),
        Effects = [new ChangeLife(new Amount(-3)) { Scope = PlayerScope.EachOpponent }],
    };

    /// <summary>
    /// The trigger vocabulary the cards use, asked for a condition the rules wrote rather than a
    /// card. A condition it cannot read would be a silent ability, so it is refused loudly here
    /// where there is no card to leave unread instead.
    /// </summary>
    private static Func<Events.GameEvent, State.GameState, TriggerSource, bool> TriggerOn(
        string condition) =>
        TriggerConditions.Parse(condition)
            ?? throw new InvalidOperationException(
                $"The Ring's trigger condition '{condition}' is not one the engine reads.");
}
