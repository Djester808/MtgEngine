using System.Text;
using System.Text.Json;
using MtgEngine.Api.Dtos;
using MtgEngine.Domain.Enums;

namespace MtgEngine.Api.Services;

public interface IAiBuildService
{
    Task<AiBuildResultDto> BuildDeckAsync(Guid deckId, string userId, AiBuildRequest request);

    /// <summary>
    /// Reviews a built deck and swaps its weakest cards for better picks from the same
    /// legal pool, leaving the deck the same size.
    /// </summary>
    Task<AiRefineResultDto> RefineDeckAsync(Guid deckId, string userId, AiRefineRequest request);
}

public sealed class AiBuildService : IAiBuildService
{
    private readonly IScryfallService _scryfall;
    private readonly ICollectionService _collection;
    private readonly IEdhrecPoolService _edhrec;
    private readonly IHttpClientFactory _httpFactory;
    private readonly string _apiKey;
    private readonly ILogger<AiBuildService> _logger;

    private const string ModelId = "claude-sonnet-4-6";

    /// <summary>
    /// Spare candidates requested alongside the main deck, used to backfill slots lost
    /// to validation. Observed rejection counts run 2-20 per build, so 30 covers the
    /// range with headroom; unused entries cost nothing but a few output tokens.
    /// </summary>
    private const int SubstituteCount = 30;

    private static readonly HashSet<string> BasicLands = new(StringComparer.OrdinalIgnoreCase)
        { "Plains", "Island", "Swamp", "Mountain", "Forest",
          "Wastes", "Snow-Covered Plains", "Snow-Covered Island",
          "Snow-Covered Swamp", "Snow-Covered Mountain", "Snow-Covered Forest" };

    public AiBuildService(
        IScryfallService scryfall,
        ICollectionService collection,
        IEdhrecPoolService edhrec,
        IHttpClientFactory httpFactory,
        IConfiguration config,
        ILogger<AiBuildService> logger)
    {
        _scryfall = scryfall;
        _collection = collection;
        _edhrec = edhrec;
        _httpFactory = httpFactory;
        _apiKey = SecretConfig.AnthropicApiKey(config);
        _logger = logger;
    }

    public async Task<AiBuildResultDto> BuildDeckAsync(Guid deckId, string userId, AiBuildRequest request)
    {
        var commanderOracleId = request.CommanderOracleId;

        var cmdDef = await _scryfall.GetByOracleIdAsync(commanderOracleId)
            ?? throw new InvalidOperationException($"Commander not found: {commanderOracleId}");

        var cmdColors = cmdDef.ColorIdentity.ToHashSet();
        var colorNames = FormatColors(cmdColors);

        // Fetch the current deck to know how many main-board slots remain
        var existingDeck = await _collection.GetDeckAsync(deckId, userId);
        var existingCards = existingDeck?.Cards ?? [];

        var addedOracleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { commanderOracleId };

        int existingMainCount = 0;
        foreach (var c in existingCards)
        {
            if ((c.Board ?? "main") != "main")
                continue;
            if (string.Equals(c.OracleId, commanderOracleId, StringComparison.OrdinalIgnoreCase))
                continue;
            existingMainCount += c.Quantity;
            addedOracleIds.Add(c.OracleId);
        }

        int mainSlotsLeft = Math.Max(0, 99 - existingMainCount);

        // Fetch cards from recent sets to feed the LLM as candidates
        var recentSetCodes = await _scryfall.GetRecentSetCodesAsync(monthsBack: 9);
        var recentCardNames = await _scryfall.GetRecentCardNamesAsync(recentSetCodes, cmdColors);

        // Commander-specific candidate pool, hard-filtered before the model ever sees it.
        // Constraining selection to legal cards beats instructing the model to respect
        // the constraint -- a prompt-only attempt at that measured strictly worse.
        var pool = await BuildCandidatePoolAsync(cmdDef.Name, cmdColors, request.Bracket);

        var llmResult = await CallAnthropicAsync(
            cmdDef.Name,
            cmdDef.OracleText ?? string.Empty,
            colorNames,
            mainSlotsLeft,
            request.Bracket,
            request.PriceRange,
            request.IncludeSideboard,
            request.IncludeMaybeboard,
            recentCardNames,
            pool);

        // Substitutes trail the main picks so a rejected card is backfilled rather than
        // leaving a hole. Without this the deck comes up short by however many cards
        // validation discarded -- an illegal deck, reported only as a skip count.
        var mainCandidates = llmResult.Main.Concat(llmResult.Substitutes).ToArray();

        var (mainAdded, mainSkipped, mainReasons) =
            await AddCards(mainCandidates, "main", deckId, userId, cmdColors, addedOracleIds, mainSlotsLeft, request.Bracket);

        var (sideAdded, sideSkipped, sideReasons) = request.IncludeSideboard
            ? await AddCards(llmResult.Side, "side", deckId, userId, cmdColors, addedOracleIds, 10, request.Bracket)
            : (0, 0, new Dictionary<string, int>());

        var (maybeAdded, maybeSkipped, maybeReasons) = request.IncludeMaybeboard
            ? await AddCards(llmResult.Maybe, "maybe", deckId, userId, cmdColors, addedOracleIds, 10, request.Bracket)
            : (0, 0, new Dictionary<string, int>());

        var byReason = new Dictionary<string, int>();
        foreach (var source in new[] { mainReasons, sideReasons, maybeReasons })
            foreach (var (reason, count) in source)
                byReason[reason] = byReason.GetValueOrDefault(reason) + count;

        int shortfall = Math.Max(0, mainSlotsLeft - mainAdded);
        if (shortfall > 0)
        {
            _logger.LogWarning(
                "AI build for {Commander}: {Added}/{Target} main-deck slots filled — {Short} short " +
                "after {Candidates} candidates. Rejections: {Reasons}",
                cmdDef.Name, mainAdded, mainSlotsLeft, shortfall, mainCandidates.Length,
                string.Join(", ", byReason.Select(kv => $"{kv.Key}={kv.Value}")));
        }

        return new AiBuildResultDto
        {
            CardsAdded = mainAdded,
            SideboardAdded = sideAdded,
            MaybeboardAdded = maybeAdded,
            CardsSkipped = mainSkipped + sideSkipped + maybeSkipped,
            MainTarget = mainSlotsLeft,
            MainShortfall = shortfall,
            SkippedByReason = byReason,
        };
    }

    // ---- Refine -------------------------------------------------------------

    public async Task<AiRefineResultDto> RefineDeckAsync(Guid deckId, string userId, AiRefineRequest request)
    {
        var deck = await _collection.GetDeckAsync(deckId, userId)
            ?? throw new InvalidOperationException($"Deck not found: {deckId}");

        if (string.IsNullOrWhiteSpace(deck.CommanderOracleId))
            throw new InvalidOperationException("Deck has no commander to refine against.");

        var cmdDef = await _scryfall.GetByOracleIdAsync(deck.CommanderOracleId)
            ?? throw new InvalidOperationException($"Commander not found: {deck.CommanderOracleId}");

        var cmdColors = cmdDef.ColorIdentity.ToHashSet();

        // Basics are excluded from swapping: they are the mana base, and trading them
        // away silently changes the land count the build deliberately set.
        var swappable = deck.Cards
            .Where(c => (c.Board ?? "main") == "main"
                        && !string.Equals(c.OracleId, deck.CommanderOracleId, StringComparison.OrdinalIgnoreCase)
                        && c.CardDetails is not null
                        && !c.CardDetails.Supertypes.Contains("Basic"))
            .GroupBy(c => c.OracleId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();

        int sizeBefore = deck.Cards.Where(c => (c.Board ?? "main") == "main").Sum(c => c.Quantity);

        if (swappable.Length == 0)
            return new AiRefineResultDto { DeckSizeBefore = sizeBefore, DeckSizeAfter = sizeBefore };

        var pool = await BuildCandidatePoolAsync(cmdDef.Name, cmdColors, request.Bracket);

        var proposed = await CallRefineAsync(
            cmdDef.Name, cmdDef.OracleText ?? string.Empty, FormatColors(cmdColors),
            swappable.Select(c => c.CardDetails!.Name).ToArray(),
            request, pool);

        var inDeck = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in swappable)
            inDeck[c.CardDetails!.Name] = c.OracleId;

        var applied = new List<CardSwapDto>();
        var rejected = new Dictionary<string, int>();
        void Reject(string reason) => rejected[reason] = rejected.GetValueOrDefault(reason) + 1;

        var addedNames = new HashSet<string>(
            deck.Cards.Where(c => c.CardDetails is not null).Select(c => c.CardDetails!.Name),
            StringComparer.OrdinalIgnoreCase);

        foreach (var swap in proposed.Take(Math.Max(0, request.MaxSwaps)))
        {
            if (string.IsNullOrWhiteSpace(swap.Out) || string.IsNullOrWhiteSpace(swap.In))
            { Reject("incomplete-swap"); continue; }

            if (!inDeck.TryGetValue(swap.Out, out var outOracleId))
            { Reject("out-card-not-in-deck"); continue; }

            if (addedNames.Contains(swap.In))
            { Reject("in-card-already-in-deck"); continue; }

            var inDef = await _scryfall.GetByNameAsync(swap.In);
            if (inDef is null)
            { Reject(Rejection.UnknownCard); continue; }

            if (cmdColors.Count > 0
                && !inDef.ColorIdentity.All(c => c == ManaColor.Colorless || cmdColors.Contains(c)))
            { Reject(Rejection.ColorIdentity); continue; }

            if (inDef.Legalities.TryGetValue("commander", out var leg) && leg == "banned")
            { Reject(Rejection.BannedInCommander); continue; }

            if (inDef.GameChanger && request.Bracket < 4)
            { Reject(Rejection.AboveBracket); continue; }

            try
            {
                // Add first, then remove: if the add fails the deck is unchanged rather
                // than left a card short.
                var printings = await _scryfall.GetPrintingsAsync(inDef.OracleId);
                await _collection.AddCardToCollectionAsync(deckId, userId, new AddCardToCollectionRequest(
                    OracleId: inDef.OracleId,
                    ScryfallId: printings.FirstOrDefault()?.ScryfallId,
                    Quantity: 1,
                    QuantityFoil: 0,
                    Board: "main"));

                await _collection.RemoveCardByOracleAsync(deckId, outOracleId, userId);

                addedNames.Add(inDef.Name);
                inDeck.Remove(swap.Out);
                applied.Add(new CardSwapDto { Out = swap.Out, In = inDef.Name, Why = swap.Why });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Refine: failed to swap '{Out}' for '{In}'", swap.Out, swap.In);
                Reject(Rejection.AddFailed);
            }
        }

        var after = await _collection.GetDeckAsync(deckId, userId);
        int sizeAfter = after?.Cards.Where(c => (c.Board ?? "main") == "main").Sum(c => c.Quantity) ?? sizeBefore;

        if (sizeAfter != sizeBefore)
        {
            _logger.LogWarning(
                "Refine changed deck size for {Commander}: {Before} -> {After}",
                cmdDef.Name, sizeBefore, sizeAfter);
        }

        _logger.LogInformation(
            "Refined {Commander}: {Applied}/{Proposed} swaps applied{Rejected}",
            cmdDef.Name, applied.Count, proposed.Length,
            rejected.Count == 0 ? "" : $", rejected {string.Join(", ", rejected.Select(kv => $"{kv.Key}={kv.Value}"))}");

        return new AiRefineResultDto
        {
            Swaps = [.. applied],
            RejectedByReason = rejected,
            DeckSizeBefore = sizeBefore,
            DeckSizeAfter = sizeAfter,
        };
    }

    private sealed record ProposedSwap(string Out, string In, string Why);

    private async Task<ProposedSwap[]> CallRefineAsync(
        string commanderName, string commanderText, string colors,
        string[] deckCards, AiRefineRequest request, CandidatePool pool)
    {
        var bracketDesc = DescribeBracket(request.Bracket);
        var priceDesc = DescribePrice(request.PriceRange);

        var poolBlock = pool.IsUsable
            ? $"\nReplacements must come from this legal pool ({pool.LegalCount} cards, grouped by role):\n\n" +
              string.Join("\n\n", pool.LegalByRole.Select(kv =>
                  $"{CardRoleClassifier.Label(kv.Key)} ({kv.Value.Length}):\n{string.Join(", ", kv.Value)}"))
            : string.Empty;

        var prompt = $$"""
            You are a Magic: The Gathering Commander/EDH expert improving an existing deck.

            Commander: {{commanderName}}
            Oracle text: {{commanderText}}
            Color identity: {{colors}}

            ── POWER LEVEL ──────────────────────────────────────────────
            {{bracketDesc}}

            ── PRICE ────────────────────────────────────────────────────
            {{priceDesc}}
            {{poolBlock}}

            ── CURRENT DECK ({{deckCards.Length}} non-basic cards) ──────
            {{string.Join(", ", deckCards)}}

            Identify the weakest cards in this deck and replace them with better ones.
            A card is weak if it does little for {{commanderName}}'s game plan, duplicates an
            effect the deck already does better, or is simply outclassed by an available option.

            Propose AT MOST {{request.MaxSwaps}} swaps. Fewer is fine — only swap where the
            replacement is a clear improvement. If the deck is already strong, return an empty list.

            Return ONLY a JSON object (no markdown, no explanation):
            {"swaps":[{"out":"<exact card name currently in the deck>","in":"<exact replacement card name>","why":"<one short sentence>"}]}

            Rules:
            - "out" must be a card from the CURRENT DECK list above, spelled exactly as given.
            - "in" must not already be in the deck, and must respect the colour identity {{colors}}
              and the bracket and price constraints above.
            - Do not swap lands for spells or spells for lands — keep the mana base intact.
            - Each "why" under 15 words.
            """;

        var body = new
        {
            model = ModelId,
            max_tokens = 2000,
            temperature = 0,
            messages = new[] { new { role = "user", content = prompt } },
        };

        var http = _httpFactory.CreateClient("AnthropicApi");
        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/messages")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-api-key", _apiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");

        var resp = await http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            _logger.LogError("Anthropic refine {Status}: {Body}", resp.StatusCode, err);
            throw new HttpRequestException($"{resp.StatusCode}: {err}");
        }

        var respJson = await resp.Content.ReadAsStringAsync();
        LogCacheUsage(respJson);
        var parsed = AnthropicResponse.DeserializeJson<RefineJson>(respJson);

        return [.. (parsed?.Swaps ?? []).Select(s => new ProposedSwap(s.Out, s.In, s.Why))];
    }

    private sealed class RefineJson
    {
        [System.Text.Json.Serialization.JsonPropertyName("swaps")]
        public RefineSwapJson[] Swaps { get; set; } = [];
    }

    private sealed class RefineSwapJson
    {
        [System.Text.Json.Serialization.JsonPropertyName("out")] public string Out { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("in")] public string In { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("why")] public string Why { get; set; } = string.Empty;
    }

    // ---- Candidate pool -----------------------------------------------------

    /// <summary>
    /// The cards this build may legally use, plus an optional hint about which of them
    /// are commonly played with this commander.
    /// </summary>
    /// <param name="Legal">
    /// Every Commander-legal card inside the colour identity and allowed at this
    /// bracket -- roughly 7,000 for a mono-colour commander. Filtered on legality only:
    /// nothing is excluded for being unpopular or unusual, so the model keeps the full
    /// search space and can still find off-meta cards.
    /// </param>
    /// <param name="CommonlyPlayed">
    /// A few hundred cards the community plays with this commander. Advisory only --
    /// listing them alongside the full pool gives a quality signal without narrowing
    /// what may be chosen.
    /// </param>
    private sealed record CandidatePool(
        IReadOnlyDictionary<CardRole, string[]> LegalByRole,
        string[] CommonlyPlayed)
    {
        public int LegalCount => LegalByRole.Sum(kv => kv.Value.Length);

        /// <summary>Below this the pool cannot fill a deck, so fall back to unconstrained generation.</summary>
        public bool IsUsable => LegalCount >= 200;

        public static readonly CandidatePool Empty =
            new(new Dictionary<CardRole, string[]>(), []);
    }

    private async Task<CandidatePool> BuildCandidatePoolAsync(
        string commanderName, HashSet<ManaColor> cmdColors, int bracket)
    {
        var legalByRole = await _scryfall.GetLegalCardsByRoleAsync(cmdColors, bracket);
        var legal = legalByRole.SelectMany(kv => kv.Value).ToArray();
        if (legal.Length == 0)
            return CandidatePool.Empty;

        // EDHREC is used as a hint, never as a filter. Restricting selection to it
        // discards ~96% of legal cards and collapses deck variety.
        var commonlyPlayed = Array.Empty<string>();
        try
        {
            var raw = await _edhrec.GetCommanderPoolAsync(commanderName);
            var legalSet = new HashSet<string>(legal, StringComparer.OrdinalIgnoreCase);
            commonlyPlayed = [.. raw.Select(r => r.Name).Where(legalSet.Contains).Distinct(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EDHREC hint unavailable for {Commander}", commanderName);
        }

        _logger.LogInformation(
            "Candidate pool for {Commander}: {Legal} legal cards, {Hint} flagged as commonly played",
            commanderName, legal.Length, commonlyPlayed.Length);

        return new CandidatePool(legalByRole, commonlyPlayed);
    }

    // ---- Card resolution + insertion ----------------------------------------

    /// <summary>Why a proposed card was not added.</summary>
    private static class Rejection
    {
        public const string UnknownCard = "unknown-card";
        public const string ColorIdentity = "color-identity";
        public const string BannedInCommander = "banned-in-commander";
        public const string AboveBracket = "game-changer-above-bracket";
        public const string Duplicate = "duplicate";
        public const string AddFailed = "add-failed";
    }

    /// <param name="names">
    /// Primary picks followed by substitutes. The loop stops once <paramref name="maxCards"/>
    /// is reached, so substitutes cost nothing unless a primary pick was rejected.
    /// </param>
    private async Task<(int Added, int Skipped, Dictionary<string, int> Reasons)> AddCards(
        string[] names, string board, Guid deckId, string userId,
        HashSet<ManaColor> cmdColors, HashSet<string> addedOracleIds, int maxCards, int bracket)
    {
        int added = 0, skipped = 0;
        var reasons = new Dictionary<string, int>();
        void Reject(string reason)
        {
            skipped++;
            reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
        }

        foreach (var name in names)
        {
            if (added >= maxCards)
                break;
            if (string.IsNullOrWhiteSpace(name))
                continue;
            try
            {
                var def = await _scryfall.GetByNameAsync(name);
                if (def is null)
                { Reject(Rejection.UnknownCard); continue; }

                if (cmdColors.Count > 0)
                {
                    bool legal = def.ColorIdentity.All(c => c == ManaColor.Colorless || cmdColors.Contains(c));
                    if (!legal)
                    {
                        if (_logger.IsEnabled(LogLevel.Debug))
                        {
                            _logger.LogDebug(
                                "AI build reject (color): '{Card}' identity [{CardColors}] vs commander [{CmdColors}]",
                                def.Name,
                                string.Join(",", def.ColorIdentity),
                                string.Join(",", cmdColors));
                        }
                        Reject(Rejection.ColorIdentity);
                        continue;
                    }
                }

                if (def.Legalities.TryGetValue("commander", out var leg) && leg == "banned")
                { Reject(Rejection.BannedInCommander); continue; }

                // Hard-enforce bracket: game changers only allowed in bracket 4+
                if (def.GameChanger && bracket < 4)
                { Reject(Rejection.AboveBracket); continue; }

                bool isBasic = BasicLands.Contains(def.Name);
                if (!isBasic && addedOracleIds.Contains(def.OracleId))
                { Reject(Rejection.Duplicate); continue; }

                var printings = await _scryfall.GetPrintingsAsync(def.OracleId);
                var scryfallId = printings.FirstOrDefault()?.ScryfallId;

                await _collection.AddCardToCollectionAsync(deckId, userId, new AddCardToCollectionRequest(
                    OracleId: def.OracleId,
                    ScryfallId: scryfallId,
                    Quantity: 1,
                    QuantityFoil: 0,
                    Board: board
                ));

                addedOracleIds.Add(def.OracleId);
                added++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AI build: failed to add card '{Name}' to {Board}", name, board);
                Reject(Rejection.AddFailed);
            }
        }
        return (added, skipped, reasons);
    }

    // ---- LLM call ---------------------------------------------------

    private sealed record LlmDeckResponse(string[] Main, string[] Side, string[] Maybe, string[] Substitutes);

    private async Task<LlmDeckResponse> CallAnthropicAsync(
        string commanderName, string commanderText, string colors,
        int mainSlots, int bracket, string priceRange,
        bool includeSide, bool includeMaybe,
        string[] recentCardNames,
        CandidatePool pool)
    {
        var bracketDesc = DescribeBracket(bracket);
        var priceDesc = DescribePrice(priceRange);

        // Cap to ~60 names so the prompt doesn't balloon. Sampling is seeded on the
        // request so the same commander + bracket + price always produces a
        // byte-identical prompt, which is what makes prompt caching possible.
        // Note this does not make the *deck* reproducible: measured output overlap
        // between two identical requests is ~50%, because the API does not guarantee
        // identical completions at temperature = 0.
        var recentSpotlight = DeterministicSample.Take(
            recentCardNames, 60, $"{commanderName}|{bracket}|{priceRange}");

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("AI build spotlight ({Count}): {Names}",
                recentSpotlight.Length, string.Join(" | ", recentSpotlight));
        }

        var recentSection = recentSpotlight.Length > 0
            ? $"\nRECENT SETS SPOTLIGHT (from the last 9 months of Magic releases):\n" +
              $"The following cards are real, legally-printable cards from newly released sets. " +
              $"Prioritize including AS MANY of these as make sense for the deck — they are your primary source. " +
              $"You may supplement with older staples when needed.\n" +
              string.Join(", ", recentSpotlight)
            : string.Empty;

        var sideSection = includeSide
            ? $"\n- \"side\": exactly 10 sideboard/tech cards (answers, hate pieces, situational tools)"
            : "";
        var maybeSection = includeMaybe
            ? $"\n- \"maybe\": exactly 10 maybeboard cards (cards you'd consider adding, interesting alternatives)"
            : "";

        // Advisory only — a starting point, deliberately not a restriction.
        var commonlyPlayedSection = pool.CommonlyPlayed.Length > 0
            ? $"\nCOMMONLY PLAYED WITH {commanderName.ToUpperInvariant()} ({pool.CommonlyPlayed.Length}):\n" +
              "These are popular choices and a reasonable starting point, but they are only a hint. " +
              "Any card from the legal pool is equally valid — prefer a less obvious card when it " +
              "genuinely fits this commander better.\n" +
              string.Join(", ", pool.CommonlyPlayed)
            : string.Empty;

        // Every main pick that fails validation (misspelled, off-colour, banned,
        // above-bracket) would otherwise leave a permanent hole in the deck.
        var substituteSection = $"""

            ── SUBSTITUTES ──────────────────────────────────────────────
            Also return a "substitutes" list of exactly {SubstituteCount} additional cards, in priority order.
            Some main-deck picks may be unusable (a misremembered name, a card outside the
            colour identity, or one disallowed by the bracket). Substitutes are drawn on, in
            order, to fill those slots — so the deck must remain coherent if any are used.
            - Cover the same roles as the main deck in roughly the same proportion:
              include lands, ramp, draw, interaction and synergy pieces, not just filler.
            - They must satisfy every constraint above: colour identity {colors}, the bracket
              rules, and the price constraint.
            - Do not repeat anything already in "main".
            """;

        var responseShape = $$"""
              Return ONLY a JSON object in this exact shape (no markdown, no explanation):
              {
                "main": ["Card 1", ... ({{mainSlots}} cards)],{{(includeSide ? "\n  \"side\": [\"Card 1\", ... (10 cards)]," : "")}}{{(includeMaybe ? "\n  \"maybe\": [\"Card 1\", ... (10 cards)]," : "")}}
                "substitutes": ["Card 1", ... ({{SubstituteCount}} cards)]
              }
              """;

        // ---- Cacheable prefix -------------------------------------------------
        // Depends only on (colour identity, bracket, price) -- never on the commander --
        // so every build sharing those values sends byte-identical text here and hits
        // Anthropic's prompt cache. The legal pool is ~39k tokens, which is what makes
        // caching worth doing: uncached it is ~$0.12/build, cached ~$0.012.
        var legalPoolBlock = pool.IsUsable
            ? $"\n── LEGAL CARD POOL ({pool.LegalCount} cards) ─────────────────────\n" +
              $"Every card below is Commander-legal, inside the colour identity {colors}, and " +
              $"allowed at this bracket. This list is filtered for legality ONLY — it is not a " +
              $"recommendation list, and it deliberately includes obscure and situational cards.\n" +
              $"Choose freely from it. Do not use any card that is not on this list (basic lands " +
              $"excepted); anything else will be rejected as illegal.\n" +
              $"Cards are grouped by the role they usually fill, to help you hit the composition " +
              $"targets below. The grouping is a guide, not a rule — a card can serve a different " +
              $"purpose in the right deck.\n\n" +
              string.Join("\n\n", pool.LegalByRole.Select(kv =>
                  $"{CardRoleClassifier.Label(kv.Key)} ({kv.Value.Length}):\n{string.Join(", ", kv.Value)}"))
            : string.Empty;

        var stablePrefix = $$"""
            You are a Magic: The Gathering Commander/EDH deck-building expert.

            ── POWER LEVEL ──────────────────────────────────────────────
            {{bracketDesc}}

            ── PRICE ────────────────────────────────────────────────────
            {{priceDesc}}
            {{legalPoolBlock}}
            """;

        // ---- Variable remainder (after the cache breakpoint) ------------------
        var prompt = $$"""

            Build a cohesive, well-thought-out deck for this commander:
            Commander: {{commanderName}}
            Oracle text: {{commanderText}}
            Color identity: {{colors}}
            {{recentSection}}
            {{commonlyPlayedSection}}
            ── DECK COMPOSITION ({{mainSlots}} main-deck cards) ────────
            Every card must earn its slot. Think about what {{commanderName}} wants to do, then build around that.

            Land base (~36–38 lands):
            - Include basic lands matching the color identity (e.g. Forest, Plains)
            - Choose dual lands and utility lands appropriate for the budget and bracket

            Mana ramp (~8–10 cards):
            - Land-fetch spells (Cultivate, Rampant Growth, etc.) and artifacts appropriate to the bracket/price

            Card draw & advantage (~8–10 cards):
            - Spells that refill your hand or generate card advantage; choose based on bracket rules above

            Interaction (~8–10 cards):
            - Targeted removal, counterspells (if in color), and board wipes appropriate for the bracket

            Strategy & synergy (~35–38 cards):
            - Creatures, enchantments, artifacts, and spells that directly advance {{commanderName}}'s game plan
            - Every synergy card should have a clear reason to be in THIS deck specifically

            ── HARD RULES ───────────────────────────────────────────────
            - ALL cards must be legal in Commander format (not banned)
            - ALL cards must fit within the color identity: {{colors}}
            - Use exact official Magic card names (correct spelling and capitalization)
            - No duplicate non-basic-land cards
            - Basic lands may repeat
            - Do NOT include {{commanderName}}
            - Strictly follow the bracket and price constraints above — violations will be rejected
            {{sideSection}}{{maybeSection}}
            {{substituteSection}}

            {{responseShape}}
            """;

        // The prefix must be a pure function of (colours, bracket, price) for the cache
        // to hit. Logging both fingerprints makes a cache-busting regression visible:
        // the prefix hash should repeat across different commanders.
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "AI build prompt: prefix {PrefixChars} chars sha256={PrefixHash}, body {BodyChars} chars sha256={BodyHash}",
                stablePrefix.Length, PromptFingerprint(stablePrefix),
                prompt.Length, PromptFingerprint(prompt));
        }

        var body = new
        {
            model = ModelId,
            max_tokens = 6000,
            temperature = 0,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        // Breakpoint goes on the last block of the stable prefix.
                        new
                        {
                            type = "text",
                            text = stablePrefix,
                            cache_control = new { type = "ephemeral" },
                        },
                        new { type = "text", text = prompt },
                    },
                },
            },
        };

        var http = _httpFactory.CreateClient("AnthropicApi");
        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/messages")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-api-key", _apiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");

        var resp = await http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            _logger.LogError("Anthropic AI-build {Status}: {Body}", resp.StatusCode, err);
            throw new HttpRequestException($"{resp.StatusCode}: {err}");
        }

        var respJson = await resp.Content.ReadAsStringAsync();
        LogCacheUsage(respJson);
        var parsed = AnthropicResponse.DeserializeJson<JsonElement>(respJson);

        string[] ParseArray(string key)
        {
            // Guard on Object: an unparseable response yields a default JsonElement
            // (ValueKind.Undefined), on which TryGetProperty would throw.
            if (parsed.ValueKind == JsonValueKind.Object
                && parsed.TryGetProperty(key, out var el)
                && el.ValueKind == JsonValueKind.Array)
            {
                return el.Deserialize<string[]>(AnthropicResponse.JsonOptions) ?? [];
            }
            return [];
        }

        return new LlmDeckResponse(
            ParseArray("main"), ParseArray("side"), ParseArray("maybe"), ParseArray("substitutes"));
    }

    // ---- Helpers ---------------------------------------------------

    /// <summary>
    /// Reports prompt-cache effectiveness. A read of ~0 across repeated builds with the
    /// same colours and bracket means the prefix is not byte-stable and the cache is
    /// silently doing nothing.
    /// </summary>
    private void LogCacheUsage(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (!doc.RootElement.TryGetProperty("usage", out var usage))
                return;

            int Read(string name) =>
                usage.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;

            int write = Read("cache_creation_input_tokens");
            int hit = Read("cache_read_input_tokens");

            _logger.LogInformation(
                "AI build tokens: cache_read={Hit} cache_write={Write} uncached_input={In} output={Out}",
                hit, write, Read("input_tokens"), Read("output_tokens"));
        }
        catch (JsonException)
        {
            // Diagnostics only; never fail a build over usage reporting.
        }
    }

    /// <summary>Short SHA-256 prefix of the prompt, for cache-stability diagnostics.</summary>
    private static string PromptFingerprint(string prompt)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(prompt));
        return Convert.ToHexString(bytes)[..12];
    }

    private static string FormatColors(HashSet<ManaColor> colors)
    {
        if (colors.Count == 0)
            return "Colorless";
        var parts = new List<string>();
        if (colors.Contains(ManaColor.White))
            parts.Add("White");
        if (colors.Contains(ManaColor.Blue))
            parts.Add("Blue");
        if (colors.Contains(ManaColor.Black))
            parts.Add("Black");
        if (colors.Contains(ManaColor.Red))
            parts.Add("Red");
        if (colors.Contains(ManaColor.Green))
            parts.Add("Green");
        if (colors.Contains(ManaColor.Colorless) && parts.Count == 0)
            parts.Add("Colorless");
        return string.Join(", ", parts);
    }

    private static string DescribeBracket(int bracket) => bracket switch
    {
        1 => """
             Bracket 1 (Casual):
             - NO tutors of any kind (no Demonic Tutor, Vampiric Tutor, Enlightened Tutor, Worldly Tutor, etc.)
             - NO stax pieces (no Rhystic Study, Smothering Tithe, Esper Sentinel)
             - NO mass land denial, no 2-card infinite combos, no free spells
             - NO game changer cards whatsoever
             - Focus: fun synergies, flavorful cards, creatures that do interesting things
             - Example good cards: Cultivate, Commander's Sphere, Reclamation Sage, Divination
             """,
        2 => """
             Bracket 2 (Core):
             - NO game changer cards (these include: Sol Ring, Rhystic Study, Smothering Tithe, Consecrated Sphinx, Cyclonic Rift, Demonic Tutor, Vampiric Tutor, Mana Crypt, Jeweled Lotus, Doubling Season, Toxrill, Vorinclex, etc.)
             - Limited tutors only (Cultivate and Kodama's Reach for land are fine; no tutor-any-card spells)
             - No stax, no mass land denial, no fast mana rocks beyond Arcane Signet and Fellwar Stone
             - Focus: solid creatures, good removal, fair card draw like Phyrexian Arena or Read the Bones
             """,
        3 => """
             Bracket 3 (Upgraded):
             - NO game changer cards. The game changer list includes but is not limited to: Sol Ring, Mana Crypt, Jeweled Lotus, Rhystic Study, Smothering Tithe, Consecrated Sphinx, Cyclonic Rift, Demonic Tutor, Vampiric Tutor, Mystical Tutor, Enlightened Tutor, Worldly Tutor, Doubling Season, Parallel Lives, Vorinclex Monstrous Raider, Toxrill the Corrosive, Elesh Norn Grand Cenobite, Jin-Gitaxias, Omniscience, Tooth and Nail
             - Tutors that find land (Cultivate, Kodama's Reach, Farseek, Nature's Lore) are fine
             - Good staples that ARE allowed: Arcane Signet, Commander's Sphere, Counterspell, Swords to Plowshares, Path to Exile, Sylvan Library, Fierce Guardianship, Heroic Intervention, Teferi's Protection
             - Focus: synergistic creatures and spells that advance the commander's strategy
             """,
        4 => """
             Bracket 4 (Optimized):
             - Game changer cards ARE allowed and encouraged where they fit the strategy
             - Include efficient tutors, strong combo pieces, powerful staples
             - Fast mana (Sol Ring, Mana Vault, Chrome Mox) is appropriate
             - Near-competitive but not full cEDH
             """,
        5 => """
             Bracket 5 (cEDH):
             - Maximum efficiency — include every legal game changer that fits
             - Free spells (Force of Will, Fierce Guardianship, Deflecting Swat)
             - Fast mana (Mana Crypt, Jeweled Lotus, Chrome Mox, Mox Diamond)
             - Best tutors and fastest win conditions available
             """,
        _ => "Bracket 3 (Upgraded): Standard Commander experience without game changers.",
    };

    private static string DescribePrice(string priceRange) => priceRange switch
    {
        "budget" => """
                    PRICE CONSTRAINT — Budget (under $100 total):
                    - Individual cards should cost under $3 each
                    - FORBIDDEN: fetch lands (Scalding Tarn, Verdant Catacombs, etc.), shock lands (Blood Crypt, Breeding Pool, etc.), original dual lands, Mana Crypt, Mana Vault, Demonic Tutor, Vampiric Tutor, Rhystic Study, Smothering Tithe
                    - GOOD budget lands: Guildgates, bounce lands (Dimir Aqueduct), basics, Terramorphic Expanse, Evolving Wilds, Command Tower
                    - GOOD budget ramp: Cultivate, Kodama's Reach, Arcane Signet, Commander's Sphere, Wayfarer's Bauble
                    """,
        "mid" => """
                    PRICE CONSTRAINT — Mid-range (under $500 total):
                    - Most cards should be under $20; a few can reach $30
                    - FORBIDDEN: original dual lands (Underground Sea, Tropical Island, etc.), Mana Crypt, Jeweled Lotus, Mox Diamond, Chrome Mox
                    - ALLOWED: shock lands (Breeding Pool, Blood Crypt), fetch lands, Demonic Tutor, Vampiric Tutor, Rhystic Study (if bracket allows)
                    - Mix expensive staples sparingly with efficient mid-range cards
                    """,
        _ => "PRICE CONSTRAINT: None — use the best cards available for the strategy.",
    };
}
