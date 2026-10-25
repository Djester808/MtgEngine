using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Library;

/// <summary>Starts an authorized library selection; the implementation owns its prompt continuation.</summary>
public interface ILibrarySearchInteraction
{
    Outcome Begin(GameState state, ManualLibrarySearch command);
}
