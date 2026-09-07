# Card engine — the agent plan

From scratch. Server-authoritative C# engine, cards as data in a closed vocabulary, the
Scryfall JSON and the parsed rulebook as the only inputs, and a board that is playable before
the engine is finished. This document is the brief every agent reads first; the per-agent
briefs below only add what is specific to that agent.

Nothing from the deleted engine is restored. Where this plan names a decision the old engine
also made (state as a fold of the event log, computed characteristics, the game stops and
asks), it is because the decision was right, not because the code was.

## The four rules every agent works under

1. **A card is data or it does not exist.** No card name, card text or card-shaped special
   case may appear in `MtgEngine.Rules`. The engine knows the vocabulary; the files under
   `cards/` know the cards. A pull request that adds a card to C# is refused.
2. **The vocabulary is closed and generated.** Keyword abilities and their rule references
   come from `IComprehensiveRules.Keywords`; keyword actions from rule group 701. An agent
   that needs a primitive the vocabulary lacks adds the record, the interpreter arm, the name
   table entry and the rule citation in one change, never a workaround.
3. **Coverage is counted in verified plays.** A card's status moves to `Verified` only when
   its scenarios pass in the real runner. "Compiles", "deserialises" and "looks right" are
   not statuses.
4. **Nothing half-implemented reaches a table.** The deck gate reads one status field. A card
   is enforced, badged honour-system, or refused. There is no fourth state.

## Layout

```
MtgEngine.Cards/            the vocabulary: records, JSON polymorphism, schema, linter, name table
MtgEngine.Rules/            the engine: state, events, reducer, interpreter, subsystems
MtgEngine.Cards.Pipeline/   console tool: select, translate, agree, scenarios, run, write
MtgEngine.Api/              definition store, deck gate, game hub, session, status endpoints
cards/                      one JSON file per oracle id: definition, status, provenance, scenarios
tests/MtgEngine.Cards.Tests/    schema round-trips, linter, name-table completeness
tests/MtgEngine.Rules.Tests/    the 277 rulebook examples as scenarios, plus per-subsystem tests
tests/MtgEngine.Pipeline.Tests/ stage idempotence, agreement diff, keyword cross-check
```

## Waves

Parallel work only starts once the contract it builds against is merged. Every wave has an
exit criterion the coordinator checks before launching the next; "the agents reported done"
is not one.

### Wave 0 — the contract (serial, one agent, coordinator reviews)

Deliverables:

- `MtgEngine.Cards`: `CardDefinition`, `Face`, the six `Ability` shapes, `Effect`, `Selector`,
  `ObjectFilter`, `Amount`, `Trigger`, `Condition`, `Cost`, `ContinuousEffect`, `Duration`,
  `PlayerRef`, `Zone`, `TokenSpec`; `KeywordKind` generated from the parsed rulebook with a
  test asserting the enum and `IComprehensiveRules.Keywords` agree; `[JsonPolymorphic]` with a
  checked name table; `CardFileSerializer.Schema()`; `Linter.Check(CardFile)`.
- `MtgEngine.Rules` skeleton: `GameState`, `GameObject`, `PlayerState`, `Zone`, `ObjectId`,
  `GameEvent` base and the reducer signature, `Command` base, `Game.Apply` signature, the
  `IInterpreter`, `ISubsystem` seams every wave-1 builder implements, and stubs that throw
  `NotImplementedException` naming the subsystem.
- `Scenario` records and `ScenarioRunner` interface in `MtgEngine.Cards`, so pipeline and
  engine agents test against the same shape from day one.
- `tools/build.sh` (scoped: `dotnet build <project>`, then targeted tests), `tools/gate.sh`
  (format, full build, full test, run as separate steps), the integration branch, and the
  worktree base reset to it.

Exit: solution builds, `dotnet format --verify-no-changes` clean, the name-table and
keyword-agreement tests green, a `CardFile` round-trips through JSON unchanged.

### Wave 1 — subsystems and the pipeline (parallel, at most 8 engine builders + 4 others)

Each builder owns the files in its row and no others. Where two subsystems meet, the seam is
an event or an interface fixed in wave 0; a builder who needs to change a seam stops and
sends the change to the coordinator rather than editing the other builder's files.

| Agent | Owns | Lands with |
|---|---|---|
| E0 query layer | `Rules/Query/*`: `IQuery` — filter matching, conditions, amounts, non-targeting selectors, player refs | `QueryTests` |
| E1 zones and objects | `Rules/Zones/*`, `Rules/Objects/*`, game creation, shuffle, draw | `ZoneChangeTests`, rulebook examples for 400–406 |
| E2 turn and priority | `Rules/Turn/*`, `Rules/Priority/*`, untap, draw step, cleanup, passing, N players | `TurnStructureTests`, `PriorityTests`, examples for 500–514, 117 |
| E3 mana, costs, casting, stack | `Rules/Mana/*`, `Rules/Casting/*`, `Rules/Stack/*` | `CastingTests`, `ManaTests`, examples for 601–608, 106 |
| E4 combat | `Rules/Combat/*` | `CombatTests`, examples for 506–510, evasion keywords |
| E5 state-based actions, win/loss, commander | `Rules/Sba/*`, `Rules/Commander/*` | `StateBasedActionTests`, examples for 704, 903 |
| E7 continuous effects and layers | `Rules/Characteristics/*`: layers 1–7, timestamps, dependency | `LayerTests`, examples for 611–613 |

Triggers (E6) and replacement effects (E8) move to wave 2: a game of lands and vanilla
creatures needs neither, and both consume the query layer, which has to exist first. The
shared events every subsystem produces or reads — damage, mana, casting, activation, land
plays, resolution, attackers, blockers, zone reorders — are defined in wave 0's
`Events/GameEvent.cs`, so no builder invents its own spelling of one.
| P1 translation | `Pipeline/Translate/*`: batch requests, two models, structured output against the schema | `TranslateStageTests` with recorded responses |
| P2 free slice and tokens | `Pipeline/Templates/*`: keyword-only, vanilla, six mana patterns, `all_parts` token specs | `TemplateStageTests` asserting the 2,990 count |
| P3 agreement and cross-check | `Pipeline/Agree/*`: structural diff, keyword cross-check, status assignment, file writer | `AgreeStageTests` |
| C1 honour-system board | client `board/` manual controls, badges, hub commands `ManualMove`, `ManualTap`, `ManualCounters`, `ManualLife` | client specs, `GameHubTests` for the four commands |

Exit: every builder's tests green on the integration branch after the coordinator's full
gate; E1–E8 together play a two-player game of basic lands and vanilla creatures to a win
through the real `Game.Apply`; P1–P3 have produced `cards/` files for the 3,864 deck faces
and the free slice; C1's board moves a card and logs the event.

### Wave 2 — the interpreter meets the game (parallel, up to 8)

| Agent | Owns | Lands with |
|---|---|---|
| E6 triggers | `Rules/Triggers/*`: event matching, APNAP ordering, intervening if | `TriggeredAbilityTests`, examples for 603 |
| E8 replacement and prevention | `Rules/Replacement/*` | `ReplacementEffectTests`, examples for 614–615 |
| E9 interpreter core | `Rules/Interpreter/*`: `Sequence`, `If`, `May`, `Unless`, `ForEach`, `ChooseMode`, targets, `Remembered` | `InterpreterTests` |
| E10 keyword actions A | one arm per CR 701 action, first half by corpus frequency | one scenario per arm |
| E11 keyword actions B | second half | one scenario per arm |
| E12 keyword abilities | one arm per CR 702 keyword in the deck slice, by frequency | one scenario per keyword |
| E13 choices | `Rules/Choices/*`: pending question, resume by kind, targets, modes, optional costs | `ChoiceResumptionTests` |
| E14 view and legal actions | `Rules/Views/*`: per-player projection, `LegalActions.For` | `HiddenInformationTests`, `LegalActionsTests` |
| E15 faces and layouts | `Rules/Faces/*`: transform, modal double-faced, split, adventure, meld, face-down, copies, tokens and emblems reading the definition store | `FaceTests`, examples for 712–715, 707 |
| A1 hub and session | `Api/Hubs/GameHub`, `GameSessionService`, persistence of the log, reconnection, `CardFilePool`, deck gate, `GameReport` from a log position | `GameHubTests`, gate tests for all three admissions |
| P4 scenarios and runner | `Pipeline/Scenarios/*`: rulings to `Scenario`, generated smoke scenario per card, runner, status ledger, `/api/cards/{id}/implementation`, coverage endpoint | `ScenarioStageTests`, the ledger endpoint, throughput test |
| C2 status and disputes | client deck view badges per admission, the dispute queue where a `Disputed` card's two readings are shown and one is chosen or edited through the pipeline | client specs |
| C3 coverage dashboard | client page over the coverage endpoint: verified, translated, disputed, blocked by primitive, ranked | client specs |

Exit: the free slice and every deck card whose scenarios pass are `Verified`; a deck of
verified cards plays end to end on the hub with enforcement; the status endpoint reports the
counts the ledger holds.

### Wave 3 — verify, triage, grow (rolling)

- **Verifiers**, eight per landing, never the builder who wrote it. Each takes a `Verified`
  card, plays it by hand through the runner in a scenario the builder did not write, and
  either confirms or files a refutation with the event log. A refuted card returns to
  `Translated`. Verifiers refuse with `allow-lost` and never edit engine code.
- **Triage**, one agent, reads every failed scenario and attributes it: engine, primitive
  missing, translation, or scenario wrong. Output is the next wave's briefs, ranked by cards
  blocked, plus the primitive backlog from the `Unsupported` markers the translation stage
  emitted.
- **Primitive builders**, up to six, each taking the top backlog item: record, interpreter arm,
  name-table entry, rule citation, the waiting scenarios.
- **Corpus run**: P1–P4 over all 33,605 faces in the background, deck cards first.

Exit for each round: the verified count rose, no refuted card is still `Verified`, the full
gate is green on the integration branch.

## Orchestration — what the coordinator does

- Owns the integration branch, the worktree base, the ledger and every brief. Launches
  agents only into free slots: the subagent cap is 20, builders are capped at 8, and the
  ledger records every launch so a lost notification does not become a duplicate launch.
- Merges through one queue. A builder marks a worktree ready; the queue runs the gate steps
  separately, never chained, and merges on green. The queue's checks are not edited while
  it runs. If the queue is stopped mid-check, the integration tip is reset to its first
  parent before anything else happens, and `loop.out` is read with `grep -a`.
- Runs the full gate on the integration branch after every merge round, not per merge, and
  kills stale test hosts by pid before starting it.
- After a rate-limit reset: read outputs first, relaunch only what is missing, resume
  builders by message, and restart the merge loop only when the ready directory is empty.
- Never launches wave N+1 on a report. Launches it on the exit criterion.

## The brief every agent gets

```
You are <agent id>. First run `node tools/ledger.js claim <agent id>` in your worktree:
until you do, every edit and every non-read command is refused. Then read
CARD_ENGINE_PLAN.md and CLAUDE.md with the Read tool; the docs hook checks your own
transcript, not the coordinator's.

You own exactly these files: <list>. Do not create or edit files outside them. If you need a
seam changed, write the change you need to seam-requests/<agent id>.md and stop that thread.

Your test anchor is <TestClassName>. Every behaviour you add has a test there that fails
without your change; break the code, watch it go red, put it back.

Build with `timeout 540 tools/build.sh <project>`; on exit 124 run it again, it was the lock.
Never run dotnet test while editing. Files are CRLF; do not run sed -i on C#.

Cite the rule number beside every rule you implement. Your subsystem's rulebook examples
are your acceptance tests: <rule range>.

When green: commit on your branch, write ready/<agent id>, and report the test names.
```

Verifier briefs replace the middle with: the card, its file, the runner command, "write a
scenario the builder did not write", and "refuse with allow-lost if the card does not play
as printed".

## Models

| Role | Model | Why |
|---|---|---|
| coordinator: contract, seams, briefs, merges, exit checks, the sweep | the session itself | holds the design; never builds in parallel with itself |
| wave 0 contract | the coordinator, directly | serial and design-heavy; a handoff adds nothing |
| subsystem builders, waves 1 and 2 | Opus, effort high | real engineering with rule citations |
| primitive builders, wave 3 | Sonnet, promoted or demoted by data | one record, one arm, one test; the first twenty run on both models and the verify pass rate decides |
| verifiers | Opus, effort high, prompted to refute | judgement is what verification is |
| triage | Opus | attribution is judgement |
| lookups, file searches | Haiku, read-only | never writes code |
| translation, first reading | `claude-opus-5`, Message Batches, JSON constrained by the prompt and validated by the serializer and linter | the unique sentences |
| translation, second reading | `claude-sonnet-5`, same | a different model, so agreement carries information |

Structured outputs cannot take the vocabulary's schema, because the API rejects recursive
schemas and effects, selectors and filters refer to each other. So the translator gets the
generated vocabulary reference (`pipeline reference`, about ten thousand tokens, cached as the
system prefix) and is asked for JSON only; the serializer and the linter are the validation,
with one repair round for a reading that fails either. Parse and lint failure rates are pilot
metrics in their own right.
| rulings to scenarios | `claude-sonnet-5`, Message Batches | checked by the runner, not a second model |
| narration judge | `claude-sonnet-5` | never Haiku, which echoes |

Translation and scenarios are API batches, not agents: hours of wall time, no agent time,
roughly $1,500 for the whole corpus at cached batch rates and about a tenth of that for the
deck slice. Agents draw on the usage window, which is the scarce resource.

## Enforcement — what stops an agent, as opposed to what asks it

The rule this repo already lives by: an instruction the assistant is asked to follow is not
a control. Everything in this section is a hook, a test or a gate check. The briefs repeat
the rules so agents know why they were stopped, not so that they stop.

### The docs

`.claude/hooks/require-docs.js` gains one rule: any path under `MtgEngine.Cards/`,
`MtgEngine.Rules/`, `MtgEngine.Cards.Pipeline/`, `cards/` or their test projects requires a
`Read` of `CARD_ENGINE_PLAN.md` in the **calling agent's own transcript**. The hook reads the
transcript of whoever is making the call, so the coordinator having read it does not count
for a builder. `.claude/` is tracked, so every worktree carries the hooks.

### Scope, build discipline and the time box: `.claude/hooks/agent-guard.js`

A second `PreToolUse` hook on `Edit`, `Write` and `Bash`. It reads `.agent/manifest.json`
at the worktree root, written by the coordinator before launch:

```json
{ "id": "E4", "role": "builder", "owns": ["MtgEngine.Rules/Combat/**", "tests/MtgEngine.Rules.Tests/CombatTests*.cs"],
  "anchor": "CombatTests", "maxToolCalls": 120, "launchedAt": "2026-09-07T14:00:00Z" }
```

No manifest means the coordinator's own checkout, and the hook allows. With one:

| Call | Verdict |
|---|---|
| `Edit`/`Write` outside `owns` | denied: "write the change you need to `seam-requests/<id>.md`" |
| `dotnet build` without a project path, or naming the solution | denied: scoped builds only |
| `dotnet test` without `--filter` when role is builder or verifier | denied: the full suite is the queue's |
| any `dotnet build`/`test` not wrapped in `timeout` | denied |
| `sed -i` on a `.cs` file, `git stash`, `git checkout` of another agent's files | denied |
| any tool call past `maxToolCalls` except `git commit`, `git status`, writing `ready/` or the report | denied: "time box exceeded: commit what is green, report, stop" |

Every allowed call also writes `.agent/last-call` with a timestamp. That file is the
heartbeat the sweep reads. The turn cap is the hard time box; the harness has no wall-clock
limit per agent, so this is where one lives.

### The four rules, in tests and gate checks

| Rule | Control |
|---|---|
| a card is data or it does not exist | architecture test: `MtgEngine.Rules` references neither `MtgEngine.Api` nor `cards/`, and contains no string literal matching `tools/card-names.txt`, the corpus's names checked in |
| the vocabulary is closed and generated | `KeywordKind` must equal `IComprehensiveRules.Keywords`; every ability and effect record must appear in the serializer's name table; the generated schema is committed and the gate fails if it differs from the build's |
| coverage is verified plays | a card file may carry `Verified` only with a `verifiedWith` engine hash and a runner log id; the gate check demotes any file whose hash is stale to `Translated` and fails if a `Verified` file has no scenarios |
| nothing half-implemented reaches a table | gate tests for the three admissions, and an architecture test that the deck gate depends on `StatusOf` and nothing else |
| files stay readable | `tools/check-file-size.js`: no `.cs` under `Rules/` or `Cards/` over 500 lines |
| line endings | `.gitattributes` plus a gate check |

### Idle agents

An agent is idle when its heartbeat is older than ten minutes and there is no `ready/<id>`
marker, or when its report carries `blocked_on`. Idleness is prevented first and detected
second:

- Builders never wait for the queue. They write `ready/<id>` and end. The queue merges later.
- Every build and test is under `timeout 540`. Exit 124 is re-run once, then reported.
- A seam request ends that thread; the builder finishes other owned work or reports.
- The structured-output schema means a run cannot end without the report, and the report
  ends the run. There is no "done but still sitting there".

The coordinator runs a sweep every ten minutes, from a `Monitor` or a cron in the session,
over every worktree with a manifest:

| Heartbeat | Action |
|---|---|
| under 10 minutes | nothing |
| 10 to 20 minutes, no `ready/` | message the agent: report or stop |
| over 20 minutes, or over 30 minutes since `launchedAt` | kill the agent, kill its test hosts by pid, mark the ledger `killed:<reason>`, relaunch with a narrower brief in a fresh context |

After a rate-limit reset the ledger is read before anything is launched: outputs first,
relaunch only what is missing, resume builders by message, restart the queue only when
`ready/` is empty.

### What the harness enforces on its own

A `schema` on an agent call forces a typed result and ends the run when it is produced.
`model` and `effort` are per call. Concurrency is capped at sixteen or the cores minus two,
with this plan's cap of eight builders inside it. A token ceiling set on the run is hard and
throws rather than exceeds. Wall-clock per agent is not native, which is why the turn cap and
the sweep exist.

### Wave 0 therefore also delivers

`agent-guard.js`, the `require-docs.js` rule, `tools/check-file-size.js`,
`tools/card-names.txt`, the ledger and sweep scripts, and the architecture tests above. No
builder is launched until the hooks are in place and have been seen to deny.

## Decisions and gaps closed on review

**What "every card" means.** Every card legal in at least one sanctioned format, minus a
published list in `cards/EXCLUDED.md` with a reason per card: subgames, physical dexterity,
watching what players say or wear, ante, and silver-bordered cards that reference art, the
draft or the expansion symbol. XMage's list is the starting point. A card is added to the
list by the coordinator only, with the reason, and the dashboard counts it separately from
unimplemented. A card whose only mechanic is itself is not excluded; it gets its own
primitive.

**The pilot runs before P1 exists.** The coordinator runs it with a script against the
wave-0 schema: 200 deck faces, both models, the structural diff. That is what the first
decision point measures. P1 then productises what the script did.

**The game is a desktop surface.** The phone requirement was removed on 2026-09-06 for the
game. The rest of the client keeps its mobile-first standard until told otherwise; the board,
the dispute queue and the dashboard are judged at desktop width and are exempt from the
phone audit, which needs an entry in `tools/check-ui-states.js` saying so rather than a
silent skip.

**`cards/` is generated.** Only the pipeline writes it. A human correction goes through
`pipeline edit <oracleId>`, which records `Human` provenance and resets the status to
`Translated` so the runner re-verifies it. A hand-edited JSON file fails the gate check
because its provenance no longer matches its content hash.

**Versioning and errata.** Every card file carries `irVersion` and `oracleHash`. A change to
a primitive's shape ships with a migration in `Pipeline/Migrations/` and bumps `irVersion`;
the gate fails if any file is behind. The API's existing Scryfall bulk refresh feeds the
pipeline; a face whose oracle text hash changed is re-translated and its status reset. New
sets arrive the same way, a few hundred faces per quarter, with no agent involved.

**Honour-system rules of engagement.** Whether a table admits honour-system cards is a
table setting chosen when it is created. Manual actions are accepted only on objects the
gate admitted as honour-system; a manual action on an enforced object is refused. Every
manual action is an event carrying the actor, so replay still holds and the log shows who
did what.

**Reports from play become scenarios.** A player reports a card from the log; `GameReport`
stores the game, the event index and the oracle id; the pipeline turns it into a scenario
candidate for triage. This is the last line of verification and the only one that sees
real play.

**Performance gate.** The runner must clear 1,000 scenarios a minute on the development
machine, asserted by a test, so that the corpus's scenarios run in hours. An engine change
that halves throughput fails the gate.

**Branching and CI.** Builders branch from the integration branch; the queue merges to it;
each wave's exit criterion is a pull request from integration to `master`. The gate runs in
CI on that pull request, so the exit is checked by the machine, not read from a report.

**Build isolation.** Every worktree builds with `UseSharedCompilation=false` and
`MSBUILDDISABLENODEREUSE=1`, never runs `dotnet build-server shutdown`, and uses
`.agent/tmp` rather than `/tmp`, which is shared across agents. `agent-guard.js` denies
writes under `/tmp`.

**Budget.** Each wave has a usage-window budget and the API batches have a dollar cap, both
written in the ledger before launch. Hitting either stops launches; it does not stop the
queue. The corpus translation is not started until the pilot's cost per face is known.

**Secrets.** The pipeline uses the API's existing Anthropic configuration. No key appears in
a brief, a manifest or a file under `cards/`.

**Determinism.** Every random choice in the engine draws from the seeded generator in the
game state, and the seed is in the log, so a scenario or a reported game replays exactly.

**Still deliberately out.** The Storybook proof of concept the client work was going to
start with. It is a wave-2 client task if wanted, not a dependency of anything.

## Decision points

- **After the pilot** (day 1, the first 200 deck faces through P1–P3): if the two
  translators agree on fewer than 80% of ordinary deck cards, the vocabulary is redesigned
  before anything else scales. Two hours to find out.
- **After wave 1**: if any two builders' work merges cleanly and fails at runtime together,
  the seam was wrong; unify before wave 2, do not patch.
- **After the first verify round**: if more than a tenth of `Verified` cards are refuted,
  scenarios are too weak; the smoke scenario gains assertions before the corpus run.

## Schedule

| By the end of | State |
|---|---|
| day 0 | wave 0 merged; pilot translation running |
| day 1 | pilot measured; wave 1 launched; board playable, all cards honour-system |
| day 3 | wave 1 exit: basic games play through the engine; deck faces translated |
| day 5 | wave 2 exit: verified deck cards enforced on the hub |
| day 7 onward | wave 3 rounds; verified count climbs daily; corpus run in the background |

Days, not hours, because subsystem builders finish in about a day and the exit criteria
are gates that get checked, not estimates. Anything faster is bought with scope: fewer
subsystems in wave 1 means a smaller enforced slice at day 3, not an earlier one.
