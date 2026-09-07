# E0h — the layer system evaluates a static ability's amounts; a resolved effect's targets need a binding

## 1. Done without a seam: CR 604.3 and 611.3a amounts (cards `162572f2`, `80ea56ad`)

`LayerSystem` now evaluates a static ability's `Amount` through the query every time the
characteristics are read (`Characteristics/Layers.PowerToughness.cs`): a characteristic-defining
`SetPt` (CR 604.3, layer 7a) and a static `ModifyPt`/`SetPt` (CR 611.3a) with a `CountOf` count the
board as it is now, with the ability's source as "this" and its controller as "you" (CR 109.5). The
query is built on the layer system itself (`new Query.Query(this)`, lazily); the recursion that
invites — counting "lands you control" reads every object, one of which is the object being
counted — is cut per object on the thread: the re-entrant visit reads that object without its own
layer-7 amounts. That is the CR 613.8 dependency case left unbuilt, and it is the one that matters
for "nontoken creatures you control are lands" plus "power equal to the number of lands you
control": the object counts itself (test
`A_characteristic_defining_count_includes_the_object_when_another_static_makes_it_a_land`).

A resolved effect's amounts stay frozen by `EffectWalk.Freeze` (CR 611.2c); a non-`Fixed` amount
reaching the layers from an `ActiveEffect` is now a `RefusedException` with rule `611.2c`, never a
`NotImplementedException`. Every other unbuilt shape in `Characteristics/**` (a control change
naming a player other than "you", a filter field the matcher does not answer, a comparison against a
counted amount, a player reference the matcher does not answer) is likewise refused with its rule.

No change to `Game.cs`, `Seams.cs` or the interpreter was needed for these two cards.

## 2. Ask (State/* and the interpreter's `Create`): `ActiveEffect` carries the announced objects (card `b01e698b`)

"This land becomes a copy of target land" is `Apply { To: Self, What: BecomesCopyOf { Of: Target } }`.
CR 611.2c with 601.2c: the object the copy effect copies is the one the ability announced, fixed as
it resolved. The layer system cannot read it from the board, and `Freeze` cannot bind it into the
effect tree: the vocabulary has no selector that names an object id (nor should it — it is card
language), and a Rules-side `Selector` subtype inside `ActiveEffect.Effect` would fail
`EventLogSerializer` (`ContinuousEffectCreated` carries the `ActiveEffect`; `Selector` is
`[JsonPolymorphic]` with the default fail-on-unknown-derived-type handling), which every verify
writes. So the binding has to live beside the effect, the way `DelayedTriggerState.Remembered`
already does for a delayed trigger.

**Exact ask**

1. `MtgEngine.Rules/State/GameState.cs`: add a last positional parameter to `ActiveEffect`,
   defaulted so no constructor call changes:

   ```csharp
   ImmutableDictionary<string, ImmutableList<ObjectId>>? Remembered = null
   ```

   The announced objects the effect's selectors name, by key: the target slots
   (`Query.Query.TargetSlot(n)`, which `EffectWalk.KeyTargets` gives every unkeyed `Target`), the
   card's own target keys, and the remembered keys (CR 601.2c, 608.2h).

2. `MtgEngine.Rules/Interpreter/EffectInterpreter.Verbs.cs`, `Create`: pass
   `run.Context.Remembered` as that parameter (the context after the announcement seeding, so the
   target slots are in it). Nothing else in `Create` changes; `Freeze` stays as it is.

3. `MtgEngine.Rules/Characteristics/Layers.cs`, `Applicable` and `Layers.Statics.cs`,
   `FromResolutions`: change `new Layered(e.Effect, e.Timestamp, false, e.Source, e.Controller)` to
   add `Bound: e.Remembered`. The layer half is already built and waiting on that one argument:
   `Layered.Bound` and `ObjectsFor(..., bound)` in `Layers.Statics.cs` resolve `Target`, `AnyTarget`,
   `Remembered` and `Choose` from the binding by key (an announced object that has since left the
   game is dropped, CR 400.7), and refuse with rule `611.2c` when the effect carries no binding for
   the key (test `A_copy_of_a_target_the_effect_was_not_created_with_is_refused_never_thrown`).

4. Then add to `LayerTests` the positive half: an `ActiveEffect` whose `Effect` is
   `BecomesCopyOf(Target(Key: "target:0"))` and whose `Remembered` maps `"target:0"` to a Forest's id
   makes the object it applies to a copy of that Forest (name, types, subtypes) and not of itself.

Until 1–3 land, `b01e698b` is refused at the first read of the Stage's characteristics after the
ability resolves, with the rule, rather than crashing.

## 3. Noted, not asked

- `EffectWalk.Freeze` descends into a `GrantAbility`'s ability and freezes the amounts *of the
  granted ability's own text* (an `X` or a count inside "gains '{T}: ...'"). CR 611.2c fixes the
  resolving effect's numbers, not the text of an ability it grants, which is read when that ability
  resolves. No corpus card reaches it yet; when one does, `Rewrite` needs a prune predicate so a
  `GrantAbility` is kept whole.
- The static-ability context passes an empty `Chosen` map; a static whose count reads "the chosen
  type" (CR 607) will need the source's own chosen values threaded into `ContextOf`.
