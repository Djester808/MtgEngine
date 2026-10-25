using System.Text.Json;
using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The seams cut after wave 9, from the builders' requests: an object that remembers what it
/// chose (E6c), a stack entry that carries the choices and the mana that paid (E6c, E3g), a mana
/// change that says what it paid for (E6c), one reading of a player filter (E5d), the printed
/// characteristics off an object alone (E0d), an instructed cast (EINT3), a copy in place (E9h),
/// and the colours an activation chooses (P12). Each is additive; the builders that read them are
/// wave 10's.
/// </summary>
public sealed class WaveTenSeamTests
{
    private static readonly PlayerId P0 = new(0);

    [Fact]
    [RuleRef("608.2d")]
    public void A_chosen_value_is_kept_on_the_object_that_chose_it()
    {
        var board = new Board();
        var source = board.Put(Board.Creature(1, 1, "seam chooser"));

        var state = new ValueChosen(source, "color", "Green").Apply(board.State);

        Assert.Equal("Green", state.Objects[source].Chosen["color"]);
        Assert.Empty(board.State.Objects[source].Chosen);
        Assert.Same(state, new ValueChosen(new ObjectId(9999), "color", "Red").Apply(state));
    }

    [Fact]
    [RuleRef("106.6a")]
    public void A_mana_change_can_say_what_it_paid_for_and_the_stack_entry_can_carry_the_choices()
    {
        var board = new Board();
        var spell = board.Put(Board.Creature(1, 1, "seam spell"));
        var paid = new ManaChanged(P0, ManaPool.Empty, "cast", SpentOn: spell);
        Assert.Equal(spell, paid.SpentOn);
        Assert.Null(new ManaChanged(P0, ManaPool.Empty, "produced").SpentOn);

        var entry = new StackEntry(spell, 0, [], [], [], null, false, Chosen: ImmutableDictionary<string, string>.Empty.Add("tapped-for-mana-color", "Green"));
        Assert.Equal("Green", entry.Chosen!["tapped-for-mana-color"]);
        Assert.Null(entry.ManaProvenanceSpent);
    }

    [Fact]
    [RuleRef("608.2d")]
    public void An_activation_can_choose_the_colours_it_adds()
    {
        var step = new ActivateStep("A", "ring", 0, ManaColors: "WU");
        var json = JsonSerializer.Serialize<ScenarioStep>(step, CardFileSerializer.Options);

        Assert.Contains("manaColors", json, StringComparison.Ordinal);
        Assert.Equal("WU", Assert.IsType<ActivateStep>(JsonSerializer.Deserialize<ScenarioStep>(json, CardFileSerializer.Options)).ManaColors);
        Assert.Null(new ActivateAbility(P0, new ObjectId(1), 0).ManaColors);
        Assert.Equal("W", new ActivateAbility(P0, new ObjectId(1), 0, ManaColors: "W").ManaColors);
    }
}
