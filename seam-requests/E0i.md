# E0i — a static ability's filter is answered by the query, not by a second matcher

## 1. Done without a seam: CR 611.3a with 613.1 (card `1182591d`, Gauntlet of Power)

`LayerSystem` no longer refuses a filter field its own matcher cannot read. A static ability's
`Each` filter now takes one of two roads (`Characteristics/Layers.Matcher.cs`):

- the narrow matcher, unchanged, for a filter made only of the fields it has always read;
- `IQuery.Matches` — the one matcher that answers the rest — for a filter carrying `Also`,
  `ManaValue`, `Attacking`, `Blocking`, `Blocked`, `InCombat`, `Historic`, `Outlaw`, `Spell`,
  `IsCommander`, `HasBasicLandType`, `ManaCostIs`, `NotInCommanderIdentityColors`, a
  `Power`/`Toughness`/`Loyalty` bound that has to be counted rather than read, or a
  `ControlledBy`/`OwnedBy` naming a player beyond the four the narrow matcher knows.

The context is the static ability's source as "this" and its controller as "you" (CR 109.5), and
it now carries **the source's own `Chosen` values** (CR 608.2d) — the note E0h left in §3. That is
what makes `1182591d` work: "creatures of the chosen color get +1/+1" is `Also: IsChosenColor`
reading the colour the artifact was given as it entered.

**The recursion guard.** The query reads characteristics through `ICharacteristics.Of`, and the
statics being gathered are gathered from every object on the battlefield, so an unguarded read
would gather them again to answer the very filter that is gathering them — a stack overflow, not
a wrong answer. A thread-static depth counter (`_matching`) makes `Of` and `ControllerOf` compute
**without** the board's statics while a filter is being answered. That is not new behaviour: the
narrow matcher has always computed `withStatics: false`, it is what `seam-requests/E7.md` §4
records, and effects from resolved spells and abilities stay visible, which is what the example
under CR 613.9 needs. CR 613.8's dependency rules remain unbuilt, and this is where they would go.

Nothing crashes: the query refuses with its own rule (a `CanBlock` predicate is refused with
509.1a, not thrown), and a `NotImplementedException` from anything it has not built is turned into
a `RefusedException` with 611.3a rather than escaping the layer system.

Five tests in `tests/MtgEngine.Rules.Tests/LayerTests.Matcher.cs`; the four behavioural ones were
run against a build with the routing disabled and all four fail there. 887 tests green in
`tests/MtgEngine.Rules.Tests`.

## 2. Noted, not asked (two gaps that are the query's, not the layer system's)

- **`ObjectFilter.ManaCostIs` and `ObjectFilter.NotInCommanderIdentityColors` are read by nobody.**
  `Query.Matches` has no arm for either — it does not refuse them, it ignores them, so a filter
  carrying one silently admits objects it should not. The layer system used to ignore them too, so
  routing them to the query is no worse, but neither is right. They want an arm in
  `MtgEngine.Rules/Query/Query.cs` (an E0-series file, not mine): a mana cost compared symbol by
  symbol, and the commander identity read off the game's commanders. Four card files under
  `cards/` mention one of the two.
- **A static ability with a `While` condition is still skipped** (`Layers.Statics.cs`), so its
  effect never applies. `IQuery.Holds` is reachable from here now — the same guard would cover it —
  and that is the natural next step; it is a different card shape from this one's, so it is left to
  whoever owns the wave that needs it rather than folded in here.

## 3. No seam changed

`ICharacteristics`, `IQuery`, `EffectContext`, `ActiveEffect`, `GameObject` and the vocabulary are
untouched. Every file changed is under `MtgEngine.Rules/Characteristics/**` or
`tests/MtgEngine.Rules.Tests/Layer*.cs`.
