using System.Collections;
using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using Xunit.Abstractions;

namespace MtgEngine.Api.Tests;

/// <summary>
/// Whether a mechanism does anything when the printed card carrying it is played.
/// </summary>
/// <remarks>
/// Four instruments already ask four different questions about a compiled card and none of them
/// asks this one. Coverage counts the lines a card's text got <em>read</em>; the set diff compares
/// which cards became complete; <c>CardCompilerInvariantTests.Inert</c> asks whether an effect list
/// is empty, a filter selects nothing, a locator can be found again; <c>DeadWriteAuditTests</c>
/// decodes IL to find a marker nothing reads; <c>TriggerProbeAuditTests</c> fires events at a
/// closure to see whether it accepts any of them. A card can pass all five while the thing it does
/// when somebody casts it is nothing, or the opposite of what it says.
/// <para>
/// Three of the four defects this round's sweep found were invisible to every one of them, and
/// each was invisible for a different reason:
/// </para>
/// <list type="bullet">
/// <item>An Attraction's lights are not in the event log, so a <em>stored</em> game brings back an
/// Attraction lit on nothing. Nothing that compiles a card can see a field that goes missing
/// between two processes, and the replay test that exists resumes from the in-memory log, where
/// the card objects are the same instances.</item>
/// <item>A destroyed Attraction goes back on top of the Attraction deck, because the junkyard and
/// the deck are one zone and nothing told them apart. There is no compiled artefact anywhere in
/// that sentence — it is a fact about where a card ends up.</item>
/// <item>The blink family exiled the creature and never returned it, because one trailing sentence
/// threw a whole-line match away and what caught the fall was a <em>different reader that
/// compiles clean</em>. The card is complete, its effects are non-empty, its filters select, its
/// triggers fire. It just does the wrong thing.</item>
/// </list>
/// <para>
/// So each check here <b>plays a printed card and looks at the board</b>, and each has a control
/// beside it — a board the mechanism should not have changed, or the same card with the one fact
/// the mechanism turns on altered. A check with no control is a check that cannot fail for the
/// right reason: the Attraction sweep below was run once with the control's lights set equal to
/// the printed ones, and every card in the population became a finding, which is what proves the
/// difference it measures is the visit and not the weather.
/// </para>
/// </remarks>
public sealed partial class MechanismPlayAuditTests(ITestOutputHelper output)
{
    private static readonly Guid Alice = Guid.Parse("aaaaaaaa-2159-1111-1111-111111111111");
    private static readonly Guid Bob = Guid.Parse("bbbbbbbb-2159-2222-2222-222222222222");

    // ---- 1. what a stored game brings back ------------------------------------------------

    /// <summary>
    /// Printed characteristics the rules engine reads but the event log does not carry.
    /// </summary>
    /// <remarks>
    /// Each entry is a claim that the engine never asks this field a question, with the reason.
    /// Everything not listed fails the build. The list is checked against the type as well, so a
    /// renamed or deleted property cannot leave a stale line sitting here vouching for nothing.
    /// </remarks>
    private static readonly ImmutableDictionary<string, string> NotAskedByTheRules =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ImageUriNormal"] = "artwork; the engine never looks at a picture",
            ["ImageUriLarge"] = "artwork",
            ["ImageUriNormalBack"] = "artwork",
            ["ImageUriSmall"] = "artwork",
            ["ImageUriArtCrop"] = "artwork",
            ["FlavorText"] = "flavour; CR 207.2 says it has no effect on play",
            ["Artist"] = "credit; no rule reads it",
            ["SetCode"] = "printing, not the card; the rules are about the oracle text",
            ["Rarity"] = "printing",
            ["Legalities"] = "deck construction, decided before a game starts (CR 100.2)",
            ["GameChanger"] = "a bracket label for deck building; MtgEngine.Rules never reads it",
            ["Prices"] = "commerce",
            ["ManaCost"] =
                "the engine parses ManaCostRaw into its own ManaCostSpec - ManaSymbol says in as "
                + "many words that the domain type is lossy by design - and the three reads of "
                + "this one copy it onto a derived definition rather than ask it anything",
        }.ToImmutableDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Every printed characteristic the rules engine reads survives being stored and re-read.
    /// </summary>
    /// <remarks>
    /// This is the third time one field of <see cref="CardDefinition"/> has failed to survive the
    /// log, and the first two are written down in <c>EventLogSerializer</c> beside the fields that
    /// fix them: faces, so a stored game forgot every transform, and specializations, so a stored
    /// game could not specialize again. The third was Attraction lights, which nothing noticed
    /// because an Attraction with no lights is not an error anywhere — it sits on the battlefield,
    /// still makes the precombat main phase roll a d6, and matches no result for the rest of the
    /// game.
    /// <para>
    /// Asked by doing the round trip rather than by reading the serializer, so it cannot be
    /// satisfied by a field that is written and dropped, or written and read into the wrong slot.
    /// Every property is given a value distinguishable from its default first, and a property this
    /// audit does not know how to vary is a finding too — otherwise a new field of a new type
    /// would be compared default-to-default and pass without being carried at all.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_printed_characteristic_the_engine_reads_survives_a_stored_game()
    {
        var properties = typeof(CardDefinition)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.SetMethod is not null)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

        var stale = NotAskedByTheRules.Keys
            .Where(name => !properties.Exists(p => string.Equals(p.Name, name, StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            stale.Count == 0,
            "accepted names that are no longer properties of CardDefinition: " + string.Join(", ", stale));

        var varied = new CardDefinition();
        var unvariable = new List<string>();

        foreach (var property in properties)
        {
            if (Varied(property.PropertyType) is not { } value)
            {
                unvariable.Add(property.Name);
                continue;
            }

            // An init-only setter is an ordinary setter to reflection, which is what makes this
            // possible without a copy constructor somebody would have to keep in step.
            property.SetValue(varied, value);
        }

        var written = EventLogSerializer.Write(
            [new ObjectCreated(ObjectId.New(), varied, Alice, Alice, Zone.Command)]);

        var back = Assert.IsType<ObjectCreated>(Assert.Single(EventLogSerializer.Read(written))).Card;

        var lost = properties
            .Where(p => !unvariable.Contains(p.Name))
            .Where(p => !Same(p.GetValue(varied), p.GetValue(back)))
            .Select(p => p.Name)
            .ToList();

        output.WriteLine($"printed characteristics: {properties.Count}");
        output.WriteLine($"carried through the log:  {properties.Count - lost.Count - unvariable.Count}");

        var findings = lost.Concat(unvariable)
            .Where(name => !NotAskedByTheRules.ContainsKey(name))
            .Order(StringComparer.Ordinal)
            .ToList();

        foreach (var finding in findings)
            output.WriteLine("  lost in the log: " + finding);

        Assert.True(
            findings.Count == 0,
            "CardDefinition fields the rules engine reads that a stored game does not bring back:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, findings)
                + Environment.NewLine
                + "Carry each in EventLogSerializer.PrintedCard, or name it in NotAskedByTheRules "
                + "with the reason the engine never asks it anything.");
    }

    /// <summary>A value of this type that no default equals, or null if this audit cannot make one.</summary>
    private static object? Varied(Type type)
    {
        if (type == typeof(string))
            return "r21-59";

        if (type == typeof(int) || type == typeof(int?))
            return 7;

        if (type == typeof(bool))
            return true;

        if (type == typeof(CardType))
            return CardType.Artifact | CardType.Creature;

        if (type == typeof(KeywordAbility))
            return KeywordAbility.Flying;

        if (type == typeof(IReadOnlyList<string>))
            return new[] { "r21-59" };

        if (type == typeof(IReadOnlyList<int>))
            return new[] { 3, 5 };

        if (type == typeof(IReadOnlyList<ManaColor>))
            return new[] { ManaColor.Blue };

        if (type == typeof(IReadOnlyList<CardFace>))
            return new[] { new CardFace { Name = "r21-59", OracleText = "r21-59" } };

        if (type == typeof(IReadOnlyDictionary<string, string>))
            return new Dictionary<string, string>(StringComparer.Ordinal) { ["r21"] = "59" };

        return null;
    }

    /// <summary>
    /// Value equality that reads a sequence as its members and a card's own types as their fields.
    /// </summary>
    /// <remarks>
    /// A record's generated equality is not enough here and the difference is the whole point of
    /// the check. <see cref="CardFace"/> is a record whose fields are <c>IReadOnlyList</c>s, and a
    /// generated <c>Equals</c> compares those with the default comparer — by reference. Two faces
    /// that carry identical words through a round trip are unequal to it, so a comparison that
    /// trusted it would report every faced card as lost and the audit would have to be silenced
    /// rather than believed.
    /// </remarks>
    private static bool Same(object? left, object? right)
    {
        if (left is null || right is null)
            return left is null && right is null;

        if (left is not string && left is IEnumerable mine && right is IEnumerable theirs)
        {
            var ours = mine.Cast<object?>().ToList();
            var yours = theirs.Cast<object?>().ToList();

            return ours.Count == yours.Count
                && ours.Zip(yours).All(pair => Same(pair.First, pair.Second));
        }

        if (left.GetType() != right.GetType())
            return false;

        if (left.GetType().Namespace?.StartsWith("MtgEngine", StringComparison.Ordinal) == true)
        {
            return left.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                .All(p => Same(p.GetValue(left), p.GetValue(right)));
        }

        return left.Equals(right);
    }

    // ---- 2. a marker that is only ever read off the battlefield ----------------------------

    /// <summary>
    /// A permission or a tax is read by sweeping the battlefield, so a card that can never be
    /// there is one that can never speak.
    /// </summary>
    /// <remarks>
    /// The three markers this round added — <see cref="FlashPermission"/>,
    /// <see cref="LibraryTopPermission"/> and <see cref="CombatTax"/> — are all gathered by
    /// <c>Bans.InPlay</c> and its two neighbours, which walk <c>state.Battlefield</c> at the one
    /// moment the question is asked. That is the right design and it has one failure mode nothing
    /// else here can see: an instant or a sorcery that compiled one. It would be complete, its
    /// marker would be non-empty, no filter would be dead and no closure would be inert, and no
    /// board would ever hold it long enough to be asked.
    /// </remarks>
    [Fact]
    public void Every_permission_and_tax_is_printed_on_something_that_can_be_on_a_battlefield()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present — skipping.");
            return;
        }

        const CardType Permanents = CardType.Artifact | CardType.Creature | CardType.Enchantment
            | CardType.Land | CardType.Planeswalker | CardType.Battle;

        var carried = 0;
        var findings = new List<string>();

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

            var markers = new List<string>();
            if (compiled.FlashPermissions.Count > 0)
                markers.Add("flash permission");
            if (compiled.LibraryTopPermissions.Count > 0)
                markers.Add("library-top permission");
            if (!compiled.Bans.CombatTaxes.IsEmpty)
                markers.Add("combat tax");

            if (markers.Count == 0)
                continue;

            carried++;

            var types = card.CardTypes
                | card.Faces.Aggregate(CardType.None, (all, face) => all | face.CardTypes);

            if ((types & Permanents) == 0)
                findings.Add($"{card.Name} ({string.Join(", ", markers)}) is {card.CardTypes}");
        }

        output.WriteLine($"cards carrying a battlefield-read marker: {carried}");

        foreach (var finding in findings)
            output.WriteLine("  never on a battlefield: " + finding);

        Assert.True(
            findings.Count == 0,
            "these carry a marker read only by sweeping the battlefield and can never be there:"
                + Environment.NewLine + string.Join(Environment.NewLine, findings));
    }

    // ---- 3. the blink family, played ------------------------------------------------------

    /// <summary>
    /// Every printed spell that says it returns what it exiled actually returns it.
    /// </summary>
    /// <remarks>
    /// The whole of this check is one assertion — after the spell resolves, the creature it was
    /// cast at is on the battlefield — and that assertion is the one the family failed. Anchoring
    /// the reader to the end of the line meant a single trailing sentence took the match away, and
    /// the fallback was not a refusal but the graveyard reanimator that shares the wording, which
    /// looks for its subject in a graveyard and finds it in exile. Complete, castable, legal, and
    /// it removed your own creature from the game.
    /// <para>
    /// The control is the count printed beside the findings: a harness that quietly cast nothing
    /// would report zero findings and zero plays, so the number played is asserted to be the whole
    /// population rather than left as a note. That is what makes an empty finding list mean
    /// something.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_spell_that_says_it_returns_what_it_exiled_returns_it()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present — skipping.");
            return;
        }

        var pool = new CompiledPool();
        var blinks = new List<CardDefinition>();

        foreach (var card in corpus)
        {
            CompiledCard compiled;
            try
            {
                compiled = pool.For(card);
            }
            catch (Exception)
            {
                continue;
            }

            if (!compiled.IsComplete || compiled.Spell is not { } spell)
                continue;

            // Selected on the words the card prints, never on what it compiled to. Selecting on
            // the compiled effect is how this check first passed while the family was broken: the
            // reader that mis-read these cards produced a different effect, so a population read
            // out of the compiler quietly dropped every card that was wrong and asked the
            // question only of the ones that were already right.
            if (!card.OracleText.Split('\n').Any(line => PrintedBlink().IsMatch(line.Trim())))
                continue;

            if (spell.Targets.Count == 1 && spell.Targets[0].Kind == TargetKind.Permanent)
                blinks.Add(card);
        }

        var played = 0;
        var refused = new List<string>();
        var findings = new List<string>();

        foreach (var card in blinks.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            var game = TwoSeats(pool, seed: 5);

            // Created rather than settled onto the board: a vanilla creature files no trigger, and
            // settling here would hand priority to Bob and refuse every cast below (CR 117.1).
            var bear = game.Create(Alice, Blinked(), Zone.Battlefield);

            var spell = game.Create(Alice, card, Zone.Hand);
            Fund(game, Alice);

            try
            {
                game.CastSpell(Alice, spell, [Target.ToPermanent(bear)]);
            }
            catch (InvalidOperationException why)
            {
                refused.Add($"{card.Name}: {why.Message}");
                continue;
            }

            played++;
            Play(game);

            var home = game.State.Battlefield
                .Select(game.State.GetObject)
                .Any(o => string.Equals(o.Card.Name, "R2159 Blinked Bear", StringComparison.Ordinal));

            if (!home)
            {
                var where = game.State.Objects.Values
                    .Where(o => string.Equals(
                        o.Card.Name, "R2159 Blinked Bear", StringComparison.Ordinal))
                    .Select(o => o.Zone.ToString())
                    .DefaultIfEmpty("nowhere")
                    .First();

                findings.Add($"{card.Name}: the creature it blinked is in {where}");
            }
        }

        output.WriteLine($"printed blinks found: {blinks.Count}");
        output.WriteLine($"played:               {played}");

        foreach (var one in refused)
            output.WriteLine("  not reached: " + one);

        foreach (var finding in findings)
            output.WriteLine("  did not come back: " + finding);

        Assert.True(
            findings.Count == 0,
            "these say they return the permanent they exiled and do not:"
                + Environment.NewLine + string.Join(Environment.NewLine, findings));

        // An empty finding list from a harness that cast nothing is not evidence of anything.
        Assert.True(played > 0, "no printed blink was reached at all");
        Assert.True(
            played == blinks.Count,
            $"only {played} of {blinks.Count} printed blinks were cast; the rest are listed above");
    }

    /// <summary>
    /// "Exile target creature you control, then return that card to the battlefield under its
    /// owner's control" — the printed promise, whatever follows it.
    /// </summary>
    [GeneratedRegex(
        @"^exile .+?, then return (it|that card|those cards) to the battlefield"
            + @"( tapped)? under (its|their) owner's control\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex PrintedBlink();

    // ---- 4. every corpus Attraction, visited -----------------------------------------------

    /// <summary>
    /// Attractions whose visit ability changes nothing on the board this audit builds.
    /// </summary>
    /// <remarks>
    /// Each is a claim that the ability is correct and the <em>board</em> is what has nothing for
    /// it to do, with the reason. Anything else fails the build.
    /// </remarks>
    private static readonly ImmutableDictionary<string, string> VisitsNothingHereChanges =
        new Dictionary<string, string>(StringComparer.Ordinal).ToImmutableDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Every printed Attraction does something when a roll lights it, and nothing when one does
    /// not (CR 701.52a, 717.5).
    /// </summary>
    /// <remarks>
    /// Two games, identical in every respect but one: the second holds the same Attraction with
    /// the <em>complement</em> of its printed lights. Same seed, same deck, same board, same
    /// roller, so the die comes up the same number in both — and because the two light columns
    /// partition the six faces, exactly one of the two games is visited by it. The boards must
    /// therefore differ, and if they do not then the visit did nothing at all.
    /// <para>
    /// The control is the whole design and it is the reason a substituted board is used rather
    /// than an absent one. Comparing "with the Attraction" against "without it" would pass on any
    /// card at all, because a permanent on the battlefield is itself a difference. Comparing two
    /// games that differ only in which of them the die lit isolates the ability. Run with the
    /// control's lights set <em>equal</em> to the printed ones — an identity control rather than a
    /// real one — every card in the population is reported inert, which is the harness being made
    /// to fail on demand.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_printed_attraction_is_visited_by_its_lights_and_does_something()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present — skipping.");
            return;
        }

        var pool = new CompiledPool();

        var attractions = corpus
            .Where(c => Attractions.Is(c) && c.AttractionLights.Count > 0)
            .Where(c =>
            {
                try
                {
                    var compiled = pool.For(c);
                    return compiled.IsComplete && compiled.Triggers.Count > 0;
                }
                catch (Exception)
                {
                    return false;
                }
            })
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ThenBy(c => string.Join(",", c.AttractionLights), StringComparer.Ordinal)
            .ToList();

        var findings = new List<(string Name, string What)>();
        var moved = 0;

        foreach (var attraction in attractions)
        {
            var unlit = Enumerable.Range(1, Attractions.DieSides)
                .Where(face => !attraction.AttractionLights.Contains(face))
                .ToList();

            if (unlit.Count == 0)
                continue;

            var printed = Visit(pool, attraction, attraction.AttractionLights);
            var control = Visit(pool, attraction, unlit);

            var lights = string.Join("/", attraction.AttractionLights);

            // Two findings rather than one assertion and one finding, because the two failures
            // are the two halves of the same claim and a mutation lands on either. Making
            // Attractions.IsLit answer yes to every face puts all ten cards in the first arm;
            // making a visit ability resolve to nothing puts all ten in the second.
            if (printed.Visited == control.Visited)
            {
                findings.Add((
                    attraction.Name,
                    $"{attraction.Name} [{lights}]: the lights made no difference to the roll"));
            }
            else if (string.Equals(printed.Board, control.Board, StringComparison.Ordinal))
            {
                findings.Add((attraction.Name, $"{attraction.Name} [{lights}] changed nothing"));
            }
            else
            {
                moved++;
            }
        }

        output.WriteLine($"printed Attractions with a readable visit: {attractions.Count}");
        output.WriteLine($"whose visit changed the board:            {moved}");

        var unexplained = findings
            .Where(f => !VisitsNothingHereChanges.ContainsKey(f.Name))
            .Select(f => f.What)
            .Order(StringComparer.Ordinal)
            .ToList();

        foreach (var finding in findings)
            output.WriteLine("  visited and did nothing: " + finding.What);

        Assert.True(
            unexplained.Count == 0,
            "these were visited and changed nothing:"
                + Environment.NewLine + string.Join(Environment.NewLine, unexplained));

        Assert.True(moved > 0, "no Attraction was visited at all — the harness reached nothing");
    }

    /// <summary>Plays one board with this Attraction wearing these lights, and reports what it left.</summary>
    private static (bool Visited, string Board) Visit(
        CompiledPool pool, CardDefinition attraction, IReadOnlyList<int> lights)
    {
        var abilities = pool;
        var game = TwoSeats(pool, seed: 11);

        foreach (var seat in new[] { Alice, Bob })
        {
            game.Create(seat, Bear(seat.ToString() + " one"), Zone.Battlefield);
            game.Create(seat, Bear(seat.ToString() + " two"), Zone.Battlefield);
            game.Create(seat, Grove(seat), Zone.Battlefield);
        }

        game.Create(Alice, Bear("in the yard"), Zone.Graveyard);
        game.Create(Alice, Bear("in hand"), Zone.Hand);
        Play(game);

        game.Create(Alice, Relit(attraction, lights), Zone.Battlefield);
        Play(game);

        game.Create(Alice, Roller(), Zone.Battlefield);
        Play(game);

        return (game.Log.OfType<AttractionVisited>().Any(), Board(game, abilities));
    }

    /// <summary>
    /// The board as something two different games can be compared by: no ids, no timestamps.
    /// </summary>
    /// <remarks>
    /// Card names in zone order rather than a set, because a visit that only reordered a library —
    /// which is what "Visit — Scry 1" does — has to count as having done something. The roller is
    /// left out because it is in both games and its own arrival is not the difference under test.
    /// </remarks>
    private static string Board(Game game, CompiledPool pool)
    {
        var text = new StringBuilder(512);

        foreach (var seat in new[] { Alice, Bob })
        {
            var player = game.State.GetPlayer(seat);
            text.Append(CultureInfo.InvariantCulture, $"life={player.Life};");
            text.Append(CultureInfo.InvariantCulture, $"pool={player.ManaPool};");
            text.Append(CultureInfo.InvariantCulture, $"energy={player.Energy};");

            foreach (var (what, zone) in new[]
            {
                ("hand", player.Hand),
                ("library", player.Library),
                ("graveyard", player.Graveyard),
            })
            {
                text.Append(what).Append('[');
                foreach (var id in zone)
                    text.Append(Named(game, id)).Append(',');
                text.Append("];");
            }
        }

        foreach (var id in game.State.Battlefield.Concat(game.State.Exile).Concat(game.State.Command))
        {
            var o = game.State.GetObject(id);
            if (string.Equals(o.Card.Name, "R2159 Roller", StringComparison.Ordinal))
                continue;

            var now = o.Permanent is null
                ? null
                : Characteristics.Of(game.State, pool, o);

            text.Append(o.Card.Name)
                .Append('=').Append(now?.Power).Append('/').Append(now?.Toughness)
                .Append('&').Append(now?.Keywords)
                .Append('@').Append(o.Zone)
                .Append('/').Append(o.ControllerId == Alice ? 'a' : 'b')
                .Append('/').Append(o.Permanent?.IsTapped == true ? 't' : 'u')
                .Append('/').Append(o.Permanent is null
                    ? string.Empty
                    : string.Join("+", o.Permanent.Counters.OrderBy(c => c.Key, StringComparer.Ordinal)
                        .Select(c => c.Key + c.Value.ToString(CultureInfo.InvariantCulture))))
                .Append(';');
        }

        foreach (var delayed in game.State.Delayed)
        {
            text.Append("delayed:").Append(delayed.EffectId)
                .Append('@').Append(delayed.Step).Append(';');
        }

        // Everything a visit can do that leaves no mark on an object: a pump, a granted keyword,
        // a combat requirement. All three are one-turn continuous effects and none of them is
        // visible in a zone listing, which is what "Visit - Creatures you control get +1/+0 until
        // end of turn" had already proved by being reported inert.
        foreach (var floating in game.State.FloatingEffects.OrderBy(
            f => f.DefinitionId, StringComparer.Ordinal))
        {
            text.Append("floating:").Append(floating.DefinitionId)
                .Append('x').Append(floating.AffectedIds.Count).Append(';');
        }

        return text.ToString();
    }

    private static string Named(Game game, ObjectId id) =>
        game.State.TryGetObject(id, out var o) ? o.Card.Name : "?";

    /// <summary>
    /// The same card wearing a different column of lights (CR 717.1).
    /// </summary>
    /// <remarks>
    /// Copied field by field rather than through a constructor somebody has to keep in step:
    /// <see cref="CardDefinition"/> is a class with init-only properties, so a new field added
    /// tomorrow travels here by itself. The oracle id and the text are untouched, which is what
    /// keeps the compiled card identical — the lights are printed data and no reader compiles
    /// them.
    /// </remarks>
    private static CardDefinition Relit(CardDefinition card, IReadOnlyList<int> lights)
    {
        var copy = new CardDefinition();

        foreach (var property in typeof(CardDefinition)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.CanRead && property.SetMethod is not null)
                property.SetValue(copy, property.GetValue(card));
        }

        typeof(CardDefinition)
            .GetProperty(nameof(CardDefinition.AttractionLights))!
            .SetValue(copy, lights.ToArray());

        return copy;
    }

    // ---- boards ----------------------------------------------------------------------------

    private static Game TwoSeats(CompiledPool pool, int seed)
    {
        var game = Game.Start(
            Guid.NewGuid(),
            [
                new PlayerSetup(Alice, "Alice", 40, Deck("a")),
                new PlayerSetup(Bob, "Bob", 40, Deck("b")),
            ],
            new GameRandom(seed),
            startingPlayerId: Alice,
            abilities: pool);

        game.BeginPlay(openingHandSize: 3, withMulligans: false);

        for (var guard = 0; guard < 200 && game.State.CurrentStep < TurnStep.PrecombatMain; guard++)
        {
            if (game.State.Priority.Holder is not { } holder)
                break;

            game.PassPriority(holder);
        }

        return game;
    }

    /// <summary>Answers every question and empties the stack, the way a table would.</summary>
    private static void Play(Game game)
    {
        var passed = false;

        for (var guard = 0; guard < 400; guard++)
        {
            if (game.State.Choice is { } choice)
            {
                game.Choose(
                    choice.PlayerId,
                    [.. choice.Options.Take(Math.Max(choice.MinPicks, 1)).Select(o => o.Id)]);

                continue;
            }

            if (passed && game.State.Stack.IsEmpty && game.State.PendingTriggers.IsEmpty)
                return;

            if (game.State.Priority.Holder is not { } holder)
                return;

            game.PassPriority(holder);
            passed = true;
        }
    }

    private static void Fund(Game game, Guid who)
    {
        game.AddMana(who, null, 20);

        foreach (var colour in Enum.GetValues<ManaColor>())
            game.AddMana(who, colour, 20);
    }

    private static ImmutableList<CardDefinition> Deck(string who) =>
        [.. Enumerable.Range(0, 40).Select(i => new CardDefinition
        {
            OracleId = $"r2159-{who}-{i}",
            Name = $"R2159 Filler {who} {i}",
            CardTypes = CardType.Creature,
            Power = 1,
            Toughness = 1,
        })];

    private static CardDefinition Bear(string which) => new()
    {
        OracleId = "r2159-bear-" + which,
        Name = "R2159 Bear " + which,
        CardTypes = CardType.Creature,
        Subtypes = ["Bear"],
        Power = 2,
        Toughness = 2,
    };

    private static CardDefinition Grove(Guid who) => new()
    {
        OracleId = "r2159-grove-" + who,
        Name = "R2159 Grove",
        CardTypes = CardType.Land,
    };

    private static CardDefinition Blinked() => new()
    {
        OracleId = "r2159-blinked-bear",
        Name = "R2159 Blinked Bear",
        CardTypes = CardType.Creature,
        Subtypes = ["Bear"],
        Power = 2,
        Toughness = 2,
    };

    /// <summary>The printed sentence that rolls, so the die is thrown by a card and not by a turn.</summary>
    private static CardDefinition Roller() => new()
    {
        OracleId = "r2159-roller",
        Name = "R2159 Roller",
        OracleText = "When this creature enters, roll to visit your Attractions.",
        CardTypes = CardType.Creature,
        Power = 1,
        Toughness = 1,
    };
}
