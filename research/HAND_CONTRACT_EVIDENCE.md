# Basic hand contract and evidence

Inspected 2026-10-25 with the reverse-engineer-anything skill, reusing retained
REA evidence and hash-verified reconstructed artifacts. No client or foreign
server was executed. Static client behavior, local design, and unknown server
behavior are distinguished below. No new REA MCP result is claimed: this session
has no REA tools, and retained reconstruction suffices for this bounded inquiry.

## Contract

A hand is an owner's hidden zone of existing card instances. `Zones.Hand` is a
read-only snapshot over `GameState.In(ZoneKey.Of(Hand, owner))`, exposing `Owner`,
`Key`, `Members`, `Count`, `Contains`, and predicate `Find`. The list's enumeration
order supports stable presentation, with no top/bottom semantics or authoritative
sorting API. It is server data, not a safe transport DTO. `ViewProjector` remains
the disclosure boundary: one's own hand is visible, other hands expose counts.
Control of an object alone grants no access to another owner's hand.

`LibraryDraw.Apply` and `ZoneSystem.Draw` remain the existing draw paths;
`ZoneSystem.Move` handles all other arrivals/departures. Wrong-owner destinations
route to the owner. Same-hand movement is a no-op, not a new game object or sort.
Cross-zone movement retains `CardInstanceId` and selected printing/finish while
allocating a new `ObjectId`. Hand entry clears face-down status and restores the
front face. Event serialization and replay remain authoritative. This is a local
design, not a recovered foreign class API.

## Concrete static evidence and method mapping

| Question | Inspected client implementation | Repository mapping / evidence limit |
| --- | --- | --- |
| Owner-specific membership | MTGO `GameCardZone` stores `CardZone` and `Player`, inherits collection/list interfaces, and classifies zone 1 as nonshared (`mtgo/decompiled/GameCardZone.cs:13-95`). `PlayerViewModel.ConfigureZones` assigns zone 1 to `m_handZone` (`PlayerViewModel.cs:1541-1546`). `Game.GetCardZone` validates player-zone lookup (`Game.cs:932-950`). | `Hand.Key` and `Members` use the existing owner zone. Foreign server wrong-owner routing is unknown; CR 400.3 supplies that requirement. |
| Count versus visible cards | MTGO `PlayerViewModel` assigns `HandTotal = GamePlayer.HandCount` (`1832`); `Game.cs:2075` receives separate hand/library/graveyard counts. Current Arena `MtgZone` stores `Owner`, `Visibility`, `CardIds`, `VisibleCards`; `TotalCardCount` reads `CardIds.Count` (`arena/decompiled/MtgZone.cs:7-25`). | `Hand.Count` derives from authoritative membership; viewer projection separately chooses data to disclose. Client fields alone do not establish foreign server redaction. |
| Entry/departure | MTGO `GameCard.Zone` returns for the same zone; otherwise removes from old collection, adds to new collection, then notifies (`GameCard.cs:671-703`). Retained Arena `ParseFullGameStateMessage` copies zone object IDs and owner/type/visibility (`GreInterface.cs:318-326`), then associates received objects with zones and visible-card collections (`516-540`). | Reuse event-producing `ZoneSystem`, not client observable collections. The same-hand no-op policy is consistent with the inspected setter; the setter does not prove a server movement algorithm. |
| Identity, printing and secrecy | Retained Arena assigns `InstanceId`, `GrpId`, separate owner/controller, visibility, and resolves printing (`GreInterface.cs:331-366`). Current `MtgCardInstance.UnknownCardData` constructs only instance/zone/owner/controller/card-back data (`388-398`). | Existing `GameObject`, `StartingCard`, `CardInstanceId` and `ObjectId` stay distinct. Owner projection exposes printing/finish; opponent views omit card objects. Unknown placeholders do not prove that foreign object IDs can safely be exposed by this engine. |
| Sorting versus membership | MTGO `ZoneViewModel.Add` optionally inserts presentation cards by timestamp; `Move` reorders `m_cards` and refuses sorted views (`ZoneViewModel.cs:406-441`). Inspected methods contain no authoritative movement request. | No library-like ordering contract, sort event, or duplicate hand store. Whether every hand layout uses this exact optional sort path is unknown. |
| Synchronization/replay | Retained Arena merges received game objects and zones by IDs (`GreInterface.cs:197-201`) and rebuilds visible membership from received state. | Existing `EventLogSerializer` / `GameReducer` own local replay. No exact foreign server event-log equivalence is claimed. |

## Public rules resolving gaps

Checked the official [rules page](https://magic.wizards.com/en/rules) and linked
[Comprehensive Rules effective September 25, 2026](https://media.wizards.com/2026/downloads/MagicCompRules%2020260925.txt).
CR 400.1–400.3 establish owner zones, hidden hands and owner routing. CR 402.1
covers drawing and other arrivals. CR 402.3 permits personal arrangement and
inspection, but only counting others' hands. CR 400.7 governs new objects after
zone changes; CR 712.8a establishes front-face characteristics in hand. Hidden
information is distinct from the face-down status of a permanent. CR 402.2 does
not limit the zone's storage capacity; cleanup enforcement remains outside this
abstraction. No casting, discard ability, mulligan, or hand-size system is added.

## Artifact identity and coverage

All seven files in `graveyard-contract-reconstruction-hashes.json` and current
`MtgCardInstance.cs` from `card-contract-reconstruction-hashes.json` were hashed
again and matched. `hand-contract-reconstruction-hashes.json` records this pass's
file identities. Proprietary reconstructed source remains in ignored directories.

The retained MTGO Play DLL was rehashed as
`7b16823fbaa9a965de3b9ee69ba727992a9b7b50d1187defadd9ac5a6c688081`
(retained build 3.4.159.4708; earlier member Evidence
`ev_5c508a6e3992121057eb96955fb197522fc3ed304c4de84eacaa75354286b265`).
The current installed Arena SharedClientCore DLL was rehashed as
`aceb0cd5004b322ceb59b28b0ad44ec799b3d47a46666b17a50150da5449e392`.
`MtgZone` and `MtgCardInstance` reconstructions belong to that newer artifact
(earlier recorded package declaration 0.1.14589.1332174).
Retained `GreInterface.cs` instead belongs to the earlier DLL hash
`42f2fb5f062ccc6098182a902d411746070759a404e875b6e0aac44d3b7ccc6e`,
member Evidence `ev_99270613b579e92d6cc431fa8d639036b41fbf27d7ab6d4000c4ef2ea6853b54`.
These are different builds; cross-file corroboration is not same-build proof.
Decompiler unresolved-type comments limit fidelity; claims here use concrete
assignments and method bodies. Foreign authoritative privacy, draw, and identity
lifetime algorithms remain unknown and are not needed for this implementation.

## Scope limits

The wrapper assumes valid authoritative state, as the graveyard wrapper does.
It does not repair malformed logs or authorize raw-state serialization. Existing
explicit look/reveal/choice paths retain their own permissions; generalized
hand-reveal effects and controlling another player are not implemented here.
Resetting the display face is not complete split/meld characteristic evaluation.
No additional UI controls or UI sorting were implemented; live UI is unverified.

## Implementation and verification

Added `MtgEngine.Rules/Zones/Hand.cs` and integrated its membership/count into
`ViewProjector`. Added hand-only same-zone refusal in `ZoneSystem.Proposal` and
hand front-face/face-down normalization in `Reborn`. The concurrent exile branch
in that method remains intact. No duplicate draw algorithm, catalog, store,
serialization format, public command, or card definition was added.

`HandContractTests` has 12 separately tagged `Scope=Hand` cases. Before the fixes,
five failed (three same-hand placements and two back-face returns) and seven
passed (`hand-focused-before-final.log`). After the fixes all 12 passed
(`hand-focused.log`). An earlier build caught and corrected a test analyzer issue;
temporary concurrent exile compilation errors also resolved before this baseline.
The initial sandboxed .NET invocation stalled and was canceled; tests completed
with approved execution outside the sandbox.

The unchanged Deck acceptance group separately passed **66 rules + 80 API**
(`hand-deck-regressions.log`). Full backend regressions passed **1,695 rules,
835 API, 326 Cards, and 322 pipeline** (`hand-broad.log`). That shared-checkout
snapshot includes concurrent exile/player work; its totals are not all hand
tests and do not certify complete Magic rules. Existing tests were preserved.
No client source changed, no live UI fix is claimed, and no commit or push was
performed. Run hand acceptance with `./tools/verify-test-scope.ps1 -Scope Hand`.
Touched-file `dotnet format --verify-no-changes` passes after normalizing new
C# line endings (`hand-format-final.log`, exit 0); `git diff --check` passes.
