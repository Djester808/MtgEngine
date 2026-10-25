> Historical evidence from the pre-reset implementation. See [the current models-only checkpoint](FOUNDATION_INTEGRATION.md); execution and replay claims below no longer describe the active code.

# Basic turn and priority contracts

Inspected 2026-10-25 using the reverse-engineer-anything workflow. This pass reuses
retained REA evidence and reads the actual retained ILSpy reconstructions. It does
not claim a new REA session, runtime observation, or access to either game's server.

## Two contracts

`TurnSnapshot` captures the existing `TurnState`: number (zero during setup),
active player, concrete step/main phase, turn-order cursor and existing extra-turn
bookkeeping. It validates referenced seats and concrete step identity. `Step.Main`
is a descriptive trigger selector, not a concrete position in a running turn.
An active player may have departed while their turn is still being finished.

`PrioritySnapshot` separately captures the stored holder, successive passes and
pending chooser. `IsAvailable`/`CanPass` distinguish stored priority bookkeeping
from permission to pass: a pending choice, ended game, departed holder or absent
holder makes it unavailable. This is not a general casting/action legality API.
Neither snapshot mutates state, advances a step, resolves a spell or chooses a
player. Both use existing immutable state; neither is added to persisted JSON.

`TurnSystem.Pass` now enforces the existing command gateway's pending-choice and
game-end boundaries for direct service callers too, and consults the priority
snapshot for the holder check. The pre-fix focused run reproduced both bypasses.
The existing pass-count, living-seat successor, stack resolution, step progression,
extra-turn, cleanup and combat algorithms are unchanged.

## Static evidence and method mapping

| Responsibility | Inspected artifact evidence | Repository mapping and limits |
| --- | --- | --- |
| Turn position | MTGO `Game.cs:476-505` exposes `CurrentTurn`/`CurrentPhase`; `ProcessTurnStepElement` at 2584-2603 assigns received turn/phase and creates a prompt using `PromptedPlayer`. | `TurnState` and `TurnSnapshot` identify position independently of the prompted player. Client receipt is not an authoritative turn algorithm. |
| Active versus priority player | MTGO `Game.cs:532-576` has separate `ActivePlayer` and `PriorityPlayer` properties and separate per-player flags. `HandleGamePlayStatus` at 1584-1597 bounds-checks and resolves a priority index using `PlayersInServerOrder`; active identity is resolved separately at 2121-2122. | Reuse `PlayerId` and validated `GameState.Player` lookup. Do not collapse turn ownership into the priority holder or derive either from display name. |
| Decision versus active player | Retained Arena `GreInterface.ParseFullGameStateMessage`, lines 560-566, assigns turn number, phase, step, next step/phase, `DecidingPlayer` from `DecisionPlayer` and `ActivePlayer` from `ActivePlayer`. | `PendingChoice.Player` is distinct from active/priority identity. Arena's deciding player is not proven to be a priority holder; no such equivalence is inferred. |
| State updates | Arena `GreInterface.cs:176-179` replaces received `TurnInfo` during merge; MTGO receives separate status and turn-step structures. | Keep the local event reducer authoritative. Existing `TurnBegan`, `StepBegan`, `PriorityGranted`, `PriorityPassed`, `PriorityHandedTo`, `ChoiceRequested` and `ChoiceAnswered` own transitions. Foreign transport does not establish a local replay format. |
| Basic operation | Local `TurnSystem.Pass` already records a pass, uses `PlayerTurnOrder.NextLiving`, then advances or delegates to `ResolveTop`; `Game.Apply` already blocks ordinary commands while pending or after outcome. | The fix closes a direct-service boundary gap only. Snapshots add access/validation, not a competing transition owner. |
| Public projection | `ViewProjector.Project` already supplies turn number/step, active player, priority holder and separately redacted pending choice to `GameView`. | Keep that wire contract; focused four-seat assertions protect identity separation and private choice options. No UI changes or live UI claim. |

## Public rules

The official [rules page](https://magic.wizards.com/en/rules), checked 2026-10-25,
links [Comprehensive Rules effective September 25, 2026](https://media.wizards.com/2026/downloads/MagicCompRules%2020260925.txt).
CR 102.1 identifies the active player by turn ownership. CR 117.2e distinguishes
choices made during resolution from priority. CR 117.3a/d and 117.4 distinguish
priority grants, passing and all-player passes; CR 500.1 describes turn structure.
These support separating the concepts, without establishing foreign server code.
The uniform pending-choice guard also preserves this repository's existing gateway
policy; it is not a claim that every possible Magic choice has identical timing.

## Artifact identity

Exact file hashes and sizes checked in this pass are retained in
`turn-priority-contract-hashes.json`. MTGO gameplay DLL matches retained SHA-256
`7b16823fbaa9a965de3b9ee69ba727992a9b7b50d1187defadd9ac5a6c688081`
(build 3.4.159.4708, prior member Evidence
`ev_5c508a6e3992121057eb96955fb197522fc3ed304c4de84eacaa75354286b265`).
The inspected `Game.cs` hash matches `graveyard-contract-reconstruction-hashes.json`:
`17c0bb8cefc655603587f6b6c2e871c6b6ddacef5783a6456d75808cd642a034`.

Arena `GreInterface.cs` matches its retained `reconstruction-file-hashes.json`:
`9caad39b83478dbcc34a9a8825b0ee09ecc049b743610ccfb10082e9104879ba`.
It belongs to earlier SharedClientCore hash
`42f2fb5f062ccc6098182a902d411746070759a404e875b6e0aac44d3b7ccc6e`,
prior member Evidence
`ev_99270613b579e92d6cc431fa8d639036b41fbf27d7ab6d4000c4ef2ea6853b54`.
The currently installed DLL was independently hashed as
`aceb0cd5004b322ceb59b28b0ad44ec799b3d47a46666b17a50150da5449e392`.
The retained reconstruction is not attributed to that newer binary. Proprietary
reconstructions remain in ignored directories. Unresolved decompiler type comments
and unknown server implementation remain explicit limits.

## Verification scope

`TurnPriorityContractTests` uses `Scope=TurnPriority`, separate from deck acceptance.
It covers setup, seat validation, concrete steps, captured immutability, normal
handoff and serialized event replay, step/grant resets, departed active-player
identity, pending/ended direct-pass refusal and four-seat projection privacy.
Existing priority/turn structure tests remain unchanged. Full-suite results are
regression evidence, not proof of complete Magic rules. No card definitions,
keywords, payment, combat, triggers or additional turn mechanics are implemented.

Verification on the concurrently updated checkout:

- Before the guard: 10 passed / 2 failed (`turn-priority-reproduce-2.log`), both
  failures the direct pending/ended pass cases. An earlier attempt encountered
  temporary compile errors in another chat's test; no foreign files were edited.
- Final focused group: 14 passed (`turn-priority-focused.log`).
- Unchanged Deck subsets: 66 rules and 80 API passed
  (`turn-priority-deck-rules.log`, `turn-priority-deck-api.log`).
- Full solution: 1,776 rules, 835 API, 326 Cards, 322 pipeline passed
  (`turn-priority-regressions.log`). These totals include other chats' foundations
  present when this run built, and are not solely this abstraction's tests.
- Targeted whitespace verification passed after normalizing owned-file line endings
  (`turn-priority-format.log`, exit 0). No whole-repository formatting was run.

The initial sandbox runner stalled and was canceled; successful runs used the
approved external .NET runner. No client files, test-scope registry, shared README,
commits, staging or pushes were made by this chat. Parent consolidation owns those.

Owned changes: `MtgEngine.Rules/Turn/TurnSnapshot.cs`,
`MtgEngine.Rules/Turn/PrioritySnapshot.cs`, the seven-line diff in
`MtgEngine.Rules/Turn/TurnSystem.cs`,
`tests/MtgEngine.Rules.Tests/TurnPriorityContractTests.cs`, this report and
`research/turn-priority-contract-hashes.json`.
