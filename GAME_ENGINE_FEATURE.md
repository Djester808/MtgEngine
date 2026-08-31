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
damage (CR 510.1c), dividing an ability's quantity among the targets it chose (CR 601.2d),
discarding to hand size, discarding because an effect said so, choosing a triggered ability's
targets (CR 603.3d), and whether to pay an optional cost (CR 601.2b).

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

**And the same rule holds for the tests.** A test that restates something the code owns is the
same list, one repository further out, and it goes stale the same way — except that when it does,
the thing it was guarding silently stops being guarded. One audit found four of them at once:
`GameState.Equals` was a hand-written list of fields with no check on it at all until
`StateEqualityTests` learned to *build* a variation of any field type rather than keep an arm per
type; `MechanicCoverageTests` carried a copy of the compiler's ability-word pattern that had grown
*wider* than the original and a card-name pattern written for the wrong word order, so 3,200 of the
lines these tests hand the compiler reached the comparison in a form it never sees; the invariant
suite's noun reader knew three of the grammar's seven ownership clauses, so "creatures you don't
control" was checked as the phrase "creatures"; and four separate walkers over a `CompiledCard`
each missed something different, between them leaving every adventure, prepared half, cleave
text, gift and split-card face checked by nothing at all. Each is now taken from the thing it
checks — the compiler's own `Regex` object, `EffectPhrase.OwnershipClauses`, one walker that
finds the alternate castings by type — and where taking it is not possible, built rather than
listed.

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
| `PlayerQualities` | continuous effects whose subject is a *player* (CR 702.11c) — no layer, because CR 613 orders objects |
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

The **possessive** form is read, and it is a different scope for exactly that reason.
`PlayerScope.NamedPlayer` is what a printed "that player's" compiles to, and it asks the targets
first and the trigger's subject only behind them. The order is the whole rule: a target is named
by the ability's own words and a trigger subject only by its condition, and asked the other way
round The Mouth of Sauron counts the graveyard of whoever its "when this enters" trigger was
about — which is its own controller. `SubjectController` gained the same fallback and the same
order, so "destroy target creature. Its controller discards a card" finds the creature this spell
chose rather than a triggering object no spell has.

**A permanent's characteristics need not come from its card.** A face-down permanent is a 2/2
colourless creature with no name, types or abilities (CR 707.2) — and that is not an effect
applied to the card, it is what the object *is*. `IsFaceDown` redirects where the characteristics
are computed *from*, so the card underneath is never modified and turning it face up is simply
ceasing to redirect. The CR 613 layer loop is shared between both paths, which keeps the
interesting half right: a lord still pumps a face-down 2/2.

**And a copy is that generalised.** A permanent that has become a copy reads its characteristics —
and its abilities, and its replacement effects, and its name — from the copied card, which is
layer 1's answer and nothing else's (CR 613.2c). `Characteristics.CardOf` is the cheap reader for
"which card", and **every place that asks a permanent about itself has to go through it.** Nine
did not, and each was the same bug wearing different clothes: the legend rule grouped by printed
name so a Clone of a commander sat beside it; the replacement gatherer read the printed card so a
Clone of a permanent that enters tapped arrived upright (CR 707.5's own example); the two ability
readers asked the object's card *and* took the copy's, so a copy had both cards' abilities; the
state-trigger sweep watched the wrong card's condition; `CreateTokenCopy` and myriad made tokens
of the card rather than of the permanent (CR 707.3); a Saga counted the wrong card's chapters; and
the board was shown the name and the rules text of a card the engine was no longer playing. This
is the same lesson control-changing effects taught, one layer earlier: **any read of `obj.Card`
where a computed characteristic was meant is the same bug.**

Two things about the copy family are decisions rather than mechanics:

- **The copied card travels whole inside the effect's name.** CR 707.2b fixes the copiable values
  when the copy is made, so an effect holding the copied permanent's *id* would stop being a copy
  the moment that permanent died — its id stops existing (CR 400.7). It makes much the largest
  thing this engine puts in a log, and the log still has to read back, so there is a test that
  round-trips one through the serializer.
- **"Which creature do you copy?" is asked as a set of candidate replacements.** The question falls
  in the middle of applying an event, which is the one place this engine has nothing to ask with —
  a mid-effect continuation is exactly what a folded log cannot rebuild. So the effect offers one
  candidate *per permanent it could copy* (`ReplacementEffectDefinition.Branches`), and CR 616.1's
  "which of these replacements applies first" — which already halts the whole game, is answered by
  an event, and replays — asks it. "You may" is the same question's decline arm. Each branch is
  labelled with the card and its controller, because a board with two Grizzly Bears on it must not
  offer two identical buttons.

The ordering inside the replacement is load-bearing and was measured: the copy effect is emitted
**before** the arrival it replaces. Triggers are collected against the state just after the event
that caused them (CR 603.6), so a permanent that becomes a copy in the event *after* its own
arrival has already been asked what its enters abilities are and answered with the copying card's.
Reversed, the permanent is still a copy with the right name and the right size and the CR 707.5
enters trigger silently never fires.

`ReadCopiedCard` is the seam. It runs at the end of layer 1, not in layer 6, because an effect that
removes every ability (CR 613.1f) has to take a copy's abilities with it whenever it applies.

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

### Round twenty-one: an effect on a card that is not on the battlefield, and the row it retires

The brief was the largest remaining Alchemy row. Perpetual is 193 by excision and 4 by
substitution, and the reading offered for that gap was that **241 of its 245 lines want a grammar
for cards in non-battlefield zones** — "creature cards in your graveyard get +1/+1", "each nonland
card in defending player's hand gains …". That is a claim about a *shape* rather than a word, so it
was measured as one, corpus-wide, by
`CardCompilerWorkQueueTests.What_an_effect_on_a_card_outside_the_battlefield_would_be_worth`.

**Both halves of the claim are wrong, and the second one badly.**

| | |
|---|---|
| the shape, printed anywhere in the corpus | **110 cards, 112 unread lines** — graveyard 62, hand 41, library 7 |
| of those, already compiling whole | **0.** No complete card in the corpus prints it; the grammar reads nothing today |
| how many are perpetual lines | **42 of 112** — so 203 of perpetual's 245 lines are *not* this shape at all |
| **excision** — cut the lines and recompile | **82** |
| **substitution** — same sentence, subject moved onto the battlefield | **1** (Embalmer's Tools) |
| **line control** — drop a *different* unread line on a carrier that has one | **0 of 28** |

The substitution is the one that answers the question, and it is mechanical on purpose: strip the
word "card", pluralise, add "you control", leave every other word standing. "Creature cards in your
graveyard get +1/+1" becomes "creatures you control get +1/+1", which the compiler has read for
many rounds. **One card in a hundred and ten survives that rewrite.** The zone is not what blocks
them. What blocks them is on the other side of the verb, and a witness battery of framed sentences
says which:

| sentence, on the battlefield, with nothing to do with a zone | |
|---|---|
| `Each creature you control has flying.` | READ |
| `Creatures you control get +1/+1.` | READ |
| `Each creature you control has unearth {2}{B}.` | UNREAD |
| `Each instant and sorcery you control has flashback.` | UNREAD |
| `Each creature you control has cycling {R}.` / `miracle {2}` / `ninjutsu {2}{U}{B}` / `scavenge` / `escape` / `dredge 2` | UNREAD |

Fifty-five of the 110 grant one of those — a **casting keyword with a cost, granted by a static
ability** — and the compiler cannot read that grant *anywhere*, battlefield included. Forty-two
carry `perpetually` as well. The rest grant a quoted ability. **The non-battlefield zone is the one
thing on these cards that would have been cheap**, and it is worth one card.

So the row is retired: **the non-battlefield continuous-effect grammar is not worth building**, and
neither is the reading that ranked perpetual by it. Excision has now over-counted 4.4×, 13.7×, 5×,
50× and here **82×** on six families in one round; the excision column should be read as an upper
bound and nothing else.

#### The line control is the part worth keeping

Twenty-eight of the 110 carriers have another unread line, and dropping it completes **none** of
them. These cards are not one line short of anything — they are two and three shapes short, and
each of those shapes has a larger family of its own. A control that drops any line from any card
completes 1,340 corpus-wide, which is why it had to be this one.

#### What was built instead, because the audit found a live one

The shape was declined; the *audit* for it was not. Sweeping every complete card whose ability
targets a card in a graveyard and then creates a continuous effect turned up eleven, and playing
one showed the whole family broken:

> **Goryo's Vengeance**, Bond of Revival, Foul Renewal, Macabre Mockery, Grave Upheaval, Fated
> Return, Dawn of the Dead, Balduvian Atrocity, Kami of Industry, Kardur's Vicious Return —
> "Return target creature card from your graveyard to the battlefield. **It gains haste. Exile it
> at the beginning of the next end step.**" The creature arrived, could not attack, and was never
> exiled. All eleven compiled clean and counted as covered.

The cause is CR 400.7 in the place the engine had not looked: the card the player targets is in a
graveyard and the permanent that arrives is a different object under a different id, and
`Subjects.Resolve` accepted a target only when it was *already* a permanent. A graveyard card is
not one, so both the pump and the delayed trigger resolved to nothing at all — no event, no log
line, no error.

**CR 400.7j is what makes this a fix rather than a convenience:** "if an effect causes an object to
move to a public zone, other parts of that effect can find that object." The resolution record
(round eighteen's, for "destroyed this way") already held every zone change of the resolution; what
it had never kept was the id the object had *before* the move, because a participle asks what
happened and never which id it happened to. A pronoun asks the other question. `Touch.OldId` is the
join, and the resolver walks the chain — one resolution can move the same card twice.

Two refusals ride on it, and both are the rule rather than caution:

- **Only to a public zone.** A card returned to a hand or shuffled into a library is not followed,
  because following it would leave the resolution holding the id of a card only its owner may see.
  This is the same line `GameView Project` draws, arrived at from the other side: an effect on a
  card in a hidden zone must not become a route to it. There is a behaviour test that returns the
  corpse to a hand, watches the delayed exile *not* fire, and asserts the opponent's view still
  carries a hand count and no hand.
- **Only a move this resolution made.** Feldon of the Third Path targets a creature card in a
  graveyard, *copies* it, and says "it gains haste" about the token. Nothing in the record can tell
  a created object from a moved one, so the resolver answers nothing — as
  `EffectSubject.TriggeringObject` already does when the event was about no object. A fallback to
  "the card you targeted" would have passed every other test in the section and put the haste on
  the corpse. There is a test pinning exactly that: the corpse is still in the graveyard and still
  hasteless. **Feldon's token remains a live gap**, and its fix is a record of what a resolution
  *created*, which nothing here has.

#### Result

**18,419 → 18,419 complete cards.** The set diff and the per-card effect fingerprint are both
empty by construction: no compiler file was touched, and the change is in how a subject is resolved
while an ability runs. That is the point worth recording — **eleven complete cards were wrong in a
way no structural instrument in this project could see.** Coverage counted them, the inert audit
found effects on them, the trigger probe found their triggers firing, and the defect was between
the target's id and the permanent's, which is not a field on anything.

Made to fail: pinning the new arm to an id that cannot occur turns the two behaviour tests red
naming exactly the two things a player would see — the reanimated 4/4 is refused as an attacker
(CR 302.6), and it is still on the battlefield at the end of the turn.
### Round twenty-one: which object a pronoun in an amount means

**18,419 → 18,436 complete cards, +17, none lost, measured by set difference on this branch's own
binary.** And 22 cards that were *already* complete now play a different number, which is the
larger half of this round: they were reading the wrong object and nobody could see it.

The family is an amount whose size is a characteristic of an object the sentence names by pronoun —
"you gain life equal to its power", "draw cards equal to that creature's toughness", "create a
number of tokens equal to ~'s power". The count rewrite next door declined it in round twenty-one
with a precise reason: the clause reader resolves "its" against the head's target, so
`~ deals damage equal to its power to target creature` would compile as the *target's* power.
Reads-and-is-wrong is worse than unread.

#### The measurement

274 incomplete corpus cards carry an unread line of this family; 215 of them have no other unread
line at all. Three probes on that population, on the pre-change binary:

| probe | completes |
|---|---|
| **excision** — drop the whole line that carries the stat amount | **112** |
| **line control** — drop a *different* unread line on the same cards | **0** |
| **substitution control** — rewrite only the stat phrase into a count already read | **43** |

The line control at 0 says the stat line is the blocker on every one of these cards and nothing
else on them is. The substitution control at 43 against an excision of 112 says the rest. The two
sets overlap on 31: **81 of the excision's 112 are cards where the sentence around the amount is
unread as well**, so dropping the line took a second win with it and the excision figure is 2.6×
the honest ceiling. Most of the 81 are damage sentences with a target grammar the compiler cannot
read yet — "whenever ~ becomes blocked, you may have it deal damage equal to its power to target
creature" is four cards on its own and none of them turns on the amount. The other 12 run the other
way: cards whose *only* printed line is the stat line, which excision cannot score at all because
cutting it leaves no card.

Of the 43 the substitution can reach, two in five name their object as "the sacrificed creature",
which is a referent the engine has no way to point at: the sacrifice is a cost or a player's
choice, and nothing carries which permanent it was into the resolution. Those are left in the
queue. 17 is what the two referents below actually reach.

#### Two referents, each with a rule

**The source, decided by nearest antecedent — with people struck out.** The compiler already had
`MeansTheSource` for "it deals": yes exactly when the last thing named before the pronoun is `~`
and nothing that could be an antecedent stands in between. A possessive in an amount wants the same
rule with two changes the grammar forces and one the rules do.

The position asked about is the **quantity**, not the pronoun: "draw cards equal to its power"
carries the word "cards" between the two, and "card" is on the antecedent list, so asked at the
pronoun every sentence of this family refuses itself on the noun it is a quantity *of*.

And **a player is not a candidate**, because a player has no power and no toughness (CR 107.3). The
words "each opponent" and "to a player" stand between the source and the quantity on Gregor, Shrewd
Magistrate and Imperious Mindbreaker, and the pronoun cannot be pointing at them — so they are
struck out of the text before the antecedent list is consulted. Nothing else is: every object word
stays a refusal, planeswalkers included.

**And the source has to still be there.** The number is read as the effect resolves, so a sentence
whose own trigger buried the source first — "when ~ dies, create a number of Treasure tokens equal
to its power" — is asking about a permanent that no longer exists, and a card in a graveyard
answers its *printed* power. Goldvein Hydra and Termagant Swarm are printed 0/0s that live entirely
on the counters they enter with: read that way they would compile, resolve and make nothing, for
ever. Refused. The refusal applies to the printed name as readily as to the pronoun — Termagant
Swarm spells its own name where the pronoun would go, and spelling it out settles *which* object is
meant while saying nothing about whether it is still there.

**The object this same resolution has already named, read as last known information.** "Exile
target creature. Its controller gains life equal to its power" is Swords to Plowshares, and by the
time the life is gained the creature is a card in exile. The life family answered that possessive
with the *trigger's* subject, which on a spell is nobody at all — Swords to Plowshares, Condemn and
Avenger en-Dal each gained nought. It now answers with the permanent the sentence in front
targeted, through `PeerAt`, and reads its size **through the layers while it is on a battlefield
and off the resolution's own record once it has left** (CR 608.2h): a printed 2/2 with three +1/+1
counters left the battlefield as a 5/5, and the exile card would have said two.

Only a target that *has* the characteristic. A player target is refused, and so is "any target",
which is a player as readily as a creature and the words cannot say which it turned out to be —
that refusal is what keeps Consuming Vapors and Tribute to Hunger from measuring a person. A spell
on the stack is admitted beside a permanent, because Illumination counters one and gains life equal
to its mana value.

#### The 22 already-complete cards that were playing the wrong number

The life family's stat arm always answered with the trigger's subject. 27 complete corpus cards
reach it, and on **22** of them their own sentence has targeted something first — Divine Offering,
Chastise, Sever Soul, Vendetta, Devour in Shadow, Reanimate, Terashi's Grasp, Feed the Swarm,
Garruk's −3, Sheltering Word, Heal the Scars, Serene Offering, Infernal Reckoning and nine more.
Most are spells with no trigger at all, so the subject was nobody and every one of them gained or
lost **nothing**. Rotfeaster Maggot is worse than nothing: an enters trigger whose subject is the
Maggot itself, so it exiled a creature card from a graveyard and gained life equal to *its own*
toughness. All 22 now measure the object their sentence names.

The other five are untouched, and the reasons are the refusals above. Engulfing Slagwurm and
Doomgape name no target, so the trigger's subject is still the only answer and still the right one.
Consuming Vapors and Tribute to Hunger target a *player* who then sacrifices something, and reading
that target would have measured a person. Syr Ginger sacrifices itself for a cost and targets
nothing.

#### Damage is deliberately absent from the rewrite's nouns

The rewrite that hands this vocabulary to every verb at once takes `life`, `cards` and
"a number of …" and **not** `damage`. The damage sentences of this family are read whole by a
matcher that already knows what their pronoun means; handing them to the clause instead would give
that clause a head with a target in it, which is precisely the mis-reading the count rewrite
declined this family for. A guard test plays `{T}: ~ deals damage equal to its power to target
creature` on a 5/5 source and a 2/6 target and asserts five marks rather than two.

#### What is still declined, and why

- **"The sacrificed creature's power/toughness" — 25 sole-blocked cards** (Fling, Thud, Greater Good,
  Life's Legacy, Altar of Dementia, Brion Stoutarm, Starlit Sanctum, Bloodshot Cyclops, Barrage
  Tyrant, Scourge of Skola Vale, Tormented Thoughts, Eye of Yawgmoth, Rite of Consumption, Airdrop
  Condor, Final Strike, Pyrrhic Blast, Rhovanion Rampager). The referent is a permanent sacrificed
  to pay a cost or chosen by a player, and nothing carries which one it was into the resolution.
  A referent the engine cannot name is not a phrase to widen.
- **"Assigns combat damage equal to its toughness rather than its power" — 20 lines, 12 sole
  blockers.** Not an amount at all: it is a rule about how a creature assigns combat damage
  (CR 510.1a), and the possessive in it is never in doubt.
- **Arithmetic tails — 3 cards** ("its power plus its toughness", "its power minus 1"). The pattern
  ends at the stat word, because a tail admitted here would read as the bare stat and give the card
  a smaller number than it prints.
- **A source buried by its own sentence — 8 cards** (Goldvein Hydra, Termagant Swarm, Mortis Dogs,
  Rapacious Guest, Feral Ghoul, Lifeblood Hydra, Doom Weaver, Riders of the Mark). Answerable only
  by a last-known-power the engine does not keep for an object that left before this resolution
  began. The record covers what *this* resolution moved and nothing earlier.
- **A stat measured on the trigger's subject after it has died** is read, and it reads the
  graveyard card's printed size. That is the shipped behaviour of the arm this round widened — Death
  Watch and Banewasp Affliction inherit it — and it is short by every counter that was on the
  creature. The fix is the same missing last-known-power as the bullet above.

### Round twenty-one: how much damage was excess

**18,144 → 18,149 complete cards, +5, none lost, measured by set difference on this branch's own
binary.** The per-card compiled-effect diff moved 6 rows: the five cards gained, and Cramped Vents,
which now reads its life-gain sentence and is still short of the rest of the Room.

Round twenty built CR 120.4a's subtraction and spent it on the redirect, which moves the excess
without ever saying how much it was. This is the other half — the number — and the design it was
written down with turns out to have been twice the size it needed to be.

#### The measurement, and the control that decided the shape

30 corpus cards are one line short with "excess" in the unread line. Three probes on that
population, on this branch's own compiler:

| probe | completes |
|---|---|
| **excision** — drop the sentence that says "excess" | **14** |
| **line control** — drop a *different* sentence of the same line | **0** |
| **substitution control** — rewrite only the excess phrase into a form already read | **4** |

The line control at 0 says the excess sentence is the blocker on every one of the 14 and nothing
else on those lines is. The substitution control at 4 says something the excision figure cannot:
on ten of the fourteen the *rest* of the sentence is unread too, so an excess reader alone reaches
four cards and not fourteen. The ten are short for siblings that have nothing to do with damage —
"create a number of X tokens equal to N" is six of them on its own, and it does not read with a
count the compiler already knows in the slot.

**So the honest price of this family is four, and it was built for four.** The fifth card is the
redirect refusal below.

#### The number does not have to reach the log, and the trigger cards are why

The design round twenty wrote down wanted an excess magnitude on `DamageMarked` — a field on a core
event, so the log, the serializer and `Replay(log) == State` all carry it — plus a magnitude on
`ResolutionRecord`. Neither is needed, and the measurement is what shows it.

A field on the *event* would be wanted only by a **trigger** that asks about excess (CR 120.10):
Aegar, Magmatic Galleon, Fall of Cair Andros, Toralf, Rith, Maarika, Overclocked Electromancer.
Every one of those fails its substitution control — rewrite "is dealt excess damage" to "is dealt
damage" and not one of them completes, because each is short for a batch trigger, an
intervening-if, a delayed check or an unread tail as well. **No corpus card reachable today needs
the number on the event.** So it lives on `ResolutionContext` instead: derived from the events an
effect emitted, consumed by the next effect of the same resolution, and never stored. Whatever the
next sentence does with it — life gained, tokens created — lands in the log as its own event with
its own final number, which is exactly how `SubjectAmount`'s "that much" has always worked.

**`ResolutionRecord` is left as round eighteen set it, and its reason still holds.** That record is
kept to zone changes because an entry the readers cannot tell apart from another is how a count
comes out too big. A magnitude has nothing to be told apart from — it is not a member of a set, it
is a size — so the twin it belongs beside is `SubjectAmount`, not `Touches`. Widening the record
would have been answering the right question in the wrong place.

#### Three grammars, one gate, and one word that matters

- **The amount** — "you gain life equal to the excess damage dealt this way" is the sentence the
  compiler already read one adjective shorter, so it is that reader's regex with `excess` optional
  and a different magnitude behind it.
- **The guard** — "if excess damage was dealt this way, …" is `OnlyIfExcessDealt`, its own effect
  for the reason `OnlyIfTouched` is one: `OnlyIf` is handed a state and a source, and every
  condition it can express is a fact about the board. This one is a fact about the resolution.
- **"That many" inside the guard is the excess, not the damage.** Bottle-Cap Blast prints "if
  excess damage was dealt to a permanent this way, create that many tapped Treasure tokens", and
  the running magnitude at that point is the five the spell dealt. A guard that only gated would
  have made that card five Treasures off a 2/2 — compiling, resolving, logging plausibly and
  paying out half as much again as it prints. The guard rebinds the magnitude for what it guards.
- **"To a creature" and "to a permanent" are two numbers.** A planeswalker whose loyalty is
  overshot has been dealt excess damage and is not a creature (CR 120.4a). Vikya says "creature",
  Bottle-Cap Blast says "permanent", and both aim at anything, so one number for both would draw
  Vikya a card off a planeswalker.
- **The gate is a damage effect in the same line.** "Excess damage dealt this way" points back at
  a sentence of the same instruction; a card where the reader cannot find one would compile a
  clause whose number is nought for ever — complete, castable and silent. Both grammars are gated,
  because a gate on one of them is a gate on neither. A pronoun ("to *that creature* this way") is
  refused for the same reason the recorded-set condition refuses one: on a fight the reader's
  number is about every permanent hit and the sentence names one.

#### Gandalf's Sanction was refused for a reason that has moved

Round twenty declined it precisely: its damage is inside the "where X is the number of instant and
sorcery cards in your graveyard" box, the rider's search for a top-level hit found nothing, and a
rider that quietly found nothing would print a redirect and perform none. That is still true of a
rider that only looks at the top level — the fix is to **look one level down and one level only**,
and to insist the box holds exactly the damage. The rider still declines rather than guessing which
of several hits a sentence meant, and the test that asserted the refusal is now a game that plays
the card, because a refusal that has become reachable is a game to be played and not a test to be
flipped.

#### What is declined, with counts

- **Ram Through (1).** Its "target creature you control deals damage equal to its power to target
  creature you don't control" compiles to `Fight`, not `DealDamage`, so the redirect rider cannot
  reach it and the trample condition has nowhere to sit. Extending the excess split to `Fight`
  was measured and is worth **0 cards today**: every other fight-shaped excess card — Rhino's
  Rampage, Windswift Slice, Ravenous Pursuit, Contest of Claws, Intruder's Inquisition — fails its
  substitution control for a reflexive trigger, a token count, "perpetually", discover or "the
  greatest mana value". A mechanism no card can use is the thing this file spends most of its
  space refusing.
- **A general excess amount in `CountingAmount` (0).** "Where X is the amount of excess damage
  dealt this way" and "equal to the amount of excess damage dealt this way" appear on eight cards
  and the substitution control completes none of them: the host grammar is the blocker every time
  (a group pump with a variable, a variable token count, an exile-that-many, a look-at-X). The
  reader would have been correct and would have had nothing behind it.
- **The seven trigger cards (7).** Measured above: none is reachable, and building the event field
  for them would have been a core-event change with no card behind it.
- **The token-count sibling (6).** "Create a number of &lt;token&gt; tokens equal to &lt;amount&gt;"
  reads with no amount the compiler knows — Lacerate Flesh, Windswift Slice, Goblin Negotiation,
  Hell to Pay are excess cards behind it, and it is a family of its own rather than part of this
  one.
### Round twenty-one: the Alchemy families, re-measured — and the mechanic word is not the blocker

Five figures had stood behind the Alchemy and Un-set declines for several rounds without anybody
re-running them. **Three of the five were wrong**, and the two that held were right for reasons
worth writing down. Measured against the current dump (38,626 objects, 32,717 playable) and the
current compiler.

| the old figure | what it actually is |
|---|---|
| "62 spellbook cards with no data field" | **64 cards, and the field really is absent.** No key anywhere in the dump contains the word; the only linking field any of them carries is `all_parts`, on 5 of the 64, and every entry there is a `combo_piece` or a `token` — never a spellbook. A spellbook's contents are not in this file. |
| "19 specialize cards absent from the corpus" | **Wrong twice.** The 19 base cards *are* in the playable corpus, and their five specialized versions are in the dump too — all 45 of them, set `hbg`, `not_legal` in every format, which is why the legality filter drops them. **Every base card carries `all_parts` with exactly six entries: itself and its five colours.** The link is complete and machine-readable. |
| "246 `perpetually` cards contradict CR 400.7" | **246 confirmed**, and the identity problem is real. But see below: it is not what blocks them. |
| "46 Attractions missing `attraction_lights`" | **Wrong.** There are 22 Attractions in the playable corpus and **all 22 carry `attraction_lights`**; so do all 50 in the dump. The 46 was a text match on cards that *open* Attractions, not on Attractions. Nothing about this family is blocked on missing data. |
| "48 sticker sheets excluded as not-cards" | **Correct and deliberate.** 50 sheet objects, 48 of which pass the legality test; `CardCompilerCoverageTests` drops them on the type line, for the reason it drops tokens. |

#### The excision number is an upper bound, and everybody had been quoting it as the answer

`CardCompilerWorkQueueTests.What_the_alchemy_and_un_set_families_would_actually_be_worth` measures
each family three ways. **Excision** deletes the family's lines and recompiles: that is the number
in every previous decline. **Substitution** takes only the mechanic *word* out and leaves the
sentence standing, which is what modelling the mechanic would actually buy.

| family | cards | unread lines | distinct | excised | substituted |
|---|---|---|---|---|---|
| conjure | 164 | 171 | 171 | 111 | 31 |
| perpetual | 238 | 245 | 245 | **193** | **4** |
| spellbook | 64 | 64 | 60 | 43 | 15 |
| specialize | 19 | 19 | 9 | 12 | **11** |
| attraction | 27 | 34 | 22 | 24 | 14 |
| sticker | 47 | 63 | 53 | 42 | 2 |
| seek | 81 | 85 | 84 | 61 | **0** |
| double team | 23 | 24 | 10 | 14 | 3 |

**Perpetual is the lesson at one end: 193 against 4.** Take the word "perpetually" out of all 245
lines and 241 of them are *still* unread, because what is left is "creature cards in your graveyard
get +1/+1", "a random land card in your library gains …", "each nonland card in defending player's
hand gains …" — pumping and granting to cards in zones the compiler's grammar has never been shown.
Perpetual duration is a small part of that family and modelling it completes four cards. Ranking
this work by 193 recommends fifty times what it can pay for. Seek is the same story taken to zero, and it is
the family that proves the reading rather than illustrating it: **the seek verb already has a
reader** (`Seek` in `Effects.cs`, and its own behaviour test), and 81 cards still carry an unread
seek line. Substitute a printed tutor wording for the verb and **0 of 85** read, because what is
left is the filter — "a card with mana value less than the number of cards in your hand", "a
creature card of the most prevalent creature type in your library" — which is where the work
always was.

**Specialize is the lesson at the other end: 12 against 11.** Rewrite `Specialize {3}` to
`{3}: Draw a card` and eleven of the twelve compile whole — every other word on those cards
already reads, including the activation restrictions printed beside it. That is a family waiting
on one reader, and it is the family whose data turned out to be complete. Attraction is the same
shape one step weaker: 24 against 14.

The `distinct` column is the third thing to read, and it is why the middle of this table is not
worth what it looks like. 245 unread perpetual lines have 245 distinct spellings; 171 conjure lines
have 171; 53 sticker lines have 53. **Those families have no template at all.** Specialize has 19
lines and 9 spellings, 15 of them the bare `Specialize {2}`.

#### What was built, and what it proved

The reachable half of conjure. A conjured *duplicate* needs nothing the game does not already
have, because the thing being copied is an object in it — so `ConjureDuplicate` is
`CreateTokenCopy` with two differences and no third: no `TokenCards.AsToken`, because CR 701.55a
says a conjured object is a card, and the zone the sentence names instead of the battlefield.
Nothing new was needed to hold it: `ObjectCreated` already carried a zone and a whole definition,
and `GameReducer.Create` already read that zone rather than assuming the battlefield — the same
reuse emblems made of the command zone one round earlier.

**It completed one card** (Sinister Reflections), reading three lines. That is not a
disappointment, it is the substitution column arriving in person: conjure is not what blocks
conjure cards. The other 41 lines mentioning a duplicate are blocked by the *rest* of their
sentence — `The duplicate perpetually gains …`, `for each creature sacrificed this way`, a trigger
condition nothing reads.

One near-miss is worth recording. "Conjure a duplicate of each of up to two target creatures you
control into your hand" compiled on the first run and looked like a card being read as half of
itself — one duplicate where the card says two. It is not: `EachOfTargets` normalises the phrase
and the shared grammar expands it to two targets and one effect apiece, so the reader must *not*
do its own counting. There is a test asserting exactly that, because the instinct to add a count
here is the wrong one and would have to be resisted twice.

#### The verdict, ranked by what the substitution column says is actually reachable

**Reachable, and the next round's work.**

- **Specialize — 19 cards, 12 by excision, 11 by substitution.** The correction, and the only
  family in the table whose mechanic really is the whole blocker. Its data is complete: every base
  card carries `all_parts` with six entries, and the five specialized versions are in the dump.
  What it needs is `all_parts` reaching `CardDefinition`, five extra definitions per card in the
  pool, and an exchange action (CR 702.161). The five versions carry their own unread text ("when
  this creature specializes", "it unspecializes"), so a `Specialize {2}` that compiled without them
  would be an ability that activates and does nothing — which is worse than the unread line.
  Engine and loader work, not compiler work, and it is bounded.
- **Attraction — 27 cards, 24 by excision, 14 by substitution.** Also not a data decline: every
  Attraction in the dump carries its lights. It needs an Attraction deck outside the game
  (CR 717.2), a die roll, a visit trigger and a new zone. 12 of the 27 print the identical line
  `When ~ enters, open an Attraction.` — the largest single unread Alchemy/Un shape in the corpus.

**Reachable only behind a seam that does not exist.**

- **Conjure a card *named* X — 94 cards, 31 by substitution.** Not a grammar problem, and the name
  grammar landing this round did not change it: `MtgEngine.Rules` has no name-to-definition lookup
  at all. `IAbilitySource` takes a `CardDefinition` in and never a name; `CompiledPool` compiles
  definitions it is handed. Conjuring Lightning Bolt needs the corpus reachable from the engine,
  which is a new seam, not a template. The precedents offered for it — `TokenCards.Granting`,
  emblems — all build a definition out of words *printed on the card doing the conjuring*, and a
  named conjure prints no text. (A second agent's independent control this round put the same
  family at 55 cards under a narrower rewrite and reached the same conclusion.)

**Not reachable from this dump.**

- **Spellbook — 64 cards, 43 by excision, 15 by substitution.** The one decline that is genuinely a
  data decline, and the substitution column is what proves it is *only* a data decline for fifteen
  of them: swap the draft for a draw and they compile whole. The contents are not in
  `oracle_cards.json` under any key, and `all_parts` carries `combo_piece` and `token` and nothing
  else. A second data source, or nothing.

**Not worth what the excision number says.**

- **Perpetual — 238 cards, 193 by excision, 4 by substitution.** The zone-change identity problem
  (CR 400.7) is real and is *not* the reason these cards are unread.
- **Sticker — 47 cards, 42 by excision, 2 by substitution.** The sheets are excluded correctly.
  Name stickers change a card's name, ability stickers add text, power/toughness stickers change a
  printed size — four mechanics behind one word, on 53 distinct lines, and swapping the sticker out
  leaves 45 of the 47 still unread.
- **Seek — 81 cards, 61 by excision, 0 by substitution.** The verb is already built. What is left
  is 84 distinct filters, which is not this family's work but the target grammar's.
- **Double team — 23 cards, 14 by excision, 3 by substitution.** It is conjure plus perpetual
  wearing a keyword, and it inherits both walls.
### Round twenty-one: the third audit of the same family, and the first that can see inside a closure

Every instrument this project had compared compiled **structure**. Coverage counts lines the
compiler read; the set diff compares which cards became complete; the per-card effect fingerprint
compares effect lists; `DeadWriteAuditTests` decodes IL to find a marker nothing reads;
`CardCompilerInvariantTests.Inert` asks whether an effect list is empty, a filter *string* selects
nothing, a deferred question can be found again. None of them can see inside a `Func`, because a
predicate is a closure and its captured parameters are private fields of a compiler-generated
class with no name — which is exactly where the worst defects have lived. The 23-card subtype
class (`TriggerConditions` pairing every capitalised noun with `CardType.Creature`, so "whenever a
Forest you control enters" watched for a *creature* with the land type Forest) was invisible to
the card diff by construction, and was found by playing a card.

`TriggerProbeAuditTests` closes that. For every triggered ability a complete card compiles it
fires a battery of real `GameEvent`s at the predicate on real boards and asks whether **any** of
them is accepted. A predicate that accepts nothing is the closure-shaped inert card: the ability
exists, is watched on every event, and cannot fire.

#### The probes are printed cards, and that is the whole design

Its nearest neighbour — `Every_printed_noun_phrase_the_grammar_reads_can_be_satisfied` — also runs
a closure, but over a *synthesised* witness board that deliberately holds "one permanent that is
every type at once". That board answers **yes** to a creature with the land type Forest, so it
could never have found the twenty-three. Here the objects the battery moves, casts, attacks with
and kills are corpus cards, and the deep sweep is one representative of every distinct (card
types, subtype) pair the corpus prints. "No object the corpus can produce satisfies this
predicate" is then a claim about the printed game rather than about what the compiler believes.

Three stages, because the space is a product of three independent things: a **screen** (every
step, every zone-change pair against every move cause, both combat declarations, and one
reflectively built instance of every other `GameEvent` type in the assembly, so no event family
is missed by omission), then the **worlds** (the same battery on boards whose counters, turn
history, attachment, lean, designations and daylight differ, because "if this permanent is tapped"
and "your second spell each turn" are questions about the board and not about the event), then the
**deep probes**.

#### What the noise was, and it was all battery rather than compiler

The screen's first run reported **1,256 of 8,099 triggers** as firing on nothing. Every reduction
from there to three was a limit in the probe set, found and closed rather than suppressed:

| what was missing | cards it wrongly reported |
|---|---|
| counters the compiler invents and no card prints — `fade`, `echo` | 40 |
| a populated board (a party, powers that differ, a stocked graveyard, a commander) | ~130 |
| a *lean*, so "an opponent controls more lands than you" has a direction | ~56 |
| the two ids of a zone change tried both ways round (CR 400.7) | every Aura in the game |
| a source that had been kicked with its own printed costs, blitzed, disguised, gifted | ~35 |
| the cycling ability id, both Room doors, and casting the source itself | ~74 |
| mana of every colour spent, and `WasTeamwork`, `OnAdventure`, `IsSolved` off as well as on | ~15 |

The lesson is the one the noun guard already learned: **the witness board is half the instrument,
and the half that decides whether the answer means anything.** A list of counter names somebody
wrote is a list somebody can leave `fade` off, so the names are now read out of
`State.CounterKinds` by reflection and out of the corpus by frequency, and only the two the
compiler invents are written down.

#### The finding: a trigger pinned to a zone its own condition forbids

Six cards asked to be in two zones at once. `Game.Consider` refuses an ability whose source is not
in its functioning zone (CR 603.6), the compiler wrote the default battlefield on these, and their
own intervening-if says the card is somewhere else:

- **Genesis**, **Gigapede**, **Arden Angel** — "at the beginning of your upkeep, if ~ is in your
  graveyard, …"
- **Blood Operative** — "whenever you surveil, if ~ is in your graveyard, …"
- **Pyrewild Shaman** — "whenever one or more creatures you control deal combat damage to a
  player, if ~ is in your graveyard, …"
- **Oloro, Ageless Ascetic** — "at the beginning of your upkeep, if ~ is in the command zone, you
  gain 2 life", which is the whole reason anybody plays the card

All six compiled clean, read as complete, counted towards coverage, and could not do anything on
any board. Nothing structural could see it: the zone is a field, the condition is a closure, and
the contradiction is *between* them — the effect list, the target list, the filter strings and the
IL are all exactly what a working card's would be.

`GraveyardOnly` was already the same rule read off the **effects**, and it cannot reach these: it
is restricted to cards with no permanent type, because a creature's "when this dies, return it to
your hand" has to keep watching from the battlefield (CR 603.10a). All six of these are permanents.
What makes the new rule safe where that one is not is that the card has *said* where it is, so
`BoardConditions.ZoneTheSourceMustBeIn` reads the zone off the condition's own words and answers
null for a negation ("isn't on the battlefield" says where it is not), for an alternation ("in the
command zone or on the battlefield" names two and an ability functions from one), and for a
trigger whose own condition already named a zone of its own.

#### Result

**18,108 → 18,108 complete cards, none gained, none lost.** The per-card fingerprint of every
compiled trigger, activated ability, spell, static, replacement and cost modifier moves on
**exactly 7 cards**, and on one field of one trigger in each: `FunctionsFrom`, battlefield to
graveyard or command zone. Six are the complete cards above; the seventh is Auntie's Snitch, which
is one line short of complete and whose trigger is now right for the day it is not.

#### Three survivors, and what is known about each

The audit is a gate: a card it reports that is not named in `Understood` fails the build. The
three named are live defects rather than battery limits, and each says why it was not fixed here.

- **Slayer's Plate**, **Avacyn's Collar** — "whenever equipped creature dies, **if it was a
  Human**". `BoardConditions.SelfTypeLine` asks the question of the ability's own *source*, and an
  Equipment is never a Human, so the condition is false on every board. Its own comment says so
  out loud — "'It' is the source here rather than a target … the pronoun in that sentence has only
  one thing it can mean" — which is true of the clauses it was written for and false here, where
  the trigger's subject is the creature that died. The fix is a signature: `BoardCondition` carries
  a state, an ability source, the source object and a *seat*, and has nowhere to put the object a
  trigger was about.
- **Storyteller Pixie** — "whenever you cast an **Adventure** spell". The cast reader treats
  "Adventure" as a subtype and filters the cast card on it, and nothing in this engine produces an
  object carrying it: an adventure is a *face* (CR 715.3), the spell put on the stack keeps the
  creature card's own subtypes, and `OnAdventure` is marked on the exiled card after resolution.
  That is the twenty-three-card defect exactly, one card wide.

#### The gate was made to fail

Reverting the one-line fix and re-running the audit turns it red naming **precisely** the six
cards — Genesis, Oloro, Arden Angel, Gigapede, Pyrewild Shaman, Blood Operative — and the two
behaviour tests beside it go red for the two things a player would see: the graveyard trigger
never reaches the log, and Oloro's controller stays on 20 life.

### Round twenty-one: the basic land type of your choice, and two neighbours re-measured

Round twenty moved CR 305.6's intrinsic mana ability off the printed card and onto the land's
*current* subtypes, and named this family as the obvious next round. It is **12 cards**, not the
13 the census ranked, and every one of them is one line short with the same sentence: `Target land
becomes the basic land type of your choice until end of turn.` Cutting that sentence out of the
compiler's own lines and recompiling completes all twelve — Dream Thrush, Grixis Illusionist,
Jinx, Moonbow Illusionist, Mystic Compass, Navigator's Compass, Pixie Illusionist, Reef Shaman,
Sea Snidd, Shimmering Mirage, Tideshaper Mystic and Unstable Frontier. **The control that cuts
nothing moves the count by one** (Lightwheel Enhancements, which reads differently when its own
lines are re-joined), so every figure in this section carries that much noise.

#### The question was the whole of the work, because the answer already had somewhere to go

The named form of this sentence — "target land becomes an Island until end of turn" — has read
for a round, and what it builds is `becomes-type:Island`, which already knows both halves of
CR 305.7: a land *set* to a basic land type loses its old land types and the abilities its rules
text gave it, and one that gains a type "in addition to its other types" keeps every word. So
nothing new was needed in the layers at all. What was missing was the *question*, and this engine
has exactly one shape for one: an event plus a `ChoiceKind`, so a replay reaches the same offer
and reads the answer back out of the log rather than out of a captured continuation.

`ChooseBasicLandType` is a kind of its own beside `ChooseCreatureType` for the reason
`ChooseManaColor` is one beside `ChooseColor` — **the menu is what differs**. A creature type is
offered from the types in play, because there are several hundred of them and no board can show
them all. The land types are the five CR 305.6 names, so the offer is closed, the same on every
board, and never empty: narrowing it to what is in play would leave the commonest board of all —
one player, one colour — with a question that could not be asked and an ability already paid for.

Which half of CR 305.7 runs rides on the request (`LandTypeChoiceRequested.InAddition`), because
by the time the answer arrives the sentence is gone. Both spellings are printed one card apart:
Reef Shaman replaces, Navigator's Compass adds.

#### The duration is not a flag on this effect, and that is the point

What the answer builds is a floating effect stamped with the turn number, which comes off in the
cleanup step (CR 514.2). The compiler therefore requires the printed duration exactly as the named
form does — all twelve cards print it — and the sentence without one stays unread, with a test
saying so. An indefinite retyping read through this path would compile a card that undoes itself
and count as coverage while doing it.

#### Result

**18,004 → 18,016 complete cards, +12, none lost**, diffed as a set and again as a per-card
fingerprint of every compiled ability, spell, static, replacement and cost modifier. The effect
diff moved on **exactly the same 12 cards** and nothing else in the corpus changed — no card
gained or lost a clause inside a card that stayed complete.

(The fingerprint had to be built by hand for that: a record's `ToString` prints a nested
`ImmutableList` as its type name, so a diff taken straight off it cannot see a spell's effects
change at all — two of these twelve moved invisibly under the first instrument.)

#### The two neighbours, re-measured

Both were declined by round twenty and both figures needed correcting. Measured by rewriting the
duration clause to `until end of turn` — the one duration this engine has — and recompiling:

| rewritten duration | cards completed |
|---|---|
| control, rewrite nothing | **0** |
| `for as long as it has a <kind> counter on it` | **0** |
| `for as long as ~ remains on the battlefield` | 5 (Tide Shaper, Awakener Druid, Skilled Animator, The Wondrous Wasp, Unctus's Retrofitter) |
| `until ~ leaves the battlefield` | 1 (Graceful Antelope) |
| `until its controller's next untap step` | 1 (Orcish Farmer) |
| a retyping printed with **no** duration at all | 2 (Thelonite Monk, Cyclopean Giant) |

**The counter-scoped family is worth nothing to a duration.** Aquitect's Will, Quicksilver
Fountain, Cyclopean Tomb, Xolatoyac and The Flood of Mars complete **0** when "for as long as it
has a flood counter on it" is rewritten to a duration that works, so "a duration the engine has no
shape for" was the wrong blocker to record. What actually blocks them is in front of that clause:
a counter put on and a retyping joined in one sentence, a pronoun ("That land is an Island")
reaching back to the sentence before, a branch on what kind of permanent was targeted (The Flood
of Mars), and an upkeep sweeper that names what a *specific source* put counters on (Cyclopean
Tomb).

**The indefinite retypings are worth 5, not 6, and they are four different durations** — which is
why they stay refused rather than being widened into this round's reader. Gaea's Liege completes
under none of the rewrites, because its other line (a power and toughness that changes while it
attacks) is unread too. The truly indefinite arm is worth **2** on its own.

Coverage is **54.3% of playable cards fully read** (17,765 of 32,717), 70.0% of lines.

### Round twenty-one: a quoted ability that is a static, and the population is 33 rather than 232

`TryQuotedAbility` took a trigger, an activated ability or a mana ability and nothing else, so
every grant whose quotation is a *static* ability was refused at the frame with both halves of the
sentence readable. `TryQuotedStatic` is the missing arm.

#### The brief's population was measured a different way, and it is much smaller

Round twenty's quadrant said 571 quoted one-short cards — 16 both readable, 142 frame-reads, **232
inner-reads-frame-doesn't**, 181 neither — and read the last of those as mostly wanting a granted
static. Measured against the compiler's own text, by compiling each quoted span as a card of its
own and asking what it produced:

| | cards |
|---|---|
| one line short, and the blocker carries a quotation | 558 |
| ... every quoted span on it compiles to **statics alone** | **33** |
| ... and the frame reads with a known-readable ability swapped in | **9** |
| ... frame blocked as well | 24 |

The 232 is not 232 statics. Most quoted spans that compile without being an activated or triggered
ability compile to *nothing* — they are unread, not statics — and a span that compiles to a
trigger *and* a static is a trigger this reader already took. Nine cards is what the whole
"quoted ability that is a static" family was worth as a sole blocker, and eight of them landed.

The two families round nineteen listed were re-measured at the same time and are **not**
quotation work: `as though its power were N greater` is the sole blocker on **17** cards (the
brief said 6) and `can't be blocked by non-[tribe] creatures` on **4**. Both are printed statics
with no reader anywhere, and neither is touched by anything below.

#### Where the effect lands is the whole difficulty

A granted *ability* goes in layer 6 (CR 613.1f) and there is a slot for it —
`CharacteristicsBuilder.GrantedActivated` and its triggered twin. A granted *static* has no
ability to store: what it has is a continuous effect, and CR 613.1 says that effect applies in
whichever layer the effect belongs to. `+1/+1` is 7c. Put in layer 6 it would be added and then
erased by any layer-7b effect setting a base size, on a card that compiled clean — which is the
assertion `A_granted_bonus_is_applied_after_a_base_size_is_set` exists to make.

So there is no new slot on the builder, and there was never a place for one. The granted static is
a `ContinuousEffectDefinition` **of the granting permanent**, reporting the *inner* effect's
layer, gathered from the battlefield with every other static (CR 604.2). `GrantedStatic` is the
wrapper: it finds the permanent *carrying* the ability and hands that permanent to the inner
effect as its source. That one substitution is what makes a tilde inside the quotation mean the
bearer, "equipped creature" mean what the bearer is attached to, and "you" mean whoever controls
it — Sedge Sliver's bonus reaches Bob's Sliver only when *Bob* controls a Swamp.

**Which permanent is carrying it is a different question from which permanent it changes**, and
that is why the wrapper takes a bearer rather than reusing `Receives`. `As long as enchanted
permanent is an Equipment, it has "Equipped creature gets +1/+1 and has trample"` is three
objects: the Aura owns the effect, the Equipment carries the ability, and the creature the
Equipment holds is what changes. Handing the inner reader the Equipment is the whole of it; the
five Runes needed nothing else.

#### The refusal, and it is the same hazard the layers always had

Every bearer arm reads **raw state** — the source itself, or `Permanent.AttachedTo`. A group
cannot be answered that way: finding which permanents are in it means computing another
permanent's characteristics from inside this one's, which is CR 613.8's hazard and has overflowed
the stack here before. So a **group** frame takes only a static whose subject is the permanent
receiving it, where the receiver *is* the object being computed and no second one has to be
found. `Commander creatures you own have "Creature tokens you control get +2/+2"` (Inspiring
Leader) is the one card that costs, and it stays unread.

Two more refusals, for the reason `Characteristics` refuses the same two outside their own layer:
a granted static may not remove all abilities and may not be a copy effect. Each is two things and
only one of them is an effect — both are decided before any effect is applied, by a pass over
permanents that are not the one being computed, and nothing reachable from a granted effect can
make that call.

#### Result

**18,004 → 18,012 complete, +8, none lost.** Diffed as a set and again as a per-card fingerprint
of every compiled ability: **9 cards moved** — the 8 plus A-Ancestral Katana, which gained its
whole Equipment half and is still one line short of a trigger. Lines read 43,366 → 43,375.

The eight: Sedge Sliver, Giant's Amulet, Prison Barricade, and the five Runes (Sustenance, Might,
Flight, Speed, Mortality).

#### Declined, with the measurement behind each

- **A granted static whose subject is a group, on a group frame — 1 card.** Above.
- **A token created with a quoted static — 8 cards.** `create a green Treefolk Warrior creature
  token with "~'s power and toughness are each equal to the number of Forests you control"`. These
  are frame-blocked, not quotation-blocked: swapping a known-readable *activated* ability into the
  quotation does not complete them either. The token has no printed power or toughness, because
  the granted characteristic-defining ability is where its size comes from — which is a token
  reader problem and not this one.
- **Emblems — still the largest single missing frame in the quadrant.** Unchanged from round
  twenty; CR 114's command-zone object does not exist here.
- **`as though its power were N greater` (17) and `can't be blocked by non-[tribe] creatures`
  (4).** Printed statics on bare creatures, with no reader at all. Nothing here touches them, and
  the quotation is not what blocks them.

### Round twenty: a card name the player chooses, and a row that was 169 and is 8

Meddling Mage, Pithing Needle, Nevermore and their kin ask a question no other entry choice
asks: **a card name**. The engine had the shape for it — an entry choice is `ChoiceOnEntry`
plus an answer stored on the object, and `GameObject.Chosen` already held the colour or
creature type a permanent named. What it did not have was anywhere safe to put a *name*.

#### The row is an upper bound with almost nothing attached, again

The census ranked `named` at 169 cards. Measured on this corpus, **341** cards have every one
of their unread lines in the family (any line printing `named`, `card name`, `chosen name`,
`that name`). Cutting candidate lines and recompiling — with a control that cuts nothing and
moves the count by zero — gives the honest figure:

| cut | cards completed |
|---|---|
| control, nothing cut | **0** |
| `As ~ enters, choose a … card name.` **alone** | **0** |
| + `Spells with the chosen name can't be cast.` | 2 |
| + `Activated abilities of sources with the chosen name can't be activated[ unless…]` | 5 |
| + `Spells/activated abilities … with the chosen name cost {N} more/less` | **8** |
| + `You have protection from the chosen card name.` | 9 |
| + `As ~ enters, look at an opponent's hand, then choose any card name.` | 11 |

**The entry choice on its own is worth nothing at all**, and that is the finding: the question
is never a card's only unread line, because a card that asks it always prints a static that
reads the answer. A round that had built the choice and stopped would have moved the number by
zero and looked like progress. Everything down to the cost cells is built; the two rows below
it are declined, with reasons under "declined" below.

The other 300-odd cards in the 341 are the same *substring* and a different mechanic: 90 are
tokens with a printed token name, 108 count "cards named ~ in your graveyard", 45 are the
two-zone "search your library or graveyard for a card named X", and 34 are Alchemy's
"conjure a card named X". None of them is a name a player chooses.

#### A name is not a characteristic, and every reader here had to be told

The answer goes in **`GameObject.ChosenName`**, its own field beside `Chosen`. One field would
have been less code and the wrong model: a card name is capitalised by definition, and this
compiler tells a subtype from everything else *by the capital letter* — `SearchFilters.Matches`
falls through to `card.Subtypes.Contains(filterId)`, and the mass-static reader reads
`source.Chosen` as a tribe. "Meddling Mage" arriving in that field would have been answered
rather than refused, which is the capitalised-word-in-a-type-table defect this document has now
recorded seven times. So the name never becomes a filter id anywhere: `ChosenNameBan` and
`CostModifier.ChosenName` carry a *flag* saying "ask the host by name", and the comparison is
against `CardDefinition.Name`.

The qualifier goes somewhere else again. `ChoiceOnEntry` names a kind of question and has
nowhere to put a parameter — the same reason the Thriving lands' "other than red" is still
refused — so `CompiledCard.ChosenNameFilter` rides beside it as an ordinary `SearchFilters` id.
"Noncreature, nonland" is then two clauses joined with the ampersand the filter grammar already
reads as "and", and a qualifier word the closed list does not know leaves the whole line unread
rather than offering every card in the game.

#### Null means "nothing was named", in one place

`ChosenName` is null until the question is answered, and null has to mean **matches nothing**.
Read the other way a Meddling Mage entering makes every spell in the game uncastable, and the
bug looks like the card working. Three things ask it — the cast ban, the activation ban and the
cost modifiers — and all three converge on one private `Bans.NamesTheSame` (the modifiers
reach it through the public `Bans.NameMatches`), so no caller can decide the null for itself.
There is a test whose fixture is a permanent printing the ban with nothing that ever names.

#### The offer is narrowed twice, and the second narrowing is the card text

CR 201.4 lets a player name any card in the Oracle reference. No board can show thirty-two
thousand options, so the offer is the names in this game — the same narrowing
`CreatureTypesInPlay` makes. But it is also narrowed to **what the chooser is allowed to know**:
the public zones plus their own hand, never an opponent's hand and never a library. That is not
tidiness. Sorcerous Spyglass and Anointed Peacekeeper spend printed card text on the words
"look at an opponent's hand" before they name, and Meddling Mage does not — an offer built by
scanning every zone would have handed the Mage exactly what those two are printed to buy.

#### One matcher recognising a shape it cannot build

`Activated abilities of sources with the chosen name cost {2} more to activate` was matched by
the general ability-cost pattern, whose `what` group swallowed "sources with the chosen name",
failed to map it to a filter, and **returned false from the whole reader** — so the line never
reached the matcher written for it and Skyseer's Chariot stayed one line short after the work
that was meant to finish it. A matcher that recognises a shape it cannot build has to run after
the one that can.

#### Declined here, with the measurement behind each

- **"Look at an opponent's hand, then choose any card name"** — 2 (Sorcerous Spyglass,
  Anointed Peacekeeper, whose other three lines now all read). Realising the hand-look as "its
  names are among your options" is exact at two players and strictly better than printed at
  four, since the offer would carry every opponent's hand; doing it properly needs a second
  question naming which opponent, which the entry-choice mechanism has no room for.
- **"You have protection from the chosen card name"** — 1 (Runed Halo). A protection whose
  subject is a *player* and whose parameter is a name: neither half fits the keyword flag, and
  it wants a player quality carrying a predicate.
- **"Choose a card name. Search target player's graveyard, hand, and library for all cards with
  that name and exile them"** — 6 (Cranial Extraction, Memoricide, Slaughter Games, Stain the
  Mind, Ancient Vendetta, Infinite Obliteration), measured by cutting the whole chain. Cutting
  only the `Choose a … card name.` sentence completes **0**, so the name is not what blocks
  them: a multi-zone search and a hand reveal are, and neither exists.
- **`{U}: Counter target spell with the chosen name`** (1, Declaration of Naught) and
  **"Whenever an opponent casts a spell with the chosen name…"** (1, Silverquill Silencer) —
  the name as a *target filter* and as a *trigger condition*, each one card.
- **The literal-name families** the same substring collects: "conjure a card named X" (34,
  Alchemy), the two-zone "search your library or graveyard for a card named X" (45), "the
  number of cards named ~ in your graveyard" (108) and token naming (90). `SearchFilters`
  already has `name:` and reads the single-zone search; what blocks these is the zone list, the
  counting, and conjure — not the name.
Coverage is **54.3% of playable cards fully read** (17,772 of 32,717), 70.0% of lines.

### Round twenty: a full stop inside a quotation, and the sentence undying spells out

Round nineteen wrote the rule down — **a join inside a quotation is not a join** — and left its
sibling measured but unbuilt: the same thing said about the **full stop**, in the readers one level
out. `MayPayLine` and `IfYouDidLine` cut each branch of an offer at `[^.]+`, which stops one
character short of the closing quote on any consequence carrying a quoted ability, so the anchor
could never be reached and the whole line went unread.

It is one shared pattern now, `EffectPhrase.SENTENCE` — a run of anything that is neither a stop
nor a quote, *or* a whole quoted span. The two alternatives are disjoint on their first character,
so the run is linear and there is nothing to backtrack over; an unbalanced quotation matches
neither and simply ends the run, which is the fail-closed answer.

**The count the decline carried was right and its examples were not.** Three cards were named;
three landed, and only one of them was on the list — Minion Reflector, plus Digsite Engineer and
Indoctrination Attendant. Hofri Ghostforge and Giant Inheritance are blocked by something else
again, and Brenard, Ginger Sculptor gained its whole second ability while staying one line short.

**The same stop was refusing the Clone family, and that refusal had outlived its reason.**
`EntersAsACopyLine` documented `[^.]` as deliberate — "a granted ability is not something an
exception clause can express here" — which stopped being true when the token copies taught
`CopyException` to carry `GrantedText`. `GenerativeEffects.Excepting` is the same call on both
paths, so Copycrook, Phantasmal Image, Mercurial Pretender and Machine God's Effigy needed nothing
but the pattern. The clause reader still refuses an ability it cannot compile, which is where the
fail-closed promise actually lives.

#### The one-shot grant was not the gap; the words inside the quotation were

The decline said "19 cards wanting a floating layer-6 effect with a duration". Re-measured, the
frame reads and has for a round: `Until end of turn, target creature gains "{T}: Add {G}"` compiles
today. **44 corpus cards are one line short with that frame**, and every one of them is blocked by
the ability inside the quotation marks — which is why a duration would have bought nothing.

Nine of the 44 print the same inner sentence, and it is one the engine already had under another
name: **"When this creature dies, return it to the battlefield tapped under its owner's control"**
is undying with the counter taken out and the word *tapped* put in. `ReturnSourceFromGraveyard` was
reachable only through the keywords; it now has a `Tapped` flag, emits its counter only when there
is one, and `EffectPhrase` reads the printed sentence into it.

**Three refusals sit around that reader, and the third is the interesting one.**

- "Under **your** control" is a different player. Nine corpus lines say it, and this effect returns
  the card to whoever's graveyard it found it in.
- "...**at the beginning of the next end step**" is a delayed ability; a pattern loose enough to
  take it would fire the return immediately.
- "**Exile ~, then return it to the battlefield**" is a *blink*. The card is in exile, not in a
  graveyard, so the effect would find nothing — a card that compiles clean and does nothing, which
  is worth strictly less than an unread line. Both cards that print it (Ojutai Exemplars, Estrid's
  Invocation) completed before the guard went in.

  The guard is on the **whole phrase**, not on the sentence, and it has to be: the free branch of
  an offer and its "if you do" are parsed one at a time and neither can see the other, and the
  offer leaves `TryParse` by its own early return long before the sentence loop ends. So `TryParse`
  is now a wrapper — `TryRead` does the reading, and the refusals that can only be seen from
  outside sit around it. Nothing legitimate is caught by this one: a permanent in exile is not a
  permanent in a graveyard, so no printed card does both.

#### Result

**17,757 → 17,772 complete cards, +15, none lost.** Diffed as a set and again as a per-card
fingerprint of every compiled ability: **17 cards moved**, the 15 plus two that gained an ability
while staying incomplete — Bail Out reads its grant and is still short its Overload line, Brenard,
Ginger Sculptor reads its whole dying-copy trigger. Nothing lost a clause inside a card that stayed
complete.

#### Declined, with the measurement behind each

The quoted-frame quadrant, re-measured after this round by swapping a known-readable ability into
every quotation on the card and separately compiling each quoted ability as a card of its own:
**571 cards one line short whose blocker carries a quotation** — 16 where both halves read, 142
where the frame reads and the inner does not, 232 where the inner reads and the frame does not, 181
neither.

**All sixteen of the "both readable" cards want one thing: a quoted ability that is a *static*.**
`TryQuotedAbility` takes a trigger, an activated ability or a mana ability, and nothing else, so
`Equipped creature has "Equipped creature has lifelink."`, `All Sliver creatures have "~ gets +1/+1
as long as you control a Swamp."` and `Enchanted creature has "Cumulative upkeep {1}."` are refused
at the frame. It is also what most of the 232 want, and what the two families round nineteen listed
want — `~ crews Vehicles as though its power were 2 greater` (17 cards touching, 6 sole) and `~
can't block or be blocked by non-Spirit creatures` (4) are both statics with no reader at all, so
the quotation is not what blocks them. A granted static is a real piece of engine work: the
receiving permanent needs the effect in the layer the static belongs to, which is not layer 6, and
`CharacteristicsBuilder` has no slot for one.

**Group nouns: the decline is stale, and what is left is not `TryGrantedAbility`'s.** Round
nineteen's `ReadGroupFilter` extraction landed and that reader asks it. Of the frame-blocked
remainder, **7 lines** name a group the shared filter cannot spell — "Each land and Ally you
control", "Creatures you control with flying", "Creatures and enchantments you control" — which is
compound and with-clause vocabulary in the group filter itself, shared by every lord.

**Emblems are the largest single missing frame in the quadrant: 23 cards** print `−N: You get an
emblem with "Q"`, plus 2 for an opponent and 5 for Alchemy's one-time boon. CR 114's emblem is an
object in the command zone that the engine has no notion of.
Coverage is **54.4% of playable cards fully read** (17,794 of 32,717), 70.0% of lines.

### Round twenty: the frame population was a measurement artefact, and there is no frame family

Round nineteen's census left three numbers that five rounds have been steering by: 4,549 sole
blockers across 4,177 shapes at 1.09 cards each, no sentence template worth more than 8 — and
**8,435 incomplete cards "blocked by a line's frame, not by any sentence", said to be larger than
every sentence family combined.** That last figure was the biggest unexplored thing anyone had
identified. It does not exist.

**This section supersedes every census figure round nineteen produced.** Where a number below
disagrees with one quoted elsewhere in this document or in a brief written from it, the number
below is the one measured against the compiler's own text; the older one was measured against
text the compiler never read. The corrected TSVs and the scripts that slice them are in the
shared scratchpad under a `-r20` suffix - re-slice from those rather than re-deriving.

#### What the excision was cutting from

The census works by excision: take a card the compiler could not finish, cut out the sentences it
reported unread, recompile, and see whether the rest reads. `allCut = 0` — the card still not
complete after every unread sentence is gone — was read as "the blocker is the line's structure".

The sentences came from `CompiledCard.Unhandled`, which is the compiler's **normalised** text.
`CardCompiler.Lines` folds the card's own name and every "this creature"/"this spell" to `~`,
rewrites "enters the battlefield" to "enters" and the long spelling of "dies" to "dies", strips
ability words and reminder text, and normalises typographic quotes. The excision cut those
sentences out of the **raw** oracle text. On any card that refers to itself, the sentence being
cut simply was not there to find, the cut was a no-op, and the card was recorded as frame-blocked.

Measured on round nineteen's own two files, `units-r19s3.tsv` against the oracle text in
`dump-r19s3.tsv`:

| | every unread sentence found in the text | some missing | none found |
|---|---|---|---|
| recorded sentence-blocked (6,641) | **6,270** | 371 | 0 |
| recorded frame-blocked (8,435) | 903 | 2,495 | **5,037** |

**7,532 of the 8,435 had at least one sentence that could not be located in the text the excision
ran against.** The correlation is not subtle: 7,332 of the frame population print `~` in an unread
sentence, against 371 of the sentence-blocked population.

#### Re-run against the compiler's own text

Excising from `CardCompiler.Lines(card)` — the same strings the compiler read — and recompiling
with the remainder:

| | round nineteen | corrected |
|---|---|---|
| incomplete cards | 15,076 | 14,960 |
| complete once every unread sentence is cut | 6,641 | **14,927** |
| **still incomplete: the "frame" population** | **8,435** | **33** |
| sole-blocker units | 4,549 | **8,817** on 8,795 cards |

The frame population is 33 cards, and none of them is a frame family. Every one is the compiler's
own multi-reading structure reporting text that was never on a printed line, so no excision from
the printed lines can remove it: **12 Cases** (three sections, and the section split leaves a bare
`.` and `Solved —` behind), **11 gift** cards and **4 cleave** cards (the promised and cleaved
readings are compiled separately and their unread text is synthesised), **3** whose quotation was
lifted out of a token or copy line leaving `""`, and three singletons.

#### And the corrected population is flatter, not richer

Doubling the sole-blocker set did not produce a head. Ranked by normalised shape:

| | shapes | cards each | largest |
|---|---|---|---|
| round nineteen (4,549 units) | 4,177 | 1.09 | 8 |
| corrected (8,817 units) | 8,139 | **1.08** | **10** |

The 4,430 newly visible cards are overwhelmingly the self-referential ones — 3,820 of their 4,444
sole-blocker sentences print `~` — which is to say the half of the corpus the old instrument could
not see was permanents, and permanents are exactly where triggers and activated abilities live.
So the flatness now holds on the population that was supposed to be structurally different.

#### The frame, asked properly: which half the compiler blames

The question round nineteen was reaching for has an answer, and it is a different instrument: for
each sole-blocker line, swap the head for one the compiler certainly reads (`When ~ enters,`,
`{T}:`) and ask whether the body compiles; swap the body for `draw a card` and ask whether the
head does. Over all 8,817:

| kind of line | units | head blamed | body blamed | both | composition |
|---|---|---|---|---|---|
| plain sentence — no frame at all | 5,045 | — | — | — | — |
| trigger | 2,844 | 552 | 1,839 | 446 | 7 |
| activated | 760 | 118 | 599 | 43 | — |
| static `as long as` | 168 | 7 | 100 | 24 | 37 |

**The frame is the blocker on 677 units — 7.7% of the corpus's remaining work.** Where a line has
a frame at all, the compiler blames the effect roughly four times more often than the frame. And
the 677 are flat in their own right: **515 distinct head shapes, 1.31 cards each, largest 9.** Cut
in two it stays flat on both sides — 552 trigger conditions, and 118 activation costs across 88
shapes whose largest is 7 ("{1}, remove a +1/+1 counter from a creature you control").

One caveat on that table, because it undercounts one arm honestly: the body probe substitutes an
`enters` head, so a body naming the trigger's *subject* ("destroy that creature") cannot read
under it and lands in "both" rather than "body". The 18 "both" rows in the family taken below are
exactly that shape.

The other side of that table is the work queue, and it is flat too. The 7,583 blamed effect
bodies — the 5,045 plain sentences plus the 2,538 whose frame reads — are **6,775 distinct
shapes at 1.12 cards each, largest 12**, and four of the six largest are mechanics the engine
does not model at all: a spellbook draft (12), an attraction (11), specialize (9), a sticker
(9). The two that are ordinary Magic are "~ isn't a creature" (10) and the Laccolith family's
"if you do, ~ assigns no combat damage this turn" (10).

**The conclusion for the rounds after this one: stop looking for families.** Three independent
rankings of the corrected census — whole sentence (largest 10), effect body (12), trigger head
(9) — all top out in single figures or barely past them, on twice the data that produced the
last flat verdict. What is left is a long tail, and the only things worth taking out of it are
*grammars over readers that already work*, which is what this round took.

#### Taken: the three qualifiers on "becomes the target"

`~ becomes the target of a spell( or ability)?` was the whole of the pattern. The corpus prints
that sentence on **112 cards** with three independent qualifiers on it, and the old pattern
matched eight of them — so what did the targeting, whose it was, and which of two permanents the
sentence is about were each an unread line rather than a word. Rewriting every printed form to the
bare one and recompiling measured the ceiling first: **42 incomplete cards** complete if the
condition reads.

The grammar multiplies rather than enumerates, the way `Specs.Parse` and `ZoneChangeLine` do:
subject (`~`, enchanted/equipped creature) × what did it (`a spell`, `an ability`, `a spell or
ability`, `an instant or sorcery spell`, `an Aura spell`) × whose it was (`you control`, `an
opponent controls`). The kind is read from computed characteristics rather than the printed card
(CR 613), and a spell or ability the state cannot find on the stack makes the qualified readings
answer no rather than fire blind.

**"For the first time each turn" is deliberately not in that grammar.** It is a limit on how often
the ability triggers (CR 603.1) — the same thing "this ability triggers only once each turn" says
after the effect — and the compiler has had `OncePerTurn` for it all along. Lifted off the
condition before any reader sees it, for the reason the sentence form is lifted off the effect:
every condition reader is written against the event on its own. 33 corpus cards print it across
23 different conditions, and **12 cards outside this family completed on that rewrite alone**:
life gained (Attended Healer, Cleric of Life's Bond, Vanguard Seraph, Deathless Knight,
Gourmand's Talent), life lost (Gonti's Machinations, Vengeful Warchief, Intermediate
Chirography), counters put on (Axgard Artisan, Danny Pink), discard (Rielle, Veronica) and
surveil (Whispering Snitch).

#### The pronoun, and the one event that names two objects

`TargetsChosen` carries the spell or ability that did the targeting **and** the permanent it was
aimed at. `Game.SubjectObjectOf` can only answer with one, and it answers with the spell, because
ward's "counter it" is much the commonest sentence written on that event. So "put a +1/+1 counter
on it" — seven corpus cards, Heartfire Hero among them — would have compiled, resolved, and
put a counter on an object in the stack zone: reads perfectly, plays as nothing, counts as coverage.

The condition already knows which of the two it means, because it said `~`. So the ability records
the answer — `TriggeredAbilityDefinition.SubjectIsSource`, set from
`TriggerConditions.TargetsTheSource`, one query and an allow-list whose default is false, exactly
as `NamesAnObject` and `DeclarationSubject` are set — and `Game.Consider` uses the source as the
subject for those abilities instead of asking the event. The attached subjects are deliberately
**not** in that allow-list: "enchanted creature becomes the target" is about the host, which is a
third answer, so those cards read only the sentences whose effect names no object at all.

The compiler withholds the flag from any ability whose effect names "that spell" or "that
ability", because both readings cannot sit on one ability. Ten corpus cards print the pair —
Frost Titan, Reality Smasher, Glyph Keeper, the two Glasskites, Bonecrusher Giant, Forsaken
Wastes, Lava Runner, Retromancer and Agrus Kos — and they stay unread rather than wrong.

The behaviour test that puts ward and one of these triggers on the same permanent is the
regression that catches the override leaking into the keyword: both fire off one event, and
countering the creature instead of the spell is a silent no-op that no test asserting the
trigger alone would notice.

**+37 cards, and the set diff is one-sided: 37 gained, 0 lost** — 25 from the targeting grammar,
12 from the first-time-each-turn lift. A fingerprint of every card's compiled abilities moved on
44 cards; the seven that were not the 37 each gained an ability and are still incomplete for
another line (Recruit Instructor, Brave Meadowguard, Altanak, Eternal Scourge, and the two Classes
whose level triggers the lift unlocked). One of the seven *changed* rather than gained: Task Force
compiled "it gets +0/+3" as a source pump while the pronoun had nothing to mean, and now reads it
as the pronoun the card prints, aimed at the same permanent.

#### Declined here, with the measurement

- **A group subject — "whenever a creature you control becomes the target" — 12 cards.** The
  targeted permanent is not the source, so the pronoun would need `SubjectObjectOf` to answer with
  the *other* object the event carries, which is ward's. Half these bodies say "it gets +3/+3"
  (Wild Defiance, Daru Spiritualist) and would land on the spell. Wants the verb added to
  `ZoneChangeLine`'s subject grammar *and* a second subject slot on the event, not a third pattern.
- **A compound condition — "whenever ~ enters or becomes the target", "attacks or becomes the
  target" — 5 cards.** A disjunction of two conditions the compiler reads separately; the trigger
  builder has one predicate slot and no notion of "or".
- **"Whenever one or more X …" batch triggers — 48 head-blamed cards, the largest head prefix in
  the census.** It is 39 distinct shapes with a maximum of 5, across ten different event kinds
  (enter, die, deal combat damage, be put into a graveyard, attack, leave the battlefield, become
  tapped, phase out, be exiled, have counters put on). Rewriting to the singular is the trap CR
  603.2c names: "whenever one or more creatures die" fires once, and the singular reader fires per
  creature, so the rewrite prints a strictly better card. Each event kind needs its own batch
  event, which is ten pieces of work for a family whose head is 5.

### Round nineteen: the recorded set as an object, and the second fail-closed line

Round eighteen built `ResolutionRecord` and read three of "this way"'s five grammars off it — a
count, an amount and a did-it conditional. It left the other two, **the set as an object** and **a
member of it**, measured at 38 and 12, on the grounds that the effects which act on a recorded set
did not exist and that most sources of one are a deferred look. Both halves of that were right,
and the honest number is smaller than either figure — which took re-measuring rather than
believing the table.

#### What the family is worth now, measured the way round eighteen measured it

748 corpus cards are one line short of complete with that line printing "this way". Swapping the
this-way *sentence* of each for `Draw a card.` and recompiling completed **143** of them before
this round — the whole remaining ceiling. Grouped by the participle sitting immediately in front
of the phrase:

| participle | reachable cards | recorded |
|---|---|---|
| dealt damage / prevented | 39 | **no** — a magnitude, not a set, and a shield spends no event |
| discarded | 21 | **no** — chosen, and deferred |
| exiled | 14 | yes |
| countered | 11 | **no** — `ExileCounteredLine` rewrites the previous effect instead |
| sacrificed | 10 | **no** — chosen, and deferred |
| milled | 9 | yes |
| destroyed | 8 | yes |
| put into a graveyard | 6 | yes |
| put a card into your hand | 6 | **no** — the look that produced it is deferred |
| revealed · enters/created/cast · regenerates | 9 | **no** |
| returned | 3 | yes |
| put onto the battlefield | 3 | yes |
| drawn | 2 | yes |
| counters put on · searched | 3 | **no** |

**44 of the 143 name a participle the record carries**; everything else is refused by name for the
reason round eighteen wrote down. Ten of those 44 are what this round is worth.

#### The set as an object is three readers and one phrase

The phrase is the one the count and the condition already read, so it goes to the same
`TouchFilter`: "a permanent card **from among the cards** milled this way" is folded into "a
permanent card milled this way" before anything reads it, because half this family prints the noun
and the participle side by side and half prints them either side of an interposed clause, and they
mean one thing. What is new is where the answer goes.

- **`TakeFromTouched`** — the recorded set as the object of a verb. It offers the cards rather than
  moving them, because which one is a decision; the ceiling and the minimum are the only things
  that differ between "you may put a permanent card ... into your hand" and "return a creature card
  ... to your hand", so those are one effect and not two.
- **`MayPlayTouched`** — the set given a window to be played in (CR 601.3e), reusing the event the
  impulse-draw pair already emits. Deliberately not folded into that pair: "this way" points at
  whatever exiled them, which on Heartless Conscription is a sweeper two sentences back.
- **A characteristic of the one thing** — "where X is the mana value of the permanent exiled this
  way", the fourth grammar and the only one that is not a number of things. Written in the singular
  and answered in the singular: nothing touched and several touched both come to zero, because a
  phrase saying "the permanent" has not said which one.

That third reader is why `Touch` now carries the size the object last had. CR 608.2h's last known
information for a permanent is what it was *after* the layers, so a 2/2 under an anthem that gets
exiled was a 4/4 — and a record keeping only the printed card would have answered 2, on a card
that compiles, plays and looks right.

#### The second fail-closed line, one step past the verb list

Round eighteen drew the line at the verb: nothing a player answers may be recorded, because the
engine settles those after the resolution. **Taking a card out of the set is itself a question**,
so the line moves out one step — a sentence asking about *the take* is asking about an event that
has not happened either.

Cache Grab is the whole argument. "Mill four cards. You may put a permanent card from among the
cards milled this way into your hand. If you control a Squirrel or **returned a Squirrel card to
your hand this way**, create a Food token." The middle sentence is readable now and the last one
never will be, so the card stays unread — while Sparring Dummy's second sentence asks about the
*mill* instead and is answerable. One word apart, and nothing downstream could tell a card that
answers wrongly from one that answers.

#### A subtype in the noun, which cost one lower-casing

"If a Pirate was exiled this way", "at least one Angel card is milled this way", "another Desert
was returned this way" — round eighteen measured these at 16 clauses and declined them. Measured
as whole cards they are worth four, three of which also want the taking. The reader could not see
them for a reason that looked like a missing vocabulary and was a missing *distinction*: `ThisWay`
lower-cased every phrase on the way in, and a capital is the only thing on the page that marks a
subtype. Case is kept now, and every comparison asks for it to be ignored.

**The guard is what makes that safe.** The type table is asked first, because the opening word of a
sentence carries a capital whether it is a subtype or not: "Land card milled this way" read the
other way round would demand the *Land subtype*, which no card in the game has, and the clause
would answer no for ever on a card that compiles clean.

#### Result

**17,641 → 17,651 complete cards, +10, none lost** — Arid Archway, Escape to the Wilds, Foul
Renewal, Leyline Dowser, Monster Manual, Renegade Reaper, Ruinous Intrusion, Siren's Ruse, Szarekh
and Wasteful Harvest — diffed as a set and again as a per-card fingerprint of the compiled
abilities. The fingerprint moved on twelve cards; the two that are not the ten each **gained an
ability while staying incomplete**: Liliana, Untouched by Death now reads its Zombie clause, and
Terra, Magical Adept now reads its mill and its take. A corpus-wide census of effect names rose in
fourteen places and fell in none, which is the check that says no card lost a clause *inside* a
card that stayed complete — the failure a set diff cannot see. The family goes 758 → 748 sole
blockers, 948 → 936 touched.

#### Declined, with the measurement behind each

34 of the 44 are left, and a third of them are not about "this way" at all. The probe that says so
is the one that compiles `You gain 1 life for each <phrase>.` and its siblings with an ordinary
board count in place of the this-way phrase: where that is unread too, the family is not the
blocker.

- **A gate outside the family — 7.** "Add {B} or {G} for each X", "create an X/X token, where X is
  the number of X", "for each X, you create a token", "the greatest power among X", "put X counters
  on *a* commander creature you control", "if X is 6 or more" — every one unread with a board count
  in place. Astarion's Thirst, Culling Ritual, Discerning Taste, Dread Summons, Phyrexian Rebirth,
  Zero Point Ballad, and Stitcher Geralf, whose "exile up to two creature cards put into graveyards
  this way" *does* read now and whose third sentence does not.
- **The "its controller" pronoun guard — 6.** Descent of the Dragons, From the Ashes, Hour of Need,
  March of Souls, Martyr's Cry, Rampage of the Clans. Round eighteen's guard, still right: fronting
  the count gives the tokens to whoever "its" resolves to once.
- **A trigger inside the resolution — 3.** "When one or more nonland cards are exiled this way, ..."
  is a reflexive trigger and not a later sentence of the same instruction. Augusta, Gilgamesh,
  Vivien's Invocation.
- **A relation between the things — 3.** Grindstone, Sphinx's Tutelage, Triple Triad.
- **A possessive naming somebody else — 3.** Deadly Tempest, Oversimplify, Cut a Deal: a different
  number for each player asked, and one total would be wrong for all of them.
- **A deferred source under a recordable participle — 3.** Cache Grab's condition names what was
  taken, Expand the Sphere's put-onto-battlefield comes out of a search, Danse Macabre's return
  names what was sacrificed. Each would answer nought for ever.
- **The rest — 9, one shape each.** A token copy of a member (Ardyn), "a permanent you controlled
  **or a token**" (Break the Spell), an until-loop (Dream Harvest), a present-tense active clause
  with three other defects beside it (Flood of Tears), a per-card permission carrying a mana rider
  (Heartless Conscription), an unless-payment under a leading for-each (Read the Runes), a conjure
  (Sheoldred's Assimilator), a delayed trigger the record does not outlive (Song of Blood), and a
  trailing "if" the sentence grammar does not read (Sparring Dummy).

### Round nineteen: a token that carries the abilities its card granted it

Three rounds walked into the same wall from three directions and each wrote it down as somebody
else's work. The frame round built `As long as <cond>, ~ has "Q"` and declined **27 token frames**
because "`CreateToken` cannot carry granted abilities". The token-copy round declined **7 cards**
whose exception grants a quoted ability, because a token copy keeps the copied card's oracle id
and `CompiledPool` throws when two cards share an id with different text. The prohibition round
measured **27 "can't block" cards** inside created tokens, found that swapping the prohibition for
a keyword completed **0** of them, and correctly reattributed the blocker to the token line's
`with <kw> and "<quote>"` shape.

**The union of the three is 44 cards, not 61.** Measured before building anything, from the
compile dump and five substitution probes over the 191 cards that are one line short with a token
line carrying a quotation:

| rewrite of the unread line | completes |
|---|---|
| a keyword list **and** a quotation → the quotation alone | 13 |
| a keyword list **and** a quotation → the keywords alone | 10 |
| `You create` → `Create` | 7 |
| a copy exception granting a quotation → the exception alone | 2 |
| two quotations → the first alone | 1 |
| any quotation on a created token → deleted | 29 |
| **union** | **44** |

**And the capability all three wanted already half existed.** A token *is* a `CardDefinition`, the
pool compiles its text, and a quoted ability has therefore always become the token's rules text —
which is also how a printed token card says it. Two things were missing, and neither is a new kind
of thing:

- **A slot that holds a keyword list *and* a quotation.** `CreatureTokenLine`'s ability slot was an
  alternation, so `with flying and "this creature can block only creatures with flying"` — the
  commonest shape in the family — matched neither arm. It is now an optional keyword list followed
  by *any number* of quotations, which is what the cards print; Pursued Whale's Pirate has two.
- **An id that can tell a granted card from the card it copied.** `TokenCards.Granting` appends the
  granted text and re-keys the definition with a stable hash of it, so the pool sees a card of its
  own. The ungranted copy's id is left exactly where it was, deliberately: the reason it keeps the
  copied card's id is that an ability source keyed by it should serve abilities already compiled,
  and only a grant has any reason to break that.

**Everything the sentence grants goes to the same place, and the keyword flags are only the cheaper
route.** A word the flags cannot carry becomes a line of the token's text rather than refusing the
sentence — `Toxic 1.` is a real printed grant with no flag behind it, and the compiler has read
that line on a card's own text for as long as toxic has existed. The promise is kept by the probe
that was already there: the minted token has to compile completely or the line stays unread, so
`frobnication` still costs the card. Only two words in the whole corpus take that route beside a
quotation — `toxic 1` (15 cards) and `vanishing 3` (1) — and the fold is worth another eight on its
own through `prowess`, `training`, `banding` and `firebending N`.

**A join inside a quotation is not a join — for the third time.** `TryCopyExceptions` splits its
clause list on "and" and on commas, and a granted ability contains both: Electroduplicate's
exception was being cut into "it has haste" and two sentence fragments. The work queue's naive
split and the static conjunction's clause splitter had each paid for this before it, which is why
it is now written as a rule rather than as a fix. The quote tally needs a cursor of its own,
too: counted from the start of the pending clause it re-counts the same opening quote once per
skipped join, so the tally flips back to even on the *second* join inside one quotation and
cuts there. Mythos of Illuna is the card that cost — its granted ability has two commas in
it — and it is the forty-sixth gain.

**The pronoun stops being ambiguous once the ability is on the token.** Round eighteen refused
"Create a token that's a copy of target creature, except it has haste. **Sacrifice it** at the
beginning of the next end step" because the pronoun ladder answers "it" with the creature that was
copied — three cards were complete and sacrificing the wrong permanent. The *same sentence inside
the quotation* is unambiguous and now reads: it is the token's own ability, so its "this token" is
the token. Both are asserted, one either side of the line.

**+46 cards, −4** (17,641 → 17,683, by set difference), and the per-card effect diff moved 84 rows:
the 46, the 4, **16 that are only the new `CopyException` field printing in a record's
`ToString`**, and **21 cards that gained or lost an ability while staying incomplete** — Sword of
Body and Mind's Wolf, Mite Overseer, Chandra Flameshaper and fifteen more gained one; Mana Cache
and Cavern-Hoard Dragon lost one, for the reason the four losses have.

**The four losses are the round's other finding, and the invariant suite is what found it.** One
card this round completed — Curious Herd, "Choose target opponent. You create X 3/3 green Beast
creature tokens, where X is the number of artifacts *that player* controls" — failed
`Every_compiled_card_is_structurally_sound` with "target 0 is chosen and never used". It was right,
and the cause was older and wider than the line that exposed it: **a counted group's filter is
handed one player, the controller of what is counting**, so a clause naming a seat only the
resolution knows had nothing to compare against and the filter answered true for *every* permanent.
Played, Curious Herd counted its own artifacts along with the target's.

Four cards were complete and playing a strictly better version of themselves: **Anathemancer** dealt
damage equal to every nonbasic land on the battlefield rather than its target's, **Emissary of Hope**
gained life for every artifact in play, and **Terra Ravager** and **Coastline Marauders** each got
+X/+0 for every land anybody controlled. The phrase is now refused (`SeatRelativeOwnership`), which
is the standing trade: a card that compiles and plays wrongly is worse than one a deck check can
turn away. Reading it properly is a filter that can be handed a player rather than only the
controller — the same measured pass the counting vocabulary has wanted for three rounds.

Only **23 of the measured 44** landed; 23 of the 46 gains came from outside that pool, which is the
keyword fold and the `You create` scope word reaching token lines with no quotation at all. That
divergence is the useful part of the measurement: the union was a ceiling on *one* family and the
build crossed into two others.

**Declined, with the counts.** The 21 of the 44 that did not land are almost all one thing — **the
quotation's own words**, not the frame around it, so no amount of composition reaches them:

| cards | what is unread | |
|---|---|---|
| 6 | `~ crews Vehicles / saddles Mounts as though its power were 2 greater` | Prodigy's Prototype, Defend the Rider, Roadside Assistance, Back on Track, Valor's Flagship, Shorikai |
| 4 | `~ can't block or be blocked by non-Spirit creatures` | a prohibition with a tribe filter |
| 3 | the `. ` inside a quotation ends the branch of an offer | Minion Reflector, Hofri Ghostforge, Giant Inheritance — `MayPayLine` and `IfYouDidLine` cut their branch at `[^.]+`, so a granted ability containing a full stop ends it early. The sibling of the join rule above, in the reader one level out; left for its own measurement rather than widened blind |
| 2 | a quotation nested inside a quotation | Reef Worm, Nesting Dragon — declined on purpose; no printed card nests more than two deep and a depth counter would only hide the day one does |
| 6 | one each: a counting static on the token, a blocking trigger, an upkeep sacrifice with an "if you can't", an attack requirement | |

Two further families were left alone. **`They have "Q"` / `Those tokens have "Q"` as a separate
sentence** — `FoldGrantedTokenAbility` knows two spellings and the corpus prints five — completes
**0** on the probe: every card printing the wider spellings is short something else as well. And
Alchemy's **`perpetually gains "Q"`** (10 cards) and **emblems** (6) are mechanics the engine does
not model at all.

### Round eighteen: the prohibition family is half the size it was ranked at

The shape table put `can't` third at **672 sole blockers / 920 cards**. That number is a
line-level artefact and the family is worth **329**. `sole` there means every unread line on the
card contains the word, and most of those lines fail somewhere else in the same line: excising
each `can't` **sentence** one at a time and asking whether the card then compiles gives 329 cards
completed by exactly one, plus 14 that need two. The rest of the 672 are riding along.

**"Can't be regenerated" is the whole lesson in one row.** Ranked at 72 sole blockers, and the
readable spelling - `It can't be regenerated.`, `They can't be regenerated.` - completes **3** of
the 58 cards printing it. It has read for a long time as a rider on the destruction in front of
it, and what blocks those 55 cards is the destruction: "destroy the creature with the least
power", "destroy each creature with mana value equal to the number of age counters on ~". The
whole family is worth **15**, and 7 of those are the one-shot `can't be regenerated this turn`,
which is a different mechanism (a shield ban with a duration, not a rider on a verb).

The head is flat everywhere else too: **309 distinct sentence shapes for 329 cards**, largest
**3**. So the family is not one build, and the rows below are the decomposition that says which.

| ceiling | sub-shape | what it wants |
|---|---|---|
| 71 | can't be blocked | **24 of them are one join** - taken below |
| 50 | can't block | 27 are a quoted ability inside a created token, blocked by the token line |
| 45 | can't attack | 16 group statics · 6 the Vow rider · 6 an attack tax |
| 26 | players can't cast | CR 601.3 needs a board-read ban with a spell filter - declined |
| 21 | can't attack or block | 16 excise clean; the Gods want conditions `BoardConditions` cannot read |
| 20 | can't be countered | 8 are a group of spells; the rest are mana riders and "the next spell" |
| 18 | can't be the target of | hexproof-from, which the flags cannot parameterise |
| 15 | can't be regenerated | see above |

**+44 cards, none lost** (17,432 -> 17,476, by set difference), across three changes that between
them are one idea: the prohibitions are keywords this engine already models, and what was missing
was the grammar around them.

**A one-shot joined by "and" - 24 cards, and the join was all of it.** "Target creature gets +1/+0
until end of turn **and** can't be blocked this turn" is two instructions, and both halves have
read alone for months. So it is folded into the two sentences the readers know
(`EffectPhrase.FoldConjoinedProhibition`), beside the token-ability fold that does the same thing
for the same reason. Two decisions in it are the correctness:

- **The carried subject is a pronoun where the head chose a target.** Repeating "target creature"
  announces a second target (CR 601.2c) and the card would ask for two creatures where it prints
  one. Asserted directly - the compiled spell has one target slot.
- **The fold is anchored to a clause opening.** Without that, "Up to two target creatures each get
  +1/+0 until end of turn and can't be blocked this turn" folds from the word "target" in the
  middle of itself and hands the prohibition to one creature of the two. Aquatic Ingress is that
  card, and it is left unread rather than read as a worse version of itself.

**A group can now be forbidden something - 15 cards.** `MassStaticLine` could grant a keyword and
could not forbid anything, so "Creatures you control can't attack", "Black creatures can't block"
and "Boars you control can't be blocked by more than one creature" were unread while every
single-creature spelling of the same rule worked. Most map to a keyword the declaration checks
already read off the computed characteristics - defender *is* "can't attack" (CR 702.3b) - so a
granted one is enforced by the code that enforces a printed one. The switch fails closed: a
spelling it cannot map leaves the line unread rather than forbidding the nearest thing it knows.

**One of them was mapped to the wrong rule, and stayed wrong for four rounds.** This passage used
to say that "can't be blocked by more than one creature" *is* menace. It is the opposite of
menace: CR 702.111b is "can't be blocked except by two or more creatures", a **floor** of two
blockers, and the sentence is a **ceiling** of one. Both the group reader and the keyword synonym
table made the same substitution, so Charging Rhino, Bristling Boar, Stalking Tiger, Familiar
Ground and sixteen others were unblockable by a lone creature when their card says a lone
creature is the only thing that may block them. Every one of them compiled **complete**, which is
why nothing noticed: a card read as the wrong rule is indistinguishable from a card read
correctly in any count of coverage, and only the per-card effect diff separates them. The
sentence now builds `MaxBlockers`, the mirror of the `MinBlockers` that generalises menace, and
the group and single-creature spellings write the same characteristic.

**"Can't attack" was missing from the one-shot vocabulary that already had "can't block" - 8
cards.** `CantLine` read `be blocked` and `block` and not `attack` or `attack or block`, so Off
Balance, Change of Heart, Briber's Purse, Alchemist's Vial, Thundersong Trumpeter, Martyred
Rusalka and Netter en-Dal sat unread on half a sentence. The three arms behind that pattern - a
group, a pronoun, a target - had each worked the flag out for itself, so the fourth spelling would
have reached whichever arm the card happened to take; they now share one `Forbidden` table.

**And the arm order was a live defect on ten complete cards.** Widening that pattern exposed it:
the group arm was asked before the pronoun arm, and the group grammar reads a capitalised "That
creature" as a creature *subtype* of that name - the defect its own comments record for "Islands"
and "You". No card has such a subtype, so **Duel Tactics, Mugging, Blindblast, Blood Aspirant,
Stealth Mission, Kappa Cannoneer, Assassin Den, Razzle-Dazzler, Merciless Javelineer and Creeping
Tar Pit** all compiled complete, dealt their damage, and did nothing with the prohibition.
Capitalisation is why it survived - the pronoun list is compared case-insensitively, so a
mid-sentence "it" reached the right reader and a sentence-opening "That creature" did not. Found
by a compiled-effect diff per card rather than by the set of complete cards, which could not see
it: nothing was gained or lost, ten cards simply stopped being blank.

**And the invariant suite caught the same class in the new reader, one card in.** The first build
of the group prohibition read Bothersome Quasit's "Goaded creatures your opponents control can't
block", and `Every_printed_noun_phrase_the_grammar_reads_can_be_satisfied` refused it: goading is a
*designation* (CR 701.15b), the group reader's tribe fallback took the capitalised word for a
creature type, and no card has the type "Goaded". A sixteenth card that forbade nothing. It is now
refused at `GroupAdjective`, in that reader's own idiom for a word it recognises and cannot
answer.

Answering it properly is not a word in a table: goad is applied in layer 6 by a floating
effect and so is the lord, so a static whose permanent arrived first is asked before the goad has
been applied and sees nothing goaded. That is CR 613.8's dependency, which this engine does not
model, and a prohibition that binds on some turns and not others is worse than an unread line.

**Declined, with the measurement behind each:**

- **A cast prohibition** (26). The largest single mechanism left in the family and a real engine
  capability: CR 601.3's "no rule or effect prohibits that player from casting it" needs a ban
  read off the board at cast time, with a spell filter (a colour, a chosen colour, a name, a mana
  value, a card type, a timing window) and a scope. `CastLimit` is the half of CR 601.3 that
  exists, and the note beside it already records that `GameView` carries no castability - so 26
  more cards' worth of refusals would land on a board that offers the spell and then refuses it.
- **A group of spells that can't be countered** (8 of the 20). `StaticBans` is the right home and
  the shape is the life-gain ban's: a board-read prohibition outside the replacement pass. Left
  because the other 12 in the row are three different mechanisms (mana that carries a property,
  "the next spell you cast this turn", a condition on the card's own keyword) and 8 does not pay
  for the third one alone.
- **Hexproof from a quality** (18). "Can't be the target of black spells or abilities from black
  sources" is a parameterised keyword, and the flags enum carries protection from five colours and
  artifacts because those are the qualities it can name. Admitting these as protection would be
  wrong in both directions - protection stops damage and blocking too, and hexproof-from stops
  only an opponent's targeting.
- **A quoted prohibition inside a created token** (27 of the 50 "can't block" cards). Measured, and
  not the prohibition's fault: swapping the prohibition inside the quotation for a keyword the
  granting path reads completes **0** of them. What refuses the line is the token reader, whose
  ability slot is a keyword list **or** a quotation and never `with toxic 1 and "..."` - dropping
  the quotation and keeping the keyword completes 7 cards on its own. A token-line composition
  gap wearing a prohibition's clothes, and it should be counted against the token row.
- **The God cycle's "unless"** (4). "~ can't attack or block unless you control another creature
  with power 4 or greater" reads as a conditional static today; what refuses it is
  `BoardConditions`, which cannot answer the clause. A board-condition row, not a prohibition one.
- **An attack tax** (6). "Creatures can't attack you unless their controller pays {1} for each of
  those creatures" is a cost demanded of a declaration, and nothing in the engine can charge one
  during the declare attackers step.
- **A filtered one-shot** (3). "…and can't be blocked by Walls this turn" needs a block restriction
  with a filter *and* a duration; the static form of it exists and the floating form does not.

### Round eighteen: "this way", and the difference between a ceiling and a reader

The largest lead in the shape table — **797 sole blockers, 991 cards** when re-measured on this
branch — and the one-sentence diagnosis it came with was right: nothing recorded what an effect
touched, so a later sentence of the same instruction had nothing to ask. That is CR 608.2's
"information about what happened during a resolution", and the engine wrote none of it down.

**The record is one field and no state.** `ResolutionRecord` is derived in `Game.RunEffects` from
the events each effect emitted and handed to the next effect through `ResolutionContext.Record`. It
is the twin of the magnitude that loop has carried forward since the day it was written — that one
is "that much", this one is the things themselves. Nothing new is folded, nothing joins
`GameState.Equals`, and `Replay(log) == State` is untouched, because the record lives exactly as
long as "this way" means anything. The id it stores is the one the object has *after* the move
(CR 400.7), which every mover already picks when it builds the event; the card is read *before* the
batch is applied and through `Characteristics.CardOf`, so a Clone of a Grizzly Bears that is swept
away is a creature card destroyed this way.

**It accumulates rather than replacing, and the verb is why.** "Discard two cards, then draw two
cards. For each card drawn this way ..." asks about the draw and says so in the word. Keeping only
the last effect's events would answer the same question by guessing at the order, and would be
wrong the moment a card puts a third sentence between the two.

**Three grammars, one filter.** "For each creature card exiled this way", "equal to the number of
creature cards exiled this way" and "if a creature card is exiled this way" are one question in
three grammars, so the phrase is read once into a `TouchFilter` and each grammar asks for it where
it stands. The count went into `CountingAmount` — the one place in the counting vocabulary that
holds a `ResolutionContext`, since `CountFn` is handed a state and a player and neither knows what
the sentence before just did — so all eight positions that scale an amount read it at once. The
condition is `OnlyIfTouched`, its own effect rather than an arm of `OnlyIf`, for the reason
`OnlyIfRollAtLeast` is: `OnlyIf` is given a state and a source, and every condition it can express
is a fact about the board.

#### The measurement that decided the size, and it is not 797

A sole-blocker rank is an upper bound on a reader's worth, not a forecast. Each of the 795
one-line-short cards had its this-way sentence swapped for `Draw a card.` and was recompiled:
**224** completed, so on the other 571 the line carries a second defect and no reader of this family
can reach them. The 224 decompose as **did-it conditional 93 · a count 56 · the set as an object 38
· an amount 27 · a member and one-offs 12** — near enough the shape table's proportions, at a
quarter of its size. Priced again with a substitution probe that keeps the rest of the sentence, the
union is **75**. And even that is above what may honestly be built, for the next reason.

#### The fail-closed cut, which is most of the family

**This engine defers every question a player has to answer until after the resolution.** A reveal, a
search, a chosen discard, a chosen sacrifice each become an owed question settled by
`SettleBeforePriority`, precisely so that a resolution is never stopped half way through. A sentence
asking "for each card revealed this way" from inside the same resolution is therefore asking about
events that have not happened: it would answer nought, every time, on a card that compiles clean,
plays without an error, and looks exactly like a card that works. Nothing downstream can tell it
from one.

So `TouchVerb` carries only what an effect does *while it resolves*, and `ThisWay` refuses the rest
by name. What that costs, counted in cards the ceiling probe says are otherwise reachable:

| participle | reachable | recorded |
|---|---|---|
| exiled | 31 | yes |
| destroyed | 26 | yes |
| milled | 18 | yes |
| discarded | 17 | **no** — chosen, and deferred |
| prevented | 14 | **no** — prevention is a shield and spends no event |
| countered | 12 | **no** — see the declines |
| sacrificed | 8 | **no** — chosen, and deferred |
| revealed | 5 (98 rows) | **no** — deferred |
| returned | 5 | yes |
| dealt damage | 4 | **no** — a magnitude, not a set |
| drawn | 2 | yes |
| died | 1 | yes |

`DiscardCards` is what shows why the line is drawn at the verb rather than at the card: it emits the
moves directly when the whole hand goes and a `DiscardRequested` when there is a choice. A verb that
is recorded on some cards and not on others is exactly the silent failure the refusal exists for.

#### Two gates on the family that are not about "this way" at all

Both were found by asking the compiler which *positions* read, rather than which lines do — a probe
that compiles `You gain 1 life for each <phrase>.` and reads the answer.

- **The leading "For each X, &lt;effect&gt;" is not read, on 0 of 65.** The count in front instead
  of behind, and every verb already understands it behind — so `TryOne` fronts it and re-offers it
  rather than growing a second grammar for the counted amount. **The guard is what makes that
  safe:** "for each permanent destroyed this way, its controller creates a 3/3 Centaur" is one token
  to each permanent's *own* controller, and fronted it becomes N tokens to whoever "its" resolves to
  once — which on a sweeper that targets nothing is nobody, or the wrong player. Half of this family
  prints such a pronoun. Four cards compiled while the guard was broken by a mangled escape and the
  corpus diff caught every one: Hour of Need, March of Souls, Rampage of the Clans, Descent of the
  Dragons.
- **"Put a +1/+1 counter on ~ for each X" had no counted tail**, while its targeted twin has had one
  for months — `PutCountersOnSubjectLine` simply stopped at the pronoun. Malanthrope, Bane of
  Progress, Whiptongue Hydra and Froghemoth sat behind one missing group.

#### Result

**17,432 → 17,489 complete cards, +57, none lost**, diffed as a set and again as a per-card
fingerprint of the compiled abilities. The fingerprint moved on 76 cards; of the 22 that are not
the 57, eleven are the `attached-count` id-hash noise this file has recorded before, and **eleven
gained an ability while staying incomplete** — Graveyard Trespasser, Avenge, Dune Chanter, Feral
Appetite, Mana Cache, Turf War, Huatli, Dihada and three more, each now reading one line further.
Nothing lost an ability. The family itself goes 797 → 759 sole blockers, 991 → 948 touched.

The four cards the pronoun guard refuses are the measurement of the guard: they are the difference
between +61 and +57, and every one of them would have given the tokens to the wrong side of the
table. The guard was broken for one build by a `\b` mangled into a backspace on the way into the
file, and **the only thing that caught it was the corpus diff** — the build was clean, the suite was
green, and the count had gone up.

#### Declined, with the measurement behind each

- **The set as an object — 38 reachable.** "Put all cards revealed this way into your hand" wants
  effects that act on the recorded set rather than on a target or a group, and almost every source
  of one is a deferred look, so the record would be empty when the sentence ran. Both halves would
  have to move; neither alone buys anything.
- **A member of the set — 12.** As above, one card at a time.
- **"If that spell is countered this way, exile it instead" — 12.** `EffectPhrase` already reads one
  wording of this by *rewriting the previous effect* (`ExileCounteredLine`), which is the right
  shape for it: two effects would counter the spell and then try to exile a card that is no longer
  where the first one left it. The other wordings want that reader widened, not this record.
- **A subtype or a colour in the noun — 16 clauses, largest 2.** "At least one Angel card is milled
  this way", "for each Plains returned this way", "a nonblack card is exiled this way". The filter
  reads card *types* through the shared two-table lookup; subtypes and colours are two more tables
  against a flat tail.
- **A relation between the things — 8.** "Two cards that share a color were milled this way" needs
  the cards compared with each other, which no filter expresses; read as "two cards were milled" the
  card would fire on a pair that shares nothing.
- **A pronoun subject — 6, beside the 12 countered.** "That artifact is put into a graveyard this
  way" names one particular object and this reader cannot tell it from the set. The same refusal the
  "instead" rider makes, for the same reason.
- **A possessive naming somebody else — 14.** "Card exiled from their hand this way", "creatures
  they controlled that were destroyed this way": a different number for each player being asked, and
  one total would be wrong for all of them.

### Round eighteen: a token that's a copy, and the 141 that was really 37

The lead the round-seventeen table ranked first — "a token that's a copy of a permanent, 141
sole blockers, the largest single buildable sub-shape in the file" — **is worth 37 cards, not
141**, and the gap is entirely in how the 141 was counted. A card counts as a sole blocker when
every one of its unread *lines* matches the family substring, and an unread line is a whole
printed line rather than a clause: "{2}: Create a token that's a copy of target artifact. That
token gains haste. Exile it at the beginning of the next end step." is one line, matches the
family, and would still not read if the copy clause were perfect.

Measured the way the "look at the top N" round learned to measure it — cut the clause out and
ask whether the rest compiles — the family decomposes like this. 353 corpus cards print a
token-copy sentence; 95 of them already read (populate and embalm/eternalize are the bulk, and
their reminder text is what puts them in the substring count at all). Of the remaining 258, each
copy clause was rewritten in place onto a token reader that already works — `create a 1/1 white
Soldier creature token`, keeping the frame around it, so an activated ability keeps its cost and
"if you do" keeps its payment:

| cards | what the rewrite showed |
|---|---|
| **37** | complete afterwards — the copy clause is the only defect, and a reader can have them |
| 221 | still incomplete — a second defect on the same line that no copy reader reaches |

The 221 have no head at all. Grouped by the whole line left unread after the rewrite, the
commonest shape is worth **four** cards and it is squad's reminder text; every other row is one
or two. What the lines have in common is not a template but a length — they are two and three
sentence lines, and the copy is one sentence of them. That is the same flatness the corpus has
everywhere else, arrived at from a family that looked like an exception to it.

**+16 cards, 3 lost, net +13** (17,432 → 17,445). The three losses are the round's second
finding and are below; the gains are one-sided. A compiled-effect fingerprint moved on 27 cards
before the losses were taken: the gains, and six that gained an ability while staying incomplete
— Jace, Cunning Castaway's `−2`, The Scarab God's activated ability, Mirror Room, The Cloning of
Shredder, Fable of the Mirror-Breaker's Kiki-Jiki line, and Lorehold Archivist, which is the
repair below. The 88 other cards whose print changed are the record's shape changing, not the
card's: `CreateTokenCopy` gained fields and its default count became explicit.

**Almost all of it was one parser, connected.** `CardCompiler.CopyExceptions` had read CR 707.9b
clauses since the "enters as a copy" work, and `CreateTokenCopy` could not reach it — it carried
its own `ExceptNotLegendary` flag instead, which read one of the dozen printed exceptions and
silently refused the other eleven. That is the vocabulary-restated-in-a-second-pattern bug again,
and the fix is the same one: one parser, and a `CopyException` record both callers hold. The
clause list itself gained the spellings the token half prints and the permanent half does not —
"the token isn't legendary", "they're 3/3 creatures", a size in front of the types, and a bare
keyword inheriting `has` from the conjunction that split it off.

Two things in that grammar are rules rather than patterns:

- **"In addition to its other colors and types" adds the colour; the same sentence without those
  two words replaces it.** Ratadrabik of Urborg's Zombie is black *and* the green it copied;
  Croaking Counterpart's Frog is only green, and has no creature type but Frog. The templating
  draws that line itself and the reader follows it rather than guessing, because a reader that
  treated every colour as a replacement is right about half the family and looks right about all
  of it.
- **The one "and" the clause splitter may not cut on is the one inside that phrase.** Splitting
  it left a fragment reading "types", which matched nothing and refused the line — so *every*
  card whose exception adds a colour was blocked by a conjunction that was not one.

**One repair, and it was a card counted as complete for as long as the reader has existed.**
Lorehold Archivist // Restore Relic reads "Exile target artifact or creature card from your
graveyard. Create a token that's a copy of it." The pronoun names a *card*, and `CreateTokenCopy`
took every pronoun for a permanent: it looked for one that was not on the battlefield, found
none, and made no token at all. Coverage counted the card and nothing ever played it. A copy of a
card is read from the card itself (CR 707.2) rather than from copiable values only a permanent
has, so the effect is told which it is — and the exile in the sentence before is what makes the
last-known-information path load-bearing, because by the time the copy runs the targeted id names
nothing (CR 400.7, 608.2g). Feldon of the Third Path reads its whole ability on the same change,
and is then held back by the sentence after it — which is the next finding.

**And the second finding, which cost three cards.** Kiki-Jiki, Mirror Breaker completed on this
work and then failed the test that plays it: "{T}: Create a token that's a copy of target
nonlegendary creature you control, except it has haste. Sacrifice it at the beginning of the next
end step" sacrificed **the creature it copied** and left the token on the battlefield forever.
The pronoun ladder answers "it" with the target an earlier sentence chose, and on this family the
sentence before it created a token that no referent in the vocabulary can name. That is the
declined "a pronoun naming a token" family arriving from a direction nobody had it arriving from
— and it is exactly the shape this file keeps recording, a line that reads perfectly and plays a
different card.

The sentence is now refused where the pronoun resolves to a target, which cost **three cards that
were complete and wrong**: The Fire Crystal, Tempestra, Dame of Games, and Nemesis Trap, each
sacrificing or exiling the permanent it had just copied. The refusal is deliberately not widened
to the arm where nothing was targeted and "it" falls back to the permanent with the ability: that
one is wrong on a further 14 cards, its own comment in `EffectPhrase` already says so, and
correcting it wants a delayed action that can name a token — which is the measured pass that
comment asks for, not a side effect of this sentence becoming reachable. Refusing it here as well
was measured: 14 cards, all of them currently sacrificing the wrong permanent.

Declined, with counts. The first four rows are what is left of the 37 once the built shapes are
taken out; the last is the five whose copy clause this round *did* build and whose card is
refused by the sentence beside it, so the rows do not sum to 37 - 16:

| cards | shape | why |
|---|---|---|
| 7 | an exception granting a quoted ability — "except it has haste and \"At the beginning of the end step, sacrifice this token.\"" | a token's abilities come from the card it copies, and `CompiledPool` throws when two cards share an id with different text — so the quotation cannot be appended to the copied card's rules text. It needs a granted-ability list on the token, which is an effect change, not a reader |
| 7 | a copied referent nothing names — "the exiled card", "that card", "a card exiled with this Saga", "the sacrificed creature", "a random creature card with mana value X" | each is a different record of what an earlier sentence did, which is the "this way" family |
| 3 | a target phrase that does not parse — "target creature token that entered the battlefield this turn", "target token you control not named ~" | |
| 3 | one each: "loses soulbond", a token created under another player's control, "except it enters with an additional +1/+1 counter on it" | |
| 5 | the copy clause reads and the sentence after it says "sacrifice it" — Kiki-Jiki, Feldon of the Third Path, Molten Duplication, Saheeli, the Sun's Brilliance, The Jolly Balloon Man | the token-pronoun family above; the clause is built, the card is refused |

"Tapped and attacking" is in the reader's decomposition and **not** in the build: 14 corpus
sentences say it and not one of them is on a card the copy clause is the only defect of, so it
would have been machinery with no card to reach it.

### Round seventeen: a guard that forbade every question behind it

+56 cards, none lost, and the change is a guard moved four lines down the method it was already
in. CR 603.4's intervening "if" wraps what a trigger does in an `OnlyIf`, and the compiler refused
**any** line whose guarded effect held a deferred question - an offer, a search, a flip, a roll.
The reason was true when it was written: `MayPay`, `ChooseAndMove`, `FlipCoin` and `RollDice` each
carry a locator back to themselves, the lookup resolved it against an ability's *top-level*
effects, and a wrapped question named the wrapper instead.

**The two halves had never met.** `EffectTree.Locate` has walked the whole tree since the round it
was written for - `EffectPhrase.OneQuestionCanBeNested` says so in its own remarks, and relies on
it for a free offer's branch - and every one of the six deferred questions in `Game` is resolved
through it. Wrapping an effect list in one `OnlyIf` cannot change what `Locate` answers: the
wrapper carries no locator, and `Flatten` yields exactly the same questions with exactly the same
indices underneath it. The refusal had been costing cards for nothing since the day `Locate`
changed.

So the guard now asks the question `Locate` actually asks, of the tree it is about to hand over
rather than the list it started from: no two effects of one kind in the tree may carry one
locator (`EffectPhrase.EveryQuestionFindsItself`, using `EffectTree.LocatorOf` so the check and the
lookup can never disagree about what "findable" means).

**That check refuses zero corpus cards**, measured by disabling it and diffing the complete set -
byte-identical. It is a precondition rather than a filter, and it is kept for the reason every
other refusal here is kept: a guard that is an argument stops being true when the parser changes,
and a guard that is a check does not. The ambiguity it defends is asserted directly - two
`MayPay`s sharing locator 0 make `Locate` answer null.

**The set, not the number.** 17,181 -> 17,237, nothing lost. Land Tax, Knight of the White Orchid,
Valakut, the Molten Pinnacle, Genesis, Oversold Cemetery, Deathreap Ritual, Flamewake Phoenix,
Emeria, the Sky Ruin, Gatekeeper of Malakir, Pyrewild Shaman, Sygg, River Cutthroat, Fathom Fleet
Captain, Mirror-Sigil Sergeant, Pit Keeper, Sand Strangler, Rocco, Cabaretti Caterer, the five
Hedge-Mages and thirty-six more. Every one of them is "at the beginning of X, if `<condition>`,
**you may** ...".

**Five of the seven tests fail against the old code and two must not.** The two that pass either
way are the refusals that had to survive: a guard that fails puts *no question at all* rather than
a question whose answer is discarded, and a condition `BoardConditions` cannot name still leaves
the whole line unread. The second check (CR 603.4) is asserted on the log rather than on a life
total, because an engine that hoisted the offer out of its guard would still swallow the branch
and would differ only in stopping the game for a click that decides nothing - that mutant was run,
and that test is the one that catches it.

### The instrument: rank by sub-shape, not by substring

The dump is `(oracleId, name, isComplete, unhandled lines)` for all 32,717 cards, and the warning
from round sixteen held everywhere it was checked. **Whole-line clustering is flat**: 17,662
distinct templates with numbers and mana symbols folded out, and the largest completes **nine**
cards. Cutting to the one sentence whose *removal* lets a line read is flat too - 8,382 shapes,
largest **ten**. (Both figures were re-measured in round twenty against the compiler's own
normalised text, which roughly doubles the population and leaves the head flatter still - see
"the frame population was a measurement artefact" above.) A substring row like `for each` at 831 is one word across a thousand templates.

What is not flat is the **clause**, and the probe that finds it is an excision: take a structural
clause out of a blocked line, ask whether the rest compiles, and ask separately whether
`BoardConditions` reads the clause. That splits a family into vocabulary and composition, which no
ranking by words can do:

| clause excised | completes the card | clause refused | clause *reads* |
|---|---|---|---|
| intervening `if` on a trigger | 201 | 151 | **50 - the composition gap taken above** |
| `unless <clause>` | 150 | 134 | 16 |
| `as long as <clause>` | 29 | 13 | 16 |

The 50 were the whole round. After it that row reads **0**, and the two 16s are the same shape one
step away.

### Round seventeen's ranked sub-shape table

Re-measured on the corpus after the change. `sole` is cards where *every* unread line matches, so
handling the shape completes the card; `touch` counts cards where any does. The families are
substrings and are listed to be decomposed, not built:

| sole | touch | family | the sub-shapes inside it |
|---|---|---|---|
| 810 | 1009 | `... this way` back-reference | did-it conditional 255 · the set as an object 213 · a count `for each ... this way` 192 · an amount 87 · a member chosen from the set 15 |
| 729 | 930 | `where X is <expr>` | **not one gap but two, and only 325 of the 729 are about the clause at all** — see "Where X is: the clause was rarely the blocker" below |
| 672 | 920 | `can't` prohibitions | can't be blocked 147 · players can't 140 · can't attack 130 · can't block 96 · can't be regenerated 72 · can't be countered 26 · can't be targeted 24 |
| 589 | 818 | a condition clause `, if <cond>,` | 151 refused by `BoardConditions`, flattest head in the file (max 7) |
| 556 | 678 | `shuffle` | 271 of them are a bare trailing `shuffle.` after a search - riding along, blocking nothing |
| 537 | 689 | `if you do` chain | already read in seven places; a co-occurrence row, not a gap |
| 470 | 627 | `copy` | **a token that's a copy of 141** · becomes a copy 87 · copy that spell 67 · new targets for the copy 125 · copy target spell 32 (declined) |
| 433 | 589 | `as long as` | 13 clauses refused; 16 are a group static the condition cannot be hung on |
| 420 | 536 | `each player` scope | 321 sub-shapes - flat |
| 372 | 540 | `each opponent` scope | damage to each opponent 69 · loses N life 57 · a trigger scope 40 · sacrifices 27 |
| 363 | 544 | `you may cast` | |
| 354 | 432 | `search your library` | |
| 329 | 398 | `unless` | a payment offered to a player 57 · a non-mana cost 46 · **a fact about the board 25** |
| 277 | 385 | `named` | |
| 260 | 335 | `equal to the number of` | |
| 228 | 273 | delayed `at the beginning of the next` | |
| 215 | 335 | `the chosen ...` | chosen creature type 70 · chosen colour 47 |
| 178 | 250 | `without paying its mana cost` | |
| 136 | 153 | `of your choice` | |

**The three best-shaped leads in it, measured and not taken:**

- **A token that's a copy of a permanent (CR 707.2) - 141 sole blockers.** The largest single
  buildable sub-shape in the table, and *not* the standing decline: "copy target instant or
  sorcery" is a separate 32 and still needs a mid-resolution question. **Taken in round eighteen,
  and the 141 was wrong**: cut the clause out and ask whether the rest compiles and it is 37. See
  "a token that's a copy, and the 141 that was really 37" above - the count is a warning about
  this whole table, because a sole blocker is a whole unread *line* and most of these lines carry
  a second clause.
- **A result set named by the sentence that produced it - 810 across five readers.** "This way" is
  one mechanism and five ways of spelling a reference to it. Nothing in the engine records what an
  effect touched, which is why every one of the five is unread; build the record and the five
  readers are readers.
- **`<effect> unless <condition>` where the condition already reads - 16 cards.** The direct
  sibling of what this round took: `OnlyIf` with the condition negated. Left for a reason rather
  than for time - `OnlyIf.Resolve` needs `context.PhysicalSourceId` to still name an object, and
  every card in this family that is an instant or sorcery would resolve *from the stack*. If that
  lookup comes back empty the guard silently does nothing and sixteen cards compile and play as
  blanks, which is the worse half of the trade. Check that first; the reader is ten lines after it.

### Round seventeen: the frame around an ability the compiler already understood

**655 cards sat one line short of complete with a quotation in that line, and on 213 of them the
quoted ability compiles through the path that grants one.** The compiler understood the ability
perfectly and could not read the sentence around it. That is a composition gap, not a vocabulary
one, and it is worth measuring separately because the two want opposite work. (An earlier pass put
that figure at 314 by compiling the quotation as a card's own text, which is a different question:
a sentence that reads as a printed line does not necessarily read as a granted ability, and it is
the granted reading the frame needs.)

Measured as a quadrant rather than a ranking, which is what made it actionable. Each of the 655
was asked two questions: does the **inner** quotation read on its own (compiled through
`Enchanted creature has "…"`, the path that grants one), and does the **frame** read once the
quotation is swapped for a keyword the compiler already knows?

| | inner reads | inner does not |
|---|---|---|
| **frame reads** | **56** — pure composition | 128 — needs vocabulary inside the quotes |
| **frame does not** | 157 — needs a new frame reader | 283 |

(plus 31 lines carrying more than one quotation.) The frames, grouped into families:

| cards | family | worth |
|---|---|---|
| 25 | `As long as <cond>, ~ [gets +N/+N] [and] has "Q"` | built |
| 17 | `Enchanted/Equipped X … has <kw> and "Q"` | built |
| 6 | group grant whose noun the grant pattern cannot read (`Creature tokens you control have "Q"`) | declined |
| 3 | `… gains "Q" until end of turn` — a one-shot grant | declined |
| 3 | `create a N/N token with "Q"` | declined |
| 24 | tokens again, with the frame unread as well | declined |
| 16 | one-shot grants, frame unread | declined |
| 10 | Alchemy's `perpetually gains "Q"` | declined |
| 6 | `You get an emblem with "Q"` | declined |

**No frame is worth more than about thirty cards**, which is the same flatness the line ranking
has. The answer to a flat head is not a bigger reader, it is a rule that applies to a family.

Three of them, and none is a new vocabulary entry:

- **A condition distributes over the sentence it governs.** `TryConditionalConjunction` lifts
  `As long as <cond>,` off, splits the remainder into clauses, and offers each one twice — with
  the condition written back in front of it, which is what `TryConditionalStatic` and every other
  conditional reader expects to see; and bare, with the condition applied to whatever came back.
  The second arm is the one a quoted ability needs, because nothing reads a grant with a
  condition in front of it and everything reads one without. It runs **after**
  `TryAttachedConjunction`, so it can only ever see a line every other matcher refused.
- **A bare quotation inherits the verb of its conjunction.** `has flying and "{T}: Draw a card"`
  is two things the subject *has*, and the second says so only by standing beside the first.
  Without the verb the clause is a subject with a sentence after it, which nothing understands,
  and every Aura and Equipment listing a keyword before a quoted ability went unread over one
  missing word.
- **A join inside a quotation is not a join.** The clause splitter tracked quotation marks; it
  had been cutting granted abilities in half at their own commas. This codebase had already paid
  for that exact mistake once, in the work queue's naive split, which reported the orphaned
  quotation mark as the largest blocker in the corpus.

`~` became a subject of the grant pattern to make the first of those pay. **The corpus has no bare
`~ has "…"` line at all** - it was counted, not assumed - so the only way into that arm is through
a frame that lifted a condition off, which is what makes it safe. It is deliberately
still not a subject of `ConjoinedAttachedLine`: a card's own text folded into a static conjunction is
how a spell's one-shot effect would become a permanent one, while a card granting itself a whole
quoted ability is a static ability whatever else is true of it. It needed its own arm in the
recipient test — with no noun and no scope it fell through to the group filter, which reads the
line as "every permanent you control".

**+51 cards, and the set diff is one-sided: 51 gained, 0 lost.** A fingerprint of every card's
compiled abilities — not just the complete/incomplete flag — moved on 71 cards, and the twenty
that were not the 51 divide cleanly: **nine gained a static they had never had** and are still
incomplete, now reading one line more each (Ray of Frost's conditional "loses all abilities", The
Masamune's conditional lure, Diviner's Wand's second quoted ability); the other eleven were id
noise from `attached-count`, whose id embeds a string hash and is therefore not stable between
processes. Nothing lost a static.

Declined, with the measurement: the token frames (27 across both halves) want `CreateToken` to
carry granted abilities, which is an effect change rather than a reader; the one-shot grants (19)
want a floating layer-6 effect with a duration; the group nouns (11) want `TryGrantedAbility` to
take its filter from `ReadStaticGroup` instead of its own hand-written pattern, which is the
"vocabulary restated in a second pattern" bug one more time and the right next thing here;
`perpetually` (10) and emblems (6) are mechanics the engine does not model at all.

### Round seventeen: "look at the top N" decomposed, and the half of it that is one reader

The last large coherent family anybody had identified, and it had been declined once — eight
meaning-preserving rewrites onto the existing `LookAndTake` reader completed 24 cards, and the
verdict was that it "needs several readers plus a mid-resolution chooser, not one". The verdict
was right about the family and wrong about the size, and the way to tell was to stop counting
lines and start asking what each line *needs*.

**The measurement that decided it.** Ranking by sole blockers said 434 cards. That number is an
upper bound on a reader's worth and not a forecast, because a card is only completed if the look
is the *only* thing wrong with its line. So each family line was cut at the look, the whole
instruction was replaced with `Draw a card.`, and the card was recompiled: **560** cards were one
line short with a line mentioning the top of a library, and on **294** of them the look was the
only defect. The other 266 carry a second one on the same line and no look reader can reach them.

That is the number worth decomposing, and decomposed it stops being one family:

| sub-shape | reachable | what it actually needs |
|---|---|---|
| take → battlefield, and the rest go somewhere | 49 | a chooser, a destination, sometimes tapped |
| take (filtered) → hand, and the rest go somewhere | 41 | a chooser and the search's filter vocabulary |
| take (plain) → hand, and the rest go somewhere | 43 | a chooser |
| take **all** of a kind, and the rest go somewhere | 21 | **no chooser at all** (CR 118.3) |
| take → exile | 2 | a chooser |
| **the take family** | **156** | **one reader**, and a ceiling on the answer |
| the top card, conditional on what it is | 40 | a reveal and a condition — not a chooser |
| the top card, other | 28 | as above |
| the top card straight to hand | 6 | as above |
| **the single-card family** | **74** | a reveal that a condition can then read |
| reveal until you reveal a *X* | 27 | a loop with a stopping rule, and no fixed N |
| look, then put them back in any order | 8 | an ordering question, not a taking one |
| cast one of them for free | 4 | casting from a zone that is not the hand |
| piles, "an opponent chooses", planar decks | 25 | one card each |

**So the take family — 156 of the 294 — is one reader and three fields, and that is what was
built.** The two existing patterns became one grammar: the verb may be "reveal" as well as "look
at" (143 of the family open with the other word); the take clause has nine printed spellings that
differ only in the ceiling, the filter and the destination; the rest may go to the bottom, the
graveyard, your hand or exile, or be shuffled back. `LookAndTake` gained `TakeLimit` (the ceiling
on the answer, null for "any number of"), `TakeAll` (every match, asked of nobody), `MaxManaValue`
(the search's own bound, asked of a smaller pile) and `TappedOnTaken`.

**The mid-resolution chooser the family was thought to need already existed.** `ChoiceKind.LookAndTake`
has been a deferred question since the impulse-draw shape was built; what it could not do was
return more than one card. Taking two, or any number, or every match, is the same question with a
different ceiling — so the resolution reads a list of picks instead of one, clamps it to what the
sentence allows, and re-checks every pick against the filter and the bound rather than trusting
the answer. `TakeAll` is the case where the game must *not* stop: the sentence names every match,
so a prompt built from it would have exactly one legal answer, which is the rule the empty filter
already obeyed one branch along.

**Two things the reader had to learn that are not about looking at all.** The idiom is read before
the sentence splitter, because "put one of them into your hand" does not say which them — and it
had therefore been anchored to the *start* of the line, which refused every card printing a
sentence in front of it (Prophetic Bolt, Ral's Outburst, Creative Outburst). It now cuts the line
in front of the look, reads the head the ordinary way, and hands the rest over whole. And
"…, where X is the number of lands you control" is read here rather than by the sentence-level
wrapper that reads it everywhere else, because that wrapper anchors the clause to the end of a
sentence and here it ends the *first* of the two sentences that must be read together.

**Result: +83 cards, 0 lost**, diffed as a set rather than a count. One regression was caught that
way and by nothing else: widening the look's opening to admit "that many cards from the top of
your library" dropped the word "of" from the other branch, which un-read 108 cards that had worked
for months — Impulse, Anticipate, Sleight of Hand, every Commune. The card count still went *up*
on that build.

**Declined, with the numbers.** The single-card family (74) is a different mechanism — "look at
the top card of your library. If it's a land card, you may put it onto the battlefield" wants a
reveal that a condition can then read, and nothing about a chooser helps it. Reveal-until (27) has
no fixed N and needs a loop with a stopping rule. Within the take family, four things are still
unread on purpose: **"with power N or less"** (6 cards) and **"total mana value"** (2) would each
need another bound beside the filter; **"of the chosen type"** (5) needs the type chosen as the
card entered; and **"put the rest on top of your library in any order"** (7) is the one
destination the resolution cannot honour, because the rest go to the *bottom* whenever they go
back to a library. Reading that last one would quietly bury cards Diabolic Vision leaves on top —
a card that compiles, plays, and is wrong in a way nothing downstream can see.

### Round seventeen: "instead" is mostly not a replacement effect

`instead` ranked fifth in the compile dump — **621 sole blockers, 817 cards, 829 rows** — and the
mining note's own caveat was the thing to test first. Decomposed by *what is being replaced*:

| sub-shape | sole blockers |
|---|---|
| **no "would" clause at all** | **345** |
| damage would be dealt to something | 44 |
| a card would be put into a graveyard | 43 |
| a source would deal damage | 38 |
| a permanent would die | 33 |
| a player would draw or mill | 32 |
| a permanent would leave the battlefield | 15 |
| a permanent would enter the battlefield | 14 |
| an effect would create tokens | 11 |
| counters, life, the game, turn structure, mana, and one-offs | 46 |

**Fifty-six per cent of the family is not a CR 614 replacement effect at all.** Nothing outside the
card is being replaced, so there is no shield to hang on the battlefield and nothing to order
against anybody else's (CR 616.1). It is CR 614.15 self-replacement written the way cards print
it — `[A]. If [condition], [B] instead.` — one spell choosing between two of its own instructions
as it resolves. The whole standing replacement mechanism, which is what this round was pointed at,
is the wrong tool for the largest part of what the word does.

The right tool was already here. `TryConditionalPair` reads "If [condition], [A]. Otherwise, [B]"
through `BoardConditions` and two `OnlyIf`s; what was missing is the else branch taken from
*behind*, which is where every printed "instead" keeps it. Inside the 345:

| | sole blockers |
|---|---|
| `If ~ was kicked, [B] instead` | 51 |
| `[B] instead if [condition]` — the trailing order | 46 |
| `If [condition], [B] instead` — every other condition | 234 |
| remainder | 14 |

The 234 are not one shape and are one *mechanism*: the commonest single condition is worth 15 and
the tail is a hundred distinct ones, all of them clauses the condition vocabulary already reads.
One reader takes the lot, which is the difference between a family and a list.

**17,181 → 17,242 complete cards, +61, none lost, measured by set difference.**

#### The reader is five guards and one wrapper

`EffectPhrase.TryInsteadRider` compiles the pair to `IfKicked(B, Else: A)` or to
`TryConditionalPair`'s own `OnlyIf(⊤, [OnlyIf(cond, B), OnlyIf(¬cond, A)])`, for its CR 608.2c
reason: two sibling guards ask their question at two different moments, and a then branch that
falsifies its own condition would let the other one fire as well. `IfKicked` gained an `Else` to
match `IfBargained`'s.

Everything interesting is in what it refuses.

- **Only the previous *sentence* is replaced.** Gift of Growth is "Untap target creature. It gets
  +2/+2 until end of turn. If this spell was kicked, that creature gets +4/+4 instead" — the untap
  happens either way. The sentence loop already tracked that boundary for the Curse family's
  repeat, but at the ", then" clause rather than at the full stop, and Primal Growth is one printed
  instruction cut into three clauses: taking the last of them left the search outside the swap, so
  a kicked spell searched twice and fetched three lands.
- **An "instead" with nothing in front of it is refused.** Read alone it is a card that does its
  bigger half *as well as* its smaller — the worst available misreading, on the gentlest available
  wording.
- **The replacement may choose no target of its own** (CR 601.2c), the same refusal the
  "Otherwise" pair already makes.
- **The two arms have to be aimed at the same thing.** `AimedAt` compares where each branch's
  effects land, asked through `EffectTargets.ReadsATarget` — the one place that knows whether an
  effect's index is a target at all. It is what stops Blade of the Bloodchief putting its two
  counters on the creature that *died* rather than the one wearing the Equipment.
- **A condition whose subject is a pronoun is refused.** `BoardConditions` is handed a state and
  the source, and the source of a resolving instant is the spell — never a Mount, never an artifact
  creature. "Put a +1/+1 counter on it instead if it's a Mount" compiles clean, answers no every
  time, and prints a card whose better half can never happen. Five of them, before the refusal
  existed. It costs the cards where "it" is English's dummy subject ("if it's night"), and telling
  those apart means knowing which reader inside the condition grammar took the clause — a list one
  repository further out, which this file has been burned by four times.

#### A reader that ate its neighbour, and had for as long as both existed

`ThatCreaturePumps` read "that creature gets +N/+N until end of turn" as a pump on the **source**,
and sat in front of `ItPumps`, which spells both pronouns and answers in the order the rest of the
file does — the target the sentence before it chose, then the object the trigger was about, then
the source. Everything the first matched the second matches, so it is deleted rather than fixed.

It is the same bug this file records against the exalted keyword one section over, and the comment
above it said so: "exalted's tail". **Nothing about exalted moves** — its keyword form is built in
the compiler and never came through here, and the longhand trigger names no object, so the pronoun
still resolves to the source exactly as it did. What changes is the case that *has* a target:
"Target creature gets +3/+3 … that creature gets +5/+5 instead" pumped the **instant that cast
it**, so the kicked half of Might of Murasa, Explosive Growth, Vicious Offering, Final Flourish,
Stomped by the Foot and Vayne's Treachery did nothing whatsoever. Six cards, and the corpus diff
before the fix counted every one of them as a win.

#### The ability word breaks the line, and a line is the unit

Forty-six sole blockers print the trailing word order, and what keeps most of them from reading is
not the grammar. An ability word — "Metalcraft —", "Threshold —", "Morbid —" — is flavour
with no rules meaning (CR 207.2c) that nonetheless forces a line break, so the corpus prints one
instruction across two lines and the compiler reads lines. `CardCompiler.Lines` already folds one such half-sentence into
the line above it (CR 706.3b's dice results rows), and this is a second arm on the same fold, with
the join itself as the guard: the two lines are joined only when they read as one phrase together.
**Of 278 candidates the parser accepts 18**, and the other 260 stay exactly as they were —
Galvanic Blast among them, because "~ deals 4 damage" names nothing to aim at.

#### Declined, with the measurement behind each

- **The 276 genuine CR 614 sub-shapes.** Each is its own mechanism — a damage modifier, a
  destination swap, a draw replacement, a token doubler — and the largest is 44 sole blockers
  against the 345 taken. Sized, not started.
- **The elliptical damage replacement** — "~ deals 4 damage instead", with the recipient left to
  the sentence before it. **29 sole blockers**, and much the largest thing still inside this
  family. It wants a rewrite that borrows the previous sentence's recipient phrase, which is a
  different kind of change from a reader: the two sentences have to be joined *before* either is
  parsed, the way the results row is.
- **A replacement that names a fresh target** — "If ~ was kicked, instead destroy target creature
  or planeswalker". **8 of the 54 kicker cards**: Bloodchief's Thirst, Tear Asunder, Blood
  Beckoning, Divine Resilience, Expel the Unworthy, The Eagles Are Coming!, Waste Management,
  Galadriel's Dismissal. Each would make the spell ask at cast time for a target it will not use —
  CR 702.33g says that target is chosen only if the spell was kicked, and the engine has one
  target list per spell. The refusal is CR 601.2c's, and it is the same one the "Otherwise" pair
  makes for the same reason.
- **"[B] instead as long as [condition]"** — 4 cards (So Tiny, Precipitous Drop and its Alchemy
  twin, Mind Carver). A continuous effect whose size changes while the game does, not one
  instruction choosing between two at resolution; reading it as this would fix the answer at the
  moment the spell resolved.
- **`TriggerConditions.NamesAnObject("a creature you control attacks alone")`.** Making it true
  would give the longhand exalted the answer the keyword form has, and it is an allow-list whose
  entries have to be matched by `Game.SubjectObjectOf` really answering with that creature. Out of
  this round’s scope and worth naming: the pronoun reader is now ready for it.

### Round seventeen: 35 cards that aimed a pronoun at the source

The set diff is **byte-identical** - 17,181 complete cards before and after, none gained, none
lost - and 35 of them stopped doing the wrong thing. This class cannot move the coverage number
in either direction, because every card in it already compiled.

Six readers resolved a pronoun to the permanent with the ability without ever asking whether the
sentence had named something else. All six predate `EffectSubject` or sit in front of the reader
that knows about it, and each was found the same way: by playing the card, since a pump landing
on the wrong one of two creatures looks exactly like a game working.

| reader | verdict | what it was doing |
|---|---|---|
| `ThatCreaturePumps` | aimed at the source wrongly | deleted; `ItPumps` already read the same sentence with the three answers |
| `ItPumpsPerEach` | aimed at the source wrongly | Asari Captain grew at home while the Samurai it sent out stayed small |
| `ExploreLine` | aimed at the source wrongly | Path of Discovery put the +1/+1 counter on the enchantment |
| `ConniveLine` | aimed at the source wrongly | Doctor Doom connived himself instead of the Villain he named |
| `RegenerateLine` | aimed at the source wrongly | four instants put the regeneration shield on themselves |
| `BiteLine` | aimed at the source wrongly | fourteen cards had the *spell* deal damage equal to its power, and a spell is not on the battlefield, so nothing happened at all |

**The condition allow-list gained the attacks-alone family, and that is what exalted's own
sentence needed.** CR 702.90a is "whenever a creature you control attacks alone, that creature
gets +1/+1 until end of turn", and the creature attacking alone is very often not the one with
exalted on it. `TryExalted` had been given the right subject by hand; the cards that print the
sentence out rather than saying the keyword reached `NamesAnObject` and were told there was no
subject. Admitting it is safe on the same terms as every other entry: all four spellings of
"attacks alone" fire only on a declaration holding exactly one attacker, which is exactly when
`Game.SubjectObjectOf` answers.

The 35, by what changed:

| what landed | cards |
|---|---|
| the pump moved to the creature the sentence named | Primal Forcemage, Ambuscade Shaman, Ardoz, Flailing Drake, Agents of S.H.I.E.L.D., Eiganjo Exemplar (+A-), Asari Captain (+A-), Strategic Intervention, Derelict Attic // Widow's Walk, Candy Grapple |
| half a fight found its dealer | Ambuscade, Clear Shot, Rabid Gnaw, Nature's Way, Hunter's Mark, Hunter's Edge, Bite Down on Crime, Colossal Collision, Diplomatic Relations, Domri's Ambush, Felling Blow, Huatli's Final Strike, Knockout Maneuver, Halana Kessig Ranger |
| the regeneration shield found its creature | Boon of Erebos, Butcher's Glee, Necrobite, Unnatural Endurance |
| explore and connive found theirs | Path of Discovery, Doctor Doom |
| unchanged in play, and asserted as controls | Reckless Ogre, Rogue Kavu, Lunk Errant - their lone attacker *is* the source |

**`Fight` and the bite family are the worst of the six, and were the least visible.** "Target
creature you control gets +1/+0 until end of turn. It deals damage equal to its power to target
creature you don't control" put the *instant* in the dealer's seat; `Fight.Resolve` checks that
the dealer is on the battlefield, a spell is not, and the whole effect returned no events. The
pump landed, the damage never happened, and the card was complete, castable and silent. Fourteen
cards, all in a family the corpus prints forty times.

**A round-sixteen claim in this file was wrong and is corrected above.** It recorded Flailing
Drake as fixed by the block-pair work "because the block condition is now in the allow-list and
the earlier reader no longer wins". The subject arrived; the earlier reader still won. The Drake
was still pumping itself, and a played game is what showed it - the compile dump could not,
because the card compiles either way.

#### Declined here, with the measurement behind each

- **`TransformSelfLine` aims "transform it" at the source: 2 cards.** Vildin-Pack Alpha
  ("whenever a Werewolf you control enters, you may transform it") and Vincent Valentine are the
  whole family, and `TransformSource` carries no subject at all - so this is an effect change and
  a state question (which face, whose permanent) for two cards, not a reader gate.
- **`ItDealsDamage` aims its dealer at the source: 1 card.** Chainer's Torment's "create an X/X
  Nightmare Horror token. It deals X damage to you" means the token, and `ObjectOf` refuses a
  token referent by design - that refusal is the thing standing between this parser and a
  pronoun aimed at whatever happened to be last.
- **`GoadLine`, `SuspectLine`, `ItLine`, `SkipUntapLine` refuse a pronoun they cannot resolve.**
  They leave the line unread rather than aim at the source, which is the fail-closed side of this
  class and costs cards rather than correctness. Worth taking, and it is a different pass.
- **`EndureLine`, `SacrificeSelfLine`, `DelayedSelfCountersLine`, `BecomesPreparedSentence`
  aim at the source correctly.** Checked against every corpus printing: 7, 36, 13 and 5 cards
  respectively, and on all of them the word means the card.

### Round sixteen: 424 cards were already complete and already wrong

The round's largest result moved coverage by **zero**. CR 605.3a lets a player activate mana
abilities whenever a rule asks them for a payment; this engine required the mana to be floating
already, so **424 complete cards auto-declined every payment they print** - ward, echo,
cumulative upkeep, extort, and every "counter target spell unless its controller pays {3}".
They compiled, they counted as read, and they answered no on the player's behalf.

The fix was one gate, because **the other two halves already existed and had never met**:
`ActivateAbility` already skips `RequirePriority` for a mana ability and returns without
settling, so a pending question survives the taps, and `ResolveOptionalPayment` already
re-checked the real pool before charging. Only `AskOwedPayment`'s CR 118.3 test was wrong -
"has the mana" rather than "has, or could produce, the mana". Set diff: byte-identical.

### A wall three rounds hit, and two diagnosed wrongly

Ashmouth Hound and eight siblings were recorded twice as blocked by `SubjectObjectOf` refusing
`BlockersDeclared`. Flipping that arm completes five cards and **none of the nine**. The nine
were behind `DealDamage` having no `EffectSubject` at all - and both halves were required, since
building only the effect half makes all nine damage *themselves*. Now one trigger per blocking
pair (CR 509.3c/d), with the batch wording still firing once. It also found **Flailing Drake
pumping itself**, **Quagmire Lamprey countering itself**, and bushido firing for an attacker
nobody blocked.

### Four walkers that were walking nothing

`FiltersIn` had no `Flatten`; `EveryEffect` had no modes; **none** of the four `CompiledCard`
walkers reached `Adventure`, `PreparedSpell`, `CleaveSpell`, `GiftSpell` or `Halves`. Every
adventure, prepared half, cleave text, gift and split-card face was checked by nothing while the
invariant suite reported clean over 32,765 cards. `AssertEveryFieldCounts` now guards all 35
`GameState` properties, verified by a negative control that drops six fields and names all six.

### The queue is flat at the line level, and the leverage is elsewhere

A full-corpus compile dump grouped by shape shows the top rows are **substrings, not buildable
units**: `for each` looks like 831 sole blockers and is one word across a thousand unrelated
templates. Clustering by whole line gives **11,897 distinct templates, the largest completing
20 cards**. What pays is shared vocabulary, which is why this round's convergence work was worth
more than its readers.

The best-shaped lead found and not taken: of 655 cards one line short whose unread line contains
a quoted ability, **314 have an inner quotation that already compiles**. The compiler understands
the ability and cannot read the frame granting it - a composition gap, not a vocabulary one.

### A stolen neighbour no instrument could see

Widening the "its controller" rewrite broke `Destroy target creature. Its controller loses 1
life for each creature you control`, because the reader taking that clause tests for the phrase
by hand and did not know the new spelling. **The corpus diff showed nothing** - every card
printing that shape is short something else too - and coverage, the ratchet and
`MechanicCoverageTests` were all green. Only a played game saw it.

### Round sixteen: one trigger per blocking pair

+23 cards, none lost, and the whole round is one commit because it changes `Game.Consider` —
the hottest path in the engine. The wall was recorded wrongly twice and correctly once; this
built what the corrected diagnosis named, and the corrected diagnosis was right.

**A block declaration is a batch of pairs, and now the engine can say which pair.**
`SubjectObjectOf` still answers nothing for `BlockersDeclared`, because the *event* is about no
one creature. But a card of this family is not asking about the declaration - it asks about its
own pair, and there the other creature is unambiguous. So `Game.Consider` cuts the declaration
into single pairs, asks the ability's own predicate about each one alone, and records one
`AbilityTriggered` per pair with the *other* creature as its subject. That is the same
singleton-probe technique `AmountFor` already uses on an attack batch, and it is what the
`Func<…, bool>` predicate shape can express after all: a predicate cannot be asked how many, and
it can be asked once per candidate.

**Both halves were required and neither was worth anything alone**, exactly as recorded:

- `TriggerConditions.BlockPairSubject` is the subject half - a static query on the condition text,
  a sibling of `NamesAnObject` on the same channel, so no signature changed. It sets both
  `namesAnObject` (so the pronoun compiles) and `TriggeredAbilityDefinition.PerBlockPair` (so the
  subject exists), from one query, so the two can never disagree.
- `DealDamage` gained an `EffectSubject`. It was the last of the four pronoun verbs without one -
  destroy, exile and tap all had it - and it read `context.TargetAt(index)` and nothing else.

**The object in the sentence decides how often it fires, and that is the whole rule.** CR 509.3c:
"whenever this creature becomes blocked" triggers *once* each combat however many creatures block
it. CR 509.3d: "becomes blocked **by a creature**" triggers once for *each* of them. CR 603.2b's
own example is those two sentences side by side. A condition naming no creature is refused, both
because it fires once and because a sentence that names nothing has no pronoun to resolve - and
that refusal is asserted, because getting it generous prints a strictly better card than the one
on the table.

The set, not the number. Nine cards were named in the diagnosis and all nine landed; the other
fourteen came from the same two changes reaching further than the nine:

| what landed | cards |
|---|---|
| the nine, whose whole text is "~ deals N damage to that creature" | Ashmouth Hound, Inferno Elemental, Ornery Goblin, Acolyte of the Inferno, Kolaghan Aspirant, Flame-Kin War Scout, Kessig Forgemaster, Skewer Slinger, Somberwald Vigilante |
| the same pair subject with a different verb | Engulfing Slagwurm, Infernal Medusa, Sylvan Basilisk, Tangle Asp, Venomous Dragonfly, Ogre Leadfoot, Phyrexian Reaper, Phyrexian Slayer, Nessian Boar, Gloom Sower, Vicious Battlerager |
| the damage subject in the families that already had a subject | Aether Flash, Caltrops, Raking Canopy |

**Three cards were already compiling and playing wrongly, and the count could not show it.**
Flailing Drake ("that creature gets +1/+1") pumped *itself*; Quagmire Lamprey put its -1/-1 counter
on *itself*. The Lamprey was fixed here. The Drake was not - the claim that it was is corrected in
round seventeen below - and neither appears in the set diff, because the diff only sees cards
crossing from unread to read. The count is blind to this class in both directions.

**And one card was better than printed.** The reader for "~ blocks or becomes blocked" asked only
whether the source's id was *in* the declaration, so bushido fired for an attacker nobody blocked -
CR 509.1h says an attacker with an empty blocker list did not become blocked, and the sibling
reader for "~ becomes blocked" had had that check from the start. Two spellings of one rule, one of
them right, which is this file's most-repeated shape of defect.

**Cost on the hot path: none that a run can see, and the reason is where the branch sits.** It is
after the `continue` that a non-firing predicate takes, so the millions of false answers the corpus
checks produce reach nothing new at all; a trigger that actually fires then pays one field read and
one type test. Measured on the rules suite, which is the thing that runs `Consider` in anger:
interleaved with the baseline on a machine five branches were sharing, 36/42/43s before against
38/40/40/42s after, with seven more tests in the second figure. An earlier non-interleaved pair read
28-32 against 31-41 and looked like a 30% regression; re-measuring the baseline in the same window
put it at 36-43, which is the whole of the difference. **Interleave the measurement or do not report
it** - this box drifts by more than the effect being looked for.

#### Declined here, with the measurement behind each

- **The same decomposition for `AttackersDeclared`: 139 distinct corpus lines.** "Whenever a
  creature attacks, ~ deals 1 damage to it" is the identical shape one batch along, and
  `SubjectObjectOf` answers it only when the declaration holds exactly one attacker (CR 508.1) -
  so Caltrops and Raking Canopy read now and do nothing when two creatures attack. That is the
  pre-existing policy for the attack arm, written down in `NamesAnObject` and deliberate; extending
  the pair machinery to it would change every "whenever a creature attacks" card in the corpus and
  wants its own commit and its own soak, exactly as this one did. **Built in round seventeen,
  below, and the measurement was right about the shape and low about the reach: it changes 42
  complete cards and no card's coverage at all.**
- **`ThatCreaturePumps` aims at the source unconditionally: 7 cards.** Primal Forcemage, Gahiji,
  Wild Defiance, Ambuscade Shaman, Ardoz, Werewolf Lightning Mage and Flailing Drake all print
  "that creature gets +N/+N until end of turn" on a trigger that names an object, and the reader
  that takes the sentence is the one *before* the reader that knows about the trigger's subject.
  Flailing Drake is fixed here only because the block condition is now in the allow-list and the
  earlier reader no longer wins; the other six still pump the wrong creature. It is a one-line gate
  on a reader shared with exalted and 73 corpus lines, which is a measured pass rather than a
  ride-along. **Built in round seventeen, below, and two halves of this were wrong: the fix was a
  deletion rather than a gate, and Flailing Drake was not fixed here at all - the unconditional
  reader still won, and a probe on either side of the change is what showed it.**

  Flailing Drake was recorded as fixed here by the block condition reaching the allow-list; that was
  wrong, and round seventeen measured it - the subject arrived and the earlier reader still won.
  All seven were still pumping the wrong creature. It is a one-line gate on a reader shared with
  exalted and 73 corpus lines, which is a measured pass rather than a ride-along. **Taken in round
  seventeen**, along with five more readers of the same shape.
- **The coverage ratchet was left at 0.517** against a measured 52.0%. Raising it is a one-character
  change to a constant five branches are editing this round, and the slack it currently carries is
  the same slack it was set with.

### Round seventeen: one trigger per attacking creature

+0 cards, none lost, and the set diff is byte-identical — which is the whole of the result. This is
the attack arm of the decomposition round sixteen built for blocks and measured for attacks, and it
changes how **42 complete cards play** without changing whether a single card compiles. Caltrops,
Raking Canopy, Hissing Miasma, Marchesa's Decree, Righteous Cause, Utvara Hellkite and thirty-six
others read, counted as complete, fired once for a declaration of any size, and aimed their
pronoun at nothing whenever two or more creatures attacked. The count is blind
to this class in both directions, exactly as the block round recorded, and this round is that
sentence's second proof.

**One mechanism, not a sibling.** The dangerous way to build this was a `PerAttacker` beside
`PerBlockPair`, and the two would have been two spellings of one rule — this file's most-repeated
shape of defect. All three pieces were generalised in place instead:

| round sixteen | round seventeen |
|---|---|
| `TriggerConditions.BlockPairSubject` | `TriggerConditions.DeclarationSubject`, with `AttacksPerCreature` as its attack half |
| `TriggeredAbilityDefinition.PerBlockPair` | `TriggeredAbilityDefinition.PerDeclaredCreature` |
| `Game.RecordOnePerBlockPair` | `Game.RecordOnePerDeclaredCreature` over `OccurrencesIn` |

`OccurrencesIn` is the only place the two declarations differ, and they differ in what the sentence's
subject is rather than in how the batch comes apart: a block yields the *other* creature in the pair,
an attack yields the attacker. Everything downstream — the singleton probe of the ability's own
predicate, the once-each-turn budget, the strict subject that resolves to a creature or to nobody —
is one copy serving both.

**The object in the sentence decides how often it fires, and the generous direction is the dangerous
one.** CR 508.3a: "whenever *a creature* attacks" triggers if that creature is declared as an
attacker, which with CR 603.2c is one occurrence for each of them. CR 508.3b and CR 508.3d are the
batch wordings beside it — "whenever a player is attacked", "whenever you attack" — and they trigger
once however many were declared. So the word this turns on is the scope: "a", "an", "another" and
"~ or another" are one creature said once; "one or more creatures" is the declaration said as a
batch, and it is why the six corpus lines that go on to say "that many" mean the batch. Firing those
per attacker would print a strictly better card than the one on the table and the coverage number
would score it as a win, so the refusal is checked rather than left to the pattern — and it is
asserted, beside the test that proves the other half.

The verb is checked as narrowly. Only the bare `attacks` is decomposed: "~ attacks" is its own
reader and is about one creature already, and "attacks alone", "attacks and isn't blocked", "you
attack with one or more creatures" and "enchanted player is attacked" are each a sentence about the
whole declaration with no one creature to name.

**Asking the predicate again is what keeps the card's own qualifiers.** The engine cuts the
declaration up and knows nothing at all about what the cards say: the tribe, the colour, the side,
the power and the defending player are applied per attacker because the ability's own predicate is
asked about each attacker alone. The `AttackTarget` rides along on the occurrence for the same
reason — without it both halves of a split declaration would look alike, and Hissing Miasma would
charge for a creature that attacked a planeswalker (CR 508.1b).

**Cost on the hot path: none a run can see, and the reason is where the branch sits** — after the
`continue` a non-firing predicate takes, so the millions of false answers the corpus checks produce
reach nothing new. Measured on the rules suite, which is the thing that runs `Consider` in
anger: interleaved with the baseline on a machine several branches were sharing, **33/30/32/38s
after against 42/42/36s before**, with six more tests in the first figure. The after side is
nominally the *faster* of the two, and the spread inside each side is as wide as the gap
between them - which is what no effect looks like on this box.
**Interleave the measurement or do not report it**: the block round's first non-interleaved pair
looked like a 30% regression that re-measurement put inside the noise, and nothing about this box
has got quieter.

#### The pump that had two readers, and only one of them right

`ThatCreaturePumps` is gone. It matched "that creature gets +N/+N until end of turn" and aimed the
pump at the permanent with the ability **unconditionally**, sitting in front of `ItPumps`, which is a
strict superset of it and carries the pronoun ladder every other reader uses: the target, then the
object the trigger was about, then the permanent with the ability. Written for exalted — where the
sentence is about a lone attacker and the source usually *is* that attacker — every game where it
mattered looked like a game where it did not.

Four cards were compiling, counting as complete, and pumping the wrong creature: **Primal Forcemage,
Ambuscade Shaman, Ardoz and Flailing Drake**. Flailing Drake is the one worth naming twice: round
sixteen recorded it as fixed on the grounds that the block condition had entered the allow-list and
"the earlier reader no longer wins". The earlier reader was unconditional and did still win, and a
probe comparing the compiled effect on either side of this change is what showed it. A fix recorded
and not landed is worse than one not attempted, because the next round reads the record.

The fix was to delete the second reader rather than gate it. A gate would have left two copies of one
list, and a second copy of a list is how they come to disagree — which is what this whole section is
about.

Three of the seven cards the round-sixteen measurement named are untouched, and for a reason that is
not this reader: **Gahiji, Wild Defiance and Werewolf Lightning Mage** print conditions the
vocabulary cannot read at all ("attacks one of your opponents or a planeswalker an opponent
controls", "becomes the target of an instant or sorcery spell", "a creature blocks this creature"),
so no trigger of theirs compiles and there is no pump to aim. The seven were counted by their
sentence and not by whether the sentence was reachable.

#### Declined here, with the measurement behind each

- **The "attacks alone" family's pronoun: 44 corpus lines.** "Whenever a creature you control attacks
  alone, it gains double strike until end of turn" means the lone attacker, and the text path still
  reads "it" as the permanent with the ability — Rafiq of the Many, Battlegrace Angel, Black Panther,
  Peggy Carter, Agents of S.H.I.E.L.D. and the rest. The *keyword* path already has it right:
  `TryExalted` builds `EffectSubject.TriggeringObject` by hand, so the two spellings of exalted
  disagree today. Admitting `AnyCreatureAttacksAlone` and its typed siblings to `NamesAnObject` would
  close it and is safe on both ends — the predicate refuses any declaration that does not hold
  exactly one attacker, so `SubjectObjectOf` always answers — but it changes what a pronoun means on
  44 lines and wants its own commit and its own soak, exactly as this one did.
- **The batch attack wording keeps the old subject policy.** "One or more creatures attack" still
  takes its subject from `SubjectObjectOf`, which answers only when the declaration held exactly one
  attacker. It is the pre-existing policy and it is not made worse here. Checked rather than
  assumed: five corpus lines of that shape carry a pronoun and not one of them means the
  attacking creature - Winota's "it" is the Human she put onto the battlefield, Glass-Cast
  Heart's is a Blood token's reminder text, and Raging River's "that creature" is bound by the
  loop in its own sentence.
- **The coverage ratchet was left where round sixteen ratcheted it.** This round moved coverage by
  zero in both directions, so there is no new slack to take up.

### Two cards that compiled perfectly and could not be played

The round-end soak found both, and neither was reachable by any unit test, because each needs a
real card played in real company:

- **A spell whose own text moves it off the stack faulted.** CR 608.2m's last step - put the
  spell into its owner's graveyard - asked the game for the object unconditionally. "Exile Blood
  for the Blood God!" exiles the card during its own resolution, so the step then looked up
  something that had stopped existing. It took **five spells cast in front of it** to reproduce:
  the card costs {1} less for each creature that died this turn and is uncastable on an empty
  board. Nothing to move is the rule having nothing to do, not an error.
- **A division was checked against targets it was not among.** CR 601.2d says each target *the
  division is among* gets at least one; the validator asked it of every chosen target. Rhino,
  Terrible Trampler destroys a target artifact or land and then distributes three counters among
  up to three **other** target creatures - the artifact's slot has to be zero, so every possible
  announcement was illegal and a fully read permanent could not be cast at all.

Both are one rule applied one scope too wide, and both were invisible to 1,969 unit tests. The
soak's own driver had to learn the same distinction the second fix encodes: one entry per chosen
target, zero outside the divided slice. Where the harness was wrong it was taught the answer a
player gives, never by weakening the rule - the engine was right to refuse both times it threw.

**The round is not done when the branches merge; it is done when the soak agrees.** That has now
been true in four of the last five rounds, and twice this round the unit suites were green while
a card in the corpus was unplayable.

### Round sixteen: the player a sentence already named

+19 cards, none lost, and it is round fourteen's finding once more — except that this time there
was **no shared vocabulary to have drifted from.** Five readers each carried a private list of
the possessive player words and no two agreed:

| the reader | the possessives it knew | what it could not say |
|---|---|---|
| `CardsInZoneLine` (counts a pile) | your, all, each player's | that player's, each opponent's, its controller's |
| `ZoneCountLine` (the static pump beside it) | your, each | everything else, including "your opponents'" |
| `HandSizeChangeLine` | your, each opponent's | anything — and it answered *each opponent* to every other word |
| `BeginningOfStep` | seven | each other player's |
| `BeginningOfCombatOn` (beside it) | three | the other six |

They now all read `EffectPhrase.WHOSE`, and the word list maps to a `PlayerScope` through
`PossessiveScopeOf`, which recovers the bare subject and hands it to the *subject* vocabulary's
`ScopeOf`. One list of words, one mapping, and a word added to either spelling is understood in
both. Three more copies of "its controller / that creature's controller" collapsed into
`ItsController` for the same reason, and that one had already cost something (below).

**The structural half is that a count had no parameter for a player.** `CountFn` is handed the
state, the abilities, "you" and the source, and "that player's graveyard" is none of those four.
It now takes a `PlayerLookup`, and the caller declares how far its answer reaches with
`CountSeats`:

- **`Board`** — what the battlefield settles on its own: you, each player, each opponent, each
  other player. Every phrase this vocabulary could count before.
- **`Host`** — the board plus "its controller", which is what an *attached* continuous effect
  knows: it only ever computes the permanent it is attached to, so the pronoun's seat is the one
  in front of it. No resolution needed, and it is what Righteous Authority, Death's Approach and
  Disturbing Conversion were behind.
- **`Resolution`** — everything, because a resolution has the targets and the trigger's subject.

One statement of reach rather than a bool per relation, because three callers genuinely reach
three distances. A phrase whose seat the caller cannot find is **refused at compile time**: a
count of nobody's pile is nought, and nought compiles as a complete card that does nothing.

What the shared list reached beyond the seven cards the audit had priced:

| what fell out | cards |
|---|---|
| "that player's hand/graveyard" meaning the player the spell targeted | 8 |
| "its controller" as a subject *after* a target — Swords to Plowshares' shape | 4 |
| "its controller's hand/graveyard" inside a count on an Aura | 3 |
| "your opponents' graveyards/hands" in a static pump — a reader nobody was measuring | 2 |
| "each other player's draw step" | 1 |
| "your opponents'", "each other player's", "all" and "the end step of *X*" in the step readers | 0 today |

#### The refusals are the load-bearing half

The prefix tests the step reader used — `owner.StartsWith("your")`, `StartsWith("each opponent")`
— answered **"everybody"** to every word not in their four. Handing those a list of nine would
have turned "at the beginning of that player's upkeep" into a trigger firing on all four turns:
a strictly better card than the one printed, and a change coverage counts as a win. So the reader
was rewritten to resolve a `PlayerScope` and refuse the ones a *turn* cannot belong to — a
target, a trigger's subject, the defending player. `HandSizeChangeLine` had the identical
fall-through and lost it the same way.

#### A widened rewrite stole a neighbour's clause, and only a behaviour test saw it

Lifting the refusal on "its controller" — it used to be left alone whenever anything had been
targeted — broke "Destroy target creature. Its controller loses 1 life for each creature you
control", because the reader that had been taking that clause tests for the phrase *by hand* and
did not know the rewritten spelling. **The corpus diff showed nothing**: every card printing that
shape is short something else as well, so the complete count moved neither way. `MechanicCoverage`
and the ratchet were both green. The one thing that caught it was a behaviour test written when
the clause was built, which is the argument for writing them.

The fix is the reason `ItsController` is one fragment: the rewrite produces one of its own
alternatives, so it is idempotent and every reader downstream recognises the phrase whether or not
the rewrite has already been over the sentence.

#### What is still out, and why

Everything below was measured with a substitution probe on the compile dump, so these are prices
rather than guesses.

- **A count phrase that has to *add a target*** — "the number of cards in target player's hand",
  Corpse Augur, Gerrard Capashen, Recurring Insight. **3 cards.** `Counting` is handed a phrase
  and no target builder, so a possessive that targets cannot announce one. Structural, and a
  bigger change than the possessives were.
- **A target spec scoped to a named player's graveyard** — "exile target card from that player's
  graveyard", Skullsnatcher, Ink-Eyes, Scion of Darkness, Zombie Cannibal, Graven Abomination,
  Rakshasa Debaser. **6 cards.** A `TargetSpec`'s filter is handed the controller, not the
  trigger's subject, so the pile cannot be narrowed to a seat.
- **The top *N* of a named player's library** — Elemental Augury, Architects of Will, Korvold and
  the Noble Thief, Orochi Soul-Reaver. **4 cards**, and mostly the targeted possessive again.
- **"The colour of its controller's choice"** — Pale Wayfarer, Wishmonger. **2 cards**, and not a
  vocabulary gap: it is a choice made by a player who is not the controller.
- **The subject-position "that player"** was deliberately left where it is. `ScopeOf` still
  answers `TriggerSubject`, and moving it to `NamedPlayer` would change what already-complete
  cards *do* — which no set diff can see. It is the same measurement this file records against
  reading all 1,588 of those lines one way, and it wants its own round and its own soak.

### Round fifteen: chosen by grepping for a shape, not by reading down the queue

Rank had stopped predicting value, so every target this round was found by grepping the
**compile dump** — a probe emitting `(oracleId, name, isComplete, unhandled)` for all 32,717
cards, grouped by the shape of the unread line. That is a better instrument than grepping the
corpus, because it asks what the compiler *fails* on rather than what cards say. One query -
`^\w+-(?!\{)`, a keyword head and an em dash followed by something that is not a mana symbol -
returned the whole non-mana-keyword-cost family at **91 rows / 104 cards, five times what the
line ranking showed**. It also splits families the ranking conflates: `devotion` returned 51
unread rows that are two unrelated mechanics, 34 counts and 10 type-removals.

### The founding invariant was quietly broken, and by a gap in a helper

`GameState.Equals` omitted `ArmedStateTriggers`, so two states differing only in which state
triggers had fired compared **equal** - and a replay would fire each of them a second time
(CR 603.8). The cause is worth more than the fix: `Structural` had equality overloads for a
list and for a dictionary and **none for a set**, so the one set-valued field on the state had
nowhere to go. Nobody decided to leave it out.

### Live defects, each a card better than printed

- **A spell target's qualifier was parsed and discarded.** `Counter target spell with mana value
  4 or greater` compiled *complete* and countered anything - Disdainful Stroke, Spell Queller,
  Hypnotic Sprite and five others shipped that way.
- **Nothing enforced CR 115.3.** Every "up to two target creatures" was castable twice at one
  creature.
- **`RequireDamageDivision` ran at the end of `CastSpell`**, so an illegal announcement got as
  far as paying for the spell. Moved beside `RequireLegalTargets`, where 601.2d belongs.
- **A trigger with a trailing optional target and nothing legal left was removed from the stack**
  rather than resolving with what it had.

### Two instruments had drifted from what they measure

The coverage harness's Scryfall keyword table held **33 of production's 34** (no `Banding`), so
every banding card reached the compiler as a different card from the one the app builds. And
`WitnessBoard.Satisfies` asked three of `TargetSpec`'s four filters - the same two-of-three bug
as before, recurring the moment `PeerFilter` was added, which made every radiance phrase
satisfiable for free. It now calls `spec.Accepts`, the one place that asks them all.

### A wall recorded twice, and recorded wrong

Two rounds had concluded that Ashmouth Hound and its eight siblings were blocked by
`SubjectObjectOf` refusing `BlockersDeclared`. Flipping that arm and recompiling the corpus
completes **five** cards and **not one of the nine**. The nine are behind `DealDamage` having no
`EffectSubject` at all - unlike destroy, exile and tap, it cannot take a pronoun. Both halves
are required and neither is worth anything alone: building only the effect half would make all
nine damage *themselves*, which is the failure the corpus gate caught and reverted two rounds
before. The corrected diagnosis is here so the next attempt starts from the right wall.

### Round fifteen: a trigger that watches a batch, and a wall misattributed for two rounds

+35 cards, none lost. Two families whose *effects* already read and whose trigger was the whole
blocker, both of them the plural of a sentence about one thing:

| wording | cards | what was missing |
|---|---|---|
| `Whenever one or more cards leave your graveyard` | 22 | no event said which moves were simultaneous |
| `Whenever you discard one or more cards` | 13 | the same, one zone along, plus "that many" |

The engine already had one batched trigger — `CombatDamageDealt`, a damage step summarised as a
single fact — so this is that mechanism extended rather than a second one. What is new is
**where** the batch comes from. A damage step is produced in one place and can append its own
summary; a card leaves a graveyard from dozens of places, so the batch is defined by a scope
instead: `Game.AsOneBatch` marks a unit of work whose moves happen at once, `Emit` files
qualifying `ObjectMoved`s into it, and the summary is derived from that list in **one** place so
the summary and the moves it summarises cannot disagree.

Three details the design turns on:

- **A move outside any scope is a batch of one, summarised immediately.** The alternative — only
  emitting a summary inside an explicit scope — makes every route nobody remembered to wrap into
  a card that reads perfectly and does nothing, which is this file's most-repeated failure.
  Wrapping is therefore an optimisation for correctness in the *other* direction: it stops a loop
  of five `Move` calls firing the trigger five times.
- **Nested scopes join the outer one.** Two scopes over one simultaneous event would fire the
  trigger twice, and a trigger paid per card instead of per batch prints a strictly better card
  than the one on the table (CR 603.2c: an ability triggers once each time its event occurs, and
  a plural sentence makes the whole batch one occurrence).
- **The batch says how big it was, and how big it was *for this ability*.** `AmountFor` already
  narrowed an attack batch by asking the ability's own predicate about each attacker on its own;
  the two card batches are narrowed by exactly that method, because the description lives in the
  predicate and nowhere else. Without the count, "create that many tokens" fires and creates
  nothing — the defect this file records against `AttackersDeclared`, which is why that family
  was refused before it was built.

The scopes are `RunEffects` (one effect's events, CR 608.2c), the three discard-choice paths and
the chosen-cost payment. `and/or` was added to the shared search vocabulary rather than to the
one reader that met it, so "artifact and/or creature cards" reads everywhere at once.

**`deal combat damage to one or more players` is worth zero cards** and was left alone. It was
briefed as part of this family; a substitution probe says every card printing it is held up by
something else as well. Two other briefed numbers were high by the same measure — the two
families above were briefed at 27 and 17.

#### The block-declaration subject is not what those nine cards are behind

`SubjectObjectOf` deliberately answers nothing for `BlockersDeclared`, because a block
declaration is a batch of pairs. Two rounds have now recorded that as the wall in front of
Ashmouth Hound, Inferno Elemental, Ornery Goblin and six others. **It is not.** Admitting the
block pronoun — flipping `NamesAnObject`'s `blocks` and `becomes blocked` arms and recompiling
the corpus — completes **five** cards, and all five are the *attached* wording ("whenever
enchanted creature blocks or becomes blocked, its controller loses 2 life") where the pronoun
means the host. Not one of the nine is among them.

The nine are behind **`~ deals N damage to that creature`**: `DealDamage` reads
`context.TargetAt(index)` and has no `EffectSubject` at all, so unlike destroy, exile and tap it
cannot take a pronoun. Swapping that clause for one that reads completes exactly those nine and
nothing else — Ashmouth Hound, Inferno Elemental, Ornery Goblin, Acolyte of the Inferno, Kolaghan
Aspirant, Flame-Kin War Scout, Kessig Forgemaster, Skewer Slinger, Somberwald Vigilante.

So the two halves are **both** required and neither is worth anything alone:

- the subject alone completes none of the nine;
- the effect alone completes all nine **as cards that damage themselves**, because "that
  creature" would fall back to the source — which is the exact failure this file already records
  for "whenever ~ blocks a creature, destroy that creature", found by the corpus structural gate
  and reverted.

**Built in round sixteen, below, and the corrected diagnosis was right: all nine landed.** The
costing that follows is what it was costed at, kept because the estimate held.

Costed, having mapped it: `EffectPhrase.BlockPairSubject(condition)` as a sibling of
`NamesAnObject` (the channel already exists — `NamesAnObject` is a separate static query on the
same condition text, so no signature has to change); one `init` property on
`TriggeredAbilityDefinition` beside `Chapter` and `OpensDoor`; `Game.Consider` decomposing a
declaration into single pairs and recording one `AbilityTriggered` per matching pair, which is
the same singleton-probe technique `AmountFor` already uses and is what the `Func<…, bool>`
shape can express after all; an `EffectSubject` on `DealDamage`. An afternoon of work, and it
touches `Consider` — the hottest path in the engine, run millions of times by the corpus checks
— so it wants its own commit and its own soak rather than riding along with a batch summariser.

#### The tribal narrowing on the batched combat trigger is a tail, not a family

Briefed as a family; measured at **8 cards**, and they are four unrelated grammar features of
one to three cards each: `artifact creatures` (3, wants the plural head singularised on its last
word), `Ninja or Rogue creatures` (2, two subtypes qualifying one noun), `non-Human creatures`
(1), `creatures you control with +1/+1 counters on them` (1) and `creatures you control that
entered this turn` (1). `OneOrMoreLine`'s head is one word, and widening it safely is a bigger
change than any of those rows is worth.


### Round fourteen: the compiler was competing with itself

+294 cards, none lost, and the reason is one finding that three agents reached independently
from three different slices of the queue: **readers had each grown a private copy of a
vocabulary the compiler already had next door.**

| the shared thing | the reader that had its own smaller version | cards |
|---|---|---|
| `EffectPhrase.Counting` - domain, colours, party, every zone | `TryCountingStatic` knew a battlefield filter; `DefinedCount` knew two piles | **57** |
| the seven player subjects in `W` | `EdictLine` spelled two | 18 |
| "its controller" effects | existed for life, draws and mills - not tokens | 33 |
| group unions | read `A or B`, not `A, B, and C` | 17 |
| the same counting vocabulary again | the characteristic-defining reader | 21 |

Two of those rows are the same defect found twice in one round, twenty queue ranks apart, on
cards that look nothing alike: a static pump on a Kavu and a defining ability on a Lhurgoyf.
The floating pump had been counting domain correctly for months, because it reads the shared
vocabulary back off its own generated name; the two static readers never did. **The gap was
never the sentence - it was which of the compiler's several vocabularies the sentence reached.**

### Three silent no-ops, each counted as complete

- **A card's own `When you cast ~` had `FunctionsFrom = Battlefield`**, and a cast is announced
  with the card already on the stack. Roughly **50 corpus cards read perfectly and did nothing.**
- **`{X}` in an activation cost was never paid.** `ActivateAbility` took the announced number
  and never handed it to `PayMana`, so `{X}, {T}: search for a card with mana value X or less`
  was a free tutor for any number. Two siblings: an activated ability reaching the stack with
  `VariableValue` 0, and a triggered ability whose targets were re-checked against zero and
  fizzled every time.
- **`NumberWordOrDigits` answers 1 to any word it does not know**, for the second round running.
  Hundred-Handed One's "an additional ninety-nine creatures" compiled complete and granted one.

### The settle sweep, audited rather than repaired one bug at a time

Three bugs of one shape had been found in three separate rounds, each by accident and each after
shipping. All 44 arms of `SettleBeforePriority` were then walked deliberately, and **six more**
turned up: `SettleOwedFlip`, `SettleOwedDiscover`, `SettleOwedCascade`, `SettleOwedClash`,
`OfferOwedMiracles` and `SettleOwedLookAndTake` all returned from the sweep as though a question
were pending, and one stopped the game to pick from an empty list. **An existing test was
asserting the bug** - `A_filtered_look_may_be_declined` asserted `Assert.Empty(choice.Options)`,
which is how it survived.

The audit's replacement for vigilance is a structural test:
`Every_settling_step_of_the_sweep_goes_round_rather_than_returning` reads `Game.cs` and enforces
the convention the sweep runs on - an arm named `Ask...` may return, every other arm must
continue. It catches all six reverts at once, and it is the only thing covering cascade, which
always settles with its own spell still on the stack so no card can show the harm.

**The largest correctness gap it names has since been closed** - see "A mana payment asked while
an effect resolves" at the end of this file. What it named: CR 605.3a lets a player activate mana
abilities whenever a rule asks for a payment, mid-resolution; this engine could not, and
`AskOwedPayment` admitted as much. So every "counter target spell unless its controller pays {3}"
was auto-declined against anyone who did not pre-float the mana - CR 118.3 declining on their
behalf from a false premise. That is the Pact finding generalised, and it was the engine playing a
different game rather than a card going unread.

### What predicts value now, and it is no longer rank

The deepest sweep (ranks 321-380) reported its own floor: 70% of its rows are each their own
mechanic, and 10% are Alchemy rebalance pairs worth half what they claim. But its six wins were
worth 12 cards inside the slice and **62 across the corpus**, because every shape recurred at
ranks the slice never showed - semicolon keyword lists span ranks 143 to 12,418. **Rank stopped
predicting value; whether a row's shape recurs is what predicts it.** Grep by cause, not by rank.

### Round fourteen: leads that had been measured and left, and what they were worth

A round with no work queue in it at all: every item was something a previous agent found while
doing something else, measured, wrote down, and deliberately did not build. **+49 cards, none
lost** (16,505 -> 16,554 by set difference), plus two engine fixes and one silent no-op class.

**Five of the eleven briefed counts were wrong, and only one of them downwards.** Re-measuring each
lead before building it is what this round is an argument for:

- **`{M}: ~ phases out` was briefed at 3 cards. It is 13.** The brief had counted one wording of
  one template; the effect is a verb, and the verb reaches the targeted form (Reality Ripple,
  Vodalian Illusionist, Haystack, Slip Out the Back, a mode of Unite the Coalition), the attached
  form through the Aura and Equipment wordings (Vanishing, Robe of Stars), and the source form the
  brief named (Blink Dog, Rainbow Efreet, Teferi's Honor Guard, Crystal Golem) - plus two that
  needed nothing else at all once the verb existed (Frenetic Efreet, Renegade Silent).
- **`Whenever you expend N` was briefed at 8 sole blockers. It is 10** - Bakersbane Duo and
  Trailtracker Scout were missing from the list, and the second is on `expend 8`, which is the
  same reader with a different number.
- **The tribal narrowing on the batched combat triggers was briefed as one missing word reaching
  past its own row. Measured, it is worth one card**, because the narrowing already read for every
  real creature type - Rogues, Dragons, Pirates all compiled. What did not read was three quite
  different things wearing the same shape: `outlaws` (CR 700.12's five types, printed lowercase
  because it is not a type), the defender said out loud (`attack a player`), and the batched
  recipient (`deal combat damage to one or more players`). Together they are 5 cards on the row and
  6 in the corpus. **The substitution probe is what told them apart** - swapping only the tribe
  gained one card, and swapping only `attack a player` gained two.
- **`is dealt combat damage` was already fixed** and the lead was stale: three cards read it. The
  two still short die on something else in the same line.
- **`You may shuffle.` was briefed at 2 and is 3**, because Pondering Mage's other trailing
  sentence came with the same fix.

**Two engine fixes that had been located precisely and left alone.** Both were one line, and both
had been written down rather than taken because the round they were found in was about something
else:

- **Regeneration's replacement claimed in its own comment to remove the permanent from combat and
  did not.** `RemovedFromCombat` had existed since the Gustcloaks. Without it a regenerated blocker
  is still in `Combat.Blockers`, so the attacker it was holding up stays blocked and a later damage
  step finds a creature the rules say has left. The behaviour test for it was mutation-checked:
  deleting the new line fails it.
- **`Game.HandLimitFor` asked `permanent.Card` in both of its reads**, so a Clone of Gnat Miser was
  not a Miser for hand size and a Clone of Reliquary Tower did not lift the limit (CR 613.2c). The
  comment beside it had already said the seam was open and that the pair had to be closed together.

**A silent no-op class, found by building something next to it.** The generic trigger reader knew
one exception to "a trigger watches from the battlefield": the cycling one, added because a card
left at the default compiled cleanly and never fired. **A card's own `When you cast ~` is the same
shape and had no exception** - the cast is announced with the card already on the stack, so the
trigger was watching a zone it had left. Around fifty corpus cards print it, every one of them read
perfectly, and not one of them did anything. `SelfTriggerZone` now answers for both.

It also answers **null**, and that is the honest half. `When you cast or cycle ~` needs the ability
to watch from two zones at once - the stack for one arm, the hand for the other - and a
`TriggeredAbilityDefinition` has one `FunctionsFrom`. Compiled with either zone the card plays half
of what it says, silently, so the line is refused and Drownyard Lurker and Warped Tusker are two
cards this round gained and then gave back. Closing it means letting an ability watch from more
than one zone, which is a change to what a triggered ability is.

**"Expend" needed a number nothing was keeping.** CR 700.14 counts mana spent casting spells this
turn, and fires on the payment that takes the total from below N to N or more - so
`PlayerState.ManaSpentCastingThisTurn` plus a `ManaSpentCasting` event carrying **both** totals.
Both, because a trigger predicate is handed the state from one side of the event (CR 603.6) and
neither side alone says a line was crossed. What is counted is what actually left the pool rather
than what the cost named, which is the same distinction `PayMana` already returns for sunburst.
One seam is written down: mana somebody else pays through assist is counted for nobody.

**A refusal narrowed rather than lifted.** `FindsItselfByIndex` refused any deferred question
inside a free offer, a rule written when the locator was resolved against an ability's *top-level*
effects. `EffectTree.Locate` walks the whole tree now, so the refusal narrowed to what still cannot
be answered: more than one question in the branch, or a question already compiled whose index the
nested one could be confused with (`Locate` answers null on ambiguity rather than guessing). That
took the briefed 3 cards and 4 more nobody had listed.

Two more, each one word: **`its owner` is not `its controller`** (CR 108.3 - the life goes to the
player who lost the card, not to the one who stole it, which is the only board the sentence is
about), and **a coloured surcharge is not a generic one** - `Black spells you cast cost {B} more`
adds a black pip, and reading it as `{1}` gives the Leech cycle's controller a tax payable with
anything.

### Round fourteen: X inside a filter, and the four places it comes from

A filter is a predicate the compiler closes over, and **"target creature with mana value X or
less" has no number to close over**: X is chosen as the spell is cast (CR 601.2b). `TargetSpec`
was three delegates over a printed card, `SearchLibrary`'s bounds were `int?`, and neither had
anywhere to put a number that is only known at the moment of casting. That is why the family had
been declined twice, and the decline recorded further down this file was right about the danger
while wrong about the reason: it said "the cast-time filter check runs before the chosen X is
recorded", and in fact `CastSpell` has the announced value in scope the whole time - it simply
never handed it to anything.

**+22 cards, none lost** (16,505 → 16,527, by set difference on a rebuilt baseline).

**Measured before it was built, and the measurement decided the shape.** 201 corpus cards carry
an X inside a filter clause and all 201 are unread; 154 of them are one line short. A
substitution probe swapping the clause for a printed equivalent says **+21 for `mana value X or
less` alone and +27 for the whole clause**, `power X or less` and `toughness X or less` beside
it. Five of that 27 are refused deliberately - see below - which is where +22 comes from.

The same probe answered the two neighbouring questions and answered both **no**:

- **X in a cost blocks no card.** Substituting `{X}` for `{2}` across the corpus is **+0 / -3**:
  the symbol, the payment (`ManaPayment.Pay` takes the announced value) and the wire
  (`GameHub.ActivateAbility`'s positional `variableValue`) were all already there, so nothing is
  left unread for want of one. It was still *wrong* - `ActivateAbility` never handed the number
  to `PayMana`, so an activation cost of `{X}` charged nothing - which is the difference between
  a coverage gap and a behaviour bug, and only the second kind was there.
- **X in an amount was never the gap either.** `Amount.X` has read
  `ResolutionContext.VariableValue` since that type existed. Substituting a bare `X` for `2`
  everywhere is **+66 / -171** - net *negative*, because the amount reader already does better
  than a literal would. Of that +66, 27 are this family; the rest are other shapes entirely.

  So the three rows that were declined together are one row. Only the filter needed anything.

**The mechanism is a fourth thing a filter can be about.** `TargetSpec` already carried three
delegates - the candidate, the source, a sibling target - and `VariableFilter` is the fourth,
taking the announced X. `Accepts` and `IsLegal` gained a nullable `announced`, and every reader
that has one passes it: the cast (CR 601.2c), the activation (CR 602.2b), the resolution re-check
(CR 608.2b), the sweeper finding its set (CR 609.2), and the list of targets a trigger is offered.

**A missing X is refused, not defaulted, and the reason is sharper than fail-closed by habit.**
Read as zero, the *same clause* breaks in both directions: "X or greater" admits the whole
battlefield and "X or less" admits nothing. Neither is the printed card, so a caller that cannot
say what X is gets no legal target - the decision `PeerFilter` already makes, made again.

**The search bound is not a delegate at all.** A library search has to read back out of a log, so
its bounds travel in the event as numbers. They became `Amount`s on the *effect*, which is this
engine's existing answer to "a number not known until the spell is cast", and `Resolve` settles
each against the resolution before emitting. Nothing downstream of the event knows X exists.

**Three things were carrying zero and had to be taught to carry the value:**

- **An activation cost of `{X}` charged nothing.** `ActivateAbility` had taken an announced number
  since "remove any number of counters", and never passed it to `PayMana` - so `{X}, {T}: search
  your library for a card with mana value X or less` would have been a free tutor for as much as
  its controller cared to name. Six of the 22 cards are activated abilities.
- **An activated ability reached the stack with `VariableValue` zero**, and its `TargetsChosen`
  said zero outright, so the filter would have measured against nothing as it resolved.
- **A triggered ability reached the stack with nothing.** The permanent keeps what its spell
  announced (CR 607.2) and has since ravenous, but the ability is its own object (CR 113.7a);
  its targets would have been chosen against the right number and re-checked against zero, so it
  fizzled every time while the card looked implemented.

**The gate is what makes this honest, and it costs five cards.** X in a filter is only a card when
something announces one, and it comes from exactly two places: `{X}` in the spell's mana cost -
which a permanent then keeps for its own abilities - and `{X}` in the activation cost of the
ability the line prints. An activated ability announces **its own** X and never inherits its
card's, because CR 107.3 defines X by the cost being paid and a permanent cast for one value and
activated for another has two.

Everywhere else the sentence defines a different X in words this engine does not read, and five
corpus cards do exactly that: **Spellstutter Sprite** and **Unforgiving One** ("where X is the
number of Faeries/modified creatures you control"), **Go-Shintai of Hidden Cruelty** and
**Invasion of Lorwyn** (the same, counted differently), and **Taj-Nar Swordsmith**, whose X is
paid in the middle of a resolution. Compiled anyway, each would settle at zero, match nothing in
any zone, and report itself complete - a card that passes the deck gate and does nothing, which
is worse than an unread line. They stay unread. The probe was *wrong by 5 in the optimistic
direction* until the gate existed, which is the substitution probe's own known weakness: it
answers "if this clause read" and not "if this clause read *correctly*".

**Still declined, with counts.** 179 of the 201 remain unread, and apart from the five the gate
refuses, none of them is held back by the filter clause any more:

- **`with mana value X` exactly** (2 sole blockers, e.g. "Counter target spell with mana value X"),
  where the qualifier grammar reads only "or less"/"or greater". An exact match is a different
  comparison and one line, not a family.
- **"exile the top X cards ... you may cast a spell with mana value X or less from among them"**
  (about 8 lines, each unique) - a play-from-exile permission, which is a different capability.
- **`where X is <a count>` as a filter** - the five above plus a long tail. Reading them needs the
  counted-amount vocabulary to reach a *filter* rather than an amount, which is a real family and
  a separate piece of work.
- The remainder are each one-off sentences whose blocker is beside the X, not the X.

**And the board is told which abilities want a number.** `AbilityView.AnnouncesVariable` is the
same courtesy `CostChoices` is, one cost along: the announcement travels *with* the activation,
so an ability whose cost is `{X}` cannot be activated by clicking it, and a board that offered it
as a plain button would send an X of zero - which on "search your library for a card with mana
value X or less" is a search that finds nothing. The hub has carried `variableValue` positionally
since it was added; only the view could not say which abilities want one, which would have left
six of these 22 cards implemented and unreachable.

### Round thirteen: past half, and what a sweep is for

The named families were gone, so this round sliced the work queue by rank and gave four agents
fifty rows each. That shape earned its keep in a way a family round could not: **most of the
gain came from cards outside the rows that were read.** One agent claimed eight rows worth 24
cards and gained 50, because the shared vocabulary it touched freed 26 more elsewhere - the
poison sentence alone moved 15, toxic having been the only path to `GivePoisonCounters`.
Another took eight rows worth 16 and gained 58 for the same reason.

Three defects the coverage count reported as gains, each caught only by diffing the *set*:

- A causative reader that let its subject be anything undid a refusal this compiler had made
  deliberately - Aether Charge's "you may have **it** deal 4 damage" would have dealt from the
  enchantment rather than the Beast. Eight cards became different cards while the count rose
  by eight.
- A hand-size reader leaned on `NumberWord`, which answers 1 to anything it does not recognise.
  All eight cards compiled and every one moved the limit by exactly one.
- `~ attacks and isn't blocked` asked the **state** whether the attacker was blocked, but a
  trigger condition is evaluated against the game as it was before the event (CR 603.6), and
  before blockers are declared nothing is blocked. It fired on every attacker. Nobody had
  noticed because **no card using it had ever compiled** - the effect sentence beside it was
  unread, so the family sat in the queue behind a trigger that would have played every one of
  them better than printed. A defect can hide behind an unread neighbour, not only a stolen one.

**Two owed-settle bugs of one shape, making three this week.** `SettleOwedShuffle` returned from
the settle sweep as though a question were pending, so "each player shuffles their graveyard"
performed one and handed priority back with the other graveyard unshuffled - the same shape as
`SettleOwedRoll` and the zero-card discard. Every owed-settle site is worth auditing against
this: a step that asks nobody anything must continue the sweep rather than return from it.

**The obvious construction of a Pact kills its caster every time.** Mana empties as a step ends
(CR 500.4) and delayed triggers fire on entry, so a payment asked there is one nobody can ever
make - and CR 118.3 correctly declines on the player's behalf, because a question with one
possible answer is not a question. All four pacts would have compiled, passed the gate, and lost
their controller the game on schedule. The delayed ability goes on the stack instead, so priority
happens and lands can be tapped first.

### Two measurement techniques worth keeping

**The substitution probe.** Swap the suspect phrase for a known-read equivalent, recompile the
whole corpus, diff the complete set. It corrected three estimates by more than 2x in both
directions in one slice, and it is what proved the mid-resolution mana class was 19 cards rather
than the 161 that were one line short - the other 142 die on something else in the same line.
Run it in both directions: half of that agent's gain came from the player scope it added, not
the colour choice it was sent to build.

**Rebuild before measuring a baseline.** Two agents independently reported a warm worktree whose
built output was stale - one by 94 cards, one by 135. A `--no-build` coverage run against it
reports a baseline low by that much, and every gain measured from it is inflated.

### Round thirteen: a pre-game step, a delayed ability that reaches the stack, and one prohibition

Three families the compiler had been waiting on, each of which needed the *engine* to grow rather
than the pattern list. **+19 cards, none lost** (16,279 → 16,298, by set difference). The three
briefed counts were 6 / 4 / 5; measured, they are **6 / 4 / 9**, and the third is bigger than it
was described because the family is one sentence with three slots rather than one wording.

**The opening hand exists now, and it is a step rather than a flag.** `Game.BeginPlay` went from
the last mulligan straight to `BeginTurn()`, so CR 103.6 — "once the mulligan process is complete,
the starting player may take any such actions in any order, then each other player in turn order
may do the same" — had nowhere to happen. The decline recorded further down this file was right
that reading the Leyline line into `DeckRules` would have filed six cards as understood while
deleting the only thing they do. What it needed was small and contained once the shape was clear:
`ChoiceKind.OpeningHandBattlefield`, an `OpeningHandActionsTaken` event registered in both
`GameReducer` and `EventLogSerializer`, and `AskNextOpeningHandAction` walking the table from the
starting player.

The one design decision worth keeping is **why the marker is in state**. Which players have
already been past is a fact a saved game has to carry: `Game.Resume` folds a log and keeps no
private fields, so a game rebuilt while the second player is being asked would otherwise go round
the table again. `GameState.OpeningHandActed` is that fact, and it is a list rather than a flag
because the step is a queue — a boolean can say the step is running and cannot say how far round
it has got. A player holding nothing eligible is marked and skipped rather than asked to pick from
an empty list.

**+6**: Leyline Axe, Leyline of Abundance, Leyline of Lightning, Leyline of Sanctity, Leyline of
Vitality, Leyline of the Meek. Twelve more Leylines now read the line and are still short of
something else; Gemstone Caverns' longer version ("and you're not the starting player … with a
luck counter on it") is deliberately left unread rather than compiled into a card that starts in
play for nothing.

**A pact could not be built on the delayed vocabulary, and finding out why is the useful part.**
The obvious construction — a delayed trigger that asks for the payment when the upkeep begins —
produces a card that **kills its caster every single time**. Mana empties as a step ends
(CR 500.4) and `FireDelayedTriggers` runs on the way in, so the pool is guaranteed empty at the
moment the question would be asked; CR 118.3 then declines for the player, correctly, and the
else branch runs. Four cards would have compiled, counted towards coverage, and been strictly
harsher than printed.

`FireDelayedTriggers`' own note said what to do instead: "a delayed ability that drew a card would
be wrong here, and should go through the trigger machinery instead." `DelayedActions.Ability` is
that route. The prefix names an ability id on the delayed trigger's own card; `FireDelayedTriggers`
looks the body up through the same `EffectsOfAbility` that resolves every other trigger and emits
a `TriggerPutOnStack`. From there the pact is an ordinary object on the stack: an opponent may
respond, and its controller may tap lands before it resolves and then be asked. The body is a
plain `MayPay` whose `IfYouDont` is a new `LoseTheGame` effect (CR 104.3e), which is why CR 118.3
needed no new code — a player who genuinely cannot pay is still not asked.

The ability-bodied arm is also the first delayed trigger that waits for a **turn** rather than a
step. Every card printing this shape says "at beginning of *your* next upkeep", and a pact that
came due on the opponent's upkeep is the same always-fatal card by another route. `IsDueNow` scopes
only that prefix, because the word-based delays genuinely mean the next such step in the game.
**+4**: Pact of Negation, Pact of the Titan, Slaughter Pact, Summoner's Pact. Intervention Pact now
reads the payment and is short its prevention clause.

**One seam is open and written down in the code**: the ability is looked up through the card the
delay was created by, so a pact whose card left the graveyard owes nothing. That is wrong — a pact
is owed whatever became of the card — and fixing it means the delayed trigger carrying the cost
and text itself, which changes what a stored delayed ability is.

**The cast limit was the briefed count doubled, and needed no new state at all.** "Each player
can't cast more than one spell each turn" is **5** sole blockers on that exact wording and **9**
across the family, which is one sentence with three slots: the subject (`Each player`, `You`,
`Enchanted player`), the number, and an optional qualifier that narrows what is stopped *and* what
counts. Reading it as one reader rather than five is what makes the other four worth having.

CR 601.3's second clause — "no rule or effect prohibits that player from casting it" — had no
implementation: `SpellDefinition.CastOnlyWhen` is a restriction a card prints about *itself*, so
nothing on the board could prohibit anything. `CastLimit` is a value on the card rather than a
continuous effect because there is nothing to compute; CR 613 orders effects that change objects'
characteristics, and a limit on casting changes no object. The counting came free:
`PlayerState.SpellsCastThisTurn` and `SpellCardsCastThisTurn` have existed since "your first
enchantment spell each turn", and keeping the *cards* rather than only a tally is exactly what
makes "more than one non-Phyrexian spell" answerable.

Two details are the rule rather than the code. **The hyphen is load-bearing**: "noncreature" is a
card type written closed up and "non-Phyrexian" is a subtype written with one, and a reader that
took them for the same thing builds a limit matching no card while reporting the card understood.
And **the qualifier narrows both halves at once** — a limit on noncreature spells neither stops a
creature spell nor counts one; applying it to only one half gives a card that stops the wrong part
of a turn. A qualifier the reader has no meaning for refuses the whole line.

**+9**: Arcane Laboratory, Archon of Emeria, Curse of Exhaustion, Deafening Silence, Eidolon of
Rhetoric, High Noon, Moderation, Phyrexian Censor, Rule of Law. Two more read the line and are
short something else (Colfenor's Plans, Yawgmoth's Agenda), and Hedonist's Trove's "more than one
spell **this way** each turn" is a restriction on a granted permission rather than this rule, and
is correctly refused.

**What the board is not told.** A cast limit is the first refusal in the engine that comes off
somebody else's permanent, and `GameView` carries no castability at all — so the board will offer
a second spell and the engine will refuse it. That is the "offered and then refused" failure this
document warns about, one card short of the client work that would fix it, and it is recorded here
rather than guessed at from this repo.

### Round twelve: six structural walls, and what measuring them was worth

This round targeted things the engine could not *express* rather than lines it had not read.
+143 cards, none lost. What is worth keeping is that **five of the six briefs were wrong about
their own family**, and every correction came from a probe rather than an argument:

- The damage back-reference was briefed at "at most 95 cards". It is two families sharing a
  verb: 310 cards print the *subject* pronoun ("**It deals** 4 damage"), 264 the *object* one
  ("deals 4 damage to **that creature**"). 552 print one, 76 are reachable by respelling, 47
  were taken - and the money was in the subject pronoun, which the brief had not mentioned.
- "Any number of target" was briefed as a structural wall. `RequireLegalTargets` had always
  validated a *range*, so the block grammar was buildable with no new state, event or hub
  argument. Then the decisive measurement: recompiling all 147 remaining cards with "up to two
  target" substituted completes **zero** of them. The block grammar is now as complete as the
  effect vocabulary behind it allows, and widening its pattern buys nothing.
- The arrival family was briefed as one class of ~30. Decomposed: enters-tapped is 15 sole
  blockers, enters-with-counters was **already built two rounds ago**, enter-untapped and
  enter-under-another's-control are worth one card between them, and **no corpus card says**
  "enter face down" as a group static at all.
- Delayed destroy was briefed at 18 and completes 14; the impulse tail was briefed at 15 and the
  trailing sentence turned out to be worth exactly **one** card, the real blockers being a
  pronoun the reader did not know and a duration printed behind the permission.
- The prevention tail's recorded decline - "26 name a target the shield cannot aim at" - was
  **half stale**. Nine turned over with no pattern work once the shield could name a source,
  because `Specs.Parse` had grown `attacking`/`blocked`/`unblocked` in the meantime. The other
  half stands, and now says why: those adjectives live in the grammar that reads a *target*,
  not the one that describes a *set*.

Two defects found that the coverage number could never show. `Specs.ParseGroup` read the pronoun
**"You"** as a creature type, so "You gain shroud until end of turn" compiled to a grant aimed at
a tribe no card has - Gilded Light read as complete, passed the deck gate, and did nothing when
it resolved. Refusing the pronoun costs one card from the count and removes a blank from the
game. And a first cut of the delayed destroy let "that creature" take the source fallback that
only "it" is entitled to, compiling Tangle Asp, Venomous Dragonfly and Infernal Medusa into
creatures that destroy themselves whenever they block - the count rose by three while three
cards became wrong, and only the set diff showed it.

### A delayed destroy, and an idiom that could not stand a sentence beside it

Two families measured before they were built, and both were smaller than the probe that found
them. **+27 cards, none lost** (16,138 -> 16,165), and the set diff is the number that says so:
a net gain hides a stolen neighbour, and this round it very nearly hid three.

**"Destroy it at end of combat" was recorded as 18 cards and completes 14.** The delayed
vocabulary had three words - sacrifice, exile, return-to-hand - and no destroy, deliberately:
its switch's last arm is a sacrifice, and CR 701.21a says sacrificing a permanent does not
destroy it, so regeneration (CR 701.19b) and indestructible (CR 702.12b) both miss it. Reading
the one as the other makes every one of those cards harsher than printed. `DelayedActions` now
names the four words in one place, `FireDelayedTriggers` has a destroy arm above the zone table
that asks about indestructible and lets the ordinary `MoveCause.Destroy` replacement find the
regeneration shield, and the line reads.

The larger half was not the verb but **the subject**. Of the 33 corpus cards carrying a delayed
destroy, only four say "destroy ~"; the rest say "it" or "that creature", and those mean the
creature the spell targeted or the creature the trigger was about. `DelayObjectAction` resolves
the pronoun through `ObjectOf` - the same reader every other verb uses - and aims the delayed
ability at what it found. That is what Ohran Viper, Mogg Cannon, Blood Frenzy, Serpentine
Basilisk, Lowland Basilisk and Puffer Extract needed, and widening the same subject group for
the three older verbs picked up Footsteps of the Goryo, In Thrall to the Pit, Lowland Oaf and
Soulshriek without changing how any existing card is read.

**The first cut of it destroyed three basilisks.** "That creature" was allowed the same
last-resort fall back to the source that "it" has, and blocking names no object the engine can
hand a sentence - `NamesAnObject` refuses the verb because a declaration is a batch - so Tangle
Asp, Venomous Dragonfly and Infernal Medusa compiled into creatures that destroy themselves
whenever they block. The complete count went **up by three** and the three cards were wrong.
Only "it" may fall back now, and a destroy may take even that step only when nothing before it
in the ability produced another permanent to mean, so "create a token, destroy it" stays unread
rather than blowing up the card that made the token. Both are behaviour tests.

**Measured and not fixed: 52 complete cards read a delayed "sacrifice it" as the source.** The
older verbs fall back to the source unconditionally, and about a dozen of those cards mean
something else - Planebound Accomplice sacrifices itself instead of the planeswalker it put
onto the battlefield, Slave of Bolas sacrifices a spell that is already in the graveyard and so
keeps the stolen creature forever. Correcting it needs its own measured pass: the honest fix
refuses the cards whose pronoun names a token, which *loses* cards. Widening that reading to a
verb that destroys was never on the table, which is the whole of why the destroy arm is stricter
than its neighbours.

**The impulse's blocker was not the trailing sentence.** Crossover Collaboration reads as an
impulse until a third sentence follows it, and the family was sized at 15. It completes 13, and
the trailing sentence is worth exactly **one** of them - the control asked for and the control
that mattered. The other twelve were two vocabulary gaps hiding behind it: six cards print "you
may play **it** this turn" where the reader only knew "that card", and five print the duration
behind the permission ("you may play that card **until the end of your next turn**") where the
reader only knew it in front. The tail itself was the shape `TryLookAndTake` had already solved
two hundred lines above - match the pair, then read what follows the ordinary way - and the tail
is read rather than dropped, so a tail nothing can read still refuses the whole line.

**The work queue's head is worth nine cards.** Both of these families are larger than anything
ranked in it, which is what a flat queue looks like from the inside: the leverage has moved out
of the line ranking and into shapes that have to be found by probing a hypothesis against the
corpus.

### Somebody else's arrival, and a family measured before it was built

`CardCompiler.Arriving` answers one question — `moved.OldId == source.Id`, "is this my own source
entering" — and every enters-tapped reader in the compiler was built on it. So a card that
changes how *other* permanents arrive had nowhere to compile to, however ordinary its sentence.
That is CR 614.1d, and round eleven's decline, recorded further down this file, was right that
it is not a duration problem.

**16,138 → 16,156 complete cards, +18, none lost, measured by set difference.**

**The family, measured before anything was built.** The class is "a static ability that changes
how other permanents enter", and the honest unit is *cards a fix completes* — cards where the
line is the only thing the compiler cannot read. Two of the five groupings turn out to be worth
almost nothing, and one was already finished:

| grouping | cards it would complete | cards touching | verdict |
|---|---|---|---|
| **enter tapped** — the group static | **15** | 24 | built; 14 taken, 1 declined |
| enter with counters — the group static | 13 | 19 | **already built** (`TryGroupEntersWithAdditionalCounter`); the 13 left are harder variants, not this gap |
| enter untapped — the group static | 1 | 6 | declined, and it is a different mechanism |
| enter under another player's control | 1 | 1 | declined — one card, and a one-shot with a duration |
| enter face down — the group static | **0** | 0 | **no corpus card says it** |

The earlier note read "4 on that exact wording, 13 across the family", and named three other
groupings the class was supposed to include. Measured against the compiler rather than against the
corpus text: the enters-tapped grouping is **15 sole blockers**, not 13; **entering face down is
not a grouping at all** — no corpus card prints it about a group; entering under another player's
control is **one card**; and **entering with counters was already built** two rounds ago and is
not this gap. Two of the three neighbours the note gestured at are worth one card between them.
Counting the class as one number would have promised about thirty and delivered eighteen. That is
the dice round's overstatement arrived at from the other direction, and the fix is the same one:
count the cards a change *completes*, not the lines that mention its subject.

**`TryGroupEntersTapped` is the reader, and three details are the rule rather than the code.**

- **Only the first word of the noun phrase is lowered.** The phrase always begins its sentence,
  so its capital says nothing; reading it as printed looks for a creature type called "Creature",
  which no card has, and builds a static that matches nothing while compiling clean. That is
  exactly the silent no-op the mass-static reader records on 27 lines. Every *later* word keeps
  its case, which is what lets "Snow lands your opponents control" and "Non-Phyrexian creatures"
  read correctly rather than being flattened.
- **A missing ownership clause means everyone.** Orb of Dreams says "Permanents enter tapped" and
  taps the whole table. Defaulting a missing clause to "you control" is the 83-card defect this
  file already records one layer over, and the behaviour test asserts the controller's *own*
  creature arrives tapped so that reading cannot come back.
- **The source is never the permanent arriving, and two separate guards say so.** CR 614.12's own
  example is Orb of Dreams not affecting itself. A permanent spell resolving is still on the
  stack while its own arrival is replaced, so `FunctionsFrom` — the battlefield, which is where a
  static functions from — excludes it; a token is built by the pipeline out of its own event and
  arrives already on the battlefield, so only the identity check excludes it. Both were mutated
  away and a test failed for each.

**One seam is left open on purpose and is written down in the reader.** Whose permanent is
arriving is read from the stored `ControllerId`, which is only where control *started* — control
is layer 2 — so a stolen Kismet asks about its original controller's opponents. `Applies` is
handed a state and no `IAbilitySource`, so the computed characteristics are out of reach without
widening every replacement in the engine; the group-counter reader has the same seam. That is one
change to the replacement signature, not a special case for this family.

The wrinkle the earlier note warned about is real and is handled by a helper that already
existed: for an `ObjectMoved` the arriving object is not in the state yet (CR 400.7), so
`Entering` reads its card out of the object it still is in the zone it is leaving, and its
controller off the event. `Arriving`'s sibling, and it had been sitting beside the group-counter
reader the whole time.

**The 14 taken**, by set difference: Authority of the Consuls, Blind Obedience, Frozen Aether,
Imposing Sovereign, Kinjalli's Sunwing, Kismet, Loxodon Gatekeeper, Manglehorn, Orb of Dreams,
Root Maze, Spider-Woman Stunning Savior, Thalia and The Gitrog Monster, Thalia Heretic Cathar,
Urabrask the Hidden. Six more cards had the line read without being completed, because they are
short something else: Archon of Emeria, Dauntless Dismantler, False Floor, Phyrexian Censor,
Zhao the Moon Slayer, and Reidane.

**And the sibling was four more.** "If ~ would be put into a graveyard from anywhere, reveal ~ and
shuffle it into its owner's library instead" is the same rule aimed at the source's own zone
change, and two of its three destinations were already read. The third needed more than a
different zone: **shuffling into a library is a move and then a request, not a position.** The
other two arms finish by naming where in the destination the card lands, and a shuffle names no
position at all — the order is the game's to decide and comes from the seeded source, which no
effect can reach. So the card moves to the library and leaves a `ShuffleRequested` for the settle
loop. Putting it on top and calling that close enough would leave Blightsteel Colossus on top of
its owner's library, which is a tutor rather than a removal, and the behaviour test counts the
library rather than only checking the graveyard for exactly that reason. +4: Blightsteel Colossus,
Darksteel Colossus, Legacy Weapon, Nexus of Fate.

**Declined here, with the measurement behind each.**

- **`Creatures played by your opponents enter tapped.`** — 1 card (Uphill Battle). "Played" is
  narrower than "enters under their control": a token an opponent creates was never played.
  Reading it as the ownership clause taps permanents the card does not name, and one card is the
  right price for not doing that.
- **`Permanents enter tapped this turn.`** — 1 card (Due Respect). The same sentence with a
  duration, on a sorcery. A pattern loose enough to claim it hands that spell a permanent lock on
  the table, which is the harshest available misreading of the gentlest available line. It is a
  test case rather than a target.
- **`As long as ~ is tapped, other permanents enter tapped.`** — 1 card (Archelos), 2 lines
  short anyway. A replacement gated on a condition, which is a different shape from a static one.
- **`Creatures enchanted player controls enter tapped.`** — 1 card, 2 lines short. The
  mass-static reader has a branch for a group defined by a relation to the source and this one
  does not; the scope word is matched rather than skipped so an unrecognised clause leaves the
  line unread instead of quietly meaning everybody.
- **`Lands you control enter untapped.`** — 1 sole blocker (Gond Gate), 6 touching. Not the same
  mechanism turned round: the engine's arrivals are untapped already, so this has to *suppress*
  another replacement rather than add an event. That is a change to how the arrival carries its
  tapped state, not another group reader.
- **`If a creature would enter the battlefield under an opponent's control this turn, it enters
  under your control instead.`** — 1 card (Gather Specimens), and the whole of the
  "enters under another player's control" grouping.

**The mana neighbour is the trigger's effect, not the trigger.** The recorded note said
"`ManaAdded` carries what it needs", and it does — `TriggerConditions` has read "whenever a
player taps a land for mana" since Manabarbs, and `SubjectOf` already answers which player. The
blocker is the other half of the sentence. `Whenever a player taps a land for mana, that player
adds one mana of any type that land produced` is **4 sole blockers** (Dictate of Karametra,
Heartbeat of Spring, Mana Flare, Zhur-Taa Ancient) of the **7** the trigger touches, and it needs
three separate pieces of engine: a mana colour chosen *mid-resolution*, which `EffectPhrase`
refuses by name today — "there is nowhere to ask it"; the set of types a particular land can
produce, read at resolution; and an `AddMana` with a player scope, since it puts mana in somebody
else's pool and the effect always uses `context.ControllerId`. The other 3 of the 7 want a
different effect again (Price of Glory destroys the land; Overabundance and Barbflare Gremlin add
damage). Worth doing as a mana-choice change, not as a card-reader one.

### A player is not an object, and now has abilities of its own

The engine could say a *permanent* had hexproof and had no way to say a *player* did.
`TargetSpec.IsLegal`'s player arm asked three questions — does the seat exist, has it lost, does
the filter admit it — and returned; the hexproof and shroud checks underneath it ran against
`Characteristics.Of`, which exists only for game objects. `PlayerState` carried no keywords at
all, so there was nothing for a compiler reader to produce even if one had been written. That is
why "You have hexproof" was unread: not the wording, the subject.

`PlayerCharacteristics.Of(state, abilities, playerId)` is the missing half, and it is built the
way the object half is — **computed, never stored**. It sweeps the battlefield, asks each
permanent what card it *is* now (CR 707.2a, so a permanent that has become a copy of Aegis of the
Gods has Aegis's ability), and unions the grants that reach this player. A `PlayerQualityDefinition`
is what a static ability contributes; `IAbilitySource.PlayerQualitiesOf` serves them, and
`PlayableCards` delegates it like everything else.

**There is no layer here, and that is a rule rather than a shortcut.** CR 613 orders the effects
that change *objects'* characteristics. A player has none of those: several sources of the same
keyword are simply redundant (CR 702.11h, 702.18b), so the grants are unioned and no order can
change the answer. The definition therefore carries no layer and no timestamp, and adding one
later would be a claim about the rules rather than a refinement.

The one thing a sweep has to remember is that a permanent can stop having its ability: CR 613.1f,
and a player behind a Humility'd Aegis of the Gods has no hexproof. The check is a full layer
computation, so it is asked only of permanents that actually granted something — on any real board
none or one — which is how the object path pays for the same question.

Three things fell out of building it that were not the point of building it:

- **The predicate needs the ability source, and the test is what said so.** "You" is whoever
  controls the permanent *now*, which is layer 2 (CR 613.1b) — the stolen-lord shape, in the one
  place it had never been made. `Characteristics.ControllerOf` answers it, but it finds a control
  change by looking the floating effect's definition up through the ability source, so the first
  version — which passed `EmptyAbilities.Instance`, because the predicate had no source argument —
  answered "who controls this" with where control *started*. A stolen Aegis of the Gods went on
  protecting the player it was taken from. Only the control-change test failed; the other seven
  were green.
- **The silencing check was missing.** The first version swept the battlefield and applied every
  grant it found, so a permanent stripped of its abilities went on granting. None of the eight
  tests written for the family would have caught it — every one plays a board with the source
  intact — and it was found by reading the sweep beside `Characteristics.Candidates`, which does
  the same thing and remembers to ask.
- **"You" was being read as a creature type.** `Specs.ParseGroup` resolves a group phrase by
  finding the capitalised noun in it, so `"You gain shroud until end of turn"` compiled to a
  keyword grant aimed at a tribe no card has — the "Islands"/"Assassins" defect arriving through
  a pronoun instead of a plural. **Gilded Light read as a complete card, passed the deck gate and
  did nothing at all when it resolved.** The pronoun is now refused there, which costs that one
  card off the complete list and is the right trade: a card a deck check refuses is strictly
  better than one that plays as a blank.

### The size of the player-quality class, measured

**39 playable cards** grant a player a keyword: hexproof on 21, protection on 14, shroud on 4
(13 of them also spell "can't be the target" out in full). That is the whole class — the
`"You have hexproof."` line the work queue ranks is 12 appearances and 4 sole blockers, and is one
wording of it.

Reading the **static** half completes **7**, and the diff is the whole set: Aegis of the Gods,
Ivory Mask, Metropolis Reformer, Sigarda Heron's Grace, Spirit of the Hearth, Teyo the
Shieldmage, True Believer. Against Gilded Light leaving, the net is **+6** — and the one that
left was complete and inert, which is the trade this project has said it wants every time it has
been asked.

What the rest is blocked on, counted rather than estimated:

- **18 say "gain … until end of turn"** rather than "have" — Gilded Light, Lazotep Plating,
  Blossoming Calm, Teferi's Protection, Veil of Summer, Everybody Lives! and the rest. A one-shot
  grant to a player outlives its spell, so it has to be recorded, and `FloatingEffect` records
  object ids only. The cheapest honest shape is an `AffectedPlayers` beside `AffectedIds` — its
  two durations and its expiry sweep then work unchanged — but that is a state field, so it is a
  reducer arm, a serializer arm and a line in `GameState.Equals`. **3 of the 18 would complete**
  (Gilded Light coming back, plus Lazotep Plating and Blossoming Calm); the other 15 have
  something else unreadable in the same line. Deferred as a separate unit, not skipped.
- **14 cards give a player a protection**, and not one of them names a quality this engine has a
  flag for: "everything", a chosen card name, a chosen card type, a named player, a creature type.
  The flags that exist are the five colours and artifacts, and no card gives a player any of
  those. So the reader refuses protection outright and `ComputedPlayerCharacteristics` carries no
  protection arm — a rule nothing can reach, sitting beside two that fire, is where the next
  silent defect goes. Absolute Virtue's printed line is the negative control. Four of the 14 print
  it as a static "have"; the rest are inside the one-shot family above.
- **2 put a condition in front of a compound subject** — Gruul Spellbreaker's "During your turn,
  you and ~ have hexproof" and Captain America's shield-counter version. The compound form is
  folded (the player half is taken and the remainder handed straight back to the group readers,
  which is how Sigarda reads), but neither the condition wrapper nor "~" as a group member is
  part of that fold yet.
- The remaining **8** are now *one line* short, on a line with nothing to do with players: a
  leyline opening-hand permission, a Curse sweeper, three prevention shields, a venture
  restriction, a teammate token, a life-total replacement. Reading the player line moved every one
  of them from two blockers to one, which does not show up in a card count and is where the next
  pass should look.

The consumer that matters is targeting, and it is one place: `TargetSpec.IsLegal`'s player arm now
asks the two questions the permanent arm has always asked — CR 702.18a's shroud, which stops
everybody including the player themselves, and CR 702.11c's hexproof, which stops only opponents.
`Game.LegalTargetsFor` asks that same method, so **the list the board renders and the refusal the
cast makes cannot disagree**; there is a test asserting the hexproof player is not among the
options rather than assuming it.

### Round eleven: finishing interrupted work, and four instrument defects

Seven branches landed, +203 cards, none losing a card. Four of the seven were resumed from work
a session limit had cut off mid-debug, and the resumption is the part worth recording: every one
of those four failures turned out to be **the instrument, not the engine**.

- The soulbond regression was two tests arguing over a missing dictionary word.
  `A_self_pump_grants_its_keyword_in_the_same_sentence` is a negative control proving that a
  sentence whose grant half is unreadable leaves the *whole* line unread - and it happened to
  prove it with a card granting phasing, which that branch had just added to the grantable
  table. The control is now anchored to `protection from Zombies`, a quality that table
  structurally cannot hold, so it cannot be armed by accident again.
- The scopes regression was a misplaced parenthesis: a `withcounter` capture group opened before
  an alternation and closed after it, so every keyword-qualified lord ("other creatures you
  control **with flying** get +2/+2") read the qualifier as a counter requirement and buffed
  nothing. Both lord tests were right and the reader was wrong.
- Three of the dice branch's four failures were the tests: one had been overtaken by station
  thresholds landing on the trunk, one forgot that `PutInHand` adds a card, one had already
  passed priority away before the activation it was testing.
- The spellfacts branch failed nothing but `MechanicCoverageTests`, which refused five reader
  shapes no played game exercised. Writing those five games then exposed a real gap in
  `CardCompilerInvariantTests`: it called a modal card broken when its mode floor was zero, and
  two printed headers - "choose up to four" and "choose X" - legitimately have one.

**The stolen-lord defect is closed**, and the CR 613.8 loop this document recorded as the reason
not to attempt it is real rather than theoretical: using computed characteristics at those call
sites aborts the test host with a stack overflow, `DependsOn -> Matches -> Of -> ApplyLayers`
repeating, because Alice's bear needs Bob's lord which needs Alice's lord. The safe shape
gathers only layer-2 control candidates and guards a nested ask.

### Two corrections to how this effort measures itself

**The work queue overstates a family whose blocker is a block.** It promised 119 completable
dice cards; the honest number was 29, because it ranks *lines* and one line of a dice card is an
entire results table, unread the moment anything inside it is. The 66 still one line short have
66 distinct blockers between them.

**A whole-line ranking overstates a permission by an order of magnitude.** `"You may choose new
targets for the copy"` is the sole unread line on 104 cards and would complete **8**; on the
other 96 it is the rest of the sentence that defeats the compiler. Those 8 would only complete
by dropping the printed permission, which needs a question asked mid-resolution - the one thing
this engine has nothing to ask with - so they stay unread.

### Round ten, and what an interrupted round leaves behind

Seven agents were stopped mid-flight by a session limit rather than by failure. Three had
finished: the initiative and Undercity (the room that blocked it turned out to be sayable with
the `UntilTurnOf` duration built for a different dungeon the round before), cleave with the
square-bracket defect closed in the same change as the doc demanded, and CR 310 battles -
defense counters, a protector, being attacked, and the defeat that exiles and recasts.

The battle refusal added by the previous gate was **narrowed rather than deleted**: a battle
that is not a Siege is still refused whole, because the protector provisions differ by battle
type and only the Siege's are implemented. Every battle printed into a legal format is one.

The other four are parked on their branches, committed but unmerged, and the triage is worth
recording because it is the shape an interrupted round always takes. All four compile. One
(bargain and per-kicker read-back) fails nothing but `MechanicCoverageTests`, which refuses
five new line shapes that no played-game test exercises - the gate working exactly as designed,
since a reader without a game behind it is how a card comes to compile and not play. That one has
since been resumed and finished; the section below records it. One (dice)
fails only its own new tests. **Two regress pre-existing tests** - soulbond breaks a self-pump
keyword grant, and the source-scoped group work breaks a keyword lord - and those two are the
reason none of the four was merged on a "it compiles and mostly passes" basis.

### Resuming the parked spell-facts branch, and what the gate was actually protecting

The bargain-and-per-kicker branch was picked back up and finished. Nothing about the readers
needed changing: all five refused shapes — `KickerAndOrLine`, `BargainDiscountLine`,
`FactModalHeader`, `UpToModalHeader`, `ChooseXHeader` — were correct, and what they were missing
was a game. Five played-game tests now cast the real cards and assert the board, and the branch
carries **+55 complete cards over the merged tip with none lost**, the same number the
interrupted agent had measured before the round's other work landed on top of it.

What the tests are worth is visible in what they had to distinguish. Anavolver's two kicker
clauses hand out *different* counters and *different* abilities, so a permanent that only knows
it was kicked cannot be told from one that knows which kicker was paid — two counters and flying
for the `{1}{U}` clause, one counter and a regenerate ability for the `{B}` one, three counters
and both gifts when both were paid, and kicked twice for it (CR 702.33d). Bargain's reduction is
proved by the two mana that cannot pay the printed cost and do pay the bargained one, with the
sacrifice really taken. "Choose both instead" is asserted in both directions, because a swap read
as a widening would let a teamwork caster take one mode and a reading that ignored the clause
would refuse both.

**And the gate caught a second thing on the way out, which is the argument for running it.**
`CardCompilerInvariantTests` refused Doomsday Confluence and Moment of Reckoning for having
"modes but chooses none" — an invariant written back when every modal header this engine read
had a floor of at least one. Two printed headers do not: "choose up to four" puts a ceiling above
a floor of zero, and "choose X" takes its count from the announcement (CR 700.2d, 601.2b). The
invariant now asks whether *anything* can reach the bullets — a ceiling above the floor, or an
announced X — instead of only whether the floor is above zero. The defect it was written for, a
menu with all three numbers at rest that nobody can order from, is still caught. Nothing in the
rules suite could see this: the cards compile, and they play.

**The two "near-misses" turned out to belong to other families.** Helicarrier Strike and Crossover
Collaboration were being diagnosed as cast-fact failures and are not: the teamwork rider reads on
both, and the sentence it wraps does not. Helicarrier's inner sentence — "it deals 4 damage to
that creature" — is unread on its own, a back-reference family: 552 cards print one, 76 are reachable by respelling it, and 47
were taken in round twelve. Crossover's
line is the impulse idiom with a third sentence after it, and the same line with an ordinary third
sentence is equally unread, so the blocker is the impulse reader's intolerance of a tail, worth at
most 15. Both are separate work with their own tests; neither is evidence against the readers this
branch added.

### What the round-end gate caught that no agent could

Running the full suite once at merge, on the whole merged tip, found four things the per-agent
gates could not have. `PlayableCards` was not delegating `AmplifyCountOf` and `HasReadAhead`,
so both mechanics worked in every test and would have silently vanished in production - the
reflection test exists for exactly this and fired. Ten battles compiled "complete" while the
engine has no CR 310 at all - no defense counters, no protector, no defeat - so the compiler
now refuses the battle type outright, fail-closed, until the engine can play what the type
line promises. The 48 Unfinity sticker sheets left the corpus: they are supplements with
ticket costs that no deck may contain, and four of them had become "complete cards" nothing
could ever cast. And four reversible cards - the same card printed on both physical sides -
arrived typeless because their type line lives on the faces, so no soak would ever have
selected them.

The census assertion that flagged it ("a card kind has appeared that nothing plays") now
names the cards it cannot classify, because the bucket label alone sent the first
investigation to battles when the residents were reversible printings of ordinary cards.

### Rounds eight and nine: ten agents, +410 cards, and what the denominator hides

The largest single round of this effort: ten parallel agents, every one measured by the *set*
of complete cards rather than the count, and none lost a card. What landed: level up compiled as
bands (+22); the prevention family wired to its readers (+49); alternative costs paid in
something other than mana, plus emerge, spree and repeatable modes (+65); the copy reader (+24);
the attached-conjunction fold (+36); counter kinds that read their own names, CR 122.1c (+30);
seek (+7); mutate as a merged stack of cards (+25); dungeons as command-zone rules objects
(+30); loses-all-abilities and held-while durations wired (+46); the defender-side combat
triggers (+25); and nine combat keywords with their engine halves - sneak, awaken, amplify,
phasing, ripple, teamwork, hideaway, ravenous, read ahead (+51).

Five defects worth remembering came out of it. Every printed -1/-1 enters-counter compiled as a
+1/+1 - sixteen cards served to games at the wrong size, invisible to coverage because the cards
counted as read. A cast had never charged a ReturnToHand cost; two agents found it
independently and their fixes were merged into one arm that also honours CR 108.3 (home to its
owner, not the payer). A trigger granted by an Aura, a dungeon room or a buried mutate
component went to the stack with no targets, because the lookup lacked the source id - also
found twice. The layers hook for copiable values never ran on a board with no continuous
effects. And ability ids collide across cards, which Mirage Mirror turned into resolving the
wrong ability in a played game.

**The denominator now has a measured floor of unimplementable cards in it.** 48 Unfinity
sticker sheets are in the corpus with type line `Stickers` - ticket costs, no mana cost, not
legal in any deck; they are supplements, not cards. The digital-only family is 541 cards, of
which `spellbook` (62) has no contents anywhere in the bulk data, `specialize` (19) has nothing
to specialize into because all 85 specialized versions are excluded from every format, and
`perpetually` (246) is defined to survive the exact zone change CR 400.7 builds this engine's
identity on. Attractions (46) need an `attraction_lights` field the data does not carry. 100%
of the corpus as loaded is therefore not reachable by reading cards; the honest ceiling is
lower by several hundred, and reaching a number that means anything requires deciding what the
denominator should be.

### A capability the compiler cannot reach is not coverage

Copiable values (CR 706, 707) landed complete and correct: a permanent under a copy effect *is*
the copied card, proved by five tests that play a real game — it fires the copied attack trigger,
activates the copied `{T}` ability, a copy of a lord pumps a third creature the spell never
touched, and CR 707.2's exclusions hold, so a 1/1 that entered with a `+1/+1` counter and copies a
3/3 is a 4/4 and stays tapped. The whole copied card round-trips through `EventLogSerializer` and
`GameReducer.Replay`. **Coverage moved by exactly zero**, because the compiler emits no copy
effect, so nothing in the corpus can ask for any of it.

That is the third time this session the same shape has appeared — the deck gate above, the
prevention shield that was half a feature, and now this — and it is worth stating as a rule rather
than as three anecdotes: **engine work is not coverage until a printed card reaches it.** The
honest ceiling here was measured before the work started and is 79 of the 132 cards that print a
copy sentence, because 79 of 135 such sentences carry an "except" clause and the ranking of
copy templates is completely flat.

### The line ranking is not a work queue, and two families proved it again

Two plausible "doors" — a whole family reachable through one grammar — were checked and both
were false. **Sagas** read 34 of 215, which looks like the chapter grammar is missing; it is not,
and what blocks the other 181 is 181 bespoke chapter effects plus `Read ahead`. **Planeswalkers**
read about 5% of some subtypes, which looks like loyalty abilities are unread; `TryLoyaltyAbility`
has read `+1:` and `-3:` for a long time, and what blocks 353 planeswalkers is one bespoke loyalty
effect at a time. The work queue agrees: its top ten templates are worth 89 cards between them,
0.3% of the corpus, and its top *thousand* are worth 1,902. The remaining corpus is a long tail of
roughly 9,700 distinct blocking sentences at about 1.1 cards each, and no ranking of sentences
changes that.

What is still shaped like a door is the **keyword backlog** — each one a self-contained grammar
with its own CR 702 section: `{M} - N/N` (48 cards), sneak (27),
level up (25 cards, printed as four line kinds), soulbond (24), spree (21), kicker-and/or (18),
overload (17), teamwork (17), awaken (15), double team (15), specialize (15), hideaway (14),
emerge (14), cleave (12), phasing (12), ravenous (12), backup (11), tribute (11), prowl (10),
read ahead (10).

**Mutate (CR 702.140) is off that list**, and it is the one that was not a grammar at all. The
other entries are templates; this one is a change to what a permanent *is*, because CR 702.140e
makes a mutated permanent one object represented by a stack of cards — the topmost card's
characteristics and *every* card's abilities. `GameObject.MergedComponents` holds the cards under
the top one and `GameObject.Card` stays the topmost, which is what made it affordable: CR 730.2a
says a merged permanent has only its topmost component's characteristics, so every existing reader
of `obj.Card` — the layers, the view, the legality checks, `Characteristics.CardOf` and therefore
every copy effect — was already answering the question the rule asks. Only the abilities are the
exception, and abilities are not characteristics: they are looked up from an `IAbilitySource` *by
card*, so the components' go where a copied card's already go, `GrantedActivated` and
`GrantedTriggers`, which `Game.ActivatedAbilitiesOf` and `Game.TriggersWatching` already union in.

Three things it turned up that nothing else had:

- **The copiable-values hook never ran on a quiet board.** `Characteristics.ApplyLayers` iterates
  the layers *that have effects in them*, and read the copied card at the end of the layer 1 group
  — so with no continuous effect anywhere on the battlefield there was no layer 1 group and the
  hook did not fire at all. Invisible for the copy reader, whose own first line is "return if this
  is not a copy", and fatal for mutate. It now runs when the loop passes layer 1 in either
  direction, and again after the loop for a board with no effects on it.
- **An ability id is unique only within its card.** The compiler numbers them `t0`, `a0` from zero
  per card, which is enough everywhere else because a permanent's abilities come from one card. A
  mutated permanent's do not, and two cards in one stack collide on the first ability each has —
  and an ability goes on the stack as an *id*, resolved by looking that id up on the permanent's
  card, so the card underneath would have had its trigger resolved with the top card's effects.
  Component abilities are prefixed by their position in the stack.
- **A trigger on the card that just arrived was never considered.** `CollectTriggers` reads an
  object that exists on both sides of an event as it was *before* it, and a mutated permanent keeps
  its id (CR 730.2c) — so the mutating card's own "whenever this creature mutates" trigger, which
  is the trigger the whole cast was for, looked at a state where that card was still a spell on the
  stack. It is considered once, afterwards.

`Game.Move` is **not** the funnel for a permanent leaving the battlefield — every removal effect
builds its own `ObjectMoved` — so CR 730.3's "each of the individual components are put into the
appropriate zone" is done in `Emit`, which is the one path all of them come down. Without it the
stack becomes one card the moment it dies and every card ever mutated onto something is gone from
the game.

What it is worth: **25 cards**, measured before and after over the whole corpus, and nothing lost.
The remaining 13 of the 38 that mention mutate are blocked by their own bespoke effects, not by the
keyword — Nethroi's "total power 10 or less", Illuna's exile-until, Vadrok's free cast from a
graveyard, Brokkos casting itself from one, and Pollywog Symbiote's two lines that ask a *spell*
whether it has mutate, which nothing in the vocabulary can ask.

Two deviations, both written down rather than pretended away. **The over-or-under choice is made
with the cast** rather than on resolution (CR 702.140c), for the reason every other cast-time choice
is: a question asked mid-resolution is a continuation the log cannot rebuild. It commits the caster
earlier than printed and can never make the card better than printed. And **the board has no way to
offer it**: `CastOptionsDto` carries `Mutated`/`MutateOnTop` and `GameHub` forwards them, but
nothing in `mtg-client` sends them yet, so mutate is reachable through the hub and not through the
table.

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

### A reader can eat its neighbour and the coverage number still goes up

The standing advice for adding a reader was to measure coverage before and after, so that one
which claims a sentence and then refuses it — silently disabling the readers below — shows up as a
loss. **That advice is not sufficient, and the counter-example is measured.** A new reader was
placed before an existing one matching the same string, took **16 corpus lines** off its neighbour,
and coverage still rose, because it gained more than the neighbour lost. A net figure hides a
regression completely.

It was caught only because the probe carried the **neighbour's own wording as a control case**, so
the neighbour was being checked for READ on every run.

Two things are therefore required of anything that widens a pattern, not one:

- **Keep every neighbouring wording in the probe as a control.** If a new arm claims "no basic
  lands", the probe must still assert that "no lands" and "a basic land" read.
- **Diff the set of complete cards, not the count**, and check that the newly-incomplete set is
  empty rather than inferring it from the total.

`BoardConditions` alone has produced four of these: two patterns that had never once matched
(`you 're the monarch` wanted a space; the zone arm wanted "in **the the** battlefield"), and two
that matched, refused, and took the clause from every reader below — one of which cost six cards
before anyone noticed.

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
  (CR 601.2c), while `SpellDefinition.Targets` is a fixed list. ~~**`mana value X or less`**~~ —
  **now built** (round fourteen, above). The decline was right that a guessed reading is worse
  than none and wrong about the mechanism: `CastSpell` has the announced X in scope throughout
  and simply never handed it to a filter. What it needed was a fourth delegate on `TargetSpec`
  and a refusal where no value was announced.
- **`at random`** (36 cards) — randomness reaches an effect only through an event the `Game` handles.
- **`destroy it at end of combat`** (4 cards) — the delayed vocabulary defaults an unknown verb to
  *sacrifice*, and sacrificing is not destroying (CR 701.21a): regeneration and indestructible
  cannot touch it, so that reading is harsher than printed.
- **The initiative** (24 cards that take it, 8 that ask for it). CR 726.2 gives it three inherent
  triggered abilities, two of which venture into Undercity — of which this engine has zero lines.
  Modelling the designation alone would compile 24 cards that then skip most of what they say.
- ~~**Opening hand**~~ (20 cards) — **half of it is now built**, and the decline was right about
  what it would take: it needed a pre-game question, a choice kind, and a declaration on the
  compiled card, and it got all three (see round thirteen above). The battlefield half — CR 103.6a,
  "you may begin the game with it on the battlefield" — completes **6** cards. The reveal half
  (CR 103.6b) is still declined and for a better reason than before: it is a different action with
  a different payload, the card stays revealed until the first turn begins, and no corpus card
  printing it is otherwise readable, so offering it would be a question about nothing.

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

### Retired: "putting cards back in any order" was deferred, then built, and the note stayed

**This decline was stale, and the check that retired it took one probe.** It said the family was
deferred because reordering inside a zone would invalidate ids half way through. `LookAtTop
ThenArrange` was built at some point after it was written, and by round fourteen **thirteen** cards
printing the phrase already read - Sensei's Divining Top, Sage Owl, Index, Mirri's Guile, Halimar
Depths and the rest.

What kept the note looking true is that the best-known cards on it were still short, for an
unrelated reason: Ponder, Omen and Pondering Mage differ from Index by one trailing sentence, and
the idiom's pattern was anchored to the end of the line, so `You may shuffle.` threw the whole
match away. Unanchoring it and reading the tail the ordinary way took all three - the same fix the
look-and-take idiom beside it had already been given, and for the same reason.

The general lesson about these notes: **a decline records what was true when it was written, and
nothing tells you when it stops being true.** Several have now been retired by running the probe
rather than re-reading the paragraph.

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
- **"Any number of target creatures"** - **built since, and the note was wrong about why it was
  hard.** The decision it defends - no arbitrary ceiling - is right and is kept; what the note
  missed is that the engine had already been counting a target list for as long as "up to one
  target" had existed. See the section at the end of this file.
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

### The attached-permanent family is flat, and the gap in it was the conjunction

Measured before anything was built, because the family *looks* like a door. **1,234 Auras and 631
Equipment**, of which 652 and 369 were incomplete. The blocking lines do not cluster: the 540 Auras
blocked by exactly one line print **491 distinct shapes** between them, and the 288 Equipment print
**276** - 1.10 and 1.04 cards per shape. Three further cuts agreed. Ranking the *count* phrases
("for each ...") across the corpus gives 132 cards behind 103 distinct tails. Ranking the second
clause of every conjoined attached line gives 131 lines behind 40-odd shapes whose head is `has
ward {N}` at six. There is no head to attack.

The attached *subject* was not the gap either, and had not been for some time: "enchanted creature"
and "equipped creature" are already read as a subject by the buff, the base power and toughness,
the granted ability, the untap restriction, the block restrictions, the conditional statics, the
mana trigger and regeneration.

What was missing is **composition**. The attached static line was one closed regex whose tail was an
enumeration - `and has [keyword]`, `and can't attack or block`, `and attacks each combat if able` -
grown by hand three times. So a clause the compiler read perfectly well on its own could not be
joined to a bonus: `Enchanted creature doesn't untap during its controller's untap step` read,
`Enchanted creature can't be blocked by creatures with flying` read, and neither could follow
`gets +2/+2 and`. The mass-static sibling had learned to compose ("Creatures you control get +1/+1
and have flying" reads); the attached one had not. That is the same one-vocabulary-in-two-places
drift as the four-way split over what an Aura calls its host.

**A conjoined static line now folds.** The subject is lifted out, the tail is cut at its commas and
"and"s, and each clause is re-offered to the same static readers with the subject put back in front
of it. The vocabulary is therefore whatever the compiler already reads, and a clause added in future
joins the grammar with nobody coming back to the fold.

Three things make it safe rather than clever:

- **It runs last**, after every other matcher has refused the whole line, so it cannot take a clause
  off a neighbour. That failure has happened here before - a new reader took 16 corpus lines off the
  one below it and coverage still rose - and placement is the only fix that does not depend on
  noticing. The neighbouring wordings are asserted READ on every run as well, not inferred.
- **Every clause must read or the line stays unread.** An Aura that pumps and silently drops "and
  doesn't untap" is a strictly better card than the printed one, and unlike an unread card the
  legality gate would let it through.
- **The longest join is tried first**, so `has flying, first strike, trample, and haste` is offered
  whole before its commas are ever treated as joins.

One clause was worth building alongside it because the two multiply: **`is a [Subtype] in addition to
its other types`** (layer 4, CR 613.1d). Alone it completes almost nothing - every corpus line that
prints it on an attached subject is a conjunction. With the fold it is worth 15 more cards than the
fold alone - every corpus line that prints it on an attached subject is a conjunction, so on its
own it completes nothing at all. The capital letter decides which half of CR 205.3 the word is, as everywhere else here,
and a subtype is added *without* the card type it implies: "is a Knight in addition to its other
types" says nothing about card types (CR 205.1a), and adding Creature would animate whatever the
Equipment was on.

**+36 complete cards - 21 from the fold alone, 15 more from the two together - and the set diff
is a strict superset** - 36 gained, **0 lost**. Auras 582 ->
601, Equipment 262 -> 279. Nothing outside the two families moved, which is what the fold requiring
an attached subject predicts.

Declined here, with the count: **goad** (10 cards, and CR 701.39 needs a goaded-by record with a
duration), **granted ward** (13, and it was measured inert once already), **`loses [keyword]`** (94,
but only 7 on an attached subject and the rest are one-shots on targets), **`for each [kind] counter
on ~`** (30 mention it, 7 as a pump), **`is a black Zombie in addition to its other colors and
types`** (a colour the fold would drop), and **`for each opponent`** (37 cards, none of them here).

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

The check runs beside the target check now, not at the end of the cast. CR 601.2d comes before
601.2h, and an illegal announcement was getting as far as paying for the spell before it was
refused.

**None of that is about damage, and the machinery no longer is either.** CR 601.2d is written over
a spell or ability that "requires a player to divide or distribute an effect", and the second
family printed on cards is counters: *"distribute three +1/+1 counters among one, two, or three
target creatures"* is the same announcement, the same "at least one each", the same refusal to
redistribute a lost target's share. So the announcement check is keyed on an `IDividedEffect`
interface rather than on a verb, `DealDividedDamage` and `DistributeCounters` both implement it,
and one shared `Division.Shares` reads the announcement back out. A third divided verb costs a
record, not a second copy of the rule.

The total is an `Amount` rather than an `int` for the same reason — "deals X damage divided as you
choose" is a real card, and X is announced before the division is (CR 601.2b before 601.2d), so
the number is known by the time it is checked. An amount *counted off the board* is not, and is
refused at compile time rather than guessed at.

**An ability has nowhere to put the announcement, so the game stops and asks.** A spell carries its
division on the cast; a triggered ability is put on the stack by nobody and picks its targets from
questions of its own (CR 603.3d), and a hub method may not grow a parameter. `AskOwedDivision`
therefore sweeps the stack from the settle step — after the ability is on the stack, before
anybody has priority, which is the same moment the rules mean — and asks one pick per point, the
way combat damage division is already asked. The answer lands as a `DivisionAnnounced` event.
Without it the division would have been empty and every one of these abilities would have put no
counters on anything while compiling perfectly: **13 of the 28 cards this reading finished are
triggers or activated abilities**, so the question is half the work rather than a corner of it.

A **mode** may not divide. A division is announced as one list over every target the spell chose,
while a mode's effects index into that mode's own slice of them, so a divided effect inside a mode
would read the wrong end of the announcement. The compiler refuses it and the card is reported
unread. Abzan Charm is the only printed card that costs.

The hub carries the division too, so the feature is reachable over the wire rather than only from
a test. `CastOptionsDto.DamageDivision` and `TargetsChosen.DamageDivision` keep their printed names
because both are on a wire — clients send the first by property name, and the persisted event log
replays JSON written by earlier builds — while what they carry is a division of anything. The
state field they land in is `GameObject.Division`, named for what it is. The engine checks the
division against what the spell divides; the hub checks that the numbers are amounts at all,
because a **negative** share would let a division sum to the right total while healing its target —
the sort of thing a client can send and the rules never contemplate.

"One, two, or three targets" compiles to three specs of which only the first is required, which is
what the phrase says — and so does **"one, two, or three target creatures"**, which is the same
sentence with a noun on it and was refused for years by a pattern that allowed only the bare word
"targets". The noun is singularised (each side of an "and/or" on its own) and handed to the
ordinary target grammar, so every phrase that grammar already reads arrives working; a phrase it
does not read leaves the sentence unread rather than guessed at. "Up to N" is its own count phrase
because it means something else: every target is optional, the first one included.

A division whose ceiling cannot be known at compile time is still refused. "Any number of" takes
the quantity as its ceiling — every target must be assigned at least one, so three counters cannot
reach a fourth creature — and that only works for a printed number. **"X damage divided as you
choose among any number of target creatures" is left unread** — 7 sole blockers print it, of which
4 are spells the block machinery below could express. A divided effect's target count is fixed in
the definition and the expansion does not grow it, so reading it would compile a card that divided
across one target however many were chosen; the other 3 are triggers, which are refused a block
anyway for the reason given at the end of this file.

**"Any number of target ..." reads now**, and without a ceiling - the
objection this paragraph used to raise still stands and is what the shape is built to avoid. One
spec is marked as standing for a block, and the count arrives when the caster announces it. See
the section at the end of this file.

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
  **Hideaway now lands on it too, and so does the free half of the impulse family.** The keyword
  had compiled for a long time and could never pay out: `Hideaway N` buries a card and the *second*
  line every one of the twelve cards prints - "you may play the exiled card without paying its mana
  cost if `<condition>`" - was the one thing on them nothing read. It is the same offer, with two
  additions. The exiled card remembers which permanent buried it (`LookAndTake.LinksTakenToSource`
  writes `GameObject.ExiledBy`), because "the exiled card" means the one *this* permanent hid and
  two hideaway permanents can share a board; and the offer is read as permission to **play a land**
  as well as to cast, since the buried card is a land about as often as anything else and every
  printing of the line says "play". Three things are declines rather than gaps.
  **The verb is the test**: all twelve cards saying "you may *play* the exiled card" are hideaway
  cards and all fourteen saying "*cast*" mean a card some other sentence of their own exiled, with
  no link to read - so the wider verb is refused rather than compiled into an ability that finds
  nothing and silently does nothing. **A gate that cannot be read refuses the line**: Windbrisk
  Heights ("you attacked with three or more creatures this turn") and Spinerock Knoll ("an opponent
  was dealt 7 or more damage this turn") need counts the state keeps only as booleans, and a
  hideaway that pays out with its condition dropped is a strictly better card than the one printed.
  And **the "Word — " strip cannot tell an ability word from a gate**: `AbilityWord` removes any
  capitalised prefix as flavour (CR 207.2c), which is right for "Landfall — " and wrong for
  "Max speed — ", so `TryCastFromGraveyard` takes only a line that arrived without one.
- **"You may cast this card from your graveyard" is flashback's static ability written out in
  full**, and it lands in the same field: `AlternativeCastZone(Graveyard, the card's own cost,
  ExileOnResolve: false)`. What it needed that no keyword did is a **gate** - nine cards print the
  sentence and five of them hang it on the board ("as long as you control a Zombie"), so
  `AlternativeCastZone.Available` is asked at the moment of the cast rather than folded in at
  compile time, which is what "as long as" means. A condition `BoardConditions` cannot answer
  refuses the whole line: Gravecrawler with the gate dropped is not a card being read, it is a
  different card. The riders that charge something extra - "by discarding two cards in addition to
  paying its other costs", "if you pay {1} more for each other creature card in your graveyard" -
  are declined for the same reason, since the permission has no way to charge them and admitting
  them would hand out the zone for free.
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
  leave the defender compelled to block and refused for blocking alone. ~~The "this turn" wordings
  of both this and "can block an additional creature" stay unread - they are one-shot effects,
  and this static layer has nowhere to put a duration, so reading them would make them
  permanent.~~ **Both now read**, as floating effects rather than statics - see "The declines a
  duration retired" below. The static reader is unchanged and still refuses them, which is the
  control a behaviour test holds: "each combat" is on from the moment the permanent arrives and
  "this turn" is off until somebody pays for it.
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
- ~~`Apply` can count the board but `Applies` still cannot see the source's controller.~~ ~~A
  stolen *lord* still buffs its old controller's creatures.~~ Both fixed together in r10-scopes,
  and the loop was real. The `Applies` predicate had the source as a raw object and no ability
  source, so it could compute only its *target's* controller; reading `source.ControllerId`
  instead is where control *started* (CR 613.1b), which is the tenth instance of this file's
  recurring mistake. The signature was not widened — the computation's `IAbilitySource` now rides
  on the `CharacteristicsBuilder`, because `Applies` is constructed in over a hundred places and
  nearly none of them want another argument.

  **What the fix could not be** is the obvious one. Asking `Characteristics.Of(source)` from
  inside another permanent's computation is the CR 613.8 hazard exactly: with a lord on each side
  of the table, computing Alice's bear evaluates Bob's lord's filter, which computes Bob's lord,
  which evaluates Alice's lord's filter, which computes Alice's lord — unbounded. That is not a
  deduction; swapping `ControllerOf` for `Of` at the two call sites and running
  `A_stolen_lord_buffs_its_new_controllers_creatures_and_two_lords_do_not_loop` overflows the
  stack and aborts the test host, with `DependsOn → Matches → Of → ApplyLayers → InDependencyOrder
  → DependsOn` repeating down the trace.

  So control is asked of **layer 2 alone**. `Characteristics.ControllerOf` gathers only
  `EffectLayer.Control` candidates and applies those, which is the whole answer (CR 613.1b, and
  nothing after layer 2 changes control), and none of their predicates re-enters the layers. A
  thread-static guard makes a nested ask fall back to the stored controller rather than recurse,
  the same way CR 613.8b breaks a dependency loop by falling back rather than looping. The test
  above is the guard: two lords, opposite sides, then a theft — it asserts the four powers *and*
  that the computation terminates at all.
- **Landwalk reads computed land types, but nothing yet grants one.** The check is right; the
  template that would exercise it — "each land is a Swamp in addition to its other types" — does
  not compile, so that half is unverified and is not claimed by any test.
- ~~The phrase parser ignores square brackets rather than refusing them.~~ Fixed in the cleave
  round, in the same change that made it live: a line containing `[...]` that no cleave-aware
  reader claimed is now refused, with a test holding the exact sentence that used to read
  flier-less. See "Cleave, and the bracket the parser had been eating".
- **A cast had never charged a `ReturnToHand` cost.** Only ninjutsu produced one, and only as an
  activated ability, so the cast path had no arm for it and would have put the returned lands in
  the *graveyard*. Found by wiring alternative costs that are not mana; now covered by a Gush test.
- **A modal spell whose bullets all failed compiled into a menu with nothing on it.** The trigger
  side already guarded against this; the spell side did not, and it also hid a latent
  `Math.Clamp(0, 1, 0)` throw. Such a header now goes back to unread, which is why a round can
  gain fewer lines than it gains complete cards.
- **Displayed power and toughness ignore CR 613.** `ObjectView` carries printed values plus
  counters, so a creature under a lord reads at its printed size on the board while the engine
  fights with the right number. The engine is right; the board is lying.

### Level up, and the keywords a card is not supposed to have yet

A leveler is a Class with a different switch. Its bands are chosen by a count of level counters
rather than by a bought designation — CR 711.4 says the two do not interact, which is why the
progress is a real counter and not `PermanentState.Level` — and the lines under a band must not
function before the permanent has been levelled that far. That is the same requirement Classes,
Cases and Rooms already answer, so it gets the same answer: each band is compiled as a card of its
own and the abilities that come back are wrapped in a level test before they are merged, and every
matcher in the compiler is reused unchanged.

Three of the four line kinds were free that way. The fourth was not, and it is the one worth
writing down.

**A card database lists a card's keywords from its whole rules text, and a leveler's whole rules
text includes bands it has not reached.** Student of Warfare arrives from the corpus carrying
`First strike` *and* `Double strike` as printed flags, on a 1/1 that has neither until it has been
levelled twice. Nothing in the level machinery causes that — the flags are on the card before the
compiler reads a line — but reading the mechanic is what would have *shipped* it, because a card
the compiler cannot fully read is refused by the deck check and a card it can is not. Coverage
would have gone up by twenty-two while twenty-two cards became strictly better than printed.

So the keywords named inside bands are taken off in layer 6 and given back one band at a time, in a
single continuous effect rather than one per band: the two halves have to happen in that order and
layer 6 offers no ordering between two effects of the same permanent. A keyword named both inside a
band and outside one is kept, because the card has it at every level and the band is repeating it.
The size is set in layer 7b (CR 711.4), so a +1/+1 counter on a levelled creature counts on top of
the band's numbers rather than under them — a levelled 3/3 with a counter is a 4/4, and that is the
assertion that can tell 7b from 7c.

Two smaller decisions, both fail-closed. A sentence that means a keyword — `~ can't be blocked` on
Hada Spy Patrol — comes back from a section as a *granted* keyword rather than as an ability, and a
granted keyword has no gate of its own; it joins the band's keywords instead, because the Class
compiler's habit of merging only the four ability lists would have read that sentence and then
thrown it away. And a section that comes back carrying anything a level gate cannot wrap — a spell,
a cost modifier, one of the flat permissions — takes the whole card back to unread, since merging
it would let it function at every level and dropping it would lose it silently.

22 of the 25 levelers in the corpus are now read completely: 15,499 → 15,521, with the set of
complete cards diffed rather than the count, and no card lost. The three that remain are held back
by sentences that have nothing to do with levelling — protection from a card type and from
everything (Hexdrinker), a prevention shield of a fixed size (Hedron-Field Purists), and copying a
spell twice (Echo Mage) — and each of those now reports only those sentences, which is the honest
shape of the remaining work.

Three cards nearby are reachable *because* level counters now exist and were still declined, at one
card each: Champion's Drake ("as long as you control a creature with three or more level counters
on it"), Time of Heroes ("each creature you control with a level counter on it gets +2/+2") and
Venerated Teacher ("put two level counters on each creature you control with level up"). Each needs
a different piece of vocabulary — a board condition counting counters on *somebody else's*
permanent, a group anthem filtered by a counter, a group counter-placement filtered by an ability —
and none of them is shared with anything else.

### Measured and declined: mutate

34 cards, entirely unread, and the largest named mechanic left in the corpus. It stays that way, and
the measurement is the reason rather than the effort.

Every one of the 34 is blocked by two things: the `Mutate {cost}` line, and (on 33 of them) a
`Whenever this creature mutates, …` trigger whose effect the shared vocabulary can mostly already
run. Neither is the hard part. The hard part is that CR 702.140 makes a mutated permanent **one
permanent represented by a stack of cards** — it has the topmost card's characteristics plus every
ability of every card under it — and `GameObject` holds exactly one `CardDefinition`, which is also
the key everything uses to look an ability up.

What landing it correctly would take, listed rather than estimated:

- a permanent that holds an ordered list of cards, with the top one answering for characteristics
  and all of them answering for abilities — which is `Characteristics`, `Game.ActivatedAbilitiesOf`,
  the trigger sweep and the statics gather, all of which ask a card;
- a zone change that moves *every* card in the stack (CR 702.140e), and state-based actions that
  see one object where the graveyard will see several;
- a cast path that is not "resolve into a new permanent": mutate targets a non-Human creature you
  own, asks over-or-under as the spell is cast, and merges on resolution;
- "the number of times this has mutated", which four of the cards read as X and which has to
  survive on the permanent;
- new events for the merge, registered in both `GameReducer` and `EventLogSerializer`, and the new
  fields added to `Equals` or their updates are silently dropped;
- `PlayerViewProjector`, which has nowhere to put a permanent that is several cards.

That is a structural change to the object model, not a template. The half-built version — read the
mutate cost, read the triggers, and cast the card as an ordinary creature — is exactly the failure
this file exists to prevent: 33 cards would compile as complete, be let into decks, and play as
vanilla creatures whose printed trigger never fires.

### The digital-only family, measured — and the one door in it

The corpus is the whole Scryfall oracle set, so it carries the Alchemy and Arena-only cards, and
their mechanics were entirely unread — no matcher, effect or event in `MtgEngine.Rules` mentioned
`perpetually`, `conjure`, `seek`, `spellbook`, `intensity` or `specialize` at all. The family is
self-contained — nothing else in the corpus depends on it — which makes it the one place where a
misreading cannot regress a paper card.

**It is much larger than it looks and has almost no template in it.** Both halves of that are the
finding.

| mechanic | cards | complete before | would complete if this alone were read |
|---|---:|---:|---:|
| `perpetually` | 246 | 0 | 187 |
| `conjure` | 178 | 0 | 104 |
| `seek` | 107 | 0 | 74 |
| a card's `spellbook` | 64 | 0 | 39 |
| `double team` | 23 | 0 | 11 |
| `intensity` | 20 | 0 | 5 |
| `specialize` | 19 | 0 | 10 |
| **the union** | **541** | **0** | **393** |

The right-hand column is what makes the family look like a door, and it is the number to distrust:
it assumes every line carrying the word can be read, and those lines are not a template. Counted as
shapes with the numbers and mana symbols normalised out, `perpetually` is **245 lines with 244
distinct shapes**, `conjure` is **175 with 175**, and `seek` is **113 with 108**. Split further into
sentences — the unit the work queue ranks, because that is where the leverage lives — it does not
improve: 248 perpetual sentences in 225 shapes, 177 conjure sentences in 177. There is no head to
attack anywhere in it.

Which is why the honest yield here is one verb.

#### Seek: a tutor whose card the player does not get to choose

Seeking is search with the choice taken away and the shuffle removed, and that is the whole of it:
the same `SearchFilters` ids, the same mana-value bounds, the same destinations. So `Seek` reuses
the search's filter grammar rather than growing a second one, and differs in exactly the two places
where the mechanics differ.

- **The game picks, not the player.** Reading a seek as a search would hand its controller the pick
  of their library, which is a strictly better card than the printed one — the failure this file
  exists to prevent. The pick goes through the one seeded source, like a shuffle or a discard at
  random, and the log carries the moves that came out rather than the roll that chose them.
- **The library is not shuffled.** A search shuffles when it is done (CR 701.23e). Leaving the
  order alone is most of the reason the mechanic exists, and it is why this is its own effect rather
  than a flag on `SearchLibrary`: a flag governing two behaviours is one edit away from turning a
  real tutor random.

`SeekRequested` carries **no `Rule`**, and that is deliberate rather than an omission. Seeking is
digital-only and the Comprehensive Rules do not define it, so there is no paragraph to point at — and
a citation invented to fill the field would be worse than none, which this repository has already
learned four times over.

The reader is anchored at both ends, and that anchoring is what refuses the half of the family that
must stay unread: `seek a nonland card instead`, `seek a card with mana value equal to the number of
cards in your hand`, `seek three nonland cards, then nonland cards in your hand perpetually gain …`.
Each means something the effect cannot build, and each stops matching at the tail rather than being
read as the plain seek it is not.

**15,570 → 15,577 complete cards, diffed as a set rather than counted**: Audacious Knuckleblade,
Excogitator Sphinx, Routeway Moose, Skyshroud Lookout, Spirited Simulacrum, Sune's Intervention and
Worldweave in, and **nothing out**. Six behaviour tests play it, and three were checked by watching
them fail: taking the first match instead of a random one fails the seeded-variation test, shuffling
afterwards fails the order test, and dropping the mana-value ceiling fails the ceiling test. The
neighbouring search wording is played in the same test as a control, because a new reader placed
beside an old one can claim its line and refuse it while the coverage total still rises.

Seven cards is small, and it is worth saying what it is small *against*. The top row of the whole
corpus work queue is worth **seven cards** — the head of that ranking is now completely flat — and
`Specialize {N}` is one of the rows tied at the very top of it, on a mechanic that turns out to be
unbuildable at any price. Meanwhile no seek line appears anywhere near the top of that ranking,
because all 113 of them are spelled differently and each is worth one card. Both halves are the lesson this document has now recorded three times:
a ranking of line shapes cannot see a gap in the shared vocabulary, and it cannot see that its own
top row is impossible.

#### Declined here, with the measurement behind each

- **`perpetually`** (246 cards, 187 reachable in principle). Two independent reasons, either
  sufficient. It fights CR 400.7 head on: a perpetual effect is defined to survive a zone change,
  and this engine's identity model says an object that changes zones is a new object. Carrying it on
  the *card* the way suspend carries time counters is the shape that could work — but it would have
  to reach cards in a hand, a graveyard and a library, which is where most of these lines aim. And
  it would buy nothing without 244 distinct sentences behind it. Blurring the identity rule the
  whole event-sourced model rests on, for a mechanic with no template, is the worst trade available
  here.
- **`specialize`** (19 cards). It reads like the best row in the family — one template line, ten
  cards — and it cannot be built at all, for a reason that has nothing to do with the engine.
  Specializing turns the card into one of five printed versions of itself, and **all 85 of those
  versions are legal in no format**, so the corpus loader (which admits a card only if some format
  says legal or restricted) excludes every one of them. There is nothing in the playable corpus to
  specialize into. Compiling the line anyway would give nineteen cards a button that does nothing,
  which is the failure mode this file rates worse than an unread card.
- **`intensity`** (20 cards, 5 reachable). The same fight as perpetually and a harder one: intensity
  is a counter on the *card*, shared by **every card you own with that name**, in every zone —
  "cards you own named Chittering Skullspeaker intensify by 1". That is neither a permanent's counter
  nor a player's resource, and the five cards it would finish do not pay for a third kind of state.
- **A card's `spellbook`** (62 cards). Not a rules problem: **the bulk data has no field naming a
  spellbook's contents.** Every one of these lines draws a card from a list that does not exist
  anywhere in the corpus, so there is nothing to conjure or draft.
- **`conjure`** (178 cards, 104 reachable). Beyond the flat 175-shape tail, conjuring brings a named
  card in from outside the game (CR 400.11b) and `MtgEngine.Rules` has no way to find a card by
  name — `IAbilitySource` is keyed by the card it is handed. That is a new interface across the
  layer boundary in service of 175 distinct sentences.
- **`Activate only once`** (5 cards, the whole "Gate to …" cycle, each of them `{3}{C}, {T}: Seek a
  nonland card. Activate only once.`). Bare "once" is once per *game*, and
  `MaxActivationsPerTurn` can only say once per turn — reading it as the latter hands the card an
  activation every turn after the first. That refusal is already recorded above for the conjunction
  form; this is the same clause standing alone, and it is now the single largest blocker left in the
  seek family. Making it work needs a count that survives a reload, which means state rather than an
  engine field, and the failure if it is got wrong is silent and strictly generous.

One instrument note worth keeping. **433 of the 434 "A-" rebalanced Alchemy cards are in the playable
corpus** — they carry `alchemy`, `historic` and `timeless` legalities — while the 85 specialized
versions above are in none of it, and both facts come from the same one line of the corpus loader:
a card counts as playable if any format says legal or restricted. Every number on this page depends
on that line, so it is worth reading before quoting one.

### A dungeon is a rules object too, and its rooms are abilities

The Ring's entry above records the decision: a game object that no card carries has its wording in
the rules. A dungeon is the second of those and it is a larger one, because a dungeon is not an
emblem — it is a **card in the command zone that is not a permanent, cannot be cast, and never
leaves that zone except to leave the game** (CR 309.2c). The three venture dungeons are in
Scryfall's bulk data and legal in no format, so they never enter the corpus at all; Undercity is a
double-faced token and is filtered out before that. There is nothing for the compiler to read, and
`Dungeons.cs` is where the rooms live.

**The rooms are triggered abilities, and that is the whole design.** CR 309.4c: "the full text of
each room ability is 'When you move your venture marker into this room, [effect]'", and its source
is the dungeon card. The cheap implementation — keep the marker on the player and run the room's
effect inside the venture — is a shorter piece of code and a different game: nobody can respond,
and no room can target. Two of Lost Mine of Phandelver's seven rooms target. So the dungeon is a
real `GameObject` in `Zone.Command`, its rooms come back from `Game.TriggersWatching` the way a
granted ability does, and the ordinary trigger machinery does the rest.

Three details the rules insist on and the implementation keeps:

- **Completing is not entering the last room.** CR 309.6 removes the dungeon *as a state-based
  action*, once the marker is on the bottommost room **and no room ability of that dungeon is still
  on the stack** — and CR 309.7 says the player completes it as that happens. This is the Saga
  sacrifice's trap met a second time: the last room triggers on the marker arriving, so removing
  the dungeon then is removing it before its last room has done anything.
- **A fork is a choice.** CR 701.49b has the player choose which arrow to follow. A room with one
  arrow moves the marker inside the effect and asks nothing — a question with one answer stops the
  game and hands an opponent a free window.
- **What is completed stays on the player, not on the board.** Completing a dungeon is the moment
  its card *leaves*, so "as long as you've completed a dungeon" asked of the board would be false
  exactly when it has to be true.

**One dungeon ships.** Not a scoping compromise but a measurement: every one of the four has at
least one room the effect vocabulary cannot say, and Lost Mine of Phandelver is the only one where
that number was one rather than three.

| dungeon | the room that blocks it |
|---|---|
| Lost Mine of Phandelver | *(none — Fungi Cavern needed a duration, which was built)* |
| Undercity | *(none since the initiative round — Throne of the Dead Three's four welded instructions became the look-and-take with the taking dressed, and it ships)* |
| Dungeon of the Mad Mage | Mad Wizard's Lair ("draw three, cast one free" — and it is the bottommost room), Runestone Caverns ("you may play them", a play permission with no duration where both stored permissions expire). Twisted Caverns no longer blocks: defender is exactly "can't attack" (CR 702.3b) and the until-your-next-turn duration carries it |
| Tomb of Annihilation | two of five rooms (a re-measurement — Trapped Entry is a plain "each player loses 1 life") are "each player loses 2 life unless they …", an offer to every player at once whose decline falls on the decliner, which `MayPay` cannot say for more than one player |

A dungeon with a room that does nothing would be worse than a dungeon nobody owns, and it does not
cost the cards anything: **a player who brought one dungeon card is playing a legal game of Magic**,
and every "venture into the dungeon" card is correct for them. CR 701.49a's choice of *which*
dungeon has one answer here, which is the same game.

**Fungi Cavern is why "until your next turn" now exists.** A `FloatingEffect` knew one duration —
the turn it was made in — and there is no way to spell the other as that: "until your next turn"
runs through everyone else's turn and ends as that player's untap step begins. Storing a turn
*number* for it would be wrong the moment somebody takes an extra turn, so what is stored is the
player and the untap step does the comparing.

**And a live bug fell out of needing it.** A granted trigger's *targets* were looked up by card and
ability id with **no source id**, so `TargetsOfAbility` fell through every arm and came back empty —
the trigger went on the stack with nothing to target, resolved, and did nothing. Its *effects* were
found correctly, by the same lookup with the source passed. So the ability worked and looked
implemented, and only the half that chooses a target was blind. That is the third instance this file
records of *the same ability having to be found twice and only one of the lookups being right*, and
it reaches past dungeons: any Aura reading `enchanted creature has "when this dies, destroy target
creature"` had the same hole.

**30 cards, measured by set difference rather than by the coverage delta.** Complete cards went from
15,570 to 15,600 with nothing lost, and the thirty are the venture cards whose every line is now
read — Nadaar's neighbours: Veteran Dungeoneer, Dungeon Map, Triumphant Adventurer, Varis, Gloom
Stalker, Dungeon Crawler, Fifty Feet of Rope and the rest.

### Measured and declined: the initiative, Attractions, and dice

Three families were sized in the same pass and none of them is built. The numbers are the argument.

**The initiative (CR 726) — 26 cards touching, 13 reachable, declined for one room.** *(Taken the
round after — see "The initiative ships, and Undercity with it" below. The decline was correct when
it was made and its reasoning is why the next round was cheap.)* Not for want
of machinery: it is the monarch's twin. One designation at most one player holds, moving on combat
damage, with an inherent upkeep trigger — and the monarch is already built exactly that way, down to
the stated simplification about sourceless triggers. What stops it is that **all three of CR 726.2's
inherent abilities venture into Undercity by name**, and Undercity's bottommost room is the one
listed above. An initiative that sent players into a dungeon which stops working at its last room is
a mechanic that reads as implemented and is not. `When ~ enters, you take the initiative.` is
19 cards and is now the single largest sole-blocker in this corner; it becomes cheap the day Throne
of the Dead Three can be said. `venture into Undercity` is deliberately left unread rather than
folded into `venture into the dungeon`, and there is a test holding that line: a pattern loose enough
to admit it would send nineteen cards into a different dungeon with different rooms.

**Attractions and stickers — 46 and 96 cards touching, declined.** These are the worst-read subtypes
in the corpus (Attraction 22 of 22 unread, Guest 21 of 21) and that ranking is what put them at the
top of the queue. Measuring what they need is what took them off it:

- **An Attraction deck is a second deck**, opened from rather than drawn from (CR 701.51), which is
  a deck-construction change reaching `CardPool`, the deck gate and the client, not a rules change.
- **Which Attractions a roll visits is decided by the lights printed on the card** (CR 701.52a), and
  the lights are not in the rules text at all — they are a Scryfall field (`attraction_lights`) that
  `CardDefinition` does not have and the corpus reader does not read. Without them "roll to visit
  your Attractions" cannot say which Attractions were visited, and inventing lights would print
  a different card.
- **Stickers are a physical sheet**, and the Guest cards are mostly stickers rather than
  Attractions: `_____ Bird Gets the Worm` gains life equal to *the number of unique vowels on the
  name sticker*, and `Clandestine Chameleon` has *all abilities of ability stickers on other
  permanents you own*. Neither the sheet nor its contents exist anywhere in the data.
- Individual Attractions then want phasing (Ferris Wheel), horsemanship (Merry-Go-Round) and
  "claim the prize" (Pick-a-Beeble) on top.

This file already records the size of the supplemental formats: 75 of 32,765 playable cards are
format-specific, 0.2% of the corpus. Against that, a second deck, a card characteristic threaded
through `Domain` and the corpus reader, a sticker sheet and a dice subsystem is the largest
machinery-to-cards ratio anything in this document has proposed. **The alternative that was built
instead — dungeons — cost one new file, one duration and no new deck, and turned over the same
number of cards.**

**Dice rolling (CR 706) — 142 cards touching, 119 reachable, deferred rather than declined.**
*(Taken two rounds later — see "A results table is one ability printed across four lines" below.
The deferral was right; the reachable figure was not. 29 cards, not 119: the queue counted every
card whose only unread line mentioned a die, and most of those lines are unread for a second
reason printed inside the same sentence.)* It is
genuinely large and it is genuinely ordinary paper Magic: 113 corpus cards roll dice outside
Un-sets, mostly the AFR d20 with a results table. The randomness has a settled precedent here —
`FlipCoin` records the *outcome* in the log so a replay reproduces it — so this is a normal piece of
work rather than a structural one. It is not built because **nothing in the dungeon family needs
it**: not one of the 60 dungeon cards or the 26 initiative cards rolls anything, so pairing them
would have been two mechanics in one round for no shared machinery. The d20 cards' real blocker is
not the roll but the **results table** — `1—9 |`, `10—19 |`, `20 |` are separate lines the line
splitter hands to the compiler alone, and reading them means reading a line in the context of the
one above it.

### Curses were not blocked by their subject; the player being attacked was

Curses read 2 of 39, the worst-read subtype in the corpus at twenty cards or more, and the
hypothesis going in was the obvious one: a Curse is an Aura with `enchant player`, the family
shares the subject "enchanted player", and one subject added to the vocabulary would reach all of
them. **That was wrong, and measuring it first is what stopped a grammar being built for it.**

The subject was already there. `enchant player` attaches to a player, `AttachedToPlayer` records
it, "at the beginning of enchanted player's upkeep" reads, and "that player" resolves off the step
because a step trigger is about whoever's step it is — which is why the two Curses that compiled
compiled at all. Swapping the subject out of each unread line and putting a known-good one in its
place is what showed it: "creatures **your opponents control** get -1/-1" reads and "creatures
**enchanted player controls** get -1/-1" does not, but "whenever a creature attacks **you**" does
not read either, so the subject was never what stopped that half of the family.

What actually blocks the 37, counted rather than estimated: 11 need a combat trigger that names who
is being attacked, 7 need one bespoke upkeep effect each (exile from a graveyard, reveal-until,
sacrifice-of-their-choice, an unless-clause with two ways to pay), 7 need a group of permanents
defined by the enchanted player as a *static*, 5 need "each opponent attacking that player does the
same", and the rest are one-offs — a damage doubler, a spell-count restriction, a transform that
attaches, a search for a Curse by name. It is a pile of unrelated effects wearing one subtype, the
way Sagas and planeswalkers turned out to be.

So the work moved to the largest genuine completion adjacent to it, which the same measurement
named: **who is being attacked.** An attack is declared against a particular player or a
planeswalker they control (CR 508.1b) and the declaration has carried that all along; only the
sentence had nowhere to say it, so "whenever a creature attacks" was read and "whenever a creature
attacks you" was not — 39 cards carry that shape and 28 of them are one line short.

Four pieces, all of them one relation added to a vocabulary rather than a matcher:

- **The defender on the attack and combat-damage verbs.** "Attacks you", "attacks you or a
  planeswalker you control", "attacks enchanted player", "deals combat damage to you / to enchanted
  player". "Attacks you" is the player and *not* the planeswalker — a creature attacking a
  planeswalker is not attacking its controller, which is why eight corpus cards print the longer
  phrase and why the two are separate readings here.
- **"Enchanted player" in the shared player word list**, so every verb that takes a player takes it:
  mills, loses life, draws, discards. Fraying Sanity and Volrath's Motion Sensor print it that way.
- **"Its controller" / "that creature's controller" in the same list**, which is what most of the
  attack family does with the trigger it just gained. This one needs a guard and the guard is the
  interesting part: with a target in the ability the words mean the *target's* controller, and
  matchers with their own grammar for that have been reading them correctly for months. Refusing
  the phrase outright took 98 cards away from those matchers. It is now *rewritten* into a word no
  card prints, and only when nothing was targeted and the trigger is one whose event carries an
  object — so the shared vocabulary sees it exactly where it means the subject, and every older
  matcher sees the printed words untouched everywhere else.
- **"A creature enchanted player controls" as the possessive side** of the trigger subject grammar,
  beside "you control" and "an opponent controls". Trespasser's Curse is the printed card.

Two things the family exposed on the way through, both of them cards that compiled complete and did
nothing:

- **The pump verb was missing the middle answer of the pronoun order.** "It" means the target the
  sentence before chose, then the object the trigger was about, then the permanent with the
  ability — and this verb went straight from the first to the third. Briar Patch's "whenever a
  creature attacks you, it gets -1/-0 until end of turn" therefore shrank the enchantment, which is
  not a creature.
- **The two pronoun allow-lists disagreed about untapping.** CR 502.2 untaps everything at once,
  `PermanentsUntapped` carries a set of ids, and `SubjectObjectOf` answers nothing for it — which
  the attached-permanent list says and the zone-change list did not, so a pronoun in "whenever a
  permanent becomes untapped" was admitted with nothing to resolve to. Corrected to agree.

**15,570 → 15,595 complete cards, none lost. Curses 2/39 → 6/39** (Curse of Predation, Curse of
Stalked Prey, Curse of the Forsaken, Trespasser's Curse), and the other 21 are the attack family
and the trigger-subject controller: Blood Reckoning, Hissing Miasma, Marchesa's Decree, Revenge of
Ravens, Riddlekeeper, MacCready, Isperia, Slumbering Dragon, Thantis, Search the Premises, Briar
Patch, Bereavement, Kavu Lair, Poisonbelly Ogre, Fate Foretold, Flayed Nim, Ragged Veins, Chronic
Flooding, Corrupted Roots, Pooling Venom, Pattern of Rebirth.

Declined here, with the count behind each:

- **"Creatures enchanted player controls" as a static group** — 7 cards, including Curse of Death's
  Hold, which needs nothing else. The group filter is handed a state, an ability source, a
  permanent and a controller id, and never the source *object*, so it cannot ask what the source is
  attached to. Widening that signature reaches every group filter in the compiler for one relation
  used by seven cards; the trigger side of the same relation was free because its predicate already
  has the source.
- **"Each opponent attacking that player does the same"** — 5 Curses. It repeats the preceding
  effect once per attacking opponent with that opponent as "you", which is an effect that wraps
  another effect and re-scopes it. Buildable, and worth doing beside the "is attacked" trigger it
  always appears with; not worth either alone.
- **"Whenever enchanted player is attacked"** — 6 cards, 5 of which are the line above. Left with
  it.
- **"One or more creatures deal combat damage to you"** — refused rather than approximated.
  `CombatDamageDealt` records who dealt the damage and not who took it, so the recipient cannot be
  checked, and a trigger that fired for damage dealt to anybody is a strictly better card than the
  one printed. The recipient is admitted on the singular sentence only, and there is a test holding
  the refusal.


### The initiative ships, and Undercity with it

The decline the round before named its own price of admission: all three of CR 726.2's inherent
abilities venture into Undercity by name, and Undercity's bottommost room - reveal ten, put a
creature from among them onto the battlefield with three +1/+1 counters, it gains hexproof until
your next turn, then shuffle - could not be said. **Every piece of that sentence existed within a
round.** The until-your-next-turn duration was built for Fungi Cavern; the reveal, the counters,
the grant and the shuffle are one look-and-take with the taking dressed. The dressing rides the
request rather than becoming separate effects, for the reason unearth is one effect: only the move
knows the id the taken card lands under (CR 400.7), so a second effect running afterwards could
not name the thing that just arrived.

So Undercity is the second dungeon in `Dungeons.cs`, nine rooms of existing vocabulary plus that
one, and the initiative sits on top exactly as the monarch does: one nullable field on the state
(CR 726.3), an upkeep hook beside the suspend tick, and a combat-damage hook beside the crown's -
in the one place every `PlayerDamaged` passes, because the crown's first version taught what
happens when the hook watches a door combat does not use. Two behaviours are the initiative's own
rather than the monarch's, and each has a test that plays it:

- **Taking it while holding it ventures again** (CR 726.5). `TakeTheInitiative` never
  short-circuits the way `BecomeTheMonarch` does; the designation re-assigns harmlessly and the
  venture happens every time.
- **"One or more creatures" is one trigger** (CR 726.2). The batching costs nothing: the first
  creature's damage moves the designation, after which the holder is no longer the player being
  damaged and the rest of the batch fails the guard. Two unblocked attackers, one venture - the
  test asserts the marker sits on the topmost room, because a double-take would have walked past it.

**"Venture into Undercity" reads now** (CR 701.49d), as one alternation with the plain venture so
no looser pattern can ever send a named venture into the wrong dungeon, and the test that held the
line unread flipped to assert both halves: Undercity's name reads, any other name still refuses the
card. The variant differs from the plain instruction only in what it *starts* - a player already in
any dungeon advances it, which the "taking the initiative mid-Lost-Mine" test plays out. And the
plain venture still has exactly one answer with two dungeons shipped, because the restriction is
printed on the card: "You can't enter this dungeon unless you 'venture into Undercity.'"
CR 701.49a's choice never offers it, which is the rule as written rather than a simplification.

**15,899 -> 15,908 complete cards, +9, none lost, measured by set difference**: Aarakocra Sneak,
Avenging Hunter, Bloodboil Sorcerer, Feywild Caretaker, Goliath Paladin, Passageway Seer, Stirring
Bard, Underdark Explorer, Undermountain Adventurer. The 19-card estimate for `When ~ enters, you
take the initiative.` was the line's frequency, not its sole-blocker count: the enters line now
reads on every card that prints it, and the other ten are each one *different* line short -
attack-and-blocked triggers, "draw another card if" tails, a free-cast offer - none of them
initiative-shaped. `you have the initiative` joined the board conditions (the "you" arm only;
no corpus card prints the opponent arm as a bare condition, and an arm no card exercises is an arm
no test keeps honest), which is what Passageway Seer's and Feywild Caretaker's end-step triggers
needed beside the enters line.

**Checked against the vocabulary and still declined, with the stale notes corrected:**

- **Dungeon of the Mad Mage.** The recorded blocker list was half stale: Twisted Caverns
  ("target creature can't attack until your next turn") stopped blocking the day the duration
  was built, because defender *is* "can't attack" (CR 702.3b) and a floating grant carries it.
  What actually blocks is **Mad Wizard's Lair** - "draw three cards and reveal them, you may cast
  one of them without paying its mana cost" needs a reference to the cards a draw just drew,
  which nothing has - and it is the *bottommost* room, the exact place a dungeon must not stop
  working. **Runestone Caverns** is the subtler one: "exile the top two cards of your library,
  you may play them" grants a permission with no duration, and both permissions the engine stores
  expire (`MayPlayUntilTurn`, `MayPlayThroughOwnersNextTurn`) - reading it as either would be
  a room that quietly takes the cards back.
- **Tomb of Annihilation.** Two of five rooms, not the recorded three - Trapped Entry is a plain
  "each player loses 1 life", sayable today. Veils of Fear and Sandfall Cell are "each player
  loses 2 life unless they [discard / sacrifice]": an offer put to every player at once, with the
  decline consequence falling on the decliner. `MayPay` asks exactly one player and resolves its
  branches as the room controller's, so each opponent's decline would drain the venturing player.
  Oubliette's mandatory discard-and-sacrifices became sayable while nobody was looking
  (`ChooseAndMove` asks the owner of the board it picks from), and Cradle of the Death God is a
  token - the family is down to one missing shape used twice.

### Cleave, and the bracket the parser had been eating

Cleave (CR 702.148) waited behind a defect that had to land first. The phrase parser *ignored*
square brackets rather than refusing them: "Draw a card for each creature you control [with
flying]." compiled as the flier-less draw -- a card read strictly better than printed, the one
class of error the fail-closed rule exists to prevent. Nothing playable was affected only because
every bracketed line sat on a card that was incomplete for other reasons, and that was luck: the
moment cleave read, it went live. So the fix is the guard, not the mechanic: a line containing a
bracket that no cleave-aware reader claimed lands in `Unhandled` before any matcher can see it,
and a test holds the exact sentence. The corpus's only other brackets -- loyalty costs inside
granted-ability quotes ('has "[+1]: ..."') and Comet's dice lines -- were unread anyway, so the
refusal cost nothing, which the set diff proved rather than assumed.

Cleave itself is the adventurer answer a third time: one card carrying two spells, chosen as it
is cast. The text is compiled **twice** -- brackets dropped and the words kept, bracketed words
gone -- each reading through every matcher unchanged, so the vocabulary work other rounds landed
is what made 7 of the 12 readable both ways (an earlier agent measured 3; re-measuring before
building is the habit that found the difference). The printed reading is the card;
`CompiledCard.CleaveSpell` and `CleaveCostRaw` hold the other, `Game.CastSpell(cleaved: true)`
charges the alternative cost and swaps the reading in through the same `CastAs` record adventures
use, and a `SpellCleaved` event folds `WasCleaved` onto the stack object -- so `SpellBeingCast`
can answer from the state and a *resumed* game still resolves the reading that was paid for,
which the in-process table alone could not promise (adventures and split halves still cannot;
that gap stands recorded). The behaviour tests turn on the readings answering differently: one
flier among two creatures draws one card printed and two cleaved, and "Destroy target [attacking]
creature" refuses an idle creature printed and kills it cleaved -- the target list is part of the
reading, not just the effects.

Fail-closed carries through the pair: a card either of whose readings has an unreadable sentence
stays unread whole, reporting only the real blocker, and a cleaved reading that compiled to
anything besides a single spell is refused because the swap carries a spell and nothing else.
The five that stay incomplete, each with its honest sentence: Lantern Flare (a standalone "X is
the number of creatures you control" definition), Alchemist's Gambit (an extra turn with a
prevention rider and a delayed loss), Inspired Idea (a lasting hand-size reduction), Wash Away
(a cast-zone target filter), Dread Fugue (a mana-value filter after "from it").

### Gift: a promise is a cast fact with a name on it

Gift (CR 702.174) is kicker's family with one addition -- the fact has a *player* in it.
Promising is choosing an opponent (702.174a), so `GiftPromised(stackId, opponent)` is one event,
the reducer folds it to `GameObject.GiftedTo`, and the field rides the resolution move exactly as
the kicker flag does (CR 607.2), because a permanent's trigger asks about the spell it used to
be. Delivery is synthesized from CR 702.174d-j's own sentences ("Create a Food token.") and then
re-aimed at a new `PlayerScope.GiftRecipient` -- "the chosen player" is deliberately not taught
to the shared grammar, since no printed rules text says it, and the re-aim refuses any effect
shape other than the draw and the token creation the six defined kinds produce.

The branch went two ways, by card type. An instant or sorcery is the cleave shape again: the
promise is settled at cast, so "If the gift was promised, ..." is not a runtime conditional at
all -- the text is rewritten into an unpromised and a promised reading (the "instead" sentences
swap an instruction, targets included, which is what CR 702.174m asks; the delivery is the
promised reading's first effect, which is 702.174j), and each compiles as a card of its own. A
permanent cannot do that, because its own printed trigger reads the promise *later*, off the
permanent -- so "the gift was promised" joined `BoardConditions`, where the intervening-if and
the bare-conditional sentence readers pick it up unchanged, and the gift line itself compiles to
the enters trigger 702.174b spells out. The multiplayer half is behaviour-tested at a table of
three: the Food goes to the opponent named at cast and to nobody else, promising yourself
refuses, and promising off a card with no gift refuses.

10 of the 27 gift cards read completely: Mind Spiral, Pool Resources, Valley Rally, Into the
Flood Maw, Peerless Recycling, Sazacap's Brew, Nocturnal Hunger, Long River's Pull, and the two
permanents Scrapshooter and Kitnap. The other 17 are blocked by their own sentences, not by the
mechanic -- doubled damage numbers in one clause, "up to N ... each with mana value" reanimation,
"put into your graveyard this way", phase-out-and-protection, a copy with an exception -- and two
kinds were declined by measurement: "Gift an extra turn" (one card, Perch Protection, blocked by
its phasing sentence regardless of the delivery) and "Gift a Rhystic Study" (one card; the kind
is not defined by CR 702.174 at all, and inventing a delivery for it would be guessing).

### The aftermath flag never fired on cardboard, and neither did fuse

The audit that said `CardHalf.HasAftermath` was reachable by zero cards was pointing at a missing
reader, not a stale record. The flag was matched with `^Aftermath$` against the face's *raw*
text, and no printing says the bare word -- the cardboard says "Aftermath (Cast this spell only
from your graveyard. Then exile it.)", so the flag set on zero corpus cards while the keyword
*line* read fine through `Lines`' reminder stripping. Thirteen split cards were complete,
flagless, and quietly castable from hand twice -- the exact strictly-better failure the aftermath
test narrates, live in the shipping compiler, invisible because the test's fixture printed the
bare word no card prints. `HasFuse` had the same bug on the same pattern, which cost all 22 fuse
cards their fused cast. Both now strip reminder text before matching; all 27 aftermath cards flag
their half, and a corpus-shaped fixture -- reminder text and all -- holds the from-hand refusal
so the fixture blindspot cannot reopen.

**15,899 → 15,916 complete cards, the set diffed and none lost:** 7 cleave (Winged Portent,
Fierce Retribution, Alchemist's Retrieval, Dig Up, Lunar Rejection, Path of Peril, Parasitic
Grasp) and 10 gift, as named above.

### Soulbond, and the negative control that was standing on a gap

Soulbond was declined in an earlier round because the paired status on its own is coverage for a
fact no card can observe. It ships now because both halves are here: the status (CR 702.95a-e)
and the payoff (CR 702.95b), which every one of the 24 printings spells `As long as ~ is paired
with another creature, ...` -- a conditional static, which is the mechanism 32 other cards
already reach through `ContinuousEffectDefinition`.

The status is a designation held on both creatures, each pointing at the other, and read only
where the two agree. That redundancy is what lets `GameState.PairedPartnerOf` be shallow enough
to call from inside layer 6: it reads the stored pairing and the partner's zone and nothing
computed, because computing the partner's characteristics while computing your own is re-entrant.
The questions the shallow reader cannot ask -- is either half still a creature, do they still
share a controller -- are asked from outside, by a sweep beside the state-based actions, which
severs the pairing for good (CR 702.95e: a creature that qualifies again later has still stopped
being paired, and only a new soulbond trigger pairs it again). Pairing itself is a *choice*, so
each of the two entry triggers resolves into one question answered by naming a creature or
declining, exactly as exploit's does; the intervening if is on the trigger predicate and CR
702.95c's re-check happens where the question is asked.

**The regression that parked this branch was not soulbond.** The round-ten record above says it
"breaks a self-pump keyword grant", and it does, through one line that has nothing to do with
pairing: the branch added `["phasing"] = KeywordAbility.Phasing` to `EffectPhrase`'s grantable
vocabulary so that "Enchanted permanent has phasing" could be read at all. `A_self_pump_grants_
its_keyword_in_the_same_sentence` proves the whole-or-nothing rule -- a sentence whose grant half
is unreadable must leave the *whole* line unread, size included -- and it proved it with a card
saying "gains phasing", which was refused only because that word was missing from the table.
Deleting the one dictionary line makes the control pass and Teferi's Curse unreadable; restoring
it does the reverse. The two tests were arguing over a gap, not over the reader, and the reader
was never touched.

So the control was repointed rather than the vocabulary reverted, because the vocabulary
addition is real: the untap step already read the *computed* keyword and already took everything
attached along with its host, so a granted phasing plays in full, and a game is now played that
phases an enchanted creature out on one untap step and back on the next. What the control names
now is protection from a creature type -- the table holds one flag per colour and the ability
takes a quality, so no dictionary line can carry it, and Diregraf Escort is short on exactly that
sentence and no other. A negative control anchored to a missing word re-arms every time the word
gets written down, and that table has twice been extended by diffing it against the enum. The
`Keywords` remark was corrected in the same change; it still called fear, intimidate, shadow,
skulk and changeling omissions long after all five had been added, which is the reading that made
a gap look like a boundary in the first place.

One arm of CR 702.95e had a sweep and no game behind it. Leaving the battlefield and changing
controller are both events with tests; ceasing to be a creature is a computed characteristic that
simply stops being true, and nothing played it. A land animated until end of turn is a legal
partner while it is animated -- 702.95a asks about creatures, not about cards -- and the test now
pairs one, watches the bond come apart at cleanup, and animates it again to confirm the pairing
does not come back with it. Removing the `IsCreature` test from the sweep fails it.

Declined, and why, out of 27 cards that mention the pairing: Diregraf Escort (protection from a
creature type), Doom Weaver and Imperious Mindbreaker (their quoted grants count "cards equal to
its power/toughness"), Breathkeeper Seraph (a delayed return), Mirage Phalanx (a token copy with
exceptions) and Donna Noble (a trigger watching damage to either half). All six are short on
that line alone -- the `Soulbond` line itself reads on every card that prints it, which the shelf
test asserts card by card.

**15,935 -> 15,958 complete cards, the set diffed and none lost:** 21 soulbond (Wingcrafter,
Nearheath Pilgrim, Spectral Gateguards, Hanweir Lancer, Lightning Mauler, Geist Trappers, Elgaud
Shieldmate, Pathbreaker Wurm, Nightshade Peddler, Silverblade Paladin, Trusted Forcemage, Druid's
Familiar, Wolfir Silverheart, Stonewright, Galvanic Alchemist, Stern Mentor, Thundering
Mightmare, Tandem Lookout, Deadeye Navigator, Flowering Lumberknot, Joint Assault) and the 2 the
phasing word unlocked (Teferi's Curse, Cloak of Invisibility).

### A results table is one ability printed across four lines

`Roll a d20.` and the rows under it are one ability (CR 706.3b) and four lines of text, and the
compile loop reads one line at a time — a row alone is half a sentence, and `1—9 | Scry 1.` says
nothing without the line above it. So `Lines` folds a row into the line that called for the roll
before the compiler ever sees either, which is the shape `CompileClass` and `CompileLeveler`
already use for the same problem. The fold is one-way and gated on the line above: a Spacecraft's
station bar is `10+ | Flying`, character for character a results row, and it folds into nothing
because the keyword above it rolls no dice. That guard is worth an ability — the station threshold
reads that bar (CR 721.2), so a fold that swallowed it would leave a Spacecraft that never becomes
a creature and no count anywhere would have gone down.

The randomness follows `FlipCoin` exactly: `RollDice` cannot reach the seeded source, so it emits
`DiceRollRequested`, the game settles it against `_random`, and `DiceRolled` records the number. The
log holds the *outcome*, so a replay reads what came up instead of rolling again — the assertion
that a game resumed on a different seed equals the game that produced the log is what holds it.
`Natural` is recorded beside `Result` although no modifier machinery exists to separate them yet,
because "a die's highest natural result" has to keep reading the face on the day one does.

**Three bugs, and all three were silent.** The first refused cards outright: `EffectPhrase.TryParse`
rejects a phrase that produces no effect — correctly, since a line that only chooses is not a line —
and "Choose target creature, then roll a d20" is exactly that phrase, with the doing printed in the
rows underneath. The preamble is now read sentence by sentence into the line's own builders instead,
which also fixes the arithmetic beside it: the count a row was checked against was taken *before* the
preamble rather than after, so every row saying "that creature" looked like a row choosing a target
of its own, and the all-or-nothing rule threw away the whole table.

The second and third bugs were worse, because the card compiled and the game ran. `DiceRollRequested`
carried no targets, and the settle read them off the source it names — which is the *physical* source,
the permanent whose ability rolled, and a permanent carries no targets because the ability on the
stack did. Spiked Pit Trap rolled its d20, ran the row the number landed in, created the Treasure that
row prints, and dealt its five damage to nobody; nothing in the log said anything had gone wrong. That
is the third time this exact mistake has been made here, and `OptionalPaymentRequested.Targets`
already carries the note about the previous two, so the roll request now carries them the same way.
Then, with the damage landing, the creature it killed went on standing there: `SettleOwedRoll`
returned from the settle sweep as though a question were pending, so priority was handed back with
state-based actions unchecked and five damage plainly marked on a 4/4. A roll asks nobody anything —
CR 706.2b's extra die is a replacement, applied on the way in — so it now continues the sweep the way
a seek does.

**15,935 → 15,964 complete cards, the set diffed and none lost.** The 29: Ancient Copper Dragon,
Arcane Investigator, Atomwheel Acrobats, Barbarian Class, Boing!, Brazen Dwarf, Contact Other Plane,
Dissatisfied Customer, Djinni Windseer, Farideh's Fireball, Farideh Devil's Chosen, Feywild Trickster,
Goblin Morningstar, Herald of Hadar, Hoarding Ogre, Lightfoot Rogue, Monoxa Midway Manager, Netherese
Puzzle-Ward, Non-Human Cannonball, Nothic, Overwhelming Encounter, Pixie Guide, Recruitment Drive,
Scion of Stygia, Shackle Slinger, Spiked Pit Trap, Sylvan Shepherd, Vegetation Abomination, and Wyll
Blade of Frontiers.

**Twenty-nine, against a work queue that promised a hundred and nineteen.** The gap is the queue's
own blind spot rather than a shortfall here, and it is worth writing down because the queue is how
every round of this work picks its next target. It ranks cards whose *sole* unread line mentions a
mechanic — but a single line of a dice card is a whole results table, and a table is unread the
moment any one thing inside it is. 105 corpus cards roll dice; 28 read completely; 66 are one line
short, and every one of those 66 blockers is distinct. Sorted by why:

- **27 cards** — the roll reads and something in a row does not: "Each player sacrifices a permanent
  of their choice", "Copy that spell", "Gain control of it until the end of your next turn". Ordinary
  effect vocabulary, reachable by ordinary vocabulary work, and nothing to do with dice.
- **24 cards** — the result is spelled a way the row rewrite does not cover. Measured and declined:
  the biggest single spelling, "…, where X is the result", is 17 cards, and the X-definition wrapper
  already in `TryOne` would take it in about twenty lines. It was built and then reverted, because
  it turned over **zero** cards: Growth Spurt, Painiac and Ground Pounder are legal in no format and
  are not in the corpus at all, and every legal card printing the clause carries a second blocker in
  the same sentence — "Monstrosity X", "become an X/X Insect", "up to X target creatures", "the
  result minus 1". A compiler path no card reaches is untested surface, so it is not there.
- **14 cards** — multi-dice rolls: "roll two d6 and choose one result", "roll a d20 for each player
  being attacked and ignore all but the highest". The engine deciding which result to keep takes the
  choice off the player, and there is a test holding that line.
- **9 cards** — the unread line is not a roll line at all.
- **7 cards** — modified rolls: "roll a d20 and add the number of cards in your hand". A modified
  roll read as unmodified lands in the wrong row of its own table, which prints a different card.
- **2 cards** — the planar die, whose faces are symbols rather than numbers (CR 901.4).
- **1 card** — Critical Hit's "when you roll a natural 20", watched from the graveyard, where a
  trigger compiled to the battlefield would read cleanly and never fire.

The one piece of dice machinery here with no results table under it is Pixie Guide's replacement
(CR 706.2b): the extra die is added to the request on the way into the log, the settle keeps the
highest, and CR 706.6's ignored rolls never happened — so one number goes in the log however many
dice went in, and the watchers see one roll.

### The declines a duration retired, and the ones it did not

This file has now recorded the same shape of mistake three times: **a line is declined for want of
a mechanism, the mechanism arrives for some other reason, and nobody goes back.** The initiative
was blocked on an unsayable room that became sayable the moment a different dungeon needed the
same duration. Goad's "measured and declined" note went stale the same way. Both were found by
somebody re-reading a note rather than by anything in the build.

So this round is the sweep rather than the reader: every recorded decline whose stated reason was
a missing duration or a one-shot-against-permanent problem, re-measured against the code as it
now stands. **Three durations existed that did not when those notes were written** —
`PumpUntilEndOfTurn.ForTheTurn` (a change that never wears off), `FloatingEffect.UntilTurnOf`
("until your next turn", CR 611.2b), and `ContinuousEffectDefinition.While` ("for as long as …").
The list below is the deliverable, not the code: the next round should not have to derive it
again.

**15,935 → 15,970 complete cards, +35, none lost, measured by set difference.** Five declines
taken, and the numbers on four of the five were the ones the notes had.

| decline, as recorded | verdict | measured |
|---|---|---|
| `{cost}: ~ can block an additional creature this turn` — *"one-shot effects, and this static layer has nowhere to put a duration"* | **taken** | +8: the 4 activated printings, Act of Heroism's trailing clause, and the "any number"/"up to two" wordings on Give No Ground, Valor Made Real and Yare |
| `~ must be blocked this turn if able` — the same note's sibling | **taken** | +11, from a bare sorcery, an activated ability, a trigger and six compound sentences |
| `Flashback—{cost}, Pay N life` — a price that is mana *and* life | **taken** | +4 (Acorn Harvest, Crippling Fatigue, Deep Analysis, Spirit Flare) |
| `remove a +1/+1 counter from it at end of combat` — an effect scheduled for a later step | **taken** | +6: the four Clockwork cards, plus Wicker Warcrawler and Frostweb Spider, which put one on instead |
| `Spells your opponents cast that target ~ cost {M} more` — *"a condition on the spell rather than a filter on the card"* | **taken** | +6, including the "less" direction and the bare "spells that target ~" |
| `You have hexproof.` | **stands — not a duration** | 4 sole blockers, 11 touching |
| `Creatures your opponents control enter tapped.` | **stands — not a duration** | 4 on that exact wording, 13 across the family |
| `destroy it at end of combat` (recorded at 4 cards) | **stands** | 18 sole blockers, and the reason is unchanged |
| A static prevention with no duration | **taken the round after** — see "A prevention with no duration is not a shorter one" | 33 sole blockers, 59 touching when recorded; +23 when built |
| `you may play them`, a play permission with no duration (Runestone Caverns) | **stands, and is duration-shaped** | 14 sole blockers — the missing thing is a permission that does *not* expire |
| `gain control … for as long as you control this` (~170 lines) | **already built**; the note was stale | `ControlWhileId`/`HeldWhileId` |
| `all creatures able to block ~ this turn do so` | **already built** | `LureId` |

**The two "this turn" combat requirements needed nothing but a name.** That is the whole finding
and it is worth being precise about, because it says what the original decline actually cost. The
requirement itself was never the problem: `Characteristics.ExtraBlocks` and
`Characteristics.MustBeBlocked` have existed since the block rules were written, and the compiler
already fills both from a printed static ability. What was missing was a *generated* name for
them — `extra-blocks:1` and `must-be-blocked` — so that the ordinary pump effect could hand them
to a creature with a turn number attached. `PumpUntilEndOfTurn` is not a pump: it is "apply the
effect this id names to this subject until the turn ends", and every characteristic that has a
generated name gets a duration for free. Two entries in `GenerativeEffects.Resolve` and one
subject helper is the whole of it.

**And the static reader is deliberately untouched.** `TryExtraBlocks` and `TryMustBeBlocked` still
refuse the "this turn" wordings, exactly as before, because they are not statics.
`The_each_combat_wording_is_a_static_and_the_this_turn_wording_is_not` is the control that keeps
those two readings apart: a single pattern loose enough to claim both would pass every other test
in this section and fail that one — which is the mistake the decline was right to refuse.

**Flashback's life was a hole in the permission, not in the mechanic.** CR 702.34a's cost is paid
"rather than the card's mana cost" and a cost is allowed to be more than mana;
`AlternativeCastZone` could hold chosen cards and permanents (retrace's land, escape's exiles) and
had nowhere to put life. It is charged where `ConditionalCost.LifeCost` is charged and checked
where it is checked — before any of the cost is paid, so a caster who cannot afford it is refused
having spent nothing. Two details fell out: Scryfall prints the separator as a space when the cost
is mana alone and as an em dash when it is not, so the pattern takes either; and the mana group
stays required, which is what keeps "Flashback—Sacrifice a Mountain" out.

**The delayed counter change is a new effect rather than a fourth verb, and the reason is the
default arm.** `DelaySourceAction`'s vocabulary is three zone changes, and the switch in
`FireDelayedTriggers` that reads its id **falls through to a sacrifice** for any word it does not
recognise. A counter removal handed to that vocabulary would not shrink the Clockwork Beetle, it
would destroy it — the harshest possible reading of the gentlest possible line, and precisely the
failure this file already recorded when it declined `destroy it at end of combat` for the mirror
reason. `DelaySourceCounters` therefore carries `counters:+1/+1:-1` and is read *above* that
switch, and the behaviour test's last assertion is that the creature is still on the battlefield.

**`destroy it at end of combat` still stands, and the sweep re-measured it at 18 rather than 4.**
Nothing about a delayed counter change gives the engine a delayed *destroy*: sacrificing is not
destroying (CR 701.21a), so regeneration and indestructible cannot touch it, and the reading would
still be harsher than printed. The number was worth re-measuring anyway — it is the largest
remaining sole-blocker family in this corner and the cheapest thing left here, needing one entry
in that same delayed vocabulary.

**"That target ~" cost more to ask about than to answer.** The pattern's own remarks recorded the
refusal — *"a condition on the spell rather than a filter on the card … would have to be read as
the unconditional form, which is a better card than the printed one"* — and the condition turns
out to be free: CR 601.2c chooses targets before CR 601.2f works the cost out, so the cast has the
list in hand when it gathers modifiers. `CostModifier.TargetsSource` compares the chosen targets
against the loop's own permanent id, which is why the modifier itself carries no id at all. An
activated ability is never handed a target list, so a modifier carrying the flag never applies to
one — which is what its printed word "spells" says. Every other `that target …` wording stays
refused, and both Kaervek's Torch (the spell taxing itself on the stack) and Killian (a
description rather than a permanent) are the controls that prove the new pattern did not eat its
neighbours.

**The two that are not duration problems at all, and what each actually needs.**

- **`You have hexproof.`** — 4 sole blockers, 11 cards touching. Nothing to do with duration: the
  ability is a permanent's static and lasts exactly as long as the permanent, which the layers
  already handle for objects. The gap is that **a player is not an object**.
  `TargetSpec.IsLegal`'s player arm asks three things — does the player exist, have they lost, and
  does an optional filter accept them — and the hexproof/shroud/protection check one block below
  it runs against `Characteristics.Of`, which exists only for game objects. `PlayerState` carries
  no keywords and nothing computes any. What this needs is a small second layer system over
  players, or an `IAbilitySource` seam the player arm can sweep the battlefield through; either is
  a real piece of engine, and neither is a duration.
- **`Creatures your opponents control enter tapped.`** — 4 on the exact wording, 13 across the
  family once artifacts, lands and "nonbasic lands" are counted. Also not a duration: it is a
  replacement effect **about somebody else's arrival**. The gathering loop already offers every
  battlefield object's replacements against every event, so the mechanism is there; what is
  missing is that `CardCompiler.Arriving` matches only the source's *own* arrival
  (`moved.OldId == source.Id`), and every enters-tapped reader is built on it. A group form needs
  a filter and a player scope, and one wrinkle worth writing down before somebody starts: for an
  `ObjectMoved` the arriving object does not exist in the state yet, so its controller and type
  have to be read off `OldId` in the zone it is leaving; only `ObjectCreated` carries them on the
  event. It is the same shape as the "creatures enchanted player controls" group-filter decline,
  and it is worth more cards than that one.

**Two declines that *are* duration-shaped and that these three durations do not reach**, recorded
so the next sweep does not re-hope for them:

- **A static prevention with no duration** — 33 sole blockers, 59 touching. The recorded reason
  still holds: `PreventDescribedDamage` produces a `PreventionEffect`, which is state the engine
  keeps until the turn ends, and `ContinuousEffectDefinition.While` is a condition on a
  *continuous* effect, so it cannot be hung on one. The shape that works is already in the file
  once — `PreventAllCombatDamage` is a `ReplacementEffectDefinition` with
  `FunctionsFrom = Zone.Battlefield`, which stops the moment its permanent does. That is the route,
  and it is a rewrite of the described-prevention reader rather than a duration. **Taken the
  following round, by exactly that route** — see below.
- **A play permission that does not expire** — 14 sole blockers. Both permissions the engine
  stores run out (`MayPlayUntilTurn`, `MayPlayThroughOwnersNextTurn`), and "you may play them for
  as long as they remain exiled" needs one that does not. A third field, not a fourth duration.

### The block of targets, and the strive cost that could not be built without it

> Strive — This spell costs {2}{W} more to cast for each target beyond the first.
> Any number of target creatures each get +1/+1 and gain indestructible until end of turn.

Declined twice, on a reason this file stated plainly: a spell whose target list has no length
cannot be written as a fixed list of specs, and an arbitrary ceiling would be a different card
whenever the ceiling mattered. Both halves of that are still true. What was wrong was the
conclusion, and the thing that makes it wrong had been in the engine since "up to one target" was
read: **`RequireLegalTargets` has always checked a range, not a number.** It counts how many specs
are not optional, and accepts any list between that and the whole. A cast already hands its
targets over as a list of the caster's own length.

So CR 601.2c is not asking for a new mechanism, it is describing the one that is there. "If the
spell has a variable number of targets, the player announces how many targets they will choose
before they announce those targets", and "once the number of targets the spell has is determined,
that number doesn't change". The announcement *is* the list. `TargetSpec.AnyNumber` marks one spec
as standing for the block, `ToEachChosenTarget` holds what happens to each of them, and
`VariableTargets.Expand` turns the pair into an ordinary counted spell against a number read off
the stack object. Nothing is remembered that was not already: no state field, no event, no
argument on the wire, and a game folded back from its log reaches the same spell it was cast as.

**Strive falls out of it, and could not have been built before it.** "Costs {2}{W} more for each
target beyond the first" is multikicker's shape with nothing to ask: the number was settled at
CR 601.2c and CR 601.2f only prices it, so the cost is read off the announced list. A previous
round refused to build this line on its own and was right to - every strive card also prints "any
number of target ...", so the cost would have been a price on a choice no player could make.

**Three rules the shape turns on, each of them a card:**

- **Zero is a legal announcement.** CR 601.2c's own worked example is Loaming Shaman resolving
  with "no cards are targeted". So an empty block is a cast, not a refusal - and such a spell can
  never fizzle for having no legal target, because it never had one. A strive spell cast for none
  of its targets pays what it prints, since there is no target beyond the first when there is no
  first.
- **The expanded copies are required, not optional.** That is the point of expanding at all.
  Optional says "the caster may stop here"; these say "the caster stopped here", so every one of
  them has to be legal at CR 601.2c and each fizzles its own share at CR 608.2b.
- **The block must be the spell's last target, and the compiler refuses to emit one anywhere
  else.** Every index downstream is positional - the effects' target indices, the slices modes and
  spliced text take - so a block that grew in the middle would move everything after it and aim
  one sentence's effect at another sentence's creature. A second block, or any later line that
  targets, sends the line back unread.

It is read for **spells only**, and that is a fail-closed choice rather than an oversight. A
trigger is asked for its targets one question at a time as it goes on the stack (CR 603.3d), and
that loop knows nothing about a block; the deferred questions an ability can leave behind find
themselves again by an index that expansion would duplicate. Left in, such a card would not throw
- it would resolve against exactly one target every time, which is a quietly weaker card than the
one printed. So a block on a trigger, an activated ability or a mode sends its line back unread,
and the coverage figure carries the debt where it can be seen. The same guard refuses a block
whose per-target effects contain a deferred question at all.

**16,138 → 16,156 complete cards, +18, none lost, measured by set difference.** Eight of the
eighteen are strive (Aerial Formation, Ajani's Presence, Blinding Flare, Cruel Feeding, Desperate
Stand, Kiora's Dismissal, Phalanx Formation, Rouse the Mob); the other ten are the block on its
own (Bone Harvest, Eerie Interlude, Footbottom Feast, Forever Young, Frantic Salvage, Gravepurge,
Hunters' Feast, Rabid Attack, Scapegoat, Sway of Illusion).

**And the measurement that matters most is the one that says to stop.** 173 corpus cards print
"any number of target"; 165 were unread. A first count said 126 of them would finish once the
phrase was expressible - that number was an upper bound and it was wrong, because it asked only
whether any *other* line was blocking, never whether the sentence carrying the phrase was one the
engine could read. Asked properly - every remaining card recompiled with **"up to two target"**
substituted for the block, which routes the identical sentence through the counted path that has
worked for a year - **not one further card completes.** Zero of 147.

That is a decisive answer and it is worth stating as one: **the block grammar is now as complete
as the effect vocabulary behind it allows.** Everything left in this family is blocked on the
sentence, not on the target list - "shuffle target creature card from your graveyard into your
library", "distribute four +1/+1 counters among target creature", "exile target creature
controlled by a different player" are each their own unread instruction and would be just as
unread with a count in front of them. Widening the block's own pattern buys nothing. The next
person tempted to spend a round on "any number of targets" should spend it on effect sentences
instead, and this paragraph is here so they do not have to re-derive that.

**A side finding, in the instrument.** `MechanicCoverageTests` asserts that every line shape the
compiler reads is played by some behaviour test, and it read the test source through a
hand-written list of the seven words a card calls itself by - against the compiler's twenty-five.
"This spell" was not on it, so the strive line reached the matchers unnormalised and the shape was
reported unplayed while a test was playing it. It also did not strip ability words, which the
compiler does before any template sees a line. Both now derive from the compiler:
`CardCompiler.SelfReferenceTypeNames` is exposed for exactly this reason, and it is the third
vocabulary list in this project to be caught having drifted from a copy of itself.

### A prevention with no duration is not a shorter one

The route above, built. **16,138 → 16,161 complete cards, +23, none lost, measured by set
difference.** Coverage 49.3% → 49.4%.

The whole of the difference between the two readers is the phrase "this turn", and it decides
which of two mechanisms the same sentence compiles to:

- **With it**, the words are a one-shot effect a resolving spell creates. It becomes a
  `PreventDescribedDamage`, which resolves into a `PreventionEffect` held in `GameState.Preventions`
  and swept away at cleanup with the rest of the turn (CR 514.2).
- **Without it**, the words are a permanent's static ability, and the shield has to last exactly as
  long as the permanent (CR 611.2c). It becomes a `ReplacementEffectDefinition` with
  `FunctionsFrom = Zone.Battlefield`, so it stops the moment the permanent does — with nothing to
  remember it by, which is the point: there is no cleanup that could forget to run.

`The_no_duration_wording_is_a_static_and_the_this_turn_wording_is_not` is the control that keeps
the two apart, and it asserts both directions of the mistake. A "this turn" line on a permanent
compiled as a static is a fog that never lifts; a no-duration line on an instant compiled as
described-prevention is a shield put up by a card that is already in a graveyard. Both are left
unread instead.

**The sentence is parsed once and read twice.** The old static reader had its own regex and its own
three-word vocabulary — `~`, `enchanted creature`, `equipped creature` — while the sentence reader
next door had a full one, and every noun the first could not say was a card nobody could play.
`EffectPhrase.ReadPreventionSentence` is now the one front end: it takes the sentence apart into
kind, victims, sources and *whether* it said "this turn", and reports the duration rather than
requiring it. `PreventVictim` and `PreventSource` are shared with it. What stayed in the compiler
is only the part that is true of a static ability and meaningless to a spell — the nouns that name
one object, which is a permanent's "this" and its host.

**Two shields, not one with both slots filled.** "Prevent all combat damage that would be dealt to
and dealt by enchanted creature" is an *alternative*: damage reaching it, or damage it deals. One
shield with the victim and the source both set is the conjunction of those — only the damage it
dealt to itself — which is a card that does nothing while reporting itself complete. It is also
the clause that defeated the shared splitter first: searching for " by " inside "to and dealt by
enchanted creature" cuts the sentence in the middle and reports the shield as covering something
called "and dealt".

**"Other" is an object, not a kind of card.** No filter over card types can say what Tajic's
"other creatures you control" leaves out, because what it leaves out is the permanent that printed
the sentence. `PreventionEffect.Excludes` holds that id; `PreventVictim` reports the word rather
than answering it, and the spell reader *refuses* a sentence carrying it, because a resolving spell
is not on the battlefield to be left out. Read as the bare filter, Tajic shields himself — a
strictly better card than the printed one, and precisely what the fail-closed rule is for.

**"Dealt by target creature" needed a field and got two payoffs.** `PreventionEffect` could describe
a source (`SourceFilter`) but could not *name* one, so the eleven cards aiming a shield at one
attacker were unread. `PreventionEffect.Source` is an id compared rather than a description
rechecked, which is what CR 609.7a says: the source is chosen when the effect is created. It is
filled from a target by `PreventDescribedDamage.TargetIsSource` — a flag rather than a second index,
because `EffectTargets` keys on the one index an effect carries and a sentence never names both —
and from the board by the static reader's host nouns. Nine of the twenty-three cards came from this
half: Kor Haven, Lady Evangela, Safeguard, Songstitcher, Benalish Missionary, Gossamer Chains, Fend
Off, Restrain and Warning, plus Soul Parry, whose "one or two target creatures" the existing
multi-target grammar already read into two shields.

**The predicate is one copy.** `Preventions.Watches`/`Covers`/`CoversPlayer` moved out of `Game`
into `MtgEngine.Rules/State/Prevention.cs`, and both mechanisms ask them. Only the lifetime differs
between a turn-long shield and a permanent's; "does this shield cover this damage" is the same
question, and two copies of it are two chances to disagree about what "creatures you control"
means. The one thing the replacement path cannot do as well is read a *granted* control effect:
`ReplacementEffectDefinition.Applies` is handed a state and an object and no `IAbilitySource`, so
the controller comes from the control-only layer reader asked with `EmptyAbilities` — the
compiler's standing compromise in eighteen other places. Threading abilities through that signature
is the fix and it is forty-two call sites wide.

**A condition on a static costs a replacement nothing.** "During your turn, prevent all damage that
would be dealt to you" is CR 604.3, and a replacement effect asks its question at the moment the
event would happen — which is exactly when the condition has to hold. It is one more clause in
`Applies`, read through `BoardConditions` so "your turn" means the same thing here as in living
metal, and a condition that vocabulary cannot read leaves the whole line unread rather than
producing the unconditional shield.

**What is still refused, re-measured: 17 sole blockers on this exact family**, and every one needs
a capability rather than a wider pattern.

- **5 need a filter over what a permanent is *doing*** rather than over what its card says:
  "attacking creatures you control" (Dolmen Gate), "other attacking Soldiers you control" (Rescue
  Retriever), "creatures it's blocking" (Wall of Vapor), "creatures blocking it" (Armored
  Transport), "creatures with first strike" (Tresserhorn Skyknight). `PermanentFilter` is a
  `SearchFilters` id asked of the printed card — the same documented deviation `Preventions.Covers`
  already carries — and these want computed characteristics and combat state.
- **6 lead with a condition `BoardConditions` cannot read**: "you control a permanent of each color"
  (Spirit of Resistance), "~ has an ice counter on it" (Woolly Razorback), "~ is untapped"
  (Thunderstaff), "~ is attacking" with banding behind it (Camel), "you control another creature"
  with "spells that target it" behind it (Bronze Horse), and Caduceus, whose shield is a quoted
  ability granted by an equipment. The conditional prefix reader is there; the vocabulary is what
  stops these.
- **2 want "enchanted creatures"** as a description — a permanent's state, not its type (Enchanted
  Being, Wall of Putrid Flesh).
- **1 each** for a colour chosen and remembered on the permanent (Prismatic Ward), `token` in the
  card-filter vocabulary (Emmara Tandris — the cheapest thing here, and a change to `SearchFilters`
  rather than to prevention), a comparison between the two objects in one sentence (Well-Laid
  Plans), and one that is not a prevention problem at all: Guardian Naga // Banishing Coils reads
  its line correctly and is refused because the whole card counts as a spell, so the static reader
  is never offered the line.

**One recorded decline was stale, and it was the biggest one.** The tail was written down as "26
name a target the shield cannot aim at", and the shield could not aim at one because
`PreventionEffect` had no source slot — but the *target grammar* had meanwhile grown the adjectives
those cards use. `Specs.Parse` already read "target attacking creature", "target blocked creature",
"target unblocked creature" and "target attacking or blocking creature", and it already read "one or
two target creatures" into two targets. Adding the field turned nine of them over with no pattern
work at all. That is the fourth time this file has recorded a decline going stale because a
mechanism arrived elsewhere; the check that finds them is re-reading the note, and it costs an hour.

The other two thirds of that tail stand and were re-measured. **"Needs a state-based filter" is only
half stale**: the adjectives arrived in the grammar that reads a *target*, and not in the vocabulary
that describes a *set* — `PermanentFilter` and `SourceFilter` are `SearchFilters` ids asked of a
printed card, so "prevent all combat damage that would be dealt this turn by attacking creatures"
(Harmless Assault) is still unread while Kor Haven now plays. The multi-sentence declines are
unchanged: Subdue, Boros Fury-Shield, Chain of Silence and Inquisitor's Snare each read their
prevention and are blocked by the clause after it.

Three families sit next to this one and are each worth more than what is left inside it. CR 615.10's
numbered static — "if a source would deal damage to you, prevent 1 of that damage" — is **23 sole
blockers, 24 cards**: the five Spheres, Urza's Armor, Daunting Defender, Djeru, Temple Altisaur,
Gisela. It needs a reader and not a mechanism: `PreventionEffect.Amount` is already CR 615.10's cap
applied afresh to each damage event, and `Preventions.Watches` already asks about the source's
colour and type. CR 615.7's countdown is **32 sole blockers**, and that machinery is finished too.
And "damage can't be prevented" (CR 615.12) is **24 sole blockers** and is the one of the three that
is genuinely a mechanism — an unpreventable flag the replacement pass has to carry, which is why
Banefire and Questing Beast are unread.

### A mana colour named while an effect resolves

The mana vocabulary had a hole with a comment in it: **"there is nowhere to ask it."** `AddMana`
needs its colours decided when the card is compiled, and the mana-ability path answers the same
question by splitting itself into one ability per colour — which an effect cannot do. So every
sentence that adds mana whose colour is chosen on resolution was left unread.

It is the same shape as every other question this engine asks. `AddChosenMana` emits a
`ManaColorChoiceRequested`, the settle sweep raises a `ChoiceKind.ChooseManaColor`, and the answer
is a `ChoiceMade` in the log like any other — so a replay reaches the same offer rather than needing
a continuation the log cannot rebuild. `SettleOwedSeek` and `SettleOwedRoll` are the models.

**The menu rides on the event.** "One mana of any type that land produced" is read off a permanent
that may have left the battlefield by the time the answer arrives, and a question whose options
moved underneath it is not the question that was asked.

**A question with one possible answer is not a question (CR 118.3).** A Forest can only make green,
and Dictate of Karametra sees every land anybody taps — a question per tap would make the card
unplayable rather than merely annoying. The effect collapses a one-item menu into the mana itself,
and `SettleForcedManaColors` is a second lock in the sweep for a palette that narrowed in between.

Three pieces, and only one of them is the choice:

- **A colour decided at resolution**, which is the mechanism above.
- **The types a land could produce, read at resolution** (CR 106.7), from the land's *computed*
  abilities rather than its printed ones — a granted mana ability counts and a face-down permanent
  has none. `Game.SubjectObjectOf` now answers `ManaAdded` with the permanent that made the mana, so
  "that land" and "its controller" have something to mean; the compiler's `NamesAnObject` allow-list
  was widened to match, one list mirroring the other the way that pairing already works elsewhere.
- **A player scope on `AddMana`.** "Whenever a player taps a land for mana, **that player** adds
  {G}" puts mana in somebody else's pool, and the effect could only ever fill its controller's.
  That half alone was worth as many cards as the choice was: Mana Flare, Heartbeat of Spring,
  Eladamri's Vineyard, Magus of the Vineyard and Zhur-Taa Ancient are all symmetrical and none of
  them needed a colour named.

**A stated divergence.** "Any type that land produced" is read as the types the land *could*
produce, because a resolution context carries the object an event was about and not the mana it
made. On the lands this is printed against — a Forest, a Swamp, anything with one mana ability —
the two answers are identical and there is no question at all. They come apart only on a land that
could have made something else, where the engine offers the wider menu.

**Reading the sentence opened a hole and it had to be closed in the same commit.** The general
activated-ability path reads `cost: effect`, and the moment `EffectPhrase` could read "Add three
mana in any combination of colors" that path would have built it — as an ability that **goes on the
stack**, which a mana ability never does (CR 605.3b). That is the worst shape of bug this compiler
has and this file has already recorded one of them: the card compiles, counts as covered, offers
its button, and plays differently from what it prints. `TryActivatedAbility` now refuses any
untargeted line whose whole effect is mana (CR 605.1a), so a payout `TryManaAbility` declined stays
unread instead of arriving through the back door. A test asserts the refusal.

**The measured class, because the brief's numbers were not the class.** 228 incomplete cards carry
an unread line that wants a mana colour or type decided during resolution; 161 of them are one line
short. Mutating the corpus so the colour was already fixed showed that **only 19** of those 161 were
blocked by the colour at all. The other 142 are blocked by something else *in the same line*, and
they sort into families this work does not touch:

- **~35 want "Spend this mana only to cast \<thing>"** — Base Camp, Pillar of the Paruns, Cavern of
  Souls, Jeweled Lotus. The restriction vocabulary, not the colour. Every one of these is a mana
  ability whose colour the enumeration path already reads.
- **~16 want a variable amount** — "add X mana of any one color, where X is…" (Food Chain, Axebane
  Guardian, Nykthos, Empowered Autogenerator).
- **~10 carry the ability inside quotation marks** — "lands you control have '{T}: Add one mana of
  any color'" (The World Tree, Jiang Yanggu, Kitesail Larcenist). A granting problem.
- **5 are the storage lands** — "{1}, Remove X storage counters from ~: Add X mana in any
  combination of {G} and/or {W}" (Calciform Pools and its four siblings).

A further 95 incomplete cards match on the words "mana of any color" and are **not this class at
all**: "you may spend mana as though it were mana of any color" is a spending permission, not a
question about what colour mana is. 61 of those are one line short. Counting them in would have
tripled the apparent size of the work.

**Declined, and why.** The storage lands are the only named row left undone, and they are undone
for a reason rather than for time: the choice is inside a *mana ability*, which never uses the stack
and is activated in the middle of paying for a spell. Suspending one to put a question would stop
the game mid-cast, and the engine's own answer to that problem — take the choice *with* the
activation, the way `ActivateAbility(..., costPayment:)` takes a cost — is a change to the hub's
wire contract and belongs with that decision, not smuggled in beside a resolution-time question.
The three-colour combination cap is unchanged and is now defended by the guard above.

**36 cards, and the set matters more than the count.** 16,279 → 16,315 with nothing lost.
The set is the evidence: **Manamorphose, Lotus Cobra, Deathrite Shaman, Mirari's Wake, Zendikar
Resurgent, Dictate of Karametra, Zhur-Taa Ancient, Verdant Haven, Wild Growth, Fertile Ground,
Overgrowth, Utopia Sprawl, Trace of Abundance, Wolfwillow Haven, Dawn's Reflection, Market Festival,
Blighted Burgeoning, Mana Flare, Heartbeat of Spring, Eladamri's Vineyard, Magus of the Vineyard,
Blinkmoth Urn, Elvish Guidance, Tangleroot, Regal Behemoth, Sarkhan Unbroken, Realm-Scorcher
Hellkite, Traitorous Greed, Branch of Vitu-Ghazi, Crumbling Vestige, Outcaster Trailblazer, Quirion
Sentinel, Red Death Shipwrecker, Lavaleaper, Buried in the Garden, Rosethorn Acolyte // Seasonal
Ritual.** Half of them are the player scope rather than the colour, which is the finding the
mutation probe would have missed if it had only been run one way.

### A mana payment asked while an effect resolves

CR 605.3a lets a player activate mana abilities whenever a rule or effect asks them for a mana
payment, in the middle of a resolution included. This engine read the mana **pool** instead, so
`AskOwedPayment` skipped the question whenever the pool was short and ran the "if you don't"
branch. CR 118.3 was applied correctly - a question with one possible answer is not a question -
to a premise that was false. Every "counter target spell unless its controller pays {3}" was a
hard counter against anybody who had not floated the mana before the counterspell was cast.

**The measured surface, and the distinction that matters more than the count here.** 1,445 corpus
cards print a payment asked while something resolves, once the cast-time wordings (kicker,
multikicker, buyback, "rather than pay") are stripped out: 612 complete, 833 not. The number to
look at is the first one:

| | cards |
|---|---|
| Complete, and compiling to a `MayPay` with a mana cost - **asked, and auto-declining** | **424** |
| Print the shape, still unread (long tail; no family above 5 rows) | 833 |
| Complete cards overall, before and after | 17,002 |

A card that compiles and auto-declines is worse than one that does not compile, and the coverage
number cannot tell them apart - both of these rounds' numbers are 17,002. The 424 sort into seven
families: 164 "you may pay {N}. If you do", 83 ward, 58 "counter ... unless its controller pays",
45 echo, 33 "sacrifice/destroy unless you pay", 24 cumulative upkeep, 11 extort.

**Only the gate was shut, and that is the finding.** The two other halves of this were already in
the engine and had never met:

- `Game.ActivateAbility` calls `RequirePriority` **only** for an ability that is not a mana
  ability (CR 117.1d), and a mana ability returns from it early - without settling, without
  granting priority. So a pending question is still pending after the player taps three lands.
- `ResolveOptionalPayment` already re-checked the real pool before charging it, and ran the "if
  you don't" branch when it came up short.

So nothing here is a suspended continuation: the taps are ordinary logged activations and the
answer is an ordinary `ChoiceMade`. The one line that had to change was the CR 118.3 test, from
"has the mana" to "has, or could produce, the mana".

**The check is optimistic on purpose, and the asymmetry is the whole argument.** A wrong "no" is
silent and unrecoverable - the player is never asked and the else-branch runs as though they had
refused a question nobody put. A wrong "yes" costs one question answered no, and the real pool is
still checked before anything is charged. Every approximation in `CouldPayMidResolution` therefore
leans towards asking, including the search budget: running out answers "ask".

**It is a search rather than a total, because a dual land is a decision.** One land that taps for
{G} or {U} cannot pay {G}{U} - it taps once and makes one of them - and "how much mana could this
board make" says yes. One entry per permanent holding every payout that permanent offers, and the
caller chooses one per permanent. Restrictions travel with the mana rather than being dropped:
"spend this mana only to cast creature spells" genuinely cannot pay a counterspell's tax, and
dropping it would be the one approximation that errs towards a question with no answer.

**Nothing was gained and nothing was lost - 17,002 before, 17,002 after, the sets byte-identical.**
That is the right result for the change and the reason it needed a set diff to say so: this is not
a compiler change, it is 424 already-complete cards that now play the rule they print. The proof is
in games rather than in a count. A player with three untapped Forests and an empty pool is asked,
taps them while the question stands, and pays; a player with one Forest is not asked at all and the
spell is countered; a player who answers yes and taps nothing declines. All seven tests were
mutation-checked in both directions - three fail against the old pool-only gate, and the four
"not asked" ones fail against a check that always says yes.

**Declined, measured, and named.** The audit's two smaller relatives were both re-measured and
neither is what it was briefed as:

- **A `MayPay` inside a coin-flip or die-roll branch is a zero-card surface today.** The wording
  does not compile at all - "Flip a coin. If you win the flip, you may pay {2}. If you do, ..." is
  unread - and no corpus card compiles to a mana payment nested inside a `FlipCoin` or `RollDice`.
  39 complete cards flip or roll; none of them nest a payment. The defect the audit named is real
  and latent, and this change retires it in advance: the branch's payment reaches `AskOwedPayment`
  at the next settle like any other, and now counts untapped lands.
- **The forced-answer arms are eight, not nine.** Walked deliberately, every `Ask` arm of the sweep
  is `MinPicks = 1, MaxPicks = 1`, and eight of them can be handed a one-item list with no decline
  option in it: `AskOwedPermanentChoice`, `AskOwedPopulate`, `AskOwedManifestDread`,
  `AskOwedConnive`, `AskOwedCreatureTypeChoice`, `AskOwedEntryChoice`, `AskOwedVenture`,
  `AskOwedReadAhead`. Three more look forced and are not - `AskOwedEnlist`, `AskOwedExploit` and
  `AskOwedSoulbond` always append a decline option, so their menus are never shorter than two -
  and `AskOwedLibraryEnd` and `AskOwedColorChoice` have fixed menus of two and five. The four that
  already settle are `AskOwedCounterChoice`, `AskOwedRingBearer`, `SettleForcedManaColors` (with
  `AskOwedManaColorChoice`'s `Count <= 1`) and `ChooseForcedProtectors` (with `AskOwedProtector`'s
  `Count < 2`), which is the model each of the eight wants.

  Left undone deliberately rather than for time: a forced one-option question **breaks no rule**.
  The player does choose, and there is one legal choice; the harm is a game that stops to collect a
  click, which is an ergonomic and soak-driver problem rather than a card playing differently from
  its text. The payment gate above was the opposite - it changed outcomes silently - and mixing the
  two in one commit would have put eight behaviour changes with no rules consequence next to the
  one with all of it.

### Where X is: the clause was rarely the blocker

`where X is <expr>` ranked second in the shape table at 729 sole blockers across 347 distinct
expressions, and the obvious reading was that it wanted an amount grammar. It did not. Compiling
the whole corpus and asking three questions of each of the 729 lines separates the family into
parts that want different work:

- **does the head read at all** with X replaced by a literal `2`?
- **does the frame carry a bound X**, with the expression replaced by one `Counting` answers?
- **does the expression read**, in a frame the compiler already takes?

| | cards | what it means |
|---|---|---|
| head does not read with a number either | **394** | not a `where X is` card at all. The clause is riding along; the sentence in front of it is unread for its own reasons, and no amount grammar would finish one of them |
| head reads, frame carries X, expression unknown | **183** | a vocabulary gap — 114 distinct expressions, the largest 18 |
| head reads, expression known, frame refuses X | **142** | a *frame* gap: `Counting` already answers the clause and the sentence in front cannot hold the answer |
| head reads, both unknown | 73 | needs one of each |

So the family is 45% mirage, and the half that is real splits evenly between vocabulary and frame.
The ranked expression table below is the vocabulary half; the frame half is not an expression
question at all and is the bigger single lever.

**Ranked by family, over all 729:**

| family | cards | head reads | frame carries X | expression known |
|---|---|---|---|---|
| a count — `the number of <group>` | 360 | 160 | 78 | 183 |
| a possessive stat — `<x>'s power/toughness/mana value` | 174 | 86 | 58 | 28 |
| everything else (a long tail of one-offs) | 74 | 22 | 17 | 0 |
| greatest/least among — `the greatest power among ...` | 47 | 29 | 15 | 0 |
| life gained/lost — `the amount of life you gained this turn` | 22 | 15 | 7 | 0 |
| mana spent/paid — `the amount of mana spent to cast ~` | 14 | 3 | 2 | 0 |
| damage — `that excess damage` | 13 | 5 | 3 | 0 |
| a die roll — `the result` | 8 | 1 | 0 | 0 |
| a life total — `your life total` | 7 | 4 | 3 | 0 |

**The commonest single expressions** (`head` = the sentence reads with a literal, `frame` = it
reads with a counted X already, `expr` = the shared vocabulary already answers the clause):

| cards | head | frame | expr | expression |
|---|---|---|---|---|
| 46 | 25 | 18 | 0 | `~'s power` |
| 21 | 13 | 9 | 0 | `that spell's mana value` |
| 16 | 7 | 2 | 16 | `its power` |
| 15 | 12 | 6 | 0 | `the amount of life you gained this turn` |
| 13 | 8 | 7 | 0 | `the number of colors of mana spent to cast ~` |
| 12 | 6 | 6 | 0 | `that creature's power` |
| 12 | 7 | 4 | 0 | `the greatest power among creatures you control` |
| 12 | 8 | 5 | 0 | `the sacrificed creature's power` |
| 11 | 4 | 2 | 11 | `its mana value` |
| 11 | 4 | 0 | 11 | `the number of creatures you control` |
| 10 | 6 | 0 | 10 | `the number of cards in your hand` |
| 9 | 4 | 0 | 9 | `the number of creature cards in your graveyard` |
| 8 | 1 | 0 | 8 | `the number of attacking creatures` |
| 8 | 0 | 0 | 0 | `that card's mana value` |
| 7 | 0 | 0 | 0 | `the result` |
| 7 | 4 | 3 | 0 | `your life total` |
| 7 | 3 | 3 | 0 | `that creature's mana value` |

The three rows where `frame` is 0 and `expr` is the full count are the shape of the frame gap: the
compiler knows the clause perfectly and the sentence in front of it cannot take the answer.

### Five readers wrote a pump's size out five times, and one of them knew about X

Of the 142 frame-gap cards, **102 are a pump** and they split three ways by what the sentence
pumps: `~ gets +X/+X` 23, `it gets +X/+X` 22, `<group> get +X/+X` 27, and the rest already worked.
Which is the whole story: the size fragment `(?<p>[+-]\d+)/(?<tough>[+-]\d+)` was written out
separately in **six** pump readers — target, source, pronoun, group, "creatures you control", and
an Aura's host — and only the *targeted* one had ever been widened to `[+-](\d+|X)`. So
"target creature gets +X/+X until end of turn, where X is the number of Elves you control" read,
and the same clause about any of the other five did not.

Not a missing feature: `WithCountedVariable` had been binding X for two rounds and the pronoun
reader was already resolving the right permanent. The five readers simply could not read a size
that was not a digit, and the sentence never reached the wrapper.

The fix is one regex fragment (`PT`) and one shared record. `VariablePumpSize(Amount, Amount)`
carries the size beside a placeholder id; each pump effect builds the real id as it resolves, so
the layer machinery is untouched and never learns a pump can be variable. `PumpSizeOf` is the one
place that decides whether a printed size is a number or an X — the previous arrangement had that
decision twice, and the second copy is what `PumpTargetByVariable` was. That record is gone: the
targeted reader now uses `PumpUntilEndOfTurn { Size }` like the other five, which resolves the
target through exactly the same permanent check it did.

### `~'s power` is not the pronoun, and it never needed to be

The largest single expression in the whole family, 46 cards, and it was blocked by one word.
`VariableIsStatLine` read `where X is its power` and refused everything else — but `~` is the
card's own name, which the compiler substitutes before any of this runs, so `~'s power` names the
**source** and nothing else. None of the pronoun's difficulty applies: "its" has to be worked out
from the shape of the head, and the reader refuses a head that has a target because it cannot tell
Onward's reading from Dying Wish's. A card that says its own name has nothing to work out, so the
possessive is read where the pronoun is refused, with a target in front of it or without one.

Read off `Characteristics.Of` rather than the printed card, for the reason the whole engine does:
a Wild Beastmaster wearing two +1/+1 counters pumps by three.

**Declined, and why**: `that spell's mana value` (21), `that creature's power` (12), `the
sacrificed creature's power` (12), `that card's mana value` (8), `the discarded/exiled/revealed/
milled card's ...` (11 between them). Each names an object this clause has no way to find — a
spell on the stack the trigger was about, a permanent sacrificed as a cost, a card a previous
sentence moved — and a stat read off the wrong permanent is a card that compiles, resolves and
plays a different number. They stay unread, which is the answer a deck check can refuse.

**The fail-closed edge, tested**: an X the vocabulary cannot compute leaves the line unread rather
than settling at nought. A spell dealing X damage where X silently resolves to 0 compiles, passes
the deck gate, goes on the stack, resolves and does nothing, and the coverage number goes *up*.
Every one of the 63 cards this round completed has X bound by a `where X is` clause the compiler
now reads or by a `{X}` it was cast for; none has a pump whose size nothing supplies.

**Still open**: `Counting` gets a phrase and no target builder, so a count phrase cannot *introduce*
a target. "Target player draws X cards, where X is the number of cards in that player's hand" reads
because the head named the player; "draw X cards, where X is the number of cards in target player's
hand" does not, because the clause would have to add the target itself. Unchanged by this round.

### Round nineteen: the pronoun family's last two halves, and a delay that can name a token

Round seventeen classified every reader whose pattern admits a subject pronoun, fixed six that
aimed at the source when the sentence named somebody else, and left two groups for a pass of their
own. This is that pass. **17,641 -> 17,658 complete cards, +17, none lost by set diff**, and a
compiled-effect diff of 37 cards — of which **11 were already complete and were playing the wrong
card**, which no coverage number can see in either direction.

**The longhand exalted disagreement was taken two rounds ago and this round only confirmed it.**
`TriggerConditions.NamesAnObject` has admitted the attacks-alone family since `075b59f`, the same
commit as the six readers, so Agents of S.H.I.E.L.D. and A-Eiganjo Exemplar already compile to
`PumpUntilEndOfTurn(Subject=TriggeringObject)` — the creature that attacked alone, not the
permanent with the ability. A re-measurement rather than a change, and worth writing down because
the work was queued twice.

#### The readers that refuse a pronoun rather than aiming at the source

Under-read rather than mis-aimed, so the direction of error was already safe and the only question
was what each is worth. Measured against the corpus by the shape of the sentence *and* by whether
its trigger is one `NamesAnObject` admits, then confirmed by a compiled-effect diff:

| reader | corpus cards it reaches | newly complete | taken |
|---|---|---|---|
| `ItLine`, the return verb | **8** — Squee's Embrace, Demonic Vigor, six Zendikons | 2; the Zendikons are blocked elsewhere as well | yes |
| `SkipUntapLine` | **6** — Wall of Frost, Labyrinth Minotaur, Cleric of Chill Depths, Vertigo Spawn, Mercurial Kite, Queen of Ice | 7, the seventh being Mesmerizing Benthid, whose Illusion token quotes the same sentence | yes |
| `ItLine`, the untap verb | **0** | 0 | subject added anyway — one sentence, one ladder |
| `SuspectLine` | **3**, and all three need the decline below | 0 | no |
| `GoadLine` | **0** — every corpus printing of "goad it" has a target in front of it | 0 | no |
| `AimedThisTurn` | **0** — its own comment predicted this and was right | 0 | no |

`ItLine`'s five verbs now ask `ObjectOf` once and hand the answer to whichever effect the verb
names, rather than reaching straight for the last target. Three of the five — destroy, exile, tap —
only ever reach it for wordings their own readers further up refused, so the two that changed are
untap and return, and `UntapTarget` and `ReturnToHand` are the two effects that gained a subject.
`SkipNextUntap` gained one for the same reason.

**The zone is the load-bearing half of the return, and it was nearly a silent no-op.**
`ReturnToHand` read only from the battlefield, because a target that has stopped being legal is
skipped (CR 608.2b). But a pronoun the *trigger* answered names a card that is expected to have
left: "when enchanted creature dies, return that card to its owner's hand" is about the card now in
a graveyard under a new id (CR 400.7). All eight cards would have compiled clean and returned
nothing at all — a complete card that does nothing is worse than the unread line it replaced. The
battlefield rule now applies to a target and not to a trigger's subject, and the played game moves
the card rather than reading the compiled effect.

**Declined, with the measurement behind each:**

- **The self-enters trigger as a subject** (`when ~ enters`). It looks free — the event is the
  source arriving, `Game.SubjectObjectOf` answers with it, and the answer is the same one the old
  source fallback gave. Admitting it read **6 more cards and lost 2**, and three of the six are
  Auras: "when this Aura enters, if enchanted creature is red, tap it" (Ray of Frost, Volition
  Reins, Howl of the Hunt) means the *enchanted permanent*, and the triggering object is the Aura.
  It also moved Scion of Stygia's two d20 branches off the creature the ability had targeted and
  onto the Scion, because a roll branch is read against a builder the ability's own target is not
  in yet. Coverage up, cards worse — the exact trade the allow-list exists to refuse. It is the
  only thing blocking `SuspectLine`'s three cards, which is why those stay unread.
- **"That land" as a pronoun** (2 — Vorinclex, Voice of Hunger and Winter's Night). The shared
  `Pronouns` list is read by five readers, and widening it for one sentence changes all five. The
  new arm of `SkipUntapLine` is gated on that list; the old arm, which reads whatever the sentence
  had already targeted, is not — gating both cost Chandra's Revolution, Mana Skimmer and Stensia
  Innkeeper, which had been reading "tap target land. That land doesn't untap" for months.

#### The delayed pronoun's fallback, and eleven cards that sacrificed themselves

"Create a 2/1 red Elemental creature token with trample and haste. **Sacrifice it** at the
beginning of the next end step" is Lagomos, Hand of Hatred. It was fully compiled, it played, and
every turn it sacrificed **itself** — the delayed vocabulary could only aim at the source or at a
target, and neither is the token. Rakdos Guildmage exiled itself instead of the Goblin; Angelic
Favor, Balduvian Dead, Daring Piracy, Elemental Appeal, Giantbaiting, Hungry for More, Thunderheads,
Tidal Wave and Zektar Shrine Expedition are the rest of the eleven.

**The delay is folded into the effect that mints the token, not added beside it.** A delayed
ability is set up against an object id (CR 603.7b) and the only place that knows the token's id is
the creating effect — which is how mobilize's own sacrifice has always been built. So `CreateToken`
and `CreateTokenCopy` carry an optional `DelayedTokenAction` and emit one delayed ability per token
as they mint them. Nothing flows between effects at resolution time, which is what makes it work
inside a branch as well as beside one.

The alternative was measured and rejected. A subject resolved from a record of what the resolution
had created would answer *nothing* wherever the creation runs inside another effect's branch,
because `ResolutionRecord` is threaded by `Game.RunEffects` and not by the ten effects that run
children — and a silent no-op on a card that reads as complete is the failure this whole family is
written to avoid. The fold needs no such flow at all: the minting effect emits the delayed ability
itself, so it works wherever the creation is. What the reader will not do is fold onto a creation
it cannot see as a sibling of the sentence, and that leaves the line unread.

**+8 cards on top of the correction**, all of them the Kiki-Jiki shape whose pronoun the reader had
been refusing outright rather than aiming at the creature that was copied: Kiki-Jiki, Mirror
Breaker itself, Feldon of the Third Path, Molten Duplication, Nemesis Trap, Saheeli, the Sun's
Brilliance, Tempestra, Dame of Games, The Fire Crystal and The Jolly Balloon Man.

The destroy verb's narrowing is untouched — it takes the source only when nothing at all preceded
it — because the token case that was its whole reason is now answered above it, on all four verbs
rather than on the one.

**Two tests that asserted a refusal were made to play the card rather than flipped.** Both refusals
existed only because a token could not be named, so both are now games: Kiki-Jiki's full printed
ability sacrifices the copy and leaves the creature it copied on the battlefield, and the Hornet
Cannon shape is a `[Theory]` over both verbs asserting that the token goes and the permanent that
made it stays. That second claim is the one the old reading failed — something did go away at end
of turn, and it was the card that made the token.

### Round twenty: an emblem, and the first half of excess damage

Two mechanisms the engine did not have, both declined repeatedly as "unmodelled", and both
measured before anything was built. The measurements disagreed with the census in opposite
directions, which is the fifth and sixth time that has happened.

#### Emblems are worth 27, and 13 of them landed

93 corpus cards print "gets an emblem with". Swapping the emblem *sentence* of each for
`Draw a card.` and recompiling completed **27** of them: that is the row's real ceiling, above the
census's 22. The other 66 are short somewhere else on the card and an emblem would not finish them.

**An emblem needs no new zone, no new event and no new state field.** CR 114 says what one is by
saying what it is not — no card types, no mana cost, no colour, usually no name — and says the one
thing it does: *its abilities function in the command zone* (CR 114.4). So it is an object created
into `Zone.Command` the way a dungeon is, carrying a `CardDefinition` whose whole text is the
quoted ability, which `CompiledPool` then compiles like any other card. `Emblems.CardFor` is a pure
function of the printed words, so a log replayed in a later process rebuilds the same definition —
the same promise `TokenCards.Granting` makes about a granted ability one zone along.

Three seams were all it needed: `Emblems.TriggersOf` re-keys the compiled triggers to
`FunctionsFrom = Zone.Command`, because the compiler writes the battlefield onto every trigger it
reads; `Characteristics.Candidates` sweeps the command zone beside the battlefield, because an
anthem whose source is not a permanent was a thing the layers had never been shown; and
`CreateEmblem` emits one `ObjectCreated` per recipient.

**The gate that decides whether an emblem reads is a comparison against an empty compile, not a
list.** An emblem whose text compiles to an activated ability, a cost modifier or a player quality
would sit in the command zone reading perfectly and doing nothing, because none of those is
gathered from that zone. `Emblems.Reads` compiles the quoted text and requires every `init`
property of the result to equal a blank card's except `Triggers` and `Statics` — so a field added
to `CompiledCard` tomorrow closes the gate by default rather than opening it. Get-only properties
are dropped by derivation rather than by name, because each is a summary of the fields beside it.
That refusal is what keeps Saheeli's cost reduction, Teferi's loyalty permission, retrace, storm
and "that player loses the game" out: **14 of the 27 are still one line short, and each is short
for a mechanic that genuinely does not exist.**

**A join inside a quotation is not a join** — the fourth time this codebase has paid for that. The
`, then` splitter did not respect quotation marks, so `"search your library for a creature card,
put it onto the battlefield, then shuffle"` was cut in half inside the emblem clause and both
halves were fragments. Three of the thirteen are that fix. The reader also refuses a clause with
anything after the quotation: Kiora prints "You get an emblem with '…' Then create three 8/8 blue
Octopus creature tokens" and the full stop that would have separated them is *inside* the quotes,
so reading the emblem and dropping the rest would have made three Octopuses vanish off a card
reporting itself understood.

#### Excess damage is worth 19, and only three of them are cheap

The census's 20 is nearly right and the substring is not: 142 corpus cards say "excess", and 104 of
them are trample's reminder text, which the compiler strips before it sees a line. **38 cards have
"excess" in a line the compiler reads; 20 are reachable** with only that sentence blocking them,
and one of those (Superior Numbers) says "in excess of the number of creatures" and is not about
damage at all. So **19**.

They split three ways, and the split is what decides how much of this row a round can take:

| what the sentence needs | cards |
|---|---|
| the CR 120.4a redirect — "excess damage is dealt to that creature's controller instead" | 4 |
| the *number*, carried out of the damage event into the resolution | 8 |
| amass, discover, conjure, Lander tokens, "discard the greatest mana value", a delayed trigger | 6 |
| Ram Through's redirect, conditional on the dealer having trample | 1 |

**The redirect is built and is worth three.** `ExcessDamage.Over` is the subtraction the engine had
never made: damage marked, computed toughness, loyalty and defence counters were all here, and
nothing had ever taken one from the other. CR 120.4a is step one of the four-part sequence — before
replacement and prevention — so the split is made where the damage event is built, and `DealDamage`
emits a `DamageMarked` for the lethal part and a `PlayerDamaged` for the rest, both naming the same
source so protection, prevention and lifelink all still read it. All three of CR 120.4a's measures
are implemented, not only the creature arm, because a card reading "target creature or planeswalker"
hands it either kind.

The fourth is refused, and deliberately: Gandalf's Sanction wraps its damage in "where X is the
number of instant and sorcery cards in your graveyard", so the rider's search for a top-level hit
to modify finds nothing, and a rider that quietly found nothing would leave a card printing a
redirect and performing none.

**The eight that need the number are left, with the design written down rather than half-built.**
They want an excess magnitude on `DamageMarked` — which is a field on a core event, so the log, the
serializer and `Replay(log) == State` all have to carry it — a magnitude on `ResolutionRecord`,
which round eighteen deliberately kept to sets, and five separate sentence grammars between them.
That is a round of its own. An excess clause that silently reads zero is worse than an unread line,
and eight cards is not worth risking one.
### Round twenty: a shield round a source of your choice

**17,757 → 17,789 complete cards, +32, none lost, measured by set difference.** Coverage 54.3% →
54.4%. The per-card compiled-effect diff moved 80 rows, of which 32 are the cards gained, 47 are
the two new default-false fields printing in the dump, and one — Ajani's Aid — reads a line it
could not read before and is still short of a second.

The Circles of Protection, and the last shape of prevention the compiler had no answer to. The
family was measured at 119 cards across four rows; the row with a mechanism-shaped answer was "the
next time *a source of your choice* would deal damage", **33 sole blockers**, and building it also
took the twelve cards printing "prevent all damage *a source of your choice* would deal this
turn" — which the census had filed under the two filter rows. Two rows, one question.

**It is a question, so it is an event plus a `ChoiceKind`.** `AddChosenMana` is the model and this
follows it exactly: `PreventDescribedDamage` with `ChooseSource` set emits a
`DamageSourceChoiceRequested` instead of a shield, the settle sweep raises
`ChoiceKind.ChooseDamageSource`, and the answer is a `ChoiceMade` in the log like any other — so a
replay reaches the same offer rather than needing a continuation the log cannot rebuild. The
alternative, answering inside `Resolve`, is not available at all: a resolution is never stopped
half way through, and there is nothing in the sentence to answer from.

**The shield rides on the event, whole but for its source.** Everything the sentence settled — what
it shields, which damage it watches, whether the first use ends it — was worked out while the spell
resolved; a shield rebuilt when the answer arrives would be rebuilt from a board that has moved.
Only `PreventionEffect.Source` is left empty, and it is the one field the answer fills. The menu
rides on it too, for the reason the mana menu does.

**An empty menu makes no shield, and that is the whole of the safety argument.** A prevention
effect with an empty source slot means *any source*: the failure mode of dropping this question is
not a card that does nothing, it is a card that fogs the table, and coverage would score it as a
win either way. So the request is the only thing that arm of `Resolve` returns, an answer off the
menu is refused rather than partly honoured, and
`A_source_choice_with_nothing_to_name_makes_no_shield` proves it with a life total that goes down.
Every test in the section shows damage **arriving** in a control case; a prevention read too
broadly is the one defect this family produces that looks exactly like success.

**"The next time" is a use, not a duration** (CR 615.8). `PreventionEffect.OnlyOnce` is a count of
*events* and is not `Amount`, which is CR 615.10's per-event cap and never runs out. The two are
independent and both are printed: "the next time a source of your choice would deal damage to you
this turn" against Pay No Heed's "prevent all damage a source of your choice would deal this turn"
are the same shield round the same chosen object, and the only difference is whether it survives
its first use. Read without the flag, a Circle of Protection is blanket immunity to a whole source
for the turn — a strictly better card than the printed one. The end is an event,
`PreventionEffectSpent`, emitted by the replacement that prevented the damage and in the same
batch: state is a fold of the log, and "the Circle has been used" is not derivable from the damage
event. It is spent on damage it *prevented* rather than on having applied, which is what keeps
CR 615.12's "shields won't be reduced by damage that can't be prevented" true — the unpreventable
guard already sits above both arms.

**"Of your choice" is reported, not answered — exactly as "other" is.** `PreventSource` hands the
word back as a flag, and the two static readers in `CardCompiler` *refuse* it rather than dropping
it: a permanent is not resolving and has no moment at which to ask, and the sentence with the words
removed is a permanent that shields against every red source on the table for as long as it is on
the battlefield. `A_prevention_of_your_choice_is_read_as_a_one_shot_and_refused_as_a_static`
asserts both directions.

**CR 615.9 costs nothing extra.** The filter is kept on the shield as well as used to build the
menu, so the properties are rechecked when the damage would happen; a source that has stopped being
red is not prevented and does not spend the shield. `Preventions.Watches` already asked both
questions. The one-answer case goes through a forced arm beside the mana colour's (CR 118.3): a
question with one button on it is not a question, and settling it still makes the shield.

**The invariant suite caught the half nobody was playing.** Aiming a shield at a *victim* was new:
the only targeted prevention before this named a **source**, and CR 609.7a says a source is never a
player, so `PreventDescribedDamage` had never been offered an "any target" spec at all.
`Every_effect_aimed_at_any_target_answers_for_a_player` asks of every effect that can be aimed at
one whether anybody has read what it does with a player, and it failed the moment the new reader
made that arm reachable. The arm was in fact correct — the chosen victim goes into
`PreventionEffect.Player` and `Preventions.CoversPlayer` is what the `PlayerDamaged` half of the
replacement pass asks — but nothing played it, and "an effect whose `Resolve` returns `[]` on an
input its own grammar admits is indistinguishable from a working one" is the lesson this file
already records from the first prevention shield. It is now played by
`A_chosen_source_shield_aimed_at_a_player_covers_that_player_alone`, with the other player as the
control: a shield that lost which player it named would cover her too, and every other assertion in
that test would still pass.

**What is refused on this row, re-measured: 28 sole blockers**, and only two of them are about
prevention.

- **8 redirect rather than prevent** — Aegis of Honor, Beacon of Destiny, Eye for an Eye, General's
  Regalia, Jade Monolith, Nova Pentacle, Reflect Damage, Shaman en-Kor. The shield is not the verb.
- **9 carry a rider sized by the damage prevented** (CR 615.5) — Awe Strike, Bone Mask, Cho-Arrim
  Alchemist, Deflecting Palm, Honorable Passage, Intervention Pact, New Way Forward, Reverse
  Damage, Shadowbane. The amount prevented is not carried anywhere a later sentence can read it,
  and the pattern anchors at "prevent that damage" so the rider cannot fall off a card that would
  then look implemented.
- **3 name a characteristic the permanent remembered** — Story Circle, Prismatic Circle and Circle
  of Solace read "the chosen color"/"the chosen type" off an as-enters choice.
- **2 have an activation cost the compiler cannot read** (Penance, Seasoned Tactician), and the
  prevention sentence itself now reads on both.
- **1 each**: a keyword in the card-filter vocabulary (Circle of Protection: Shadow wants "a
  creature with shadow"), an Aura's host as the victim of a one-shot (Kithkin Armor), half the
  damage rounded down (Dark Sphere), a pronoun in the source slot (Dazzling Reflection), a
  condition in front of the shield (Rhystic Circle), and one that is not a prevention at all
  (Desperate Gambit).

**The two filter rows were re-measured and are not one gap.** 92 cards still print "prevent all …
damage" as their only unread line, and the biggest coherent group in them is **23 that filter on
combat state** — "attacking creatures you control", "creatures it's blocking", "unblocked
creatures". That is the same capability the previous round wrote down and it has not gone stale:
`PermanentFilter` and `SourceFilter` are `SearchFilters` ids asked of a *printed card*, and these
want computed characteristics and the combat state. Behind it, 14 want a filter over what the
source is (power bounds, "creatures without trample", "creature tokens"), 8 want a colour named,
remembered or compared, 7 carry the CR 615.5 rider, 5 name a spell on the stack as the source, 4
lead with a condition `BoardConditions` cannot read, and 3 take a variable number of targets. The
rest are one-offs. There is no second convergence in there of the size this round found.

### A filter that asks the permanent instead of the printed card (CR 613)

The row above predicted this one: `PermanentFilter` and `SourceFilter` were `SearchFilters` ids
asked of a **printed card**, and fourteen prevention cards wanted a filter over what the source
*is*. That was the small half of the problem. `SearchFilters.Matches` took a `CardDefinition` and
nothing else, and it is the vocabulary shared by search, hand filters, graveyard counts, cost
modifiers, casting restrictions, splice and the chosen costs — so **every filter in the compiler
that named a permanent was blind to the board**. A creature that gained flying this turn was not
"a creature with flying"; a 2/2 pumped to 5/5 was not one "with power 4 or greater"; and no
creature anywhere could be told to carry a +1/+1 counter, because a card has no counters to read.

**The vocabulary was not forked, and that was the whole design problem.** A filter is asked in
places where there is no permanent to ask about — a card in a library, a hand, a graveyard — and
for those the printed answer is not a compromise, it is correct: CR 613 orders continuous effects
on permanents and none of them reaches a card in another zone. So `Matches` reads a `Subject`,
which is either a printed card or a permanent's `ComputedCharacteristics`, and every word of the
grammar reads off that without knowing which it was handed. The two forms cannot come apart the
way a second grammar over computed characteristics would have.

Three qualities the board decides joined the vocabulary, and the compiler learned to read them off
"X with Y", "X without Y" and "X with no Y":

- `keyword:<name>` — **folded out of the `KeywordAbility` enum rather than listed beside it.** The
  compiler's grantable-keyword table has twice been found narrower than the enum it describes, and
  both times the missing word was silent; `FirstStrike` is written `first-strike` and nothing has
  to remember it.
- `power>=N` / `power<=N`, and the same for toughness — CR 613.4's number, not the one in the
  corner of the card.
- `counter:<kind>` — the only quality here that a card can never carry at all.

The noun and the quality are joined with the ampersand the vocabulary already has, so nothing
downstream knows the reader exists. A noun that reads as an *alternation* is refused instead: the
bar binds looser than the ampersand where the filter is read, so "artifact|creature&keyword:flying"
would mean any artifact at all, and a precedence a string cannot spell is one to refuse.

**CR 613.8's hazard is real here** — a filter asked from inside the layer loop that computed a
second permanent would recurse without bound, which is exactly what overflowed the stack when
`ControllerOf` was first written as `Of`. It is answered the same way: a nested ask falls back to
the printed card rather than looping, the way CR 613.8b breaks a dependency loop it cannot order.

**Measured.** 8 cards completed — Scarecrow, Tresserhorn Skyknight, Tanglesap, Al-abara's Carpet,
Fog of War, Vine Snare, Hindervines and Circle of Protection: Shadow, which is every card the
previous round's row named. **89 already-complete cards changed behaviour**: 43 carry a prevention
shield whose filter is now asked of the permanent, 20 carry a static shield of the same shape, and
27 print a cost that sacrifices or returns a permanent answering a filter — "sacrifice a creature"
could not be paid with an animated land, and now can. The compiled-output diff over the whole
corpus is exactly those 8 rows and nothing else.

**Two things this did not reach, and both are the same limitation.** The static-shield path passes
`EmptyAbilities.Instance` into `Preventions.Watches`, so on those twenty cards the computation
gathers no continuous effects at all — counters and the face-down rules still apply, a granted
keyword does not. And a *spell on the stack* is still asked of its printed card, deliberately:
the object is a card, and the readers that describe one say so.

### A shield that computed characteristics with no abilities to compute from (CR 613)

The round above made `SearchFilters.Matches` ask the permanent instead of the printed card and
named the one path it would not widen blind: **the static-shield family passed
`EmptyAbilities.Instance` into `Preventions.Watches`/`Covers`**, and the layer walk gathers its
candidates *through* that source. With an empty one it gathers none. Counters and the face-down
rules still applied — those are read off the object rather than produced by an effect — and
nothing else did. So "creatures with first strike" meant creatures with first strike printed on
them, and Tresserhorn Skyknight read the wrong half of the board with an Aura sitting next to it.

The same argument reaches the controller. `Bind` asks `Characteristics.ControllerOf` so that a
stolen shield follows the theft (CR 613.1b), and that reader gathers control effects through the
ability source too — so asked with an empty one it always answered the stored controller, which
is where control *started*. The fix for the stolen-lord defect was in place and could not fire.

**The widening is the recorded one and it is the whole of the change.**
`ReplacementEffectDefinition.Applies` now takes an `IAbilitySource` as its third argument, the
way `Branches` already did. Thirty-two construction sites in the engine gained a parameter and
thirty of them ignore it; fifteen more in the tests; `Game` passes the game's own source at both
call sites. `Replace`, `Decline` and `Branches` are untouched.

**Where an empty source is right, and it is not a detail.** A *continuous* effect's `Applies`
runs from inside the layer walk, and a board condition asked from there must not compute anything
else's characteristics — that is CR 613.8's hazard, and the two sites that pass `EmptyAbilities`
inside one (`while:`'s conditional wrapper and living metal) are deliberate and stay. A
*replacement's* `Applies` runs from the replacement loop, outside any computation, and there the
same argument says the opposite. The line between the two is which side of the layer walk the
predicate is on, and it is the only thing that decides it.

**It cannot recurse.** The two readers the shield reaches are each bounded the way CR 613.8b
bounds a dependency loop it cannot order: a nested filter ask is answered from the printed card,
a nested control ask from the stored controller. Both fallbacks were **neutered in turn** to see
whether the new tests reach them, and they do not — no continuous effect in the engine today
asks a board filter or a control question about a permanent other than the one being computed
from inside its own predicate. They are a guarantee about a shape one card away, not a behaviour
anything reaches. Worth knowing before someone writes the card that gets there.

**Measured: no card completes and 24 already-complete cards change behaviour.** The complete
count is 18,144 before and after, and the per-card compiled-effect diff over the whole corpus is
**byte-identical** — which is the honest reading of that instrument rather than a null result: a
predicate is a closure and the diff cannot see inside one. 43 complete cards carry a static
shield; **21** name a filter the board decides on one side of the sentence or the other, and
**13** name a player scope, which is the half `ControllerOf` answers. Twenty-four distinct cards
are in one set or the other.

The filter twenty-one: Argothian Pixies, Argothian Treefolk, Armored Transport, Artifact Ward,
Blessed Sanctuary, Bubble Matrix, Champion Lancer, Crystal Barricade, Dolmen Gate, Goblin
Furrier, Indentured Oaf, Inner Sanctum, Light of Sanction, Mark of Asylum, Rescue Retriever,
Statecraft, Tajic Legion's Edge, The Wanderer, Tresserhorn Skyknight, Uncle Istvan, Wall of
Vapor. The scoped thirteen add Glacial Chasm, Personal Sanctuary and Solitary Confinement.

**The pattern was not in one place, and the sweep found two more.** Both are board conditions on
a replacement's `Applies`, and each was one token: bloodthirst's "an opponent was dealt damage
this turn" (19 complete cards) and enters-tapped's `unless` clause (4 — Barad-dûr, Mines of
Moria, Rivendell and The Shire, all reading "unless you control a legendary creature").
Bloodthirst's condition arm ignores its ability source outright, so those nineteen are corrected
for uniformity and change nothing today; the four lands read a controlled-permanent count that
does use it.

**What is still asked with an empty source, and why it was left.** The damage-amount
replacements — Furnace of Rath and its 36 relatives — read their dealer and victim through local
delegates typed `(GameState, GameObject, ObjectId)`, so threading abilities there means widening
two more signatures and measuring a different card family. `Replace` and `Decline` still take no
ability source at all; no reader inside them needs one yet. Both are this same fix, one family
further out.
### Round twenty-one: the durations, and how much of the decline was really a duration

Seven agents across two rounds declined work in six unrelated families for what looked like one
missing concept — a window the engine could not express. Measured corpus-wide against the
compiler's own text, with a substitution control (rewrite **only** the duration clause into a
duration that already reads, and recompile) and a line control (drop a *different* unread line
from the same card), the six families are not one population and four of them are not about a
duration at all.

| unread lines naming a window | lines | cards | sole blocker | excision | line control | **substitution** |
|---|---:|---:|---:|---:|---:|---:|
| `until your next turn` | 121 | 119 | 64 | 56 | 0 | **5** |
| `until the end of your next turn` | 63 | 62 | 42 | 39 | 0 | **1** |
| `until <its controller>'s next untap step` | 59 | 59 | 53 | 52 | 0 | **1** |
| `for as long as …` | 177 | 175 | 128 | 126 | 0 | **14** |
| `until ~ leaves the battlefield` | 68 | 68 | 49 | 45 | 0 | **1** |
| `the next spell you cast this turn` | 14 | 14 | 12 | 12 | 0 | 0 |
| `as though it had flash` | 54 | 53 | 41 | 39 | 0 | 0 |
| `once each turn` | 193 | 190 | 143 | 142 | 0 | 0 |
| `without paying its mana cost` | 232 | 232 | 167 | 164 | 0 | 0 |
| an attack tax carrying a duration | 23 | 23 | 15 | 15 | 0 | 0 |
| **any unread line naming a window** | **432** | **424** | **300** | **287** | **0** | **21** |

**Excision over-counts this family by 13.7×** — 287 against 21 — because dropping the line takes
the sentence's verb with it. The line control is 0 everywhere, which is what makes the
substitution number worth reading. (A naive control that drops any line from any multi-line card
completes 1,340 and measures nothing: the control has to be a *different unread* line on a card in
the family.)

**The honest ceiling for the whole concept is 21 cards, and four of the six declined families are
worth nothing to it.** The flash windows, the free casts, the once-per-turn static and the attack
taxes complete **0** when their duration is rewritten into one that already reads. Those seven
refusals were right, but the blocker they each named was not the duration: for the taxes it is
that a floating tax has nowhere to live at all, at any duration, and for the flash and free-cast
families it is the permission itself. Nothing below widens any of them, and the count to carry
forward for them is zero-from-a-duration rather than 9, 7, 6 and 3.

#### Three of the four are readings, not shapes

The engine already told `until end of turn` (CR 514.2, the cleanup of the turn that made it) apart
from `until your next turn` (CR 611.2b, that player's next untap step) on a floating effect. What
it could not do was **read the second off a printed line**: every group reader spelled "until end
of turn" out literally, so five cards whose whole sentence was otherwise understood — Bond of
Revival, Kardur's Vicious Return, Mouth of the Storm, Song of Freyalise, For the Common Good —
were refused over three words. One shared `DUR` fragment now serves the mass pump, the mass grant,
the pump-and-grant, both quoted-ability grants and the reanimation tail, and the same group name
is used at both ends of a sentence because .NET collects the two positions into one group.

`until its controller's next untap step` is the same moment read around a *different player*, and
on Orcish Farmer's card the two are hardly ever the same person. The player is read when the
effect resolves and kept as an identity, for the reason a prevention's source is: control is layer
2 and moves, so a duration re-derived from the permanent later would follow it to a new controller
and end on the wrong turn. A subject that cannot be read produces **no effect at all** rather than
one with no duration — an unreadable window must not become permanent.

`for as long as you control ~ and ~ remains tapped` is one duration with two questions in it, and
had to be a fourth `ControlHeldWhile` rather than the first clause with the rest dropped. Read as
`Controlled` alone, Rubinia Soulsinger keeps what she took when she untaps, which is a strictly
better card than the printed one; read as `Tapped` alone the thief could itself be stolen and go
on holding it. The tail's conjoined alternative is written first because .NET alternation is
leftmost-first.

#### The fourth is a shape, and it is a whole turn from either neighbour

`until the end of your next turn` is neither of the other two. It runs through the cleanup step
that ends "until end of turn", through the untap step that ends "until your next turn", and stops
a turn after either — the difference between a creature you get to attack with and one you do not.
`FloatingEffect.UntilEndOfTurnOf` is the field, stored as the player it is read around **plus** the
turn it began on, never as a deadline: whose turn comes next depends on the turn order and an
extra turn taken in between would move a stored number. The turn it began on is what lets the
cleanup of *this* turn pass when this turn is already that player's, and the cleanup sweep asks
for a strictly later one. It is the same shape `GameObject.MayPlayThroughOwnersNextTurn` already
uses for the exile play window, and the ending is an event, so `Replay(log) == State` holds.

#### Result

**18,293 → 18,306 complete cards, +13, none lost.** Diffed as a set and again as a per-card
fingerprint of every compiled spell, trigger, activated ability, static and unread line. Once the
new records' print-only fields are normalised away, the effect diff is **15 cards**: the 13 that
became complete, plus Elspeth, Storm Slayer and Wyll, Pact-Bound Duelist, which each gained the
duration clause and are still short on another line. **No card that was complete before changed
what it compiles to.**

The thirteen are Bond of Revival, Kardur's Vicious Return, Mouth of the Storm, Song of Freyalise,
For the Common Good, Emeria's Call, Karvanista, Loyal Lupari, Behold the Unspeakable (the five the
substitution named, plus three more the shared fragment reached), Helm of Possession, Rubinia
Soulsinger, Willow Satyr, Orcish Farmer and Power of Persuasion.

#### What is still declined, with the count behind it

- **`for as long as …` on a verb that has no held tail — 10 cards.** Rewriting the tail into one
  that *reads* completes 4; rewriting it into `until end of turn` completes 14. The difference is
  the verb, not the duration: `becomes a 4/5 green Treefolk creature`, `becomes an artifact
  creature with base power and toughness 5/5`, `loses all abilities`, a quoted-ability grant, `you
  may play that card`, and `all creatures get +2/+2` all refuse the tail the four verbs above
  accept. That is a held-tail-for-more-verbs job and not a duration one.
- **`for as long as that creature is enchanted` — 1 card** (Rootwater Matriarch). The condition is
  about the *affected* object rather than about the effect's source, which is a different subject
  from any the `while:` id can carry.
- **`until ~ leaves the battlefield` — 0 on its own.** It is `for as long as ~ remains on the
  battlefield` spelled the other way and worth nothing until the `becomes` verbs take a tail;
  Graceful Antelope, its only sole-blocked card, needs both.
- **The four families that are not duration work at all — 0 each**, as measured above.
