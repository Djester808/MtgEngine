# E3m — Phyrexian mana is paid with mana or two life (CR 107.4f)

## What landed, inside my box

`ManaSymbols` parses a Phyrexian symbol and the payer pays it. Nothing outside
`MtgEngine.Rules/Mana/**` and `MtgEngine.Rules/Casting/**` changed, and no seam type moved.

- `ManaSymbol.IsPhyrexian` rides beside `Colors` rather than replacing them, because CR 202.2
  reads the colours of a Phyrexian symbol for the card's colour and `Layers.ColorsOf` already
  does (it scans the printed characters, so it was right about `{G/P}` before this landed).
- `TryHybrid` reads a `P` half out of a slashed symbol, so `{G/P}` is one colour plus the life
  alternative and `{G/U/P}` is two. A bare `{P}` is still an unknown symbol: it names no colour
  and no card prints one.
- `ManaValue` counts a Phyrexian symbol as 1 before it reads the rest of its shape (CR 202.3d).
- `ManaSymbols.Pay` moved to `Mana/ManaSymbols.Payment.cs` (a partial of the same class) to keep
  both files under 500 lines; `ManaPayment` gained `LifePaid`.
- `CostPayment.Spend` passes the payer's life total in (CR 119.4 caps the life half at what the
  player actually has) and emits the `LifeChanged` beside the `ManaChanged`, so one payment is
  one outcome. A cast whose whole cost is Phyrexian and paid in life emits no `ManaChanged`.

## Which road the choice takes, and why

**The payment plan decides, and the command may override it.** CR 601.2h makes the choice the
payer's, per symbol, so both halves are here:

- By default a Phyrexian symbol takes one mana of its colour when the pool has it, and 2 life
  otherwise. This is what a generated scenario and every existing caller get for free.
- A command that puts `"life"` in its `CostChoices` pays every Phyrexian symbol in the cost with
  2 life instead (`CostContext.PayWithLife`, read by `CastingSystem.PaysWithLife` on both the
  cast and the activation paths). `CostChoices` is the list a command already carries for every
  other choice a cost needs, so **no seam type changed** — a per-symbol channel would have needed
  a new field on `CastSpell`/`ActivateAbility`, and no corpus card needs one. CR 119.4 still
  wins: an announced life payment the player cannot afford takes the mana rather than refusing a
  cost the pool can pay.

Two ordering rules make the plan find a payment whenever one exists (CR 601.2h forbids a partial
payment, so a payer that gives up early is a wrong answer, not a conservative one):

- A Phyrexian symbol sorts **after** every other restrictive symbol, because it has one more way
  to be paid — `{G/U}{G/P}` with one green pays the `{G/U}` with the green and the `{G/P}` with
  life, instead of the reverse, which would refuse.
- When the generic part then comes up short, a Phyrexian symbol **gives its mana back** and pays
  2 life — `{1}{W/P}` with one white mana and 20 life is payable exactly one way, and that is it.

Both are tested (`A_phyrexian_symbol_never_takes_the_mana_another_symbol_needs`,
`A_phyrexian_symbol_gives_its_mana_back_when_the_generic_part_needs_it`).

## The ask — the withheld generator half (pipeline owner, not mine)

`97cabeda` (Noxious Revival, `{G/P}`) now casts. Two things outside my box still withhold its
scenario, both named in `seam-requests/P20.md` § 3:

1. **`MtgEngine.Cards.Pipeline/Scenarios/SmokeScenario.cs:71`** — delete the arm

   ```csharp
   if (face.ManaCost is { Length: > 0 } phyrexian && phyrexian.Contains("/P", StringComparison.OrdinalIgnoreCase))
       return null;
   ```

   and the comment above it. Its stated reason ("the casting system refuses such a cost
   outright") is no longer true.

2. **`tests/MtgEngine.Pipeline.Tests/ScenarioStageTests.Wave19.cs:125`** — delete
   `A_phyrexian_cost_gets_no_scenario_until_the_casting_system_can_pay_one`, which asserts the
   arm above. Its own doc comment says to drop it with this seam.

`SmokeBoard.PaysExactly` needs nothing: a Phyrexian cost falls to the deep pool and asserts less,
which is still a true play — the deep pool has green, so Noxious Revival pays the colour there.
Nothing else in the repo reads the old refusal string; `Query/ManaCosts.cs` has its own character
scan for `"P"` and was already right.

## Gate

`MtgEngine.Rules` and `tests/MtgEngine.Rules.Tests` build clean and
`dotnet format --verify-no-changes` is clean on both. The Rules suite is **896 green, 0 failed**
(was 880 before this wave's box); 43 of them under the `ManaTests` anchor.
