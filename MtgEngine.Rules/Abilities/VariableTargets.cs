using System.Collections.Immutable;

namespace MtgEngine.Rules.Abilities;

/// <summary>
/// Turns "any number of target ..." into the counted target list everything else reads
/// (CR 601.2c).
/// </summary>
/// <remarks>
/// A spell that chooses how many things it targets was the one shape a fixed
/// <see cref="ImmutableList{T}"/> of specs could not express, and giving it a ceiling was refused
/// twice as "a different card whenever the ceiling mattered". This is the reading that needs no
/// ceiling: CR 601.2c has the player announce how many targets they will choose before choosing
/// them, and "once the number of targets the spell has is determined, that number doesn't
/// change". The engine already carries that number — it is the length of the target list handed
/// to <c>CastSpell</c>, and it rides on the stack object from the moment the spell is cast — so
/// the definition holds one spec and one per-target effect, and the announced count turns them
/// into as many as were chosen.
/// <para>
/// Nothing new is remembered. The expansion is derived from what the stack object already holds,
/// which is what lets a game folded back from its log reach the same spell it was cast as. No
/// state field, no event, and no new argument on the wire.
/// </para>
/// <para>
/// <strong>The block must be last.</strong> Every index downstream is positional — the effects'
/// target indices, the slices modes and spliced text take — so a block that grew in the middle
/// would silently move everything after it. That is refused rather than guessed at, in both
/// directions: the compiler will not emit a block anywhere but last, and
/// <see cref="Expand(ImmutableList{TargetSpec}, ImmutableList{IEffect}, int)"/> throws on one it
/// is handed. A cast that adds targets of its own after the card's — bestow, mutate, awaken —
/// therefore refuses instead of aiming a pump at whatever the Aura chose.
/// </para>
/// </remarks>
public static class VariableTargets
{
    /// <summary>Whether any of these specs stands for a block rather than one target.</summary>
    public static bool Present(ImmutableList<TargetSpec> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);

        for (var i = 0; i < targets.Count; i++)
        {
            if (targets[i].AnyNumber)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether these specs could be expanded — one block at all, and that one last.
    /// </summary>
    /// <remarks>
    /// Asked by the compiler before it keeps a block, so a card that would need a shape the
    /// expansion refuses is left unread instead of compiled into one that throws when it is cast.
    /// </remarks>
    public static bool CanExpand(ImmutableList<TargetSpec> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);

        var blocks = 0;

        for (var i = 0; i < targets.Count; i++)
        {
            if (!targets[i].AnyNumber)
                continue;

            blocks++;

            if (i != targets.Count - 1)
                return false;
        }

        return blocks <= 1;
    }

    /// <summary>
    /// The specs a spell actually has, once the caster has announced how many (CR 601.2c).
    /// </summary>
    /// <remarks>
    /// The block takes whatever is left over after the specs that are not it, and never fewer
    /// than none: a caster who chose no targets for the block gets an empty block, which is what
    /// "any number" permits (CR 601.2c's own Loaming Shaman example resolves with nothing
    /// targeted).
    /// <para>
    /// The copies are required rather than optional, and that is the point of expanding at all.
    /// Optional specs say "the caster may stop here"; these say "the caster stopped here", so
    /// <c>RequireLegalTargets</c> demands a legal target for every one of them and a block whose
    /// creature has since been sacrificed fizzles that share exactly as a named target does
    /// (CR 608.2b).
    /// </para>
    /// </remarks>
    public static ImmutableList<TargetSpec> ExpandSpecs(
        ImmutableList<TargetSpec> targets, int announced)
    {
        ArgumentNullException.ThrowIfNull(targets);

        if (!Present(targets))
            return targets;

        if (!CanExpand(targets))
        {
            throw new InvalidOperationException(
                "A spell that targets any number of things may not be given further targets "
                    + "(CR 601.2c).");
        }

        var block = targets[^1];
        var fixedSpecs = targets.RemoveAt(targets.Count - 1);
        var chosen = Math.Max(0, announced - fixedSpecs.Count);

        return fixedSpecs.AddRange(
            Enumerable.Repeat(block with { AnyNumber = false, Optional = false }, chosen));
    }

    /// <summary>
    /// The targets and effects a spell actually has, once the caster has announced how many.
    /// </summary>
    /// <remarks>
    /// The two go together and are returned together, because they are one answer: a block
    /// expanded to three targets whose effects were expanded to two is a card that asks for a
    /// creature and then ignores it.
    /// </remarks>
    public static (ImmutableList<TargetSpec> Targets, ImmutableList<IEffect> Effects) Expand(
        ImmutableList<TargetSpec> targets, ImmutableList<IEffect> effects, int announced)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(effects);

        if (!Present(targets))
            return (targets, effects);

        var expanded = ExpandSpecs(targets, announced);
        var copies = expanded.Count - (targets.Count - 1);

        var grown = ImmutableList.CreateBuilder<IEffect>();

        foreach (var effect in effects)
        {
            if (effect is not ToEachChosenTarget block)
            {
                grown.Add(effect);
                continue;
            }

            // Each copy in turn, rather than each effect in turn across the copies: a block that
            // pumps and then grants gives the first creature both before it touches the second,
            // which is the order the sentence is written in and the order "up to three target
            // creatures" already resolves in.
            for (var copy = 0; copy < copies; copy++)
            {
                foreach (var inner in block.Effects)
                    grown.Add(EffectTargets.Shift(inner, copy));
            }
        }

        return (expanded, grown.ToImmutable());
    }
}
