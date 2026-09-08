using MtgEngine.Rules.Scenarios;

namespace MtgEngine.Cards.Pipeline.Scenarios;

/// <summary>The board's second half: turning what the generators asked for into the scenario and its counts.</summary>
internal sealed partial class SmokeBoard
{
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
                        Hand: _opponentActs ? [new CardRef(_opponentCasts), .. Filler(HandSize)] : Filler(HandSize),
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
        if (_casterHand != 0 || CasterHandHolds)
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
        if (_casterLife != 0 || _casterLifeHolds)
            _expect.Add(new Life(SmokeScenario.Caster, 20 + _casterLife));
        if (_opponentLife != 0 || _opponentLifeHolds)
            _expect.Add(new Life(SmokeScenario.Opponent, 20 + _opponentLife));
    }

    private static List<CardRef> Filler(int n) =>
        [.. Enumerable.Range(0, n).Select(_ => new CardRef(ScenarioFiller.Land))];
}
