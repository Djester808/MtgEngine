using MtgEngine.Cards;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Replacement;

public sealed partial class ReplacementSystem
{
    private string Label(GameState state, GameObject source, ReplacementAbility ability, int index, PlayerId controller)
    {
        if (!string.IsNullOrWhiteSpace(ability.Text))
            return ability.Text;
        var action = ability.Instead switch
        {
            WouldDraw => "Replace a card draw",
            WouldMill => "Replace milling cards",
            WouldShuffle => "Replace shuffling the library",
            WouldBePutIntoZone move => $"Replace a move to {ZoneLabel(move.To)}",
            _ => "Apply replacement effect",
        };
        // Use the real current state, not the hypothetical entry probe: an unrevealed
        // card about to enter has not become public merely because matching needs a probe.
        if (!state.Objects.TryGetValue(source.Id, out var current) || current.FaceDown
            || current.Zone.Zone is not (Zone.Battlefield or Zone.Graveyard or Zone.Exile or Zone.Command or Zone.Stack))
            return action;
        var name = _layers.Of(state, source.Id).Name;
        return $"{state.Player(controller).Name}: {name} — {action} (ability {index + 1})";
    }

    private static string ZoneLabel(Zone zone) => zone switch
    {
        Zone.Library => "the library",
        Zone.Graveyard => "the graveyard",
        Zone.Exile => "exile",
        Zone.Hand => "hand",
        Zone.Battlefield => "the battlefield",
        Zone.Command => "the command zone",
        Zone.Stack => "the stack",
        _ => "another zone",
    };
}
