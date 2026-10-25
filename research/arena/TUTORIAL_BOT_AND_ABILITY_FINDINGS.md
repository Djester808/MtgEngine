# Arena tutorials, local bots and ability metadata

Follow-up inspection of installed package 0.1.14517.1329972 on 2026-10-21. Same installed artifact identities and card-database snapshot as FINDINGS.md. All observations below are static; no tutorial or bot match was launched and no recovered code was executed.

## Result

**Local opponent decision-making exists. A complete local rules engine was not found in the traced tutorial/practice paths.** The client creates a headless opponent, supplies it with scripted or heuristic decision handlers, and connects both players to the remote GRE match. This distinguishes local AI from local authoritative simulation.

**Ability records contain useful structured semantic metadata and relationships, but no executable card-effect representation was established.** Their importer and consumers provide concrete examples for an independent ability model, not a recovered effect interpreter.

## Tutorial and practice match paths

Observed reconstructed chains:

```text
TutorialLauncher -> NodeNpeStrategy.PlayMatch
  -> CampaignGraphManager.ProcessNode for an available PlayFamiliar node
  -> Matchmaking.SetupBotMatch
  -> InitializeMatchManager creates GREConnection / MatchTcpConnection / TcpConnection
```

```text
Matchmaking.JoinMatch(received match configuration)
  -> player ConnectAndJoinMatch(configured endpoint, match ID)
  -> for MatchType.NPE: CreateNPEClient -> HeadlessClient
  -> for MatchType.Familiar: CreateFamiliarClient -> HeadlessClient
  -> opponent ConnectAndJoinMatch(CreateFamiliar connection config, same match ID)
```

`NodeNpeStrategy.PlayMatch` enters this path for the tutorial's available `PlayFamiliar` node. `Matchmaking.JoinMatch` explicitly branches on `MatchType.NPE`, creates tutorial-specific request handlers, and connects the opponent through `FamiliarConnection`. That method constructs a real `TcpConnection`, `MatchTcpConnection`, and `GREConnection` with `ClientType.Familiar`. For practice/familiar matches it instead supplies a `HeuristicStrategy` built from deck, attack and block configuration.

`HeadlessClient.ConnectAndJoinMatch` calls the injected GRE connection's `Connect`. Its `Update` method processes received messages, processes `GreInterface` queues, sends queued `ClientToGREMessage` responses, and replaces its view with `GameStateUpdate.NewState`. It supplies the resulting state and pending requests to its strategy. It does not run a recovered authoritative rules evaluator in this path.

The separate `AwsBotMatchServiceWrapper.AIBotMatch` entry constructs `IssueAiBotMatchReq` with player deck ID, optional opponent-deck ID and match type, and sends `CmdType.EventAiBotMatch` through the front-door connection. This confirms a backend bot-match request, but should not be mistaken for proof that all bot decisions run on that backend: the local headless-client paths above show otherwise.

Useful saved reconstruction coordinates: `NodeNpeStrategy.cs:133`; `Matchmaking.cs:168`, `:177`, `:310`, `:438`, `:475`, `:480`; `HeadlessClient.cs:69`, `:101`; `AwsBotMatchServiceWrapper.cs:20`. These are reconstructed-file locations, not original source coordinates.

## What the local opponent actually does

- `CreateNPEClient` supplies `RequestHandlerStrategy` with a tutorial-game-specific factory and random-request fallback handlers.
- `RequestHandlerFactory_NPE_Game1` selects from received target selections, prefers a target marked hot, submits completed selections, assigns a supplied attacker to a supplied legal damage recipient, and chooses blockers from supplied attacker candidates. This is action selection over GRE-provided choices.
- `HeuristicStrategy.HandleRequest` chooses a request handler, applies configurable delays and tracks current state. The heuristic scoring implementation was not exhaustively inspected, so no claim is made that it cannot contain predictive calculations.
- `ActionsAvailableGoldfishHandler` passes if the request permits it; otherwise it submits the first available action. Its name does not indicate a standalone simulator.
- `NPE_Game` and `F1_NPEGame1_Combined` define timed dialogs, tooltips, reminders, pauses and input interceptions keyed to turn/phase/step or observed card events. `NPEDirector` reacts to outgoing messages and incoming game-state updates. These are tutorial guidance scripts rather than implementations of card effects.

**Boundary established:** local scripts/AI choose moves and guide the human; the inspected tutorial/practice implementation obtains game state and legal-choice data through the remote GRE connection. This is a strong architectural conclusion for these paths, not proof that no uninspected debug mode, native component or historical build ever contained a local simulator.

## Ability importer and consumers

`GrpdParser.CardDatabase.GrpdXmlImporter.ParseContentFromGrpdXml` reads catalog XML into localization, ability, card and prompt providers. Its ability loop:

1. Reads ability ID, base ID, optional base numeric parameter, category, subcategory, ability word and payment type.
2. Reads `RulesText` as an integer localization ID, not executable source.
3. Collects mana text, embedded abilities, referenced keywords/ability types, cost types and other metadata, then constructs an `AbilityPrintingRecord`.
4. Reads `IsFullyParsed` from XML, defaulting it to true if the attribute is absent. It does not demonstrate successful local execution or full local rules parsing.
5. Performs some limited text extraction: loyalty cost from localized English text, plus level requirement and power/toughness fields for particular level/station-related categories. These are genuine local parsing operations, but they are narrow metadata extraction rather than general Magic semantics.

`AbilityPrintingData` wraps these records. It resolves base, referenced, hidden and modal relationships lazily; converts stored mana text into mana quantities; checks modal relationships; and identifies basic mana abilities using the mana subcategory and a known-ID set. Its `HasParseFailure` traverses hidden, referenced and modal-child abilities to inspect their imported `FullyParsed` flags. That method is a graph-based status check, not an effect evaluator.

The inspected code does not turn the localized ability text into a general executable effect tree. Nor was an opcode dispatch loop found in the inspected importer/record wrapper. This conclusion is scoped to these consumers and the database schema; it does not exclude additional semantics elsewhere.

## Database evidence

Read-only queries over all 21,624 ability records, saved in `ability-analysis.json`, found:

| Relationship/status | Records |
| --- | ---: |
| Nonzero base ability ID | 1,664 |
| Referenced ability IDs | 3,535 |
| Hidden ability IDs | 1,937 |
| Modal child IDs | 532 |
| FullyParsed flag true | 21,624 |

Counts overlap. Relationship fields are comma-separated numeric IDs in the sampled rows. For example, ability **1013** links to modal children **117144** and **23606**; ability **1206** shares child **23606** and also links child **29759**. These are real graph relationships, not recovered spell instructions.

Base family **166** appears in **511** records with **six** distinct numeric parameters. This demonstrates reuse and parameterization in the stored representation. Its precise rules meaning has not been mapped; it must not be treated as an executable opcode based on frequency or ID alone.

The existing schema includes categories, payment/loyalty data, cost kinds, ability-word fields, references, modal relationships and tags. It has no explicit code/bytecode column. Text fields were sampled and the importer traced, but every tag or category has not been decoded. The database therefore provides a semantic index and client metadata; it has not yielded enough information to independently execute all listed card effects.

Reproduce the analysis with `analyze-abilities.cjs`, which opens the snapshot read-only. The snapshot hash is in `database-schema.json`; no personal account data was queried.

## Lessons for our independent engine

These are recommendations derived from the observations, not claims about GRE's internal implementation:

- Keep **decision agents** separate from **rules execution**. Human UI, scripted tutorial opponents, test bots and heuristic AI can all consume the same offered choices and submit the same commands.
- Keep reusable ability definitions, numeric parameters, modal links and generated/hidden abilities explicit in our model. Shared children should retain identity rather than being duplicated as unrelated prose.
- Keep executable effects separate from presentation metadata. Categories, tags, localized text and cost summaries help display and classify a card but do not supply missing targeting predicates, durations, event triggers, replacement ordering or resolution instructions.
- Track implementation/parse status from our own validation. Arena's imported boolean would be an unsuitable completeness measure for our engine.
- Test a tutorial as a deterministic sequence of commands and expected authoritative states. Local prompts and guidance can react to engine events without becoming another rules implementation.

The remaining implementation gap is still the same: we must author or validate our own effect representation and rules executor. The new evidence makes the client/agent/metadata separation clearer; it does not supply GRE's hidden server evaluator.

## Evidence and limitations

- Bot wrapper, goldfish handler and ability wrapper: REA **ev_5e9a04773e86252fec5e7e1b6960630bae3b9e6ce85dd09cb1333e2faaeab38a**, `bot-ability-members-evidence.json`, complete reported coverage for selected types.
- Headless client and request strategy: REA **ev_696f6ddcb5fa7275d7f248560fe14aa52c5b7244ba14d4959f4780f878a4f5ca**, `headless-members-evidence.json`, complete reported coverage. Its CIL anchors corroborate connection, queue processing and response send.
- Ability importer: REA **ev_2260c05b7240dd2cef9aa9a4dfd067b3137f8891f26a00a6e36468de15058c3e**, `importer-members-evidence.json`, complete reported coverage for selected type.
- Core tutorial member attempt: REA **ev_e723b0ec7b0c17999878ab2f12e108865fb8fe5c48c7dbdedee4db6161c92c92**, `tutorial-members-evidence.json`, reports unavailable member coverage because its metadata representation exceeds the 64 MiB budget. It is not positive member Evidence. Core identity remains bound by the earlier `core-evidence.json` and artifact hashes; ILSpy reconstruction supplied the tutorial-path observations. No unchanged retry or client configuration change was made.
- Updated reconstruction hashes are stored in `reconstruction-file-hashes.json`. Generated source and database snapshots remain Git-ignored. No production source or tests changed.

Requested questions: **tutorial and practice paths traced**; **local AI confirmed**; **a local full rules engine not established within this scope**; **ability import, metadata graph and representative consumers inspected**; **complete executable card logic not recovered**.
