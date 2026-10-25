# Library implementation review

Current review: see `DECK_SCOPE_CORRECTION.md`, approved cleanup 2026-10-25.
The active keyword/payment expansion was removed after explicit user approval.
The checkpoints below predate removal and are retained only as history.

## Corrected scope: deck definition and basic interactions

The user rejected the keyword/rules expansion. The current implementation boundary
is documented in DECK_SCOPE_CORRECTION.md: reuse Collection/CollectionCard for
saved decks, StartingCard for game input, and the existing ordered GameState zone
for live card instances. Basic operations are count, draw, shuffle, inspect/search,
reposition/arrange, existing zone movement and opening/mulligan orchestration.

Draw, shuffle and ordered matching now have single basic-operation owners.
Manual search depends on ILibrarySearchInteraction; its current rules adapter
owns effect construction and existing continuation behavior. Scry/surveil planning
and orchestration have moved into Interpreter, with answer validation in Choices.
These are layer separations, not deletion of all existing keyword behavior.

After this cleanup, unchanged full suites pass 2,103 rules and 846 API tests.
The rebuilt backend also passes the four-player basic search test with private
inspection, pending-choice reload and result delivery
(library-core-boundary-search-live.log). No client changes were made in cleanup.
No commit. The following older review describes the expanded implementation and
must not be treated as the user's current acceptance checklist.

## Historical expanded review

The scope remains library operations and their Commander opening boundary.
Nothing is committed. This review describes implemented and verified behavior;
it does not declare full MTGO equivalence.

## Current behavior

- Shared engine owners handle draw, shuffle, search, inspection, arrangement,
  scry/surveil/mill and opening-hand decisions through event-reduced state.
- Authorized choices and inspections survive supported replay/reconnect paths;
  hidden continuation data is removed from player-facing payloads.
- Single searches and completed capacity searches submit directly. Partial and
  optional searches retain their appropriate completion controls.
- Scry offers top/bottom card menus, shows local destination decisions and sends
  a complete partition without an extra top-order prompt. Retaining visible cards
  for correction deliberately differs from the recorded MTGO presentation.
- Selecting every offered surveil card for the graveyard finishes directly;
  partial selections retain confirmation and correction.
- Top-order inspections offer sequential Put on top actions. Last placement becomes
  first in the resulting library; numbered drafts remain visible for correction.
- Library windows move, resize and retain separate own/opponent geometry. Settings
  imports/exports versioned geometry without private card or game information.
  Files are our format, not a claim of MTGO settings-file compatibility.

Saved-deck startup now has two- and four-seat integration coverage for a deck
edited after joining: invalid size creates no game, correction allows retry,
and repeated startup does not duplicate the persisted game.

## Verification

Current recorded passing totals: **2,103 rules, 846 API, 1,919 client tests**.
The latest client verify includes formatting, lint, UI checks, coverage and the
production build. The existing card-modal stylesheet warning remains.

Fresh four- and two-player opening runs pass on the current worktree.
Four-player live runs also cover complete scry, independent window roles,
inspection expiry and reload. A separate two-player opening run checks its first
draw variant. Real file upload/download checks verify exact content, malformed
file rejection and application to an actual game inspection. The acceptance
index links the exact logs and limits for each requirement.

![Separate own and opponent library windows](C:/Users/John/Documents/Projects/MtgEngine/tools/TableQa/library-all-disclosure-owner-3-desktop.png)

![A visible scry destination decision](C:/Users/John/Documents/Projects/MtgEngine/tools/TableQa/library-scry-first-decision-landscape.png)

These synthetic fixtures do not establish artwork loading; separate image/error
acceptance is indexed in LIBRARY_UI_ACCEPTANCE_MATRIX.md.

![Retained library window order after focus leaves](C:/Users/John/Documents/Projects/MtgEngine/tools/TableQa/library-panel-retained-order.png)

## Comparison status and remaining acceptance work

1. Overlapping-window activation and retained stacking now pass four-player live
   checks, including background clicks and keyboard focus. Local collapse remains
   an intentional addition to the recovered native library template.
2. Keep hidden bottom-card ordering explicitly unverified against MTGO. Public
   video confirms two bottom menu actions finish directly, but not hidden order.
3. Reconcile any remaining library-specific action descriptors only when an actual
   producer is evidenced. Generic casting flags are not library requirements.

The local rule, privacy and replay guarantees are independently tested. They do
not prove superiority over unavailable MTGO server code. Fixed acceptance tests
must not be weakened to obtain a green run.
