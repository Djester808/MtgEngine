namespace MtgEngine.Rules.State;

/// <summary>A saved game contains an operation that this engine can no longer replay.</summary>
public sealed class UnsupportedGameLogException() : Exception(Detail)
{
    public const string Detail = "This saved game uses removed payment recovery. Start a new game.";
}
