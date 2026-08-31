using System.Collections.Immutable;
using System.Reflection;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using Xunit.Abstractions;

namespace MtgEngine.Api.Tests;

/// <summary>
/// What a compiled trigger predicate actually accepts, asked by playing events at it.
/// </summary>
/// <remarks>
/// Every other instrument in this project compares compiled <em>structure</em>. The coverage
/// count reads a card's lines; the set diff compares which cards became complete; the per-card
/// effect fingerprint compares the effect list; <c>DeadWriteAuditTests</c> decodes IL to find a
/// marker nothing reads; <c>CardCompilerInvariantTests.Inert</c> asks whether an effect list is
/// empty, a filter string selects nothing, a deferred question can be found again. Not one of
/// them can see inside a <c>Func</c>, because a predicate is a closure and a closure has no
/// structure to compare — its captured parameters are private fields of a compiler-generated
/// class with no name.
/// <para>
/// That is exactly where the worst defects have lived. <c>TriggerConditions</c> paired every
/// capitalised noun in a zone-change sentence with <c>CardType.Creature</c>, so "whenever a
/// Forest you control enters" compiled a trigger watching for a <em>creature</em> with the land
/// type Forest. Twenty-three cards — Cartouches, Shrines, Gates, Vehicles, Auras — were
/// complete, legal, castable and could not fire on any board that will ever be built. The card
/// diff could not see it, because only the closure changed. It was found by playing a card.
/// </para>
/// <para>
/// So this plays cards at every predicate. For each triggered ability a complete card compiles,
/// it fires a battery of real <see cref="GameEvent"/>s at the predicate on real boards and
/// records whether <b>any</b> of them is accepted. A predicate that accepts nothing is the
/// closure-shaped form of the inert card: the ability exists, is watched on every event, and
/// cannot fire.
/// </para>
/// <para>
/// <b>The probes are real corpus cards, and that is the whole design.</b> Its nearest neighbour,
/// <c>CardCompilerInvariantTests.Every_printed_noun_phrase_the_grammar_reads_can_be_satisfied</c>,
/// also runs a closure, but over a <em>synthesised</em> witness board that deliberately holds "one
/// permanent that is every type at once" — and that board answers yes to a creature with the land
/// type Forest, so it could never have found the twenty-three. Here the objects the battery moves,
/// casts, attacks with and kills are printed cards, and the deep sweep is a representative of every
/// distinct (card types, subtype) pair the corpus prints. "No object the corpus can produce
/// satisfies this predicate" is then a claim about the printed game rather than about the
/// compiler's own beliefs.
/// </para>
/// <para>
/// Three stages, because the space is a product of three independent things and sweeping all of it
/// for every trigger costs hours. The <b>screen</b> fires a wide battery — every step, every
/// zone-change pair against every move cause, both combat declarations, and one reflectively built
/// instance of every other <see cref="GameEvent"/> type in the assembly, so no event family is
/// missed by omission — over a small diverse probe set, and stops at the first acceptance. What
/// the screen finds nothing for is swept again over the <b>worlds</b>: the same battery on boards
/// where the counters, the turn's history, the attachment and the day are different, because "if
/// this permanent is tapped" and "your second spell each turn" are conditions about the board and
/// not about the event. What survives that is swept a third time over the <b>deep probes</b>.
/// </para>
/// </remarks>
public sealed partial class TriggerProbeAuditTests(ITestOutputHelper output)
{
    private static readonly Guid Mine = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Theirs = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly ObjectId SourceId =
        new(Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111"));

    private static readonly ObjectId ProbeId =
        new(Guid.Parse("bbbbbbbb-1111-1111-1111-111111111111"));

    private static readonly ObjectId ProbeWasId =
        new(Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222"));

    private static readonly ObjectId StackId =
        new(Guid.Parse("cccccccc-1111-1111-1111-111111111111"));

    /// <summary>The zone pairs a card can actually move between, both directions of each.</summary>
    private static readonly (Zone From, Zone To)[] ZoneMoves =
    [
        (Zone.Hand, Zone.Battlefield),
        (Zone.Library, Zone.Battlefield),
        (Zone.Graveyard, Zone.Battlefield),
        (Zone.Exile, Zone.Battlefield),
        (Zone.Stack, Zone.Battlefield),
        (Zone.Command, Zone.Battlefield),
        (Zone.Battlefield, Zone.Graveyard),
        (Zone.Battlefield, Zone.Exile),
        (Zone.Battlefield, Zone.Hand),
        (Zone.Battlefield, Zone.Library),
        (Zone.Battlefield, Zone.Command),
        (Zone.Battlefield, Zone.Battlefield),
        (Zone.Hand, Zone.Graveyard),
        (Zone.Hand, Zone.Exile),
        (Zone.Library, Zone.Graveyard),
        (Zone.Library, Zone.Hand),
        (Zone.Library, Zone.Exile),
        (Zone.Graveyard, Zone.Exile),
        (Zone.Graveyard, Zone.Hand),
        (Zone.Graveyard, Zone.Library),
        (Zone.Stack, Zone.Graveyard),
        (Zone.Stack, Zone.Exile),
        (Zone.Exile, Zone.Hand),
        (Zone.Exile, Zone.Graveyard),
        (Zone.Command, Zone.Stack),
        (Zone.Hand, Zone.Stack),
    ];

    /// <summary>
    /// The kinds of counter a board can carry, read out of the corpus rather than listed.
    /// </summary>
    /// <remarks>
    /// A list somebody wrote is a list somebody can leave a name off, and leaving one off here is
    /// invisible: the battery simply never puts that counter on anything and every trigger that
    /// reads it is reported inert. That is not a hypothetical — the first run of this audit
    /// reported all ten fading creatures and thirty echo creatures as dead, because "fade" and
    /// "echo" were not in the hand-written list. Both are counters the <em>compiler</em> invents
    /// and no card prints, which is why they are named here and everything else is counted.
    /// </remarks>
    private static List<string> CounterNames(IReadOnlyList<CardDefinition> corpus)
    {
        // The names the engine itself has a constant for, taken from the type that owns them
        // rather than copied. A copy would go stale exactly where it matters: "defense" is read
        // by every Siege in the game and is nowhere near the fifty commonest words in front of
        // "counter", so a frequency cut alone dropped it and reported all thirteen as inert.
        var named = typeof(MtgEngine.Rules.State.CounterKinds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        // Invented by the compiler, printed by nothing: fading's clock (CR 702.32a) and the
        // marker echo uses instead of remembering when a permanent arrived (CR 702.29a).
        named.Add("fade");
        named.Add("echo");

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var card in corpus)
        {
            foreach (System.Text.RegularExpressions.Match found
                in CounterWord().Matches(card.OracleText))
            {
                var kind = found.Groups["kind"].Value;
                counts[kind] = counts.GetValueOrDefault(kind) + 1;
            }
        }

        return
        [
            .. named,
            .. counts.OrderByDescending(e => e.Value)
                .Select(e => e.Key.ToLowerInvariant())
                .Where(k => !named.Contains(k, StringComparer.OrdinalIgnoreCase))
                .Take(60),
        ];
    }

    /// <summary>The word in front of "counter" on a printed card (CR 122.1).</summary>
    [System.Text.RegularExpressions.GeneratedRegex(
        @"(?<kind>[+\-]\d/[+\-]\d|[A-Za-z']+) counters?\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex CounterWord();

    /// <summary>Event types this file builds by hand, so the reflective sweep leaves them alone.</summary>
    private static readonly HashSet<string> HandBuilt = new(StringComparer.Ordinal)
    {
        "ObjectMoved", "ObjectCreated", "StepBegan", "TurnBegan", "AttackersDeclared",
        "BlockersDeclared", "CombatDamageDealt", "CardsDiscarded", "CardsLeftGraveyard",
        "CardsRevealed", "SpellCastEvent", "CountersChanged", "DamageMarked", "PlayerDamaged",
        "PermanentTapped", "PermanentAttached", "AbilityActivated", "TargetsChosen",
        "LifeChanged", "CreatureExploited", "PermanentTurned", "PermanentTransformed",
        "ManaAdded",
    };

    /// <summary>
    /// One shape of board, held constant while the whole event battery is fired at it.
    /// </summary>
    /// <remarks>
    /// The second axis, and the one the first run of this audit did not have. Roughly a third of
    /// the predicates it reported as firing on nothing were asking about the <em>board</em> rather
    /// than about the event — "if this permanent is tapped", "when the last defense counter is
    /// removed", "whenever you draw your second card each turn", "whenever enchanted creature
    /// deals damage" — and a battery fired at one pristine untouched board answers no to every one
    /// of them, correctly and uselessly.
    /// <para>
    /// <see cref="Counters"/> is a number rather than a flag because the conditions that read one
    /// are equalities: a Saga's third chapter fires on the third lore counter and on no other, and
    /// "your second spell each turn" is a different board from "your third". So it drives the
    /// counters on the permanents and the turn's tallies together, and the sweep runs it from one
    /// to six.
    /// </para>
    /// </remarks>
    private sealed record World(
        string Name,
        int Counters,
        bool Busy,
        bool Attached,
        bool Tapped,
        bool? Day,
        bool Bare,
        bool FaceDown,
        int Lean,
        IReadOnlyList<CardDefinition> Crowd,
        IReadOnlyList<string> Kinds,
        ImmutableDictionary<string, int> CounterMap,
        bool Lone,
        bool Designated);

    /// <summary>The board shapes, in the order the sweep tries them.</summary>
    /// <remarks>
    /// The first is what the screen uses, and it is the busiest one on purpose: every fact a
    /// condition can ask about is switched on, so a predicate that is going to accept anything
    /// usually accepts here and never costs the rest of the list. The others differ from it in one
    /// direction each — a different count, night rather than day, an untouched permanent, an empty
    /// battlefield — because those are the four ways a busy board can be the wrong answer.
    /// </remarks>
    private static World[] Worlds(
        IReadOnlyList<CardDefinition> crowd, IReadOnlyList<string> kinds)
    {
        World Shape(
            string name,
            int counters,
            bool busy,
            bool bare = false,
            bool faceDown = false,
            int lean = 0,
            IReadOnlyList<CardDefinition>? with = null,
            bool day = true,
            bool lone = false,
            bool designated = true) =>
            new(name, counters, busy, busy, busy, day, bare, faceDown, lean, with ?? [], kinds,
                kinds.ToImmutableDictionary(k => k, _ => counters), lone, busy && designated);

        return
        [
            Shape("busy(1)", 1, true),
            Shape("crowded, you ahead", 2, true, lean: 1, with: crowd),
            Shape("crowded, they are ahead", 2, true, lean: 2, with: crowd),
            Shape("busy(2)", 2, true),
            Shape("busy(3)", 3, true),
            Shape("busy(4)", 4, true),
            Shape("busy(5)", 5, true),
            Shape("busy(6)", 6, true),
            Shape("busy(7)", 7, true),
            Shape("busy(8)", 8, true),
            Shape("face down", 1, true, faceDown: true),
            Shape("everyone nearly dead", 1, true, lean: 3),
            Shape("busy at night", 1, true, day: false),
            Shape("quiet", 0, false, day: false),
            Shape("bare battlefield", 0, false, bare: true, day: false),
            Shape("one friend", 1, true, bare: true, lone: true),

            // A designation is not like the other facts here: monstrous, renowned, saddled,
            // prepared and solved each turn some abilities on and others *off*. CR 719.3a's "and
            // this Case is not solved" is the clearest — a Case that is already solved can never
            // solve again, so a battery that stamped every designation on reported the two
            // printed Cases as inert for having done what they were waiting to do.
            Shape("nothing designated", 4, true, designated: false),
        ];
    }

    /// <summary>One board, and the events fired at a predicate while it is that board.</summary>
    private sealed record Scene(GameState State, IReadOnlyList<GameEvent> Events);

    /// <summary>
    /// Three of every colour and four colourless, as the mana a spell was paid with.
    /// </summary>
    /// <remarks>
    /// Eleven printed cards read the pool a spell was cast with — "if {B} was spent to cast it",
    /// "if at least three mana of the same color was spent" — and a battery that spent two green
    /// and two blue answers no to nine of them for no reason but the colours it happened to pick.
    /// </remarks>
    private static readonly MtgEngine.Rules.Mana.ManaPool EveryColour =
        Enum.GetValues<MtgEngine.Domain.Enums.ManaColor>()
            .Aggregate(
                MtgEngine.Rules.Mana.ManaPool.Empty.AddColorless(4),
                (pool, colour) => pool.Add(colour, 3));

    /// <summary>One object placed on a board, with the status this world gives a permanent.</summary>
    private static GameObject At(
        ObjectId id, CardDefinition card, Guid who, Zone zone, long stamp, World world)
        => new()
        {
            Id = id,
            Card = card,
            OwnerId = who,
            ControllerId = who,
            Zone = zone,
            Timestamp = stamp,
            WasBlitzed = world.Busy,
            WasKicked = world.Busy,
            TimesKicked = world.Counters,
            SquadPaid = world.Counters,
            VariableValue = world.Counters,
            WasBargained = world.Busy,
            WasCleaved = world.Busy,
            WasEvoked = world.Busy,
            WasDashed = world.Busy,
            WasAwakened = world.Busy,
            WasEscaped = world.Busy,
            WasBoughtBack = world.Busy,
            WasWarped = world.Busy,
            WasPrototyped = world.Busy,
            WasOffspring = world.Busy,
            WasBestowed = world.Busy,
            IsRevealed = world.Busy,
            OnAdventure = world.Busy,
            WasTeamwork = world.Busy,
            GiftedTo = world.Busy ? Theirs : null,
            CastBy = world.Busy ? who : null,
            CastFromZone = world.Busy ? Zone.Hand : null,
            PlottedOnTurn = world.Busy ? 2 : null,
            ForetoldOnTurn = world.Busy ? 2 : null,
            DiscardedOnTurn = world.Busy ? 2 : null,
            TimeCounters = world.Counters,
            ManaSpent = world.Busy ? EveryColour : MtgEngine.Rules.Mana.ManaPool.Empty,
            Permanent = zone == Zone.Battlefield ? Status(id, world) : null,
        };

    /// <summary>What has been done to a permanent on a board of this shape.</summary>
    /// <remarks>
    /// <c>DamagedBy</c> carries both objects because "whenever a creature dealt damage by this
    /// creature this turn dies" reads it off the <em>dying</em> permanent and asks whether the
    /// ability's own source is in it — a fact about history that no event carries.
    /// </remarks>
    private static PermanentState Status(ObjectId id, World world) => new()
    {
        HasSummoningSickness = false,
        IsTapped = world.Tapped,
        IsMonstrous = world.Designated,
        IsSaddled = world.Designated,
        IsRenowned = world.Designated,
        IsSolved = world.Designated,
        IsPrepared = world.Designated,
        Level = Math.Max(1, world.Counters),
        EnteredOnTurn = 3,
        DamageMarked = world.Busy ? 1 : 0,
        DamagedBy = world.Busy ? [SourceId, ProbeId, ProbeWasId] : [],
        UnlockedHalves = world.Busy ? [0, 1] : [],
        AttachedTo = world.Attached ? (id == SourceId ? ProbeId : SourceId) : null,
        AttachedToPlayer = world.Attached ? Theirs : null,
        IsFaceDown = world.FaceDown,
        Counters = world.Counters == 0 ? ImmutableDictionary<string, int>.Empty : world.CounterMap,
    };

    /// <summary>
    /// A two-player board carrying the ability's source, a probe, the probe's earlier self, and a
    /// copy of the probe on the stack.
    /// </summary>
    /// <remarks>
    /// Four objects rather than a populated battlefield, because a predicate that scans the board
    /// pays for every permanent on it and the whole corpus is swept through here. The earlier self
    /// is what makes a zone change answerable: <c>TriggerConditions.CardOfMoved</c> looks the moved
    /// card up under its old id first and its new id second (CR 400.7 makes them different
    /// objects), so both have to exist for the reading to be the one the engine does.
    /// </remarks>
    private static GameState Board(
        CardDefinition source,
        CardDefinition probe,
        Guid side,
        Zone probeZone,
        Zone probeWasZone,
        Guid active,
        TurnStep step,
        CombatState combat,
        World world)
    {
        // An empty battlefield is a board shape a condition can ask for — "if no creatures are on
        // the battlefield" — and the only way to offer one is to put the probes somewhere else.
        var elsewhere = world.Bare ? Zone.Exile : Zone.Battlefield;

        var objects = new List<GameObject>
        {
            At(SourceId, source, Mine, Zone.Battlefield, 1, world),
            At(ProbeId, probe, side, Swap(probeZone, elsewhere), 2, world),
            At(ProbeWasId, probe, side, Swap(probeWasZone, elsewhere), 3, world),
            At(StackId, probe, side, Zone.Stack, 4, world),

            // More of the same card, so a sentence counting them has something to count:
            // "two or more Gates", "three or more Dragons", "at least five other Mountains",
            // "another Knight". One probe answers no to every one of those whatever card it is.
        };

        // More of the same card, so a sentence counting them has something to count: "two or more
        // Gates", "three or more Dragons", "at least five other Mountains", "another Knight". One
        // probe answers no to every one of those whatever card it is. And some of the copies are
        // on the asking side and in the other zones, because "if you control a red permanent" and
        // "if there's a Lesson card in your graveyard" are questions about the trigger's own
        // controller, which every copy belonging to the other seat answers no to.
        //
        // None of it on the bare board, which is the world that exists to answer the opposite
        // kind of question — "if you have no cards in hand", "if you control exactly one
        // creature". Filling every zone for the counting sentences made four of those inert.
        // "If you control exactly one creature" is a board nobody else here builds: the busy
        // worlds hold a dozen permanents and the bare one holds the source alone.
        if (world.Lone)
            objects.Add(At(Sequential(5), probe, Mine, Zone.Battlefield, 5, world));

        if (!world.Bare)
        {
            objects.AddRange(
            [
                At(Sequential(5), probe, side, Zone.Battlefield, 5, world),
                At(Sequential(6), probe, side, Zone.Battlefield, 6, world),
                At(Sequential(7), probe, side, Zone.Battlefield, 7, world),
                At(Sequential(8), probe, side, Zone.Battlefield, 8, world),
                At(Sequential(9), probe, side, Zone.Battlefield, 9, world),
                At(Sequential(10), probe, Mine, Zone.Battlefield, 10, world),
                At(Sequential(11), probe, Mine, Zone.Battlefield, 11, world),
                At(Sequential(12), probe, Mine, Zone.Graveyard, 12, world),
                At(Sequential(13), probe, Mine, Zone.Hand, 13, world),
                At(Sequential(14), probe, Mine, Zone.Exile, 14, world),
            ]);
        }

        objects.AddRange(Extras(world));

        var players = new[] { Mine, Theirs }.ToImmutableDictionary(
            id => id,
            id => Seat(id, probe, objects, world));

        return new GameState
        {
            GameId = Guid.Empty,
            Objects = objects.ToImmutableDictionary(o => o.Id),
            TurnOrder = [Mine, Theirs],
            Players = players,
            Battlefield = [.. Shared(objects, Zone.Battlefield)],
            Stack = [.. Shared(objects, Zone.Stack)],
            Exile = [.. Shared(objects, Zone.Exile)],
            Command = [.. Shared(objects, Zone.Command)],
            TurnNumber = 3,
            ActivePlayerId = active,
            PreviousActivePlayerId = active == Mine ? Theirs : Mine,
            CurrentStep = step,
            Combat = combat,
            NextTimestamp = 20,
            MonarchId = world.Busy ? (world.Lean == 2 ? Theirs : Mine) : null,
            InitiativeId = world.Busy ? (world.Lean == 2 ? Theirs : Mine) : null,
            IsDay = world.Day,
            // Everything on the board arrived and left this turn. "If three or more artifacts
            // entered the battlefield under your control this turn" and "if two or more creatures
            // died this turn" are counts of these two lists and nothing else can answer them.
            ArrivalsThisTurn = world.Busy
                ? [new BattlefieldArrival(ProbeId, side, probe),
                   new BattlefieldArrival(SourceId, Mine, source),
                   .. objects.Where(o => o.Zone == Zone.Battlefield)
                       .Select(o => new BattlefieldArrival(o.Id, o.ControllerId, o.Card))]
                : [],
            DeparturesThisTurn = world.Busy
                ? [new BattlefieldDeparture(ProbeWasId, side, probe, Zone.Graveyard),
                   new BattlefieldDeparture(ProbeWasId, side, probe, Zone.Exile),
                   .. objects.Where(o => o.Zone == Zone.Graveyard)
                       .Select(o => new BattlefieldDeparture(
                           o.Id, o.ControllerId, o.Card, Zone.Graveyard))]
                : [],
        };
    }

    private static Zone Swap(Zone zone, Zone instead) =>
        zone == Zone.Battlefield ? instead : zone;

    /// <summary>
    /// The supporting cast a crowded board carries: everything in the crowd, on both sides.
    /// </summary>
    /// <remarks>
    /// Half the printed triggers in the corpus are "at the beginning of X, if Y" and the whole of
    /// Y is a question about a <em>populated</em> board — three creatures with different powers,
    /// four or more card types in your graveyard, a full party, five or more artifacts, your
    /// commander. A four-object board answers no to every one of them, correctly and uselessly,
    /// and the first run of this audit reported four hundred of them as inert. So one world holds
    /// a real board instead: every card in the crowd on the battlefield under both players, in
    /// both graveyards and in both hands, with each seat's commander named as the first of them.
    /// </remarks>
    private static IEnumerable<GameObject> Extras(World world)
    {
        var stamp = 100;

        foreach (var card in world.Crowd)
        {
            foreach (var who in new[] { Mine, Theirs })
            {
                // The lean is what makes a comparison answerable. Fifty-six printed cards ask
                // "if an opponent controls more lands than you" or the mirror of it, and a board
                // where both seats hold the same thing answers no to both halves — so one world
                // stacks it each way rather than pretending the question is about a total.
                var many = (world.Lean == 1 && who == Mine) || (world.Lean == 2 && who == Theirs)
                    ? 8
                    : 1;

                foreach (var zone in
                    new[] { Zone.Battlefield, Zone.Graveyard, Zone.Hand, Zone.Library })
                {
                    for (var copy = 0; copy < many; copy++)
                        yield return At(Sequential(stamp++), card, who, zone, stamp, world);
                }
            }
        }
    }

    /// <summary>A deterministic id for the nth member of the supporting cast.</summary>
    private static ObjectId Sequential(int n) =>
        new(new Guid(0x0EEE0000 + n, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));

    /// <summary>One seat, with everything its turn can already have done to it.</summary>
    /// <remarks>
    /// Every tally here is a fact some printed condition asks about and no event carries: "your
    /// second spell each turn", "if you gained life this turn", "if you descended this turn", "if
    /// you attacked this turn". They move together with the world's count so that an equality on
    /// any of them is satisfied by one of the six busy worlds.
    /// </remarks>
    private static PlayerState Seat(
        Guid id, CardDefinition probe, IEnumerable<GameObject> objects, World world) => new()
        {
            PlayerId = id,
            Name = id == Mine ? "Mine" : "Theirs",
            Life = world.Lean switch
            {
                1 => id == Mine ? 30 : 3,
                2 => id == Mine ? 3 : 30,
                3 => 5,
                _ => 20,
            },
            Library = [.. Held(objects, id, Zone.Library)],
            Hand = [.. Held(objects, id, Zone.Hand)],
            Graveyard = [.. Held(objects, id, Zone.Graveyard)],
            CardsDrawnThisTurn = world.Counters,
            SpellsCastThisTurn = world.Counters,
            NoncreatureSpellsCastThisTurn = world.Counters,
            SpellsCastLastTurn = world.Counters,
            TimesDescendedThisTurn = world.Counters,
            LifeGainedThisTurn = world.Counters,
            LandsPlayedThisTurn = world.Counters,
            ManaSpentCastingThisTurn = world.Counters,
            RingTemptations = world.Counters,
            ExperienceCounters = world.Counters,
            Energy = world.Busy ? 5 : 0,
            PoisonCounters = world.Busy ? 3 : 0,
            Speed = world.Busy ? 4 : 1,
            SpeedIncreasedThisTurn = world.Busy,
            AttackedThisTurn = world.Busy,
            LostLifeThisTurn = world.Busy,
            WasDealtDamageThisTurn = world.Busy,
            DealtCombatDamageToPlayerThisTurn = world.Busy,
            AssassinOrCommanderConnectedThisTurn = world.Busy,
            HasCitysBlessing = world.Busy,
            SpellCardsCastThisTurn = world.Crowd.Count > 0
                ? [.. world.Crowd, .. world.Crowd]
                : world.Busy ? [probe, probe, probe, probe] : [],
            CompletedDungeons = world.Busy ? ["Tomb of Annihilation"] : [],
            CommanderOracleId = world.Crowd.Count > 0
                ? world.Crowd[0].OracleId
                : world.Busy ? probe.OracleId : null,
        };

    private static IEnumerable<ObjectId> Held(IEnumerable<GameObject> objects, Guid who, Zone zone)
        => objects.Where(o => o.ControllerId == who && o.Zone == zone).Select(o => o.Id);

    private static IEnumerable<ObjectId> Shared(IEnumerable<GameObject> objects, Zone zone)
        => objects.Where(o => o.Zone == zone).Select(o => o.Id);

    /// <summary>
    /// The same board with this ability's own source on it, in the zone the ability works from.
    /// </summary>
    /// <remarks>
    /// The batteries are built once and reused for every trigger in the corpus, which is only
    /// sound if the one thing that differs per trigger — its source — is substituted rather than
    /// rebuilt. A trigger that works from somewhere other than the battlefield (CR 603.6) has its
    /// source moved to that zone as well, because a predicate reading the source out of the state
    /// would otherwise find it in play when the card says it is in a graveyard.
    /// </remarks>
    private static GameState WithSource(GameState board, GameObject source)
    {
        var state = board with { Objects = board.Objects.SetItem(source.Id, source) };
        if (source.Zone == Zone.Battlefield)
            return state;

        state = state with { Battlefield = state.Battlefield.Remove(source.Id) };

        return source.Zone switch
        {
            Zone.Stack => state with { Stack = state.Stack.Add(source.Id) },
            Zone.Exile => state with { Exile = state.Exile.Add(source.Id) },
            Zone.Command => state with { Command = state.Command.Add(source.Id) },
            Zone.Graveyard => WithSeat(
                state, source.ControllerId, p => p with { Graveyard = p.Graveyard.Add(source.Id) }),
            Zone.Hand => WithSeat(
                state, source.ControllerId, p => p with { Hand = p.Hand.Add(source.Id) }),
            _ => WithSeat(
                state, source.ControllerId, p => p with { Library = p.Library.Add(source.Id) }),
        };
    }

    private static GameState WithSeat(
        GameState state, Guid who, Func<PlayerState, PlayerState> change) =>
        state with { Players = state.Players.SetItem(who, change(state.Players[who])) };

    /// <summary>
    /// The battery that does not depend on which card the probe is: steps, turns, and everything
    /// an ability's own source can have happen to it.
    /// </summary>
    /// <remarks>
    /// First in the order, and deliberately: the commonest triggers printed are "at the beginning
    /// of your upkeep", "when this enters" and "when this dies", so a healthy predicate usually
    /// accepts within the first few dozen probes and the corpus sweep costs almost nothing. The
    /// cost is paid by the predicates that accept nothing, which is the population being looked
    /// for.
    /// </remarks>
    private static List<Scene> PhaseScenes(CardDefinition filler, World world)
    {
        var scenes = new List<Scene>();

        foreach (var step in Enum.GetValues<TurnStep>())
        {
            foreach (var active in new[] { Mine, Theirs })
            {
                scenes.Add(new Scene(
                    Board(filler, filler, active, Zone.Battlefield, Zone.Graveyard, active, step,
                        new CombatState(), world),
                    [new StepBegan(step), new TurnBegan(3, active)]));
            }
        }

        // The source's own zone changes, every cause the engine records. "When this dies", "when
        // this enters", "when you discard this", "when this is put into exile" are all this shape
        // and between them are most of the printed triggers in the game.
        foreach (var (from, to) in ZoneMoves)
        {
            var events = new List<GameEvent>();
            foreach (var cause in Enum.GetValues<MoveCause>())
            {
                events.Add(new ObjectMoved(SourceId, SourceId, from, to, Mine, cause));
                events.Add(new ObjectMoved(SourceId, SourceId, from, to, Theirs, cause));
            }

            scenes.Add(new Scene(
                Board(filler, filler, Mine, to, from, Mine, TurnStep.PrecombatMain,
                    new CombatState(), world),
                events));
        }

        // The source in combat, on both sides of both declarations.
        var attacking = new CombatState
        {
            Attackers = ImmutableDictionary<ObjectId, AttackTarget>.Empty
                .Add(SourceId, AttackTarget.Player(Theirs))
                .Add(ProbeId, AttackTarget.Player(Theirs))
                .Add(ProbeWasId, AttackTarget.Player(Theirs)),
            AttackersDeclared = true,
        };

        scenes.Add(new Scene(
            Board(filler, filler, Mine, Zone.Battlefield, Zone.Battlefield, Mine,
                TurnStep.DeclareAttackers, attacking, world),
            [
                new AttackersDeclared(ImmutableDictionary<ObjectId, AttackTarget>.Empty
                    .Add(SourceId, AttackTarget.Player(Theirs))),
                new AttackersDeclared(ImmutableDictionary<ObjectId, AttackTarget>.Empty
                    .Add(SourceId, AttackTarget.At(Theirs, ProbeId))),
                new AttackersDeclared(ImmutableDictionary<ObjectId, AttackTarget>.Empty
                    .Add(SourceId, AttackTarget.Player(Theirs))
                    .Add(ProbeId, AttackTarget.Player(Theirs))
                    .Add(ProbeWasId, AttackTarget.Player(Theirs))),
            ]));

        var blocking = new CombatState
        {
            Attackers = ImmutableDictionary<ObjectId, AttackTarget>.Empty
                .Add(SourceId, AttackTarget.Player(Theirs))
                .Add(ProbeId, AttackTarget.Player(Mine)),
            Blockers = ImmutableDictionary<ObjectId, ImmutableList<ObjectId>>.Empty
                .Add(SourceId, [ProbeId])
                .Add(ProbeId, [SourceId]),
            AttackersDeclared = true,
            BlockersDeclared = true,
        };

        scenes.Add(new Scene(
            Board(filler, filler, Theirs, Zone.Battlefield, Zone.Battlefield, Mine,
                TurnStep.DeclareBlockers, blocking, world),
            [
                new BlockersDeclared(ImmutableDictionary<ObjectId, ImmutableList<ObjectId>>.Empty
                    .Add(SourceId, [ProbeId])),
                new BlockersDeclared(ImmutableDictionary<ObjectId, ImmutableList<ObjectId>>.Empty
                    .Add(ProbeId, [SourceId])),
                new BlockersDeclared(ImmutableDictionary<ObjectId, ImmutableList<ObjectId>>.Empty
                    .Add(ProbeId, [SourceId, ProbeWasId])),
                new CombatDamageDealt([SourceId], [SourceId]),
                new CombatDamageDealt([SourceId], []),
                new CombatDamageDealt([SourceId, ProbeId], [SourceId, ProbeId]),
                new CombatDamageDealt([ProbeId], [ProbeId]),
            ]));

        // A whole board attacking at once. "Whenever you attack with five or more Soldiers" and
        // its siblings count the declaration, and three attackers is not five.
        if (world.Crowd.Count > 0)
        {
            var horde = ImmutableDictionary<ObjectId, AttackTarget>.Empty
                .Add(SourceId, AttackTarget.Player(Theirs));

            var dealers = ImmutableList<ObjectId>.Empty.Add(SourceId);

            foreach (var id in Extras(world)
                .Where(o => o.ControllerId == Mine && o.Zone == Zone.Battlefield)
                .Select(o => o.Id))
            {
                horde = horde.SetItem(id, AttackTarget.Player(Theirs));
                dealers = dealers.Add(id);
            }

            scenes.Add(new Scene(
                Board(filler, filler, Mine, Zone.Battlefield, Zone.Battlefield, Mine,
                    TurnStep.DeclareAttackers,
                    new CombatState { Attackers = horde, AttackersDeclared = true },
                    world),
                [new AttackersDeclared(horde), new CombatDamageDealt(dealers, dealers)]));
        }

        // Everything else that happens to a permanent in play, aimed at the source.
        var plain = new List<GameEvent>
        {
            new PermanentTapped(SourceId),
            new PermanentAttached(ProbeId, SourceId),
            new PermanentAttached(SourceId, ProbeId),
            new PermanentAttached(SourceId, null, Theirs),
            new PermanentTurned(SourceId, false),
            new PermanentTurned(SourceId, true),
            new PermanentTransformed(SourceId, 1),
            new AbilityActivated(Mine, SourceId, "a0", "text"),
            new AbilityActivated(Theirs, SourceId, "a0", "text"),
            new TargetsChosen(StackId, [new Target(TargetKind.Permanent, SourceId, default)], 0),
            new TargetsChosen(StackId, [new Target(TargetKind.Player, default, Mine)], 0),
            new CreatureExploited(SourceId, ProbeId),
            new CreatureExploited(ProbeId, SourceId),
            new CreaturesPaired(SourceId, ProbeId),
            new PlayerDamaged(Mine, SourceId, 3, true),
            new PlayerDamaged(Theirs, SourceId, 3, true),
            new PlayerDamaged(Mine, SourceId, 3, false),
            new PlayerDamaged(Theirs, SourceId, 3, false),
            new DamageMarked(SourceId, 3, false, ProbeId, true),
            new DamageMarked(SourceId, 3, false, ProbeId, false),
            new DamageMarked(SourceId, 3, true, ProbeId, true),
            new DamageMarked(ProbeId, 3, false, SourceId, true),
            new DamageMarked(ProbeId, 3, false, SourceId, false),
            new LifeChanged(Mine, 3, 23),
            new LifeChanged(Mine, -3, 17),
            new LifeChanged(Theirs, 3, 23),
            new LifeChanged(Theirs, -3, 17),
            new ManaAdded(Mine, MtgEngine.Domain.Enums.ManaColor.Green, 1, null, SourceId),
            new ManaAdded(Theirs, MtgEngine.Domain.Enums.ManaColor.Green, 1, null, SourceId),
            new ManaAdded(Mine, null, 1, null, SourceId),

            // Cycling is an activated ability with a fixed id, and "when you cycle this" is the
            // activation of that id (CR 702.29a). A battery whose only activation carried a made
            // up id reported all thirty-five Gempalms as inert.
            new AbilityActivated(Mine, SourceId, "cycling", "Cycling"),
            new AbilityActivated(Theirs, SourceId, "cycling", "Cycling"),

            // Both doors of a Room, the front one included. CR 709.5f's trigger is keyed on the
            // half, and a battery that only ever unlocked half one never opened half zero.
            new HalfUnlocked(SourceId, 0),
            new HalfUnlocked(SourceId, 1),

            // The tally crossing a threshold, which is what expend reads (CR 700.14). Both totals
            // are on the event because neither side alone says a line was crossed.
            new ManaSpentCasting(Mine, 8, 0, 8),
            new ManaSpentCasting(Mine, 4, 0, 4),
            new ManaSpentCasting(Mine, 1, 3, 4),
            new ManaSpentCasting(Theirs, 8, 0, 8),
        };

        // "When you cast this" matches the spell by the card it was cast from rather than by id
        // (CR 400.7), so the object on the stack has to be carrying the source's own card — which
        // means naming the source itself as the spell. Nothing else in the battery ever casts it.
        foreach (var from in
            new[] { Zone.Hand, Zone.Graveyard, Zone.Exile, Zone.Library, Zone.Command })
        {
            plain.Add(new SpellCastEvent(Mine, SourceId, string.Empty, from));
            plain.Add(new SpellCastEvent(Theirs, SourceId, string.Empty, from));
        }

        foreach (var kind in world.Kinds)
        {
            plain.Add(new CountersChanged(SourceId, kind, 1));
            plain.Add(new CountersChanged(SourceId, kind, -1));
            plain.Add(new CountersChanged(SourceId, kind, -world.Counters));
            plain.Add(new CountersChanged(ProbeId, kind, 1));
        }

        scenes.Add(new Scene(
            Board(filler, filler, Mine, Zone.Battlefield, Zone.Battlefield, Mine,
                TurnStep.PrecombatMain, new CombatState(), world),
            plain));

        scenes.Add(new Scene(
            Board(filler, filler, Theirs, Zone.Battlefield, Zone.Battlefield, Theirs,
                TurnStep.PrecombatMain, new CombatState(), world),
            plain));

        return scenes;
    }

    /// <summary>
    /// The battery that turns on which card the probe is: what it does, what is done to it, and
    /// where it goes.
    /// </summary>
    private static List<Scene> ProbeScenes(
        CardDefinition filler, CardDefinition probe, Guid side, World world)
    {
        var scenes = new List<Scene>();
        var other = side == Mine ? Theirs : Mine;

        foreach (var (from, to) in ZoneMoves)
        {
            var events = new List<GameEvent>();
            foreach (var cause in Enum.GetValues<MoveCause>())
            {
                events.Add(new ObjectMoved(ProbeWasId, ProbeId, from, to, side, cause));

                // The same move with the two ids the other way round. CR 400.7 makes the object
                // that left and the object that arrived different, and which of the two a
                // predicate reads depends on the sentence: "when enchanted creature dies" asks
                // whether the id it is attached to is the one that *left*, so a battery that only
                // ever names the arriving object refuses every Aura in the game. Five cards were
                // reported inert on exactly that, and the fault was here rather than in them.
                events.Add(new ObjectMoved(ProbeId, ProbeWasId, from, to, side, cause));
            }

            scenes.Add(new Scene(
                Board(filler, probe, side, to, from, Mine, TurnStep.PrecombatMain,
                    new CombatState(), world),
                events));
        }

        var inPlay = new List<GameEvent>
        {
            new ObjectCreated(ProbeId, probe, side, side, Zone.Battlefield),
            new ObjectCreated(ProbeId, probe, side, side, Zone.Graveyard),
            new ObjectCreated(ProbeId, probe, side, side, Zone.Exile),
            new PermanentTapped(ProbeId),
            new PermanentAttached(ProbeId, SourceId),
            new PermanentAttached(SourceId, ProbeId),
            new PermanentTurned(ProbeId, false),
            new PermanentTransformed(ProbeId, 1),
            new AbilityActivated(side, ProbeId, "a0", "text"),
            new TargetsChosen(StackId, [new Target(TargetKind.Permanent, ProbeId, default)], 0),

            // The spell on the stack is this probe, so "becomes the target of an Aura spell" can
            // only be answered by aiming that spell at the trigger's own source.
            new TargetsChosen(StackId, [new Target(TargetKind.Permanent, SourceId, default)], 0),
            new TargetsChosen(StackId, [new Target(TargetKind.Player, default, Mine)], 0),
            new DamageMarked(ProbeId, 3, false, SourceId, true),
            new DamageMarked(SourceId, 3, false, ProbeId, true),
            new DamageMarked(ProbeId, 3, false, ProbeWasId, false),
            new PlayerDamaged(other, ProbeId, 3, true),
            new PlayerDamaged(side, ProbeId, 3, true),
            new PlayerDamaged(other, ProbeId, 3, false),
            new CreatureExploited(ProbeId, ProbeWasId),
            new CreaturesPaired(SourceId, ProbeId),
            new ManaAdded(side, MtgEngine.Domain.Enums.ManaColor.Green, 1, null, ProbeId),
            new ManaAdded(side, null, 1, null, ProbeId),
            new ManaAdded(other, MtgEngine.Domain.Enums.ManaColor.Blue, 1, null, ProbeId),
        };

        foreach (var kind in world.Kinds)
        {
            inPlay.Add(new CountersChanged(ProbeId, kind, 1));
            inPlay.Add(new CountersChanged(ProbeId, kind, -1));
        }

        // The source arriving and leaving while this probe is on the board. "When this enters,
        // if you control two or more Gates" is an entry trigger whose whole content is a question
        // about somebody else's permanent, so the arrival has to happen with that permanent
        // already standing there — and the arrival only ever happened on the probe-less battery.
        foreach (var (from, to) in ZoneMoves)
        {
            foreach (var cause in Enum.GetValues<MoveCause>())
            {
                inPlay.Add(new ObjectMoved(SourceId, SourceId, from, to, Mine, cause));
                inPlay.Add(new ObjectMoved(SourceId, SourceId, from, to, side, cause));
            }
        }

        scenes.Add(new Scene(
            Board(filler, probe, side, Zone.Battlefield, Zone.Battlefield, Mine,
                TurnStep.PrecombatMain, new CombatState(), world),
            inPlay));

        var casting = new List<GameEvent>();
        foreach (var from in
            new[] { Zone.Hand, Zone.Graveyard, Zone.Exile, Zone.Library, Zone.Command })
        {
            casting.Add(new SpellCastEvent(side, StackId, probe.Name, from));
        }

        casting.Add(
            new ObjectMoved(ProbeWasId, StackId, Zone.Hand, Zone.Stack, side, MoveCause.Cast));

        // Both turns. "Whenever you cast a spell during an opponent's turn" is nine printed
        // cards and a battery that only ever casts on its own turn answers no to all of them.
        foreach (var active in new[] { Mine, Theirs })
        {
            scenes.Add(new Scene(
                Board(filler, probe, side, Zone.Stack, Zone.Hand, active,
                    TurnStep.PrecombatMain, new CombatState(), world),
                casting));
        }

        scenes.Add(new Scene(
            Board(filler, probe, side, Zone.Graveyard, Zone.Hand, Mine, TurnStep.PrecombatMain,
                new CombatState(), world),
            [
                new CardsDiscarded(side, [ProbeId]),
                new CardsLeftGraveyard(side, [ProbeId]),
                new CardsRevealed(side, [ProbeId]),
                new CardsDiscarded(side, [ProbeId, ProbeWasId]),
            ]));

        // The phases, with this probe on the battlefield. "At the beginning of combat on your
        // turn, if you control an Ajani planeswalker" is a phase trigger whose whole content is a
        // question about one permanent, so the deep sweep can only answer it if the steps are
        // fired while the probe is standing there.
        foreach (var active in new[] { Mine, Theirs })
        {
            var phases = new List<GameEvent> { new TurnBegan(3, active) };
            foreach (var step in Enum.GetValues<TurnStep>())
                phases.Add(new StepBegan(step));

            scenes.Add(new Scene(
                Board(filler, probe, side, Zone.Battlefield, Zone.Battlefield, active,
                    TurnStep.Upkeep, new CombatState(), world),
                phases));
        }

        var probeAttacks = new CombatState
        {
            Attackers = ImmutableDictionary<ObjectId, AttackTarget>.Empty
                .Add(ProbeId, AttackTarget.Player(other)),
            AttackersDeclared = true,
        };

        scenes.Add(new Scene(
            Board(filler, probe, side, Zone.Battlefield, Zone.Battlefield, side,
                TurnStep.DeclareAttackers, probeAttacks, world),
            [
                new AttackersDeclared(ImmutableDictionary<ObjectId, AttackTarget>.Empty
                    .Add(ProbeId, AttackTarget.Player(other))),
                new AttackersDeclared(ImmutableDictionary<ObjectId, AttackTarget>.Empty
                    .Add(ProbeId, AttackTarget.At(other, SourceId))),
                new AttackersDeclared(ImmutableDictionary<ObjectId, AttackTarget>.Empty
                    .Add(ProbeId, AttackTarget.Player(other))
                    .Add(SourceId, AttackTarget.Player(Theirs))),
                new BlockersDeclared(ImmutableDictionary<ObjectId, ImmutableList<ObjectId>>.Empty
                    .Add(ProbeId, [SourceId])),
                new BlockersDeclared(ImmutableDictionary<ObjectId, ImmutableList<ObjectId>>.Empty
                    .Add(SourceId, [ProbeId])),
                new CombatDamageDealt([ProbeId], [ProbeId]),
                new CombatDamageDealt([ProbeId], []),
            ]));

        return scenes;
    }

    /// <summary>
    /// One instance of every other event type in the assembly, built by reflection.
    /// </summary>
    /// <remarks>
    /// The hand-written battery above is a list somebody wrote, and a list somebody wrote is a
    /// list somebody can leave a family off. This closes that: the sweep walks every concrete
    /// <see cref="GameEvent"/> in <c>MtgEngine.Rules</c>, fills its constructor from a table of
    /// values keyed on parameter type, and reports by name every type it could not build — so a
    /// blind spot is printed rather than assumed away.
    /// </remarks>
    private static List<GameEvent> TailEvents(
        CardDefinition probe, Guid side, ICollection<string> unbuildable)
    {
        var built = new List<GameEvent>();

        foreach (var type in typeof(GameEvent).Assembly.GetTypes())
        {
            if (type.IsAbstract
                || !typeof(GameEvent).IsAssignableFrom(type)
                || HandBuilt.Contains(type.Name))
            {
                continue;
            }

            var ctor = type.GetConstructors()
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault();

            if (ctor is null)
            {
                unbuildable.Add(type.Name);
                continue;
            }

            var choices = new List<IReadOnlyList<object?>>();
            var readable = true;

            foreach (var parameter in ctor.GetParameters())
            {
                if (ValuesFor(parameter.ParameterType, probe, side, 0) is not { } values)
                {
                    readable = false;
                    break;
                }

                choices.Add(values);
            }

            if (!readable)
            {
                unbuildable.Add(type.Name);
                continue;
            }

            // A bounded cross product rather than a diagonal. The diagonal this started as took
            // the ith value of every parameter at once, so a two-parameter event never saw its
            // second parameter's later values with its first parameter's first — which is exactly
            // the combination "when you unlock this door" needed, and all twenty Rooms were
            // reported inert. Sampled with a stride when the product is large, so the spread is
            // across the whole space rather than across its first corner.
            var product = choices.Aggregate(1L, (all, one) => all * one.Count);
            var variants = (int)Math.Min(product, 48);

            for (var i = 0; i < variants; i++)
            {
                var at = product <= 48 ? i : (i * 2654435761L) % product;
                var args = new object?[choices.Count];

                for (var j = 0; j < choices.Count; j++)
                {
                    args[j] = choices[j][(int)(at % choices[j].Count)];
                    at /= choices[j].Count;
                }

                try
                {
                    built.Add((GameEvent)ctor.Invoke(args));
                }
                catch (TargetInvocationException)
                {
                    unbuildable.Add(type.Name);
                }
            }
        }

        return built;
    }

    /// <summary>Sample values for one constructor parameter, or null when none can be made.</summary>
    private static IReadOnlyList<object?>? ValuesFor(
        Type type, CardDefinition probe, Guid side, int depth)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying)
            return ValuesFor(underlying, probe, side, depth);

        if (type == typeof(ObjectId))
            return [ProbeId, SourceId, StackId];

        if (type == typeof(Guid))
            return [side, Mine, Theirs];

        // Every small number, not a selection of them. "When this Class becomes level 3" is an
        // equality on the level the event carries, and a list that jumped from two to four
        // reported both printed Classes as inert for want of a three.
        if (type == typeof(int))
            return [1, 2, 0, 3, 4, 5, 8];

        if (type == typeof(long))
            return [1L];

        if (type == typeof(bool))
            return [true, false];

        if (type == typeof(string))
            return [probe.Name, string.Empty];

        if (type == typeof(CardDefinition))
            return [probe];

        if (type.IsEnum)
            return [.. Enum.GetValues(type).Cast<object?>().Take(8)];

        if (type.IsGenericType && Sequence(type, probe, side, depth) is { } sequence)
            return sequence;

        if (depth >= 2)
            return null;

        var ctor = type.GetConstructors()
            .OrderBy(c => c.GetParameters().Length)
            .FirstOrDefault();

        if (ctor is null)
            return type.IsValueType ? [Activator.CreateInstance(type)] : null;

        var parameters = ctor.GetParameters();
        var args = new object?[parameters.Length];

        for (var i = 0; i < args.Length; i++)
        {
            if (ValuesFor(parameters[i].ParameterType, probe, side, depth + 1) is not { } values)
                return null;

            args[i] = values[0];
        }

        try
        {
            return [ctor.Invoke(args)];
        }
        catch (TargetInvocationException)
        {
            return null;
        }
    }

    /// <summary>A one- or two-element collection of whatever the generic wants, or null.</summary>
    private static IReadOnlyList<object?>? Sequence(
        Type type, CardDefinition probe, Guid side, int depth)
    {
        var definition = type.GetGenericTypeDefinition();
        var arguments = type.GetGenericArguments();

        if (definition == typeof(ImmutableDictionary<,>))
        {
            if (ValuesFor(arguments[0], probe, side, depth + 1) is not { } keys
                || ValuesFor(arguments[1], probe, side, depth + 1) is not { } values)
            {
                return null;
            }

            var pair = typeof(KeyValuePair<,>).MakeGenericType(arguments);
            var entries = Array.CreateInstance(pair, 1);
            entries.SetValue(Activator.CreateInstance(pair, keys[0], values[0]), 0);

            return
            [
                typeof(ImmutableDictionary)
                    .GetMethods()
                    .First(m => m.Name == "CreateRange" && m.GetParameters().Length == 1)
                    .MakeGenericMethod(arguments)
                    .Invoke(null, [entries]),
            ];
        }

        if (definition != typeof(ImmutableList<>)
            && definition != typeof(ImmutableHashSet<>)
            && definition != typeof(IReadOnlyList<>)
            && definition != typeof(IReadOnlyCollection<>)
            && definition != typeof(IEnumerable<>))
        {
            return null;
        }

        if (ValuesFor(arguments[0], probe, side, depth + 1) is not { } items)
            return null;

        var made = Array.CreateInstance(arguments[0], Math.Min(2, items.Count));
        for (var i = 0; i < made.Length; i++)
            made.SetValue(items[i], i);

        var factory = definition == typeof(ImmutableHashSet<>)
            ? typeof(ImmutableHashSet)
            : typeof(ImmutableList);

        return
        [
            factory.GetMethods()
                .First(m => m.Name == "CreateRange" && m.GetParameters().Length == 1)
                .MakeGenericMethod(arguments[0])
                .Invoke(null, [made]),
        ];
    }

    /// <summary>Whether the predicate says yes, with a throw counted as no.</summary>
    /// <remarks>
    /// A predicate handed a board it never expected can throw, and a throw is not an answer. It is
    /// counted rather than swallowed: a family of predicates that throws on every probe would look
    /// exactly like a family that refuses every probe, and the two want different fixes.
    /// </remarks>
    private static bool Fires(
        TriggeredAbilityDefinition trigger,
        GameEvent happened,
        GameState state,
        TriggerSource source,
        int[] threw)
    {
        try
        {
            return trigger.Triggers(happened, state, source);
        }
        catch (Exception)
        {
            threw[0]++;
            return false;
        }
    }

    /// <summary>Whether any scene in this battery makes the trigger fire.</summary>
    private static bool AcceptsAnything(
        TriggeredAbilityDefinition trigger,
        CardDefinition card,
        World world,
        IEnumerable<Scene> battery,
        int[] threw)
    {
        var source = At(SourceId, card, Mine, trigger.FunctionsFrom, 1, world) with
        {
            // "If it was kicked with its {1}{U} kicker" names a cost, and the only place the cost
            // can come from is the card. Taken off its own printed text — every run of mana
            // symbols on it — rather than guessed, because a guessed list would answer no to the
            // twelve Battlemages for a reason that has nothing to do with the compiler.
            KickedWith = world.Busy ? [.. ManaRuns(card.OracleText)] : [],
        };
        var subject = new TriggerSource(source, NoAbilities.Instance);

        foreach (var scene in battery)
        {
            var state = WithSource(scene.State, source);

            foreach (var happened in scene.Events)
            {
                if (Fires(trigger, happened, state, subject, threw))
                    return true;
            }
        }

        return false;
    }

    /// <summary>Every run of mana symbols a card prints, which is where its kicker costs are.</summary>
    private static IEnumerable<string> ManaRuns(string text) =>
        ManaRun().Matches(text).Select(m => m.Value).Distinct(StringComparer.Ordinal);

    [System.Text.RegularExpressions.GeneratedRegex(@"(\{[^{}]+\})+")]
    private static partial System.Text.RegularExpressions.Regex ManaRun();

    /// <summary>A small, diverse set of printed cards: one per card-type line, one per subtype.</summary>
    private static List<CardDefinition> ScreenProbes(IReadOnlyList<CardDefinition> corpus)
    {
        var chosen = new Dictionary<string, CardDefinition>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var card in corpus)
        {
            foreach (var subtype in card.Subtypes)
                counts[subtype] = counts.GetValueOrDefault(subtype) + 1;
        }

        var popular = counts.OrderByDescending(e => e.Value).Take(40)
            .Select(e => e.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var card in corpus)
        {
            chosen.TryAdd("types:" + card.CardTypes, card);

            foreach (var subtype in card.Subtypes)
            {
                if (popular.Contains(subtype))
                    chosen.TryAdd("sub:" + subtype.ToLowerInvariant(), card);
            }
        }

        return [.. chosen.Values.DistinctBy(c => c.OracleId)];
    }

    /// <summary>One printed card for every (card types, subtype) pair the corpus has.</summary>
    /// <remarks>
    /// The set the whole design turns on. "Whenever a Forest you control enters" watching for a
    /// creature with the land type Forest is refuted by there being no card in here that is both,
    /// and confirmed by there being one — which is a claim about the printed corpus and not about
    /// anything the compiler believes.
    /// </remarks>
    private static List<CardDefinition> DeepProbes(IReadOnlyList<CardDefinition> corpus)
    {
        var chosen = new Dictionary<string, CardDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var card in corpus)
        {
            chosen.TryAdd(card.CardTypes + "/", card);

            foreach (var subtype in card.Subtypes)
            {
                chosen.TryAdd(card.CardTypes + "/" + subtype, card);

                foreach (var colour in card.Colors)
                    chosen.TryAdd(card.CardTypes + "/" + subtype + "/" + colour, card);
            }

            foreach (var supertype in card.Supertypes)
                chosen.TryAdd(card.CardTypes + "//" + supertype, card);

            chosen.TryAdd(
                card.CardTypes + "/p" + card.Power + "/t" + card.Toughness + "/" + card.Cmc, card);
        }

        return [.. Tokens, .. chosen.Values.DistinctBy(c => c.OracleId)];
    }

    /// <summary>
    /// A real board's worth of printed cards: a party, a spread of powers, one of everything.
    /// </summary>
    /// <remarks>
    /// Chosen by description rather than by name, so the crowd survives a corpus that gains and
    /// loses printings. Each entry is here because some printed intervening-if asks for it: the
    /// four party classes (CR 700.8), creatures whose powers differ, the five basic land types,
    /// a planeswalker, a battle, a multicoloured permanent, and one card of every type for the
    /// graveyard, which is where "four or more card types among cards in your graveyard" looks.
    /// </remarks>
    private static List<CardDefinition> Crowd(IReadOnlyList<CardDefinition> corpus)
    {
        var crowd = new List<CardDefinition>();

        void Want(string why, Func<CardDefinition, bool> matches)
        {
            var found = corpus.FirstOrDefault(matches)
                ?? throw new InvalidOperationException(
                    $"the corpus has no {why} for the crowd.");

            if (!crowd.Contains(found))
                crowd.Add(found);
        }

        foreach (var kind in new[] { "Cleric", "Rogue", "Warrior", "Wizard", "Human", "Elf" })
        {
            var wanted = kind;
            Want(
                wanted,
                c => c.CardTypes == MtgEngine.Domain.Enums.CardType.Creature
                    && c.Subtypes.Contains(wanted, StringComparer.OrdinalIgnoreCase));
        }

        for (var power = 0; power <= 5; power++)
        {
            var wanted = power;
            Want(
                $"creature with power {wanted}",
                c => c.CardTypes == MtgEngine.Domain.Enums.CardType.Creature
                    && c.Power == wanted);
        }

        foreach (var basic in new[] { "Plains", "Island", "Swamp", "Mountain", "Forest" })
        {
            var wanted = basic;
            Want(wanted, c => c.Name.Equals(wanted, StringComparison.Ordinal));
        }

        foreach (var type in Enum.GetValues<MtgEngine.Domain.Enums.CardType>())
        {
            var wanted = type;
            if (wanted is MtgEngine.Domain.Enums.CardType.None
                or MtgEngine.Domain.Enums.CardType.Token)
            {
                continue;
            }

            // Tribal is never a card's whole type line (CR 308.1 was repealed), so the crowd
            // simply has no member for it. A miss here is the corpus saying so, not a fault.
            if (corpus.FirstOrDefault(c => c.CardTypes == wanted) is { } only
                && !crowd.Contains(only))
            {
                crowd.Add(only);
            }
        }

        foreach (var wanted in new[] { "Desert", "Gate", "Knight", "Soldier", "Dragon", "Equipment" })
        {
            var kind = wanted;
            if (corpus.FirstOrDefault(c =>
                    c.Subtypes.Contains(kind, StringComparer.OrdinalIgnoreCase)) is { } typed
                && !crowd.Contains(typed))
            {
                crowd.Add(typed);
            }
        }

        // Tokens are not in the corpus — it is what a deck could contain — so the only way a
        // board can hold one is to make it. "If you control a token" and "three or more tokens"
        // are printed sentences no corpus card can ever answer.
        crowd.AddRange(Tokens);

        Want("multicoloured permanent", c => c.Colors.Count >= 2 && c.IsPermanentType);
        Want("colourless artifact creature", c => c.Colors.Count == 0 && c.IsArtifact && c.IsCreature);
        Want("legendary permanent", c => c.Supertypes.Contains("Legendary") && c.IsPermanentType);
        Want("snow permanent", c => c.Supertypes.Contains("Snow") && c.IsPermanentType);
        Want("high mana value card", c => c.Cmc >= 8);

        return crowd;
    }

    /// <summary>The token permanents a board can hold that no printed card can be.</summary>
    private static readonly CardDefinition[] Tokens =
    [
        new()
        {
            OracleId = "probe-token-soldier",
            Name = "Soldier Probe Token",
            CardTypes = MtgEngine.Domain.Enums.CardType.Creature
                | MtgEngine.Domain.Enums.CardType.Token,
            Subtypes = ["Soldier"],
            Power = 1,
            Toughness = 1,
            Colors = [MtgEngine.Domain.Enums.ManaColor.White],
        },
        new()
        {
            OracleId = "probe-token-treasure",
            Name = "Treasure Probe Token",
            CardTypes = MtgEngine.Domain.Enums.CardType.Artifact
                | MtgEngine.Domain.Enums.CardType.Token,
            Subtypes = ["Treasure"],
        },
        new()
        {
            OracleId = "probe-token-clue",
            Name = "Clue Probe Token",
            CardTypes = MtgEngine.Domain.Enums.CardType.Artifact
                | MtgEngine.Domain.Enums.CardType.Token,
            Subtypes = ["Clue"],
        },
    ];

    /// <summary>The whole screen battery for one board shape.</summary>
    private static List<Scene> Battery(
        CardDefinition filler,
        IReadOnlyList<CardDefinition> probes,
        World world,
        ICollection<string> unbuildable)
    {
        var battery = PhaseScenes(filler, world);

        var tail = new List<GameEvent>();
        foreach (var probe in probes.Take(3))
        {
            foreach (var side in new[] { Mine, Theirs })
                tail.AddRange(TailEvents(probe, side, unbuildable));
        }

        battery.Add(new Scene(
            Board(filler, filler, Mine, Zone.Battlefield, Zone.Graveyard, Mine,
                TurnStep.PrecombatMain, new CombatState(), world),
            tail));

        // A crowded board carries fifty extra objects, so the per-probe half of the battery is
        // left off it: the questions a crowd is there to answer are all asked by phase triggers
        // about the board, and building four thousand fifty-object boards is a gigabyte.
        if (world.Crowd.Count > 0)
            return battery;

        foreach (var probe in probes)
        {
            foreach (var side in new[] { Mine, Theirs })
                battery.AddRange(ProbeScenes(filler, probe, side, world));
        }

        return battery;
    }

    /// <summary>
    /// Every triggered ability a complete card compiles has to be able to fire on some board.
    /// </summary>
    [Fact]
    public void Every_trigger_a_complete_card_compiles_can_fire_on_some_board()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present — skipping.");
            return;
        }

        // A big multicoloured creature, so a condition about a colour or about power four or
        // greater has something on the board to answer it.
        var filler = corpus.First(c =>
            c.CardTypes == MtgEngine.Domain.Enums.CardType.Creature
            && c.Power >= 5 && c.Toughness >= 5
            && c.Colors.Count >= 2 && c.Subtypes.Count > 0);

        var screen = ScreenProbes(corpus);
        var worlds = Worlds(Crowd(corpus), CounterNames(corpus));
        output.WriteLine($"counter kinds: {worlds[0].Kinds.Count}");
        var unbuildable = new SortedSet<string>(StringComparer.Ordinal);
        var battery = Battery(filler, screen, worlds[0], unbuildable);

        output.WriteLine(
            $"screen probes: {screen.Count}, scenes: {battery.Count}, "
                + $"events: {battery.Sum(s => s.Events.Count)}, worlds: {worlds.Length}");

        output.WriteLine(
            unbuildable.Count == 0
                ? "every GameEvent type was built"
                : $"event types the sweep could not build ({unbuildable.Count}): "
                    + string.Join(", ", unbuildable));

        var threw = new int[1];
        var candidates = new List<(CardDefinition Card, TriggeredAbilityDefinition Trigger)>();
        var inspected = 0;
        var exempt = 0;

        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);
            if (!compiled.IsComplete || compiled.Triggers.IsEmpty)
                continue;

            var delayed = EveryEffect(compiled)
                .OfType<DelayAbility>()
                .Select(d => d.AbilityId)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var trigger in compiled.Triggers)
            {
                // A state trigger watches no event at all (CR 603.8) and its predicate is
                // deliberately a refusal; a delayed ability is put on the stack by id (CR 603.7a)
                // and is never offered an event either. Both are alive by another path.
                if (trigger.StateCondition is not null || delayed.Contains(trigger.Id))
                {
                    exempt++;
                    continue;
                }

                inspected++;

                if (!AcceptsAnything(trigger, card, worlds[0], battery, threw))
                    candidates.Add((card, trigger));
            }
        }

        var remaining = new List<(CardDefinition Card, TriggeredAbilityDefinition Trigger)>(
            candidates);

        output.WriteLine(
            $"triggers inspected: {inspected} (exempt: {exempt}); "
                + $"after {worlds[0].Name}: {remaining.Count}");

        // The other board shapes. Built one at a time and thrown away, because a battery is forty
        // thousand boards and holding nine of them at once is a quarter of a gigabyte.
        foreach (var world in worlds.Skip(1))
        {
            if (remaining.Count == 0)
                break;

            var shaped = Battery(filler, screen, world, unbuildable);
            remaining.RemoveAll(c => AcceptsAnything(c.Trigger, c.Card, world, shaped, threw));
            output.WriteLine($"after {world.Name}: {remaining.Count}");
        }

        // Probes outer, candidates inner, and a candidate is dropped the moment one probe makes
        // it fire. The other order — sweep the whole probe set per candidate — rebuilds the same
        // few thousand boards once per candidate and takes hours; this builds each board once and
        // asks every surviving candidate about it, so the cost falls away as the survivors do.
        var deep = DeepProbes(corpus);

        foreach (var probe in deep)
        {
            if (remaining.Count == 0)
                break;

            foreach (var side in new[] { Mine, Theirs })
            {
                var scenes = ProbeScenes(filler, probe, side, worlds[0]);
                remaining.RemoveAll(
                    c => AcceptsAnything(c.Trigger, c.Card, worlds[0], scenes, threw));
            }
        }

        output.WriteLine(
            $"deep probes: {deep.Count}; after the deep sweep: {remaining.Count}; "
                + $"predicate throws: {threw[0]}");

        var dead = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var (card, trigger) in remaining)
        {
            if (!dead.TryGetValue(trigger.Text, out var who))
                dead[trigger.Text] = who = [];

            who.Add(card.Name);
        }

        foreach (var (text, who) in dead.OrderByDescending(e => e.Value.Count))
            output.WriteLine($"  {who.Count,4}  \"{text}\" — {string.Join(", ", who.Take(6))}");

        var unexplained = remaining
            .Select(c => c.Card.Name)
            .Where(name => !Understood.ContainsKey(name))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var stale = Understood.Keys
            .Where(name => !remaining.Exists(c => string.Equals(
                c.Card.Name, name, StringComparison.Ordinal)))
            .ToList();

        Assert.True(
            unexplained.Count == 0,
            $"{unexplained.Count} cards compile a trigger that fires on nothing this battery can "
                + "build, and nothing in this file says why. Either the card is inert — fix the "
                + "compiler — or the battery cannot reach it, in which case widen the battery "
                + $"rather than adding a name here:\n  {string.Join("\n  ", unexplained)}");

        Assert.True(
            stale.Count == 0,
            $"{stale.Count} cards are listed as understood and now fire on something. Remove the "
                + $"entry in the commit that fixed them:\n  {string.Join("\n  ", stale)}");
    }

    /// <summary>
    /// The cards this audit still reports, and what is understood about each.
    /// </summary>
    /// <remarks>
    /// Every entry here is a claim that somebody looked, and the reason is the evidence for it.
    /// Two kinds of thing end up in this list and they want opposite treatment, so each entry
    /// says which it is: a <b>live defect</b> the fix for is bigger than this round, and a
    /// <b>battery limit</b> where the card is fine and the probe set cannot reach it. There are
    /// no entries of the second kind today, and that is the point of the list rather than an
    /// accident — every battery limit found while this was built was closed by widening the
    /// battery instead, because a name here silences a real finding just as easily.
    /// <para>
    /// The screen started at 8,099 triggers and 1,256 of them fired on nothing. Every reduction
    /// from there to three was a battery limit found and closed: counters the compiler invents
    /// and no card prints ("fade", "echo"); a board with a lean, so "more lands than you" has a
    /// direction; a source that had been kicked, blitzed, disguised, given a gift and paid for
    /// with every colour; the two ids of a zone change tried both ways round, without which
    /// every Aura in the game refused; and a supporting cast, because half the printed triggers
    /// are "at the beginning of X, if Y" and Y is a question about a populated board.
    /// </para>
    /// </remarks>
    private static readonly ImmutableDictionary<string, string> Understood =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // --- Live defects, each needing more than this round ---------------------
            //
            // "Whenever equipped creature dies, if it was a Human, ..." — the intervening-if is
            // read by BoardConditions.SelfTypeLine, whose closure asks the question of the
            // ability's own *source*: "if (!state.TryGetObject(source.Id, out var self))" and
            // then the filter against `self`. The source is the Equipment, an Equipment is never
            // a Human, and the condition is therefore false on every board. Its own comment says
            // so out loud — "'It' is the source here rather than a target ... the pronoun in that
            // sentence has only one thing it can mean" — which is true of the clauses it was
            // written for and false here, where the trigger's subject is the creature that died.
            //
            // Not fixed here because the fix is a signature: BoardCondition carries a state, an
            // ability source, the source object and a *seat*, and there is nowhere to put the
            // object a trigger was about. That parameter list was widened once already, on
            // purpose, and widening it again is its own change with its own tests.
            ["Slayer's Plate"] =
                "live: \"if it was a Human\" is asked of the Equipment, which never is one",
            ["Avacyn's Collar"] =
                "live: the same clause on the same reader, and the same always-false answer",

            // "Whenever you cast an Adventure spell" — the cast reader treats "Adventure" as a
            // subtype and filters the cast card on it, and nothing in this engine ever produces
            // an object carrying it: an adventure is a *face* (CR 715.3), the spell put on the
            // stack keeps the creature card's own subtypes, and Game marks OnAdventure on the
            // exiled card after the spell has resolved. So no object the corpus can produce
            // satisfies the predicate, which is the twenty-three-card defect exactly, one card
            // wide. Fixing it means the cast event carrying which half was cast.
            ["Storyteller Pixie"] =
                "live: \"Adventure\" is filtered as a subtype and no object in this engine has it",
        }.ToImmutableDictionary(StringComparer.Ordinal);

    /// <summary>Every effect a compiled card runs, from wherever it hangs.</summary>
    private static IEnumerable<IEffect> EveryEffect(CompiledCard compiled)
    {
        foreach (var trigger in compiled.Triggers)
        {
            foreach (var effect in EffectTree.Flatten(trigger.Effects))
                yield return effect;
        }

        foreach (var ability in compiled.Activated)
        {
            foreach (var effect in EffectTree.Flatten(ability.Effects))
                yield return effect;
        }

        if (compiled.Spell is { } spell)
        {
            foreach (var effect in EffectTree.Flatten(spell.Effects))
                yield return effect;
        }
    }
}
