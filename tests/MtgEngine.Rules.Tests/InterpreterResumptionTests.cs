using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Layers;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Resuming a resolution the game stopped to ask about (CR 101.4), and the verbs that turn a card
/// over or copy one (CR 701.27, 710, 708, 707). Every resumption here goes through the seam the
/// choice system uses, with nothing carried between the two calls but the state and the answer,
/// so a game rebuilt from its log resumes the same way.
/// </summary>
public sealed class InterpreterResumptionTests
{
    /* -------------------------------------------------------------------------------------- *
     * Each kind of question answers and continues.
     * -------------------------------------------------------------------------------------- */

    [Fact]
    public void May_answered_yes_runs_the_effect_and_does_not_repeat_what_came_before()
    {
        var table = new InterpreterTable();
        var spell = table.OnStack(table.A, new Sequence([new GainLife(new Fixed(1)), new May(new GainLife(new Fixed(5)))]));

        table.Resolve(spell);
        Assert.Equal(21, table.Life(table.A));
        Assert.Equal("may", table.Pending.Kind);

        var events = table.Answer("yes");

        // 21 + 5, not 22 + 5: the gain before the question is not run a second time.
        Assert.Equal(26, table.Life(table.A));
        Assert.Contains(events, e => e is StackObjectResolved);
    }

    [Fact]
    public void May_answered_no_does_not_run_the_effect()
    {
        var table = new InterpreterTable();
        var spell = table.OnStack(table.A, new Sequence([new GainLife(new Fixed(1)), new May(new GainLife(new Fixed(5)))]));

        table.Resolve(spell);
        var events = table.Answer("no");

        Assert.Equal(21, table.Life(table.A));
        Assert.Contains(events, e => e is StackObjectResolved);
    }

    [Fact]
    public void A_chosen_value_of_three_is_the_number_the_rest_of_the_effect_uses()
    {
        var table = new InterpreterTable();
        var spell = table.OnStack(table.A, new Sequence(
        [
            new ChooseValue(ChooseWhat.Number, "n", Options: ["1", "2", "3"]),
            new GainLife(new ChosenNumber("n")),
        ]));

        table.Resolve(spell);
        Assert.Equal("value", table.Pending.Kind);

        table.Answer("3");

        Assert.Equal(23, table.Life(table.A));
    }

    [Fact]
    public void A_search_puts_the_chosen_card_in_hand_and_leaves_the_rest_in_the_library()
    {
        var table = new InterpreterTable();
        var wanted = table.Card(table.A, Zone.Library);
        table.Card(table.A, Zone.Library);
        var spell = table.OnStack(table.A, new Search(new ObjectFilter(In: Zone.Library, Types: CardType.Creature), Zone.Hand, ShuffleAfter: false));

        table.Resolve(spell);
        Assert.Equal("search", table.Pending.Kind);
        Assert.Equal(2, table.Pending.Options.Count);

        table.Answer(wanted.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.Single(table.In(Zone.Hand, table.A));
        Assert.Single(table.In(Zone.Library, table.A));
    }

    [Fact]
    public void A_scried_card_goes_to_the_bottom_of_the_library()
    {
        var table = new InterpreterTable();
        var top = table.Card(table.A, Zone.Library);
        var second = table.Card(table.A, Zone.Library);
        var spell = table.OnStack(table.A, new Scry(new Fixed(2)));

        table.Resolve(spell);
        Assert.Equal("scry", table.Pending.Kind);

        table.Answer(top.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var library = table.In(Zone.Library, table.A);
        Assert.Equal(2, library.Count);

        // Both cards remain in the same zone, with the chosen card at the bottom.
        Assert.Equal(second, library[0]);
        Assert.Equal(top, library[1]);
    }

    [Fact]
    public void A_trigger_order_answer_reorders_the_stack_into_the_order_chosen()
    {
        var table = new InterpreterTable();
        var first = table.OnStack(table.A, new GainLife(new Fixed(1)));
        var second = table.OnStack(table.A, new GainLife(new Fixed(2)));
        var resumption = new Resumption(
            ChoiceKinds.TriggerOrder,
            table.A,
            [Option(second), Option(first)],
            new ChoiceContext(null, null, null, ImmutableDictionary<string, string>.Empty));

        var outcome = table.Subject.Resume(table.State, resumption);

        Assert.Contains(outcome.Events, e => e is ZoneReordered);
        Assert.Equal([second, first], outcome.State.In(ZoneKey.Shared(Zone.Stack)));
    }

    /// <summary>
    /// CR 616.1 with 101.4: a question the combat or the zone layer raised names no effect tree,
    /// so there is no walk to go back to and the answer is the replacement system's alone.
    /// </summary>
    [Fact]
    public void A_replacement_order_answer_with_no_walk_behind_it_is_handed_to_the_replacement_system()
    {
        var table = new InterpreterTable();
        var resumption = new Resumption(
            ChoiceKinds.ReplacementOrder,
            table.A,
            [],
            new ChoiceContext(null, null, null, ImmutableDictionary<string, string>.Empty));

        table.Subject.Resume(table.State, resumption);

        Assert.Same(resumption, Assert.Single(table.Replacements.Resumed));
    }

    [Fact]
    public void A_targets_answer_is_bound_before_the_instruction_that_needs_it()
    {
        var table = new InterpreterTable();
        var victim = table.Permanent(table.B);
        var spell = table.OnStack(table.A, new Destroy(new Target(ObjectFilter.Creature)));
        var resumption = new Resumption(
            ChoiceKinds.Targets,
            table.A,
            [Option(victim)],
            new ChoiceContext(null, null, "0", ImmutableDictionary<string, string>.Empty));

        var outcome = table.Subject.Resume(table.State, resumption);

        Assert.Contains(outcome.Events, e => e is ObjectLeftBy left && left.Object == victim && left.Verb == "destroyed");
    }

    /// <summary>
    /// The question came from inside an ability, not from a spell's own text, so the answer has to
    /// find that ability again by the index the stack object carries (CR 603.3d).
    /// </summary>
    [Fact]
    public void An_ability_resumes_through_the_ability_index_the_question_recorded()
    {
        var table = new InterpreterTable();
        var source = table.Permanent(table.A);
        var ability = table.OnStack(table.A, new May(new GainLife(new Fixed(4))), new StackEntry(source, 0, [], [], [], null, false));

        table.Resolve(ability);

        Assert.Equal(0, table.Pending.Context.AbilityIndex);
        Assert.Equal(source, table.Pending.Context.Source);
        table.Answer("yes");
        Assert.Equal(24, table.Life(table.A));
    }

    /* -------------------------------------------------------------------------------------- *
     * The face verbs, CR 701.27, 701.28, 710, 708, 701.42.
     * -------------------------------------------------------------------------------------- */

    [Fact]
    public void Transforming_turns_the_face_over_and_the_layers_then_read_the_back_face()
    {
        var table = new InterpreterTable(new LayerSystem());
        var id = table.Put(table.A, ZoneKey.Shared(Zone.Battlefield), TwoFaced(Layout.Transform));

        var events = table.Run(new Transform(new Each(ObjectFilter.Creature)));

        var turned = Assert.IsType<ObjectTurnedOver>(Assert.Single(events));
        Assert.Equal(TurnKind.Transform, turned.Kind);
        Assert.Equal(1, table.Object(id).FaceIndex);

        var now = table.Characteristics.Of(table.State, id);
        Assert.Equal("back", now.Name);
        Assert.Equal(3, now.Power);
    }

    /// <summary>CR 701.27c: a permanent that is not a double-faced card does not transform.</summary>
    [Fact]
    public void Transforming_a_single_faced_permanent_does_nothing()
    {
        var table = new InterpreterTable();
        table.Permanent(table.A);

        Assert.Empty(table.Run(new Transform(new Each(ObjectFilter.Creature))));
    }

    /// <summary>CR 710.2: a flip card's "flip" is one-way, and CR 710.4 refuses the second one.</summary>
    [Fact]
    public void Flipping_a_flip_card_turns_it_once_and_never_back()
    {
        var table = new InterpreterTable();
        var id = table.Put(table.A, ZoneKey.Shared(Zone.Battlefield), Flippable());

        var events = table.Run(new Transform(new Each(ObjectFilter.Creature)));

        Assert.Equal(TurnKind.Flip, Assert.IsType<ObjectTurnedOver>(Assert.Single(events)).Kind);
        Assert.Equal(1, table.Object(id).FaceIndex);
        Assert.Empty(table.Run(new Transform(new Each(ObjectFilter.Creature))));
    }

    /// <summary>CR 708: a permanent turned face down, and turned face up again.</summary>
    [Fact]
    public void Turning_a_permanent_face_down_and_face_up_again()
    {
        var table = new InterpreterTable();
        var id = table.Permanent(table.A);

        var down = Assert.IsType<ObjectTurnedOver>(Assert.Single(table.Run(new TurnFaceDown(new Each(ObjectFilter.Creature)))));
        Assert.Equal(TurnKind.TurnedFaceDown, down.Kind);
        Assert.True(table.Object(id).FaceDown);

        // CR 708.4: it is already face down, so turning it face down again does nothing.
        Assert.Empty(table.Run(new TurnFaceDown(new Each(ObjectFilter.Creature))));

        var up = Assert.IsType<ObjectTurnedOver>(Assert.Single(table.Run(new TurnFaceUp(new Each(ObjectFilter.Creature)))));
        Assert.Equal(TurnKind.TurnedFaceUp, up.Kind);
        Assert.False(table.Object(id).FaceDown);
    }

    /// <summary>CR 701.42c: two cards that are not a meld pair do not meld and do not move.</summary>
    [Fact]
    public void Melding_two_cards_that_are_not_a_pair_does_nothing()
    {
        var table = new InterpreterTable();
        var source = table.Permanent(table.A);
        table.Permanent(table.A);

        Assert.Empty(table.Run(new Meld(new Each(ObjectFilter.Creature)), table.Context(source)));
    }

    /* -------------------------------------------------------------------------------------- *
     * Copies, CR 707.
     * -------------------------------------------------------------------------------------- */

    [Fact]
    public void A_copy_token_has_the_copiable_values_and_none_of_the_counters()
    {
        var table = new InterpreterTable();
        var original = table.Put(table.A, ZoneKey.Shared(Zone.Battlefield), TwoFaced(Layout.Transform));
        table.Run(new Sequence(
        [
            new Transform(new Each(ObjectFilter.Creature)),
            new PutCounters(new Each(ObjectFilter.Creature), "+1/+1", new Fixed(2)),
        ]));
        Assert.Equal(2, table.Object(original).Counters["+1/+1"]);

        var events = table.Run(new CreateToken(new TokenSpec("copy", Color.Colorless, CardType.Creature), CopyOf: new Each(ObjectFilter.Creature)));

        var created = Assert.IsType<ObjectCreated>(Assert.Single(events, e => e is ObjectCreated)).Object;
        Assert.True(created.IsToken);

        // CR 707.2: the definition and the face that is up are copiable; counters are not.
        Assert.Same(table.Object(original).Definition, created.Definition);
        Assert.Equal(1, created.FaceIndex);
        Assert.Empty(created.Counters);
        Assert.Equal(0, created.Damage);
    }

    /// <summary>CR 707.10: a copy of a spell is put on the stack with the original's choices.</summary>
    [Fact]
    public void A_copy_of_a_spell_is_put_on_the_stack_as_a_copy()
    {
        var table = new InterpreterTable();
        var victim = table.Permanent(table.B);
        var spell = table.OnStack(table.A, new Destroy(new Target(ObjectFilter.Creature)), new StackEntry(null, null, [victim], [], [], null, false));

        // MayChooseNewTargets off: the copy takes the original's targets and nobody is asked.
        var events = table.Run(new CopySpell(new Remembered("it"), MayChooseNewTargets: false), table.Context() with
        {
            Remembered = ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty.SetItem("it", [spell]),
        });

        var copy = Assert.IsType<ObjectCreated>(Assert.Single(events)).Object;
        Assert.True(copy.Stack!.IsCopy);
        Assert.Equal([victim], copy.Stack.Targets);
        Assert.False(copy.IsToken);
    }

    /// <summary>
    /// CR 707.10: "the controller of the copy may choose new targets for it." The flag has been
    /// on the record since wave 0 and defaults to true, and nothing read it — so a Fork could
    /// never be pointed anywhere else and every storm copy was locked to the original's target.
    /// </summary>
    [Fact]
    [RuleRef("707.10")]
    public void The_controller_of_a_copy_is_asked_for_new_targets()
    {
        var table = new InterpreterTable();
        var victim = table.Permanent(table.B);
        var spell = table.OnStack(table.A, new Destroy(new Target(ObjectFilter.Creature)), new StackEntry(null, null, [victim], [], [], null, false));

        var events = table.Run(new CopySpell(new Remembered("it")), table.Context() with
        {
            Remembered = ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty.SetItem("it", [spell]),
        });

        // The copy is made, and then its controller is asked where to point it.
        var copy = Assert.IsType<ObjectCreated>(events[0]).Object;
        Assert.True(copy.Stack!.IsCopy);

        var asked = Assert.IsType<ChoiceRequested>(events[1]).Choice;
        Assert.Equal(table.A, asked.Player);
        Assert.Equal("target", asked.Kind);
        Assert.Equal(1, asked.Min);
    }

    /// <summary>A copy that targets nothing has nothing to re-aim, so nobody is asked.</summary>
    [Fact]
    [RuleRef("707.10")]
    public void A_copy_that_targets_nothing_asks_nobody()
    {
        var table = new InterpreterTable();
        var spell = table.OnStack(table.A, new Draw(new Fixed(1)), new StackEntry(null, null, [], [], [], null, false));

        var events = table.Run(new CopySpell(new Remembered("it")), table.Context() with
        {
            Remembered = ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty.SetItem("it", [spell]),
        });

        Assert.IsType<ObjectCreated>(Assert.Single(events));
    }

    /* -------------------------------------------------------------------------------------- *
     * Last known information, CR 608.2h.
     * -------------------------------------------------------------------------------------- */

    /// <summary>
    /// A dies trigger says "that creature", and by the time it resolves the creature is not on the
    /// battlefield any more. What the trigger triggered on travels on the stack object (CR 603.3d)
    /// and the selector still finds it, which is what CR 608.2h means by last known information.
    /// </summary>
    [Fact]
    public void A_dies_trigger_reads_the_creature_that_left_the_battlefield()
    {
        var table = new InterpreterTable();
        var creature = table.Permanent(table.A);
        var died = Assert.IsType<ObjectMoved>(Assert.Single(
            table.Run(new Destroy(new Each(ObjectFilter.Creature))), e => e is ObjectMoved));

        Assert.Equal(creature, died.Departed!.Id);
        Assert.False(table.State.Objects.ContainsKey(creature));

        var trigger = table.OnStack(
            table.A,
            new ForEach(new Remembered("triggering"), new GainLife(new Fixed(7))),
            new StackEntry(null, null, [], [], [], null, false, Triggering: [died.Departed.Id]));

        table.Resolve(trigger);

        Assert.Equal(27, table.Life(table.A));
    }

    private static ChoiceOption Option(ObjectId id) =>
        new(id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), "option", id);

    /// <summary>A double-faced card: a 1/1 front and a 3/3 back, so the layers can be seen to change.</summary>
    private static CardDefinition TwoFaced(Layout layout) => new(
        "00000000-0000-0000-0000-00000000000b",
        "front",
        layout,
        [
            new Face("front", FaceRole.Main, new TypeLine(CardType.Creature), Power: new PrintedStat("1"), Toughness: new PrintedStat("1")),
            new Face("back", FaceRole.Back, new TypeLine(CardType.Creature), Power: new PrintedStat("3"), Toughness: new PrintedStat("3")),
        ]);

    private static CardDefinition Flippable() => new(
        "00000000-0000-0000-0000-00000000000c",
        "upright",
        Layout.Flip,
        [
            new Face("upright", FaceRole.Main, new TypeLine(CardType.Creature), Power: new PrintedStat("1"), Toughness: new PrintedStat("1")),
            new Face("flipped", FaceRole.Flipped, new TypeLine(CardType.Creature), Power: new PrintedStat("2"), Toughness: new PrintedStat("2")),
        ]);
}
