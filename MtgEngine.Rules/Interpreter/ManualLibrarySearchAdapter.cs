using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Library;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Interpreter;

/// <summary>Connects deck search controls to the existing rules prompt and replay machinery.</summary>
public sealed class ManualLibrarySearchAdapter(IInterpreter interpreter) : ILibrarySearchInteraction
{
    public Outcome Begin(GameState state, ManualLibrarySearch command)
    {
        var search = new Cards.Search(command.Filter ?? Cards.ObjectFilter.Any, command.Destination,
            UpTo: new Cards.Fixed(command.Count), Where: command.Where, Reveal: command.Reveal,
            Tapped: command.Tapped, ShuffleAfter: command.ShuffleAfter);
        var context = new EffectContext(null, command.Player, [], [], null,
            ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty,
            ImmutableDictionary<string, string>.Empty, ImmutableDictionary<string, int>.Empty, [], null);
        return interpreter.Run(state, search, context);
    }
}
