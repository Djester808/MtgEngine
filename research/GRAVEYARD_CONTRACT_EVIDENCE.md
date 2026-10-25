# Basic graveyard contract and evidence

Inspected 2026-10-25 using the reverse-engineer-anything workflow: reuse retained evidence, follow concrete reconstructed methods, distinguish static observations from inference, and use public rules for semantics absent from client code. No client was launched; no server implementation was inspected. No graveyard ability, trigger, casting or replacement implementation is implied by this report.

## Minimum contract

A graveyard is a player's public, ordered zone of existing card instances. It starts empty. Use the existing `GameState` zone list and `ZoneKey.Of(Graveyard, owner)` as the sole storage. A small read-only `Graveyard` wrapper may expose `Key`, `Order`, `Count`, `Top`, `Contains`, and `Find` for a captured immutable state. Existing `ZoneSystem` remains responsible for movement and replay; `ViewProjector` consumes the same list for every viewer. This is a repository design proposal, not a recovered foreign class signature.

Normal entry goes to the owner's graveyard, face up, at the top. Removal preserves the order of survivors. Ordinary inspection does not reorder cards. Repeating a same-graveyard move should not manufacture a new object or secretly reorder the pile. Retain existing card identity/selection data through cross-zone movement, while the existing zone-change logic allocates its new object identity. Count, lookup and top access must not maintain a separate card collection or clone game objects.

## Client evidence mapping

| Responsibility | Concrete static observations | Local mapping and limits |
| --- | --- | --- |
| Player-owned zone | MTGO `GameCardZone` has `CardZone`, `Player`, typed `Create` overloads, and `IsSharedZone` classifies enum value 3 as nonshared (`mtgo/decompiled/GameCardZone.cs:17-95`). `Game.cs:1292` creates zone 3 for each player; `GameCard.cs:3144-3146` identifies it as the graveyard and resolves the encoded player. `Game.GetCardZone(zone, player)` checks per-player ownership versus shared zones (`Game.cs:932-950`). | Reuse `ZoneKey` with owner. MTGO checks zone lookup structure; it does not establish its server's wrong-owner destination correction. |
| Membership and basic access | MTGO `GameCardZone` inherits a notified card collection and implements list/collection/enumeration interfaces (`GameCardZone.cs:13`). `GameCard.Zone` removes itself from the old collection and adds itself to the new one (`GameCard.cs:671-700`). Current Arena `MtgZone` stores `Type`, `Id`, `Owner`, `Visibility`, `CardIds`, `VisibleCards`; `TotalCardCount` derives from `CardIds.Count` (`arena/decompiled/MtgZone.cs:7-25`). | A read-only wrapper over existing state offers equivalent basic count/membership/order access without duplicating authoritative state. Mutations stay in the event-producing zone service, not UI collections. |
| Received membership and ordering | Retained Arena `GreInterface.ParseFullGameStateMessage` creates each zone from `ZoneInfo`, assigns owner/type/visibility, and copies `ObjectInstanceIds` into a list (`GreInterface.cs:318-326`). It associates reconstructed cards with their zone and adds visible objects (`516-540`). `GetZoneChanges` compares old/new membership, producing client creation/deletion/transfer/anonymity updates (`1823-1940`). MTGO receives `ORDERED_ZONE_INDEX` into `GameCard.OrderedZoneIndex` (`GameCard.cs:393-403,2197`). | Preserve authoritative list order and derive views from it. The presence of an ordered list/index does **not** establish either client's top-first convention or authoritative graveyard insertion algorithm. Our top-at-index-zero convention comes from existing `ZoneSystem.PositionIn`, not reverse-engineered server behavior. |
| Public inspection | MTGO `PlayerViewModel.ConfigureZones` assigns graveyard zone 3 and exposes a graveyard temporary view with card dismissal disabled (`PlayerViewModel.cs:1541-1556`). `ZoneViewModel` enumerates the underlying model and subscribes to membership changes (`ZoneViewModel.cs:185-207`). Arena copies received zone and object visibility (`GreInterface.cs:325,343`) and maintains distinct visible-card collections. | Existing per-seat graveyard view is the natural integration point. Public access is established by rules below; generic received visibility and inspectable UI alone do not prove server privacy enforcement or every graveyard UI path. |
| Sorting versus authoritative order | MTGO `ZoneViewModel.Add` optionally sorts its presentation by timestamp; `Move` changes the view collection and throws for sorted views (`ZoneViewModel.cs:406-441`). These operations do not send a graveyard state-change request in the inspected method bodies. | Do not copy UI sorting into authoritative graveyard order. No arbitrary reorder method is warranted. A presentation sort, if ever added, must remain separate from the zone list. No claim that this optional timestamp sorting is enabled for graveyards was established. |
| Synchronization | Retained Arena state merge uses zone IDs and object instance IDs (`GreInterface.cs:197-201`); MTGO applies property updates and collection membership changes on incoming state. | Reuse current event serialization/reducer and verify graveyard membership/order/identity after reload. Neither protocol establishes our replay format or a server algorithm to transplant. |

## Published rules resolving client gaps

The official [rules page](https://magic.wizards.com/en/rules) currently links the [Comprehensive Rules effective September 25, 2026](https://media.wizards.com/2026/downloads/MagicCompRules%2020260925.txt), checked on 2026-10-25. Relevant requirements:

- CR 400.1–400.3: each player owns a graveyard; it is public; a card destined for someone else's graveyard goes to its owner's corresponding zone.
- CR 400.5 and 404.1–404.2: preserve order, start empty, insert normal arrivals at the top, and allow all players to inspect the face-up pile.
- CR 400.7: crossing zones normally creates a new game object. A same-zone no-op is a local basic-operation policy consistent with the absence of a permitted reorder.
- CR 404.3: the owner chooses the relative order of simultaneous arrivals. This remains an explicit batch/caller responsibility; a read-only wrapper does not implement that choice or prove existing callers supply it.
- CR 712.8a: double-faced cards outside battlefield/stack have front-face characteristics. Resetting a stored selected face on graveyard entry supports that boundary. CR 709.4 separately combines split-card halves; a display index reset is not full multi-face characteristic evaluation.
- CR 708.9 requires certain face-down departures to be revealed. Face-up graveyard state alone does not prove the separate reveal-event behavior.

Tournament-specific reorder exceptions and card-specific permissions are outside this basic abstraction.

## Existing repository boundary inspected

`ZoneSystem.IsOwnerZone` and `KeyFor` already include graveyards. `Proposal` corrects owner-zone destinations; `PositionIn` treats index zero as top for library/graveyard. `ViewProjector.Seat` already enumerates a player's graveyard for every viewer and derives its count; `VisibleGameObjects.Current` includes those projected cards. These are reusable responsibilities, not grounds to add another authoritative graveyard store.

The implementation now supplies `MtgEngine.Rules.Zones.Graveyard`, a read-only
snapshot with `Owner`, `Key`, `Order`, `Count`, `Top`, `Contains` and `Find`.
`ViewProjector.Seat` uses that same ordered membership. `ZoneSystem.Proposal`
returns no movement for a member already in the destination graveyard;
`ZoneSystem.Reborn` resets the face index and face-down flag on graveyard entry.
Movement still uses the existing event, allocation and replacement paths.
No new zone store, wire format, public command or UI control was introduced.

Tests first reproduced four failures (three same-zone placements and a back-face
entry), then passed after these fixes. The final 11 `Scope=Graveyard` cases cover
empty piles and owner validation, public inspection for all four seats, lookup,
owner routing, top insertion, same-zone identity/order preservation, front-face
visibility, departure to a private hand, physical identity/printing/finish and
replay. Two batch cases verify the existing API applies the supplied arrivals
bottom-to-top; they do not claim an interactive owner-choice prompt exists.

Verification logs: `graveyard-focused-before.log`, `graveyard-focused.log`,
`graveyard-regressions.log`, and `graveyard-deck-regressions.log`. The final pre-push
solution run (`graveyard-push-tests.log`) passed 1,656 rules (including all eleven
graveyard cases), 835 API, 326 Cards and 322 pipeline tests. The focused run passes all 11 graveyard
cases. Existing card/deck acceptance remains separately reported as 66 rules and
80 API tests. Touched-file formatting passes. No client source changed; no new
UI behavior or live-client verification is claimed.

## Provenance and limits

Retained MTGO build: 3.4.159.4708. Gameplay assembly `mtgo/artifacts/WotC.MtGO.Client.Model.Play.dll` SHA-256 `7b16823fbaa9a965de3b9ee69ba727992a9b7b50d1187defadd9ac5a6c688081`; earlier REA member Evidence `ev_5c508a6e3992121057eb96955fb197522fc3ed304c4de84eacaa75354286b265`. New `GameCardZone.cs` was reconstructed from that retained binary with the existing ILSpy 11.1.0.9782 tool. Its behavior claims above are ordinary ILSpy static evidence, not a new REA result.

Retained Arena `GreInterface.cs` belongs to the earlier SharedClientCore SHA-256 `42f2fb5f062ccc6098182a902d411746070759a404e875b6e0aac44d3b7ccc6e`, backed by member Evidence `ev_99270613b579e92d6cc431fa8d639036b41fbf27d7ab6d4000c4ef2ea6853b54`. Current `MtgZone.cs` was reconstructed from the installed SharedClientCore SHA-256 `aceb0cd5004b322ceb59b28b0ad44ec799b3d47a46666b17a50150da5449e392`, package declaration `0.1.14589.1332174`. The builds differ; no same-build attribution is made.

`graveyard-contract-reconstruction-hashes.json` records exact inspected file bytes/hashes. The card investigation already verified retained `GameCard.cs` and `GreInterface.cs` against their prior manifests. Additional zone/UI files are identified by this new manifest, without claiming broader original-source fidelity. Reconstructed proprietary files remain in ignored directories. Decompiler unresolved-type comments remain limitations; conclusions above use clear field assignments, control flow and cross-file corroboration.

Status: player ownership, collection/state projection and basic access are supported by concrete client code; game semantics for public visibility, default insertion and fixed order are backed by official rules. Exact foreign server graveyard algorithms and full client UI ordering remain unresolved and are unnecessary for the proposed minimal contract.
