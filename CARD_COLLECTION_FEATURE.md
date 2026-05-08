# Card Collection Feature Documentation

## Overview

The Card Collection feature allows users to:
- **Create and manage multiple collections** - Organize cards into named collections (e.g., "Modern Staples", "My Cube", "Vintage")
- **Track owned cards** - Add cards you own with quantity tracking, foil status, and custom notes
- **Build decks from your collection** - Use cards from your collections to construct decks
- **View card details** - See full card information including artwork, mana costs, and abilities from Scryfall

## Architecture

### Domain Models

#### Collection
Represents a user's card collection with metadata:
- **Id**: Unique identifier
- **UserId**: Owner of the collection
- **Name**: Collection name (required)
- **Description**: Optional description
- **Cards**: List of owned cards in this collection
- **CreatedAt/UpdatedAt**: Timestamps

#### CollectionCard
Represents a specific card instance owned in a collection:
- **Id**: Unique identifier
- **CollectionId**: Parent collection
- **OracleId**: Card's Oracle ID (from Scryfall)
- **ScryfallId**: Specific card printing ID
- **Quantity**: How many copies you own
- **IsFoil**: Whether this copy is foil
- **Notes**: Custom notes about the card
- **AddedAt**: When added to collection

### Database Schema

Uses Entity Framework Core with SQLite (can be changed to PostgreSQL/SQL Server):
- **Collections** table - User's collections with cascade delete
- **CollectionCards** table - Cards in collections with foreign key to Collection
- Indexes on UserId, CollectionId, and (CollectionId, OracleId) unique constraint

## API Endpoints

### Collections Management

#### GET /api/collections
List all collections for the current user.
```http
GET /api/collections
```
**Response**: `CollectionDto[]`
```json
[
  {
    "id": "guid",
    "name": "Modern Staples",
    "description": "Cards for modern format",
    "cardCount": 247,
    "createdAt": "2026-01-15T10:30:00Z",
    "updatedAt": "2026-04-23T15:45:00Z"
  }
]
```

#### GET /api/collections/{collectionId}
Get a specific collection with all cards.
```http
GET /api/collections/550e8400-e29b-41d4-a716-446655440000
```
**Response**: `CollectionDetailDto`
```json
{
  "id": "guid",
  "name": "Modern Staples",
  "description": "Cards for modern format",
  "createdAt": "2026-01-15T10:30:00Z",
  "updatedAt": "2026-04-23T15:45:00Z",
  "cards": [
    {
      "id": "guid",
      "oracleId": "e95a0f49-39fa-4bef-b83d-3f08fe85f4d0",
      "scryfallId": "c0e3a3d4-2f8d-4b5e-9c3a-1d2e4f5a6b7c",
      "quantity": 2,
      "isFoil": false,
      "notes": "Playset for main deck",
      "addedAt": "2026-02-01T12:00:00Z",
      "cardDetails": {
        "cardId": "e95a0f49-39fa-4bef-b83d-3f08fe85f4d0",
        "oracleId": "e95a0f49-39fa-4bef-b83d-3f08fe85f4d0",
        "name": "Lightning Bolt",
        "manaCost": "{R}",
        "manaValue": 1,
        "cardTypes": ["Instant"],
        "subtypes": [],
        "supertypes": [],
        "oracleText": "Lightning Bolt deals 3 damage to any target.",
        "power": null,
        "toughness": null,
        "startingLoyalty": null,
        "keywords": [],
        "imageUriNormal": "https://cards.scryfall.io/...",
        "colorIdentity": ["R"],
        "artist": "Christopher Rush",
        "setCode": "LEA"
      }
    }
  ]
}
```

#### POST /api/collections
Create a new collection.
```http
POST /api/collections
Content-Type: application/json

{
  "name": "Modern Staples",
  "description": "Cards for modern format"
}
```
**Response**: `CollectionDetailDto` (201 Created)

#### PUT /api/collections/{collectionId}
Update a collection's metadata.
```http
PUT /api/collections/550e8400-e29b-41d4-a716-446655440000
Content-Type: application/json

{
  "name": "Modern Staples - Updated",
  "description": "Updated description"
}
```
**Response**: `CollectionDetailDto` (200 OK)

#### DELETE /api/collections/{collectionId}
Delete a collection and all its cards.
```http
DELETE /api/collections/550e8400-e29b-41d4-a716-446655440000
```
**Response**: 204 No Content

### Collection Cards Management

#### POST /api/collections/{collectionId}/cards
Add a card to a collection (increments quantity if already exists).
```http
POST /api/collections/550e8400-e29b-41d4-a716-446655440000/cards
Content-Type: application/json

{
  "oracleId": "e95a0f49-39fa-4bef-b83d-3f08fe85f4d0",
  "scryfallId": "c0e3a3d4-2f8d-4b5e-9c3a-1d2e4f5a6b7c",
  "quantity": 2,
  "isFoil": false,
  "notes": "Playset for main deck"
}
```
**Response**: `CollectionCardDto` (201 Created)

#### GET /api/collections/{collectionId}/cards/{cardId}
Get a specific card from a collection.
```http
GET /api/collections/550e8400-e29b-41d4-a716-446655440000/cards/guid
```
**Response**: `CollectionCardDto` (200 OK)

#### PUT /api/collections/{collectionId}/cards/{cardId}
Update a card's quantity, foil status, or notes.
```http
PUT /api/collections/550e8400-e29b-41d4-a716-446655440000/cards/guid
Content-Type: application/json

{
  "quantity": 3,
  "isFoil": true,
  "notes": "Updated notes"
}
```
**Response**: `CollectionCardDto` (200 OK)

#### DELETE /api/collections/{collectionId}/cards/{cardId}
Remove a card from a collection.
```http
DELETE /api/collections/550e8400-e29b-41d4-a716-446655440000/cards/guid
```
**Response**: 204 No Content

#### DELETE /api/collections/{collectionId}/cards/by-oracle/{oracleId}
Remove all copies of a card (by OracleId) from a collection.
```http
DELETE /api/collections/550e8400-e29b-41d4-a716-446655440000/cards/by-oracle/e95a0f49-39fa-4bef-b83d-3f08fe85f4d0
```
**Response**: 204 No Content

### Deck Building

#### GET /api/collections/{collectionId}/deck-cards
Get all cards from a collection available for deck building.
```http
GET /api/collections/550e8400-e29b-41d4-a716-446655440000/deck-cards
```
**Response**: `CardDto[]` (200 OK)

Returns all cards in the collection with their full details from Scryfall, which can be used to build a deck.

## Key Features

### 1. Multi-Collection Support
Users can create multiple collections to organize cards by:
- Format (Modern, Standard, Commander, etc.)
- Purpose (Main deck, sideboard, cube, etc.)
- Set/Edition
- Custom categories

### 2. Ownership Tracking
Each collection card tracks:
- How many copies are owned
- Whether they're foil
- Custom notes (e.g., condition, source, intended use)

### 3. Automatic Card Details
When cards are added or retrieved, the system automatically fetches and displays:
- Full card artwork (multiple sizes)
- Mana costs and color identity
- Power/Toughness for creatures
- Loyalty for planeswalkers
- Oracle text and abilities
- Artist and set information

### 4. Deck Building Foundation
The `GetAvailableCardsForDeckAsync` method returns all cards from a collection, enabling:
- Deck building UI to show available cards
- Quantity checking before adding to deck
- Full card details for reference

### 5. Timeline Tracking
Collections and collection cards track:
- Creation time
- Last update time
- When cards were added to collection

## Service Implementation Details

### CollectionService

Handles all business logic:
- **Collection Management**: CRUD operations with user isolation
- **Card Management**: Add, update, remove cards from collections
- **Quantity Handling**: Automatically increments quantity when adding duplicate cards
- **Foil Tracking**: Marks a collection card as foil if any copy is foil
- **Scryfall Integration**: Automatically fetches and caches card details
- **User Isolation**: All operations verify user ownership

### Key Methods

```csharp
// Collections
Task<CollectionDto[]> GetUserCollectionsAsync(string userId)
Task<CollectionDetailDto?> GetCollectionAsync(Guid collectionId, string userId)
Task<CollectionDetailDto> CreateCollectionAsync(string userId, CreateCollectionRequest request)
Task<CollectionDetailDto> UpdateCollectionAsync(Guid collectionId, string userId, UpdateCollectionRequest request)
Task<bool> DeleteCollectionAsync(Guid collectionId, string userId)

// Collection Cards
Task<CollectionCardDto> AddCardToCollectionAsync(Guid collectionId, string userId, AddCardToCollectionRequest request)
Task<CollectionCardDto?> GetCollectionCardAsync(Guid collectionId, Guid cardId, string userId)
Task<CollectionCardDto> UpdateCollectionCardAsync(Guid collectionId, Guid cardId, string userId, UpdateCollectionCardRequest request)
Task<bool> RemoveCardFromCollectionAsync(Guid collectionId, Guid cardId, string userId)
Task<bool> RemoveCardByOracleAsync(Guid collectionId, string oracleId, string userId)

// Deck Building
Task<CardDto[]> GetAvailableCardsForDeckAsync(Guid collectionId, string userId)
```

## Database Configuration

The DbContext is configured with:
- **SQLite** for development/simple deployments
- **Relationships**: Collections have many CollectionCards (cascade delete)
- **Constraints**:
  - Unique index on (CollectionId, OracleId) per collection
  - Required fields: UserId, Name, OracleId, Quantity, IsFoil
  - Max lengths: UserId (256), Name (256), Description (1000), OracleId (256), Notes (1000)
- **Timestamps**: CreatedAt and UpdatedAt automatically tracked

### Migrations

Initial migration includes:
- Collections table
- CollectionCards table
- All constraints and indexes

To apply migrations:
```bash
cd MtgEngine.Api
dotnet ef database update
```

## Future Enhancements

Potential features to build on this foundation:

### 1. Deck Management
- Save deck lists associated with collections
- Track deck composition and statistics
- Export/import deck lists (JSON, DeckBox format)
- Calculate mana curve, type distribution, etc.

### 2. Advanced Filtering
- Search cards by name, mana cost, type, etc.
- Filter collections by card properties
- Find gaps in collections (missing staples)

### 3. Statistics & Analytics
- Collection value tracking
- Format legality checking
- Deck statistics (curve, coverage, etc.)
- Collection insights (format representation, etc.)

### 4. Trading & Sharing
- Share collection views with other users
- Trade cards between users
- Wishlist functionality

### 5. Integration Features
- Import from external sources (TCGPlayer, Moxfield, etc.)
- Price tracking integration
- Bulk card entry
- Barcode scanning

### 6. Authentication
- Replace `DefaultUserId` with proper JWT/Identity integration
- Multi-tenant support
- User preferences

## Testing

The feature includes proper:
- Entity Framework model configuration
- Service layer abstraction with interfaces
- Dependency injection integration
- Error handling (KeyNotFoundException, validation)
- User isolation checks on all operations

## Configuration

### appsettings.json

Currently uses SQLite. To use a different database:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Data Source=mtgengine.db"
  }
}
```

**PostgreSQL** example:
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Port=5432;Database=mtgengine;Username=postgres;Password=password"
  }
}
```

Then use `services.AddDbContext<MtgEngineDbContext>(options => options.UseNpgsql(...))` in Program.cs.

## Security Notes

⚠️ **TODO: Replace `DefaultUserId` with proper authentication**

Current implementation uses a hardcoded user ID. Before production:
1. Implement ASP.NET Core Identity or JWT authentication
2. Extract userId from HttpContext.User claims
3. Add authorization filters to controller
4. Validate user ownership on every operation

```csharp
// Example fix:
private string GetUserId() => User.FindFirst(ClaimTypes.NameIdentifier)?.Value 
    ?? throw new UnauthorizedAccessException("User not authenticated");
```

## Conclusion

The Card Collection feature provides a solid foundation for building a complete MTG collection management system, with:
- ✅ Full CRUD operations
- ✅ User isolation
- ✅ Scryfall integration
- ✅ Deck building foundation
- ✅ Type-safe API
- ✅ EF Core database
- ✅ Proper service abstractions

You can now extend this with deck management, advanced filtering, statistics, and other features!
