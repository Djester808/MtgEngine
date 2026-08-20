namespace MtgEngine.Rules.State;

/// <summary>
/// The zones of CR 400.1: library, hand, battlefield, graveyard, stack, exile, and command.
/// </summary>
/// <remarks>
/// Ante is deliberately absent. It exists only for a handful of pre-Sixth-Edition cards that
/// no supported format is legal for, and modelling it would mean modelling wagering.
/// </remarks>
public enum Zone
{
    Library,
    Hand,
    Battlefield,
    Graveyard,
    Stack,
    Exile,
    Command,
}

/// <summary>Facts about zones that the rules state, so no caller has to restate them.</summary>
public static class Zones
{
    /// <summary>
    /// Library, hand, and graveyard belong to a player; battlefield, stack, exile, and command
    /// are shared by everyone (CR 400.1).
    /// </summary>
    public static bool IsPerPlayer(this Zone zone) =>
        zone is Zone.Library or Zone.Hand or Zone.Graveyard;

    /// <summary>
    /// Hidden zones are those in which not all players can be expected to see the cards' faces:
    /// library and hand, and only those (CR 400.2).
    /// </summary>
    /// <remarks>
    /// This is the whole basis of <see cref="Views.PlayerViewProjector"/>. The previous engine
    /// broadcast one state to every player in the game's SignalR group, which handed each of
    /// them the other's hand and both libraries. Asking the question here, once, is what stops
    /// that being re-decided per call site.
    /// </remarks>
    public static bool IsHidden(this Zone zone) => zone is Zone.Library or Zone.Hand;

    /// <summary>
    /// Whether the order of objects in this zone is part of the game state and may not be
    /// rearranged at will (CR 400.5): library, graveyard, and stack.
    /// </summary>
    public static bool IsOrdered(this Zone zone) =>
        zone is Zone.Library or Zone.Graveyard or Zone.Stack;
}
