using System.ComponentModel.DataAnnotations;
using MtgEngine.Cards;

namespace MtgEngine.Api.Dtos;

/// <summary>One scenario a card carries, and whether it passed the last verification run.</summary>
public sealed record ScenarioSummaryDto(string Name, string Source, bool? Passed);

/// <summary>The last time the runner played this card's scenarios, and what came of it.</summary>
public sealed record VerificationDto(string EngineHash, string RunId, int Passed, int Total, DateTimeOffset At);

/// <summary>How far along one card is: the status the deck gate reads, and the evidence behind it.</summary>
public sealed record CardImplementationDto(
    string OracleId,
    string Name,
    ImplementationStatus Status,
    string? ExclusionReason,
    string Provenance,
    IReadOnlyList<ScenarioSummaryDto> Scenarios,
    VerificationDto? LastVerification);

/// <summary>A primitive the translation stage could not express, and how many cards are waiting on it.</summary>
public sealed record PrimitiveBacklogDto(string Reason, int CardsBlocked);

/// <summary>One model's reading of a disputed card.</summary>
public sealed record ReadingDto(string Model, string DefinitionJson);

/// <summary>A card whose two readings disagreed, with the first place they differ.</summary>
public sealed record DisputedCardDto(string OracleId, string Name, IReadOnlyList<ReadingDto> Readings, string? FirstDifference);

/// <summary>The coverage the ledger holds: counts by status, what is blocking the rest, and the last run.</summary>
public sealed record CardCoverageDto(
    int Total,
    IReadOnlyDictionary<string, int> ByStatus,
    IReadOnlyList<PrimitiveBacklogDto> PrimitiveBacklog,
    VerificationDto? LastVerification,
    IReadOnlyList<DisputedCardDto>? Disputed = null);

/// <summary>
/// How far along a whole deck is, in one request. The client draws a badge per card and a deck is
/// a hundred of them; a hundred round trips to draw one list is not a deck view.
/// </summary>
public sealed record CardImplementationsRequest(
    [Required]
    [MinLength(1, ErrorMessage = "Ask about at least one card.")]
    [MaxLength(CardImplementationsRequest.MaxIds, ErrorMessage = "Ask about at most 500 cards at a time.")]
    IReadOnlyList<string> OracleIds)
{
    /// <summary>Two commander decks' worth. The cap is the server's, not the caller's.</summary>
    public const int MaxIds = 500;
}

/// <summary>Choosing one model's reading as the card's definition.</summary>
public sealed record ChooseReadingRequest([Required][StringLength(120, MinimumLength = 1)] string Model);
