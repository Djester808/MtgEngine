namespace MtgEngine.Rules.State;

/// <summary>CR 103.5: declarations are collected before any replacement hands are dealt.</summary>
public sealed record OpeningHandsState(ImmutableList<OpeningHandSeat> Seats, bool Redrawn = false);

public sealed record OpeningHandSeat(PlayerId Player, int HandSize, int Mulligans,
    bool Kept = false, bool? Redraw = null, bool NeedsBottom = false);
