# E7b — player-scoped continuous effects

Wave 5. `MtgEngine.Rules/Characteristics/**` and `MtgEngine.Rules/Sba/**` now compute and honour
the prohibitions of CR 613.10. Three seam changes are needed; none of them is in a file I own.

## 1. Hoist the question onto `ICharacteristics` (one line)

`MtgEngine.Rules/Engine/Seams.cs`, in `ICharacteristics`:

```csharp
public interface ICharacteristics : ISubsystem
{
    Characteristics Of(GameState state, ObjectId id);

    PlayerId ControllerOf(GameState state, ObjectId id);

    /// <summary>Every player-scoped prohibition in force on this player right now, CR 613.10.</summary>
    [RuleRef("613.10")]
    PlayerLimits PlayerRestrictions(GameState state, PlayerId player);
}
```

`PlayerLimits` and the interim interface `IPlayerLimits` are already declared in the namespace
`MtgEngine.Rules.Engine` (in `MtgEngine.Rules/Characteristics/PlayerLimits.cs`, so they sit beside
the layer system they come from), which is why the addition above needs no `using`:

```csharp
public sealed record PlayerLimits(
    PlayerRestriction Cant = PlayerRestriction.None,
    bool CantWin = false,
    bool CantGainLife = false,
    bool ProtectionFromEverything = false)
{
    public static readonly PlayerLimits None = new();

    public bool Has(PlayerRestriction restriction);   // None is never "in force"
    public bool CantLose { get; }                     // PlayerRestriction.Lose
    public bool LifeTotalCantChange { get; }          // PlayerRestriction.LifeTotalChanges
    public bool CantBeDealtDamage { get; }            // BeDealtDamage, or protection from everything
    public bool CantBeTargeted { get; }               // BeTargeted, or protection from everything
    public bool CantGainLifeNow { get; }              // CantGainLife, or a life total that can't change
}
```

`LayerSystem` already implements the member (`Characteristics/Layers.Players.cs`), so the hoist is
the interface line plus deleting `IPlayerLimits` and its two uses:

- `StateBasedActions.LimitsOn` becomes `characteristics.PlayerRestrictions(state, player)` — it
  currently pattern-matches `characteristics is IPlayerLimits` and reports no restrictions
  otherwise, which is the pre-E7b behaviour and is what every test fake gets today.
- `PlayerEffectTests` needs no change; it calls `LayerSystem` directly.

Every fake `ICharacteristics` in the test projects then needs the member. Only
`StateBasedActionTests.Fakes.cs`'s `FakeCharacteristics` is in my files; the coordinator's hoist
should add `=> PlayerLimits.None;` to the rest (grep `: ICharacteristics`).

**`PlayerRestriction` is missing two flags.** "Can't win the game" and "can't gain life" have no
flag in `MtgEngine.Cards/Continuous.cs`, so `PlayerLimits` carries them as bools beside the flag
set. If V4 adds `Win = 1 << 4` and `GainLife = 1 << 5` to the enum, the two bools collapse into
`Cant` and the record loses two fields; the derived properties above keep their names either way,
so nothing that reads this changes. That is a vocabulary change, so it is not mine.

## 2. What the replacement system should ask (CR 615, 616.1)

`ReplacementSystem` is the only place that drops an event, so it is where these three prohibitions
bite. It already holds no `ICharacteristics`; it needs one, and then:

- **`DamageDealt` with `ToPlayer` set** — before any shield of CR 615.1 is considered, if
  `characteristics.PlayerRestrictions(state, damage.ToPlayer).CantBeDealtDamage` the damage is
  prevented entirely (CR 120.3 for the flag, CR 702.16j for protection from everything: "all damage
  that would be dealt to such a player is prevented"). This is not a shield with a source filter —
  no `From` is consulted and it does not run out — so it belongs beside `Shields`, not inside it.
- **`LifeChanged`** — if `PlayerRestrictions(state, event.Player).LifeTotalCantChange`, the event is
  dropped whatever its sign (CR 119.3 read as a prohibition, CR 101.2). If only `CantGainLifeNow`
  holds, drop it when `Delta > 0` and let a loss through.
- **Targeting** is not the replacement system's: `CantBeTargeted` is read where targets are chosen
  and rechecked (CR 115.6, 115.7, 702.16j). That is E9/E13's path, not one I can reach.

The one thing I would ask for in the shape of the seam: `Replace` takes `(state, proposed,
affected)` and the affected player is already there, so no signature changes — only the
`ICharacteristics` constructor argument, in both compositions in `Subsystems.cs`.

## 3. Not asked for, recorded

- **CR 104.2a overrides "can't win".** The rule says the last-player-standing win "happens
  immediately and overrides all effects that would preclude that player from winning the game", so
  `StateBasedActions.Ending` deliberately does not consult `CantWin`, and a game with one player
  left ends even while an effect says they can't win. `CantWin` bites on **104.2b** — an effect that
  states a player wins — which nothing emits yet; the arm belongs to whoever writes that effect.
  `PlayerEffectTests.The_last_player_remaining_wins_even_while_an_effect_says_they_cant_win` is the
  guard, so a later reading of "can't win" cannot quietly turn 104.2a off.
- **704.5b's flag is now consumed by a prevented loss too.** A player who can't lose and drew from
  an empty library keeps the flag cleared, because 704.5b is "since the last time state-based
  actions were checked" and that check has now happened. Without this the player would lose the
  instant the can't-lose effect ended, which is not how the condition reads.
- **`StaticAbility.While` is still skipped**, as in the object-scoped gather; the conditional
  statics wait on `IQuery.Holds` (wave-1 row E7, still open).
