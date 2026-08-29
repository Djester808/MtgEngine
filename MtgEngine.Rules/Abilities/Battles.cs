using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Abilities;

/// <summary>
/// The intrinsic ability every Siege battle has (CR 310.12b).
/// </summary>
/// <remarks>
/// Written out here rather than compiled from card text, because no card prints it: "When the
/// last defense counter is removed from this permanent, exile it, then you may cast it
/// transformed without paying its mana cost" is what the rules give the subtype, the way the
/// Ring's abilities live in CR 701.54c rather than on a card. The compiler attaches it to every
/// Siege it compiles, so it rides the ordinary trigger machinery — it goes on the stack, may be
/// responded to, and its resolution is what defeats the battle.
/// <para>
/// The state-based action for a battle at defense 0 (CR 704.5v) is deliberately not this: it is
/// the fallback for a Siege whose trigger never resolved, and it lives in
/// <c>StateBasedActions</c> beside its siblings.
/// </para>
/// </remarks>
internal static class SiegeRules
{
    /// <summary>The id the defeat trigger is filed under on every Siege.</summary>
    public const string DefeatAbilityId = "siege-defeated";

    public static TriggeredAbilityDefinition DefeatTrigger { get; } = new()
    {
        Id = DefeatAbilityId,
        Text = "When the last defense counter is removed from this permanent, exile it, then "
            + "you may cast it transformed without paying its mana cost.",
        Triggers = (e, _, source) => LastDefenseCounterRemoved(e, source),
        Effects = [new DefeatSiege()],
    };

    /// <summary>
    /// Whether this event took the battle's last defense counter (CR 310.12b).
    /// </summary>
    /// <remarks>
    /// The source is the object as it was <em>before</em> the event (CR 603.6 — it existed then),
    /// so "last" is answerable: the battle had counters, and this event removed at least that
    /// many. Asking the after-state alone could not tell the removal of the last counter from a
    /// later hit on a battle already at zero. Two events can take defense counters off — damage,
    /// which removes rather than marks (CR 120.3h), and an effect removing them directly.
    /// </remarks>
    private static bool LastDefenseCounterRemoved(GameEvent e, TriggerSource source)
    {
        if (source.Permanent is not { } was)
            return false;

        var had = was.Counters.GetValueOrDefault(CounterKinds.Defense);
        if (had <= 0)
            return false;

        return e switch
        {
            DamageMarked hit => hit.Id == source.Id && hit.Amount >= had,
            CountersChanged changed => changed.Id == source.Id
                && string.Equals(
                    changed.Kind, CounterKinds.Defense, StringComparison.Ordinal)
                && changed.Delta < 0
                && had + changed.Delta <= 0,
            _ => false,
        };
    }
}
