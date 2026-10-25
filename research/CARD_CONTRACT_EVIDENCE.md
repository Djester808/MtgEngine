> Historical evidence from the pre-reset implementation. See [the current models-only checkpoint](FOUNDATION_INTEGRATION.md); execution and replay claims below no longer describe the active code.

# Basic card contract: evidence and scope

Inspected 2026-10-25. This is static reconstruction evidence, not observed gameplay or a reconstruction of either server. The proposed contract deliberately reuses existing MtgEngine responsibilities: catalog `Domain.Models.CardDefinition`, executable/printed `Cards.CardDefinition`, quantity-bearing `CollectionCard`, per-copy `StartingCard`, and the existing `GameObject` with `CardInstanceId` and `DeckSelection`.

## Proposed contract

A card definition describes a card and its faces; a printing/art selection describes its presentation; a selected copy identifies the Oracle card, optional printing, and finish; a game object represents that copy in a game with its own identity and owner. A collection row can contain many copies and is not a game instance.

Use a validated `GameObject.CreateCard` construction path, validate the selection and definition shapes, and provide bounded face access on the existing definition. Keep library operations in the existing library/zone services. Preserve `CardInstanceId`, ownership and `DeckSelection` as existing movement/replay semantics require. Access to authoritative definition data does not grant permission to disclose it: existing per-viewer projections remain the boundary. Do not add a third catalog, duplicate instance store, rules-text interpreter, or methods for casting, combat, keywords and abilities.

This is a design recommendation supported by the following observations, not a claim that either client uses our exact contract.

## Concrete mapping

| Concern | MTGO observed implementation | Arena observed implementation | MtgEngine mapping and parity boundary |
| --- | --- | --- | --- |
| Definition versus instance | `GameCard : MagicEntityInstance<ICardDefinition, CardDefinition>`; constructor accepts an object `id`, `Game`, and definition (`mtgo/decompiled/GameCard.cs:23,1771`). | Retained `GreInterface.cs:331-366` separately assigns `InstanceId`, `GrpId`, owner/controller and visibility; resolves printing through `GetCardPrintingById(obj.GrpId)`. | Keep catalog/printed definition separate from `GameObject`. An Oracle ID is not an object ID. `CardInstanceId` surviving zone changes is our established design; these paths do not prove equivalent server identity lifetime. |
| Creation and validation | `GameCard.Create` selects the definition, asserts nonnull, constructs the object and copies properties (`GameCard.cs:2773-2780`). | Retained `GreInterface` reconstructs instances from incoming `GameObjectInfo`; current `MtgCardInstance.UnknownCardData` constructs a placeholder from instance ID and zone (`MtgCardInstance.cs:388`). | `CreateCard` is the corresponding local construction seam. Reject malformed local selections/definitions rather than constructing an unresolved authoritative card. Do not copy MTGO's presentation fallback into authoritative card admission. |
| Owner versus controller | Separate getters (`GameCard.cs:1054,1066`), populated independently from server player properties (`2038-2052`). | Separate assignment from `ControllerSeatId`/`OwnerSeatId` (`GreInterface.cs:337-338`); separate fields on current `MtgCardInstance.cs:49-51`. | Retain existing Owner/Controller; a library operation must not conflate them. |
| Printing, artwork, finish | Reconstructed `CardDefinition.ArtId`, set/collector fields, `IsPremium` from `IS_FOIL` (`CardDefinition.cs:446-473,718`). | Current `CardPrintingData` exposes `GrpId`, `ArtId`, expansion, collector number, `ImageAssetPath`, supported styles (`CardPrintingData.cs:109-129,179,247`). Retained database schema has `GrpId`, `ArtId`, art path, and alternate-printing/style tables. | Keep optional printing and selected finish on `StartingCard`; resolve artwork through the existing catalog. MTGO premium and Arena styles are not evidence of a shared Nonfoil/Foil/Etched model. Our three finishes reuse collection semantics, not an invented Arena enum. |
| Faces and printed data | `OtherFaceCard` lazily resolves another definition and returns null when no `OTHER_FACE` property exists (`CardDefinition.cs:511-525`); front/back flags (`538-542`); `RulesText` is a string (`1192`). | Current `CardPrintingData.LinkedFaceGrpIds` and `LinkedFacePrintings` resolve through the provider (`171,265,649-661`); `MtgCardInstance.TryGetBackInstance` searches linked instances (`311-329`). Retained database stores linked face IDs, costs, textual power/toughness, type and color data. | Bounded `CardDefinition.GetFace` is a simpler local equivalent for accessing the already-materialized face list. Face access does not transform a card or execute its text. Preserve descriptive `Face.OracleText`. Do not turn the clients' linked-face transport model into duplicate physical copies. |
| Hidden information | `GetCardDefinitionForNewGameCard` resolves texture data when supplied and otherwise selects specific hidden-card display definitions or a generic face-down definition (`GameCard.cs:2782-2847`). | Retained state assigns zone/object visibility (`GreInterface.cs:325,343`). Current `UnknownCardData` contains instance/zone/owner/controller and a standard card back, no printing lookup (`MtgCardInstance.cs:388-398`). | Preserve current viewer projections; hidden cards must not leak Oracle IDs, printing/art IDs or descriptive text merely because the server knows them. Client placeholders are display evidence, not proof of either server's privacy algorithm. |
| Copy, reload, ordering | Retained MTGO incoming-state path reconstructs objects and updates their zones; it does not reveal authoritative shuffle/draw algorithms. | Retained diffs merge `GameObjects` by `InstanceId`, zone membership by object IDs (`GreInterface.cs:197-201,318-326`). Current `GetCopy` copies state; `GetExamineCopy` explicitly clears instance ID for display (`MtgCardInstance.cs:258-298`). | Reuse existing serialization/replay and library order; do not use examine/display copy semantics for live instances. Test identity and selection retention through draw/search/shuffle/order/reload locally. Server shuffle algorithms and physical-copy tracking are unknown, so no exact method-parity claim is justified. |

## Evidence identity and verification

The starting reports and inventories were inspected: MTGO `card-evidence.json`, `card-inventory.json`, card/core/cardmanager type inventories, `ACTION_STATE_AND_CARD_FINDINGS.md`, `INVESTIGATION.md`; Arena `FINDINGS.md`, `card-database-evidence.json`, schema and member selections. `arena/database-schema-selection.json` is literally null; use `database-schema.json`, not that empty selection, for schema claims. MTGO's failed Card.dll REA record is a memory-limit result, and Arena's failed SQLite REA record is an access-denied result: neither proves positive card behavior.

Existing reconstructed files were hash-checked against their retained manifests and matched:

- MTGO `GameCard.cs`: `a38135cbc0fb0dcfe1e3388a411479d7ad3776560324793b2ee68632b2fd9641`.
- MTGO `Card.CardViewModel.cs`: `b0abdeff1b612cb578e46019f87613d6e6cbe376f79bfe156bb0ee4d0570ee75`.
- Arena `GreInterface.cs`: `9caad39b83478dbcc34a9a8825b0ee09ecc049b743610ccfb10082e9104879ba`.

Retained Arena member Evidence `ev_99270613b579e92d6cc431fa8d639036b41fbf27d7ab6d4000c4ef2ea6853b54` binds the earlier SharedClientCore hash `42f2fb5f062ccc6098182a902d411746070759a404e875b6e0aac44d3b7ccc6e`; its `ParseFullGameStateMessage` anchor is token `0x060008e4`. Retained MTGO gameplay member Evidence is `ev_5c508a6e3992121057eb96955fb197522fc3ed304c4de84eacaa75354286b265`, with gameplay assembly hash `7b16823fbaa9a965de3b9ee69ba727992a9b7b50d1187defadd9ac5a6c688081`. These are earlier REA results, not new tool results from this pass.

Focused additional reconstruction used the existing ILSpy 11.1.0.9782 tool, without executing the target assemblies:

- `WotC.MtGO.Client.Model.Core.CardDefinition` from retained `mtgo/artifacts/WotC.MtGO.Client.Model.Core.dll`, SHA-256 `0b315e11a92a96585a8d5c85cac268b2ecd6a68df6d07459139de6063f449527`.
- `GreClient.CardData.CardPrintingData` and `GreClient.Rules.MtgCardInstance` from the currently installed SharedClientCore.dll, SHA-256 `aceb0cd5004b322ceb59b28b0ad44ec799b3d47a46666b17a50150da5449e392`. The installation now declares package `0.1.14589.1332174`; it differs from the retained 10/21 evidence. These new types corroborate the architecture across the two artifacts, but are not falsely attributed to the older build or its method tokens.

New reconstructed-file hashes are in `card-contract-reconstruction-hashes.json`. Proprietary reconstruction files remain in the existing ignored directories. This pass reuses retained REA outputs and supplements them with ordinary ILSpy static inspection; it does not claim a connected REA server or new REA Evidence IDs.

## Public documentation for our catalog boundary

The clients' proprietary IDs do not establish Scryfall identity semantics. Scryfall's official [CardFields type definitions](https://github.com/scryfall/api-types/blob/main/src/objects/Card/CardFields.ts) distinguish the printing's `id` from `oracle_id` shared across reprints, plus language, artwork identity and finishes. Its official [CardFace definitions](https://github.com/scryfall/api-types/blob/main/src/objects/Card/CardFace.ts) distinguish split, double-sided and reversible faces. These support retaining our catalog/printing split. Reversible-card per-face Oracle identity is a catalog-ingestion limitation to acknowledge, not a reason to invent a third catalog in this task. Direct docs access returned HTTP 403; the official source repository was found through web search on 2026-10-25.

## Limits and completion criteria

Implemented in this pass: `Cards.CardDefinition.Validate/GetFace`,
`StartingCard.Validate`, and `GameObject.CreateCard`, called by `ZoneSystem.Born`.
`GameObject.Face` and visible Oracle-text projection use bounded face access.
The factory requires positive allocated IDs/timestamps, a nonnegative owner,
matching Oracle identity, usable face data, a defined finish, and an initial
owner-library or shared command zone. It does not check a printing against a
remote catalog, execute descriptive text, or replace legacy event deserialization.
Uniqueness remains the existing state's allocator responsibility.

Verification: 11 new focused cases; deck acceptance 66 rules + 80 API; separate
regressions 1,579 rules + 755 API all pass. Existing privacy/copy/old-log acceptance
was retained. The extra Cards suite is 324 passed / 2 failed, solely stale schema
snapshots retaining the previously removed `paymentOrder` scenario field. No
schema expectations were rewritten. Touched-file whitespace verification passes.
No client edits, UI claims, rule execution additions or commits were made.

Basic construction, data/face access, identity separation and hidden-display boundaries are answered by concrete code. Exact server identity lifetime, shuffle/search/draw algorithms, and server revalidation remain unknown. Neither rendering flags nor ability metadata justify new rule implementations. Method parity here means equivalent basic responsibilities; our explicit validated authoritative construction and existing privacy projections are preferable for this repository because both foreign clients consume externally computed state. That is a design rationale, not a claim of superior unobserved server behavior.
