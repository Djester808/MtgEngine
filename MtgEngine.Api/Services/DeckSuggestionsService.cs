using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MtgEngine.Api.Dtos;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

public interface IDeckSuggestionsService
{
    Task<DeckSuggestionsDto> GetSuggestionsAsync(DeckSuggestionsRequest request);
}

public sealed class DeckSuggestionsService : IDeckSuggestionsService
{
    private readonly IScryfallService _scryfall;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IAiCacheService _cache;
    private readonly string _apiKey;
    private readonly ILogger<DeckSuggestionsService> _logger;

    private const string ModelId = "claude-haiku-4-5-20251001";

    /// <summary>
    /// How many of the newest sets count as "latest" for the latestSet category.
    /// A date window pulled in twenty-plus products and the category stopped meaning
    /// anything; the newest three sets is the baseline, whenever they happened to ship.
    /// </summary>
    private const int LatestSetCount = 3;

    /// <summary>Bump when the model or the prompt changes, to invalidate cached responses.</summary>
    // v2: gameChangers grounded in the official Scryfall list.
    // v3: response carries rejection diagnostics; v2 payloads would deserialise with
    //     an empty Diagnostics block and misreport nothing as having been rejected.
    // v4: "recent sets" now means the newest N real expansions rather than a 6-month
    //     window, so previously cached latestSet picks are no longer correct.
    // v5: categories with a membership requirement drop unresolved names instead of
    //     passing them through unverified.
    // v6: not_legal cards are rejected, and sets with no Commander-legal cards no
    //     longer count as "recent".
    // v7: reasons are rewritten against each card's real rules text.
    // v8: reasons must cite verbatim spans of the card and commander text, and are
    //     replaced with a plain rules-text restatement when the citation does not check out.
    // v9: a separate judge pass rejects reasons whose inference is wrong even though
    //     the quotes are real (Treasures being sacrificed to "sacrifice a creature").
    // v10: quote check counts words rather than characters (mana abilities are mostly
    //      symbols), and the fallback quotes an activated ability rather than a drawback.
    // v11: latest = the newest three sets, with no release-date cutoff.
    // v12: candidates carry type line and rules text, and the commander's type line is
    //      in the prompt, so tribal commanders stop being offered off-type cards.
    private const string CacheVersion = "claude-haiku-4-5-20251001-suggestions-v12";

    public DeckSuggestionsService(
        IScryfallService scryfall,
        IHttpClientFactory httpFactory,
        IAiCacheService cache,
        IConfiguration config,
        ILogger<DeckSuggestionsService> logger)
    {
        _scryfall = scryfall;
        _httpFactory = httpFactory;
        _cache = cache;
        _apiKey = SecretConfig.AnthropicApiKey(config);
        _logger = logger;
    }

    public async Task<DeckSuggestionsDto> GetSuggestionsAsync(DeckSuggestionsRequest request)
    {
        // Part of the key: when a new set arrives, cached answers still name the old one
        // as "latest", so they have to expire even though the request is unchanged.
        var recentSets = await _scryfall.GetRecentSetsAsync(maxSets: LatestSetCount);

        // Deck contents and tags are sorted so that merely reordering cards -- which
        // does not change the request semantically -- still hits the cache.
        var keyParts = new[] { request.CommanderOracleId, request.CommanderName }
            .Concat(request.DeckCardNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            .Append("|tags|")
            .Concat(request.DeckTags.Concat(request.SuggestionTags)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
            .Append("|sets|")
            .Concat(recentSets.Select(s => s.Code).OrderBy(c => c, StringComparer.OrdinalIgnoreCase));

        return await _cache.GetOrCreateAsync(
            "suggestions", CacheVersion, keyParts, () => BuildSuggestionsAsync(request, recentSets));
    }

    private async Task<DeckSuggestionsDto> BuildSuggestionsAsync(
        DeckSuggestionsRequest request, IReadOnlyList<RecentSetDto> recentSetInfo)
    {
        var cmdDef = await _scryfall.GetByOracleIdAsync(request.CommanderOracleId);
        var cmdColors = cmdDef?.ColorIdentity.ToHashSet() ?? new HashSet<ManaColor>();

        var recentSets = new HashSet<string>(
            recentSetInfo.Select(s => s.Code), StringComparer.OrdinalIgnoreCase);
        // debutOnly: the heading says "new", so a staple reprinted into a Commander
        // precon does not belong here however good it is.
        var allRecentNames = await _scryfall.GetRecentCardNamesAsync(
            recentSets, cmdColors, allowedRarities: null, debutOnly: true);

        // The data layer returns every match; cap the prompt here. Seeded on the
        // commander so repeat requests produce an identical, cacheable prompt.
        var recentCardNames = DeterministicSample.Take(
            allRecentNames, 80, request.CommanderOracleId);

        // "Game Changer" is an official Scryfall-flagged list, not a vibe. ResolveAsync
        // rejects anything not on it, so the model must choose from the real list --
        // otherwise the category silently comes back empty.
        var gameChangerNames = await _scryfall.GetGameChangerNamesAsync(cmdColors);

        var recentCardDetails = await DescribeCandidatesAsync(recentCardNames);

        var raw = await CallAnthropicAsync(
            request, recentCardDetails, gameChangerNames, DescribeTypes(cmdDef));

        var (latestSet, rejLatest) = await ResolveAsync(
            raw.LatestSet, request.DeckCardNames, cmdColors, recentSets, requireGameChanger: false,
            allowedNames: new HashSet<string>(recentCardNames, StringComparer.OrdinalIgnoreCase));
        var (topSynergy, rejSynergy) = await ResolveAsync(raw.TopSynergy, request.DeckCardNames, cmdColors, null, requireGameChanger: false);
        var (gameChangers, rejGc) = await ResolveAsync(raw.GameChangers, request.DeckCardNames, cmdColors, null, requireGameChanger: true);
        var (notableMentions, rejNotable) = await ResolveAsync(raw.NotableMentions, request.DeckCardNames, cmdColors, null, requireGameChanger: false);

        var rejections = new List<string>();
        rejections.AddRange(rejLatest);
        rejections.AddRange(rejSynergy);
        rejections.AddRange(rejGc);
        rejections.AddRange(rejNotable);

        int proposed = raw.LatestSet.Length + raw.TopSynergy.Length
                     + raw.GameChangers.Length + raw.NotableMentions.Length;

        // Deduplicate across all categories: each card appears in at most one section
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        SuggestedCardDto[] Dedup(SuggestedCardDto[] cards)
        {
            var kept = cards.Where(c => seen.Add(c.Name)).ToArray();
            for (int i = kept.Length; i < cards.Length; i++)
                rejections.Add(Rejection.DuplicateAcrossCategories);
            return kept;
        }

        latestSet = Dedup(latestSet);
        topSynergy = Dedup(topSynergy);
        gameChangers = Dedup(gameChangers);
        notableMentions = Dedup(notableMentions);

        int accepted = latestSet.Length + topSynergy.Length
                     + gameChangers.Length + notableMentions.Length;

        var byReason = rejections
            .GroupBy(r => r)
            .ToDictionary(g => g.Key, g => g.Count());

        if (byReason.Count > 0)
        {
            _logger.LogInformation(
                "Suggestions for {Commander}: {Accepted}/{Proposed} accepted; rejected {Rejected}",
                request.CommanderName, accepted, proposed,
                string.Join(", ", byReason.Select(kv => $"{kv.Key}={kv.Value}")));
        }

        // A category that the model filled but validation emptied is a defect, not a
        // quiet no-op -- surface it loudly. This is exactly how gameChangers silently
        // returned nothing on every request.
        WarnIfFullyRejected(nameof(raw.LatestSet), raw.LatestSet.Length, latestSet.Length);
        WarnIfFullyRejected(nameof(raw.TopSynergy), raw.TopSynergy.Length, topSynergy.Length);
        WarnIfFullyRejected(nameof(raw.GameChangers), raw.GameChangers.Length, gameChangers.Length);
        WarnIfFullyRejected(nameof(raw.NotableMentions), raw.NotableMentions.Length, notableMentions.Length);

        // The first pass names cards and explains them in one go, so the explanations are
        // written from recollection -- it described Goblin Bombardment as sacrificing
        // treasures when it sacrifices a creature. Now that the picks are resolved, their
        // real rules text is available, so the reasons are rewritten against it.
        await GroundReasonsAsync(
            request, [latestSet, topSynergy, gameChangers, notableMentions]);

        return new DeckSuggestionsDto
        {
            LatestSet = latestSet,
            TopSynergy = topSynergy,
            GameChangers = gameChangers,
            NotableMentions = notableMentions,
            LatestSetSources = [.. recentSetInfo],
            Diagnostics = new SuggestionDiagnosticsDto
            {
                Proposed = proposed,
                Accepted = accepted,
                Rejected = byReason,
            },
        };
    }

    /// <summary>Renders a card's types the way a type line reads: "Legendary Creature — Wolf".</summary>
    private static string DescribeTypes(CardDefinition? def)
    {
        if (def is null)
            return "unknown";

        var head = string.Join(" ", def.Supertypes.Concat(
            Enum.GetValues<Domain.Enums.CardType>()
                .Where(t => t != Domain.Enums.CardType.None && def.CardTypes.HasFlag(t))
                .Select(t => t.ToString())));

        return def.Subtypes.Count > 0 ? $"{head} — {string.Join(" ", def.Subtypes)}" : head;
    }

    /// <summary>
    /// Turns candidate names into "name | cost | types | rules text" lines for the prompt.
    /// </summary>
    /// <remarks>
    /// Rules text is clipped: the model needs enough to judge relevance, not the full
    /// card, and eighty untruncated cards would dominate the prompt.
    /// </remarks>
    private async Task<string[]> DescribeCandidatesAsync(string[] names)
    {
        const int MaxTextChars = 160;

        var described = await Task.WhenAll(names.Select(async n =>
        {
            var def = await _scryfall.GetByNameAsync(n);
            if (def is null)
                return n;

            var text = (def.OracleText ?? string.Empty).Replace("\n", " ");
            if (text.Length > MaxTextChars)
                text = text[..MaxTextChars].TrimEnd() + "…";

            return $"{def.Name} | {def.ManaCostRaw} | {DescribeTypes(def)} | {text}";
        }));

        return described;
    }

    // ---- Grounded reasons -------------------------------------------

    /// <summary>
    /// Rewrites each suggestion's reason against the card's actual rules text, in place.
    /// </summary>
    /// <remarks>
    /// Costs one extra call, but the whole response is cached, so it is paid once per
    /// commander + deck. A reason that misstates what a card does is worse than no
    /// reason: it makes every other explanation on the page suspect.
    /// </remarks>
    private async Task GroundReasonsAsync(
        DeckSuggestionsRequest request, SuggestedCardDto[][] groups)
    {
        // Only cards that resolved have trustworthy rules text to ground against.
        var resolved = groups.SelectMany(g => g)
            .Where(c => c.Card is not null && !string.IsNullOrWhiteSpace(c.Card!.OracleText))
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();

        if (resolved.Length == 0)
            return;

        try
        {
            var reasons = await CallReasonPassAsync(request, resolved);

            // Citing real text is not enough. Both halves of "Goblin Bombardment
            // converts Smaug's Treasures into damage" are quotable; the inference
            // joining them is what is false, so a separate pass attacks the inference.
            reasons = await CritiqueReasonsAsync(request, resolved, reasons);

            int verified = 0, fallback = 0;
            foreach (var group in groups)
            {
                for (int i = 0; i < group.Length; i++)
                {
                    var card = group[i].Card;
                    if (card is null || string.IsNullOrWhiteSpace(card.OracleText))
                        continue;

                    if (reasons.TryGetValue(group[i].Name, out var better)
                        && !string.IsNullOrWhiteSpace(better))
                    {
                        group[i] = group[i] with { Reason = better.Trim() };
                        verified++;
                    }
                    else
                    {
                        // The model's explanation could not be traced back to real rules
                        // text. A plain restatement of what the card does is less
                        // interesting than a synergy claim, but it is never wrong.
                        group[i] = group[i] with { Reason = FallbackReason(card.OracleText) };
                        fallback++;
                    }
                }
            }

            _logger.LogInformation(
                "Grounded reasons for {Commander}: {Verified} verified, {Fallback} fell back to rules text",
                request.CommanderName, verified, fallback);
        }
        catch (Exception ex)
        {
            // Keep the first-pass reasons rather than failing the request. They may be
            // imprecise, but the suggestions themselves are still valid.
            _logger.LogWarning(ex, "Reason grounding failed for {Commander}", request.CommanderName);
        }
    }

    private async Task<Dictionary<string, string>> CallReasonPassAsync(
        DeckSuggestionsRequest request, SuggestedCardDto[] cards)
    {
        var cardList = string.Join("\n", cards.Select(c =>
            $"- {c.Name} | {c.Card!.ManaCost} | {c.Card.OracleText.Replace("\n", " ")}"));

        var prompt = $$"""
            You are a Magic: The Gathering Commander/EDH expert.

            Commander: {{request.CommanderName}}
            Oracle text: {{request.CommanderText}}

            For each card below, write one short sentence explaining why it is worth
            playing in THIS deck. Each card is given as "name | mana cost | rules text".

            Cards ({{cards.Length}}):
            {{cardList}}

            Respond with ONLY this JSON (no markdown, no extra text):
            {"reasons":[{
              "name":"<exact card name as given>",
              "cardQuote":"<the exact span of THIS CARD'S rules text the reason relies on, copied character for character>",
              "commanderQuote":"<the exact span of the COMMANDER'S oracle text the reason relies on, copied character for character, or \"\" if the reason makes no claim about the commander>",
              "reason":"<one sentence>"
            }]}

            Rules:
            - Describe only what the rules text above actually says. Do not rely on memory
              of the card, and never attribute an ability it does not have.
            - The quotes must be copied verbatim from the text given above. A reason whose
              quotes are not found in that text is discarded, so do not paraphrase them.
            - Check that the card types line up before claiming a synergy. A Treasure is an
              artifact, not a creature; a "sacrifice a creature" cost cannot eat Treasures.
              Food, Clues and Blood are artifacts too. Only claim one card feeds another
              when the thing produced is something the other card can actually consume.
            - If there is no genuine mechanical link to the commander, just say what the
              card contributes to the deck. An honest generic reason beats an invented combo.
            - Say why it helps this commander specifically, not why it is a good card.
            - Under 18 words each. Include every card, exactly once, name spelled as given.
            """;

        // Each entry now carries two verbatim quotes as well as the sentence.
        var parsed = await CallJsonAsync<ReasonPassJson>(prompt, maxTokens: 4000);

        var byName = cards.ToDictionary(c => c.Name, c => c.Card!.OracleText!, StringComparer.OrdinalIgnoreCase);
        var commanderText = Normalize(request.CommanderText ?? string.Empty);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in parsed?.Reasons ?? [])
        {
            if (string.IsNullOrWhiteSpace(r.Name) || string.IsNullOrWhiteSpace(r.Reason))
                continue;
            if (!byName.TryGetValue(r.Name, out var oracle))
                continue;

            // Both citations must be traceable to text we supplied. This is what stops a
            // fluent-sounding explanation from attributing an ability no card here has.
            if (!QuoteIsGrounded(r.CardQuote, Normalize(oracle)))
            {
                _logger.LogDebug("Reason for {Card} rejected: cardQuote not in rules text ({Quote})",
                    r.Name, r.CardQuote);
                continue;
            }
            if (!string.IsNullOrWhiteSpace(r.CommanderQuote)
                && !QuoteIsGrounded(r.CommanderQuote, commanderText))
            {
                _logger.LogDebug("Reason for {Card} rejected: commanderQuote not in commander text ({Quote})",
                    r.Name, r.CommanderQuote);
                continue;
            }

            map[r.Name] = r.Reason;
        }
        return map;
    }

    /// <summary>
    /// Re-reads each surviving reason as a sceptic and drops or repairs the ones whose
    /// claims do not follow from the two cards' rules text.
    /// </summary>
    /// <remarks>
    /// The failure this exists for is a category error, not a fabrication: every phrase
    /// is quotable, but the sentence asserts that one card consumes something the other
    /// produces when the types do not match. Asking the same call that wrote the
    /// sentence to check it does not work -- it has already committed to the claim.
    /// A reason that survives here is worth more than four that read well.
    /// </remarks>
    private async Task<Dictionary<string, string>> CritiqueReasonsAsync(
        DeckSuggestionsRequest request,
        SuggestedCardDto[] cards,
        Dictionary<string, string> reasons)
    {
        var toCheck = cards
            .Where(c => reasons.ContainsKey(c.Name))
            .Select(c => (c.Name, Text: c.Card!.OracleText!.Replace("\n", " "), Reason: reasons[c.Name]))
            .ToArray();

        if (toCheck.Length == 0)
            return reasons;

        var claimList = string.Join("\n", toCheck.Select((c, i) =>
            $"{i + 1}. {c.Name}\n   rules text: {c.Text}\n   claim: {c.Reason}"));

        var prompt = $$"""
            You are a Magic: The Gathering rules judge checking claims for accuracy.

            Commander: {{request.CommanderName}}
            Commander's oracle text: {{request.CommanderText}}

            Below are claims about why each card belongs in this commander's deck.
            Decide whether each claim is literally true given only the rules text shown.

            Claims ({{toCheck.Length}}):
            {{claimList}}

            Reject a claim if it depends on any of these:
            - Treating an artifact token (Treasure, Food, Clue, Blood, Powerstone, Map) as
              a creature, or vice versa. "Sacrifice a creature" cannot sacrifice a Treasure.
            - A trigger firing more often than its text allows. Double strike does not
              re-trigger "whenever this creature attacks"; that triggers once per combat.
            - An ability, keyword, type or cost the rules text does not contain.
            - The commander producing or caring about something its oracle text never mentions.

            Respond with ONLY this JSON (no markdown, no extra text):
            {"checks":[{"name":"<exact card name>","verdict":"ok"|"wrong","fixed":"<if wrong, a corrected sentence under 18 words that is true given the text; otherwise \"\">"}]}

            Be strict. If a claim is even partly unsupported, mark it wrong and fix it.
            """;

        try
        {
            var parsed = await CallJsonAsync<CritiquePassJson>(prompt, maxTokens: 3000);

            int corrected = 0, dropped = 0;
            foreach (var check in parsed?.Checks ?? [])
            {
                if (string.IsNullOrWhiteSpace(check.Name) || !reasons.ContainsKey(check.Name))
                    continue;
                if (!string.Equals(check.Verdict, "wrong", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.IsNullOrWhiteSpace(check.Fixed))
                {
                    reasons[check.Name] = check.Fixed.Trim();
                    corrected++;
                }
                else
                {
                    // No usable repair: drop it so the caller falls back to rules text.
                    reasons.Remove(check.Name);
                    dropped++;
                }
            }

            _logger.LogInformation(
                "Reason critique for {Commander}: {Checked} checked, {Corrected} rewritten, {Dropped} dropped",
                request.CommanderName, toCheck.Length, corrected, dropped);
        }
        catch (Exception ex)
        {
            // An unchecked reason is still quote-grounded; keep it rather than fail.
            _logger.LogWarning(ex, "Reason critique failed for {Commander}", request.CommanderName);
        }

        return reasons;
    }

    /// <summary>Lowercased, punctuation-stripped, whitespace-collapsed form for quote matching.</summary>
    internal static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool lastSpace = false;
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastSpace = false;
            }
            else if (!lastSpace)
            {
                sb.Append(' ');
                lastSpace = true;
            }
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// True when the quote appears in the source text. One- and two-word quotes are
    /// rejected: they match almost anything and support no particular claim.
    /// </summary>
    /// <remarks>
    /// The bar is word count, not character count. Stripping punctuation turns
    /// "{T}: Add {C}{C}{C}." into eleven characters, so a length floor threw out
    /// perfectly good citations for every mana rock in the game.
    /// </remarks>
    internal static bool QuoteIsGrounded(string? quote, string normalizedSource)
    {
        var q = Normalize(quote ?? string.Empty);
        if (q.Length == 0 || !normalizedSource.Contains(q, StringComparison.Ordinal))
            return false;

        int words = 1;
        foreach (var ch in q)
            if (ch == ' ')
                words++;
        return words >= 3;
    }

    /// <summary>
    /// A reason built only from the card's own rules text, used when the model's
    /// explanation could not be verified. Truthful by construction.
    /// </summary>
    internal static string FallbackReason(string oracleText)
    {
        var lines = oracleText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Prefer an activated ability over the first line: Mana Vault opens with its
        // drawback, and "doesn't untap during your untap step" is a poor case for
        // including a card.
        var pick = Array.Find(lines, l => l.Contains(": ", StringComparison.Ordinal))
                   ?? (lines.Length > 0 ? lines[0] : oracleText.Trim());

        var cut = pick.IndexOf(". ", StringComparison.Ordinal);
        if (cut > 0)
            pick = pick[..(cut + 1)];

        return pick.Length <= 110 ? pick : pick[..107].TrimEnd() + "…";
    }

    /// <summary>One deterministic JSON-in, JSON-out call to the model.</summary>
    private async Task<T?> CallJsonAsync<T>(string prompt, int maxTokens)
    {
        var body = new
        {
            model = ModelId,
            max_tokens = maxTokens,
            temperature = 0,
            messages = new[] { new { role = "user", content = prompt } },
        };

        var http = _httpFactory.CreateClient("AnthropicApi");
        using var httpReq = new HttpRequestMessage(HttpMethod.Post, "v1/messages")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        httpReq.Headers.Add("x-api-key", _apiKey);
        httpReq.Headers.Add("anthropic-version", "2023-06-01");

        var resp = await http.SendAsync(httpReq);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            throw new AiUpstreamException("Anthropic", resp.StatusCode, err);
        }

        return AnthropicResponse.DeserializeJson<T>(await resp.Content.ReadAsStringAsync());
    }

    private sealed class ReasonPassJson
    {
        [JsonPropertyName("reasons")] public ReasonEntry[] Reasons { get; set; } = [];
    }

    private sealed class CritiquePassJson
    {
        [JsonPropertyName("checks")] public CritiqueEntry[] Checks { get; set; } = [];
    }

    private sealed class CritiqueEntry
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("verdict")] public string Verdict { get; set; } = string.Empty;
        [JsonPropertyName("fixed")] public string Fixed { get; set; } = string.Empty;
    }

    private sealed class ReasonEntry
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("cardQuote")] public string CardQuote { get; set; } = string.Empty;
        [JsonPropertyName("commanderQuote")] public string CommanderQuote { get; set; } = string.Empty;
        [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
    }

    // ---- LLM call ---------------------------------------------------

    private async Task<RawSuggestions> CallAnthropicAsync(
        DeckSuggestionsRequest req, string[] recentCardDetails, string[] gameChangerNames,
        string commanderTypeLine)
    {
        var deckContext = req.DeckCardNames.Length > 0
            ? $"\n\nCards already in the deck ({req.DeckCardNames.Length}):\n{string.Join(", ", req.DeckCardNames)}"
            : string.Empty;

        var allTags = req.DeckTags.Concat(req.SuggestionTags).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var tagsContext = allTags.Length > 0
            ? $"\n\nDeck style / focus tags: {string.Join(", ", allTags)}\nLet these tags strongly guide your suggestions (e.g. 'budget' → prefer affordable cards; 'combo' → lean into synergistic combos)."
            : string.Empty;

        // Give the candidates' types and rules text, not just names. Cards from a set
        // released after the model's training data are unknown to it, so a bare list of
        // names is picked from blind -- that is how a Wolf-tribal commander ended up
        // being offered Rhino, Terrible Trampler.
        var recentContext = recentCardDetails.Length > 0
            ? $"\n\nRecent cards available for the latestSet category (choose the best 4 from this list, "
              + "judging them on the type and rules text given -- do not rely on memory):\n"
              + string.Join("\n", recentCardDetails.Select(d => $"- {d}"))
            : string.Empty;

        var gameChangerContext = gameChangerNames.Length > 0
            ? $"\n\nOfficial Game Changer cards legal in this commander's colour identity " +
              $"(the gameChangers category MUST be chosen from this exact list — any other card will be rejected):\n" +
              string.Join(", ", gameChangerNames)
            : string.Empty;

        var prompt = $$"""
            You are a Magic: The Gathering Commander/EDH expert.

            Commander: {{req.CommanderName}}
            Type: {{commanderTypeLine}}
            Oracle text: {{req.CommanderText}}{{deckContext}}{{tagsContext}}{{recentContext}}{{gameChangerContext}}

            Suggest cards NOT already in the deck that would improve it. Use only real, official Magic card names (exact spelling).
            Only suggest cards that are legal in the commander's color identity.
            If the commander cares about a creature type, weight every category towards
            that type and towards cards that make or reward it.

            Respond with ONLY this exact JSON (no markdown, no extra text):
            {
              "latestSet": [{"name": "...", "reason": "...", "score": 85}, ...],
              "topSynergy": [{"name": "...", "reason": "...", "score": 85}, ...],
              "gameChangers": [{"name": "...", "reason": "...", "score": 85}, ...],
              "notableMentions": [{"name": "...", "reason": "...", "score": 85}, ...]
            }

            Rules:
            - latestSet: exactly 4 cards chosen from the "Recent cards available" list above that best fit this strategy (MUST use names exactly as given)
            - topSynergy: exactly 6 cards with the strongest synergy with this specific commander
            - gameChangers: exactly 4 cards taken verbatim from the "Official Game Changer cards" list above, picking those that best fit this strategy. Do not invent entries for this category — cards outside that list are discarded. If the list has fewer than 4 entries, return all of them.
            - notableMentions: exactly 4 solid staples or support cards worth including
            - score: 0-100 compatibility percentage with this commander and existing deck

            Do not repeat cards between categories. Do not suggest cards already in the deck.
            """;

        var body = new
        {
            model = ModelId,
            max_tokens = 1500,
            temperature = 0,
            messages = new[] { new { role = "user", content = prompt } },
        };

        var http = _httpFactory.CreateClient("AnthropicApi");
        using var httpReq = new HttpRequestMessage(HttpMethod.Post, "v1/messages")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        httpReq.Headers.Add("x-api-key", _apiKey);
        httpReq.Headers.Add("anthropic-version", "2023-06-01");

        var resp = await http.SendAsync(httpReq);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            _logger.LogError("Anthropic suggestions {Status}: {Body}", resp.StatusCode, err);
            throw new AiUpstreamException("Anthropic", resp.StatusCode, err);
        }

        var respJson = await resp.Content.ReadAsStringAsync();
        return AnthropicResponse.DeserializeJson<RawSuggestions>(respJson) ?? new RawSuggestions();
    }

    // ---- Card resolution --------------------------------------------

    private void WarnIfFullyRejected(string category, int proposed, int accepted)
    {
        if (proposed > 0 && accepted == 0)
        {
            _logger.LogWarning(
                "Suggestion category '{Category}' rejected all {Proposed} proposals — " +
                "prompt and validation rules are likely out of sync",
                category, proposed);
        }
    }

    /// <summary>Why a proposed card did not make it into the response.</summary>
    private static class Rejection
    {
        public const string AlreadyInDeck = "already-in-deck";
        public const string BlankName = "blank-name";
        public const string UnknownCard = "unknown-card";
        public const string ColorIdentity = "color-identity";
        public const string NotGameChanger = "not-a-game-changer";
        public const string NotCommanderLegal = "not-commander-legal";
        public const string NotRecentPrinting = "no-recent-printing";
        public const string DuplicateAcrossCategories = "duplicate-across-categories";
        public const string LookupFailed = "lookup-failed";
    }

    private sealed record Resolution(SuggestedCardDto? Card, string? RejectedBecause);

    private async Task<(SuggestedCardDto[] Cards, List<string> Rejections)> ResolveAsync(
        RawCard[] rawCards,
        string[] deckCardNames,
        HashSet<ManaColor> cmdColors,
        IReadOnlySet<string>? recentSets,
        bool requireGameChanger,
        IReadOnlySet<string>? allowedNames = null)
    {
        var deckSet = new HashSet<string>(deckCardNames, StringComparer.OrdinalIgnoreCase);
        var rejections = new List<string>();

        var candidates = new List<RawCard>();
        foreach (var r in rawCards)
        {
            if (string.IsNullOrWhiteSpace(r.Name)) rejections.Add(Rejection.BlankName);
            else if (deckSet.Contains(r.Name)) rejections.Add(Rejection.AlreadyInDeck);
            else candidates.Add(r);
        }

        var results = await Task.WhenAll(candidates.Select(r =>
            ResolveOneAsync(r, cmdColors, recentSets, requireGameChanger, allowedNames)));

        var cards = new List<SuggestedCardDto>();
        foreach (var res in results)
        {
            if (res.Card is not null) cards.Add(res.Card);
            else if (res.RejectedBecause is not null) rejections.Add(res.RejectedBecause);
        }

        return (cards.ToArray(), rejections);
    }

    private async Task<Resolution> ResolveOneAsync(
        RawCard raw,
        HashSet<ManaColor> cmdColors,
        IReadOnlySet<string>? recentSets,
        bool requireGameChanger,
        IReadOnlySet<string>? allowedNames = null)
    {
        try
        {
            var def = await _scryfall.GetByNameAsync(raw.Name);
            if (def is null)
            {
                // Categories with a membership requirement (official Game Changer list,
                // printed in a recent set) cannot verify an unresolved name, so it must
                // be dropped -- otherwise "latest set" silently lists cards that may not
                // be from a recent set at all.
                bool categoryRequiresProof = requireGameChanger || recentSets is { Count: > 0 };

                // Elsewhere an unresolved name is still shown -- the model may know a card
                // the local bulk data does not -- but it is counted either way, so a spike
                // in hallucinated names stays visible.
                return categoryRequiresProof
                    ? new Resolution(null, Rejection.UnknownCard)
                    : new Resolution(
                        new SuggestedCardDto { Name = raw.Name, Reason = raw.Reason, Score = raw.Score },
                        Rejection.UnknownCard);
            }

            // Format legality. "not_legal" covers Un-sets, Alchemy, and standalone
            // Universes Beyond products -- none of which can go in a Commander deck.
            if (!CommanderRules.IsLegalInCommander(def))
                return new Resolution(null, Rejection.NotCommanderLegal);

            // Color identity check — filter cards that exceed the commander's color identity
            if (cmdColors.Count > 0)
            {
                bool isLegal = def.ColorIdentity.All(c => c == ManaColor.Colorless || cmdColors.Contains(c));
                if (!isLegal)
                    return new Resolution(null, Rejection.ColorIdentity);
            }

            // Game Changer check — only official GC-designated cards allowed in this category
            if (requireGameChanger && !def.GameChanger)
                return new Resolution(null, Rejection.NotGameChanger);

            var printings = await _scryfall.GetPrintingsAsync(def.OracleId);

            // Recent-set check (latestSet category only). The allow-list is the exact set
            // of names the prompt offered, already filtered to cards that debuted in
            // these sets -- a card the model recalled instead of reading off the list is
            // usually an old reprint, which is what put Thran Dynamo under "new cards".
            if (allowedNames is { Count: > 0 } && !allowedNames.Contains(raw.Name))
                return new Resolution(null, Rejection.NotRecentPrinting);

            if (recentSets is { Count: > 0 })
            {
                bool hasRecentPrinting = printings.Any(p => p.SetCode is not null && recentSets.Contains(p.SetCode));
                if (!hasRecentPrinting)
                    return new Resolution(null, Rejection.NotRecentPrinting);
            }

            var scryfallId = printings.FirstOrDefault()?.ScryfallId;

            return new Resolution(new SuggestedCardDto
            {
                Name = raw.Name,
                Reason = raw.Reason,
                Score = raw.Score,
                ScryfallId = scryfallId,
                Card = MapToCardDto(def),
            }, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve suggestion: {Name}", raw.Name);
            return new Resolution(
                new SuggestedCardDto { Name = raw.Name, Reason = raw.Reason, Score = raw.Score },
                Rejection.LookupFailed);
        }
    }

    // ---- Mapping ----------------------------------------------------

    private static CardDto MapToCardDto(CardDefinition def) => new()
    {
        CardId = def.OracleId,
        OracleId = def.OracleId,
        Name = def.Name,
        ManaCost = string.IsNullOrEmpty(def.ManaCostRaw) ? def.ManaCost.ToString() : def.ManaCostRaw,
        ManaValue = def.Cmc,
        CardTypes = def.CardTypes.ToString().Split(", ")
                                .Where(t => Enum.IsDefined(typeof(CardTypeDto), t))
                                .Select(t => Enum.Parse<CardTypeDto>(t))
                                .ToArray(),
        Subtypes = [.. def.Subtypes],
        Supertypes = [.. def.Supertypes],
        OracleText = def.OracleText,
        Power = def.Power,
        Toughness = def.Toughness,
        StartingLoyalty = def.StartingLoyalty,
        Keywords = def.Keywords.ToString().Split(", ")
                                .Where(k => !string.IsNullOrEmpty(k) && k != "None")
                                .ToArray(),
        ImageUriNormal = def.ImageUriNormal,
        ImageUriNormalBack = def.ImageUriNormalBack,
        ImageUriSmall = def.ImageUriSmall,
        ImageUriArtCrop = def.ImageUriArtCrop,
        ColorIdentity = def.ColorIdentity
                                .Select(c => c switch
                                {
                                    ManaColor.White => ManaColorDto.W,
                                    ManaColor.Blue => ManaColorDto.U,
                                    ManaColor.Black => ManaColorDto.B,
                                    ManaColor.Red => ManaColorDto.R,
                                    ManaColor.Green => ManaColorDto.G,
                                    _ => ManaColorDto.C,
                                })
                                .ToArray(),
        FlavorText = def.FlavorText,
        Artist = def.Artist,
        SetCode = def.SetCode,
        Rarity = def.Rarity,
        Legalities = def.Legalities.ToDictionary(kv => kv.Key, kv => kv.Value),
        GameChanger = def.GameChanger,
    };

    // ---- Raw JSON shapes --------------------------------------------

    private sealed class RawSuggestions
    {
        [JsonPropertyName("latestSet")] public RawCard[] LatestSet { get; set; } = [];
        [JsonPropertyName("topSynergy")] public RawCard[] TopSynergy { get; set; } = [];
        [JsonPropertyName("gameChangers")] public RawCard[] GameChangers { get; set; } = [];
        [JsonPropertyName("notableMentions")] public RawCard[] NotableMentions { get; set; } = [];
    }

    private sealed class RawCard
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
        [JsonPropertyName("score")] public int Score { get; set; }
    }
}
