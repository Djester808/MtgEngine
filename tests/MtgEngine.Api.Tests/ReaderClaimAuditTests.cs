using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.Cards;
using Xunit.Abstractions;

namespace MtgEngine.Api.Tests;

/// <summary>
/// Readers that claim a line a better reader would have read.
/// </summary>
/// <remarks>
/// The compiler offers each line to a chain of matchers and takes the first that recognises it.
/// That makes two failures possible which no other instrument here can see, because both leave a
/// perfectly ordinary artefact behind:
/// <para>
/// <b>A reader nothing ever reaches.</b> "Enchanted creature gets +0/+1 until end of turn" had a
/// matcher of its own and an effect of its own, and neither could ever run: a general rewrite
/// 1,400 lines earlier claims the same sentence, turns it into the targeted pump and wraps that in
/// an <c>OnAttached</c>. The dead half was widened by hand, the live half by every improvement to
/// the targeted pump, and the two had drifted apart — a second implementation waiting for a
/// reordering to wake it up. Coverage could not see it (the line reads), the invariants could not
/// (the card is well formed), and the behaviour tests could not (the surviving route passes them).
/// </para>
/// <para>
/// <b>A reader that matches and then refuses.</b> It has consumed the line's chance without
/// reading it, and every matcher after it — including the ones written for exactly that
/// sentence — is never offered the line at all. This is not a defect on its own: most refusals
/// here are deliberate, because half a card read is worse than none. It becomes one when the
/// refusal is the reader's own vocabulary falling short of a conjunction its neighbours can read.
/// Three families were exactly that and are fixed in the commit that adds this file — protection
/// printed with the noun once, a two-filter cost modifier, and a list after "can't be blocked
/// by" — 28 cards between them, each with a one-noun twin that read perfectly.
/// </para>
/// <para>
/// <b>False positives are real and are named, not suppressed.</b> An overlapping pattern is
/// usually deliberate, with the general arm as the correct fallback; every entry in the two lists
/// below says which it is and why.
/// </para>
/// </remarks>
public sealed class ReaderClaimAuditTests(ITestOutputHelper output)
{
    /// <summary>
    /// Effects the compiler contains code to build and never builds, each with the reason.
    /// </summary>
    /// <remarks>
    /// An entry here is a claim that the code is deliberately unreachable over the whole corpus.
    /// There is no such reason today: a reader nothing reaches is a reader nothing maintains, and
    /// the answer is to delete it and let the route that does run keep the mechanic. The list
    /// exists so that a future one has to be argued for rather than merged.
    /// </remarks>
    private static readonly ImmutableDictionary<string, string> AcceptedUnreachableEffects =
        ImmutableDictionary<string, string>.Empty;

    /// <summary>
    /// How many cards one reader may leave one line short before it has to be explained.
    /// </summary>
    /// <remarks>
    /// A ratchet on the same footing as the coverage floor: it records what is true rather than
    /// what ought to be, and it may only come down. Cards are counted rather than lines because
    /// a template on nine hundred cards that all have three other unread lines completes nobody.
    /// </remarks>
    private const int MostCardsAReaderMayClaimAndDrop = 25;

    /// <summary>
    /// Readers above the threshold, each with the reason its refusals are right.
    /// </summary>
    /// <remarks>
    /// Two shapes are legitimate and both appear here. The first is a <em>wrapper</em>: a trigger,
    /// an activated ability, a modal bullet or a loyalty ability recognises the frame and hands
    /// what is inside it to the shared sentence vocabulary, so the line is unread because of the
    /// sentence and not because of the frame. The second is a <em>fail-closed refusal</em>: the
    /// reader understands the shape, finds it says something the engine cannot play, and leaves
    /// the line in the work queue rather than compiling a card better or different than printed.
    /// <para>
    /// What must never be here is the third shape — a reader whose own vocabulary is short of a
    /// conjunction, a list or a spelling that the compiler reads perfectly one sentence away.
    /// That is a gap to close, and the three closed in this commit were all found by reading this
    /// list top down.
    /// </para>
    /// </remarks>
    private static readonly ImmutableDictionary<string, string> AcceptedClaims =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // --- Wrappers: the frame reads, the sentence inside it does not ----------------
            ["CardCompiler.TriggerLine"] =
                "every trigger's outer form; what defeats these lines is the effect sentence",
            ["CardCompiler.ActivatedLine"] =
                "every activated ability's cost/effect split; the effect is what is unread",
            ["CardCompiler.ModalBullet"] =
                "a mode's bullet; the mode's own sentence is what the vocabulary refused",
            ["CardCompiler.LoyaltyLine"] =
                "a loyalty ability's cost and effect; the effect is what is unread",
            ["CardCompiler.SagaChapterLine"] =
                "a chapter's numerals and its effect; the effect is what is unread",
            ["CardCompiler.InterveningIf"] =
                "the CR 603.4 frame; the condition or the effect behind it is what failed",
            ["EffectPhrase.ConditionalSentence"] =
                "the same frame one layer in, and the same answer",
            ["EffectPhrase.UnlessSentence"] =
                "the CR 118.5 frame; what is unread is the cost or the effect it guards",
            ["EffectPhrase.IfYouDidLine"] =
                "the CR 608.2 frame for a paid-for rider; the rider's own sentence is the blocker",
            ["EffectPhrase.MayDoLine"] = "the optional frame; the instruction inside it is unread",
            ["EffectPhrase.MayPayLine"] = "the same, for the forms that name a cost",
            ["EffectPhrase.UnlessTheyPayLine"] =
                "the frame for a ransom clause; the payment or the effect is what failed",
            ["CardCompiler.GroupPhrase"] =
                "the last-resort keyword-list arm; a bare word nothing else claimed lands here",
            ["CardCompiler.MassStaticLine"] =
                "the lord frame; the group noun or the granted ability is what is unread",
            ["CardCompiler.ConditionalFrameLine"] =
                "\"as long as …\" around another static; the condition or the static is unread",
            ["CardCompiler.ConditionalStaticLine"] = "the same frame, narrower, and the same answer",
            ["CardCompiler.ManaAbilityLine"] =
                "the mana frame; the rider or the restriction after the mana is what failed",
            ["CardCompiler.AdditionalCostLine"] =
                "the CR 601.2b frame; the cost it names is what the cost vocabulary refused",
            ["CardCompiler.ConjoinedAttachedLine"] =
                "the attached-conjunction fold, offered only lines every other reader refused; "
                    + "one clause of the conjunction is what is unread",
            ["CardCompiler.GrantedAbilityLine"] =
                "\"[subject] has \\\"…\\\"\" — the quotation is compiled as a card of its own, and "
                    + "it is that card's text that is unread",
            ["EffectPhrase.GrantsQuotedAbilityLine"] = "the same, for a grant that lasts a turn",
            ["EffectPhrase.MultiTargetLine"] =
                "rewrites \"two target creatures\" to the singular and reads that; what fails is "
                    + "the singular sentence, which no other reader would do better with",
            ["EffectPhrase.AnyNumberTargetLine"] = "the same rewrite for the unbounded form",
            ["EffectPhrase.LeadingForEachLine"] =
                "fronted counting, tried last and falling through to the \"and\" split on "
                    + "failure; it consumes nothing",
            ["EffectPhrase.TrailingInsteadSentence"] =
                "the CR 608.2c replacement frame; the branch it replaces is what is unread",
            ["EffectPhrase.RollDieLine"] =
                "the CR 706 frame; the result rows carry the sentences that failed",
            ["EffectPhrase.LosesAllAbilitiesSentence"] =
                "the CR 613.1f frame; what it is joined to is what failed",
            ["CardCompiler.AttachedBuffLine"] =
                "the Aura/Equipment frame; a keyword or a clause it carries is what is unread",
            ["CardCompiler.AttachedLosesAllAbilitiesLine"] =
                "the same frame for the animating form; the new body is what failed",
            ["CardCompiler.DefinedPowerToughnessLine"] =
                "\"~'s power is equal to …\"; the counting phrase is what the vocabulary refused",
            ["CardCompiler.CountingStaticLine"] = "the same, for the additive form",
            ["CardCompiler.AlternativeCardPart"] =
                "a cost paid with cards; what is unread is the ability the cost buys",

            // --- Fail-closed refusals: the shape is read and refused on purpose ------------
            ["CardCompiler.EntersAsACopyLine"] =
                "CR 706.2 — an exception clause the copy grammar cannot express is refused whole, "
                    + "because a copy missing its exception is a different permanent",
            ["EffectPhrase.MassGrantsQuotedAbilityLine"] =
                "refuses rather than granting a blank: a creature that gained an ability the "
                    + "compiler could not read would look like it had it and do nothing",
            ["CardCompiler.CountedCostReductionLine"] =
                "refuses a count the shared counting vocabulary cannot read, rather than "
                    + "compiling a reduction that is a flat {0}",
            ["CardCompiler.ConditionalCostReductionLine"] =
                "refuses a condition BoardConditions cannot answer, rather than an unconditional "
                    + "discount — which is a strictly better card than the printed one",
            ["CardCompiler.AlternativeManaCostLine"] =
                "the same refusal for an alternative cost, where reading it unconditionally "
                    + "would let the spell be cast for the cheap price at any time",
            ["CardCompiler.CastOnlyLine"] =
                "a timing or board restriction the compiler cannot enforce is refused: a card "
                    + "that drops its own restriction is castable when it may not be",
            ["CardCompiler.EntersWithCountersPerGroupLine"] =
                "refuses a count it cannot read rather than entering with none",
            ["CardCompiler.SpellCostModifierLine"] =
                "refuses a filter the shared card vocabulary cannot name; taxing or discounting "
                    + "more spells than printed is the silent direction of wrong",
            ["EffectPhrase.PeerGroupLine"] =
                "\"and each other creature that shares …\" — refused unless both halves read, "
                    + "because half of a peer group is a different set",
            ["EffectPhrase.TwoPartDamageLine"] =
                "refused unless every recipient reads: a burn spell that drops one of its two "
                    + "halves deals less damage than printed and never fails",
            ["CardCompiler.GroupLandTypeLine"] =
                "sits last in the static chain and refuses a noun the group vocabulary cannot "
                    + "name, rather than typing a group nothing is in",
        }.ToImmutableDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Every effect the compiler can construct is constructed for some real card.
    /// </summary>
    /// <remarks>
    /// Asked of the compiled IL and of the corpus rather than of the source, for the reason
    /// <c>DeadWriteAuditTests</c> gives: a source scan cannot tell a call from a doc-comment, and
    /// a running game cannot see a branch it did not take. A <c>newobj</c> in
    /// <c>MtgEngine.Rules.Cards</c> says the compiler has code that builds this effect; a walk of
    /// every compiled card says whether any card ever gets one. The difference is a reader
    /// something else claims first.
    /// </remarks>
    [Fact]
    public void Every_effect_the_compiler_can_build_is_built_for_some_card()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present — skipping.");
            return;
        }

        var buildable = BuildableEffects();
        var reached = new HashSet<Type>();

        foreach (var card in corpus)
        {
            CompiledCard compiled;
            try
            {
                compiled = CardCompiler.Compile(card);
            }
            catch (Exception)
            {
                // A card the compiler throws on is a different defect, with its own suite.
                continue;
            }

            Reached(compiled, reached, new HashSet<object>(ReferenceEqualityComparer.Instance), 0);
        }

        output.WriteLine($"effects the compiler can build: {buildable.Count}");
        output.WriteLine($"effects some corpus card carries: {reached.Count(buildable.ContainsKey)}");

        var findings = buildable
            .Where(pair => !reached.Contains(pair.Key))
            .Where(pair => !AcceptedUnreachableEffects.ContainsKey(pair.Key.Name))
            .Select(pair => $"{pair.Key.Name} (built by {string.Join(", ", pair.Value)})")
            .Order(StringComparer.Ordinal)
            .ToList();

        foreach (var finding in findings)
            output.WriteLine("  unreachable: " + finding);

        Assert.True(
            findings.Count == 0,
            "these effects have code that builds them and no card that gets one — read the "
                + "reader that builds each and find which earlier matcher claims the same "
                + "sentence, then delete the shadowed half or move it in front: "
                + string.Join("; ", findings));
    }

    /// <summary>
    /// No reader is the sole claimant of more cards than the ratchet allows without a reason.
    /// </summary>
    /// <remarks>
    /// The counted thing is a card that is <em>one line short</em>, and a whole-line reader whose
    /// pattern matches that line. That reader is what the compiler offered the line to, and every
    /// matcher behind it in the chain never saw it — so if the refusal is wrong, this is where
    /// the card was lost.
    /// <para>
    /// The threshold is a ratchet rather than a rule because most of this list is correct: a
    /// wrapper is supposed to claim a line whose inner sentence is unread, and a fail-closed
    /// refusal is supposed to leave a card in the work queue. What the list is for is the fourth
    /// or fifth entry down that is neither — a reader short of a conjunction its neighbours read
    /// — and reading it top down is how the three families fixed alongside it were found.
    /// </para>
    /// </remarks>
    [Fact]
    public void No_reader_claims_and_drops_more_cards_than_the_ratchet_allows()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present — skipping.");
            return;
        }

        var readers = WholeLineReaders();
        output.WriteLine($"whole-line readers: {readers.Count}");

        // The line a card is one short of, and how many cards are in that position.
        var oneShort = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var card in corpus)
        {
            CompiledCard compiled;
            try
            {
                compiled = CardCompiler.Compile(card);
            }
            catch (Exception)
            {
                continue;
            }

            var unread = compiled.Unhandled.Distinct(StringComparer.Ordinal).ToList();
            if (unread.Count != 1)
                continue;

            oneShort[unread[0]] = oneShort.GetValueOrDefault(unread[0]) + 1;
        }

        var claimed = new Dictionary<string, (int Cards, List<string> Lines)>(StringComparer.Ordinal);

        foreach (var (line, cards) in oneShort)
        {
            foreach (var (name, rx) in readers)
            {
                bool hit;
                try
                {
                    hit = rx.IsMatch(line);
                }
                catch (RegexMatchTimeoutException)
                {
                    continue;
                }

                if (!hit)
                    continue;

                if (!claimed.TryGetValue(name, out var seen))
                    seen = (0, []);

                seen.Cards += cards;
                if (seen.Lines.Count < 3)
                    seen.Lines.Add($"[{cards}] {line}");

                claimed[name] = seen;
            }
        }

        output.WriteLine("");
        output.WriteLine("readers ranked by the cards their claim leaves one line short:");

        foreach (var (name, seen) in claimed.OrderByDescending(p => p.Value.Cards).Take(40))
        {
            var known = AcceptedClaims.ContainsKey(name) ? "" : "  <-- unexplained";
            output.WriteLine($"{seen.Cards,6}  {name}{known}");

            foreach (var line in seen.Lines)
                output.WriteLine($"          {line}");
        }

        var findings = claimed
            .Where(p => p.Value.Cards > MostCardsAReaderMayClaimAndDrop)
            .Where(p => !AcceptedClaims.ContainsKey(p.Key))
            .Select(p => $"{p.Key} ({p.Value.Cards} cards, e.g. {p.Value.Lines.FirstOrDefault()})")
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            findings.Count == 0,
            $"these readers each match the only unread line of more than "
                + $"{MostCardsAReaderMayClaimAndDrop} cards and are not explained. Read the "
                + "reader: if the refusal is deliberate — a frame whose inner sentence failed, or "
                + "a shape it will not compile better than printed — say so in AcceptedClaims. If "
                + "it is a conjunction or a spelling the compiler reads one sentence away, close "
                + "the gap instead: " + string.Join("; ", findings));
    }

    /// <summary>Every effect type the compiler's own code contains a <c>newobj</c> for.</summary>
    /// <remarks>
    /// Restricted to <see cref="IEffect"/>, which is what a reader <em>produces</em>. The other
    /// ability records the compiler builds are transports — a value read apart into the fields of
    /// something else — and never appear on a compiled card even when the reader that built one
    /// ran on every card in the corpus.
    /// </remarks>
    private static IReadOnlyDictionary<Type, List<string>> BuildableEffects()
    {
        var byValue = typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(OpCode))
            .Select(f => (OpCode)f.GetValue(null)!)
            .ToDictionary(o => o.Value);

        var found = new Dictionary<Type, List<string>>();

        foreach (var type in typeof(CardCompiler).Assembly.GetTypes())
        {
            if (type.Namespace is not { } ns
                || !ns.StartsWith("MtgEngine.Rules.Cards", StringComparison.Ordinal))
            {
                continue;
            }

            var members = type
                .GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                    | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Cast<MethodBase>()
                .Concat(type.GetConstructors(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                    | BindingFlags.Static | BindingFlags.DeclaredOnly));

            foreach (var member in members)
                Walk(member, byValue, found);
        }

        return found;
    }

    private static void Walk(
        MethodBase method,
        Dictionary<short, OpCode> byValue,
        Dictionary<Type, List<string>> into)
    {
        byte[] il;
        try
        {
            if (method.GetMethodBody()?.GetILAsByteArray() is not { } body)
                return;

            il = body;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        var typeArguments = method.DeclaringType?.IsGenericType == true
            ? method.DeclaringType.GetGenericArguments()
            : null;
        var methodArguments = method is MethodInfo { IsGenericMethodDefinition: true } generic
            ? generic.GetGenericArguments()
            : null;

        var i = 0;
        while (i < il.Length)
        {
            var first = il[i++];
            var value = first == 0xFE && i < il.Length
                ? unchecked((short)((0xFE << 8) | il[i++]))
                : (short)first;

            if (!byValue.TryGetValue(value, out var opcode))
                return;

            var operand = i;
            i += OperandSize(opcode, il, operand);
            if (i > il.Length)
                return;

            if (opcode.OperandType is not (OperandType.InlineMethod or OperandType.InlineTok))
                continue;

            MethodBase? callee;
            try
            {
                callee = method.Module.ResolveMethod(
                    BitConverter.ToInt32(il, operand), typeArguments, methodArguments);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (callee is not ConstructorInfo built
                || built.DeclaringType is not { } made
                || !typeof(IEffect).IsAssignableFrom(made))
            {
                continue;
            }

            if (!into.TryGetValue(made, out var sites))
                into[made] = sites = [];

            var where = (method.DeclaringType?.Name ?? "?") + "." + method.Name;
            if (!sites.Contains(where, StringComparer.Ordinal))
                sites.Add(where);
        }
    }

    private static int OperandSize(OpCode opcode, byte[] il, int operand) =>
        opcode.OperandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI
                or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI
                or OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString
                or OperandType.InlineTok or OperandType.InlineType
                or OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, operand)),
            _ => int.MaxValue,
        };

    /// <summary>Every ability object reachable from a compiled card.</summary>
    /// <remarks>
    /// A plain walk of the public surface, which is where a compiled card keeps everything: the
    /// spell, the abilities, the modes, the alternate castings and the effects nested inside
    /// optional payments. Closures are not followed — an effect built inside one is built while a
    /// game runs rather than while a card compiles, and this is a question about compiling.
    /// </remarks>
    private static void Reached(object? node, HashSet<Type> into, HashSet<object> seen, int depth)
    {
        if (node is null || depth > 14)
            return;

        var type = node.GetType();

        if (type.IsPrimitive || type.IsEnum || node is string or Delegate or Guid)
            return;

        if (node is System.Collections.IEnumerable list)
        {
            foreach (var item in list)
                Reached(item, into, seen, depth + 1);

            return;
        }

        if (!seen.Add(node))
            return;

        if (node is IEffect)
            into.Add(type);

        if (type.Namespace is not { } owner
            || !owner.StartsWith("MtgEngine.Rules", StringComparison.Ordinal))
        {
            return;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || !property.CanRead)
                continue;

            object? value;
            try
            {
                value = property.GetValue(node);
            }
            catch (TargetInvocationException)
            {
                continue;
            }

            Reached(value, into, seen, depth + 1);
        }
    }

    /// <summary>Every compiled pattern in the compiler that claims a whole line.</summary>
    private static IReadOnlyList<(string Name, Regex Rx)> WholeLineReaders()
    {
        var found = new List<(string, Regex)>();

        foreach (var type in new[] { typeof(CardCompiler), typeof(EffectPhrase) })
        {
            Collect(type, type.Name, found);

            foreach (var nested in type.GetNestedTypes(
                BindingFlags.Public | BindingFlags.NonPublic))
            {
                Collect(nested, type.Name + "." + nested.Name, found);
            }
        }

        return found;
    }

    private static void Collect(Type type, string owner, List<(string, Regex)> into)
    {
        foreach (var method in type.GetMethods(
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (method.ReturnType != typeof(Regex) || method.GetParameters().Length != 0)
                continue;

            if (method.Invoke(null, null) is not Regex rx || !WholeLine(rx.ToString()))
                continue;

            into.Add((owner + "." + method.Name, rx));
        }
    }

    /// <summary>Whether a pattern anchors a whole line rather than a fragment inside one.</summary>
    /// <remarks>
    /// A caret and a dollar are not enough: <c>^(an?|the)\s+|\s+cards?$</c> has both and anchors
    /// neither, because the alternation splits it into two fragments that are matched anywhere.
    /// Only a pattern whose caret and dollar are on the same branch claims a line.
    /// </remarks>
    internal static bool WholeLine(string pattern)
    {
        if (!pattern.StartsWith('^'))
            return false;

        if (!pattern.EndsWith('$') && !pattern.EndsWith("$)", StringComparison.Ordinal))
            return false;

        var depth = 0;
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\\')
            {
                i++;
                continue;
            }

            if (c == '[')
            {
                while (i < pattern.Length && pattern[i] != ']')
                {
                    if (pattern[i] == '\\')
                        i++;

                    i++;
                }

                continue;
            }

            if (c == '(')
                depth++;
            else if (c == ')')
                depth--;
            else if (c == '|' && depth == 0)
                return false;
        }

        return true;
    }
}
