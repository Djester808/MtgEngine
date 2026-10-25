namespace MtgEngine.Rules.State;

/// <summary>One copy selected from an existing deck entry; catalog identity and presentation, not rules or game state.</summary>
public sealed record StartingCard(string OracleId, string? PrintingId = null, CardFinish Finish = CardFinish.Nonfoil)
{
    /// <summary>Checks the selection's shape; catalog lookup owns printing membership and availability.</summary>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(OracleId);
        if (PrintingId is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(PrintingId);
        if (!Enum.IsDefined(Finish))
            throw new ArgumentOutOfRangeException(nameof(Finish));
    }

    /// <summary>Rules scenarios can select an Oracle card without specifying a printing.</summary>
    public static implicit operator StartingCard(string oracleId) => new(oracleId);
}

public enum CardFinish { Nonfoil, Foil, Etched }
