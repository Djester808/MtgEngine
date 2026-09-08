# E1f — the prevention event

Wave 28. Owned: `MtgEngine.Rules/Zones/**`, `MtgEngine.Rules/Events/**`,
`tests/MtgEngine.Rules.Tests/Zone*.cs`, `tests/MtgEngine.Rules.Tests/Event*.cs`,
`tests/MtgEngine.Rules.Tests/events.txt`.
Landed: `MtgEngine.Rules/Events/PreventionEvents.cs` (the `DamagePrevented` record), its
`events.txt` line, three tests in `tests/MtgEngine.Rules.Tests/EventLogTests.Wave28.cs`.

`DamagePrevented(ObjectId Source, ObjectId? ToObject, PlayerId? ToPlayer, int Prevented, ObjectId?
PreventedBy) : GameEvent` implements `MtgEngine.Rules.Triggers.IDamagePrevented` — the interface is
the agreement, and the record answers its five members and nothing else. `Source` is a plain
`ObjectId`, not the nullable the brief spelled: the interface declares it non-nullable, `ObjectId`
is a struct, and a nullable field cannot implement it. `Apply` is the identity (CR 615: a prevention
changes nothing beyond the damage not dealt), so a log that carries the event replays to the state a
log without it replays to — which is what lets the emit be added to an engine whose stored games
predate it.

## 1. `tests/MtgEngine.Rules.Tests/Trigger*.cs` — the event's name collides with the trigger's (blocker)

**The test project does not compile with the record in it.** `MtgEngine.Cards.DamagePrevented` (the
trigger the card asks for, `Triggers.cs`) and `MtgEngine.Rules.Events.DamagePrevented` (the
happening the log records) are two different things with one simple name, and
`tests/MtgEngine.Rules.Tests/TriggeredAbilityTests.Wave27.cs` imports both namespaces unaliased:

```
TriggeredAbilityTests.Wave27.cs(73,48): error CS0104: 'DamagePrevented' is an ambiguous reference
between 'MtgEngine.Cards.DamagePrevented' and 'MtgEngine.Rules.Events.DamagePrevented'
```

Five sites, all `new DamagePrevented(...)` meaning the trigger: lines 73, 92, 109, 125, 141. That
file is `Trigger*.cs` and outside this agent's ownership, so the fix is asked for here.

**Ask.** One line added to that file's usings, after the four it has:

```csharp
using DamagePrevented = MtgEngine.Cards.DamagePrevented;
```

A file-scoped using alias wins over the namespaces imported beside it, so all five sites keep their
meaning and `IDamagePrevented` is untouched. Nothing else in `MtgEngine.Rules` or
`tests/MtgEngine.Rules.Tests` reads the bare name with both namespaces in scope — `TriggerMatching.cs`
and `TriggerMatching.Wave27.cs` already alias the events folder as `Ev`, which is why they compile.
`EventLogTests.Wave28.cs` carries the mirror of that line (aliasing the event) and is the worked
example.

A global using alias cannot do this: file-level usings bind in an inner scope than global ones, so
the ambiguity is decided before a global alias is ever consulted. Renaming either type was rejected
— the trigger's name is the vocabulary's and is in the schema snapshots, and the event's is the one
`events.txt`, the log discriminator and `seam-requests/E6j.md` all name.

**Until it lands, `tests/MtgEngine.Rules.Tests` is red and no test in it runs**, including this
agent's three. `MtgEngine.Rules` itself builds clean and both projects pass
`dotnet format --verify-no-changes`.

## 2. `Replacement/**` — the two arms that must emit it (E8f's, this wave)

The emit is E8f's, not this agent's; the shape was agreed through `IDamagePrevented` and neither
worktree was read. Both arms are in `ReplacementSystem.Applying.cs` and both go through the same
private `Prevent` (line 199), which is where the prevented amount is known:

| Arm | Line | What it is |
|---|---|---|
| the prevention shield | 34 | `Prevent(state, current, null, candidate)` — the whole damage event, CR 615.7/615.8 |
| `PreventThatDamage` | 66–67 | `Prevent(state, current, prevention.N, candidate)` — the effect that names an amount, CR 615.10 |

One `DamagePrevented` in `Applied.Extra` per damage event an application touched (CR 615.13: "one or
more simultaneous damage events"), with:

- `Prevented` = the amount actually removed from the event, `min(shield, damage.Amount)` — **not** the
  amount dealt, and **not** the shield. The matcher publishes it verbatim as "that much".
- **No event at all when nothing was prevented.** CR 615.12: a prevention effect applied to
  unpreventable damage is still applied and still prevents nothing, and CR 615.12a says it is applied
  just once. An event there would fire every card of this shape on damage nobody was saved from. The
  matcher refuses `Prevented <= 0`, so a zero is harmless but pointless; write nothing.
- `PreventedBy` = `Candidate.Source`, which is what a trigger's "this way" names; null when no object
  is behind the prevention.
- `Source`/`ToObject`/`ToPlayer` = the three the `DamageDealt` being replaced carries, unchanged, so
  the trigger's `By` and `To` halves ask about the same objects whether the damage landed or not.

The prevention that prevents **all** of the damage returns `null` from `Prevent` and the
`DamageDealt` never happens — that is the case the log was silent about, and it is the one that most
needs the event.

## 3. `Triggers/**` — the interface belongs in `Events/**` now (recorded, not urgent)

`seam-requests/E6j.md` § 1 says `IDamagePrevented` sits in `Triggers/TriggerMatching.Wave27.cs` only
because the events folder was not that agent's, and asks for it to move beside the record once the
record lands. It has landed. The move is a folder change only — same assembly, same name — but it
cannot be done from here and from `Triggers/**` at the same time: two declarations of the name would
have the matcher reading one interface and the record implementing the other, and nothing would
match. So it is one commit by one owner, moving the declaration and dropping
`using MtgEngine.Rules.Triggers;` from `Events/PreventionEvents.cs`. Nothing is wrong while it waits;
the record implements the interface where it stands today.
