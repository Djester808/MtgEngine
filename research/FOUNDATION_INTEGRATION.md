# Foundation integration checkpoint

Completed 2026-10-25. The existing card, library, graveyard, hand, exile and
player contracts now compose with the remaining basic game abstractions.
Existing authoritative state, movement, characteristics, replay and projection
services remain the implementation boundaries.

| Contract | Implementation boundary | Evidence |
| --- | --- | --- |
| Command zone | Shared snapshot; membership does not imply commander status | [Command zone](COMMAND_ZONE_CONTRACT_EVIDENCE.md) |
| Reveal area | Viewer-specific disclosure history and validated live references; not a rules zone | [Reveal](REVEAL_CONTRACT_EVIDENCE.md) |
| Stack | Shared ordered snapshot, top last; cards and stack-only objects | [Stack](STACK_CONTRACT_EVIDENCE.md) |
| Battlefield | Shared membership, phased-out versus active members, evaluated controller | [Battlefield](BATTLEFIELD_CONTRACT_EVIDENCE.md) |
| Permanent | Validated battlefield access and existing evaluated characteristics | [Permanent](PERMANENT_CONTRACT_EVIDENCE.md) |
| Tokens and copies | Existing identity/lifecycle paths; preserve copy-of-card marker across movement | [Tokens and copies](TOKEN_COPY_CONTRACT_EVIDENCE.md) |
| Game/table | Validate initialization and replay start boundaries | [Game/table](GAME_TABLE_CONTRACT_EVIDENCE.md) |
| Turn and priority | Validated snapshots and pass refusal during unavailable priority | [Turn/priority](TURN_PRIORITY_CONTRACT_EVIDENCE.md) |

`FoundationIntegrationTests` exercises two-, three- and four-seat games through
inspection, private look, hand, battlefield, graveyard, stack, exile and command.
It checks owner versus controller, persistent physical/printing identity, zone
membership, face-down disclosure, noncommander command membership, per-seat
projection and serialized replay. These use basic movement, not a new casting
or resolution algorithm.

## Verification

- Focused Foundations: 197 Rules and 80 API tests passed.
- Full Release solution: 1,776 Rules, 835 API, 326 Cards and 322 Pipeline passed.
- Release build: zero warnings and errors.
- `dotnet format MtgEngine.sln --no-restore --verify-no-changes`: passed.
- Client `npm run verify`: 1,896 tests and production build passed; existing
  card-modal stylesheet budget warning remains.

Local logs: `foundations-acceptance.log`, `foundations-release-build.log`,
`foundations-release-tests.log`, `foundations-final-format.log`, and the sibling
client's `foundations-merge-client-verify.log`. Logs and raw reconstruction
artifacts remain local; evidence reports and hash manifests are versioned.

## Limits

These definitions do not establish complete rules coverage or fresh live UI
verification. No new keyword, casting/payment, combat, trigger or replacement
system was added. Individual reports distinguish observed client representation
from architectural inference and unknown server behavior. In particular, full
copy-effect semantics, persistent face-down exile access permissions and all
zone-specific ordering interactions remain outside this checkpoint.
