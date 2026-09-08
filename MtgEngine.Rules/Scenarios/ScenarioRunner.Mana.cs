using MtgEngine.Cards;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Scenarios;

/// <summary>
/// The mana a scenario writes down: the pool string a setup stocks a player with, and the same
/// spelling an expectation names. Split out of <c>ScenarioRunner.cs</c>, which had reached the
/// 500-line limit the card programme works under.
/// </summary>
internal sealed partial class ScenarioRun
{
    /// <summary>The mana a scenario writes as a string: one letter per mana, a number for that much colourless.</summary>
    [RuleRef("106.1a")]
    internal static ManaPool ParseMana(string mana)
    {
        int w = 0, u = 0, b = 0, r = 0, g = 0, c = 0;
        foreach (var symbol in mana ?? string.Empty)
        {
            switch (char.ToUpperInvariant(symbol))
            {
                case 'W':
                    w++;
                    break;
                case 'U':
                    u++;
                    break;
                case 'B':
                    b++;
                    break;
                case 'R':
                    r++;
                    break;
                case 'G':
                    g++;
                    break;
                case 'C':
                    c++;
                    break;
                case >= '0' and <= '9':
                    c += symbol - '0';
                    break;
                default:
                    break;
            }
        }

        return new ManaPool(w, u, b, r, g, c);
    }

    /// <summary>
    /// Which of a permanent's abilities a <see cref="TapForManaStep"/> activates: the mana ability
    /// the step names, by its index in the object's current abilities — the same index an
    /// <see cref="ActivateStep"/> would have had to count out, read off the layer system so an
    /// ability granted to the permanent counts as much as one it printed (CR 613.1d) — and so does
    /// the one CR 305.6 gives a permanent for its basic land type, which is the only mana ability a
    /// filler basic land has and the reason it is tapped for its type's colour at index 0. The step
    /// counts mana abilities alone (CR 605.1a), so a permanent with two of them is tapped for the
    /// one the scenario means and an ability that is not a mana ability never moves the number;
    /// a step that names none is the first, which is what the step meant before it could name one.
    /// A scenario that names a permanent with no mana ability, or a mana ability it does not have,
    /// has written a step the game cannot take, and saying so with the rule is better than
    /// activating some other ability that happens to be there.
    /// </summary>
    [RuleRef("605.3")]
    private int ManaAbilityIndex(TapForManaStep step)
    {
        var wanted = step.AbilityIndex ?? 0;
        if (wanted < 0)
            throw new ScenarioFailure($"'{step.Card}' names mana ability {wanted}, and mana abilities are counted from zero. (605.3)");

        var abilities = Host.Engine.Characteristics.Of(_state, Object(step.Card)).Abilities;
        var found = 0;
        for (var index = 0; index < abilities.Count; index++)
        {
            if (abilities[index] is not ActivatedAbility ability || !IsManaAbility(ability))
                continue;
            if (found++ == wanted)
                return index;
        }

        throw found == 0
            ? new ScenarioFailure($"'{step.Card}' has no mana ability to tap for mana. (605.1a)")
            : new ScenarioFailure($"'{step.Card}' has {found} mana abilities and the scenario names number {wanted}. (605.3)");
    }

    /// <summary>
    /// CR 605.1a: an activated ability is a mana ability when it could add mana as it resolves, is
    /// not a loyalty ability, and needs no target. The step announces no targets, so the third
    /// condition holds by construction and only the other two are asked here; "could add mana" is
    /// read off the effect tree the way the casting system reads it, so the ability this picks is
    /// one the casting system will also resolve without the stack (CR 605.3a).
    /// </summary>
    [RuleRef("605.1a")]
    private static bool IsManaAbility(ActivatedAbility ability) =>
        CouldAddMana(ability.Do) && !ability.Costs.Any(cost => cost is LoyaltyCost);

    /// <summary>CR 605.1a's "could add mana as it resolves", over the steps of a sequence.</summary>
    [RuleRef("605.1a")]
    private static bool CouldAddMana(Effect effect) => effect switch
    {
        AddMana => true,
        Sequence sequence => sequence.Steps.Any(CouldAddMana),
        _ => false,
    };
}
