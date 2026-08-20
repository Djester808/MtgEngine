using MtgEngine.Domain.Enums;
using MtgEngine.Rules.Mana;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Mana symbols, costs, and paying them (CR 106, 107.4, 202, 601.2h).
/// </summary>
/// <remarks>
/// The engine has its own mana model because the domain's <c>ManaCost</c> is lossy by design:
/// <c>CardParser</c> strips hybrid, phyrexian, X, snow and colourless symbols before parsing, so
/// <c>{2/W}</c> arrives as <c>2W</c> and <c>{X}</c> disappears. That is the right trade for deck
/// statistics and the wrong one for deciding what a player can cast.
/// </remarks>
public sealed class ManaTests
{
    [Fact]
    public void A_plain_cost_parses_symbol_by_symbol()
    {
        var cost = ManaCostSpec.Parse("{2}{G}{G}");

        Assert.Equal(3, cost.Symbols.Count);
        Assert.Equal(4, cost.ManaValue);
    }

    [Fact]
    public void A_hybrid_symbol_survives_parsing()
    {
        // The domain parser turns this into "W" and loses the choice.
        var cost = ManaCostSpec.Parse("{W/U}");

        var symbol = Assert.Single(cost.Symbols);
        Assert.True(symbol.IsHybrid);
        Assert.Contains(ManaColor.White, symbol.Colors);
        Assert.Contains(ManaColor.Blue, symbol.Colors);
        Assert.Equal(1, cost.ManaValue);
    }

    [Fact]
    public void A_monocoloured_hybrid_keeps_both_halves()
    {
        // CR 107.4d: {2/W} is two generic or one white, and its mana value is 2. The domain
        // parser reads it as "2W", a cost of three mana that also demands white.
        var cost = ManaCostSpec.Parse("{2/W}");

        var symbol = Assert.Single(cost.Symbols);
        Assert.Equal(2, symbol.Generic);
        Assert.Contains(ManaColor.White, symbol.Colors);
        Assert.Equal(2, cost.ManaValue);
    }

    [Fact]
    public void Phyrexian_and_variable_symbols_are_recognised()
    {
        // CR 107.4f and 107.3.
        var cost = ManaCostSpec.Parse("{X}{W/P}{C}");

        Assert.True(cost.HasVariable);
        Assert.True(cost.Symbols[1].IsPhyrexian);
        Assert.True(cost.Symbols[2].IsColorless);
        // CR 202.3b: {X} is zero anywhere but on the stack.
        Assert.Equal(2, cost.ManaValue);
    }

    [Fact]
    public void An_empty_cost_is_free()
    {
        Assert.Equal(ManaCostSpec.Free, ManaCostSpec.Parse(null));
        Assert.Equal(ManaCostSpec.Free, ManaCostSpec.Parse(""));
    }

    [Fact]
    public void A_cost_round_trips_through_its_own_text()
    {
        Assert.Equal("{2}{W/U}{X}", ManaCostSpec.Parse("{2}{W/U}{X}").ToString());
    }

    [Fact]
    public void Coloured_mana_pays_a_coloured_symbol()
    {
        var pool = ManaPool.Empty.Add(ManaColor.Green, 2);

        var left = ManaPayment.Pay(pool, ManaCostSpec.Parse("{G}"));

        Assert.NotNull(left);
        Assert.Equal(1, left[ManaColor.Green]);
    }

    [Fact]
    public void The_wrong_colour_cannot_pay_a_coloured_symbol()
    {
        var pool = ManaPool.Empty.Add(ManaColor.Red, 5);

        Assert.Null(ManaPayment.Pay(pool, ManaCostSpec.Parse("{G}")));
    }

    [Fact]
    public void Anything_pays_generic_mana()
    {
        var pool = ManaPool.Empty.Add(ManaColor.Red, 1).AddColorless(2);

        var left = ManaPayment.Pay(pool, ManaCostSpec.Parse("{3}"));

        Assert.NotNull(left);
        Assert.True(left.IsEmpty);
    }

    [Fact]
    public void Coloured_symbols_are_paid_before_generic_ones()
    {
        // The reason payment is not a subtraction. With one green and one red against {1}{G},
        // paying the generic first can spend the green and then fail on a {G} that was payable
        // all along.
        var pool = ManaPool.Empty.Add(ManaColor.Green, 1).Add(ManaColor.Red, 1);

        var left = ManaPayment.Pay(pool, ManaCostSpec.Parse("{1}{G}"));

        Assert.NotNull(left);
        Assert.True(left.IsEmpty);
    }

    [Fact]
    public void A_hybrid_is_paid_with_whichever_half_the_pool_has()
    {
        var white = ManaPayment.Pay(ManaPool.Empty.Add(ManaColor.White), ManaCostSpec.Parse("{W/U}"));
        var blue = ManaPayment.Pay(ManaPool.Empty.Add(ManaColor.Blue), ManaCostSpec.Parse("{W/U}"));

        Assert.NotNull(white);
        Assert.NotNull(blue);
    }

    [Fact]
    public void A_monocoloured_hybrid_falls_back_to_its_generic_half()
    {
        // CR 107.4d: no white, but two of anything will do.
        var pool = ManaPool.Empty.Add(ManaColor.Red, 2);

        Assert.NotNull(ManaPayment.Pay(pool, ManaCostSpec.Parse("{2/W}")));
        Assert.Null(ManaPayment.Pay(ManaPool.Empty.Add(ManaColor.Red, 1), ManaCostSpec.Parse("{2/W}")));
    }

    [Fact]
    public void Colourless_mana_only_pays_a_colourless_symbol()
    {
        // CR 106.1b: colourless is a kind of mana, not the absence of one.
        Assert.NotNull(ManaPayment.Pay(ManaPool.Empty.AddColorless(1), ManaCostSpec.Parse("{C}")));
        Assert.Null(ManaPayment.Pay(ManaPool.Empty.Add(ManaColor.Green, 1), ManaCostSpec.Parse("{C}")));
    }

    [Fact]
    public void X_adds_to_the_generic_part_of_the_cost()
    {
        // CR 601.2b: X is chosen as the spell is cast, and then it is just that much generic.
        var pool = ManaPool.Empty.Add(ManaColor.Red, 1).AddColorless(3);

        Assert.NotNull(ManaPayment.Pay(pool, ManaCostSpec.Parse("{X}{R}"), variableValue: 3));
        Assert.Null(ManaPayment.Pay(pool, ManaCostSpec.Parse("{X}{R}"), variableValue: 4));
    }

    [Fact]
    public void An_empty_pool_pays_nothing_but_a_free_cost()
    {
        Assert.NotNull(ManaPayment.Pay(ManaPool.Empty, ManaCostSpec.Free));
        Assert.Null(ManaPayment.Pay(ManaPool.Empty, ManaCostSpec.Parse("{1}")));
    }

    [Fact]
    public void A_pool_compares_by_what_is_in_it()
    {
        Assert.Equal(
            ManaPool.Empty.Add(ManaColor.Green, 1),
            ManaPool.Empty.Add(ManaColor.Green, 1));
        Assert.NotEqual(
            ManaPool.Empty.Add(ManaColor.Green, 1),
            ManaPool.Empty.Add(ManaColor.Red, 1));
        // Spending back to nothing is the same as never having had it.
        Assert.Equal(ManaPool.Empty, ManaPool.Empty.Add(ManaColor.Green).Spend(ManaColor.Green));
    }
}
