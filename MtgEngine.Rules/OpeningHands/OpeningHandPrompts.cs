using System.Globalization;
using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.OpeningHands;

/// <summary>Builds the private choices required by the opening-hand procedure.</summary>
public static class OpeningHandPrompts
{
    /// <summary>
    /// The declaration a player makes each round of mulligans, CR 103.5. Public because the
    /// turn system raises the first one when the game begins; every later one is raised here.
    /// </summary>
    public static PendingChoice Declaration(GameState state, PlayerId player, int taken, int handSize) =>
        new(
            new ChoiceId(state.NextChoiceId),
            player,
            ChoiceKinds.Mulligan,
            "Keep this hand, or take a mulligan?",
            [new ChoiceOption(ChoiceKinds.Keep, "Keep"), new ChoiceOption(ChoiceKinds.Mulligan, "Mulligan")],
            1,
            1,
            Context(taken, handSize));

    /// <summary>Where a mulligan declaration keeps its count and the starting hand size, CR 103.5.</summary>
    public static ChoiceContext Context(int taken, int handSize) =>
        new(
            null,
            null,
            ChoiceKinds.Mulligan,
            ImmutableDictionary<string, string>.Empty
                .Add("mulligans", taken.ToString(CultureInfo.InvariantCulture))
                .Add("handSize", handSize.ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// One card per mulligan taken goes on the bottom, CR 103.5. A player who kept their first
    /// hand puts none back, and there is nothing to ask.
    /// </summary>
    public static Outcome AskToBottom(GameState state, PlayerId player, int taken, int handSize)
    {
        var hand = state.In(ZoneKey.Of(Zone.Hand, player));
        var count = Math.Min(taken, hand.Count);
        if (count <= 0)
            return Outcome.Unchanged(state);

        var options = hand
            .Select(id => new ChoiceOption(id.Value.ToString(CultureInfo.InvariantCulture), state.Objects[id].Face.Name, id))
            .ToImmutableList();
        return Ask(state, new PendingChoice(
            new ChoiceId(state.NextChoiceId),
            player,
            ChoiceKinds.MulliganBottom,
            $"Put {count} card(s) on the bottom of your library.",
            options,
            count,
            count,
            Context(taken, handSize)));
    }

    public static Outcome Ask(GameState state, PendingChoice choice)
    {
        var requested = new ChoiceRequested(choice);
        return new Outcome(requested.Apply(state), [requested]);
    }

}
