# Test suites

Run `dotnet test MtgEngine.sln` for all remaining suites.

- `MtgEngine.Cards.Tests`: printed catalog/face mapping.
- `MtgEngine.Rules.Tests`: model contracts, state/zone invariants, movement, viewer disclosure and table layout; no gameplay executor.
- `MtgEngine.Api.Tests`: collection, deck, account, forum, rules reference and other API behavior.

`tools/verify-test-scope.ps1 -Scope Foundations` runs both model suites.
Use `-Scope Api` or `-Scope All` for broader checks.

The old engine and pipeline suites were removed with their implementations.
Historical test counts in research evidence are not current coverage claims.
