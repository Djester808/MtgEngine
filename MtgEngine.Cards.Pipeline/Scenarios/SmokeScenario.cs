namespace MtgEngine.Cards.Pipeline.Scenarios;

/// <summary>
/// One generated scenario per card: cast it, and assert the direct consequence of its own
/// definition. A <c>Draw</c> raises the hand count, a <c>DealDamage</c> lowers a life total, a
/// permanent arrives on the battlefield.
/// </summary>
/// <remarks>
/// The smoke scenario is deliberately shallow — it is the floor under "coverage is counted in
/// verified plays", not the ceiling. It reads only the definition, so it cannot be wrong about
/// what the card is supposed to do without the definition being wrong too, and a card whose
/// effect it cannot assert gets no scenario at all rather than a scenario that always passes.
/// </remarks>
public static class SmokeScenario
{
    internal const string Caster = "A";
    internal const string Opponent = "B";

    /// <summary>Enough of every colour that a cost is never what a smoke scenario is testing.</summary>
    internal const string DeepPool = "WWWWWWWWWWUUUUUUUUUUBBBBBBBBBBRRRRRRRRRRGGGGGGGGGG999999";

    public const string Generator = "smoke";

    /// <summary>The scenario for this card, or null when its definition says nothing a cast can assert.</summary>
    public static Scenario? Of(CardFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var face = file.Definition.Faces.FirstOrDefault();
        if (face is null)
            return null;

        var expectations = Expectations(file.Name, face);
        if (expectations.Count == 0)
            return null;

        return new Scenario(
            $"smoke-{file.OracleId}",
            $"casting {file.Name} does what its definition says",
            new GeneratedSource(Generator),
            new ScenarioSetup(
                [
                    new PlayerSetup(Caster, Hand: [new CardRef(file.Name)], ManaPool: DeepPool),
                    new PlayerSetup(Opponent),
                ],
                Caster),
            [new CastStep(Caster, file.Name), new ResolveAllStep()],
            expectations);
    }

    /// <summary>CR 608.2: what the spell does when it resolves, read straight off the definition.</summary>
    [RuleRef("608.2")]
    private static IReadOnlyList<Expectation> Expectations(string name, Face face)
    {
        // CR 110.1: a permanent spell resolves onto the battlefield, and that alone is worth asserting.
        if (Permanent(face.Types.Types))
            return [new InZone(name, Zone.Battlefield), new StackEmpty()];

        var effects = face.AbilityList.OfType<SpellAbility>().Select(a => a.Do).ToList();
        var expectations = new List<Expectation>();
        foreach (var effect in effects.SelectMany(Flatten))
        {
            switch (effect)
            {
                // CR 121.1: drawing puts cards in hand, and the spell itself has left it.
                case Draw { N: Fixed n, Who: null or You } when n.N > 0:
                    expectations.Add(new ZoneCount(Caster, Zone.Hand, n.N));
                    break;

                // CR 119.3: damage to a player is life lost.
                case DealDamage { N: Fixed n, To: null, ToPlayer: not null and not You } when n.N > 0:
                    expectations.Add(new Life(Opponent, 20 - n.N));
                    break;

                case GainLife { N: Fixed n, Who: null or You } when n.N > 0:
                    expectations.Add(new Life(Caster, 20 + n.N));
                    break;

                case LoseLife { N: Fixed n, Who: not null and not You } when n.N > 0:
                    expectations.Add(new Life(Opponent, 20 - n.N));
                    break;

                default:
                    break;
            }
        }

        if (expectations.Count > 0)
        {
            // CR 608.2m: the spell itself goes to its owner's graveyard as its last step.
            expectations.Add(new InZone(name, Zone.Graveyard));
            expectations.Add(new StackEmpty());
        }

        return expectations;
    }

    /// <summary>CR 205.2a: the permanent types. A spell of any of them ends up on the battlefield.</summary>
    [RuleRef("205.2a")]
    private static bool Permanent(CardType types) =>
        (types & (CardType.Creature | CardType.Artifact | CardType.Enchantment | CardType.Planeswalker | CardType.Battle | CardType.Land)) != 0;

    /// <summary>A sequence is its parts; anything conditional is skipped, because a smoke scenario asserts only what always happens.</summary>
    private static IEnumerable<Effect> Flatten(Effect effect) =>
        effect is Sequence sequence ? sequence.Steps.SelectMany(Flatten) : [effect];
}
