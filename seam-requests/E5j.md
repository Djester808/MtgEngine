# E5j — the query reads the secret numbers, the two per-turn counts, and the commander

Owns `MtgEngine.Rules/Query/**` and `tests/MtgEngine.Rules.Tests/Query*.cs`. Wave 25.

## Done here

| Record | Where | Reading |
|---|---|---|
| `HighestChosen(Key)` / `LowestChosen(Key)` (CR 700.2 with 101.4, E9s ask 2) | `Query.Amounts.cs` | Every entry of `EffectContext.Chosen` whose key starts with `EffectInterpreter.SecretNumbersUnder(key)`, parsed culture-invariantly; the greatest or the least. An entry whose value is not a number is passed over. A key nobody answered is **refused** naming 700.2. |
| `PlayerWhoChose(Key, Lowest)` (CR 700.2 with 101.4) | `Query.Selectors.cs` | Every seat whose number is that extreme — a tie names them all — in turn order (CR 101.4: the active player first). Refused the same way when the key holds nothing. |
| `CardsDrawnThisTurn(Who)` / `LandsEnteredThisTurn(Who)` (CR 121.1, E2c ask 4) | `Query.Amounts.cs` | `GameState.CardsDrawnThisTurn` / `GameState.LandsEnteredThisTurn` over the seats the reference admits, folded with **max**. No seat admitted is 0. |
| `Cards.Commander(Whose)` (CR 903.3, E5i ask 3) | `Query.Selectors.cs` | `state.Objects.Values.Where(o => o.IsCommander && owners.Contains(o.Owner))`, ordered by id, in whatever zone the card is. The player is the **owner** (CR 108.3), so an opponent controlling it does not make it theirs. A game that designated no commander selects nothing. The `NoCommander` constant in `Query.Conditions.cs` is deleted. |

Two decisions a reader should know about:

- **The refusal, not zero.** E9s ask 2 wrote "no entry at all is 0"; the wave-25 brief says a key
  nobody answered is refused with the rule. The brief won, and it is the better answer: zero is a
  number a player could have chosen, so a card that damages each player by the highest number
  would deal none and look as though the game had played the card. The refusal reaches a table as a
  `Refused` naming 700.2.
- **Max, not sum, and only for these two amounts.** `LifeOf` and `CardsInHandOf` total their seats;
  these do not. Smuggler's Share's "any opponent drew two or more cards this turn" asks whether
  some *single* opponent reached the number, and a sum answers yes for two opponents who drew one
  card each. With one seat admitted — every "you" reading — the two folds are the same number. The
  divergence is in the doc comment on `Query.PerTurn`.

## Asks

### 1. `State/**` — a token land that stopped existing is lost from `LandsEnteredThisTurn` (CR 111.7 with 121.1)

`GameState.LandsEnteredThisTurn` walks `History.EnteredBattlefield` first and marks each id
`counted` **before** it has found an object for it:

```csharp
foreach (var id in History.EnteredBattlefield)
{
    if (!counted.Add(id))
        continue;
    var entered = Objects.TryGetValue(id, out var live) ? live : History.LeftBattlefield.FirstOrDefault(o => o.Id == id);
    if (entered is not null && EnteredAsLand(entered, who))
        lands++;
}
```

A token land is now in both `EnteredBattlefield` (the coordinator's wave-24 line) and
`TokensCreated`. When it stops existing (CR 111.7, `ObjectCeasedToExist`) it is in `Objects` no
longer and in `LeftBattlefield` never — that event adds to neither — so the first loop counts
nothing **and** consumes the id, and the `TokensCreated` pass that would have counted it is
skipped by `counted.Add` returning false. The land entered the battlefield this turn and the count
says it did not.

The narrow fix is to add to `counted` only where the entry is actually counted, so an entry the
first loop cannot resolve falls through to the token record:

```csharp
foreach (var id in History.EnteredBattlefield)
{
    if (counted.Contains(id))
        continue;
    var entered = Objects.TryGetValue(id, out var live) ? live : History.LeftBattlefield.FirstOrDefault(o => o.Id == id);
    if (entered is null)
        continue;               // nothing in the state says what it was; the token pass may.
    counted.Add(id);
    if (EnteredAsLand(entered, who))
        lands++;
}
```

Filed rather than fixed because `State/**` is not this box's. The query test asserts the case that
does work — a land put into a graveyard still counts, through `LeftBattlefield` — and says nothing
about the token that ceased to exist, so the suite will not go green over the gap.

### 2. Vocabulary / rules — CR 700.2 is the modal rule in the shipped text

`HighestChosen`, `LowestChosen`, `PlayerWhoChose`, `ChooseNumberSecretly` and the interpreter's
`ChoosingSecretly` all cite CR 700.2. In `MtgEngine.Api/Knowledge/comprehensive-rules.txt`, 700.2
is *"A spell or ability is modal if it has two or more options in a bulleted list…"*. The rule that
actually governs a simultaneous secret choice is **CR 101.4** (APNAP), which is why every one of
these records says "700.2 with 101.4". The readers here cite 700.2 to match the vocabulary and the
interpreter rather than diverging from them alone; if a wave moves the citation, it moves in all
five places at once. Recorded, not changed.

### 3. Nothing wants the *total* of the two per-turn counts yet

If a card ever reads "the total number of cards your opponents have drawn this turn", it wants a
record that says so (a `Total` flag on the amount, or a `TotalAmong`-shaped wrapper) rather than a
change of fold here — changing the fold would silently break the "any opponent" reading that is in
the corpus today. Vocabulary's call, not the query's.

## Not done in this box

- **No numeric answer channel and no `Extra` leak fix** — E9s asks 3 and 4 are `Choices/**` (E4h
  this wave). Until the channel lands, an unbounded secret choice is still refused by the
  interpreter, so these readers are exercised only by bounded ones.
- **No commander-identity change.** E5i ask 2 (mana symbols inside an ability's *effect* are not
  read for CR 903.4) is untouched: it needs a colour reader over `ManaProduction`, which is a
  vocabulary-shaped question and not one arm.
- **No zone narrowing on `Cards.Commander`.** "Your commander in the command zone" is a filter's
  job, and the selector carries none; if a card needs one the record wants a `Filter`, which is a
  vocabulary change.
