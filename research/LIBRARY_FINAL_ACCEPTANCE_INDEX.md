# Library final acceptance index

Scope: the library-only correction governs the broader goal wording. Preserve
Commander admission/opening boundaries already named by the plan; do not expand
into card catalogs, other zones or general casting. No commit is authorized.

This index reconciles the current evidence. It is not a declaration that every
MTGO interaction is equivalent. Historical ledgers retain their original totals.

## Verified checkpoints

| Gate | Current evidence | What it proves |
| --- | --- | --- |
| Rules | library-final-integrated-rules.log: 2,103 passed | Current engine regression suite, including complete scry partition validation and multiplayer replay. |
| API | library-lobby-revalidation-api.log: 846 passed | Current API regression suite; inspection of GameHubTests.LibraryChoicePrivacy confirms assertions on both direct choice delivery and restored view redaction. |
| Client | ../mtg-client/library-surveil-completion-verify.log: 1,919 passed, coverage gate and production build | Latest production client, including scry labels, library layout controls and independent own/opponent geometry and retained stacking. Existing card-modal style warning remains. |
| Four-player opening | library-latest-opening-four.log | Latest client: Commander counts, paid bottoming, private options, all-seat reload, preserved kept hands and multiplayer first draw. |
| Two-player opening | library-latest-opening-two.log | Latest client: separately verifies the two-player first-draw variant, private choices, paid bottoming after reload and kept-hand preservation. |
| Complete scry | library-scry-draft-presentation-live.log and library-scry-draft-bottom-live.log | Four live players, visible first decisions, exact top/bottom ordering, deferred movement and reload. |
| Layout import through actual game panel | library-layout-panel-final-live.log | Fixed file payload, malformed-input preservation, responsive controls and exact imported inspection rectangle. Visible capture inspected in tools/TableQa/layout-acceptance-isNEk9. |

## Requirement reconciliation

The original `Requirements and evidence` table in LIBRARY_IMPLEMENTATION.md
contains historical pending statements. Its full scope remains required; the
following evidence owners prevent treating those statements as fresh gaps.

| Original requirement | Evidence owner / remaining determination |
| --- | --- |
| Deck validation and commander separation | CommanderDeckValidator and CommanderPairPolicy tests; opening runs above. Current source review confirms one admission-to-validator call. The new two/four-seat GameLobbyDecksTests exercise saved-deck revalidation through real startup, refusal without a game, correction/retry and persisted reload (library-lobby-revalidation-tests.log). Metadata/pool remain test doubles; TableQa itself still bypasses admission. |
| Ordered library, shuffle, draw and movement | LIBRARY_ACCEPTANCE_AUDIT.md base-operation reconciliation and full rules suite. Inspected LibraryOrderInvariantTests preserves exact membership despite duplicate/stale references; LibraryShuffleReplacementTests verifies replacement recursion, RNG preservation and continuation. |
| Private projection and public revealed top | LIBRARY_UI_ACCEPTANCE_MATRIX.md plus HiddenInformation/LibraryStaticPermission tests and API private-context tests. Historical disclosure never grants current inspection. |
| Opening hands | Separate two- and four-player evidence above; OpeningHandCoordinator is the shared owner. |
| Search | Current method register records single, unrestricted and capacity-search observations; local partial/zero selections, privacy and continuation evidence remains in the acceptance audit. Unobserved MTGO action descriptors are still unknown. |
| Look/reveal/order/scry/surveil/mill | Acceptance audit, hidden-mill decision document, disclosure lifetime tests and latest scry runs. Scry labels retain correction access, an explicit presentation difference from the recorded MTGO removal behavior. |
| Commander replacement | CommanderReplacement/CompetingReplacement tests and the corresponding audit entries; no new card implementation required. |
| Duplicate/stale actions and reconnect | API answer validation, request-journal live delivery-loss evidence and all-seat reload assertions. These establish local guarantees, not MTGO private server behavior. |
| UI | Finite inventory in LIBRARY_UI_ACCEPTANCE_MATRIX.md, supplemented by scry labels and layout file controls. Portrait board rotation and physical-device limitations remain explicit. |
| Manual play | ManualLibraryCoordinator authorization and existing manual-library API/live evidence. It is intentionally an honour-system feature. |
| SOLID and removal of duplicates | LIBRARY_OWNERSHIP_AUDIT.md identifies authoritative owners and bounded compatibility behavior. New layout methods reuse UiPreferencesService.panel; scry presentation projects ChoiceSelectionService without a second destination registry. |
| Verification and show before commit | Current checkpoints above. Consolidated review is in LIBRARY_REVIEW.md. No commit. |

## Still not established

Additional bounded evidence: the Muddstah recording od-JTIVaPm8 was inspected
at 17:25–17:27, 19:01 and 20:10–20:22. It supports later single-card bottom
placement below an earlier one, with narration identifying Bog before Razerunners
and the latter visibly arriving. These are separate scry-one decisions, not proof
of hidden relative order within one multi-card scry. See the current method register.

- Click-to-front, keyboard activation and retained stacking are verified in library-panel-stack-live.log. The retained library template has no collapse control; local collapse is an intentional convenience. All-bottom menu completion is observed, but hidden final relative order is not.
- Which additional generic MTGO action descriptors have real library producers;
  do not manufacture requirements from flags used by unrelated casting paths.
- Full one-for-one-or-demonstrably-better verdict across the above comparisons.
  Private server algorithms remain explicitly unknown as allowed by the plan.

Tests are fixed acceptance contracts. Extend coverage without weakening existing
expectations. An incorrect test requires independent evidence and an explicit
explanation; fresh actual screenshots are observations, not expected baselines.
