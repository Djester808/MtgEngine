using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Cards;
using Xunit.Abstractions;

namespace MtgEngine.Api.Tests;

public sealed class ZzR2140Probe(ITestOutputHelper output)
{
    private static CardDefinition Sorcery(string text) => new()
    {
        OracleId = "probe-" + text.GetHashCode(StringComparison.Ordinal).ToString("x", System.Globalization.CultureInfo.InvariantCulture),
        Name = "Probe Card",
        Cmc = 2,
        CardTypes = CardType.Sorcery,
        OracleText = text,
    };

    [Fact]
    public void What_reads()
    {
        string[] probes =
        [
            "Destroy target creature with power less than or equal to the number of Warriors you control.",
            "Gain control of target creature with power less than or equal to the number of treasure counters on ~.",
            "Target creature an opponent controls with power less than or equal to the number of Warriors you control can't block this turn.",
            "Target creature with power less than or equal to the number of Warriors you control can't block this turn.",
            "Target creature with power 3 or less can't block this turn.",
            "Return to their owners' hands all creatures with toughness less than or equal to the number of Islands you control.",
            "Return to their owners' hands all creatures with toughness 2 or less.",
            "Destroy each creature with power less than or equal to the number of Islands you control.",
            "Destroy each creature with power 2 or less.",
            "Exile all creatures with mana value less than or equal to the number of Islands you control.",
            "Exile all creatures with mana value 2 or less.",
            "Search your library for a card with mana value less than or equal to the number of lands you control, reveal it, put it into your hand, then shuffle.",
            "Return target creature card with mana value less than or equal to the number of lands you control from your graveyard to the battlefield.",
            "Return target creature card with mana value 3 or less from your graveyard to the battlefield.",
            "You may cast a spell with mana value less than or equal to the number of lands you control from your hand without paying its mana cost.",
            "Counter target spell with mana value less than or equal to the number of lands you control.",
            "Destroy target creature with power greater than or equal to the number of Warriors you control.",
        ];

        foreach (var text in probes)
        {
            var compiled = CardCompiler.Compile(Sorcery(text));
            output.WriteLine(
                (compiled.IsComplete ? "READ    " : "UNREAD  ") + text);
        }
    }
}
