using MtgEngine.Cards;
using MtgEngine.Rules.Engine;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// CR 305.6: an object with the land card type and a basic land type has the intrinsic mana
/// ability of that type, whether it printed one or not; CR 305.7: an effect that gives a land a
/// basic land type gives it that ability too. The layer system reads it off the subtypes layer 4
/// settled, so the printed Forest and the animated one are the same read (E0j, wave 21). Before
/// this every basic land had an empty ability list and could not be tapped for mana at all.
/// </summary>
public sealed partial class LayerTests
{
    [Fact]
    [RuleRef("305.6")]
    public void A_printed_basic_land_taps_for_its_types_mana_though_its_face_prints_nothing()
    {
        // The face is a type line and nothing else, which is what a real Forest is: CR 305.6 says
        // the ability is there "even if the text box doesn't actually contain that text".
        var forest = Obj(1, LandFace(subtype: "Forest"), Mine);
        Assert.Empty(forest.Definition.Faces[0].AbilityList);

        var chars = _layers.Of(Board(forest), forest.Id);

        Assert.Equal("{G}", Assert.Single(IntrinsicMana(chars)));
    }

    [Fact]
    [RuleRef("305.6")]
    public void Every_basic_land_type_the_rules_name_carries_the_mana_that_rule_pairs_with_it()
    {
        // CR 205.3i names the five and LandTypes.Basic is generated from that sentence; CR 305.6
        // pairs each with a symbol. Reading the generated set here is what keeps the two in step:
        // a sixth basic land type in a new rules file fails this test rather than tapping for
        // nothing.
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Plains"] = "{W}",
            ["Island"] = "{U}",
            ["Swamp"] = "{B}",
            ["Mountain"] = "{R}",
            ["Forest"] = "{G}",
        };

        Assert.Equal(expected.Count, LandTypes.Basic.Count);
        foreach (var basic in LandTypes.Basic)
        {
            var land = Obj(1, LandFace(subtype: basic), Mine);
            Assert.Equal(expected[basic], Assert.Single(IntrinsicMana(_layers.Of(Board(land), land.Id))));
        }
    }

    [Fact]
    [RuleRef("305.7")]
    public void A_land_that_becomes_a_Forest_by_an_effect_gains_the_mana_ability_with_the_type()
    {
        // CR 305.7: "it gains the appropriate mana ability for each new basic land type". The
        // subtype arrives in layer 4 (CR 613.1d) and the ability is read from the subtypes after
        // it, so nothing has to grant the ability separately.
        var land = Obj(1, LandFace(), Mine);
        Assert.Empty(IntrinsicMana(_layers.Of(Board(land), land.Id)));

        var board = Board(land) with
        {
            ActiveEffects = [Active(1, new AddTypes(Subtypes: ["Forest"]), land.Id, Mine, timestamp: 2)],
        };

        Assert.Equal("{G}", Assert.Single(IntrinsicMana(_layers.Of(board, land.Id))));
    }

    [Fact]
    [RuleRef("305.6")]
    public void A_land_with_two_basic_land_types_has_both_mana_abilities()
    {
        // A dual land is one object with two basic land types (CR 205.4a: it need not be basic),
        // and CR 305.6 gives it the ability of each — two abilities, not a choice inside one.
        var dual = Obj(1, LandFace(subtype: "Island"), Mine);
        var board = Board(dual) with
        {
            ActiveEffects = [Active(1, new AddTypes(Subtypes: ["Swamp"]), dual.Id, Mine, timestamp: 2)],
        };

        Assert.Equal(["{U}", "{B}"], IntrinsicMana(_layers.Of(board, dual.Id)));
    }

    [Fact]
    [RuleRef("305.6")]
    public void A_creature_that_is_not_a_land_gains_nothing_from_a_basic_land_subtype()
    {
        // CR 305.6 asks for the land card type as well as the subtype, so a creature given the
        // subtype alone taps for nothing. Give it the land type as well and the same object has
        // the ability, which is the whole of the difference: the type is the condition, and the
        // subtype without it is just a word on the type line.
        var bear = Obj(1, CreatureFace("Test Bear", 2, 2), Mine);
        var subtypeOnly = Board(bear) with
        {
            ActiveEffects = [Active(1, new AddTypes(Subtypes: ["Forest"]), bear.Id, Mine, timestamp: 2)],
        };

        Assert.Empty(IntrinsicMana(_layers.Of(subtypeOnly, bear.Id)));

        var alsoALand = Board(bear) with
        {
            ActiveEffects = [Active(1, new AddTypes(CardType.Land, Subtypes: ["Forest"]), bear.Id, Mine, timestamp: 2)],
        };

        Assert.Equal("{G}", Assert.Single(IntrinsicMana(_layers.Of(alsoALand, bear.Id))));
    }

    [Fact]
    [RuleRef("605.1a")]
    public void The_intrinsic_ability_is_shaped_as_the_mana_ability_CR_605_1a_describes()
    {
        // The engine recognises a mana ability by its shape (CR 605.1a): an activated ability
        // with no target, not a loyalty ability, that could add mana when it resolves. The cost
        // is tapping the land itself (CR 106.4, the "{T}" of the printed sentence).
        var mountain = Obj(1, LandFace(subtype: "Mountain"), Mine);
        var ability = Assert.IsType<ActivatedAbility>(Assert.Single(_layers.Of(Board(mountain), mountain.Id).Abilities));

        Assert.IsType<TapSelf>(Assert.Single(ability.Costs));
        Assert.Equal(Zone.Battlefield, ability.Where);
        Assert.Null(ability.MaxPerTurn);
        var add = Assert.IsType<AddMana>(ability.Do);
        Assert.Equal("{R}", add.Mana.Fixed);
        Assert.Null(add.N);
        Assert.False(add.Mana.AnyColor);
    }

    [Fact]
    [RuleRef("613.1f")]
    public void An_effect_that_removes_all_abilities_removes_the_intrinsic_one_too()
    {
        // The intrinsic ability is on the object going into layer 6, so CR 613.1f's ability
        // -removing effects reach it the way they reach a printed one. This is the ordering the
        // arm is placed for: layer 4 settles the subtypes, the ability is read, then layer 6 runs.
        var forest = Obj(1, LandFace(subtype: "Forest"), Mine);
        var board = Board(forest) with
        {
            ActiveEffects = [Active(1, new LoseAllAbilities(), forest.Id, Mine, timestamp: 2)],
        };

        Assert.Empty(_layers.Of(board, forest.Id).Abilities);
    }

    [Fact]
    [RuleRef("708.2a")]
    public void A_face_down_basic_land_taps_for_nothing()
    {
        // CR 708.2a: a face-down permanent is a 2/2 creature with no subtypes and no text, so it
        // is neither a land nor a Forest and CR 305.6 has nothing to say about it.
        var forest = Obj(1, LandFace(subtype: "Forest"), Mine) with { FaceDown = true };

        Assert.Empty(IntrinsicMana(_layers.Of(Board(forest), forest.Id)));
    }

    /// <summary>The fixed mana of each "{T}: Add [mana symbol]" this object has, in ability order.</summary>
    private static string[] IntrinsicMana(Characteristics chars) =>
    [
        .. chars.Abilities
            .OfType<ActivatedAbility>()
            .Where(a => a.Costs.Count == 1 && a.Costs[0] is TapSelf)
            .Select(a => a.Do)
            .OfType<AddMana>()
            .Select(a => a.Mana.Fixed ?? string.Empty),
    ];
}
