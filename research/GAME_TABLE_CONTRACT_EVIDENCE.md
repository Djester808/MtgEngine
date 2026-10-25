> Historical evidence from the pre-reset implementation. See [the current models-only checkpoint](FOUNDATION_INTEGRATION.md); execution and replay claims below no longer describe the active code.

# Basic game/table contract and evidence

Inspected 2026-10-25. This report follows the reverse-engineer-anything workflow:
retained artifact inspection and hash verification, concrete method mapping, and
separation of static client observations from local design and unknown servers.
No client was launched and no foreign server implementation was recovered.

## Contract and composition

`GameSetup` supplies ordered `PlayerStart` entries, seed, descriptive format,
starting life, starting seat, opening-hand size and mulligan choice. `Game.Create`
composes the existing zone and turn services and settle loop; production
`Subsystems.Wave2` remains intact. `ZoneSystem.Create` emits `GameStarted`, creates
individual objects from existing `StartingCard` selections and definitions, then
uses existing shuffle/draw services. This task adds no format or game mechanics.
The API admits two to four distinct accounts; the rules layer still supports
one-player synthetic scenarios and does not duplicate the API size policy.

`GameState` is the authoritative immutable snapshot: indexed players, object map,
ordered zone memberships, format and existing allocation counters. Missing zone
keys read as empty through `In`; no eagerly populated parallel zone store is
required. `ObjectCreated.Place` advances existing object/timestamp counters.
Object identity, physical card identity, definition identity and optional printing
selection remain separate. Copies/tokens and turn internals retain their owners.

`ViewProjector.Project` builds a fresh viewer-specific `GameView`; hidden data is
not sent by serializing `GameState`. `TableView.From` consumes only that projection,
rotates presentation around the viewer and keeps stable roster seat indices.
Its board/exile/stack lists reference projected object IDs. It is neither a zone
store nor a second game. Existing table tests separately cover controller-based
battlefield placement versus owner-based exile and top-first stack presentation.

`GameSession` owns the existing mutable state/log pair and account seat mapping.
`GameSessionService` stores the event log plus session metadata, and reloads with
`EventLogSerializer.Read` and `GameReducer.Replay`. Account IDs do not become
`PlayerId`s until `SeatOf` resolves their stable roster index. Admission coverage
is stored on `GameStarted`; serialized object definitions/selections preserve the
game's already-admitted cards. `GameHub` authenticates reconnecting users and
projects their seat; a table GUID alone does not authorize a viewer.

## Demonstrated gaps corrected

Seven focused cases failed before the changes (`game-table-before-verified.log`):
empty replay, events before startup, repeated startup silently resetting a game,
three missing/blank format labels, and negative opening-hand size silently doing
nothing. `GameReducer.Replay` now requires exactly one initial `GameStarted`;
`GameStarted.Apply` requires a nonblank format label; `ZoneSystem.Create` rejects
negative hand size before constructing events. Incremental `GameReducer.Apply`
still accepts an empty event batch. Format labels are not a new whitelist and
stored life is not restricted by a new game-rule policy.

These are local composition and persistence invariants, not recovered foreign
server algorithms. Valid wire formats are unchanged; no properties, discriminators
or persisted derived data were added. Malformed historical logs with these shapes
now fail explicitly. This is not a universal validator for every hand-constructed
state or event: direct event application remains the existing internal primitive.

## Evidence and method mapping

| Concern | Concrete static evidence | Local mapping and limitation |
| --- | --- | --- |
| Stable roster versus screen order | MTGO `Game.cs:76-82` keeps user lookup, `m_playersInServerOrder` and displayed `m_players` separately. `HandleReplayPlayerOrder` at 1926 creates players and their zones then calls `OrderPlayers`; `OrderPlayers` at 1938 rebuilds server order separately from display order. `GetServerPlayerNumber` at 927 reads the server-order index. | `PlayerState`/`PlayerId` and session account seats remain stable; `TableView` rotates references only. MTGO's specific multiplayer layout is not copied. |
| Zone composition | MTGO `CreatePlayerZones` at 1286 creates per-player collections; `GetCardZone` overloads at 932 and 953 validate shared versus owned lookup. | Reuse `ZoneKey`, `GameState.In`, and existing zone abstractions; UI collections do not justify another mutable store. |
| State identity and synchronization | Retained Arena `GreInterface.cs:197-201` merges objects by `InstanceId`, zones by `ZoneId`, players by `SystemSeatNumber`. `ParseFullGameStateMessage` at 269-338 creates players, sorts by seat ID, copies zone object IDs, and resolves owner/controller separately. | Replay preserves existing identities and authoritative membership. Our allocator and event fold are repository design, not an Arena server implementation. |
| Visibility and presentation | Retained Arena assigns zone visibility at 325 and object visibility at 343, while separately determining local-player/team presentation at 273-275. | A viewer projection is distinct from stored state. These client fields do not establish server authorization or privacy enforcement; local tests and hub checks provide that evidence. |
| Restart | MTGO replay-player ordering and Arena identity-keyed merge preserve explicit identities rather than names. | Existing log serialization/reducer and session reload are reused. Foreign server persistence, transport guarantees and reconnect protocols remain unknown. |

The actual retained files `research/mtgo/decompiled/Game.cs` and
`research/arena/decompiled/GreInterface.cs` were read for these claims. Unresolved
decompiler types remain limitations; claims use visible assignments, collection
operations and method calls. No exact server parity is claimed.

## Public rules and scope

The official [rules page](https://magic.wizards.com/en/rules) linked the
[September 25, 2026 Comprehensive Rules](https://media.wizards.com/2026/downloads/MagicCompRules%2020260925.txt)
when checked on 2026-10-25. CR 100.1 distinguishes two-player and multiplayer games;
102.1 defines participants; 103 establishes starting-player/setup responsibilities;
400.1-400.2 distinguish owned/shared and hidden/public zones. These support the
composition boundaries. They do not prescribe our JSON log, authentication,
two-to-four-player product limit or data-validation exception types.

No account system, lobby, format mechanics, win/loss behavior, keywords, casting,
combat, replacement, triggers or named card definitions were added. The test-only
minimal definition is a synthetic fixture. No client source changed, no live UI
verification was performed, and no UI fix is claimed.

## Provenance

`game-table-contract-reconstruction-hashes.json` records freshly computed SHA-256
and size for the inspected files and binaries. The files match the prior player
manifest. MTGO gameplay build 3.4.159.4708 has SHA-256
`7b16823fbaa9a965de3b9ee69ba727992a9b7b50d1187defadd9ac5a6c688081`, retained REA
Evidence `ev_5c508a6e3992121057eb96955fb197522fc3ed304c4de84eacaa75354286b265`.
`Game.cs` hashes to `17c0bb8cefc655603587f6b6c2e871c6b6ddacef5783a6456d75808cd642a034`.

Retained Arena `GreInterface.cs` hashes to
`9caad39b83478dbcc34a9a8825b0ee09ecc049b743610ccfb10082e9104879ba` and belongs to
older SharedClientCore `42f2fb5f062ccc6098182a902d411746070759a404e875b6e0aac44d3b7ccc6e`,
Evidence `ev_99270613b579e92d6cc431fa8d639036b41fbf27d7ab6d4000c4ef2ea6853b54`.
The installed assembly is now
`aceb0cd5004b322ceb59b28b0ad44ec799b3d47a46666b17a50150da5449e392` (previously
recorded package 0.1.14589.1332174); old method bodies are not attributed to it.
No REA tools were connected. Read-only doctor acquisition failed with npm DNS
resolution (`game-table-rea-doctor.log`); no configuration was changed. This pass
uses retained evidence and ordinary file/hash inspection, with no new REA IDs.

## Verification

`GameTableContractTests` is independently selectable with `Scope=GameTable`.
Its 11 cases cover the seven regressions above, unchanged empty incremental fold,
and production creation/reload for two, three and four seats. The latter check
unique physical identity, allocator continuation, exactly-one zone membership,
printing/finish/owner preservation, serialized per-viewer equality, stable seats,
viewer-relative order, hand ownership and absence of physical card IDs in views.
Existing API `GameHubTests.TableCreation` and restart/reconnect tests cover account
mapping and authentication separately.

Focused run `game-table-focused.log`: 11 passed. Full backend run
`game-table-broad.log`: 1,776 rules, 835 API, 326 Cards and 322 pipeline tests
passed. Those broad counts include concurrent foundation changes at that build
checkpoint, not just this task's tests. Separate unchanged card/deck acceptance
passed 66 rules (`game-table-deck-rules.log`) and 80 API
(`game-table-deck-api.log`). These are bounded regression results, not evidence
of complete Magic rules implementation. Initial build collisions from concurrent
edits/output locks were retried without changing the other owners' files.

Touched-file whitespace verification uses `game-table-format.log`; the format
correction was limited to `GameEvent.cs` and this task's test file. The sole
`ZoneSystem.cs` edit is the opening-hand-size validation in `Create`; concurrent
zone edits were preserved. No shared test-scope documentation/tool was edited,
and no staging, commit or push was performed.
