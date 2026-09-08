using MtgEngine.Rules.Scenarios;

namespace MtgEngine.Cards.Pipeline.Scenarios;

/// <summary>
/// The board a smoke scenario is assembled on: who has what, what is done, and what must hold
/// afterwards. A generator says "I need a creature of the opponent's to point at" or "the hand
/// grows by two" and the board works out the counts, because a count only means something
/// relative to what the setup put there.
/// </summary>
internal sealed partial class SmokeBoard(string card)
{
    private const int LibrarySize = SmokeScenario.LibrarySize;
    private const int HandSize = SmokeScenario.HandSize;

    private readonly List<CardRef> _casterBattlefield = [];
    private readonly List<CardRef> _opponentBattlefield = [];
    private readonly List<ScenarioStep> _before = [];
    private readonly List<string> _targets = [];
    private readonly List<Expectation> _expect = [];
    private readonly List<CardRef> _library = [];
    private readonly List<CardRef> _casterGraveyardCards = [];

    private readonly List<int> _modes = [];
    private readonly List<string> _costObjects = [];

    /// <summary>The alias of the one hand card a printed discard cost eats, so the step can name it.</summary>
    private const string HandCost = "cost-hand";

    /// <summary>How many bodies the printed cost ate: placed for the cast, and gone by the time anything is counted.</summary>
    private int _costSacrifices;

    private bool _discards;
    private string? _pool;
    private string? _printed;
    private int _casterArrivals;
    private int _casterHand;
    private int _casterLibrary;
    private int _casterGraveyard;
    private int _opponentHand;
    private int _opponentLibrary;
    private int _opponentGraveyard;
    private bool _opponentActs;
    private int _casterLife;
    private int _opponentLife;

    /// <summary>Objects an earlier step remembered under a key, as the alias the setup gave each and the filler it is (CR 608.2h).</summary>
    private readonly Dictionary<string, List<(string Alias, string Card)>> _remembered = new(StringComparer.Ordinal);

    /// <summary>A reveal of the top of the library that has yet to be stocked: the key it remembers under, and how many cards.</summary>
    private readonly Dictionary<string, int> _reveals = new(StringComparer.Ordinal);

    /// <summary>
    /// Nothing was asserted, so there is no scenario: a check that always passes is worse than
    /// none. A zone that changed counts, even though its expectation is only written at the end.
    /// </summary>
    /// <remarks>
    /// A hand the board was told holds (<see cref="HandHolds"/>) is something to say even when its
    /// deltas cancel: "draw two, then put two cards back on top of your library" moves four cards
    /// and leaves every count where it started, and a scenario that said nothing would pass on a
    /// play that did none of it.
    /// </remarks>
    internal bool Empty => _expect.Count == 0 && !Moved && !_casterHandHolds && SpellEndsIn == Zone.Graveyard;

    private bool Moved =>
        (_casterArrivals | _casterHand | _casterLibrary | _casterGraveyard | _opponentHand | _opponentLibrary | _opponentGraveyard | _casterLife | _opponentLife) != 0;

    /// <summary>The card under test, so a generator can name it in an expectation of its own.</summary>
    internal string Card => card;

    /// <summary>
    /// CR 608.2m: where the spell itself ends up once it has resolved — its owner's graveyard,
    /// unless the card says otherwise ("exile this card", "shuffle this card into its owner's
    /// library"), in which case the last thing the scenario asserts is that other zone.
    /// </summary>
    [RuleRef("608.2m")]
    internal Zone SpellEndsIn { get; set; } = Zone.Graveyard;

    /// <summary>
    /// CR 119.3 and CR 119.4: life gained and lost adds up, so a card that does both to one player
    /// asserts the total rather than two totals that cannot both hold.
    /// </summary>
    [RuleRef("119.4")]
    internal void Lives(bool mine, int delta)
    {
        if (mine)
            _casterLife += delta;
        else
            _opponentLife += delta;
    }

    private bool _casterLifeHolds;
    private bool _opponentLifeHolds;

    /// <summary>
    /// CR 119.1: this player's life total is asserted even when the card under test never moves it,
    /// because <em>not</em> moving is what the play is about — a countered spell is a spell whose
    /// damage never happened, and a scenario that only names the graveyard the countered spell went
    /// to would pass over a counter that did nothing at all (CR 608.2m puts a spell that resolved
    /// in the same graveyard). The delta is still whatever every other generator added, so a card
    /// that counters and drains asserts one total rather than two that cannot both hold.
    /// </summary>
    [RuleRef("119.1")]
    internal void LifeHolds(bool mine)
    {
        if (mine)
            _casterLifeHolds = true;
        else
            _opponentLifeHolds = true;
    }

    /// <summary>
    /// CR 701.9b: this play discards at random, so no card the setup stocked can be named
    /// afterwards — the discard may take the very card a search had just put in the hand, and an
    /// expectation that names it is a coin toss rather than a check. The counts still hold: one
    /// card left the hand and one reached the graveyard whichever card it was.
    /// </summary>
    [RuleRef("701.9b")]
    internal bool DiscardsAtRandom { get; set; }

    private bool _casterHandHolds;

    /// <summary>
    /// CR 701.9b with CR 121.1: the caster's hand is counted even when the deltas cancel. A card
    /// searched into the hand and a card discarded out of it net to nothing, and the size the hand
    /// is left at is the one thing about it that holds however the random discard fell — the same
    /// reason <see cref="LifeHolds"/> exists for a life total the card did not move.
    /// </summary>
    [RuleRef("701.9b")]
    internal void HandHolds() => _casterHandHolds = true;

    /// <summary>Whether the caster's hand is counted whatever its delta came to.</summary>
    internal bool CasterHandHolds => _casterHandHolds;

    /// <summary>CR 608.2h: an object a step remembered under a key, so a later step can read it back.</summary>
    [RuleRef("608.2h")]
    internal void Remember(string key, string alias, string card)
    {
        if (!_remembered.TryGetValue(key, out var list))
            _remembered[key] = list = [];
        list.Add((alias, card));
    }

    internal IReadOnlyList<(string Alias, string Card)> Recalled(string key) =>
        _remembered.TryGetValue(key, out var list) ? list : [];

    /// <summary>A reveal of the top cards remembered under a key, waiting for the step that says what they must be.</summary>
    internal void Reveals(string key, int n) => _reveals[key] = n;

    /// <summary>How many cards a pending reveal is for, taken once: the step that stocks them settles it.</summary>
    internal int? Revealed(string key) => _reveals.Remove(key, out var n) ? n : null;

    /// <summary>How many targets the steps supply. More than <see cref="TargetLimit"/> is a smoke scenario's limit.</summary>
    internal int Targets => _targets.Count;

    /// <summary>
    /// CR 601.2c: how many targets one play may name. One, unless a single record names two of its
    /// own — a fight's pair — which the step carries in the order the record does; two separate
    /// targeted effects stay over the limit, because which of them the engine's slots answer to
    /// is not a generated scenario's to guess.
    /// </summary>
    [RuleRef("601.2c")]
    internal int TargetLimit { get; set; } = 1;

    internal void Expect(Expectation expectation) => _expect.Add(expectation);

    /// <summary>
    /// CR 601.2c with CR 608.2h: the key the one player this play announces was announced under,
    /// so an amount that reads "that player's library" or "their life" knows it is about the
    /// opponent the step aims at. Null when the card names no player target, or names more than one.
    /// </summary>
    [RuleRef("608.2h")]
    internal string? AimedPlayerKey { get; set; }

    /// <summary>The card the spell is aimed at, placed on the battlefield of whoever is to own it.</summary>
    internal string Body(bool mine) => Aimed(mine, ScenarioFiller.Creature);

    /// <summary>
    /// CR 601.2c: a permanent of the named kind, placed and then named as a target. An Aura's
    /// "enchant" restriction picks the kind (CR 702.5b); everything else aims at a creature.
    /// </summary>
    [RuleRef("702.5b")]
    internal string Aimed(bool mine, string card)
    {
        // Under an alias, as a stocked library card is: the runner names objects first-wins and
        // places the hand before the battlefield, so a filler that also sits in the hand would
        // otherwise be what the target name resolves to (seam-requests/E5g.md).
        var alias = NextAim();
        (mine ? _casterBattlefield : _opponentBattlefield).Add(new CardRef(card, Alias: alias));
        _targets.Add(alias);
        return alias;
    }

    /// <summary>
    /// CR 404.1: a card in the caster's graveyard for a spell to be aimed at, under an alias like a
    /// battlefield target's — "return target card from your graveyard" has to name one particular
    /// card, and the graveyard is otherwise empty until the spell itself arrives there.
    /// </summary>
    [RuleRef("404.1")]
    internal string Buries(string card)
    {
        var alias = NextAim();
        _casterGraveyardCards.Add(new CardRef(card, Alias: alias));
        _targets.Add(alias);
        return alias;
    }

    private string NextAim() => FormattableString.Invariant($"aim-{_targets.Count + 1}");

    /// <summary>How many permanents have been stood on a battlefield for the card's own choice to take.</summary>
    private int _bystanders;

    /// <summary>
    /// CR 603.3: a permanent stood on a battlefield for the card's own resolution to choose, under
    /// an alias so the scenario can say where it ended up. It is not a target — nothing announces
    /// it — and it is placed by the setup, so its object is older than the card under test once
    /// that card has been played (CR 400.7 makes the played card a new object). A generated
    /// scenario answers a choice with the first legal option and the options are in object order,
    /// so the choice takes this one: a land whose enters-trigger returns "a land you control"
    /// would otherwise be the only land its controller had and would return itself.
    /// </summary>
    [RuleRef("603.3")]
    internal string Bystander(bool mine, string card)
    {
        var alias = FormattableString.Invariant($"stood-{++_bystanders}");
        (mine ? _casterBattlefield : _opponentBattlefield).Add(new CardRef(card, Alias: alias));
        return alias;
    }

    /// <summary>
    /// CR 601.2c with CR 109.2: every body the target's filter admits is placed, and the first is
    /// the one named as the target. "Destroy target artifact or enchantment" puts one of each on
    /// the board, so the scenario shows the effect reaching the kind it was pointed at while the
    /// other kind stands there as the control.
    /// </summary>
    [RuleRef("109.2")]
    internal string AimedAmong(bool mine, IReadOnlyList<string> cards)
    {
        ArgumentNullException.ThrowIfNull(cards);
        foreach (var extra in cards.Skip(1))
            Place(mine, extra, 1);
        return Aimed(mine, cards[0]);
    }

    /// <summary>
    /// CR 701.19: a card the library must be holding for a search to find it, put on top under an
    /// alias of its own. On top because the runner answers a question with its first legal option
    /// (<c>ScenarioAnswers</c>), so the card the scenario stocked is the card the search finds, and
    /// the expectation can name it rather than settle for a count that a wrong card also satisfies.
    /// </summary>
    [RuleRef("701.19")]
    internal string Stocks(string card)
    {
        var alias = FormattableString.Invariant($"find-{_library.Count + 1}");
        _library.Add(new CardRef(card, Alias: alias));
        return alias;
    }

    /// <summary>CR 601.2c: a spell that targets a player is aimed at the opponent, once, whatever else it targets; the runner reads a player's name as a player target.</summary>
    [RuleRef("601.2c")]
    internal void AimsAtPlayer(string player)
    {
        if (!_targets.Contains(player))
            _targets.Add(player);
    }

    /// <summary>Whether the library still has room for another stocked card, so its size stays the size a count assumes.</summary>
    internal bool CanStock => _library.Count < LibrarySize;

    /// <summary>What an Aura was cast at, so the scenario asserts the attachment and not the zone (CR 303.4f).</summary>
    internal string? Enchanted { get; private set; }

    /// <summary>
    /// CR 702.5b: the object an Aura's "enchant" restriction describes, placed and aimed at, and
    /// remembered so the scenario can say what it must end up attached to.
    /// </summary>
    [RuleRef("303.4f")]
    internal string Enchants(bool mine, string card) => Enchanted = Aimed(mine, card);

    /// <summary>What the spell is printed to cost, read before a mana effect asks to be paid exactly.</summary>
    internal void Printed(string? manaCost) => _printed = manaCost;

    /// <summary>
    /// CR 601.2b: the value the cast announces for X, or null when the cost has no X. A filter
    /// bounded by X ("with mana value X or less") is read against this, so the library is stocked
    /// with a card on the right side of the bound the cast is going to name.
    /// </summary>
    [RuleRef("601.2b")]
    internal int? AnnouncedX { get; set; }

    /// <summary>
    /// CR 601.2f: stock the caster's pool with exactly what the spell costs instead of the deep
    /// pool, so the cast empties it and everything left afterwards is what the card itself
    /// produced. Only a cost of plain symbols can be stocked this way — a hybrid or a Phyrexian
    /// symbol is a choice, and which half of it the engine spends is not a generated scenario's to
    /// predict; a card whose cost it cannot read keeps the deep pool and asserts less.
    /// </summary>
    [RuleRef("601.2f")]
    internal bool PaysExactly()
    {
        if (Plain(_printed) is not { } exact)
            return false;
        _pool = exact;
        return true;
    }

    /// <summary>
    /// CR 602.2a: an activation is paid out of the pool the setup stocks, and the cost is the
    /// ability's rather than the card's — an ability that costs no mana is paid from an empty pool.
    /// Stocking exactly what the activation pays leaves the pool holding precisely what the ability
    /// produced, which is what <see cref="PaysExactly"/> does for a cast.
    /// </summary>
    /// <returns>False when a symbol of the cost is a choice rather than a symbol, so nothing is stocked.</returns>
    [RuleRef("602.2a")]
    internal bool PaysActivationExactly(string cost)
    {
        if (cost is not { Length: > 0 })
        {
            _pool = string.Empty;
            return true;
        }

        if (Plain(cost) is not { } exact)
            return false;
        _pool = exact;
        return true;
    }

    /// <summary>
    /// CR 601.2c: a permanent the printed cost sacrifices, placed on the caster's battlefield under
    /// an alias of its own — the step has to name one particular body, and a filler creature the
    /// board placed for some other generator may belong to the opponent.
    /// </summary>
    [RuleRef("601.2c")]
    internal void Sacrificed(string card)
    {
        var alias = FormattableString.Invariant($"cost-{_costObjects.Count + 1}");
        _casterBattlefield.Add(new CardRef(card, Alias: alias));
        _costObjects.Add(alias);
        _costSacrifices++;
        Caster(graveyard: 1);
    }

    /// <summary>CR 701.8: a printed discard cost eats one card from the caster's hand, which the setup aliases for it.</summary>
    [RuleRef("701.8")]
    internal void Discarded()
    {
        _discards = true;
        _costObjects.Add(HandCost);
        Caster(hand: -1, graveyard: 1);
    }

    /// <summary>The objects the cast step names as the body of its printed costs (CR 601.2c).</summary>
    internal IReadOnlyList<string>? CostObjects() => _costObjects.Count == 0 ? null : _costObjects;

    /// <summary>Permanents that nothing points at: bodies for a wipe to destroy, or for a count to count.</summary>
    internal void Place(bool mine, string card, int n)
    {
        var where = mine ? _casterBattlefield : _opponentBattlefield;
        for (var i = 0; i < n; i++)
            where.Add(new CardRef(card));
    }

    /// <summary>How many permanents each player has, so a wipe knows what it is asserting the absence of.</summary>
    internal int OnBattlefield(bool mine) => mine ? _casterBattlefield.Count : _opponentBattlefield.Count;

    /// <summary>CR 700.2: the modes are chosen as the spell is cast, so the step carries them.</summary>
    [RuleRef("700.2")]
    internal void Mode(int index) => _modes.Add(index);

    internal IReadOnlyList<int>? Modes() => _modes.Count == 0 ? null : _modes;

    /// <summary>
    /// CR 400.1: one more permanent on the caster's battlefield than the setup put there —
    /// a token made, or a card a search put straight onto it.
    /// </summary>
    [RuleRef("400.1")]
    internal void Arrives(int n) => _casterArrivals += n;

    /// <summary>
    /// CR 601: a spell on the stack for a counterspell to be aimed at, cast by the other player.
    /// </summary>
    /// <param name="withNothingLeftToPay">
    /// CR 118.6: the opponent's pool is stocked with exactly what the filler spell costs, so it is
    /// empty by the time an "unless its controller pays" asks — the payment cannot be made and the
    /// scenario can say what happens when it is not. Without it the opponent keeps the deep pool
    /// and would simply pay, which is a different half of the card.
    /// </param>
    /// <param name="card">
    /// Which filler spell it is (CR 601.2a): the one that does nothing, unless the card under test
    /// needs the resolution to show something — a retarget reads the life totals, so it wants the
    /// bolt.
    /// </param>
    /// <param name="aimedAt">
    /// CR 601.2c: what the filler spell itself is announced at, named on its own cast step. Null for
    /// a filler that targets nothing.
    /// </param>
    [RuleRef("601")]
    internal string SpellOnTheStack(bool withNothingLeftToPay = false, string? card = null, IReadOnlyList<string>? aimedAt = null)
    {
        _opponentActs = true;
        _opponentPaysNothing = withNothingLeftToPay;
        _opponentCasts = card ?? ScenarioFiller.Spell;
        _before.Add(new CastStep(SmokeScenario.Opponent, _opponentCasts, aimedAt));
        _targets.Add(_opponentCasts);
        return _opponentCasts;
    }

    /// <summary>Which filler spell the opponent casts, so their hand holds it and their pool can be sized for it.</summary>
    private string _opponentCasts = ScenarioFiller.Spell;

    /// <summary>Whether the opponent's pool holds only the filler spell's own cost (CR 118.6).</summary>
    private bool _opponentPaysNothing;

    /// <summary>
    /// CR 602.2a: the pool the opponent casts the filler spell out of — the deep pool, or exactly
    /// the filler's printed cost when the scenario needs them unable to pay for anything else.
    /// </summary>
    [RuleRef("602.2a")]
    private string? OpponentPool() => (_opponentActs, _opponentPaysNothing) switch
    {
        (false, _) => null,
        (true, false) => SmokeScenario.DeepPool,
        (true, true) => Plain(ScenarioFiller.For(_opponentCasts)?.Faces[0].ManaCost) ?? SmokeScenario.DeepPool,
    };

    /// <summary>
    /// CR 110.6 with CR 400.7: the zone the source moved itself to, when its own effect took it off
    /// the battlefield. A card in a library, a hand, a graveyard or exile is neither tapped nor
    /// untapped, so nothing asserts a tap of it — the zone is the whole of what the play shows.
    /// </summary>
    [RuleRef("110.6")]
    internal Zone? MovedItselfTo { get; private set; }

    /// <summary>The source leaves the battlefield for the zone its own effect named, and the counts follow it.</summary>
    [RuleRef("400.7")]
    internal void MovesItselfTo(Zone destination)
    {
        MovedItselfTo = destination;
        Expect(new InZone(card, destination, SmokeScenario.Caster));

        // One fewer permanent than the setup put there, which is what says it actually left.
        _casterArrivals--;
        switch (destination)
        {
            case Zone.Library:
                Caster(library: 1);
                break;
            case Zone.Hand:
                Caster(hand: 1);
                break;
            case Zone.Graveyard:
                Caster(graveyard: 1);
                break;
            default:
                break;
        }
    }

    /// <summary>CR 111.1: tokens arrive on the caster's battlefield, beside whatever the setup put there.</summary>
    [RuleRef("111.1")]
    internal void Tokens(int n) => Arrives(n);

    /// <summary>Cards move between the caster's zones: the counts are asserted once, at the end.</summary>
    internal void Caster(int hand = 0, int library = 0, int graveyard = 0)
    {
        _casterHand += hand;
        _casterLibrary += library;
        _casterGraveyard += graveyard;
    }

    internal void Opponent(int hand = 0, int library = 0, int graveyard = 0)
    {
        _opponentHand += hand;
        _opponentLibrary += library;
        _opponentGraveyard += graveyard;
    }
}
