# Prototype verification

Verified on October 21, 2026. This verifies the local board prototype, not the production client or Magic rules engine.

## Automated checks

77 Node model, geometry, area and arrangement checks pass. They cover two-to-four-player topology, invalid scopes, hidden-zone projections, stable seat rotation, control versus ownership, zone-change identity and reset behavior, ordered draws, stack order, controller preservation through resolution, attacker relationships, token removal, sample priority rotation and store notifications. JavaScript syntax checks and Prettier formatting checks pass.

## Browser checks

The local preview was opened and visually inspected in the in-app browser. Desktop layouts were exercised with two, three and four players. Responsive checks used 320 × 667, 375 × 667 and 768 × 1024 viewports, plus the default 1280-pixel desktop viewport. The document did not overflow horizontally in those checks. At tablet width, buttons were also checked for containment within their player areas. The temporary viewport override was reset afterward.

Exercised controls:

- Player count: correct 10, 13 and 16 normal zone instances.
- Perspective and phone opponent focus: selected viewer and visible seat update.
- Opponent hand and library inspection: faces withheld, counts displayed.
- Shared command, exile and battlefield browsing: correct shared collections.
- Card inspection: owner, controller, location and identities displayed.
- Tap, sample counter and defending-player selection: updated permanent state and combat preview.
- Draw: hand and library counts change.
- Life adjustment: player total changes.
- Control transfer followed by graveyard move: original owner's graveyard receives the card.
- Priority pass, manual stack resolution and next turn: corresponding table state updates.
- Reset confirmation: original sample state restored.
- Phone card inspector: readable content and contained native dialog.

The browser reported no captured console warnings or errors during the checked flows. Screenshots are in `preview/desktop.png`, `preview/phone.png`, and `preview/phone-inspector.png`.

## Limits

The prototype is browser-local sample state. It has no authoritative server boundary, full rules execution, persistence, network synchronization, player departure, attachment editing, face-down access model, blockers, or damage execution. Commander damage and command-zone cast counts are labeled placeholders in player details. Real mobile devices, screen-reader navigation and production-client integration remain unverified.

## Continuous battlefield revision

Opponents occupy the top row; the viewer’s continuous battlefield is below. Status controls are on the left, expandable zone strips on the right, and phase and stack controls below the table. Independent battlefield lanes and their scrolling containers have been removed. Card geometry adapts to each surface without changing game state.

Browser checks confirmed 24 permanents per player fit without clipping or horizontal document overflow in the default 1280 by 720 view. Collapsing an opponent enlarged the others. Inline command-zone expansion and a keyboard-triggered enlarged card preview were verified. Card inspection and tapping still worked. Two-, three- and four-player layouts were exercised. The 375-pixel phone view had no horizontal document overflow. The new geometry tests also cover a 200-object surface.

## Drag and group revision

Native browser pointer drags moved an individual creature and a whole five-token pile. All five group members moved by the same delta. A life-total update preserved the manually moved creature’s position. Three overlapping lands remained individually accessible through the group browser, including the middle object. Pulling a token out reduced the group count from five to four without removing any token. Arrow-key movement was verified. Arrangement tests cover bounded movement, unchanged game state, identity retention, cancellation snapshots, controller/zone cleanup and per-player reset.

The phone group browser was visually checked with five individually readable tokens. Resizing from 375 pixels to desktop retained an 18-pixel pile stagger rather than spreading group members apart. After that adjustment, native group dragging and pulling out an individual token were exercised again. The final board screenshot is `preview/arranged-board.png`; the corrected phone group browser is `preview/phone-group.png`. Touch pointer handling is implemented, but physical touchscreen input remains unverified.

## Area constrained placement

Free placement is superseded by semantic battlefield areas and stable slots. Browser dragging moved Canopy Scout from creature slot zero to creature slot one. Dropping it onto the mana area was rejected and retained creature slot one. Land grouping remained available. New checks cover card classification, occupied-slot swapping, empty-slot preservation, newcomer placement and rejection of out-of-area drops.

## Stacking and stable grid revision

Browser verification turned automatic land stacking off, then dragged one Forest directly onto another and produced a two-land pile. Dragging a third onto that existing pile increased its count to three. A life-total update retained the manual pile and the existing creature’s coordinates. Adding five sample tokens automatically produced a five-token group; enabling other same-name grouping combined two Relics. The mana area exposed 27 spots at the checked viewport. Initial area contents are centered, and geometry checks establish unchanged slot locations when occupancy changes within capacity.

### Direct land interaction

Native pointer drag of o3 out of a three-land pile moved only that card; two remained stacked. Clicking exposed o4 tapped only o4 with no dialog; o3 and o5 stayed untapped. Click/Enter taps your lands; Shift-click inspects. Tapping detaches the card. All 39 existing checks pass.

### In-pile tapping (supersedes tap-to-detach behavior above)

Tapping pins existing membership and rotates only that card 90 degrees. Untapping preserves the same three-card pile. Native drag still separates one member into a new slot. Tap/untap uses a 260ms eased animation from the current visible transform, including reversal, and respects reduced motion. Verified three-card pile on tap and untap, two-card pile after dragging one out. All 39 existing tests pass.

### Readable card sizing and board controls

Battlefield cards now use 110 by 154 pixels consistently across all players and piles. The whole page grows vertically rather than reducing card size; there are no independently scrolling battlefield lanes. Piles fan horizontally by 46px and cap at three cards. Additional members form another pile or remain individual. Verified all twenty sample battlefield cards at the same dimensions and positive pointer hit areas for every member of a mixed tapped/untapped pile. Settings opens from the header gear. Turn order is above the board; Pass priority passes priority, while Skip to next turn is explicitly a prototype shortcut. 41 tests pass, including fixed-size crowding and pile-cap coverage.

### Drag settling, resize centering, and compact columns

45 checks pass. Added checks cover mirrored opponent rows, capacity reflow, fixed 138px single-card pitch, and centering even/odd card counts across widths. Browser checks confirmed a pair of creatures centered exactly at 1280px and 1000px viewports. Three separate lands centered at 1000px and 1600px; widths stayed 110px. Native pointer drags separated tapped o4 from a three-land pile while preserving its tapped state and 90-degree rotation, then separated the remaining two-land pile. Drop remapping uses destination coordinates when pile dissolution changes spacing. Drag transforms retain rotation; transitions are disabled under the pointer and a 180ms eased animation settles into the final slot, respecting reduced motion. Opponent mana rows are above creatures. Preview: preview/drag-resize-columns.png.

### One-screen desktop table (supersedes fixed-size, page-growing layouts)

The desktop shell reserves room for the hand and controls and shares the remaining height between opponent panels and the player battlefield. Player cards occupy front/mana rows; compact opponents use side-by-side mana, creature and support regions. At 1280 by 720, the page height is exactly 720 and the hand ends at 714; player cards measure about 76px wide and opponents about 55px. Hiding one opponent increases remaining opponent cards to about 71px. At 1600 by 900, page height is 900, player cards are 110px and opponent cards about 76px. All 96 busy-fixture cards have direct pointer hit areas and no page scroll. Mixed tapped piles retain individual hit areas; a native drag preserved tap state while separating one member. Crowded regions expose cards in ordered overlapping strips. 48 tests pass, including screen-fit bounds, adaptive opponent sizing and region-aware drop rejection. Desktop preview: preview/screen-fit-table.png. Phone focus mode retains its existing separate responsive layout.

### Restored opponent rows while retaining screen fit

Supersedes the compact opponent column layout. Screen-fit opponent mana occupies the upper half at y=16; creatures and support occupy the lower half facing the player (y=118 at 1280x720). Compact header controls reserve more table height. Page height remains exactly 720 and hand bottom is 714; at 1600x900 page height is 900 and hand bottom is 894. Opponent cards are about 55px wide versus player cards about 78px; hiding one opponent grows remaining cards to about 64px. All 96 crowded-board cards retain direct hit areas. 49 tests pass, including a regression test against screen-fit opponent columns. Screenshot: preview/opponent-rows-screen-fit.png.

### Stable self layout, hand controls, and context actions

At 1280x720, hiding Theo preserved the player board height exactly (258.890625px), player card width (78px), and hand height (90px). Remaining opponent cards grew from 55px to 61px with the same row orientation. Names open details; self Hand and all Mana shortcuts are removed. A native hand drag moved the first card to last without opening an inspector. A native divider drag resized the hand from 90px to 170px; collapse reduced it to 34px and expansion restored 170px with order intact. Keyboard divider sizing and Alt+Right reordering also passed, including order retention through a life-total update. Right-click opened Card options and Card inspector; left-click selected a creature without a dialog and did not declare an attack. Land tap showed green mana in a floating display with no dialog. Full turn/combat steps are still not connected. 51 model/layout tests pass, including immutable hand reorder and sample mana credit to a land controller. Preview: preview/hand-mana-card-menu.png.

### Prototype turn timing (supersedes manual turn and undo-tap behavior)

62 tests pass. Added timing cases cover no-priority untap, instant/noninstant restrictions, all-pass resolution, priority retention/reset, off-turn casting, one land per turn, summoning sickness and tokens, vigilance, declaration-before-priority ordering, skipped empty combat steps, conditional first-strike timing, automatic draw including two-player first draw, mana expiration, cleanup discard and turn rollover. Initial stack is now empty. Browser flow rejected a land tap in untap and left it untapped; upkeep allowed a paid instant and rejected a creature. Four explicit passes resolved only the instant and retained upkeep. Further passes advanced through automatic draw into precombat main. A land play and creature cast/resolution succeeded there. During declare attackers, the newly cast creature was rejected, a pre-existing creature was selected untapped, and confirmation tapped it and opened priority. Page height remained 720px. Full blocking, combat damage, arbitrary spell effects, trigger processing and state-based actions remain explicit omissions. Screenshot: preview/turn-steps.png.

### Blocking and combat damage (supersedes timing-only combat checkpoints)

77 tests pass. Added cases cover simultaneous exchanges and both creatures dying, unblocked life loss, blocker ownership/tapped restrictions, flying/reach and menace, freely chosen multiple-blocker splits, rejected invalid totals, first-strike blockers, double strike and persistent blocked status, trample/deathtouch/lifelink, indestructible, marked damage clearing, token deaths, stolen creatures dying to owners, multiplayer defender order including active-seat wraparound, tapped-after-declaration blockers and zero toughness. Damage previews are a no-priority turn action; damage and lethal checks finish before active-player priority.

Native browser flow assigned Cloudwing Drake to Canopy Scout by selecting the blocker and clicking the attacker. Stable labels B25/A1 and an SVG block link appeared. Confirming blocks and passing opened the damage editor. A two-blocker scenario then split the Scout’s two damage as 1 to Drake and 1 to Saproling. After confirmation, the Scout took four simultaneous damage and moved to Alex’s graveyard, Saproling died and ceased to exist, and Drake survived with one marked damage. The Combat report listed all four hits and both deaths. The page remained 720px high with all zones and hand visible. Previews: preview/combat-blocks.png, preview/combat-damage-split.png, preview/combat-damage.png. Remaining omissions are documented in README and the report.

### Targeting and stack sidebar

84 tests pass. New cases cover player damage, lethal creature damage, atomic invalid-target rejection, destroy and indestructible, leave-and-return identity invalidation, caster-owned draws across perspective changes, and hexproof targeting.

Browser checks at 1280x720: the stack is on the right, collapse reduces it from 166px to 34px, priority and Pass priority share the top turn row, and the Combat button is removed. Page height remains 720px. Selected Mira's name for Cinder Spark; four passes resolved it and changed life from 40 to 37. Quiet Demise targeted Cloudwing Drake and moved it to Mira's graveyard. Sudden Insight drew two cards and reduced Alex's library from 12 to 10. Stack entries show their effect and chosen target. The latest screenshot is preview/targeting-stack-sidebar.png.
