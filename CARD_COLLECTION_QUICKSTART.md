# Card Collection Feature - Quick Start Guide

## Running the Application

1. **Build the project**:
```bash
cd MtgEngine
dotnet build
```

2. **Run the API**:
```bash
cd MtgEngine.Api
dotnet run
```

The database (`mtgengine.db`) will be created automatically on first run.

## Testing the API

### Using curl

#### Create a Collection
```bash
curl -X POST http://localhost:5000/api/collections \
  -H "Content-Type: application/json" \
  -d '{
    "name": "My Modern Collection",
    "description": "Cards for modern format"
  }'
```

#### List Collections
```bash
curl http://localhost:5000/api/collections
```

#### Add Card to Collection
```bash
curl -X POST http://localhost:5000/api/collections/{collectionId}/cards \
  -H "Content-Type: application/json" \
  -d '{
    "oracleId": "e95a0f49-39fa-4bef-b83d-3f08fe85f4d0",
    "quantity": 2,
    "isFoil": false,
    "notes": "Playset"
  }'
```

#### Get Collection with Cards
```bash
curl http://localhost:5000/api/collections/{collectionId}
```

#### Get Cards for Deck Building
```bash
curl http://localhost:5000/api/collections/{collectionId}/deck-cards
```

### Using Swagger UI
1. Navigate to `https://localhost:7000/swagger`
2. Expand "Collections" endpoints
3. Try endpoints interactively

## How to Find OracleIds

To add cards, you need the OracleId. You can:

1. **Use Scryfall API directly**:
```bash
curl "https://api.scryfall.com/cards/named?exact=Lightning%20Bolt"
```

2. **Extract from response**:
```json
{
  "oracle_id": "e95a0f49-39fa-4bef-b83d-3f08fe85f4d0",
  "name": "Lightning Bolt",
  ...
}
```

3. **Find printings of a card**:
```bash
curl "https://api.scryfall.com/cards/search?q=Lightning%20Bolt"
```

## Example Workflow

### 1. Create a Collection
```bash
COLLECTION_ID=$(curl -s -X POST http://localhost:5000/api/collections \
  -H "Content-Type: application/json" \
  -d '{"name":"Modern Deck"}' | jq -r '.id')
echo $COLLECTION_ID
```

### 2. Add Cards
```bash
# Lightning Bolt (2 copies, non-foil)
curl -X POST http://localhost:5000/api/collections/$COLLECTION_ID/cards \
  -H "Content-Type: application/json" \
  -d '{
    "oracleId": "e95a0f49-39fa-4bef-b83d-3f08fe85f4d0",
    "quantity": 2
  }'

# Counterspell (3 copies, 1 foil)
curl -X POST http://localhost:5000/api/collections/$COLLECTION_ID/cards \
  -H "Content-Type: application/json" \
  -d '{
    "oracleId": "a45b465e-4ff7-437b-a7b2-c38d1596491d",
    "quantity": 3
  }'
```

### 3. View Collection
```bash
curl http://localhost:5000/api/collections/$COLLECTION_ID | jq
```

### 4. Get Cards for Deck Building
```bash
curl http://localhost:5000/api/collections/$COLLECTION_ID/deck-cards | jq
```

## Database Access

To view the database directly:

### Using SQLite CLI
```bash
cd MtgEngine.Api
sqlite3 mtgengine.db
```

### Useful Queries
```sql
-- List all collections
SELECT Id, UserId, Name, CreatedAt FROM Collections;

-- List cards in a collection
SELECT cc.OracleId, cc.Quantity, cc.IsFoil, c.Name
FROM CollectionCards cc
JOIN Collections c ON cc.CollectionId = c.Id
WHERE c.Id = 'collection-id';

-- Count cards by collection
SELECT c.Name, COUNT(cc.Id) as CardCount, SUM(cc.Quantity) as TotalQuantity
FROM Collections c
LEFT JOIN CollectionCards cc ON c.Id = cc.CollectionId
GROUP BY c.Id, c.Name;
```

## Development Notes

### File Structure
```
MtgEngine.Api/
├── Controllers/
│   └── CollectionsController.cs      # API endpoints
├── Services/
│   └── CollectionService.cs          # Business logic
├── Data/
│   └── MtgEngineDbContext.cs         # Database context
├── Dtos/
│   └── Dtos.cs                       # All DTOs
├── Migrations/
│   ├── 20260423183918_InitialCreate.cs
│   └── 20260423183918_InitialCreate.Designer.cs
└── Program.cs                         # Configuration

MtgEngine.Domain/Models/
└── Collection.cs                      # Domain entities
```

### Adding New Migrations
If you modify the Collection models:
```bash
cd MtgEngine.Api
dotnet ef migrations add {MigrationName}
dotnet ef database update
```

### Changing Database Provider

To use PostgreSQL instead of SQLite:

1. Add NuGet package: `dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL`
2. Update Program.cs:
```csharp
builder.Services.AddDbContext<MtgEngineDbContext>(options =>
    options.UseNpgsql("Host=localhost;Database=mtgengine;Username=postgres;Password=password"));
```
3. Create new migration and update database

## Next Steps

To extend the feature:

1. **Add Deck Management**: Create Deck and DeckCard entities
2. **Add Card Search**: Implement filtering by name, mana cost, type
3. **Add Statistics**: Calculate mana curve, card types, format breakdown
4. **Add Authentication**: Replace DefaultUserId with JWT
5. **Add Validation**: Implement deck building validation (max 4 of a card, etc.)
6. **Add Bulk Operations**: Import multiple cards at once

## Troubleshooting

### Database Locked Error
```bash
# Kill lingering dotnet processes
Get-Process dotnet | Stop-Process -Force
```

### Migration Failed
```bash
# Recreate database
rm mtgengine.db
dotnet run
```

### Scryfall API Timeout
The ScryfallService has a 10-second timeout. If cards aren't loading:
- Check network connection
- Verify Scryfall API is responsive
- Check card OracleId is valid

### Port Already in Use
```bash
# Check what's using port 5000
netstat -ano | findstr :5000

# Or use different port
dotnet run --urls "https://localhost:7001"
```
