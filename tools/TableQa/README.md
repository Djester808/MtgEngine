# Local board and library verification

This loopback-only host uses the production engine, session, and hub with isolated
in-memory fixtures. It never uses the application database. Separate ports isolate
the browser accounts qa0 through qa3; any nonempty password signs them in.

Build the sibling client first. From the engine root, start a fresh host per scenario:

```powershell
dotnet run --project tools/TableQa --no-restore -- --libraryDraw=true --librarySeats=4 --port=4340
```

Then run one supported manual-library scenario:

```powershell
node tools/TableQa/verify-library.js --search --four
node tools/TableQa/verify-library.js --look --four
node tools/TableQa/verify-library.js --arrange --four
node tools/TableQa/verify-library.js --draw --four
```

Use a fresh `--libraryShuffle=true` host for `--shuffle --four`. Omit `--four`
and use `--librarySeats=2` for two players. `--library=true` prepares a basic
library table without replacement fixtures. The draw/shuffle fixtures retain
competing replacements to exercise pause, answer, and reload behavior.

The verifier checks private views, commands, all-seat broadcasts, reload, and
captures under this directory. It requires an explicit action; there is no
default payment scenario. Payment/recovery, mill-observer, and mill-cost fixtures
and their branches have been removed. The old verify-library-payment.js filename
is no longer supported.

Additional retained probes:

- Search: `--search-battlefield`, `--partial-search`, `--zero-match`,
  `--zero-pick`, `--choice-busy`, `--choice-skip`, `--withdrawal-probe`,
  `--destination-controls`, `--search-panel-probe`, `--busy-request`, `--reject-busy`.
- Inspection: `--reposition`, `--arrange`, `--touch`, `--revoke-inspection`,
  `--panel-probe`, `--opponent-library`, `--detail-focus`.
- Delivery: `--lost-response`, `--reload-response`, `--drop-stop`,
  `--intervening-arrange`, `--intervening-reposition`.

Each probe's argument guards state the required base scenario. Use a fresh host
after each mutating run. Captures are observations, not automatically updated
expected baselines. Keep acceptance expectations independent of implementation.

## Opening hands

```powershell
dotnet run --project tools/TableQa --no-restore -- --libraryOpening=true --librarySeats=4 --port=4340
node tools/TableQa/verify-library-opening.js --four
```

This retains the production opening procedure with 99 Forests plus one commander.
It checks initial counts, free/paid mulligans, private bottoming, pending reload,
kept hands, and first draw. It bypasses collection-backed deck admission, so it
does not replace Commander deck-construction tests.

## Other retained fixtures

Default host: `dotnet run --project tools/TableQa` serves four sample board seats
on ports 4320–4323. `--dense=true` prepares a crowded board; `--cleanup=true`
and `--returns=true` exercise cleanup and commander return positions.

Top access: start with `--libraryTopPlay=true` or `--libraryTopLand=true`,
then run `verify-library.js --top-play --four` or `--top-land --four`.
`--unavailableArtwork=true` pairs with `--broken-artwork --four`.

Disclosure: start with `--libraryDisclosureShuffle=true --librarySeats=4 --port=4340`
and run `verify-library-disclosure-shuffle.js`. `--libraryPrivateShuffle=true`
pairs with `--private`; `--libraryAllDisclosure=true` pairs with `--all-libraries`.
The phasing variants use `--libraryPhasing=true` and verifier `--phasing`
or `--phasing-detail`.
