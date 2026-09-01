# Playable, not merely compiled

What the coverage number means, measured rather than argued. The project reports **15,036 of
32,765 cards (45.9%) fully read**. That is a claim about the compiler: every printed line of those
cards was recognised. It is not a claim that any of them can be put in a deck and played, and this
document is the gap between the two.

Everything here comes from `tests/MtgEngine.Api.Tests/CardPlayabilityTests.cs`, which is calibrated
against `CardCompilerCoverageTests` before it measures anything else — its first test asserts that
it reads the same 32,765 cards and computes the same 15,036, so a harness quietly reading a
different corpus fails rather than reporting a plausible fiction. The corpus is not in git, and
without it every test in the file reads zero cards; that is what the calibration test also guards.

## Which commit these numbers are

Measured on **`05b5e2a`**, the commit this work was pinned to, where `CardCompilerCoverageTests`
reports `complete: 15036 (45.9%)`. The compiler is being worked on in parallel and the tip has
since moved to 15,262 (46.6%); every ratchet in `CardPlayabilityTests` is a constant asserted
against `05b5e2a`, so on a newer tip the calibration test is what fails first, deliberately and
loudly, rather than the numbers below quietly drifting. Re-run it and update the constants.

The *shape* of every finding here is independent of that: the gate does not consult the compiler
at all, so the compiled total can move without moving the admitted total by a single card.

## The headline: 917, not 15,036

**The deck-legality gate as shipped admits 917 cards.** Not 15,036, and not 21,455.

| | cards |
|---|---|
| corpus (playable in some format) | 32,765 |
| compiler reads every line of | 15,036 |
| `CardCoverage` says needs no code at all | 899 |
| **gate admits, with the pool `Program.cs` registers** | **917** |
| … of which are fully read | 916 |
| gate admits, with `CompiledPool` wired in instead | 21,455 |
| … of the fully read cards | 14,640 |
| … fully read and refused anyway | 396 |
| … **half-read and admitted anyway** | **6,815** |

The gate is `GameTableService.Unsupported`, and it is the real one — the harness constructs the
service and calls it rather than re-implementing its rule, because a copy would be a measurement
of the copy.

### Why 917

`Program.cs:158` registers `CardPool` as the `IAbilitySource`:

```csharp
builder.Services.AddSingleton<CardPool>();
builder.Services.AddSingleton<IAbilitySource>(sp => sp.GetRequiredService<CardPool>());
```

`CardPool` is the five basic lands plus the hand-written `StarterCards`. It has never heard of the
compiler. `GameTableService.IsPlayable` therefore admits a card only when

- `CardCoverage.IsFullyCovered` says it needs no code — a vanilla creature, or one whose entire
  text is keywords on the `Honoured` list — which is **899** cards; or
- `CardPool` finds a hand-written script by name, which adds **18** more.

`CompiledPool` — the ability source that compiles a card's rules text, the thing that makes the
compiler more than an analysis tool — **is constructed in the test projects and nowhere else.**
`MtgEngine.Rules/Cards/CompiledPool.cs` is referenced by `CardCompilerInvariantTests`,
`CompiledCardSoakTests`, `GameHubTests` and `CompiledCardBehaviourTests`. Production does not
reference it at all.

So the 15,037-card figure and the playable game are not connected by any wire. **The real playable
count is 917 cards, 2.8% of the corpus and 6.1% of the fully read set**, and it is where it was
before the compiler existed.

This is not a claim that the compiler is wrong. It is a claim that nothing plugs it in.

### The gate also asks the wrong question, in both directions

Wiring `CompiledPool` in would not by itself be right. `IsPlayable` asks whether any of five
collections is non-empty:

```csharp
return _abilities.SpellOf(card) is not null
    || _abilities.TriggersOf(card).Count > 0
    || _abilities.StaticsOf(card).Count > 0
    || _abilities.ActivatedOf(card).Count > 0
    || _abilities.ReplacementsOf(card).Count > 0;
```

**It under-admits 396 fully read cards.** A card whose whole text compiles into something that is
not one of those five has all five empty and is refused although every line of it was read. The
harness prints them; they are all of one shape:

| card | text | where it compiles to |
|---|---|---|
| Valeron Outlander | `Protection from black` | keyword flags |
| Zodiac Goat | `Mountainwalk` | keyword flags |
| Contagious Nim | `Infect` | keyword flags |
| Wei Strike Force | `Horsemanship` | keyword flags |
| Welkin Tern | `Flying` / `… can block only creatures with flying` | a block restriction |
| Ashen Monstrosity | `Haste` / `… attacks each combat if able` | `AttacksOnlyIfDefenderControls` and siblings |
| Mocking Sprite | `Instant and sorcery spells you cast cost {1} less` | `CostReducers` |
| Starnheim Courser | `Artifact and enchantment spells you cast cost {1} less` | `CostReducers` |

Every one of those is behaviour the engine implements. None of it is in the five collections the
gate inspects, and `CardCoverage.Honoured` deliberately does not list protection, infect,
horsemanship or landwalk — so neither arm admits them.

**It over-admits 6,815 half-read cards, which is the dangerous half.** A card with one ability that
compiled and three lines that did not has a non-empty collection, so the gate lets it into a game.
That is precisely the failure `GAME_ENGINE_FEATURE.md` says the gate exists to prevent — "a card
treated as vanilla looks like it works and does not, and players cannot tell which of the two they
are looking at."

`CompiledPool` already has the method that would prevent it:

```csharp
/// <remarks>
/// A deck containing one is not playable. The alternative — letting it through with the
/// lines that did compile — produces a game that looks right and is quietly playing a
/// different card, which is the failure mode the whole design exists to avoid.
/// </remarks>
public bool Refuses(CardDefinition card) => !For(card).IsComplete;
```

**`Refuses` is called from nowhere in the repository.** It is written, documented, and dead.

Note that `GameHubTests` builds its `GameTableService` with `new CompiledPool()`, so the hub tests
run against a wiring production does not have. That is a second reason the gap has stayed
invisible: the tests exercise the pool the app does not use, on the gate the app does use.

## What no test ever plays

A card that compiles, is admitted, and is never played by any test is **unverified**. That is a
much weaker claim than **broken**, and the two are kept apart below.

The three soaks in `CompiledCardSoakTests` select their cards by predicate, so what they never
touch is computable exactly rather than instrumented.

| | complete cards |
|---|---|
| fully read | 15,036 |
| selected by the permanent soak (`IsPermanent`) | 11,334 |
| selected by the spell soak (instant/sorcery) | 2,943 |
| **selected by neither** | **759** |

The 759 are **every one of them a land**, and the cause is a single predicate:

```csharp
private static bool IsPermanent(CardType types) =>
    (types & (CardType.Creature | CardType.Artifact | CardType.Enchantment
        | CardType.Planeswalker)) != 0
    && !types.HasFlag(CardType.Token);
```

`CardType.Land` is not named, and a land is not an instant or a sorcery, so 759 fully read cards
had never been on a battlefield in any test in the repository.

### Selecting is not resolving

> **Re-measured, and the numbers below are superseded.** Kept because what changed is not just
> the count. At the current tip the spell soak selects **3,842** complete spells and reports
> **`cast 1691 spells of 3842`**, so **2,151** fully read instants and sorceries are still never
> resolved. The two causes named below as unfixable by any board were both addressed afterwards -
> a decoy spell is now put up for counterspells, and combat is staged for the ones that need an
> attacker - and the second of those **did not work at all until it was measured**.
>
> The combat retry selected its attacker on `HasSummoningSickness == false`. The board carries a
> creature given haste specifically so combat could be staged, but CR 302.6 makes haste *ignore*
> summoning sickness rather than clear it, so the flag stays set and the predicate matched
> nothing: **321 calls, 0 arrivals at the retry loop**, for the whole life of the mechanism. Every
> assertion in the soak passed throughout, because nothing in it distinguished "these spells could
> not be cast" from "the code meant to cast them never ran". Fixing the predicate takes the retry
> to 299 arrivals of 321 and rescues **13 cards** - not the ~300 the refusal tally implied, which
> is worth stating plainly: a refusal count is attempts, not cards, and reading it as a ceiling
> overcounts the same way excision does.
>
> `_combatRetriesReached` is now asserted greater than zero, so a mechanism that reaches nothing
> fails instead of passing quietly. The same mistake was in the engine proper - enlist eligibility
> read the same flag, making every hasty creature ineligible against CR 702.154a - and that is
> fixed with its own test.
## Where a complete card throws: nowhere found

Two new passes, both in `CardPlayabilityTests`. Both are negative results, and both are worth
recording because of what they ran, not what they found.

### The 759 lands, created and then played from hand — clean

759 of 759 played, in 63 tables created directly onto a battlefield and 189 tables where each land
was **played from hand** with `Game.PlayLand`. The same three assertions the soak makes: nothing
threw, `Characteristics.Of` still answers for everything standing, and `Replay(log)` equals the
state. **0 faults, 0 refusals, 759 of 759 land drops taken.**

The second pass exists because conjuring is not playing. The soak calls
`Game.Create(..., Zone.Battlefield)`; a land in a game is *played* (CR 305.1), a special action
that does not use the stack. This engine has already had one whole-class bug in exactly that seam —
every "enters tapped" land arrived untapped, because the replacement was pinned to the zone a spell
is in — so the path is worth running rather than assuming.

### 11,278 permanents cast rather than conjured — clean

**None of the 11,334 fully read permanents had ever been cast.** The permanent soak conjures them
onto the battlefield, which skips the whole casting path: paying the cost (CR 601.2f–h), sitting on
the stack where "whenever you cast" can see it, and entering the battlefield *from the stack*.

`Complete_permanents_survive_being_cast_rather_than_conjured` puts each in a hand, funds it with
twenty of every colour, offers the plausible target shapes, and lets it resolve. **11,278 of 11,334
were cast. 0 faults.** The 56 that were not are refused for reasons that are the rules: 369
refusals of `A land is played, not cast (CR 305.1)` — artifact lands and creature lands, which are
permanents by the soak's predicate but are not cast — and a tail of `Illegal target` for shapes a
four-permanent board cannot supply.

### The harness lied to itself once, and it is recorded here

The from-hand pass first reported **64 of 759 land drops** and would have passed a weaker
assertion. The cause was the harness, not the engine: an opening hand of seven filler cards plus
the lands is over the maximum hand size, the cleanup step asks for a discard (CR 514.1), and the
harness answered by discarding the very cards it came to play — reporting `No object … in the game`
695 times without noticing it was measuring its own discard step.

The fix is `openingHandSize: 0` and short games, and the reason it was caught is that the reach is
asserted (`drops > 600`) rather than printed. A harness that stops reaching things passes exactly
like one that reaches everything; this file follows the soak's discipline on that and says so in
the code.

## What to do, in the order the measurements suggest

1. **Wire the compiler into the game.** Register `CompiledPool` (or a source that consults it)
   as the `IAbilitySource` in `Program.cs`. Until then the playable pool is 917 cards and the
   compiler's 15,036 is a number about a tool, not about the app.
2. **Call `CompiledPool.Refuses` from the gate.** Without it, wiring the compiler in would admit
   6,815 half-read cards into real games — strictly worse than the status quo, because a refused
   deck is honest and a quietly wrong game is not.
3. **Widen `IsPlayable` past the five collections**, or invert it to ask the compiler "did you read
   this card" rather than "did you produce one of these five things". 396 fully read cards are
   refused today for having their behaviour in `CostReducers`, granted keywords, or a combat
   restriction.
4. **Add `CardType.Land` to the soak's `IsPermanent`**, or add a land arm. 759 cards were outside
   every soak for want of one flag. `CardPlayabilityTests` covers them now, but it covers them from
   the outside — the soak is where they belong.
5. **The 1,670 unresolved spells are the largest remaining unverified set.** They are refused for
   want of a situation rather than a board, which is the same wall the spell soak already hit; the
   honest next step is to say so in its output rather than to keep tuning arrangements.

## Cost

`CardPlayabilityTests` is about **3m 45s** in total, and essentially all of it is one test:
`Complete_permanents_survive_being_cast_rather_than_conjured`, ~3 minutes, marked
`[Trait("Category", "Slow")]`. Everything else in the file is a few seconds once the corpus is
loaded (the load itself is ~20s and is shared across the file).

If that becomes a burden it is the thing to trim — it casts every fully read permanent, and a
deterministic slice of them would keep most of the value. What should not be trimmed is the
calibration test, which is what makes any of these numbers worth reading.
