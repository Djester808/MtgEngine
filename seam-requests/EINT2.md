# EINT2 — what the wave-2 exit test found

Two defects at joins between wave-2 subsystems, neither in a folder EINT2 owns. Both are
named to their builder with the rule. The exit test (`WaveTwoIntegrationTests`) asserts what
it can around each of them and says so in the test's own comments.

## 1. No subsystem offers an entering permanent to the replacement system — CR 614.1c, 616.1

**Owner: E8b (replacement) with E3b (casting) and whoever holds `Rules/Zones` this wave.**

`IReplacementSystem.Replace` is called from exactly one place in the engine:
`Interpreter/EffectInterpreter.cs:193` (`Propose`), and the only event the interpreter ever
proposes is `DamageDealt` (`EffectInterpreter.Verbs.cs`, the `DealDamage` arm). Every other
path that changes a zone calls `IZoneSystem.Move` directly:

- `Casting/CastingSystem.cs` `PlayLand` — a land played from hand,
- `Casting/CastingSystem.cs` `ResolveTop` — a permanent spell becoming a permanent (CR 608.3),
- `Interpreter/EffectInterpreter.Actions.cs:190` — an effect that moves a card,
- `Choices/ChoiceSystem.Resume.cs` — the legend rule, discards, mulligan bottoming.

So no `ReplacedEvent` other than the damage ones can ever apply in a real game:
`WouldEnterBattlefield` (and with it `EntersTapped`, `EntersWithCounters`, `EntersAs`),
`WouldDie`, `WouldBeDestroyed`, `WouldBePutIntoZone`, `WouldLeaveBattlefield`, `WouldDraw`,
`WouldMill`, `WouldBeExiled`, `WouldShuffle` and the rest. The replacement system itself is
right — asked directly with the `ObjectMoved` that would put a land onto the battlefield, it
hands the move back with the permanent tapped, which is what
`WaveTwoIntegrationTests.AssertEnteringTappedIsTheRule` asserts, and `Probe` already handles
the object not existing yet. Nothing asks it.

The seam this needs: either `IZoneSystem` takes the replacement system and proposes its own
`ObjectMoved` before applying it (one place, every mover fixed at once), or `Move` gains an
overload that returns the proposed event for the caller to run through `Replace`. The first
is the smaller change and keeps CR 616.1's "the affected player chooses" in one place; it
makes `IZoneSystem` depend on `IReplacementSystem`, which `Subsystems.Wave2()` already builds
with a forwarding handle for the interpreter cycle, so a second handle is the same shape.

Until it lands, a table plays every "enters tapped" land untapped and every replacement other
than damage silently does nothing — the honour-system badge cannot see it, because the card
compiles, lints and looks implemented.

## 2. `Subsystems.Wave2()` raises questions from cards that nothing can answer — CR 101.4, 608.2

**Owner: E9b (interpreter `IResumable`), with the coordinator for the wiring.**

`ChoiceSystem` routes every kind it does not perform itself to `IResumable`, and
`Subsystems.Wave2()` constructs it without one (`Engine/Game.cs:79`), so `UnbuiltResumable`
takes those. A card's "you may" (`May` in the interpreter's structure arm) therefore raises a
`PendingChoice` that stops the game (CR 101.4) and can never be cleared: the same holds for
`value`, `search`, `scry`, `targets`, `trigger-order` and `replacement-order`.

EINT2 fixed the half in its own folder: `UnbuiltResumable.Resume` now throws
`RefusedException` naming CR 101.4 instead of `NotImplementedException`, so `Game.Apply`
returns a `Refused` and the question stays open, rather than an exception leaving the engine's
front door and reaching the hub as a bare 500 (`ChoiceResumptionTests
.An_unbuilt_interpreter_refuses_the_answer_rather_than_leaving_the_front_door`, and the exit
test's `A_may_trigger_stops_the_game_and_asks_its_controller`). What is still owed is E9b's
implementation and the wiring named in `seam-requests/DECISIONS.md` ("wave 3 (E9b), after
which `Subsystems.Wave2()` passes the interpreter to the choice system").

A table that reaches a "you may" is stuck on that question until then. That is the honest
behaviour and it is now a refusal rather than a crash, but it is not a playable one, so any
deck admitted for enforcement in the meantime must contain no `May`.
