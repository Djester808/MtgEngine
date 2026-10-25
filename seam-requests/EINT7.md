# EINT7 — the exile link, and a remembered key that outlives one resolution

Filed while fixing the review of the wave-30 tip. Two halves of one card are missing, and neither
can be written in the vocabulary as it stands. Nothing here is urgent enough to refuse a card over:
refusing at the interpreter turns into a `Refused` answer at `Game.Apply`, which throws the whole
command away, so a card that works two thirds of the way would stop working at all.

## The card that needs both

`cards/660f370a-b25b-4ea5-b765-7abf96899c1c.json` — "The Darkness Crystal", status **Verified**:

> Black spells you cast cost {1} less to cast.
> If a nontoken creature an opponent controls would die, instead exile it and you gain 2 life.
> {4}{B}{B}, {T}: Put target creature card exiled with The Darkness Crystal onto the battlefield
> tapped under your control with two additional +1/+1 counters on it.

It is Verified because its one smoke scenario casts the artifact and stops; the replacement never
fires, so nothing here is exercised. The first two lines play correctly. The third can never find
anything.

## 1. `Exile.ExiledWith` is write-only

`Exile(what, until?, faceDown?, rememberAs?, exiledWith?)` has carried `ExiledWith` since wave 0.
`grep -rn ExiledWith MtgEngine.Rules MtgEngine.Cards` finds the record and nothing else: the
interpreter does not record the link, no `ObjectFilter` predicate reads it, and no selector names
it. Two cards in the corpus write it.

**What is owed:** somewhere to put the link when the exile happens (the exiled object is a new
object, CR 400.7, so it has to be stamped as it lands), and a predicate to read it back — the
printed phrase is "exiled with <this permanent>", which is a link to the *source object*, not to a
name. The natural shape is `ObjectFilter.ExiledWith: Selector` beside `Also`.

## 2. A remembered key does not outlive its resolution

The card's own workaround is `rememberAs: "k1"` on the exile and `IsRemembered("k1")` in the third
ability's target filter. That cannot work either. `EffectInterpreter.Recall` writes into
`run.Context.Remembered`, which is the walk's own context; `Query.Predicates` reads
`ctx.Remembered`, which for the activated ability is that ability's context and holds nothing the
replacement put there a turn earlier.

**What is owed:** a decision on whether a key set by one resolution can be read by another
resolution of the same source, and if so where it lives (`GameObject`, keyed by source, is the only
place that survives a zone change and replays from the log). If the answer is no, the translator
prompt has to say so, because the vocabulary currently reads as though it works.

## Until the seam exists

- `Exile.Until` **is refused** by `EffectInterpreter.Actions` (CR 701.13). It has no reader, so an
  "exile until this leaves the battlefield" exiled the card for ever — a card silently destroyed.
  No card in the corpus writes it, so the refusal costs nothing that works.
- `Exile.ExiledWith` **is not refused**. The exile itself is right; only the link is missing.
  `tests/MtgEngine.Rules.Tests/InterpreterExileTests.cs` holds both decisions.
