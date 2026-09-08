# E3q — a choice between costs, the storm copies, and the tax's entry point

Wave 28. Owns `MtgEngine.Rules/Casting/**`, `tests/MtgEngine.Rules.Tests/Casting*.cs`. Anchor
`Casting`: **150 green**, up from 141; nine new tests in
`tests/MtgEngine.Rules.Tests/CastingTests.Wave28.cs`. `dotnet format --verify-no-changes` clean on
`MtgEngine.Rules` and `tests/MtgEngine.Rules.Tests`. Bitter Triumph (`776341cb`) **verifies**
through the real runner (`verify cards --only`, engine `9781a597f8c8ddfa`).

## 1. Done — CR 601.2b: an additional cost that is a choice between costs

`ChooseCost` had no arm at all, so `CostPayment.One` fell through to its default and refused
"A ChooseCost cost cannot be paid yet (601.2h)". It is now paid, and the choice is made where CR
601.2b puts it — at announcement, before anything is paid (CR 601.2h) — so no payment is suspended
and nothing new reaches the command seam.

- `CostContext` gained `Choices` (the command's `CostChoices`) and the word `ChooseCostChoice`
  (`"cost"`, `"cost:1"`), beside `PayWithLife`. It is parsed by `CastPlan.Names`, the reader `"alt"`
  and `"addcost"` already share; that method went from `private` to `internal` so there is one
  spelling of the `word:index` shape and not three.
- `CostPayment.Chosen(state, context, choose)` is the one reading of which option is being paid:
  the index the command names, else **the first option that can be paid**, in printed order. An
  index the cost does not offer is refused with CR 601.2b; a choice no option of which can be paid
  is refused with CR 601.2h and nothing is taken.
- The probe that finds the first payable option pays against `context with { Chosen =
  context.Named.Rest() }` — a new `ChosenObjects.Rest()` that copies the position rather than
  advancing it, so trying an option the payer will not take cannot move where the costs *after* it
  read the command's objects from (CR 601.2c). The probe's state is discarded; only the chosen
  option is paid, once.
- `CastingSystem.CostChoices` (the question channel) descends into a `ChooseCost` through the same
  `Chosen`, so what the question offers and what the payment accepts can never disagree. An offer
  no option of which can be paid asks nothing rather than silencing the whole question — `Check` is
  what refuses.

**Why Bitter Triumph now plays, and it is not the discard.** "Discard a card" over a hand of four
is a real choice, and CR 601.2c's rule in this engine is that no object is ever picked for the
player, so `ObjectCosts.Choose` refuses it when the command names no card. The life payment can be
paid, so first-payable takes it: A goes to 17, its graveyard holds only the spell, and the
generated expectation (`ZoneCount A Graveyard 1`) is met. A command that names a card to discard
takes the discard instead — tested both ways.

## 2. Flusterstorm (`86bf58f2`) — the copies are **not** made, and the casting half is already right

Asked: are the storm copies made, and does each copy ask its own unless?

**No copies are made.** `KeywordKind.Storm` is in `MtgEngine.Rules/Keywords/KeywordSemantics.Unsupported.cs`'s
table (beside `Gravestorm`), so the `KeywordAbility` on the face has no semantics: nothing watches
the cast, nothing counts the spells cast before it this turn (CR 702.40a) and nothing copies the
spell. The card therefore plays as an ordinary "counter target instant or sorcery spell unless its
controller pays {1}", and the question "does each copy ask its own unless" does not arise yet —
there is one object, and it asks once. When the copies do exist, each is a separate spell on the
stack (CR 707.10) and each resolution runs the interpreter's `Unless` arm on its own, so the
per-copy question needs nothing from this folder.

**The casting half is not the defect.** E3p's `PayForRule`/`CanPayForRule` and the coordinator's
`Unless` arm landed in wave 27 and Mana Tithe — the same shape without storm — verifies. Nine of
this file's tests stand on that entry point.

**Ask (Keywords/** with Interpreter/**): CR 702.40.** Storm wants a triggered ability on the cast
that copies the spell once for each spell cast before it this turn, with new targets allowed for
the copies (CR 702.40a with 707.10). It needs a per-turn count of spells cast (the wave-25
per-turn counts are the likely reader) and the copy road the interpreter already has for
"copy target spell". Until it lands, the honest state of the card is one ability expressible and
one not — a marker, not a silent pass.

**Observation for the coordinator, not an ask.** The failure text is `Life was 19`, and
**the scenario stored in the card file carries no `Life` expectation at all** (its four are
`InZone`, `InZone`, `ZoneCount`, `StackEmpty`) — so the verify run is not running the stored
scenario, it is running a regenerated one, and the current smoke generator
(`MtgEngine.Cards.Pipeline/Scenarios/SmokeBoard.Build.cs:135`) emits `Life(caster, 20 + delta)` for
this definition while some player ends the game at 19. The card file on disk still reads
`"status": "Verified"`, `passed 1/1` from run `verify-20260908T042913-3e3c94`, and the failing run
did not overwrite it. Whichever of those two is wrong is the pipeline's (P-series): either the
regeneration is producing an expectation the definition does not warrant, or a Verified file is
being kept over a fresh failure. Nothing in `Casting/**` produces or reads a `Life` expectation.

## 3. Done — the entry point the combat tax will call (E3p § 3 with E4i § 2)

`CastingSystem.PayForRule`/`CanPayForRule` are on `ICastingSystem` as default members
(`Engine/Seams.cs:140`, landed by the coordinator in wave 27). This wave asserts them from the
**caller's** side, which nothing had:

- a cost that is **not mana** and needs **no source** (`PayLife`) is paid for a player, the state
  handed in is untouched, and the trial answer changes nothing;
- a cost that wants a source it was given none of (`SacrificeSelf`) comes back as a `Refusal`
  naming CR 601.2h — **never a bare throw**, which is what combat needs, because an unpayable tax
  has to be reportable at CR 508.1j rather than crashing the declaration;
- `x` is threaded: `ManaCost("{X}")` with `x: 2` spends exactly two.

**Ask (Combat/** with Engine/**), unchanged from E3p § 3.** `CombatSystem` wants a settable
`ICastingSystem? Casting { get; set; }` assigned in `Engine/Game.cs` beside `interpreter.Casting`,
and `TaxSymbols`' refusal for a non-mana attack cost becomes `Casting?.CanPayForRule(...)` at the
CR 508.1h total and `Casting?.PayForRule(...)` at CR 508.1j's payment. Use `CanPayForRule` for the
"is this declaration legal" answer and `PayForRule` for the payment, so an unpayable tax refuses
the whole declaration (E4i § 1's reading) without a trial payment leaking state. A `Casting` left
null keeps today's refusal, so the wiring and the call can land in either order.

## Not done in this box

- **No test of a `ChooseCost` inside an activated ability's costs** beyond the shared code path;
  the cast road is what the corpus prints today. The arm is in `CostPayment.One`, so both roads
  reach it, and `OfActivation` passes the activation's `CostChoices` for the question.
- **Nothing outside my own files is committed.** `cards/776341cb-…json` and its `.lint` sibling were
  rewritten by the per-card verify run (status `Translated` → `Verified`); the guard refuses a
  `git checkout` of `cards/`, so they are left in the worktree unstaged and **not** in my commit.
