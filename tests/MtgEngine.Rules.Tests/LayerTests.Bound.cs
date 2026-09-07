using MtgEngine.Cards;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// CR 611.2c with 601.2c: an effect a resolving ability created applies to the objects it
/// announced, fixed as it resolved; the binding rides on the active effect (E0h, wave 18) and
/// the layer system reads a target from it, never from the board.
/// </summary>
public sealed partial class LayerTests
{
    [Fact]
    [RuleRef("611.2c")]
    public void A_copy_of_a_target_reads_the_object_the_effect_was_created_with()
    {
        var land = Obj(1, LandFace("Test Forest", "Forest"), Mine);
        var stage = Obj(2, LandFace("Test Stage"), Mine);
        var copy = new BecomesCopyOf(new Target(new ObjectFilter(Types: CardType.Land), Key: "target:0"));
        var bound = Active(1, copy, stage.Id, Mine, timestamp: 2) with
        {
            Remembered = ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty.Add("target:0", [land.Id]),
        };
        var board = Board(land, stage) with { ActiveEffects = [bound] };

        // CR 707.2: the copy takes the copiable values of the object the ability announced.
        var characteristics = _layers.Of(board, stage.Id);
        Assert.Equal("Test Forest", characteristics.Name);
        Assert.Contains("Forest", characteristics.Subtypes);
        Assert.Equal("Test Forest", _layers.Of(board, land.Id).Name);
    }
}
