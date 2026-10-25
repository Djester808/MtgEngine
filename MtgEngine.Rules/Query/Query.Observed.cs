using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Query;

public sealed partial class Query
{
    /// <summary>Use one observed appearance throughout the existing filter and nested predicate readers.</summary>
    public bool MatchesObserved(GameState state, ObjectFilter filter, GameObject card, Characteristics appearance, EffectContext context)
    {
        var observed = state with { Objects = state.Objects.SetItem(card.Id, card) };
        return new Query(new ObservedCharacteristics(_layers, card.Id, appearance))
            .Matches(observed, filter, card.Id, context);
    }

    private sealed class ObservedCharacteristics(ICharacteristics current, ObjectId card, Characteristics appearance) : ICharacteristics
    {
        public string Name => "observed card characteristics";
        public Characteristics Of(GameState state, ObjectId id) => id == card ? appearance : current.Of(state, id);
        public PlayerId ControllerOf(GameState state, ObjectId id) => id == card ? appearance.Controller : current.ControllerOf(state, id);
        public Characteristics PrintedOf(GameObject obj) => current.PrintedOf(obj);
        public PlayerLimits PlayerRestrictions(GameState state, PlayerId player) => current.PlayerRestrictions(state, player);
    }
}
