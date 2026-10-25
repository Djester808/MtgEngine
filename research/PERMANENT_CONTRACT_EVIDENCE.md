# Basic permanent contract

Inspected 2026-10-25 using the reverse-engineer-anything evidence workflow.
This is static client reconstruction and local implementation evidence, not observed
foreign gameplay or recovered server code.

## Contract and implementation

`State/PermanentAccess.cs` supplies `Find(state, id)`, `Require(state, id)` and
`Evaluate(state, id, ICharacteristics)`. A permanent is an existing battlefield
`GameObject`; lookup checks object identity, shared zone key and membership agree.
It returns the same immutable object, including phased-out permanents. This is
server inspection, not targeting permission, action authorization or disclosure.
Unknown/stale IDs return null from Find and fail Require/Evaluate. Evaluation
delegates to the supplied existing characteristic service against the same state.

No new mutable Permanent, catalog, event, zone store, serializer property or
allocator exists. `IsToken` distinguishes tokens; absence of `CardInstanceId`
does not establish tokenhood because legacy cards may lack that ID. `ObjectId`
identifies this rules object; persistent `CardInstanceId` identifies the selected
deck copy through ordinary zone changes. `DeckSelection` preserves printing and
finish. Token/copy creation remains its existing owner's responsibility.

`Owner` is stored separately from `InitialController`. Current controller comes
from `ICharacteristics` (the repository includes controller in that result even
though CR 109.3 does not classify controller as a characteristic). `FaceIndex`,
`FaceDown`, `Tapped`, `PhasedOut`, `Damage`, `Counters`, `AttachedTo` and
`AttachedToPlayer` remain stored state. Printed `Face` is not evaluated power,
types or abilities. Existing `ObjectChanged`, `CountersChanged`, `DamageDealt`
and movement events own state changes/replay; LayerSystem owns evaluation.
The access API deliberately adds no setters that bypass these owners.

## Concrete method/evidence mapping

| Concern | Observed static client implementation | Local boundary |
| --- | --- | --- |
| Instance versus definition | MTGO `GameCard : MagicEntityInstance<ICardDefinition, CardDefinition>` and its constructor retain instance ID and definition. Arena retained `GreInterface.ParseFullGameStateMessage`, lines 331–366, assigns `InstanceId`, `GrpId`, `ObjectType` and resolves printing independently. | Reuse GameObject/Definition; client IDs do not prove physical-card lifetime. |
| Ownership/control | MTGO `GameCard.Controller`/`Owner` at 1054/1066 are separately populated at 2038–2052. Arena parser assigns `ControllerSeatId` and `OwnerSeatId` separately at 337–338; current `MtgCardInstance` has separate fields at 49–51. | Stored owner and evaluated controller must not be conflated. |
| Status and damage | MTGO incoming property path reads `DAMAGE`, `TAPPED`, flip flags and phased-out encoding at 2147–2179. Arena parser assigns `IsTapped`, power/toughness, visibility and damage from incoming object data at 340–343 and 382–385. Current MtgCardInstance exposes tap, face-down, copy, damage and type fields. | Stored status is separate from computed characteristics. Foreign clients consume server results; they do not prove our layers or damage algorithms. |
| Counters | MTGO `ResolveCounters` at 3893 clears/repopulates the dictionary from positive `COUNTERS_LIST` values and notifies changes; incoming processing invokes it at 2388. | Preserve existing immutable counters and CountersChanged. No counter effects added. |
| Attachments | MTGO `ResolveAttachedToCards` at 2531 follows received attachment IDs via `Game.ResolveDigitalObject`, validates returned ID and invokes UpdateAttachedTo. It also follows presentation-related associations. | Reuse stored object/player attachment references. This method is not evidence for legal attachment rules. |
| Privacy | Arena parser copies received visibility; current MtgCardInstance.UnknownCardData builds a card-back placeholder. MTGO hidden-card selection is documented in CARD_CONTRACT_EVIDENCE. | Authoritative access is never a transport DTO. Existing ViewProjector owns face-down redaction. |

## Public rules and design inference

The official [rules page](https://magic.wizards.com/en/rules) linked the
[September 25, 2026 rules](https://media.wizards.com/2026/downloads/MagicCompRules%2020260925.txt)
when checked on 2026-10-25. CR 110.1–110.3 distinguish battlefield permanents,
ownership/control and evaluated characteristics. CR 110.4c means loss of permanent
types does not end permanent identity, so lookup does not filter by printed type.
CR 110.5 separates status from characteristics; CR 702.26d says phasing does not
change zones. CR 400.7 supplies the normal zone-change identity boundary.
These support the local API design, not a claim that foreign clients expose it.

The existing model has no independent flipped/unflipped flag. FaceIndex is the
existing selected-face representation; this task does not claim complete flip
status semantics. No combat, tapping costs, attachment legality, counter effects,
summoning-sickness rules, layers, triggers or replacement behavior was added.

## Provenance and limits

`permanent-contract-hashes.json` records SHA-256 of the actual inspected files
and retained MTGO gameplay assembly. GameCard and GreInterface hashes match the
prior card report; MtgCardInstance matches card-contract-reconstruction-hashes.json.
MTGO retained gameplay binary is `7b16823fbaa9a965de3b9ee69ba727992a9b7b50d1187defadd9ac5a6c688081`,
build 3.4.159.4708, earlier REA member evidence
`ev_5c508a6e3992121057eb96955fb197522fc3ed304c4de84eacaa75354286b265`.
Retained Arena GreInterface corresponds to earlier SharedClientCore hash
`42f2fb5f062ccc6098182a902d411746070759a404e875b6e0aac44d3b7ccc6e`, evidence
`ev_99270613b579e92d6cc431fa8d639036b41fbf27d7ab6d4000c4ef2ea6853b54`.
Current reconstructed MtgCardInstance was obtained by the prior card investigation
from hash `aceb0cd5004b322ceb59b28b0ad44ec799b3d47a46666b17a50150da5449e392`,
package 0.1.14589.1332174. These different builds are not conflated. This pass
reuses retained REA/ILSpy artifacts with ordinary file inspection; no new REA
Evidence IDs or runtime traces are claimed. Decompiler unresolved-type comments
remain limitations. Exact foreign server mutation, identity and rules evaluation
are unknown and unnecessary to this basic access contract.

## Verification

Dedicated tests are `PermanentContractTests`, tagged `Scope=Permanent`. They cover
identity/membership validation, token and legacy-card handling, ownership/control,
stored status via existing serialized events, departure/return identity, selection
retention and face-down evaluation/projection. Verification results follow below.

Final focused run: **11 passed**, `permanent-focused-final.log`.
Unchanged card/deck acceptance: **66 rules + 80 API passed**, separately recorded
in `permanent-deck-rules.log` and `permanent-deck-api.log`.
Broader solution regressions: **1,773 rules, 835 API, 326 Cards and 322 pipeline
passed**, `permanent-broad.log`. This is a checkpoint of the concurrently edited
checkout, not a count of permanent tests or evidence of full Magic rules coverage.
The initial host run encountered a transient compile error in another chat's
battlefield test; that chat corrected its call before the successful rerun.
The sandboxed initial build stalled and was interrupted; successful results above
come from host-access runs. No assertions were weakened or unrelated files edited.
No client changes, UI claims, staging, commits or pushes were made.
Touched-file whitespace verification passed after normalizing the two new C#
files to the repository's CRLF convention (`permanent-format-verified.log`).
