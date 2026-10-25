using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Query;

public sealed partial class Query
{
    /// <summary>CR 903.8: read persistent casts for the selected commander, including a departed source.</summary>
    [RuleRef("903.8")]
    private int CommanderCastCount(GameState state, CommanderCastsThisGame amount, EffectContext context)
    {
        var ids = amount.Which is null or Self
            ? context.Source is { } source ? ImmutableList.Create(source) : []
            : Resolve(state, amount.Which, context);
        var identities = new HashSet<CommanderIdentity>();
        foreach (var id in ids)
        {
            var card = LastKnown(state, id);
            if (card is { IsCommander: true })
                identities.Add(State.CommanderIdentity.Of(card));
        }

        return identities.Sum(identity => state.CommanderCasts.GetValueOrDefault((identity, context.Controller)));
    }
}
