using MtgEngine.Cards;
using MtgEngine.Rules.Interpreter;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The interpreter's freeze, CR 611.2c: the numbers of an effect a resolving spell or ability
/// creates are worked out as it resolves and never again. The layer system reads the frozen
/// literal (LayerTests), so this pins the half that produces it.
/// </summary>
public sealed class E0hFreezeTests
{
    [Fact]
    [RuleRef("611.2c")]
    public void A_counted_amount_is_fixed_to_its_value_as_the_effect_is_created()
    {
        var forests = new ObjectFilter(Subtypes: ["Forest"], ControlledBy: new You());
        var counted = new ModifyPt(new CountOf(forests), new CountOf(forests));

        var frozen = EffectWalk.Freeze(counted, _ => 3);

        var modify = Assert.IsType<ModifyPt>(frozen);
        Assert.Equal(new Fixed(3), modify.Power);
        Assert.Equal(new Fixed(3), modify.Toughness);
    }

    [Fact]
    [RuleRef("611.2c")]
    public void A_literal_is_left_as_it_is_and_the_tree_is_not_rebuilt()
    {
        var literal = new ModifyPt(new Fixed(1), new Fixed(1));

        var frozen = EffectWalk.Freeze(literal, _ => throw new InvalidOperationException("a literal is not evaluated"));

        Assert.Same(literal, frozen);
    }

    [Fact]
    [RuleRef("611.2c")]
    public void A_target_inside_a_copy_effect_is_left_for_the_binding_the_effect_is_created_with()
    {
        // Freeze fixes numbers; the objects a Target names are bound by the interpreter as the
        // effect is created (CR 601.2c) and read by the layers from that binding, so the selector
        // itself passes through unchanged.
        var of = new Target(new ObjectFilter(Types: CardType.Land), Key: "target:0");
        var copy = new BecomesCopyOf(of);

        var frozen = Assert.IsType<BecomesCopyOf>(EffectWalk.Freeze(copy, _ => 0));

        Assert.Same(of, frozen.Of);
    }
}
