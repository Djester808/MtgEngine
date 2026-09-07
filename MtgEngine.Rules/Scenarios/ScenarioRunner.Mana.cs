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
}
