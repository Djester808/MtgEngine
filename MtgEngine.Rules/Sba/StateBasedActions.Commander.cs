using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Sba;

public sealed partial class StateBasedActions
{
    [RuleRef("903.9a")]
    private static ChoiceRequested? CommanderReturn(GameState state)
    {
        var card = state.CommanderReturnCandidates
            .Where(state.Objects.ContainsKey).Select(id => state.Objects[id])
            .Where(card => card.IsCommander && card.Zone.Zone is Zone.Graveyard or Zone.Exile
                && !state.Player(card.Owner).HasLost)
            .OrderBy(card => (card.Owner.Value - state.Turn.ActivePlayer.Value + state.Players.Count) % state.Players.Count)
            .ThenBy(card => card.Id.Value).FirstOrDefault();
        return card is null ? null : new ChoiceRequested(new PendingChoice(
            new ChoiceId(state.NextChoiceId), card.Owner, ChoiceKinds.CommanderReturn,
            $"Return {card.Face.Name} to the command zone?",
            [new ChoiceOption("return", "Return to command zone", card.Id)], 0, 1,
            new ChoiceContext(card.Id, null, null, ImmutableDictionary<string, string>.Empty)));
    }
}
