using System.Numerics;
using MtgEngine.Cards;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Characteristics;

/// <summary>
/// The half of the layer system that reads the board: the continuous effects of static
/// abilities (CR 604.2), which objects each one applies to (CR 611.3a), and the small filter
/// matcher that answers that question. The matcher is deliberately its own: the query layer is
/// built on this one, so taking it here would be a cycle.
/// </summary>
public sealed partial class LayerSystem
{
    /// <summary>
    /// Every continuous effect a permanent's static abilities generate right now, CR 604.2.
    /// Recomputed on every call because a static ability's effect is not locked in: it applies
    /// at any given moment to whatever its text indicates, CR 611.3a.
    /// </summary>
    [RuleRef("604.2")]
    private ImmutableList<StaticEffect> Statics(GameState state)
    {
        var gathered = ImmutableList.CreateBuilder<StaticEffect>();
        foreach (var source in state.Objects.Values.Where(o => o.Zone.Zone == Zone.Battlefield))
        {
            // CR 708.2a: a face-down permanent has no text, so it has no static abilities.
            if (source.FaceDown)
                continue;

            var controller = ControllerFrom(source, FromResolutions(state, source.Id));
            foreach (var ability in source.Face.AbilityList.OfType<StaticAbility>())
            {
                // CR 611.3b: the effect applies while its source is in the zone the ability works in.
                if (ability.Where != Zone.Battlefield)
                    continue;

                // A conditional static ("as long as you control an artifact") needs
                // IQuery.Holds, which does not exist yet; see seam-requests/E7.md.
                if (ability.While is not null)
                    continue;

                // CR 613.10: an effect that applies to players rather than objects is applied
                // after characteristics are determined, and is not this layer's business.
                if (ability.To is null)
                    continue;

                var appliesTo = ObjectsFor(state, source, controller, ability.To);
                if (appliesTo.IsEmpty)
                    continue;

                // CR 613.7a: the effect's timestamp is the timestamp of the object the ability is on.
                gathered.Add(new StaticEffect(
                    new Layered(ability.Effect, source.Timestamp, ability.CharacteristicDefining, source.Id, controller),
                    appliesTo));
            }
        }

        return gathered.ToImmutable();
    }

    /// <summary>The effects a resolving spell or ability left on this object, layer 2's input for a source.</summary>
    private static ImmutableList<Layered> FromResolutions(GameState state, ObjectId id) =>
    [
        .. state.ActiveEffects
            .Where(e => e.AppliesTo.Contains(id))
            .OrderBy(e => e.Timestamp)
            .Select(e => new Layered(e.Effect, e.Timestamp, false, e.Source, e.Controller)),
    ];

    /// <summary>
    /// Which objects a static ability's selector names. Only the three shapes a static ability
    /// uses are answered here: itself, what it is attached to, and each object matching a
    /// filter. Anything else is the interpreter's to resolve and pre-record.
    /// </summary>
    private ImmutableList<ObjectId> ObjectsFor(GameState state, GameObject source, PlayerId controller, Selector selector) => selector switch
    {
        Self => [source.Id],
        Attached => source.AttachedTo is { } attached && state.Objects.ContainsKey(attached) ? [attached] : [],
        Each each =>
        [
            .. state.Objects.Values
                .Where(candidate => Matches(state, each.Filter, candidate, source, controller))
                .Select(candidate => candidate.Id)
                .OrderBy(id => id.Value),
        ],
        _ => throw new NotImplementedException(
            $"A continuous effect selected its objects with {selector.GetType().Name}; the layer system resolves Self, Attached and Each. See seam-requests/E7.md."),
    };

    /// <summary>
    /// Does this object match the filter? Matched against characteristics computed without the
    /// static abilities of the board, so that gathering them cannot recurse into itself. An
    /// effect whose filter reads a characteristic another static ability changes needs the
    /// dependency rules of CR 613.8, which are not built.
    /// </summary>
    private bool Matches(GameState state, ObjectFilter filter, GameObject candidate, GameObject source, PlayerId controller)
    {
        Unsupported(filter);
        if (candidate.Zone.Zone != (filter.In ?? Zone.Battlefield))
            return false;
        if (filter.Other == true && candidate.Id == source.Id)
            return false;
        if (filter.Other == false && candidate.Id != source.Id)
            return false;
        if (filter.Token is { } token && candidate.IsToken != token)
            return false;
        if (filter.Tapped is { } tapped && candidate.Tapped != tapped)
            return false;
        if (filter.FaceDown is { } faceDown && candidate.FaceDown != faceDown)
            return false;
        if (filter.WithAnyCounter is { } any && candidate.Counters.Any(c => c.Value > 0) != any)
            return false;
        if (filter.WithCounter is { } named && !(candidate.Counters.TryGetValue(named, out var count) && count > 0))
            return false;
        if (filter.OwnedBy is { } owner && !IsPlayer(state, owner, candidate.Owner, controller))
            return false;

        var chars = Compute(state, candidate.Id, withStatics: false);
        if (filter.ControlledBy is { } controlledBy && !IsPlayer(state, controlledBy, chars.Controller, controller))
            return false;
        if (filter.Types is { } types && (chars.Types & types) != types)
            return false;
        if (filter.AnyTypes is { } anyTypes && (chars.Types & anyTypes) == CardType.None)
            return false;
        if (filter.NotTypes is { } notTypes && (chars.Types & notTypes) != CardType.None)
            return false;
        if (filter.Permanent == true && (chars.Types & PermanentTypes) == CardType.None)
            return false;
        if (filter.Supertypes is { } supertypes && (chars.Supertypes & supertypes) != supertypes)
            return false;
        if (filter.NotSupertypes is { } notSupertypes && (chars.Supertypes & notSupertypes) != Supertype.None)
            return false;
        if (filter.Subtypes is { } subtypes && !subtypes.All(s => chars.Subtypes.Contains(s, StringComparer.OrdinalIgnoreCase)))
            return false;
        if (filter.NotSubtypes is { } notSubtypes && notSubtypes.Any(s => chars.Subtypes.Contains(s, StringComparer.OrdinalIgnoreCase)))
            return false;
        if (filter.Colors is { } colors && (chars.Colors & colors) != colors)
            return false;
        if (filter.AnyColor is { } anyColor && (chars.Colors & anyColor) == Color.Colorless)
            return false;
        if (filter.NotColors is { } notColors && (chars.Colors & notColors) != Color.Colorless)
            return false;
        if (filter.Colorless is { } colorless && (chars.Colors == Color.Colorless) != colorless)
            return false;
        if (filter.Multicolored is { } multi && (BitOperations.PopCount((uint)chars.Colors) > 1) != multi)
            return false;
        if (filter.Monocolored is { } mono && (BitOperations.PopCount((uint)chars.Colors) == 1) != mono)
            return false;
        if (filter.Named is { } name && !string.Equals(chars.Name, name, StringComparison.Ordinal))
            return false;
        if (filter.Has is { } keyword && !chars.Has(keyword))
            return false;
        if (filter.Power is { } power && !Compare(chars.Power, power))
            return false;
        if (filter.Toughness is { } toughness && !Compare(chars.Toughness, toughness))
            return false;
        if (filter.Loyalty is { } loyalty && !Compare(chars.Loyalty, loyalty))
            return false;

        return true;
    }

    /// <summary>The parts of a filter this matcher cannot answer without the query layer, named rather than ignored.</summary>
    private static void Unsupported(ObjectFilter filter)
    {
        string? missing =
            filter.Also is not null ? nameof(filter.Also)
            : filter.ManaValue is not null ? nameof(filter.ManaValue)
            : filter.Attacking is not null ? nameof(filter.Attacking)
            : filter.Blocking is not null ? nameof(filter.Blocking)
            : filter.Blocked is not null ? nameof(filter.Blocked)
            : filter.Historic is not null ? nameof(filter.Historic)
            : filter.Outlaw is not null ? nameof(filter.Outlaw)
            : filter.Spell is not null ? nameof(filter.Spell)
            : filter.IsCommander is not null ? nameof(filter.IsCommander)
            : null;

        if (missing is not null)
        {
            throw new NotImplementedException(
                $"A static ability's filter used {missing}, which the layer system's matcher does not answer; see seam-requests/E7.md.");
        }
    }

    /// <summary>"You" is the controller of the ability whose effect this is, CR 109.5.</summary>
    [RuleRef("109.5")]
    private static bool IsPlayer(GameState state, PlayerRef who, PlayerId candidate, PlayerId controller) => who switch
    {
        You => candidate == controller,
        EachOpponent => candidate != controller,
        EachPlayer => true,
        ActivePlayer => candidate == state.Turn.ActivePlayer,
        _ => throw new NotImplementedException(
            $"A static ability's filter named the player {who.GetType().Name}, which needs the query layer; see seam-requests/E7.md."),
    };

    private static bool Compare(int? value, Comparison comparison)
    {
        if (value is not { } actual)
            return false;

        var against = Value(comparison.Value, "a filter's comparison");
        return comparison.Op switch
        {
            ComparisonOp.Eq => actual == against,
            ComparisonOp.Ne => actual != against,
            ComparisonOp.Lt => actual < against,
            ComparisonOp.Le => actual <= against,
            ComparisonOp.Gt => actual > against,
            ComparisonOp.Ge => actual >= against,
            _ => throw new NotImplementedException($"The comparison {comparison.Op} is not implemented."),
        };
    }

    /// <summary>The permanent types, CR 110.4a.</summary>
    [RuleRef("110.4a")]
    private const CardType PermanentTypes =
        CardType.Artifact | CardType.Battle | CardType.Creature | CardType.Enchantment | CardType.Land | CardType.Planeswalker;

    /// <summary>A static ability's effect and the objects it applies to at this moment.</summary>
    private sealed record StaticEffect(Layered Effect, ImmutableList<ObjectId> AppliesTo);
}

/// <summary>
/// The keyword a keyword counter grants, CR 122.1a. The counter names in
/// <see cref="Counters.KeywordCounters"/> are the printed spellings; the enum is generated from
/// the rulebook, so the two are matched by letters alone rather than by a second table.
/// </summary>
[RuleRef("122.1a")]
internal static class Keywords
{
    private static readonly Dictionary<string, KeywordKind> _byName = Enum.GetValues<KeywordKind>()
        .ToDictionary(k => Letters(k.ToString()), k => k, StringComparer.Ordinal);

    public static KeywordKind? Find(string counter) =>
        _byName.TryGetValue(Letters(counter), out var keyword) ? keyword : null;

    private static string Letters(string text) =>
        string.Concat(text.Where(char.IsLetter)).ToLowerInvariant();
}
