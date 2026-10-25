Run from the MtgEngine repository root with API https://localhost:7001 and the
sibling client development server http://localhost:4200 running. Requires the
sibling mtg-client/e2e Selenium dependencies and Chrome.

node tools/ApiAudit/verify-account.cjs
  Creates a disposable account/deck against the real API, verifies browser login,
  profile, forum and directory, captures desktop/mobile screenshots, and deletes
  its test account in finally. Uses only generated in-memory test credentials.

node tools/ApiAudit/verify-ai.cjs
  Exercises real development components/buttons with deterministic mocked API
  responses and browser-local authentication. Verifies budget serialization on
  build/refine apply at desktop/mobile widths; does not call a model or write DB.

Generated captures and results go to audit-live-account/ and audit-live-ai/ in
the repository root. Those local evidence directories are ignored by Git.

Game browser verification uses tools/TableQa; see its README.md. Collection
browser verification lives in the sibling e2e/tests/collection-*.test.js suites.
