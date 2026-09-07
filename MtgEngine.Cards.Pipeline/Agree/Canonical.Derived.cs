using System.Reflection;
using System.Text.Json.Nodes;

namespace MtgEngine.Cards.Pipeline.Agree;

/// <summary>
/// The second canonical pass, from the re-scored pilot: after the first pass 72 of 200 readings
/// agreed, and the first differences that remained were not two readings of a card but two
/// spellings of one reading. These are the rules that need more than the node in front of them —
/// a value derived from a sibling (<c>selfOnly</c> from the event replaced), a value filled in
/// rather than struck out (<c>Basic</c> implies <c>Land</c>), a rewrite that spans two steps of a
/// sequence (a search that shuffles), and a default that is only a default in one place
/// (<c>ownedBy</c> inside a search of your own library).
///
/// Nothing here decides a disagreement. Where the vocabulary genuinely has two meanings — a
/// Search's <c>upTo</c>, "any target", <c>anyColor</c> — both readings are left as written and the
/// card stays disputed, which is the honest outcome until the vocabulary gains the record.
/// </summary>
public static partial class Canonical
{
    /// <summary>The rules that read more than the node: run after the strips, before the collapses.</summary>
    private static void Derive(JsonObject obj, string? kind, Context context)
    {
        StripOwnedByInOwnLibrarySearch(obj, kind, context);
        StripCountOfOne(obj, kind);
        FillLandOnBasic(obj, kind);
        DeriveSelfOnly(obj, kind);
        MergeSearchAndShuffle(obj, kind);
    }

    /// <summary>
    /// CR 701.23a: a search of a library searches the searcher's own, so "a creature card you own"
    /// in a search of your library says nothing the search does not already say. Anywhere else an
    /// owner is a real constraint — whose graveyard, whose exile — so it stays.
    /// </summary>
    private static void StripOwnedByInOwnLibrarySearch(JsonObject obj, string? kind, Context context)
    {
        if (kind is null && context.InOwnLibrarySearch && IsYou(obj["ownedBy"]))
            obj.Remove("ownedBy");
    }

    /// <summary>True when a Search reads the searcher's own library, which is the only zone that is theirs by the record.</summary>
    private static bool SearchesOwnLibrary(JsonObject search)
    {
        if (search["from"] is JsonValue from
            && (!from.TryGetValue<string>(out var zone) || !string.Equals(zone, nameof(Zone.Library), StringComparison.Ordinal)))
        {
            return false;
        }

        // Who is stripped to absent later in this same pass; both spellings mean the controller.
        return search["who"] is null || IsYou(search["who"]);
    }

    /// <summary>
    /// An <see cref="Amount"/> of Fixed(1) where leaving it out already means one. "Discard a card",
    /// "sacrifice a creature", "target creature" — the record's own default is null and every reader
    /// of it reads null as one, so the two spellings are one instruction.
    /// </summary>
    private static void StripCountOfOne(JsonObject obj, string? kind)
    {
        if (kind is null || !OneDefaults.Value.TryGetValue(kind, out var properties))
            return;

        // "Discard your hand" ignores the count; a written one there is not the same as none.
        if (obj["wholeHand"] is JsonValue whole && whole.TryGetValue<bool>(out var all) && all)
            return;

        foreach (var name in properties)
        {
            if (IsOne(obj[name]))
                obj.Remove(name);
        }
    }

    /// <summary>
    /// CR 205.4c: Basic is a supertype only lands have, so "basic land card" and a filter naming
    /// only Basic are the same filter. Filled in rather than stripped, so the engine's type check
    /// sees the Land it would otherwise have to infer.
    /// </summary>
    private static void FillLandOnBasic(JsonObject obj, string? kind)
    {
        if (kind is not null || obj["types"] is not null || obj["supertypes"] is not JsonArray supertypes)
            return;

        var basic = supertypes.Any(s => s is JsonValue v
            && v.TryGetValue<string>(out var name)
            && string.Equals(name, nameof(Supertype.Basic), StringComparison.Ordinal));
        if (basic)
            obj["types"] = new JsonArray(JsonValue.Create(nameof(CardType.Land)));
    }

    /// <summary>
    /// CR 614.15 and the replacement system's reading of it: <c>SelfOnly</c> means the ability
    /// replaces only an event about its own source. When the replaced event already names this
    /// permanent — its subject filter is <see cref="IsThisObject"/>, or it is an unconstrained
    /// <see cref="WouldEnterBattlefield"/>, which on a permanent is that permanent entering
    /// (CR 614.1c) — the flag is derived, not a reading. Elsewhere it is a real disagreement and
    /// stays as written.
    /// </summary>
    private static void DeriveSelfOnly(JsonObject obj, string? kind)
    {
        if (!string.Equals(kind, nameof(ReplacementAbility), StringComparison.Ordinal))
            return;
        if (obj["instead"] is JsonObject instead && NamesThisObject(instead))
            obj["selfOnly"] = true;
    }

    private static bool NamesThisObject(JsonObject instead)
    {
        var kind = Kind(instead);
        if (kind is null || !SubjectProperties.Value.TryGetValue(kind, out var property))
            return false;

        var subject = instead[property] as JsonObject;
        if (subject is not null && subject["also"] is JsonObject also && Kind(also) == nameof(IsThisObject))
            return true;

        return string.Equals(kind, nameof(WouldEnterBattlefield), StringComparison.Ordinal)
            && (subject is null || subject.Count == 0);
    }

    /// <summary>
    /// "Search your library for a land, then shuffle" has two spellings: a Search that shuffles, and
    /// a Search that does not followed by a Shuffle of the same player's library. CR 701.23b makes
    /// the shuffle part of the search, so that is the spelling; a Sequence left with one step then
    /// collapses to it.
    /// </summary>
    private static void MergeSearchAndShuffle(JsonObject obj, string? kind)
    {
        if (!string.Equals(kind, nameof(Sequence), StringComparison.Ordinal) || obj["steps"] is not JsonArray steps)
            return;

        for (var i = steps.Count - 2; i >= 0; i--)
        {
            if (steps[i] is not JsonObject search || Kind(search) != nameof(Search) || !SearchesOwnLibrary(search))
                continue;
            if (search["shuffleAfter"] is not JsonValue flag || !flag.TryGetValue<bool>(out var after) || after)
                continue;
            if (steps[i + 1] is not JsonObject shuffle || Kind(shuffle) != nameof(Shuffle) || shuffle["intoLibrary"] is not null)
                continue;
            if (!SamePlayer(search["who"], shuffle["who"]))
                continue;

            search["shuffleAfter"] = true;
            steps.RemoveAt(i + 1);
        }
    }

    private static bool SamePlayer(JsonNode? left, JsonNode? right) =>
        (left is null || IsYou(left)) && (right is null || IsYou(right));

    private static bool IsOne(JsonNode? node) =>
        node is JsonObject amount
        && amount.Count == 2
        && Kind(amount) == nameof(Fixed)
        && amount["n"] is JsonValue value
        && value.TryGetValue<int>(out var n)
        && n == 1;

    private static readonly Lazy<Dictionary<string, string>> SubjectProperties = new(BuildSubjectProperties);

    private static readonly Lazy<Dictionary<string, string[]>> OneDefaults = new(BuildOneDefaults);

    /// <summary>
    /// The property of each replaced event that names the object the event is about: the first
    /// filter its record takes. Read by reflection so a new event is covered the day it is added.
    /// </summary>
    private static Dictionary<string, string> BuildSubjectProperties()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (type, name) in CardFileSerializer.RegisteredNames(typeof(ReplacedEvent)))
        {
            var subject = LongestConstructor(type)?.GetParameters().FirstOrDefault(p => p.ParameterType == typeof(ObjectFilter));
            if (subject?.Name is { } parameter)
                map[name] = Camel(parameter);
        }

        return map;
    }

    /// <summary>
    /// Every record with an optional Amount called N or Count whose default is null: in each of
    /// them the record's own default says one, so a written Fixed(1) is the same instruction.
    /// A required Amount is deliberately not here — Draw.N and Mill.N have no default, absent
    /// there does not mean one, and striking it out would write a definition the engine cannot read.
    /// </summary>
    private static Dictionary<string, string[]> BuildOneDefaults()
    {
        var map = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var baseType in CardFileSerializer.PolymorphicBases)
        {
            foreach (var (type, name) in CardFileSerializer.RegisteredNames(baseType))
            {
                var properties = LongestConstructor(type)?.GetParameters().Where(IsOptionalCount).Select(p => Camel(p.Name!)).ToArray();
                if (properties is { Length: > 0 })
                    map[name] = properties;
            }
        }

        return map;
    }

    private static bool IsOptionalCount(ParameterInfo p) =>
        p.ParameterType == typeof(Amount)
        && (string.Equals(p.Name, "N", StringComparison.Ordinal) || string.Equals(p.Name, "Count", StringComparison.Ordinal))
        && p.HasDefaultValue
        && p.DefaultValue is null;

    /// <summary>
    /// The records this pass strips a Fixed(1) from, and the property of each: derived, not listed,
    /// so the set is readable rather than asserted from memory. Public because the coverage of a
    /// reflection-derived rule is a thing to test and a thing to print.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> CountsThatMeanOne =>
        OneDefaults.Value.ToDictionary(e => e.Key, e => (IReadOnlyList<string>)e.Value, StringComparer.Ordinal);

    private static ConstructorInfo? LongestConstructor(Type type) =>
        type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];
}
