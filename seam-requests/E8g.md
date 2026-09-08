# E8g — wave 29: X reaches the enters-with, the prevention says its number, and a forbidden entry is refused

Owned: `MtgEngine.Rules/Replacement/**`, `tests/MtgEngine.Rules.Tests/ReplacementEffectTests*.cs`,
`tests/MtgEngine.Rules.Tests/ZoneReplacementTests*.cs`. Landed:
`Replacement/ReplacementSystem.Wave29.cs` (new partial), three edits in `ReplacementSystem.cs`,
five in `ReplacementSystem.Applying.cs`, one arm in `ReplacementSystem.Limits.cs`, and nine tests in
`tests/MtgEngine.Rules.Tests/ReplacementEffectTests.Wave29.cs` (`ReplacementEffectTests` anchor
**45**, up from 36). Build clean, format clean on both projects; the four neighbouring suites
(`ZoneReplacementTests`, `PlayerLimitReplacementTests`, `CombatReplacementTests`, `ZoneChangeTests`)
and `StateBasedActionTests` are 141 green together.

## 1. Ask — `Query/**` (E5-series): `ObjectFilter.InAnyOf` is not read

`Query.ZoneOf` (`Query/Query.cs:25`) reads `filter.In` alone and defaults to the battlefield, so
`Matches` refuses every object for a filter whose only zone rider is V17's `InAnyOf` — a graveyard
card is asked about as if it should be on the battlefield and never matches.

The replacement road cannot wait for it, so `ReplacementSystem.Admits` answers the plural rider
itself: an object is in exactly one zone (CR 400.1), so the zones the rider lists are checked against
the object's own zone and the filter is handed to the query with `In` set to that one zone and
`InAnyOf` cleared. Every other criterion is still the query's.

**Ask.** `ZoneOf` should admit the plural — a filter matches when the object's zone is `In`, or is one
of `InAnyOf` — and `Candidates` should enumerate all of them. Then `Admits`'s rewrite is one line to
delete. Until it lands, any *other* reader handed an `InAnyOf` filter silently admits nothing, which
is a card played wrongly with nothing in the log to say so. L047 already forbids `In` and `InAnyOf`
together, so the two spellings cannot disagree.

## 2. Ask — `Zones/**` (E1-series): CR 608.3e, where a refused permanent spell goes

`CantEnterBattlefield` is now read in `ReplacementSystem.Forbidden` (CR 613.11 with 101.2), so the
`ObjectMoved` is dropped and the object stays where it was. That is right for a card returned or
searched into play and for a token that is never created (CR 111.5).

It is **not** the whole rule for a resolving permanent spell: CR 608.3e says a permanent spell whose
controller can't put it onto the battlefield is put into its owner's graveyard instead. The
replacement road can only refuse the move — it does not know the move was a resolution and must not
invent a second move. The zone system proposed it and is the one place that knows.

**Ask.** Where `ZoneSystem` resolves a permanent spell, a proposal the replacement system refuses
(the outcome carries no `ObjectMoved`) should be followed by the CR 608.3e move to the owner's
graveyard. Nothing else changes shape; the refusal is already observable as an outcome with no move.

## 3. Recorded — X is read off the departed spell and nowhere else

E9w's diagnosis was exact. `ReplacementSystem.Context` passed `X: null`, `Query.Amounts.cs:21` reads
`X => context.X ?? 0` (CR 107.3b), so `EntersWithCounters { N: X }` added nothing and the 0/0 was
buried by state-based actions (CR 704.5f). `ChosenX` reads `ObjectMoved.Departed.Stack.X`: the value
chosen as the spell was cast (CR 601.2b) lives on the spell, and the arriving permanent is a new
object with no stack entry of its own (CR 400.7). A move with no departed object, or a departed
object that was never on the stack, still reads none — the honest zero, which the second test pins.

This is every "enters with X counters" card, not one; it also feeds every other amount a replacement
evaluates, since `Context` is the one context the road builds.

## 4. Recorded — a prevention's amount, and the sequence that used to be dropped

`Query.PreventedDamageAmount` (`"prevented-damage"`) is now written into `Chosen` whenever a
prevention ran in this application, **including as `"0"`** for damage that can't be prevented
(CR 615.12). It is absent when no prevention ran, which is the distinction E5l § 1 asked for and the
reader refuses on. The amount is `min(shield, damage)` — what came off the event, never what the
effect offered.

One thing the ask did not name and the shape needs: `Perform`'s `Sequence` arm returned as soon as
the event was gone, so `[PreventThatDamage, <the rest>]` dropped the rest whenever the prevention took
all of the damage — exactly the card E5l is written for. The sequence now runs the remaining steps
through the CR 614.1a "instead" road when a prevention has run (CR 615.12's "any additional effects
they have will take place"), and is unchanged when none has: a sequence whose event vanished for any
other reason still stops where it did, so no existing reading moved.

## 5. Not done in this box

- **No corpus card was played through `verify`.** The box is the Rules project with a
  `ReplacementEffectTests` filter and never loads the corpus, and the corpus lives under the main
  checkout's `MtgEngine.Api/bin`. Walking Ballista (`4b515bb0`) is **unverified end to end**; what is
  proved is that a spell cast for X=3 puts three `+1/+1` counters on the permanent that enters.
- **No mutation check was run on the three roads.** The nine tests are written against the arms and
  each asserts a number or an absence that the old code could not produce (0 counters, a missing key,
  a dropped step, an entry that happened), but none was watched failing first.
- `CantEnterBattlefield` is read off `state.ActiveEffects` only, the road `DamageCantBePrevented`
  already uses. Whether a static ability of that shape actually reaches `ActiveEffects` is the layer
  system's (`FromStaticAbility` is a field there, so the road exists); the test puts the effect in
  force by hand.
