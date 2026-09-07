using System.Globalization;
using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;
using Chars = MtgEngine.Rules.Engine.Characteristics;

namespace MtgEngine.Rules.Layers;

/// <summary>
/// The layer system, CR 613. Characteristics are computed on every call and never stored
/// (CR 613.5): the printed face is the start (CR 613.1), then the state's active effects and
/// the static abilities of permanents on the battlefield (CR 604.2) are applied layer by
/// layer, in timestamp order within a layer (CR 613.7).
/// </summary>
[RuleRef("613")]
public sealed partial class LayerSystem : ICharacteristics
{
    public string Name => "continuous effects and layers";

    public Chars Of(GameState state, ObjectId id) => Compute(state, id, withStatics: true);

    /// <summary>Layer 2 on its own, CR 613.1b: who controls this object now.</summary>
    [RuleRef("613.1b")]
    public PlayerId ControllerOf(GameState state, ObjectId id)
    {
        ArgumentNullException.ThrowIfNull(state);
        var obj = Find(state, id);
        return ControllerFrom(obj, Applicable(state, id, withStatics: true));
    }

    private Chars Compute(GameState state, ObjectId id, bool withStatics)
    {
        ArgumentNullException.ThrowIfNull(state);
        var obj = Find(state, id);
        return Layers(state, obj, Applicable(state, id, withStatics));
    }

    private Chars Layers(GameState state, GameObject obj, ImmutableList<Layered> effects)
    {
        var draft = Printed(state, obj);

        Layer1Copy(state, obj, draft, effects);
        draft.Controller = ControllerFrom(obj, effects);

        // Layer 3, CR 613.1c: text-changing effects are wave 3's; ChangeText is read by nothing here.
        Layer4Types(draft, effects);
        Layer5Colors(draft, effects);
        Layer6Abilities(obj, draft, effects);
        Layer7PowerAndToughness(obj, draft, effects);

        return Snapshot(draft);
    }

    private static Chars Snapshot(Draft draft) =>
        new(
            draft.Name,
            draft.Types,
            draft.Supertypes,
            [.. draft.Subtypes],
            draft.Colors,
            draft.Power,
            draft.Toughness,
            draft.Loyalty,
            draft.Defense,
            draft.ManaCost,
            [.. draft.Abilities],
            draft.Controller,
            draft.EveryCreatureType);

    private static GameObject Find(GameState state, ObjectId id) =>
        state.Objects.TryGetValue(id, out var obj)
            ? obj
            : throw new ArgumentOutOfRangeException(nameof(id), $"No object {id} in this game.");

    /// <summary>
    /// Every continuous effect that applies to this object, earliest timestamp first
    /// (CR 613.7). Effects from a resolving spell or ability were locked to their set of
    /// objects when they began (CR 611.2c), so <see cref="ActiveEffect.AppliesTo"/> is read as
    /// written; a static ability's set is recomputed here every call (CR 611.3a).
    /// </summary>
    [RuleRef("613.7")]
    private ImmutableList<Layered> Applicable(GameState state, ObjectId id, bool withStatics)
    {
        var fromEffects = state.ActiveEffects
            .Where(e => e.AppliesTo.Contains(id))
            .Select(e => new Layered(e.Effect, e.Timestamp, false, e.Source, e.Controller));
        var all = withStatics
            ? fromEffects.Concat(Statics(state).Where(l => l.AppliesTo.Contains(id)).Select(l => l.Effect))
            : fromEffects;
        return [.. all.OrderBy(l => l.Timestamp)];
    }

    /// <summary>The printed face, CR 613.1, and the face-down exception of layer 1b.</summary>
    [RuleRef("613.1")]
    private static Draft Printed(GameState state, GameObject obj)
    {
        // CR 712.8: which face is read depends on the zone and the layout, and CR 708.2a gives a
        // face-down permanent a face of its own; both are the face rules' answer.
        var face = obj.FaceDown
            ? Faces.FaceRules.FaceDown
            : obj.Definition.Faces[Faces.FaceRules.ActiveFace(obj.Definition, obj.Zone.Zone, obj.FaceIndex)];
        var draft = new Draft
        {
            Name = face.Name,
            Types = face.Types.Types,
            Supertypes = face.Types.Supertypes,
            Subtypes = [.. face.Types.SubtypeList],
            // CR 202.2: a card's color comes from its mana cost's symbols, or from a color indicator.
            Colors = face.ColorIndicator ?? ColorsOf(face.ManaCost),
            Power = face.Power?.Number,
            Toughness = face.Toughness?.Number,
            Loyalty = Number(face.Loyalty),
            Defense = face.Defense,
            ManaCost = face.ManaCost,
            Abilities = [.. face.AbilityList],
            Controller = obj.InitialController,
        };

        // Layer 1b, CR 613.2b and 708.2a: a face-down permanent with no listed characteristics
        // is a 2/2 creature with no name, no subtypes, no text and no mana cost.
        if (obj.FaceDown)
        {
            draft.Name = string.Empty;
            draft.Types = CardType.Creature;
            draft.Supertypes = Supertype.None;
            draft.Subtypes.Clear();
            draft.Colors = Color.Colorless;
            draft.Power = 2;
            draft.Toughness = 2;
            draft.Loyalty = null;
            draft.ManaCost = null;
            draft.Abilities.Clear();
        }

        return draft;
    }

    /// <summary>
    /// The objects whose copy layer is being resolved on this thread. A copy effect names what
    /// it copies with a filter, and reading that filter means reading characteristics, which
    /// can lead back to the object being copied. CR 613 has no such cycle in it; the second
    /// visit takes the object without its copy layer rather than recurring.
    /// </summary>
    [ThreadStatic]
    private static HashSet<ObjectId>? _copying;

    /// <summary>Layer 1a, CR 613.2a: a copy takes the copiable values of what it copies (CR 707.2).</summary>
    [RuleRef("613.2a")]
    private void Layer1Copy(GameState state, GameObject obj, Draft draft, ImmutableList<Layered> effects)
    {
        var copying = _copying ??= [];
        if (!copying.Add(obj.Id))
            return;

        try
        {
            ApplyCopies(state, obj, draft, effects);
        }
        finally
        {
            copying.Remove(obj.Id);
        }
    }

    private void ApplyCopies(GameState state, GameObject obj, Draft draft, ImmutableList<Layered> effects)
    {
        foreach (var layered in effects)
        {
            if (layered.Effect is not BecomesCopyOf copy)
                continue;

            var of = ObjectsFor(state, obj, layered.Controller, copy.Of);
            if (of.IsEmpty)
                continue;

            var source = Printed(state, Find(state, of[0]));
            draft.Name = source.Name;
            draft.Types = source.Types;
            draft.Supertypes = copy.IsLegendary ? source.Supertypes | Supertype.Legendary : source.Supertypes;
            draft.Subtypes = source.Subtypes;
            draft.Colors = source.Colors;
            draft.Power = source.Power;
            draft.Toughness = source.Toughness;
            draft.Loyalty = source.Loyalty;
            draft.Defense = source.Defense;
            draft.ManaCost = source.ManaCost;
            draft.Abilities = source.Abilities;

            // CR 707.2: the exceptions the copy effect names are applied on top of the copy.
            foreach (var except in copy.Except ?? [])
            {
                var one = ImmutableList.Create(new Layered(except, layered.Timestamp, false, layered.Source, layered.Controller));
                Layer4Types(draft, one);
                Layer5Colors(draft, one);
                Layer6Abilities(obj, draft, one);
            }
        }
    }

    /// <summary>Layer 2, CR 613.1b: control-changing effects, in timestamp order.</summary>
    [RuleRef("613.1b")]
    private static PlayerId ControllerFrom(GameObject obj, ImmutableList<Layered> effects)
    {
        var controller = obj.InitialController;
        foreach (var layered in effects)
        {
            if (layered.Effect is not ControlBy control)
                continue;

            controller = control.Who is You
                ? layered.Controller
                : throw new NotImplementedException(
                    $"A control-changing effect naming {control.Who.GetType().Name} needs the query layer to resolve one player; see seam-requests/E7.md.");
        }

        return controller;
    }

    /// <summary>Layer 4, CR 613.1d: card types, subtypes and supertypes.</summary>
    [RuleRef("613.1d")]
    private static void Layer4Types(Draft draft, ImmutableList<Layered> effects)
    {
        foreach (var layered in effects)
        {
            switch (layered.Effect)
            {
                case AddTypes add:
                    draft.Types |= add.Types ?? CardType.None;
                    draft.Supertypes |= add.Supertypes ?? Supertype.None;

                    // CR 702.72a and 205.3d: changeling is every creature type. The names are not
                    // copied into the list — CR 205.3m changes every set, so a snapshot of it in a
                    // characteristics record would be stale; readers consult the flag instead.
                    if (add.AllCreatureTypes)
                        draft.EveryCreatureType = true;

                    foreach (var subtype in add.Subtypes ?? [])
                    {
                        if (!draft.Subtypes.Contains(subtype, StringComparer.OrdinalIgnoreCase))
                            draft.Subtypes.Add(subtype);
                    }

                    break;

                case SetTypes set:
                    if (set.Types is not null)
                        draft.Types = set.Types.Value;
                    if (set.Supertypes is not null)
                        draft.Supertypes = set.Supertypes.Value;
                    if (!set.KeepSubtypes)
                    {
                        // The list is replaced wholesale (CR 613.1d), so "every creature type"
                        // goes with it: the effect now names all of the object's subtypes.
                        draft.Subtypes.Clear();
                        draft.EveryCreatureType = false;
                    }

                    foreach (var subtype in set.Subtypes ?? [])
                    {
                        if (!draft.Subtypes.Contains(subtype, StringComparer.OrdinalIgnoreCase))
                            draft.Subtypes.Add(subtype);
                    }

                    break;

                case RemoveTypes remove:
                    draft.Types &= ~(remove.Types ?? CardType.None);
                    draft.Supertypes &= ~(remove.Supertypes ?? Supertype.None);

                    // CR 205.3d: "loses all creature types" removes exactly those — an artifact
                    // creature keeps Equipment — so the list is filtered, not cleared.
                    if (remove.AllCreatureTypes)
                    {
                        draft.EveryCreatureType = false;
                        draft.Subtypes.RemoveAll(CreatureTypes.IsCreatureType);
                    }

                    foreach (var subtype in remove.Subtypes ?? [])
                        draft.Subtypes.RemoveAll(s => string.Equals(s, subtype, StringComparison.OrdinalIgnoreCase));

                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>Layer 5, CR 613.1e: color-changing effects.</summary>
    [RuleRef("613.1e")]
    private static void Layer5Colors(Draft draft, ImmutableList<Layered> effects)
    {
        foreach (var layered in effects)
        {
            switch (layered.Effect)
            {
                case AddColors add:
                    draft.Colors |= add.Colors;
                    break;
                case SetColors set:
                    draft.Colors = set.Colors;
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>
    /// Layer 6, CR 613.1f: abilities added, keyword counters, abilities removed. Keyword
    /// counters come first because CR 613.1f lists them before ability-removing effects; the
    /// effects themselves are applied in timestamp order, so of a grant and a loss of the same
    /// keyword the later one wins (the example under CR 613.9).
    /// </summary>
    [RuleRef("613.1f")]
    private static void Layer6Abilities(GameObject obj, Draft draft, ImmutableList<Layered> effects)
    {
        // CR 122.1a: a permanent with a keyword counter on it has that keyword.
        foreach (var counter in obj.Counters.Where(c => c.Value > 0))
        {
            if (Counters.KeywordCounters.Contains(counter.Key) && Keywords.Find(counter.Key) is { } keyword)
                draft.Abilities.Add(new KeywordAbility(keyword));
        }

        foreach (var layered in effects)
        {
            switch (layered.Effect)
            {
                case TreatAsHavingKeywordCounter treat when Keywords.Find(treat.Counter) is { } granted:
                    draft.Abilities.Add(new KeywordAbility(granted));
                    break;

                case GrantAbility grant:
                    draft.Abilities.Add(grant.Ability);
                    break;

                case GrantKeyword keyword:
                    draft.Abilities.Add(new KeywordAbility(keyword.Keyword, keyword.Cost, keyword.N, keyword.Of));
                    break;

                case LoseAbility lose:
                    draft.Abilities.RemoveAll(a => a is KeywordAbility k && k.Keyword == lose.Keyword);
                    break;

                case LoseAllAbilities:
                    draft.Abilities.Clear();
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>
    /// Layer 7, CR 613.1g, in the sublayers of CR 613.4: 7a characteristic-defining power and
    /// toughness, 7b effects that set them, 7c effects and counters that modify them, 7d
    /// effects that switch them.
    /// </summary>
    [RuleRef("613.4")]
    private static void Layer7PowerAndToughness(GameObject obj, Draft draft, ImmutableList<Layered> effects)
    {
        // 7a, CR 613.4a: a characteristic-defining ability that defines power and toughness.
        foreach (var layered in effects.Where(l => l.CharacteristicDefining))
        {
            if (layered.Effect is SetPt cda)
            {
                draft.Power = Value(cda.Power, "a characteristic-defining power");
                draft.Toughness = Value(cda.Toughness, "a characteristic-defining toughness");
            }
        }

        // 7b, CR 613.4b: effects that set power and toughness to a specific number.
        foreach (var layered in effects.Where(l => !l.CharacteristicDefining))
        {
            if (layered.Effect is SetPt set)
            {
                draft.Power = Value(set.Power, "a power-setting effect");
                draft.Toughness = Value(set.Toughness, "a toughness-setting effect");
            }
        }

        // 7c, CR 613.4c: effects and counters that modify power and toughness. Addition
        // commutes, so the timestamp order the list is already in changes nothing here.
        var power = 0;
        var toughness = 0;
        foreach (var layered in effects)
        {
            if (layered.Effect is ModifyPt modify)
            {
                power += Value(modify.Power, "a power-modifying effect");
                toughness += Value(modify.Toughness, "a toughness-modifying effect");
            }
        }

        foreach (var counter in obj.Counters)
        {
            var delta = PowerToughnessCounter(counter.Key);
            if (counter.Value != 0 && delta is not null)
            {
                power += delta.Value.Power * counter.Value;
                toughness += delta.Value.Toughness * counter.Value;
            }
        }

        if (draft.Power is not null)
            draft.Power += power;
        if (draft.Toughness is not null)
            draft.Toughness += toughness;

        // 7d, CR 613.4d: switches. Two switches cancel each other out.
        if (effects.Count(l => l.Effect is SwitchPt) % 2 == 1)
            (draft.Power, draft.Toughness) = (draft.Toughness, draft.Power);
    }

    /// <summary>
    /// The numbers a continuous effect carries are pre-evaluated by the interpreter before the
    /// effect becomes active (CR 608.2h, 611.2d), so only a literal reaches this layer.
    /// </summary>
    private static int Value(Amount amount, string what) => amount switch
    {
        Fixed fixedAmount => fixedAmount.N,
        _ => throw new NotImplementedException(
            $"{what} used the amount {amount.GetType().Name}; the layer system takes only Fixed, because amounts are evaluated when the effect is created (CR 611.2d)."),
    };

    /// <summary>"+1/+1", "-1/-1", "+2/+0" and the rest: what one counter of that kind modifies.</summary>
    [RuleRef("613.4c")]
    private static (int Power, int Toughness)? PowerToughnessCounter(string counter)
    {
        var parts = counter.Split('/');
        if (parts.Length != 2)
            return null;
        if (!int.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var power))
            return null;
        if (!int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var toughness))
            return null;
        return (power, toughness);
    }

    /// <summary>CR 202.2: the colors of the mana symbols in a cost, hybrid and Phyrexian included.</summary>
    [RuleRef("202.2")]
    private static Color ColorsOf(string? manaCost)
    {
        var colors = Color.Colorless;
        foreach (var symbol in manaCost ?? string.Empty)
        {
            colors |= symbol switch
            {
                'W' => Color.White,
                'U' => Color.Blue,
                'B' => Color.Black,
                'R' => Color.Red,
                'G' => Color.Green,
                _ => Color.Colorless,
            };
        }

        return colors;
    }

    private static int? Number(string? text) =>
        int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>One continuous effect ready to apply: what it does, when it started, and who made it.</summary>
    private sealed record Layered(ContinuousEffect Effect, int Timestamp, bool CharacteristicDefining, ObjectId? Source, PlayerId Controller);

    /// <summary>The characteristics as the layers are being applied to them.</summary>
    private sealed class Draft
    {
        public required string Name { get; set; }

        public required CardType Types { get; set; }

        public required Supertype Supertypes { get; set; }

        public required List<string> Subtypes { get; set; }

        /// <summary>CR 205.3d: the object is every creature type, as changeling grants (CR 702.72a).</summary>
        public bool EveryCreatureType { get; set; }

        public required Color Colors { get; set; }

        public int? Power { get; set; }

        public int? Toughness { get; set; }

        public int? Loyalty { get; set; }

        public int? Defense { get; set; }

        public string? ManaCost { get; set; }

        public required List<Ability> Abilities { get; set; }

        public required PlayerId Controller { get; set; }
    }
}
