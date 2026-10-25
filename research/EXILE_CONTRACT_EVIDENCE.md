# Basic exile contract and evidence

Inspected 2026-10-25 using the reverse-engineer-anything workflow and retained
MTGO/Arena artifacts. This is static client evidence, not observed gameplay or
foreign server code. No keywords, casting permissions, triggers or replacements
are implemented by this work.

## Contract and implementation

Exile is one shared zone containing objects with individual owners. It is not an
owner's graveyard and is not outside the game. `Zones.ExileZone` in `Exile.cs`
captures immutable `GameState`, exposing `Key`, `Members`, `Count`, `Contains`,
`Get` and `Find`. The name avoids collision with the existing `Cards.Exile` effect.
All membership comes from `ZoneKey.Shared(Exile)`. These are server-side accessors,
not viewer permissions; a predicate can inspect authoritative hidden data and
must not be exposed as an unrestricted player search. Enumeration follows storage
order for deterministic projection, without defining a top, bottom, shuffle or
reorder operation.

`ViewProjector.Project` now reads this snapshot's members. Face-up objects use
the existing public projection; face-down objects retain public membership and
ownership while withholding name, Oracle ID, selected printing/finish, text,
types and power/toughness from every seat, including their owner. Membership IDs
are current game object handles; physical `CardInstanceId` stays server-only.

`ZoneSystem.Move/MoveAll`, `Proposal`, `Reborn`, the existing events and reducer
remain the only movement/identity/replay path. Exile entry now resets the stored
display face to `FaceRules.FrontFace`, fixing the reproduced mismatch where the
computed name was front-face but projected Oracle text came from the back face.
Explicit face-down entry remains supported; ordinary entry is face up. Owner,
physical identity and printing/finish persist. Re-exiling an exiled object creates
a new object through the existing path; it must not inherit graveyard's no-op.

## Concrete client evidence and method mapping

| Responsibility | Observed static implementation | Local mapping / inference boundary |
| --- | --- | --- |
| Zone versus ownership | MTGO `GameCard.GetPlayerCardZone` maps `CARDZONE_RFG` to zone enum 4 with a player (`GameCard.cs:3163-3171`). `GameCardZone.IsSharedZone` classifies 4 as nonshared, but 5/6 as shared (`GameCardZone.cs:62-95`). `GameCard` remaps actual zone 4 to display zones 5/6 based on `ExileProperties` and local server player number (`GameCard.cs:1919-1921`). | This is a client presentation partition, not evidence that rules exile is player-owned. Our shared `ZoneKey` follows CR 400.1. Do not copy MTGO's view partition into authoritative storage. |
| Membership/count | MTGO `GameCardZone` inherits `BatchedNotifiedCollection<IGameCard>` and implements list/enumeration interfaces (`GameCardZone.cs:13`); the `GameCard.Zone` setter removes/adds collection membership (`GameCard.cs:671-700`). Current Arena `MtgZone` has `CardIds`, `VisibleCards`, `Type`, `Owner`, `Visibility`; `TotalCardCount` reads `CardIds.Count` (`MtgZone.cs:7-25`). | `ExileZone.Members/Count/Get/Contains/Find` provide equivalent basic responsibilities over existing state, without creating another mutable store. |
| Independent owner/controller | Retained Arena `GreInterface.ParseFullGameStateMessage` assigns `ControllerSeatId` and `OwnerSeatId` separately (`GreInterface.cs:337-338`); MTGO has separate getters (`GameCard.cs:1054,1066`). | Keep card ownership separate from membership and control. Existing engine controller bookkeeping outside battlefield/stack is not a claim that exiled cards have a rules controller. |
| Received identities and visibility | Arena parser builds zones from `ZoneInfo`, copies `ObjectInstanceIds`, and assigns visibility (`GreInterface.cs:318-326`), then populates instance/printing identifiers and object visibility (`330-343`). | Reuse authoritative state and viewer projection. Received visibility is not proof of the foreign server's permission algorithm. |
| Face-down display | MTGO `GetCardDefinitionForNewGameCard` falls back to a generic face-down definition when `FACE_DOWN` or `REMOVED_FROM_GAME_BY_ID` is supplied without earlier usable texture data (`GameCard.cs:2782-2847`). Current Arena `MtgCardInstance.UnknownCardData` supplies instance/zone/owner/controller and a standard card back (`MtgCardInstance.cs:388-398`, inspected in prior card report). | A placeholder is not permission to inspect. Default-deny projection stays intact. Neither client establishes persistent look permissions or safe selection rules on our server. |
| State synchronization | Arena parses zone object IDs and subsequently associates reconstructed objects with zones (`GreInterface.cs:516-540`); `GetZoneChanges` compares membership and anonymity (`1823-1940`). MTGO membership changes follow incoming state. | Existing event serialization/replay is our implementation choice, not a transplanted foreign server algorithm. Tests verify our membership, redaction and identity after round-trip serialization. |

## Rules resolving client gaps

The official [rules page](https://magic.wizards.com/en/rules) linked the
[Comprehensive Rules effective September 25, 2026](https://media.wizards.com/2026/downloads/MagicCompRules%2020260925.txt)
when checked on 2026-10-25. CR 400.1 establishes shared exile; 400.11 distinguishes
it from outside the game. CR 406.2–406.3 establish entry, public face-up inspection
and explicit permission for face-down inspection. CR 406.3a gives face-down exile
no characteristics. CR 406.7 requires a new object when already-exiled objects
are exiled again. CR 712.8a supplies front-face characteristics outside
battlefield/stack. These semantics are rules-backed, not recovered server methods.

## Provenance and verification boundary

Retained MTGO build 3.4.159.4708: Play assembly SHA-256
`7b16823fbaa9a965de3b9ee69ba727992a9b7b50d1187defadd9ac5a6c688081`, rehashed in
this pass. Retained REA member Evidence:
`ev_5c508a6e3992121057eb96955fb197522fc3ed304c4de84eacaa75354286b265`.

Retained Arena `GreInterface.cs` is associated with earlier SharedClientCore hash
`42f2fb5f062ccc6098182a902d411746070759a404e875b6e0aac44d3b7ccc6e`, Evidence
`ev_99270613b579e92d6cc431fa8d639036b41fbf27d7ab6d4000c4ef2ea6853b54`.
Current installed SharedClientCore rehashes to
`aceb0cd5004b322ceb59b28b0ad44ec799b3d47a46666b17a50150da5449e392`;
`MtgZone.cs` is the reconstruction from that newer artifact, package declaration
0.1.14589.1332174. The two builds are not interchangeable.

`exile-contract-reconstruction-hashes.json` records inspected file identities.
Rehashed `GameCard.cs`, `Game.cs`, `GameCardZone.cs`, `GreInterface.cs` and
`MtgZone.cs` match the prior graveyard manifest. Proprietary reconstructions stay
in the existing ignored directories. No new decompilation was needed. The active
session exposes no REA tools; an attempted npm doctor lookup failed DNS, so retained
REA results and direct static file inspection provide the evidence here. A cached
REA 6.7.0 CLI doctor subsequently ran and reported missing/drifted registration;
its live server state is unknown (`exile-rea-doctor-local.log`). No configuration
was changed; the retained evidence suffices for this static comparison. No new
REA Evidence IDs or observed runtime behavior are claimed.

## Explicit limitations

Default face-down secrecy is implemented. `CardsLookedAt`/`LastLooks` provide an
existing recipient-only historical disclosure and survive replay; they deliberately
carry no object identity and do not authorize ongoing inspection. Persistent
per-object look permission (including look-before-exile continuity), grouping
face-down cards by exiling cause/time, and permission-aware/random selection among
those piles are not represented by this snapshot. CR 406.3–406.5 therefore remain
partially supported. This task adds no automatic owner permission or public
arbitrary rearrangement. Linked return permissions and cast/play permissions
remain outside this abstraction; no card text is executed here.

Exact foreign server movement, permission and replay algorithms remain unknown.
The minimal local snapshot and tested default privacy are complete within the
stated boundary, not a claim of full exile rules or full Magic implementation.

## Tests

`ExileContractTests`, tagged `Scope=Exile`, separately cover empty/shared storage,
owners, lookup, all-seat public inspection, face-up/down entry, face-down secrecy,
recipient-only historical look, re-exile identity, departure and serialized replay.
`exile-focused-reproduction.log` records two failing front-face cases before the
movement correction (five other cases passed). Verification counts follow below.

- Final focused run: 8 passed (`exile-focused-final.log`), including the additional
  explicit private-disclosure case.
- Unchanged card/deck acceptance: 66 rules and 80 API passed
  (`exile-deck-rules.log`, `exile-deck-api.log`).
- Broad shared-checkout checkpoint: 1,695 rules, 835 API, 326 Cards and 322 pipeline
  passed (`exile-broad.log`). Concurrent hand/player files are included; the broad
  checkpoint preceded the final focused rebuild. These are not exile test counts.
- Touched-file whitespace verification passes (`exile-format-final.log`).
  Only the new exile files had line endings normalized; no repository-wide format
  rewrite was performed. No existing assertions were weakened.

No client source changed, and no UI behavior/live verification is claimed. No
commit, staging or push was performed. Other chats' hand/player changes remain
in the checkout; this report attributes only the exile snapshot, exile projection
branch, exile entry face reset, focused tests, scope selector and evidence files.

### Push verification — 2026-10-25

The user subsequently authorized commit/push. Hand and player work was committed
separately by its owning chats before staging this exile-only commit. Final build
passes with zero warnings/errors (`exile-push-build.log`). The final full solution
run passes 1,696 rules, 835 API, 326 Cards and 322 pipeline tests
(`exile-push-tests.log`); the eight exile cases remain the focused acceptance count.
