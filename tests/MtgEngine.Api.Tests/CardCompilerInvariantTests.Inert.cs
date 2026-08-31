using System.Collections.Immutable;
using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.Cards;
using MtgEngine.Rules.State;

namespace MtgEngine.Api.Tests;

/// <summary>
/// The inert-card audit: a card the compiler reads completely that can never do anything.
/// </summary>
/// <remarks>
/// This project's most expensive defect is not an unread card. An unread card is refused by the
/// legality gate and says so. It is a card that reads as <em>complete</em>, is legal, is cast,
/// resolves, writes a line to the game log, and changes nothing — coverage counts it as a win and
/// every unit suite stays green. Five shipped in one round and every one was found by accident:
/// Living Lore compiled a trigger that found nothing; Ojutai Exemplars exiled itself and then
/// looked for itself in a graveyard; "you may tap or untap target permanent" wrapped a question in
/// another question with the same locator, so neither was ever asked; "all Mountains are Plains"
/// became a lord for the creature <em>type</em> Mountain; stun counters were put on by 87 cards
/// and read by nothing.
/// <para>
/// The checks here are deliberately narrow. "Does nothing on this board" is not a defect — a
/// conditional that is false and a target that is absent are the game working — so nothing in this
/// file asks that question. Each check below is a claim of the stronger kind: <b>this card cannot
/// do anything on any board, ever</b>, because the thing it selects does not exist, or the effect
/// list it would run is empty, or the branch it defers to cannot be found again.
/// </para>
/// </remarks>
public sealed partial class CardCompilerInvariantTests
{
    /// <summary>
    /// Every filter a complete card names has to select some card in the corpus.
    /// </summary>
    /// <remarks>
    /// The sharpest of the four, because it needs no board and admits no "well, it depends". A
    /// filter is the whole of what an effect selects: what a search digs for, what a discard asks
    /// to be pitched, what a cost modifier cheapens. <c>SearchFilters.Matches</c> answers it the
    /// same way at runtime as it does here, so a filter no printed card answers to selects nothing
    /// in any game that will ever be played with these cards.
    /// <para>
    /// Its sibling <see cref="Every_subtype_a_filter_names_is_a_subtype_some_card_has"/> checks the
    /// <em>words</em> of a filter against the set of subtypes; this checks the <em>filter</em>
    /// against the set of cards. The difference is conjunction: a filter built of two words that
    /// are each a real subtype can still select nothing, because no card is both, and the
    /// word-wise check passes it — a whole class of dead filter only asking the corpus can see.
    /// </para>
    /// <para>
    /// The allowlist is types that exist only on tokens. No card is printed as a Clue or a
    /// Treasure, so a filter naming one selects nothing <em>in the corpus</em> and everything it
    /// was written for on a real board. That is the one honest false positive this check has, and
    /// naming them is cheaper than synthesising a token for each.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_filter_a_complete_card_names_selects_some_card()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present — skipping.");
            return;
        }

        // Filter string -> the cards carrying it. Memoised because the same filter is written by
        // hundreds of cards and answering it costs a scan of the corpus; asked once per distinct
        // string, the whole check is seconds rather than hours.
        var carriers = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);
            if (!compiled.IsComplete)
                continue;

            foreach (var filter in FiltersIn(compiled)
                .Concat(compiled.CostModifiers.Select(m => m.FilterId)))
            {
                if (filter.Length == 0)
                    continue;

                if (!carriers.TryGetValue(filter, out var who))
                    carriers[filter] = who = [];

                who.Add(card.Name);
            }
        }

        var dead = new List<string>();

        foreach (var (filter, who) in carriers)
        {
            // "A card named ~" is the card naming itself, and the compiler has no name to put
            // there because it reads a sentence rather than a card. Both effects that can carry
            // one - SearchLibrary and Seek - substitute the source's own name as they resolve, so
            // this selects the right 23 cards at runtime and nothing here. Skipped by asking for
            // the tilde rather than by listing the cards, because the substitution is the reason.
            if (filter.Contains('~', StringComparison.Ordinal))
                continue;

            if (TokenOnlyTypes.Overlaps(
                filter.Split(['|', '&'], StringSplitOptions.RemoveEmptyEntries)))
            {
                continue;
            }

            if (corpus.Any(card => SearchFilters.Matches(filter, card)))
                continue;

            var names = who.Distinct(StringComparer.Ordinal).ToList();
            dead.Add(
                $"'{filter}' selects no card at all — {names.Count} cards: "
                    + string.Join(", ", names.Take(8)));
        }

        output.WriteLine($"distinct filters on complete cards: {carriers.Count}");
        foreach (var line in dead)
            output.WriteLine("  " + line);

        Assert.True(
            dead.Count == 0,
            $"{dead.Count} filters select no card in the corpus, so every card carrying one is "
                + "complete, playable and inert:\n  " + string.Join("\n  ", dead.Take(40)));
    }

    /// <summary>
    /// Subtypes that exist only on tokens, which is the one reason a live filter selects no card.
    /// </summary>
    private static readonly HashSet<string> TokenOnlyTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Clue", "Food", "Treasure", "Blood", "Gold", "Powerstone", "Incubator", "Map", "Junk",
        "Army", "Servo", "Thopter", "Role", "Shard", "Walker",
    };

    /// <summary>
    /// Every ability a complete card declares has to carry something to do.
    /// </summary>
    /// <remarks>
    /// <see cref="Every_complete_card_with_rules_text_declares_some_behaviour"/> asks this of the
    /// whole card and is therefore satisfied by any one line working. A card with four printed
    /// lines, three of which compile and one of which compiles to an empty effect list, passes it
    /// — and that fourth line is exactly Living Lore's defect: an ability that exists, is offered,
    /// goes on the stack, resolves, and runs nothing.
    /// <para>
    /// Asked per ability, so the unit is the printed sentence rather than the card. A mana ability
    /// is exempt because its whole purpose is in <c>Produces</c> and the structural check already
    /// holds it to that.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_ability_a_complete_card_declares_carries_an_effect()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present — skipping.");
            return;
        }

        var empty = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        var abilities = 0;

        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);
            if (!compiled.IsComplete)
                continue;

            foreach (var trigger in compiled.Triggers)
            {
                abilities++;
                if (!trigger.Effects.IsEmpty || !trigger.Modes.IsEmpty)
                    continue;

                Note(empty, $"trigger \"{trigger.Text}\"", card.Name);
            }

            foreach (var mode in compiled.Triggers.SelectMany(t => t.Modes))
            {
                abilities++;
                if (!mode.Effects.IsEmpty)
                    continue;

                Note(empty, $"trigger mode \"{mode.Text}\"", card.Name);
            }

            foreach (var ability in compiled.Activated)
            {
                abilities++;

                // A mana ability's purpose is its production, and the structural check already
                // fails an empty one. Everything else has to do something on resolution.
                if (!ability.Effects.IsEmpty || ability.IsManaAbility)
                    continue;

                Note(empty, $"activated \"{ability.Text}\"", card.Name);
            }
        }

        output.WriteLine($"abilities inspected: {abilities}");
        foreach (var (shape, who) in empty)
            output.WriteLine($"  {shape} — {who.Count}: {string.Join(", ", who.Take(6))}");

        Assert.True(
            empty.Count == 0,
            $"{empty.Sum(e => e.Value.Count)} abilities compile to an empty effect list, so they "
                + "are offered, resolve and do nothing:\n  "
                + string.Join(
                    "\n  ",
                    empty.Take(40).Select(e => $"{e.Key} — {string.Join(", ", e.Value.Take(4))}")));
    }

    /// <summary>
    /// Every question an effect defers has to be findable again in its own ability.
    /// </summary>
    /// <remarks>
    /// A mid-resolution question — an optional payment, a clash, a coin, a die — is answered later
    /// by a separate player action, and the branch it chooses between is read back out of the
    /// card's compiled definition through <c>EffectTree.Locate</c>, keyed on the locator the effect
    /// carries. <b>Locate returns null on ambiguity rather than guessing</b>, and null is silent:
    /// the question is never asked, no branch runs, and the card resolves to nothing.
    /// <para>
    /// That is what "you may tap or untap target permanent" did. The offer reader wrapped one
    /// question inside another and gave both locator 0, so the pair was indistinguishable and
    /// <em>neither</em> was ever put to the player. Four cards: complete, cast, silent.
    /// </para>
    /// <para>
    /// The check is the lookup itself, run over every deferred question every complete card
    /// carries, asking the tree the same thing <c>Game.Resume</c> will ask it. Nothing about the
    /// board can change the answer, which is what makes a failure here a certainty rather than a
    /// candidate.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_deferred_question_can_be_found_again_where_it_was_left()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present — skipping.");
            return;
        }

        var lost = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        var questions = 0;

        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);
            if (!compiled.IsComplete)
                continue;

            foreach (var (effects, _) in Slices(compiled))
            {
                foreach (var question in EffectTree.Flatten(effects))
                {
                    if (EffectTree.LocatorOf(question) is not { } locator)
                        continue;

                    questions++;

                    // Asked through the generic the engine uses, so this is the lookup and not a
                    // restatement of it. A second effect of the same kind with the same locator
                    // makes Locate answer null, which is the whole failure.
                    if (Found(effects, question, locator))
                        continue;

                    Note(lost, $"{question.GetType().Name} at {locator}", card.Name);
                }
            }
        }

        output.WriteLine($"deferred questions inspected: {questions}");
        foreach (var (shape, who) in lost)
            output.WriteLine($"  {shape} — {who.Count}: {string.Join(", ", who.Take(6))}");

        Assert.True(
            lost.Count == 0,
            $"{lost.Sum(e => e.Value.Count)} deferred questions cannot be found again, so the "
                + "player is never asked and the branch never runs:\n  "
                + string.Join(
                    "\n  ",
                    lost.Take(40).Select(e => $"{e.Key} — {string.Join(", ", e.Value.Take(4))}")));
    }

    /// <summary>Whether the engine's own lookup finds this exact question and no other.</summary>
    /// <remarks>
    /// <c>EffectTree.Locate</c> is generic in the effect kind, and the kind is only known at
    /// runtime here, so the call is made through reflection rather than reimplemented. Restating
    /// it would be the drift this suite exists to prevent — a check that agrees with itself and
    /// not with the engine.
    /// </remarks>
    private static bool Found(ImmutableList<IEffect> effects, IEffect question, int locator)
    {
        var located = typeof(EffectTree)
            .GetMethod(nameof(EffectTree.Locate))!
            .MakeGenericMethod(question.GetType())
            .Invoke(null, [effects, locator]);

        return ReferenceEquals(located, question);
    }

    /// <summary>
    /// No complete card may read a source it has already put somewhere else.
    /// </summary>
    /// <remarks>
    /// Ojutai Exemplars and Estrid's Invocation, both complete, both silent. A phrase holding
    /// <c>ExileSource</c> and <c>ReturnSourceFromGraveyard</c> is a <em>blink</em> — "exile it,
    /// then return it" — and compiling the return half as the graveyard recursion of the same name
    /// means the card is in exile when the return goes looking in the graveyard. Nothing throws.
    /// Nothing is logged. The permanent is simply gone.
    /// <para>
    /// The general shape is an effect list whose earlier member moves the source to a zone its
    /// later member does not look in, and it is checkable without a board because both zones are
    /// written into the effect types themselves. Only pairs the engine can be certain about are
    /// listed: an effect that moves the source somewhere definite, followed by one that reads the
    /// source somewhere else.
    /// </para>
    /// </remarks>
    [Fact]
    public void No_complete_card_reads_a_source_it_has_already_moved_elsewhere()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present — skipping.");
            return;
        }

        var stranded = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);
            if (!compiled.IsComplete)
                continue;

            foreach (var (effects, _) in Slices(compiled))
            {
                var flat = EffectTree.Flatten(effects).ToList();

                for (var i = 0; i < flat.Count; i++)
                {
                    if (MovesSourceTo(flat[i]) is not { } landed)
                        continue;

                    for (var j = i + 1; j < flat.Count; j++)
                    {
                        if (ReadsSourceIn(flat[j]) is not { } wanted || wanted == landed)
                            continue;

                        Note(
                            stranded,
                            $"{flat[i].GetType().Name} puts it in {landed}, then "
                                + $"{flat[j].GetType().Name} looks in {wanted}",
                            card.Name);
                    }
                }
            }
        }

        foreach (var (shape, who) in stranded)
            output.WriteLine($"  {shape} — {who.Count}: {string.Join(", ", who.Take(6))}");

        Assert.True(
            stranded.Count == 0,
            $"{stranded.Sum(e => e.Value.Count)} cards move their own source to one zone and then "
                + "read it in another, so the second half silently finds nothing:\n  "
                + string.Join(
                    "\n  ",
                    stranded.Take(40)
                        .Select(e => $"{e.Key} — {string.Join(", ", e.Value.Take(4))}")));
    }

    /// <summary>Where an effect definitely puts the source, when it puts it somewhere definite.</summary>
    private static Zone? MovesSourceTo(IEffect effect) => effect switch
    {
        ExileSource => Zone.Exile,
        ReturnSourceToHand => Zone.Hand,
        PutSourceOnLibrary => Zone.Library,
        ShuffleSourceIntoLibrary => Zone.Library,
        _ => null,
    };

    /// <summary>Where an effect expects the source, when it reads it from one zone only.</summary>
    private static Zone? ReadsSourceIn(IEffect effect) => effect switch
    {
        ReturnSourceFromGraveyard => Zone.Graveyard,
        UnearthSource => Zone.Graveyard,
        ReturnSourceFromBattlefield => Zone.Battlefield,
        UntapSource => Zone.Battlefield,
        PutCountersOnSource => Zone.Battlefield,
        _ => null,
    };

    /// <summary>
    /// A trigger may not be pinned to a zone its card can never be in (CR 603.6).
    /// </summary>
    /// <remarks>
    /// <c>Game</c> asks one question before it offers any trigger the event: <c>if (obj.Zone !=
    /// ability.FunctionsFrom) continue;</c>. A triggered ability on an instant or a sorcery that
    /// says it functions from the battlefield is therefore asked about no event ever, because an
    /// instant never becomes a permanent (CR 608.2m) — the card compiles, reads as complete, is
    /// cast, and the ability it was compiled for is unreachable.
    /// <para>
    /// The default is the battlefield, which is what makes this worth a check rather than a
    /// comment: a reader that forgets to set the zone produces exactly this, and produces it
    /// silently. The card types are taken from the whole printed type line, so a card with any
    /// permanent face at all — an adventure, a split half, a modal back — is outside the check.
    /// </para>
    /// </remarks>
    [Fact]
    public void No_trigger_functions_from_a_zone_its_card_can_never_reach()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present — skipping.");
            return;
        }

        const MtgEngine.Domain.Enums.CardType Permanents =
            MtgEngine.Domain.Enums.CardType.Artifact
            | MtgEngine.Domain.Enums.CardType.Creature
            | MtgEngine.Domain.Enums.CardType.Enchantment
            | MtgEngine.Domain.Enums.CardType.Land
            | MtgEngine.Domain.Enums.CardType.Planeswalker
            | MtgEngine.Domain.Enums.CardType.Battle;

        var unreachable = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var card in corpus)
        {
            if ((card.CardTypes & Permanents) != 0)
                continue;

            var compiled = CardCompiler.Compile(card);
            if (!compiled.IsComplete)
                continue;

            foreach (var trigger in compiled.Triggers)
            {
                if (trigger.FunctionsFrom != Zone.Battlefield)
                    continue;

                Note(unreachable, $"\"{trigger.Text}\"", card.Name);
            }
        }

        foreach (var (shape, who) in unreachable)
            output.WriteLine($"  {shape} — {who.Count}: {string.Join(", ", who.Take(6))}");

        Assert.True(
            unreachable.Count == 0,
            $"{unreachable.Sum(e => e.Value.Count)} triggers wait on the battlefield for a card "
                + "that never gets there, so they are asked about no event ever:\n  "
                + string.Join(
                    "\n  ",
                    unreachable.Take(40)
                        .Select(e => $"{e.Key} — {string.Join(", ", e.Value.Take(4))}")));
    }

    /// <summary>
    /// A modal ability has to choose at least one mode (CR 700.2).
    /// </summary>
    /// <remarks>
    /// Modes are the whole of what a modal spell does: nothing is in the top-level effect list,
    /// because every clause sits inside a mode. An ability that carries modes and asks for none
    /// resolves by picking nothing and running nothing — the same silence as an empty effect
    /// list, one indirection along, and invisible to the check that looks at the list.
    /// </remarks>
    [Fact]
    public void Every_modal_ability_chooses_at_least_one_mode()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present — skipping.");
            return;
        }

        var mute = new List<string>();

        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);
            if (!compiled.IsComplete)
                continue;

            foreach (var trigger in compiled.Triggers)
            {
                if (!trigger.Modes.IsEmpty && trigger.ModesToChoose <= 0)
                    mute.Add($"{card.Name}: trigger \"{trigger.Text}\"");
            }

            foreach (var spell in EverySpell(compiled))
            {
                if (!spell.Modes.IsEmpty && spell.ModesToChoose <= 0)
                    mute.Add($"{card.Name}: {spell.Modes.Count} modes, none chosen");
            }
        }

        Assert.True(
            mute.Count == 0,
            $"{mute.Count} modal abilities pick no mode, so they resolve and run nothing:\n  "
                + string.Join("\n  ", mute.Take(40)));
    }

    /// <summary>Every castable spell definition a compiled card carries, alternates included.</summary>
    /// <remarks>
    /// Found by type over the record's own properties, the way <see cref="Slices"/> finds them,
    /// so the sixth alternate casting is walked on the day it is added rather than on the day
    /// somebody remembers this list.
    /// </remarks>
    private static IEnumerable<SpellDefinition> EverySpell(CompiledCard compiled)
    {
        foreach (var property in typeof(CompiledCard).GetProperties())
        {
            if (property.PropertyType == typeof(SpellDefinition)
                && property.GetValue(compiled) is SpellDefinition spell)
            {
                yield return spell;
            }
        }

        foreach (var half in compiled.Halves)
        {
            if (half.Spell is { } face)
                yield return face;
        }
    }

    /// <summary>
    /// An effect whose whole size is a fixed zero — a report, and the reasoning behind it.
    /// </summary>
    /// <remarks>
    /// <c>Amount.In</c> comes to a fixed zero in two shapes: a plain <c>0</c>, and <c>0</c> with a
    /// "for each" multiplier, which multiplies the count by zero. Draw zero cards, gain zero life,
    /// put zero counters — the effect resolves, emits its event with a zero in it, and nothing on
    /// the board moves. That is the shape the counting phrase produced when it resolved
    /// "Equipment" to a creature subtype nothing has and the card gained 0 life instead of 2.
    /// <para>
    /// <b>This asserts nothing.</b> An amount is not the whole of every effect that carries one,
    /// and a zero can be deliberate; the honest form of the check is to print the shapes and let
    /// a reader judge, which is what the untargeted-resolution report next door does for the same
    /// reason. What it is for is the next zero, which will not be deliberate.
    /// </para>
    /// </remarks>
    [Fact]
    public void Effects_whose_whole_size_is_a_fixed_zero_are_reported()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present — skipping.");
            return;
        }

        var zero = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);
            if (!compiled.IsComplete)
                continue;

            foreach (var effect in EveryCompiledEffect(compiled))
            {
                var amounts = effect.GetType().GetProperties()
                    .Where(p => p.PropertyType == typeof(Amount)
                        || p.PropertyType == typeof(Amount?))
                    .Select(p => p.GetValue(effect) as Amount?)
                    .Where(a => a is not null)
                    .Select(a => a!.Value)
                    .ToList();

                if (amounts.Count == 0 || !amounts.TrueForAll(DeadZero))
                    continue;

                Note(zero, effect.GetType().Name, card.Name);
            }
        }

        output.WriteLine("A report and not a gate: an amount is not the whole of every effect.");
        foreach (var (shape, who) in zero)
            output.WriteLine($"  {shape} — {who.Count}: {string.Join(", ", who.Take(8))}");
    }

    /// <summary>Whether this amount comes to zero however the game goes.</summary>
    /// <remarks>
    /// The "for each" arm is the one worth spelling out: <c>Amount.In</c> reads a counted amount
    /// as <c>Fixed * count</c>, so a fixed part of zero is zero whatever the count comes to. Only
    /// X escapes, because X is chosen as the spell is cast (CR 601.2b) and is not on the card.
    /// </remarks>
    private static bool DeadZero(Amount amount) => amount.Fixed == 0 && !amount.IsVariable;

    /// <summary>File one finding under the shape it has, so a class of them reads as one row.</summary>
    private static void Note(
        SortedDictionary<string, List<string>> found, string shape, string cardName)
    {
        if (!found.TryGetValue(shape, out var who))
            found[shape] = who = [];

        who.Add(cardName);
    }
}
