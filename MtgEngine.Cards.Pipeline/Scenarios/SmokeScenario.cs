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
public static partial class SmokeScenario
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
            Enters(board, face);
            return board.Build(id, $"playing {file.Name} puts it onto the battlefield", new PlayLandStep(Caster, file.Name), spellGoesToTheGraveyard: false, usedTheStack: false);
        }

        if (!Costs(board, face))
            return null;
        board.Printed(face.ManaCost);

        // CR 601.2b: X is announced before the spell resolves, so everything the resolution reads
        // it for — a filter bounded by it above all — has the number the cast is going to name.
        board.AnnouncedX = face.ManaCost is { Length: > 0 } printed && printed.Contains("{X}", StringComparison.Ordinal) ? AnnouncedX : null;

        // CR 601.2c: the key the one targeted player was announced under, read before anything is
        // asserted, so an amount about "that player" knows the opponent is who it is about.
        board.AimedPlayerKey = OneTargetedPlayer(file.Definition);

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

            // CR 603.6b: a permanent spell's own arrival trigger goes on the stack as the spell
            // resolves, so what it does is part of the play — and a trigger whose choice would
            // otherwise take the card that just arrived needs the board to stand something older.
            Enters(board, face);
        }
        else if (!Read(board, face))
        {
            return null;
        }

        if (board.Empty || board.Targets > board.TargetLimit)
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
            // CR 121.1 with CR 119.4: what the one player the spell announced draws and loses,
            // when both are halves of numbers the board itself stocked.
            case Draw or LoseLife when board.AimedPlayerKey is { } aimed && Drains(board, effect, aimed):
                break;

            // CR 121.1: drawing puts cards in hand, and the spell itself has left it.
            case Draw { Who: null or You } draw when Number(board, draw.N) is { } drawn && drawn > 0 && drawn <= LibrarySize:
                board.Caster(hand: drawn, library: -drawn);
                break;

            // CR 119.3: damage to a player is life lost.
            case DealDamage { To: null, ToPlayer: not null and not You } burn when Number(board, burn.N) is { } dealt && dealt > 0:
                board.Lives(false, -dealt);
                break;

            // CR 115.4: "any target" is one of a creature, a player, a planeswalker or a battle, and
            // the choice is the scenario's — it takes the opponent, who is always there to be aimed
            // at (CR 601.2c), and CR 120.3a turns the damage into life lost.
            case DealDamage { To: AnyTarget { Count: null or Fixed { N: 1 }, UpTo: false }, ToPlayer: null } bolt
                when Number(board, bolt.N) is { } struck && struck > 0:
                board.AimsAtPlayer(Opponent);
                board.Lives(false, -struck);
                break;

            // CR 704.5g: three damage to a two-toughness body is lethal, so the play can say the
            // creature it was aimed at is in a graveyard rather than that damage was dealt.
            case DealDamage { To: Target hit, ToPlayer: null } lethal when Lethal(board, lethal, hit):
                break;

            case GainLife { Who: null or You } gain when Number(board, gain.N) is { } gained && gained > 0:
                board.Lives(true, gained);
                break;

            case LoseLife { Who: not null and not You } drain when Number(board, drain.N) is { } lost && lost > 0:
                board.Lives(false, -lost);
                break;

            // CR 119.4: "you lose 2 life", or life equal to a remembered card's mana value (CR 202.3).
            case LoseLife { Who: null or You } paid when Number(board, paid.N) is { } cost && cost > 0:
                board.Lives(true, -cost);
                break;

            // CR 611.2a: a continuous effect on one target creature is created when the spell
            // resolves, and the target has to be named for the cast to be legal (CR 601.2c). A spell's
            // road only: an activation that aims is a road of its own (seam-requests/P19.md).
            case Apply { To: Target granted } when !board.PlaysFromBattlefield && Aimable(granted) is { Count: > 0 } bodies:
                board.AimedAmong(Mine(granted), bodies);
                board.Expect(new EventLogged(nameof(ContinuousEffectCreated)));
                break;

            // CR 608.2m as the card overrides it: "exile this card" sends the resolved spell to
            // exile instead of the graveyard.
            case Exile { What: Self, Until: null }:
                board.SpellEndsIn = Zone.Exile;
                break;

            // CR 608.2m with CR 400.7: "shuffle this card into its owner's library" is the same
            // road — the spell has already been moved by the time its resolution ends, so the
            // graveyard never gets it. The shuffle itself is the search's to assert (CR 701.20);
            // what this arm settles is only where the spell went.
            case Shuffle { IntoLibrary: Self, Who: null or You } when !board.PlaysFromBattlefield:
                board.SpellEndsIn = Zone.Library;
                break;

            // CR 402.1: "return target card from your graveyard to your hand" — the graveyard is
            // stocked with a card the filter describes, under an alias, and the spell is aimed at
            // it; afterwards it is in hand and the graveyard holds only the spell.
            case Move { What: Target dug, To: Zone.Hand, Where: Placement.Default, UnderControlOf: null, Tapped: false, FaceDown: false, Attacking: false, Transformed: false, WithCounters: null, AttachedTo: null }
                when dug is { Count: null or Fixed { N: 1 }, UpTo: false } && dug.Filter.OwnedBy is null or You
                    && SmokeFillers.Buried(dug.Filter, board.AnnouncedX) is { } buried:
                board.Expect(new InZone(board.Buries(buried), Zone.Hand, Caster));
                board.Caster(hand: 1, graveyard: -1);
                break;

            // CR 400.7: "return target nonland permanent to its owner's hand" takes the body off the
            // battlefield, and the hand it lands in is its owner's rather than the caster's.
            case Move { What: Target bounced, To: Zone.Hand, Where: Placement.Default, UnderControlOf: null, Tapped: false, FaceDown: false, Attacking: false, Transformed: false, WithCounters: null, AttachedTo: null }
                when bounced.Filter is { In: null or Zone.Battlefield, OwnedBy: null } && Aimable(bounced) is { Count: > 0 } standing:
                board.Expect(new InZone(board.AimedAmong(Mine(bounced), standing), Zone.Hand, Owner(bounced)));
                Moves(board, bounced.Filter.ControlledBy, hand: 1);
                break;

            // CR 701.14a: each creature deals damage equal to its power to the other. The first is
            // the six-power filler and the second the two-power one, so exactly one of them has
            // lethal damage marked (CR 704.5g) and the scenario can say which — the loser is in its
            // owner's graveyard and the winner is still on the battlefield. Both are targets, named
            // on the step in the order the record names them (CR 601.2c). Only a keyed pair plays
            // today: the engine hands an unkeyed Target every announced target rather than its own
            // slot, so the first creature would fight itself (seam-requests/P16.md); a keyed one is
            // remembered as announced and reads its own.
            case Fight { A: Target first, B: Target second } when Fightable(first) && Fightable(second):
                {
                    board.TargetLimit = 2;
                    var winner = board.Aimed(Mine(first), ScenarioFiller.CostlyCreature);
                    var loser = board.Aimed(Mine(second), ScenarioFiller.Creature);
                    board.Expect(new InZone(loser, Zone.Graveyard, Owner(second)));
                    board.Expect(new InZone(winner, Zone.Battlefield));
                    Moves(board, second.Filter.ControlledBy, graveyard: 1);
                }

                break;

            // CR 208.1 with CR 121.1: "draw cards equal to the greatest power among creatures you
            // control" — or the greatest toughness — draws that printed stat off the one creature
            // the board gives it to look at.
            case Draw { N: GreatestAmong greatest, Who: null or You } when GreatestStat(greatest) is { } stat:
                board.Place(true, ScenarioFiller.Creature, 1);
                board.Caster(hand: stat, library: -stat);
                break;

            // CR 700.1: "put any number of creature cards from your hand onto the battlefield"
            // over a hand of lands puts none, so the rest of the card is what the play shows.
            case Move { What: Choose chosen, To: Zone.Battlefield } when ChoosesNothing(chosen):
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

            // CR 701.5: a countered spell is put into its owner's graveyard, and the thing it was
            // going to do never happens — which is the half the play has to assert, because a spell
            // that resolved reaches the same graveyard (CR 608.2m).
            case Counter { What: Target countered, PutInto: null or Zone.Graveyard } when Counters(board, countered, withNothingLeftToPay: false):
                break;

            // CR 115.7: changing a spell's target moves what it does from the player it was aimed
            // at to the one the answer names, and the two life totals are what says it moved.
            case ChangeTarget { To: null, AllTargets: false } change when Retargets(board, change):
                break;

            // CR 118.6 with CR 701.5: "counter target spell unless its controller pays {1}" — the
            // opponent's pool holds exactly what their spell cost and is empty by the time they are
            // asked, so the payment cannot be made and the spell is countered. Only a mana payment:
            // a cost that eats an object is one the opponent might be able to meet, and which half
            // of the card the play then shows is not a generated scenario's to guess.
            case Unless { Do: Counter { What: Target unpaid, PutInto: null or Zone.Graveyard }, Paid: ManaCost { Mana.Length: > 0 } } when Counters(board, unpaid, withNothingLeftToPay: true):
                break;

            // CR 701.13: milling moves cards from the top of a library into that graveyard.
            case Mill { N: Fixed milled } mill when milled.N > 0 && milled.N <= LibrarySize:
                Moves(board, mill.Who, library: -milled.N, graveyard: milled.N);
                break;

            // CR 701.8: a discard moves a card from a hand to its owner's graveyard.
            case Discard { N: Fixed discarded, What: null, WholeHand: false } discard when discarded.N > 0 && discarded.N <= HandSize:
                Moves(board, discard.Who, hand: -discarded.N, graveyard: discarded.N);
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
            // CR 611.1 with CR 611.2c: an effect over "each" of nothing is never created, so the
            // board has to hold one the filter actually describes — on the side the filter names,
            // so a wipe-shaped effect kills someone else's and the caster's graveyard count holds.
            // A filter the fillers cannot answer ("white creatures you control") gets no scenario:
            // standing a body the filter never described asserted an event the game never logged.
            case Apply { To: Each affected } when SmokeFillers.Bodies(affected.Filter) is [var described, ..]:
                board.Place(affected.Filter.ControlledBy is You, described, 1);
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
                return Remembers(board, effect);
        }

        return true;
    }

    /// <summary>Milling and discarding name a player, and everyone who is not you is the opponent here.</summary>
    private static void Moves(SmokeBoard board, PlayerRef? who, int hand = 0, int library = 0, int graveyard = 0)
    {
        if (who is null or You)
            board.Caster(hand, library, graveyard);
        else
            board.Opponent(hand, library, graveyard);
    }

    /// <summary>How many bodies each player gets when the card is about permanents in general rather than one of them.</summary>
    private const int Bodies = 2;

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

    /// <summary>CR 701.14: one creature the target's filter admits, so a filler creature can stand in for it in a fight.</summary>
    [RuleRef("701.14")]
    private static bool Fightable(Target target) => Aimable(target).Contains(ScenarioFiller.Creature);
}
