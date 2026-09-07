using MtgEngine.Cards;
using MtgEngine.Rules.Mana;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Printed mana, parsed and paid: CR 107.4 for the symbols, CR 601.2h for the payment. The
/// tables here are the acceptance test for the parser — every symbol shape the vocabulary
/// admits — and for the payer, which must find a payment whenever one exists and refuse
/// naming the rule when none does, because partial payments are not allowed.
/// </summary>
public sealed class ManaTests
{
    private static ManaPool Pool(int w = 0, int u = 0, int b = 0, int r = 0, int g = 0, int c = 0) => new(w, u, b, r, g, c);

    [Theory]
    [InlineData("{2}{G}", 2, 3)]
    [InlineData("{W}{W}", 2, 2)]
    [InlineData("{G/U}", 1, 1)]
    [InlineData("{2/B}", 1, 2)]
    [InlineData("{C}", 1, 1)]
    [InlineData("{S}", 1, 1)]
    [InlineData("{0}", 1, 0)]
    [InlineData("{10}", 1, 10)]
    [InlineData(null, 0, 0)]
    [InlineData("", 0, 0)]
    public void A_printed_cost_parses_to_its_symbols_and_mana_value(string? printed, int symbols, int manaValue)
    {
        var parsed = ManaSymbols.Parse(printed);
        Assert.Equal(symbols, parsed.Count);
        Assert.Equal(manaValue, ManaSymbols.ManaValue(parsed));
    }

    [Fact]
    public void X_counts_as_the_announced_amount_in_the_mana_value()
    {
        // CR 107.3b: on the stack, X is the announced value.
        var parsed = ManaSymbols.Parse("{X}{X}{R}");
        Assert.Equal(1, ManaSymbols.ManaValue(parsed));
        Assert.Equal(5, ManaSymbols.ManaValue(parsed, x: 2));
    }

    [Fact]
    public void A_symbol_the_slice_cannot_pay_is_refused_naming_the_rule()
    {
        // CR 107.4f: Phyrexian mana may be paid with life. Until that lands, the parser says so.
        Assert.False(ManaSymbols.TryParse("{G/P}", out _, out var error));
        Assert.Contains("Phyrexian", error!, StringComparison.Ordinal);
        Assert.False(ManaSymbols.TryParse("{Q}", out _, out _));
        Assert.False(ManaSymbols.TryParse("2G", out _, out _));
    }

    [Fact]
    public void Colored_symbols_are_paid_from_their_own_color()
    {
        // CR 107.4a: coloured mana in costs can be paid only with that colour.
        var payment = ManaSymbols.Pay(Pool(g: 2, r: 1), ManaSymbols.Parse("{G}{G}"));
        Assert.True(payment.Paid);
        Assert.Equal(Pool(g: -2), payment.Delta);
    }

    [Fact]
    public void Generic_is_paid_from_anything_and_spends_colorless_first()
    {
        // CR 107.4b: generic mana can be paid with any type. Colorless goes first, which never
        // strands a coloured symbol because coloured symbols are paid before generic ones.
        var payment = ManaSymbols.Pay(Pool(g: 2, c: 1), ManaSymbols.Parse("{2}{G}"));
        Assert.True(payment.Paid);
        Assert.Equal(Pool(g: -2, c: -1), payment.Delta);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public void A_hybrid_symbol_is_paid_with_either_half(int green, int blue)
    {
        // CR 107.4e: {G/U} is one green or one blue.
        var payment = ManaSymbols.Pay(Pool(u: blue, g: green), ManaSymbols.Parse("{G/U}"));
        Assert.True(payment.Paid);
        Assert.Equal(Pool(u: -blue, g: -green), payment.Delta);
    }

    [Fact]
    public void A_monocolored_hybrid_takes_the_colored_half_when_it_can_and_two_generic_when_it_cannot()
    {
        // CR 107.4e: {2/B} is one black or two mana of any type.
        var colored = ManaSymbols.Pay(Pool(b: 1, g: 2), ManaSymbols.Parse("{2/B}"));
        Assert.Equal(Pool(b: -1), colored.Delta);

        var generic = ManaSymbols.Pay(Pool(g: 2), ManaSymbols.Parse("{2/B}"));
        Assert.Equal(Pool(g: -2), generic.Delta);

        var neither = ManaSymbols.Pay(Pool(g: 1), ManaSymbols.Parse("{2/B}"));
        Assert.Equal("601.2h", neither.Refused?.Rule);
    }

    [Fact]
    public void The_colorless_symbol_is_paid_only_with_colorless_mana()
    {
        // CR 107.4c: {C} can be paid only with colorless mana.
        Assert.True(ManaSymbols.Pay(Pool(c: 1), ManaSymbols.Parse("{C}")).Paid);
        var refused = ManaSymbols.Pay(Pool(g: 5), ManaSymbols.Parse("{C}"));
        Assert.False(refused.Paid);
        Assert.Equal("601.2h", refused.Refused?.Rule);
    }

    [Fact]
    public void Snow_is_paid_with_one_mana_of_any_type()
    {
        // CR 107.4h. Whether the source was snow is not tracked in this slice.
        Assert.True(ManaSymbols.Pay(Pool(r: 1), ManaSymbols.Parse("{S}")).Paid);
        Assert.False(ManaSymbols.Pay(ManaPool.Empty, ManaSymbols.Parse("{S}")).Paid);
    }

    [Fact]
    public void X_is_paid_from_the_announced_amount_and_refused_when_it_was_not_announced()
    {
        var paid = ManaSymbols.Pay(Pool(r: 1, c: 3), ManaSymbols.Parse("{X}{R}"), x: 3);
        Assert.True(paid.Paid);
        Assert.Equal(Pool(r: -1, c: -3), paid.Delta);

        Assert.Equal("601.2b", ManaSymbols.Pay(Pool(r: 4), ManaSymbols.Parse("{X}{R}")).Refused?.Rule);
        Assert.Equal("601.2h", ManaSymbols.Pay(Pool(r: 2), ManaSymbols.Parse("{X}{R}"), x: 3).Refused?.Rule);
    }

    [Fact]
    public void A_tight_pool_is_still_spent_in_an_order_that_pays_the_whole_cost()
    {
        // The payer must not spend the only green on the generic symbol: coloured first, CR 601.2h.
        var payment = ManaSymbols.Pay(Pool(g: 1, w: 1), ManaSymbols.Parse("{1}{G}"));
        Assert.True(payment.Paid);
        Assert.Equal(Pool(w: -1, g: -1), payment.Delta);
    }

    [Fact]
    public void An_unpayable_cost_names_601_2h_and_spends_nothing()
    {
        var payment = ManaSymbols.Pay(Pool(g: 1), ManaSymbols.Parse("{2}{G}"));
        Assert.False(payment.Paid);
        Assert.Equal("601.2h", payment.Refused?.Rule);
        Assert.Equal(ManaPool.Empty, payment.Delta);
    }

    [Fact]
    public void Production_turns_printed_mana_into_a_pool()
    {
        // CR 106.1b: there is no generic mana, so a number in a production is colorless.
        Assert.Equal(Pool(g: 2), ManaSymbols.Produced("{G}{G}"));
        Assert.Equal(Pool(c: 1), ManaSymbols.Produced("{C}"));
        Assert.Equal(Pool(c: 2), ManaSymbols.Produced("{2}"));
        Assert.Equal(Pool(w: 1, b: 1), ManaSymbols.Produced("{W}{B}"));
        Assert.Equal(Pool(r: 1), ManaSymbols.OfColor(Color.Red));
    }

    [Theory]
    [InlineData("G", Color.Green)]
    [InlineData("{U}", Color.Blue)]
    [InlineData("white", Color.White)]
    [InlineData("C", Color.Colorless)]
    public void A_color_choice_is_read_from_the_command(string choice, Color expected) =>
        Assert.Equal(expected, ManaSymbols.ColorFromChoice(choice));

    [Fact]
    public void An_unknown_color_choice_is_no_color() => Assert.Null(ManaSymbols.ColorFromChoice("purple"));
}
