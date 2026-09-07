using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// CR 614.1c with 608.2d: "as this enters, choose a creature type". The choosing is part of the
/// entering rather than a replacement of it, so the object still arrives; the question is raised
/// against the object that arrives and the answer is kept on that permanent. Read through the front
/// door on <see cref="Engine.Subsystems.Wave2"/>, and once at the unit level for the index the
/// answer is found again by (CR 101.4).
/// </summary>
public sealed partial class ReplacementEffectTests
{
    private static PlayerId Seat => ReplacementEffectTestsEnteringTable.P0;

    /// <summary>
    /// CR 614.1c: the choice is made <em>as</em> the permanent enters, so the land is already on the
    /// battlefield while the game waits, and CR 608.2d writes the answer on it. Before the choose-only
    /// arm the event fell to CR 614.1a's "instead" and the land never arrived at all.
    /// </summary>
    [Fact]
    [RuleRef("614.1c")]
    public void A_land_that_chooses_a_type_as_it_enters_arrives_and_keeps_the_answer()
    {
        var table = ReplacementEffectTestsEnteringTable.WithTheLandPlayed();
        var pending = table.State.Pending;
        Assert.NotNull(pending);
        Assert.Equal(ChoiceKinds.Value, pending.Kind);
        Assert.Equal(Seat, pending.Player);

        var land = Assert.Single(table.Battlefield(Seat, ReplacementEffectTestsEnteringTable.CavernName));
        Assert.Equal(land, pending.Context.Source);

        var events = table.Do(new AnswerChoice(pending.Player, pending.Id, ["Goblin"]));

        // CR 608.2d: the permanent keeps the choice, which is where every later reader of "the chosen
        // type" looks — the mana ability of this same land among them.
        var chosen = Assert.Single(events.OfType<ValueChosen>());
        Assert.Equal(land, chosen.Object);
        Assert.Equal(ReplacementEffectTestsEnteringTable.TypeKey, chosen.Key);
        Assert.Equal("Goblin", chosen.Value);
        Assert.Equal("Goblin", table.State.Objects[land].Chosen[ReplacementEffectTestsEnteringTable.TypeKey]);
        Assert.Null(table.State.Pending);
    }

    /// <summary>
    /// CR 101.4 with 614.1c: until it is answered nothing else happens — but the object has entered,
    /// so the effect that asked can still be found on it. The land waiting on its own question is the
    /// state a game is saved and rebuilt in.
    /// </summary>
    [Fact]
    [RuleRef("101.4")]
    public void An_unanswered_choice_as_a_land_enters_leaves_the_game_pending_with_the_land_entered()
    {
        var table = ReplacementEffectTestsEnteringTable.WithTheLandPlayed();

        var pending = table.State.Pending;
        Assert.NotNull(pending);
        var land = Assert.Single(table.Battlefield(Seat, ReplacementEffectTestsEnteringTable.CavernName));
        Assert.True(table.State.Objects.ContainsKey(pending.Context.Source!.Value));
        Assert.Equal(land, pending.Context.Source);
        Assert.Equal(Zone.Battlefield, table.State.Objects[land].Zone.Zone);
    }

    /// <summary>
    /// CR 101.4 with 400.7: the index the question carries is the replacement ability's own position
    /// on the face of the object that is entering — an object that exists only in the probe state
    /// (CR 614.12), which is why reading it out of the state the move has not reached left every such
    /// question with no index and no way back to the effect that asked.
    /// </summary>
    [Fact]
    [RuleRef("101.4")]
    public void The_question_a_land_asks_as_it_enters_carries_its_replacement_abilitys_index()
    {
        var table = ReplacementEffectTestsEnteringTable.WithTheLandPlayed();
        var pending = table.State.Pending;
        Assert.NotNull(pending);

        Assert.Equal(ReplacementEffectTestsEnteringTable.ReplacementIndex, pending.Context.AbilityIndex);

        var land = Assert.Single(table.Battlefield(Seat, ReplacementEffectTestsEnteringTable.CavernName));
        var abilities = table.State.Objects[land].Face.AbilityList;
        Assert.IsType<ReplacementAbility>(abilities[pending.Context.AbilityIndex!.Value]);
    }

    /// <summary>
    /// The same two rules at the unit level, where the index handed to the walk can be read directly:
    /// a <see cref="ChooseValue"/> With leaves the <see cref="ObjectMoved"/> alone (CR 614.1c) and the
    /// index is the ability's position on the entering object's face (CR 101.4 with 614.12).
    /// </summary>
    [Fact]
    [RuleRef("614.1c")]
    public void A_choose_only_replacement_keeps_the_entering_event_and_names_its_own_ability()
    {
        var board = new Board();
        var card = board.Put(
            Board.Definition(
                "quiet well",
                CardType.Land,
                abilities:
                [
                    new ActivatedAbility([new TapSelf()], new AddMana(new ManaProduction(Fixed: "{C}"))),
                    new ReplacementAbility(
                        new WouldEnterBattlefield(new ObjectFilter(Also: new IsThisObject())),
                        new ChooseValue(ChooseWhat.CreatureType, "k1"),
                        SelfOnly: true),
                ]),
            Zone.Hand);
        var walk = new ReplacementEffectFakeInterpreter();

        var outcome = System(walk).Replace(board.State, Entering(board.State, card), new PlayerId(0));

        Assert.IsType<ChooseValue>(Assert.Single(walk.Ran));
        Assert.Equal(1, Assert.Single(walk.Indexes));
        var moved = Assert.IsType<ObjectMoved>(Assert.Single(outcome.Events));
        Assert.Equal(Zone.Battlefield, outcome.State.Objects[moved.To.Id].Zone.Zone);
    }

    /// <summary>
    /// CR 616.1: a choose-only replacement modifies nothing, so the event goes on to the effects that
    /// have not applied yet — "enters tapped" still applies to the same arrival.
    /// </summary>
    [Fact]
    [RuleRef("616.1")]
    public void A_choose_only_replacement_leaves_the_event_for_the_other_effects()
    {
        var board = new Board();
        var card = board.Put(
            Board.Definition(
                "shallow well",
                CardType.Land,
                abilities:
                [
                    new ReplacementAbility(
                        new WouldEnterBattlefield(new ObjectFilter(Also: new IsThisObject())),
                        new ChooseValue(ChooseWhat.CreatureType, "k1"),
                        SelfOnly: true),
                ]),
            Zone.Hand);
        board.Put(WithReplacement("wet ground", CardType.Enchantment, new WouldEnterBattlefield(Land), new EntersTapped()));

        var outcome = System(new ReplacementEffectFakeInterpreter()).Replace(board.State, Entering(board.State, card), new PlayerId(0));

        var moved = Assert.IsType<ObjectMoved>(Assert.Single(outcome.Events));
        Assert.True(moved.To.Tapped);
    }
}
