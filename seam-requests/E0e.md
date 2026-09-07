# E0e — the two seams are implemented, and the player limits nobody reads

Wave 10. Owned: `MtgEngine.Rules/Query/**`, `MtgEngine.Rules/Characteristics/**`,
`MtgEngine.Rules/Layers/**` and the query, layer and creature-type test files. Both seams the
wave-9 table gave me had throwing defaults on the base; both are implemented here, so this box
asks for nothing new. What it does ask is that the copies it replaces are actually deleted, and it
writes down the player limits that are computed correctly and read by nobody.

## Landed

- **`IQuery.PlayerMatches` (CR 702.5b with 601.2c)** — one reading, in `Query/Query.Selectors.cs`.
  `IsYou` and `Opponent` are read from the context's controller (CR 109.5, CR 102.1); `Also` is
  evaluated with the context rebound to the candidate (`context with { Controller = candidate }`),
  which is the reading E5d's `StateBasedActionTests.An_enchant_players_condition_is_asked_of_the_player_it_filters`
  asserts and the one `Query.Selectors.Extreme` already takes for `PlayerWithMostOf`. Three tests
  in `QueryTests.PlayerFilters.cs`, one of them built so the ambient reading fails it: Bob controls
  a creature, Alice is the context's controller and controls none, and "a player who controls a
  creature" has to be true of Bob and false of Alice.
- **`ICharacteristics.PrintedOf` (CR 613.1a)** — `Characteristics/Layers.Printed.cs`, one line over
  the existing `PrintedCharacteristics` static. `Query.History.cs`'s look-back now goes through the
  interface instead of reaching past it for `MtgEngine.Rules.Layers.LayerSystem` by name, which is
  what E0d asked for. `QueryTests`' `FakeLayers` implements it as the real printed snapshot,
  because a look-back asks what an object *was* and no `Set` in a test can answer that.
- **`PlayerLimits.CantBeEnchanted` (CR 702.16j)** — the third of the three things protection from
  everything does for a player. The other two were already there; this one had no property at all,
  so a reader could not have asked the question. See the table below for who should ask it.

## The two private copies that now have to go

Both are named in E5d's request and both still compile, so nothing breaks the moment `PlayerMatches`
lands — which is exactly the risk. Until they are deleted the engine still has three readings of a
`PlayerFilter`, one of them wrong:

| File | Line | Who deletes it |
|---|---|---|
| `Triggers/TriggerMatching.cs` | `private bool PlayerMatches` at 460 | **E6d.** This is the copy that evaluates `Also` in the ambient context — the wrong reading. Its one caller is the damage-trigger arm at 244. |
| `Sba/StateBasedActions.Permanents.cs` | `private bool PlayerMatches` at 236 | **E5e.** Same reading as the seam, so this one is pure duplication; its caller is `EnchantsAPlayer`, and `ContextOf(chars)` is the context to hand the seam. |

`ChoosePlayer` and `TargetPlayer` both carry a `PlayerFilter` that nothing enforces yet (E5d said a
third caller was a matter of time); they are the next two callers, and they now have a seam to call.

## CR 613.10: which limit is read where, and which is read by nobody

`ICharacteristics.PlayerRestrictions` computes all of these correctly today —
`Characteristics/Layers.Players.cs` folds every `PlayerCant`, the three older records
(`CantLoseGame`, `CantWinGame`, `CantGainLife`) and `PlayerProtectionFromEverything` into one
`PlayerLimits`, from both sources CR 611.2c and 611.3a name. The gap is entirely on the reading
side. Asked for by the brief; **nothing here is a request for a seam change**, it is the list E5e
should work down.

| Limit | Vocabulary | Read where today | CR |
|---|---|---|---|
| `CantLose` | `PlayerCant(Lose)`, `CantLoseGame` | **`Sba/StateBasedActions.cs:85`** — the only reader. | 104.3, 704.5a-c |
| `CantBeDealtDamage` | `PlayerCant(BeDealtDamage)`, protection | **`Replacement/ReplacementSystem.Limits.cs:36`** — the damage event is replaced with nothing. | 120.3, 702.16j |
| `LifeTotalCantChange` / `CantGainLifeNow` | `PlayerCant(LifeTotalChanges)`, `CantGainLife` | **`Replacement/ReplacementSystem.Limits.cs:38,52`** — a `LifeChanged` of the wrong sign is unchangeable. | 119.3 |
| `CantBeTargeted` | `PlayerCant(BeTargeted)`, protection | **nobody.** | 115.6, 702.16j |
| `CantBeEnchanted` | protection only | **nobody** (new this wave). | 702.16j |
| `CantWin` | `CantWinGame` | **nobody.** | 104.2b |

The three unread ones, with where the reader belongs:

1. **`CantBeTargeted`.** `Query/Query.Targets.cs`'s `MatchesAnyTarget(GameState, PlayerId)` is the
   CR 608.2b re-check for the player half of "any target", and it asks only CR 800.4a — is this
   still a player. It has **no caller anywhere in `MtgEngine.Rules`**, so the casting system's
   choice of targets and the interpreter's re-check both go somewhere else; wiring the limit into
   that static without a caller would be a fourth reading nobody runs. It is left alone
   deliberately. The reader that matters is the one that *chooses* targets at CR 601.2c, in the
   casting system — and the check is not "can't be targeted" flat: both the flag and CR 702.16j are
   worded "by spells or abilities your opponents control", so the reader needs the source's
   controller and the static has no context to give it. **E5e or the casting owner**: add the
   player arm where the object arm's protection check already lives, and give the re-check the
   same overload rather than a second reading.
2. **`CantBeEnchanted`.** Two readers, both outside `Characteristics/`:
   `Sba/StateBasedActions.Permanents.cs`'s `AttachedLegally`/`EnchantsAPlayer` (an Aura already
   attached to a protected player is put into its owner's graveyard, CR 704.5m), and the same
   targeting site as above (CR 601.2c — an Aura spell has no legal target). `EnchantsAPlayer`
   currently answers from the `Enchant` ability alone and never asks the player's limits.
   **E5e**, in the same visit that deletes its `PlayerMatches`.
3. **`CantWin`.** CR 104.2b says an effect that would state this player wins does nothing — so the
   reader is whatever emits the win, and the only losing/winning writes today are `PlayerLost`
   (`Sba/StateBasedActions.cs:87`, `Interpreter/EffectInterpreter.Verbs.cs:188`, `Engine/Game.cs:214`
   for a concession). There is no "wins the game" event at all, so `CantWin` has nothing to gate
   yet and this row is honest state waiting for the verb, not a gap in a reader.

One more that is a reading gap rather than an unread limit: **`EffectInterpreter.Verbs.cs:188`
emits `PlayerLost` for an effect that says "you lose the game" without asking `CantLose`**. CR 104.3b
is a "can't", and CR 101.2 makes a "can't" beat a "does" wherever the loss comes from, so the
state-based path (which does ask) and the effect path disagree. That file is E9's; named here
because this box is the one that read every limit. A concession is correctly ungated — CR 104.3a
puts a conceding player out of the game whatever else is true.

## `Query/LandTypes.cs` deletes when V7 lands

Unchanged from E0d: CR 205.3i's five basic land types are one internal `LandTypes.Basic` read by
the `HasBasicLandType` filter clause and by the domain count in `Query.Amounts.cs`.
`MtgEngine.Cards/Generated/` still holds only `CreatureTypes.g.cs`, `KeywordKind.g.cs` and
`KeywordAction.g.cs`, so the generated basic land types are not on this base and the file stays.
**It deletes in the same landing that generates them** — both readers take the generated list and
the file goes; there is no other caller.

## What I did not do

- `PlayerRestriction` has no flag for drawing, and `CantDrawMoreThan(Who, PerTurn)` is a separate
  `ContinuousEffect` carrying an `Amount`. `Layers.Players.cs` cannot fold it into `PlayerLimits`:
  evaluating an `Amount` needs `IQuery`, and the layer system deliberately does not have one (the
  same reason a conditional static's `While` is skipped there, seam-requests/E7.md). If a card
  needs it read, the reader is the draw verb asking the query directly, not a flag on this record.
  No seam asked for.
- No new vocabulary, no state record, no event, no interface member.
