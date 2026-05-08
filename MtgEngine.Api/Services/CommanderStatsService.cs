using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Api.Dtos;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

public interface ICommanderStatsService
{
    Task<CommanderSummaryDto[]>       GetTopCommandersAsync(int limit = 50, int sinceMonths = 0);
    Task<CommanderProfileDto?>        GetCommanderProfileAsync(string oracleId);
    Task<CommanderCardsDto>           GetCommanderCardsAsync(string oracleId, int limit = 100);
    Task<CommanderHistoryPointDto[]>  GetCommanderHistoryAsync(string oracleId, int months = 12);
    Task<SimilarCommanderDto[]>       GetSimilarCommandersAsync(string oracleId, int limit = 6);
    Task<CommanderDeckDto[]>          GetCommanderDecksAsync(string oracleId, int limit = 50);
}

public sealed class CommanderStatsService : ICommanderStatsService
{
    private readonly MtgEngineDbContext _context;
    private readonly IScryfallService   _scryfall;

    private static readonly string[] ColorOrder = ["W", "U", "B", "R", "G"];

    public CommanderStatsService(MtgEngineDbContext context, IScryfallService scryfall)
    {
        _context  = context;
        _scryfall = scryfall;
    }

    public async Task<CommanderSummaryDto[]> GetTopCommandersAsync(int limit = 50, int sinceMonths = 0)
    {
        // Join ForumPosts → Collections to get (commanderOracleId, deckCount)
        var posts = _context.ForumPosts.AsQueryable();
        if (sinceMonths > 0)
        {
            var cutoff = DateTime.UtcNow.AddMonths(-sinceMonths);
            posts = posts.Where(fp => fp.PublishedAt >= cutoff);
        }

        var grouped = await posts
            .Join(
                _context.Collections.Where(c => c.IsDeck && c.CommanderOracleId != null),
                fp => fp.DeckId,
                c  => c.Id,
                (fp, c) => new { c.CommanderOracleId })
            .GroupBy(x => x.CommanderOracleId!)
            .Select(g => new { OracleId = g.Key, DeckCount = g.Count() })
            .OrderByDescending(x => x.DeckCount)
            .Take(limit)
            .ToListAsync();

        var results = new List<CommanderSummaryDto>(grouped.Count);
        for (int i = 0; i < grouped.Count; i++)
        {
            var item = grouped[i];
            var card = await _scryfall.GetByOracleIdAsync(item.OracleId);
            if (card == null) continue;

            results.Add(new CommanderSummaryDto
            {
                OracleId        = item.OracleId,
                Name            = card.Name,
                ImageUri        = card.ImageUriNormal,
                ImageUriArtCrop = card.ImageUriArtCrop,
                ColorIdentity   = ToColorLetters(card.ColorIdentity),
                ManaCost        = card.ManaCostRaw,
                DeckCount       = item.DeckCount,
                Rank            = i + 1,
            });
        }

        return [..results];
    }

    public async Task<CommanderProfileDto?> GetCommanderProfileAsync(string oracleId)
    {
        var card = await _scryfall.GetByOracleIdAsync(oracleId);
        if (card == null) return null;

        // Deck count from published forum posts
        var deckCount = await _context.ForumPosts
            .Join(
                _context.Collections.Where(c => c.IsDeck && c.CommanderOracleId == oracleId),
                fp => fp.DeckId,
                c  => c.Id,
                (fp, c) => fp.Id)
            .CountAsync();

        // Rank: how many commanders have more decks
        var rank = await _context.ForumPosts
            .Join(
                _context.Collections.Where(c => c.IsDeck && c.CommanderOracleId != null),
                fp => fp.DeckId,
                c  => c.Id,
                (fp, c) => new { c.CommanderOracleId })
            .GroupBy(x => x.CommanderOracleId!)
            .Select(g => new { OracleId = g.Key, Count = g.Count() })
            .Where(x => x.Count > deckCount)
            .CountAsync() + 1;

        // Top tags from decks published with this commander
        var deckIds = await _context.ForumPosts
            .Join(
                _context.Collections.Where(c => c.IsDeck && c.CommanderOracleId == oracleId),
                fp => fp.DeckId, c => c.Id,
                (fp, c) => c.Id)
            .ToListAsync();

        var tagGroups = await _context.Collections
            .Where(c => deckIds.Contains(c.Id))
            .Select(c => c.Tags)
            .ToListAsync();

        var tagCounts = tagGroups
            .SelectMany(tags => tags)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .Select(g => new TagCountDto(g.Key, g.Count()))
            .OrderByDescending(t => t.Count)
            .Take(6)
            .ToArray();

        return new CommanderProfileDto
        {
            OracleId        = oracleId,
            Name            = card.Name,
            ImageUri        = card.ImageUriNormal,
            ImageUriArtCrop = card.ImageUriArtCrop,
            ColorIdentity   = ToColorLetters(card.ColorIdentity),
            ManaCost        = card.ManaCostRaw,
            OracleText      = card.OracleText,
            DeckCount       = deckCount,
            Rank            = rank,
            TopTags         = tagCounts,
        };
    }

    public async Task<CommanderCardsDto> GetCommanderCardsAsync(string oracleId, int limit = 100)
    {
        // All published-deck IDs for this commander
        var deckIds = await _context.ForumPosts
            .Join(
                _context.Collections.Where(c => c.IsDeck && c.CommanderOracleId == oracleId),
                fp => fp.DeckId, c => c.Id,
                (fp, c) => c.Id)
            .Distinct()
            .ToListAsync();

        var totalDecks = deckIds.Count;
        if (totalDecks == 0)
            return new CommanderCardsDto { TotalDecks = 0, Cards = [] };

        // Card inclusion counts — exclude the commander itself and non-main board
        var cardCounts = await _context.CollectionCards
            .Where(cc => deckIds.Contains(cc.CollectionId)
                      && cc.Board == "main"
                      && cc.OracleId != oracleId)
            .GroupBy(cc => cc.OracleId)
            .Select(g => new
            {
                OracleId  = g.Key,
                DeckCount = g.Select(cc => cc.CollectionId).Distinct().Count(),
            })
            .OrderByDescending(x => x.DeckCount)
            .Take(limit)
            .ToListAsync();

        var entries = new List<CommanderCardEntryDto>(cardCounts.Count);
        foreach (var item in cardCounts)
        {
            var cardDef = await _scryfall.GetByOracleIdAsync(item.OracleId);
            if (cardDef == null) continue;
            if (cardDef.CardTypes.HasFlag(MtgEngine.Domain.Enums.CardType.Land)) continue;

            entries.Add(new CommanderCardEntryDto
            {
                Card             = MapToCardDto(cardDef),
                DeckCount        = item.DeckCount,
                TotalDecks       = totalDecks,
                InclusionPercent = Math.Round((double)item.DeckCount / totalDecks * 100, 1),
                IsGameChanger    = cardDef.GameChanger,
            });
        }

        return new CommanderCardsDto
        {
            TotalDecks = totalDecks,
            Cards      = [..entries],
        };
    }

    public async Task<CommanderHistoryPointDto[]> GetCommanderHistoryAsync(string oracleId, int months = 12)
    {
        var cutoff = DateTime.UtcNow.AddMonths(-months);

        var dates = await _context.ForumPosts
            .Join(
                _context.Collections.Where(c => c.IsDeck && c.CommanderOracleId == oracleId),
                fp => fp.DeckId, c => c.Id,
                (fp, c) => fp.PublishedAt)
            .Where(d => d >= cutoff)
            .ToListAsync();

        // Group by year-month and compute cumulative total
        var byMonth = dates
            .GroupBy(d => new { d.Year, d.Month })
            .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month)
            .Select(g => new { Label = $"{g.Key.Year}-{g.Key.Month:D2}", Count = g.Count() })
            .ToList();

        // Fill in any missing months with 0
        var result = new List<CommanderHistoryPointDto>();
        for (int i = months - 1; i >= 0; i--)
        {
            var dt    = DateTime.UtcNow.AddMonths(-i);
            var label = $"{dt.Year}-{dt.Month:D2}";
            var found = byMonth.FirstOrDefault(x => x.Label == label);
            result.Add(new CommanderHistoryPointDto(label, found?.Count ?? 0));
        }

        return [..result];
    }

    public async Task<SimilarCommanderDto[]> GetSimilarCommandersAsync(string oracleId, int limit = 6)
    {
        // Get the oracle IDs of cards used in this commander's decks
        var deckIds = await _context.ForumPosts
            .Join(
                _context.Collections.Where(c => c.IsDeck && c.CommanderOracleId == oracleId),
                fp => fp.DeckId, c => c.Id,
                (fp, c) => c.Id)
            .Distinct()
            .ToListAsync();

        if (deckIds.Count == 0) return [];

        var ourCards = await _context.CollectionCards
            .Where(cc => deckIds.Contains(cc.CollectionId) && cc.Board == "main" && cc.OracleId != oracleId)
            .Select(cc => cc.OracleId)
            .Distinct()
            .ToListAsync();

        var ourSet = ourCards.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Find other commanders and their card sets
        var otherCommanders = await _context.ForumPosts
            .Join(
                _context.Collections.Where(c => c.IsDeck && c.CommanderOracleId != null && c.CommanderOracleId != oracleId),
                fp => fp.DeckId, c => c.Id,
                (fp, c) => new { c.CommanderOracleId, c.Id })
            .GroupBy(x => x.CommanderOracleId!)
            .Select(g => new { OracleId = g.Key, DeckIds = g.Select(x => x.Id).ToList() })
            .ToListAsync();

        var scored = new List<(string OracleId, int SharedCards, int DeckCount)>();
        foreach (var cmd in otherCommanders)
        {
            var theirCards = await _context.CollectionCards
                .Where(cc => cmd.DeckIds.Contains(cc.CollectionId) && cc.Board == "main")
                .Select(cc => cc.OracleId)
                .Distinct()
                .ToListAsync();

            var shared = theirCards.Count(c => ourSet.Contains(c));
            if (shared > 0)
                scored.Add((cmd.OracleId, shared, cmd.DeckIds.Count));
        }

        var top = scored.OrderByDescending(x => x.SharedCards).Take(limit).ToList();

        var results = new List<SimilarCommanderDto>(top.Count);
        int rank = 1;
        foreach (var item in top)
        {
            var card = await _scryfall.GetByOracleIdAsync(item.OracleId);
            if (card == null) continue;
            results.Add(new SimilarCommanderDto
            {
                OracleId        = item.OracleId,
                Name            = card.Name,
                ImageUri        = card.ImageUriNormal,
                ImageUriArtCrop = card.ImageUriArtCrop,
                ColorIdentity   = ToColorLetters(card.ColorIdentity),
                DeckCount       = item.DeckCount,
                SharedCards     = item.SharedCards,
                Rank            = rank++,
            });
        }

        return [..results];
    }

    public async Task<CommanderDeckDto[]> GetCommanderDecksAsync(string oracleId, int limit = 50)
    {
        var rows = await _context.ForumPosts
            .Join(
                _context.Collections.Where(c => c.IsDeck && c.CommanderOracleId == oracleId),
                fp => fp.DeckId,
                c  => c.Id,
                (fp, c) => new
                {
                    fp.Id,
                    fp.DeckId,
                    fp.AuthorUsername,
                    fp.PublishedAt,
                    fp.ColorIdentityJson,
                    c.Name,
                    c.Description,
                    c.Tags,
                })
            .OrderByDescending(x => x.PublishedAt)
            .Take(limit)
            .ToListAsync();

        var deckIds = rows.Select(r => r.DeckId).ToList();
        var cardCounts = await _context.CollectionCards
            .Where(cc => deckIds.Contains(cc.CollectionId) && cc.Board == "main")
            .GroupBy(cc => cc.CollectionId)
            .Select(g => new { DeckId = g.Key, Count = g.Count() })
            .ToListAsync();
        var countMap = cardCounts.ToDictionary(x => x.DeckId, x => x.Count);

        return [.. rows.Select(r =>
        {
            var colors = System.Text.Json.JsonSerializer.Deserialize<string[]>(
                r.ColorIdentityJson, (System.Text.Json.JsonSerializerOptions?)null) ?? [];
            return new CommanderDeckDto
            {
                ForumPostId    = r.Id,
                DeckId         = r.DeckId,
                Name           = r.Name,
                Description    = r.Description,
                AuthorUsername = r.AuthorUsername,
                PublishedAt    = r.PublishedAt,
                CardCount      = countMap.TryGetValue(r.DeckId, out var cnt) ? cnt : 0,
                Bracket        = EstimateBracket(r.Tags),
                Tags           = [.. r.Tags],
                ColorIdentity  = colors,
            };
        })];
    }

    private static int EstimateBracket(List<string> tags)
    {
        foreach (var tag in tags.Select(t => t.ToLowerInvariant()))
        {
            if (tag is "competitive" or "optimized") return 4;
            if (tag is "spicy")                      return 4;
            if (tag is "tuned" or "upgraded" or "focused" or "streamlined" or "refined") return 3;
            if (tag is "budget" or "casual")         return 2;
        }
        return 3;
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static string[] ToColorLetters(IReadOnlyList<ManaColor> colors) =>
        ColorOrder.Where(l => colors.Any(c => ColorToLetter(c) == l)).ToArray();

    private static string ColorToLetter(ManaColor c) => c switch
    {
        ManaColor.White => "W",
        ManaColor.Blue  => "U",
        ManaColor.Black => "B",
        ManaColor.Red   => "R",
        ManaColor.Green => "G",
        _               => "C",
    };

    private static CardDto MapToCardDto(CardDefinition def) => new()
    {
        CardId             = def.OracleId,
        OracleId           = def.OracleId,
        Name               = def.Name,
        ManaCost           = string.IsNullOrEmpty(def.ManaCostRaw) ? def.ManaCost.ToString() : def.ManaCostRaw,
        ManaValue          = def.Cmc,
        CardTypes          = def.CardTypes.ToString().Split(", ")
                               .Where(t => Enum.IsDefined(typeof(CardTypeDto), t))
                               .Select(t => Enum.Parse<CardTypeDto>(t))
                               .ToArray(),
        Subtypes           = [..def.Subtypes],
        Supertypes         = [..def.Supertypes],
        OracleText         = def.OracleText,
        Power              = def.Power,
        Toughness          = def.Toughness,
        StartingLoyalty    = def.StartingLoyalty,
        Keywords           = def.Keywords.ToString().Split(", ")
                               .Where(k => !string.IsNullOrEmpty(k) && k != "None")
                               .ToArray(),
        ImageUriNormal     = def.ImageUriNormal,
        ImageUriNormalBack = def.ImageUriNormalBack,
        ImageUriSmall      = def.ImageUriSmall,
        ImageUriArtCrop    = def.ImageUriArtCrop,
        ColorIdentity      = def.ColorIdentity
                               .Select(c => c switch
                               {
                                   ManaColor.White => ManaColorDto.W,
                                   ManaColor.Blue  => ManaColorDto.U,
                                   ManaColor.Black => ManaColorDto.B,
                                   ManaColor.Red   => ManaColorDto.R,
                                   ManaColor.Green => ManaColorDto.G,
                                   _               => ManaColorDto.C,
                               })
                               .ToArray(),
        FlavorText         = def.FlavorText,
        Artist             = def.Artist,
        SetCode            = def.SetCode,
        Rarity             = def.Rarity,
        Legalities         = def.Legalities.ToDictionary(kv => kv.Key, kv => kv.Value),
        GameChanger        = def.GameChanger,
    };
}
