using System.Collections.Immutable;

namespace MtgEngine.Rules.Abilities;

/// <summary>
/// Which effects aim at a chosen target, and how to read or move that index.
/// </summary>
/// <remarks>
/// One table, because there were three switches and they drifted. An effect that aims at a target
/// carries an index into its ability's target list, and two callers need it: the compiler, when it
/// folds a clause parsed on its own into a larger ability and every index has to shift; and the
/// corpus invariant test, which checks that no index points past the end. A third place — a new
/// targeting effect — used to mean remembering both.
/// <para>
/// <see cref="IEffect"/> deliberately says nothing about targets, because plenty of effects have
/// none and widening the interface to suit two callers would be the callers dictating the design.
/// So the knowledge lives here, keyed by type so that <see cref="HandledTypes"/> is derived from
/// the same entries rather than restated — a second list is exactly what this file exists to
/// prevent. A reflection test asserts the table covers every effect that has a target index.
/// </para>
/// </remarks>
/// <summary>
/// Walks the effects of one ability, including the ones nested inside branches.
/// </summary>
/// <remarks>
/// An ability's effects are a tree, not a list: an optional payment holds the two branches it
/// chooses between, an intervening-if holds what it guards, and an attachment effect holds what it
/// redirects. Anything that has to find an effect again — every deferred question does, because the
/// answer arrives after the resolution is over — has to be able to look inside those.
/// <para>
/// The children are found by reflection over properties that hold effects, so a new branching
/// effect is walked without anyone remembering to add it here. That is the same reason
/// <see cref="EffectTargets"/> exists: a hand-written list of which effects contain which is a
/// list that goes stale, and the failure is silent.
/// </para>
/// </remarks>
public static class EffectTree
{
    /// <summary>Every effect in the tree, parents before their children.</summary>
    public static IEnumerable<IEffect> Flatten(IEnumerable<IEffect> effects)
    {
        ArgumentNullException.ThrowIfNull(effects);

        foreach (var effect in effects)
        {
            yield return effect;

            foreach (var nested in Children(effect))
            {
                foreach (var deeper in Flatten([nested]))
                    yield return deeper;
            }
        }
    }

    /// <summary>
    /// The one effect of a kind carrying a given locator, or null if it is not there or not alone.
    /// </summary>
    /// <remarks>
    /// Ambiguity returns null rather than a guess. Two effects of the same kind with the same
    /// index in different branches is a card this cannot answer for, and answering the wrong one
    /// would run a branch the player did not choose.
    /// </remarks>
    public static T? Locate<T>(IEnumerable<IEffect> effects, int index)
        where T : class, IEffect
    {
        var matches = Flatten(effects)
            .OfType<T>()
            .Where(candidate => LocatorOf(candidate) == index)
            .Take(2)
            .ToList();

        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>The locator an effect carries so a deferred question can find it again.</summary>
    /// <remarks>
    /// Public because the compiler has to ask the same question <see cref="Locate{T}"/> asks -
    /// whether a question in a tree it is about to build would still be found uniquely - and the
    /// only honest way to ask it is with the accessor the lookup itself uses. A second reflection
    /// over the same property name is exactly the drift this file exists to prevent.
    /// </remarks>
    public static int? LocatorOf(IEffect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);

        return effect.GetType().GetProperty("EffectIndex") is { } property
            ? property.GetValue(effect) as int?
            : null;
    }

    private static IEnumerable<IEffect> Children(IEffect effect)
    {
        foreach (var property in effect.GetType().GetProperties())
        {
            // A wrapper that holds one effect rather than a list of them - "run this, and keep
            // what it produced" - is just as much a parent, and was invisible here until it was
            // asked for by name. An effect the tree cannot see is an effect the structural check
            // cannot vouch for: its target looks chosen and never used.
            if (typeof(IEffect).IsAssignableFrom(property.PropertyType)
                && property.GetValue(effect) is IEffect single)
            {
                yield return single;
                continue;
            }

            if (!typeof(IEnumerable<IEffect>).IsAssignableFrom(property.PropertyType))
                continue;

            if (property.GetValue(effect) is IEnumerable<IEffect> nested)
            {
                foreach (var child in nested)
                    yield return child;
            }
        }
    }
}

public static class EffectTargets
{
    private sealed record Accessor(
        Func<IEffect, int?> Read,
        Func<IEffect, int, IEffect> WithOffset,
        Func<IEffect, IEnumerable<int>>? ReadAll = null);

    private static readonly ImmutableDictionary<Type, Accessor> Table = Build();

    /// <summary>Every effect type that aims at a target. Derived from the table, not restated.</summary>
    public static IReadOnlyCollection<Type> HandledTypes => Table.Keys.ToList();

    /// <summary>The target index an effect uses, or null when it does not aim at one.</summary>
    public static int? IndexOf(IEffect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);

        return Table.TryGetValue(effect.GetType(), out var accessor) ? accessor.Read(effect) : null;
    }

    /// <summary>
    /// Whether this effect actually reads a target, rather than being aimed somewhere else.
    /// </summary>
    /// <remarks>
    /// Several effects can be pointed at something that is not a target - the creature a trigger
    /// was about, the permanent an Aura is on - and when they are, their target index is not read
    /// at all. Anything checking that an index is in range has to know that, or it reports a card
    /// as malformed for using an index it never looks at.
    /// <para>
    /// Asked here rather than at the check, so the one place that knows how an effect finds its
    /// subject is the place that answers it.
    /// </para>
    /// </remarks>
    public static bool ReadsATarget(IEffect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);

        if (IndexOf(effect) is null)
            return false;

        return effect.GetType().GetProperty("Subject") is not { } subject
            || subject.GetValue(effect) is not EffectSubject aimed
            || aimed == EffectSubject.Target;
    }

    /// <summary>
    /// Every target index an effect uses, which is not always one.
    /// </summary>
    /// <remarks>
    /// <see cref="IndexOf"/> answers with the index that is always there, which is what the
    /// parser needs and what most effects have. A fight names two, and anything asking "is this
    /// target used by anything" has to see both or it will decide a card ignores a creature the
    /// player chose. Shifting has always moved both; only the reading was ever the single one.
    /// </remarks>
    public static IEnumerable<int> IndicesOf(IEffect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);

        if (!Table.TryGetValue(effect.GetType(), out var accessor))
            return [];

        if (accessor.ReadAll is { } all)
            return all(effect);

        return accessor.Read(effect) is { } one ? [one] : [];
    }

    /// <summary>
    /// The same effect with its target index moved by an offset.
    /// </summary>
    /// <remarks>
    /// Needed wherever a clause is parsed on its own and then folded into a larger ability: the
    /// phrase parser numbers targets from zero because it does not know what it is being folded
    /// into. Effects that aim at nothing are returned unchanged.
    /// <para>
    /// So is an effect aimed somewhere that is not a target. Its index is never read - that is
    /// what <see cref="ReadsATarget"/> says - so moving it changes nothing about how the effect
    /// resolves, and leaves behind a number pointing past the end of the target list that looks
    /// exactly like the malformed card the invariant test hunts for.
    /// </para>
    /// </remarks>
    public static IEffect Shift(IEffect effect, int offset)
    {
        ArgumentNullException.ThrowIfNull(effect);

        if (offset == 0 || !Table.TryGetValue(effect.GetType(), out var accessor))
            return effect;

        return ReadsATarget(effect) ? accessor.WithOffset(effect, offset) : effect;
    }

    private static ImmutableDictionary<Type, Accessor> Build()
    {
        var table = ImmutableDictionary.CreateBuilder<Type, Accessor>();

        void Add<T>(
            Func<T, int?> read,
            Func<T, int, T> shift,
            Func<T, IEnumerable<int>>? readAll = null)
            where T : IEffect =>
            table.Add(
                typeof(T),
                new Accessor(
                    e => read((T)e),
                    (e, offset) => shift((T)e, offset),
                    readAll is null ? null : e => readAll((T)e)));

        Add<DealDamage>(e => e.TargetIndex, (e, n) => e with { TargetIndex = e.TargetIndex + n });
        // These three take a pronoun as well as a target ("destroy that creature"), and an
        // index only means anything in the first case. Reported as null otherwise, which is what
        // stops the structural check reading it as a reference to a target that is not there.
        Add<DestroyTarget>(
            e => e.Subject == EffectSubject.Target ? e.TargetIndex : null,
            (e, n) => e.Subject == EffectSubject.Target
                ? e with { TargetIndex = e.TargetIndex + n }
                : e);
        Add<ExileTarget>(
            e => e.Subject == EffectSubject.Target ? e.TargetIndex : null,
            (e, n) => e.Subject == EffectSubject.Target
                ? e with { TargetIndex = e.TargetIndex + n }
                : e);
        Add<ExileAndReturnAtEndStep>(
            e => e.TargetIndex, (e, n) => e with { TargetIndex = e.TargetIndex + n });

        // The same pronoun-or-target shape as the three above: a delayed action aimed at the
        // creature a trigger named carries an index nothing reads.
        Add<DelayObjectAction>(
            e => e.Subject == EffectSubject.Target ? e.TargetIndex : null,
            (e, n) => e.Subject == EffectSubject.Target
                ? e with { TargetIndex = e.TargetIndex + n }
                : e);

        Add<FlickerTarget>(
            e => e.TargetIndex, (e, n) => e with { TargetIndex = e.TargetIndex + n });

        // Only when it is about a target: the source and the host forms carry an index that
        // means nothing, and shifting it would be shifting a number nobody reads.
        Add<Regenerate>(
            e => e.Subject == EffectSubject.Target ? e.TargetIndex : null,
            (e, n) => e.Subject == EffectSubject.Target
                ? e with { TargetIndex = e.TargetIndex + n }
                : e);
        Add<ExileUntilSourceLeaves>(
            e => e.TargetIndex, (e, n) => e with { TargetIndex = e.TargetIndex + n });
        Add<RevealAndTake>(
            e => e.TargetIndex, (e, n) => e with { TargetIndex = e.TargetIndex + n });
        Add<ExileInsteadOfDying>(
            e => e.TargetIndex, (e, n) => e with { TargetIndex = e.TargetIndex + n });
        Add<ReturnToHand>(
            e => e.Subject == EffectSubject.Target ? e.TargetIndex : null,
            (e, n) => e.Subject == EffectSubject.Target
                ? e with { TargetIndex = e.TargetIndex + n }
                : e);
        Add<TapTarget>(
            e => e.Subject == EffectSubject.Target ? e.TargetIndex : null,
            (e, n) => e.Subject == EffectSubject.Target
                ? e with { TargetIndex = e.TargetIndex + n }
                : e);
        Add<UntapTarget>(
            e => e.Subject == EffectSubject.Target ? e.TargetIndex : null,
            (e, n) => e.Subject == EffectSubject.Target
                ? e with { TargetIndex = e.TargetIndex + n }
                : e);
        Add<PhaseOutPermanent>(
            e => e.Subject == EffectSubject.Target ? e.TargetIndex : null,
            (e, n) => e.Subject == EffectSubject.Target
                ? e with { TargetIndex = e.TargetIndex + n }
                : e);
        // Only when it is about a target: the source, host and trigger-subject forms carry an
        // index that means nothing, and shifting it would move a number nobody reads.
        Add<PutCounters>(
            e => e.Subject == EffectSubject.Target ? e.TargetIndex : null,
            (e, n) => e.Subject == EffectSubject.Target
                ? e with { TargetIndex = e.TargetIndex + n }
                : e);
        // The same shape as PutCounters, and about the same thing: doubling is aimed at a target
        // only when the sentence named one.
        Add<DoubleCounters>(
            e => e.Subject == EffectSubject.Target ? e.TargetIndex : null,
            (e, n) => e.Subject == EffectSubject.Target
                ? e with { TargetIndex = e.TargetIndex + n }
                : e);
        // Only when it is about a target, for the same reason PutCounters is: "it gets -1/-0"
        // in a trigger names the object the trigger was about, and the index it carries then
        // means nothing.
        Add<PumpUntilEndOfTurn>(
            e => e.Subject == EffectSubject.Target ? e.TargetIndex : null,
            (e, n) => e.Subject == EffectSubject.Target
                ? e with { TargetIndex = e.TargetIndex + n }
                : e);
        Add<PumpPairedPartner>(
            e => e.TargetIndex, (e, n) => e with { TargetIndex = e.TargetIndex + n });
        Add<AttachSourceTo>(e => e.TargetIndex, (e, n) => e with { TargetIndex = e.TargetIndex + n });
        // Nullable, because the shield may name a player by scope instead of by target.
        Add<PreventDamage>(
            e => e.TargetIndex,
            (e, n) => e.TargetIndex is { } i ? e with { TargetIndex = i + n } : e);

        // Nullable for the same reason and more of them: a described prevention names its subject
        // by filter or by scope far more often than it targets one.
        Add<PreventDescribedDamage>(
            e => e.TargetIndex,
            (e, n) => e.TargetIndex is { } i ? e with { TargetIndex = i + n } : e);
        Add<CounterTargetSpell>(e => e.TargetIndex, (e, n) => e with { TargetIndex = e.TargetIndex + n });
        Add<MoveTargetedCard>(e => e.TargetIndex, (e, n) => e with { TargetIndex = e.TargetIndex + n });

        // A search reads a target for either of two reasons and never for both at once: the
        // extraction family names the player whose zones are searched through the thing it
        // countered or exiled, and the Aura family names the permanent or player what it finds
        // arrives attached to. An ordinary tutor does neither, carries the index and never looks
        // at it, so it is reported as null - the same shape as the three pronoun effects above.
        Add<SearchLibrary>(
            e => SearchReadsATarget(e) ? e.TargetIndex : null,
            (e, n) => SearchReadsATarget(e) ? e with { TargetIndex = e.TargetIndex + n } : e);
        Add<GainControlUntilEndOfTurn>(
            e => e.TargetIndex, (e, n) => e with { TargetIndex = e.TargetIndex + n });

        Add<Explore>(
            e => e.Subject == EffectSubject.Target ? e.TargetIndex : null,
            (e, n) => e.Subject == EffectSubject.Target
                ? e with { TargetIndex = e.TargetIndex + n }
                : e);

        Add<Connive>(
            e => e.Subject == EffectSubject.Target ? e.TargetIndex : null,
            (e, n) => e.Subject == EffectSubject.Target
                ? e with { TargetIndex = e.TargetIndex + n }
                : e);

        Add<GoadTarget>(
            e => e.TargetIndex, (e, n) => e with { TargetIndex = e.TargetIndex + n });

        Add<SuspectTarget>(
            e => e.TargetIndex, (e, n) => e with { TargetIndex = e.TargetIndex + n });

        Add<CantBlockSource>(
            e => e.TargetIndex, (e, n) => e with { TargetIndex = e.TargetIndex + n });

        Add<MustBlockSource>(
            e => e.TargetIndex, (e, n) => e with { TargetIndex = e.TargetIndex + n });

        Add<GainControlWhileSourceHolds>(
            e => e.TargetIndex, (e, n) => e with { TargetIndex = e.TargetIndex + n });

        // Only shifted when it is aimed at a target: like its until-end-of-turn twin it can name
        // the creature a trigger was about instead, and that index means nothing here.
        Add<HoldsWhileSourceHolds>(
            e => e.TargetIndex,
            (e, n) => e.Subject == EffectSubject.Target
                ? e with { TargetIndex = e.TargetIndex + n }
                : e);

        Add<ChooseCreatureTypeForTarget>(
            e => e.TargetIndex,
            (e, n) => e.TargetIndex is { } i ? e with { TargetIndex = i + n } : e);

        Add<ChooseColorForTarget>(
            e => e.TargetIndex, (e, n) => e with { TargetIndex = e.TargetIndex + n });

        // Nullable for the same reason the creature type's is: the sentence has a self form
        // as well as a targeted one, and a null index means the source is choosing for itself.
        Add<ChooseBasicLandTypeForTarget>(
            e => e.TargetIndex,
            (e, n) => e.TargetIndex is { } i ? e with { TargetIndex = i + n } : e);

        // These two aim at a player only when they name one, so their index is nullable — which
        // is why membership of this table is what "targets something" means, and not whether a
        // particular instance happens to carry a number.
        Add<DrawCards>(
            e => e.TargetIndex,
            (e, n) => e.TargetIndex is { } i ? e with { TargetIndex = i + n } : e);

        Add<ChangeLife>(
            e => e.TargetIndex,
            (e, n) => e.TargetIndex is { } i ? e with { TargetIndex = i + n } : e);

        // Nullable for the same reason ChangeLife's is: the sentence names a scope far more
        // often than it targets a player.
        Add<LoseHalfLife>(
            e => e.TargetIndex,
            (e, n) => e.TargetIndex is { } i ? e with { TargetIndex = i + n } : e);

        Add<MillHalfLibrary>(
            e => e.TargetIndex,
            (e, n) => e.TargetIndex is { } i ? e with { TargetIndex = i + n } : e);

        Add<ShuffleLibrary>(
            e => e.TargetIndex,
            (e, n) => e.TargetIndex is { } i ? e with { TargetIndex = i + n } : e);

        Add<RevealHand>(
            e => e.TargetIndex,
            (e, n) => e.TargetIndex is { } i ? e with { TargetIndex = i + n } : e);

        Add<SkipNextUntap>(
            e => e.Subject == EffectSubject.Target ? e.TargetIndex : null,
            (e, n) => e.Subject == EffectSubject.Target
                ? e with { TargetIndex = e.TargetIndex + n }
                : e);

        Add<PutTargetOnLibrary>(
            e => e.TargetIndex,
            (e, n) => e with { TargetIndex = e.TargetIndex + n });

        // The index names the creature whose *controller* is damaged, not the creature - but it
        // is still read off the target list, so it shifts with everything else.
        Add<DamageTargetsController>(
            e => e.TargetIndex, (e, n) => e with { TargetIndex = e.TargetIndex + n });
        Add<ChangeLifeOfTargetsController>(
            e => e.TargetIndex,
            (e, n) => e with { TargetIndex = e.TargetIndex + n });

        Add<DrawForTargetsController>(
            e => e.TargetIndex,
            (e, n) => e with { TargetIndex = e.TargetIndex + n });

        Add<MillForTargetsController>(
            e => e.TargetIndex,
            (e, n) => e with { TargetIndex = e.TargetIndex + n });

        Add<CreateTokenForTargetsController>(
            e => e.TargetIndex,
            (e, n) => e with { TargetIndex = e.TargetIndex + n });

        // Nullable for the same reason ChangeLife's is: "you get an emblem with ..." names a
        // scope and only "target opponent gets an emblem with ..." names a player.
        Add<CreateEmblem>(
            e => e.TargetIndex,
            (e, n) => e.TargetIndex is { } i ? e with { TargetIndex = i + n } : e);

        Add<DiscardCards>(
            e => e.TargetIndex,
            (e, n) => e.TargetIndex is { } i ? e with { TargetIndex = i + n } : e);

        Add<MillCards>(
            e => e.TargetIndex,
            (e, n) => e.TargetIndex is { } i ? e with { TargetIndex = i + n } : e);

        Add<ChooseAndMove>(
            e => e.TargetIndex,
            (e, n) => e.TargetIndex is { } i ? e with { TargetIndex = i + n } : e);

        // Aims at nothing itself, but its rows may refer back to a target the ability chose
        // before rolling — "1—9 | Tap that creature." — so a fold that renumbers targets has to
        // reach inside and move every row's references with it. Reading is left to the tree
        // walk: RollBranch is itself an effect precisely so the rows' contents are visible.
        Add<RollDice>(
            _ => null,
            (e, n) => e with
            {
                Rows =
                [
                    .. e.Rows.Select(row => row with
                    {
                        Effects = [.. row.Effects.Select(inner => Shift(inner, n))],
                    }),
                ],
            });

        Add<ExileGraveyard>(
            e => e.TargetIndex, (e, n) => e with { TargetIndex = e.TargetIndex + n });

        // A range rather than a slot: a divided effect aims at every target the spell chose, and
        // which of them get any is decided by the division announced as it was cast. These are the
        // effects whose targets are positions rather than a number they carry.
        Add<DealDividedDamage>(
            e => e.FirstIndex,
            (e, n) => e with { FirstIndex = e.FirstIndex + n },
            e => Enumerable.Range(e.FirstIndex, e.TargetCount));

        Add<DistributeCounters>(
            e => e.FirstIndex,
            (e, n) => e with { FirstIndex = e.FirstIndex + n },
            e => Enumerable.Range(e.FirstIndex, e.TargetCount));

        // The block moves and so does everything written against it. Shifting only the outer
        // index would leave the per-target effects pointing at whatever the ability targeted
        // before this clause was folded in — the same defect that made a card print the same
        // pump three times and put all of it on one creature.
        Add<ToEachChosenTarget>(
            e => e.FirstIndex,
            (e, n) => e with
            {
                FirstIndex = e.FirstIndex + n,
                Effects = [.. e.Effects.Select(inner => Shift(inner, n))],
            });

        // Two slots, and both move together. The read returns the one that is always present —
        // a fight always has an opponent, and only sometimes names its own fighter.
        Add<Fight>(
            e => e.TheirIndex,
            (e, n) => e with
            {
                TheirIndex = e.TheirIndex + n,
                MyIndex = e.MyIndex is { } mine ? mine + n : null,
            },
            e => e.MyIndex is { } mine ? [e.TheirIndex, mine] : [e.TheirIndex]);

        // Storm copies the spell that made it and names no target at all; "copy target instant or
        // sorcery spell" names one. Same effect, and the nullable index is what tells them apart.
        Add<CopySpell>(
            e => e.TargetIndex,
            (e, n) => e.TargetIndex is { } i ? e with { TargetIndex = i + n } : e);

        // Null when it copies the permanent whose ability it is, an index when it copies a target.
        // Only when a player was named: "each opponent creates" carries no index, and shifting
        // one that is not there would move a number nobody reads.
        Add<CreateToken>(
            e => e.TargetIndex,
            (e, n) => e.TargetIndex is { } i ? e with { TargetIndex = i + n } : e);

        Add<CreateTokenCopy>(
            e => e.TargetIndex,
            (e, n) => e.TargetIndex is { } i ? e with { TargetIndex = i + n } : e);

        // The same slot on conjure's half of the family (CR 701.56): null when the duplicate is
        // of this permanent or of whatever a trigger named, an index when the sentence targets.
        Add<ConjureDuplicate>(
            e => e.TargetIndex,
            (e, n) => e.TargetIndex is { } i ? e with { TargetIndex = i + n } : e);

        // The index names what is copied rather than what changes: the permanent that becomes
        // the copy is usually the source. Always a real index - a copy effect with nothing to
        // copy is CR 608.2b and does nothing.
        Add<BecomeCopyOfTarget>(
            e => e.TargetIndex, (e, n) => e with { TargetIndex = e.TargetIndex + n });

        // Null for the bare "take an extra turn after this one", an index when the card names
        // somebody instead. Registering it is what lets the structural check see that the target
        // a targeted printing chooses is actually read.
        // Its index is always a real one: the pronoun form with nothing targeted uses the
        // trigger subject instead, and that carries no index to shift.
        Add<LookAtHand>(
            e => e.TargetIndex, (e, n) => e with { TargetIndex = e.TargetIndex + n });

        Add<RedirectDamage>(
            e => e.TargetIndex, (e, n) => e with { TargetIndex = e.TargetIndex + n });

        Add<OwnerChoosesLibraryEnd>(
            e => e.TargetIndex, (e, n) => e with { TargetIndex = e.TargetIndex + n });

        Add<TapAndFreeze>(
            e => e.TargetIndex, (e, n) => e with { TargetIndex = e.TargetIndex + n });

        Add<TakeExtraTurn>(
            e => e.TargetIndex,
            (e, n) => e.TargetIndex is { } i ? e with { TargetIndex = i + n } : e);

        // The two group effects, which reach a target without being aimed at one. A sweeper
        // chooses nothing (CR 609.2), but "each other creature that shares a color with it" reads
        // the creature the same spell targeted, so the index that names that sibling has to move
        // with every other index when a clause parsed on its own is folded into a larger ability.
        // Null on every ordinary sweeper, and a null index is inert here: nothing to read, nothing
        // to shift, nothing for the range check to complain about.
        Add<ToEachPermanent>(
            e => e.PeerIndex,
            (e, n) => e.PeerIndex is { } i ? e with { PeerIndex = i + n } : e);

        Add<PumpGroup>(
            e => e.PeerIndex,
            (e, n) => e.PeerIndex is { } i ? e with { PeerIndex = i + n } : e);

        return table.ToImmutable();
    }

    /// <summary>Whether a search's target index names anything (CR 601.2c).</summary>
    /// <remarks>
    /// One index answering two questions, which is safe only because no card asks both: a search
    /// of somebody else's zones reads the target to find out whose they are, and a search that
    /// attaches what it finds reads it to find the host. A card doing both would need two, and
    /// this table can shift one.
    /// </remarks>
    private static bool SearchReadsATarget(SearchLibrary search) =>
        search.Whose != SearchWhoseZones.Searcher
        || search.Attaches is SearchAttachment.TargetPermanent or SearchAttachment.TargetPlayer;
}
