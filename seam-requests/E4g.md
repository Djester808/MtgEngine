# E4g — every question kind has a constant, and a literal now fails a test

Wave 12. No seam change asked for: everything landed inside `MtgEngine.Rules/Choices/**` and
`tests/MtgEngine.Rules.Tests/ChoiceResumptionTests.Kinds.cs`. Rules suite 785 tests, all green.

## What is there now

`ChoiceKinds` moved out of `Choices/Resumption.cs` into `Choices/ChoiceKinds.cs` (same namespace,
same public shape, nothing to change at any call site) so the sweep below has one file to exempt.
Every kind carries a doc comment naming its CR rule, the subsystem that raises it and the one that
finishes it. `ChoiceSystem.RuleFor` now cites a rule for each of them instead of falling back to
CR 101.4, so a refused answer says why.

Added, none of which had a constant: `Unless` (`"unless"`), `MayPay` (`"may-pay"`),
`MayCast` (`"may-cast"`), `CastCost` (`"cast-cost"`), `Mode` (`"mode"`),
`Sacrifice` (`"sacrifice"`), `Surveil` (`"surveil"`), `NewTargets` (`"target"`, CR 115.7 —
**not** the same kind as `Targets`/`"targets"`, CR 601.2c) and `RingBearer` (`"ring-bearer"`,
CR 701.54a). E9j § 5 asked for the first four; the sweep found the other five, which no grep for
the four had turned up.

## The sweep, and what it will and will not catch

`ChoiceResumptionTests.Every_choice_kind_spelled_in_the_rules_engine_is_a_declared_constant` walks
up from `AppContext.BaseDirectory` to the repository root, reads every `.cs` of
`MtgEngine.Rules/` except `ChoiceKinds.cs` (and `bin`/`obj`), and fails naming any kind spelled as
a literal that is not a declared constant's value. It counts a literal as a kind when it is
kind-shaped (lower case, hyphens) **and** either the next literal in the same statement is a prompt
(a sentence with a space in it — every raise site puts the prompt straight after the kind) or it
sits in a comparison against something named `…Kind`. That excludes verbs, option ids and context
keys, which is why `"countered"`, `"destroyed"`, `"yes"` and `"modes"` do not fail it.
`ChoiceResumptionTests.The_kind_sweeper_finds_a_literal_kind_and_not_a_prompt_or_an_option_id`
checks the sweeper itself against a snippet written in the test, so it keeps saying something after
the tree's literals become constants.

**It passes on a literal that equals a constant's value** — the point is that a kind is declared,
not where it is spelled — so E9k's interpreter work and the conversions below can land in any
order.

## Owed by other folders (a literal, or a second constant for the same kind)

Each of these is now a duplicate spelling of a `ChoiceKinds` constant. None breaks anything today;
each is one edit in a folder I do not own.

- `MtgEngine.Rules/Interpreter/` — `"discard"`, `"sacrifice"`, `"scry"`, `"surveil"`, `"search"`,
  `"may"`, `"unless"`, `"may-pay"`, `"may-cast"`, `"value"`, `"mode"`, `"target"`, `"ring-bearer"`
  at the `Ask`/`Look` sites, and `private const string CastCost = "cast-cost"` in
  `EffectInterpreter.Retarget.cs` (E9k).
- `MtgEngine.Rules/Turn/TurnSystem.cs:321` — `"discard"` → `ChoiceKinds.Discard`.
- `MtgEngine.Rules/Triggers/TriggerSystem.cs:26` — `public const string OrderChoice =
  "trigger-order"` → `ChoiceKinds.TriggerOrder` (it is public, so check its readers first).
- `MtgEngine.Rules/Sba/StateBasedActions.cs:38` — `private const string LegendRule =
  "legend-rule"` → `ChoiceKinds.LegendRule`.

## One behaviour change to know about

`UnbuiltResumable`'s refusal message reads "That may-pay question is one a card asked, and
answering one is not built yet." (was "That may-pay is a question a card asked…"). It carried a
`?? "question"` fallback that the sweep would have read as a kind; the rule cited is still
CR 101.4 and nothing asserts the string.
