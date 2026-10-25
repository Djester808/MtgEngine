# Existing engine baseline audit

## Conclusion

The current branch contains a substantial executable engine, including a real stack. It is **not a conformant general two-player Magic engine yet**. Ordinary stack operations and scripted games work, but independent checks found defects in basic priority and turn sequencing, plus combat and trigger edge cases. The immediate task is correctness of the core game loop, not more translated cards or a larger ability catalogue.

The stack is not missing: ordinary two-pass resolution and last-in-first-out behavior passed independent controls. The important distinction is that a functioning container and a successful scripted game do not establish correct opportunities to respond in every game.

## Scope and reproducibility

- Audit date: October 21, 2026; checkout: `card-engine`; HEAD at start: **8a72e856f3716dd7ea8cbd55b7c84bab09c119a5**.
- This was a dirty working tree, with 629 Git status entries at the start. The build includes staged, unstaged and untracked source, not merely HEAD. `working-tree-before.txt` records the starting inventory. Existing work was not reset.
- SDK: .NET **10.0.204**. Build: Debug, solution-wide, no restore, serial MSBuild, build servers disabled.
- Rules reference: repository Comprehensive Rules effective **August 7, 2026**, hash in `inventory-summary.json`. This is a pinned baseline, not the latest-rule claim. Wizards published later updates, including an [October 19 update bulletin](https://magic.wizards.com/en/news/announcements/mystery-booster-commander-edition-update-bulletin); the [official rules page](https://magic.wizards.com/en/rules) is the source for a future version comparison. No live rules asset was replaced.
- Existing unit/integration suites, a copied card corpus and isolated independent audit probes were run. No live browser match, deployment or full product UI verification was performed.
- Production source and original `cards/` files were not changed for the audit. The probe program lives under `research/baseline-audit/probes`; card verification writes only to the ignored `card-snapshot/` copy.

## Build and existing test results

The solution builds with **zero warnings and zero errors** using:

```powershell
dotnet build MtgEngine.sln --no-restore --disable-build-servers -m:1 -v normal
```

The sandboxed parallel build failed without a compiler diagnostic, and sandboxed test hosts could not connect to their test processes. Running the test hosts outside that restriction resolved communication. These environment failures are preserved in the logs and are not counted as engine assertion failures.

| Suite | Passed | Failed | Skipped | Total |
| --- | ---: | ---: | ---: | ---: |
| Rules | 1,339 | 0 | 0 | 1,339 |
| Card vocabulary | 321 | 0 | 0 | 321 |
| Pipeline | 322 | 0 | 0 | 322 |
| API | 698 | 1 | 0 | 699 |
| Total | 2,680 | 1 | 0 | 2,681 |

The API failure is `AccountManagementTests.RegistrationRequiresTermsAcceptance`: the test expected InvalidRequestException, but no exception was thrown. It is a real failing test in the current tree, outside the engine behavior audit. It was recorded rather than fixed incidentally.

Machine-readable counts: [test-summary.json](test-summary.json). Each successful test invocation produced a TRX file under `results/`. The aborted sandbox runs are not substituted for these completed runs.

### What those passes demonstrate

Existing integration tests run real subsystem compositions through Game.Apply, including scripted games to a win, land/mana use, casting/resolution, damage, triggers, replacements, a legend choice and private views. Other tests isolate subsystems with fakes. PriorityTests demonstrate ordinary passing and one-object resolution; MulliganTests demonstrate the implemented sequential procedure. Layer, casting, interpreter, zone, SBA and replacement suites cover many specific vocabulary operations.

### What those passes do not demonstrate

They do not certify all rules or all combinations. Some existing expectations encode an incomplete implementation: for example, the turn tests walk steps that the rules require skipping in certain games. A correct test of one ordinary stack resolution does not detect stale pass history after a response. Generated card scenarios use the same definitions being tested and often give ample mana and automatically choose first-legal answers. They cannot independently establish the definition matches the real card.

## Independent rules probes

The standalone probe project references the real Rules project and composes Wave2. It has **12 checks: 3 pass and 9 expose failures**, with no probe harness errors in the final run. The probe executable reports observations; a zero harness exit means it completed, not that every conformance assertion passed.

Most checks submit commands to Game.Apply using constructed two-player states. Three checks isolate specific histories: combat after all blockers leave, first strike lost between damage steps, and a once-per-turn trigger removed without resolving. Those are explicitly subsystem/event-history checks rather than fully staged UI reproductions.

| Check | Result | Observed behavior and consequence |
| --- | --- | --- |
| One ordinary spell waits for both players | Pass | No effect after the first pass; after the second, the spell resolves and leaves the stack. |
| Two ordinary spells resolve LIFO | Pass | Two spells cast without earlier pass history resolve only the top spell after both players pass. |
| An ordinary required-target cast has a legal offer | Pass | The explicit targeted command is accepted and the spell is offered. This particular offer is not a demonstrated gap. |
| Response resets the sequence of passes | **Fail** | A casts; A passes; B casts a response; B passes. B's response resolves immediately because A's earlier pass is still counted. A loses the required new chance to respond. |
| Mana activation resets earlier passes | **Fail** | A passes; B activates a mana ability; B passes. The engine advances from precombat main to beginning of combat and empties mana instead of returning priority to A in the same main phase. |
| Required modes are announced before the spell is on the stack | **Fail** | A modal spell is accepted on the stack with zero announced modes and no pending announcement question. |
| Trample with all blockers gone | **Fail** | A recorded blocked 5-power trampler deals zero to the defending player instead of five. |
| First striker loses first strike between damage steps | **Fail** | It assigns five damage again in the regular damage step. Eligibility is read from current keywords rather than the first step's required history. |
| Once-per-turn trigger remains spent after it is countered/removed | **Fail** | After the first trigger ceases without resolving, another matching cast creates another firing in the same turn. The control source remains on the battlefield. |
| Cleanup trigger gets priority before next turn | **Fail** | Passing out of the end step advances to turn two's upkeep with an empty stack rather than handling the cleanup trigger and cleanup priority. |
| Starting player skips the entire first draw step | **Fail** | Passing out of first upkeep enters Draw rather than PrecombatMain. Suppressing the card draw is not the same as skipping the step. |
| Empty combat skips blockers and damage | **Fail** | Empty attacker declaration followed by both passes enters DeclareBlockers rather than EndOfCombat. |

Exact expected rules, observed values and probe IDs: [results/probes.json](results/probes.json). Reproduction source: [probes/Program.cs](probes/Program.cs).

## Confirmed defects and repair directions

### B01 Consecutive passes are not invalidated by intervening actions

Rules: 117.3c, 117.4. Sources: `Turn/TurnSystem.cs:Pass`, casting/activation events and the command boundary.

Keep the player's priority after a successful action where required, but explicitly clear the earlier pass sequence. This must cover casts, activations including mana abilities, and relevant special actions. Do not reset passes indiscriminately on every domain event or on another pass. Add full response chains with multiple counter-responses and retained priority to the normal regression suite. This is the highest priority because it makes even simple interaction timing wrong.

### B02 Skipped steps are modeled as suppressed actions

Rules: 103.8a, 508.8. Sources: TurnSystem.DrawStep, NextStep and combat-step advancement.

Determine the next actual step before emitting StepBegan or offering priority. Skip the entire starting player's first draw step in a two-player game. When there are no attackers, skip declare blockers and combat damage. Correct the existing expectations that imply every turn always enters all steps. Test that triggers for skipped steps do not fire and that extra priority windows are not invented.

### B03 Cleanup is not a full repeatable rules checkpoint

Rule: 514.3a. Sources: TurnSystem.Advance, FinishCleanup, BeginNextTurn and Game.Settle.

Keep cleanup active while discard decisions, expiration, state-based actions and waiting triggers are handled. If cleanup gives priority, require another cleanup after its stack/priority cycle ends. Test toughness-expiry deaths, triggers from those deaths, a cleanup trigger that itself causes another cleanup event, and both player views while a cleanup choice is pending.

### B04 Modes can be selected too late

Rules: 601.2b, 700.2. Source: Casting/ModeAnnouncement.cs explicitly permits omitted modes and leaves a later choice.

Require the command to supply modes or stop in a genuine announcement transaction before the cast is complete. Lock modes before target selection, cost determination and opponent responses. Resolution-time choices remain resolution-time choices only when the instruction/rules put them there. Apply the same principle to triggered and activated ability announcement.

### B05 Combat loses required history and bypasses assignment choices

Rules: 510.1, 510.4, 702.19. Sources: Combat/CombatDamage.cs:AttackerDamage, Fights, Batch.

Preserve blocked status separately from the surviving blocker list. Apply trample's no-surviving-blockers exception. Record first/double-strike eligibility at the first damage step and use that history for the second. Add a validated player assignment choice instead of treating the automatic lethal-first allocation as the only way combat can be played. The direct probes prove the first two algorithmic failures; complete assignment-choice coverage still needs implementation/testing.

### B06 Once-per-turn accounting tracks resolution rather than occurrence

Rule: 603.2h. Source: Triggers/TriggerSystem.cs:OncePerTurn and Spent; its comments explicitly acknowledge the countered-trigger gap.

Record the triggering occurrence using an appropriate source/ability-instance identity. Do not infer it from whether a stack object still exists or whether the ability resolved. Test countering, multiple events in one batch, changed/granted abilities and new object identities after leaving/reentering. The probe removes the first triggered stack object directly to isolate precisely this history defect.

## Other incomplete areas established by source inspection

These are source findings, not additional passing/failing runtime probes:

| Area | Evidence | Why it matters |
| --- | --- | --- |
| Layer dependency rules | Layers.cs, Layers.Matcher.cs and Layers.PowerToughness.cs explicitly say CR 613.8 is not built; recursion guards substitute simplified reads | Continuous effects can change one another's applicability or characteristics. Timestamp ordering alone is insufficient. |
| Text-changing effects | Layers.cs skips layer 3 while ChangeText exists in the vocabulary | Representable data is not automatically executable semantics. |
| Control-changing expressions | ControllerFrom accepts You and refuses other PlayerRef shapes | Some representable control effects cannot execute. |
| Simultaneous mulligan rounds | TurnSystem.Mulligans.cs documents its sequential compromise | The two players do not follow the full prescribed declaration/redraw procedure. |
| Product mulligan defaults | GameSetup defaults OfferMulligans to false; GameSessionService does not override it | The API game-creation path is not demonstrated to offer normal mulligans. |
| Trigger collection versus placement | Collect immediately calls PutOnStack; Settle calls Collect before its SBA loop | The combined priority checkpoint needs testing/redesign so triggered occurrence is not confused with placement. |
| Legal offer completeness | LegalActions enumerates hand casts/lands and permanent activations; executor has other-zone permissions | UI/bot offers can lag supported execution. The new ordinary target-offer probe passed, so this is not a claim all targeted offers are absent. |
| Ability identity and resumption | AbilityIndex, effect paths and ChoiceContext.Extra strings identify work | Granted abilities, version changes and nested paused instructions need more robust identity and typed state. |
| Execution guards | Settle 1,000 rounds, SBA 100 passes, replacement 64 rounds return without a dedicated exhaustion result | A processing guard can yield apparently successful incomplete state. These paths need explicit fault cases. |
| Session durability | ApplyAsync updates live State and Log before awaiting persistence | A failed save can leave memory ahead of stored history. |
| Readiness fingerprint | EngineHash uses assembly version plus event names; pool returns saved status | A behavioral change can retain a verification label without proving the same implementation was tested. |
| Full format/match procedures | Creation checks card readiness; no complete tournament/match lifecycle is established by this path | Deck legality, best-of-three, sideboarding and next-game play/draw are separate responsibilities. |

There is an explicit unsupported keyword-expansion list in Keywords/KeywordSemantics.Unsupported.cs. Some listed names also have narrower direct subsystem support, so the list must be audited by operation and representation, not copied into a blanket assertion that every such card is impossible.

## Card corpus rerun

The original directory contains **632 top-level card files**: 500 saved Verified, 83 Translated and 49 Disputed. It contains **637 stored scenarios** across files. Nested auxiliary JSON files are excluded from these card counts.

The audit copied the top-level files and reran the real pipeline verifier against that copy. It evaluates Translated/Verified candidates, so the **583 candidates** partition as follows:

| Outcome | Cards |
| --- | ---: |
| Passed their generated/stored verification scenarios | 500 |
| Failed at least one verification scenario | 6 |
| No runnable verification scenario | 69 |
| Explicit unsupported marker blocks verification | 8 |
| Crashed | 0 |
| Stale according to the current verifier | 0 |

The remaining 49 files are disputed and outside that candidate set. The verifier intentionally exits zero when it finishes even if cards fail; the printed summary and per-card results are the measurement. Current engine hash: `engine:c7d1a471661256a2`; this hash has the weaknesses described above.

Failed cards and observed assertions:

| Card | Result |
| --- | --- |
| Frantic Search | Generated InZone expectation could no longer find the named spell in the game. |
| Multani, Yavimaya's Avatar | Expected zone differed; actual Graveyard. |
| Flusterstorm | Generated life expectation differed; actual 19. |
| Lumra, Bellow of the Woods | Expected zone differed; actual Graveyard. |
| Karoo | One of two scenarios failed; actual Graveyard in the generated scenario. |
| Mox Diamond | One of two scenarios failed; actual Graveyard in the generated scenario. |

These are **untriaged scenario failures**, not automatically six engine bugs. A correctly sacrificed permanent or a correctly dying 0-toughness creature can reveal a bad smoke fixture/expectation. Each needs an independently authored scenario from the card's actual rules, then classification as definition, engine, scenario generator or expectation error. Preserve the failures rather than loosening assertions to get a green count.

Passing 500 files is evidence for their tested scenarios in this small corpus. It is not a claim that 500 cards are fully correct in arbitrary interactions, that every face/ability was exercised, or that the engine covers a percentage of all Magic rules.

## Complete documentation surface

Three levels are provided:

1. [TWO_PLAYER_GAME_FLOW.md](TWO_PLAYER_GAME_FLOW.md) walks every normal game phase, priority checkpoint, casting/activation/resolution responsibility, combat step, cleanup and game-ending boundary, including cross-cutting rules.
2. [RULES_REGISTER.md](RULES_REGISTER.md) registers all **147 primary rule groups** and all **265 keyword/action section headings** in the pinned rulebook.
3. [rule-register.json](rule-register.json) indexes **3,161 numbered rule clauses**, their rulebook lines and exact attribute-reference candidates. `rule-reference-index.json` and `test-source-index.json` let a later pass find code and tests. Rules that were not independently checked remain unassessed. Citation counts are navigation aids, never a coverage metric.

This documents the entire numbered-rule tracking surface while being explicit about what this execution audit did and did not establish. Certifying all those clauses and their interactions is a multi-iteration conformance project, not something a single build/test run can prove.

## Updated implementation priorities

1. Promote the independent failure cases into ordinary regression tests without changing their rules-based expected outcomes. Add complete command-driven reproductions for the isolated combat/history checks.
2. Repair the priority/action boundary, step skipping, cleanup and announcement timing. The acceptance demo must include a real two-player response chain, not just two no-response passes.
3. Repair trample/first-strike history and once-per-turn trigger accounting; then test APNAP/SBA/trigger placement and simultaneous damage as a combined system.
4. Correct layer dependency/text-changing gaps and add explicit failure for every execution-budget guard. Gate unsupported shapes before admission.
5. Strengthen build/definition/scenario fingerprints, pinned definition bundles and durable command receipts/revisions. These are still important, but they do not outrank the confirmed basic game-flow errors.
6. Build complete action plans and typed choice continuations, then stable ability identities and a validated reusable definition catalogue.
7. Expand cards/mechanics only after the core two-player benchmark passes independent interaction tests. Local bots and tutorials should consume the same authoritative choices, as the Arena investigation suggested.

## Audit artifacts and rerun instructions

- Baseline: `commit.txt`, `working-tree-before.txt`, `inventory-summary.json`, `card-inventory.json`.
- Build: `results/build.log` (initial restricted attempt), `results/build-serial.log` (successful solution build).
- Suites: completed `rules-audit*.trx`, `MtgEngine.Cards.Tests*.trx`, `MtgEngine.Pipeline.Tests*.trx`, `MtgEngine.Api.Tests*.trx`; aggregate `test-summary.json`.
- Corpus: `results/card-status-before.log`, `results/card-verify.log`, ignored `card-snapshot/` copy.
- New checks: `probes/AuditProbes.csproj`, `probes/Program.cs`, `results/probe-build.log`, `results/probes.json`.
- Index generation: `node research/baseline-audit/inventory.cjs`.

After building, run each suite with `dotnet test tests/<suite>/<suite>.csproj --no-build --no-restore --logger trx`. In this host the test runner needs permission for its local interprocess communication. Build the probe project serially, then run `dotnet research/baseline-audit/probes/bin/Debug/net10.0/AuditProbes.dll research/baseline-audit/results/probes.json`.

To repeat corpus verification, first copy `cards/*.json` into a new audit directory and point `MtgEngine.Cards.Pipeline.dll verify` there. Do not point this audit run at original card files, because verification writes statuses and scenario results.
