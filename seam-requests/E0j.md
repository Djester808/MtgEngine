# E0j — a basic land type carries its intrinsic mana ability (CR 305.6, 305.7)

Wave 21. `LayerSystem.IntrinsicManaAbilities` is in `MtgEngine.Rules/Characteristics/Layers.Intrinsic.cs`
and is applied in `Layers()` between layer 5 and layer 6; eight tests in
`tests/MtgEngine.Rules.Tests/LayerTests.Intrinsic.cs`. No seam moved, no other folder was touched.
The whole Rules project is green (939 passed, 0 failed; 931 before).

## 1. What landed, and where in the layers it sits

CR 305.6 gives an object with **the land card type and a basic land type** the ability
"{T}: Add [mana symbol]" whether or not it prints one, and CR 305.7 gives it to a land that gains a
basic land type from an effect. Both are one read if it is taken off the subtypes rather than the
printed face, so the arm runs after `Layer4Types`/`Layer5Colors` and **before** `Layer6Abilities`:
the object carries the intrinsic ability into layer 6 the way it carries a printed one, so
`LoseAllAbilities` (CR 613.1f) removes it and a later grant sits beside it. Nothing about it is
stored — the layers are recomputed on every call (CR 613.5), so a land that stops being a Forest
stops having `{T}: Add {G}` in the same read.

The ability is `ActivatedAbility([TapSelf], AddMana(ManaProduction(Fixed: "{W}"…)))` — a mana
ability by CR 605.1a **by construction** rather than by inspection: no target, could add mana on
resolution, not a loyalty ability, and nothing moved to or from a library.

**R1 § 3 is answered.** `TapForManaStep` reads the object's current abilities through
`ICharacteristics.Of` and takes the first ability CR 605.1a calls a mana ability, so it now works on
a real basic land and on all five `ScenarioFiller` basic lands, whose faces still print nothing —
which is the honest fix R1 asked for and not the "fillers print the ability themselves" fallback, so
there is no doubled ability to unwind. The runner was not touched. `00d8efa6` (the Aura on a Forest)
is unblocked as far as this half goes; its `ManaPoolIs` still has to name the total of the land's own
mana and whatever the trigger adds, exactly as R1 § 4 said.

## 2. The non-land answer, and why it is the type and not the supertype

CR 305.6's sentence is "an object with **the land card type** and a basic land type", so the gate is
`CardType.Land`, and a creature given the Forest subtype alone gains nothing. The test asserts both
halves on one object: the subtype alone gives it no mana ability, and `AddTypes(Land, ["Forest"])`
on the same creature gives it `{T}: Add {G}`. Three things it is deliberately **not** gated on:

- **not the `Basic` supertype** — CR 205.4a/305.8: a nonbasic dual land with two basic land types
  has both abilities, and "basic" is a different question;
- **not the battlefield** — CR 305.6 says "object", so a land *card* in a library or a graveyard has
  the ability too. It is unactivatable there because the ability's `Where` is `Zone.Battlefield`
  (CR 602.5a/605.3a), not because the layer system withheld it;
- **not face-up-ness as a special case** — a face-down permanent is already a 2/2 creature with no
  subtypes at layer 1b (CR 708.2a), so it fails the land-type gate on its own and taps for nothing.
  There is a test for that so a later face-down change cannot silently grant `{G}`.

## 3. Two things I did not build (no request, recorded so the next box does not re-derive them)

1. **CR 305.7's ability *loss*.** The rule's first half — "if an effect **sets** a land's subtype to
   one or more of the basic land types, ... it loses all abilities generated from its rules text, its
   old land types, and any copiable effects affecting that land" — is not built. `SetTypes` with
   `KeepSubtypes: false` replaces the subtype list in layer 4 and leaves the printed abilities alone,
   so a Blood Moon-shaped effect would leave a nonbasic land's printed abilities on it beside the new
   `{T}: Add {R}`. Building it means layer 4 recording the loss and layer 6 reading that record
   (the loss is a layer-6 event, CR 613.1f, even though its cause is a layer-4 effect), and it
   changes the answer for every existing `SetTypes` test, so it wants its own box and a card that
   asks for it. **No card in the corpus asks yet**; the gaining half, which is what R1 needed, is in.
2. **One public reading of CR 605.1a.** R1 § 2 counted three private copies of "is this a mana
   ability" (`CastingSystem.ManaAbilities`, `Query.Predicates`, the runner). This arm adds no fourth
   — it *builds* an ability that satisfies 605.1a rather than recognising one — but it does make the
   question load-bearing on more objects than before, since every basic land now has an ability the
   three predicates must agree about. If a shared reading is ever wanted, it is still a cross-folder
   change no folder here owns.

## 4. The colour table

Which mana goes with which basic land type is CR 305.6's own sentence and lives in
`BasicLandManaTable()`, in a method rather than a static field (partial-class initialiser order).
The five *names* are already generated rules data — `LandTypes.Basic`, from CR 205.3i — and
`Every_basic_land_type_the_rules_name_carries_the_mana_that_rule_pairs_with_it` iterates that
generated set and asserts its size, so a sixth basic land type in a new rules file fails a test
instead of tapping for nothing. **Ask, if it is wanted:** the pairing could be generated beside the
names in `LandTypes.g.cs` (CR 305.6 states it in one parseable sentence), which would delete the
table. `MtgEngine.Cards/Generated/**` and `tools/gen-vocabulary.js` are not mine, and the test is a
sufficient guard, so I did not.
