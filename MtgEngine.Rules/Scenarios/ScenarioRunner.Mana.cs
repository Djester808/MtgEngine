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
    /// Which of a permanent's abilities a <see cref="TapForManaStep"/> activates: the first mana
    /// ability it has right now, by its index in the object's current abilities — the same index an
    /// <see cref="ActivateStep"/> would have had to count out, read off the layer system so an
    /// ability granted to the permanent counts as much as one it printed (CR 613.1d). A scenario
    /// that names a permanent with no mana ability has written a step the game cannot take, and
    /// saying so with the rule is better than activating some other ability that happens to be first.
    /// </summary>
    [RuleRef("605.1a")]
    private int ManaAbilityIndex(TapForManaStep step)
    {
        var abilities = Host.Engine.Characteristics.Of(_state, Object(step.Card)).Abilities;
        for (var index = 0; index < abilities.Count; index++)
            if (abilities[index] is ActivatedAbility ability && IsManaAbility(ability))
                return index;

        throw new ScenarioFailure($"'{step.Card}' has no mana ability to tap for mana. (605.1a)");
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
