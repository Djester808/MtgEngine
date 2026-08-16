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

#### GET /api/collections/owned-oracle-ids
Every OracleId the current user owns a copy of, across all of their collections.
```http
GET /api/collections/owned-oracle-ids
```
**Response**: `string[]` — distinct OracleIds.

Decks and collections share the `Collections` table, so "owned" is the `IsDeck = false`
half of it: a card listed in a deck is not a card you own, which is the entire question
the deck grid asks in order to grey out the cards you are missing. Rows whose copies have
gone to zero (`Quantity + QuantityFoil == 0`) are placeholders and do not count.

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

#### POST /api/collections/{collectionId}/cards/{cardId}/move
Move copies of a card into another collection. Omit the quantities to move the whole row.
```http
POST /api/collections/{collectionId}/cards/{cardId}/move
Content-Type: application/json

{ "targetCollectionId": "guid", "quantity": 1, "quantityFoil": 0 }
```
**Response**: `MoveCardResultDto` — `{ target, sourceRemainder }`, where `sourceRemainder`
is null when the row moved whole and left the source.

#### POST /api/collections/{collectionId}/cards/move
Move several whole rows at once (multi-select in the UI). **All-or-nothing**: if any id is
no longer in the collection the whole batch is rejected, rather than moving some and
silently skipping the rest.
```http
POST /api/collections/{collectionId}/cards/move
Content-Type: application/json

{ "targetCollectionId": "guid", "cardIds": ["guid", "guid"] }
```
**Response**: `MoveCardsResultDto` — `{ cardsMoved, cardsFolded, copiesTransferred, removedCardIds }`.

#### POST /api/collections/{collectionId}/merge
Fold another collection's cards into this one.
```http
POST /api/collections/{collectionId}/merge
Content-Type: application/json

{ "sourceCollectionId": "guid", "deleteSource": false }
```
**Response**: `MergeCollectionsResultDto` — `{ cardsMoved, cardsFolded, copiesTransferred,
sourceDeleted, target }`.

**Transfer semantics (both endpoints)**
- Copies fold into the destination row for the same **(OracleId, ScryfallId, Board)**;
  anything else becomes its own row. This is the same key as the unique index, so a
  transfer can never collide with it.
- **Acquisition data travels with the copies**: the moved row keeps its original
  `AddedAt` and price-at-add, because it is the same physical card. When two rows fold
  together the *earlier* acquisition wins, so the surviving row still describes the
  oldest copy in it. Resetting these would restate when a card was acquired and wipe the
  baseline the price-change display compares against.
- Both collections' `UpdatedAt` are bumped. Decks and collections share the table, so a
  transfer works between either — ownership is what is checked.
- Errors: unknown or unowned collection → 404, self-merge / self-move / moving more
  copies than are held / moving nothing → 409 (via `AiExceptionHandler`).

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

### 6. Price Tracking
Scryfall publishes prices daily (USD from TCGplayer, EUR from Cardmarket, tix from
Cardhoarder), which the app surfaces three ways:

- **Current prices per printing** — `CardDto.Prices` and `PrintingDto.Prices`
  (`CardPricesDto`: `usd`, `usdFoil`, `usdEtched`, `eur`, `eurFoil`, `tix`, plus
  `tcgplayerId`/`cardmarketId`/`mtgoId` for building listing links). Null means "no
  listing for that finish", never zero.
- **Price at acquisition** — `CollectionCard.PriceUsdAtAdd` / `PriceUsdFoilAtAdd`,
  captured once when the row is created and never rewritten, so the client can show
  what a copy cost then against what it costs now.
- **Daily history** — `CardPriceSnapshots` (one row per printing per day, unique on
  `(ScryfallId, CapturedAt)`), written by `PriceSnapshotWorker` for every printing that
  appears in a collection. Scryfall exposes no historical endpoint, so history exists
  only from the day a printing is first owned. Read it via:

```http
GET /api/cards/printings/{scryfallId}/price-history?days=90
```
**Response**: `PricePointDto[]` — `{ date, usd, usdFoil, eur, tix }`, oldest first.

`CacheCleanupWorker` prunes snapshots past five years, the largest window the endpoint
serves.

### 7. Card History

Every change a user makes to a card is recorded as an append-only `CollectionCardEvent`,
which backs the **History tab** in the client's card modal.

```http
GET /api/cards/{oracleId}/history?limit=100
```
**Response**: `CardHistoryEntryDto[]` — newest first, capped at `CardHistoryService.MaxLimit`
(500). Scoped to the caller: this is *their* activity with the card, not the card's.

`eventType` serializes as its name, never an ordinal (global `JsonStringEnumConverter`):

| `eventType` | Written when |
|---|---|
| `Added` | A new row was created for the card |
| `QuantityChanged` | Copies were added to or taken off an existing row (sign says which) |
| `PrintingChanged` | The row was re-pinned to a different printing |
| `Removed` | The row was deleted — including when its whole collection or deck was |
| `MovedOut` / `MovedIn` | The two halves of a move or merge, each naming the other end |

**Recording rules**
- Events are staged onto the same `SaveChangesAsync` as the change they describe, so the
  log cannot drift from the data. The one exception is the add path, which saves a second
  time: increments run as `ExecuteUpdate` and bypass the change tracker, so the resulting
  copy counts are only knowable after the post-write row read.
- `UserId`, `CollectionName` and `IsDeck` are **denormalised onto the event**, and the
  table has **no foreign key to `Collections`**. That is deliberate — a cascade would
  delete the history along with the collection, and "which deck did I pull this out of"
  is exactly the question the tab exists to answer. Names are the values as they read at
  the time; renaming a collection later does not rewrite its past events.
- A notes-only edit records nothing — nothing about the card itself moved.
- `SetCode`/`PriceUsd` are only populated where the server already had the card definition
  in hand (the add path). Resolving them on every event would put a Scryfall lookup inside
  merge loops that already iterate hundreds of rows. Null means "not recorded".
- **There is no backfill.** A `CollectionCard` row knows only its current state, so history
  exists from the day this shipped. An empty tab on a long-owned card is correct.

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

### CardHistoryService

Owns the `CollectionCardEvent` trail. `CollectionService` depends on it and calls
`Record(...)` from every mutation path; the read side backs the card modal's History tab.

```csharp
// Staged onto the caller's SaveChangesAsync — this does not save on its own.
void Record(Collection collection, CollectionCard card, CollectionCardEventType eventType,
            int quantityDelta, int quantityFoilDelta, int quantityAfter, int quantityFoilAfter,
            Collection? counterpart = null, string? setCode = null, decimal? priceUsd = null)

Task<CardHistoryEntryDto[]> GetForCardAsync(string userId, string oracleId, int limit, CancellationToken ct)
```

Copy counts are passed explicitly rather than read off the entity, because on a removal the
entity still holds the copies it is about to lose.

## Database Configuration

The DbContext is configured with:
- **SQLite** for development/simple deployments
- **Relationships**: Collections have many CollectionCards (cascade delete)
- **Constraints**:
  - Unique index on `(CollectionId, ScryfallId, Board)` — this only constrains rows that
    **pin a printing**. SQLite treats NULLs as distinct, so it silently permitted any
    number of duplicate *unpinned* rows for one card, and unpinned rows are the majority
    (decks rarely pin a printing). A card could therefore occupy several rows in one
    collection, rendering as duplicate tiles with its count split between them.
  - A filtered companion index `IX_CollectionCards_Unpinned_Unique` on
    `(CollectionId, OracleId, Board) WHERE ScryfallId IS NULL` closes that hole. The
    `UnpinnedRowUniqueness` migration folds pre-existing duplicates into their earliest
    row (summing quantities, keeping the oldest `AddedAt`/price-at-add) before creating it.
  - Required fields: UserId, Name, OracleId, Quantity, IsFoil
  - Max lengths: UserId (256), Name (256), Description (1000), OracleId (256), Notes (1000)
- **Timestamps**: CreatedAt and UpdatedAt automatically tracked
- **All `DateTime`s are UTC, and say so on the wire.** SQLite has no date type, so values
  round-trip through TEXT and materialize as `Unspecified`; serialized that way they reach
  the client with no trailing `Z`, and **JavaScript reads a bare date-time as local**. For a
  browser at UTC-5 that put every timestamp five hours in the future — the card modal's
  History tab read "just now" for five hours, and `addedAt` displayed the UTC clock time as
  if it were local. A convention in `MtgEngineDbContext.ConfigureConventions` converts every
  `DateTime`/`DateTime?` on read, so no endpoint has to remember to. It is CLR-type
  preserving, so it needs no migration.
  - One consequence worth knowing: a value that is a *calendar day* rather than an instant
    (`CardPriceSnapshot.CapturedAt`, stamped at UTC midnight) must be **rendered in UTC**, or
    every point west of UTC lands on the previous day's label. See `formatDay` in the
    client's `utils/price-chart.ts`. Instants (`addedAt`, `publishedAt`, history
    `createdAt`) should render in local time as normal.

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
- ~~Price tracking integration~~ — shipped, see "Price Tracking" above
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
