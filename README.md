# Compleat

Formerly MTGEngine. This is the active Compleat engine; its browser client is in the sibling `mtg-client` repository. Project paths, namespaces and database settings retain their existing names for compatibility.

ASP.NET Core (.NET 10) API behind the MTG deck builder. The Angular client lives in the
sibling `mtg-client` repo.

The engineering standards for this repo are in [CLAUDE.md](CLAUDE.md); read it before
changing anything here.

## Solution structure

```
MtgEngine.sln
├── MtgEngine.Domain/          # Models, enums, value objects. The leaf: no ASP.NET, no EF Core.
│   ├── Enums/Enums.cs
│   ├── Models/                # Card, Collection, User, ForumPost, LifeMatch, price snapshots
│   └── ValueObjects/          # ManaCost, CardPrices
│
├── MtgEngine.Api/             # Controllers, services, EF DbContext, DTOs, mapping
│   ├── Controllers/           # Auth, Cards, Collections, Decks, Commanders, Forum,
│   │                          # Matches, Profile, Rules, Users, Vision, Preferences, Admin
│   ├── Services/              # Collection/deck domain, Scryfall + bulk data, price history,
│   │                          # AI deck building, commander analysis, comprehensive rules
│   ├── Knowledge/             # Live runtime assets: commander doctrine, comprehensive rules
│   ├── Data/                  # MtgEngineDbContext (SQLite)
│   └── Migrations/
│
└── tests/MtgEngine.Api.Tests/ # xUnit, including the architecture tests that enforce layering
```

## Features

- **Collection and decks** — per-printing entries with foil/non-foil quantities across
  boards, deck import, price tracking and history. See `CARD_COLLECTION_FEATURE.md`.
- **AI deck building** — build, refine and explain Commander decks against the doctrine in
  `MtgEngine.Api/Knowledge/commander-doctrine.md`, which is injected verbatim into prompts.
- **Commander tools** — stats, suggestions, synergy scoring, EDHREC-backed pools.
- **Comprehensive Rules knowledge base** — `/api/rules` parses the published rules text at
  startup into sections, rules, keywords and glossary.
- **Profiles and forum** — public profiles (`USER_PROFILE_FEATURE.md`) and deck discussion.
- **Life counter** — the table at `/tools/life-counter` and the matches it files
  (`LIFE_COUNTER_FEATURE.md`).

## Running

The API holds file locks on the SQLite database and its own dll, so stop it before you
build or test:

```bash
Stop-Process -Name MtgEngine.Api -Force
```

Build, test, and run:

```bash
dotnet build
```

```bash
dotnet test
```

```bash
dotnet run --project MtgEngine.Api
```

The API listens on `https://localhost:7001`; the dev client runs on `http://localhost:4200`.

`CARD_COLLECTION_QUICKSTART.md` walks through exercising the API by hand.
