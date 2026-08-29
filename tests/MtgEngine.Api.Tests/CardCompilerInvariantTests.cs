using System.Collections.Immutable;
using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using Xunit.Abstractions;

namespace MtgEngine.Api.Tests;

/// <summary>
/// Structural checks on every card the compiler claims to understand.
/// </summary>
/// <remarks>
/// Coverage says a line was <em>read</em>; the behaviour tests say a template <em>plays</em>. This
/// is the third question, and it is the one that scales: given 32,765 cards and a few dozen
/// behaviour tests, most compiled cards are never played by anything. These assert the properties
/// that must hold for <em>all</em> of them, so a template that quietly produces a malformed card
/// on some other card's wording is caught by the corpus rather than by a player.
/// <para>
/// It exists because of a specific failure. "This land enters tapped" compiled perfectly and every
/// such land arrived untapped for months, because the replacement was pinned to the zone a
/// <em>spell</em> is in and a land is played rather than cast (CR 305.1). Nothing noticed: the
/// coverage test asks whether a line parsed, and no behaviour test happened to play a land.
/// </para>
/// </remarks>
public sealed class CardCompilerInvariantTests(ITestOutputHelper output)
{
    [Fact]
    public void Every_compiled_card_is_structurally_sound()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present — skipping.");
            return;
        }

        var faults = new List<string>();
        var checkedCards = 0;

        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);

            // A card the compiler refuses is not a claim about anything, so it has nothing to be
            // sound about. Only what it says it understands is held to these.
            if (!compiled.IsComplete)
                continue;

            checkedCards++;
            Check(card, compiled, faults);
        }

        output.WriteLine($"checked {checkedCards} fully compiled cards");
        foreach (var fault in faults.Take(25))
            output.WriteLine("  " + fault);

        Assert.True(
            faults.Count == 0,
            $"{faults.Count} compiled cards are malformed:\n  " + string.Join("\n  ", faults.Take(25)));
    }

    /// <summary>
    /// Every effect aimed at an "any target" spec has to have an answer for a player (CR 115.4).
    /// </summary>
    /// <remarks>
    /// An effect that pattern-matches on <see cref="TargetKind"/> returns nothing for the kinds
    /// it does not name, and nothing is indistinguishable from working: the card compiles, the
    /// spell is legal to aim at a player, it resolves, an event log is written and the life
    /// total never moves. <c>PreventDamage</c> was exactly that for as long as it existed - the
    /// permanent arm was written and tested, and nobody asked what the same effect did with the
    /// other kind of target its own grammar admits.
    /// <para>
    /// The allowlist is the point. A new effect reaching an "any target" phrase fails here until
    /// somebody says which arm answers for a player, which is a question that otherwise only
    /// gets asked by a player wondering why their burn spell did nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_effect_aimed_at_any_target_answers_for_a_player()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
            return;

        var seen = new SortedDictionary<string, int>(StringComparer.Ordinal);

        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);
            if (!compiled.IsComplete)
                continue;

            foreach (var (effects, targets) in Slices(compiled))
            {
                foreach (var effect in effects)
                {
                    if (!EffectTargets.ReadsATarget(effect)
                        || EffectTargets.IndexOf(effect) is not { } i
                        || i < 0
                        || i >= targets.Count)
                    {
                        continue;
                    }

                    if (targets[i].Kind != TargetKind.Any)
                        continue;

                    var key = effect.GetType().Name;
                    seen[key] = seen.GetValueOrDefault(key) + 1;
                }
            }
        }

        // Each of these has been read and has a deliberate answer for a player.
        var answered = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Damage to a player is its own event, and both of these emit it.
            ["DealDamage"] = "emits PlayerDamaged for a player target",
            ["DealDividedDamage"] = "divides across players and permanents alike",

            // The shield has a player half - PlayerState.DamageToPrevent - and the replacement
            // soaks PlayerDamaged with it.
            ["PreventDamage"] = "emits PlayerPreventionChanged for a player target",

            // Nothing, and correctly: this is the "if a creature dealt damage this way would
            // die, exile it" rider on a burn spell. Aimed at a player there is no creature for
            // it to be about, so doing nothing is the whole answer.
            ["ExileInsteadOfDying"] = "no creature to exile when a player was chosen",
        };

        foreach (var (name, count) in seen.OrderByDescending(kv => kv.Value))
            output.WriteLine($"{count,6}  {name}");

        var unanswered = seen.Keys.Where(k => !answered.ContainsKey(k)).ToList();

        Assert.True(
            unanswered.Count == 0,
            "these effects are aimed at \"any target\" and have no recorded answer for a player "
                + "- read each one's Resolve, and either give it a player arm or add it to the "
                + "list with the reason doing nothing is right: "
                + string.Join(", ", unanswered));
    }

    private static IEnumerable<(ImmutableList<IEffect> Effects, IReadOnlyList<TargetSpec> Targets)>
        Slices(CompiledCard compiled)
    {
        if (compiled.Spell is { } spell)
        {
            yield return (spell.Effects, spell.Targets);
            foreach (var mode in spell.Modes)
                yield return (mode.Effects, mode.Targets);
        }

        foreach (var ability in compiled.Activated)
            yield return (ability.Effects, ability.Targets);

        foreach (var trigger in compiled.Triggers)
        {
            yield return (trigger.Effects, trigger.Targets);
            foreach (var mode in trigger.Modes)
                yield return (mode.Effects, mode.Targets);
        }
    }

    private static void Check(
        MtgEngine.Domain.Models.CardDefinition card, CompiledCard compiled, List<string> faults)
    {
        var name = card.Name;

        // ---- Ability ids have to be unique within a card ----
        //
        // The board addresses an ability by id and the engine looks it up by id, so a duplicate
        // means one of them is unreachable and the other answers for both.
        var ids = compiled.Activated.Select(a => a.Id).ToList();
        if (ids.Count != ids.Distinct(StringComparer.Ordinal).Count())
            faults.Add($"{name}: duplicate activated ability id");

        var triggerIds = compiled.Triggers.Select(t => t.Id).ToList();
        if (triggerIds.Count != triggerIds.Distinct(StringComparer.Ordinal).Count())
            faults.Add($"{name}: duplicate triggered ability id");

        // ---- A mana ability has to make mana ----
        //
        // It is the one ability whose whole purpose is in a single field, and an empty one is
        // silent: the card compiles, reads as complete, offers the player a button, and taps for
        // nothing. That is not hypothetical - the mana phrase used to run to the end of the line,
        // so "Add {C}{C}. This land doesn't untap during your next untap step" was handed to the
        // symbol reader whole and came back with no symbols it recognised.
        foreach (var barren in compiled.Activated.Where(
            a => a.IsManaAbility && a.Produces.Count == 0))
        {
            faults.Add($"{name}: mana ability '{barren.Id}' produces no mana");
        }

        // ---- Every effect's target index has to exist ----
        //
        // An effect indexes into its ability's target list, and an index past the end silently
        // does nothing at all. All the index arithmetic — conjunction splits, modal slices —
        // makes this the likeliest way for a template to go quietly wrong.
        if (compiled.Spell is { } spell)
        {
            CheckIndices($"{name} (spell)", spell.Effects, spell.Targets.Count, faults);
            CheckTargetsAreUsed(
                $"{name} (spell)",
                spell.Effects,
                spell.Targets.Count,
                card.Subtypes.Contains("Aura", StringComparer.OrdinalIgnoreCase),
                faults);

            foreach (var (mode, i) in spell.Modes.Select((m, i) => (m, i)))
            {
                CheckIndices($"{name} (mode {i})", mode.Effects, mode.Targets.Count, faults);
                CheckTargetsAreUsed(
                    $"{name} (mode {i})",
                    mode.Effects,
                    mode.Targets.Count,
                    isAura: false,
                    faults);
            }

            // CR 700.2d: a modal spell has to offer at least as many modes as it makes you take.
            if (spell.ModesToChoose > spell.Modes.Count)
                faults.Add($"{name}: asks for {spell.ModesToChoose} modes but offers {spell.Modes.Count}");

            if (spell.Modes.Count > 0 && spell.ModesToChoose == 0)
                faults.Add($"{name}: has modes but chooses none");
        }

        foreach (var trigger in compiled.Triggers)
        {
            // CR 700.2d, asked of an ability rather than a spell: a menu with fewer dishes than
            // it makes you order is not a menu. "When ~ enters, choose one —" with no bullets
            // under it compiles to exactly that, and the header alone reads as complete.
            if (trigger.ModesToChoose > trigger.Modes.Count)
            {
                faults.Add($"{name}: trigger '{trigger.Id}' asks for {trigger.ModesToChoose} "
                    + $"modes but offers {trigger.Modes.Count}");
            }

            if (trigger.Modes.Count > 0 && trigger.ModesToChoose == 0)
                faults.Add($"{name}: trigger '{trigger.Id}' has modes but chooses none");
        }

        foreach (var ability in compiled.Activated)
        {
            CheckIndices($"{name} ({ability.Id})", ability.Effects, ability.Targets.Count, faults);
            CheckTargetsAreUsed(
                $"{name} ({ability.Id})",
                ability.Effects,
                ability.Targets.Count,
                isAura: false,
                faults);

            // CR 605.1a: a mana ability is one that could add mana and takes no target. An
            // ability that produces mana *and* targets is an ordinary ability and must use the
            // stack — if the flag said otherwise it would resolve where nobody could respond.
            if (!ability.Produces.IsEmpty && !ability.Targets.IsEmpty && ability.IsManaAbility)
                faults.Add($"{name} ({ability.Id}): targeting mana ability (CR 605.1a)");

            // An ability that works from a graveyard has to take the card out of it, or it can
            // be activated again and again from a zone nothing removes it from. The card may
            // leave as part of the cost — exiling itself, the way a flashback-style ability does
            // — or as part of the effect, the way unearth returns it to the battlefield. Either
            // satisfies this; neither is the fault.
            if (ability.FunctionsFrom == Zone.Graveyard
                && ability.SelfCost == SelfCost.None
                && !ability.Effects.Any(MovesTheSource))
            {
                faults.Add($"{name} ({ability.Id}): repeatable from the graveyard");
            }
        }

        foreach (var trigger in compiled.Triggers)
        {
            CheckIndices($"{name} ({trigger.Id})", trigger.Effects, trigger.Targets.Count, faults);
            CheckTargetsAreUsed(
                $"{name} ({trigger.Id})",
                trigger.Effects,
                trigger.Targets.Count,
                isAura: false,
                faults);
        }

        // ---- A replacement about entering the battlefield must not be pinned to the stack ----
        //
        // The bug this whole file exists for. A land is played, not cast (CR 305.1), so it is
        // never on the stack — a replacement pinned there never applies to one.
        foreach (var replacement in compiled.Replacements)
        {
            if (replacement.FunctionsFrom == Zone.Stack
                && card.CardTypes.HasFlag(MtgEngine.Domain.Enums.CardType.Land))
                faults.Add($"{name}: replacement pinned to the stack on a land (CR 305.1)");
        }
    }

    /// <summary>Whether an effect takes the source out of the zone it is being used from.</summary>
    private static bool MovesTheSource(IEffect effect) =>
        effect is UnearthSource
            or ReturnSourceToBattlefield
            or ReturnSourceToHand
            or ReturnSourceFromGraveyard
            or ReturnSourceFromBattlefield
            or SacrificeSource;

    /// <summary>Every effect an ability runs, including the ones nested inside others.</summary>
    /// <remarks>
    /// A branch holds its own list — "you may pay {2}. If you do, draw a card" is one effect
    /// carrying two lists — and an effect inside one aims at the ability's targets like any
    /// other. Walking only the top level would call a target unused because the only thing that
    /// uses it is one level down.
    /// </remarks>
    /// <summary>Every effect in the tree, parents before their children.</summary>
    /// <remarks>
    /// The engine's own flattener rather than a copy. This was a hand-written switch over the
    /// wrapper types — <c>MayPay</c>, <c>OnlyIf</c>, <c>OnAttached</c>, <c>IfKicked</c> — and a
    /// wrapper missing from it was invisible: its children were never walked, so their targets
    /// looked unused and the card was reported malformed. A fifth wrapper was added and eighteen
    /// perfectly sound cards were accused of choosing a target and never using it.
    /// <para>
    /// The engine's version finds nested effects by reflecting over properties, so it covers a
    /// wrapper the moment one exists. It is the same lesson as the spell-definition guard, applied
    /// to the checks rather than to the code they check: a list somebody has to remember to extend
    /// is a list that will be wrong.
    /// </para>
    /// </remarks>
    private static IEnumerable<IEffect> Flatten(IEnumerable<IEffect> effects) =>
        EffectTree.Flatten(effects);

    /// <summary>
    /// Every target the ability asks for is used by something (CR 601.2c).
    /// </summary>
    /// <remarks>
    /// A target nothing reads is a card that stops the game, makes a player choose a creature,
    /// and then does nothing to it. It compiles, it is legal, and it plays as a blank — the same
    /// silence as a filter that names nothing, arrived at from the other direction.
    /// </remarks>
    private static void CheckTargetsAreUsed(
        string what,
        ImmutableList<IEffect> effects,
        int targetCount,
        bool isAura,
        List<string> faults)
    {
        // An Aura's target is read by the attaching and not by any effect: "enchant creature"
        // makes the spell target, and what it does with that target is become attached to it
        // (CR 303.4). There is no effect to find because the engine does it as the spell
        // resolves, so an Aura's target being unread is the correct answer and not a fault.
        if (targetCount == 0 || isAura)
            return;

        var used = Flatten(effects).SelectMany(EffectTargets.IndicesOf).ToHashSet();

        for (var i = 0; i < targetCount; i++)
        {
            if (!used.Contains(i))
                faults.Add($"{what}: target {i} is chosen and never used");
        }
    }

    private static void CheckIndices(
        string what, ImmutableList<IEffect> effects, int targetCount, List<string> faults)
    {
        foreach (var effect in effects)
        {
            // An effect aimed at something other than a target never reads its index, so an
            // index out of range says nothing about it. Exalted is the case: "that creature" is
            // the one that attacked, and the ability has no targets at all.
            if (!EffectTargets.ReadsATarget(effect))
                continue;

            if (EffectTargets.IndexOf(effect) is not { } index)
                continue;

            if (index < 0 || index >= targetCount)
                faults.Add($"{what}: effect targets #{index} of {targetCount}");
        }
    }

    /// <summary>
    /// Every effect type that aims at a target has to be known to <see cref="EffectTargets"/>.
    /// </summary>
    /// <remarks>
    /// The list there is the single place that knows which effects carry a target index, and two
    /// callers depend on it — the compiler when it shifts a folded clause, and the index check
    /// above. A new targeting effect that is not listed silently keeps the wrong index when
    /// folded and is silently skipped when checked, which is precisely the failure a list is
    /// prone to. Reflection is what makes forgetting impossible rather than merely unlikely.
    /// </remarks>
    [Fact]
    public void Every_compiled_permanent_can_have_its_characteristics_computed()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present — skipping.");
            return;
        }

        // The other invariants read what the compiler produced; this one runs it. A static's
        // Applies and Apply are closures the compiler builds and nothing else executes until a
        // game does, so a null dereference in one is invisible to every check that only looks.
        var pool = new CompiledPool();
        var alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var bob = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var game = Game.Start(
            Guid.NewGuid(),
            [
                new PlayerSetup(alice, "Alice", 20, SmokeDeck("Alice")),
                new PlayerSetup(bob, "Bob", 20, SmokeDeck("Bob")),
            ],
            new GameRandom(1),
            startingPlayerId: alice,
            abilities: pool);

        game.BeginPlay(withMulligans: false);

        // One of each kind the opening of a game produces, plus the shapes most predicates are
        // actually watching for — an attack, a cast, damage, a counter. An empty attack batch is
        // deliberate: it is the case where a predicate reaches into the combat state for an
        // attacker that is not there.
        var sample = game.Log
            .GroupBy(e => e.GetType())
            .Select(g => g.First())
            .Concat<GameEvent>(
            [
                new StepBegan(TurnStep.Upkeep),
                new StepBegan(TurnStep.DeclareAttackers),
                new AttackersDeclared(ImmutableDictionary<ObjectId, AttackTarget>.Empty),
                new SpellCastEvent(alice, ObjectId.New(), "Something"),
                new SpellCastEvent(bob, ObjectId.New(), "Something Else"),
                new PlayerDamaged(bob, ObjectId.New(), 3, IsCombat: true),
                new DamageMarked(ObjectId.New(), 2),
                new CountersChanged(ObjectId.New(), "+1/+1", 1),
            ])
            .ToList();

        var faults = new List<string>();
        var checkedCards = 0;

        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);
            if (!compiled.IsComplete || (compiled.Statics.IsEmpty && compiled.Triggers.IsEmpty))
                continue;

            checkedCards++;

            try
            {
                // On the battlefield, because that is where a static functions (CR 604.3) and
                // where the closure will first be asked anything.
                var id = game.Create(alice, card, Zone.Battlefield);

                // Arriving can be a question now: a card with two entry replacements on it -
                // fading and enters-tapped on the same creature - has to be told which applies
                // first (CR 616.1), and the arrival is held until it is. Left unanswered, the
                // permanent never finishes entering and everything after this looks for an
                // object that is not there.
                AnswerAnyChoice(game);

                var obj = game.State.GetObject(id);
                _ = Characteristics.Of(game.State, pool, obj);

                // And every trigger predicate against a spread of real events. A predicate is
                // handed whatever happened, not only the thing it is watching for, so the answer
                // that matters most is the one it gives to an event it does not care about -
                // "the attacking creature's controller" asked about a step beginning has no
                // attacker to follow, and reaching for one is a crash in the middle of a game.
                foreach (var trigger in compiled.Triggers)
                {
                    foreach (var happened in sample)
                        _ = trigger.Triggers(happened, game.State, new TriggerSource(obj, pool));
                }

                // And once more with the permanent gone, which is the state a dependency probe
                // and a leaving trigger both see.
                game.Move(id, Zone.Exile, MoveCause.Exile);
                AnswerAnyChoice(game);
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                faults.Add($"{card.Name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        output.WriteLine(
            $"ran {checkedCards} cards' statics and triggers over {sample.Count} event kinds");
        foreach (var fault in faults.Take(25))
            output.WriteLine("  " + fault);

        Assert.True(
            faults.Count == 0,
            $"{faults.Count} compiled cards throw while being computed:" + Environment.NewLine
                + string.Join(Environment.NewLine, faults.Take(25)));
    }

    /// <summary>
    /// A card with two faces is understood only when every face of it is.
    /// </summary>
    /// <remarks>
    /// This test used to say the opposite - that such a card is *never* understood - and it was
    /// right when it was written: a `CardDefinition` held one set of characteristics, the two
    /// halves arrived as one body of merged text, and the `//` between them was left unread on
    /// purpose so nothing could compile as complete and play as one face doing the other's work.
    /// <para>
    /// The card model holds faces now and the compiler reads them one at a time, so the rule that
    /// replaces it is the one that actually protects the same thing: a card whose back face says
    /// something the engine cannot read is not one it can be trusted to play, so it must not
    /// report as understood. The check is worth keeping precisely because the guarantee is now
    /// structural - a refactor that lost it would otherwise be silent.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_card_with_two_faces_is_understood_only_when_every_face_is()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present - skipping.");
            return;
        }

        var faced = 0;
        var understood = 0;
        var claimed = new List<string>();

        foreach (var card in corpus.Where(c => c.Faces.Count > 1))
        {
            faced++;

            if (!CardCompiler.Compile(card).IsComplete)
                continue;

            understood++;

            if (card.Faces.Any(face => !CardCompiler.CompileFace(card, face).IsComplete))
                claimed.Add(card.Name);
        }

        output.WriteLine($"{faced} cards carry more than one face; {understood} are understood");

        // A floor, so a loader that stopped reading faces fails here rather than passing by
        // having nothing to check.
        Assert.True(faced >= 800, $"only {faced} cards arrived with faces; the corpus has 853");

        Assert.True(
            claimed.Count == 0,
            $"{claimed.Count} two-faced cards report as understood with a face that is not: "
                + string.Join(", ", claimed.Take(15)));
    }

    [Fact]
    public void Every_trigger_that_says_it_can_find_out_what_it_means()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present — skipping.");
            return;
        }

        // "That player" and "it" mean whatever the triggering event was about, and an event that
        // cannot say leaves them meaning nobody: the trigger fires, the effect resolves to an
        // empty list, and the card does nothing at all. Two cards were found doing exactly that
        // — one punishing a draw, one putting a counter on the wrong permanent — and neither
        // failed anything.
        //
        // **What the check can conclude depends entirely on the sample, and it says so every
        // run.** The sample below is one game opened and passed through a turn, plus the events
        // a game that short never produces; anything no event reaches is counted as unreachable
        // rather than as passing, so a green run is never proof the class is covered. It is
        // written this way so the number climbs as the sample grows - and it has. The sample was
        // one-sided: Alice's creature hit Bob and Bob's hit nobody, life only ever moved one
        // way, every spell cast was a creature cast from a hand, and a piece of Equipment was
        // attached to a *land*, so "whenever equipped creature deals combat damage" could not
        // fire by construction. Filling those in took it from 81 triggers checked to 97, and the
        // wider sample failed on its first run: **Manabarbs** had been dealing its damage to
        // nobody, because nothing read the player out of a mana event.
        var alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var bob = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var pool = new CompiledPool();

        var game = Game.Start(
            Guid.NewGuid(),
            [
                new PlayerSetup(alice, "Alice", 20, SmokeDeck("Alice")),
                new PlayerSetup(bob, "Bob", 20, SmokeDeck("Bob")),
            ],
            new GameRandom(1),
            startingPlayerId: alice,
            abilities: pool);

        game.BeginPlay(withMulligans: false);

        // Played far enough to produce a real draw and a real discard, because the events that
        // matter here are the ones carrying ids the state can still resolve. A synthetic
        // ObjectMoved names an object that never existed, and every subject read from it is null
        // whether the code under test is right or wrong - a check that cannot fail either way.
        for (var guard = 0; guard < 200 && game.State.TurnNumber < 2; guard++)
        {
            if (game.State.Choice is { } choice)
            {
                game.Choose(
                    choice.PlayerId,
                    [.. choice.Options.Take(Math.Max(choice.MinPicks, 1)).Select(o => o.Id)]);
                continue;
            }

            if (game.State.Priority.Holder is not { } holder)
                break;

            game.PassPriority(holder);
        }

        var discarding = game.Create(alice, SmokeDeck("Alice")[0], Zone.Hand);
        game.Move(discarding, Zone.Graveyard, MoveCause.Discard);

        // The events most triggers are actually watching for, made by doing the thing rather
        // than by constructing the event: a creature that really entered and really died leaves
        // ids the state can still resolve, and a subject read from a hand-built event is null
        // whether the code under test is right or wrong.
        var died = game.Create(alice, SmokeDeck("Alice")[1], Zone.Battlefield);
        game.Move(died, Zone.Graveyard, MoveCause.Destroy);

        var sacrificed = game.Create(bob, SmokeDeck("Bob")[1], Zone.Battlefield);
        game.Move(sacrificed, Zone.Graveyard, MoveCause.Sacrifice);

        var bounced = game.Create(alice, SmokeDeck("Alice")[2], Zone.Battlefield);
        game.Move(bounced, Zone.Hand, MoveCause.Other);

        var banished = game.Create(bob, SmokeDeck("Bob")[2], Zone.Battlefield);
        game.Move(banished, Zone.Exile, MoveCause.Exile);

        // Both players draw and discard, because "whenever an opponent draws a card" is only
        // reached by an opponent's draw - and which player is the opponent depends on whose turn
        // it is when the card under test is made.
        foreach (var who in new[] { alice, bob })
        {
            var drawn = game.Create(who, SmokeDeck("Alice")[4], Zone.Library);
            game.Move(drawn, Zone.Hand, MoveCause.Draw);

            var thrown = game.Create(who, SmokeDeck("Alice")[5], Zone.Hand);
            game.Move(thrown, Zone.Graveyard, MoveCause.Discard);

            // A land entering and leaving on each side, for the triggers that watch lands
            // rather than creatures.
            var land = game.Create(who, SmokeLand(who == alice ? "Alice" : "Bob"), Zone.Battlefield);
            game.Move(land, Zone.Graveyard, MoveCause.Destroy);
        }

        // Real spells on the stack: a cast predicate looks the spell up to see what kind it
        // was, and an event naming an id that was never created answers "no" to every question
        // about it - so a synthetic cast reaches none of these triggers.
        var herSpell = game.Create(alice, SmokeDeck("Alice")[6], Zone.Stack);
        var hisSpell = game.Create(bob, SmokeDeck("Bob")[6], Zone.Stack);

        // And a noncreature spell on each side. "Whenever a player casts a noncreature spell"
        // is told apart from the creature case by looking the spell up, and a deck of bears can
        // only ever produce the half of that question that is a creature.
        var herRelic = game.Create(alice, SmokeArtifact(), Zone.Stack);
        var hisRelic = game.Create(bob, SmokeArtifact(), Zone.Stack);
        var herCharm = game.Create(alice, SmokeEnchantment(), Zone.Stack);
        var hisCharm = game.Create(bob, SmokeEnchantment(), Zone.Stack);

        // And one with {X} in its cost, because that is a fact about the card rather than about
        // the casting, and the cards that ask about it look the spell up to find out.
        var herHydra = game.Create(alice, SmokeVariable(), Zone.Stack);
        var hisHydra = game.Create(bob, SmokeVariable(), Zone.Stack);

        // One spell of each colour, and one of two colours, on each side. A spell's colour comes
        // from its mana cost (CR 202.2), so a costless bear is colourless and answers no to every
        // card that asks about a red spell or a multicoloured one.
        var coloured = new[] { "{W}", "{U}", "{B}", "{R}", "{G}", "{W}{U}" }
            .SelectMany(cost => new[]
            {
                (Who: alice, Id: game.Create(alice, SmokeCost(cost), Zone.Stack)),
                (Who: bob, Id: game.Create(bob, SmokeCost(cost), Zone.Stack)),
            })
            .ToList();

        // A creature left standing, so the events that name one still resolve, and a land for
        // the Auras that enchant one.
        var standing = game.Create(alice, SmokeDeck("Alice")[3], Zone.Battlefield);
        var theirs = game.Create(bob, SmokeDeck("Bob")[3], Zone.Battlefield);
        var host = game.Create(bob, SmokeLand("Host"), Zone.Battlefield);

        // A creature to be equipped, because a piece of Equipment attached to a land does not
        // have an equipped *creature* and never reaches its own trigger. Which host a card gets
        // is read from its own words below.
        var creatureHost = game.Create(bob, SmokeDeck("Bob")[7], Zone.Battlefield);

        // An artifact and an enchantment that are actually on the battlefield. Both existed
        // already but only ever on the stack, so "whenever an artifact you control enters" was
        // watching for an arrival the sample never made.
        foreach (var who in new[] { alice, bob })
        {
            game.Create(who, SmokeArtifact(), Zone.Battlefield);
            game.Create(who, SmokeEnchantment(), Zone.Battlefield);
        }

        // Lands left standing on both sides, for the triggers that watch one being tapped for
        // mana - the pair inside the loop above is destroyed as part of making the death events.
        // One of each basic type, because "whenever a player taps an Island for mana" reads the
        // land's subtype off the source and a Forest answers no to every one of those.
        var basics = new[] { "Plains", "Island", "Swamp", "Mountain", "Forest" }
            .SelectMany(type => new[]
            {
                (Who: alice, Id: game.Create(alice, SmokeBasic(type), Zone.Battlefield)),
                (Who: bob, Id: game.Create(bob, SmokeBasic(type), Zone.Battlefield)),
            })
            .ToList();

        // One of each shape, and for the zone changes one of each cause: the cause is what says
        // whether a move is about a player, so collapsing them all into the first ObjectMoved
        // hides exactly the case this is looking for.
        // Arrivals and departures are kept in full rather than one per kind. "Whenever a land
        // an opponent controls enters" and "whenever another creature you control enters" are
        // told apart by whose it was and what it was, and collapsing every ObjectCreated to the
        // first one throws away exactly the difference they are reading.
        var sample = game.Log
            .Where(e => e is ObjectCreated or ObjectMoved)
            .Concat(game.Log
                .Where(e => e is not (ObjectCreated or ObjectMoved))
                .GroupBy(e => e.GetType())
                .Select(g => g.First()))
            .Concat<GameEvent>(
            [
                .. Enum.GetValues<TurnStep>().Select(step => new StepBegan(step)),
                new SpellCastEvent(alice, herSpell, "Hers"),
                new SpellCastEvent(bob, hisSpell, "Theirs"),

                // Declarations naming creatures that exist. The game is not in combat, so a
                // predicate reaching into the combat state finds nothing - which is itself a
                // shape worth driving, and the smoke test is what reports a throw from it.
                new AttackersDeclared(
                    ImmutableDictionary<ObjectId, AttackTarget>.Empty
                        .Add(standing, AttackTarget.Player(bob))),
                new BlockersDeclared(
                    ImmutableDictionary<ObjectId, ImmutableList<ObjectId>>.Empty
                        .Add(standing, [theirs])),
                new PermanentTapped(standing),
                new PermanentsUntapped([standing]),
                new CountersChanged(standing, "+1/+1", 1),
                new DamageMarked(theirs, 2, false, standing, IsCombat: true),
                new PlayerDamaged(bob, standing, 2, IsCombat: true),
                new CombatDamageDealt([standing], [standing]),
                new AttackersDeclared(ImmutableDictionary<ObjectId, AttackTarget>.Empty),
                new SpellCastEvent(alice, ObjectId.New(), "Something"),
                new PlayerDamaged(bob, ObjectId.New(), 3, IsCombat: true),
                new DamageMarked(ObjectId.New(), 2),
                new CountersChanged(ObjectId.New(), "+1/+1", 1),

                // The same combat again with the sides swapped. Every card under test belongs
                // to one player, so "a creature you control deals combat damage to a player" is
                // only reachable from the half of the sample on that player's side - and the
                // sample had one half, which is why a dozen triggers of that shape were being
                // reported as watching for something it never produced.
                new AttackersDeclared(
                    ImmutableDictionary<ObjectId, AttackTarget>.Empty
                        .Add(theirs, AttackTarget.Player(alice))),
                new BlockersDeclared(
                    ImmutableDictionary<ObjectId, ImmutableList<ObjectId>>.Empty
                        .Add(theirs, [standing])),
                new PermanentTapped(theirs),
                new PermanentsUntapped([theirs]),
                new CountersChanged(theirs, "+1/+1", 1),
                new DamageMarked(standing, 2, false, theirs, IsCombat: true),
                new PlayerDamaged(alice, theirs, 2, IsCombat: true),
                new CombatDamageDealt([theirs], [theirs]),

                // A gain and a loss for each player. These are one event with the sign flipped,
                // and the sample carried a single malformed one - eighteen life gained and a
                // total of minus two, from the arguments being written in the order the sentence
                // says them rather than the order the record declares them.
                new LifeChanged(alice, -2, 18),
                new LifeChanged(alice, 2, 22),
                new LifeChanged(bob, -2, 18),
                new LifeChanged(bob, 2, 22),

                new SpellCastEvent(alice, herRelic, "Her Relic"),
                new SpellCastEvent(bob, hisRelic, "His Relic"),
                new SpellCastEvent(alice, herCharm, "Her Charm"),
                new SpellCastEvent(bob, hisCharm, "His Charm"),
                new SpellCastEvent(alice, herHydra, "Her Hydra"),
                new SpellCastEvent(bob, hisHydra, "His Hydra"),

                // A nonbasic land tapped for mana. "Basic" is a supertype, so the basics above
                // answer no to every card that asks for the other half of that question.
                new ManaAdded(bob, MtgEngine.Domain.Enums.ManaColor.Colorless, 1, null, host),

                // Cast from somewhere other than hand. The zone a spell came from is part of the
                // event because the object that was in the graveyard stopped existing when it
                // moved (CR 400.7), and a sample that only ever casts from hand answers no to
                // every card asking about the rest.
                new SpellCastEvent(alice, herSpell, "Hers", Zone.Graveyard),
                new SpellCastEvent(bob, hisSpell, "Theirs", Zone.Graveyard),

                // Counters go down as well as up, and they are different triggers: "whenever you
                // put one or more -1/-1 counters on a creature" is never reached by a +1/+1.
                new CountersChanged(standing, "-1/-1", 1),
                new CountersChanged(theirs, "-1/-1", 1),

                // The equipped creature dealing damage, rather than the Equipment. A card that
                // says "whenever equipped creature deals combat damage" is watching its host,
                // and the host never hit anything.
                new PlayerDamaged(alice, creatureHost, 2, IsCombat: true),
                new PlayerDamaged(alice, creatureHost, 2, IsCombat: false),
                new DamageMarked(standing, 2, false, creatureHost, IsCombat: true),
                new AttackersDeclared(
                    ImmutableDictionary<ObjectId, AttackTarget>.Empty
                        .Add(creatureHost, AttackTarget.Player(alice))),
            ])

            // Each of those spells being cast, by the player whose it is.
            .Concat(coloured.Select(spell =>
                (GameEvent)new SpellCastEvent(spell.Who, spell.Id, "Smoke Spell")))

            // A tap of each basic, for both players. The source is part of the event, which is
            // what lets "whenever a player taps an Island for mana" tell it from any other mana.
            .Concat(basics.Select(basic =>
                (GameEvent)new ManaAdded(
                    basic.Who, MtgEngine.Domain.Enums.ManaColor.Green, 1, null, basic.Id)))
            .ToList();

        // The other half of the question, and a report rather than a gate. The check above asks
        // whether a trigger that fires can say who it is about; this asks whether it fires at
        // all. A trigger nothing reaches is a card that compiles, counts as covered, and sits on
        // the battlefield doing nothing - which is the same silent failure, one step earlier.
        //
        // It cannot assert: most of what it lists is the sample's own shortfall rather than the
        // engine's - "when ~ enters, if it was kicked" is unreachable because the probe puts the
        // permanent onto the battlefield instead of casting it, and no amount of engine work
        // changes that. It is read by hand, and it earns its place: the colour family sat near
        // the top of it, and the reason was that every colour question in the engine was answered
        // from the card's colour identity rather than from its colour.
        var deadShapes = new Dictionary<string, int>(StringComparer.Ordinal);
        var deadTotal = 0;
        var mute = new List<string>();
        var checkedTriggers = 0;
        var unreachable = new List<string>();
        var skipped = 0;

        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);
            if (!compiled.IsComplete)
                continue;

            foreach (var trigger in compiled.Triggers)
            {
                var effects = string.Join(" ", trigger.Effects.Select(e => e.ToString()));
                var wantsPlayer = effects.Contains("Scope = TriggerSubject", StringComparison.Ordinal);
                var wantsObject = effects.Contains("Subject = TriggerSubject", StringComparison.Ordinal);

                // "Put that many +1/+1 counters on it" reads how much the event was of, the
                // same way "it" reads what it was about, and fails the same way: an event with
                // no amount makes it nothing, and nothing happens. Read from the card's own
                // words because the amount is a delegate on the effect and every counting
                // amount looks alike from outside.
                // Except when the sentence says how much itself: "each opponent loses 1 life
                // and you gain that much life" is answered by the clause before it, which the
                // resolution carries forward, and no event needs to carry anything. Extort is
                // this shape and so is every drain.
                var wantsAmount =
                    (trigger.Text.Contains("that many", StringComparison.OrdinalIgnoreCase)
                        || trigger.Text.Contains("that much", StringComparison.OrdinalIgnoreCase))
                    && !SaysItsOwnAmountPattern.IsMatch(trigger.Text);

                if (!wantsPlayer && !wantsObject && !wantsAmount)
                {
                    var mark = game.Log.Count();
                    var probeId = game.Create(game.State.ActivePlayerId, card, Zone.Battlefield);
                    if (!game.State.TryGetObject(probeId, out var probeObj))
                        continue;

                    if (card.Subtypes.Contains("Aura", StringComparer.OrdinalIgnoreCase)
                        || card.Subtypes.Contains("Equipment", StringComparer.OrdinalIgnoreCase))
                    {
                        game.Attach(probeId, creatureHost);
                        probeObj = game.State.GetObject(probeId);
                    }

                    var own = new List<GameEvent>(game.Log.Skip(mark));
                    own.AddRange(
                    [
                        new PlayerDamaged(bob, probeId, 2, IsCombat: true),
                        new PlayerDamaged(bob, probeId, 2, IsCombat: false),
                        new DamageMarked(theirs, 2, false, probeId, IsCombat: true),
                        new DamageMarked(probeId, 2, false, theirs, IsCombat: true),
                        new CombatDamageDealt([probeId], [probeId]),
                        new AttackersDeclared(
                            ImmutableDictionary<ObjectId, AttackTarget>.Empty
                                .Add(probeId, AttackTarget.Player(bob))),
                        new BlockersDeclared(
                            ImmutableDictionary<ObjectId, ImmutableList<ObjectId>>.Empty
                                .Add(theirs, [probeId])),
                        new BlockersDeclared(
                            ImmutableDictionary<ObjectId, ImmutableList<ObjectId>>.Empty
                                .Add(probeId, [theirs])),
                        new PermanentTapped(probeId),
                        new PermanentsUntapped([probeId]),
                        new CountersChanged(probeId, "+1/+1", 1),
                        new CountersChanged(probeId, "-1/-1", 1),
                        new TargetsChosen(herSpell, [Target.ToPermanent(probeId)], 0),
                        new TargetsChosen(hisSpell, [Target.ToPermanent(probeId)], 0),
                        new PermanentTurned(probeId, false),
                    ]);

                    if (game.State.TryGetObject(probeId, out _))
                    {
                        var dying = game.Log.Count();
                        game.Move(probeId, Zone.Graveyard, MoveCause.Destroy);
                        own.AddRange(game.Log.Skip(dying));
                    }

                    if (!own.Concat(sample).Any(e => Fires(trigger, e, game.State, probeObj, pool)))
                    {
                        var shape = string.Join(" ", trigger.Text.Split(' ').Take(6));
                        deadShapes[shape] = deadShapes.GetValueOrDefault(shape) + 1;
                        deadTotal++;
                    }

                    continue;
                }

                // "Whenever ~ is dealt damage, put a +1/+1 counter on it" - the sentence is
                // about the card itself, so the fallback to the permanent with the ability is
                // not a guess, it is the answer. Excluded deliberately: this check is looking
                // for sentences about something else that quietly become sentences about the
                // source. "~ or another" is not excluded, because that half can be the other
                // creature and the fallback would then be wrong.
                if (AboutItselfPattern.IsMatch(trigger.Text))
                    continue;

                // An Aura or Equipment whose sentence names what it is on has an answer without
                // the event supplying one: "put a -1/-1 counter on it" under "the upkeep of
                // enchanted creature's controller" means the creature, and the resolver reaches
                // the host before it reaches the card. Excluded for the same reason as the
                // sentences about the card itself - the fallback is the reading, not a guess.
                var namesItsHost =
                    trigger.Text.Contains("enchanted ", StringComparison.OrdinalIgnoreCase)
                    || trigger.Text.Contains("equipped ", StringComparison.OrdinalIgnoreCase);

                // Only the object half: a host answers "it", and says nothing about "that
                // player", which still has to come from the event.
                if (wantsObject && namesItsHost)
                {
                    if (!wantsPlayer)
                        continue;

                    wantsObject = false;
                }

                // Created under whoever's turn it is, because "at the beginning of your
                // upkeep" asks whether the active player controls the ability - and a card
                // parked under the other player never sees its own step.
                var id = game.Create(game.State.ActivePlayerId, card, Zone.Battlefield);
                var obj = game.State.GetObject(id);

                // Most of these triggers are about the card itself - "whenever ~ deals combat
                // damage", "whenever ~ attacks" - and an event naming some other creature never
                // reaches them. The shared sample cannot name a card it was built before, so
                // the events about this one are made here, with its own id.
                // An Aura or a piece of Equipment says almost nothing until it is on something:
                // "at the beginning of the upkeep of enchanted land's controller" reads its host
                // and finds none, so every trigger of that shape is unreachable while it floats.
                // Attached to a permanent somebody else controls, because that is the case these
                // cards are printed for and the one where host and owner differ.
                if (card.Subtypes.Contains("Aura", StringComparer.OrdinalIgnoreCase)
                    || card.Subtypes.Contains("Equipment", StringComparer.OrdinalIgnoreCase))
                {
                    // The host is read from the card's own words. "Enchanted player" wants a
                    // player and "enchanted land" wants the land; everything else wants the
                    // creature, and the land it used to get was never one - so every trigger
                    // that says "equipped creature" was unreachable by construction.
                    if (trigger.Text.Contains("enchanted player", StringComparison.OrdinalIgnoreCase))
                        game.Attach(id, null, game.State.ActivePlayerId);
                    else if (trigger.Text.Contains("enchanted land", StringComparison.OrdinalIgnoreCase))
                        game.Attach(id, host);
                    else
                        game.Attach(id, creatureHost);

                    obj = game.State.GetObject(id);
                }

                var mine = new GameEvent[]
                {
                    new PlayerDamaged(bob, id, 2, IsCombat: true),
                    new PlayerDamaged(bob, id, 2, IsCombat: false),
                    new DamageMarked(theirs, 2, false, id, IsCombat: true),
                    new DamageMarked(id, 2, false, theirs, IsCombat: true),
                    new CombatDamageDealt([id], [id]),
                    new AttackersDeclared(
                        ImmutableDictionary<ObjectId, AttackTarget>.Empty
                            .Add(id, AttackTarget.Player(bob))),
                    new BlockersDeclared(
                        ImmutableDictionary<ObjectId, ImmutableList<ObjectId>>.Empty
                            .Add(theirs, [id])),
                    new PermanentTapped(id),
                    new PermanentsUntapped([id]),
                    new CountersChanged(id, "+1/+1", 1),

                    // "Whenever you cast a spell that targets ~" - the targeting is its own
                    // event, and it has to name this card to reach the trigger.
                    new TargetsChosen(herSpell, [Target.ToPermanent(id)], 0),
                    new TargetsChosen(hisSpell, [Target.ToPermanent(id)], 0),
                };

                var accepted = sample
                    .Concat(mine)
                    .Where(e => Fires(trigger, e, game.State, obj, pool))
                    .ToList();

                game.Move(id, Zone.Exile, MoveCause.Exile);

                // No sample event reaches this trigger, so there is nothing to conclude: the
                // check reports what it can see and stays quiet about what it cannot.
                if (accepted.Count == 0)
                {
                    skipped++;
                    unreachable.Add(trigger.Text);
                    continue;
                }

                checkedTriggers++;

                if (wantsPlayer
                    && !accepted.Any(e => Game.SubjectOf(e, game.State) is not null))
                {
                    mute.Add($"{card.Name} ({trigger.Id}): says \"that player\" and no event it "
                        + "accepts names one");
                }

                if (wantsObject
                    && !accepted.Any(e => Game.SubjectObjectOf(e) is not null))
                {
                    mute.Add($"{card.Name} ({trigger.Id}): says \"it\" and no event it accepts "
                        + "names one");
                }

                if (wantsAmount && !accepted.Any(e => Game.AmountOf(e) is not null))
                {
                    mute.Add($"{card.Name} ({trigger.Id}): says \"that many\" and no event it "
                        + "accepts carries an amount");
                }
            }
        }

        output.WriteLine($"{deadTotal} triggers no sample event reaches, by opening words:");
        foreach (var (shape, n) in deadShapes.OrderByDescending(p => p.Value).Take(25))
            output.WriteLine($"  dead {n,5}  {shape}");

        output.WriteLine(
            $"checked {checkedTriggers} triggers; {skipped} name a subject but watch for "
            + "something this sample never produces, so nothing is concluded about them");

        foreach (var group in unreachable
            .GroupBy(t => string.Join(" ", t.Split(' ').Take(4)))
            .OrderByDescending(g => g.Count())
            .Take(18))
        {
            output.WriteLine($"  unreachable x{group.Count()}: {group.First()}");
        }

        foreach (var line in mute.Take(25))
            output.WriteLine("  " + line);

        Assert.True(
            mute.Count == 0,
            $"{mute.Count} triggers resolve their subject to nobody:" + Environment.NewLine
                + string.Join(Environment.NewLine, mute.Take(25)));
    }

    /// <summary>Answers whatever is being asked, taking the first option each time.</summary>
    private static void AnswerAnyChoice(Game game)
    {
        for (var guard = 0; guard < 20 && game.State.Choice is { } choice; guard++)
        {
            game.Choose(
                choice.PlayerId,
                [.. choice.Options.Take(Math.Max(choice.MinPicks, 1)).Select(o => o.Id)]);
        }
    }

    /// <summary>Passes priority until a condition holds, answering any choice on the way.</summary>
    private static void Advance(Game game, Func<bool> until)
    {
        for (var guard = 0; guard < 400 && !until(); guard++)
        {
            if (game.State.Choice is { } choice)
            {
                game.Choose(
                    choice.PlayerId,
                    [.. choice.Options.Take(Math.Max(choice.MinPicks, 1)).Select(o => o.Id)]);
                continue;
            }

            if (game.State.Priority.Holder is not { } holder)
                break;

            game.PassPriority(holder);
        }
    }

    /// <summary>A coloured creature, optionally with flying.</summary>
    private static MtgEngine.Domain.Models.CardDefinition SmokeColoured(
        MtgEngine.Domain.Enums.ManaColor colour,
        bool flying)
    {
        return new()
        {
            OracleId = $"smoke-{colour}-{flying}",
            Name = $"Smoke {colour}{(flying ? " Flier" : string.Empty)}",
            CardTypes = MtgEngine.Domain.Enums.CardType.Creature,
            ColorIdentity = [colour],
            Colors = [colour],
            Power = 2,
            Toughness = 2,
            Keywords = flying
                ? MtgEngine.Domain.Enums.KeywordAbility.Flying
                : MtgEngine.Domain.Enums.KeywordAbility.None,
            OracleText = string.Empty,
        };
    }

    /// <summary>A creature big enough for the effects that name a toughness.</summary>
    private static MtgEngine.Domain.Models.CardDefinition SmokeBig() => new()
    {
        OracleId = "smoke-big",
        Name = "Smoke Colossus",
        CardTypes = MtgEngine.Domain.Enums.CardType.Creature,
        Power = 5,
        Toughness = 5,
        OracleText = string.Empty,
    };

    /// <summary>A basic land of a named type.</summary>
    private static MtgEngine.Domain.Models.CardDefinition SmokeBasic(string type) => new()
    {
        OracleId = $"smoke-basic-{type}",
        Name = type,
        CardTypes = MtgEngine.Domain.Enums.CardType.Land,
        Supertypes = ["Basic"],
        Subtypes = [type],
        OracleText = string.Empty,
    };

    /// <summary>An artifact and an enchantment, for the effects that name them.</summary>
    private static MtgEngine.Domain.Models.CardDefinition SmokeArtifact() => new()
    {
        OracleId = "smoke-artifact",
        Name = "Smoke Relic",
        CardTypes = MtgEngine.Domain.Enums.CardType.Artifact,
        OracleText = string.Empty,
    };

    private static MtgEngine.Domain.Models.CardDefinition SmokeEnchantment() => new()
    {
        OracleId = "smoke-enchantment",
        Name = "Smoke Charm",
        CardTypes = MtgEngine.Domain.Enums.CardType.Enchantment,
        OracleText = string.Empty,
    };

    /// <summary>A spell of a named cost, for the cards that ask what colour one was.</summary>
    private static MtgEngine.Domain.Models.CardDefinition SmokeCost(string cost) => new()
    {
        OracleId = "smoke-cost-" + cost,
        Name = "Smoke Bolt " + cost,
        CardTypes = MtgEngine.Domain.Enums.CardType.Sorcery,
        ManaCostRaw = cost,

        // Stated rather than derived from the cost above. The engine reads a card's colour off
        // its own field, and a sample that only wrote the cost was colourless to every card
        // asking about a red spell - which is how the colour family stayed at the top of the
        // unreachable list through two rounds of filling the sample in.
        Colors =
        [
            .. new[]
            {
                ("{W}", MtgEngine.Domain.Enums.ManaColor.White),
                ("{U}", MtgEngine.Domain.Enums.ManaColor.Blue),
                ("{B}", MtgEngine.Domain.Enums.ManaColor.Black),
                ("{R}", MtgEngine.Domain.Enums.ManaColor.Red),
                ("{G}", MtgEngine.Domain.Enums.ManaColor.Green),
            }
            .Where(symbol => cost.Contains(symbol.Item1, StringComparison.Ordinal))
            .Select(symbol => symbol.Item2),
        ],
        OracleText = string.Empty,
    };

    /// <summary>A spell with {X} in its cost, for the cards that ask whether one has.</summary>
    private static MtgEngine.Domain.Models.CardDefinition SmokeVariable() => new()
    {
        OracleId = "smoke-variable",
        Name = "Smoke Surge",
        CardTypes = MtgEngine.Domain.Enums.CardType.Sorcery,
        ManaCostRaw = "{X}{R}",
        OracleText = string.Empty,
    };

    /// <summary>A land, for the triggers that watch lands rather than creatures.</summary>
    private static MtgEngine.Domain.Models.CardDefinition SmokeLand(string who) => new()
    {
        OracleId = $"smoke-land-{who}",
        Name = $"Smoke Waste {who}",
        CardTypes = MtgEngine.Domain.Enums.CardType.Land,
        OracleText = string.Empty,
    };

    /// <summary>A sentence that names its own amount before asking for "that much".</summary>
    private static readonly System.Text.RegularExpressions.Regex SaysItsOwnAmountPattern =
        new(
            @"(loses?|lost|deals?|dealt|mills?|milled|draws?|drew|discards?|discarded)[^.]*"
                + @"that (much|many)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>A trigger whose subject is the card itself and nothing else.</summary>
    private static readonly System.Text.RegularExpressions.Regex AboutItselfPattern =
        new(@"^(When|Whenever) ~ (?!or )", System.Text.RegularExpressions.RegexOptions.None);

    /// <summary>Whether a trigger accepts an event, treating a throw as "no".</summary>
    /// <remarks>
    /// A predicate that throws on an event it does not care about is a different fault, and the
    /// smoke test is what reports it. Swallowing it here keeps one failure from being reported
    /// as two different things.
    /// </remarks>
    private static bool Fires(
        TriggeredAbilityDefinition trigger,
        GameEvent e,
        GameState state,
        GameObject source,
        IAbilitySource abilities)
    {
        try
        {
            return trigger.Triggers(e, state, new TriggerSource(source, abilities));
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            return false;
        }
    }

    /// <summary>
    /// Untargeted spells that resolve and emit no events at all — a report, not a gate.
    /// </summary>
    /// <remarks>
    /// **This asserts nothing and is not meant to.** Whether a mass effect does anything depends
    /// on what is on the board: "destroy all creatures" emitting nothing is the right answer when
    /// there are no creatures. The board below is built up to make the number mean something —
    /// a creature of each colour, a flier, a tapped one, a basic of each type, an artifact, an
    /// enchantment, cards in graveyards and hands, X set to 2 — and it went 173 → 33 as that
    /// board filled, which is the measure of how much of the count is scenery.
    /// <para>
    /// The residue is worth reading by hand, and it earned that: **Boil** was in it, destroying
    /// no Islands, because the group grammar folded plurals for the six card types and left
    /// every tribe and land type plural. What is left after that fix is combat — no attackers,
    /// no blockers — and shapes the board still has none of.
    /// </para>
    /// </remarks>
    [Fact]
    public void Untargeted_spells_that_resolve_to_nothing_are_reported()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
            return;

        var alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var bob = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var pool = new CompiledPool();

        var game = Game.Start(
            Guid.NewGuid(),
            [
                new PlayerSetup(alice, "Alice", 20, SmokeDeck("Alice")),
                new PlayerSetup(bob, "Bob", 20, SmokeDeck("Bob")),
            ],
            new GameRandom(1),
            startingPlayerId: alice,
            abilities: pool);

        game.BeginPlay(withMulligans: false);

        // A board with something on it. "Destroy all creatures" resolving to nothing is the
        // right answer when there are no creatures, so an empty battlefield makes this check
        // report every mass effect in the game and mean none of it.
        foreach (var who in new[] { alice, bob })
        {
            game.Create(who, SmokeDeck(who == alice ? "Alice" : "Bob")[0], Zone.Battlefield);
            game.Create(who, SmokeArtifact(), Zone.Battlefield);
            game.Create(who, SmokeEnchantment(), Zone.Battlefield);
            game.Create(who, SmokeLand(who == alice ? "A" : "B"), Zone.Battlefield);
            game.Create(who, SmokeDeck(who == alice ? "Alice" : "Bob")[1], Zone.Graveyard);
            game.Create(who, SmokeDeck(who == alice ? "Alice" : "Bob")[2], Zone.Hand);

            // One creature of each colour, one with flying, one tapped, and a basic of each
            // type: the residue of this check is otherwise just the shapes the board happens
            // not to contain.
            foreach (var colour in Enum.GetValues<MtgEngine.Domain.Enums.ManaColor>())
            {
                game.Create(who, SmokeColoured(colour, flying: false), Zone.Battlefield);
                game.Create(who, SmokeColoured(colour, flying: true), Zone.Battlefield);
            }

            var tapped = game.Create(who, SmokeBig(), Zone.Battlefield);
            game.Tap(tapped);

            foreach (var basic in new[] { "Island", "Forest", "Plains", "Swamp", "Mountain" })
                game.Create(who, SmokeBasic(basic), Zone.Battlefield);

            game.Create(who, SmokeEnchantment(), Zone.Graveyard);
        }

        // And a combat, because "attacking creatures get +2/+0" and "deals 1 damage to each
        // attacking creature" are a third of what is left otherwise. Played rather than
        // constructed: the combat state is what the filters read, and nothing else sets it.
        Advance(game, () => game.State.TurnNumber >= 3
            && game.State.CurrentStep == TurnStep.DeclareAttackers);

        var attackers = game.State.Battlefield
            .Select(game.State.GetObject)
            .Where(o => o.ControllerId == game.State.ActivePlayerId
                && o.Card.CardTypes.HasFlag(MtgEngine.Domain.Enums.CardType.Creature)
                && o.Permanent?.IsTapped == false)
            .Take(2)
            .Select(o => o.Id)
            .ToList();

        var defender = game.State.TurnOrder.First(id => id != game.State.ActivePlayerId);

        if (attackers.Count > 0)
        {
            game.DeclareAttackers(
                game.State.ActivePlayerId,
                attackers.ToDictionary(a => a, _ => AttackTarget.Player(defender)));

            Advance(game, () => game.State.CurrentStep == TurnStep.DeclareBlockers);

            var blocker = game.State.Battlefield
                .Select(game.State.GetObject)
                .FirstOrDefault(o => o.ControllerId == defender
                    && o.Card.CardTypes.HasFlag(MtgEngine.Domain.Enums.CardType.Creature)
                    && o.Permanent?.IsTapped == false);

            if (blocker is not null)
            {
                game.DeclareBlockers(
                    defender,
                    new Dictionary<ObjectId, IReadOnlyList<ObjectId>>
                    {
                        [attackers[0]] = [blocker.Id],
                    });
            }
        }

        var silent = new List<string>();
        var looked = 0;

        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);
            if (!compiled.IsComplete || compiled.Spell is not { } spell)
                continue;

            // Only spells that ask for nothing: with no targets and no modes there is nothing
            // missing, so resolving one has to do something.
            if (!spell.Targets.IsEmpty || !spell.Modes.IsEmpty || spell.Effects.IsEmpty)
                continue;

            looked++;

            var context = new ResolutionContext
            {
                State = game.State,
                Abilities = pool,
                ControllerId = alice,
                SourceId = game.Create(alice, card, Zone.Stack),

                // X is chosen as the spell is cast (CR 601.2b), and a spell that draws X cards
                // with X left at zero draws none - which is right, and says nothing about
                // whether the card works.
                VariableValue = 2,
            };

            try
            {
                var emitted = spell.Effects.Sum(e => e.Resolve(context).Count);
                if (emitted == 0)
                    silent.Add($"{card.Name}: {card.OracleText?.Replace((char)10, ' ')}");
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                silent.Add($"{card.Name}: threw {ex.GetType().Name}");
            }
        }

        output.WriteLine($"resolved {looked} untargeted spells; {silent.Count} emitted nothing");
        output.WriteLine(
            "A report and not a gate: most of these need something the board has not got.");

        foreach (var line in silent.Take(40))
            output.WriteLine("  " + line);
    }

    [Fact]
    public void Every_subtype_a_filter_names_is_a_subtype_some_card_has()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present — skipping.");
            return;
        }

        // Every subtype printed on any card, which is the only definition of the word there is.
        var known = new HashSet<string>(
            corpus.SelectMany(c => c.Subtypes ?? []), StringComparer.OrdinalIgnoreCase);

        var faults = new List<string>();
        var checkedFilters = 0;

        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);
            if (!compiled.IsComplete)
                continue;

            foreach (var filter in FiltersIn(compiled))
            {
                // A filter is one or more names joined by the vocabulary's separators; only the
                // capitalised ones are subtypes, the rest are card types and keywords.
                foreach (var name in filter.Split(['|', '&'], StringSplitOptions.RemoveEmptyEntries))
                {
                    if (name.Length == 0 || !char.IsUpper(name[0]))
                        continue;

                    checkedFilters++;
                    if (!known.Contains(name))
                        faults.Add($"{card.Name}: filter names '{name}', which no card is");
                }
            }
        }

        output.WriteLine($"checked {checkedFilters} subtype filters");
        foreach (var fault in faults.Take(25))
            output.WriteLine("  " + fault);

        // A filter naming a subtype nothing has is silent: the search offers no cards, the dig
        // finds nothing, and the card plays as a blank while compiling perfectly. It is how
        // "you control no Islands" came to look for the subtype "Islands" and find none however
        // many Islands were on the battlefield.
        Assert.True(
            faults.Count == 0,
            $"{faults.Count} filters name a subtype no card has:\n  "
                + string.Join("\n  ", faults.Take(25)));
    }

    /// <summary>A deck of vanilla creatures, so the game can be started and drawn from.</summary>
    private static ImmutableList<MtgEngine.Domain.Models.CardDefinition> SmokeDeck(string who) =>
        [.. Enumerable.Range(0, 40).Select(i => new MtgEngine.Domain.Models.CardDefinition
        {
            OracleId = $"smoke-{who}-{i}",
            Name = $"Smoke Bear {who} {i}",
            CardTypes = MtgEngine.Domain.Enums.CardType.Creature,
            Power = 2,
            Toughness = 2,
        })];

    /// <summary>Every filter string a compiled card carries, wherever it is held.</summary>
    private static IEnumerable<string> FiltersIn(CompiledCard compiled)
    {
        var effects = new List<MtgEngine.Rules.Abilities.IEffect>();

        if (compiled.Spell is { } spell)
        {
            effects.AddRange(spell.Effects);
            effects.AddRange(spell.Modes.SelectMany(m => m.Effects));
        }

        effects.AddRange(compiled.Triggers.SelectMany(t => t.Effects));
        effects.AddRange(compiled.Activated.SelectMany(a => a.Effects));

        foreach (var effect in effects)
        {
            foreach (var property in effect.GetType().GetProperties())
            {
                if (property.PropertyType == typeof(string)
                    && property.Name.Contains("Filter", StringComparison.Ordinal)
                    && property.GetValue(effect) is string value
                    && value.Length > 0)
                {
                    yield return value;
                }
            }
        }
    }

    /// <summary>
    /// A mass static may not name a creature type no card has (CR 205.3m).
    /// </summary>
    /// <remarks>
    /// The twin of <see cref="Every_subtype_a_filter_names_is_a_subtype_some_card_has"/>, for the
    /// half it could not see. That one walks the <c>*Filter</c> string properties of spell,
    /// trigger and activated-ability effects; a continuous effect carries its filter as a compiled
    /// predicate instead, so the only handle on what it selects is the description baked into its
    /// id — and nothing was checking it.
    /// <para>
    /// What went through the gap is the reason this exists. The mass-static pattern read any
    /// capitalised plural as a creature subtype, and every printed sentence starts with a capital,
    /// so <c>"Artifact creatures you control get +1/+1"</c> compiled into a lord for the creature
    /// type "Artifact". No card has that type. The line therefore read as <em>complete</em> and
    /// buffed nothing — 150 lines across 129 cards. That is strictly worse than an unread card:
    /// an unread card is refused by the legality gate, while this one is legal, playable, and
    /// quietly does nothing, with no test failing and nothing on the board to say so.
    /// </para>
    /// <para>
    /// Only the group description is inspected. The card's own name is in the id too and is full
    /// of capitalised words that are not subtypes, which is why the id is split rather than
    /// scanned whole.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_subtype_a_mass_static_names_is_a_subtype_some_card_has()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present — skipping.");
            return;
        }

        // Every subtype printed on any card, which is the only definition of the word there is.
        var known = new HashSet<string>(
            corpus.SelectMany(c => c.Subtypes ?? []), StringComparer.OrdinalIgnoreCase);

        var faults = new List<string>();
        var inspected = 0;

        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);
            if (!compiled.IsComplete)
                continue;

            foreach (var stat in compiled.Statics)
            {
                // "mass:<group>:<card name>:<what it does>" — the group is the only segment that
                // names what the effect selects.
                var parts = stat.Id.Split(':');
                if (parts.Length < 2 || !string.Equals(parts[0], "mass", StringComparison.Ordinal))
                    continue;

                inspected++;

                foreach (var word in parts[1].Split(
                    [' ', '|', '&'], StringSplitOptions.RemoveEmptyEntries))
                {
                    // Lower-case words are card types, adjectives and keywords; only a capital
                    // claims to be a creature type.
                    if (word.Length == 0 || !char.IsUpper(word[0]) || known.Contains(word))
                        continue;

                    faults.Add($"{card.Name}: \"{word}\" is not a subtype any card has ({stat.Id})");
                }
            }
        }

        output.WriteLine($"mass statics inspected: {inspected}");

        Assert.True(
            faults.Count == 0,
            "these mass statics select a creature type no card has, so they read as complete "
                + "and do nothing:\n  " + string.Join("\n  ", faults.Take(40)));
    }

    /// <summary>
    /// A card that reads as complete must declare some behaviour.
    /// </summary>
    /// <remarks>
    /// The cheapest guard against this project's worst failure mode, which is not an unread card
    /// but a card that reads as understood and does nothing. An unread card is refused by the
    /// legality gate and says so; an inert one is legal, playable, and silently wrong, and no
    /// other test in the suite can see it. Two have shipped and been found by hand — a mass static
    /// that became a lord for the creature type "Artifact" (129 cards), and a counting phrase that
    /// resolved "Equipment" to a creature subtype nothing has, so the card gained 0 life instead
    /// of 2.
    /// <para>
    /// This catches only the blunt case: printed rules text, no keyword flags, and nothing at all
    /// declared. It is green today across every complete card and costs almost nothing to keep,
    /// which is the whole argument for it — the subtler forms need a witness board or a behavioural
    /// mutation, and neither is cheap enough to run on every build.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_complete_card_with_rules_text_declares_some_behaviour()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present — skipping.");
            return;
        }

        var inert = new List<string>();
        var inspected = 0;

        foreach (var card in corpus)
        {
            // Asked of the lines the compiler actually reads, not of the printed text. A vanilla
            // creature is correctly inert, and so is Icehide Golem, whose whole oracle text is the
            // reminder "({S} can be paid with one mana from a snow source.)" - reminder text has no
            // rules meaning (CR 207.2), the compiler strips it, and there is nothing left to
            // declare behaviour from. That card is the only one in the corpus this distinction
            // separates, and getting it wrong would have meant either a permanently red guard or a
            // named exception for a card that has done nothing wrong.
            if (!CardCompiler.Lines(card).Any())
                continue;

            var compiled = CardCompiler.Compile(card);
            if (!compiled.IsComplete)
                continue;

            inspected++;

            // Asked of the compiled card rather than restated here. This used to be a second copy
            // of everything a CompiledCard can carry, and it drifted the moment one was added:
            // amplify landed on the record, was left off this list, and Glowering Rogon - whose
            // only printed line is "Amplify 1" - read as complete, playable and inert. The five
            // below are the ones HasAbilities deliberately does not cover, because they are ways
            // of casting the card rather than things the permanent does.
            var declares =
                compiled.HasAbilities
                || compiled.Adventure is not null
                || compiled.PreparedSpell is not null
                || !compiled.Halves.IsEmpty
                || compiled.HasFuse
                || compiled.PartnerRule is not null
                || card.Keywords != MtgEngine.Domain.Enums.KeywordAbility.None;

            if (!declares)
                inert.Add($"{card.Name}: \"{card.OracleText.Replace('\n', ' ')}\"");
        }

        output.WriteLine($"complete cards with rules text inspected: {inspected}");

        Assert.True(
            inert.Count == 0,
            "these cards read as complete and declare no behaviour at all, so they are legal, "
                + "playable and inert:\n  " + string.Join("\n  ", inert.Take(40)));
    }

    [Fact]
    public void Every_targeting_effect_is_listed_in_EffectTargets()
    {
        var targeting = typeof(IEffect).Assembly.GetTypes()
            .Where(t => t.IsAssignableTo(typeof(IEffect)) && !t.IsAbstract && !t.IsInterface)
            // Any property that names a target slot, not just one called TargetIndex. Fight
            // has TheirIndex and MyIndex, and was invisible to this test while being exactly the
            // kind of effect it exists to catch — one whose indices have to move when a clause is
            // folded into a larger ability.
            .Where(t => t.GetProperties().Any(IsTargetSlot))
            .ToList();

        Assert.NotEmpty(targeting);

        // Membership of the table is the question, not what any instance happens to hold — two of
        // these have a nullable index, so probing an instance would report them missing when they
        // are listed. That was the first thing this test got wrong.
        var listed = EffectTargets.HandledTypes.ToHashSet();
        var unlisted = targeting.Where(t => !listed.Contains(t)).ToList();

        // The message carries the line to add, because this test fires every time an effect that
        // aims at something is written and the fix is always the same two lines. Being told what
        // is missing and then having to go and work out the shape is a build cycle spent on
        // nothing — six of them, at the last count.
        var lines = unlisted.Select(t =>
            t.GetProperties().First(IsTargetSlot).PropertyType == typeof(int?)
                ? $"        Add<{t.Name}>(\n"
                    + "            e => e.TargetIndex,\n"
                    + "            (e, n) => e.TargetIndex is { } i ? e with { TargetIndex = i + n } : e);"
                : $"        Add<{t.Name}>(\n"
                    + "            e => e.TargetIndex, (e, n) => e with { TargetIndex = e.TargetIndex + n });");

        Assert.True(
            unlisted.Count == 0,
            "Targeting effects missing from EffectTargets. Add to EffectTargets.Build():\n\n"
                + string.Join("\n\n", lines));
    }

    /// <summary>
    /// Whether a property is a target slot the table would have to move.
    /// </summary>
    /// <remarks>
    /// Every index except <c>EffectIndex</c>, which ends in the same word and is the opposite
    /// thing: a locator saying where an effect sits inside its own ability, so that a deferred
    /// question can find it again. Shifting one of those would break the lookup rather than fix
    /// an index — which the first version of this check would have had the compiler do.
    /// </remarks>
    private static bool IsTargetSlot(System.Reflection.PropertyInfo property) =>
        property.Name.EndsWith("Index", StringComparison.Ordinal)
        && !string.Equals(property.Name, "EffectIndex", StringComparison.Ordinal)
        && (property.PropertyType == typeof(int) || property.PropertyType == typeof(int?));

    /// <summary>
    /// Every compiled effect must resolve, and the events it produces must apply, whichever zone
    /// the object it is about turns out to be in.
    /// </summary>
    /// <remarks>
    /// The other checks here are all static: they read the compiled shape and never run it. That
    /// is the gap a real bug fell through. "Whenever a creature an opponent controls dies, exile
    /// that creature" compiled cleanly, and then threw on resolution -
    /// <c>is in Graveyard, but the move says it is leaving Battlefield</c> - because
    /// <c>ExileTarget</c> had its origin zone hardcoded. It had the object in hand and never asked
    /// it where it was, which was invisible for as long as every pronoun pointed at a permanent.
    /// <para>
    /// So this runs them. Each effect is resolved twice against a fresh game, once with its
    /// subject on the battlefield and once with the same card in a graveyard, and the events are
    /// pushed through the reducer - because that is where the failure surfaced, not in the effect.
    /// Effects that need targets return nothing and pass trivially; that is fine, the point is the
    /// subject path, which is the one nothing else covers.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_compiled_effect_resolves_wherever_its_subject_is()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present — skipping.");
            return;
        }

        var pool = new CompiledPool();
        var faults = new Dictionary<string, (int Count, string Example)>(StringComparer.Ordinal);
        var resolved = 0;

        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);

            foreach (var (effect, specs) in EveryEffect(compiled))
            {
                foreach (var how in Variations)
                {
                    resolved++;

                    try
                    {
                        RunOnce(effect, pool, how, specs);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException
                        or NullReferenceException
                        or ArgumentException
                        or KeyNotFoundException
                        or IndexOutOfRangeException)
                    {
                        var shape = $"{effect.GetType().Name}: {ex.GetType().Name}";
                        _ = how;
                        var seen = faults.GetValueOrDefault(shape);
                        faults[shape] = (seen.Count + 1, seen.Example ?? $"{card.Name}: {ex.Message}");
                    }
                }
            }
        }

        output.WriteLine($"resolved {resolved} effect runs across {corpus.Count} cards");

        foreach (var (shape, (count, example)) in faults.OrderByDescending(p => p.Value.Count))
            output.WriteLine($"  {count,6}  {shape}  e.g. {example}");

        Assert.True(faults.Count == 0, $"{faults.Count} effect shapes threw; see output");
    }

    /// <summary>
    /// The situations an effect is put in. Each is a real shape a resolution can have, and the
    /// list is short on purpose: every entry has to be one the engine could actually produce, or
    /// the failures it reports are about a game nobody is playing.
    /// </summary>
    private enum Situation
    {
        /// <summary>Subject on the battlefield, nothing targeted.</summary>
        SubjectOnBattlefield,

        /// <summary>Subject in a graveyard - what a dies trigger hands its effects.</summary>
        SubjectInGraveyard,

        /// <summary>
        /// No subject at all, which is what an activated ability and a spell both have.
        /// </summary>
        NoSubject,

        /// <summary>
        /// Targets chosen. Most of the effect library takes a target and returns nothing without
        /// one, so the first two situations never reach past their opening guard - this is what
        /// actually exercises them.
        /// </summary>
        Targeted,

        /// <summary>
        /// Targets chosen, and then the objects moved before the effect resolved (CR 608.2b).
        /// </summary>
        /// <remarks>
        /// The reason this exists is a pattern found three times: an effect that has the object
        /// in hand, and describes a move out of the zone it assumed rather than the one the object
        /// is in. Twenty-four moves in the effect library name their origin as a literal, and
        /// reading each of them to decide which could be wrong is the sort of audit that misses
        /// one. This asks all of them at once, by pointing every target somewhere its spec did not
        /// expect - which is a real situation, not an invented one: a target can stop being legal
        /// between being chosen and the spell resolving.
        /// </remarks>
        TargetsMoved,

        /// <summary>
        /// Targets chosen, and the controller has nothing left: no hand, no library.
        /// </summary>
        /// <remarks>
        /// The other half of the same idea. The situations above ask what an effect does when the
        /// object it names is somewhere else; this asks what it does when the *resource* it reaches
        /// for is not there at all. An empty library is a real game state that a player survives
        /// until they next have to draw (CR 104.3c), and an empty hand is ordinary by the end of
        /// most turns.
        /// </remarks>
        NothingAvailable,
    }

    private static readonly Situation[] Variations =
        [.. Enum.GetValues<Situation>()];

    /// <summary>One game, one card, and the effect run against it.</summary>
    private static void RunOnce(
        IEffect effect, CompiledPool pool, Situation how, ImmutableList<TargetSpec> specs)
    {
        var alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var bob = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var game = Game.Start(
            Guid.NewGuid(),
            [
                new PlayerSetup(alice, "Alice", 20, TestDeck(alice)),
                new PlayerSetup(bob, "Bob", 20, TestDeck(bob)),
            ],
            new GameRandom(1),
            startingPlayerId: alice,
            abilities: pool);

        game.BeginPlay(withMulligans: false);

        var subject = game.Create(alice, Bear, Zone.Battlefield);
        var theirs = game.Create(bob, Bear, Zone.Battlefield);

        var spare = game.Create(alice, Bear, Zone.Battlefield);
        var inGraveyard = game.Move(spare, Zone.Graveyard, MoveCause.Destroy, alice);

        if (how == Situation.SubjectInGraveyard)
            subject = game.Move(subject, Zone.Graveyard, MoveCause.Destroy, alice);

        // Built from the card's own declared targets, one for one, rather than from a guessed
        // list. The first version alternated permanents and players and reported 156 failures in
        // two effect shapes - all of them a draw or a life change reading a permanent where the
        // card had declared a player. None of that was reachable: a sweep of the corpus for a
        // draw or life effect whose target index names a non-player found **zero**, so every one
        // of those failures was about a game the engine cannot produce. A check that invents its
        // own situations reports its own inventions.
        // The same object list either way; what changes is where the object turns out to be.
        // A permanent target that has gone to a graveyard, and a graveyard target that is on the
        // battlefield, are both things a real game produces between choosing and resolving.
        var moved = how == Situation.TargetsMoved;

        if (how == Situation.NothingAvailable)
        {
            // Emptied by moving, so the state stays one the reducer built rather than one the
            // test assembled behind it.
            foreach (var card in game.State.GetPlayer(alice).Hand)
                game.Move(card, Zone.Graveyard, MoveCause.Discard, alice);

            foreach (var card in game.State.GetPlayer(alice).Library)
                game.Move(card, Zone.Graveyard, MoveCause.Other, alice);
        }

        var targets = how is Situation.Targeted or Situation.TargetsMoved
                or Situation.NothingAvailable
            ? ImmutableList.CreateRange(specs.Select(spec => spec.Kind switch
            {
                TargetKind.Player => Target.ToPlayer(bob),
                TargetKind.Permanent => Target.ToPermanent(moved ? inGraveyard : theirs),

                TargetKind.CardInGraveyard => Target.ToCard(moved ? theirs : inGraveyard),

                // Nothing is on the stack in this game, so a spell target names an object that is
                // not there. That is a real situation - a target can stop being legal between
                // being chosen and the spell resolving (CR 608.2b) - and an effect has to survive
                // it rather than assume its spell is still around.
                _ => Target.ToSpell(inGraveyard),
            }))
            : ImmutableList<Target>.Empty;

        var context = new ResolutionContext
        {
            State = game.State,
            Abilities = pool,
            ControllerId = alice,
            SourceId = subject,
            Targets = targets,
            SubjectPlayer = how == Situation.NoSubject ? null : bob,
            SubjectObject = how == Situation.NoSubject ? null : subject,
            SubjectAmount = how == Situation.NoSubject ? null : 1,
        };

        var state = game.State;

        foreach (var produced in effect.Resolve(context))
            state = GameReducer.Apply(state, produced);
    }

    private static readonly Domain.Models.CardDefinition Bear = new()
    {
        OracleId = "oracle-invariant-bear",
        Name = "Invariant Bear",
        CardTypes = Domain.Enums.CardType.Creature,
        Power = 2,
        Toughness = 2,
    };

    private static ImmutableList<Domain.Models.CardDefinition> TestDeck(Guid who) =>
        [.. Enumerable.Range(0, 12).Select(i => new Domain.Models.CardDefinition
        {
            OracleId = $"oracle-invariant-filler-{who}-{i}",
            Name = $"Invariant Filler {i}",
            CardTypes = Domain.Enums.CardType.Land,
        })];

    /// <summary>
    /// Every effect, paired with the target list it was compiled against.
    /// </summary>
    /// <remarks>
    /// The pairing is the point. An effect's target index means nothing without the specs it was
    /// numbered against, and a check that supplies targets of its own choosing is testing a game
    /// that does not exist.
    /// </remarks>
    private static IEnumerable<(IEffect Effect, ImmutableList<TargetSpec> Specs)> EveryEffect(
        CompiledCard compiled)
    {
        if (compiled.Spell is { } spell)
        {
            foreach (var effect in Flatten(spell.Effects))
                yield return (effect, spell.Targets);
        }

        foreach (var trigger in compiled.Triggers)
        {
            foreach (var effect in Flatten(trigger.Effects))
                yield return (effect, trigger.Targets);
        }

        foreach (var ability in compiled.Activated)
        {
            foreach (var effect in Flatten(ability.Effects))
                yield return (effect, ability.Targets);
        }
    }

    /// <summary>
    /// Every compiled replacement must answer whether it applies, and produce events that apply,
    /// for the arrival it was written about.
    /// </summary>
    /// <remarks>
    /// The resolution check beside this one covers effects. Replacements are the other half of the
    /// library and nothing exercised them, which is the same gap that let a hardcoded zone sit in
    /// an effect for months — and replacements are the harder half to notice, because one that
    /// declines to apply looks exactly like a card that had nothing to say.
    /// <para>
    /// Each is asked about a permanent arriving on the battlefield, which is what almost every
    /// compiled replacement is written for, and the events it returns are pushed through the
    /// reducer. A replacement that does not apply passes trivially; the point is that none of them
    /// throws, and that what they emit is a move the engine will accept.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_compiled_replacement_applies_to_an_arrival_without_throwing()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present — skipping.");
            return;
        }

        var pool = new CompiledPool();
        var faults = new Dictionary<string, (int Count, string Example)>(StringComparer.Ordinal);
        var asked = 0;
        var declines = 0;
        var applied = 0;

        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);
            if (compiled.Replacements.IsEmpty)
                continue;

            foreach (var replacement in compiled.Replacements)
            {
                asked++;

                if (replacement.Decline is not null)
                    declines++;

                try
                {
                    if (RunReplacement(replacement, card, pool))
                        applied++;
                }
                catch (Exception ex) when (ex is InvalidOperationException
                    or NullReferenceException
                    or ArgumentException
                    or KeyNotFoundException
                    or IndexOutOfRangeException)
                {
                    var shape = $"{replacement.Id}: {ex.GetType().Name}";
                    var seen = faults.GetValueOrDefault(shape);
                    faults[shape] = (
                        seen.Count + 1,
                        seen.Example ?? $"{card.Name}: {ex.Message}");
                }
            }
        }

        output.WriteLine(
            $"asked {asked} replacements; {applied} applied to an arrival; "
            + $"{declines} also say what declining means");

        // Without this the declined branch above is a check that cannot fail: a corpus with none
        // of these in it would run none of them and report green. Eleven shocklands carry one.
        Assert.True(
            declines >= 10,
            $"only {declines} replacements carry a decline branch; the corpus has 11.");

        foreach (var (shape, (count, example)) in faults.OrderByDescending(p => p.Value.Count))
            output.WriteLine($"  {count,6}  {shape}  e.g. {example}");

        Assert.True(faults.Count == 0, $"{faults.Count} replacement shapes threw; see output");
    }

    /// <summary>Puts the card on the battlefield and offers the arrival to the replacement.</summary>
    private static bool RunReplacement(
        ReplacementEffectDefinition replacement, Domain.Models.CardDefinition card, CompiledPool pool)
    {
        var alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var bob = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var game = Game.Start(
            Guid.NewGuid(),
            [
                new PlayerSetup(alice, "Alice", 20, TestDeck(alice)),
                new PlayerSetup(bob, "Bob", 20, TestDeck(bob)),
            ],
            new GameRandom(1),
            startingPlayerId: alice,
            abilities: pool);

        game.BeginPlay(withMulligans: false);

        var arriving = game.Create(alice, card, Zone.Battlefield);

        // A card with two arrival replacements does not simply arrive: CR 616.1 gives its
        // controller the choice of which applies first, and the engine raises that question
        // before the permanent exists. Answering it is part of putting a card onto the
        // battlefield, and a harness that skipped it looked up an object that was not there yet
        // and reported six perfectly correct cards as broken.
        while (game.State.Choice is { } pending)
            game.Choose(pending.PlayerId, [pending.Options[0].Id]);

        var source = game.State.GetObject(arriving);

        // The event the replacement is written about: this very object arriving. Built by hand
        // rather than captured, because by the time the object exists the arrival has happened.
        var arrival = new ObjectCreated(arriving, card, alice, alice, Zone.Battlefield);

        if (!replacement.Applies(arrival, game.State, source))
            return false;

        var state = game.State;
        foreach (var produced in replacement.Replace(arrival, state, source))
        {
            if (produced is ObjectCreated)
                continue;

            state = GameReducer.Apply(state, produced);
        }

        // The other half of an optional replacement. A shockland's two answers are two different
        // arrivals - pay and stand up, or decline and come in tapped - so the declined branch is
        // as much of the card as the applied one, and a check that only ever applied would have
        // left half of every such effect unexercised. Written the moment the hook was added, for
        // exactly the reason the corpus checks exist: the applied half is what a behaviour test
        // drives, and the declined half is what nothing would.
        if (replacement.Decline is { } declined)
        {
            var afterDecline = game.State;
            foreach (var produced in declined(arrival, afterDecline, source))
            {
                if (produced is ObjectCreated)
                    continue;

                afterDecline = GameReducer.Apply(afterDecline, produced);
            }
        }

        return true;
    }
    /// <summary>
    /// Every type name a card uses to refer to itself is one the compiler understands.
    /// </summary>
    /// <remarks>
    /// A card says "this Saga", "this Vehicle", "this Spacecraft" where an older one said its
    /// own name. The compiler turns those into <c>~</c> from a list, and a name missing from that
    /// list does not fail: the line simply stops being about the card, so an ordinary enters
    /// trigger goes unread on every card of that type at once. Eight names were missing when this
    /// was written - Spacecraft, Contraption, Siege, Case, Attraction, Planet, Conspiracy,
    /// Realm - and the only symptom was that newer cards were quietly harder to read than older
    /// ones saying the same thing.
    /// <para>
    /// A name only counts as a self-reference when the card <em>is</em> one, which is what keeps
    /// this from demanding the whole creature-type table: "destroy target Zombie" says a type the
    /// card need not have, and "this Zombie" on a card that is not a Zombie does not occur.
    /// </para>
    /// <para>
    /// This is the third vocabulary list in the compiler to be checked against the corpus rather
    /// than trusted. Every one of them went stale first.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_type_a_card_calls_itself_by_is_understood()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present - skipping.");
            return;
        }

        var known = CardCompiler.SelfReferenceTypeNames
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = new Dictionary<string, (int Lines, string Example)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var card in corpus)
        {
            var text = card.OracleText;
            if (string.IsNullOrEmpty(text))
                continue;

            var mine = card.Subtypes.ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (System.Text.RegularExpressions.Match found in
                SelfReferenceCandidate().Matches(text))
            {
                var name = found.Groups["type"].Value;

                if (known.Contains(name) || !mine.Contains(name))
                    continue;

                var seen = missing.GetValueOrDefault(name);
                missing[name] = (seen.Lines + 1, seen.Example ?? card.Name);
            }
        }

        Assert.True(
            missing.Count == 0,
            "cards refer to themselves by a type the compiler does not know:\n"
                + string.Join(
                    "\n",
                    missing
                        .OrderByDescending(p => p.Value.Lines)
                        .Select(p => $"  {p.Value.Lines,5}  \"this {p.Key}\"  e.g. {p.Value.Example}")));
    }

    /// <summary>"this Spacecraft" - a card naming a type, which may or may not be its own.</summary>
    private static readonly System.Text.RegularExpressions.Regex SelfReferenceCandidateRegex =
        new(@"\bthis (?<type>[A-Z][a-z]+)\b");

    private static System.Text.RegularExpressions.Regex SelfReferenceCandidate() =>
        SelfReferenceCandidateRegex;
    /// <summary>
    /// A prefix the compiler treats as structural is not stripped as flavour (CR 207.2c).
    /// </summary>
    /// <remarks>
    /// An ability word is recognised by its shape - a capitalised phrase before an em dash -
    /// because there are 579 distinct ones in the corpus and a list of them would be stale by
    /// the next set. The cost of a shape is that other things have it: a Saga's chapter symbol
    /// "III" is three capitals before a dash, and a Case says "To solve -" and "Solved -".
    /// Every one of those was stripped as flavour, and each time the symptom was a card that
    /// compiled with its meaning quietly removed.
    /// <para>
    /// Twice was a pattern, so the exclusions are declared in one place and the stripper is
    /// built from them. This is what checks that the two have not come apart.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_structural_prefix_is_not_stripped_as_flavour()
    {
        foreach (var prefix in CardCompiler.StructuralPrefixes)
        {
            var card = new MtgEngine.Domain.Models.CardDefinition
            {
                OracleId = "prefix-" + prefix,
                Name = "Prefix Probe",
                OracleText = prefix + " — you gain 1 life.",
                CardTypes = MtgEngine.Domain.Enums.CardType.Enchantment,
            };

            var line = Assert.Single(CardCompiler.Lines(card));

            Assert.StartsWith(prefix, line, StringComparison.Ordinal);
        }
    }

    /// <summary>A Saga chapter symbol survives the same stripper, for the same reason.</summary>
    [Theory]
    [InlineData("I")]
    [InlineData("II")]
    [InlineData("III")]
    [InlineData("IV")]
    [InlineData("I, II")]
    [InlineData("II, III")]
    public void A_chapter_symbol_is_not_stripped_as_flavour(string chapter)
    {
        var card = new MtgEngine.Domain.Models.CardDefinition
        {
            OracleId = "chapter-" + chapter,
            Name = "Chapter Probe",
            OracleText = chapter + " — you gain 1 life.",
            CardTypes = MtgEngine.Domain.Enums.CardType.Enchantment,
            Subtypes = ["Saga"],
        };

        var line = Assert.Single(CardCompiler.Lines(card));

        Assert.StartsWith(chapter, line, StringComparison.Ordinal);
    }
    /// <summary>
    /// Every "when you unlock this door" trigger in the corpus answers an unlock (CR 709.5f).
    /// </summary>
    /// <remarks>
    /// This one guards a hole the compiler deliberately digs. The unlock trigger is compiled with
    /// a predicate that answers <em>nothing</em>, because the line alone does not say which of a
    /// Room's two doors it belongs to - only the Room path knows that, and it replaces the
    /// predicate as it merges the halves.
    /// <para>
    /// If a card ever carries that line without going down the Room path - a single-faced card, a
    /// face whose subtypes are read differently, a Room printed in some new shape - the trigger
    /// compiles, the card counts as fully read, and the ability silently never fires. That is the
    /// worst failure this project has: a card that looks understood and does nothing.
    /// </para>
    /// <para>
    /// The dead-trigger sweep beside this cannot catch it, because its sample events do not
    /// include an unlock. Absence from that report is not evidence, which is why this asks
    /// directly.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_unlock_trigger_answers_an_unlock()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present - skipping.");
            return;
        }

        var pool = new CompiledPool();
        var alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var bob = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var game = Game.Start(
            Guid.NewGuid(),
            [
                new PlayerSetup(alice, "Alice", 20, SmokeDeck("Alice")),
                new PlayerSetup(bob, "Bob", 20, SmokeDeck("Bob")),
            ],
            new GameRandom(1),
            startingPlayerId: alice,
            abilities: pool);

        game.BeginPlay(withMulligans: false);

        var dead = new List<string>();
        var live = 0;

        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);

            foreach (var trigger in compiled.Triggers.Where(t => t.OpensDoor))
            {
                // Built rather than played onto the battlefield: an unlock predicate is asked
                // about the event and its own source and nothing else, and putting 30 Rooms into
                // one game only invites a state-based action to take them away again.
                var subject = new GameObject
                {
                    Id = ObjectId.New(),
                    Card = card,
                    Zone = Zone.Battlefield,
                    OwnerId = alice,
                    ControllerId = alice,
                    Timestamp = 1,
                    Permanent = new PermanentState(),
                };

                var source = new TriggerSource(subject, pool);

                var answers = Enumerable.Range(0, 4).Any(door =>
                    trigger.Triggers(new HalfUnlocked(subject.Id, door), game.State, source));

                if (answers)
                    live++;
                else
                    dead.Add($"{card.Name}: {trigger.Text}");
            }
        }

        output.WriteLine($"{live} unlock triggers answer an unlock");

        Assert.True(
            dead.Count == 0,
            "these unlock triggers never fire, so the card reads as understood and does "
                + $"nothing:\n  " + string.Join("\n  ", dead.Take(20)));

        // A guard over an empty set is not a guard. If Rooms stop compiling at all, this says so
        // rather than passing quietly.
        Assert.True(live > 0, "no unlock triggers compiled at all, so nothing was checked.");
    }
    /// <summary>
    /// Every noun phrase a complete card prints, once the compiler's own noun grammar has read
    /// it, has to describe something a board can actually contain (CR 205.3).
    /// </summary>
    /// <remarks>
    /// Aimed at the failure this project is least able to see: a card that compiles as
    /// <em>complete</em> and then does nothing, or half of what it prints. Four defects of that
    /// shape have shipped and every one was found by hand rather than by a test —
    /// "you gain 1 life for each Equipment you control" gaining 0, because an unknown capitalised
    /// noun resolved to a creature type; "Artifact creatures you control get +1/+1" becoming a
    /// lord for the creature type "Artifact", 129 cards; a mass static with no ownership clause
    /// pumping only its controller's half of the board, 83 cards; and a <c>+X/+Y</c> counter on a
    /// group read as <c>+1/+1</c>, 3 cards live and wrong. None of them failed anything, because
    /// what selects the permanents is a closure and nothing static can read one.
    /// <para>
    /// So this runs the closure. Every noun phrase a <em>complete</em> card actually prints is
    /// pulled out of the corpus and handed to <c>Specs.Parse</c> / <c>Specs.ParseGroup</c> — the
    /// compiler's own noun grammar, not a second copy of it — and, when it reads into a filter
    /// over permanents, tried against a board built to hold anything the game can: a real-shaped
    /// permanent for every subtype the rules define, a plain permanent of each card type, one
    /// that is every type at once, in and out of combat, tapped and untapped, coloured and
    /// colourless, token and not, snow, basic, face down, attached and bare, all under both
    /// players. A filter that nothing on that board answers is one no game can ever satisfy, and
    /// the card carrying it is legal, playable and silently inert.
    /// </para>
    /// <para>
    /// 839 distinct printed noun phrases today: 216 the noun grammar does not read at all — which
    /// is not a fault, because an unread line is refused and refusing is the honest answer — 10
    /// that read into something other than a filter over permanents, 601 that read and are
    /// satisfiable, and 12 that read and are not. About 30 seconds, most of it compiling the
    /// corpus, which every invariant in this file pays anyway.
    /// </para>
    /// <para>
    /// The witness board is half the instrument, and the half that decides whether the answer
    /// means anything. An earlier build of it reported 53, and every one of the 41 it then lost
    /// was the board rather than the compiler: it could not hold an attacking Goblin, a snow
    /// permanent, a tapped colourless creature, or an Army — a creature type no playable card is
    /// printed with, because the only way to have one is to amass it.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_printed_noun_phrase_the_grammar_reads_can_be_satisfied()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present — skipping.");
            return;
        }

        // Only what a card that reads as *complete* prints. An unread card is refused by the
        // legality gate and says so; this whole invariant is about the other kind.
        var printed = new Dictionary<string, (bool Group, string Card)>(StringComparer.Ordinal);
        var tokens = new List<(string Subtype, MtgEngine.Domain.Enums.CardType Types)>();

        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);
            if (!compiled.IsComplete)
                continue;

            tokens.AddRange(TokenShapes(compiled));

            foreach (var line in CardCompiler.Lines(card))
            {
                foreach (var (group, phrase) in NounPhrases(line))
                    printed.TryAdd((group ? "group|" : "target|") + phrase, (group, card.Name));
            }
        }

        // What subtypes exist is a question the rules answer and the corpus only samples: a
        // token type nothing is printed with is still a thing a board can hold.
        var defined = RulesSubtypes();

        Assert.True(
            defined.Count > 250,
            $"only {defined.Count} subtypes were read out of CR 205.3, which is far too few — the "
                + "rules file has changed shape and the witness board is quietly missing most of "
                + "the game's types.");

        var board = new WitnessBoard(corpus, [.. tokens, .. defined]);

        var unread = 0;
        var elsewhere = 0;
        var satisfiable = 0;
        var survivors = new List<string>();

        foreach (var entry in printed.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            var phrase = entry.Key[(entry.Key.IndexOf('|', StringComparison.Ordinal) + 1)..];

            TargetSpec? spec;
            try
            {
                spec = entry.Value.Group
                    ? EffectPhrase.Specs.ParseGroup(phrase)
                    : EffectPhrase.Specs.Parse("target " + phrase);
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                survivors.Add($"{entry.Value.Card}: \"{phrase}\" threw {ex.GetType().Name}");
                continue;
            }

            if (spec is null)
            {
                unread++;
                continue;
            }

            if (spec.Kind != TargetKind.Permanent)
            {
                elsewhere++;
                continue;
            }

            if (board.Satisfies(spec, phrase))
                satisfiable++;
            else
                survivors.Add($"{entry.Value.Card}: \"{phrase}\" -> {spec.Description}");
        }

        output.WriteLine($"printed noun phrases: {printed.Count}");
        output.WriteLine($"  not read by the noun grammar: {unread}");
        output.WriteLine($"  read, but not a filter over permanents: {elsewhere}");
        output.WriteLine($"  read as a permanent filter, satisfiable: {satisfiable}");
        output.WriteLine($"  read as a permanent filter, unsatisfiable: {survivors.Count}");
        output.WriteLine($"largest witness board: {board.Size} permanents");

        foreach (var survivor in survivors)
            output.WriteLine("  " + survivor);

        Assert.True(
            survivors.Count <= UnsatisfiablePhrases,
            $"{survivors.Count} printed noun phrases compile to a filter nothing on a board can "
                + $"satisfy, where {UnsatisfiablePhrases} were recorded:\n  "
                + string.Join("\n  ", survivors.Take(60)));

        // A ratchet is only a ratchet while it is tight. If the count drops, the number here is
        // stale and the next regression hides under the slack.
        Assert.True(
            survivors.Count == UnsatisfiablePhrases,
            $"only {survivors.Count} phrases are unsatisfiable now, not {UnsatisfiablePhrases}. "
                + "Lower the recorded number in the same commit that fixed them.");
    }

    /// <summary>
    /// How many printed noun phrases the grammar currently reads into an unsatisfiable filter.
    /// </summary>
    /// <remarks>
    /// A bare number, and the weakest of the three ways this could have been left. Zero is not
    /// true today, and a rule about the <em>kind</em> of survivor would be worse than the number:
    /// all twelve are live defects in the noun grammar rather than states the witness board
    /// cannot build, so any such rule would amount to asserting that the bug is acceptable. The
    /// number records what is true, and the test prints the twelve in full on every run.
    /// <para>
    /// It found twelve, and ten of those are now fixed. Two faults accounted for them.
    /// <para>
    /// <c>SingularWord</c> over-reached on eight: "Caves" folded to "Caf" and "Detectives" to
    /// "Detectif" by a blanket <c>-ves</c> rule added to rescue "Elves", "Faeries" to "Faery" by a
    /// blanket <c>-ies</c> rule, and "Locus" and "Pegasus" lost a letter for being plurals they
    /// are not. Both blanket rules are gone: the subtypes whose plural really changes the stem are
    /// a small closed set and are now listed, a word ending in "us" is left alone, and "Aurochs"
    /// joined "Plains" as spelled the same either way. That last one was <em>caused</em> by the
    /// second fix and caught here immediately, which is the argument for this guard in one line.
    /// </para>
    /// <para>
    /// On two more the group grammar looked for its noun only at the front of the phrase, so a
    /// single lowercase adjective stopped the search before it arrived: "untapped Mountains you
    /// control" and "tapped Assassins you control" kept the s and asked for creature types spelled
    /// that way. Ben-Ben dealt damage equal to the number of "Mountains" and Lydia Frye surveilled
    /// per "Assassins" — both counting zero, both compiling as complete cards. The run is now
    /// found wherever it starts.
    /// </para>
    /// <para>
    /// The two that remain need something this layer does not have. "Commanders you control" names
    /// a designation rather than a creature type, and "Equipped creatures you control" is a
    /// <em>sentence-initial</em> adjective that the capital-letter heuristic cannot tell from a
    /// subtype. Both are residue of the founding bug — <c>SubtypeCardType</c> says which card type
    /// a <em>known</em> subtype implies and still defaults everything else to Creature — and
    /// closing them wants a subtype dictionary, which lives outside <c>MtgEngine.Rules</c>.
    /// </para>
    /// <para>
    /// Asserted from both sides on purpose. A ceiling alone leaves slack, and slack is exactly
    /// how an instrument in this repository stops measuring without anyone noticing. Fix the
    /// singulariser and this has to come down in the same commit; print a new card that trips the
    /// same fault and it has to be looked at rather than absorbed.
    /// </para>
    /// </remarks>
    private const int UnsatisfiablePhrases = 2;

    /// <summary>The words that end a printed noun phrase rather than belonging to it.</summary>
    /// <remarks>
    /// Verbs, the ownership clauses' own words, the keywords that introduce a noun without being
    /// part of it, and the conjunctions and prepositions that start the next clause. Deliberately
    /// generous about what it lets through: an over-long phrase is refused by the grammar and
    /// lands in the "not read" pile, which costs nothing, while a phrase cut short would be
    /// checked as a different phrase from the one the card prints.
    /// </remarks>
    private static readonly HashSet<string> PhraseStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "abilities", "ability", "an", "and", "another", "are", "as", "at", "attack",
        "attacks", "be", "became", "become", "becomes", "been", "being", "block", "blocks",
        "but", "by", "can", "cannot", "control", "controlled", "controller", "controls",
        "deal", "dealt", "deals", "die", "dies", "do", "does", "done", "down", "during",
        "each", "enchant", "enter", "enters", "equip", "every", "for", "from", "gain",
        "gained", "gains", "get", "gets", "had", "has", "have", "he", "if", "in", "instead",
        "into", "is", "it", "its", "may", "must", "name", "named", "of", "off", "on", "only",
        "onto", "or", "other", "out", "over", "player", "players", "put", "puts", "she",
        "target", "than", "that", "the", "their", "them", "then", "there", "these", "they",
        "this", "those", "to", "under", "unless", "until", "up", "when", "whenever", "where",
        "which", "while", "who", "whose", "with", "without", "you", "your",
    };

    /// <summary>The three ownership clauses the group grammar reads, already tokenised.</summary>
    private static readonly string[][] OwnershipClauses =
    [
        ["you", "control"],
        ["your", "opponents", "control"],
        ["an", "opponent", "controls"],
    ];

    /// <summary>Words and single punctuation marks, which is all this needs to find a noun run.</summary>
    private static readonly System.Text.RegularExpressions.Regex PhraseTokenRegex =
        new(@"[A-Za-z][A-Za-z'’-]*|[^A-Za-z\s]");

    /// <summary>
    /// Every noun phrase one printed line names, and which of the two grammars reads it.
    /// </summary>
    /// <remarks>
    /// Anchored on the words that introduce a group or a target — "all", "each", "every",
    /// "other", "target" — plus the bare ownership clause, which carries no opener at all and is
    /// how every mass static names the group it applies to.
    /// </remarks>
    private static IEnumerable<(bool Group, string Phrase)> NounPhrases(string line)
    {
        var tokens = new List<string>();
        foreach (System.Text.RegularExpressions.Match token in PhraseTokenRegex.Matches(line))
            tokens.Add(token.Value);

        for (var i = 0; i < tokens.Count; i++)
        {
            var opener = tokens[i];
            var group = opener.Equals("all", StringComparison.OrdinalIgnoreCase)
                || opener.Equals("each", StringComparison.OrdinalIgnoreCase)
                || opener.Equals("every", StringComparison.OrdinalIgnoreCase)
                || opener.Equals("other", StringComparison.OrdinalIgnoreCase);

            if (!group && !opener.Equals("target", StringComparison.OrdinalIgnoreCase))
                continue;

            var run = NounRun(tokens, i + 1, out var after);
            if (run.Length == 0)
                continue;

            var owner = OwnershipAt(tokens, after);
            yield return (group, owner is null ? run : run + " " + owner);
        }

        // "Slivers you control", with nothing in front of it: the commonest group phrase there
        // is, and the shape both mass-static defects were printed in.
        for (var i = 0; i < tokens.Count; i++)
        {
            if (OwnershipAt(tokens, i) is not { } clause)
                continue;

            var run = NounRunBackwards(tokens, i - 1);
            if (run.Length > 0)
                yield return (true, run + " " + clause);
        }
    }

    /// <summary>The run of ordinary words starting at <paramref name="start"/>.</summary>
    private static string NounRun(IReadOnlyList<string> tokens, int start, out int after)
    {
        var words = new List<string>();
        var i = start;

        while (i < tokens.Count && words.Count < 4)
        {
            var word = tokens[i];
            if (!char.IsLetter(word[0]) || PhraseStopWords.Contains(word))
                break;

            words.Add(word);
            i++;
        }

        after = i;
        return string.Join(' ', words);
    }

    /// <summary>The same run, read backwards from the word before an ownership clause.</summary>
    private static string NounRunBackwards(IReadOnlyList<string> tokens, int last)
    {
        var words = new List<string>();

        for (var i = last; i >= 0 && words.Count < 4; i--)
        {
            var word = tokens[i];
            if (!char.IsLetter(word[0]) || PhraseStopWords.Contains(word))
                break;

            words.Insert(0, word);
        }

        return string.Join(' ', words);
    }

    /// <summary>The ownership clause starting at a token index, or null.</summary>
    private static string? OwnershipAt(IReadOnlyList<string> tokens, int at)
    {
        foreach (var clause in OwnershipClauses)
        {
            if (at < 0 || at + clause.Length > tokens.Count)
                continue;

            var matched = true;
            for (var k = 0; k < clause.Length; k++)
            {
                if (!tokens[at + k].Equals(clause[k], StringComparison.OrdinalIgnoreCase))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
                return string.Join(' ', clause);
        }

        return null;
    }

    /// <summary>
    /// Every subtype a compiled card can put onto the battlefield, and what card type it is on.
    /// </summary>
    /// <remarks>
    /// The corpus is cards, and a token is not a card — Scryfall's token layouts are dropped
    /// before the compiler ever sees them. So "Army", "Blinkmoth" and "Incubator" are subtypes no
    /// corpus card is printed with and that a real board is full of, and a witness board built
    /// from printed subtypes alone accuses every card that names one. They are read off the token
    /// definitions the compiler itself builds, which is the only list of them that cannot go
    /// stale as sets are added.
    /// </remarks>
    private static IEnumerable<(string Subtype, MtgEngine.Domain.Enums.CardType Types)> TokenShapes(
        CompiledCard compiled)
    {
        foreach (var effect in EveryCompiledEffect(compiled))
        {
            foreach (var property in effect.GetType().GetProperties())
            {
                if (property.PropertyType != typeof(MtgEngine.Domain.Models.CardDefinition)
                    || property.GetValue(effect) is not MtgEngine.Domain.Models.CardDefinition token)
                {
                    continue;
                }

                foreach (var subtype in token.Subtypes)
                    yield return (subtype, token.CardTypes);
            }
        }
    }

    /// <summary>
    /// Every subtype the rules themselves define, and the card type it belongs to (CR 205.3).
    /// </summary>
    /// <remarks>
    /// The corpus only samples this. Scryfall's token layouts are dropped before the compiler
    /// sees them, so "Army" and "Blinkmoth" are creature types no playable card is printed with
    /// and that a real board is full of — one is amassed (CR 701.44a), the other is a land that
    /// animates itself. A witness board built from printed subtypes alone accuses every card that
    /// names one, and the accusation is about Scryfall rather than about the compiler.
    /// <para>
    /// Read from the same file the <c>/api/rules</c> endpoint serves, and by rule number rather
    /// than by a table written here: which card type a subtype belongs to is CR 205.3g through
    /// 205.3q and nothing else. That also settles the question the four bugs turned on —
    /// "Equipment" is an artifact type because 205.3g says so, and so no witness anywhere on this
    /// board is a creature with it.
    /// </para>
    /// </remarks>
    private static List<(string Subtype, MtgEngine.Domain.Enums.CardType Types)> RulesSubtypes()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Knowledge", "comprehensive-rules.txt");
        var found = new List<(string, MtgEngine.Domain.Enums.CardType)>();

        if (!File.Exists(path))
            return found;

        foreach (var line in File.ReadLines(path))
        {
            if (SubtypeRule(line) is not { } types)
                continue;

            // "The artifact types are Attraction (see rule 717), Blood, ... and Space." — and for
            // the battle types, a single one written "That battle type is Siege."
            var plural = line.IndexOf(" types are ", StringComparison.Ordinal);
            var singular = line.IndexOf(" type is ", StringComparison.Ordinal);

            var listing = plural >= 0
                ? line[(plural + " types are ".Length)..]
                : singular >= 0 ? line[(singular + " type is ".Length)..] : null;

            if (listing is null)
                continue;

            // The land types are followed by a second sentence naming the basic ones, and the
            // creature types by nothing at all. Either way the list ends at the first full stop.
            var stop = listing.IndexOf(". ", StringComparison.Ordinal);
            if (stop >= 0)
                listing = listing[..stop];

            // "All other creature types are one word long: Advisor, ..." — the colon, where there
            // is one, is where the prose stops and the list starts.
            var colon = listing.LastIndexOf(": ", StringComparison.Ordinal);
            if (colon >= 0)
                listing = listing[(colon + 2)..];

            foreach (var item in listing.Split(','))
            {
                var name = SeeRule().Replace(item, string.Empty).Trim().TrimEnd('.').Trim();

                if (name.StartsWith("and ", StringComparison.Ordinal))
                    name = name[4..];

                if (SubtypeName().IsMatch(name))
                    found.Add((name, types));
            }
        }

        return found;
    }

    /// <summary>Which card type CR 205.3's subtype lists belong to, by rule number.</summary>
    private static MtgEngine.Domain.Enums.CardType? SubtypeRule(string line) =>
        line.StartsWith("205.3g", StringComparison.Ordinal) ? MtgEngine.Domain.Enums.CardType.Artifact
        : line.StartsWith("205.3h", StringComparison.Ordinal) ? MtgEngine.Domain.Enums.CardType.Enchantment
        : line.StartsWith("205.3i", StringComparison.Ordinal) ? MtgEngine.Domain.Enums.CardType.Land
        : line.StartsWith("205.3j", StringComparison.Ordinal) ? MtgEngine.Domain.Enums.CardType.Planeswalker
        : line.StartsWith("205.3m", StringComparison.Ordinal) ? MtgEngine.Domain.Enums.CardType.Creature
        : line.StartsWith("205.3q", StringComparison.Ordinal) ? MtgEngine.Domain.Enums.CardType.Battle
        : null;

    /// <summary>The cross-reference the rules put beside a subtype that has its own section.</summary>
    private static readonly System.Text.RegularExpressions.Regex SeeRuleRegex =
        new(@"\s*\(see rule[^)]*\)");

    private static System.Text.RegularExpressions.Regex SeeRule() => SeeRuleRegex;

    /// <summary>A subtype as the rules spell one: capitalised letters, hyphens and apostrophes.</summary>
    private static readonly System.Text.RegularExpressions.Regex SubtypeNameRegex =
        new(@"^[A-Z][A-Za-z'’\-]*$");

    private static System.Text.RegularExpressions.Regex SubtypeName() => SubtypeNameRegex;

    /// <summary>Every effect a compiled card runs, from wherever it hangs, nested ones included.</summary>
    private static IEnumerable<IEffect> EveryCompiledEffect(CompiledCard compiled)
    {
        var effects = new List<IEffect>();

        if (compiled.Spell is { } spell)
        {
            effects.AddRange(spell.Effects);
            effects.AddRange(spell.Modes.SelectMany(m => m.Effects));
        }

        effects.AddRange(compiled.Triggers.SelectMany(t => t.Effects));
        effects.AddRange(compiled.Activated.SelectMany(a => a.Effects));

        return Flatten(effects);
    }

    /// <summary>One permanent's worth of status, varied so conjunctions have a single witness.</summary>
    private sealed record Flavour(
        string Name,
        IReadOnlyList<MtgEngine.Domain.Enums.ManaColor> Colors,
        MtgEngine.Domain.Enums.KeywordAbility Keywords,
        int Power,
        int Toughness,
        int Cmc,
        bool Token,
        bool Legendary,
        bool Tapped,
        bool Sick,
        bool Counters,
        bool Decorated,
        bool InCombat = false,
        bool Blocked = false);

    /// <summary>
    /// A board carrying one of everything the game can contain, for a phrase to be tried against.
    /// </summary>
    /// <remarks>
    /// The instrument the invariant above turns on, and the half that decides whether its answer
    /// means anything: a board that cannot hold a tapped multicoloured legendary Sliver accuses
    /// every card that names one. The first build of this reported 53 unsatisfiable phrases and
    /// most of the difference between that and the real number was here — attacking permanents of
    /// a named subtype, the snow supertype, and the token-only creature types together accounted
    /// for two thirds of them.
    /// <para>
    /// Built by hand rather than played into, the way <c>Game.AddCounters</c> and
    /// <c>Game.Attach</c> are: a board with an attacker, an Equipment on a creature and counters
    /// stacked up is a position, and playing four turns to reach it says nothing this is about.
    /// </para>
    /// </remarks>
    private sealed class WitnessBoard
    {
        private static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
        private static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-222222222222");
        private static readonly Guid[] Controllers = [Alice, Bob];

        private static readonly MtgEngine.Domain.Enums.ManaColor[] EveryColor =
        [
            MtgEngine.Domain.Enums.ManaColor.White,
            MtgEngine.Domain.Enums.ManaColor.Blue,
            MtgEngine.Domain.Enums.ManaColor.Black,
            MtgEngine.Domain.Enums.ManaColor.Red,
            MtgEngine.Domain.Enums.ManaColor.Green,
        ];

        private static readonly MtgEngine.Domain.Enums.ManaColor[] JustWhite =
            [MtgEngine.Domain.Enums.ManaColor.White];

        private static readonly MtgEngine.Domain.Enums.ManaColor[] Colorless = [];

        private static readonly string[] BasicLands =
            ["Plains", "Island", "Swamp", "Mountain", "Forest"];

        private static readonly string[] Snow = ["Snow"];

        /// <summary>
        /// Every keyword at once, except the one that would make this check vacuous.
        /// </summary>
        /// <remarks>
        /// Changeling is every creature type in every zone (CR 702.73a). A witness carrying it
        /// answers every subtype filter ever written, including one asking for a subtype no card
        /// has — which is precisely the fault this exists to find.
        /// </remarks>
        private static readonly MtgEngine.Domain.Enums.KeywordAbility EveryKeyword = AllKeywords();

        /// <summary>Every card-type combination a permanent has, plus one that is all of them.</summary>
        private static readonly (string Name, MtgEngine.Domain.Enums.CardType Types)[] Shapes =
        [
            ("Creature", MtgEngine.Domain.Enums.CardType.Creature),
            ("Artifact", MtgEngine.Domain.Enums.CardType.Artifact),
            ("Enchantment", MtgEngine.Domain.Enums.CardType.Enchantment),
            ("Land", MtgEngine.Domain.Enums.CardType.Land),
            ("Planeswalker", MtgEngine.Domain.Enums.CardType.Planeswalker),
            ("Battle", MtgEngine.Domain.Enums.CardType.Battle),
            ("ArtifactCreature",
                MtgEngine.Domain.Enums.CardType.Artifact | MtgEngine.Domain.Enums.CardType.Creature),
            ("EnchantmentCreature",
                MtgEngine.Domain.Enums.CardType.Enchantment | MtgEngine.Domain.Enums.CardType.Creature),
            ("LandCreature",
                MtgEngine.Domain.Enums.CardType.Land | MtgEngine.Domain.Enums.CardType.Creature),
            ("Everything",
                MtgEngine.Domain.Enums.CardType.Creature
                    | MtgEngine.Domain.Enums.CardType.Artifact
                    | MtgEngine.Domain.Enums.CardType.Enchantment
                    | MtgEngine.Domain.Enums.CardType.Land
                    | MtgEngine.Domain.Enums.CardType.Planeswalker
                    | MtgEngine.Domain.Enums.CardType.Battle),
        ];

        /// <summary>
        /// The states a permanent can be in, chosen so that the printed conjunctions each have
        /// one object answering every half of them at once.
        /// </summary>
        /// <remarks>
        /// A filter is satisfied by a single permanent or by nothing, so spreading "tapped" and
        /// "has a counter on it" across two witnesses answers "tapped creature with a +1/+1
        /// counter on it" with a false accusation. Hence a maximal witness in both tapped and
        /// untapped, a plain one in both, and three in combat rather than one flag per witness.
        /// </remarks>
        private static readonly Flavour[] Flavours =
        [
            new("plain", Colorless, MtgEngine.Domain.Enums.KeywordAbility.None,
                2, 2, 0, Token: false, Legendary: false, Tapped: false, Sick: true,
                Counters: false, Decorated: false),
            new("held", Colorless, MtgEngine.Domain.Enums.KeywordAbility.None,
                2, 2, 0, Token: false, Legendary: false, Tapped: true, Sick: false,
                Counters: false, Decorated: false),
            new("tapped", EveryColor, EveryKeyword,
                7, 7, 8, Token: false, Legendary: true, Tapped: true, Sick: false,
                Counters: true, Decorated: true),
            new("ready", EveryColor, EveryKeyword,
                7, 7, 8, Token: false, Legendary: true, Tapped: false, Sick: false,
                Counters: true, Decorated: true),
            new("token", JustWhite, MtgEngine.Domain.Enums.KeywordAbility.None,
                1, 1, 0, Token: true, Legendary: false, Tapped: false, Sick: false,
                Counters: false, Decorated: false),
            new("small", Colorless, MtgEngine.Domain.Enums.KeywordAbility.None,
                0, 1, 3, Token: false, Legendary: false, Tapped: false, Sick: false,
                Counters: true, Decorated: false),

            // In combat. One token, one plain, one maximal, and only the last of them blocked, so
            // that "unblocked attacking creature" has an answer and so does "blocking creature".
            new("raiding", JustWhite, MtgEngine.Domain.Enums.KeywordAbility.None,
                1, 1, 0, Token: true, Legendary: false, Tapped: false, Sick: false,
                Counters: false, Decorated: false, InCombat: true),
            new("charging", Colorless, MtgEngine.Domain.Enums.KeywordAbility.None,
                3, 3, 2, Token: false, Legendary: false, Tapped: false, Sick: false,
                Counters: false, Decorated: false, InCombat: true),
            new("storming", EveryColor, EveryKeyword,
                7, 7, 8, Token: false, Legendary: true, Tapped: false, Sick: false,
                Counters: true, Decorated: true, InCombat: true, Blocked: true),
        ];

        private readonly CompiledPool pool = new();
        private readonly GameState bare;
        private readonly ImmutableList<GameObject> generic;

        private readonly Dictionary<string, ImmutableHashSet<MtgEngine.Domain.Enums.CardType>> printedOn =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, (GameState State, ImmutableList<GameObject> Objects)> boards =
            new(StringComparer.Ordinal);

        /// <summary>Who is in combat, filled in as witnesses are made and read back per board.</summary>
        private readonly List<(ObjectId Id, bool Blocked)> attacking = [];
        private readonly List<ObjectId> blocking = [];

        private long stamp = 5000;

        internal WitnessBoard(
            IReadOnlyList<MtgEngine.Domain.Models.CardDefinition> corpus,
            IEnumerable<(string Subtype, MtgEngine.Domain.Enums.CardType Types)> tokens)
        {
            // Which card types a subtype is actually printed on, which is the only definition of
            // that fact there is: "Equipment" is an artifact type because every card printed with
            // it is an artifact, and no table anywhere says so.
            foreach (var card in corpus)
            {
                foreach (var subtype in card.Subtypes)
                    Record(subtype, card.CardTypes);
            }

            foreach (var (subtype, types) in tokens)
                Record(subtype, types);

            var game = Game.Start(
                Guid.NewGuid(),
                [
                    new PlayerSetup(Alice, "Alice", 20, SmokeDeck("Alice")),
                    new PlayerSetup(Bob, "Bob", 20, SmokeDeck("Bob")),
                ],
                new GameRandom(1),
                startingPlayerId: Alice,
                abilities: pool);

            game.BeginPlay(withMulligans: false);
            bare = game.State;

            generic = BuildGeneric();
        }

        /// <summary>The largest board any one phrase was tried against, for the report.</summary>
        internal int Size { get; private set; }

        /// <summary>Whether anything the game can contain answers this filter.</summary>
        internal bool Satisfies(TargetSpec spec, string phrase)
        {
            // Both what the card printed and what the grammar made of it. The two disagree
            // exactly when a plural was folded, and the board should hold whichever either meant.
            var (state, objects) = BoardFor(phrase + " " + spec.Description);

            foreach (var obj in objects)
            {
                foreach (var controller in Controllers)
                {
                    try
                    {
                        if (spec.ObjectFilter?.Invoke(state, pool, obj, controller) == false)
                            continue;

                        if (spec.SourceFilter?.Invoke(state, pool, obj, null, controller) == false)
                            continue;

                        return true;
                    }
                    catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
                    {
                        // A filter that throws when handed a witness has answered "not this one".
                    }
                }
            }

            return false;
        }

        private void Record(string subtype, MtgEngine.Domain.Enums.CardType types) =>
            printedOn[subtype] = printedOn.TryGetValue(subtype, out var already)
                ? already.Add(types)
                : [types];

        /// <summary>
        /// The board one phrase is tried against: everything generic, plus a real-shaped
        /// permanent for every capitalised word in it that some card prints as a subtype.
        /// </summary>
        /// <remarks>
        /// Cached by which subtypes it needs, because most phrases name none and the ones that do
        /// mostly name the same few. A game per phrase would be the same board and forty times
        /// the cost.
        /// </remarks>
        private (GameState State, ImmutableList<GameObject> Objects) BoardFor(string phrase)
        {
            var named = new SortedSet<string>(StringComparer.Ordinal);

            foreach (var word in phrase.Split(
                [' ', ',', '.', '"'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.Length < 2 || !char.IsUpper(word[0]))
                    continue;

                foreach (var candidate in SubtypeCandidates(word))
                {
                    if (printedOn.ContainsKey(candidate))
                        named.Add(candidate);
                }
            }

            var key = string.Join('|', named);
            if (boards.TryGetValue(key, out var already))
                return already;

            var objects = generic;
            foreach (var subtype in named)
                objects = objects.AddRange(SubtypeWitnesses(subtype));

            var here = objects.Select(o => o.Id).ToHashSet();

            var state = bare with
            {
                Objects = bare.Objects.SetItems(objects.Select(o => KeyValuePair.Create(o.Id, o))),
                Battlefield = bare.Battlefield.AddRange(objects.Select(o => o.Id)),
                Combat = BuildCombat(here),
            };

            Size = Math.Max(Size, objects.Count);

            var built = (state, objects);
            boards[key] = built;
            return built;
        }

        /// <summary>
        /// Every singular a printed plural could honestly have been.
        /// </summary>
        /// <remarks>
        /// The witness side of the singularisation, and deliberately <em>not</em> the compiler's
        /// own <c>SingularWord</c>. Mirroring that would put whatever it produced onto the board,
        /// and a check that agrees with the thing it is checking cannot fail. This offers every
        /// reading the word could have instead, so a phrase left unsatisfied is one the compiler
        /// resolved to a word no card is printed with — which is the whole fault.
        /// </remarks>
        private static IEnumerable<string> SubtypeCandidates(string word)
        {
            yield return word;

            if (word.EndsWith("ies", StringComparison.OrdinalIgnoreCase))
            {
                yield return word[..^3] + "y";
                yield return word[..^3] + "ie";
            }

            if (word.EndsWith("ves", StringComparison.OrdinalIgnoreCase))
            {
                yield return word[..^3] + "f";
                yield return word[..^3] + "fe";
            }

            if (word.EndsWith("es", StringComparison.OrdinalIgnoreCase))
            {
                yield return word[..^2];
                yield return word[..^1];
            }
            else if (word.EndsWith('s') && !word.EndsWith("ss", StringComparison.OrdinalIgnoreCase))
            {
                yield return word[..^1];
            }

            // The five English plurals no rule reaches, which the compiler also keeps as a list.
            var irregular = word.ToLowerInvariant() switch
            {
                "mice" => "Mouse",
                "geese" => "Goose",
                "children" => "Child",
                "teeth" => "Tooth",
                "feet" => "Foot",
                _ => null,
            };

            if (irregular is not null)
                yield return irregular;
        }

        /// <summary>A permanent of every card-type shape that subtype is actually printed on.</summary>
        private IEnumerable<GameObject> SubtypeWitnesses(string subtype)
        {
            foreach (var types in printedOn[subtype])
            {
                foreach (var flavour in Flavours)
                {
                    foreach (var who in Controllers)
                        yield return Make(subtype, types, [subtype], flavour, who);
                }
            }
        }

        private static MtgEngine.Domain.Enums.KeywordAbility AllKeywords()
        {
            var all = MtgEngine.Domain.Enums.KeywordAbility.None;

            foreach (var keyword in Enum.GetValues<MtgEngine.Domain.Enums.KeywordAbility>())
            {
                if (keyword != MtgEngine.Domain.Enums.KeywordAbility.Changeling)
                    all |= keyword;
            }

            return all;
        }

        private ImmutableList<GameObject> BuildGeneric()
        {
            var built = ImmutableList.CreateBuilder<GameObject>();
            var host = new Dictionary<Guid, ObjectId>();

            foreach (var shape in Shapes)
            {
                foreach (var flavour in Flavours)
                {
                    foreach (var who in Controllers)
                    {
                        var made = Make(shape.Name, shape.Types, [], flavour, who);
                        built.Add(made);

                        if (shape.Types == MtgEngine.Domain.Enums.CardType.Creature)
                            host.TryAdd(who, made.Id);
                    }
                }

                // Snow is a supertype and nothing else on this board has one, so "snow permanent
                // you control" — 200-odd corpus lines across the ice ages — has no witness at all
                // without these.
                foreach (var who in Controllers)
                {
                    built.Add(Make(shape.Name, shape.Types, [], Flavours[0], who, supertypes: Snow));
                    built.Add(Make(shape.Name, shape.Types, [], Flavours[1], who, supertypes: Snow));
                }
            }

            // The five basics: the only lands carrying a supertype nothing else has, and the only
            // answer to "basic land you control".
            foreach (var basic in BasicLands)
            {
                foreach (var who in Controllers)
                {
                    built.Add(Make(
                        basic,
                        MtgEngine.Domain.Enums.CardType.Land,
                        [basic],
                        Flavours[0],
                        who,
                        supertypes: ["Basic"]));

                    built.Add(Make(
                        basic,
                        MtgEngine.Domain.Enums.CardType.Land,
                        [basic],
                        Flavours[1],
                        who,
                        supertypes: ["Snow", "Basic"]));
                }
            }

            // A face-down permanent is a 2/2 colourless creature with no name, no other types and
            // no abilities (CR 707.2) — not a card with an effect on it, so nothing else is one.
            foreach (var who in Controllers)
            {
                built.Add(Make(
                    "FaceDown",
                    MtgEngine.Domain.Enums.CardType.Creature,
                    [],
                    Flavours[0],
                    who,
                    faceDown: true));
            }

            // An Aura and an Equipment on a creature, and an Aura on a player. Attachment is a
            // state nothing else here is in, and "enchanted creature" is a printed phrase.
            foreach (var who in Controllers)
            {
                built.Add(Make(
                    "Aura",
                    MtgEngine.Domain.Enums.CardType.Enchantment,
                    ["Aura"],
                    Flavours[0],
                    who,
                    attachedTo: host.GetValueOrDefault(who)));

                built.Add(Make(
                    "Equipment",
                    MtgEngine.Domain.Enums.CardType.Artifact,
                    ["Equipment"],
                    Flavours[0],
                    who,
                    attachedTo: host.GetValueOrDefault(who)));

                built.Add(Make(
                    "PlayerAura",
                    MtgEngine.Domain.Enums.CardType.Enchantment,
                    ["Aura"],
                    Flavours[0],
                    who,
                    attachedToPlayer: who));
            }

            return built.ToImmutable();
        }

        /// <summary>
        /// The combat every witness made in a combat flavour is already in.
        /// </summary>
        /// <remarks>
        /// One player's copy of each combat flavour attacks and the other player's copy blocks,
        /// so "attacking Goblin you control" and "blocking creature you control" are both
        /// answered without any one permanent doing both. Set on the state rather than played
        /// out, the way <c>Game.AddCounters</c> is: a board with an attacker on it is a position,
        /// and playing four turns to reach it says nothing this check is about.
        /// </remarks>
        private CombatState BuildCombat(HashSet<ObjectId> here)
        {
            var attackers = ImmutableDictionary.CreateBuilder<ObjectId, AttackTarget>();
            var blockers = ImmutableDictionary.CreateBuilder<ObjectId, ImmutableList<ObjectId>>();
            var blocked = ImmutableHashSet.CreateBuilder<ObjectId>();

            // Only the witnesses this board actually holds: a combat naming an object the state
            // has never heard of is a state no game could reach, and the filters read it.
            var free = blocking.Find(here.Contains);

            foreach (var (id, isBlocked) in attacking)
            {
                if (!here.Contains(id))
                    continue;

                attackers.Add(id, AttackTarget.Player(Bob));

                if (!isBlocked || free == default)
                    continue;

                blockers.Add(id, [free]);
                blocked.Add(id);
            }

            return new CombatState
            {
                Attackers = attackers.ToImmutable(),
                Blockers = blockers.ToImmutable(),
                Blocked = blocked.ToImmutable(),
                AttackersDeclared = true,
                BlockersDeclared = true,
            };
        }

        private GameObject Make(
            string name,
            MtgEngine.Domain.Enums.CardType types,
            IReadOnlyList<string> subtypes,
            Flavour flavour,
            Guid who,
            IReadOnlyList<string>? supertypes = null,
            bool faceDown = false,
            ObjectId attachedTo = default,
            Guid? attachedToPlayer = null)
        {
            var creature = types.HasFlag(MtgEngine.Domain.Enums.CardType.Creature);
            var walker = types.HasFlag(MtgEngine.Domain.Enums.CardType.Planeswalker);

            var supers = new List<string>(supertypes ?? []);
            if (flavour.Legendary)
                supers.Add("Legendary");

            var card = new MtgEngine.Domain.Models.CardDefinition
            {
                OracleId = $"witness-{stamp}",
                Name = $"Witness {name} {flavour.Name} {stamp}",
                CardTypes = flavour.Token ? types | MtgEngine.Domain.Enums.CardType.Token : types,
                Subtypes = subtypes,
                Supertypes = supers,
                Colors = flavour.Colors,
                Keywords = flavour.Keywords,
                Power = creature ? flavour.Power : null,
                Toughness = creature ? flavour.Toughness : null,
                StartingLoyalty = walker ? 4 : null,
                Cmc = flavour.Cmc,
            };

            var counters = flavour.Counters
                ? ImmutableDictionary<string, int>.Empty
                    .Add("+1/+1", 1)
                    .Add("charge", 1)
                    .Add("loyalty", 4)
                : ImmutableDictionary<string, int>.Empty;

            var made = new GameObject
            {
                Id = ObjectId.New(),
                Card = card,
                OwnerId = who,
                ControllerId = who,
                Zone = Zone.Battlefield,
                Timestamp = stamp++,
                Permanent = new PermanentState
                {
                    IsTapped = flavour.Tapped,
                    HasSummoningSickness = flavour.Sick,
                    IsFaceDown = faceDown,
                    IsMonstrous = flavour.Decorated,
                    IsRenowned = flavour.Decorated,
                    IsSaddled = flavour.Decorated,
                    DamageMarked = flavour.Decorated ? 1 : 0,
                    Counters = counters,
                    AttachedTo = attachedTo == default ? null : attachedTo,
                    AttachedToPlayer = attachedToPlayer,
                },
            };

            if (creature && flavour.InCombat)
            {
                if (who == Alice)
                    attacking.Add((made.Id, flavour.Blocked));
                else
                    blocking.Add(made.Id);
            }

            return made;
        }
    }
}
