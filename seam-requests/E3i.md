# E3i — the casting system reads the player limits, and the instruction is the permission

Wave 11. Owned: `MtgEngine.Rules/Casting/**`, `MtgEngine.Rules/Stack/**`, `MtgEngine.Rules/Mana/**`
and the casting/mana/aura test files. EINT4 § 4 and § 5 are the two gaps in this folder. § 4 is
closed end to end. § 5 is closed on the casting system's side and **needs one seam member** before
the interpreter can reach it; the rest of this box carried on without it, as the brief says.

## Landed

- **CR 115.6 with 702.16j — a player who can't be targeted is refused at announcement.**
  `CastingSystem.Instructed.cs`'s new `RequireLegalPlayerTargets`, called from `CastCore` on both
  roads, reads `ICharacteristics.PlayerRestrictions(state, player)` — the seam E0e built and named
  as having no reader. **Two spellings, two readings**, and the difference is deliberate:
  - `PlayerRestriction.BeTargeted` is the card-text form, "by spells or abilities your opponents
    control", so it is read from the caster's point of view and the protected player may still name
    themselves.
  - `PlayerLimits.ProtectionFromEverything` carries no such qualifier in CR 702.16j ("Such a
    permanent or player can't be targeted by spells or abilities"), so **no source targets through
    it, the controller's own included**.

  `PlayerLimits.CantBeTargeted` merges the two into one bool, so it is not the property to read for
  this: the reader asks `ProtectionFromEverything` and `Has(PlayerRestriction.BeTargeted)`
  separately. E0e § 1 says "both the flag and CR 702.16j are worded 'by spells or abilities your
  opponents control'"; the rules text says otherwise for 702.16j, and this box took the text.
  CR 115.10's gift promise rides on `CastSpell.TargetPlayers` and is not a target, so the player
  `CastPlan.GiftPromisedTo` read is skipped rather than refused.
- **CR 702.16c — an Aura cast at a player who can't be enchanted is refused at cast.** E5e § 3's
  half that was not E5e's. `RequireLegalAuraTarget` is now an instance method and asks
  `PlayerLimits.CantBeEnchanted` after the enchant ability's own filter. It is asked **before** the
  targeting check, because protection from everything sets both prohibitions and "that player
  cannot be enchanted" is the nearer reason an Aura at one is illegal.
- **CR 608.2b — the same Aura loses that player on resolution.** `CastingSystem.AuraAttachment`
  re-checks the player the Aura was cast at; a protection that arrived after the cast makes the
  only target illegal and the spell does not resolve, so it never enters and CR 704.5m never has an
  attachment to bury.
- **CR 601.2b/601.2c on the instructed road.** `CastingSystem.Choices.cs`'s `OfCast` now reads the
  plan with `instructed: true`. What a cost consumes is the same whichever road the cast takes, and
  neither CR 601.3's zone permission nor CR 117.1a's timing is what a cost question asks about — a
  card a `MayCast` names in a graveyard has to be able to say what its sacrifice wants *before* the
  answer that casts it. Nothing is authorised there; `Check` still refuses.
- **`CastingSystem.CheckInstructed(GameState, CastSpell)`** — `Check` without the two things the
  instruction itself grants. Public on the class, **not on the interface**; see below.

`CastPlan.Alternative` already skipped the CR 601.3 zone refusal for an instructed cast (wave 10),
so `CastInstructed` from a graveyard was always going to work. EINT4 § 5's refusal does not come
from there.

## The seam: `ICastingSystem.CheckInstructed`

`EffectInterpreter.Retarget.cs:199` pre-flights a may-cast with `Casting!.Check(run.State, command)`
and only then calls `CastInstructed`. `Check` routes a `CastSpell` to `Cast`, the ordinary road, so
**the zone refusal EINT4 § 5 reports is that pre-flight, not the cast**. `Check` takes a `Command`
and has no way to know the caller is a resolving effect.

Asked for, on `ICastingSystem`, beside `CastInstructed`:

```csharp
/// <summary>CR 608.2g: the refusal an instructed cast would be given now — Check without the zone
/// permission (CR 601.3a) and the timing (CR 117.1a) the instruction itself grants.</summary>
[RuleRef("608.2g")]
Refusal? CheckInstructed(GameState state, CastSpell command) => Check(state, command);
```

The default keeps every existing implementation compiling and behaving as it does today, and
`CastingSystem` already implements the member with that exact signature — the seam commit only has
to add the declaration. **E9's follow-on**, in the same landing: `EffectInterpreter.Retarget.cs`'s
`Cast` calls `Casting.CheckInstructed(run.State, command)` instead of `Check`. Nothing else changes;
`CostQuestion` still relays the refusal, and the cost refusal it now gets is CR 601.2c rather than
CR 601.3, which is the question it was written to ask.

With those two lines,
`WaveNineIntegrationTests.An_instructed_cast_asks_for_its_additional_cost_and_sacrifices_what_is_named`
reaches the cast-cost question, and the E9i + E3h + E3g join is verified through the front door on
the graveyard road. Until then that test asserts the refusal, as EINT4 wrote it. I did not touch it.

## Not mine: the player half of the CR 608.2b re-check

`EffectInterpreter.LegalTargets` (E9's) re-checks `entry.Targets` and **never looks at
`entry.TargetPlayers` at all**; `ContextFor` hands them to the walk unfiltered. The Aura road is
re-checked here because the attachment is the casting system's, but a burn spell that targets a
player who became protected after it was cast still names them on resolution, and only the damage
replacement (CR 702.16j, `ReplacementSystem.Limits.cs`) saves the outcome.

**Asked of E9**: `LegalTargets`' sibling for players — drop a player from the context whose limits
say they can't be targeted by this spell's controller (the same two readings as above), and count
them with the objects when CR 608.2b asks whether every target is illegal. `Query.Targets.cs`'s
`MatchesAnyTarget(GameState, PlayerId)` is E0e's uncalled static and wants the source-controller
overload before it can be that reading; E0e § 1 says so and left it alone deliberately.

## The `WaveNineIntegrationTests` assertion that flips

`tests/MtgEngine.Rules.Tests/WaveNineIntegrationTests.Restrictions.cs:212` — EINT5's file, read only
here, and it is **red on my branch by design**:

```csharp
Assert.Single(table.Do(new CastSpellCommand(P0, bolt, TargetPlayers: [P1])).OfType<SpellCastEvent>());
```

The announcement is now refused, so `Do` throws. The flip EINT4 wrote the test for is
`Assert.Equal("115.6", table.Refuse(new CastSpellCommand(P0, bolt, TargetPlayers: [P1])).Rule)`,
the "THE GAP" comment above it goes, and the two lines after it (pass until the stack is empty, then
the life total) go with it — nothing reaches the stack any more. **EINT5 or the coordinator owns
that edit**; my box may not write in that file.

`WaveNineIntegrationTests.An_instructed_cast_asks_for_its_additional_cost_and_sacrifices_what_is_named`
(line 118, `Assert.Equal("601.3", ...)`) flips in the same way once the seam above lands, to the
cast-cost question EINT4 § 5 describes.

## Rule numbers, corrected against the text

- The brief said "CR 702.16d" for the Aura refusal. 702.16d is Equipment and Fortifications;
  **702.16c** is "A permanent or player with protection can't be enchanted by Auras that have the
  stated quality", which is the rule the refusal names. 702.16j is the "from everything" variant
  behind it, and `PlayerLimits.CantBeEnchanted` already cites it.
- The brief said "refuse naming 115.6" and EINT4 § 4 names 115.6, so the refusal keeps that rule
  string and EINT5's flipped assertion can match it. It is worth noting for a later pass that 115.6
  in the rules file is about a spell that allows zero targets; the sentence that actually makes a
  protected player an illegal choice is CR 601.2c with 702.16j. If the coordinator would rather the
  refusal named "601.2c" — which is what the Aura refusal beside it names — it is a one-line change
  here and a one-word change in EINT5's assertion.

## No other seam change

No new vocabulary, no state record, no event, nothing in `MtgEngine.Cards`. `PlayerLimits`,
`ICharacteristics.PlayerRestrictions` and `ICastingSystem.CastInstructed` were all read as they
stand.
