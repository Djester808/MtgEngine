# E8f — the prevention event, unpreventable damage, and the two replaced-event riders

Wave 28. Owned: `MtgEngine.Rules/Replacement/**`, `tests/MtgEngine.Rules.Tests/ReplacementEffectTests*.cs`,
`tests/MtgEngine.Rules.Tests/ZoneReplacementTests*.cs`.

Landed: `Replacement/ReplacementSystem.Wave28.cs` (the stand-in event, the CR 615.12 reading, the two
rider helpers), the prevention road in `ReplacementSystem.Applying.cs` (`Spend` and `Prevent`), the
`WouldDraw` and `WouldAddMana` arms in `ReplacementSystem.Matching.cs`, ten tests in
`ReplacementEffectTests.Wave28.cs` and three existing prevention assertions widened.
`ReplacementEffectTests` is **36/36** (26 before), `dotnet format --verify-no-changes` clean on
`MtgEngine.Rules` and `tests/MtgEngine.Rules.Tests`.

## 1. `Events/**` (E1f) — the one-line swap, because the event is not in my tree

E1f's `DamagePrevented` was not in this worktree at the merge base (`fca8c8e`), so, as the brief
directed, the emitter goes through a stand-in declared in my own folder:

```csharp
// MtgEngine.Rules/Replacement/ReplacementSystem.Wave28.cs
public sealed partial class ReplacementSystem
{
    public sealed record DamagePrevented(
        ObjectId Source, ObjectId? ToObject, PlayerId? ToPlayer, int Prevented, ObjectId? PreventedBy)
        : GameEvent, IDamagePrevented
    {
        public override GameState Apply(GameState state) => state;
    }
}
```

**The swap when E1f's record lands: delete that nested record.** Nothing else in `Replacement/**`
changes — `Recorded` constructs it by the five members of `IDamagePrevented` and by nothing else,
and `Applying.cs` already has `using MtgEngine.Rules.Events;`. The only edit outside the deletion is
the test helper `Wave28Prevention` in `ReplacementEffectTests.Wave28.cs`, which names the type
`ReplacementSystem.DamagePrevented`; drop the `ReplacementSystem.` prefix.

**One shape note for E1f.** The brief spells `Source` as `ObjectId?`, but
`IDamagePrevented.Source` (`Triggers/TriggerMatching.Wave27.cs`, E6j's own interface) is a
non-nullable `ObjectId`, and `DamageDealt.Source` is non-nullable too — so the stand-in declares
`ObjectId Source`. A nullable positional parameter cannot implement that member without an explicit
implementation. Either E1f matches this, or E6j's interface loosens; the two cannot both stand.

## 2. `tests/MtgEngine.Rules.Tests/events.txt` — one line, and the gate is red without it

`EventLogTests.Every_event_type_is_listed_in_events_txt` compares the file against reflection over
every `GameEvent` in the assembly, so **it fails as of this commit**, whether the record is mine or
E1f's. This is the second half of E6j § 1 and it is not my file.

**Add `DamagePrevented` immediately after `DamageDealt`** (currently line 13), so it becomes line 14.
The list is ordinal-ordered and `DamageDealt` sorts first (`D` before `P`).

## 3. `tests/MtgEngine.Rules.Tests/CombatReplacementTests.cs` — two assertions widened

Both assert that a fully prevented combat hit leaves **no** events at all; a prevention now records
that it happened (CR 615.13), so each collection holds exactly one `DamagePrevented`. Not my file.

| Line | Now | Should be |
|---|---|---|
| 90 (`A_halted_hit_the_answer_prevents_gains_no_lifelink`) | `Assert.Empty(events);` | `Assert.DoesNotContain(events, e => e is DamageDealt or LifeChanged);` |
| 392 (`The_real_replacement_system_prevents_combat_damage_to_the_shielded_player`) | `Assert.Empty(events);` | `Assert.DoesNotContain(events, e => e is DamageDealt);` |

Both are strictly stronger about the thing they were testing — that no damage and no lifelink
happened — and no longer say "nothing at all happened", which is now false and is the whole point of
the event. `Assert.Empty` there was the assertion E6j's request predicted would have to move.

## 4. Recorded, not asked for

- **`WouldAddMana.FromSource` is still unread** in the matching arm, as it was before this wave. The
  arm now reads `TappedFor` (CR 605.1a with 106.12: a positive provenance block naming a permanent
  that is tapped, CR 106.6) and leaves `FromSource` alone, because narrowing it would change how
  every stored card that carries one already plays and that is a wave of its own. A card that pairs
  the two gets the rider's narrowing and not the filter's.
- **`DamageCantBePrevented` narrows no recipient.** The record carries only `From` and `Combat`
  (CR 615.12, "however it is dealt"), so `Unpreventable` does not consult the effect's `AppliesTo`
  or `AppliesToPlayers` — which is right, because E9v creates it against every player in the game
  (CR 102.1) precisely for want of a recipient. If a corpus card ever needs "damage to *you* can't
  be prevented", that is a field on the record (V-series), not a reading here.
- **CR 615.12a costs nothing to hold.** "Applied just once" is the `used` set the CR 616.1 loop
  already keeps; the arm returns the event unchanged and the effect is never offered it again.
- **`PreventedDamage`** (V16 gap 4, "for each 1 damage prevented this way") is a **query** reader
  inside the running replacement and is not built here — the amount this ability's own prevention
  took off is now computed at the emit site, so an E5-series reader has a number to read, but the
  channel it should ride on is not decided. Nothing in `Query/**` is mine.

## 5. Unverified

The two neighbouring anchors were run (`EventLogTests`, `ZoneReplacementTests`,
`CombatReplacementTests`, `PlayerLimitReplacementTests`, `TriggeredAbilityTests`: 118 of 121 pass,
the three failures are exactly §2 and §3). **No wider suite and no `verify` run** — the box was up.
Whoever merges must run the engine anchor whole after the two files above are edited.
