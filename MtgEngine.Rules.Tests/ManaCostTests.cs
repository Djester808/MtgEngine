using FluentAssertions;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.ValueObjects;
using Xunit;

namespace MtgEngine.Rules.Tests;

public class ManaCostTests
{
    [Theory]
    [InlineData("1G", 2)]
    [InlineData("2WW", 4)]
    [InlineData("3UBB", 6)]
    [InlineData("G", 1)]
    [InlineData("5", 5)]
    [InlineData("", 0)]
    public void ManaValue_calculates_correctly(string cost, int expected)
    {
        ManaCost.Parse(cost).ManaValue.Should().Be(expected);
    }

    [Fact]
    public void Colored_pips_parsed_correctly()
    {
        var cost = ManaCost.Parse("2WW");
        cost.Generic.Should().Be(2);
        cost.Colored[ManaColor.White].Should().Be(2);
    }

    [Fact]
    public void Can_be_paid_with_exact_mana()
    {
        var cost = ManaCost.Parse("1G");
        var pool = ManaPool.Empty.Add(ManaColor.Green, 2);

        cost.CanBePaidBy(pool).Should().BeTrue();
    }

    [Fact]
    public void Cannot_be_paid_with_wrong_color()
    {
        var cost = ManaCost.Parse("GG");
        var pool = ManaPool.Empty.Add(ManaColor.Red, 2);

        cost.CanBePaidBy(pool).Should().BeFalse();
    }

    [Fact]
    public void Generic_can_be_paid_with_any_color()
    {
        var cost = ManaCost.Parse("2");
        var pool = ManaPool.Empty.Add(ManaColor.Red).Add(ManaColor.Blue);

        cost.CanBePaidBy(pool).Should().BeTrue();
    }

    [Fact]
    public void Insufficient_mana_returns_false()
    {
        var cost = ManaCost.Parse("3GGG");
        var pool = ManaPool.Empty.Add(ManaColor.Green, 2);

        cost.CanBePaidBy(pool).Should().BeFalse();
    }

    [Fact]
    public void ManaPool_pay_reduces_pool_correctly()
    {
        var cost = ManaCost.Parse("1G");
        var pool = ManaPool.Empty.Add(ManaColor.Green, 2);

        var remaining = pool.Pay(cost);

        remaining.Total.Should().Be(0);
    }

    [Fact]
    public void ManaCost_equality_works()
    {
        var a = ManaCost.Parse("2WW");
        var b = ManaCost.Parse("2WW");
        var c = ManaCost.Parse("2WU");

        a.Should().Be(b);
        a.Should().NotBe(c);
    }

    [Fact]
    public void ToString_roundtrips_correctly()
    {
        var cost = ManaCost.Parse("2WW");
        cost.ToString().Should().Be("2WW");
    }
}
