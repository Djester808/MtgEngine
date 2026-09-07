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

            // 704.5d and 704.5e: a token and a copy leave the game the same way, so they are one
            // path here and one question in Ceases.
            if (Ceases(obj, zone))
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
    /// Whether this object ceases to exist now: CR 704.5d for a token and CR 704.5e for a copy,
    /// asked together because they perform the same action — the object is not a card, so there
    /// is nothing for the zone it reached to hold. Each has its own set of zones it may be in:
    /// <list type="bullet">
    /// <item>704.5d — a token exists only on the battlefield.</item>
    /// <item>704.5e — a copy of a spell exists only on the stack; a copy of a card exists on the
    /// stack or the battlefield. The wider set is the one asked, because a copy this engine can
    /// reach on the battlefield is a copy of a card or of a permanent: CR 400.7 gives a spell
    /// that resolves a new object and the zone system does not carry the stack entry onto it, so
    /// a resolved copy of a permanent spell arrives as the token CR 707.10a makes it and 704.5d
    /// is the arm that then reads it.</item>
    /// </list>
    /// A copy of a card is marked the only way the state marks any copy, <c>StackEntry.IsCopy</c>
    /// — what CR 707.12's copy-in-place sets on the copy it makes in the original's zone. If a
    /// zone system marks it some other way, this is the reader that has to learn about it
    /// (seam-requests/E5f.md).
    /// </summary>
    [RuleRef("704.5e")]
    private static bool Ceases(GameObject obj, Zone zone) =>
        obj.IsToken
            ? zone != Zone.Battlefield
            : (obj.Stack is { IsCopy: true } || obj.Designations.Contains(Zones.ZoneSystem.CopyOfCard)) && zone is not (Zone.Stack or Zone.Battlefield);

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

            // 704.5h A creature dealt damage by a source with deathtouch since the last check is
            // destroyed, whatever the amount; 702.12b indestructible ignores this too.
            if (obj.DealtDeathtouchDamage && obj.Damage > 0 && !chars.Has(KeywordKind.Indestructible))
                return new Verdict(id, Action.Graveyard, Destroyed);
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

    /// <summary>
    /// CR 205.3: does this object have that subtype? An object that is every creature type
    /// (CR 205.3d, what changeling grants under CR 702.72a) has every creature type of CR 205.3m
    /// without any of them appearing in its list, so the flag is read beside the list here as it
    /// is everywhere else. It grants only creature types: a changeling is not thereby an Aura,
    /// an Equipment or a Fortification, which is what keeps 704.5m and 704.5n asking the right
    /// question of it.
    /// </summary>
    [RuleRef("205.3d")]
    private static bool Has(Characteristics chars, string subtype) =>
        chars.Subtypes.Any(s => string.Equals(s, subtype, StringComparison.OrdinalIgnoreCase))
        || (chars.EveryCreatureType && CreatureTypes.IsCreatureType(subtype));

    /// <summary>
    /// Whether an Aura is attached to something it may legally enchant, CR 702.5b. An Aura is
    /// attached to an object <em>or a player</em> (CR 303.4f), so the question is asked of
    /// whichever half it holds; attached to neither, it is illegal and 704.5m buries it.
    /// </summary>
    [RuleRef("704.5m")]
    private bool AttachedLegally(GameState state, GameObject aura, Characteristics chars)
    {
        // "Enchant [object or player]" is what legal means for an Aura. With no enchant ability
        // there is nothing to be illegal about, so the attachment stands either way.
        var enchant = chars.Abilities.OfType<KeywordAbility>().FirstOrDefault(k => k.Keyword == KeywordKind.Enchant);

        // CR 303.4f with CR 800.4: a player who has left the game is not a player any more, so
        // the Aura on them is attached to nothing and 704.5m buries it on the next check.
        if (aura.AttachedToPlayer is { } player)
            return InGame(state, player) && EnchantsAPlayer(state, enchant, player, chars);

        if (aura.AttachedTo is not { } host)
            return false;
        if (!state.Objects.TryGetValue(host, out var target) || target.Zone.Zone != Zone.Battlefield)
            return false;

        return enchant?.Of is not { } filter || query.Matches(state, filter, host, ContextOf(chars));
    }

    /// <summary>
    /// Whether this Aura's enchant ability admits <paramref name="player"/>, CR 702.5b. E5c's
    /// request landed, so there are now three cases rather than one:
    /// <list type="bullet">
    /// <item><c>OfPlayer</c> named — "enchant player", "enchant opponent" — is asked of the
    /// player, from the Aura's controller's point of view; a player who fails it is an illegal
    /// attachment and 704.5m buries the Aura.</item>
    /// <item>Neither half named is "enchant anything", so a player is admitted. That is E5c's
    /// reading, kept because a bare <c>Enchant</c> is still what a translation writes when it
    /// drops a filter, and the linter's pair rule is what will catch that, not this.</item>
    /// <item><c>Of</c> only is a restriction to objects and refuses every player.</item>
    /// </list>
    /// An Aura with no enchant ability at all has nothing to be illegal about, so it stays.
    /// </summary>
    [RuleRef("702.5b")]
    private bool EnchantsAPlayer(GameState state, KeywordAbility? enchant, PlayerId player, Characteristics chars)
    {
        // CR 702.16d with 702.16j: a player with protection from everything can't be enchanted by
        // any Aura, their own included — protection from everything is protection from each
        // object (702.16b), so no Aura is a legal one and 704.5m buries every one attached here.
        // This is read before the enchant ability because it holds even where there is none.
        if (LimitsOn(state, player).ProtectionFromEverything)
            return false;

        if (enchant is null)
            return true;

        // CR 702.5b through the query, not through a copy of it. This rule had a private
        // PlayerMatches until wave 10; E5d's seam request named it as one of two copies that
        // disagreed about `Also`, and the seam that settles it is IQuery.PlayerMatches, which
        // asks the condition from the candidate's point of view — this arm's reading, kept.
        return enchant.OfPlayer is { } who
            ? query.PlayerMatches(state, who, player, ContextOf(chars))
            : enchant.Of is null;
    }

    /// <summary>CR 800.4a: a player who has left the game is no longer a player.</summary>
    [RuleRef("800.4a")]
    private static bool InGame(GameState state, PlayerId player) =>
        state.Players.FirstOrDefault(p => p.Id == player) is { HasLost: false };

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
