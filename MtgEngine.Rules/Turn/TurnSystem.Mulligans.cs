using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Turn;

/// <summary>
/// What happens before turn 1: the mulligan declarations of CR 103.4–103.5, and the beginning of
/// the first turn once every player has kept.
/// <para>
/// The loop is split between two subsystems and neither owns all of it. The turn system knows
/// the turn order and when turn 1 may begin; the choice system knows how a declaration is
/// answered — it shuffles, redraws, asks again after a mulligan, and asks which cards go on the
/// bottom after a keep. So the turn system raises the first declaration
/// (<see cref="TurnSystem.OpenHands"/>) and is called back once a player's declaration is
/// finished (<see cref="TurnSystem.AfterMulligans"/>) to ask the next player, or to begin.
/// </para>
/// <para>
/// CR 103.5 has every player declare and then all mulligans happen at once. This resolves one
/// player at a time, which is what the choice system implements and what a single pending
/// question can express; the difference shows only in the order of the events, never in the
/// hands. The simultaneity is filed as a seam request rather than faked here.
/// </para>
/// </summary>
[RuleRef("103.5")]
public sealed partial class TurnSystem
{
    /// <summary>
    /// CR 103.4–103.5: before the first turn, the starting player declares whether they will
    /// take a mulligan, then each other player in turn order. This raises the first declaration;
    /// <see cref="Begin"/> is not called until the last player has kept, because CR 103.5 puts
    /// the whole process before turn 1.
    /// </summary>
    [RuleRef("103.5")]
    public Outcome OpenHands(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (Turn(state) > 0)
            return Outcome.Unchanged(state);

        // Nobody to ask — a scenario that dealt fixed hands, or none at all. Unchanged, so that
        // Game.Create begins turn 1 itself rather than this beginning it twice.
        return NextToDeclare(state, Starting(state)) is { } who ? Declare(state, who) : Outcome.Unchanged(state);
    }

    /// <summary>
    /// One player's declaration is finished — they kept, and any cards a mulligan cost them are
    /// on the bottom of their library. The next player in turn order declares, and when the turn
    /// order has come back round to the starting player, every player has kept and turn 1 begins,
    /// CR 103.5 and CR 103.8.
    /// </summary>
    [RuleRef("103.5")]
    public Outcome AfterMulligans(GameState state, PlayerId finished)
    {
        ArgumentNullException.ThrowIfNull(state);

        // Turn 1 has already begun: a later mulligan-shaped question is not this loop's.
        if (Turn(state) > 0 || state.Outcome is not null)
            return Outcome.Unchanged(state);

        var next = NextInTurnOrder(state, finished);
        if (next == Starting(state))
            return Begin(state);

        return NextToDeclare(state, next) is { } who ? Declare(state, who) : Begin(state);
    }

    /// <summary>
    /// The first player from here onwards, in turn order, who has an opening hand to declare
    /// over; null once the turn order has come back round to the starting player, which is
    /// CR 103.5's "once each player has made a declaration".
    /// </summary>
    [RuleRef("103.5")]
    private static PlayerId? NextToDeclare(GameState state, PlayerId from)
    {
        var starting = Starting(state);
        var player = from;
        for (var i = 0; i < state.Players.Count; i++)
        {
            if (HandSize(state, player) > 0)
                return player;

            var next = NextInTurnOrder(state, player);
            if (next == starting)
                return null;
            player = next;
        }

        return null;
    }

    /// <summary>
    /// CR 103.5c: in a game with more than two players, and in Brawl, the first mulligan a player
    /// takes costs them no card. The count the declaration carries is the number of cards the
    /// player will put on the bottom when they keep, so the exemption is that count starting one
    /// lower: the first mulligan brings it to zero rather than to one.
    /// </summary>
    [RuleRef("103.5c")]
    internal static int FirstCount(GameState state) =>
        state.Players.Count > 2 || string.Equals(state.Format, "brawl", StringComparison.OrdinalIgnoreCase) ? -1 : 0;

    /// <summary>
    /// The opening hand size, CR 103.4. Every player draws the same number, so the hand a player
    /// holds before they have declared anything is it.
    /// </summary>
    [RuleRef("103.4")]
    private static int HandSize(GameState state, PlayerId player) =>
        state.In(ZoneKey.Of(Zone.Hand, player)).Count;

    /// <summary>The player who takes turn 1, CR 103.1 and CR 103.8; the state carries the answer.</summary>
    [RuleRef("103.8")]
    private static PlayerId Starting(GameState state) => state.Turn.ActivePlayer;

    private static int Turn(GameState state) => state.Turn.Number;

    /// <summary>
    /// Ask this player to keep or mulligan, CR 103.5. The question is built by the choice system
    /// that answers it, so the kind, the option ids and the context keys have one spelling.
    /// </summary>
    [RuleRef("103.5")]
    private static Outcome Declare(GameState state, PlayerId player)
    {
        var requested = new ChoiceRequested(ChoiceSystem.Declaration(state, player, FirstCount(state), HandSize(state, player)));
        return new Outcome(requested.Apply(state), [requested]);
    }
}
