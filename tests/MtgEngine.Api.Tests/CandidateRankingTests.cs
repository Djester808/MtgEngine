using Microsoft.Extensions.Logging.Abstractions;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Tests;

/// <summary>
/// Ranking is what decides which cards a player sees. It used to be a separate model pass
/// whose picks the scorer then disagreed with — a category could show a 76% card while its
/// own pool held six above 80%. These tests pin the ordering contract and the property that
/// makes the suggestion list and the "show more" list the same list.
/// </summary>
public class CandidateRankingTests
{
    // ---- Fakes ------------------------------------------------------------

    /// <summary>Returns a fixed pool and records what it was asked for.</summary>
    private sealed class FakePool(params CardDefinition[] cards) : StubScryfallService
    {
        public int Calls { get; private set; }
        public List<int> RequestedLimits { get; } = [];

        public override Task<(CardDefinition[] Cards, int Total)> GetCandidatePoolAsync(
            IReadOnlySet<ManaColor> commanderColors, CardDefinition? commander = null, string? query = null,
            IReadOnlySet<string>? setCodes = null, bool gameChangersOnly = false,
            CardType types = CardType.None, int? cmcMin = null, int? cmcMax = null,
            int limit = 50, int offset = 0)
        {
            Calls++;
            RequestedLimits.Add(limit);
            return Task.FromResult((cards.Take(limit).ToArray(), cards.Length));
        }
    }

    /// <summary>Scores from a lookup table and counts how many cards it was handed.</summary>
    private sealed class FakeScorer(Dictionary<string, int> scores) : StubSynergyService
    {
        public int Calls { get; private set; }
        public int TotalCardsScored { get; private set; }
        public IReadOnlyList<string>? LastFocus { get; private set; }

        public override Task<ScoredCardDto[]> ScoreCardsAsync(
            string commanderOracleId, IReadOnlyList<string> cardOracleIds,
            ScoringMode mode = ScoringMode.Ideal, DeckProfile? profile = null,
            IReadOnlyList<string>? focus = null)
        {
            Calls++;
            TotalCardsScored += cardOracleIds.Count;
            LastFocus = focus;

            return Task.FromResult(cardOracleIds
                .Where(scores.ContainsKey)
                .Select(id => new ScoredCardDto { OracleId = id, Name = id, Score = scores[id], Reason = $"{id} reason" })
                .ToArray());
        }
    }

    private static CardDefinition Card(string id) => new()
    {
        OracleId = id,
        Name = id,
        ColorIdentity = [ManaColor.Green],
    };

    private static CandidateRanking Ranking(IScryfallService pool, ISynergyService scorer) =>
        new(pool, scorer, NullLogger<CandidateRanking>.Instance);

    private static readonly CardDefinition Commander = Card("commander");

    // ---- Ordering ----------------------------------------------------------

    [Fact]
    public async Task Cards_come_back_highest_score_first()
    {
        var pool = new FakePool(Card("a"), Card("b"), Card("c"));
        var scorer = new FakeScorer(new() { ["a"] = 40, ["b"] = 90, ["c"] = 65 });

        var result = await Ranking(pool, scorer).RankAsync(new RankRequest(Commander, Limit: 3));

        Assert.Equal(["b", "c", "a"], result.Cards.Select(c => c.Card.Name));
        Assert.Equal([90, 65, 40], result.Cards.Select(c => c.Score));
    }

    /// <summary>
    /// The whole point of ranking server-side: page one is the head of the same list page two
    /// continues, so a category and its "show more" cannot disagree.
    /// </summary>
    [Fact]
    public async Task A_short_page_is_the_head_of_a_longer_one()
    {
        var cards = Enumerable.Range(0, 10).Select(i => Card($"c{i}")).ToArray();
        var scores = cards.ToDictionary(c => c.OracleId, c => 100 - int.Parse(c.OracleId[1..]) * 5);

        var top3 = await Ranking(new FakePool(cards), new FakeScorer(scores))
            .RankAsync(new RankRequest(Commander, Limit: 3));
        var top10 = await Ranking(new FakePool(cards), new FakeScorer(scores))
            .RankAsync(new RankRequest(Commander, Limit: 10));

        Assert.Equal(
            top3.Cards.Select(c => c.Card.Name),
            top10.Cards.Take(3).Select(c => c.Card.Name));
    }

    [Fact]
    public async Task Paging_continues_where_the_previous_page_stopped()
    {
        var cards = Enumerable.Range(0, 6).Select(i => Card($"c{i}")).ToArray();
        var scores = cards.ToDictionary(c => c.OracleId, c => 100 - int.Parse(c.OracleId[1..]));

        var page1 = await Ranking(new FakePool(cards), new FakeScorer(scores))
            .RankAsync(new RankRequest(Commander, Limit: 3, Offset: 0));
        var page2 = await Ranking(new FakePool(cards), new FakeScorer(scores))
            .RankAsync(new RankRequest(Commander, Limit: 3, Offset: 3));

        Assert.Empty(page1.Cards.Select(c => c.Card.Name)
            .Intersect(page2.Cards.Select(c => c.Card.Name)));

        Assert.True(page1.Cards.Last().Score >= page2.Cards.First().Score);
    }

    /// <summary>An unscored card sinks rather than being treated as a zero-scoring one.</summary>
    [Fact]
    public async Task Unscored_cards_sort_below_scored_ones()
    {
        var pool = new FakePool(Card("unscored"), Card("scored"));
        var scorer = new FakeScorer(new() { ["scored"] = 10 });

        var result = await Ranking(pool, scorer).RankAsync(new RankRequest(Commander, Limit: 2));

        Assert.Equal("scored", result.Cards[0].Card.Name);
        Assert.Equal("unscored", result.Cards[1].Card.Name);
        Assert.True(result.Cards[1].Score < 0);
    }

    // ---- The batch path ----------------------------------------------------

    /// <summary>
    /// Ranking pools one at a time meant the same card scored once per pool, each run waiting
    /// on the last. The union is scored once instead.
    /// </summary>
    [Fact]
    public async Task Overlapping_pools_are_scored_once_between_them()
    {
        var shared = new[] { Card("a"), Card("b") };
        var pool = new FakePool(shared);
        var scorer = new FakeScorer(new() { ["a"] = 50, ["b"] = 60 });

        var results = await Ranking(pool, scorer).RankManyAsync([
            new RankRequest(Commander, Limit: 2),
            new RankRequest(Commander, Limit: 2),
            new RankRequest(Commander, Limit: 2),
        ]);

        Assert.Equal(3, results.Count);
        Assert.Equal(1, scorer.Calls);
        Assert.Equal(2, scorer.TotalCardsScored);   // not 6
    }

    [Fact]
    public async Task Every_pool_gets_its_own_ranked_result()
    {
        var pool = new FakePool(Card("a"), Card("b"));
        var scorer = new FakeScorer(new() { ["a"] = 10, ["b"] = 20 });

        var results = await Ranking(pool, scorer).RankManyAsync([
            new RankRequest(Commander, Limit: 1),
            new RankRequest(Commander, Limit: 2),
        ]);

        Assert.Single(results[0].Cards);
        Assert.Equal(2, results[1].Cards.Count);
    }

    [Fact]
    public async Task Ranking_nothing_returns_nothing() =>
        Assert.Empty(await Ranking(new FakePool(), new FakeScorer([])).RankManyAsync([]));

    [Fact]
    public async Task An_empty_pool_ranks_to_an_empty_page()
    {
        var result = await Ranking(new FakePool(), new FakeScorer([]))
            .RankAsync(new RankRequest(Commander, Limit: 8));

        Assert.Empty(result.Cards);
        Assert.Equal(0, result.Total);
    }

    // ---- Contract details ---------------------------------------------------

    [Fact]
    public async Task The_focus_is_passed_through_to_the_scorer()
    {
        var scorer = new FakeScorer(new() { ["a"] = 1 });

        await Ranking(new FakePool(Card("a")), scorer)
            .RankAsync(new RankRequest(Commander, Focus: ["wolf"], Limit: 1));

        Assert.Equal(["wolf"], scorer.LastFocus);
    }

    /// <summary>
    /// Total is the pageable count — how far the client can walk in synergy order — not how
    /// deep this one request happened to score. A pool under the score cap is fully pageable,
    /// so Total is the whole pool even when a single request scored only its window.
    /// </summary>
    [Fact]
    public async Task Total_reports_the_pageable_pool_not_the_single_window()
    {
        var cards = Enumerable.Range(0, 50).Select(i => Card($"c{i}")).ToArray();
        var scores = cards.ToDictionary(c => c.OracleId, _ => 50);

        var result = await Ranking(new FakePool(cards), new FakeScorer(scores))
            .RankAsync(new RankRequest(Commander, Limit: 5, ScoreWindow: 10));

        Assert.Equal(10, result.Scored);   // only the window scored in this request
        Assert.Equal(50, result.Total);    // ...but all 50 are reachable by paging
    }

    /// <summary>Total is capped at the deepest the ranker will ever score.</summary>
    [Fact]
    public async Task Total_is_capped_at_the_max_score_window_for_large_pools()
    {
        var cards = Enumerable.Range(0, 500).Select(i => Card($"c{i}")).ToArray();
        var scores = cards.ToDictionary(c => c.OracleId, _ => 50);

        var result = await Ranking(new FakePool(cards), new FakeScorer(scores))
            .RankAsync(new RankRequest(Commander, Limit: 5, ScoreWindow: 10));

        Assert.Equal(CandidateRanking.MaxScoreWindow, result.Total);
    }

    /// <summary>Paging past the default window scores deep enough to fill the requested page.</summary>
    [Fact]
    public async Task Paging_past_the_window_still_returns_a_full_page()
    {
        var cards = Enumerable.Range(0, 200).Select(i => Card($"c{i}")).ToArray();
        // Descending scores so ordering is deterministic (c0 highest).
        var scores = cards.Select((c, i) => (c.OracleId, Score: 200 - i))
            .ToDictionary(x => x.OracleId, x => x.Score);

        var result = await Ranking(new FakePool(cards), new FakeScorer(scores))
            .RankAsync(new RankRequest(Commander, Limit: 10, Offset: 100, ScoreWindow: 10));

        // window = max(10, 100 + 10) = 110, so the page at offset 100 is scored and returned.
        Assert.Equal(10, result.Cards.Count);
    }

    [Fact]
    public async Task The_score_window_bounds_how_deep_the_pool_is_read()
    {
        var pool = new FakePool(Enumerable.Range(0, 100).Select(i => Card($"c{i}")).ToArray());

        await Ranking(pool, new FakeScorer([]))
            .RankAsync(new RankRequest(Commander, Limit: 8, ScoreWindow: 25));

        Assert.Equal(25, pool.RequestedLimits.Single());
    }

    /// <summary>A window smaller than the page would silently truncate the page.</summary>
    [Fact]
    public async Task The_window_is_never_smaller_than_the_page()
    {
        var pool = new FakePool(Enumerable.Range(0, 100).Select(i => Card($"c{i}")).ToArray());

        await Ranking(pool, new FakeScorer([]))
            .RankAsync(new RankRequest(Commander, Limit: 40, ScoreWindow: 10));

        Assert.Equal(40, pool.RequestedLimits.Single());
    }
}
