using System.Net;
using System.Text.Json;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Mapping;

namespace MtgEngine.Api.Services.Community;

public sealed class ExploreService(IScryfallService cards, IHttpClientFactory clients, IAiCacheService cache)
{
    public sealed record CachedPage(string? Json, DateTimeOffset FetchedAt);

    public async Task<ExploreEntry[]> Search(string query, string kind)
    {
        if (query.Length > 200 || kind is not ("card" or "commander"))
            throw new InvalidRequestException("Choose a valid card search.");
        var matches = kind == "commander" ? await cards.SearchCommandersAsync(query, 60) : await cards.SearchAsync(query, 60);
        return [.. matches.Select(c => new ExploreEntry(c.Name, DomainMapper.ToDto(c), Kind: kind))];
    }

    private async Task<CachedPage> Fetch(string path, Action<JsonDocument> validate, CancellationToken ct)
    {
        return await cache.GetOrCreateAsync("explore-edhrec", "original-parsers-v1", [path], async () =>
        {
            try
            {
                using var response = await clients.CreateClient("CommunityEdhrec").GetAsync(path, ct);
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return new CachedPage(null, DateTimeOffset.UtcNow);
                var text = await response.Content.ReadAsStringAsync(ct);
                // S3's missing-key response is sometimes 403. Other forbidden responses are failures.
                if (response.StatusCode == HttpStatusCode.Forbidden && text.Contains("<Code>NoSuchKey</Code>", StringComparison.Ordinal))
                    return new CachedPage(null, DateTimeOffset.UtcNow);
                response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(text);
                validate(document);
                return new CachedPage(text, DateTimeOffset.UtcNow);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
            {
                if (ct.IsCancellationRequested)
                    throw;
                throw new CommunityDataException("EDHREC could not be read. Please retry.");
            }
        }, TimeSpan.FromHours(24)) ?? throw new CommunityDataException("EDHREC could not be read. Please retry.");
    }

    private async Task<ExploreEntry> Entry(string name, double? synergy = null, int? decks = null,
        int? potential = null, double? trend = null, int quantity = 1, string kind = "card")
    {
        var card = await cards.GetLocalByNameAsync(name);
        return new(name, card is null ? null : DomainMapper.ToDto(card), synergy, decks, potential,
            potential > 0 && decks is not null ? (double)decks / potential : null, trend, quantity, kind);
    }

    public async Task<ExploreDirectory> Directory(CancellationToken ct)
    {
        var entries = new List<ExploreEntry>();
        var fetchedAt = DateTimeOffset.UtcNow;
        for (int index = 0; index < 5; index++)
        {
            var path = index == 0 ? "pages/commanders/year.json" : $"pages/commanders/year-past2years-{index}.json";
            var cached = await Fetch(path, d => EdhrecTopCommandersParser.Parse(d), ct);
            fetchedAt = cached.FetchedAt;
            if (cached.Json is null)
                break;
            using var doc = JsonDocument.Parse(cached.Json);
            var rows = EdhrecTopCommandersParser.Parse(doc);
            foreach (var row in rows)
                entries.Add((await Entry(row.Name, decks: row.DeckCount, kind: "commander")) with { Rank = row.Rank });
            if (rows.Length < 100)
                break;
        }
        return new(fetchedAt, [.. entries.DistinctBy(e => e.Name)]);
    }

    public async Task<ExplorePage> Page(string oracleId, string kind, string key, CancellationToken ct)
    {
        if (!Guid.TryParse(oracleId, out _))
            throw new InvalidRequestException("Choose a valid card.");
        var subject = await cards.GetByOracleIdAsync(oracleId, ct) ?? throw new ResourceNotFoundException("Card not found.");
        if (kind != "card" && !CommanderRules.IsCommanderEligible(subject))
            throw new InvalidRequestException("This card cannot lead a Commander deck.");
        if (!EdhrecVariant.TryParse(key, out var variant) || (kind == "card" && key.Length > 0))
            throw new InvalidRequestException("Choose a valid page variant.");
        var slug = EdhrecSlug.From(subject.Name);
        var options = new List<ExploreVariant> { new("", "All decks"), new("budget", "Budget"), new("expensive", "Expensive") };
        for (int bracket = 1; bracket <= 5; bracket++)
            options.Add(new($"bracket-{bracket}", $"Bracket {bracket}"));
        if (kind != "card")
        {
            var basePage = await Fetch(EdhrecVariant.Base.UpstreamPath(slug), d => EdhrecCommanderPageParser.Parse(d, slug), ct);
            if (basePage.Json is not null)
            {
                using var doc = JsonDocument.Parse(basePage.Json);
                var parsed = EdhrecCommanderPageParser.Parse(doc, slug);
                options.AddRange(parsed.Tags.Where(t => EdhrecVariant.TryParse("theme-" + t.Slug, out _))
                    .Select(t => new ExploreVariant("theme-" + t.Slug, string.IsNullOrWhiteSpace(t.Name) ? t.Slug : t.Name, t.Count)));
                if (variant.IsTheme && !parsed.Tags.Any(t => t.Slug == variant.ThemeSlug))
                    throw new InvalidRequestException("This theme is not listed for this commander.");
            }
            else if (variant.IsTheme)
                throw new InvalidRequestException("This commander has no listed themes.");
        }
        var path = kind switch { "card" => $"pages/cards/{slug}.json", "average" => variant.AverageDeckPath(slug), _ => variant.UpstreamPath(slug) };
        var fetched = await Fetch(path, d =>
        {
            if (kind == "average")
                EdhrecAverageDeckParser.Parse(d);
            else if (kind == "card")
                EdhrecCardPageParser.Parse(d, slug);
            else
                EdhrecCommanderPageParser.Parse(d, slug);
        }, ct);
        var sections = new List<ExploreSection>();
        var similar = new List<ExploreEntry>();
        var commanders = new List<ExploreEntry>();
        ExploreChart[] charts = [];
        int? deckCount = null, potential = null, rank = null;
        decimal? salt = null;
        if (fetched.Json is not null)
        {
            using var doc = JsonDocument.Parse(fetched.Json);
            charts = ExploreCharts.Read(doc.RootElement);
            if (kind == "average")
            {
                var deck = EdhrecAverageDeckParser.Parse(doc);
                foreach (var commander in deck.Commanders)
                    commanders.Add(await Entry(commander.N, kind: "commander"));
                foreach (var section in deck.Sections)
                {
                    var entries = new List<ExploreEntry>();
                    foreach (var row in section.Cards)
                        entries.Add(await Entry(row.N, quantity: row.Q));
                    sections.Add(new(section.Type, section.Type, [.. entries]));
                }
            }
            else
            {
                EdhrecStoredList[] lists;
                EdhrecStoredName[] related;
                if (kind == "card")
                {
                    var page = EdhrecCardPageParser.Parse(doc, slug);
                    (deckCount, potential, salt, lists, related) = (page.DeckCount, page.PotentialDecks, page.Salt, page.Lists, page.Similar);
                }
                else
                {
                    var page = EdhrecCommanderPageParser.Parse(doc, slug);
                    (deckCount, rank, lists, related) = (page.DeckCount, page.Rank, page.Lists, page.Similar);
                }
                foreach (var section in lists)
                {
                    var entries = new List<ExploreEntry>();
                    foreach (var row in section.Entries)
                        entries.Add(await Entry(row.N, row.S, row.D, row.P, row.T, kind: kind == "card" && (section.Tag.Contains("commander", StringComparison.OrdinalIgnoreCase) || section.Header.Contains("commander", StringComparison.OrdinalIgnoreCase)) ? "commander" : "card"));
                    sections.Add(new(section.Tag, section.Header, [.. entries]));
                }
                foreach (var row in related)
                    similar.Add(await Entry(row.N, kind: kind == "card" ? "card" : "commander"));
            }
        }
        charts = [.. charts, .. ExploreDeckShape.Read([.. sections], kind == "average")];
        if (kind == "commander" && fetched.Json is not null)
        {
            try
            {
                var average = await Page(oracleId, "average", key, ct);
                charts = [.. charts, .. average.Charts.Where(c => c.Title.StartsWith("Average deck", StringComparison.Ordinal))];
            }
            catch (CommunityDataException) { /* A missing optional deck shape does not hide recommendations. */ }
        }
        return new(fetched.Json is not null, "EDHREC", fetched.FetchedAt, kind, DomainMapper.ToDto(subject), key,
            deckCount, potential, rank, salt, [.. sections], kind == "card" ? [] : [.. options], [.. similar], [.. commanders], charts);
    }
}
