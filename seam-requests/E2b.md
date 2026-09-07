# E2b — turns and combat, wave 3

Four requests. The first is the only one that blocks anything: without it the mulligan loop is
half-wired in the engine and whole only in `MulliganTests`.

---

## 1. `ITurnSystem.AfterMulligans`, and the one line in `ChoiceSystem` that calls it (CR 103.5)

**Why.** CR 103.5 splits across two subsystems and neither owns all of it. The choice system
knows how a declaration is *answered* — shuffle, redraw, ask again, ask which cards go on the
bottom. The turn system knows the **turn order** and when **turn 1** may begin. `OpenHands`
raises the starting player's declaration (built, see below), but nothing then asks the *next*
player, and nothing begins turn 1: `ChoiceSystem.Mulligan` on a keep goes to `AskToBottom`,
which returns `Outcome.Unchanged` when there is nothing to bottom, and `Bottom` ends after the
moves. So a game created with `OfferMulligans: true` stops for ever after the first keep.
`DECISIONS.md` gives E2b "begins turn 1 after the last keep"; this is the seam it needs.

On `ITurnSystem` in `Engine/Seams.cs`, beside `OpenHands`:

```csharp
/// <summary>
/// CR 103.5: one player's declaration is finished — they kept, and any cards their mulligans
/// cost them are on the bottom. The next player in turn order declares; when the turn order
/// has come back round to the starting player, every player has kept and turn 1 begins.
/// </summary>
[RuleRef("103.5")]
Outcome AfterMulligans(GameState state, PlayerId finished) => Outcome.Unchanged(state);
```

`TurnSystem` already implements it (`Turn/TurnSystem.Mulligans.cs`), so adding it to the
interface with that default is the whole change on this side.

In `Choices/ChoiceSystem.Resume.cs`, the two mulligan arms of `Resume` end with the call. The
smallest edit is in the dispatcher, where the answered `choice` is still in hand:

```csharp
private Outcome Resume(GameState state, PendingChoice choice, ImmutableList<ChoiceOption> chosen) => choice.Kind switch
{
    ...
    ChoiceKinds.Mulligan => Mulligan(state, choice, chosen).Then(s => Next(s, choice.Player)),
    ChoiceKinds.MulliganBottom => Bottom(state, chosen).Then(s => Next(s, choice.Player)),
    ...
};

/// <summary>CR 103.5: a finished declaration hands back to the turn system, which asks the
/// next player or begins turn 1. A question still open means this player is not finished.</summary>
[RuleRef("103.5")]
private Outcome Next(GameState state, PlayerId finished) =>
    state.Pending is null ? turn.AfterMulligans(state, finished) : Outcome.Unchanged(state);
```

The `state.Pending is null` guard is what makes it right for all three paths: after a mulligan
the next declaration is already pending, and after a keep that costs cards the bottoming
question is pending, so neither hands back; only a genuinely finished declaration does.

`MulliganTests` proves the turn system's half against exactly this, with a decorator over the
real `ChoiceSystem` (`MulliganTable.MulliganChoices`) standing in for the four lines above.
**Delete that decorator with the same commit** — it is a no-op once the wiring is right, and
leaving both would be two spellings of one rule.

**What `OpenHands` now does**, so the contract is written down: it raises
`ChoiceSystem.Declaration(state, starting, FirstCount(state), handSize)` for the starting
player (`state.Turn.ActivePlayer`, CR 103.8), skipping any player dealt no opening hand, and
returns `Outcome.Unchanged` when nobody can declare — so `Game.Create`'s existing
`s.Pending is null ? Begin(s)` arm still begins turn 1 and never begins it twice.

**CR 103.5c** is the `FirstCount` above: `-1` in a game of more than two players and in Brawl
(read from `GameState.Format` and the player count), `0` otherwise. The count a declaration
carries is the number of cards the player will bottom when they keep, so starting it one lower
is exactly "the first mulligan costs no card", and it needs no change on your side.

## 2. Not done: CR 103.5's simultaneity

CR 103.5 has *every* player declare, and then all mulligans happen **at once**. What is built
resolves one player at a time — declaration, shuffle, redraw, bottom, next player — because
that is what E13 implemented and what a single `PendingChoice` can express. The hands are the
same; the order of the events is not, and a card that cared about another player's opening hand
would see it. Filing it rather than faking it, per E13's note. Fixing it properly needs a
pending choice that several players answer, which is a bigger seam than this wave.

## 3. `ICombatSystem.CouldAttack` / `CouldBlock` (E14's request, CR 508.1a and 509.1a)

`CombatSystem` now has both as public methods with the signatures E14 asked for:

```csharp
public bool CouldAttack(GameState state, ObjectId creature);   // CR 508.1a
public bool CouldBlock(GameState state, ObjectId creature);    // CR 509.1a
```

Adding them to `ICombatSystem` in `Engine/Seams.cs` is the seam change; then
`Legal/LegalActions.cs` takes `ICombatSystem` and deletes its own reading of CR 508.1a, 302.6,
702.10b and 702.3b (`LegalActionsTests.An_attacker_must_be_untapped_and_controlled_since_the_turn_began`
and `.Haste_lets_a_creature_that_entered_this_turn_attack` should pass unchanged). Both are
tested here at `CombatTests.Could_attack_answers_the_same_rule_the_declaration_enforces` and
`.Could_block_answers_what_stops_a_creature_blocking_at_all`.

`CouldBlock` deliberately answers only what stops a creature blocking **at all** — off the
battlefield, not a creature, tapped, or an unconditional "can't block". The evasion keywords and
"can't block creatures with flying" need the attacker, so they stay on the declaration (CR
509.1b). A view that wants "could this creature block *that* attacker" needs a third method and
should ask for it rather than reading `CantBlock` itself.

## 4. Wiring: `Subsystems.Wave1()` and `Wave2()` should pass `ICharacteristics` to `TurnSystem`

`Engine/Game.cs` is the coordinator's. `TurnSystem`'s constructor is now

```csharp
TurnSystem(IZoneSystem zones, ICastingSystem casting, ICombatSystem? combat = null, ICharacteristics? characteristics = null)
```

with the fourth argument optional, so both compositions still compile — and both are still
wrong until it is passed:

```csharp
var turn = new Turn.TurnSystem(zones, casting, combat, layers);
```

Two rules read it, and each falls back to the wave-1 behaviour when it is null:

- **CR 502.3** — the untap step untaps the permanents the active player controls *now*, which
  is CR 613.1's computed controller. Without the layer system it untaps by
  `GameObject.InitialController`, so a permanent whose control changed untaps in the wrong
  player's untap step. This closes the wave-1 row "untap reads the stored controller".
- **CR 510.4** — the first-strike combat damage step exists only when a creature in combat has
  first or double strike. Without the layer system the step is walked whenever there is a
  combat at all, because a granted keyword cannot be seen from here. This closes EINT's first
  deviation. Note the visible change: a turn with no combat now has **twelve** steps, not
  thirteen, and one fewer round of priority; `TurnFixture.WholeTurn` became
  `TurnFixture.TurnWithNoCombat` for that reason.

EINT's other deviation, **CR 704.3** (combat damage is dealt inside `Advance`, and state-based
actions run afterwards in `Game.Settle`, so the log shows priority granted in the damage step
before the dead creature left), is **not** fixed and should not be fixed in the turn system: it
would put SBA knowledge in a subsystem, which is `Game.Settle`'s job. If the log order matters,
`Advance` needs to be able to hand back mid-step — a seam change, not a subsystem one.
