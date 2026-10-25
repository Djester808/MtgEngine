> Historical evidence from the pre-reset implementation. See [the current models-only checkpoint](FOUNDATION_INTEGRATION.md); execution and replay claims below no longer describe the active code.

# Basic stack contract and evidence

Inspected 2026-10-25 using the reverse-engineer-anything workflow. This pass
reuses retained REA evidence and reads reconstructed artifacts with ordinary
source tools; it does not claim new REA output or observed gameplay. No client
or foreign server was executed. Reconstructed proprietary files remain ignored.

## Contract and implementation

The stack is one shared ordered zone containing existing game objects: spells,
abilities and copies, including objects with no physical deck card. Its sole
storage remains `GameState.Objects` and `GameState.In(StackZone.Key)`.
`StackZone.Capture(state)` returns a `StackZoneSnapshot` with `Key`, `Order`,
`Count`, `Top`, `Contains`, `Lookup` and predicate `Find`. Order is bottom-first;
the last entry is top. Lookup excludes objects in other zones. Inspection never
mutates, resolves or reorders anything. Capturing a later state does not alter an
earlier snapshot. These are authoritative server reads; transport must use
`ViewProjector`, which now uses the snapshot order for `GameView.Stack`.

Existing static `StackZone.Top/IsEmpty`, spell movement and ability event
builders retain their signatures. No public command, push/pop/reorder permission,
mutable collection, catalog, identity allocator or replay format was added.
This is a repository design choice, not an extracted foreign class signature.

## Static evidence and method mapping

| Responsibility | Concrete artifact observation | Local mapping / limits |
| --- | --- | --- |
| Shared zone | MTGO `GameCard.GetSharedCardZone` maps `CARDZONE_STACK` to zone 8 (`mtgo/decompiled/GameCard.cs:3173-3189`); `GameCardZone.IsSharedZone` includes 8 in its shared cases (`GameCardZone.cs:55-95`). | Existing `StackZone.Key = ZoneKey.Shared(Zone.Stack)`. No player-owned stack partition. |
| Membership and count | MTGO `GameCardZone` implements collection/list interfaces; `GameCard.Zone` removes old membership and adds new membership (`GameCard.cs:671-700`). Current Arena `MtgZone` stores `CardIds`, `VisibleCards`, type/owner/visibility and derives `TotalCardCount` from IDs (`arena/decompiled/MtgZone.cs:7-25`). | Snapshot count/membership derive from the existing immutable list. These client collections do not establish authoritative mutation permission. |
| Ordered state | Retained Arena `GreInterface.ParseFullGameStateMessage` copies `ZoneInfo.ObjectInstanceIds` in order (`GreInterface.cs:318-326`). MTGO exposes `OrderedZoneIndex` (`GameCard.cs:393-403`). | Preserve local bottom-first order; foreign list/index evidence does not prove which end either foreign server considers top. Local event position -1 appends; existing casting reads the last entry. |
| Abilities, copies, sources | Arena assigns `InstanceId`, `ObjectType`, `ObjectSourceGrpId`, `ParentId` and `IsCopy` separately (`GreInterface.cs:331-361`); ability display may obtain a title through its parent (`444-451`). MTGO separately exposes activated/triggered ability flags (`GameCard.cs:558-584`). | `GameObject.Stack` / `StackEntry.Source`, `AbilityIndex`, `Triggering`, `IsCopy` remain distinct from `CardInstanceId` and `DeckSelection`. No requirement that every member is a physical card. Arena's source printing ID is not assumed equivalent to our source ObjectId. |
| Ownership versus control | Arena assigns owner and controller from separate seat fields (`GreInterface.cs:337-338`). MTGO populates separate owner/controller properties, with fallbacks (`GameCard.cs:2038-2052`). | Snapshot preserves existing `Owner`, `InitialController`, source references; views obtain current controller through characteristics. Ability construction retains source-owner bookkeeping and explicit activating controller. No broader ability-characteristic implementation is claimed. |
| Disclosure | Arena separately copies zone and object visibility (`GreInterface.cs:325,343`), rather than inferring all display data from membership. | Existing `ViewProjector.View/FaceDown` retain public spells and hide face-down identity, printing and finish. Snapshot access is not disclosure authorization. Existing controller inspection exceptions are not expanded. |
| Replay / synchronization | Retained Arena merges objects and zones by IDs (`GreInterface.cs:197-201`) and compares zone membership in `GetZoneChanges` (`1823-1940`). | Reuse `ObjectMoved`, `ObjectCreated`, `ObjectCeasedToExist`, `EventLogSerializer` and `GameReducer`. Foreign wire state is not evidence of a foreign event-log algorithm. |

## Existing call paths preserved

- `CastingSystem.Instructed` calls `StackZone.MoveSpellToStack`; the builder
  allocates through state counters, preserves physical identity/selection and
  emits `ObjectMoved` at position -1. This is an internal event builder, not a
  newly authorized external action.
- `CastingSystem.Activate` uses `CreateAbilityOnStack` with a source reference
  and explicit controller. It creates a stack-only object without selection or
  physical identity. The source may leave while that stack object remains.
- `EffectInterpreter.CopyOnStack` uses existing copy construction, then emits
  `ObjectCreated` with `StackEntry.IsCopy`; it does not inherit deck selection.
  The focused replay test exercises this representation, not copy-rule execution.
- `CastingSystem.Resolve` reads `StackZone.Top`; existing resolution/counter
  callers remove abilities through `ObjectCeasedToExist` and move spells through
  `IZoneSystem.Move/ResolveOntoBattlefield`. Event reducers preserve survivor order.
- `Subsystems.Wave2`, priority, payment, targets, triggers and replacement paths
  remain intact. No new named card definitions or executable rules were added.

## Published semantics and remaining limits

The official [rules page](https://magic.wizards.com/en/rules) links the
[Comprehensive Rules effective September 25, 2026](https://media.wizards.com/2026/downloads/MagicCompRules%2020260925.txt), checked on 2026-10-25.
CR 400.1–400.2 establish the shared public zone and face-down exceptions;
405.1–405.2 establish spells/abilities and arrival on top; 405.5 identifies the
last-added object as next to resolve. CR 112.2 and 113.8 distinguish ownership,
spell control and ability control; source identity is separately retained.
CR 405.3 simultaneous ordering remains a caller responsibility. CR 405.6
exceptions, priority/pass handling, targeting, casting costs, countering and
resolution execution are outside this abstraction. No full-rules claim follows.

No observed basic insertion/removal regression required a change to existing
event builders. Exact foreign server algorithms, server validation, UI ordering
and all hidden-information exceptions remain unknown. Static client code only
supports the explicit observations above. No UI behavior was changed or verified.

## Artifact identity

`stack-contract-reconstruction-hashes.json` records this pass's exact file hashes
and lengths. The reconstructed GameCard, GameCardZone, GreInterface and MtgZone
hashes match the retained card/graveyard manifests.

MTGO gameplay assembly SHA-256:
`7b16823fbaa9a965de3b9ee69ba727992a9b7b50d1187defadd9ac5a6c688081`
(build 3.4.159.4708; retained REA member Evidence
`ev_5c508a6e3992121057eb96955fb197522fc3ed304c4de84eacaa75354286b265`).
The retained Arena GreInterface is attributed to earlier SharedClientCore
`42f2fb5f062ccc6098182a902d411746070759a404e875b6e0aac44d3b7ccc6e`,
Evidence `ev_99270613b579e92d6cc431fa8d639036b41fbf27d7ab6d4000c4ef2ea6853b54`.
Current installed SharedClientCore rehashed as
`aceb0cd5004b322ceb59b28b0ad44ec799b3d47a46666b17a50150da5449e392`;
the retained current `MtgZone` reconstruction belongs to this newer artifact.
These builds are not conflated. Decompiled unresolved-type comments limit
fidelity; conclusions use explicit field assignments and control flow.

## Verification

Focused command: `dotnet test tests/MtgEngine.Rules.Tests --filter "Scope=Stack"`.
Nine cases pass in `stack-focused.log`: empty/null boundaries, immutable snapshots,
physical identity, ability source lifetime, mixed spell/ability/copy ordering,
serialized replay, LIFO removal, non-top departure and four-seat privacy.
Existing acceptance assertions were not modified. Unchanged Deck acceptance
passes separately: 66 rules (`stack-deck-rules.log`) and 80 API
(`stack-deck-api.log`). The full backend run (`stack-broad.log`) passes 1,718
rules, 835 API, 326 Cards and 322 pipeline tests. These totals include other
chats' current work and are not counts of stack tests or proof of full Magic
rules. One transient test-DLL lock warning cleared on the build's retry.
The sandboxed initial dotnet invocation stalled without output; the completed
runs used approved escalation. Stack-owned source whitespace verification passes
after normalizing new files to the repository's CRLF convention. No client
source changed. Changes remain local, uncommitted and unstaged.
