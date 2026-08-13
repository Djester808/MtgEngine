using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

/// <summary>
/// The shared validation ladder for AI-proposed commander-deck cards: color identity
/// (colorless is never a violation), commander legality, and the bracket gate on game
/// changers.
/// </summary>
/// <remarks>
/// This ladder was previously typed inline in both <see cref="AiBuildService"/> paths
/// (build and refine) — two untested copies that could drift apart silently. Every AI
/// service that grounds model-proposed names against a commander deck validates here.
/// </remarks>
public static class CardGrounding
{
    /// <summary>Why a proposed card was not added; keys of the rejection counters.</summary>
    public static class Rejection
    {
        public const string UnknownCard = "unknown-card";
        public const string ColorIdentity = "color-identity";
        public const string NotCommanderLegal = "not-commander-legal";
        public const string AboveBracket = "game-changer-above-bracket";
        public const string Duplicate = "duplicate";
        public const string AddFailed = "add-failed";
    }

    /// <summary>
    /// Validates a resolved card against a commander deck's constraints. Returns null
    /// when the card passes, else the <see cref="Rejection"/> constant naming the first
    /// failed rule. An empty <paramref name="commanderColors"/> skips the identity check
    /// (no commander assigned yet).
    /// </summary>
    public static string? ValidateForCommanderDeck(
        CardDefinition def,
        IReadOnlySet<ManaColor> commanderColors,
        int bracket)
    {
        if (commanderColors.Count > 0
            && !def.ColorIdentity.All(c => c == ManaColor.Colorless || commanderColors.Contains(c)))
            return Rejection.ColorIdentity;

        if (!CommanderRules.IsLegalInCommander(def))
            return Rejection.NotCommanderLegal;

        // Hard-enforce bracket: game changers only allowed in bracket 4+.
        if (def.GameChanger && bracket < 4)
            return Rejection.AboveBracket;

        return null;
    }
}
