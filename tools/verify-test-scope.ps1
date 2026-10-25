param(
    [ValidateSet('Foundations', 'Api', 'All')]
    [string] $Scope = 'Foundations'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projects = switch ($Scope) {
    'Foundations' { @('MtgEngine.Cards.Tests', 'MtgEngine.Rules.Tests') }
    'Api' { @('MtgEngine.Api.Tests') }
    'All' { @('MtgEngine.Cards.Tests', 'MtgEngine.Rules.Tests', 'MtgEngine.Api.Tests') }
}
foreach ($project in $projects) {
    & dotnet test (Join-Path $repoRoot "tests/$project") --no-restore --verbosity minimal
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
