> Historical evidence from the pre-reset implementation. See [the current models-only checkpoint](FOUNDATION_INTEGRATION.md); execution and replay claims below no longer describe the active code.

# Basic player contract and evidence

Inspected 2026-10-25 using the reverse-engineer-anything workflow. This is static
client reconstruction and repository evidence, not observed gameplay or recovered
server behavior. No account, lobby, priority, life/counter mechanic, or loss/win
implementation is added.

## Contract and implementation

A player is a participant in one game. `PlayerId` identifies its zero-based seat
in that game's `GameState.Players`; it is not an account ID or card identity.
`PlayerState` remains the single immutable record of stored player data. Names
are descriptive and may repeat. `PlayerStart` carries initial name and selected
cards; `GameStarted` assigns seats in roster order and initializes existing data.
Account authentication and account-to-seat mapping stay in the API session.

`PlayerState.Create` validates nonnegative identity and a nonblank name, preserving
the supplied name and life value. `GameStarted.Apply` uses it and validates that
the starting seat exists, including on replay. `GameState.Player` checks bounds
and rejects a stored identity that disagrees with its list position. This protects
the indexed event-update contract rather than introducing an identity search that
would conceal malformed state. No seat compaction or renumbering is introduced.

`PlayerState.OwnedZone` supplies keys for library, hand and graveyard only. Callers
resolve the current participant with `state.Player(id)`, then read membership
with `state.In(player.OwnedZone(zone))`. Shared zones, including command and exile,
are rejected. Keys confer no visibility or action permissions. Zone-specific
abstractions and `ZoneSystem` remain responsible for their existing operations;
`ViewProjector` remains the transport privacy boundary.

## Evidence and method mapping

| Concern | Observed static evidence | Local mapping / inference |
| --- | --- | --- |
| Account versus participant | MTGO `Game.cs:76-82` separates user lookup, server ordering and displayed players. `HandleReplayPlayerOrder` at 1926 creates `GamePlayer` from ID/name, creates zones and calls `OrderPlayers`; `OrderPlayers` at 1938 maps user IDs to `PlayersInServerOrder` while separately arranging display order. `GetGamePlayerForUser` at 977 resolves a user to a game player. | Reuse `GameSession.SeatOf`, persisted session `Seats`, and `PlayerId`; do not use display names as identity or import client display ordering as authoritative seats. |
| Initial versus stored data | Retained Arena `GreInterface.ParseFullGameStateMessage`, lines 269-317, creates `MtgPlayer` from received players, assigns `InstanceId` from `SystemSeatNumber`, separately assigns `StartingLifeTotal`, `LifeTotal`, `MaxHandSize`, mana and status, and sorts by instance ID. Merge at line 199 keys players by system seat number. | `PlayerStart`/`GameStarted` versus `PlayerState`. Existing events update life, counters and status. Our zero-based contiguous IDs and factory validation are design choices, not Arena protocol equivalence. |
| Owned-zone lookup | MTGO `Game.GetCardZone` at 932-950 distinguishes player zones from shared zones; `CreatePlayerZones` at 1286 creates per-player collections. Arena lines 318-326 assign zone owner using `GetPlayerById(zone.OwnerSeatId)` and copy received membership. | Supply a key only; keep `GameState.Zones` authoritative. No second player-owned card store. |
| Owner/controller/viewer | Arena lines 337-338 assign card controller and owner independently; lines 490-496 distinguish received viewers from ownership. Player controller assignment is independently present at 297-298. | Retain `GameObject.Owner`, computed controller/`InitialController`, and explicit projection viewer. A controller reference does not authorize reading another player's hand. This task does not implement controlling another player. |
| Reload | MTGO replay player order and Arena merge use stable explicit identities rather than names. | Use existing `EventLogSerializer` and `GameReducer`; no new serialized fields or competing replay mechanism. Exact foreign server persistence remains unknown. |

The concrete MTGO methods were read from `research/mtgo/decompiled/Game.cs`;
Arena methods from `research/arena/decompiled/GreInterface.cs`. These are actual
retained reconstructed artifacts, not conclusions drawn solely from report titles.
Decompiler unresolved-type comments remain a limitation; claims use clear field
assignments, collection operations and cross-method calls.

## Rules and existing application boundaries

The official [rules page](https://magic.wizards.com/en/rules) linked the
[September 25, 2026 Comprehensive Rules](https://media.wizards.com/2026/downloads/MagicCompRules%2020260925.txt)
when checked on 2026-10-25. CR 102.1 establishes the participant meaning; 108.3
separates in-game card ownership from legal ownership; 400.1 assigns each player's
library, hand and graveyard; 400.2 establishes the hidden/public distinction.
These support the boundaries, not account authentication or our storage format.

`GameSessionService.CreateCoreAsync` already admits two to four distinct signed-in
accounts with nonblank names. `GameHub.Me` reads the authenticated subject and
`Seat` resolves it with `GameSession.SeatOf`; client input does not select the
acting account. `GameSessionService` persists account seats separately from the
rules event log. Existing `GameHubTests.TableCreation` verifies all supported table
sizes, restart seat mapping, and exclusion of an uninvited account. Those assertions
remain unchanged. The rules layer continues to permit a one-seat synthetic
scenario; it does not duplicate the API's table-size policy.

## Provenance and limits

`player-contract-reconstruction-hashes.json` records freshly calculated SHA-256
and size for the inspected reconstruction files, retained MTGO gameplay assembly,
and current installed Arena SharedClientCore. MTGO build 3.4.159.4708 gameplay hash
is `7b16823fbaa9a965de3b9ee69ba727992a9b7b50d1187defadd9ac5a6c688081`, associated
with retained REA Evidence `ev_5c508a6e3992121057eb96955fb197522fc3ed304c4de84eacaa75354286b265`.
Game.cs matches the prior graveyard manifest hash
`17c0bb8cefc655603587f6b6c2e871c6b6ddacef5783a6456d75808cd642a034`.

Arena GreInterface.cs matches retained reconstruction hash
`9caad39b83478dbcc34a9a8825b0ee09ecc049b743610ccfb10082e9104879ba` and belongs
to earlier assembly hash `42f2fb5f062ccc6098182a902d411746070759a404e875b6e0aac44d3b7ccc6e`,
Evidence `ev_99270613b579e92d6cc431fa8d639036b41fbf27d7ab6d4000c4ef2ea6853b54`.
The installed assembly now hashes to
`aceb0cd5004b322ceb59b28b0ad44ec799b3d47a46666b17a50150da5449e392` (previous
investigation recorded package 0.1.14589.1332174). This pass does not attribute
the old method bodies to that current build. No new REA Evidence was generated:
REA tools were unavailable in the session and the read-only doctor acquisition
failed with npm DNS/network resolution. Retained evidence and ordinary file/hash
inspection were sufficient; no configuration was changed or target executed.

The public record constructor and `with` remain available for existing reducers,
tests and serialization; this is validation at startup/access, not a universal
validator for arbitrary externally constructed state. Negative stored life is
valid data, not rejected by the factory. Names are not normalized. No new account
data enters player views. Shared-zone access, controller computation and card
printing/finish remain in their established owners. Malformed historical startup
logs with blank names or invalid starting seats now fail validation rather than
silently constructing invalid players; valid old wire formats are unchanged.

## Verification

`PlayerContractTests` has `Scope=Player`, runnable directly with
`dotnet test tests/MtgEngine.Rules.Tests --filter Scope=Player`.
Six regression cases failed before implementation (`player-before-verified.log`).
All 20 focused cases pass (`player-focused.log`), covering startup validation,
seat consistency, owned-zone keys and snapshot membership, shared-zone refusal,
duplicate names, stored data/event replay, record serialization, and hand privacy
despite a distinct controller. Existing account-to-seat API tests provide the
separate authentication/restart coverage. Broader results are recorded below;
none is a claim of complete Magic rules implementation. No client source changed
and no live UI behavior is claimed.

Full backend run `player-broad.log`: 1,695 rules, 835 API, 326 Cards and 322
pipeline tests passed. The rules count includes the concurrently added hand and
exile tests, not just this player's 20 cases. Separate unchanged card/deck
acceptance `player-deck-regressions.log`: 66 rules and 80 API tests passed.
Touched-file formatting is checked by `player-format.log`; `git diff --check`
passes. The test-scope helper is concurrently owned
by the zone tasks; the explicit `Scope=Player` command above selects these cases
independently of that helper's named options.
