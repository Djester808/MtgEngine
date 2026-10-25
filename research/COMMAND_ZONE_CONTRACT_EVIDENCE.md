> Historical evidence from the pre-reset implementation. See [the current models-only checkpoint](FOUNDATION_INTEGRATION.md); execution and replay claims below no longer describe the active code.

# Basic command-zone contract and evidence

Inspected 2026-10-25 using the reverse-engineer-anything skill and retained static reconstructions. No target application was executed, no foreign server was inspected, and no new REA Evidence ID is claimed. REA tools are unavailable in this session; existing retained records and ordinary file/hash inspection suffice for the bounded questions below.

## Contract

`MtgEngine.Rules.Zones.CommandZone` captures an immutable `GameState`. `Key` is the shared `Zone.Command` key; `Members`, `Count`, `Contains`, `Get`, and `Find` read that existing storage. Objects retain individual owners and controllers. Membership neither requires nor assigns `IsCommander` and does not require `CardInstanceId` or a deck selection. Storage order is not a top/bottom gameplay contract. All entry/exit remains in `ZoneSystem`; no new mutable store, replay identity, catalog, or public action was added.

`PlayerView.Command` filters shared membership by object owner for presentation to every viewer. It is not an owner-only zone or a commander-only list. Existing projection remains the disclosure boundary. Starting selections are commanders because `ZoneSystem.Create` explicitly designates them, not because `CommandZone` infers designation. The generic object test covers non-card membership without implementing emblem creation or abilities.

## Static method and field mapping

| Question | Concrete retained evidence | Local mapping and limits |
| --- | --- | --- |
| Is command a shared zone? | MTGO `GameCardZone.IsSharedZone` classifies client enum 9 as nonshared (`mtgo/decompiled/GameCardZone.cs:53-86`). `GameCard` maps `CARDZONE_COMMAND` to `Game.GetCardZone((CardZone)9, Game.Players[Game.LocalPlayerIndex])` (`GameCard.cs:3196-3199`). | This is a client presentation partition, not evidence that Magic's command zone belongs to one player. Retain the local shared `ZoneKey` and owner-filter only the display. No foreign server storage layout was recovered. |
| Membership/count/lookup | MTGO `GameCardZone` is a notified `IGameCard` collection (`:13`); `GameCard.Zone` removes from the old collection and adds to the new (`GameCard.cs:671-700`). Current Arena `MtgZone` has `Type`, `Id`, `Owner`, `Visibility`, `CardIds`, `VisibleCards`, and count derived from `CardIds.Count` (`arena/decompiled/MtgZone.cs:7-25`). | Read membership and count from authoritative local state. The exact wrapper methods are a local design, not foreign API signatures. |
| Received state versus owners/controllers | Retained Arena `GreInterface.ParseFullGameStateMessage` copies `ZoneInfo` ID, owner, type, membership and visibility (`GreInterface.cs:318-327`), then separately sets object identity, printing group, object type, controller, owner and visibility (`331-365`). | Existing `GameObject`, `ObjectId`, and state projection provide those separate responsibilities. Zone owner metadata alone does not prove command-zone privacy or authoritative admission. |
| Commander designation independent of location | MTGO `GameCard.IsCommander` and `CommanderCastNTimes` are separate properties (`GameCard.cs:606-626`), updated from `IS_COMMANDER` and `COMMANDER_CAST_N_TIMES` (`2194-2195`). Command-display branches separately check `IsCommander` and zone 9 (`1851,1927`). | Preserve `GameObject.IsCommander` across movement; do not derive it from zone membership or recreate tax state in the wrapper. Static client fields do not prove server designation lifetime. |
| Printing/finish and identity | Retained Arena printing resolution uses `obj.GrpId` (`GreInterface.cs:365`); object instance ID and owner are distinct. Card evidence report documents MTGO premium and Arena styles. | Reuse `StartingCard`, `DeckSelection`, and `CardInstanceId`. Our finish enum and persistent physical-copy identity are existing repository design, not recovered Arena protocol semantics. |
| Synchronization | Retained Arena merges object and zone IDs and reconstructs visible membership; MTGO updates notified collections from received object properties. | Existing event serialization/reducer remains authoritative. The tests establish local replay, not foreign replay or server algorithms. |

## Rules and repository paths

The official [rules page](https://magic.wizards.com/en/rules), checked on 2026-10-25, links the [September 25, 2026 Comprehensive Rules](https://media.wizards.com/2026/downloads/MagicCompRules%2020260925.txt). CR 400.1–400.2 establish shared/public zones; CR 408 includes specialized objects and emblems. CR 903.3 keeps commander designation across zone changes, and 903.6 establishes initial face-up command placement. CR 712.8a supplies front-face characteristics outside battlefield/stack. These rules resolve semantics that client collection code cannot prove.

`GameLobbyDecks.LoadAsync` expands selected printing/finish copies, removes saved primary/secondary commander selections with `TakeCommander`, and calls `CommanderDeckAdmission.ValidateAsync`; the latter delegates to the existing construction validator. `ZoneSystem.Create` uses `PlayerStart.CommandCards`, `Born`, and `GameObject.CreateCard`, then explicitly sets `IsCommander`. `Reborn` retains ownership, designation, physical identity and selection while allocating a new object ID. `CommanderDamage`, commander cast history/tax, replacement/return choices and `Subsystems.Wave2` are unchanged live dependencies.

A reproduced gap was corrected in `ZoneSystem.Reborn`: command-zone entry now chooses `FaceRules.FrontFace`, as hand/exile entry already does. Before the correction, the test returned a transformed commander with stored face index 1, allowing `ViewProjector` to emit back-face descriptive text while layers used front-face characteristics. The regression verifies front-face text and designation/selection after serialized replay. This is a description/state consistency fix, not new casting or transform mechanics. Existing face-down redaction remains; no global rule that all command-zone objects must be face up was added.

## Provenance

`command-zone-contract-hashes.json` records hashes recomputed for the inspected files and retained gameplay assembly. The reconstructed files match the earlier graveyard/card manifests. MTGO gameplay assembly SHA-256 is `7b16823fbaa9a965de3b9ee69ba727992a9b7b50d1187defadd9ac5a6c688081`, build 3.4.159.4708, earlier member Evidence `ev_5c508a6e3992121057eb96955fb197522fc3ed304c4de84eacaa75354286b265`.

Retained Arena `GreInterface.cs` belongs to earlier SharedClientCore SHA-256 `42f2fb5f062ccc6098182a902d411746070759a404e875b6e0aac44d3b7ccc6e`, member Evidence `ev_99270613b579e92d6cc431fa8d639036b41fbf27d7ab6d4000c4ef2ea6853b54`. Installed SharedClientCore was separately hash-checked this pass: `aceb0cd5004b322ceb59b28b0ad44ec799b3d47a46666b17a50150da5449e392`. Current `MtgZone.cs` was reconstructed from that newer binary in the graveyard investigation. These are different builds; current binary identity does not authenticate older reconstructed methods as current. Proprietary reconstructions remain ignored. Decompiler unresolved-type comments and absence of runtime/server evidence remain limitations.

## Verification

Focused acceptance runs with `dotnet test tests/MtgEngine.Rules.Tests --filter Scope=CommandZone`. Five dedicated cases cover empty/missing lookup, shared initial placement, four-seat visibility, non-card membership, owner versus controller, front-face return, immutable snapshots, physical identity, selected printing/finish, persistent commander designation and serialized movement/replay. Existing Deck acceptance separately covers saved paired commander admission and initial libraries; no assertions were weakened.

The initial reproduction (`command-zone-repro.log`) passed four cases and failed the back-face return case. Final results are recorded below. No client source changed, no live UI claim is made, and no commit or push was performed. Test totals are bounded regression evidence, not full Magic rules certification.

- Final focused acceptance: 5 passed (`command-zone-focused-final.log`).
- Unchanged card/deck acceptance: 66 rules and 80 API passed (`command-zone-deck-rules.log`, `command-zone-deck-api.log`).
- Combined solution: 1,718 rules, 835 API, 326 Cards and 322 pipeline passed (`command-zone-final-broad.log`). This includes concurrent stack/reveal additions available at that checkpoint. An earlier run caught the reveal chat's in-progress reorder test; that failure is resolved in the final run.
- Touched command-zone source/test whitespace verification passes (`command-zone-format-final.log`). No repository-wide formatting was applied.

Owned production changes: new `Zones/CommandZone.cs`, the command branch and explanatory comment in `Views/ViewProjector.cs`, and command destination face selection in `Zones/ZoneSystem.cs`. Owned tests: `tests/MtgEngine.Rules.Tests/CommandZoneContractTests.cs`. Owned evidence: this report and `command-zone-contract-hashes.json`. Other shared-checkout changes belong to their respective chats.
