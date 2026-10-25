# Independent Magic engine comparison and implementation plan

## Start with the table and zones

The current implementation sequence starts with [Board and zones for two to four players](BOARD_AND_ZONES.md). Establish stable seats, the seven-zone topology, card versus object identity, membership/order invariants, and per-viewer visibility before connecting a board to game execution. The initial multiplayer mode is free-for-all with attack-multiple-players and unlimited range; format selection remains separate. The document defines layouts, existing-code gaps, and acceptance scenarios. Earlier audit findings remain the evidence for the execution work that follows this foundation.

## Establish the baseline

The inspected checkout is on **card-engine**. It contains tracked card vocabulary, rules, pipeline, API session and game hub code. The solution includes the engine projects, the API references them, and Program.cs composes `Subsystems.Wave2` and maps `/hubs/game`. The README and engineering guide say the old engine was removed; they do not describe this branch's current wiring accurately. There are also staged and unstaged engine changes, so implementation must preserve the existing work and establish which revision is being validated.

The next step is incremental correctness work on this audited baseline. Existing code is a candidate for reuse; its presence does not prove general rules conformance. The initial source comparison has now been supplemented by the build, test and behavioral audit below. Live client gameplay remains unverified. Historical counts in CARD_ENGINE_PLAN.md should not be used as current coverage measurements.

Arena and MTGO establish useful client boundaries, not a recovered authoritative rules algorithm. Both send decisions and receive state; Arena additionally exposes local opponent agents and structured ability metadata. Implement our rules independently from the published rules and our own scenarios.

## Baseline audit results and revised order

The [executed baseline audit](baseline-audit/AUDIT.md) changes the implementation order below. The solution builds with zero warnings/errors; 1,339 Rules, 321 Cards and 322 Pipeline tests pass. The API suite passes 698 of 699, with an unrelated registration assertion failing. A copied 632-card corpus yields 500 passing verifications, six scenario failures, 69 without scenarios and eight unsupported candidates; 49 disputed files are outside the verifier's candidate set.

More importantly, **nine of twelve independent conformance probes fail**. Ordinary stack storage and LIFO resolution work, but response/mana actions preserve stale passes; the first draw step and empty-combat steps are not actually skipped; cleanup skips a required trigger/priority checkpoint; required modes can be omitted; and combat/once-per-turn history is incomplete. Green existing tests do not establish correct general game sequencing.

The immediate order is therefore **core game-flow repairs, then combat/trigger/layer correctness, then reliability and interface improvements**. The detailed required flow is in [the two-player checklist](baseline-audit/TWO_PLAYER_GAME_FLOW.md); [the complete register](baseline-audit/RULES_REGISTER.md) tracks every rule group and keyword/action section without implying full coverage.

### Components to retain and audit

| Area | Existing source | Comparison and action |
| --- | --- | --- |
| Executable ability definitions | `MtgEngine.Cards/Abilities.cs`, `Effects.Structure.cs`, verb/action records | We already have an effect representation that the inspected commercial clients did not reveal. Preserve it and improve identity, validation and reuse. |
| Reusable parameters and templates | `KeywordAbility` parameters, `Cards.Pipeline/Templates/` | Parameterized keywords and basic templates exist. Expand selected families after their semantics are verified; do not introduce a second effect language. |
| Authoritative command execution | `Rules/Engine/Game.cs`, `Commands/Command.cs` | A single front door already dispatches casting, combat, choices and priority. Keep this composition. |
| Legal action offers | `Rules/Legal/LegalActions.cs`, `Views/GameView.cs` | Existing offers reuse execution checks, but the offer shape is coarse and enumeration is narrower than the executor. |
| Choices | `State/GameState.cs`, `Choices/ChoiceSystem.cs` | Choice ID, player ownership, counts, option membership and duplicate checks already exist. Add structured requests, relationships and durable typed resumption. |
| Resolution and interaction | Interpreter, replacements, triggers, layers, zones, SBA and turn systems | All have real implementations in Wave2. Audit interaction behavior rather than assuming missing subsystems must be written from scratch. |
| Visibility and replay | `Views/ViewProjector.cs`, `Events/EventLogSerializer.cs`, reducer | Per-player views and event persistence already exist. Preserve their separation from internal state and strengthen reproducibility. |
| Session serialization and persistence | `Api/Services/GameSession.cs`, `GameSessionService.cs` | A per-table semaphore serializes commands. Durability and revision semantics need improvement around this existing boundary. |
| Card admission and verification | `Cards/Files.cs`, `Pipeline/Scenarios/VerifyStage.cs`, `Api/Services/CardFilePool.cs`, `DeckGate.cs` | Status, provenance, Oracle hashes and scenario verification exist. Current acceptance needs stronger ties to the code and exact definition that were tested. |

## Highest priority gaps

### Verification can outlive the implementation it supposedly verifies

`EventLogSerializer.EngineHash()` hashes assembly version plus event type names. A rules behavior change that alters neither can retain the same hash. `Verification` stores this engine hash and scenario totals, while `CardFilePool.StatusOf` returns the saved status directly. DeckGate admits a saved Verified status without independently establishing freshness.

Introduce a reproducible engine build fingerprint, definition hash, vocabulary/schema version, scenario-suite hash and rules-release identifier. Compute one effective readiness verdict at the pool/admission boundary; keep DeckGate's simple enforced/manual/refused decision. A stale verdict should have an explicit reason, and existing games should retain their pinned definition bundle rather than silently adopting new data.

### Execution limits can conceal incomplete processing

`Game.Settle` stops after 1,000 rounds and returns its accumulated state without signaling limit exhaustion. `ReplacementSystem.Continue` caps at 64 iterations and can then apply the current event even if further replacements remain. These are confirmed code paths; whether reachable with current cards needs regression scenarios.

Return a distinct engine failure when a guard is exhausted and preserve the last committed state. Add diagnostics identifying the subsystem and repeated state/event context. A safety limit must not be reported as a successfully settled game. Budget exhaustion also must not be automatically adjudicated as a rules-defined draw.

### Durable command handling is incomplete

`GameSession.ApplyAsync` updates in-memory State and Log before awaiting persistence. A persistence failure therefore risks leaving the live table ahead of its durable record. The inspected session path has no explicit command ID or expected session revision. PendingChoice IDs already reject obsolete answers, but they do not replace session-level duplicate and stale-command handling.

Under the existing table lock: validate a command envelope, compute a candidate result, persist it atomically with its revision and command receipt, then publish the new state. Define duplicate-retry behavior and reconcile uncertain commit outcomes. Keep account-to-seat mapping at the session boundary. Enforce optimistic concurrency in durable storage as well as in process if more than one server may host a table.

### Offered actions do not fully describe executable commands

`LegalActions.For` enumerates casts and lands from the player's hand, and activation offers from permanents. The casting system supports other zones and face-dependent permissions. Offers also contain mostly object IDs and ability indexes rather than faces, cost alternatives, modes or target-group requests. In addition, `CastingSystem.Check` invokes the real Cast/Activate/PlayLand paths with partially populated commands; parameter-dependent discovery and complete-command validation should be distinguished and tested. The audit did not demonstrate a failure for its ordinary required-target offer case.

Introduce a noncommitting action planner that describes what can be attempted and what information is still required. Execution must use the same planner/validators with complete parameters. An offered action is not a guarantee that every target or payment selection will succeed. Extend candidate enumeration to supported zones, faces and ability locations using the executor's permissions, not independent UI rules. The independent ordinary targeted-spell offer check passed; do not classify all target-requiring offers as broken. More complex parameter-dependent discovery needs its own cases.

### Ability and continuation identity rely on mutable positions

Abilities are inline records, and `ActivateAbility`, `StackEntry`, and `ChoiceContext` use ability indexes. The interpreter also handles dynamically appended delayed-trigger definitions. Reordering abilities, granting abilities or changing characteristics makes index meaning dependent on the exact definition and evaluation context. This is an identity risk to address with targeted cases, not a claim that every indexed reference is currently wrong.

Add stable definition-level ability IDs, effect-node IDs and runtime ability-instance IDs. Capture the relevant ability definition when creating a stack entry or durable continuation, with last-known information where the rules require it. A runtime instance should retain source object identity, controller, definition version and grant origin. Merely hashing the printed ability text is insufficient because identical abilities can be distinct instances.

`PendingChoice` currently uses a string kind and `ChoiceContext.Extra` string values to locate resumption work. Preserve the existing serializable approach, but replace one continuation family at a time with explicit typed state. Do not replace it with captured delegates or restart every effect from the beginning.

## Implementation phases

### Phase 0 Confirm and measure the chosen baseline

Record the branch, commit and working-tree state without resetting changes. Build the intended engine composition and run existing Cards, Rules, Pipeline and relevant API tests. Check whether Wave1/stub compositions are reachable outside tests. Resolve documentation that says the engine is gone only after identifying the intended active architecture.

Produce a baseline matrix of executable vocabulary operations, supported choice kinds, card readiness, known refusals and interaction tests. Distinguish tests that exist from tests that pass. Keep the current rules document as the authoritative reference and record its release identity; do not hand-edit it.

Status: the first baseline run is complete and recorded in baseline-audit/AUDIT.md, with suite results, a card-corpus rerun, twelve additional probes, a full game-flow checklist and the numbered-rule register. The nine independent probe failures must become ordinary regressions; the six card scenario failures still need engine/definition/fixture triage. A live client playthrough remains unverified. If the intended product baseline removed these projects, treat their code as recovery candidates and port only validated components into the chosen architecture.

### Phase 1A Repair core two player execution

Promote the nine independent failing expectations without weakening them. For response spells and mana activations, invalidate prior consecutive passes when an intervening action completes while retaining the appropriate player's priority. Skip entire first-draw/empty-combat steps before generating events or priority. Implement repeatable cleanup with SBA/trigger handling before the next turn. Require modes and other casting announcement choices before a spell is complete on the stack.

Then preserve first-strike eligibility history, honor trample after blockers leave, and record once-per-turn triggering at occurrence rather than inferring it from resolution. Add command-driven reproductions of the isolated combat/history probes. Audit APNAP trigger placement, SBA timing, simultaneous damage and nested replacement choices together. Implement missing text-changing/dependency behavior or explicitly refuse affected shapes before they are offered as enforced.

Acceptance: all twelve audit probes pass; a complete two-player command trace includes mulligans, skipped first draw, mana, a spell/response/counter-response chain, target changes, an activated ability whose source leaves, both ordinary and first-strike combat, a cleanup trigger and a game-ending outcome. Save/load at every decision must preserve the result. Passing old tests alone is insufficient, because some old turn expectations encode the incomplete behavior and must be corrected from the rules.

Priority: this phase comes before definition-library expansion and before treating current Verified statuses as general gameplay readiness.

### Phase 1B Make readiness and execution outcomes trustworthy

Implement build/definition/scenario fingerprints and effective readiness. Add failures for settle/replacement guard exhaustion. Make session state publication follow durable commit, with expected revision and command ID. Return structured result categories for applied, refused, stale, unsupported and engine fault; retain detailed internal diagnostics separately from player-facing messages.

Tests: behavior changes invalidate readiness even when event names do not change; old definitions stay pinned in existing games; exceeding each guard leaves no committed partial result; failed persistence leaves memory and durable history consistent; repeated commands have exactly one effect; stale commands cause an explicit rejection/resync.

Exit condition: no known path in these boundaries reports partial or unverified execution as a normal success.

### Phase 2 Stabilize definitions and ability references

Retain current effect records as the canonical executable language. Add optional stable IDs through a versioned migration; identify runtime grants and delayed triggers explicitly. Build a validated, immutable definition bundle that the interpreter can still read through an adapter, avoiding a wholesale rewrite.

Extend the current linter with graph-reference, effect/selector compatibility, supported-operation and cycle checks. Use our existing typed Amount, Cost, Condition and Selector records for template parameters. Expand a small catalogue of generic semantic families in the pipeline, compile them into normal effect trees, and store provenance/version/hash information. Cards remain data; no card-name special cases enter Rules.

Tests: JSON migration and deterministic expansion; renamed/reordered authoring records retain correct identity; two identical granted abilities remain distinct; removing a source does not destroy a stacked ability or required last-known information; changed template dependencies invalidate affected definitions.

Exit condition: each runtime ability and paused operation can identify the exact executable definition it belongs to.

### Phase 3 Unify action plans and typed choices

Add an action-plan response containing the object/face, ability identity, supported cost alternatives and required parameter schemas. Initially support ordinary casting, land play and activation, then expand to supported non-hand zones and permissions.

Introduce typed choice payloads for targets, modes, ordered objects, numeric values, distributions and payment. Keep ChoiceId and player validation. Derive candidates and constraints using shared engine queries. Relationship constraints should support such cases as shared types, different controllers, cross-group uniqueness and bounded totals. Existing predicate records such as SharesTypeWith should be reused where they express the rule; assess genuinely missing group constraints before extending the vocabulary.

The API projects a safe choice DTO, not raw ChoiceContext or proposed internal events. Large searches can page a view of an engine-owned candidate set while submission validates against authoritative state. Clients and bots may filter for usability, but the executor rechecks everything. Request revision policy must distinguish a genuinely invalidated choice from unrelated permitted changes such as concession.

Tests: offers include permitted graveyard/exile casts and appropriate faces; offered action requirements can be completed; impossible combinations fail in the shared validator; protected/changed targets are rechecked at resolution; wrong, duplicate and obsolete choice answers remain rejected; opponent views reveal no private candidates or continuation internals.

Exit condition: a client can complete supported commands using offered schemas without recreating Magic rules or guessing dictionary keys.

### Phase 4 Make paused execution reproducible

Migrate the highest-risk resumption paths first: replacement choice inside damage resolution, multi-effect sequences, payment choices and simultaneous trigger ordering. Introduce serializable execution frames recording remaining instructions, bindings, chosen modes/targets, proposal state, already-applied replacements and the enclosing batch.

Preserve the existing algorithms while replacing fragile locator fields. Audit the complete sequence of command, replacement/prevention, event application, state-based actions, trigger collection/ordering and priority. The commercial clients do not establish that ordering; use rules-derived scenarios to determine it. Verify no priority appears inside a resolving spell or unfinished batch.

Tests: repeated pause/save/load/resume does not pay costs or apply effects twice; sequences resume after the exact completed instruction; a second replacement choice retains the rest of the damage batch; trigger order follows player ordering rules; execution from the same setup, seed and commands produces the same result. Separately test event replay and command re-execution, since an event log can replay consistently even when the original rules decision was incorrect.

Exit condition: representative interaction-heavy games can be stopped at every choice and resumed with equivalent authoritative results.

### Phase 5 Add independent agents and explanations

Expose one decision-agent interface over each player's safe view, action plans and pending requests. Implement a deterministic scripted agent first, then a simple heuristic opponent. Both submit ordinary commands. Bot heuristics must not become another source of legality. Tutorials provide hints and expected decisions over real engine events.

Add player-safe explanatory annotations linked to committed event IDs: costs paid, replacements chosen, targets becoming illegal and resolution outcomes. Keep display annotations separate from state-changing events, while preserving current domain events. Start with full projected views and revisions; add deltas only if measured performance justifies them, with an explicit resynchronization route.

Exit condition: human, scripted and heuristic seats complete the same supported scenario deck without special execution paths, and explanations do not leak hidden information.

### Phase 6 Expand supported cards by verified interactions

Choose a small, explicit benchmark pool spanning ordinary effects, targeted/modal spells, triggered/static/replacement abilities, multi-face cards and zone permissions. Increase it by semantic families and interaction coverage, not by a target count of translated files. Keep unsupported effects explicit and preserve the existing enforced/manual/refused admission policy.

Use hand-authored expected results from published rules and rulings alongside generated scenarios. Add adversarial cases and invariant/property tests for conservation, object identity, visibility, replay and command/offer agreement. A card needs ordinary, refusal, interaction and resumption cases where applicable. Passing a generated happy-path scenario alone should not be treated as proof of full card correctness.

Exit condition: a documented benchmark pool passes its rules-derived interaction suite on the pinned engine/definition bundle and produces a repeatable full game through the intended API/client path.

## Recommended first implementation batch

After the completed baseline run, first repair the pass-history boundary and actual skipped-step transitions. Follow with cleanup/announcement timing, then the independently reproduced combat and trigger-history defects. Recheck the combined SBA/trigger/layer checkpoint before expanding card coverage. Correct verification fingerprints, execution guards and durable publication next, followed by stable ability identity and the shared action planner. This revised ordering addresses demonstrated failures in ordinary two-player play before architectural expansion.

Do not begin by copying Arena's database, reproducing its Protobuf protocol, expanding to the full card catalogue, or replacing the current engine wholesale. Our existing effect vocabulary is already the foundation we need. The commercial-client evidence is most valuable for improving its boundaries and models.

## Sources

Local comparison sources are named in the tables and findings above. Commercial evidence and limits are in `arena/TUTORIAL_BOT_AND_ABILITY_FINDINGS.md`, `arena/FINDINGS.md` and `mtgo/ACTION_STATE_AND_CARD_FINDINGS.md`. CARD_ENGINE_PLAN.md supplies the existing card-as-data, closed-vocabulary, scenario-verification and admission requirements. Exact hidden server algorithms were not recovered and are not prerequisites for this independent implementation.
