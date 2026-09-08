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

            // CR 301.5c, in the direction the vocabulary's own doc comment names: "attached to the
            // given object: Auras attached to it". The candidate is the attachment and To is the
            // host, so the question is whether the candidate's host is among the objects To names.
            // The other direction — "the object this is attached to", the equipped creature — is
            // the Attached *selector*, and it has no predicate spelling; a filter that wants it
            // cannot be written, which is the gap seam-requests/E0g.md records.
            // The record names objects, so an Aura on a player (CR 303.4f) matches nothing here
            // however the selector is written — the player half of "attached to" has no spelling in
            // the vocabulary; seam-requests/E5c.md asks for it.
            AttachedTo to => obj.AttachedTo is { } host && Resolve(state, to.To, ctx).Contains(host),

            // CR 301.5c from the other end of the link: "equipped creature" inside a filter is the
            // creature this source is attached to, which only the Attached *selector* names, so
            // IsOneOf is a selector said inside a filter — the gap seam-requests/E0g.md recorded,
            // closed by V9. The selector is read in the context the filter is read in.
            IsOneOf one => OneOf(state, one.Of, candidate, ctx),

            // CR 603.10: "that creature" said inside a filter, naming a key an earlier step
            // remembered. A key nothing wrote names nothing, so the filter admits nothing rather
            // than guessing — a Remembered selector reads the same way, and IsOneOf(Remembered) is
            // the same question asked through the general form.
            IsRemembered remembered => ctx.Remembered.GetValueOrDefault(remembered.Key, []).Contains(candidate),

            // CR 301.5 with CR 303.4f: attached means attached to an object or to a player, so
            // an "enchant player" Aura on its player answers this as any other Aura does.
            IsAttached attached => attached.Value == (obj.AttachedTo is not null || obj.AttachedToPlayer is not null),

            // CR 608.2b: a spell or ability on the stack knows what it targets.
            TargetedBy by => Resolve(state, by.By, ctx)
                .Any(source => state.Objects.TryGetValue(source, out var s) && s.Stack?.Targets.Contains(candidate) == true),

            // CR 115.1 with 601.2c: the other end of TargetedBy — this admits the spell or ability
            // doing the pointing, and the filter inside it describes what it points at. It reads the
            // targets that were announced, so a target that has since become illegal was still
            // targeted, and a spell that announced none is admitted by nothing here. A player target
            // is no object and so admits no ObjectFilter, which is why "targets a permanent you
            // control" is false of a spell aimed at a player.
            Targets targets => obj.Stack is { } entry
                && entry.Targets.Any(target => Matches(state, targets.Of, target, ctx)),

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

            // CR 707.10 with CR 707.12: the state carries two marks for a copy and this asks both.
            // A copied *spell* is marked on its stack entry; a copy of a *card* made in place is
            // marked with a designation, because such a copy has no stack entry to be found by and
            // is not a token (CR 111.1). CR 704.5e keeps a copy of a card on the battlefield, so
            // the question is reachable off the stack and is answered rather than refused.
            IsCopy when obj.Zone.Zone == Cards.Zone.Stack =>
                obj.Stack?.IsCopy == true || obj.Designations.Contains(Zones.ZoneSystem.CopyOfCard),
            IsCopy => obj.Designations.Contains(Zones.ZoneSystem.CopyOfCard),

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
    /// CR 301.5c: the candidate is one of the objects a selector names, the selector being said
    /// inside a filter. "Whenever equipped creature dies" describes the creature this Equipment is
    /// attached to, which the <see cref="Attached"/> selector names from the Equipment's side and
    /// no predicate of its own can say; "whenever that creature attacks" in a delayed trigger is
    /// the firing's <see cref="Triggering"/> object the same way. The selector is resolved in the
    /// context the filter is evaluated in — the ability's source for <see cref="Attached"/>, the
    /// firing's objects for <see cref="Triggering"/>, the resolution's memory for
    /// <see cref="Remembered"/> — so a selector that names nothing there admits nothing: an
    /// unattached Equipment, a filter read off any resolution with a remembered key.
    /// </summary>
    [RuleRef("301.5c")]
    private bool OneOf(GameState state, Selector of, ObjectId candidate, EffectContext ctx) =>
        Resolve(state, of, ctx).Contains(candidate);

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
