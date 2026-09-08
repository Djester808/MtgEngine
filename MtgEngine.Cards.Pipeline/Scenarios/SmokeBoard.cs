using MtgEngine.Rules.Scenarios;

namespace MtgEngine.Cards.Pipeline.Scenarios;

/// <summary>
/// The board a smoke scenario is assembled on: who has what, what is done, and what must hold
/// afterwards. A generator says "I need a creature of the opponent's to point at" or "the hand
/// grows by two" and the board works out the counts, because a count only means something
/// relative to what the setup put there.
/// </summary>
internal sealed class SmokeBoard(string card)
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
    internal bool Empty => _expect.Count == 0 && !Moved && SpellEndsIn == Zone.Graveyard;

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
    [RuleRef("601")]
    internal string SpellOnTheStack(bool withNothingLeftToPay = false)
    {
        _opponentActs = true;
        _opponentPaysNothing = withNothingLeftToPay;
        _before.Add(new CastStep(SmokeScenario.Opponent, ScenarioFiller.Spell));
        _targets.Add(ScenarioFiller.Spell);
        return ScenarioFiller.Spell;
    }

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
        (true, true) => Plain(ScenarioFiller.For(ScenarioFiller.Spell)?.Faces[0].ManaCost) ?? SmokeScenario.DeepPool,
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

    /// <summary>The scenario, with the counts turned into expectations now that every generator has spoken.</summary>
    internal Scenario Build(string id, string title, ScenarioStep action, bool spellGoesToTheGraveyard, bool usedTheStack = true)
    {
        if (spellGoesToTheGraveyard)
        {
            // CR 608.2m: the spell itself is the last thing to move, into its owner's graveyard —
            // or wherever its own text sent it instead, which the zone counts follow.
            _expect.Add(new InZone(card, SpellEndsIn));
            if (SpellEndsIn == Zone.Graveyard)
                _casterGraveyard++;
            else if (SpellEndsIn == Zone.Library)
                _casterLibrary++;
        }

        Counts();

        // CR 305.9: playing a land does not use the stack, so what is on it afterwards is about the
        // card's triggers and not about the play. A spell's own resolution is worth asserting.
        if (usedTheStack)
            _expect.Add(new StackEmpty());

        return new Scenario(
            id,
            title,
            new GeneratedSource(SmokeScenario.Generator),
            new ScenarioSetup(
                [
                    new PlayerSetup(
                        SmokeScenario.Caster,
                        Battlefield: PlaysFromBattlefield ? [new CardRef(card), .. _casterBattlefield] : _casterBattlefield,
                        Hand: PlaysFromBattlefield ? CasterHand() : [new CardRef(card), .. CasterHand()],
                        Library: CasterLibrary(),
                        Graveyard: _casterGraveyardCards.Count == 0 ? null : _casterGraveyardCards,
                        ManaPool: _pool ?? SmokeScenario.DeepPool),
                    new PlayerSetup(
                        SmokeScenario.Opponent,
                        Battlefield: _opponentBattlefield,
                        Hand: _opponentActs ? [new CardRef(ScenarioFiller.Spell), .. Filler(HandSize)] : Filler(HandSize),
                        Library: Filler(LibrarySize),
                        ManaPool: OpponentPool()),
                ],
                SmokeScenario.Caster,
                AutoAnswer: true),
            [.. _before, action, new ResolveAllStep()],
            _expect);
    }

    /// <summary>
    /// The filler beside the card under test. The first card carries an alias only when a printed
    /// discard cost has to name one, so every other card file's scenario reads as it did.
    /// </summary>
    private List<CardRef> CasterHand() =>
        _discards ? [new CardRef(ScenarioFiller.Land, Alias: HandCost), .. Filler(HandSize - 1)] : Filler(HandSize);

    /// <summary>
    /// CR 601.2c: the card the play uses starts on the battlefield rather than in hand — an
    /// activated ability is activated from where the permanent already is (CR 602.2), not cast.
    /// </summary>
    [RuleRef("602.2")]
    internal bool PlaysFromBattlefield { get; set; }

    /// <summary>
    /// The library: what a search was told to find, on top, and filler under it. The size is
    /// <see cref="LibrarySize"/> either way, so every count written against it still reads.
    /// </summary>
    private List<CardRef> CasterLibrary() => [.. _library, .. Filler(LibrarySize - _library.Count)];

    /// <summary>
    /// A mana cost written as the runner's pool string, or null when a symbol is a choice rather
    /// than a symbol: <c>{2}{B}</c> reads as <c>"2B"</c>, <c>{B/R}</c> and <c>{X}</c> read as
    /// nothing at all.
    /// </summary>
    [RuleRef("107.4")]
    private static string? Plain(string? manaCost)
    {
        if (manaCost is not { Length: > 0 })
            return null;
        var pool = new System.Text.StringBuilder();
        foreach (var symbol in manaCost.Split('{', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!symbol.EndsWith('}'))
                return null;
            // One character, because the runner reads a pool string a symbol at a time: "{10}"
            // would stock one mana and not ten, so a cost that big keeps the deep pool.
            var body = symbol[..^1];
            if (body.Length != 1 || !(char.IsAsciiDigit(body[0]) || "WUBRGC".Contains(body[0], StringComparison.Ordinal)))
                return null;
            pool.Append(body);
        }

        return pool.Length == 0 ? null : pool.ToString();
    }

    /// <summary>The step the card is played with, carrying whatever targets the generators asked for.</summary>
    internal IReadOnlyList<string>? TargetNames() => _targets.Count == 0 ? null : _targets;

    private void Counts()
    {
        // The card itself is on the caster's battlefield when the play was an activation, so it
        // counts towards what is there afterwards (CR 602.2 leaves the source where it was). What
        // the printed cost ate does not: it was placed to be sacrificed and is in a graveyard by
        // the time the spell resolves (CR 601.2h).
        if (_casterArrivals != 0)
            _expect.Add(new ZoneCount(
                SmokeScenario.Caster,
                Zone.Battlefield,
                _casterBattlefield.Count - _costSacrifices + (PlaysFromBattlefield ? 1 : 0) + _casterArrivals));

        // The card under test left the caster's hand when it was played, and the filler stayed.
        if (_casterHand != 0)
            _expect.Add(new ZoneCount(SmokeScenario.Caster, Zone.Hand, HandSize + _casterHand));
        if (_casterLibrary != 0)
            _expect.Add(new ZoneCount(SmokeScenario.Caster, Zone.Library, LibrarySize + _casterLibrary));
        // On top of whatever the setup buried there for a spell to be aimed at (CR 404.1) — and
        // written whenever something was buried, because a card that left and a spell that
        // arrived net to nothing and are still worth counting.
        if (_casterGraveyard != 0 || _casterGraveyardCards.Count != 0)
            _expect.Add(new ZoneCount(SmokeScenario.Caster, Zone.Graveyard, _casterGraveyardCards.Count + _casterGraveyard));
        if (_opponentHand != 0)
            _expect.Add(new ZoneCount(SmokeScenario.Opponent, Zone.Hand, HandSize + _opponentHand));
        if (_opponentLibrary != 0)
            _expect.Add(new ZoneCount(SmokeScenario.Opponent, Zone.Library, LibrarySize + _opponentLibrary));
        if (_opponentGraveyard != 0)
            _expect.Add(new ZoneCount(SmokeScenario.Opponent, Zone.Graveyard, _opponentGraveyard));

        // CR 119.1: each player starts at twenty, and what the card did to that is one number.
        if (_casterLife != 0)
            _expect.Add(new Life(SmokeScenario.Caster, 20 + _casterLife));
        if (_opponentLife != 0)
            _expect.Add(new Life(SmokeScenario.Opponent, 20 + _opponentLife));
    }

    private static List<CardRef> Filler(int n) =>
        [.. Enumerable.Range(0, n).Select(_ => new CardRef(ScenarioFiller.Land))];
}
