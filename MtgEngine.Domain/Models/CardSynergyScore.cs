namespace MtgEngine.Domain.Models;

/// <summary>
/// Cached LLM-generated synergy score between a commander and a candidate card.
/// Keyed on (CommanderOracleId, CardOracleId) so a score is computed once and reused.
/// </summary>
public sealed class CardSynergyScore
{
    public Guid   Id                 { get; set; } = Guid.NewGuid();
    public string CommanderOracleId  { get; set; } = string.Empty;
    public string CardOracleId       { get; set; } = string.Empty;
    public int    Score              { get; set; }
    public string Reason             { get; set; } = string.Empty;
    public string ModelVersion       { get; set; } = string.Empty;
    public DateTime CreatedAt        { get; set; } = DateTime.UtcNow;
}
