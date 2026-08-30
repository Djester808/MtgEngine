using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using MtgEngine.Domain.Enums;
using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Cards;

/// <summary>One sentence of effect text, and what it compiles to.</summary>
public sealed record ParsedPhrase
{
    public ImmutableList<TargetSpec> Targets { get; init; } = [];

    public ImmutableList<IEffect> Effects { get; init; } = [];
}

/// <summary>
/// Reads one sentence of effect text into targets and effects.
/// </summary>
/// <remarks>
/// The single most reused piece of the compiler. "Destroy target creature" is the same sentence
/// whether it is a sorcery, the effect half of an activated ability, or what happens when a
/// creature dies — so it is read here once, and each caller wraps the result in whatever kind of
/// ability it is building. Adding a sentence form here adds it to every kind of ability at once,
/// which is the whole reason cards are compiled rather than written.
/// </remarks>
public static partial class EffectPhrase
{
    /// <summary>Reads a sentence, or several joined by full stops.</summary>
    /// <param name="objectNamedByTrigger">
    /// Whether the trigger these sentences belong to is one whose event carries an object, so that
    /// "that creature" has something to mean. False everywhere else, including every spell: a
    /// spell has no triggering event and a pronoun in one can only refer to a target it named.
    /// </param>
    public static bool TryParse(
        string text, out ParsedPhrase parsed, bool objectNamedByTrigger = false)
    {
        parsed = new ParsedPhrase();

        var effects = ImmutableList.CreateBuilder<IEffect>();
        var targets = ImmutableList.CreateBuilder<TargetSpec>();

        // "You may pay {2}. If you do, draw a card." is three sentences that only mean anything
        // together, so the offer and its branches are paired before the text is split. Everything
        // else in this parser reads one sentence at a time precisely because it can.
        // "Counter target spell unless its controller pays {2}" — the offer goes to the other
        // player, and declining is what makes the spell resolve as written. Read before the
        // ordinary optional payment, whose offer always goes to the caster.
        // "Exile target creature. Return that card to the battlefield under its owner's control
        // at the beginning of the next end step." Two sentences that only mean anything together:
        // the second names what the first exiled, and split apart it is a sentence about a card
        // nothing has identified.
        var slowFlicker = ExileAndReturnLine().Match(text.Trim());
        if (slowFlicker.Success
            && Specs.Parse(slowFlicker.Groups["t"].Value.Trim())
                is { Kind: TargetKind.Permanent } blinked)
        {
            targets.Add(blinked);
            effects.Add(new ExileAndReturnAtEndStep(targets.Count - 1));

            parsed = new ParsedPhrase
            {
                Effects = effects.ToImmutable(),
                Targets = targets.ToImmutable(),
            };

            return true;
        }

        // "Exile the top card of your library. You may play that card this turn." Read before
        // the sentence split, because the second sentence names what the first produced and
        // neither half means anything alone.
        if (ExileAndPlayLine().Match(text.Trim()) is { Success: true } impulse)
        {
            effects.Add(new ExileTopAndMayPlay(
                impulse.Groups["n"].Success ? Number(impulse.Groups["n"].Value) : new Amount(1),
                impulse.Groups["long"].Success));

            // Whatever the card says next is read the ordinary way, exactly as the look-and-take
            // idiom below already is. Anchoring the pair to the end of the line meant one
            // trailing sentence - "If this spell was cast using teamwork, create a Treasure
            // token." - threw the whole match away and left the card unread, which is a reader
            // refusing a shape it understands because of a sentence beside it.
            foreach (var sentence in Sentences(impulse.Groups["after"].Value))
            {
                if (!TryOne(sentence, targets, effects, objectNamedByTrigger))
                    return false;
            }

            parsed = new ParsedPhrase
            {
                Targets = targets.ToImmutable(),
                Effects = effects.ToImmutable(),
            };

            return true;
        }

        if (TryClash(text, effects))
        {
            parsed = new ParsedPhrase { Effects = effects.ToImmutable() };
            return true;
        }

        if (TryFlipCoin(text, effects))
        {
            parsed = new ParsedPhrase { Effects = effects.ToImmutable() };
            return true;
        }

        if (TryRollDice(text, targets, effects, objectNamedByTrigger))
        {
            parsed = new ParsedPhrase
            {
                Targets = targets.ToImmutable(),
                Effects = effects.ToImmutable(),
            };

            return true;
        }

        // Read before the text is split into sentences, because the split happens on ", then"
        // and this phrase is written across one: "exile target creature you control, then return
        // that card to the battlefield". Split, the second half is a sentence about a card that
        // no longer exists, and neither half means anything alone.
        // The same sentence about the source, which is written across the same ", then" and has
        // to be read before the split for the same reason. Kept apart from the targeted form
        // because a card that blinks itself is usually doing it to come back as its other face.
        if (FlickerSelfLine().Match(text.Trim()) is { Success: true } blinking)
        {
            effects.Add(new FlickerSource(blinking.Groups["transformed"].Success));

            parsed = new ParsedPhrase { Effects = effects.ToImmutable() };
            return true;
        }

        if (TryFlicker(text, effects, targets))
        {
            parsed = new ParsedPhrase
            {
                Targets = targets.ToImmutable(),
                Effects = effects.ToImmutable(),
            };

            return true;
        }

        if (TryRevealAndTake(text, effects, targets))
        {
            parsed = new ParsedPhrase
            {
                Targets = targets.ToImmutable(),
                Effects = effects.ToImmutable(),
            };

            return true;
        }

        if (TryUnlessTheyPay(text, effects, targets))
        {
            parsed = new ParsedPhrase
            {
                Targets = targets.ToImmutable(),
                Effects = effects.ToImmutable(),
            };

            return true;
        }

        if (TryOptionalPayment(text, effects, targets, objectNamedByTrigger))
        {
            parsed = new ParsedPhrase
            {
                Targets = targets.ToImmutable(),
                Effects = effects.ToImmutable(),
            };

            return true;
        }

        // "Tap target untapped creature you control. If you do, you gain 2 life." - "if you do"
        // after a **mandatory** action rather than after an offer. Read here, before the splitter,
        // for the same reason the offer is: split, the second sentence is a condition about
        // something the first sentence did and neither half means anything alone.
        //
        // Read *after* the offer readers above, so "you may exile target creature. If you do, …"
        // still reaches the one that knows how to ask.
        var didIt = IfYouDidLine().Match(text.Trim());
        if (didIt.Success)
        {
            var doingTargets = ImmutableList.CreateBuilder<TargetSpec>();
            var doingEffects = ImmutableList.CreateBuilder<IEffect>();
            var thenTargets = ImmutableList.CreateBuilder<TargetSpec>();
            var thenEffects = ImmutableList.CreateBuilder<IEffect>();

            if (TryOne(didIt.Groups["doing"].Value.Trim(), doingTargets, doingEffects)
                && doingEffects.Count > 0
                && TryOne(didIt.Groups["then"].Value.Trim(), thenTargets, thenEffects)
                && thenEffects.Count > 0
                && !doingEffects.Any(FindsItselfByIndex)
                && !thenEffects.Any(FindsItselfByIndex))
            {
                targets.AddRange(doingTargets);

                // The second half's targets come after the first half's, so its effects are
                // shifted past them - the same arithmetic every borrowed parse here needs.
                var offset = targets.Count;
                targets.AddRange(thenTargets);

                effects.Add(new IfItHappened(
                    doingEffects.ToImmutable(),
                    [.. thenEffects.Select(e => EffectTargets.Shift(e, offset))]));

                parsed = new ParsedPhrase
                {
                    Targets = targets.ToImmutable(),
                    Effects = effects.ToImmutable(),
                };

                return true;
            }

            targets.Clear();
            effects.Clear();
        }

        // "Create three 0/1 Eldrazi Spawn creature tokens. They have "Sacrifice ~: Add {C}."" is
        // one instruction printed as two sentences: the second says what the first created. The
        // token reader already takes an ability inline - create ... tokens with "..." - and it
        // refuses a token whose ability it cannot read rather than putting a vanilla creature of
        // the right size on the board, so folding the sentence in reuses that guard as it stands.
        //
        // Done before the splitter for the same reason as the two idioms below it: split apart,
        // "They have ..." has nothing to attach to and the whole line goes unread.
        text = FoldGrantedTokenAbility(text);

        // "Look at the top three cards of your library, then put them back in any order" is one
        // instruction, and ", then" is exactly what the splitter cuts on - so read before it, for
        // the same reason as the idiom below. Split apart, "put them back in any order" has no
        // "them" and the whole line goes unread.
        var arranging = LookAndArrangeLine().Match(text.Trim());
        if (arranging.Success)
        {
            effects.Add(new LookAtTopThenArrange(Number(arranging.Groups["n"].Value)));

            parsed = new ParsedPhrase
            {
                Targets = targets.ToImmutable(),
                Effects = effects.ToImmutable(),
            };

            return true;
        }

        // "Look at the top four cards of your library. Put one of them into your hand and the
        // rest on the bottom of your library in a random order." — one instruction spelled across
        // two sentences. Like the optional payment, it has to be read before the splitter runs,
        // because neither half means anything without the other: "put one of them into your hand"
        // does not say which them.
        if (TryLookAndTake(text, effects, out var afterLook))
        {
            // Whatever the card says next is read the ordinary way. Anchoring the idiom to the
            // end of the line meant a single trailing sentence - "You lose 2 life.", "You may
            // shuffle." - threw away the whole match and left the card unread, which is most of
            // the family: 87 of the 103 printings did not match, and a good share of those
            // differed from one that did only by a clause after it.
            foreach (var sentence in Sentences(afterLook))
            {
                if (!TryOne(sentence, targets, effects, objectNamedByTrigger))
                    return false;
            }

            parsed = new ParsedPhrase
            {
                Targets = targets.ToImmutable(),
                Effects = effects.ToImmutable(),
            };

            return true;
        }

        // "If you control a Demon, you gain 2 life. Otherwise, you lose 1 life." An else branch
        // belongs to the "if" in front of it and to nothing else, so the two sentences are
        // offered as a pair before either is read on its own - the same reason the optional
        // payment and the slow blink are read before the splitter. Alone, "Otherwise, you lose 1
        // life" is an instruction with no question attached to it.
        var sentences = Sentences(text).ToList();

        // Where the previous sentence's effects begin, for the one sentence that repeats them.
        var lastSentenceStart = 0;

        for (var i = 0; i < sentences.Count; i++)
        {
            if (i + 1 < sentences.Count
                && OtherwiseSentence().Match(sentences[i + 1]) is { Success: true } fallback
                && TryConditionalPair(
                    sentences[i],
                    fallback.Groups["effect"].Value,
                    targets,
                    effects,
                    objectNamedByTrigger))
            {
                i++;
                continue;
            }

            // "Each opponent attacking that player does the same." — the Curse family's second
            // sentence, which is not an instruction of its own: "the same" is whatever the
            // sentence before it did, so the wrapper holds a copy of that sentence's effects
            // and re-runs them per attacking opponent. Only when the phrase has chosen no
            // target — repeating an effect that reads a choice made once would re-spend the
            // choice for players who never made it, and no card that prints this targets.
            if (i > 0
                && targets.Count == 0
                && effects.Count > lastSentenceStart
                && DoesTheSameSentence().IsMatch(sentences[i]))
            {
                effects.Add(new RepeatForOpponentsAttackingEnchanted(
                    [.. effects.Skip(lastSentenceStart)]));
                continue;
            }

            lastSentenceStart = effects.Count;

            if (!TryOne(sentences[i], targets, effects, objectNamedByTrigger))
                return false;
        }

        if (effects.Count == 0)
            return false;

        parsed = new ParsedPhrase
        {
            Targets = targets.ToImmutable(),
            Effects = effects.ToImmutable(),
        };
        return true;
    }

    /// <summary>
    /// The clauses of an effect, one at a time.
    /// </summary>
    /// <remarks>
    /// Splitting on the full stop alone leaves "Draw a card, then discard a card" as a single
    /// unreadable sentence made of two the parser already knows. ", then" is the one conjunction
    /// safe to split on: it always joins two complete instructions performed in order (CR 608.2c).
    /// <para>
    /// " and " deliberately is not split. It joins clauses on some cards and parts of one clause
    /// on others — "gets +2/+2 and gains flying" is a single effect on a single target — and a
    /// splitter cannot tell those apart without understanding the halves.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The sentences of an effect, split the way the parser splits them.
    /// </summary>
    /// <remarks>
    /// Public because the work-queue test ranks what the parser cannot read, and it has to cut the
    /// text into the same pieces the parser will. It had its own naive <c>Split('.')</c>, which cut
    /// inside a quoted granted ability and reported the orphaned closing quote as the single
    /// biggest blocker in the corpus — 119 cards of pure measurement noise, sitting at the top of
    /// the queue. A ranking is only worth acting on if it counts the same units the code does.
    /// </remarks>
    public static IEnumerable<string> SentencesOf(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return Sentences(text);
    }

    /// <summary>
    /// Rewrites a granted ability printed as its own sentence into the inline form.
    /// </summary>
    /// <remarks>
    /// A normalisation rather than a new reader: the two spellings mean the same thing and the
    /// inline one is already understood, so the second sentence is folded into the first and
    /// every guard on the way through applies unchanged.
    /// </remarks>
    private static string FoldGrantedTokenAbility(string text)
    {
        var m = GrantedTokenAbilityLine().Match(text);
        return m.Success
            ? m.Groups["head"].Value + " with " + '"' + m.Groups["ability"].Value + '"'
            : text;
    }

    /// <summary>
    /// Aims a run of generated effects at what a "for as long as" sentence names (CR 611.2b).
    /// </summary>
    /// <remarks>
    /// The three verbs that take this duration all name their subject the same two ways — a
    /// target phrase, or a pronoun for the one the sentence before chose — so the split lives
    /// here rather than three times over. Nothing is added to either builder until the phrase has
    /// been read, so a sentence this refuses falls through with no half-built effect behind it.
    /// <para>
    /// A pronoun is read only once something <em>has</em> been targeted. "That creature doesn't
    /// untap" is the tail of "tap target creature", and on a card that targeted nothing those
    /// words mean something this cannot see.
    /// </para>
    /// </remarks>
    private static bool HoldsWhile(
        Match m,
        IReadOnlyList<string> definitionIds,
        ImmutableList<TargetSpec>.Builder targets,
        ImmutableList<IEffect>.Builder effects)
    {
        var who = m.Groups["t"].Value.Trim();
        int index;

        if (Pronouns.Contains(who, StringComparer.OrdinalIgnoreCase))
        {
            if (targets.Count == 0)
                return false;

            index = targets.Count - 1;
        }
        else
        {
            if (Specs.Parse(who) is not { Kind: TargetKind.Permanent } affected)
                return false;

            targets.Add(affected);
            index = targets.Count - 1;
        }

        var until = WhileNamed(m);
        foreach (var id in definitionIds)
            effects.Add(new HoldsWhileSourceHolds(id, until, index));

        return true;
    }

    /// <summary>Which "for as long as" condition a gain-control clause printed (CR 611.2b).</summary>
    /// <remarks>
    /// Three spellings of one mechanism rather than three mechanisms: the effect, the definition
    /// and the sweep that ends it are shared, and only the question differs. "Remains on the
    /// battlefield" asks nothing beyond what every one of them already asks - the source has to
    /// still be there for the effect to mean anything - which is why it is the plain case.
    /// </remarks>
    private static GenerativeEffects.ControlHeldWhile WhileNamed(Match m) =>
        m.Groups["until2"].Success
            ? m.Groups["until2"].Value.StartsWith("tapped", StringComparison.OrdinalIgnoreCase)
                ? GenerativeEffects.ControlHeldWhile.Tapped
                : GenerativeEffects.ControlHeldWhile.OnBattlefield
            : GenerativeEffects.ControlHeldWhile.Controlled;

    /// <summary>
    /// "If [condition], [then]. Otherwise, [else]." — one instruction printed as two sentences.
    /// </summary>
    /// <remarks>
    /// The "if" half is exactly the bare conditional sentence <see cref="TryOne"/> already reads,
    /// and this adds the branch it had nowhere to put. Both halves go through the same readers
    /// everything else does, so whatever the compiler can guard, it can now also alternate; a
    /// condition <see cref="BoardConditions"/> cannot answer leaves the pair unread rather than
    /// defaulting either way, because an else branch that always ran and a then branch that
    /// always ran are two different wrong cards.
    /// <para>
    /// <strong>The two branches are wrapped in one effect on purpose.</strong> CR 608.2c has each
    /// instruction carried out in order, and this engine honours that by handing every top-level
    /// effect the state the one before it left. Two sibling guards - one on the condition, one on
    /// its negation - would therefore ask their question at two different moments, and a then
    /// branch that falsifies its own condition ("if you control a creature, sacrifice it")
    /// would let the else branch fire as well. Nested inside one wrapper they are resolved
    /// against a single state, which is what an "otherwise" means.
    /// </para>
    /// <para>
    /// The wrapper's own condition is a tautology because there is no plain sequencing effect to
    /// use instead, and inventing one to hold two guards would be a second way to spell what
    /// <see cref="OnlyIf"/> already does.
    /// </para>
    /// </remarks>
    private static bool TryConditionalPair(
        string conditional,
        string otherwise,
        ImmutableList<TargetSpec>.Builder targets,
        ImmutableList<IEffect>.Builder effects,
        bool objectNamedByTrigger)
    {
        var opening = ConditionalSentence().Match(conditional.Trim());
        if (!opening.Success
            || BoardConditions.Parse(opening.Groups["cond"].Value.Trim()) is not { } holds)
        {
            return false;
        }

        // Targets go into the caller's list so that each branch's effects record the index they
        // will actually be read at, and are truncated back on failure - the same arithmetic and
        // the same clean-up the "and" splitter does.
        var targetsBefore = targets.Count;

        var then = ImmutableList.CreateBuilder<IEffect>();
        var instead = ImmutableList.CreateBuilder<IEffect>();

        var readThen =
            TryOne(opening.Groups["effect"].Value.Trim(), targets, then, objectNamedByTrigger)
            && then.Count > 0
            && !then.Any(FindsItselfByIndex);

        // The branches are read in printed order and share the caller's target list, so the else
        // branch's "it" and "that creature" find whatever the sentence in front of it named -
        // "target creature gets +3/+1. If it's your turn, that creature gains trample. Otherwise,
        // it gains first strike" is one creature, mentioned three times.
        var chosenSoFar = targets.Count;

        var readElse =
            readThen
            && TryOne(otherwise.Trim(), targets, instead, objectNamedByTrigger)
            && instead.Count > 0
            && !instead.Any(FindsItselfByIndex)

            // …but an else branch may not choose a target of its own. Targets are chosen as the
            // spell is cast and every one of them has to be legal then (CR 601.2c), so a branch
            // that will not run would still make the card uncastable for want of something to
            // aim it at. No corpus card is written that way; refusing costs nothing and keeps
            // the reader from inventing a requirement the printed card does not have.
            && targets.Count == chosenSoFar;

        if (!readElse)
        {
            while (targets.Count > targetsBefore)
                targets.RemoveAt(targets.Count - 1);

            return false;
        }

        effects.Add(new OnlyIf(
            Whatever,
            [
                new OnlyIf(holds, then.ToImmutable()),
                new OnlyIf(
                    (state, abilities, source) => !holds(state, abilities, source),
                    instead.ToImmutable()),
            ]));

        return true;
    }

    /// <summary>A condition that is always met — the wrapper above holds two that are not.</summary>
    private static bool Whatever(GameState state, IAbilitySource abilities, GameObject source) =>
        true;

    private static IEnumerable<string> Sentences(string text)
    {
        foreach (var part in SplitOutsideQuotes(text))
        {
            foreach (var clause in ThenSeparator().Split(part))
            {
                // A clause after "then" often opens with "then" again on cards that print
                // "Do X. Then do Y."; either way the word carries no rules meaning of its own.
                var s = LeadingThen().Replace(clause.Trim(), string.Empty).Trim();
                if (s.Length > 0)
                    yield return s;
            }
        }
    }

    /// <summary>
    /// "You may pay [cost]. If you do, ... If you don't, ..." (CR 601.2b).
    /// </summary>
    /// <remarks>
    /// Both branches are optional and either may be absent, but at least one has to be readable —
    /// an offer with nothing on either side of it is a question with no consequence, and reading
    /// it would put a pointless prompt in front of the player.
    /// <para>
    /// The offer must be the whole effect, not part of one. A card that does something, then
    /// offers a payment, then does something else would need the offer answered mid-effect, and
    /// the engine asks it after the resolution finishes — so those are left unread rather than
    /// resolved in the wrong order.
    /// </para>
    /// </remarks>
    /// <param name="objectNamedByTrigger">
    /// Carried through to the branches, and it had been dropped here. An offer inside a trigger
    /// is still inside that trigger: "whenever a Beast you control enters, you may have it deal 4
    /// damage" and the same sentence without the offer are one question about what "it" means,
    /// and the branches were being read as though there were no trigger at all. Passing it can
    /// only widen what reads — with the flag false a pronoun with nothing targeted resolves to
    /// nothing and the line is refused — except where a reader uses it to refuse, which is what
    /// the optional-damage rewrite does.
    /// </param>
    private static bool TryOptionalPayment(
        string text,
        ImmutableList<IEffect>.Builder effects,
        ImmutableList<TargetSpec>.Builder targets,
        bool objectNamedByTrigger)
    {
        var m = MayPayLine().Match(text.Trim());
        if (!m.Success)
            return false;

        // "You may draw a card" is the same shape with no cost: an offer whose price is nothing.
        // It goes through the same machinery rather than getting a mechanism of its own, because
        // the thing that is hard about both is identical — the answer decides what happens next.
        // {E} is an energy counter, not a mana symbol (CR 107.4c). Parsed as mana it would ask
        // the pool for a colour called E, so it is counted out of the cost and charged
        // separately - the same split the activated-ability cost reader already makes.
        var printedCost = m.Groups["cost"].Success ? m.Groups["cost"].Value : string.Empty;
        var energy = EnergySymbol().Count(printedCost);
        var manaPart = EnergySymbol().Replace(printedCost, string.Empty);

        var cost = manaPart.Length > 0
            ? Mana.ManaCostSpec.Parse(manaPart)
            : Mana.ManaCostSpec.Free;

        // "You may pay 2 life" - a price, not something that happens. Read here rather than
        // through the free branch, which would try to parse "pay 2 life" as an effect and fail,
        // taking the whole offer with it.
        var life = 0;
        if (m.Groups["free"].Success
            && PayLifeOffer().Match(m.Groups["free"].Value.Trim()) is { Success: true } paid)
        {
            life = int.Parse(paid.Groups["n"].Value, CultureInfo.InvariantCulture);
        }

        var ifYouDo = ImmutableList<IEffect>.Empty;
        var ifYouDont = ImmutableList<IEffect>.Empty;

        // With no cost the offer's own sentence is what happens if they accept, so there is no
        // separate "if you do" clause to read.
        if (m.Groups["free"].Success && life == 0)
        {
            if (!TryParse(m.Groups["free"].Value, out var freeBranch, objectNamedByTrigger))
                return false;

            ifYouDo = Hoist(freeBranch, targets);
        }

        if (m.Groups["do"].Success)
        {
            if (!TryParse(m.Groups["do"].Value, out var thenBranch, objectNamedByTrigger))
                return false;

            // Added to whatever the offer itself was, not instead of it. "You may sacrifice a
            // creature. If you do, draw a card" is one offer with two consequences: the free
            // clause is what accepting costs, and this is what it buys. Overwriting meant every
            // card of that shape drew the card without ever sacrificing anything.
            ifYouDo = ifYouDo.AddRange(Hoist(thenBranch, targets));
        }

        if (m.Groups["dont"].Success)
        {
            if (!TryParse(m.Groups["dont"].Value, out var elseBranch, objectNamedByTrigger))
                return false;

            ifYouDont = Hoist(elseBranch, targets);
        }

        if (ifYouDo.IsEmpty && ifYouDont.IsEmpty)
            return false;

        // A free offer has no price to name, so the button would read "Pay " with nothing after
        // it. The offer's own words are what it is asking, so they are what it says.
        var yes = m.Groups["free"].Success
            ? char.ToUpperInvariant(m.Groups["free"].Value[0]) + m.Groups["free"].Value[1..]
            : null;

        effects.Add(new MayPay(
            cost,
            ifYouDo,
            ifYouDont,
            effects.Count,
            YesLabel: yes ?? (energy > 0 ? $"Pay {new string('E', energy)} energy" : null),
            NoLabel: yes is null && energy == 0 ? null : "Decline",
            EnergyCost: energy,
            LifeCost: life));

        return true;
    }

    /// <summary>
    /// Moves a branch's targets into the ability's own list, renumbering the branch's effects.
    /// </summary>
    /// <remarks>
    /// A branch is parsed on its own and numbers its targets from zero, because it does not know
    /// what it is being folded into. The ability declares every target it might use up front —
    /// they are chosen when it is put on the stack (CR 601.2c for a spell, 603.3d for a trigger),
    /// which is long before the offer is answered.
    /// <para>
    /// The guard this replaces refused a targeted branch outright, and it was reasoning from a
    /// limitation that had already been fixed: the offer's targets are captured when the question
    /// is owed, so the branch can still find them after the source has gone. Refusing them cost
    /// every "you may return target card from your graveyard" on the plainest wording there is.
    /// </para>
    /// </remarks>
    private static ImmutableList<IEffect> Hoist(
        ParsedPhrase branch, ImmutableList<TargetSpec>.Builder targets)
    {
        if (branch.Targets.IsEmpty)
            return branch.Effects;

        var offset = targets.Count;
        targets.AddRange(branch.Targets);

        return [.. branch.Effects.Select(e => EffectTargets.Shift(e, offset))];
    }

    /// <summary>"Look at the top N. Put one into your hand and the rest on the bottom." (CR 701.20a)</summary>
    /// <summary>
    /// "Look at the top N cards of your library. You may reveal a [type] card from among them and
    /// put it into your hand..." - the same look, with a filter on what may be taken.
    /// </summary>
    /// <remarks>
    /// The filter names one card type or subtype, read by the vocabulary searching already uses.
    /// Compound filters - "a creature or land card", "a noncreature, nonland card" - name two
    /// things and are left unread rather than collapsed to one of them, which would offer the
    /// player cards the card never said they could take.
    /// </remarks>
    private static bool TryLookAndReveal(
        string text, ImmutableList<IEffect>.Builder effects, out string rest)
    {
        rest = string.Empty;

        var m = LookAndRevealLine().Match(text.Trim());
        if (!m.Success)
            return false;

        rest = m.Groups["after"].Value.Trim();

        // "An Elf, Warrior, or Tyvar card" names three things the card may be any one of. Joined
        // with a separator the filter vocabulary understands rather than collapsed to the first,
        // which would offer cards the printing never named.
        if (JoinedFilter(m.Groups["what"].Value) is not { } filter)
            return false;

        effects.Add(new LookAndTake(
            Number(m.Groups["n"].Value),
            Zone.Hand,
            m.Groups["rest"].Value.Contains("graveyard", StringComparison.OrdinalIgnoreCase)
                ? Zone.Graveyard
                : Zone.Library,
            filter));

        return true;
    }

    private static bool TryLookAndTake(
        string text, ImmutableList<IEffect>.Builder effects, out string rest)
    {
        rest = string.Empty;

        var m = LookAndTakeLine().Match(text.Trim());
        if (!m.Success)
            return TryLookAndReveal(text, effects, out rest);

        rest = m.Groups["after"].Value.Trim();

        effects.Add(new LookAndTake(
            Number(m.Groups["n"].Value),
            m.Groups["where"].Value.Contains("graveyard", StringComparison.OrdinalIgnoreCase)
                ? Zone.Graveyard
                : Zone.Hand,
            m.Groups["rest"].Value.Contains("graveyard", StringComparison.OrdinalIgnoreCase)
                ? Zone.Graveyard
                : Zone.Library));

        return true;
    }

    /// <summary>
    /// Splits on the full stop, but not on one inside quotation marks.
    /// </summary>
    /// <remarks>
    /// A quoted ability is a sentence inside a sentence — <c>Create a 1/1 red Devil creature
    /// token with "When this creature dies, it deals 1 damage to any target."</c> — and splitting
    /// on its full stop tears the instruction in half, leaving a fragment and a lone quotation
    /// mark that nothing can read. It cost every token with an ability, and the outer sentence
    /// looked perfectly well-formed right up to the point it was cut.
    /// </remarks>
    private static IEnumerable<string> SplitOutsideQuotes(string text)
    {
        var start = 0;
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (text[i] != '.' || quoted)
                continue;

            if (i > start)
                yield return text[start..i];

            start = i + 1;
        }

        if (start < text.Length)
            yield return text[start..];
    }

    /// <summary>
    /// "[Do something] to target [thing] unless its controller pays [cost]" (CR 601.2b).
    /// </summary>
    /// <remarks>
    /// The counterspell tax. It is the ordinary optional payment with the offer redirected to the
    /// other player, which is the whole difference — everything else about it, including how the
    /// branch is found again when the answer arrives, is the same machinery.
    /// </remarks>
    private static bool TryUnlessTheyPay(
        string text,
        ImmutableList<IEffect>.Builder effects,
        ImmutableList<TargetSpec>.Builder targets)
    {
        var m = UnlessTheyPayLine().Match(text.Trim());
        if (!m.Success)
            return false;

        if (Specs.Parse(m.Groups["t"].Value) is not { } aimed)
            return false;

        // The consequence is what happens when they *decline*, so it is parsed as the "if you
        // don't" branch of an offer nobody made.
        var sentence = $"{m.Groups["verb"].Value} {m.Groups["t"].Value}";
        var scratchTargets = ImmutableList.CreateBuilder<TargetSpec>();
        var scratchEffects = ImmutableList.CreateBuilder<IEffect>();

        if (!TryOne(sentence, scratchTargets, scratchEffects) || scratchTargets.Count != 1)
            return false;

        // The scratch effects were built against a list of one and index target zero. Read as a
        // whole phrase that is also the real index; read as one sentence among several it is not,
        // so they are shifted the same way every other borrowed parse is.
        var offset = targets.Count;
        targets.Add(aimed);

        effects.Add(new MayPay(
            Mana.ManaCostSpec.Parse(m.Groups["cost"].Value),
            IfYouDo: [],
            IfYouDont: [.. scratchEffects.Select(e => EffectTargets.Shift(e, offset))],
            EffectIndex: effects.Count,
            AskTargetController: offset));

        return true;
    }

    /// <summary>
    /// "Flip a coin. If you win the flip, ... If you lose the flip, ..." (CR 705.2).
    /// </summary>
    /// <remarks>
    /// Read across the whole effect rather than one sentence at a time, because the flip and its
    /// consequences only mean anything together — the same reason the optional payment is.
    /// </remarks>
    /// <summary>
    /// "Clash with an opponent. If you win, ..." (CR 701.30b).
    /// </summary>
    /// <remarks>
    /// The coin flip's twin, and it refuses a branch that targets for the same reason: targets
    /// are chosen as the spell is cast (CR 601.2c), long before the cards are revealed.
    /// </remarks>
    private static bool TryClash(string text, ImmutableList<IEffect>.Builder effects)
    {
        var m = ClashLine().Match(text.Trim());
        if (!m.Success)
            return false;

        var won = ImmutableList<IEffect>.Empty;

        if (m.Groups["won"].Success)
        {
            if (!TryParse(m.Groups["won"].Value, out var branch)
                || !branch.Targets.IsEmpty
                || branch.Effects.Any(FindsItselfByIndex))
            {
                return false;
            }

            won = branch.Effects;
        }

        effects.Add(new Clash(won, effects.Count));
        return true;
    }

    private static bool TryFlipCoin(string text, ImmutableList<IEffect>.Builder effects)
    {
        var m = FlipCoinLine().Match(text.Trim());
        if (!m.Success)
            return false;

        var won = ImmutableList<IEffect>.Empty;
        var lost = ImmutableList<IEffect>.Empty;

        foreach (var (group, isWin) in new[] { ("won", true), ("lost", false) })
        {
            if (!m.Groups[group].Success)
                continue;

            // A branch that needs a target cannot be read: targets are chosen as the spell is
            // cast (CR 601.2c), long before the coin comes down.
            if (!TryParse(m.Groups[group].Value, out var branch)
                || !branch.Targets.IsEmpty
                || branch.Effects.Any(FindsItselfByIndex))
            {
                return false;
            }

            if (isWin)
                won = branch.Effects;
            else
                lost = branch.Effects;
        }

        if (won.IsEmpty && lost.IsEmpty)
            return false;

        effects.Add(new FlipCoin(won, lost, effects.Count));
        return true;
    }

    /// <summary>
    /// "Roll a d20." with its results table, and the sentences before and after it (CR 706).
    /// </summary>
    /// <remarks>
    /// Read across the whole effect like the coin flip, because a roll and its consequences only
    /// mean anything together — CR 706.3b says in as many words that the instruction, the table
    /// and any sentence using the result are one ability. The rows arrive on this line because
    /// <see cref="CardCompiler.Lines"/> folds a results row into the line above it: a row alone
    /// is half a sentence, and the compiler reads whole ones.
    /// <para>
    /// All or nothing. One row this cannot read refuses the whole roll, because a table missing a
    /// row is a different card — a die that only ever does the good rows was printed by nobody.
    /// </para>
    /// </remarks>
    private static bool TryRollDice(
        string text,
        ImmutableList<TargetSpec>.Builder targets,
        ImmutableList<IEffect>.Builder effects,
        bool objectNamedByTrigger)
    {
        var m = RollDieLine().Match(text.Trim());
        if (!m.Success)
            return false;

        // A roll inside quotation marks belongs to an ability this line grants, not to this
        // line; reading across the quote would hand the granted ability's table to the grantor.
        if (m.Groups["before"].Value.Contains('"', StringComparison.Ordinal))
            return false;

        var sides = m.Groups["sides"].Success
            ? int.Parse(m.Groups["sides"].Value, CultureInfo.InvariantCulture)
            : m.Groups["worded"].Value.ToLowerInvariant() switch
            {
                "four" => 4,
                "six" => 6,
                "eight" => 8,
                "ten" => 10,
                "twelve" => 12,
                _ => 20,
            };

        var targetsBefore = targets.Count;
        var effectsBefore = effects.Count;

        void Rewind()
        {
            while (targets.Count > targetsBefore)
                targets.RemoveAt(targets.Count - 1);
            while (effects.Count > effectsBefore)
                effects.RemoveAt(effects.Count - 1);
        }

        // The sentences before the roll are ordinary effects, and they may target: targets are
        // chosen as the spell is cast (CR 601.2c), long before the die comes down, which is
        // exactly why the rows below may only refer back to them.
        //
        // Read sentence by sentence into this line's own builders rather than through TryParse,
        // and that is the whole of the difference: TryParse refuses a phrase that chooses and
        // does nothing, because a line that only chooses is not a line — and "Choose target
        // creature, then roll a d20" is exactly that phrase, with the doing printed in the rows
        // underneath. Sharing the builders is also what keeps the target indices right, since a
        // nested parse numbers its own targets from zero.
        var before = m.Groups["before"].Value.Trim().TrimEnd(',');
        if (before.Length > 0)
        {
            foreach (var sentence in Sentences(before))
            {
                if (!TryOne(sentence, targets, effects, objectNamedByTrigger))
                {
                    Rewind();
                    return false;
                }
            }

            if (effects.Skip(effectsBefore).Any(FindsItselfByIndex))
            {
                Rewind();
                return false;
            }
        }

        // What the rows may refer back to and may not add to. Counted after the preamble rather
        // than before it: a row saying "that creature" points at the target the preamble chose,
        // and measuring from before the preamble read every such row as choosing one of its own
        // — which refused the whole table on every card that names its victim first.
        var chosen = targets.Count;

        var rows = ImmutableList.CreateBuilder<RollBranch>();

        foreach (var (from, to, body) in RollSegments(m.Groups["rest"].Value))
        {
            if (!TryRollBranch(body, targets, chosen, out var branch, objectNamedByTrigger))
            {
                Rewind();
                return false;
            }

            rows.Add(new RollBranch(from, to, branch));
        }

        effects.Add(new RollDice(sides, rows.ToImmutable(), effects.Count));
        return true;
    }

    /// <summary>
    /// Cuts the text after a roll instruction into rows: the striations of a results table
    /// (CR 706.3a), a conditional sentence about the result, or a plain sentence that covers
    /// every result.
    /// </summary>
    /// <remarks>
    /// A malformed piece is returned as a row whose body will not parse rather than being
    /// skipped, so the caller's all-or-nothing rule sees it.
    /// </remarks>
    private static IEnumerable<(int From, int? To, string Body)> RollSegments(string rest)
    {
        foreach (var piece in ResultsRowStart().Split(rest.Trim()))
        {
            var segment = piece.Trim();
            if (segment.Length == 0)
                continue;

            var head = ResultsRowHead().Match(segment);
            if (head.Success)
            {
                var body = segment[head.Length..].Trim();
                var first = int.Parse(head.Groups["from"].Value, CultureInfo.InvariantCulture);

                // "9 or less" covers everything up from 1 (CR 706.3a's single number, said the
                // other way around); "N+" and a bare number are the two ends of the same shape.
                if (head.Groups["less"].Success)
                    yield return (1, first, body);
                else if (head.Groups["to"].Success)
                {
                    yield return (
                        first,
                        int.Parse(head.Groups["to"].Value, CultureInfo.InvariantCulture),
                        body);
                }
                else if (head.Groups["plus"].Success)
                    yield return (first, null, body);
                else
                    yield return (first, first, body);

                continue;
            }

            // Sentences with no row head: each "If the result is N …" sentence is a row of its
            // own, and the plain sentences around them cover every result. Consecutive plain
            // sentences stay together so a template written across two of them is still seen.
            var plain = new List<string>();

            foreach (var sentence in SplitOutsideQuotes(segment))
            {
                var s = sentence.Trim();
                if (s.Length == 0)
                    continue;

                var iffy = ResultConditionSentence().Match(s);
                if (!iffy.Success)
                {
                    plain.Add(s);
                    continue;
                }

                if (plain.Count > 0)
                {
                    yield return (1, null, string.Join(". ", plain) + ".");
                    plain.Clear();
                }

                var n = int.Parse(iffy.Groups["n"].Value, CultureInfo.InvariantCulture);
                var eff = iffy.Groups["eff"].Value.Trim();

                yield return iffy.Groups["dir"].Value.ToLowerInvariant() switch
                {
                    "less" or "lower" => (1, (int?)n, eff),
                    "higher" or "greater" or "more" => (n, null, eff),
                    _ => (n, (int?)n, eff),
                };
            }

            if (plain.Count > 0)
                yield return (1, null, string.Join(". ", plain) + ".");
        }
    }

    /// <summary>Reads one row's effects, refusing anything the settle could not run (CR 706.3a).</summary>
    /// <remarks>
    /// A row may refer back to a target the ability already chose — that is why the caller's
    /// list is shared — but may not choose one of its own: targets are chosen as the spell is
    /// cast (CR 601.2c), long before the die decides whether the row happens.
    /// </remarks>
    private static bool TryRollBranch(
        string body,
        ImmutableList<TargetSpec>.Builder targets,
        int chosenBefore,
        out ImmutableList<IEffect> branch,
        bool objectNamedByTrigger)
    {
        branch = [];

        if (RewriteRollResult(body) is not { } rewritten)
            return false;

        // Whole-text first, so a template written across sentences — "exile the top card of
        // your library. You may play it this turn." — is still seen whole.
        if (TryParse(rewritten, out var parsed, objectNamedByTrigger)
            && parsed.Targets.IsEmpty
            && !parsed.Effects.IsEmpty
            && !parsed.Effects.Any(FindsItselfByIndex))
        {
            branch = parsed.Effects;
            return true;
        }

        // Sentence by sentence against the shared target list, which is what lets "tap that
        // creature" in a row find the creature the preamble targeted.
        var built = ImmutableList.CreateBuilder<IEffect>();

        foreach (var sentence in Sentences(rewritten))
        {
            if (!TryOne(sentence, targets, built, objectNamedByTrigger)
                || targets.Count != chosenBefore)
            {
                while (targets.Count > chosenBefore)
                    targets.RemoveAt(targets.Count - 1);

                return false;
            }
        }

        if (built.Count == 0 || built.Any(FindsItselfByIndex))
            return false;

        branch = built.ToImmutable();
        return true;
    }

    /// <summary>
    /// Rewrites "equal to the result" into the "that many" the vocabulary already reads, or
    /// null when some spelling of the result would be left behind.
    /// </summary>
    /// <remarks>
    /// The number these words name is the roll's result, which the settle hands the branch as
    /// its subject amount — the same channel "that many" reads in a trigger. Rewriting is safe
    /// precisely because it happens only inside a roll's own rows; anywhere else "the result"
    /// stays unread, and a sentence still saying it after the rewrite refuses the row rather
    /// than compiling into an amount of nothing.
    /// </remarks>
    private static string? RewriteRollResult(string body)
    {
        var s = ScryTheResult().Replace(body, "scry that many");
        s = ANumberOfEqualToResult().Replace(s, "that many ${what}");
        s = CardsEqualToResult().Replace(s, "that many cards");
        s = LifeEqualToResult().Replace(s, "that much life");

        return s.Contains("the result", StringComparison.OrdinalIgnoreCase) ? null : s;
    }

    /// <summary>
    /// Whether an effect finds itself again by position in its ability's effect list.
    /// </summary>
    /// <remarks>
    /// <c>MayPay</c>, <c>ChooseAndMove</c> and <c>FlipCoin</c> all defer a question and carry an
    /// index back to themselves, which the engine resolves against the ability's <em>top-level</em>
    /// effects. Nested inside a wrapper — the "if you do" branch of another offer, or a kicked
    /// clause — that index names the wrapper instead, the lookup finds the wrong record or none,
    /// and the effect quietly does nothing.
    /// <para>
    /// Rather than make the locator understand nesting, a branch containing one is refused. The
    /// card goes unread, which is the promise every other template makes: what cannot be run
    /// correctly is not claimed.
    /// </para>
    /// </remarks>
    internal static bool FindsItselfByIndex(IEffect effect) =>
        effect is MayPay or ChooseAndMove or FlipCoin or RollDice;

    /// <summary>
    /// "Target opponent reveals their hand. You choose a card from it. That player discards
    /// that card." (CR 701.16)
    /// </summary>
    /// <remarks>
    /// Read across the whole effect rather than sentence by sentence, for the same reason the
    /// optional payment is: the three sentences are one instruction. "You choose a card from it"
    /// on its own has no hand to choose from, and "that player discards that card" has no card.
    /// </remarks>
    private static bool TryRevealAndTake(
        string text,
        ImmutableList<IEffect>.Builder effects,
        ImmutableList<TargetSpec>.Builder targets)
    {
        var m = RevealAndTakeLine().Match(text.Trim());
        if (!m.Success)
            return false;

        if (Specs.Parse(m.Groups["t"].Value.Trim()) is not { Kind: TargetKind.Player } revealer)
            return false;

        // The kind goes through the shared filter vocabulary, so "an instant or sorcery card" and
        // "a nonland card" are read by the same reader that reads a tutor - and a kind it cannot
        // read leaves the line unread rather than offering the player the whole hand.
        var kind = m.Groups["kind"].Value.Trim();

        // "A card" with no adjective in front of it is the whole hand, and the noun has to come
        // off before the filter vocabulary sees it. Stripping only " card" left the bare word
        // standing, which was then looked up as a filter name and refused - so the plainest
        // wording of the line was the one it could not read, while every narrowed form worked.
        if (kind.Equals("card", StringComparison.OrdinalIgnoreCase))
            kind = string.Empty;
        else if (kind.EndsWith(" card", StringComparison.OrdinalIgnoreCase))
            kind = kind[..^" card".Length];

        if ((kind.Length == 0 ? Abilities.SearchFilters.AnyCard : SearchFilterNamed(kind))
            is not { } picked)
        {
            return false;
        }

        targets.Add(revealer);

        effects.Add(new RevealAndTake(
            targets.Count - 1,
            picked,
            Destination: m.Groups["verb2"].Success ? Zone.Exile : Zone.Graveyard,
            EffectIndex: effects.Count));

        return true;
    }

    /// <summary>
    /// "Target X and each other Y that shares a color with it" — a target and a group described
    /// by comparing against it (CR 608.2h).
    /// </summary>
    /// <remarks>
    /// Radiance and Bile Blight are the same sentence with different comparisons, and both
    /// halves of it are already sentences this parser reads: "destroy target land" and "destroy
    /// all lands" are two readers that exist, and what was missing was only the conjunction and
    /// the comparison between them. So the sentence is cut apart, each half is offered to the
    /// ordinary vocabulary, and the group is told which target to look at — which is why every
    /// verb in the family arrives at once rather than one reader per printed line.
    /// <para>
    /// Nothing is written into the caller's builders until both halves have read. A helper that
    /// half-parses and then returns false leaves the readers below it working on a sentence that
    /// has already contributed effects, and the line after it compiles into something no card
    /// prints.
    /// </para>
    /// </remarks>
    private static bool TryPeerGroup(
        string sentence,
        ImmutableList<TargetSpec>.Builder targets,
        ImmutableList<IEffect>.Builder effects,
        bool objectNamedByTrigger)
    {
        var m = PeerGroupLine().Match(sentence);
        if (!m.Success)
            return false;

        // Only as the first thing on its line. The two halves are parsed into their own builders
        // and joined back together by target index, and the index is the same number in both
        // only when nothing has been targeted yet. No corpus card prints this shape second.
        if (targets.Count > 0)
            return false;

        // The group half is read on its own, so it may only name an owner that half can answer
        // by itself. "All other creatures that player controls" parses perfectly and asks the
        // ability for a subject player — which a spell does not have, so the sweep would find
        // nobody while the card compiled complete. Four corpus cards print such a clause and
        // every one of them carries other unread text as well, so refusing it costs nothing and
        // keeps the reader from growing a hole that nothing would report.
        if (GroupOwnerClause().IsMatch(m.Groups["g"].Value))
            return false;

        var comparison = m.Groups["colour"].Success
            ? PeerFilters.SharesAColour
            : (Func<GameState, IAbilitySource, GameObject, GameObject, Guid, bool>)
                PeerFilters.HasTheSameName;

        var head = m.Groups["head"].Value;
        var tail = m.Groups["tail"].Value;
        var determiner = m.Groups["det"].Value;

        // The printed verb agrees with the whole conjunction, so it is plural; each half on its
        // own needs its own agreement. "Target creature ... get +1/+1" is not a sentence this
        // parser reads, and neither is "all creatures gets +1/+1".
        var aimed = head + "target " + m.Groups["t"].Value + Agreeing(tail, singular: true);

        // "Other" is dropped rather than carried into the group phrase. There it means "not the
        // permanent whose ability this is", and on these cards it means "not the one that was
        // targeted" — Wojek Embermage damages every creature sharing a colour with its target,
        // itself included. The exclusion the card prints is the peer one, supplied below.
        var group = head + determiner + " " + m.Groups["g"].Value + Agreeing(
            tail, singular: !determiner.Equals("all", StringComparison.OrdinalIgnoreCase));

        var aimedTargets = ImmutableList.CreateBuilder<TargetSpec>();
        var aimedEffects = ImmutableList.CreateBuilder<IEffect>();

        if (!TryOne(aimed, aimedTargets, aimedEffects, objectNamedByTrigger)
            || aimedTargets is not [{ Kind: TargetKind.Permanent }])
        {
            return false;
        }

        var groupTargets = ImmutableList.CreateBuilder<TargetSpec>();
        var groupEffects = ImmutableList.CreateBuilder<IEffect>();

        // A group names no target of its own, and exactly one effect: anything else means the
        // half was read as something other than the sweep this sentence describes.
        if (!TryOne(group, groupTargets, groupEffects, objectNamedByTrigger)
            || groupTargets.Count > 0
            || groupEffects.Count != 1)
        {
            return false;
        }

        // Index 0 because the guard above makes the targeted half's spec the first one there is.
        var peered = groupEffects[0] switch
        {
            ToEachPermanent swept => swept with
            {
                What = swept.What with { PeerFilter = PeerFilters.Other(comparison) },
                PeerIndex = 0,
            },
            PumpGroup boosted => boosted with
            {
                What = boosted.What with { PeerFilter = PeerFilters.Other(comparison) },
                PeerIndex = 0,
            },
            _ => (IEffect?)null,
        };

        if (peered is null)
            return false;

        targets.AddRange(aimedTargets);
        effects.AddRange(aimedEffects);
        effects.Add(peered);
        return true;
    }

    /// <summary>One half of a conjunction, with the shared verb made to agree with it.</summary>
    /// <remarks>
    /// Only the two verbs the family prints, and only towards the singular. Adding an "s" to
    /// anything else would be a guess, and a tail this does not recognise is handed on unchanged
    /// so that the half either reads as printed or does not read at all.
    /// </remarks>
    private static string Agreeing(string tail, bool singular)
    {
        if (!singular || PluralVerbTail().Match(tail) is not { Success: true } verb)
            return tail;

        var word = verb.Groups["verb"];
        return tail[..(word.Index + word.Length)] + "s" + tail[(word.Index + word.Length)..];
    }

    /// <summary>
    /// A sentence gated on a fact the cast recorded — bargained, cast using teamwork, or kicked
    /// with one particular kicker cost (CR 702.166c, 702.194b, 702.33f).
    /// </summary>
    /// <returns>
    /// True when the sentence is such a rider at all; <paramref name="read"/> says whether its
    /// inner sentence parsed. A rider whose inner sentence nothing reads leaves the line unread
    /// rather than half-run.
    /// </returns>
    /// <remarks>
    /// "Instead" makes the clause a replacement: everything this phrase has read so far becomes
    /// the other branch, which is exactly the sentence in front of it — every printed "instead"
    /// rider is the second sentence of two. "Unless" is the same branch swap with nothing in the
    /// main arm: the effect happens only when the fact does <em>not</em> hold. And "also" —
    /// "that creature also gains trample" — is the additive relationship the wrapper already
    /// expresses, so the word comes out before the inner readers, which do not know it.
    /// </remarks>
    private static bool TryCastFactRider(
        string sentence,
        ImmutableList<TargetSpec>.Builder targets,
        ImmutableList<IEffect>.Builder effects,
        bool objectNamedByTrigger,
        out bool read)
    {
        read = false;

        string inner;
        string? kickerCost = null;
        var teamwork = false;
        var instead = false;
        var negated = false;

        var leading = LeadingCastFactSentence().Match(sentence);
        if (leading.Success)
        {
            teamwork = leading.Groups["team"].Success;
            kickerCost = leading.Groups["cost"].Success
                ? leading.Groups["cost"].Value.ToUpperInvariant()
                : null;
            inner = leading.Groups["effect"].Value.Trim();

            // "..., instead it deals 3 damage" and "..., destroy that creature instead" are one
            // replacement spelled two ways. The trailing form is anchored to the end so that
            // "instead of putting it into your hand" mid-sentence stays what it is: unread.
            if (inner.StartsWith("instead ", StringComparison.OrdinalIgnoreCase))
            {
                instead = true;
                inner = inner["instead ".Length..].Trim();
            }
            else if (InsteadTail().Match(inner) is { Success: true } tail)
            {
                instead = true;
                inner = inner[..tail.Index].Trim();
            }
        }
        else if (TrailingTeamworkSentence().Match(sentence) is { Success: true } trailing)
        {
            teamwork = true;
            negated = trailing.Groups["unless"].Success;
            inner = trailing.Groups["effect"].Value.Trim();
        }
        else
        {
            return false;
        }

        if (inner.StartsWith("also ", StringComparison.OrdinalIgnoreCase))
        {
            inner = inner[5..].Trim();
        }
        else
        {
            var alsoAt = inner.IndexOf(" also ", StringComparison.OrdinalIgnoreCase);
            if (alsoAt >= 0)
                inner = inner.Remove(alsoAt, " also".Length);
        }

        var scratch = ImmutableList.CreateBuilder<IEffect>();
        if (!TryOne(inner, targets, scratch, objectNamedByTrigger))
            return true;

        var other = ImmutableList<IEffect>.Empty;
        if (instead)
        {
            other = effects.ToImmutable();
            effects.Clear();
        }

        var then = scratch.ToImmutable();
        var main = negated ? ImmutableList<IEffect>.Empty : then;
        var alt = negated ? then : other;

        effects.Add(
            kickerCost is not null ? new IfKickedWith(kickerCost, main, alt)
            : teamwork ? new IfTeamwork(main, alt)
            : new IfBargained(main, alt));

        read = true;
        return true;
    }

    /// <summary>
    /// Rewrites "have [someone] [verb] ..." into the indicative sentence it means.
    /// </summary>
    /// <remarks>
    /// The subject is whatever stands between "have" and the first verb the list knows, so the
    /// whole target vocabulary is reachable without being restated here. Only that verb is
    /// conjugated: "have target creature defending player controls untap and block it" becomes
    /// "... untaps and block it", which the readers below either understand or refuse - and
    /// refusing is the right answer for a sentence whose second verb is still an infinitive.
    /// <para>
    /// The verb list is closed on purpose. "Have" takes a bare infinitive, so the rewrite has to
    /// conjugate, and a verb nobody has checked would be conjugated by guess. A sentence whose
    /// verb is not here stays unread, which is the failure this file prefers.
    /// </para>
    /// </remarks>
    private static string Causative(string sentence)
    {
        var m = CausativeLine().Match(sentence);
        if (!m.Success)
            return sentence;

        var verb = m.Groups["verb"].Value;

        // "Search" is the one verb here whose third person is not the bare "+s", and it is in the
        // list rather than left out so that the sibilant rule is written down once.
        var sibilant = verb.EndsWith("ch", StringComparison.OrdinalIgnoreCase)
            || verb.EndsWith("sh", StringComparison.OrdinalIgnoreCase)
            || verb.EndsWith('s')
            || verb.EndsWith('x')
            || verb.EndsWith('z');

        return m.Groups["who"].Value + " " + verb + (sibilant ? "es" : "s") + m.Groups["rest"].Value;
    }

    private static bool TryOne(
        string sentence,
        ImmutableList<TargetSpec>.Builder targets,
        ImmutableList<IEffect>.Builder effects,
        bool objectNamedByTrigger = false)
    {
        // "Its controller", "that creature's controller" — a player named off the triggering
        // event rather than off a target (CR 603.2), and the same pronoun discipline the object
        // vocabulary uses. Two things have to be true or the phrase names nobody:
        //
        // - Nothing may have been targeted. "Counter target spell. Its controller mills four
        //   cards" is about the spell's controller, and matchers below already read that shape
        //   with the target's controller in mind.
        // - The trigger has to be one whose event carries an object, which is the same
        //   allow-list that decides whether "that creature" may be read at all.
        //
        // Only when both hold is the phrase rewritten into the one word the shared player
        // vocabulary knows. Rewriting rather than refusing is deliberate: a refusal here took
        // thirty-seven cards away from matchers that have their own grammar for these words and
        // had been reading them correctly for months, and a rewrite is invisible to every one of
        // them because it never happens on the sentences they take.
        if (targets.Count == 0 && objectNamedByTrigger)
            sentence = SubjectControllerPhrase().Replace(sentence, SubjectControllerWord, 1);

        // "Have target opponent discard a card" - the causative, which reaches here with the
        // "you may" already taken off by the offer reader below. It is the same instruction as
        // "target opponent discards a card" with the subject demoted to an object of "have", and
        // the corpus prints it that way against a dozen different verbs: get, gain, lose,
        // discard, mill, draw, reveal, sacrifice, become, block, untap, create and fight. Every
        // one of those sentences already had a reader for its indicative form, so the grammar was
        // there and only the word "have" stood in front of it - 39 cards, on rows of the work
        // queue that look nothing alike.
        //
        // Rewritten rather than given its own matcher for the reason the pronoun above is: a
        // matcher would have to restate the whole target vocabulary to say who is doing it, and
        // a dozen verbs times that vocabulary is the product this file exists to avoid.
        //
        // The subject may be a target phrase or the source and nothing else. The first cut let it
        // be anything, which quietly undid a refusal made on purpose: "you may have **it** deal 4
        // damage to target opponent" is Aether Charge, where "it" is the Beast that entered and
        // the only reading available deals the damage from the enchantment. The complete count
        // went up and the card became a different one - and a test written when that refusal was
        // made is what caught it, which is the whole argument for writing the refusals down.
        sentence = Causative(sentence);

        // "Choose target creature an opponent controls" — a sentence that is all choosing and no
        // doing. The choice is made as the spell or ability is put on the stack (CR 601.2c), so
        // the sentence compiles to a target and no effect, and the sentences after it say what
        // happens — "then roll a d20", with rows referring back to "that creature". A line whose
        // later sentences cannot be read still refuses whole, so the target is never left chosen
        // with nothing reading it.
        var pick = ChooseTargetSentence().Match(sentence);
        if (pick.Success)
        {
            if (Specs.Parse(pick.Groups["t"].Value.Trim()) is not { } spec)
                return false;

            targets.Add(spec);
            return true;
        }

        // "If the roll was 4 or higher, it gains menace until end of turn." — a clause of a dice
        // ability's effect, testing the number the trigger carried (CR 706.4). Not a board
        // condition: nothing on the board remembers what was rolled, the trigger's subject
        // amount does, so the guard reads the context where OnlyIf reads the state.
        var wasRolled = RollWasSentence().Match(sentence);
        if (wasRolled.Success)
        {
            var guarded = ImmutableList.CreateBuilder<IEffect>();

            if (!TryOne(wasRolled.Groups["eff"].Value.Trim(), targets, guarded, objectNamedByTrigger)
                || guarded.Count == 0
                || guarded.Any(FindsItselfByIndex))
            {
                return false;
            }

            effects.Add(new OnlyIfRollAtLeast(
                int.Parse(wasRolled.Groups["n"].Value, CultureInfo.InvariantCulture),
                guarded.ToImmutable()));

            return true;
        }

        // Every effect below that takes a target reads the phrase through Specs.Parse rather
        // than matching it: "destroy target creature" and "destroy target artifact an opponent
        // controls" are one effect and two phrases, and pairing each effect with each phrase by
        // hand is what made the vocabulary grow as a product instead of a sum.
        // "Target player draws X cards, where X is the number of creatures you control." The
        // clause is a definition rather than an instruction: it says nothing about what happens,
        // only what X comes to. So the head is read by the ordinary vocabulary with X left as the
        // variable it always is, and the answer is filled in around it when the sentence resolves.
        // Every verb the parser already knows arrives here working, and none of them had to learn
        // that X can be a count.
        //
        // Read first, because the head on its own is a sentence the matchers below would take -
        // and take with X meaning "a number the caster chose", which is a different card.
        //
        // The mutation count comes before the general count, whose wording it starts with: "the
        // number of times ~ has mutated" would otherwise reach the group grammar as a group
        // called "times ~ has mutated", which is nothing, and the whole sentence would go unread.
        var mutations = VariableIsMutationsLine().Match(sentence);
        if (mutations.Success)
        {
            var scratch = ImmutableList.CreateBuilder<IEffect>();

            if (!TryOne(
                    mutations.Groups["head"].Value.Trim(),
                    targets,
                    scratch,
                    objectNamedByTrigger))
            {
                return false;
            }

            effects.Add(new WithCountedVariable(
                context => context.State.TryGetObject(context.PhysicalSourceId, out var self)
                    ? self.TimesMutated
                    : 0,
                scratch.ToImmutable()));

            return true;
        }

        var defining = VariableIsCountLine().Match(sentence);
        if (defining.Success)
        {
            var scratch = ImmutableList.CreateBuilder<IEffect>();

            // Targets go into the caller's list rather than a scratch one, so the indices the
            // head's effects were numbered against stay the indices they will be read at.
            if (!TryOne(
                    defining.Groups["head"].Value.Trim(),
                    targets,
                    scratch,
                    objectNamedByTrigger))
            {
                return false;
            }

            if (CountingAmount(1, "each " + defining.Groups["group"].Value.Trim()) is not { } howMany
                || howMany.Counter is not { } counting)
            {
                return false;
            }

            effects.Add(new WithCountedVariable(counting, scratch.ToImmutable()));
            return true;
        }

        // "Target creature gets +X/+0 until end of turn, where X is its power." The same clause
        // as above measuring one permanent instead of counting a group, and read the same way:
        // the head keeps X as the variable it always is and the clause says what X comes to.
        //
        // What "its" points at is the whole of the difficulty, and it is settled by the shape of
        // the head rather than by guessing, because the corpus disagrees with itself. Onward says
        // it of the creature it targets; Yew Spirit says it of the creature whose ability it is;
        // Murder Investigation is an Aura and says it of the creature that died, which is neither.
        // So:
        //
        //   - one target and it is a permanent  -> that permanent, which is the only thing the
        //     sentence is about;
        //   - no target at all -> the object the trigger was about, and the source when there was
        //     no trigger. That one answer covers both the Aura reading its enchanted creature and
        //     the creature reading itself, because a trigger's subject *is* the source when the
        //     card says "when this creature dies";
        //   - anything else -> unread. Two targets, or a single player target, and the pronoun is
        //     genuinely ambiguous: Dying Wish targets a player and means the enchanted creature,
        //     and reading the player's power would give a card that silently does nothing.
        var byStat = VariableIsStatLine().Match(sentence);
        if (byStat.Success)
        {
            // Only what this head adds. A target left by an earlier sentence on the same line is
            // not something this clause may claim - "Untap target creature. It gets +X/+X" would
            // be a different reading of the pronoun, and one this cannot tell apart.
            if (targets.Count > 0)
                return false;

            var scratch = ImmutableList.CreateBuilder<IEffect>();

            if (!TryOne(byStat.Groups["head"].Value.Trim(), targets, scratch, objectNamedByTrigger))
                return false;

            var stat = byStat.Groups["stat"].Value.ToLowerInvariant();

            Func<ResolutionContext, int> measure;

            if (targets.Count == 0)
            {
                measure = context => StatOfObject(context, SubjectOrSource(context), stat);
            }
            else if (targets is [{ Kind: TargetKind.Permanent }])
            {
                measure = context => StatOfObject(
                    context,
                    context.TargetAt(0) is { } aimed
                    && context.State.TryGetObject(aimed.Subject, out var measured)
                        ? measured
                        : null,
                    stat);
            }
            else
            {
                return false;
            }

            effects.Add(new WithCountedVariable(measure, scratch.ToImmutable()));
            return true;
        }

        // "~ deals 2 damage to target creature and each other creature that shares a color with
        // it" - one sentence about two things: a target, and a group described by looking at
        // that target. Read before the verbs below, because each of them would take the head of
        // this sentence and then choke on the conjunction.
        if (TryPeerGroup(sentence, targets, effects, objectNamedByTrigger))
            return true;

        // "~ deals damage equal to the number of Elves you control to target creature" - the
        // same effect as the sentence below with the count written the other way round, and the
        // difference is only word order: there the amount comes first and the counting trails,
        // here the counting is the amount. Read first because the pattern below would otherwise
        // try to take "damage equal to the number of Elves you control" as a number.
        var byCount = DamageEqualToCountLine().Match(sentence);
        if (byCount.Success
            && Specs.Parse(byCount.Groups["t"].Value) is { } measured
            && CountingAmount(Times(byCount), "each " + byCount.Groups["foreach"].Value.Trim())
                is { } tally)
        {
            targets.Add(measured);
            effects.Add(new DealDamage(tally, targets.Count - 1));
            return true;
        }

        // The same instruction as the reader above with the clauses the other way round: there
        // the count comes before the target, here after it. Two orders, one effect - and the one
        // reader could not take both because each has to know where the target phrase stops.
        var afterTarget = DamageToTargetEqualToCountLine().Match(sentence);
        if (afterTarget.Success
            && Specs.Parse(afterTarget.Groups["t"].Value.Trim()) is { } tallied
            && CountingAmount(Times(afterTarget), afterTarget.Groups["foreach"].Value.Trim())
                is { } perOne)
        {
            targets.Add(tallied);
            effects.Add(new DealDamage(perOne, targets.Count - 1));
            return true;
        }

        var m = DealsDamageLine().Match(sentence);
        if (m.Success && Specs.Parse(m.Groups["t"].Value) is { } burned)
        {
            var much = Number(m.Groups["n"].Value);

            // "~ deals 1 damage to any target for each creature you control" - the same amount
            // taken off the board, through the counting the other verbs already use.
            if (m.Groups["foreach"].Success)
            {
                if (CountingAmount(much, m.Groups["foreach"].Value.Trim()) is not { } perThing)
                    return false;

                much = perThing;
            }

            targets.Add(burned);
            effects.Add(new DealDamage(much, targets.Count - 1));
            return true;
        }

        // "~ deals 2 damage to that creature" - the pronoun names what the sentence before it
        // chose, which is the rule <see cref="ObjectOf"/> already applies for destroy, exile and
        // tap. Damage was the verb that never got it, on thirty-six cards.
        //
        // Only the arm that resolves to a target is honoured. DealDamage carries a target index
        // and nothing else, so a pronoun meaning the *trigger's* subject has nowhere to go here -
        // and answering it with the last target instead is the exact mistake this codebase
        // reverted once already, where "whenever ~ blocks a creature, destroy that creature"
        // destroyed the blocker. Refusing leaves the line unread, which is the safe half.
        //
        // Placed after the target form rather than inside it, so that a phrase the target grammar
        // cannot read still falls through to the readers below as it always did.
        var burnPronoun = PronounObject(m, targets, objectNamedByTrigger);

        if (burnPronoun is { } already)
        {
            if (CountedBy(Number(m.Groups["n"].Value), m.Groups["foreach"]) is not { } dealt)
                return false;

            effects.Add(new DealDamage(dealt, already));
            return true;
        }

        // "Destroy all creatures", "All creatures get -2/-2 until end of turn" — a group, not
        // a target. Read before the targeted forms, because "destroy all creatures" would
        // otherwise reach DestroyLine and be rejected there for having no target phrase.
        m = ToEachLine().Match(sentence);
        if (m.Success
            && Specs.ParseGroup(m.Groups["t"].Value) is { Kind: TargetKind.Permanent } swept)
        {
            effects.Add(new ToEachPermanent(GroupActionOf(m.Groups["verb"].Value), swept));
            return true;
        }

        // "~ deals N damage to each creature and each player." Both halves already have a
        // reading; what was missing was the conjunction between them. The right half is tried as
        // a set of players first and as a second group of permanents second, because "each
        // creature and each planeswalker" is the same sentence with a different second half.
        // "Its controller loses 1 life", after a sentence that countered something. It reads the
        // target the sentence before chose, which is the last one added.
        // "Its controller loses 1 life", "its controller draws a card" - one clause shape with
        // two verbs, so one matcher reads both. A second pattern for the second verb is how the
        // two would drift over which subjects they accept.
        m = TargetControllerLine().Match(sentence);
        if (m.Success && targets.Count > 0)
        {
            // "Its controller loses 1 life for each creature you control" - the same clause with
            // the count the other life verbs already read. Both effects take an Amount, so the
            // tail costs nothing here beyond asking for it.
            if (CountedBy(Number(m.Groups["n"].Value), m.Groups["foreach"]) is not { } many)
                return false;

            var whose = targets.Count - 1;

            effects.Add(m.Groups["mills"].Success
                ? new MillForTargetsController(many, whose)
                : m.Groups["draws"].Success
                    ? new DrawForTargetsController(many, whose)
                    : new ChangeLifeOfTargetsController(
                        m.Groups["verb"].Value.StartsWith(
                            "gain", StringComparison.OrdinalIgnoreCase)
                            ? many
                            : -many,
                        whose));

            return true;
        }

        // "Sacrifice ~ unless you pay {U}" — the upkeep tax that echo already compiles to,
        // written out as a sentence instead of as a keyword. An offer whose only consequence is
        // on the "no" side: paying is how nothing happens.
        // "If that spell is countered this way, exile it instead of putting it into its
        // owner's graveyard" - a rider on the sentence before it, changing where the spell it
        // just countered ends up. Read by rewriting that effect rather than by adding one: two
        // effects would counter the spell and then try to exile a card that is no longer where
        // the first one left it.
        if (ExileCounteredLine().IsMatch(sentence))
        {
            var last = effects.FindLastIndex(e => e is CounterTargetSpell);
            if (last < 0)
                return false;

            effects[last] = ((CounterTargetSpell)effects[last]) with { ToExile = true };
            return true;
        }

        // "Return ~ from your graveyard to the battlefield tapped" - a card that buys itself
        // back. It names no target because there is nothing to aim at: the card doing the
        // returning is the card being returned.
        var selfReturn = ReturnSelfFromGraveyardLine().Match(sentence);
        if (selfReturn.Success)
        {
            effects.Add(new ReturnSourceToBattlefield(selfReturn.Groups["tapped"].Success));
            return true;
        }

        // "~ doesn't untap during your next untap step" - the tail of a mana ability that pays
        // double, and the one form of this sentence with nothing to aim at.
        if (SkipUntapSourceLine().IsMatch(sentence))
        {
            effects.Add(new SkipNextUntapSource());
            return true;
        }

        // "That creature doesn't untap during its controller's next untap step" - the tail of
        // a tap effect, reading the target the sentence before it named.
        var skipping = SkipUntapLine().Match(sentence);
        if (targets.Count > 0 && skipping.Success)
        {
            // "Those creatures" is the plural of the same sentence and means every one the
            // sentence before it named - "tap up to two target creatures" left two behind, and
            // freezing only the last of them is a strictly weaker card. Singular still means the
            // most recent target, which is what "that creature" refers to.
            if (skipping.Groups["many"].Success)
            {
                for (var index = 0; index < targets.Count; index++)
                {
                    if (targets[index].Kind == TargetKind.Permanent)
                        effects.Add(new SkipNextUntap(index));
                }

                return effects.Count > 0;
            }

            effects.Add(new SkipNextUntap(targets.Count - 1));
            return true;
        }

        // "Target creature doesn't untap during its controller's next untap step" - the same
        // sentence standing on its own rather than trailing a tap, so it names its own target.
        var skipTarget = SkipUntapTargetLine().Match(sentence);
        if (skipTarget.Success
            && Specs.Parse(skipTarget.Groups["t"].Value.Trim()) is
            { Kind: TargetKind.Permanent } frozen)
        {
            targets.Add(frozen);
            effects.Add(new SkipNextUntap(targets.Count - 1));
            return true;
        }

        // "Untap them" - the plural of "untap it", and a sentence that says nothing at all on
        // its own: "them" is whatever the sentence before it chose. Two things can have chosen,
        // and both are already sitting in this builder, so the pronoun is resolved from what has
        // been built rather than guessed from the words.
        //
        // Refused when neither is there. A dangling plural pronoun leaves the line unread, the
        // same rule "it" follows: untapping the wrong set is a card that plays wrongly while
        // reading as complete.
        if (UntapThemLine().IsMatch(sentence))
        {
            // The targets first, because a sentence that named targets is unambiguous about
            // what "them" is. Every permanent target, not the last one: "put a +1/+1 counter on
            // up to three target creatures. Untap them" means all three, and untapping only the
            // last is a strictly weaker card.
            var chosen = false;

            for (var index = 0; index < targets.Count; index++)
            {
                if (targets[index].Kind != TargetKind.Permanent)
                    continue;

                effects.Add(new UntapTarget(index));
                chosen = true;
            }

            if (chosen)
                return true;

            // Otherwise the group the sentence before it found - "creatures you control get
            // +1/+1 until end of turn. Untap them." The set is named by re-asking that effect's
            // own filter rather than by remembering which permanents it touched, which is what
            // an untargeted group effect means (CR 609.2) and is the same answer: both are
            // evaluated at the same moment in the same resolution.
            var group = effects.FindLast(e => e is PumpGroup or ToEachPermanent);

            if (group is PumpGroup boosted)
            {
                effects.Add(new ToEachPermanent(GroupAction.Untap, boosted.What));
                return true;
            }

            if (group is ToEachPermanent handled)
            {
                effects.Add(new ToEachPermanent(GroupAction.Untap, handled.What));
                return true;
            }

            return false;
        }

        // "Put a +1/+1 counter on each other creature you control" - the whole board at once,
        // which the group vocabulary already does for destroying and tapping. Only +1/+1 and
        // -1/-1 are offered, matching the group action: a named counter on each of something is
        // rare enough to stay unread rather than to be guessed at.
        var counterEach = CounterOnEachLine().Match(sentence);
        if (counterEach.Success)
        {
            // The group action names the counter it puts, so only the two it can name may be
            // read. The pattern accepts any power/toughness counter and this arm accepted them
            // all, taking nothing from the number but its sign: "put a +4/+4 counter on each
            // creature you control" compiled to a definition byte-identical to the +1/+1 one,
            // read as complete, and did a quarter of what it printed.
            //
            // Refused outright rather than narrowed in the pattern, so the shape is recognised
            // and declined in one place where the reason can be written down.
            if (CounterKindNamed(counterEach.Groups["kind"].Value) is not (
                    CounterKinds.PlusOnePlusOne or CounterKinds.MinusOneMinusOne))
            {
                return false;
            }

            var others = counterEach.Groups["other"].Success;
            var group = counterEach.Groups["t"].Value.Trim();

            if (others)
                group = OtherPrefix().Replace(group, string.Empty).Trim();

            if (Specs.ParseGroup(group) is { Kind: TargetKind.Permanent } each)
            {
                // "Other" is the source leaving itself out, kept as a filter on the spec for the
                // same reason it is on a sacrifice: it is about which object is asking.
                if (others)
                {
                    each = each with
                    {
                        Description = "other " + each.Description,
                        SourceFilter = (_, _, obj, source, _) =>
                            source is null || obj.Id != source.Id,
                    };
                }

                effects.Add(new ToEachPermanent(
                    counterEach.Groups["kind"].Value.StartsWith('-')
                        ? GroupAction.MinusOneCounters
                        : GroupAction.PlusOneCounters,
                    each,
                    Number(counterEach.Groups["n"].Value)));

                return true;
            }
        }

        // "Shuffle and put that card on top" - the tail of a tutor, and a sentence of its own
        // because the splitter separates on ", then". It says where the card the sentence before
        // it found ends up, so it amends that search rather than adding an effect: there is no
        // other way to say "that card", and reading it as a bare shuffle would leave the tutor
        // putting its find in hand.
        if (SearchToTopLine().IsMatch(sentence)
            && effects.Count > 0
            && effects[^1] is SearchLibrary tutored)
        {
            effects[^1] = tutored with { Destination = Zone.Library };
            return true;
        }


        m = ShuffleLine().Match(sentence);
        if (m.Success)
        {
            effects.Add(new ShuffleLibrary(
                PlayerScope.You, GraveyardFirst: m.Groups["yard"].Success));
            return true;
        }

        // "Target player shuffles their graveyard into their library", and the same instruction
        // said of everybody. The shuffle itself is the one the tutors have used for months; only
        // its subject was missing, and the subject was the whole blocker on six cards - five of
        // them naming a target, so the effect grew a target index the way drawing and draining
        // already had one rather than a second effect that shuffles.
        var shuffleWho = ShuffleWhoLine().Match(sentence);
        if (shuffleWho.Success)
        {
            var whose = shuffleWho.Groups["who"].Value;
            var yard = shuffleWho.Groups["yard"].Success;

            if (!whose.StartsWith("target", StringComparison.OrdinalIgnoreCase))
            {
                effects.Add(new ShuffleLibrary(ScopeOf(whose.ToLowerInvariant()), yard));
                return true;
            }

            if (Specs.Parse(whose) is not { Kind: TargetKind.Player } shuffler)
                return false;

            targets.Add(shuffler);
            effects.Add(new ShuffleLibrary(
                PlayerScope.You, yard, TargetIndex: targets.Count - 1));

            return true;
        }

        m = RemoveCounterLine().Match(sentence);
        if (m.Success)
        {
            effects.Add(new PutCountersOnSource(
                m.Groups["kind"].Value, -Number(m.Groups["n"].Value).Fixed));
            return true;
        }

        m = SacrificeUnlessPayLine().Match(sentence);
        if (m.Success)
        {
            effects.Add(new MayPay(
                Mana.ManaCostSpec.Parse(m.Groups["cost"].Value),
                IfYouDo: [],
                IfYouDont: [new SacrificeSource()],
                effects.Count));
            return true;
        }

        if (FogLine().IsMatch(sentence))
        {
            effects.Add(new PreventAllCombatDamage());
            return true;
        }

        m = DamageEachBothLine().Match(sentence);
        if (m.Success
            && Specs.ParseGroup(m.Groups["t"].Value) is { Kind: TargetKind.Permanent } sweptLeft)
        {
            var howMuch = Number(m.Groups["n"].Value);
            var rest = m.Groups["rest"].Value;

            IEffect? second = PlayerWords().IsMatch(rest)
                ? new DamageEach(howMuch, ScopeOf(rest))
                : Specs.ParseGroup(rest) is { Kind: TargetKind.Permanent } sweptRight
                    ? new ToEachPermanent(GroupAction.Damage, sweptRight, howMuch)
                    : null;

            if (second is not null)
            {
                effects.Add(new ToEachPermanent(GroupAction.Damage, sweptLeft, howMuch));
                effects.Add(second);
                return true;
            }
        }

        m = DamageEachPermanentLine().Match(sentence);
        if (m.Success
            && Specs.ParseGroup(m.Groups["t"].Value) is { Kind: TargetKind.Permanent } burned2)
        {
            effects.Add(new ToEachPermanent(
                GroupAction.Damage, burned2, Number(m.Groups["n"].Value)));
            return true;
        }

        // "Creatures you control get +1/+1 and gain vigilance until end of turn." Both halves
        // apply to the same group, so both are made here rather than by splitting the sentence:
        // "and gain vigilance until end of turn" on its own names nobody.
        // Read before the two single-effect matchers, because either of them would otherwise
        // match the head of this sentence and leave the rest of it unread.
        m = MassPumpAndGrantLine().Match(sentence);
        if (m.Success
            && Keywords(m.Groups["kw"].Value) is { } alsoGranted
            && Specs.ParseGroup(m.Groups["t"].Value) is { Kind: TargetKind.Permanent } bothTo)
        {
            effects.Add(new PumpGroup(
                GenerativeEffects.PumpId(Signed(m.Groups["p"].Value), Signed(m.Groups["tough"].Value)),
                bothTo));
            effects.Add(new PumpGroup(GenerativeEffects.GrantId(alsoGranted), bothTo));
            return true;
        }

        // "Each creature you control gains trample until end of turn." The group form of a
        // keyword grant, and it reaches the same machinery a group pump does - a granted keyword
        // is a continuous effect with a generated id exactly as a P/T change is.
        m = MassGrantLine().Match(sentence);
        if (m.Success
            && Keywords(m.Groups["kw"].Value) is { } givenToGroup
            && Specs.ParseGroup(m.Groups["t"].Value) is { Kind: TargetKind.Permanent } grantedTo)
        {
            effects.Add(new PumpGroup(GenerativeEffects.GrantId(givenToGroup), grantedTo));
            return true;
        }

        // "Creatures you control gain \"{T}: Add {G}\" until end of turn" - the group form of the
        // quoted grant, reaching the same machinery the group keyword grant above does.
        if (MassGrantsQuotedAbilityLine().Match(sentence) is { Success: true } massGrant)
        {
            var massText = massGrant.Groups["ability"].Value.Trim();

            if (!CardCompiler.TryQuotedAbility(massText, out _, out _))
                return false;

            if (Specs.ParseGroup(massGrant.Groups["t"].Value.Trim()) is not
                { Kind: TargetKind.Permanent } massTo)
            {
                return false;
            }

            effects.Add(new PumpGroup(GenerativeEffects.GrantAbilityId(massText), massTo));
            return true;
        }

        // The plural "get" needs no group word - "Creatures you control get +1/+1" is already a
        // group by its noun. The singular "gets" does, because without one it is indistinguishable
        // from "Target creature gets +2/+2", which is a different sentence entirely; allowing the
        // optional form to take the singular made this matcher eat every combat trick in the game.
        m = MassPumpLine().Match(sentence);
        if (m.Success
            && Specs.ParseGroup(m.Groups["t"].Value) is { Kind: TargetKind.Permanent } pumpedGroup)
        {
            effects.Add(new PumpGroup(
                GenerativeEffects.PumpId(Signed(m.Groups["p"].Value), Signed(m.Groups["tough"].Value)),
                pumpedGroup));
            return true;
        }

        m = DestroyLine().Match(sentence);
        if (m.Success
            && ObjectOf(m.Groups["t"].Value, targets, objectNamedByTrigger) is { } doomed)
        {
            effects.Add(new DestroyTarget(doomed.Index, Subject: doomed.Subject));
            return true;
        }

        // Reanimation and graveyard hate. Read before the battlefield forms, because "exile
        // target creature card from your graveyard" would otherwise reach ExileLine, whose target
        // grammar looks on the battlefield and would find nothing.
        // "Return all creature cards from your graveyard to your hand." Read before the
        // targeted form, whose grammar starts at "target" and would not reach this at all.
        m = AllFromGraveyardLine().Match(sentence);
        if (m.Success
            && Specs.Parse("target " + m.Groups["what"].Value.Trim() + " card in your graveyard")
                is { Kind: TargetKind.CardInGraveyard } gathered)
        {
            effects.Add(new MoveGraveyardGroup(
                gathered,
                m.Groups["verb"].Value.StartsWith("exile", StringComparison.OrdinalIgnoreCase)
                    ? Zone.Exile
                    : GraveyardDestination(m.Groups["where"].Value),
                ScopeOf(m.Groups["whose"].Value)));
            return true;
        }

        // "Return a creature card from your graveyard to your hand" - a choice made on
        // resolution rather than a target chosen on casting (CR 609.4), and the same choice
        // machinery a sacrifice uses, asked of the graveyard instead of the battlefield.
        var raise = ChooseFromGraveyardLine().Match(sentence);
        var raisedKind = raise.Groups["what"].Value.Trim();
        if (raise.Success
            && Specs.Parse(
                raisedKind.Length == 0
                    ? "target card in your graveyard"
                    : $"target {raisedKind} card in your graveyard")
                is { Kind: TargetKind.CardInGraveyard } raised)
        {
            effects.Add(new ChooseAndMove(
                raised,
                raise.Groups["verb"].Value.StartsWith("exile", StringComparison.OrdinalIgnoreCase)
                    ? Zone.Exile
                    : Zone.Hand,
                MoveCause.Return,
                effects.Count,
                From: Zone.Graveyard));

            return true;
        }

        m = FromGraveyardLine().Match(sentence);
        if (m.Success && Specs.Parse(m.Groups["t"].Value) is
            { Kind: TargetKind.CardInGraveyard } exhumed)
        {
            targets.Add(exhumed);
            var destination = m.Groups[1].Value.Equals("exile", StringComparison.OrdinalIgnoreCase)
                ? Zone.Exile
                : GraveyardDestination(m.Groups["where"].Value);

            effects.Add(new MoveTargetedCard(
                destination,
                targets.Count - 1,
                UnderYourControl: m.Groups["mine"].Value.Contains(
                    "your", StringComparison.OrdinalIgnoreCase)));
            return true;
        }

        m = ExileLine().Match(sentence);
        if (m.Success
            && ObjectOf(m.Groups["t"].Value, targets, objectNamedByTrigger) is { } banished)
        {
            effects.Add(new ExileTarget(banished.Index, banished.Subject));
            return true;
        }

        // "Look at target opponent's hand" - one player sees it, not the table.
        if (LookAtHandLine().Match(sentence) is { Success: true } peeking
            && Specs.Parse(peeking.Groups["t"].Value.Trim() + " " + peeking.Groups["kind"].Value) is
            { Kind: TargetKind.Player } peeked)
        {
            targets.Add(peeked);
            effects.Add(new LookAtHand(targets.Count - 1));
            return true;
        }

        m = RevealHandLine().Match(sentence);
        if (m.Success)
        {
            if (m.Groups["t"].Success && Specs.Parse(m.Groups["t"].Value) is
                { Kind: TargetKind.Player } shown)
            {
                targets.Add(shown);
                effects.Add(new RevealHand(targets.Count - 1));
                return true;
            }

            if (m.Groups["who"].Success)
            {
                effects.Add(new RevealHand(Scope: ScopeOf(m.Groups["who"].Value)));
                return true;
            }
        }

        m = OntoLibraryLine().Match(sentence);

        // "Your library" is only the same library as "its owner's" when the card was already
        // yours, and the effect files it under its owner. A card in your graveyard is owned by
        // you - that is where cards go - so the phrase is read exactly when the target says so,
        // and refused otherwise rather than posting an opponent's permanent into your deck.
        if (m.Success
            && m.Groups["whose"].Value.Equals("your", StringComparison.OrdinalIgnoreCase)
            && !m.Groups["t"].Value.Contains("your graveyard", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (m.Success && Specs.Parse(m.Groups["t"].Value) is { } tucked)
        {
            targets.Add(tucked);
            effects.Add(new PutTargetOnLibrary(
                targets.Count - 1,
                m.Groups["where"].Value.Contains("bottom", StringComparison.OrdinalIgnoreCase)
                    ? ZonePosition.Bottom
                    : ZonePosition.Top));
            return true;
        }

        m = ReturnToHandLine().Match(sentence);
        if (m.Success && Specs.Parse(m.Groups["t"].Value) is { } bounced)
        {
            targets.Add(bounced);
            effects.Add(new ReturnToHand(targets.Count - 1));
            return true;
        }

        m = CounterSpellLine().Match(sentence);
        if (m.Success && Specs.Parse(m.Groups["t"].Value) is { } countered)
        {
            targets.Add(countered);
            effects.Add(new CounterTargetSpell(targets.Count - 1));
            return true;
        }

        m = TapOrUntapLine().Match(sentence);
        if (m.Success)
        {
            var untapping = m.Groups["verb"].Value.StartsWith("un", StringComparison.OrdinalIgnoreCase);

            // Untapping has no subject of its own yet, so a pronoun there is still refused -
            // reading it as a target would untap whatever the player last chose. A phrase this
            // cannot read falls through to the matchers after it rather than failing the
            // sentence: "untap it" is read further down, and returning here took the whole card
            // with it.
            if (untapping)
            {
                if (Specs.Parse(m.Groups["t"].Value) is { } handled)
                {
                    targets.Add(handled);
                    effects.Add(new UntapTarget(targets.Count - 1));
                    return true;
                }
            }
            else if (ObjectOf(m.Groups["t"].Value, targets, objectNamedByTrigger) is { } tapped)
            {
                effects.Add(new TapTarget(tapped.Index, tapped.Subject));
                return true;
            }
        }

        // ---- Effects on a group of players rather than a target -------------------------
        //
        // "Each opponent discards a card" names nobody; it is not a targeted effect and must not
        // become one, because a targeted version would be stopped by hexproof and the card is not.
        // "Discard your hand", "each player discards their hand" - all of it, however many
        // that is, which is a different number for each player being asked.
        var dumping = DiscardHandLine().Match(sentence);
        if (dumping.Success)
        {
            effects.Add(new DiscardCards(
                0, ScopeOf(dumping.Groups["who"].Value), WholeHand: true));

            return true;
        }

        // "…, then draw that many cards" — the second half of Tolarian Winds, arriving as its own
        // sentence because the splitter cuts on ", then". It is read by folding the two into one
        // effect rather than by adding a draw beside the discard: "that many" is the size of a
        // hand that no longer exists once the discard has resolved. Accepted only directly after
        // a whole-hand discard, so a stray "draw that many cards" with nothing to count stays
        // unread instead of drawing zero.
        if (DrawThatManyLine().IsMatch(sentence)
            && effects.Count > 0
            && effects[^1] is DiscardCards { WholeHand: true, TargetIndex: null } emptied)
        {
            effects[^1] = new DiscardHandThenDraw(emptied.Scope);
            return true;
        }

        m = DiscardLine().Match(sentence);
        if (m.Success)
        {
            if (CountedBy(Number(m.Groups["n"].Value), m.Groups["foreach"]) is not { } discarding)
                return false;

            effects.Add(new DiscardCards(
                discarding,
                ScopeOf(m.Groups["who"].Value),
                AtRandom: m.Groups["random"].Success));

            return true;
        }

        // "Exile target player's graveyard" — the zone is the target, not the cards in it.
        var raked = ExileGraveyardLine().Match(sentence);
        if (raked.Success
            && Specs.Parse(raked.Groups["t"].Value.Trim()) is { Kind: TargetKind.Player } robbed)
        {
            targets.Add(robbed);
            effects.Add(new ExileGraveyard(targets.Count - 1));
            return true;
        }

        // "~ fights target creature", "target creature you control fights target creature you
        // don't control" — one verb, two shapes, and the only difference is whether the first
        // fighter is the source or something the card also targets.
        var brawl = FightLine().Match(sentence);
        if (brawl.Success)
        {
            int? mineIndex = null;

            if (brawl.Groups["mine"].Success)
            {
                if (Specs.Parse(brawl.Groups["mine"].Value.Trim()) is not
                    { Kind: TargetKind.Permanent } myFighter)
                {
                    return false;
                }

                targets.Add(myFighter);
                mineIndex = targets.Count - 1;
            }
            else if (brawl.Groups["pronoun"].Success && targets.Count > 0)
            {
                // The same order of answers the pumps use for the same word: the target the
                // sentence before it chose, and the permanent with the ability when there was
                // none. Left as null in the second case, which is what makes the source fight -
                // "Whenever ~ attacks, it fights target creature defending player controls" is
                // one card and "Target creature you control gets +1/+2. It fights ..." is the
                // other, and both spell the first fighter "it".
                //
                // Refused rather than guessed when the earlier target is a player: a fight is
                // between two creatures (CR 701.14a), and aiming one half at somebody who cannot
                // fight would compile a card that quietly does half of what it prints.
                if (targets[^1].Kind is not TargetKind.Permanent)
                    return false;

                mineIndex = targets.Count - 1;
            }

            if (Specs.Parse(brawl.Groups["theirs"].Value.Trim()) is not
                { Kind: TargetKind.Permanent } theirFighter)
            {
                return false;
            }

            targets.Add(theirFighter);
            effects.Add(new Fight(targets.Count - 1, mineIndex));
            return true;
        }

        // "Sacrifice another creature" — you choosing one of your own, which is what the free
        // half of "you may sacrifice a creature. If you do, ..." says. The offer machinery has
        // always read that half as an ordinary effect and there was no ordinary effect for it,
        // so 178 cards stopped on a sentence the engine could otherwise run: choosing a
        // permanent and moving it is what an edict already does.
        var sacrificeOwn = SacrificeOwnLine().Match(sentence);
        if (sacrificeOwn.Success
            && Specs.Parse("target " + sacrificeOwn.Groups["what"].Value.Trim() + " you control")
                is { Kind: TargetKind.Permanent } mine)
        {
            // "Another" is the source excluding itself. Left as a filter on the spec rather than
            // as a word the noun grammar has to know, because it is about which object is asking
            // and nothing else in a target phrase is.
            if (sacrificeOwn.Groups["another"].Success)
            {
                mine = mine with
                {
                    Description = "another " + mine.Description,
                    SourceFilter = (_, _, obj, source, _) => source is null || obj.Id != source.Id,
                };
            }

            effects.Add(new ChooseAndMove(
                mine,
                Zone.Graveyard,
                MoveCause.Sacrifice,
                effects.Count,
                PlayerScope.You));

            return true;
        }

        // "Target player sacrifices a creature" — the same choice an edict makes of a group,
        // asked of one named player instead.
        var toldToSacrifice = TargetSacrificeLine().Match(sentence);
        if (toldToSacrifice.Success
            && Specs.Parse(toldToSacrifice.Groups["t"].Value.Trim()) is
            { Kind: TargetKind.Player } victim
            && Specs.Parse("target " + toldToSacrifice.Groups["what"].Value.Trim()) is
            { Kind: TargetKind.Permanent } given)
        {
            targets.Add(victim);
            effects.Add(new ChooseAndMove(
                given,
                Zone.Graveyard,
                MoveCause.Sacrifice,
                effects.Count,
                PlayerScope.You,
                TargetIndex: targets.Count - 1));

            return true;
        }

        // "~ deals damage equal to its power to any target" — the number is the source's power
        // as the effect resolves, not as the card was printed, so a pumped creature deals more.
        //
        // "It deals damage equal to its power" is the same sentence with the source named by a
        // pronoun, and it is read only where the pronoun can mean nothing else. Both the damage's
        // source and the power measured are `PhysicalSourceId` — one effect, one permanent — so a
        // pronoun pointing anywhere else cannot be honoured here at all:
        //
        // - after an earlier target ("Tap target creature. It deals damage equal to its power to
        //   its controller") the pronoun is that creature, and this would deal the wrong
        //   creature's power from the wrong source;
        // - inside a trigger whose event names an object ("whenever another Demon you control
        //   enters, it deals damage equal to its power to any target") it is that object.
        //
        // Both are refused rather than approximated, which leaves the shapes where "it" is the
        // permanent with the ability: a self-referring trigger, and an activated ability.
        var byPower = DamageByPowerLine().Match(sentence);
        if (byPower.Success
            && (!byPower.Groups["pronoun"].Success || (targets.Count == 0 && !objectNamedByTrigger)))
        {
            var burnt = byPower.Groups["t"].Value.Trim();

            // "~ deals damage equal to its power to each opponent" — a sentence that does not say
            // "target" targets nothing (CR 115.1a), so the group is asked about before the target
            // grammar rather than after it.
            if (PlayerWords().IsMatch(burnt))
            {
                effects.Add(new DamageEach(SourcePower(), ScopeOf(burnt)));
                return true;
            }

            if (Specs.Parse(burnt) is { } burned3)
            {
                targets.Add(burned3);
                effects.Add(new DealDamage(SourcePower(), targets.Count - 1));
                return true;
            }
        }

        // "Target creature deals damage to itself equal to its power" - a fight with one
        // participant. Built on the same effect rather than a new one: the dealer and the
        // recipient are the same target, which is a thing Fight already handles because it looks
        // both up by index and nothing says the two indices must differ.
        var selfBite = SelfBiteLine().Match(sentence);
        if (selfBite.Success
            && Specs.Parse(selfBite.Groups["t"].Value.Trim()) is
            { Kind: TargetKind.Permanent } selfBitten)
        {
            targets.Add(selfBitten);
            var both = targets.Count - 1;
            effects.Add(new Fight(both, both, BothWays: false));
            return true;
        }

        // "Target creature you control deals damage equal to its power to target creature an
        // opponent controls" - half a fight, and read as one: the damage goes one way and
        // nothing comes back, but whose power and how it is computed are the same question.
        var bite = BiteLine().Match(sentence);
        if (bite.Success)
        {
            var dealer = bite.Groups["mine"].Success
                ? Specs.Parse(bite.Groups["mine"].Value.Trim())
                : null;

            if ((bite.Groups["mine"].Success && dealer is not { Kind: TargetKind.Permanent })
                || Specs.Parse(bite.Groups["theirs"].Value.Trim()) is not
                { Kind: TargetKind.Permanent } bitten)
            {
                return false;
            }

            int? mineIndex2 = null;
            if (dealer is not null)
            {
                targets.Add(dealer);
                mineIndex2 = targets.Count - 1;
            }

            targets.Add(bitten);
            effects.Add(new Fight(targets.Count - 1, mineIndex2, BothWays: false));
            return true;
        }

        // "Target creature gets +1/+1 until end of turn for each creature you control" — the
        // size is not known until it applies, so the group rides in the effect's id and is
        // counted by the layer rather than by the compiler.
        var perPump = PerEachPumpLine().Match(sentence);
        if (perPump.Success
            && Specs.Parse(perPump.Groups["t"].Value.Trim()) is { } perPumped
            && Counting(perPump.Groups["group"].Value.Trim(), hasSource: false) is not null)
        {
            targets.Add(perPumped);
            effects.Add(new PumpUntilEndOfTurn(
                GenerativeEffects.PerEachPumpId(
                    Signed(perPump.Groups["p"].Value),
                    Signed(perPump.Groups["tough"].Value),
                    perPump.Groups["group"].Value.Trim()),
                targets.Count - 1));

            return true;
        }

        // "If it's paired with a creature, that creature also gets +2/+2 until end of turn" —
        // Joint Assault's second sentence, the one spell that consults the soulbond pairing.
        // "It" is the creature an earlier sentence targeted, so the sentence is read only after
        // a target exists and never inside a trigger that names its own object; "that creature"
        // is the target's partner, found when the spell resolves rather than chosen
        // (CR 702.95b), which is why it is not a second target.
        var alsoPaired = PairedAlsoPumpLine().Match(sentence);
        if (alsoPaired.Success && targets.Count > 0 && !objectNamedByTrigger)
        {
            effects.Add(new PumpPairedPartner(
                GenerativeEffects.PumpId(
                    Signed(alsoPaired.Groups["p"].Value), Signed(alsoPaired.Groups["tough"].Value)),
                targets.Count - 1));

            return true;
        }

        // "Put the top three cards of your library into your graveyard" — milling, spelled the
        // long way round. The engine has had the effect since before the word existed on cards.
        var selfMill = PutTopIntoGraveyardLine().Match(sentence);
        if (selfMill.Success)
        {
            effects.Add(new MillCards(
                Number(selfMill.Groups["n"].Value), ScopeOf(selfMill.Groups["who"].Value)));

            return true;
        }

        // "Tap or untap target permanent" — a binary choice on resolution, which is what a free
        // optional payment is. The two answers are given words so the player is asked which they
        // want rather than being asked to "Pay ?".
        var either = TapOrUntapChoiceLine().Match(sentence);
        if (either.Success
            && Specs.Parse(either.Groups["t"].Value.Trim()) is
            { Kind: TargetKind.Permanent } swung)
        {
            targets.Add(swung);
            var index = targets.Count - 1;

            effects.Add(new MayPay(
                Mana.ManaCostSpec.Free,
                IfYouDo: [new TapTarget(index)],
                IfYouDont: [new UntapTarget(index)],
                EffectIndex: effects.Count,
                YesLabel: "Tap it",
                NoLabel: "Untap it"));

            return true;
        }

        m = TargetMillLine().Match(sentence);
        if (m.Success
            && Specs.Parse(m.Groups["t"].Value.Trim()) is { Kind: TargetKind.Player } milled)
        {
            if (CountedBy(Number(m.Groups["n"].Value), m.Groups["foreach"]) is not { } many)
                return false;

            targets.Add(milled);
            effects.Add(new MillCards(many, TargetIndex: targets.Count - 1));

            return true;
        }

        m = MillLine().Match(sentence);
        if (m.Success)
        {
            if (CountedBy(Number(m.Groups["n"].Value), m.Groups["foreach"]) is not { } milling)
                return false;

            effects.Add(new MillCards(milling, ScopeOf(m.Groups["who"].Value)));
            return true;
        }

        m = ExileTopLine().Match(sentence);
        if (m.Success)
        {
            effects.Add(new ExileFromTopOfLibrary(
                Number(m.Groups["n"].Value), ScopeOf(m.Groups["who"].Value)));
            return true;
        }

        m = EachLifeLine().Match(sentence);
        if (m.Success)
        {
            var losing = m.Groups["verb"].Value.StartsWith("lose", StringComparison.OrdinalIgnoreCase);
            var amount = Number(m.Groups["n"].Value);
            effects.Add(new ChangeLifeOfEach(
                losing ? -amount : amount, ScopeOf(m.Groups["who"].Value)));

            // "Each opponent loses 2 life and you gain 2 life" is one printed sentence and two
            // effects; the tail is optional and read here rather than split, because " and " is
            // not safe to split on in general.
            if (m.Groups["gain"].Success)
                effects.Add(new ChangeLife(Number(m.Groups["gain"].Value)));

            return true;
        }

        // "Each player draws a card." The targeted form has been read for a long time; the
        // group form is a different sentence and shares nothing but the verb.
        m = DrawEachLine().Match(sentence);
        if (m.Success)
        {
            effects.Add(new DrawEach(
                Number(m.Groups["n"].Value), ScopeOf(m.Groups["who"].Value)));
            return true;
        }

        m = DamageEachLine().Match(sentence);
        if (m.Success)
        {
            effects.Add(new DamageEach(
                Number(m.Groups["n"].Value), ScopeOf(m.Groups["who"].Value)));
            return true;
        }

        // "Target creature can't block this turn" — a keyword granted until end of turn, not a
        // rule of its own. The engine already refuses a block by anything carrying the flag, so
        // this is the same effect as any other combat trick (CR 613.1f, layer 6).
        // "Creatures without flying can't block this turn" - the group form, reaching the same
        // machinery the single-target one does. A granted keyword is a continuous effect with a
        // generated id whether it lands on one creature or on a board full of them.
        m = CantLine().Match(sentence);
        if (m.Success && Specs.ParseGroup(m.Groups["t"].Value) is
            { Kind: TargetKind.Permanent } restrainedGroup
            && Specs.Parse(m.Groups["t"].Value) is null)
        {
            var groupFlag = m.Groups["what"].Value.StartsWith(
                "be blocked", StringComparison.OrdinalIgnoreCase)
                ? KeywordAbility.CantBeBlocked
                : KeywordAbility.CantBlock;

            effects.Add(new PumpGroup(GenerativeEffects.GrantId(groupFlag), restrainedGroup));
            return true;
        }

        // "That creature can't block this turn" - the pronoun form, which names whatever the
        // sentence before it was about rather than anything targeted here.
        if (m.Success
            && Pronouns.Contains(m.Groups["t"].Value.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var pronounFlag = m.Groups["what"].Value.StartsWith(
                "be blocked", StringComparison.OrdinalIgnoreCase)
                ? KeywordAbility.CantBeBlocked
                : KeywordAbility.CantBlock;

            effects.Add(targets.Count > 0
                ? new PumpUntilEndOfTurn(
                    GenerativeEffects.GrantId(pronounFlag), targets.Count - 1)
                : new PumpUntilEndOfTurn(
                    GenerativeEffects.GrantId(pronounFlag),
                    Subject: EffectSubject.TriggeringObject));

            return true;
        }

        if (m.Success && Specs.Parse(m.Groups["t"].Value) is { } restrained)
        {
            var flag = m.Groups["what"].Value.StartsWith("be blocked", StringComparison.OrdinalIgnoreCase)
                ? KeywordAbility.CantBeBlocked
                : KeywordAbility.CantBlock;

            targets.Add(restrained);
            effects.Add(new PumpUntilEndOfTurn(
                GenerativeEffects.GrantId(flag), targets.Count - 1));
            return true;
        }

        // "Return ~ to its owner's hand" — the source, not a target. It reads as a bounce of
        // something else until you notice the tilde, which is why it is matched before the
        // general form rather than after it.
        if (ReturnSelfToHand().IsMatch(sentence))
        {
            effects.Add(new ReturnSourceFromBattlefield());
            return true;
        }

        // ---- Tokens (CR 111.1) -----------------------------------------------------------
        // "You gain 2 life for each creature you control", "draw cards equal to the number of
        // Islands you control" — one shape wearing two wordings, and both are a fixed number
        // times a count of the board.
        var perEach = PerEachLifeLine().Match(sentence);
        if (perEach.Success)
        {
            // "Life equal to the number of X" is one per thing with the number left out.
            var each = perEach.Groups["n"].Success
                ? Number(perEach.Groups["n"].Value)
                : Times(perEach);

            // "Life equal to that creature's toughness" measures one permanent rather than
            // counting a group, and the permanent is whatever the trigger was about. Last known
            // information when it has gone (CR 608.2g), which is the ordinary case: the commonest
            // printing of this is on a creature dying.
            var gained = perEach.Groups["stat"].Success
                ? StatOfTriggerSubject(perEach.Groups["stat"].Value)
                : CountingAmount(each, perEach.Groups["t"].Value);

            if (gained is not { } gainedAmount)
                return false;

            var losing = perEach.Groups["verb"].Value.StartsWith(
                "lose", StringComparison.OrdinalIgnoreCase);

            var amount = losing ? Amount.Negate(gainedAmount) : gainedAmount;

            // "Each opponent loses 1 life for each X" - the same amount asked of somebody else,
            // and the scope vocabulary already knows who.
            effects.Add(new ChangeLife(amount)
            {
                Scope = ScopeOf(perEach.Groups["who"].Value),
            });

            return true;
        }

        var perEachDraw = PerEachDrawLine().Match(sentence);
        if (perEachDraw.Success)
        {
            // "Draw a card for each X" says how many per thing; "draw cards equal to the number
            // of X" says one per thing and leaves the number out. The same amount either way.
            var per = perEachDraw.Groups["n"].Success
                ? Number(perEachDraw.Groups["n"].Value)
                : Times(perEachDraw);

            if (CountingAmount(per, perEachDraw.Groups["t"].Value) is not { } drawn)
                return false;

            effects.Add(new DrawCards(drawn));
            return true;
        }

        // "~ deals 2 damage to any target and 2 damage to you" - two damages in one sentence,
        // and the amounts differ as often as they match. Read whole rather than split on the
        // "and", because the second half is not a sentence: "2 damage to you" has no verb and
        // nothing would take it.
        var twoDamages = TwoPartDamageLine().Match(sentence);
        if (twoDamages.Success && Specs.Parse(twoDamages.Groups["t1"].Value.Trim()) is { } hurt)
        {
            var second = twoDamages.Groups["t2"].Value.Trim();

            targets.Add(hurt);
            var firstIndex = targets.Count - 1;

            // "And 2 damage to you" names the caster, who is not a target - a spell that damages
            // you does not target you, and reading it as a target would let it be redirected.
            if (string.Equals(second, "you", StringComparison.OrdinalIgnoreCase))
            {
                effects.Add(new DealDamage(Number(twoDamages.Groups["a"].Value), firstIndex));
                effects.Add(new DamageEach(
                    Number(twoDamages.Groups["b"].Value), PlayerScope.You));

                return true;
            }

            if (Specs.Parse(second) is { } alsoHurt)
            {
                targets.Add(alsoHurt);
                effects.Add(new DealDamage(Number(twoDamages.Groups["a"].Value), firstIndex));
                effects.Add(new DealDamage(
                    Number(twoDamages.Groups["b"].Value), targets.Count - 1));

                return true;
            }

            // The first target was added and nothing will use it, so the sentence has to be
            // given up whole rather than left half-read.
            targets.RemoveAt(firstIndex);
            return false;
        }

        // "~ deals 3 damage divided as you choose among one, two, or three targets" - one
        // sentence naming a variable number of targets and a split between them. The largest
        // count becomes that many optional targets, because "one, two, or three" is exactly
        // "up to three, at least one" and the target grammar already has optional targets.
        var divided = DividedDamageLine().Match(sentence);
        if (divided.Success)
        {
            // "Among any number of targets" names no ceiling, and the damage is the ceiling:
            // every target in a division has to be assigned at least one (CR 601.2d), so three
            // damage cannot reach a fourth target however many are on the board.
            var most = divided.Groups["any"].Success
                ? Number(divided.Groups["n"].Value).Fixed
                : Number(divided.Groups["most"].Value).Fixed;

            // "Among one, two, or three targets" names no kind, and "any target" is what the
            // grammar calls a creature, player or planeswalker (CR 115.4).
            var kind = divided.Groups["t"].Value.Trim();
            var each = Specs.Parse(kind.Length == 0 ? "any target" : "target " + Singular(kind));

            if (each is null || most < 2)
                return false;

            // The first is required and the rest are optional, which is exactly what "one, two,
            // or three" says: at least one, at most three.
            for (var i = 0; i < most; i++)
                targets.Add(i == 0 ? each : each with { Optional = true });

            effects.Add(new DealDividedDamage(
                Number(divided.Groups["n"].Value).Fixed, most, targets.Count - most));
            return true;
        }

        // "Return up to two target creature cards from your graveyard to your hand", "two target
        // creatures get +1/+1" — one instruction aimed at several things. Read by rewriting it to
        // the singular, parsing that with the ordinary vocabulary, and then making that many
        // copies of what came back. Every verb the parser knows arrives here already working, and
        // nothing has to learn what a second target means.
        var several = MultiTargetLine().Match(sentence);
        if (several.Success)
        {
            var howManyWord = several.Groups["n"].Value;

            // A range takes its ceiling from the larger word, and only its ceiling: "one or two"
            // means at least one, so the first target is required and the second is not. "Up to
            // two" means every one of them is optional, which is the other card entirely.
            var range = howManyWord.Contains(" or ", StringComparison.OrdinalIgnoreCase);
            var howMany = Number(
                range ? howManyWord[(howManyWord.LastIndexOf(' ') + 1)..] : howManyWord).Fixed;

            var optional = several.Groups["upto"].Success;

            // The verb has to agree with the singular target, or the rewritten sentence reads
            // "target creature get +1/+1" and no matcher will take it.
            // "Up to two **other** target creatures" - the word excludes the permanent whose
            // ability this is, and the singular grammar already spells that "another target
            // creature". So the rewrite says it that way and every reader that already handles
            // one of those handles all of them. 46 corpus lines, all of them plural: the singular
            // has read since the target grammar was built.
            var singular = several.Groups["head"].Value
                + (several.Groups["other"].Success ? "another target " : "target ")
                + Singular(several.Groups["t"].Value.Trim())
                + Agreeing(several.Groups["tail"].Value);

            var scratchTargets = ImmutableList.CreateBuilder<TargetSpec>();
            var scratchEffects = ImmutableList.CreateBuilder<IEffect>();

            // Strictly more than one. "Up to one target" is read by the target grammar itself,
            // and letting this rewrite claim it as well was worse than redundant: a sentence that
            // merely *contains* the phrase - "put a counter on it and tap up to one target
            // creature an opponent controls" - was swallowed whole and rewritten, and the
            // conjunction stopped compiling.
            if (howMany > 1
                && TryOne(singular, scratchTargets, scratchEffects)
                && scratchTargets.Count == 1
                && scratchEffects.Count > 0
                && !scratchEffects.Any(FindsItselfByIndex))
            {
                for (var copy = 0; copy < howMany; copy++)
                {
                    var offset = targets.Count;
                    targets.Add(optional || (range && copy > 0)
                        ? scratchTargets[0] with { Optional = true }
                        : scratchTargets[0]);

                    foreach (var effect in scratchEffects)
                        effects.Add(EffectTargets.Shift(effect, offset));
                }

                return true;
            }

            return false;
        }

        // "Any number of target creatures get +1/+1 and gain flying until end of turn" — the
        // same instruction aimed at several things, with the caster choosing how many rather
        // than the card. Read exactly the way the counted form above is: rewrite to the singular,
        // parse that with the ordinary vocabulary, and keep what came back — but keep it once,
        // as a block, because there is no number here to make copies with. The number arrives
        // when the spell is cast (CR 601.2c) and the block is counted out then.
        var unbounded = AnyNumberTargetLine().Match(sentence);
        if (unbounded.Success)
        {
            var one = unbounded.Groups["head"].Value
                + (unbounded.Groups["other"].Success ? "another target " : "target ")
                + Singular(unbounded.Groups["t"].Value.Trim())
                + Agreeing(unbounded.Groups["tail"].Value);

            var blockTargets = ImmutableList.CreateBuilder<TargetSpec>();
            var blockEffects = ImmutableList.CreateBuilder<IEffect>();

            if (!TryOne(one, blockTargets, blockEffects)
                || blockTargets.Count != 1
                || blockEffects.Count == 0)
            {
                return false;
            }

            // Deferred questions are refused for a reason expansion makes sharper than nesting
            // ever did. Each of them finds itself again by an index into its ability's effect
            // list, and a block expanded to three targets is the same effect three times over —
            // three records carrying one index, which the lookup answers with nothing. Asked
            // over the whole tree rather than the top level, because a locator one branch down
            // is copied just the same.
            if (EffectTree.Flatten(blockEffects).Any(FindsItselfByIndex))
                return false;

            var at = targets.Count;

            // Written against the block's own position and not against zero: the corpus
            // invariant that every chosen target is read by something walks this template where
            // it sits, and one numbered from zero would report the block's target as chosen and
            // ignored on every card whose block is not the first thing it targets.
            targets.Add(blockTargets[0] with { AnyNumber = true });
            effects.Add(new ToEachChosenTarget(
                [.. blockEffects.Select(effect => EffectTargets.Shift(effect, at))], at));

            return true;
        }

        // "Put a +1/+1 counter on each creature you control" — the same group grammar the
        // sweepers use, with counters instead of a verb.
        var massCounters = MassCountersLine().Match(sentence);
        if (massCounters.Success
            && Specs.ParseGroup(massCounters.Groups["t"].Value) is
            { Kind: TargetKind.Permanent } counterees)
        {
            effects.Add(new ToEachPermanent(
                massCounters.Groups["kind"].Value == "+1/+1"
                    ? GroupAction.PlusOneCounters
                    : GroupAction.MinusOneCounters,
                counterees,
                Number(massCounters.Groups["n"].Value)));

            return true;
        }

        // "Target player discards a card" — a discard aimed at one player rather than a group.
        var told = TargetDiscardLine().Match(sentence);
        if (told.Success
            && Specs.Parse(told.Groups["t"].Value.Trim()) is { Kind: TargetKind.Player } discarder)
        {
            targets.Add(discarder);
            effects.Add(new DiscardCards(
                Number(told.Groups["n"].Value),
                AtRandom: told.Groups["random"].Success,
                TargetIndex: targets.Count - 1));

            return true;
        }

        // "Create a token that's a copy of ..." — of this permanent, of something targeted, or
        // of whatever the sentence before named. Three sources, one effect, because the only
        // thing that differs is which permanent's card is taken.
        var tokenCopy = TokenCopyLine().Match(sentence);
        if (tokenCopy.Success)
        {
            var of = tokenCopy.Groups["of"].Value.Trim();
            var plain = tokenCopy.Groups["except"].Success;

            if (of == "~")
            {
                effects.Add(new CreateTokenCopy(ExceptNotLegendary: plain));
                return true;
            }

            if (Specs.Parse(of) is { Kind: TargetKind.Permanent } copiedTarget)
            {
                targets.Add(copiedTarget);
                effects.Add(new CreateTokenCopy(
                    TargetIndex: targets.Count - 1, ExceptNotLegendary: plain));

                return true;
            }

            if (Pronouns.Contains(of, StringComparer.OrdinalIgnoreCase))
            {
                // A pronoun after a target means that target; with nothing targeted it means
                // whatever the trigger was about - "whenever a creature dies, create a token
                // that's a copy of that creature", where the creature is neither targeted nor
                // the permanent with the ability.
                effects.Add(targets.Count > 0
                    ? new CreateTokenCopy(
                        TargetIndex: targets.Count - 1, ExceptNotLegendary: plain)
                    : new CreateTokenCopy(
                        Subject: EffectSubject.TriggeringObject, ExceptNotLegendary: plain));

                return true;
            }

            return false;
        }

        m = CreatureTokenLine().Match(sentence);
        if (m.Success && TokenFrom(m) is { } minted)
        {
            var many = Number(m.Groups["n"].Value);

            // "A 1/1 Soldier token for each creature you control" - the same sentence with its
            // count taken from the board, through the same counting the pumps and the discounts
            // use.
            if (m.Groups["foreach"].Success)
            {
                if (CountingAmount(many, m.Groups["foreach"].Value.Trim()) is not { } perThing)
                    return false;

                many = perThing;
            }

            // "Each opponent creates a 1/1 Soldier" - the same token, made under somebody
            // else's control. A named player is a target and is chosen as the spell is cast;
            // a group is not, and neither is "you".
            var maker = new CreateToken(minted, many, m.Groups["tapped"].Success);

            if (m.Groups["who"].Success)
            {
                var whose = m.Groups["who"].Value.Trim();

                if (whose.StartsWith("target", StringComparison.OrdinalIgnoreCase))
                {
                    if (Specs.Parse(whose) is not { Kind: TargetKind.Player } named)
                        return false;

                    targets.Add(named);
                    maker = maker with { TargetIndex = targets.Count - 1 };
                }
                else if (TargetsControllerMakes(whose, targets) is { } index)
                {
                    effects.Add(new CreateTokenForTargetsController(
                        minted, many, index, m.Groups["tapped"].Success));

                    return true;
                }
                else
                {
                    maker = maker with { Scope = ScopeOf(whose) };
                }
            }

            effects.Add(maker);
            return true;
        }

        m = NamedTokenLine().Match(sentence);
        if (m.Success && PredefinedTokens.TryGetValue(m.Groups["kind"].Value, out var known))
        {
            // "Create a Treasure token for each creature that died this turn" - the count tail
            // the creature-token reader above has had all along. The two readers differ only in
            // whether the token's characteristics are printed or named, which has nothing to do
            // with how many of it there are, so a verb that could carry a count and a verb that
            // could not was an accident of which one was written first.
            if (CountedBy(Number(m.Groups["n"].Value), m.Groups["foreach"]) is not { } howMany)
                return false;

            // "Target opponent creates a Treasure token" - the same token under somebody else's
            // control, exactly as the creature-token reader beside this one already read it.
            // This one had only the bare "create", so every named token somebody else made was
            // unread on a sentence the other half of the grammar understood perfectly.
            var minting = new CreateToken(known, howMany, m.Groups["tapped"].Success);

            if (m.Groups["who"].Success)
            {
                var whose = m.Groups["who"].Value.Trim();

                if (whose.StartsWith("target", StringComparison.OrdinalIgnoreCase))
                {
                    if (Specs.Parse(whose) is not { Kind: TargetKind.Player } named)
                        return false;

                    targets.Add(named);
                    minting = minting with { TargetIndex = targets.Count - 1 };
                }
                else if (TargetsControllerMakes(whose, targets) is { } index)
                {
                    effects.Add(new CreateTokenForTargetsController(
                        known, howMany, index, m.Groups["tapped"].Success));

                    return true;
                }
                else
                {
                    minting = minting with { Scope = ScopeOf(whose) };
                }
            }

            effects.Add(minting);
            return true;
        }

        // "Investigate" is a keyword action that means exactly "create a Clue token"
        // (CR 701.16a), so it compiles to the same effect rather than to a mechanic of its own.
        if (InvestigateLine().IsMatch(sentence))
        {
            effects.Add(new CreateToken(PredefinedTokens["Clue"]));
            return true;
        }

        // "…, then attach ~ to it" - the destination is the pronoun this time, not the thing
        // being attached. It means the target the sentence before it chose, exactly as every
        // other "it" in this grammar does, so it is only meaningful once something has been
        // targeted. 16 corpus lines end this way, and they read the other direction already.
        if (AttachSelfToItLine().IsMatch(sentence) && targets.Count > 0)
        {
            effects.Add(new AttachSourceTo(targets.Count - 1));
            return true;
        }

        // The same sentence with nothing targeted, where "it" is instead the token the sentence
        // before it made: "create a 1/1 white Soldier creature token, then attach ~ to it" is
        // living weapon written out. Read *after* the targeted form and never instead of it -
        // the two are one string apart, and claiming this one first took sixteen corpus lines
        // away from the reader above before the order was fixed.
        //
        // It rewrites that effect rather than adding one beside it, because a token has no id
        // until the effect resolves and a second effect would have nothing to name it with.
        if (AttachSourceToItLine().IsMatch(sentence)
            && effects.Count > 0
            && effects[^1] is CreateToken
            {
                Tapped: false,
                Scope: PlayerScope.You,
                TargetIndex: null,

                // Exactly one plain token. A zero fixed part is how "create a token" with no
                // number arrives and CreateToken already reads it as one; a count, an X or a
                // "for each" makes several, and "it" then names none of them.
                Count: { IsVariable: false, Negated: false, Counter: null, Fixed: 0 or 1 },
            } made)
        {
            effects[^1] = new CreateTokenAndAttachSource(made.Token);
            return true;
        }

        // "Attach it to target creature you control" — the tail of an Equipment's enters trigger,
        // where "it" is the Equipment (CR 701.3a).
        m = AttachSelfLine().Match(sentence);
        if (m.Success && Specs.Parse(m.Groups["t"].Value) is { } host)
        {
            targets.Add(host);
            effects.Add(new AttachSourceTo(targets.Count - 1));
            return true;
        }

        // "Untap it" / "Untap that creature" — the tail of an effect that already named one, so
        // it means the target the sentence before it chose rather than a new one.
        // Two patterns rather than an optional suffix: the target group accepts spaces, so an
        // optional " until end of turn" is simply swallowed by it and the phrase parser is handed
        // "target creature until end of turn", which is not a phrase. Requiring the duration in
        // one form and forbidding it in the other is what keeps the boundary where it belongs.
        // "…for as long as you control this creature" is a third clock beside "until end of turn"
        // and "for ever": the effect ends when the condition stops holding and does not resume if
        // it holds again (CR 611.2b). Read before the other two, because the phrase they match
        // is a prefix of this one and either would take it and drop the duration.
        // "…gains protection from the color of your choice until end of turn" - the colour is
        // named on resolution, so the effect asks rather than finishing.
        var amassing = AmassLine().Match(sentence);
        if (amassing.Success)
        {
            effects.Add(new Amass(
                amassing.Groups["kind"].Success
                    ? amassing.Groups["kind"].Value.Trim()
                    : "Zombie",
                Number(amassing.Groups["n"].Value)));
            return true;
        }

        var bolstering = BolsterLine().Match(sentence);
        if (bolstering.Success)
        {
            effects.Add(new Bolster(Number(bolstering.Groups["n"].Value)));
            return true;
        }

        var freeing = UntapUpToLine().Match(sentence);
        if (freeing.Success
            && Specs.ParseGroup(freeing.Groups["group"].Value.Trim()) is
            { Kind: TargetKind.Permanent } freed
            && freed.ObjectFilter is { } canUntap)
        {
            effects.Add(new UntapUpTo(
                Number(freeing.Groups["n"].Value),
                (state, obj, you) => canUntap(state, EmptyAbilities.Instance, obj, you)));
            return true;
        }

        m = LureTargetLine().Match(sentence);
        if (m.Success && Specs.Parse(m.Groups["t"].Value) is { } lured)
        {
            targets.Add(lured);
            effects.Add(new PumpUntilEndOfTurn(
                GenerativeEffects.LureId(), targets.Count - 1));
            return true;
        }

        // "Target creature blocks ~ this turn if able" and its unnamed twin. Two readers rather
        // than one optional group, because the second is a different requirement and not a
        // weaker version of the first: blocking anything satisfies one and only blocking the
        // source satisfies the other.
        // "~ connives", "it connives", "~ connives X" (CR 701.50a). The subject is always the
        // permanent whose ability it is - the counter goes on the thing that connived - so the
        // sentence names it and the effect never has to be told.
        var discovering = DiscoverLine().Match(sentence);
        if (discovering.Success)
        {
            effects.Add(new Discover(Number(discovering.Groups["n"].Value)));
            return true;
        }

        var blighting = BlightLine().Match(sentence);
        if (blighting.Success)
        {
            effects.Add(new Blight(Number(blighting.Groups["n"].Value)));
            return true;
        }

        if (RingTemptsLine().IsMatch(sentence))
        {
            effects.Add(new TheRingTemptsYou());
            return true;
        }

        var incubating = IncubateLine().Match(sentence);
        if (incubating.Success)
        {
            effects.Add(new Incubate(Number(incubating.Groups["n"].Value)));
            return true;
        }

        var populating = PopulateLine().Match(sentence);
        if (populating.Success)
        {
            effects.Add(new Populate(
                populating.Groups["n"].Success
                    ? Number(populating.Groups["n"].Value)
                    : new Amount(1)));

            return true;
        }

        if (ManifestDreadLine().IsMatch(sentence))
        {
            effects.Add(new ManifestDread());
            return true;
        }

        var conniving = ConniveLine().Match(sentence);
        if (conniving.Success)
        {
            var many = conniving.Groups["n"].Success
                ? Number(conniving.Groups["n"].Value)
                : new Amount(1);

            if (!conniving.Groups["t"].Success)
            {
                effects.Add(new Connive(many));
                return true;
            }

            if (Specs.Parse(conniving.Groups["t"].Value.Trim()) is not { } named)
                return false;

            targets.Add(named);
            effects.Add(new Connive(many, targets.Count - 1));
            return true;
        }

        // "~ endures 2", "it endures 1" (CR 701.63a): the permanent's controller "creates an N/N
        // white Spirit creature token unless they put N +1/+1 counters on that permanent". That
        // is a choice with two outcomes and no cost, which is exactly the offer machinery the
        // optional payment already uses with an empty mana cost - so endure is a composition of
        // two effects this compiler has had all along rather than a mechanic of its own.
        //
        // The token half is built by running the printed sentence for it back through the token
        // reader, rather than by assembling a second CardDefinition here. Two constructions of
        // "an N/N white Spirit creature token" would produce two oracle ids for one token, and
        // the pool's duplicate guard would then refuse to play either; going through the one
        // builder also means endure's Spirit is the same object every other card's Spirit is.
        //
        // Only a printed digit is taken. "Endure X" is three corpus cards and the token's size
        // is part of its identity - a definition cannot be named before X is known - so those are
        // left unread rather than given a Spirit of some guessed size.
        var enduring = EndureLine().Match(sentence);
        if (enduring.Success)
        {
            var endured = enduring.Groups["n"].Value;
            var spirit = CreatureTokenLine().Match(
                $"create a {endured}/{endured} white Spirit creature token");

            if (!spirit.Success || TokenFrom(spirit) is not { } ghost)
                return false;

            effects.Add(new MayPay(
                Mana.ManaCostSpec.Parse(string.Empty),
                [new PutCountersOnSource(CounterKinds.PlusOnePlusOne, Number(endured))],
                [new CreateToken(ghost)],
                effects.Count,
                YesLabel: $"Put {endured} +1/+1 counters on this permanent",
                NoLabel: $"Create a {endured}/{endured} white Spirit creature token"));

            return true;
        }

        // "Manifest the top card of your library" (CR 701.40a). The 2/2 is not described here
        // because a face-down permanent already is one - the characteristics compute it - and
        // the card underneath is never changed, so turning it up needs nothing remembered.
        var manifesting = ManifestLine().Match(sentence);
        if (manifesting.Success)
        {
            effects.Add(new Manifest(
                manifesting.Groups["n"].Success
                    ? Number(manifesting.Groups["n"].Value)
                    : new Amount(1)));

            return true;
        }

        // "Goad target creature", "goad it", "goad each creature target player controls" - one
        // designation asked for in several ways. The target half goes through the ordinary
        // grammar, so whatever it can name, this can goad.
        m = GoadLine().Match(sentence);
        if (m.Success)
        {
            if (m.Groups["t"].Success)
            {
                if (Specs.Parse(m.Groups["t"].Value.Trim()) is not { } provoked)
                    return false;

                targets.Add(provoked);
                effects.Add(new GoadTarget(targets.Count - 1));
                return true;
            }

            if (targets.Count == 0)
                return false;

            effects.Add(new GoadTarget(targets.Count - 1));
            return true;
        }

        // "Suspect target creature", "suspect it" - a designation rather than an ability, and
        // one the engine can express entirely as an indefinite keyword grant (CR 701.60c).
        m = SuspectLine().Match(sentence);
        if (m.Success)
        {
            if (m.Groups["t"].Success)
            {
                if (Specs.Parse(m.Groups["t"].Value.Trim()) is not { } suspected)
                    return false;

                targets.Add(suspected);
                effects.Add(new SuspectTarget(targets.Count - 1));
                return true;
            }

            // "Suspect it" - the target the sentence before chose, which is the only thing the
            // pronoun can mean here.
            if (targets.Count == 0)
                return false;

            effects.Add(new SuspectTarget(targets.Count - 1));
            return true;
        }

        m = CantBlockSourceLine().Match(sentence);
        if (m.Success && Specs.Parse(m.Groups["t"].Value) is { } barred)
        {
            targets.Add(barred);
            effects.Add(new CantBlockSource(targets.Count - 1));
            return true;
        }

        m = MustBlockSourceLine().Match(sentence);
        if (m.Success && Specs.Parse(m.Groups["t"].Value) is { } compelled)
        {
            targets.Add(compelled);
            effects.Add(new MustBlockSource(targets.Count - 1));
            return true;
        }

        // "Target creature attacks this turn if able" - the block reader's mirror, and the same
        // requirement read from the other side of combat. The engine already models "attacks
        // each combat if able" as a keyword, so this is that keyword granted for a turn.
        m = MustAttackLine().Match(sentence);
        if (m.Success && Specs.Parse(m.Groups["t"].Value) is { } compelledToAttack)
        {
            targets.Add(compelledToAttack);
            effects.Add(new PumpUntilEndOfTurn(
                GenerativeEffects.GrantId(KeywordAbility.MustAttack), targets.Count - 1));

            return true;
        }

        m = MustBlockLine().Match(sentence);
        if (m.Success && Specs.Parse(m.Groups["t"].Value) is { } drafted)
        {
            targets.Add(drafted);
            effects.Add(new PumpUntilEndOfTurn(
                GenerativeEffects.MustBlockId(), targets.Count - 1));
            return true;
        }

        // "Target creature must be blocked this turn if able" (CR 509.1c) and "~ can block an
        // additional creature this turn" (CR 509.1a). Both requirements already exist as static
        // abilities the compiler registers on a card, and the "this turn" wordings of both were
        // recorded as declined together for one reason: a static continuous effect has nowhere
        // to put a duration, so reading them there would have made them permanent. A floating
        // effect is where a duration lives, so each is that same characteristic granted for the
        // turn and nothing else - and the "each combat" printings still read as statics, which
        // is the pair of readings the behaviour tests hold apart.
        m = MustBeBlockedThisTurnLine().Match(sentence);
        if (m.Success
            && AimedThisTurn(
                m.Groups["t"].Value, GenerativeEffects.MustBeBlockedId(), targets) is
            { } compelledToBeBlocked)
        {
            effects.Add(compelledToBeBlocked);
            return true;
        }

        m = ExtraBlocksThisTurnLine().Match(sentence);
        if (m.Success)
        {
            // The same three amounts the static reader knows, read the same way: "any number" is
            // a number no board can reach rather than a separate flag.
            var more = m.Groups["any"].Success
                ? 1_000_000
                : m.Groups["n"].Success
                    ? Number(m.Groups["n"].Value).Fixed
                    : 1;

            if (AimedThisTurn(
                    m.Groups["t"].Value, GenerativeEffects.ExtraBlocksId(more), targets) is
                { } blocking)
            {
                effects.Add(blocking);
                return true;
            }
        }

        m = SwitchPowerToughnessLine().Match(sentence);
        if (m.Success && Specs.Parse(m.Groups["t"].Value) is { } swapped)
        {
            targets.Add(swapped);
            effects.Add(new PumpUntilEndOfTurn(
                GenerativeEffects.SwitchPowerToughnessId(), targets.Count - 1));
            return true;
        }

        // The self forms of the two readers below. A card that says "~" rather than "target
        // permanent" is aiming the same effect at itself, and the near-miss sweep found both of
        // these unread beside their targeted twins.
        if (SelfBecomesChosenTypeLine().IsMatch(sentence))
        {
            effects.Add(new ChooseCreatureTypeForTarget());
            return true;
        }

        if (SelfBecomesChosenColourLine().IsMatch(sentence))
        {
            effects.Add(new ChooseColorForTarget(ColorChoiceUse.BecomesColor, TargetIndex: null));
            return true;
        }

        // "~ gains protection from the color of your choice until end of turn" - the same
        // question the targeted form below asks, aimed at the permanent whose ability it is.
        // <see cref="ChooseColorForTarget"/> already reads a null index as the source, so this
        // is the sentence and nothing else; it went unread only because no pattern said it.
        if (SelfProtectionFromChosenColourLine().IsMatch(sentence))
        {
            effects.Add(new ChooseColorForTarget(
                ColorChoiceUse.ProtectionFrom, TargetIndex: null));
            return true;
        }

        // "~ can attack this turn as though it didn't have defender" - a permission that lasts
        // the turn (CR 702.3b). Written as a floating effect on the source rather than as a
        // keyword removal, because the creature keeps its defender: cards that count creatures
        // with defender, and the Walls that care about being Walls, must not change because one
        // of them was let through.
        if (SelfMayAttackDespiteDefenderLine().IsMatch(sentence))
        {
            effects.Add(new PumpSourceUntilEndOfTurn(
                GenerativeEffects.MayAttackAsThoughNoDefenderId()));
            return true;
        }

        var released = TargetMayAttackDespiteDefenderLine().Match(sentence);
        if (released.Success && Specs.Parse(released.Groups["t"].Value) is { } unwalled)
        {
            targets.Add(unwalled);
            effects.Add(new PumpUntilEndOfTurn(
                GenerativeEffects.MayAttackAsThoughNoDefenderId(), targets.Count - 1));
            return true;
        }

        // "Put ~ on top of its owner's library" - the source sending itself back, matched here
        // beside the other self-move sentences and before the general target grammar, which
        // would otherwise read the tilde as a phrase naming something.
        var deckbound = PutSelfOnLibraryLine().Match(sentence);
        if (deckbound.Success)
        {
            effects.Add(new PutSourceOnLibrary(
                deckbound.Groups["where"].Value.StartsWith(
                    "bottom", StringComparison.OrdinalIgnoreCase)
                    ? ZonePosition.Bottom
                    : ZonePosition.Top));
            return true;
        }

        if (SwitchSelfPowerToughnessLine().IsMatch(sentence))
        {
            effects.Add(new PumpSourceUntilEndOfTurn(
                GenerativeEffects.SwitchPowerToughnessId()));
            return true;
        }

        m = ProtectionFromChosenColourLine().Match(sentence);
        if (m.Success && Specs.Parse(m.Groups["t"].Value) is { } warded)
        {
            targets.Add(warded);
            effects.Add(new ChooseColorForTarget(
                m.Groups["becomes"].Success
                    ? ColorChoiceUse.BecomesColor
                    : ColorChoiceUse.ProtectionFrom,
                targets.Count - 1));
            return true;
        }

        m = GainControlWhileLine().Match(sentence);
        if (m.Success && Specs.Parse(m.Groups["t"].Value) is { } borrowed)
        {
            targets.Add(borrowed);
            effects.Add(new GainControlWhileSourceHolds(
                WhileNamed(m), targets.Count - 1));
            return true;
        }

        // "Target creature gets +2/+0 for as long as ~ remains tapped", and its two siblings —
        // a granted keyword and a permanent that stops untapping. The same duration the gain
        // control clause above already reads, on the three other verbs the corpus prints it with
        // (CR 611.2b).
        m = PumpWhileLine().Match(sentence);
        if (m.Success)
        {
            var boost = Keywords(m.Groups["kw"].Value);
            if (m.Groups["kw"].Success && boost is null)
                return false;

            var power = int.Parse(
                m.Groups["p"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            var toughness = int.Parse(
                m.Groups["tough"].Value, NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture);

            var held = new List<string> { GenerativeEffects.PumpId(power, toughness) };
            if (boost is { } alongside)
                held.Add(GenerativeEffects.GrantId(alongside));

            if (HoldsWhile(m, held, targets, effects))
                return true;
        }

        m = GainsKeywordWhileLine().Match(sentence);
        if (m.Success)
        {
            if (Keywords(m.Groups["kw"].Value) is not { } lasting)
                return false;

            if (HoldsWhile(m, [GenerativeEffects.GrantId(lasting)], targets, effects))
                return true;
        }

        m = DoesNotUntapWhileLine().Match(sentence);
        if (m.Success
            && HoldsWhile(m, [GenerativeEffects.DoesNotUntapId()], targets, effects))
        {
            return true;
        }

        m = GainControlUntilLine().Match(sentence);
        if (!m.Success)
            m = GainControlLine().Match(sentence);

        if (m.Success && Specs.Parse(m.Groups["t"].Value) is { } stolen)
        {
            targets.Add(stolen);
            effects.Add(new GainControlUntilEndOfTurn(targets.Count - 1));
            return true;
        }

        // "Whenever an opponent taps an artifact for mana, gain control of that artifact until
        // the end of your next turn" - the theft aimed at something the card has already picked
        // out. Seventeen cards, and the same restriction as the damage reader above: only the
        // arm that resolves to a target is taken, because GainControlUntilEndOfTurn carries a
        // target index and a pronoun meaning the trigger's subject has nowhere to go.
        if (PronounObject(m, targets, objectNamedByTrigger) is { } seized)
        {
            effects.Add(new GainControlUntilEndOfTurn(seized));
            return true;
        }

        // "It gains haste until end of turn" — the tail of a threaten effect, where "it" is the
        // creature the sentence before named. Only meaningful once something has been targeted.
        m = ItGainsUntilLine().Match(sentence);
        if (!m.Success)
            m = ItGainsLine().Match(sentence);

        if (m.Success && Keywords(m.Groups["kw"].Value) is { } itGains)
        {
            if (targets.Count > 0)
            {
                effects.Add(new PumpUntilEndOfTurn(
                    GenerativeEffects.GrantId(itGains), targets.Count - 1));
                return true;
            }

            // With nothing targeted and nothing done yet, "that creature" can only be what the
            // trigger was about - there is no earlier clause for it to be pointing back at.
            // The guard is the whole of what makes this safe: after a clause that creates a
            // token, "it" means the token, and answering with the trigger's subject would pump
            // the wrong creature and never fail.
            if (effects.Count == 0)
            {
                effects.Add(new PumpUntilEndOfTurn(
                    GenerativeEffects.GrantId(itGains),
                    Subject: EffectSubject.TriggerSubject));

                return true;
            }

            return false;
        }

        // "Tap enchanted creature", "destroy equipped creature" — the permanent this is attached
        // to, which is not a target: it was chosen when the Aura was cast and this does not choose
        // it again. Read by writing the sentence out as though it did target, parsing that with
        // the ordinary verb vocabulary, and then aiming the result at the attachment instead —
        // so every verb the parser learns arrives here already working.
        var attached = AttachedSubjectLine().Match(sentence);
        if (attached.Success)
        {
            var scratchTargets = ImmutableList.CreateBuilder<TargetSpec>();
            var scratchEffects = ImmutableList.CreateBuilder<IEffect>();

            var rewritten = attached.Groups["verb"].Value
                + " target " + attached.Groups["what"].Value
                + attached.Groups["tail"].Value;

            if (TryOne(rewritten, scratchTargets, scratchEffects)
                && scratchTargets.Count == 1
                && scratchEffects.Count > 0
                && !scratchEffects.Any(FindsItselfByIndex))
            {
                effects.Add(new OnAttached(scratchEffects.ToImmutable()));
                return true;
            }

            return false;
        }

        // The same rewrite for the sentences that lead with the subject rather than the verb.
        // "Enchanted creature gains first strike until end of turn" is a one-shot inside a
        // trigger, not the static that a bare "enchanted creature gets +1/+1" is - and the
        // rewriter above could only see a sentence whose first word was a verb, so seventeen
        // lines of the first kind went unread while the second read perfectly.
        var attachedSubject = AttachedSubjectFirstLine().Match(sentence);
        if (attachedSubject.Success)
        {
            var scratchTargets = ImmutableList.CreateBuilder<TargetSpec>();
            var scratchEffects = ImmutableList.CreateBuilder<IEffect>();

            var rewritten = "target " + attachedSubject.Groups["what"].Value
                + " " + attachedSubject.Groups["tail"].Value;

            if (TryOne(rewritten, scratchTargets, scratchEffects)
                && scratchTargets.Count == 1
                && scratchEffects.Count > 0
                && !scratchEffects.Any(FindsItselfByIndex))
            {
                effects.Add(new OnAttached(scratchEffects.ToImmutable()));
                return true;
            }

            return false;
        }

        // "It", "that creature", "that permanent" — a pronoun for whatever the sentence before
        // named. One matcher for every verb that takes one, because they all mean the same thing:
        // do this to the target already chosen rather than asking for another.
        //
        // Only meaningful once something *has* been targeted, which is the guard that stops "it"
        // being read on a card where it means something else entirely — a token just created, or
        // a card just revealed.
        m = ItLine().Match(sentence);

        // "~ gains indestructible until end of turn. Tap it." - with nothing targeted, "it" is
        // whatever the sentence before acted on, and that sentence was about the source. The
        // test is the effect it produced rather than the words: only a self-pump says the
        // previous clause was about "~", so a token created a moment ago cannot be mistaken
        // for it.
        if (m.Success
            && targets.Count == 0
            && effects.Count > 0
            && effects[^1] is PumpSourceUntilEndOfTurn
            && m.Groups["verb"].Value.Equals("tap", StringComparison.OrdinalIgnoreCase))
        {
            effects.Add(new TapTarget(Subject: EffectSubject.Source));
            return true;
        }

        if (m.Success && targets.Count > 0)
        {
            var index = targets.Count - 1;

            effects.Add(m.Groups["verb"].Value.ToLowerInvariant() switch
            {
                "untap" => new UntapTarget(index),
                "tap" => new TapTarget(index),
                "destroy" => new DestroyTarget(index),
                "exile" => new ExileTarget(index),
                _ => new ReturnToHand(index),
            });

            return true;
        }

        if (UntapSelfLine().IsMatch(sentence))
        {
            effects.Add(new UntapSource());
            return true;
        }

        // "Tap ~" — the other half of the sentence above, and the one that had no reader. Tapping
        // the source needs no effect of its own: TapTarget already asks the shared subject
        // resolver which permanent it means, so naming the source is a constructor argument
        // rather than a new verb. Twelve corpus lines say it, and every one of them is a cost or
        // a drawback ("at the beginning of your combat step, tap ~"), which is why the pair
        // reading only the untap half was the wrong way round to be missing.
        if (TapSelfLine().IsMatch(sentence))
        {
            effects.Add(new TapTarget(Subject: EffectSubject.Source));
            return true;
        }

        // "Add {G}{G}" outside a mana ability - a trigger that makes mana, an ability that makes
        // mana as well as doing something else, or a spell. All of them use the stack, unlike a
        // mana ability (CR 605.3b), which is why they compile to an effect rather than to
        // Produces - and it is that resolution which gives a colour somewhere to be chosen.
        m = AddManaLine().Match(sentence);
        if (m.Success)
        {
            var whose = ScopeOf(m.Groups["who"].Value);
            var what = m.Groups["mana"].Value.Trim();

            // "Add one mana of any color", "add two mana of any one color", "add two mana in any
            // combination of colors" - the colour is not known until this resolves, so the
            // effect asks. Read before the fixed forms because the words reach both.
            if (ChosenMana(what) is { } asking)
            {
                effects.Add(asking with { Who = whose });
                return true;
            }

            var alternatives = ManaWords.Alternatives(what);

            // Only an unambiguous production can be a fixed effect. Anything with more than one
            // payout is a choice, and the ones this reader cannot turn into a question - "add X
            // mana in any combination of {U} and/or {R}" - stay unread rather than being picked
            // for the player.
            if (alternatives.Count == 1)
            {
                effects.Add(new AddMana(alternatives[0]) { Who = whose });
                return true;
            }
        }

        // "Sacrifice it at the beginning of the next end step" — a delayed triggered ability
        // (CR 603.7), not something that happens now.
        //
        // "At end of combat" is the same ability with a different moment (CR 511.1): the combat
        // damage has been dealt, the attacker did its work, and then it goes. It is a separate
        // arm rather than a step name because the cards do not spell it as one - nothing prints
        // "at the beginning of the next end of combat step" - and inventing that phrase so the
        // step table could take it would put a wording in the grammar that no card has.
        m = DelayedSelfLine().Match(sentence);
        if (m.Success)
        {
            var when = m.Groups["combat"].Success
                ? State.TurnStep.EndOfCombat
                : TriggerConditions.StepNamed(m.Groups["step"].Value);

            // The verb is mapped rather than passed through. The engine's delayed vocabulary
            // spells the bounce "return-to-hand", and handing it the printed word would fall to
            // the default arm - which is a sacrifice, and a sacrifice is not a bounce.
            //
            // "Destroy" is a word of its own in that vocabulary rather than a synonym of the
            // sacrifice, because CR 701.21a says outright that sacrificing a permanent does not
            // destroy it: nothing that replaces destruction sees a sacrifice, neither
            // regeneration (CR 701.19b) nor indestructible (CR 702.12b). The line stayed unread
            // for as long as the engine had no such word, which was the right answer while it
            // was true - a drawback read harsher than it prints is worse than one not read.
            var doing = m.Groups["verb"].Value.ToLowerInvariant() switch
            {
                "sacrifice" => DelayedActions.Sacrifice,
                "exile" => DelayedActions.Exile,
                "return" => DelayedActions.ReturnToHand,
                "destroy" => DelayedActions.Destroy,
                _ => null,
            };

            if (when is { } moment && doing is not null)
            {
                var who = m.Groups["who"].Value.Trim();

                // "~" is the permanent with the ability and can be nothing else, whatever the
                // verb and whatever came before it in the line.
                if (string.Equals(who, "~", StringComparison.Ordinal))
                {
                    effects.Add(new DelaySourceAction(doing, moment));
                    return true;
                }

                // A pronoun goes to the shared reader, which answers with the target an earlier
                // sentence chose or with the object the trigger was about, and with nothing at
                // all where it can see neither. "Destroy that creature at end of combat" on a
                // basilisk is the second of those; "target creature you control gets +X/+X until
                // end of turn. Destroy it at the beginning of the next end step" is the first.
                if (ObjectOf(who, targets, objectNamedByTrigger) is { } named
                    && named.Subject != EffectSubject.Source)
                {
                    effects.Add(
                        new DelayObjectAction(doing, moment, named.Subject, named.Index));

                    return true;
                }

                // Only "it" is left, and only "it" may fall back. "That creature" names something
                // the sentence has already been told about, and where the reader above could not
                // say what, there is no answer - reading it as the source turns "whenever ~
                // blocks a creature, destroy that creature at end of combat" into a basilisk
                // that destroys itself, which is the exact card this codebase has reverted
                // before. Three of them compiled that way for the length of one measurement.
                if (!string.Equals(who, "it", StringComparison.OrdinalIgnoreCase))
                    return false;

                // "It" with nothing else named can only be the permanent with the ability. The
                // three older verbs have always taken that step; a destroy takes it only when
                // nothing before it in the ability produced another permanent to mean. "Create a
                // 1/1 Insect token. Destroy it at the beginning of the next end step" is about
                // the token, and a destroy falling back to the source there would blow up the
                // card that made it. That fallback is wrong for the sacrifice on those same
                // cards and is left alone deliberately: it is the reading 52 complete cards
                // already have, and correcting it is a measured pass of its own rather than a
                // side effect of adding a word.
                if (!string.Equals(doing, DelayedActions.Destroy, StringComparison.Ordinal)
                    || (effects.Count == 0 && targets.Count == 0))
                {
                    effects.Add(new DelaySourceAction(doing, moment));
                    return true;
                }
            }
        }

        // "Remove a +1/+1 counter from it at end of combat" - the same delayed ability with a
        // counter change instead of a zone change (CR 603.7, 122.1). The Clockwork cycle winds
        // itself down this way and two other cards wind themselves up, so the sign is read from
        // the verb rather than assumed. It is a separate effect from the delayed move above
        // because that one's vocabulary defaults an unrecognised instruction to a sacrifice, and
        // a counter removal answered with a sacrifice is a far worse card than an unread line.
        m = DelayedSelfCountersLine().Match(sentence);
        if (m.Success && CounterKindNamed(m.Groups["kind"].Value) is { } delayedKind)
        {
            var many = Number(m.Groups["n"].Value).Fixed;
            var taking = m.Groups["verb"].Value.StartsWith(
                "remove", StringComparison.OrdinalIgnoreCase);

            var delayedWhen = m.Groups["combat"].Success
                ? State.TurnStep.EndOfCombat
                : TriggerConditions.StepNamed(m.Groups["step"].Value);

            if (many > 0 && delayedWhen is { } thisStep)
            {
                effects.Add(new DelaySourceCounters(
                    delayedKind, taking ? -many : many, thisStep));

                return true;
            }
        }

        // "Search your library for a basic land card, put it onto the battlefield tapped, then
        // shuffle." — every ramp spell and every fetchland. The trailing shuffle is matched but
        // not read, because the search does it either way (CR 701.23e); requiring the words is
        // what keeps this from claiming a search that does something else with what it finds.
        // "Then shuffle" arrives as a clause of its own, because the sentence splitter separates
        // on ", then" — and the search has already shuffled (CR 701.23e). Accepted only when
        // something has already been read, so a bare "Shuffle your library" on a card with no
        // search does not quietly compile to nothing.
        if (effects.Count > 0 && BareShuffleLine().IsMatch(sentence))
            return true;

        m = SearchLibraryLine().Match(sentence);
        if (m.Success && m.Groups["named"].Success && !m.Groups["what"].Success)
        {
            // A name is a filter of its own and cannot be combined with a kind here: "a Goblin
            // card named X" would need both, and the one card printing that shape is not worth
            // a filter grammar that can be got wrong.
            effects.Add(new SearchLibrary(
                Abilities.SearchFilters.NamedPrefix + m.Groups["named"].Value.Trim(),
                m.Groups["where"].Value.Contains("battlefield", StringComparison.OrdinalIgnoreCase)
                    ? Zone.Battlefield
                    : Zone.Hand,
                Tapped: m.Groups["tapped"].Success));

            return true;
        }

        if (m.Success && SearchFilterNamed(m.Groups["what"].Value) is { } filter)
        {
            // "Shuffle and put that card on top" leaves it in the library, which is a
            // destination like any other here - the engine puts it back after the shuffle
            // rather than before, because that is the order the card prints.
            // "Their library" is only ever somebody else's: a card that means your own says
            // "your library", so the two words decide whose library this is.
            var searchWho = m.Groups["whose"].Value.StartsWith(
                "their", StringComparison.OrdinalIgnoreCase)
                ? SearchWho.SubjectController
                : SearchWho.You;

            effects.Add(new SearchLibrary(
                filter,
                m.Groups["ontop"].Success ? Zone.Library : SearchDestination(m.Groups["where"].Value),
                Tapped: m.Groups["tapped"].Success,
                Who: searchWho,
                Count: m.Groups["any"].Success ? AnyNumber : SearchCount(m.Groups["n"].Value),
                // "With mana value 3" on its own is an exact match, not a ceiling. Reading it
                // as "3 or less" would find cards the card does not allow, which is a strictly
                // better tutor than the one printed.
                ExactManaValue: !m.Groups["dir"].Success ? ManaValueBound(m) : null,
                MinManaValue: m.Groups["dir"].Value.Equals(
                    "greater", StringComparison.OrdinalIgnoreCase) ? ManaValueBound(m) : null,
                MaxManaValue: m.Groups["dir"].Success
                    && !m.Groups["dir"].Value.Equals("greater", StringComparison.OrdinalIgnoreCase)
                        ? ManaValueBound(m)
                        : null));
            return true;
        }

        // "Seek a nonland card." - a search with the choice taken away and the shuffle removed.
        // The filter, the count and the mana-value bounds are read by the same vocabulary the
        // search above uses, so a word either of them learns is learned by both; what is
        // different is the effect it builds, and the difference is that the game picks.
        //
        // Anchored at both ends, like every sentence reader here, and that is what refuses the
        // half of this family that must stay unread: "seek a nonland card instead", "seek a card
        // with mana value equal to the number of cards in your hand", "seek three nonland cards,
        // then nonland cards in your hand perpetually gain ...". Each of those means something
        // this cannot build, and each stops matching at the tail rather than being read as the
        // plain seek it is not.
        m = SeekLine().Match(sentence);
        if (m.Success && m.Groups["named"].Success && !m.Groups["what"].Success)
        {
            effects.Add(new Seek(
                Abilities.SearchFilters.NamedPrefix + m.Groups["named"].Value.Trim(),
                m.Groups["where"].Success ? Zone.Battlefield : Zone.Hand,
                Tapped: m.Groups["tapped"].Success));

            return true;
        }

        if (m.Success && SearchFilterNamed(m.Groups["what"].Value) is { } sought)
        {
            effects.Add(new Seek(
                sought,
                m.Groups["where"].Success ? Zone.Battlefield : Zone.Hand,
                Tapped: m.Groups["tapped"].Success,
                Count: SearchCount(m.Groups["n"].Value),

                // "With mana value 3" alone is an exact match and not a ceiling, the same
                // reading the search takes: treating it as "3 or less" would find cards the
                // card does not allow, which is a strictly better card than the printed one.
                ExactManaValue: !m.Groups["dir"].Success ? ManaValueBound(m) : null,
                MinManaValue: m.Groups["dir"].Value.Equals(
                    "greater", StringComparison.OrdinalIgnoreCase) ? ManaValueBound(m) : null,
                MaxManaValue: m.Groups["dir"].Success
                    && !m.Groups["dir"].Value.Equals("greater", StringComparison.OrdinalIgnoreCase)
                        ? ManaValueBound(m)
                        : null));

            return true;
        }

        // "Until end of turn, target creature loses all abilities and has base power and
        // toughness 0/1" (CR 613.1f). Read before the animation family below, because the clause
        // is printed in front of every one of their wordings and the whole point of reading it
        // here is that they carry on reading the rest.
        if (LosesAllAbilitiesSentence().Match(sentence) is { Success: true } silencing
            && TrySilencing(silencing, targets, effects, objectNamedByTrigger))
        {
            return true;
        }

        // "Target land you control becomes a 3/3 Elemental creature until end of turn." — the
        // manland shape. Two effects for one sentence, because setting power and toughness is
        // layer 7b and adding a type is layer 4 (CR 613.1d, 613.4b), and one effect cannot be in
        // two layers. Adding the type rather than replacing is what keeps it a land, which is
        // exactly what the rider sentence goes on to say.
        m = AnimateLine().Match(sentence);
        if (m.Success && AnimationLastsTheTurn(m) && Specs.Parse(m.Groups["t"].Value) is { } animated)
        {
            var granted = m.Groups["kw"].Success ? Keywords(m.Groups["kw"].Value) : null;
            if (m.Groups["kw"].Success && granted is null)
                return false;

            targets.Add(animated);
            var index = targets.Count - 1;

            effects.Add(new PumpUntilEndOfTurn(
                GenerativeEffects.BecomesId(AnimatedTypes(m)), index));

            foreach (var subtype in AnimatedSubtypes(m))
            {
                effects.Add(new PumpUntilEndOfTurn(AnimatedSubtypeId(m, subtype), index));
            }

            if (AnimatedColors(m).ToList() is { Count: > 0 } becomes)
            {
                effects.Add(new PumpUntilEndOfTurn(
                    GenerativeEffects.BecomesColorsId(becomes), index));
            }

            effects.Add(new PumpUntilEndOfTurn(
                GenerativeEffects.SetPowerToughnessId(
                    int.Parse(m.Groups["p"].Value, CultureInfo.InvariantCulture),
                    int.Parse(m.Groups["tough"].Value, CultureInfo.InvariantCulture)),
                index));

            if (granted is { } keywords)
                effects.Add(new PumpUntilEndOfTurn(GenerativeEffects.GrantId(keywords), index));

            return true;
        }

        // The same sentence about the permanent that says it. Every land that animates itself is
        // written this way and none could be read, because the pattern above begins at a target
        // phrase and the source is not a target - the same split the pump readers already have,
        // and for the same reason.
        m = AnimateSelfLine().Match(sentence);
        if (m.Success && AnimationLastsTheTurn(m))
        {
            var grantedSelf = m.Groups["kw"].Success ? Keywords(m.Groups["kw"].Value) : null;
            if (m.Groups["kw"].Success && grantedSelf is null)
                return false;

            effects.Add(new PumpSourceUntilEndOfTurn(
                GenerativeEffects.BecomesId(AnimatedTypes(m))));

            foreach (var subtype in AnimatedSubtypes(m))
            {
                effects.Add(new PumpSourceUntilEndOfTurn(AnimatedSubtypeId(m, subtype)));
            }

            if (AnimatedColors(m).ToList() is { Count: > 0 } becomesSelf)
            {
                effects.Add(new PumpSourceUntilEndOfTurn(
                    GenerativeEffects.BecomesColorsId(becomesSelf)));
            }

            effects.Add(new PumpSourceUntilEndOfTurn(
                GenerativeEffects.SetPowerToughnessId(
                    int.Parse(m.Groups["p"].Value, CultureInfo.InvariantCulture),
                    int.Parse(m.Groups["tough"].Value, CultureInfo.InvariantCulture))));

            if (grantedSelf is { } selfKeywords)
                effects.Add(new PumpSourceUntilEndOfTurn(GenerativeEffects.GrantId(selfKeywords)));

            return true;
        }

        // "{2}: ~ becomes a copy of target artifact, creature, enchantment, or land until end of
        // turn" (CR 613.2a, 707.2). Layer 1, so everything else on the board applies on top of
        // the card it became rather than of the card it was printed as.
        //
        // The only continuous effect here that is an IEffect rather than a
        // generated id: every other one is a family with two numbers in its name, which the
        // compiler can write down while reading the card, and a copy's name carries the whole
        // copied card. Which card that is depends on the battlefield at the moment the ability
        // resolves (CR 707.2b), so it cannot be known here.
        m = BecomesACopyLine().Match(sentence);
        if (m.Success && Specs.Parse(m.Groups["t"].Value) is { Kind: TargetKind.Permanent } copiable)
        {
            targets.Add(copiable);

            // No duration read means no duration printed, which is a permanent change: a copy
            // effect is a layer, so nothing has to be taken back off the object when it ends and
            // nothing has to be written into it when it starts. Durations the engine cannot
            // express - "until your next turn", "for as long as that creature remains tapped" -
            // never reach here, because they are left inside the target phrase and refused by it.
            effects.Add(new BecomeCopyOfTarget(
                targets.Count - 1,
                UntilEndOfTurn: m.Groups["ueot"].Success || m.Groups["pre"].Success));

            return true;
        }

        // "~ becomes an artifact creature until end of turn" — the Vehicle wording, and the one
        // animation that prints no size because the permanent already has one. CR 205.1b names
        // the phrase and says the object keeps every card type and subtype it had, which is what
        // adding the two flags does and what setting a size here would have thrown away.
        m = AnimateArtifactLine().Match(sentence);
        if (m.Success
            && AnimationLastsTheTurn(m)
            && AnimationEffects(m, sets: null) is { } artifactAnimation
            && Animates(m, targets, effects, artifactAnimation))
        {
            return true;
        }

        // "Until end of turn, target creature becomes a Dragon with base power and toughness 4/4
        // and gains flying" — the same animation with its size printed after the noun instead of
        // before it. The two halves land in different layers either way (CR 613.1d, 613.4b), so
        // this is one more shape of sentence and not one more kind of effect.
        m = AnimateWithBaseLine().Match(sentence);
        if (m.Success
            && AnimationLastsTheTurn(m)
            && AnimationEffects(
                m,
                sets: (int.Parse(m.Groups["p"].Value, CultureInfo.InvariantCulture),
                    int.Parse(m.Groups["tough"].Value, CultureInfo.InvariantCulture)))
                is { } sizedAnimation
            && Animates(m, targets, effects, sizedAnimation))
        {
            return true;
        }

        // "Target permanent becomes an artifact in addition to its other types until end of turn"
        // — a type change with no size at all, because nothing about the permanent's size is
        // being touched. The phrase is CR 205.1b in so many words, so every type it already had
        // survives and the subtypes are added rather than replacing what was there.
        m = AnimateTypeOnlyLine().Match(sentence);
        if (m.Success
            && AnimationLastsTheTurn(m)
            && AnimationEffects(m, sets: null) is { } typeAnimation
            && Animates(m, targets, effects, typeAnimation))
        {
            return true;
        }

        // "~ has base power and toughness 3/3 until end of turn", and the same aimed at a target
        // below it — layer 7b, which is a different sublayer from the pump above and reaches
        // permanents the pump cannot: "gets +4/+4" on something with no printed size modifies
        // nothing (CR 613.4b, 613.4c). The source form comes first because the target grammar
        // begins at a target phrase and the source is not one.
        m = BasePowerToughnessSelfLine().Match(sentence);
        if (m.Success && SettingLastsTheTurn(m))
        {
            var setSelfKeywords = m.Groups["kw"].Success ? Keywords(m.Groups["kw"].Value) : null;
            if (m.Groups["kw"].Success && setSelfKeywords is null)
                return false;

            effects.Add(new PumpSourceUntilEndOfTurn(BaseSizeId(m)));

            if (setSelfKeywords is { } selfSet)
                effects.Add(new PumpSourceUntilEndOfTurn(GenerativeEffects.GrantId(selfSet)));

            return true;
        }

        m = BasePowerToughnessLine().Match(sentence);
        if (m.Success && SettingLastsTheTurn(m) && Specs.Parse(m.Groups["t"].Value) is { } resized)
        {
            var setKeywords = m.Groups["kw"].Success ? Keywords(m.Groups["kw"].Value) : null;
            if (m.Groups["kw"].Success && setKeywords is null)
                return false;

            targets.Add(resized);
            var resizedIndex = targets.Count - 1;

            effects.Add(new PumpUntilEndOfTurn(BaseSizeId(m), resizedIndex));

            if (setKeywords is { } setGranted)
            {
                effects.Add(new PumpUntilEndOfTurn(
                    GenerativeEffects.GrantId(setGranted), resizedIndex));
            }

            return true;
        }

        // "It's still a land." The engine already behaves that way — animation *adds* the creature
        // type rather than replacing what was there — so the sentence is true without doing
        // anything. Accepted only alongside something else, so it can never be a whole card.
        if (effects.Count > 0 && StillALandLine().IsMatch(sentence))
            return true;

        // "Each opponent sacrifices a creature" — the same choice as below, asked of somebody
        // else. The permanent is not targeted, so hexproof does not save it and there is nothing
        // to be illegal on resolution: an opponent with a creature loses one.
        var edict = EdictLine().Match(sentence);
        if (edict.Success
            && Specs.Parse("target " + edict.Groups["what"].Value.Trim()) is
            { Kind: TargetKind.Permanent } edicted)
        {
            effects.Add(new ChooseAndMove(
                edicted,
                Zone.Graveyard,
                MoveCause.Sacrifice,
                effects.Count,
                ScopeOf(edict.Groups["who"].Value)));

            return true;
        }

        // "Return a land you control to its owner's hand", "sacrifice a creature" — a choice
        // made on resolution rather than a target chosen on casting (CR 609.4).
        m = ChooseAndMoveLine().Match(sentence);
        var chosenPhrase = m.Success
            ? (m.Groups["another"].Value.Equals("another", StringComparison.OrdinalIgnoreCase)
                ? "another target "
                : "target ")
                + m.Groups["what"].Value.Trim() + " you control"
            : string.Empty;

        if (m.Success
            && Specs.Parse(chosenPhrase) is { Kind: TargetKind.Permanent } chooseable)
        {
            var verb = m.Groups["verb"].Value.ToLowerInvariant();

            effects.Add(new ChooseAndMove(
                chooseable,
                verb == "sacrifice" ? Zone.Graveyard : Zone.Hand,
                verb == "sacrifice" ? MoveCause.Sacrifice : MoveCause.Return,
                effects.Count));

            return true;
        }

        // "Copy target instant or sorcery spell" — the copy is not a new target, so the target
        // is the thing being copied and the effect aims at it.
        var copyTarget = CopyTargetSpellLine().Match(sentence);
        if (copyTarget.Success && Specs.Parse(copyTarget.Groups["t"].Value) is
            { Kind: TargetKind.SpellOnStack } copied)
        {
            targets.Add(copied);
            effects.Add(new CopySpell(TargetIndex: targets.Count - 1));
            return true;
        }

        var energy = GetEnergyLine().Match(sentence);
        if (energy.Success)
        {
            effects.Add(new GainEnergy(
                energy.Groups["e"].Value.Length / "{E}".Length,
                ScopeOf(energy.Groups["who"].Value)));

            return true;
        }

        // "You get an experience counter" (CR 122.1) - energy's twin, and the effect for it
        // has been in the engine since before anything printed the sentence. Sixteen
        // commanders hand these out and every one of them is the reason the count beside it
        // in the counting vocabulary has anything to multiply by: without this line no
        // compiled card could ever give a player one.
        var experience = GetExperienceLine().Match(sentence);
        if (experience.Success)
        {
            effects.Add(new GainExperience(
                Number(experience.Groups["n"].Value),
                ScopeOf(experience.Groups["who"].Value)));

            return true;
        }

        // "Defending player gets a poison counter" (CR 122.1, 704.5c). The effect has been in
        // the engine since toxic was read, and toxic was the only thing that could produce one:
        // the keyword assembles the ability in the compiler rather than going through a
        // sentence, so every card that prints the sentence outright was unread.
        var poison = GetPoisonLine().Match(sentence);
        if (poison.Success)
        {
            effects.Add(new GivePoisonCounters(
                Number(poison.Groups["n"].Value),
                ScopeOf(poison.Groups["who"].Value)));

            return true;
        }

        if (ProliferateLine().IsMatch(sentence))
        {
            effects.Add(new Proliferate());
            return true;
        }

        // "Counter target spell unless its controller pays {2}. Scry 1." - the offer is read at
        // the top of a whole phrase, and until this was here it was read *only* there: a line
        // with a second sentence after it split into sentences, and the first sentence reached a
        // reader that had never been told about it. Six corpus lines, and the only ones in the
        // whole corpus whose sentences each read alone while the line did not.
        if (TryUnlessTheyPay(sentence, effects, targets))
            return true;

        // "If this spell was bargained, ..." (CR 702.166c), "if this spell was cast using
        // teamwork, ..." (CR 702.194b), "if it was kicked with its {2}{R} kicker, ..."
        // (CR 702.33f) — one clause shape over three recorded facts, in both positions the cards
        // print it: leading, and trailing ("... if this spell was cast using teamwork",
        // "... unless ..."). Read here rather than as lines of their own, because unlike
        // kicker's plain clause these are printed *inside* a line. A card that reads a fact back
        // without having the ability is refused by the compiler afterwards — the sentence
        // grammar cannot see the rest of the card, so that guard lives where it can.
        if (TryCastFactRider(sentence, targets, effects, objectNamedByTrigger, out var rode))
            return rode;

        // "Transform ~" (CR 701.27a). Only ever about the source: a card that transforms
        // something else names it, and no card in the corpus does.
        //
        // 701.28 is *convert*, which this once cited. The two turn a permanent over by the same
        // physical action and 701.28a defers to 701.27a for the rules, so nothing about the code
        // was wrong — but they are different game actions (701.27b, 701.28b) and the citation
        // guard only checks that a rule exists, not that it is the right one.
        if (TransformSelfLine().IsMatch(sentence))
        {
            effects.Add(new TransformSource());
            return true;
        }

        // "Venture into the dungeon" (CR 701.49). One sentence for the whole keyword action,
        // which is what the cards print: 46 of them say it and none says what a dungeon is,
        // because the dungeon is a rules object and its rooms live in Dungeons.cs. "Venture
        // into Undercity" is CR 701.49d's named variant and compiles to the same effect
        // carrying the name - the two arms stay one alternation so no looser pattern can ever
        // send a named venture into the wrong dungeon.
        if (VentureLine().Match(sentence) is { Success: true } ventured)
        {
            effects.Add(new VentureIntoTheDungeon(
                ventured.Groups["named"].Success ? Dungeons.Undercity : null));
            return true;
        }

        // "You become the monarch" (CR 725.3). A designation rather than a counter or a token,
        // and the commonest sentence in the family by a wide margin - thirty-five cards say it
        // and nothing else about the monarch.
        if (BecomeMonarchLine().IsMatch(sentence))
        {
            effects.Add(new BecomeTheMonarch());
            return true;
        }

        // "You take the initiative" (CR 726.1). The monarch's twin a rule over, and the same
        // steep distribution: nineteen cards say exactly this after an enters trigger and
        // nothing else about the initiative. The venturing it causes is inside the effect,
        // because taking the initiative is venturing into Undercity (CR 726.2).
        if (TakeInitiativeLine().IsMatch(sentence))
        {
            effects.Add(new TakeTheInitiative());
            return true;
        }

        if (ExploreLine().IsMatch(sentence))
        {
            effects.Add(new ExploreSource());
            return true;
        }

        // ---- Effects on the source itself ------------------------------------------------
        if (ReturnSelfFromGraveyard().IsMatch(sentence))
        {
            effects.Add(new ReturnSourceToHand());
            return true;
        }

        // "It can't be regenerated" — a rider on the destruction before it, and the one place
        // in this parser where a sentence reaches back and changes what an earlier one built. It
        // has to: the words are a separate sentence and the rule they state belongs to the
        // destroy. Accepting it as a no-op would let a creature regenerate that the card says
        // cannot, which is a wrong answer nothing downstream would question.
        if (NoRegenerationLine().IsMatch(sentence))
        {
            var last = effects.Count - 1;
            if (last < 0)
                return false;

            switch (effects[last])
            {
                case DestroyTarget destroying:
                    effects[last] = destroying with { NoRegeneration = true };
                    return true;

                case ToEachPermanent { Action: GroupAction.Destroy } sweeping:
                    effects[last] = sweeping with { Action = GroupAction.DestroyNoRegeneration };
                    return true;

                default:
                    return false;
            }
        }

        // "Draw a card at the beginning of the next turn's upkeep" — a delayed ability the
        // spell leaves behind (CR 603.7a). It belongs to the player, not to a permanent, which
        // is why nothing has to survive for it to happen.
        if (DelayedDrawLine().IsMatch(sentence))
        {
            effects.Add(new DelaySourceAction("draw", State.TurnStep.Upkeep));
            return true;
        }

        // "If that creature would die this turn, exile it instead" — a rider on whatever hurt
        // it, and it needs the target that sentence already named.
        //
        // "A creature dealt damage this way" is the same rider with a wider subject: on a spell
        // that damages one target the two coincide, which is every card in the corpus that prints
        // it. On one that damages several the rider would reach only the last, so this is exact
        // for the single-target burn it appears on and narrow for anything wider.
        if (targets.Count > 0 && DiesToExileLine().IsMatch(sentence))
        {
            effects.Add(new ExileInsteadOfDying(targets.Count - 1));
            return true;
        }

        // "Shuffle ~ into its owner's library" - the beacons, which go back into the deck instead
        // of to the graveyard as they finish resolving.
        if (ShuffleSelfIntoLibraryLine().IsMatch(sentence))
        {
            effects.Add(new ShuffleSourceIntoLibrary());
            return true;
        }

        // "Target creature gains \"{T}: Add {G}\" until end of turn" - an ability handed over for
        // a turn rather than a keyword. The ability's text rides in the generated definition's id
        // and is parsed back by the same readers that read it on a card, so anything printable as
        // an ability is grantable as one.
        if (GrantsQuotedAbilityLine().Match(sentence) is { Success: true } granting)
        {
            var abilityText = granting.Groups["ability"].Value.Trim();

            // Refused rather than granted blank: a creature that gained an ability the compiler
            // could not read would look like it had it and do nothing when it was used.
            if (!CardCompiler.TryQuotedAbility(abilityText, out _, out _))
                return false;

            if (Specs.Parse(granting.Groups["t"].Value.Trim()) is not
                { Kind: TargetKind.Permanent } gainer)
            {
                return false;
            }

            targets.Add(gainer);
            var gainerIndex = targets.Count - 1;

            // "Gets +2/+2 and gains ..." is two effects, because changing power and adding an
            // ability are different layers and one effect cannot be in both.
            if (granting.Groups["p"].Success)
            {
                effects.Add(new PumpUntilEndOfTurn(
                    GenerativeEffects.PumpId(
                        Signed(granting.Groups["p"].Value),
                        Signed(granting.Groups["tough"].Value)),
                    gainerIndex));
            }

            effects.Add(new PumpUntilEndOfTurn(
                GenerativeEffects.GrantAbilityId(abilityText), gainerIndex));

            return true;
        }

        // "Learn" (CR 701.47a): reveal a Lesson from outside the game and put it into your hand,
        // *or* discard a card to draw a card. This engine has no outside-the-game zone at all -
        // decks are the whole of what a player brings - so the first half can never be taken and
        // learn is exactly the second, which fifteen cards in the corpus already print in full.
        //
        // Routed through the parser as that sentence rather than built as effects, so the keyword
        // and the cards that spell it out can never drift into meaning different things.
        if (LearnLine().IsMatch(sentence))
        {
            if (!TryParse("You may discard a card. If you do, draw a card.", out var learned))
                return false;

            effects.AddRange(Hoist(learned, targets));
            return true;
        }

        // "Return it to the battlefield under its owner's control at the beginning of the next
        // end step" - the second half of a slow flicker whose first half was its own sentence.
        // It wraps that sentence's effect rather than adding one, because only that effect knows
        // what it exiled.
        if (ReturnAtEndStepLine().IsMatch(sentence))
        {
            if (effects.Count == 0)
                return false;

            effects[^1] = new ReturnAtNextEndStep(effects[^1]);
            return true;
        }

        // "~ deals 1 damage to that creature's controller" - the punisher shape. The player is
        // read off the permanent the trigger was about rather than named by the event, which is
        // why it is its own scope.
        if (DamageSubjectControllerLine().Match(sentence) is { Success: true } punishing)
        {
            effects.Add(new DamageEach(
                Number(punishing.Groups["n"].Value), PlayerScope.SubjectController));

            return true;
        }

        // "The owner of target nonland permanent puts it on their choice of the top or bottom of
        // their library" - removal whose only mercy is that the owner picks the end.
        if (OwnerFilesLine().Match(sentence) is { Success: true } filed)
        {
            // "Its owner puts it..." names whatever the sentence before this one targeted, and
            // means nothing without one.
            if (filed.Groups["its"].Success)
            {
                if (targets.Count == 0)
                    return false;

                effects.Add(new OwnerChoosesLibraryEnd(targets.Count - 1));
                return true;
            }

            var named = filed.Groups["t"].Success
                ? filed.Groups["t"].Value
                : filed.Groups["t2"].Value;

            if (Specs.Parse(named.Trim()) is not { Kind: TargetKind.Permanent } shelved)
                return false;

            targets.Add(shelved);
            effects.Add(new OwnerChoosesLibraryEnd(targets.Count - 1));
            return true;
        }

        // "Return that card to the battlefield under your control" - the Aura and death-trigger
        // reanimators. "That card" is what the trigger was about, which by now is in a graveyard
        // under a new id.
        if (ReturnSubjectCardLine().Match(sentence) is { Success: true } raising)
        {
            effects.Add(new ReturnSubjectCardToBattlefield(
                raising.Groups["whose"].Value.StartsWith("your", StringComparison.OrdinalIgnoreCase)));

            return true;
        }

        // "Tap that creature and it doesn't untap during its controller's next untap step" -
        // the freeze that so many combat-damage triggers print. One effect, because the second
        // clause names what the first tapped.
        if (TapAndFreezeLine().Match(sentence) is { Success: true } freezing)
        {
            var who = freezing.Groups["t"].Value.Trim();

            if (Pronouns.Contains(who, StringComparer.OrdinalIgnoreCase))
            {
                effects.Add(targets.Count > 0
                    ? new TapAndFreeze(targets.Count - 1)
                    : new TapAndFreeze(Subject: EffectSubject.TriggeringObject));

                return true;
            }

            if (Specs.Parse(who) is not { Kind: TargetKind.Permanent } frozenTarget)
                return false;

            targets.Add(frozenTarget);
            effects.Add(new TapAndFreeze(targets.Count - 1));
            return true;
        }

        // "If there are no depletion counters on ~, sacrifice it." The mana lands that spend
        // themselves: the counter is removed as a cost, and this is what happens when the last
        // one has gone.
        if (NoCountersLine().Match(sentence) is { Success: true } spent)
        {
            if (CounterKindNamed(spent.Groups["kind"].Value.Trim()) is not { } kind)
                return false;

            effects.Add(new OnlyIf(
                (_, _, self) =>
                    (self.Permanent?.Counters.GetValueOrDefault(kind, 0) ?? 0) == 0,
                [new SacrificeSource()]));

            return true;
        }

        // "Sacrifice it unless {B} was spent to cast it" - a condition on how the spell was paid
        // for, not a cost to pay now. What was spent travels with the card as it resolves into
        // the permanent, so the question can still be asked once it has arrived.
        if (SacrificeUnlessSpentLine().Match(sentence) is { Success: true } paidWith)
        {
            if (ColorNamedBySymbol(paidWith.Groups["c"].Value) is not { } needed)
                return false;

            effects.Add(new OnlyIf(
                (_, _, arrived) => arrived.ManaSpent[needed] == 0,
                [new SacrificeSource()]));

            return true;
        }

        // "Sacrifice it unless you sacrifice a Forest" / "...unless you discard a card." The
        // filter vocabulary is the one searching uses, so what it cannot name leaves the line
        // unread rather than letting any card pay a cost that names a particular one.
        if (SacrificeUnlessLine().Match(sentence) is { Success: true } ransom)
        {
            // "Discard a card at random" is left unread: the engine would let the player pick,
            // and choosing beats being robbed - a strictly better card than the one printed.
            if (ransom.Groups["random"].Success)
                return false;

            var verb = ransom.Groups["verb"].Value;

            var kind = verb.StartsWith("sacrifice", StringComparison.OrdinalIgnoreCase)
                ? ChosenCostKind.SacrificePermanents
                : verb.StartsWith("return", StringComparison.OrdinalIgnoreCase)
                ? ChosenCostKind.ReturnToHand
                : ChosenCostKind.DiscardCards;

            // "Two Swamps" names the same filter as "a Swamp", so a counted noun is singularised
            // before it becomes one. Only a counted one: the article is what says whether the
            // word is plural, and "a Pegasus" is already singular - stripping its "s" asked for
            // a "Pegasu", which is not a creature type any card has.
            // "A land you control" names the same filter as "a land": the cost only ever takes
            // your own permanents, and the asker already filters by who controls them.
            var printed = ransom.Groups["what"].Value.Trim();
            if (printed.EndsWith(" you control", StringComparison.OrdinalIgnoreCase))
                printed = printed[..^" you control".Length].Trim();
            var noun = ransom.Groups["n"].Success ? Singular(printed) : printed;

            if (SearchFilterNamed(noun) is not { } ransomFilter)
                return false;

            effects.Add(new SacrificeSourceUnlessPaid(
                kind, SearchCount(ransom.Groups["n"].Value), ransomFilter));

            return true;
        }

        // "Take an extra turn after this one", and the targeted form beside it.
        if (ExtraTurnLine().Match(sentence) is { Success: true } another)
        {
            if (!another.Groups["who"].Success)
            {
                effects.Add(new TakeExtraTurn());
                return true;
            }

            if (Specs.Parse(another.Groups["who"].Value.Trim()) is not
                { Kind: TargetKind.Player } chosen)
            {
                return false;
            }

            targets.Add(chosen);
            effects.Add(new TakeExtraTurn(targets.Count - 1));
            return true;
        }

        // "Exile ~ with three time counters on it." Always printed beside suspend, which is what
        // makes the counters mean anything - the ticking and the free cast at the end are that
        // ability's, and this only starts the clock.
        if (ExileSelfWithTimeCountersLine().Match(sentence) is { Success: true } waiting)
        {
            effects.Add(new SuspendSourceOnResolve(SearchCount(waiting.Groups["n"].Value)));
            return true;
        }

        // "You may put a land card from your hand onto the battlefield." The filter vocabulary
        // is the one searching already uses, so land, creature, permanent and the coloured and
        // subtyped forms all arrive here for free - and anything it cannot name leaves the line
        // unread rather than putting the wrong sort of card onto the battlefield.
        if (PutFromHandLine().Match(sentence) is { Success: true } fromHand)
        {
            if (SearchFilterNamed(fromHand.Groups["what"].Value) is not { } handFilter)
                return false;

            effects.Add(new PutFromHandOntoBattlefield(
                handFilter, fromHand.Groups["tapped"].Success));

            return true;
        }

        // "Until end of turn, you don't lose this mana as steps and phases end." The clause
        // modifies the add that came before it, so it wraps the effect already in the list rather
        // than adding one of its own - an effect cannot see the events of the one before it, and
        // the amount to keep is exactly what that one produced.
        if (KeepManaLine().Match(sentence) is { Success: true } keep)
        {
            if (effects.Count == 0)
                return false;

            var until = keep.Groups["combat"].Success
                ? Mana.ManaPersistence.EndOfCombat
                : Mana.ManaPersistence.EndOfTurn;

            effects[^1] = new KeepManaAdded(effects[^1], until);
            return true;
        }

        // "You may play an additional land this turn" - the one-shot form. The standing
        // "on each of your turns" version is a static permission read off the permanent instead,
        // and cannot serve here because the spell printing this has already left.
        if (ExtraLandThisTurnLine().Match(sentence) is { Success: true } extraLand)
        {
            effects.Add(new GrantExtraLandDrop(
                extraLand.Groups["n"].Success ? SearchCount(extraLand.Groups["n"].Value) : 1));
            return true;
        }

        if (BecomesPreparedSentence().IsMatch(sentence))
        {
            effects.Add(new PrepareSource());
            return true;
        }

        if (SacrificeSelfLine().IsMatch(sentence))
        {
            effects.Add(new SacrificeSource());
            return true;
        }

        // "Exile ~" — only the card's own name, not "it": "exile it" is the pronoun and belongs
        // to whatever the sentence before targeted.
        if (ExileSelfLine().IsMatch(sentence))
        {
            effects.Add(new ExileSource());
            return true;
        }

        // "That player draws a card", "each opponent draws two cards" - the same draw asked of
        // somebody the sentence names by role rather than by target.
        var drawScope = ScopedDrawLine().Match(sentence);
        if (drawScope.Success)
        {
            effects.Add(new DrawCards(
                Number(drawScope.Groups["n"].Value),
                Scope: ScopeOf(drawScope.Groups["who"].Value)));

            return true;
        }

        m = DrawCardsLine().Match(sentence);
        if (m.Success && ScopeOf(m.Groups["who"].Value) == PlayerScope.You)
        {
            effects.Add(new DrawCards(Number(m.Groups["n"].Value)));
            return true;
        }

        // "Target player draws two cards" — the same effect aimed at somebody. DrawCards and
        // ChangeLife have always taken a target index; nothing was feeding them one, so every
        // card that draws or drains a *named* player went unread while the untargeted forms
        // worked.
        m = TargetDrawsLine().Match(sentence);
        if (m.Success && Specs.Parse(m.Groups["t"].Value) is { Kind: TargetKind.Player } drawer)
        {
            if (CountedBy(Number(m.Groups["n"].Value), m.Groups["foreach"]) is not { } drawn)
                return false;

            targets.Add(drawer);
            effects.Add(new DrawCards(drawn, targets.Count - 1));
            return true;
        }

        m = TargetLifeLine().Match(sentence);
        if (m.Success && Specs.Parse(m.Groups["t"].Value) is { Kind: TargetKind.Player } subject)
        {
            var losing = m.Groups["verb"].Value.StartsWith("lose", StringComparison.OrdinalIgnoreCase);

            if (CountedBy(Number(m.Groups["n"].Value), m.Groups["foreach"]) is not { } amount)
                return false;

            targets.Add(subject);
            effects.Add(new ChangeLife(losing ? -amount : amount, targets.Count - 1));
            return true;
        }

        // The refusal that used to stand here said DiscardCards named a group and not a target.
        // It had stopped being true: the effect grew a nullable TargetIndex and resolves it the
        // way drawing and milling already did, and nothing came back to the reader that was
        // waiting for it. Twenty-odd lines were unread for a reason that had been fixed.
        m = TargetDiscardsLine().Match(sentence);
        if (m.Success && Specs.Parse(m.Groups["t"].Value) is { Kind: TargetKind.Player } holder)
        {
            if (CountedBy(Number(m.Groups["n"].Value), m.Groups["foreach"]) is not { } howMany)
                return false;

            targets.Add(holder);
            effects.Add(new DiscardCards(howMany, TargetIndex: targets.Count - 1));

            return true;
        }

        // The subject group is shared with the group grammar, so "each opponent gains 2 life"
        // would reach here too — and it is not the controller gaining it. Only "you", or no
        // subject at all, means the controller (CR 608.2).
        // "Each opponent loses 3 life. You gain life equal to the life lost this way." The
        // number is what the sentence before this one just did, which the resolution loop already
        // carries forward - so this is the same "that much" in a longer grammar.
        //
        // Only loss and damage: "the damage prevented this way" is deliberately not read, because
        // prevention is a shield here and nothing records how much of it was spent. Reading it
        // would gain nothing rather than the printed amount, which is a wrong card either way.
        if (LifeLostThisWayLine().IsMatch(sentence))
        {
            effects.Add(new ChangeLife(ThatMany()));
            return true;
        }

        m = GainLife().Match(sentence);
        if (m.Success && ScopeOf(m.Groups["who"].Value) == PlayerScope.You)
        {
            effects.Add(new ChangeLife(Number(m.Groups["n"].Value)));
            return true;
        }

        m = LoseLife().Match(sentence);
        if (m.Success && ScopeOf(m.Groups["who"].Value) == PlayerScope.You)
        {
            effects.Add(new ChangeLife(-Number(m.Groups["n"].Value)));
            return true;
        }

        // "~ gets +N/+N until end of turn", with or without the keyword half — the same effect
        // aimed at the source instead. One matcher for both halves, the way the targeted reader
        // below takes all four of its shapes at once: the card prints the grant inside the same
        // sentence, and a second matcher for the longer form is a second matcher that has to
        // agree about the size. Reading only the shorter one is what left "~ gets +1/+0 and
        // gains trample until end of turn" unread on 34 corpus lines whose targeted twin worked.
        m = PumpSelf().Match(sentence);
        if (m.Success)
        {
            // A keyword list with a word the engine does not model reads as no keywords at all,
            // so a card would quietly get the pump and lose the trample. Refused for the same
            // reason the targeted form refuses it: half a combat trick is worse than none.
            var selfGranted = m.Groups["kw"].Success ? Keywords(m.Groups["kw"].Value) : null;
            if (m.Groups["kw"].Success && selfGranted is null)
                return false;

            effects.Add(new PumpSourceUntilEndOfTurn(
                GenerativeEffects.PumpId(Signed(m.Groups["p"].Value), Signed(m.Groups["tough"].Value))));

            // Two effects for one sentence, because modifying power is layer 7c and adding an
            // ability is layer 6 (CR 613.4c, 613.1f) — one continuous effect cannot be in both.
            if (selfGranted is { } selfKeywords)
                effects.Add(new PumpSourceUntilEndOfTurn(GenerativeEffects.GrantId(selfKeywords)));

            // "~ gets +3/-1 until end of turn and can attack this turn as though it didn't
            // have defender" - the wall-animation shape, where the permission is the whole
            // point and the pump is what it costs. A third effect rather than a flag on the
            // pump: the permission is not a size change, and it is the same named effect the
            // static form uses, so both spellings of the card reach one place.
            if (m.Groups["mayattack"].Success)
            {
                effects.Add(new PumpSourceUntilEndOfTurn(
                    GenerativeEffects.MayAttackAsThoughNoDefenderId()));
            }

            return true;
        }

        // "Target creature gets +N/+N", "gains trample", or both, until end of turn — the combat
        // trick shape. Two effects when it is both, because changing power and granting an
        // ability are different layers (CR 613.1f, 613.4c) and a single effect cannot be in two.
        m = PumpOrGrantLine().Match(sentence);
        if (m.Success && Specs.Parse(m.Groups["t"].Value) is { } pumped)
        {
            var power = m.Groups["p"];
            var keywordWords = m.Groups["kw"];
            var granted = keywordWords.Success ? Keywords(keywordWords.Value) : null;

            // A keyword list with a word the engine does not model reads as no keywords at all,
            // and granting half of what the card says is worse than leaving the line unread.
            if (keywordWords.Success && granted is null)
                return false;

            targets.Add(pumped);
            var index = targets.Count - 1;

            if (power.Success)
            {
                // "+X/+X" has no size until X does, so the continuous effect cannot be named when
                // the card compiles. Both halves are read as amounts and the definition is built
                // when the ability resolves; a printed number takes the same path with an amount
                // that happens to be constant, so there is one reader rather than two that have to
                // agree about the target phrase.
                var toughnessText = m.Groups["tough"].Value;

                effects.Add(
                    power.Value.Contains('X', StringComparison.OrdinalIgnoreCase)
                    || toughnessText.Contains('X', StringComparison.OrdinalIgnoreCase)
                        ? new PumpTargetByVariable(
                            SignedAmount(power.Value), SignedAmount(toughnessText), index)
                        : new PumpUntilEndOfTurn(
                            GenerativeEffects.PumpId(
                                Signed(power.Value), Signed(toughnessText)),
                            index));
            }

            if (granted is { } keywords)
                effects.Add(new PumpUntilEndOfTurn(GenerativeEffects.GrantId(keywords), index));

            return effects.Count > 0;
        }

        // "that creature gets +N/+N until end of turn" — exalted's tail, where "that creature"
        // is the one that attacked alone. With a lone attacker there is only one it can mean.
        m = ThatCreaturePumps().Match(sentence);
        if (m.Success)
        {
            effects.Add(new PumpSourceUntilEndOfTurn(
                GenerativeEffects.PumpId(Signed(m.Groups["p"].Value), Signed(m.Groups["tough"].Value))));
            return true;
        }

        // "it gets +1/+1 until end of turn for each other Goblin you control" — the same tail
        // with its size counted rather than printed. The group rides in the effect's id and is
        // counted when the layer applies, so a Goblin that dies in response makes it smaller.
        m = ItPumpsPerEach().Match(sentence);
        if (m.Success
            && Counting(m.Groups["group"].Value.Trim(), hasSource: false) is not null)
        {
            effects.Add(new PumpSourceUntilEndOfTurn(
                GenerativeEffects.PerEachPumpId(
                    Signed(m.Groups["p"].Value),
                    Signed(m.Groups["tough"].Value),
                    m.Groups["group"].Value.Trim())));

            return true;
        }

        // "It gets +N/+N until end of turn" — the same word, and the same order of answers as
        // everywhere else: the target the sentence before it chose, then the object the trigger
        // was about, then the creature with the ability. "Untap target creature. It gets +2/+2"
        // means the creature that was untapped, not the card that said so.
        //
        // The middle answer was missing here while the rest of the parser had it, and the card
        // that shows the difference is Briar Patch: "whenever a creature attacks you, it gets
        // -1/-0 until end of turn" shrank the enchantment, which is not a creature, so the card
        // compiled complete and did nothing at all.
        m = ItPumps().Match(sentence);
        if (m.Success)
        {
            // The keyword half is refused whole for the same reason it is on the other three
            // pump readers: a word the engine cannot grant would silently drop out and leave the
            // pump behind, which is a different card rather than a smaller one.
            var alsoGains = m.Groups["kw"].Success ? Keywords(m.Groups["kw"].Value) : null;
            if (m.Groups["kw"].Success && alsoGains is null)
                return false;

            var pumpId = GenerativeEffects.PumpId(
                Signed(m.Groups["p"].Value), Signed(m.Groups["tough"].Value));

            effects.Add(PumpPronoun(pumpId));

            // Layer 6 beside layer 7c (CR 613.1f, 613.4c), aimed at whichever of the three the
            // pump was: reading the pronoun twice is what keeps the pair on one permanent.
            if (alsoGains is { } gained)
                effects.Add(PumpPronoun(GenerativeEffects.GrantId(gained)));

            return true;

            IEffect PumpPronoun(string definitionId) =>
                targets.Count > 0 ? new PumpUntilEndOfTurn(definitionId, targets.Count - 1)
                : objectNamedByTrigger
                    ? new PumpUntilEndOfTurn(definitionId, 0, EffectSubject.TriggeringObject)
                    : new PumpSourceUntilEndOfTurn(definitionId);
        }

        m = MassPump().Match(sentence);
        if (m.Success)
        {
            effects.Add(new PumpCreaturesYouControl(
                GenerativeEffects.PumpId(Signed(m.Groups["p"].Value), Signed(m.Groups["tough"].Value))));
            return true;
        }

        // "Enchanted creature gets +0/+1 until end of turn" — an Aura pumping what it is on,
        // on demand rather than continuously. It names no target: "enchanted creature" is
        // whatever the Aura is already attached to.
        m = HostPumpLine().Match(sentence);
        if (m.Success)
        {
            effects.Add(new PumpHostUntilEndOfTurn(
                GenerativeEffects.PumpId(
                    Signed(m.Groups["p"].Value), Signed(m.Groups["tough"].Value))));
            return true;
        }

        // "~ can't be blocked this turn" — the same self-grant written as a rule rather than a
        // keyword. It reaches the same flag the blocking rules already ask for, because the two
        // sentences mean one thing (CR 509.1b).
        if (SelfUnblockableLine().IsMatch(sentence))
        {
            effects.Add(new PumpSourceUntilEndOfTurn(
                GenerativeEffects.GrantId(KeywordAbility.CantBeBlocked)));
            return true;
        }

        // "Return it to its owner's hand" with nothing named before it — the tail of a dies
        // trigger, where "it" is the card that just went to the graveyard. Read after the
        // targeted form, which needs a target phrase and does not find one in the word "it".
        if (targets.Count == 0 && ReturnSelfToHandLine().IsMatch(sentence))
        {
            effects.Add(new ReturnSourceToHand());
            return true;
        }

        // "~ gains flying until end of turn" — a keyword granted to the source rather than a
        // target, which is the shape every firebreathing-style ability uses.
        m = SelfGrant().Match(sentence);
        if (m.Success && Keywords(m.Groups["kw"].Value) is { } selfGrant)
        {
            effects.Add(new PumpSourceUntilEndOfTurn(GenerativeEffects.GrantId(selfGrant)));
            return true;
        }

        // "have it deal 2 damage to target creature" — the inside of "you may have it deal ...",
        // which is how the cards spell an optional ping. The free-offer reader above strips the
        // "you may" and hands the rest here, and what arrives is an ordinary damage sentence with
        // its verb put in the bare infinitive: "have ~ deal" is "~ deals", one word apart.
        //
        // So it is rewritten rather than given its own effect. Every damage sentence the parser
        // knows arrives here already working - a number, a count, a group, "damage equal to its
        // power" - and none of them had to learn that the offer exists. Fourteen distinct
        // wordings across the family were checked, and all fourteen read once the verb agrees.
        //
        // "It" is refused when the trigger names an object of its own. On "whenever a Beast you
        // control enters, you may have it deal 4 damage" the pronoun is the Beast and not the
        // enchantment that said so, and the source of damage is not a detail - lifelink,
        // deathtouch and every "whenever this deals damage" trigger read it. The strict reading
        // is to leave that line unread rather than attribute the damage to the wrong permanent.
        var caused = HaveItDealLine().Match(sentence);
        if (caused.Success)
        {
            var causer = caused.Groups["who"].Value.Trim();

            return (causer.Equals("~", StringComparison.Ordinal) || !objectNamedByTrigger)
                && TryOne(
                    causer + " deals " + caused.Groups["rest"].Value,
                    targets,
                    effects,
                    objectNamedByTrigger);
        }

        // "it deals N damage to target opponent" — the tail of an enters trigger, where "it"
        // is the permanent that just arrived, so the source is the same either way.
        // "It deals 2 damage to each opponent" - the same sentence the source names itself in,
        // written the way a trigger writes it. The reader beside this one had the group form for
        // "~ deals" and this one had only the target form, so a trigger that hit a group went
        // unread on sixty-odd lines while the identical instruction on a spell read.
        m = ItDealsGroupDamage().Match(sentence);
        if (m.Success)
        {
            effects.Add(new DamageEach(
                Number(m.Groups["n"].Value), ScopeOf(m.Groups["who"].Value)));
            return true;
        }

        m = ItDealsDamage().Match(sentence);
        if (m.Success && Specs.Parse(m.Groups["t"].Value) is { } hit)
        {
            targets.Add(hit);
            effects.Add(new DealDamage(Number(m.Groups["n"].Value), targets.Count - 1));
            return true;
        }

        // "Put a +1/+1 counter on it", "on that creature", "on enchanted creature" - the same
        // counter put somewhere the sentence names by role rather than by target. "It" means the
        // target the sentence before it chose; "that creature" means what the trigger was about.
        var onSomething = PutCountersOnSubjectLine().Match(sentence);
        if (onSomething.Success)
        {
            var where = onSomething.Groups["who"].Value.Trim().ToLowerInvariant();

            EffectSubject putOn;
            var index = 0;

            if (where is "~")
            {
                putOn = EffectSubject.Source;
            }
            else if (where.StartsWith("enchanted", StringComparison.Ordinal)
                || where.StartsWith("equipped", StringComparison.Ordinal))
            {
                putOn = EffectSubject.AttachedHost;
            }
            else if (targets.Count > 0)
            {
                putOn = EffectSubject.Target;
                index = targets.Count - 1;
            }
            else
            {
                putOn = EffectSubject.TriggerSubject;
            }

            if (CounterKindNamed(onSomething.Groups["kind"].Value) is not { } kindOnSubject)
                return false;

            effects.Add(new PutCounters(
                kindOnSubject,
                Number(onSomething.Groups["n"].Value),
                index,
                putOn));

            return true;
        }


        // CR 702.131a: adapt N is "if this creature has no +1/+1 counters on it, put N +1/+1
        // counters on it" - a keyword action that is exactly one conditional effect, so it
        // compiles to that rather than to a mechanic of its own. The condition is the whole
        // keyword: a creature that has already adapted, or picked up a counter any other way,
        // gets nothing.
        // CR 701.32a: monstrosity N is adapt with a designation instead of a counter check -
        // if it is not already monstrous, put N +1/+1 counters on it and make it monstrous. The
        // designation is what the second activation reads and what three dozen cards trigger on.
        var monstrous = MonstrosityLine().Match(sentence);
        if (monstrous.Success)
        {
            effects.Add(new Monstrosity(Number(monstrous.Groups["n"].Value)));
            return true;
        }

        var adapting = AdaptLine().Match(sentence);
        if (adapting.Success)
        {
            effects.Add(new OnlyIf(
                (state, _, source) => state.TryGetObject(source.Id, out var self)
                    && (self.Permanent?.Counters.GetValueOrDefault(CounterKinds.PlusOnePlusOne) ?? 0) == 0,
                [
                    new PutCounters(
                        CounterKinds.PlusOnePlusOne,
                        Number(adapting.Groups["n"].Value),
                        0,
                        EffectSubject.Source),
                ]));

            return true;
        }

        // The shield goes on whatever the card names, which the target grammar reads: hardcoding
        // "any target" and "target creature" left every other filter - an artifact creature, a
        // creature an opponent controls - unread, for a reader that had nothing to do with the
        // noun.
        // "The next 1 damage that would be dealt to ~ this turn is dealt to target creature you
        // control instead" - the redirection shape. Read before the prevention forms below,
        // because the words up to "this turn" are the same and only the tail says which it is.
        if (RedirectLine().Match(sentence) is { Success: true } redirecting)
        {
            if (Specs.Parse(redirecting.Groups["t"].Value.Trim()) is not
                { Kind: TargetKind.Permanent } soaker)
            {
                return false;
            }

            targets.Add(soaker);
            effects.Add(new RedirectDamage(
                Number(redirecting.Groups["n"].Value), targets.Count - 1));

            return true;
        }

        // "Prevent all damage that would be dealt to target creature this turn" - the same
        // shield with no number on it. A shield large enough never to run out is exactly that for
        // as long as it lasts, and shields are cleared as the turn ends (CR 514.2), so nothing
        // can outlive the sentence that made it.
        //
        // "Dealt by" is read by the described form below rather than here: preventing what a
        // creature *deals* is a different question from shielding what reaches it, and a
        // countdown shield sitting on one permanent cannot ask it at all.
        //
        // Both of the shield readers fall through when the noun defeats them rather than
        // refusing the sentence outright. They used to return false, which ended the whole
        // parse — so "prevent all damage that would be dealt to players this turn" was decided
        // by a reader that could not say "players", and the described reader below never saw a
        // line it can read.
        if (PreventAllToLine().Match(sentence) is { Success: true } blanket)
        {
            if (blanket.Groups["t"].Value.Trim().Equals("you", StringComparison.OrdinalIgnoreCase))
            {
                effects.Add(new PreventDamage(new Amount(AllDamage), Scope: PlayerScope.You));
                return true;
            }

            if (Specs.Parse(blanket.Groups["t"].Value.Trim()) is { } allShielded)
            {
                targets.Add(allShielded);
                effects.Add(new PreventDamage(new Amount(AllDamage), targets.Count - 1));
                return true;
            }
        }

        m = PreventLine().Match(sentence);
        if (m.Success)
        {
            // "You" is the one phrasing here that is not a target at all.
            if (m.Groups["t"].Value.Trim().Equals("you", StringComparison.OrdinalIgnoreCase))
            {
                effects.Add(new PreventDamage(
                    Number(m.Groups["n"].Value), Scope: PlayerScope.You));

                return true;
            }

            if (Specs.Parse(m.Groups["t"].Value.Trim()) is { } shielded)
            {
                targets.Add(shielded);
                effects.Add(new PreventDamage(Number(m.Groups["n"].Value), targets.Count - 1));
                return true;
            }
        }

        // "Prevent all combat damage that would be dealt this turn by creatures your opponents
        // control" — the shield described rather than aimed (CR 615.1). Read after the two
        // targeted forms above so that everything they already answer for stays theirs.
        if (TryPreventDescribed(sentence, targets, effects))
            return true;

        m = ScryLine().Match(sentence);
        if (m.Success)
        {
            effects.Add(new Scry(Number(m.Groups["n"].Value)));
            return true;
        }

        m = SurveilLine().Match(sentence);
        if (m.Success)
        {
            effects.Add(new Surveil(Number(m.Groups["n"].Value)));
            return true;
        }

        // "Regenerate this creature", "regenerate target creature", "regenerate enchanted
        // creature" - one action asked of three subjects, and read as one.
        var regenerating = RegenerateLine().Match(sentence);
        if (regenerating.Success)
        {
            var who = regenerating.Groups["who"].Value.Trim();

            if (who.StartsWith("target", StringComparison.OrdinalIgnoreCase))
            {
                if (Specs.Parse(who) is not { Kind: TargetKind.Permanent } shielded)
                    return false;

                targets.Add(shielded);
                effects.Add(new Regenerate(EffectSubject.Target, targets.Count - 1));
                return true;
            }

            effects.Add(new Regenerate(
                who.StartsWith("enchanted", StringComparison.OrdinalIgnoreCase)
                || who.StartsWith("equipped", StringComparison.OrdinalIgnoreCase)
                    ? EffectSubject.AttachedHost
                    : EffectSubject.Source));

            return true;
        }

        m = PutCountersLine().Match(sentence);
        if (m.Success && Specs.Parse(m.Groups["t"].Value) is { } counted)
        {
            var howMany = Number(m.Groups["n"].Value);

            // "Put a +1/+1 counter on target creature for each artifact you control" - the same
            // counting every other amount uses, on the verb that says it most often.
            if (m.Groups["foreach"].Success)
            {
                if (CountingAmount(howMany, m.Groups["foreach"].Value.Trim()) is not { } perThing)
                    return false;

                howMany = perThing;
            }

            if (CounterKindNamed(m.Groups["kind"].Value) is not { } kindOnTarget)
                return false;

            targets.Add(counted);
            effects.Add(new PutCounters(
                kindOnTarget,
                howMany,
                targets.Count - 1));

            return true;
        }

        // "You may draw a card" - an offer with nothing to pay, which is the commonest optional
        // effect in the game and had no reader at all: 45 corpus lines say it and every one of
        // them reads with the two words taken off. Taking them off is not an option - a trigger
        // that always draws is a different card - so the sentence becomes the free offer the
        // engine already has, with its own words as the button.
        //
        // Read last, after every reader that knows a particular "you may" - "you may pay {2}",
        // "you may sacrifice a creature. If you do, ..." - so the general shape never takes a
        // sentence one of those would have understood better.
        var offered = MayDoLine().Match(sentence);
        if (offered.Success)
        {
            var scratchTargets = ImmutableList.CreateBuilder<TargetSpec>();
            var scratchEffects = ImmutableList.CreateBuilder<IEffect>();

            if (TryOne(offered.Groups["effect"].Value.Trim(), scratchTargets, scratchEffects, objectNamedByTrigger)
                && scratchEffects.Count > 0
                && !scratchEffects.Any(FindsItselfByIndex))
            {
                var offset = targets.Count;
                targets.AddRange(scratchTargets);

                effects.Add(new MayPay(
                    Mana.ManaCostSpec.Parse(string.Empty),
                    [.. scratchEffects.Select(e => EffectTargets.Shift(e, offset))],
                    [],
                    effects.Count,
                    YesLabel: char.ToUpperInvariant(offered.Groups["effect"].Value[0])
                        + offered.Groups["effect"].Value[1..].TrimEnd('.'),
                    NoLabel: "Decline"));

                return true;
            }
        }

        // "If you control an artifact, draw a card." A condition in the middle of an effect, and
        // the whole of it was already built: BoardConditions is the shared condition vocabulary
        // the statics, the activation restrictions and the trigger's intervening "if" all read
        // through, and OnlyIf is the wrapper that guards a list of effects with one. There was
        // simply no door into either from a plain sentence, so 94 cards sat one line short with
        // both halves of that line already understood.
        //
        // This is a *new* caller and not a reuse of the intervening "if", which is the thing
        // worth being careful about. CR 603.4 says an intervening "if" is checked twice - once
        // when the ability would trigger and again as it resolves - and says in as many words
        // that the rule applies only to an "if" immediately after a trigger condition; anywhere
        // else the word has its normal English meaning. So this one is checked once, when the
        // instruction is carried out in the order written (CR 608.2c). Same predicate, different
        // schedule, and reading this one twice would refuse cards the rules let through.
        //
        // Read last among the whole-sentence forms, so every reader that knows a particular "if"
        // - "if you do", "if you win the flip", "if ~ was kicked" - still sees it first.
        var conditional = ConditionalSentence().Match(sentence);
        if (conditional.Success
            && BoardConditions.Parse(conditional.Groups["cond"].Value.Trim()) is { } required)
        {
            var guarded = ImmutableList.CreateBuilder<IEffect>();

            // Targets go into the caller's list rather than a scratch one, so an effect inside
            // the guard records the index it will actually be read at - the same reason the
            // "where X is the number of" wrapper above does it.
            //
            // The whole remainder is handed over as one instruction, and a remainder that cannot
            // be read leaves the sentence unread rather than falling through to the "and" split.
            // That split would take "if you control an artifact, draw a card and gain 2 life"
            // apart at the conjunction, guard the draw and leave the life gain unconditional -
            // which is a card strictly better than the one printed.
            if (!TryOne(
                    conditional.Groups["effect"].Value.Trim(),
                    targets,
                    guarded,
                    objectNamedByTrigger)
                || guarded.Count == 0
                || guarded.Any(FindsItselfByIndex))
            {
                return false;
            }

            effects.Add(new OnlyIf(required, guarded.ToImmutable()));
            return true;
        }

        return TrySplitOnAnd(sentence, targets, effects);
    }

    /// <summary>
    /// A last resort: "You gain 3 life and draw a card" is two instructions joined by "and".
    /// </summary>
    /// <remarks>
    /// The sentence splitter deliberately does not split on " and ", because it joins clauses on
    /// some cards and parts of one clause on others — "gets +2/+2 and gains flying" is a single
    /// effect on a single target. This is safe where a blanket split would not be, for two
    /// reasons: it runs only after every whole-sentence form has already failed, so anything the
    /// parser understands as one thing is still read as one thing; and it accepts the split only
    /// if <em>every</em> half parses on its own, so a sentence torn in the wrong place fails
    /// rather than being half-read.
    /// <para>
    /// The builders are handed on directly rather than into a scratch copy, because an effect
    /// records its target as an index into them — and are truncated back on failure, so a
    /// rejected split leaves nothing behind.
    /// </para>
    /// </remarks>
    private static bool TrySplitOnAnd(
        string sentence,
        ImmutableList<TargetSpec>.Builder targets,
        ImmutableList<IEffect>.Builder effects)
    {
        var parts = AndSeparator().Split(sentence);
        if (parts.Length < 2)
            return false;

        var targetsBefore = targets.Count;
        var effectsBefore = effects.Count;

        foreach (var part in parts)
        {
            var one = part.Trim();
            if (one.Length > 0 && TryOne(one, targets, effects))
                continue;

            while (targets.Count > targetsBefore)
                targets.RemoveAt(targets.Count - 1);

            while (effects.Count > effectsBefore)
                effects.RemoveAt(effects.Count - 1);

            return false;
        }

        return effects.Count > effectsBefore;
    }

    /// <summary>
    /// A described prevention shield — "prevent all damage that would be dealt to X by Y this
    /// turn" (CR 615.1).
    /// </summary>
    /// <remarks>
    /// The half of prevention that names what it shields by description instead of aiming at it,
    /// which is most of the printed lines: "…to creatures you control", "…to players", "…by
    /// creatures" name no target at all, so the countdown shield above had nowhere to be put and
    /// left them unread. <see cref="PreventDescribedDamage"/> and the whole
    /// <see cref="State.PreventionEffect"/> machinery behind it were already built and tested;
    /// nothing in the compiler emitted one.
    /// <para>
    /// <strong>Only the "this turn" wordings.</strong> A prevention with no duration is one a
    /// permanent's static ability generates and lasts as long as that permanent does — and
    /// nothing removes such an effect when the permanent leaves, so reading "prevent all combat
    /// damage that would be dealt to enchanted creature" here would leave a shield on the board
    /// after the aura was destroyed. Those lines stay unread rather than being answered with a
    /// shield that outlives its card.
    /// </para>
    /// <para>
    /// The number is likewise refused. "Prevent the next 3 damage" is CR 615.7's countdown, a
    /// pool of points spent as damage arrives, while <see cref="PreventDescribedDamage.Amount"/>
    /// is CR 615.10's cap that applies afresh to every damage event — reading one as the other
    /// gives a card an unbounded shield it was never printed with.
    /// </para>
    /// </remarks>
    private static bool TryPreventDescribed(
        string sentence,
        ImmutableList<TargetSpec>.Builder targets,
        ImmutableList<IEffect>.Builder effects)
    {
        // No duration at all is a permanent's static ability, which the compiler reads into a
        // replacement effect that functions from the battlefield. Compiled here it would be a
        // shield the engine kept until the turn ended — put up by a card that has since gone to
        // a graveyard, or by an Aura that has been destroyed.
        //
        // "To and dealt by" is refused for a different reason: it is two shields around one
        // noun, and the two cards that print it with a duration name their noun with a pronoun
        // this reader cannot resolve anyway.
        if (ReadPreventionSentence(sentence) is not { ForTheTurn: true, BothWays: false } read)
            return false;

        (string? Filter, PlayerScope? Who) from = (null, null);
        TargetSpec? aimedSource = null;

        if (read.Sources is { } dealt)
        {
            if (PreventSource(dealt) is { } dealer)
            {
                from = dealer;
            }
            else if (Specs.Parse(dealt) is { Kind: TargetKind.Permanent } chosen)
            {
                // "Prevent all combat damage that would be dealt by target creature this turn" —
                // one object rather than a description of one (CR 609.7a). It cannot be read as
                // a filter derived from whatever the target happens to be: that would shield
                // against every creature of the kind instead of the one the card aimed at.
                aimedSource = chosen;
            }
            else
            {
                return false;
            }
        }

        var shields = ImmutableList.CreateBuilder<IEffect>();

        // No "to" clause at all means the shield covers everyone and everything, which is what
        // a fog says: "prevent all combat damage that would be dealt this turn".
        if (read.Victims is null)
        {
            shields.Add(new PreventDescribedDamage
            {
                Kind = read.Kind,
                SourceFilter = from.Filter,
                SourceController = from.Who,
            });
        }
        else
        {
            // "To you and creatures you control" is two shields and not one: a player and a set
            // of permanents are covered by different fields, and one effect cannot hold both.
            foreach (var part in read.Victims.Split(
                " and ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // "Other creatures you control" leaves out the permanent that said it, and a
                // resolving spell is not on the battlefield to be left out. The static reader
                // fills that slot; here the word has nothing to point at, so the line is left
                // unread rather than widened to every creature.
                if (PreventVictim(part) is not { Other: false } who)
                    return false;

                shields.Add(new PreventDescribedDamage
                {
                    Kind = read.Kind,
                    PermanentFilter = who.Filter,
                    PermanentController = who.Filter is null ? null : who.Who,
                    Players = who.Filter is null ? who.Who : null,
                    SourceFilter = from.Filter,
                    SourceController = from.Who,
                });
            }
        }

        if (shields.Count == 0)
            return false;

        // The target is claimed last, so a sentence that defeats the vocabulary above leaves the
        // ability's target list exactly as it found it.
        if (aimedSource is { } spec)
        {
            targets.Add(spec);
            var index = targets.Count - 1;

            for (var i = 0; i < shields.Count; i++)
            {
                shields[i] = ((PreventDescribedDamage)shields[i]) with
                {
                    TargetIndex = index,
                    TargetIsSource = true,
                };
            }
        }

        effects.AddRange(shields);
        return true;
    }

    /// <summary>
    /// A prevention sentence taken apart — what it watches, what it shields, whose damage, and
    /// whether it lasts the turn.
    /// </summary>
    /// <param name="Kind">Combat, noncombat, or any (CR 615.1).</param>
    /// <param name="Victims">The "to …" clause, or null for everyone and everything.</param>
    /// <param name="Sources">The "by …" clause, or null for anyone's damage.</param>
    /// <param name="BothWays">
    /// Whether the two clauses are the same noun said once — "to and dealt by enchanted
    /// creature", which shields what reaches it <em>or</em> what it deals, and is therefore two
    /// shields rather than one with both slots filled.
    /// </param>
    /// <param name="ForTheTurn">Whether the sentence printed "this turn" (CR 514.2).</param>
    internal readonly record struct PreventionSentence(
        DamageKind Kind, string? Victims, string? Sources, bool BothWays, bool ForTheTurn);

    /// <summary>
    /// Reads a prevention sentence in either voice, without deciding what it means.
    /// </summary>
    /// <remarks>
    /// Shared with <c>CardCompiler.TryStaticPrevention</c>, which reads the same sentence when it
    /// carries no duration and has to compile it to something that stops when its permanent does.
    /// Both readers need the same three answers out of the same two patterns, and the sentence is
    /// awkward enough — "this turn" lands in any of three places — that a second parse of it
    /// would be a second set of the four corrections this one already took.
    /// </remarks>
    internal static PreventionSentence? ReadPreventionSentence(string sentence)
    {
        ArgumentNullException.ThrowIfNull(sentence);

        // A whole rules line arrives with its full stop; a sentence split out of one does not.
        // Both patterns end at the first period, so it is taken off here rather than in each.
        var text = sentence.Trim().TrimEnd('.').Trim();

        var passive = PreventDescribedPassiveLine().Match(text);
        var active = passive.Success
            ? Match.Empty
            : PreventDescribedActiveLine().Match(text);

        var read = passive.Success ? passive : active;
        if (!read.Success)
            return null;

        string? victims;
        string? sources;
        bool bothWays;
        bool forTheTurn;

        if (passive.Success)
        {
            if (!SplitPreventClauses(
                passive.Groups["rest"].Value, out victims, out sources, out bothWays,
                out forTheTurn))
            {
                return null;
            }
        }
        else
        {
            // "Prevent all damage that creatures would deal to players this turn" — the same
            // sentence with the dealer as its subject, which is why the two halves swap places.
            if (!SplitPreventClauses(
                    active.Groups["rest"].Value, out victims, out var trailing, out bothWays,
                    out forTheTurn)
                || trailing is not null
                || bothWays)
            {
                return null;
            }

            sources = active.Groups["by"].Value.Trim();
        }

        var kind = read.Groups["kind"].Value.Trim().ToLowerInvariant() switch
        {
            "combat" => DamageKind.Combat,
            "noncombat" => DamageKind.Noncombat,
            _ => DamageKind.Any,
        };

        return new PreventionSentence(kind, victims, sources, bothWays, forTheTurn);
    }

    /// <summary>
    /// The "to" and "by" halves of a prevention sentence, and whether it lasts the turn.
    /// </summary>
    /// <remarks>
    /// The cards print "this turn" in three different places — before the "to", after it, and
    /// after the "by" — so it is taken out wherever it is rather than pinned to a position in
    /// the pattern.
    /// <para>
    /// Its presence is <em>reported</em> rather than required, because it is the one thing that
    /// separates the two readers of this sentence. With it, the line is a one-shot effect a
    /// resolving spell creates and the shield is state that ends with the turn (CR 514.2);
    /// without it, the same words are a permanent's static ability and the shield has to stop
    /// when the permanent does (CR 611.2c). Both readers parse the clauses identically — a
    /// second copy of this vocabulary is how the two halves would start disagreeing about what
    /// "creatures you control" means.
    /// </para>
    /// </remarks>
    private static bool SplitPreventClauses(
        string rest,
        out string? victims,
        out string? sources,
        out bool bothWays,
        out bool forTheTurn)
    {
        victims = null;
        sources = null;
        bothWays = false;
        forTheTurn = false;

        const string ThisTurn = " this turn";

        var at = rest.IndexOf(ThisTurn, StringComparison.OrdinalIgnoreCase);

        // Twice is a sentence naming two durations, which neither reader can answer.
        if (at >= 0 && rest.IndexOf(ThisTurn, at + 1, StringComparison.OrdinalIgnoreCase) >= 0)
            return false;

        forTheTurn = at >= 0;

        var clauses = WhitespaceRun()
            .Replace(at >= 0 ? rest.Remove(at, ThisTurn.Length) : rest, " ")
            .Trim()
            .TrimEnd('.');

        if (clauses.Length == 0)
            return true;

        // "To and dealt by enchanted creature" is one noun in both slots, and it has to be
        // recognised before the "by" search below — which would otherwise cut the sentence at
        // that same "by" and report the shield as covering something called "and dealt".
        const string BothDirections = "to and dealt by ";
        if (clauses.StartsWith(BothDirections, StringComparison.OrdinalIgnoreCase))
        {
            var both = clauses[BothDirections.Length..].Trim();
            if (both.Length == 0)
                return false;

            victims = both;
            sources = both;
            bothWays = true;
            return true;
        }

        if (clauses.StartsWith("by ", StringComparison.OrdinalIgnoreCase))
        {
            sources = clauses[3..].Trim();
            return sources.Length > 0;
        }

        if (!clauses.StartsWith("to ", StringComparison.OrdinalIgnoreCase))
            return false;

        var body = clauses[3..];
        var by = body.IndexOf(" by ", StringComparison.OrdinalIgnoreCase);

        if (by >= 0)
        {
            sources = body[(by + 4)..].Trim();
            body = body[..by];

            if (sources.Length == 0)
                return false;
        }

        victims = body.Trim();
        return victims.Length > 0;
    }

    /// <summary>
    /// What one "to" clause shields — a set of players, or permanents answering a filter.
    /// </summary>
    /// <remarks>
    /// Null for anything the shared filter vocabulary cannot say, which is the safe answer:
    /// "attacking creatures you control" read as "creatures you control" is a strictly better
    /// card than the printed one.
    /// <para>
    /// "Other" is reported rather than answered, because what it excludes is an object and not a
    /// kind of card. Only a static ability has one to point at, so the two readers of this
    /// sentence do different things with the same word.
    /// </para>
    /// </remarks>
    internal static (string? Filter, PlayerScope? Who, bool Other)? PreventVictim(string phrase)
    {
        ArgumentNullException.ThrowIfNull(phrase);

        var what = phrase.Trim();

        switch (what.ToLowerInvariant())
        {
            case "you":
                return (null, PlayerScope.You, false);
            case "players":
            case "each player":
            case "all players":
                return (null, PlayerScope.EachPlayer, false);
            case "your opponents":
            case "each opponent":
            case "opponents":
                return (null, PlayerScope.EachOpponent, false);
            default:
                break;
        }

        // No possessive at all leaves the scope null rather than defaulting to "you": "prevent
        // all damage that would be dealt to creatures this turn" is every creature on the
        // battlefield, and Forfend read as though it said "creatures you control" is a narrower
        // card than the printed one.
        PlayerScope? whose = null;

        if (TrimTail(ref what, " you control"))
            whose = PlayerScope.You;
        else if (TrimTail(ref what, " your opponents control")
            || TrimTail(ref what, " you don't control"))
        {
            whose = PlayerScope.EachOpponent;
        }

        // "Other creatures you control" (CR 109.5's "other"). Taken off the front and handed
        // back as a flag, because the rest of the phrase is an ordinary filter and the word is
        // not a property of any card — read as part of the filter it would either match nothing
        // or, worse, be ignored, and an ignored "other" is Tajic shielding himself.
        var other = what.StartsWith("other ", StringComparison.OrdinalIgnoreCase);
        if (other)
            what = what["other ".Length..].Trim();

        return CardFilterNamed(Singular(what)) is { } filter ? (filter, whose, other) : null;
    }

    /// <summary>What one "by" clause describes — the filter a damage source has to answer.</summary>
    internal static (string? Filter, PlayerScope? Who)? PreventSource(string phrase)
    {
        ArgumentNullException.ThrowIfNull(phrase);

        var what = phrase.Trim();
        PlayerScope? whose = null;

        if (TrimTail(ref what, " you control"))
            whose = PlayerScope.You;
        else if (TrimTail(ref what, " your opponents control")
            || TrimTail(ref what, " you don't control"))
        {
            whose = PlayerScope.EachOpponent;
        }

        // "Sources you don't control" describes nothing but who they belong to, which is a
        // shield with no filter rather than one that matches nothing.
        if (string.Equals(what, "sources", StringComparison.OrdinalIgnoreCase))
            return whose is null ? null : (null, whose);

        // "Artifact sources", "black sources" — the word "source" adds only that it is whatever
        // dealt the damage, which is the question being asked anyway.
        _ = TrimTail(ref what, " sources") || TrimTail(ref what, " source");

        return CardFilterNamed(Singular(what)) is { } filter ? (filter, whose) : null;
    }

    private static bool TrimTail(ref string phrase, string tail)
    {
        if (!phrase.EndsWith(tail, StringComparison.OrdinalIgnoreCase))
            return false;

        phrase = phrase[..^tail.Length].Trim();
        return true;
    }

    /// <summary>
    /// A list of keyword names — "trample", "flying and first strike" — as one flag set.
    /// </summary>
    /// <remarks>
    /// Null when any word is a keyword the engine does not model, because granting half of what
    /// the card says is worse than not reading the line: the card would look implemented and
    /// play differently.
    /// <para>
    /// A keyword belongs in the table only if the engine reads it off <em>computed</em>
    /// characteristics, and the table is the whole of what a granting line may say. What is
    /// left out is left out for a reason, not for want of a word: prowess, persist, undying
    /// and annihilator are triggered abilities rather than flags and cannot be granted by
    /// setting one; ward takes a cost and protection takes a quality, neither of which a flag
    /// can carry; banding's attacking half is not modelled, so the flag would be half the
    /// ability. A word absent for any other reason is a gap to close, not a boundary — this
    /// list has twice been extended by diffing it against the enum, so nothing should treat a
    /// missing word here as a fact that will keep.
    /// </para>
    /// </remarks>
    internal static KeywordAbility? Keywords(string words)
    {
        var all = KeywordAbility.None;

        foreach (var word in words.Split([" and ", ","], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = word.Trim();
            if (trimmed.Length == 0)
                continue;

            if (!GrantableKeywords.TryGetValue(trimmed, out var flag))
                return null;

            all |= flag;
        }

        return all == KeywordAbility.None ? null : all;
    }

    private static readonly Dictionary<string, KeywordAbility> GrantableKeywords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["flying"] = KeywordAbility.Flying,
            ["reach"] = KeywordAbility.Reach,
            ["first strike"] = KeywordAbility.FirstStrike,
            ["double strike"] = KeywordAbility.DoubleStrike,
            ["trample"] = KeywordAbility.Trample,
            ["deathtouch"] = KeywordAbility.Deathtouch,
            ["lifelink"] = KeywordAbility.Lifelink,
            ["vigilance"] = KeywordAbility.Vigilance,
            ["haste"] = KeywordAbility.Haste,
            ["hexproof"] = KeywordAbility.Hexproof,
            ["indestructible"] = KeywordAbility.Indestructible,
            ["menace"] = KeywordAbility.Menace,
            ["shroud"] = KeywordAbility.Shroud,
            ["defender"] = KeywordAbility.Defender,
            ["flash"] = KeywordAbility.Flash,
            ["horsemanship"] = KeywordAbility.Horsemanship,
            ["infect"] = KeywordAbility.Infect,
            ["swampwalk"] = KeywordAbility.Swampwalk,
            ["forestwalk"] = KeywordAbility.Forestwalk,
            ["islandwalk"] = KeywordAbility.Islandwalk,
            ["mountainwalk"] = KeywordAbility.Mountainwalk,
            ["plainswalk"] = KeywordAbility.Plainswalk,
            ["protection from white"] = KeywordAbility.ProtectionFromWhite,
            ["protection from blue"] = KeywordAbility.ProtectionFromBlue,
            ["protection from black"] = KeywordAbility.ProtectionFromBlack,
            ["protection from red"] = KeywordAbility.ProtectionFromRed,
            ["protection from green"] = KeywordAbility.ProtectionFromGreen,
            ["protection from artifacts"] = KeywordAbility.ProtectionFromArtifacts,

            // The engine plays all of these and could not be told to grant any of them: the
            // evasion trio is enforced in the blocking rules, skulk and wither and changeling
            // are each read somewhere in the engine, and the words for them were simply never
            // written down here. Found by diffing this table against the enum rather than by
            // meeting a card that wanted one.
            ["fear"] = KeywordAbility.Fear,
            ["intimidate"] = KeywordAbility.Intimidate,
            ["shadow"] = KeywordAbility.Shadow,
            ["skulk"] = KeywordAbility.Skulk,
            ["wither"] = KeywordAbility.Wither,
            ["changeling"] = KeywordAbility.Changeling,
            ["can't be blocked"] = KeywordAbility.CantBeBlocked,
            ["can't block"] = KeywordAbility.CantBlock,

            // The untap step reads the computed keyword (CR 702.26a) and everything attached
            // phases out indirectly with its host, so a granted phasing is played in full -
            // "enchanted permanent has phasing" was unreadable over this one missing word.
            ["phasing"] = KeywordAbility.Phasing,
        };

    /// <summary>A signed modifier as printed, e.g. "+3" or "-1".</summary>
    private static int Signed(string word) =>
        int.Parse(word, NumberStyles.Integer | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

    /// <summary>The same signed number, as an amount, so that "+X" is expressible too.</summary>
    /// <remarks>
    /// "-X" is X counted the other way rather than a negative X: the amount X resolves to is
    /// whatever the caster paid, and the minus belongs to the sentence. Written as a negation of
    /// the amount for exactly that reason - the negation is applied to what it comes to, not to
    /// the zero that stands in for it while the card is being read.
    /// </remarks>
    private static Amount SignedAmount(string word)
    {
        var negative = word.StartsWith('-');

        if (!word.Contains('X', StringComparison.OrdinalIgnoreCase))
            return Signed(word);

        return negative ? -Amount.X : Amount.X;
    }

    /// <summary>
    /// The token a "create ... token" sentence describes, or null if it describes one we cannot
    /// build (CR 111.1).
    /// </summary>
    /// <remarks>
    /// A token is just a card that was never printed, so it is assembled here as a
    /// <see cref="Domain.Models.CardDefinition"/> and everything downstream treats it like any
    /// other permanent. The oracle id is derived from the description rather than invented, so
    /// two cards that make the same token make the same token — which matters, because the id is
    /// what a "how many Soldiers do you control" effect would count on.
    /// <para>
    /// Any keyword the engine does not model makes the whole sentence unreadable. A 1/1 flier
    /// compiled as a 1/1 without flying is a different card, and nothing downstream would notice.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The rest of what makes a token definition itself, folded into its id.
    /// </summary>
    /// <remarks>
    /// Hashed rather than spelled out because the text of a token's granted ability can run to a
    /// sentence, and an oracle id is a key rather than a description.
    /// <para>
    /// <strong>The hash has to be stable across runs.</strong> This id reaches the event log, and
    /// a log replayed in a later process has to name the same card - which rules out
    /// <c>string.GetHashCode</c>, randomised per process since .NET Core. FNV-1a is here because
    /// it is short, deterministic and defined by its arithmetic rather than by a runtime.
    /// </para>
    /// </remarks>
    private static string Distinguishing(
        Domain.Enums.CardType extraTypes,
        IReadOnlyList<string> subtypes,
        Domain.Enums.KeywordAbility keywords,
        string text)
    {
        var parts = $"{(long)extraTypes}|{string.Join(',', subtypes)}|{(long)keywords}|{text}";

        if (parts == "0||0|")
            return string.Empty;

        unchecked
        {
            const uint Offset = 2166136261;
            const uint Prime = 16777619;

            var hash = Offset;
            foreach (var c in parts)
            {
                hash ^= c;
                hash *= Prime;
            }

            return "-" + hash.ToString("x8", CultureInfo.InvariantCulture);
        }
    }

    private static Domain.Models.CardDefinition? TokenFrom(Match m)
    {
        var power = int.Parse(m.Groups["p"].Value, CultureInfo.InvariantCulture);
        var toughness = int.Parse(m.Groups["tough"].Value, CultureInfo.InvariantCulture);

        var colours = new List<ManaColor>();
        foreach (var word in m.Groups["colours"].Value
            .Split([" and ", ", ", " "], StringSplitOptions.RemoveEmptyEntries))
        {
            if (ColourWords.TryGetValue(word.Trim(), out var colour))
                colours.Add(colour);
            else if (!string.Equals(word.Trim(), "colorless", StringComparison.OrdinalIgnoreCase))
                return null;
        }

        var keywords = KeywordAbility.None;
        if (m.Groups["kw"].Success)
        {
            if (Keywords(m.Groups["kw"].Value) is not { } granted)
                return null;

            keywords = granted;
        }

        // A token with a quoted ability needs no new machinery at all: a token *is* a card
        // definition, so the quoted text becomes its rules text and the pool compiles it on
        // demand like any other card. Anything the compiler cannot read in there leaves the
        // whole sentence unread, which is the same promise every other template makes.
        var text = m.Groups["text"].Success ? m.Groups["text"].Value.Trim() : string.Empty;

        var subtypes = m.Groups["subtypes"].Value
            .Split([" ", "and"], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim())
            .Where(w => w.Length > 0)
            .ToArray();

        var name = string.Join(' ', subtypes);
        if (name.Length == 0)
            return null;

        var extraTypes = Domain.Enums.CardType.None;
        foreach (var word in m.Groups["types"].Value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            extraTypes |= word.Trim().ToLowerInvariant() switch
            {
                "artifact" => Domain.Enums.CardType.Artifact,
                _ => Domain.Enums.CardType.Enchantment,
            };
        }

        var token = new Domain.Models.CardDefinition
        {
            // Everything that tells one token from another goes in the id, not just its name
            // and size. Two cards that each make a 1/1 black Rat - one plain, one with an
            // ability - produced the same id for two different definitions, and the pool would
            // then have served one card's token behaviour for the other. The pool's duplicate
            // guard catches it, which is why this surfaced as a refusal to play rather than as a
            // Rat quietly gaining somebody else's ability.
            OracleId = $"token-{name}-{power}-{toughness}-"
                + string.Concat(colours.Select(c => c.ToString()[..1])).ToLowerInvariant()
                + Distinguishing(extraTypes, subtypes, keywords, text),
            Name = name,
            // A token can be more than a creature: an artifact creature token is both, and
            // reading only the creature half would put a Thopter on the board that no artifact
            // sweeper could find and no metalcraft would count.
            CardTypes = Domain.Enums.CardType.Creature | Domain.Enums.CardType.Token | extraTypes,
            Subtypes = subtypes,
            Power = power,
            Toughness = toughness,
            Keywords = keywords,
            ColorIdentity = [.. colours],
            Colors = [.. colours],
            OracleText = text,
        };

        // The promise every template makes: what the compiler cannot read, it does not claim. A
        // token whose ability went unread would arrive as a vanilla creature of the right size,
        // which looks right on the board and plays as a different card.
        //
        // Nested quotes would recurse, and no printed card nests them — a token's ability never
        // creates another token with an ability — so the guard is a refusal rather than a depth
        // counter, which would only hide the day one appears.
        if (text.Length > 0
            && (text.Contains('"', StringComparison.Ordinal)
                || !CardCompiler.Compile(token).IsComplete))
        {
            return null;
        }

        return token;
    }

    private static readonly Dictionary<string, ManaColor> ColourWords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["white"] = ManaColor.White,
            ["blue"] = ManaColor.Blue,
            ["black"] = ManaColor.Black,
            ["red"] = ManaColor.Red,
            ["green"] = ManaColor.Green,
        };

    /// <summary>
    /// The tokens the game defines by name rather than by description (CR 111.9).
    /// </summary>
    /// <remarks>
    /// A Treasure or a Clue is not described on the card that makes it — the name carries the
    /// whole definition — so the definition lives here rather than being parsed out of a sentence
    /// that does not contain it.
    /// </remarks>
    private static readonly Dictionary<string, Domain.Models.CardDefinition> PredefinedTokens =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Treasure"] = new()
            {
                OracleId = "token-treasure",
                Name = "Treasure",
                CardTypes = Domain.Enums.CardType.Artifact | Domain.Enums.CardType.Token,
                Subtypes = ["Treasure"],
                OracleText = "{T}, Sacrifice this artifact: Add one mana of any color.",
            },
            // CR 111.10h. The restriction is the whole of what a Powerstone is - mana that
            // cannot cast a nonartifact spell - so a Powerstone whose text lost it would be a
            // strictly better token than the one the card makes.
            ["Powerstone"] = new()
            {
                OracleId = "token-powerstone",
                Name = "Powerstone",
                CardTypes = Domain.Enums.CardType.Artifact | Domain.Enums.CardType.Token,
                Subtypes = ["Powerstone"],
                OracleText =
                    "{T}: Add {C}. This mana can't be spent to cast a nonartifact spell.",
            },
            ["Clue"] = new()
            {
                OracleId = "token-clue",
                Name = "Clue",
                CardTypes = Domain.Enums.CardType.Artifact | Domain.Enums.CardType.Token,
                Subtypes = ["Clue"],
                OracleText = "{2}, Sacrifice this artifact: Draw a card.",
            },
            ["Food"] = new()
            {
                OracleId = "token-food",
                Name = "Food",
                CardTypes = Domain.Enums.CardType.Artifact | Domain.Enums.CardType.Token,
                Subtypes = ["Food"],
                OracleText = "{2}, {T}, Sacrifice this artifact: You gain 3 life.",
            },

            // The rest of CR 111.10's artifact tokens, copied from the rule rather than from any
            // card: the text is the same on every card that makes one, which is the whole point
            // of a predefined token. Their bodies go through the ordinary compiler, so each is
            // only as playable as the sentence in it - and a token whose text this compiler
            // cannot read leaves its card incomplete rather than arriving as a blank artifact.
            ["Gold"] = new()
            {
                OracleId = "token-gold",
                Name = "Gold",
                CardTypes = Domain.Enums.CardType.Artifact | Domain.Enums.CardType.Token,
                Subtypes = ["Gold"],
                OracleText = "Sacrifice this artifact: Add one mana of any color.",
            },
            ["Blood"] = new()
            {
                OracleId = "token-blood",
                Name = "Blood",
                CardTypes = Domain.Enums.CardType.Artifact | Domain.Enums.CardType.Token,
                Subtypes = ["Blood"],
                OracleText = "{1}, {T}, Discard a card, Sacrifice this artifact: Draw a card.",
            },
            ["Lander"] = new()
            {
                OracleId = "token-lander",
                Name = "Lander",
                CardTypes = Domain.Enums.CardType.Artifact | Domain.Enums.CardType.Token,
                Subtypes = ["Lander"],
                OracleText = "{2}, {T}, Sacrifice this artifact: Search your library for a basic "
                    + "land card, put it onto the battlefield tapped, then shuffle.",
            },
            ["Map"] = new()
            {
                OracleId = "token-map",
                Name = "Map",
                CardTypes = Domain.Enums.CardType.Artifact | Domain.Enums.CardType.Token,
                Subtypes = ["Map"],
                OracleText = "{1}, {T}, Sacrifice this artifact: Target creature you control "
                    + "explores. Activate only as a sorcery.",
            },
            ["Junk"] = new()
            {
                OracleId = "token-junk",
                Name = "Junk",
                CardTypes = Domain.Enums.CardType.Artifact | Domain.Enums.CardType.Token,
                Subtypes = ["Junk"],
                OracleText = "{T}, Sacrifice this artifact: Exile the top card of your library. "
                    + "You may play that card this turn. Activate only as a sorcery.",
            },
            ["Mutagen"] = new()
            {
                OracleId = "token-mutagen",
                Name = "Mutagen",
                CardTypes = Domain.Enums.CardType.Artifact | Domain.Enums.CardType.Token,
                Subtypes = ["Mutagen"],
                OracleText = "{1}, {T}, Sacrifice this artifact: Put a +1/+1 counter on target "
                    + "creature. Activate only as a sorcery.",
            },
        };

    /// <summary>
    /// One of those tokens by name, for a caller that is not reading a card (CR 111.9).
    /// </summary>
    /// <remarks>
    /// A dungeon room says "create a Treasure token" and there is no card to parse it out of, so
    /// the rules ask for the same definition every card gets. A second copy of the Treasure would
    /// be a second Treasure — two oracle ids for one token, and every count of them wrong.
    /// </remarks>
    public static Domain.Models.CardDefinition PredefinedToken(string name) =>
        PredefinedTokens.TryGetValue(name, out var token)
            ? token
            : throw new ArgumentOutOfRangeException(
                nameof(name), $"No predefined token is named '{name}'.");

    /// <summary>The verb a sweeper is printed with, as what it does to each permanent.</summary>
    private static GroupAction GroupActionOf(string verb) => verb.ToLowerInvariant() switch
    {
        "exile" => GroupAction.Exile,
        "tap" => GroupAction.Tap,
        "untap" => GroupAction.Untap,
        "return" => GroupAction.ReturnToHand,
        _ => GroupAction.Destroy,
    };

    /// <summary>Where a card leaving a graveyard is going.</summary>
    private static Zone GraveyardDestination(string phrase) =>
        phrase.Contains("battlefield", StringComparison.OrdinalIgnoreCase) ? Zone.Battlefield
        : phrase.Contains("exile", StringComparison.OrdinalIgnoreCase) ? Zone.Exile
        : Zone.Hand;

    /// <summary>
    /// The search filter a printed phrase names, or null if it names one we cannot honour.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow. A filter that accepted more than the card says would let a player
    /// fetch something the card forbids, which is worse than the line going unread.
    /// </remarks>
    /// <summary>
    /// The filter vocabulary, for callers outside the phrase grammar.
    /// </summary>
    /// <remarks>
    /// A cost reducer names what it discounts the same way a tutor names what it finds, so it
    /// reads the phrase with the same code rather than growing a second, drifting copy.
    /// </remarks>
    public static string? SearchFilterFor(string phrase) => SearchFilterNamed(phrase);

    /// <summary>
    /// The count that means "any number of", which has no ceiling until the library is looked at.
    /// </summary>
    internal const int AnyNumber = -1;

    /// <summary>How many cards "up to N" allows a search to find, one when it says nothing.</summary>
    /// <remarks>
    /// A short list rather than a call to <see cref="Number"/>, because that returns an
    /// <c>Amount</c> - a number the game works out on resolution - and this is a fixed ceiling
    /// read off the printed word. The pattern admits only these five.
    /// </remarks>
    private static int SearchCount(string word) => word.ToLowerInvariant() switch
    {
        "two" => 2,
        "three" => 3,
        "four" => 4,
        "five" => 5,
        "six" => 6,
        "seven" => 7,
        "eight" => 8,
        "nine" => 9,
        "ten" => 10,
        _ => 1,
    };

    /// <summary>
    /// The mana-value bound a search or seek prints, or null where it prints none.
    /// </summary>
    /// <remarks>
    /// An <see cref="Amount"/> rather than a number because "with mana value X or less" names one
    /// that is not on the card: X is chosen as the spell is cast or the ability activated
    /// (CR 601.2b, 602.2b), and the compiled definition is shared by every casting. The effect
    /// settles it against the resolution, so what reaches the log is still a number.
    /// <para>
    /// Shared by both grammars, because a bound is the same clause in both and a second copy
    /// would drift the first time either of them learned a word.
    /// </para>
    /// </remarks>
    private static Amount? ManaValueBound(Match m)
    {
        if (!m.Groups["cap"].Success)
            return null;

        var printed = m.Groups["cap"].Value;

        return string.Equals(printed, "X", StringComparison.Ordinal)
            ? Amount.X
            : new Amount(int.Parse(printed, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The shared card-filter vocabulary, under the name other readers ask for it by.
    /// </summary>
    /// <remarks>
    /// Exposed so a condition counting a filtered pile - "four or more creature cards in your
    /// graveyard" - asks the same vocabulary a search does. A second filter grammar would drift
    /// from this one the first time either learned a word.
    /// </remarks>
    internal static string? CardFilterNamed(string phrase) => SearchFilterNamed(phrase);

    private static string? SearchFilterNamed(string phrase)
    {
        var what = phrase.Trim();

        if (what.Length == 0)
            return SearchFilters.AnyCard;

        if (string.Equals(what, "basic land", StringComparison.OrdinalIgnoreCase))
            return SearchFilters.BasicLand;

        // "Permanent card" names the five types that make one rather than a type of its own.
        if (string.Equals(what, "permanent", StringComparison.OrdinalIgnoreCase))
            return SearchFilters.PermanentCard;

        // "A creature or land card" - each part named separately and the card answering to any
        // of them (CR 109.4). Every part has to be readable: a list with one unknown in it is
        // refused outright rather than fetched from the parts that were understood, which would
        // be a tutor that finds less than the card allows.
        if (what.Contains(" or ", StringComparison.OrdinalIgnoreCase)
            && !what.Contains("non", StringComparison.OrdinalIgnoreCase))
        {
            var parts = what
                .Replace(", or ", ",", StringComparison.OrdinalIgnoreCase)
                .Replace(" or ", ",", StringComparison.OrdinalIgnoreCase)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(SearchFilterNamed)
                .ToList();

            return parts.Count > 1 && parts.All(one => one is not null)
                ? string.Join('|', parts)
                : null;
        }

        // "Instant and sorcery cards in your graveyard" - card types joined by "and", which
        // means a card answering to either of them. It cannot mean both: nothing is an instant
        // and a sorcery at once, so reading it as a conjunction counts zero every time. Only
        // when every part is a card type - "a noncreature, nonland card" joins adjectives with
        // "and" and does mean both, and that is the ampersand's job further down.
        if (what.Contains(" and ", StringComparison.OrdinalIgnoreCase))
        {
            var joined = what
                .Split(" and ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            if (joined.Count > 1 && joined.TrueForAll(SearchableTypes.Contains))
                return string.Join('|', joined.Select(one => one.ToLowerInvariant()));
        }

        // "Mercenary permanent card" - a subtype and the word permanent, which adds nothing a
        // subtype does not already imply here: every card with that subtype is a permanent card.
        // Taken off before the words are read one by one, or "permanent" would be a word the
        // vocabulary does not know and would refuse the whole phrase.
        if (what.EndsWith(" permanent", StringComparison.OrdinalIgnoreCase))
            what = what[..^" permanent".Length].Trim();

        // A single card type, lower case as the cards print it.
        if (SearchableTypes.Contains(what))
            return what.ToLowerInvariant();

        // "Nonland", "noncreature" - the same question with the answer turned round. The filter
        // vocabulary has understood a "non" prefix since compound filters were added; this half,
        // which decides whether the compiler will accept the words at all, had not been told.
        if (what.StartsWith("non", StringComparison.OrdinalIgnoreCase)
            && SearchableTypes.Contains(what[3..]))
        {
            return what.ToLowerInvariant();
        }

        // "Mount creature", "basic Forest", "green creature", "legendary creature" - several
        // words that all have to be true of one card. Read here rather than refused for
        // containing a space, which is what used to happen: the phrase went to a caller that
        // joined it up unvalidated and the search looked for a subtype spelled "Mount creature
        // card", which nothing is.
        var words = what.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is > 1 and <= 3)
        {
            var parts = new List<string>();

            foreach (var word in words)
            {
                var atom = FilterAtom(word);
                if (atom is null)
                    return null;

                parts.Add(atom);
            }

            return string.Join('&', parts);
        }

        // Otherwise a subtype, told apart by its capital — the same rule the target grammar and
        // the token grammar use. Anything else is left unread rather than guessed at: a tutor
        // that fetched more than the card allows is a strictly better card.
        return what.Length > 1 && char.IsUpper(what[0]) && what.All(char.IsLetter) ? what : null;
    }

    /// <summary>
    /// One word of a filter phrase, as the filter vocabulary spells it, or null if unread.
    /// </summary>
    /// <remarks>
    /// A capital means a subtype, the same rule the target grammar uses. Everything else has to
    /// be a word the shared vocabulary already knows — a card type, a supertype or a colour — so
    /// a phrase with one unrecognised word in it is refused whole rather than fetched from the
    /// parts that happened to be understood.
    /// </remarks>
    private static string? FilterAtom(string word)
    {
        // "non-Lair" - a negated subtype, hyphenated as the cards print it. The matcher reads a
        // "non" prefix by taking the rest of the string, so the hyphen has to come off here or it
        // would go looking for a subtype spelled "-Lair", which nothing is.
        if (word.StartsWith("non-", StringComparison.OrdinalIgnoreCase)
            && word.Length > 4
            && char.IsUpper(word[4])
            && word[4..].All(char.IsLetter))
        {
            return "non" + word[4..];
        }

        if (word.Length > 1 && char.IsUpper(word[0]) && word.All(char.IsLetter))
            return word;

        var lower = word.ToLowerInvariant();

        // A negation is an atom too: "nonlegendary creature" is two words that both have to
        // be true, and the first of them is a "non". Without this the whole phrase was refused
        // and the card went unread - the right failure, but an avoidable one.
        if (lower.StartsWith("non", StringComparison.Ordinal) && FilterAtom(lower[3..]) is not null)
            return lower;

        return SearchableTypes.Contains(lower)
            || lower is "basic" or "legendary" or "snow" or "world"
            || lower is "white" or "blue" or "black" or "red" or "green" or "colorless"
                ? lower
                : null;
    }

    private static readonly HashSet<string> SearchableTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "creature", "artifact", "enchantment", "land", "planeswalker", "instant", "sorcery",
        };

    /// <summary>
    /// A printed counter name as the kind the engine stores (CR 122.1).
    /// </summary>
    /// <remarks>
    /// Counters are keyed by name and the engine has never cared which names exist — a permanent
    /// can hold "charge" as readily as "+1/+1". Only the compiler was pinned to the two that
    /// change power and toughness, which left every storage, depletion and age counter unread.
    /// </remarks>
    private static string? CounterKindNamed(string printed)
    {
        var word = printed.Trim();

        if (string.Equals(word, "+1/+1", StringComparison.Ordinal))
            return CounterKinds.PlusOnePlusOne;

        if (string.Equals(word, "-1/-1", StringComparison.Ordinal))
            return CounterKinds.MinusOneMinusOne;

        // "+4/+4", "-0/-2", "+2/+0" — a counter shaped like the two above and not one of them.
        // This was refused outright until the layers could tell what such a name means: power and
        // toughness came out of a list of two, so a +1/+0 counter would have gone onto the
        // permanent, appeared in the log and changed nothing, on a card that compiled complete
        // and passed the legality gate. Now CounterKinds.PowerToughnessOf reads the name the rule
        // does (CR 122.1c) and layer 7c applies whatever it says, so the name is kept as printed
        // and there is nothing left to refuse.
        //
        // Kept exactly as printed rather than lowercased with the rest, because a name made of
        // digits and signs has no case to fold and the counter has to be the same string every
        // other reader of the card writes.
        if (CounterKinds.PowerToughnessOf(word) is not null)
            return word;

        // Anything else is a named counter — charge, storage, depletion, age — and the engine
        // has never cared which names exist. Those are kept exactly as printed.
        return word.ToLowerInvariant();
    }

    /// <summary>
    /// "One mana of any color" and the rest of the family whose colour is decided on resolution.
    /// </summary>
    /// <remarks>
    /// Returns the effect with its player scope still unset, because the words that name whose
    /// pool it goes into sit in front of the verb and are read by the caller.
    /// <para>
    /// "N mana of any one color" and "N mana in any combination of colors" differ by exactly one
    /// thing and it is not the count: the first is one colour for all of it, the second a colour
    /// per mana. Writing them as one matcher with a flag is what keeps that difference visible -
    /// two matchers would let one of them quietly acquire the other's reading.
    /// </para>
    /// </remarks>
    private static AddChosenMana? ChosenMana(string text)
    {
        var m = ChosenColorManaLine().Match(text);
        if (!m.Success)
            return null;

        var many = Number(m.Groups["n"].Value);

        // X is chosen as the spell is cast and is not a number this reader has; a count of zero
        // adds nothing and is not worth an effect. Both stay unread.
        if (many.IsVariable || many.Fixed < 1)
            return null;

        return new AddChosenMana(
            many.Fixed,
            m.Groups["produced"].Success
                ? ManaPalette.TypesTheSubjectProduces
                : ManaPalette.AnyColor,
            EachSeparately: m.Groups["combination"].Success);
    }

    /// <summary>Which players a printed group word names (CR 109.5).</summary>
    /// <summary>
    /// Which layer 7b setting a "has base ..." clause asks for (CR 613.4b).
    /// </summary>
    /// <remarks>
    /// Four corpus cards print a power with no toughness beside it - Singing Tree, Crater
    /// Elemental, Island of Wak-Wak and Symmetry Sage - and the pair form had read for months
    /// while they sat in the unread pile one word apart from it. Filling the toughness in would
    /// have set a number the card never printed, so the missing half stays missing.
    /// </remarks>
    private static string BaseSizeId(Match m)
    {
        var power = int.Parse(m.Groups["p"].Value, CultureInfo.InvariantCulture);

        return m.Groups["tough"].Success
            ? GenerativeEffects.SetPowerToughnessId(
                power, int.Parse(m.Groups["tough"].Value, CultureInfo.InvariantCulture))
            : GenerativeEffects.SetPowerId(power);
    }

    /// <summary>
    /// Which target's controller a subject names, or null when it names a scope instead.
    /// </summary>
    /// <remarks>
    /// "Its controller" is two different players depending on what the sentence in front of it
    /// did, and the guard in <see cref="TryOne"/> has already decided which: with nothing
    /// targeted the words are rewritten to the one spelling the scope vocabulary knows, so
    /// anything still saying "its controller" here is talking about a target. That is why this
    /// asks about the printed words rather than re-deciding - two places deciding one question
    /// is how "destroy target creature, its controller creates a token" comes to make the token
    /// for the wrong player.
    /// <para>
    /// Null when nothing has been targeted at all, which leaves the sentence unread rather than
    /// pointing an index at a target that is not there.
    /// </para>
    /// </remarks>
    private static int? TargetsControllerMakes(
        string subject, ImmutableList<TargetSpec>.Builder targets) =>
        targets.Count > 0 && TargetsControllerWords().IsMatch(subject)
            ? targets.Count - 1
            : null;

    /// <summary>"Its controller", "that creature's controller" - as a subject, and after a target.</summary>
    [GeneratedRegex(@"^(its|that [a-z]+'s) controller$", RegexOptions.IgnoreCase)]
    private static partial Regex TargetsControllerWords();

    private static PlayerScope ScopeOf(string word) => word.ToLowerInvariant() switch
    {
        "each opponent" => PlayerScope.EachOpponent,
        "each other player" => PlayerScope.EachOtherPlayer,
        "each player" => PlayerScope.EachPlayer,
        "that player" => PlayerScope.TriggerSubject,
        "defending player" => PlayerScope.DefendingPlayer,
        "enchanted player" => PlayerScope.EnchantedPlayer,
        SubjectControllerWord => PlayerScope.SubjectController,
        _ => PlayerScope.You,
    };

    /// <summary>
    /// What "its controller" and "that creature's controller" are rewritten to before the shared
    /// player vocabulary reads them.
    /// </summary>
    /// <remarks>
    /// A word no card prints, so the vocabulary can carry one spelling of a relation the cards
    /// write half a dozen ways — "that spell's controller", "that permanent's controller", "that
    /// land's controller". The rewrite is what applies the guard; this is only its output.
    /// </remarks>
    private const string SubjectControllerWord = "the subject's controller";

    /// <summary>
    /// Those words as the <em>subject</em> of a sentence, which is the only place they name a
    /// player rather than qualify something else.
    /// </summary>
    /// <remarks>
    /// Anchored, and that is the whole of the difference. "That creature doesn't untap during
    /// its controller's next untap step" carries the same words in the middle of a sentence
    /// about something else, and a pattern matching them anywhere reached ninety-eight cards
    /// that were never this reader's business.
    /// </remarks>
    [GeneratedRegex(@"^(its|that [a-z]+'s) controller\b", RegexOptions.IgnoreCase)]
    private static partial Regex SubjectControllerPhrase();

    /// <summary>
    /// "Spend this mana only to cast creature spells" — a restriction on the mana (CR 106.6).
    /// </summary>
    /// <remarks>
    /// Returns null for anything it does not recognise, and the caller leaves the whole line
    /// unread when it does. That matters more here than in most places: mana with a restriction
    /// the engine dropped is strictly better than the mana printed, and a land that taps for
    /// unrestricted mana is a different and better card than the one in the deck.
    /// <para>
    /// Subtypes and supertypes are deliberately not read — "spend this mana only to cast Dragon
    /// spells", "only to cast legendary spells". The restriction is a card-type filter, and
    /// answering a subtype question with a type filter would say yes to every creature.
    /// </para>
    /// </remarks>
    public static ManaRestriction? RestrictionFor(string sentence)
    {
        // "This mana can't be spent to cast a nonartifact spell" - the negative form, which is not
        // the same restriction said backwards: it forbids one kind of casting and leaves
        // activating an ability with the mana entirely alone.
        var forbidden = CantSpendLine().Match(sentence ?? string.Empty);
        if (forbidden.Success)
        {
            if (!forbidden.Groups["types"].Success)
            {
                // "This mana can't be spent to cast spells" - no casting at all, anything else
                // allowed.
                return new ManaRestriction(ManaPurpose.ActivateAbility, CardType.None);
            }

            if (TypesInRestriction(forbidden.Groups["types"].Value) is not { } allowed)
                return null;

            return new ManaRestriction(
                ManaPurpose.CastSpell | ManaPurpose.ActivateAbility,
                allowed)
            {
                TypesOnlyWhenCasting = true,
            };
        }

        var m = SpendOnlyLine().Match(sentence ?? string.Empty);
        if (!m.Success)
            return null;

        var purposes = ManaPurpose.Other;
        var types = CardType.None;

        // "Cast artifact spells or activate abilities of artifacts" is one restriction with two
        // purposes over one type, so the clauses are read into the same pair rather than into a
        // list of restrictions that would then have to be OR-ed at payment time.
        foreach (var clause in SpendClauseSeparator().Split(m.Groups["what"].Value))
        {
            var trimmed = clause.Trim();
            if (trimmed.Length == 0)
                continue;

            var cast = SpendCastClause().Match(trimmed);
            if (cast.Success)
            {
                purposes |= ManaPurpose.CastSpell;
                if (TypesInRestriction(cast.Groups["types"].Value) is not { } castTypes)
                    return null;

                types |= castTypes;
                continue;
            }

            var activate = SpendActivateClause().Match(trimmed);
            if (activate.Success)
            {
                purposes |= ManaPurpose.ActivateAbility;
                if (TypesInRestriction(activate.Groups["types"].Value) is not { } fromTypes)
                    return null;

                types |= fromTypes;
                continue;
            }

            return null;
        }

        return purposes == ManaPurpose.Other ? null : new ManaRestriction(purposes, types);
    }

    /// <summary>The card types named in a restriction, or null if one of them is not a type.</summary>
    private static CardType? TypesInRestriction(string words)
    {
        var trimmed = words.Trim();
        if (trimmed.Length == 0)
            return CardType.None;

        var types = CardType.None;
        foreach (var word in RestrictionTypeSeparator().Split(trimmed))
        {
            var singular = word.Trim().TrimEnd('s');
            if (singular.Length == 0)
                continue;

            var one = singular.ToLowerInvariant() switch
            {
                "creature" => (CardType?)CardType.Creature,
                "artifact" => CardType.Artifact,
                "instant" => CardType.Instant,
                "sorcery" or "sorcerie" => CardType.Sorcery,
                "enchantment" => CardType.Enchantment,
                "land" => CardType.Land,
                "planeswalker" => CardType.Planeswalker,
                "battle" => CardType.Battle,
                _ => null,
            };

            if (one is null)
                return null;

            types |= one.Value;
        }

        return types;
    }

    /// <summary>
    /// "Exile target creature you control, then return that card to the battlefield" (CR 400.7).
    /// </summary>
    /// <remarks>
    /// One effect, because what comes back is a different object and a separate return could not
    /// name it: the id the exile produced stops existing the moment the card leaves exile.
    /// </remarks>
    private static bool TryFlicker(
        string text,
        ImmutableList<IEffect>.Builder effects,
        ImmutableList<TargetSpec>.Builder targets)
    {
        var m = FlickerLine().Match(text.Trim());
        if (!m.Success)
            return false;

        // "Exile up to two target creatures you control, then return those cards ..." - a plural
        // this reader has to count for itself. The shared multi-target rewrite is a sentence
        // matcher, and a flicker is read *before* the text is split into sentences, so the
        // rewrite never sees one: Illusionist's Stratagem and Displace were unread beside a
        // reader that already did what they ask, for want of the word "two".
        var phrase = m.Groups["t"].Value.Trim();
        var howMany = 1;

        if (FlickerManyLine().Match(phrase) is { Success: true } several)
        {
            howMany = Number(several.Groups["n"].Value).Fixed;
            phrase = Singular(several.Groups["t"].Value.Trim());
        }

        if (Specs.Parse(phrase) is not { Kind: TargetKind.Permanent } blinked)
            return false;

        // Every one of them optional, because "up to two" is what the card says: a caster who
        // chooses one creature blinks one, and a spec left unchosen is not a fizzle (CR 115.1).
        for (var copy = 0; copy < howMany; copy++)
        {
            targets.Add(howMany > 1 ? blinked with { Optional = true } : blinked);
            effects.Add(new FlickerTarget(targets.Count - 1, m.Groups["tapped"].Success));
        }

        return true;
    }

    /// <summary>Where a search puts what it found (CR 701.23).</summary>
    /// <remarks>
    /// The hand is the default because a search that says nothing about a destination reveals the
    /// card and takes it — which is what every tutor without a "put" clause does.
    /// </remarks>
    private static Zone SearchDestination(string where) =>
        where.Contains("battlefield", StringComparison.OrdinalIgnoreCase) ? Zone.Battlefield
        : where.Contains("graveyard", StringComparison.OrdinalIgnoreCase) ? Zone.Graveyard
        : Zone.Hand;

    /// <summary>
    /// "An Elf, Warrior, or Tyvar card", "a noncreature, nonland card" — several filters joined.
    /// </summary>
    /// <remarks>
    /// The two lists join differently and the difference is the whole point. "A creature or land
    /// card" is a card that answers to *either*; "a noncreature, nonland card" is one that
    /// answers to *both*, because each half rules something out. Collapsing either to one filter
    /// would offer the player cards the printing never named.
    /// <para>
    /// A mixed list — one negation and one not — is left unread. English joins those with a
    /// meaning that depends on the sentence, and guessing which would be guessing at the card.
    /// </para>
    /// </remarks>
    private static string? JoinedFilter(string phrase)
    {
        var parts = phrase
            .Replace(", or ", ",", StringComparison.OrdinalIgnoreCase)
            .Replace(" or ", ",", StringComparison.OrdinalIgnoreCase)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0)
            return null;

        var negations = parts.Count(p => p.StartsWith("non", StringComparison.OrdinalIgnoreCase));
        if (negations != 0 && negations != parts.Length)
            return null;

        // Each part goes through the one reader that knows what a filter may say. Joining them
        // unvalidated is how "a Mount creature card or a Plains card" became the two filters
        // "Mount creature card" and "a Plains", neither of which any card answers to - and the
        // card compiled, looked complete, and dug through five cards finding nothing.
        var read = new List<string>();
        foreach (var part in parts)
        {
            var trimmed = ArticleAndCard().Replace(part, string.Empty).Trim();
            if (SearchFilterNamed(trimmed) is not { } one)
                return null;

            read.Add(one);
        }

        return string.Join(negations == 0 ? '|' : '&', read);
    }

    /// <summary>
    /// An amount multiplied by how many permanents answer a group phrase, or null if unread.
    /// </summary>
    /// <remarks>
    /// The counting goes through the same group grammar the sweepers use, so "for each creature
    /// you control" and "for each Goblin an opponent controls" both work and neither needed a
    /// word of its own here. A phrase the grammar cannot read returns null and leaves the whole
    /// sentence unread — an amount that silently came out as one would be a card that does far
    /// less than it says, and nothing downstream would notice.
    /// </remarks>
    /// <summary>The five basic land types domain counts (CR 305.6).</summary>
    private static readonly string[] BasicLandTypes =
        ["Plains", "Island", "Swamp", "Mountain", "Forest"];

    /// <summary>
    /// How many the count is worth per thing: "twice the number of X" is "2 for each X".
    /// </summary>
    /// <remarks>
    /// A multiplier is not a different mechanism from "N for each X" - it is the same amount with
    /// the fixed part spelled as a word - so it is folded into the per-thing number rather than
    /// given a wrapper of its own, and every verb that can carry a count gets it for free.
    /// </remarks>
    /// <summary>
    /// The card types one noun in a zone-counting phrase names, or null if it names none.
    /// </summary>
    /// <remarks>
    /// Two tables rather than one, in this order: the permanent table answers the compounds
    /// ("artifact creature") and the bare word "permanent", and the graveyard table answers the
    /// types a permanent can never have. A graveyard holds both, so a count over one has to ask
    /// both (CR 205.2a).
    /// </remarks>
    private static IReadOnlyList<Domain.Enums.CardType>? TypesOfCardNoun(string noun) =>
        Specs.PermanentTypes(noun)
            ?? (Specs.CardTypeInGraveyard(noun) is { } single ? [single] : null);

    private static int Times(Match m) => m.Groups["mult"].Value.Trim().ToLowerInvariant() switch
    {
        "twice" => 2,
        "three times" => 3,
        _ => 1,
    };

    /// <summary>
    /// The card types an animation confers (CR 205.1b).
    /// </summary>
    /// <remarks>
    /// "Becomes a 3/3 Elemental creature" and "becomes a 3/3 Soldier artifact creature" differ by
    /// one word, and the word is not decoration: an animated permanent that is also an artifact
    /// answers to artifact removal, and one that quietly was not would be a better card than the
    /// printed one.
    /// <para>
    /// This used to say the subtype beside it was ignored because the engine had no use for
    /// "Elemental" and could not check it. That was wrong on both counts by the time it was
    /// written: <c>CharacteristicsBuilder.Subtypes</c> exists and every lord reads it, so
    /// "Elemental creatures you control get +1/+1" is a question a Keyrune can be asked. The
    /// colour was dropped without even that argument. Azorius Keyrune printed "a 2/2 white and
    /// blue Bird artifact creature" and became a colourless, typeless 2/2 - which is a different
    /// card in front of protection from blue, in front of removal that names a Bird, and in front
    /// of any lord. 264 corpus cards print that shape, 202 naming a type and 72 a colour.
    /// </para>
    /// </remarks>
    /// <summary>The creature types an animation confers (CR 205.3m, layer 4).</summary>
    /// <remarks>
    /// Capitalised words in the modifier run, which is how the printed line spells a subtype and
    /// how every other reader here tells one from an adjective. "Artifact" is excluded because it
    /// is a card type and is answered by <see cref="AnimatedTypes"/>.
    /// </remarks>
    private static IEnumerable<string> AnimatedSubtypes(Match m) =>
        m.Groups["mods"].Value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length > 1 && char.IsUpper(word[0]));

    /// <summary>The colours an animation confers (CR 105.2, layer 5).</summary>
    /// <remarks>
    /// Printed lower-case and joined by "and", so the words are taken individually and anything
    /// that is not a colour name is left alone - the same run carries "artifact" and the subtype.
    /// </remarks>
    private static IEnumerable<ManaColor> AnimatedColors(Match m) =>
        m.Groups["mods"].Value
            .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(ColourNamed)
            .Where(colour => colour is not null)
            .Select(colour => colour!.Value);

    /// <summary>One of the five colour words, or null for anything else (CR 105.1).</summary>
    private static ManaColor? ColourNamed(string word) => word.ToLowerInvariant() switch
    {
        "white" => ManaColor.White,
        "blue" => ManaColor.Blue,
        "black" => ManaColor.Black,
        "red" => ManaColor.Red,
        "green" => ManaColor.Green,
        _ => null,
    };

    /// <summary>The card type words an animation's modifier run may name (CR 205.2a).</summary>
    private static readonly Dictionary<string, Domain.Enums.CardType> AnimationCardTypeWords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["artifact"] = Domain.Enums.CardType.Artifact,
            ["creature"] = Domain.Enums.CardType.Creature,
            ["enchantment"] = Domain.Enums.CardType.Enchantment,
            ["land"] = Domain.Enums.CardType.Land,
            ["planeswalker"] = Domain.Enums.CardType.Planeswalker,
        };

    /// <summary>The card types an animation's modifier run confers, if any (CR 205.1b).</summary>
    /// <remarks>
    /// None is a real answer and not a failure: "becomes a Dragon with base power and toughness
    /// 4/4" is aimed at a creature and changes its creature type and its size without touching
    /// its card types. Handing out the creature type there would make a permanent a creature on
    /// the strength of a word the card did not print.
    /// </remarks>
    private static Domain.Enums.CardType AnimatedCardTypes(Match m)
    {
        var types = Domain.Enums.CardType.None;

        foreach (var word in m.Groups["mods"].Value.Split(
            [' ', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            if (AnimationCardTypeWords.TryGetValue(word, out var one))
                types |= one;
        }

        return types;
    }

    /// <summary>
    /// Whether every word of a modifier run is one this reader can act on.
    /// </summary>
    /// <remarks>
    /// The guard the older animation reader does not have, and the reason the new shapes get it:
    /// a run is read by picking out the words that are understood, so a word that is <em>not</em>
    /// — "colorless", "basic", "nonlegendary" — is silently dropped and the card compiles as
    /// complete while doing something else. "Becomes a colorless artifact in addition to its
    /// other types" would have kept every colour it had. Refusing the whole sentence leaves it in
    /// the work queue, where it can be seen.
    /// </remarks>
    private static bool ModifiersUnderstood(string mods) =>
        mods.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
            .All(word =>
                char.IsUpper(word[0])
                || AnimationCardTypeWords.ContainsKey(word)
                || ColourNamed(word) is not null);

    /// <summary>
    /// The generated effects an animation sentence comes to, or null if it names something this
    /// cannot confer.
    /// </summary>
    /// <param name="sets">
    /// The base power and toughness the sentence prints, if it prints one. Layer 7b and not 7c:
    /// CR 613.4b puts an effect that speaks of a permanent's <em>base</em> size in the setting
    /// sublayer, so a counter added afterwards stacks on top of it.
    /// </param>
    /// <remarks>
    /// One list of ids for one sentence, because an animation is several continuous effects and
    /// each of them lives in its own layer: the type in 4, the colour in 5, the granted keyword in
    /// 6, the size in 7b (CR 613.1). They are built here so that every shape of animation sentence
    /// produces the same run of effects and none of them can quietly leave one out.
    /// </remarks>
    private static List<string>? AnimationEffects(Match m, (int Power, int Toughness)? sets)
    {
        if (!ModifiersUnderstood(m.Groups["mods"].Value))
            return null;

        var granted = m.Groups["kw"].Success ? Keywords(m.Groups["kw"].Value) : null;
        if (m.Groups["kw"].Success && granted is null)
            return null;

        var ids = new List<string>();

        if (AnimatedCardTypes(m) is var types && types != Domain.Enums.CardType.None)
            ids.Add(GenerativeEffects.BecomesId(types));

        foreach (var subtype in AnimatedSubtypes(m))
            ids.Add(AnimatedSubtypeId(m, subtype));

        // A colour is set rather than added even here: "in addition to its other types" is about
        // types, and a card that meant its colours too says "colors and types" — which this does
        // not read, so those sentences stay in the queue rather than losing a colour in silence.
        if (AnimatedColors(m).ToList() is { Count: > 0 } colours)
            ids.Add(GenerativeEffects.BecomesColorsId(colours));

        if (sets is { } size)
            ids.Add(GenerativeEffects.SetPowerToughnessId(size.Power, size.Toughness));

        if (granted is { } keywords)
            ids.Add(GenerativeEffects.GrantId(keywords));

        return ids.Count > 0 ? ids : null;
    }

    /// <summary>
    /// Aims a run of generated effects at whatever the sentence animates — the source, or a
    /// target phrase.
    /// </summary>
    /// <remarks>
    /// The split every reader in this family needs and each of them used to write out twice: a
    /// permanent that animates itself names no target, and the target grammar begins at a target
    /// phrase. Nothing is added to either builder until the phrase has been read, so a sentence
    /// this refuses falls through to the readers below with no half-built effect left behind.
    /// </remarks>
    private static bool Animates(
        Match m,
        ImmutableList<TargetSpec>.Builder targets,
        ImmutableList<IEffect>.Builder effects,
        IReadOnlyList<string> definitionIds)
    {
        if (m.Groups["self"].Value == "~")
        {
            foreach (var id in definitionIds)
                effects.Add(new PumpSourceUntilEndOfTurn(id));

            return true;
        }

        if (Specs.Parse(m.Groups["t"].Value) is not { } animated)
            return false;

        targets.Add(animated);
        var index = targets.Count - 1;

        foreach (var id in definitionIds)
            effects.Add(new PumpUntilEndOfTurn(id, index));

        return true;
    }

    /// <summary>
    /// "… loses all abilities …", lifted out so the rest of the sentence keeps its readers
    /// (CR 613.1f).
    /// </summary>
    /// <remarks>
    /// The corpus prints the clause in front of half a dozen different tails — a base size, an
    /// animation, a colour, a keyword — and every one of those tails is a wording the readers
    /// above already know. So the removal is lifted out and the sentence is offered back with the
    /// clause deleted: "Until end of turn, target creature loses all abilities and has base power
    /// and toughness 0/1" is read as the removal plus "Until end of turn, target creature has
    /// base power and toughness 0/1", which reads today. Pairing a reader with each tail would
    /// have been six copies of one rule, and the seventh wording would still have been unread.
    /// <para>
    /// The target is added by the tail's own reader and then checked to be the phrase the head
    /// named. Adding it here as well would make the spell ask its controller to choose two
    /// creatures for a sentence that names one.
    /// </para>
    /// <para>
    /// A duration is required, in any of the three places a card prints it. The effect this
    /// builds ends in the cleanup step either way, so without the words a card that silences a
    /// permanent <em>for good</em> would be read as a trick that undoes itself, and "perpetually"
    /// and "until your next turn" would each be read as shorter than printed. Those stay in the
    /// work queue, which is the same refusal <see cref="AnimationLastsTheTurn"/> makes and for
    /// the same reason.
    /// </para>
    /// </remarks>
    private static bool TrySilencing(
        Match m,
        ImmutableList<TargetSpec>.Builder targets,
        ImmutableList<IEffect>.Builder effects,
        bool objectNamedByTrigger)
    {
        const string Prefix = "Until end of turn, ";

        var head = m.Groups["head"].Value.Trim();
        var rest = m.Groups["rest"].Success ? m.Groups["rest"].Value.Trim() : null;

        var lastsTheTurn = m.Groups["ueot"].Success
            || head.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            || rest?.EndsWith(" until end of turn", StringComparison.OrdinalIgnoreCase) == true;

        if (!lastsTheTurn)
            return false;

        var who = head.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            ? head[Prefix.Length..].Trim()
            : head;

        var onSource = who == "~";
        var pronoun = Pronouns.Contains(who, StringComparer.OrdinalIgnoreCase);
        var named = onSource || pronoun ? null : Specs.Parse(who);

        if (!onSource && !pronoun && named is null)
            return false;

        // Where the removal lands is decided from the head, never from whatever the tail happened
        // to add: a sentence whose head is "~" and whose tail targets something else would
        // otherwise silence the wrong permanent.
        var before = targets.Count;

        if (rest is not null)
        {
            var scratch = ImmutableList.CreateBuilder<IEffect>();

            // Compared by description rather than by the specs themselves: a spec carries its
            // filters as delegates, so two readings of one phrase are never equal as records
            // even when they are the same phrase. The description is what the phrase said.
            if (!TryOne(head + " " + rest, targets, scratch, objectNamedByTrigger)
                || (named is null
                    ? targets.Count != before
                    : targets.Count != before + 1
                        || !string.Equals(
                            targets[before].Description, named.Description, StringComparison.Ordinal)))
            {
                // The tail read a different target phrase from the head's, or none at all, so
                // which permanent loses its abilities would be a guess. Whatever it added comes
                // back off, because a refused sentence has to fall through to the readers below
                // with no half-built spell left behind.
                while (targets.Count > before)
                    targets.RemoveAt(targets.Count - 1);

                return false;
            }

            effects.AddRange(scratch);
        }
        else if (named is not null)
        {
            targets.Add(named);
        }

        var id = GenerativeEffects.LosesAllAbilitiesId();

        if (onSource)
        {
            effects.Add(new PumpSourceUntilEndOfTurn(id));
            return true;
        }

        if (!pronoun)
        {
            effects.Add(new PumpUntilEndOfTurn(id, before));
            return true;
        }

        // "Tap target creature. It loses all abilities until end of turn." The pronoun means the
        // target the sentence before named, and on a card that has targeted nothing it means
        // something else entirely — so it is read only once something has been.
        if (before == 0)
            return false;

        effects.Add(new PumpUntilEndOfTurn(id, before - 1));
        return true;
    }

    private static Domain.Enums.CardType AnimatedTypes(Match m) =>
        m.Groups["mods"].Value.Contains("artifact", StringComparison.OrdinalIgnoreCase)
            ? Domain.Enums.CardType.Artifact | Domain.Enums.CardType.Creature
            : Domain.Enums.CardType.Creature;

    /// <summary>
    /// Whether an animation says how long it lasts, in either of the two places it can (CR 514.2).
    /// </summary>
    /// <remarks>
    /// The duration is required rather than assumed, and that is a refusal rather than a
    /// convenience: the effect this reader builds is created with the turn number on it and ends
    /// in the cleanup step, so a card that animates a permanent <em>for good</em> — "Target
    /// Mountain becomes a 3/1 creature" — would have been read as a combat trick that undoes
    /// itself. Seven corpus cards were compiling that way, complete and wrong, and requiring the
    /// words puts them back in the work queue. That is the failure this file exists to avoid:
    /// nothing downstream can see it, because the card is legal and playable and only the second
    /// turn tells.
    /// </remarks>
    private static bool AnimationLastsTheTurn(Match m) =>
        m.Groups["pre"].Success || m.Groups["ueot"].Success;

    /// <summary>The same question of a base power and toughness, which prints it twice.</summary>
    /// <remarks>
    /// Two trailing positions rather than one, because the keyword clause can sit between them:
    /// "has base power and toughness 4/2 until end of turn and gains first strike until end of
    /// turn" is one sentence with the duration on both halves.
    /// </remarks>
    private static bool SettingLastsTheTurn(Match m) =>
        m.Groups["pre"].Success || m.Groups["u1"].Success || m.Groups["u2"].Success;

    /// <summary>
    /// The layer 4 effect an animation's printed subtype makes — replacing, or adding (CR 205.1).
    /// </summary>
    /// <remarks>
    /// Two rules, one word apart. CR 205.1a is the ordinary case: a new subtype replaces the ones
    /// the permanent had from the same set. CR 205.1b is the exception the card asks for in so
    /// many words — "in addition to its other types" keeps every prior type, so a Vampire that
    /// becomes a Demon is both. Reading the second as the first would silently take away the type
    /// the rest of the board is counting, on a card that says in print that it does not.
    /// </remarks>
    private static string AnimatedSubtypeId(Match m, string subtype) =>
        m.Groups["add"].Success
            ? GenerativeEffects.GainsCreatureTypeId(subtype)
            : GenerativeEffects.BecomesCreatureTypeId(subtype);

    /// <summary>
    /// Which set of subtypes a printed subtype belongs to (CR 205.1a).
    /// </summary>
    /// <remarks>
    /// Exposed for <see cref="GenerativeEffects"/>, which needs it to answer the only question
    /// 205.1a asks of a replacing type change: which of the subtypes already there this one
    /// displaces. The classification itself is the target grammar's, so there is one table.
    /// </remarks>
    internal static Domain.Enums.CardType SubtypeSetOf(string subtype) =>
        Specs.SubtypeCardType(subtype);

    /// <summary>
    /// How many a counted phrase comes to, whatever it is counting (CR 107.3).
    /// </summary>
    /// <param name="you">Who the phrase means by "you" - the controller of what is counting.</param>
    /// <param name="source">
    /// The permanent a phrase saying "it" points at. Only meaningful when the caller had one to
    /// give; see the <c>hasSource</c> argument of <see cref="Counting"/>.
    /// </param>
    internal delegate int CountFn(
        GameState state, IAbilitySource abilities, Guid you, ObjectId source);

    private static Amount? CountingAmount(Amount each, string groupPhrase) =>
        Counting(groupPhrase, hasSource: true) is not { } count
            ? null
            : each with
            {
                Counter = context => count(
                    context.State,
                    context.Abilities,
                    context.ControllerId,
                    context.PhysicalSourceId),
            };

    /// <summary>
    /// Reads a counted group phrase, or null when it names something this cannot count.
    /// </summary>
    /// <param name="hasSource">
    /// Whether the caller can say which permanent the phrase means by "it". A resolution always
    /// can; a generated continuous effect cannot, because a floating effect made by a spell is
    /// handed a <c>null</c> source when characteristics are computed. The phrases that read the
    /// source are refused outright when it is false rather than answered with zero - a count
    /// that quietly comes out as nought is the failure this whole vocabulary exists to avoid,
    /// and it compiles as a complete card while doing nothing.
    /// </param>
    internal static CountFn? Counting(string groupPhrase, bool hasSource)
    {
        ArgumentNullException.ThrowIfNull(groupPhrase);

        var phrase = groupPhrase.Trim();

        // "For each creature on the battlefield" is "for each creature". The count at the bottom
        // of this method walks the battlefield and nothing else, so those three words say where
        // to look rather than narrowing what to look for - and the group grammar, which has no
        // word for a zone, refused all seventy corpus cards that spell it out. Trimmed here and
        // not in that grammar, because a *target* phrase saying "on the battlefield" is not
        // redundant in the same way: only a count is already confined to one zone.
        phrase = OnTheBattlefieldTail().Replace(phrase, string.Empty).Trim();

        // "Artifact and/or enchantment you control" is one group of two kinds, and the slash is
        // the whole of the difference from "artifact or enchantment", which the group grammar
        // reads as alternatives already. Spelled back into the wording it knows rather than
        // taught a second punctuation for the same idea.
        phrase = AndOrJoin().Replace(phrase, " or ");

        // Callers disagree about whether "each" belongs to the phrase: some patterns capture
        // "for each X" and hand over "each X", others strip the words and hand over "X". Both
        // arrive here, so both are answered here - a caller that had to know which spelling this
        // wanted would be a caller that sometimes gets it wrong, and one of them already did.
        var people = phrase.StartsWith("each ", StringComparison.OrdinalIgnoreCase)
            ? phrase[5..].Trim()
            : phrase;

        // "The number of opponents you have" is "opponents" with the possessive spelled out, and
        // twenty-two cards prefer that spelling. Nothing about it narrows the group - you have
        // exactly the opponents you have - so it is trimmed off here rather than given a branch
        // of its own, and every verb that can count reaches the same answer for both wordings.
        people = OwnedPlayersTail().Replace(people, string.Empty).Trim();

        // "For each opponent" counts players, not permanents, and the group grammar beside this
        // only knows how to walk the battlefield. It is the same sentence shape with a different
        // population, and a hundred and fifty lines are written that way - so the player case is
        // answered here rather than by teaching the permanent grammar about people.
        //
        // Opponents are those still in the game (CR 102.1): somebody who has lost is no longer an
        // opponent, and a count that included them would keep paying for a player who left.
        if (people.Equals("opponent", StringComparison.OrdinalIgnoreCase)
            || people.Equals("opponents", StringComparison.OrdinalIgnoreCase))
        {
            return (state, _, you, _) => state.TurnOrder.Count(
                id => id != you && !state.GetPlayer(id).HasLost);
        }

        if (people.Equals("player", StringComparison.OrdinalIgnoreCase)
            || people.Equals("players", StringComparison.OrdinalIgnoreCase))
        {
            return (state, _, _, _) => state.TurnOrder.Count(
                id => !state.GetPlayer(id).HasLost);
        }

        // "For each time it was kicked" - not a count of the board at all, but a fact the cast
        // recorded on the spell and CR 607.2 carried onto the permanent. Read off the source
        // because that is where the number lives; a caller with no source to give cannot ask.
        if (TimesKickedLine().IsMatch(people))
        {
            if (!hasSource)
                return null;

            return (state, _, _, source) =>
                state.TryGetObject(source, out var kickedSpell) ? kickedSpell.TimesKicked : 0;
        }

        // "For each experience counter you have" - a counter on the player rather than on a
        // permanent (CR 122.1), which is why it is answered here and not by the counter phrase
        // below: that one looks for "counters on" something, and this one has nothing to be on.
        // The "you have" has already come off with the possessive tail above.
        if (ExperienceCountersLine().IsMatch(people))
            return (state, _, you, _) => state.GetPlayer(you).ExperienceCounters;

        // "For each card you've drawn this turn" (CR 121.1). The player already keeps the tally,
        // because a draw is an event and the fold counts them; nothing here has to remember which
        // cards they were, and a card that has since been discarded still counts.
        if (CardsDrawnThisTurnLine().IsMatch(people))
            return (state, _, you, _) => state.GetPlayer(you).CardsDrawnThisTurn;

        // "For each creature that died this turn" (CR 700.4) - battlefield to graveyard and
        // nothing else, so a creature exiled or bounced this turn is not counted. Game-wide and
        // not "yours": no corpus card asking this names a player, and narrowing it to one would
        // answer a smaller number than the card says.
        var died = CreaturesDiedThisTurnLine().Match(people);
        if (died.Success)
        {
            // "Nontoken" is printed on its own cards, and a token dying would otherwise inflate
            // every one of them.
            var nontokenOnly = died.Groups["nontoken"].Success;

            return (state, _, _, _) => state.CreaturesDiedThisTurn(nontokenOnly);
        }

        // "For each creature in your party" (CR 700.8) - not a count of creatures at all: a party
        // is at most one Cleric, one Rogue, one Warrior and one Wizard, so eight Clerics are a
        // party of one. Answered here rather than through the noun grammar, which counts what it
        // matches and would say eight.
        if (PartyCountLine().IsMatch(people))
            return PartySize;

        // "For each creature attacking you" - attacking the player, and not a planeswalker they
        // control. Only a player, a planeswalker or a battle can be attacked (CR 506.3), and they
        // are three different things to attack: a creature aimed at your planeswalker is not
        // attacking you, even though the defending player is you either way.
        if (CreaturesAttackingYouLine().IsMatch(people))
        {
            return (state, _, you, _) => state.Battlefield.Count(
                id => state.Combat.Attackers.TryGetValue(id, out var at)
                    && at.DefendingPlayer == you
                    && !at.IsPlaneswalker);
        }

        // "For each +1/+1 counter on ~", "for each charge counter on it" - counting what is on
        // one permanent rather than how many permanents there are, which is a different question
        // asked in the same words. Four spellings across 26 corpus lines, and they differ only in
        // which pronoun names the permanent - both of which mean the source here, because a
        // counting phrase has no target of its own to point at.
        var counters = CountersOnLine().Match(people);
        if (counters.Success)
        {
            if (!hasSource)
                return null;

            var kind = counters.Groups["kind"].Value.Trim();

            return (state, _, _, source) =>
            {
                if (!state.TryGetObject(source, out var bearing)
                    || bearing.Permanent is not { } onIt)
                {
                    return 0;
                }

                // "The number of counters on it" names no kind and means all of them
                // (CR 122.1) - one number over every kind the permanent has, which is why it
                // sums rather than looking one up. A card asking this is usually a proliferate
                // or a charge payoff, where the counters really are of several kinds.
                if (kind.Length == 0)
                    return onIt.Counters.Values.Sum();

                return onIt.Counters.TryGetValue(kind, out var many) ? many : 0;
            };
        }

        // "For each Aura attached to it", "for each Aura and Equipment attached to it" - counting
        // what is attached to the source rather than counting the board. The group grammar cannot
        // ask this and should not learn to: attachment is a fact about a *pair* of permanents,
        // and every filter it builds is a question about one.
        //
        // "It" is the source for the same reason it is on the counter phrase above - a counting
        // phrase has no target of its own to point at - and every corpus card saying this is an
        // Aura or Equipment payoff reading its own attachments.
        var attached = AttachedToSourceLine().Match(people);
        if (attached.Success)
        {
            if (!hasSource)
                return null;

            var kinds = AndOrSplit()
                .Split(attached.Groups["kinds"].Value)
                .Select(word => SingularWord(word.Trim()))
                .Where(word => word.Length > 0)
                .ToArray();

            return (state, abilities, _, source) => state.Battlefield.Count(id =>
            {
                var onIt = state.GetObject(id);

                if (onIt.Permanent?.AttachedTo != source)
                    return false;

                // Computed subtypes, not printed: an Equipment that has been made an Aura, or
                // a permanent given a subtype by a layer, is what it is now (CR 613.1d).
                var now = Characteristics.Of(state, abilities, onIt);

                return Array.Exists(
                    kinds, kind => now.Subtypes.Contains(kind, StringComparer.OrdinalIgnoreCase));
            });
        }

        // Domain. It counts *types*, not lands: the basic land types are the five at CR 305.6,
        // so five Forests are one and a single Stomping Ground is two. That is why it cannot be
        // written as a permanent group like everything else here - the group grammar counts
        // permanents, and this counts something about them.
        //
        // No rule of its own to cite: domain is an ability word, and those have no rules meaning
        // (CR 207.2c). What the phrase means is CR 305.6 and nothing else.
        //
        // Read off the computed characteristics rather than the printed card, because a land that
        // has been given a basic land type counts for it (CR 305.7), which is exactly what the
        // dual-land cycles these appear beside are for.
        if (DomainPhrase().IsMatch(people))
        {
            return (state, abilities, you, _) => BasicLandTypes.Count(
                type => state.Battlefield.Any(id =>
                {
                    var land = state.GetObject(id);
                    var now = Characteristics.Of(state, abilities, land);

                    return now.ControllerId == you
                        && now.Subtypes.Contains(type, StringComparer.OrdinalIgnoreCase);
                }));
        }

        // "The number of colors among permanents you control" - like domain above, this counts
        // something *about* the permanents rather than the permanents themselves, so the group
        // grammar cannot express it. Colour is read from the computed characteristics, because a
        // permanent that has been made another colour counts as that colour (CR 105.2, 613.1e).
        //
        // The noun is singular as often as it is plural - "for each color among permanents you
        // control" against "where X is the number of colors among permanents you control" - and
        // the two say the same thing, so the pattern takes the s or leaves it.
        if (ColorsAmongLine().Match(people) is { Success: true } hues
            && Specs.ParseGroup("each " + hues.Groups["group"].Value.Trim()) is
            { Kind: TargetKind.Permanent } among)
        {
            return (state, abilities, you, _) =>
            {
                var seen = new HashSet<ManaColor>();

                foreach (var id in state.Battlefield)
                {
                    var permanent = state.GetObject(id);

                    if (among.ObjectFilter?.Invoke(state, abilities, permanent, you) == false)
                        continue;

                    foreach (var colour in Characteristics
                        .Of(state, abilities, permanent).Colors)
                    {
                        seen.Add(colour);
                    }
                }

                return seen.Count;
            };
        }

        // "The number of differently named lands you control" - how many *names* there are, not
        // how many permanents, which is the same shape as domain and colours above. The tail is
        // an ordinary group phrase, so the words in front are lifted off and everything the group
        // grammar reads works behind them.
        if (DifferentlyNamedLine().Match(people) is { Success: true } distinct
            && Specs.ParseGroup("each " + distinct.Groups["group"].Value.Trim()) is
            { Kind: TargetKind.Permanent } byName)
        {
            return (state, abilities, you, _) =>
            {
                var names = new HashSet<string>(StringComparer.Ordinal);

                foreach (var id in state.Battlefield)
                {
                    var permanent = state.GetObject(id);

                    // A face-down permanent has no name at all (CR 708.2), so it is not one more
                    // name - and reading the card underneath would count a name no player can
                    // see. Two face-down permanents are not "differently named"; they are two
                    // things with no name.
                    if (permanent.Permanent?.IsFaceDown == true)
                        continue;

                    if (byName.ObjectFilter?.Invoke(state, abilities, permanent, you) == false)
                        continue;

                    names.Add(permanent.Card.Name);
                }

                return names.Count;
            };
        }

        // "The number of creature cards in your graveyard", "the number of cards in your hand" -
        // counting a *zone* rather than the battlefield, which is the same question about a
        // different pile and had no answer at all. The battlefield count below walks
        // state.Battlefield; these walk the hand or the graveyard, and the noun in front is the
        // same type word the target grammar reads everywhere else.
        var pile = CardsInZoneLine().Match(people);
        if (pile.Success)
        {
            var noun = pile.Groups["noun"].Value.Trim();

            // "Instant and sorcery cards" means either one, not both - no card is both types, so
            // the conjunctive reading counts nothing at all. Juxtaposition is the opposite:
            // "artifact creature cards" does mean both. The printed word "and" is what separates
            // the two, which is why the split is on that word rather than on the type list.
            var alternatives = AndSplit()
                .Split(noun)
                .Select(part => part.Trim())
                .Where(part => part.Length > 0)
                .Select(TypesOfCardNoun)
                .ToList();

            List<Domain.Enums.CardType[]> types;

            if (noun.Length == 0)
            {
                types = [[]];
            }
            else if (alternatives.Count == 0 || alternatives.Exists(set => set is null))
            {
                // A noun the type table does not know leaves the phrase unread rather than
                // counting everything: "the number of Zombie cards" is not "the number of cards".
                return null;
            }
            else
            {
                types = [.. alternatives.Select(set => set!.ToArray())];
            }

            var mine = pile.Groups["whose"].Value.StartsWith(
                "your", StringComparison.OrdinalIgnoreCase);

            var named = pile.Groups["zone"].Value.ToLowerInvariant();

            return (state, _, you, _) => state.TurnOrder
                .Where(who => !mine || who == you)
                .Sum(who =>
                {
                    var player = state.GetPlayer(who);

                    var zone = named switch
                    {
                        "hand" => player.Hand,
                        "library" => player.Library,
                        _ => player.Graveyard,
                    };

                    return zone.Count(id =>
                        state.TryGetObject(id, out var card)
                        && types.Exists(set => set.All(
                            type => card.Card.CardTypes.HasFlag(type))));
                });
        }

        if (Specs.ParseGroup(phrase) is not { Kind: TargetKind.Permanent } counted)
            return null;

        return (state, abilities, you, _) => state.Battlefield.Count(
            id => counted.ObjectFilter?.Invoke(
                state, abilities, state.GetObject(id), you) != false);
    }

    /// <summary>The four creature types a party is made of (CR 700.8).</summary>
    private static readonly string[] PartyRoles = ["Cleric", "Rogue", "Warrior", "Wizard"];

    /// <summary>
    /// How many creatures are in a player's party (CR 700.8a).
    /// </summary>
    /// <remarks>
    /// CR 700.8b is the whole of the difficulty: a creature that could fill two of the roles
    /// fills only one, and the number is taken the way that produces the highest result. So this
    /// is a matching and not a tally, and a greedy assignment is wrong exactly where it matters -
    /// a Cleric Rogue beside a plain Rogue is half a party, and a reader that spent the Cleric
    /// Rogue on the Rogue slot would report one.
    /// <para>
    /// Types are computed rather than printed (CR 613.1d, layer 4), because the cards that make a
    /// creature "a Cleric in addition to its other types" are printed alongside the ones asking
    /// this question.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Exposed so the one other reader that counts a party asks this and does not keep a second
    /// answer to the same rule.
    /// </summary>
    internal static int PartySizeFor(GameState state, IAbilitySource abilities, Guid you) =>
        PartySize(state, abilities, you, default);

    private static int PartySize(GameState state, IAbilitySource abilities, Guid you, ObjectId _)
    {
        var candidates = state.Battlefield
            .Select(state.GetObject)
            .Select(obj => Characteristics.Of(state, abilities, obj))
            .Where(now => now.IsCreature && now.ControllerId == you)
            .ToList();

        // Which creature each role has been given, as the search reassigns them.
        var filled = new int[PartyRoles.Length];
        Array.Fill(filled, -1);

        bool Fill(int role, bool[] tried)
        {
            for (var i = 0; i < candidates.Count; i++)
            {
                if (tried[i]
                    || !candidates[i].Subtypes.Contains(
                        PartyRoles[role], StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                tried[i] = true;

                // Free, or the role holding it can be re-housed somewhere else - which is the
                // step that makes this a maximum rather than a first-come assignment.
                var heldBy = Array.IndexOf(filled, i);
                if (heldBy < 0 || Fill(heldBy, tried))
                {
                    filled[role] = i;
                    return true;
                }
            }

            return false;
        }

        var size = 0;
        for (var role = 0; role < PartyRoles.Length; role++)
        {
            if (Fill(role, new bool[candidates.Count]))
                size++;
        }

        return size;
    }

    /// <summary>
    /// "That many" — however much the triggering event was about (CR 603.2).
    /// </summary>
    /// <remarks>
    /// Read off the trigger rather than looked up again on resolution, because by then the event
    /// is over: the damage has been marked and netted against other damage, the life total has
    /// moved on. How much it was is a fact about the event and travels with it.
    /// </remarks>
    [GeneratedRegex(
        @"^basic land types? among lands you control$", RegexOptions.IgnoreCase)]
    private static partial Regex DomainPhrase();

    /// <summary>"Colors among permanents you control" - how many colours, not how many things.</summary>
    /// <remarks>
    /// Singular or plural, because the two word orders disagree about it and mean the same
    /// thing: "for each color among permanents you control" is the same count as "the number
    /// of colors among permanents you control".
    /// </remarks>
    [GeneratedRegex(
        @"^colou?rs? among (?<group>[A-Za-z0-9'’ ]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ColorsAmongLine();

    /// <summary>"Creatures on the battlefield" - a zone a count is already confined to.</summary>
    [GeneratedRegex(@"\s+on the battlefield$", RegexOptions.IgnoreCase)]
    private static partial Regex OnTheBattlefieldTail();

    /// <summary>"Artifact and/or enchantment" - alternatives, written with a slash.</summary>
    [GeneratedRegex(@"\s+and/or\s+", RegexOptions.IgnoreCase)]
    private static partial Regex AndOrJoin();

    /// <summary>"Opponents you have" - the possessive spelled out, and no narrower for it.</summary>
    [GeneratedRegex(@"\s+(you|they) (have|has)$", RegexOptions.IgnoreCase)]
    private static partial Regex OwnedPlayersTail();

    /// <summary>"Aura and Equipment attached to it" - what is on the source (CR 301.5, 303.4).</summary>
    [GeneratedRegex(
        @"^(?<kinds>[A-Za-z]+(?: (?:and|or) [A-Za-z]+)*) attached to (it|~|this [A-Za-z]+)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex AttachedToSourceLine();

    /// <summary>The "and"/"or" joining two kinds of attachment into one count.</summary>
    [GeneratedRegex(@"\s+(?:and|or)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex AndOrSplit();

    /// <summary>The "and" that joins two card types into alternatives, not one type line.</summary>
    [GeneratedRegex(@"\s+and\s+", RegexOptions.IgnoreCase)]
    private static partial Regex AndSplit();

    /// <summary>"Creatures that died this turn" (CR 700.4).</summary>
    /// <remarks>
    /// The tense is spelled three ways across the corpus and means one thing, so all three are
    /// one pattern rather than three entries that could fall out of step.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<nontoken>nontoken )?creatures? that (?:died|have died|has died) this turn$",
        RegexOptions.IgnoreCase)]
    private static partial Regex CreaturesDiedThisTurnLine();

    /// <summary>"Creatures in your party" (CR 700.8).</summary>
    [GeneratedRegex(@"^creatures? in your party$", RegexOptions.IgnoreCase)]
    private static partial Regex PartyCountLine();

    /// <summary>"Experience counters" - a counter a player has, not one on a permanent (CR 122.1).</summary>
    [GeneratedRegex(@"^experience counters?$", RegexOptions.IgnoreCase)]
    private static partial Regex ExperienceCountersLine();

    /// <summary>"Time it was kicked" - the recorded kick count, not a board count (CR 702.33c).</summary>
    [GeneratedRegex(
        @"^times? (it|~|this spell|this creature) was kicked$", RegexOptions.IgnoreCase)]
    private static partial Regex TimesKickedLine();

    /// <summary>"Cards you've drawn this turn" (CR 121.1), with either apostrophe.</summary>
    [GeneratedRegex(@"^cards? you(?:'|\u2019)ve drawn this turn$", RegexOptions.IgnoreCase)]
    private static partial Regex CardsDrawnThisTurnLine();

    /// <summary>"Creatures attacking you" - the player, not a planeswalker they control (CR 506.3).</summary>
    [GeneratedRegex(
        @"^creatures? (?:that(?:'|\u2019)s |that are )?attacking you$", RegexOptions.IgnoreCase)]
    private static partial Regex CreaturesAttackingYouLine();

    /// <summary>"Differently named lands you control" - how many names (CR 201.1).</summary>
    [GeneratedRegex(@"^differently named (?<group>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex DifferentlyNamedLine();


    /// <summary>
    /// The power or toughness of whatever the trigger was about, read when the effect resolves.
    /// </summary>
    private static Amount StatOfTriggerSubject(string stat)
    {
        var wanted = stat.ToLowerInvariant();

        return new Amount(1)
        {
            Counter = context => StatOfObject(context, TriggerSubject(context), wanted),
        };
    }

    /// <summary>
    /// One permanent's power, toughness or mana value, as it is when the question is asked.
    /// </summary>
    /// <remarks>
    /// Shared by the trigger-subject amount above and the "where X is its power" clause, because
    /// they differ only in <em>which</em> permanent they measure. Mana value is a fact about the
    /// card and never about the permanent, so it is read off the printed cost rather than through
    /// the layers (CR 202.3b); power and toughness are the opposite, so a lord's bonus counts
    /// (CR 613.1).
    /// <para>
    /// Nothing to measure is zero rather than a refusal: by the time a death trigger resolves the
    /// permanent it was about has gone, and an amount cannot decline. The reader that built it is
    /// where a card is refused.
    /// </para>
    /// </remarks>
    private static int StatOfObject(ResolutionContext context, GameObject? subject, string wanted)
    {
        if (subject is null)
            return 0;

        if (wanted.StartsWith("mana", StringComparison.Ordinal))
            return Math.Max(0, subject.Card.Cmc);

        var now = State.Characteristics.Of(context.State, context.Abilities, subject);

        return Math.Max(
            0,
            (wanted.StartsWith("power", StringComparison.Ordinal) ? now.Power : now.Toughness) ?? 0);
    }

    /// <summary>The object the trigger was about, followed forward if it has gone (CR 608.2g).</summary>
    private static GameObject? TriggerSubject(ResolutionContext context) =>
        context.SubjectObject is not { } id
            ? null
            : context.State.TryGetObject(id, out var present)
                ? present
                : context.ObjectBehind?.Invoke(id);

    /// <summary>
    /// What "its" names when the sentence points at no target: the trigger's subject, or the
    /// source when there was no trigger (CR 700.7).
    /// </summary>
    /// <remarks>
    /// One answer for two readings that look unrelated on the card and are the same question
    /// here. An Aura printing "when enchanted creature dies, ... where X is its power" means the
    /// creature, which is the trigger's subject and is emphatically not the Aura; a creature
    /// printing "{2}{G}{G}: this creature gets +X/+X, where X is its power" has no trigger and
    /// means itself. Reading only the source would give the Aura a power of nothing and a card
    /// that compiles, resolves and does zero — the failure this whole reader is arranged around.
    /// </remarks>
    private static GameObject? SubjectOrSource(ResolutionContext context) =>
        TriggerSubject(context)
        ?? (context.State.TryGetObject(context.PhysicalSourceId, out var source) ? source : null);

    private static Amount ThatMany() => new(1)
    {
        Counter = context => Math.Max(0, context.SubjectAmount ?? 0),
    };

    /// <summary>
    /// An amount that is whatever the source's power is when it is asked (CR 107.3).
    /// </summary>
    /// <remarks>
    /// Computed rather than read off the card, because the card's printed power is not the
    /// question — a creature that has been pumped, or is a copy of something else, deals what it
    /// has now. Power is a characteristic and characteristics are computed (CR 613).
    /// </remarks>
    private static Amount SourcePower() => new(1)
    {
        Counter = context => context.State.TryGetObject(context.PhysicalSourceId, out var source)
            ? Math.Max(
                0,
                Characteristics.Of(context.State, context.Abilities, source).Power ?? 0)
            : 0,
    };

    /// <summary>
    /// The tail of a rewritten sentence, with its verb agreeing with one target rather than two.
    /// </summary>
    private static string Agreeing(string tail)
    {
        foreach (var (plural, singular) in new[]
        {
            (" get ", " gets "), (" gain ", " gains "), (" become ", " becomes "),
            (" have ", " has "), (" are ", " is "),
        })
        {
            if (!tail.StartsWith(plural, StringComparison.OrdinalIgnoreCase))
                continue;

            // "Get +2/+2 and gain first strike" has two verbs and both have to agree, or the
            // rewritten sentence reads "target creature gets +2/+2 and gain first strike" and
            // the combined matcher - which wants "and gains" - refuses the whole line.
            var rest = tail[plural.Length..];
            foreach (var (alsoPlural, alsoSingular) in new[]
            {
                (" and get ", " and gets "), (" and gain ", " and gains "),
                (" and become ", " and becomes "), (" and have ", " and has "),
            })
            {
                var at = rest.IndexOf(alsoPlural, StringComparison.OrdinalIgnoreCase);
                if (at < 0)
                    continue;

                rest = rest[..at] + alsoSingular + rest[(at + alsoPlural.Length)..];
                break;
            }

            return singular + rest;
        }

        // "To their owners' hands" is plural in three places at once, and the singular grammar
        // spells all three differently. It is the only phrase in the corpus that does this.
        return tail
            .Replace(
                " to their owners' hands",
                " to their owner's hand",
                StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A plural noun as the singular the target grammar wants.
    /// </summary>
    /// <remarks>
    /// Only the trailing s, because the nouns that appear here are ordinary — "creatures",
    /// "creature cards", "artifacts". A phrase whose head is irregular simply fails to parse
    /// afterwards, which loses the card rather than mis-reading it.
    /// </remarks>
    internal static string Singular(string phrase)
    {
        var words = phrase.Split(' ');

        for (var i = 0; i < words.Length; i++)
        {
            var one = SingularWord(words[i]);
            if (!string.Equals(one, words[i], StringComparison.Ordinal))
            {
                words[i] = one;
                break;
            }
        }

        return string.Join(' ', words);
    }

    /// <summary>
    /// One of a thing, given the word for several — "Elves" to "Elf" (CR 205.3m).
    /// </summary>
    /// <remarks>
    /// English is irregular enough that this is a list and not a rule, and getting it wrong is
    /// silent: a lord reading "other Elves you control get +1/+1" looked for the creature type
    /// "Elve", found none however many Elves were on the battlefield, and buffed nothing while
    /// compiling perfectly.
    /// <para>
    /// Shared by every reader that has to turn a printed plural into a type — the lord grammar,
    /// the board conditions that count a tribe, and the rewrite that turns "two target creatures"
    /// into a sentence about one. It was three copies, and the weakest of them stripped a
    /// trailing "s" and nothing else.
    /// </para>
    /// </remarks>
    internal static string SingularWord(string word)
    {
        if (word.Length < 3)
            return word;

        // Spelled the same either way, and the ones that break by every rule below. Aurochs
        // reached this list by being caught: it had only ever escaped folding by accident, because
        // the adjective in front of it stopped the search before the noun was reached, and fixing
        // that search turned "attacking Aurochs" into the type "Auroch" that no card has.
        if (string.Equals(word, "Plains", StringComparison.Ordinal)
            || string.Equals(word, "Aurochs", StringComparison.OrdinalIgnoreCase))
        {
            return word;
        }

        foreach (var (many, one) in Irregulars)
        {
            if (string.Equals(word, many, StringComparison.OrdinalIgnoreCase))
                return one;
        }

        // "Heroes" is "Hero" and "Horses" is "Horse": the "-oes" plural takes both letters back
        // and everything else takes one.
        if (word.EndsWith("oes", StringComparison.OrdinalIgnoreCase))
            return word[..^2];

        // Latin singulars that already end in "s". Stripping one gave "Locu" and "Pegasu", which
        // are creature types no card has - so a phrase naming them counted nothing, and Glimmerpost
        // gained life per "Locu" while compiling as a complete card.
        if (word.EndsWith("us", StringComparison.OrdinalIgnoreCase))
            return word;

        return word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal)
            ? word[..^1]
            : word;
    }

    /// <remarks>
    /// The "-ves" and "-ies" plurals are listed rather than folded by rule, and that is the whole
    /// point of this table. As rules they were wrong more often than right on this corpus: "-ves"
    /// turned <c>Caves</c> into "Caf" and <c>Detectives</c> into "Detectif", and "-ies" turned
    /// <c>Faeries</c> into "Faery" when the type is spelled Faerie. Every one of those is a
    /// creature type no card has, so the phrase naming it counted nothing while the card compiled
    /// as complete - the same silent failure as reading an unknown noun as a creature type.
    /// <para>
    /// The set of subtypes whose plural really does change the stem is small and closed, so it is
    /// written out. A type added to the game that belongs here will fold to the wrong stem until
    /// it is added - which shows up as a phrase nothing can satisfy, and there is an invariant
    /// watching for exactly that.
    /// </para>
    /// </remarks>
    private static readonly (string Many, string One)[] Irregulars =
    [
        ("Mice", "Mouse"),
        ("Geese", "Goose"),
        ("Children", "Child"),
        ("Teeth", "Tooth"),
        ("Feet", "Foot"),

        // "-ves": the stem loses the v.
        ("Elves", "Elf"),
        ("Wolves", "Wolf"),
        ("Werewolves", "Werewolf"),
        ("Dwarves", "Dwarf"),
        ("Thieves", "Thief"),

        // "-ies": the stem ends in y. Faeries and Zombies do not belong here - their singulars
        // end in e and the ordinary "-s" strip is right for them.
        ("Allies", "Ally"),
        ("Armies", "Army"),
    ];

    /// <summary>A printed count as an amount, which may be X (CR 601.2b).</summary>
    /// <summary>
    /// An amount scaled by a "for each" tail, or the amount unchanged when there is none.
    /// </summary>
    /// <remarks>
    /// Null when the tail names a group the target grammar cannot read, which leaves the whole
    /// sentence unread — an amount that quietly came out as one would be a card doing far less
    /// than it says.
    /// </remarks>
    private static Amount? CountedBy(Amount each, Group tail) =>
        tail.Success ? CountingAmount(each, tail.Value.Trim()) : each;

    /// <summary>
    /// Whether a printed count refers back to the triggering event rather than naming a number.
    /// </summary>
    /// <remarks>
    /// "That many" and "that much" are the same word in two grammars — cards are counted and life
    /// is measured — so they are one question here. Splitting them would be two spellings of one
    /// idea, and the second is where a spelling gets forgotten.
    /// </remarks>
    private static bool IsThatMany(string word) =>
        word.Trim().Equals("that many", StringComparison.OrdinalIgnoreCase)
        || word.Trim().Equals("that much", StringComparison.OrdinalIgnoreCase);

    internal static Amount Number(string word)
    {
        if (IsThatMany(word))
            return ThatMany();

        if (string.Equals(word, "X", StringComparison.OrdinalIgnoreCase))
            return Amount.X;

        if (int.TryParse(word, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            return n;

        return word.ToLowerInvariant() switch
        {
            "a" or "an" or "one" => 1,
            "two" => 2,
            "three" => 3,
            "four" => 4,
            "five" => 5,
            "six" => 6,
            "seven" => 7,
            "eight" => 8,
            "nine" => 9,
            "ten" => 10,
            "eleven" => 11,
            "twelve" => 12,
            "thirteen" => 13,
            "fourteen" => 14,
            "fifteen" => 15,
            "twenty" => 20,
            "thirty" => 30,
            "fifty" => 50,
            _ => 1,
        };
    }

    /// <summary>The target specs the templates keep reaching for (CR 115.1).</summary>
    public static partial class Specs
    {
        public static readonly TargetSpec AnyTarget = new()
        {
            Kind = TargetKind.Any,
            Description = "any target",
            ObjectFilter = (state, abilities, obj, _) =>
                Characteristics.Of(state, abilities, obj).IsCreature,
        };

        public static readonly TargetSpec TargetCreature = new()
        {
            Kind = TargetKind.Permanent,
            Description = "target creature",
            ObjectFilter = (state, abilities, obj, _) =>
                Characteristics.Of(state, abilities, obj).IsCreature,
        };

        public static readonly TargetSpec TargetPermanent = new()
        {
            Kind = TargetKind.Permanent,
            Description = "target permanent",
        };

        public static readonly TargetSpec TargetCreatureYouControl = new()
        {
            Kind = TargetKind.Permanent,
            Description = "target creature you control",
            ObjectFilter = (state, abilities, obj, controller) =>
                obj.ControllerId == controller
                && Characteristics.Of(state, abilities, obj).IsCreature,
        };

        /// <summary>A creature currently in combat (CR 506.3).</summary>
        public static readonly TargetSpec TargetAttackingOrBlockingCreature = new()
        {
            Kind = TargetKind.Permanent,
            Description = "target attacking or blocking creature",
            ObjectFilter = (state, abilities, obj, _) =>
                Characteristics.Of(state, abilities, obj).IsCreature
                && (state.Combat.Attackers.ContainsKey(obj.Id)
                    || state.Combat.Blockers.Values.Any(list => list.Contains(obj.Id))),
        };

        /// <summary>An opponent of the ability's controller (CR 109.5).</summary>
        public static readonly TargetSpec TargetOpponent = new()
        {
            Kind = TargetKind.Player,
            Description = "target opponent",
            PlayerFilter = (_, candidate, controller) => candidate != controller,
        };

        public static readonly TargetSpec TargetPlayer = new()
        {
            Kind = TargetKind.Player,
            Description = "target player",
        };

        public static readonly TargetSpec TargetSpell = new()
        {
            Kind = TargetKind.SpellOnStack,
            Description = "target spell",
        };

        /// <summary>
        /// "Target creature spell", "target noncreature spell" — a spell of a named type.
        /// </summary>
        /// <remarks>
        /// The adjective is read off the card rather than off computed characteristics, because
        /// the thing being described is still a spell on the stack: there is no permanent yet and
        /// the layers describe permanents (CR 613). Its printed types are what it has.
        /// <para>
        /// An adjective this does not know returns null, which loses the card. That is the right
        /// way round — a counterspell that could hit anything is a strictly better card, and the
        /// player aiming it would never find out it was wrong.
        /// </para>
        /// </remarks>
        private static TargetSpec? SpellOfKind(string adjective)
        {
            if (adjective.Length == 0)
                return TargetSpell;

            var negated = adjective.StartsWith("non", StringComparison.Ordinal);
            var bare = negated ? adjective[3..] : adjective;

            // "Target instant or sorcery spell" - two types the spell answers to *either* of,
            // which is the opposite of the several-words-one-card reading below: no card is both
            // an instant and a sorcery, so a conjunction would counter nothing. The same
            // distinction the search filters make between "and" and "or".
            var alternatives = bare
                .Split(" or ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(SpellTypes)
                .ToList();

            if (alternatives.Count == 0 || alternatives.Exists(one => one is null or { Count: 0 }))
                return null;

            return new TargetSpec
            {
                Kind = TargetKind.SpellOnStack,
                Description = $"target {adjective} spell",
                ObjectFilter = (_, _, obj, _) =>
                {
                    // Each alternative is a list of types the card must have all of, and the
                    // card answers to any one alternative.
                    var matches = alternatives.Exists(
                        one => one!.All(type => obj.Card.CardTypes.HasFlag(type)));

                    return negated ? !matches : matches;
                },
            };
        }

        /// <summary>
        /// Reads a printed target phrase — "target artifact creature an opponent controls".
        /// </summary>
        /// <remarks>
        /// The single change that stopped the effect vocabulary growing as a product. Before it,
        /// every effect needed its own regex per target it could take: "destroy target creature",
        /// "destroy target artifact", "destroy target creature an opponent controls" were three
        /// entries, and adding a target phrase meant adding it to destroy, exile, tap, bounce and
        /// pump separately. The phrase is one grammar and the effects are another, so they are
        /// read separately and multiplied.
        /// <para>
        /// It returns null rather than a permissive spec for anything it does not fully
        /// understand. A target filter that is too loose is worse than an unread line: the card
        /// looks implemented and lets a player aim at something the card forbids.
        /// </para>
        /// </remarks>
        public static TargetSpec? Parse(string phrase)
        {
            ArgumentNullException.ThrowIfNull(phrase);

            var text = phrase.Trim().TrimEnd('.');

            // "Up to one target creature" is an ordinary target that may be left unchosen
            // (CR 115.1). Read here rather than by each verb, because it is on more than six
            // hundred cards and every one says it in front of a phrase this method understands -
            // the alternative is teaching "up to" to every matcher that takes a target.
            if (UpToOnePrefix().Match(text) is { Success: true } upToOne)
            {
                return Parse(text[upToOne.Length..]) is { } single
                    ? single with { Optional = true, Description = "up to one " + single.Description }
                    : null;
            }

            // "Another target creature", and "other target creature" once a count has been
            // stripped off the front - the same phrase with the source left out (CR 115.1).
            // A prefix like "up to one", and read in the same place for the same reason: every
            // verb reaches its target through here, so none of them has to learn the word.
            if (AnotherPrefix().Match(text) is { Success: true } another)
            {
                return Parse("target " + text[another.Length..]) is { } other
                    ? other with
                    {
                        Description = "another " + other.Description,
                        SourceFilter = (_, _, obj, source, _) =>
                        {
                            if (source is null)
                                return true;

                            // The card that just changed zones is a new object (CR 400.7), so an
                            // id comparison alone lets a dies-trigger name the very card it came
                            // from - the one thing "another" exists to forbid.
                            var self = source.Ability?.SourceId ?? source.Id;
                            return obj.Id != self && obj.PreviousId != self;
                        },
                    }
                    : null;
            }

            // "Target creature that was dealt damage this turn" — a clause about what has
            // happened to the target rather than about what it is. It sits *after* the owner
            // clause, where the "with ..." qualifier grammar cannot reach it, so it is stripped
            // here and re-attached as a filter: every owner clause the grammar already reads
            // ("an opponent controls", "you don't control") arrives working, and so do the "up
            // to one" and "another" prefixes, which recurse back through here.
            if (DealtDamageClause().Match(text) is { Success: true } wound
                && Parse(wound.Groups["rest"].Value) is { Kind: TargetKind.Permanent } wounded)
            {
                var already = wounded.ObjectFilter;

                return wounded with
                {
                    Description = wounded.Description + " that was dealt damage this turn",

                    // Damage is marked on the permanent (CR 120.3) and stays marked until the
                    // cleanup step (CR 514.2), so "was dealt damage this turn" and "has damage
                    // marked" are the same question of a creature. The one thing that separates
                    // them here is regeneration, which removes the damage (CR 701.15a) — a
                    // creature that regenerated is no longer a legal target for these cards,
                    // which is narrower than printed and not wider.
                    ObjectFilter = (state, abilities, obj, controller) =>
                        obj.Permanent?.DamageMarked > 0
                        && already?.Invoke(state, abilities, obj, controller) != false,
                };
            }

            if (AnyTargetPhrase().IsMatch(text))
                return AnyTarget;

            // A card in a graveyard is targetable — the zone is public (CR 404.2) — and it is
            // its own kind of target, because the legality check has to look in a different zone.
            // "Target creature spell" — a spell of a named type. Read before the permanent
            // grammar, whose noun list would take the "creature" and then choke on "spell".
            if (TypedSpellPhrase().Match(text) is { Success: true } kindOfSpell)
                return SpellOfKind(kindOfSpell.Groups["adj"].Value.Trim().ToLowerInvariant());

            if (GraveyardPhrase().Match(text) is { Success: true } buried)
            {
                var buriedCap = buried.Groups["cap"].Value;
                var buriedCapIsX = string.Equals(buriedCap, "X", StringComparison.Ordinal);

                return InGraveyard(
                    buried.Groups["noun"].Value,
                    buried.Groups["whose"].Value,
                    buried.Groups["cap"].Success && !buriedCapIsX
                        ? int.Parse(buriedCap, CultureInfo.InvariantCulture)
                        : null,
                    buriedCapIsX);
            }

            // "...with power 3 or greater", "...without flying". A qualifier sits between the
            // noun and the owner clause, so it is lifted out and the rest is read as an ordinary
            // phrase - which is what lets "target creature with power 4 or greater you control"
            // work without the noun grammar having to know anything about power.
            Func<GameState, IAbilitySource, GameObject, int, bool>? qualifier = null;
            var qualifierReadsX = false;
            var described = text;

            // Kept before the qualifier is lifted off, because an alternation has to be split on
            // the words as printed: "artifact, enchantment, or creature with flying" binds the
            // qualifier to the last alternative alone, and stripping it up here would have
            // applied it to none of them.
            var asPrinted = text;

            if (QualifierPhrase().Match(text) is { Success: true } qualified)
            {
                qualifier = QualifierFilter(
                    qualified.Groups["q"].Value.Trim(), out qualifierReadsX);

                if (qualifier is null)
                    return null;

                text = text.Remove(
                    qualified.Groups["q"].Index - 1, qualified.Groups["q"].Length + 1);
            }

            var m = TargetPhrase().Match(text);
            if (!m.Success)
            {
                // "Target artifact or enchantment" is one target that may be either, not two
                // targets and not a type intersection. It is read as an alternation over the same
                // grammar, so every qualifier the single form accepts works on each side.
                //
                // Tried only *after* the single form, and that order is the whole point: "target
                // attacking or blocking creature" contains the word "or" and is not an
                // alternation at all — splitting it first gave "target attacking", which is not a
                // phrase, and lost every card that names a creature in combat.
                return EitherPhrase().Match(asPrinted) is { Success: true } either
                    ? Either(either.Groups["left"].Value, either.Groups["right"].Value)
                    : null;
            }

            var adjectives = m.Groups["adj"].Captures
                .Select(c => c.Value.Trim().ToLowerInvariant())
                .Where(a => a.Length > 0)
                .ToList();

            var noun = m.Groups["noun"].Value.Trim().ToLowerInvariant();
            var owner = m.Groups["own"].Value.Trim().ToLowerInvariant();

            // Players and spells are not permanents and take none of the permanent grammar, so
            // they are answered before any of it is applied.
            switch (noun)
            {
                case "player":
                    return owner.Length == 0 && adjectives.Count == 0 ? TargetPlayer : null;
                case "opponent":
                    return owner.Length == 0 && adjectives.Count == 0 ? TargetOpponent : null;

                // A spell takes its kind from one word, so two of them is a phrase this does not
                // read - refused rather than narrowed to whichever happened to be last.
                case "spell":
                    return owner.Length == 0 && adjectives.Count <= 1
                        ? SpellOfKind(adjectives.Count == 1 ? adjectives[0] : string.Empty)
                        : null;
            }

            // "Target Goblin", "Sacrifice a Goblin" — a subtype standing in for the noun, told
            // from an ordinary word by its capital letter the same way the tribal triggers tell
            // them apart. The card type is implied by which subtype it is (CR 205.3).
            string? subtype = null;
            var tribal = false;
            var required = PermanentTypes(noun);
            var eitherOf = required is null ? EitherPermanentType(noun) : null;

            if (required is null && eitherOf is not null)
                required = [];

            if (required is null)
            {
                var printed = m.Groups["noun"].Value.Trim();
                if (printed.Length < 2 || !char.IsUpper(printed[0]))
                    return null;

                subtype = printed;

                // Which card type a subtype implies is a fact about that subtype, and reading it
                // as "creature" whatever the word was is a card that compiles and does nothing:
                // "you gain 1 life for each Equipment you control" asked for a *creature* with the
                // subtype Equipment, of which there are none, so it gained nothing and reported
                // itself complete. 945 corpus cards name a subtype that is not a creature type.
                //
                // This is the failure that is worse than an unread line, because nothing says so -
                // the deck builder allows the card and the board plays it wrong in silence.
                // The card type printed after the subtype wins over the one the subtype implies.
                // "Nissa" is a planeswalker type and would otherwise fall to the creature default,
                // because the planeswalker types are eighty proper names and a table of them
                // would go stale every set - the word after them does not.
                var spelled = m.Groups["kind"].Success
                    ? PermanentTypes(m.Groups["kind"].Value.Trim().ToLowerInvariant())
                    : null;

                var implied = spelled is [var only] ? only : SubtypeCardType(printed);

                tribal = implied == CardType.Creature;
                required = spelled is [] ? [] : [implied];
            }

            var filters = adjectives.ConvertAll(AdjectiveFilter);
            if (filters.Exists(f => f is null))
                return null;

            Func<GameState, IAbilitySource, GameObject, bool> adjectiveFilter =
                (state, abilities, obj) =>
                    filters.TrueForAll(f => f!.Invoke(state, abilities, obj));

            // "Creature defending player controls" - who that is depends on which combat the
            // source is in, so it cannot be an ordinary owner filter: those are handed the state
            // and the object and nothing about who is asking. It is a source filter, which is
            // what that second delegate exists for.
            var defending = string.Equals(
                owner, "defending player controls", StringComparison.OrdinalIgnoreCase);

            // "That player" is the player the trigger was about, which is recorded on the
            // ability when it triggers (CR 603.2) and known nowhere else by the time targets are
            // chosen. Like the defending player it depends on who is asking, so it is a source
            // filter rather than one of the ordinary owner filters - those are handed the object
            // and nothing about the ability.
            var theirs = string.Equals(
                owner, "that player controls", StringComparison.OrdinalIgnoreCase);

            if (!defending && !theirs && OwnerFilter(owner) is null)
                return null;

            var ownerFilter = defending || theirs
                ? (_, _, _, _) => true
                : OwnerFilter(owner)!;

            return new TargetSpec
            {
                Kind = TargetKind.Permanent,
                Description = described,
                SourceFilter = theirs
                    ? (state, abilities, obj, source, _) =>
                        source?.Ability?.SubjectPlayer is { } about
                        && Characteristics.Of(state, abilities, obj).ControllerId == about
                    : !defending
                    ? null
                    : (state, abilities, obj, source, _) =>
                    {
                        // The attacker names the player. Which object that is depends on when
                        // the question is asked: as the ability is put on the stack the source is
                        // the permanent, and as it resolves it is the ability, whose own source
                        // is the permanent (CR 405.4). Asking only the first way made every one
                        // of these targets legal to choose and illegal to resolve.
                        if (source is null)
                            return false;

                        var attacker = source.Ability?.SourceId ?? source.Id;

                        // With no attack there is no defending player and nothing answers to the
                        // phrase, which is the right answer outside combat rather than an error.
                        if (!state.Combat.Attackers.TryGetValue(attacker, out var attacking))
                            return false;

                        return Characteristics.Of(state, abilities, obj).ControllerId
                            == attacking.DefendingPlayer;
                    },
                // A clause that measures against X hangs off VariableFilter instead, so the
                // spec refuses outright wherever no value was announced rather than being asked
                // with a zero. It is the whole filter that moves and not just the clause: the
                // two are one conjunction, and splitting them would leave the type test
                // answering on its own for a card that has not said what X is.
                ObjectFilter = qualifierReadsX
                    ? null
                    : (state, abilities, obj, controller) =>
                        Matches(state, abilities, obj, controller, 0),
                VariableFilter = qualifierReadsX ? Matches : null,
            };

            bool Matches(
                GameState state,
                IAbilitySource abilities,
                GameObject obj,
                Guid controller,
                int announced)
            {
                if (qualifier?.Invoke(state, abilities, obj, announced) == false)
                    return false;

                // Types come from the computed characteristics, not the printed card: a land
                // animated into a creature is a legal "target creature" (CR 613.1c).
                var computed = Characteristics.Of(state, abilities, obj);
                foreach (var type in required)
                {
                    if (!computed.CardTypes.HasFlag(type))
                        return false;
                }

                // "Target artifact or enchantment" - one target that may be either, so the
                // types are alternatives rather than the intersection every other list here
                // means. Held apart for that reason: an intersection would ask for a card
                // that is both at once, which is a different and much rarer thing.
                if (eitherOf is { } alternatives && (computed.CardTypes & alternatives) == 0)
                    return false;

                // CR 702.73a: a changeling is every creature type, so it answers to any tribe.
                // Every *creature* type — an artifact creature changeling is not an Equipment,
                // so the bypass is offered only where the subtype was read as a tribe.
                if (subtype is not null
                    && !(tribal && computed.IsEveryCreatureType)
                    && !computed.HasSubtype(subtype))
                {
                    return false;
                }

                return adjectiveFilter(state, abilities, obj)
                    && ownerFilter(state, abilities, obj, controller);
            }
        }

        /// <summary>
        /// One target that may be either of two things (CR 115.1).
        /// </summary>
        /// <remarks>
        /// Built by parsing each side as a target phrase in its own right and accepting whatever
        /// either accepts. Writing it that way rather than as a table of pairs means "target
        /// creature or planeswalker an opponent controls" works the moment both halves do.
        /// <para>
        /// A pair spanning a player and a permanent — "target player or planeswalker" — is exactly
        /// what <see cref="TargetKind.Any"/> already models, so it resolves to that rather than to
        /// a kind that cannot hold both.
        /// </para>
        /// </remarks>
        private static TargetSpec? Either(string leftPhrase, string rightPhrase)
        {
            // Each side is written without the word "target", which only the whole phrase carries.
            //
            // A longer list arrives here as "artifact" and "enchantment, or creature": the right
            // side still holds a list, and parsing it goes through this same reader again. So one
            // rule covers two alternatives or five, and the comma needed no grammar of its own -
            // it only had to stop being excluded by the pattern above.
            var left = Parse("target " + leftPhrase.Trim().TrimEnd(','));
            var right = Parse("target " + rightPhrase.Trim());
            if (left is null || right is null)
                return null;

            if (left.Kind != right.Kind)
            {
                return left.Kind is TargetKind.Player || right.Kind is TargetKind.Player
                    ? new TargetSpec
                    {
                        Kind = TargetKind.Any,
                        Description = $"target {leftPhrase} or {rightPhrase}",
                        ObjectFilter = (state, abilities, obj, controller) =>
                            (left.Kind is TargetKind.Permanent
                                && left.ObjectFilter?.Invoke(state, abilities, obj, controller) != false)
                            || (right.Kind is TargetKind.Permanent
                                && right.ObjectFilter?.Invoke(state, abilities, obj, controller) != false),
                    }
                    : null;
            }

            return new TargetSpec
            {
                Kind = left.Kind,
                Description = $"target {leftPhrase} or {rightPhrase}",
                ObjectFilter = (state, abilities, obj, controller) =>
                    left.ObjectFilter?.Invoke(state, abilities, obj, controller) != false
                    || right.ObjectFilter?.Invoke(state, abilities, obj, controller) != false,
                PlayerFilter = (state, candidate, controller) =>
                    left.PlayerFilter?.Invoke(state, candidate, controller) != false
                    || right.PlayerFilter?.Invoke(state, candidate, controller) != false,
            };
        }

        /// <summary>
        /// Reads a group phrase — "all creatures", "creatures your opponents control".
        /// </summary>
        /// <remarks>
        /// Rewritten into the target grammar rather than parsed separately: the two describe the
        /// same sets in almost the same words, and a second vocabulary would have to be kept in
        /// step with the first by hand. The resulting spec is used only as a filter — nothing is
        /// targeted — which is why the caller never adds it to the targets list.
        /// </remarks>
        public static TargetSpec? ParseGroup(string phrase)
        {
            ArgumentNullException.ThrowIfNull(phrase);

            var text = GroupOpener().Replace(phrase.Trim().TrimEnd('.'), string.Empty).Trim();
            if (text.Length == 0)
                return null;

            // "You" is a player and never a group of permanents. Left to the noun grammar below
            // it is read as a creature type, which is the "Islands"/"Assassins" defect those
            // comments describe arriving through a pronoun instead of a plural: "You gain shroud
            // until end of turn" compiled into a keyword grant to a tribe no card has, so Gilded
            // Light read as a complete card, passed the deck gate and did nothing when it
            // resolved. Refused here rather than answered, because the sentence is about a
            // player and this method can only describe objects — the reading belongs with
            // PlayerQualityDefinition, which has no floating form yet.
            if (text.Equals("you", StringComparison.OrdinalIgnoreCase))
                return null;

            // "Other creatures you control get +2/+2" - the word excludes the permanent whose
            // ability this is, and dropping it would make a lord pump itself. Lifted off before
            // the noun is read, because everything after it is an ordinary group phrase, and put
            // back as a filter that has the source to compare against. 127 corpus lines carry it.
            var excludesSelf = OtherOpener().IsMatch(text);
            if (excludesSelf)
                text = OtherOpener().Replace(text, string.Empty).Trim();

            // The grammar is written around a singular noun, so the plural is folded back.
            text = PluralNoun().Replace(text, "$1");

            // That regex knows the six card types and nothing else, so a tribe or a land type
            // stayed plural and the grammar went looking for a subtype called "Islands" - which
            // no card has. Every mass effect naming one silently touched nothing: Boil destroyed
            // no Islands, and it compiled, cast and resolved without complaint.
            //
            // The run of capitalised words is the noun, and only its last word carries the
            // plural: "Elf Warriors" is one noun with the s on the end of it.
            //
            // The run is found wherever it starts, not only at the front. Scanning from index 0
            // meant a single lowercase adjective stopped the search before it reached the noun, so
            // "untapped Mountains you control" and "tapped Assassins you control" stayed plural
            // and asked for the types "Mountains" and "Assassins", which no card has. Ben-Ben
            // dealt damage equal to the number of "Mountains" and Lydia Frye surveilled per
            // "Assassins" - both counting zero, both compiling as complete cards.
            var words = text.Split(' ');
            var start = 0;

            while (start < words.Length
                && !(words[start].Length > 1 && char.IsUpper(words[start][0])))
            {
                start++;
            }

            var head = start;

            while (head < words.Length && words[head].Length > 1 && char.IsUpper(words[head][0]))
                head++;

            if (head > start)
            {
                words[head - 1] = SingularWord(words[head - 1]);
                text = string.Join(' ', words);
            }

            var group = Parse("target " + text) ?? Union(phrase);

            if (group is null || !excludesSelf)
                return group;

            // Layered onto whatever the phrase already asked for rather than replacing it: the
            // source is one more thing the group is not, not the only thing.
            var already = group.SourceFilter;

            return group with
            {
                Description = "other " + group.Description,
                SourceFilter = (state, abilities, obj, source, controller) =>
                    (already?.Invoke(state, abilities, obj, source, controller) != false)
                    && (source is null || obj.Id != source.Id),
            };
        }

        /// <summary>
        /// "All artifacts, creatures, and enchantments" — several plural nouns meaning any of
        /// them (CR 109.2).
        /// </summary>
        /// <remarks>
        /// The grammar reads "artifact or enchantment" already, and every one of these lines is
        /// that group with the printed conjunction spelled the other way. Seventeen corpus wipes
        /// were unread for the word "and" alone — Nevinyrral's Disk beside Akroma's Vengeance,
        /// which says the same thing with "or" and has compiled for months.
        /// <para>
        /// <strong>Plural on every element, or nothing.</strong> That is the whole of what tells
        /// a union apart from a single noun with a compound adjective: "artifacts and
        /// enchantments" is two groups, and "artifact and enchantment creatures" — were a card
        /// ever to print it — is one, whose members must be both. Reading the second as the first
        /// would destroy every artifact on the board, which is a far worse card than an unread
        /// line. So the test is made against what was printed, before the plural is folded away.
        /// </para>
        /// <para>
        /// Asked only where the ordinary reading has already failed, so a phrase that reads today
        /// cannot start reading as a union tomorrow.
        /// </para>
        /// </remarks>
        private static TargetSpec? Union(string phrase)
        {
            var text = GroupOpener()
                .Replace(phrase.Trim().TrimEnd('.'), string.Empty)
                .Trim();

            var parts = UnionJoin().Split(text);
            if (parts.Length < 2)
                return null;

            var singular = new List<string>(parts.Length);

            foreach (var part in parts)
            {
                var one = part.Trim();

                // Every element has to be a plural noun the grammar knows, and it has to be the
                // last word: "all creatures you control and artifacts" is not a shape any card
                // prints, and admitting it here would quietly drop the "you control".
                if (!PluralNoun().IsMatch(one) || !one.EndsWith('s'))
                    return null;

                singular.Add(PluralNoun().Replace(one, "$1"));
            }

            return Parse("target " + string.Join(" or ", singular));
        }

        /// <summary>The conjunctions a printed list of groups is joined by.</summary>
        /// <remarks>
        /// The Oxford comma is taken with the "and" rather than left as an empty element, and the
        /// bare comma is here for the same list's middle. "Or" is included so that a list mixing
        /// the two — no card prints one, but nothing here has to care — reads the same way.
        /// <para>
        /// Every group is non-capturing, and that is not tidiness: <c>Regex.Split</c> returns the
        /// captured groups <em>alongside</em> the pieces it split, so a capturing "and" arrives as
        /// an element of the list and the plural test below rejects the whole phrase. It read
        /// nothing at all until the groups came out.
        /// </para>
        /// </remarks>
        [GeneratedRegex(@",\s+(?:and\s+|or\s+)?|\s+(?:and|or)\s+", RegexOptions.IgnoreCase)]
        private static partial Regex UnionJoin();

        [GeneratedRegex(@"^(all|each|every)\s+", RegexOptions.IgnoreCase)]
        private static partial Regex GroupOpener();

        /// <summary>"Other creatures you control" - the group without the source in it.</summary>
        [GeneratedRegex(@"^other\s+", RegexOptions.IgnoreCase)]
        private static partial Regex OtherOpener();

        [GeneratedRegex(
            @"\b(creature|permanent|artifact|enchantment|land|planeswalker|token)s\b",
            RegexOptions.IgnoreCase)]
        private static partial Regex PluralNoun();

        /// <summary>A card in a graveyard, filtered by type and by whose graveyard (CR 404.2).</summary>
        /// <summary>
        /// A card type by name, including the ones that are never permanents (CR 205.2a).
        /// </summary>
        /// <remarks>
        /// <see cref="PermanentTypes"/> deliberately knows only the types a permanent can have,
        /// which is right where it is used and wrong here: a graveyard is full of instants and
        /// sorceries, and "target instant or sorcery card" is the commonest way to name them.
        /// </remarks>
        internal static CardType? CardTypeInGraveyard(string name) => name.ToLowerInvariant() switch
        {
            "creature" => CardType.Creature,
            "artifact" => CardType.Artifact,
            "enchantment" => CardType.Enchantment,
            "land" => CardType.Land,
            "planeswalker" => CardType.Planeswalker,
            "instant" => CardType.Instant,
            "sorcery" => CardType.Sorcery,
            _ => null,
        };

        /// <param name="maxManaValue">
        /// The cap the phrase prints, if it prints one, and null if it does not. A cap of
        /// <c>null</c> and a cap of X are different things and are told apart by
        /// <paramref name="capIsVariable"/>: X is not a number until the spell is cast.
        /// </param>
        private static TargetSpec? InGraveyard(
            string noun, string whose, int? maxManaValue = null, bool capIsVariable = false)
        {
            var word = noun.Trim();
            string? subtype = null;

            // "Target nonland permanent card in your graveyard" - a type word with a type taken
            // out of it, which the type table cannot hold and does not need to: the word in front
            // is one more test rather than a different noun. Lifted off before the table is asked
            // so every noun it knows keeps working with it.
            var excludesLands = word.StartsWith("nonland ", StringComparison.OrdinalIgnoreCase);
            if (excludesLands)
                word = word[8..].Trim();

            // "Target instant or sorcery card" - one target that may be either. Each side is a
            // type in its own right and the card only has to be one of them, so they are held as
            // alternatives rather than as the intersection every other type list means here.
            var either = EitherType().Split(word);

            if (either.Length == 2
                && CardTypeInGraveyard(either[0].Trim()) is { } first
                && CardTypeInGraveyard(either[1].Trim()) is { } second)
            {
                return new TargetSpec
                {
                    Kind = TargetKind.CardInGraveyard,
                    Description = $"target {word} card from {whose} graveyard",
                    ObjectFilter = (_, _, obj, controller) =>
                        (obj.Card.CardTypes.HasFlag(first) || obj.Card.CardTypes.HasFlag(second))
                        && (!whose.Trim().StartsWith("your", StringComparison.OrdinalIgnoreCase)
                            || obj.OwnerId == controller),
                };
            }

            // "Target card in a graveyard" names no type at all, and means any card there. An
            // empty list of required types is how "anything" is already spelled for permanents.
            //
            // A graveyard is not the battlefield, so the type table it asks is not the permanent
            // one: an instant or a sorcery is a perfectly ordinary card to name there. The pair
            // form above already knew that and the single form did not, so "target instant or
            // sorcery card" read while "target sorcery card" did not.
            var required = word.Length == 0
                ? []
                : PermanentTypes(word)
                    ?? (CardTypeInGraveyard(word) is { } only ? [only] : null);

            if (required is null)
            {
                if (word.Length < 2 || !char.IsUpper(word[0]))
                    return null;

                subtype = word;
                required = [CardType.Creature];
            }

            var mine = whose.Trim().StartsWith("your", StringComparison.OrdinalIgnoreCase);

            var capWord = capIsVariable
                ? "X"
                : maxManaValue?.ToString(CultureInfo.InvariantCulture);

            return new TargetSpec
            {
                Kind = TargetKind.CardInGraveyard,
                Description = capWord is { } cap
                    ? $"target {(excludesLands ? "nonland " : string.Empty)}{word} card with mana "
                        + $"value {cap} or less from {whose} graveyard"
                        .Replace("  ", " ", StringComparison.Ordinal)
                    : $"target {(excludesLands ? "nonland " : string.Empty)}{word} card from "
                        + $"{whose} graveyard"
                        .Replace("  ", " ", StringComparison.Ordinal),

                // A printed cap is an ordinary object filter; a cap of X is the same test against
                // a number only the cast knows, so it goes where a caller with no announced value
                // is refused rather than answered.
                ObjectFilter = capIsVariable
                    ? null
                    : (_, _, obj, controller) => Matches(obj, controller, maxManaValue),
                VariableFilter = capIsVariable
                    ? (_, _, obj, controller, announced) =>
                        Matches(obj, controller, announced)
                    : null,
            };

            bool Matches(GameObject obj, Guid controller, int? cap)
            {
                if (excludesLands && obj.Card.CardTypes.HasFlag(CardType.Land))
                    return false;

                // CR 202.3: mana value is computed from the printed cost, and a card in a
                // graveyard has only its printed cost — nothing on the battlefield can be
                // raising or lowering it.
                if (cap is { } limit && obj.Card.Cmc > limit)
                    return false;

                // Read from the card, not from computed characteristics: a card in a
                // graveyard is not on the battlefield, so no continuous effect applies to it
                // (CR 613 is about permanents) and its printed types are what it has.
                foreach (var type in required)
                {
                    if (!obj.Card.CardTypes.HasFlag(type))
                        return false;
                }

                if (subtype is not null
                    && !obj.Card.Subtypes.Contains(subtype, StringComparer.OrdinalIgnoreCase))
                {
                    return false;
                }

                return !mine || obj.OwnerId == controller;
            }
        }

        /// <remarks>
        /// The mana-value cap is optional and shared with every other graveyard phrase, which is
        /// the point of putting it in the grammar rather than building the one spec soulshift
        /// needs: reanimation spells cap the same way, and they get it for free.
        /// </remarks>
        [GeneratedRegex(
            @"^[Tt]arget ((?<noun>(nonland )?[A-Za-z]+( or [A-Za-z]+)?) )?card"
                + @"( with mana value (?<cap>\d+|X) or less)?"
                + @" (from|in) (?<whose>your|a|an opponent's) graveyard$",
            RegexOptions.None)]
        private static partial Regex GraveyardPhrase();

        /// <summary>Every card type a printed noun demands at once, or null if it is not one.</summary>
        /// <remarks>
        /// Internal rather than private because the trigger grammar needs the same answer — "a
        /// permanent you control is put into a graveyard" and "target artifact" are asking one
        /// question. A second copy of this table is a second place to forget a card type.
        /// </remarks>
        /// <summary>
        /// "Artifact or enchantment" as a mask of the types it may be, or null if not a pair.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="PermanentTypes"/> because the two mean opposite things: that
        /// one lists what a permanent must be <em>all</em> of, and this one lists what it may be
        /// <em>any</em> of. Returning a pair from the same method would have made every caller
        /// guess which it had been handed.
        /// </remarks>
        internal static CardType? EitherPermanentType(string noun)
        {
            var halves = EitherType().Split(noun);
            if (halves.Length != 2)
                return null;

            if (PermanentTypes(halves[0].Trim()) is not [var first]
                || PermanentTypes(halves[1].Trim()) is not [var second])
            {
                return null;
            }

            return first | second;
        }

        /// <summary>
        /// The types a *spell* can be named by, which is every card type and not only the ones
        /// that make permanents (CR 205.2a).
        /// </summary>
        /// <remarks>
        /// A spell on the stack is a card of any type, so "target instant spell" and "target
        /// sorcery spell" are ordinary phrases — and neither could be read, because the only
        /// type table here was the one built for permanents. Kept separate rather than adding
        /// them to that table: a permanent is never an instant, and a filter that said otherwise
        /// would let "destroy target instant" compile.
        /// </remarks>
        private static IReadOnlyList<CardType>? SpellTypes(string noun) => noun switch
        {
            "instant" => [CardType.Instant],
            "sorcery" => [CardType.Sorcery],
            "battle" => [CardType.Battle],
            _ => PermanentTypes(noun),
        };

        internal static IReadOnlyList<CardType>? PermanentTypes(string noun) => noun switch
        {
            "permanent" => [],
            "creature" => [CardType.Creature],
            "artifact" => [CardType.Artifact],
            "enchantment" => [CardType.Enchantment],
            "land" => [CardType.Land],
            "planeswalker" => [CardType.Planeswalker],

            // CR 111.1: a token is a permanent, and which kind is not part of the word.
            "token" => [CardType.Token],
            "artifact creature" => [CardType.Artifact, CardType.Creature],
            "enchantment creature" => [CardType.Enchantment, CardType.Creature],

            // "Creature token", "artifact token" - a kind of permanent and the fact that it is a
            // token, which is two tests and reads as one noun. Spelled out beside the other
            // compounds rather than parsed as an adjective, because "token" is the *only* word
            // that combines this way and a general rule for one word is a rule waiting to be
            // wrong. 52 corpus lines name one of these.
            "creature token" => [CardType.Creature, CardType.Token],
            "artifact token" => [CardType.Artifact, CardType.Token],
            "enchantment token" => [CardType.Enchantment, CardType.Token],
            "artifact creature token" => [CardType.Artifact, CardType.Creature, CardType.Token],
            _ => null,
        };

        private static Func<GameState, IAbilitySource, GameObject, bool>? AdjectiveFilter(
            string adjective)
        {
            // A negated creature type is open-ended, so it is answered here rather than in the
            // table below: there are some fifteen hundred of them and the cards use whichever
            // they please. CR 702.73a decides the awkward case - a changeling is every creature
            // type, so it is a non-Human no more than it is a non-Wall.
            if (adjective.StartsWith("non-", StringComparison.Ordinal))
            {
                var excluded = adjective[4..];
                return excluded.Length == 0
                    ? null
                    : (state, abilities, obj) =>
                        !Characteristics.Of(state, abilities, obj).HasSubtype(excluded);
            }

            // "Red or green creature" - one adjective naming two colours, and the only place in
            // this vocabulary where a word is an alternative rather than another thing that has
            // to be true. Answered by splitting it rather than by two dozen table rows, so a pair
            // the cards have not printed yet works the same as the ones they have.
            var either = adjective.Split(" or ", StringSplitOptions.TrimEntries);
            if (either.Length == 2
                && ColorNamed(either[0]) is { } left
                && ColorNamed(either[1]) is { } right)
            {
                return (state, abilities, obj) =>
                {
                    var colors = Characteristics.Of(state, abilities, obj).Colors;
                    return colors.Contains(left) || colors.Contains(right);
                };
            }

            return adjective switch
            {
                "" => (_, _, _) => true,
                "attacking" => (state, _, obj) => state.Combat.Attackers.ContainsKey(obj.Id),
                "blocking" => (state, _, obj) =>
                    state.Combat.Blockers.Values.Any(list => list.Contains(obj.Id)),
                "attacking or blocking" => (state, _, obj) =>
                    state.Combat.Attackers.ContainsKey(obj.Id)
                    || state.Combat.Blockers.Values.Any(list => list.Contains(obj.Id)),
                "unblocked" => (state, _, obj) =>
                    state.Combat.Attackers.ContainsKey(obj.Id)
                    && !state.Combat.Blockers.ContainsKey(obj.Id),
                "tapped" => (_, _, obj) => obj.Permanent?.IsTapped == true,
                "untapped" => (_, _, obj) => obj.Permanent?.IsTapped == false,
                "nonland" => (_, _, obj) => !obj.Card.CardTypes.HasFlag(CardType.Land),

                // Supertypes are read off the printed card because nothing in the engine changes
                // one; colours and types are read computed, because plenty of things change those.
                "legendary" => (_, _, obj) => IsLegendary(obj),
                "nonlegendary" => (_, _, obj) => !IsLegendary(obj),
                "token" => (_, _, obj) => obj.Card.CardTypes.HasFlag(CardType.Token),
                "nontoken" => (_, _, obj) => !obj.Card.CardTypes.HasFlag(CardType.Token),
                "multicolored" => (state, abilities, obj) =>
                    Characteristics.Of(state, abilities, obj).Colors.Count > 1,
                "monocolored" => (state, abilities, obj) =>
                    Characteristics.Of(state, abilities, obj).Colors.Count == 1,
                "colorless" => (state, abilities, obj) =>
                    Characteristics.Of(state, abilities, obj).Colors.IsEmpty,
                "face-down" => (_, _, obj) => obj.Permanent?.IsFaceDown == true,
                "noncreature" => NotOfType(CardType.Creature),
                "nonartifact" => NotOfType(CardType.Artifact),
                "nonenchantment" => NotOfType(CardType.Enchantment),

                // Colour negations, computed like every other colour question: a creature an
                // effect has turned black is not a legal target for "destroy target nonblack
                // creature", however it was printed (CR 105.2, 613.1e).
                "nonwhite" => NotOfColor(ManaColor.White),
                "nonblue" => NotOfColor(ManaColor.Blue),
                "nonblack" => NotOfColor(ManaColor.Black),
                "nonred" => NotOfColor(ManaColor.Red),
                "nongreen" => NotOfColor(ManaColor.Green),

                // And the colours themselves, which were never here: only their negations were,
                // so "destroy all white creatures" had no reading at all.
                "white" => OfColor(ManaColor.White),
                "blue" => OfColor(ManaColor.Blue),
                "black" => OfColor(ManaColor.Black),
                "red" => OfColor(ManaColor.Red),
                "green" => OfColor(ManaColor.Green),

                // "Basic" and "snow" are supertype questions and read off the printed card,
                // because nothing in the engine makes a land basic or snow or stops it being so
                // (CR 205.4a). "Nonbasic" was here without "basic", the same way the colour
                // negations were here without the colours.
                "nonbasic" => (_, _, obj) => !HasSupertype(obj, "Basic"),
                "basic" => (_, _, obj) => HasSupertype(obj, "Basic"),
                "snow" => (_, _, obj) => HasSupertype(obj, "Snow"),

                // CR 509.1h: a creature stays blocked once it has been, even if every creature
                // blocking it has gone.
                "blocked" => (state, _, obj) => state.Combat.Blocked.Contains(obj.Id),

                // CR 701.48a: modified is a counter on it, an Aura its controller controls, or
                // Equipment attached - three unrelated things under one word, like historic.
                "modified" => (state, _, obj) => IsModified(state, obj),
                "enchanted" => (state, _, obj) => AttachedTo(state, obj, aurasOnly: true),

                // CR 205.4h: historic is legendary, artifact, or Saga - three unrelated things
                // under one word, which is why it is spelled out rather than derived.
                "historic" => (state, abilities, obj) =>
                    IsLegendary(obj)
                    || Characteristics.Of(state, abilities, obj).CardTypes
                        .HasFlag(CardType.Artifact)
                    || Characteristics.Of(state, abilities, obj).HasSubtype("Saga"),
                _ => null,
            };
        }

        /// <summary>One of the five colours by name, or null for any other word (CR 105.1).</summary>
        private static ManaColor? ColorNamed(string word) => word.ToLowerInvariant() switch
        {
            "white" => ManaColor.White,
            "blue" => ManaColor.Blue,
            "black" => ManaColor.Black,
            "red" => ManaColor.Red,
            "green" => ManaColor.Green,
            _ => null,
        };

        /// <summary>A printed supertype, which nothing in the engine changes (CR 205.4a).</summary>
        private static bool HasSupertype(GameObject obj, string supertype) =>
            obj.Card.Supertypes.Contains(supertype, StringComparer.OrdinalIgnoreCase);

        /// <summary>Whether anything is attached to this permanent (CR 701.3).</summary>
        private static bool AttachedTo(GameState state, GameObject obj, bool aurasOnly) =>
            state.Battlefield
                .Select(state.GetObject)
                .Any(other => other.Permanent?.AttachedTo == obj.Id
                    && (!aurasOnly
                        || other.Card.Subtypes.Contains("Aura", StringComparer.OrdinalIgnoreCase)));

        /// <summary>CR 701.48a — a counter on it, an Aura on it, or Equipment attached.</summary>
        private static bool IsModified(GameState state, GameObject obj) =>
            (obj.Permanent?.Counters.Values.Any(count => count > 0) ?? false)
            || AttachedTo(state, obj, aurasOnly: false);

        /// <summary>Everything of a given colour, read computed (CR 105.2, 613.1e).</summary>
        private static Func<GameState, IAbilitySource, GameObject, bool> OfColor(ManaColor colour) =>
            (state, abilities, obj) =>
                Characteristics.Of(state, abilities, obj).Colors.Contains(colour);

        /// <summary>Everything that is not of a given type, read computed (CR 613.1c).</summary>
        private static Func<GameState, IAbilitySource, GameObject, bool> NotOfType(CardType type) =>
            (state, abilities, obj) =>
                !Characteristics.Of(state, abilities, obj).CardTypes.HasFlag(type);

        /// <summary>
        /// A permanent that is not of a colour — which is not the same as being colourless.
        /// </summary>
        /// <remarks>
        /// A colourless creature is nonblack, and so is a white one. The question is only whether
        /// the colour is among its colours (CR 105.2), computed rather than printed because
        /// plenty of effects change a permanent's colour.
        /// </remarks>
        private static Func<GameState, IAbilitySource, GameObject, bool> NotOfColor(ManaColor color) =>
            (state, abilities, obj) =>
                !Characteristics.Of(state, abilities, obj).Colors.Contains(color);

        /// <summary>The five subtypes that name a land rather than a creature (CR 205.3i).</summary>
        internal static bool IsBasicLandType(string subtype) =>
            subtype is "Plains" or "Island" or "Swamp" or "Mountain" or "Forest";

        /// <summary>The artifact types, verbatim from CR 205.3g.</summary>
        private static readonly HashSet<string> ArtifactTypes =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "Attraction", "Blood", "Bobblehead", "Book", "Clue", "Contraption", "Equipment",
                "Food", "Fortification", "Gold", "Incubator", "Infinity", "Junk", "Lander", "Map",
                "Mutagen", "Powerstone", "Spacecraft", "Stone", "Treasure", "Vehicle", "Vibranium",
            };

        /// <summary>The enchantment types, verbatim from CR 205.3h.</summary>
        private static readonly HashSet<string> EnchantmentTypes =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "Aura", "Background", "Cartouche", "Case", "Class", "Curse", "Plan", "Role",
                "Room", "Rune", "Saga", "Shard", "Shrine",
            };

        /// <summary>The land types, verbatim from CR 205.3i.</summary>
        private static readonly HashSet<string> LandTypes =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "Cave", "Desert", "Forest", "Gate", "Island", "Lair", "Locus", "Mine", "Mountain",
                "Plains", "Planet", "Power-Plant", "Sphere", "Swamp", "Tower", "Town", "Urza's",
            };

        /// <summary>
        /// The card type a subtype implies (CR 205.3).
        /// </summary>
        /// <remarks>
        /// Every card type has its <em>own</em> set of subtypes and the sets do not overlap, so
        /// the word alone settles it: an Equipment is an artifact, an Aura is an enchantment, a
        /// Gate is a land, and everything left over is a creature type — those are the long list
        /// and the ordinary case, which is why they are the fallback rather than a fourth table
        /// that would have to be kept in step with every new set.
        /// <para>
        /// It exists because reading every capitalised noun as a creature type is not a gap, it
        /// is a card that plays wrong in silence: "for each Equipment you control" asked for a
        /// creature with the Equipment subtype, found none, gained nothing, and reported itself
        /// completely understood. An unread line is visible; this was not.
        /// </para>
        /// <para>
        /// The three lists are copied from the rules document rather than derived from the corpus
        /// deliberately. A subtype the corpus has not printed yet is still a subtype, and a table
        /// built from what happened to be in a bulk file goes wrong on the next set rather than
        /// on the next release of the rules.
        /// </para>
        /// </remarks>
        internal static CardType SubtypeCardType(string subtype) =>
            ArtifactTypes.Contains(subtype) ? CardType.Artifact
            : EnchantmentTypes.Contains(subtype) ? CardType.Enchantment
            : LandTypes.Contains(subtype) ? CardType.Land
            : CardType.Creature;

        private static bool IsLegendary(GameObject obj) =>
            obj.Card.Supertypes.Contains("Legendary", StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// A "with ..." clause narrowing what may be targeted, or null if it is not one we read.
        /// </summary>
        /// <remarks>
        /// Numbers come from the <em>computed</em> characteristics, so a creature pumped to 4
        /// power answers "with power 3 or greater" and one shrunk below it stops answering
        /// (CR 613.1). Mana value is the printed one: nothing in the engine changes it, and the
        /// card is the only place it is written down.
        /// </remarks>
        /// <param name="readsVariable">
        /// Whether the clause measures against X rather than against a printed number. The
        /// caller has to know, because a filter that reads X may not be asked without one: it
        /// goes in <see cref="TargetSpec.VariableFilter"/>, which refuses when no value was
        /// announced, rather than in the plain object filter, which would be handed a zero.
        /// </param>
        private static Func<GameState, IAbilitySource, GameObject, int, bool>? QualifierFilter(
            string qualifier, out bool readsVariable)
        {
            readsVariable = false;

            var counter = CounterQualifier().Match(qualifier);
            if (counter.Success)
            {
                // "A counter on it" with no kind named means any counter at all (CR 122.1), and
                // a permanent can carry a counter at zero, so the count is what is asked about
                // rather than whether the key is present.
                if (!counter.Groups["kind"].Success)
                    return (_, _, obj, _) => obj.Permanent?.Counters.Values.Any(n => n > 0) == true;

                var kind = counter.Groups["kind"].Value;
                return (_, _, obj, _) => obj.Permanent?.Counters.GetValueOrDefault(kind, 0) > 0;
            }

            var number = NumberQualifier().Match(qualifier);
            if (number.Success)
            {
                // "With mana value X or less" measures against a number nothing printed on the
                // card knows - the value announced as the spell was cast (CR 601.2b). The
                // comparison is otherwise identical, so the two spellings share one delegate and
                // differ only in where the number comes from.
                var printed = number.Groups["n"].Value;
                var variable = string.Equals(printed, "X", StringComparison.Ordinal);
                readsVariable = variable;

                var wanted = variable ? 0 : Number(printed).Fixed;
                var direction = number.Groups["dir"].Value;
                var orMore =
                    direction.StartsWith("greater", StringComparison.OrdinalIgnoreCase)
                    || direction.StartsWith("more", StringComparison.OrdinalIgnoreCase);

                var what = number.Groups["what"].Value.ToLowerInvariant();

                return (state, abilities, obj, announced) =>
                {
                    var computed = Characteristics.Of(state, abilities, obj);
                    int? has = what switch
                    {
                        "power" => computed.Power,
                        "toughness" => computed.Toughness,
                        _ => obj.Card.Cmc,
                    };

                    var limit = variable ? announced : wanted;

                    return has is { } value && (orMore ? value >= limit : value <= limit);
                };
            }

            var keyword = KeywordQualifier().Match(qualifier);
            if (keyword.Success && Keywords(keyword.Groups["kw"].Value) is { } wantedKeyword)
            {
                var negated = keyword.Groups["not"].Success;
                return (state, abilities, obj, _) =>
                    Characteristics.Of(state, abilities, obj).Has(wantedKeyword) != negated;
            }

            return null;
        }

        /// <summary>
        /// Whose permanent it has to be (CR 109.5).
        /// </summary>
        /// <remarks>
        /// Reads the <em>computed</em> controller. Control-changing effects are layer 2
        /// (CR 613.1b), so a creature that has been stolen is controlled by its new controller
        /// for every purpose — including whether a sweeper aimed at "creatures your opponents
        /// control" finds it, and whether you may target it with something that says "you
        /// control". The stored controller is what it was before anything happened.
        /// </remarks>
        private static Func<GameState, IAbilitySource, GameObject, Guid, bool>? OwnerFilter(
            string owner) => owner switch
            {
                "" => (_, _, _, _) => true,
                "you control" => (state, abilities, obj, controller) =>
                    Characteristics.Of(state, abilities, obj).ControllerId == controller,
                "you don't control" or "an opponent controls" or "your opponents control"
                    or "another player controls" =>
                    (state, abilities, obj, controller) =>
                        Characteristics.Of(state, abilities, obj).ControllerId != controller,
                _ => null,
            };

        [GeneratedRegex(@"^up to one\s+", RegexOptions.IgnoreCase)]
        private static partial Regex UpToOnePrefix();

        /// <remarks>
        /// Both spellings: a card says "another target creature" on its own and "up to one
        /// *other* target creature" once a count is in front of it. They mean the same thing -
        /// not this one - and reading only the first left the second family unread, with the
        /// "up to one" already stripped and nothing left that the grammar recognised.
        /// </remarks>
        [GeneratedRegex(@"^(another|other) target\s+", RegexOptions.IgnoreCase)]
        private static partial Regex AnotherPrefix();

        [GeneratedRegex(
            @"\s(?<q>with(out)? .+?)"
                + @"(\s+you control|\s+you don't control|\s+an opponent controls"
                + @"|\s+your opponents control|\s+another player controls"
                + @"|\s+defending player controls)?$",
            RegexOptions.IgnoreCase)]
        private static partial Regex QualifierPhrase();

        /// <remarks>
        /// "X" sits in the number alternation rather than in a pattern of its own, because it is
        /// the same clause in the same place with the number left to the cast: 27 corpus cards
        /// print one, and every one of them writes it exactly where a digit would go. It is
        /// matched case-sensitively even though the rest of this pattern is not — a lowercase x
        /// is a letter in a word, and the surrounding alternatives are all spelled out — so the
        /// literal turns the option off around itself rather than relying on the group.
        /// </remarks>
        [GeneratedRegex(
            @"^with (?<what>power|toughness|mana value) "
                + @"(?<n>\d+|(?-i:X)|one|two|three|four|five|six|seven|eight|nine|ten) "
                + @"or (?<dir>greater|more|less|fewer)$",
            RegexOptions.IgnoreCase)]
        private static partial Regex NumberQualifier();

        /// <remarks>
        /// The noun is written out as "creature" and the tail is the closed list of owner
        /// clauses rather than anything the grammar might read, and that narrowness is the whole
        /// point. Damage is marked on a permanent (CR 120.3) — except on a planeswalker, where
        /// CR 306.7 removes that many loyalty counters instead and nothing in the state remembers
        /// it was ever dealt. So "target creature or planeswalker … that was dealt damage this
        /// turn" and "any target that was dealt damage this turn" are refused *by construction*:
        /// compiled, they would be cards that can never choose the damaged planeswalker they
        /// print, and nothing about them would look unfinished. Three corpus cards, left unread.
        /// <para>
        /// "That <em>dealt</em> damage this turn" is the opposite sentence — the creature that
        /// dealt it, not the one dealt to — and two corpus cards print it, so the verb is
        /// required rather than optional.
        /// </para>
        /// </remarks>
        [GeneratedRegex(
            @"^(?<rest>target creature(\s+you control|\s+you don't control"
                + @"|\s+an opponent controls|\s+your opponents control"
                + @"|\s+another player controls|\s+defending player controls)?)"
                + @" that (was|has been) dealt damage this turn$",
            RegexOptions.IgnoreCase)]
        private static partial Regex DealtDamageClause();

        [GeneratedRegex(
            @"^with an? ((?<kind>[+-]\d/[+-]\d) )?counter on it$", RegexOptions.IgnoreCase)]
        private static partial Regex CounterQualifier();

        [GeneratedRegex(@"^with(?<not>out)? (?<kw>[a-z ]+)$", RegexOptions.IgnoreCase)]
        private static partial Regex KeywordQualifier();

        [GeneratedRegex(@"\s+or\s+", RegexOptions.IgnoreCase)]
        private static partial Regex EitherType();

        [GeneratedRegex(@"^any target$", RegexOptions.IgnoreCase)]
        private static partial Regex AnyTargetPhrase();

        [GeneratedRegex(
            @"^target (?<left>[a-z0-9'’ ]+?)(?:,\s*or\s+|,\s*|\s+or\s+)"
            + @"(?<right>[a-z0-9'’, ]+)$",
        RegexOptions.IgnoreCase)]
        private static partial Regex EitherPhrase();

        /// <remarks>
        /// Not case-insensitive: a capital letter is the only thing separating "target creature"
        /// from "target Goblin", and the cards rely on exactly that distinction. The literal words
        /// therefore spell out both cases where a sentence can begin with them.
        /// </remarks>
        /// <remarks>
        /// Separate from the permanent phrase because the two disagree about what "creature"
        /// means: there it is the noun and here it is an adjective on "spell". One pattern trying
        /// to be both would have to guess, and guessing wrong on a counterspell's target is how a
        /// card ends up able to counter anything.
        /// </remarks>
        [GeneratedRegex(
            @"^([Aa]nother |[Uu]p to one )?[Tt]arget (?<adj>[A-Za-z][A-Za-z ]*?) spell$",
            RegexOptions.None)]
        private static partial Regex TypedSpellPhrase();

        [GeneratedRegex(
            @"^([Aa]nother |[Uu]p to one )?[Tt]arget "
                // Case-insensitive for this group only, and that is load-bearing: the rest of
                // the pattern uses a capital to tell a creature type from an ordinary noun, but
                // an adjective at the start of a sentence is capitalised by position and nothing
                // else. "White creatures get +2/+0" read White as a tribe and pumped nothing.
                //
                // The bare colours sit beside the negations they were missing from. A card that
                // says "nonwhite" has to match before one that says "white", so the longer
                // alternative is written first - alternation is ordered.
                // Repeatable, because a card may name more than one: "nonartifact, nonblack
                // creature" is two adjectives that both have to hold, and a single-shot group
                // could only ever read one of them. .NET keeps every capture of a repeated
                // group, so the reader below ands them together rather than taking the last.
                + @"(?:(?<adj>(?i:attacking or blocking|attacking|blocking|unblocked|blocked"
                // "A red or green creature you control" - two colours, either of which will do.
                // First in the alternation because it has to beat the bare colour that starts it:
                // "red" alone matches, and then " or green creature" is left for a noun that has
                // no room for it, which is how all 17 of these were lost.
                + @"|(?:white|blue|black|red|green) or (?:white|blue|black|red|green)"
                + @"|tapped|untapped|modified|enchanted|snow"
                + @"|nonland|nonbasic|basic"
                + @"|nonwhite|nonblue|nonblack|nonred|nongreen"
                + @"|white|blue|black|red|green"
                + @"|nonlegendary|legendary|nontoken|token|multicolored|monocolored|colorless"
                + @"|face-down|noncreature|nonartifact|nonenchantment|historic"
                // "Non-Human creature", "non-Wall creature" - a negated creature type, written
                // with the hyphen the negated card types do without. Last in the alternation so
                // no fixed word it could swallow is reached by it first.
                + @"|non-[a-z]+)),?\s+)*"
                // Every ordinary noun is case-insensitive; only the tribe fallback at the end
                // keeps its capital. A sentence-initial capital is position, not meaning, and
                // reading it as meaning cost "Creature tokens you control" its whole grammar:
                // "Creature token" missed the lowercase compound, fell through to the tribe
                // alternative, matched "Creature" alone and left " token" with nowhere to go.
                //
                // The bare types survived that only by accident - the tribe branch caught them
                // and the noun is lowercased before it is looked up, so "Creature" came out as
                // the type anyway. Anything with a second word in it had no such luck.
                + @"(?<noun>(?i:(artifact|creature|enchantment|land|planeswalker|battle)"
                + @" or (artifact|creature|enchantment|land|planeswalker|battle)"
                // The token compounds come first, because the alternation is ordered: with
                // "creature" in front of "creature token", the bare word wins and the rest of the
                // phrase is left over for a pattern that has no room for it.
                + @"|artifact creature token|creature token|artifact token|enchantment token"
                + @"|artifact creature|enchantment creature|creature|permanent|artifact"
                + @"|enchantment|land|planeswalker|token|player|opponent|spell)|[A-Z][a-z]+)"
                // "Target Nissa planeswalker", "target Goblin creature" - a subtype with the card
                // type printed after it. Captured rather than discarded, because the word decides
                // which type the subtype belongs to and the subtype table can only guess: a
                // planeswalker type shares no list with a creature type, and 38 cards name one
                // this way while "target Nissa" alone was already read.
                + @"(?<kind>\s+(?i:creature|planeswalker|artifact|enchantment|land|permanent))?"
                + @"(?<own>\s+you control|\s+you don't control|\s+an opponent controls"
                + @"|\s+your opponents control|\s+another player controls"
                + @"|\s+that player controls|\s+defending player controls)?$",
            RegexOptions.None)]
        private static partial Regex TargetPhrase();
    }

    [GeneratedRegex(@",\s+then\s+", RegexOptions.IgnoreCase)]
    private static partial Regex ThenSeparator();

    [GeneratedRegex(@"\s+and\s+", RegexOptions.IgnoreCase)]
    private static partial Regex AndSeparator();

    [GeneratedRegex(@"^then\s+", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingThen();

    /// <remarks>
    /// "X" is a number here like any other. What it comes to is decided when the spell is cast
    /// (CR 601.2b) and read at resolution, so the compiler only has to notice that the card said
    /// X rather than a digit.
    /// </remarks>
    /// <summary>
    /// The characters a counted group may contain (CR 107.3).
    /// </summary>
    /// <remarks>
    /// Three characters wider than the target class <see cref="T"/> — plus, slash and hyphen —
    /// because a counter spells its own name with them, and "for each +1/+1 counter on this
    /// creature" is the single commonest counted group on any card. Widening <see cref="T"/> the
    /// same way is what broke the pump matcher, so the two classes are deliberately different
    /// rather than one shared one: a counted group is always anchored after a literal "for each"
    /// or "the number of" and runs to the end of the sentence, so there is no verb behind it for
    /// a greedy "+2/+2" to eat. A target phrase has a verb behind it, and that is the difference.
    /// <para>
    /// Every position that hands its capture to <see cref="CountingAmount"/> is built from this.
    /// The group classes on the per-each <em>pumps</em> deliberately are not: those go to
    /// <c>Specs.ParseGroup</c> and want a group of permanents, so admitting counter names there
    /// would widen the pattern without widening what can be read.
    /// </para>
    /// </remarks>
    private const string COUNTED = @"[A-Za-z0-9'’~+/ -]";

    /// <summary>
    /// The optional "for each ..." tail that scales an amount by a count of the board.
    /// </summary>
    /// <remarks>
    /// Shared, because it was written into four verbs one at a time and each of the four was a
    /// separate round of work. A verb that reads a number should get this with it.
    /// </remarks>
    private const string FOREACH = @"( for (?<foreach>each " + COUNTED + @"+))?";

    private const string N =
        // The larger words come first because alternation is ordered: with "four" in front of
        // "fourteen", the shorter one matches and leaves "teen" for a pattern with no room for
        // it. The list stops where the cards do - no card counts in words past fifty.
        @"(?<n>\d+|X|thirteen|fourteen|fifteen|twenty|thirty|fifty"
            + @"|a|an|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve"
            + @"|that many|that much)";

    /// <summary>A printed target phrase, read by <see cref="Specs.Parse"/> rather than here.</summary>
    /// <summary>
    /// A noun phrase: the words naming what an effect acts on.
    /// </summary>
    /// <remarks>
    /// Digits are in the class so that a qualifier can carry a number - "target creature with
    /// power 3 or greater" is one noun phrase, not a phrase and a number. Plus and slash are
    /// deliberately <em>not</em>, which looks like an omission and is not: with them in, the pump
    /// matcher greedily reads "target creature gets +2/+2" as a target called
    /// "creature gets +2/+2". The cost is that a phrase naming a counter by its symbol stays
    /// unread at sentence level, which is the cheaper of the two.
    /// </remarks>
    // The comma is in the class because a target phrase may contain one - "target nonartifact,
    // nonblack creature" is a single target with two adjectives, not two clauses. Every pattern
    // built from this fragment anchors the phrase at a fixed literal or the end of the sentence,
    // and the target grammar refuses anything it cannot read, so a comma that does separate
    // clauses still loses the card rather than compiling into the wrong target.
    private const string T = @"(?<t>[A-Za-z0-9'’ ,-]+)";

    /// <remarks>
    /// The hyphen in the target class is load-bearing: "target non-Dragon creature an opponent
    /// controls" is a printed target phrase, and without it the named form of this sentence was
    /// narrower than the pronoun form beside it - Glorybringer read only because the pronoun
    /// reader used the wider class. Two spellings of one sentence should not read differently.
    /// </remarks>
    [GeneratedRegex(
        @"^~ deals " + N + @" damage to (?<t>[A-Za-z0-9'’ -]+?)"
            + @"( for (?<foreach>each " + COUNTED + @"+))?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex DealsDamageLine();


    /// <remarks>
    /// The head is anything at all - every verb the parser knows can carry an X - so it is matched
    /// lazily up to the clause rather than described. The clause itself is fixed wording: cards
    /// say "where X is the number of" and nothing else, and admitting a looser shape would take
    /// "where X is the number of cards in your hand" as a permanent count and be silently wrong.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<head>.+?), where X is the number of (?<group>" + COUNTED + @"+?)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex VariableIsCountLine();

    /// <summary>
    /// "…, where X is the number of times ~ has mutated" (CR 702.140).
    /// </summary>
    /// <remarks>
    /// Not a count of anything on the battlefield, so it cannot go through the group grammar
    /// above: it is a fact about one permanent's own history, the way "where X is its power" is a
    /// fact about its own size. Four corpus cards read it, and each of them would otherwise take
    /// X to mean a number the caster chose - which on a spell nobody casts for X is zero, and
    /// zero is the number that makes every one of them do nothing at all.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<head>.+?), where X is the number of times ~ has mutated$",
        RegexOptions.IgnoreCase)]
    private static partial Regex VariableIsMutationsLine();

    /// <summary>
    /// "…, where X is its power" — X measured on one permanent rather than counted (CR 107.3).
    /// </summary>
    /// <remarks>
    /// The stat list is closed on purpose. "Its mana value" and "its toughness" are the same
    /// question about the same object and cost nothing extra, but "its power minus 1" and "its
    /// mana value minus 4" are printed too, and an arithmetic tail admitted here would be read as
    /// the bare stat and give a card a bigger number than it prints — so the pattern ends at the
    /// word and those stay unread.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<head>.+?), where X is its (?<stat>power|toughness|mana value)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex VariableIsStatLine();

    /// <remarks>
    /// The counted group is written as "the number of X" and handed to the same group grammar as
    /// "each X", so the word "each" is put back rather than the grammar being taught a second
    /// spelling. One vocabulary, two ways of saying it on a card.
    /// </remarks>
    [GeneratedRegex(
        @"^~ deals damage equal to (?<mult>twice |three times )?the number of (?<foreach>" + COUNTED + @"+?) "
            + @"to (?<t>[A-Za-z0-9'’ ]+?)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex DamageEqualToCountLine();

    /// <summary>The same count with the target named first (CR 107.3).</summary>
    [GeneratedRegex(
        @"^~ deals damage to (?<t>[A-Za-z0-9'’ ]+?) "
            + @"equal to (?<mult>twice |three times )?the number of (?<foreach>" + COUNTED + @"+?)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex DamageToTargetEqualToCountLine();

    [GeneratedRegex(@"^destroy " + T + @"$", RegexOptions.IgnoreCase)]
    private static partial Regex DestroyLine();

    /// <summary>A group phrase: no "target", and usually plural.</summary>
    private const string G = @"(?<t>(all |each |every )?[A-Za-z0-9'’ ]+)";

    /// <remarks>
    /// One pattern for every sweeper verb. The group phrase has to open with "all", "each" or
    /// "every" — without that requirement "destroy target creature" would reach here first and be
    /// read as an untargeted effect, which is a different card entirely.
    /// </remarks>
    [GeneratedRegex(
        // The comma is in the class for a list of groups - "destroy all artifacts, creatures,
        // and enchantments" - and reaches no further than that: a sweeper's own sentence has
        // already been cut from its neighbours before it arrives here, and the group grammar
        // refuses a comma'd phrase whose parts are not each a plural noun.
        @"^(?<verb>destroy|exile|tap|untap|return) (?<t>(all|each|every) [A-Za-z0-9,'’ ]+?)"
            + @"( to (its|their) owners?'? hands?)?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ToEachLine();

    [GeneratedRegex(
        // "It deals" as well as "~ deals": a trigger has already named the source, and the
        // sentence that follows says "it". The player-scope twin of this reader was widened the
        // same way and for the same reason - the two halves of one grammar had drifted apart.
        @"^(~|it) deals " + N + @" damage to (?<t>(all|each|every) [A-Za-z0-9'’ ]+)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex DamageEachPermanentLine();

    [GeneratedRegex(
        @"^~ deals " + N + @" damage to (?<t>(all|each|every) [A-Za-z'’]+( [A-Za-z'’]+)*?) and "
            + @"(?<rest>(all|each|every) [A-Za-z0-9'’ ]+)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex DamageEachBothLine();

    /// <remarks>
    /// The subject is a pronoun about as often as it is a noun - "it doesn't untap", "they don't
    /// untap" - because the sentence before it has just named the thing. Whether it is plural is
    /// the one part that changes what happens, so that is the only group.
    /// </remarks>
    [GeneratedRegex(
        @"^((that|the|this) (creature|permanent|land|artifact)|the chosen creature|it"
            + @"|(?<many>(those|these) (creatures|permanents|lands|artifacts)|they))"
            + @" (doesn't|don't) untap during "
            + @"(its controller's|their controller's|your) next untap step$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SkipUntapLine();

    [GeneratedRegex(
        @"^~ doesn't untap during (your|its controller's) next untap step$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SkipUntapSourceLine();

    [GeneratedRegex(
        @"^[Rr]eturn (~|this card) from your graveyard to the battlefield(?<tapped> tapped)?$",
        RegexOptions.None)]
    private static partial Regex ReturnSelfFromGraveyardLine();

    [GeneratedRegex(
        @"^if that spell is countered this way, exile it "
            + @"instead of putting it into its owner's graveyard$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ExileCounteredLine();

    /// <remarks>
    /// "A Mount creature card" is an article, a filter and a noun. Only the middle is the filter;
    /// the other two are grammar, and they are stripped in one place so every list reads alike.
    /// </remarks>
    [GeneratedRegex(@"^(an?|the)\s+|\s+cards?$", RegexOptions.IgnoreCase)]
    private static partial Regex ArticleAndCard();

    [GeneratedRegex(@"^spend this mana only to (?<what>.+?)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex SpendOnlyLine();

    /// <remarks>
    /// Only the "non-" form carries a type: "can't be spent to cast spells" names none at all and
    /// forbids casting outright, which is a different and simpler restriction.
    /// </remarks>
    [GeneratedRegex(
        @"^this mana can't be spent to cast (an? )?(non-?(?<types>[a-z]+) )?spells?\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex CantSpendLine();

    [GeneratedRegex(@"\s+or(\s+to)?\s+", RegexOptions.IgnoreCase)]
    private static partial Regex SpendClauseSeparator();

    [GeneratedRegex(@"^\s*(and|or|,)\s*|\s+(and|or)\s+|,\s*", RegexOptions.IgnoreCase)]
    private static partial Regex RestrictionTypeSeparator();

    /// <remarks>
    /// "Cast spells" with nothing in between is every spell, which is why the type words are
    /// optional rather than a second pattern.
    /// </remarks>
    [GeneratedRegex(
        @"^cast (an?\s+)?(?<types>[A-Za-z, ]*?)\s*spells?$", RegexOptions.IgnoreCase)]
    private static partial Regex SpendCastClause();

    [GeneratedRegex(
        @"^activate (an ability|abilities)( of (an?\s+)?(?<types>[A-Za-z, ]*?)"
            + @"(\s+source)?s?)?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SpendActivateClause();

    [GeneratedRegex(
        @"^(?<t>target [a-z ]+?) doesn't untap during "
            + @"(its controller's|your) next untap step$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SkipUntapTargetLine();

    /// <remarks>
    /// A bare "shuffle" is the modern wording and means your library (CR 701.23a); "shuffle your
    /// library" is the same instruction as it used to be printed. Requiring the object was one
    /// word standing between the compiler and 189 cards, every one of which it could already do
    /// the work for.
    /// </remarks>
    [GeneratedRegex(
        @"^(if you search(ed)? your library this way, )?"
            + @"shuffle( your library| (?<yard>your graveyard) into your library)?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ShuffleLine();

    /// <summary>The same shuffle said of somebody the sentence names (CR 701.24a).</summary>
    /// <remarks>
    /// Separate from <see cref="ShuffleLine"/> rather than an optional subject on it, because the
    /// two differ in more than the subject: this one has to reach a target and the bare form is
    /// the tail of a tutor, where a stray subject would be a different instruction.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<who>each player|target player) shuffles "
            + @"(their library|(?<yard>their graveyard) into their library)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ShuffleWhoLine();

    [GeneratedRegex(
        @"^(then )?shuffle and put (it|that card) on top( of your library)?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SearchToTopLine();

    [GeneratedRegex(
        @"^put " + N + @" (?<kind>[+-]\d/[+-]\d) counters? on "
            + @"(?<t>(all|each|every) (?<other>other )?[A-Za-z0-9'\u2019 ]+?)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex CounterOnEachLine();

    [GeneratedRegex(@"\bother\s+", RegexOptions.IgnoreCase)]
    private static partial Regex OtherPrefix();

    [GeneratedRegex(
        @"^(it|they|that creature|those creatures) can't be regenerated$",
        RegexOptions.IgnoreCase)]
    private static partial Regex NoRegenerationLine();

    [GeneratedRegex(
        @"^remove " + N + @" (?<kind>[+-]\d/[+-]\d|[a-z]+) counters? from (~|it)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex RemoveCounterLine();

    [GeneratedRegex(
        @"^sacrifice (~|it) unless you pay (?<cost>(\{[^}]+\})+)$", RegexOptions.IgnoreCase)]
    private static partial Regex SacrificeUnlessPayLine();

    /// <remarks>
    /// The subjects are shared between the verbs deliberately: whatever "its controller" may
    /// refer to for a life change, it refers to the same thing for a draw, and two patterns
    /// would eventually disagree about that.
    /// </remarks>
    [GeneratedRegex(
        @"^(its|that spell's|that creature's|that permanent's) controller "
            + @"((?<verb>loses|gains) " + N + @" life"
            + @"|(?<draws>draws) " + N + @" cards?"
            + @"|(?<mills>mills) " + N + @" cards?)" + FOREACH + @"$",
        RegexOptions.IgnoreCase)]
    private static partial Regex TargetControllerLine();

    /// <remarks>
    /// Only the bare sentence. "Remove all attacking creatures from combat and untap them" says
    /// the same words about a set this parser never chose, and reading it here would untap
    /// whatever the last clause happened to leave behind.
    /// </remarks>
    [GeneratedRegex(@"^untap them$", RegexOptions.IgnoreCase)]
    private static partial Regex UntapThemLine();

    /// <remarks>
    /// The two comparisons are the whole list, and both are printed with "other" every time —
    /// 10 radiance cards and 18 same-name ones, counted rather than assumed. "Shares a card type
    /// with it" is deliberately absent: its 8 cards name a card to cast or a second target to
    /// exchange, and not one of them is this shape.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<head>.*?)target (?<t>[a-z0-9'’ ]+?)"
            + @" and (?<det>each|all) other (?<g>[a-z0-9'’ ]+?)"
            + @" (?:(?<colour>that shares a color with it)"
            + @"|with the same name as that [a-z]+)"
            + @"(?<tail>.*)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex PeerGroupLine();

    [GeneratedRegex(@"^\s*(?<verb>get|gain)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PluralVerbTail();

    [GeneratedRegex(@"\bcontrols?\b", RegexOptions.IgnoreCase)]
    private static partial Regex GroupOwnerClause();

    /// <remarks>
    /// "You may attach ~ to it" is deliberately not read here. Those cards say it of a creature
    /// the <em>trigger</em> named — "whenever a Warrior creature enters, you may attach ~ to
    /// it" — which is a different referent this parser has no way to reach, and eleven cards
    /// print it. Reading them here would attach the Equipment to whatever the last effect
    /// happened to make.
    /// </remarks>
    [GeneratedRegex(@"^attach ~ to it$", RegexOptions.IgnoreCase)]
    private static partial Regex AttachSourceToItLine();

    [GeneratedRegex(
        @"^prevent all combat damage that would be dealt this turn$", RegexOptions.IgnoreCase)]
    private static partial Regex FogLine();

    [GeneratedRegex(@"^each (player|opponent)$", RegexOptions.IgnoreCase)]
    private static partial Regex PlayerWords();

    [GeneratedRegex(
        @"^((?<t>(all|each|every) [A-Za-z0-9'’ ]+?) gets"
            + @"|" + G + @" get)"
            + @" (?<p>[+-]\d+)/(?<tough>[+-]\d+) until end of turn$",
        RegexOptions.IgnoreCase)]
    private static partial Regex MassPumpLine();

    /// <remarks>
    /// Two branches for the same sentence, exactly as the mass pump has: the singular "gains"
    /// requires a group word, because without one it is indistinguishable from "target creature
    /// gains flying" - a different card entirely, read a few matchers earlier. The plural "gain"
    /// needs no group word, because "creatures you control" is already a group by its noun.
    /// <para>
    /// The pump matcher had both branches and this one had only the first, so "creatures you
    /// control get +1/+1 until end of turn" was read and **"creatures you control gain vigilance
    /// until end of turn" was not** - 192 lines of the corpus, on the same shape, told apart by
    /// nothing but which verb they used.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"^((?<t>(all|each|every) [A-Za-z0-9'’ ]+?) gains"
            + @"|" + G + @" gain)"
            + @" (?<kw>[a-z ,]+?) until end of turn$",
        RegexOptions.IgnoreCase)]
    private static partial Regex MassGrantLine();

    /// <remarks>
    /// "Creatures you control get +1/+1 and gain vigilance until end of turn" - one sentence, two
    /// continuous effects, and 112 lines of it. Read as one matcher rather than by splitting on
    /// "and", because the conjunction splitter would hand the second half "gain vigilance until
    /// end of turn" with no subject at all.
    /// </remarks>
    [GeneratedRegex(
        @"^((?<t>(all|each|every) [A-Za-z0-9'’ ]+?) gets"
            + @"|" + G + @" get)"
            + @" (?<p>[+-]\d+)/(?<tough>[+-]\d+) and gains? (?<kw>[a-z ,]+?) until end of turn$",
        RegexOptions.IgnoreCase)]
    private static partial Regex MassPumpAndGrantLine();

    [GeneratedRegex(@"^exile " + T + @"$", RegexOptions.IgnoreCase)]
    private static partial Regex ExileLine();

    /// <remarks>
    /// "Exile" is spelled out as a destination as well as a verb, because "exile target creature
    /// card from your graveyard" names where it goes by the verb alone.
    /// <para>
    /// The target half is matched loosely and handed to <see cref="Specs.Parse"/>, which is the
    /// authority on what a graveyard target may say. It used to spell the phrase out again here,
    /// and the copies drifted the moment the grammar learned mana-value caps: the spec understood
    /// "with mana value 3 or less" and this pattern silently did not, so every card that said it
    /// went unread. The call site already refuses anything that does not parse to a graveyard
    /// target, so being permissive here costs nothing.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        // The count and the "another" come before the word "target", so anchoring on that word
        // alone shut out phrases the target grammar has understood all along - it strips both
        // prefixes itself and parses what is left.
        @"^(return|put|exile) (?<t>(another |up to one |up to two |up to three )?target .+? graveyard)"
            + @"( (?<where>to (your|its owner's) hand|onto the battlefield|to the battlefield))?"
            + @"(?<mine> under your control| under its owner's control)?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex FromGraveyardLine();

    [GeneratedRegex(
        @"^return " + T + @" to (its owner's|their owner's) hand$", RegexOptions.IgnoreCase)]
    private static partial Regex ReturnToHandLine();

    [GeneratedRegex(
        @"^look at (?<t>target) (?<kind>player|opponent)'s hand\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex LookAtHandLine();

    [GeneratedRegex(
        @"^((?<t>target (player|opponent))|" + W + @") reveals? (their|your) hand$",
        RegexOptions.IgnoreCase)]
    private static partial Regex RevealHandLine();

    [GeneratedRegex(
        @"^put " + T + @" on (?<where>top|the bottom) of "
            + @"(?<whose>its owner's|their owner's|your) library$",
        RegexOptions.IgnoreCase)]
    private static partial Regex OntoLibraryLine();

    [GeneratedRegex(@"^counter " + T + @"$", RegexOptions.IgnoreCase)]
    private static partial Regex CounterSpellLine();

    [GeneratedRegex(@"^(?<verb>tap|untap) " + T + @"$", RegexOptions.IgnoreCase)]
    private static partial Regex TapOrUntapLine();

    /// <remarks>
    /// The subject is optional on all three of these, and that had been inconsistent: draw
    /// accepted only the imperative ("draw a card") while the life lines accepted only the
    /// explicit subject ("you gain 2 life"). Cards use both spellings freely, and the two
    /// conjunction splitters made it matter — "you draw a card and you lose 1 life" divides into
    /// halves that each carry a subject, while "Draw a card, then discard a card" divides into
    /// halves that carry none.
    /// </remarks>
    /// <remarks>
    /// "An additional card" is a plain draw with a word saying what it is in addition to. The
    /// word carries no count of its own — the "additional" is relative to the draw step the
    /// trigger already fires in — so reading it as the number beside it is the whole of it.
    /// </remarks>
    [GeneratedRegex(
        @"^" + WOPT + @"draws? " + N + @" (additional )?cards?$", RegexOptions.IgnoreCase)]
    private static partial Regex DrawCardsLine();

    /// <remarks>
    /// These three carry <see cref="FOREACH"/> for the same reason the imperative forms beside
    /// them do. "Each opponent discards a card for each creature you control" read and "target
    /// player discards a card for each Swamp you control" did not, which is not a fact about
    /// discarding — it is a fact about which of the two readers happened to be written after the
    /// count tail existed. A verb that takes a number takes a counted one.
    /// </remarks>
    [GeneratedRegex(
        @"^" + T + @" draws " + N + @" (additional )?cards?" + FOREACH + @"$",
        RegexOptions.IgnoreCase)]
    private static partial Regex TargetDrawsLine();

    [GeneratedRegex(
        @"^" + T + @" (?<verb>gains|loses) " + N + @" life" + FOREACH + @"$",
        RegexOptions.IgnoreCase)]
    private static partial Regex TargetLifeLine();

    [GeneratedRegex(
        @"^" + T + @" discards " + N + @" cards?" + FOREACH + @"$", RegexOptions.IgnoreCase)]
    private static partial Regex TargetDiscardsLine();

    /// <summary>
    /// "you" / "each opponent" / "each player" — the group an effect names (CR 109.5).
    /// </summary>
    /// <remarks>
    /// Deliberately excludes "target opponent". A targeted effect and an each-opponent effect are
    /// different cards: the first can be stopped by hexproof and fizzles if its target becomes
    /// illegal, the second cannot and does not. Reading one as the other would be wrong in a way
    /// that only shows up at a four-player table, so those phrases stay unread until the
    /// targeting grammar handles them.
    /// </remarks>
    /// <remarks>
    /// "That player" and "defending player" name somebody the game has already picked out rather
    /// than a group, and they belong here for the same reason "each opponent" does: every verb
    /// that takes a player takes any of them, so widening the word list widens every sentence at
    /// once instead of adding a matcher per verb.
    /// </remarks>
    private const string W = @"(?<who>you|" + WhoElse + @")";

    /// <summary>
    /// Every player a sentence can name except the one whose spell or ability it is.
    /// </summary>
    /// <remarks>
    /// "Each other player" before "each player": alternation is ordered, and though these two
    /// share no prefix the longer phrasings are kept in front so a later addition that does share
    /// one cannot be swallowed by the shorter neighbour it was written beside.
    /// <para>
    /// "Enchanted player" is the same idea one relation further out: a Curse is an Aura whose
    /// host is a player (CR 303.4b), and every verb below already takes a player, so the word
    /// belongs in the list rather than in a matcher of its own.
    /// </para>
    /// <para>
    /// "The subject's controller" is not printed on any card: it is what "its controller" and
    /// "that creature's controller" are rewritten to once the guard in TryOne has decided they
    /// name the triggering event's object rather than a target (CR 603.2).
    /// </para>
    /// <para>
    /// Held apart from <see cref="W"/> rather than spelled out twice, so a verb whose imperative
    /// form belongs to another matcher can take every subject except "you" without a second copy
    /// of the list. One vocabulary written down twice is how the two come to disagree, and this
    /// one already had: the edict below knew two of these seven.
    /// </para>
    /// </remarks>
    private const string WhoElse =
        @"each opponent|each other player|each player|that player|defending player"
            + @"|enchanted player|the subject's controller";

    /// <summary>The same group with "you" left out.</summary>
    private const string WThem = @"(?<who>" + WhoElse + @")";

    /// <summary>
    /// The same group, optional — because half of these sentences are imperative.
    /// </summary>
    /// <remarks>
    /// A card prints "Draw a card, then discard a card", not "…then you discard a card". The
    /// subject is the controller and is simply left out (CR 608.2), so the grammar has to accept
    /// its absence rather than treating it as a different sentence.
    /// </remarks>
    private const string WOPT = @"(?:" + W + @"\s+)?";

    [GeneratedRegex(
        @"^" + WOPT + @"discards? " + N + @" cards?(?<random> at random)?" + FOREACH + @"$",
        RegexOptions.IgnoreCase)]
    private static partial Regex DiscardLine();

    /// <remarks>
    /// "Discard all the cards in your hand" is the long spelling of the same instruction and the
    /// wording every card in the Tolarian Winds family uses. Reading it here rather than as its
    /// own matcher is what keeps one meaning behind one effect.
    /// </remarks>
    [GeneratedRegex(
        @"^" + WOPT + @"discards? (all the cards in (your|their) hand|(your|their) hand)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex DiscardHandLine();

    /// <summary>"…, then draw that many cards" — only ever after a whole hand has gone.</summary>
    [GeneratedRegex(@"^" + WOPT + @"draws? that many cards$", RegexOptions.IgnoreCase)]
    private static partial Regex DrawThatManyLine();

    [GeneratedRegex(
        @"^(?<who>that player|each player|each opponent) draws? " + N
            + @" (additional )?cards?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ScopedDrawLine();

    [GeneratedRegex(
        @"^" + WOPT + @"mills? " + N + @" cards?" + FOREACH + @"$", RegexOptions.IgnoreCase)]
    private static partial Regex MillLine();

    [GeneratedRegex(
        @"^(?<t>target (player|opponent)) mills " + N + @" cards?" + FOREACH + @"$",
        RegexOptions.IgnoreCase)]
    private static partial Regex TargetMillLine();

    /// <remarks>
    /// The pronoun is a group of its own so the reader can tell the two apart. "~" always means
    /// the permanent with the ability; "it" means it only where nothing else has been named, and
    /// the reader refuses the rest rather than pointing the damage at the source anyway.
    /// </remarks>
    [GeneratedRegex(
        @"^(?:~|(?<pronoun>it)) deals damage equal to its power to (?<t>[a-z0-9'’ ]+)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex DamageByPowerLine();

    /// <remarks>
    /// The pronoun is a third shape and not a spelling of the first two. Twenty corpus cards
    /// write the fight as its own sentence — "Target creature you control gets +1/+2 until end of
    /// turn. It fights target creature you don't control." — so the fighter has already been
    /// chosen by the sentence in front of it and this one names nobody.
    /// </remarks>
    [GeneratedRegex(
        @"^(~|(?<pronoun>it|that creature)|(?<mine>target [a-z0-9'’ ]+)) "
            + @"fights (?<theirs>(?:up to one )?target [a-z0-9'’ ]+)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex FightLine();

    [GeneratedRegex(
        @"^exile (?<t>target (player|opponent))'s graveyard$", RegexOptions.IgnoreCase)]
    private static partial Regex ExileGraveyardLine();

    [GeneratedRegex(
        @"^(?<who>you|each opponent|each player) puts? the top " + N
            + @" cards? of (your|their) library into (your|their) graveyard$",
        RegexOptions.IgnoreCase)]
    private static partial Regex PutTopIntoGraveyardLine();

    /// <remarks>
    /// Distinct from the tap-or-untap verb pattern beside it, which reads "tap target creature"
    /// and "untap target creature" as one shape with two verbs. This is the card that offers the
    /// player both.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<t>target [a-z0-9'’ ]+) gets (?<p>[+-]\d+)/(?<tough>[+-]\d+) until end of turn "
            + @"for (?<group>each [A-Za-z0-9'’ ]+)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex PerEachPumpLine();

    /// <summary>"If it's paired with a creature, that creature also gets +2/+2 until end of turn."</summary>
    [GeneratedRegex(
        @"^if it('s| is) paired with a creature, that creature also gets "
            + @"(?<p>[+-]\d+)/(?<tough>[+-]\d+) until end of turn$",
        RegexOptions.IgnoreCase)]
    private static partial Regex PairedAlsoPumpLine();

    /// <remarks>
    /// The dealer is optional because it is sometimes the card itself - "~ deals damage equal to
    /// its power to target creature" - and sometimes a target of its own. "It" and "that
    /// creature" mean the source in the tail of a trigger, which is the same thing.
    /// </remarks>
    [GeneratedRegex(
        @"^((?<mine>[Tt]arget [a-z’' ]+?)|~|[Ii]t|[Tt]hat creature) "
            + @"deals damage equal to its power to (?<theirs>target [a-z’' ]+?)\.?$",
        RegexOptions.None)]
    private static partial Regex BiteLine();

    /// <summary>"Target creature deals damage to itself equal to its power" (CR 701.12a).</summary>
    [GeneratedRegex(
        @"^(?<t>[Tt]arget [a-z’' ]+?) deals damage to itself equal to its power" + @"$",
        RegexOptions.None)]
    private static partial Regex SelfBiteLine();

    /// <remarks>
    /// Only the counted shapes. "Any number of targets" is the same sentence with no bound, and
    /// a spell whose target list has no length cannot be expressed by a fixed list of specs -
    /// so it is left unread rather than given an arbitrary ceiling, which would be a different
    /// card whenever the ceiling mattered.
    /// </remarks>
    [GeneratedRegex(
        @"^~ deals (?<n>\d+) damage divided as you choose among "
            + @"((one, two, or |one or )(?<most>three|two)|(?<any>any number of)) "
            + @"(?<t>[a-z ]*?)targets?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex DividedDamageLine();

    [GeneratedRegex(
        @"^~ deals (?<a>\d+) damage to (?<t1>.+?) and (?<b>\d+) damage to (?<t2>.+?)\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex TwoPartDamageLine();

    /// <remarks>
    /// "A" and not "target": this is a choice on resolution, so nothing is chosen as the spell is
    /// cast and hexproof has nothing to say about it.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<verb>[Rr]eturn|[Ee]xile) an? ((?<what>[a-zA-Z ]+?) )?card from your graveyard "
            + @"to your hand\.?$",
        RegexOptions.None)]
    private static partial Regex ChooseFromGraveyardLine();

    /// <remarks>
    /// Only "under its owner's control". "Under your control" is a theft wearing the same
    /// sentence, and reading it as a flicker would quietly hand the permanent back to the player
    /// it was taken from.
    /// <para>
    /// The plural pronouns are here for the multi-target rewrite above, not for a plural target.
    /// "Exile up to two target creatures you control, then return those cards ..." is rewritten
    /// to the singular and copied, and that rewrite agrees the *verb* while leaving the pronouns
    /// alone - so the singular sentence it hands down still says "those cards" and "their
    /// owner's". Refusing them left Illusionist's Stratagem and Displace unread beside a reader
    /// that already did exactly what they ask.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"^exile (?<t>.+?), then return (it|that card|those cards) to the battlefield"
            + @"(?<tapped> tapped)? under (its|their) owner's control\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex FlickerLine();

    /// <summary>"Up to two target creatures you control" — the flicker's own plural.</summary>
    [GeneratedRegex(
        @"^up to (?<n>two|three|four) (?<t>target [A-Za-z' ]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex FlickerManyLine();

    /// <summary>"Exile ~, then return it to the battlefield transformed under your control."</summary>
    [GeneratedRegex(
        @"^exile ~, then return (it|that card) to the battlefield(?<transformed> transformed)?"
            + @" under (your|its owner's) control\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex FlickerSelfLine();

    /// <remarks>
    /// The self-referring twin of <see cref="PerEachPumpLine"/>. "It" and "~" both mean the
    /// permanent the ability is on, which is why they share one arm: an attack trigger says
    /// "it", a static-turned-trigger says the card's name, and the effect is the same either way.
    /// </remarks>
    [GeneratedRegex(
        @"^(it|~) gets (?<p>[+-]\d+)/(?<tough>[+-]\d+) until end of turn "
            + @"for (?<group>each [A-Za-z0-9'’ ]+)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ItPumpsPerEach();

    [GeneratedRegex(@"^tap or untap (?<t>target [a-z0-9'’ ]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex TapOrUntapChoiceLine();

    [GeneratedRegex(
        // The trailing "of their choice" says what the effect does anyway - the sacrificing
        // player picks - and the group reader beside this one has always allowed it. This one
        // did not, which is the whole of why the commonest wording of an edict went unread.
        @"^(?<t>target (player|opponent)) sacrifices an? (?<what>[a-z ]+?)"
            + @"( of their choice)?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex TargetSacrificeLine();

    /// <remarks>
    /// "~" is excluded because sacrificing the source itself is its own effect, read later and
    /// needing no choice at all - a card that says "Sacrifice this creature" is not asking.
    /// </remarks>
    [GeneratedRegex(
        @"^sacrifice ((?<another>another)|an?)\s+(?<what>[a-z][a-z ]*?)(?<! you control)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SacrificeOwnLine();

    /// <remarks>
    /// The count is optional because "the top card" is how one card is printed — the number is
    /// implied by the singular, not written.
    /// </remarks>
    [GeneratedRegex(
        @"^" + WOPT + @"exiles? the top (" + N + @" )?cards? of (your|their) library$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ExileTopLine();

    [GeneratedRegex(
        @"^" + W + @" (?<verb>loses?|gains?) " + N + @" life"
            + @"( and you gain (?<gain>\d+|a|an|one|two|three|four|five|six|seven) life)?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex EachLifeLine();

    [GeneratedRegex(
        @"^~ deals " + N + @" damage to " + W + @"$", RegexOptions.IgnoreCase)]
    private static partial Regex DamageEachLine();

    [GeneratedRegex(
        @"^" + W + @" draws? " + N + @" cards?$", RegexOptions.IgnoreCase)]
    private static partial Regex DrawEachLine();

    [GeneratedRegex(
        @"^(?<verb>return|exile) all (?<what>[a-z ]*?)cards? from "
            + @"(?<whose>your) graveyard to (?<where>your hand|the battlefield)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex AllFromGraveyardLine();

    [GeneratedRegex(
        @"^return ~ from your graveyard to your hand$", RegexOptions.IgnoreCase)]
    private static partial Regex ReturnSelfFromGraveyard();

    [GeneratedRegex(@"^sacrifice (it|~)$", RegexOptions.IgnoreCase)]
    private static partial Regex SacrificeSelfLine();

    [GeneratedRegex(@"^exile ~$", RegexOptions.IgnoreCase)]
    private static partial Regex ExileSelfLine();

    [GeneratedRegex(
        @"^if (it"
            + @"|(a|that|the) creature( or planeswalker| an opponent controls)?"
            + @"( dealt damage this way)?"
            + @"|(that|the) permanent) "
            + @"would die this turn, exile it instead$",
        RegexOptions.IgnoreCase)]
    private static partial Regex DiesToExileLine();

    [GeneratedRegex(
        @"^draw a card at the beginning of the next turn's upkeep$", RegexOptions.IgnoreCase)]
    private static partial Regex DelayedDrawLine();

    /// <remarks>
    /// Only the filters the corpus actually uses in quantity — any card, or a nonland one. A
    /// narrower filter this does not read leaves the card unread rather than letting the caster
    /// take something the card put out of reach.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<t>[Tt]arget (opponent|player)) reveals their hand\.\s*"
            + @"[Yy]ou choose (a|an) (?<kind>[a-z ]*?card) from it"
            + @"(\. [Tt]hat player (?<verb>discards) that card"
            + @"|\.? ?(and )?(?<verb2>[Ee]xile) that card)\.?$",
        RegexOptions.None)]
    private static partial Regex RevealAndTakeLine();

    [GeneratedRegex(
        @"^(it's|it is|they're|they are) still (a|an) (land|artifact|creature)s?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex StillALandLine();

    [GeneratedRegex(
        @"^(?<verb>untap|tap|destroy|exile|return) (it|that creature|that permanent|that card)"
            + @"( to (its owner's|their owner's) hand)?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ItLine();

    /// <remarks>
    /// The verb is left open rather than listed, because the sentence it is rewritten into has to
    /// parse for anything to happen — an unknown verb produces a sentence the parser refuses, and
    /// the line goes unread. Listing them here would be a second copy of the verb vocabulary that
    /// could fall behind the real one.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<verb>[a-z]+) (enchanted|equipped) (?<what>creature|permanent|artifact|land)"
            + @"(?<tail>| to (its owner's|their owner's) hand)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex AttachedSubjectLine();

    /// <remarks>
    /// Only the wordings that end in "until end of turn". Without that the pattern would also
    /// swallow the static "enchanted creature gets +1/+1", which is a continuous effect read
    /// elsewhere and would become a one-shot that fires once and never again.
    /// </remarks>
    [GeneratedRegex(
        @"^(enchanted|equipped) (?<what>creature|permanent|artifact|land) "
            + @"(?<tail>(gains|gets) [^.]+ until end of turn)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex AttachedSubjectFirstLine();

    /// <remarks>
    /// The condition may not contain a comma, which is deliberate and is what the trigger's
    /// intervening "if" does too: the comma is the only thing separating the condition from the
    /// instruction, so a condition allowed to swallow one would take the first half of the
    /// instruction with it and then fail on the rest. Conditions listing several things are lost
    /// by that, and refusing them is the cheaper mistake.
    /// </remarks>
    [GeneratedRegex(@"^if (?<cond>[^,]+), (?<effect>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ConditionalSentence();

    /// <summary>The else branch of the sentence above, which is where its condition lives.</summary>
    /// <remarks>
    /// Matched only as the sentence <em>after</em> a conditional one. On its own it names no
    /// question, and reading it alone would compile "otherwise, you lose 1 life" into a card that
    /// always loses the life.
    /// </remarks>
    [GeneratedRegex(@"^otherwise, (?<effect>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex OtherwiseSentence();

    /// <summary>The Curse family's repeat, which names no instruction of its own.</summary>
    /// <remarks>
    /// Matched only as a sentence <em>after</em> one that did something, the way the otherwise
    /// branch above is: "the same" is the sentence before it, and reading it first would be an
    /// instruction with nothing to repeat.
    /// </remarks>
    [GeneratedRegex(
        @"^each opponent attacking that player does the same\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex DoesTheSameSentence();

    [GeneratedRegex(@"^untap ~$", RegexOptions.IgnoreCase)]
    private static partial Regex UntapSelfLine();

    [GeneratedRegex(@"^tap ~$", RegexOptions.IgnoreCase)]
    private static partial Regex TapSelfLine();

    /// <remarks>
    /// The subject is optional because a card writes the theft both ways — "gain control of
    /// target creature" and "you gain control of that creature" — and it is the controller
    /// either way (CR 109.5). A second matcher for the second spelling is how the two would
    /// drift over which phrases they accept.
    /// </remarks>
    [GeneratedRegex(
        @"^(you )?gain control of " + T + @" until end of turn$", RegexOptions.IgnoreCase)]
    private static partial Regex GainControlUntilLine();

    /// <summary>
    /// The "for as long as …" tail, in the three spellings the cards print (CR 611.2b).
    /// </summary>
    /// <remarks>
    /// Shared with the gain-control clause that first needed it, so the four verbs that take this
    /// duration agree about what it says. <see cref="WhileNamed"/> reads the two groups, and a
    /// second copy of the tail would be a second chance for them to disagree.
    /// </remarks>
    private const string HELD =
        @" for as long as (you control (?<until>~|this [a-z]+)"
            + @"|(~|this [a-z]+) remains (?<until2>tapped|on the battlefield))";

    /// <summary>"Gain control of X for as long as you control [this]" (CR 611.2b).</summary>
    [GeneratedRegex(
        @"^gain control of " + T + HELD + "$",
        RegexOptions.IgnoreCase)]
    private static partial Regex GainControlWhileLine();

    /// <summary>
    /// "Target Zombie creature gets +2/+2 and has fear for as long as ~ remains tapped."
    /// </summary>
    /// <remarks>
    /// The bonus and the keyword arrive together on most of the cards that print this shape — the
    /// five Couriers are one card five times — and they are two effects in two layers either way
    /// (CR 613.1f, 613.4c), so the pattern captures both and the reader emits both.
    /// </remarks>
    [GeneratedRegex(
        @"^" + T + @" gets (?<p>[+-]\d+)/(?<tough>[+-]\d+)"
            + @"( and has (?<kw>[a-z ,]+?))?" + HELD + @"\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex PumpWhileLine();

    /// <summary>"Another target permanent gains indestructible for as long as you control ~."</summary>
    [GeneratedRegex(
        @"^" + T + @" gains (?<kw>[a-z ,]+?)" + HELD + @"\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex GainsKeywordWhileLine();

    /// <summary>
    /// "That creature doesn't untap during its controller's untap step for as long as ~ remains
    /// tapped" (CR 502.3, 611.2b).
    /// </summary>
    /// <remarks>
    /// Singular only. The plural spelling — "those creatures don't untap … " — is the tail of a
    /// sentence that tapped several targets at once, and a reader that took it would have to
    /// guess which of them the restriction lands on; the four corpus cards that say it stay in
    /// the work queue rather than being read as one.
    /// </remarks>
    [GeneratedRegex(
        @"^" + T + @" doesn't untap during (its|their) controller's untap step" + HELD + @"\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex DoesNotUntapWhileLine();

    /// <summary>"…gains protection from the color of your choice until end of turn".</summary>
    [GeneratedRegex(
        @"^" + T + @" (gains protection from|(?<becomes>becomes)) "
            + @"the colou?r of your choice until end of turn$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ProtectionFromChosenColourLine();

    /// <summary>"Switch target creature's power and toughness until end of turn" (CR 613.4d).</summary>
    [GeneratedRegex(
        @"^switch " + T + @"'s power and toughness until end of turn$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SwitchPowerToughnessLine();

    /// <summary>"~ becomes the color of your choice until end of turn" — the self form.</summary>
    [GeneratedRegex(
        @"^~ becomes the colou?r of your choice until end of turn$", RegexOptions.IgnoreCase)]
    private static partial Regex SelfBecomesChosenColourLine();

    /// <summary>"~ gains protection from the color of your choice until end of turn".</summary>
    [GeneratedRegex(
        @"^~ gains protection from the colou?r of your choice until end of turn\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SelfProtectionFromChosenColourLine();

    /// <summary>"~ can attack this turn as though it didn't have defender" (CR 702.3b).</summary>
    [GeneratedRegex(
        @"^~ can attack (this turn )?as though it didn't have defender\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SelfMayAttackDespiteDefenderLine();

    /// <summary>The same permission handed to something else.</summary>
    [GeneratedRegex(
        @"^" + T + @" can attack (this turn )?as though it didn't have defender\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex TargetMayAttackDespiteDefenderLine();

    /// <summary>"Put ~ on top of its owner's library" (CR 400.7).</summary>
    [GeneratedRegex(
        @"^put ~ on (the )?(?<where>top|bottom) of its owner's library\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex PutSelfOnLibraryLine();

    /// <summary>The same line about creature types (CR 205.1b).</summary>
    [GeneratedRegex(
        @"^~ becomes the creature type of your choice until end of turn$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SelfBecomesChosenTypeLine();

    /// <summary>"Switch ~'s power and toughness until end of turn" — the self form.</summary>
    [GeneratedRegex(
        @"^switch ~'s power and toughness until end of turn$", RegexOptions.IgnoreCase)]
    private static partial Regex SwitchSelfPowerToughnessLine();

    /// <summary>"All creatures able to block target creature this turn do so" (CR 509.1c).</summary>
    [GeneratedRegex(
        @"^" + T + @" blocks ~ this (turn|combat) if able$", RegexOptions.IgnoreCase)]
    private static partial Regex MustBlockSourceLine();

    [GeneratedRegex(
        @"^" + T + @" can't block ~ this (turn|combat)$", RegexOptions.IgnoreCase)]
    private static partial Regex CantBlockSourceLine();

    [GeneratedRegex(
        @"^suspect (?:(?<t>target [a-z' ]+)|it|that creature)$", RegexOptions.IgnoreCase)]
    private static partial Regex SuspectLine();

    [GeneratedRegex(
        @"^goad (?:(?<t>(another |up to one )?target [a-z' ]+)|it|them|that creature)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex GoadLine();

    /// <remarks>
    /// Only "your library". "That player's" is deliberately refused: the player it names comes
    /// from the trigger, and a batch trigger - "whenever one or more creatures you control deal
    /// combat damage to a player" - has no single subject the engine can answer with. Reading it
    /// compiled one card whose manifest would have found nobody and done nothing, which the
    /// corpus-wide subject guard caught before any test did.
    /// </remarks>
    [GeneratedRegex(@"^discover " + N + @"$", RegexOptions.IgnoreCase)]
    private static partial Regex DiscoverLine();

    [GeneratedRegex(@"^blight " + N + @"$", RegexOptions.IgnoreCase)]
    private static partial Regex BlightLine();

    [GeneratedRegex(@"^the ring tempts you$", RegexOptions.IgnoreCase)]
    private static partial Regex RingTemptsLine();

    [GeneratedRegex(@"^incubate " + N + @"$", RegexOptions.IgnoreCase)]
    private static partial Regex IncubateLine();

    [GeneratedRegex(@"^populate( " + N + @" times)?$", RegexOptions.IgnoreCase)]
    private static partial Regex PopulateLine();

    [GeneratedRegex(@"^manifest dread$", RegexOptions.IgnoreCase)]
    private static partial Regex ManifestDreadLine();

    /// <remarks>
    /// The gendered pronouns are here because the cards use them: a handful of legendary
    /// creatures say "he connives" and mean exactly what "it connives" means everywhere else.
    /// </remarks>
    [GeneratedRegex(
        @"^(?:~|it|he|she|they|that creature|(?<t>(up to one )?target [a-z' ]+))"
            + @" connives?( " + N + @")?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ConniveLine();

    [GeneratedRegex(
        @"^manifest the top (" + N + @" )?cards? of (?<whose>your) library$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ManifestLine();

    /// <summary>"~ endures 2" — counters or a Spirit, whichever its controller picks (CR 701.63a).</summary>
    /// <remarks>
    /// The subject is "~" or "it" and nothing else, because those are the only two spellings the
    /// nine corpus cards use and the effect puts the counters on the <em>source</em>. "That
    /// creature" is deliberately absent: it would mean whatever the trigger was about, which is a
    /// different permanent, and admitting a word no card prints would be a reader that fires only
    /// on the day it is wrong.
    /// <para>
    /// A digit rather than the shared number class, because the token's size is part of the
    /// definition being built and an X has no size until the ability resolves.
    /// </para>
    /// </remarks>
    [GeneratedRegex(@"^(?:~|it) endures? (?<n>\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex EndureLine();

    [GeneratedRegex(
        @"^" + T + @" blocks this (turn|combat) if able$", RegexOptions.IgnoreCase)]
    private static partial Regex MustBlockLine();

    [GeneratedRegex(
        @"^" + T + @" attacks this (turn|combat) if able$", RegexOptions.IgnoreCase)]
    private static partial Regex MustAttackLine();

    [GeneratedRegex(
        @"^all creatures able to block " + T + @" this turn do so$",
        RegexOptions.IgnoreCase)]
    private static partial Regex LureTargetLine();

    /// <summary>"Target creature must be blocked this turn if able" (CR 509.1c).</summary>
    /// <remarks>
    /// The subject is optional because a sentence split on "and" leaves the second half without
    /// one - "target creature gets +3/+3 until end of turn and must be blocked this turn if
    /// able" is one subject with two things said about it.
    /// </remarks>
    [GeneratedRegex(
        @"^(?:(?<t>[A-Za-z0-9'’ ,-]+|~) )?must be blocked this (turn|combat) if able$",
        RegexOptions.IgnoreCase)]
    private static partial Regex MustBeBlockedThisTurnLine();

    /// <summary>"~ can block an additional creature this turn" (CR 509.1a).</summary>
    /// <remarks>
    /// The same three amounts the static printing prints, and no others: one more, a stated
    /// number more, or any number at all.
    /// </remarks>
    [GeneratedRegex(
        @"^(?:(?<t>[A-Za-z0-9'’ ,-]+|~) )?can block "
            + @"(?:(?<any>any number of creatures)|an additional creature|"
            + @"up to (?<n>[a-z]+) additional creatures) this (turn|combat)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ExtraBlocksThisTurnLine();

    /// <summary>"Untap up to three lands" — a choice made on resolution (CR 701.21a).</summary>
    [GeneratedRegex(
        @"^untap up to (?<n>" + N + @") (?<group>[a-z ]+)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex UntapUpToLine();

    /// <summary>"Look at the top N cards of your library, then put them back in any order".</summary>
    [GeneratedRegex(
        @"^look at the top (?<n>" + N + @") cards? of your library, "
            + @"then put (them|those cards) back in any order\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex LookAndArrangeLine();

    /// <summary>"Bolster 2" — counters on the smallest creature you control (CR 701.36a).</summary>
    [GeneratedRegex(@"^bolster (?<n>" + N + @")$", RegexOptions.IgnoreCase)]
    private static partial Regex BolsterLine();

    /// <summary>"Amass Orcs 2" and the older "Amass 2" (CR 701.44a).</summary>
    /// <remarks>
    /// The tribe was added to the keyword after it was printed without one; the cards that say
    /// nothing mean Zombies, which is why the group is optional rather than a second reader.
    /// </remarks>
    [GeneratedRegex(@"^[Aa]mass (?<kind>[A-Z][a-z]+)? ?(?<n>" + N + @")$", RegexOptions.None)]
    private static partial Regex AmassLine();

    [GeneratedRegex(@"^(you )?gain control of " + T + @"$", RegexOptions.IgnoreCase)]
    private static partial Regex GainControlLine();

    [GeneratedRegex(
        @"^(it|that creature) gains (?<kw>[a-z ,]+?) until end of turn$", RegexOptions.IgnoreCase)]
    private static partial Regex ItGainsUntilLine();

    [GeneratedRegex(@"^(it|that creature) gains (?<kw>[a-z ,]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ItGainsLine();

    [GeneratedRegex(@"^(it|~) explores$", RegexOptions.IgnoreCase)]
    private static partial Regex ExploreLine();

    [GeneratedRegex(@"^proliferate$", RegexOptions.IgnoreCase)]
    private static partial Regex ProliferateLine();

    /// <summary>"[Do something]. If you do, [do something else]" after a mandatory action.</summary>
    [GeneratedRegex(
        @"^(?<doing>(?!you may )[^.]+)\. If you do, (?<then>[^.]+)\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex IfYouDidLine();

    /// <summary>"You may [do something]" with nothing to pay for it (CR 603.2c).</summary>
    [GeneratedRegex(@"^you may (?<effect>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex MayDoLine();

    /// <summary>"Have [someone] [verb] ..." — the causative, conjugated back by Causative.</summary>
    /// <remarks>
    /// The subject is a target phrase or the source, and nothing else. **A pronoun may not be
    /// one**: "you may have <em>it</em> deal 4 damage to target opponent" is Aether Charge, where
    /// "it" is the Beast that just entered and the only reading available would deal the damage
    /// from the enchantment instead — a refusal this file already made deliberately, and one the
    /// first cut of this rewrite walked straight past. The corpus prints 54 lines with "it" as the
    /// subject and 16 with "that creature"; both are left where they were.
    /// </remarks>
    [GeneratedRegex(
        @"^have (?<who>~|(another |up to one )?target [^,]*?) "
            + @"(?<verb>get|gain|lose|discard|mill|draw|reveal|sacrifice"
            + @"|become|block|untap|create|fight|put|deal|shuffle|exile|destroy|return"
            + @"|tap|search|attack|scry|surveil)\b(?<rest>.*)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex CausativeLine();

    [GeneratedRegex(@"^you become the monarch$", RegexOptions.IgnoreCase)]
    private static partial Regex BecomeMonarchLine();

    /// <summary>"You take the initiative" (CR 726.1).</summary>
    [GeneratedRegex(@"^you take the initiative$", RegexOptions.IgnoreCase)]
    private static partial Regex TakeInitiativeLine();

    /// <summary>"Venture into the dungeon" (CR 701.49), or into Undercity (CR 701.49d).</summary>
    /// <remarks>
    /// Anchored at both ends, and the named arm names Undercity <em>exactly</em> — the two
    /// instructions start the venturing player in different dungeons with different rooms, so a
    /// pattern loose enough to admit any name after "into" would send a card into the wrong one
    /// while looking implemented. A named dungeon the engine does not ship stays unread.
    /// </remarks>
    [GeneratedRegex(
        @"^venture into (the dungeon|(?<named>Undercity))$", RegexOptions.IgnoreCase)]
    private static partial Regex VentureLine();

    /// <summary>"Creature cards in your graveyard" - counting a zone rather than the board.</summary>
    /// <remarks>
    /// The library is here beside the hand and the graveyard because it is the same question
    /// about a third pile, and the counter that answers it is the same line of code. It is not
    /// hidden information the count could leak: any player may count any library at any time
    /// (CR 401.3), and it is the order and the faces that are hidden, not the size.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<noun>[a-z ]*?) ?cards? in (?<whose>your|all|each player's) "
            + @"(?<zone>hand|graveyard|library)s?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex CardsInZoneLine();

    /// <summary>"+1/+1 counter on ~" - counting what is on a permanent, not the permanents.</summary>
    /// <remarks>
    /// "This creature", "this artifact", "this enchantment", "this Equipment", "this Aura",
    /// "this land" — every one of them names the object the ability is printed on (CR 700.7),
    /// exactly as "~" and "it" do here, so the noun after "this" is not read: it says which kind
    /// of permanent the source is, which the source already knows. The current templating uses
    /// those spellings and the older cards use the name, so refusing them left ninety-odd corpus
    /// lines unread for a synonym.
    /// <para>
    /// The kind is optional because "the number of counters on it" means every counter of every
    /// kind, which is a different question from any one of them and is answered below by summing.
    /// It is written as its own alternative rather than an optional group so that the two cannot
    /// be confused: an absent kind is a total, never a kind that failed to match.
    /// </para>
    /// <para>
    /// Plural as well as singular, because the two spellings on a card mean the same count:
    /// "for each charge counter on it" and "the number of charge counters on it" are the same
    /// question, and the second arrives here with its "s" still on. The plural noun folding that
    /// handles this everywhere else knows the six card types and not the word "counter", so the
    /// commonest counted group in the corpus — forty-three cards say "the number of +1/+1
    /// counters on it" — was refused by one letter.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"^((?<kind>[+-]?[0-9]+/[+-]?[0-9]+|[a-z]+) )?counters? on (~|it|this [A-Za-z]+)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex CountersOnLine();

    [GeneratedRegex(@"^transform (~|it)$", RegexOptions.IgnoreCase)]
    private static partial Regex TransformSelfLine();

    /// <remarks>
    /// The subject alternation is broad because the printed subject varies with where the clause
    /// sits — "this spell" on an instant, "it" inside a trigger — and the wrapper reads the
    /// resolving source whichever word the card used. Bargain is the default arm: the absence of
    /// both named groups.
    /// </remarks>
    [GeneratedRegex(
        @"^if (~|this spell|this creature|it) (was bargained"
            + @"|(?<team>was cast using teamwork)"
            + @"|was kicked with its (?<cost>(\{[^}]+\})+) kicker)"
            + @", (?<effect>.+)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex LeadingCastFactSentence();

    /// <remarks>
    /// Lazy on the effect and anchored on the tail, so the condition is taken off the end and
    /// nothing shorter. Only teamwork prints the trailing spelling; the other facts' clauses
    /// always lead.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<effect>.+?),? (?:(?<unless>unless)|if) (~|this spell) was cast using teamwork$",
        RegexOptions.IgnoreCase)]
    private static partial Regex TrailingTeamworkSentence();

    [GeneratedRegex(@"\s+instead$", RegexOptions.IgnoreCase)]
    private static partial Regex InsteadTail();

    /// <remarks>
    /// The head and tail are kept so the rewritten sentence reads as the card would have written
    /// it for one target — "Return " + "target creature card from your graveyard" + " to your
    /// hand" — which is what lets the ordinary matchers take it.
    /// </remarks>
    [GeneratedRegex(
        // "One or two target creatures" - a range rather than a count, and a different thing
        // from "up to two": the first target is required. Written before the bare numbers
        // because alternation is ordered and "two" would otherwise match the front of
        // "two or three" and leave the rest of the phrase stranded.
        @"^(?<head>.*?)(?<upto>up to )?"
            + @"(?<n>one or two|two or three|three or four|two|three|four) "
            + @"(?<other>other )?target "
            + @"(?<t>[A-Za-z' ]+?)"
            + @"(?<tail>| (get|gain|become|have|are)\s.*| to .*| from .*)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex MultiTargetLine();

    /// <summary>
    /// "Any number of target creatures ..." — the same sentence with the count left to the
    /// caster (CR 601.2c).
    /// </summary>
    /// <remarks>
    /// The counted form's twin, and written to match it group for group so the same singular
    /// rewrite reads both. The tail alternation is wider by exactly the verbs this phrase is
    /// printed with and the counted one is not — "any number of target players each mill two
    /// cards", "any number of target creatures can't block this turn" — because a card that
    /// says "any number" is far more often a whole sentence about a group than a pump.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<head>.*?)any number of (?<other>other )?target "
            + @"(?<t>[A-Za-z' ]+?)"
            + @"(?<tail>| (get|gain|become|have|are|can't|each|deal|die|phase|lose|draw|"
            + @"may|shuffle|mill|discard|sacrifice|search|untap|tap)\s.*| to .*| from .*)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex AnyNumberTargetLine();

    [GeneratedRegex(@"^copy (?<t>target [a-z0-9'’ ]+ spell)$", RegexOptions.IgnoreCase)]
    private static partial Regex CopyTargetSpellLine();

    /// <summary>The words a card uses for "the permanent this sentence is still about".</summary>
    private static readonly string[] Pronouns =
        ["it", "that creature", "that permanent", "that artifact", "that token"];

    /// <summary>
    /// Reads a verb's object as either a target or the thing the sentence is already about.
    /// </summary>
    /// <remarks>
    /// "Destroy target creature" and "destroy it" are one verb with two kinds of object, and
    /// before this they were one verb that could only take the first. Returning the subject and
    /// the index together is what lets the caller add a target in one case and not the other -
    /// a pronoun names nothing, so adding a target for it would make the spell ask the player to
    /// choose something the sentence had already decided.
    /// </remarks>
    /// <summary>
    /// The index of an already-chosen target a verb's pronoun object names, or null.
    /// </summary>
    /// <remarks>
    /// For the verbs whose effect carries a target index and nothing else — damage and the
    /// until-end-of-turn theft. <see cref="ObjectOf"/> answers the same question in three ways,
    /// and only one of the three can be honoured by an effect with nowhere to put a subject: the
    /// target an earlier sentence chose. The other two are refused here rather than approximated.
    /// <para>
    /// Approximating them is the specific mistake this codebase has already made and reverted.
    /// A pronoun that means the <em>trigger's</em> subject, answered with "whatever was targeted",
    /// turned "whenever ~ blocks a creature, destroy that creature" into a card that destroyed
    /// the blocker — it compiled, it played, and it was the wrong card. An unread line is the
    /// cheaper failure, so a pronoun this cannot resolve to a target leaves the line unread.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Aims a "this turn" combat requirement at whatever its sentence names (CR 611.2).
    /// </summary>
    /// <remarks>
    /// The three subjects these sentences use, and the effect each one needs. "~" is the
    /// permanent whose ability it is, so it takes the source form and asks the player nothing;
    /// "target creature" adds a target; and a pronoun - or the bare half left behind when a
    /// sentence is split on "and" - is the target the sentence has already chosen.
    /// <para>
    /// The pronoun arm is deliberately stricter than its neighbours: with nothing targeted it
    /// returns null and the line stays unread, rather than reaching for the triggering object.
    /// Every printing of these two sentences that the corpus has either names a target or is the
    /// tail of one that does, so an arm no card exercises would be an arm no test keeps honest.
    /// </para>
    /// </remarks>
    private static IEffect? AimedThisTurn(
        string who,
        string definitionId,
        ImmutableList<TargetSpec>.Builder targets)
    {
        var subject = who.Trim();

        if (string.Equals(subject, "~", StringComparison.Ordinal))
            return new PumpSourceUntilEndOfTurn(definitionId);

        if (subject.Length == 0 || Pronouns.Contains(subject, StringComparer.OrdinalIgnoreCase))
        {
            return targets.Count > 0
                ? new PumpUntilEndOfTurn(definitionId, targets.Count - 1)
                : null;
        }

        if (Specs.Parse(subject) is not { } aimed)
            return null;

        targets.Add(aimed);
        return new PumpUntilEndOfTurn(definitionId, targets.Count - 1);
    }

    private static int? PronounObject(
        Match m,
        ImmutableList<TargetSpec>.Builder targets,
        bool objectNamedByTrigger)
    {
        if (!m.Success || !Pronouns.Contains(
                m.Groups["t"].Value.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var named = ObjectOf(m.Groups["t"].Value, targets, objectNamedByTrigger);

        return named is { Subject: EffectSubject.Target } ? named.Value.Index : null;
    }

    private static (EffectSubject Subject, int Index)? ObjectOf(
        string phrase,
        ImmutableList<TargetSpec>.Builder targets,
        bool objectNamedByTrigger = false)
    {
        var trimmed = phrase.Trim();

        if (Pronouns.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
        {
            // A target named earlier in the same sentence wins: "destroy target creature, then
            // exile it" is about that creature and not about whatever the trigger saw.
            if (targets.Count > 0)
                return (EffectSubject.Target, targets.Count - 1);

            // With nothing targeted, a pronoun refers to the triggering event - but only when
            // there is one and it carries an object. That is what the flag says, and it is set
            // from an allow-list of trigger shapes rather than guessed, because the failure it
            // prevents is specific: reading the pronoun as the ordinary trigger subject was tried
            // and reverted, since that subject falls back to the permanent with the ability when
            // the event has none, and "whenever ~ blocks a creature, destroy that creature" then
            // destroyed the blocker. The strict subject cannot do that - it resolves to the object
            // or to nothing - and the flag stops it being asked where nothing is what it would be.
            if (objectNamedByTrigger)
                return (EffectSubject.TriggeringObject, 0);

            // Otherwise a pronoun has no referent this reader can see, and guessing aims the
            // effect at whatever happened to be last. A verb that destroys the wrong permanent is
            // worse than one that is not read at all.
            return null;
        }

        if (trimmed.Equals("~", StringComparison.Ordinal))
            return (EffectSubject.Source, 0);

        if (Specs.Parse(trimmed) is not { } spec)
            return null;

        targets.Add(spec);
        return (EffectSubject.Target, targets.Count - 1);
    }

    [GeneratedRegex(
        @"^create a token that's a copy of (?<of>[^,]+)"
            + @"(?<except>, except it isn't legendary)?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex TokenCopyLine();

    [GeneratedRegex(
        @"^(?<who>you|each opponent|each player) (?<verb>gains?|loses?) "
            + @"((?<n>\d+|X) life for (?<t>each " + COUNTED + @"+)"
            + @"|life equal to (?<mult>twice |three times )?the number of (?<t>" + COUNTED + @"+)"
            + @"|life equal to (?<subject>that [a-z]+'s|its|the sacrificed [a-z]+'s) "
            + @"(?<stat>power|toughness|mana value))$",
        RegexOptions.IgnoreCase)]
    private static partial Regex PerEachLifeLine();

    /// <remarks>
    /// "Equal to the number of" is "for each" with the multiplier left off, so it is read into the
    /// same amount with a fixed part of one rather than getting a mechanism of its own.
    /// </remarks>
    [GeneratedRegex(
        @"^draw " + N + @" cards? for each (?<t>" + COUNTED + @"+)"
            + @"|^draw cards equal to (?<mult>twice |three times )?the number of (?<t>" + COUNTED + @"+)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex PerEachDrawLine();

    /// <remarks>
    /// The group phrase keeps its "each"/"all", because that is what the target grammar reads it
    /// by — "each creature you control" is a group and "creature you control" is not.
    /// </remarks>
    [GeneratedRegex(
        @"^put " + N + @" (?<kind>\+1/\+1|-1/-1) counters? on "
            + @"(?<t>(each|all) [A-Za-z0-9'’ ]+)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex MassCountersLine();

    [GeneratedRegex(
        @"^(?<t>target (player|opponent)) discards " + N + @" "
            + @"cards?(?<random> at random)?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex TargetDiscardLine();

    /// <remarks>
    /// The amount is spelled in symbols rather than digits — "you get {E}{E}" is two — so it is
    /// counted rather than parsed.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<who>you|each opponent|each player) gets? (?<e>(\{E\})+)$", RegexOptions.IgnoreCase)]
    private static partial Regex GetEnergyLine();

    /// <summary>"You get an experience counter" (CR 122.1).</summary>
    /// <remarks>
    /// Every printed line is exactly one, and the number is read rather than assumed for the
    /// same reason the amount on the effect is an <c>Amount</c>: a card that ever prints two
    /// then needs nothing new.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<who>you|each opponent|each player) gets? "
            + @"(?<n>an|one|two|three|\d+) experience counters?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex GetExperienceLine();

    /// <summary>"Defending player gets a poison counter" (CR 122.1, 704.5c).</summary>
    /// <remarks>
    /// The whole player vocabulary rather than the three words energy takes, because this
    /// sentence is printed with a combat subject far more often than with "you": it is nearly
    /// always the defender or the player a trigger was about who takes the counter.
    /// </remarks>
    [GeneratedRegex(
        @"^" + W + @" gets? " + N + @" poison counters?$", RegexOptions.IgnoreCase)]
    private static partial Regex GetPoisonLine();

    /// <remarks>
    /// "Otherwise" would be a correct synonym for "if you lose the flip" — CR 705.2 gives a flip
    /// exactly two outcomes — and is left out because <strong>one</strong> corpus card spells it
    /// that way, Plasma Caster, whose other half ("choose target creature that's blocking equipped
    /// creature") the compiler cannot read either. An alternation that no card can reach is a
    /// reader that never fires, which looks exactly like a reader that works.
    /// </remarks>
    [GeneratedRegex(
        @"^[Ff]lip a coin\.?"
            + @"(\s*If you win the flip, (?<won>[^.]+)\.?)?"
            + @"(\s*If you lose the flip, (?<lost>[^.]+)\.?)?\s*$",
        RegexOptions.None)]
    private static partial Regex FlipCoinLine();

    /// <remarks>
    /// The full stop straight after the die is load-bearing: "roll a d20 and add the number of
    /// cards in your hand" is a roll with a modifier (CR 706.2), machinery that does not exist,
    /// and the sentence shape is what keeps every modified roll honestly unread. "Roll two d6"
    /// and "roll X six-sided dice" fail the "a" for the same reason — nothing reads a
    /// multi-dice roll's aggregate yet.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<before>.+?[.,!] )??(?:[Tt]hen )?[Rr]oll a (?:d(?<sides>4|6|8|10|12|20)"
            + @"|(?<worded>four|six|eight|ten|twelve|twenty)-sided die)\.(?<rest>.*)$")]
    private static partial Regex RollDieLine();

    /// <summary>Where the next results-table row begins (CR 706.3a).</summary>
    /// <remarks>
    /// The pipe is what makes this safe to split on: no playable card's rules text contains
    /// " | " anywhere but a results row — measured across the corpus, not assumed. The
    /// lookbehind is start-or-space rather than <c>\b</c>, because a word boundary also sits
    /// between "1—" and "9" — and a split there hands the reader a row head torn in half.
    /// </remarks>
    [GeneratedRegex(@"(?=(?<=^| )\d+(?:[—–-]\d+|\+| or less)? \| )")]
    private static partial Regex ResultsRowStart();

    [GeneratedRegex(@"^(?<from>\d+)(?:[—–-](?<to>\d+)|(?<plus>\+)| or (?<less>less))? \| ")]
    private static partial Regex ResultsRowHead();

    /// <remarks>
    /// "If the result is equal to or less than the number of Robots you control" fails the
    /// digit and is meant to: a row's edges are literal numbers or they are not a row.
    /// </remarks>
    [GeneratedRegex(
        @"^[Ii]f the result is (?<n>\d+)(?: or (?<dir>less|lower|higher|greater|more))?, (?<eff>.+)$")]
    private static partial Regex ResultConditionSentence();

    [GeneratedRegex(@"[Ss]cry (?:a number of cards equal to the result|X, where X is the result)")]
    private static partial Regex ScryTheResult();

    [GeneratedRegex(@"a number of (?<what>[^.]+?) equal to the result")]
    private static partial Regex ANumberOfEqualToResult();

    [GeneratedRegex(@"\bcards equal to the result")]
    private static partial Regex CardsEqualToResult();

    [GeneratedRegex(@"\blife equal to the result")]
    private static partial Regex LifeEqualToResult();

    /// <remarks>
    /// Only the bare "choose target": "choose up to two target cards" and "choose any number of"
    /// are counts the downstream sentences then distribute over, which is different machinery.
    /// </remarks>
    [GeneratedRegex(@"^[Cc]hoose (?<t>target [a-z0-9' ,-]+)$")]
    private static partial Regex ChooseTargetSentence();

    /// <remarks>
    /// "Any of those results" alongside "the roll", because a card that watches "one or more
    /// dice" speaks of its results in the plural even when one die was rolled — and one die is
    /// all a roll produces until the multi-dice instructions compile.
    /// </remarks>
    [GeneratedRegex(
        @"^[Ii]f (?:the roll was|any of those results was|the result was) "
            + @"(?<n>\d+) or (?:higher|greater), (?<eff>.+)$")]
    private static partial Regex RollWasSentence();

    /// <summary>Whether a line is a results-table row, for the line reader's fold (CR 706.3b).</summary>
    internal static bool IsResultsRow(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return ResultsRowHead().IsMatch(line);
    }

    /// <summary>Whether a line instructs somebody to roll dice, however it is worded.</summary>
    /// <remarks>
    /// Deliberately broader than <see cref="RollDieLine"/>: a results table under a roll this
    /// cannot read yet — "roll two d20 and ignore the lower roll" — still belongs to that roll
    /// (CR 706.3b), and folding it there reports one unread ability rather than three unread
    /// fragments. The planar die is excluded because its faces are symbols, not numbers
    /// (CR 901.4), and no results table has ever ridden under one.
    /// </remarks>
    internal static bool CallsForDice(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return RollAnywhere().IsMatch(line) && !line.Contains("planar", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"\broll(s|ed)? .{0,80}?\b(?:die|dice|d4|d6|d8|d10|d12|d20)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RollAnywhere();

    /// <remarks>
    /// Only "an opponent". "Clash with defending player" names somebody the clash machinery does
    /// not take, and at a table of more than two "an opponent" is a choice this does not ask -
    /// the settle refuses there rather than clashing with whoever came first.
    /// </remarks>
    [GeneratedRegex(
        @"^[Cc]lash with an opponent\.?"
            + @"(\s*If you win, (?<won>[^.]+)\.?)?\s*$",
        RegexOptions.None)]
    private static partial Regex ClashLine();

    /// <remarks>
    /// Only the this-turn window and the one that reaches the end of the player's next turn.
    /// The longer one is not writable as a turn number - see <c>MayPlayThroughOwnersNextTurn</c>
    /// for why - so it rides as a flag, and it is accepted written either way round: cards spell
    /// it in front of the permission and behind it about equally often, and taking only the
    /// first left five cards unread over a word order.
    /// <para>
    /// The pronoun matters as much as the duration. "You may play it this turn" and "you may
    /// play that card this turn" are the same sentence, and reading only the noun form refused
    /// six cards whose exile half had been understood all along.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"^[Ee]xile the top (" + N + @" )?cards? of your library\."
            + @"\s*([Uu]ntil end of turn, |[Uu]ntil (?<long>the end of your next turn), )?"
            + @"[Yy]ou may play (that card|those cards|it|them)"
            + @"( this turn| until end of turn| until (?<long>the end of your next turn))?"
            + @"\.?(?<after>.*)$",
        RegexOptions.None)]
    private static partial Regex ExileAndPlayLine();

    [GeneratedRegex(
        @"^[Ee]xile (?<t>(another |up to one )?target [a-z' ]+)\."
            + @"\s*[Rr]eturn (that card|it) to the battlefield under its owner's control"
            + @" at the beginning of the next end step\.?$",
        RegexOptions.None)]
    private static partial Regex ExileAndReturnLine();

    [GeneratedRegex(
        @"^shuffle ~ into its owner's library\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex ShuffleSelfIntoLibraryLine();

    /// <remarks>
    /// "It" and the card's own name both mean the permanent the ability is printed on, which is
    /// the only thing any printing of this makes prepared.
    /// </remarks>
    [GeneratedRegex(@"^(it|~) becomes prepared\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex BecomesPreparedSentence();

    [GeneratedRegex(
        @"^you may play (an|(?<n>one|two|three)) additional lands? this turn\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ExtraLandThisTurnLine();

    /// <remarks>
    /// Both durations in one pattern, and the wording follows the duration: a turn outlasts a
    /// phase boundary and says "steps and phases", while end of combat only has steps left to
    /// survive.
    /// </remarks>
    /// <remarks>
    /// Only the costs that need one selection from one zone. "Unless you return a land you
    /// control to its owner's hand" is deliberately not here: it is a different destination and
    /// would want its own kind rather than a third meaning for this one.
    /// </remarks>
    [GeneratedRegex(@"^learn\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex LearnLine();


    [GeneratedRegex(
        @"^return (it|that card|them|those cards) to the battlefield "
            + @"under (its owner's|their owner's|your) control "
            + @"at the beginning of the next end step\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ReturnAtEndStepLine();

    [GeneratedRegex(
        @"^~ deals " + N + @" damage to that [a-z]+'s controller\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex DamageSubjectControllerLine();

    [GeneratedRegex(
        @"^((the owner of (?<t>[a-z0-9'’ ,-]+?))|((?<t2>[a-z0-9'’ ,-]+?)'s owner)|(?<its>its owner))"
            + @" puts it on their choice of the top or bottom of their library\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex OwnerFilesLine();

    [GeneratedRegex(
        @"^return that card to the battlefield under (?<whose>your|its owner's) control\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ReturnSubjectCardLine();

    [GeneratedRegex(
        @"^tap (?<t>[A-Za-z0-9'’~ ,-]+?) and (it|they) (doesn't|don't) untap during "
            + @"(its|their) controller'?s? next untap step\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex TapAndFreezeLine();

    [GeneratedRegex(
        @"^if there are no (?<kind>[a-z+/0-9 ]+?) counters on (~|it), sacrifice (it|~)\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex NoCountersLine();

    [GeneratedRegex(
        @"^sacrifice (it|~) unless \{(?<c>[WUBRG])\} was spent to cast (it|~)\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SacrificeUnlessSpentLine();

    /// <summary>The colour a single mana symbol names, or null if it is not a coloured one.</summary>
    private static Domain.Enums.ManaColor? ColorNamedBySymbol(string symbol) =>
        symbol.ToUpperInvariant() switch
        {
            "W" => Domain.Enums.ManaColor.White,
            "U" => Domain.Enums.ManaColor.Blue,
            "B" => Domain.Enums.ManaColor.Black,
            "R" => Domain.Enums.ManaColor.Red,
            "G" => Domain.Enums.ManaColor.Green,
            _ => null,
        };

    /// <remarks>
    /// The duration is printed at either end - "Until end of turn, target creature gains ..." and
    /// "... gains ... until end of turn" are the same sentence - so both are matched and neither
    /// is captured: this reader only ever makes the until-end-of-turn kind.
    /// </remarks>
    [GeneratedRegex(
        @"^(until end of turn, )?(?<t>[A-Za-z0-9'’~ ,-]+?)"
            + @"( gets (?<p>[+-](\d+|X))/(?<tough>[+-](\d+|X)) and)? gains "
            + "\"(?<ability>[^\"]+)\""
            + @"( until end of turn)?\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex GrantsQuotedAbilityLine();

    [GeneratedRegex(
        @"^(until end of turn, )?(?<t>[A-Za-z0-9'’~ ,-]+?) gain "
            + "\"(?<ability>[^\"]+)\""
            + @"( until end of turn)?\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex MassGrantsQuotedAbilityLine();

    /// <remarks>
    /// "Another" is accepted and not acted on, which is safe only because this ransom is always
    /// on the source: the one permanent the word excludes is the source itself, and paying with
    /// it puts it in the graveyard exactly as declining would (CR 109.5). Anywhere the subject
    /// was not the source, the word would have to be obeyed.
    /// </remarks>
    [GeneratedRegex(
        @"^sacrifice (it|~) unless you "
            + @"((?<verb>sacrifice|discard) (another|an?|(?<n>two|three|four|five|six|seven|eight|nine|ten)) (?<what>[A-Za-z' ]*?)"
            + @"\s*(cards?)?(?<random> at random)?"
            + @"|(?<verb>return) (another|an?|(?<n>two|three|four|five|six|seven|eight|nine|ten)) ?(?<what>[A-Za-z'\- ]+?)"
            + @" to (its|their) owner's hand)\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SacrificeUnlessLine();

    /// <remarks>
    /// "You take" and the bare imperative are the same sentence (CR 608.2), and the targeted form
    /// names somebody instead - which is a different card at a table of more than two.
    /// </remarks>
    [GeneratedRegex(
        @"^(you take|take|(?<who>target player) takes) an extra turn after this one\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ExtraTurnLine();

    [GeneratedRegex(
        @"^exile ~ with (?<n>one|two|three|four|five) time counters? on it\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ExileSelfWithTimeCountersLine();

    [GeneratedRegex(
        @"^you may put an? (?<what>[A-Za-z, ]+? )?card from your hand onto the battlefield"
            + @"(?<tapped> tapped)?\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex PutFromHandLine();

    [GeneratedRegex(
        @"^until end of (turn|(?<combat>combat)), you don't lose this mana as steps"
            + @"( and phases)? end\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex KeepManaLine();

    /// <remarks>
    /// "You control" is required rather than optional: an effect that made an <em>opponent</em>
    /// choose is a different card, and one this cannot express — the question would go to the
    /// wrong player.
    /// </remarks>
    /// <remarks>
    /// Only sacrifice. An edict aimed at "target opponent" is a different sentence and needs
    /// the target machinery; this is the shape that names its player by relation rather than
    /// by choosing one.
    /// <para>
    /// It knew two of the seven subjects the shared player vocabulary spells, and the five it
    /// did not were the whole of why "whenever ~ deals combat damage to a player, <em>that
    /// player</em> sacrifices a creature of their choice" went unread beside "each opponent
    /// sacrifices a creature of their choice" - one instruction, one effect, and a subject the
    /// grammar had already been taught next door. Every scope in the list resolves through
    /// <see cref="PlayerScopes"/>, so nothing underneath had to learn what any of them mean.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"^" + WThem + @" sacrifices an? (?<what>[a-z ]+?)"
            + @"( of their choice)?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex EdictLine();

    [GeneratedRegex(
        // The noun is a phrase, not a word: "a basic land", "a red or green creature", "an
        // untapped Island". All of those go through the target grammar below, which has read
        // them for a long time - only this pattern could not hand them over.
        //
        // "Another" travels with them for the same reason: the grammar strips it itself and
        // excludes the source, and writing it out here would be a second copy of that rule.
        @"^(?<verb>return|sacrifice) (?<another>another|an|a) (?<what>[a-z ]+?)( you control)?"
            + @"( to (its owner's|their owner's) hand)?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ChooseAndMoveLine();

    /// <remarks>
    /// The modifier run is kept, not discarded: the colours and the creature type it prints are
    /// conferred with the size (see <see cref="AnimatedSubtypes"/> and
    /// <see cref="AnimatedColors"/>). An older note here said the type was matched and thrown
    /// away because the engine had no use for it, and that has not been true since
    /// <c>CharacteristicsBuilder.Subtypes</c> existed - a Keyrune that animated into a typeless,
    /// colourless 2/2 was a different card in front of a lord and in front of protection.
    /// <para>
    /// The <c>add</c> tail is CR 205.1b: an animation saying "in addition to its other types"
    /// keeps every type the permanent already had, so the subtype is <em>added</em> rather than
    /// replacing what was there. It is spelled out as its own group because the keyword clause
    /// before it would otherwise swallow the words and hand "haste in addition to its other
    /// types" to the keyword table, which refuses it and takes the whole sentence down with it.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"^(?<pre>[Uu]ntil end of turn, )?" + T + @" [Bb]ecomes an? (?<p>\d+)/(?<tough>\d+)"
            + @"(?<mods>( [a-z][a-z,]*| [A-Z][a-z]+)*) creature"
            + @"( with (?<kw>[a-z ,]+?))?(?<add> in addition to its other types)?"
            + @"(?<ueot> until end of turn)?$",
        RegexOptions.None)]
    private static partial Regex AnimateLine();

    /// <summary>The same animation, said of the permanent whose ability it is.</summary>
    [GeneratedRegex(
        @"^(?<pre>[Uu]ntil end of turn, )?~ [Bb]ecomes an? (?<p>\d+)/(?<tough>\d+)"
            + @"(?<mods>( [a-z][a-z,]*| [A-Z][a-z]+)*) creature"
            + @"( with (?<kw>[a-z ,]+?))?(?<add> in addition to its other types)?"
            + @"(?<ueot> until end of turn)?$",
        RegexOptions.None)]
    private static partial Regex AnimateSelfLine();

    /// <summary>"~ becomes a copy of target artifact until end of turn" (CR 707.2).</summary>
    /// <remarks>
    /// Only of the permanent whose ability it is. Every printing of the targeted form —
    /// "target creature becomes a copy of another target creature" — needs two targets in one
    /// sentence, and the second of them has to be read as "another", so it is a different shape
    /// rather than a wider version of this one.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<pre>[Uu]ntil end of turn, )?~ becomes a copy of (?<t>.+?)"
            + @"(?<ueot> until end of turn)?\.?$",
        RegexOptions.None)]
    private static partial Regex BecomesACopyLine();

    /// <summary>
    /// "Target creature has base power and toughness 4/4 until end of turn" (CR 613.4b).
    /// </summary>
    /// <remarks>
    /// A different verb from the animation and a different layer from a pump: 613.4b says an
    /// effect that <em>refers to the base power and toughness</em> of a creature applies in the
    /// setting sublayer, so a 2/2 set to 4/4 and then given a +1/+1 counter is a 5/5. Read as a
    /// pump it would have been a 6/6, and on a permanent with no printed size it would have been
    /// nothing at all.
    /// <para>
    /// The duration may be printed in either of two places and the keyword clause may sit between
    /// them - "has base power and toughness 4/2 until end of turn and gains first strike until
    /// end of turn" prints it twice. One of them has to be there: the effect this builds ends in
    /// the cleanup step, and a card that sets a size for good would be silently undone.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"^(?<pre>[Uu]ntil end of turn, )?" + T
            + @" has base (power and toughness (?<p>\d+)/(?<tough>\d+)"
            + @"|power (?<p>\d+))"
            + @"(?<u1> until end of turn)?( and gains (?<kw>[a-z ,]+?))?"
            + @"(?<u2> until end of turn)?$",
        RegexOptions.None)]
    private static partial Regex BasePowerToughnessLine();

    /// <summary>The same setting, said of the permanent whose ability it is.</summary>
    [GeneratedRegex(
        @"^(?<pre>[Uu]ntil end of turn, )?~"
            + @" has base (power and toughness (?<p>\d+)/(?<tough>\d+)"
            + @"|power (?<p>\d+))"
            + @"(?<u1> until end of turn)?( and gains (?<kw>[a-z ,]+?))?"
            + @"(?<u2> until end of turn)?$",
        RegexOptions.None)]
    private static partial Regex BasePowerToughnessSelfLine();

    /// <summary>
    /// "Target creature loses all abilities until end of turn" (CR 613.1f, layer 6).
    /// </summary>
    /// <remarks>
    /// Both halves are captured rather than alternated over. What precedes the clause is a target
    /// phrase, "~", or a pronoun, and <see cref="Specs"/> already tells those apart; what follows
    /// it is a whole instruction, and it is handed back to the sentence grammar rather than
    /// enumerated here — so this pattern never has to know what can come after "and".
    /// <para>
    /// Case-sensitive, and the plural verb is optional in one letter: "creatures target player
    /// controls <em>lose</em> all abilities" is the group form of the same sentence.
    /// </para>
    /// <para>
    /// The tail is anchored on the whole clause, so a wording this cannot read leaves the line
    /// unread instead of silencing something and dropping the rest. "All lands lose all abilities
    /// <em>except mana abilities</em>" is the case that matters: it does not match, which is the
    /// right answer, because a land silenced outright is a land that cannot tap for mana.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"^(?<head>.+?) loses? all abilities"
            + @"(?<ueot> until end of turn)?"
            + @"( and (?<rest>.+?))?\.?$",
        RegexOptions.None)]
    private static partial Regex LosesAllAbilitiesSentence();

    /// <summary>
    /// "~ becomes an artifact creature until end of turn" — an animation with no size (CR 205.1b).
    /// </summary>
    /// <remarks>
    /// The Vehicle wording, and the reason it is its own pattern rather than a relaxation of the
    /// animation above: there is no printed P/T in the sentence because the permanent already has
    /// one, so setting a size here would overwrite the number the card is played for. CR 205.1b
    /// names this exact phrase and says the object keeps all of its prior card types and
    /// subtypes, which is what adding the two type flags does.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<pre>[Uu]ntil end of turn, )?(?<self>~|" + T + @")"
            + @" becomes an(?<mods> artifact creature)( and gains (?<kw>[a-z ,]+?))?"
            + @"(?<ueot> until end of turn)?$",
        RegexOptions.None)]
    private static partial Regex AnimateArtifactLine();

    /// <summary>
    /// "Target creature becomes a Dragon with base power and toughness 4/4" (CR 205.1, 613.4b).
    /// </summary>
    /// <remarks>
    /// The animation with its size printed after the noun rather than before it. Same layers,
    /// same effects, one more sentence shape — which is the whole argument for building the run
    /// of effects in one place and matching the wording in several.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<pre>[Uu]ntil end of turn, )?(?<self>~|" + T + @")"
            + @" becomes an?(?<mods>( [a-z][a-z,]*| [A-Z][a-z]+)+)"
            + @" with base power and toughness (?<p>\d+)/(?<tough>\d+)"
            + @"(?<add> in addition to its other types)?"
            + @"( and gains (?<kw>[a-z ,]+?))?(?<ueot> until end of turn)?$",
        RegexOptions.None)]
    private static partial Regex AnimateWithBaseLine();

    /// <summary>
    /// "Target permanent becomes an artifact in addition to its other types" (CR 205.1b).
    /// </summary>
    /// <remarks>
    /// The phrase is required rather than optional, and that is the reader's safety: with it, the
    /// rule says every prior type is kept and adding is right. Without it, CR 205.1a replaces the
    /// permanent's subtypes from the same set — "target land becomes an Island" takes the land
    /// types with it — and the two readings differ on exactly the cards this shape is printed on.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<pre>[Uu]ntil end of turn, )?(?<self>~|" + T + @")"
            + @" becomes an?(?<mods>( [a-z][a-z,]*| [A-Z][a-z]+)+)"
            + @"(?<add> in addition to its other types)"
            + @"( and gains (?<kw>[a-z ,]+?))?(?<ueot> until end of turn)?$",
        RegexOptions.None)]
    private static partial Regex AnimateTypeOnlyLine();

    /// <remarks>
    /// Where the rest go is read rather than assumed, because the corpus does not agree: most say
    /// the bottom of the library and a good number say the graveyard, and one that quietly buried
    /// cards the card meant to bin would be wrong in a way nothing downstream could see.
    /// <para>
    /// "In any order" and "in a random order" are both accepted and treated alike. The cards end
    /// face down on the bottom of a library either way, so the difference is one no player can
    /// observe — unlike the destination, which they certainly can.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"^look at the top " + N + @" cards? of your library"
            + @"[.,]?\s*(then\s+)?(You may\s+)?[Pp]ut one of them into your "
            + @"(?<where>hand|graveyard)"
            + @"[.,]?\s*(and\s+|then\s+)?([Pp]ut\s+)?the (rest|other)"
            + @"\s+(?<rest>on the bottom of your library|on the bottom|into your graveyard)"
            + @"( in (a random|any) order)?\.?(?<after>.*)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex LookAndTakeLine();

    [GeneratedRegex(
        @"^look at the top " + N + @" cards? of your library"
            + @"[.,]?\s*You may reveal an? (?<what>[A-Za-z][A-Za-z, ]*?) card from among them"
            + @" and put (it|that card) into your hand"
            + @"[.,]?\s*(and\s+|then\s+)?([Pp]ut\s+)?the (rest|other)"
            + @"\s+(?<rest>on the bottom of your library|on the bottom|into your graveyard)"
            + @"( in (a random|any) order)?\.?(?<after>.*)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex LookAndRevealLine();

    /// <remarks>
    /// The bounce is a third arm rather than a third verb in the first one, because it is the
    /// only one of the three that names where the card goes: "sacrifice it" and "exile it" say
    /// the whole instruction in two words and "return it" does not.
    /// </remarks>
    [GeneratedRegex(
        @"^((?<verb>sacrifice|exile|destroy) (?<who>it|~|that creature)"
            + @"|(?<verb>return) (?<who>it|~) to its owner's hand)"
            + @" (at the beginning of the next (?<step>[a-z ]+)|(?<combat>at end of combat))\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex DelayedSelfLine();

    /// <summary>
    /// "Remove a +1/+1 counter from it at end of combat" (CR 603.7, 122.1).
    /// </summary>
    /// <remarks>
    /// The subject words are the ones a card uses for the permanent whose ability this is - "it",
    /// "~", "this creature" - and nothing wider. A delayed counter change is aimed at the source
    /// and has nowhere to put a target, so a phrase naming anything else must stay unread rather
    /// than land on the wrong permanent.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<verb>put|remove) (?<n>a|an|one|two|three|[0-9]+) (?<kind>[+-]?[0-9]+/[+-]?[0-9]+|[a-z]+) "
            + @"counters? (on|from) (it|~|this creature)"
            + @" (at the beginning of the next (?<step>[a-z ]+)|(?<combat>at end of combat))\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex DelayedSelfCountersLine();

    [GeneratedRegex(@"^attach (it|~) to " + T + @"$", RegexOptions.IgnoreCase)]
    private static partial Regex AttachSelfLine();

    /// <summary>"Then attach ~ to it" - the source onto whatever was just targeted.</summary>
    [GeneratedRegex(@"^attach ~ to (it|that creature)$", RegexOptions.IgnoreCase)]
    private static partial Regex AttachSelfToItLine();

    /// <remarks>
    /// Case-sensitive, unlike every other pattern here, because capitalisation is what tells a
    /// creature type from an ordinary word: "white Soldier creature token" has exactly one
    /// subtype in it and no vocabulary of type names is needed to find it. The cost is that the
    /// verb has to spell out both cases, since the sentence may open a line or follow a trigger.
    /// </remarks>
    [GeneratedRegex(
        @"^((?<who>[Ee]ach opponent|[Ee]ach player|[Tt]arget player|[Tt]arget opponent"
            + @"|[Ii]ts controller|[Tt]hat [a-z]+'s controller|[Tt]he subject's controller) "
            + @"creates?|[Cc]reate) " + N
            + @" (?<tapped>tapped )?(?<p>\d+)/(?<tough>\d+) (?<colours>[a-z, ]*?)\s*"
            + @"(?<subtypes>(?:[A-Z][a-z]+ )+)(?<types>(?:artifact |enchantment )*)creature tokens?"
            + @"(?: with (?<kw>[a-z ,]+?)|(?: with)? ""(?<text>[^""]+)"")?"
            + @"( for (?<foreach>each " + COUNTED + @"+))?$",
        RegexOptions.None)]
    private static partial Regex CreatureTokenLine();

    /// <summary>A granted ability printed as its own sentence after a token is created.</summary>
    [GeneratedRegex(
        @"^(?<head>.*\btokens?)\.\s+(?:They have|It has) ""(?<ability>[^""]+)""\.?$",
        RegexOptions.Singleline)]
    private static partial Regex GrantedTokenAbilityLine();

    [GeneratedRegex(
        @"^((?<who>you|each opponent|each player|target player|target opponent"
            + @"|its controller|that [a-z]+'s controller|the subject's controller) "
            + @"creates?|create) "
            + N
            + @"(?<tapped> tapped)?"
            + @" (?<kind>Treasure|Clue|Food|Gold|Blood|Lander|Map|Junk|Mutagen|Powerstone)"
            + @" tokens?" + FOREACH + @"$",
        RegexOptions.IgnoreCase)]
    private static partial Regex NamedTokenLine();

    [GeneratedRegex(@"^investigate$", RegexOptions.IgnoreCase)]
    private static partial Regex InvestigateLine();

    [GeneratedRegex(
        @"^(?<verb>counter|destroy|exile) (?<t>target [a-z0-9'’ ]+?) unless its controller pays "
            + @"(?<cost>(\{[^}]+\})+)\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex UnlessTheyPayLine();

    /// <remarks>
    /// Every wording of the same rider. The shuffle is something the search already did
    /// (CR 701.23e), so reading these costs nothing and refusing them loses the whole card — and
    /// the guard that something else was read first is what stops a bare "Shuffle your library"
    /// compiling to an empty spell.
    /// </remarks>
    [GeneratedRegex(
        @"^(if you (search|searched) your library this way, )?shuffle( your library)?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex BareShuffleLine();

    /// <remarks>
    /// The filter is optional: "search your library for a card" has none at all, and requiring
    /// one lost every unrestricted tutor.
    /// <para>
    /// "Reveal it" is matched and not read. Revealing changes nothing the engine models — the
    /// library is already hidden and the card is on its way somewhere public — but the words are
    /// on 345 cards, and refusing them cost every one of those a line. Requiring the words to be
    /// there rather than skipping any clause keeps this from claiming a search that does
    /// something else with what it finds.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"^(?<who>its controller may |that player may |that land's controller may "
            + @"|the subject's controller may )?"
            + @"searche?s? (?<whose>your|their) library for "
            + @"(an?|up to (?<n>one|two|three|four|five)|(?<any>any number of)) "
            + @"(?<what>[A-Za-z, ]+? )?cards?"
            + @"( named (?<named>[^,.]+?))?"
            + @"( with mana value (?<cap>\d+|X)( or (?<dir>less|greater))?)?"
            + @"(,? reveal (it|that card|them|those cards))?"
            + @"(,? put (it|that card|them|those cards) "
            + @"(?<where>onto the battlefield|into your hand|into your graveyard)"
            + @"(?<tapped> tapped)?)?"
            + @"(,? (then |and )?shuffle"
            + @"(?<ontop> and put (it|that card) on top( of your library)?)?"
            + @"| then shuffle your library)?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SearchLibraryLine();

    /// <remarks>
    /// The seek grammar, written against the search's so the two stay legible side by side. The
    /// tail is narrower on purpose: a seek puts the card in its controller's hand unless the
    /// sentence says the battlefield, and nothing else it might say is read.
    /// </remarks>
    [GeneratedRegex(
        @"^seeks? (an?|(?<n>two|three|four|five)) "
            + @"(?<what>[A-Za-z, ]+? )?cards?"
            + @"( named (?<named>[^,.]+?))?"
            + @"( with mana value (?<cap>\d+|X)( or (?<dir>less|greater))?)?"
            + @"( (and|then) put (it|that card|them|those cards) "
            + @"(?<where>onto the battlefield)(?<tapped> tapped)?)?\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SeekLine();

    /// <remarks>
    /// The subject is optional and the trailing full stop is not part of the mana, both for
    /// the same reason the shared verbs elsewhere accept them: a card prints "Add {G}" with
    /// no subject at all (CR 608.2) and "that player adds {G}" with one, and they are one
    /// sentence with a different pool at the end of it.
    /// <para>
    /// "An additional" is swallowed rather than read. It is what the mana is <em>beside</em>,
    /// not a fact about the mana: the trigger has already fired on the first lot, and this
    /// clause adds its own on top whatever the word in front of it.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"^" + WOPT + @"adds? (an additional )?(?<mana>.+?)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex AddManaLine();

    /// <remarks>
    /// Anchored whole, so the count belongs to this clause and the words after the colour
    /// are read rather than shrugged off. "Any type that land produced" carries its noun in
    /// a group only so the shape is visible in the parse; which permanent it means comes
    /// from the triggering event, not from the word.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<n>one|two|three|four|five|[0-9]+) mana "
            + @"(of any (one )?color"
            + @"|of any type that (?<produced>[a-z ]+) produced"
            + @"|(?<combination>in any combination of colors))$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ChosenColorManaLine();

    /// <remarks>
    /// Two shapes in one pattern, because they are one idea: "you may pay [cost]" followed by the
    /// branches, and "you may [do something]" where the offer and the consequence are the same
    /// sentence. Splitting them into two matchers would mean two copies of the branch reading.
    /// </remarks>
    /// <remarks>
    /// "When you do" is accepted alongside "If you do" and is not quite the same thing: the rules
    /// make it a reflexive triggered ability (CR 603.11), which goes on the stack and can be
    /// responded to between the payment and what it buys. Here it happens as the payment does.
    /// The deviation is the one evoke's sacrifice carries, and it is worth taking: the wording is
    /// on 43 cards whose effect the engine could already run.
    /// </remarks>
    /// <remarks>
    /// "Otherwise" is the other spelling of "if you don't" and is deliberately <em>not</em>
    /// accepted here. It was, for one measurement: the two corpus cards it reached are Insatiable
    /// Appetite and Pippin's Bravery, both of which write the decline branch as "…, target
    /// creature gets +5/+5 until end of turn. Otherwise, <em>that creature</em> gets +3/+3", and
    /// each branch is parsed on its own with an empty target list — so the pronoun in the second
    /// found nothing to point at and compiled to <c>PumpSourceUntilEndOfTurn</c>, pumping the
    /// instant. Two cards read, both of them silently inert on the branch that was added. The
    /// word is only safe here once a branch can see the targets its sibling chose.
    /// </remarks>
    [GeneratedRegex(
        @"^[Yy]ou may (pay (?<cost>(\{[^}]+\})+)|(?<free>[^.]+?))\.?"
            + @"(\s*(If|When) you do,\s*(?<do>[^.]+)\.?)?"
            + @"(\s*If you don't,\s*(?<dont>[^.]+)\.?)?\s*$",
        RegexOptions.None)]
    private static partial Regex MayPayLine();

    [GeneratedRegex(@"\{E\}", RegexOptions.IgnoreCase)]
    private static partial Regex EnergySymbol();

    [GeneratedRegex(@"^pay (?<n>\d+) life$", RegexOptions.IgnoreCase)]
    private static partial Regex PayLifeOffer();

    [GeneratedRegex(
        @"^" + T + @" can't (?<what>be blocked|block)( this turn)?$", RegexOptions.IgnoreCase)]
    private static partial Regex CantLine();

    [GeneratedRegex(
        @"^return ~ to (its owner's|your) hand$", RegexOptions.IgnoreCase)]
    private static partial Regex ReturnSelfToHand();

    [GeneratedRegex(@"^" + WOPT + @"gains? " + N + @" life$", RegexOptions.IgnoreCase)]
    private static partial Regex GainLife();

    [GeneratedRegex(
        @"^you gain life equal to the (life lost|damage dealt) this way$",
        RegexOptions.IgnoreCase)]
    private static partial Regex LifeLostThisWayLine();

    [GeneratedRegex(@"^" + WOPT + @"loses? " + N + @" life$", RegexOptions.IgnoreCase)]
    private static partial Regex LoseLife();

    /// <summary>
    /// "[Target] gets +N/+N and/or gains [keywords] until end of turn" — all four shapes at once.
    /// </summary>
    /// <remarks>
    /// One regex rather than a pump one and a grant one, because the card prints them as one
    /// sentence with either half optional and a separate matcher for each combination is three
    /// matchers that must agree about the target phrase.
    /// </remarks>
    [GeneratedRegex(
        @"^" + T + @" (gets (?<p>[+-](\d+|X))/(?<tough>[+-](\d+|X))"
            + @"( and gains (?<kw>[a-z ,]+?))?"
            + @"|gains (?<kw>[a-z ,]+?)) until end of turn$",
        RegexOptions.IgnoreCase)]
    private static partial Regex PumpOrGrantLine();

    /// <remarks>
    /// The keyword half is optional and lazy, so "gets +1/+0 and gains trample" is one sentence
    /// here rather than two matchers. It is spelled "gains" and not "has": the "has" form is a
    /// static ability that lasts as long as the permanent does, and reading it here would turn a
    /// printed anthem into a combat trick that wears off at cleanup.
    /// </remarks>
    [GeneratedRegex(
        @"^~ gets (?<p>[+-]\d+)/(?<tough>[+-]\d+)"
            + @"( and gains (?<kw>[a-z ,]+?))? until end of turn"
            + @"(?<mayattack> and can attack this turn as though it didn't have defender)?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex PumpSelf();

    [GeneratedRegex(
        @"^~ can't be blocked this turn$", RegexOptions.IgnoreCase)]
    private static partial Regex SelfUnblockableLine();

    [GeneratedRegex(
        @"^enchanted (creature|permanent) gets (?<p>[+-]\d+)/(?<tough>[+-]\d+) "
            + @"until end of turn$",
        RegexOptions.IgnoreCase)]
    private static partial Regex HostPumpLine();

    [GeneratedRegex(
        @"^return it to its owner's hand$", RegexOptions.IgnoreCase)]
    private static partial Regex ReturnSelfToHandLine();

    [GeneratedRegex(
        @"^~ gains (?<kw>[a-z ,]+?) until end of turn$", RegexOptions.IgnoreCase)]
    private static partial Regex SelfGrant();

    [GeneratedRegex(
        @"^it deals " + N + @" damage to " + T + @"$", RegexOptions.IgnoreCase)]
    private static partial Regex ItDealsDamage();

    [GeneratedRegex(
        @"^it deals " + N + @" damage to " + W + @"$", RegexOptions.IgnoreCase)]
    private static partial Regex ItDealsGroupDamage();

    /// <remarks>
    /// The tail is captured whole and handed back to the parser rather than described, because
    /// the point of the rewrite is that it names nothing: every shape of damage sentence already
    /// read - a number, "damage equal to its power", a group, a count - reaches this family for
    /// free. Describing the tail here would be a second copy of the damage grammar, and a second
    /// copy is a copy that falls behind.
    /// <para>
    /// Only the two subjects that can mean the source. "That creature" is printed too and is
    /// deliberately absent: it names the permanent a trigger was about, which is not what deals
    /// the damage in this engine.
    /// </para>
    /// </remarks>
    [GeneratedRegex(@"^have (?<who>it|~) deal (?<rest>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex HaveItDealLine();

    [GeneratedRegex(
        @"^put " + N + @" (?<kind>\+1/\+1|-1/-1|[a-z]+) counters? on "
            + @"(?<t>[A-Za-z0-9'’ ]+?)( for (?<foreach>each " + COUNTED + @"+))?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex PutCountersLine();

    /// <remarks>
    /// The subjects a counter is put on that are not targets. "It" and "that creature" are the
    /// same word in two grammars - one points back at a target the sentence chose, the other at
    /// what the trigger was about - and which one is meant depends on whether a target was
    /// chosen, so the sentence cannot tell them apart on its own.
    /// </remarks>
    [GeneratedRegex(
        @"^put " + N + @" (?<kind>[+-]\d/[+-]\d|[a-z]+) counters? on "
            + @"(?<who>~|it|that creature|that permanent|(enchanted|equipped) [a-z]+)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex PutCountersOnSubjectLine();

    [GeneratedRegex(
        @"^prevent the next " + N + @" damage that would be dealt to (?<t>[a-z0-9'’ ,]+?) this turn$",
        RegexOptions.IgnoreCase)]
    private static partial Regex PreventLine();

    /// <remarks>
    /// Only "dealt to". "Dealt by" and "dealt to and dealt by" name a different question and stay
    /// unread rather than being answered with the shield that happens to be available.
    /// </remarks>
    [GeneratedRegex(
        @"^the next " + N + @" damage that would be dealt to ~ this turn "
            + @"is dealt to (?<t>[a-z0-9'’ ,]+?) instead\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex RedirectLine();

    [GeneratedRegex(
        @"^prevent all (combat )?damage that would be dealt to (?<t>[a-z0-9'’ ,]+?) this turn$",
        RegexOptions.IgnoreCase)]
    private static partial Regex PreventAllToLine();

    /// <remarks>
    /// The whole tail is captured and taken apart afterwards rather than matched in place: the
    /// cards put "this turn" before the "to", after it and after the "by", and a pattern with an
    /// optional group in each of the three positions matches by backtracking into whichever one
    /// makes the rest fit, which is how a "by" clause ends up read as the thing being shielded.
    /// </remarks>
    [GeneratedRegex(
        @"^prevent all (?<kind>combat |noncombat )?damage that would be dealt(?<rest>[^.]*)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex PreventDescribedPassiveLine();

    /// <remarks>
    /// The active voice — "prevent all damage that creatures would deal to players this turn" —
    /// which names the dealer first and is otherwise the same sentence.
    /// </remarks>
    [GeneratedRegex(
        @"^prevent all (?<kind>combat |noncombat )?damage (that )?(?<by>[^.]+?) would deal(?<rest>[^.]*)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex PreventDescribedActiveLine();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

    /// <summary>
    /// A shield big enough that nothing in a turn exhausts it, which is what "all" means here.
    /// </summary>
    /// <remarks>
    /// Bounded rather than <c>int.MaxValue</c> so that two of these on one permanent still add up
    /// to a number, instead of overflowing into a shield that prevents nothing at all.
    /// </remarks>
    private const int AllDamage = 1_000_000;

    [GeneratedRegex(@"^adapt " + N + @"$", RegexOptions.IgnoreCase)]
    private static partial Regex AdaptLine();

    [GeneratedRegex(@"^monstrosity " + N + @"$", RegexOptions.IgnoreCase)]
    private static partial Regex MonstrosityLine();

    [GeneratedRegex(@"^scry " + N + @"$", RegexOptions.IgnoreCase)]
    private static partial Regex ScryLine();

    [GeneratedRegex(@"^surveil " + N + @"$", RegexOptions.IgnoreCase)]
    private static partial Regex SurveilLine();

    [GeneratedRegex(
        @"^regenerate (?<who>~|it|target [a-z ]+|(enchanted|equipped) [a-z]+)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex RegenerateLine();

    [GeneratedRegex(
        @"^that creature gets (?<p>[+-]\d+)/(?<tough>[+-]\d+) until end of turn$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ThatCreaturePumps();

    /// <remarks>
    /// Carries the same optional keyword tail as <see cref="PumpSelf"/>, and for the same
    /// measurement: the pronoun form of the pump-and-grant is the commoner of the two at 37
    /// corpus occurrences against 34. <see cref="ThatCreaturePumps"/> is tried first and has no
    /// tail, so "that creature gets +1/+1 and gains trample" reaches this reader rather than
    /// being cut in half by the shorter one.
    /// </remarks>
    [GeneratedRegex(
        @"^(it|that creature) gets (?<p>[+-]\d+)/(?<tough>[+-]\d+)"
            + @"( and gains (?<kw>[a-z ,]+?))? until end of turn$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ItPumps();

    [GeneratedRegex(
        @"^creatures you control get (?<p>[+-]\d+)/(?<tough>[+-]\d+) until end of turn$",
        RegexOptions.IgnoreCase)]
    private static partial Regex MassPump();
}

/// <summary>Reads the "when" half of a triggered ability into a predicate (CR 603.1).</summary>
public static partial class TriggerConditions
{
    /// <summary>
    /// The object a move is about, found by whichever of its two ids the state still knows.
    /// </summary>
    /// <remarks>
    /// A trigger predicate is handed the state from <em>one side</em> of the event, and which
    /// side depends on where its own source is (CR 603.6). So the object being moved exists under
    /// its old id in the state before and its new id in the state after, and a predicate that
    /// picks one is a predicate that fires on only half the cards it should. This bug has been
    /// found twice in this engine — once for tokens entering, once for sacrifices.
    /// </remarks>
    private static GameObject? CardOfMoved(GameState state, ObjectMoved moved) =>
        state.TryGetObject(moved.OldId, out var before) ? before
            : state.TryGetObject(moved.NewId, out var after) ? after
            : null;

    /// <summary>Who owned the card a move is about.</summary>
    private static Guid? OwnerOfMoved(GameState state, ObjectMoved moved) =>
        CardOfMoved(state, moved)?.OwnerId;

    /// <summary>Whether a player answers "you", "an opponent" or "a player".</summary>
    private static bool MatchesPlayer(string who, Guid candidate, Guid controller) => who switch
    {
        "you" => candidate == controller,
        "an opponent" => candidate != controller,
        _ => true,
    };

    /// <summary>
    /// What a card means by the kind of spell it counts (CR 205.2, 205.3m).
    /// </summary>
    /// <remarks>
    /// "Outlaw" is the one that is not a type at all: CR 700.12 defines it as any of five creature
    /// types, and the reason it is a list here rather than five conditions is that a Pirate Rogue
    /// is one outlaw. Counting the cards and asking each of them settles that; counting five
    /// tallies would not.
    /// </remarks>
    private static (Domain.Enums.CardType Types, string[] Subtypes)? SpellKind(string kind) =>
        kind switch
        {
            "enchantment" => (Domain.Enums.CardType.Enchantment, []),
            "artifact" => (Domain.Enums.CardType.Artifact, []),
            "instant" => (Domain.Enums.CardType.Instant, []),
            "sorcery" => (Domain.Enums.CardType.Sorcery, []),
            "instant or sorcery" =>
                (Domain.Enums.CardType.Instant | Domain.Enums.CardType.Sorcery, []),
            "outlaw" => (
                Domain.Enums.CardType.Creature,
                new[] { "Assassin", "Mercenary", "Pirate", "Rogue", "Warlock" }),
            _ => null,
        };

    /// <summary>Whether one card answers to a kind, asked the same way the count is.</summary>
    private static bool IsOfKind(
        Domain.Models.CardDefinition card, (Domain.Enums.CardType Types, string[] Subtypes) kind) =>
        (card.CardTypes & kind.Types) != Domain.Enums.CardType.None
        && (kind.Subtypes.Length == 0
            || card.Subtypes.Any(had => kind.Subtypes.Contains(had, StringComparer.OrdinalIgnoreCase)));

    /// <summary>A number a card prints in words, ordinal or cardinal.</summary>
    /// <remarks>
    /// One table for both, because the two readings never disagree where they overlap: "second"
    /// and "two" both mean 2, and a clause is either counting or ordering, never both.
    /// </remarks>
    private static int? Ordinal(string word) => word.ToLowerInvariant() switch
    {
        "first" or "one" => 1,
        "second" or "two" => 2,
        "third" or "three" => 3,
        "fourth" or "four" => 4,
        _ => null,
    };

    /// <summary>The predicate for a trigger condition, or null if it is not one we read.</summary>
    /// <summary>
    /// Whether a trigger on this condition hands its effects an object to call "that creature".
    /// </summary>
    /// <remarks>
    /// An allow-list, and it has to stay one. Every shape named here must be a shape for which
    /// <c>Game.SubjectObjectOf</c> actually returns something; a condition added here whose event
    /// carries no object would compile a sentence that silently does nothing, which is worse than
    /// the unread line it replaced. Adding a shape means checking both ends.
    /// <para>
    /// "Deals damage to a creature" is the first, and the corpus is unanimous about what the
    /// pronoun means there: thirty-five cards say "that creature" for the creature that was
    /// damaged, and every one that means the source names it outright.
    /// </para>
    /// </remarks>
    public static bool NamesAnObject(string condition)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var damage = DealsDamageTo().Match(condition);
        if (damage.Success)
        {
            return damage.Groups["victim"].Value.Equals(
                "a creature", StringComparison.OrdinalIgnoreCase);
        }

        // An Aura or Equipment's trigger is about the permanent it is attached to, which is never
        // the permanent with the ability - so "it" on one of these cards means the host and can
        // never mean the source. Saying so here is what stops a reader that can only aim at the
        // source from taking the sentence: Fiendlash ("whenever equipped creature is dealt damage,
        // it deals damage equal to its power to target player") compiled with the *Equipment* as
        // the damage source and the Equipment's power as the amount, which is no power at all, and
        // dealt nothing.
        //
        // Same allow-list discipline as the family below: a verb here may return true only where
        // Game.SubjectObjectOf really answers with the host, so that a pronoun the flag admits has
        // something to resolve to.
        var attached = AttachedCreature().Match(condition);
        if (attached.Success)
        {
            return attached.Groups["verb"].Value.ToLowerInvariant() switch
            {
                // One object each, and it is the host: the card that reached the graveyard, the
                // permanent that turned, the permanent the damage was marked on. Checked against
                // Game.SubjectObjectOf one event at a time - ObjectMoved, PermanentTapped and
                // DamageMarked all answer with the id this pronoun means.
                "dies" => true,
                "becomes tapped" => true,
                "is dealt damage" => true,

                // Untapping is the one that looks like its twin and is not: PermanentsUntapped
                // carries a set of ids and SubjectObjectOf answers nothing for it, so a pronoun
                // admitted here would resolve to nothing and the sentence would compile into an
                // effect that does nothing at all.
                "becomes untapped" => false,

                // Batches, exactly as below - a declaration names a set, and a set is what a
                // pronoun cannot mean. Named rather than left to the default so that a verb added
                // to the pattern has to be considered here.
                "attacks" => false,
                "blocks" => false,
                "attacks or blocks" => false,
                "becomes blocked" => false,

                // The damage verbs name the host as the *source* of the damage, and the event's
                // object is whoever took it - two different permanents, and this question is
                // about neither reliably. Refused rather than guessed.
                _ => false,
            };
        }

        // "Whenever a player taps a land for mana" - the event names the permanent that made
        // the mana, and Game.SubjectObjectOf answers with it, so "its controller" and "that
        // land" each have exactly one thing they can mean. Admitted with the same discipline
        // as the families below: the pronoun resolves to that land or to nobody, and never
        // falls back to the permanent with the ability.
        if (TappedForManaLine().IsMatch(condition))
            return true;

        // CR 702.140c: a mutation is one spell merging with one creature, and the event names
        // that creature - so "put a +1/+1 counter on it" and "put a +1/+1 counter on that
        // creature" both have exactly one thing they can mean. Admitted with the same discipline
        // as the families below: Game.SubjectObjectOf really does answer with that permanent's
        // id, and the id is still the permanent's afterwards because merging does not make a new
        // object (CR 730.2c).
        if (MutatesLine().IsMatch(condition))
            return true;

        // The zone-change family, and it is admitted one verb at a time rather than whole. Most
        // of its verbs name an event carrying exactly one object - the permanent that entered,
        // the card that reached the graveyard, the permanent that tapped - and for those the
        // sentence's "that creature" can only mean it. But "blocks" and "becomes blocked" are in
        // the same pattern and name no object at all, and admitting the family wholesale would
        // compile "whenever ~ blocks a creature, destroy that creature" into a trigger that
        // silently does nothing. That is the card this whole mechanism exists to keep failing.
        var zoneChange = ZoneChangeLine().Match(condition);

        return zoneChange.Success
            && zoneChange.Groups["verb"].Value.ToLowerInvariant() switch
            {
                // The new object, in the zone it arrived in (CR 400.7): a dies trigger's "that
                // creature" is the card now in the graveyard, and that is the id the event
                // carries.
                "enters" => true,
                "dies" => true,
                "leaves the battlefield" => true,
                "is put into a graveyard from the battlefield" => true,
                "is put into a graveyard from anywhere" => true,
                "becomes tapped" => true,

                // Untapping is the one that looks like its twin and is not, exactly as the
                // attached family two hundred lines above already says: CR 502.2 turns them all
                // at once, PermanentsUntapped carries a set of ids, and Game.SubjectObjectOf
                // answers nothing for it. This arm said true and the two lists disagreed, so a
                // pronoun admitted here resolved to nothing and the sentence compiled into an
                // effect that does nothing - which is the failure the whole allow-list exists to
                // prevent.
                "becomes untapped" => false,

                // Named to be refused rather than left to the default, so that a verb added to
                // the pattern later has to be considered here rather than quietly admitted.
                "blocks" => false,
                "becomes blocked" => false,

                // Attackers are declared as a batch (CR 508.1), and one attacker is the case the
                // event can answer: Game.SubjectObjectOf names the creature when the declaration
                // holds exactly one and nothing when it holds several. That is enough here and
                // was not before, because the subject this flag admits is the strict one - it
                // resolves to the attacker or to nobody, and can never fall back to the permanent
                // with the ability. Refusing it was the worse of the two: "whenever a creature
                // attacks you, it gets -1/-0" then read "it" as the enchantment and shrank a card
                // that is not a creature.
                "attacks" => true,
                _ => false,
            };
    }

    public static Func<GameEvent, GameState, TriggerSource, bool>? Parse(string condition)
    {
        ArgumentNullException.ThrowIfNull(condition);

        // "Whenever ~ attacks **while saddled**" - a clause about the board tacked onto a clause
        // about an event. The two are read separately and joined, because the half in front is an
        // ordinary trigger condition and the half behind is an ordinary board condition, and
        // teaching either grammar about the other would be a third grammar.
        //
        // A "while" the board reader cannot answer leaves the whole condition unread rather than
        // dropping the qualifier: a trigger that fires whenever it is *not* saddled as well is a
        // strictly better card than the one printed.
        var qualified = WhileLine().Match(condition);
        if (qualified.Success)
        {
            var meanwhile = qualified.Groups["while"].Value.Trim();

            // "While saddled" has no subject, because the sentence already named one: the
            // permanent whose trigger this is. Asked plainly first, so a clause that does name
            // its own subject still reaches the reader that expects one.
            var holds = BoardConditions.Parse(meanwhile)
                ?? BoardConditions.Parse("~ is " + meanwhile);

            if (Parse(qualified.Groups["when"].Value.Trim()) is not { } fires || holds is null)
                return null;

            return (e, state, source) =>
                fires(e, state, source) && holds(state, source.Abilities, source);
        }

        if (TryPhase(condition) is { } phase)
            return phase;

        if (EntersLine().IsMatch(condition))
            return (e, state, source) => Entered(e, state)?.Id == source.Id;

        if (DiesLine().IsMatch(condition))
        {
            return (e, _, source) =>
                e is ObjectMoved { From: Zone.Battlefield, To: Zone.Graveyard } m
                && m.OldId == source.Id;
        }

        // "When this creature exploits a creature" (CR 702.110b). Its own event rather than a
        // sacrifice, because sacrifices happen for all sorts of reasons and nothing about the
        // move says which of them this was.
        if (ExploitsLine().IsMatch(condition))
        {
            return (e, _, source) =>
                e is CreatureExploited exploited && exploited.ExploiterId == source.Id;
        }

        // "~ and at least two other creatures attack" - the same declaration, counted. The
        // batch is one event carrying every attacker (CR 508.1), so the count is there to be
        // read; what was missing was the sentence asking for it.
        var withOthers = AttacksWithOthersLine().Match(condition);
        if (withOthers.Success)
        {
            var others = withOthers.Groups["n"].Value.ToLowerInvariant() switch
            {
                "one" => 1,
                "two" => 2,
                "three" => 3,
                "four" => 4,
                "five" => 5,
                var digits => int.Parse(digits, CultureInfo.InvariantCulture),
            };

            return (e, _, source) =>
                e is AttackersDeclared batch
                && batch.Attackers.ContainsKey(source.Id)
                && batch.Attackers.Count >= others + 1;
        }

        // "A Samurai or Warrior you control attacks alone" - the same count with the sole
        // attacker having to answer to a description rather than be this card. The filter is the
        // vocabulary tutors and digs share, which is what makes "Samurai or Warrior" two filters
        // instead of a phrase needing its own reader.
        var groupAlone = GroupAttacksAloneLine().Match(condition);
        if (groupAlone.Success)
        {
            var filter = string.Join(
                '|',
                groupAlone.Groups["types"].Value
                    .Replace(", or ", ",", StringComparison.OrdinalIgnoreCase)
                    .Replace(" or ", ",", StringComparison.OrdinalIgnoreCase)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            return (e, state, source) =>
            {
                if (e is not AttackersDeclared batch || batch.Attackers.Count != 1)
                    return false;

                var only = batch.Attackers.Keys.First();

                return state.TryGetObject(only, out var attacker)
                    && attacker.ControllerId == source.ControllerId
                    && Abilities.SearchFilters.Matches(filter, attacker.Card);
            };
        }

        // "~ enters or leaves the battlefield" - two zone changes named together, and the second
        // is any departure rather than a death: exiled, bounced and sacrificed all count.
        if (EntersOrLeavesLine().IsMatch(condition))
        {
            return (e, state, source) => e switch
            {
                ObjectMoved { To: Zone.Battlefield } arrived => arrived.NewId == source.Id,
                ObjectMoved { From: Zone.Battlefield } left => left.OldId == source.Id,
                ObjectCreated { Zone: Zone.Battlefield } made => made.Id == source.Id,
                _ => false,
            };
        }

        // "~ attacks alone" - it is the only attacker (CR 506.3c). Counted the same way, and the
        // opposite question to the one above.
        if (AttacksAloneLine().IsMatch(condition))
        {
            return (e, _, source) =>
                e is AttackersDeclared only
                && only.Attackers.Count == 1
                && only.Attackers.ContainsKey(source.Id);
        }

        // "Whenever enchanted player is attacked" - the Curse family. Attackers are declared as
        // one batch saying who each was declared against (CR 508.1b), so the trigger fires on
        // the declaration when any of them was aimed at the player the source enchants - once
        // per declaration however many attackers, the same simplification every batch trigger
        // here makes. Aimed at the *player*: a creature attacking their planeswalker is not
        // attacking them, the same line the "attacks you" family draws. An Aura on nobody
        // enchants no player, and then nothing in the declaration can answer.
        if (EnchantedPlayerAttackedLine().IsMatch(condition))
        {
            return (e, _, source) =>
                e is AttackersDeclared declared
                && source.Permanent?.AttachedToPlayer is { } enchanted
                && declared.Attackers.Values.Any(
                    attack => !attack.IsPlaneswalker && attack.DefendingPlayer == enchanted);
        }

        if (AttacksLine().IsMatch(condition))
        {
            // CR 508.1: attackers are declared as one batch, so the trigger fires on the
            // declaration and asks whether this creature was among them.
            return (e, _, source) =>
                e is AttackersDeclared declared && declared.Attackers.ContainsKey(source.Id);
        }

        // "You cycle or discard a card" - one event, not two. Cycling discards the card as its
        // cost (CR 702.29c), so the discard covers both halves; watching the activation as well
        // would fire twice for a single cycle, which is a different card from the one printed.
        if (CycleOrDiscardLine().IsMatch(condition))
        {
            return (e, _, source) =>
                e is ObjectMoved { To: Zone.Graveyard, Cause: MoveCause.Discard } discarded
                && discarded.ControllerId == source.ControllerId;
        }

        // "You cycle ~" - cycling is an activated ability with a known id (CR 702.29a), so the
        // trigger is the activation of that ability on this card. Nothing new is needed to make
        // it fire; what was missing was only the sentence naming it.
        if (CyclesLine().IsMatch(condition))
        {
            return (e, _, source) =>
                e is AbilityActivated { AbilityId: "cycling" } cycled
                && cycled.SourceId == source.Id;
        }

        // "You roll one or more dice" and "you roll a die" are one event here: a roll
        // instruction rolls one die for as long as nothing reads the multi-dice instructions,
        // so the two wordings cannot yet come apart. The day "roll two d6" compiles, the
        // per-die wording fires once per die kept (CR 706.6 - ignored dice never happened).
        if (RollsDiceCondition().IsMatch(condition))
            return (e, _, source) => e is DiceRolled rolled && rolled.PlayerId == source.ControllerId;

        // "You roll a die's highest natural result" - the face, not the modified result
        // (CR 706.2), which is why the event records both.
        if (RollsHighestCondition().IsMatch(condition))
        {
            return (e, _, source) =>
                e is DiceRolled rolled
                && rolled.PlayerId == source.ControllerId
                && rolled.Natural == rolled.Sides;
        }

        // "You roll a 6", "you roll a 1 or 2", "you roll a 3 or higher" - conditions on the
        // number that came up (CR 706.4).
        var rolledN = RollsNumberCondition().Match(condition);
        if (rolledN.Success)
        {
            var wanted = int.Parse(rolledN.Groups["n"].Value, CultureInfo.InvariantCulture);
            var orAlso = rolledN.Groups["m"].Success
                ? int.Parse(rolledN.Groups["m"].Value, CultureInfo.InvariantCulture)
                : (int?)null;
            var orMore = rolledN.Groups["dir"].Success;

            return (e, _, source) =>
                e is DiceRolled rolled
                && rolled.PlayerId == source.ControllerId
                && (orMore
                    ? rolled.Result >= wanted
                    : rolled.Result == wanted || rolled.Result == orAlso);
        }

        if (TryZoneChange(condition) is { } zoneChange)
            return zoneChange;

        if (TryCast(condition) is { } cast)
            return cast;

        var caught = BecomesBlocked().Match(condition);
        if (caught.Success)
        {
            // CR 509.1h: an attacker becomes blocked when at least one creature is declared as
            // blocking it. Being named in the declaration with an empty list is not blocked.
            if (!BlockingCreature(caught.Groups["what"], out var blocker))
                return null;

            return (e, state, source) =>
                e is BlockersDeclared declared
                && declared.Blockers.TryGetValue(source.Id, out var by)
                && !by.IsEmpty
                && (blocker is null || by.Any(id => Describes(blocker, state, source, id)));
        }

        var blocks = BlocksLine().Match(condition);
        if (blocks.Success)
        {
            // "~ blocks a creature with flying" - the description is of the attacker, on the
            // other side of the declaration from the source.
            if (!BlockingCreature(blocks.Groups["what"], out var attacker))
                return null;

            return (e, state, source) =>
                e is BlockersDeclared declared
                && declared.Blockers.Any(pair =>
                    pair.Value.Contains(source.Id)
                    && (attacker is null || Describes(attacker, state, source, pair.Key)));
        }

        if (AttacksOrBlocks().IsMatch(condition))
        {
            return (e, _, source) => e switch
            {
                AttackersDeclared declared => declared.Attackers.ContainsKey(source.Id),
                BlockersDeclared declared =>
                    declared.Blockers.Values.Any(list => list.Contains(source.Id)),
                _ => false,
            };
        }

        if (EntersOrAttacks().IsMatch(condition))
        {
            return (e, state, source) =>
                Entered(e, state)?.Id == source.Id
                || (e is AttackersDeclared declared && declared.Attackers.ContainsKey(source.Id));
        }

        if (YouAttack().IsMatch(condition))
        {
            // CR 508.1: "whenever you attack" is one trigger for the whole declaration, however
            // many creatures were in it — not one per attacker.
            return (e, state, source) =>
                e is AttackersDeclared { Attackers.IsEmpty: false }
                && state.ActivePlayerId == source.ControllerId;
        }

        // "Whenever you attack with one or more creatures with counters on them", "with two or
        // more legendary creatures", "with one or more Elves" — the same declaration and the
        // same once-per-combat rule, counting only the attackers a group phrase describes.
        //
        // The group goes through the shared target grammar, so every noun it reads arrives here
        // working and a noun it cannot read leaves the line unread rather than firing on
        // anything. That refusal is the point: a trigger that ignored its own condition would
        // fire on every attack, which is a strictly better card than the one printed and nothing
        // downstream could tell.
        var attackWith = YouAttackWith().Match(condition);
        if (attackWith.Success
            && EffectPhrase.Specs.ParseGroup(attackWith.Groups["what"].Value.Trim())
                is { Kind: TargetKind.Permanent, ObjectFilter: not null } attackers)
        {
            var least = EffectPhrase.Number(attackWith.Groups["n"].Value).Fixed;

            return (e, state, source) =>
                e is AttackersDeclared declared
                && state.ActivePlayerId == source.ControllerId
                && declared.Attackers.Keys.Count(
                    id => Describes(attackers, state, source, id)) >= least;
        }

        if (YouCastThis().IsMatch(condition))
        {
            // The card is on the stack as a spell when this triggers, and a spell is a new object
            // (CR 400.7) — so it is matched by the card it was cast from rather than by id.
            return (e, state, source) =>
                e is SpellCastEvent cast
                && cast.PlayerId == source.ControllerId
                && state.TryGetObject(cast.StackId, out var spell)
                && string.Equals(spell.Card.OracleId, source.Card.OracleId, StringComparison.Ordinal);
        }

        if (BecomesTapped().IsMatch(condition))
            return (e, _, source) => e is PermanentTapped tapped && tapped.Id == source.Id;

        if (BecomesUntapped().IsMatch(condition))
        {
            return (e, _, source) =>
                e is PermanentsUntapped untapped && untapped.Ids.Contains(source.Id);
        }

        if (LeavesTheBattlefield().IsMatch(condition))
        {
            // CR 603.6c: a leave-the-battlefield ability looks back in time, and the engine hands
            // it the state as it was — so the source is found under the id it had before the move.
            return (e, _, source) =>
                e is ObjectMoved { From: Zone.Battlefield } gone
                && gone.To != Zone.Battlefield
                && gone.OldId == source.Id;
        }

        if (EntersOrDies().IsMatch(condition))
        {
            return (e, state, source) =>
                Entered(e, state)?.Id == source.Id
                || (e is ObjectMoved { From: Zone.Battlefield, To: Zone.Graveyard } dead
                    && dead.OldId == source.Id);
        }

        if (AttacksUnblocked().IsMatch(condition))
        {
            // CR 509.1h: unblocked-ness is settled by the declaration of blockers, so that is the
            // event to watch — not the attack, which happens a step earlier and cannot know yet.
            //
            // Read off the *event* and not off the state, which is the half this had wrong.
            // Trigger conditions are asked against the game as it was before the event applied
            // (CR 603.6), and before a declaration of blockers nothing is blocked — so the state
            // question answered "unblocked" for every attacker in the batch and the ability fired
            // whether or not somebody had just blocked it. Nothing noticed because no card using
            // this condition had ever compiled: the effect sentence beside it was unread, so the
            // whole family sat in the work queue with a trigger that would have played the cards
            // strictly better than printed.
            return (e, state, source) =>
                e is BlockersDeclared declared
                && state.Combat.Attackers.ContainsKey(source.Id)
                && !(declared.Blockers.TryGetValue(source.Id, out var stoppedBy)
                    && !stoppedBy.IsEmpty);
        }

        var dealsDamage = DealsDamageTo().Match(condition);
        if (dealsDamage.Success)
        {
            var combatOnly = dealsDamage.Groups["combat"].Success;
            var victim = dealsDamage.Groups["victim"].Value.ToLowerInvariant();

            return (e, _, source) => victim switch
            {
                // The combat check belongs here as much as it does on the two arms below, and
                // it was missing: "whenever this creature deals combat damage to a creature" fired
                // on a ping, a fight, a damage-based removal spell - anything at all that this
                // permanent damaged. The word "combat" was read off the card, stored, and then
                // asked of players only. Nothing failed; the trigger simply went off too often.
                "a creature" => e is DamageMarked marked
                    && marked.SourceId == source.Id
                    && (!combatOnly || marked.IsCombat),
                "an opponent" => e is PlayerDamaged hit
                    && hit.SourceId == source.Id
                    && hit.PlayerId != source.ControllerId
                    && (!combatOnly || hit.IsCombat),
                _ => e is PlayerDamaged any
                    && any.SourceId == source.Id
                    && (!combatOnly || any.IsCombat),
            };
        }

        if (YouDrawACard().IsMatch(condition))
        {
            return (e, _, source) =>
                e is ObjectMoved { From: Zone.Library, To: Zone.Hand, Cause: MoveCause.Draw } drawn
                && drawn.ControllerId == source.ControllerId;
        }

        var wounded = IsDealtDamage().Match(condition);
        if (wounded.Success)
        {
            // "Whenever ~ is dealt combat damage" is the same trigger with one more term, and
            // the term was the only thing between the compiler and Wall of Essence, Pious Warrior
            // and Wall of Souls: the plain form had read for months and the combat form went to
            // the unread pile beside it. DamageMarked has carried IsCombat all along.
            var combatOnly = wounded.Groups["combat"].Success;

            return (e, _, source) => e is DamageMarked hit
                && hit.Id == source.Id
                && (!combatOnly || hit.IsCombat);
        }

        var castTargeting = CastSpellTargeting().Match(condition);
        if (castTargeting.Success)
        {
            var mine = castTargeting.Groups["who"].Value
                .StartsWith("you", StringComparison.OrdinalIgnoreCase);

            // CR 601.2c: targets are chosen as the spell goes on the stack, which is the same
            // moment the cast happens — so the trigger reads the target choice, not the cast.
            return (e, state, source) =>
                e is TargetsChosen chosen
                && chosen.Targets.Any(t => t.Subject == source.Id)
                && state.TryGetObject(chosen.StackId, out var spell)
                && spell.Zone == Zone.Stack
                && spell.Ability is null
                && (!mine || spell.ControllerId == source.ControllerId);
        }

        if (BecomesTargeted().IsMatch(condition))
        {
            // CR 603.2c: the trigger is on being chosen as a target, which happens once as the
            // spell or ability is put on the stack (CR 601.2c) rather than on resolution.
            return (e, _, source) =>
                e is TargetsChosen chosen
                && chosen.Targets.Any(t => t.Subject == source.Id);
        }

        var life = GainsOrLosesLife().Match(condition);
        if (life.Success)
        {
            var gaining = life.Groups["verb"].Value.StartsWith("gain", StringComparison.OrdinalIgnoreCase);
            var mine = life.Groups["who"].Value.StartsWith("you", StringComparison.OrdinalIgnoreCase);

            return (e, _, source) =>
                e is LifeChanged changed
                && (gaining ? changed.Delta > 0 : changed.Delta < 0)
                && (mine
                    ? changed.PlayerId == source.ControllerId
                    : changed.PlayerId != source.ControllerId);
        }

        var attached = AttachedCreature().Match(condition);
        if (attached.Success)
        {
            // "Enchanted creature" and "equipped creature" name whatever this permanent is
            // attached to (CR 701.3c), so the trigger is about a permanent the source points at
            // rather than about the source itself.
            var verb = attached.Groups["verb"].Value.ToLowerInvariant();

            return (e, state, source) =>
            {
                if (source.Permanent?.AttachedTo is not { } host)
                    return false;

                return verb switch
                {
                    "dies" => e is ObjectMoved { From: Zone.Battlefield, To: Zone.Graveyard } m
                        && m.OldId == host,
                    "attacks" => e is AttackersDeclared declared
                        && declared.Attackers.ContainsKey(host),
                    "blocks" => e is BlockersDeclared blocked
                        && blocked.Blockers.Values.Any(list => list.Contains(host)),
                    "attacks or blocks" => e switch
                    {
                        AttackersDeclared a => a.Attackers.ContainsKey(host),
                        BlockersDeclared b => b.Blockers.Values.Any(list => list.Contains(host)),
                        _ => false,
                    },
                    "becomes tapped" => e is PermanentTapped tapped && tapped.Id == host,
                    "becomes untapped" => e is PermanentsUntapped untapped
                        && untapped.Ids.Contains(host),
                    "is dealt damage" => e is DamageMarked hit && hit.Id == host,
                    "becomes blocked" => e is BlockersDeclared caught
                        && caught.Blockers.TryGetValue(host, out var by) && !by.IsEmpty,

                    // The damage verbs, which vary by two things the way they do for a source
                    // naming itself: whether the damage must be combat damage, and whether the
                    // sentence names who took it. A sentence naming nobody means anybody.
                    "deals combat damage to a player" =>
                        e is PlayerDamaged { IsCombat: true } dealt && dealt.SourceId == host,
                    "deals combat damage" => e switch
                    {
                        PlayerDamaged { IsCombat: true } any => any.SourceId == host,
                        DamageMarked { IsCombat: true } marked => marked.SourceId == host,
                        _ => false,
                    },
                    "deals damage" => e switch
                    {
                        PlayerDamaged any => any.SourceId == host,
                        DamageMarked marked => marked.SourceId == host,
                        _ => false,
                    },
                    "deals damage to a player" =>
                        e is PlayerDamaged hitPlayer && hitPlayer.SourceId == host,

                    // "To an opponent" is the same event with one more question: whose life it
                    // was. Asked of the ability's controller, because "opponent" is said from
                    // the point of view of whoever controls the Aura, not of the creature.
                    "deals damage to an opponent" =>
                        e is PlayerDamaged theirs
                        && theirs.SourceId == host
                        && theirs.PlayerId != source.ControllerId,

                    _ => false,
                };
            };
        }

        // "You cast your second spell each turn", "an opponent casts their first noncreature
        // spell each turn", "you draw your second card each turn" — one shape, and the only two
        // things the corpus counts this way.
        var nth = NthEachTurn().Match(condition);
        if (nth.Success)
        {
            var wanted = Ordinal(nth.Groups["ord"].Value);
            var spells = nth.Groups["what"].Value.StartsWith(
                "spell", StringComparison.OrdinalIgnoreCase);

            var whose = nth.Groups["who"].Value.ToLowerInvariant();
            var kind = nth.Groups["kind"].Value.Trim().ToLowerInvariant();

            if (wanted is null)
                return null;

            // The count is read from the state *before* the event, so this one is added back to
            // ask "is this the Nth". Reading it after would need the state the trigger is not
            // given, and off-by-one here is the difference between a card that never fires and
            // one that fires a turn early. The spell itself *is* in that state — it goes on the
            // stack before the cast event is emitted, which is how the reducer counts its type.
            return (e, state, source) =>
            {
                if (spells)
                {
                    if (e is not SpellCastEvent cast
                        || !MatchesPlayer(whose, cast.PlayerId, source.ControllerId))
                    {
                        return false;
                    }

                    // What was cast is asked as well as how many came before it. A count alone
                    // would fire "your first creature spell each turn" on an instant, since the
                    // instant is the first creature spell's predecessor rather than the spell
                    // the sentence is about.
                    if (kind.Length > 0)
                    {
                        if (!state.TryGetObject(cast.StackId, out var spell))
                            return false;

                        if (kind is "creature" or "noncreature")
                        {
                            var isCreature = spell.Card.CardTypes.HasFlag(
                                Domain.Enums.CardType.Creature);

                            if (isCreature != kind.Equals("creature", StringComparison.Ordinal))
                                return false;
                        }
                        else if (SpellKind(kind) is not { } asked
                            || !IsOfKind(spell.Card, asked))
                        {
                            return false;
                        }
                    }

                    var caster = state.GetPlayer(cast.PlayerId);

                    // Creature spells are the difference between the two counts rather than a
                    // third one, which is the same derivation the board conditions make — one
                    // fewer number on the player, and no way for the two to disagree.
                    var already = kind switch
                    {
                        "noncreature" => caster.NoncreatureSpellsCastThisTurn,
                        "creature" =>
                            caster.SpellsCastThisTurn - caster.NoncreatureSpellsCastThisTurn,
                        "" => caster.SpellsCastThisTurn,

                        // Counted off the cards the player actually cast, so that a spell
                        // answering to two of the words the kind names is still one spell. The
                        // null arm cannot be reached — the same table refused the sentence above —
                        // and answers zero rather than throwing, because a predicate that throws
                        // in the middle of a game is a worse failure than one that never fires.
                        _ => SpellKind(kind) is { } counted
                            ? caster.SpellsCastThisTurnOfKind(counted.Types, counted.Subtypes)
                            : 0,
                    };

                    return already + 1 == wanted;
                }

                return e is ObjectMoved { Cause: MoveCause.Draw, To: Zone.Hand } drawn
                    && state.TryGetObject(drawn.OldId, out var card)
                    && MatchesPlayer(whose, card.OwnerId, source.ControllerId)
                    && state.GetPlayer(card.OwnerId).CardsDrawnThisTurn + 1 == wanted;
            };
        }

        // "A land enters under your control" is "a land you control enters" with the words in a
        // different order. Rewritten rather than given a matcher, so the whole enters-family —
        // another, types, subtypes — arrives in this wording too without being written twice.
        var enteringUnder = EntersUnderControlLine().Match(condition);
        if (enteringUnder.Success)
        {
            var whose = enteringUnder.Groups["whose"].Value.StartsWith(
                "your", StringComparison.OrdinalIgnoreCase)
                ? " you control"
                : " an opponent controls";

            return Parse(
                enteringUnder.Groups["head"].Value + enteringUnder.Groups["what"].Value.Trim()
                    + whose + " enters");
        }

        if (OpponentDrawsLine().IsMatch(condition))
        {
            return (e, state, source) =>
                e is ObjectMoved { Cause: MoveCause.Draw, To: Zone.Hand } drawn
                && CardOfMoved(state, drawn) is { } taken
                && taken.OwnerId != source.ControllerId;
        }

        var manyAttack = AttackWithManyLine().Match(condition);
        if (manyAttack.Success)
        {
            // "Two" here is a count, not an ordinal, but the words are the same and the ordinal
            // table already reads them — first/second/third are 1/2/3 either way round.
            var needed = Ordinal(manyAttack.Groups["n"].Value)
                ?? int.Parse(manyAttack.Groups["n"].Value, CultureInfo.InvariantCulture);

            return (e, state, source) =>
                e is AttackersDeclared declared
                && declared.Attackers.Keys.Count(id =>
                    state.TryGetObject(id, out var attacker)
                    && attacker.ControllerId == source.ControllerId) >= needed;
        }

        // "Whenever you put a counter on a creature", "whenever one or more +1/+1 counters are
        // put on this creature" — the same event in two voices, and the passive one is how a
        // card says it does not care who did it. Only counters going *on* count: removing one is
        // the same event with a negative delta. "One or more" needs nothing extra here, unlike
        // the plural combat trigger, because counters arrive on one permanent per event however
        // many of them there are.
        var putting = PutsACounterLine().Match(condition);
        if (putting.Success)
        {
            var kind = putting.Groups["kind"].Success
                ? putting.Groups["kind"].Value.Trim()
                : null;

            var onSelf = putting.Groups["on"].Value.Equals("~", StringComparison.Ordinal);
            var mine = putting.Groups["side"].Success
                || putting.Groups["on"].Value.StartsWith("you", StringComparison.OrdinalIgnoreCase);

            return (e, state, source) =>
            {
                if (e is not CountersChanged { Delta: > 0 } added)
                    return false;

                // A named kind is the whole point of the sentence: "+1/+1 counters are put on"
                // is not satisfied by a stun counter, and answering it from any counter at all
                // would be the wider-number mistake in a different suit.
                if (kind is not null
                    && !added.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (onSelf)
                    return added.Id == source.Id;

                return state.TryGetObject(added.Id, out var gained)
                    && (!mine || gained.ControllerId == source.ControllerId);
            };
        }

        // A batch of clauses that are each one question about one event. They are written here
        // rather than as separate matchers because that is all they are — the shared grammar
        // earns its keep on phrases with parts, and these have none.
        if (BecomesMonstrousLine().IsMatch(condition))
        {
            return (e, _, source) =>
                e is BecameMonstrous monstrous && monstrous.Id == source.Id;
        }

        // CR 702.140d: "an ability that triggers whenever a creature mutates triggers when a
        // spell merges with a creature as a result of a resolving mutating creature spell" -
        // which is exactly the event, and nothing else produces one.
        var mutates = MutatesLine().Match(condition);
        if (mutates.Success)
        {
            var itself = mutates.Groups["who"].Value.Equals("~", StringComparison.Ordinal);

            return (e, state, source) =>
            {
                if (e is not PermanentMutated merged)
                    return false;

                if (itself)
                    return merged.Id == source.Id;

                // CR 613.1b: whose creature it is, is a computed characteristic. Asked of the
                // permanent that mutated rather than of its stored controller, because a creature
                // an opponent has taken is not one you control however it started.
                return state.TryGetObject(merged.Id, out var creature)
                    && Characteristics.Of(state, source.Abilities, creature).ControllerId
                        == source.ControllerId;
            };
        }

        if (TurnedFaceUpLine().IsMatch(condition))
        {
            return (e, _, source) =>
                e is PermanentTurned { FaceDown: false } turned && turned.Id == source.Id;
        }

        var discards = DiscardsACardLine().Match(condition);
        if (discards.Success)
        {
            var who = discards.Groups["who"].Value.ToLowerInvariant();

            return (e, state, source) =>
                e is ObjectMoved { Cause: MoveCause.Discard, To: Zone.Graveyard } discarded
                && OwnerOfMoved(state, discarded) is { } thrown
                && MatchesPlayer(who, thrown, source.ControllerId);
        }

        var sacrifices = SacrificesLine().Match(condition);
        if (sacrifices.Success)
        {
            var who = sacrifices.Groups["who"].Value.ToLowerInvariant();
            var printed = sacrifices.Groups["what"].Value.Trim();

            // The noun was matched and thrown away, so "whenever you sacrifice a creature" fired
            // on sacrificing a land - every card of this shape was reading a sentence it did not
            // then obey. It is a permanent type where the words name one and a subtype otherwise,
            // told apart by the capital exactly as the target grammar does.
            var required = EffectPhrase.Specs.PermanentTypes(printed.ToLowerInvariant());
            var subtype = required is null && printed.Length > 1 && char.IsUpper(printed[0])
                ? printed
                : null;

            if (required is null && subtype is null)
                return null;

            // "Another" excludes the permanent whose ability this is (CR 109.5).
            var other = sacrifices.Groups["another"].Success;

            return (e, state, source) =>
                e is ObjectMoved { Cause: MoveCause.Sacrifice, From: Zone.Battlefield } given
                && OwnerOfMoved(state, given) is { } gone
                && MatchesPlayer(who, gone, source.ControllerId)
                && (!other || given.OldId != source.Id)
                && CardOfMoved(state, given) is { } offering
                && (required is null
                    || required.All(type => offering.Card.CardTypes.HasFlag(type)))
                && (subtype is null
                    || offering.Card.Subtypes.Contains(subtype, StringComparer.OrdinalIgnoreCase));
        }

        // "Whenever a permanent you control is put into a graveyard" — the long way of saying
        // dies, and it covers artifacts and enchantments where "dies" is only about creatures.
        var buried = PutIntoGraveyardLine().Match(condition);
        if (buried.Success)
        {
            var required = EffectPhrase.Specs.PermanentTypes(
                buried.Groups["what"].Value.Trim().ToLowerInvariant());

            return (e, state, source) =>
                e is ObjectMoved { From: Zone.Battlefield, To: Zone.Graveyard } lost
                && CardOfMoved(state, lost) is { } corpse
                && corpse.OwnerId == source.ControllerId
                && (required is null
                    || required.All(type => corpse.Card.CardTypes.HasFlag(type)));
        }

        // "A creature you control attacks alone" and "~ attacks alone" are two different
        // sentences and were being read as one. The first does not say the source attacked - the
        // creature with exalted usually stays home - so requiring it made exalted fire only when
        // the exalted creature was itself the lone attacker, which is the common case and
        // therefore the one that hides the bug.
        //
        // The typed form above ("a Samurai you control attacks alone") had it right all along,
        // and this is the same check without the type filter.
        if (AnyCreatureAttacksAlone().IsMatch(condition))
        {
            return (e, state, source) =>
            {
                if (e is not AttackersDeclared batch || batch.Attackers.Count != 1)
                    return false;

                return state.TryGetObject(batch.Attackers.Keys.First(), out var attacker)
                    && attacker.ControllerId == source.ControllerId;
            };
        }

        if (AttacksAlone().IsMatch(condition))
        {
            // CR 506.3c: exactly one creature was declared as an attacker, and this is the
            // sentence that says it was this one.
            return (e, _, source) =>
                e is AttackersDeclared declared
                && declared.Attackers.Count == 1
                && declared.Attackers.ContainsKey(source.Id);
        }

        if (BlocksOrIsBlocked().IsMatch(condition))
        {
            // CR 702.46a: bushido triggers on blocking or on becoming blocked, which are the two
            // sides of the same declaration.
            return (e, _, source) =>
                e is BlockersDeclared declared
                && (declared.Blockers.ContainsKey(source.Id)
                    || declared.Blockers.Values.Any(list => list.Contains(source.Id)));
        }

        // "Whenever a creature dealt damage by ~ this turn dies" - a question the damage total
        // cannot answer, so the permanent remembers what hit it. The dying creature is read from
        // the state before the move, which is the only state that still has it: a zone change
        // makes a new object (CR 400.7) and the new one is a card in a graveyard with no damage
        // on it at all.
        var avenging = DamagedByThenDiesLine().Match(condition);
        if (avenging.Success)
        {
            var host = avenging.Groups["by"].Value
                .StartsWith("enchanted", StringComparison.OrdinalIgnoreCase)
                || avenging.Groups["by"].Value
                    .StartsWith("equipped", StringComparison.OrdinalIgnoreCase);

            return (e, state, source) =>
            {
                if (e is not ObjectMoved { From: Zone.Battlefield, To: Zone.Graveyard } died)
                    return false;

                if (!state.TryGetObject(died.OldId, out var corpse)
                    || corpse.Permanent is not { } body
                    || !corpse.Card.CardTypes.HasFlag(Domain.Enums.CardType.Creature))
                {
                    return false;
                }

                var dealer = host ? source.Permanent?.AttachedTo : source.Id;

                return dealer is { } who && body.DamagedBy.Contains(who);
            };
        }

        // "Whenever you tap a land for mana", "whenever enchanted land is tapped for mana".
        // Three independent choices - whose permanent, what it is, and whether the sentence
        // names the attached host instead - over one event, which now says which permanent made
        // the mana. A land tapped for anything else is not this: the event is the mana, not the
        // tap, so a land tapped to pay a cost never reaches here.
        var tapping = TappedForManaLine().Match(condition);
        if (tapping.Success)
        {
            var host = tapping.Groups["who"].Value
                .StartsWith("enchanted", StringComparison.OrdinalIgnoreCase)
                || tapping.Groups["who"].Value
                    .StartsWith("equipped", StringComparison.OrdinalIgnoreCase);

            var mine = tapping.Groups["who"].Value.Equals("you", StringComparison.OrdinalIgnoreCase);
            var theirs = tapping.Groups["who"].Value
                .StartsWith("an opponent", StringComparison.OrdinalIgnoreCase);

            var itself = tapping.Groups["self"].Success;

            // The noun goes through the target grammar, so every filter it knows - a Swamp, a
            // land creature, an artifact token - works here without a second vocabulary.
            var spec = itself || host || tapping.Groups["what"].Value.Trim().Length == 0
                ? null
                : EffectPhrase.Specs.Parse("target " + tapping.Groups["what"].Value.Trim());

            if (!itself && !host && spec is not { Kind: Abilities.TargetKind.Permanent })
                return null;

            return (e, state, source) =>
            {
                if (e is not ManaAdded { SourceId: { } made } added)
                    return false;

                if (host)
                    return source.Permanent?.AttachedTo == made;

                if (itself)
                    return made == source.Id;

                if (!state.TryGetObject(made, out var tapped))
                    return false;

                if (spec?.ObjectFilter?.Invoke(
                        state, EmptyAbilities.Instance, tapped, source.ControllerId) == false)
                {
                    return false;
                }

                if (mine && added.PlayerId != source.ControllerId)
                    return false;

                return !theirs || added.PlayerId != source.ControllerId;
            };
        }

        // "Whenever you complete a dungeon" (CR 309.7), on five cards. What it watches is the
        // state-based action removing the dungeon card from the game, not the marker reaching the
        // last room - so the last room's ability has already resolved by the time this fires,
        // which is the whole difference between Varis making a Wolf after his dungeon paid out
        // and making one instead of it.
        if (CompleteDungeonTriggerLine().IsMatch(condition))
        {
            return (e, _, source) =>
                e is DungeonCompleted done && done.PlayerId == source.ControllerId;
        }

        // "Whenever you scry", "whenever you surveil" - the request is the event: it is emitted
        // the moment the effect resolves and carries who is doing it, and the moves that follow
        // are the answer to a question rather than the thing the card is watching for. Surveil is
        // the same request with a different destination (CR 701.42a), so the two are told apart
        // by that flag rather than by two readers.
        var looking = ScryTriggerLine().Match(condition);
        if (looking.Success)
        {
            var surveilling = looking.Groups["verb"].Value
                .Equals("surveil", StringComparison.OrdinalIgnoreCase);

            return (e, _, source) =>
                e is LookAtTopRequested look
                && look.ToGraveyard == surveilling
                && look.PlayerId == source.ControllerId;
        }

        // "~ deals combat damage to a player", "~ deals damage", "~ deals noncombat damage to
        // a player". Three independent choices - whether the damage has to be combat damage,
        // and whether the sentence names who took it - so they are read as two groups rather
        // than as a regex each. A sentence that names no recipient means any of them, which is
        // the reading that was missing: the recipient was mandatory and the bare form went
        // unread.
        var dealing = SourceDealsDamage().Match(condition);
        if (dealing.Success)
        {
            var mustBeCombat = dealing.Groups["combat"].Value
                .Equals("combat", StringComparison.OrdinalIgnoreCase);

            var mustNotBeCombat = dealing.Groups["combat"].Value
                .Equals("noncombat", StringComparison.OrdinalIgnoreCase);

            // "To a player or planeswalker", "to a player or battle" - the recipient is a list on
            // 29 corpus lines, and any one of the things named answers it (CR 109.4). Read as a
            // set of nouns rather than as two more spellings of the whole clause, because the
            // combat/noncombat choice in front of it already multiplies against every one.
            //
            // "Any target" and "a permanent" name nothing in particular and leave the set empty,
            // which is the same as a sentence with no recipient at all: any damage this source
            // dealt satisfies it.
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Capture noun in dealing.Groups["who"].Captures)
                wanted.Add(noun.Value);

            return (e, state, source) =>
            {
                var (dealer, wasCombat, hitAPlayer, victim) = e switch
                {
                    PlayerDamaged hit => (hit.SourceId, hit.IsCombat, true, default(ObjectId)),
                    DamageMarked struck => (struck.SourceId, struck.IsCombat, false, struck.Id),
                    _ => (default, false, false, default),
                };

                if (dealer != source.Id)
                    return false;

                if (mustBeCombat && !wasCombat)
                    return false;

                if (mustNotBeCombat && wasCombat)
                    return false;

                if (wanted.Count == 0)
                    return true;

                if (hitAPlayer)
                    return wanted.Contains("player");

                // Which permanent took it decides the rest, and it is asked of the object rather
                // than of its card: a permanent that is a creature only by layer 4 (CR 613.1d)
                // is still what the damage was dealt to. A battle is named and testable here even
                // though nothing in the engine can attack one yet - the reader is complete, and
                // it is combat that has the gap.
                if (!state.TryGetObject(victim, out var hurt))
                    return false;

                var types = Characteristics.Of(state, EmptyAbilities.Instance, hurt).CardTypes;

                return (wanted.Contains("creature") && types.HasFlag(CardType.Creature))
                    || (wanted.Contains("planeswalker") && types.HasFlag(CardType.Planeswalker))
                    || (wanted.Contains("battle") && types.HasFlag(CardType.Battle));
            };
        }

        return null;
    }

    /// <summary>
    /// "At the beginning of [whose] [step]" — a trigger that fires off the turn itself (CR 603.1).
    /// </summary>
    /// <remarks>
    /// The single biggest family of trigger conditions in Magic: upkeep, end step and beginning
    /// of combat between them are the "when" half of some six hundred cards, which is more than
    /// every keyword ability in the queue put together. They are all one shape — a step, and
    /// whose turn it has to be — so they are read as one shape rather than one regex each.
    /// <para>
    /// "Your" is the ability's controller, not the card's owner, which is why the predicate asks
    /// the source rather than closing over a player id: a stolen permanent triggers on its new
    /// controller's upkeep.
    /// </para>
    /// </remarks>
    private static Func<GameEvent, GameState, TriggerSource, bool>? TryPhase(string condition)
    {
        string owner;
        TurnStep step;

        var m = BeginningOfStep().Match(condition);
        if (m.Success)
        {
            owner = m.Groups["owner"].Value;
            if (StepNamed(m.Groups["step"].Value) is not { } named)
                return null;

            step = named;
        }
        else if (BeginningOfCombatOn().Match(condition) is { Success: true } combat)
        {
            owner = combat.Groups["owner"].Value;
            step = TurnStep.BeginningOfCombat;
        }
        else if (EndOfCombat().IsMatch(condition))
        {
            // CR 511.1: every combat phase has an end of combat step, so this is not restricted
            // to a particular player's turn.
            owner = "each";
            step = TurnStep.EndOfCombat;
        }
        else
        {
            return null;
        }

        // "Each of your postcombat main phases" is "your" with the repetition spelled out - a
        // trigger fires every time its step is reached anyway, so the words add nothing the
        // engine has to do differently.
        var mine = owner.StartsWith("your", StringComparison.OrdinalIgnoreCase)
            || owner.StartsWith("each of your", StringComparison.OrdinalIgnoreCase);
        var theirs = owner.StartsWith("each opponent", StringComparison.OrdinalIgnoreCase)
            || owner.StartsWith("your opponents", StringComparison.OrdinalIgnoreCase);

        // "The beginning of the upkeep of enchanted creature's controller" - an Aura's clock runs
        // on whoever it is attached to, not on whoever owns the Aura. The two differ exactly when
        // the Aura is on somebody else's creature, which is what most of them are for.
        // "Enchanted player's upkeep" is the Aura's host being a player rather than a permanent,
        // and the answer is simply that player - there is no controller to follow through.
        var hostPlayer = owner.StartsWith("enchanted player", StringComparison.OrdinalIgnoreCase);

        var hosts = !hostPlayer
            && (owner.StartsWith("enchanted", StringComparison.OrdinalIgnoreCase)
                || owner.StartsWith("equipped", StringComparison.OrdinalIgnoreCase));

        return (e, state, source) =>
        {
            if (e is not StepBegan began || began.Step != step)
                return false;

            if (hosts)
            {
                // Attached to nothing means there is no such player and the ability does not
                // trigger, which is the right answer rather than an error.
                return source.Permanent?.AttachedTo is { } host
                    && state.TryGetObject(host, out var wearer)
                    && state.ActivePlayerId == wearer.ControllerId;
            }

            if (hostPlayer)
            {
                // Attached to nobody means there is no such player and the ability does not
                // trigger, which is the right answer rather than an error (CR 704.5n).
                return source.Permanent?.AttachedToPlayer is { } enchanted
                    && state.ActivePlayerId == enchanted;
            }

            if (mine)
                return state.ActivePlayerId == source.ControllerId;

            return !theirs || state.ActivePlayerId != source.ControllerId;
        };
    }

    /// <summary>
    /// The object a move is about, whichever side of the move the caller's state is on.
    /// </summary>
    /// <remarks>
    /// A zone change gives the object a new identity (CR 400.7), so a move carries two ids and
    /// only one of them exists in any given state: the old one before the move, the new one
    /// after. Trigger predicates are handed <em>both</em> states — the game asks a permanent that
    /// already existed about the state before the event and one that has just arrived about the
    /// state after (CR 603.6) — so a predicate that looks up only one id silently never fires for
    /// half the sources. Every "something enters" trigger on the board was dead for that reason.
    /// </remarks>
    private static GameObject? Moved(GameState state, ObjectMoved m)
    {
        if (state.TryGetObject(m.NewId, out var after))
            return after;

        return state.TryGetObject(m.OldId, out var before) ? before : null;
    }

    /// <summary>
    /// The permanent that just entered the battlefield, however it got there (CR 603.6a).
    /// </summary>
    /// <remarks>
    /// There are two ways onto the battlefield and they are different events. A card moves there
    /// from somewhere else; a token was never anywhere else and is created there (CR 111.1). Only
    /// the move was being watched, so no "whenever a creature enters" trigger in the game had ever
    /// noticed a token — the commonest way creatures arrive on a modern board.
    /// </remarks>
    private static Arrival? Entered(GameEvent e, GameState state) => e switch
    {
        ObjectMoved { To: Zone.Battlefield } moved => Moved(state, moved) is { } obj
            ? new Arrival(moved.NewId, obj.Card, obj.ControllerId)
            : null,

        // Read straight off the event rather than looked up. A created object exists in neither
        // state a trigger predicate is handed — not in the one before, because it did not exist,
        // and not necessarily in the one after, because a source that predates it is asked about
        // the earlier state (CR 603.6). The event already carries everything the question needs.
        ObjectCreated { Zone: Zone.Battlefield } made =>
            new Arrival(made.Id, made.Card, made.ControllerId),

        _ => null,
    };

    /// <summary>Which side of a block a trigger is about (CR 509.1).</summary>
    private enum BlockRole
    {
        /// <summary>The attacker something was declared against.</summary>
        Blocked,

        /// <summary>The creature that was declared as a blocker.</summary>
        Blocking,
    }

    /// <summary>
    /// A word describing a spell that is not its type — "red", "multicolored", "historic".
    /// </summary>
    /// <remarks>
    /// Read off the card rather than computed, because a spell on the stack has no permanent to
    /// compute from and what these ask about — its colour, whether it is legendary — is printed
    /// on it. Anything not named here leaves the trigger unread, which is the point: an
    /// unrecognised word used to mean "every spell".
    /// </remarks>
    /// <summary>How many times one string occurs in another, without overlapping.</summary>
    private static int CountOccurrences(string text, string needle)
    {
        var found = 0;

        for (var at = text.IndexOf(needle, StringComparison.Ordinal);
            at >= 0;
            at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            found++;
        }

        return found;
    }

    /// <summary>
    /// The creature a block trigger names on the other side of the declaration (CR 509.1).
    /// </summary>
    /// <remarks>
    /// Read by the same vocabulary a target is: "a creature with flying" and "target creature
    /// with flying" describe the same creature, and a second reader written to say so would only
    /// be a second place for the two to disagree. A phrase this vocabulary cannot read leaves the
    /// whole trigger unread - <c>false</c> here - rather than compiling to a trigger that fires on
    /// every block, which is a strictly better card than the one printed.
    /// </remarks>
    private static bool BlockingCreature(
        System.Text.RegularExpressions.Group what, out TargetSpec? described)
    {
        described = null;

        if (!what.Success)
            return true;

        var phrase = what.Value.Trim();

        // "A creature" is every creature, and a filter that admits all of them is one more thing
        // to go wrong than no filter at all.
        if (phrase.Equals("a creature", StringComparison.OrdinalIgnoreCase))
            return true;

        var article = phrase.StartsWith("an ", StringComparison.OrdinalIgnoreCase) ? 3 : 2;

        if (EffectPhrase.Specs.Parse("target " + phrase[article..].Trim())
            is not { ObjectFilter: not null } spec)
        {
            return false;
        }

        described = spec;
        return true;
    }

    /// <summary>
    /// Whether the creature at the other end of a block answers to the description.
    /// </summary>
    /// <remarks>
    /// Asked of the object as it is <em>now</em> rather than of its printed card, which is what
    /// CR 613 means by a creature "with flying": one that has flying at the moment the question is
    /// asked, however it came by it. The filter reaches the layers through the ability source the
    /// trigger now carries - before it did, every question of this kind had to be answered from
    /// the printed card and a granted keyword was invisible.
    /// </remarks>
    private static bool Describes(
        TargetSpec described, GameState state, Abilities.TriggerSource source, ObjectId other) =>
        state.TryGetObject(other, out var creature)
        && described.ObjectFilter!.Invoke(state, source.Abilities, creature, source.ControllerId);

    private static Func<Domain.Models.CardDefinition, bool>? SpellDescription(string word) =>
        word.ToLowerInvariant() switch
        {
            // The card's colours (CR 202.2), not its colour identity (CR 903.4). The identity
            // counts the mana symbols in the rules text too, so it said Bosh, Iron Golem was a
            // red spell - its cost is {8} and it is colourless - and that devoid cards are the
            // colour they are printed to not be.
            "white" => card => card.Colors.Contains(ManaColor.White),
            "blue" => card => card.Colors.Contains(ManaColor.Blue),
            "black" => card => card.Colors.Contains(ManaColor.Black),
            "red" => card => card.Colors.Contains(ManaColor.Red),
            "green" => card => card.Colors.Contains(ManaColor.Green),
            "multicolored" => card => card.Colors.Count > 1,
            "monocolored" => card => card.Colors.Count == 1,
            "colorless" => card => card.Colors.Count == 0,
            "legendary" => card => card.Supertypes.Contains("Legendary", StringComparer.OrdinalIgnoreCase),

            // CR 111.1: a token is not a card, and the engine says so on the definition rather
            // than on the object, so the two readers can share one answer for the word.
            "token" => card => card.CardTypes.HasFlag(CardType.Token),
            "nontoken" => card => !card.CardTypes.HasFlag(CardType.Token),

            // CR 205.4h: historic is legendary, artifact, or Saga — three unrelated things under
            // one word, which is why it is spelled out rather than derived.
            "historic" => card =>
                card.Supertypes.Contains("Legendary", StringComparer.OrdinalIgnoreCase)
                || card.CardTypes.HasFlag(CardType.Artifact)
                || card.Subtypes.Contains("Saga", StringComparer.OrdinalIgnoreCase),

            "permanent" => card => card.CardTypes.HasFlag(CardType.Creature)
                || card.CardTypes.HasFlag(CardType.Artifact)
                || card.CardTypes.HasFlag(CardType.Enchantment)
                || card.CardTypes.HasFlag(CardType.Land)
                || card.CardTypes.HasFlag(CardType.Planeswalker)
                || card.CardTypes.HasFlag(CardType.Battle),

            _ => null,
        };

    /// <summary>What a permanent that has just arrived is, for the triggers that ask.</summary>
    private readonly record struct Arrival(
        ObjectId Id, Domain.Models.CardDefinition Card, Guid ControllerId);

    /// <summary>
    /// "[Another] [permanent type] [you control] enters/dies" — the zone-change family.
    /// </summary>
    /// <remarks>
    /// One shape covering what would otherwise be several dozen near-identical regexes. The
    /// choices are independent — whether the source itself counts, what type the mover has to be,
    /// and whose it has to be — so they are read as three groups and applied as three tests.
    /// </remarks>
    private static Func<GameEvent, GameState, TriggerSource, bool>? TryZoneChange(string condition)
    {
        // "Enters the battlefield under your control" is "you control ... enters" written the
        // other way round. Rewritten before matching rather than given its own alternative,
        // which would have to swallow the verb to reach the end of the phrase.
        condition = EntersUnderYourControl().Replace(condition, " you control enters");

        // "One or more creatures you control deal combat damage to a player" is the plural of a
        // sentence this already reads. The subject and the verb are both plural, and putting
        // both back in the singular lets one reader answer both - the difference that survives
        // is how often it fires, which is decided below and not here.
        var many = OneOrMoreLine().Match(condition);
        var oneOrMore = many.Success;

        if (oneOrMore)
        {
            condition = "one or more "
                + EffectPhrase.SingularWord(many.Groups["head"].Value)
                + many.Groups["rest"].Value
                + " "
                + PluralVerb(many.Groups["verb"].Value.Trim());
        }

        var m = ZoneChangeLine().Match(condition);
        if (!m.Success)
            return null;

        var scope = m.Groups["scope"].Value.ToLowerInvariant();
        var side = m.Groups["side"].Value.Trim().ToLowerInvariant();
        // CR 700.4: "dies" is shorthand for "is put into a graveyard from the battlefield", and
        // the cards use both - the long form for the permanents that are not creatures, where
        // "dies" would read oddly. One verb, two spellings, and 78 cards on the longer one.
        var dying = m.Groups["verb"].Value.Equals("dies", StringComparison.OrdinalIgnoreCase)
            || m.Groups["verb"].Value.Equals(
                "is put into a graveyard from the battlefield", StringComparison.OrdinalIgnoreCase);

        // "From anywhere" is the wider question - from hand, from library, from the stack, as
        // well as from play - so it is not the same verb with a longer name.
        var buried = m.Groups["verb"].Value.Equals(
            "is put into a graveyard from anywhere", StringComparison.OrdinalIgnoreCase);

        var tapVerb = m.Groups["verb"].Value.ToLowerInvariant() switch
        {
            "becomes tapped" => true,
            "becomes untapped" => false,
            _ => (bool?)null,
        };

        var blockVerb = m.Groups["verb"].Value.ToLowerInvariant() switch
        {
            "becomes blocked" => BlockRole.Blocked,
            "blocks" => BlockRole.Blocking,
            _ => (BlockRole?)null,
        };

        // "With power 4 or greater" - read off the printed card rather than computed, because a
        // trigger predicate is handed the state and no ability source, and characteristics need
        // one. The two differ for a creature that entered under a lord or with counters, which is
        // a deviation worth naming rather than a reason to leave 37 cards unread.
        int? statFloor = null;
        int? statCeiling = null;
        var onToughness = m.Groups["stat"].Value.Equals(
            "toughness", StringComparison.OrdinalIgnoreCase);

        var onManaValue = m.Groups["stat"].Value.Equals(
            "mana value", StringComparison.OrdinalIgnoreCase);

        // "Another black creature you control dies", "another nontoken creature enters" - words
        // about the subject that are not its type, read by the same table the cast triggers use
        // so the two cannot disagree about what "colorless" means.
        var describes = m.Groups["adj"].Success
            ? SpellDescription(m.Groups["adj"].Value.Trim())
            : null;

        if (m.Groups["adj"].Success && describes is null)
            return null;

        if (m.Groups["pow"].Success)
        {
            var bound = EffectPhrase.Number(m.Groups["pow"].Value).Fixed;
            if (m.Groups["cmp"].Value.StartsWith("greater", StringComparison.OrdinalIgnoreCase))
                statFloor = bound;
            else
                statCeiling = bound;
        }

        // "With flying", "with a +1/+1 counter on it" - the other two things this slot says, and
        // they ask about the creature rather than about a number. The keyword is read off the
        // printed card for the same reason the power is, and the counter off the permanent, which
        // is where counters live whatever else is true of it.
        var needsKeyword = m.Groups["kw"].Success
            ? EffectPhrase.Keywords(m.Groups["kw"].Value.Trim())
            : null;

        if (m.Groups["kw"].Success && needsKeyword is null)
            return null;

        var needsCounter = m.Groups["counter"].Success ? m.Groups["counter"].Value.Trim() : null;

        // "A creature you control deals combat damage to a player" - the same family with an
        // event that is not a zone change. Everything after the subject is worked out is shared:
        // the type, the tribe, and which side controls it are the same questions whether the
        // creature arrived, died, or connected.
        // Which player the attack or the damage was aimed at, when the sentence says (CR 508.1b).
        // "You" is whoever controls the ability; "enchanted player" is whoever the permanent is
        // attached to (CR 303.4b), and an Aura on nothing names nobody rather than falling back.
        var whom = m.Groups["whom"].Value.Trim();
        var defenderNamed = whom.Length > 0;
        var defendsEnchanted = whom is "enchanted player" or "to enchanted player";

        // "Attacks you" is the player and not the planeswalker: a creature attacking a
        // planeswalker its controller's opponent controls is not attacking that opponent, which
        // is exactly the difference the longer phrasing spells out.
        var planeswalkerCounts = whom is "you or a planeswalker you control";

        // "Deals combat damage to a player" names its recipient; "deals combat damage" does not
        // and means any of them.
        var toAnything = !defenderNamed && m.Groups["verb"].Value.Equals(
            "deals combat damage", StringComparison.OrdinalIgnoreCase);

        var dealing = m.Groups["verb"].Value.StartsWith(
            "deals combat damage", StringComparison.OrdinalIgnoreCase);

        // "A Dragon you control attacks" - one more verb for the same subject grammar, and the
        // one that had been written out on its own. Only the plainest form of it was read
        // ("a creature you control attacks"), so the tribe, the colour, the "another", the
        // opponent's side and the power qualifier were all missing from the attack trigger while
        // sitting right here for entering and dying - 35 corpus lines, on a family that already
        // existed.
        var attacking = m.Groups["verb"].Value.Equals(
            "attacks", StringComparison.OrdinalIgnoreCase);

        // "One or more" has to fire once however many qualified (CR 603.2), and only some of
        // these verbs have an event that says what happened at once. Blocking is a declaration
        // and combat damage is now summarised; entering and dying are one event each, so a
        // plural sentence about them is left unread rather than fired several times.
        //
        // Attacking is a declaration too and was refused for longer than the others, because six
        // of the 28 corpus lines shaped "one or more X you control attack" go on to say "that
        // many" - "add that much {R}", "create that many Treasure tokens" - and the declaration
        // carried no amount, so each of those compiled into a trigger that fired and then added
        // nothing. The invariant found it on Grand Warlord Radha the first time this verb was
        // admitted.
        //
        // The engine records the count now, and records the *narrowed* one: the raw batch size is
        // the wrong number for the four of those lines that name a tribe, and a Dragon trigger
        // paid for every attacker would print a strictly better card than the one on the table.
        if (oneOrMore && !dealing && !attacking && blockVerb is null)
            return null;

        // Only two of these verbs have a defender. Nothing else in the pattern can end with one
        // of those words, but naming the pair here is what stops a verb added later from
        // silently accepting a clause its event cannot answer.
        if (defenderNamed && !attacking && !dealing)
            return null;

        // "One or more creatures deal combat damage to you" - the batched damage event records
        // who dealt it and not who took it, so the recipient cannot be checked and the sentence
        // is left unread. A trigger that fired for damage dealt to anybody would be a strictly
        // better card than the one printed.
        if (defenderNamed && dealing && oneOrMore)
            return null;

        // "Ally", "Goblin", "Zombie" — a creature type rather than a card type. The two are
        // told apart by capitalisation, which is how the cards themselves distinguish them, and
        // a tribal trigger is otherwise identical to the general one: same scope, same side,
        // same verb, one extra test. Forty cards hang on "~ or another Ally you control enters"
        // alone.
        //
        // "A Wolf or Werewolf you control", "an artifact or creature you control" — the noun is
        // an alternation on 36 corpus lines, and a permanent that is either half answers to the
        // sentence (CR 109.4). Collected as a list rather than given a second matcher, because
        // every other choice in this pattern — the scope, the side, the qualifier, the verb —
        // already multiplies against the noun, and a second matcher would have to repeat all of
        // them and then agree with this one forever.
        var alternatives = new List<(Domain.Enums.CardType Type, string? Subtype)>();

        foreach (Capture named in m.Groups["type"].Captures)
        {
            var word = named.Value.Trim();

            if (TypeNamed(word) is { } known)
            {
                alternatives.Add((known, null));
            }
            else if (word.Length > 1 && char.IsUpper(word[0]))
            {
                // A named type is always a creature type in this position, so the card type is
                // implied and does not have to be printed.
                alternatives.Add((Domain.Enums.CardType.Creature, word));
            }
            else
            {
                // One unreadable half leaves the whole sentence unread. Keeping the half that
                // parsed would compile a narrower trigger than the card prints, and a trigger
                // that fires on less than it should is as wrong as one that fires on more.
                return null;
            }
        }

        // "Another" excludes the source; "~ or another" deliberately includes it, which is why
        // the two cannot be collapsed into one flag on the type.
        var excludesSelf = scope.StartsWith("another", StringComparison.Ordinal);
        var yours = side.StartsWith("you", StringComparison.Ordinal);
        var theirs = side.StartsWith("an opponent", StringComparison.Ordinal);
        var hostPlayers = side.StartsWith("enchanted player", StringComparison.Ordinal);

        return (e, state, source) =>
        {
            Arrival subject;
            ObjectId movedFrom;

            // "A creature you control becomes blocked" / "blocks" - the same family with a
            // declaration for an event. One trigger for the declaration however many creatures
            // qualify, which is the same simplification the combat-damage verb already makes:
            // the predicate answers yes or no, and a batch is one event.
            if (blockVerb is { } blocking)
            {
                if (e is not BlockersDeclared declared)
                    return false;

                var involved = blocking == BlockRole.Blocked
                    ? declared.Blockers.Where(pair => !pair.Value.IsEmpty).Select(pair => pair.Key)
                    : declared.Blockers.Values.SelectMany(list => list);

                var who = involved
                    .Select(id => state.TryGetObject(id, out var o) ? o : null)
                    .FirstOrDefault(o => o is not null);

                if (who is null)
                    return false;

                subject = new Arrival(who.Id, who.Card, who.ControllerId);
                movedFrom = default;
            }
            else if (attacking)
            {
                // CR 508.1: attackers are declared as one batch, so the trigger fires on the
                // declaration and asks whether any of them answered the description. Once for
                // the batch rather than once per attacker, which is the same simplification the
                // combat-damage and blocking verbs beside it already make.
                if (e is not AttackersDeclared declared)
                    return false;

                // "Attacks you", "attacks enchanted player" - the declaration says who each
                // attacker was declared against, so the trigger asks about the attackers aimed
                // at that player and ignores the rest of the batch. An Aura attached to nobody
                // names no defender, and then nothing in the declaration can answer.
                var defended = defendsEnchanted
                    ? source.Permanent?.AttachedToPlayer
                    : source.ControllerId;

                if (defenderNamed && defended is null)
                    return false;

                return declared.Attackers
                    .Where(one => !defenderNamed
                        || (one.Value.DefendingPlayer == defended
                            && (planeswalkerCounts || !one.Value.IsPlaneswalker)))
                    .Select(one => state.TryGetObject(one.Key, out var o) ? o : null)
                    .OfType<GameObject>()
                    .Any(attacker =>
                    {
                        // An attacker is the one subject in this family still on the battlefield
                        // when the question is asked, so it is asked of the permanent rather than
                        // of the card. That matters twice over: control is layer 2 (CR 613.1b),
                        // so a stolen creature attacks for its new controller, and a crewed
                        // Vehicle is a creature only by layer 4 (CR 613.1d) — nothing on its card
                        // says so, and reading the card would leave every Vehicle out.
                        var live = Characteristics.Of(state, EmptyAbilities.Instance, attacker);

                        return Qualifies(
                            new Arrival(attacker.Id, attacker.Card, live.ControllerId),
                            default,
                            live);
                    });
            }
            else if (dealing && oneOrMore)
            {
                // One event for the whole damage step, so the trigger fires once. Any of the
                // creatures that connected satisfying the description is enough - the sentence
                // says "one or more", not "all".
                if (e is not CombatDamageDealt batchDamage)
                    return false;

                var whoDealt = toAnything ? batchDamage.Dealers : batchDamage.DealtToPlayer;

                return whoDealt
                    .Select(id => state.TryGetObject(id, out var o) ? o : null)
                    .OfType<GameObject>()
                    .Any(o => Qualifies(new Arrival(o.Id, o.Card, o.ControllerId), default));
            }
            else if (dealing)
            {
                // "Deals combat damage" with no recipient means to anything - a player, a
                // planeswalker, or a creature - so both damage events count, and each of them
                // now says whether it was combat damage rather than leaving it to be guessed
                // from which step the game is in.
                // "Deals combat damage to you" / "to enchanted player" - the same verb with the
                // recipient named, which the damage event has always carried and the sentence
                // could not say. An Aura on nobody names no recipient and nothing answers.
                var struckPlayer = defendsEnchanted
                    ? source.Permanent?.AttachedToPlayer
                    : source.ControllerId;

                if (defenderNamed && struckPlayer is null)
                    return false;

                var dealtBy = e switch
                {
                    PlayerDamaged { IsCombat: true } hit
                        when !defenderNamed || hit.PlayerId == struckPlayer => hit.SourceId,
                    DamageMarked { IsCombat: true } struck when toAnything => struck.SourceId,
                    _ => (ObjectId?)null,
                };

                if (dealtBy is not { } dealerId || !state.TryGetObject(dealerId, out var dealer))
                    return false;

                subject = new Arrival(dealer.Id, dealer.Card, dealer.ControllerId);
                movedFrom = default;
            }
            else if (tapVerb is { } tapped)
            {
                // Untapping is a batch (CR 502.2 turns them all at once) and tapping is not, so
                // the two events have different shapes for what is one sentence on the card.
                var turned = e switch
                {
                    PermanentTapped hit when tapped => hit.Id,
                    PermanentsUntapped { Ids: [var first, ..] } when !tapped => first,
                    _ => (ObjectId?)null,
                };

                if (turned is not { } turnedId || !state.TryGetObject(turnedId, out var permanent))
                    return false;

                subject = new Arrival(permanent.Id, permanent.Card, permanent.ControllerId);
                movedFrom = default;
            }
            else if (buried)
            {
                if (e is not ObjectMoved { To: Zone.Graveyard } put)
                    return false;

                if (Moved(state, put) is not { } arriving)
                    return false;

                subject = new Arrival(put.NewId, arriving.Card, arriving.ControllerId);
                movedFrom = put.OldId;
            }
            else if (dying)
            {
                if (e is not ObjectMoved { From: Zone.Battlefield, To: Zone.Graveyard } died)
                    return false;

                if (Moved(state, died) is not { } corpse)
                    return false;

                subject = new Arrival(died.NewId, corpse.Card, corpse.ControllerId);
                movedFrom = died.OldId;
            }
            else
            {
                if (Entered(e, state) is not { } arrived)
                    return false;

                subject = arrived;
                movedFrom = e is ObjectMoved move ? move.OldId : default;
            }

            // Every question after the subject is worked out, in one place: the plural form
            // has several candidates and has to ask all of them, and asking them with a
            // second copy of these checks is how the two would drift apart.
            //
            // <paramref name="live"/> is supplied only by the attacking branch, where the
            // subject is still a permanent and its computed characteristics can be read. Every
            // other verb here is a zone change or a damage event whose subject may already have
            // left, so those fall back to the printed card - a deviation the family has always
            // had and which is named where the power qualifier is parsed.
            bool Qualifies(
                Arrival subject, ObjectId movedFrom, ComputedCharacteristics? live = null)
            {
                if (excludesSelf && (subject.Id == source.Id || movedFrom == source.Id))
                    return false;

                var types = live?.CardTypes ?? subject.Card.CardTypes;

                // Either half of "a Wolf or Werewolf" satisfies the sentence (CR 109.4), and
                // CR 702.73a decides the tribe: a changeling is every creature type, so it
                // answers to all of them.
                if (!alternatives.Exists(one =>
                    types.HasFlag(one.Type)
                    && (one.Subtype is null
                        || (live is { } computed
                            ? computed.HasSubtype(one.Subtype)
                            : subject.Card.Keywords.HasFlag(KeywordAbility.Changeling)
                                || subject.Card.Subtypes.Contains(
                                    one.Subtype, StringComparer.OrdinalIgnoreCase)))))
                {
                    return false;
                }

                if (describes is { } asked && !asked(subject.Card))
                    return false;

                var stat = onManaValue ? subject.Card.Cmc
                    : onToughness ? live?.Toughness ?? subject.Card.Toughness ?? 0
                    : live?.Power ?? subject.Card.Power ?? 0;

                if (statFloor is { } least && stat < least)
                    return false;

                if (statCeiling is { } most && stat > most)
                    return false;

                if (needsKeyword is { } wanted
                    && !(live?.Keywords ?? subject.Card.Keywords).HasFlag(wanted))
                {
                    return false;
                }

                // Counters live on the permanent, and the arrival carries only what the event knew.
                // Looked up in the state, which is where the permanent is - and a subject that has
                // already left has none, which is the right answer for "dies with a counter on it"
                // only because the death is read from the object as it last existed.
                if (needsCounter is { } kind)
                {
                    if (!state.TryGetObject(subject.Id, out var standing)
                        || (standing.Permanent?.Counters.GetValueOrDefault(kind) ?? 0) <= 0)
                    {
                        return false;
                    }
                }

                // "Enchanted player controls" is whoever the source is attached to
                // (CR 303.4b), and an Aura on nobody describes nobody's permanents rather than
                // its controller's - the difference between a Curse that does nothing and a
                // Curse that quietly turns on its owner.
                if (hostPlayers)
                {
                    return source.Permanent?.AttachedToPlayer is { } enchantedController
                        && subject.ControllerId == enchantedController;
                }

                if (yours && subject.ControllerId != source.ControllerId)
                    return false;

                return !theirs || subject.ControllerId != source.ControllerId;
            }

            return Qualifies(subject, movedFrom);
        };
    }

    /// <summary>"[Who] casts a [kind] spell" — the cast family (CR 601.2).</summary>
    private static Func<GameEvent, GameState, TriggerSource, bool>? TryCast(string condition)
    {
        var m = CastsLine().Match(condition);
        if (!m.Success)
            return null;

        var who = m.Groups["who"].Value.ToLowerInvariant();
        var printedKind = m.Groups["kind"].Value.Trim();
        var kind = printedKind.ToLowerInvariant();
        var copies = m.Groups["copy"].Success;

        // "From your graveyard", "from anywhere other than your hand" - a question about where
        // the spell was cast from, which only the casting knew and the event now carries.
        Zone? castFrom = m.Groups["from"].Value.ToLowerInvariant() switch
        {
            "your hand" => Zone.Hand,
            "your graveyard" => Zone.Graveyard,
            "exile" => Zone.Exile,
            _ => null,
        };

        var notFromHand = m.Groups["from"].Value.StartsWith(
            "anywhere", StringComparison.OrdinalIgnoreCase);

        // "A spell with mana value 3 or greater" - a filter on the card rather than on who cast
        // it, read off the printed cost the same way every other mana-value question is.
        int? floor = null;
        int? ceiling = null;
        if (m.Groups["mv"].Success)
        {
            var bound = int.Parse(m.Groups["mv"].Value, CultureInfo.InvariantCulture);
            if (m.Groups["cmp"].Value.StartsWith("greater", StringComparison.OrdinalIgnoreCase))
                floor = bound;
            else
                ceiling = bound;
        }

        var mine = who.StartsWith("you", StringComparison.Ordinal);
        var theirs = who.StartsWith("an opponent", StringComparison.Ordinal);

        // "Spell" on its own is every spell; a named type is a filter; "noncreature" is the one
        // negation common enough on printed cards to be worth reading. "Instant or sorcery" is
        // two alternatives, while "artifact creature" is one alternative needing both flags —
        // which is why this is a list of flag sets rather than a single mask.
        var noncreature = kind.StartsWith("noncreature", StringComparison.Ordinal);
        var named = kind.Split(" or ", StringSplitOptions.RemoveEmptyEntries);

        var alternatives = noncreature
            ? [Domain.Enums.CardType.Creature]
            : named
                .Select(TypeNamed)
                .Where(t => t is not null and not Domain.Enums.CardType.None)
                .Select(t => t!.Value)
                .ToList();

        // "A Goblin spell", "an Aura spell" - the kind is a subtype rather than a card type, and
        // told apart by its capital the same way it is everywhere else in this grammar.
        var subtypes = printedKind.Split(" or ", StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Trim())
            .Where(word => word.Length > 1 && char.IsUpper(word[0]))
            .ToList();

        // "A red spell", "a multicolored spell", "a historic spell" - words about the card that
        // are not its type, and the commonest thing a cast trigger names after the type itself.
        var describes = named
            .Select(word => SpellDescription(word.Trim()))
            .Where(one => one is not null)
            .Select(one => one!)
            .ToList();

        // A kind that names nothing the engine knows - "a playtest card" - used to leave an empty
        // list, and an empty list means every spell. The trigger fired on everything, which is a
        // strictly better card than the one printed.
        if (kind.Length > 0
            && !noncreature
            && alternatives.Count + subtypes.Count + describes.Count != named.Length)
        {
            return null;
        }

        // "During an opponent's turn", "during your turn" - a question about whose turn it is
        // rather than about the spell, and answered from the state at the moment it is asked.
        var opponentsTurn = m.Groups["when"].Value.StartsWith(
            "during an opponent", StringComparison.OrdinalIgnoreCase);

        var yourTurn = m.Groups["when"].Value.StartsWith(
            "during your", StringComparison.OrdinalIgnoreCase);

        // "With {X} in its mana cost" - a question about the printed cost, which is where an X
        // lives: the value chosen for it belongs to the casting, but whether there is one at all
        // is a fact about the card (CR 107.3).
        var hasVariableCost = m.Groups["withx"].Success;

        // "A spell using teamwork" - a question about how the spell was cast (CR 702.194b),
        // answerable because the teamwork event lands on the stack object before the cast event
        // this trigger watches. A copy was never cast at all, so it never satisfies this.
        var usingTeamwork = m.Groups["teamwork"].Success;

        return (e, state, source) =>
        {
            // "Cast or copy" is two events for one sentence (CR 707.10): a copy is put on the
            // stack without being cast, so nothing about the cast event sees it. Both are read
            // here into the same shape - who did it, and which card it was - so everything after
            // that is asked once rather than twice.
            Guid who;
            Domain.Models.CardDefinition? what;

            // CR 107.3a: while a spell is on the stack, any X in its mana cost equals the value
            // announced as it was cast; CR 107.3g puts X at zero in every other zone. A Fireball
            // cast for X=5 has mana value 6 and its printed cost says 1, so "whenever you cast a
            // spell with mana value 5 or greater" read the card instead of the spell and never
            // fired. A copy is left at zero: the event carries the card and not the object, so
            // the value chosen for X is not there to read.
            var chosenX = 0;

            switch (e)
            {
                case SpellCastEvent cast:
                    if (castFrom is { } wanted && cast.From != wanted)
                        return false;

                    if (notFromHand && cast.From == Zone.Hand)
                        return false;

                    who = cast.PlayerId;

                    if (state.TryGetObject(cast.StackId, out var spell))
                    {
                        if (usingTeamwork && !spell.WasTeamwork)
                            return false;

                        what = spell.Card;
                        chosenX = spell.VariableValue;
                    }
                    else
                    {
                        if (usingTeamwork)
                            return false;

                        what = null;
                    }

                    break;

                case SpellCopied copied when copies && !usingTeamwork:
                    who = copied.ControllerId;
                    what = copied.Card;
                    break;

                default:
                    return false;
            }

            if (opponentsTurn && state.ActivePlayerId == source.ControllerId)
                return false;

            if (yourTurn && state.ActivePlayerId != source.ControllerId)
                return false;

            if (mine && who != source.ControllerId)
                return false;

            if (theirs && who == source.ControllerId)
                return false;

            if (what is null)
            {
                return floor is null
                    && ceiling is null
                    && !hasVariableCost
                    && alternatives.Count == 0
                    && subtypes.Count == 0;
            }

            if (hasVariableCost
                && !(what.ManaCostRaw ?? string.Empty).Contains("{X}", StringComparison.Ordinal))
            {
                return false;
            }

            // One {X} is the common case and two exist ({X}{X} on 51 cards), so the symbol is
            // counted rather than assumed.
            var manaValue = what.Cmc
                + (chosenX * CountOccurrences(what.ManaCostRaw ?? string.Empty, "{X}"));

            if (floor is { } least && manaValue < least)
                return false;

            if (ceiling is { } most && manaValue > most)
                return false;

            if (alternatives.Count == 0 && subtypes.Count == 0 && describes.Count == 0)
                return true;

            // Either half satisfies the sentence: "a Goblin or Elf spell" names two subtypes and
            // "an artifact or creature spell" names two types, and a card that is any one of what
            // was named answers to it (CR 109.4).
            var matches = alternatives.Any(t => what.CardTypes.HasFlag(t))
                || subtypes.Any(sub => what.Subtypes.Contains(sub, StringComparer.OrdinalIgnoreCase))
                || describes.Any(asked => asked(what));

            return noncreature ? !matches : matches;
        };
    }

    /// <summary>
    /// A printed type word as the flags a card must have, or null when the word is not one.
    /// </summary>
    /// <remarks>
    /// <see cref="Domain.Enums.CardType.None"/> is a real answer here and means "no filter":
    /// "permanent" and "spell" are not card types, they are every card type, and a caller testing
    /// with <c>HasFlag</c> gets exactly that behaviour for free. Null is the different answer —
    /// a word this does not know, which must leave the whole line unread rather than quietly
    /// matching everything.
    /// </remarks>
    private static Domain.Enums.CardType? TypeNamed(string word) => word.Trim().ToLowerInvariant() switch
    {
        "creature" or "creatures" or "nontoken creature" => Domain.Enums.CardType.Creature,
        "artifact" or "artifacts" => Domain.Enums.CardType.Artifact,
        "artifact creature" => Domain.Enums.CardType.Creature | Domain.Enums.CardType.Artifact,
        "enchantment" or "enchantments" => Domain.Enums.CardType.Enchantment,
        "land" or "lands" => Domain.Enums.CardType.Land,
        "planeswalker" or "planeswalkers" => Domain.Enums.CardType.Planeswalker,
        "instant" or "instants" => Domain.Enums.CardType.Instant,
        "sorcery" or "sorceries" => Domain.Enums.CardType.Sorcery,
        "permanent" or "permanents" or "spell" or "spells" => Domain.Enums.CardType.None,
        _ => null,
    };

    /// <summary>The step a printed phrase names, or null when it names something else.</summary>
    internal static TurnStep? StepNamed(string phrase) => phrase.ToLowerInvariant() switch
    {
        "untap step" => TurnStep.Untap,
        "upkeep" or "upkeep step" => TurnStep.Upkeep,
        "draw step" => TurnStep.Draw,
        // A main phase has no steps of its own (CR 505.2), so the position the engine walks
        // through carries the phase's name and a trigger on the phase fires when it is reached.
        "precombat main phase" or "main phase" or "first main phase" => TurnStep.PrecombatMain,
        "postcombat main phase" or "postcombat main phases" or "second main phase" =>
            TurnStep.PostcombatMain,

        // "Your combat step" is the phase by another name: a card saying it means the beginning
        // of combat, which is where the phase starts (CR 506.1).
        "combat" or "combat phase" or "combat step" => TurnStep.BeginningOfCombat,
        "end step" => TurnStep.End,
        _ => null,
    };

    [GeneratedRegex(
        @"^the beginning of the (?<step>upkeep|end step) of "
            + @"(?<owner>enchanted [a-z]+'s controller)$"
            + @"|^the beginning of "
            + @"(?<owner>each of your|your|each player's|each opponent's|your opponents'"
            + @"|enchanted player's|the|each) "
            + @"(?<step>untap step|upkeep step|upkeep|draw step|precombat main phase|"
            + @"first main phase|postcombat main phases?|second main phase|main phase|"
            + @"combat step|combat phase|combat|end step)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex BeginningOfStep();

    [GeneratedRegex(
        @"^the beginning of combat on (?<owner>your|each player's|each opponent's) turn$",
        RegexOptions.IgnoreCase)]
    private static partial Regex BeginningOfCombatOn();

    [GeneratedRegex(@"^(the )?end of combat$", RegexOptions.IgnoreCase)]
    private static partial Regex EndOfCombat();

    [GeneratedRegex(
        @"^~ enters( the battlefield)?$", RegexOptions.IgnoreCase)]
    private static partial Regex EntersLine();

    /// <remarks>
    /// CR 700.4: "dies" means "is put into a graveyard from the battlefield". Cards printed
    /// before the word existed spell it out, and both wordings are the same trigger.
    /// </remarks>
    [GeneratedRegex(
        @"^~ (dies|is put into a graveyard from the battlefield)$", RegexOptions.IgnoreCase)]
    private static partial Regex DiesLine();

    [GeneratedRegex(@"^~ exploits an? [a-z' ]*creature$", RegexOptions.IgnoreCase)]
    private static partial Regex ExploitsLine();

    [GeneratedRegex(
        @"^((?<who>you|a player|an opponent) taps? "
            + @"(~(?<self>)|an? (?<what>[A-Za-z][A-Za-z ]*?))"
            + @"|(?<who>enchanted|equipped) [a-zA-Z]+ is tapped) for mana$",
        RegexOptions.None)]
    private static partial Regex TappedForManaLine();

    [GeneratedRegex(
        @"^a creature dealt damage by (?<by>~|enchanted creature|equipped creature) "
            + @"this turn dies$",
        RegexOptions.IgnoreCase)]
    private static partial Regex DamagedByThenDiesLine();

    [GeneratedRegex(@"^you (?<verb>scry|surveil)$", RegexOptions.IgnoreCase)]
    private static partial Regex ScryTriggerLine();

    /// <summary>"Whenever you complete a dungeon" (CR 309.7).</summary>
    [GeneratedRegex(@"^you complete a dungeon$", RegexOptions.IgnoreCase)]
    private static partial Regex CompleteDungeonTriggerLine();

    /// <remarks>
    /// The recipient nouns are written as a repeatable group under one name, so "a player or
    /// planeswalker" arrives as two captures of <c>who</c> and the reader takes a set. "Any
    /// target" and "a permanent" capture nothing on purpose: neither narrows what was hit, and
    /// an empty set is exactly the reading a sentence with no recipient at all wants.
    /// </remarks>
    [GeneratedRegex(
        @"^~ deals ((?<combat>combat|noncombat) )?damage"
            + @"( to (?:any target|a permanent"
            + @"|an? (?<who>player|creature|planeswalker|battle)"
            + @"(?:,? or (?<who>player|creature|planeswalker|battle))*))?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SourceDealsDamage();

    [GeneratedRegex(@"^~ attacks$", RegexOptions.IgnoreCase)]
    private static partial Regex AttacksLine();

    [GeneratedRegex(@"^~ attacks alone$", RegexOptions.IgnoreCase)]
    private static partial Regex AttacksAlone();

    /// <summary>"A creature you control attacks alone" - any of them, not this one.</summary>
    [GeneratedRegex(@"^a creature you control attacks alone$", RegexOptions.IgnoreCase)]
    private static partial Regex AnyCreatureAttacksAlone();

    [GeneratedRegex(
        @"^~ blocks or becomes blocked( by a creature)?$", RegexOptions.IgnoreCase)]
    private static partial Regex BlocksOrIsBlocked();

    /// <remarks>
    /// Not case-insensitive, because capitalisation is the only thing separating "another
    /// creature" from "another Ally" — and the two are different triggers.
    /// <para>
    /// The noun group is written twice under one name so that "a Wolf or Werewolf you control"
    /// arrives as two captures of <c>type</c> rather than needing a second pattern. The
    /// alternation inside it stays ordered — the two-word compound before the bare word — for
    /// the same reason it is ordered in the target grammar: with "creature" tried first,
    /// "artifact creature" reads as "artifact" and leaves a word the pattern has nowhere to put.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"^(?<scope>~ or another|[Aa]nother|[Oo]ne or more|[Aa]n?)\s+"
            + @"(?<adj>white|blue|black|red|green|colorless|multicolored|legendary|nontoken)?\s*"
            + @"(?<type>artifact creature|[a-z]+|[A-Z][a-z]+)"
            + @"(\s+or\s+(?<type>artifact creature|[a-z]+|[A-Z][a-z]+))?"
            + @"(?<side>\s+you control|\s+an opponent controls"
            // "A creature enchanted player controls enters" - the same possessive one
            // relation further out. A Curse is attached to a player (CR 303.4b), so "that
            // player's permanents" is a group a trigger can describe without targeting.
            + @"|\s+enchanted player controls)?"
            + @"(\s+with (?<stat>power|toughness|mana value) (?<pow>\d+|one|two|three|four|five"
            + @"|six|seven|eight|nine|ten) or (?<cmp>greater|less)"
            + @"|\s+with (?<kw>[a-z ]+?)"
            + @"|\s+with a (?<counter>[+-]\d/[+-]\d) counter on it)?\s+"
            + @"(?<verb>enters|dies|leaves the battlefield"
            + @"|is put into a graveyard from the battlefield"
            + @"|is put into a graveyard from anywhere"
            + @"|becomes tapped|becomes untapped"
            + @"|becomes blocked|blocks|attacks"
            + @"|deals combat damage to a player|deals combat damage)"
            // Who is being attacked. An attack is declared against a particular player or a
            // planeswalker they control (CR 508.1b), and the declaration has carried that all
            // along - only the sentence had nowhere to say it, so "whenever a creature attacks
            // you" was refused while "whenever a creature attacks" was read. Optional, because
            // the unqualified sentence means any defender and is the commoner one.
            + @"(?<whom> to you| to enchanted player"
            + @"| you or a planeswalker you control| you| enchanted player)?$",
        RegexOptions.None)]
    private static partial Regex ZoneChangeLine();

    /// <remarks>
    /// Both spellings, even though the compiler normalises the longer one away before this is
    /// reached. This reader is also called with hand-written conditions that never passed through
    /// that normalisation, and a rewrite that only worked for text arriving one way is a trap for
    /// whoever writes the next one.
    /// </remarks>
    [GeneratedRegex(
        @"\s+enters( the battlefield)? under your control$", RegexOptions.IgnoreCase)]
    private static partial Regex EntersUnderYourControl();

    /// <remarks>
    /// The head noun and the verb are pulled out separately because they are the only two words
    /// that have to change: everything between them - "you control", "with flying", a tribe -
    /// reads the same in both numbers and is carried across untouched.
    /// </remarks>
    [GeneratedRegex(
        @"^one or more (?<head>[A-Za-z]+)(?<rest>.*?)"
            + @" (?<verb>deal combat damage to a player|deal combat damage|attack|block"
            + @"|become blocked)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex OneOrMoreLine();

    /// <summary>The singular of the handful of verbs a plural trigger subject can take.</summary>
    /// <remarks>
    /// A table rather than a rule, because the rule ("add an s") is wrong for the two-word forms
    /// and there are only five of these. A verb missing from it leaves the sentence unread,
    /// which is the outcome the caller is built around.
    /// </remarks>
    private static string PluralVerb(string verb) => verb.ToLowerInvariant() switch
    {
        "deal combat damage to a player" => "deals combat damage to a player",
        "deal combat damage" => "deals combat damage",
        "attack" => "attacks",
        "block" => "blocks",
        "become blocked" => "becomes blocked",

        // Deliberately not "enter" or "die". Adding them here is dead vocabulary: the rewrite
        // then happens and the batch guard below refuses the result, because the engine emits
        // one entry and one death event per object and a plural trigger built on them would fire
        // once per creature instead of once for the batch (CR 603.2). Twenty-five corpus lines
        // wait on batched events, not on this table.
        _ => verb,
    };

    /// <remarks>
    /// The type word is optional, because "whenever you cast a spell" has none — and requiring
    /// one silently lost the plainest cast trigger there is, on sixty-odd cards.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<who>you|a player|an opponent|another player)\s+"
            + @"casts?(?<copy>\s+or\s+cop(y|ies))?\s+an?\s+"
            + @"((?<kind>noncreature|[a-z]+(\s+or\s+[a-z]+)?)\s+)?spell"
            + @"(\s+(?<teamwork>using teamwork))?"
            + @"(\s+with mana value (?<mv>\d+) or (?<cmp>greater|less))?"
            + @"(\s+with \{X\} in its mana (?<withx>cost))?"
            + @"(\s+(?<when>during an opponent's turn|during your turn))?"
            + @"(\s+from (?<from>your hand|your graveyard|exile"
            + @"|anywhere other than your hand))?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex CastsLine();

    [GeneratedRegex(
        @"^~ becomes blocked( by (?<what>an? .+))?$", RegexOptions.IgnoreCase)]
    private static partial Regex BecomesBlocked();

    [GeneratedRegex(@"^~ blocks( (?<what>an? .+))?$", RegexOptions.IgnoreCase)]
    private static partial Regex BlocksLine();

    [GeneratedRegex(@"^~ attacks or blocks$", RegexOptions.IgnoreCase)]
    private static partial Regex AttacksOrBlocks();

    [GeneratedRegex(
        @"^(?<when>.+?) while (?<while>[a-z][a-z' ]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex WhileLine();

    [GeneratedRegex(@"^~ enters or attacks$", RegexOptions.IgnoreCase)]
    private static partial Regex EntersOrAttacks();

    [GeneratedRegex(@"^you attack$", RegexOptions.IgnoreCase)]
    private static partial Regex YouAttack();

    /// <summary>"You attack with one or more creatures with counters on them" (CR 508.1).</summary>
    /// <remarks>
    /// The group is left whole for the target grammar rather than cut up here: "creatures with
    /// counters on them", "legendary creatures", "Goblins and/or Orcs" and "non-Gnome creatures"
    /// are one vocabulary's problem and not four patterns.
    /// </remarks>
    [GeneratedRegex(
        @"^you attack with (?<n>one|two|three|four|five|\d+) or more (?<what>.+)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex YouAttackWith();

    [GeneratedRegex(@"^~ becomes tapped$", RegexOptions.IgnoreCase)]
    private static partial Regex BecomesTapped();

    [GeneratedRegex(@"^you cast ~$", RegexOptions.IgnoreCase)]
    private static partial Regex YouCastThis();

    [GeneratedRegex(@"^~ becomes untapped$", RegexOptions.IgnoreCase)]
    private static partial Regex BecomesUntapped();

    [GeneratedRegex(@"^~ leaves the battlefield$", RegexOptions.IgnoreCase)]
    private static partial Regex LeavesTheBattlefield();

    [GeneratedRegex(@"^you cycle ~$", RegexOptions.IgnoreCase)]
    private static partial Regex CyclesLine();

    [GeneratedRegex(
        @"^you (cycle or discard|discard or cycle) an? card$", RegexOptions.IgnoreCase)]
    private static partial Regex CycleOrDiscardLine();

    [GeneratedRegex(@"^you roll (one or more dice|a die)$", RegexOptions.IgnoreCase)]
    private static partial Regex RollsDiceCondition();

    [GeneratedRegex(@"^you roll a die's highest natural result$", RegexOptions.IgnoreCase)]
    private static partial Regex RollsHighestCondition();

    /// <remarks>
    /// "A natural 20" is deliberately absent: the one card watching for it does so from the
    /// graveyard (Critical Hit), and a trigger compiled here functions from the battlefield —
    /// the wording would read cleanly into an ability that never fires, which is the compile
    /// this vocabulary exists to refuse.
    /// </remarks>
    [GeneratedRegex(
        @"^you roll a (?<n>\d+)( or (?<m>\d+)| or (?<dir>higher|greater))?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex RollsNumberCondition();

    [GeneratedRegex(
        @"^~ and at least (?<n>\d+|one|two|three|four|five) other creatures attack$",
        RegexOptions.IgnoreCase)]
    private static partial Regex AttacksWithOthersLine();

    [GeneratedRegex(@"^~ attacks alone$", RegexOptions.IgnoreCase)]
    private static partial Regex AttacksAloneLine();

    [GeneratedRegex(@"^enchanted player is attacked$", RegexOptions.IgnoreCase)]
    private static partial Regex EnchantedPlayerAttackedLine();

    /// <remarks>
    /// Case-sensitive on the type list, because a capital is what separates a creature type from
    /// an ordinary noun - the same rule the target grammar and the filter vocabulary both use.
    /// </remarks>
    [GeneratedRegex(
        @"^an? (?<types>[A-Z][A-Za-z]*(,? or [A-Z][A-Za-z]*)*) you control attacks alone$",
        RegexOptions.None)]
    private static partial Regex GroupAttacksAloneLine();

    [GeneratedRegex(@"^~ enters or leaves the battlefield$", RegexOptions.IgnoreCase)]
    private static partial Regex EntersOrLeavesLine();

    [GeneratedRegex(@"^~ enters or dies$", RegexOptions.IgnoreCase)]
    private static partial Regex EntersOrDies();

    [GeneratedRegex(@"^~ attacks and isn't blocked$", RegexOptions.IgnoreCase)]
    private static partial Regex AttacksUnblocked();

    [GeneratedRegex(
        @"^~ deals (?<combat>combat )?damage to (?<victim>a creature|an opponent|a player)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex DealsDamageTo();

    [GeneratedRegex(@"^you draw a card$", RegexOptions.IgnoreCase)]
    private static partial Regex YouDrawACard();

    [GeneratedRegex(@"^~ is dealt (?<combat>combat )?damage$", RegexOptions.IgnoreCase)]
    private static partial Regex IsDealtDamage();

    [GeneratedRegex(
        @"^~ becomes the target of a spell( or ability)?$", RegexOptions.IgnoreCase)]
    private static partial Regex BecomesTargeted();

    /// <remarks>
    /// The head keeps "a" or "another" so the rewritten clause reads as the card would have
    /// written it the other way round, and the existing family handles the rest.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<head>an?other |an? )(?<what>[A-Za-z' ]+?) enters under (?<whose>your|an opponent's) control$",
        RegexOptions.IgnoreCase)]
    private static partial Regex EntersUnderControlLine();

    [GeneratedRegex(@"^an opponent draws a card$", RegexOptions.IgnoreCase)]
    private static partial Regex OpponentDrawsLine();

    [GeneratedRegex(
        @"^you attack with (?<n>two|three|four|\d+) or more creatures$", RegexOptions.IgnoreCase)]
    private static partial Regex AttackWithManyLine();

    [GeneratedRegex(
        @"^(you put (an?|one or more) (?<kind>[+\-0-9/]+ )?counters? on "
            + @"(?<on>~|an? [a-z]+)(?<side> you control)?"
            + @"|(an?|one or more) (?<kind>[+\-0-9/]+ )?counters? are put on "
            + @"(?<on>~|an? [a-z]+)(?<side> you control)?)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex PutsACounterLine();

    [GeneratedRegex(@"^~ is turned face up$", RegexOptions.IgnoreCase)]
    private static partial Regex TurnedFaceUpLine();

    [GeneratedRegex(@"^~ becomes monstrous$", RegexOptions.IgnoreCase)]
    private static partial Regex BecomesMonstrousLine();

    /// <summary>
    /// "~ mutates" and "a creature you control mutates" (CR 702.140d).
    /// </summary>
    /// <remarks>
    /// The two shapes are one pattern because they differ only in who the creature is, which is
    /// the same distinction every other trigger family here draws. "Another creature" is not
    /// printed on any card and is left out rather than guessed at.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<who>~|a creature you control) mutates$", RegexOptions.IgnoreCase)]
    private static partial Regex MutatesLine();

    [GeneratedRegex(
        @"^(?<who>you|a player|an opponent) discards? a card$", RegexOptions.IgnoreCase)]
    private static partial Regex DiscardsACardLine();

    [GeneratedRegex(
        @"^(?<who>you|a player|an opponent) sacrifices? "
            + @"(an?|(?<another>another)) (?<what>[A-Za-z]+)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SacrificesLine();

    [GeneratedRegex(
        @"^an? (?<what>[a-z]+) you control is put into a graveyard( from the battlefield)?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex PutIntoGraveyardLine();

    /// <summary>"[Who] casts their Nth [kind] spell each turn" — the ordinal cast family.</summary>
    /// <remarks>
    /// The kind slot was once only "creature" and "noncreature", and the reason was what the
    /// player carried rather than what the sentence said: <c>SpellsCastThisTurn</c> and
    /// <c>NoncreatureSpellsCastThisTurn</c> were the only counts kept, and creature spells are
    /// the difference between them. Everything else was refused, because the tempting reading —
    /// the ability's <c>OncePerTurn</c> flag with a plain enchantment-cast condition — is wrong in
    /// the direction that matters: that flag is per (permanent, ability), so a permanent arriving
    /// after an enchantment had already been cast this turn would still trigger on the second one,
    /// which the printed card never does.
    /// <para>
    /// The player records the cards themselves now, so the rest of the family can be answered
    /// exactly. It stores cards rather than tallies for a reason worth keeping: a per-type counter
    /// would count a Pirate Rogue twice over the five outlaw types.
    /// </para>
    /// <para>
    /// A tribe is still not admitted here — "your first Human creature spell each turn" — and the
    /// omission is deliberate. The word would have to be taken on trust as a creature type, and a
    /// word that is not one matches nothing, which compiles a trigger that never fires. That is
    /// the failure this file spends most of its comments avoiding, and it is worth more than the
    /// two lines it costs.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"^(?<who>you|an opponent|a player) (casts?|draws?) (your|their) (?<ord>[a-z]+) "
            + @"(?<kind>instant or sorcery |creature |noncreature |enchantment |artifact "
            + @"|instant |sorcery |outlaw )?(?<what>spell|card) each turn$",
        RegexOptions.IgnoreCase)]
    private static partial Regex NthEachTurn();

    [GeneratedRegex(
        @"^(?<who>you|a player|an opponent) casts? a spell that targets ~$",
        RegexOptions.IgnoreCase)]
    private static partial Regex CastSpellTargeting();

    [GeneratedRegex(
        @"^(?<who>you|an opponent|a player)\s+(?<verb>gains?|loses?)\s+life$",
        RegexOptions.IgnoreCase)]
    private static partial Regex GainsOrLosesLife();

    [GeneratedRegex(
        // The noun is not always "creature": a land or an artifact can be enchanted and become
        // tapped just as a creature can. "Player" is deliberately absent - an Aura on a player
        // has a player for a host, and everything below resolves a permanent.
        @"^(enchanted|equipped) (creature|land|permanent|artifact|enchantment|planeswalker) "
            + @"(?<verb>dies|attacks or blocks|attacks|blocks|becomes tapped|becomes untapped"
            + @"|becomes blocked|is dealt damage"
            + @"|deals combat damage to a player|deals combat damage"
            + @"|deals damage to a player|deals damage to an opponent|deals damage)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex AttachedCreature();
}

/// <summary>
/// Reads the mana half of a mana ability into the alternatives it offers (CR 605.1a).
/// </summary>
/// <remarks>
/// "Add {G}" offers one way to pay; "Add {G} or {W}" offers two; "Add one mana of any color"
/// offers five. The rules call that a single ability with a choice made on resolution, and the
/// engine has no way to ask a question during a mana ability — they do not use the stack
/// (CR 605.3b), and stopping the game inside a cost payment is a much larger change.
/// <para>
/// So each alternative becomes its own activated ability. A player choosing which of two
/// abilities to activate reaches exactly the same board state as one choosing a colour, and the
/// difference is invisible from the table. It is a deliberate simplification, recorded here
/// rather than hidden: a card that cares about *which ability* was activated would notice.
/// </para>
/// </remarks>
public static partial class ManaWords
{
    /// <summary>Every way this line can be paid out. Empty means it was not understood.</summary>
    public static ImmutableList<ImmutableList<ManaProduction>> Alternatives(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // "Add one mana of the chosen color" - the colour is not known until the permanent has
        // named one, so it rides as a flag and the engine asks the source when the ability is
        // activated.
        if (ChosenColorMana().IsMatch(text))
            return [ImmutableList.Create(ManaProduction.Chosen())];

        // "Add two mana in any combination of colors" - each mana is its own colour, so the
        // payout is a multiset rather than a single choice. Written out as one alternative per
        // combination, which is what this model is: the player picks a payout by picking which
        // ability to activate, and fifteen of them is a menu rather than a mechanism.
        //
        // Capped at two deliberately. Three colours would be thirty-five alternatives and five
        // would be a hundred and twenty-six, which is not a menu anybody can use - those are
        // left unread rather than made unusable.
        // "Add {C} for each storage counter removed this way" - the amount is the number the
        // player named as they paid, so it is a flag rather than a figure: the production says
        // "as many as the cost took" and the activation supplies it.
        var perCounter = ManaPerCounterRemoved().Match(text);
        if (perCounter.Success)
        {
            var symbol = perCounter.Groups["m"].Value;

            var colour = symbol switch
            {
                "{W}" => ManaColor.White,
                "{U}" => ManaColor.Blue,
                "{B}" => ManaColor.Black,
                "{R}" => ManaColor.Red,
                "{G}" => ManaColor.Green,
                _ => (ManaColor?)null,
            };

            if (colour is null && symbol != "{C}")
                return [];

            return
            [
                ImmutableList.Create(new ManaProduction(colour, 1, FromCounterCost: true)),
            ];
        }

        var combination = AnyCombinationMana().Match(text);
        if (combination.Success)
        {
            var count = EffectPhrase.Number(combination.Groups["n"].Value).Fixed;
            if (count is < 1 or > 2)
                return [];

            var colours = new[]
            {
                ManaColor.White, ManaColor.Blue, ManaColor.Black, ManaColor.Red, ManaColor.Green,
            };

            if (count == 1)
                return [.. colours.Select(c => ImmutableList.Create(new ManaProduction(c, 1)))];

            var pairs = ImmutableList.CreateBuilder<ImmutableList<ManaProduction>>();

            for (var first = 0; first < colours.Length; first++)
            {
                for (var second = first; second < colours.Length; second++)
                {
                    // Two of one colour is one production of two, not two of one - the pool
                    // counts mana and does not care how it was described.
                    pairs.Add(first == second
                        ? ImmutableList.Create(new ManaProduction(colours[first], 2))
                        : ImmutableList.Create(
                            new ManaProduction(colours[first], 1),
                            new ManaProduction(colours[second], 1)));
                }
            }

            return pairs.ToImmutable();
        }

        if (AnyColor().IsMatch(text))
        {
            // "One mana of any color" is one ability per colour, and the player picks by
            // choosing which to activate. The *count* was not read at all: "add two mana of any
            // one color" made one mana and "three" made one, so half a dozen rocks and every
            // filter land of that shape paid out a fraction of what they print.
            var many = AnyColorCount().Match(text) is { Success: true } counted
                ? EffectPhrase.Number(counted.Groups["n"].Value).Fixed
                : 1;

            return
            [
                .. new[] { ManaColor.White, ManaColor.Blue, ManaColor.Black, ManaColor.Red, ManaColor.Green }
                    .Select(c => ImmutableList.Create(new ManaProduction(c, many))),
            ];
        }

        var alternatives = ImmutableList.CreateBuilder<ImmutableList<ManaProduction>>();

        foreach (var option in SplitOr(text))
        {
            var produced = Fixed(option);
            if (produced.IsEmpty)
                return [];

            alternatives.Add(produced);
        }

        return alternatives.ToImmutable();
    }

    /// <summary>"{G}, {W}, or {U}" and "{G} or {W}" both split into their options.</summary>
    private static IEnumerable<string> SplitOr(string text)
    {
        var parts = OrSeparator().Split(text);
        foreach (var part in parts)
        {
            var trimmed = part.Trim().Trim(',');
            if (trimmed.Length > 0)
                yield return trimmed;
        }
    }

    /// <summary>One fixed production, e.g. "{C}{C}" or "{G}".</summary>
    private static ImmutableList<ManaProduction> Fixed(string text)
    {
        var produced = ImmutableList.CreateBuilder<ManaProduction>();

        foreach (Match sym in ManaSymbolRef().Matches(text))
        {
            var body = sym.Groups[1].Value;

            if (int.TryParse(body, NumberStyles.Integer, CultureInfo.InvariantCulture, out var generic))
            {
                produced.Add(ManaProduction.Colorless(generic));
                continue;
            }

            if (body.Equals("C", StringComparison.OrdinalIgnoreCase))
            {
                produced.Add(ManaProduction.Colorless());
                continue;
            }

            var colour = body.ToUpperInvariant() switch
            {
                "W" => (ManaColor?)ManaColor.White,
                "U" => ManaColor.Blue,
                "B" => ManaColor.Black,
                "R" => ManaColor.Red,
                "G" => ManaColor.Green,
                _ => null,
            };

            if (colour is null)
                return [];

            produced.Add(new ManaProduction(colour.Value, 1));
        }

        return produced.ToImmutable();
    }

    [GeneratedRegex(@"^one mana of the chosen color$", RegexOptions.IgnoreCase)]
    private static partial Regex ChosenColorMana();

    [GeneratedRegex(@"any color|any type|any one color", RegexOptions.IgnoreCase)]
    private static partial Regex AnyColor();

    [GeneratedRegex(
        @"^(?<n>one|two|three|four|five|[0-9]+) mana in any combination of colors$",
        RegexOptions.IgnoreCase)]
    private static partial Regex AnyCombinationMana();

    [GeneratedRegex(
        @"^(?<m>\{[WUBRGC]\}) for each [a-z ]*counter removed this way$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ManaPerCounterRemoved();

    /// <remarks>
    /// Anchored at the start so it reads the count this clause names and not a number from
    /// somewhere else in the sentence.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<n>one|two|three|four|five|[0-9]+) mana of any", RegexOptions.IgnoreCase)]
    private static partial Regex AnyColorCount();

    [GeneratedRegex(@",?\s+or\s+|,\s*", RegexOptions.IgnoreCase)]
    private static partial Regex OrSeparator();

    [GeneratedRegex(@"\{([^}]+)\}")]
    private static partial Regex ManaSymbolRef();
}
