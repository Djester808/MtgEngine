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

            // CR 707.10: only a spell on the stack records that it is a copy; a permanent has no
            // such flag in state yet, and guessing would be a lie. See seam-requests/E0.md.
            IsCopy when obj.Zone.Zone == Cards.Zone.Stack => obj.Stack?.IsCopy == true,
            IsCopy => throw Unanswerable(nameof(IsCopy), "no state records that a permanent is a copy (CR 707.10)"),

            HasAbilityOfKind kind => HasAbility(_layers.Of(state, candidate), kind.Ability), // CR 602.1
            ProducesMana produces => _layers.Of(state, candidate).Abilities
                .OfType<ActivatedAbility>().Any(a => Produces(a.Do, produces.Colors)), // CR 605.1a

            EnteredThisTurn => throw Unanswerable(nameof(EnteredThisTurn), NoHistory),
            CastThisTurn => throw Unanswerable(nameof(CastThisTurn), NoHistory),
            DealtDamageThisTurn => throw Unanswerable(nameof(DealtDamageThisTurn), NoHistory),
            WasDealtDamageThisTurn => throw Unanswerable(nameof(WasDealtDamageThisTurn), NoHistory),
            WasKicked => throw Unanswerable(nameof(WasKicked), "no state records the optional costs paid for a spell (CR 601.2b)"),
            IsSuspected => throw Unanswerable(nameof(IsSuspected), NoDesignation),
            IsGoaded => throw Unanswerable(nameof(IsGoaded), NoDesignation),
            IsRingBearer => throw Unanswerable(nameof(IsRingBearer), NoDesignation),
            IsHarnessed => throw Unanswerable(nameof(IsHarnessed), NoDesignation),
            CanBlock => throw Unanswerable(nameof(CanBlock), "blocking legality belongs to the combat subsystem (CR 509.1a)"),
            CouldBlock => throw Unanswerable(nameof(CouldBlock), "blocking legality belongs to the combat subsystem (CR 509.1a)"),
            _ => throw Unanswerable(predicate.GetType().Name, "no arm in the query layer"),
        };
    }

    private const string NoHistory = "no state records what happened earlier this turn; see seam-requests/E0.md";

    private const string NoDesignation = "no state records per-object designations (CR 701.60, 701.15, 701.54, 701.64); see seam-requests/E0.md";

    private bool SharesType(GameState state, ObjectId left, ObjectId right, bool creatureTypeOnly)
    {
        var a = _layers.Of(state, left);
        var b = _layers.Of(state, right);
        if (creatureTypeOnly)
            return a.Subtypes.Any(s => HasSubtype(b, s)); // CR 205.3m
        return (a.Types & b.Types) != 0 || a.Subtypes.Any(s => HasSubtype(b, s));
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
