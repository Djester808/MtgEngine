# E5i — wave 24: the six readers the query layer owed

Everything below landed inside `MtgEngine.Rules/Query/**` and `tests/MtgEngine.Rules.Tests/Query*.cs`,
so nothing here blocks the merge. No seam changed: `IQuery`, `ICharacteristics`, `EffectContext`,
`GameObject`, `StackEntry`, the events and the vocabulary are untouched.

## What was built

| Item | Where | How it answers |
|---|---|---|
| `TriggeringDamage` (CR 603.10, E6g ask 2) | `Query.Amounts.cs` | Parses `context.Chosen[TriggerSystem.TriggeringDamageAmount]` culture-invariantly; 0 when the key is absent or is not a number. |
| `AttackersAgainst(Who, IncludingPermanentsTheyControl)` (CR 508.1, E6g ask 3) | `Query.Amounts.cs` | Counts `state.Combat.Attackers` whose `CombatTarget.Player` is a player `Who` admits, plus — while the flag stands — those aimed at a planeswalker or battle such a player controls. No combat is 0. |
| `Targets(ObjectFilter)` predicate (CR 115.1, V13 row 5) | `Query.Predicates.cs` | The candidate's stack entry's announced `Targets` admit the filter. A player target admits no `ObjectFilter`; a candidate with no stack entry, or none announced, is admitted by nothing. |
| `ObjectFilter.CastFrom` / `NotCastFrom` (CR 601.3, V13 row 6) | `Query.cs` (`CastFromMatches`) | Read off the candidate's stack entry's `CastFrom`. A candidate that is not a spell — and a spell that was never cast, such as a copy put onto the stack (CR 707.10) — matches **neither** field. |
| `PaidAlternativeCost(null)` by name (CR 118.9, E3n § 1) | `Query.Conditions.cs` (`IsAlternativeCostMarker`) | The alternative markers by name — `"alternative"`, `"life"`, `"free"`, and any keyword whose `KeywordSemantics.Of(kind).Cost` is `CostShape.Alternative` — instead of `OptionalCostsPaid.Count > 0`. |
| `ObjectFilter.ManaCostIs` and `NotInCommanderIdentityColors` (CR 202.1, 903.4, E0i § 2) | `Query.cs`, `ManaCosts.Colors` | `ManaCostIs` compares the cost symbol by symbol, so `{0}` is not a land's absent cost and `{1}` is not `{W}`; an empty list matches nothing. The identity is the printed colours, the mana cost's symbols and the symbols the card's ability **costs** print, unioned over every commander that player owns. |

Both `ObjectFilter` fields were already routed to the query by `Layers.Matcher.NeedsQuery`, so a
static ability's filter carrying one is answered by this work with no change on that side.

## Two behaviour changes a reader should know about

- **A kicked spell now answers `PaidAlternativeCost` with no keyword as false.** That is E3n's ask
  and the rule (CR 601.2b: kicker is paid *in addition to* the mana cost), but it is a change of
  answer, not only of implementation: any card file that leaned on the old loose reading changes
  its behaviour. `"additional"`, the offered-additional-cost marker, is likewise not an alternative.
- **`NotInCommanderIdentityColors` refuses when the asking player has no commander in the game**
  (`RefusedException`, CR 903.4), where before the field was silently ignored and admitted every
  object. A filter carrying it in a non-commander game is now a refusal rather than a wrong answer;
  the pilot's games are commander games, so nothing in the corpus should reach it.

## Asks

1. **Casting — a constant for the waived-mana-cost marker.** `CastPlan.cs` writes `paid.Add("free")`
   as a bare literal (line ~228), where its siblings are `CastPlan.PaidPrintedAlternativeCost` and
   `CastPlan.PaidLifeForManaValue`. The query has to name it to tell an alternative cost from an
   additional one, so the string is spelled a second time in `Query.Conditions.cs` as
   `WaivedManaCost`. One `internal const string PaidWaivedManaCost = "free";` on `CastPlan`, and that
   duplicate goes away. Meanwhile the query's test writes the literal `"free"` onto a stack entry, so
   a rename on the casting side that missed this reader would leave that test passing and the game
   answering wrongly — which is the whole reason for the ask.
2. **CR 903.4 — mana symbols inside an ability's effect are not read.** The identity reads the
   printed colours, the mana cost and the mana symbols an ability's *costs* print. A symbol printed
   in what an ability *does* ("add {G}") is a colour of the card's identity under CR 903.4 and is not
   counted, because the vocabulary carries produced mana as a `ManaProduction` record rather than as
   text and `AnyColor`/`LandCouldProduce` name no colour at all. Closing it wants either a colour
   reader over `ManaProduction` (E-series, in the query) or the identity computed once where the card
   is compiled and carried on the object (C-series). No card in the corpus needs it today: a
   commander is a legendary creature, and its cost almost always already names the colour.
3. **The `Commander` selector is still refused.** `Query.Selectors.cs` throws `NoCommander` for
   `Cards.Commander` citing "no state records which object is a player's commander" — but
   `GameObject.IsCommander` has recorded exactly that since wave 9, and this work reads it for
   CR 903.4. The selector is one arm away (`state.Objects.Values.Where(o => o.IsCommander && ...)`),
   and it is left alone here only because it is not on this brief. An E-series wave should close it
   and delete the constant.

## Shapes refused rather than thrown

- CR 903.4 — `NotInCommanderIdentityColors` asked in a game where the asking player has no commander:
  `RefusedException` naming 903.4, with a test.

Everything else in this brief answers rather than refuses, and each "answers nothing" case above (no
combat, no stack entry, no announced target, no printed mana cost, an absent trigger key) is a test.
