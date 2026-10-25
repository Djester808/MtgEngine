# Card Collection Feature Documentation

## Overview

The Card Collection feature allows users to:
- **Create and manage multiple collections** - Organize cards into named collections (e.g., "Modern Staples", "My Cube", "Vintage")
- **Track owned cards** - Add cards you own with quantity tracking, foil status, and custom notes
- **Build decks from your collection** - Use cards from your collections to construct decks
- **View card details** - See full card information including artwork, mana costs, and abilities from Scryfall

## Architecture

### Existing deck-to-game boundary

`Collection` and `CollectionCard` are the source for saved decks as well as inventory.
A `CollectionCard` row is a quantity-bearing entry, not one in-game card instance.
It stores the selected `ScryfallId`, `OracleId`, board, and independent nonfoil,
foil and etched quantities. `ICardLookup.ResolveForEntryAsync` is the existing
shared printing-resolution policy used by collection reads and mutations.

The catalog `MtgEngine.Domain.Models.CardDefinition` contains displayable Oracle
and printing data. `MtgEngine.Cards.CardDefinition` instead contains executable
rules structures. These representations serve different consumers; the common
short name does not make their responsibilities interchangeable.

`GameLobbyDecks` expands saved main-board rows into `StartingCard` selections,
preserving Oracle ID, selected Scryfall printing ID and nonfoil/foil/etched finish.
Commander extraction moves the full selection into the command zone. These are
references to existing catalog entries, not additional card definitions. Game
objects retain their selection through zone changes and serialized replay;
tokens and copies do not inherit a deck selection. Visible artwork uses the shared
printing resolver with an Oracle fallback, and hidden/face-down views omit selections.
Older startup logs containing Oracle-ID lists migrate without inventing printings.
Game startup now
prefers executable definitions from `CardFilePool`; an admitted honour-system deck
can resolve missing definitions from the existing bulk catalog through
`GameDefinitionResolver` and `IPrintedCardLookup`. The shared `PrintedCardMapping`
preserves printed faces/layout and text but does not invent executable abilities
or upgrade coverage. Strict and excluded-card admission policies remain in place.
New games persist their admitted coverage in the opening log event so restarting
does not upgrade manual cards after generated files change.
The handoff and its intended migration are tracked in `LIBRARY_IMPLEMENTATION.md`.
No new collection model or duplicate card lookup is required by that migration.

The basic card contract now validates `StartingCard` selection shape and the existing
`Cards.CardDefinition` before `GameObject.CreateCard` creates an initial library or
command-zone instance. Definition and selected Oracle IDs must agree; a printing
may remain unspecified. `CardDefinition.GetFace` provides bounded access to printed
faces, including descriptive Oracle text. Printing availability and membership are
still catalog-lookup responsibilities, not claims made by structural validation.
`CardInstanceId` remains server-only and persists across object changes; quantities
remain on collection rows. See `research/CARD_CONTRACT_EVIDENCE.md` for the concrete
MTGO/Arena comparison and its limits.

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

### Audited mutation and read boundaries

All collection/deck deletion paths remove published posts and comments in the same
transaction, including a merge that deletes its source. Adopting an unspecified
printing adds nonfoil, foil and etched quantities consistently. Bulk moves preload
destination rows once and match on Oracle ID, printing, board and language.

AI refinement uses `ReplaceCardCopiesAsync`: one outgoing main-board copy becomes
one incoming copy in one transaction. Other copies/printings remain, and neither
commander can be swapped out. A failure rolls back both inventory and history.

Paged collection reads share a bounded snapshot cache (50,000 rows total, one-minute
expiry), keyed by owner, collection update time and catalog version. Each request
still verifies ownership; mutations and catalog reloads invalidate the read key.
This avoids resolving the entire collection again for every page while preserving
whole-collection filtering, ordering and valuation. The first read after invalidation
still builds the complete read snapshot.

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

## Compleat collection metadata and bulk Undo port (2026-10-20)

The current model stores regular Quantity and QuantityFoil separately; the older IsFoil examples above are historical. CollectionCard now also stores Language (default en), Condition (NM/LP/MP/HP/DMG or unset), private PurchasePrice/PurchaseCurrency and TagsJson. The printing uniqueness keys include language for both pinned and unpinned cards. Existing rows migrate to en and an empty tag list. Moves retain language and metadata; folded rows union tags and keep the earlier acquisition purchase-price baseline.

Owner-only GET/PUT /api/collections/{id}/cards/{rowId}/metadata serves purchase details. Purchase fields are deliberately absent from the shared card DTO used by public deck/forum readers. POST /api/collections/{id}/cards/bulk accepts 1–500 row ids and tag/untag/condition/remove. It validates ownership and every selected row before committing the whole edit. The result includes a persisted owner-scoped UndoToken and a five-second expiry; POST /api/collections/undo/{token} restores only the selected rows after comparing the exact post-edit fingerprint. Newer edits cause 409 instead of being overwritten, unrelated rows remain unchanged, and undo is single-use. Removed/restored copies append history entries. A background worker deletes expired undo records.

The collection card sheet exposes owned details alongside personal notes. Select mode exposes Edit selected, with all four bulk actions and Undo.

## Collection export and local value visibility

GET /api/collections/{collectionId}/export?format=csv|text is authenticated and owner-only; deck ids and foreign ids return 404. It exports the full collection, independent of client filters. CSV splits regular and foil quantities into separate rows, preserving printing/oracle IDs, name, set, language, condition, purchase price/currency, original market prices, tags, multiline notes and added time. Prices use invariant decimal units, not cents. Fields are quoted; formula-leading text is neutralized for spreadsheets. Plain text carries quantities and names with *F* for foils. Empty collections export a CSV header or empty text file. Responses use no-store. A read transaction keeps row metadata consistent with quantities.

The collection header has Export CSV, Export text, and an eye control for its USD market-value summary. Unknown prices are excluded and the priced-copy count is shown. Hide value removes the amount from the rendered content; the preference persists on this device and does not change public collection sharing or the contents of an explicitly requested export.


## Compleat parity extension (2026-10-21)

Ownership now distinguishes nonfoil, foil and etched quantities on the printing/language row. QuantityEtched defaults to zero; omitting it in an update preserves the existing etched count. A quantity-only update preserves notes; use UpdateNotes=true to explicitly clear shared notes. A row is empty only when all three quantities are zero.

Each owned finish has independent condition, tags, private purchase price/currency, private notes, acquisition timestamp and price-at-add snapshot. FinishMetadataJson stores these details without splitting existing rows. Legacy rows fall back to their original metadata. Public collection DTO FinishData contains only condition, tags, acquisition date and market snapshot; purchase details and finish notes are returned only by the owner-only metadata endpoint. Passing finish=nonfoil|foil|etched selects that finish. A language change with a selected finish moves only that finish; an existing owned finish in the destination language is a conflict.

Migration AddIndependentCollectionFinishes adds fields with zero/empty defaults and nullable price snapshots; it does not replace existing collection rows. History records etched delta and remaining count. Collection totals, deck totals and legality include all finishes, while each finish is valued at its own actual market price.

Collection mutations return X-Compleat-Undo, X-Compleat-Undo-Expires, X-Compleat-Undo-Collections and X-Compleat-Undo-Kind. Undo atomically restores both transfer ends, verifies owner, expiry and single use, and rejects a newer-state hash mismatch. Edit/move offers last five seconds; last-add offers last one hour. Deck additions use the same mechanism for Explore visit Undo. No operation silently overwrites newer card changes.

CSV and text export include every owned finish and owner metadata. An unspecified printing exports empty set/collector fields. CSV formula-leading values are escaped. Global printing ownership excludes decks and other owners. The collection UI pages 100 matching rows and releases offscreen tile/text content; global filtering and Insights still use the complete collection in memory.

## Server collection reads and complete regex (2026-10-21 follow-up)

The collection screen now uses owner-only POST /api/collections/{id}/query, returning a CollectionDetailDto whose Cards contain only the requested page. PageData contains the actual page, page size, matching total, full row count, full valuation, ten compact Insights summaries, set options and a query notice. Default pages contain 100 rows; supported page sizes are 20–200. Filters and sorting run across the collection before pagination, including finish-specific condition/tag matching and language. Page indices clamp after removals. Section ordering is global. Same-card mode pages whole oracle groups so a printing group cannot be split; its page may consequently contain multiple stored printing/language rows per tile. Filtering/group changes reset to page one; mutations and Undo retain the current query and refresh authoritative totals. The browser no longer downloads the full collection to compute filters or Insights.

POST /api/collections/{id}/rows accepts at most 100 oracle IDs and resolves only those owner rows for card sheets and the currently browsed Add Cards results, including printings/languages outside the active page. Both read endpoints reject foreign collections and deck IDs. Metadata/merge HTTP responses avoid full card arrays; their views refetch the current page. The legacy full-detail service remains available internally to exports and other existing consumers. Broad metadata queries still scan and resolve collection rows on the server; this change bounds browser loading rather than claiming an indexed database plan for every metadata expression.

Collection regex now executes in .NET with the original Compleat semantics: full lookarounds, backreferences and repeated groups, no 200-character restriction, culture-invariant matching and a 50 ms per-match timeout. Regex overrides the whole-word flag. Whole-word plain searches match punctuation-delimited name tokens. Invalid regex matches nothing; a timed-out row is a non-match. A readable query notice replaces browser exceptions, and subsequent valid searches recover normally. The browser does not execute the collection regex.

Verification: CollectionReadTests covers bounded pages, global summaries/filtering/order, whole oracle groups, full regex constructs and length, invalid expressions, timeouts, cancellation, row lookup and owner/deck guards. verify:collection-scale checks a real 201-row collection on phone/tablet/desktop and records fetch payloads, three pages, cross-page filtering, complete regex, invalid recovery and no document overflow. verify:parity-port rechecks finish metadata, language quantities, transfer/export and Undo on the same three device sizes.

## Game lobby deck loading (2026-10-22)

GameLobbyDecks reads only Collections whose UserId matches the authenticated caller and
IsDeck is true. Lobby deck choices contain summary metadata; private expanded card lists
stay in the startup service. Only board main is loaded, with normal/foil/etched quantities
expanded. Commander format requires CommanderOracleId among those cards and removes one
copy into command. The selected format must match and DeckGate applies the table's fixed
honour-system setting. A1000-card expansion cap is a service resource limit, not a Magic
format rule. Full ban lists, singleton/size and commander eligibility rules are not established
by this reader. It is groundwork for lobby endpoints, not a new public deck-sharing route.

### Commander game admission

GameLobbyDecks delegates format checks to CommanderDeckAdmission, which resolves
oracle metadata once per distinct ID and invokes the pure CommanderDeckValidator.
The lobby now checks exactly 100 main-deck cards including the designated commander,
commander eligibility, Commander legality, color identity (including colorless decks),
basic land types, and singleton with basic-land and explicit copy-count exceptions.
Normal, foil and etched quantities all contribute. Side/maybe boards are excluded.
DeckGate remains responsible only for engine implementation coverage; honour-system
mode does not bypass these format checks. Missing oracle metadata refuses admission.
This currently follows the collection model's single CommanderOracleId. Two-commander
selection and additional card-specific construction overrides remain outstanding.

CommanderDeckValidator also accepts two resolved commanders through CommanderPairPolicy.
The policy handles matching Partner variants, reciprocal named partners, Backgrounds,
and Doctor's companion, and validates the union of their color identities. The existing
collection/editor still exposes one commander; persisted second-commander selection is
remaining integration work, so this policy alone does not enable partner decks in the UI.

### Saved commander pairs

Collections now have nullable SecondaryCommanderOracleId, added by migration
20261022224115_AddSecondaryCommander. Deck list/detail responses and create/update
requests carry the field; null in an update clears it. GameLobbyDecks removes each
selected commander from main cards, validates the pair through CommanderDeckAdmission,
and passes both to the command zone. A valid 100-card partner deck yields a 98-card
starting library. The client editor still needs to expose and preserve the second
selection; older update requests without this field clear it.
