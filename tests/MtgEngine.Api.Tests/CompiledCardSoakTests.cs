using System.Collections.Immutable;
using System.Globalization;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.Cards;
using MtgEngine.Rules.Engine;
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
/// It asserts the three things that must hold for <em>every</em> card, whatever it says:
/// nothing throws, the layers can compute characteristics for everything on the battlefield, and
/// <c>Replay(log)</c> still equals the state. That last is the invariant the whole engine rests
/// on, and it is checked here against real text rather than against fixtures chosen to be easy.
/// </para>
/// </remarks>
public sealed class CompiledCardSoakTests(ITestOutputHelper output)
{
    /// <summary>How many permanents share a battlefield. Enough to interact, few enough to read.</summary>
    private const int PerGame = 12;

    /// <summary>How many turns each game runs before it is called settled.</summary>
    private const int Turns = 10;

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
            .Where(c => IsPermanent(c.CardTypes))
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
                    $"{broke.GetType().Name}: {broke.Message}\n      "
                        + string.Join(", ", table.Select(c => c.Name)));
            }
        }

        output.WriteLine(
            $"played {played} compiled permanents across {playable.Count / PerGame} games, "
                + $"with {Attacks} attacks and {Blocks} blocks declared");

        // Combat is the half of a game that passing priority never reaches: attacking is a
        // turn-based action somebody has to take. Counted so that a soak which stops fighting
        // fails rather than going quietly green.
        // Accumulated across the slices, which is what the floor is written against: the point
        // is that combat is happening somewhere in the run, not in any one of them.
        Assert.True(
            Attacks > 500 && Blocks > 100,
            $"only {Attacks} attacks and {Blocks} blocks - combat is not being reached.");

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
            .Where(c => IsPermanent(c.CardTypes))
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
                    $"{broke.GetType().Name}: {broke.Message}\n      "
                        + string.Join(", ", table.Select(c => c.Name)));
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
                    $"{broke.GetType().Name}: {broke.Message}\n      "
                        + string.Join(", ", hand.Select(c => c.Name)));
            }
        }

        output.WriteLine($"cast {resolved} spells of {spells.Count}");

        foreach (var (why, n) in Refusals.OrderByDescending(p => p.Value).Take(10))
            output.WriteLine($"  refused {n,5}  {why}");

        Assert.True(
            faults.Count == 0,
            $"{faults.Count} hands broke when their spells were cast:\n  "
                + string.Join("\n  ", faults.Take(12)));

        Assert.True(
            resolved > spells.Count / 3,
            $"only {resolved} of {spells.Count} spells could be cast at all - the harness has "
                + "stopped reaching them, whatever the rest of this test says.");
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

        foreach (var id in game.State.Battlefield)
            Characteristics.Of(game.State, pool, game.State.GetObject(id));

        Assert.Equal(game.State, GameReducer.Replay(game.Log));
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

        var attacker = game.State.Battlefield.FirstOrDefault(id =>
            game.State.GetObject(id).ControllerId == alice
            && game.State.GetObject(id).Card.CardTypes.HasFlag(CardType.Creature)
            && game.State.GetObject(id).Permanent?.HasSummoningSickness == false);

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
        foreach (var targets in shapes)
        {
            try
            {
                game.CastSpell(player, card, targets);
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
            }
        }

        return false;
    }

    /// <summary>Why the engine said no, and how often. Diagnosis, not assertion.</summary>
    private static readonly Dictionary<string, int> Refusals = new(StringComparer.Ordinal);

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

        foreach (var id in game.State.Battlefield)
            Characteristics.Of(game.State, pool, game.State.GetObject(id));

        Assert.Equal(game.State, GameReducer.Replay(game.Log));
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

        var playable = corpus
            .Where(c => IsPermanent(c.CardTypes))
            .Where(c => CardCompiler.Compile(c).IsComplete)
            .OrderBy(c => c.OracleId, StringComparer.Ordinal)
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
                    $"{broke.GetType().Name}: {broke.Message}\n      "
                        + string.Join(", ", table.Select(c => c.Name)));
            }
        }

        output.WriteLine($"played {played} compiled permanents at four-player tables");

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

        for (var guard = 0; guard < 8000 && game.State.TurnNumber < until; guard++)
        {
            if (game.State.IsOver)
                break;

            if (game.State.Choice is { } choice)
            {
                Decide(game, choice);
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

        foreach (var id in game.State.Battlefield)
            Characteristics.Of(game.State, pool, game.State.GetObject(id));

        Assert.Equal(game.State, GameReducer.Replay(game.Log));
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

        for (var guard = 0; guard < 4000 && game.State.TurnNumber < until; guard++)
        {
            if (game.State.IsOver)
                break;

            if (game.State.Choice is { } choice)
            {
                Decide(game, choice);
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

        // The layers have to be able to answer for everything still standing. A card whose
        // static ability throws when asked is a card that cannot be looked at, let alone played.
        foreach (var id in game.State.Battlefield)
            Characteristics.Of(game.State, pool, game.State.GetObject(id));

        // And the invariant the engine rests on, against real text.
        Assert.Equal(game.State, GameReducer.Replay(game.Log));
    }

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
}
