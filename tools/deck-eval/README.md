# deck-eval

Measures what the AI build actually produces, so a change to a prompt, a model or an
effort setting can be judged on evidence instead of on one build and a feeling about it.

That is not hypothetical. A sentence in the bracket prompt claimed Sol Ring was a Game
Changer — it is not, and our own card data says so — and it survived until someone asked
why the card was missing. A price tier asked the model to respect a ceiling it was never
shown, and 13 of 99 cards came back over it. A tribe hint matched "battle" inside
"battlefield" and handed a Wolf deck a list that was 93% noise. Each was found by hand,
one build at a time.

## Two halves, on purpose

**`run.js` costs money.** Each case is a real build: two Opus calls, roughly two minutes.
It records the plan *and freezes the facts of every card in it* — colour identity, types,
cheapest printing, Game Changer flag, rules text.

**`score.js` is free.** It reads those recordings and never touches the network.

The split is the important part. Prices move, the oracle index gets rebuilt, and a check
that re-read them at scoring time would make two runs differ for reasons unrelated to the
change being measured. Freezing the facts also means **adding a check and re-scoring every
run ever recorded costs nothing**, so the check set can grow without the evidence going
stale.

## Use

```bash
export EVAL_USERNAME=... EVAL_PASSWORD=...        # an account on the running API

node run.js --label=baseline                       # record (slow, costs money)
node score.js --label=baseline                     # score (instant, free)

# then change something, and:
node run.js --label=assessment-low-effort
node compare.js --before=baseline --after=assessment-low-effort
```

`--only=meren,chief` scopes a run to matching case ids. `--force` re-runs cases already
recorded; without it `run.js` resumes, so an interrupted run costs nothing to finish.

`score.js` exits non-zero only on a **hard** failure. `compare.js` exits non-zero if any
check regressed.

## What it checks

**Hard — a failure is a defect.** Legality and completeness: 99 cards with no shortfall
(§1.1), every card inside the commander's colour identity (§1.2), singleton outside basics,
no Game Changer below bracket 4 (§1.4), nothing above the price tier's ceiling. These
should be green on every run.

**Bands — a doctrine range, read as a trend.** Lands (§3.1, adjusted for curve by §2.2 —
a flat 36–38 would mark a correctly-built low-curve deck wrong), ramp (§4), card advantage
(§5), interaction (§6.1), mana sources (§2.1), and tribal density where the case names a
tribe (§7).

Out of band is a signal, not a verdict. §2 says the roles overlap and to count a card in
the role it is actually being played for, which a one-bucket classifier cannot do: a
measured deck showed 17 interaction against a band of 8–12, and 10 of those were the
sacrifice payoffs the deck was built on. The interaction split is printed beside every
case for exactly that reason. Judge these across runs, not on one deck.

One archetype check is hard rather than banded: if a deck is at least 55% creatures, a
symmetrical sweeper is a defect, because §6.4 inverts there — the deck loses more than the
table does.

**Nothing here asks a model whether a deck is good.** That would be circular, expensive,
and unstable between runs. The measurement has to be the one thing that does not move.

## What it found the first time it was used

Two attempts to make the assessment cheaper — it is 46% of a build's wall clock, on a call
that reads a finished list rather than building anything.

| config | assessment call | findings with a fix (3 cases) |
| --- | --- | --- |
| Opus, medium effort | 57.6 s | **12** (4, 3, 5) |
| Opus, low effort | 23.5 s | 7 (2, 2, 3) |
| Sonnet, medium effort | ~36 s | 11 (5, 4, **2**) |

Low effort lost 42% of the actionable findings, in every case. Sonnet held the total but
collapsed on one case, whose findings fell from 10 to 4; a second sample would have settled
whether that was variance, and it was not obtainable. Both were reverted, and the numbers
live in `AiBuildService.AssessModelId` so nobody pays for them twice.

It also found a flaw in itself, which is worth knowing before reading any comparison:

**Deck checks cannot judge a change to the assessment.** The assessment runs after the deck
is built and cannot influence which cards were chosen — yet dropping its effort appeared to
move five deck bands, including one that went out of band. That was the build call's own
variance, a separate model call that was not the thing under test. The assessment metrics
(`assess:findings`, `assess:with-fix`, `assess:citations`) exist because they are the
numbers that actually respond, and `compare.js` prints the warning on every run.

The general rule: attribute a difference only to a change that could plausibly have caused
it, and trust several cases moving the same way over one moving a lot.

## Cases

`cases.json`. Eight builds, each varying an axis the checks read: colour count (1, 2, 4),
tribal versus not, bracket (1, 2, 3, 4) and price tier. Every case carries a `why`.

Keep it short. Every entry is two Opus calls per run, and a matrix nobody can afford to run
is a matrix nobody runs.

## Results

`results/<label>/<case-id>.json`, gitignored — they are large, they contain a snapshot of
the card corpus, and they are cheap to regenerate for anyone who needs them.
