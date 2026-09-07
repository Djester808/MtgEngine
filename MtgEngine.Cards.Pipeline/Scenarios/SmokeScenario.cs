using MtgEngine.Rules.Events;
using MtgEngine.Rules.Interpreter;
using MtgEngine.Rules.Scenarios;

namespace MtgEngine.Cards.Pipeline.Scenarios;

/// <summary>
/// One generated scenario per card: play it, and assert the direct consequence of its own
/// definition. A <c>Draw</c> raises the hand count, a <c>DealDamage</c> lowers a life total, a
/// <c>Destroy</c> puts the thing it was aimed at in a graveyard, a permanent arrives on the
/// battlefield and a land is played rather than cast.
/// </summary>
/// <remarks>
/// The smoke scenario is deliberately shallow — it is the floor under "coverage is counted in
/// verified plays", not the ceiling. It reads only the definition, so it cannot be wrong about
/// what the card is supposed to do without the definition being wrong too, and a card whose
/// effect it cannot assert gets no scenario at all rather than a scenario that always passes.
/// Nothing here asserts a card's flavour: only what the record in front of it says happens.
/// </remarks>
public static class SmokeScenario
{
    public const string Caster = "A";
    public const string Opponent = "B";

    /// <summary>Enough library that a draw, a mill and a search all have something to take (CR 121.4).</summary>
    [RuleRef("121.4")]
    public const int LibrarySize = 8;

    /// <summary>Enough cards in hand, beside the card under test, that a discard has something to discard.</summary>
    public const int HandSize = 3;

    /// <summary>Enough of every colour that a cost is never what a smoke scenario is testing.</summary>
    internal const string DeepPool = "WWWWWWWWWWUUUUUUUUUUBBBBBBBBBBRRRRRRRRRRGGGGGGGGGG999999";

    /// <summary>CR 601.2b: X is announced when the spell is cast, and one is the smallest value worth playing.</summary>
    [RuleRef("601.2b")]
    public const int AnnouncedX = 1;

    public const string Generator = "smoke";

    /// <summary>The scenario for this card, or null when its definition says nothing a play can assert.</summary>
    public static Scenario? Of(CardFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var face = file.Definition.Faces.FirstOrDefault();
        if (face is null)
            return null;

        var board = new SmokeBoard(file.Name);
        var id = "smoke-" + file.OracleId;

        // CR 305.9: a land is played, not cast, and arriving on the battlefield is the whole of it.
        if ((face.Types.Types & CardType.Land) != 0)
        {
            board.Expect(new InZone(file.Name, Zone.Battlefield));
            return board.Build(id, $"playing {file.Name} puts it onto the battlefield", new PlayLandStep(Caster, file.Name), spellGoesToTheGraveyard: false, usedTheStack: false);
        }

        if (!Costs(board, face))
            return null;
        board.Printed(face.ManaCost);

        // CR 601.2b: X is announced before the spell resolves, so everything the resolution reads
        // it for — a filter bounded by it above all — has the number the cast is going to name.
        board.AnnouncedX = face.ManaCost is { Length: > 0 } printed && printed.Contains("{X}", StringComparison.Ordinal) ? AnnouncedX : null;

        var permanent = Permanent(face.Types.Types);
        if (permanent)
        {
            if (!Enchant(board, face))
                return null;

            // CR 303.4f: an Aura enters attached to what it was cast at, and the attachment is what
            // the scenario means; being on the battlefield is true of an unattached one only for as
            // long as CR 704.5m takes to bury it. Everything else asserts the zone (CR 110.1).
            board.Expect(board.Enchanted is { } host
                ? new AttachedToIs(file.Name, host)
                : new InZone(file.Name, Zone.Battlefield));
        }
        else if (!Read(board, face))
        {
            return null;
        }

        if (board.Empty || board.Targets > 1)
            return null;

        // CR 601.2c: "target player" is announced with the spell, so the step names the opponent
        // whenever the definition asks for one; without it the resolution has nobody to remember.
        if (Linter.Walk(file.Definition).OfType<TargetPlayer>().Any())
            board.AimsAtPlayer(Opponent);

        var x = board.AnnouncedX;
        var cast = new CastStep(Caster, file.Name, board.TargetNames(), x, board.Modes(), CostObjects: board.CostObjects());
        var title = x is null
            ? $"casting {file.Name} does what its definition says"
            : FormattableString.Invariant($"casting {file.Name} with X={x} does what its definition says");
        return board.Build(id, title, cast, spellGoesToTheGraveyard: !permanent);
    }

    /// <summary>CR 608.2: what the spell does when it resolves, read straight off the definition.</summary>
    /// <returns>False when the card needs a choice a smoke scenario cannot make, so it gets none.</returns>
    [RuleRef("608.2")]
    private static bool Read(SmokeBoard board, Face face)
    {
        var effects = face.AbilityList.OfType<SpellAbility>().Select(a => a.Do).SelectMany(Flatten).ToList();
        foreach (var effect in effects)
        {
            if (!Assert(board, effect))
                return false;
        }

        return true;
    }

    /// <summary>
    /// CR 608.2: what one ability does when it resolves, asserted onto the board. Shared with
    /// <see cref="SmokeActivation"/>, so an activated ability's effect is read by the same code
    /// that reads a spell's and the two cannot drift apart.
    /// </summary>
    [RuleRef("608.2")]
    internal static bool Reads(SmokeBoard board, Effect effect)
    {
        foreach (var inner in Flatten(effect))
        {
            if (!Assert(board, inner))
                return false;
        }

        return true;
    }

    /// <summary>One effect's direct consequence, or nothing when the record says something a play cannot show.</summary>
    private static bool Assert(SmokeBoard board, Effect effect)
    {
        switch (effect)
        {
            // CR 121.1: drawing puts cards in hand, and the spell itself has left it.
            case Draw { N: Fixed n, Who: null or You } when n.N > 0 && n.N <= LibrarySize:
                board.Caster(hand: n.N, library: -n.N);
                break;

            // CR 119.3: damage to a player is life lost.
            case DealDamage { N: Fixed n, To: null, ToPlayer: not null and not You } when n.N > 0:
                board.Expect(new Life(Opponent, 20 - n.N));
                break;

            case GainLife { N: Fixed n, Who: null or You } when n.N > 0:
                board.Expect(new Life(Caster, 20 + n.N));
                break;

            case LoseLife { N: Fixed n, Who: not null and not You } when n.N > 0:
                board.Expect(new Life(Opponent, 20 - n.N));
                break;

            // CR 701.7: destroying a permanent puts it into its owner's graveyard. "Artifact or
            // enchantment" puts one of each on the board and aims at the first, so the scenario
            // shows the spell reaching the kind it was pointed at.
            case Destroy { What: Target target } when Aimable(target) is { Count: > 0 } bodies:
                board.Expect(new InZone(board.AimedAmong(Mine(target), bodies), Zone.Graveyard, Owner(target)));
                break;

            // CR 701.10: sacrificing does the same, at its controller's hand rather than the spell's.
            case Sacrifice { What: Target sacrificed } when Aimable(sacrificed) is { Count: > 0 } given:
                board.Expect(new InZone(board.AimedAmong(Mine(sacrificed), given), Zone.Graveyard, Owner(sacrificed)));
                break;

            // CR 701.16: "sacrifice a land" is chosen on resolution, so the board gives the caster
            // one of whatever kind to lose and counts it into their graveyard.
            case Sacrifice { What: null, Choice: { } offered, Who: null or You, N: null or Fixed { N: 1 } }
                when SmokeFillers.Bodies(offered) is [var lost, ..]:
                board.Place(true, lost, 1);
                board.Caster(graveyard: 1);
                break;

            // CR 406.2: an exiled permanent is in the exile zone, face up, and stays there.
            case Exile { What: Target exiled, Until: null } when Aimable(exiled) is { Count: > 0 } taken:
                board.Expect(new InZone(board.AimedAmong(Mine(exiled), taken), Zone.Exile, Owner(exiled)));
                break;

            // CR 111.1: a token is a permanent on the battlefield, and there is one more of them.
            case CreateToken { Who: null or You, CopyOf: null, N: null or Fixed } token:
                board.Tokens(token.N is Fixed made ? made.N : 1);
                break;

            // CR 121.2: counters go on the object they were put on and stay there.
            case PutCounters { On: Target on, N: Fixed count } counters when count.N > 0 && Aimable(on) is { Count: > 0 } bearers:
                board.Expect(new CountersAre(board.AimedAmong(Mine(on), bearers), counters.Counter, count.N));
                break;

            // CR 701.19 and CR 701.20: a search takes the card it found out of the library and puts
            // it where the effect said, and then the library is shuffled.
            case Search { From: Zone.Library, Who: null or You } search when Stock(board, search) is { Count: > 0 } found:
                Found(board, search, found);
                break;

            // CR 701.5: a countered spell is put into its owner's graveyard.
            case Counter { What: Target countered, PutInto: null or Zone.Graveyard } when Spell(countered):
                board.Expect(new InZone(board.SpellOnTheStack(), Zone.Graveyard, Opponent));
                break;

            // CR 701.13: milling moves cards from the top of a library into that graveyard.
            case Mill { N: Fixed milled } mill when milled.N > 0 && milled.N <= LibrarySize:
                Move(board, mill.Who, library: -milled.N, graveyard: milled.N);
                break;

            // CR 701.8: a discard moves a card from a hand to its owner's graveyard.
            case Discard { N: Fixed discarded, What: null, WholeHand: false } discard when discarded.N > 0 && discarded.N <= HandSize:
                Move(board, discard.Who, hand: -discarded.N, graveyard: discarded.N);
                break;

            // CR 701.21: tapping a permanent leaves it tapped.
            case Tap { What: Target tapped } when Aimable(tapped) is { Count: > 0 } bodies:
                board.Expect(new TappedIs(board.AimedAmong(Mine(tapped), bodies), true));
                break;

            // CR 701.7 over CR 109.2: a wipe destroys everything the filter describes, whoever
            // controls it, so both battlefields empty and both graveyards fill.
            case Destroy { What: Each all } when SmokeFillers.Wiped(all.Filter) is { } destroyed:
                Sweep(board, destroyed, Bodies, toGraveyard: true);
                break;

            // CR 406.2: an "exile all" empties the same two battlefields, into exile instead.
            case Exile { What: Each all, Until: null } when SmokeFillers.Wiped(all.Filter) is { } banished:
                Sweep(board, banished, Bodies, toGraveyard: false);
                break;

            // CR 701.16: each player sacrifices, so each player loses the one permanent they had.
            // The engine asks which one; a generated scenario answers with the first legal option.
            case Sacrifice { What: null, Choice: { } offered, Who: EachPlayer, N: null or Fixed { N: 1 } }
                when SmokeFillers.Wiped(offered) is { } each:
                Sweep(board, each, 1, toGraveyard: true);
                break;

            // CR 700.2: the modes are chosen as the spell is cast. A smoke scenario takes as few as
            // the card allows, in the order they are printed, and asserts whatever they do — so a
            // "choose one or more" is a choice of one and a "choose two" is the first two.
            case ChooseMode { Min: > 0 } choose when choose.Modes.Count >= choose.Min:
                for (var index = 0; index < choose.Min; index++)
                {
                    board.Mode(index);
                    foreach (var inner in Flatten(choose.Modes[index].Do))
                    {
                        if (!Assert(board, inner))
                            return false;
                    }
                }

                break;

            // CR 106.4: mana goes into a pool, and the pool is what the play leaves behind. With the
            // caster's pool stocked at exactly the printed cost, the cast empties it and what is
            // there afterwards is precisely what the card produced — so the scenario can name the
            // mana rather than settle for the event, which is logged just as loudly by a card that
            // produced the wrong colour.
            case AddMana { Mana.Fixed: { Length: > 0 } produced, Who: null or You, N: null } when board.PaysExactly():
                board.Expect(new ManaPoolIs(Caster, produced));
                break;

            case AddMana { Who: null or You }:
                board.Expect(new EventLogged(nameof(ManaChanged)));
                break;

            // CR 611.1: a continuous effect changes nothing about where anything is, so what a
            // play can show is that the game created the effect (seam-requests/P9.md § 2).
            case Apply { To: Each affected }:
                // An effect over "each" of nothing is not created at all, so the board has to hold
                // one of whatever it is about — on the side the filter names, so a wipe-shaped
                // effect kills someone else's and the caster's graveyard count still holds.
                board.Place(affected.Filter.ControlledBy is You, ScenarioFiller.Creature, 1);
                board.Expect(new EventLogged(nameof(ContinuousEffectCreated)));
                break;

            case Apply { To: Self } or ApplyToPlayer:
                board.Expect(new EventLogged(nameof(ContinuousEffectCreated)));
                break;

            // CR 121.1: "draw a card for each" draws as many as the board gives it to count.
            case Draw { N: CountOf counted, Who: null or You } when Countable(counted.Filter):
                {
                    board.Place(true, ScenarioFiller.Creature, Bodies);
                    var drawn = Math.Min(board.OnBattlefield(true), LibrarySize);
                    board.Caster(hand: drawn, library: -drawn);
                }

                break;

            case Destroy or Sacrifice or Exile or PutCounters or Counter or Tap:
                // The effect aims at something the board cannot place — a land, a spell of a kind,
                // a filtered permanent. Casting it would be refused for want of a legal target, so
                // the card gets no scenario rather than one that reports the runner's own gap.
                return false;

            default:
                break;
        }

        return true;
    }

    /// <summary>Milling and discarding name a player, and everyone who is not you is the opponent here.</summary>
    private static void Move(SmokeBoard board, PlayerRef? who, int hand = 0, int library = 0, int graveyard = 0)
    {
        if (who is null or You)
            board.Caster(hand, library, graveyard);
        else
            board.Opponent(hand, library, graveyard);
    }

    /// <summary>How many bodies each player gets when the card is about permanents in general rather than one of them.</summary>
    private const int Bodies = 2;

    /// <summary>
    /// CR 704.5m: an Aura on the battlefield attached to nothing is put into its owner's
    /// graveyard, so an Aura cast at nothing never verifies. "Enchant X" is the targeting
    /// restriction (CR 702.5b), and it is in the definition as a keyword ability with a filter —
    /// so the board places what the filter describes and the spell is cast at it. A filter naming
    /// something the board has no card for gets no scenario, rather than one that always fails.
    /// </summary>
    [RuleRef("704.5m")]
    private static bool Enchant(SmokeBoard board, Face face)
    {
        var enchant = face.AbilityList.OfType<KeywordAbility>().FirstOrDefault(k => k.Keyword == KeywordKind.Enchant);
        if (enchant is null)
            return true;
        if (enchant.Of is not { } filter || filter.In is not (null or Zone.Battlefield))
            return false;

        // "Enchant permanent" is the whole filter, and a creature is a permanent (CR 110.1).
        var anyPermanent = filter == new ObjectFilter(In: filter.In, Permanent: true, ControlledBy: filter.ControlledBy);
        if (!anyPermanent && !Simple(filter))
            return false;

        var card = anyPermanent ? ScenarioFiller.Creature : filter.Types switch
        {
            null or CardType.Creature => ScenarioFiller.Creature,
            CardType.Land => ScenarioFiller.Land,
            _ => null,
        };
        if (card is null)
            return false;

        board.Enchants(filter.ControlledBy is null or You, card);
        return true;
    }

    /// <summary>
    /// CR 601.2c: an additional cost that eats an object is chosen as the spell is cast, and the
    /// engine refuses a cast that names nothing for it ("There are not enough permanents to
    /// sacrifice", CR 601.2h). So the board places the body the cost will eat and the step names
    /// it. A cost the board has no card for gets no scenario, rather than one that reports the
    /// generator's own gap as the card failing.
    /// </summary>
    /// <returns>False when a printed cost cannot be paid from what the board can place.</returns>
    [RuleRef("601.2c")]
    private static bool Costs(SmokeBoard board, Face face)
    {
        foreach (var cost in face.AdditionalCosts ?? [])
        {
            switch (cost)
            {
                // CR 701.10: the cost sacrifices one permanent of the caster's, of whatever kind it
                // names — a creature, a land ("as an additional cost, sacrifice a land"), an
                // artifact. The board places whichever filler the cost's own filter admits.
                case SacrificeCost { Count: null or Fixed { N: 1 } } sacrifice
                    when sacrifice.What.ControlledBy is null or You && SmokeFillers.Bodies(sacrifice.What) is [var eaten, ..]:
                    board.Sacrificed(eaten);
                    break;

                // CR 701.8: the cost discards one card, any card, and the hand is filler.
                case DiscardCost { Count: null or Fixed { N: 1 }, What: null, Random: false }:
                    board.Discarded();
                    break;

                // A cost that eats an object the board cannot place is the CR 601.2h refusal
                // itself: no body, no cast. The card gets no scenario rather than one that
                // reports the generator's gap as the card failing.
                case SacrificeCost or DiscardCost:
                    return false;

                // Everything else — life, mana, counters on the spell's own source — is paid
                // without naming an object, so the engine settles it as it did before there was
                // anywhere to name one.
                default:
                    break;
            }
        }

        return true;
    }

    /// <summary>Bodies on both battlefields and nothing left of them: what a wipe or an each-player sacrifice does.</summary>
    [RuleRef("701.7")]
    private static void Sweep(SmokeBoard board, string body, int each, bool toGraveyard)
    {
        board.Place(true, body, each);
        board.Place(false, body, each);
        board.Expect(new ZoneCount(Caster, Zone.Battlefield, 0));
        board.Expect(new ZoneCount(Opponent, Zone.Battlefield, 0));
        if (!toGraveyard)
            return;
        board.Caster(graveyard: each);
        board.Opponent(graveyard: each);
    }

    /// <summary>
    /// CR 601.2c: the bodies a target's filter admits, or nothing when it describes a permanent the
    /// board has no card for. More than one target is a smoke scenario's limit, so a target that
    /// wants several is refused here rather than half-answered.
    /// </summary>
    [RuleRef("601.2c")]
    private static IReadOnlyList<string> Aimable(Target target) =>
        target is { Count: null or Fixed { N: 1 }, UpTo: false } ? SmokeFillers.Bodies(target.Filter) : [];

    /// <summary>
    /// CR 701.19: the library is stocked with the cards this search is going to find, on top, and
    /// the aliases they were stocked under are returned so the expectations can name them. A search
    /// for "up to N" finds N, because the runner answers with as many as the question allows — so
    /// the board stocks N and the scenario says where every one of them went, rather than asserting
    /// one card and staying silent about the rest. Empty when the search wants a card the runner
    /// has not got, or the library has no room left for them.
    /// </summary>
    [RuleRef("701.19")]
    private static IReadOnlyList<string> Stock(SmokeBoard board, Search search)
    {
        if (search.To is not (Zone.Hand or Zone.Graveyard or Zone.Battlefield or Zone.Library))
            return [];
        if (SmokeFillers.Findable(search.For, board.AnnouncedX) is not { } card)
            return [];

        var wanted = search.UpTo is Fixed { N: > 0 and var n } ? n : 1;
        var stocked = new List<string>(wanted);
        for (var i = 0; i < wanted && board.CanStock; i++)
            stocked.Add(board.Stocks(card));
        return stocked;
    }

    /// <summary>
    /// Where each found card ended up, and CR 701.20's shuffle. A search that puts the cards back
    /// on top of the library moves nothing, so the shuffle is all there is to see — and it is worth
    /// seeing, because a search that never shuffled would leave the library stacked. The shuffle is
    /// asserted as a shuffle and not as "something was reordered", which the backlog also logs.
    /// </summary>
    [RuleRef("701.20")]
    private static void Found(SmokeBoard board, Search search, IReadOnlyList<string> found)
    {
        var n = found.Count;
        switch (search.To)
        {
            case Zone.Hand:
                board.Caster(hand: n, library: -n);
                break;
            case Zone.Graveyard:
                board.Caster(graveyard: n, library: -n);
                break;
            case Zone.Battlefield:
                board.Caster(library: -n);
                board.Arrives(n);
                break;
            default:
                break;
        }

        if (search.To is not Zone.Library)
        {
            foreach (var card in found)
                board.Expect(new InZone(card, search.To));
        }

        if (search.ShuffleAfter)
            board.Expect(new Shuffled(Caster));
    }

    /// <summary>A count the board can supply: the caster's own creatures, and nothing else about them.</summary>
    private static bool Countable(ObjectFilter filter) =>
        Simple(filter) && filter.Types == CardType.Creature && filter.ControlledBy is You && filter.In is null or Zone.Battlefield;

    /// <summary>A target the board can put on the stack: a spell described by nothing but being one.</summary>
    private static bool Spell(Target target) =>
        target is { Count: null or Fixed { N: 1 }, UpTo: false } && Simple(target.Filter)
        && target.Filter.Types is null or CardType.Instant or (CardType.Instant | CardType.Sorcery);

    /// <summary>
    /// Every field the board cannot honour is null: a filter with a colour, a power or a predicate
    /// describes a permanent the runner has no card for, and guessing one would be inventing a card.
    /// </summary>
    private static bool Simple(ObjectFilter filter) =>
        filter == new ObjectFilter(In: filter.In, Types: filter.Types, ControlledBy: filter.ControlledBy, Spell: filter.Spell);

    private static bool Mine(Target target) => target.Filter.ControlledBy is You;

    private static string Owner(Target target) => Mine(target) ? Caster : Opponent;

    /// <summary>CR 205.2a: the permanent types. A spell of any of them ends up on the battlefield.</summary>
    [RuleRef("205.2a")]
    private static bool Permanent(CardType types) =>
        (types & (CardType.Creature | CardType.Artifact | CardType.Enchantment | CardType.Planeswalker | CardType.Battle)) != 0;

    /// <summary>A sequence is its parts; anything conditional is skipped, because a smoke scenario asserts only what always happens.</summary>
    private static IEnumerable<Effect> Flatten(Effect effect) =>
        effect is Sequence sequence ? sequence.Steps.SelectMany(Flatten) : [effect];
}
