using System.Globalization;
using System.Text.RegularExpressions;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

/// <summary>Pure Commander construction checks over resolved oracle metadata.</summary>
public static partial class CommanderDeckValidator
{
    public static void Validate(IReadOnlyList<CardDefinition> deck, IReadOnlyList<CardDefinition> commanders)
    {
        if (deck.Count != 100)
            throw new InvalidRequestException("A Commander deck must contain exactly 100 cards, including its commander.");
        if (commanders.Count is < 1 or > 2)
            throw new InvalidRequestException("Select one commander or a legal pair of commanders.");
        if (commanders.Any(commander => !deck.Any(card => card.OracleId == commander.OracleId)))
            throw new InvalidRequestException("Each commander must be present in the deck.");
        if (commanders.Count == 1 ? !CommanderRules.IsCommanderEligible(commanders[0])
                : !CommanderPairPolicy.CanPair(commanders[0], commanders[1]))
            throw new InvalidRequestException("Choose an eligible commander or compatible commander pair.");

        var colors = commanders.SelectMany(card => card.ColorIdentity).ToHashSet();
        foreach (var card in deck.DistinctBy(card => card.OracleId))
        {
            if (!CommanderRules.IsLegalInCommander(card))
                throw new InvalidRequestException($"{card.Name} is not legal in Commander.");
            if (card.ColorIdentity.Any(color => color != ManaColor.Colorless && !colors.Contains(color)))
                throw new InvalidRequestException($"{card.Name} is outside the commander's color identity.");
            if (card.IsLand && card.Subtypes.Any(type => BasicLandColor(type) is { } color && !colors.Contains(color)))
                throw new InvalidRequestException($"{card.Name} has a basic land type outside the commander's color identity.");
        }
        foreach (var group in deck.GroupBy(card => card.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() > CopyLimit(group.First()))
                throw new InvalidRequestException($"Too many copies of {group.Key} for Commander.");
        }
    }

    private static ManaColor? BasicLandColor(string type) => type switch
    {
        "Plains" => ManaColor.White,
        "Island" => ManaColor.Blue,
        "Swamp" => ManaColor.Black,
        "Mountain" => ManaColor.Red,
        "Forest" => ManaColor.Green,
        _ => null,
    };

    private static int CopyLimit(CardDefinition card)
    {
        if (card.IsLand && card.Supertypes.Contains("Basic"))
            return int.MaxValue;
        foreach (Match match in CopyPermission().Matches(card.OracleText))
        {
            if (!string.Equals(match.Groups["name"].Value, card.Name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (match.Groups["any"].Success)
                return int.MaxValue;
            var count = match.Groups["count"].Value;
            if (int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric))
                return numeric;
            return count.ToLowerInvariant() switch
            {
                "one" => 1,
                "two" => 2,
                "three" => 3,
                "four" => 4,
                "five" => 5,
                "six" => 6,
                "seven" => 7,
                "eight" => 8,
                "nine" => 9,
                "ten" => 10,
                _ => 1,
            };
        }
        return 1;
    }

    [GeneratedRegex(@"A deck can have (?:(?<any>any number of)|up to (?<count>\w+)) cards named (?<name>[^.\r\n]+)\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CopyPermission();
}
