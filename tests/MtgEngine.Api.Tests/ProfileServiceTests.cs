using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Domain.ValueObjects;

namespace MtgEngine.Api.Tests;

/// <summary>
/// User profiles: the self-authored fields, the derived stats, and the line between what
/// a visitor sees and what only the owner does.
/// </summary>
public sealed class ProfileServiceTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly MtgEngineDbContext _db;
    private readonly ProfileService _sut;
    private readonly Lookup _cards = new();

    private readonly User _user;
    private string UserId => _user.Id.ToString();

    public ProfileServiceTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        var options = new DbContextOptionsBuilder<MtgEngineDbContext>().UseSqlite(_conn).Options;
        _db = new MtgEngineDbContext(options);
        _db.Database.EnsureCreated();

        _user = new User
        {
            Username = "Nissa",
            Email = "nissa@example.com",
            PasswordHash = "x",
            CreatedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        };
        _db.Users.Add(_user);
        _db.SaveChanges();

        var collections = new CollectionService(_db, _cards, new CardHistoryService(_db));
        var forum = new ForumService(_db, _cards, collections);
        _sut = new ProfileService(_db, _cards, forum);
    }

    /// <summary>Cards the tests register by oracle id; anything else resolves to a plain spell.</summary>
    private sealed class Lookup : StubScryfallService
    {
        public Dictionary<string, CardDefinition> Cards { get; } = [];

        public override Task<CardDefinition?> GetByOracleIdAsync(string oracleId) =>
            Task.FromResult<CardDefinition?>(
                Cards.TryGetValue(oracleId, out var card)
                    ? card
                    : new CardDefinition { OracleId = oracleId, Name = oracleId });

        public override Task<CardDefinition?> GetByScryfallIdAsync(string scryfallId) =>
            Task.FromResult<CardDefinition?>(
                Cards.TryGetValue(scryfallId, out var card)
                    ? card
                    : new CardDefinition { OracleId = scryfallId, Name = scryfallId });
    }

    // ---- Fixtures ---------------------------------------------------------

    private Collection AddDeck(
        string name, string? commanderOracleId = null, string? format = "Commander",
        DateTime? updatedAt = null, string? userId = null)
    {
        var deck = new Collection(userId ?? UserId, name, isDeck: true)
        {
            CommanderOracleId = commanderOracleId,
            Format = format,
            UpdatedAt = updatedAt ?? DateTime.UtcNow,
        };
        _db.Collections.Add(deck);
        _db.SaveChanges();
        return deck;
    }

    private Collection AddCollection(string name, string? userId = null)
    {
        var collection = new Collection(userId ?? UserId, name);
        _db.Collections.Add(collection);
        _db.SaveChanges();
        return collection;
    }

    private void AddCard(Collection collection, string oracleId, int qty = 1, int foil = 0)
    {
        _db.CollectionCards.Add(
            new CollectionCard(collection.Id, oracleId, null, qty, foil, null, "main"));
        _db.SaveChanges();
    }

    private ForumPost Publish(Collection deck, string? authorId = null)
    {
        var post = new ForumPost
        {
            DeckId = deck.Id,
            AuthorId = authorId ?? UserId,
            AuthorUsername = _user.Username,
            PublishedAt = DateTime.UtcNow,
        };
        _db.ForumPosts.Add(post);
        _db.SaveChanges();
        return post;
    }

    private void Comment(ForumPost post, string authorId, string content = "nice deck")
    {
        _db.ForumComments.Add(new ForumComment
        {
            ForumPostId = post.Id,
            AuthorId = authorId,
            AuthorUsername = authorId == UserId ? _user.Username : "someone",
            Content = content,
        });
        _db.SaveChanges();
    }

    private static CardDefinition BasicLand(string name) => new()
    {
        OracleId = name,
        Name = name,
        CardTypes = CardType.Land,
        Supertypes = ["Basic"],
    };

    // ---- Identity ---------------------------------------------------------

    [Fact]
    public async Task A_member_who_has_never_posted_still_has_a_profile()
    {
        // The previous implementation derived profiles from ForumPosts, so a user who had
        // not published 404'd on their own page.
        var profile = await _sut.GetPublicProfileAsync("Nissa");

        Assert.Equal("Nissa", profile.Username);
        Assert.Equal(0, profile.DeckCount);
        Assert.Empty(profile.PublishedDecks);
    }

    [Fact]
    public async Task Joined_date_is_the_account_creation_date()
    {
        // Not the first post's date, which is what the old projection reported.
        var profile = await _sut.GetPublicProfileAsync("Nissa");

        Assert.Equal(_user.CreatedAt, profile.JoinedAt);
    }

    [Fact]
    public async Task Username_lookup_ignores_case()
    {
        var profile = await _sut.GetPublicProfileAsync("nISSa");

        Assert.Equal("Nissa", profile.Username);
    }

    [Fact]
    public async Task An_unknown_user_is_a_not_found_not_a_wildcard_match()
    {
        // Under LIKE, "%" would match the first row in the table and hand out a stranger's
        // profile. Equality means it is simply absent.
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => _sut.GetPublicProfileAsync("%"));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => _sut.GetPublicProfileAsync("nobody"));
    }

    // ---- Stats ------------------------------------------------------------

    [Fact]
    public async Task Cards_owned_counts_foils_and_ignores_decks()
    {
        var collection = AddCollection("Binder");
        AddCard(collection, "sol-ring", qty: 2, foil: 3);

        var deck = AddDeck("Deck");
        AddCard(deck, "sol-ring", qty: 1);

        var stats = (await _sut.GetPublicProfileAsync("Nissa")).Stats;

        Assert.Equal(5, stats.CardsOwned);
        Assert.Equal(1, stats.DistinctCards);
        Assert.Equal(1, stats.Collections);
        Assert.Equal(1, stats.DecksBuilt);
    }

    [Fact]
    public async Task Distinct_cards_ignores_rows_whose_copies_are_all_gone()
    {
        var collection = AddCollection("Binder");
        AddCard(collection, "kept", qty: 1);
        AddCard(collection, "given-away", qty: 0, foil: 0);

        var stats = (await _sut.GetPublicProfileAsync("Nissa")).Stats;

        Assert.Equal(1, stats.DistinctCards);
    }

    [Fact]
    public async Task Comments_received_excludes_the_authors_own_replies()
    {
        var post = Publish(AddDeck("Deck"));
        Comment(post, authorId: "someone-else");
        Comment(post, authorId: UserId, content: "thanks!");

        var stats = (await _sut.GetPublicProfileAsync("Nissa")).Stats;

        Assert.Equal(1, stats.CommentsReceived);
        Assert.Equal(1, stats.CommentsPosted);
    }

    [Fact]
    public async Task Colour_spread_is_reported_in_wubrg_order_and_omits_unplayed_colours()
    {
        _cards.Cards["golgari"] = new CardDefinition
        {
            OracleId = "golgari",
            Name = "Golgari Commander",
            ColorIdentity = [ManaColor.Black, ManaColor.Green],
        };
        AddDeck("Rot", commanderOracleId: "golgari");
        AddDeck("More Rot", commanderOracleId: "golgari");

        var spread = (await _sut.GetPublicProfileAsync("Nissa")).Stats.ColorSpread;

        Assert.Equal(["B", "G"], spread.Select(c => c.Color));
        Assert.All(spread, c => Assert.Equal(2, c.DeckCount));
    }

    [Fact]
    public async Task Most_played_cards_counts_decks_not_copies_and_drops_basic_lands()
    {
        // Without the basic-land filter this stat reads "Forest" for every player alive.
        _cards.Cards["forest"] = BasicLand("Forest");

        var one = AddDeck("One");
        var two = AddDeck("Two");
        AddCard(one, "forest", qty: 30);
        AddCard(two, "forest", qty: 30);
        AddCard(one, "sol-ring");
        AddCard(two, "sol-ring");
        AddCard(one, "arcane-signet", qty: 4);

        var played = (await _sut.GetPublicProfileAsync("Nissa")).MostPlayedCards;

        Assert.DoesNotContain(played, c => c.OracleId == "forest");
        Assert.Equal("sol-ring", played[0].OracleId);
        Assert.Equal(2, played[0].DeckCount);
        Assert.Equal(1, played.Single(c => c.OracleId == "arcane-signet").DeckCount);
    }

    [Fact]
    public async Task Top_decks_are_ordered_by_conversation()
    {
        var quiet = Publish(AddDeck("Quiet"));
        var busy = Publish(AddDeck("Busy"));
        Comment(busy, "a");
        Comment(busy, "b");
        Comment(quiet, "c");

        var top = (await _sut.GetPublicProfileAsync("Nissa")).TopDecks;

        Assert.Equal(busy.Id, top[0].Id);
        Assert.Equal(2, top[0].CommentCount);
    }

    // ---- Public vs private ------------------------------------------------

    [Fact]
    public async Task A_visitor_never_sees_an_unpublished_decks_name()
    {
        AddDeck("Secret Brew", updatedAt: DateTime.UtcNow.AddMinutes(5));
        Publish(AddDeck("Published Deck", updatedAt: DateTime.UtcNow));

        var visitor = await _sut.GetPublicProfileAsync("Nissa");

        Assert.DoesNotContain(visitor.RecentlyActive, d => d.Name == "Secret Brew");
        Assert.Contains(visitor.RecentlyActive, d => d.Name == "Published Deck");
    }

    [Fact]
    public async Task The_owner_sees_their_unpublished_decks()
    {
        AddDeck("Secret Brew", updatedAt: DateTime.UtcNow.AddMinutes(5));

        var mine = await _sut.GetMyProfileAsync(_user.Id);

        Assert.Contains(mine.Profile.RecentlyActive, d => d.Name == "Secret Brew");
        Assert.Equal(1, mine.PrivateStats.UnpublishedDecks);
    }

    [Fact]
    public async Task Collection_value_prices_foils_as_foils_and_is_owner_only()
    {
        _cards.Cards["dual"] = new CardDefinition
        {
            OracleId = "dual",
            Name = "Dual Land",
            Prices = new CardPrices { Usd = 10m, UsdFoil = 100m },
        };
        var binder = AddCollection("Binder");
        AddCard(binder, "dual", qty: 2, foil: 1);

        var mine = await _sut.GetMyProfileAsync(_user.Id);

        Assert.Equal(120m, mine.PrivateStats.CollectionValueUsd);
        Assert.Equal(3, mine.PrivateStats.CopiesValued);

        // The public shape has nowhere to carry it: the value lives on MyProfileDto only.
        var visitor = await _sut.GetPublicProfileAsync("Nissa");
        Assert.Equal(3, visitor.Stats.CardsOwned);
    }

    [Fact]
    public async Task Copies_with_no_market_price_are_left_out_of_the_total()
    {
        _cards.Cards["unpriced"] = new CardDefinition { OracleId = "unpriced", Name = "Unpriced" };
        var binder = AddCollection("Binder");
        AddCard(binder, "unpriced", qty: 4);

        var mine = await _sut.GetMyProfileAsync(_user.Id);

        // Reporting 4 copies valued at $0 would read as "worthless" rather than "unknown".
        Assert.Equal(0m, mine.PrivateStats.CollectionValueUsd);
        Assert.Equal(0, mine.PrivateStats.CopiesValued);
        Assert.Equal(4, mine.Profile.Stats.CardsOwned);
    }

    [Fact]
    public async Task One_users_decks_never_count_toward_anothers_profile()
    {
        AddDeck("Someone Elses", userId: "another-user");

        var stats = (await _sut.GetPublicProfileAsync("Nissa")).Stats;

        Assert.Equal(0, stats.DecksBuilt);
    }

    // ---- Editing ----------------------------------------------------------

    [Fact]
    public async Task Editing_trims_text_and_clears_blank_fields()
    {
        await _sut.UpdateMyProfileAsync(_user.Id, new UpdateProfileRequest
        {
            DisplayName = "  Nissa Revane  ",
            Tagline = "   ",
            Bio = "I play green.",
        });

        var profile = await _sut.GetPublicProfileAsync("Nissa");

        Assert.Equal("Nissa Revane", profile.DisplayName);
        Assert.Null(profile.Tagline);
        Assert.Equal("I play green.", profile.Bio);
    }

    [Fact]
    public async Task A_pinned_commander_the_card_data_does_not_know_is_refused()
    {
        _cards.Cards.Clear();
        var unknown = new Lookup();
        var sut = new ProfileService(
            _db, new MissingCardLookup(), new ForumService(_db, unknown, new CollectionService(_db, unknown, new CardHistoryService(_db))));

        await Assert.ThrowsAsync<InvalidRequestException>(() =>
            sut.UpdateMyProfileAsync(_user.Id, new UpdateProfileRequest
            {
                FavoriteCommanderOracleId = "not-a-card",
            }));
    }

    private sealed class MissingCardLookup : StubScryfallService
    {
        public override Task<CardDefinition?> GetByOracleIdAsync(string oracleId) =>
            Task.FromResult<CardDefinition?>(null);
    }

    [Fact]
    public async Task A_pinned_commander_is_returned_with_the_decks_it_leads()
    {
        _cards.Cards["atraxa"] = new CardDefinition
        {
            OracleId = "atraxa",
            Name = "Atraxa",
            ColorIdentity = [ManaColor.White, ManaColor.Blue, ManaColor.Black, ManaColor.Green],
        };
        AddDeck("Superfriends", commanderOracleId: "atraxa");

        await _sut.UpdateMyProfileAsync(_user.Id, new UpdateProfileRequest
        {
            FavoriteCommanderOracleId = "atraxa",
        });

        var profile = await _sut.GetPublicProfileAsync("Nissa");

        var favorite = Assert.IsType<CommanderBriefDto>(profile.FavoriteCommander);
        Assert.Equal("Atraxa", favorite.Name);
        Assert.Equal(1, favorite.DeckCount);
        Assert.Equal(["W", "U", "B", "G"], favorite.ColorIdentity);
    }

    // ---- Avatar -----------------------------------------------------------

    private static byte[] SmallPng(int size = 64)
    {
        var bytes = new byte[24];
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        bytes[19] = (byte)size;
        bytes[23] = (byte)size;
        return bytes;
    }

    [Fact]
    public async Task Uploading_an_avatar_publishes_a_stamped_url()
    {
        var before = await _sut.GetPublicProfileAsync("Nissa");
        Assert.Null(before.AvatarUrl);

        var mine = await _sut.SetAvatarAsync(_user.Id, SmallPng());

        Assert.NotNull(mine.Profile.AvatarUrl);
        Assert.Contains("/api/users/Nissa/avatar?v=", mine.Profile.AvatarUrl, StringComparison.Ordinal);

        var stored = await _sut.GetAvatarAsync("Nissa");
        Assert.Equal("image/png", stored!.ContentType);
    }

    [Fact]
    public async Task Replacing_an_avatar_changes_the_url_so_caches_cannot_serve_the_old_one()
    {
        var first = (await _sut.SetAvatarAsync(_user.Id, SmallPng(64))).Profile.AvatarUrl;
        var second = (await _sut.SetAvatarAsync(_user.Id, SmallPng(128))).Profile.AvatarUrl;

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task Deleting_an_avatar_removes_both_the_bytes_and_the_url()
    {
        await _sut.SetAvatarAsync(_user.Id, SmallPng());

        var mine = await _sut.DeleteAvatarAsync(_user.Id);

        Assert.Null(mine.Profile.AvatarUrl);
        Assert.Null(await _sut.GetAvatarAsync("Nissa"));
    }

    [Fact]
    public async Task An_upload_that_is_not_an_image_is_refused()
    {
        await Assert.ThrowsAsync<InvalidRequestException>(() =>
            _sut.SetAvatarAsync(_user.Id, "<html>hi</html>"u8.ToArray()));

        Assert.Null((await _sut.GetPublicProfileAsync("Nissa")).AvatarUrl);
    }

    // ---- Comment history --------------------------------------------------

    [Fact]
    public async Task Comment_history_pages_newest_first_and_names_the_deck()
    {
        var deck = AddDeck("Talked About");
        var post = Publish(deck);
        for (var i = 0; i < 5; i++)
            Comment(post, UserId, $"comment {i}");

        var page = await _sut.GetCommentHistoryAsync("Nissa", page: 1, pageSize: 2);

        Assert.Equal(5, page.Total);
        Assert.Equal(2, page.Items.Length);
        Assert.Equal("Talked About", page.Items[0].DeckName);
        Assert.Equal(deck.Id, page.Items[0].DeckId);
    }

    [Fact]
    public async Task A_caller_cannot_ask_for_an_unbounded_page()
    {
        var post = Publish(AddDeck("Deck"));
        Comment(post, UserId);

        var page = await _sut.GetCommentHistoryAsync("Nissa", page: 1, pageSize: int.MaxValue);

        Assert.Equal(ProfileService.MaxCommentPageSize, page.PageSize);
    }

    [Fact]
    public async Task A_freshly_posted_comment_is_not_reported_as_edited()
    {
        // ForumComment initialises CreatedAt and UpdatedAt from two separate UtcNow reads,
        // so a new comment's timestamps differ by a few ticks. Comparing them directly
        // libelled brand-new comments as edited — and only sometimes, depending on where
        // the clock ticked between the two initialisers.
        var post = Publish(AddDeck("Deck"));
        Comment(post, UserId);

        var comment = _db.ForumComments.Single();
        comment.UpdatedAt = comment.CreatedAt.AddTicks(7431);
        await _db.SaveChangesAsync();

        var page = await _sut.GetCommentHistoryAsync("Nissa", 1, 10);

        Assert.False(page.Items[0].Edited);
    }

    [Fact]
    public async Task An_edited_comment_says_so()
    {
        var post = Publish(AddDeck("Deck"));
        Comment(post, UserId);

        var comment = _db.ForumComments.Single();
        comment.UpdatedAt = comment.CreatedAt.AddMinutes(1);
        await _db.SaveChangesAsync();

        var page = await _sut.GetCommentHistoryAsync("Nissa", 1, 10);

        Assert.True(page.Items[0].Edited);
    }

    // ---- Player list ------------------------------------------------------

    [Fact]
    public async Task The_player_list_ranks_by_contribution_and_includes_quiet_members()
    {
        _db.Users.Add(new User { Username = "Lurker", Email = "l@example.com", PasswordHash = "x" });
        _db.SaveChanges();

        Publish(AddDeck("Deck"));

        var players = await _sut.GetPlayersAsync(50);

        Assert.Equal(["Nissa", "Lurker"], players.Select(p => p.Username));
        Assert.Equal(1, players[0].DeckCount);
    }

    [Fact]
    public async Task The_player_list_is_capped_however_many_are_asked_for()
    {
        var players = await _sut.GetPlayersAsync(int.MaxValue);

        Assert.True(players.Length <= ProfileService.MaxPlayerLimit);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }
}
