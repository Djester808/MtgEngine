using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using MtgEngine.Api.Data;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;
using MtgEngine.Domain.Models;
using Xunit;

namespace MtgEngine.Rules.Tests;

public sealed class CollectionServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MtgEngineDbContext> _dbOptions;
    private readonly Mock<IScryfallService> _scryfallServiceMock;
    private MtgEngineDbContext _context = null!;
    private CollectionService _service = null!;

    private const string TestUserId = "test-user-123";
    private const string OtherUserId = "other-user-456";

    private const string LightningBoltOracleId = "e95a0f49-39fa-4bef-b83d-3f08fe85f4d0";
    private const string CounterspellOracleId = "a45b465e-4ff7-437b-a7b2-c38d1596491d";

    public CollectionServiceTests()
    {
        // Keep the connection open so the in-memory database survives across context instances
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<MtgEngineDbContext>()
            .UseSqlite(_connection)
            .Options;

        _scryfallServiceMock = new Mock<IScryfallService>();
        SetupDefaultMockResponses();
    }

    public async Task InitializeAsync()
    {
        _context = new MtgEngineDbContext(_dbOptions);
        await _context.Database.EnsureCreatedAsync();
        _service = new CollectionService(_context, _scryfallServiceMock.Object);
    }

    public async Task DisposeAsync()
    {
        await _context.Database.EnsureDeletedAsync();
        _context.Dispose();
        _connection.Dispose();
    }

    private void SetupDefaultMockResponses()
    {
        _scryfallServiceMock
            .Setup(s => s.GetByOracleIdAsync(LightningBoltOracleId))
            .ReturnsAsync(CreateTestCardDefinition(LightningBoltOracleId, "Lightning Bolt", "R"));

        _scryfallServiceMock
            .Setup(s => s.GetByOracleIdAsync(CounterspellOracleId))
            .ReturnsAsync(CreateTestCardDefinition(CounterspellOracleId, "Counterspell", "UU"));

        _scryfallServiceMock
            .Setup(s => s.GetByOracleIdAsync(It.IsNotIn(LightningBoltOracleId, CounterspellOracleId)))
            .ReturnsAsync((CardDefinition?)null);
    }

    // ---- Collection CRUD Tests ----

    [Fact]
    public async Task CreateCollectionAsync_CreatesNewCollection()
    {
        // Arrange
        var request = new CreateCollectionRequest("My Collection", "Test description");

        // Act
        var result = await _service.CreateCollectionAsync(TestUserId, request);

        // Assert
        result.Should().NotBeNull();
        result.Name.Should().Be("My Collection");
        result.Description.Should().Be("Test description");
        result.Id.Should().NotBeEmpty();
        result.Cards.Should().BeEmpty();

        // Verify in database
        var dbCollection = await _context.Collections
            .FirstOrDefaultAsync(c => c.Id == result.Id);
        dbCollection.Should().NotBeNull();
        dbCollection!.UserId.Should().Be(TestUserId);
    }

    [Fact]
    public async Task GetUserCollectionsAsync_ReturnsOnlyUserCollections()
    {
        // Arrange
        await _context.Collections.AddAsync(new Collection(TestUserId, "Collection 1"));
        await _context.Collections.AddAsync(new Collection(TestUserId, "Collection 2"));
        await _context.Collections.AddAsync(new Collection(OtherUserId, "Other Collection"));
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetUserCollectionsAsync(TestUserId);

        // Assert
        result.Should().HaveCount(2);
        result.Should().AllSatisfy(c => c.Name.Should().StartWith("Collection"));
    }

    [Fact]
    public async Task GetCollectionAsync_ReturnsCollectionWithCards()
    {
        // Arrange
        var collection = new Collection(TestUserId, "Test Collection");
        await _context.Collections.AddAsync(collection);

        var card = new CollectionCard(
            collection.Id,
            LightningBoltOracleId,
            quantity: 2);
        await _context.CollectionCards.AddAsync(card);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetCollectionAsync(collection.Id, TestUserId);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(collection.Id);
        result.Cards.Should().HaveCount(1);
        result.Cards[0].OracleId.Should().Be(LightningBoltOracleId);
        result.Cards[0].Quantity.Should().Be(2);
    }

    [Fact]
    public async Task GetCollectionAsync_ReturnsNullForOtherUserCollection()
    {
        // Arrange
        var collection = new Collection(OtherUserId, "Other Collection");
        await _context.Collections.AddAsync(collection);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetCollectionAsync(collection.Id, TestUserId);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetCollectionAsync_ReturnsNullForNonexistentCollection()
    {
        // Act
        var result = await _service.GetCollectionAsync(Guid.NewGuid(), TestUserId);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task UpdateCollectionAsync_UpdatesCollectionMetadata()
    {
        // Arrange
        var collection = new Collection(TestUserId, "Original Name", "Original description");
        await _context.Collections.AddAsync(collection);
        await _context.SaveChangesAsync();

        var updateRequest = new UpdateCollectionRequest("Updated Name", "Updated description");

        // Act
        var result = await _service.UpdateCollectionAsync(collection.Id, TestUserId, updateRequest);

        // Assert
        result.Name.Should().Be("Updated Name");
        result.Description.Should().Be("Updated description");

        // Verify in database
        var dbCollection = await _context.Collections.FirstAsync(c => c.Id == collection.Id);
        dbCollection.Name.Should().Be("Updated Name");
        dbCollection.UpdatedAt.Should().BeAfter(collection.CreatedAt);
    }

    [Fact]
    public async Task UpdateCollectionAsync_ThrowsKeyNotFoundForNonexistentCollection()
    {
        // Arrange
        var updateRequest = new UpdateCollectionRequest("Name", "Description");

        // Act & Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _service.UpdateCollectionAsync(Guid.NewGuid(), TestUserId, updateRequest));
    }

    [Fact]
    public async Task UpdateCollectionAsync_ThrowsKeyNotFoundForOtherUserCollection()
    {
        // Arrange
        var collection = new Collection(OtherUserId, "Other Collection");
        await _context.Collections.AddAsync(collection);
        await _context.SaveChangesAsync();

        var updateRequest = new UpdateCollectionRequest("Name", "Description");

        // Act & Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _service.UpdateCollectionAsync(collection.Id, TestUserId, updateRequest));
    }

    [Fact]
    public async Task DeleteCollectionAsync_DeletesCollection()
    {
        // Arrange
        var collection = new Collection(TestUserId, "To Delete");
        await _context.Collections.AddAsync(collection);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.DeleteCollectionAsync(collection.Id, TestUserId);

        // Assert
        result.Should().BeTrue();

        var dbCollection = await _context.Collections
            .FirstOrDefaultAsync(c => c.Id == collection.Id);
        dbCollection.Should().BeNull();
    }

    [Fact]
    public async Task DeleteCollectionAsync_ReturnsFalseForNonexistentCollection()
    {
        // Act
        var result = await _service.DeleteCollectionAsync(Guid.NewGuid(), TestUserId);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteCollectionAsync_DeletesCollectionAndAllCards()
    {
        // Arrange
        var collection = new Collection(TestUserId, "To Delete");
        await _context.Collections.AddAsync(collection);
        await _context.SaveChangesAsync();

        var card = new CollectionCard(collection.Id, LightningBoltOracleId, quantity: 2);
        await _context.CollectionCards.AddAsync(card);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.DeleteCollectionAsync(collection.Id, TestUserId);

        // Assert
        result.Should().BeTrue();

        var dbCards = await _context.CollectionCards
            .Where(cc => cc.CollectionId == collection.Id)
            .ToListAsync();
        dbCards.Should().BeEmpty();
    }

    // ---- Card Management Tests ----

    [Fact]
    public async Task AddCardToCollectionAsync_AddsNewCard()
    {
        // Arrange
        var collection = new Collection(TestUserId, "Test Collection");
        await _context.Collections.AddAsync(collection);
        await _context.SaveChangesAsync();

        var request = new AddCardToCollectionRequest(LightningBoltOracleId, Quantity: 2);

        // Act
        var result = await _service.AddCardToCollectionAsync(collection.Id, TestUserId, request);

        // Assert
        result.OracleId.Should().Be(LightningBoltOracleId);
        result.Quantity.Should().Be(2);
        result.QuantityFoil.Should().Be(0);
        result.CardDetails.Should().NotBeNull();
    }

    [Fact]
    public async Task AddCardToCollectionAsync_IncrementsQuantityForDuplicateCard()
    {
        // Arrange
        var collection = new Collection(TestUserId, "Test Collection");
        await _context.Collections.AddAsync(collection);
        await _context.SaveChangesAsync();

        var card = new CollectionCard(collection.Id, LightningBoltOracleId, quantity: 2);
        await _context.CollectionCards.AddAsync(card);
        await _context.SaveChangesAsync();

        var request = new AddCardToCollectionRequest(LightningBoltOracleId, null, 1);

        // Act
        var result = await _service.AddCardToCollectionAsync(collection.Id, TestUserId, request);

        // Assert
        result.Quantity.Should().Be(3);
    }

    [Fact]
    public async Task AddCardToCollectionAsync_AccumulatesQuantityFoilSeparately()
    {
        // Arrange
        var collection = new Collection(TestUserId, "Test Collection");
        await _context.Collections.AddAsync(collection);
        await _context.SaveChangesAsync();

        var card = new CollectionCard(collection.Id, LightningBoltOracleId, quantity: 2);
        await _context.CollectionCards.AddAsync(card);
        await _context.SaveChangesAsync();

        // Add 0 normal + 3 foil copies to the existing entry
        var request = new AddCardToCollectionRequest(LightningBoltOracleId, Quantity: 0, QuantityFoil: 3);

        // Act
        var result = await _service.AddCardToCollectionAsync(collection.Id, TestUserId, request);

        // Assert
        result.Quantity.Should().Be(2);
        result.QuantityFoil.Should().Be(3);
    }

    [Fact]
    public async Task AddCardToCollectionAsync_FoilOnlyAddIsAccepted()
    {
        // Arrange
        var collection = new Collection(TestUserId, "Test Collection");
        await _context.Collections.AddAsync(collection);
        await _context.SaveChangesAsync();

        var request = new AddCardToCollectionRequest(LightningBoltOracleId, Quantity: 0, QuantityFoil: 2);

        // Act
        var result = await _service.AddCardToCollectionAsync(collection.Id, TestUserId, request);

        // Assert
        result.Quantity.Should().Be(0);
        result.QuantityFoil.Should().Be(2);
    }

    [Fact]
    public async Task AddCardToCollectionAsync_ThrowsForNonexistentCollection()
    {
        // Arrange
        var request = new AddCardToCollectionRequest(LightningBoltOracleId);

        // Act & Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _service.AddCardToCollectionAsync(Guid.NewGuid(), TestUserId, request));
    }

    [Fact]
    public async Task RemoveCardFromCollectionAsync_RemovesCard()
    {
        // Arrange
        var collection = new Collection(TestUserId, "Test Collection");
        await _context.Collections.AddAsync(collection);
        await _context.SaveChangesAsync();

        var card = new CollectionCard(collection.Id, LightningBoltOracleId, quantity: 2);
        await _context.CollectionCards.AddAsync(card);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.RemoveCardFromCollectionAsync(collection.Id, card.Id, TestUserId);

        // Assert
        result.Should().BeTrue();

        var dbCard = await _context.CollectionCards
            .FirstOrDefaultAsync(cc => cc.Id == card.Id);
        dbCard.Should().BeNull();
    }

    [Fact]
    public async Task RemoveCardFromCollectionAsync_ReturnsFalseForNonexistentCard()
    {
        // Arrange
        var collection = new Collection(TestUserId, "Test Collection");
        await _context.Collections.AddAsync(collection);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.RemoveCardFromCollectionAsync(collection.Id, Guid.NewGuid(), TestUserId);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task RemoveCardByOracleAsync_RemovesAllCopies()
    {
        // Arrange
        var collection = new Collection(TestUserId, "Test Collection");
        await _context.Collections.AddAsync(collection);
        await _context.SaveChangesAsync();

        var card = new CollectionCard(collection.Id, LightningBoltOracleId, quantity: 5);
        await _context.CollectionCards.AddAsync(card);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.RemoveCardByOracleAsync(collection.Id, LightningBoltOracleId, TestUserId);

        // Assert
        result.Should().BeTrue();

        var dbCard = await _context.CollectionCards
            .FirstOrDefaultAsync(cc => cc.OracleId == LightningBoltOracleId && cc.CollectionId == collection.Id);
        dbCard.Should().BeNull();
    }

    [Fact]
    public async Task UpdateCollectionCardAsync_UpdatesCardDetails()
    {
        // Arrange
        var collection = new Collection(TestUserId, "Test Collection");
        await _context.Collections.AddAsync(collection);
        await _context.SaveChangesAsync();

        var card = new CollectionCard(collection.Id, LightningBoltOracleId, quantity: 2);
        await _context.CollectionCards.AddAsync(card);
        await _context.SaveChangesAsync();

        var updateRequest = new UpdateCollectionCardRequest(4, 2, Notes: "Updated notes");

        // Act
        var result = await _service.UpdateCollectionCardAsync(collection.Id, card.Id, TestUserId, updateRequest);

        // Assert
        result.Quantity.Should().Be(4);
        result.QuantityFoil.Should().Be(2);
        result.Notes.Should().Be("Updated notes");
    }

    [Fact]
    public async Task UpdateCollectionCardAsync_ThrowsForNonexistentCard()
    {
        // Arrange
        var collection = new Collection(TestUserId, "Test Collection");
        await _context.Collections.AddAsync(collection);
        await _context.SaveChangesAsync();

        var updateRequest = new UpdateCollectionCardRequest(1, 0);

        // Act & Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _service.UpdateCollectionCardAsync(collection.Id, Guid.NewGuid(), TestUserId, updateRequest));
    }

    // ---- Deck Building Tests ----

    [Fact]
    public async Task GetAvailableCardsForDeckAsync_ReturnAllCardsFromCollection()
    {
        // Arrange
        var collection = new Collection(TestUserId, "Test Collection");
        await _context.Collections.AddAsync(collection);
        await _context.SaveChangesAsync();

        var card1 = new CollectionCard(collection.Id, LightningBoltOracleId, quantity: 2);
        var card2 = new CollectionCard(collection.Id, CounterspellOracleId, quantity: 1);
        await _context.CollectionCards.AddRangeAsync(card1, card2);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetAvailableCardsForDeckAsync(collection.Id, TestUserId);

        // Assert
        result.Should().HaveCount(2);
        result.Should().ContainSingle(c => c.OracleId == LightningBoltOracleId);
        result.Should().ContainSingle(c => c.OracleId == CounterspellOracleId);
    }

    [Fact]
    public async Task GetAvailableCardsForDeckAsync_ReturnsEmptyForNonexistentCollection()
    {
        // Act
        var result = await _service.GetAvailableCardsForDeckAsync(Guid.NewGuid(), TestUserId);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAvailableCardsForDeckAsync_SkipsCardsWithoutScryfallData()
    {
        // Arrange
        var collection = new Collection(TestUserId, "Test Collection");
        await _context.Collections.AddAsync(collection);
        await _context.SaveChangesAsync();

        var unknownOracleId = "unknown-oracle-id";
        var card1 = new CollectionCard(collection.Id, LightningBoltOracleId, quantity: 2);
        var card2 = new CollectionCard(collection.Id, unknownOracleId, quantity: 1);
        await _context.CollectionCards.AddRangeAsync(card1, card2);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetAvailableCardsForDeckAsync(collection.Id, TestUserId);

        // Assert
        result.Should().HaveCount(1);
        result.Should().ContainSingle(c => c.OracleId == LightningBoltOracleId);
    }

    // ---- User Isolation Tests ----

    [Fact]
    public async Task RemoveCardFromCollectionAsync_EnforcesUserIsolation()
    {
        // Arrange
        var collection = new Collection(OtherUserId, "Other Collection");
        await _context.Collections.AddAsync(collection);
        await _context.SaveChangesAsync();

        var card = new CollectionCard(collection.Id, LightningBoltOracleId, quantity: 2);
        await _context.CollectionCards.AddAsync(card);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.RemoveCardFromCollectionAsync(collection.Id, card.Id, TestUserId);

        // Assert
        result.Should().BeFalse();

        // Card should still exist
        var dbCard = await _context.CollectionCards.FirstOrDefaultAsync(cc => cc.Id == card.Id);
        dbCard.Should().NotBeNull();
    }

    [Fact]
    public async Task RemoveCardByOracleAsync_EnforcesUserIsolation()
    {
        // Arrange
        var collection = new Collection(OtherUserId, "Other Collection");
        await _context.Collections.AddAsync(collection);
        await _context.SaveChangesAsync();

        var card = new CollectionCard(collection.Id, LightningBoltOracleId, quantity: 5);
        await _context.CollectionCards.AddAsync(card);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.RemoveCardByOracleAsync(collection.Id, LightningBoltOracleId, TestUserId);

        // Assert
        result.Should().BeFalse();

        // Card should still exist
        var dbCard = await _context.CollectionCards
            .FirstOrDefaultAsync(cc => cc.OracleId == LightningBoltOracleId);
        dbCard.Should().NotBeNull();
    }

    // ---- Deck Tags Tests ----

    [Fact]
    public async Task GetDeckAsync_IncludesTagsInDto()
    {
        // Arrange
        var deck = new Collection(TestUserId, "Tagged Deck", isDeck: true)
        {
            Tags = ["combo", "aggro"]
        };
        await _context.Collections.AddAsync(deck);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetDeckAsync(deck.Id, TestUserId);

        // Assert
        result.Should().NotBeNull();
        result!.Tags.Should().BeEquivalentTo(new[] { "combo", "aggro" });
    }

    [Fact]
    public async Task GetDeckAsync_ReturnsEmptyTagsWhenNoneSet()
    {
        // Arrange
        var deck = new Collection(TestUserId, "Untagged Deck", isDeck: true);
        await _context.Collections.AddAsync(deck);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetDeckAsync(deck.Id, TestUserId);

        // Assert
        result.Should().NotBeNull();
        result!.Tags.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateDeckAsync_UpdatesTags_WhenTagsProvided()
    {
        // Arrange
        var deck = new Collection(TestUserId, "Deck", isDeck: true)
        {
            Tags = ["old-tag"]
        };
        await _context.Collections.AddAsync(deck);
        await _context.SaveChangesAsync();

        var request = new UpdateDeckRequest("Deck", Tags: ["new-tag1", "new-tag2"]);

        // Act
        var result = await _service.UpdateDeckAsync(deck.Id, TestUserId, request);

        // Assert
        result.Tags.Should().BeEquivalentTo(new[] { "new-tag1", "new-tag2" });

        var dbDeck = await _context.Collections.FirstAsync(c => c.Id == deck.Id);
        dbDeck.Tags.Should().BeEquivalentTo(new[] { "new-tag1", "new-tag2" });
    }

    [Fact]
    public async Task UpdateDeckAsync_PreservesExistingTags_WhenTagsIsNull()
    {
        // Arrange
        var deck = new Collection(TestUserId, "Deck", isDeck: true)
        {
            Tags = ["preserved-tag"]
        };
        await _context.Collections.AddAsync(deck);
        await _context.SaveChangesAsync();

        var request = new UpdateDeckRequest("Updated Name", Tags: null);

        // Act
        var result = await _service.UpdateDeckAsync(deck.Id, TestUserId, request);

        // Assert
        result.Tags.Should().BeEquivalentTo(new[] { "preserved-tag" });
    }

    [Fact]
    public async Task UpdateDeckAsync_ClearsTags_WhenTagsIsEmptyArray()
    {
        // Arrange
        var deck = new Collection(TestUserId, "Deck", isDeck: true)
        {
            Tags = ["to-remove"]
        };
        await _context.Collections.AddAsync(deck);
        await _context.SaveChangesAsync();

        var request = new UpdateDeckRequest("Deck", Tags: []);

        // Act
        var result = await _service.UpdateDeckAsync(deck.Id, TestUserId, request);

        // Assert
        result.Tags.Should().BeEmpty();

        var dbDeck = await _context.Collections.FirstAsync(c => c.Id == deck.Id);
        dbDeck.Tags.Should().BeEmpty();
    }

    // ---- Helper Methods ----

    private static CardDefinition CreateTestCardDefinition(string oracleId, string name, string manaCost)
    {
        return new CardDefinition
        {
            OracleId = oracleId,
            Name = name,
            ManaCost = MtgEngine.Domain.ValueObjects.ManaCost.Parse(manaCost),
            CardTypes = MtgEngine.Domain.Enums.CardType.Instant,
            OracleText = $"Test card: {name}",
            ImageUriNormal = "https://example.com/card.png",
            ColorIdentity = []
        };
    }
}
