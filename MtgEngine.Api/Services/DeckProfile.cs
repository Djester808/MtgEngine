using System.Text;
using System.Text.RegularExpressions;
using MtgEngine.Api.Dtos;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

/// <summary>
/// What a deck is actually doing, computed from its cards.
/// </summary>
/// <remarks>
/// Scoring a card against the commander alone cannot see that Gaea's Cradle is excellent
/// in a token deck -- the commander's text says nothing about creature count. Archetype
/// and role gaps are properties of the ninety-nine, so they are measured here and handed
/// to the model as facts. Thresholds come from the doctrine, §7 and §2.
/// </remarks>
public sealed record DeckProfile(
    int TotalCards,
    int Lands,
    int NonLands,
    double AverageManaValue,
    IReadOnlyDictionary<CardRole, int> RoleCounts,
    IReadOnlyDictionary<ManaColor, int> ColourPips,
    IReadOnlyDictionary<ManaColor, int> ColourSources,
    IReadOnlyList<string> Archetypes,
    IReadOnlyList<string> Gaps)
{
    /// <summary>Below this the deck is too empty for gap analysis to mean anything (§10.2).</summary>
    public const int MinCardsForGapAnalysis = 20;

    public bool IsTooSmallForGapAnalysis => NonLands < MinCardsForGapAnalysis;

    /// <summary>The fields the profile is built from, so definitions and DTOs share a path.</summary>
    public readonly record struct ProfileCard(
        string Name, int Cmc, string ManaCostRaw, string? OracleText,
        CardType CardTypes, IReadOnlyList<string> Subtypes, IReadOnlyList<string> Supertypes);

    public static ProfileCard From(CardDefinition d) => new(
        d.Name, d.Cmc, d.ManaCostRaw, d.OracleText, d.CardTypes, d.Subtypes, d.Supertypes);

    public static ProfileCard From(CardDto d) => new(
        d.Name, d.ManaValue, d.ManaCost, d.OracleText,
        d.CardTypes.Aggregate(CardType.None, (acc, t) =>
            Enum.TryParse<CardType>(t.ToString(), out var parsed) ? acc | parsed : acc),
        d.Subtypes, d.Supertypes);

    // ---- Text signals ---------------------------------------------------

    private static readonly Regex MakesToken = new(@"\bcreate(s)?\s+(a|an|one|two|three|X|\d+)\b[^.]*\btoken",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PlusOneCounter = new(@"\+1/\+1 counter", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SacrificeMatters = new(
        @"\bsacrifice (a|an|another|two|three|X)\b|\bwhen(ever)? .{0,40}\bdies\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex GraveyardMatters = new(
        @"\bfrom your graveyard\b|\bmill\b|\breturn .{0,40}from your graveyard\b|\bexile .{0,30}from your graveyard\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Landfall = new(
        @"\blandfall\b|\bwhenever a land (you control )?enters\b|\bplay an additional land\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Not "add" followed by a brace: the most valuable fixers word it "Add one mana of any
    // color", with no symbol at all, and requiring the brace excluded every one of them
    // from the source count.
    private static readonly Regex AddsMana = new(@"\badds?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ManaSymbol = new(@"\{([^}]+)\}", RegexOptions.Compiled);

    public static DeckProfile Build(IEnumerable<ProfileCard> cards)
    {
        var all = cards.ToArray();
        if (all.Length == 0)
            return Empty;

        var lands = all.Count(c => c.CardTypes.HasFlag(CardType.Land));
        var nonLands = all.Length - lands;

        // Lands are excluded from the curve: thirty-seven zero-cost cards would drag the
        // average far below what the deck actually costs to operate.
        var spells = all.Where(c => !c.CardTypes.HasFlag(CardType.Land)).ToArray();
        var avgMv = spells.Length == 0 ? 0 : spells.Average(c => c.Cmc);

        var roles = new Dictionary<CardRole, int>();
        foreach (var c in all)
        {
            var role = ClassifyRole(c);
            roles[role] = roles.GetValueOrDefault(role) + 1;
        }

        var pips = CountPips(all);
        var sources = CountColourSources(all);
        var archetypes = DetectArchetypes(all, nonLands);
        var gaps = FindGaps(all.Length, lands, roles, nonLands);

        return new DeckProfile(
            all.Length, lands, nonLands, Math.Round(avgMv, 2),
            roles, pips, sources, archetypes, gaps);
    }

    public static readonly DeckProfile Empty = new(
        0, 0, 0, 0,
        new Dictionary<CardRole, int>(), new Dictionary<ManaColor, int>(),
        new Dictionary<ManaColor, int>(), [], []);

    /// <summary>
    /// Mirrors <see cref="CardRoleClassifier"/>, which works on a CardDefinition we may not
    /// have here. Kept in the same order so the two agree on precedence.
    /// </summary>
    private static CardRole ClassifyRole(ProfileCard c)
    {
        if (c.CardTypes.HasFlag(CardType.Land)) return CardRole.Land;

        var text = c.OracleText?.ToLowerInvariant() ?? string.Empty;
        if (text.Length == 0) return CardRole.Other;

        if (AddsMana.IsMatch(text) || Regex.IsMatch(text, @"search your library for (a|up to \w+)[^.]*\bland\b"))
            return CardRole.Ramp;

        if (Regex.IsMatch(text,
            @"\b(destroy target|destroy all|destroy each|exile target|exile all|exile each" +
            @"|deals? \d+ damage to target|target creature gets -|target player sacrifices" +
            @"|return target (creature|permanent|nonland permanent) to its owner's hand|counter target)\b"))
            return CardRole.Removal;

        if (Regex.IsMatch(text, @"\bdraws?\s+(a|one|two|three|four|five|\w+|X)\s+cards?\b"))
            return CardRole.Draw;

        return CardRole.Other;
    }

    /// <summary>
    /// Coloured pips across the deck, weighted equally. Hybrid counts for both halves --
    /// it can be paid either way, so it is a real requirement for neither and an option
    /// for both.
    /// </summary>
    private static Dictionary<ManaColor, int> CountPips(IEnumerable<ProfileCard> cards)
    {
        var pips = new Dictionary<ManaColor, int>();

        foreach (var c in cards)
        {
            foreach (Match m in ManaSymbol.Matches(c.ManaCostRaw ?? string.Empty))
            {
                foreach (var ch in m.Groups[1].Value)
                {
                    var colour = ColourOf(ch);
                    if (colour is ManaColor col)
                        pips[col] = pips.GetValueOrDefault(col) + 1;
                }
            }
        }

        return pips;
    }

    /// <summary>
    /// Permanents that can produce each colour. A dual counts for both, which is exactly
    /// why doctrine §3.2 values them: one card, two sources.
    /// </summary>
    private static Dictionary<ManaColor, int> CountColourSources(IEnumerable<ProfileCard> cards)
    {
        var sources = new Dictionary<ManaColor, int>();

        foreach (var c in cards)
        {
            var seen = new HashSet<ManaColor>();

            // Basic land types grant the intrinsic ability, so they are sources even
            // when the card prints no mana symbol at all.
            foreach (var sub in c.Subtypes)
            {
                var colour = sub switch
                {
                    "Plains" => (ManaColor?)ManaColor.White,
                    "Island" => ManaColor.Blue,
                    "Swamp" => ManaColor.Black,
                    "Mountain" => ManaColor.Red,
                    "Forest" => ManaColor.Green,
                    _ => null,
                };
                if (colour is ManaColor lc) seen.Add(lc);
            }

            var text = c.OracleText ?? string.Empty;
            if (AddsMana.IsMatch(text))
            {
                // "Add one mana of any color" produces every colour.
                if (Regex.IsMatch(text, @"add one mana of any color", RegexOptions.IgnoreCase))
                {
                    foreach (var col in new[] { ManaColor.White, ManaColor.Blue, ManaColor.Black, ManaColor.Red, ManaColor.Green })
                        seen.Add(col);
                }
                else
                {
                    foreach (Match m in Regex.Matches(text, @"add[^.]*", RegexOptions.IgnoreCase))
                        foreach (Match sym in ManaSymbol.Matches(m.Value))
                            foreach (var ch in sym.Groups[1].Value)
                                if (ColourOf(ch) is ManaColor col) seen.Add(col);
                }
            }

            foreach (var col in seen)
                sources[col] = sources.GetValueOrDefault(col) + 1;
        }

        return sources;
    }

    private static ManaColor? ColourOf(char c) => char.ToUpperInvariant(c) switch
    {
        'W' => ManaColor.White,
        'U' => ManaColor.Blue,
        'B' => ManaColor.Black,
        'R' => ManaColor.Red,
        'G' => ManaColor.Green,
        _ => null,
    };

    /// <summary>Doctrine §7. A deck can be several of these at once.</summary>
    private static List<string> DetectArchetypes(ProfileCard[] all, int nonLands)
    {
        var found = new List<string>();
        if (nonLands == 0) return found;

        var creatures = all.Count(c => c.CardTypes.HasFlag(CardType.Creature));
        var creatureShare = (double)creatures / nonLands;

        if (creatureShare >= 0.55) found.Add($"creature-centric ({creatures} creatures, {creatureShare:P0} of nonlands)");
        else if (creatureShare >= 0.40) found.Add($"creature-based ({creatures} creatures, {creatureShare:P0} of nonlands)");

        int Count(Regex r) => all.Count(c => r.IsMatch(c.OracleText ?? string.Empty));

        var tokens = Count(MakesToken);
        if (tokens >= 6) found.Add($"tokens ({tokens} cards create tokens)");

        var counters = Count(PlusOneCounter);
        if (counters >= 8) found.Add($"+1/+1 counters ({counters} cards)");

        var sac = Count(SacrificeMatters);
        if (sac >= 6) found.Add($"sacrifice/aristocrats ({sac} cards)");

        var yard = Count(GraveyardMatters);
        if (yard >= 8) found.Add($"graveyard ({yard} cards)");

        var landfall = Count(Landfall);
        if (landfall >= 6) found.Add($"landfall ({landfall} cards)");

        var spells = all.Count(c => c.CardTypes.HasFlag(CardType.Instant) || c.CardTypes.HasFlag(CardType.Sorcery));
        if (spells >= 20) found.Add($"spellslinger ({spells} instants/sorceries)");

        var artifacts = all.Count(c => c.CardTypes.HasFlag(CardType.Artifact));
        if (artifacts >= 15) found.Add($"artifacts ({artifacts} cards)");

        // Tribal: the most common creature type, if it is dense enough.
        var tribe = all
            .Where(c => c.CardTypes.HasFlag(CardType.Creature))
            .SelectMany(c => c.Subtypes)
            .GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();

        if (tribe is not null && tribe.Count() >= 12)
            found.Add($"{tribe.Key} tribal ({tribe.Count()} cards)");

        return found;
    }

    /// <summary>Doctrine §2 quotas. Only shortfalls and clear excesses are reported.</summary>
    private static List<string> FindGaps(
        int total, int lands, Dictionary<CardRole, int> roles, int nonLands)
    {
        var gaps = new List<string>();
        if (nonLands < MinCardsForGapAnalysis) return gaps;

        void Check(string label, int actual, int min, int max)
        {
            if (actual < min) gaps.Add($"{label}: {actual}, short of the {min}-{max} target");
            else if (actual > max) gaps.Add($"{label}: {actual}, above the {min}-{max} target");
        }

        Check("lands", lands, 36, 38);
        Check("ramp", roles.GetValueOrDefault(CardRole.Ramp), 8, 12);
        Check("card draw", roles.GetValueOrDefault(CardRole.Draw), 8, 12);
        Check("interaction", roles.GetValueOrDefault(CardRole.Removal), 8, 12);

        var manaSources = lands + roles.GetValueOrDefault(CardRole.Ramp);
        if (manaSources < 45)
            gaps.Add($"total mana sources: {manaSources}, below the 45-48 target (doctrine §2.1)");

        return gaps;
    }

    /// <summary>Renders the profile for a prompt. Empty string when there is no deck to describe.</summary>
    public string Describe()
    {
        if (TotalCards == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine($"Cards: {TotalCards} ({Lands} lands, {NonLands} nonlands)");
        sb.AppendLine($"Average mana value (nonlands): {AverageManaValue}");

        if (RoleCounts.Count > 0)
        {
            var roles = RoleCounts.OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Key.ToString().ToLowerInvariant()} {kv.Value}");
            sb.AppendLine($"Roles: {string.Join(", ", roles)}");
        }

        if (ColourSources.Count > 0)
        {
            var src = ColourSources.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}");
            sb.AppendLine($"Coloured sources: {string.Join(", ", src)}");
        }

        if (ColourPips.Count > 0)
        {
            var pip = ColourPips.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}");
            sb.AppendLine($"Coloured pips required: {string.Join(", ", pip)}");
        }

        sb.AppendLine(Archetypes.Count > 0
            ? $"Archetype signals: {string.Join("; ", Archetypes)}"
            : "Archetype signals: none strong enough to register");

        if (Gaps.Count > 0)
            sb.AppendLine($"Gaps against doctrine quotas: {string.Join("; ", Gaps)}");
        else if (!IsTooSmallForGapAnalysis)
            sb.AppendLine("Gaps against doctrine quotas: none");

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// A short stable signature of the things deck-aware scoring actually reacts to.
    /// Two decks with the same roles, archetypes and gaps get the same answer, so this is
    /// the correct cache key -- far coarser, and far more reusable, than the card list.
    /// </summary>
    public string GapSignature()
    {
        if (TotalCards == 0) return "empty";

        var roles = string.Join(",", RoleCounts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}{kv.Value}"));
        var arch = string.Join(",", Archetypes.OrderBy(a => a, StringComparer.Ordinal));
        var gaps = string.Join(",", Gaps.OrderBy(g => g, StringComparer.Ordinal));

        return $"{Lands}|{NonLands}|{AverageManaValue}|{roles}|{arch}|{gaps}";
    }
}
