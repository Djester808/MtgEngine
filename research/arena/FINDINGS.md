# Installed Arena: rules-facing client investigation

Inspected 2026-10-21 for the independent MtgEngine rules project. Installation: `C:/Program Files/Wizards of the Coast/MTGA`. No application was launched, modified, or connected to an account. Installed files were read statically; one public card-data database was copied into an ignored research snapshot directory.

Follow-up: [Tutorials, local bots and ability metadata](TUTORIAL_BOT_AND_ABILITY_FINDINGS.md) confirms local opponent decision-making connected to remote GRE, and traces the ability importer and relationship graph.

## Identity

- Installed `version` file records package **0.1.14517.1329972**, date string `10/6/26`. This is the installed package declaration, not a check against the latest online release.
- The executable's product-version field identifies Unity **6000.3.14f1**; it is not Arena's game version.
- REA classifies `Core.dll` as **Unity Mono, CIL, AnyCPU**. Its metadata inventory contains 12,560 types and 56,166 methods. This client can be inspected as managed code; no IL2CPP reconstruction was needed.
- Useful assemblies: `Core.dll`, `SharedClientCore.dll`, `Wizards.MDN.GreProtobuf.dll`, `GrpdParser.dll`, and the separately named deck-validation assemblies. File paths, sizes and SHA-256 hashes are retained in `artifact-identities.json`.

## Main conclusion

Arena exposes a richer, strongly typed rules-facing protocol than the MTGO client inspected earlier. It offers concrete examples of action requests, choice continuations, message/state correlation, full and incremental views, and explanatory annotations. The traced client sends decisions to a match service and reconstructs results from GRE responses. This strongly supports remote authoritative game execution; it does not expose the server's actual rules algorithms.

Do not mistake the namespace `GreClient.Rules` or the class `GreInterface` for a complete local Magic evaluator. The inspected methods are translators, state projectors, request handlers and annotation/event converters. Some local computations exist, but a complete card-resolution, layers, replacement-ordering or state-based-action evaluator was not established in the inspected paths.

## Outgoing choices

Observed reconstructed chain:

```text
ActionsAvailableRequest.SubmitAction
  -> PerformActionResp containing selected action
  -> BaseUserRequest.Submit -> OnSubmit callback
  -> GreInterface outgoing queue
  -> MatchManager.Update dequeues the message
  -> GREConnection.SendMessage(ClientToGREMessage)
  -> ClientToMatchServiceMessage containing serialized Protobuf payload
  -> GREConnection.SendNetworkMessage -> IMatchTcpConnection.Send
  -> MatchTcpConnection.Send -> underlying TCP connection Send
```

`ActionsAvailableRequest` copies the available and inactive actions from the received `ActionsAvailableReq`; it does not enumerate the full game's legal actions from scratch. `SelectTargetsRequest` takes target selections from received data and submits `SelectTargetsResp`, followed by `SubmitTargetsReq` when appropriate.

`BaseUserRequest` sets the response's **GameStateId** from the incoming state ID and **RespId** from the incoming message ID. It exposes received cancel/undo permissions. This is a clear example of correlating choices to the state and request that created them. The server's exact stale-response validation policy remains unknown.

Coordinates in saved ILSpy reconstructions: `decompiled/ActionsAvailableRequest.cs`, `BaseUserRequest.cs`, `SelectTargetsRequest.cs`; `MatchManager.cs:608`; `GREConnection.cs:109`; `MatchTcpConnection.cs:136`. `RequestTranslatorBase.SetCurrentRequest` subscribes `OnInteractionSubmitted` to the request's `OnSubmit` callback; that handler clears the current interaction and enqueues the response. This confirms the callback-to-queue link in the chain above.

## Incoming state and annotations

Observed path:

```text
MatchTcpConnection.OnMsgReceived parses MatchServiceToClientMessage
  -> GREConnection handles GreToClientEvent / queues GREToClientMessage
  -> MessageReceived -> MatchManager.OnMessageReceived
  -> GreInterface.OnMessageReceived -> incoming queue
  -> GreInterface.ProcessQueues -> ProcessMessage
  -> full GameStateMessage or merge of incremental state
  -> ParseFullGameStateMessage -> client state and UI/event updates
```

`GreInterface.ProcessMessage` selects request translators by GRE message type. For state messages it records `GameStateId`, keeps the previous state, merges diffs or accepts a full state, and processes transient and persistent annotations. Its reconstructed state includes players, teams, zones, object identities, actions and turn information. Undo updates also have a distinct projection path.

The annotation distinction is particularly useful. `ReplacementEffectAppliedEventParser.Process` reads an incoming annotation's affector, affected objects, ability group ID and damage-replacement flag, then yields client `ReplacementEffectAppliedEvent` values. It **reports** an already-described replacement event; it does not select a replacement effect or calculate a replacement result. Similar names in the type inventory must not be treated as proof of local rules execution.

`ActionsAvailableRequestTranslator` translates received actions and uses supplied auto-tap solutions to decorate ability presentation. `PayCostsRequestTranslator` similarly consumes received cost/auto-tap data. `AffordableActionFilter` invokes a local affordability predicate, but the predicate's complete implementation was not traced here, so the local-versus-remote split of the whole mana solver remains unresolved.

Confirmed local work includes message correlation, constructing responses, request translation, state/diff reconstruction, annotation-to-event conversion and presentation updates. The server-authority conclusion applies to the traced online match path, not a proof about every tutorial, test, mock, bot or offline mode.

## Card database

The installed `Raw_CardDatabase_c344d0dccdfb898a5e3fd4101eedbd48.mtga` is an ordinary **SQLite** file, **259,883,008 bytes**, despite its extension. A read-only inspection of a workspace copy found:

| Item | Count |
| --- | ---: |
| Tables | 17 |
| Cards rows | 27,136 |
| Abilities rows | 21,624 |
| Prompts rows | 3,520 |

These are database records, not counts of unique paper Magic cards. The database's own `Versions` table reports Data `2026.64.0.280` and GRP `280.1327372`.

`Cards` contains printing/group IDs, costs, power/toughness, colors, types/subtypes, face links, ability links, token/conjuration links, digital-only/rebalanced flags, artwork fields and sorting metadata.

`Abilities` contains base IDs, text IDs, categories, ability words, payment types, loyalty/level metadata, embedded/referenced ability IDs, modal relationships, cost types, tags and a `FullyParsed` flag. `Prompts` and localization tables provide presentation text references.

The complete schema has no explicit executable-code or bytecode column. This is structured metadata useful for understanding the client model; the presence of `FullyParsed` does not establish that these rows can execute card behavior or that a complete rules parser ships here. Text fields could encode additional structures and were not exhaustively decoded. The `GrpdParser.dll` type inventory includes XML import/export, metadata tagging, ability remapping and database export helpers; that inventory alone does not establish a runtime Magic interpreter.

Full schema, count queries, five samples of non-prose ability fields, database versions and snapshot hash: `database-schema.json`. Reproduction: `inspect-database.cjs`, using Node's SQLite module with `readOnly:true`. No player logs, account data or authentication files were inspected.

## Comparison and implications for our project

| Concern | MTGO evidence | Arena evidence |
| --- | --- | --- |
| Authoritative online game path | Action messages out, property state back | Typed Protobuf decisions out, GRE state and requests back |
| Choice identity | State timestamp on actions | State ID plus response/request ID |
| Interaction model | Property lists and prompts | Named request/response types for actions, targets, payments and others |
| Explanation of changes | Property/state projection | Explicit transient/persistent annotations and client events |
| Card information | Card.dll mainly renders cards | SQLite card/ability/relationship metadata, with separate client assemblies |
| Complete rules evaluator recovered | No | No |

Arena is especially useful as a reference for our `Command` / `PendingChoice` / `GameView` boundary: explicit continuation IDs, clear choice kinds, stable object references, and an explanatory event stream distinct from state. Our engine still needs independently implemented casting, costs, continuous effects, triggers, replacements and state-based actions. Its published-rule semantics should not be inferred from client display helpers.

Arena's Protobuf namespace includes `Wotc.Mtgo.Gre.External.Messaging`. That is an observed naming fact; it does not prove that Arena and MTGO run identical rules implementations or protocols.

## Evidence and limits

- Core REA identity Evidence: **ev_bd684882bab1bf4f56b9cd432f17edb4669767d2e2420cf96e4eedf3c54633fa**, `core-evidence.json`.
- SharedClientCore identity Evidence: **ev_abe9c0da200151de52762617c060e026fe3026430280be343df4a24a9b092d2e**, `shared-evidence.json`.
- Focused shared members/call anchors: **ev_99270613b579e92d6cc431fa8d639036b41fbf27d7ab6d4000c4ef2ea6853b54**, `shared-members-evidence.json`, complete reported coverage for the selected types. Compact selection: `rules-boundary-evidence.json`.
- Example managed method tokens: `GREConnection.SendMessage` **0x06000416**, `SendNetworkMessage` **0x06000420**, `GreInterface.ParseFullGameStateMessage` **0x060008e4**, `ProcessMessage` **0x06000905**. Tokens apply only to the exact SharedClientCore hash in `artifact-identities.json`.
- REA's SQLite command returned an EPERM filesystem-read error for the copied file. The record is retained in `card-database-evidence.json`; it is not positive schema Evidence. An ordinary read-only SQLite connection successfully inspected the same copy. No client configuration repair was needed.
- ILSpy 11.1.0.9782 reconstructed the selected types with installed dependencies as references. Reconstructed-file hashes are retained in `reconstruction-file-hashes.json`; these representations are not original source or runtime observations.
- No current online release verification, live game capture, server inspection, exhaustive assembly analysis, or execution of recovered code occurred. No production code or tests changed. Database snapshots and reconstructed proprietary code are ignored by Git.

Status: installation and runtime identified; online action/state boundary traced; card-data schema inspected. Exact authoritative rules algorithms and availability of any separate local full evaluator remain unresolved.
