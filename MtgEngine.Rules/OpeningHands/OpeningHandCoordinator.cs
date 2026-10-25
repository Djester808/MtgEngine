using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.OpeningHands;

/// <summary>Owns opening-hand rounds; delegates card movement to the zone system.</summary>
public sealed class OpeningHandCoordinator(IZoneSystem zones)
{
    public Outcome Start(GameState state)
    {
        var seats = Enumerable.Range(0, state.Players.Count)
            .Select(offset => new PlayerId((state.Turn.ActivePlayer.Value + offset) % state.Players.Count))
            .Where(player => !state.Player(player).HasLost && state.In(ZoneKey.Of(Zone.Hand, player)).Count > 0)
            .Select(player => new OpeningHandSeat(player, state.In(ZoneKey.Of(Zone.Hand, player)).Count, InitialMulliganCount(state)))
            .ToImmutableList();
        if (seats.IsEmpty)
            return Outcome.Unchanged(state);
        return OpeningState(state, new OpeningHandsState(seats)).Then(Advance);
    }

    public Outcome Advance(GameState state)
    {
        if (state.Pending is not null || state.OpeningHands is not { } round)
            return Outcome.Unchanged(state);
        if (!round.Redrawn)
        {
            var undecided = round.Seats.FirstOrDefault(seat => !seat.Kept && seat.Redraw is null);
            if (undecided is not null)
                return OpeningHandPrompts.Ask(state, OpeningHandPrompts.Declaration(state, undecided.Player,
                    undecided.Mulligans, undecided.HandSize));

            var result = Outcome.Unchanged(state);
            var seats = round.Seats;
            foreach (var seat in round.Seats.Where(seat => seat.Redraw == true))
            {
                var player = seat.Player;
                result = result.Then(s => Redraw(s, player, seat.HandSize));
                var count = seat.Mulligans + 1;
                seats = seats.Replace(seat, seat with
                {
                    Mulligans = count,
                    NeedsBottom = count > 0,
                    Kept = count >= seat.HandSize,
                });
            }
            return result.Then(s => OpeningState(s, new OpeningHandsState(seats, Redrawn: true)))
                .Then(Advance);
        }

        var bottom = round.Seats.FirstOrDefault(seat => seat.NeedsBottom);
        if (bottom is not null)
            return OpeningHandPrompts.AskToBottom(state, bottom.Player, bottom.Mulligans, bottom.HandSize);
        if (round.Seats.All(seat => seat.Kept))
            return OpeningState(state, null);
        var next = round with { Redrawn = false, Seats = [.. round.Seats.Select(seat => seat with { Redraw = null })] };
        return OpeningState(state, next).Then(Advance);
    }

    private static int InitialMulliganCount(GameState state) =>
        state.Players.Count > 2 || string.Equals(state.Format, "brawl", StringComparison.OrdinalIgnoreCase) ? -1 : 0;

    private static Outcome OpeningState(GameState state, OpeningHandsState? round)
    {
        var changed = new OpeningHandsChanged(round);
        return new Outcome(changed.Apply(state), [changed]);
    }

    /// <summary>Records a declaration; round advancement owns all redraws.</summary>
    [RuleRef("103.5")]
    public Outcome Decide(GameState state, PendingChoice choice, ImmutableList<ChoiceOption> chosen)
    {
        var round = state.OpeningHands
            ?? throw new RefusedException("No opening-hand round is active.", "103.5");
        var seat = round.Seats.SingleOrDefault(s => s.Player == choice.Player)
            ?? throw new RefusedException("This player is not in the opening-hand round.", "103.5");
        if (round.Redrawn || seat.Kept || seat.Redraw is not null)
            throw new RefusedException("This opening-hand declaration is no longer available.", "103.5");
        var redraw = chosen.Any(c => string.Equals(c.Id, ChoiceKinds.Mulligan, StringComparison.Ordinal));
        return OpeningState(state, round with
        {
            Seats = round.Seats.Replace(seat, seat with { Redraw = redraw, Kept = !redraw }),
        });
    }

    /// <summary>
    /// CR 103.5: the kept cards that go on the bottom of the library, in the order answered —
    /// the rule says any order, and the answer is the order the player chose.
    /// </summary>
    [RuleRef("103.5")]
    public Outcome Bottom(GameState state, PendingChoice choice, ImmutableList<ChoiceOption> chosen)
    {
        var moves = ImmutableList.CreateBuilder<MoveRequest>();
        foreach (var option in chosen)
        {
            if (option.Object is not { } id || !state.Objects.TryGetValue(id, out var obj)
                || obj.Zone != ZoneKey.Of(Zone.Hand, choice.Player))
                throw new RefusedException("Choose a card in your hand.", "103.5");
            moves.Add(new MoveRequest(id, ZoneKey.Of(Zone.Library, obj.Owner), Placement.Bottom));
        }
        return zones.MoveAll(state, moves.ToImmutable()).Then(s => CompleteOpeningBottom(s, choice.Player));
    }

    private static Outcome CompleteOpeningBottom(GameState state, PlayerId player)
    {
        if (state.OpeningHands is not { } round)
            return Outcome.Unchanged(state);
        var seat = round.Seats.Single(s => s.Player == player);
        var changed = new OpeningHandsChanged(round with
        {
            Seats = round.Seats.Replace(seat, seat with { NeedsBottom = false }),
        });
        return new Outcome(changed.Apply(state), [changed]);
    }

    private Outcome Redraw(GameState state, PlayerId player, int handSize) =>
        zones.MoveAll(state, [.. Hand(state, player).Select(id => new MoveRequest(id, ZoneKey.Of(Zone.Library, player)))])
            .Then(s => zones.Shuffle(s, player))
            .Then(s => zones.Draw(s, player, handSize));

    private static ImmutableList<ObjectId> Hand(GameState state, PlayerId player) =>
        state.In(ZoneKey.Of(Zone.Hand, player));

}
