using System.Reflection;
using System.Text.Json.Nodes;

namespace MtgEngine.Cards.Pipeline.Agree;

/// <summary>
/// The third canonical pass, from the subscription re-translation: of the 46 first differences it
/// left, five families are one meaning written two ways, and each of those families is a rule here.
/// The families are counted in the collect report, so the rules are chosen from what two models
/// actually disagreed about rather than from what might happen.
///
/// As in the two passes before it, nothing here decides a disagreement. Where the two readings say
/// different things about the card — a Search's <c>shuffleOnlyIfFound</c> true against false, a
/// filter's <c>permanent</c> true against absent, Compare against Controls — both are left exactly
/// as written and the card stays disputed, which is the honest outcome.
/// </summary>
public static partial class Canonical
{
    /// <summary>The re-translation's rules: run from <see cref="Derive"/>, after the strips.</summary>
    private static void Retranslated(JsonObject obj, string? kind)
    {
        StripWrittenDefaults(obj, kind);
        StripLandImpliedByLandSubtype(obj, kind);
        StripYourSpellsOnYourCostModifier(obj, kind);
        StripSpellFlagOnSpellCast(obj, kind);
        FoldTriggeringAmongToSelf(obj, kind);
    }

    /// <summary>
    /// A property written out with exactly the value its record already defaults to. Absent and
    /// written-out are the same instruction by construction: the file is read back through the
    /// record's constructor, so an omitted parameter <em>is</em> its default, and striking the
    /// written one out cannot change what the engine executes.
    ///
    /// This is the general form of the family the re-translation's report calls
    /// <c>shuffleOnlyIfFound: false</c>, and of the <c>role: "Subject"</c> and <c>from: "Library"</c>
    /// noise underneath several other first differences. Only non-nullable parameters count: a
    /// <c>bool?</c> on an <see cref="ObjectFilter"/> defaults to null and a written false there says
    /// "not tapped", which is a constraint and not a default — that is why the report's
    /// <c>permanent: true vs absent</c> is still a difference after this pass.
    /// </summary>
    private static void StripWrittenDefaults(JsonObject obj, string? kind)
    {
        if (kind is null || !WrittenDefaults.Value.TryGetValue(kind, out var defaults))
            return;

        foreach (var (name, value) in defaults)
        {
            if (obj[name] is JsonValue written && JsonNode.DeepEquals(written, value))
                obj.Remove(name);
        }
    }

    /// <summary>
    /// CR 205.3i: the basic land types are land types, and only a land can have one. A filter that
    /// names Forest or Island among its subtypes therefore says Land already, and the reading that
    /// also writes <c>types: ["Land"]</c> says nothing more than the one that does not. The land
    /// types come from the generated rules data (<see cref="LandTypes.Basic"/>), never from a list
    /// in C#, so a rules release that changes them changes this rule with it.
    ///
    /// Only a filter whose whole type line is Land is touched, and only when every subtype it names
    /// is a basic land type: "artifact land" and "Forest Dryad" both mean more than their subtypes,
    /// and a nonbasic land type (Gate, Locus) is not rules data we hold, so those stay as written.
    /// Runs before <see cref="FillLandOnBasic"/>, so "basic Forest card" still ends with the Land
    /// that rule fills in and the two passes cannot fight over one filter.
    /// </summary>
    private static void StripLandImpliedByLandSubtype(JsonObject obj, string? kind)
    {
        if (kind is not null || obj["types"] is not JsonArray types || obj["subtypes"] is not JsonArray subtypes)
            return;
        if (types.Count != 1 || subtypes.Count == 0)
            return;
        if (types[0] is not JsonValue only || !only.TryGetValue<string>(out var type)
            || !string.Equals(type, nameof(CardType.Land), StringComparison.Ordinal))
        {
            return;
        }

        foreach (var subtype in subtypes)
        {
            if (subtype is not JsonValue value || !value.TryGetValue<string>(out var name) || !LandTypes.IsBasicLandType(name))
                return;
        }

        obj.Remove("types");
    }

    /// <summary>
    /// CR 601.2f and the reading of "spells you cast cost {1} less": a <see cref="CostModifier"/>
    /// already names whose costs it changes in its own <c>who</c>, and the spells it applies to are
    /// that player's spells. A <c>controlledBy</c> of You on the filter of a modifier whose
    /// <c>who</c> is You repeats the record's own field, and the seven cards in the report's
    /// <c>controlledBy</c> family are exactly that repetition.
    ///
    /// A modifier for someone else's spells is left alone in both halves: "spells your opponents
    /// cast cost {1} more" has an opponent in <c>who</c>, and a filter naming a different player
    /// from the one paying is a real constraint the record cannot carry twice.
    /// </summary>
    private static void StripYourSpellsOnYourCostModifier(JsonObject obj, string? kind)
    {
        if (!string.Equals(kind, nameof(CostModifier), StringComparison.Ordinal))
            return;
        if (obj["who"] is not null && !IsYou(obj["who"]))
            return;
        if (obj["spells"] is JsonObject spells && IsYou(spells["controlledBy"]))
            spells.Remove("controlledBy");
    }

    /// <summary>
    /// CR 608.1: a <see cref="SpellCast"/> trigger watches a spell being cast, so everything its
    /// filter can match is a spell. A written <c>spell: true</c> there is the trigger's own
    /// discriminator repeated inside its filter, and both readings of the report's <c>spell</c>
    /// family are that.
    ///
    /// The flag is stripped only on the trigger's own filter. Anywhere else — a filter compared
    /// against something on the stack inside an effect, a static ability's subject — it separates
    /// spells from permanents and is the whole point of the filter, so it stays.
    /// </summary>
    private static void StripSpellFlagOnSpellCast(JsonObject obj, string? kind)
    {
        if (!string.Equals(kind, nameof(SpellCast), StringComparison.Ordinal))
            return;
        if (obj["spell"] is JsonObject filter && filter["spell"] is JsonValue flag
            && flag.TryGetValue<bool>(out var isSpell) && isSpell)
        {
            filter.Remove("spell");
        }
    }

    /// <summary>
    /// CR 107.3 as the engine reads "the greatest power among creatures you control": inside
    /// <see cref="GreatestAmong"/> and <see cref="TotalAmong"/> the candidate being measured is
    /// bound as the source of the amount's own context (<c>Query.Amounts.Among</c>), so
    /// <see cref="Self"/> is what the inner amount reads. <see cref="Triggering"/> in that position
    /// is the same reading written with the wrong word: it resolves to the same object when the
    /// engine evaluates the candidate, and to nothing at all when the ability is not a trigger.
    /// So Self is the spelling and Triggering in the measured amount folds into it.
    ///
    /// Only the measured amount (<c>of</c>) is walked, never the <c>among</c> filter: a filter that
    /// compares candidates against the triggering object ("creatures that share a color with it")
    /// means the trigger's object there, and rewriting that would change the card.
    /// </summary>
    private static void FoldTriggeringAmongToSelf(JsonObject obj, string? kind)
    {
        if (kind is not (nameof(GreatestAmong) or nameof(TotalAmong)))
            return;

        if (IsPlainTriggering(obj["of"]))
            obj["of"] = new JsonObject { ["kind"] = nameof(Self) };
        else
            FoldTriggering(obj["of"]);
    }

    private static void FoldTriggering(JsonNode? node)
    {
        switch (node)
        {
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (IsPlainTriggering(array[i]))
                        array[i] = new JsonObject { ["kind"] = nameof(Self) };
                    else
                        FoldTriggering(array[i]);
                }

                break;
            case JsonObject obj:
                foreach (var name in obj.Select(p => p.Key).ToList())
                {
                    if (IsPlainTriggering(obj[name]))
                        obj[name] = new JsonObject { ["kind"] = nameof(Self) };
                    else
                        FoldTriggering(obj[name]);
                }

                break;
            default:
                break;
        }
    }

    /// <summary>
    /// The triggering object itself, with no role. A Triggering that names a role (the attacker, the
    /// blocked creature) points at a different object from the candidate and is left as written.
    /// The default role is stripped by <see cref="StripWrittenDefaults"/> before this runs.
    /// </summary>
    private static bool IsPlainTriggering(JsonNode? node) =>
        node is JsonObject obj && obj.Count == 1 && Kind(obj) == nameof(Triggering);

    private static readonly Lazy<Dictionary<string, Dictionary<string, JsonNode?>>> WrittenDefaults = new(BuildWrittenDefaults);

    /// <summary>
    /// Every registered record's parameters that have a default the file can also write out, and
    /// that default as it appears in JSON. Read by reflection rather than listed, so a record that
    /// gains a defaulted flag is covered the day it is added, and so this cannot drift from the
    /// constructors the serializer actually calls.
    /// </summary>
    private static Dictionary<string, Dictionary<string, JsonNode?>> BuildWrittenDefaults()
    {
        var map = new Dictionary<string, Dictionary<string, JsonNode?>>(StringComparer.Ordinal);
        foreach (var baseType in CardFileSerializer.PolymorphicBases)
        {
            foreach (var (type, name) in CardFileSerializer.RegisteredNames(baseType))
            {
                var defaults = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
                foreach (var p in LongestConstructor(type)?.GetParameters() ?? [])
                {
                    if (WrittenDefault(p) is { } value)
                        defaults[Camel(p.Name!)] = value;
                }

                if (defaults.Count > 0)
                    map[name] = defaults;
            }
        }

        return map;
    }

    /// <summary>
    /// The JSON a parameter's default would be written as, or null when writing it out is not the
    /// same as leaving it out. A nullable parameter is excluded on purpose: its default is null,
    /// which no reading writes, and a written value there is a constraint rather than a default.
    /// </summary>
    private static JsonNode? WrittenDefault(ParameterInfo p)
    {
        if (!p.HasDefaultValue || p.DefaultValue is null || Nullable.GetUnderlyingType(p.ParameterType) is not null)
            return null;

        var type = p.ParameterType;
        if (type == typeof(bool))
            return JsonValue.Create((bool)p.DefaultValue);
        if (type == typeof(int))
            return JsonValue.Create((int)p.DefaultValue);
        if (type.IsEnum)
            return Enum.GetName(type, p.DefaultValue) is { } written ? JsonValue.Create(written) : null;

        return null;
    }

    /// <summary>
    /// The records and properties this pass strikes a written-out default from, for the same reason
    /// <see cref="CountsThatMeanOne"/> is public: the coverage of a reflection-derived rule is a
    /// thing to test and a thing to print, not a thing to assert from memory.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> DefaultsThatNeedNoWriting =>
        WrittenDefaults.Value.ToDictionary(
            e => e.Key,
            e => (IReadOnlyList<string>)e.Value.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray(),
            StringComparer.Ordinal);
}
