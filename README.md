# Compleat

Formerly MTGEngine. This is the Compleat API and game-model foundation; its browser client is in the sibling `mtg-client` repository. Project paths, namespaces and database settings retain their existing names for compatibility.

ASP.NET Core (.NET 10) API behind the MTG deck builder. The Angular client lives in the
sibling `mtg-client` repo.

The engineering standards for this repo are in [CLAUDE.md](CLAUDE.md); read it before
changing anything here.

## Game foundation status

The legacy rules executor was removed on 2026-10-25. `MtgEngine.Rules` now contains
game models, read-only snapshots, and bounded state/zone operations. `MtgEngine.Cards` contains printed
card data and catalog mapping, without executable abilities or keywords. The old
card pipeline, compiled corpus and game session/hub endpoints are removed.

No games can be executed through this API yet. The browser client in the sibling
repository has not been changed; its old play screens require a future rebuild.
Collection, deck, account, forum, rules-reference and life-counter features remain.
Existing database migrations and historical game storage models are retained to
avoid destroying saved data; they are not a live rules engine.

See [the foundation checkpoint](research/FOUNDATION_INTEGRATION.md) for what remains.

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
├── MtgEngine.Cards/           # Printed card/face data and catalog mapping only
├── MtgEngine.Rules/           # State/zone primitives, identity, turn models and viewer snapshots
└── tests/                    # API, printed-card and foundation-model tests
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
