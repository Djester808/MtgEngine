# Four-player board implementation status

## Goal remains active

Implement the full real board for four players based on `prototypes/table`, and verify
phases, tapping, payment, and zones. Do not mark complete from compilation or prototype
tests. Both repositories are on `codex/multiplayer-table-implementation`. Existing staged
and unstaged work in each repository must be preserved.

## Implemented in the real client

The sibling `mtg-client` repository now renders all opponents in circular order above self,
permanents split by controller, opponent lands above creatures, per-player zone rails,
floating mana, and a collapsible right stack. Priority and pass are in the top turn row.
The hand is resizable, collapsible, reorderable, and primary-click plays castable cards.
Right-click opens Card inspector options. Player names open details or act as targets.

`TableSeatComponent` is the seat boundary; `BattlefieldArrangement` owns presentation-only
pile membership. `ZonesComponent` supports constrained pointer dragging, individual pile
members, automatic matching land/token piles capped at three, and tap-preserving membership.
`ResizeObserver` fits cards to the available surface and keeps centered row layout. Initial
browser clipping was reproduced and corrected. Dense layouts still need checking.

`EngineView` normalization converts the actual C# snapshot into the existing typed client
store. The client now receives `ViewUpdated` and sends the closed `Command` contract.
The API adds a seated GET `/api/games/{id}` snapshot; actor identity remains token-derived.
Views expose public costs, Oracle text, computed abilities, colors, combat relationships,
land counts and loss flags. Face-down metadata remains absent. Server ability offers filter
unavailable duplicate land actions. The manual-counter property is `counterKind` to avoid
colliding with the command discriminator `kind`.

## Evidence so far

- 1,347 rules tests pass after public metadata/relationship projection changes.
- 14 API hub tests pass after adding the snapshot endpoint.
- 126 focused real-client tests pass (board, zones, hand, arrangement, transport).
  A subsequently added server-ability-offer test still needs the next run.
- Angular development build passes after the ability-offer correction.
- The actual Angular board loads at 1280x720 with three opponents and self, page size
  1280x720, and all five hand cards on screen.
- Against the production engine/session/hub in an isolated QA host: primary Mountain
  activation adds R1 and taps exactly one land; Cinder Spark becomes castable; choosing
  Mira as a player target spends the mana and moves the card out of hand onto the stack.
  All four separately authenticated browser accounts pass. Mira goes 40→37, stack empties,
  Alex has priority, and the step remains Precombat main.

The isolated QA host is `tools/TableQa`, with no application database or production auth
changes. It serves the actual compiled Angular client on localhost:4320 through :4323 so
each browser origin has separate login storage. Accounts are qa0..qa3; any nonempty password
works only in this loopback QA host. It runs the production GameHub and engine on an explicit
replayed sample position. Read `/api/qa/table` for the current game ID. Start it with
`dotnet run --project tools/TableQa/TableQa.csproj`; build the client first. Do not expose it
on a public interface. Its sample cards are illustrative, not a claim of corpus verification.

## Remaining required work

1. Multiplayer combat: current engine `BlockersDeclared` still has a single global flag
   and replaces earlier blockers. Track declarations per defending player, merge blocks,
   and open priority only after the whole declaration. Add four-player engine/hub cases.
2. Client combat: attacks still default to the first opponent in SignalRService. Add
   per-attacker defender selection and retain it through confirmation. Wire click blocker,
   then attacker, to existing AssignBlocker; current ZonesComponent still has the old no-op.
   Draw arrows to actual defending player panels and assigned blocker cards.
3. Phase correctness: explicitly exercise a whole four-player turn, untap, draw, land limit,
   mana expiration, cleanup/discards, combat and damage. Earlier audit found stale passes
   after actions and incorrect skipped steps; verify against current source before fixing.
   Relevant source: TurnSystem, Game.Apply, and GameEvent folding. Direct subsystem combat
   tests intentionally bypass timing, so enforce command timing at the coordinator boundary.
4. Verify paid creature casting and sickness, colored and insufficient payment, draw/destroy
   spells, lethal deaths, every zone shortcut, hidden-zone privacy, and reconnection through
   actual UI/server flow. Check duplicate stats are not added twice by the adapter.
5. Dense board, all 2/3/4 viewer perspectives, hiding opponents (self remains same size),
   tapped-pile drag/extraction, keyboard drag/reorder, touch, hand resize/collapse, context
   menus, resizing and centering; verify desktop and 375x667 with screenshots.
6. Harden accessibility: focus management in player details and menus, Shift+F10/ContextMenu,
   Escape, outside-click dismissal, no inert controls, and meaningful labels. Current context
   menus are first-pass and not yet fully verified. Add settings gear for arrangement options.
7. Run appropriate client suites, lint/format and production build, backend tests and formatting.
   Update superseded documentation, remove one-off port scripts in tools, and record verified
   coverage and any remaining limits. Do not commit unrelated existing changes.

The pre-existing lobby API and old PlayHub service also differ from this server. Game
creation/invitation work is separate from board commands; decide any necessary integration
from current source rather than assuming the old endpoints exist. Never claim that a lobby
or production game-launch flow was exercised merely because the isolated board is working.

## Verification update — 2026-10-21, later implementation pass

The remaining-work list above is historical; items 1 and 2 have since been implemented.
The engine now merges blocker declarations per defending player, withholds priority until
all declarations finish, and resets pass rounds after actions. The client supports a
defender per attacker, blocker assignment, and rendered combat links.

Observed through four separately authenticated browser accounts against the real engine:
- Two attackers attacked different opponents. Each defending player assigned a blocker;
  both assignments survived, and combat damage killed the four 2/2 creatures correctly.
- A creature spell consumed two green mana, resolved onto the battlefield, and left hand.
- A draw spell consumed blue plus generic mana and drew two cards from the library.
- A land play bypassed the stack and increased the battlefield land count.
- A destroy spell consumed black plus generic mana and moved the opponent's creature to
  its owner's graveyard.

Recorded checks: 1,355 rules tests, 16 focused GameHub tests, and 266 focused client tests
passed before the latest settings/artwork refinements. The latest development client build
also passes. A subsequent arrangement/settings run passes all 13 tests, including dropping
back onto the same pile, disabling/restoring automatic stacking, and resetting arrangement
while preserving individual tapped states. Idle stack clicks no longer add target drafts.

Still unproven: complete turn-cycle/cleanup and rejection flows through the UI; dense and
phone layouts; all native dragging, zone-dialog and focus interactions; production build,
updated artwork-service tests, and broad final checks. This remains an active implementation,
not a completion claim. Earlier passing totals do not validate changes made afterward.

### Live zone and geometry checks

Reloaded the current client build against the running four-account QA game. Native zone
viewer opened graveyard (5 cards), exile (1), and command (1) with expected visible cards.
Library (55) and Mira hand (5) displayed only counts and the hidden-identity notice. Escape
closed each dialog; after the graveyard check focus returned to its exact opener. Board
settings opened as a native modal with the three arrangement options and reset control.

At 1280x720 the document measured exactly 1280x720. With three opponents each opponent
panel was 355x182.8 and its cards 38px wide. Hiding Mira enlarged the other panels to
500.5px and cards to 42px. The local panel stayed 1075x274.2, local cards 75px, and hand
height 100px. Restoring Mira restored the opponent geometry. This proves size isolation,
not general readability or dense-board completion. Screenshot: real-board-zones-verified.png.

### Command boundary and artwork regression checks

Updated SignalR service tests to the current `Command(gameId, dto)` endpoint instead of
obsolete per-action hub methods. The boundary now verifies actual numeric object/player
identifiers, announced X, separated object/player targets, per-attacker defenders, multiple
blockers, current choice identity, and manual command fields. Transport regression coverage
also proves a controlled opponent card goes to its owner's graveyard/command zone rather
than the viewer's. All 26 service/transport tests pass.

Added artwork tests proving only unique face-up definitions are queried, a cache miss never
uses remote lookup, absent views request nothing, and cancellation stops before lookup.
All 19 focused GameHub/artwork tests pass, including compilation of latest API changes.
Personal-zone ownership remains unverified through a native manual-move UI interaction;
the pure command translation is covered, not a claim about every manual interaction.

### Battlefield drag and card presentation pass

Latest development build passes; 45 ZonesComponent/arrangement tests pass. Idle left-click
on own or opponent creatures no longer opens the inspector; right-click remains inspection.
Corrected an Angular action-expression error in the stack targeting guard found by the full
client build. The earlier focused tests did not compile that template.

Card corner radii now scale with card width instead of inheriting 20px on tiny cards.
Removed the 38px opponent cap (height still limits cards), reserved a stable horizontal
footprint for rotated cards, and insert empty-space drops near the intended position rather
than always appending. Native pointer-drag verification on the real four-player board:
extracted one tapped Forest from a three-Forest pile, observed [2,1,1,1,1] land pile sizes
and six tapped lands, then dragged it back to a Forest and observed [3,1,1,1] with six tapped
lands. No game action or tap-state change occurred. Document remained 1280x720.
Screenshot: real-tapped-pile-restacked.png. Mid-drag state capture, dense boards, touch,
mobile and arbitrary resize centering are still outstanding, not implied by this check.

### Full four-player rotation and rules regression

Added coordinator-level tests that start in main phase two and drive a complete table round
back to the initial player. Empty attacks are explicitly declared; no priority is fabricated.
Each new active player reaches upkeep with its own permanents untapped and summoning sickness
cleared, while the initial player's tapped permanent stays tapped until that player's next
turn. Each player draws exactly one from its seeded library and reaches main phase one. A
separate probe verifies all mana pools empty on a step transition and the next pass round
starts empty. Full current rules suite: 1,357 passed, 0 failed, 0 skipped; evidence file
results/rules-turn-cycle.trx. This is coordinator evidence; the corresponding complete native
browser round remains to be exercised.

Scoped lint of board TypeScript, transport, zones, stack and SignalR service: zero errors,
four advisory complexity/size warnings (arrangement drop, board view model and file size,
engine command adapter). These warnings are still refactoring work rather than waived proof
of best-practice completion.

### Native phase advance and pointer-capture action repair

Native four-account UI flow advanced Alex main phase two → End → automatic cleanup →
Mira upkeep. The live check then exposed a real interaction defect: pointer capture on the
battlefield wrapper retargeted the click away from the child card action handler. Moved the
primary click handler to the wrapper and removed the child output binding, keeping one
activation per click. Added a rendered DOM regression (not just a direct method call).
All 35 ZonesComponent tests pass and the current development build passes.

Verified the fix through the actual browser: click a Forest during Mira upkeep → exactly
one Forest tapped and floating G1. All four players passed → Draw, library57→56, hand5→6,
no floating mana, Forest still tapped. Screenshot real-upkeep-mana-draw.png. Alex's six
lands remained tapped on Mira's turn. Current QA game is now Turn2/Mira/Draw, priorityMira.
This native evidence covers next-turn cleanup and upkeep/draw mana expiration; returning
all the way to Alex's next untap through the UI is still outstanding.

### Native full table round and hand controls

Through all four authenticated board UIs, advanced Mira and Theo turns, then Jules, declaring
zero attackers explicitly on each. Empty combat skipped blockers/damage. Reached Turn5 Alex
Upkeep: all six previously tapped lands untapped, no summoning-sick indicator remained. This
completes the native round-return check previously outstanding (automatic cleanup without a
hand-size discard choice; discard-required cleanup still needs separate evidence).

Verified hand Alt+Right reordered Island/Spark/Demise; collapse hid the hand (height0), reopen
preserved order; keyboard resize changed height100→110 while document stayed1280x720. Pointer
drag moved Island from first to last without casting it. These arrangements are client-local
and currently reset on full page reload.

Inspector menu verification exposed immediate closing from the document click handler.
Added propagation stops to both hand and battlefield menu items. The latest development build
passes and 76 zone/hand tests passed after the initial menu guard, but the final native
inspector retest is still pending; do not count the earlier test result as visual proof.

Final native inspector retest: both battlefield Forest and hand Cinder Spark right-click →
Card inspector remained visible. Escape closed the battlefield inspector before testing the
hand. The propagation stop must run on the menu item itself before removing the menu; a
parent-only guard was insufficient in the live app. Screenshot real-turn-five-inspector.png
also records Turn5/Alex/Upkeep with Alex's lands untapped.

### Crowded board, phone panel sizing, and live resize checks

Added reusable dense QA position (`--dense=true --port=4324`, separate OutputPath bin/Dense):
42 permanents per seat, 168 total, with 24 lands, 8 creatures, 4 artifacts and 6 tokens each.
Fixed crowded overlap with a pure tested spacing helper; pile stacking contexts no longer
interleave member z-index values. Transparent pile containers no longer intercept the exposed
strips of cards behind them. In the final 1280x720 dense position, sampled hit tests for
all 168 cards found an exposed card hit area for every card (42/42 per seat, none hidden).
This proves reachability, not readability of full card text on densely packed opponents.
Screenshot real-crowded-168-cards.png.

Manual adjustments are collapsed by default, filtered to cards needing them, and now include
all four seats (previous source only handed the panel two seats). Engine-created token
classification still places sample tokens in manual adjustments; that policy needs review
rather than silently relabeling them as enforced. No backend policy was weakened.

Short-panel overflow came from the zone rail's intrinsic minimum height. Constrained the
seat-body grid row and gave the four rail buttons explicit flexible tracks. The real board
at a 375x667 iframe viewport now measures document375x667, hand100, opponent panels338x140,
self338x189. Opponents pan horizontally on phone; this is not all four side-by-side. This is
CSS viewport testing, not touch-device emulation. Screenshot real-phone-375x667.png.

QA viewport harness also measured the same live board while resizing: 375x667 →1280x720
→1600x900 →667x375. Document dimensions matched each viewport. The creature/support/land
pile bounds remained centered with zero pixel center offset at all four sizes. At667x375
panels become too short for readable cards; fitting document dimensions alone does not prove
that landscape presentation complete. Need improve the short-height layout separately.

Combined board/hand/zones/game/transport regression suite:295 passed. Scoped changed-code
lint:0 errors,2 advisory board complexity/file-size warnings. Latest development build passes.
The final pile hit-surface change is SCSS and was verified in the actual dense browser.

### Final production pass — full-screen route, outcomes, and concession ordering

Corrected the application shell's full-screen route matcher: it previously recognized /play/
while this board actually uses /game/. Gameplay now receives the full viewport without the
navbar covering its header; settings supplies Return to lobby. The short-height shared
stylesheet compacts controls and the hand. Hand card sizing now respects the available compact
height instead of imposing an80px minimum in a64px container. Native production667x375:
document667x375, opponent panels207x101, local630x136, hand64, zero center offsets; header
and hand are visible. This is a dense landscape compromise, not full readable card text.

Fixed multiplayer outcomes: the adapter no longer calls the first elimination a victory.
It carries the authoritative server outcome and names actual winners, including effect wins
without eliminated seats. Eliminated players are labelled; terminal priority/concede controls
are replaced by finished-game messaging and a lobby return.

Native concession test first reproduced a transport failure: the old board disconnected
immediately after dispatching the command. The effect now awaits the hub command before
closing the connection/navigating, prevents duplicate in-flight concessions, and preserves
the table/session on failure. Regression tests cover acknowledgement ordering and failure.
Then successfully conceded Mira, Theo, Jules through their real UI accounts: after the first
and second concessions the game continued; after the third Alex won was displayed. The
original QA game c6062e54-8390-44a2-9390-f4267de6ede5 is now finished. Reloading a finished
game currently returns home via the pre-existing gameOverOnJoin effect; completed-game review
is not implemented. The dense QA game7ad13659-5b8b-4bae-ac14-8fd21ee336d4 remains playable.

Current recorded validation:
- Full client suite:1768 passed (client-full-tests.log), including final concession behavior.
- Full API suite:719 passed (results/api-board-final.trx and api-full-tests.log).
- Full rules suite:1357 passed (results/rules-turn-cycle.trx).
- Final production build passed (client-production-build.log); existing card-modal stylesheet
  budget warning17.27kB vs16kB remains. Final changed outcome/concession/template lint:0errors.
- In the final full-screen production dense board all168cards still have exposed sampled hit
  areas (42per seat,0hidden). Capture:real-production-four-player-board.png.
- Temporary port/edit scripts removed; reusable tools/TableQa remains. Normal API restarted.
- Both repositories remain on codex/multiplayer-table-implementation. No unrelated work committed.

Next acceptance checks before completion: live required choices (including discard cleanup),
invalid payment/land-limit feedback through the UI, final settings/menu keyboard and touch
coverage, and the generated-token enforcement/manual-control classification. The board's
lobby still exposes an older two-player creation flow; the isolated board checks do not prove
four-player production game creation/invitations. Preserve that distinction in delivery.

### Required choices, invalid-play feedback, and generated tokens — 2026-10-22

Added a repeatable cleanup scenario to TableQa (`--cleanup=true --port=4328`, separate
OutputPath bin/Cleanup). Native four-account verification entered cleanup with Alex's eight
hand cards. Only Alex received the discard options; an opponent saw a waiting message and no
options. Selecting Cinder Spark moved it to Alex's graveyard (1→2), reduced hand8→7, and
continued directly to Mira upkeep. Capture:real-cleanup-discard-complete.png.

Hand plays now report the same legality predicate that enables them: missing priority,
unfinished decisions, main-phase timing, nonempty stack, land limit, and insufficient mana.
Tooltips/disabled semantics also expose the reason. Zero-cost spells do not require a
nonempty mana pool. Spell announcements now retain the engine's actual target count rather
than forcing one target. Battlefield actions are gated while priority/required choices block
acting; combat declarations keep their separate step-action path. Removed the duplicate
board toast renderer; the app shell renders notifications once.

Native verification after rotating the cleanup game back to Alex's main:
- First Forest entered battlefield (land count5→6, hand8→7). Second Forest remained in hand;
  one notification said already played a land this turn. Hand7/lands6/stack0 stayed unchanged.
  Capture:real-land-limit-feedback.png. DOM confirmed exactly one toast container.
- R1/U1 could not pay Quiet Demise (1B). Adding B1 enabled it. Entering then canceling targeting
  preserved all three mana, seven hand cards and an empty stack.
- Sudden Insight consumed U1 plus B1 for its generic cost, leaving R1; hand7→6 and stack0→1.
  Four passes resolved it: library53→51, hand6→8, graveyard2→3, stack0. Native blue and generic
  payment still work with the new UI gates.
- A failed/unpayable Spark displayed the required-colors explanation without being cast.

Hand pointer cancellation now clears drag ownership/body styling, ignores unrelated pointers,
and does not swallow the next primary action. Unit regressions exercise pointer7 vs pointer8
and cancellation; this is pointer-event coverage, not a physical touch-device certification.

Generated tokens previously lacked a deck oracle id and were incorrectly badged as manual.
RulesCoverage now supplies one policy to ViewProjector and GameSessionService's manual-action
guard: literal tokens with embedded definitions are enforced; copies with an oracle id keep
that card's admission status, and unidentified ordinary cards do not gain coverage. Tests
cover projection, replay/resume and both oracle/token cases. The restarted dense host now
shows all168 cards, six distinct local Saprolings with alternating tapped states, and no manual
adjustment dock. Capture:real-generated-token-coverage.png. Hiding Mira kept self height
308.4, self card width87 and hand100 unchanged; remaining opponent cards enlarged48→51.

Current validation:1775 full client tests passed,720 API tests passed,1363 rules tests passed.
Final production build passed with the previously noted unrelated card-modal budget warning.
Changed feedback/decision code lint has0errors,3 advisory complexity/size warnings. Normal
API restored. No temporary CJS edit scripts remain.

Current QA games: dense2bad52ae-1ac1-4a08-b6bd-3d8745d04295 on4324..4327; cleanup
 e941ffa3-7637-41eb-ba9f-60244e596260 on4328..4331 is now Turn5/Alex/main, hand8, six
lands (Mountain/Island/Swamp tapped), R1 pool, graveyard3. Both are disposable test positions.

Remaining board acceptance work includes inspection/context menus for cards in public zone
viewers (currently hover-only), and a final scope audit of zone operations versus what the
existing rules engine actually implements. Command-zone casting/tax is not currently supplied
by the engine and must not be represented as working merely because its zone is displayed.
Production lobby creation/invitation is still an older separate flow. These limits are not
proved away by the passing basic-board checks above.

### Shared card inspection across every visible card area — 2026-10-22

Added one standalone CardOptionsMenuComponent for hand, battlefield, public zone viewers and
stack entries. It owns placement, focus, arrow/Home/End keys, Escape dismissal and outside
click handling. Removed the duplicated hand/battlefield menu templates, styles and document
listeners. Inspection clicks stop propagation before emitting, preserving the earlier fix
for immediate outside-click closure. Menu destruction restores the original card focus when
appropriate; closing a native zone dialog keeps its native focus restoration.

The preview selector resolves selected card ids from the viewer's hand, public graveyards,
exile and command cards across all seats, and authorized stack-card metadata. It deliberately
does not search other players' private hands; regression tests also exercise an accidentally
populated opponent-hand DTO. Zone cards support right-click and Shift+F10/ContextMenu. Their
inspector closes the native viewer before selecting the card. Missing-art inspectors show
rules immediately and wrap long names; touch inspectors also show details without hover.

Native production checks on the four-account cleanup table:
- Own graveyard Sudden Insight opened the correct persistent inspector after the viewer closed.
- Mira's graveyard card worked through Shift+F10. Escape closed only the menu, leaving the
  zone dialog open; opening the menu again and inspecting then closed the viewer correctly.
- Exile Wayfarer's Relic and command Keeper of the Horizon opened their correct details.
- Hand Quiet Demise and battlefield Forest inspection still worked after sharing the menu.
- Added G1 to the remaining R1, cast Canopy Scout for1G: hand8→7, stack0→1. Right-clicked
  the stack entry and inspected its actual2/2 creature metadata without selecting a target
  or advancing the stack. Four passes resolved it: stack0, own creatures2→3, hand7; the old
  stack-object inspector disappeared when that object changed zones.

Captures:real-public-zone-inspector.png,real-readable-command-inspector.png,
real-stack-card-inspector.png. Latest full client suite1785 passed; production build passed
with the previously noted existing card-modal style warning. Shared inspection/stack/privacy
code lint:0errors. Backend suites remain720 API and1363 rules (no backend changes this pass).
Temporary edit scripts removed.

Zone-operation audit: display/privacy/inspection are now verified for all ordinary board
zones. Command-zone casting remains an actual engine gap: IsCommander is initialized and
preserved, but CastPlan has no built-in command-zone permission/tax, SpellCast records only
turn history, and Query explicitly refuses CommanderCastsThisGame because no per-game tally
is kept. A correct implementation needs persistent commander-identity cast history, command
permission and tax in the normal cost pipeline, replay tests, and the corresponding zone UI
action. A visible command zone alone is not evidence that those rules work; keep the goal
active until the remaining zone-operation scope is handled or explicitly resolved.

### Persistent commander cast history — 2026-10-22

Added GameState.CommanderCasts keyed by stable commander owner/oracle identity and caster.
SpellCast folds only actual commander casts from the command zone into this per-game tally;
hand/graveyard casts and non-commanders do not increment it. Turn changes preserve it, and
new object ids refer to the same commander. Five focused tests passed, including event replay,
partner/owner/caster separation and excluded casting zones. Full rules suite:1368 passed.
Changed files received scoped whitespace formatting. This is a foundation only: command-zone
permission, tax cost shaping, query integration, UI casting and return choices remain unfinished.
No UI behavior was changed or claimed verified during this pass.

### Commander permission and base tax pipeline — 2026-10-22

CastPlan now recognizes CR903.8 permission for an owned designated commander in command.
It adds two generic mana per prior command cast before ordinary cost reductions, including
when an alternative cost replaces the printed cost. It uses the persistent per-caster tally.
Six focused payment/permission regressions passed: first cast, repeated tax, insufficient
payment without history changes, noncommander refusal, opponent ownership and creature timing.
No UI has changed this pass. Remaining acceptance includes alternate/free/reduced-cost
interaction tests (including once-per-turn cost permissions), query integration, command-zone
casting controls, return-to-command choices and production four-player lobby flow.

### Commander cost-permission tracking — 2026-10-22

Found and corrected an interaction introduced by the commander permission road: a free or
life-instead cast applied its permission's cost rider but did not stamp the permission id,
so a once-per-turn allowance could be reused. UsesZonePermission now distinguishes the
redundant plain zone permission from an actually applied cost rider. Three regressions verify
free casts pay tax, life-instead casts pay printed mana value in life plus tax in mana, and
ordinary commander casts leave redundant plain permissions unspent. All assert the persistent
cast count and original-state immutability. Focused tests passed; scoped formatting completed.
Command-zone UI, query integration, return choices and four-player lobby remain unfinished.

### Commander ability query integration — 2026-10-22

Replaced the unsupported CommanderCastsThisGame query arm with a persistent-history reader.
Defaults to self, honors explicit selectors, deduplicates commander identities, attributes
casts to the effect controller, and supports departed-source last-known information. Removed
obsolete NoCommanderCastCount refusal and stale documentation. Query regressions cover zero,
three casts surviving turn reset, explicit Self, separate commander identities, caster counts,
departed self and noncommander zero. Both focused tests passed; scoped formatting completed.
No UI changed. Command-zone board actions and commander return choices remain unimplemented;
production four-player creation/invitation and cost-reduction acceptance also remain open.

### Commander cost reductions verified — 2026-10-22

Added five affinity/commander-tax casting regressions using the existing face-casting query
fixture, not a fixed-answer amount stub. Verified printed4 plus tax2 reduces to6/4/1/0 with
0/2/5/7 controlled artifacts, and printed4 plus tax4 reduces to3 with5 artifacts. Each nonzero
case also refuses a pool exactly one mana short; successful casts consume exact payment and
increment persistent commander history. All five focused tests passed; scoped formatting done.
No UI changed. Command-zone casting controls, commander return choices and the production
four-player lobby remain unfinished. This pass verifies affinity's existing reduction road;
it does not claim every possible Magic cost-changing ability is implemented.

### Command-zone legal-action integration — 2026-10-22

While wiring the zone UI, found LegalActions.For only considered hand cards. Added owned
command-zone objects to spell candidates (stable id order), still deferring every offer to
ICastingSystem.Check. Hand-only land offers remain unchanged. Real casting/layer/zone fixture
proves the command object is offered with priority and no tax, hidden from own offers without
priority, withheld when repeat tax is unpaid, offered with exact tax payment, and withheld
when the commander designation is removed. Focused regression passed; scoped formatting done.
Client source inspected but not changed this pass: command zone viewer currently only inspects.
Next UI work should extract HandComponent's announce/target/cast dispatch flow into one shared
service before reusing it from ZoneViewer, and gate the latter from server legal.Castable.
Do not duplicate the casting workflow or infer commander tax from printed mana in the UI.
Commander return choices and the production four-player lobby also remain unfinished.

### Shared casting workflow and command-zone UI — 2026-10-22

Added CardCastingService shared by Hand and ZoneViewer: land play, announce X, actual target
count, or ordinary cast dispatch. Removed hand's duplicate workflow. Preserved legal.castable
ids through EngineLegal -> GameView -> GameStateDto. Own command viewer offers a Cast button
only enabled for server-offered ids while UI idle; opponent zones never offer it. Viewer
closes before starting casting/targeting. Six new client regressions cover flow and gating;
51 focused tests and1791 full client tests passed. UI is UNVERIFIED in a live browser so far.
Current QA processes were not found; restart an actual-engine harness for native verification.
Follow-up: announcement selector currently searches only hand for a spell's name/text and
needs command support before calling X-command casts fully complete. Add transport/adapter
regressions for castable ids and live verification, including current rules build/tax payment.
Temporary edit scripts removed. Commander return choices and four-player lobby remain open.

### Command cast prompts and offer-transport regressions — 2026-10-22

Announcement selector now resolves name and rules text from own command cards as well as hand.
Added tests for command-source prompt/target count, numeric engine castable id normalization,
empty offers on absent legal payload, and adapter preservation.75 focused client tests passed.
Rebuilt current actual-engine QA host on4320..4323: game c284ddd7-5d4b-48ee-96da-8b705be2bbc2,
exec session85542. Earlier process discovery inside sandbox missed elevated processes; verified
and stopped API9868 and old QA42820 to clear build locks. Normal API restored, session72605.
Browser tab30/commanderQA is on the new QA table, pending native casting verification.
UI remains unverified until that check is completed. Temporary edit script removed.

Native command-zone verification on tab30/current QA: Cast disabled with0 mana; tapped Forest,
Island, Mountain, Swamp for GUBR; own command Cast became enabled. Clicking consumed all4mana,
command1->0, stack0->1 with Keeper of the Horizon; hand remained5. Screenshot:
real-command-zone-cast.png. This proves casting onto stack, not resolution/repeat-tax UI yet.
Screenshot at1280x720 reveals local battlefield vertical clipping (creature top/name outside
visible lane, lower card edge near bottom). Treat this as an open layout defect requiring
current-build reproduction/fix. Do not claim the entire table layout is accepted from this.
Production build for latest prompt completed; existing card-modal style-budget warning only.

### Hidden battlefield scroll diagnosis — 2026-10-22

Reproduced current1280x720 clipping via DOM geometry: local app-zones top306.19 but creatures
row top242.19, battlefield scrollTop68/clientHeight266/scrollHeight391. All ancestor scroll
positions were0. Invisible off-card tooltip content contributes overflow; focus could scroll
the overflow:hidden battlefield without any visible scrollbar. Changed only battlefield
container to overflow:clip so it cannot be scrolled by focus restoration. Production build
passed with the existing card-modal style-budget warning. Native post-fix verification follows.

Post-fix native proof at1280x720/current QA game: battlefield overflow=clip, scrollTop0,
host top306.19/bottom572. All8local app-card bounds now top315.39..bottom567.80, inside host.
Opened empty command viewer, closed it, then keyboard-focused tappedForest and Tabbed to
Island: scrollTop remained0 and first card top315.39. Screenshot visually confirms full
creature/artifact faces and complete land row:real-battlefield-clip-fixed.png. This fixes
the reproduced clipping; do not extrapolate this one viewport to mobile/dense acceptance.
Current commander still on stack1; four-seat resolution/repeat-tax UI and commander return
choices remain to verify/implement. Production four-player lobby also remains unfinished.

### Optional commander returns from graveyard and exile — 2026-10-22

Added per-state CommanderReturnCandidates populated by ObjectMoved for commanders arriving
in graveyard/exile, removing prior object ids on later moves. SBA requests a return choice
in APNAP owner order, after ordinary permanent actions, and avoids overriding pending choices.
ChoiceRequested consumes that object's eligibility, so declining cannot prompt indefinitely.
ChoiceSystem handles optional return through the normal zones system; only the owner can
answer. New choice kind has its own rule903.9a. Four real zones/layers/SBA tests exercise both
zones and both answers, wrong-player refusal, replay, no repeated prompt, and fresh eligibility
after a subsequent zone change. Scoped formatting done; full rules1388 passed.
This is CR903.9a only: hand/library replacement choices under903.9b are still missing. Native
UI return-choice acceptance, simultaneous multi-owner coverage, commander resolution/recast
and production four-player lobby remain open. Current running QA host has older binaries;
rebuild/restart separately before claiming native commander-return verification.

### Four-owner commander return sequencing — 2026-10-22

Added real zones/layers/SBA regression for four commanders dying together, each controlled
by a different seat from its owner. Confirms all4 reach graveyards before the first prompt;
active seat2 gets first choice, followed3,0,1. Mixed return/decline answers produce exactly2
command/2graveyard objects, no repeated or skipped prompt, empty pending/candidate sets and
matching replay object ids. ObjectCeasedToExist now removes stale return candidates; regression
also verifies remaining queued ids still name actual objects.5 focused return tests passed.
Scoped formatting done. UI behavior did not change this pass; native return verification and
hand/library replacement choices remain open, as does production four-player lobby work.

### Commander hand/library replacement road — 2026-10-22

Added built-in CR903.9b candidate to ReplacementSystem for commander ObjectMoved proposals
to hand/library. It participates in replacement ordering and then asks its owner return/keep
through existing ReplacementOrder routing. The original move remains unapplied while pending.
The context carries proposed event and all already-used replacement keys. Acceptance redirects
to command; decline preserves destination. Acceptance permits the commander rule to apply again
if another replacement redirects the same event back to hand/library. Four focused tests
passed for both destinations/answers, unchanged source before answer, exactly one final move,
commander designation and replay. Scoped formatting done.
IMPORTANT remaining acceptance: zones.MoveAll currently stops a batch on a pending replacement
without recording the remaining moves; inspect/fix continuation before claiming multi-card
bounce/tuck effects work. Also exercise competing replacement effects and actual interpreter
resumption, then native UI choices. Native QA still uses older rules binaries. Production
four-player lobby and remaining responsive acceptance remain open.

### Interpreter resumption for zone-move questions — 2026-10-22

Found interpreter Relocate merged zone-system questions without marking the walk halted or
recording its coordinates, so later moves/effects ran while a commander move remained pending.
Extracted ProposeThrough as shared proposal/continuation plumbing for direct events and zone
moves. Questions retain the effect path, proposal ordinal and replacement context. Resumed
proposals that ask another question are wrapped again instead of losing their walk. Move and
shuffle loops now stop on a halted Relocate. Real zones/replacement/layers/interpreter test
confirms commander-first two-creature bounce pauses before either move/life gain, then owner
return completes both moves and gains3 once. Five commander-replacement tests passed.
Remaining: dynamic selector snapshots when earlier objects moved before a later pause, multiple
commander pauses, direct ZoneSystem.MoveAll batch resumption, shuffle owner memory, competing
replacements and native UI acceptance. Do not claim all multi-card continuations complete.

### Later and repeated movement pauses — 2026-10-22

Expanded real interpreter commander-bounce regression to first/second commander placement
and two commanders. Before fix2of3 cases failed: dynamic Each selectors dropped already moved
ids, shifting proposal ordinals so a later commander remained on battlefield. MovementObjects
now snapshots the instruction's selected ids in replayed choice memory; missing earlier ids
still consume their saved proposal positions without repeating a move. Partial arrivals are
preserved for RememberAs, and internal movement memory is removed when the instruction finishes
so a later loop iteration does not reuse an old selection. All3 focused scenarios now pass,
including second prompt and life gain only after all decisions. Scoped formatting done.
Direct ZoneSystem.MoveAll continuation, shuffle memory, competing replacements, native return
choices, remaining responsive checks and four-player lobby remain open.

### Shuffle continuation across commander choices — 2026-10-22

Expanded real interpreter movement tests to shuffle-into-library instructions. Reproduced
failure before fix (shuffle-before-tests.log). Shuffling now uses the saved movement list and
proposal ordinals, records affected owners in replayed question marks, and restores them when
resuming after earlier objects left. Every affected library shuffles once after all moves,
including when a commander returns to command instead; temporary owner/movement marks clear
on completion. Five focused movement scenarios pass, including different owners and two
commander pauses, with following life gain deferred until decisions finish. Scoped formatting
done. Direct ZoneSystem.MoveAll, competing replacements, live choices, responsive checks and
production four-player lobby remain open. No UI changed this pass.

### Competing commander replacements verified — 2026-10-22

Added3 real zones/replacement/layers/interpreter tests with a stolen commander and a separate
replacement that substitutes life gain for a move to hand. Initial order belongs to affected
controllerP1; selecting commander replacement asks ownerP0. Return goes to command without
life gain; decline preserves the other replacement's gain forP1 and leaves commander on board.
Selecting the card replacement first raises no obsolete commander question. All3 focused
cases passed, proving ordering and owner decision remain separate. Scoped formatting done.
This verifies this competing-effect shape, not all replacements. Direct zone-batch continuation,
live commander return choices, responsive acceptance and production four-player lobby remain
open. No production code or UI changed this pass.

### Pending proposal privacy prerequisite — 2026-10-22

While tracing durable zone-batch state, found the owning viewer's pending choice could expose
Context.Event in full, including hidden card definitions in a proposed move. ChoiceSecrets
now strips that server-only proposed event from projected own-choice data as well as filtering
secret answers; authoritative state and event log remain unchanged. New projector/serialization
regression proves no hidden oracle/name in the owning viewer's serialized choice, visible
options/input metadata intact, opponent options empty, and original event still restored by
log serialization/replay. Focused regression passed; scoped formatting done. This prerequisite
is complete, not the direct batch continuation itself. Zone-batch continuation, native returns,
responsive acceptance and production four-player lobby remain unfinished.

### Durable direct zone-move batches — 2026-10-22

MoveAll now records the remaining proposals in ChoiceContext.DeferredZoneMoves when a
replacement pauses a batch. Proposals are read from the original board; resumed moves allocate
fresh ids/timestamps from current counters to avoid collisions. IZoneSystem.ResumeMoveBatch
handles the queue and carries remaining moves onto subsequent questions. ChoiceSystem resumes
the queue after replacement resolution, preserving it if that resolution raises another choice.
ChoiceSecrets removes the queue from projected own-choice views. One real Wave2 engine test
runs4moves with2commander decisions, serializes/reloads before each answer, mixes keep/return,
and proves4moves exactly, correct zones, distinct ids and no deferred data in the player view.
Focused regression passed; full rules1403 passed; scoped formatting done.
Remaining batch acceptance includes replacement sources leaving within a simultaneous batch,
nested batches and post-batch turn actions. Native return-choice verification, responsive
checks and production four-player lobby remain unfinished. Do not claim completion yet.

### Cleanup completion after paused batches — 2026-10-22

TurnSystem.Advance now refuses to advance while any choice is pending. Discard completion no
longer unconditionally advances after MoveAll: it marks AdvanceAfterZoneBatch on an interrupted
choice, carries it through further replacement questions and deferred moves, then advances once
the whole batch finishes. Real Wave2 test uses two explicitly harmless replacement-order fixtures
on hand discards: cleanup/active player stay unchanged at both pauses, both cards reach graveyard,
and the final answer begins next player's upkeep. Direct Advance on a pending discard emits no
events. Both zone-batch focused tests passed; scoped formatting done.
This covers delayed cleanup advancement, not all cleanup/SBA/trigger rules. Native return-choice
verification, remaining batch snapshot/nesting cases, responsive checks and four-player lobby
remain open. No UI changed this pass.

### Live four-seat commander return choices — 2026-10-22

Added isolated --returns QA fixture on4332..4335, outputbin/Returns, current game
2140a67f-2fbd-4202-98b1-78f147005e3b/session19524. Four designated commanders newly enter
G/E via real zone events and SBA raises the first owner decision. Native checking found two
UI defects: optional single-pick choices had no decline control, and hand land play appeared
available while a server choice was outstanding. Added owner-only Choose none/Leave in current
zone control (guarded against required choices); Hand now includes server choice in idle gating.
79 focused tests passed, production build passed with existing card-modal budget warning.
Native updated-client sequence: Alex declines graveyard return (G2/C0), Mira returns from
exile (E2->1/C0->1), Theo returns from graveyard(G2->1/C0->1), Jules declines exile return
(E2/C0). Other seats show waiting rather than answer controls. Hands show current-decision
restriction until the last answer; then Alex can play Forest again and pass priority returns.
Captures real-optional-commander-return.png and real-commander-returns-complete.png.
Tabs31..34 returnQA0..3 marked for handoff. Normal API restored session70841. Temporary edit
script removed. Native hand/library replacement choices and tax recast still need acceptance,
along with batch snapshot edge cases, responsive checks and production four-player lobby.

### Production table creation boundaries — 2026-10-22

Current-state lobby audit found the gap is larger than an old two-player form: PlayApiService
calls /api/games/decks,/opponents,/invites endpoints, but current API has no invite controller,
service or persisted invitation model. /lobby still uses preset-deck/player-token creation;
/play uses the unavailable invite endpoints. Neither is an accepted production entry flow.
Decks are Collection(IsDeck) with main/side/maybe rows, total normal/foil/etched quantities and
CommanderOracleId. Any new lobby must bind only the authenticated user's own deck selection,
persist invites/readiness and start exactly once after2-4distinct seats consent.
Added GameSessionService creation guards for2-4 seats, nonblank account/name and unique account
ids.10focused tests pass: invalid sizes/identities persist nothing;2/3/4seat games survive
restart with correct account-seat mapping and nonparticipants get no seat. Scoped formatting
done. This is a server boundary prerequisite, not a completed lobby. No UI changed this pass.

### Lobby seat-consent domain model — 2026-10-22

Added immutable GameTableLobby/GameTableSeat in Domain. Host chooses capacity2-4, format/life/
honour setting and own deck. Only host invites, bounded/distinct accounts; invited seats begin
without deck selection. BringDeck binds to the actor's own existing seat; the future service
must verify actual deck ownership. BeginStart requires host and full ready table, freezes
mutation, CompleteStart binds game id, and failed-start reopening preserves selections. Guest
leave removes own seat; host leave cancels. Revision increments support future optimistic store
writes. Seven focused tests passed covering2/3/4players, consent, unauthorized actions, duplicate
and full seats, startup freeze/completion/retry and capacity bounds. Scoped formatting done.
This is the domain foundation only, not a shipped lobby: persistent storage with revision
compare-and-swap, authenticated deck validation, endpoints and UI still need implementation.
No invitation was sent to a real account and no UI changed this pass.

### Persistent lobby storage and revision claims — 2026-10-22

Added PersistedGameLobby snapshot and normalized GameLobbyMember index with cascade relationship,
revision concurrency token and indexes. Generated additive GameTableLobbies EF migration (only
new lobby/member tables and indexes; existing snapshot changes preserved). GameLobbyStore uses
scoped DB contexts, member-scoped read/list (bounded50), transactional revision compare/update
plus membership replacement. A failed stale write cannot change membership. Store registered in
DI. Three real SQLite tests passed: invite/leave read visibility and reload; stale competing
invite rejection; two genuinely concurrent startup claims yield exactly one successful claim,
with selected decks and Starting state preserved. Scoped formatting done.
This prevents duplicate lobby state claims, not yet duplicate game creation across a crash:
startup orchestration/idempotency still needs implementation. Authenticated deck loading,
endpoint DTOs/redaction, invitation routes and UI also remain unfinished. No real invitations
sent. Normal app startup applies its configured pending migrations when restored.

### Account-bound lobby decks — 2026-10-22

Added scoped IGameLobbyDecks/GameLobbyDecks with account+IsDeck filtered list/load. Summary
choices exclude card identities. Main rows expand normal/foil/etched quantities, side/maybe
never enter play, one selected commander is removed into command, matching format and actual
commander presence required. DeckGate enforces the table's admission setting; malformed counts
and expansion outside1..1000 are refused. Four SQLite tests pass for ownership/nondeck refusal,
quantity expansion/commander separation, format/missing commander, and unverified main cards
with honour-system off/on. Scoped formatting done; feature documentation updated. This is not
full format deck legality and does not implement lobby start/endpoints/UI. Those remain open,
as do remaining board acceptance cases. No real invitations sent.

### Authenticated lobby service and initial endpoints — 2026-10-22

Added GameLobbyService composing persisted member-scoped store, account lookup and own-deck
loader. Create resolves names from accounts, requires capacity-matching invitees and validates
host deck; BringDeck validates the authenticated actor's deck against fixed table format/admission
and uses expected revision; leave/cancel uses member identity and optimistic save. DTOs expose
seat readiness but deck id only to its owner. Outsiders get no table; stale writes are409.
Added authorized /api/games/lobbies list/create/get, /decks, /{id}/deck and /{id}/leave controller
routes, thin service calls; record request annotations target constructor parameters.
Four service tests on real SQLite cover2/3/4players, account/deck binding and DTO redaction,
outsider reads, stale updates, cancellation, stolen deck and duplicate host invite refusal.
Focused tests pass; API build passed; scoped formatting done. HTTP/runtime endpoint acceptance
is not yet verified. Starting a game, account picker, client lobby integration and exactly-once
startup/crash recovery remain unfinished, along with remaining board acceptance. No actual
user invitations were created outside tests.

### Retry-safe lobby startup verification — 2026-10-22

GameLobbyStartup and authorized start route now claim ready tables, load each owner's deck,
create a game under the stable lobby ID, and link the saved game back to the table. Atomic
insert prevents a retry from overwriting an existing game log. Tests cover 2/3/4 seats,
recovery after saving a game before linking it, repeated completed startup, host/revision
checks, invalid-deck reopening with seat consent preserved, and successful retry. The real
SQLite concurrent insert test proves exactly one initial game log wins. Six focused tests
and the full API suite passed (754 tests), recorded in lobby-startup-tests.log. No UI changes
or live HTTP acceptance in this pass. Client lobby integration and broader table acceptance
remain unfinished; this is not a claim of complete multiplayer delivery.

### Client table API contract — 2026-10-22

Added typed GameTable/seat/deck/create models and GameTableApiService in the sibling client.
The service calls implemented /api/games/lobbies routes for member list/detail, own decks,
creation, deck consent, leave and startup. All mutations carry the observed revision; no
actor identity or other players' decks are supplied. Three focused ChromeHeadless tests pass
for routes, four-seat creation payload and revision-bound mutations/start result. These files
are formatted. This is the transport foundation; /play still uses the obsolete invitation
component, so the production lobby remains unconnected and UI unverified. Next work is account
selection plus replacement of that component, followed by live multi-account acceptance.

### Lobby player search — 2026-10-22

Added authorized lobby players route and service query returning only account ID/username.
Active actor validation, own/deleted exclusion, prefix length bounds and stable 20-result
limit are verified against SQLite. Full API suite:755 passed (lobby-player-search-tests.log).
Client table API now sends search as a query parameter, with focused request coverage.
No lobby component/UI changes yet; account selection UI and complete multiplayer entry
acceptance remain unfinished and unverified. Profile contract documentation updated.

### Production lobby component replacement — 2026-10-22

Replaced /play GameLobbyComponent's nonexistent two-player invite API usage with typed
GameTableApiService. Supports member tables/readiness, matching-format own deck selection,
revision-bound consent, leave/cancel, start/recover and join saved game; four-seat creation
uses up to three distinct searched accounts. Explicit refresh exposes remote readiness;
stale mutations preserve server error and reload. Production build passed (existing unrelated
card-modal SCSS budget warning). Focused component tests cover distinct four-seat creation,
start result navigation and stale consent refusal. UI is unverified: browser rendering/mobile
acceptance, host-only control presentation, search empty/loading feedback, and complete live
four-account creation/consent/start flow remain required. No real invitations sent this pass.

### Host-specific lobby controls — 2026-10-22

Lobby DTO now projects IsHost per authenticated recipient; service tests for2/3/4 seats
confirm hosttrue/guestfalse. UI hides start/recovery from guests and labels own leave vs
host cancel distinctly. Production client build passes with existing card-modal budget
warning. Focused API tests3passed; component coverage exercises host/guest controls.
Normal API restored and dev client4200 started. Browser /play correctly redirects to login;
authenticated lobby visual acceptance is still unverified. Continue using isolated QA
accounts for the multi-account lobby flow; do not create invitations for real users.

### First real browser lobby creation — 2026-10-22

Added --lobby QA mode using private in-memory SQLite and actual lobby store/service/startup,
GUID-mapped dummy accounts and fixture-owned decks. Host running4336-4339, process session99494.
Browser tab37 loggedqa0, searchedqa and sawqa1/2/3 only, selected allthree and own practice deck,
created actual four-seat lobby. Rendered host ready/three guests choosing, start disabled.
Evidence:lobby-four-seats-created.png. Full consent/start not yet verified. Browser also
revealed navbar Play still points /lobby (old component); fix redirect/navigation next.
Normal app sign-in untouched; only dummy QA invitations created. Keep tab37 for continuation.

### Browser four-account consent and startup — 2026-10-22

On isolated4336-4339 host, qa1/2/3 each signedin, loaded /play, selected their own practice deck,
and confirmed consent at current revision. Guests have Leave but no Start. Host refresh shows
all4ready and enables Start. Startup persisted gamec43d63f6-ed3f-4b2c-85a8-b56396710133.
Guestqa1 joined via Join game; actual board shows ownseat2/7handcards, threeopponents each7hidden,
40life,65library,1commander, andqa0Upkeeppriority. Screenshot:lobby-created-game-joined.png.
Host refreshed and sees Join game too. Rebuilding while old host tab open removed lazy chunks;
refresh recovered. Avoid rebuilding mid-session for further gameplay acceptance.
Fixed /lobby to redirect /play; initial exact-text patch had not matched, browser reproduced
oldpresetUI, scopedregex correction applied and productionbuildpassed. Guest reload from
/lobby visibly landed /play. Browser tabs37host/39qa1board/40qa2lobby/41qa3lobby retained.
Full fresh-game phase/tap/payment/zone checks across all seats and responsive lobby remain open.

### Fresh lobby-created game: land, payment and resolution — 2026-10-22

Continued actual gamec43d63f6-ed3f-4b2c-85a8-b56396710133 across four browser accounts.
All seats joined from lobby. Upkeep priority passed0/1/2/3 into Draw; Draw passed allfour
into PrecombatMain. Multiplayer first-turn draw observed: hosthand7→8/library65→64.
Lands disabled duringUpkeep, enabled ownmain. Host played Mountain:hand8→7/BF1; second
Mountain disabled with already-played-land reason. Clicking BFland tapped it and produced
R1 floatingmana. Relic(cost1) enabled, cast:mana removed/hand7→6/stack1; other sorcery
plays blocked with nonemptystack. Priority passed0/1/2/3, stack emptied and Relic entered
BF; Mountain remains tapped and no mana remains. Actual UI evidence:
fresh-game-paid-artifact-resolved.png. This proves the fresh-game basic payment sequence,
not all cost variants or complete turn/combat acceptance. Continue current four tabs37/39/40/41.
