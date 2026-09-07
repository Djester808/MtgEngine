# P2b — seam requests

## 1. `tools/gate.sh` needs a slow lane for the corpus measurement (coordinator's file)

`TemplateStageTestsCorpus.The_stage_accepts_at_least_the_measured_free_slice` is the only
test in the pipeline project that reads the 200 MB Scryfall bulk file. It now carries
`[Trait("Category", "Slow")]` and returns early — printing that it measured nothing —
unless `RUN_CORPUS` is exactly `"1"`. So `tools/gate.sh` as written stays fast and green
with no change: the ordinary `dotnet test MtgEngine.sln` run skips the measurement.

What is missing is the lane that actually measures. It is a step the coordinator runs, not
a builder, and it must be its own command so a stall is attributable (the reason gate.sh's
steps are separate in the first place):

```sh
RUN_CORPUS=1 timeout 540 dotnet test tests/MtgEngine.Pipeline.Tests/MtgEngine.Pipeline.Tests.csproj \
  --no-build --filter "Category=Slow" -v n
```

`-v n` matters: the assertion is a floor, but the number and the refusal breakdown the test
prints are the actual output, and `-v q` swallows them.

Two things that lane has to know:

- **The corpus is not in git.** `Repo.CorpusPath()` looks under
  `MtgEngine.Api/bin/Debug/net10.0/bulk-data/oracle_cards.json`; the API downloads it on
  first run. Without it the test prints "skipped: no oracle_cards.json …" and passes. A
  lane that does not check for the file measures nothing and reports green — which is the
  failure mode the printed note exists to make visible, so read the note, do not trust the
  exit code alone.
- **It is minutes, not seconds.** Whole-corpus, single pass over every line of the bulk
  file. It does not belong in the merge queue's per-merge gate; once per round is enough.

## 2. The floor is P2's measurement, not a target

`FreeSliceFloor` is 2,400 faces against a measured 2,462, where the plan's table says
2,990. The gap is explained in `seam-requests/P2.md` (ability-word cards that Scryfall's
`keywords` array admits and this stage rightly refuses to the translator). If the
coordinator wants the plan's 2,990 back, that is a decision about ability words belonging
to the template stage, not a bug in it — and it changes what P1 is given, so it is a
coordinator call rather than something P2b should have quietly moved.
