using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MtgEngine.Api.Dtos;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using static MtgEngine.Api.Services.CardGrounding;

namespace MtgEngine.Api.Services;

public interface IAiBuildService
{
    Task<AiBuildResultDto> BuildDeckAsync(Guid deckId, string userId, AiBuildRequest request);

    /// <summary>
    /// Computes the deck this build would produce, without writing anything.
    /// </summary>
    /// <remarks>
    /// Every card in the returned plan has already passed the same validation the write
    /// path applies, so applying it is an insert rather than a second judgement call.
    /// </remarks>
    Task<AiBuildPlanDto> PlanDeckAsync(Guid deckId, string userId, AiBuildRequest request);

    /// <summary>
    /// The same plan, reported as it happens.
    /// </summary>
    /// <remarks>
    /// The build takes minutes — the model reasons over the whole legal pool, then a second
    /// pass judges what it produced. Waiting for both before showing anything is several
    /// minutes of blank screen, so this emits progress, then the deck the moment it exists,
    /// then the assessment when it lands. The deck is usable from the middle event onward.
    /// </remarks>
    Task<AiBuildPlanDto> PlanDeckStreamAsync(
        Guid deckId, string userId, AiBuildRequest request,
        Func<string, object, Task> emit, CancellationToken ct = default);

    /// <summary>Inserts a plan the caller has accepted, re-validating every card first.</summary>
    Task<AiBuildResultDto> ApplyPlanAsync(Guid deckId, string userId, AiApplyPlanRequest request);

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
    private readonly IAnthropicClient _anthropic;
    private readonly ICommanderDoctrine _doctrine;
    private readonly ICommanderAnalysis _analysis;
    private readonly ILogger<AiBuildService> _logger;

    /// <summary>
    /// The model every AI pass in this service runs on.
    /// </summary>
    /// <remarks>
    /// Building a legal, coherent 99 from a large candidate pool against the doctrine is the
    /// most reasoning-heavy thing this app asks of a model, so it runs on the current
    /// flagship rather than the mid tier.
    /// <para>
    /// Two constraints travel with that choice and are easy to undo by accident:
    /// the sampling parameters are removed on this model — sending <c>temperature</c> at all
    /// is a 400, which is why every request here sets <c>Temperature = null</c> — and thinking
    /// is on by default, sharing <c>max_tokens</c> with the visible answer. A ceiling sized
    /// for the answer alone truncates the response mid-list.
    /// </para>
    /// </remarks>
    private const string ModelId = "claude-opus-5";

    /// <summary>
    /// Output ceiling for the build call.
    /// </summary>
    /// <remarks>
    /// Raised from 6000 with the move to a thinking model: the budget now covers reasoning
    /// *and* the card list, and a 99-card list that stops two thirds of the way through is
    /// indistinguishable from a model that could not fill the deck. It is a ceiling, not a
    /// target — ordinary runs finish far below it.
    /// </remarks>
    private const int BuildMaxTokens = 32000;

    /// <summary>Reasoning effort for the build call.</summary>
    /// <remarks>
    /// Raising the ceiling alone did not fix the truncation, because adaptive thinking
    /// expands to fill what it is given: at a 16,000 ceiling a build spent all 16,000
    /// reasoning and emitted 1,543 characters of an unterminated object, which the caller
    /// read as zero candidates. Effort caps the reasoning so the ceiling covers the answer
    /// too. Medium, not low: choosing ninety-nine cards against the doctrine is the
    /// judgement this feature exists for, and it is not a lookup.
    /// </remarks>
    private const string BuildEffort = "medium";

    /// <summary>Output ceiling for the refine call, which returns a handful of swaps.</summary>
    private const int RefineMaxTokens = 12000;

    /// <summary>Reasoning effort for the refine call.</summary>
    private const string RefineEffort = "medium";

    /// <summary>
    /// Output ceiling for the assessment.
    /// </summary>
    /// <remarks>
    /// 6000 was not enough and failed invisibly: thinking shares this budget with the answer,
    /// so a run spent the whole allowance reasoning and the JSON was cut off mid-object. The
    /// parse then returned null and the assessment came back empty — a silent loss after
    /// seventy-five seconds of work, indistinguishable from a model that had nothing to say.
    /// Measured: output landed on exactly 6000, which is the ceiling, not a coincidence.
    /// </remarks>
    private const int AssessMaxTokens = 24000;

    /// <summary>Reasoning effort for the assessment.</summary>
    private const string AssessEffort = "medium";

    /// <summary>
    /// Spare candidates requested alongside the main deck, used to backfill slots lost
    /// to validation. Observed rejection counts run 2-20 per build, so 30 covers the
    /// range with headroom; unused entries cost nothing but a few output tokens.
    /// </summary>
    private const int SubstituteCount = 30;

    /// <summary>
    /// Sets treated as "recent" when spotlighting new cards during a build. Wider than
    /// the suggestions panel's window, since a deck can reasonably draw on a few recent
    /// releases rather than only the newest one or two.
    /// </summary>
    private const int RecentSetCount = 6;

    /// <summary>
    /// Per-card price ceilings, in USD, for each price tier.
    /// </summary>
    /// <remarks>
    /// These enforce the tier; the prose in <see cref="DescribePrice"/> only explains it.
    /// The budget number is the one the prose already stated, and the mid number is the
    /// upper bound it allowed "a few" cards to reach — the pool stops the outliers and the
    /// prose still steers the average below them.
    /// </remarks>
    internal static decimal? PriceCeiling(string priceRange) => priceRange switch
    {
        "budget" => 3m,
        "mid" => 30m,
        _ => null,
    };

    /// <summary>Ceiling on the Game Changer hint. The official list is far shorter than this.</summary>
    private const int MaxGameChangers = 80;

    /// <summary>Ceiling on the tribe hint, so a broad creature type cannot flood the prompt.</summary>
    private const int MaxTribeCards = 220;

    /// <summary>
    /// How long a single tribe-mention match may run before it is abandoned.
    /// </summary>
    /// <remarks>
    /// The tribe names come from the analysis pass, which is model output, and the patterns
    /// are built from them. House rule for anything of that shape: never run an untimed
    /// regex over text you did not author.
    /// </remarks>
    private static readonly TimeSpan TribeMatchTimeout = TimeSpan.FromMilliseconds(50);

    private static readonly HashSet<string> BasicLands = new(StringComparer.OrdinalIgnoreCase)
        { "Plains", "Island", "Swamp", "Mountain", "Forest",
          "Wastes", "Snow-Covered Plains", "Snow-Covered Island",
          "Snow-Covered Swamp", "Snow-Covered Mountain", "Snow-Covered Forest" };

    /// <summary>
    /// One gate per deck so concurrent builds/refines on the same deck serialize. Both
    /// read the main-board count, compute free slots from it, then insert — running two
    /// at once double-filled decks to ~198 cards. Bounded by the number of distinct decks
    /// that ever use AI build (tiny; a SemaphoreSlim each), so entries are not evicted.
    /// </summary>
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> _deckLocks = new();

    private static async Task<T> WithDeckLockAsync<T>(Guid deckId, Func<Task<T>> action)
    {
        var gate = _deckLocks.GetOrAdd(deckId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        { return await action(); }
        finally { gate.Release(); }
    }

    public AiBuildService(
        IScryfallService scryfall,
        ICollectionService collection,
        IEdhrecPoolService edhrec,
        IAnthropicClient anthropic,
        ICommanderDoctrine doctrine,
        ICommanderAnalysis analysis,
        ILogger<AiBuildService> logger)
    {
        _scryfall = scryfall;
        _collection = collection;
        _edhrec = edhrec;
        _anthropic = anthropic;
        _doctrine = doctrine;
        _analysis = analysis;
        _logger = logger;
    }

    public Task<AiBuildResultDto> BuildDeckAsync(Guid deckId, string userId, AiBuildRequest request) =>
        WithDeckLockAsync(deckId, () => BuildDeckCoreAsync(deckId, userId, request));

    /// <summary>
    /// Computes a deck without writing it.
    /// </summary>
    /// <remarks>
    /// Deliberately outside the deck lock: this only reads, and holding a per-deck gate
    /// across a model call that runs for minutes would serialise previews for no gain. A
    /// plan that goes stale meanwhile is caught by <see cref="ApplyPlanAsync"/>, which does
    /// take the lock and re-validates every card against the deck as it is by then.
    /// </remarks>
    public Task<AiBuildPlanDto> PlanDeckAsync(Guid deckId, string userId, AiBuildRequest request) =>
        PlanDeckCoreAsync(deckId, userId, request);

    public Task<AiBuildResultDto> ApplyPlanAsync(Guid deckId, string userId, AiApplyPlanRequest request) =>
        WithDeckLockAsync(deckId, () => ApplyPlanCoreAsync(deckId, userId, request));

    public async Task<AiBuildPlanDto> PlanDeckStreamAsync(
        Guid deckId, string userId, AiBuildRequest request,
        Func<string, object, Task> emit, CancellationToken ct = default)
    {
        await emit("stage", new { label = "Reading the commander", step = 1, total = 4 });

        await emit("stage", new { label = "Choosing ninety-nine cards", step = 2, total = 4 });

        // The model call runs for minutes. Reporting the names as they arrive is the
        // difference between a bar that sits on step 2 looking dead and one that visibly
        // fills, which is the whole complaint a static stage label produces.
        int lastThinkingBucket = -1;
        var plan = await ComputePlanAsync(
            deckId, userId, request,
            async named =>
            {
                await emit("stage", new
                {
                    label = $"Choosing cards — {named} named",
                    step = 2,
                    total = 4,
                    named,
                });
            },
            async chars =>
            {
                // Reasoning runs for minutes before the first card is named. Reported in
                // coarse buckets so the stream carries a heartbeat rather than a frame per
                // token, and the bar shows work happening from the first second.
                int bucket = chars / 2000;
                if (bucket == lastThinkingBucket)
                    return;
                lastThinkingBucket = bucket;
                await emit("stage", new
                {
                    label = "Working out the deck",
                    step = 2,
                    total = 4,
                    thinking = chars,
                });
            });

        ct.ThrowIfCancellationRequested();

        // The deck exists now. Send it before the assessment so the list can be read while
        // the judgement is still running, rather than after it.
        var facts = MeasureFacts(plan.Cards);
        var provisional = ToPlanDto(plan, new DeckAssessmentDto { Facts = facts });
        await emit("plan", provisional);

        await emit("stage", new { label = "Checking the deck balance", step = 3, total = 4 });
        var assessment = await AssessDeckAsync(plan.Commander, plan.Cards, facts);

        await emit("stage", new { label = "Done", step = 4, total = 4 });
        return provisional with { Assessment = assessment };
    }

    private async Task<ComputedPlan> ComputePlanAsync(
        Guid deckId, string userId, AiBuildRequest request,
        Func<int, Task>? onCardsNamed = null, Func<int, Task>? onThinking = null)
    {
        var commanderOracleId = request.CommanderOracleId;

        var cmdDef = await _scryfall.GetByOracleIdAsync(commanderOracleId)
            ?? throw new ResourceNotFoundException($"Commander not found: {commanderOracleId}");

        var cmdColors = cmdDef.ColorIdentity.ToHashSet();
        var colorNames = FormatColors(cmdColors);

        // Fetch the current deck to know how many main-board slots remain. A missing
        // deck fails here — the old fall-through built the whole prompt, paid for the
        // LLM call, then failed every insert and reported a 200 with zero cards added.
        var existingDeck = await _collection.GetDeckAsync(deckId, userId)
            ?? throw new ResourceNotFoundException($"Deck not found: {deckId}");
        var existingCards = existingDeck.Cards;

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
        var recentSetCodes = await _scryfall.GetRecentSetCodesAsync(maxSets: RecentSetCount);
        var recentCardNames = await _scryfall.GetRecentCardNamesAsync(recentSetCodes, cmdColors);

        // Commander-specific candidate pool, hard-filtered before the model ever sees it.
        // Constraining selection to legal cards beats instructing the model to respect
        // the constraint -- a prompt-only attempt at that measured strictly worse.
        var pool = await BuildCandidatePoolAsync(cmdDef, cmdColors, request.Bracket, PriceCeiling(request.PriceRange));

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
            pool,
            onCardsNamed,
            onThinking);

        // Substitutes trail the main picks so a rejected card is backfilled rather than
        // leaving a hole. Without this the deck comes up short by however many cards
        // validation discarded -- an illegal deck, reported only as a skip count.
        var mainCandidates = llmResult.Main.Concat(llmResult.Substitutes).ToArray();

        var main = await SelectCards(
            mainCandidates, "main", cmdColors, addedOracleIds, mainSlotsLeft, request.Bracket);

        var side = request.IncludeSideboard
            ? await SelectCards(llmResult.Side, "side", cmdColors, addedOracleIds, 10, request.Bracket)
            : Selection.Empty;

        var maybe = request.IncludeMaybeboard
            ? await SelectCards(llmResult.Maybe, "maybe", cmdColors, addedOracleIds, 10, request.Bracket)
            : Selection.Empty;

        var byReason = new Dictionary<string, int>();
        foreach (var source in new[] { main.Reasons, side.Reasons, maybe.Reasons })
            foreach (var (reason, count) in source)
                byReason[reason] = byReason.GetValueOrDefault(reason) + count;

        int shortfall = Math.Max(0, mainSlotsLeft - main.Cards.Count);
        if (shortfall > 0)
        {
            _logger.LogWarning(
                "AI build for {Commander}: {Added}/{Target} main-deck slots filled — {Short} short " +
                "after {Candidates} candidates. Rejections: {Reasons}",
                cmdDef.Name, main.Cards.Count, mainSlotsLeft, shortfall, mainCandidates.Length,
                string.Join(", ", byReason.Select(kv => $"{kv.Key}={kv.Value}")));
        }

        return new ComputedPlan(
            cmdDef,
            [.. main.Cards, .. side.Cards, .. maybe.Cards],
            mainSlotsLeft,
            shortfall,
            main.Skipped + side.Skipped + maybe.Skipped,
            byReason);
    }

    /// <summary>Computes a deck and writes it — the one-shot build, unchanged from the caller's view.</summary>
    private async Task<AiBuildResultDto> BuildDeckCoreAsync(Guid deckId, string userId, AiBuildRequest request)
    {
        var plan = await ComputePlanAsync(deckId, userId, request);
        await EnsureCommanderCardAsync(plan.Commander, deckId, userId);
        return await WritePlanAsync(plan.Cards, deckId, userId, plan.MainTarget, plan.Skipped, plan.Reasons);
    }

    // ---- Plan, then apply ---------------------------------------------------

    private async Task<AiBuildPlanDto> PlanDeckCoreAsync(Guid deckId, string userId, AiBuildRequest request)
    {
        var plan = await ComputePlanAsync(deckId, userId, request);
        var facts = MeasureFacts(plan.Cards);

        return ToPlanDto(plan, await AssessDeckAsync(plan.Commander, plan.Cards, facts));
    }

    /// <summary>Shapes a computed plan for the wire. Shared by the plain and streamed paths.</summary>
    private static AiBuildPlanDto ToPlanDto(ComputedPlan plan, DeckAssessmentDto assessment)
    {
        return new AiBuildPlanDto
        {
            CommanderOracleId = plan.Commander.OracleId,
            CommanderName = plan.Commander.Name,
            Cards = [.. plan.Cards.Select(c => new PlannedCardDto
            {
                OracleId = c.Def.OracleId,
                Name = c.Def.Name,
                ScryfallId = c.ScryfallId,
                ManaCost = c.Def.ManaCostRaw,
                TypeLine = TypeLineOf(c.Def),
                ImageUriArtCrop = c.Def.ImageUriArtCrop,
                Board = c.Board,
                Quantity = 1,
            })],
            MainTarget = plan.MainTarget,
            MainShortfall = plan.MainShortfall,
            CardsSkipped = plan.Skipped,
            SkippedByReason = plan.Reasons,
            Assessment = assessment,
        };
    }

    /// <summary>
    /// Writes a plan the caller accepted.
    /// </summary>
    /// <remarks>
    /// Re-resolves and re-validates every card rather than trusting the payload. The plan
    /// travels out through the client and back, so it is a request like any other — and the
    /// deck may have changed since it was computed, which the slot and duplicate checks
    /// catch here rather than letting a stale plan overfill the deck.
    /// </remarks>
    private async Task<AiBuildResultDto> ApplyPlanCoreAsync(
        Guid deckId, string userId, AiApplyPlanRequest request)
    {
        var cmdDef = await _scryfall.GetByOracleIdAsync(request.CommanderOracleId)
            ?? throw new ResourceNotFoundException($"Commander not found: {request.CommanderOracleId}");

        var deck = await _collection.GetDeckAsync(deckId, userId)
            ?? throw new ResourceNotFoundException($"Deck not found: {deckId}");

        var cmdColors = cmdDef.ColorIdentity.ToHashSet();
        var addedOracleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { request.CommanderOracleId };

        int existingMainCount = 0;
        foreach (var c in deck.Cards)
        {
            if ((c.Board ?? "main") != "main")
                continue;
            if (string.Equals(c.OracleId, request.CommanderOracleId, StringComparison.OrdinalIgnoreCase))
                continue;
            existingMainCount += c.Quantity;
            addedOracleIds.Add(c.OracleId);
        }

        int mainSlotsLeft = Math.Max(0, 99 - existingMainCount);

        var accepted = new List<PlannedCard>();
        int skipped = 0;
        var reasons = new Dictionary<string, int>();

        foreach (var (board, ceiling) in new[] { ("main", mainSlotsLeft), ("side", 10), ("maybe", 10) })
        {
            var names = request.Cards
                .Where(c => string.Equals(c.Board, board, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Name)
                .ToArray();
            if (names.Length == 0)
                continue;

            var selection = await SelectCards(
                names, board, cmdColors, addedOracleIds, ceiling, request.Bracket);

            accepted.AddRange(selection.Cards);
            skipped += selection.Skipped;
            foreach (var (reason, count) in selection.Reasons)
                reasons[reason] = reasons.GetValueOrDefault(reason) + count;
        }

        await EnsureCommanderCardAsync(cmdDef, deckId, userId);
        return await WritePlanAsync(accepted, deckId, userId, mainSlotsLeft, skipped, reasons);
    }

    /// <summary>
    /// Puts the commander itself into the deck, if it is not already there.
    /// </summary>
    /// <remarks>
    /// The deck row carries <c>CommanderOracleId</c>, but the client resolves the command
    /// zone by looking for a CARD in the deck with that oracle id
    /// (<c>deck-legality.service.ts</c>). The build deliberately never picks the commander —
    /// it is excluded from the prompt and pre-seeded into the duplicate set — so an AI-built
    /// deck arrived with the field set, no matching row, and an empty command zone reading
    /// "click or drop to assign".
    /// </remarks>
    private async Task EnsureCommanderCardAsync(
        CardDefinition commander, Guid deckId, string userId)
    {
        var deck = await _collection.GetDeckAsync(deckId, userId);
        if (deck is null)
            return;

        bool present = deck.Cards.Any(c =>
            string.Equals(c.OracleId, commander.OracleId, StringComparison.OrdinalIgnoreCase));
        if (present)
            return;

        try
        {
            var printings = await _scryfall.GetPrintingsAsync(commander.OracleId);

            await _collection.AddCardToCollectionAsync(deckId, userId, new AddCardToCollectionRequest(
                OracleId: commander.OracleId,
                ScryfallId: printings.FirstOrDefault()?.ScryfallId,
                Quantity: 1,
                QuantityFoil: 0,
                Board: "main"));
        }
        catch (Exception ex)
        {
            // A deck without its commander card is wrong but still usable; losing the
            // ninety-nine over it would not be.
            _logger.LogWarning(
                ex, "AI build: could not add commander '{Commander}' to the deck", commander.Name);
        }
    }

    /// <summary>Inserts selected cards and reports what landed, shared by build and apply.</summary>
    private async Task<AiBuildResultDto> WritePlanAsync(
        IReadOnlyList<PlannedCard> cards, Guid deckId, string userId,
        int mainTarget, int skippedBefore, Dictionary<string, int> reasons)
    {
        var added = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int failed = 0;
        var byReason = new Dictionary<string, int>(reasons);

        foreach (var card in cards)
        {
            try
            {
                await _collection.AddCardToCollectionAsync(deckId, userId, new AddCardToCollectionRequest(
                    OracleId: card.Def.OracleId,
                    ScryfallId: card.ScryfallId,
                    Quantity: 1,
                    QuantityFoil: 0,
                    Board: card.Board));

                added[card.Board] = added.GetValueOrDefault(card.Board) + 1;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex, "AI build: failed to add card '{Name}' to {Board}", card.Def.Name, card.Board);
                failed++;
                byReason[Rejection.AddFailed] = byReason.GetValueOrDefault(Rejection.AddFailed) + 1;
            }
        }

        int mainAdded = added.GetValueOrDefault("main");

        return new AiBuildResultDto
        {
            CardsAdded = mainAdded,
            SideboardAdded = added.GetValueOrDefault("side"),
            MaybeboardAdded = added.GetValueOrDefault("maybe"),
            CardsSkipped = skippedBefore + failed,
            MainTarget = mainTarget,
            MainShortfall = Math.Max(0, mainTarget - mainAdded),
            SkippedByReason = byReason,
        };
    }

    /// <summary>
    /// The deckbuilding doctrine, as a cached system prefix.
    /// </summary>
    /// <remarks>
    /// This service was the only AI pass in the app that never received it. Every other one —
    /// synergy scoring, deck suggestions, commander suggestion — reasons from the doctrine,
    /// while the pass that builds the entire deck worked from a hand-written copy of the role
    /// quotas in its own prompt. Those had already drifted: ramp, draw and interaction each
    /// read 8–10 against the doctrine's 8–12, the strategy core read 35–38 against 25–35, win
    /// conditions were missing entirely, and nothing mentioned the mana-source total (§2.1) or
    /// that mass removal is archetype-dependent (§6.4).
    /// <para>
    /// It is byte-identical on every build, so it is its own cache breakpoint and does not
    /// disturb the legal-pool prefix inside the message, which caches on a different key.
    /// </para>
    /// </remarks>
    private object[] DoctrinePrefix() =>
    [
        new
        {
            type = "text",
            text = _doctrine.Text,
            cache_control = new { type = "ephemeral" },
        },
    ];

    /// <summary>
    /// What the deck measurably contains. Facts only — no verdict.
    /// </summary>
    /// <remarks>
    /// Doctrine §0.1 draws this line and this follows it: code states the structured facts
    /// and never interprets them. There are deliberately no target numbers here. The
    /// doctrine's quotas are baselines that move with the deck — §2.2 lowers the land count
    /// for a cheap curve and raises it for an expensive one, §6.4 inverts the value of mass
    /// removal depending on how creature-dense the deck is — so a fixed table in code would
    /// contradict the standard it claims to enforce, and would call a correct deck broken.
    /// <para>
    /// Roles come from <see cref="CardRoleClassifier"/>, the same classifier that grouped the
    /// pool the model chose from, so the counts are measured on the terms the prompt asked in.
    /// </para>
    /// </remarks>
    private static DeckFactsDto MeasureFacts(IReadOnlyList<PlannedCard> cards)
    {
        var main = cards.Where(c => c.Board == "main").Select(c => c.Def).ToArray();
        if (main.Length == 0)
            return new DeckFactsDto();

        var byRole = new Dictionary<CardRole, int>();
        int interactionOnCreatures = 0;
        foreach (var def in main)
        {
            var role = CardRoleClassifier.Classify(def);
            byRole[role] = byRole.GetValueOrDefault(role) + 1;

            // Counted separately, not moved: the card really does interact, and it really
            // is a body doing something else as well. See the DTO field's remarks.
            if (role == CardRole.Removal && def.CardTypes.HasFlag(CardType.Creature))
                interactionOnCreatures++;
        }

        var nonLand = main.Where(d => !d.CardTypes.HasFlag(CardType.Land)).ToArray();
        int creatures = main.Count(d => d.CardTypes.HasFlag(CardType.Creature));

        // A land's colour identity is what it can produce, which is the quantity §3.2 counts
        // in sources. Non-lands are excluded: a spell that makes mana once is ramp, not a source.
        var sources = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var land in main.Where(d => d.CardTypes.HasFlag(CardType.Land)))
        {
            foreach (var letter in ToColorLetters(land.ColorIdentity))
                sources[letter] = sources.GetValueOrDefault(letter) + 1;
        }

        int lands = byRole.GetValueOrDefault(CardRole.Land);
        int ramp = byRole.GetValueOrDefault(CardRole.Ramp);

        return new DeckFactsDto
        {
            Cards = main.Length,
            Lands = lands,
            Ramp = ramp,
            Draw = byRole.GetValueOrDefault(CardRole.Draw),
            Interaction = byRole.GetValueOrDefault(CardRole.Removal),
            InteractionOnCreatures = interactionOnCreatures,
            Other = byRole.GetValueOrDefault(CardRole.Other),
            Creatures = creatures,
            CreaturePercentOfNonland = nonLand.Length == 0
                ? 0
                : (int)Math.Round(100.0 * creatures / nonLand.Length),
            ManaSources = lands + ramp,
            AverageManaValue = nonLand.Length == 0
                ? 0
                : Math.Round(nonLand.Average(d => d.Cmc), 2),
            ColorSources =
            [
                .. ColorOrderLetters
                    .Where(sources.ContainsKey)
                    .Select(c => new ColorSourceDto(c, sources[c])),
            ],
        };
    }

    private static readonly string[] ColorOrderLetters = ["W", "U", "B", "R", "G"];

    private static string[] ToColorLetters(IReadOnlyList<ManaColor> colors) =>
    [
        .. ColorOrderLetters.Where(l => colors.Any(c => Letter(c) == l)),
    ];

    private static string Letter(ManaColor c) => c switch
    {
        ManaColor.White => "W",
        ManaColor.Blue => "U",
        ManaColor.Black => "B",
        ManaColor.Red => "R",
        ManaColor.Green => "G",
        _ => "C",
    };

    /// <summary>
    /// Asks the model whether this deck actually makes this commander work.
    /// </summary>
    /// <remarks>
    /// The judgement pass, and the reason there is no quota table in code. It is given the
    /// doctrine, the commander's own text, the measured facts above and the decklist by role,
    /// and it judges the deck <em>for this commander</em>: whether the plan is executable,
    /// whether the mana supports the curve and the pips actually present, whether the
    /// interaction split suits the archetype the deck turned out to be.
    /// <para>
    /// A failure here must not lose the deck. The build is the expensive part and the plan is
    /// still perfectly usable without an assessment, so any error returns an empty assessment
    /// rather than propagating.
    /// </para>
    /// </remarks>
    private async Task<DeckAssessmentDto> AssessDeckAsync(
        CardDefinition commander, IReadOnlyList<PlannedCard> cards, DeckFactsDto facts)
    {
        try
        {
            var byRole = cards
                .Where(c => c.Board == "main")
                .GroupBy(c => CardRoleClassifier.Classify(c.Def))
                .OrderBy(g => g.Key)
                .Select(g => $"{CardRoleClassifier.Label(g.Key)} ({g.Count()}):\n"
                             + string.Join(", ", g.Select(c => c.Def.Name)));

            var colorSources = facts.ColorSources.Length == 0
                ? "none"
                : string.Join(", ", facts.ColorSources.Select(c => $"{c.Color}={c.Count}"));

            var prompt = $$"""
                Assess whether this deck makes its commander work, and how efficiently.

                Commander: {{commander.Name}}
                Commander type: {{TypeLineOf(commander)}}
                Commander oracle text: {{commander.OracleText}}

                MEASURED FACTS (computed in code — correct, do not recompute or contradict):
                - Main deck: {{facts.Cards}} cards
                - Lands {{facts.Lands}}, ramp {{facts.Ramp}}, card advantage {{facts.Draw}}, interaction {{facts.Interaction}} (of which {{facts.InteractionOnCreatures}} are creatures), other {{facts.Other}}
                - Mana sources (lands + ramp): {{facts.ManaSources}}
                - Creatures: {{facts.Creatures}} ({{facts.CreaturePercentOfNonland}}% of nonland cards)
                - Average mana value of nonland cards: {{facts.AverageManaValue}}
                - Coloured sources among lands: {{colorSources}}

                DECK BY ROLE:
                {{string.Join("\n\n", byRole)}}

                Judge it against the doctrine in the system prompt, FOR THIS COMMANDER.

                There are no fixed quotas to check against. The doctrine's numbers are
                baselines that move with the deck: §2.2 adjusts the land count for the curve
                this deck actually has, §2.1 cares about lands and ramp together rather than
                lands alone, §3.2 sets coloured sources against the pips this deck actually
                casts, and §6.4 inverts the value of symmetrical mass removal depending on how
                creature-dense the deck is. Work out what THIS deck needs, then say whether it
                has it.

                Cover, in order of how much it matters here:
                - The plan: what the commander is trying to do, and whether the deck can
                  execute it. Name the cards that carry it and any missing piece it needs.
                - Mana: whether the count and the colours support this curve and these pips.
                - Interaction: whether the split fits this archetype and these colours (§6.1,
                  §6.3, §6.4), not merely whether the total looks reasonable.
                - Resilience: protection and recursion in proportion to how much the deck
                  depends on the commander (§2.2).

                Rules:
                - Every finding names a concrete card or a measured fact. "Needs more ramp" is
                  not a finding; "8 ramp against an average mana value of 3.9 is short for a
                  curve this high" is.
                - Never claim a card has an ability its name does not obviously carry — you
                  have the list, not the rules text, so speak about counts and roles, and
                  about the commander's text, which you do have.
                - If an area is genuinely fine, say so briefly rather than inventing a problem.
                - severity: "critical" only when the deck cannot execute its plan as built.
                - Do not open the verdict with "Yes" or "No". Nobody asked a question — it is
                  read as a headline above the decklist, so lead with what the deck does.

                Return ONLY this JSON (no markdown):
                {"verdict":"<2-3 sentences: does this deck make this commander work, and what is the single biggest lever>",
                  "findings":[{"area":"<Plan|Mana|Interaction|Resilience>","severity":"<critical|improve|note>",
                               "finding":"<what, citing a card or a number>","fix":"<what to change, or \"\" if nothing>"}]}
                """;

            var respJson = await _anthropic.SendAsync(new AnthropicRequest(
                ModelId,
                MaxTokens: AssessMaxTokens,
                Messages: [new { role = "user", content = prompt }])
            {
                System = DoctrinePrefix(),
                Temperature = null,
                Effort = AssessEffort,
                Operation = "AI build assessment",
            });

            var parsed = AnthropicResponse.DeserializeJson<AssessmentJson>(respJson);
            if (parsed is null)
                return new DeckAssessmentDto { Facts = facts };

            return new DeckAssessmentDto
            {
                Verdict = parsed.Verdict ?? string.Empty,
                Findings =
                [
                    .. (parsed.Findings ?? []).Select(f => new DeckFindingDto
                    {
                        Area = f.Area ?? string.Empty,
                        Severity = f.Severity ?? "note",
                        Finding = f.Finding ?? string.Empty,
                        Fix = f.Fix ?? string.Empty,
                    }),
                ],
                Facts = facts,
            };
        }
        catch (Exception ex)
        {
            // The deck is the expensive part and is still usable unassessed.
            _logger.LogWarning(ex, "AI build: deck assessment failed for {Commander}", commander.Name);
            return new DeckAssessmentDto { Facts = facts };
        }
    }

    private sealed class AssessmentJson
    {
        [System.Text.Json.Serialization.JsonPropertyName("verdict")]
        public string? Verdict { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("findings")]
        public FindingJson[]? Findings { get; set; }
    }

    private sealed class FindingJson
    {
        [System.Text.Json.Serialization.JsonPropertyName("area")]
        public string? Area { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("severity")]
        public string? Severity { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("finding")]
        public string? Finding { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("fix")]
        public string? Fix { get; set; }
    }

    /// <summary>A card that passed validation, with the printing the deck should pin.</summary>
    private sealed record PlannedCard(CardDefinition Def, string? ScryfallId, string Board);

    private sealed record Selection(List<PlannedCard> Cards, int Skipped, Dictionary<string, int> Reasons)
    {
        public static Selection Empty => new([], 0, []);
    }

    /// <summary>A whole deck decided but not yet written.</summary>
    private sealed record ComputedPlan(
        CardDefinition Commander,
        List<PlannedCard> Cards,
        int MainTarget,
        int MainShortfall,
        int Skipped,
        Dictionary<string, int> Reasons);

    /// <summary>Rebuilds a printable type line from the structured type fields.</summary>
    private static string TypeLineOf(CardDefinition def)
    {
        var types = def.CardTypes == CardType.None
            ? []
            : def.CardTypes.ToString().Split(", ", StringSplitOptions.RemoveEmptyEntries);

        var left = string.Join(" ", def.Supertypes.Concat(types));
        return def.Subtypes.Count > 0
            ? $"{left} \u2014 {string.Join(" ", def.Subtypes)}"
            : left;
    }

    // ---- Refine -------------------------------------------------------------

    public Task<AiRefineResultDto> RefineDeckAsync(Guid deckId, string userId, AiRefineRequest request) =>
        WithDeckLockAsync(deckId, () => RefineDeckCoreAsync(deckId, userId, request));

    private async Task<AiRefineResultDto> RefineDeckCoreAsync(Guid deckId, string userId, AiRefineRequest request)
    {
        var deck = await _collection.GetDeckAsync(deckId, userId)
            ?? throw new ResourceNotFoundException($"Deck not found: {deckId}");

        if (string.IsNullOrWhiteSpace(deck.CommanderOracleId))
            throw new InvalidResourceStateException("Deck has no commander to refine against.");

        var cmdDef = await _scryfall.GetByOracleIdAsync(deck.CommanderOracleId)
            ?? throw new ResourceNotFoundException($"Commander not found: {deck.CommanderOracleId}");

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

        var pool = await BuildCandidatePoolAsync(cmdDef, cmdColors, request.Bracket, PriceCeiling(request.PriceRange));

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

            // Shared ladder with AddCards — color identity, commander legality, bracket.
            if (CardGrounding.ValidateForCommanderDeck(inDef, cmdColors, request.Bracket) is string rejection)
            { Reject(rejection); continue; }

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

                // Main board only — the swap-out was selected from main, and an
                // unscoped remove could delete the same card's sideboard row instead.
                await _collection.RemoveCardByOracleAsync(deckId, outOracleId, userId, board: "main");

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

        var respJson = await _anthropic.SendAsync(new AnthropicRequest(
            ModelId,
            MaxTokens: RefineMaxTokens,
            Messages: [new { role = "user", content = prompt }])
        {
            System = DoctrinePrefix(),
            Temperature = null,
            Effort = RefineEffort,
            Operation = "AI refine",
        });

        var parsed = AnthropicResponse.DeserializeJson<RefineJson>(respJson);

        // Filter null elements too — `[null]` deserializes into the array as-is.
        return [.. (parsed?.Swaps ?? [])
            .Where(s => s is not null)
            .Select(s => new ProposedSwap(s.Out, s.In, s.Why))];
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
        string[] CommonlyPlayed,
        string[] Tribes,
        string[] TribeCards,
        string[] GameChangers)
    {
        public int LegalCount => LegalByRole.Sum(kv => kv.Value.Length);

        /// <summary>Below this the pool cannot fill a deck, so fall back to unconstrained generation.</summary>
        public bool IsUsable => LegalCount >= 200;

        public static readonly CandidatePool Empty =
            new(new Dictionary<CardRole, string[]>(), [], [], [], []);
    }

    private async Task<CandidatePool> BuildCandidatePoolAsync(
        CardDefinition commander, HashSet<ManaColor> cmdColors, int bracket, decimal? maxUsd)
    {
        // Timed because it is the one stage of a build invisible from outside: the stream's
        // stage frames bracket the model call and the card resolution, but everything
        // before the first frame is this, and it scans the whole legal pool.
        var poolWatch = System.Diagnostics.Stopwatch.StartNew();
        var commanderName = commander.Name;
        var legalByRole = await _scryfall.GetLegalCardsByRoleAsync(cmdColors, bracket, maxUsd);
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

        // The commander's tribe, and the legal cards that actually share it.
        //
        // The pool is every legal card grouped by role, which is complete but says nothing
        // about theme: a Wolf commander was handed the same undifferentiated list as any
        // other, and the build returned a deck with barely a Wolf in it. The tribe is a
        // computed fact — CommanderAnalysis already reads it off the commander's text and
        // type line — so naming the members costs a scan and no judgement.
        string[] tribes = [];
        string[] tribeCards = [];
        try
        {
            var requirements = await _analysis.AnalyseAsync(commander);
            tribes = [.. requirements.Tribes];

            if (tribes.Length > 0)
            {
                var wanted = new HashSet<string>(tribes, StringComparer.OrdinalIgnoreCase);
                var namers = TribeMentionPatterns(tribes);
                var byType = new List<string>();
                var byText = new List<string>();

                foreach (var name in legal)
                {
                    var def = await _scryfall.GetByNameAsync(name);
                    if (def is null)
                        continue;

                    if (def.Subtypes.Any(wanted.Contains))
                    {
                        byType.Add(def.Name);
                    }
                    else if (def.OracleText is { Length: > 0 } text && MentionsTribe(namers, text))
                    {
                        byText.Add(def.Name);
                    }
                }

                // Cards that *are* the tribe come first, then cards that merely name it.
                // Sorting the two together alphabetically and then truncating is how a Wolf
                // commander was handed a list holding 13 Wolves and 204 cards whose only
                // claim was the word "battlefield": the noise sorted in among the real
                // members and pushed 56 of the 69 out of the prompt.
                var ordered = byType
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .Concat(byText.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                tribeCards = [.. ordered.Take(MaxTribeCards)];

                if (ordered.Length > tribeCards.Length)
                {
                    // Said out loud rather than trimmed quietly: a hint that dropped most
                    // of what it found looks identical to a hint that found little.
                    _logger.LogInformation(
                        "Tribe hint for {Commander}: {Kept} of {Found} names kept "
                        + "({ByType} by creature type, {ByText} by rules text)",
                        commanderName, tribeCards.Length, ordered.Length,
                        byType.Count, byText.Count);
                }
            }
        }
        catch (Exception ex)
        {
            // A themeless pool still builds a legal deck; failing the build over the hint
            // would trade a good outcome for no outcome.
            _logger.LogWarning(ex, "Tribe hint unavailable for {Commander}", commanderName);
        }

        _logger.LogInformation(
            "Candidate pool for {Commander} built in {Elapsed}ms: {Legal} legal cards, "
            + "{Hint} commonly played, "
            + "tribes [{Tribes}] with {TribeCards} members",
            commanderName, poolWatch.ElapsedMilliseconds, legal.Length, commonlyPlayed.Length,
            string.Join("/", tribes), tribeCards.Length);

        // Which of these are Game Changers, named as such — but only where the bracket
        // allows them, so the list is never a description of what is missing.
        //
        // Doctrine §1.4: membership "is supplied as a fact, never inferred". It was being
        // neither. The pool at bracket 4 quietly contained 23 more cards than at bracket 3
        // and nothing said which, so the model had only the names to go on — exactly the
        // recall §0.3 says not to lean on. Measured: a bracket-4 build picked none of them.
        string[] gameChangers = [];
        if (bracket >= 4)
        {
            try
            {
                var (flagged, _) = await _scryfall.GetCandidatePoolAsync(
                    cmdColors, commander, gameChangersOnly: true, limit: MaxGameChangers);
                gameChangers = [.. flagged
                    .Select(d => d.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];
            }
            catch (Exception ex)
            {
                // The bracket still permits them; the model simply loses the hint.
                _logger.LogWarning(ex, "Game Changer hint unavailable for {Commander}", commanderName);
            }
        }

        return new CandidatePool(legalByRole, commonlyPlayed, tribes, tribeCards, gameChangers);
    }

    // ---- Card resolution + insertion ----------------------------------------

    // Rejection reasons live on CardGrounding, shared with the validation ladder.

    /// <param name="names">
    /// Primary picks followed by substitutes. The loop stops once <paramref name="maxCards"/>
    /// is reached, so substitutes cost nothing unless a primary pick was rejected.
    /// </param>
    /// <summary>
    /// Resolves names to cards and applies the validation ladder, without writing anything.
    /// </summary>
    /// <remarks>
    /// Split out of the old <c>AddCards</c> so the same decisions serve a preview and a
    /// write. Keeping one copy matters more than the convenience did: colour identity,
    /// legality, bracket and duplicate rules that differed between preview and commit would
    /// show the player a deck they cannot have.
    /// </remarks>
    private async Task<Selection> SelectCards(
        string[] names, string board,
        HashSet<ManaColor> cmdColors, HashSet<string> addedOracleIds, int maxCards, int bracket)
    {
        var picked = new List<PlannedCard>();
        int skipped = 0;
        var reasons = new Dictionary<string, int>();
        void Reject(string reason)
        {
            skipped++;
            reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
        }

        foreach (var name in names)
        {
            if (picked.Count >= maxCards)
                break;
            if (string.IsNullOrWhiteSpace(name))
                continue;
            try
            {
                var def = await _scryfall.GetByNameAsync(name);
                if (def is null)
                { Reject(Rejection.UnknownCard); continue; }

                // Shared ladder with RefineDeckAsync — color identity, legality, bracket.
                if (CardGrounding.ValidateForCommanderDeck(def, cmdColors, bracket) is string rejection)
                {
                    if (rejection == Rejection.ColorIdentity && _logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug(
                            "AI build reject (color): '{Card}' identity [{CardColors}] vs commander [{CmdColors}]",
                            def.Name,
                            string.Join(",", def.ColorIdentity),
                            string.Join(",", cmdColors));
                    }
                    Reject(rejection);
                    continue;
                }

                bool isBasic = BasicLands.Contains(def.Name);
                if (!isBasic && addedOracleIds.Contains(def.OracleId))
                { Reject(Rejection.Duplicate); continue; }

                var printings = await _scryfall.GetPrintingsAsync(def.OracleId);
                var scryfallId = printings.FirstOrDefault()?.ScryfallId;

                picked.Add(new PlannedCard(def, scryfallId, board));
                addedOracleIds.Add(def.OracleId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AI build: failed to resolve card '{Name}' for {Board}", name, board);
                Reject(Rejection.AddFailed);
            }
        }

        return new Selection(picked, skipped, reasons);
    }

    // ---- LLM call ---------------------------------------------------

    /// <summary>
    /// How many card names the answer has produced so far, read off the partial JSON.
    /// </summary>
    /// <remarks>
    /// Counts completed quoted strings inside the <c>"main"</c> array. Deliberately
    /// crude: it drives a progress bar, and an approximate count that moves beats an
    /// exact one that cannot be had until the call finishes. Never used as data.
    /// <para>
    /// It stops at the array's own closing bracket. Counting to the end of the text
    /// instead ran straight on through <c>side</c>, <c>maybe</c> and <c>substitutes</c>,
    /// so a ninety-nine card deck announced "130 named" on screen while the bar sat
    /// pinned near full. A progress readout that overshoots the thing it is counting
    /// reads as broken, which is the one job it has.
    /// </para>
    /// </remarks>
    internal static int CountNamedCards(string partial)
    {
        int key = partial.IndexOf("\"main\"", StringComparison.Ordinal);
        if (key < 0)
            return 0;

        int open = partial.IndexOf('[', key + 6);
        if (open < 0)
            return 0;

        int quotes = 0;
        bool inString = false;

        for (int i = open + 1; i < partial.Length; i++)
        {
            char c = partial[i];

            if (c == '"' && partial[i - 1] != '\\')
            {
                inString = !inString;
                quotes++;
                continue;
            }

            // A bracket inside a string is part of a card name, not the end of the array.
            if (!inString && c == ']')
                break;
        }

        return quotes / 2;
    }

    private sealed record LlmDeckResponse(string[] Main, string[] Side, string[] Maybe, string[] Substitutes);

    private async Task<LlmDeckResponse> CallAnthropicAsync(
        string commanderName, string commanderText, string colors,
        int mainSlots, int bracket, string priceRange,
        bool includeSide, bool includeMaybe,
        string[] recentCardNames,
        CandidatePool pool,
        Func<int, Task>? onCardsNamed = null,
        Func<int, Task>? onThinking = null)
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

        // The tribe, named explicitly. Without it the model was left to infer the theme
        // from the commander's text against an undifferentiated list of every legal card,
        // and tribal density suffered badly.
        var tribeSection = pool.Tribes.Length > 0 && pool.TribeCards.Length > 0
            ? $"\n── {string.Join(" / ", pool.Tribes).ToUpperInvariant()} CARDS "
              + $"({pool.TribeCards.Length}) ──────────────\n"
              + $"This commander cares about {string.Join(" and ", pool.Tribes)}. Every card below "
              + "is legal here and either has that creature type or names it in its rules text.\n"
              + "A tribal deck needs real density — doctrine §7 counts twelve or more before the "
              + "theme is a theme at all, and the payoffs are dead without it. Draw the strategy "
              + "core from this list first, then fill the remaining roles from the pool.\n"
              + string.Join(", ", pool.TribeCards)
            : string.Empty;

        // Named, because membership is a fact and the model has no other way to know it.
        var gameChangerSection = pool.GameChangers.Length > 0
            ? $"\n── GAME CHANGERS AVAILABLE AT THIS BRACKET ({pool.GameChangers.Length}) ──────────────\n"
              + "These pool entries carry the official Game Changer flag. That is a supplied\n"
              + "fact, not a judgement — do not infer membership for anything else, and do not\n"
              + "assume a card is absent from this list because it is weak (doctrine §1.4).\n"
              + "This bracket permits them. Judge each one the way you judge every other card:\n"
              + "on the slot it earns in this deck, not on its reputation (§10.1, §9.4). A\n"
              + "powerful card that does not serve the plan is still the wrong card (§8 T6).\n"
              + string.Join(", ", pool.GameChangers)
            : string.Empty;

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
            {{gameChangerSection}}
            {{tribeSection}}
            {{commonlyPlayedSection}}
            ── DECK COMPOSITION ({{mainSlots}} main-deck cards) ────────
            Every card must earn its slot. Think about what {{commanderName}} wants to do, then build around that.

            The composition standard is §2 of the doctrine in the system prompt, and it is the
            authority — do not substitute remembered ratios for it. Apply, at minimum:

            - §2 role quotas: lands, ramp, card advantage, interaction, strategy core and win
              conditions. A card counts toward the role it is actually being played for.
            - §2.1 the mana-source total: lands + ramp together, which matters more than the
              land count alone. A deck can hit its land quota and still be broken on this one.
            - §2.2 the adjustments that apply to THIS deck — curve, commander dependence,
              go-wide, graveyard or combo — and say nothing if none apply.
            - §3 the mana base: land count against curve (§3.1), coloured sources per pip
              (§3.2), and the ceiling on lands entering tapped (§3.3).
            - §6 interaction: the SPLIT across spot removal, catch-all answers, artifact and
              enchantment removal, mass removal, graveyard hate and protection — not just the
              total (§6.1), judged against what these colours can actually produce (§6.3).
            - §6.4 mass removal is archetype-dependent. In a creature-dense, go-wide or token
              deck, symmetrical board wipes lose you more than the table and belong in the
              weak band. Build the deck this commander wants, then choose removal to match it.

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

        var buildRequest = new AnthropicRequest(
            ModelId,
            MaxTokens: BuildMaxTokens,
            Messages:
            [
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
            ])
        {
            System = DoctrinePrefix(),
            // Null, not zero: this model rejects the sampling parameters outright.
            Temperature = null,
            Effort = BuildEffort,
            Operation = "AI build",
        };

        string respJson;
        if (onCardsNamed is null)
        {
            respJson = await _anthropic.SendAsync(buildRequest);
        }
        else
        {
            // Stream it, so the wait can be reported instead of merely endured. The count
            // is derived from the answer text as it arrives: every card is a quoted string,
            // so completed quotes after the "main" key is a fair reading of how far along
            // the model is. It is a progress signal, never the parsed result — the real
            // parse still happens once on the assembled response.
            int lastReported = -1;
            respJson = await _anthropic.StreamTextAsync(
                buildRequest,
                async (_, whole) =>
                {
                    int named = CountNamedCards(whole);
                    if (named == lastReported)
                        return;
                    lastReported = named;
                    await onCardsNamed(named);
                },
                onThinking);
        }

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

        if (parsed.ValueKind != JsonValueKind.Object)
        {
            // The response was not readable JSON — almost always an answer cut off at the
            // token ceiling. Reported rather than returned empty: an unreadable answer used
            // to fall through as zero candidates, which surfaced to the user as a deck with
            // "0 of 99 slots filled" and no error anywhere, and read as the model having
            // nothing to suggest.
            _logger.LogError(
                "AI build response was not usable JSON ({Chars} chars). "
                + "See the preceding client log for the stop reason.",
                respJson.Length);

            throw new AiUpstreamException(
                "Anthropic", System.Net.HttpStatusCode.OK, "Build response was not usable JSON.");
        }

        return new LlmDeckResponse(
            ParseArray("main"), ParseArray("side"), ParseArray("maybe"), ParseArray("substitutes"));
    }

    // ---- Helpers ---------------------------------------------------

    /// <summary>
    /// One whole-word pattern per tribe, for spotting a card that names the tribe in its text.
    /// </summary>
    /// <remarks>
    /// Whole-word, because a plain substring test is wrong in a way that is easy to miss and
    /// expensive when missed. The tribe <c>Battle</c> — a real card type, and one a Wolf
    /// commander genuinely referenced — matched every card whose text says "enters the
    /// battlefield", which is most of Magic: 1,406 of one commander's 1,475 hits were text
    /// matches, and 1,349 of those said nothing but "battlefield".
    /// </remarks>
    internal static Regex[] TribeMentionPatterns(IEnumerable<string> tribes)
    {
        var patterns = new List<Regex>();

        foreach (var tribe in tribes)
        {
            if (string.IsNullOrWhiteSpace(tribe))
                continue;

            // Singular, regular plural, and the -f/-ves plural that several creature types
            // take. Rules text says "Wolves" far more often than "Wolf", so a pattern that
            // only knew the singular would miss most of the cards it exists to find.
            var forms = new List<string> { Regex.Escape(tribe) + "s?" };
            if (tribe.EndsWith("fe", StringComparison.OrdinalIgnoreCase))
                forms.Insert(0, Regex.Escape(tribe[..^2]) + "ves");
            else if (tribe.EndsWith("f", StringComparison.OrdinalIgnoreCase))
                forms.Insert(0, Regex.Escape(tribe[..^1]) + "ves");

            try
            {
                patterns.Add(new Regex(
                    $@"\b(?:{string.Join("|", forms)})\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TribeMatchTimeout));
            }
            catch (ArgumentException)
            {
                // A tribe name that will not compile is simply not searched for by text;
                // the creature-type match still finds the real members.
            }
        }

        return [.. patterns];
    }

    /// <summary>True when the rules text names one of the tribes as a word.</summary>
    internal static bool MentionsTribe(Regex[] patterns, string text)
    {
        foreach (var pattern in patterns)
        {
            try
            {
                if (pattern.IsMatch(text))
                    return true;
            }
            catch (RegexMatchTimeoutException)
            {
                // Treated as no match, per the house rule for timed patterns.
            }
        }
        return false;
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

    /// <summary>
    /// What a bracket asks of a deck, as properties rather than as a list of cards.
    /// </summary>
    /// <remarks>
    /// This used to name cards — roughly sixty of them across the five brackets — and the
    /// doctrine forbids exactly that twice over. §1.4: Game Changer membership "is supplied
    /// as a fact, never inferred", and §0.3: "A name is a lookup. A property is a reason.
    /// Only the reason transfers."
    /// <para>
    /// It was also wrong, which is what a hand-maintained list of card names does. Sol Ring
    /// was named as a Game Changer at brackets 2 and 3; it is not one, and never has been —
    /// our own card data flags it <c>false</c>. So the single most-played card in the format
    /// was being talked out of every mid-bracket deck by a sentence in a prompt, while the
    /// pool that feeds the prompt was happily offering it.
    /// </para>
    /// <para>
    /// Nothing here needs to police Game Changers at all: the candidate pool already
    /// excludes them below bracket 4 on the data flag, so a card the model cannot see is not
    /// a card it needs warning about. What is left is the part a list cannot express — how
    /// hard the deck is trying.
    /// </para>
    /// </remarks>
    internal static string DescribeBracket(int bracket) => bracket switch
    {
        1 => """
             Bracket 1 (Casual):
             - No tutors. No stax. No mass land denial. No two-card infinite combos, no free spells.
             - Nothing that ends or locks a game before the table has had one.
             - Aim for flavour and visible synergy over efficiency; a card that does something
               interesting beats a card that does something optimal.
             """,
        2 => """
             Bracket 2 (Core):
             - Tutors only for land. Nothing that fetches any card in the deck.
             - No stax, no mass land denial.
             - Mana rocks are fine at their fair rate; nothing that produces more than it cost.
             - Solid, fair cards: efficient creatures, honest removal, draw that costs its cards.
             """,
        3 => """
             Bracket 3 (Upgraded):
             - The format's efficient staples belong here. A card that is near-universal
               because it is efficient is a strength, not a compromise (doctrine §9.4).
             - Land tutors are fine. Prefer targeted answers and clean interaction.
             - Avoid pieces whose job is to stop the table playing rather than to advance
               this deck's plan.
             - Focus: cards that make the commander's strategy work, at a fair rate.
             """,
        4 => """
             Bracket 4 (Optimized):
             - Maximum power short of cEDH. High-impact cards are appropriate where they
               serve the plan, including fast mana and efficient tutors.
             - Judge by contribution, not by reputation: a strong card in the wrong deck is
               still the wrong card (doctrine §6.4, §8 T6).
             """,
        5 => """
             Bracket 5 (cEDH):
             - Efficiency is the only criterion. Fast mana, the best tutors, free interaction,
               and the fastest reliable win the colours support.
             - Every slot is judged on how much it shortens or protects the win.
             """,
        _ => "Bracket 3 (Upgraded): efficient staples and clean interaction, nothing built to lock the table.",
    };

    /// <summary>
    /// What a price tier means for how the deck is put together.
    /// </summary>
    /// <remarks>
    /// It no longer states the ceiling as a rule to obey, because the candidate pool now
    /// enforces it: cards above the tier's limit are not in the list the model is choosing
    /// from, so there is nothing here to police. See <see cref="PriceCeiling"/>.
    /// <para>
    /// The prose used to be the only control, and it could not work. Prices are not among
    /// the facts the model is given (doctrine §0.1 supplies structured fields only), so
    /// asking it to keep cards under three dollars asked it to recall a market it cannot
    /// see — and a measured budget build came back with 13 of 99 cards over the ceiling,
    /// five of them between thirteen and sixteen dollars. What is left here is the part a
    /// filter cannot express: where to spend the room the tier leaves.
    /// </para>
    /// </remarks>
    internal static string DescribePrice(string priceRange) => priceRange switch
    {
        "budget" => """
                    BUDGET BUILD:
                    - Every card offered to you is already inside the budget. Choose freely
                      from the pool; you do not need to reason about price at all.
                    - Expect no expensive land cycles and no fast mana. Fixing comes from
                      taplands, utility lands that produce several colours, and ramp spells
                      that fetch basics — lean on those rather than treating the mana base
                      as the place to economise.
                    - A cheap pool rewards a lower curve and a clearer plan. Prefer cards
                      that do one thing well over cards that need three others to matter.
                    """,
        "mid" => """
                    MID-RANGE BUILD:
                    - Every card offered to you is already inside the budget. Choose freely
                      from the pool; you do not need to reason about price at all.
                    - There is room for real fixing and the format's ordinary staples. Spend
                      it on the mana base before the top end: a deck that casts its spells
                      on time beats a deck with a better finisher it cannot reach.
                    """,
        _ => "PRICE CONSTRAINT: None -- use the best cards available for the strategy.",
    };
}
