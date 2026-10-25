# Basic reveal-area contract and evidence

Inspected 2026-10-25 using the reverse-engineer-anything skill. This pass reused
retained REA evidence and inspected reconstructed method bodies using ordinary
file tools. No new REA session, client runtime or foreign server was executed.

## Terminology and implemented contract

At the user's request, the redundant `RevealArea` wrapper was removed. Disclosure
is represented by the existing `GameView.LastReveal`, `LastLook` and
`InspectedLibraryCards` properties. The projected list supplies count/membership;
existing `GameState.LibraryInspections` retains scope and provenance. No new rules
zone or inspection store is needed. Scope keys identify inspection grants; `ObjectId`
identifies a currently inspectable object. Neither is `CardInstanceId`.
Historical `RevealedCard` has no physical/object identity or future-location link.
No new reveal ID, replay mechanism, catalog or mutable collection was added.

`ViewProjector.Project` populates the existing three disclosure properties
directly. It checks the viewer, the inspection's library owner, object existence,
the object's zone, and authoritative zone membership. The last check closes a
demonstrated defensive gap: a dangling inspection plus an object still labelled
Library could previously disclose that object even when absent from the zone
list. The focused stale-reference fixture explicitly constructs this state; no
ordinary public command producing corrupt membership is claimed.

Existing snapshots retain already-disclosed names, text, printing and artwork
after expiry, departure or shuffle. They do not disclose the new hidden location
or grant current-object access. Existing live object projection preserves selected
printing/finish, owner/control and face-down redaction. Historical snapshots keep
their existing wire format (which does not include finish); no new private data
was added to it. Captured views remain immutable when later states change.

## Concrete evidence and method mapping

| Concern | Static evidence | Local responsibility / boundary |
| --- | --- | --- |
| Display area versus physical zone | Retained Arena `GreInterface.ParseFullGameStateMessage`, `GreInterface.cs:500-514`, branches on `GameObjectType.RevealedCard`, assigns its actual `Zone` from `obj.ZoneId`, adds it to the separate reveal area's `VisibleCards` and `RevealedCards`, then skips normal zone processing. | Supports a separate display model. It does not require moving local cards into a new authoritative zone. |
| Temporary reveal UI | MTGO `PlayerViewModel.ConfigureZones`, `PlayerViewModel.cs:1548,1560-1562`, uses card-zone value 16 for a temporary revealed view and enables card dismissal. Graveyard dismissal is disabled in the same method. | Client terminology includes a reveal zone; dismissible presentation is not evidence of a Magic rules zone or its server lifetime algorithm. |
| Membership/count | Current Arena `MtgZone.cs:7-25` keeps `Type`, `Id`, `Owner`, `Visibility`, `CardIds`, `VisibleCards`; total count derives from `CardIds`. | Keep current access derived from authoritative membership, with historical display kept separate. Do not transplant Arena's mutable UI store. |
| Reveal-specific notifications | Retained `GreInterface.cs:45,49,795` uses reveal-specific event generators; `1429-1440` emits delete events for absent revealed instances; `1827` excludes Revealed/Limbo from normal zone changes. | Disclosure lifecycle and ordinary movement are distinct. Exact foreign server expiry and identity allocation remain unknown. |
| Public versus private | Local `CardsRevealed.Apply` updates `LastReveal`; `CardsLookedAt.Apply` updates only named viewers in `LastLooks`. `ViewProjector` populates the existing view properties. | Public history reaches every seat; private history reaches named viewers only. A look granted to all viewers still does not become public-reveal provenance. |
| Provenance and expiry | `LibraryInspection` retains Viewer/Owner/Cards plus Resolution/PublicReveal. Existing `LibraryInspectionChanged`, resolution completion, manual stop and `ZoneReordered` own lifetime. | Projection reads grants; it does not issue/extend them. Stale/moved/reidentified objects cannot be followed by physical identity. Shuffle invalidates owner inspections; other reorder paths retain their existing policies. |
| Continuous top | `LibraryDisclosurePolicy.TopAvailable`, `LibraryRevealCoordinator.Reconcile` and `EndReorderedDisclosures` control payment-time availability and top reidentification. | Continuous top and choice cards remain their existing separate projection paths; they are not counted as explicit inspection membership. Reidentification is not a zone move. |
| Historical artwork | `VisibleGameObjects.Current` excludes historical reveals; `GameArtworkService.ForAsync` separately adds already-visible snapshot artwork keys using local-only lookup. | Historical artwork remains available without granting action/current-object access. Existing API artwork tests run unchanged. |
| Client presentation | Sibling `library-reveal.component.ts` groups active inspected cards, renders histories separately, clears stale active context, and opens historical details without active actions. | Existing UI responsibilities fit this contract. No client changes or live UI claims were made. |

## Public rules and boundaries

The official [rules page](https://magic.wizards.com/en/rules) was checked and links
the [September 25, 2026 Comprehensive Rules](https://media.wizards.com/2026/downloads/MagicCompRules%2020260925.txt).
CR 400.1 lists the rules zones without a reveal zone. CR 701.20a-e distinguishes
public revealing from a specified viewer looking, states that revealing does not
move the card, permits repeated reveals, and ends revealed identity on reorder.
These rules support the terminology and access boundary; retained client code
alone does not prove authoritative rules compliance.

This small reader does not certify every lifetime prescribed by 701.20a or every
reorder case. Existing resolution/manual/continuous-top policies remain in place;
broader explicit-reveal reorder semantics and legacy-event migration remain
bounded by the earlier library evidence reports. No new keyword, reveal-until,
search effect, trigger, replacement or casting/payment implementation was added.

## Artifact identity

`reveal-contract-reconstruction-hashes.json` records freshly calculated SHA-256
and byte lengths for inspected reconstructions and binaries. PlayerViewModel,
GreInterface and MtgZone match the retained graveyard reconstruction manifest.

- MTGO retained Play.dll: `7b16823fbaa9a965de3b9ee69ba727992a9b7b50d1187defadd9ac5a6c688081`, build 3.4.159.4708; earlier member evidence `ev_5c508a6e3992121057eb96955fb197522fc3ed304c4de84eacaa75354286b265`.
- Retained Arena GreInterface source hash: `9caad39b83478dbcc34a9a8825b0ee09ecc049b743610ccfb10082e9104879ba`; earlier member evidence `ev_99270613b579e92d6cc431fa8d639036b41fbf27d7ab6d4000c4ef2ea6853b54` associates it with SharedClientCore `42f2fb5f062ccc6098182a902d411746070759a404e875b6e0aac44d3b7ccc6e`.
- Current installed Arena SharedClientCore freshly hashes to `aceb0cd5004b322ceb59b28b0ad44ec799b3d47a46666b17a50150da5449e392`; the prior card/graveyard reconstruction attributes MtgZone to this newer artifact. It is not the retained GreInterface build. No current-build GreInterface equivalence is asserted.

Proprietary reconstructions remain in ignored directories. All observations above
are static, not observed execution; exact foreign server algorithms are unknown.

## Verification

Run focused acceptance with `dotnet test tests/MtgEngine.Rules.Tests --filter Scope=Reveal`.
Eight `RevealContractTests` cover empty access, four-seat public/private routing,
overlapping grants and provenance, captured snapshots, expiry/shuffle, departure
and return, reorder identity invalidation, dangling references, and event reload.
Existing acceptance assertions were not changed.

The existing disclosure/privacy subset passed 48 cases before the added four-seat
case (`reveal-focused.log`). Final focused acceptance passes 8 cases; unchanged
card/deck acceptance passes 66 Rules and 80 API cases, recorded in
`reveal-contract-tests.log`, `reveal-deck-rules.log`, `reveal-deck-api.log`.
Full solution `reveal-regressions.log` passed 1,718 Rules, 835 API, 326 Cards and
322 Pipeline tests. These include concurrent command-zone/stack work and are not
all reveal tests or proof of complete Magic rules. No client source changed;
live UI is unverified. Nothing was staged, committed or pushed by this chat.

Only the two new reveal-owned C# files were whitespace-formatted
(`reveal-format.log`); `git diff --check` passes. Other chats' stack/command-zone
branches in the shared ViewProjector file were preserved.

Property simplification verification: all 19 reveal, foundation integration and
library-disclosure lifetime tests pass (reveal-property-tests.log).

Push verification for the property simplification: full backend passes 1,765 Rules,
835 API, 326 Cards and 322 Pipeline cases (reveal-property-push-tests.log).
These counts reflect the concurrent checkout; only reveal cleanup is included
in this chat's commit.
