# E6i — the life an event moved, and a main phase that names neither half

Wave 26. Owns `MtgEngine.Rules/Triggers/**` and `tests/MtgEngine.Rules.Tests/Trigger*.cs`.
Both brief items are built, tested and green: 1074 Rules tests pass, 12 of them new, and 8 of the
12 fail when the two new bodies are neutralised (the other 4 are the guards — a named main phase,
a non-main step, whose turn it is, and a zero-delta life change — which hold either way and exist
so the widening cannot be widened further by accident).

## 1. The keys the firing carries — for E5k to read (CR 603.10)

**`TriggerSystem.TriggeringLifeGainedAmount = "triggering-life-gained"`**
**`TriggerSystem.TriggeringLifeLostAmount = "triggering-life-lost"`**

Both are `public const string` on `MtgEngine.Rules.Triggers.TriggerSystem`, beside
`TriggeringDamageAmount = "triggering-damage"`, and the readers must name the constant rather than
the literal. What is stored, so the reader knows exactly what it is parsing:

- The value is the **magnitude as a decimal, culture-invariant** — `Math.Abs(delta)`, so a loss of
  4 is `"4"` and never `"-4"`. A reader that expected a sign would make "draw that many cards" draw
  nothing.
- The key follows **the event, not the trigger**: a `LifeChanged` with a positive delta writes only
  the gained key, a negative delta only the lost key, and every other event writes neither. So the
  two amounts read two different events and **neither can fall back to the other**, which is the
  pairing L039 checks in the vocabulary. A firing on `DamageDealt` still writes only
  `TriggeringDamageAmount` — damage to a player is not read back as life lost here, because the
  life-loss half of that damage is its own `LifeChanged` event with its own firing.
- A `LifeChanged` with `Delta == 0` writes nothing (nothing fires either — CR 119.3).
- The channel is `TriggerSubject.Chosen`, the same one the damage amount and the tapped-for-mana
  colour use, so the value reaches the stack entry's `Chosen` and therefore the resolution's
  `EffectContext.Chosen`. **Read it exactly as `TriggeringDamage` does** (`Query/Query.Amounts.cs`
  line ~150: `ctx.Chosen.TryGetValue(...)`, `int.TryParse` invariant, `0` when absent) — 0 outside
  such a trigger, per the brief.
- A `DelayedTrigger` gets it too: the decoration is inside `TriggerMatching.Match`, which is the one
  road both a printed trigger and a delayed one take, so no separate wiring is needed.

## 2. `Step.Main` matches either main phase (CR 505.1)

`TriggerMatching.StepAdmits(wanted, began)` is the one place a step reference is compared to a
`StepBegan`: a named step matches itself, and `Step.Main` additionally matches `PrecombatMain` and
`PostcombatMain`. `AtStep(Main, Whose)` still asks whose turn it is the same way, so "your next main
phase" is still only yours. A `Once` delayed trigger fires at whichever main phase begins first and
`TriggerFired.Apply` spends it (CR 603.7b) — including the case the collective reading exists for,
an ability created during combat whose next main phase is the postcombat one.

## Asks

1. **E5k (this wave)** — the two readers, on the two constants above. Nothing else in the engine
   reads these keys today, so a reader that invents its own spelling reads nothing and answers 0
   silently.
2. **Turn system / any other step matcher (recorded)** — `StepAdmits` is private to
   `TriggerMatching` because nothing outside `MtgEngine.Rules/Triggers/**` is mine to change. A
   `SkipStepEffect(Main)`, a duration that ends "at the beginning of your next main phase", or any
   other reader of `Step.Main` outside the trigger system needs the same CR 505.1 reading, and there
   should be **one** public helper rather than three copies. Whoever owns the shared step vocabulary
   should lift it; the body is one line and the doc comment says what it is for.
3. **Coordinator (a citation divergence, not a defect)** — `[RuleRef("603.10")]` on both keys is the
   spelling `TriggeringDamageAmount`, `Amounts.cs` and V14 already use for "the firing carries its
   own event's number". The shipped text of CR 603.10 is the *look back in time* rule and does not
   say this; the reading itself is CR 608.2 with 119. Every site is consistent today, so this box
   did not diverge from it — but one pass should move all of them together (the same shape as the
   700.2 note in the wave-25 table), and the number should not be corrected in one file alone.

## Not done in this box

- `dotnet format --verify-no-changes` was **not run** — the 35-minute box closed on that command.
  Both new files were written CRLF, UTF-8 without a BOM, and the analyzer-enforced build
  (`0 Warning(s)`, warnings-as-errors, IDE0005/IDE0051 among them) is clean on both projects, so
  what is unverified is whitespace and ordering only. The coordinator should run it before the merge.
- No card in `cards/` was touched or searched: the two readings are engine behaviour, and the card
  that needs them is E5k's to verify once the amounts are readable.
