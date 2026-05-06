using System.Text;
using System.Text.Json;
using MtgEngine.Api.Dtos;
using MtgEngine.Domain.Enums;

namespace MtgEngine.Api.Services;

public interface IAiBuildService
{
    Task<AiBuildResultDto> BuildDeckAsync(Guid deckId, string userId, AiBuildRequest request);
}

public sealed class AiBuildService : IAiBuildService
{
    private readonly IScryfallService     _scryfall;
    private readonly ICollectionService   _collection;
    private readonly IHttpClientFactory   _httpFactory;
    private readonly string               _apiKey;
    private readonly ILogger<AiBuildService> _logger;

    private const string ModelId = "claude-sonnet-4-6";

    private static readonly HashSet<string> BasicLands = new(StringComparer.OrdinalIgnoreCase)
        { "Plains", "Island", "Swamp", "Mountain", "Forest",
          "Wastes", "Snow-Covered Plains", "Snow-Covered Island",
          "Snow-Covered Swamp", "Snow-Covered Mountain", "Snow-Covered Forest" };

    public AiBuildService(
        IScryfallService scryfall,
        ICollectionService collection,
        IHttpClientFactory httpFactory,
        IConfiguration config,
        ILogger<AiBuildService> logger)
    {
        _scryfall    = scryfall;
        _collection  = collection;
        _httpFactory = httpFactory;
        _apiKey      = config["Anthropic:ApiKey"] ?? throw new InvalidOperationException("Anthropic:ApiKey not configured");
        _logger      = logger;
    }

    public async Task<AiBuildResultDto> BuildDeckAsync(Guid deckId, string userId, AiBuildRequest request)
    {
        var commanderOracleId = request.CommanderOracleId;

        var cmdDef = await _scryfall.GetByOracleIdAsync(commanderOracleId)
            ?? throw new InvalidOperationException($"Commander not found: {commanderOracleId}");

        var cmdColors  = cmdDef.ColorIdentity.ToHashSet();
        var colorNames = FormatColors(cmdColors);

        // Fetch the current deck to know how many main-board slots remain
        var existingDeck = await _collection.GetDeckAsync(deckId, userId);
        var existingCards = existingDeck?.Cards ?? [];

        var addedOracleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { commanderOracleId };

        int existingMainCount = 0;
        foreach (var c in existingCards)
        {
            if ((c.Board ?? "main") != "main") continue;
            if (string.Equals(c.OracleId, commanderOracleId, StringComparison.OrdinalIgnoreCase)) continue;
            existingMainCount += c.Quantity;
            addedOracleIds.Add(c.OracleId);
        }

        int mainSlotsLeft = Math.Max(0, 99 - existingMainCount);

        // Fetch cards from recent sets to feed the LLM as candidates
        var recentSetCodes  = await _scryfall.GetRecentSetCodesAsync(monthsBack: 9);
        var recentCardNames = await _scryfall.GetRecentCardNamesAsync(recentSetCodes, cmdColors);

        var llmResult = await CallAnthropicAsync(
            cmdDef.Name,
            cmdDef.OracleText ?? string.Empty,
            colorNames,
            mainSlotsLeft,
            request.Bracket,
            request.PriceRange,
            request.IncludeSideboard,
            request.IncludeMaybeboard,
            recentCardNames);

        var (mainAdded,  mainSkipped)  = await AddCards(llmResult.Main,  "main",  deckId, userId, cmdColors, addedOracleIds, mainSlotsLeft,  request.Bracket);
        var (sideAdded,  sideSkipped)  = request.IncludeSideboard
            ? await AddCards(llmResult.Side,  "side",  deckId, userId, cmdColors, addedOracleIds, 10, request.Bracket)
            : (0, 0);
        var (maybeAdded, maybeSkipped) = request.IncludeMaybeboard
            ? await AddCards(llmResult.Maybe, "maybe", deckId, userId, cmdColors, addedOracleIds, 10, request.Bracket)
            : (0, 0);

        return new AiBuildResultDto
        {
            CardsAdded      = mainAdded,
            SideboardAdded  = sideAdded,
            MaybeboardAdded = maybeAdded,
            CardsSkipped    = mainSkipped + sideSkipped + maybeSkipped,
        };
    }

    // ---- Card resolution + insertion ----------------------------------------

    private async Task<(int Added, int Skipped)> AddCards(
        string[] names, string board, Guid deckId, string userId,
        HashSet<ManaColor> cmdColors, HashSet<string> addedOracleIds, int maxCards, int bracket)
    {
        int added = 0, skipped = 0;
        foreach (var name in names)
        {
            if (added >= maxCards) break;
            if (string.IsNullOrWhiteSpace(name)) continue;
            try
            {
                var def = await _scryfall.GetByNameAsync(name);
                if (def is null) { skipped++; continue; }

                if (cmdColors.Count > 0)
                {
                    bool legal = def.ColorIdentity.All(c => c == ManaColor.Colorless || cmdColors.Contains(c));
                    if (!legal) { skipped++; continue; }
                }

                if (def.Legalities.TryGetValue("commander", out var leg) && leg == "banned")
                    { skipped++; continue; }

                // Hard-enforce bracket: game changers only allowed in bracket 4+
                if (def.GameChanger && bracket < 4)
                    { skipped++; continue; }

                bool isBasic = BasicLands.Contains(def.Name);
                if (!isBasic && addedOracleIds.Contains(def.OracleId))
                    { skipped++; continue; }

                var printings  = await _scryfall.GetPrintingsAsync(def.OracleId);
                var scryfallId = printings.FirstOrDefault()?.ScryfallId;

                await _collection.AddCardToCollectionAsync(deckId, userId, new AddCardToCollectionRequest(
                    OracleId:     def.OracleId,
                    ScryfallId:   scryfallId,
                    Quantity:     1,
                    QuantityFoil: 0,
                    Board:        board
                ));

                addedOracleIds.Add(def.OracleId);
                added++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AI build: failed to add card '{Name}' to {Board}", name, board);
                skipped++;
            }
        }
        return (added, skipped);
    }

    // ---- LLM call ---------------------------------------------------

    private sealed record LlmDeckResponse(string[] Main, string[] Side, string[] Maybe);

    private async Task<LlmDeckResponse> CallAnthropicAsync(
        string commanderName, string commanderText, string colors,
        int mainSlots, int bracket, string priceRange,
        bool includeSide, bool includeMaybe,
        string[] recentCardNames)
    {
        var bracketDesc = bracket switch {
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

        var priceDesc = priceRange switch {
            "budget" => """
                        PRICE CONSTRAINT — Budget (under $100 total):
                        - Individual cards should cost under $3 each
                        - FORBIDDEN: fetch lands (Scalding Tarn, Verdant Catacombs, etc.), shock lands (Blood Crypt, Breeding Pool, etc.), original dual lands, Mana Crypt, Mana Vault, Demonic Tutor, Vampiric Tutor, Rhystic Study, Smothering Tithe
                        - GOOD budget lands: Guildgates, bounce lands (Dimir Aqueduct), basics, Terramorphic Expanse, Evolving Wilds, Command Tower
                        - GOOD budget ramp: Cultivate, Kodama's Reach, Arcane Signet, Commander's Sphere, Wayfarer's Bauble
                        """,
            "mid"    => """
                        PRICE CONSTRAINT — Mid-range (under $500 total):
                        - Most cards should be under $20; a few can reach $30
                        - FORBIDDEN: original dual lands (Underground Sea, Tropical Island, etc.), Mana Crypt, Jeweled Lotus, Mox Diamond, Chrome Mox
                        - ALLOWED: shock lands (Breeding Pool, Blood Crypt), fetch lands, Demonic Tutor, Vampiric Tutor, Rhystic Study (if bracket allows)
                        - Mix expensive staples sparingly with efficient mid-range cards
                        """,
            _        => "PRICE CONSTRAINT: None — use the best cards available for the strategy.",
        };

        // Cap to ~60 names so the prompt doesn't balloon; shuffle for variety
        var rng = new Random();
        var recentSpotlight = recentCardNames
            .OrderBy(_ => rng.Next())
            .Take(60)
            .ToArray();

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

        var responseShape = includeSide || includeMaybe
            ? $$"""
              Return ONLY a JSON object in this exact shape (no markdown, no explanation):
              {
                "main": ["Card 1", ... ({{mainSlots}} cards)],{{(includeSide ? "\n  \"side\": [\"Card 1\", ... (10 cards)]," : "")}}{{(includeMaybe ? "\n  \"maybe\": [\"Card 1\", ... (10 cards)]" : "")}}
              }
              """
            : $"Return ONLY a JSON object: {{\"main\": [\"Card 1\", ... ({mainSlots} cards)]}}";

        var prompt = $$"""
            You are a Magic: The Gathering Commander/EDH deck-building expert.

            Build a cohesive, well-thought-out deck for this commander:
            Commander: {{commanderName}}
            Oracle text: {{commanderText}}
            Color identity: {{colors}}

            ── POWER LEVEL ──────────────────────────────────────────────
            {{bracketDesc}}

            ── PRICE ────────────────────────────────────────────────────
            {{priceDesc}}
            {{recentSection}}
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

            {{responseShape}}
            """;

        var body = new
        {
            model       = ModelId,
            max_tokens  = 6000,
            temperature = 0,
            messages    = new[] { new { role = "user", content = prompt } },
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
        var doc      = JsonDocument.Parse(respJson);
        var text     = doc.RootElement
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString() ?? "{}";

        text = ExtractJsonObject(text);

        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var parsed = JsonSerializer.Deserialize<JsonElement>(text, opts);

        string[] ParseArray(string key)
        {
            if (parsed.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.Array)
                return el.Deserialize<string[]>(opts) ?? [];
            return [];
        }

        return new LlmDeckResponse(ParseArray("main"), ParseArray("side"), ParseArray("maybe"));
    }

    // ---- Helpers ---------------------------------------------------

    private static string FormatColors(HashSet<ManaColor> colors)
    {
        if (colors.Count == 0) return "Colorless";
        var parts = new List<string>();
        if (colors.Contains(ManaColor.White))  parts.Add("White");
        if (colors.Contains(ManaColor.Blue))   parts.Add("Blue");
        if (colors.Contains(ManaColor.Black))  parts.Add("Black");
        if (colors.Contains(ManaColor.Red))    parts.Add("Red");
        if (colors.Contains(ManaColor.Green))  parts.Add("Green");
        if (colors.Contains(ManaColor.Colorless) && parts.Count == 0) parts.Add("Colorless");
        return string.Join(", ", parts);
    }

    // Extracts the first complete JSON object from text that may contain preamble/postamble.
    private static string ExtractJsonObject(string text)
    {
        int start = text.IndexOf('{');
        if (start < 0) return "{}";

        int depth = 0;
        bool inString = false;
        bool escaped  = false;

        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (escaped)              { escaped = false; continue; }
            if (c == '\\' && inString){ escaped = true;  continue; }
            if (c == '"')             { inString = !inString; continue; }
            if (inString)               continue;
            if (c == '{') depth++;
            else if (c == '}') { if (--depth == 0) return text[start..(i + 1)]; }
        }

        return text[start..]; // malformed — return from '{' to end and let the caller throw
    }
}
