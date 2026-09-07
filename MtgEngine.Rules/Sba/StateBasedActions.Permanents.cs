using System.Globalization;
using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Sba;

/// <summary>
/// The arms of 704.5 that are about objects rather than players. Every verdict is decided from
/// the snapshot handed in and applied afterwards, so two actions on the same object cannot see
/// each other's work — 704.3's "simultaneously as a single event".
/// </summary>
public sealed partial class StateBasedActions
{
    /// <summary>What happens to one object, and the verb the log records for it.</summary>
    private enum Action
    {
        /// <summary>Into its owner's graveyard: 704.5f, 704.5g, 704.5i, 704.5m.</summary>
        Graveyard,

        /// <summary>Out of the game entirely: 704.5d, 704.5e.</summary>
        Cease,

        /// <summary>Off whatever it was attached to, staying on the battlefield: 704.5n.</summary>
        Unattach,
    }

    private sealed record Verdict(ObjectId Id, Action What, string Verb);

    private const string AuraSubtype = "Aura";
    private const string EquipmentSubtype = "Equipment";
    private const string FortificationSubtype = "Fortification";

    /// <summary>Every object action of one check, decided from <paramref name="snapshot"/> and then applied.</summary>
    [RuleRef("704.5")]
    private Outcome Permanents(GameState snapshot)
    {
        var counters = new List<GameEvent>();
        var verdicts = new List<Verdict>();

        foreach (var id in snapshot.Objects.Keys.OrderBy(k => k.Value))
        {
            var obj = snapshot.Objects[id];

            // A player who has lost keeps their objects in this slice: leaving the game takes
            // them with it (CR 800.4), which is the turn system's business, not 704's.
            if (snapshot.Player(obj.Owner).HasLost)
                continue;

            var zone = obj.Zone.Zone;

            // 704.5d If a token is in a zone other than the battlefield, it ceases to exist.
            if (obj.IsToken && zone != Zone.Battlefield)
            {
                verdicts.Add(new Verdict(id, Action.Cease, Ceased));
                continue;
            }

            // 704.5e If a copy of a spell is in a zone other than the stack, it ceases to exist.
            if (obj.Stack is { IsCopy: true } && zone != Zone.Stack)
            {
                verdicts.Add(new Verdict(id, Action.Cease, Ceased));
                continue;
            }

            if (zone != Zone.Battlefield)
                continue;

            var chars = characteristics.Of(snapshot, id);

            // 704.5q N +1/+1 and N -1/-1 counters are removed, N the smaller of the two.
            var pairs = Math.Min(
                obj.Counters.GetValueOrDefault(Counters.PlusOnePlusOne),
                obj.Counters.GetValueOrDefault(Counters.MinusOneMinusOne));
            if (pairs > 0)
            {
                counters.Add(new CountersChanged(id, Counters.PlusOnePlusOne, -pairs));
                counters.Add(new CountersChanged(id, Counters.MinusOneMinusOne, -pairs));
            }

            if (Leaving(snapshot, id, obj, chars) is { } verdict)
                verdicts.Add(verdict);
        }

        var state = snapshot;
        var events = ImmutableList.CreateBuilder<GameEvent>();
        foreach (var e in counters)
        {
            events.Add(e);
            state = e.Apply(state);
        }

        foreach (var verdict in verdicts)
        {
            var applied = verdict.What switch
            {
                Action.Cease => Cease(state, verdict.Id),
                Action.Unattach => Unattach(state, verdict.Id),
                _ => Bury(state, verdict.Id, verdict.Verb),
            };
            state = applied.State;
            events.AddRange(applied.Events);
        }

        // 704.5j goes last because it asks rather than acts: the question halts the game, so
        // everything else this check performs has to be in the state the chooser is shown.
        if (state.Pending is null && Legend(state) is { } asked)
        {
            events.Add(asked);
            state = asked.Apply(state);
        }

        return new Outcome(state, events.ToImmutable());
    }

    /// <summary>
    /// Whether this permanent leaves the battlefield now, and by which verb. The order is the
    /// order of the rules: toughness before lethal damage, because 704.5f is not a destruction
    /// and indestructible does not stop it.
    /// </summary>
    private Verdict? Leaving(GameState state, ObjectId id, GameObject obj, Characteristics chars)
    {
        if (chars.Types.HasFlag(CardType.Creature) && chars.Toughness is { } toughness)
        {
            // 704.5f Toughness 0 or less: put into its owner's graveyard. Not destroyed —
            // regeneration cannot replace it and 702.12b's indestructible does not apply.
            if (toughness <= 0)
                return new Verdict(id, Action.Graveyard, PutIntoGraveyard);

            // 704.5g Damage marked greater than or equal to toughness is lethal and destroys.
            // 702.12b A permanent with indestructible is not destroyed by lethal damage.
            if (obj.Damage >= toughness && !chars.Has(KeywordKind.Indestructible))
                return new Verdict(id, Action.Graveyard, Destroyed);

            // 704.5h — a creature dealt damage by a source with deathtouch is destroyed — is
            // not here: the state does not record which source dealt the damage it holds. See
            // seam-requests/E5.md.
        }

        // 704.5i A planeswalker with loyalty 0 is put into its owner's graveyard. Loyalty is
        // the number of loyalty counters on it, CR 306.5b.
        if (chars.Types.HasFlag(CardType.Planeswalker) && obj.Counters.GetValueOrDefault(Counters.Loyalty) <= 0)
            return new Verdict(id, Action.Graveyard, PutIntoGraveyard);

        // 704.5m An Aura attached to an illegal object, or to nothing, goes to the graveyard.
        if (Has(chars, AuraSubtype) && !AttachedLegally(state, obj, chars))
            return new Verdict(id, Action.Graveyard, PutIntoGraveyard);

        // 704.5n An Equipment or Fortification attached to an illegal permanent becomes
        // unattached. It remains on the battlefield.
        if ((Has(chars, EquipmentSubtype) || Has(chars, FortificationSubtype))
            && obj.AttachedTo is { } host
            && !HostIsLegal(state, chars, host))
        {
            return new Verdict(id, Action.Unattach, string.Empty);
        }

        return null;
    }

    private static bool Has(Characteristics chars, string subtype) =>
        chars.Subtypes.Any(s => string.Equals(s, subtype, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether an Aura is attached to something it may legally enchant, CR 702.5b.</summary>
    [RuleRef("704.5m")]
    private bool AttachedLegally(GameState state, GameObject aura, Characteristics chars)
    {
        if (aura.AttachedTo is not { } host)
            return false;
        if (!state.Objects.TryGetValue(host, out var target) || target.Zone.Zone != Zone.Battlefield)
            return false;

        // "Enchant [object]" is what legal means for an Aura. With no enchant ability there is
        // nothing to be illegal about, so the attachment stands.
        var enchant = chars.Abilities.OfType<KeywordAbility>().FirstOrDefault(k => k.Keyword == KeywordKind.Enchant);
        return enchant?.Of is not { } filter || query.Matches(state, filter, host, ContextOf(chars));
    }

    /// <summary>Equipment attaches to a creature (CR 301.5c), a Fortification to a land (CR 301.6b).</summary>
    [RuleRef("704.5n")]
    private bool HostIsLegal(GameState state, Characteristics chars, ObjectId host)
    {
        if (!state.Objects.TryGetValue(host, out var target) || target.Zone.Zone != Zone.Battlefield)
            return false;

        var needs = Has(chars, FortificationSubtype) ? CardType.Land : CardType.Creature;
        return characteristics.Of(state, host).Types.HasFlag(needs);
    }

    /// <summary>
    /// The legend rule, 704.5j: two or more legendary permanents with the same name controlled
    /// by one player. The rest are put into their owners' graveyards, but only after that
    /// player has chosen which to keep, so this raises the question and answers nothing.
    /// </summary>
    [RuleRef("704.5j")]
    private ChoiceRequested? Legend(GameState state)
    {
        // Grouped on (controller, name) rather than a joined string: a name may contain any
        // character, and a separator that a name could also contain would merge two groups.
        var groups = new Dictionary<(PlayerId Controller, string Name), List<ObjectId>>();
        var order = new List<(PlayerId Controller, string Name)>();

        foreach (var id in state.Objects.Keys.OrderBy(k => k.Value))
        {
            var obj = state.Objects[id];
            if (obj.Zone.Zone != Zone.Battlefield)
                continue;

            var chars = characteristics.Of(state, id);
            if (!chars.Supertypes.HasFlag(Supertype.Legendary) || state.Player(chars.Controller).HasLost)
                continue;

            var key = (chars.Controller, chars.Name);
            if (!groups.TryGetValue(key, out var duplicates))
            {
                groups[key] = duplicates = [];
                order.Add(key);
            }

            duplicates.Add(id);
        }

        foreach (var (controller, name) in order)
        {
            var duplicates = groups[(controller, name)];
            if (duplicates.Count < 2)
                continue;

            var options = duplicates
                .Select(d => new ChoiceOption(d.Value.ToString(CultureInfo.InvariantCulture), name, Object: d))
                .ToImmutableList();

            return new ChoiceRequested(new PendingChoice(
                new ChoiceId(state.NextChoiceId),
                controller,
                LegendRule,
                "Choose which one to keep; the rest go to their owners' graveyards.",
                options,
                1,
                1,
                new ChoiceContext(null, null, LegendRule, ImmutableDictionary<string, string>.Empty.Add("name", name))));
        }

        return null;
    }

    /// <summary>
    /// Into its owner's graveyard through the zone system, so the object's identity changes
    /// once and in one place (CR 400.7), preceded by the verb the log needs. "destroyed" is
    /// the verb a regeneration or an indestructible replacement will hook, CR 614.
    /// </summary>
    private Outcome Bury(GameState state, ObjectId id, string verb)
    {
        if (!state.Objects.TryGetValue(id, out var obj))
            return Outcome.Unchanged(state);

        var left = new ObjectLeftBy(id, verb, null);
        var moved = zones.Move(left.Apply(state), id, ZoneKey.Of(Zone.Graveyard, obj.Owner));
        return new Outcome(moved.State, moved.Events.Insert(0, left));
    }

    /// <summary>Out of the game: 704.5d and 704.5e leave nothing behind, not even a graveyard card.</summary>
    private static Outcome Cease(GameState state, ObjectId id)
    {
        if (!state.Objects.ContainsKey(id))
            return Outcome.Unchanged(state);

        var left = new ObjectLeftBy(id, Ceased, null);
        var gone = new ObjectCeasedToExist(id);
        return new Outcome(gone.Apply(left.Apply(state)), [left, gone]);
    }

    [RuleRef("704.5n")]
    private static Outcome Unattach(GameState state, ObjectId id)
    {
        if (!state.Objects.ContainsKey(id))
            return Outcome.Unchanged(state);

        var cleared = new ObjectChanged(id, ClearAttachment: true);
        return new Outcome(cleared.Apply(state), [cleared]);
    }

    /// <summary>The bare context a filter needs when no effect is running: a controller and nothing else.</summary>
    private static EffectContext ContextOf(Characteristics chars) => new(
        null,
        chars.Controller,
        [],
        [],
        null,
        ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty,
        ImmutableDictionary<string, string>.Empty,
        ImmutableDictionary<string, int>.Empty,
        [],
        null);
}
