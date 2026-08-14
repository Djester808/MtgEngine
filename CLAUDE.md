# MtgEngine.Api — engineering standards

ASP.NET Core (.NET 10) API for the MTG deck builder. Angular client lives in the
sibling `mtg-client` repo. These are the rules that keep new code from
re-introducing the debt we deliberately cleaned up. When a rule below and the
surrounding code disagree, the rule wins — fix the code, don't copy it.

## Build / run gotchas (read first)

- **Stop the API before building or testing.** `MtgEngine.Api` holds file locks
  (the SQLite db, the built dll); `dotnet build`/`test` fail with locked-file
  errors while it runs. `Stop-Process -Name MtgEngine.Api -Force` first.
- API listens on `https://localhost:7001`. Dev client on `http://localhost:4200`.
- Tests are xUnit in `tests/MtgEngine.Api.Tests`. Run `dotnet test` from the
  solution root (after stopping the API).

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
