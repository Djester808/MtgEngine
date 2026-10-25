using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Library;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Interpreter;

public sealed partial class EffectInterpreter
{
    private readonly LibraryRevealPlanner revealPlanner = new(query);

    private EffectRun RevealingUntil(EffectRun run, RevealUntil reveal)
    {
        var maximum = reveal.MaxCards is null ? (int?)null : Number(run, reveal.MaxCards);
        var plans = InTurnOrder(run.State, Players(run, reveal.Who))
            .Select(player => revealPlanner.Plan(run.State, player, reveal.Until, maximum, run.Context)).ToList();
        // One public snapshot retains every affected library's revealed prefix. Later
        // movement instructions consume the remembered groups through the normal zone path.
        run = RevealCards(run, plans.SelectMany(plan => plan.Revealed), FirstPlayer(run, reveal.Who));
        run = Recall(run, reveal.RememberHitAs, plans.Where(plan => plan.Hit is not null).Select(plan => plan.Hit!.Value));
        return Recall(run, reveal.RememberRestAs, plans.SelectMany(plan => plan.Rest));
    }

    private static EffectRun RevealCards(EffectRun run, IEnumerable<ObjectId> ids, PlayerId by)
    {
        var revealed = ids.ToImmutableList();
        var cards = DisclosedCards(run, revealed);
        run = InspectDisclosedLibraries(run, revealed, run.State.Players.Select(player => player.Id), publicReveal: true);
        return cards.IsEmpty ? run : Emit(run, new CardsRevealed(new CardReveal(by, cards))
        {
            FromLibrary = revealed.Any(id => run.State.Objects.TryGetValue(id, out var card) && card.Zone.Zone == Zone.Library),
        });
    }

    private static ImmutableList<RevealedCard> DisclosedCards(EffectRun run, IEnumerable<ObjectId> ids) =>
        ids.Where(run.State.Objects.ContainsKey).Select(id => run.State.Objects[id])
            .Select(card => new RevealedCard(card.Definition.OracleId, card.Face.Name,
                card.Face.ManaCost, card.Face.OracleText, card.DeckSelection?.PrintingId)).ToImmutableList();
}
