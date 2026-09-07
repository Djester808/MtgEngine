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
/// Search's <c>upTo</c>, <c>anyColor</c> — both readings are left as written and the card stays
/// disputed, which is the honest outcome until the vocabulary gains the record. Two of them since
/// have: "any target" is <see cref="AnyTarget"/> and "can't lose the game" is
/// <see cref="CantLoseGame"/>, so the readings that spelled either the long way collapse here.
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
        CollapseAnyTarget(obj, kind);
        CollapsePlayerCant(obj, kind);
    }

    /// <summary>
    /// CR 115.4: "any target" is one target chosen from among creatures, players, planeswalkers and
    /// battles, and since V4 the vocabulary has one selector that says so. Before it, a reading had
    /// to spell the phrase across two fields that do not know about each other, and the two ways of
    /// doing that were the last first difference in the pilot that canonicalisation was forbidden to
    /// close: a Target naming the three permanent types beside a TargetPlayer of anyone, or — from
    /// the wording used before battles existed (CR 115.4 as it read pre-March of the Machine) — a
    /// Target naming just creature and planeswalker beside the same TargetPlayer. Both are that
    /// phrase and nothing else, so both become AnyTarget and the player half goes, because AnyTarget
    /// already carries it.
    ///
    /// What is deliberately not collapsed: a Target with an open filter beside a TargetPlayer, which
    /// would let the damage hit a land and is a different instruction; a Target over only creatures,
    /// or only a player, with no other half; and a TargetPlayer that is restricted, counted or up to,
    /// none of which "any target" can say.
    /// </summary>
    private static void CollapseAnyTarget(JsonObject obj, string? kind)
    {
        if (!string.Equals(kind, nameof(DealDamage), StringComparison.Ordinal) || !IsAnyPlayerTarget(obj["toPlayer"]))
            return;

        if (obj["to"] is JsonObject already && Kind(already) == nameof(AnyTarget))
        {
            // Already the one spelling: the player half repeats what AnyTarget means.
            obj.Remove("toPlayer");
            return;
        }

        if (obj["to"] is not JsonObject target || Kind(target) != nameof(Target) || !IsAnyTargetFilter(target))
            return;

        var any = new JsonObject { ["kind"] = nameof(AnyTarget) };
        foreach (var name in (string[])["count", "upTo", "key"])
        {
            if (target[name] is { } value)
                any[name] = value.DeepClone();
        }

        obj["to"] = any;
        obj.Remove("toPlayer");
    }

    /// <summary>The filter of a Target that describes exactly the permanents "any target" reaches.</summary>
    private static bool IsAnyTargetFilter(JsonObject target)
    {
        // Distinct false says the same object may be chosen twice; AnyTarget has nowhere to keep that.
        if (target["distinct"] is JsonValue distinct && distinct.TryGetValue<bool>(out var separate) && !separate)
            return false;
        if (target["filter"] is not JsonObject filter || filter.Count != 1 || filter["anyTypes"] is not JsonArray types)
            return false;

        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in types)
        {
            if (type is not JsonValue value || !value.TryGetValue<string>(out var name))
                return false;
            named.Add(name);
        }

        return named.SetEquals(AnyTargetTypes) || named.SetEquals(AnyTargetTypesBeforeBattles);
    }

    /// <summary>An unrestricted, uncounted player target: the "player" half of "any target" and nothing more.</summary>
    private static bool IsAnyPlayerTarget(JsonNode? node)
    {
        if (node is not JsonObject player || Kind(player) != nameof(TargetPlayer))
            return false;
        if (player["count"] is not null || player["key"] is not null)
            return false;
        if (player["upTo"] is JsonValue upTo && upTo.TryGetValue<bool>(out var atMost) && atMost)
            return false;
        return player["filter"] is null || (player["filter"] is JsonObject filter && filter.Count == 0);
    }

    private static readonly string[] AnyTargetTypes =
        [nameof(CardType.Creature), nameof(CardType.Planeswalker), nameof(CardType.Battle)];

    /// <summary>The same phrase as it was printed before battles were a permanent type.</summary>
    private static readonly string[] AnyTargetTypesBeforeBattles =
        [nameof(CardType.Creature), nameof(CardType.Planeswalker)];

    /// <summary>
    /// CR 104.3a: "can't lose the game" has a record of its own, <see cref="CantLoseGame"/>, and so
    /// do "can't win the game" (CR 104.2a) and "can't gain life" (CR 119.5). <see cref="PlayerCant"/>
    /// is the general form for the restrictions those three do not cover. A PlayerCant carrying
    /// exactly one restriction that has a record of its own is that record; a PlayerCant carrying
    /// several flags, or one flag with no record of its own, stays exactly as written, because no
    /// single record says it.
    ///
    /// Today <see cref="PlayerRestriction"/> has one such flag, Lose. LifeTotalChanges is not
    /// CantGainLife — it also forbids losing life and setting a life total (CR 119.1) — and the enum
    /// has no Win or GainLife member, so those two records have nothing to collapse from. The map is
    /// the place a flag joins them if one is ever added.
    ///
    /// The player comes from the ability doing the applying, a StaticAbility's Players or an
    /// ApplyToPlayer's Who, because the specific records name a player and PlayerCant does not.
    /// Absent means you, the controller of the ability (CR 109.5), as it does everywhere else here.
    /// The parent's own field is left as written: it is what tells the engine the effect is about
    /// players at all.
    /// </summary>
    private static void CollapsePlayerCant(JsonObject obj, string? kind)
    {
        var property = kind switch
        {
            nameof(StaticAbility) => "effect",
            nameof(ApplyToPlayer) => "what",
            _ => null,
        };

        if (property is null || obj[property] is not JsonObject effect || Kind(effect) != nameof(PlayerCant))
            return;
        if (effect["what"] is not JsonArray flags || flags.Count != 1)
            return;
        if (flags[0] is not JsonValue flag || !flag.TryGetValue<string>(out var restriction))
            return;
        if (!SpecificPlayerRestrictions.TryGetValue(restriction, out var record))
            return;

        var who = (string.Equals(kind, nameof(StaticAbility), StringComparison.Ordinal) ? obj["players"] : obj["who"])?.DeepClone()
            ?? new JsonObject { ["kind"] = nameof(You) };
        obj[property] = new JsonObject { ["kind"] = record, ["who"] = who };
    }

    /// <summary>Each restriction that a record of its own already says, and that record's name.</summary>
    private static readonly Dictionary<string, string> SpecificPlayerRestrictions = new(StringComparer.Ordinal)
    {
        [nameof(PlayerRestriction.Lose)] = nameof(CantLoseGame),
    };

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
