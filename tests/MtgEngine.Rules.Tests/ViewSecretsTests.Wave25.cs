using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Interpreter;
using MtgEngine.Rules.Layers;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Views;

namespace MtgEngine.Rules.Tests;

/// <summary>CR 101.4b: a secret answer another player already gave never reaches a view, not even the asked player's own question (E4h ask 1).</summary>
public sealed class ViewSecretsTests
{
    [Fact]
    [RuleRef("101.4")]
    public void The_asked_players_own_question_is_shown_without_the_secrets_it_carries()
    {
        var state = GameReducer.Replay([new GameStarted([new PlayerStart("A", [], []), new PlayerStart("B", [], [])], Seed: 42, StartingLife: 20, Format: "test")]);
        var asked = new PlayerId(1);
        var extra = ImmutableDictionary<string, string>.Empty
            .SetItem("storeAs", EffectInterpreter.SecretNumberKey("wheel", asked))
            .SetItem("answered:" + EffectInterpreter.SecretNumberKey("wheel", new PlayerId(0)), "6");
        var question = new PendingChoice(new ChoiceId(state.NextChoiceId), asked, ChoiceKinds.Value, "Choose a number, in secret.", [], 1, 1, new ChoiceContext(null, null, null, extra));
        state = state with { Pending = question };

        var shown = new ViewProjector(new LayerSystem(), _ => true).Project(state, asked).Pending;

        // The question is still theirs to answer — same id, same bounds — and the first seat's number is not on it.
        Assert.NotNull(shown);
        Assert.Equal(question.Id, shown.Id);
        Assert.DoesNotContain(shown.Context.Extra.Keys, ChoiceSecrets.IsSecret);
        Assert.True(shown.Context.Extra.ContainsKey("storeAs"));
    }
}
