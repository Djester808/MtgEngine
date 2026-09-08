using MtgEngine.Cards;
using MtgEngine.Rules.Casting;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Interpreter;

/// <summary>
/// CR 701.3, attaching. An effect that attaches an Aura, an Equipment or a Fortification to an
/// object takes it from wherever it is attached and puts it onto that object (CR 701.3a); the
/// attachment is the same stored property, and so the same <see cref="ObjectChanged"/> with
/// <c>AttachedTo</c>, that the casting system writes when an Aura spell resolves attached to what
/// it was cast at (CR 303.4f). Nothing else about the permanent changes: it does not move zones,
/// and it does not become a new object (CR 400.7 is about zone changes).
/// <para>
/// CR 701.3b is the whole of what an illegal attachment does: nothing. Legality is read from the
/// attaching permanent's own kind — an Equipment onto a creature (CR 301.5c), a Fortification onto
/// a land (CR 301.6b), an Aura onto an object its "enchant" ability admits (CR 303.4 with
/// 702.5b) — and an object that is none of those three cannot be attached at all. What happens
/// <em>later</em>, when a host stops being legal, is not this arm's: CR 704.5m buries the Aura and
/// CR 704.5n unattaches the Equipment, both in the state-based actions.
/// </para>
/// </summary>
public sealed partial class EffectInterpreter
{
    private const string AuraSubtype = "Aura";
    private const string EquipmentSubtype = "Equipment";
    private const string FortificationSubtype = "Fortification";

    /// <summary>
    /// CR 701.3a: each named permanent is attached to the object the effect names. A permanent is
    /// attached to at most one object, so a selector that named several hosts attaches to the first
    /// it named — the announcement's order, which is the order the text reads (CR 601.2c).
    /// </summary>
    [RuleRef("701.3")]
    private EffectRun Attaching(EffectRun run, Selector? what, Selector? to)
    {
        var hosts = Objects(run, to);
        if (hosts.Count == 0)
            return run;

        var host = hosts[0];
        foreach (var id in Objects(run, what))
        {
            // CR 701.3b: an attachment the rules do not allow simply does not happen. It is not a
            // refusal — the rest of the effect still runs — and it is not a crash.
            if (!CanAttach(run, id, host))
                continue;

            // CR 303.4f: the same event the casting system emits for an Aura entering attached, so
            // one reader of the attachment serves both roads. Setting AttachedTo clears any
            // attachment to a player, which is right: the two exclude each other.
            run = Emit(run, new ObjectChanged(id, AttachedTo: host));
        }

        return run;
    }

    /// <summary>
    /// Whether this permanent may legally be attached to that object right now, CR 701.3b. Both
    /// have to be on the battlefield: an attachment is a relationship between two permanents, and
    /// nothing off the battlefield is one (CR 110.1). Nothing may be attached to itself.
    /// </summary>
    [RuleRef("701.3b")]
    private bool CanAttach(EffectRun run, ObjectId what, ObjectId host)
    {
        var state = run.State;
        if (what == host)
            return false;
        if (!state.Objects.TryGetValue(what, out var moving) || moving.Zone.Zone != Zone.Battlefield)
            return false;
        if (!state.Objects.TryGetValue(host, out var target) || target.Zone.Zone != Zone.Battlefield)
            return false;

        var chars = characteristics.Of(state, what);

        // CR 301.5c: an Equipment can be attached only to a creature. CR 301.6b says the same of a
        // Fortification and a land. Both are read off the host's current types, not its printed
        // ones, so a land animated into a creature is a legal host for an Equipment.
        if (HasSubtype(chars, EquipmentSubtype))
            return characteristics.Of(state, host).Types.HasFlag(CardType.Creature);
        if (HasSubtype(chars, FortificationSubtype))
            return characteristics.Of(state, host).Types.HasFlag(CardType.Land);
        if (HasSubtype(chars, AuraSubtype))
            return Enchantable(state, chars, what, host, run.Context.X);

        // CR 701.3a names three kinds of permanent that can be attached. Anything else has nothing
        // legal to be attached to, so CR 701.3b's "nothing happens" is the whole answer.
        return false;
    }

    /// <summary>
    /// CR 702.5b: "enchant [object]" is what a legal host means for an Aura, and the filter is
    /// asked of the candidate through the query rather than through a second copy of it. An Aura
    /// with no enchant ability has nothing to be illegal about; one that enchants a <em>player</em>
    /// has no legal object host at all (CR 303.4f makes the two halves exclusive).
    /// </summary>
    [RuleRef("702.5b")]
    private bool Enchantable(GameState state, Characteristics aura, ObjectId what, ObjectId host, int? x)
    {
        var enchant = aura.Abilities.OfType<KeywordAbility>().FirstOrDefault(k => k.Keyword == KeywordKind.Enchant);
        if (enchant is null)
            return true;
        if (enchant.Of is not { } filter)
            return enchant.OfPlayer is null;

        // The filter is read from the Aura's own point of view — "enchant creature you control" is
        // the Aura's controller's — so the context carries the Aura, not the effect's source.
        return query.Matches(state, filter, host, CostPayment.Context(what, aura.Controller, x));
    }

    /// <summary>
    /// CR 205.3: does this object have that subtype? Aura, Equipment and Fortification are not
    /// creature types, so the "is every creature type" flag (CR 205.3d) cannot grant one and the
    /// printed-and-granted list is the whole question.
    /// </summary>
    [RuleRef("205.3")]
    private static bool HasSubtype(Characteristics chars, string subtype) =>
        chars.Subtypes.Any(s => string.Equals(s, subtype, StringComparison.OrdinalIgnoreCase));
}
