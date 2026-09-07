# E8c — combat damage never reaches the replacement system

Wave 6. `MtgEngine.Rules/Replacement/**` now drops the events a player's limits forbid
(CR 120.3, 702.16j, 119.3 through 101.2), before any card-driven candidate is gathered.
One seam is needed for it to bite in a real game; it is not in a file I own.

## 1. `CombatSystem.DealCombatDamage` applies its damage without proposing it (CR 510.2, 614.1a)

`MtgEngine.Rules/Combat/CombatDamage.cs` builds the whole batch and folds it directly:

```csharp
        return new Outcome(GameReducer.Apply(state, events), events);
```

Nothing in `MtgEngine.Rules/Combat/` mentions `IReplacementSystem` or `Replace`. So today
**no combat damage is ever offered to replacement or prevention.** That is the same shape of
gap EINT2 filed in wave 3 for zone changes ("nothing offers a zone change to the replacement
system", fixed in wave 4 by `zones.Replacements`), and it is wider than my own limits: a
prevention shield of CR 615.1, "prevent all combat damage that would be dealt to you", damage
multipliers, and every `WouldBeDealtDamage` replacement on a creature are all dead in combat
for the same reason. My limits are simply the newest reader of a call that is not made.

**The one call combat owes**, mirroring what `ZoneSystem` and `EffectInterpreter` already do —
a settable handle set in both compositions, so the constructor does not gain a cycle:

```csharp
// MtgEngine.Rules/Combat/CombatSystem.cs
/// <summary>CR 614.1a: set after construction, because the replacement system holds the
/// interpreter and the interpreter holds this. Null until then; damage is dealt unreplaced.</summary>
public IReplacementSystem? Replacements { get; set; }
```

and in `DealCombatDamage`, in place of the line above:

```csharp
        if (Replacements is null)
            return new Outcome(GameReducer.Apply(state, events), events);

        // CR 510.2 deals the batch simultaneously, so every event is proposed against the state
        // as the step began; each one's own result is folded before the next is proposed, which
        // is CR 616.1 applied per event, not per batch.
        var outcome = Outcome.Unchanged(state);
        foreach (var e in events)
        {
            var affected = e switch
            {
                DamageDealt { ToPlayer: { } p } => p,
                DamageDealt { ToObject: { } o } => _characteristics.ControllerOf(outcome.State, o),
                LifeChanged life => life.Player,
                _ => (PlayerId?)null,
            };
            outcome = affected is { } who
                ? new Outcome(outcome.State, outcome.Events).Then(s => Replacements.Replace(s, e, who))
                : new Outcome(e.Apply(outcome.State), outcome.Events.Add(e));
        }

        return outcome;
```

plus the one line in `Subsystems.Wave1()`/`Wave2()` that sets it, beside the existing
`zones.Replacements = …`.

Two things I would not decide for the combat owner:

- **Lifelink.** `WithLifelink` appends a `LifeChanged` to the same batch (CR 702.15b). Once
  damage is proposed, the life gain should follow *what was actually dealt*, not what was
  assigned — a shield that prevents the damage should stop the lifelink gain too (CR 615.1,
  702.15b: lifelink triggers on damage dealt). That means computing it after the replacement
  loop rather than before, which is a change to `WithLifelink`'s position, not just its call.
- **First strike ordering.** The batch is per step, so proposing per event inside the step is
  right; nothing here changes CR 510.4.

Until this lands, my drop is reached only through the interpreter's `Propose` — burn spells,
drains and the like — and a player with "can't be dealt damage" or protection from everything
still takes combat damage. `PlayerLimitReplacementTests` covers the interpreter path and the
direct `Replace` path; the combat path has no test because it has no call.

## 2. Nothing else

`ReplacementSystem` already took `ICharacteristics` (E7b's ask in `seam-requests/E7b.md` § 2
was satisfied by the existing constructor), `PlayerLimits` already unions
`PlayerProtectionFromEverything` into `CantBeDealtDamage`, and `Replace`'s signature already
carries the affected player. No other change was needed.
