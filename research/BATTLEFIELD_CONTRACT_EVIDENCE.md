> Historical evidence from the pre-reset implementation. See [the current models-only checkpoint](FOUNDATION_INTEGRATION.md); execution and replay claims below no longer describe the active code.

# Battlefield aggregate contract and evidence

Inspected 2026-10-25 with the reverse-engineer-anything workflow. This pass reuses
retained REA evidence and reads actual reconstructed method bodies with ordinary
filesystem tools. No connected REA tools are available in this chat; no new REA
Evidence IDs, target execution, live gameplay observation or server reconstruction
are claimed. The supplied skill's connection/recovery guide was read; the retained
artifacts answer this bounded static question without configuration changes.

## Contract and local method mapping

`Zones.Battlefield` captures an immutable `GameState`; the sole authoritative list
is `state.In(ZoneKey.Shared(Zone.Battlefield))`. This is a repository design, not a
recovered foreign class signature. It adds no persisted property or wire field.

| Responsibility | Local method and boundary |
| --- | --- |
| Shared membership/count | `Key`, `Members`, `Count`, `Contains`. No per-player battlefield stores. Enumeration preserves storage order but does not represent layout or grant reorder permission. |
| Lookup | `Get` returns only a current member; `Find` is a server-side predicate over stored objects. Neither grants disclosure or replaces the existing rules query filters. |
| Ownership/control | `OwnedBy` selects stored ownership; `ControlledBy` calls the supplied existing `ICharacteristics.ControllerOf`. Both describe stored membership, including phased-out objects, rather than rules target eligibility. |
| Phasing | `Members` retains phased-out objects; `ActiveMembers` excludes them. Count is the stored member count, not the number a rules effect may count. Existing `ObjectChanged`/reducer preserves identity on phase-out/in. No phasing mechanic is added. |
| Projection | `ViewProjector.Project` reads `Members`; `TableView` continues grouping already-redacted objects by their evaluated controller. Phased-out members remain in public presentation as before. Individual permanent presentation/status belongs to the separate permanent work. No phased-out badge or live UI claim is made here. |
| Face-down boundary | Existing projection supplies public object identity, owner/controller and generic face-down characteristics, withholding name, Oracle identity, printing, finish and text. The authoritative aggregate is never a transport DTO. Controller-only inspection permissions are not added. |
| Entry/departure | Existing `ZoneSystem.Move/MoveAll`, `Proposal`, `Reborn`, events and reducer own movement, owner-zone routing and identity. A narrow `Proposal` guard now refuses battlefield-to-same-battlefield movement before it recreates the object. It does not silently implement control change, untapping or entry. |
| Replay | Existing `EventLogSerializer` and `GameReducer` reconstruct membership, physical card identity and printing/finish. No alternate replay mechanism is introduced. |

## Concrete client observations

| Static evidence | Mapping and limits |
| --- | --- |
| MTGO `GameCardZone.cs:13-95` is a notified card collection with zone and player fields; `IsSharedZone` returns true for enum 7. `GameCard.GetSharedCardZone` at `GameCard.cs:3188-3194` maps `CARDZONE_PLAY`, incoming-play and phased-out protocol states to zone 7. | Supports one shared battlefield collection rather than one authoritative collection per controller. Protocol phase states do not require separate local zones. |
| MTGO `GameCard.Zone` at `671-700` removes/adds the card from/to zone collections. Separate `Controller` and `Owner` properties occur at `1054` and `1066`; `IsPhasedOut` is a separate notified status at `522-531`. | Membership, ownership, control and status are independent. Client collection updates are not an authoritative server move algorithm. |
| Retained Arena `GreInterface.ParseFullGameStateMessage` at `318-326` copies zone ID/type/owner/visibility and `ObjectInstanceIds`; `337-338` sets controller and owner separately. Current `MtgZone.cs:7-25` holds `CardIds`, `VisibleCards`, and derives total count from `CardIds.Count`. | Reuse the existing zone list and separate redacted projection. Current and retained Arena files are different builds; architectural corroboration is not same-build attribution. |
| Retained `GreInterface.cs:516-524` associates visible instances with their zone and also adds phased-out instances to `Battlefield.VisibleCards`. `568-580` derives `BattlefieldPermanentsByControllerId` from visible cards' controller IDs. Current `MtgCardInstance.cs:209-215` derives `IsPhasedOut` from its received zone type. | Public presentation may retain phased-out members; controller layout is derived, not ownership. Our state uses a status flag instead of Arena's transport zone type. No foreign phase animation or server behavior is inferred. |
| Retained `GreInterface.cs:197-201` merges objects/zones by received identity; `GetZoneChanges` at `1823-1940` compares memberships for client updates. | Supports deriving views from authoritative membership. It does not establish the foreign server's physical-card lifetime, movement, replay format or privacy implementation. |

## Official rules resolving gaps

The [official rules page](https://magic.wizards.com/en/rules) linked the
[September 25, 2026 Comprehensive Rules](https://media.wizards.com/2026/downloads/MagicCompRules%2020260925.txt)
when checked on 2026-10-25. CR 400.1–400.2 establish shared/public membership with
face-down exceptions; 403.1–403.4 establish an initially empty battlefield and
new objects on actual entry. CR 400.5 separates physical arrangement from control
and status. CR 702.26b–d distinguishes stored phased-out membership from active
rules presence and preserves identity. CR 708 governs withheld face-down details.
The same-battlefield no-op is a local seam policy consistent with these boundaries,
not a claim about an unobserved MTGO/Arena server method.

## Identity and evidence provenance

`battlefield-contract-reconstruction-hashes.json` records exact SHA-256 hashes and
sizes for every reconstructed file used above, the retained MTGO gameplay binary,
and the currently installed Arena SharedClientCore binary. The selected prior
graveyard-manifest hashes for GameCardZone, GameCard, MtgZone and GreInterface all
match. GameCard/MtgCardInstance correspond to the card investigation's retained
reconstructions; these are static ILSpy artifacts with unresolved-type comments,
not original source.

- MTGO retained gameplay DLL: `7b16823fbaa9a965de3b9ee69ba727992a9b7b50d1187defadd9ac5a6c688081`, build 3.4.159.4708. Prior REA member evidence: `ev_5c508a6e3992121057eb96955fb197522fc3ed304c4de84eacaa75354286b265`.
- Arena retained GreInterface comes from SharedClientCore `42f2fb5f062ccc6098182a902d411746070759a404e875b6e0aac44d3b7ccc6e`, prior member evidence `ev_99270613b579e92d6cc431fa8d639036b41fbf27d7ab6d4000c4ef2ea6853b54`.
- Current installed Arena SharedClientCore rehashed to `aceb0cd5004b322ceb59b28b0ad44ec799b3d47a46666b17a50150da5449e392`; current MtgZone/MtgCardInstance reconstruction belongs to this later artifact (previous inspection recorded package 0.1.14589.1332174). The old binary identity is retained evidence, not a claim that today's installed binary matches it.

Proprietary artifacts remain in ignored research directories. Exact foreign
server control/movement/privacy logic remains unknown. No layer engine, combat,
trigger, replacement, keyword, casting or payment behavior was expanded. Existing
Wave2 composition remains intact. No client sources were edited.

## Verification

`BattlefieldContractTests` carries `Scope=Battlefield`. The initial executable
run reproduced all three same-battlefield placement failures: each fabricated a
new object, changed initial control and cleared tapped status. Six other tests
passed. Evidence: `battlefield-focused-before-host.log`. The first sandbox run
did not start the .NET host; the host-permission run is the actual test evidence.

Final verification:

- Focused battlefield: **9 passed**, `battlefield-focused.log` (after a compiler
  output-lock collision on the first post-fix build; retried using built output).
- Unchanged card/deck subset: **66 rules + 80 API passed**,
  `battlefield-deck-regressions.log`.
- Broad shared-checkout snapshot: **1,773 rules, 835 API, 326 Cards passed**,
  `battlefield-broad.log`. Pipeline initially passed 321 and failed one
  `TranslateStageTests.Every_request_carries_the_cached_vocabulary_and_the_card`.
  Its test helper uses a deterministic per-test directory in the same checkout,
  so overlapping chat test processes can share/delete its cache. This is a
  plausible collision explanation, not a proved process trace. A separate
  unchanged pipeline retry passed **322/322**, `battlefield-pipeline-retry.log`.
- New-file whitespace verification passed, `battlefield-format-final.log`;
  the earlier scoped check reported only new-file line endings, now corrected.
  Shared-file diff whitespace check passed. No whole-repository formatter ran.

These totals reflect concurrent foundation work present at build time and are
not battlefield acceptance counts or proof of complete Magic rules. No live UI
verification was performed: client display is **unverified**, including any
phased-out visual treatment. Existing face-down redaction intentionally withholds
identity even from its controller; a controller inspection workflow remains
outside this aggregate. No commit, stage, push or merge was performed here.

Owned changes for integration: `Zones/Battlefield.cs`,
`BattlefieldContractTests.cs`, this report and its hash manifest; one battlefield
membership expression in `Views/ViewProjector.Project`; five added lines in
`Zones/ZoneSystem.Proposal` for the same-battlefield guard. Other changes in those
shared files belong to the concurrent chats and were preserved. The parent chat
owns test-scope script/docs consolidation and final review/commit/merge.
