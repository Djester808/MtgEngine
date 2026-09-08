# E9t — a keyed retarget plays; one ask to the query

Wave 25. Owns `MtgEngine.Rules/Interpreter/**` and `tests/MtgEngine.Rules.Tests/InterpreterTests*.cs`.
Answers P23 § 2 (`30ec73ad` Imp's Mischief plays as nothing).

## What was measured

`InterpreterTests.Wave25.cs` builds the card's exact shape through the real query and the real zone
system — `Sequence [ ChangeTarget { What: Target { Filter, Key: "k1" } }, LoseLife { ManaValueOf
{ Remembered "k1" } } ]` resolving with a three-mana spell announced as its target:

- With `Filter = ObjectFilter(In: Stack, Spell: true)` **both halves already work**. The keyed
  announcement is remembered by `RememberTargets` (CR 601.2c), read back by `Objects(run,
  change.What)`, the CR 115.7 question is asked, the answer writes `StackTargetsChanged`, and after
  the resumption `ManaValueOf(Remembered "k1")` reads the stack object's printed mana value
  (CR 202.3) — life 20 → 17. Two tests, both green.
- With the card's own `Filter = ObjectFilter(Spell: true)` — no zone — **nothing happens at all**,
  exactly as P23 reports: no question, no event, life 20.

So neither half of P23 § 2 is the interpreter's. There is one defect, and it is in the query.

## The ask (owner: `MtgEngine.Rules/Query/**`, E5j this wave) — CR 112.1

`Query.cs:21`

```csharp
private static Cards.Zone ZoneOf(ObjectFilter filter) => filter.In ?? Cards.Zone.Battlefield;
```

`Matches` rejects any candidate whose zone is not `ZoneOf(filter)` before it reads anything else, so
a filter that says `Spell: true` and names no zone can never match anything: **CR 112.1 — a spell is
a card on the stack**, and the default sends the filter to the battlefield, where `IsSpell` is false
by construction. `ObjectFilter(Spell: true)` matches the empty set today.

That is the whole of Imp's Mischief. The CR 608.2b re-check (`EffectInterpreter.Targets.cs`,
`LegalTargets`) runs the selector's own filter over the announced target, finds the spell illegal,
and so remembers the empty list under `"k1"` — which is the correct reading of CR 608.2b for a
target that really is illegal, and is why the interpreter must not paper over it. Every later step
then reads that empty key: the retarget finds no spell (CR 115.7) and `ManaValueOf` sums nothing.

**Asked for:** when a filter says `Spell: true` and names no zone, the zone it names is the stack.

```csharp
// CR 112.1: a spell is a card on the stack, so a filter that says "spell" and names no zone
// names the stack; every other filter still reads the battlefield (CR 110.1).
private static Cards.Zone ZoneOf(ObjectFilter filter) =>
    filter.In ?? (filter.Spell == true ? Cards.Zone.Stack : Cards.Zone.Battlefield);
```

Narrow on purpose: `Spell: false` with no zone keeps reading the battlefield (a permanent is not a
spell there, so the arm at `Query.cs:162` still admits it), and every filter that names `In`
explicitly is untouched — `c20a96f7` Bolt Bend writes `In: Stack` and keeps verifying. Only
`Spell: true` with no zone changes, and it changes from "matches nothing, ever" to the rule.

## It is wider than one card, and two Verified cards may be resting on it

**32 card files** carry an `ObjectFilter` with `spell: true` and no `in`. Two of them are badged
**Verified**: `3407fe41` Negate (`{notTypes:[Creature], spell:true}`) and `0826cf7d` Muddle the
Mixture (`{anyTypes:[Instant,Sorcery], spell:true}`), both landed by P23 § 3. A `Counter` reaches
its target down the same road — `Objects(run, counter.What)` over the key `RememberTargets` filled
from the CR 608.2b re-check — so the same filter sinks it, and the green
`A_spell_filter_that_names_the_stack_is_read_by_the_re_check` is that road with `In: Stack` on it.
**Not measured in this box** (the time box ended first): whether the two smoke scenarios pass anyway
because the *resolving* counterspell reaches its own owner's graveyard under CR 608.2m whatever it
countered — that assertion would hold over a counter that did nothing. Worth one probe from the
P-series, which has the corpus; this worktree has none. The one query change below fixes all 32 card
files at once and nothing needs re-translating.

`Spell: true` with no `In` in `cards/`: `30ec73ad` is the one P23 § 2 names, and 31 more read the
same filter.

## When it lands

Flip the two `In: Zone.Stack` filters in `InterpreterTests.Wave25.cs` (`KeyedRetargetAndPay`) to the
card's own `ObjectFilter(Spell: true)` and they must stay green; then
`ScenarioStageTestsWave24.A_retarget_that_keys_its_target_gets_no_scenario` (P23 § 2) flips to a play
and `verify cards --only 30ec73ad-c7a1-4527-8dc6-44bdd65b9ba6` should pass.
