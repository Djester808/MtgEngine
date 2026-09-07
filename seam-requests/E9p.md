# E9p — a permanent card outside the battlefield, a spell that shuffles itself away, the announced mode ceiling

Wave 19. Three items, all inside `Interpreter/**` and `Query/**`; **no seam change was needed**, so
this file asks for nothing that blocks. It records one wrong rule number in the brief, the one line
P19 is now free to ship, and two shapes the next box should look at.

## 1. `permanent: true` off the battlefield is a question about card types (CR 110.4a)

`Query.cs` answered `ObjectFilter.Permanent` with `obj.Zone.Zone == Battlefield` in every zone, so a
`Choose` among revealed library cards (P19 § 1, card `e2487868`) had no option and everything
revealed went to the graveyard. The arm now reads:

- **battlefield** — true, whatever the types. CR 110.1 with 110.4c: a permanent that has lost all
  its permanent types is still a permanent, so the types cannot be what is asked there.
- **library, hand, graveyard, exile, command** — the card's types against CR 110.4a's six permanent
  types (artifact, battle, creature, enchantment, land, planeswalker).
- **stack** — unchanged, still false. CR 110.4b's "permanent spell" is a narrower set than
  "permanent card" (no land spell), no record asks for it, and guessing between the two is how this
  arm was wrong in the first place. If a card ever needs it, that is a new field, not this one.

**The brief's rule number is wrong.** It cites CR 110.4c for "permanent card"; in the rules text
this repo ships, 110.4a is "permanent card", 110.4b is "permanent spell", and 110.4c is the
lost-all-its-types clause. The code and the tests cite 110.4a. `P19`'s canonical table says the same
thing in words and needs no change.

`Query.Conditions.cs` had its own copy of the six types as a method-local `const`; both now read one
`Query.PermanentTypes`.

## 2. `Shuffle { IntoLibrary: ... }` moves before it shuffles (CR 400.7, 608.2m)

The `Shuffle` arm ignored `IntoLibrary` entirely and only ever shuffled a library. It now moves each
object the selector names into **its owner's** library first (`Relocate`, so it is a real zone change
through the zone system and the object that lands is a new object, CR 400.7), then shuffles each of
those owners' libraries, plus any player the effect named itself. A `Shuffle` that names no object is
the arm it always was and shuffles "you".

That is the whole of `Shuffle { IntoLibrary: Self }`: the resolving spell leaves the stack during its
own resolution, and `CastingSystem.FinishResolving` — which returns `Outcome.Unchanged` for an object
that is no longer in the stack zone — then has nothing to send to the graveyard, which is exactly what
CR 608.2m says ("if it is still on the stack"). **P19 § 2 is unblocked**: the generator's one line
(`case Shuffle { IntoLibrary: Self, Who: null or You }: board.SpellEndsIn = Zone.Library;`) and the
flip of its "no scenario" test can go in.

Tested through a resolved spell whose `Sequence` gains life after the shuffle: the later step still
runs, the library holds the spell, the graveyard is empty, and the resolution still ends with
`StackObjectResolved`. The 608.2m half itself is `Casting/**` and was read, not run, from here.

## 3. The mode question takes the announced ceiling (E3l ask 1 — done)

`EffectInterpreter.Structure.cs`'s `ChooseMode` arm now takes its ceiling from
`run.Context.Chosen[ModeAnnouncement.CeilingKey]` when the key is present (parsed with
`CultureInfo.InvariantCulture`) and from `modal.Max` otherwise, and `Min` is clamped to it so a
ceiling below the record's `Min` asks an answerable question rather than an impossible one. The
`MaxIf` condition is never read here — that is E3l's point, and `ContextFor` already folds
`entry.Chosen` in, so nothing else moved. The arm that runs modes already in `Chosen["modes"]` is
untouched and now has a test pinning that it does not re-check: an entry carrying two modes runs both
though the record's `Max` is one.

E3l ask 2 (the trigger system calling `Announce` in `Create`) is outside this box and still open.

## 4. Not taken in the box

- **E0h § 3, `EffectWalk.Freeze` descending into a `GrantAbility`.** Still true, still no corpus card
  reaching it. It wants a prune predicate on `Rewrite` so a `GrantAbility` is kept whole, which is a
  change to a walker every arm uses; the box had no room to build it and pin it properly.
- **E9o's `Search.OthersTo` split in `Found()`** — `seam-requests/E9o.md` is not on `card-engine`
  (the merge commit `8522a81` is, the file is not), so there was nothing to read. If the split is
  still owed, it needs its own brief.
- **A "permanent spell" filter field** for CR 110.4b, if a card ever spells one.
