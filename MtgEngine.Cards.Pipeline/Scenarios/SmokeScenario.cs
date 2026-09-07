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

        var permanent = Permanent(face.Types.Types);
        if (permanent)
        {
            // CR 110.1: a permanent spell resolves onto the battlefield, and that alone is worth asserting.
            board.Expect(new InZone(file.Name, Zone.Battlefield));
        }
        else if (!Read(board, face))
        {
            return null;
        }

        if (board.Empty || board.Targets > 1)
            return null;

        var x = face.ManaCost is { Length: > 0 } cost && cost.Contains("{X}", StringComparison.Ordinal) ? AnnouncedX : (int?)null;
        var cast = new CastStep(Caster, file.Name, board.TargetNames(), x);
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

            // CR 701.7: destroying a permanent puts it into its owner's graveyard.
            case Destroy { What: Target target } when Creature(target):
                board.Expect(new InZone(board.Body(Mine(target)), Zone.Graveyard, Owner(target)));
                break;

            // CR 701.10: sacrificing does the same, at its controller's hand rather than the spell's.
            case Sacrifice { What: Target sacrificed } when Creature(sacrificed):
                board.Expect(new InZone(board.Body(Mine(sacrificed)), Zone.Graveyard, Owner(sacrificed)));
                break;

            // CR 406.2: an exiled permanent is in the exile zone, face up, and stays there.
            case Exile { What: Target exiled, Until: null } when Creature(exiled):
                board.Expect(new InZone(board.Body(Mine(exiled)), Zone.Exile, Owner(exiled)));
                break;

            // CR 111.1: a token is a permanent on the battlefield, and there is one more of them.
            case CreateToken { Who: null or You, CopyOf: null, N: null or Fixed } token:
                board.Tokens(token.N is Fixed made ? made.N : 1);
                break;

            // CR 121.2: counters go on the object they were put on and stay there.
            case PutCounters { On: Target on, N: Fixed count } counters when Creature(on) && count.N > 0:
                board.Expect(new CountersAre(board.Body(Mine(on)), counters.Counter, count.N));
                break;

            // CR 701.19: searching a library for a card and putting it in hand takes it out of the library.
            case Search { To: Zone.Hand, From: Zone.Library, Who: null or You } search when Findable(search.For):
                board.Caster(hand: 1, library: -1);
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
            case Tap { What: Target tapped } when Creature(tapped):
                board.Expect(new TappedIs(board.Body(Mine(tapped)), true));
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

    /// <summary>A target the board can place: a creature described by nothing but its type and its controller.</summary>
    private static bool Creature(Target target) =>
        target is { Count: null or Fixed { N: 1 }, UpTo: false } && Simple(target.Filter) && target.Filter.Types == CardType.Creature;

    /// <summary>A target the board can put on the stack: a spell described by nothing but being one.</summary>
    private static bool Spell(Target target) =>
        target is { Count: null or Fixed { N: 1 }, UpTo: false } && Simple(target.Filter)
        && target.Filter.Types is null or CardType.Instant or (CardType.Instant | CardType.Sorcery);

    /// <summary>The filler library is a land, so a search finds something only when a land will do.</summary>
    private static bool Findable(ObjectFilter filter) => Simple(filter) && filter.Types is null or CardType.Land;

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
