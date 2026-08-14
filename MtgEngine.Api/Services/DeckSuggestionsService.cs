using System.Text;
using System.Text.Json.Serialization;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Mapping;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

/// <summary>
/// Best-effort progress sink for streamed suggestion builds. <paramref name="event"/> is the
/// SSE event name ("stage" | "cards"); <paramref name="payload"/> is serialized as its data.
/// Implementations must swallow their own write failures so a disconnected client never fails
/// the (cacheable) build.
/// </summary>
public delegate Task SuggestionEmit(string @event, object payload);

public interface IDeckSuggestionsService
{
    Task<DeckSuggestionsDto> GetSuggestionsAsync(DeckSuggestionsRequest request);

    /// <summary>
    /// Same result as <see cref="GetSuggestionsAsync"/>, but reports progress through
    /// <paramref name="emit"/> as it goes: coarse "stage" events, then one "cards" event with
    /// the provisional (pre-judge) lists so the UI can render before reasons are grounded.
    /// On a cache hit nothing is emitted — the caller sends the returned result as the final event.
    /// </summary>
    Task<DeckSuggestionsDto> GetSuggestionsStreamAsync(
        DeckSuggestionsRequest request, SuggestionEmit emit, CancellationToken ct = default);
}

public sealed class DeckSuggestionsService : IDeckSuggestionsService
{
    private readonly IScryfallService _scryfall;
    private readonly IAnthropicClient _anthropic;
    private readonly IAiCacheService _cache;
    private readonly ISynergyService _synergy;
    private readonly ICandidateRanking _ranking;
    private readonly ICommanderDoctrine _doctrine;
    private readonly IEdhrecPoolService _edhrec;
    private readonly ILogger<DeckSuggestionsService> _logger;

    private const string ModelId = "claude-haiku-4-5-20251001";

    /// <summary>
    /// The critique/judge pass runs on a stronger model than the scorer and reason writer.
    /// It is the correctness gate — it must catch subtle rules errors like a type-restricted
    /// commander ability ("a Wizard you control") being credited to a card that lacks the
    /// type. Haiku missed those even with the card's full text and type line in front of it;
    /// this is one call per generation, so the extra cost is bounded. Same id the vision
    /// service already uses successfully.
    /// </summary>
    private const string JudgeModelId = "claude-sonnet-4-6";

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
    // v13: category sizes are ceilings rather than quotas, and the judge cuts cards a
    //      good deckbuilder would not run instead of padding the list out.
    // v14: ceilings raised to 8 per category, with max_tokens raised to match. v13 answers
    //      generated between those two changes are empty -- the reply was truncated
    //      mid-JSON and parsed to nothing -- so they must not be served.
    // v15: categories are the head of one ranked pool rather than a separate model pick,
    //      and the judge is given the doctrine so it can catch format-rule violations. It
    //      had passed "tutors your commander", which is true of the card's text and
    //      impossible in Commander.
    // v16: the judge cuts a card whose only honest reason argues against it — a card
    //      shown at a high score for a payoff it cannot reach, with a reason that says so.
    //      A corrected reason must say why to PLAY the card; if it cannot, the card is
    //      weak and is dropped, not kept.
    // v17: doctrine §9.10 — a restricted ability reaches only objects with the characteristic
    //      it names. The judge now cuts a hook that credits the commander's restricted payoff
    //      to a card that does not satisfy the restriction, even when both texts are quotable.
    // v18: the reason and judge passes are now given each card's TYPE LINE, not just its rules
    //      text. Both were told to check a card's types against the commander's restriction but
    //      never shown the types, so a non-Wizard kept being credited with a "Wizard you
    //      control" payoff it was guessing at. The full rules text is still supplied too.
    // v19: the critique/judge pass runs on Sonnet instead of Haiku. It had the card text, the
    //      type line, and the doctrine and still passed a type-gated ability applied to a card
    //      lacking the type; the correctness gate needs the stronger model.
    // v20: doctrine §9.10 tightened into a mandatory per-claim procedure (the restriction is
    //      about the specific object, not the deck), and the judge prompt now points at it and
    //      is given each card's type line. The prior cached-doctrine principle alone let two
    //      restricted-payoff claims survive on cards that did not satisfy the restriction.
    // v21: doctrine §9.11 (name the mechanism, not the association) + the reason writer now
    //      forbids "synergizes with / fuels / supports the commander" filler without a named
    //      rules step. Kills vague coupling to the commander's payoff (a creature "synergizing
    //      with" a noncreature-spell trigger it never sets off).
    // v22: candidate relevance rewards the card types the commander's text rewards, caps the
    //      on-type-creature bonus, and interleaves the scored window across card-type buckets
    //      in proportion to the pool. A category is no longer creature-only unless the pool is.
    // v23: the top-synergy pool is grounded in EDHREC — the cards real decks actually run with
    //      this commander — rather than a relevance slice of the whole colour pool. Empirical
    //      inclusion is a far better prior than keyword matching; commanders with no EDHREC page
    //      fall back to the Scryfall pool.
    // v24: the EDHREC pool drops basic lands, matching the Scryfall pool. Basics are the mana
    //      base, not a suggestion, and are never scored or shown as a synergy pick.
    private const string CacheVersion = "claude-sonnet-4-6-judge-suggestions-v24";

    public DeckSuggestionsService(
        IScryfallService scryfall,
        IAnthropicClient anthropic,
        IAiCacheService cache,
        ISynergyService synergy,
        ICandidateRanking ranking,
        ICommanderDoctrine doctrine,
        IEdhrecPoolService edhrec,
        ILogger<DeckSuggestionsService> logger)
    {
        _scryfall = scryfall;
        _anthropic = anthropic;
        _cache = cache;
        _synergy = synergy;
        _ranking = ranking;
        _doctrine = doctrine;
        _edhrec = edhrec;
        _logger = logger;
    }

    /// <summary>How many cards each category shows.</summary>
    private const int CategorySize = 8;

    public Task<DeckSuggestionsDto> GetSuggestionsAsync(DeckSuggestionsRequest request)
        => RunAsync(request, emit: null, CancellationToken.None);

    public Task<DeckSuggestionsDto> GetSuggestionsStreamAsync(
        DeckSuggestionsRequest request, SuggestionEmit emit, CancellationToken ct = default)
        => RunAsync(request, emit, ct);

    /// <summary>Total number of coarse "stage" events a full (cache-miss) build reports.</summary>
    private const int StageTotal = 4;

    private async Task<DeckSuggestionsDto> RunAsync(
        DeckSuggestionsRequest request, SuggestionEmit? emit, CancellationToken ct)
    {
        // Deck contents and tags are sorted so that merely reordering cards -- which does
        // not change the request semantically -- still hits the cache. The set codes are
        // part of the key because "latest" means something different once a set ships.
        var recentForKey = await _scryfall.GetRecentSetsAsync(maxSets: LatestSetCount);

        // CommanderName is fully determined by CommanderOracleId, so it is redundant in the
        // key — and including client text in the key is what let one caller poison every
        // other caller's entry for a commander (BuildAsync rebuilds the commander fields from
        // the server-resolved definition, so the cached payload is honest regardless).
        var keyParts = new[] { request.CommanderOracleId }
            .Concat(request.DeckCardNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            .Append("|tags|")
            .Concat(request.DeckTags.Concat(request.SuggestionTags)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
            .Append("|sets|")
            .Concat(recentForKey.Select(s => s.Code).OrderBy(c => c, StringComparer.OrdinalIgnoreCase));

        return await _cache.GetOrCreateAsync(
            "suggestions", CacheVersion, keyParts, () => BuildAsync(request, emit, ct));
    }

    /// <summary>Reports one coarse progress stage. Never throws — a dead client must not fail the build.</summary>
    private static async Task StageAsync(SuggestionEmit? emit, int step, string label)
    {
        if (emit is null) return;
        try { await emit("stage", new { label, step, total = StageTotal }); }
        catch { /* best-effort: the build is cacheable and must complete regardless */ }
    }

    private async Task<DeckSuggestionsDto> BuildAsync(
        DeckSuggestionsRequest request, SuggestionEmit? emit, CancellationToken ct)
    {
        var cmdDef = await _scryfall.GetByOracleIdAsync(request.CommanderOracleId)
            ?? throw new ResourceNotFoundException($"Commander not found: {request.CommanderOracleId}");

        // Never trust the client's commander name/text: both feed the reason and judge prompts,
        // and CommanderText is the grounding source for quote verification. A caller could
        // otherwise inject instructions and control what counts as a "grounded" quote, then
        // have the poisoned result cached and served to everyone. Rebuild from the oracle card.
        request = request with
        {
            CommanderName = cmdDef.Name,
            CommanderText = cmdDef.OracleText ?? string.Empty,
        };

        var recentSets = await _scryfall.GetRecentSetsAsync(maxSets: LatestSetCount);
        var recentCodes = new HashSet<string>(
            recentSets.Select(s => s.Code), StringComparer.OrdinalIgnoreCase);

        var focus = request.DeckTags
            .Concat(request.SuggestionTags)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var inDeck = new HashSet<string>(request.DeckCardNames, StringComparer.OrdinalIgnoreCase);

        // Over-fetch each pool so that removing cards already in the deck, or already shown
        // in an earlier category, still leaves a full list rather than a silently short one.
        var depth = CategorySize * 4;

        // Focus is deliberately not a Query. A query is a hard filter, and using one emptied
        // the Game Changers category outright -- nothing on that list mentions a tribe.
        // Focus belongs in the score, where it reorders without excluding.
        RankRequest Pool(IReadOnlySet<string>? sets = null, bool gameChangers = false) =>
            new(cmdDef, SetCodes: sets, GameChangersOnly: gameChangers, Focus: focus, Limit: depth);

        // The general "top synergy" pool is grounded in what the community actually plays with
        // this commander (EDHREC) rather than a relevance slice of the whole colour pool — real
        // decklists are a far better prior for "is this card good here" than any keyword match,
        // and they surface the deck's real engine (spells, ramp, draw) instead of a wall of
        // on-colour creatures. Obscure or brand-new commanders have no EDHREC page and fall
        // back to the Scryfall pool automatically.
        var edhrecPool = await ResolveEdhrecPoolAsync(cmdDef);
        var everythingPool = edhrecPool.Count > 0
            ? Pool() with { Candidates = edhrecPool, ScoreWindow = 150 }
            : Pool();

        // All three pools are ranked in one pass: they overlap heavily, and scoring them
        // separately meant the same card scored up to three times, each run waiting on the
        // one before it.
        // Stop before each expensive Anthropic phase if the SSE client has disconnected —
        // otherwise a closed stream still pays for scoring, reason-grounding and judging.
        // Throwing here skips the cache write (a partial build must not be cached) and the
        // streaming controller treats the cancellation as a clean disconnect.
        ct.ThrowIfCancellationRequested();
        await StageAsync(emit, 1, "Scoring candidates…");
        var pools = await _ranking.RankManyAsync([
            Pool(sets: recentCodes),   // latest — newest-set discovery (Scryfall pool)
            everythingPool,            // top synergy — EDHREC-grounded when available
            Pool(gameChangers: true),  // official game-changers list
        ]);

        await StageAsync(emit, 2, "Selecting the best cards…");
        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Latest and Game Changers are narrow, defined slices; Top Synergy is the general
        // list and takes everything they did not claim. There is no fourth "notable" tier:
        // it drew from the same pool as Top Synergy, so a card in it always sat just below
        // the section above with nothing to distinguish the two but an arbitrary cut.
        var latest = await TakeAsync(pools[0], inDeck, shown, recentCodes);
        var gameChangers = await TakeAsync(pools[2], inDeck, shown, null);
        var top = await TakeAsync(pools[1], inDeck, shown, null);

        // Provisional reveal: the cards and their scores are settled now, so the UI can render
        // them immediately. Their reasons are still the scorer's first pass and some cards may
        // yet be cut by the judge below — the client shows them as "verifying" until the final
        // event arrives and replaces this whole payload.
        if (emit is not null)
        {
            var provisional = new DeckSuggestionsDto
            {
                LatestSet = latest,
                TopSynergy = top,
                GameChangers = gameChangers,
                LatestSetSources = [.. recentSets],
            };
            try { await emit("cards", provisional); } catch { /* best-effort */ }
        }

        // Rewrite each reason against the card's real rules text, then let a second pass
        // attack the inference. Ranking decides WHICH cards appear; this decides whether
        // what we say about them is true. Skipping it is how "triggers double via
        // commander" reached the UI for a commander with no such ability.
        var proposed = latest.Length + gameChangers.Length + top.Length;
        ct.ThrowIfCancellationRequested();
        var dropped = await GroundReasonsAsync(request, [latest, gameChangers, top], emit);

        if (dropped.Count > 0)
        {
            // A card the judge cannot justify leaves entirely rather than being shown with
            // a weaker reason. A short honest list beats a padded one.
            SuggestedCardDto[] Keep(SuggestedCardDto[] cards) =>
                [.. cards.Where(c => !dropped.Contains(c.Name))];

            latest = Keep(latest);
            gameChangers = Keep(gameChangers);
            top = Keep(top);
        }

        var dto = new DeckSuggestionsDto
        {
            LatestSet = latest,
            TopSynergy = top,
            GameChangers = gameChangers,
            LatestSetSources = [.. recentSets],
            Diagnostics = new SuggestionDiagnosticsDto
            {
                Proposed = proposed,
                Accepted = latest.Length + gameChangers.Length + top.Length,
                Rejected = dropped.Count > 0
                    ? new Dictionary<string, int> { ["judge-rejected"] = dropped.Count }
                    : [],
            },
        };

        _logger.LogInformation(
            "Suggestions for {Commander} (focus: {Focus}): {Latest} latest, {Gc} game changers, " +
            "{Top} synergy; judge dropped {Dropped}",
            cmdDef.Name, focus.Length > 0 ? string.Join("/", focus) : "none",
            dto.LatestSet.Length, dto.GameChangers.Length, dto.TopSynergy.Length, dropped.Count);

        return dto;
    }

    /// <summary>
    /// The cards the community actually plays with this commander (EDHREC), resolved to card
    /// definitions, filtered to the commander's colour identity, and type-interleaved so the
    /// scored window is a mix rather than whatever section EDHREC happens to list first. Empty
    /// for commanders EDHREC has no page for; the caller then falls back to the Scryfall pool.
    /// EDHREC itself is cached for weeks, so this resolves names against the in-memory index.
    /// </summary>
    private async Task<IReadOnlyList<CardDefinition>> ResolveEdhrecPoolAsync(CardDefinition commander)
    {
        IReadOnlyList<EdhrecCard> raw;
        try
        {
            raw = await _edhrec.GetCommanderPoolAsync(commander.Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EDHREC lookup failed for {Commander}; using Scryfall pool", commander.Name);
            return [];
        }

        if (raw.Count == 0)
            return [];

        var cmdColors = commander.ColorIdentity.ToHashSet();
        var resolved = new List<CardDefinition>(raw.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ec in raw)
        {
            var def = await _scryfall.GetByNameAsync(ec.Name);
            if (def is null || def.OracleId == commander.OracleId)
                continue;
            // Basic lands are the mana base, not a suggestion — the Scryfall pool path drops
            // them too (BulkDataService.GetCandidatePoolAsync), so the EDHREC path must match.
            if (def.Supertypes.Contains("Basic"))
                continue;
            // Colour-identity legal: every colour in the card must be in the commander's identity.
            if (!def.ColorIdentity.All(cmdColors.Contains))
                continue;
            if (!seen.Add(def.OracleId))
                continue;
            resolved.Add(def);
        }

        _logger.LogInformation(
            "EDHREC-grounded pool for {Commander}: {Resolved}/{Raw} cards resolved and colour-legal",
            commander.Name, resolved.Count, raw.Count);

        return [.. BulkDataService.InterleaveByType(resolved)];
    }

    /// <summary>
    /// Takes the next <see cref="CategorySize"/> cards from a ranked pool, skipping anything
    /// already in the deck or already claimed by an earlier category. <paramref name="shown"/>
    /// is mutated so each category continues where the previous one stopped.
    /// </summary>
    private async Task<SuggestedCardDto[]> TakeAsync(
        RankedPool pool,
        HashSet<string> inDeck,
        HashSet<string> shown,
        IReadOnlySet<string>? preferSets)
    {
        var picked = new List<SuggestedCardDto>(CategorySize);

        foreach (var r in pool.Cards)
        {
            if (picked.Count == CategorySize) break;
            if (r.Score <= 0) continue;
            if (inDeck.Contains(r.Card.Name)) continue;
            if (!shown.Add(r.Card.Name)) continue;

            var printings = await _scryfall.GetPrintingsAsync(r.Card.OracleId);
            var printing = printings.FirstOrDefault(pr =>
                    pr.SetCode is not null && preferSets?.Contains(pr.SetCode) == true)
                ?? printings.FirstOrDefault();

            picked.Add(new SuggestedCardDto
            {
                Name = r.Card.Name,
                Reason = r.Reason,
                Score = r.Score,
                ScryfallId = printing?.ScryfallId,
                Card = MapToCardDto(r.Card),
            });
        }

        return [.. picked];
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
    /// <returns>Names the judge rejected outright; the caller removes them.</returns>
    private async Task<HashSet<string>> GroundReasonsAsync(
        DeckSuggestionsRequest request, SuggestedCardDto[][] groups, SuggestionEmit? emit = null)
    {
        var dropped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Only cards that resolved have trustworthy rules text to ground against.
        var resolved = groups.SelectMany(g => g)
            .Where(c => c.Card is not null && !string.IsNullOrWhiteSpace(c.Card!.OracleText))
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();

        if (resolved.Length == 0)
            return dropped;

        try
        {
            await StageAsync(emit, 3, "Writing reasons…");
            var reasons = await CallReasonPassAsync(request, resolved);

            // Citing real text is not enough. Both halves of "Goblin Bombardment
            // converts Smaug's Treasures into damage" are quotable; the inference
            // joining them is what is false, so a separate pass attacks the inference.
            await StageAsync(emit, 4, "Fact-checking suggestions…");
            (reasons, dropped) = await CritiqueReasonsAsync(request, resolved, reasons);

            int verified = 0, fallback = 0;
            foreach (var group in groups)
            {
                for (int i = 0; i < group.Length; i++)
                {
                    var card = group[i].Card;
                    if (card is null || string.IsNullOrWhiteSpace(card.OracleText))
                        continue;
                    // Cut by the judge; the caller removes it, so do not count it either way.
                    if (dropped.Contains(group[i].Name))
                        continue;

                    if (reasons.TryGetValue(group[i].Name, out var better)
                        && !string.IsNullOrWhiteSpace(better))
                    {
                        group[i] = group[i] with { Reason = better.Trim() };
                        verified++;
                    }
                    else
                    {
                        // No verifiable synergy claim. Keep the reason the scorer already
                        // wrote — it is grounded in computed facts and the doctrine, so it
                        // is trustworthy even when no commander quote supports it. This is
                        // the normal case for colourless staples, where there is no
                        // commander text to cite and restating "{T}: Add {C}{C}" as the
                        // reason told the player nothing.
                        if (string.IsNullOrWhiteSpace(group[i].Reason))
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

        return dropped;
    }

    private async Task<Dictionary<string, string>> CallReasonPassAsync(
        DeckSuggestionsRequest request, SuggestedCardDto[] cards)
    {
        var cardList = string.Join("\n", cards.Select(c =>
            $"- {c.Name} | {TypeLineOf(c.Card!)} | {c.Card!.ManaCost} | {c.Card.OracleText.Replace("\n", " ")}"));

        var prompt = $$"""
            You are a Magic: The Gathering Commander/EDH expert.

            Commander: {{request.CommanderName}}
            Oracle text: {{request.CommanderText}}

            For each card below, write one short sentence explaining why it is worth
            playing in THIS deck. Each card is given as
            "name | type line | mana cost | rules text". The type line lists the card's own
            supertypes, card types and subtypes — use it to check the card against any
            restriction in the commander's text before claiming that ability helps it.

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
            - When the commander's ability applies only to objects with a stated
              characteristic — a creature type, keyword, colour, name, or numeric threshold —
              it does nothing for a card that lacks that characteristic. Read the restriction,
              then check THIS card's own types before claiming the commander's ability affects
              it. Never say the commander's restricted effect helps a card that fails the
              restriction, even if the deck contains other objects that meet it.
            - Name the mechanism, not an association. Do not write that a card "synergizes
              with", "works with", "fuels", "supports", or pairs "alongside" the commander's
              ability unless you state the actual step — what triggers what, or what one card
              produces that the other uses. If you cannot name that step, do not tie the card
              to the commander at all; give the plain reason for what the card itself does.
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
    private async Task<(Dictionary<string, string> Reasons, HashSet<string> Dropped)> CritiqueReasonsAsync(
        DeckSuggestionsRequest request,
        SuggestedCardDto[] cards,
        Dictionary<string, string> reasons)
    {
        var dropped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var toCheck = cards
            .Where(c => reasons.ContainsKey(c.Name))
            .Select(c => (c.Name, TypeLine: TypeLineOf(c.Card!),
                          Text: c.Card!.OracleText!.Replace("\n", " "), Reason: reasons[c.Name]))
            .ToArray();

        if (toCheck.Length == 0)
            return (reasons, dropped);

        var claimList = string.Join("\n", toCheck.Select((c, i) =>
            $"{i + 1}. {c.Name} — {c.TypeLine}\n   rules text: {c.Text}\n   claim: {c.Reason}"));

        var focus = request.DeckTags.Concat(request.SuggestionTags)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var focusContext = focus.Length > 0
            ? $"\nThe player asked to focus on: {string.Join(", ", focus)}"
            : "\nThe player gave no particular focus.";

        var prompt = $$"""
            You are a Magic: The Gathering rules judge and a strong Commander deckbuilder.

            Commander: {{request.CommanderName}}
            Commander's oracle text: {{request.CommanderText}}{{focusContext}}

            Below are claims about why each card belongs in this commander's deck.
            Judge each one twice: is it true, and is the card actually worth a slot here?

            Claims ({{toCheck.Length}}):
            {{claimList}}

            Mark a claim "wrong" ONLY when it is inaccurate but the card still genuinely
            belongs and you can write a positive corrected reason to run it. The "fixed"
            sentence must say what the card DOES for this deck — never what it is not, does
            not do, does not help, or does not count. If the only honest correction would be
            a reason NOT to play the card, the verdict is "weak", not "wrong".

            Check it against BOTH the rules text above AND the format rules in the doctrine
            you were given (§1). A claim can be perfectly consistent with two cards' text and
            still be impossible in Commander — the deckbuilding rules are part of whether it
            is true, not a separate question.

            Reject the reasoning if it depends on any of these:
            - Anything the format forbids: tutoring or drawing the commander from the library
              or graveyard when it starts in the command zone, ignoring commander tax, a card
              outside the commander's colour identity, or a second copy of a singleton card.
            - Treating an artifact token (Treasure, Food, Clue, Blood, Powerstone, Map) as
              a creature, or vice versa. "Sacrifice a creature" cannot sacrifice a Treasure.
            - A trigger firing more often than its text allows. Double strike does not
              re-trigger "whenever this creature attacks"; that triggers once per combat.
            - An ability, keyword, type or cost the rules text does not contain.
            - The commander producing or caring about something its oracle text never mentions.
            - An ability belonging to a different card in this list being credited to the
              commander. Each claim stands on its own card's text plus the commander's.
            - A restricted ability credited to a card that does not satisfy the restriction —
              apply doctrine §9.10 to every claim, using the type line given beside each name.

            Mark a card "weak" when a good deckbuilder would not run it here, whatever the
            claim says. Judge this on the card's own merits for THIS commander and focus:
            - Its payoff depends on cards this deck has no reason to play, so it does
              nothing most games.
            - Another card already suggested does the same job strictly better.
            - It ignores the stated focus while contributing nothing else the deck wants.
            - The commander's payoff is gated on a characteristic this card lacks — a
              creature type, keyword, or card type the commander must see to do anything —
              and the card advances none of the commander's abilities another way. A
              commander that transforms when you control four Wizards gains nothing from a
              non-Wizard whose text only makes unrelated tokens, however good that card is
              elsewhere.
            A card that genuinely advances the commander's own abilities earns its slot
            whatever its creature type — do not cut those for being off-tribe. But a card
            that neither advances the commander nor serves the focus is weak, not merely
            off-theme, and must be cut rather than kept with a reason that says so.

            Respond with ONLY this JSON (no markdown, no extra text):
            {"checks":[{"name":"<exact card name>","verdict":"ok"|"wrong"|"weak","fixed":"<if wrong, a corrected sentence under 18 words that is true given the text; otherwise \"\">"}]}

            Returning fewer good cards is better than padding the list. Be honest, not generous.
            """;

        try
        {
            var parsed = await CallJsonAsync<CritiquePassJson>(
                prompt, maxTokens: 3000, withDoctrine: true, modelId: JudgeModelId);

            int corrected = 0, unusable = 0;
            foreach (var check in parsed?.Checks ?? [])
            {
                if (string.IsNullOrWhiteSpace(check.Name) || !reasons.ContainsKey(check.Name))
                    continue;

                if (string.Equals(check.Verdict, "weak", StringComparison.OrdinalIgnoreCase))
                {
                    dropped.Add(check.Name);
                    reasons.Remove(check.Name);
                    continue;
                }

                if (!string.Equals(check.Verdict, "wrong", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.IsNullOrWhiteSpace(check.Fixed) && !IsDisqualifyingReason(check.Fixed))
                {
                    reasons[check.Name] = check.Fixed.Trim();
                    corrected++;
                }
                else if (IsDisqualifyingReason(check.Fixed))
                {
                    // The "correction" argues against the card -- it says what the card is
                    // not or does not do rather than why to run it. A suggestion whose own
                    // reason is a reason NOT to play it does not belong; cut it outright
                    // (this is the Iron Fist / Crimson Cowl case for a Wizard commander).
                    dropped.Add(check.Name);
                    reasons.Remove(check.Name);
                    unusable++;
                }
                else
                {
                    // No usable repair: drop it so the caller falls back to rules text.
                    reasons.Remove(check.Name);
                    unusable++;
                }
            }

            _logger.LogInformation(
                "Reason critique for {Commander}: {Checked} checked, {Corrected} rewritten, "
                + "{Unusable} unrepairable, {Weak} cut as weak [{Names}]",
                request.CommanderName, toCheck.Length, corrected, unusable, dropped.Count,
                string.Join(", ", dropped));
        }
        catch (Exception ex)
        {
            // An unchecked reason is still quote-grounded; keep it rather than fail.
            _logger.LogWarning(ex, "Reason critique failed for {Commander}", request.CommanderName);
        }

        return (reasons, dropped);
    }

    /// <summary>
    /// True when a "corrected" reason actually argues against the card — it states what the
    /// card is not, or does not do, rather than why to run it. The judge is told never to
    /// return these, but the model still does for high-scoring off-theme cards, so this is
    /// the belt-and-braces net: we cut the card rather than show a suggestion whose own
    /// reason says it does not help.
    /// </summary>
    internal static bool IsDisqualifyingReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return false;

        var r = reason.ToLowerInvariant();

        string[] phrases =
        {
            "does not help", "doesn't help",
            "does not count", "doesn't count", "not count toward", "count toward transformation",
            "does not advance", "doesn't advance",
            "does not support", "doesn't support",
            "does not contribute", "doesn't contribute", "contributes nothing",
            "does not synergize", "doesn't synergize", "no synergy",
            "does nothing", "provides no", "offers no", "adds no", "adds nothing",
            "not relevant", "unrelated to",
        };
        foreach (var p in phrases)
            if (r.Contains(p, StringComparison.Ordinal))
                return true;

        // "is not a Wizard", "is not an artifact", etc. -- the card lacks the type/keyword
        // the commander needs. Excludes harmless forms like "is not affected".
        return System.Text.RegularExpressions.Regex.IsMatch(r, @"\bis not an? \w+");
    }

    /// <summary>
    /// The card's printed type line ("Legendary Creature — Human Warlock Hero") from its
    /// structured type fields. Sent to the reason and judge passes so they can check a card's
    /// actual types against a restriction in the commander's text instead of guessing them —
    /// without it, a non-Wizard was repeatedly credited with a "Wizard you control" payoff.
    /// Type is one input; the full rules text is still supplied alongside it, so the decision
    /// stays about the whole card.
    /// </summary>
    private static string TypeLineOf(CardDto c)
    {
        var head = string.Join(" ", c.Supertypes.Concat(c.CardTypes.Select(t => t.ToString())));
        return c.Subtypes.Length > 0 ? $"{head} — {string.Join(" ", c.Subtypes)}" : head;
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
    /// <param name="withDoctrine">
    /// Sends the deckbuilding doctrine as a cached system prefix. The judge needs it: it
    /// checks claims against card text and knows no format rules on its own, so it passed
    /// "tutors your commander" — true of the card's text, impossible in Commander, where
    /// the commander starts in the command zone rather than the library (doctrine §1.1).
    /// </param>
    private async Task<T?> CallJsonAsync<T>(
        string prompt, int maxTokens, bool withDoctrine = false, string? modelId = null)
    {
        var request = new AnthropicRequest(
            modelId ?? ModelId,
            MaxTokens: maxTokens,
            Messages: [new { role = "user", content = prompt }])
        {
            System = withDoctrine
                ?
                [
                    new
                    {
                        type = "text",
                        text = _doctrine.Text,
                        cache_control = new { type = "ephemeral" },
                    },
                ]
                : null,
            Operation = withDoctrine ? "suggestions (doctrine)" : "suggestions",
        };

        return AnthropicResponse.DeserializeJson<T>(await _anthropic.SendAsync(request));
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

    // ---- Mapping ------------------------------------------------------

    /// <summary>Oracle card to DTO. See <see cref="DomainMapper"/>.</summary>
    private static CardDto MapToCardDto(CardDefinition def) => DomainMapper.ToDto(def);

}
