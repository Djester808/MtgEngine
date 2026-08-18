# MtgEngine.Api — engineering standards

ASP.NET Core (.NET 10) API for the MTG deck builder. Angular client lives in the
sibling `mtg-client` repo. These are the rules that keep new code from
re-introducing the debt we deliberately cleaned up. When a rule below and the
surrounding code disagree, the rule wins — fix the code, don't copy it.

## Verification — never report a UI fix you have not looked at

**A passing unit test is not evidence that the screen is fixed.** A green suite proves the
function you wrote does what you think; it says nothing about whether the component is
wired up, whether the branch you changed is the one that runs, or whether the user is
even on the page you edited. Every one of those has been the actual bug here.

Before telling the user a user-visible defect is fixed:

1. **Open the app and look at the thing.** Dev client is `http://localhost:4200`, API on
   `https://localhost:7001`. There is a Selenium harness in `mtg-client/e2e`. Drive it to
   the exact control — open the dropdown, scroll the list, click the toggle — and look.
2. **Reproduce the failure first.** If you cannot make it fail, you do not know what it
   is, and you must say so instead of shipping a plausible change.
3. **If you cannot verify, say the word "unverified" in the report**, and say what you'd
   need to verify it. Do not describe a speculative change as a fix.

Guessing repeatedly and reporting each guess as a fix wastes the user's money and their
time re-testing. Three separate rounds were burned that way on the collection grouping
picker. One screenshot of the open dropdown would have settled it on round one — ask for
the specific artifact you need, or go get it yourself.

Reason from the code to form a hypothesis. Confirm it against the running app before it
becomes a claim.

## Mobile/app is the baseline for anything that reaches a screen

The client is designed narrow-first and judged at 375 × 667 before any wider size, and it
is headed for a packaged build (PWA/Capacitor). That constraint travels with API work:
default page sizes small enough to render on a phone, payloads that do not assume a wide
table, and ProblemDetails `Detail` strings short enough to read inside a sheet.

The layout standard itself lives in **`mtg-client/CLAUDE.md` § "Mobile is the baseline"** —
read it before touching anything under `mtg-client/src/` (the docs hook enforces that
anyway). It is the source of truth for breakpoints, the shared style vocabularies, the
reflow-vs-pan rule, and the capture harness that any phone-layout claim has to come from.

## Build / run gotchas (read first)

- **Stop the API before building or testing.** `MtgEngine.Api` holds file locks
  (the SQLite db, the built dll); `dotnet build`/`test` fail with locked-file
  errors while it runs. `Stop-Process -Name MtgEngine.Api -Force` first.
- API listens on `https://localhost:7001`. Dev client on `http://localhost:4200`.
- Tests are xUnit in `tests/MtgEngine.Api.Tests`. Run `dotnet test` from the
  solution root (after stopping the API).

## Knowledge docs — read the relevant one BEFORE you write code

This file is loaded automatically every session; the documents below are not. Open the
one that covers your area first, and treat it as authoritative over surrounding code.

**This is enforced, not requested.** `.claude/hooks/require-docs.js` runs as a
`PreToolUse` hook on `Edit`/`Write`/`MultiEdit`/`NotebookEdit` and **denies** the call
when the file is governed by a doc that has no `Read` for it in the current session's
transcript. The instruction below had been in this file for a while and was still being
skipped — an instruction the assistant is asked to follow is not a control, so the docs
got a gate like everything else here (build gate, arch tests, CI). The check reads the
transcript, so it cannot be satisfied by asserting you read something. Edit the rule
table in that script when a doc starts governing new files; if it fires, read the doc —
do not route around it by editing a different file.

**The client's standards are gated the same way.** `mtg-client/CLAUDE.md` is *not*
auto-loaded when you work from this repo — only this file is — so anything under
`mtg-client/src/` requires reading it first. That gap is not hypothetical: a session
wrote ~200 lines of client code without it and duplicated `card-search-base.ts`'s entire
filter vocabulary, which that doc explicitly forbids. Reading this file does not satisfy
that rule; the hook matches on the trailing path, so the two CLAUDE.md files are
distinct.

- **`MtgEngine.Api/Knowledge/commander-doctrine.md`** — the deck-building standard every
  AI pass reasons from (suggestions, synergy scoring, reason writing, deck building).
  **It is a live runtime asset**: `CommanderDoctrine.cs` loads it and it is injected
  verbatim into prompts, so editing it changes the app's judgement with no C# change.
  Read it before touching `AiBuildService`, `SynergyService`, `DeckSuggestionsService`,
  `CandidateRanking`, or any prompt text. Never duplicate it into another repo — a
  second copy silently drifts and the two halves start disagreeing.
- **`MtgEngine.Api/Knowledge/comprehensive-rules.txt`** — the Magic Comprehensive Rules
  exactly as Wizards publishes them, and **a live runtime asset**: `ComprehensiveRules.cs`
  parses it at startup into the sections, rules, keywords and glossary that `/api/rules`
  and the `/kb` page serve. **Do not hand-edit it.** Moving to a new rules release is a
  file swap: download the current text from Magic.Wizards.com/Rules and replace the file.
  The parser throws on any line it does not recognise, so a format change fails at
  startup rather than serving a partial rulebook — if that happens, fix
  `ComprehensiveRulesParser`, never the document. Nothing about this knowledge base
  describes an implementation: it had entries badged "implemented"/"partial"/"stub" and
  mechanics documenting C# types back when it was a window onto a rules engine, and that
  engine is gone. Keep it a reference to the game's rules.
- **`CARD_COLLECTION_FEATURE.md`** — collection/deck domain: models, endpoints, price
  tracking. Read before changing `CollectionService`, `CollectionCard`, or the
  collection/price endpoints, and **update it in the same commit** when you add surface.
- **`USER_PROFILE_FEATURE.md`** — accounts and profiles: the public/private line, the
  avatar upload gate, and how each derived stat is computed. Read before changing
  `ProfileService`, `ProfileController`, `UsersController` or `AvatarImage`. The rule it
  exists to protect: **counts are public, money is not** — a public profile never carries
  what a collection is worth, and the DTO has nowhere to put it.
- **`CARD_COLLECTION_QUICKSTART.md`** — how to exercise the API by hand.

⚠️ The two `CARD_COLLECTION_*` docs predate later work and have drifted: they still
describe a boolean `IsFoil` (now `Quantity`/`QuantityFoil`), a unique index on
`(CollectionId, OracleId)` (now `(CollectionId, ScryfallId, Board)`), a hardcoded
`DefaultUserId` (auth now deny-by-default JWT), and port 5000 (now `https://localhost:7001`).
Trust the code for those specifics; fix the doc when you touch the area.

## Layering

- `MtgEngine.Domain` — models, enums, value objects, interfaces. **No ASP.NET,
  no EF Core, no dependency on `MtgEngine.Api`.** It is the leaf.
- `MtgEngine.Api` — controllers, services, EF `AppDbContext`, DTOs, mapping.
- **Controllers stay thin:** validate/bind, call one service, return the result.
  No business logic in controllers. Prefer routing data access through a service;
  a few legacy read-only controllers (Auth, Preferences, Users) still query the
  `DbContext` directly — don't add new ones. Arch tests keep the Mapping/DTO
  layers clean of it.
- Services return DTOs, never tracked entities, to callers/controllers.

## Errors — never a bare 500, never echo internals

- Services throw **typed domain exceptions**; `AiExceptionHandler`
  (`Services/AiExceptionHandler.cs`) maps them to ProblemDetails (RFC 7807) in
  one place. Do not add per-endpoint try/catch that formats errors — extend the
  handler instead.
  - `ResourceNotFoundException` → 404
  - `InvalidResourceStateException` → 409
  - `AiUpstreamException` → 502 · `TaskCanceledException`/`TimeoutException` → 504
  - `ConfigurationException` → 503
  - anything else → framework 500 (a bug — add a mapping or fix the throw site)
- **Never return an exception's raw `Message` to the client** when it can contain
  request content, upstream response bodies, API keys, or config paths. Log the
  detail; return a fixed, safe `Detail` string (see the AiUpstream/Configuration
  arms for the pattern).

## Request validation

- Every request DTO validates its inputs with **DataAnnotations**
  (`[Required]`, `[StringLength]`, `[Range]`, `[MaxLength]`). MVC runs these
  before the service does; EF `HasMaxLength` does **not** protect the service.
- On **positional records**, attributes target the constructor parameter
  directly — `[Range(0, 9999)] int Quantity`, **not** `[property: Range(...)]`.
  The `property:` form throws at model-bind time (500 on every call).
- Keep DTO caps aligned with their sibling requests (e.g. add and update of the
  same field share the same `[Range]`/`[StringLength]`).

## User-input safety

- **Regex built from user input needs a match timeout.** Compile once with
  `new Regex(pattern, opts, TimeSpan.FromMilliseconds(50))`; catch
  `RegexParseException`/`ArgumentException` at compile and
  `RegexMatchTimeoutException` at match time (treat as no-match). Never run an
  un-timed regex over a user pattern (ReDoS). See `Services/CardQuery.cs`.
- **Cache keys must exclude attacker-controlled free text.** Key on stable ids
  (oracleId), then re-derive the trusted fields from your own store — do not
  trust a caller-supplied name/text as part of a cache key or a prompt (prompt
  injection). See `DeckSuggestionsService`.
- Cap page/window sizes server-side (`CandidateRanking` clamps to
  `MaxScoreWindow`); never let a caller request an unbounded scan.

## Cancellation & long work

- Thread `CancellationToken` through AI calls, DB queries, and any loop that can
  run long; call `ct.ThrowIfCancellationRequested()` before each expensive stage.
- Per-resource critical sections use a keyed `SemaphoreSlim` from a `static
  ConcurrentDictionary` (see `AiBuildService` deck locks) — not a single global
  lock.
- Bulk mutations use `ExecuteUpdateAsync`/`ExecuteDeleteAsync`, and delete
  dependent rows (forum posts/comments on deck delete) so nothing is orphaned.

## Caching & correctness

- Versioned caches carry a `ModelVersion`/`CacheVersion` in the lookup key, so a
  bumped version invalidates stale rows instead of serving them.
- Distinguish "empty but cacheable" (upstream 404) from "transient failure"
  (`EnsureSuccessStatusCode` → throw, return uncached) — don't cache a transient
  error as an empty result. See `EdhrecPoolService`.
- Count across **all** relevant rows/boards, not just `main` (e.g. card totals
  sum `Quantity + QuantityFoil` across boards).
- Background maintenance goes in a `BackgroundService` registered in
  `Program.cs` (see `CacheCleanupWorker`), using `IServiceScopeFactory` for
  scoped services.

## Before you commit

`dotnet format --verify-no-changes` and `dotnet build` must be clean (analyzers
and code-style are enforced in-build — warnings fail). `dotnet test` green.
Architecture tests in `tests/MtgEngine.Api.Tests` encode the layering rules
above; if one fails, the design drifted — fix the code, not the test.
