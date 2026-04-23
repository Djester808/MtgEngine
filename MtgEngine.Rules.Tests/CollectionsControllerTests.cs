using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using MtgEngine.Api.Controllers;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;
using Xunit;

namespace MtgEngine.Rules.Tests;

public sealed class CollectionsControllerTests
{
    private readonly Mock<ICollectionService> _collectionServiceMock;
    private readonly CollectionsController _controller;

    public CollectionsControllerTests()
    {
        _collectionServiceMock = new Mock<ICollectionService>();
        _controller = new CollectionsController(_collectionServiceMock.Object);
    }

    // ---- GetCollections Tests ----

    [Fact]
    public async Task GetCollections_ReturnsOkWithCollections()
    {
        // Arrange
        var collections = new[]
        {
            new CollectionDto { Id = Guid.NewGuid(), Name = "Collection 1", CardCount = 10, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new CollectionDto { Id = Guid.NewGuid(), Name = "Collection 2", CardCount = 20, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow }
        };
        _collectionServiceMock
            .Setup(s => s.GetUserCollectionsAsync(It.IsAny<string>()))
            .ReturnsAsync(collections);

        // Act
        var result = await _controller.GetCollections();

        // Assert
        result.Result.Should().BeOfType<OkObjectResult>();
        var okResult = result.Result as OkObjectResult;
        var returnValue = okResult?.Value as CollectionDto[];
        returnValue.Should().HaveCount(2);
    }

    // ---- GetCollection Tests ----

    [Fact]
    public async Task GetCollection_WithValidId_ReturnsOkWithCollection()
    {
        // Arrange
        var collectionId = Guid.NewGuid();
        var collection = new CollectionDetailDto
        {
            Id = collectionId,
            Name = "Test Collection",
            Cards = []
        };
        _collectionServiceMock
            .Setup(s => s.GetCollectionAsync(collectionId, It.IsAny<string>()))
            .ReturnsAsync(collection);

        // Act
        var result = await _controller.GetCollection(collectionId);

        // Assert
        result.Result.Should().BeOfType<OkObjectResult>();
        var okResult = result.Result as OkObjectResult;
        var returnValue = okResult?.Value as CollectionDetailDto;
        returnValue?.Name.Should().Be("Test Collection");
    }

    [Fact]
    public async Task GetCollection_WithInvalidId_ReturnsNotFound()
    {
        // Arrange
        _collectionServiceMock
            .Setup(s => s.GetCollectionAsync(It.IsAny<Guid>(), It.IsAny<string>()))
            .ReturnsAsync((CollectionDetailDto?)null);

        // Act
        var result = await _controller.GetCollection(Guid.NewGuid());

        // Assert
        result.Result.Should().BeOfType<NotFoundResult>();
    }

    // ---- CreateCollection Tests ----

    [Fact]
    public async Task CreateCollection_WithValidRequest_ReturnsCreated()
    {
        // Arrange
        var request = new CreateCollectionRequest("New Collection", "Description");
        var createdCollection = new CollectionDetailDto
        {
            Id = Guid.NewGuid(),
            Name = "New Collection",
            Description = "Description"
        };
        _collectionServiceMock
            .Setup(s => s.CreateCollectionAsync(It.IsAny<string>(), request))
            .ReturnsAsync(createdCollection);

        // Act
        var result = await _controller.CreateCollection(request);

        // Assert
        result.Result.Should().BeOfType<CreatedAtActionResult>();
        var createdResult = result.Result as CreatedAtActionResult;
        createdResult?.ActionName.Should().Be(nameof(CollectionsController.GetCollection));
        createdResult?.RouteValues?["collectionId"].Should().Be(createdCollection.Id);
    }

    [Fact]
    public async Task CreateCollection_WithEmptyName_ReturnsBadRequest()
    {
        // Arrange
        var request = new CreateCollectionRequest("", "Description");

        // Act
        var result = await _controller.CreateCollection(request);

        // Assert
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    // ---- UpdateCollection Tests ----

    [Fact]
    public async Task UpdateCollection_WithValidRequest_ReturnsOk()
    {
        // Arrange
        var collectionId = Guid.NewGuid();
        var request = new UpdateCollectionRequest("Updated Name", "Updated Description");
        var updatedCollection = new CollectionDetailDto
        {
            Id = collectionId,
            Name = "Updated Name",
            Description = "Updated Description"
        };
        _collectionServiceMock
            .Setup(s => s.UpdateCollectionAsync(collectionId, It.IsAny<string>(), request))
            .ReturnsAsync(updatedCollection);

        // Act
        var result = await _controller.UpdateCollection(collectionId, request);

        // Assert
        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task UpdateCollection_WithInvalidId_ReturnsNotFound()
    {
        // Arrange
        var request = new UpdateCollectionRequest("Name", "Description");
        _collectionServiceMock
            .Setup(s => s.UpdateCollectionAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<UpdateCollectionRequest>()))
            .ThrowsAsync(new KeyNotFoundException());

        // Act
        var result = await _controller.UpdateCollection(Guid.NewGuid(), request);

        // Assert
        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task UpdateCollection_WithEmptyName_ReturnsBadRequest()
    {
        // Arrange
        var request = new UpdateCollectionRequest("", "Description");

        // Act
        var result = await _controller.UpdateCollection(Guid.NewGuid(), request);

        // Assert
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    // ---- DeleteCollection Tests ----

    [Fact]
    public async Task DeleteCollection_WithValidId_ReturnsNoContent()
    {
        // Arrange
        var collectionId = Guid.NewGuid();
        _collectionServiceMock
            .Setup(s => s.DeleteCollectionAsync(collectionId, It.IsAny<string>()))
            .ReturnsAsync(true);

        // Act
        var result = await _controller.DeleteCollection(collectionId);

        // Assert
        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task DeleteCollection_WithInvalidId_ReturnsNotFound()
    {
        // Arrange
        _collectionServiceMock
            .Setup(s => s.DeleteCollectionAsync(It.IsAny<Guid>(), It.IsAny<string>()))
            .ReturnsAsync(false);

        // Act
        var result = await _controller.DeleteCollection(Guid.NewGuid());

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }

    // ---- AddCardToCollection Tests ----

    [Fact]
    public async Task AddCardToCollection_WithValidRequest_ReturnsCreated()
    {
        // Arrange
        var collectionId = Guid.NewGuid();
        var request = new AddCardToCollectionRequest("oracle-id", null, 2);
        var addedCard = new CollectionCardDto
        {
            Id = Guid.NewGuid(),
            OracleId = "oracle-id",
            Quantity = 2
        };
        _collectionServiceMock
            .Setup(s => s.AddCardToCollectionAsync(collectionId, It.IsAny<string>(), request))
            .ReturnsAsync(addedCard);

        // Act
        var result = await _controller.AddCardToCollection(collectionId, request);

        // Assert
        result.Result.Should().BeOfType<CreatedAtActionResult>();
        var createdResult = result.Result as CreatedAtActionResult;
        createdResult?.ActionName.Should().Be(nameof(CollectionsController.GetCollectionCard));
    }

    [Fact]
    public async Task AddCardToCollection_WithEmptyOracleId_ReturnsBadRequest()
    {
        // Arrange
        var request = new AddCardToCollectionRequest("");

        // Act
        var result = await _controller.AddCardToCollection(Guid.NewGuid(), request);

        // Assert
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task AddCardToCollection_WithZeroTotalQuantity_ReturnsBadRequest()
    {
        // Arrange
        var request = new AddCardToCollectionRequest("oracle-id", null, 0, 0);

        // Act
        var result = await _controller.AddCardToCollection(Guid.NewGuid(), request);

        // Assert
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task AddCardToCollection_WithFoilOnlyQuantity_ReturnsCreated()
    {
        // Arrange — quantity=0, quantityFoil=1 should be valid (foil-only add)
        var collectionId = Guid.NewGuid();
        var request = new AddCardToCollectionRequest("oracle-id", null, 0, 1);
        var addedCard = new CollectionCardDto { Id = Guid.NewGuid(), OracleId = "oracle-id", Quantity = 0, QuantityFoil = 1 };
        _collectionServiceMock
            .Setup(s => s.AddCardToCollectionAsync(collectionId, It.IsAny<string>(), request))
            .ReturnsAsync(addedCard);

        // Act
        var result = await _controller.AddCardToCollection(collectionId, request);

        // Assert
        result.Result.Should().BeOfType<CreatedAtActionResult>();
    }

    [Fact]
    public async Task AddCardToCollection_WithInvalidCollection_ReturnsNotFound()
    {
        // Arrange
        var request = new AddCardToCollectionRequest("oracle-id");
        _collectionServiceMock
            .Setup(s => s.AddCardToCollectionAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<AddCardToCollectionRequest>()))
            .ThrowsAsync(new KeyNotFoundException());

        // Act
        var result = await _controller.AddCardToCollection(Guid.NewGuid(), request);

        // Assert
        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    // ---- GetCollectionCard Tests ----

    [Fact]
    public async Task GetCollectionCard_WithValidIds_ReturnsOk()
    {
        // Arrange
        var collectionId = Guid.NewGuid();
        var cardId = Guid.NewGuid();
        var card = new CollectionCardDto
        {
            Id = cardId,
            OracleId = "oracle-id",
            Quantity = 2
        };
        _collectionServiceMock
            .Setup(s => s.GetCollectionCardAsync(collectionId, cardId, It.IsAny<string>()))
            .ReturnsAsync(card);

        // Act
        var result = await _controller.GetCollectionCard(collectionId, cardId);

        // Assert
        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetCollectionCard_WithInvalidCardId_ReturnsNotFound()
    {
        // Arrange
        _collectionServiceMock
            .Setup(s => s.GetCollectionCardAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>()))
            .ReturnsAsync((CollectionCardDto?)null);

        // Act
        var result = await _controller.GetCollectionCard(Guid.NewGuid(), Guid.NewGuid());

        // Assert
        result.Result.Should().BeOfType<NotFoundResult>();
    }

    // ---- UpdateCollectionCard Tests ----

    [Fact]
    public async Task UpdateCollectionCard_WithValidRequest_ReturnsOk()
    {
        // Arrange
        var collectionId = Guid.NewGuid();
        var cardId = Guid.NewGuid();
        var request = new UpdateCollectionCardRequest(3, 2, Notes: "Updated notes");
        var updatedCard = new CollectionCardDto
        {
            Id = cardId,
            OracleId = "oracle-id",
            Quantity = 3,
            QuantityFoil = 2,
            Notes = "Updated notes"
        };
        _collectionServiceMock
            .Setup(s => s.UpdateCollectionCardAsync(collectionId, cardId, It.IsAny<string>(), request))
            .ReturnsAsync(updatedCard);

        // Act
        var result = await _controller.UpdateCollectionCard(collectionId, cardId, request);

        // Assert
        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task UpdateCollectionCard_WithZeroTotalQuantity_ReturnsBadRequest()
    {
        // Arrange
        var request = new UpdateCollectionCardRequest(0, 0);

        // Act
        var result = await _controller.UpdateCollectionCard(Guid.NewGuid(), Guid.NewGuid(), request);

        // Assert
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    // ---- RemoveCardFromCollection Tests ----

    [Fact]
    public async Task RemoveCardFromCollection_WithValidIds_ReturnsNoContent()
    {
        // Arrange
        var collectionId = Guid.NewGuid();
        var cardId = Guid.NewGuid();
        _collectionServiceMock
            .Setup(s => s.RemoveCardFromCollectionAsync(collectionId, cardId, It.IsAny<string>()))
            .ReturnsAsync(true);

        // Act
        var result = await _controller.RemoveCardFromCollection(collectionId, cardId);

        // Assert
        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task RemoveCardFromCollection_WithInvalidCardId_ReturnsNotFound()
    {
        // Arrange
        _collectionServiceMock
            .Setup(s => s.RemoveCardFromCollectionAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>()))
            .ReturnsAsync(false);

        // Act
        var result = await _controller.RemoveCardFromCollection(Guid.NewGuid(), Guid.NewGuid());

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }

    // ---- RemoveCardByOracle Tests ----

    [Fact]
    public async Task RemoveCardByOracle_WithValidIds_ReturnsNoContent()
    {
        // Arrange
        var collectionId = Guid.NewGuid();
        var oracleId = "oracle-id";
        _collectionServiceMock
            .Setup(s => s.RemoveCardByOracleAsync(collectionId, oracleId, It.IsAny<string>()))
            .ReturnsAsync(true);

        // Act
        var result = await _controller.RemoveCardByOracle(collectionId, oracleId);

        // Assert
        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task RemoveCardByOracle_WithInvalidOracleId_ReturnsNotFound()
    {
        // Arrange
        _collectionServiceMock
            .Setup(s => s.RemoveCardByOracleAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(false);

        // Act
        var result = await _controller.RemoveCardByOracle(Guid.NewGuid(), "oracle-id");

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }

    // ---- GetDeckCards Tests ----

    [Fact]
    public async Task GetDeckCards_WithValidCollectionId_ReturnsOkWithCards()
    {
        // Arrange
        var collectionId = Guid.NewGuid();
        var cards = new[]
        {
            new CardDto { Name = "Card 1", OracleId = "oracle-1" },
            new CardDto { Name = "Card 2", OracleId = "oracle-2" }
        };
        _collectionServiceMock
            .Setup(s => s.GetAvailableCardsForDeckAsync(collectionId, It.IsAny<string>()))
            .ReturnsAsync(cards);

        // Act
        var result = await _controller.GetDeckCards(collectionId);

        // Assert
        result.Result.Should().BeOfType<OkObjectResult>();
        var okResult = result.Result as OkObjectResult;
        var returnValue = okResult?.Value as CardDto[];
        returnValue.Should().HaveCount(2);
    }
}
