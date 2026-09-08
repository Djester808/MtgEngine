using System.Globalization;
using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Replacement;

/// <summary>
/// Wave 29: the three numbers and one prohibition the replacement road was missing — the X a
/// permanent spell was cast for (CR 601.2b with 107.3b), what a prevention actually took off the
/// event (CR 615), and an effect that says a card can't enter the battlefield at all (CR 613.11).
/// </summary>
public sealed partial class ReplacementSystem
{
    /// <summary>
    /// CR 601.2b with 107.3b: the value chosen for X as the spell was cast is the value of X for
    /// everything that spell's own text then asks, including the replacement that reads "enters
    /// with X +1/+1 counters" as the permanent arrives (CR 614.12).
    /// <para>
    /// The chosen value lives on the spell, and the spell is what the move carries as its departed
    /// object; the arriving permanent is a new object (CR 400.7) and has no stack entry of its own.
    /// Read as none, <see cref="Query.Query"/> answers X as 0 (CR 107.3b), the permanent enters with
    /// no counters, and a 0/0 creature is put into its owner's graveyard by state-based actions
    /// (CR 704.5f) — which is the whole of every "enters with X counters" card failing.
    /// </para>
    /// </summary>
    [RuleRef("601.2b")]
    private static int? ChosenX(GameEvent proposed) => proposed switch
    {
        ObjectMoved { Departed.Stack.X: { } x } => x,
        _ => null,
    };

    /// <summary>
    /// CR 615: what the prevention this effect is running took off the event, carried so the rest of
    /// the same effect can read it ("for each 1 damage prevented this way").
    /// <para>
    /// It is written even when it is zero, and it is absent when no prevention ran. Those are two
    /// different answers: a shield that met no damage prevented none, which the card may legally do
    /// nothing for, while a missing key means nothing prevented anything and the reader refuses
    /// rather than playing an invented 0 (seam-requests/E5l.md § 1). The text is decimal and
    /// culture-invariant, the spelling every carried number uses.
    /// </para>
    /// </summary>
    [RuleRef("615")]
    private static ImmutableDictionary<string, string> Prevented(int? prevented) =>
        prevented is { } amount
            ? ImmutableDictionary<string, string>.Empty.Add(Query.Query.PreventedDamageAmount, amount.ToString(CultureInfo.InvariantCulture))
            : ImmutableDictionary<string, string>.Empty;

    /// <summary>
    /// CR 613.11 with 111.5: a continuous effect that modifies the rules of the game and says a card
    /// can't enter the battlefield. Nothing is countered, destroyed or replaced — the object simply
    /// does not arrive — so this is not a replacement effect and is read here, beside the player's
    /// own limits, where the entry is still only proposed and no card-driven effect has been offered
    /// it. CR 101.2: "can't" beats "does".
    /// <para>
    /// It is read wherever an object <em>would be put onto</em> the battlefield and not only where a
    /// permanent spell resolves, so a card returned or searched into play stays where it is and a
    /// token with a forbidden characteristic is never created (CR 111.5). What a permanent spell
    /// whose controller can't put it onto the battlefield does next — CR 608.3e puts it into its
    /// owner's graveyard — belongs to the zone system, which proposed the move; here the move is
    /// refused and nothing else happens (seam-requests/E8g.md).
    /// </para>
    /// </summary>
    [RuleRef("613.11")]
    private bool CantEnter(GameState state, GameEvent proposed)
    {
        GameObject arriving;
        GameObject? standing;

        switch (proposed)
        {
            case ObjectMoved moved when moved.To.Zone.Zone == Zone.Battlefield:
                arriving = moved.To;
                standing = state.Objects.TryGetValue(moved.From, out var from) ? from : moved.Departed;
                break;

            // CR 111.5: a token that would be created with a characteristic an effect forbids is
            // never created at all, so the creation is refused the same way the move is.
            case ObjectCreated created when created.Object.Zone.Zone == Zone.Battlefield:
                arriving = created.Object;
                standing = null;
                break;

            default:
                return false;
        }

        var probe = proposed is ObjectCreated made && !state.Objects.ContainsKey(made.Object.Id)
            ? made.Apply(state)
            : Probe(state, proposed);

        foreach (var active in state.ActiveEffects)
        {
            if (active.Effect is CantEnterBattlefield cant
                && Admits(probe, cant.What, arriving, standing, Context(active.Source, active.Controller, proposed)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// CR 400.1 with 613.11: which object the restriction's filter is asked of, and where.
    /// <para>
    /// A filter that names zones — "creature cards in graveyards and libraries", an
    /// <see cref="ObjectFilter.InAnyOf"/> of the two — describes the card as it still is, so it is
    /// read against the object that is about to leave. An object is in exactly one zone (CR 400.1),
    /// so the plural rider is answered here by the zone that object is actually in and the filter is
    /// handed on naming that one zone; the query layer reads a single <c>In</c> and does not yet read
    /// the plural (seam-requests/E8g.md). A filter that names no zone describes a permanent
    /// (CR 110.1), so it is read against the object as it would exist on the battlefield, which is
    /// the probe of CR 614.12 — the reading that lets a token be judged at all, since a token that is
    /// never created was never anywhere else.
    /// </para>
    /// </summary>
    [RuleRef("400.1")]
    private bool Admits(GameState probe, ObjectFilter what, GameObject arriving, GameObject? standing, EffectContext context)
    {
        IReadOnlyList<Zone>? zones = what.InAnyOf is { Count: > 0 } any
            ? any
            : what.In is { } one ? [one] : null;

        if (zones is null)
            return probe.Objects.ContainsKey(arriving.Id) && _query.Matches(probe, what, arriving.Id, context);

        return standing is not null
            && zones.Contains(standing.Zone.Zone)
            && probe.Objects.ContainsKey(standing.Id)
            && _query.Matches(probe, what with { In = standing.Zone.Zone, InAnyOf = null }, standing.Id, context);
    }
}
