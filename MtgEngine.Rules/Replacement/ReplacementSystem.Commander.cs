using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Replacement;

public sealed partial class ReplacementSystem
{
    private const string CommanderReplacementKey = "commander-zone-replacement";

    [RuleRef("903.9b")]
    private static Candidate? CommanderCandidate(GameState state, GameEvent proposed, HashSet<string> used)
    {
        if (used.Contains(CommanderReplacementKey) || proposed is not ObjectMoved move
            || move.To.Zone.Zone is not (Zone.Hand or Zone.Library)
            || !state.Objects.TryGetValue(move.From, out var card) || !card.IsCommander)
            return null;
        return new Candidate(CommanderReplacementKey, "Commander return to command zone", card.Id, card.Owner, false, null, null);
    }

    [RuleRef("903.9b")]
    private static Applied AskCommander(GameState state, GameEvent current, HashSet<string> used)
    {
        var move = (ObjectMoved)current;
        var card = state.Objects[move.From];
        var context = new ChoiceContext(card.Id, null, "replacement",
            ImmutableDictionary<string, string>.Empty.Add(CommanderReplacementKey, "true")
                .Add(UsedKey, string.Join(",", used)), current);
        var ask = new ChoiceRequested(new PendingChoice(new ChoiceId(state.NextChoiceId), card.Owner,
            ChoiceKinds.ReplacementOrder, $"Return {card.Face.Name} to the command zone instead?",
            [new ChoiceOption("return", "Return to command zone"), new ChoiceOption("keep", $"Move to {move.To.Zone.Zone}")],
            1, 1, context));
        return new Applied(null, ask.Apply(state), [ask]);
    }

    [RuleRef("903.9b")]
    private Outcome ResumeCommander(GameState state, GameEvent proposed, Resumption answer, HashSet<string> used)
    {
        var move = (ObjectMoved)proposed;
        if (answer.Chosen[0].Id == "return")
        {
            move = move with { To = move.To with { Zone = ZoneSystem.KeyFor(Zone.Command, move.To.Owner) }, Position = -1 };
            // CR 903.9b permits this replacement again if another effect changes the destination back.
            used.Remove(CommanderReplacementKey);
        }

        return Continue(state, move, move.To.Owner, used, []);
    }
}
