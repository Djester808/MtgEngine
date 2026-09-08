using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The wave-29 joins E3r and E1g asked for: a copy count of 0 makes no copy (CR 702.40a with
/// 707.10), and a manifested card is offered to the game face down by the move itself rather than
/// turned over after it (CR 701.40a). Every definition here is invented.
/// </summary>
public sealed partial class InterpreterTests
{
    /// <summary>
    /// CR 702.40a: storm copies the spell once for each other spell cast before it this turn. A
    /// first spell has none, so its trigger copies zero times — the clamp to one made a copy for
    /// a storm of nothing.
    /// </summary>
    [Fact]
    [RuleRef("702.40a")]
    public void A_copy_count_of_zero_makes_no_copy()
    {
        var table = new InterpreterTable();
        var victim = table.Permanent(table.B);
        var spell = table.OnStack(table.A, new Destroy(new Target(ObjectFilter.Creature)), new StackEntry(null, null, [victim], [], [], null, false));

        var events = table.Run(new CopySpell(new Remembered("it"), new Fixed(0)), table.Context() with
        {
            Remembered = ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty.SetItem("it", [spell]),
        });

        Assert.DoesNotContain(events, e => e is ObjectCreated);
        Assert.Single(table.State.Objects.Values, o => o.Stack is not null);
    }

    /// <summary>CR 707.10: an absent count is still one copy, so the fix above changes nothing for the plain record.</summary>
    [Fact]
    [RuleRef("707.10")]
    public void An_absent_copy_count_still_makes_one_copy()
    {
        var table = new InterpreterTable();
        var victim = table.Permanent(table.B);
        var spell = table.OnStack(table.A, new Destroy(new Target(ObjectFilter.Creature)), new StackEntry(null, null, [victim], [], [], null, false));

        var events = table.Run(new CopySpell(new Remembered("it")), table.Context() with
        {
            Remembered = ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty.SetItem("it", [spell]),
        });

        Assert.Single(events, e => e is ObjectCreated);
    }

    /// <summary>
    /// CR 701.40a: the card is turned face down before it is put onto the battlefield, so the
    /// move the game sees already carries the face-down object and nothing is turned over after
    /// it — the interpreter used to emit a second event, which is what an entering trigger read
    /// past.
    /// </summary>
    [Fact]
    [RuleRef("701.40a")]
    public void Manifest_arrives_face_down_on_the_move_itself_and_is_not_turned_over_after()
    {
        var table = new InterpreterTable();
        table.Card(table.A, Zone.Library);

        var events = table.Run(new Manifest());

        var moved = Assert.IsType<ObjectMoved>(Assert.Single(events, e => e is ObjectMoved));
        Assert.True(moved.To.FaceDown);
        Assert.DoesNotContain(events, e => e is ObjectChanged);
        Assert.True(table.Object(moved.To.Id).FaceDown);
    }
}
