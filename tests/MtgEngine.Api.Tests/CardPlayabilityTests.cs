using System.Collections.Immutable;
using System.Text;
using MtgEngine.Api.Cards;
using MtgEngine.Api.Services;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;
using Xunit.Abstractions;

namespace MtgEngine.Api.Tests;

/// <summary>
/// Whether a card the compiler reads can be put in a deck and played.
/// </summary>
/// <remarks>
/// Coverage says every printed line of a card compiled. That is a claim about the compiler, not
/// about the game, and three separate things sit between the two:
/// <list type="number">
/// <item>the legality gate in <see cref="GameTableService"/>, which decides what a real deck may
/// contain - and which asks a different question from the compiler's, in both directions;</item>
/// <item>the soaks, which play a large subset of the compiled cards and leave a remainder that
/// nothing has ever put on a battlefield;</item>
/// <item>the runtime itself, where a card that compiles can still throw.</item>
/// </list>
/// <para>
/// Every number here is measured against the same corpus the coverage ratchet uses, and the first
/// test in the file exists only to prove that: a harness that quietly reads a different set of
/// cards can report anything it likes, and a harness with no corpus at all reads zero and passes.
/// </para>
/// <para>
/// <b>These are ratchets on the measured state, not statements of what ought to be true.</b>
/// PLAYABILITY.md says what each number should be and why the gap is there.
/// </para>
/// <para>
/// <b>"Not exercised" and "broken" are different claims</b> and are kept apart throughout. A card
/// no test plays is unverified; a card that throws is a defect. Only the last test in this file
/// can produce the second kind, and it currently produces none.
/// </para>
/// </remarks>
public sealed class CardPlayabilityTests(ITestOutputHelper output)
{
    /// <summary>The corpus size <c>CardCompilerCoverageTests</c> reports.</summary>
    // 32,717 rather than 32,765: the 48 Unfinity sticker sheets left the corpus when the
    // loader learned they are supplements, not cards - type line "Stickers", ticket costs, and
    // no deck may contain one, though Scryfall marks them legal. Four of them read fully and
    // were "complete cards" nothing could ever cast, which is how the census caught it.
    private const int CorpusCards = 32_717;

    /// <summary>The "fully read" count <c>CardCompilerCoverageTests</c> reports (45.9%).</summary>
    private const int CompleteCards = 15_500;

    /// <summary>How many cards share a battlefield, as the soak does it.</summary>
    private const int PerGame = 12;

    /// <summary>
    /// How many lands one game is asked to play from hand, and over how many turns.
    /// </summary>
    /// <remarks>
    /// Far fewer than <see cref="PerGame"/>, and the reason is worth keeping. A land drop is one
    /// per player per turn (CR 505.6b), so twelve lands need twelve turns - and over twelve turns
    /// a player draws past the maximum hand size, discards at cleanup (CR 514.1), and what they
    /// discard is the lands still waiting. The first run of this pass reached <b>64 of 759</b>
    /// drops for exactly that reason, and reported "No object … in the game" 695 times without
    /// noticing it was measuring its own discard step. Two per player over eight turns keeps
    /// every hand under the limit, which is why the number is small rather than tidy.
    /// </remarks>
    private const int PerHandGame = 4;

    private const int HandTurns = 8;

    private static readonly Lazy<Corpus?> Loaded = new(Load);

    /// <summary>The corpus split by whether the compiler read every line of the card.</summary>
    private sealed record Corpus(
        ImmutableList<CardDefinition> All,
        ImmutableList<CardDefinition> Complete,
        ImmutableList<CardDefinition> Incomplete);

    private static Corpus? Load()
    {
        var cards = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (cards is null)
            return null;

        var complete = ImmutableList.CreateBuilder<CardDefinition>();
        var incomplete = ImmutableList.CreateBuilder<CardDefinition>();

        foreach (var card in cards)
        {
            if (CardCompiler.Compile(card).IsComplete)
                complete.Add(card);
            else
                incomplete.Add(card);
        }

        return new Corpus([.. cards], complete.ToImmutable(), incomplete.ToImmutable());
    }

    /// <summary>
    /// The calibration. Everything else in this file is only worth reading if this passes.
    /// </summary>
    /// <remarks>
    /// A harness that loads a different corpus, or compiles it differently, produces numbers that
    /// cannot be compared with the coverage ratchet's - and the failure is silent, because both
    /// sets of numbers look plausible. This asserts the two agree before any of the rest is
    /// measured. It is also the guard against the corpus being absent: <c>oracle_cards.json</c>
    /// is not in git, and without it every test in this file reads zero cards and passes.
    /// </remarks>
    [Fact]
    public void This_harness_reads_the_same_corpus_the_coverage_ratchet_reads()
    {
        if (Loaded.Value is not { } corpus)
        {
            output.WriteLine("oracle_cards.json not present - skipping (nothing was measured).");
            return;
        }

        output.WriteLine($"corpus:   {corpus.All.Count} playable cards");
        output.WriteLine($"complete: {corpus.Complete.Count} fully read");

        Assert.Equal(CorpusCards, corpus.All.Count);
        // A ratchet rather than a pin, and the direction is the point: this number rises every
        // time a reader is added, so exact equality turned the suite red on every productive
        // commit and cost more in re-ratcheting than it ever caught. What it is really guarding is
        // that this harness reads the same corpus as the coverage test - a harness filtering
        // differently would be out by thousands, not by the handful a round of readers moves.
        Assert.True(
            corpus.Complete.Count >= CompleteCards,
            $"only {corpus.Complete.Count} complete cards, below the recorded {CompleteCards} - "
                + "either coverage regressed or this harness is reading a different corpus.");
    }

    /// <summary>
    /// What the deck-legality gate would actually admit, against what the compiler reads.
    /// </summary>
    /// <remarks>
    /// The gate is <c>GameTableService.Unsupported</c>, and it is called here rather than
    /// re-implemented: a second copy of the rule would be a measurement of the copy.
    /// <para>
    /// It is measured against two ability sources, because which one is wired up is most of the
    /// finding. <see cref="CardPool"/> is what <c>Program.cs</c> registers - hand-written
    /// <see cref="StarterCards"/> plus the basics. <see cref="CompiledPool"/> is the one that
    /// compiles a card's text, and it is constructed nowhere outside the test projects.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_legality_gate_and_the_compiler_do_not_answer_the_same_question()
    {
        if (Loaded.Value is not { } corpus)
        {
            output.WriteLine("oracle_cards.json not present - skipping (nothing was measured).");
            return;
        }

        var shipped = Admitted(new CardPool(), corpus.All);
        var compiled = Admitted(new CompiledPool(), corpus.All);

        var shippedAll = shipped.Count;
        var compiledAll = compiled.Count;
        var shippedComplete = corpus.Complete.Count(c => shipped.Contains(c.Name));
        var compiledComplete = corpus.Complete.Count(c => compiled.Contains(c.Name));
        var compiledHalfRead = corpus.Incomplete.Count(c => compiled.Contains(c.Name));
        var covered = corpus.All.Count(CardCoverage.IsFullyCovered);
        var compiledRefusesComplete = corpus.Complete.Count - compiledComplete;

        output.WriteLine($"corpus                                {corpus.All.Count,6}");
        output.WriteLine($"compiler reads every line of          {corpus.Complete.Count,6}");
        output.WriteLine($"CardCoverage says needs no code       {covered,6}");
        output.WriteLine(string.Empty);
        output.WriteLine($"gate admits (CardPool - as shipped)   {shippedAll,6}");
        output.WriteLine($"  ... of the fully read cards         {shippedComplete,6}");
        output.WriteLine($"gate admits (CompiledPool - in tests) {compiledAll,6}");
        output.WriteLine($"  ... of the fully read cards         {compiledComplete,6}");
        output.WriteLine($"  ... fully read and REFUSED anyway   {compiledRefusesComplete,6}");
        output.WriteLine($"  ... of the HALF-read cards          {compiledHalfRead,6}");
        output.WriteLine(string.Empty);
        output.WriteLine("fully read cards the compiled gate still refuses:");

        foreach (var card in corpus.Complete.Where(c => !compiled.Contains(c.Name)).Take(12))
            output.WriteLine($"  {card.Name}");

        // This measures the two sources on their own, which is what makes the shape of the old
        // defect visible: for most of this engine's life `Program.cs` registered CardPool as the
        // only IAbilitySource, so the gate had never heard of the compiler and admitted 917 cards
        // while every line of 15,262 read. That is now fixed - PlayableCards composes the two and
        // GameTableService asks it - and the numbers below are kept because they are what the fix
        // has to keep beating.
        Assert.Equal(ShippedGateAdmits, shippedAll);
        Assert.Equal(ShippedGateAdmitsComplete, shippedComplete);

        // Wiring the compiled pool in was necessary and not sufficient: on its own it still asks
        // the wrong question, in both directions, which is why PlayableCards.Refuses exists rather
        // than a plain "does this pool know the card".
        //
        // Under-admits: it asks whether any of five ability collections is non-empty, and a card
        // whose whole text compiles into something else - an adventure, a cost reducer, granted
        // keywords, a split card's halves, an "enters with counters" that lands elsewhere - has
        // all five empty and is refused although the compiler read every line of it.
        //
        // Over-admits, which is the dangerous half: asking "did any ability compile" admits a card
        // with one ability read and three lines unread. That is the quietly-wrong game the gate
        // exists to prevent, and CompiledPool.Refuses - written for exactly this and, until the
        // wiring landed, called from nowhere in the repository - is what PlayableCards asks
        // instead. PlayableCardsTests holds that behaviour directly.
        // Each of these is a ratchet in the direction that represents progress, so the numbers
        // move with the work instead of against it. Admitted-complete may only rise; the two
        // faults - fully read cards the old question refused, and half-read cards it admitted -
        // may only fall.
        Assert.True(
            compiledComplete >= CompiledGateAdmitsComplete,
            $"the compiled gate admits {compiledComplete} fully read cards, below the recorded "
                + $"{CompiledGateAdmitsComplete}.");

        Assert.True(
            compiledRefusesComplete <= CompiledGateRefusesComplete,
            $"{compiledRefusesComplete} fully read cards are refused, above the recorded "
                + $"{CompiledGateRefusesComplete} - the gate got stricter about cards it should "
                + "admit.");

        Assert.True(
            compiledHalfRead <= CompiledGateAdmitsHalfRead,
            $"{compiledHalfRead} half-read cards are admitted, above the recorded "
                + $"{CompiledGateAdmitsHalfRead}.");
    }

    /// <summary>How many cards the gate admits with the pool <c>Program.cs</c> registers.</summary>
    /// <remarks>
    /// Measured, not desired. 899 of these are cards <see cref="CardCoverage"/> says need no code
    /// at all; the remainder are hand-written <see cref="StarterCards"/> found by name.
    /// </remarks>
    private const int ShippedGateAdmits = 917;

    private const int ShippedGateAdmitsComplete = 916;

    private const int CompiledGateAdmitsComplete = 15_090;

    /// <summary>Fully read and refused anyway. Should be 0; see PLAYABILITY.md.</summary>
    // Re-recorded 410 -> 418 after investigation, not as drift: the eight newcomers are
    // cards whose whole text lands outside the five collections this naive question counts -
    // granted keywords, cost modifiers - so the number rises precisely when such a card becomes
    // fully read. The production gate asks PlayableCards.Refuses, which admits all of them and
    // is held by PlayableCardsTests; this row documents the naive question's gap, and the
    // ratchet stays because it forces exactly the investigation that wrote this comment.
    private const int CompiledGateRefusesComplete = 462;

    /// <summary>Half-read and admitted anyway. Should be 0; see PLAYABILITY.md.</summary>
    private const int CompiledGateAdmitsHalfRead = 6_679;

    /// <summary>
    /// How many fully read cards no soak ever selects, and what they are.
    /// </summary>
    /// <remarks>
    /// This is a claim about the reach of the tests, not about the cards: a card counted here is
    /// <em>unverified</em>, which is much weaker than <em>broken</em>. The three soaks in
    /// <c>CompiledCardSoakTests</c> pick their cards by predicate, so the set they never select
    /// is computed exactly rather than instrumented.
    /// </remarks>
    [Fact]
    public void Complete_cards_that_no_soak_ever_selects_are_counted()
    {
        if (Loaded.Value is not { } corpus)
        {
            output.WriteLine("oracle_cards.json not present - skipping (nothing was measured).");
            return;
        }

        var permanents = corpus.Complete.Where(c => SoakPermanent(c.CardTypes)).ToImmutableList();
        var spells = corpus.Complete.Where(SoakSpell).ToImmutableList();
        var neither = Unsoaked(corpus.Complete);

        var permanentCount = permanents.Count;
        var spellCount = spells.Count;
        var neitherCount = neither.Count;

        output.WriteLine($"fully read                       {corpus.Complete.Count,6}");
        output.WriteLine($"  played by the permanent soak   {permanentCount,6}");
        output.WriteLine($"  selected by the spell soak     {spellCount,6}");
        output.WriteLine($"  selected by neither            {neitherCount,6}");
        output.WriteLine(string.Empty);
        output.WriteLine("what the soaks never select, by printed type:");

        foreach (var (line, count) in neither
            .GroupBy(c => TypeWord(c.CardTypes), StringComparer.Ordinal)
            .Select(g => (g.Key, g.Count()))
            .OrderByDescending(p => p.Item2))
        {
            output.WriteLine($"  {count,6}  {line}");
        }

        // The typeless bucket is named card by card, because "a card kind has appeared that
        // nothing plays" is only actionable when it says which cards: the label alone sent one
        // investigation to battles when the four residents were something else entirely.
        foreach (var card in neither.Where(c => TypeWord(c.CardTypes).StartsWith('(')))
            output.WriteLine($"          ? {card.Name}  [{card.CardTypes}]");

        // Ratchets, same reasoning: what the soak reaches may only grow, and the residue it
        // reaches by neither route may only shrink. An exact pin here recorded a moment rather
        // than a property, and every round of readers falsified it.
        Assert.True(
            permanentCount >= SoakPlaysPermanents,
            $"the permanent soak selects {permanentCount}, below the recorded "
                + $"{SoakPlaysPermanents}.");

        Assert.True(
            spellCount >= SoakCastsSpells,
            $"the spell soak selects {spellCount}, below the recorded {SoakCastsSpells}.");

        Assert.True(
            neitherCount <= SoakSelectsNeither,
            $"{neitherCount} complete cards are selected by no soak, above the recorded "
                + $"{SoakSelectsNeither} - a card kind has appeared that nothing plays.");

        // The three sets partition the fully read cards: a card is a soak permanent, a soak
        // spell, or unselected. If that stops holding the predicates have drifted from the soak.
        Assert.Equal(corpus.Complete.Count, permanentCount + spellCount + neitherCount);
    }

    /// <summary>Fully read permanents the permanent soak puts on a battlefield.</summary>
    private const int SoakPlaysPermanents = 11_680;

    /// <summary>
    /// Fully read instants and sorceries the spell soak <em>selects</em>.
    /// </summary>
    /// <remarks>
    /// Selecting is not resolving. The soak's own output says how many of these are actually
    /// cast, and it is under half - the rest are refused for want of a legal target or a legal
    /// moment. Attempted-and-refused is weaker than played and stronger than untouched;
    /// PLAYABILITY.md keeps the three apart rather than adding them up.
    /// </remarks>
    private const int SoakCastsSpells = 3_039;

    /// <summary>
    /// Fully read cards no soak selects at all - the ones nothing has ever played.
    /// </summary>
    /// <remarks>
    /// Every one of them is a land, and the cause is one predicate:
    /// <c>CompiledCardSoakTests.IsPermanent</c> names creature, artifact, enchantment and
    /// planeswalker, and a land is none of those. They are not instants or sorceries either, so
    /// the spell soak does not see them. See <see cref="SoakPermanent"/>.
    /// </remarks>
    // 782 and every one of them a Land, checked rather than assumed: the typeless bucket
    // this number exists to police is empty, and it rose because one more land became
    // fully read. The two times it caught something real - 48 sticker sheets that are not
    // cards, and four reversible printings arriving with no type at all - both showed up
    // in that bucket, which is why the failure message names its residents card by card.
    private const int SoakSelectsNeither = 805;

    /// <summary>
    /// The cards no soak selects, put into real games to find out what they do.
    /// </summary>
    /// <remarks>
    /// The measurement above says these are unverified. This is what turns some of that into a
    /// verified claim: the same three assertions the soak makes - nothing threw, the layers can
    /// still compute characteristics, and <c>Replay(log)</c> equals the state - against the cards
    /// the soak's own predicate excludes.
    /// <para>
    /// Run twice, because for a land the two are different code. The soak <em>creates</em> its
    /// permanents directly onto the battlefield; a land in a real game is <em>played</em> from
    /// hand (CR 305.1), which is a special action rather than a spell. That distinction has
    /// already produced one whole-class bug here - every "enters tapped" land arrived untapped,
    /// because the replacement was pinned to the zone a spell is in - so the second pass exists
    /// specifically to run the path the first one skips.
    /// </para>
    /// <para>
    /// A fault is attributed by re-running each card of the broken table alone, so a finding
    /// names a card rather than twelve. A card that only faults in company is reported
    /// separately: that is an interaction, not a card.
    /// </para>
    /// <para>
    /// About six seconds once the corpus is loaded, which is the whole file's cost.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_complete_cards_no_soak_plays_survive_being_played()
    {
        if (Loaded.Value is not { } corpus)
        {
            output.WriteLine("oracle_cards.json not present - skipping (nothing was measured).");
            return;
        }

        // Only the ones that can be put onto a battlefield at all. Anything else in the
        // unselected set is counted and named by the test above rather than played.
        var table = Unsoaked(corpus.Complete)
            .Where(c => c.CardTypes.HasFlag(CardType.Land) || c.CardTypes.HasFlag(CardType.Battle))
            .OrderBy(Scatter)
            .ThenBy(c => c.OracleId, StringComparer.Ordinal)
            .ToImmutableList();

        Assert.True(table.Count > 500, $"only {table.Count} unsoaked permanents - wrong set.");

        var pool = new CompiledPool();
        var broken = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var interactions = 0;
        var played = 0;
        var drops = 0;

        foreach (var (fromHand, size) in new[] { (false, PerGame), (true, PerHandGame) })
        {
            for (var start = 0; start < table.Count; start += size)
            {
                var batch = table.Skip(start).Take(size).ToImmutableList();

                try
                {
                    drops += Play(batch, pool, fromHand);
                    played += batch.Count;
                }
                catch (Exception broke)
                {
                    // Which of the twelve was it? Re-run each alone. A fault naming a whole table
                    // names nothing anybody can act on.
                    var alone = 0;

                    foreach (var card in batch)
                    {
                        try
                        {
                            Play([card], pool, fromHand);
                        }
                        catch (Exception itself)
                        {
                            alone++;
                            Record(broken, Cause(itself, How(fromHand)), card.Name);
                        }
                    }

                    if (alone == 0)
                    {
                        // Every card is fine on its own: what broke is the combination.
                        interactions++;
                        Record(
                            broken,
                            Cause(broke, How(fromHand)),
                            "(interaction) " + string.Join(", ", batch.Select(c => c.Name)));
                    }
                }
            }
        }

        var faulted = broken.Sum(p => p.Value.Count);

        output.WriteLine(
            $"played {played} card-games over {table.Count} cards the soaks never select "
                + $"({table.Count / PerGame} tables created on the battlefield, "
                + $"{table.Count / PerHandGame} played from hand)");
        output.WriteLine($"land drops actually taken:         {drops}");
        output.WriteLine($"tables that broke only in company: {interactions}");
        output.WriteLine($"cards that faulted:                {faulted}");

        foreach (var (why, n) in Refusals.OrderByDescending(p => p.Value).Take(8))
            output.WriteLine($"  refused {n,5}  {why}");


        foreach (var (cause, cards) in broken.OrderByDescending(p => p.Value.Count))
        {
            output.WriteLine($"  {cards.Count,5}  {cause}");
            foreach (var name in cards.Take(6))
                output.WriteLine($"           {name}");
        }

        // A floor on the reach, not only on the outcome - the discipline the soak had to learn
        // twice. A harness that stops playing lands from hand passes exactly like one that plays
        // them all, and the second pass is the only thing in the repository that runs PlayLand
        // against a corpus card at all.
        Assert.True(
            drops > 600,
            $"only {drops} lands were played from hand - the second pass has stopped reaching "
                + "them, whatever the rest of this test says.");

        // The measured state, not the desired one. Zero is both here.
        Assert.Equal(UnsoakedCardsThatFault, faulted);
    }

    /// <summary>How many of the never-soaked cards throw in a real game. Measured: none.</summary>
    private const int UnsoakedCardsThatFault = 0;

    /// <summary>
    /// Every fully read permanent, <em>cast</em> rather than conjured onto the battlefield.
    /// </summary>
    /// <remarks>
    /// The permanent soak calls <c>Game.Create(..., Zone.Battlefield)</c>. That is not how a
    /// permanent reaches a battlefield in a game: it is cast, which is a different body of code
    /// entirely - the cost is paid (CR 601.2f-h), the spell sits on the stack where anything
    /// watching "whenever you cast" can see it, and it enters the battlefield <em>from the
    /// stack</em> rather than from nowhere.
    /// <para>
    /// That distinction has already produced a whole-class bug in this engine: every "enters
    /// tapped" land arrived untapped because the replacement was pinned to the zone a spell is
    /// in. Conjuring skips the zone the bug lived in. So <b>none of the 11,334 fully read
    /// permanents had ever been cast</b>, and this casts them.
    /// </para>
    /// <para>
    /// A refusal is not a failure - a permanent whose cost cannot be paid, or which wants a
    /// target this board has not got, is the rules working. What this looks for is the other
    /// kind. The count that <em>is</em> asserted is the reach, because a harness that stops
    /// casting passes exactly like one that casts everything.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("Category", "Slow")]
    public void Complete_permanents_survive_being_cast_rather_than_conjured()
    {
        if (Loaded.Value is not { } corpus)
        {
            output.WriteLine("oracle_cards.json not present - skipping (nothing was measured).");
            return;
        }

        var permanents = corpus.Complete
            .Where(c => SoakPermanent(c.CardTypes))
            .OrderBy(Scatter)
            .ThenBy(c => c.OracleId, StringComparer.Ordinal)
            .ToImmutableList();

        Assert.True(permanents.Count > 5_000, $"only {permanents.Count} permanents - wrong set.");

        var pool = new CompiledPool();
        var broken = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var interactions = 0;
        var cast = 0;

        for (var start = 0; start < permanents.Count; start += PerGame)
        {
            var hand = permanents.Skip(start).Take(PerGame).ToImmutableList();

            try
            {
                cast += CastFromHand(hand, pool);
            }
            catch (Exception broke)
            {
                var alone = 0;

                foreach (var card in hand)
                {
                    try
                    {
                        CastFromHand([card], pool);
                    }
                    catch (Exception itself)
                    {
                        alone++;
                        Record(broken, Cause(itself, "cast from hand"), card.Name);
                    }
                }

                if (alone == 0)
                {
                    interactions++;
                    Record(
                        broken,
                        Cause(broke, "cast from hand"),
                        "(interaction) " + string.Join(", ", hand.Select(c => c.Name)));
                }
            }
        }

        var faulted = broken.Sum(p => p.Value.Count);

        output.WriteLine($"offered  {permanents.Count} fully read permanents to be cast");
        output.WriteLine($"cast     {cast}");
        output.WriteLine($"faulted  {faulted}  (hands that broke only in company: {interactions})");
        output.WriteLine(string.Empty);

        foreach (var (why, n) in CastRefusals.OrderByDescending(p => p.Value).Take(10))
            output.WriteLine($"  refused {n,6}  {why}");

        output.WriteLine(string.Empty);

        foreach (var (cause, cards) in broken.OrderByDescending(p => p.Value.Count))
        {
            output.WriteLine($"  {cards.Count,5}  {cause}");
            foreach (var name in cards.Take(8))
                output.WriteLine($"           {name}");
        }

        // The reach, asserted. Everything else here is a print-out, and a harness whose casts
        // all start being refused would go quietly green while checking nothing.
        Assert.True(
            cast > PermanentsCastFloor,
            $"only {cast} of {permanents.Count} permanents could be cast at all - the harness "
                + "has stopped reaching them, whatever the rest of this test says.");

        // The measured state, not the desired one. See PLAYABILITY.md.
        Assert.Equal(PermanentsThatFaultWhenCast, faulted);
    }

    /// <summary>A floor under the reach, set below the measured figure so noise cannot trip it.</summary>
    private const int PermanentsCastFloor = 9_000;

    /// <summary>How many fully read permanents throw when cast. Should be 0.</summary>
    private const int PermanentsThatFaultWhenCast = 0;

    /// <summary>Why a cast was refused, so a harness that reaches nothing has to say so.</summary>
    private static readonly Dictionary<string, int> CastRefusals = new(StringComparer.Ordinal);

    /// <summary>
    /// Puts each of these permanents in a hand, funds it, and casts it.
    /// </summary>
    /// <remarks>
    /// Everything is cast in one main phase rather than one per turn. Resolving a spell returns
    /// priority to the active player in the same step, so emptying the stack after each cast
    /// leaves the window open - which keeps the games short, and keeps the hand from growing past
    /// the maximum and being discarded, the mistake the land pass above made first.
    /// </remarks>
    private static int CastFromHand(ImmutableList<CardDefinition> hand, CompiledPool pool)
    {
        var alice = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
        var bob = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");

        var game = Game.Start(
            Guid.NewGuid(),
            [
                new PlayerSetup(alice, "Alice", 400, Filler("Alice")),
                new PlayerSetup(bob, "Bob", 400, Filler("Bob")),
            ],
            new GameRandom(23),
            startingPlayerId: alice,
            abilities: pool);

        game.BeginPlay(openingHandSize: 0, withMulligans: false);

        // A board with one of each permanent type on both sides, so an Aura has something to
        // enchant and "target artifact an opponent controls" is not refused for the arrangement
        // rather than for the rule. The spell soak learnt this the expensive way: nine of its ten
        // commonest refusals were "illegal target" against a field of vanilla bears.
        foreach (var owner in new[] { alice, bob })
        {
            game.Create(owner, Filler(owner.ToString())[0], Zone.Battlefield);

            foreach (var (what, type) in new[]
            {
                ("Relic", CardType.Artifact),
                ("Charm", CardType.Enchantment),
                ("Grove", CardType.Land),
            })
            {
                game.Create(
                    owner,
                    new CardDefinition
                    {
                        OracleId = $"playability-{what}-{owner}",
                        Name = $"Playability {what}",
                        CardTypes = type,
                    },
                    Zone.Battlefield);
            }
        }

        Settle(game);

        var cast = 0;

        foreach (var card in hand)
        {
            if (!Advance(game, alice))
                break;

            var inHand = game.Create(alice, card, Zone.Hand);

            // Enough of every colour, and of nothing, to pay for anything printed. This is a
            // harness reaching code, not a game being won.
            game.AddMana(alice, null, 20);
            foreach (var colour in Enum.GetValues<ManaColor>())
                game.AddMana(alice, colour, 20);

            if (Speak(game, alice, inHand, pool))
                cast++;

            Empty(game);
        }

        foreach (var id in game.State.Battlefield)
            Characteristics.Of(game.State, pool, game.State.GetObject(id));

        Assert.Equal(game.State, GameReducer.Replay(game.Log));
        return cast;
    }

    /// <summary>Runs the game on until this player could cast a sorcery, or gives up.</summary>
    private static bool Advance(Game game, Guid player)
    {
        for (var guard = 0; guard < 2_000; guard++)
        {
            if (game.State.IsOver)
                return false;

            if (game.State.Choice is { } choice)
            {
                Decide(game, choice);
                continue;
            }

            if (game.State.Priority.Holder == player && game.State.IsSorcerySpeedFor(player))
                return true;

            if (game.State.Priority.Holder is { } holder)
            {
                game.PassPriority(holder);
                continue;
            }

            return false;
        }

        return false;
    }

    /// <summary>Lets the stack resolve, answering whatever it asks on the way down.</summary>
    private static void Empty(Game game)
    {
        for (var guard = 0; guard < 600 && !game.State.Stack.IsEmpty; guard++)
        {
            if (game.State.IsOver)
                return;

            if (game.State.Choice is { } choice)
            {
                Decide(game, choice);
                continue;
            }

            if (game.State.Priority.Holder is { } holder)
            {
                game.PassPriority(holder);
                continue;
            }

            break;
        }

        Settle(game);
    }

    /// <summary>
    /// A legal division for a spell that divides something among its targets (CR 601.2d).
    /// </summary>
    /// <remarks>
    /// The soak plays every complete card, so it has to answer every question a player would be
    /// asked, and a divided spell asks one at announcement. Without this the engine throws where
    /// a real cast would have offered a choice — which is what it did the first time a divided
    /// spell reached here, and it read as a faulting card rather than a harness that had not
    /// learned the question. The engine is right to throw: CR 601.2d makes an announcement that
    /// leaves a target with nothing illegal, not legal-and-inert.
    /// <para>
    /// One each and the remainder on the first, which is the simplest legal answer. Null when the
    /// spell divides nothing, or when the total is counted off the board rather than printed —
    /// the engine refuses to guess at those, and so does this.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<int>? Shares(
        Game game, CompiledPool pool, ObjectId card, IReadOnlyList<Target> aimed)
    {
        var definition = pool.SpellOf(game.State.GetObject(card).Card);

        if (definition?.Effects.OfType<IDividedEffect>().FirstOrDefault() is not { } dividing)
            return null;

        if (dividing.Total.IsVariable || dividing.Total.Fixed <= 0)
            return null;

        var covered = Math.Min(dividing.TargetCount, aimed.Count - dividing.FirstIndex);
        if (covered <= 0 || dividing.Total.Fixed < covered)
            return null;

        // One entry per chosen target, because that is what the announcement is checked against
        // - zero for the slots the division does not cover, one each for the ones it does, and
        // the remainder on the first of them.
        var shares = new int[aimed.Count];
        for (var i = 0; i < covered; i++)
            shares[dividing.FirstIndex + i] = 1;

        shares[dividing.FirstIndex] += dividing.Total.Fixed - covered;

        return shares;
    }

    /// <summary>Casts this card, trying the target shapes in turn until one is accepted.</summary>
    private static bool Speak(Game game, Guid player, ObjectId card, CompiledPool pool)
    {
        foreach (var targets in Aims(game, player, card))
        {
            var dividing = pool.SpellOf(game.State.GetObject(card).Card)
                ?.Effects.OfType<IDividedEffect>().FirstOrDefault();

            var shares = Shares(game, pool, card, targets);

            // A divided spell with no legal division is not a cast this harness can make: X is
            // announced as zero here, so "every target gets at least one" (CR 601.2d) cannot be
            // satisfied at all. Skipping the shape is the honest answer - attempting it would
            // make the engine throw for being right.
            if (dividing is not null && shares is null)
                continue;

            try
            {
                game.CastSpell(player, card, targets, damageDivision: shares);
                return true;
            }
            catch (InvalidOperationException refused)
                when (!refused.Message.Contains("did not settle", StringComparison.Ordinal))
            {
                var why = Fold(refused.Message);
                CastRefusals[why] = CastRefusals.GetValueOrDefault(why) + 1;
            }
        }

        return false;
    }

    /// <summary>The target shapes a permanent spell might want, cheapest first.</summary>
    private static IEnumerable<IReadOnlyList<Target>> Aims(Game game, Guid player, ObjectId source)
    {
        yield return [];

        ObjectId Find(bool ours, CardType type) => game.State.Battlefield.FirstOrDefault(id =>
            id != source
            && (game.State.GetObject(id).ControllerId == player) == ours
            && game.State.GetObject(id).Card.CardTypes.HasFlag(type));

        foreach (var type in new[]
        {
            CardType.Creature, CardType.Artifact, CardType.Enchantment, CardType.Land,
        })
        {
            foreach (var ours in new[] { false, true })
            {
                if (Find(ours, type) is var found && found != default)
                    yield return [Target.ToPermanent(found)];
            }
        }

        yield return [Target.ToPlayer(player)];

        var opponent = game.State.TurnOrder.FirstOrDefault(id => id != player);
        if (opponent != default)
            yield return [Target.ToPlayer(opponent)];
    }

    private static string How(bool fromHand) =>
        fromHand ? "played from hand" : "created on the battlefield";

    private static void Record(Dictionary<string, List<string>> broken, string cause, string what)
    {
        if (!broken.TryGetValue(cause, out var names))
            broken[cause] = names = [];

        names.Add(what);
    }

    // ---- the gate ------------------------------------------------------------------------

    /// <summary>
    /// Which of these cards the real deck-legality gate would let into a game.
    /// </summary>
    /// <remarks>
    /// The service is constructed with nulls for everything <c>Unsupported</c> does not touch,
    /// which is how <c>GameInviteRulesTests</c> already builds it. The point is to call the
    /// shipped gate rather than a copy of its rule.
    /// </remarks>
    private static HashSet<string> Admitted(
        IAbilitySource abilities, ImmutableList<CardDefinition> cards)
    {
        var gate = new GameTableService(null!, null!, abilities, null!);
        var refused = new HashSet<string>(gate.Unsupported(cards), StringComparer.Ordinal);

        return [.. cards.Select(c => c.Name).Where(name => !refused.Contains(name))];
    }

    // ---- the soaks' own predicates, restated ---------------------------------------------

    /// <summary>
    /// <c>CompiledCardSoakTests.IsPermanent</c>, which is what the permanent soak selects on.
    /// </summary>
    /// <remarks>
    /// Copied deliberately rather than shared: that file belongs to another piece of work, and a
    /// copy that drifts is caught by the partition assertion above rather than by trust. Note
    /// what it does not name - <see cref="CardType.Land"/> and <see cref="CardType.Battle"/>.
    /// </remarks>
    private static bool SoakPermanent(CardType types) =>
        (types & (CardType.Creature | CardType.Artifact | CardType.Enchantment
            | CardType.Planeswalker)) != 0
        && !types.HasFlag(CardType.Token);

    private static bool SoakSpell(CardDefinition card) =>
        !SoakPermanent(card.CardTypes)
        && (card.CardTypes.HasFlag(CardType.Instant) || card.CardTypes.HasFlag(CardType.Sorcery));

    private static ImmutableList<CardDefinition> Unsoaked(ImmutableList<CardDefinition> complete) =>
        [.. complete.Where(c => !SoakPermanent(c.CardTypes) && !SoakSpell(c))];

    private static string TypeWord(CardType types)
    {
        var words = new List<string>(4);

        foreach (var (flag, word) in new[]
        {
            (CardType.Land, "Land"),
            (CardType.Battle, "Battle"),
            (CardType.Artifact, "Artifact"),
            (CardType.Enchantment, "Enchantment"),
            (CardType.Creature, "Creature"),
            (CardType.Instant, "Instant"),
            (CardType.Sorcery, "Sorcery"),
            (CardType.Planeswalker, "Planeswalker"),
            (CardType.Tribal, "Tribal"),
            (CardType.Token, "Token"),
            (CardType.Other, "Other"),
        })
        {
            if (types.HasFlag(flag) && flag != CardType.None)
                words.Add(word);
        }

        return words.Count == 0
            ? "(no type this corpus loader recognises - battles and the like)"
            : string.Join(" ", words);
    }

    // ---- a game ---------------------------------------------------------------------------

    /// <summary>
    /// Puts these cards into a game and checks the three invariants. Returns the land drops made.
    /// </summary>
    /// <remarks>
    /// Four turns when the cards are created on the battlefield, which is the soak's own setup
    /// applied to the set it excludes. Longer when they are played from hand, because a land drop
    /// is one per player per turn (CR 505.6b) and twelve of them need twelve turns to reach.
    /// <para>
    /// The three assertions are the soak's, deliberately: a card that throws, a card the layers
    /// cannot answer for, and a log that no longer folds to the state are the three failures that
    /// mean the same thing whatever the card says.
    /// </para>
    /// </remarks>
    private static int Play(ImmutableList<CardDefinition> table, CompiledPool pool, bool fromHand)
    {
        var alice = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
        var bob = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");

        var game = Game.Start(
            Guid.NewGuid(),
            [
                new PlayerSetup(alice, "Alice", 400, Filler("Alice")),
                new PlayerSetup(bob, "Bob", 400, Filler("Bob")),
            ],
            new GameRandom(11),
            startingPlayerId: alice,
            abilities: pool);

        // No opening hand when the cards are to be played from it. Seven filler cards plus the
        // lands puts the hand over the maximum, the cleanup step asks for a discard (CR 514.1),
        // and this harness answers by discarding the very cards it came to play.
        game.BeginPlay(openingHandSize: fromHand ? 0 : 7, withMulligans: false);

        // Split across both players, so "you control" and "an opponent controls" each find
        // something. A board one player owns answers half the questions a card can ask.
        var waiting = new Dictionary<Guid, Queue<ObjectId>>
        {
            [alice] = new(),
            [bob] = new(),
        };

        for (var i = 0; i < table.Count; i++)
        {
            var owner = i % 2 == 0 ? alice : bob;

            if (fromHand)
                waiting[owner].Enqueue(game.Create(owner, table[i], Zone.Hand));
            else
                game.Create(owner, table[i], Zone.Battlefield);
        }

        Settle(game);

        var drops = 0;
        var until = game.State.TurnNumber + (fromHand ? HandTurns : 4);

        for (var guard = 0; guard < 6_000 && game.State.TurnNumber < until; guard++)
        {
            if (game.State.IsOver)
                break;

            if (game.State.Choice is { } choice)
            {
                Decide(game, choice);
                continue;
            }

            if (fromHand && Drop(game, waiting, ref drops))
                continue;

            if (Fight(game))
                continue;

            if (game.State.Priority.Holder is { } holder)
            {
                game.PassPriority(holder);
                continue;
            }

            break;
        }

        // The layers have to be able to answer for everything still standing.
        foreach (var id in game.State.Battlefield)
            Characteristics.Of(game.State, pool, game.State.GetObject(id));

        // And the invariant the engine rests on, against real text.
        Assert.Equal(game.State, GameReducer.Replay(game.Log));

        return drops;
    }

    /// <summary>
    /// Plays the active player's next waiting land, if the rules allow one right now.
    /// </summary>
    /// <remarks>
    /// A refusal takes the card off the queue rather than retrying: this is a harness that wants
    /// to reach the code, not a player trying to win, and a land it cannot place would otherwise
    /// spin the loop until the guard ran out. "Did not settle" is excluded from the refusals,
    /// because that one is the engine failing rather than the rules saying no.
    /// </remarks>
    private static bool Drop(Game game, Dictionary<Guid, Queue<ObjectId>> waiting, ref int drops)
    {
        var active = game.State.ActivePlayerId;

        if (!waiting.TryGetValue(active, out var queue) || queue.Count == 0)
            return false;

        if (game.State.Priority.Holder != active || !game.State.IsSorcerySpeedFor(active))
            return false;

        if (game.State.GetPlayer(active).LandsPlayedThisTurn > 0)
            return false;

        var card = queue.Dequeue();

        try
        {
            game.PlayLand(active, card);
            drops++;
        }
        catch (InvalidOperationException refused)
            when (!refused.Message.Contains("did not settle", StringComparison.Ordinal))
        {
            // The rules said no - a land that may only be played under a condition, say.
            Refusals[refused.Message] = Refusals.GetValueOrDefault(refused.Message) + 1;
        }

        return true;
    }

    /// <summary>Why a land drop was refused, so a harness that reaches nothing says so.</summary>
    private static readonly Dictionary<string, int> Refusals = new(StringComparer.Ordinal);

    /// <summary>
    /// Attacks with everything and blocks one-for-one, so combat is reached at all.
    /// </summary>
    /// <remarks>
    /// One blocker per attacker on purpose. Ganging up is what makes combat damage a division
    /// (CR 510.1c), the soak already covers that, and a harness that cannot compute lethal and
    /// guesses at the split reports the rules working as a failure - which cost that soak two
    /// runs. This set is lands, so what combat buys here is the ones that animate.
    /// </remarks>
    private static bool Fight(Game game)
    {
        var active = game.State.ActivePlayerId;

        if (game.State.CurrentStep == TurnStep.DeclareAttackers
            && !game.State.Combat.AttackersDeclared)
        {
            var defender = game.State.TurnOrder.FirstOrDefault(id => id != active);

            var able = game.State.Battlefield
                .Where(id => game.State.GetObject(id) is
                {
                    Permanent: { IsTapped: false, HasSummoningSickness: false },
                } o && o.ControllerId == active
                    && o.Card.CardTypes.HasFlag(CardType.Creature))
                .ToList();

            foreach (var batch in new[] { able, able.Take(1).ToList(), [] })
            {
                try
                {
                    game.DeclareAttackers(
                        active,
                        batch.ToDictionary(id => id, _ => AttackTarget.Player(defender)));

                    return true;
                }
                catch (InvalidOperationException refused)
                    when (!refused.Message.Contains("did not settle", StringComparison.Ordinal))
                {
                    // Something in that batch may not attack. Try a smaller one.
                }
            }

            return false;
        }

        if (game.State.CurrentStep == TurnStep.DeclareBlockers
            && game.State.Combat.AttackersDeclared
            && !game.State.Combat.BlockersDeclared)
        {
            var defending = game.State.TurnOrder.FirstOrDefault(id => id != active);
            var attackers = game.State.Combat.Attackers.Keys.ToList();

            var able = game.State.Battlefield
                .Where(id => game.State.GetObject(id) is { Permanent.IsTapped: false } o
                    && o.ControllerId == defending
                    && o.Card.CardTypes.HasFlag(CardType.Creature))
                .ToList();

            var blocks = new Dictionary<ObjectId, IReadOnlyList<ObjectId>>();
            for (var i = 0; i < able.Count && i < attackers.Count; i++)
                blocks[attackers[i]] = [able[i]];

            foreach (var batch in new[] { blocks, [] })
            {
                try
                {
                    game.DeclareBlockers(defending, batch);
                    return true;
                }
                catch (InvalidOperationException refused)
                    when (!refused.Message.Contains("did not settle", StringComparison.Ordinal))
                {
                    // Not a legal block - menace, an evasion keyword, a restriction.
                }
            }

            return false;
        }

        return false;
    }

    private static void Settle(Game game)
    {
        for (var guard = 0; guard < 200 && game.State.Choice is { } choice; guard++)
            Decide(game, choice);
    }

    /// <summary>Answers one question, trying the shapes an answer can take until one is legal.</summary>
    private static void Decide(Game game, PendingChoice choice)
    {
        foreach (var answer in Answers(choice))
        {
            try
            {
                game.Choose(choice.PlayerId, answer);
                return;
            }
            catch (InvalidOperationException refused)
                when (!refused.Message.Contains("did not settle", StringComparison.Ordinal))
            {
                // Not that shape. Try the next.
            }
        }

        // Nothing was accepted: let the last attempt throw for real, so a question this harness
        // genuinely cannot answer is a fault rather than a silent skip.
        game.Choose(choice.PlayerId, Answer(choice));
    }

    private static IEnumerable<IReadOnlyList<string>> Answers(PendingChoice choice)
    {
        // A division first, because it is the one question whose legal answers are not a subset
        // of "some of the options": CR 601.2d makes every chosen target need at least one, so an
        // answer that names only the first two options is illegal however it is split, and the
        // enumeration below can never reach a legal one for three targets or more.
        if (choice.IsDivision && choice.Options.Count > 0)
        {
            var each = new List<string>(Math.Max(choice.MinPicks, choice.Options.Count));
            foreach (var option in choice.Options)
                each.Add(option.Id);

            while (each.Count < choice.MinPicks)
                each.Add(choice.Options[0].Id);

            yield return each;
        }

        yield return Answer(choice);

        if (choice.Options.Count == 0)
            yield break;

        var wanted = Math.Max(choice.MinPicks, 1);

        // Every split of the picks between the first option and the rest, front-loaded. Enough
        // for anything with two answers; the soak carries the general division for the rest.
        for (var first = wanted; first >= 0; first--)
        {
            var picks = new List<string>(wanted);

            for (var i = 0; i < wanted; i++)
            {
                picks.Add(i < first
                    ? choice.Options[0].Id
                    : choice.Options[Math.Min(1, choice.Options.Count - 1)].Id);
            }

            yield return picks;
        }
    }

    private static IReadOnlyList<string> Answer(PendingChoice choice)
    {
        if (choice.Options.Count == 0)
            return [];

        var wanted = Math.Max(choice.MinPicks, 1);
        var picks = new List<string>(wanted);

        for (var i = 0; i < wanted; i++)
            picks.Add(choice.Options[i % choice.Options.Count].Id);

        return picks;
    }

    private static ImmutableList<CardDefinition> Filler(string who) =>
        [.. Enumerable.Range(0, 40).Select(i => new CardDefinition
        {
            OracleId = $"playability-{who}-{i}",
            Name = $"Playability Bear {who} {i}",
            CardTypes = CardType.Creature,
            Power = 2,
            Toughness = 2,
        })];

    /// <summary>
    /// What broke, with the card-specific parts taken out so that faults group.
    /// </summary>
    /// <remarks>
    /// One line per card would be a list, not a finding. Numbers become <c>#</c> and anything
    /// quoted becomes <c>~</c>, which folds "Thing 'Foo' has 3 of them" and "Thing 'Bar' has 7 of
    /// them" into one row. Truncated, because a stack-shaped message buries the list it heads.
    /// </remarks>
    private static string Cause(Exception broke, string how) =>
        $"[{how}] {broke.GetType().Name}: {Fold(broke.Message)}";

    private static string Fold(string message)
    {
        var text = new StringBuilder(160);
        var quoted = false;
        var digits = false;

        foreach (var c in message)
        {
            if (c is '\'' or '"')
            {
                if (!quoted)
                    text.Append('~');

                quoted = !quoted;
                continue;
            }

            if (quoted)
                continue;

            if (char.IsAsciiDigit(c))
            {
                if (!digits)
                    text.Append('#');

                digits = true;
                continue;
            }

            digits = false;
            text.Append(c is '\r' or '\n' ? ' ' : c);

            if (text.Length >= 150)
                break;
        }

        return text.ToString();
    }

    /// <summary>A stable scramble of an oracle id, so batches vary but reproduce (FNV-1a).</summary>
    /// <remarks>
    /// Not <c>GetHashCode</c>, which is randomised per process: a harness that groups its cards
    /// differently on every run reports a different set of interactions each time, and a failure
    /// nobody can reproduce is barely a failure.
    /// </remarks>
    private static uint Scatter(CardDefinition card)
    {
        unchecked
        {
            var hash = 2166136261u;

            foreach (var c in card.OracleId)
            {
                hash ^= c;
                hash *= 16777619u;
            }

            return hash;
        }
    }
}
