using System.Collections.Immutable;
using System.Globalization;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Mana;
using MtgEngine.Rules.State;
using Xunit.Abstractions;

namespace MtgEngine.Api.Tests;

/// <summary>
/// Real cards, put onto a real battlefield, in real games.
/// </summary>
/// <remarks>
/// Every other test in this repository plays either a hand-written fixture or a vanilla bear.
/// **No card out of the corpus had ever been played through a game at all** - they were compiled,
/// inspected, counted, and never put on a battlefield to see what happened next.
/// <para>
/// That is the gap the static instruments cannot close by construction. A condition written
/// inside a trigger's predicate is a closure: nothing can read it, and the only way to find out
/// what it does is to run it. So this runs thousands of them.
/// </para>
/// <para>
/// It asserts the things that must hold for <em>every</em> card, whatever it says: nothing
/// throws, the layers can compute characteristics for every object in every zone, the board's
/// view projects for every player, and <c>Replay(log)</c> still equals the state - sampled
/// through the serializer as well, which is the door persistence actually uses. That last pair
/// is the invariant the whole engine rests on, checked here against real text rather than against
/// fixtures chosen to be easy. They live in one place, <see cref="Check"/>, because four copies
/// had already drifted into asking less than they claimed.
/// </para>
/// <para>
/// <strong>Playing a card is not the same as reaching its behaviour.</strong> Putting a permanent
/// down runs its statics and offers its replacements; pressing its button runs an activated
/// ability; casting it runs a spell. None of those makes a <em>trigger</em> fire, and a game of
/// pure priority-passing is a game in which almost nothing happens - so every "whenever a
/// creature dies", "whenever you draw", "whenever you gain life" and landfall in the corpus was
/// compiled, played, and never once triggered. <see cref="Provoke"/> makes those things happen,
/// and the count is asserted for the same reason the attack count is.
/// </para>
/// </remarks>
public sealed class CompiledCardSoakTests(ITestOutputHelper output)
{
    /// <summary>How many permanents share a battlefield. Enough to interact, few enough to read.</summary>
    private const int PerGame = 12;

    /// <summary>How many turns each game runs before it is called settled.</summary>
    private const int Turns = 10;

    /// <summary>
    /// Whether the expensive per-game checks run on every game rather than on a sample.
    /// </summary>
    /// <remarks>
    /// The soaks already cost about twenty-five minutes. The persistence fold below roughly
    /// doubles the price of a game, which would push that past the point where anybody runs it,
    /// so by default it is taken on one game in <see cref="StorageSample"/> - which is still
    /// hundreds of games per run, and enough that a systematically unwritable event shows up.
    /// Set <c>MTG_SOAK_DEEP</c> to pay for all of them; the same environment-gate convention the
    /// dump tests use.
    /// </remarks>
    private static readonly bool Deep =
        Environment.GetEnvironmentVariable("MTG_SOAK_DEEP") is { Length: > 0 };

    /// <summary>One game in this many is folded back through the persistence door as well.</summary>
    private const int StorageSample = 16;

    /// <summary>
    /// Turns the provocations off, so the trigger count can be measured against a run without them.
    /// </summary>
    /// <remarks>
    /// The floor asserted below is only meaningful next to the number the same games produce with
    /// nothing provoked. Set <c>MTG_SOAK_QUIET</c> to take that measurement; it is not something
    /// to run in a gate, because a soak with its provocations switched off is the soak this file
    /// is trying to stop being.
    /// </remarks>
    private static readonly bool SkipProvocation =
        Environment.GetEnvironmentVariable("MTG_SOAK_QUIET") is { Length: > 0 };

    /// <summary>
    /// How many turns of a game are provoked before it is left alone.
    /// </summary>
    /// <remarks>
    /// A trigger that has fired has fired, and the yield falls off a cliff after the first few
    /// rounds: measured on the four-turn slice, provoking took the firings from 1,733 to 2,545 and
    /// the distinct cards from 1,255 to 1,408, and the rounds after the first few are almost all
    /// the same cards going off again. Uncapped it cost the deep pass 44% of its runtime for that.
    /// <para>
    /// The cap is also what keeps the deep slice deep. Its last five turns exist so a Saga can
    /// finish and a fading permanent run out, and a battery firing into them every turn buys
    /// nothing those mechanics need.
    /// </para>
    /// </remarks>
    private const int ProvokeTurns = 5;

    /// <summary>
    /// What share of the corpus each of these tests actually reaches, counted rather than assumed.
    /// </summary>
    /// <remarks>
    /// The soaks below each print how many cards they played, and every one of those numbers is a
    /// numerator with no denominator beside it. "Played 11,334 permanents" says nothing about
    /// whether that is most of the corpus or a third of it, and it cannot say what the *rest* is
    /// or why nothing touched it.
    /// <para>
    /// That gap hid a whole card type. <see cref="IsPermanent"/> does not count lands, the spell
    /// soak takes only instants and sorceries, and nothing else selected anything - so **every
    /// fully-compiled land in the corpus was in none of these tests**, including every mana
    /// ability the compiler builds. Nothing said so, because nothing was counting the residue.
    /// </para>
    /// <para>
    /// Calibrated against <see cref="CardCompilerCoverageTests"/> on purpose: it counts complete
    /// cards the same way, off the same loader, so the two figures can be read next to each other.
    /// An instrument that disagrees with the one already trusted is wrong before it has measured
    /// anything.
    /// </para>
    /// </remarks>
    [Fact]
    public void What_share_of_the_corpus_these_tests_reach_is_counted()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present - skipping.");
            return;
        }

        var complete = 0;
        var battlefield = 0;
        var permanentsOnly = 0;
        var lands = 0;
        var spells = 0;
        var untouched = new Dictionary<string, int>(StringComparer.Ordinal);
        var typeless = 0;

        // The four kinds of declaration a compiled card can carry. Playing a permanent runs its
        // statics and offers its replacements every event; pressing a button runs an activated
        // ability. Nothing in "play it, activate it, cast it" makes a *trigger* fire, which is
        // why the count of cards carrying one is the number worth having here.
        var activated = 0;
        var triggers = 0;
        var statics = 0;
        var replacements = 0;
        var vanilla = 0;
        var activatedInPlay = 0;
        var triggersInPlay = 0;

        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);
            if (!compiled.IsComplete)
                continue;

            complete++;

            if (compiled.Activated.Count > 0)
                activated++;

            if (compiled.Triggers.Count > 0)
                triggers++;

            if (compiled.Statics.Count > 0)
                statics++;

            if (compiled.Replacements.Count > 0)
                replacements++;

            if (!compiled.HasAbilities)
                vanilla++;

            // Not exclusive buckets, on purpose. A `Sorcery // Land` card is put on a battlefield
            // by one soak and cast by another, and an if/else here would have hidden 19 of them
            // from whichever arm came second.
            var onBoard = GoesToBattlefield(card.CardTypes);
            var asSpell = !IsPermanent(card.CardTypes)
                && (card.CardTypes.HasFlag(CardType.Instant)
                    || card.CardTypes.HasFlag(CardType.Sorcery));

            if (onBoard)
            {
                battlefield++;

                if (IsPermanent(card.CardTypes))
                    permanentsOnly++;
                else
                    lands++;

                if (compiled.Activated.Count > 0)
                    activatedInPlay++;

                if (compiled.Triggers.Count > 0)
                    triggersInPlay++;
            }

            if (asSpell)
                spells++;

            if (!onBoard && !asSpell)
            {
                // An entry with no card type at all is not a card a game can contain: the corpus
                // carries art-series prints, planes and token faces whose whole type line is
                // "Card", and a handful of them have text empty enough to compile as complete.
                // Counted apart rather than folded into the gap, because "no soak plays this card
                // type" and "this is not a card" are different findings and only the first is a
                // hole worth filling.
                if (card.CardTypes == CardType.None)
                {
                    typeless++;
                    continue;
                }

                var shape = card.CardTypes.ToString();
                untouched[shape] = untouched.GetValueOrDefault(shape) + 1;
            }
        }

        output.WriteLine($"corpus: {corpus.Count} playable cards, {complete} of them fully compiled");
        output.WriteLine($"  onto a battlefield: {battlefield}  ({permanentsOnly} permanents, {lands} lands)");
        output.WriteLine($"  cast as a spell:    {spells}");
        output.WriteLine($"  reached by nothing: {untouched.Values.Sum()}");
        output.WriteLine($"  not cards at all:   {typeless}  (art series, planes, token faces)");

        foreach (var (shape, n) in untouched.OrderByDescending(p => p.Value).Take(10))
            output.WriteLine($"    {n,6}  {shape}");

        output.WriteLine(string.Empty);
        output.WriteLine("what the complete cards declare (a card may carry several):");
        output.WriteLine($"  activated abilities:  {activated,6}  ({activatedInPlay} on a battlefield)");
        output.WriteLine($"  triggered abilities:  {triggers,6}  ({triggersInPlay} on a battlefield)");
        output.WriteLine($"  static abilities:     {statics,6}");
        output.WriteLine($"  replacement effects:  {replacements,6}");
        output.WriteLine($"  nothing at all:       {vanilla,6}");

        // The calibration. CardCompilerCoverageTests reports 15,036 complete cards against this
        // corpus; a census that counts them a different way is measuring something else, and the
        // numbers above would then be about a set nobody else has.
        Assert.True(
            complete > 15_000,
            $"only {complete} complete cards - the coverage test counts over 15,000, so this "
                + "census is reading a different corpus or a different compiler.");

        // Every complete card that is actually a card is in one of the two buckets, and the
        // third is meant to be empty. It was 759 lands before they were let in; if it grows again
        // a card type has appeared that no soak plays.
        Assert.True(
            untouched.Values.Sum() == 0,
            $"{untouched.Values.Sum()} complete cards are in no soak at all: "
                + string.Join(", ", untouched.OrderByDescending(p => p.Value).Take(5)
                    .Select(p => $"{p.Value} {p.Key}")));
    }

    /// <summary>
    /// A permanent that has transformed forgets it when the game is stored and read back.
    /// </summary>
    /// <remarks>
    /// <strong>This asserts a defect, not a rule.</strong> It is the smallest reproduction of what
    /// the soak below found by folding its logs through the door persistence uses, and it is here
    /// rather than as a red test because the fix is in <c>MtgEngine.Rules</c> and this file is a
    /// test file.
    /// <para>
    /// <c>EventLogSerializer</c> writes a card as the fourteen printed fields the rules act on,
    /// and <see cref="CardDefinition.Faces"/> is not one of them - so every card in a re-read log
    /// comes back with no faces. <c>GameReducer.Transform</c> refuses an index the card does not
    /// have, correctly, because a fold has to be total; with no faces at all there is no index it
    /// has, so <em>every</em> <c>PermanentTransformed</c> event in a stored game is silently
    /// dropped. The permanent comes back on its front face, with the front face's characteristics,
    /// and nothing anywhere says so.
    /// </para>
    /// <para>
    /// What that costs a player: <c>GameSessionService</c> saves a game by writing its log and
    /// resumes it by reading one, so **every werewolf that had flipped to its night side is a day
    /// creature again the moment the session is rehydrated**, and cannot flip back either - the
    /// resumed object has no faces for <c>Game.Transform</c> to find. It is 837 cards of the
    /// corpus, and the soak reached it because a four-player table happened to transform one.
    /// </para>
    /// <para>
    /// What it should say: <c>Assert.Equal(1, ...)</c> - the same permanent, still on its back
    /// face. Carrying <c>Faces</c> in <c>PrintedCard</c> is what would make it true.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_stored_game_remembers_that_a_permanent_had_transformed()
    {
        var alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var bob = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var game = Game.Start(
            Guid.NewGuid(),
            [
                new PlayerSetup(alice, "Alice", 20, Filler("Alice")),
                new PlayerSetup(bob, "Bob", 20, Filler("Bob")),
            ],
            new GameRandom(3),
            startingPlayerId: alice,
            abilities: new CompiledPool());

        game.BeginPlay(withMulligans: false);

        var werewolf = game.Create(alice, new CardDefinition
        {
            OracleId = "soak-two-faced",
            Name = "Soak Daybound Wolf",
            CardTypes = CardType.Creature,
            Power = 2,
            Toughness = 2,
            Faces =
            [
                new CardFace
                {
                    Name = "Soak Daybound Wolf",
                    TypeLine = "Creature — Human",
                    CardTypes = CardType.Creature,
                    Power = 2,
                    Toughness = 2,
                },
                new CardFace
                {
                    Name = "Soak Nightbound Wolf",
                    TypeLine = "Creature — Werewolf",
                    CardTypes = CardType.Creature,
                    Power = 4,
                    Toughness = 4,
                },
            ],
        }, Zone.Battlefield);

        game.Transform(werewolf);

        // It really did turn over in the game that was played.
        Assert.Equal(1, game.State.GetObject(werewolf).Permanent!.FaceIndex);

        // And the in-memory fold agrees, which is why nothing had caught this: the invariant every
        // other test asserts holds perfectly well.
        Assert.Equal(
            1,
            GameReducer.Replay(game.Log).GetObject(werewolf).Permanent!.FaceIndex);

        var stored = GameReducer.Replay(
            EventLogSerializer.Read(EventLogSerializer.Write(game.Log)));

        // The point of the test. PrintedCard carried fourteen printed fields and not Faces, so a
        // re-read card had none - and GameReducer.Transform, which correctly refuses a face index
        // the card does not have, dropped every PermanentTransformed event in a stored log. A
        // saved game came back with its werewolves on their day faces and unable to flip again,
        // on 837 corpus cards, while the in-memory fold above agreed perfectly. That is why
        // nothing caught it: the invariant every other test asserts was never violated.
        Assert.Equal(1, stored.GetObject(werewolf).Permanent!.FaceIndex);
        Assert.Equal(2, stored.GetObject(werewolf).Card.Faces.Count);

        // And it is still the back face's characteristics, not just the index.
        Assert.Equal("Soak Nightbound Wolf", stored.GetObject(werewolf).Card.Name);
    }

    /// <remarks>
    /// Run over several slices. The scatter groups the corpus reproducibly, which is what makes a
    /// failure chaseable - but it also means one run only ever plays one set of 778 combinations.
    /// A second and third seed are two more sets that had never been played, and they are run
    /// shallower because breadth is what they are for: the deep pass is slice zero, where a Saga
    /// has time to finish and a fading permanent to run out.
    /// </remarks>
    [Theory]
    [InlineData(0, 10, 1)]
    [InlineData(1, 4, 3)]
    [InlineData(2, 4, 3)]
    public void Every_compiled_permanent_survives_being_played(int slice, int turns, int every)
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present - skipping.");
            return;
        }

        var pool = new CompiledPool();

        // Permanents only, and only the ones the compiler claims to understand completely. A card
        // it could not read is not a claim about behaviour and has nothing to disappoint.
        // Shuffled, deterministically. Batched in corpus order the same twelve cards share every
        // battlefield for ever, so a lord never meets the creature it would pump and card one
        // never meets card nine thousand. The seed is fixed so the run is reproducible, and the
        // ordering is by hash so it is nothing like alphabetical.
        var playable = corpus
            .Where(c => GoesToBattlefield(c.CardTypes))
            .Where(c => CardCompiler.Compile(c).IsComplete)
            .OrderBy(c => Scatter(c.OracleId + slice.ToString(CultureInfo.InvariantCulture)))
            .ThenBy(c => c.OracleId, StringComparer.Ordinal)

            // The deep slice takes every card; the broad ones take a third each. What they are
            // for is combinations that have never shared a battlefield, and a third of the corpus
            // regrouped gives plenty of those at a third of the time. The whole set is played by
            // slice zero regardless, so nothing goes unplayed.
            .Where((_, i) => i % every == 0)
            .ToImmutableList();

        Assert.True(playable.Count > 500, $"only {playable.Count} playable permanents - wrong set.");

        var faults = new List<string>();
        var played = 0;

        for (var start = 0; start < playable.Count; start += PerGame)
        {
            var table = playable.Skip(start).Take(PerGame).ToImmutableList();

            try
            {
                Play(table, pool, turns);
                played += table.Count;
            }
            catch (Exception broke)
            {
                // The whole table is named: which of them did it is what the next run finds out
                // by bisecting, and a fault that names nothing is a fault nobody can chase.
                faults.Add(
                    string.Join(", ", table.Select(c => c.Name))
                        + $"\n      {broke.GetType().Name}: {broke.Message}");
            }
        }

        output.WriteLine(
            $"played {played} compiled permanents across {playable.Count / PerGame} games, "
                + $"with {Attacks} attacks and {Blocks} blocks declared");

        output.WriteLine(
            $"{Fired} triggered abilities fired, on {FiredOn.Count} distinct cards; "
                + $"{Folded} of the games were folded through storage; {Transformed} of the "
                + "games played turned a permanent over");

        // Combat is the half of a game that passing priority never reaches: attacking is a
        // turn-based action somebody has to take. Counted so that a soak which stops fighting
        // fails rather than going quietly green.
        // Accumulated across the slices, which is what the floor is written against: the point
        // is that combat is happening somewhere in the run, not in any one of them.
        Assert.True(
            Attacks > 500 && Blocks > 100,
            $"only {Attacks} attacks and {Blocks} blocks - combat is not being reached.");

        // The same guard as the attack count, one mechanic along, and for the same reason. A
        // trigger's condition is a closure and the only way to know it does anything is to fire
        // it; a soak that stops provoking would keep playing every card and stop exercising the
        // single largest body of card behaviour in the corpus, silently and green.
        //
        // The floor is set from a measured A/B on the shallow slice alone, so that whichever
        // slice runs first has to clear it: the same 336 games fired 1,733 triggers on 1,255
        // cards with the provocations off and 2,545 on 1,408 with them on. 2,200 sits between the
        // two, which is the only place a floor is worth anything - above the number it is meant
        // to catch and below the number it is meant to allow. The distinct-card count is the
        // weaker of the pair (the two arms are 12% apart, not 47%) and is here as a sanity check
        // rather than as the guard, because one card looping could carry the firing count alone.
        Assert.True(
            Fired > 2_200 && FiredOn.Count > 1_200,
            $"only {Fired} triggers fired on {FiredOn.Count} cards - the provocations have "
                + "stopped reaching them, whatever the rest of this test says.");

        Assert.True(
            faults.Count == 0,
            $"{faults.Count} tables broke when their cards were played:\n  "
                + string.Join("\n  ", faults.Take(12)));
    }

    /// <summary>
    /// Every activated ability the compiler builds is activated, and the game survives it.
    /// </summary>
    /// <remarks>
    /// Putting a permanent on the battlefield exercises what it does on its own: enters triggers,
    /// static abilities, whatever watches the turn go by. It never presses the button. An
    /// activated ability is code that has never run until somebody pays for it, and until this
    /// test nobody had - not once, for any card in the corpus.
    /// <para>
    /// <strong>A refusal is not a failure.</strong> The engine says no to a great many of these,
    /// correctly: wrong timing, no legal target, a cost that cannot be paid. Those are the rules
    /// working. What this looks for is the other kind - an ability that throws something the
    /// engine never meant to say, or leaves the game unable to settle.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_activated_ability_survives_being_activated()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present - skipping.");
            return;
        }

        var pool = new CompiledPool();

        var buttons = corpus
            .Where(c => GoesToBattlefield(c.CardTypes))
            .Select(c => (Card: c, Compiled: CardCompiler.Compile(c)))
            .Where(p => p.Compiled.IsComplete && p.Compiled.Activated.Count > 0)
            .OrderBy(p => Scatter(p.Card.OracleId))
            .ThenBy(p => p.Card.OracleId, StringComparer.Ordinal)
            .Select(p => p.Card)
            .ToImmutableList();

        Assert.True(buttons.Count > 200, $"only {buttons.Count} cards with abilities - wrong set.");

        var faults = new List<string>();
        var pressed = 0;

        for (var start = 0; start < buttons.Count; start += PerGame)
        {
            var table = buttons.Skip(start).Take(PerGame).ToImmutableList();

            try
            {
                pressed += Activate(table, pool);
            }
            catch (Exception broke)
            {
                faults.Add(
                    string.Join(", ", table.Select(c => c.Name))
                        + $"\n      {broke.GetType().Name}: {broke.Message}");
            }
        }

        output.WriteLine($"activated {pressed} abilities across {buttons.Count} cards");

        // A floor on the reach, not only on the outcome. This test passed while activating 254
        // abilities out of 3,403 cards - every other one refused for summoning sickness or for
        // want of a target it would have accepted - and a soak that stops reaching things is a
        // soak that keeps passing while checking nothing. The number is what it checks.
        Assert.True(
            pressed > 1800,
            $"only {pressed} abilities could be activated at all - the harness has stopped "
                + "reaching them, whatever the rest of this test says.");

        Assert.True(
            faults.Count == 0,
            $"{faults.Count} tables broke when their abilities were used:\n  "
                + string.Join("\n  ", faults.Take(12)));
    }

    /// <summary>
    /// Every compiled instant and sorcery is cast, and the game survives it.
    /// </summary>
    /// <remarks>
    /// The other two soaks put permanents on a battlefield and pressed their buttons. Neither
    /// casts anything: a spell that resolves and does something to the board is a third body of
    /// code, and it was the last part of the corpus that had never run.
    /// <para>
    /// Spells are the hardest of the three to reach, because most of them want a target and the
    /// legal one differs card by card. The same answer as the abilities: offer the plausible
    /// shapes in turn and count how many actually resolve, so that a harness which stops reaching
    /// them fails rather than passes quietly.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_compiled_spell_survives_being_cast()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present - skipping.");
            return;
        }

        var pool = new CompiledPool();

        var spells = corpus
            .Where(c => !IsPermanent(c.CardTypes)
                && (c.CardTypes.HasFlag(CardType.Instant) || c.CardTypes.HasFlag(CardType.Sorcery)))
            .Where(c => CardCompiler.Compile(c).IsComplete)
            .OrderBy(c => Scatter(c.OracleId))
            .ThenBy(c => c.OracleId, StringComparer.Ordinal)
            .ToImmutableList();

        Assert.True(spells.Count > 500, $"only {spells.Count} compiled spells - wrong set.");

        var faults = new List<string>();
        var resolved = 0;

        for (var start = 0; start < spells.Count; start += PerGame)
        {
            var hand = spells.Skip(start).Take(PerGame).ToImmutableList();

            try
            {
                resolved += Cast(hand, pool);
            }
            catch (Exception broke)
            {
                faults.Add(
                    string.Join(", ", hand.Select(c => c.Name))
                        + $"\n      {broke.GetType().Name}: {broke.Message}");
            }
        }

        output.WriteLine($"cast {resolved} spells of {spells.Count}");

        output.WriteLine($"combat retries reached {_combatRetriesReached}");
        foreach (var (why, n) in Refusals.OrderByDescending(p => p.Value).Take(10))
            output.WriteLine($"  refused {n,5}  {why}");

        // The histogram that ranks the work: one entry per CARD that never resolved, keyed by
        // the last thing the engine said about it. A refusal count is attempts, not cards - the
        // combat-retry investigation recorded that reading attempts as a ceiling overcounted by
        // 20x - so the ranking below is what says where the unresolved spells actually are.
        var unresolved = spells
            .Where(c => !ResolvedSpells.Contains(c.Name))
            .ToList();

        output.WriteLine(string.Empty);
        output.WriteLine($"spells that never resolved: {unresolved.Count} of {spells.Count}");

        var byCause = unresolved
            .GroupBy(
                c => LastRefusal.GetValueOrDefault(c.Name, "(no refusal recorded at all)"),
                StringComparer.Ordinal)
            .OrderByDescending(g => g.Count());

        foreach (var group in byCause)
        {
            output.WriteLine($"  {group.Count(),5}  {group.Key}");
            foreach (var card in group.Take(6))
                output.WriteLine($"           {card.Name}");
        }

        Assert.True(
            faults.Count == 0,
            $"{faults.Count} hands broke when their spells were cast:\n  "
                + string.Join("\n  ", faults.Take(12)));

        // The combat retry has to have run. It spent its whole life bailing before its loop -
        // 321 calls, 0 arrivals - and every assertion in this test passed throughout, because
        // nothing here distinguishes "the spells could not be cast" from "the code meant to
        // cast them never executed". This is the cheapest sentence that tells them apart.
        Assert.True(
            _combatRetriesReached > 0,
            "the combat retry never reached its loop, so every spell needing an attacking or "
                + "blocking creature was refused by the harness rather than by the rules.");

        Assert.True(
            resolved > spells.Count / 3,
            $"only {resolved} of {spells.Count} spells could be cast at all - the harness has "
                + "stopped reaching them, whatever the rest of this test says.");
    }

    /// <summary>
    /// Every fully read land is played from hand, tapped, and made to say what it produces.
    /// </summary>
    /// <remarks>
    /// The permanent soak reaches a land, but only as a thing that sits there: it is conjured
    /// onto the battlefield, its statics run, and nothing ever presses it. **No land in the
    /// corpus had been tapped for mana by any test in this repository**, which means the single
    /// largest body of behaviour the compiler builds - a mana ability - had never once run
    /// against a real card. 826 fully read lands were in that position.
    /// <para>
    /// A land is different from every other permanent in three ways this has to respect. It
    /// arrives by a <em>land drop</em> (CR 305.1, 505.6b), which is a special action and not a
    /// spell, so the enters-tapped replacements and the arrival questions run on a path
    /// <c>Game.Create</c> skips entirely - that path has already produced one whole-class bug
    /// here. It taps for mana without using the stack (CR 605.3b). And since CR 305.6 moved off
    /// the printed card, **its basic-land mana ability is granted by the layers**, computed from
    /// the subtypes the permanent has right now: 113 already-complete lands changed behaviour
    /// when that moved, and nothing anywhere played one to find out what happened.
    /// </para>
    /// <para>
    /// <strong>The assertion is what comes out, not merely that nothing threw.</strong> The
    /// other soaks check invariants because a creature's text can mean anything; a mana ability
    /// declares exactly what it adds, so the pool before and after can be compared against the
    /// declaration. A land that adds the wrong mana, or adds none, fails here rather than
    /// passing quietly - which is the whole reason this exists and the other three do not
    /// suffice.
    /// </para>
    /// <para>
    /// One player holds every land in the game. A land drop is one per player per turn, so
    /// splitting the table across two would halve the turns - but a mana ability is activated by
    /// whoever has priority, and on Alice's main phase Bob has none. Split, half the lands would
    /// reach the battlefield and never be pressed.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_compiled_land_taps_for_what_it_says()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present - skipping.");
            return;
        }

        var pool = new CompiledPool();

        var lands = corpus
            .Where(c => IsLand(c.CardTypes))
            .Where(c => CardCompiler.Compile(c).IsComplete)
            .OrderBy(c => Scatter(c.OracleId))
            .ThenBy(c => c.OracleId, StringComparer.Ordinal)
            .ToImmutableList();

        Assert.True(lands.Count > 500, $"only {lands.Count} compiled lands - wrong set.");

        var found = new LandFindings();

        for (var start = 0; start < lands.Count; start += LandsPerGame)
        {
            var table = lands.Skip(start).Take(LandsPerGame).ToImmutableList();

            try
            {
                TapForMana(table, pool, found);
            }
            catch (Exception broke)
            {
                // Which of the twelve was it? Re-run each alone, the way the unsoaked-card
                // harness does: a fault that names a whole table names nothing anybody can act
                // on.
                var alone = 0;

                foreach (var card in table)
                {
                    try
                    {
                        TapForMana([card], pool, found);
                    }
                    catch (Exception itself)
                    {
                        alone++;
                        Note(
                            found.Faults,
                            $"{itself.GetType().Name}: {Shorten(itself.Message)}",
                            card.Name);
                    }
                }

                if (alone == 0)
                {
                    Note(
                        found.Faults,
                        $"(interaction) {broke.GetType().Name}: {Shorten(broke.Message)}",
                        string.Join(", ", table.Select(c => c.Name)));
                }
            }
        }

        output.WriteLine($"fully read lands:                  {lands.Count,6}");
        output.WriteLine($"  played from hand as a land drop: {found.Dropped,6}");
        output.WriteLine($"  distinct lands that reached one: {found.Reached.Count,6}");
        output.WriteLine($"  entered tapped:                  {found.EnteredTapped,6}");
        output.WriteLine($"  asked a question as they landed: {found.Questioned,6}");
        output.WriteLine($"mana abilities activated:          {found.Activated,6}");
        output.WriteLine($"  distinct lands that made mana:   {found.Producers.Count,6}");
        output.WriteLine($"  exactly what they promised:      {found.Exact,6}");
        output.WriteLine($"  production the text decides:     {found.Unreadable,6}");
        output.WriteLine($"  offered a mana ability:          {found.Offering.Count,6}");
        output.WriteLine($"non-mana buttons pressed:          {found.Pressed,6}");
        output.WriteLine($"  distinct lands that pressed one: {found.Pressers.Count,6}");
        output.WriteLine($"  offered one:                     {found.Buttons.Count,6}");
        output.WriteLine($"lands with no mana ability at all: {found.NoManaAbility.Count,6}");

        // The residue that matters: a land the compiler gave a mana ability, put on a real
        // battlefield, and asked for mana, which never produced any. Most of these are the rules
        // saying no - a cost this harness cannot pay, a condition it does not arrange - and the
        // engine's own words are printed beside each so that "the rules refused" and "the land is
        // broken" can be told apart by reading rather than by assuming.
        var silent = found.Offering.Except(found.Producers, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        output.WriteLine(string.Empty);
        output.WriteLine($"lands that offered mana and never produced any: {silent.Count}");
        foreach (var name in silent)
            output.WriteLine($"           {name}  --  {found.WhyNot.GetValueOrDefault(name, "never even tried")}");

        output.WriteLine(string.Empty);
        output.WriteLine("lands whose pool did not match what the ability said:");
        foreach (var (why, names) in found.WrongMana.OrderByDescending(p => p.Value.Count))
        {
            output.WriteLine($"  {names.Count,5}  {why}");
            foreach (var name in names.Take(6))
                output.WriteLine($"           {name}");
        }

        output.WriteLine(string.Empty);
        output.WriteLine("mana abilities that also do something else, and did not add what they said:");
        foreach (var (why, names) in found.RiderMana.OrderByDescending(p => p.Value.Count))
        {
            output.WriteLine($"  {names.Count,5}  {why}");
            foreach (var name in names.Take(6))
                output.WriteLine($"           {name}");
        }

        output.WriteLine(string.Empty);
        output.WriteLine("abilities whose production the game decides, which decided on none:");
        foreach (var (why, names) in found.AddedNothing.OrderByDescending(p => p.Value.Count))
        {
            output.WriteLine($"  {names.Count,5}  {why}");
            foreach (var name in names.Take(6))
                output.WriteLine($"           {name}");
        }

        output.WriteLine(string.Empty);
        output.WriteLine("lands that lost a basic land type's intrinsic ability (CR 305.6):");
        foreach (var (why, names) in found.LostBasicMana.OrderByDescending(p => p.Value.Count))
        {
            output.WriteLine($"  {names.Count,5}  {why}");
            foreach (var name in names.Take(8))
                output.WriteLine($"           {name}");
        }

        output.WriteLine(string.Empty);
        output.WriteLine("faults:");
        foreach (var (why, names) in found.Faults.OrderByDescending(p => p.Value.Count))
        {
            output.WriteLine($"  {names.Count,5}  {why}");
            foreach (var name in names.Take(6))
                output.WriteLine($"           {name}");
        }

        output.WriteLine(string.Empty);
        foreach (var (why, n) in found.Refused.OrderByDescending(p => p.Value).Take(12))
            output.WriteLine($"  refused {n,5}  {why}");

        output.WriteLine(string.Empty);
        output.WriteLine("the lands that carry no mana ability at all:");
        foreach (var name in found.NoManaAbility.Order(StringComparer.Ordinal))
            output.WriteLine($"           {name}");

        // A floor on the reach before anything about the outcome, the discipline the other soaks
        // had to learn twice: a harness that stops playing lands passes exactly like one that
        // plays them all. The land drop is the reachable half - a few lands may only be played
        // under a condition this harness does not arrange - and the mana count is the half that
        // matters, because a run that drops every land and presses none has checked nothing.
        Assert.True(
            found.Dropped > 700,
            $"only {found.Dropped} of {lands.Count} lands were played from hand - the drop loop "
                + "has stopped reaching them, whatever the rest of this test says.");

        Assert.True(
            found.Activated > 700 && found.Producers.Count > 500,
            $"only {found.Activated} mana abilities fired on {found.Producers.Count} distinct "
                + "lands - the tapping has stopped reaching them.");

        // CR 305.6 is not a measurement, it is a rule: an object with the land card type and a
        // basic land type *has* "{T}: Add [symbol]", however its text box reads. This is asked of
        // the permanent on the battlefield rather than of the card, because since the rule moved
        // into the layers the permanent is the only place the answer exists.
        Assert.True(
            found.LostBasicMana.Count == 0,
            $"{found.LostBasicMana.Sum(p => p.Value.Count)} lands with a basic land type do not "
                + "have its intrinsic mana ability (CR 305.6): "
                + string.Join("; ", found.LostBasicMana.Take(4)
                    .Select(p => $"{p.Value.Count} {p.Key}")));

        // What a mana ability adds is the one thing on a card that is written down exactly, so
        // this is an equality and not a ratchet.
        Assert.True(
            found.WrongMana.Count == 0,
            $"{found.WrongMana.Sum(p => p.Value.Count)} lands added mana their ability did not "
                + "promise:\n  "
                + string.Join(
                    "\n  ",
                    found.WrongMana.OrderByDescending(p => p.Value.Count).Take(8)
                        .Select(p => $"{p.Value.Count}  {p.Key}  e.g. {p.Value[0]}")));

        Assert.True(
            found.Faults.Count == 0,
            $"{found.Faults.Sum(p => p.Value.Count)} lands broke when they were played:\n  "
                + string.Join(
                    "\n  ",
                    found.Faults.OrderByDescending(p => p.Value.Count).Take(8)
                        .Select(p => $"{p.Value.Count}  {p.Key}  e.g. {p.Value[0]}")));
    }

    /// <summary>How many lands share a game. One land drop per turn, so this is also the turns.</summary>
    private const int LandsPerGame = 12;

    /// <summary>
    /// How many untap steps the tapping gets.
    /// </summary>
    /// <remarks>
    /// Most mana abilities cost a tap, so a land offering several - "Add one mana of any color"
    /// compiles to five alternatives, one per colour - can only produce one of them per turn.
    /// Eight covers every alternative count in the corpus with turns to spare for a land that
    /// entered tapped and for the round its non-mana button took.
    /// </remarks>
    private const int TapRounds = 8;

    /// <summary>The five basic land types and the mana each carries (CR 305.6).</summary>
    private static readonly (string Subtype, ManaColor Colour)[] BasicLandTypes =
    [
        ("Plains", ManaColor.White),
        ("Island", ManaColor.Blue),
        ("Swamp", ManaColor.Black),
        ("Mountain", ManaColor.Red),
        ("Forest", ManaColor.Green),
    ];

    /// <summary>What the land soak found, gathered across every game in the run.</summary>
    private sealed class LandFindings
    {
        public int Dropped;
        public int EnteredTapped;
        public int Questioned;
        public int Activated;
        public int Exact;
        public int Unreadable;
        public int Pressed;

        public HashSet<string> Reached { get; } = new(StringComparer.Ordinal);

        /// <summary>Lands that offered a mana ability, whether or not one ever paid off.</summary>
        public HashSet<string> Offering { get; } = new(StringComparer.Ordinal);

        /// <summary>Lands that offered a button that is not a mana ability.</summary>
        public HashSet<string> Buttons { get; } = new(StringComparer.Ordinal);

        /// <summary>The last thing the engine said when a land refused to tap, by land.</summary>
        public Dictionary<string, string> WhyNot { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Producers { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Pressers { get; } = new(StringComparer.Ordinal);

        public HashSet<string> NoManaAbility { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, List<string>> WrongMana { get; } = new(StringComparer.Ordinal);

        /// <summary>Divergences on an ability that also does something else. Reported, not asserted.</summary>
        public Dictionary<string, List<string>> RiderMana { get; } = new(StringComparer.Ordinal);

        /// <summary>Abilities whose production the game decides, which decided on none.</summary>
        public Dictionary<string, List<string>> AddedNothing { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, List<string>> LostBasicMana { get; } =
            new(StringComparer.Ordinal);

        public Dictionary<string, List<string>> Faults { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, int> Refused { get; } = new(StringComparer.Ordinal);
    }

    private static void Note(Dictionary<string, List<string>> into, string cause, string what)
    {
        if (!into.TryGetValue(cause, out var names))
            into[cause] = names = [];

        if (!names.Contains(what, StringComparer.Ordinal))
            names.Add(what);
    }

    /// <summary>
    /// Plays these lands from hand one per turn, then taps each of them for what it says.
    /// </summary>
    private static void TapForMana(
        ImmutableList<CardDefinition> table, CompiledPool pool, LandFindings found)
    {
        var alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var bob = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var game = Game.Start(
            Guid.NewGuid(),
            [
                // Deep enough that a land asking for life, and the cumulative upkeep a few of
                // them carry, do not end the game before the tapping starts.
                new PlayerSetup(alice, "Alice", 400, Filler("Alice")),
                new PlayerSetup(bob, "Bob", 400, Filler("Bob")),
            ],
            new GameRandom(17),
            startingPlayerId: alice,
            abilities: pool);

        // No opening hand, and each land is put into it only at the moment it is to be played.
        // A hand of twelve is already over the maximum on its own, the cleanup step asks for a
        // discard (CR 514.1), and the harness answers by throwing away the very cards it came to
        // play: measured, that cost 274 of the 826 lands before one of them reached a
        // battlefield. The lands still share a board afterwards - only the hand is kept legal.
        game.BeginPlay(openingHandSize: 0, withMulligans: false);

        var waiting = new Queue<CardDefinition>(table);

        Settle(game);

        var placed = new List<ObjectId>();

        for (var guard = 0; guard < 4_000 && waiting.Count > 0; guard++)
        {
            if (game.State.IsOver)
                break;

            if (game.State.Choice is { } choice)
            {
                Decide(game, choice);
                continue;
            }

            if (game.State.ActivePlayerId == alice
                && game.State.Priority.Holder == alice
                && game.State.IsSorcerySpeedFor(alice)
                && game.State.GetPlayer(alice).LandsPlayedThisTurn == 0)
            {
                var definition = waiting.Dequeue();
                var card = game.Create(alice, definition, Zone.Hand);

                try
                {
                    var arrived = game.PlayLand(alice, card);
                    found.Dropped++;
                    found.Reached.Add(definition.Name);
                    placed.Add(arrived);

                    // The question a land can ask as it arrives - a shockland's "you may pay 2
                    // life", a fetchland's search. Ten of them ask one, and an earlier harness
                    // that did not answer left the board frozen with the question standing.
                    if (game.State.Choice is not null)
                        found.Questioned++;

                    Settle(game);

                    if (game.State.TryGetObject(arrived, out var landed)
                        && landed.Permanent is { IsTapped: true })
                    {
                        found.EnteredTapped++;
                    }
                }
                catch (InvalidOperationException refused)
                    when (!refused.Message.Contains("did not settle", StringComparison.Ordinal))
                {
                    // "You may only play a land if you control a Swamp", say. The rules talking.
                    var why = Shorten(refused.Message);
                    found.Refused[why] = found.Refused.GetValueOrDefault(why) + 1;
                }

                continue;
            }

            if (game.State.Priority.Holder is { } holder)
            {
                game.PassPriority(holder);
                continue;
            }

            break;
        }

        var used = new HashSet<string>(StringComparer.Ordinal);

        for (var round = 0; round < TapRounds; round++)
        {
            if (!ReachMain(game, alice))
                break;

            var anything = false;

            foreach (var id in placed)
            {
                if (!game.State.TryGetObject(id, out var land)
                    || land.Zone != Zone.Battlefield
                    || land.Permanent is null)
                {
                    continue;
                }

                var offered = Game.ActivatedAbilitiesOf(game.State, pool, land);

                if (round == 0)
                    CheckIntrinsic(land, offered, found);

                // Which kind of button goes first alternates with the round, and both halves of
                // that matter.
                //
                // Most abilities of both kinds cost a tap, and a land has one tap per turn, so
                // whichever is tried first is the only one that fires that round - fix the order
                // and the other kind is never reached at all. Alternating gives each of them
                // four of the eight rounds.
                //
                // Mana goes first on the round the land arrives, because a good many of these
                // lands pay for their non-mana ability by sacrificing themselves - every
                // Panorama, every Blighted land, every Horizon land - and a land that has been
                // sacrificed has no mana ability left to try. Measured: pressing those first
                // left 65 lands that offered mana and were never once asked for it.
                foreach (var mana in round % 2 == 0 ? new[] { true, false } : [false, true])
                {
                    foreach (var ability in offered)
                    {
                        if (ability.IsManaAbility != mana)
                            continue;

                        // The land may have left: a fetchland sacrifices itself to pay for its
                        // own ability, and the abilities after it in this list belong to an
                        // object that is no longer on the battlefield.
                        if (!game.State.TryGetObject(id, out var still)
                            || still.Zone != Zone.Battlefield)
                        {
                            break;
                        }

                        var key = id.Value.ToString() + "/" + ability.Id;
                        if (!used.Add(key))
                            continue;

                        if (mana)
                        {
                            // Put back if it was refused, so the next untap step tries it again:
                            // a land offering five alternatives can only pay the tap for one of
                            // them per turn, and dropping the other four is how a harness
                            // silently stops reaching four fifths of what it came for.
                            if (Taps(game, alice, id, land.Card, ability, found))
                                anything = true;
                            else
                                used.Remove(key);

                            continue;
                        }

                        // A land whose button is not a mana ability - "{T}: Target creature gains
                        // shroud", "{2}, {T}: Draw a card". Generously funded, with the shapes the
                        // ability soak offers, because it is code that runs only when somebody
                        // pays for it and nothing else here pays.
                        Fund(game, alice);

                        if (Press(game, alice, id, ability.Id, Aims(game, alice, id)))
                        {
                            found.Pressed++;
                            found.Pressers.Add(land.Card.Name);
                            anything = true;
                        }
                        else
                        {
                            used.Remove(key);
                        }

                        Settle(game);
                    }
                }
            }

            if (!anything)
                break;
        }

        Check(game, pool);
    }

    /// <summary>
    /// CR 305.6: a land with a basic land type has that type's mana ability, text box or no.
    /// </summary>
    /// <remarks>
    /// Asked of the permanent rather than of the card on purpose. The ability used to be compiled
    /// into the card off its printed subtypes, which made a Mountain turned into an Island still
    /// tap for red; it is granted by the layers now, computed from the subtypes the object has at
    /// this moment. 113 already-complete lands changed behaviour when it moved and nothing played
    /// one afterwards - this is the check that would have caught a mistake there.
    /// <para>
    /// The printed subtypes are the floor and not the answer: an effect may <em>add</em> a basic
    /// land type (Urborg makes every land a Swamp) and the extra ability that comes with it is
    /// correct. So this asks whether each printed basic type's colour is on offer, never whether
    /// anything else is.
    /// </para>
    /// </remarks>
    private static void CheckIntrinsic(
        GameObject land, IReadOnlyList<ActivatedAbilityDefinition> offered, LandFindings found)
    {
        var colours = offered
            .Where(a => a.IsManaAbility)
            .SelectMany(a => a.Produces)
            .Where(p => !p.FromChosenColor && p.Restriction is null)
            .Select(p => p.Color)
            .ToHashSet();

        if (offered.Any(a => a.IsManaAbility))
            found.Offering.Add(land.Card.Name);
        else
            found.NoManaAbility.Add(land.Card.Name);

        if (offered.Any(a => !a.IsManaAbility))
            found.Buttons.Add(land.Card.Name);

        foreach (var (subtype, colour) in BasicLandTypes)
        {
            if (!land.Card.Subtypes.Contains(subtype, StringComparer.OrdinalIgnoreCase))
                continue;

            if (!colours.Contains(colour))
            {
                Note(
                    found.LostBasicMana,
                    $"a {subtype} that does not tap for {colour}",
                    land.Card.Name);
            }
        }
    }

    /// <summary>
    /// Taps one land for mana and checks the pool against what the ability said it would add.
    /// </summary>
    /// <remarks>
    /// The reading is only taken on an ability that charges no mana, and the reason is that
    /// funding destroys it: the cost is paid out of the same pool the production lands in, and
    /// which colours a generic cost eats is the engine's choice rather than this harness's, so
    /// the difference across the activation would no longer be the production. A cost-bearing
    /// mana ability is still activated - it is still code that has never run - and still has to
    /// not throw; it simply has no exact claim to check. That is a small minority of the set.
    /// </remarks>
    private static bool Taps(
        Game game,
        Guid player,
        ObjectId source,
        CardDefinition card,
        ActivatedAbilityDefinition ability,
        LandFindings found)
    {
        var funded = !ability.ManaCost.Symbols.IsEmpty;

        if (funded)
            Fund(game, player);

        var before = game.State.GetPlayer(player).ManaPool;

        try
        {
            game.ActivateAbility(player, source, ability.Id, []);
        }
        catch (InvalidOperationException refused)
            when (!refused.Message.Contains("did not settle", StringComparison.Ordinal))
        {
            // Tapped already, a cost that cannot be paid, a condition that is not met. The rules
            // working, and the next untap step may be the moment this one wanted.
            var why = Shorten(refused.Message);
            found.Refused[why] = found.Refused.GetValueOrDefault(why) + 1;
            found.WhyNot[card.Name] = why;
            return false;
        }

        var after = game.State.GetPlayer(player).ManaPool;

        found.Activated++;
        found.Producers.Add(card.Name);

        if (!funded)
        {
            var got = Pool(before, after);
            var promised = Promised(ability);
            var said = $"said {promised}, added {(got.Length == 0 ? "nothing" : got)}"
                + $"   [{Shorten(ability.Text)}]";

            if (promised is null)
            {
                // "Add one mana of the chosen color", "add {C} for each counter removed this
                // way". What it produces is decided in the game rather than printed, so there is
                // no exact claim to check - only whether anything came out at all, which is
                // reported because both answers are legitimate: a land that has not yet named a
                // colour produces nothing, correctly (CR 106.5).
                found.Unreadable++;

                if (got.Length == 0)
                    Note(found.AddedNothing, $"[{Shorten(ability.Text)}]", card.Name);
            }
            else if (string.Equals(got, promised, StringComparison.Ordinal))
            {
                found.Exact++;
            }
            else if (!ability.Effects.IsEmpty)
            {
                // The rider is the other half of a line like "{T}: Add {C}{C}. This land doesn't
                // untap during your next untap step", and it resolves with the ability
                // (CR 605.3b). Almost none of them touch the pool, but one that did would read
                // exactly like a broken promise - so a divergence here is reported rather than
                // asserted, and the bucket exists so that "the rider added mana" and "the mana
                // was wrong" are never the same finding.
                Note(found.RiderMana, said, card.Name);
            }
            else
            {
                Note(found.WrongMana, said, card.Name);
            }
        }

        Settle(game);
        return true;
    }

    /// <summary>Generous funding, so a cost is never what stops an ability being reached.</summary>
    private static void Fund(Game game, Guid player)
    {
        foreach (var colour in Enum.GetValues<ManaColor>())
        {
            for (var i = 0; i < 4; i++)
                game.AddMana(player, colour);
        }
    }

    /// <summary>
    /// What actually arrived in the pool, spelled the way <see cref="Promised"/> spells it.
    /// </summary>
    /// <remarks>
    /// The two halves of colourless are kept apart - <c>c</c> for a production carrying
    /// <see cref="ManaColor.Colorless"/> and <c>C</c> for one carrying no colour at all - because
    /// the reducer puts them in different places: a null colour goes to
    /// <c>ManaPool.Colorless</c>, and anything else goes into the coloured dictionary. Merging
    /// them here would hide exactly the mix-up that difference can cause, where mana lands
    /// somewhere no cost ever looks.
    /// </remarks>
    private static string Pool(ManaPool before, ManaPool after)
    {
        var parts = new List<string>(8);

        foreach (var colour in Enum.GetValues<ManaColor>())
        {
            var gained = after[colour] - before[colour];
            if (gained != 0)
                parts.Add($"{gained}{Letter(colour)}");
        }

        var colourless = after.Colorless - before.Colorless;
        if (colourless != 0)
            parts.Add($"{colourless}C");

        var restricted = after.Restricted.Count - before.Restricted.Count;
        if (restricted != 0)
            parts.Add($"{restricted}*");

        return string.Join("+", parts);
    }

    /// <summary>
    /// What the ability says it adds, or null when the game rather than the text decides.
    /// </summary>
    private static string? Promised(ActivatedAbilityDefinition ability)
    {
        var coloured = new Dictionary<ManaColor, int>();
        var colourless = 0;
        var restricted = 0;

        foreach (var production in ability.Produces)
        {
            // The colour a permanent named as it entered (CR 614.12), and "add {C} for each
            // counter removed this way" - neither is a number this can read off the card.
            if (production.FromChosenColor || production.FromCounterCost)
                return null;

            if (production.Amount <= 0)
                continue;

            // CR 106.6: restricted mana is held apart from the ordinary pool, one entry per mana,
            // because the restriction travels with the individual mana rather than its colour.
            if (production.Restriction is not null)
            {
                restricted += production.Amount;
                continue;
            }

            if (production.Color is { } colour)
                coloured[colour] = coloured.GetValueOrDefault(colour) + production.Amount;
            else
                colourless += production.Amount;
        }

        var parts = new List<string>(8);

        foreach (var colour in Enum.GetValues<ManaColor>())
        {
            if (coloured.GetValueOrDefault(colour) is var n and > 0)
                parts.Add($"{n}{Letter(colour)}");
        }

        if (colourless > 0)
            parts.Add($"{colourless}C");

        if (restricted > 0)
            parts.Add($"{restricted}*");

        return string.Join("+", parts);
    }

    private static string Letter(ManaColor colour) => colour switch
    {
        ManaColor.White => "W",
        ManaColor.Blue => "U",
        ManaColor.Black => "B",
        ManaColor.Red => "R",
        ManaColor.Green => "G",
        _ => "c",
    };

    /// <summary>
    /// Walks the game on to the player's next precombat main phase, so everything has untapped.
    /// </summary>
    private static bool ReachMain(Game game, Guid player)
    {
        var from = game.State.TurnNumber;

        for (var guard = 0; guard < 600; guard++)
        {
            if (game.State.IsOver)
                return false;

            if (game.State.Choice is { } choice)
            {
                Decide(game, choice);
                continue;
            }

            if (game.State.TurnNumber > from
                && game.State.ActivePlayerId == player
                && game.State.CurrentStep == TurnStep.PrecombatMain
                && game.State.Stack.IsEmpty
                && game.State.Priority.Holder == player)
            {
                return true;
            }

            if (game.State.Priority.Holder is { } holder)
            {
                game.PassPriority(holder);
                continue;
            }

            return false;
        }

        return false;
    }

    private static int Cast(ImmutableList<CardDefinition> hand, CompiledPool pool)
    {
        var alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var bob = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var game = Game.Start(
            Guid.NewGuid(),
            [
                new PlayerSetup(alice, "Alice", 20, Filler("Alice")),
                new PlayerSetup(bob, "Bob", 20, Filler("Bob")),
            ],
            new GameRandom(13),
            startingPlayerId: alice,
            abilities: pool);

        game.BeginPlay(withMulligans: false);

        // A board varied enough to be a legal target for something. A field of vanilla bears
        // refuses every spell that names an artifact, an enchantment or a flier - which was most
        // of them: "illegal target" was 9 of the 10 commonest refusals before this.
        foreach (var owner in new[] { alice, bob })
        {
            game.Create(owner, Filler(owner.ToString())[0], Zone.Battlefield);

            game.Create(owner, new CardDefinition
            {
                OracleId = $"soak-artifact-{owner}",
                Name = "Soak Relic",
                CardTypes = CardType.Artifact,
            }, Zone.Battlefield);

            game.Create(owner, new CardDefinition
            {
                OracleId = $"soak-enchantment-{owner}",
                Name = "Soak Charm",
                CardTypes = CardType.Enchantment,
            }, Zone.Battlefield);

            // With haste, so combat can actually be staged: everything here arrives this turn
            // and a board of summoning-sick creatures cannot declare an attacker (CR 302.6),
            // which is why the first attempt at the combat pass quietly reached nothing.
            game.Create(owner, new CardDefinition
            {
                OracleId = $"soak-hasty-{owner}",
                Name = "Soak Charger",
                CardTypes = CardType.Creature,
                Power = 2,
                Toughness = 2,
                Keywords = KeywordAbility.Haste,
            }, Zone.Battlefield);

            game.Create(owner, new CardDefinition
            {
                OracleId = $"soak-flier-{owner}",
                Name = "Soak Flier",
                CardTypes = CardType.Creature,
                Power = 1,
                Toughness = 1,
                Keywords = KeywordAbility.Flying,
            }, Zone.Battlefield);
        }

        // Something in each graveyard. "Target creature card in a graveyard" is a whole family
        // of spells and an empty graveyard refuses every one of them.
        foreach (var owner in new[] { alice, bob })
        {
            game.Create(owner, Filler(owner.ToString())[1], Zone.Graveyard);

            game.Create(owner, new CardDefinition
            {
                OracleId = $"soak-buried-{owner}",
                Name = "Soak Buried Spell",
                ManaCostRaw = "{1}",
                Cmc = 1,
                OracleText = "You gain 1 life.",
                CardTypes = CardType.Instant,
            }, Zone.Graveyard);
        }

        Settle(game);

        var cast = 0;
        var attempted = 0;
        var missed = new List<CardDefinition>();

        foreach (var card in hand)
        {
            var inHand = PutInHand(game, alice, card);

            foreach (var colour in Enum.GetValues<ManaColor>())
            {
                for (var i = 0; i < 8; i++)
                    game.AddMana(alice, colour);
            }

            if (Speak(game, alice, inHand, Aims(game, alice, inHand)))
            {
                cast++;
            }
            // Alice puts it up herself: Bob does not hold priority during her main phase, so a
            // decoy cast by him is never cast at all - which is why the first version of this
            // changed nothing and looked exactly like a counterspell that could not be reached.
            else if (Decoy(game, alice) is { } waiting)
            {
                // A counterspell has nothing to answer on an empty stack, and "illegal target:
                // target spell" was the commonest refusal in the whole run - 587 of them, every
                // counterspell in the corpus, none of them ever exercised. So one is put up for
                // them to answer and the card is asked a second time.
                if (Speak(game, alice, inHand, [[Target.ToSpell(waiting)]]))
                    cast++;
                else
                    missed.Add(card);
            }
            else
            {
                missed.Add(card);
            }

            // Every other one is left on the stack while the next is cast, so that some spells
            // are cast in response to a spell rather than into silence. A stack that is emptied
            // after every cast never has two things on it, and "whenever you cast", split second
            // and the ordering rules never see a second object.
            //
            // The rest are resolved: a stack that is never emptied refuses every sorcery after
            // the first, and calling that "correctly refused" is how this test reached nothing
            // twice already.
            if (++attempted % 2 == 0)
                Empty(game);
        }

        cast += InCombat(game, alice, bob, missed);

        Check(game, pool);
        return cast;
    }

    /// <summary>
    /// Tries again with an attacker declared, for the spells that need one.
    /// </summary>
    /// <remarks>
    /// "Target attacking creature" and "target attacking or blocking creature" were the second
    /// commonest refusal in the run, and no board can satisfy them: they need a <em>situation</em>
    /// rather than a permanent. So combat is staged once and everything the main phase could not
    /// cast is offered it - which also reaches the instants that are only legal here.
    /// </remarks>
    private static int InCombat(Game game, Guid alice, Guid bob, List<CardDefinition> missed)
    {
        if (missed.Count == 0)
            return 0;

        for (var guard = 0; guard < 400; guard++)
        {
            if (game.State.ActivePlayerId == alice
                && game.State.CurrentStep == TurnStep.DeclareAttackers
                && !game.State.Combat.AttackersDeclared)
            {
                break;
            }

            if (game.State.Choice is { } waiting)
            {
                Decide(game, waiting);
                continue;
            }

            if (game.State.Priority.Holder is { } holder)
            {
                game.PassPriority(holder);
                continue;
            }

            return 0;
        }

        if (game.State.CurrentStep != TurnStep.DeclareAttackers)
            return 0;

        // The board deliberately contains a hasty creature so that combat can be staged on the
        // turn everything arrives. Selecting on HasSummoningSickness threw it away again:
        // CR 302.6 makes haste ignore the sickness rather than clear it, so the flag stays
        // set and this predicate matched nothing, 321 times out of 321. The retry below had
        // never run once - which is invisible from the outside, because a soak that reaches
        // nothing and a soak that reaches everything both pass.
        var attacker = game.State.Battlefield.FirstOrDefault(id =>
            game.State.GetObject(id).ControllerId == alice
            && game.State.GetObject(id).Card.CardTypes.HasFlag(CardType.Creature)
            && game.State.GetObject(id).Permanent is { IsTapped: false } permanent
            && (!permanent.HasSummoningSickness
                || game.State.GetObject(id).Card.Keywords.HasFlag(KeywordAbility.Haste)));

        if (attacker == default)
            return 0;

        try
        {
            game.DeclareAttackers(
                alice,
                new Dictionary<ObjectId, AttackTarget> { [attacker] = AttackTarget.Player(bob) });
        }
        catch (InvalidOperationException)
        {
            return 0;
        }

        _combatRetriesReached++;
        Settle(game);

        var cast = 0;

        foreach (var card in missed)
        {
            var inHand = PutInHand(game, alice, card);

            foreach (var colour in Enum.GetValues<ManaColor>())
            {
                for (var i = 0; i < 8; i++)
                    game.AddMana(alice, colour);
            }

            if (Speak(game, alice, inHand, Aims(game, alice, inHand)))
                cast++;

            Empty(game);
        }

        return cast;
    }

    /// <summary>Casts one spell, letting the engine's own refusals through and nothing else.</summary>
    private static bool Speak(
        Game game, Guid player, ObjectId card, IEnumerable<IReadOnlyList<Target>> shapes)
    {
        var name = game.State.GetObject(card).Card.Name;

        foreach (var targets in shapes)
        {
            try
            {
                game.CastSpell(player, card, targets);
                ResolvedSpells.Add(name);
                return true;
            }
            catch (InvalidOperationException refused)
                when (!refused.Message.Contains("did not settle", StringComparison.Ordinal))
            {
                // Wrong timing, no legal target, a cost that cannot be paid. The rules talking -
                // and worth counting, because the shape of the refusals is what says whether the
                // harness is being told "no" by the game or by its own arrangements.
                Refusals[Shorten(refused.Message)] =
                    Refusals.GetValueOrDefault(Shorten(refused.Message)) + 1;

                // Folded rather than shortened, so "no legal target for 'Foo'" and "... 'Bar'"
                // are one cause: card-specific names and numbers would split one finding into a
                // thousand rows of one.
                LastRefusal[name] = FoldMessage(refused.Message);
            }
        }

        return false;
    }

    /// <summary>Why the engine said no, and how often. Diagnosis, not assertion.</summary>
    private static readonly Dictionary<string, int> Refusals = new(StringComparer.Ordinal);

    /// <summary>Every spell that was successfully cast at least once, by name.</summary>
    private static readonly HashSet<string> ResolvedSpells = new(StringComparer.Ordinal);

    /// <summary>The last refusal the engine gave for each card, folded so causes group.</summary>
    private static readonly Dictionary<string, string> LastRefusal = new(StringComparer.Ordinal);

    /// <summary>Numbers become <c>#</c> and quoted text <c>~</c>, so refusals group by cause.</summary>
    private static string FoldMessage(string message)
    {
        var text = new System.Text.StringBuilder(160);
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

    /// <summary>How many times the combat retry actually got as far as re-offering a card.</summary>
    /// <remarks>
    /// Counted because for its whole life this retry reached its loop <em>zero</em> times and
    /// nothing said so: it selected its attacker on <c>HasSummoningSickness</c>, which the
    /// board's deliberately hasty creature still carries (CR 302.6), so it bailed 321 times out
    /// of 321. A soak that reaches nothing passes exactly like a soak that reaches everything,
    /// so the only way this class of defect becomes visible is to assert that the path ran.
    /// </remarks>
    private static int _combatRetriesReached;

    private static string Shorten(string message) =>
        message.Length <= 70 ? message : message[..70];

    private static ObjectId PutInHand(Game game, Guid player, CardDefinition card) =>
        game.Create(player, card, Zone.Hand);

    /// <summary>Puts a spell on the stack for something to be cast at, or null if it cannot.</summary>
    private static ObjectId? Decoy(Game game, Guid caster)
    {
        var card = game.Create(caster, new CardDefinition
        {
            OracleId = "soak-decoy",
            Name = "Soak Decoy",
            ManaCostRaw = "{1}",
            Cmc = 1,
            OracleText = "You gain 1 life.",
            CardTypes = CardType.Instant,
        }, Zone.Hand);

        foreach (var colour in Enum.GetValues<ManaColor>())
            game.AddMana(caster, colour);

        try
        {
            return game.CastSpell(caster, card, []);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Passes priority until the stack is empty, answering whatever is asked on the way.</summary>
    private static void Empty(Game game)
    {
        for (var guard = 0; guard < 400; guard++)
        {
            if (game.State.Choice is { } choice)
            {
                Decide(game, choice);
                continue;
            }

            if (game.State.Stack.IsEmpty)
                return;

            if (game.State.Priority.Holder is { } holder)
            {
                game.PassPriority(holder);
                continue;
            }

            return;
        }
    }

    private static int Activate(ImmutableList<CardDefinition> table, CompiledPool pool)
    {
        var alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var bob = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var game = Game.Start(
            Guid.NewGuid(),
            [
                new PlayerSetup(alice, "Alice", 20, Filler("Alice")),
                new PlayerSetup(bob, "Bob", 20, Filler("Bob")),
            ],
            new GameRandom(11),
            startingPlayerId: alice,
            abilities: pool);

        game.BeginPlay(withMulligans: false);

        var mine = table.Select(c => game.Create(alice, c, Zone.Battlefield)).ToList();

        // Something of the opponent's to aim at, since a good many of these want a target and a
        // board with only your own permanents on it answers half the questions.
        game.Create(bob, Filler("Bob")[0], Zone.Battlefield);

        Settle(game);

        // Wait for a main phase of Alice's *next* turn before pressing anything. Most of these
        // abilities cost a tap and most of their permanents are creatures, so on the turn they
        // arrive every one of them is refused for summoning sickness (CR 302.6) - and a test that
        // is refused everywhere passes while exercising nothing. The first run of this reached
        // 254 abilities out of 3,403 cards for exactly that reason.
        var turn = game.State.TurnNumber;

        for (var guard = 0; guard < 400; guard++)
        {
            if (game.State.TurnNumber > turn
                && game.State.ActivePlayerId == alice
                && game.State.CurrentStep == TurnStep.PrecombatMain
                && game.State.Stack.IsEmpty)
            {
                break;
            }

            if (game.State.Choice is { } waiting)
            {
                Decide(game, waiting);
                continue;
            }

            if (game.State.Priority.Holder is { } holder)
            {
                game.PassPriority(holder);
                continue;
            }

            break;
        }

        var used = 0;

        foreach (var id in mine)
        {
            if (!game.State.TryGetObject(id, out var permanent) || permanent.Permanent is null)
                continue;

            foreach (var ability in Game.ActivatedAbilitiesOf(game.State, pool, permanent))
            {
                // Generously funded: the point is to reach the ability, not to model a mana base.
                foreach (var colour in Enum.GetValues<ManaColor>())
                {
                    for (var i = 0; i < 4; i++)
                        game.AddMana(alice, colour);
                }

                if (Press(game, alice, id, ability.Id, Aims(game, alice, id)))
                    used++;

                Settle(game);
            }
        }

        Check(game, pool);
        return used;
    }

    /// <summary>
    /// Activates one ability, letting the engine's own refusals through and nothing else.
    /// </summary>
    private static bool Press(
        Game game,
        Guid player,
        ObjectId source,
        string ability,
        IEnumerable<IReadOnlyList<Target>> shapes)
    {
        foreach (var targets in shapes)
        {
            try
            {
                game.ActivateAbility(player, source, ability, targets);
                return true;
            }
            catch (InvalidOperationException refused)
                when (!refused.Message.Contains("did not settle", StringComparison.Ordinal))
            {
                // "Not now", "no legal target", "you cannot pay that". The rules talking, and
                // the next shape may be the one this ability wanted.
            }
        }

        return false;
    }

    /// <summary>
    /// The target shapes worth trying, in order.
    /// </summary>
    /// <remarks>
    /// One shape is not enough, and neither is one <em>kind</em>. "Target creature you control"
    /// and "target creature an opponent controls" each refuse the other's answer, and a spell
    /// that names an artifact refuses a creature however legal that creature is.
    /// <para>
    /// This is the harness's own arrangements rather than the rules, and getting it wrong reads
    /// exactly like the engine refusing: enriching the board with artifacts and enchantments made
    /// the count go <em>down</em>, because the first permanent an opponent controlled stopped
    /// being a creature and every "target creature" spell was then handed a relic.
    /// </para>
    /// </remarks>
    private static IEnumerable<IReadOnlyList<Target>> Aims(Game game, Guid player, ObjectId source)
    {
        yield return [];

        ObjectId Find(bool ours, CardType type) => game.State.Battlefield.FirstOrDefault(id =>
            id != source
            && game.State.GetObject(id).ControllerId == player == ours
            && game.State.GetObject(id).Card.CardTypes.HasFlag(type));

        foreach (var type in new[] { CardType.Creature, CardType.Artifact, CardType.Enchantment })
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

        // Two targets, for the spells that name a pair. Last because it is the rarest shape and
        // every earlier one is cheaper to try.
        var mine = Find(ours: true, CardType.Creature);
        var theirs = Find(ours: false, CardType.Creature);

        if (mine != default && theirs != default)
            yield return [Target.ToPermanent(mine), Target.ToPermanent(theirs)];
    }


    /// <summary>
    /// The same cards, at a table of four.
    /// </summary>
    /// <remarks>
    /// The engine was built for any number of players on purpose - the plan it was rebuilt from
    /// says the previous one died of a hardcoded <c>OpponentOf</c> and an if/else on "am I the
    /// active player". Nothing had ever checked that claim with real cards: every soak, every
    /// behaviour test and every fixture in this repository is two players.
    /// <para>
    /// Four changes what a great many cards mean. "Each opponent" is three players rather than
    /// one, triggers from different controllers have to be ordered in turn order rather than
    /// simply both fired, and the priority ladder has to come back round to the active player
    /// through two people who are not the attacker or the defender.
    /// </para>
    /// <para>
    /// A third of the corpus rather than all of it, taken evenly. Four-player games are slower
    /// and the point here is whether the shape holds, not to run the whole set twice.
    /// </para>
    /// </remarks>
    [Fact]
    public void Compiled_permanents_survive_a_four_player_table()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present - skipping.");
            return;
        }

        var pool = new CompiledPool();

        // Scattered, for the reason the two-player soaks were: batched in oracle-id order the
        // same twelve cards shared every four-player table on every run, which is the exact fault
        // this file recorded fixing everywhere else and then left standing here. A hash of the id
        // is reproducible and nothing like alphabetical, and it costs a sort key.
        var playable = corpus
            .Where(c => GoesToBattlefield(c.CardTypes))
            .Where(c => CardCompiler.Compile(c).IsComplete)
            .OrderBy(c => Scatter(c.OracleId + "four"))
            .ThenBy(c => c.OracleId, StringComparer.Ordinal)
            .Where((_, i) => i % 3 == 0)
            .ToImmutableList();

        Assert.True(playable.Count > 500, $"only {playable.Count} cards for the four-player run.");

        var faults = new List<string>();
        var played = 0;

        for (var start = 0; start < playable.Count; start += PerGame)
        {
            var table = playable.Skip(start).Take(PerGame).ToImmutableList();

            try
            {
                PlayAtFour(table, pool);
                played += table.Count;
            }
            catch (Exception broke)
            {
                faults.Add(
                    string.Join(", ", table.Select(c => c.Name))
                        + $"\n      {broke.GetType().Name}: {broke.Message}");
            }
        }

        output.WriteLine(
            $"played {played} compiled permanents at four-player tables, "
                + $"{Fired} triggers fired on {FiredOn.Count} distinct cards");

        Assert.True(
            faults.Count == 0,
            $"{faults.Count} four-player tables broke:\n  "
                + string.Join("\n  ", faults.Take(12)));
    }

    private static void PlayAtFour(ImmutableList<CardDefinition> table, CompiledPool pool)
    {
        var seats = Enumerable.Range(1, 4)
            .Select(i => Guid.Parse($"{i}{i}{i}{i}{i}{i}{i}{i}-{i}{i}{i}{i}-{i}{i}{i}{i}-{i}{i}{i}{i}-{i}{i}{i}{i}{i}{i}{i}{i}{i}{i}{i}{i}"))
            .ToList();

        var game = Game.Start(
            Guid.NewGuid(),
            [.. seats.Select((id, i) => new PlayerSetup(id, $"Seat {i}", 400, Filler($"S{i}")))],
            new GameRandom(17),
            startingPlayerId: seats[0],
            abilities: pool);

        game.BeginPlay(withMulligans: false);

        // Spread round the table, so "each opponent" has three answers and the permanents are
        // not all on one side of every question.
        for (var i = 0; i < table.Count; i++)
            game.Create(seats[i % seats.Count], table[i], Zone.Battlefield);

        Settle(game);

        var until = game.State.TurnNumber + Turns;
        var provokedOn = 0;
        var rounds = 0;

        for (var guard = 0; guard < 10_000 && game.State.TurnNumber < until; guard++)
        {
            if (game.State.IsOver)
                break;

            if (game.State.Choice is { } choice)
            {
                Decide(game, choice);
                continue;
            }

            // Provoked here as well, and this is the table where it says the most: four seats
            // each drawing, gaining life and losing a creature in the same round is four
            // controllers with simultaneous triggers, which is the APNAP ordering (CR 603.3b)
            // that four players exists to test and that nothing had ever put a real card through.
            if (ShouldProvoke(game, provokedOn, rounds))
            {
                provokedOn = game.State.TurnNumber;
                rounds++;
                Provoke(game);
                continue;
            }

            if (Fight(game))
                continue;

            if (game.State.Priority.Holder is { } holder)
            {
                game.PassPriority(holder);
                continue;
            }

            break;
        }

        Check(game, pool);
    }

    private static void Play(ImmutableList<CardDefinition> table, CompiledPool pool, int turns)
    {
        var alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var bob = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var game = Game.Start(
            Guid.NewGuid(),
            [
                // Life enough to survive ten turns of everything attacking. At twenty a player
                // is dead by the third combat and the game stops - which is realistic and
                // useless here, because the mechanics that need several turns to show anything
                // (a Saga advancing, vanishing counting down, echo coming due) never get there.
                new PlayerSetup(alice, "Alice", 400, Filler("Alice")),
                new PlayerSetup(bob, "Bob", 400, Filler("Bob")),
            ],
            new GameRandom(7),
            startingPlayerId: alice,
            abilities: pool);

        game.BeginPlay(withMulligans: false);

        // Split across both players so that "you control" and "an opponent controls" both have
        // something to find. A board where one player owns everything answers half the questions.
        for (var i = 0; i < table.Count; i++)
            game.Create(i % 2 == 0 ? alice : bob, table[i], Zone.Battlefield);

        Settle(game);

        var until = game.State.TurnNumber + turns;
        var provokedOn = 0;
        var rounds = 0;

        for (var guard = 0; guard < 6000 && game.State.TurnNumber < until; guard++)
        {
            if (game.State.IsOver)
                break;

            if (game.State.Choice is { } choice)
            {
                Decide(game, choice);
                continue;
            }

            // Once a turn, in a clean main phase, something happens. Anywhere else it would be
            // an illegal moment for the spell and a lie about when a trigger's event occurs.
            if (ShouldProvoke(game, provokedOn, rounds))
            {
                provokedOn = game.State.TurnNumber;
                rounds++;
                Provoke(game);
                continue;
            }

            if (Fight(game))
                continue;

            if (game.State.Priority.Holder is { } holder)
            {
                game.PassPriority(holder);
                continue;
            }

            break;
        }

        Check(game, pool);
    }

    /// <summary>
    /// Everything that has to be true of a finished game, whatever its cards said.
    /// </summary>
    /// <remarks>
    /// One place rather than four copies, because the four had drifted: each soak asked the
    /// layers about the battlefield and folded the log, and that was all any of them asked.
    /// <para>
    /// Three of these are new and each closes a way a card can be broken without failing
    /// anything. The layers are asked about <em>every zone</em>, because a characteristic-defining
    /// ability functions everywhere (CR 604.3) and the card whose power is defined by one spends
    /// most of a game in a graveyard or a hand. The view is projected for every player, because
    /// that is the board's whole contract and no other test has ever handed the projector a card
    /// out of the corpus. And the log is folded a second time through the door persistence
    /// actually uses - <c>GameSessionService</c> stores a game by writing its log and resumes one
    /// by reading it, so an event that does not round-trip is a game the players lose, and
    /// <c>Replay(log)</c> on the in-memory objects cannot see that.
    /// </para>
    /// </remarks>
    private static void Check(Game game, CompiledPool pool)
    {
        // A card whose static ability throws when asked is a card that cannot be looked at, let
        // alone played.
        foreach (var (_, obj) in game.State.Objects)
            Characteristics.Of(game.State, pool, obj);

        foreach (var player in game.State.TurnOrder)
            game.ViewFor(player);

        // The invariant the engine rests on, against real text.
        var folded = GameReducer.Replay(game.Log);
        Assert.True(
            game.State.Equals(folded),
            "Replay(log) does not equal the state: " + Divergence(game.State, folded));

        var transformed = CountTriggers(game);

        // Sampled rather than universal: writing and re-reading the log roughly doubles the cost
        // of a game, and a systematically unwritable event shows up in one game out of sixteen
        // just as surely as in all of them. MTG_SOAK_DEEP pays for the rest.
        //
        // Games in which something transformed used to be excluded here, because the serializer
        // did not carry a card's faces and the fold of a re-read log dropped every transform in
        // it. PrintedCard carries Faces now, so the exclusion is gone and this check sees every
        // sampled game - which is what it was written to do. 43 of 105 sampled games in the deep
        // slice were being skipped by it, so this is most of the coverage it was missing.
        if (++Stored % StorageSample == 0 || Deep)
        {
            var stored = GameReducer.Replay(
                EventLogSerializer.Read(EventLogSerializer.Write(game.Log)));

            Folded++;

            Assert.True(
                game.State.Equals(stored),
                "a stored and re-read log does not fold to the same state: "
                    + Divergence(game.State, stored));
        }

        if (transformed)
            Transformed++;
    }

    /// <summary>How many games were written out and read back in.</summary>
    private static int Folded;

    /// <summary>
    /// How many games turned a permanent over. Counted across every game, not only the sampled
    /// ones, so it is not a subset of <see cref="Folded"/>.
    /// </summary>
    /// <remarks>
    /// It used to decide which games were left out of the storage fold, because the serializer did
    /// not carry a card's faces and a re-read log dropped every transform in it. It now reports
    /// only how often the case arises - which is the number that says whether folding these games
    /// is worth anything, and it plainly is.
    /// </remarks>
    private static int Transformed;

    /// <summary>
    /// Which part of two states differs, for a divergence whose printed form is identical.
    /// </summary>
    /// <remarks>
    /// <c>Assert.Equal</c> on a <see cref="GameState"/> prints four kilobytes of type names -
    /// <c>Objects = ImmutableDictionary`2[ObjectId,GameObject]</c> - twice, and the two dumps are
    /// character-for-character the same however far apart the states are, because everything that
    /// can differ lives inside a collection that does not print itself. A run of this soak
    /// produced exactly that: a real divergence, reported as two identical walls of text, with
    /// the twelve card names that would have identified it pushed off the end.
    /// <para>
    /// So the comparison is done here instead, field by field, and the answer is one line naming
    /// the object or the player that moved. It is only ever called on a state that has already
    /// failed to compare equal, so its cost is nothing.
    /// </para>
    /// </remarks>
    private static string Divergence(GameState expected, GameState actual)
    {
        foreach (var (id, mine) in expected.Objects)
        {
            if (!actual.Objects.TryGetValue(id, out var theirs))
                return $"{mine.Card.Name} ({mine.Zone}) is not in the replayed state at all";

            if (!mine.Equals(theirs))
                return $"{mine.Card.Name}: {Short(mine.ToString())}  ||  {Short(theirs.ToString())}";
        }

        foreach (var (id, theirs) in actual.Objects)
        {
            if (!expected.Objects.ContainsKey(id))
                return $"the replay has an extra object: {theirs.Card.Name} ({theirs.Zone})";
        }

        foreach (var (id, mine) in expected.Players)
        {
            if (!actual.Players.TryGetValue(id, out var theirs))
                return $"player {mine.Name} is not in the replayed state";

            if (!mine.Equals(theirs))
                return $"player {mine.Name}: {Short(mine.ToString())}  ||  {Short(theirs.ToString())}";
        }

        foreach (var (what, mine, theirs) in Ordered(expected, actual))
        {
            if (!mine.SequenceEqual(theirs))
                return $"{what} differs: [{string.Join(", ", mine)}]  ||  [{string.Join(", ", theirs)}]";
        }

        if (!expected.ArmedStateTriggers.SetEquals(actual.ArmedStateTriggers))
            return "the armed state triggers differ";

        if (!expected.Combat.Equals(actual.Combat))
            return $"combat differs: {Short(expected.Combat.ToString())}  ||  {Short(actual.Combat.ToString())}";

        if (!Equals(expected.Choice, actual.Choice))
            return $"the pending choice differs: {expected.Choice?.Prompt} || {actual.Choice?.Prompt}";

        return "in a field this comparison does not cover - widen it, that is what it is for";
    }

    /// <summary>The ordered collections on a state, which compare by sequence rather than by reference.</summary>
    private static IEnumerable<(string What, IEnumerable<object> Mine, IEnumerable<object> Theirs)>
        Ordered(GameState expected, GameState actual)
    {
        yield return ("battlefield", expected.Battlefield.Cast<object>(), actual.Battlefield.Cast<object>());
        yield return ("stack", expected.Stack.Cast<object>(), actual.Stack.Cast<object>());
        yield return ("exile", expected.Exile.Cast<object>(), actual.Exile.Cast<object>());
        yield return ("command zone", expected.Command.Cast<object>(), actual.Command.Cast<object>());
        yield return ("turn order", expected.TurnOrder.Cast<object>(), actual.TurnOrder.Cast<object>());
        yield return ("extra turns", expected.ExtraTurns.Cast<object>(), actual.ExtraTurns.Cast<object>());
        yield return ("pending triggers", expected.PendingTriggers.Cast<object>(), actual.PendingTriggers.Cast<object>());
        yield return ("delayed triggers", expected.Delayed.Cast<object>(), actual.Delayed.Cast<object>());
        yield return ("floating effects", expected.FloatingEffects.Cast<object>(), actual.FloatingEffects.Cast<object>());
        yield return ("arrivals this turn", expected.ArrivalsThisTurn.Cast<object>(), actual.ArrivalsThisTurn.Cast<object>());
        yield return ("departures this turn", expected.DeparturesThisTurn.Cast<object>(), actual.DeparturesThisTurn.Cast<object>());
    }

    private static string Short(string text) =>
        text.Length <= 400 ? text : text[..400];

    /// <summary>How many games have finished, so one in sixteen can be folded through storage.</summary>
    private static int Stored;

    /// <summary>
    /// How many triggered abilities actually fired, and on how many distinct cards.
    /// </summary>
    /// <remarks>
    /// The number this file most needed and did not have. A trigger's condition is a closure,
    /// exactly like an activated ability's cost - and where the activated soak counts how many
    /// buttons it managed to press, nothing counted how many triggers ever went off. A card whose
    /// only behaviour is a trigger that never fires is compiled, played, asserted about, and
    /// completely unverified.
    /// </remarks>
    private static int Fired;

    private static readonly HashSet<string> FiredOn = new(StringComparer.Ordinal);

    /// <summary>
    /// Walks a finished log for triggers, naming the card each one came off.
    /// </summary>
    /// <remarks>
    /// The source is an <c>ObjectId</c> and an object gets a new one every time it changes zone
    /// (CR 400.7), so the card is carried forward across moves rather than looked up in the final
    /// state - by the end of the game a great many of the sources are in a graveyard under an id
    /// nothing on the battlefield has. Cards dealt at the start are not in this map and do not
    /// need to be: they are the filler, and the filler is a vanilla bear.
    /// </remarks>
    /// <returns>Whether anything in this game turned over, which the storage fold cannot survive.</returns>
    private static bool CountTriggers(Game game)
    {
        var owners = new Dictionary<ObjectId, string>();
        var turnedOver = false;

        foreach (var e in game.Log)
        {
            switch (e)
            {
                case ObjectCreated made:
                    owners[made.Id] = made.Card.Name;
                    break;

                case ObjectMoved moved when owners.TryGetValue(moved.OldId, out var carried):
                    owners[moved.NewId] = carried;
                    break;

                case AbilityTriggered fired:
                    Fired++;
                    if (owners.TryGetValue(fired.SourceId, out var source))
                        FiredOn.Add(source);
                    break;

                case PermanentTransformed:
                    turnedOver = true;
                    break;

                default:
                    break;
            }
        }

        return turnedOver;
    }

    /// <summary>
    /// Whether this is a legal and useful moment to make something happen.
    /// </summary>
    /// <remarks>
    /// A clean precombat main phase held by the active player, which is the one moment where the
    /// spell below can legally be cast. It is also the only moment where the direct emissions are
    /// honest: a trigger reads the state the event happened in, and firing a battery in the middle
    /// of combat would be telling every one of them something untrue about when it went off.
    /// </remarks>
    private static bool ShouldProvoke(Game game, int provokedOn, int rounds) =>
        !SkipProvocation
        && rounds < ProvokeTurns
        && game.State.TurnNumber != provokedOn
        && game.State.CurrentStep == TurnStep.PrecombatMain
        && game.State.Stack.IsEmpty
        && game.State.Priority.Holder == game.State.ActivePlayerId;

    /// <summary>
    /// Makes the things happen that triggers watch for.
    /// </summary>
    /// <remarks>
    /// Ten turns of passing priority is ten turns in which almost nothing happens. The board is
    /// built once and never changes, so after the arrival every "whenever another creature
    /// enters", "whenever a creature dies", "whenever you draw a card", "whenever you gain life",
    /// "landfall", "whenever you cast", "whenever this becomes tapped" and every counter or
    /// damage trigger in the corpus watches a game in which its event never occurs. Combat was
    /// added to this file for exactly that reason and closed exactly one of those families.
    /// <para>
    /// So each turn something happens, on a disposable object rather than on the table. That
    /// matters: marking damage on the cards being tested would kill them, and the deep slice
    /// exists so that a Saga has ten turns to finish and a fading permanent to run out. The bear
    /// and the land below arrive, are counted and tapped and damaged, and leave again, while the
    /// twelve cards under test stand and watch - which is what a trigger does.
    /// </para>
    /// <para>
    /// Drawing and milling are gated on a library deep enough to survive it. Without that the
    /// filler runs out around turn eight, a player loses to CR 704.5b, and the game ends early -
    /// which would have quietly shortened the one pass that is long enough to matter.
    /// </para>
    /// </remarks>
    private static void Provoke(Game game)
    {
        foreach (var player in game.State.TurnOrder)
        {
            var seat = game.State.GetPlayer(player);

            if (seat.Library.Count > 12)
            {
                game.Draw(player);
                game.Move(game.State.GetPlayer(player).Library[0], Zone.Graveyard, MoveCause.Mill);
            }

            // Gained and then lost, so both halves of the commonest pair of life triggers fire
            // and the total is where it started.
            game.ChangeLife(player, 3);
            game.ChangeLife(player, -3);

            if (game.State.GetPlayer(player).Hand is [var held, ..])
                game.Discard(player, held);

            if (game.State.GetPlayer(player).Graveyard is [var buried, ..])
                game.Move(buried, Zone.Exile, MoveCause.Exile);

            // A creature that enters, is counted, taps, takes damage and dies - five families of
            // trigger on one throwaway body.
            var bear = game.Create(player, ProvokeBear, Zone.Battlefield);

            if (game.State.TryGetObject(bear, out var arrived) && arrived.Permanent is { } body)
            {
                game.ChangeCounters(bear, "+1/+1", 1);

                if (!body.IsTapped)
                    game.Tap(bear);

                game.MarkDamage(bear, 1);

                // Damage to a player that is not combat damage: a different question from the
                // one the attack step asks, and one a great many triggers are written against.
                // Dealt by the bear, because a source of no object is not a source a trigger can
                // ask anything about.
                game.MarkDamageToPlayer(player, bear, 1, isCombat: false);

                game.Move(bear, Zone.Graveyard, MoveCause.Destroy);
            }

            // A land entering, which is landfall and every other "whenever a land enters" - and
            // then sacrificed, so the board does not silently grow by a land a turn per seat.
            var land = game.Create(player, ProvokeLand, Zone.Battlefield);
            if (game.State.TryGetObject(land, out _))
                game.Move(land, Zone.Graveyard, MoveCause.Sacrifice);
        }

        // And a spell cast, from the player who holds priority - the only one who may. That is
        // "whenever you cast", "whenever an opponent casts", storm, prowess and magecraft, none
        // of which a game of pure priority-passing ever shows.
        Decoy(game, game.State.ActivePlayerId);
    }

    /// <summary>The body every provoked turn throws away. One definition, so the pool keeps one entry.</summary>
    private static readonly CardDefinition ProvokeBear = new()
    {
        OracleId = "soak-provoke-bear",
        Name = "Soak Provocation Bear",
        CardTypes = CardType.Creature,
        Power = 2,
        Toughness = 2,
    };

    private static readonly CardDefinition ProvokeLand = new()
    {
        OracleId = "soak-provoke-land",
        Name = "Soak Provocation Waste",
        CardTypes = CardType.Land,
    };

    /// <summary>
    /// Attacks with everything and blocks with everything, so that combat actually happens.
    /// </summary>
    /// <remarks>
    /// Passing priority through the declare steps declares <em>nothing</em>: attacking is a
    /// turn-based action the active player has to take, so a soak that only passes plays four
    /// turns in which no creature ever attacks and no combat trigger ever fires. Attack triggers,
    /// block restrictions, combat damage, first strike, deathtouch and trample were all outside
    /// what these games touched.
    /// <para>
    /// Everything attacks and everything blocks, and where that is illegal - a creature with
    /// defender, a restriction that forbids the whole batch - the engine refuses and the batch is
    /// narrowed to one, then to none. A refusal is the rules working, so it is caught and the
    /// step passes on rather than failing the run.
    /// </para>
    /// </remarks>
    /// <summary>How much fighting actually happened. Counted, because a harness that reaches
    /// nothing passes exactly like one that reaches everything.</summary>
    private static int Attacks;

    private static int Blocks;

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

                    Attacks += batch.Count;
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

            // Gang up on the first attacker when there is somebody spare. One blocker each never
            // exercises damage assignment order, which is the part of combat with a choice in it.
            if (attackers.Count > 0 && able.Count > attackers.Count)
                blocks[attackers[0]] = [able[0], able[attackers.Count]];

            foreach (var batch in new[]
            {
                blocks,
                new Dictionary<ObjectId, IReadOnlyList<ObjectId>>(),
            })
            {
                try
                {
                    game.DeclareBlockers(defending, batch);
                    Blocks += batch.Count;
                    return true;
                }
                catch (InvalidOperationException refused)
                    when (!refused.Message.Contains("did not settle", StringComparison.Ordinal))
                {
                    // A block that is not legal - menace, an evasion keyword, a restriction.
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

    /// <summary>
    /// Answers one question, trying the shapes an answer can take until one is legal.
    /// </summary>
    /// <remarks>
    /// Dividing combat damage is the question that cannot be answered by a rule of thumb. CR
    /// 510.1c will not let a second blocker take damage until the first has lethal, and the
    /// harness does not know what lethal is - so one each in turn is illegal, and all on the
    /// first is <em>also</em> illegal, being more than lethal while the other has none.
    /// <para>
    /// Rather than teach the harness to compute lethal, the splits are offered in turn and the
    /// engine picks: it already knows, and it is the thing being tested. Getting this wrong in
    /// two different directions cost two runs and 94 broken tables, both of them the rules being
    /// right.
    /// </para>
    /// </remarks>
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
        // genuinely cannot answer is a failure rather than a silent skip.
        game.Choose(choice.PlayerId, Answer(choice));
    }

    private static IEnumerable<IReadOnlyList<string>> Answers(PendingChoice choice)
    {
        yield return Answer(choice);

        if (choice.Options.Count == 0)
            yield break;

        var wanted = Math.Max(choice.MinPicks, 1);

        // Every split of the picks between the first option and the rest. One of them gives the
        // first blocker exactly lethal, which is the only legal shape when there are two.
        //
        // Down to none: a blocker that already carries lethal damage - from first strike, or from
        // anything earlier in the turn - may legally be assigned nothing at all, and a loop that
        // stopped at one could not say so.
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

        // Three blockers need a three-way split, and the shapes above cannot express one:
        // lethal to the first, lethal to the second, the rest to the third. Every division of the
        // picks among the options is offered, front-loaded first, because the legal one gives the
        // earlier blockers exactly lethal and the leftovers to the last.
        //
        // Bounded rather than open-ended: this is a search for a legal answer, not a proof that
        // one exists, and an unbounded one would hang a soak instead of failing it.
        if (choice.Options.Count is < 2 or > 5 || wanted > 12)
            yield break;

        foreach (var split in Divisions(wanted, choice.Options.Count))
        {
            var picks = new List<string>(wanted);
            for (var option = 0; option < split.Length; option++)
            {
                for (var i = 0; i < split[option]; i++)
                    picks.Add(choice.Options[option].Id);
            }

            yield return picks;
        }
    }

    /// <summary>Every way of dividing <paramref name="total"/> among <paramref name="parts"/>.</summary>
    private static IEnumerable<int[]> Divisions(int total, int parts)
    {
        if (parts == 1)
        {
            yield return [total];
            yield break;
        }

        for (var first = total; first >= 0; first--)
        {
            foreach (var rest in Divisions(total - first, parts - 1))
                yield return [first, .. rest];
        }
    }

    /// <summary>
    /// The first answer that satisfies a choice, whatever shape it is.
    /// </summary>
    /// <remarks>
    /// Taking one option is wrong whenever the question wants more than one - an ordering wants
    /// all of them, "distribute two counters" wants two - and the engine says so rather than
    /// guessing, which is right of it and was the first thing this harness got wrong.
    /// </remarks>
    private static IReadOnlyList<string> Answer(PendingChoice choice)
    {
        if (choice.Options.Count == 0)
            return [];

        // Some questions want more picks than there are options, and mean it: dividing four
        // combat damage between two blockers is four picks from two answers. Taking each option
        // once gave two, and the engine rightly said "pick between 4 and 4; got 2" - which looked
        // like a broken choice and was a harness that could not count.
        var wanted = Math.Max(choice.MinPicks, 1);
        var picks = new List<string>(wanted);

        for (var i = 0; i < wanted; i++)
            picks.Add(choice.Options[i % choice.Options.Count].Id);

        return picks;
    }

    private static ImmutableList<CardDefinition> Filler(string who) =>
        [.. Enumerable.Range(0, 40).Select(i => new CardDefinition
        {
            OracleId = $"soak-{who}-{i}",
            Name = $"Soak Bear {who} {i}",
            CardTypes = CardType.Creature,
            Power = 2,
            Toughness = 2,
        })];

    /// <summary>
    /// A stable scramble of an oracle id, so that batches are varied but reproducible.
    /// </summary>
    /// <remarks>
    /// FNV-1a rather than <c>GetHashCode</c>, which is randomised per process: a soak that
    /// grouped its cards differently on every run would report a different set of interactions
    /// each time, and a failure nobody could reproduce is barely a failure at all.
    /// </remarks>
    private static uint Scatter(string oracleId)
    {
        unchecked
        {
            var hash = 2166136261u;

            foreach (var c in oracleId)
            {
                hash ^= c;
                hash *= 16777619u;
            }

            return hash;
        }
    }

    private static bool IsPermanent(CardType types) =>
        (types & (CardType.Creature | CardType.Artifact | CardType.Enchantment
            | CardType.Planeswalker)) != 0
        && !types.HasFlag(CardType.Token);


    /// <summary>
    /// A land and nothing else - the set the land soak plays (CR 305.1).
    /// </summary>
    /// <remarks>
    /// Deliberately narrower than "has the land type". A card that is also a creature or an
    /// artifact is already played by the permanent soak, and a <c>Sorcery // Land</c> is already
    /// cast by the spell soak; what is left is the population no soak had ever selected, which is
    /// the set this predicate exists to name. Written as an exclusion of the other two predicates
    /// rather than as a list of its own, so the three cannot overlap and cannot leave a gap - and
    /// <c>CardPlayabilityTests</c> restates all three and asserts they partition the corpus.
    /// </remarks>
    private static bool IsLand(CardType types) =>
        types.HasFlag(CardType.Land)
        && !IsPermanent(types)
        && !types.HasFlag(CardType.Instant)
        && !types.HasFlag(CardType.Sorcery)
        && !types.HasFlag(CardType.Token);
    /// <summary>
    /// Everything that can be put onto a battlefield, which includes lands (CR 110.4a).
    /// </summary>
    /// <remarks>
    /// <see cref="IsPermanent"/> leaves lands out and nothing else picked them up - they are not
    /// instants or sorceries either - so **every fully-compiled land in the corpus was in none of
    /// these tests**. That is 759 cards, the type a real game plays more of than any other, and
    /// the one carrying most of the mana abilities the compiler builds. They were compiled,
    /// counted, swept, and never put anywhere.
    /// <para>
    /// Kept as a second predicate rather than folded into <see cref="IsPermanent"/> because the
    /// spell soak uses that one as a *negation* - "not a permanent, and an instant or a sorcery" -
    /// and a land counted as a permanent there would have thrown the <c>Sorcery // Land</c> cards
    /// out of the only test that casts them.
    /// </para>
    /// </remarks>
    private static bool GoesToBattlefield(CardType types) =>
        (IsPermanent(types) || types.HasFlag(CardType.Land))
        && !types.HasFlag(CardType.Token);
}
