using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Query;

/// <summary>
/// The long tail of object descriptions: <see cref="ObjectFilter.Also"/>. Everything a fixed
/// field cannot say, answered from state and computed characteristics only.
/// </summary>
public sealed partial class Query
{
    private bool Holds(GameState state, Predicate predicate, ObjectId candidate, EffectContext ctx)
    {
        var obj = state.Objects[candidate];
        return predicate switch
        {
            NotPredicate not => !Holds(state, not.Of, candidate, ctx),
            AllOf all => all.Of.All(p => Holds(state, p, candidate, ctx)),
            AnyOf any => any.Of.Any(p => Holds(state, p, candidate, ctx)),

            // CR 105.1: sharing a color means the two color sets intersect.
            SharesColorWith shares => Resolve(state, shares.With, ctx)
                .Any(other => other != candidate && (_layers.Of(state, other).Colors & _layers.Of(state, candidate).Colors) != 0),

            // CR 205.2a and 205.3: a shared card type, or a shared creature type when asked.
            SharesTypeWith shares => Resolve(state, shares.With, ctx)
                .Any(other => other != candidate && SharesType(state, candidate, other, shares.CreatureTypeOnly)),

            SharesNameWith shares => Resolve(state, shares.With, ctx)
                .Any(other => other != candidate
                    && string.Equals(_layers.Of(state, other).Name, _layers.Of(state, candidate).Name, StringComparison.Ordinal)), // CR 201.2

            AttachedTo to => obj.AttachedTo is { } host && Resolve(state, to.To, ctx).Contains(host), // CR 301.5c
            IsAttached attached => attached.Value == (obj.AttachedTo is not null), // CR 301.5

            // CR 608.2b: a spell or ability on the stack knows what it targets.
            TargetedBy by => Resolve(state, by.By, ctx)
                .Any(source => state.Objects.TryGetValue(source, out var s) && s.Stack?.Targets.Contains(candidate) == true),

            IsChosenName chosen => ctx.Chosen.TryGetValue(chosen.Key, out var name)
                && string.Equals(_layers.Of(state, candidate).Name, name, StringComparison.Ordinal),
            IsChosenType chosen => ctx.Chosen.TryGetValue(chosen.Key, out var type)
                && (HasSubtype(_layers.Of(state, candidate), type) || HasCardType(_layers.Of(state, candidate), type)),
            IsChosenColor chosen => ctx.Chosen.TryGetValue(chosen.Key, out var color)
                && ColorNamed(color) is { } named && (_layers.Of(state, candidate).Colors & named) != 0,

            ManaValueParity parity => parity.Odd == (ManaCosts.Value(_layers.Of(state, candidate).ManaCost, obj) % 2 == 1), // CR 202.3
            HasSummoningSickness sick => sick.Value == obj.SummoningSick, // CR 302.6
            IsTransformed => obj.FaceIndex > 0, // CR 701.28a: transformed means its back face is up
            IsThisObject => ctx.Source is { } self && self == candidate, // CR 109.5: "this" is the object the ability is on

            // CR 707.10: only a spell on the stack records that it is a copy; a permanent has no
            // such flag in state yet, and guessing would be a lie. See seam-requests/E0.md.
            IsCopy when obj.Zone.Zone == Cards.Zone.Stack => obj.Stack?.IsCopy == true,
            IsCopy => throw Unanswerable(nameof(IsCopy), "no state records that a permanent is a copy (CR 707.10)"),

            HasAbilityOfKind kind => HasAbility(_layers.Of(state, candidate), kind.Ability), // CR 602.1
            ProducesMana produces => _layers.Of(state, candidate).Abilities
                .OfType<ActivatedAbility>().Any(a => Produces(a.Do, produces.Colors)), // CR 605.1a

            // CR 603.10: the turn's history is what "this turn" means, and a zone change folds it
            // as it happens, so the answer survives the object it is about ceasing to exist.
            EnteredThisTurn => state.History.EnteredBattlefield.Contains(candidate),

            // CR 400.7: a spell that resolved became a new object, so this is true of the spell on
            // the stack, not of the permanent it became. A card asking about the permanent asks
            // through the trigger that saw it enter.
            CastThisTurn => state.History.SpellsCast.Any(cast => cast.Spell == candidate),

            DealtDamageThisTurn dealt => DealtDamage(state, dealt, candidate, ctx), // CR 120.3
            WasDealtDamageThisTurn => state.History.DamageReceivedBy.GetValueOrDefault(candidate) > 0, // CR 120.3

            // CR 702.32a: kicker is an optional additional cost, and casting records what was paid.
            WasKicked => Kicks(obj) > 0,

            IsSuspected => obj.Designations.Contains(Designations.Suspected), // CR 701.60a
            IsGoaded goaded => Goaded(state, obj, goaded.By, ctx), // CR 701.15a
            IsRingBearer => obj.Designations.Contains(Designations.RingBearer), // CR 701.54a
            IsHarnessed => obj.Designations.Contains(Designations.Harnessed), // CR 701.64a
            IsPrepared => obj.Designations.Contains(Designations.Prepared), // CR 702.180

            CanBlock => throw Unanswerable(nameof(CanBlock), "blocking legality belongs to the combat subsystem (CR 509.1a)", "509.1a"),
            CouldBlock => throw Unanswerable(nameof(CouldBlock), "blocking legality belongs to the combat subsystem (CR 509.1a)", "509.1a"),
            _ => throw Unanswerable(predicate.GetType().Name, "no arm in the query layer"),
        };
    }

    private const string NoDamagePairs =
        "the turn's history totals damage per source and per receiver, not per pair, so \"dealt damage to that player this turn\" has nothing to read; see seam-requests/E0c.md";

    /// <summary>
    /// CR 701.15a: goading names the goader, because a goaded creature must attack a player other
    /// than that one, so one creature can be goaded by several players at once. With no player
    /// named the question is "goaded at all"; with one it is "goaded by them".
    /// </summary>
    [RuleRef("701.15a")]
    private bool Goaded(GameState state, GameObject obj, PlayerRef? by, EffectContext ctx) =>
        by is null
            ? obj.Designations.Any(d => d.StartsWith(Designations.GoadedPrefix, StringComparison.Ordinal))
            : Players(state, by, ctx).Any(p => obj.Designations.Contains(Designations.GoadedBy(p)));

    /// <summary>
    /// CR 702.32b: kicker's cost may be paid more than once when it is repeatable, and casting
    /// records one entry per payment, so the count is the number of entries and "was kicked" is
    /// that count being more than none.
    /// </summary>
    [RuleRef("702.32b")]
    private static int Kicks(GameObject obj) =>
        obj.Stack?.OptionalCostsPaid?.Count(c => string.Equals(c, nameof(KeywordKind.Kicker), StringComparison.Ordinal)) ?? 0;

    private bool SharesType(GameState state, ObjectId left, ObjectId right, bool creatureTypeOnly)
    {
        var a = _layers.Of(state, left);
        var b = _layers.Of(state, right);
        if (creatureTypeOnly)
            return SharesCreatureType(a, b);
        return (a.Types & b.Types) != 0 || a.Subtypes.Any(s => HasSubtype(b, s)) || SharesCreatureType(a, b);
    }

    /// <summary>
    /// CR 205.3m: a shared creature type, which is the only kind "shares a creature type with"
    /// counts — a shared Equipment or Aura subtype is not one. An object that is every creature
    /// type (CR 205.3d) shares one with anything that has a creature type at all, and with
    /// nothing that has none, which is why the flag is asked before the two lists are compared.
    /// </summary>
    [RuleRef("205.3m")]
    private static bool SharesCreatureType(Characteristics a, Characteristics b)
    {
        if (a.EveryCreatureType && b.EveryCreatureType)
            return true;
        if (a.EveryCreatureType)
            return CreatureTypes.AnyIn(b.Subtypes);
        if (b.EveryCreatureType)
            return CreatureTypes.AnyIn(a.Subtypes);
        return CreatureTypes.Shared(a.Subtypes, b.Subtypes);
    }

    private static bool HasCardType(Characteristics c, string type) =>
        Enum.TryParse<CardType>(type, ignoreCase: true, out var parsed) && (c.Types & parsed) != 0;

    private static Color? ColorNamed(string color) =>
        Enum.TryParse<Color>(color, ignoreCase: true, out var parsed) ? parsed : null;

    /// <summary>CR 602.1 and 605.1a: which kind of activated ability an object has.</summary>
    private static bool HasAbility(Characteristics c, AbilityKind kind)
    {
        var activated = c.Abilities.OfType<ActivatedAbility>().ToList();
        return kind switch
        {
            AbilityKind.Any => activated.Count > 0,
            AbilityKind.Loyalty => activated.Any(a => a.Costs.OfType<LoyaltyCost>().Any()), // CR 606.1
            AbilityKind.Mana => activated.Any(IsManaAbility),
            AbilityKind.NonMana => activated.Any(a => !IsManaAbility(a)),
            _ => throw new NotImplementedException($"Ability kind {kind} is not implemented."),
        };
    }

    /// <summary>CR 605.1a: an activated mana ability has no target, is not a loyalty ability, and could add mana.</summary>
    private static bool IsManaAbility(ActivatedAbility ability) =>
        !ability.Costs.OfType<LoyaltyCost>().Any() && Produces(ability.Do, null);

    /// <summary>Whether an effect tree adds mana, optionally of one of the given colors, CR 106.1.</summary>
    private static bool Produces(Effect effect, Color? colors) => effect switch
    {
        AddMana mana => colors is null || ProducesColor(mana.Mana, colors.Value),
        Sequence sequence => sequence.Steps.Any(s => Produces(s, colors)),
        If branch => Produces(branch.Then, colors) || (branch.Else is not null && Produces(branch.Else, colors)),
        May may => Produces(may.Do, colors),
        _ => false,
    };

    private static bool ProducesColor(ManaProduction production, Color colors)
    {
        if (production.AnyColor || production.AnyType || production.ChosenColor || production.CommanderIdentity
            || production.ColorOfAttachedOrTarget || production.LandCouldProduce)
        {
            return true;
        }

        if (production.OneOf is { } oneOf && (oneOf & colors) != 0)
            return true;
        return production.Fixed is { } fixedMana && ManaCosts.Devotion(fixedMana, colors) > 0;
    }
}
