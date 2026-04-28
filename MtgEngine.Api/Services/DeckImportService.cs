using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MtgEngine.Api.Dtos;

namespace MtgEngine.Api.Services;

// ---- Plain-text deck list parser ----------------------------------------

public static class DeckListParser
{
    private static readonly HashSet<string> SectionHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Deck", "Sideboard", "Commander", "Companion", "Maybeboard", "About",
        "Land", "Lands", "Creature", "Creatures", "Instant", "Instants",
        "Sorcery", "Sorceries", "Enchantment", "Enchantments",
        "Artifact", "Artifacts", "Planeswalker", "Planeswalkers",
        "Other Spells", "MTGA Format", "Arena Deck",
    };

    public record ParseResult(
        string?                         DetectedFormat,
        string?                         CommanderName,
        IReadOnlyList<(int Qty, string Name)> Cards);

    public static ParseResult Parse(string text)
    {
        var lines   = text.ReplaceLineEndings("\n").Split('\n');
        var cards   = new List<(int, string)>();
        string? commanderName      = null;
        string? format             = null;
        bool    inCommanderSection = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line)) { inCommanderSection = false; continue; }
            if (line.StartsWith("//") || line.StartsWith('#')) continue;

            // Section headers (bare word, optional trailing colon)
            var headerKey = line.TrimEnd(':');
            if (SectionHeaders.Contains(headerKey))
            {
                inCommanderSection = headerKey.Equals("Commander", StringComparison.OrdinalIgnoreCase);
                if (inCommanderSection) format ??= "commander";
                continue;
            }

            // Strip trailing set/collector notation: " (M21) 148a" or " (M21)"
            var cleaned = Regex.Replace(line, @"\s+\([A-Za-z0-9]+\)(\s+\d+[a-z]?)?$", "").Trim();

            // Commander marker (*CMDR* or [Commander])
            bool isCommanderCard =
                Regex.IsMatch(cleaned, @"\*CMDR\*", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(cleaned, @"\[Commander\]", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"\s*(\*CMDR\*|\[Commander\])\s*", " ", RegexOptions.IgnoreCase).Trim();

            // "N[x] <name>" or just "<name>" (qty = 1)
            var m = Regex.Match(cleaned, @"^(\d+)[xX]?\s+(.+)$");
            string name;
            int    qty;
            if (m.Success)
            {
                qty  = int.Parse(m.Groups[1].Value);
                name = m.Groups[2].Value.Trim();
            }
            else
            {
                qty  = 1;
                name = cleaned;
            }

            if (string.IsNullOrWhiteSpace(name)) continue;

            cards.Add((qty, name));
            if ((inCommanderSection || isCommanderCard) && commanderName == null)
                commanderName = name;
        }

        return new ParseResult(format, commanderName, cards);
    }
}

// ---- Import service -------------------------------------------------------

public sealed class DeckImportService
{
    private readonly IScryfallService  _scryfall;
    private readonly ICollectionService _collection;
    private readonly IHttpClientFactory _httpFactory;

    public DeckImportService(
        IScryfallService   scryfall,
        ICollectionService collection,
        IHttpClientFactory httpFactory)
    {
        _scryfall   = scryfall;
        _collection = collection;
        _httpFactory = httpFactory;
    }

    public async Task<ImportDeckResult> ImportAsync(string userId, ImportDeckRequest request)
    {
        string  text;
        string? detectedName          = null;
        string? detectedFormat        = request.Format;
        string? detectedCommanderName = null;

        if (!string.IsNullOrWhiteSpace(request.Url))
        {
            var fetched           = await FetchFromUrlAsync(request.Url.Trim());
            text                  = fetched.Text;
            detectedName          = fetched.Name;
            detectedFormat      ??= fetched.Format;
            detectedCommanderName = fetched.CommanderName;
        }
        else
        {
            text = request.Text ?? string.Empty;
        }

        var parsed  = DeckListParser.Parse(text);
        var format  = detectedFormat ?? parsed.DetectedFormat;
        var deckName = !string.IsNullOrWhiteSpace(request.Name)
            ? request.Name.Trim()
            : (detectedName ?? "Imported Deck");

        var deck = await _collection.CreateDeckAsync(userId,
            new CreateDeckRequest(deckName, null, format));

        var unresolved  = new List<string>();
        int resolved    = 0;
        string? commanderName      = detectedCommanderName ?? parsed.CommanderName;
        string? commanderOracleId  = null;

        foreach (var (qty, name) in parsed.Cards)
        {
            var def = await _scryfall.GetByNameAsync(name);
            if (def == null)
            {
                unresolved.Add($"{qty}x {name}");
                continue;
            }

            var printings  = await _scryfall.GetPrintingsAsync(def.OracleId);
            var scryfallId = printings.FirstOrDefault()?.ScryfallId;

            await _collection.AddCardToCollectionAsync(deck.Id, userId,
                new AddCardToCollectionRequest(def.OracleId, scryfallId, qty));
            resolved++;

            if (commanderName != null &&
                string.Equals(name, commanderName, StringComparison.OrdinalIgnoreCase))
            {
                commanderOracleId = def.OracleId;
            }
        }

        if (commanderOracleId != null || format == "commander")
        {
            await _collection.UpdateDeckAsync(deck.Id, userId,
                new UpdateDeckRequest(deck.Name, deck.CoverUri, format ?? "commander", commanderOracleId));
        }

        var fullDeck = await _collection.GetDeckAsync(deck.Id, userId) ?? deck;
        return new ImportDeckResult(fullDeck, resolved, parsed.Cards.Count, unresolved);
    }

    // ---- URL fetchers -------------------------------------------------------

    private async Task<(string Text, string? Name, string? Format, string? CommanderName)>
        FetchFromUrlAsync(string url)
    {
        var moxfield = Regex.Match(url, @"moxfield\.com/decks/([A-Za-z0-9_-]+)");
        if (moxfield.Success) return await FetchMoxfieldAsync(moxfield.Groups[1].Value);

        var archidekt = Regex.Match(url, @"archidekt\.com/decks/(\d+)");
        if (archidekt.Success) return await FetchArchidektAsync(archidekt.Groups[1].Value);

        throw new InvalidOperationException(
            "Unsupported deck URL. Paste a Moxfield or Archidekt link, " +
            "or use the Text tab to paste a deck list directly.");
    }

    private async Task<(string Text, string? Name, string? Format, string? CommanderName)>
        FetchMoxfieldAsync(string deckId)
    {
        var client  = _httpFactory.CreateClient("DeckImport");
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.moxfield.com/v2/decks/all/{deckId}");
        request.Headers.Add("Accept",  "application/json, text/plain, */*");
        request.Headers.Add("Origin",  "https://moxfield.com");
        request.Headers.Add("Referer", "https://moxfield.com/");
        var resp = await client.SendAsync(request);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"Moxfield returned {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}");
        }

        using var doc  = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root       = doc.RootElement;
        string? name   = root.TryGetProperty("name",   out var np) ? np.GetString() : null;
        string? format = root.TryGetProperty("format", out var fp) ? fp.GetString() : null;

        var sb = new StringBuilder();
        string? commanderName = null;

        if (root.TryGetProperty("boards", out var boards))
        {
            if (boards.TryGetProperty("commanders", out var cmdrBoard) &&
                cmdrBoard.TryGetProperty("cards", out var cmdrCards))
            {
                sb.AppendLine("Commander");
                foreach (var entry in cmdrCards.EnumerateObject())
                {
                    var qty      = entry.Value.TryGetProperty("quantity", out var qp) ? qp.GetInt32() : 1;
                    var cardName = entry.Value.TryGetProperty("card", out var cp) &&
                                   cp.TryGetProperty("name", out var cnp) ? cnp.GetString() : null;
                    if (cardName == null) continue;
                    commanderName ??= cardName;
                    sb.AppendLine($"{qty} {cardName}");
                }
                sb.AppendLine();
            }

            if (boards.TryGetProperty("mainboard", out var mainBoard) &&
                mainBoard.TryGetProperty("cards", out var mainCards))
            {
                sb.AppendLine("Deck");
                foreach (var entry in mainCards.EnumerateObject())
                {
                    var qty      = entry.Value.TryGetProperty("quantity", out var qp) ? qp.GetInt32() : 1;
                    var cardName = entry.Value.TryGetProperty("card", out var cp) &&
                                   cp.TryGetProperty("name", out var cnp) ? cnp.GetString() : null;
                    if (cardName != null) sb.AppendLine($"{qty} {cardName}");
                }
            }
        }

        return (sb.ToString(), name, format, commanderName);
    }

    private async Task<(string Text, string? Name, string? Format, string? CommanderName)>
        FetchArchidektAsync(string deckId)
    {
        var client = _httpFactory.CreateClient("DeckImport");
        var resp   = await client.GetAsync($"https://archidekt.com/api/decks/{deckId}/small/");
        resp.EnsureSuccessStatusCode();

        using var doc  = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root       = doc.RootElement;
        string? name   = root.TryGetProperty("name", out var np) ? np.GetString() : null;
        string? format = null;
        if (root.TryGetProperty("deckFormat", out var fp))
            format = fp.GetInt32() switch { 3 => "commander", _ => null };

        var sb = new StringBuilder();
        string? commanderName = null;

        if (root.TryGetProperty("cards", out var cards))
        {
            foreach (var card in cards.EnumerateArray())
            {
                var qty      = card.TryGetProperty("quantity", out var qp) ? qp.GetInt32() : 1;
                string? cardName = null;
                if (card.TryGetProperty("card", out var cp) &&
                    cp.TryGetProperty("oracleCard", out var op) &&
                    op.TryGetProperty("name", out var cnp))
                    cardName = cnp.GetString();
                if (cardName == null) continue;

                bool isCommander = false;
                if (card.TryGetProperty("categories", out var cats))
                    foreach (var cat in cats.EnumerateArray())
                        if (cat.GetString()?.Equals("Commander", StringComparison.OrdinalIgnoreCase) == true)
                        { isCommander = true; break; }

                if (isCommander) commanderName ??= cardName;
                sb.AppendLine($"{qty} {cardName}");
            }
        }

        return (sb.ToString(), name, format, commanderName);
    }
}
