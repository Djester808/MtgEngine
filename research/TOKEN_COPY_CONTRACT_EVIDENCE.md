> Historical evidence from the pre-reset implementation. See [the current models-only checkpoint](FOUNDATION_INTEGRATION.md); execution and replay claims below no longer describe the active code.

# Token and copy contract

Inspected 2026-10-25. This pass applies the reverse-engineer-anything evidence workflow to retained client reconstructions. It does not claim new REA analysis, runtime gameplay observation, or access to either server. The local abstraction is already present in `GameObject`, `StackEntry`, `CopiableValues`, `CopyRules` and `ZoneSystem`; another token catalog or mutable copy store would duplicate it.

## Contract and method mapping

| Representation | Existing construction and access | Identity and lifetime boundary |
| --- | --- | --- |
| Token permanent | `ZoneSystem.CreateToken(TokenSpec, ...)` builds a definition from the supplied characteristics; the `CopiableValues` overload serves the interpreter's `CopyToken` path. `Arrive` uses the existing event/replacement pipeline. Access uses `GameState.Objects`, battlefield membership, `GameObject.Face` and `IsToken`. | A new `ObjectId`, owner/controller and timestamp; no `CardInstanceId` or `DeckSelection`. A copy token takes characteristics, not source counters, damage, commander designation, owner, printing or finish. Existing movement retains token status, and the existing state-based action removes it outside the battlefield. |
| Copied spell or ability | `EffectInterpreter.CopyOnStack` calls `CopyRules.NewCopy(..., isToken: false)` and attaches the source `StackEntry` with `IsCopy = true`. `AbilityIndex` distinguishes an ability from a spell; source, targets, modes and X remain on the stack entry. | Separate object identity, no physical-card identity or selected printing. Copy creation is not a cast. Ability and spell resolution remain existing casting/interpreter responsibilities; this pass does not expand them. |
| Copy of a card | `ZoneSystem.CopyInPlace` uses `CopyRules.CopyOf` and `NewCopy`, adds `ZoneSystem.CopyOfCard`, inserts beside the source when in the same owner zone, and publishes `ObjectCreated`. Missing source is an unchanged outcome. | It is not a token or a new physical card. Ownership routes private zones through existing `KeyFor`. The source is untouched. The marker must survive movement so the existing cessation check can recognize it. |
| Copied characteristics on an existing object | `CopyRules.Become(target, values)` replaces definition, face index and face-down data. | The target keeps its own object and physical identity, selected printing/finish, ownership/control, counters, damage, token status and commander status. It does not acquire the ephemeral copy-of-card designation. This pure operation is not itself a new public command or an event-producing copy effect. |

The catalog `Domain.Models.CardDefinition`, executable/printed `Cards.CardDefinition` and `Face`, quantity-bearing `CollectionCard`, and starting selections retain their existing roles. No new named production definitions, catalogs, replay discriminators, persisted derived properties or client fields were added. Text remains descriptive. Hidden-information authorization remains in projection; a server-side copy operation does not grant inspection.

## Concrete foreign-client evidence

| Concern | Inspected static code | Interpretation and limits |
| --- | --- | --- |
| Distinct object categories | MTGO `GameCard.GameObjectType` lists `AbilityOnStack`, `Card`, `Copy`, `Emblem`, and `Token` (`research/mtgo/decompiled/GameCard.cs:83-94`). `ResolvePropertiesButDeferUpdatingAssociations` reads `GAME_OBJECT_TYPE` at line 1993. | Object category is distinct from a card definition. This supports existing orthogonal token/stack metadata; it does not require copying MTGO's enum into our state. |
| Copied characteristics versus object type | MTGO sets `IsClone` from either `GameObjectType.Copy` or `IS_CLONED_PERMANENT` (`GameCard.cs:2218`); `IsCopyOfMorph` is separately derived at 2220. `SetNameOfUnusualToken` reads the definition's token flag and optional name property (`1802-1814`). | A UI clone flag can cover both a copied object and a permanent with copied characteristics. It cannot substitute for persistent physical identity or establish copy-effect expiry. |
| Instance, definition and ownership | Retained Arena `GreInterface.ParseFullGameStateMessage` assigns `InstanceId`, `GrpId`, `ObjectType`, controller and owner separately (`research/arena/decompiled/GreInterface.cs:331-338`), visibility at 343 and `IsCopy` at 357. It looks up printing by `GrpId` at 366 and only adds ordinary Card objects to seen-card IDs at 369-375. | Incoming authoritative state supplies these distinctions. This is evidence for representation, not the server's allocation, privacy, or copy algorithms. |
| Token lifecycle display | Retained Arena annotation processing groups `TokenCreated` with ability creation/reveal, resolves affected IDs, and groups `TokenDeleted` with ability deletion (`GreInterface.cs:2363-2381`). | The client can display creation/deletion separately from transfer. Our existing creation, movement and cessation events are the appropriate local equivalents; timing and replacement behavior cannot be inferred from those annotations. |
| Current Arena copy fields | Current `MtgCardInstance` has `ObjectType`, `IsCopy`, `IsObjectCopy`, `CopyObjectGrpId`, owner and controller (`research/arena/decompiled/MtgCardInstance.cs:22-51`). | Concrete fields corroborate separate data responsibilities. No producer semantics are asserted for the two additional current-build copy fields. |
| Display clone is not game creation | Current `MtgCardInstance.GetCopy` uses memberwise clone plus `CopyValuesTo` (`258-263`). `GetExamineCopy` clears instance IDs, damage, tapped and summoning-sickness display data (`265-298`). | These are presentation copies, not a server token factory. Using them as a live identity algorithm would be incorrect. |

The method mapping is architectural inference from these observed assignments and call paths. Exact server copy selection, copiable-value evaluation, allocation, lifecycle timing, and complete rules parity remain unknown.

## Provenance

`token-copy-contract-hashes.json` records freshly calculated SHA-256 and byte lengths for the inspected reconstructed files and available binaries. Reconstructed file hashes match the earlier card evidence manifests:

- MTGO `GameCard.cs`: `a38135cbc0fb0dcfe1e3388a411479d7ad3776560324793b2ee68632b2fd9641`.
- Retained Arena `GreInterface.cs`: `9caad39b83478dbcc34a9a8825b0ee09ecc049b743610ccfb10082e9104879ba`.
- Current Arena `MtgCardInstance.cs`: `fac735818cddd576a87e56a1a836e7ac7ace26f901e04db0e717750f0c1cbe77`.

Retained MTGO gameplay assembly (build 3.4.159.4708) hashes to `7b16823fbaa9a965de3b9ee69ba727992a9b7b50d1187defadd9ac5a6c688081`; member Evidence is `ev_5c508a6e3992121057eb96955fb197522fc3ed304c4de84eacaa75354286b265`. Retained Arena member Evidence `ev_99270613b579e92d6cc431fa8d639036b41fbf27d7ab6d4000c4ef2ea6853b54` binds SharedClientCore `42f2fb5f062ccc6098182a902d411746070759a404e875b6e0aac44d3b7ccc6e`. The currently installed SharedClientCore instead hashes to `aceb0cd5004b322ceb59b28b0ad44ec799b3d47a46666b17a50150da5449e392` (prior reconstruction package declaration 0.1.14589.1332174). `MtgCardInstance` is from that current artifact; `GreInterface` remains older evidence. No build-local tokens or behavior are silently attributed across builds.

No REA tools are connected in this session. Retained REA metadata and ordinary static reconstruction inspection were sufficient; no registration changes, installation or fresh Evidence IDs were needed. Proprietary reconstructed files remain in ignored research directories. Unresolved-type decompiler comments and uninspected server code limit parity claims.

## Public rules boundary

Checked the official [rules page](https://magic.wizards.com/en/rules) and its [September 25, 2026 Comprehensive Rules](https://media.wizards.com/2026/downloads/MagicCompRules%2020260925.txt) on 2026-10-25. CR 111.6 distinguishes tokens from cards; 111.7 and 704.5d govern token cessation. CR 707.2 separates copiable characteristics from status and counters. CR 707.10 distinguishes copied stack objects from casting, and 707.12 describes creating a copy of a card in its source zone. CR 704.5e supplies the copy-of-card lifetime boundary used by the existing state-based check. These rules resolve semantics that client rendering does not establish.

## Reproduced gap and correction

`CopyInPlace` set the existing copy-of-card designation, but `ZoneSystem.Reborn` dropped every designation. A copy moved to hand, exile or graveyard therefore became indistinguishable from an ordinary non-token to the existing cessation check. The focused regression first failed in all three destinations: the arrived copy remained after state-based actions.

The correction preserves only `CopyOfCard` during rebirth; other designations still expire. It retains the existing allocator, movement events, owner routing, token flag, physical identity and deck-selection behavior. Cessation remains in the existing state-based action. Comments on `CopyRules`, the marker and the cessation reader now distinguish new ephemeral copies from copied characteristics.

## Verification and limitations

`TokenCopyContractTests` has ten `Scope=TokenCopy` cases. They cover token creation and four-seat public access, copy-token status/identity isolation, copied-characteristic identity preservation, copy-of-card ownership and cessation, three cross-zone copy lifetimes through serialized replay, token departure/cessation replay, and both spell/ability stack-copy choice representation and replay. Existing acceptance assertions were not changed.

- `token-copy-reproduction.log`: 7 passed, 3 failed before the movement correction.
- `token-copy-focused.log`: 10 passed after it.
- `token-copy-deck-rules.log` / `token-copy-deck-api.log`: unchanged Deck acceptance, 66 rules and 80 API passed.
- `token-copy-broad.log`: 1,776 rules, 835 API, 326 Cards and 322 pipeline passed. The shared checkout included concurrently completed foundation tests; these are checkpoint totals, not a measure of full Magic support. Transient output-DLL locks from concurrent test hosts retried successfully.

Parent integration subsequently reported a clean authoritative Release build (zero warnings/errors) and the same full-suite results: 1,776 Rules, 835 API, 326 Cards and 322 Pipeline. These are parent-reported integration results, not a second run by this chat. Parent normalized `TokenCopyContractTests.cs` from LF to CRLF after the formatting gate identified its line endings; the formatting recheck was still pending at this handoff.

No client changed and no UI verification is claimed. No staging, commit or push was performed by this chat. Parent integration owns consolidated test-scope documentation and final review.

Limits: the ten cases do not certify all copy effects, copy exceptions, face-down copiable-value rules, characteristic-effect expiry or restoration of a real card's printed characteristics after departure. They do not certify every copied spell's resolving, countering or off-stack lifecycle, permanent-spell-copy conversion into a token, or the prohibition on a departed token moving again before the next state-based check. Those existing rules-system behaviors are not implemented or expanded here. No ability, casting/payment, trigger or replacement mechanics were added. The correction is specifically the demonstrated copy-of-card marker loss, and existing token cessation is preserved.
