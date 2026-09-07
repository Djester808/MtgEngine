namespace MtgEngine.Cards.Pipeline.Scenarios;

/// <summary>
/// A generated scenario per activated ability: the permanent is already on the battlefield, the
/// ability is activated with the mana and the objects its cost needs (CR 602.2), and what it does
/// is asserted the same way a spell's is.
/// </summary>
/// <remarks>
/// The permanent is placed rather than cast, because CR 602.2 activates an ability from where the
/// object already is and casting it first would make every activation scenario a test of the cast
/// as well. An ability whose cost the board cannot pay, or whose effect nothing on the board can
/// show, gets no scenario at all — the same rule the spell generator works under.
/// </remarks>
public static class SmokeActivation
{
    /// <summary>One scenario per activated ability this card can be made to use.</summary>
    public static IReadOnlyList<Scenario> Of(CardFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var face = file.Definition.Faces.FirstOrDefault();
        if (face is null)
            return [];

        var scenarios = new List<Scenario>();
        var abilities = face.AbilityList;
        for (var index = 0; index < abilities.Count; index++)
        {
            if (abilities[index] is ActivatedAbility ability && One(file, ability, index) is { } scenario)
                scenarios.Add(scenario);
        }

        return scenarios;
    }

    /// <summary>The id a card's activation scenario carries, so a stale one can be told from a fresh one.</summary>
    public static string IdFor(string oracleId, int index) =>
        FormattableString.Invariant($"smoke-activate-{oracleId}-{index}");

    private static Scenario? One(CardFile file, ActivatedAbility ability, int index)
    {
        // CR 602.5: an ability with a condition on it may simply not be activatable on the board a
        // generated scenario can build, and a refusal reads as the card failing.
        if (ability.Where != Zone.Battlefield || ability.OnlyIf is not null || ability.PayOnlyWith is not null)
            return null;

        // CR 608.2d: "add one mana of any colour" asks the activator which colour, and the engine
        // refuses an activation that did not bring the answer with it. The step carries the answer
        // (seam-requests/P12.md § 1), so the scenario chooses white and asserts the pool that
        // leaves. A production the step cannot answer for — a chosen colour, the commander's
        // identity, what a land could produce — still gets no scenario rather than a refusal.
        var additions = Parts(ability.Do).OfType<AddMana>().ToList();
        if (additions.Any(a => a.Mana.Fixed is not { Length: > 0 } && !Choosable(a)))
            return null;
        var colors = Colors(additions);

        var board = new SmokeBoard(file.Name) { PlaysFromBattlefield = true };
        if (Pay(board, ability.Costs) is not { } mana)
            return null;
        if (!SmokeScenario.Reads(board, ability.Do) || board.Empty || board.Targets > 1)
            return null;

        // CR 106.4: the pool the activation leaves behind is only the ability's own mana when the
        // pool it was paid from held exactly the cost, so an ability whose cost cannot be stocked
        // to the symbol keeps its colour unasserted rather than asserting the deep pool.
        if (colors is { Length: > 0 })
        {
            if (!board.PaysActivationExactly(mana))
                return null;
            board.Expect(new ManaPoolIs(SmokeScenario.Caster, colors));
        }

        var taps = ability.Costs.Any(c => c is TapSelf);
        if (taps)
            board.Expect(new TappedIs(file.Name, true));

        var step = new ActivateStep(
            SmokeScenario.Caster,
            file.Name,
            index,
            board.TargetNames(),
            mana.Contains("{X}", StringComparison.Ordinal) ? SmokeScenario.AnnouncedX : null,
            CostObjects: board.CostObjects(),
            ManaColors: colors);
        return board.Build(
            IdFor(file.OracleId, index),
            $"activating an ability of {file.Name} does what its definition says",
            step,
            spellGoesToTheGraveyard: false);
    }

    /// <summary>
    /// CR 601.2h: the costs the board can actually pay. Mana comes from the deep pool, tapping the
    /// source costs nothing to arrange, life is paid out of twenty, and a cost that eats a permanent
    /// gets a body of its own placed for it. Anything else — a counter to remove, a card to exile
    /// from somewhere, a choice between costs — is a cost the board cannot honour, and an ability
    /// whose cost cannot be paid gets no scenario rather than one that reports the runner's gap.
    /// </summary>
    /// <returns>The printed mana part, or null when a cost cannot be paid.</returns>
    [RuleRef("601.2h")]
    private static string? Pay(SmokeBoard board, IReadOnlyList<Cost> costs)
    {
        var mana = string.Empty;
        foreach (var cost in costs ?? [])
        {
            switch (cost)
            {
                case ManaCost printed:
                    mana += printed.Mana;
                    break;

                case TapSelf:
                    break;

                // CR 118.4: a player may pay life down to nothing, and the scenario starts at 20.
                case PayLife { Amount: Fixed life } when life.N is > 0 and < ScenarioRunnerLife:
                    board.Expect(new Life(SmokeScenario.Caster, ScenarioRunnerLife - life.N));
                    break;

                // CR 701.10: the cost sacrifices one creature of the activator's, so one is placed.
                case SacrificeCost { Count: null or Fixed { N: 1 } } sacrifice
                    when sacrifice.What.ControlledBy is null or You && SmokeFillers.Bodies(sacrifice.What) is [var body, ..]:
                    board.Sacrificed(body);
                    break;

                default:
                    return null;
            }
        }

        return mana;
    }

    /// <summary>CR 103.3: the life every scenario's players start on.</summary>
    [RuleRef("103.3")]
    private const int ScenarioRunnerLife = 20;

    /// <summary>
    /// CR 608.2d: a mana production the activation step can answer for — one mana, of any colour,
    /// for the activator. Every other undecided production (a chosen colour, the commander's
    /// identity, whatever a land could produce, one of a named pair) is a choice the step has no
    /// field for, and the ability gets no scenario at all.
    /// </summary>
    [RuleRef("608.2d")]
    private static bool Choosable(AddMana add) =>
        add is
        {
            Who: null or You,
            N: null,
            Mana:
            {
                Fixed: null,
                AnyColor: true,
                OneOf: null,
                CommanderIdentity: false,
                ChosenColor: false,
                ChosenColorKey: null,
                ColorOfAttachedOrTarget: false,
                LandCouldProduce: false,
                AnyType: false,
                Restriction: false,
                RestrictedTo: null,
            },
        };

    /// <summary>
    /// The colours the step announces: one letter per "of any colour" the ability adds, in the
    /// order the effect adds them (seam-requests/P12.md § 1). White every time — the choice is
    /// the scenario's to make and one colour is as good as another — and null when the ability
    /// prints every colour it produces, which is what every activation scenario meant before.
    /// </summary>
    [RuleRef("608.2d")]
    private static string? Colors(IReadOnlyList<AddMana> additions)
    {
        var chosen = string.Concat(additions.Where(Choosable).Select(_ => "W"));
        return chosen.Length == 0 ? null : chosen;
    }

    /// <summary>A sequence is its parts; an activation asserts only what always happens.</summary>
    private static IEnumerable<Effect> Parts(Effect effect) =>
        effect is Sequence sequence ? sequence.Steps.SelectMany(Parts) : [effect];
}
