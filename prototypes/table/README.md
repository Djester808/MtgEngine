# Interactive table prototype

From this folder, run `npm start`, then open http://127.0.0.1:4317. Run `npm test` for the model checks. No package installation or API is required. Stop the preview with Ctrl+C.

This is a local board and zone prototype using illustrative cards and a Commander-style fixture. It does not connect to the existing engine or enforce a full Magic game. All data stays in memory; reloading restores the sample table. Switching player count resets the fixture.

## Interactions

- Matching lands and tokens stack automatically. The top-right Board settings gear includes toggles for these defaults and optional grouping of other same-name cards. New automatic groups compare counters, tapped states and characteristics. Tapping an existing member keeps that pile intact. Piles cap at three cards.
- Drop a land directly onto another land or land pile to stack them, including different land names. Drop on an empty mana spot to move it. Matching tokens also stack on drop; other same-name drops require the matching-cards setting. Every member remains a distinct selectable object.
- Slot spacing is stable across ordinary moves and updates. Columns use a centered, fixed pitch with a 28-pixel gap for individual cards and extra room for piles. Resizing recenters the occupied group, including even card counts, while retaining order and identities.

- Drag cards between defined slots in their own battlefield area. Creatures, other permanents and mana sources have separate placement areas. Occupied slots swap; cross-area drops return to the original slot. Dragging a group member into another slot in the same area separates it.
- The Board settings gear opens Stack lands, Group tokens, Add 5 sample tokens, and Auto arrange. Matching land or token objects overlap with a count handle. Drag the handle to move the pile; click it to inspect each individual member or spread the group.
- Tab reaches every card, including covered cards. Arrow keys swap a card or group into an adjacent slot within the same area; Enter uses the primary action, subject to timing restrictions. Shift-click inspects a land. Escape cancels a drag. Keyboard focus brings covered cards to the front.
- Arrangements survive game-state updates and perspective changes; resizing reflows and centers them. Reloading or resetting the sample fixture clears arrangements. Manual movement is confined to the current player’s battlefield.

- Opponents are across the top, your battlefield below, and your hand along the bottom. Each player has one continuous battlefield, with creatures forward and mana sources behind. Compact side strips hold status and expandable zones. There are no scrolling battlefield lanes. Stack controls sit below the boards.
- On desktop, opponents, your battlefield and your hand share one fixed-height screen without page scrolling. Your battlefield uses front and mana rows; opponent panels mirror the player battlefield: lands run across the upper row, with creatures and support permanents in the lower row facing the player. Card sizes adapt to the available panel dimensions, with larger player cards and smaller opponent thumbnails. Hiding opponents increases the remaining panel widths and their card sizes. Crowded regions overlap cards while exposing individual selectable strips. Hover or keyboard-focus a card for an enlarged preview. Click your land to tap for mana with priority; Shift-click inspects it. Dragged tapped cards keep their rotation, preserve the grab point, and settle smoothly into a valid slot. Side-zone drawers expand inline and offer a full browser.
- Collapse opponents to enlarge the remaining areas. Busy board replaces the sample state with 24 permanents per player.

- Select two, three or four players and switch viewing seat. On phones, select an opponent to focus.
- Open every player's library, hand and graveyard; inspect shared battlefield, exile, stack and command. Opponent hands and all library contents render as counts.
- Select any visible card. Change its zone, inspect owner and controller, tap/untap, add/remove counters, change control, or choose a defending player for a creature you control.
- Change life totals, inspect player details, adjust sample mana, draw from your library, and create an individual sample token.
- Pass priority around the table. A full round of passes resolves the top stack entry. Advance the sample turn, inspect activity, read the board guide, and reset sample state.

## Architecture

- `model.js`: pure table state and transitions; validates topology and membership. Separate individual card identity from rules-object identity. `TableStore` offers a narrow dispatch/subscription boundary.
- `app.js`: presentation and interaction adapter; renders viewer-relative seats and zone inspectors. Views use projections rather than retrieving private faces for display.
- `arrangements.js`: independent presentation positions, group membership, bounded group movement, cancellation snapshots and stale-object cleanup.
- `battlefield-layout.js`: pure geometry adapter that fits card objects into the available surface; no state mutation or rules logic.
- `styles.css`: shared shell and app tokens. `battlefield.css`: continuous battlefield layout, side strips, compact controls and responsive behavior. `components.css`: reusable cards and native dialogs. Uses the client app’s system fonts, charcoal surfaces, warm text and purple accents without external assets.
- `server.mjs`: local static preview only; serves an explicit file allowlist.
- `model.test.mjs`: meaningful transition and invariant checks using Node's built-in test runner.

The engine adapter should eventually replace the sample transition implementation behind the store interface. Do not move full hidden game state into production browser clients. This local fixture deliberately contains every player's sample cards so perspective can be explored; it is not a security boundary.

Player departure, attachment editing, face-down inspection permissions, arbitrary card definitions, persistence and network synchronization remain outside this prototype. The sample timing, combat and effect support is described below. Player details explicitly label commander damage and cast counts as placeholders. The board specification remains in `research/BOARD_AND_ZONES.md`.

## Hand and primary actions

Your battlefield and hand keep their dimensions when an opponent is hidden. Remaining opponent cards enlarge within the opponent panels, with lands above creatures. Player names open details; there are no separate Details buttons. Only opponents have a Hand shortcut. Mana is a non-interactive floating pool display, populated by legal sample land taps; mana empties at step boundaries. This is a demonstration, not a full mana-ability/payment/phase implementation.

Drag the hand divider upward/downward to resize, or focus it and use Up/Down (Home/End for limits). Hide hand/Show hand collapses and restores its chosen size. Drag cards within the hand to reorder, or use Alt+Left/Right; ordering is presentation-only and survives table updates. Right-click a visible card (or use Shift+F10) for a menu with Card inspector. Left-click performs a supported primary action: lands add mana when allowed, and creatures declare attacks or blocks during the appropriate action. Turn steps and casting timing are now enforced for the sample cards; sample combat is supported, while arbitrary card effects remain unimplemented.

## Turn controller (current behavior)

The table starts in Untap with an empty stack. The visible phase/step button opens the complete sequence. Untap is performed automatically; Continue to upkeep opens the first priority window. The top-row Pass priority button explicitly simulates the indicated priority player passing; click the priority label or switch Perspective to act as another player. A full uninterrupted round of passes resolves only the top stack item, returning priority to the active player. With an empty stack it advances the step. Taking an action resets the pass succession.

The sequence is untap, upkeep, draw, precombat main, beginning of combat, declare attackers, declare blockers, conditional first-strike damage, regular damage, end of combat, postcombat main, end step, cleanup, then the next player. Draw happens automatically on entering the draw step; the starting player skips their first draw only in a two-player fixture. Mana empties at every step boundary.

- Left-click your untapped basic land with priority to add mana immediately. It stays tapped until its controller’s untap step; clicking it again cannot undo the action. Mana-during-payment transactions and general activated abilities are not implemented.
- Left-click a hand card to cast it. Instants (or sample flash cards) require priority. Other spells require your main phase, priority, and an empty stack. All illustrative spells currently cost one generic mana, paid from the floating pool; explicit sample effect definitions run on resolution; arbitrary card text is not interpreted. Permanents enter the battlefield; instants/sorceries go to the owner’s graveyard.
- Left-click a land in hand during your main phase with priority and an empty stack to play it, once per turn. Lands do not use the stack.
- During your declare attackers action, choose an opposing player in the inline Attack selector, then click eligible creatures. Click again to deselect. Confirm attackers commits the set together, taps attackers without vigilance, and grants the active player priority. Tapped creatures and creatures newly controlled this turn cannot attack without haste. New tokens follow the same restriction.
- No attackers skips blockers and damage. Otherwise each defending player selects blockers in turn order and confirms their declaration before priority opens. First-strike damage is included if any attacker or blocker has first/double strike. Damage assignments are reviewed before simultaneous damage and lethal-creature checks.
- Normal cleanup grants no priority. If the active player holds more than seven cards, switch to that perspective and click excess hand cards to discard. Finish cleanup starts the next player’s untap. Cleanup triggers, replacement effects and exceptions require the future full engine.

Card-inspector fixture edits are separately labelled and bypass gameplay restrictions for test setup. They are not casts or legal game actions. Timing references: official Comprehensive Rules 117, 500–514, 601 and 605 at https://magic.wizards.com/en/rules (verified against the June 19, 2026 PDF).

## Blocking and combat damage

During Declare blockers, click the acting-player label to view that defender. Select an untapped creature you control, then click an attacker aimed at you. Multiple creatures can block the same attacker; one normal blocker can block only one attacker. Select a blocker again to choose a different target, or click it twice to remove its assignment. Blue outlines show the current blocker and legal attacker targets. Card labels use stable A/B object references and connecting lines show attacks and blocks. Confirm blocks for that defender; multiplayer defenders act in turn order starting after the active player. Blockers stay untapped. New creatures may block despite summoning sickness.

After everyone passes in the blockers step, combat damage opens a no-priority assignment checkpoint. Default assignments are supplied. Damage split lets you divide an attacking creature’s power freely among multiple blockers under the current rules; there is no obsolete blocker-damage ordering requirement. Assign the source’s full power. Trample requires lethal damage to each blocker before player damage. Save validates the whole split; Deal damage applies it simultaneously and then checks for creature deaths before giving priority back to the active player.

Supported sample keywords: flying, reach, menace, first strike, double strike, trample, deathtouch, lifelink, indestructible, vigilance, haste, and defender. These are simple fixture keywords, not an Oracle text interpreter. Sample counters count as +1/+1 for combat stats. Marked damage remains visible through the turn and clears at cleanup. Lethal damage or zero toughness moves creatures to their owner’s graveyard, regardless of controller; dead tokens cease to exist. A blocked creature stays blocked after its blockers leave combat. Indestructible prevents damage destruction but not death from zero toughness.

Attacker/blocker labels and links show combat directly on the battlefield. The unrelated Combat button has been removed. The prototype does not implement arbitrary card effects, damage prevention/replacement effects, full blocking requirements, regeneration, planeswalker/battle attacks, commander damage/replacement choices, or player elimination. Combat references: Comprehensive Rules 509, 510, 702, and 704.5 at https://media.wizards.com/2026/downloads/MagicCompRules%2020260619.pdf.

## Targeting, effects, and the stack sidebar

The stack lives in a collapsible right sidebar. Its top entry resolves next; each entry shows its caster, effect, and chosen target. Priority is shown beside the phase/step in the top turn row. Pass priority simulates the indicated player declining to act. All living players passing consecutively resolves one top spell, or advances the step if the stack is empty. It does not skip a whole turn. The prototype lets you simulate every seat; switch perspective to cast or activate for that player.

Sudden Insight now draws two cards. Use Spell samples + beside the hand to add Cinder Spark (3 damage to a creature or player) and Quiet Demise (destroy a creature). These illustrative spells each cost one generic mana. Continue to upkeep, tap a land, click a spell, then click a highlighted creature or player name. Choosing the target casts the spell; it remains in the sidebar until all players pass. Cancel targeting or Escape spends no mana and keeps the card in hand. Right-click still opens card options.

Targets are checked at casting and resolution using object identity. A creature leaving and returning is a new object. An illegal sole target makes the spell fail to resolve. Damage marks creatures and lethal creatures die; destroy respects indestructible, tokens cease, and cards go to their owners' graveyards. Shroud and opponent hexproof are checked for these sample targets. Effect definitions are typed data, not card-text parsing.

This subset supports one required target and fixed effects. It does not yet support multiple targets, optional targets, modes, X, protection, damage prevention/replacement, counterspells, triggered abilities, or losing for drawing from an empty library. Drawing takes as many cards as remain. These are prototype rules, not integration with the production engine. Target-resolution reference: Comprehensive Rules 608.2b in the linked June 2026 rules PDF.
