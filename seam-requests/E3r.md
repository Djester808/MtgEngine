# E3r — storm, the once-per-turn permission and its additional cost, and the two-zone prohibition

Wave 29. Owns `MtgEngine.Rules/Casting/**`, `MtgEngine.Rules/Keywords/**`,
`tests/MtgEngine.Rules.Tests/Casting*.cs`, `tests/MtgEngine.Rules.Tests/Keyword*.cs`. Anchor
`Casting|Keyword`: **186 green**, up from 170; sixteen new tests in
`tests/MtgEngine.Rules.Tests/CastingTests.Wave29.cs` (twelve) and
`tests/MtgEngine.Rules.Tests/KeywordTests.Wave29.cs` (four). `dotnet format --verify-no-changes`
clean on `MtgEngine.Rules` and `tests/MtgEngine.Rules.Tests`.

## 1. Done — CR 702.40a with 707.10: storm is an expansion, not a marker

`KeywordKind.Storm` left `KeywordSemantics.Unsupported.cs` for `TriggeredExpansions()` and
`KeywordExpander` grew its arm. CR 702.40a is quoted in the rulebook as *"When you cast this spell,
copy it for each other spell that was cast before it this turn. If the spell has any targets, you
may choose new targets for any of the copies"*, so the expansion is exactly that record and no new
vocabulary was needed:

```
TriggeredAbility(
    new SpellCast(new ObjectFilter(Spell: true, Also: new IsThisObject()), new You()),
    new CopySpell(
        new Triggering(TriggerRole.Subject),
        new Difference(new SpellsCastThisTurn(new EachPlayer()), new Fixed(1)),
        MayChooseNewTargets: true))
```

- The trigger names **this object** (`IsThisObject`, the shape the vocabulary already uses for
  "whenever this creature attacks"), and the effect copies the **triggering** spell rather than
  `Self`, because `SpellCast`'s subject *is* the spell that was cast — one reading, and it survives
  the object being a copy or a second face.
- The count is **every player's** spells this turn (CR 702.40a says "each other spell", not "each
  other spell you cast") less one: the storm spell is already in `TurnHistory.SpellsCast` when the
  trigger resolves, so `Difference(…, 1)` is the rule's word "other".
- CR 702.40b ("each instance triggers separately") falls out of two keyword records expanding to two
  abilities; that is asserted rather than special-cased.

**Ask 1 (Interpreter/**), CR 702.40a with 707.10 — `CopyOnStack` cannot make zero copies.**
`EffectInterpreter.Faces.cs:133` reads `var times = Math.Max(1, Number(run, copy.N));`. With storm
that is wrong in the commonest case in the game: the first spell of a turn has a storm count of 0
and must make **no** copies, and this makes one. The clamp is there to give an `N` of `null` its
default of one copy, so the fix is to distinguish the two rather than to clamp:
`var times = copy.N is null ? 1 : Math.Max(0, Number(run, copy.N));`. Nothing else in `Casting/**`
or `Keywords/**` can reach it, and until it lands a storm spell cast first in a turn copies itself
once. **This is the one thing between the expansion above and Flusterstorm (`86bf58f2`) playing.**

**Ask 2 (Triggers/** or State/**), CR 702.40a — "cast *before it*".** The count above is taken when
the trigger resolves, so a spell cast **in response to the storm trigger** is counted and should not
be: CR 702.40a fixes the count at the casts that happened before the storm spell. `TurnHistory` is
in cast order and `TriggerMatching.Wave27.cs`'s `NthSpellThisTurn` already computes the index of a
cast in it, so the exact answer is "that index, less one" — either an `Amount` that reads the
triggering spell's place in the turn's record, or the trigger recording the count as it fires. It is
a narrow miss (it needs a player to respond to the trigger, and it can only ever over-count), so the
expansion ships on `SpellsCastThisTurn` and this is the precision request, not a blocker.

**Not asked: the per-copy unless.** E3q § 2 answered it and nothing has changed — each copy is a
separate spell on the stack (CR 707.10) and each resolution runs the interpreter's `Unless` arm on
its own, so Flusterstorm's "unless its controller pays {1}" per copy needs nothing from this folder.

## 2. Done — CR 118.8 with 116.2a: the two fields on `MayPlayFromZone`

Both halves are read where the permission is read, so a land and a spell cannot disagree about
either (`CastPlan.Permission` is still the only reader of the record).

- **`OncePerTurn` (CR 116.2a).** A spent permission **stops applying** rather than being a separate
  refusal, which is why the second cast is refused with CR 601.3 ("a card in that zone cannot be
  cast without permission") and why the land road gets the same answer for free. It is spent only
  when the cast is actually made: the count reads `TurnHistory.SpellsCast`, which the `SpellCast`
  event writes, and a refused announcement emits none — `Check` runs the whole announcement and
  throws its state away, which is asserted.
- **`AdditionalCost` (CR 118.8).** Added to the spell's non-mana costs in `CastPlan.Alternative`, so
  it is paid at CR 601.2h with everything else by the one payment road, and it is **refused** beside
  either waiver the same record can carry (`WithoutPayingManaCost`, `PayLifeEqualToManaValueInstead`)
  naming CR 118.8 — V17 L047's rule, and the reason is in the rulebook: an additional cost is
  additional to a mana cost, and a permission that has set the mana cost aside has nothing left for
  it to be added to.

**Ask 3 (State/**), CR 116.2a — the permission has no identity on the cast record.**
`SpellCastRecord(ObjectId Spell, PlayerId By, Zone From)` is the closest the state comes to saying
which permission allowed a cast, so the limit counts *this player's casts from that zone this turn*.
Two consequences, both narrow and both documented at the reader:

1. two once-per-turn permissions over the same zone share one count, and a cast from that zone made
   by another road (flashback, an instruction under CR 608.2g) spends it;
2. a **land** played under a once-per-turn permission is not counted at all — a land play is not a
   cast and nothing in `TurnHistory` records the zone it came from.

The fix for both is one field: the id of the effect that permitted the play, on the cast record and
on a land-played record. That is `State/**`'s, and the land half additionally wants a per-turn record
of lands played by zone. Until then the limit is exact for the shape the corpus prints (one
permission, spells, one zone) and over-strict in the two cases above.

## 3. Done — CR 601.3: one restriction over two zones

`CantCastSpells` had **no reader anywhere in `MtgEngine.Rules`** — the record existed and nothing
asked it, so every "players can't cast spells from graveyards or libraries" was silently ignored.
`CastingSystem.Wave29.cs` adds `RequireNoCastProhibition`, called from `CastCore` before anything is
announced:

- the record's own `CastFrom` **list** is the reading, not the filter's single-zone
  `CastFrom`/`NotCastFrom` (L047): one restriction over two zones, one duration;
- **no zones at all means every zone**, so a bare "you can't cast spells" prohibits everywhere and
  writing the whole zone list out is never needed;
- the filter is asked about the card **where the card is now**, the way `MayCastAsThoughItHadFlash`
  asks its own — the check runs before CR 601.2a moves anything, so a filter naming no zone would
  otherwise be read against the battlefield and match nothing;
- it is asked on **both** roads into `CastCore`. CR 601.3 is a conjunction: an instruction to cast
  (CR 608.2g) is the effect that *allows* the cast, and allowing it does not answer the prohibition.

## Not done in this box

- **The storm expansion is not verified through a real game.** Flusterstorm (`86bf58f2`) needs ask 1
  above before its copies exist at all, and the per-card verify run needs the corpus, which is in the
  main checkout's `MtgEngine.Api/bin`. The expansion is proved by its four unit tests only;
  **unverified** through the runner.
- **No reading of `CantCastSpells` outside the cast.** CR 601.3 is about casting; a prohibition that
  should also stop an *activation* or a land play would be a different record, and nothing in the
  corpus asked for one here.
