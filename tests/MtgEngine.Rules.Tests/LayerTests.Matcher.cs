using MtgEngine.Cards;
using MtgEngine.Rules.Engine;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// CR 611.3a with 613.1: a static ability's effect applies, at any given moment, to whatever its
/// filter admits now. The fields the layer system's own matcher cannot read — the predicate tail,
/// a mana value, a bound that has to be counted — are asked of the query layer (E0i, wave 19);
/// what neither can answer is refused with its rule and never thrown.
/// </summary>
public sealed partial class LayerTests
{
    [Fact]
    [RuleRef("611.3a")]
    public void A_static_reaches_only_the_objects_its_Also_predicate_admits()
    {
        // "Creatures of the chosen color get +1/+1": the filter's predicate reads the colour the
        // source itself was given as it entered (CR 608.2d), so the context carries the source's
        // own chosen values. Before this the whole ability was refused for carrying an Also at all.
        var chosen = new ObjectFilter(Types: CardType.Creature, Also: new IsChosenColor("k1"));
        var gauntlet = Obj(1, EnchantmentFace(1, 1, chosen), Mine) with
        {
            Chosen = ImmutableDictionary<string, string>.Empty.Add("k1", nameof(Color.Red)),
        };
        var red = Obj(2, CreatureFace("Test Devil", 2, 2, manaCost: "{1}{R}"), Mine);
        var green = Obj(3, CreatureFace("Test Bear", 2, 2), Theirs);
        var board = Board(gauntlet, red, green);

        Assert.Equal(3, _layers.Of(board, red.Id).Power);
        Assert.Equal(3, _layers.Of(board, red.Id).Toughness);

        // The green one is a creature and nothing else about it matters: the predicate is the
        // whole of the difference, and a filter is a conjunction (CR 611.3a).
        Assert.Equal(2, _layers.Of(board, green.Id).Power);
        Assert.Equal(2, _layers.Of(board, green.Id).Toughness);
    }

    [Fact]
    [RuleRef("611.3a")]
    public void A_mana_value_bound_admits_the_cheap_creature_and_not_the_costly_one()
    {
        // CR 202.3: the mana value is the converted total of the printed cost, which is not a
        // characteristic the layers compute and was another field the matcher used to refuse on.
        var cheapOnes = new ObjectFilter(
            Types: CardType.Creature,
            ControlledBy: new You(),
            ManaValue: new Comparison(ComparisonOp.Le, new Fixed(2)));
        var banner = Obj(1, EnchantmentFace(1, 1, cheapOnes), Mine);
        var cheap = Obj(2, CreatureFace("Test Bear", 2, 2), Mine);
        var costly = Obj(3, CreatureFace("Test Wurm", 2, 2, manaCost: "{4}{G}{G}"), Mine);
        var board = Board(banner, cheap, costly);

        Assert.Equal(3, _layers.Of(board, cheap.Id).Power);
        Assert.Equal(3, _layers.Of(board, cheap.Id).Toughness);
        Assert.Equal(2, _layers.Of(board, costly.Id).Power);
        Assert.Equal(2, _layers.Of(board, costly.Id).Toughness);
    }

    [Fact]
    [RuleRef("613.8")]
    public void A_counted_bound_in_a_static_filter_does_not_recur_into_the_statics_it_is_gathering()
    {
        // "Creatures with power less than the number of creatures you control get +1/+1". Counting
        // them reads every creature's characteristics, and one of those objects is the source whose
        // static ability is being gathered to ask the question. CR 613.8's dependency rules are not
        // built: the read inside a filter leaves the board's statics out, so this terminates and
        // answers off printed power. Three creatures, each printed 2/2, so each is admitted.
        var yours = new ObjectFilter(Types: CardType.Creature, ControlledBy: new You());
        var weakOnes = yours with { Power = new Comparison(ComparisonOp.Lt, new CountOf(yours)) };
        var lord = Obj(1, LordFace(1, 1, weakOnes), Mine);
        var bear = Obj(2, CreatureFace("Test Bear", 2, 2), Mine);
        var other = Obj(3, CreatureFace("Test Boar", 2, 2), Mine);
        var board = Board(lord, bear, other);

        Assert.Equal(3, _layers.Of(board, bear.Id).Power);
        Assert.Equal(3, _layers.Of(board, other.Id).Power);
        Assert.Equal(3, _layers.Of(board, lord.Id).Power);

        // With only two of them the count is 2 and a printed 2 is not less than it: the bound is
        // read from the board as it is now (CR 611.3a), not fixed when the ability began.
        var fewer = Board(lord, bear);
        Assert.Equal(2, _layers.Of(fewer, bear.Id).Power);
    }

    [Fact]
    [RuleRef("611.3a")]
    public void A_predicate_the_query_has_not_built_is_refused_with_its_rule_and_never_thrown()
    {
        // Blocking legality is the combat subsystem's (CR 509.1a) and the query says so rather
        // than guessing; the layer system passes that refusal on instead of crashing a game.
        var blockers = new ObjectFilter(Types: CardType.Creature, Also: new CanBlock(new Self()));
        var banner = Obj(1, EnchantmentFace(1, 1, blockers), Mine);
        var bear = Obj(2, CreatureFace("Test Bear", 2, 2), Mine);
        var board = Board(banner, bear);

        var ex = Assert.Throws<RefusedException>(() => _layers.Of(board, bear.Id));
        Assert.Contains(nameof(CanBlock), ex.Reason, StringComparison.Ordinal);
        Assert.Equal("509.1a", ex.Rule);
    }

    [Fact]
    [RuleRef("611.3a")]
    public void A_filter_the_matcher_answers_on_its_own_still_reads_the_board_with_its_statics()
    {
        // The join is a routing decision and nothing else: a filter with no field the matcher is
        // missing takes the same road it always did, and the statics of the board are still what
        // an ordinary read of an object's characteristics applies (CR 613.1).
        var plain = new ObjectFilter(Types: CardType.Creature, ControlledBy: new You(), Other: true);
        var lord = Obj(1, LordFace(1, 1, plain), Mine);
        var bear = Obj(2, CreatureFace("Test Bear", 2, 2), Mine);
        var board = Board(lord, bear);

        Assert.Equal(3, _layers.Of(board, bear.Id).Power);
        Assert.Equal(2, _layers.Of(board, lord.Id).Power);
    }
}
