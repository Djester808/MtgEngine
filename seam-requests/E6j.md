# E6j — the prevention trigger, the Nth spell, and the triggering spell's payment

Wave 27. Owned: `MtgEngine.Rules/Triggers/**`, `tests/MtgEngine.Rules.Tests/Trigger*.cs`.
Landed: `TriggerMatching.Wave27.cs`, `TriggerSystem.Wave27.cs`, arms in `TriggerMatching.cs` and
`TriggerMatching.Wave25.cs`, ten tests in `TriggeredAbilityTests.Wave27.cs`.

## 1. `Replacement/**` and `Events/**` — nothing records a prevention (CR 615.13)

`ReplacementSystem.Prevent` (`Replacement/ReplacementSystem.Applying.cs`) takes the prevented
damage out of the `DamageDealt` event and, when it prevents all of it, returns `null` and the event
does not happen at all. **So the log never says a prevention happened, and it never says how much
was prevented** — the only two facts CR 615.13 triggers on. `DamagePrevented` cannot match anything
the engine emits today.

**Ask.** `Events/**` declares the event and `Replacement/**` emits it in `Applied.Extra` each time
`Prevent` is applied to a `DamageDealt` and the prevented amount is greater than zero — once per
damage event the application touched (CR 615.13: "one or more simultaneous damage events"). Its
fields, which are the shape the matcher already reads:

| Field | What it is |
|---|---|
| `Source` (`ObjectId`) | the object that would have dealt the damage — the trigger's `By` half |
| `ToObject` (`ObjectId?`) | the object that would have been damaged |
| `ToPlayer` (`PlayerId?`) | the player that would have been damaged |
| `Prevented` (`int`) | how much this application actually prevented, **not** how much was dealt |
| `PreventedBy` (`ObjectId?`) | the object whose prevention effect applied — `Candidate.Source` — which is what `ThisWay` names |

`Apply` folds nothing (the `DamageDealt` that survives already folds what was dealt); it exists so
the log says a prevention happened. It needs its line in `tests/MtgEngine.Rules.Tests/events.txt`,
which `EventLogTests.Every_event_type_is_listed_in_events_txt` compares against reflection — that
file is outside this agent's ownership, which is the second half of this ask.

**What landed here meanwhile.** `MtgEngine.Rules/Triggers/TriggerMatching.Wave27.cs` declares
`public interface IDamagePrevented` with exactly those five members, and the matcher reads the
interface, not a record. The new event record implements it and **no trigger code changes**. The
interface is in `Triggers/` only because the event's own folder is not this agent's; move it to
`Events/**` beside the record when the record lands.

Two things the emitter must get right, both of which the matcher relies on:

- **CR 615.12**: a prevention effect applied to unpreventable damage prevents nothing. Emit no event
  (or `Prevented: 0`, which the matcher refuses) — otherwise every card of this shape fires on
  damage that was never prevented.
- `Prevented` is the amount **removed from the event**, i.e. `min(shield, damage.Amount)` — the
  matcher publishes it verbatim as "that much".

## 2. `Query/**` — the two amount readers, and the keys the firing carries

The firing carries these on `StackEntry.Chosen`, the channel the damage and the two life amounts of
wave 26 already use. All three consts are public on `TriggerSystem`
(`MtgEngine.Rules/Triggers/TriggerSystem.Wave27.cs`) — **name them, never re-spell the strings**:

| Const | Value | Written when | Reader asked for |
|---|---|---|---|
| `TriggerSystem.TriggeringPreventedDamageAmount` | `triggering-prevented-damage` | the firing's event is an `IDamagePrevented` with `Prevented > 0` | `TriggeringPreventedDamage` (CR 615.13) |
| `TriggerSystem.ManaSpentOnTriggeringAmount` | `mana-spent-on-triggering` | **every** firing whose event is `Ev.SpellCast`, including as `"0"` | `ManaSpentOnTriggering()` with no colour (CR 400.7d) |
| `TriggerSystem.ManaSpentOnTriggeringColorPrefix` | `mana-spent-on-triggering-` + the `Color` enum name | one key per colour actually paid; a colour nobody paid has **no** key | `ManaSpentOnTriggering(OfColor: c)` |

Read them exactly as `TriggeringLifeGained`/`TriggeringLifeLost` are read — `CarriedNumber(context,
key)` in `Query/Query.Amounts.cs`. Two things they must not do:

- `TriggeringPreventedDamage` must **never** fall back to `TriggerSystem.TriggeringDamageAmount`. A
  firing on a prevention writes only the prevented key and a firing on a damage event only the dealt
  key; a fallback would deal 3 for a shield that stopped 2 of 5, and nothing at all when the damage
  was prevented in full.
- `ManaSpentOnTriggering` must **never** read `StackEntry.ManaSpent` off `Source(state, ctx)` — that
  is `ManaSpentOnThis`, and the two answer about different spells. The triggering spell may have
  resolved and ceased to exist (CR 400.7) by the time the ability resolves, which is why the payment
  rides on the firing at all.
- A missing `mana-spent-on-triggering` key is a firing that watched **no cast**, not a payment of
  zero; "if no mana was spent to cast it" is the key present and equal to `"0"`. Refuse the missing
  key the way the wave-25 secret-number readers refuse a key nobody answered, rather than answering 0.

The total carried is the sum of the entry's per-colour `ManaSpent`, which is exactly what
`ManaSpentOnThis` sums, so the two amounts answer the same way about one payment.

## 3. `State/**` — `SpellCastRecord` cannot answer a filtered Nth exactly (CR 603.2)

`SpellCast.Nth` counts, per player and per turn, over the spells the trigger's own `Spell` filter
admits. `TurnHistory.SpellsCast` holds `SpellCastRecord(Spell, By, From)` — an `ObjectId` — and a
spell that has already resolved is a different object by then (CR 400.7), so its filter can no
longer be asked.

What landed counts such a record **as admitted**: not counting it would make "your second spell each
turn" stop firing the moment the first spell resolved, which is every real game. The cost is that a
*narrow* filter ("your second creature spell each turn") over-counts an earlier departed spell that
would not have been admitted. Exactness needs the record to keep enough to re-ask the filter — the
face, or the filter's answer taken at cast time. No corpus card seen so far pairs `Nth` with a
narrow filter, so this is recorded rather than urgent.

## 4. Recorded, not asked

- **`DamagePrevented` with a `To` filter cannot watch damage to a player.** `To` is an
  `ObjectFilter` and a player is not an object, so a narrowed trigger refuses a player-recipient
  prevention rather than guessing. Both halves left null admit any prevention, which is the plain
  "whenever damage is prevented".
- **`ThisWay` with no source matches nothing.** A delayed ability (CR 603.7) has no object, and
  "this way" cannot name a prevention that no object applied.
- **`TriggerSystem` is now `partial`** so the three consts live in their own file rather than pushing
  `TriggerSystem.cs` past the 500-line limit. One word changed on the class declaration.
- **`DamageCantBePrevented` (V15 gap 3, CR 615.12) is not this agent's.** It is a `ContinuousEffect`
  and belongs to the damage/prevention road; the matcher never sees it.
