using MtgEngine.Cards;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Wave 28: manifest (CR 701.40). Before this wave nothing in the engine read the record and every
/// card that manifests refused with "there is no interpreter arm for Manifest".
/// </summary>
public sealed partial class InterpreterTests
{
    [Fact]
    [RuleRef("701.40a")]
    public void Manifest_puts_the_top_card_of_the_library_onto_the_battlefield_face_down()
    {
        var table = new InterpreterTable();
        var card = table.Card(table.A, Zone.Library);

        table.Run(new Manifest());

        // CR 701.40a: the card leaves the library and the permanent it becomes is face down; the
        // 2/2 with no name is FaceRules.FaceDown, which the layer system reads off that flag.
        Assert.Empty(table.In(Zone.Library, table.A));
        var manifested = Wave28OnBattlefield(table);
        Assert.True(table.Object(manifested).FaceDown);
        Assert.NotEqual(card, manifested);
    }

    [Fact]
    [RuleRef("701.40e")]
    public void Manifest_of_several_cards_takes_them_one_at_a_time_from_the_top()
    {
        var table = new InterpreterTable();
        table.Card(table.A, Zone.Library);
        table.Card(table.A, Zone.Library);
        table.Card(table.A, Zone.Library);

        table.Run(new Manifest(new Fixed(2)));

        // CR 701.40e: two cards manifested one at a time, so exactly two leave the library.
        Assert.Single(table.In(Zone.Library, table.A));
        Assert.Equal(2, table.State.Objects.Values.Count(o => o.Zone.Zone == Zone.Battlefield && o.FaceDown));
    }

    [Fact]
    [RuleRef("701.40a")]
    public void Manifest_reads_the_player_the_record_names_and_not_the_controller()
    {
        var table = new InterpreterTable();
        table.Card(table.A, Zone.Library);
        table.Card(table.B, Zone.Library);

        // Reality Shift manifests for the controller of the creature it exiled, never for its own
        // controller; a record that names a player is read, so A's library is untouched.
        table.Run(new Manifest(Who: new EachOpponent()));

        Assert.Single(table.In(Zone.Library, table.A));
        Assert.Empty(table.In(Zone.Library, table.B));
        Assert.True(table.Object(Wave28OnBattlefield(table)).FaceDown);
    }

    [Fact]
    [RuleRef("701.40a")]
    public void Manifest_with_an_empty_library_manifests_nothing()
    {
        var table = new InterpreterTable();

        table.Run(new Manifest());

        // CR 701.40a has nothing to turn face down, so no permanent arrives and nothing refuses.
        Assert.DoesNotContain(table.State.Objects.Values, o => o.Zone.Zone == Zone.Battlefield);
    }

    /// <summary>The one permanent on the battlefield, so a test can name what manifest created.</summary>
    private static ObjectId Wave28OnBattlefield(InterpreterTable table) =>
        table.State.Objects.Values.Single(o => o.Zone.Zone == Zone.Battlefield).Id;
}
