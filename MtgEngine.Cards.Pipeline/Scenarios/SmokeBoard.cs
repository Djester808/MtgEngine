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

    private int _casterTokens;
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
        (_casterTokens | _casterHand | _casterLibrary | _casterGraveyard | _opponentHand | _opponentLibrary | _opponentGraveyard) != 0;

    /// <summary>How many targets the steps supply. A second one is a smoke scenario's limit.</summary>
    internal int Targets => _targets.Count;

    internal void Expect(Expectation expectation) => _expect.Add(expectation);

    /// <summary>The card the spell is aimed at, placed on the battlefield of whoever is to own it.</summary>
    internal string Body(bool mine)
    {
        var where = mine ? _casterBattlefield : _opponentBattlefield;
        where.Add(new CardRef(ScenarioFiller.Creature));
        _targets.Add(ScenarioFiller.Creature);
        return ScenarioFiller.Creature;
    }

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
    internal void Tokens(int n) => _casterTokens += n;

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
                        Battlefield: _casterBattlefield,
                        Hand: [new CardRef(card), .. Filler(HandSize)],
                        Library: Filler(LibrarySize),
                        ManaPool: SmokeScenario.DeepPool),
                    new PlayerSetup(
                        SmokeScenario.Opponent,
                        Battlefield: _opponentBattlefield,
                        Hand: _opponentActs ? [new CardRef(ScenarioFiller.Spell), .. Filler(HandSize)] : Filler(HandSize),
                        Library: Filler(LibrarySize),
                        ManaPool: _opponentActs ? SmokeScenario.DeepPool : null),
                ],
                SmokeScenario.Caster),
            [.. _before, action, new ResolveAllStep()],
            _expect);
    }

    /// <summary>The step the card is played with, carrying whatever targets the generators asked for.</summary>
    internal IReadOnlyList<string>? TargetNames() => _targets.Count == 0 ? null : _targets;

    private void Counts()
    {
        if (_casterTokens != 0)
            _expect.Add(new ZoneCount(SmokeScenario.Caster, Zone.Battlefield, _casterBattlefield.Count + _casterTokens));

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
