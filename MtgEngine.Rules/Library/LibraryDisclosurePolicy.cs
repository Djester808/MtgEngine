using MtgEngine.Cards;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Library;

/// <summary>Continuous top-card permissions cannot disclose a new top during activation payment.</summary>
[RuleRef("401.5")]
public static class LibraryDisclosurePolicy
{
    public static bool TopAvailable(GameState state, PlayerId owner)
    {
        if (state.Pending?.Context.RemainingPayment?.Activation is not { } activation)
            return true;
        var library = state.In(ZoneKey.Of(Zone.Library, owner));
        return !library.IsEmpty && activation.InitialLibraryTops.TryGetValue(owner, out var original)
            && library[0] == original;
    }
}
