# E3e — seam requests

One seam, and it is the one the brief predicted. Restricted mana needed nothing: `ManaPool.Restricted`
and the `ManaChanged` fold landed in the seams after wave 6, so `CastingSystem.ManaFrom` emitting a
block and `RestrictedPool` reporting which block paid are both inside `Mana/` and `Casting/`.

---

## An Aura cast at a player has nowhere to attach — `GameObject.AttachedToPlayer` (CR 303.4f)

`MtgEngine.Rules/State/GameObject.cs`. `AttachedTo` is an `ObjectId?`, so CR 303.4f — "the Aura
enters the battlefield attached to the object **or player** it was cast at" — is only half
implementable. `CastingSystem.ResolveTop` now reads the stack entry's `Targets` and enters the
permanent attached to the object it was cast at; an Aura whose stack entry carries only
`TargetPlayers` still enters attached to nothing, and CR 704.5m buries it on the next state-based
action check. It is not countered on resolution, because its target is not illegal — the engine
simply cannot record the attachment.

This is the wave-1 row E5 filed ("Auras attached to players — **Wave 2.** `AttachedToPlayer` on
`GameObject` when the first 'enchant player' card is translated", `seam-requests/DECISIONS.md`),
still open. The first such card is now translated in the pilot slice, so the trigger the decision
named has fired.

### The state (one field)

```csharp
/// <summary>An Aura attached to a player, CR 303.4f; CR 704.5m buries one attached to neither.</summary>
[RuleRef("303.4f")]
public PlayerId? AttachedToPlayer { get; init; }
```

### The event

`Events/ObjectStateEvents.cs` or `Events/GameEvent.cs` — `ObjectChanged` already carries
`ObjectId? AttachedTo` and `bool ClearAttachment`, so the smallest change is one more optional
field folded the same way:

```csharp
public sealed record ObjectChanged(
    ObjectId Id, …, ObjectId? AttachedTo = null, bool ClearAttachment = false,
    PlayerId? AttachedToPlayer = null, bool? DealtDeathtouchDamage = null) : GameEvent
```

with `AttachedToPlayer = ClearAttachment ? null : (AttachedToPlayer ?? obj.AttachedToPlayer)` in
`Apply`, beside the line that already does it for `AttachedTo`.

### What lands with it, and where

- `CastingSystem.AuraAttachment` returns the player when the stack entry carries one, and
  `ResolveTop` puts it on the same `ObjectChanged` it already emits — mine, one line each.
- `StateBasedActions.Permanents.AttachedLegally` (CR 704.5m) has to accept a player attachment as
  legal, or every such Aura is buried the instant it enters — **E5's file**, and the reason this
  cannot be half landed: the field without that arm changes nothing a player would see.
- `ViewProjector` and the layer system read the new field wherever they read `AttachedTo`.

**Until it lands:** an "enchant player" Aura resolves onto the battlefield attached to nothing and
CR 704.5m puts it into the graveyard. It is named in a test rather than left silent —
`AuraResolutionTests.An_Aura_cast_at_a_player_still_enters_attached_to_nothing`.
