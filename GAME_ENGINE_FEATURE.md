# Game engine — rules, session, board

The playable game: `MtgEngine.Rules` (the rules engine), `GameHub` (the live session), the card
pool in `MtgEngine.Api/Cards`, and the board in `mtg-client/src/app/board`. This document is the
implementation; **`MtgEngine.Api/Knowledge/comprehensive-rules.txt` is the rules themselves** and
stays the authority on what the game does. Read that for the rule, this for how it is built.

## The rule this feature turns on

**The engine is the authority. The board's legality checks are a courtesy, and may never permit
something the engine forbids.**

The board asks some rules questions before it offers an action — can this creature attack, is
this castable, which ability is ready. It does that because an action offered and then refused is
worse than one never offered: declaring an attack with one ineligible creature throws away the
*whole* declaration, and the player finds out from a toast after the fact.

That makes a handful of rules deliberately implemented twice. Both copies live in one place each
with the rule number written beside them — `mtg-client/src/app/game/play-legality.service.ts` on
the client, the engine everywhere else — because the only way to notice the two drifting is for
both to be legible. Where they disagree, **the client is wrong**.

## Four decisions the whole thing rests on

**State is a fold of the event log.** `GameReducer.Replay(game.Log)` must equal `game.State`, and
there is a test saying so for every feature that touches state. This is what buys replay of a
reported bug, persistence (`PersistedGames` stores the log, not a state snapshot), and
reconnection. It is also why a decision a player makes is an *event*, never a captured
continuation — see "The game stops and asks" below.

**Characteristics are computed, never stored.** A permanent holds its printed card, counters,
status, damage and attachments. Current power, toughness, types and abilities come from
`Characteristics.Of(...)`, applying CR 613's layers. The previous engine mutated state instead and
produced "the lord died but the +1/+1 stuck" bugs that could not be retrofitted away.

**And *controller* is one of them.** Control-changing effects are layer 2 (CR 613.1b), so the
controller stored on an object is only where it started. Eight separate places were reading it as
though it were where control *is* — which meant a stolen permanent was not pumped by its new
controller's lord, was still hit by sweepers aimed at its old one, could not have its abilities
activated, could not be sacrificed or tapped for a cost, could not convoke, was not offered when
an effect said "choose a permanent you control", did not untap in its new controller's untap step,
and did not count towards "you control two or more other lands". They now all go through
`Game.ControllerOf` or the computed characteristics.

The lesson generalises past control: **any read of `obj.Card.*` or `obj.ControllerId` where a
computed characteristic was meant is the same bug.** Two were found by failing tests; the other
six by grepping for the shape of the mistake, which is far cheaper than waiting for each to
surface.

**Hidden information is projected, not flagged.** `PlayerViewProjector.Project(state, viewer,
abilities)` builds a per-player `GameView` from which libraries, opponents' hands and face-down
cards are *absent*. A `GameState` is never serialised to a client. There are tests asserting this
negatively — the serialised view for one player must not contain the other's hand.

**Priority is modelled for N players.** No `OpponentOf` anywhere. An object resolves when all
players have passed in succession (CR 117.4).

## The game stops and asks

Anything the rules give a player a choice about becomes a `PendingChoice` and the game halts —
not just that player, the whole game. Nothing else may happen while a choice is outstanding.

`ChoiceKind` is the list of questions: mulligans, which cards to bottom, the legend rule, the
order of your simultaneous triggers (CR 603.3b), which replacement to apply next, dividing combat
damage (CR 510.1c), discarding to hand size, discarding because an effect said so, choosing a
triggered ability's targets (CR 603.3d), and whether to pay an optional cost (CR 601.2b).

**The optional payment is the one whose answer decides what the rest of the effect *is*.** It
works without a continuation because the choice carries a *locator* — which permanent, which of
its abilities, which effect inside it — and the branch is read back out of the card's own
compiled definition when the answer arrives. A replay reaches the same offer because it reaches
the same card. Anything else that needs a mid-effect answer should be built the same way.

**A cost is not paid by asking.** Costs that need a choice — "Sacrifice a creature", "Discard a
card" — take the payment *with* the activation (`ActivateAbility(..., costPayment:)`), so the
engine never suspends a payment. That is not a shortcut: paying is part of an action the player
is taking, so they can choose before they commit, and a suspended payment would be exactly the
continuation the log cannot rebuild. The whole cost is checked before any of it is spent
(CR 601.2h), and `AbilityView.CostChoices` tells the board to ask first.

Two things are easy to get wrong here and are worth knowing before adding a kind:

- **Resumption is an explicit branch per kind** (`Game.Resume`), not a lambda, because a
  continuation cannot be folded from a log and a game that is mid-question has to replay as a
  game that is mid-question.
- **Options must be distinguishable.** A board with three Grizzly Bears on it offers three
  identical buttons, and a player cannot tell which they are picking; options are labelled with
  whose permanent it is. The answer itself carries the object's id, so a pick is never ambiguous
  to the engine — this is purely so it is unambiguous to the person.

## How a card gets behaviour

**Cards are compiled from their rules text, not written one at a time.** There are ~32,800
playable cards and ~59,000 lines of rules text between them, but only ~32,000 *distinct* lines
once names and numbers are normalised out, and the head of that distribution is steep — the
thirty commonest shapes are a fifth of all text. Magic is written to templates, so the work is a
compiler over those templates.

`MtgEngine.Rules/Cards/CardCompiler.cs` offers each line to a chain of matchers in frequency
order; the first that recognises it contributes an ability. Anything nothing recognises lands in
`CompiledCard.Unhandled` rather than being ignored — a silently half-implemented card is worse
than one a deck check can refuse, and those lines counted across the corpus **are** the work
queue. `EffectPhrase` reads a single sentence of effect text and is shared by every matcher, so
adding a sentence form adds it to spells, activated abilities and triggers at once.

Three things exist to keep the compiler honest:

- **`CardCompilerCoverageTests`** runs it over the whole Scryfall corpus, prints coverage and the
  commonest unread lines, and ratchets a floor. Coverage is not something a pass/fail suite can
  express — every test can be green with eighteen cards implemented.
- **`CardCompilerWorkQueueTests`** splits each unread line into the part that defeated the
  compiler — trigger condition, activation cost, effect sentence — and ranks *those*. It exists
  because the whole-line ranking went flat: no single line blocks more than about forty cards,
  while seventeen thousand sit one line short. The leverage moved into the shared vocabulary,
  where "when ~ enters, draw a card" and "{T}: Draw a card" are two lines and one missing
  sentence. It also ranks the *pairs* blocking cards that are two lines short, which answers a
  question single lines cannot: which two shapes recur together, so that two fixes pay jointly
  where neither pays alone. It asserts nothing; the ratchet holds the line, this says where to push.

  Its most useful ranking took four corrections to become honest, and every one of them had put
  fictional work at the top: a naive `Split('.')` that cut inside a quoted granted ability and
  reported the orphaned `"` as the single biggest blocker in the corpus; sentences fed to the
  parser *alone*, so "then shuffle" — legal after a search, meaningless before one — led the list
  on 487 cards while blocking none; a 96-character shape truncation that filed eleven different
  Act of Treason variants as one row naming a line that had worked for months; and counting every
  failing sentence in a line, so a line with three of them contributed three entries and fixing
  the top one finished nothing. It now ranks **only lines with exactly one unreadable sentence**,
  and prints a whole example line beside each shape. Working from shapes alone means guessing at
  the surrounding words, and that guess was wrong every time it was made.
- **`CardCompilerInvariantTests`** checks every fully compiled card for properties that must hold
  for *all* of them — unique ability ids, every effect's target index in range, no targeting mana
  ability (CR 605.1a), no ability repeatable from a graveyard, no replacement pinned to the stack
  on a land. With 32,765 cards and a hundred-odd behaviour tests, most compiled cards are never
  played by anything; this is what covers them.
- **`GenerativeEffects`** builds continuous effects from a *name* like `pump:+3/+3` rather than a
  registry, because a pump is a family, not an effect. The name is what lands in the event log,
  which is why it is readable.

**A list the compiler has to remember is a bug waiting to happen.** Three separate switches knew
which effects carry a target index — the compiler's shifter, the invariant test's checker, and a
third that had already fallen behind. They are now one type-keyed table, `EffectTargets`, whose
`HandledTypes` is *derived from* its entries rather than restated, with a reflection test asserting
it covers every `IEffect` that has a target slot. The same shape of guard covers the event
serializer. Where a list cannot be eliminated, something has to check it.

That guard fired on seven consecutive pieces of work and was right every time, so its failure
message now contains the exact lines to paste, shaped for a nullable or non-nullable index. It
also looks for any property named `*Index` rather than literally `TargetIndex` — `Fight` names its
two slots `TheirIndex` and `MyIndex` and was invisible to it — while excluding `EffectIndex`,
which ends in the same word and is the opposite thing: a locator saying where an effect sits
inside its own ability so that a deferred question can find it again. Shifting one of those breaks
the lookup rather than fixing an index.

**An ability's effects are a tree, not a list.** An optional payment holds the two branches it
chooses between; an intervening-if holds what it guards. Every deferred question has to find its
own effect again after the resolution is over, and looking that up by position in the *top-level*
list found the branch's owner instead. `EffectTree` walks the whole thing, finding children by
reflection over properties that hold effects so that a new branching effect is walked without
anyone remembering to add it. Until it existed, a choice nested inside an offer was refused
outright — and lifting that refusal immediately exposed a live bug behind it: "You may sacrifice a
creature. If you do, draw a card" was reading the two clauses as alternatives and *overwriting*
the first, so every card of that shape drew the card without ever sacrificing anything.

**A diagnostic that drifts from the compiler points at the wrong work.** Both of the above
re-implement small pieces of the compiler's own reading — which cost is lifted out before the
rest is checked, which sentences are restrictions rather than effects. Each time one drifted it
put something already implemented at the top of the queue: "Sacrifice ~" on 148 cards whose real
blocker was the effect, "Activate only as a sorcery" on 103 cards after it had been built. When
the compiler learns to lift something out, teach the diagnostic the same thing.

`MtgEngine.Api/Cards/StarterCards.cs` still holds hand-written `CardScript`s, resolved by
`CardPool` **by name**. Those are now conformance examples for the compiler rather than the way
cards are added; the hand-written route is for behaviour no template can express.

A card is composed from primitives rather than written as code:

| part | what it is |
|---|---|
| `Spell` | what the card does when cast — targets plus effects |
| `Activated` | abilities on the permanent (`{T}: …`), with their own targets and effects |
| `Triggers` | when-this-happens abilities; may target (CR 603.3d) |
| `Statics` | continuous effects, by CR 613 layer |
| `Replacements` | CR 614 effects, e.g. "enters with counters" |

Three things a permanent can be given that are not printed on its card, and each needed a place
to live that the card-keyed lookup did not have:

- **An ability.** `ComputedCharacteristics.GrantedActivated` holds what layer 6 added, and
  `Game.ActivatedAbilitiesOf` is what everything asks instead of asking the card. An Aura reading
  `Enchanted land has "{T}: Add {B}"` grants an ability to one permanent, not to a card — a
  lookup by card reports no such ability and refuses the activation.
- **A delayed triggered ability.** `GameState.Delayed` holds abilities an effect *created* while
  resolving, which fire once at a named step and are then gone (CR 603.7b). In the state, not in
  a field, because a permanent that is going to be sacrificed at end of turn is part of what the
  game is.
- **A type.** `GenerativeEffects.BecomesId` animates in layer 4, so a crewed Vehicle is an
  artifact creature and still a Vehicle, and a lord in layer 6 sees what layer 4 just created.

Effects available, grouped by what they act on:

| aimed at | effects |
|---|---|
| a target | `DealDamage`, `DestroyTarget`, `ExileTarget`, `ReturnToHand`, `TapTarget`, `UntapTarget`, `PutCounters`, `PumpUntilEndOfTurn`, `AttachSourceTo`, `PreventDamage`, `CounterTargetSpell`, `MoveTargetedCard`, `GainControlUntilEndOfTurn`, `DrawCards`/`ChangeLife` with an index |
| the source | `PumpSourceUntilEndOfTurn`, `PutCountersOnSource`, `RegenerateSource`, `SacrificeSource`, `UntapSource`, `ReturnSourceToHand`, `ReturnSourceFromBattlefield`, `ReturnSourceFromGraveyard`, `UnearthSource`, `ExploreSource`, `DelaySourceAction`, `PumpBlockersOfSource` |
| a group, untargeted | `ToEachPermanent` (destroy/exile/tap/untap/bounce/damage/+1+1/-1-1), `PumpGroup`, `PumpCreaturesYouControl`, `DamageEach`, `ChangeLifeOfEach`, `DiscardCards`, `MillCards`, `ExileFromTopOfLibrary`, `GivePoisonCounters`, `GainEnergy` |
| nobody — a question first | `Scry`, `Surveil`, `LookAndTake`, `SearchLibrary`, `Proliferate`, `ChooseAndMove`, `MayPay`, `FlipCoin`, `RevealAndTake` |
| the board | `CreateToken`, `CreateTokenCopy`, `CreateTokenAndAttach`, `AddMana` |
| the stack | `CopySpell` — a copy is not a card, so it resolves and stops existing rather than going to a graveyard (CR 707.10) |
| two creatures at once | `Fight` — both powers read before either damage is dealt, because a creature that dies to the fight still dealt its own |
| whatever the source is attached to | `OnAttached`, which hands the inner effects a context whose one target *is* the attachment. Every verb the target vocabulary knows arrives here already working, and the rules that come with it do too: an Aura that destroys what it enchants does not destroy an indestructible creature, and nothing in `OnAttached` knows what indestructible is |

**A number can be counted rather than printed.** `Amount` carries an optional counter, so "gain
2 life for each creature you control" is a fixed part times a board count taken when the effect
resolves. It lives on the amount rather than on each effect because "for each" attaches to
*numbers*, not to any particular thing a card does — every effect that already takes an `Amount`
was counted this way without knowing about it. The same need reached the layers later, and
`ContinuousEffectDefinition.Apply` was widened to receive the state so that "gets +1/+1 for each
creature you control" could count at apply time; `Applies` had always been given it.

**A trigger can point at something it never chose.** "Whenever this deals combat damage to a
player, *that player* discards a card" names a player decided by the event, and by the time the
ability resolves the event is long past. `AbilityOnStack` carries the subject — a player and an
object — recorded when the trigger fires and read through `PlayerScope.TriggerSubject`. It is
deliberately **not** a target: a target is chosen, legality-checked twice, and defeated by
hexproof, and putting it in the target list to save a field would make the board display it as
one.

The printed pronouns for it are **not** read from the shared vocabulary, and the measurement is
why. Of the corpus lines saying "that player", 840 are inside a trigger and 748 are not — "Target
opponent reveals their hand … *That player* discards that card" means the target. Reading them
all as subjects would have mis-compiled about half to do nothing, and because coverage counts
compilation the number would have gone *up* while the cards got worse. Only templates that write
their own ability text, and therefore know they are building a trigger, may use it.

**A permanent's characteristics need not come from its card.** A face-down permanent is a 2/2
colourless creature with no name, types or abilities (CR 707.2) — and that is not an effect
applied to the card, it is what the object *is*. `IsFaceDown` redirects where the characteristics
are computed *from*, so the card underneath is never modified and turning it face up is simply
ceasing to redirect. The CR 613 layer loop is shared between both paths, which keeps the
interesting half right: a lord still pumps a face-down 2/2.

**"It" is read only when something has been targeted.** The pronoun means the target the sentence
before named, and on a card that has targeted nothing it means something else entirely — a token
just created, a card just revealed. Guessing would aim the effect at whatever happened to be last,
so a dangling pronoun leaves the card unread.

**Three grammars, multiplied rather than enumerated.** The vocabulary stopped growing as a
product the moment these were separated:

- **`EffectPhrase.Specs.Parse`** reads a target *phrase* — "target artifact creature an opponent
  controls", "target player or planeswalker" — so each effect names its target with one group
  instead of one regex per (effect × phrase) pair.
- **`TriggerConditions.Parse`** reads a "when" *clause*, with families rather than entries: every
  step of the turn is one shape, every "[another] [type] [you control] enters/dies" is another.
- **`EffectPhrase.TryParse`** reads the effect, splitting on `, then` because that conjunction
  always joins two whole instructions. It deliberately does **not** split on " and ", which joins
  clauses on some cards and parts of one clause on others.

**The source of an ability's damage is the permanent, not the ability.** `ResolutionContext
.PhysicalSourceId` is what every effect uses. What resolves is the ability — its own object on
the stack — so reading `SourceId` attributes a Prodigal Pyromancer's ping to the ability, and
lifelink, deathtouch and every "whenever this creature deals damage" trigger silently miss.

**A creature with no script still works.** Printed characteristics and keywords come from the
card database, so vanilla creatures and keyword creatures need no entry here. A script is only
needed for behaviour the printed card cannot express.

**Add cards by teaching the compiler a template, not by writing a card.** `CardCompilerWorkQueue
Tests` names what to take next; the coverage ratchet says whether it worked. If a template needs
machinery that does not exist, that is a change to `Effects.cs` and a rules test — never a special
case for one card.

**Three questions, three kinds of test.** Coverage asks whether a line was *read*; the behaviour
tests ask whether a template *plays*; the invariants ask whether every compiled card is
*well-formed*. Each catches a class the others cannot, and every engine bug found so far passed
the first and failed one of the others.

**Coverage is not correctness, and only one of them has a test that can tell.** A template that
maps "destroy target creature" to the wrong target, or fires at the wrong moment, parses
perfectly and plays wrongly; the coverage number would not move a point. So every template gets a
test in `CompiledCardBehaviourTests` that *plays* it — built from the real oracle wording, run
through a real game, asserted on the board that results. Every card using that template inherits
the verification, which is the only way this scales: 32,765 cards cannot be tested one at a time,
and testing them one at a time would be the same mistake as writing them one at a time.

Those tests are also where the engine's own bugs surfaced, and every one had been invisible to
the whole suite:

- An **"enters" trigger never fired for a token**, because a token is *created* on the battlefield
  and never moves there.
- A **landfall trigger never fired at all**, because it looked up the arriving object's new id in
  the state as it was *before* it arrived (CR 400.7).
- **Damage from an ability was attributed to the ability**, not to the permanent — so lifelink,
  deathtouch and every "whenever this deals damage" trigger missed.
- **Every "enters tapped" land arrived untapped.** The replacement was pinned to the zone a
  *spell* is in, and a land is played rather than cast (CR 305.1), so it never applied.
- **A modal spell's own text was skipped** when modes were chosen, losing the kicked half of every
  modal card with a kicker.
- **`GameObject.ChosenModes` updates were silently dropped**, because the objects live in an
  `ImmutableDictionary` and `SetItem` skips a write when the value compares equal — and the
  hand-written `Equals` did not mention the new field. Anything added to that record must be added
  to the comparison, or its updates vanish.

- **The controller stored on an object is only where control started.** It is layer 2
  (CR 613.1b), and eight places read it as though it were where control *is* — so a stolen
  permanent was not pumped by its new controller's lord, was still hit by sweepers aimed at its
  old one, could not have its abilities activated, could not pay a cost, did not untap, and was
  not counted or offered anywhere.
- **A deferred question could not find the spell that asked it**, because a spell becomes a new
  object as it resolves — so the branch of an optional payment and the filter of a choice both
  resolved to nothing whenever the source was a spell rather than a permanent.

All of them passed coverage and failed to play. Two structural safeguards now cover the classes:
`Settle` asserts `GameReducer.Replay(log) == State` in **every** behaviour test, and a reflection
test asserts every `GameEvent` subclass is registered with the serializer — an unregistered event
saves fine and throws on the way back in, which is to say the game cannot be loaded.

## What the board is told, and why

The client cannot work rules out for itself, so the view carries what it needs:

- **`ObjectView.Abilities`** — every activated ability, with its id, text, whether it costs `{T}`,
  how many targets it needs, and whether it is a mana ability (CR 605.1a — which requires that it
  take *no target*, not merely that it produce mana). Without this the board hardcoded the word
  `"mana"` against cards whose type was Land, which made a Sol Ring untappable and a Prodigal
  Pyromancer unplayable.
- **`IsCreature` / `IsPlaneswalker` / `IsLand`** as flags rather than leaving the client to search
  the type line, which is display text.
- **Targets on a stack object**, so an opponent can see what a spell or ability is aimed at.

## Verifying a change here

Unit tests are not evidence that the game is playable — the board can be unable to reach a
correctly implemented ability. There are walkthroughs in `mtg-client/e2e` that drive the real
board in a real browser against a real engine:

| walk | covers |
|---|---|
| `_walk-the-stack.js` | LIFO, which end is the top, countering, sorcery timing |
| `_walk-combat.js` | summoning sickness, declaring, blocking, damage, a combat trick |
| `_walk-trigger-targets.js` | a triggered ability that targets (CR 603.3d) |
| `_walk-abilities.js` | activating abilities, including ones that target |
| `_check-board-phone.js` | the board at 375×667 — every claim a measurement |
| `_check-board-desktop.js` | that no phone rule escaped its media query |

They share `e2e/helpers/walk-harness.js`. Both servers must be up; the API dies whenever anything
builds the solution, because it holds file locks.

## The board on a phone

The board was restored from a commit that predated the mobile standard and had no phone layout
at all: three columns asking for 400px of fixed sides on a 375px screen, with `overflow: hidden`
swallowing the evidence. Below `$bp-phone` it is now one column, in the order the table is read
across — the opponent's numbers, their half, your half, your numbers — with the stack as a sheet
over the table rather than a third column.

Three things about it are decisions rather than mechanics:

- **The battlefield reflows; the hand pans.** The battlefield is a layout the app computed, so it
  wraps to fit and each half scrolls when it runs out of room. The hand is a layout the *player*
  arranged — the cards can be dragged into an order — so it keeps its geometry and pans, with a
  faded edge from `ScrollEdgesDirective`.
- **Manual life buttons are hidden.** The engine tracks life; those are for correcting it by
  hand, and two full-size targets each cost more of the screen than the table can spare.
- **The hover tooltip does not exist.** It is reachable only through `:hover`, and a phone has
  none. Tapping a card opens the preview panel, which answers the same question.

The trap worth knowing: **a media query adds no specificity.** A phone rule written on
`.battlefield-col` loses to `.battlefield-col.opponent-field`, so the desktop column placement
survived and the grid quietly went back to three columns — measuring 810px wide inside a 375px
screen. The phone overrides sit inside the compound selectors for that reason.

## What decides where a choice is made

Three places, and which one a mechanic belongs in is the first question to ask about it:

- **With the action.** Modes (CR 601.2b), kicker, chosen costs, convoke's helpers, crew's crew.
  Choosing is *part of* taking the action, so the player decides before committing and the engine
  never suspends a cast or a payment — which matters because a suspended payment is exactly the
  continuation a log cannot rebuild.
- **Deferred to the next settle.** Scry, surveil, discard, library search, look-and-take, the
  optional payment. The question is the last thing its ability does, so asking just after the
  resolution is the same game. `_looksOwed` and its siblings hold them.
- **Halting the game.** Mulligans, the legend rule, trigger order, damage division, discarding to
  hand size, trigger targets. `Game.Resume` has an explicit branch per kind.

A coin flip uses the deferred machinery without being a question at all: nobody chooses, but an
effect cannot reach the randomness — which lives on the game so that every random outcome comes
from one seeded source. Like the shuffle, the log records **what came down, not the roll**, so a
replay reads the result instead of flipping again.

A mechanic that needs an answer in the *middle* of an effect belongs to none of them and is left
unread.

**The locator is what makes the deferred kind reach further than it looks.** An optional payment
and a "choose a permanent you control" both need something the event cannot carry — a branch of
effects, a filter delegate — and neither is anything a log can rebuild. So the event carries
*which permanent, which of its abilities, which effect inside it*, and the engine reads the
branch or the filter back out of the card's own compiled definition. A replay reaches the same
question because it reaches the same card.

**A locator has to be resolved eagerly, and so do the targets.** The question is asked *after* the
resolution that raised it, and a spell does not survive its own resolution — it goes to the
graveyard and becomes a new object (CR 400.7). Resolve the locator when the answer is needed and
it points at nothing; run the branch against the object it finds and the targets have gone with
the old identity. Both are resolved when the request is *made* instead. This was invisible for as
long as every deferred question came from a permanent's ability, and broke the first time one came
from a spell — the counterspell tax, and then "sacrifice a creature you control" on a sorcery.

**Several keywords then cost almost nothing**, because the primitives were the right shape:
unearth is an ability that functions from the graveyard plus a delayed exile; explore is a reveal,
a counter and a surveil (CR 701.25a is exactly its "keep or bin it" half); a token with a quoted
ability needs no machinery at all, because a token *is* a `CardDefinition` and its quoted text is
just rules text the pool compiles on demand.

**A player can hold a resource that is not mana, and speed was the third.** Poison, energy and
now speed (CR 702.179) are all numbers on `PlayerState` moved only by an event, which is what
makes them replayable. Speed is the first one with a *rule* attached rather than only effects:
"your speed increases by 1 the first time an opponent loses life during your turn" is not a
triggered ability — nothing goes on the stack and nobody may respond — so it settles alongside
the state-based actions, guarded by a per-turn flag that lives on the player and therefore
survives a replay. Zero is not speed 1: "Start your engines!" only reaches a player with *no*
speed, and the automatic increase only reaches a player who already has some, so the two halves
read the same number and the distinction has to be in it.

**Protection is checked when targeting now, and the fix was the signature.** `IsLegal` takes the
source as an optional argument, so a caller that cannot say what the source is skips the check
rather than guessing — and the four callers that do know pass it, including the one that builds
the board's list of legal targets, because the board may never offer what the rules would refuse.
The two remaining unread target phrases, "target enchanted creature" and "target equipped
creature", want the same argument threaded one level further down into `ObjectFilter`.

**A target phrase can carry a qualifier, and that was worth more than any keyword.** "Target
creature with power 3 or greater", "without flying", "with mana value 3 or less" — the clause sits
between the noun and the owner clause, so it is lifted out and the remainder is read as an
ordinary phrase. That is what lets "target creature with power 4 or greater you control" work
without the noun grammar knowing anything about power. Numbers come from the *computed*
characteristics (CR 613.1), so a creature pumped to 4 power becomes a legal target and one shrunk
below stops being one; mana value is read printed, because nothing in the engine changes it.

One detail is load-bearing and looks like an omission: the noun-phrase character class admits
digits but **not** plus or slash. With them in, the pump matcher greedily reads "target creature
gets +2/+2" as a target called "creature gets +2/+2" — two tests caught it immediately. The cost
is that a phrase naming a counter by its symbol stays unread at sentence level, which is much the
cheaper of the two.

## Known gaps

Coverage is **46.8% of playable cards fully read** (15,347 of 32,765), 64.0% of all lines (39,559 of 61,846 lines of rules text).

### The compiler was not connected to the game

For most of this engine's life there was no wire between the two. `Program.cs` registered
`CardPool` — five basic lands and a small curated set — as the only `IAbilitySource`, while
`CompiledPool`, which reads a card's printed text, was constructed in the test projects and nowhere
else. Measured against the real `GameTableService`: **the deck gate admitted 917 cards while the
compiler read every line of 15,262.** Every reader written for this engine was exercised by tests
and by nothing that plays a game.

It stayed invisible for a reason worth remembering: `GameHubTests` builds its service with
`new CompiledPool()`, so **the tests exercised a wiring production did not have**.

`PlayableCards` now composes the two — a written script where there is one, compiled text otherwise
— and the gate asks its `Refuses`. The old question ("is any of five ability collections
non-empty") was wrong in both directions, and both were measured: it turned away **396** cards
whose whole text lands in a cost reducer or a keyword flag, and admitted **6,759** cards with one
ability read and the rest of their lines unread. `CompiledPool.Refuses` was written for exactly
this and had no caller anywhere in the repository.

Every member of the interface delegates, and a reflection test enforces it, because every default
on `IAbilitySource` returns *nothing*. That test earned its place on its first run: `FloatingEffect`
was missing, and left that way every temporary pump, grant and animation the compiler generates
would have stopped applying the moment the wiring went live — with nothing failing.

### A stored game forgot every transform

`PrintedCard` recorded fourteen printed fields and not `Faces`, so a card read back out of a stored
log had none — and `GameReducer.Transform`, which correctly refuses a face index the card does not
have, silently dropped every `PermanentTransformed` event in it. A saved game came back with its
werewolves on their day faces, with the day face's characteristics, and unable to flip again. **837
corpus cards carry faces.**

Nothing caught it because the invariant everything else leans on was never violated: live state and
the in-memory `Replay(log)` agree perfectly. The divergence appears only through the door
`GameSessionService` actually uses — write, re-read, fold. The soak now exercises that on every
sampled game rather than skipping the ones that had transformed something, an exclusion that was
hiding **43 of 105** sampled games in the deep slice: precisely the games that would have caught it.

### What the soak reaches, and what it did not

Widened after measuring the residue rather than guessing at it. `IsPermanent` named
creature/artifact/enchantment/planeswalker and not `CardType.Land`, and the spell soak takes only
instants and sorceries — so **778 complete cards, every land in the corpus, were touched by no test
at all**, carrying most of the compiler's mana abilities.

| | before | after |
|---|---:|---:|
| permanents played | 11,334 | 12,317 |
| activated abilities | 2,702 | 3,488 |
| triggers fired | not measured | 12,091 on 4,268 cards |
| complete cards no test touches | 778 | **0, asserted** |

Triggers were the real gap and the largest declaration kind in the corpus (5,858 cards): playing a
permanent runs its statics, pressing its button runs an ability, casting runs a spell, and none of
those makes a trigger fire.

### Ten phrases that named creature types no card has

The noun-satisfiability guard — every printed noun phrase a complete card carries, handed to the
compiler's own grammar and tried against a witness board — found twelve unsatisfiable phrases, and
ten are now fixed. Two faults accounted for them.

`SingularWord` over-reached on eight. A blanket `-ves` rule added to rescue "Elves" turned **Caves
into "Caf"** and Detectives into "Detectif"; a blanket `-ies` rule turned **Faeries into "Faery"**;
and Locus and Pegasus lost a letter for being plurals they are not. Both blanket rules are gone: the
subtypes whose plural really changes the stem are a small closed set and are listed, a word ending
in "us" is left alone, and **Aurochs** joined "Plains" as spelled the same either way.

The group grammar looked for its noun only at the front of the phrase, so one lowercase adjective
stopped the search before it arrived. "untapped Mountains you control" and "tapped Assassins you
control" kept the s: **Ben-Ben dealt damage equal to the number of "Mountains" and Lydia Frye
surveilled per "Assassins", both counting zero, both compiling as complete cards.**

Aurochs is the argument for the guard in one line — that break was *caused* by the second fix and
caught by the invariant in the same run.



### What is actually left, measured rather than estimated

The remaining work has no head to attack. There are **9,705 distinct blocking sentences across
10,659 blocked lines** — 1.1 lines each — and the thousand most common templates between them
would complete 1,889 cards, 5.8% of the corpus. The queue as a whole reaches 44.1%, so
implementing *every* template in it lands near 88%, not 100%: the rest needs capabilities that do
not exist, not readers.

That number is the reason the search method changed. Ranking sentences by how often they appear
finds nothing, and twice it found worse than nothing:

- `MTG_SENTENCE_DUMP` splits each unread line into sentences and counts *those*, so its top entry —
  "Activate only as a sorcery", 238 — is a fragment of lines that already read. A line-fold was
  built for it and reverted: **0 of 1,096** corpus cards print that sentence on its own line.
- Ranking blocker rows by their leading clause attributes a whole line to `when ~ enters` (647
  rows), a trigger that is fully supported. The gap is always in the tail.

What does pay is a **compositional** gap: a complete grammar reachable through only one door.
Three of those, found by probing a sentence the compiler reads and then saying it another way:

- The animation grammar — power, toughness, subtype, keywords, the "it's still a land" tail — could
  only be entered through a *target* phrase, so every permanent that animates **itself** went
  unread. One sibling pattern, 56 cards. The artifact half of "becomes a 3/3 Soldier artifact
  creature" is read rather than assumed, because an animated land that is quietly not an artifact
  dodges artifact removal.
- `BoardConditions` is the shared condition vocabulary for statics, activation restrictions,
  enters-with-counters and the intervening-if on triggers — and nothing could call it from a plain
  sentence. `If you control an artifact, draw a card.` did not read while
  `When ~ enters, if you control an artifact, draw a card.` did. **242 cards.** It refuses when
  `Parse` returns null: on 651 sentences, defaulting the condition to true would ship an
  unconditional card where a conditional one is printed. It cites CR 608.2c and not CR 603.4,
  because the double check belongs to an "if" immediately after a trigger condition and this is
  not one.
- Disturb is flashback's permission plus a turn-over, and the turn-over is *derived* at resolution
  from the zone the spell was cast from — the object already records that and the card already says
  which zone transforms it — rather than carried on an event whose only job would be to hold a flag.

Two synonyms were worth more than most mechanics. CR 700.4 defines "dies" as exactly "is put into a
graveyard from the battlefield", and every trigger template here is written against the short form,
so 130 cards saying it the long way were not read while the same card saying "dies" was. The
rewrite is anchored on "from the battlefield" and nothing else — "from *anywhere*" also catches a
card milled or discarded, and rewriting that would quietly narrow it to permanents.

Three findings worth keeping because they are about the instruments, not the cards:

- **The corpus is not in git.** `oracle_cards.json` lives only in `MtgEngine.Api/bin/`, and every
  coverage, invariant and dump test prints "skipping" and **passes** without it. Any fresh clone,
  CI job or worktree measures nothing and reports success.
- **A reader that never fires looks exactly like a reader that works.** The monarch condition had
  been present all along behind a pattern demanding `you 're the monarch` with a space; no card
  prints that. It compiled, it played, and the bonus was simply never on.
- **`RuleCitationTests` checks that a rule exists, not that it is the right one.** Four wrong
  citations passed it: cipher as 702.98 (Unleash), scavenge as 702.98a, transform as 701.28
  (Convert), and a chosen cost as 701.20a (Reveal).

### One capital letter, four bug reports

Four defects were reported independently — `you control a Desert` compiling and never firing, a
mass static that buffed nothing, `target Nissa planeswalker` unreadable, and a colour-disjunction
noun refused — and they were one bug. `Specs.Parse` fell back to `char.IsUpper(printed[0])` and
treated any unknown capitalised noun as a **creature type**. Every printed sentence starts with a
capital.

The failure mode is the one this project most wants to avoid: it does not refuse, it *succeeds
wrongly*. `You gain 1 life for each Equipment you control` compiled **complete** and gained 0 life
instead of 2 — measured in a real game, not reasoned about. Fixing the one table fixed Desert, Gate,
Aura and Shrine together, and reading subtypes against CR 205.3 reaches the 945 cards that name a
non-creature subtype.

The same shape had already been found and fixed in `MassStaticLine`, where `Artifact creatures you
control get +1/+1` compiled into a lord for the creature type "Artifact" — 150 lines across 129
cards, all reading as complete and doing nothing. **That is strictly worse than an unread card:** an
unread card is refused by the legality gate, while these are legal, playable, and quietly inert,
with nothing on the board to say so.

`Every_subtype_a_mass_static_names_is_a_subtype_some_card_has` now guards the half the existing
subtype invariant could not see — it walks the `*Filter` string properties of spell, trigger and
activated-ability effects, and a continuous effect carries its filter as a compiled predicate
instead.

### The founding invariant was asserted almost nowhere

`Settle` — the helper nearly every behaviour test ends with — asserted `Replay(log) == State` after
a loop whose two ordinary exits are both `return`s. It was reached only when the guard ran out,
which is to say almost never, while its own comment claimed every test asserted it. Present,
documented, and not running.

The loop is now a separate method and the assertion follows it unconditionally. Proved by mutation:
inverting it fails **805 of 1,350** tests, where before the change inverting it failed none.

### Half a card is not a read card

Three separate defects of the same shape were found in one pass, and none of them failed a test.

**A mass static with no ownership clause applied only to its controller's permanents.** Muscle
Sliver pumped no Sliver across the table; Crusade buffed only your white creatures; Illness in the
Ranks shrank only your tokens. **83 corpus cards** print a line of that shape and every one was
compiling as complete and doing half of what it says. The identical bug had already been found and
fixed in `TryGrantedAbility` thirty lines away.

**A `+X/+Y` counter on a group was silently read as `+1/+1`.** This engine reads exactly two counter
names when computing power and toughness (CR 122.1a gives a `+X/+Y` counter its own X and Y), so any
other P/T-shaped counter is now refused at `CounterKindNamed` — one place, so the group, targeted and
subject verbs inherit it together. It was **not** latent: Essence Flare, Shield Sphere and Spirit
Shackle were each putting a `-0/-N` counter recorded under its printed name and read by nothing.
Refusing them costs three cards of coverage and stops three cards being quietly wrong.

Worth recording how the second nearly escaped. A detector that mutates a printed digit and compares a
reflective fingerprint catches the *group* form, whose compiled definition is byte-identical to the
`+1/+1` version — and cannot catch the *targeted* form, where the counter keeps its printed name so
the definitions genuinely differ. The instrument understated its own finding.

### A granted ability could not ask a question

Every deferred question is answered by finding the effect again on the card that asked. A granted
ability is not on that card — it is held against the permanent it was granted to, keyed by (source,
ability) — so the lookup needs the source id, and five call sites omitted it.

The failure is silent the whole way down. The trigger fires, goes on the stack with the right source
and subject, resolves, and emits its request; the locator then finds nothing, and the request is
dropped with no choice, no log line and no error. It applied to optional payments, clashes, coin
flips, hand choices and permanent choices alike — every deferred question any granted ability could
raise.

Found while building granted ward, which was played in a real game, **measured inert** — the spell
resolved untaxed and no question was asked — and reverted rather than shipped. That is the discipline
the whole compiler depends on: a mechanism that looks finished and does nothing is the thing this
project is least able to detect on its own.

### Declined here, with the measurement behind each

Recorded so the next pass does not re-spend the cycle. Each was probed or swept, not guessed.

- **`~ remains tapped` / `it remains exiled` / `you control ~`** (~170 lines). The four biggest
  condition misses are **durations on one-shot effects** — "gain control of target creature *for as
  long as* you control this creature" — not board questions. A condition reader would answer the
  wrong question on all of them.
- **`unless its controller pays {N}`** (123 lines) and its siblings. An unless-*cost* is a decision,
  not a state; unreadable by a condition parser by construction.
- ~~**`your first enchantment spell each turn`**~~ and ~~**`Whenever one or more X you control
  attack`**~~ — **both now built**, and both are worth recording as declines that were correct at
  the time. Each was refused because the only available reading made the card better than printed,
  and each needed an engine capability rather than a cleverer pattern.

  The first was refused because `OncePerTurn` is per (permanent, ability), so a permanent arriving
  *after* an enchantment had already been cast would still trigger on the second one. `PlayerState`
  now records the spell cards cast this turn, and answers `SpellsCastThisTurnOfKind(types,
  subtypes)`. It holds the cards rather than a tally on purpose: a per-type counter answers
  "enchantment" and then stops, because "your first outlaw spell" is five creature types asked at
  once and summing five counters counts a Pirate Rogue twice.

  The second was caught by `CardCompilerInvariantTests` — six of the lines say "that many" and
  `AttackersDeclared` carried no amount, so the trigger fired and added nothing. The batch size was
  already in the log, since attackers are declared as one event (CR 508.1); only the lookup was
  missing. The subtlety is that the batch size *alone* is the wrong number for four of the six —
  Dragons, Dinosaurs, Birds, Treefolk — so it is narrowed by offering the ability's own predicate a
  synthetic single-attacker declaration per attacker, a declaration of one being exactly the
  question "does this creature answer the description".
- **`Living metal`** (13 faces) — *built*, and worth zero. All 13 are backs of the same 13
  Transformers cards, whose fronts carry `More Than Meets the Eye` plus bespoke unread `convert`
  sentences. "Faces blocked by this line" is an upper bound, never a card count.
- **`More Than Meets the Eye`** (15 cards). It compiles today as an alternative cost from hand — and
  that is wrong: the cast path takes an alternative cost unconditionally whenever the card is in
  that zone, so the card could never be cast for its printed cost as its front face. Cheaper and a
  mode short of printed, so left unread.
- ~~**The cost-modifier grid**~~ — **built**, and the counts in the original decline were wrong by
  about fourfold. Measured card by card when it was implemented: *opponents cast / more* is **3**
  cards and not 24; `This ability costs {N} less to activate` is printed on **zero** cards, every
  real printing carrying a counted or conditional tail; *abilities / more* is 1 readable of 3. The
  cell that genuinely does not exist is *opponents cast / less*, at zero occurrences. `CostReducer`
  is folded into `CostModifier` rather than sitting beside it, because `Game` reads both lists and
  a card emitting both would be discounted twice.
- **`X target <noun>`** (55 cards) — a variable *number of targets* chosen as the spell is cast
  (CR 601.2c), while `SpellDefinition.Targets` is a fixed list. **`mana value X or less`** (66) is
  worse than unread if guessed: the cast-time filter check runs *before* the chosen X is recorded,
  so any targeted form would become uncastable.
- **`at random`** (36 cards) — randomness reaches an effect only through an event the `Game` handles.
- **`destroy it at end of combat`** (4 cards) — the delayed vocabulary defaults an unknown verb to
  *sacrifice*, and sacrificing is not destroying (CR 701.21a): regeneration and indestructible
  cannot touch it, so that reading is harsher than printed.
- **The initiative** (24 cards that take it, 8 that ask for it). CR 726.2 gives it three inherent
  triggered abilities, two of which venture into Undercity — of which this engine has zero lines.
  Modelling the designation alone would compile 24 cards that then skip most of what they say.
- **Opening hand** (20 cards) — both printed shapes need a pre-game question, which needs a new
  choice kind and a declaration on the compiled card. Remembering the hand alone would be a field no
  reader could act on.

- **`Craft with artifact`** (24 cards), **`Convert ~`** (39 lines across 22 cards, nearly all unique
  long sentences), **perpetual effects** (6 of 74).



### Every line shape the compiler reads is now played by a test

Cross-referencing matcher *names* against the test suite was a proxy, and a proxy cannot give
certainty: it counted comments and test names as evidence, and missed `AttachedBuff` entirely
because the cards that use it say "Enchanted creature", not "attached".

The instrument that does give it is exact. `CardCompiler` declares **174 line shapes** as
generated patterns; reflection collects them, the behaviour suite's card text is read back out of
its own source, and each pattern is run against it. A shape no card line matches is a shape
nothing has ever played.

Eleven came back. Four were **fragments** - patterns handed a phrase the compiler has already cut
out of a line ("you cycle ~", "creatures with power 2 or less") - which cannot be found this way
and are covered through the lines that contain them. One was the reminder-text stripper, which is
not a mechanic. **Six were real**, and are now played: persist, extra blocks, power-based block
restrictions, the cycling trigger, counted cost reduction, and phantom damage.

`Every_line_shape_the_compiler_reads_is_played_by_a_test` keeps it that way. A matcher added
without a behaviour test fails there rather than sitting unplayed for a year, which is how long
exalted managed it.

It found one thing about itself worth keeping: a card whose wording runs to two lines of C# is
written as two string literals joined with `+`, and each piece alone is not a line of anything.
Phantom damage was reported unplayed when it had a passing test. **The instrument was wrong before
the code was**, which is the ordinary way with instruments and the reason the first thing to check
when one accuses you is the instrument.

### Nine thousand cards had never been played

Every game-based test in this repository used a hand-written fixture or a vanilla bear. **No card
out of the corpus had ever been put on a battlefield.** They were compiled, counted, swept and
diffed - and never played.

That is the gap no static instrument can close, and the reason is structural: a condition written
inside a trigger's predicate is a closure. Nothing can read it. The only way to find out what it
does is to run it.

`CompiledCardSoakTests` runs it. Every fully-compiled permanent in the corpus - **9,340 of them** -
goes onto a real battlefield, twelve at a time so they interact, split between two players so that
"you control" and "an opponent controls" both have something to find. Four turns each, then three
assertions that must hold whatever the card says: nothing threw, the layers can compute
characteristics for everything still standing, and `Replay(log)` still equals the state.

It found three faults on its first run, and none of them were reachable any other way.

#### And the spells were cast

The last body of code in the corpus that had never run. `Every_compiled_spell_survives_being_cast`
puts each compiled instant and sorcery in a hand, funds it, aims it and lets it resolve: **1,013
of 2,339** now do, with the same three assertions as the others.

Getting from 811 to 1,013 was four corrections and **every one of them was the harness lying about
the engine**:

- Casting is not resolving. Nothing passed priority, so each spell sat on the stack and every
  sorcery after the first was refused for a stack that was not empty.
- A field of vanilla bears is not a board. Nine of the ten commonest refusals were "illegal
  target" - for artifacts, enchantments, fliers, none of which existed.
- Enriching that board made the count go **down**, because the first permanent an opponent
  controlled stopped being a creature and every "target creature" spell was handed a relic. The
  shapes had to be typed, not just plural.
- A counterspell has nothing to answer on an empty stack, and "target spell" was the single
  commonest refusal in the whole run. The first decoy was cast by the opponent - who does not
  hold priority during your main phase, so it was never cast at all, and the fix changed nothing
  while looking exactly like a counterspell that could not be reached.

Both of those were then closed as far as they usefully go. Graveyards are seeded, and combat is
staged once per game so that "target attacking creature" has one - which needed a creature with
haste, because everything the harness builds arrives that turn and a board of summoning-sick
creatures cannot declare an attacker at all.

**It bought nine spells.** That is the honest number, and it is the point at which harness tuning
stopped paying: what remains refused is refused for reasons that are the rules rather than the
arrangements - a sorcery cannot be cast in combat, a land is played rather than cast, and a spell
wanting a tapped artifact creature an opponent controls wants a game rather than a board.

#### And then they fought

Passing priority through the declare steps declares **nothing**. Attacking is a turn-based action
the active player has to take, so four turns of passing is four turns in which no creature ever
attacks - and attack triggers, block restrictions, combat damage, first strike, deathtouch and
trample were all outside what these games touched, on every card in the corpus.

The soak now attacks with everything and blocks with everything, narrowing the batch when the
engine refuses one - a creature with defender, a restriction that forbids the batch. That is
**8,446 attacks and 1,557 blocks** across the 778 games, and it found nothing: every one of them
resolved, the layers still answered, and the log still replayed to the same state.

A result of nothing is worth reporting when the thing it ran was large. It is also why the counts
are asserted rather than printed: a soak that stops fighting passes exactly like one that fights,
and this document has already recorded two harnesses that reached nothing and looked green doing
it.

#### Long games, gang blocks, and three ways to divide damage wrongly

Four turns is not long enough for anything that counts. A Saga needs three, vanishing and fading
need as many as their counters, echo comes due on the turn after - and at twenty life a player is
dead by the third combat, so the games ended before any of it arrived. The soak now runs **ten
turns at four hundred life**, which is not a game anyone would play and is exactly the point:
these mechanics need time to show what they do.

Blocking gangs up where there is a spare creature, so combat damage has a choice in it.

That produced three failures in a row, and **every one was the harness being wrong about the
rules**:

1. A choice asking for five picks from two options is not broken - dividing four damage between
   two blockers is four picks from two answers. Taking each option once gave two.
2. One damage each in turn is illegal from the second pick onwards: CR 510.1c will not let a
   second blocker take any damage until the first has lethal.
3. All of it on the first blocker is *also* illegal, being more than lethal while the other has
   none.

The answer was to stop guessing. The harness offers each split in turn and lets the engine pick
the legal one - it already knows what lethal is, and it is the thing being tested. Teaching the
test to compute lethal would have been teaching it to have the same opinion as the code it checks.

**24,469 attacks and 1,819 blocks** later, across ten-turn games, nothing in the engine broke.

#### Four players, and a stack with two things on it

Two claims were still untested with real cards, and both were founding ones.

**The engine was built for any number of players.** The plan it was rebuilt from says the previous
engine died of a hardcoded `OpponentOf` and an if/else on "am I the active player", and that this
one would model priority for N from the start. Every soak, every behaviour test and every fixture
in this repository is two players, so the claim had never been put to a card. **3,114 compiled
permanents now play at four-player tables** - where "each opponent" is three answers, triggers from
different controllers have to be ordered in turn order, and the priority ladder comes back round
through two people who are neither attacking nor defending. Nothing broke.

**And the stack now has two things on it.** The spell soak emptied the stack after every cast, so
no spell was ever cast in response to anything: "whenever you cast", split second and the ordering
rules never saw a second object. Every other spell is now left waiting while the next is cast. The
count did not move - the same 1,022 - but the situation each one is cast into is a different one.

#### The same twelve cards, for ever

One more thing was wrong with all three soaks and it was invisible in their numbers: they batched
the corpus **in order**, so the same twelve cards shared every battlefield on every run. A lord
never met the creature it would pump, card one never met card nine thousand, and 9,340 cards were
being played in 778 fixed combinations rather than in anything like a game.

They are scattered now - ordered by a hash of the oracle id rather than by it, which is
reproducible and nothing like alphabetical. That is 778 combinations that had never been played,
for the cost of a sort key. It found nothing either.

FNV-1a rather than `GetHashCode` for the same reason the token ids use it: `GetHashCode` is
randomised per process, and a soak that groups its cards differently on every run reports a
different set of interactions each time. A failure nobody can reproduce is barely a failure.

The scatter is run over **three slices** - a different grouping each time, so 2,334 combinations
rather than 778. The first is deep (ten turns, where a Saga finishes and a fading permanent runs
out) and the other two are shallow, because breadth is what they are for. Cumulatively that is
42,777 attacks and 4,914 blocks.

The broad slices take **a third of the corpus each**, because what they are for is combinations
that have never shared a battlefield and a third of the cards regrouped gives plenty of those.
Slice zero still plays every card, so nothing goes unplayed - it costs seven minutes rather than
nine, and buys the same thing.

**Seven minutes is the honest price of this suite**, and it is worth writing next to the benefit:
four rounds of this found nothing. If the gate becomes a burden the broad slices are what to trim;
what should not be trimmed is the deep pass, which is the only place the several-turn mechanics
are exercised at all.

Four rounds in a row have now found **nothing**, against workloads considerably harder than the
round that found three bugs. That is the first honest sign that the yield is falling rather than
the search being shallow: the early rounds found something immediately every time.

The soaks together now run **9,340 permanents, 2,215 activated abilities and 1,022 spells** through
real games - two players and four, ten turns, real combat, a stack that is not always empty. Where
they stop is written down here rather than left to be discovered as confidence.

#### And then the button was pressed

Creating a permanent exercises what it does on its own. It never presses the button. An activated
ability is code that has never run until somebody pays for it, and until `Every_activated_ability_survives_being_activated`
nobody had - not once, for any card in the corpus.

That test is worth reading for how badly it started rather than for how it ended. Its first run
reported **254 abilities activated across 3,403 cards** - and passed. Everything else had been
refused, correctly, for reasons the harness had created: the permanents had arrived that turn and
most of these abilities cost a tap (CR 302.6), and the one target shape it offered was an
opponent's permanent, which "target creature you control" refuses by definition.

A test that is refused everywhere passes while checking nothing. Waiting for a clean main phase of
the next turn doubled it to 496; trying the plausible target shapes in turn took it to **2,215**.
The remaining third are refused for reasons that are genuinely the rules: costs that need a
payment list, loyalty, conditions that do not hold.

So the count is now asserted. `pressed > 1800` guards the *reach* rather than the outcome,
because a soak that stops reaching things keeps passing while checking nothing - which is the
same silent wrongness this whole document is about, wearing a test's clothes.

#### Two cards, one token, one behaviour

A token's id was `name-power-toughness-colours`, which is not enough to tell two tokens apart. Two
cards each making a 1/1 black Rat - one plain, one with an ability - produced **the same id for
two different definitions**, and the pool would then have served one card's token behaviour for
the other. The pool's duplicate guard caught it, so it surfaced as a refusal to play rather than
as a Rat quietly gaining somebody else's ability. Keywords, types, subtypes and the token's own
text now go into the id, hashed with FNV-1a because it must be stable across processes: the id
reaches the event log, and a log replayed tomorrow has to name the same card.

#### Every werewolf in the game was unplayable

Seventeen cards ended their game with "state-based actions and triggers did not settle". All
seventeen were daybound/nightbound.

The bulk data lists a card's keywords **once, for the whole card**. A transforming werewolf
therefore arrived carrying *daybound and nightbound at once*, on both faces - so the face rules
found it showing the wrong face whether it was day or night, turned it over, and found the same
thing again. Forever.

The fix resolves that one pair from the face's own printed text, and only that pair: daybound and
nightbound are the one pair the rules make mutually exclusive (CR 702.145b), so it cannot be right
for one face and wrong for the other. A first attempt derived *all* of a face's keywords from its
text and made things worse - six cards stopped looping only because the day/night keywords had
been dropped entirely, which is a different bug wearing the first one's clothes.

The engine deserves credit for the shape of this failure: it stopped and said the fixed point was
not reached, rather than spinning. A guard that turns a hang into a message is what made a
seventeen-card bug a morning's work.

### What the verification pass establishes, and what it does not

Six bugs, each with a test that fails without its fix: exalted pumped the wrong creature, persist
and undying returned a creature every time it died, renown grew it on every hit, rebound rebounded
out of exile forever, training compared printed power, and every board condition about a
characteristic answered about printed values.

**Every one of them compiled cleanly and passed the suite that existed.** That is the finding
worth keeping, ahead of any of the individual fixes.

Three instruments now stand behind them, and each was checked by watching it fail:

| instrument | what it makes impossible |
|---|---|
| `Every_line_shape_the_compiler_reads_is_played_by_a_test` | a matcher that nothing ever plays |
| `A_conditional_keyword_compiles_to_an_ability_that_states_its_condition` | an ability written down that is not the one the rules describe |
| `Every_field_of_a_permanent_is_part_of_its_identity` and siblings | a state field outside `Equals`, which defeats the replay invariant rather than breaking it |

**What is not established, stated plainly.** These raise the floor; they do not prove correctness.
The rule-text oracle compares *declared text* against the rulebook and cannot see what the code
does - persist's text was right while its behaviour was wrong. The coverage guard proves a shape
is played, not that the assertion made about it is the right one. And a condition written inside a
trigger's predicate is a closure: no static check can read it, which is why the corpus sweep for
unguarded intervening-ifs reports the four already-fixed keywords as suspects and cannot tell a
correct predicate from a missing one.

Exhaustive certainty is not available from testing. What is available, and what this pass bought,
is that every mechanic is now played by a game, every keyword's declared ability is checked
against the rulebook's own words, and the classes of bug that produced all six findings each have
an instrument watching for the next one.

### A condition about power could not see an anthem

Diffing every keyword's rule text against the ability the compiler builds - word by word, not
just looking for "if" - put **training** on the list. Its condition turned out to be right, and
the way it computed the comparison was not: `Characteristics.Of(state, EmptyAbilities.Instance, ...)`.

An **empty ability source means no static abilities exist**, so power comes back as printed. CR
702.149a says "power greater than this creature's power", and power is what the layers say it is.
The comparison was correct until the first lord, and a lord is the ordinary reason one 2/2 is
bigger than another. The trigger already carried the real ability source; it simply was not asked.

**The same mistake was structural in `BoardConditions`.** Its parsed conditions had the shape
`Func<GameState, GameObject, bool>` - no ability source anywhere in the signature - so every
characteristic they computed was a printed one. "If you control a creature with power 4 or
greater" is **88 cards in the corpus**, and every one of them was asking about the wrong number.

The fix is the signature: conditions now take the ability source, and it is threaded from the
places that have one - a trigger from `TriggerSource.Abilities`, a resolution from its context,
an activation from the engine. Where genuinely none exists the empty source is passed
*deliberately and locally*, which is a different thing from a signature that could never carry one.

That is the argument for the whole exercise in one finding. Nothing failed. No card was reported
unread. A question about power simply answered about a different number than the one on the
board, on every card that asked it, for as long as the vocabulary has existed.

### The rulebook as an oracle, and three more conditions that were not there

Coverage says a mechanic is *played*. It does not say it is *right*, and the two bugs above were
each a rule with a clause the code did not have. That class has a machine-readable oracle sitting
in the repository already: the rulebook writes most keywords out in full - `"Persist" means "...,
if it had no -1/-1 counters on it, ..."` - and **142 keywords have such a definition.**

`A_conditional_keyword_compiles_to_an_ability_that_states_its_condition` reads those definitions
out of `comprehensive-rules.txt`, compiles a card for each keyword, and asks a narrow question: a
rule whose definition says "if" or "unless" must produce an ability whose own text says so too.

It cannot prove a condition is *implemented* - only a game does that. What it catches is the
cheaper mistake underneath, and the one all three of these bugs started as: **writing down an
ability that is not the one the rules describe.** Renown's compiled text described an ability with
no condition, which is exactly what it built.

Six keywords came back. Five were legitimate rewrites and are exempted **with the reason written
down** - "you may X; if you don't, Y" is a choice between two outcomes, and fabricate and riot are
written as the choice they are; storm's "if" is a permission about its copies' targets rather
than a gate; cascade's restates what its own exile loop guarantees; myriad's guards a clause the
compiled text states outright. The sixth was **rebound**.

#### Rebound rebounded from exile

CR 702.88a: "**If this spell was cast from your hand**, instead of putting it into your graveyard
as it resolves, exile it and, at the beginning of your next upkeep, you may cast this card from
exile without paying its mana cost."

The condition was absent, and it is the clause that stops the mechanic eating itself: rebound's
own free cast comes **from exile**, so the spell exiles itself again and is cast for nothing at
the beginning of every upkeep for the rest of the game. Removing the fix puts the card straight
back in exile, which is how the test was checked.

Two notes on getting there, because both were the test being wrong rather than the engine. Rebound
*offers* the cast rather than making it, so a test that never takes the offer reads exactly like a
mechanic that never fires - the first version of this test accused the engine of doing nothing
when it was waiting to be asked. And the offer is keyed to priority rather than to the upkeep
step, which an existing rebound test already knew and mine had to learn.

### Persist returned a creature every time it died

CR 702.79a: "When this creature dies, **if it had no -1/-1 counters on it**, return it to the
battlefield ... with a -1/-1 counter on it." The condition was absent - not weakened, absent - so
a persist creature came back forever, and undying with it, since they share a matcher.

The first death looks identical either way, and the first death is the only one most games see.

The condition is checked in the trigger predicate, and that placement is the rule rather than a
convenience: "had" means the creature as it last existed on the battlefield (CR 608.2h, last known
information), and a trigger predicate is handed the state as it was before the event - exactly
that moment. By the time the ability resolves the permanent is a card in a graveyard with no
counters at all, so an intervening-if checked there would refuse every persist in the game.

### Exalted pumped the wrong creature, on every card that has it

The verification pass was widened from "the mechanics built this session" to **every mechanic the
compiler has**. There are 103 matchers in `CardCompiler`; cross-referencing each one's words
against the behaviour suite left exactly three that nothing had ever played: **bushido, exalted
and prowess.** Prowess and bushido were right. Exalted was not.

CR 702.83a: "Whenever **a creature you control** attacks alone, **that creature** gets +1/+1 until
end of turn." Two halves, and the engine had both of them wrong in the same direction:

- The trigger required the exalted permanent to *be* the lone attacker. The shared vocabulary
  matched `a creature you control attacks alone` and `~ attacks alone` with **one pattern and one
  handler**, and that handler asked whether the source was the attacker - right for the second
  sentence, wrong for the first.
- The effect pumped the **source** rather than "that creature", so even when it fired it grew the
  wrong permanent.

Both are invisible in the common case, which is why they lasted: the exalted creature attacking
by itself is the ordinary way the card is played, and there both readings give the same answer.
It is wrong exactly when a *different* creature attacks alone - which is what exalted is for.

The typed form beside it - "a Samurai or Warrior you control attacks alone" - had been correct all
along, checking the attacker's controller rather than its identity. The fix is that check without
the type filter, and the pump goes to `EffectSubject.TriggeringObject`.

**The "two attackers" test is not evidence, and saying so matters.** It passes whether exalted is
implemented correctly or not implemented at all; only the lone-attacker test can tell those apart.
A negative assertion is worth nothing unless something positive beside it proves the mechanism
was awake.

#### One guard taught, not silenced

Aiming the pump made 26 cards fail `Every_compiled_card_is_structurally_sound` with "effect
targets #0 of 0" - an ability with no targets holding an effect whose target index is zero. The
index is never read: the effect is aimed at the triggering creature. `EffectTargets.ReadsATarget`
now answers that question, in the one place that knows how an effect finds its subject, and the
check asks it. The guard was right to fire and wrong about why; it needed the distinction rather
than an exemption.

### The layers had one untested timestamp

Pointing the same technique at the engine core - the part with a hundred and ten tests and the
best reputation in this repository - broke deathtouch, trample and first strike, and all three
were caught immediately.

The fourth survived: **zeroing the timestamp an effect takes from its source permanent broke no
test.** CR 613.7 orders effects in the same layer by timestamp, and there was a test for it - but
it used *floating* effects, which take their timestamp from a different line. An effect from a
permanent takes the permanent's, and that path had never been exercised.

It is the commoner case by a wide margin: every enchantment and every lord on the battlefield.
With two of them in one layer, the timestamp decides what the creature ends up as. A test with two
permanents setting base power now pins it, and the mutation is caught.

Three fixture mistakes on the way to writing it, all mine and all the same kind: the helper
matches on the **oracle id**, not the name, and `TestCards` lower-cases the name into it without
replacing the space. Each wrong guess produced "power is 2" - the printed value, which is what a
test that applies no effects at all looks like, and indistinguishable from an engine that ignores
statics entirely.

### Two rules that could not be told apart, and one written twice

A fourth batch against the core - state-based actions, priority and the layer ordering. Draw from
an empty library, counter annihilation and the all-pass ladder were all caught. Two survivors, and
both were real.

**A creature at 0 toughness could stop dying and no test noticed.** CR 704.5f had a test, and it
looked exactly right: a 1/1 with a -1/-1 counter goes to the graveyard. But an ordinary creature at
0 toughness also has damage greater than or equal to its toughness - nought is not less than nought
- so with 704.5f deleted it falls through to CR 704.5g and dies anyway. The card ends up in the
same zone either way, and the test cannot see which rule sent it there.

The two rules are not interchangeable. 704.5f *puts into the graveyard*; 704.5g *destroys*. An
indestructible creature at 0 toughness dies, because indestructible answers destruction and has
nothing to say about this - and that case separates them cleanly. The engine already had it right;
what was missing was anything that would notice if it stopped being. A 0/4 Wall with four -1/-1
counters now proves both halves: that it leaves the battlefield, and that the event carries
`MoveCause.StateBasedAction` rather than `Destroy`, which is what keeps regeneration and every
"if it would be destroyed" replacement from seeing it.

**The legend rule was implemented twice, and the unreachable copy disagreed.** Mutating
`CheckLegendRule` to keep the newest duplicate rather than the oldest broke nothing, because that
method can never run. `AskLegendRuleIfNeeded` sits earlier in the same settle loop, groups by the
same `(controller, name)` key with the same ordinal comparison, and returns early whenever a group
has more than one member - so by the time state-based actions are checked, there is never a
duplicate left to find. Its own comment said as much: "making it the player's choice needs the
choice machinery that arrives with the effect system". That machinery arrived, the live path became
the one that asks, and the stand-in stayed behind still quietly keeping the oldest. Deleted. Two
implementations of one rule that disagree about the answer, one of them unreachable, is a bug
waiting for somebody to reorder the loop.

**A survivor that proved nothing.** A third mutation - disabling CR 613.8 dependency ordering -
was reported as surviving and had not compiled at all: removing the only call to `DependsOn` left
it unused, which the in-build analyzers reject as `IDE0051`, and the harness only recognised
`error CS` as a failure. Dependency ordering is properly tested; the harness was not. It now reads
a run as caught or survived only when a run actually happened, and calls anything else invalid.
That is worth more than the mutation was: a mutation harness that scores its own build failures as
results is measuring nothing, in the direction that looks like good news.

### The requirement that would have demanded an illegal block

A fifth batch, against combat and the parts of the core still unflipped. Menace at the set check,
defender, vigilance, the new identity an object takes on changing zones (CR 400.7) and the second
half of intervening-if (CR 603.4) were all caught.

The survivor was the *other* menace check - the one inside `CanBlockForRequirement`, which answers
"is this creature able to block that attacker" for a lure (CR 509.1c). Deleting it breaks nothing
anywhere the defending player has a choice, because the set check refuses the illegal declaration
a moment later. It only shows itself when there is no legal declaration to fall back on: one lure
attacker with menace, one creature able to block it. The requirement says that creature must block;
the restriction says it cannot block alone. CR 509.1c settles it - a requirement is obeyed only as
far as it can be without violating a restriction - so the lure compels nothing and blocking with
none is correct.

Without the check the engine demands a block that it then rejects, which is not a wrong answer but
a stuck game: every declaration the defending player can make is refused. The flying version of
this is already tested and is the easy one, because a flier plainly cannot be blocked by the
creature at all. Menace needs the engine to count, and that had never been asked.

### Any creature could deal commander damage, and no spell had ever half-resolved

A sixth batch, against targeting, protection and the commander rules. Hexproof reading the wrong
controller, protection not stopping a target, noncombat damage counting toward the twenty-one, and
a spell resolving with every target illegal were all caught.

**A creature that is not the commander could deal commander damage.** `TrackCommanderDamage` asks
two questions - is this player's commander known, and is this source it - and deleting the second
broke nothing. Every commander test puts a commander in front of the damage: it is tracked when it
is combat damage, not tracked when it is not, not tracked in a game with no commander. None of them
has an ordinary creature attack. Without that check, twenty-one damage from any mixture of
creatures ends a forty-life game by the rule that is supposed to be the hard way to win. An
ordinary 3/3 hitting for twenty-one now proves the total stays empty and the player stays alive.

**No test had ever watched a spell resolve for some of its targets.** Chasing two survivors in the
"target has already left" guards led somewhere better. Those two are unreached - a permanent that
changes zone comes back under a new id (CR 400.7), so the lookup above them fails first, and they
are defensive rather than dead in the way the second legend rule was. But writing a spell that
*could* reach them - two targets, one killed in response - turned up something the suite genuinely
lacked. `TargetsStillLegal` reads CR 608.2b correctly: a spell is countered on resolution only when
**every** target is illegal, and does as much as it can otherwise. Inverting it to the intuitive
misreading - fizzle if *any* target is illegal - broke no test in 1,065. Both new tests catch it,
and they are the only things in the suite that do.

That is the shape of the whole exercise in one finding: the guard the mutation aimed at was fine
and unreachable, and the rule three lines up, which every removal spell in the game depends on, had
nothing holding it at all.

### A curse on a dead player never fell off

A seventh batch, against trigger ordering, summoning sickness, the land drop and attachments.
Reverse APNAP order, haste lifting summoning sickness and failing to, an unlimited land drop, an
Aura attached to nothing and summoning sickness at the attack were all caught.

The survivor was the other half of that Aura check - the one for an Aura attached to a *player*
(CR 303.4a). It asked `state.Players.ContainsKey(enchanted)`, and that is always true: a player who
loses keeps their seat in the dictionary and is marked `HasLost`, which is how turn order skips
them. The condition read like a rule and enforced nothing.

**Fixed, and the failure was reproduced first.** A Curse on an eliminated player stayed on the
battlefield for the rest of the game - a permanent that CR 704.5m says belongs in its owner's
graveyard, still counting towards everything that counts enchantments. It takes three seats to see:
at two, the game ends with the player and nothing that happens afterwards is observable, which is
why the existing enchant-player test - a good test, about the Aura's trigger running on the right
player's upkeep - could never have found it.

**And it sits on a larger gap, which is recorded rather than filled: CR 800.4a is not implemented.**
A player who leaves the game keeps their permanents on the battlefield, their spells on the stack,
and anything they had taken control of. `HasLost` marks the seat and turn order skips it, and that
is the whole of it. The Aura fix is one corner of that rule made correct because a mutation walked
into it; the rest is unbuilt, and a multiplayer game where somebody dies is not being played
correctly after they do.

### Only one pool was ever watched emptying

An eighth batch, against the mana pool, cleanup and planeswalkers. Mana carrying across a step,
damage falling through from a planeswalker that had left, an unblocked attacker never reaching a
planeswalker at all, a hand limit ignoring what removes it and a cleanup discard off by one were
all caught.

The survivor: emptying only the first player's pool instead of everybody's. CR 500.4 empties every
pool as a step ends, and the test that covers it taps for the player whose turn it is - as does
every other test in the suite that touches mana. The nonactive player is the one who actually
floats mana, because holding up an instant in an opponent's turn is what floating mana is *for*,
and a loop that stopped after the first player would quietly hand it to them for free.

The pattern is the same one this whole exercise keeps finding, in a different costume: the rule was
tested through the case that is easy to set up rather than the case that happens. A second test,
identical except for which player taps, now covers it.

### Two combat rules that only their second term protects

A ninth batch, against combat damage assignment, loyalty and timing. Deathtouch shrinking lethal
damage, a second loyalty ability in a turn (CR 606.3), sorcery timing ignoring the stack and
Equipment staying attached to a creature that had gone were all caught.

**Lethal damage did not have to count damage already marked.** CR 510.1c asks what it would take
to kill the blocker *now*, so a creature wounded earlier in the turn needs less. The existing
trample test uses an undamaged chump blocker, which needs its full toughness either way, so the
subtraction was never exercised. A 5/5 trampler against a 0/3 wall with one damage on it sends two
to the wall and three to the player; counting from full toughness sends one less and the wall dies
just the same, so only the life total tells the two apart.

**Double strike had a test, and the test could not see the term that matters.** In the regular
damage step the rule is "everything without first strike, *plus* double strikers" (CR 702.4b), and
the second half only does anything for a creature that has both keywords. The existing test uses a
creature with double strike alone, where the first half already answers - so deleting the second
half broke nothing, while a first striker that had been granted double strike would have hit once.
Granting double strike to a first striker is an ordinary thing for a card to do.

The third survivor was neither: `CheckEquipment` skips Auras, and letting it treat them like
Equipment changes no outcome, because `CheckAuras` runs first in the same batch and has already
sent the Aura to the graveyard. A redundant event, not a rule - recorded rather than tested,
because a test for it would be about the implementation rather than about the game.

### Nobody had ever blocked a flier with a flier

A tenth batch, against evasion, tokens and regeneration. A token surviving off the battlefield, a
token's own ability being destroyed on the stack, regeneration not spending its shield, and
regeneration saving something destroyed by an effect that says it cannot be regenerated were all
caught. Reach blocking a flier was caught too.

The survivor was the other half of the same three-line condition: flying is blocked by flying **or**
reach (CR 702.9b), and only the reach half had a test. Dropping the flying half refuses a flier
blocking a flier - two fliers staring at each other, which is most of what flying does in a real
game - and nothing in 1,072 tests noticed.

That is the ninth batch's lesson again, and it is worth naming as a rule of its own rather than as
a series of coincidences: **a condition with two ways to be satisfied usually has a test for one of
them.** Reach and flying, first strike and double strike, the flying lure and the menace lure,
704.5f and 704.5g - every one of these was a disjunction where the interesting half was the one
nobody wrote down, and in most cases the tested half was the *rarer* card.

### Sweeping every keyword condition, and four live bugs

Picking anchors by hand had found the same shape often enough to name it - **a condition with two
ways to be satisfied usually has a test for one of them** - so the next step was to stop picking.
Every `Has(KeywordAbility.X)` in the engine, replaced with `false` one at a time, 41 sites: **19
caught, 8 survived, 11 would not compile.** The eleven are a limitation of the mutation, not a
result; a keyword that is the only thing in its condition leaves an unused variable behind.

The survivors were **horsemanship** (both halves), **intimidate**, **can-block-only-fliers** (both
halves), and **deathtouch in three separate places**. Horsemanship and intimidate did not appear
anywhere in the suite - the engine implements them and nothing had ever asked them anything.

Then the sweep's own gap: the pattern required a plain receiver, so every
`Characteristics.Of(...).Has(...)` was skipped. Three more sites, and one of them - **flash** -
survived too. Every card in the suite carrying flash was cast at sorcery speed anyway, so the whole
timing check could be deleted with nothing failing.

**Four live bugs came out of it, and three were the same bug.**

CR 613.1b puts control in layer 2, so the controller stored on an object is only where the
permanent started. The engine's own document says so, and records eight places that had been fixed
for it. Grepping for the shape found three more, all in combat:

- `CannotAttack` refused a stolen creature: *"you do not control it."* Threaten takes a creature,
  untaps it and gives it haste, and then the engine would not let it attack.
- `CannotBlock` refused it the same way, so a creature taken with a "you control enchanted
  creature" Aura could not block.
- The CR 508.1d requirement loop read the stored controller before computing anything, so a stolen
  creature with "attacks each combat if able" was not required to attack.

All three were reproduced as failing tests before anything was changed.

The fourth was found by following a survivor rather than by grepping. Deathtouch rides on the
damage event, because CR 704.5h remembers *that* a deathtouch source dealt damage separately from
how much. The fight effect built its damage without the flag - and its comment said the source was
named "so deathtouch, lifelink and wither all see who dealt it", which is true of the other two and
was never true of deathtouch. **A 1/1 assassin fighting a 6/6 dealt one damage and nothing else.**
Two-thirds of a correct comment is how that survived being read.

Six new tests, each verified to kill the mutation that found it, and the whole suite green at 1,082.

### The crown, three landwalks, and a fifth control bug

The eleven sites the sweep could not compile were re-run with the keyword swapped for one nothing
in the fixture has, which keeps the code valid while making the condition never fire. Seven caught;
**forestwalk, mountainwalk and plainswalk survived.** `Landwalks` is five near-identical arms and
only swampwalk and islandwalk had ever been reached - the same shape as reach standing in for
flying, this time with three understudies instead of one. A theory over all five now covers them.

Then the other half of the doc's own lesson: **any read of `obj.Card.*` or `obj.ControllerId` where
a computed characteristic was meant is the same bug.** Thirty raw reads in the engine, most of them
correct - a card in a library or a hand has only printed characteristics, and `ContinuousEffect`'s
constructor *is* layer 0, where printed is the whole point. `StealTheCrown` was not one of the
correct ones. It asked two questions about the permanent that dealt combat damage, and both read
what was printed and stored:

- **Whose creature is it.** A fifth instance of the control bug, and the nastiest reading yet: take
  the monarch's own creature and hit them with it, and `dealer.ControllerId == damaged.PlayerId`
  makes it look like the monarch damaged themselves, so the crown does not move at all.
- **Is it a creature.** An animated land is, and a land its controller has had since the turn began
  has no summoning sickness (CR 302.6) - which is exactly what a manland is for. This half was
  already right; nothing had ever asked it.

Both now go through the computed permanent, the control half was reproduced as a failing test
before it was changed, and both halves are pinned by tests verified to kill their mutations.

### Forty-seven keywords, and the four that had never run

Counting which keywords a test *names* is cheap and worth doing once: seven of the forty-seven were
never written down anywhere in the suite. It is not proof of anything on its own - `MustAttack` and
`CantBeBlocked` are both exercised through their card text and were caught by mutation - which is
why each of the seven was then flipped rather than argued about. Four were real:

- **Wither.** Infect is wither plus poison, and only infect had a test, so the half they share was
  covered through the card that does more and wither itself never ran once.
- **Protection from white** and **protection from blue.** The colour table has five entries; the
  tests reached black and green.
- **Ward's "counter it"** against a spell that cannot be countered. Ward does not target - that is
  the point of it - so it is a different effect from a counterspell and carries its own copy of the
  CR 701.6a check. Only the counterspell's copy had ever been asked.

All four now have tests, each verified to kill the mutation that found it, and the two colours went
in as a theory over all five rather than a sixth one-off.

**The shape is now the finding.** Reach standing in for flying, swampwalk for the other four
landwalks, black for the other four protections, infect for wither, one counter effect for the
other, double strike alone for double strike with first strike, 704.5g for 704.5f: seven separate
instances of one habit, which is to write the test for whichever alternative came to mind first and
let it stand for its siblings. It is worth saying plainly because it is *predictive* - given a list
of parallel arms in this engine, the way to bet is that one of them is load-bearing in the tests and
the rest are decoration.

### Measuring the card corpus, and what the remaining work actually is

The headline coverage test - "the compiler reads a known share of every playable card" - had been
**skipping silently**. It looks for `oracle_cards.json`, only `default_cards.json` was on the
machine, and its answer to that is `output.WriteLine("...skipping"); return;`. A green test that
measured nothing, in the same family as a mutation harness that scores its own build failures.

The corpus is derivable from the printings file already present: every printing of an oracle id
carries the same rules text, types and P/T, which is all the compiler reads, so deduplicating on
`oracle_id` reproduces what the oracle file is - 116,752 printings down to 38,626 cards, no network.

**The baseline, which had never been measured: 12,357 of 32,765 playable cards complete (37.7%),
35,425 of 61,846 lines read (57.3%).**

Then the question that decides what to build. Every unread line was dumped and bucketed by what it
would *need*, and the answer contradicted the obvious guess:

- **39.8% of blocking lines need no subsystem at all** - the compiler simply does not read the
  sentence.
- Copy effects (layer 1) and text-changing (layer 3) - the two layers with no producers anywhere,
  and the two that look most like structural holes - are worth **2.9%** and **0.2%**.
- A general permanent-chooser, which three separate mechanics were waiting on, is worth 4.2%.

Then the sharper version of the same question: is a line unread because of its *words* or its
*shape*? Every unread line was cut into sentences and each sentence compiled alone on the same
card. Of 15,675 cards one line short, 9,640 have a single-sentence line, and of the 6,035 with
more than one, **32** have a line whose every sentence reads on its own.

So it is vocabulary, not composition, and the tail is flat: the largest single template is worth 14
cards. **Reaching 100% is roughly 14,400 individually-written readers, not a handful of structural
ones.** That is worth knowing before starting rather than after a thousand of them.

The 32 were not wasted, though: they are almost all one family - "Create N tokens. They have
"X."" - and folding that second sentence into the inline form the token reader already takes was
worth **30 cards for one normalisation**, against ~13 for a typical hand-written mechanic. The
guard that refuses a token whose ability cannot be read applies unchanged, because the fold reuses
that path rather than adding one.

### A filter the grammar already understood, refused on the way in

Adding "can't be blocked except by X" turned up a defect in the reader it inverts.
`ReadBlockRestriction` translates a card's plural into the grammar's singular by stripping the
final letter, which is right for "artifact creatures" and wrong for **"creatures with flying"** -
there the noun sits in front of a qualifier, so the phrase went to the grammar untouched and was
refused. The grammar reads "target creature with flying" perfectly well; there is a test for it.
Every "can't be blocked by creatures with <anything>" on every card fell in that gap, in both
spellings, and the symptom - a line the compiler will not read - is identical to a filter nobody
had taught it.

That is worth generalising, because the two are indistinguishable from outside: **a missing reader
and a broken one look exactly alike.** So the compiler is now swept for the second kind. Each
unread line has a meaning-preserving rewrite applied - straighten the apostrophe, singularise the
noun rather than the phrase, drop the reminder text - and if the rewrite makes it compile, the
vocabulary was there all along and something on the way in dropped it. After the fix above, the
sweep finds **two** cards, which is the answer that makes the rest of the queue trustworthy: what
is left really is unread for its content.

The "except by" reader itself is built by negating the "by" one rather than re-reading the filter,
so the two spellings cannot come to disagree about what "creatures with flying or reach" means.

**A note on the test that first missed it.** The obvious assertion - `CombatRules.CannotBlock` -
cannot see a block restriction at all: restrictions are consulted in `IllegalBlockSet`, because a
restriction is asked of the declaration rather than of one creature. Several neighbouring tests use
that helper quite correctly for the rules it *can* answer, and a new test written beside them
inherits a blind spot that has nothing to do with the rule being added.

### A rule written twice, caught by an id collision

Implementing "enters with a +1/+1 counter on it for each color of mana spent to cast it" produced a
reader whose body was, line for line, the one already behind the **Sunburst** keyword. The suite
caught it - but not on the rule. The new test card collided on an oracle id with the existing
Sunburst test, and `CompiledPool` refuses two different cards sharing one: *"the compiled pool
would serve one card's behaviour for the other."* It passed alone and failed only in the suite.

Had the card been named anything else, a second implementation of a rule the engine already had
would have shipped - the same mistake as the second legend rule deleted earlier, which sat
unreachable and quietly disagreeing for months. Both spellings now go through the one reader: the
keyword picks the counter kind from the card's type, the spelled-out sentence names +1/+1 itself,
and the card decides which.

**The lesson is about where the guard was.** Nothing checks that a newly written reader duplicates
an existing one; what caught this was a guard against a different problem entirely, firing on a
coincidence of naming. That is luck, and it is worth saying so rather than filing it as a save.

### The builder had two copy paths and no guard over them

`CharacteristicsBuilder` is copied twice: into a throwaway that answers a CR 613.8 dependency
question, and into the `ComputedCharacteristics` everything downstream reads. Adding `MinBlockers`
landed in one and not the other, and the build was perfectly happy - a characteristic that survives
long enough to be computed and is gone by the time anything looks at it.

`StateEqualityTests` has guarded exactly this class for permanents, objects and players for a
while. It now guards the builder too, over both paths, reached by reflection rather than by opening
the internal seam - a test is not a reason to widen one. **Verified by putting the mistake back**:
with `MinBlockers` removed from `Build()` alone, it reports "MinBlockers is lost by Build()".

### The third clock

A continuous effect had two durations: a turn number, or for ever. The cards have a third -
"for as long as you control this creature", "for as long as this artifact remains tapped" - and it
was the largest single family the condition probe turned up, about 126 clauses.

**It is not the same as a condition on the effect.** CR 611.2b: such an effect *ends* when its
condition stops holding, and does not begin again if the condition becomes true once more. Folding
the question into the effect's own `Applies` is the cheap route and gives a different card - a
creature stolen "for as long as you control this creature" would come home every time the thief
did, and go again every time it came back. So the condition is a separate `While` predicate, and
the sweep that checks state-based actions ends the effect when it fails: an ending, in the place
where the game notices things about itself.

The condition lives **in the definition's id**, not in the state. State is a fold of the event log
and a delegate cannot be replayed; everything the condition needs is two ids and a word, all known
when the effect is created. That buys a rules detail for nothing: the source's id is the one it had
at creation, so a permanent that leaves and returns is a new object (CR 400.7), the old id names
nothing, the condition fails, and the effect ends - which is what the card says.

Three spellings, one mechanism: controlled, tapped, and still-on-the-battlefield. The third asks
nothing beyond what all of them already ask, because the source has to be there for any of it to
mean anything.

Both tests assert the same three steps - taken, still taken across a turn boundary, given back for
good - and then put the condition *back*. That last assertion is the whole of CR 611.2b, and
without it a pause and an ending look identical.

### Thirty-seven cards for a hyphen

The ability-word stripper removes flavour like "Landfall — " and reads what follows. Its
character class allowed letters, apostrophes and spaces - which silently excluded **Power-up**, an
ability word on thirty-seven cards, and every one of them was unread for a punctuation mark. One
character in a character class; sixteen cards completed.

The corpus was checked before the class was widened rather than after: twenty-two distinct
hyphenated prefixes exist, Power-up accounts for the overwhelming majority of the occurrences, and
the rest are card names in the same position - which the pattern already admits when they have no
hyphen. That reasoning sits beside the regex, because the change is about *how much* of an existing
risk is taken rather than whether it is taken.

The same probe then found **Descend 4** and **Descend 8**: one ability word with a number in it,
excluded by the same class for want of digits. That one is implemented and tested and completed
**no cards at all** - the cards carrying it are blocked by other lines too. Worth recording as
zero rather than counting the mechanism as progress.

**This is the shape that has paid best all session, and it is the hardest to find**: a missing
reader and a broken one look identical from outside. Both present as "the compiler will not read
this line". The only way to tell them apart is to apply a meaning-preserving rewrite and see
whether the line suddenly reads - which is what the rewrite sweep does, and why it is worth keeping
pointed at the queue after every change.

### What the card work needed that the engine did not have

Four capabilities, each built because a family of cards wanted it rather than because it was
missing in the abstract:

- **A third duration.** Continuous effects had a turn number or for ever; the cards also have "for
  as long as ...", which *ends* when its condition fails and does not resume (CR 611.2b).
- **A colour choice**, and then a second use of the same question ("becomes the color of your
  choice") for the cost of the use rather than of the mechanism.
- **An untap choice** and **a counter choice**, which between them carry bolster and amass - two
  keywords that are one shape with different candidate sets.

Each follows the pattern already in the engine: an effect emits a request, the settle sweep asks,
and the answer builds the events. Adding one is now half a day rather than an argument.

### Measured and declined: total power as a condition

"As long as creatures you control have total power 8 or greater" is nine cards and reads like the
counting conditions beside it. It is not one. Every other condition asks about *how many* things
there are, or about a characteristic settled in an early layer - the controller, a card type. This
one adds up **power**, which is layer 7, and it is asked from inside the computation of a creature's
own power.

Written and tested, it returned false where it should have returned true: the sum could not be
taken because taking it re-entered the computation it was asked from. The obvious escape - read
printed power instead - gives a condition that is quietly wrong under any anthem, which is the
failure this codebase refuses everywhere else.

CR 613.8 has an answer for characteristics that depend on other characteristics, and the engine
implements it *between effects*. A board condition sits inside an effect's `Applies` and is outside
that machinery entirely. Making this work means either teaching conditions to participate in
dependency, or giving the layer system a way to answer "the total power of a group" as a
first-class question. Both are real work; neither is nine cards' worth on its own, and the reason
is worth writing down so the next person does not rediscover it by writing the same nine lines.

### How far off each unread card actually is

Two mechanics in a row - Descend and "attacking alone" - were implemented, tested, and completed
**no cards at all**: the cards carrying those clauses were blocked somewhere else as well. The
obvious inference was that the easy cards had been used up and the rest were multiply blocked.

That inference was wrong, and the distribution says so. Of **20,074** incomplete cards:

| Lines short | Cards | Share |
|---|---|---|
| 1 | 15,466 | **77.0%** |
| 2 | 3,689 | 18.4% |
| 3 | 602 | 3.0% |
| 4 or more | 317 | 1.6% |

Three quarters of what is left needs **one more line read**, not several. What is true is the thing
already measured: those 15,466 cards carry 14,234 distinct shapes, so a reader is worth about 1.09
cards, and *which* reader decides whether it lands on cards that are one line short or on cards
that are two.

**The clause counts from the probes are an upper bound on cards unlocked, not a prediction.** Nine
clauses can be nine cards or none, and the difference is invisible until the reader is written.
That is worth knowing before planning around a ranking.

### Measured and deferred: putting cards back in any order

"Look at the top four cards of your library, then put them back in any order" is seven cards and
looks like scry, which the engine has. It is not: scry moves cards *out* of the top and this
reorders them *within* it, and `Move` mints a new object id on every move (CR 400.7). Reordering by
moving each card to the top in turn would invalidate the ids of the cards still to be moved, half
way through the reorder.

Doing it properly means either a move that keeps identity inside one zone, or an event that states
a library order outright. Both are reasonable; neither is seven cards' worth of design decided in
passing, and the reason is recorded so it is not rediscovered by writing the loop and watching it
scramble a library.

### Fourteen mutations, and what the survivors mean

Mutation testing turned out to be worth more than any hypothesis, so a second batch of eight
followed the first six: a finished Saga never sacrificed, a Saga arriving without its lore
counter, a Case re-solving every end step, a door unlocked at instant speed, an Adventure binned
instead of exiled, an aftermath half castable from hand, a fused spell costing one half, a granted
ability reaching everybody.

**Six of the eight were caught.** Two survived, and the survivors are the interesting part,
because a survivor means one of three quite different things and they are easy to confuse:

1. **A test that cannot see the rule.** "A granted ability reaches everybody" survived because
   every permanent in the fixture belonged to the caster - so "lands you control" and "lands" gave
   the same answer, and deleting the control check broke nothing. An opponent's land in the
   fixture, and it is caught. That is the non-discriminating fixture again, found mechanically
   rather than by noticing.
2. **A guard that is not the only guard.** "A Case re-solves" survived twice - once before the
   test reached a second end step, and again after, because `SolveCase` independently refuses to
   emit a second `CaseSolved`. The behaviour is right; the mutation is invisible because the rule
   is enforced in two places. The same was true of station's "another".
3. **Dead code**, which is what the prowess survivor turned out to be, above.

Only the first is a hole. The other two are worth telling apart from it, because "the mutation
survived" reads as "you have no test" and is often something else entirely - and chasing the
wrong one of the three costs an afternoon.

### Mutation testing, and a branch nobody could reach

The pattern behind three of this session's bugs was a fixture that made the right and wrong
answers coincide. The way to find more of those is not another hypothesis: it is to **break the
code on purpose and see whether anything notices.**

Six rules were flipped one at a time - prowess ignoring "noncreature", a chapter firing on
reaching rather than crossing, a level bar payable from any level, a solved section applying
unsolved, a station tapping its own ship, a door opening whatever was paid for - and the suite run
against each. Four were caught. Two survived, and neither meant what it looked like.

#### The dead branch

Removing the word "noncreature" from prowess broke nothing, which reads as an untested rule. It
was not. `TriggerConditions.Parse` had **two** handlers for "you cast a noncreature spell": a
general one for `casts a [kind] spell`, reached at line 5795, and a specific `CastNoncreature()`
branch four hundred lines later that nothing could ever reach.

The specific one is the one named after the sentence, so it is the one anybody debugging prowess
would find and change - and changing it does nothing at all. That is a worse trap than a missing
test: it costs an afternoon and leaves you believing the rule is unimplemented. It has been
deleted, and mutating the live path now fails the prowess test as it should.

The prowess test did gain something on the way: it asks the **log** rather than the power, because
prowess lasts until end of turn and "still 1" is true whether the ability triggered or not.

#### The redundant guard

Turning off station's "another" also broke nothing - but here the behaviour was right all along:
the engine refuses a source paying for its own ability in a second place as well, so the mutation
is unobservable. The obvious test could not see it either: an uncharged Spacecraft is not a
creature, so stationing with no other creature fails for want of *any* creature rather than
*another* one. The discriminating board is a charged ship, which is a creature, untapped, and
legal in every respect except the word. That test exists now.

### A good test that could not tell the rule from its fallback

CR 616.1 was already tested, and well - the rulebook's own example, one effect exiling what would
go to a graveyard and another shuffling it into a library, with the choice asserted to belong to
the affected object's controller.

But the creature in it belongs to the **active player**, and `AffectedPlayer` answers
`State.ActivePlayerId` for any event it does not recognise. The test could not tell the rule from
the fallback: both name the same person. A permanent belonging to the *other* player is the case
that separates them, and nothing asked it.

It asks now, and the engine was right. What is worth keeping is the shape of the gap: a test can
assert exactly the correct thing, pass for the correct reason, and still fail to distinguish the
implementation from a wrong one - because the fixture happened to make two different answers
identical. That is the same failure as testing exalted with the exalted creature attacking, and it
is much harder to see in a test that is otherwise good.

### APNAP, and a test that read the stack upside down

Same hypothesis, next rule: with one opponent, "the active player first, then the rest in turn
order" and "one each" put the same two abilities in the same two places. CR 603.3b only becomes a
claim at four seats.

Four watchers, one creature, four triggers - and the order came out **exactly reversed** from what
the test expected. It took a look at `GameState` to settle which of us was wrong: *"Shared. Top of
the stack is index 0 (CR 405.2)"*, and `ResolveTop` takes `Stack[0]`. So the list reads in
resolution order, the active player's ability is at the *bottom* because it went on **first**, and
it resolves **last**.

The engine was right and the test was reading the stack upside down. That is worth recording
because it is the same trap as the harnesses that reached nothing: **a correct engine and a
backwards expectation look identical from the assertion message**, and the only way to tell is to
go and read what the thing actually promises.

### The scopes, asked at a table of four

Following that advice with a hypothesis rather than a sweep: **at two players the player scopes
are indistinguishable.** "Each opponent", "each other player" and "target opponent" all pick out
the same person, and "for each opponent" is a multiplier of one, which is the same as no
multiplier. A card that means one and does another is right in every game anybody had tested -
the same shape as exalted, which was correct whenever the exalted creature was the lone attacker.

Three is the smallest number that tells them apart, so the scopes were asked at a table of four:
"each opponent loses 1 life" takes a life from three players and none from the caster; "each
player" takes one from all four including the caster; "you gain 2 life for each opponent" gains
six; a sweep reaches all four players' creatures.

**All four were right.** That is the outcome of a targeted question rather than a sweep, and it is
worth recording as a null result: the scope vocabulary was written for any number of players and
behaves like it, which is one fewer place to suspect.

### Where the risk actually lives

Nine bugs came out of this pass. **Eight of them were in the card layer** - a compiler matcher, a
sentence in the shared vocabulary, a token id - and the ninth straddled the corpus loader and one
method in the engine. Not one was in the rules core.

That is not luck, and it is worth writing down as a place to look rather than a piece of history.
The core was built test-first and its tests are about *rules*: eighteen on the layers alone,
including CR 613's own worked example, dependency beating timestamp, and what happens when
dependencies form a loop; twenty-six on combat; nineteen on state-based actions; fifteen on
triggers; eleven on priority; ten each on replacement effects and zone changes. Roughly a hundred
and ten tests asking whether the engine obeys the rulebook.

The card layer was built **coverage-first**. Its instruments counted how much text could be read
and, until this pass, nothing asked whether what was built out of that text was the ability the
card describes. Exalted had been wrong since it was written. Persist came back for ever. Renown
grew without limit. Every one of them compiled, and compiling was the only question being asked.

So: when something is wrong here, look at the compiler before the engine - and prefer a targeted
question about one mechanic to another sweep. The sweeps have gone quiet; the targeted questions
found nine.

### Verifying what is built, rather than building more

A pass over the mechanics finished this session, asking of each one not "does it compile" but
"does it play". Fourteen tests, aimed at the restrictions rather than the happy paths, because a
restriction left out is the failure that looks like a strong card: station at instant speed, a
door any player could open, a fused spell costing one half, a creature that stayed in exile and
could be cast again every turn.

**All of them passed.** That is worth writing down plainly - it is the first pass this session
that found nothing, and the value of it is that the claims already made are now claims that were
checked rather than claims that were reasonable.

Then the three gaps that pass named as unverified, closed:

- **A chapter that targets.** Every Saga test used chapters that target nothing, so the whole
  targeting path was untried for them - a chapter is an ordinary triggered ability and gets its
  targets the ordinary way, but "ordinary" is a claim until something exercises it.
- **A Case's solved trigger and solved activated ability.** CR 719.3c names three kinds of solved
  ability and only the static one had been checked; the other two take different gates.
- **Two lore counters arriving at once** (CR 714.2b's "one or more"). No card in the corpus puts
  two on, so rather than invent engine surface the chapter predicates are asked directly: given a
  Saga on one counter and two more arriving, chapters II and III both fire and chapter I does not.
  Read as "there are now N counters", chapter III would be skipped for the rest of the game - the
  Saga reaching its final chapter number without ever running its final chapter, and being
  sacrificed for it.

Two more are worth naming because they check a rule that is easy to write and hard to see:

- **A granted trigger stops when its source leaves.** The definition is written into a side table
  when the trigger is found, and a table is exactly the sort of thing that keeps answering after
  the world has moved on.
- **A charged Spacecraft's printed power is a base, so a +1/+1 counter counts on top of it**
  (CR 613.4b, 721.2b). Written into layer 7c instead of 7b it would look right on an untouched
  ship and quietly swallow every counter put on one.

#### The guard under all of it

`GameReducer.Replay(log) == State` is the invariant the whole engine rests on, and every
behaviour test asserts it on the way past. **A field left out of `Equals` does not break that
assertion - it defeats it**: the replayed state differs in exactly that field and the comparison
says they match.

Nothing was checking that. Four fields were added to these records in one session - a Class's
level, a Case's solved flag, a Room's open doors, a card on an adventure - and only care was
stopping the fifth from being missed. That is the same shape as the three vocabulary lists that
went stale: a hand-written comparison beside a growing record.

`Every_field_of_a_permanent_is_part_of_its_identity` and its two siblings vary one property at a
time and assert the result is no longer equal. A property the test cannot vary is **reported
rather than skipped**, so a new field of an unfamiliar type fails loudly instead of being waved
through - which is what it did on first run, naming three types it did not know.

Checked the way the other guards were: take `IsSolved` out of `PermanentState.Equals` and it
fails with *"changing these leaves the state equal, so a replay that got them wrong would still be
reported as matching: IsSolved"*.

#### The hole the compiler digs on purpose

One more guard, for a hole this compiler creates deliberately. A Room's unlock trigger is compiled
with a predicate that answers **nothing**, because the line alone does not say which of the two
doors it belongs to - only the Room path knows that, and it replaces the predicate as it merges
the halves.

If a card ever carries that line without going down the Room path - a single-faced card, a face
whose subtypes are read differently, a Room printed in some new shape - the trigger compiles, the
card counts as fully read, and the ability silently never fires. That is the worst failure this
project has: **a card that looks understood and does nothing.**

The dead-trigger sweep beside it cannot catch this, because its sample events do not include an
unlock; absence from that report is not evidence. `Every_unlock_trigger_answers_an_unlock` asks
directly, and finds 17 live. Disabling the Room routing makes it name the cards that would go
quiet - *Underwater Tunnel*, *Moldering Gym*, *Glassworks* - which is what a guard should say.

### Aftermath: one word, three static abilities, and two of them easy to skip

With the halves built, aftermath is small - and it is the two clauses nobody would think to look
for that make it a two-part card rather than simply a better one. CR 702.127a is a single sentence
holding three static abilities:

1. this half may be cast from a graveyard,
2. it may be cast from **nowhere else**, and
3. it is **exiled** rather than put anywhere else when it leaves the stack.

Implement only the first and the card can be cast twice from hand. Implement the first two and it
returns to the graveyard it was just cast from, and can be cast again every turn for the rest of
the game. Neither failure would look like an error from the outside: both look like a strong card.

The test casts the first half from hand, is **refused** when it tries the second from hand, casts
the second from the graveyard, and then finds the card in exile rather than back in the graveyard.
Each of those three assertions exists because dropping one clause of 702.127a passes the other
two.

One thing worth naming, because it was nearly a silent bug: **exiling and going on an adventure
are not the same thing.** Both are exiled as they resolve and only one of them may be played from
there, so folding aftermath into the flag Adventure already used would have left every aftermath
card castable out of exile for the rest of the game. Two flags, because they are two facts.

Fuse (CR 702.102) is the remaining split-card keyword and is still unread: it casts both halves as
one spell for their combined cost, which is a shape the cast path has nowhere to put yet.


### Rooms, and the third ability printed behind its own switch

With the halves in place from the round before, a Room is the section gate a third time. Both
halves are on the same permanent at once, and each one's text is switched on by its own door: CR
709.5 says a locked half "doesn't have the name, mana cost, or rules text" of that half. Without
the gate a player pays for one door and gets both - the card doing twice what it says while
looking entirely normal.

The pieces are small because the shape is now familiar: designations by face index rather than a
left/right pair, since the compiler numbers faces and nothing else in the engine knows left from
right; CR 709.5d unlocks the door that was cast, read from the same record that decided what
resolved; and CR 116.2m's unlock is a **special action**, not an ability - it does not use the
stack and nobody may respond, and its timing is sorcery speed written the long way.

**"When you unlock this door" is the third ability in this engine printed behind the switch it
fires on.** A Saga's chapter, a Class's level, and now a Room's door: each is checked against a
state that has not changed yet, so each needs its gate lifted for its own event and no other.
Three occurrences is enough to call it a shape rather than a quirk - *an ability that announces a
change is always behind the thing it announces*, and the gate that makes the rest of its section
correct is exactly the thing that would silence it.

The tests turn on the door that stayed shut: one door's bonus present and the other's absent, and
an unlock trigger that does not fire when the *other* door opens. A gate that leaked would be
invisible if both halves did the same thing, so they deliberately do not.


### Split cards, and one table for "which spell is this"

Adventure and split cards are the same problem: **a card carrying more than one spell, with the
choice made as it is cast.** So the machinery built for the first became one table for both,
holding not just the effects but the two other things that depend on which spell it is - whether
it resolves into a permanent, and where the card goes afterwards. Both are questions about the
*spell*, and both had been asked of the card.

The discriminator for a half needs no knowledge of layouts, which the compiler does not have:
**a face with a printed mana cost is a face somebody can pay for.** That separates a split card's
halves from the back of a transforming card, which has no cost and is reached only by turning the
permanent over.

One bug, and it is the same shape as the Adventure one the round before. `definition` was written
`adventure ?? halfSpell ?? normal`, and **a creature half has no spell definition at all** -
a creature spell is not a list of effects. So the null fell through to the card's own spell,
which is the *other* half, and casting the creature side of a split card demanded the instant
side's target. When a half has been chosen, that half is the spell, empty or not.

That is twice now that a fall-back has quietly substituted one half of a card for the other. The
lesson is narrow and worth keeping: *absent* and *not chosen* are different, and `??` cannot tell
them apart.

Both tests turn on the half that did **not** run - the opponent's untouched life total, the
creature that did or did not arrive - because a card with two spells on it will always resolve
*something*, and something is what a wrong answer looks like.

Still to do for Rooms: CR 709.5's locked halves and the unlock special action. The halves exist
now; the designations and the special action do not.

### Adventures: 170 cards the coverage number was lying about

Re-running the subtype report after several rounds of fixes showed Siege and Spacecraft had left
the 0% list on their own - the self-reference fix moved them - and left four families at zero.
Three of them are worth naming as **declined, with numbers**:

- **Attraction (22) and Guest (21)** are acorn cards. An Attraction needs a supplementary deck in
  the command zone, dice, physical stickers, and in one case ten seconds of real time. They are
  not implementable as rules and pretending otherwise would be worse than the gap.
- **Trap (20)**: the wrapper is one template - "If [condition], you may pay [cost] rather than pay
  this spell's mana cost" - and there are **19 cards with 19 distinct conditions**, nearly all
  needing turn history nothing else asks for ("an opponent cast a red instant or sorcery spell
  this turn"). One template, nineteen features.
- **Room (30)** is real and stays on the list: it needs CR 709.5's locked halves and the unlock
  special action, which is split-card machinery this engine does not have yet.

What the report could not show is the one that mattered. **Adventures compile perfectly and
could not be played at all.** Both halves of an adventurer card are ordinary text - a sorcery and
a creature - so all 170 read cleanly and counted as understood, while the engine had no notion of
casting the Adventure half. That is the coverage-versus-play gap with a number on it, and it is
the clearest example this project has produced: *reading a card and being able to play it are
different claims, and only one of them was being made.*

CR 715 is precise about the four things that make it work, and two of them are easy to get
backwards:

- **715.3b** - "while on the stack as an Adventure, the spell has only its alternative
  characteristics". Everything that resolves a spell looks its effects up from the card, and the
  card is the creature. So what is on the stack is recorded separately, beside the granted
  abilities and for the same reason: a spell definition is code and no event log can carry it.
- **715.3d** - it is exiled rather than binned, and its owner may then play it. The engine
  already had a set for redirecting a resolving spell to exile, so this is one more entry in it.

The second one bit. The Adventure resolved correctly, gained the life, and then **the creature
arrived on the battlefield anyway** - because the destination was decided by asking whether the
*card* is a permanent card, and it is. Two cards' worth of value from one, with the life gain
already banked so every assertion about the Adventure passed. The destination asks what is on the
stack now.

The test gains life from the Adventure, finds the card in exile rather than the graveyard, is
refused when it tries to go on a second adventure (715.3d says it cannot), and then casts the
creature out of exile.


### Backgrounds: a group that is not a characteristic

A Background's whole text is one sentence - `Commander creatures you own have "[ability]"` - and
it was the last thing standing between the quoted-ability work and 31 more cards. The ability half
was already read, both the activated and the triggered kind. What was missing was the *group*.

Every other group in this compiler is a filter over characteristics: a type, a tribe, a colour,
who controls it. **Being a commander is none of those.** It is a designation its owner gave a card
before the game began (CR 903.3), so the filter asks the *player* whether this card is the one
they named, rather than asking the permanent anything. The engine already kept
`PlayerState.CommanderOracleId`, so the group is three lines and no new state.

"You own", not "you control", and the distinction is real: a commander stolen by an opponent is
still its owner's commander, and a Background follows the card rather than the board.

The ordinary creature beside the commander carries the test, as it has in every grant test this
session - a filter that returned true for every creature you own would look identical from the
commander's side. Backgrounds go from 0 of 31 to 3, with the rest waiting on the particular
ability each one grants.


### Cases, and the stripper that had eaten two mechanics

A Case (CR 719) is a Class with a simpler switch: one designation instead of a ladder, set by a
condition instead of bought. The section under "Solved -" must not function before it is, which is
the same requirement as a level bar and got the same answer - sections compiled as cards of their
own, gated on the way back.

**Not one line of it reached the compiler.** `AbilityWord` strips a capitalised phrase before an
em dash as flavour (CR 207.2c), and "To solve" and "Solved" are capitalised phrases before em
dashes. Both were removed before any matcher saw them, which is why `IsCase` found no Case at all
and every one of the fifteen compiled as a handful of homeless sentences.

That is the **second** mechanic this stripper has eaten - chapter III of every Saga was the first,
and it was fixed with a lookahead for Roman numerals two sections up. Twice is a pattern, so the
shape stayed and the exclusions became a declared list with the pattern built from it. A shape is
still right here: there are **579 distinct ability words** in the corpus, and a list of those
would be stale by the next set. What was wrong was having no way to say "this one is not
flavour".

`A_structural_prefix_is_not_stripped_as_flavour` and `A_chapter_symbol_is_not_stripped_as_flavour`
are the guards. They compile a card whose only line is the prefix and assert the line still starts
with it.

#### Three failures, one of them the interesting one

Getting from there to a working Case took three fixes, and the order they came in is the lesson:

1. The stripper, above.
2. A trailing full stop. Every condition in the shared vocabulary is anchored at both ends, so
   "You control seven or more lands." matched nothing - which reads exactly like the vocabulary
   being missing when it was there all along.
3. `TriggerConditions.Parse("at the beginning of your end step")` returned null, because that
   vocabulary is handed the clause a card puts *after* "When" or "At", not the whole sentence.

None of the three was about Cases. All three were about handing an existing reader text in a
shape it does not take, and each looked from the outside like a missing feature. **The compiler
reported the same "unread" either way**, which is the argument for probing a single sentence
through the reader before concluding anything is absent.

Cases go from 0 of 15 to 1, with the rest blocked by their solved abilities and the more exotic
solve conditions - an ordinary long tail, and the honest place for them.


### Station, and a card that called itself by a name nothing knew

Two templates on 29 Spacecraft: the `Station` keyword and the `N+ | [abilities]` threshold.

Station is crew's cost with a different consequence (CR 702.184a): one creature rather than
enough of them, counters that stay rather than an animation that ends. **The number of counters
is the tapped creature's power** - known when the cost is paid and gone by the time the ability
resolves, so it rides on the stack object as the amount the ability is about, which is the same
field a trigger uses for "that many". No new field: the question is the same question.

The threshold is two effects rather than one, and CR 721.2b is explicit about why - at N charge
counters the permanent has the abilities *and* "is a creature with base power and toughness
[P/T]". A Spacecraft that gained flying and first strike without becoming a creature would sit
there and never attack: a card doing nothing while looking entirely correct. The P/T is set in
layer 7b rather than modified, so a +1/+1 counter still counts on top of it (CR 613.4b).

The test taps a **4-power** creature deliberately. A fixed "one counter" implementation would
still be sitting at 1, and the assertion that it reached the threshold in a single activation is
what says the power was read at all.

#### The card called itself a Spacecraft

With both templates in, the real card still would not compile - and the line that stopped it was
an ordinary enters trigger. Cards refer to themselves by their type: "this Saga", "this Vehicle",
"this Spacecraft". The compiler normalises those to `~` from a list, and the list had sixteen
names on it and was missing eight - **Spacecraft, Contraption, Siege, Case, Attraction, Planet,
Conspiracy, Realm**, about 165 lines between them.

That is the third time this session a mechanic has been most of the way to working and stopped by
a vocabulary list rather than by anything about the mechanic. A list the compiler has to remember
is a bug waiting to happen, as this document already says two sections above; this one had no
guard and quietly shortened every card that used a newer type name to say itself.

**So it has one now.** `Every_type_a_card_calls_itself_by_is_understood` sweeps the corpus for
"this [Type]" and asserts the compiler knows every name a card uses for *itself* - a name only
counts when the card actually is one, which is what keeps it from demanding the whole
creature-type table. The list is exposed and the pattern built from it rather than restating it,
the same shape as `EffectTargets`.

It was checked the way the other guards were: take "spacecraft" back out, and it fails with
`45  "this Spacecraft"  e.g. Wedgelight Rammer`. A guard nobody has watched fail is a guard
nobody knows the shape of.


### A granted trigger has to be found twice

The other half of the quoted-ability family: 86 lines give away a *triggered* ability rather than
an activated one. It is the same trap as the last round one step further along, because a trigger
has to be found **twice** - once when the event happens, to know it is watching, and again when
the ability resolves, to know what it does. Both lookups were keyed on the card, and the card does
not have it.

So layer 6 gained a second slot beside `GrantedActivated`, kept apart from it because everything
that reads the two is different: an activated ability is offered to its controller, a triggered
one is watching every event in the game. `TriggersWatching` is what `Consider` asks now, the way
`ActivatedAbilitiesOf` is what activation asks, and it writes each granted trigger into the same
side table the activated ones use so that the resolution lookup can find it.

The test kills two creatures. The one **without** the ability is what carries it: a version with
only the enchanted creature in it would pass against an implementation that handed the trigger to
everything, which is precisely what the filter bugs found two rounds ago were doing.


### The granted ability that activated and did nothing

Widening the grant pattern the round before put a family of new abilities on the board, and
asking the obvious next question - do they *work*? - found that they did not.

An ability goes on the stack as an **id**, and what the id means is looked up again when it
resolves, from the card of the permanent it came from. **A granted ability is not on that card.**
It is on whatever gave it. So the lookup returned nothing, `RunEffects` ran an empty list, and the
ability resolved as silence: the creature tapped, the cost was paid, and the card was never drawn.

Nothing had noticed because **every granted ability in the corpus until now was a mana ability**,
and a mana ability never uses the stack (CR 605.3b) - it resolves out of the definition that was
just found, so the second lookup never happened. The one worked example in this document is
`Enchanted land has "{T}: Add {B}"`. It is a mana ability. The moment the pattern learned to grant
anything else, the hole was load-bearing.

The definition is written down as the ability is activated, where it is still in hand, and read
back when it resolves. It is kept beside the modal-trigger choices rather than in the state, for
the reason given there: the state is a fold of the event log and an ability definition is code,
which no log can carry. That also answers a case the source cannot - CR 608.2 resolves an ability
whose source has left the battlefield, and a granted ability read back off a dead creature is
gone.

**The lesson is about the shape of the test, not the bug.** Every check on this family until now
asked whether the line *compiled*, and the answer was yes the whole time. The test that found it
activates the ability and counts the cards in hand.

### Granting a whole ability to a group, and two words that were read and thrown away

Chasing Backgrounds - 30 cards whose every line is `Commander creatures you own have "[ability]"`
- turned up something larger behind them. Granting a *quoted* ability is 301 lines across 300
cards, and the Aura half of it ("enchanted creature has ...") already worked. The group half did
not, and the reasons were three separate faults in one pattern:

- **The type nouns were case-sensitive**, so `Creatures you control have "..."` matched nothing.
  Exactly the fault found in the target grammar two rounds ago, in a different pattern, for the
  same reason: a sentence-initial capital is position, not meaning.
- **A bare plural tribe was not a noun at all.** The pattern demanded the word "creature", so the
  twenty-seven cards reading `All Slivers have "..."` went unread while the six spelling it out
  as `All Sliver creatures` were fine.
- **The noun, once read, was never checked.** The filter asked the tribe and the controller and
  nothing else, so `Lands you control have "..."` handed the ability to every permanent its
  controller owned. A bear with an Island's mana ability, and no symptom other than a board that
  can do slightly more than it should.

And beneath those, two words that the pattern matched and the code then discarded:

- **`all`/`each`**. `All Slivers have ...` is every Sliver on the battlefield, an opponent's
  included. With the word dropped the filter fell through to its default and read the line as
  "Slivers you control" - a hive lord that quietly stopped at the table edge.
- **`other`**. The word whose whole job is keeping a lord out of its own ability, discarded in
  the same place.

Both were *matched* by the regex and then never looked at again, which is the failure mode a
capture group invites: it looks handled from the pattern and looks handled from the code, and
only reading them together shows the word going nowhere. Six group forms now compile that did
not, and the behaviour tests are built around the permanent that must **not** get the ability -
a bear beside the lands, a bear beside the Slivers - because a grant is invisible from the card
that gives it and a filter that is too generous looks exactly like one that works.

Still unread and named: quoted *triggered* abilities (the grant path compiles activated and mana
abilities only), and "Commander creatures you own", which needs a group the vocabulary does not
have.


### Classes, and an ability that has to be switched off

The subtype instrument earned itself in one run: with the threshold lowered to twenty, the
mechanic subtypes came out on top and every one of them read **0%** - Class 38, Siege 36,
Background 30, Room 30, Spacecraft 29, Attraction 22, Trap 20. Knowing a family is unread still
says nothing about what it would cost, so the report now also prints *what stops each mechanic*,
and the answers are not alike at all: Siege is thirty-six one-off effects, while **Class is one
template on 76 lines** - `{cost}: Level N`.

Class is the first mechanic here where compiling an ability is not enough, because most of a
Class's text **must not work yet**. CR 716.2a: "[Cost]: Level N — [Abilities]" is an activated
ability *and* a static one - "as long as this Class is level N or greater, it has [abilities]".
The lines under a bar belong to that bar.

The ordinary line loop cannot express that: it reads a line, hands the ability to a builder, and
has nowhere to write "only from level 3". So a Class is split into sections and **each section is
compiled as a card of its own**, which reuses every matcher in the file unchanged; the abilities
that come back are wrapped in a level test on the way in. All four kinds have a predicate to wrap
- `Applies` on statics and replacements, `Triggers` on triggers, `ActivateOnlyIf` on activated
abilities - so the gate is one `&&` in four places rather than a new concept.

**A level is not a counter.** CR 716.4 says level counters and class levels do not interact, so
`Level` sits on the permanent beside the counter dictionary rather than in it. Put inside, a Class
would have answered to proliferate, to "remove a counter", and to everything that counts counters.

Two rules that are only visible when you try to break them:

- **A bar can only be activated from the level below it** (CR 716.2a). Without it a Class is
  bought once at its top level and every bar underneath is decoration.
- **"When this Class becomes level N" must not be gated**, though it is printed inside the very
  section level N switches on. A trigger predicate reads the state as it was *before* the event,
  where the Class is still on the level below - so the gate would refuse the one event the
  ability exists for, and no Class would ever announce a level. It carries a flag saying so,
  set by the compiler, which is the only place that knows which section a trigger came from.

Class went from 0% to 11% read, and the 76 level-bar lines are gone from the blocker list
entirely: what stops the remaining cards is now a long tail of individual effects, which is the
honest place for them. The gate is doing real work rather than merely compiling - the same anthem
gives a bear 2 power at level 1 and 3 at level 2.


### Group the corpus by something that is not its text

Sagas were found by luck - following a 28-line entry back to what it belonged to. The instrument
that would have found them on purpose now exists: **completion grouped by subtype**. A subtype is
a name cards share that does not depend on their wording, so a family hiding behind a hundred
different sentences shows up as one row with a bad ratio, however invisible it is to the two
line-ranked lists.

Its first run found two things, and neither was a template.

**642 cards had a subtype named `//`.** Both subtype parsers - the product's `CardParser` and the
corpus loader - took everything after the *first* em dash of the type line. On a two-faced card
that runs to the end of the *second* face. Delver of Secrets was an Insect while it was still a
Human Wizard; a card whose back face alone is legendary was legendary on the front, which is one
copy away from being put into a graveyard by the legend rule (CR 704.5j). Nothing crashed and no
card failed to load - tribal effects simply counted permanents they should not have, on the faces
of cards nobody was looking at. A card's characteristics are its front face's (CR 712.4a); the
back face has its own entry in `Faces` and is read from there.

**And the phantom subtypes had been masking a live bug.** With "Artifact" wrongly present as a
subtype on 41 cards, `Every_subtype_a_filter_names_is_a_subtype_some_card_has` was green. Removing
the phantoms turned it red on Sojourner's Companion: "Artifact landcycling" was asking the search
vocabulary for a card of the *subtype* "Artifact", which no card in the game has, so the ability
compiled, activated, and searched a library that could never contain a match. The keyword
capitalises whichever word comes first regardless of whether it is a type or a tribe; only the
type words are lowered now, so "Forestcycling" still fetches a Forest.

That is the argument for the instrument in one paragraph: a wrong fact in the data made a
correctness test pass. Fixing the data is what asked the question.

### Sagas, and the family the work queue could not see

The queue ranks templates by how many cards each would *finish*. That is the right ranking and
it has a blind spot, and Sagas fell straight into it: a Saga has three or four unread chapters at
once, so **no single chapter line was ever any card's sole blocker**. 240 cards sat invisible
under a queue whose head was worth fourteen. They were found by asking the other list - the one
ranked by lines - what the biggest *unread* thing was, and following a 28-line entry back to what
it belonged to.

The mechanic is four rules and only one of them is printed on the card:

- **CR 714.2b** - a chapter symbol is a triggered ability: "when one or more lore counters are
  put onto this Saga, if the number of lore counters on it was less than N and became at least
  N". The *crossing* is the trigger. Read as "there are now N counters", chapter II fires again
  every time anything else adds a counter, and nothing fires at all when two arrive at once.
- **CR 714.2c** - "II, III — [effect]" is *two* abilities. One printed line that has to run twice.
- **CR 714.3a** - every Saga has an intrinsic "enters with a lore counter", which nothing prints.
  Leave it out and the Saga sits on the battlefield at zero and simply never begins.
- **CR 714.3c** - a turn-based action at the controller's precombat main phase, not an upkeep
  trigger and not every player's turn.
- **CR 714.4** - sacrificed once the counters reach the final chapter, *unless* it is still the
  source of a chapter ability on the stack. The counter that reaches the final chapter is the
  same counter that triggers it, so reading this one carelessly sacrifices every Saga in the game
  one resolution early and its last chapter never happens.

**A trigger predicate is handed the state as it was before the event.** Written the other way
round, every chapter fired exactly one advance late and chapter I never fired at all - the Saga
entered with its first counter, read a count of zero, and concluded it had not arrived. The tests
caught it because they assert life totals rather than counters: the counter was always right.

#### The bug underneath: chapter III was flavour text

`AbilityWord` strips "Landfall —", "Constellation —" and the rest, which have no rules meaning
(CR 207.2c). Its pattern needs three or more capitals before the dash - and **"III" is three
capitals before a dash**.

So chapter III of every Saga in the game had its symbol stripped as flavour and its effect left
behind as a homeless sentence, while chapters I and II were too short to match and came through
intact. That is the shape of bug this corpus keeps producing: not a crash, not a card that fails
to load, but a card that reads two of its three chapters and looks entirely fine from outside.
It survived until something needed chapter III by name.

Sagas went from 4 of 196 single-faced cards to 18 in a direct probe. Most of the rest are now
blocked by the *effect* of one chapter rather than by the chapter grammar - which is an ordinary
long tail, and the honest place for them to be. Read ahead (CR 702.155) is deliberately still
unread: it replaces 714.3a with a choice, and a Saga carrying it stays incomplete rather than
silently starting at chapter one.


### The work queue is flat, and that is the important number

The queue has always been ranked by *cards each template would finish*. It is now measured for
steepness as well, because the shape of the list decides what kind of work is left:

```
the queue is 14347 distinct templates covering 15657 cards (47.8% of the corpus)
  top   10:     88 cards  (0.3% of the corpus,  0.6% of what the queue can reach)
  top  100:    522 cards  (1.6% of the corpus,  3.3% of what the queue can reach)
  top 1000:   2310 cards  (7.1% of the corpus, 14.8% of what the queue can reach)
```

**14,347 templates for 15,657 cards is 1.09 cards per template.** The head of the queue is worth
fourteen cards; early on it was worth hundreds. Those 15,657 are only the cards that are *one*
line short - roughly 4,900 more need two or more.

This is what the end of template work looks like from the inside. It does not announce itself:
every round still finds a family, builds it, and moves the number a little, and the number keeps
moving. What changed is the exchange rate. Reading one more template used to buy hundreds of
cards; it now buys one, which is the cost of writing cards one at a time - the approach this
engine was built to avoid, and which `CompiledCardBehaviourTests` names in its own remarks as
"the same mistake as writing them one at a time".

So the honest statement of where the corpus stands is not a percentage but a slope: **the
remaining 63% is not reachable by finding better templates, because there are no bigger ones
left to find.** A different method - per-card data, or a generative pass with the compiler as
its verifier - is what the rest would take, and that is a decision about direction rather than
another round of the same work.

### The noun alternation was case-sensitive

Found by the group forms left undone last round. The target grammar tells a creature type from an
ordinary noun by its capital letter, so the noun alternation ran with `RegexOptions.None` - and a
sentence-initial capital is position, not meaning.

The bare types survived that only by accident. "Creatures you control" failed every lowercase
alternative, fell through to the tribe branch `[A-Z][a-z]+`, matched "Creature" - and the noun is
lowercased before it is looked up, so it came out as the card type anyway. Anything with a
**second word** had no such luck: "Creature token" matched "Creature" and left " token" with
nowhere to go.

Every ordinary noun is case-insensitive now; only the tribe fallback keeps its capital. The
accident is still an accident, but nothing depends on it any more.


### "Unless" looked like 224 lines and is sixty shapes

The sweep put ", unless …" second with 224 lines, so the tails were broken down the same way the
"for each" ones were. **The biggest shape is six lines.** "Unless they pay {M}", "unless you
discard a creature card", "unless you return an untapped Island you control to its owner's hand" -
sixty-odd distinct clauses, two to six lines each, and no template worth building a mechanism for.

That is a large negative and it is the point of measuring before building: 224 in a ranked list
looks like the next big family, and it is a long tail wearing one word as a hat.

### "Creature token" is a noun, not two nouns

Taken instead, at 52 lines. The noun alternation in the target pattern is **ordered**, and
"creature" sat in front of "creature token" - so the bare word won, the leftover " token" had
nowhere to go, and the phrase failed. The compounds go first now and the type table gained the
pairs to match.

The non-token creature is what carries the test: a version with only the token in it would pass
against a reader that took "creature" and dropped the rest, which is precisely what the pattern
did.

**Left undone, and named:** the *group* forms - "creature tokens you control get +1/+1" - still do
not read, and not because of the word "token". `ParseGroup` treats a leading capitalised word as a
tribe, so a sentence that simply begins with "Creature" is read as a creature type. That is a
separate bug with a wider blast radius than this fix, and it wants its own pass.


### Counting a pile rather than the board

Fresh rewrite candidates put a new shape at the top by a distance: **295 lines** where dropping the
", where X is …" tail makes the line readable. Probing six of them found the head was never the
problem - "where X is the number of creatures you control" already read. What did not was the pile:

> …, where X is the number of **creature cards in your graveyard**.

The counting vocabulary walked the battlefield and nothing else. It now walks a hand or a graveyard
too, with the noun in front read by the same type table the target grammar uses everywhere - so
"cards in your hand", "creature cards in your graveyard" and "cards in all graveyards" are one
reader. That also answers 21 of the "for each" tails from the earlier breakdown, which were the
same question in the other word order.

A noun the type table does not know leaves the phrase **unread** rather than counting everything:
"the number of Zombie cards" is not "the number of cards". The land sitting in the graveyard in the
test is what proves the noun is read at all - without it, a reader that ignored the word would pass.

**36 cards**, the largest single move since the group-"other" fix.


### "If you do" after something that was not a choice

The engine understood "if you do" only after a **may** - after an offer, where the answer is a
choice. The other half of the phrase follows a *mandatory* action:

> Tap target creature you control. **If you do**, you gain 2 life.

and there the question is whether the action came off at all. A target that has left, a creature
already tapped, a card no longer where it was: the instruction is given and nothing happens. 14
corpus lines, read before the sentence splitter for the same reason the offer is - split, the
second sentence is a condition about something the first one did.

**"Happened" is read as "produced events"**, which is the engine's own record of something
occurring and what every effect already answers with. An approximation in one direction only, and
stated rather than hidden.

The test wanted a legal target the action whiffs on, and its first version could not get one: an
already-tapped creature is not a legal target for "target **untapped** creature", so the spell
could not be cast at all. The engine was right; the card text is "target creature" now, which is
the shape that actually reaches the case.


### A search had a ceiling and no floor

"Search your library for an artifact card with mana value 2 **or less**" read; "6 **or greater**"
did not, on 16 corpus lines. Two nullable numbers rather than a signed range, because a card names
one or the other and never both - and two nulls say "no limit" without inventing a sentinel.

**Driven through the offered list, not the compiled record.** What a limit is *for* is which cards
a player may pick, so a floor that was stored and never consulted would sail through a test that
only read the effect back. The library has both relics in it and only one is on the menu.

The theory's first run was refused by the pool: `Card(...)` derives an oracle id from the name, and
two cases with one name are two different cards claiming one id. That guard exists because a pool
serving one card's behaviour for another is the kind of bug nothing else would catch, and it did
its job on a test rather than on a card.


### A type word with a type taken out of it

"Target **permanent** card in your graveyard" read; "target **nonland** permanent card" did not, on
7 corpus lines - and for two reasons at once, which is why the probe had to be run twice before it
went green. The phrase pattern took a single-word noun, so the phrase never reached the type
reader; and the type table it hands the noun to has no entry for a type with a type removed, and
should not have one.

So the word is lifted off before the table is asked and becomes one more test on the card. Every
noun the table already knows keeps working with it in front, which is the point of doing it there
rather than adding rows.

The assertion that carries the test is the **land** in the graveyard being refused. A test with
only the creature in it would pass against a reader that ignored the word entirely - which is
exactly what the code did before.


### "You may", with nothing to pay for it

Widening the rewrite sweep's vocabulary put a new shape on top: **45 lines** where dropping the
words "you may" makes the line readable. The commonest optional effect in the game, and it had no
reader at all - every particular "you may" had one ("you may pay {2}", "you may sacrifice a
creature. If you do, …") and the plain one did not.

Dropping the words is not an option: a trigger that always draws is a different card. So the
sentence becomes the **free offer the engine already had** - a `MayPay` with nothing to pay - with
the sentence's own words as the button. Read **last**, after every reader that knows a particular
"you may", so the general shape never takes a sentence one of those would have understood better.

The test is a theory over both answers, and the one that carries it is **declining**: an offer that
always happens is exactly the card this fix exists to avoid compiling.

The sweep's next tier, for whoever picks this up: `that player controls` (24), `with mana value N
or less` (16), `If you do,` (14), `face down` (10).


### "That creature" is "it" with more words

The pronoun readers for the verbs that take one - untap, tap, destroy, exile, return - have
accepted both spellings for a long time. The three that **grant or pump** accepted only "it", so

> Put a +1/+1 counter on target creature. **That creature** gains indestructible until end of turn.

was unread while the same card saying "it" was, on 19 corpus lines. Three characters of alternation
in three patterns.

That is the fourth "two spellings of one sentence" fix in this stretch - "each of", "each get",
"another target", and now this - and they share a shape worth naming: **the cheapest gap to close
is one where the engine already understands the thing, just not the words.** The rewrite sweep
finds exactly those, and it finds them ranked, which is why it keeps being worth re-running after
each change rather than once.


### "Other" again, in the other place

Re-running the rewrite sweep after the group fix put "other" back at the top with **46 lines** -
and they are a different place. These are *targets*: "put a +1/+1 counter on up to two **other**
target creatures". The singular of that sentence has read since the target grammar was built, as
"another target creature"; what could not read it was the rewrite that turns a counted target
phrase into a singular one to parse it, which required the number and "target" to be adjacent.

So the rewrite now says **"another"**, which is how the singular grammar already spells it - and
every reader that handled one of those handles all of them. Nothing new had to learn what the word
means, which is the same shape as the "each of" and "each get" removals: when two sentences mean
the same thing, make them the same sentence.

**The test had to be reworked rather than the code.** Its first version asserted which of the two
creatures got which counter, and that is the *harness's* choice - it answers every question with
its first option - not the card's. What the card decides is that the source is not among the
answers. So it asserts that: two counters went out, and none of them to the herald.


### Blinking yourself, which transform made expressible

> Exile ~, then return it to the battlefield **transformed** under your control.

The *targeted* form of this sentence has been read since the flicker effect was built - "exile
target creature you control, then return that card to the battlefield" - and it is read before the
text is split into sentences, because the split happens on ", then" and neither half means anything
alone. The **self** form was not read at all, on 28 corpus lines.

They are kept apart rather than folded together, and the reason is the word in the middle: a card
that blinks *itself* is usually doing it to arrive as its other face, which the targeted form never
says - and which was not expressible at all until transform existed a few sections ago. A card with
nothing to turn over folds that half to nothing, which is the right answer for a sentence that
cannot apply rather than a reason to refuse the line.

What comes back is a **new object** (CR 400.7), so nothing it had before travels with it. The test
puts a counter on it first and asserts the counter is gone, which is the half a test of the face
alone would miss - and the half every card printed this way is actually for.


### A pronoun read in one direction only, and a fold that threw

The grammar already read "attach it to target creature you control", where the pronoun is the
Equipment. **16 corpus lines say it the other way** - "…, then attach ~ to it" - where the pronoun
is the *destination*, and it means what every other "it" here means: the target the sentence before
it chose. Unread while its mirror image was not.

Building it found something larger. The reducer's attach arm did `GetObject` and threw when the id
was not there, where every other arm asks with `TryGetObject`:

> **A fold has to be total.** An event naming something that has since left is ordinary - an Aura
> whose host was removed in response, an ability resolving after its source was destroyed - and it
> must fold to "nothing happens", not to an exception that takes the game down. That arm is now
> like the rest.

**And the same question was then asked of every arm.** Thirteen of them changed one object by id
and asked for it outright; every one is now guarded, behind a single helper so the next arm added
gets the rule for free. The zone-change arm is deliberately left strict: a move naming a missing
object, or one leaving a zone it is not in, is an emitter bug and its loud complaint is worth
keeping. The check asserts the *rule* over all thirteen shapes at once rather than one test each,
because what would break it is a fourteenth arm that forgets.

The test also had to be corrected rather than the engine: it built the fixture as an **Aura**, and
an Aura that reaches the battlefield attached to nothing is put into its owner's graveyard by
state-based action (CR 704.5m) - so the card was gone before its own trigger resolved. The engine
was right and the fixture was wrong; it is Equipment now, which is what those corpus lines mostly
are anyway.


### Counting counters rather than permanents

The removal sweep said the two biggest entries were **truncations** - "for each ..." and ", then
..." - which say where the difficulty is rather than what to remove. So the same instrument was
pointed one level deeper: break those tails down by what is actually *in* them.

No single template dominates either list, but one cluster does: **counting the counters on a
permanent** - "+1/+1 counter on ~", "charge counter on ~", "age counter on it" - is 26 lines across
four spellings that differ only in which word names the permanent. Both "~" and "it" mean the
source, because a counting phrase has no target of its own to point at.

**And the tilde had to be let into the noun class the counting phrases share.** It is safe where
`+` and `/` are not: admitting those two turned "target creature gets +2/+2" into a target called
"creature gets +2/+2", which is why the class excludes them and still does - so the `+1/+1 counter`
spellings remain unread, deliberately, and the plain-word ones now read.


### Sweeping for the missing word rather than the missing template

The sentence sweep worked, so the same trick was pointed at a different question: for every unread
line in the corpus, try a dozen mechanical **removals** and see which one makes it readable. What
comes back is a map of the vocabulary's missing qualifiers, ranked:

```
  414  drop "for each ..." tail        (the tail is the unread part, not a missing word)
  348  drop ", then ..." tail          (likewise)
  127  drop "other "                   <- a real gap
   19  "that creature" -> "it"
    8  drop "an opponent controls"
    7  drop "nonland "
```

The two big ones are truncations that change what the card does - they say *where* the difficulty
is, not what to remove. **"Other" is a gap**, and the distinction matters: a lord that pumps itself
is a different card, so the word cannot simply be dropped. It is lifted off before the noun is read
and put back as a filter, layered onto whatever the phrase already asked for rather than replacing
it - the source is one more thing the group is not, not the only thing.

**127 lines, 73 cards.** And the assertion that carries the test is the lord's *own* power: a test
that only checked the other creature would pass just as well against a reader that ignored the
word. Two of them on the battlefield pump each other and neither pumps itself, which is the shape
that would have caught a filter applied to the wrong object.


### A sweep that mostly said "no", and the one thing it found

Conjunctions looked like an obvious seam: a line is several sentences, each sentence has its own
reader, and joining them is where things go wrong. So the corpus was swept for exactly that shape -
**unread lines every one of whose sentences reads on its own**.

**Seven, out of 32,765 cards.** The sentence splitter is not a seam, and a plausible afternoon of
work was cancelled by four minutes of sweeping. That negative is the more valuable half of this
section.

Six of the seven were one bug. "Counter target spell unless its controller pays {2}" is read by a
reader registered at the top of a whole phrase - and *only* there. A line with a second sentence
after it is split into sentences, and the first sentence then reached a vocabulary that had never
been told about the offer. The reader is now on both paths.

The borrowed parse had to be shifted with it: it is built against a list of one and aims at target
zero, which is the right index only when the offer is the whole phrase. That was latent for as long
as the reader existed and could not show until it was called from somewhere else.


### The other word order, and a verb that had to agree twice

The same distribution said the other way round, on **43 cards**:

> Up to two target creatures **each** get +2/+2 and gain first strike until end of turn.

The multi-target rewrite already handles "two target creatures get +1/+1" - it makes the sentence
singular, parses it with the ordinary vocabulary, and then makes that many copies. The word "each"
sat between the noun and the verb and stopped the match. It comes out with the same pass that
removes "each of", and with the same anchoring: strictly between a target phrase and its verb, so
the "each" that is a quantifier - "each creature you control gets +1/+1" - is untouched. The probe
checks both in one run.

**And the second verb had to agree.** The rewrite makes the sentence singular to parse it, so "get
+2/+2 and gain first strike" became "gets +2/+2 and **gain** first strike" - and the combined
matcher wants "and gains". A sentence with two verbs was read only when it had one. Both are agreed
now, which is what the "and gain vigilance" family from earlier in this document needed the first
half of.


### "Each of", support, and a backspace in a pattern for the second time

Two instructions that are one instruction:

> Put a +1/+1 counter on **each of** up to two target creatures.
> Put a +1/+1 counter on up to two target creatures.

The second was read and the first was not, on **83 cards**. The engine already makes one effect per
target, so "each of" is spelling out what the shorter form leaves implied - the words are removed
rather than a second reader written for them. Anchored on a number and the word "target", so the
"each of" that means something else - "each of your opponents", "each of the exiled cards" - is
left alone.

**Support N** is then the same sentence with its words taken out (CR 701.41a), and they are put
back before anything else reads the line. Which words depends on the card: "support N" on a
permanent says "each of up to N **other** target creatures" and on an instant or sorcery it does
not, because a permanent can otherwise support itself.

**And the pattern went in with a literal backspace in it - 0x08, where `` was meant.** That is
the second time in this session; the first is why
`No_compiled_card_pattern_contains_a_control_character` exists. It fired the moment it was run:
reinstating the bad byte turned it red and taking it out turned it green, which was checked rather
than assumed. The pattern compiled cleanly and matched nothing both times, which is exactly why a
regex that cannot fail loudly needs a test that can.


### Enlist, which the re-check said was closer than its note

**CR 702.154a**: "as this creature attacks, you may tap up to one untapped creature you control
that you didn't choose to attack with and that either has haste or has been under your control
continuously since this turn began. When you do, this creature gets +X/+0 until end of turn, where
X is the tapped creature's power."

The note that declined it said it needed a runtime-amount pump. That arrived rounds ago and nobody
went back to look - which is the whole point of re-reading refusals rather than trusting them. What
was *actually* missing was somewhere to ask a question **as an attack is declared**: enlist's tap is
an optional cost to attack (CR 508.1g), so it belongs between the declaration and priority, and the
declaration step had no hook there.

Two details worth the ink:

- The last clause of the rule - "haste, or under your control since this turn began" - is summoning
  sickness said the long way round, which the permanent already records. A creature that could not
  have attacked cannot be enlisted either.
- The pump is a **fixed** number, not a running count. "+X/+0 where X is the tapped creature's
  power" is read once; a creature that grows or dies afterwards does not change what was added.

**A simplification, stated rather than hidden:** the rule makes the pump a triggered ability linked
to the tap (CR 702.154b), so it uses the stack and can be responded to. Here it happens with the
tap. What that costs is the window between them.

The test is a theory over both answers, because "up to one" means the declining half is the card
too - and it asserts the tap, since an enlist that took nothing would pump just the same.


### Re-checking the standing refusals, and covering the hook that unblocked one

The shockland's lesson was tested rather than assumed: every card family this document had recorded
as "declined, needs machinery the engine does not have" went back through the compiler. **All eight
are still unread**, which is the expected answer - none of them were built - so what the probe was
actually for is which are still *infeasible*, and the honest answers differ:

- **Protection from a tribe** - still genuinely blocked. Protection lives in keyword flags and a
  tribe is a string; there is nowhere to put it.
- **Firebending** - still blocked. Mana that lasts beyond the step has no representation.
- **"Any number of target creatures"** - still deliberate. An unbounded target list is a decision,
  not a gap.
- **Enlist** - closer than the note says. The runtime-amount pump it needed exists now; what is
  left is an optional tap *as the creature attacks*, which is a hook the declaration step does not
  have.
- **"Assign combat damage as though it weren't blocked"** - the note says it takes away a real
  choice, and 7 of its lines begin "**You may** have ~", so that is still true and still the reason.
- **Bolster** - the note says the shipped rulebook does not contain it, and that is confirmed: zero
  occurrences. It was reminder-text-only in its own set and the keyword was never in the rules.

**And the new hook needed covering.** `Decline` was added last round and nothing in the corpus
checks exercised it - the replacement invariant only ever applied. It now runs the declined branch
too, and carries a floor: without one, a corpus with no such effect would run none of them and
report green, which is the shape of vacuous check this file has caught twice before. **11 of 1,131
replacements say what declining means**, and the floor is 10.

### The shockland, declined three times and built on the fourth

> As ~ enters, you may pay 2 life. If you don't, it enters tapped.

This was turned down twice as "a player decision inside a synchronous `Replace`", and that was the
right call at the time. It is not any more: the CR 616.1 ordering question already holds the event,
asks, and re-emits it, and an *optional* replacement already offers "apply" or "let the event
happen instead". What was missing was somewhere to say what declining means.

Because a shockland is the one shape in the corpus where **both outcomes differ from the event**.
Applying is paying 2 life and arriving upright; declining is arriving tapped; and arriving upright
*for free* - which is what "the event happens as it was" means - is the one thing the card never
does. So `ReplacementEffectDefinition` gained a `Decline`, consulted only when the declined
question had exactly one candidate: with several, what declining means is a question about all of
them (CR 616.1) and one effect's answer is not the answer.

CR 118.4 does the rest: a player may pay life only down to zero, so one who cannot afford it is
never offered the choice and the land simply arrives tapped. That is asked in `Applies`, because an
offer that cannot be taken is not an offer.

**The lesson is about the declining, not the land.** "Needs machinery the engine does not have" is
a statement with a date on it. This one was true for three rounds and stopped being true without
anybody noticing, because the machinery arrived for a different reason - and the queue kept
listing it at the top the whole time.


### "Must be blocked if able" is not a smaller lure

Two block requirements that read alike and are different rules (CR 509.1c):

> All creatures able to block ~ do so.   *compels every creature that can*
> ~ must be blocked if able.             *compels one*

The lure was already modelled, and it was tempting to treat the weaker one as the same flag with a
smaller number. It is not: a creature can carry both, and neither check answers for the other. So
there are two flags on the computed characteristics and two checks in the declaration, and the
weaker one is asked first because it is satisfied the moment *anything* blocks - which is the
cheaper question and the one that can short-circuit.

The requirement is only a requirement while the defender has something that could meet it. The test
asserts both halves: declining to block is refused while a creature could, and blocking with the
one creature that can is enough - the lure would want all of them.


### Bargain, and a clause that is not where kicker's is

Bargain (CR 702.166) is kicker's shape with a different cost: an optional additional cost - sacrifice
an artifact, enchantment or token - whose payment is recorded and read back by "if this spell was
bargained". It reuses everything kicker has except the two things that must not be shared.

**It gets its own flag and its own wrapper.** The two clauses are linked abilities (CR 607.2), each
to its own cost, so a card printing both would need each half to answer for its own - and one
wrapper reading one flag cannot do that. `SpellBargained` and `IfBargained` sit beside
`SpellKicked` and `IfKicked` rather than borrowing them.

**And the clause is not where kicker's is.** Kicker's "if this spell was kicked" is a line of its
own, so its reader is a line reader. Bargain's is printed *inside* a line - "~ deals 4 damage to
target creature. If this spell was bargained, destroy that creature instead" - so it belongs in the
sentence grammar. Which loses kicker's guard: a line reader can be asked only on a card that has a
kicker, and the sentence grammar cannot see the rest of the card. So the guard moved to where it
can see - the compiler refuses a card that reads the clause back without having the ability, rather
than compiling it to a wrapper that is never true.


### A family sized and not built

**"Untap it"** looked like a family and is not one. 40 cards carry an "untap it" or "tap it"
clause, and the pronoun means something different on nearly every one: the enchanted creature, a
target chosen two clauses earlier, the source itself. The engine reads it correctly when a target
was chosen and leaves it unread otherwise, which is the right answer for a word that means four
things - there is no template here, only forty cards.

### Saddle, and a clause that belongs to neither grammar

**Saddle N is crew with a different consequence** (CR 702.171a): the same measured-rather-than-
counted tap cost - any number of other untapped creatures, so long as their power adds up - and
instead of animating the permanent it sets a status. So the keyword cost 30 lines of code and the
interesting part was everything crew does not have: the status, the trigger that reads it, and
that it is gone next turn.

Being saddled is until end of turn, and it is cleared **where damage is cleared** rather than by an
event of its own, because CR 514.2 says that is the same moment. Nothing new had to remember it.

**And the citation was wrong for a whole round.** Saddle went in as CR 702.166a, which exists -
702.166 is *bargain* - so `RuleCitationTests` waved it through, exactly as the note two sections
down says it would. It surfaced only because the next family looked up was bargain and the number
was already taken. Saddle is **702.171**. The guard catches inventions; nothing catches a real rule
cited for the wrong thing except opening the book, and the book has to be opened per family rather
than per session.

The other half is a clause that belongs to neither grammar:

> Whenever this creature attacks **while saddled**, ...

The front is an ordinary trigger condition and the back is an ordinary board condition, so they are
read separately and joined - teaching either grammar about the other would be a third grammar. Two
details matter and both are the difference between a card and a better card:

- A "while" the board reader cannot answer leaves the **whole trigger** unread. A trigger that also
  fires while it is *not* saddled is a strictly better card than the one printed.
- "While saddled" has no subject because the sentence already named one, so a bare clause that
  fails is retried as "~ is ...". Asked plainly first, so a clause that *does* name its own subject
  still reaches the reader that expects one.

The test drove the saddle into the wrong turn on its first run - `PassTo(game, 3, ...)` waits for
turn three, and being saddled had ended two turns earlier. It read exactly like the trigger not
working, which is worth remembering: a status with a duration makes every test about it also a
test about when it happens.


### Day and night

**CR 731**, with the keywords at **702.145** and the check itself at **502.2**. Three rules that
only mean anything together, so they are one test:

- A permanent with daybound on the battlefield makes it day when it is neither (702.145d), and one
  with nightbound makes it night when it is neither and no daybound permanent is out (702.145g).
- At the untap step, a day where the previous turn's active player cast nothing becomes night, and
  a night where they cast two or more becomes day (502.2). **While it is neither, the check does
  not happen at all** - a game with no werewolf in it never has a time of day.
- A permanent showing the wrong face for the time turns over at once (702.145c, 702.145f) -
  immediately, and explicitly *not* as a state-based action.

The "any time" rules are asked in the same sweep that checks state-based actions, which is where
rules that watch the game rather than an event belong, and they are asked *before* the actions
because turning a permanent over changes what the actions then see. The loop is what handles a
change causing another: making it day is itself a change the face rules have to see.

The check needs the previous turn's active player, and by the time it is asked the turn has
changed - so the state records who that was rather than stepping back through the turn order,
which would be wrong the moment somebody takes an extra turn.

**Not enforced, and worth saying:** CR 702.145b also means "this permanent can't transform except
due to its daybound ability". Nothing stops another effect turning a werewolf over here.


### The werewolves, and an effect that turned the wrong object over

With a transform mechanic underneath them, most of the transform *templates* turned out to be
already read: `{2}{R}: Transform ~`, "at the beginning of your upkeep, transform ~", and "at the
beginning of your first main phase, you may pay {2}. If you do, transform ~" all compiled the
moment "Transform ~" became a sentence. Three did not, and two of them are the day-night cycle:

> At the beginning of each upkeep, **if no spells were cast last turn**, transform ~.
> At the beginning of each upkeep, **if a player cast two or more spells last turn**, transform ~.

63 lines, and the whole of what a werewolf is. The state knew how many spells had been cast *this*
turn and had no memory at all of the turn before, so the count is now carried forward as the turn
changes - forward, because a fold may not read the log to look backwards.

**The effect fired, resolved, and turned nothing over.** A triggered ability is its own object
while it resolves, so `context.SourceId` is the ability on the stack, not the permanent that has
the faces - and an ability has no faces, so the effect found none and returned nothing. The
permanent behind it is `PhysicalSourceId`, which is what every other self-affecting effect in the
file already used. The trigger appeared in the log, the transform did not, and the probe printed
both in one run.

The test is a theory over the quiet turn and the noisy one, because the interesting half is the
one where **nothing** happens: a test that only played the quiet turn would pass just as well
against a trigger that ignored its condition entirely.


### Two-faced cards are read a face at a time

The compiler no longer sees a two-faced card as one body of merged text with a `//` in it. It
compiles the **front face** as the card - which is what a card is anywhere except on the
battlefield showing its back (CR 712.8a, 712.8d) - and compiles the other faces too, only to count
them: a card is understood when *every* face of it is. A card whose back face says something the
engine cannot read is not one it can be trusted to play.

The back face's abilities are deliberately **not** merged into the front's. They are reached by
compiling that face's definition, which is exactly what a transforming permanent's card becomes -
so the pool finds them under the face's own oracle id, at the moment the permanent is showing it.
Nothing in the pool, the activation path or the layers was told that faces exist.

That is the whole of it, and it moved **136 cards** and 837 lines of separator out of the unread
column in one change. The measurement in the section below predicted 114; the extra came from
cards where merging the two halves had confused the parser about which sentence belonged to which
half.

**An invariant had to change its mind, which is the point of writing down why it exists.**
`No_card_with_two_faces_is_reported_as_understood` failed on the first green build, exactly as it
should have: it said such a card is *never* understood, and that was right while the model held one
set of characteristics. The rule that replaces it protects the same thing under the new model - a
card is understood only when **every** face of it is - and it is worth keeping now that the
guarantee is structural, because a refactor that lost it would otherwise be silent. 853 cards carry
more than one face and 145 of them are understood.

The runtime half has its own test, and the engine was right where the test was wrong: activating
the back face's `{T}` ability was refused for summoning sickness (CR 302.6) on the first run. Its
back face has haste now, which is also the keyword half of the same claim - a face's keywords are
the permanent's while it is showing that face.


### A permanent can turn over

The mechanic, built on the measurement above. A permanent showing another face has that face's
characteristics (CR 712.2), and the way that is done here is to **swap the object's card** for the
face's definition rather than remember the face beside it.

That is the whole reason the change is small. The layers, the ability source, the legality checks
and the per-player view all read one `CardDefinition`; after the swap every one of them reads the
face the permanent is on, without a line of change and without learning that faces exist. The face
list travels with the swapped-in definition, which is what lets it turn back, and the face's index
is kept on the permanent so the fold can say which face it was rather than inferring it from a
name.

The test asserts through `Characteristics.Of` on purpose: a cosmetic swap would still report the
front face's power. It turns over, gets bigger, gains trample, and turns back.

**`RuleCitationTests` caught four wrong rule numbers on the way in.** It scans every `.cs` in
`MtgEngine.Rules` for `CR n.n` and fails on any that is not in the shipped rulebook, and it took
712.2, 712.4d and two 712.9a's - none of which exist. The right ones are **712.8d** (a permanent
with its front face up has that face's characteristics), **712.8e** (a back face's mana value is
calculated from the front) and **712.9** (only some double-faced permanents can transform at all).
Worth being precise about what that guard does and does not do: it checks a citation *exists*, not
that it is the right rule. The 202.3b-for-X mistake earlier in this session was a real rule cited
for the wrong thing, and this test would have passed it.

**No printed card compiles to this yet.** The compiler still leaves the `//` between the halves
unread, which is the honest state of it - the mechanic exists and is tested, and wiring the 853
cards to it is the next piece rather than something quietly half-done.

### The card model can hold two faces, and now we know what that would buy

`CardDefinition` had one name, one cost, one type line, one pair of numbers and one body of text.
853 playable cards have two of each. It now carries `Faces`, both loaders fill them in, and
`CardCompiler.CompileFace` compiles one face the same way it compiles a card - the only work is
handing the phrase parser a definition that says what the *face* says.

**This does not make a single one of them playable, and the `//` between the halves is still
deliberately unread.** The engine has one set of characteristics per object and no way to cast one
face rather than another. What the slice buys is the measurement, which is worth having before
committing to the rest:

```
two-faced cards: 853
faces fully read: 507 of 1706
cards whose every face is read: 114
```

**114 cards are one card model away**, not two hundred templates away. And what stops the other 739
is almost entirely one mechanic:

```
   72  Daybound / Nightbound
   64  At the beginning of each upkeep, ... transform ~
   33  {M}: Transform ~. Activate only as a sorcery.
   31  Disturb {M}
   28  Exile ~, then return it to the battlefield transformed under your control.
```

Transform - a permanent being on its other face - is the same feature as the card model, seen from
the other side. That is the shape of the work, and it is a feature rather than a template.

### A line count is not a card count

The unread-lines list now prints both, because ranking by lines misled a whole afternoon's
planning. `{M}{M}: Level N` is 60 lines - and 35 cards. The leveler family is 110 lines on **25**
cards, four or five each, because a leveler prints a band per level; of those 25, only 5 have bands
made purely of a P/T and plain keywords. Sizing it by lines made a twenty-five-card mechanic look
like a hundred-card one, and it was the card count that said not to build it yet.


### The monarch, and a hook that watched the wrong door

**CR 725** - and it is 725 rather than 720, which the shipped rulebook said and memory did not.
720 is Omen cards in this release. Looking it up cost thirty seconds and would have put a wrong
rule number in three files.

The monarch is a designation at most one player holds (CR 725.3), so it lives on the state as one
nullable field rather than as a flag per player - a flag per player is a fold that can produce two
monarchs. On top of that sit the two inherent abilities the *game* has rather than any card
(CR 725.2): the monarch draws at the beginning of their own end step, and a creature that deals
combat damage to the monarch hands the crown to its controller.

**The second one was written in the wrong place and looked right.** It went beside
`MarkDamageToPlayer`, which is the method that deals damage to a player - and combat does not call
it. `CombatRules` builds its own `PlayerDamaged`, so the crown never moved for the one thing the
whole mechanic is about. The probe said so in one line: the damage was in the log, the monarch had
not changed. It now hooks where *every* `PlayerDamaged` passes, beside commander damage and
lifelink, which is the only place that sees all of them.

**A simplification, stated rather than hidden:** the rules make both of these triggered abilities
with no source, so they use the stack and can be responded to. This engine keys a pending trigger
to the permanent that produced it and has nowhere to put a sourceless one, so both happen directly.
The window between trigger and effect is what is lost; nothing else differs.

60 cards mention the monarch and 35 of them say "you become the monarch" and nothing else about it.


### A triggered ability can offer modes

"When ~ enters, choose one —" is a trigger whose effect is a menu, and the menu is on the lines
after it. Modal **spells** have worked since modes were built, because a spell's modes are chosen
while it is being cast (CR 601.2b) - the engine is already talking to the player and never has to
leave to ask. An ability's are chosen as it is **put on the stack** (CR 603.3c), which happens in
the middle of settling, so the question has to become a pending choice and be resumed later.

That machinery already existed for the ability's *targets*, and this is deliberately built on the
same pattern: an answer filed against the trigger by a key encoded in the choice id, because the
engine holds no continuation - state folds from the log, so nothing computed when the question was
asked is still in hand when it is answered. The modes are asked **before** the targets, because
the modes decide what there is to target at all.

Three things were made to fail closed rather than quietly:

- A bullet the phrase parser cannot read leaves the bullet unread, so a modal ability never offers
  fewer modes than it prints.
- A header with no bullets under it - a menu with nothing on it - puts the *line* back to unread
  and drops the ability, rather than compiling to a choice that cannot be made.
- The structural check now asks CR 700.2d of abilities as well as spells: an ability that takes
  more modes than it offers is a fault, on every card in the corpus.

**71 corpus lines** open a trigger with a menu; reading them and their bullets moved 58 cards and
293 lines.


### The work queue only showed half the queue

The coverage test computed two lists and printed one. The one it printed ranks templates by **how
many cards each would finish**, and that list systematically hides large families: a line on a
hundred cards that all have a second unread line completes nothing and shows as a zero. The mass
grant above sat at **5** in it and was worth **138 cards**, because the other unread lines on those
cards were the *same family*.

Both are printed now. The second - commonest unread lines, whether or not they are the only one -
opens like this, and it is a different document entirely:

```
   837  //
    71  When ~ enters, choose one —
    60  {M}{M}: Level N
    55  ward {M} (inside a keyword list)
    36  Daybound / Nightbound
    36  When ~ enters, you become the monarch.
```

Neither list is the right one on its own. The first says what to build to finish cards *today*; the
second says how big a template really is.

### A keyword list was read whole or not at all, and one costed keyword broke it

That rule is deliberate - half a list is a card that does half of what it prints - but the plain
reader can only recognise keywords that arrive as **flags on the card**, and a costed keyword like
`ward {2}` is not one. So **"Flying" was read, "Ward {2}" was read, and "Flying, ward {2}" was
not**, on 55 corpus lines.

The list reader now validates every part before building any of it: plain keywords the card
actually has, plus parts one of the costed-keyword readers accepts. A part nothing can read still
leaves the whole line unread, which is the rule the family exists to keep.


### A mana ability that was quietly using the stack

"Activate only once each turn" and "Activate only as a sorcery" open with the same three words as
"Activate only if you control a Plains", so the mana path's timing reader matched them, could not
read them as a board condition, and **refused the line**. The line then fell through to the general
activated-ability path, where "Add {C}" reads perfectly well as an ordinary effect - and the card
compiled into an ability that **goes on the stack**, which a mana ability must never do
(**CR 605.3b**) and which an opponent could respond to.

It is the worst shape of bug this project has: the card compiled, counted as covered, offered the
player a button, and did something subtly different from what it prints. **60 lines on 59 cards**
say one of these, and the ones whose mana was written as a symbol had been compiling that way for
as long as the path has existed. The ones that said "one mana of any color" failed outright, which
is the only reason it was ever looked at - a family where the *broken* half is the visible one and
the working-looking half is the wrong one.

The cap is now lifted before the timing reader sees the line, and sorcery-speed restrictions land
in the same `Timing` field they do on every other ability.

### The mass grant had one branch where the mass pump had two

"Creatures you control **get** +1/+1 until end of turn" was read; "creatures you control **gain**
vigilance until end of turn" was not. The pump matcher has two branches - a group word is required
for the singular "gets", because without one it cannot be told from "target creature gets +2/+2",
and no group word is needed for the plural "get", because "creatures you control" is already a
group by its noun. The grant matcher had only the first.

**192 corpus lines** take the second spelling, and **112** of them are the combined "get +1/+1 and
gain vigilance until end of turn" - one sentence, two continuous effects, one group. That one is
read as a single matcher rather than by splitting on "and", because the conjunction splitter would
hand the second half a sentence with no subject at all.

That single family moved coverage by **138 cards**.


### The work queue was sending you to read code that already worked

The queue ranks unread lines by how many cards each would finish, and it grouped them by a shape
truncated at 96 characters. Six different threaten effects - the plain one, the two that also
pump, the one that scries, the one about Goats - are identical for their first 96 characters, so
they showed as **one entry of eleven cards**. The plain one had compiled for months. The truncation
is now 200, and with the shapes separated the queue's real head is `//` on **94 cards**: the
two-faced wall above, and the largest single thing standing between the compiler and the corpus.

Probing before building is what caught it, and it is the cheapest habit here: five sentences
through the compiler, four seconds, against an afternoon spent reading a matcher that was right.

### A trigger now asks about the object, not about the card

Trigger predicates were `Func<GameEvent, GameState, GameObject, bool>` - no `IAbilitySource`, so
they could not call `Characteristics.Of` and every question they asked about a creature was
answered from its **printed** card. A creature granted flying was invisible to all of them, which
is the opposite of what CR 613 says.

The third parameter is now a `TriggerSource`: the object and the ability source together, as a
`readonly record struct` because a predicate runs once per event per trigger and the corpus checks
run over a million of them. It converts to `GameObject` implicitly, so the ninety-odd predicates
that only ever wanted the object read exactly as they did before - and the compiler found every
site that needed more, which is why a change this wide was safe to make at all: there is no silent
failure mode, only build errors.

What it bought immediately: **"whenever ~ blocks a creature with flying"** and the whole "becomes
blocked by a *description*" family, which could not be read at all before. The description is
parsed by the same vocabulary a target is - "a creature with flying" and "target creature with
flying" describe the same creature - rather than by a second reader written to say the same things,
and a phrase that vocabulary cannot read leaves the trigger unread instead of firing on every
block. The test is a theory whose granted case is the point: the attacker is a vanilla 2/2 with
flying from an enchantment, and only a predicate that reaches the layers sees it.

**Still printed, deliberately:** the zone-change family ("a creature you control with flying dies")
reads its subject's card, because a creature that has died is no longer on the battlefield and
there is nothing left to compute from. Doing that properly means last-known information (CR 608.2h)
and is its own pass.

### The figure counted 853 cards that had no text to read

Every transform, adventure, split, modal double-faced, prepared and flip card - **853 playable
cards** - keeps its rules text on its *faces*, with nothing at the top level. The corpus loader
read only the top level, so all 853 arrived blank, compiled with nothing unread, and were counted
as **fully understood**. They were 6.9% of the cards the figure called complete, and the compiler
had never been shown a line of any of them.

The game itself was never blind to them: `CardParser`, which builds the pool a real game is played
from, has joined the faces with a `//` for as long as it has existed. Only the instrument was, and
an instrument that leaves a field blank makes anything reading that field look implemented - which
is the same inversion the colour-identity note below turns on, and the same one the loader's own
comment about conspire warns about a few lines up from where this was.

**The `//` between the faces is left unread on purpose, and that is the engine's whole answer to
two-faced cards.** A `CardDefinition` holds one set of characteristics and one body of text, so a
transform, adventure or split card arrives with both halves' rules merged into a single spell.
Skipping the separator was the first thing tried and it is wrong: it lets such a card compile as
complete and play as one face doing the other's work. **103 cards read every other line and are
held back by that one**, which is the legality gate doing its job - a deck containing a card the
engine cannot represent is not playable, and no vanilla fallback is offered. (Nine more are
complete honestly: only one of their faces carries any text.)

Aftermath is the same wall from the other side. Its 27 lines are now visible and stay unread,
because "cast this spell only from your graveyard" is a sentence about *one half* of a split card
and the engine has no second half to cast.

**The number went down and that is the point.** 37.8% was counting blank cards; 35.3% is measuring
something real. Every corpus invariant - structural soundness, resolution wherever the subject is,
replacement on arrival - passed on all 853 the first time they were shown.


### A spell's mana value counts the X it was cast for

**CR 107.3a**: while a spell is on the stack, any X in its mana cost equals the value announced as
it was cast - and **CR 107.3g** puts X at zero in every other zone. A Fireball cast for X=5 has
mana value 6; its printed cost says 1. The cast trigger read the
printed cost, so "whenever you cast a spell with mana value 5 or greater" never fired on the
spells that shape is printed to catch, and "counter target spell with mana value 2 or less" would
have caught one.

103 cards ask about a spell's mana value and 560 carry an {X}. The symbol is counted rather than
assumed - `{X}{X}` is on 51 cards - and a *copy* of a spell is left at zero, because the copy event
carries the card and not the object, so there is nothing there to read the chosen value from.

The other places the engine asks for a mana value are left alone, and that is the rule rather than
an omission: a card in a graveyard, a card in a library, and a permanent on the battlefield all
have X at zero, and each of those readers already says so.

### A card's colour is not its colour identity

Every colour question in the engine - "whenever you cast a **red** spell", "destroy target
**black** creature", conspire's "shares a color with it", the colour the board paints a card -
was answered from `ColorIdentity`. That is the wrong list. A card's **colour** comes from its mana
cost and colour indicator (**CR 202.2**); its **colour identity** also counts every mana symbol in
its rules text (**CR 903.4**), which is a deck-building question about what it may be played
alongside.

The two disagree on **1,158 nonland cards**. Bosh, Iron Golem costs `{8}` and is colourless, but
its `{3}{R}` ability gives it a red identity, so it was a red spell. Every devoid card is the
colour it is printed *not* to be. Every Talisman is two colours it is not.

The card model had nowhere to put the right answer - it carried only `ColorIdentity`, so both
loaders wrote the identity into it and every reader downstream inherited the mistake. The fix is a
second field, `Colors`, populated from the bulk data's own `colors`, and it goes in the game log
too: a card replayed without its colours is a different card.

**What found it was not a card.** The trigger check below reports which trigger shapes no sample
event reaches, and the colour family sat near the top of that list through two rounds of filling
the sample in. The sample's spells had the right mana costs and the engine still called them
colourless - which is only possible if the engine is not reading the cost, and it is not, because
colour is its own field. A card that says the wrong thing about itself and a harness that says the
wrong thing about a card look identical until you ask which field is being read.

### A trigger can fire and still hit nobody

`Every_trigger_that_says_it_can_find_out_what_it_means` asks whether a trigger that names "that
player" or "it" can get an answer out of the event that fired it. It could only conclude anything
about the triggers some sample event reached, and **the sample was one-sided**: Alice's creature
hit Bob and Bob's hit nobody, life only ever moved one way, every spell cast was a creature cast
from a hand, and a piece of Equipment was attached to a **land**, so "whenever equipped creature
deals combat damage" could not fire by construction.

Filling that in took the check from **81 triggers to 97**, and the wider sample failed on its first
run. **Manabarbs** - "whenever a player taps a land for mana, ~ deals 1 damage to that player" -
had been dealing its damage to nobody. Mana arrives in an event that names the player whose pool
it went into, and nothing read that name, so "that player" resolved to null and the damage was
emitted against no one. The card compiled, the ability triggered, and the game was not played.

The sweep that came out of the same work is a report and not a gate, because most of what it lists
is the sample's own shortfall - "when ~ enters, if it was kicked" is unreachable because the probe
puts the permanent onto the battlefield rather than casting it. It is read by hand, which is how
the colour gap above surfaced.

### The counting vocabulary could count things but not people

`CountingAmount` turns "for each X" into a number, and every effect that takes an amount can be
counted through it. It walked the **battlefield** — so "for each creature you control" worked and
**"for each opponent" did not**, on 128 lines, with 24 more for "each player".

It is the same sentence with a different population, so the player case is answered in the same
helper rather than by teaching the permanent grammar about people. Opponents are those still in the
game (CR 102.1): a player who has lost is no longer one, and a count that included them would keep
paying for somebody who left.

**The callers disagreed about the phrase.** Some patterns capture "for each X" and hand over
"each X"; others strip the words and hand over "X". Both arrive at the same helper, which is why
"you gain 2 life for each opponent" started working and "draw a card for each opponent" did not —
the same fix, seen twice, because the two readers spell the same thing differently. The
normalisation went into the helper rather than into either caller: a caller that has to know which
spelling this wants is a caller that will sometimes get it wrong, and one of them already had.

The test is a theory over "each opponent" and "each player" in a two-player game — 22 life against
24. They are different numbers, and a reader that answered the same for both would pass whichever
was written first.

### The other half of the library had never been run

The resolution invariant covers **effects**. Replacements are the other half and nothing exercised
them — the same gap that let a hardcoded zone sit inside an effect for months, and the harder half
to notice, because a replacement that declines to apply looks exactly like a card with nothing to
say. So there is now a check that offers every compiled replacement the arrival it was written
about, and pushes what it returns through the reducer. **1,108 replacements, 990 of which apply.**

It failed on its first run with six cards in three shapes, all saying `No object … in the game`, and
all six were **the check's own fault**. That took verifying rather than assuming: the failing cards
all had *two* replacements, so the decisive test was whether the real path worked, and it does —

```
CREATE OK
PENDING CHOICE: replace:2:ObjectCreated
ANSWERED FIRST OPTION
ARRIVED: tapped=True counters=1
```

A card with two arrival replacements does not simply arrive. **CR 616.1** gives its controller the
choice of which applies first, and the engine raises that question *before* the permanent exists —
which is right, and which the harness never answered. It then looked up an object that was not there
yet and reported six correct cards as broken. Answering the choice is part of putting a card onto
the battlefield, and the harness does it now.

That is the **third** time this stretch that a finding turned out to be an invention of the check:
156 target mismatches, one made-up trigger sentence, and now six replacement failures. All three
were settled the same way — by asking what the engine or the corpus actually does rather than
believing the report. A check that can fail wrongly is still worth having; one whose failures are
believed without testing is not.

The result underneath is worth stating on its own: the engine handles a subtle ordering rule
correctly, and now that has been seen end to end rather than assumed from the code.

### Three sweeps, all clean — and what that says about the remaining 62%

With the X family finished, the same probing method was turned on the vocabularies around it:

- **Twelve X-shaped effects** — draw, life gain, damage to a target, damage to each creature,
  counters, tokens, mill, scry, a targeted draw, a pump, an exile from the top. **All twelve read.**
- **Eighteen trigger conditions** — enters, dies, attacks, becomes tapped, a spell cast, a *second*
  spell each turn, a creature dying, another creature entering, a card drawn, a *second* card drawn,
  an opponent casting, life gained, life lost, a land entering, damage taken, a sacrifice, an
  opponent drawing. **Seventeen read**, and the eighteenth was a sentence I made up.
- **The four real cast-from-a-zone wordings** — graveyard, exile, "anywhere other than your hand",
  and the bare form. **All four read.**

The made-up one is worth recording. "Whenever a player casts a spell from their graveyard" failed,
and it would have gone on a list of gaps — except that the phrase appears **zero times** in the
corpus. Checking what cards actually say turned one apparent gap into none, which is the second time
this file has had to record that a finding was an invention of the check rather than a fault in the
engine.

So the shape of the remaining work is now measured rather than guessed. **It is not the vocabulary.**
Effects, conditions, triggers and amounts all read the ordinary forms of ordinary cards. What is left
is two things: keywords that need a mechanic the engine has never had — phasing, the Ring, learning
from outside the game, cipher, encore — and card text that is genuinely unique, which the work queue
has been saying all along in a way that was easy to read past: 15,000 cards sit one line short across
**14,000 distinct lines**.

### A pump with no size until it resolves

The one shape the round before could not take: `+X/+X`. Every other pump **names** a continuous
effect that was written when the card compiled, because the size is printed on the card — the id
literally contains the numbers. "+X/+X" has no size until X does.

So the definition is built when the ability resolves and handed over by the same event. The layer
machinery is untouched and never learns that a pump can be variable; it receives a definition like
any other, just one that did not exist a moment earlier.

Power and toughness are read as **two amounts, not one**, because the cards separate them: `+X/+0`
is on 129 lines against 132 for `+X/+X`, and reading the same X twice is a coincidence of wording
rather than a rule. Between them, the five spellings of a variable pump are 377 corpus lines.

`-X/-X` falls out of the same reader, and it is the reason the negation fix below mattered before
this could work: a minus is a negation of the **amount**, not a negative X. The test is a theory
over both directions against a **3/3** — five and one, both away from the printed size and from each
other, which a 0/0 or a symmetric bonus could not distinguish.

It composes with the clause from the round before without either knowing about the other: "target
creature gets +X/+X until end of turn, where X is the number of Elves you control" reads, because
one supplies X and the other spends it.

### Negating X did nothing, and eighty-two cards gave life away

Building the clause below turned up a bug that had nothing to do with it. `Amount` is negated to
write life loss — `-amount` — and the operator did it by flipping the **fixed** part:

```csharp
public static Amount operator -(Amount amount) => amount with { Fixed = -amount.Fixed };
```

That works exactly when the fixed part *is* the amount. It is not when X was chosen by the caster:
X's fixed part is zero, so negating it produced zero and left the variable flag untouched. **"Each
opponent loses X life" resolved to each opponent gaining X life.** Eighty-two lines in the corpus
are written that way. Nothing failed, nothing went unread, and the coverage number never moved.

The negation is a flag now, applied to whatever the amount comes to rather than to one part of how
it is written — so it works for a fixed number, for a chosen X, and for a count alike. The test
casts an ordinary `{X}{B}` drain with **no counting involved at all**, which is how it is known to
be a pre-existing bug rather than something the new clause introduced; reverting the operator turns
that test red along with the two below.

### "..., where X is the number of ..."

658 corpus lines define X rather than asking for it, and none of them could be read. The clause is a
**definition** — it says nothing about what happens, only what X comes to — so the head is parsed by
the ordinary vocabulary with X left as the variable it always is, and the answer is filled in around
it when the sentence resolves.

That is why it is a wrapper and not a rewrite. The alternative is walking the parsed effects and
replacing every variable amount inside them, which means knowing the shape of each one — and there
are dozens, some nested. The wrapper knows nothing about them: it sets X and resolves what it holds.
Draws, life gain, life loss and damage all started working from the one change, and none of their
readers learned that X can be a count.

Targets are parsed into the caller's list rather than a scratch one, so the indices the head's
effects were numbered against stay the indices they are read at. And the clause is read **before**
the ordinary matchers, because the head on its own is a sentence they would take — with X meaning
"a number the caster chose", which is a different card.

### The check had the same maintained list the code did

Adding the wrapper broke `Every_compiled_card_is_structurally_sound`, and the accusation was
specific and wrong: **eighteen sound cards were reported as choosing a target and never using it.**

The invariant walks each card's effect tree to see which target indices are actually read, and it
walked it with a **hand-written switch** over the wrapper types — `MayPay`, `OnlyIf`, `OnAttached`,
`IfKicked`. A fifth wrapper was invisible to it: its children were never entered, so their targets
looked unused.

The engine already has a flattener that finds nested effects by reflecting over properties, and the
test now uses it instead of a copy. It is the same lesson as the spell-definition guard a dozen
rounds ago, applied to the checks rather than to the code they check: **a list somebody has to
remember to extend is a list that will be wrong.** It cost one failing run to find here, which is
what a gate is for — the equivalent mistake inside the compiler cost a silently dropped cost.

### Where the effect vocabulary actually ends

Sixteen ordinary effect sentences were probed — draw-then-discard, a drain, reanimation, a token
with a keyword, a bounce to the top of a library, scry-then-draw, mill, a mass untap, a mass tap, a
board wipe, an edict, a counterspell with a payment clause, a pump-and-grant, a flicker, and a
destroy with a rider. **All sixteen compiled.** The common vocabulary is not where the missing 62%
is, and knowing that is worth more than another guess at it.

Pushing further out found the edge. Of fourteen harder shapes, nine worked and five did not — and
three of the five were one cause: **a count used as a number**.

The machinery for that already existed. `Amount` has carried a `Counter` for "for each" since
before this stretch, and `CountingAmount` turns a group phrase into one through the same grammar the
sweepers use. Draws, life gain, life loss, tokens and counters all take it. **Damage did not** — 117
lines in the corpus say "deals damage equal to the number of ...", and the damage reader only knew
the other word order, "deals N damage to X for each Y".

So it is one more spelling of a sentence the engine already understood, not a new capability: the
counted group is handed to the same grammar with the word "each" put back, rather than the grammar
being taught a second way to say it.

The test changes nothing about the board between compiling and resolving — it sets up three
creatures and shoots a 3/3, where three damage is exactly lethal, so a count that came out one high
or one low is visible either way round.

Two gaps are recorded rather than built: **"where X is the number of ..."** (658 corpus lines) is a
definitional clause that needs the variable to be computed rather than chosen, which is a property
of the ability rather than of the sentence; and a **targeted** draw counted the same way still reads
only the untargeted form.

### Sweeping the condition vocabulary instead of guessing at it

Having found one missing condition by accident, the cheap move was to ask fourteen at once rather
than wait to trip over the next. Ten of the fourteen already worked — tapped, untapped, enchanted,
"is a creature", "you control another Elf", "you control no creatures", "an opponent controls no
creatures", a graveyard count, an empty hand of your own. **That is the useful half of the result**:
the vocabulary is broader than it looked, and three rounds of guessing would have re-derived it one
sentence at a time.

Four were missing, and two of them were a word:

- **"It's face down"** — one more word in the adjective list. Asked of the permanent rather than of
  the computed card, because a face-down permanent has no abilities and no printed characteristics
  to compute from (CR 707.2); being face down is a fact about the object.
- **"An opponent has no cards in hand"** — the reader knew the question and only ever asked it of
  the controller. "An opponent" means any one of them (CR 102.1), which is a real disjunction in a
  multiplayer game rather than a synonym for "the other player".

The test for the second empties **Alice's own hand first** and asserts nothing happens, then Bob's.
A reader that asked the controller instead of the opponent is right exactly half the time, and a
test that only emptied the opponent's hand could not tell the difference.

**One card completed**, and that is the honest measure of a
sweep: most of what it found was already there. The value is in no longer wondering — the
next person writing a condition can read the ten that work instead of guessing at them and
adding an eleventh reader for a question already answered.

Two remain and are recorded rather than half-built: "you have more cards in hand than each opponent"
is a comparison rather than a threshold, and "it entered this turn" needs a per-object fact the
engine does not keep.

### A permanent could not ask what counters it had

`BoardConditions` could tell whether the source was monstrous, attacking, blocking, equipped or
enchanted — and could not tell whether it had a counter on it. So "as long as it has a +1/+1
counter on it" failed, and so did the same clause on a keyword grant, on a pump, and on a named
counter nobody else uses:

```
XX  ~ has indestructible as long as it has a divinity counter on it.
XX  ~ has indestructible as long as it has a +1/+1 counter on it.
XX  ~ has flying as long as it has a +1/+1 counter on it.
XX  ~ gets +2/+2 as long as it has a +1/+1 counter on it.
```

**One reader fixed all four**, because they are one condition wearing four sentences. That is what
the shared condition vocabulary is for, and it is the second time in three rounds that the cheapest
fix has been a missing *question* rather than a missing template.

It is a separate reader from the adjectives beside it rather than another word in their list,
because "is monstrous" and "has a counter" are different sentences and the counter has a **name**
that has to be carried through rather than matched against a fixed set. The name is normalised the
way the engine stores it when a counter is put on — lowercase for a named counter, printed as-is for
the two written as numbers — so a reader that lowercased "+1/+1" would go looking for a counter
nothing ever adds.

The counter name is a single word by construction. Admitting a phrase there would read "a +1/+1
counter on target creature" as a counter called "on target creature", which is the same mistake the
noun-phrase class in the target grammar already exists to stop.

The test asserts the static is **off with a counter of a different name** — a reader that asked
"any counter at all" passes every other assertion in it — and that it turns **off again** when the
counter is removed, because a static is not a one-way switch. A second test drives a pump from the
same clause, since one reader serves both and a test on only one of them would not say so.

### Multikicker, and a count that had to survive the card

Kicker was a **flag**, and every card reading it asks a yes-or-no question. Multikicker is the same
additional cost paid any number of times (CR 702.33c), and the cards that care ask "for each time it
was kicked" — a number. Collapsing the two would have made every existing kicker reader count to one
and back, so `TimesKicked` sits beside `WasKicked` rather than replacing it, and a multikicked spell
emits **both** events: the flag the old cards read and the number the new ones need.

`SpellMultikicked` is a new event rather than a count added to `SpellKicked`, because that one is in
every log this engine has ever written and changing its shape would make the old ones unreadable.

The count has to reach the permanent, and that is the same rule the once-only version already leant
on: the spell that was kicked and the permanent that arrives are different objects (CR 400.7), and
CR 607.2 is what carries the fact across. So the count rides the zone change exactly as the flag
does.

The test is a theory over zero kicks and three, and it asserts the **mana pool is empty** as well as
the counters. Exactly the mana needed is put out — {G} plus {1} per kick — so a multikicker that
charged once would leave mana behind and fail, and one that charged correctly but lost the count
would fail on the counters. Either half alone passes for the wrong implementation.

**A stale edit is worth recording**, because it shows an earlier fix working. Two of the changes in
this round were written against the old hand-maintained "does this card have anything spell-level"
guard, and one of them — `&& multikicker is null` — had nowhere to go: that list was replaced a few
rounds ago by a reflective check that reads the record's own properties. The new field was covered
the moment it existed, with no edit to make and none to forget.

### One word, two abilities — and the rulebook is why

The engine's first **player designation**: the city's blessing. Everything the engine had tracked
about a player until now was a number or a per-turn flag; this is a thing a player *is*, kept for
the rest of the game.

Ascend is the interesting half, because **the same printed word compiles to two different
abilities** and only the rulebook says so. CR 702.131a: on an instant or sorcery it is a *spell
ability*, resolving once with the rest of the spell. CR 702.131b: on a permanent it is a *static
ability* that applies "any time" the count is met.

Reading it as one thing would have left half the cards wrong, and the wrong half would have been
silent — an enchantment that only checked as it entered would never notice the tenth permanent
arriving afterwards. The test is built around exactly that: the beacon comes down with nine
permanents out, and the tenth arrives later. A card that looked once passes nothing.

The permanent form is a **state trigger** (CR 603.8) — it answers to no event and is checked
whenever state-based actions are, which is what "any time" asks for. That shape needs the effect to
decline when the blessing is already held, because the condition it watches stays true forever once
ten permanents are out. The rule already says "and you don't have the city's blessing"; without it
the trigger would fire every time anybody got priority.

Two more details the rules decide rather than the card:

- **Permanents, not creatures and not cards** — lands and the enchantment asking the question are
  all in the count, which is what makes ten reachable.
- **Kept when the board shrinks.** A player who had ten permanents and lost nine still has it, so
  the condition asks the designation and never re-counts. The test sacrifices a land afterwards and
  asserts the blessing survives; a condition implemented as "do you control ten permanents" passes
  every other assertion and fails that one.

### One fact the engine never wrote down

Twenty-four cards ask "if you dealt combat damage to a player this turn", and the engine had no
answer. Not a missing template — a missing *fact*: nothing recorded that a creature had connected.

The probe is what made the size of it visible. The condition failed in **all three** places a
condition can be asked from — a static's "as long as", a trigger's intervening if, and an activation
restriction — because all three bottom out in the same reader. One entry there answers all of them,
which is the whole reason conditions live in one place rather than beside the sentences that use
them.

**It completed no cards on its own**, and the number says so: 12,303 before and after.
All twenty-four carry other lines the compiler still cannot read, so the condition being
answerable is necessary and not yet sufficient. Recorded that way rather than as a win,
because a fact the engine can now answer is worth having whether or not this week's
coverage number moves — and because the alternative is quietly implying it did.

Two decisions about where to keep it:

- **On the player, not on the creature.** The question outlives the creature: "you dealt combat
  damage to a player this turn" is still true after the creature that did it has died, and a flag
  on a permanent goes to the graveyard with it.
- **Read off the source, not assumed to be the active player.** A creature can deal combat damage
  on somebody else's turn, and the two differ exactly when it matters.

Combat damage only. A burn spell to the face is damage this player dealt, and is not what any of
the twenty-four cards mean — so the condition is recorded from the combat flag the damage event
already carries rather than from the damage itself.

The test asserts the flag is **absent before combat**, present after, **absent on the player who was
hit**, unset by non-combat damage, and cleared when the turn rolls over. A flag set on the wrong
side of the attack passes every one of those but the third.

### Increment, and a clause the card does not print

The reminder text says "whenever you cast a spell, if the amount of mana you spent is greater than
this creature's power or toughness, put a +1/+1 counter on this creature." **CR 702.191a says
something slightly longer**: "whenever you cast a spell, *if this permanent is a creature* and the
amount of mana spent...".

A permanent with increment that has stopped being a creature does not trigger, and nothing on the
card says so. Reading the card alone would have produced something that works on every printing and
is wrong the moment an opponent turns it off — which is the class of error the rulebook is in this
repo to prevent, and the third time this stretch that looking it up has changed the implementation.

Two other details are decided rather than assumed:

- **"Power or toughness" is two comparisons, not one.** A 4/1 increments off two mana because two
  beats its toughness, though it is nowhere near its power. Both are the computed values, so a
  creature pumped in response is measured at the size it is when the trigger looks. The test is a
  theory over a 4/1 and a 4/4 against the same two-mana spell, so an implementation comparing only
  power fails on the first case and one comparing only toughness fails on the second.
- **The mana spent is read off the spell, not counted from its cost.** They are different numbers:
  a cost reduction, an alternative cost or a convoked permanent all make the mana actually spent
  smaller than the mana printed. The engine already records what was spent on the spell object, and
  it is recorded before the cast event that this trigger watches — so the number is there to be read
  at the moment the question is asked.

### A negative result worth the same as a find

A sixth situation was added: targets chosen, and the controller with **no hand and no library**. An
empty library is a real state a player survives until they next have to draw (CR 104.3c), and an
empty hand is ordinary by the end of most turns — so an effect that reaches for a card and assumes
one is there would be a bug of the same family as the five above, just about a resource rather than
a zone.

**It found nothing.** 113,490 effect runs, clean.

That is worth recording rather than quietly dropping, for two reasons. It says the effect library
already handles a missing resource — an answer nobody had, and one that would otherwise be guessed
at every time somebody wrote a draw or a discard. And it is the evidence that the five finds above
were about a real weakness rather than about the check being easy to fail: the same harness, pointed
at a different assumption, came back empty.

The run count is how the situation was confirmed to have happened at all. 94,575 became 113,490,
which is exactly one more situation's worth — a check that had silently skipped its new case would
have reported the old number and looked just as green.

### CR 608.2b, asked of the whole effect library at once

Three effects had been found assuming a zone rather than asking one, and the temptation was to read
the other twenty-one hardcoded moves and judge each. That is the kind of audit that misses one.

So the invariant asks all of them together. A fifth situation puts every target somewhere its spec
did not expect — a permanent target that has gone to a graveyard, a graveyard target that is on the
battlefield — which is not an invented situation at all: **CR 608.2b** says a target that has stopped
being legal is skipped and the spell does as much as it can with the rest. A creature killed or
bounced in response to a spell is exactly that, and the effect has to do nothing rather than throw.

It found **465 more, in two shapes**:

- `ReturnToHand` described a move out of the battlefield without checking the creature was still
  there — the same hardcoded origin as the exile effect, on a far commoner card.
- `DealDamage` marked damage on a permanent without checking it was one. Damage is dealt to a
  permanent and a permanent is on the battlefield (CR 120.1); the engine's own damage record refuses
  anything else, so it had to be asked in the effect rather than discovered in the reducer.

That is **five instances of one pattern** now — `ExileTarget`, `PutCounters`, `CounterTargetSpell`,
`ReturnToHand`, `DealDamage` — and none of them was found by reading code. Each fell out of putting
the effect in a situation the rules permit and the author had not pictured.

**94,575 effect runs across 32,765 cards, clean.** The count is the point: the same six static
invariants that had passed for months say nothing about any of this, because they ask what a card is
rather than what happens when it resolves.

### The check reported its own inventions, and had to be told not to

The resolution invariant was widened from two situations to four — subject on the battlefield,
subject in a graveyard, no subject at all, and **targets chosen**. That last one matters most: most
of the effect library takes a target and returns nothing without one, so the first three never reach
past the opening guard.

The first version of it invented targets: a list alternating permanents and players, four long, on
the theory that this would satisfy whatever index an effect was compiled with. It reported **156
failures in two shapes** — a draw and a life change, each reading a permanent where the card had
declared a player.

**None of it was real.** A sweep of the whole corpus for a draw or life effect whose target index
names a non-player found **zero**. Every one of those failures was about a game the engine cannot
produce, and "fixing" them would have added guards against nothing to two effects that were already
correct.

So the targets are now built from **the card's own declared specs**, one for one. An effect's target
index means nothing without the list it was numbered against, and a check that supplies targets of
its own choosing is testing a card nobody wrote. The lesson is the same one this file keeps
recording from the other side: a finding is not a bug until something reachable produces it.

### One real fault survived the correction

`CounterTargetSpell` had the same hardcoded origin the exile effect had: it described a move out of
the **stack** without checking the spell was still there. A target can stop being legal between
being chosen and the effect resolving (CR 608.2b) — something else countered it, or it resolved —
and the reducer then refused the move rather than the effect doing nothing. Countering means moving
a spell from the stack to its owner's graveyard (CR 701.5a), so with no spell on the stack there is
nothing to move.

That is the third instance of one pattern: an effect that holds the object and assumes the zone
instead of asking it. `ExileTarget`, `PutCounters`, and now this. All three were invisible to six
static invariants and all three fell out of one that resolves.

**75,660 effect runs across 32,765 cards, and the suite is clean.**

### The invariants never ran anything, and 746 effects were waiting

`CardCompilerInvariantTests` had six checks and every one of them was **static**: they read the
compiled shape and never resolved it. That is the gap the exile bug fell through the round before —
it compiled perfectly and threw on resolution, and was found by luck rather than by a gate.

So there is a seventh now, and it runs them. Every compiled effect on every card is resolved twice
against a fresh game — once with its subject on the battlefield, once with the same card in a
graveyard — and the events it produces are pushed through the reducer, because that is where the
failure surfaced rather than in the effect itself. Effects that need targets return nothing and pass
trivially; the point is the subject path, which nothing else covered.

**It failed on its first run, with 746 cases in one shape.**

`PutCounters` did not check where its subject was. Counters live on permanents and the engine's own
reducer says so — it refuses a counter on anything not on the battlefield — so
"whenever a creature you control dies, put a +1/+1 counter on it" resolved with "it" meaning the card
now in the graveyard (CR 400.7) and threw instead of doing nothing. Seven hundred and forty-six
compiled effects were one dies-trigger away from that crash, and the twenty-seven `OnlyIf` failures
beside them were the same effects seen through a wrapper.

The number is the argument for the check. Six static invariants over 32,765 cards had found nothing
here, because the question they ask is what a card *is* rather than what it *does*. One run of the
seventh covered **37,830 effect resolutions** and found a fault reachable from any of hundreds of
ordinary cards.

### Measured and declined: the shocklands

Ten cards, and the blocker is structural rather than large. "As ~ enters, you may pay 2 life. If you
don't, it enters tapped" needs a player's decision *inside* a replacement effect, and `Replace` is a
delegate returning events synchronously — it has nowhere to stop and ask. The engine's choice system
raises a pending question and resumes afterwards, which is a different shape from a replacement that
must answer immediately. `ChoiceOnEntry` looks like the hook and is not: it names a colour or a
creature type as a permanent enters, not a payment. Modelling it as "always enters tapped, then
untap" is a different card - it would set off every enters-tapped watcher.

### Swept and clean: hardcoded origin zones

After the exile fix, every other hardcoded `Zone.Battlefield` origin in the effect library was
checked. All eight are reached only through a permanent target or through the source itself, neither
of which can be anywhere else. `TapTarget` turned out to be safe by accident rather than by design -
a graveyard card has no permanent state, so its tapped check already declines. The class is closed.

### The allow-list earns its keep, one verb at a time

With the pronoun mechanism built, the zone-change family was the obvious next entry — and it is the
case that shows why the list is per **verb** and not per family. Most of those verbs name an event
carrying exactly one object: the permanent that entered, the card that reached the graveyard, the
permanent that tapped. But `blocks` and `becomes blocked` sit in the same pattern and name nothing,
and admitting the family wholesale would have compiled "whenever ~ blocks a creature, destroy that
creature" into a trigger that silently does nothing — the exact card this mechanism exists to keep
failing. Both are named in the switch and refused explicitly, so a verb added to the pattern later
has to be considered rather than quietly admitted.

### And then the pronoun pointed somewhere the effects had never been

Compiling was not the end of it. "Whenever a creature an opponent controls dies, exile that
creature" compiled, and then threw:

```
d7a81037 is in Graveyard, but the move says it is leaving Battlefield.
```

`ExileTarget` had the origin zone **hardcoded to the battlefield**. It looks the object up, so it
always had the real answer to hand; it just never asked. That was invisible for as long as every
pronoun it could see pointed at a permanent, and a dies trigger's "that creature" is the card now in
the graveyard (CR 400.7). It now moves the object from wherever it actually is.

`DestroyTarget` had the mirror of the same problem and needed the opposite fix: destroy applies to a
permanent, and a permanent is on the battlefield (CR 701.7a), so an object that has already left is
now left alone instead of being moved a second time.

Neither would have been found by the compile probe. The probe said "ok" for all six sentences; the
game said otherwise on the first one it actually played. That is the difference this file keeps
insisting on, and it caught a hardcoded zone that predates any of this work.

### "That creature", on the second attempt

Reading "that creature" as the triggering event's subject was tried once and reverted, and the
comment left behind said why: the ordinary trigger subject **falls back** to the permanent with the
ability when the event was about no object, so "whenever ~ blocks a creature, destroy that creature"
destroyed the blocker. A verb that destroys the wrong permanent is worse than one that is not read
at all, and the line was left unread instead.

That reasoning was right about the danger and wrong about the fix being impossible. The failure had
two independent causes, and both have to be removed:

1. **The subject fell back.** So there is now a second subject kind, `TriggeringObject`, which
   resolves to the event's object *or to nothing*. It cannot reach the source, ever.
2. **The parser could not tell which triggers supply one.** So it is told. `TryParse` takes a flag,
   `TriggerConditions.NamesAnObject` answers it from an **allow-list** of condition shapes, and the
   default is the safe one. A closure cannot be interrogated, so the question is asked of the
   condition text before it becomes one.

Either alone is still the bug. The strict subject without the flag compiles sentences that silently
do nothing; the flag without the strict subject is the original revert. The allow-list has to stay
an allow-list for the same reason — every shape named in it must be one `SubjectObjectOf` genuinely
answers, and adding a shape means checking both ends.

`SubjectObjectOf` gained `DamageMarked`, which it had deliberately refused on the grounds that
damage between two creatures has two objects in it and "it" cannot choose. **The corpus settles
that**: thirty-five cards of this shape say "that creature" and mean the damaged one, and every card
that means the source names it outright. Where the damaged permanent *is* the source — "whenever ~
is dealt damage" — this answers the same id the fallback used to, so nothing that worked before
changes.

Three probes hold the shape of it: the damage triggers now read, "whenever ~ blocks a creature,
destroy that creature" is **still unread**, and a bare "destroy that creature" with no trigger at all
is still unread. The second is the important one — it is the exact card that caused the revert, and
it has to keep failing.

The behavioural test attacks with a 1/4 into a 2/3 blocker, so one point of combat damage is nowhere
near lethal and only the trigger can kill it, and then asserts the *source* survived. Both creatures
are on the battlefield and both are creatures; an implementation that picked the source would look
correct until somebody checked which one died.

### The word "combat" was read, stored, and then never asked

`DealsDamageTo` reads "whenever ~ deals **combat** damage to X" and keeps whether the card said
"combat". Three arms use it: a player, an opponent, and a creature. The first two check it. **The
third did not.**

So every trigger of the shape "whenever this creature deals combat damage to a creature" fired on
damage that was not combat damage at all — a ping, a fight, a damage-based removal spell, anything
this permanent happened to damage. Thirty-five cards in the corpus carry that wording. Nothing
failed, nothing was unread, and coverage never moved; the trigger simply went off too often, which
is the failure mode this file keeps finding and the reason it keeps looking.

The tell was visible in the code itself: two arms beside each other asking a question the third
ignored, with the answer already in a local variable. It was found by probing the *neighbours* of a
blocked line rather than the blocked line itself — "deals combat damage to a creature, draw a card"
compiles and always did, which is exactly why nobody looked at it.

The test is a theory over both wordings plus a separate combat game, and the third case is not
decoration: without it, an arm made permanently false would pass every assertion in the theory. The
fix was reverted to watch the suite go red before it was kept.

### Measured and declined: protection from a tribe

Protection is a `KeywordAbility` flag, one bit per colour plus artifacts. "Protection from Goblins"
needs a *quality* carried beside the flag, and every place protection is honoured — targeting,
damage, enchanting, blocking, CR 702.16's four — would have to ask a description instead of a bit.
Twenty-two lines in the corpus, two cards completed. The change is worth doing when something else
needs the same shape; on its own it is a lot of surface for two cards.

### Two hundred lines lost to four words

"Enters the battlefield" and "enters" are the same event written two ways. Wizards' 2024 templating
shortened the first to the second, the comprehensive rules still use both interchangeably, and every
template in this compiler is written in the newer form. The corpus carries **203 lines in the older
one**, and not one of them was read — while the identical card written the modern way compiled fine.

**It bought seven lines and no cards.** That is the honest number and worth recording,
because 203 looked like the answer and was not: a phrase count is not a blocked-line count.
Nearly all of those lines fail on something else in the same sentence - the older phrasing
clusters on digital-only cards whose other words ("conjure", "perpetually") the compiler
cannot read either. Fixing the four words at the front of a sentence does nothing for a
sentence that also ends badly. The change is still right: it removes a reason to fail that
had nothing to do with the card.

Normalised in `Lines()`, beside "this creature" becoming "~" and the typographic quotes: one
template should not have to be written twice. The rewrite is deliberately narrow — "leaves the
battlefield" and "put onto the battlefield" are different events and are untouched because neither
contains the phrase.

One reader already depended on the long form: `EntersUnderYourControl` rewrites "a creature enters
the battlefield under your control" into "a creature you control enters", and normalising upstream
would have left it matching nothing. It now accepts both spellings, because it is also called with
hand-written conditions that never pass through `Lines()`.

### The backspace, again — and the check that ends it

Writing that regex reproduced a bug this file already records. `` written through a tool that
treats backslash escapes as its own becomes a **literal backspace**, so the pattern reads
`<BS>enters the battlefield<BS>`: valid, compiles clean, matches nothing, ever. The first time it
cost a matcher that looked correct in every review. This time it was caught in one probe, because
the bytes were checked — but "check the bytes" is an instruction, and instructions get skipped.

So it is a test now. `No_compiled_card_pattern_contains_a_control_character` reflects over the
`GeneratedRegex` attributes on the four card-compiler types and fails on any pattern containing a
control character, naming the method and the codepoint. It scans **318 patterns**, with the floor
set at 300 so the check going quiet is a failure.

Both halves were verified rather than assumed: the floor was temporarily raised to reveal the real
count, and a backspace was deliberately re-injected to watch the test go red and print
`has U+0008 in its pattern: <BS>enters the battlefield`. A guard nobody has seen fail is a guard
nobody knows works.

### A lord that asks for a counter, and the probe that found the seam

The keyword ranking has been the work queue's head for several rounds, and it is not the whole
queue. Reading the *sentence* ranking with the keywords set aside turned up
"Each creature you control with a +1/+1 counter on it has trample" on six cards — and probing it
against four neighbours located the seam exactly:

```
XX  Each creature you control with a +1/+1 counter on it has trample.
ok  Each creature you control with flying has trample.
ok  Each creature you control has trample.
```

The lord's qualifier slot existed and worked; it just only spoke keywords. `[a-z ]+?` cannot match
`+1/+1`, so the counter form fell off the end of a pattern that was otherwise ready for it. That is
the kind of gap worth finding by probing neighbours rather than by reading the regex: the failing
line and the working line differ by four words, and the four words say which.

The counter is asked of the **permanent**, not of the computed card, and the distinction is real:
an effect can give a creature flying, and nothing gives it a counter except something that put one
there. So the keyword filter reads `target.Keywords` (computed, layer-aware) and the counter filter
reads `target.Subject.Permanent.Counters` (stored, because that is where counters live).

The test puts the creature on the battlefield **before** the counter and asserts it has no trample,
then adds the counter and asserts it does. Asserting only the second half passes for a lord that
grants trample to everything it can see.

The counter also joins the effect's id, so a card with two of these lines produces two distinct
continuous effects rather than one shadowing the other.

### An ability that correctly does nothing, and the one beside it that does not

Partner is the first line here whose right implementation is **no behaviour at all**. CR 702.124a
is explicit: partner abilities "modify the rules for deck construction in the Commander variant ...
and they function before the game begins." A card whose only text is "Partner" is fully implemented
for play the moment the line is read. Giving it a trigger or a static would be inventing an ability
the card does not have.

But it is not read and thrown away. `CompiledCard.PartnerRule` keeps the printed line, because
reading something and discarding what it said is how a fact goes missing without anybody noticing —
and deck construction is going to want it. The field exists so the next person looking for it finds
it rather than re-deriving it from oracle text.

**"Partner with [name]" is deliberately excluded**, and that distinction is the whole reason this
needed the rulebook rather than a guess. CR 702.124j makes it *two* abilities: the deck-construction
permission, and "when this permanent enters, target player may search their library for a card named
[name], reveal it, put it into their hand, then shuffle." A matcher reading "Partner" followed by
anything would have swept those cards up and compiled them complete with their tutor silently gone —
the same failure the rest of this file keeps recording. The em dash is what separates
"Partner—Friends forever" from "Partner with Alisaie Leveilleur", so the em dash is in the pattern,
and a test asserts the named form stays **unread**.

The corpus carries 147 bare "Partner" lines, 19 "Partner—[text]", 32 "Choose a Background" and 27
"Doctor's companion" — all four inert, all four now recorded.

### Undaunted

One line, using the cost-reduction hook that affinity already uses. Worth one detail: the rule says
"for each opponent **you have**", so it counts players rather than seats and skips anyone who has
lost. Only a multiplayer game can show the difference; in a two-player game the discount is {1}
until the game ends. The test gives exactly the reduced cost and nothing more, so a reduction that
did not happen fails to pay.

### Measured and declined: provoke

Five cards. The engine has block requirements in exactly one shape — `MustBeBlockedByAll`, the lure
— and provoke is the other shape: *one named creature* must untap and block *this* attacker. That
needs a per-creature requirement with a duration, and the existing check returns the first unmet
requirement rather than maximising how many are met (CR 509.1c). Adding a second kind of requirement
on top of an approximation of the rule that combines them is how a combat step starts refusing legal
blocks.

### The guard now derives from the type, and a test holds it there

The fix in the entry below — adding the two missing terms — left the hazard exactly where it was.
The list was still hand-written, and the next field would be lost the same way.

So the question is no longer asked as a list. The definition is built unconditionally and then
asked whether it carries anything, by comparing each of its properties against a fresh one via
reflection. A property added to the record is included the moment it exists, and there is no edit
anybody can forget to make. Collections are compared by length rather than by value, because two
distinct empty `ImmutableList`s are not equal to each other and every definition builds its own.

Two things make this safe to claim rather than hope:

- **Coverage did not move.** 12,255 cards and 31,879 lines before the change and after it, so the
  reflective answer agrees with the hand-written one everywhere the hand-written one was right.
  A restructure of the thing that decides whether a card has a spell at all could have shifted
  thousands of cards quietly; measuring is the only way to know it did not.
- **A test drives the same reflection from the other side**: for every property a value can be
  invented for, set it alone and assert the definition no longer counts as empty. Twenty-seven
  fields are exercised, with the floor set at twenty-five so that checks disappearing is a
  failure rather than a silence.

### A guard that drops a cost without saying so

`CompiledCard.Spell` is built only when the card has *something* spell-level to put in it, and that
question is asked as a hand-written list of every field: `dash is null && evoke is null && ...`,
thirty terms long. A field missing from that list is **not a compile error**. It is a card that
reads fine, reports itself complete, and quietly has no spell at all.

Blitz was written and lost exactly that way. The line was read, the death trigger was added, the
compiler reported `IsComplete`, and casting the card came back "has no blitz" — because the cost had
been assigned to a `SpellDefinition` that was never constructed. It took a probe printing
`compiled.Spell is null` to see it; every visible signal said the work was done.

**The surge and spectacle cost from an earlier round had the same hole**, and it was invisible
because every real card carrying one of those also has effect text, which trips a different term in
the same list. A card whose only spell-level fact was its conditional cost would have dropped it
silently. Both are now in the guard, with a comment on it saying what the list is and that nothing
enforces it.

This is the same failure shape as the life-loss bug a few rounds earlier: nothing unread, nothing
thrown, coverage unmoved, and the card wrong. The lesson that keeps repeating is that "the compiler
accepted it" and "the engine can play it" are different claims, and only the second one needs a game
to check.

### Blitz: three abilities, and only one of them belongs to the spell

CR 702.152a spells blitz out as three: an alternative cost, a delayed trigger that sacrifices the
permanent at the next end step, and — the one that decided the design — *a static ability that
functions while the permanent is on the battlefield*: "as long as this permanent's blitz cost was
paid, it has haste and 'when this permanent is put into a graveyard from the battlefield, draw a
card'."

That last clause is why the fact has to outlive the spell. A permanent is a new object (CR 400.7),
so the spell knowing it was blitzed is no help to the creature that dies four turns later. The flag
is therefore set twice — once on the spell, once again on the permanent it becomes — by emitting the
event a second time against the new id. Copying the flag through the zone change would say the same
thing quietly; emitting it puts it in the log, where a replay can see it.

The draw is a real triggered ability on the card rather than something granted, because the engine
cannot grant triggered abilities and because it genuinely is one — it uses the stack and can be
responded to. It asks `source.WasBlitzed`, which works because a leaves-the-battlefield trigger is
considered against the state *before* the event, where the permanent still exists and still
remembers. The second test casts the same card for its printed cost and asserts nothing is drawn: a
trigger that fired there would pay off every copy that ever died.

Both tests count the **library**, not the hand. The sacrifice lands in the end step with cleanup
immediately after, so a player at seven cards draws one and discards one and finishes the turn
exactly where they started — a passing assertion for a trigger that never fired. The library only
goes one way.

### Melee, reversed — the rulebook said something different from the reminder text

Melee was declined a round earlier for needing a kind of continuous effect the engine did not have:
"for each opponent you attacked this combat" is not a filter over permanents, and the per-each pump
counts permanents through a target filter. That much was right, and checking it rather than assuming
it was worth doing — the pump-per machinery genuinely cannot express it.

What was wrong was the conclusion. Reading **CR 702.121a in the rulebook this repo ships** gives the
ability as "for each opponent you attacked **with a creature** this combat" — and an attack target
carries the defending player even when the attack is aimed at a planeswalker (CR 506.2). So the
count is exactly *distinct defending players among your attackers*, which is a fact the combat state
already holds. A well-defined count is a new definition kind worth adding; a vague one is not, and
the difference was one lookup in a document that is right here.

So `pump-per-attacked` sits beside `pump-per` as its own kind rather than borrowing its group
phrase. Spelling "opponents you attacked" as a permanent filter would have produced a string that
read plausibly and counted something else.

The card text the ability carries repeats the **rule's** wording, not the reminder text's, so the
two cannot drift apart in the log.

### Mobilize: "attacking" is a place, not a property

`CreateToken` can make a token tapped, and that is as far as flags reach. Attacking is not a
property a token can be created with — it is a position in the combat, and the token has to be put
there by joining the combat its maker is already in. Which player that is cannot be known until the
trigger resolves.

So mobilize is its own effect, built from events that already existed: `ObjectCreated`,
`PermanentTapped`, `JoinedCombat`, and a delayed trigger for the sacrifice at the next end step. The
Warriors attack the same player the source does — that is the difference from myriad, which spreads
across the *other* opponents, and it is why this reads the source's own attack target rather than
walking the turn order.

It does nothing at all if the source has left combat by the time the trigger resolves. A token
created attacking nobody would sit on the board as a 1/1 that never attacked and never got
sacrificed, which is worse than no token.

The reminder text was worth reading rather than recalling: the tokens are red **Warriors**, and
writing them from memory would have made them Goblins on every one of the seventeen cards.

The test asserts the tokens are in `Combat.Attackers` pointed at the right player, that the damage
that lands is three rather than one, and that they are gone by the next turn. Counting tokens alone
passes for two 1/1s created harmlessly beside the attack.

### Three keywords, and the difference between expanding and rebuilding

The work queue's honest head is keywords: 320 cards across 118 of them, none individually large.
Three were taken, chosen because their rules are unambiguous and the machinery already existed.

**Outlast** is the one worth reading twice, because it adds no mechanism at all. A cost, a tap, a
+1/+1 counter and sorcery timing are each read already, and the reminder text spells out the exact
sentence they compose to. So the keyword expands into that sentence and hands it to the ordinary
activated-ability reader — the way overload hands its rewritten spell to the phrase parser. Writing
the ability by hand would be a second copy of that reader, free to drift from it. The expansion
fails loudly: if the reader will not take the sentence, the printed line stays unread rather than
becoming an ability that costs the wrong thing.

**Training** is the opposite case, and could not be expanded — its trigger is a question no existing
condition asks. "Attacks with another creature with greater power" is answered from the declaration
itself, which is one event carrying every attacker (CR 508.1), so both halves — that this creature
is in it, and that somebody else in it is bigger — come from the same event rather than from a board
that may have changed by the time the trigger resolves. Power is the computed power: a creature
pumped during declare-attackers counts at the size it is. The test is a theory with a bigger partner
and a smaller one, because a test with only the bigger partner passes for a card that reads
"whenever this creature attacks".

**Annihilator N** is N separate sacrifices rather than one worth N, because the defending player
chooses each in turn and what they lose first changes what is left to lose second. Each carries its
own effect index — that is how a completed choice is read back to the effect that asked it, and N
effects sharing an index would be ambiguous and answer neither. "Of their choice" cost nothing:
`PlayerScope.DefendingPlayer` already existed, so the request is addressed to the defender and they
pick from their own board. The test asserts the attacker's board is untouched as well as the
defender's, because a scope that fell back to the controller would have taken the wrong one and a
count-only assertion would not notice.

**Measured and declined: melee.** Three cards, and the only one of the four that needed a new kind
of continuous effect — "for each opponent you attacked this combat" is not a filter over permanents,
which is what the per-each pump vocabulary is built from. Inventing a filter string that means
"opponents attacked" is exactly the sort of thing that reads fine today and disagrees with itself
later.

### Damage was not life lost, so half the cards asking never heard about combat

`LostLifeThisTurn` was set by exactly one path — the event that changes a life total — and combat
damage does not take that path. `DamagePlayer` subtracts from `Life` directly, and the comment
sitting two lines above it cites **CR 120.3c: damage dealt to a player causes them to lose that
much life**. The flag and the rule quoted beside it disagreed.

Everything asking the question was reading the answer "no" through an entire combat:

- **"If an opponent lost life this turn"** on cards, via `BoardConditions` — blind to the commonest
  way it ever happens.
- **The speed rule** (CR 702.179b), which increases your speed when an opponent loses life during
  your turn. It could not fire from an attack, which is what the mechanic is for.

One line fixes both, and the guard on it matters: damage of 0 is not dealt at all (CR 120.8), so a
zero-power creature connecting must not count as a loss. The test asserts the flag directly after a
2/2 gets through, and it fails without the change — checked by reverting the line and watching it go
red rather than by assuming.

This is the shape of bug worth the most: nothing was unread, nothing threw, and the coverage number
never moved. A card counted as fully implemented was answering a question wrongly.

### Surge and spectacle: an alternative cost with a condition

Two keywords, one shape — "you may cast this for *that* cost instead, but only if the board says
so." Dash and evoke needed no such record because their offer is unconditional; these two carry a
question as well as a price, and keeping the two halves in one place is what stops a cost being
taken when its condition is false. That would not be a discount, it would be a different card.

So `ConditionalCost` holds the keyword, its rule, the cost, and the predicate. Prowl and
freerunning are the same form with a different question, and are now a condition rather than a
mechanism.

- **Surge**: "you or a teammate has cast another spell this turn". With no teams in the engine that
  is you, and it is simply `SpellsCastThisTurn > 0` — because the spell being cast has not reached
  the log yet. Its `SpellCastEvent` is emitted after its cost is paid, so at the moment the offer is
  weighed the count still means *other* spells. The test casts the surge spell **in response to**
  the first one rather than after it resolves, which is both the truer reading of "has been cast"
  and the version that cannot accidentally end the step.
- **Spectacle**: "an opponent lost life this turn" — the condition that turned out to be broken
  above, which is why it got its own test through combat damage rather than through a life-payment.

The two refusals are kept apart on purpose. A card with no such offer at all is a client sending a
flag that card never had; a card whose condition is false is a player who has misread the board.
Each names itself and its rule, because "cost not available" tells nobody which permission they
reached for.

Reachable through the hub, not yet from the board: `CastOptionsDto` gained a field, which changes
no positional argument, and the client sends the options object as it already did. Dash, evoke,
buyback and overload are all in the same position — the board has no alternative-cost UI yet, and
this adds none.

### A storage land named its own price

Eleven lands say "Remove any number of storage counters from ~: Add {C} for each storage counter
removed." The engine could already remove a counter as a cost and could already add mana; what it
could not do was let the two be *the same number*, chosen by the player at the moment of
activation.

**"Any number" is a number named on activation (CR 601.2b)**, in the same breath as X in a mana
cost — not a price the card sets. So the counter cost stops being the amount to charge and becomes
only a **floor**: the printed count is the least the ability will accept, and the player names the
rest. An ability written the ordinary way ("Remove two charge counters") is unchanged, because
there the chosen count and the printed count are the same number.

The load-bearing detail is that the count is **read once** and handed to both halves. The check
that the permanent holds enough counters, the event that removes them, and the amount of mana
produced all read the same local, so there is no arrangement in which a land charges three and pays
one. Writing it as two independent reads is the obvious shape and is wrong the moment anything
between them can change the permanent.

The payout side is a flag rather than a number: `ManaProduction.FromCounterCost` says *this amount
follows the cost that was paid*, which is what "for each storage counter removed" means. Storing a
computed amount at compile time cannot express it, because the amount does not exist until the
ability is activated.

**This is the first ability whose activation carries a player-named number, and that made it a
cross-repo change.** `GameHub.ActivateAbility` gained a positional `int variableValue`, and both
client call sites — `play-hub.service.ts` and `signalr.service.ts` — send it in the same commit,
zero included. The client's own standard is blunt about why: *a hub call is positional, and the
server will not fill in what you leave off.* Two specs now count the arguments and assert the
chosen number arrives, because this repo has already shipped a hub signature change that broke
every cast in the app while the in-process hub tests stayed green.

The test is a theory that spends one counter and then three, asserting both the mana produced and
the counters left behind. Asserting only the mana would pass against a land that removes every
counter it has and pays out correctly anyway.

**Casting restrictions** (CR 601.3e) — 67 lines, none of which read before. "Cast this only during
combat", "only during the declare blockers step", "only after combat", "only if you've cast another
spell this turn", and the largest single one, "only during the declare attackers step and only if
you've been attacked this step".

The restriction sits **on top of** the type's own timing rather than instead of it: a sorcery that
says "only during combat" means both things, not the looser of the two. And a restriction this
cannot read leaves the card **unread** rather than dropping it — a spell castable at any time is a
different and better card than the one printed, and that is the direction that must never happen by
accident.

"You've been attacked this step" needed no new state: an attacker whose target is this player *is*
the fact, and the declaration already recorded it.

Writing the test settled a rules point worth keeping: **priority in the declare attackers step
exists only after attackers are declared** (CR 508.2), so "the step is right but nobody has attacked
yet" cannot happen in a real game. The only reachable refusal is the attacking player reaching for
it — declared, but not at me — and that is what the test uses.

### "Add two mana of any one color" made one mana

The mana vocabulary was swept expecting a compile gap and ten of the eleven commonest forms already
read. The eleventh led somewhere better: the any-colour branch hardcoded a quantity of **1**, and the
pattern that reaches it matches "any *one* color" as well — so "add two mana of any one color"
produced one mana and "three" produced one. About fifty rocks and filter lands paid out a fraction
of what they print.

Nothing failed. The card compiled, the ability activated, mana appeared. **Only asking how much**
found it, which is why the fix came from reading the neighbourhood of a gap rather than from the gap
itself. The theory asserts one, two and three separately, so a hardcoded quantity fails two of the
three.

**"Add two mana in any combination of colors"** was declined for a round and then done, because the
model expresses it after all — by enumeration. The player picks a payout by picking which ability to
activate, so fifteen pairs of five colours is a menu rather than a mechanism, and two of one colour
is one production of two rather than two of one: the pool counts mana and does not care how it was
described.

**Capped at two on purpose.** Three colours would be thirty-five alternatives and five would be a
hundred and twenty-six — not menus anybody can use — so those stay unread rather than being made
unusable, and a test asserts the five-mana form is *refused*. The cap is a stated behaviour, not the
place I happened to stop.

**The class it suggested does not exist.** Twelve counted effects were swept for the quantity they
actually carry — draw, mill, gain, lose, tokens, damage, counters, discard, scry, surveil, and the
multi-target graveyard return — and **every one is correct**. The mana bug was a one-off, not a
family, and this area does not need sweeping again.

Both apparent findings on the way were the probe's: measuring end state after `Settle` counts the
cards the turn advance drew, and truncating the output at 110 characters cut the second effect off
"return two target creature cards" and made a two-card spell look like a one-card spell. It returns
two, indices 0 and 1. **Read the whole line before believing it.**

The target grammar was swept whole — 10,116 target phrases, 1,126 distinct — and twenty of the
twenty-one commonest already read. The one gap took three attempts to find, and only the third was
the cause: widening the reader to split on "or" changed nothing, widening the regex past a single
word changed nothing, and then **`PermanentTypes` turned out to have no instant and no sorcery** —
and it was the only type table the spell-target reader had. *Every* spell target naming a
non-permanent type had never worked: "target instant spell", "target sorcery spell", "target instant
or sorcery spell", 73 lines.

Spell types now have their own table, deliberately not merged into the permanent one: a permanent is
never an instant, and merging them would let "destroy target instant" compile.

⚠️ The first corpus extraction cut phrases mid-way and produced fragments — "creature to its owner's
hand" — which the probe then reported as unread. Same fragment trap as before, one layer earlier:
**the extractor can manufacture the bug as easily as the input can.** Re-extracting on the cards'
own punctuation gave a clean list.

The activation-cost vocabulary was swept next — 11,499 activated abilities, 1,193 distinct non-mana
cost atoms — and came back **healthy**: eleven of the twelve commonest already read, which is worth
recording so nobody measures it again. Sacrifice, discard, pay life, tap a creature, remove a
counter and sacrifice-another all work.

The one gap was **"exile N cards from your graveyard"**: the filter was mandatory and the count was
not read at all, so the plain form went unread and a counted one would have charged for a single
card. They vary independently — 51 lines across the bare, filtered and counted forms.

The test failed first for a reason worth keeping: these costs are **offered with the activation**
rather than asked for during it, because the pick belongs to the player and a cost paid mid-action
would be a continuation a log cannot rebuild. The engine's own refusal — "needs 1 card(s) to pay its
cost" — proved the count was already being read correctly and located the fault in the test.

The Aura trigger verbs were swept the same way — 268 lines across 47 verbs, twelve commonest probed,
four missing. Three were **damage verbs the source-naming-itself reader already had**: "deals
damage" and "deals combat damage" vary by whether the damage must be combat damage and whether the
sentence names who took it, and that reasoning had been applied to `~` a few rounds earlier and not
carried across. "Deals damage to an opponent" adds one more question — whose life it was — asked of
the **ability's controller**, because "opponent" is said from the point of view of whoever controls
the Aura and not of the creature wearing it.

The trigger's **noun slot** only accepted "creature", so "whenever enchanted land becomes tapped"
went unread — one word, seven lines. "Player" stays out of it deliberately: an Aura on a player has
a player for a host, and everything in that handler resolves a permanent.

Four rounds on this thread and the shape of the fault was the same each time: the engine had the
host concept in some readers and not others, and each reader carried its own verb or subject list
built from whatever cards were at hand. The card that reveals one is never the whole of it.

**And the thread is now done, which the measurements say rather than a feeling.** The sweeps found
58 lines, then 27, then 7, and the last one — the attached-subject *effect* verbs — found the corpus
has only six such lines and every one already reads. Diminishing returns three sweeps running is the
signal to look somewhere else.

The mirror of the fix above: **"as long as it's attacking" on an Aura is about the creature**, not
the Aura. An Aura is not a creature and never attacks, so a reading that asked about itself would
never be true and the card would do nothing at all. **Only the pronoun redirects** — "~ is untapped"
on an Equipment is about the Equipment, which can perfectly well be tapped while the creature
holding it is not, and that has its own test: tapping the Equipment turns the bonus off.

⚠️ Doing it broke every named-subject condition, and the new test caught it. Moving "is" inside the
alternation to fit "it's" left `~ is untapped` with nowhere for its own "is". The failing test failed
at its **first** assertion, which is what said "regression" rather than "gap" — a new feature that
does not work fails at the new assertion. The first hypothesis was card-type-specific, since the
failure showed up on an Equipment; running the same lines against a creature ruled that out in one
step and pointed straight at the regex that had just changed.

Once "it" meant the host, the whole conditional-static family was swept for the same blindness and
three more shapes turned up (58 lines): "as long as you control a Swamp, **enchanted creature** gets
+1/+1", the same sentence with the condition trailing, and "during your turn, **enchanted creature**
gets +1/+1" — an arm that had no subject slot at all and could only ever say "~".

"It", "enchanted creature" and "equipped creature" are three ways of naming the same thing, and only
"~" means the card itself. Finding one instance of a gap is a reason to sweep the family it belongs
to; this is the second time in two rounds that doing so was worth more than the original find.

**An Aura's conditional static asks about its host and buffs its host.** "As long as enchanted
permanent is a creature, it gets +1/+1" was two gaps rather than one, and only the second was where
it looked.

The condition side reads the host now: a type or a subtype is a noun and stands alone, a colour or a
supertype is an adjective and needs one, so "red" becomes "red permanent" and "creature" is left as
it is — tried in that order rather than guessed at.

The **effect** side was the actual blocker. Swapping "it gets +1/+1" for "~ gets +1/+1" compiled
immediately, which located the failure in one line: **"it" on an Aura is the thing it is attached
to**, and the conditional-static reader only ever applied to the source. An Aura is not a creature,
so a reading that asked about itself would never turn on and one that buffed itself would put the
bonus where it cannot be used. The word is what decides — "~" is the card saying its own name, and
only the pronoun means the host.

**"Another" in a presence check** excludes the permanent asking (CR 109.5). Every adjective in that
slot already worked — multicoloured, legendary, tapped — and this one word did not. It is the
difference between a card that turns itself on permanently and one that needs a friend, which is
why the test asserts all three states: alone, with an opponent's creature out, and with a second
creature of your own. Worth **+32 cards** for one word, which is what a missing word in a shared
slot tends to be worth.

**Three more questions a permanent asks about itself** — "as long as ~ is monstrous", "as long as ~
is attacking", "as long as ~ is equipped". Each is answered somewhere the tapped/untapped reader
does not look: a designation, the combat state, and what is attached. The monstrous one only became
answerable because that designation was added a few rounds earlier, which is the second time this
stretch that earlier state work has made a later line cost one matcher.

**"Equipped" is a fact about the Equipment, not about the creature** (CR 301.5c), so it is asked of
the battlefield rather than of the permanent. The test attaches an Aura first and asserts no bonus,
then an Equipment and asserts the bonus — a reader that only asked "is anything attached" passes a
one-sided test and fails this one.

### Measured and declined: goad

Ninety cards mention it and no two phrase it alike; the most common single form is eight. Its rule is
"attacks a player other than you if able" — a multiplayer *requirement*, and how it combines with the
must-attack requirement in a two-player game (where the only other attackable target may be a
planeswalker, or nothing) is exactly the kind of subtlety this file exists to keep out of the
engine. It needs a goaded-by record with a duration before any of that can be got right.

The Aura buff family looked like eighty-five blocked lines and was not: probing ten of its forms
showed every one but goad already compiles. A cluster head is a place to look, not a thing to fix.

**"Whenever a creature dealt damage by this creature this turn dies."** The damage total cannot
answer it: how much is on a creature says nothing about who put it there, and by the time it dies
several things may have damaged it. Each permanent now remembers what has hit it, beside how much.

Two details decide whether this is right. The dying creature is read from the state **before** the
move, the only state that still has it — a zone change makes a new object (CR 400.7) and the new one
is a card in a graveyard with no damage on it at all. And the memory is cleared with the damage at
cleanup (CR 514.2), which is the same "this turn" the cards mean: a creature damaged last turn and
killed this one pays nobody, which the second test asserts rather than only the happy path.

**"Whenever you tap a land for mana"**, and the fifty-nine lines around it. `ManaAdded` now carries
the permanent that produced the mana: the pool does not remember where mana came from (CR 106.1) and
does not need to, but this is a fact about the ability that made it and lives on the event that says
so. One reader covers three independent choices — whose permanent, what it is, and whether the
sentence names an Aura's host instead — and **the event is the mana, not the tap**, so a land tapped
to pay a cost never reaches it.

Both bugs in it were found by tests rather than by reading: the noun group captured the article, so
the filter asked for "target a land" and nothing matched; and the alternation shared the suffix
"tapped for mana", which only the Aura branch says — "you tap a land *for mana*" has no "tapped" in
it. Making "for mana" the shared part fixed all four forms at once.

**"Whenever you scry" and "whenever you surveil".** The request *is* the event: it is emitted the
moment the effect resolves and carries who is doing it, and the moves that follow are the answer to
a question rather than the thing the card is watching for. Surveil is the same request with a
different destination (CR 701.42a), so the two are told apart by that flag rather than by two
readers — and the test drives all four combinations, because a card that watches one must not fire
on the other.

**A damage trigger need not name who took the damage.** "~ deals combat damage to a player" was
read and "~ deals damage" was not — the recipient was mandatory, so every bare form went unread.
They are now one reader over two independent questions: whether the damage has to be combat damage
(or explicitly *non*combat, which no reader could say before), and whether the sentence names a
recipient at all. A sentence that names none means any of them.

⚠️ Half the misses this probe reported were **the probe's own normalisation**: it replaced digits
with `N`, so "one or more +1/+1 counters are put on this" became "+N/+N counters" — which matches no
counter kind — and "mana value N or greater" likewise. Both read correctly with real numbers. When a
probe normalises the corpus to group shapes, it must un-normalise before asking whether a shape is
read.

Two phrasings of the filtered dig: "put **that card** into your hand" beside "put it into your
hand", and "**You may** put one of them into your hand". The second adds nothing the mechanism did
not already do — the choice offered by a dig already allows taking none — which is worth knowing
before writing a second effect for it.

⚠️ **Most of what a sentence-level probe reports as unread is the probe's own doing.** Re-running
the hundred commonest effect sentences listed "destroy it", "it gains haste until end of turn",
"untap that creature" and "it can't be regenerated" as failures — every one of them reads correctly
in the multi-sentence line it is actually printed in, where the target it refers to exists. The same
goes for "this ability triggers only once each turn", which is stripped at the trigger level and
never reaches the sentence parser. **Probe whole lines, not fragments**; a fragment probe measures
the parser's context, not its vocabulary.

**"If this creature was kicked, it enters with two +1/+1 counters on it"** — 23 cards, and readable
only because of a fix made earlier in this same stretch. The spell that was kicked and the permanent
that arrives are different objects (CR 400.7); the flag survives the move because CR 607.2's linked
abilities say it must, and that carrying was built for "if it was kicked" on an enters *trigger*.
This is the replacement form of the same question, and it cost one matcher because the hard part
was already done. A token has no such flag and was never kicked, so it arrives bare — the right
answer rather than an omission.

The regex is written with `[+]` and `[.]` rather than backslash escapes, and checked with `cat -A`
before being trusted. That is the standing habit now, after a backslash escape turned into a literal
control byte and cost an hour.

**A lord can name a keyword**: "other creatures you control with flying get +2/+2". Ten forms of
that sentence were probed and this was the only one missing — the subject slot had a scope, a
subtype, a colour, a side and a chosen type, and no qualifier. It reads with the same table that
reads the keywords a lord *grants*, which is the same question asked in the other direction.

It is asked of the computed characteristics like everything else here, so a creature given flying by
a spell joins the flock — a lord reading the printed card would never see it, and that is what the
second test checks.

**Oblivion Ring printed as two lines.** The one-line form — "exile target permanent until this
leaves the battlefield" — was already built as a pair, for a reason the two-line form makes urgent:
*an exile compiled without its return is removal that never gives the card back, which is a strictly
better card than the one printed.* Reading the two lines independently would risk exactly that, so
they are matched before the line loop and either both are read or neither is.

The return line is the one that decides. Several cards in the family return the exiled card
somewhere else — to its owner's hand, to a graveyard — and those are different cards with a
different effect. Only the battlefield form is read; the rest are left alone rather than given this
one's behaviour.

The record sunburst needed turned out to answer three more questions, so it is **the mana that was
spent and not a count of its colours**: "if {U} was spent to cast this spell", "if {R}{R} was spent"
(two of them, which a set of colours cannot say), "if at least four mana was spent" (a total, which
needs the colourless too) and "if no mana was spent". Four clause families, one record, read as one
shape with the symbols counted rather than as four readers. Converting the set to a pool was worth
doing the moment the second question appeared rather than after the fourth.

"Mana from a Treasure was spent" is deliberately still unread: *where* a mana came from is not on
this record, and answering it from the colours would be a guess.

**Sunburst** (CR 702.43a), and with it the first record of *which mana paid for a spell*. The colours
spent are read as the difference between the pool before and after payment — one subtraction cannot
disagree with the payment the way a parallel tally could — and recorded on the spell as it goes on
the stack, which is the first moment there is an object to record them on. They survive resolution
the way the kicker flag does (CR 607.2), because sunburst is asked as the permanent enters and the
spell is gone by then.

Colours *spent*, not colours in the cost: a generic symbol paid with a green mana is a green mana
spent, and colourless is not a colour (CR 106.1b), so a sunburst permanent paid for entirely with
colourless enters bare — which is what the card says. The counter is +1/+1 on a creature and a
charge counter on anything else, which is the keyword's own rule and not a reading of the type line.
The test casts the same {2} creature off two Forests and off a Forest and an Island: one counter and
two.

⚠️ **A build that fails leaves the last good binary in place, and `dotnet test` will happily run
it.** The regex above spent an hour reading `^Sunburst` where the `` was a literal backspace
byte — written through a Python patch script where `` is an escape — so the matcher never fired
and the card compiled to nothing. The evidence said "unhandled" while the code on disk said
otherwise, and the intervening builds were reported clean. Check that the build actually succeeded
before believing a test result, and prefer writing regexes without backslash escapes when the patch
tool has its own.

**The Phantom cycle** — "if damage would be dealt to this creature, prevent that damage; remove a
+1/+1 counter from it". These are printed 0/0 and enter with counters, so what kills one is running
out of counters and being a 0/0 (CR 704.5f), not the damage that never landed. Nothing checks
whether a counter is there to remove: a creature with none is already dying, and adding that
condition would make it immortal instead. One counter however much damage is dealt, and the two
clauses are one replacement rather than a prevention plus an effect, because the removal must not
happen when the damage is not being dealt.

### An entry replacement could not see a token arrive

All five entry replacements — enters tapped, enters with counters, fading, echo, bloodthirst — were
written against `ObjectMoved`, and a token is `ObjectCreated` (CR 111.1) with no earlier id at all.
**A token copy of a creature that enters with counters entered without them**, and a created land
that enters tapped arrived upright.

Widening the five was not enough on its own, which is what made this a core change rather than a
card-reader one: the replacement pipeline walks the objects that already exist, and a token being
created is not one of them, so its own replacements could never be found. The pipeline now builds
the arriving object from the event — which carries the whole card definition, all a replacement
needs — rather than folding it in early, because folding it in early would make the arrival happen
before the effects that replace it have run.

The five sites became one reader, `Arriving`, returning the id the permanent will have whichever way
it got there. They were five copies of the same cast and wrong in the same way; one of them can now
be wrong at a time. The one thing a token genuinely cannot have is X — X rides on the object doing
the casting, and a token was never cast, so "enters with X counters" gives it none.

Found by a test that reached the battlefield the wrong way and failed for the right reason.

The corpus smoke test then caught the consequence: **Skyshroud Behemoth** has fading *and* enters
tapped, two entry replacements on one creature, so arriving is now a question — the controller
chooses which applies first (CR 616.1) and the arrival is held until they answer. That question
could not arise before, because neither replacement could see the creature arrive. Left unanswered
the permanent never finishes entering, and everything after it looks for an object that is not
there; the smoke test answers it now, the way a player would.

**Adapt and monstrosity** are two keyword actions that are each one conditional effect, so they
compile to that rather than to a mechanic of their own. Adapt (CR 702.131a) needs nothing new: if
the creature has no +1/+1 counters, put N on it — and the condition *is* the keyword, so a second
activation is a legal thing to do that achieves nothing.

Monstrosity (CR 701.32) needed one piece of state. It is a **designation, not a counter count**:
`IsMonstrous` on the permanent, set by its own event, because "becomes monstrous" is what a second
activation reads and what some three dozen cards trigger on — and a creature can hold +1/+1 counters
without ever having been made monstrous, so counting them would answer yes to the wrong creature.
The effect emits the counters and the designation together rather than as two effects, because
splitting them would let a second activation put counters on without the condition being asked.

Two more readings the same family wanted. **"Instant and sorcery cards" means a card answering to
either of them** — nothing is an instant and a sorcery at once, so reading the "and" as a
conjunction counts zero every time. Only when every part is a card type: "a noncreature, nonland
card" joins adjectives with "and" and does mean both, which is what the ampersand join is for.
And **"the number of creatures on the battlefield"** is the same count with nobody's name on it —
the phrase is taken off before the target grammar is asked, because that grammar reads ownership
and has no way to spell "everyone's". What is left is a filter with no owner, which is exactly what
counts every one of them.

**A defining ability may now set one stat and leave the other printed** (CR 604.3). "~'s power and
toughness are each equal to the number of creatures you control" was read; "~'s power is equal to
the number of creatures you control" was not — the same sentence with one stat in it, and the pair
was the only form the matcher knew. The half the sentence does not mention keeps its printed value,
which is the part a pair-only reader had nowhere to put: the test destroys a creature and watches
the power move while the toughness stays where it was printed.

Same asymmetry as the colours, the keywords and "nonbasic without basic" — four now, in four
unrelated tables. It is worth assuming the next vocabulary has one too.

The search-filter vocabulary was measured the same way and came back **healthy**, which is worth
recording so nobody measures it again: every filter the corpus commonly asks for is read, including
"basic land" (296 cards), the " or " lists, and the "permanent" suffix. Two atoms were missing —
`colorless` (the absence of all five colours, CR 105.1, so it cannot sit in the colour table) and a
`non` inside a multi-word phrase — and both had been *refusing* the phrase rather than misreading
it, which is the failure this file asks for.

⚠️ Three findings before those were false: the first probe fed **raw English to the matcher**, which
takes a canonical id the compiler builds ("basic land" becomes `basic-land`, "instant or sorcery"
becomes `instant|sorcery`). Read raw, "nonlegendary creature" negates a subtype nobody has and
matches every card in the game. **A probe that skips the normalisation reports the normalisation as
a bug.** That is the third time in this stretch; the rule is to drive these through the same entry
point the compiler uses, never the layer beneath it.

### The same asymmetry, found by diffing a table against an enum

The grantable-keyword table had no entry for **fear, intimidate, shadow, skulk, wither or
changeling** — and the engine plays every one of them. The blocking rules have enforced the evasion
trio since combat was built (CR 702.36b, 702.13b, 702.27b); the words for granting them were simply
never written down, so a card that hands one out compiled to a grant of nothing.

This was not found by meeting a card that wanted one. It came from listing the `KeywordAbility` enum
and subtracting the table, which is the same shape as the colour gap and the "nonbasic without
basic" gap: **these vocabularies were built from whatever cards were in front of the author, so they
are asymmetric in ways nothing detects.** A missing word does not fail — it reads as a tribe, or as
nothing, and the card compiles. Worth re-running that diff whenever the enum grows.

Once the colours turned up missing, the rest of that slot was worth measuring rather than assuming,
and four more words were absent: **basic** (569 mentions of "basic land", and "nonbasic" was already
there — the same asymmetry as the colours), **snow**, **blocked** (CR 509.1h: a creature stays
blocked once it has been, even if every creature blocking it has gone), and **modified** (CR 701.48a:
a counter on it, an Aura its controller controls, or Equipment attached — three unrelated things
under one word, like historic). "Enchanted" joins them as a question about a permanent rather than
an Aura's own reference to its host.

### The adjective vocabulary had every colour's negation and none of the colours

There was a reading for "nonwhite creature" and none at all for "white creature". The sentence still
compiled, because a capitalised word with no other reading is taken for a creature type — so **"White
creatures get +2/+0" looked for a tribe called White** and pumped nothing. The same slot was matched
case-sensitively, which is deliberate everywhere else (a capital is what separates a creature type
from an ordinary noun) but wrong for an adjective at the start of a sentence, capitalised by position
and nothing else: "Attacking creatures get +2/+0" fell through the same hole. The group is now
case-insensitive on its own, the rest of the pattern unchanged, and the bare colours sit beside the
negations they were missing from — written after them, because alternation is ordered and "nonwhite"
must match before "white".

This is the one bug this stretch that the coverage number could see: **+87 cards**, because a
sentence that reads as a tribe still compiles, and the cards it was blocking were blocked for other
reasons too.

### "That player" meant nobody, and the card did nothing

The same defect on the player side, found by looking for it once the object side was known.
`SubjectOf` did not answer for a draw or a discard — both are zone changes — so **"whenever an
opponent draws a card, that player loses 2 life" resolved to an empty list of players and emitted
no events at all**. Not the wrong player: no player. The card compiled complete, the trigger fired
on the right event, and the game was simply not played. Both ids of the move are tried, because a
zone change carries two and only one exists in any given state (CR 400.7) — reading only the new one
left the subject null on the state a trigger is offered before the move.

There is now a check for the class: `Every_trigger_that_says_it_can_find_out_what_it_means`. Its
first version passed with the fix reverted — it reached 12 of the ~156 triggers that name a subject,
because its events came from a game that was opened and then passively passed, which produces almost
nothing worth triggering on. **A check that cannot fail is worse than no check**, so the sample is
now built by *doing* the things: a creature really enters and really dies, one is sacrificed, one
bounced, one exiled, and the declarations name creatures that exist. A hand-built `ObjectMoved`
names an object that never existed, and every subject read from it is null whether the code is right
or wrong.

That sample immediately found three more cards of the same class — **Massacre Wurm among them**,
whose "that player loses 2 life" resolved to nobody because a death had no subject player. Reverting
either fix fails the test; that was checked rather than assumed.

Reach then went from 12 to 103 of 156 for one reason: most of these triggers are about the card
itself — "whenever ~ deals combat damage", "whenever ~ attacks" — and a shared sample built before
the card existed cannot name it. The events about each card are now made inside the loop with that
card's own id.

Of the 156, **68 are checked, 29 are still unreachable, and 59 are excluded on purpose**: a trigger
whose subject is the card itself needs no answer, because the fallback *is* the card and that is
what the sentence means. "~ or another" is not excluded — that half can be the other creature, and
the fallback would then be wrong. The counts are printed every run, because a green result says
nothing about the 29.

Each thing the sample learned to produce found more of the same defect, which is the argument for
pushing its reach rather than declaring the class handled:

- Creations and moves are kept **in full** rather than one per kind. "Whenever a land an opponent
  controls enters" and "whenever another creature you control enters" are told apart by whose it
  was and what it was, and collapsing every arrival to the first one throws away exactly that.
  This is what surfaced **Polluted Bonds** and two others: a land *entering* is a creation, not a
  move, and creations had no subject player — so "that player loses 2 life and you gain 2 life"
  did neither.
- Spells on the stack are **real**. A cast predicate looks the spell up to see what kind it was,
  so a cast event naming an id that never existed answers "no" to every question about it.
- The card under test is created under **whoever's turn it is**, because "at the beginning of your
  upkeep" asks whether the active player controls the ability.

Attachment was the largest remaining blind spot — an Aura says almost nothing until it is on
something — so `Game.Attach` now exists beside `Create` and `MarkDamage` as a way of putting a board
into a position rather than playing into one. Reach went to **78 checked, 13 unreachable**.

It immediately found the next one. **Unstable Mutation** and **Essence Flare** read "at the
beginning of the upkeep of enchanted creature's controller, put a −1/−1 counter on it" — and a step
is about no object, so "it" fell back to the permanent with the ability. **The Aura put the counter
on itself**, shrinking nothing, and the creature it exists to wear down was untouched. An attached
permanent now means its host before it means itself, and only then falls back to the card — which is
what an Aura on nothing must mean.

### Boil destroyed no Islands

`Specs.ParseGroup` folds a plural back to the singular the grammar is written around — but the regex
that does it knows the six card type words and nothing else. **A tribe or a land type stayed plural**,
so the filter went looking for a subtype called "Islands", which no card has, and every mass effect
naming one silently touched nothing. Boil, Acid Rain, Tsunami, Tivadar's Crusade. The leading run of
capitalised words is the noun and only its last word carries the plural — "Elf Warriors" is one noun
with the s on the end of it — so that word goes through the same `SingularWord` the counting
vocabulary uses.

Found by a report rather than a gate, and the distinction is the point.
`Untargeted_spells_that_resolve_to_nothing_are_reported` resolves every complete spell that asks for
nothing and lists the ones that emit no events. **It asserts nothing and is not meant to**: whether a
mass effect does anything depends on what is on the board, and "destroy all creatures" emitting
nothing is correct when there are no creatures. Its first run said 173 of 584, which meant almost
nothing — an empty battlefield makes every mass effect in the game look broken. Filling the board
out (a creature of each colour, a flier, a tapped one, a basic of each type, an artifact, an
enchantment, cards in graveyards and hands, X set to 2) took it to 33, and at that size the list is
short enough to read by hand. Boil was in it. What remains is combat — no attackers, no blockers —
and shapes the board still has none of.

### Every drain took life and gave none

The check learned a third question — "that many" and "that much" read *how much* the event was of,
the same way "it" reads what it was about — and it answered immediately: **extort**, and with it
every drain in the game.

"Each opponent loses 2 life and you gain that much life" is two clauses and one number, and the
number belongs to the first. A spell has no triggering event, so there was no amount to read and the
gain resolved to **nothing**: the opponent lost the life, the caster gained none, and the card
compiled, cast and resolved without complaint. The amount a clause produces is now carried forward
to the next one, and only when the ability has no amount of its own — a trigger that says how much
it was about keeps saying so for every clause, and a running total would quietly replace it partway
down the card. Life *gained* is deliberately not counted: it is usually the clause doing the asking,
and counting it would make the number grow down the card, which is asserted by a two-drain spell.

That fix makes the sentence a legitimate source, so the check excludes sentences that name their own
amount, just as the host fix makes the host a legitimate answer and the check no longer demands one
from the event when a trigger names what it is on. The exclusion applies to the object half only: a host answers "it"
and says nothing about "that player", which must still come from the event. (Written first as
`wantsObject && namesHost || namesHost`, which excluded every "equipped" trigger including the ones
asking about a player — a gate is only as good as its narrowest condition.)

Three subjects are answered and two deliberately are not. A move names its object's last controller;
`PlayerDamaged` names the dealer, because the other participant in "deals combat damage to a player"
is a player and not an object. `DamageMarked` stays silent for the opposite reason — damage between
two creatures has two objects in it. A declaration or a damage step with **one** creature names that
creature and with several names nothing: CR 603.2 wants the ability to trigger once per creature,
this engine fires a batch trigger once, and picking one of several would be a reading of the engine
rather than of the card.

### Ranking mechanics instead of wordings, and what it changed

The near-miss probe finds wording variants worth three to five cards. A different question —
**which mechanics has the compiler never read a single line of, and for how many cards is one of
them the only thing left?** — finds whole mechanics worth twenty to fifty. It answers with about
seven hundred cards across twenty-six mechanics, against a template tail averaging 1.08 cards each.

The filter is the whole instrument. Ranking by "cards whose every unread line mentions this word"
alone puts *flying* at 610, because the word appears in token definitions on cards blocked by
something else entirely. Excluding any word the compiler reads somewhere leaves only mechanics it
cannot read at all, and the ranking becomes worth acting on.

Acting on it closed **goad** (47), **suspect** (22) and **decayed** (19) in one pass, all three of
which had been sitting under the flat tail invisible to the other probe.

### Legendary was printed, not computed - and the legend rule read a raw controller

The Ring's first ability is "your Ring-bearer is legendary", and the engine had no way to say it:
`IsLegendary` read `obj.Card.Supertypes` and nothing else. Needing the capability turned up a live
bug beside it - **the legend rule grouped permanents by `o.ControllerId`**, the stored value, which
is where a permanent *started*. Control is layer 2 (CR 613.1b), so a stolen legend was filed under
its old controller and two copies could sit side by side under one player without the rule firing.

That is the ninth instance of the mistake this file already records: *any read of `obj.Card.*` or
`obj.ControllerId` where a computed characteristic was meant.* It was sitting in state-based
actions, in a rule that had tests, and none of them stole a legend.

Both halves are computed now. The lesson repeats rather than changes: this class is found by
needing a capability, not by looking for it — the first three combat controller bugs arrived the
same way.

### The Ring is a rules object, so its wording lives in the rules

Nothing compiles CR 701.54c: the Ring is an emblem the game hands out, and no card carries its
text. Its four abilities are written out in `RingAbilities` and granted to the bearer as its
characteristics are computed, so a creature that stops being the Ring-bearer stops having them with
no effect to end and nothing to clean up.

Three details the rules insist on and the implementation keeps:

- **The count is a number, not flags.** Each ability is "as long as the Ring has tempted you N or
  more times", so they accumulate and none is taken away. Capped at four, because a fifth
  temptation still happens and still chooses a bearer (CR 701.54d) and there is no fifth ability.
- **Ring-bearer is a designation on the player** (CR 701.54b), not a characteristic of the
  creature - it is explicitly not a copiable value.
- **The third ability is delayed.** The blockers are sacrificed at end of combat, not on being
  declared, so they deal and take their combat damage first.

### A trigger whose source had ceased to exist, and four runs spent blaming the wrong thing

`LastKnownCard` answers "what card was this object?" for a trigger going on the stack whose source
has left. It walked **forwards** — find the move whose `OldId` matches, take the object it became —
and required that object to still exist. A permanent that leaves the battlefield and then ceases to
exist has nothing at the far end, and its `ObjectCreated` is filed under the id it had in the
library, several moves back. So a leave-the-battlefield trigger on such a permanent threw as it went
on the stack. It now walks backwards through the move chain to the creation, with a `seen` set so a
cycle cannot hang it; forwards is still tried first, being cheap and giving the card as it is *now*.

**Only the corpus soak could find this.** It needed a game to run on past the moment, and it broke
one table in eight hundred and forty-one. The 1,183-test rules suite was green throughout.

The diagnosis took four soak runs and was wrong for three of them, in two ways worth recording:

- **A bisect that measured the wrong variable.** Disabling the clash reader made the soak pass, so
  clash looked guilty. It was not: enabling a reader changes *which cards compile*, which changes
  how the soak slices them, which plays a **different table**. None of the fifteen cards on the
  failing table has clash — and their text was on screen when the conclusion was drawn.
- **Two "fixes" that appeared to work because the filter was narrower than the failure.** The
  failing run was `~CompiledCardSoakTests` (six tests); the confirming runs were
  `~Every_compiled_permanent_survives_being_played` (three). Comparing a passing run against a
  failing one requires the same filter, and twice it did not have one.

### The same instrument, wrong a second time

The mechanics estimator asks: *which mechanics does the compiler never read a line of, and for how
many cards is one of them the only thing left?* Its filter was "no line in the read dump contains
this word" — and **the read dump only holds lines from cards that compile in full.** So a mechanic
that is implemented but only ever appears on cards held up by something else looks unimplemented.

**Bargain** was exactly that. Its cost and its "if it was bargained" clause both work; every card
carrying it is blocked by its payload — "create a Cursed Role token attached to…", "it fights up to
one target creature you don't control". The probe ranked it at 21 cards of pure profit and there was
none there.

It now asks the compiler's own source as well, which drops bargain and `visit` off the list. This is
the second time an instrument here has agreed with itself and been wrong about the engine; the first
compared normalised text against printed text. **A probe built from two derived artefacts has to be
checked against the thing itself, and neither of these said anything was amiss.**

### What "100% of the cards" includes, measured

Worth stating once, because it had been an open question: of the 32,765 playable cards, **75 are
format-specific** — 48 Stickers, 22 Attractions, 5 Planes. Schemes, Phenomena, Vanguards and
Conspiracies are not legal in any format Scryfall tracks and never enter the corpus at all. So the
supplemental formats are 0.2% of the work, not a subsystem's worth, and nothing here needs a planar
deck or a sticker sheet to reach the rest.

### Goad, and the reason it had been declined

This file recorded goad as measured and declined, for want of "a goaded-by record with a duration".
Both existed by the time it was re-examined — the `While` predicate and the sweep that ends effects
whose condition has failed were built for something else — and the rest was three small pieces:

- **`GoadedBy` is a set**, because CR 701.15c lets several players goad the same creature.
- **The goader and the turn travel in the effect's id.** A continuous effect carries no state, and
  "until the next turn of the controller of that spell or ability" cannot be answered without both.
- **The requirement and the restriction live apart.** "Attacks each combat if able" is a keyword the
  engine already had; "attacks a player other than you if able" is a restriction, and only the
  combat rules know who is being attacked.

Two tests broke when goad started reading — both used it as their example of *unreadable* text.
Their fixtures now use an invented clause, so implementing a real mechanic can never turn a
fail-closed test green by accident. That had already happened silently twice.

### "That player" could not be named, on two hundred and twenty lines

The target grammar's owner vocabulary had "you control", "an opponent controls", "defending player
controls" — and no way to say **"that player controls"**, which two hundred and twenty lines use and
none read. The subject was recorded all along on the ability (`AbilityOnStack.SubjectPlayer`, CR
603.2); what was missing was that **targets are chosen before the ability exists**.

The offer site handed the legality check the bare permanent, whose `Ability` is null — so the filter
found no subject, refused every candidate, and the ability was removed for having no legal target.
It is now handed the source dressed as the ability it is about to become. Everything such a filter
asks about is already decided and waiting on the trigger; only the object holding it had not been
built yet.

### The probe was comparing the engine against a differently-spelled copy of itself

The near-miss probe ranks unread line shapes by how close they are to a shape the compiler *does*
read, and it had been the best instrument here for exactly that reason — it compares the engine
against itself rather than against anyone's memory. It was also, for its whole life, quietly wrong:
**the unread dump is the compiler's normalised text and the read dump was the printed text.** A
card that refers to itself appears in the first as `~` and in the second as "this creature", so the
two highest-ranked shapes it reported were differences between the dumps and not between the
readers.

Fixing it (the read dump now runs the same "this creature" → `~` rewrite) merged 220 lines that had
been counted twice and took the probe from 330 near-identical shapes to 522 — the shapes it had
been hiding were more numerous than the ones it had been showing. **An instrument that compares two
derived artefacts has to derive them the same way, and nothing about its output said otherwise.**

### Two combat requirements that had to be read from the blocker's side

`MustBeBlocked` and `MustBeBlockedByAll` are facts about an *attacker* — one blocker, or every
blocker able. Provoke and its relatives name both halves ("target creature blocks ~ this turn if
able"), and neither flag can say it: the first is satisfied by anyone blocking, the second compels
everyone who can. So the requirement now also exists on the blocker — `MustBlock` and
`MustBlockAttacker` — and the attacker's id travels inside the continuous effect's own id, because
an effect carries no state and the requirement has to survive until blockers are declared.

That unlocked the **provoke** keyword, and **`AbilitiesCantBeActivated`** followed the same shape:
the keyword vocabulary could already say "can't attack or block" and had no way at all to say the
clause printed beside it on the same cards, so forty-odd lines went unread for want of the shorter
half.

### An ability id has to be unique, and one card proved it

Widening equip to read "Equip legendary creature {3}" made **Blackblade Reforged** compile two
equip abilities with the same id — and the structural-soundness guard caught it on the corpus
before any test did. The board addresses an ability by its id and the engine looks it up by the
same, so the cheaper of the two would simply have been unreachable. The narrowed form now takes an
id derived from its qualifier.

### A prevention shield was only ever half a feature

"Prevent the next N damage that would be dealt to any target this turn" compiled, was legal to aim
at a player, resolved, emitted an event, and did nothing — because a shield was stored on
`GameObject.DamageToPrevent` and **`PlayerState` had no such field at all**. The replacement that
soaks damage matched `DamageMarked`, the event for damage to a *permanent*; damage to a player is
`PlayerDamaged` and never met a shield on the way past. Every check a reviewer would run said the
card was implemented; only the life total disagreed.

The other half was the same mistake read forwards. `ClearDamage` runs at cleanup and takes damage
off, and CR 514.2 ends "until end of turn" at that same moment — but it cleared `DamageMarked` and
left `DamageToPrevent` standing, so **a shield bought on one turn went on soaking damage on every
turn afterwards**. Its skip guard hid this from the obvious test: a permanent with no damage marked
was skipped entirely, which is exactly the creature whose shield had done its job.

Both are fixed, and the shape of the fix is the point:

- `PlayerState.DamageToPrevent`, with `PlayerPreventionChanged` as its event — registered in
  `EventLogSerializer` and folded in `GameReducer`, because state is a fold of the log.
- The replacement now has a `PlayerDamaged` arm beside the `DamageMarked` one, soaking the same
  way (CR 615.1: three into a shield of two still deals one).
- Cleanup clears both shields together, and the skip guard counts an unspent shield as a reason to
  visit a permanent.
- `PreventDamage.TargetIndex` is now nullable with a `PlayerScope` beside it, so "dealt to **you**"
  — nine lines in the corpus that name no target at all — is read as the scope it is.

**The generalisable lesson: an effect whose `Resolve` returns `[]` on an input it can legally be
handed is indistinguishable from a working one.** The permanent arm was written, tested and correct;
nothing anywhere asked what the same effect did with the other kind of target its own grammar
admits. Where an effect pattern-matches on `TargetKind`, the arms it does not handle are a silent
no-op, and the card still reports itself complete.

### "It" meant the wrong permanent everywhere

`SubjectObjectOf` answered "what was this event about" for exactly one event kind — the one that
records a spell's targets. Every other trigger reported nothing, and `EffectSubject.TriggerSubject`
falls back to the permanent with the ability when there is no subject. So **"whenever another
creature you control enters, put a +1/+1 counter on it" put the counter on the card that said it**,
every time, and nothing failed: the effect resolved, an event was emitted, the log replayed, the
counter was simply on the wrong creature. It now answers for the events with one unambiguous
subject — a move, a creation, a tap, a counter change, a cast — and stays silent for the ones
without: a declaration of blockers is a batch of pairs, and answering it with whichever came first
is how this class of bug starts.

Found by trying to extend the pronoun vocabulary rather than by looking for it. **Destroy, exile and
tap now take a pronoun** the way pump and counters already did, through one reader that turns a
verb's object into either a target or the thing the sentence is already about — `Subjects.Resolve`,
extracted from the copy that was inlined in the counter effect, because the order of preference
(target, then trigger subject, then source) is the whole content of the rule and a second copy is a
second chance to get it wrong.

What that extension did **not** buy is the demonstrative with nothing targeted. "Whenever ~ blocks a
creature, destroy that creature" reads its subject from a block declaration, which has none, so it
destroyed the blocker — the card kills itself. That path is reverted and the cards stay unread:
a verb that destroys the wrong permanent is worse than one that is not read at all. The corpus
structural gate caught the first shape of this before any test did.

**"One or more X" is the plural of a sentence the trigger reader already reads**, and the only
difference that survives normalising both the subject and the verb back to the singular is *how
often it fires*. CR 603.2 with CR 510.2: combat damage is dealt simultaneously, so "whenever one or
more creatures you control deal combat damage to a player" is one trigger however many connected —
reusing the singular reader unchanged would have fired it once per creature. `CombatDamageDealt`
now summarises each damage step as a single event, derived from the events it summarises so the two
cannot disagree, and folding to no state change because it exists only for triggers to read.

The plural form is **refused for the verbs that have no such event**: entering and dying are one
event each, so a plural sentence about them is left unread rather than fired several times. That
line — read it once correctly or not at all — is the same one drawn for "lost N or more life" and
for creature spells.

**A counter trigger reads which counter arrived.** The passive voice ("one or more +1/+1 counters
are put on this creature") is how a card says it does not care who did it, and it is 20 lines the
active-voice reader could not see. The named kind is tested: a stun counter does not satisfy a
sentence about +1/+1 counters, and answering from any counter at all would be the wider-number
mistake in a different suit. "One or more" needs nothing extra here, unlike the combat trigger,
because counters arrive on one permanent per event however many of them there are.

**Noncreature spells cast this turn are counted separately**, because by the time anything asks,
the spells are gone — a resolved instant is in a graveyard as a different object (CR 400.7) and a
countered one is nowhere, so the count is the only place the fact survives. Creature spells are the
difference between the two counts rather than a third one. The alternative considered and rejected
was to read "a noncreature spell" and then count every spell: that answers a narrower question with
a wider number, which is worse than leaving the line unread, and it is the failure this file exists
to prevent.

**A permanent now remembers two things about how it arrived**: whether it was kicked, and who
cast it. CR 400.7 makes a zone change a new object that remembers nothing, and CR 607.2 is the
exception these live in — an enters trigger reading "if it was kicked" is linked to the kicker paid
on the spell that became this permanent. The facts survive exactly one move, the resolution, and no
other. "You cast it" records *who* rather than *whether*, because the sentence says "you" and a
permanent an opponent cast and you have since taken is not one you cast. Both are the kind of fact
that goes missing across a zone change without anything failing, so both are proved by a test that
reaches the battlefield twice by different routes.

**Counting a permanent can name it by more than its type.** The count's noun was one lowercase
word, so "two or more tapped creatures" never matched while "a tapped creature" did — the same
question in two shapes with two answers. It is a noun phrase now, singularised on its last word
only, with the adjective handed to the target grammar that already has a vocabulary for it. Two
counts can also be compared against each other rather than against a number ("an opponent controls
more lands than you"), which the counting reader structurally could not do.

**A trigger's subject slot reads three more things about the permanent that moved**: an adjective
(`nontoken`, `legendary`, a colour), a mana value beside the power and toughness already there, and
"enters the battlefield under your control" — which is "you control … enters" written backwards and
is rewritten into that shape rather than given an alternative that would have to swallow the verb
to reach the end of the phrase. The adjectives go through the same table the cast triggers read, so
the two cannot disagree about what "colorless" means; `nontoken` joins it because CR 111.1's
distinction is recorded on the card definition, not on the object.

Four verbs joined the same family. **"Is put into a graveyard from the battlefield" is CR 700.4's
long spelling of "dies"** — 78 cards, mostly watching permanents that are not creatures, where
"dies" would be the wrong word. "From *anywhere*" is deliberately not the same verb: it counts a
discard, which never saw the battlefield. "Becomes tapped" and "becomes untapped" complete it, and
they read two differently-shaped events, because untapping is a batch (CR 502.2) and tapping is not.

**An intervening-if can now ask the source about itself** — "if this land is tapped", "if this
permanent is an enchantment". The type is read from computed characteristics rather than the
printed card, because the cards that ask are exactly the ones CR 613 layer 4 changes. Both readers
accept `~` as well as "this creature", which is the form the clause actually arrives in: the
compiler normalises self-references before the condition is parsed, and a probe fed raw oracle text
reported these as working when they were not.

**Life gained this turn is now tracked as a total, not a flag.** "If you gained life this turn" and
"if you gained 3 or more life this turn" are one watcher read to different depths, and a flag can
only answer the first. Gains are summed as they happen rather than compared against a start-of-turn
total, so gaining 3 and losing 3 still counts as having gained 3 (CR 118.5). Losing life stays a
flag, so "lost N or more" would go unread rather than be answered with the wrong number — no
printed card asks it.

A token can be created **under somebody else's control** — "each opponent creates a 1/1 Soldier",
"target player creates a Treasure token" — which is 76 printed cards and the last verb that could
not say who it was about. Four effects now answer that question identically: a named player is a
target and is chosen as the spell is cast; a group is not; and a named target wins outright over a
group.

**"For each" and "equal to the number of" are the same amount in two wordings**, and each verb
knew only one of them. Drawing read the second and not the first; gaining life read the first and
not the second. Both now read both, and the life change takes a scope the way drawing and
discarding already did, so "each opponent loses 1 life for each creature you control" works.

Three effects now resolve who they are about the same way: a named target wins outright over a
scope, because the sentence named one player and the scope named none.

**The "for each" tail is one shared pattern now**, and a test asks every verb that reads a number
whether it reads the tail: drawing, life, damage, milling, discarding, counters, tokens. It was
written into four of them one at a time, each on a different day and each a separate round of
work — the fifth would have been another. Counters alone were 70 printed cards, and nothing had
noticed because each verb was correct in isolation.

Putting a counter takes a **subject** the way regeneration does — the source, a target, an Aura's
host, or what the trigger was about. "Put a +1/+1 counter on it" is 397 printed lines and "on that
creature" another 78.

**"It" is one word with three answers, and the sentence cannot settle which.** It means the target
the sentence before it chose; failing that, what the trigger was about; failing that, the thing
with the ability. In that order — and each step is a real card. "Target creature you control gains
flying until end of turn. Put a +1/+1 counter on it" needs the first, "whenever a creature you
control dies, put a counter on it" the second, and "whenever you gain life, put a +1/+1 counter on
it" the third.

It used to mean the third unconditionally, so the first of those put its counter on itself and
left the creature it had just pumped alone.

The same fault was in the pump verb: "untap target creature. **It gets +2/+2** until end of turn"
grew the card that said so rather than the creature it had just untapped. Both verbs now resolve
the word the same way, which is the point of writing the order down — the next verb that reads
"it" has a rule to follow instead of a precedent to copy.

**A step trigger is about the player whose step it is** (CR 500.1). It had no subject at all, so
"at the beginning of each player's draw step, that player draws an additional card" fell back to
whoever controlled the enchantment — the card would have drawn its controller an extra card on
every player's turn instead of drawing each player their own. Drawing also takes a scope now, the
way discarding already did.

**"Discard your hand"** — 118 printed cards — is a count that cannot be worked out when the card
compiles, because it is a different number for each player being asked. The discard effect already
knew what to do with a hand smaller than the count it was given; it needed a mode that asks the
hand how big it is.

**An Aura can enchant a player** (CR 303.4a). It could not before, and the reason was that a
player is not an object and has no `ObjectId` — so there was nowhere to record the answer.
`Enchant player` was refused for naming a player rather than a permanent, and had it not been,
the state-based action that buries an Aura attached to nothing would have buried it on the spot,
because it asked whether the Aura was attached to a *permanent* still on the battlefield. Three
places, one missing idea; a player does not leave the battlefield, so the only question for one of
these is whether that player is still in the game.

The step vocabulary also reads "your combat step", "your opponents' end step", "each of your
postcombat main phases" and "enchanted player's upkeep".

A sweep for the same shape elsewhere — an unrecognised entry silently dropped from a list that
then means something permissive — found one more, in trigger ordering rather than in card text.
A trigger whose key the player's chosen order did not name was dropped from the batch entirely,
so the ability never reached the stack. CR 603.3b gives the player the order they go on in, not
whether they go on at all; anything the answer does not name now follows the ones it did.

**A cast trigger naming a word the engine did not know fired on every spell.** The kind was turned
into a list of card types and unknown words were dropped, and an empty list meant "any". So
"whenever you cast a Goblin spell" triggered on Elves, and 193 printed cast triggers named a word
that is not a card type: the five colours, "multicolored", "historic", "legendary", and subtypes.

The refusal came first and cost 126 cards, which was the honest number; then subtypes and the
describing words were read, which took it past where it started. An unrecognised word now leaves
the trigger unread.

**A cast now says where the spell came from**, so "whenever you cast a spell from anywhere other
than your hand" reads. The zone is noted before the card moves, because that move is what destroys
the answer: the object that was in the graveyard stops existing the moment it leaves (CR 400.7).
This was identified as a missing event field many rounds before it was worth building, and it is
the third time the fix has been "the event should carry the fact the rules ask about" — after how
much an event was for, and whether damage was combat damage.

**`DamageMarked` now says whether it was combat damage**, the way `PlayerDamaged` already did. The
rules distinguish combat damage from every other kind (CR 510.2) and cards ask about the
difference, so the event carries it rather than leaving it to be guessed from which step the game
is in — a spell dealing damage during the combat damage step would answer that guess wrongly.
"Whenever a creature you control deals combat damage", with no recipient named, reads because of
it.

The zone-change trigger family also reads **"becomes blocked"** and **"blocks"** for a group, which
it had for the source alone. One trigger fires per declaration however many creatures qualify —
the same simplification the combat-damage verb already makes, since the predicate answers yes or
no and a declaration is one event. Worth naming because a card that says "whenever a creature you
control becomes blocked" and has three blocked creatures should trigger three times.

The **qualifier on a trigger's subject** is one slot that reads four things now — "with power 4 or
greater", "with toughness 4 or greater", "with flying", "with a +1/+1 counter on it" — rather than
four matchers that would each have to agree about what "a creature you control" means. The
zone-change family also learned "leaves the battlefield", which it had for the source and not for
a group.

Tokens arrive **tapped** when the card says so, and are **counted off the board** — "a 1/1 Soldier
token for each artifact you control" — through the same counting the pumps and the discounts use.

The counted form exposed a floor that had been right for every previous caller and is wrong for
this one: "create a token" with no number means one, so an unset count became one. A count that
counts to *nothing* now makes nothing, because flooring that at one is a card doing something it
does not say.

The shared filter vocabulary reads **colours and supertypes** now, so "a green creature card", "a
basic Forest card" and "a legendary creature card" are words joined with the conjunction it
already had rather than three phrases each needing a reader. A phrase with one unrecognised word
in it is refused whole rather than fetched from the parts that happened to be understood.

A search may also put what it found **into a graveyard**, and its filter may contain commas — "a
Plains, Island, Swamp, or Mountain card" was refused for punctuation the list grammar could
already read.

**Cost reductions that count** — "costs {1} less to cast for each creature card in your
graveyard" — and **cost reductions with a condition** — "costs {1} less to cast if you control a
Wizard". The discount that depends on the spell's own targets already worked; these two are the
same discount asked of the board instead, and they count through the same reader the defining
abilities use, so a graveyard, a hand, a board or a life total all work here without this knowing
how any of them are counted.

The discount is clamped at zero and not at the spell's cost. A reduction larger than the cost
simply pays all the generic there is (CR 601.2f), and where that is decided is the payment code,
not here.

An Aura's buff and its rider are one sentence and are read as one. The pattern knew
"gets +N/+N **and has** [keyword]" and, separately, "can't block" standing alone — but not the two
joined, which is how the cards print it. The flags for all of it already existed; only the
sentence could not reach them.

That gap was found by taking the *cluster* the work queue points at, pulling every distinct tail
of it out of the corpus, and compiling them. A cluster says where to look; the tails say what to
fix.

**Regeneration** was read for one subject out of three. "Regenerate this creature" worked;
"regenerate target creature" and "regenerate enchanted creature" — 54 printed lines between them —
did not, and the shielding itself was already there. It is now one effect that names its subject
rather than three effects that each have to get regeneration right, and the "what am I attached
to" lookup it needs is shared with the pump that already needed it.

**Turning a printed plural into a creature type is one function now, and it used to be three.**
The weakest of them stripped a trailing "s" and nothing else, so a lord reading "other Elves you
control get +1/+1" named the creature type **"Elve"** — found none however many Elves were on the
battlefield, compiled perfectly, and buffed nothing. Fifteen printed lord lines were misread that
way: Elves, Dwarves, Allies, Heroes, Mice.

English is irregular enough that the shared version is a list and not a rule, and the same
function had already been written once for board conditions after "you control no Islands" looked
for the subtype "Islands". Three copies, three different amounts of knowledge, one silent failure
each.

**"That many"** (CR 603.2) — "whenever this deals combat damage to a player, draw that many
cards", and 469 printed lines that say it. A trigger already carried which *player* and which
*object* the event was about; it now carries **how much** as well, alongside them and derived the
same way.

It has to travel with the trigger rather than be looked up on resolution, because by then the
event is over: the damage has been marked and netted against other damage, the life total has
moved on. How much it was is a fact about the event, not about the state afterwards. An event with
no number worth referring back to gives none, and a card saying "that many" about one of those
goes unread rather than drawing zero — which would look like a card that does nothing rather than
one the engine cannot play.

"That many" and "that **much**" are the same word in two grammars — cards are counted, life is
measured — so they are one question and not two spellings. Putting them in the shared count
vocabulary meant every verb that already read a number got them at once: drawing, gaining life,
putting counters, creating tokens. The one verb that missed out had spelled its own numbers
instead of using that vocabulary, which is the same drift in miniature.

**"Skip your draw step"** (CR 504.1) is the fourth rule in a row that had its answer written into
it. The pattern is worth stating once: the hand limit, the land drop, the mana a mana ability
produces, and now the draw step were all constants where the board should have been asked. Each
one is a card that compiles to a static nothing reads, sits on the battlefield, and does nothing
while looking implemented.

A sweep for the other half of that pattern — one vocabulary written down in more than one place —
now finds three repeated word-lists across every compiled pattern, all small and none of them
disagreeing. The four-way drift in what an Aura calls what it is attached to was the last one that
mattered.

**What an Aura calls the thing it is attached to is now one vocabulary.** It had been four: the
buff line read only "creature", the untap restriction read only "creature" *and then repeated the
list a second time inside its own method body*, the granted-ability line had already learned
"land" and "permanent", and `Enchant` had a switch that collapsed everything except creature into
"any permanent". A card reading "Enchant land / Enchanted land doesn't untap during its
controller's untap step" was therefore half understood — the first line read, the second not — and
compiled to a land tax that never taxed anything.

The untap restriction was also asking whether its target was a creature, which is the same
mistake once more: what the Aura is on was settled when it was cast, and asking again is how an
Aura on a land came to hold nothing down.

**"You may play an additional land on each of your turns"** works, and the land drop is now
counted rather than assumed — one plus whatever the battlefield adds (CR 305.2). It is the third
limit in a row that turned out to be a number written into a rule: the hand limit at cleanup, the
land drop, and before them the mana that a mana ability was assumed to produce. All three have the
same shape of bug, and all three now ask the board instead.

**"You may choose not to untap this during your untap step"** (CR 502.3) is a decision the untap
step makes, not a restriction on it — so the permanents that offer it are held back and asked
about at the settle that follows, while everything else untaps at once. Untapping is simultaneous
(CR 502.2), and waiting for an answer before untapping the rest would split it. One question
covers all of them: a player with four of these should not be asked four times.

The chosen value is now used as well as stored: **lords that name a tribe or a colour**
("creatures you control of the chosen type get +1/+1") and **lands that tap for it** ("{T}: Add one
mana of the chosen color"). The mana one needed a flag rather than a colour, because
`ManaProduction.Color` being null already means *colourless* — a real kind of mana and not an
absence (CR 106.1b) — so there was no spare value to mean "ask later". A permanent that has not
answered its question taps for **nothing**, not for colourless.

A permanent can **name a colour or a creature type as it enters** (CR 614.12) — 93 cards say so
and nothing in the engine could hold the answer. It is one field and not one per kind, because a
permanent chooses at most one thing and the card says which; and it stays with the object, so a
permanent that leaves and returns is a new object (CR 400.7) and chooses again, which is what
those cards intend.

"Creatures of the chosen type get +1/+1" reads the tribe **off the source when the effect
applies**, not off the card when it compiles — the same rule as every other computed
characteristic. Until the question is answered the lord buffs nothing, which is the honest answer
rather than everything.

**The deviation, named:** the choice is asked at the next settle rather than in the middle of the
permanent arriving. The rules say "as it enters" and this is "immediately after". Nothing can act
in between, since a settle runs before any player receives priority, so the only thing that could
tell the difference is another ability resolving simultaneously — and none of the cards that
choose this way has one.

**"Target artifact or enchantment"** reads now — 580 printed occurrences of a compound target that
the battlefield grammar could not express, though the graveyard grammar had been able to for a
while. The two lists mean opposite things and are held apart for that reason: `PermanentTypes`
says what a permanent must be *all* of, `EitherPermanentType` says what it may be *any* of, and
returning a pair from one method would make every caller guess which it had been handed.

The same change fixed an Aura bug that had nothing to do with compounds. "Enchant" collapsed every
subject except "creature" into "any permanent", so an Aura reading **"enchant land" would go on a
creature** quite happily. It now goes through the same target grammar as everything else.

**"You have no maximum hand size"** works, and the hand limit is now asked rather than assumed
(CR 402.2). Cleanup had a seven written into it, so the card compiled to a static nothing read and
its controller discarded anyway. It is computed for the same reason every other characteristic is:
the answer changes when a permanent enters or leaves, and a number written into the cleanup step
goes on being seven while the card that says otherwise sits on the battlefield doing nothing.

The gaps in this area were found by **compiling the corpus's most common lines and looking at what
missed** — which also produced two false alarms worth recording: a bare effect sentence needs an
instant or sorcery to belong to, and a bare keyword line ("Flying, deathtouch") is read from the
card's keyword field rather than from its text, so both look unread on a synthetic card that is
neither.

A **choice on resolution** now knows which zone it is asking about. "Sacrifice a creature" looks
at the battlefield and "return a creature card from your graveyard to your hand" is the same
question asked of a graveyard, so `ChooseAndMove` carries the zone rather than a second effect
carrying a second copy of the choice machinery. The control test belongs only to the first: a
graveyard is already one player's, and asking who controls a card in it is not a question the
rules ask (CR 108.4).

Two places had the battlefield written into them and only one was obvious. The half that offers
the options was easy to find; the half that *applies* the answer checked `chosen.Zone ==
Zone.Battlefield` before moving anything, so the question was asked, answered, and silently
dropped.

**A filter is a filter and a prompt is words.** They were one field on `HandChoiceRequested` until
a card asked for "an instant or sorcery card": the engine decided what a player could pick by
looking for the substring `"nonland"` in the sentence it was about to show them, so every kind
that was not nonland silently offered the whole hand. The filter is now the shared search
vocabulary — the one that already reads `|`, `&` and a `non` prefix — and the prompt is only what
the player reads.

That change also found the two halves of that vocabulary disagreeing: `SearchFilters.Matches` had
understood a `non` prefix since compound filters were added, and the compiler-side validator that
decides whether the words are acceptable at all had never been told.

"Deals damage equal to its power to target creature" is **half a fight**, and it is read as one:
a flag on `Fight` rather than a second effect. Whose power, computed as it resolves rather than
read off the card, and a source named on the damage so deathtouch and lifelink still see who dealt
it — all of that is the same question, and a separate effect would have to get every part of it
right again.

**Two rules keep this from becoming a pile of card templates.**

*A clause the compiler cannot read makes the whole line unread.* Never the clause dropped and the
rest kept — a card that does most of what it says is worse than one the engine admits it cannot
play, because nothing downstream can tell the difference. The mana-ability reader broke this rule
by swallowing a trailing sentence, and it is the most expensive bug this feature has had.

*A question asked by two effects gets a name, not a second copy.* "Its controller loses 2 life"
and "its controller draws a card" are one clause with two verbs, so they share one matcher and one
`TargetOwnership.ControllerOf` — because the awkward part is shared too: the target may be gone by
the time the clause runs, destroyed a sentence earlier, and be a card in a graveyard under a new
id (CR 400.7). Written twice, the second copy is where that arm gets forgotten. `Drawing.From` is
shared for the same reason: the empty-library arm is the part a second copy would omit.

**Flicker** — "exile target creature you control, then return that card to the battlefield under
its owner's control" — is one effect and not two, because what comes back is a *different object*
and a separate return could not name it: the id the exile produced stops existing the moment the
card leaves. It is also read before the text is split into sentences, since the split happens on
", then" and this phrase is written across one; split, the second half is a sentence about a card
that no longer exists.

What returns has no counters, no Auras, no damage and none of the abilities anything gave it, and
it is summoning-sick again. None of that is arranged: it is simply what changing zones means, and
it is the whole reason these cards are played — the permanent *enters*, so everything watching for
that triggers again.

The cards saying "under **your** control" are a theft wearing the same sentence and are not read
here, because reading them as a flicker would quietly hand the permanent back to the player it was
taken from.

One missing synonym was worth 33 cards on its own: the search grammar knew "put **it** into your
hand" and not "put **that card** into your hand", which 82 printed cards say.

"~ deals 3 damage to any target **and 2 damage to you**" is read whole rather than split on the
"and", because the second half is not a sentence — "2 damage to you" has no verb and nothing would
take it. And "you" is not a target: a spell that damages its caster does not target them, so
reading it as one would let the damage be redirected and let the spell be countered for it.

**Damage divided as you choose** (CR 601.2d) is announced as the spell is *cast*, not chosen as
it resolves, and the engine now says so: the division rides on the stack object beside the targets
and is passed to `CastSpell` the way {X} already is. That is not a detail. An opponent deciding
whether to respond is entitled to know which of their creatures is about to take three and which
is about to take one, and an effect that asked on resolution would be a different card.

Three announcement rules are enforced, and each one is a card that would otherwise be better than
it is printed: the total must be exactly what the spell deals, every chosen target must be given
at least one, and nothing may be given to a target that was not chosen. Without the middle rule a
player could name three targets, give two of them nothing, and use a divided spell to hit one
creature while appearing to spread it.

The hub carries the division too, so the feature is reachable over the wire rather than only from
a test. The engine checks it against what the spell deals; the hub checks that the numbers are
amounts of damage at all, because a **negative** share would let a division sum to the right total
while healing its target — the sort of thing a client can send and the rules never contemplate.

"One, two, or three targets" compiles to three specs of which only the first is required, which is
what the phrase says. **"Any number of targets" is left unread**: a spell whose target list has no
length cannot be expressed by a fixed list of specs, and giving it an arbitrary ceiling would be a
different card whenever the ceiling mattered.

**The remaining work is a genuine long tail, and the work queue now says so.** It ranks blocked
lines, and that ranking went flat once the big families were read — everything left is six or
seven cards. So it also clusters those lines by their opening words and drills into the biggest
clusters. The answer that view gives is worth having plainly: "At the beginning of..." blocks 904
cards, and they are ~900 *distinct* sentences of two to six cards each. There is no large family
left to find; there is a very long list of small ones.

Searching **by name** — "search your library for a card named ~" — reads now. The tilde is the
card's own name and the parser has no name to put there, because it reads a sentence and not a
card, so it is filled in when the effect resolves and what reaches the log names the card
outright — which is what makes a replayed search look for the same thing.

**Characteristic-defining power and toughness** — "~'s power and toughness are each equal to the
number of lands you control" — reads now, at layer **7a** and not 7b (CR 613.4a). The difference
shows: a creature defined this way and then *set* to 3/3 is a 3/3, because defining happens first
and setting overwrites it, while the other order would make the definition win and the setting do
nothing. A +1/+1 counter is 7c and so modifies whatever the definition produced.

It counts a hand, a graveyard or a life total as readily as the battlefield, and the zones are
separate arms rather than one vocabulary because they are separate questions: the battlefield is
filtered by the target grammar, which knows about control and computed types; a hand or graveyard
is a pile of cards filtered by the search vocabulary, where control does not come into it.

A gate asserts that **every target an ability asks for is used by something**. A target nothing
reads is a card that stops the game, makes a player choose a creature, and then does nothing to
it — it compiles, it is legal, and it plays as a blank. It found eleven cards immediately: a spell
printing the same sentence on three lines ("target creature gets +3/+3 until end of turn", three
times) added three targets and pointed all three pumps at the first, so one creature became a
10/10 and the other two were asked for and ignored. Each line's effects are now shifted by the
targets already gathered, the way every other folded clause already was.

An Aura's target is exempt and that is not a hole: "enchant creature" makes the spell target, and
what it does with that target is become attached to it as the spell resolves (CR 303.4). There is
no effect to find because there is no effect.

**Mayhem** (CR 702.181a) is flashback with a condition and without the exile, and the condition is
the card: cast this way it goes back to the graveyard afterwards like any other spell, so without
"only if you discarded it this turn" it would be castable out of the graveyard for ever after.
The turn is stamped on the object in the graveyard — the way foretell and plot already stamp
theirs — rather than kept in a list, because the card in the graveyard is a new object (CR 400.7)
and the fact belongs to it. A card discarded, returned to hand and discarded again carries the
second discard, which is the answer the question wants.

"If that spell is countered this way, **exile it instead** of putting it into its owner's
graveyard" reads as a rider that rewrites the counter it follows, not as a second effect. Two
effects would counter the spell and then reach for a card the first one had already moved: the
countered spell is a new object in its new zone (CR 400.7), so the exile would find nothing and
the rider would be silently lost — which is the difference every card that recurs from a
graveyard cares about.

A third gate **runs** what the other two only read: every compiled card's statics are computed
with the permanent on the battlefield, and every trigger predicate is asked about fifteen kinds of
event — including the ones it is not watching for, which is where the answers go wrong. A
predicate is handed whatever happened, so "the attacking creature's controller" asked about a step
beginning has no attacker to follow, and reaching for one is a crash mid-game. 4,340 cards pass.

Cards that **buy themselves back** — "{4}{B}: Return this from your graveyard to the battlefield
tapped" — read now, and the ability functions from the graveyard rather than the battlefield.
Left at the default it would compile perfectly and never once be offered to a player, which is the
failure this engine keeps producing when a card reads correctly and plays as nothing.

A gate now asserts that **every subtype a compiled filter names is a subtype some card has**.
A filter naming nothing is silent — the search offers no cards, the dig finds nothing, and the
card plays as a blank while compiling perfectly — and it has now caused two separate bugs, so it
is checked rather than watched for. It found one the moment it was written: Frontier Seeker's
"a Mount creature card or a Plains card" was being split into `Mount creature card` and
`a Plains`, neither of which anything answers to. Filter lists are now read part by part through
the one reader that knows what a filter may say, and "Mount creature" reads as the conjunction it
is. Seven cards left the "complete" column with the phrases that cannot be validated.

**State triggers** exist now (CR 603.8) — the engine had none. "When you control no Islands,
sacrifice this creature" watches no event: nothing has to happen for it to be true, and it is just
as true if the last Island left three turns ago. So it is asked wherever state-based actions are
checked, which is wherever a player would receive priority.

Firing *once* per condition is the whole difficulty, and it is a hang rather than a wrong answer
if it is got wrong: a trigger whose resolution does not clear its own condition would fire on
every settle for the rest of the game. So the armed set lives in `GameState` and is changed by a
`StateTriggerArmed` event — not in the engine, because the state is a fold of the log and an
engine-side set would be rebuilt empty on replay, firing every state trigger in the game a second
time.

The shared filter vocabulary reads **negation** (`nonland`) and **conjunction** (`a&b`) alongside
the alternation (`a|b`) it already had, because the two lists join differently and the difference
is the point: "a creature or land card" is a card that answers to *either*, "a noncreature,
nonland card" is one that answers to *both*. A mixed list is left unread — English joins those
with a meaning that depends on the sentence.

**A mana ability that produces no mana is now a build failure**, checked over the whole corpus by
`CardCompilerInvariantTests`. It is the one ability whose entire purpose sits in a single field,
so an empty one is silent: the card compiles, reads as complete, offers the player a button, and
taps for nothing. That is how the swallowed-rider bug above stayed invisible, and the gate is
there so the next one cannot.

Targets read **colour negations** — "destroy target nonblack creature" — computed rather than
printed, so a creature an effect has turned black stops being a legal target (CR 105.2, 613.1e).
The case worth stating because it reads wrong at a glance: a *colourless* creature is nonblack.
The question is whether black is among its colours, not whether it has a colour at all.

**That figure went down, on purpose.** The mana-ability reader ran to the end of the line, so
"{T}: Add {C}{C}. This land doesn't untap during your next untap step" arrived with the second
sentence inside the mana phrase. The words after the full stop are not mana, the reader shrugged
them off, and the ability compiled with the rider silently gone — sometimes producing the right
mana and skipping the price, sometimes producing *no mana at all* while still counting as a card
the engine could play. It now stops at the sentence boundary, reads the tail as the ability's
effects (CR 605.3b lets a mana ability do something other than make mana), and refuses the line
when the tail is not readable. Around sixty cards left the "complete" column for the honest
reason that they were never complete: mostly "Spend this mana only to cast creature spells".

**Restricted mana** (CR 106.6) then won most of them back honestly. A pool holds restricted mana
apart from ordinary mana, one entry per mana, because the restriction travels with the individual
mana and not with its colour — a player can hold one green that pays for anything and one green
that pays only for a creature spell, and summing them into "two green" loses the only fact that
matters when the bill arrives. Payment now knows what it is paying for (a spell of these types, or
an ability of this permanent) and **spends restricted mana first**: it buys fewer things than
ordinary mana, so spending it last would routinely leave a player holding mana they were allowed
to use and a cost they could no longer meet.

The board does not render it. `PlayerView.RestrictedMana` carries the symbol and the restriction in
words, deliberately *beside* `ManaPool` rather than inside it — a client that added the two together
would tell a player they could pay for something they cannot.

**Checked rather than assumed**: `mtg-client/src` references `manaPool` in several components and
`restrictedMana` in none, so the board shows only the unrestricted pool. The client is a sibling
repo at `../mtg-client` and it is *present*, with its own Selenium harness under `e2e/` — an earlier
note here implied it was out of reach and that was wrong. Restricted mana is engine-only because the
client half has not been written, not because it could not be looked at.

A continuous effect's `Apply` is now handed the **source**, which `Applies` had from the start.
The asymmetry was invisible until a static had to count something: "you" in "gets +1/+1 for each
artifact you control" is whoever controls the *ability*, and an Aura on an opponent's creature is
where that stops being whoever controls the thing it changes. Reading the affected object's
controller there counts the wrong player's board, and no existing effect had asked, so nothing
had caught it.

Zone-change triggers took a power bound — "**a creature you control with power 3 or greater
enters**" — and it is read off the **printed** card rather than computed. A trigger predicate is
handed the state and no ability source, and characteristics need one; the two answers differ for a
creature that enters under a lord or with counters. That is a deviation worth naming rather than a
reason to leave thirty-seven cards unread, and it is named here.

"**Whenever you cycle or discard a card**" is one event rather than two, and choosing which is the
whole of getting it right: cycling discards the card as its cost (CR 702.29c), so the discard
already covers both halves. Watching the activation as well would count a single cycle twice, and
a card that triggers twice as often is a different card from the printed one. Its test asserts one
life for the cycle, not two.

The cast trigger learned a mana-value bound — "**a spell with mana value 3 or greater**" — as a
filter on the card rather than on who cast it, read off the printed cost the way every other
mana-value question is. Both directions are read, because "or less" is the same sentence.

"**A Samurai or Warrior you control attacks alone**" is that same count with the sole attacker
having to answer to a description instead of being this card, and the description is read by the
filter vocabulary tutors and digs already share — which is what makes "Samurai or Warrior" two
filters rather than a phrase needing its own reader. "**~ enters or leaves the battlefield**" is
the enters-or-dies shape with the second half widened: leaving is any departure, so exiled and
bounced count, not only died.

Two more attack triggers came free from the same event. Attackers are declared as one batch
(CR 508.1), so the declaration already carries every attacker — "**~ and at least two other
creatures attack**" and "**~ attacks alone**" are that one event counted two different ways, and
what was missing was only the sentences asking for it.

An Aura's clock runs on **its host's** controller: "at the beginning of the upkeep of enchanted
creature's controller" is not "your upkeep", and the two differ exactly when the Aura is on
somebody else's creature — which is what most of them are for. Worth noting how it was nearly
missed: the phrase was added in the wrong word order first, because the queue's shape reads
naturally as "enchanted creature's controller's upkeep" and no card prints it that way. Counting
the corpus for the real wording took one command and would have been wasted work otherwise.

**"Whenever you cast or copy a spell"** is two events for one sentence: a copy is *put on* the
stack without being cast (CR 707.10), so nothing about the cast event sees it. Both are read into
the same shape — who did it, and which card — so everything after that is asked once.

Writing its test found an engine fault underneath. One ability can trigger twice at once, and both
waiting triggers are then the same source and the same ability, so the ordering question offered
two options with the same id — and every possible answer repeated a pick, which the engine refuses.
The question was unanswerable, and the game could not continue. Any order of two identical triggers
is the same order, so it is no longer asked: the order they arrived in is used. A player still
chooses whenever the choice could matter.

The trigger vocabulary took the same treatment. "A creature you control **deals combat damage to a
player**" belongs to the group family that already read "a creature you control enters" and
"...dies" — everything after the subject is worked out is shared, since the type, the tribe and
which side controls it are the same questions whether the creature arrived, died, or connected. It
needed one more branch for an event that is not a zone change, not a trigger of its own.

**"When you cycle this card"** then needed an ordering fix in the engine, and finding it took the
test. Cycling is an activated ability with a known id, so the trigger is just its activation — but
the activation was announced *after* the self-cost was paid, and that cost discards the card. By
the time anything could watch for the event, the id it carried named nothing: the card had become
a different object in the graveyard (CR 400.7). The announcement now comes first. The cost is
still paid; only the order of saying so changed, and a trigger that compiled cleanly and never
fired now fires.

**"Up to one target"** is on more than six hundred cards and belongs in `Specs.Parse`, not in the
verbs. The first attempt put it in the multi-target rewrite beside "up to two", and it did nothing:
the damage matcher runs first and swallows the whole phrase, so the target arrived as "up to one
target creature" and parsed as an ordinary, *required* one. Reading the prefix inside the target
grammar fixes every verb at once, because they all reach a target the same way — which is the same
argument the qualifier layer settled a long time ago. The first attempt also left a rewrite
behind that had to be taken back out: with "up to one" taught to the multi-target rewrite *and* to
the target grammar, the rewrite began claiming any sentence that merely **contained** the phrase and
swallowing the conjunction around it — "put a counter on it and tap up to one target creature an
opponent controls" stopped compiling. Removing it cost four cards and fixed the composition, which
is the better trade. The bisect that found it also produced a **wrong conclusion worth recording**: it reported that
"up to one target" failed as a whole spell line, and that was the test's fault, not the compiler's
— the lines were being compiled onto a *creature*, where a bare effect sentence has nowhere to go
and is unhandled for a reason that has nothing to do with the phrase. Compiled as the instants they
are, every shape reads, and
`Up_to_one_target_reads_in_every_shape_it_is_printed_in` now says so. The card type is part of the
question, and a probe that gets it wrong will blame the thing it is pointed at. **"Another target"** joined it immediately
after, as a prefix in the same place, carrying the source-aware filter that leaves the source out —
and, as with the defending-player phrase, that filter has to follow an ability on the stack back to
the permanent it came from, or the exclusion holds while the target is chosen and lapses when it
resolves.

Measuring the family first was what made these two worth doing and stopped two others: "target
attacking creature" (307 cards) and "target creature or planeswalker" (209) both looked like gaps
and are already read — the adjective vocabulary and the either-noun rule cover them. Counting the
corpus is cheap; writing a matcher for something already supported is not.

**"You may look at the top card of your library any time"** is 47 cards and the first line the
engine answers with a *view* rather than an effect. Nothing happens when it is granted; what
changes is what one player is shown. So it is answered in `PlayerViewProjector` — which is also the
only place it can be answered, since a library is hidden from everyone including its owner
(CR 401.2) and the whole point of the line is that it stops being hidden from one of them. The
permission is asked of the battlefield rather than remembered on the player, so a card that leaves
takes the view with it and nothing has to notice. It moved the count by one card, because these
almost always pair it with "you may play lands from the top of your library" — the tests are the
evidence here, not the number, and the one that matters asserts the *opponent* still sees null.

**"Creature defending player controls"** is on 335 cards and could not be said at all. Who that is
depends on which combat the source is in, so it cannot be an ordinary owner filter — those are
handed the state and the object and nothing about who is asking. It is a `SourceFilter`, which is
exactly what that second delegate exists for. The part worth remembering is which object counts as
the source, because it changes between the two moments the question is asked: as the ability goes
on the stack the source is the permanent, and as it resolves the source is the ability, whose own
source is the permanent (CR 405.4). Looking only at the first made every one of these targets legal
to choose and illegal to resolve — the ability went on the stack, sat there, and did nothing.

**Cost reducers** are new engine capability rather than another template: "Creature spells you cast
cost {1} less to cast" is a discount a *permanent* gives its controller's spells, and the only
reduction the engine had was a card's own. It is deliberately not a continuous effect — what a
spell costs is worked out once as it is cast (CR 601.2f) and never recomputed — so a `CostReducer`
sits beside the abilities and is read at cast time from whatever the caster controls then. It comes
off the generic part only, using the same `WithoutGeneric` that assist needed; a reduction cannot
pay a coloured pip, and letting it would make a Dragon castable off two Islands. The filter is the
vocabulary tutors and digs already share, which is what lets "instant **and** sorcery" be two
filters instead of a phrase needing its own reader. 111 cards print one; 48 of them finished.

The work queue was ranking noise on a scale worth naming. It reported **every** failing sentence
in an unread line, and the compiler stops at the first one — so every sentence after the real
blocker was counted as a blocker too. Riders were the worst of it: "It can't be regenerated" sat
near the top as the obstacle on twelve cards and had been implemented for a long time, and the
duplicate implementation written before noticing is the evidence for how convincing the ranking
was. Stopping at the first failure, exactly as the compiler does, dropped the blocked count from
8,270 to 6,103 and left a list whose top entries are all real.

The mana-ability path is worth naming as the shape these wins keep taking. It had its own small
cost reader — lift a self-sacrifice out by hand, then demand the remainder be payable mana — while
`ReadCost`, which every other activated ability uses, already understood counters, life, energy,
sacrifice, discard and tapping another creature. Pointing the one at the other was a dozen lines
and finished **48 cards**, none of which needed anything the engine could not already express.
The lesson is the same one the target-qualifier layer taught: when a family will not compile, look
for the vocabulary it is being kept away from before writing a new one. It kept paying in the same
sitting. A bare "shuffle" — the modern wording, where the older printings said "shuffle your
library" — was one word standing in front of 189 cards. The look-at-the-top family was anchored to
the end of its line, so a card that added "You lose 2 life." after the dig failed entirely rather
than reading the dig and then the life; and the same family had no filter, while searching had
carried one all along, so "you may reveal a **creature** card from among them" went unread until
`LookAndTake` was handed `SearchFilters`. And "**When** you do" — the reflexive-trigger wording
(CR 603.11) — was simply absent from a matcher that knew "**If** you do", which is 43 cards whose
effect the engine could already run. That one is a real deviation and is taken deliberately: the
rules put a reflexive ability on the stack, where it can be responded to between the payment and
what it buys, and here it happens as the payment does.

None of this was findable until the work queue printed **an example beside each shape**. "you may
pay {M}" as a bare shape says nothing; the same line with `Whenever you attack, you may pay
{1}{R}. When you do, target creature can't block this turn.` beside it says exactly which word is
missing. Fixing the instrument came first, again.

Reading "you may sacrifice another creature" then turned up two engine bugs behind it, and both
were the same shape — a path asking half of what it had.

- **A deferred branch lost which ability it belonged to.** An effect that has to be found again
  later — one that asks a question and is located by (source, ability, index) when the answer
  arrives — runs from the branch *after* the ability that raised it has left the stack. It asked
  the state for the ability id, got the permanent, and the locator then searched the card's spell
  effects instead of the ability's. The request was dropped **in silence**: no question, no error,
  the branch simply did not happen. `ResolutionContext` now carries `AbilityId`, and the four
  effects that were asking the state prefer it.
- **Two paths consulted `ObjectFilter` and not `SourceFilter`.** A `TargetSpec` carries both, and
  "another creature" lives entirely in the second — so the offer listed the source's own name and a
  card could sacrifice itself to an ability saying it may not. `ToEachPermanent` had it too, and it
  put a +1/+1 counter on the very permanent whose ability said to leave itself out. A third effect
  had been doing both all along, with a comment saying why; the others simply had not followed it.
  Any new consumer of a spec has to ask both, and there is nothing that makes it.

Neither is visible from a compile. Both were found by writing a behaviour test for a family that
had just started compiling, which is the whole argument for writing one.

The queue itself needed the same treatment once more. It tested every sentence **standing alone**,
and some sentences are only ever riders — "It's still a land" says nothing by itself and is
accepted after the animation it qualifies. So it sat near the top of the ranking as the blocker of
41 cards the compiler had read all along, and the real blocker was the sentence before it. Asking
a second time with a harmless prefix tells the two apart, and doing so removed 86 phantom entries.

Energy was the last of this batch: `{E}` is a counter, not a mana symbol (CR 107.4c), and an
offer priced in it was parsing `{E}{E}` as mana and asking the pool for two of a colour called E.
`MayPay` now carries an energy cost beside its mana one, checked before the question is asked and
charged when the answer is yes. Life joined it for the same reason: "you may pay 2 life" is a
price, and the free branch was trying to read it as something that *happens*, which failed and
took the whole offer down with it. Both are checked before the question is put, so an offer that
cannot be paid is never made — and life is a floor rather than a margin, since it may be paid down
to zero and no further (CR 118.4).

A filter can now name more than one kind of card — "a creature **or land** card", "an Elf,
Warrior, **or** Tyvar card" (CR 109.4). It is one string with a separator rather than a list,
because a filter id travels in an event and has to be something a log can carry, and because
every place that reads one keeps working without knowing there are now two. Both halves of the
rule are enforced: a list with an unreadable part is refused outright rather than searched from
the parts that were understood, since a tutor that finds *less* than the card allows is as wrong
as one that finds more. Digging and searching share it, and it finished 46 cards.

"Shuffle and put that card on top" needed two things, and the second was an ordering bug rather
than a reading one. It arrives as a sentence of its own — the splitter separates on ", then" — and
it says where the card the sentence *before* it found ends up, so it amends that search instead of
adding an effect; there is no other way to say "that card", and reading it as a bare shuffle would
leave the tutor putting its find in hand. Then the search itself had to shuffle **first** and place
the card afterwards. Doing the move first and shuffling after folds the card back in at random,
which is the opposite of what the card says and looks identical from every angle except the one
that matters. Every
number here comes from `CardCompilerCoverageTests`; none of it is an estimate.

A second instrument now asks the question coverage cannot: `Cards_that_were_read_in_full_and_still_do_nothing`
compiles every fully-read card and reports the ones that produced no spell, no trigger, no static,
no replacement and no activated ability — plus any trigger, mode or instant whose effect list came
out empty. It reads **zero** on all three counts, which is the useful shape of that answer: the
compiler does not emit inert output, so a card that plays wrong is wrong in its effects rather
than missing them. Building it turned up one real defect of its own — `HasAbilities` did not count
`GrantedKeywords` or `AttacksOnlyIfDefenderControls`, and so called eight cards inert that the
combat rules enforce correctly.

It counts whether a card's **text** was read, not whether the card plays correctly. The corpus
the count is taken from is built from the bulk data and now carries colours, mana cost, power,
toughness and subtypes as well as text — adding those moved the number by exactly nine cards, all
of them conspire, because conspire is the only compiler branch that *refuses* on missing card
data rather than merely behaving differently with it. That is the shape of the instrument's blind
spot: it cannot see a field a compiler reads at runtime, only one it reads at compile time.

- **A replacement effect *can* ask a question, and that is not the same as an effect asking
  one.** The rule that a choice can only be made at the end of a resolution is about effects, and
  it stood. Replacements were always different: the engine already held an event and asked which
  of two replacements to apply first (CR 616.1), then re-emitted it. Dredge needed the same
  machinery for a different question — "you may" instead of "which first" — so
  `ReplacementEffectDefinition` gained `IsOptional`, the ask fires when a single optional
  candidate applies, and declining is offered only when every candidate is optional (a mandatory
  replacement still has to happen; taking it first re-examines the rest). The dredge test drives it
  from a *cast* draw, which puts the question in the middle of a resolution — the place the
  effects rule says is out of bounds, and is not, for this.
- **Splice and assist are cast-time choices, not blocked ones.** Both were on the "needs a
  capability the engine does not have" list, and both turned out to be the pattern the engine
  already supports best: a choice that arrives *with* the cast, the way modes and chosen costs do.
  Splice appends each revealed card's targets after the modes' and runs its effects against its
  own slice — the arithmetic modes already needed, one list further along — and the card never
  leaves hand, so nothing has to be put back when the spell is countered. Assist reduces the
  generic part of the total cost and charges it to the nominated player's pool. The deviation both
  share is the one every cast-time choice here shares: the rules give a window to respond (assist
  explicitly lets the chosen player activate mana abilities first), and the engine cannot suspend a
  cast to offer one, so both players' mana has to be floating already.
- **Banding is read, and only its blocking half is enforced — deliberately, and it makes those
  ten cards a lie the coverage number tells.** CR 702.22j is the exception to CR 510.1c: a creature
  blocked by a creature with banding has its damage divided by the *defending* player rather than
  by the one who attacked with it, and that is a contained change to the one place the engine asks
  the question. What is not implemented is the rest of the keyword — declaring an attacking band
  (702.22c–i), where blocking one member blocks them all, and 702.22k, where the active player
  divides a blocking creature's damage. Neither is reachable: `DeclareAttackers` has nowhere to
  put a band, nothing in the state holds one, and the engine never divides a *blocker's* damage at
  all. So a banding card now compiles clean and plays part of what it prints. That is the trade,
  written down here rather than left for someone to find: the blocking half is what a two-player
  game actually reaches, and no client has ever been able to declare a band.
- **A choice can only be made at the *end* of a resolution.** Deliberate: resumption is an
  explicit branch per `ChoiceKind`, because a continuation cannot be folded from a log. Scry,
  surveil, discard and the optional payment all defer their question to the next settle
  (`_looksOwed`, `_discardsOwed`, `_paymentsOwed`), which is the same game only because each is
  the last thing its ability does. A card that offers a payment in the *middle* of an effect is
  left unread rather than resolved in the wrong order.
- **Chosen costs are limited to one card each.** "Sacrifice a creature" and "Discard a card"
  work — the payment arrives with the activation, which is why the engine never has to suspend a
  cost payment — but "sacrifice two creatures" and "sacrifice any number of" do not, and neither
  do costs that name a permanent to return or exile.
- **A target filter can see its source only through a second delegate.** `TargetSpec` carries a
  `SourceFilter` beside its ordinary one, because most phrases genuinely do not need the source —
  "target creature" is the same question whoever asks — and adding an argument to the delegate
  every phrase constructs would have cost more than the handful that need it are worth. The ones
  that need it all want the same thing: "each *other* attacking creature", "enchanted creature",
  "creature with lesser power". Battle cry is the first to use it; the two unread "enchanted
  creature" target phrases are now expressible and not yet written.

- **A spell's colour is its card's colour identity.** Close enough that it has never been
  visibly wrong — the two coincide on almost every card — but they are different things
  (CR 202.2 against CR 903.4), and a card with a coloured mana symbol only in its rules text is
  the case where protection would stop something it should not.
- **CR 702.33g** — a kicked-only clause should choose its targets only if the spell was kicked.
  The engine has one target list per spell, so they are chosen either way; an unkicked spell asks
  for a target it will not use.
- **Suspend is the last unread member of the offer-a-cast family.** Madness, cascade and rebound
  all landed on one mechanism — `GameObject.MayCastFree` plus `OfferedCost` is a standing
  permission to cast a particular card from wherever it is, for a stated price, which the player
  takes through the ordinary casting path. The engine still cannot perform a cast from *inside* a
  resolution, and does not need to: an offer is a fact about an object, and taking it is a normal
  action on a later priority. The offer lapses when its window closes.
  Suspend now lands on it too. Its time counters live on the card rather than on a permanent -
  a suspended card is in exile and has none - and the countdown runs as a turn-based action at
  the upkeep rather than as a compiled trigger, because the engine cannot cast from inside its
  own bookkeeping. Both are deviations worth naming: CR 702.62a makes the removal a triggered
  ability and the resulting cast mandatory, so a real game gives a window to respond and no
  option to decline. Declining here only ever strands the card.
  Two supporting details, both learned the hard way: the offered cost is stored as the **printed
  string**, since a parsed `ManaCostSpec` holds a list and records compare lists by reference, so a
  state rebuilt from the log would compare unequal to the original; and the permission bypasses
  the zone rule *and* the timing rule, because the whole point is that the card is somewhere a
  spell is not normally cast from.
- **A modal card's header is a range, and reading it as a number silently halved 71 cards.**
  "Choose one or both" (53 cards) and "choose one or more" (18) both compiled — and both were
  counted as fully read — while the engine refused any cast that took more than one mode. That is
  the clearest example of what the coverage number does not measure: every one of those cards read
  perfectly and none of them played correctly. `ModesMax` now holds the top of the range beside
  `ModesToChoose`, and entwine and escalate are the two costs that buy their way up it.
- **Block requirements are satisfied one creature at a time, not maximised as a set.**
  CR 509.1c asks for the declaration that satisfies as many requirements as possible; the check
  instead asks each creature whether it is blocking as many lures as it is able to, which is the
  same answer whenever a creature's own limit is what binds it - and that is every board with
  one lure on it. A lure with menace requires nothing at all, deliberately: "able to block" is
  judged one creature at a time and menace is a fact about the set, so the strict reading would
  leave the defender compelled to block and refused for blocking alone. The "this turn" wordings
  of both this and "can block an additional creature" stay unread - they are one-shot effects,
  and this static layer has nowhere to put a duration, so reading them would make them permanent.
- **A permanent with two abilities** cannot be used from the board — a click that silently picked
  the first would be worse than one that does nothing, so it selects the card instead. It needs a
  menu.
- **The board was unreachable from the flow that starts a game, and now is not.** Found by
  running the app rather than by reading it: `_play-a-game.js` timed out waiting for the board,
  and the reason was two separate defects in the client. Accepting an invitation navigated to
  `/play/<gameId>`, and `/play` is the lobby — it has no `:gameId` child and there is no wildcard
  route, so the navigation matched nothing. Behind that, `GameBoardComponent` refused to start
  unless `localStorage['mtg_session']` held a per-game token for exactly that game, and only
  `/lobby` ever writes one; every game begun by accepting an invitation therefore bounced off its
  own board. The token turned out to be dead weight in both places that could have used it — the
  join is a plain `GET /api/games/:id`, and the hub authenticates with the account's own JWT and
  works out the seat from that — so the gate was removed rather than fed. Verified in a real
  browser: the URL stays on `/game/<id>` and the board renders both seats, life totals and
  libraries. The e2e driver had a second copy of the same kind of rot — its whole `gb-*` selector
  vocabulary named elements the board has not rendered in a long time, so every read came back
  empty and every script that used it timed out before reaching an assertion. Nothing said so: an
  empty read and a board with nothing on it look identical from the outside, which is exactly how
  it survived. `board()` and the click helpers are now written against selectors re-found from a
  running board (`_diag-board-dom.js`), and playing a card is a double-click because that is what
  the hand binds — the helper it replaced looked for a `.gb-card-play` button, so every attempt to
  play anything selected a card and then pressed nothing.
- **No spell could be cast from the board, because the client could not read a mana cost.**
  `PlayLegalityService.canAfford` walked the cost a character at a time, and the cost arrives as
  the card prints it — `{2}{R}`, not `2R` — so the opening brace was read as a coloured pip and
  the pool was asked for mana of colour "{". Every non-land card came back uncastable; a land
  never reaches that code, which is why lands worked and nothing else did. Its unit specs all
  passed: every one of them used the brace-stripped form, a shape nothing in the app produces.
  Fixed and proven in a browser — after tapping three Mountains the two Goblin Pikers in hand now
  carry the `castable` class, where before the fix neither did.
  Behind that was a second break, and this one was mine: `GameHub.CastSpell` had grown a trailing
  `CastOptionsDto? options = null`, and SignalR binds hub arguments by position without applying a
  C# default for one the caller left off. The board sent four arguments to a five-argument method
  and every cast came back `Failed to invoke 'CastSpell' due to an error on the server` — no
  refusal, no line in the game log, the card simply stayed in hand. `GameHubTests` calls the
  method in C#, where the default *does* apply, so the suite was green throughout. The parameter
  is required again, the client sends it, and `signalr.service.spec.ts` now counts the arguments.
  **With both fixed, a real game plays end to end in two browsers:** 9 turns, 11 lands, 6 spells
  cast and resolved, 3 blocks, 558 log lines, and both life totals down from 20 to 14 — every
  action a click on the board.
- **The board does not yet offer the costs it can now send.** `GameHub.CastSpell` takes a
  trailing optional `CastOptionsDto` carrying kicker, buyback, dash, morph, delve, entwine, the
  cards spliced onto an Arcane spell, who is assisting and for how much, what was
  tapped or sacrificed to pay, and the modes chosen — every choice CR 601.2b asks for. It is
  optional so that a client sending four arguments keeps working, which is what let the hub be
  widened without touching the client in the same breath. What remains is client-side: the board
  has no UI for choosing any of it, so in practice a spell is still cast for exactly what is
  printed on it. The list lengths are capped in the hub by hand, because a hub method is not a
  controller and DataAnnotations never run on what it is sent.
- **X spells, planeswalker attacks, and multiplayer** are unreached by the board: `castSpell`
  sends `0` for X, `planeswalker` is hardcoded null, and the layout assumes one opponent.
- **`Apply` can count the board but `Applies` still cannot see the source's controller.** The
  first half of this was fixed for "gets +1/+1 for each creature you control", which needed the
  state at apply time; the second half below is the same signature problem one step further on.
- **A stolen *lord* still buffs its old controller's creatures.** The `Applies` predicate of a
  continuous effect is handed the source as a raw object and no ability source, so it cannot
  compute the source's own controller — only its target's. Fixing it means widening that signature
  and thinking carefully about CR 613.8 dependency, since computing one lord's characteristics
  while computing another's can loop. Recorded rather than attempted.
- **Landwalk reads computed land types, but nothing yet grants one.** The check is right; the
  template that would exercise it — "each land is a Swamp in addition to its other types" — does
  not compile, so that half is unverified and is not claimed by any test.
- **Displayed power and toughness ignore CR 613.** `ObjectView` carries printed values plus
  counters, so a creature under a lord reads at its printed size on the board while the engine
  fights with the right number. The engine is right; the board is lying.
