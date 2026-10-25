using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Layers;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Views;

namespace MtgEngine.Rules.Tests;

public sealed class ChoiceProposalPrivacyTests
{
    [Fact]
    public void Zone_batch_control_stays_server_side_while_public_choice_location_is_preserved()
    {
        var context = new ChoiceContext(new ObjectId(7), 2, "0.1", ImmutableDictionary<string, string>.Empty.Add("minimum", "1"))
        { AdvanceAfterZoneBatch = true };
        var choice = new PendingChoice(new ChoiceId(1), new PlayerId(0), ChoiceKinds.ReplacementOrder,
            "Choose replacement", [new ChoiceOption("effect", "Apply effect")], 1, 1, context);
        var shown = ChoiceSecrets.Withheld(choice)!;
        Assert.False(shown.Context.AdvanceAfterZoneBatch);
        Assert.True(choice.Context.AdvanceAfterZoneBatch);
        Assert.Equal(context.Source, shown.Context.Source);
        Assert.Equal(context.AbilityIndex, shown.Context.AbilityIndex);
        Assert.Equal(context.EffectPath, shown.Context.EffectPath);
        Assert.Equal(context.Extra, shown.Context.Extra);
        Assert.Equal(choice.Options, shown.Options);
        Assert.Same(shown, ChoiceSecrets.Withheld(shown));
    }

    [Fact]
    public void Off_stack_effect_only_context_is_private_but_survives_server_replay()
    {
        var effect = new Unsupported("private-off-stack-effect-tree");
        var choice = new PendingChoice(new ChoiceId(1), new PlayerId(0), ChoiceKinds.ReplacementOrder,
            "Choose replacement", [new ChoiceOption("effect", "Apply effect")], 1, 1,
            new ChoiceContext(null, null, null, ImmutableDictionary<string, string>.Empty) { OffStackEffect = effect });
        var state = GameReducer.Replay(EventLogSerializer.Read(EventLogSerializer.Write(
            [new GameStarted([new PlayerStart("A", [], []), new PlayerStart("B", [], [])], 1, 40, "commander"),
                new ChoiceRequested(choice)])));
        Assert.Equal(effect, state.Pending!.Context.OffStackEffect);
        var shown = ChoiceSecrets.Withheld(state.Pending)!;
        Assert.Null(shown.Context.OffStackEffect);
        Assert.Equal(choice.Options, shown.Options);
        Assert.Same(shown, ChoiceSecrets.Withheld(shown));
        var projector = new ViewProjector(new LayerSystem(), _ => true);
        foreach (var viewer in new[] { new PlayerId(0), new PlayerId(1) })
        {
            var projected = projector.Project(state, viewer);
            Assert.Null(projected.Pending!.Context.OffStackEffect);
            Assert.DoesNotContain("private-off-stack-effect-tree",
                System.Text.Json.JsonSerializer.Serialize(projected, EventLogSerializer.Options));
        }
        Assert.Equal(effect, state.Pending.Context.OffStackEffect);
    }

    [Fact]
    public void Payment_only_context_is_redacted_without_changing_server_progress()
    {
        var payment = new MtgEngine.Rules.Casting.RemainingCostPayment([new MillCost(new Fixed(2))], [new ObjectId(73)]);
        var choice = new PendingChoice(new ChoiceId(1), new PlayerId(0), ChoiceKinds.ReplacementOrder,
            "Choose replacement", [new ChoiceOption("effect", "Apply effect")], 1, 1,
            new ChoiceContext(null, null, null, ImmutableDictionary<string, string>.Empty) { RemainingPayment = payment });
        var shown = ChoiceSecrets.Withheld(choice)!;
        Assert.Null(shown.Context.RemainingPayment);
        Assert.Same(payment, choice.Context.RemainingPayment);
        Assert.Equal(choice.Options, shown.Options);
        Assert.Same(shown, ChoiceSecrets.Withheld(shown));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void Even_the_choice_owner_does_not_receive_the_internal_move_or_hidden_card_definition(int continuationDepth)
    {
        var owner = new PlayerId(0);
        var secret = new CardDefinition("hidden-oracle", "Hidden library card", Layout.Normal,
            [new Face("Hidden library card", FaceRole.Main, new TypeLine(CardType.Creature))]);
        var card = new GameObject(new ObjectId(1), secret, 0, new PlayerId(1), new PlayerId(1), ZoneKey.Of(Zone.Library, new PlayerId(1)), 1);
        var proposal = new ObjectMoved(card.Id, card with { Id = new ObjectId(2), Zone = ZoneKey.Shared(Zone.Exile) }, -1, Departed: card);
        var pending = new PendingChoice(new ChoiceId(1), owner, ChoiceKinds.ReplacementOrder,
            "Choose replacement", [new ChoiceOption("effect", "Apply effect")], 1, 1,
            new ChoiceContext(null, null, "replacement", ImmutableDictionary<string, string>.Empty.Add("minimum", "1"), proposal)
            { DeferredShuffles = [new PlayerId(1), owner, new PlayerId(1)] });
        for (var depth = 0; depth < continuationDepth; depth++)
        {
            var inner = pending.Context with
            {
                Extra = pending.Context.Extra.SetItem("private-continuation", "hidden-oracle"),
            };
            pending = pending with { Context = pending.Context with { ReplacementContinuation = inner } };
        }
        var state = GameReducer.Replay(
        [
            new GameStarted([new PlayerStart("A", [], []), new PlayerStart("B", [], [])], 1, 40, "commander"),
            new ObjectCreated(card, -1), new ChoiceRequested(pending),
        ]);
        var projector = new ViewProjector(new LayerSystem(), _ => true);
        var shown = Assert.IsType<PendingChoice>(projector.Project(state, owner).Pending);
        Assert.Null(shown.Context.Event);
        Assert.Null(shown.Context.ReplacementContinuation);
        Assert.Empty(shown.Context.DeferredShuffles);
        Assert.Equal("1", shown.Context.Extra["minimum"]);
        Assert.Equal(pending.Options, shown.Options);
        Assert.Same(proposal, state.Pending!.Context.Event);
        var other = Assert.IsType<PendingChoice>(projector.Project(state, new PlayerId(1)).Pending);
        Assert.Null(other.Context.Event);
        Assert.Null(other.Context.ReplacementContinuation);
        Assert.Empty(other.Context.DeferredShuffles);
        Assert.Empty(other.Options);
        var wire = System.Text.Json.JsonSerializer.Serialize(shown, EventLogSerializer.Options);
        Assert.DoesNotContain("hidden-oracle", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("Hidden library card", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("private-continuation", wire, StringComparison.Ordinal);
        var restored = GameReducer.Replay(EventLogSerializer.Read(EventLogSerializer.Write(
            [new GameStarted([new PlayerStart("A", [], []), new PlayerStart("B", [], [])], 1, 40, "commander"), new ChoiceRequested(pending)])));
        Assert.IsType<ObjectMoved>(restored.Pending!.Context.Event);
        var context = restored.Pending.Context;
        Assert.Equal(pending.Context.DeferredShuffles, context.DeferredShuffles);
        for (var depth = 0; depth < continuationDepth; depth++)
        {
            context = Assert.IsType<ChoiceContext>(context.ReplacementContinuation);
            Assert.Equal("hidden-oracle", context.Extra["private-continuation"]);
            Assert.IsType<ObjectMoved>(context.Event);
            Assert.Equal(pending.Context.DeferredShuffles, context.DeferredShuffles);
        }
        Assert.Null(context.ReplacementContinuation);
    }
}
