using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Api.Dtos;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

/// <summary>How a card is judged. Doctrine §10.2.</summary>
public enum ScoringMode
{
    /// <summary>
    /// Against a well-built hypothetical version of this commander's deck. Stable and
    /// comparable between cards; the right mode for browsing a pool.
    /// </summary>
    Ideal,

    /// <summary>
    /// Against the deck as it stands, gaps included. Scores move as the deck changes,
    /// which is what makes it answer "what should I add next?".
    /// </summary>
    DeckAware,
}

public interface ISynergyService
{
    Task<SynergyResultDto> GetSynergyAsync(SynergyRequest request);

    /// <summary>
    /// Scores every main-deck card against the commander in one call, and persists the
    /// results to the same cache the single-card path reads.
    /// </summary>
    Task<DeckScoreDto> ScoreDeckAsync(Guid deckId, string userId);

    /// <summary>
    /// Scores an arbitrary set of cards against a commander, reading the cache first and
    /// asking the model only about the ones missing from it.
    /// </summary>
    /// <param name="profile">
    /// The deck being built, when there is one. Required for <see cref="ScoringMode.DeckAware"/>;
    /// ignored otherwise.
    /// </param>
    /// <param name="focus">
    /// Themes the player has explicitly asked to build around. Without these the scorer
    /// judges only against the commander's printed text, which is why a card satisfying a
    /// numeric requirement could outscore a card of the tribe the player actually asked for.
    /// </param>
    Task<ScoredCardDto[]> ScoreCardsAsync(
        string commanderOracleId,
        IReadOnlyList<string> cardOracleIds,
        ScoringMode mode = ScoringMode.Ideal,
        DeckProfile? profile = null,
        IReadOnlyList<string>? focus = null);
}

public sealed class SynergyService : ISynergyService
{
    private readonly MtgEngineDbContext _db;
    private readonly ICollectionService _collection;
    private readonly IScryfallService _scryfall;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ICommanderDoctrine _doctrine;
    private readonly ICommanderAnalysis _analysis;
    private readonly string _apiKey;
    private readonly ILogger<SynergyService> _logger;

    private const string ModelId = "claude-haiku-4-5-20251001";

    // v4: prompts now carry the deckbuilding doctrine as a cached system prefix, and the
    //     fact sheet covers colours, mana, keywords, tokens and archetype fit rather than
    //     only power thresholds and creature types. Scores are not comparable with v3.
    // v5: reasons are written for players rather than developers -- no doctrine section
    //     numbers -- and the prompt forbids attributing a neighbouring card's ability to
    //     the commander, which had produced "triggers double via commander" for a
    //     commander with no such ability.
    private const string CacheVersionBase = "claude-haiku-4-5-20251001-doctrine-v5";

    public SynergyService(
        MtgEngineDbContext db,
        ICollectionService collection,
        IScryfallService scryfall,
        IHttpClientFactory httpFactory,
        ICommanderDoctrine doctrine,
        ICommanderAnalysis analysis,
        IConfiguration config,
        ILogger<SynergyService> logger)
    {
        _db = db;
        _collection = collection;
        _scryfall = scryfall;
        _httpFactory = httpFactory;
        _doctrine = doctrine;
        _analysis = analysis;
        _apiKey = SecretConfig.AnthropicApiKey(config);
        _logger = logger;
    }

    /// <summary>
    /// Cache identity for a scoring run. Deck-aware scores depend on the deck's shape, so
    /// the profile's gap signature is folded in — two decks with the same roles, archetypes
    /// and gaps genuinely deserve the same answer, which makes this far more reusable than
    /// keying on the card list would be.
    /// </summary>
    private static string CacheVersionFor(
        ScoringMode mode, DeckProfile? profile, IReadOnlyList<string>? focus)
    {
        var shape = mode == ScoringMode.DeckAware && profile is not null
            ? $"deckaware-{Hash(profile.GapSignature())}"
            : "ideal";

        // The focus changes the answer, so it has to change the key. Without this, asking
        // for a wolf build and then a token build would serve each other's scores.
        var themes = NormaliseFocus(focus);
        if (themes.Count > 0)
            shape += $"-focus-{Hash(string.Join(",", themes))}";

        return $"{CacheVersionBase}-{shape}";
    }

    /// <summary>Lower-cased, de-duplicated and sorted, so tag order cannot miss the cache.</summary>
    private static List<string> NormaliseFocus(IReadOnlyList<string>? focus) =>
        [.. (focus ?? [])
            .Select(f => f.Trim().ToLowerInvariant())
            .Where(f => f.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(f => f, StringComparer.Ordinal)];

    private static string Hash(string s) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..8];

    /// <summary>The version the legacy single-card path reads and writes.</summary>
    private static string CacheVersion => $"{CacheVersionBase}-ideal";

    public async Task<SynergyResultDto> GetSynergyAsync(SynergyRequest request)
    {
        var cached = await _db.CardSynergyScores.FirstOrDefaultAsync(s =>
            s.CommanderOracleId == request.CommanderOracleId &&
            s.CardOracleId == request.CardOracleId &&
            s.ModelVersion == CacheVersion);

        if (cached != null)
            return new SynergyResultDto { Score = cached.Score, Reason = cached.Reason };

        var result = await CallAnthropicAsync(request);

        var entity = new CardSynergyScore
        {
            CommanderOracleId = request.CommanderOracleId,
            CardOracleId = request.CardOracleId,
            Score = result.Score,
            Reason = result.Reason,
            ModelVersion = CacheVersion,
        };

        // Upsert — a stale entry from an older cache version may already exist for this pair
        var stale = await _db.CardSynergyScores.FirstOrDefaultAsync(s =>
            s.CommanderOracleId == request.CommanderOracleId &&
            s.CardOracleId == request.CardOracleId);

        if (stale != null)
        {
            stale.Score = result.Score;
            stale.Reason = result.Reason;
            stale.ModelVersion = CacheVersion;
            stale.CreatedAt = DateTime.UtcNow;
        }
        else
        {
            _db.CardSynergyScores.Add(entity);
        }

        try
        { await _db.SaveChangesAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to cache synergy score"); }

        return result;
    }

    // ---- Batched deck scoring ---------------------------------------

    public async Task<DeckScoreDto> ScoreDeckAsync(Guid deckId, string userId)
    {
        var deck = await _collection.GetDeckAsync(deckId, userId)
            ?? throw new ResourceNotFoundException($"Deck not found: {deckId}");

        if (string.IsNullOrWhiteSpace(deck.CommanderOracleId))
            throw new InvalidResourceStateException("Deck has no commander to score against.");

        var cmdDef = await _scryfall.GetByOracleIdAsync(deck.CommanderOracleId)
            ?? throw new ResourceNotFoundException($"Commander not found: {deck.CommanderOracleId}");

        // Score distinct non-commander main-deck cards. Basic lands are excluded:
        // scoring 30 Swamps says nothing useful and would dominate the average.
        var cards = deck.Cards
            .Where(c => (c.Board ?? "main") == "main"
                        && !string.Equals(c.OracleId, deck.CommanderOracleId, StringComparison.OrdinalIgnoreCase)
                        && c.CardDetails is not null
                        && !c.CardDetails.Supertypes.Contains("Basic"))
            .GroupBy(c => c.OracleId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(c => c.CardDetails!.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (cards.Length == 0)
            return new DeckScoreDto();

        // Reviewing a built deck is the deck-aware case by definition: the whole point is
        // "what is wrong with what I have", which needs the gaps.
        var profile = DeckProfile.Build(
            deck.Cards.Where(c => c.CardDetails is not null).Select(c => DeckProfile.From(c.CardDetails!)));

        var mode = profile.IsTooSmallForGapAnalysis ? ScoringMode.Ideal : ScoringMode.DeckAware;

        var requirements = await _analysis.AnalyseAsync(cmdDef);

        var scores = await ScoreAllAsync(
            CommanderContext.From(cmdDef, requirements),
            [.. cards.Select(c => ToFactCard(c.OracleId, c.CardDetails!))],
            mode,
            mode == ScoringMode.DeckAware ? profile : null,
            null);

        var scored = cards
            .Select(c =>
            {
                var name = c.CardDetails!.Name;
                scores.TryGetValue(name, out var s);
                return new ScoredCardDto
                {
                    OracleId = c.OracleId,
                    Name = name,
                    Score = Math.Clamp(s?.Score ?? 0, 0, 100),
                    Reason = s?.Reason ?? string.Empty,
                };
            })
            .Where(s => s.Score > 0)   // unscored cards would drag the average to zero
            .ToArray();

        await PersistScoresAsync(deck.CommanderOracleId, scored, CacheVersionFor(mode, profile, null));

        return new DeckScoreDto
        {
            Cards = scored,
            AverageScore = scored.Length == 0 ? 0 : (int)Math.Round(scored.Average(s => s.Score)),
            WeakestCards = [.. scored.Where(s => s.Score < DeckScoreDto.WeakThreshold).OrderBy(s => s.Score)],
        };
    }

    /// <summary>Deck rows carry the DTO rather than the definition; same card, other shape.</summary>
    private static CardFactSheet.FactCard ToFactCard(string oracleId, CardDto d) => new(
        oracleId, d.Name, d.ManaCost, d.ManaValue, d.OracleText,
        CardFactSheet.TypeLineOf(d.Supertypes, d.CardTypes.Select(t => t.ToString()), d.Subtypes),
        d.CardTypes.Aggregate(CardType.None, (acc, t) =>
            Enum.TryParse<CardType>(t.ToString(), out var parsed) ? acc | parsed : acc),
        d.Subtypes, d.Power, d.Toughness,
        [.. d.ColorIdentity.Select(MapColour)], d.Keywords, d.GameChanger);

    private static ManaColor MapColour(ManaColorDto c) => c switch
    {
        ManaColorDto.W => ManaColor.White,
        ManaColorDto.U => ManaColor.Blue,
        ManaColorDto.B => ManaColor.Black,
        ManaColorDto.R => ManaColor.Red,
        ManaColorDto.G => ManaColor.Green,
        _ => ManaColor.Colorless,
    };

    private static string? PtOf(int? power, int? toughness) =>
        power is null && toughness is null ? null : $"{power?.ToString() ?? "?"}/{toughness?.ToString() ?? "?"}";

    public async Task<ScoredCardDto[]> ScoreCardsAsync(
        string commanderOracleId,
        IReadOnlyList<string> cardOracleIds,
        ScoringMode mode = ScoringMode.Ideal,
        DeckProfile? profile = null,
        IReadOnlyList<string>? focus = null)
    {
        var wanted = cardOracleIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (wanted.Count == 0)
            return [];

        var cmdDef = await _scryfall.GetByOracleIdAsync(commanderOracleId)
            ?? throw new ResourceNotFoundException($"Commander not found: {commanderOracleId}");

        // Doctrine §10.2: gap analysis on a near-empty deck is noise, so fall back to the
        // stable mode rather than reporting confident nonsense about what is "missing".
        if (mode == ScoringMode.DeckAware && (profile is null || profile.IsTooSmallForGapAnalysis))
        {
            _logger.LogInformation(
                "Deck-aware scoring requested for {Commander} but the deck is too small; using ideal mode",
                cmdDef.Name);
            mode = ScoringMode.Ideal;
            profile = null;
        }

        var version = CacheVersionFor(mode, profile, focus);

        // Cache first. Browsing pages the same cards past repeatedly, and a page that is
        // fully cached must not cost an API call.
        var cached = await _db.CardSynergyScores
            .Where(s => s.CommanderOracleId == commanderOracleId
                        && s.ModelVersion == version
                        && wanted.Contains(s.CardOracleId))
            .ToDictionaryAsync(s => s.CardOracleId, StringComparer.OrdinalIgnoreCase);

        var results = new List<ScoredCardDto>(wanted.Count);
        var missing = new List<CardFactSheet.FactCard>();

        foreach (var id in wanted)
        {
            if (cached.TryGetValue(id, out var hit))
            {
                results.Add(new ScoredCardDto
                {
                    OracleId = id,
                    Name = (await _scryfall.GetByOracleIdAsync(id))?.Name ?? string.Empty,
                    Score = hit.Score,
                    Reason = hit.Reason,
                });
                continue;
            }

            var def = await _scryfall.GetByOracleIdAsync(id);
            if (def is not null)
                missing.Add(CardFactSheet.From(id, def));
        }

        if (missing.Count > 0)
        {
            var requirements = await _analysis.AnalyseAsync(cmdDef);
            var scores = await ScoreAllAsync(
                CommanderContext.From(cmdDef, requirements), missing, mode, profile, focus);

            var fresh = missing
                .Select(c =>
                {
                    scores.TryGetValue(c.Name, out var s);
                    return new ScoredCardDto
                    {
                        OracleId = c.OracleId,
                        Name = c.Name,
                        Score = Math.Clamp(s?.Score ?? 0, 0, 100),
                        Reason = s?.Reason ?? string.Empty,
                    };
                })
                .Where(s => s.Score > 0)
                .ToArray();

            await PersistScoresAsync(commanderOracleId, fresh, version);
            results.AddRange(fresh);
        }

        _logger.LogInformation(
            "Scored {Total} cards for {Commander} in {Mode} mode (focus: {Focus}): {Cached} cached, {Fresh} from the model",
            wanted.Count, cmdDef.Name, mode,
            NormaliseFocus(focus) is { Count: > 0 } f ? string.Join("/", f) : "none",
            cached.Count, missing.Count);

        return [.. results];
    }

    /// <summary>The commander half of the prompt, gathered once and passed around whole.</summary>
    private readonly record struct CommanderContext(
        string Name, string Text, string TypeLine, string? Pt,
        CommanderRequirements Requirements)
    {
        public static CommanderContext From(CardDefinition d, CommanderRequirements req) => new(
            d.Name, d.OracleText ?? string.Empty,
            CardFactSheet.TypeLineOf(d.Supertypes, CardFactSheet.TypeNames(d.CardTypes), d.Subtypes),
            PtOf(d.Power, d.Toughness), req);
    }

    // A single call generating 30 reasons took 11 seconds, all of it spent emitting
    // tokens one after another. Splitting it into smaller calls that run at the same
    // time cuts that to roughly a third. This is only sound because a card is judged
    // against the commander's requirements rather than against whichever cards happen
    // to share its batch -- so which chunk it lands in cannot change its score.
    private const int ScoringChunkSize = 8;

    // Raised from 4: a suggestions run scores ~120 unique candidates, which is 15 chunks.
    // At 4 at a time that is four sequential rounds; at 8 it is two.
    private const int MaxConcurrentScoringCalls = 8;

    private async Task<Dictionary<string, SynergyJson>> ScoreAllAsync(
        CommanderContext cmd,
        IReadOnlyList<CardFactSheet.FactCard> cards,
        ScoringMode mode,
        DeckProfile? profile,
        IReadOnlyList<string>? focus)
    {
        // What the commander requires is read from its text once and cached per commander,
        // so the same facts reach every chunk. Deriving it here rather than per chunk also
        // means a card cannot get different facts depending on which chunk it fell into.
        var req = cmd.Requirements;

        var chunks = cards.Chunk(ScoringChunkSize).ToArray();
        if (chunks.Length == 1)
            return await CallDeckScoringAsync(cmd, req, chunks[0], mode, profile, focus);

        using var gate = new SemaphoreSlim(MaxConcurrentScoringCalls);
        var parts = await Task.WhenAll(chunks.Select(async chunk =>
        {
            await gate.WaitAsync();
            try { return await CallDeckScoringAsync(cmd, req, chunk, mode, profile, focus); }
            finally { gate.Release(); }
        }));

        var merged = new Dictionary<string, SynergyJson>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts)
            foreach (var kv in part)
                merged[kv.Key] = kv.Value;

        return merged;
    }

    private async Task<Dictionary<string, SynergyJson>> CallDeckScoringAsync(
        CommanderContext cmd,
        CommanderRequirements req,
        IReadOnlyList<CardFactSheet.FactCard> cards,
        ScoringMode mode,
        DeckProfile? profile,
        IReadOnlyList<string>? focus)
    {
        var commanderName = cmd.Name;
        var commanderPt = cmd.Pt is null ? "" : $" ({cmd.Pt})";

        // Include each card's rules text. Given names alone the model describes cards
        // from memory and gets them wrong -- it claimed Goblin Bombardment sacrifices
        // treasures when it sacrifices a creature.
        var cardList = string.Join("\n", cards.Select(c =>
        {
            var text = string.IsNullOrWhiteSpace(c.OracleText)
                ? "(no rules text)"
                : c.OracleText.Replace("\n", " ");
            var pt = c.Power is null && c.Toughness is null ? "" : $" | {PtOf(c.Power, c.Toughness)}";
            return $"- {c.Name} | {c.ManaCost} | {c.TypeLine}{pt} | {text}{CardFactSheet.For(c, req, profile)}";
        }));

        var deckSection = mode == ScoringMode.DeckAware && profile is not null
            ? $"""

               DECK PROFILE -- the deck as it stands. Score against this, gaps included
               (doctrine §10.2, deck-aware mode):
               {profile.Describe()}
               """
            : """

              No deck profile: score against a well-built hypothetical version of this
              commander's deck, ignoring current contents (doctrine §10.2, ideal mode).
              """;

        var themes = NormaliseFocus(focus);
        var focusSection = themes.Count == 0
            ? ""
            : $"""

               PLAYER'S DECLARED FOCUS: {string.Join(", ", themes)}
               The player has explicitly asked to build around this. It is a stated goal of
               the deck, so treat it exactly as doctrine §8 T2 archetype synergy -- a card
               that advances it is on-plan even when the commander's own text never mentions
               it, and a card that does not is competing for a slot the player wants spent
               elsewhere. Do not let a card outrank the declared focus merely because it
               satisfies a numeric requirement.
               """;

        var prompt = $$"""
            Score how well each card below earns a slot in this deck.

            Commander: {{commanderName}}
            Commander type: {{cmd.TypeLine}}{{commanderPt}}
            Commander oracle text: {{cmd.Text}}
            {{focusSection}}{{deckSection}}

            Cards ({{cards.Count}}), as "name | mana cost | type line | power/toughness | rules text":
            {{cardList}}

            Each card carries a [FACTS: ...] note. Those were checked mechanically and are
            correct -- treat them as settled and never contradict them. They cover only what
            is checkable; everything else about the card is yours to judge.

            Respond with ONLY valid JSON in exactly this shape (no markdown, no extra text):
            {"scores":[{"name":"<exact card name as given>","score":<integer 0-100>,"tier":"<T1-T6>","role":"<role>","reason":"<one short sentence>"}]}

            Rules:
            - Include every card listed above, exactly once, using the name exactly as given.
            - Apply the doctrine you were given: score per §10.3, tier per §8, role per §10.4.
            - Obey the anti-patterns in §9 without exception. In particular: never credit a
              card with an ability it does not have, never assert an interaction with no
              rules basis, never call a requirement the commander names "generic", and never
              dismiss mana, fixing or draw as filler merely because every deck wants it.
            - THE COMMANDER'S ABILITIES ARE EXACTLY THE ORACLE TEXT ABOVE AND NOTHING MORE.
              The other cards in this list are not the commander. Never write that something
              is doubled, copied, untapped, tutored or otherwise changed "by the commander"
              unless the commander's own text says so. An ability you read on a neighbouring
              card in this list belongs to that card alone.
            - Judge against the deck, not only the commander's text (§9.5).
            - The reason is shown to a player, not to a developer. Plain English: name the
              card's own ability and what it does for this deck. No section numbers, no tier
              codes, no references to this document. Under 18 words.
            """;

        var body = new
        {
            model = ModelId,
            // ~99 cards x (name + score + tier + role + short reason); 8k leaves headroom.
            max_tokens = 8000,
            temperature = 0,

            // The doctrine is byte-identical on every request, so it goes in a cached
            // system prefix rather than the message. Anthropic bills cache reads at a
            // fraction of the input rate and skips re-processing them, which is most of
            // why adding several thousand tokens of standard to every call is affordable.
            system = new object[]
            {
                new
                {
                    type = "text",
                    text = _doctrine.Text,
                    cache_control = new { type = "ephemeral" },
                },
            },
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
            var errBody = await resp.Content.ReadAsStringAsync();
            _logger.LogError("Anthropic deck-scoring {Status}: {Body}", resp.StatusCode, errBody);
            throw new AiUpstreamException("Anthropic", resp.StatusCode, errBody);
        }

        var respJson = await resp.Content.ReadAsStringAsync();
        LogCacheUsage(respJson);
        var parsed = AnthropicResponse.DeserializeJson<DeckScoreJson>(respJson);

        var map = new Dictionary<string, SynergyJson>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in parsed?.Scores ?? [])
            if (!string.IsNullOrWhiteSpace(s.Name))
                map[s.Name] = new SynergyJson { Score = s.Score, Reason = s.Reason };

        if (map.Count < cards.Count)
        {
            _logger.LogWarning(
                "Deck scoring returned {Got}/{Expected} cards for {Commander}",
                map.Count, cards.Count, commanderName);
        }

        return map;
    }

    /// <summary>Writes batch results into the same cache the single-card path reads.</summary>
    private async Task PersistScoresAsync(
        string commanderOracleId, ScoredCardDto[] scored, string version)
    {
        try
        {
            // List, not array: on .NET 10 `string[].Contains` binds to the span-based
            // MemoryExtensions overload, which EF cannot translate to SQL.
            var cardIds = scored.Select(s => s.OracleId).ToList();

            // Scoped to this version. Without it, writing a deck-aware score would
            // overwrite the ideal score for the same card.
            var existing = await _db.CardSynergyScores
                .Where(s => s.CommanderOracleId == commanderOracleId
                            && s.ModelVersion == version
                            && cardIds.Contains(s.CardOracleId))
                .ToDictionaryAsync(s => s.CardOracleId, StringComparer.OrdinalIgnoreCase);

            foreach (var s in scored)
            {
                if (existing.TryGetValue(s.OracleId, out var row))
                {
                    row.Score = s.Score;
                    row.Reason = Truncate(s.Reason, 500);
                    row.CreatedAt = DateTime.UtcNow;
                }
                else
                {
                    _db.CardSynergyScores.Add(new CardSynergyScore
                    {
                        CommanderOracleId = commanderOracleId,
                        CardOracleId = s.OracleId,
                        Score = s.Score,
                        Reason = Truncate(s.Reason, 500),
                        ModelVersion = version,
                    });
                }
            }

            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Persisting is a cache warm-up; never fail the caller over it.
            _logger.LogWarning(ex, "Failed to persist batch synergy scores");
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max];

    /// <summary>
    /// Reports whether the doctrine prefix was served from Anthropic's cache. Worth logging
    /// rather than assuming: a prefix under the minimum length is accepted silently and
    /// simply never cached, so the only way to know it is working is to look.
    /// </summary>
    private void LogCacheUsage(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (!doc.RootElement.TryGetProperty("usage", out var usage)) return;

            int Read(string name) =>
                usage.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;

            var created = Read("cache_creation_input_tokens");
            var readFromCache = Read("cache_read_input_tokens");

            if (created == 0 && readFromCache == 0)
            {
                _logger.LogWarning(
                    "Doctrine prefix was neither cached nor read from cache ({Tokens} input tokens). " +
                    "It is ~{Approx} tokens; Anthropic needs {Min} to cache",
                    Read("input_tokens"), _doctrine.ApproximateTokens, CommanderDoctrine.MinCacheableTokens);
                return;
            }

            _logger.LogInformation(
                "Prompt cache: {Read} read, {Created} written, {Fresh} uncached input tokens",
                readFromCache, created, Read("input_tokens"));
        }
        catch (JsonException)
        {
            // Usage reporting is diagnostics; never let it break scoring.
        }
    }

    private sealed class DeckScoreJson
    {
        [JsonPropertyName("scores")] public DeckScoreEntry[] Scores { get; set; } = [];
    }

    private sealed class DeckScoreEntry
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("score")] public int Score { get; set; }
        [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
    }

    // ---- Single-card scoring ----------------------------------------

    private async Task<SynergyResultDto> CallAnthropicAsync(SynergyRequest req)
    {
        var deckContext = req.DeckCardNames.Length > 0
            ? $"\n\nOther cards already in the deck ({req.DeckCardNames.Length}):\n{string.Join(", ", req.DeckCardNames)}"
            : string.Empty;

        var prompt = $$"""
            You are a Magic: The Gathering Commander/EDH expert. Evaluate how well a card fits into a specific deck.

            Commander (primary focus): {{req.CommanderName}}
            Commander oracle text: {{req.CommanderText}}{{deckContext}}

            Card to evaluate: {{req.CardName}}
            Card oracle text: {{req.CardText}}

            Score how well this card fits the deck. The commander's strategy is the most important factor, but also consider how the card supports or complements the other cards already in the deck.

            Respond with ONLY valid JSON in exactly this format (no markdown, no extra text):
            {"score": <integer 0-100>, "reason": "<one concise sentence explaining the fit>"}

            Where 0 = no synergy whatsoever, 100 = exceptional fit.
            """;

        var body = new
        {
            model = ModelId,
            max_tokens = 256,
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
            var errBody = await resp.Content.ReadAsStringAsync();
            _logger.LogError("Anthropic API {Status}: {Body}", resp.StatusCode, errBody);
            throw new AiUpstreamException("Anthropic", resp.StatusCode, errBody);
        }

        var respJson = await resp.Content.ReadAsStringAsync();
        var parsed = AnthropicResponse.DeserializeJson<SynergyJson>(respJson);

        return new SynergyResultDto
        {
            Score = Math.Clamp(parsed?.Score ?? 0, 0, 100),
            Reason = parsed?.Reason ?? string.Empty,
        };
    }

    private sealed class SynergyJson
    {
        [JsonPropertyName("score")] public int Score { get; set; }
        [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
    }
}
