using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MtgEngine.Api.Services.Community;


namespace MtgEngine.Api.Tests;

/// <summary>
/// The commander page parser over the page EDHREC actually serves: the sections in order with their
/// figures, the themes a variant is checked against, the counts and the chart panels — and a loud
/// failure when the shape is not the one this version knows.
/// </summary>
public sealed class EdhrecCommanderPageParserTests
{
    /// <summary>The digest of the stored shape for the recorded Atraxa page under version v2.</summary>
    private const string Golden = "7744281adf1350828ba4153c91b3c55b3a05f66715a50c4055a51710f48c4999";

    [Fact]
    public void The_sections_keep_the_order_and_the_numbers_EDHREC_published()
    {
        using var document = EdhrecFixtures.Json(EdhrecFixtures.Commander);
        var page = EdhrecCommanderPageParser.Parse(document, "atraxa-praetors-voice");

        Assert.Equal(
            ["newcards", "highsynergycards", "topcards", "gamechangers", "creatures", "instants",
             "sorceries", "utilityartifacts", "enchantments", "planeswalkers", "utilitylands",
             "manaartifacts", "lands"],
            page.Lists.Select(list => list.Tag));
        Assert.Equal("High Synergy Cards", page.Lists[1].Header);

        var first = page.Lists[0].Entries[0];
        Assert.Equal("Sanctum Lurker", first.N);
        Assert.Equal(0.018803616376431912, first.S!.Value, 12);
        Assert.Equal(43, first.D);
        Assert.Equal(945, first.P);
        Assert.Equal(15.781935788148457, first.T!.Value, 12);
        // A name becomes an oracle id at the fetch, not here: the parser never sees the corpus.
        Assert.Null(first.Id);
    }

    [Fact]
    public void The_lands_section_is_stored_even_though_the_suggestion_pool_drops_it()
    {
        using var document = EdhrecFixtures.Json(EdhrecFixtures.Commander);
        var page = EdhrecCommanderPageParser.Parse(document, "atraxa-praetors-voice");

        var lands = page.Lists.Single(list => list.Tag == "lands");
        Assert.NotEmpty(lands.Entries);
    }

    [Fact]
    public void The_deck_count_and_rank_come_from_the_page_s_own_card()
    {
        using var document = EdhrecFixtures.Json(EdhrecFixtures.Commander);
        var page = EdhrecCommanderPageParser.Parse(document, "atraxa-praetors-voice");

        // Not at the top level, where a reader would look first: container.json_dict.card.
        Assert.Equal(44799, page.DeckCount);
        Assert.Equal(4, page.Rank);
        Assert.Equal("atraxa-praetors-voice", page.Slug);
    }

    [Fact]
    public void The_themes_carry_the_slug_a_variant_is_checked_against()
    {
        using var document = EdhrecFixtures.Json(EdhrecFixtures.Commander);
        var page = EdhrecCommanderPageParser.Parse(document, "atraxa-praetors-voice");

        Assert.Equal("infect", page.Tags[0].Slug);
        Assert.Equal("Infect", page.Tags[0].Name);
        Assert.Equal(4021, page.Tags[0].Count);
    }

    [Fact]
    public void The_similar_commanders_are_names_whichever_way_EDHREC_wrote_them()
    {
        using var document = EdhrecFixtures.Json(EdhrecFixtures.Commander);
        var page = EdhrecCommanderPageParser.Parse(document, "atraxa-praetors-voice");
        Assert.Equal("Atraxa, Grand Unifier", page.Similar[0].N);

        // The same list written as objects, which other pages use.
        using var objects = JsonDocument.Parse("""
            {"similar":[{"value":"Ixhel, Scion of Atraxa","alt":"Ixhel"}],
             "container":{"json_dict":{"cardlists":[]}}}
            """);
        Assert.Equal("Ixhel, Scion of Atraxa", EdhrecCommanderPageParser.Parse(objects, "x").Similar[0].N);
    }

    [Fact]
    public void The_counts_and_the_panels_are_kept_verbatim()
    {
        using var document = EdhrecFixtures.Json(EdhrecFixtures.Commander);
        var page = EdhrecCommanderPageParser.Parse(document, "atraxa-praetors-voice");

        using var counts = JsonDocument.Parse(page.Counts);
        Assert.Equal(3280, counts.RootElement.GetProperty("bracket_counts").GetProperty("2").GetInt32());
        Assert.Equal(4479, counts.RootElement.GetProperty("budget_counts").GetProperty("budget").GetInt32());
        Assert.True(counts.RootElement.GetProperty("savedate_counts").EnumerateObject().Any());

        using var panels = JsonDocument.Parse(page.Panels);
        Assert.Equal(["piechart", "mana_curve", "rank_over_time", "combocounts"],
            panels.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(18, panels.RootElement.GetProperty("mana_curve").GetProperty("2").GetInt32());
    }

    [Fact]
    public void A_theme_page_reads_the_same_way()
    {
        using var document = EdhrecFixtures.Json(EdhrecFixtures.CommanderTheme);
        var page = EdhrecCommanderPageParser.Parse(document, "atraxa-praetors-voice");

        Assert.NotEmpty(page.Lists);
        Assert.Contains(page.Lists, list => list.Entries.Any(entry => entry.S is not null));
    }

    [Fact]
    public void A_payload_without_cardlists_is_loud_and_stores_nothing()
    {
        using var empty = JsonDocument.Parse("""{"container":{"json_dict":{}}}""");
        Assert.Throws<CommunityDataException>(() => EdhrecCommanderPageParser.Parse(empty, "x"));

        using var wrong = JsonDocument.Parse("""{"container":{"json_dict":{"cardlists":{}}}}""");
        Assert.Throws<CommunityDataException>(() => EdhrecCommanderPageParser.Parse(wrong, "x"));
    }

    [Fact]
    public void Every_stored_list_is_capped_and_so_is_the_page()
    {
        using var huge = JsonDocument.Parse(Huge(sections: 12, perSection: 400));
        var page = EdhrecCommanderPageParser.Parse(huge, "x");

        Assert.All(page.Lists, list => Assert.True(list.Entries.Length <= EdhrecPages.MaxEntriesPerList));
        Assert.Equal(EdhrecPages.MaxEntriesPerPage, page.Lists.Sum(list => list.Entries.Length));
    }

    /// <summary>
    /// The golden. It hashes what the store would write for a page we recorded, so a change to the
    /// stored shape fails here rather than silently serving two shapes out of one table. When it
    /// fails on purpose: bump <see cref="EdhrecCommanderPageParser.Version"/>, then paste the hash
    /// the failure prints.
    /// </summary>
    [Fact]
    public void The_stored_shape_cannot_change_without_a_version_bump()
    {
        using var document = EdhrecFixtures.Json(EdhrecFixtures.Commander);
        var page = EdhrecCommanderPageParser.Parse(document, "atraxa-praetors-voice");
        var stored = string.Join('\n',
            page.Slug, page.DeckCount, page.Rank,
            EdhrecPages.Write(page.Lists), EdhrecPages.Write(page.Similar), EdhrecPages.Write(page.Tags),
            page.Counts, page.Panels);
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(stored)));

        Assert.Equal("edhrec-commander-v2", EdhrecCommanderPageParser.Version);
        Assert.True(digest == Golden,
            $"The stored commander shape changed. Bump the parser version, then paste this digest: {digest}");
    }

    private static string Huge(int sections, int perSection)
    {
        var builder = new StringBuilder("""{"container":{"json_dict":{"cardlists":[""");
        for (int section = 0; section < sections; section++)
        {
            if (section > 0)
                builder.Append(',');
            builder.Append("{\"tag\":\"t").Append(section).Append("\",\"header\":\"H\",\"cardviews\":[");
            for (int card = 0; card < perSection; card++)
            {
                if (card > 0)
                    builder.Append(',');
                builder.Append("{\"name\":\"C").Append(section).Append('-').Append(card).Append("\"}");
            }
            builder.Append("]}");
        }
        return builder.Append("]}}}").ToString();
    }
}
