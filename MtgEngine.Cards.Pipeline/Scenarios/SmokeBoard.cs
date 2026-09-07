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

    /// <summary>
    /// Nothing was asserted, so there is no scenario: a check that always passes is worse than
    /// none. A zone that changed counts, even though its expectation is only written at the end.
    /// </summary>
    internal bool Empty => _expect.Count == 0 && !Moved;

    private bool Moved =>
        (_casterArrivals | _casterHand | _casterLibrary | _casterGraveyard | _opponentHand | _opponentLibrary | _opponentGraveyard) != 0;

    /// <summary>How many targets the steps supply. A second one is a smoke scenario's limit.</summary>
    internal int Targets => _targets.Count;

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
        var alias = FormattableString.Invariant($"aim-{_targets.Count + 1}");
        (mine ? _casterBattlefield : _opponentBattlefield).Add(new CardRef(card, Alias: alias));
        _targets.Add(alias);
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

    /// <summary>CR 601: a spell on the stack for a counterspell to be aimed at, cast by the other player.</summary>
    [RuleRef("601")]
    internal string SpellOnTheStack()
    {
        _opponentActs = true;
        _before.Add(new CastStep(SmokeScenario.Opponent, ScenarioFiller.Spell));
        _targets.Add(ScenarioFiller.Spell);
        return ScenarioFiller.Spell;
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
            // CR 608.2m: the spell itself is the last thing to move, into its owner's graveyard.
            _expect.Add(new InZone(card, Zone.Graveyard));
            _casterGraveyard++;
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
                        ManaPool: _pool ?? SmokeScenario.DeepPool),
                    new PlayerSetup(
                        SmokeScenario.Opponent,
                        Battlefield: _opponentBattlefield,
                        Hand: _opponentActs ? [new CardRef(ScenarioFiller.Spell), .. Filler(HandSize)] : Filler(HandSize),
                        Library: Filler(LibrarySize),
                        ManaPool: _opponentActs ? SmokeScenario.DeepPool : null),
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
        if (_casterGraveyard != 0)
            _expect.Add(new ZoneCount(SmokeScenario.Caster, Zone.Graveyard, _casterGraveyard));
        if (_opponentHand != 0)
            _expect.Add(new ZoneCount(SmokeScenario.Opponent, Zone.Hand, HandSize + _opponentHand));
        if (_opponentLibrary != 0)
            _expect.Add(new ZoneCount(SmokeScenario.Opponent, Zone.Library, LibrarySize + _opponentLibrary));
        if (_opponentGraveyard != 0)
            _expect.Add(new ZoneCount(SmokeScenario.Opponent, Zone.Graveyard, _opponentGraveyard));
    }

    private static List<CardRef> Filler(int n) =>
        [.. Enumerable.Range(0, n).Select(_ => new CardRef(ScenarioFiller.Land))];
}
