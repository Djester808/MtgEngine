# MTGO action/state boundary and Card.dll findings

Investigation date: 2026-10-21. Client build: 3.4.159.4708.

The inspected client sends gameplay decisions to a game-server message channel and consumes returned game-state data. It also implements substantive selection constraints locally and makes limited optimistic display changes. Card.dll is overwhelmingly a card-rendering library; it does not provide the full card-rules evaluator sought for this project.

The message boundary and the local algorithms below are supported by static code evidence. The conclusion that the server owns authoritative Magic execution is a strong architectural inference from that boundary. The actual server implementation, its internal ordering algorithms, and whether it independently revalidates every check cannot be observed from these files.

## 1. Outgoing decisions: traced to the session send

Observed chain, with no application execution:

```text
Game.ExecuteAction
  -> CardAction.Execute / GameAction.Execute
  -> CardAction.GetMessage constructs NewCardActionMessage
  -> InProgressGameActions.SendGameMessage
  -> Game.Process -> current state processor
  -> InProgressGameConnectedBaseState.SendGameMessageHandler
  -> GsMessageMessage(game token, match ID, game ID, serialized inner message)
  -> IClientSession.Send
```

`NewCardActionMessage` carries the card/object ID, action ID, chosen modes, game-state timestamp, and selected target IDs. Distributed amounts are serialized alongside targets when required. It describes a request rather than locally resolving the spell's consequences. `PromptActionMessage` covers prompt-button decisions; combat allocation is sent as `DamageAssignmentMessage`.

`CardAction.Execute` invokes its base send path **before** applying any permitted local tap/attack display changes. `GameAction.Execute` is metadata token `0x06000cc7`; `InProgressGameConnectedBaseState.SendGameMessageHandler` is `0x06001063` in the exact gameplay assembly below.

Source: REA member Evidence **ev_5c508a6e3992121057eb96955fb197522fc3ed304c4de84eacaa75354286b265**, CIL call edges, selected in `action-state-evidence-selection.json`. ILSpy reconstructions: `decompiled/GameAction.cs:179`, `CardAction.cs:121`, `CardAction.cs:170`, `InProgressGame.cs:47`. Message fields are corroborated by focused ILSpy reconstructions of `NewCardActionMessage` and `GsMessageMessage` in MTGOMessage.dll.

## 2. Incoming updates: state reconstruction and projection

Observed chain:

```text
connected-state GsMessageHandler
  -> Game.GsMessageReceived
  -> GsMetaMessageReceived switches on the inner opcode
  -> GAME_PLAY_STATUS -> GamePlayStatusMessageDetailed
  -> Initialize: assemble fragments, apply optional delta, check checksums
  -> ProcessStateElements
  -> HandleGamePlayStatus
  -> update cards, players, zones, mana, turn/phase, and interaction prompts
```

Specific assignments establish which values arrive as state:

- `HandleGamePlayStatus` assigns `PriorityPlayer` using the message's priority-player index.
- `ProcessPlayerStatusElement` assigns each player's life, hand/graveyard/library counts, clocks, and active player from received fields.
- `ProcessThingElement` copies received property containers into existing objects, or creates client objects from those properties. Subsequent resolution updates their displayed characteristics and relationships.
- `ManaPool` elements call `UpdateManaPoolForPlayer`.
- `ProcessInteractStateElement` reads turn number, phase, timestamp, prompted player and prompt text. Received UI element types determine card selectors, buttons, numeric ranges and other choices.
- `GameCard.ResolveCardActionsHelper` constructs action menus from `ACTION_LIST0` and subsequent property sublists. `ResolveActionTargets` resolves the transmitted `ACTION_TARGETID0` sequence into existing client objects; target-list metadata supplies minimum/maximum counts and additional constraints.

The delta decoder is transport/state synchronization, not a Magic effect interpreter. It uses head/tail flags, previous buffers indexed by priority-player value, and checksums before processing the reconstructed state.

Source: same REA member Evidence and ILSpy reconstruction. Useful coordinates: `InProgressGame.cs:114`, `Game.cs:1394`, `Game.cs:1419`, `Game.cs:1585`, `Game.cs:2063`, `Game.cs:2203`, `Game.cs:2506`; `GamePlayStatusMessageDetailed.cs:32`; `GameCard.cs:3295`, `GameCard.cs:3306`, `GameCard.cs:3322`, `GameCard.cs:3409`. Coordinates refer to the saved reconstruction, not original source files.

## 3. What actually runs locally

| Responsibility | Evidence and conclusion |
| --- | --- |
| Offered actions and initial legal target candidates | Constructed from received property data. Client projection is observed; server production of that data is strongly inferred. |
| Target selection count and membership | `TargetSet.AddTarget` checks membership in the current legal set and enforces maximum count. Minimum count determines whether the set is fulfilled. Confirmed local code. |
| Relationships among chosen targets | `TargetSet.MatchesRequirements` filters against previously selected targets and supplied requirement flags. It handles same/different controllers; shared creature/card/permanent types; equal toughness; differing names; same/different property values; and a maximum sum of supplied property values. Creature-type handling includes changeling. Confirmed local code. |
| Modes and submitted decisions | Client builds target groups, chosen-mode bytes and distributed amounts, rejects unfulfilled target sets, and validates incompatible action modifiers before submission. Confirmed local code. |
| Tap/attack responsiveness | `CardAction` respects supplied flags allowing immediate local tapping or attacking. `TapLocally` and `AttackLocally` mark the object modified and record timestamps. Received properties subsequently reconcile the display. Confirmed local code; this does not establish authority to execute arbitrary game changes. |
| Priority, life, mana, turn/phase, updated object properties | Applied from incoming messages. Confirmed consumption of authoritative-looking state; strong inference that authoritative computation happens remotely. |
| Full casting legality, costs, spell resolution, triggered abilities, replacement/prevention ordering, state-based actions and characteristic layers | No complete evaluator recovered in the inspected action/state paths or Card.dll. These responsibilities most likely live on the server, but their exact algorithms are unavailable. Other uninspected client assemblies are outside this negative search. |

`TargetSet.MatchesRequirements` is method token `0x06000c91` in gameplay assembly SHA-256 `7b16823fbaa9a965de3b9ee69ba727992a9b7b50d1187defadd9ac5a6c688081`. The distinction matters: saying there are *no* gameplay rules in the client would be incorrect. These are reusable ideas for choice validation, not a substitute for a full rule evaluator. Whether the server duplicates every client check remains unknown.

## 4. Card.dll is a rendering assembly

Official artifact: `https://mtgo.patch.daybreakgames.com/patch/mtg/live/client/3.4.159.4708.20261014120111/Card.dll`.

Read-only PE metadata facts, saved in `card-metadata-fallback.json`:

- SHA-256: `de596973043a245f3213383b60c5eca88c818812fcf4ab017587c9308c35c596`.
- Assembly version: 3.4.159.4708; MVID: `9ae9bfd2-237a-4e02-8e1a-631184f7185a`.
- File size: **226,174,976 bytes**. Managed resource directory: **225,709,232 bytes (99.7941%)**.
- **229 type definitions and 2,570 method definitions**, including generated/nested types.
- Two embedded resource containers: `Card.g.resources` and `Shiny.Card.Resources.resources`.
- ILSpy's resource listing reports **3,756 entries**: 3,122 PNG, 359 JPG, 224 BAML interface files, 28 OTF fonts, six TTF fonts, one XAML entry, and 16 entries with no extension.

The code includes `Shiny.Card` frame controls, tooltips, glyph/text layout, card view models, and mechanic-specific rendering helpers. Focused reconstructed classes establish the meaning of misleading names:

- `SagaAbility` stores chapter display data, rules text equality, and hashes; it does not advance chapters or schedule chapter triggers.
- `PrototypeAbility` exposes mana glyphs, color and power/toughness text from supplied attributes; it does not implement alternative casting or characteristic effects.
- `StationAbility` uses charge-level properties and frame/image assets to update presentation.
- `ManaCost` is a color/value data holder, not a mana-payment solver.
- `CardViewModel.DamageLeft` derives toughness minus displayed damage; this is local display arithmetic, not damage application or a lethal-damage state-based action.

The complete assembly was reconstructed to `decompiled/Card.all.cs`, its declared type/method inventory was retained, and its resource list was inspected. Searches for resolution, damage, triggers, replacements, execution and mana-pool terms led to display properties, presentation calculations, and UI behavior rather than a card-rules interpreter. This supports the rendering classification; it is not a proof about every possible semantic path or hidden payload.

**What we can learn:** mechanic-aware presentation models, prompt/choice data, and separation of received characteristics from rendering. **What was not found:** per-card spell implementations or a recoverable complete Magic evaluator in Card.dll.

## 5. Implications for our independent engine

These are design recommendations, not claims that MTGO uses our architecture internally:

1. Keep one authoritative rules executor. Clients submit decisions; they do not submit trusted life totals, resulting zones, or effective characteristics.
2. Build choices from engine state: candidates, count limits, modes, numeric bounds, and relationships among selected candidates. Recheck submitted choices in the authoritative executor even when the UI filters them.
3. Attach a state revision or current choice identity to decisions. MTGO's timestamp fields demonstrate state-associated requests, but its precise stale-request rejection policy is not observable here.
4. Keep display prediction separate from authoritative state, and reconcile it after a response. The observed tap/attack path is narrowly scoped; a full speculative rules engine is unnecessary for it.
5. Project views per player, and make resynchronization explicit. Do not infer hidden information from generic shared state or from the existence of MTGO's per-priority buffers.

This repository already has useful corresponding seams: `Commands/Command.cs` defines `CastSpell`, `ActivateAbility`, and `AnswerChoice`; `Engine/Game.cs` applies commands; `Choices/` handles suspended decisions; and `Views/GameView.cs` projects what a player can see. `AnswerChoice` already carries a `ChoiceId`. We should evaluate and extend these seams rather than transplant a foreign message protocol. No production engine code was changed during this investigation.

Independent regression scenarios worth deriving from published rules: changing eligibility between choice and submission; target combinations sharing types/controllers; cross-group uniqueness and sum constraints; stale/replaced choices; hidden-card visibility; and reconciliation of a rejected optimistic display action. Such tests have not been added or run in this research pass.

## Evidence, tools and limitations

- Existing REA gameplay identity Evidence: **ev_afdf0063e948620c4708651e233186a75f90260906136f45368175471ad01ff4**, `play-evidence.json`.
- Existing REA gameplay members: **ev_5c508a6e3992121057eb96955fb197522fc3ed304c4de84eacaa75354286b265**, `play-members-evidence.json`; focused methods/call edges in `action-state-evidence-selection.json`.
- Additional core member Evidence: **ev_cc5ff09a174ae3394e01be178f546c4e46af67e5fcad5efe71dd15b58d749885**, `core-members-evidence.json`.
- MTGOMessage REA attempt **ev_5780749cae5a3b108ef5229035ec63952aa91b407076809cd6dee1cb111c8e4e** reports **unavailable** member coverage because metadata representation exceeds its 64 MiB budget. It is not positive evidence for message fields. Focused ILSpy reconstruction supplied those observations instead.
- Card.dll exceeds REA 6.7.0's 128 MiB PE snapshot limit. The original failed result is retained in `card-evidence.json`. No unchanged retry was made. `Inspect-CardMetadata.ps1` uses the installed .NET PEReader to read metadata without loading the target, with full results in `card-metadata-fallback.json` and a compact `card-inventory.json`. This is explicitly separate from REA Evidence.
- Workspace-local ILSpy **11.1.0.9782** reconstructed selected gameplay/message classes and the complete Card.dll code. `reconstruction-file-hashes.json` binds the resulting files; they are recovered representations, not original source. Unresolved dependency/type warnings appear in some outputs, so conclusions rely on clear assignments/control flow corroborated by CIL call anchors where available. Reconstructed code was not compiled or executed.
- Downloaded proprietary binaries, reconstructed source, and local tools are ignored by Git. Research notes and static evidence remain available. No runtime game was launched; no account used; no live traffic acquired; no server binary inspected. Server-side algorithm details remain unresolved.

## Requested questions: status

- **Action request path:** answered through serialization and session send.
- **Incoming update path:** answered through message decoding, property updates and interaction projection.
- **Local versus remote responsibility:** local selection/display algorithms confirmed; authoritative server gameplay strongly inferred; exact server rules and validation implementation unresolved.
- **Card.dll contents:** answered by metadata/resource inventory and reconstructed code inspection; presentation helpers found, complete card-rules interpreter not found within this scope.
