using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Choices;

public sealed partial class ChoiceSystem
{
    [RuleRef("903.9a")]
    private Outcome ReturnCommander(GameState state, PendingChoice choice, ImmutableList<ChoiceOption> chosen)
    {
        if (chosen.IsEmpty || choice.Context.Source is not { } id)
            return Outcome.Unchanged(state);
        if (!state.Objects.TryGetValue(id, out var card) || !card.IsCommander
            || card.Owner != choice.Player || card.Zone.Zone is not (Zone.Graveyard or Zone.Exile))
            throw new RefusedException("That commander is no longer available to return.", "903.9a");
        return zones.Move(state, id, ZoneKey.Of(Zone.Command, card.Owner));
    }
}
