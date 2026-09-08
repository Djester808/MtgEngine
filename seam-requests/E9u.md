# E9u — the interpreter reads V14, and an edict reaches every player

Wave 26. Anchor `InterpreterTests`: **122 green**, up from 116; six new tests in
`tests/MtgEngine.Rules.Tests/InterpreterTests.Wave26.cs`. Engine changes are in
`MtgEngine.Rules/Interpreter/EffectInterpreter.Actions.cs`, `EffectInterpreter.Verbs.cs` and a new
partial `EffectInterpreter.Wave26.cs`. One harness method was added to
`InterpreterTests.Fakes.cs` (`Declaring`, combat already happening) — no seam moved.

## Done — CR 701.16 with 102.1: an edict asks every player it admits (P24 § 1)

The `Sacrifice` arm read `FirstPlayer(run, sacrifice.Who)`, so `Who: EachPlayer` reached the caster
alone: the opponent was never asked and their permanent never moved (P24 § 1 measured exactly that
on `d8ad23a1` through the real runner). It now walks the players `InTurnOrder` — APNAP, CR 101.4 —
asks each in turn, and the answer comes back to the same node, moves that player's permanent and
asks the next of them, the way the `Discard` arm has always done. Two behaviours changed beside it,
both cited:

- **A player who controls nothing the choice admits is skipped** rather than asked (CR 701.16b with
  101.4). The old arm asked with `min = max = 1` over an empty option list, which no answer could
  satisfy — the game would have stopped on it for ever.
- **The count is clamped to what the player actually controls.** "Each player sacrifices two
  creatures" against a player who controls one asks for one, not for an unanswerable two.

The question's kind is now `ChoiceKinds.Sacrifice` rather than the string literal `"sacrifice"`
(wave 12's rule); the constant already held the same text, so nothing on the wire changed.

**For the P-series**: `SmokeScenario.Enters.Edicts` can assert both battlefields now, and the doc
comment recording the divergence can go. The spell-side `Sweep` arm and the trigger path now agree.

## Done — CR 701.8: `Destroy.RememberAs` is what was destroyed

The arm sets the key from the objects that actually landed (`Landed(before, run)`), exactly as the
`Exile` arm does, and not from what the selector named. An indestructible permanent, one whose
regeneration shield was used (CR 701.19a) and one that had already left are all absent from the key,
which is the whole reason the printed phrase says "this way". Tested by an `Exile(Remembered(key))`
after a `Destroy(Each(creature))` over a board holding one indestructible body: one card is exiled,
and it is not the one still standing.

## Done — CR 508.4: `CreateToken.AttackingWhom`

`EffectInterpreter.Wave26.Attacking` sets the tokens that just arrived attacking the player the
reference names, by emitting one `AttackersDeclared` over the combat already happening. Without it
`ZoneSystem.DefenderIn` copies whatever the first declared attacker was declared against, which is
the wrong player whenever the token's controller is not the attacking player. The token is never
declared as an attacker, so nothing about the declaration happens again (CR 508.4 with 506.3c) and
`combat.AttackingPlayer` is untouched.

A reference that names nobody who could be attacked — nobody at all, or only the token's own
controller, who is never a defending player (CR 506.2) — is **refused with the rule**
(`RefusedException`, 508.4) rather than left attacking whoever happened to be first. With no combat
happening the instruction does nothing at all, which is what `Attacking` alone already did.

## Done — CR 702.8: the `Apply` arm creates the flash permission on a player

`Permitted` (in `EffectInterpreter.Wave26.cs`) answers the players a permission-shaped continuous
effect is granted to — `MayCastAsThoughFlash.Who` — and both Apply arms use it: the effect lands in
`ActiveEffect.AppliesToPlayers` and never in `AppliesTo`, whichever record the card spelled it
inside. "You may cast creature spells as though they had flash" reaches no object (the card is in a
hand and a spell is not a permanent), so an `Apply` whose `To` names objects would otherwise create
an effect the casting system could never find under a player.

## Ask 1 (E3-series, `MtgEngine.Rules/Casting/**`) — read the permission at the timing check

One line: at the check that says whether this player may cast this spell now, a player with an
`ActiveEffect` whose `Effect` is a `MayCastAsThoughFlash` in `AppliesToPlayers` may cast a spell the
effect's `What` admits **any time they could cast an instant** (CR 702.8b). Timing only — the cost is
unchanged and the cast is an ordinary cast, so everything watching casts still sees it (V14 row 5).
A null `What` admits every spell. The effect is created and in the state today; nothing reads it.

## Ask 2 (E8-series, `MtgEngine.Rules/Replacement/**`) — a may-pay asked by an entering replacement

Mox Diamond `f3c5978a` still fails with *"nothing is waiting on a may-pay answer: the effect that
asked cannot be found again (101.4)"*. The refusal is thrown in this folder
(`EffectInterpreter.Resume.Elsewhere`), but the cause is not: `Tree(state, context)` finds the
ability by `context.Source`, and for a CR 614.1a **"instead"** on an entering permanent that source
is the id the object has **in the probe state only** (CR 614.12). E8d fixed the sibling half — the
`AbilityIndex` is now computed against the probe — but the *source id* is still a probe id, and for a
choose-only `With` that never mattered because the object was handed back and entered the
battlefield, so the id existed by the time the answer came. Mox Diamond's replacement is a real
"instead": while the question stands the permanent has not entered, nothing in `state.Objects`
carries that id, `Tree` falls through to the delayed triggers, finds none, and the answer is refused.

What this folder needs from `Replacement/**`: **the question a `With` raises must carry a source the
resumed state can find** — the card as it exists while the replacement is being applied (the object
still on the stack, or in whatever zone it is leaving), not the probe's entering id. `Tree` then
finds `AbilityList[AbilityIndex]` on that card's own face and the existing `Elsewhere` road resumes
with no interpreter change. The second half is `Replacement/**`'s too: the answer to a may-pay
decides whether the "instead" applies (CR 614.1a), so the resumed walk's outcome has to be handed
back to the replacement system rather than merely executed — this folder can execute the `With`
tree, but it cannot decide whether the entering happens.

I could not fix it inside `Interpreter/**` without guessing at the mapping from a probe id back to
the card, which would be a second spelling of what the replacement system already knows.

## Not done in this box

- **No verify run.** The corpus lives under `MtgEngine.Api/bin` in the main checkout and this
  worktree cannot reach it, so `d8ad23a1` (the edict) and the `AttackingWhom` and flash cards are
  **unverified through the pipeline**; the six anchor tests are the evidence. The next P-series or
  the coordinator should re-verify `d8ad23a1` with both battlefields asserted.
- **No planeswalker or battle half of CR 508.4.** `AttackingWhom` names the player, and the token is
  set attacking that player; "or a planeswalker they control" is a choice the record cannot express
  and no card in the slices prints it as anything else.
- **`Permitted` is a type test on one record.** A second permission-on-a-player continuous effect
  will want the same road; when there are two, it becomes a property on the base rather than a
  switch that grows.
