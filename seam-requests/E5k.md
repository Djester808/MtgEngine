# E5k — wave 26: modified, "an opponent", the two life amounts, and one reading of CR 605.1a

Four items, all landed in `MtgEngine.Rules/Query/**` with eight tests in
`tests/MtgEngine.Rules.Tests/QueryTests.Wave26.cs` (65 in the `QueryTests` anchor, up from 57).
`dotnet format --verify-no-changes` clean on `MtgEngine.Rules` and `tests/MtgEngine.Rules.Tests`.

**The brief was not in my tree.** `.agents/briefs/wave26.md` does not exist in this worktree (there is
no `.agents/` directory at all after `git merge --ff-only card-engine`), so the four items are the
ones my launch message named, read against `seam-requests/V14.md` rows 2, 3 and 7 and
`seam-requests/R2.md` § 4. Nothing else was assumed about the wave.

## 1. `IsModified` — its rule is **CR 700.9**, not 122 and not 301.5

V14 row 3 cited it and the citation holds: CR 700.9 is the one definition, and it names three ways
to be true — one or more counters of **any** kind (CR 122), equipped (CR 301.5), or enchanted by an
Aura **that permanent's controller controls** (CR 303.4). Read in `Query.Predicates.Modified`, which
finds the attachments from the other end of the link (`GameObject.AttachedTo`), because a permanent
keeps no list of what is attached to it. Only a permanent is modified, so anything off the
battlefield answers no even when it carries counters (CR 110.1 with 122.1a) — a creature card in a
graveyard with two +1/+1 counters is the test for it.

**No ask.** The Aura's controller is read through `ICharacteristics.ControllerOf`, so a control-change
effect moves the answer without this reader knowing (CR 613.1a).

## 2. `AnOpponent` — existential inside a condition, refused everywhere else

`Query.Holds(Condition)` splits: a condition that names `AnOpponent` is asked once per opponent and
is true when **one** satisfies it (CR 102.2 with 103.1). The split is made at the innermost condition
that names it — the boolean combinators recurse through the same method — so `not (an opponent
controls a creature)` reads as "no opponent controls one" and not as "some opponent controls none".
`Query.Players(AnOpponent)` reads the binding and, with none, **refuses by name** (CR 102.2): a
filter, a selector or an effect reaching it has no opponent to act on, which is the engine's half of
L041.

The binding travels in `EffectContext.RememberedPlayers` under `"an-opponent:bound"` — the one place
the query layer already carries a player by name, and the colon keeps it apart from every key a card
can write (no vocabulary key contains one).

**Recorded, not asked:** `And(A(AnOpponent), B(AnOpponent))` is two splits and so admits two
different opponents. One printed line naming "an opponent" twice would want one opponent for both;
no card in the corpus prints it, and the fix if one appears is to hoist the split to the outermost
condition that names it while keeping the negation reading above. Say so before changing it — the two
readings differ only at three or more players.

## 3. The two life amounts — **the key strings are mine, and the trigger side has to agree**

`seam-requests/E6i.md` does not exist in my tree, and `TriggerSystem` has no constant for either
amount (it has `TriggeringDamageAmount = "triggering-damage"` and nothing else). So, as the brief
directed, I assumed the spelling and named it here:

| Amount | Key | Where the constant lives now |
|---|---|---|
| `TriggeringLifeGained` | `triggering-life-gained` | `Query.TriggeringLifeGainedAmount` (**public**) |
| `TriggeringLifeLost` | `triggering-life-lost` | `Query.TriggeringLifeLostAmount` (**public**) |

**Ask (trigger system, E6-series):** write those two keys into the firing's `Chosen` as decimal,
culture-invariant text when an ability triggers on life being gained or lost, and on a
`DelayedTrigger` the ability creates, exactly as `TriggerMatching.Wave23` writes
`TriggeringDamageAmount`. Reference the two constants above, or move them to `TriggerSystem` beside
the damage one and point the readers at them — either way the string is written in one place. Until
that side lands, both amounts read **0** rather than refusing: no life was gained or lost, which is
what the number then stands for, and it is what a copy of the ability made outside a firing means.

Neither reader ever falls back to the other or to the damage key. Life lost is not damage — a
payment, an edict's cost and a burn spell all lose life and only one deals any (CR 118.3 with 119.3)
— and that separation is the reason L039 exists; the test asserts all three directions.

## 4. One public reading of CR 605.1a — `MtgEngine.Rules.Query.ManaAbilities`

`ManaAbilities.Is(ActivatedAbility)` is the shared reading R1 § 2 and R2 § 4 asked for, and it checks
**all four** criteria: no target (CR 115.6, both `Target` and `TargetPlayer`), could add mana,
not a loyalty ability (CR 606.1), and **its cost and effect move no card to or from a library** —
the fourth, which none of the three copies checked. The library reading is the verbs that cross the
border with no zone written on them (`Draw`, `Mill`, `Surveil`, `Explore`, `Search`, `RevealUntil`,
`MillCost`, a `Shuffle` with `IntoLibrary`) plus any record naming `Zone.Library` in a `Zone`-typed
property, so a new record naming the library is caught before anyone remembers to add it to the list.
`CouldAddMana` and `AddsColor` are public beside it — the colour half `ProducesMana` asks.

The query layer now calls it; its own copy is deleted. **Asks:**

1. **Runner (R-series)** — `IsManaAbility`/`CouldAddMana` in the runner is copy two. Call
   `ManaAbilities.Is` and delete it. The number a generated scenario writes is computed there, and
   what resolves is decided by the casting system, so the two disagreeing is a wrong scenario that
   passes.
2. **Casting system (E-series)** — `CastingSystem.ManaAbilities` is copy three, and it is the one
   that decides what resolves without using the stack (CR 605.3a). Call `ManaAbilities.Is`.

An ability that adds mana and searches a library is a mana ability to all three copies today and is
not one. No corpus card in the failing set turns on it yet, which is why this is a correctness debt
and not a blocker — but the three copies are now two copies and one reading, and the point is to
finish that.
