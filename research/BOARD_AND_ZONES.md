> Historical design and integration notes. The current implementation is models-only; see [the foundation checkpoint](FOUNDATION_INTEGRATION.md).

# Board and zones for two to four players

## Foundation

Build one table supporting two, three, or four individual players. Two players play a duel; three or four use free-for-all with attack-multiple-players and unlimited range of influence. Format rules, such as Commander, are a separate configuration. Player count alone does not select starting life, deck size, commanders, or mulligan rules. Teams and shared team turns require a later explicit variant.

The board is a view of the game. The engine stores players, objects, zones, and relationships. The screen arranges the information each viewer is allowed to see. Screen position never determines ownership, control, legal targets, or turn order.

The rules basis is the repository's August 7, 2026 Comprehensive Rules, especially 102, 109–110, 400–408, 708, 800, 802, 806, and 903. The [official rules page](https://magic.wizards.com/en/rules) is the source for rules release updates. This specification defines our initial implementation contract; it does not claim these requirements already pass in the current engine.

## Seats and perspective

### Implemented foundation (October 21, 2026)

Branch `codex/multiplayer-table-implementation` begins the production implementation in
`MtgEngine.Rules/Views/TableView.cs`. The existing per-player `GameView` now includes an
additive `Table` field for two-to-four-player games, emitted through the existing
`ViewUpdated` message. It references already-authorized public object views; no raw game
state or additional private-zone content is exposed.

The contract places the viewer in `Self` and lists `Opponents` in circular seat order for
the top of the screen. Every seat keeps its original player ID and seat index. Controlled
battlefield objects are partitioned into creatures, lands, and other permanents; land
creatures appear once in the creature row. Exile shortcuts group by owner, independently
of battlefield control. `StackTopFirst` explicitly supplies sidebar display order while
the underlying stack remains bottom-first.

This supersedes the early four-player diamond diagram below: the reviewed prototype puts
all opponents across the top, with their lands above their creatures, the viewer below,
and a collapsible stack on the right. Piles, pixel coordinates, hiding opponents, hand
resizing, and hand sorting remain client presentation state.

Eight new engine tests cover every viewer at two, three and four seats, control versus
ownership, disjoint rows, stack order, private-zone isolation, and invalid topology.
The Angular client has not yet consumed this field; the reviewed prototype remains at
`prototypes/table`. Next: add the typed client adapter and replace the single-opponent
selectors with the table contract, retaining existing authoritative legal-action offers.

Give every participant a stable PlayerId and SeatId. Record a circular seat order at game creation. Record the starting player separately. For examples below, the established turn direction follows A → B → C → D → A. Extra turns and turn-control effects change execution, not seating.

Each player sees themselves at the bottom. Rotate presentation around that viewer while preserving circular order. Never rename players or rebuild identities when rotating the view. The active player, priority holder, and player answering a choice are separate indicators; they may be different players.

| Players | Viewer at bottom | Other seats | Established circular order |
| --- | --- | --- | --- |
| 2 | A | B at top | A → B → A |
| 3 | A | B upper left, C upper right | A → B → C → A |
| 4 | A | B left, C top, D right | A → B → C → D → A |

These are screen slots, not a rule that the next player must be clockwise on every device. Display a turn-order ribbon so direction is explicit. B's four-player view places B at bottom, C left, D top, and A right.

### Two players

```text
                  B player panel and hand count
                  B lands and other permanents
                  B creatures and combat area

             SHARED STACK AND TABLE STATUS

                  A creatures and combat area
                  A lands and other permanents
                  A player panel
                  A visible hand
```

### Three players

```text
       B player area                 C player area
       B controlled cards            C controlled cards

              SHARED STACK AND TABLE STATUS

                   A controlled cards
                   A player panel
                   A visible hand
```

### Four players

```text
                         C player area
                         C controlled cards

 B player area        SHARED STACK AND        D player area
 B controlled cards   TABLE STATUS            D controlled cards

                         A controlled cards
                         A player panel
                         A visible hand
```

All card text remains upright to the viewer. Each player area has library, graveyard, command, and exile shortcuts with counts; command and exile shortcuts open filtered views of shared zones. They do not create additional zones. The shared stack is one ordered list for the whole table, with the next object to resolve clearly marked.

On narrow screens, keep the viewer's hand and controls accessible, show all player summaries, and let the viewer focus one opponent's battlefield at a time. Preserve a visible stack indicator and turn/priority indicators. Target selection must allow switching opponents without losing the current choice. Do not shrink four complete playmats until the cards become unreadable. These layouts are definitions, not verified client screens.

## The seven normal zones

| Zone | Rules instances | Default information | Order | Board presentation |
| --- | --- | --- | --- | --- |
| Library | One per player | Count visible; identities and sequence hidden unless permission grants access | Preserve exact sequence | Face-down deck, count, permitted top-card view |
| Hand | One per player | Count visible; holder sees faces, others see only permitted cards | Display order is personal organization | Viewer's hand tray; opponent count or backs |
| Graveyard | One per player | Faces visible | Preserve exact sequence, including top | Count and top card; full ordered browser |
| Battlefield | One shared | Faces visible subject to face-down and phasing rules | Screen arrangement has no general rules meaning | Group primarily by current controller |
| Stack | One shared | Public spell/ability information subject to applicable hidden characteristics | Preserve exact sequence; last added on top | Shared resolution list with source, controller, choices and targets |
| Exile | One shared | Face up by default; face-down access requires permission | No universal stack order; preserve linked groups and relevant history | Shared browser, optionally filtered by owner/source/group |
| Command | One shared | Public subject to specific rules | No universal stack order | Commanders, emblems and supported variant objects |

There are seven zone kinds, but **3N + 4 zone instances**: 10 for two players, 13 for three, and 16 for four. Create the shared zones even when empty. Ante is outside this initial supported scope.

Library order must have a documented top and bottom. Keep the existing internal convention of library top first and stack bottom first unless a deliberate migration changes it. Graveyard presentation must explicitly identify its top rather than infer it from an arbitrary sort. A sorted browser may offer a visual sort while preserving the authoritative order and a way to inspect it.

A hand remains a hidden zone even when an effect reveals its cards. A public zone can contain face-down objects. Zone classification alone does not decide whether a particular viewer can inspect an object.

### Areas that are not additional rules zones

Creature rows, land rows, support rows, attack lanes, a resolution animation area, a selected-card panel, and a mana pool are presentation or game-state concepts. Tokens are objects on the battlefield, not a token zone. Phased-out permanents stay on the battlefield and are treated as nonexistent as the rules specify; phasing is not a zone change.

The sideboard and other cards outside the game belong to a separate match inventory. Outside the game is not a zone (400.11). A companion begins outside the game; using the companion special action moves it to the hand, not the command zone. Commander designation persists across zones and does not mean that the commander is always in the command zone. Commander cast counts and commander combat damage are tracked separately from location.

## Each player's area

Every player panel shows name, seat/turn-order position, life, public counters, available mana, hand count, library count, graveyard count, and participation status. Format-specific details include commander damage by individual commander and command-zone cast counts. Show public designations such as monarch or initiative when relevant. These are player state or designations; the badge itself is not a permanent.

Group controlled permanents into creatures, mana sources, and other permanents for initial readability. These are movable display groups. A land creature is one object that can be displayed in the creature group with a mana indicator; never duplicate it as two independent cards. Cards changing types may move display groups without changing zones.

Display tapped status, counters, marked damage, power/toughness, loyalty or defense where relevant, face-down status, attachments, phasing status, and current combat participation. Distinguish a newly controlled creature from an actual rules determination that it can attack or activate a tap ability. The engine supplies those permissions.

Visual piles of identical tokens may reduce clutter, but retain each object's identity, status, counters, attachments, and targetability. Expand the pile to select an individual object. Grouping cannot erase differences that matter to a choice.

## Ownership and control

Ownership identifies whose card or token it is under the rules. Control identifies who currently controls a permanent, spell, or ability where applicable. Neither is determined by where a card is drawn on screen.

* A owns a creature and B gains control: the creature remains on the same shared battlefield, moves into B's displayed group, and retains A as owner. A control change does not make a new object merely because the layout moves it.
* That creature dies: absent a replacement, it enters A's graveyard, not B's. The zone change normally creates a new object identity.
* A controls an Aura attached to B's creature: render the Aura with its attachment or a visible link and retain A's controller badge. Proximity does not transfer control.
* A casts a spell and B responds: both entries occupy the same stack regardless of their controllers. An activated ability can remain on the stack after its source leaves the battlefield.
* An exiled card appears in A's exile shortcut because A owns it: that is a filter on shared exile, not an A-owned exile zone. Access and play permissions may belong to a different player.

Do not require every object in every zone to have a meaningful current controller. Cards in hands, libraries, graveyards, and exile generally have owners and may have permission holders without having controllers. A stored fallback controller used by old code must not become a public rules claim.

## Visibility and inspection

Build a separate projection for each viewer. Hidden faces must never be sent to an unauthorized client and merely covered by a card back. Remove names, Oracle IDs, characteristics, definition references, targets, and internal choice data that would reveal the hidden information.

Represent each visible item as a known face, a face-down object with its permitted public characteristics, or a count-only hidden collection. Permissions include looking at specific cards, revealing cards to everyone, and temporarily viewing an ordered subset during a choice. Looking and revealing are distinct. Owning a face-down exiled card does not automatically grant permission to inspect it.

Do not expose stable hidden card identifiers that let an opponent follow a previously revealed card through a shuffle or hidden randomization. Public handles, temporary choice handles, and internal identities can be different. Previous knowledge is history, not continuing permission to see a card's current hidden identity.

Spectators receive an explicitly selected public perspective. Reconnection reconstructs the same authorized view. The event stream and animations must obey the same visibility rules as the current-state snapshot.

## Combat between several players

The battlefield remains shared during combat. Each attacker has an explicit defending player or attacked planeswalker/battle object and its relevant defending/protecting player. With attack-multiple-players enabled, A may send different attackers toward B, C, and D during one combat. Choosing an opponent's screen area is only an input gesture; the engine validates the resulting declaration.

Draw arrows or labeled attack groups from each attacker to its actual defender. Attach each blocker to the attacker it blocks. Blocking permissions come from the engine; cards visually near an attack do not automatically become eligible blockers. A defending player ordinarily declares their own blockers for creatures attacking them or the relevant objects they defend. Preserve relationships when an attachment, controller, or seat presentation changes.

The central area can visualize combat without becoming a combat zone. Moving a creature into an attack lane keeps the same battlefield object. One opponent leaving does not turn all remaining attacks into attacks on another opponent.

## A player leaves the table

Keep the original seat and PlayerId for history. Mark the player as having left and exclude them from subsequent applicable turn and priority rotation. Do not renumber remaining seats. A layout may reclaim space, but must preserve the established relative order and clearly show who remains.

The engine applies multiplayer departure rules (800.4), rather than sweeping that player's visual playmat into a graveyard. Objects owned by that player leave the game, including objects displayed under other players' control. Control effects granting that player control end; noncard stack objects they control cease to exist; remaining objects still under their control are exiled as prescribed. Other players' objects on their playmat may return to another controller. Departure also affects pending choices, targets and continuations. These changes must be committed before the board publishes the next actionable state.

## State contract

The following are conceptual records, not a second implementation alongside the current engine:

| Record | Required meaning |
| --- | --- |
| Table | Game identity, state revision, format/variant configuration, stable seats, circular order |
| Player | Stable identity, seat, participation state, public resources and designations |
| Zone reference | Zone kind; player identity required only for library, hand and graveyard |
| Card identity | Individual physical/card-instance identity persisting through ordinary zone changes; distinct from a printed card definition |
| Object identity | A particular rules object incarnation; normally changes on zone change, with explicit rules exceptions |
| Object location | Exactly one zone reference for every live in-game object |
| Object relationships | Owner, applicable controller, attachments, linked exile group, combat relations and source references |
| Zone contents | Object membership and any authoritative ordering |
| Viewer projection | Counts, authorized objects and characteristics, legal interactions, public relationships, current revision |
| Presentation preferences | Viewer-relative seat placement, card grouping and visual sorting; no authority over rules state |

Zone transitions pass through the engine's event/replacement pipeline. The UI requests an action; it cannot directly relocate an object between zones. A draw, discard, cast, death, bounce, exile, return, or commander move has its own rules meaning even if the screen animates all of them as card movement.

For normal moves, preserve underlying card identity and change rules-object identity as required. Do not retain counters, damage, choices or old object references by default. Apply only the explicit preservation and tracking exceptions. Double-faced, melded, merged, and copied objects require representation beyond a one-card-equals-one-object assumption; keep the contract extensible for them.

## Existing code and necessary changes

`State/Ids.cs` already has ZoneKey, and `Zones/ZoneSystem.cs` correctly distinguishes the three per-player zones from shared zones. Retain that distinction and add validation: Shared(Hand) and Of(Battlefield, player) must not silently become legitimate state keys.

`State/GameState.cs` already stores object membership and ordered zone contents. Add explicit consistency checks between zone membership and each object's location. Separate stable seats from collection indices before supporting departures and perspective changes; `Player(id)` currently indexes the player collection by ID value.

`Views/GameView.cs` already exposes shared battlefield, stack and exile, plus per-player summaries. Its command list is nested under PlayerView, while `ViewProjector.Seat` queries command using a player-specific zone filter even though ZoneSystem stores command as shared. The board contract requires an explicit shared command collection, with player shortcuts derived by ownership or appropriate rules relationships. This source mismatch needs a focused reproduction before changing it.

`Cards/Values.cs` currently includes OutsideTheGame in the Zone enum. Preserve compatibility during migration, but separate match inventory from in-game zone membership in the new contract. `GameObject` has owner, initial controller and object identity; inspect how individual card identity is preserved before using it for commander damage or hidden-information tracking. A printed Oracle ID is not a unique individual card identity.

The current view has no explicit stable seating/perspective contract or general inspection-permission contract. Define those before building a four-player screen. Existing two-player flow tests do not establish multiplayer correctness.

## Lessons applied from MTGO and Arena

The [MTGO action and state trace](mtgo/ACTION_STATE_AND_CARD_FINDINGS.md) and [Arena investigation](arena/FINDINGS.md) show clients translating actions and presenting incoming state. Use that boundary here: the authoritative engine returns locations, characteristics, legal interactions and authorized state; a client renders them. The engine may run in-process for local play or behind a service for network play with the same contract.

Borrow the separation between state, decisions and presentation. Implement multiplayer seating and visibility from Magic's rules and our own scenarios. The commercial evidence does not establish a recovered four-player Arena layout or a reusable server evaluator.

## First implementation sequence

1. Define and validate the table configuration, stable seats, and seven-zone topology for two, three and four players.
2. Establish card identity, object identity, authoritative ordering, and zone membership invariants. Route all moves through one transition boundary.
3. Implement per-viewer projection and explicit inspection permissions, including shared command/exile views and private choice payloads.
4. Build a board fixture with representative objects in every zone for all three table sizes. Check control changes, cross-player attachments, stack entries and multiple defenders using supplied fixture state.
5. Connect engine actions to that board only after these contracts pass. Then proceed to game setup and the execution coordinator for priority, choices and resolution.

### Acceptance scenarios

| Scenario | Required result |
| --- | --- |
| Create tables of 2, 3 and 4 players | Exactly 10, 13 and 16 normal zone instances; invalid player counts rejected |
| Rotate each player's perspective | Viewer at bottom; identities, circular order and state unchanged |
| Inspect all initial views | Own hand visible, other hands count-only, all library sequences hidden |
| Grant a private look or public reveal | Only the entitled viewers gain the specified information; no unrelated cards exposed |
| Inspect face-down battlefield and exile objects | Correct public representation and explicit face-access permissions |
| Transfer control of A's creature to B | Same object and battlefield zone; owner A; displayed with B |
| Put that creature into a graveyard | A's graveyard; correct new object identity and reset state |
| Attach A's Aura to B's creature | Attachment remains legible and A retains indicated control |
| Put objects from all players on the stack | One ordered stack; one unambiguous top; abilities independent of source location |
| Put commanders and emblems in command | Visible in shared command; correct player shortcuts without duplicate objects |
| Exile cards under different linked effects | Shared exile retains distinct linked groups and permissions |
| Reorder a hand or sort a graveyard browser | No unintended authoritative order or object-location changes |
| Phase a permanent out and in | Same battlefield object; no fabricated exile transition |
| Attack several players | Explicit attackers, defenders and blockers survive view rotation |
| Remove an opponent from a four-player table | Departure rules applied; stable surviving seats and usable views |
| Shuffle after a reveal | Unauthorized views cannot track hidden identities through exposed handles |
| Reconnect or watch as spectator | Reconstructed view respects the selected viewer's permissions |

Completion of this foundation means these scenarios pass and the board fixtures are visually inspected. It does not yet mean casting, priority, combat execution, or a complete game works.
